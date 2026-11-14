using System;
using System.Collections.Generic;
using Godot;

using BalticDrive.Core;

namespace BalticDrive.Map;

/// <summary>Growable vertex/index buffers for one surface, filled on a worker thread.</summary>
public sealed class MeshData
{
    public readonly List<Vector3> V = new();
    public readonly List<Vector3> N = new();
    public readonly List<Vector2> UV = new();
    public readonly List<Vector2> UV2 = new();
    public readonly List<Color> C = new();
    public readonly List<int> I = new();

    public bool Empty => I.Count == 0;

    public int Add(Vector3 v, Vector3 n, Vector2 uv, Vector2 uv2, Color c)
    {
        V.Add(v); N.Add(n); UV.Add(uv); UV2.Add(uv2); C.Add(c);
        return V.Count - 1;
    }

    /// <summary>Adds a triangle wound so that its front faces along <paramref name="facing"/> (Godot fronts are clockwise).</summary>
    public void Tri(int a, int b, int c, Vector3 facing, List<Vector3>? collision = null)
    {
        if ((V[b] - V[a]).Cross(V[c] - V[a]).Dot(facing) > 0f) (b, c) = (c, b);
        I.Add(a); I.Add(b); I.Add(c);
        if (collision != null) { collision.Add(V[a]); collision.Add(V[b]); collision.Add(V[c]); }
    }

    public void Quad(int a, int b, int c, int d, Vector3 facing, List<Vector3>? collision = null)
    {
        Tri(a, b, c, facing, collision);
        Tri(a, c, d, facing, collision);
    }

    /// <summary>An axis-aligned box, optionally turned by <paramref name="yaw"/> around its own centre line.</summary>
    public void Box(Vector3 min, Vector3 max, Color c, List<Vector3>? collision = null, float yaw = 0f, Vector2 uv2 = default)
    {
        var centre = (min + max) * 0.5f;
        var half = (max - min) * 0.5f;
        var basis = new Basis(Vector3.Up, yaw);
        Span<Vector3> axes = stackalloc Vector3[] { Vector3.Right, Vector3.Up, Vector3.Back };
        for (int axis = 0; axis < 3; axis++)
        {
            int u = (axis + 1) % 3, v = (axis + 2) % 3;
            for (int sign = -1; sign <= 1; sign += 2)
            {
                var n = basis * (axes[axis] * sign);
                var o = axes[axis] * (half[axis] * sign);
                var du = axes[u] * half[u]; var dv = axes[v] * half[v];
                int i = V.Count;
                Add(centre + basis * (o - du - dv), n, new Vector2(0, 0), uv2, c);
                Add(centre + basis * (o + du - dv), n, new Vector2(1, 0), uv2, c);
                Add(centre + basis * (o + du + dv), n, new Vector2(1, 1), uv2, c);
                Add(centre + basis * (o - du + dv), n, new Vector2(0, 1), uv2, c);
                Quad(i, i + 1, i + 2, i + 3, n, collision);
            }
        }
    }

    public ArrayMesh ToMesh(Material material)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = V.ToArray();
        arrays[(int)Mesh.ArrayType.Normal] = N.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = UV.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV2] = UV2.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = C.ToArray();
        arrays[(int)Mesh.ArrayType.Index] = I.ToArray();
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(0, material);
        return mesh;
    }
}

/// <summary>What the map adds to one terrain chunk: road ribbons and buildings, plus their collision.</summary>
public sealed class MapChunkGeometry
{
    public readonly MeshData Roads = new();
    public readonly MeshData Buildings = new();
    public readonly MeshData Barriers = new();
    public List<Vector3>? PavedFaces, LooseFaces, BuildingFaces;
    /// <summary>MultiMesh instance buffers for street lamps, wooden power poles and pylons.</summary>
    public List<float>? Lamps, Poles, Pylons;
    /// <summary>Thin colliders for the above: xyz local position, w radius.</summary>
    public List<Vector4>? Posts;
}

/// <summary>Worker-side generation of road and building meshes from map data. No scene tree access.</summary>
public static class MapMesher
{
    public const float ShoulderWidth = 1.5f, ShoulderDrop = 0.45f;
    public const float TallBuilding = 42f;   // taller than this is drawn by the skyline layer instead

    // Surface codes in vertex colour R, read by shaders/map_road.gdshader.
    private const float SurfAsphalt = 0f, SurfConcrete = 0.25f, SurfLoose = 0.5f, SurfSlab = 0.89f, SurfRail = 1f;
    public const float Kerb = 0.12f;

    /// <summary>Small height bias per class so overlapping ribbons at junctions never z-fight.</summary>
    /// <summary>COLOR.a for the road shader: lane count plus 9 x lanes in the point direction (0 = even split), over
    /// 255 so it survives 8-bit vertex colours.</summary>
    private static float LaneCode(int lanes, int forward) => (Math.Clamp(lanes, 1, 8) + 9 * Math.Clamp(forward, 0, 8)) / 255f;

    private static float ClassBias(byte cls) => cls switch
    {
        RoadClass.Motorway => 0.10f, RoadClass.Trunk => 0.095f, RoadClass.Primary => 0.09f, RoadClass.Secondary => 0.085f,
        RoadClass.Tertiary => 0.08f, RoadClass.Link => 0.075f, RoadClass.Unclassified => 0.07f, RoadClass.Residential => 0.065f,
        RoadClass.Living => 0.06f, RoadClass.Service => 0.055f, RoadClass.Track => 0.05f, _ => 0.04f,
    };

    /// <summary>
    /// Marking style for the shader: 0 none, 0.33 centre dashes only (minor through roads),
    /// 0.66 two-way with edge lines, 1 one-way carriageway with edge lines.
    /// </summary>
    private static float Marking(MapRoad r)
    {
        if (r.Unpaved || r.IsRail) return 0f;
        bool oneway = r.Oneway;
        switch (r.Cls)
        {
            case RoadClass.Motorway: case RoadClass.Trunk: case RoadClass.Primary: case RoadClass.Secondary:
                return oneway ? 1f : 0.66f;
            case RoadClass.Tertiary:
                return oneway ? (r.Lanes >= 2 ? 1f : 0f) : 0.33f;
            case RoadClass.Link:
                return oneway ? 1f : 0.33f;
            case RoadClass.Unclassified: case RoadClass.Residential:
                // only streets wide enough to really have marked lanes
                return r.Lanes >= 3 ? (oneway ? 1f : 0.66f) : 0f;
            default:
                return 0f;
        }
    }

    public static void Build(MapContext ctx, float size, int lod, MapChunkGeometry geo, bool collide)
    {
        if (collide)
        {
            geo.PavedFaces = new List<Vector3>();
            geo.LooseFaces = new List<Vector3>();
            geo.BuildingFaces = new List<Vector3>();
        }
        var segs = ctx.Segs;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            float mx = (s.Ax + s.Bx) * 0.5f, mz = (s.Az + s.Bz) * 0.5f;
            if (mx < 0 || mx >= size || mz < 0 || mz >= size) continue;
            if (s.Road.IsTram) { if (lod <= 1) AddTramSegment(geo.Roads, ctx, s); }
            else
            {
                AddRoadSegment(geo, s, lod);
                CuttingMesher.AddSegment(ctx, s, lod, geo.Barriers, collide ? geo.BuildingFaces : null);
            }
        }
        foreach (var jr in ctx.Junctions)
        {
            var j = jr.J;
            float cx = jr.OffX + j.Cx, cz = jr.OffZ + j.Cz;
            if (j.Ghost || cx < 0 || cx >= size || cz < 0 || cz >= size) continue;
            AddJunction(geo, jr, lod, ctx);
        }
        AddFurniture(ctx, size, lod, geo, collide);
        BarrierMesher.AddBarriers(ctx, size, lod, geo.Barriers, collide ? geo.BuildingFaces ??= new List<Vector3>() : null);
        var list = ctx.Buildings;
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            var b = r.B;
            if (b.Ghost || b.Height > TallBuilding) continue;
            float cx = r.OffX + b.Cx, cz = r.OffZ + b.Cz;
            if (cx < 0 || cx >= size || cz < 0 || cz >= size) continue;
            float gMin = float.MaxValue, gMax = float.MinValue;
            var rx = b.RX[0]; var rz = b.RZ[0];
            for (int k = 0; k < rx.Length; k++)
            {
                float h = ctx.Height(r.OffX + rx[k], r.OffZ + rz[k], 0f, out _);
                gMin = MathF.Min(gMin, h); gMax = MathF.Max(gMax, h);
                // far terrain shows the natural ground, not the road-shaped one: walls reach down to both
                gMin = MathF.Min(gMin, ctx.Ground(r.OffX + rx[k], r.OffZ + rz[k]));
            }
            AddBuilding(geo.Buildings, geo.BuildingFaces, b, r.OffX, r.OffZ, gMin, gMax,
                Hash.Of((int)Math.Round((ctx.Ox + r.OffX) / MapTile.Size), (int)Math.Round((ctx.Oz + r.OffZ) / MapTile.Size), b.Index, 91));
        }
    }

    // ---- junctions -----------------------------------------------------------------------

    /// <summary>
    /// A junction surface: the triangulated outline at its own heights (no markings), with a shoulder sloping down
    /// and out along the outline like the road ribbons have, so the ground meets it the same way.
    /// </summary>
    private static void AddJunction(MapChunkGeometry geo, in MapJunctionRef jr, int lod, MapContext ctx)
    {
        var j = jr.J;
        var mesh = geo.Roads;
        // paved areas sit under every road that crosses them; junction surfaces over them
        float bias = (j.Paved ? 0.045f : 0.105f) + (lod > 0 ? 0.06f * lod : 0f);
        // The road shader reads UV.x as the offset from the centre line and COLOR.b * 16 as the half width (beyond it:
        // gravel shoulder). A junction is all carriageway: half width 16 m, UV.x kept within ±10 m (wrapping every 40 m).
        var col = new Color(j.Pedestrian ? SurfConcrete : SurfAsphalt, 0f, 1f, LaneCode(2, 0));
        var uv2 = new Vector2(1000f, 0f);             // far from any junction: no stop lines, and marking style 0 anyway
        static float U(float x, float z) => ((x * 0.5f + z * 0.5f) % 20f + 30f) % 20f - 10f;
        int baseV = mesh.V.Count;
        int n = j.X.Length;
        for (int k = 0; k < n; k++)
            mesh.Add(new Vector3(jr.OffX + j.X[k], j.Y[k] + bias, jr.OffZ + j.Z[k]), Vector3.Up, new Vector2(U(j.X[k], j.Z[k]), j.Z[k] - j.X[k]), uv2, col);
        for (int t = 0; t + 2 < j.Tris.Length; t += 3)
            mesh.Tri(baseV + j.Tris[t], baseV + j.Tris[t + 1], baseV + j.Tris[t + 2], Vector3.Up, geo.PavedFaces);
        if (j.Elevated) return;
        // shoulders: each outline edge pushed outwards (away from the surface) and down
        int start = 0;
        foreach (int end in j.RingEnds)
        {
            float area = 0;
            for (int i = start, p = end - 1; i < end; p = i++) area += (j.X[p] * j.Z[i] - j.X[i] * j.Z[p]);
            // ring 0 is the outside: outward is away from its interior; islands the other way round
            float sign = (area > 0 ? 1f : -1f) * (start == 0 ? 1f : -1f);
            for (int i = start, p = end - 1; i < end; p = i++)
            {
                var a = new Vector3(jr.OffX + j.X[p], j.Y[p] + bias, jr.OffZ + j.Z[p]);
                var b = new Vector3(jr.OffX + j.X[i], j.Y[i] + bias, jr.OffZ + j.Z[i]);
                var e = new Vector2(b.X - a.X, b.Z - a.Z);
                float len = e.Length();
                if (len < 0.05f) continue;
                var outv = new Vector2(e.Y, -e.X) / len * sign;
                if (lod <= 1 && !j.Paved && CornerPavement(ctx, a, b, outv, out float cw))
                {
                    // a street corner between two pavements (D27): kerb up, slabs round the corner, slope to the ground
                    AddCornerPavement(mesh, geo.PavedFaces, a, b, outv, cw);
                    continue;
                }
                var drop = new Vector3(outv.X * ShoulderWidth, -ShoulderDrop, outv.Y * ShoulderWidth);
                var nrm = new Vector3(outv.X * 0.28f, 1f, outv.Y * 0.28f).Normalized();
                // shoulder: UV.x from the carriageway edge (16 m) outwards, as on the road ribbons
                int v0 = mesh.Add(a, Vector3.Up, new Vector2(16f, 0), uv2, col);
                int v1 = mesh.Add(b, Vector3.Up, new Vector2(16f, len), uv2, col);
                int v2 = mesh.Add(b + drop, nrm, new Vector2(16f + ShoulderWidth, len), uv2, col);
                int v3 = mesh.Add(a + drop, nrm, new Vector2(16f + ShoulderWidth, 0), uv2, col);
                mesh.Quad(v0, v1, v2, v3, Vector3.Up, geo.PavedFaces);
            }
            start = end;
        }
    }

    /// <summary>
    /// An outline edge of a junction surface that is a street corner, not an arm's mouth, with a pavement beside it
    /// (a road with a pavement on that side reaches within its walk width of the edge's outer side).
    /// </summary>
    private static bool CornerPavement(MapContext ctx, Vector3 a, Vector3 b, Vector2 outv, out float width)
    {
        width = 2f;
        float mx = (a.X + b.X) * .5f, mz = (a.Z + b.Z) * .5f;
        // an arm's mouth: some carriageway (bridges and tram streets included) continues past the edge, or covers where
        // the slabs would go
        foreach (var s in ctx.Segs)
        {
            if (s.Road.IsRail && !s.Road.IsTram) continue;
            // a tram track entering the junction through this edge makes it a mouth too (median gaps); one passing
            // near a corner does not
            if (s.Road.IsTram) { if (s.Distance(mx + outv.X * .5f, mz + outv.Y * .5f, out _) < s.Hw) return false; continue; }
            for (float t = .5f; t <= 2.6f; t += 1f)
                if (s.Distance(mx + outv.X * t, mz + outv.Y * t, out _) < s.Hw + .2f) return false;
        }
        // a corner: a road with a pavement within reach (a corner arc's middle can be ~8 m from the arm's centre line end)
        foreach (var s in ctx.Segs)
        {
            var r = s.Road;
            if (r.Walk == 0 || r.IsBridge || r.IsRail || r.IsTram) continue;
            float d = s.Distance(mx, mz, out _) - s.Hw;
            if (d < r.WalkWidth + 8f) { width = r.WalkWidth; return true; }
        }
        return false;
    }

    private static void AddCornerPavement(MeshData mesh, List<Vector3>? collision, Vector3 a, Vector3 b, Vector2 outv, float w)
    {
        var kerbCol = new Color(SurfConcrete, 0f, 1f, LaneCode(1, 0));
        var slabCol = new Color(SurfSlab, 0f, 1f, LaneCode(1, 0));
        var earthCol = new Color(SurfLoose, 0f, 1f, LaneCode(1, 0));
        var uv2 = new Vector2(1000f, 0f);
        var o = new Vector3(outv.X, 0, outv.Y);
        float len = new Vector2(b.X - a.X, b.Z - a.Z).Length();
        var up = new Vector3(0, Kerb, 0);
        // kerb face
        int k = mesh.V.Count;
        mesh.Add(a - new Vector3(0, .02f, 0), -o, new Vector2(0, 0), uv2, kerbCol); mesh.Add(b - new Vector3(0, .02f, 0), -o, new Vector2(0, len), uv2, kerbCol);
        mesh.Add(b + up, -o, new Vector2(Kerb, len), uv2, kerbCol); mesh.Add(a + up, -o, new Vector2(Kerb, 0), uv2, kerbCol);
        mesh.Quad(k, k + 1, k + 2, k + 3, -o, collision);
        // slabs (a little wider than the straight pavements so the corner closes)
        float ww = w + .4f;
        int t = mesh.V.Count;
        mesh.Add(a + up, Vector3.Up, new Vector2(a.X, a.Z), uv2, slabCol); mesh.Add(b + up, Vector3.Up, new Vector2(b.X, b.Z), uv2, slabCol);
        var bo = b + up + o * ww + new Vector3(0, .03f, 0); var ao = a + up + o * ww + new Vector3(0, .03f, 0);
        mesh.Add(bo, Vector3.Up, new Vector2(bo.X, bo.Z), uv2, slabCol); mesh.Add(ao, Vector3.Up, new Vector2(ao.X, ao.Z), uv2, slabCol);
        mesh.Quad(t, t + 1, t + 2, t + 3, Vector3.Up, collision);
        // slope down behind
        int s = mesh.V.Count;
        var drop = o * .7f + new Vector3(0, -.6f, 0);
        mesh.Add(ao, Vector3.Up, new Vector2(0, 0), uv2, earthCol); mesh.Add(bo, Vector3.Up, new Vector2(0, len), uv2, earthCol);
        mesh.Add(bo + drop, Vector3.Up, new Vector2(.7f, len), uv2, earthCol); mesh.Add(ao + drop, Vector3.Up, new Vector2(.7f, 0), uv2, earthCol);
        mesh.Quad(s, s + 1, s + 2, s + 3, Vector3.Up, collision);
    }

    // ---- roads ---------------------------------------------------------------------------

    private static Vector2 Dir(MapRoad r, int k)
    {
        var d = new Vector2(r.X[k + 1] - r.X[k], r.Z[k + 1] - r.Z[k]);
        float len = d.Length();
        return len > 1e-5f ? d / len : new Vector2(0, 1);
    }

    /// <summary>Sideways (right of travel) offset direction at a road point, lengthened on bends so the edges stay parallel.</summary>
    private static Vector2 Miter(MapRoad r, int point)
    {
        int last = r.X.Length - 2;
        if (point <= 0) { var d = Dir(r, 0); return new Vector2(-d.Y, d.X); }
        if (point > last) { var d = Dir(r, last); return new Vector2(-d.Y, d.X); }
        var a = Dir(r, point - 1); var b = Dir(r, point);
        var sa = new Vector2(-a.Y, a.X); var sb = new Vector2(-b.Y, b.X);
        var m = sa + sb;
        float len = m.Length();
        if (len < 1e-4f) return sb;
        m /= len;
        return m / MathF.Max(0.45f, m.Dot(sb));
    }

    private static void AddRoadSegment(MapChunkGeometry geo, in MapSeg s, int lod)
    {
        var road = s.Road;
        int k = s.Index;
        var mesh = geo.Roads;
        bool bridge = road.IsBridge;
        bool loose = road.Unpaved || road.IsRail;
        float surf = road.IsRail ? SurfRail : road.Unpaved ? SurfLoose : SurfAsphalt;
        var col = new Color(surf, Marking(road), road.HalfWidth / 16f, LaneCode(road.Lanes, road.LanesForward));
        // A hair of extra height that differs from road to road: where two roads of the same class
        // overlap at a junction their surfaces would otherwise be exactly coplanar and flicker.
        float jitter = (Hash.Of((int)(road.Along[0] * 7f), (int)(road.X[0] * 10f), (int)(road.Z[0] * 10f)) & 7) * 0.0012f;
        float bias = ClassBias(road.Cls) + jitter + (lod > 0 ? 0.06f * lod : 0f);
        float hw = road.HalfWidth;
        bool onPaved = road.OnPaved && !bridge;
        float outer = bridge ? hw + 0.35f : onPaved ? hw + 0.02f : hw + ShoulderWidth;
        float drop = bridge || onPaved ? 0f : ShoulderDrop;
        var collision = loose ? geo.LooseFaces : geo.PavedFaces;

        var p0 = new Vector2(s.Ax, s.Az); var p1 = new Vector2(s.Bx, s.Bz);
        var m0 = Miter(road, k); var m1 = Miter(road, k + 1);
        float y0 = s.Ya + bias, y1 = s.Yb + bias;
        var d = Dir(road, k);
        float grade = (y1 - y0) / MathF.Max(0.01f, (p1 - p0).Length());
        var up = new Vector3(-grade * d.X, 1f, -grade * d.Y).Normalized();

        bool walkL = road.WalkLeft && lod <= 1 && !onPaved, walkR = road.WalkRight && lod <= 1 && !onPaved;
        Span<float> off = stackalloc float[] { -outer, -hw, hw, outer };
        Span<float> dy = stackalloc float[] { -drop, 0f, 0f, -drop };
        int first = mesh.V.Count;
        for (int e = 0; e < 2; e++)
        {
            var p = e == 0 ? p0 : p1; var m = e == 0 ? m0 : m1;
            float y = e == 0 ? y0 : y1, along = road.Along[k + e];
            float uo = 0f;
            if (road.Hw != null)
            {
                // a taper (D21): the edges follow this point's width; UV.x keeps the full-width lane layout
                float h = road.Hw[k + e];
                off[0] = -(outer - hw + h); off[1] = -h; off[2] = h; off[3] = outer - hw + h;
                uo = road.UOff![k + e];
            }
            // a side with a pavement has no shoulder: the kerb stands at the carriageway edge (D27)
            if (walkL) { off[0] = off[1]; dy[0] = 0f; }
            if (walkR) { off[3] = off[2]; dy[3] = 0f; }
            for (int i = 0; i < 4; i++)
            {
                var n = i == 0 ? (up - new Vector3(m.X, 0, m.Y) * 0.28f).Normalized()
                      : i == 3 ? (up + new Vector3(m.X, 0, m.Y) * 0.28f).Normalized() : up;
                mesh.Add(new Vector3(p.X + m.X * off[i], y + dy[i], p.Y + m.Y * off[i]), bridge ? up : n,
                    new Vector2(off[i] + uo, along), new Vector2(road.Clear[k + e], road.JunctionAhead[k + e]), col);
            }
        }
        for (int i = 0; i < 3; i++)
            // The sloping shoulders are solid too: they run down into the ground, so there is no
            // lip along the road edge for someone on foot to catch on.
            mesh.Quad(first + i, first + i + 1, first + 4 + i + 1, first + 4 + i, Vector3.Up, collision);

        if (!road.IsRail)
        {
            if (k == 0 && (road.Flags & RoadFlag.StartCap) != 0) AddCap(mesh, p0, -d, y0, road.HalfWidthAt(k), road.Along[0], col, collision, !bridge && !onPaved);
            if (k == road.X.Length - 2 && (road.Flags & RoadFlag.EndCap) != 0) AddCap(mesh, p1, d, y1, road.HalfWidthAt(k + 1), road.Along[k + 1], col, collision, !bridge && !onPaved);
        }
        if (bridge) AddBridgeSides(mesh, geo.PavedFaces, p0, p1, m0, m1, y0, y1, MathF.Max(road.HalfWidthAt(k), road.HalfWidthAt(k + 1)));
        if (walkL) AddPavement(mesh, geo.PavedFaces, road, k, p0, p1, m0, m1, y0, y1, -1);
        if (walkR) AddPavement(mesh, geo.PavedFaces, road, k, p0, p1, m0, m1, y0, y1, 1);
    }

    /// <summary>
    /// A pavement along one side of a road segment (D27): a 12 cm concrete kerb at the carriageway edge, a strip of grey
    /// slabs (<see cref="MapRoad.WalkWidth"/>), and a short slope down to the ground behind it.
    /// </summary>
    private static void AddPavement(MeshData mesh, List<Vector3>? collision, MapRoad road, int k, Vector2 p0, Vector2 p1,
        Vector2 m0, Vector2 m1, float y0, float y1, int sgn)
    {
        float w = road.WalkWidth;
        var kerbCol = new Color(SurfConcrete, 0f, 1f, LaneCode(1, 0));
        var slabCol = new Color(SurfSlab, 0f, 1f, LaneCode(1, 0));
        var earthCol = new Color(SurfLoose, 0f, 1f, LaneCode(1, 0));
        var uv2 = new Vector2(1000f, 0f);
        // profile across the pavement, outward from the carriageway edge: (offset beyond the edge, height above the road)
        Span<float> o = stackalloc float[] { 0f, 0f, 0f, w, w, w + 0.7f };
        Span<float> h = stackalloc float[] { -0.02f, Kerb, Kerb, Kerb + 0.03f, Kerb + 0.03f, -0.45f };
        int first = mesh.V.Count;
        for (int e = 0; e < 2; e++)
        {
            var p = e == 0 ? p0 : p1; var m = (e == 0 ? m0 : m1) * sgn;
            float y = e == 0 ? y0 : y1, along = road.Along[k + e], hw = road.HalfWidthAt(k + e);
            var outward = new Vector3(m.X, 0, m.Y);
            for (int i = 0; i < 6; i++)
            {
                float off = hw + o[i];
                var nrm = i < 2 ? -outward : i < 4 ? Vector3.Up : (Vector3.Up + outward * 0.6f).Normalized();
                var c = i < 2 ? kerbCol : i < 4 ? slabCol : earthCol;
                var uv = i < 2 ? new Vector2(h[i], along) : new Vector2(o[i], along);
                mesh.Add(new Vector3(p.X + m.X * off, y + h[i], p.Y + m.Y * off), nrm, uv, uv2, c);
            }
        }
        // kerb face (0-1), slab top (2-3), slope (4-5)
        for (int q = 0; q < 6; q += 2)
        {
            int a = first + q, b = first + q + 1, c2 = first + 6 + q + 1, d = first + 6 + q;
            var facing = q == 0 ? new Vector3(-(m0.X * sgn), 0, -(m0.Y * sgn)) : Vector3.Up;
            mesh.Quad(a, b, c2, d, facing, collision);
        }
    }

    /// <summary>Rounds off the end of a road so junctions and dead ends have no notches.</summary>
    private static void AddCap(MeshData mesh, Vector2 p, Vector2 outward, float y, float hw, float along, Color col, List<Vector3>? collision, bool withSkirt)
    {
        var side = new Vector2(-outward.Y, outward.X);
        // road ends carry no paint: UV2.x below zero switches the markings off in the shader
        var blank = new Vector2(-10f, 0f);
        int centre = mesh.Add(new Vector3(p.X, y, p.Y), Vector3.Up, new Vector2(0, along), blank, col);
        const int steps = 6;
        int prev = -1;
        for (int i = 0; i <= steps; i++)
        {
            float a = Mathf.Pi * i / steps;
            float c = MathF.Cos(a), sn = MathF.Sin(a);
            var q = p + side * (c * hw) + outward * (sn * hw);
            int v = mesh.Add(new Vector3(q.X, y, q.Y), Vector3.Up, new Vector2(c * hw * 0.98f, along + sn * hw), blank, col);
            if (prev >= 0) mesh.Tri(centre, prev, v, Vector3.Up, collision);
            prev = v;
        }
        if (!withSkirt) return;
        // a skirt sloping into the ground around the rounded end, like the shoulders along the sides
        int prevIn = -1, prevOut = -1;
        float skirt = hw + ShoulderWidth;
        for (int i = 0; i <= steps; i++)
        {
            float a = Mathf.Pi * i / steps;
            float c = MathF.Cos(a), sn = MathF.Sin(a);
            var radial = side * c + outward * sn;
            var n = (Vector3.Up + new Vector3(radial.X, 0, radial.Y) * 0.28f).Normalized();
            var qi = p + radial * hw; var qo = p + radial * skirt;
            int vi = mesh.Add(new Vector3(qi.X, y, qi.Y), n, new Vector2(c * hw, along + sn * hw), blank, col);
            int vo = mesh.Add(new Vector3(qo.X, y - ShoulderDrop, qo.Y), n, new Vector2(c * skirt, along + sn * skirt), blank, col);
            if (prevIn >= 0) mesh.Quad(prevIn, prevOut, vo, vi, Vector3.Up, collision);
            prevIn = vi; prevOut = vo;
        }
    }

    private static void AddBridgeSides(MeshData mesh, List<Vector3>? collision, Vector2 p0, Vector2 p1, Vector2 m0, Vector2 m1, float y0, float y1, float hw)
    {
        var col = new Color(SurfConcrete, 0f, hw / 16f, 1f);
        const float thick = 0.9f, rail = 1.05f;
        float edge = hw + 0.35f;
        for (int side = -1; side <= 1; side += 2)
        {
            var a = p0 + m0 * (edge * side); var b = p1 + m1 * (edge * side);
            var outN = new Vector3(m0.X * side, 0, m0.Y * side).Normalized();
            // fascia below the deck edge
            int v = mesh.V.Count;
            mesh.Add(new Vector3(a.X, y0, a.Y), outN, new Vector2(0, 0), Vector2.Zero, col);
            mesh.Add(new Vector3(b.X, y1, b.Y), outN, new Vector2(1, 0), Vector2.Zero, col);
            mesh.Add(new Vector3(b.X, y1 - thick, b.Y), outN, new Vector2(1, 1), Vector2.Zero, col);
            mesh.Add(new Vector3(a.X, y0 - thick, a.Y), outN, new Vector2(0, 1), Vector2.Zero, col);
            mesh.Quad(v, v + 1, v + 2, v + 3, outN);
            // parapet, visible and solid from both sides
            var ra = p0 + m0 * ((edge - 0.08f) * side); var rb = p1 + m1 * ((edge - 0.08f) * side);
            foreach (var n in new[] { outN, -outN })
            {
                v = mesh.V.Count;
                mesh.Add(new Vector3(ra.X, y0, ra.Y), n, new Vector2(0, 0), Vector2.Zero, col);
                mesh.Add(new Vector3(rb.X, y1, rb.Y), n, new Vector2(1, 0), Vector2.Zero, col);
                mesh.Add(new Vector3(rb.X, y1 + rail, rb.Y), n, new Vector2(1, 1), Vector2.Zero, col);
                mesh.Add(new Vector3(ra.X, y0 + rail, ra.Y), n, new Vector2(0, 1), Vector2.Zero, col);
                mesh.Quad(v, v + 1, v + 2, v + 3, n, collision);
            }
        }
        // underside
        {
            var a = p0 - m0 * edge; var b = p0 + m0 * edge; var c = p1 + m1 * edge; var d = p1 - m1 * edge;
            int v = mesh.V.Count;
            mesh.Add(new Vector3(a.X, y0 - thick, a.Y), Vector3.Down, new Vector2(0, 0), Vector2.Zero, col);
            mesh.Add(new Vector3(b.X, y0 - thick, b.Y), Vector3.Down, new Vector2(1, 0), Vector2.Zero, col);
            mesh.Add(new Vector3(c.X, y1 - thick, c.Y), Vector3.Down, new Vector2(1, 1), Vector2.Zero, col);
            mesh.Add(new Vector3(d.X, y1 - thick, d.Y), Vector3.Down, new Vector2(0, 1), Vector2.Zero, col);
            mesh.Quad(v, v + 1, v + 2, v + 3, Vector3.Down);
        }
    }

    /// <summary>Tram rails: two strips of steel laid on whatever surface the line crosses.</summary>
    private static void AddTramSegment(MeshData mesh, MapContext ctx, in MapSeg s)
    {
        var road = s.Road;
        int k = s.Index;
        var p0 = new Vector2(s.Ax, s.Az); var p1 = new Vector2(s.Bx, s.Bz);
        var m0 = Miter(road, k); var m1 = Miter(road, k + 1);
        const float lift = 0.125f, gauge = 0.72f, half = 0.045f;
        bool bridge = road.IsBridge;
        float y0 = (bridge ? s.Ya : ctx.Height(p0.X, p0.Y, 0f, out _)) + lift;
        float y1 = (bridge ? s.Yb : ctx.Height(p1.X, p1.Y, 0f, out _)) + lift;
        var col = new Color(0.75f, 0f, 0.1f, 1f);
        for (int side = -1; side <= 1; side += 2)
        {
            float a = side * gauge - half, b = side * gauge + half;
            int v = mesh.V.Count;
            mesh.Add(new Vector3(p0.X + m0.X * a, y0, p0.Y + m0.Y * a), Vector3.Up, new Vector2(a, road.Along[k]), Vector2.Zero, col);
            mesh.Add(new Vector3(p0.X + m0.X * b, y0, p0.Y + m0.Y * b), Vector3.Up, new Vector2(b, road.Along[k]), Vector2.Zero, col);
            mesh.Add(new Vector3(p1.X + m1.X * b, y1, p1.Y + m1.Y * b), Vector3.Up, new Vector2(b, road.Along[k + 1]), Vector2.Zero, col);
            mesh.Add(new Vector3(p1.X + m1.X * a, y1, p1.Y + m1.Y * a), Vector3.Up, new Vector2(a, road.Along[k + 1]), Vector2.Zero, col);
            mesh.Quad(v, v + 1, v + 2, v + 3, Vector3.Up);
        }
    }

    // ---- street furniture -----------------------------------------------------------------

    private static void AddFurniture(MapContext ctx, float size, int lod, MapChunkGeometry geo, bool collide)
    {
        var pts = ctx.Points;
        for (int i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            byte kind = p.Kind;
            if (kind == MapPointKind.Tree) continue;                       // trees go through the vegetation pass
            if (p.X < 0 || p.X >= size || p.Z < 0 || p.Z >= size) continue;
            if (lod >= 2 && kind != MapPointKind.Pylon) continue;          // small posts are not worth drawing far away
            if (ctx.InBuilding(p.X, p.Z, 0.2f)) continue;
            if (!StepOffRoad(ctx, ref p.X, ref p.Z)) continue;
            float y = ctx.Height(p.X, p.Z, 0f, out _);
            if (!float.IsNaN(ctx.Water(p.X, p.Z))) continue;
            if (kind == MapPointKind.Bollard)
            {
                // a short concrete or dark steel post (D25); kept off drivable roads like the others
                bool steel = (Hash.Of((int)(p.X * 10f), (int)(p.Z * 10f), 9) & 1) == 0;
                var bc = steel ? new Color(.12f, .12f, .13f, 0f) : new Color(.55f, .54f, .5f, 0f);
                geo.Barriers.Box(new Vector3(p.X - .07f, y - .2f, p.Z - .07f), new Vector3(p.X + .07f, y + .9f, p.Z + .07f), bc);
                if (collide) (geo.Posts ??= new List<Vector4>()).Add(new Vector4(p.X, y, p.Z, 0.08f));
                continue;
            }
            float yaw = 0f;
            if (kind == MapPointKind.Lamp)
            {
                // the arm reaches out over the nearest carriageway
                float best = 40f;
                foreach (var s in ctx.Segs)
                {
                    if (!s.Road.Drivable) continue;
                    float d = s.Distance(p.X, p.Z, out float u);
                    if (d >= best) continue;
                    best = d;
                    float cx = s.Ax + (s.Bx - s.Ax) * u - p.X, cz = s.Az + (s.Bz - s.Az) * u - p.Z;
                    if (d > 0.3f) yaw = MathF.Atan2(cx, cz);
                }
            }
            else yaw = Hash.F01(Hash.Of((int)(p.X * 10f), (int)(p.Z * 10f), 5)) * Mathf.Tau;
            var list = kind == MapPointKind.Lamp ? geo.Lamps ??= new List<float>()
                     : kind == MapPointKind.Pole ? geo.Poles ??= new List<float>()
                     : geo.Pylons ??= new List<float>();
            Write(list, new Transform3D(new Basis(Vector3.Up, yaw), new Vector3(p.X, y, p.Z)), new Color(1, 1, 1));
            if (collide) (geo.Posts ??= new List<Vector4>()).Add(new Vector4(p.X, y, p.Z, kind == MapPointKind.Pylon ? 0.45f : 0.12f));
        }
    }

    /// <summary>
    /// Posts mapped at the real kerb can stand on our carriageway (streets are drawn a little wider than life, D18):
    /// move them out to just past the nearest road edge. False when the post still ends up on a road or junction.
    /// </summary>
    private static bool StepOffRoad(MapContext ctx, ref float x, ref float z)
    {
        const float Clear = 0.7f;
        for (int pass = 0; pass < 2; pass++)
        {
            bool moved = false;
            foreach (var s in ctx.Segs)
            {
                if (!s.Road.Drivable || s.Road.IsBridge) continue;
                float d = s.Distance(x, z, out float u);
                if (d >= s.Hw + Clear) continue;
                float cx = s.Ax + (s.Bx - s.Ax) * u, cz = s.Az + (s.Bz - s.Az) * u;
                float nx = x - cx, nz = z - cz;
                if (d < 0.05f) { nx = s.Bz - s.Az; nz = -(s.Bx - s.Ax); d = MathF.Sqrt(nx * nx + nz * nz); if (d < 1e-4f) return false; }
                else d = MathF.Sqrt(nx * nx + nz * nz);
                x = cx + nx / d * (s.Hw + Clear);
                z = cz + nz / d * (s.Hw + Clear);
                moved = true;
            }
            if (!moved) break;
        }
        foreach (var s in ctx.Segs)
            if (s.Road.Drivable && !s.Road.IsBridge && s.Distance(x, z, out _) < s.Hw + 0.2f) return false;
        foreach (var jr in ctx.Junctions)
            if (!jr.J.Paved && !jr.J.Elevated && jr.J.Contains(x - jr.OffX, z - jr.OffZ)) return false;
        return true;
    }

    /// <summary>Appends one MultiMesh instance (3x4 transform + colour) to a buffer.</summary>
    public static void Write(List<float> o, Transform3D t, Color c)
    {
        var b = t.Basis;
        o.Add(b.X.X); o.Add(b.Y.X); o.Add(b.Z.X); o.Add(t.Origin.X);
        o.Add(b.X.Y); o.Add(b.Y.Y); o.Add(b.Z.Y); o.Add(t.Origin.Y);
        o.Add(b.X.Z); o.Add(b.Y.Z); o.Add(b.Z.Z); o.Add(t.Origin.Z);
        o.Add(c.R); o.Add(c.G); o.Add(c.B); o.Add(c.A);
    }

    /// <summary>Lamp post, built once and instanced. Local +Z is the side the arm reaches out to.</summary>
    public static ArrayMesh LampMesh(Material m)
    {
        var d = new MeshData();
        var steel = new Color(0.20f, 0.21f, 0.22f);
        d.Box(new Vector3(-0.07f, -0.3f, -0.07f), new Vector3(0.07f, 8.2f, 0.07f), steel);
        d.Box(new Vector3(-0.045f, 8.1f, -0.05f), new Vector3(0.045f, 8.2f, 1.9f), steel);
        d.Box(new Vector3(-0.16f, 7.98f, 1.35f), new Vector3(0.16f, 8.1f, 2.0f), new Color(0.32f, 0.33f, 0.33f));
        return d.ToMesh(m);
    }

    public static ArrayMesh PoleMesh(Material m)
    {
        var d = new MeshData();
        var wood = new Color(0.16f, 0.12f, 0.09f);
        d.Box(new Vector3(-0.1f, -0.3f, -0.1f), new Vector3(0.1f, 8.6f, 0.1f), wood);
        d.Box(new Vector3(-0.95f, 7.5f, -0.05f), new Vector3(0.95f, 7.62f, 0.05f), wood);
        return d.ToMesh(m);
    }

    public static ArrayMesh PylonMesh(Material m)
    {
        var d = new MeshData();
        var steel = new Color(0.30f, 0.31f, 0.32f);
        // four splayed legs meeting in a mast, three cross arms
        foreach (var (sx, sz) in new[] { (-1f, -1f), (1f, -1f), (1f, 1f), (-1f, 1f) })
            d.Box(new Vector3(sx * 1.9f - 0.12f, -0.4f, sz * 1.9f - 0.12f), new Vector3(sx * 1.9f + 0.12f, 12f, sz * 1.9f + 0.12f), steel);
        d.Box(new Vector3(-2.0f, 11.8f, -2.0f), new Vector3(2.0f, 12.1f, 2.0f), steel);
        d.Box(new Vector3(-0.45f, 12f, -0.45f), new Vector3(0.45f, 31f, 0.45f), steel);
        d.Box(new Vector3(-6.5f, 21f, -0.14f), new Vector3(6.5f, 21.3f, 0.14f), steel);
        d.Box(new Vector3(-5.0f, 25.5f, -0.14f), new Vector3(5.0f, 25.8f, 0.14f), steel);
        d.Box(new Vector3(-3.2f, 29.8f, -0.14f), new Vector3(3.2f, 30.1f, 0.14f), steel);
        return d.ToMesh(m);
    }

    // ---- buildings -----------------------------------------------------------------------

    private static readonly Color[] CanopyBands =
    {
        new(0.55f, 0.09f, 0.07f), new(0.08f, 0.22f, 0.45f), new(0.10f, 0.36f, 0.16f), new(0.62f, 0.45f, 0.06f), new(0.42f, 0.42f, 0.40f),
    };

    /// <summary>A roof on posts: fuel station canopy, carport, platform shelter. Open underneath, so cars drive in.</summary>
    private static void AddCanopy(MeshData mesh, List<Vector3>? collision, MapBuilding b, float offX, float offZ, float gMin, float gMax, uint seed)
    {
        var rng = new Rng(seed);
        float top = gMax + MathF.Max(3.2f, b.Height), under = top - 0.4f;
        var band = rng.Pick(CanopyBands);
        var fascia = new Color(band.R, band.G, band.B, KindPlain);
        var deck = new Color(0.50f, 0.50f, 0.48f, KindFlatRoof);
        var soffit = new Color(0.55f, 0.55f, 0.53f, KindPlain);
        var rx = b.RX[0]; var rz = b.RZ[0];
        int n = rx.Length;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            float ax = offX + rx[i], az = offZ + rz[i], bx = offX + rx[j], bz = offZ + rz[j];
            float dx = bx - ax, dz = bz - az;
            float len = MathF.Sqrt(dx * dx + dz * dz);
            if (len < 0.02f) continue;
            var nrm = new Vector3(dz / len, 0, -dx / len);
            int v = mesh.V.Count;
            mesh.Add(new Vector3(ax, under, az), nrm, new Vector2(0, 0), Vector2.Zero, fascia);
            mesh.Add(new Vector3(bx, under, bz), nrm, new Vector2(len, 0), Vector2.Zero, fascia);
            mesh.Add(new Vector3(bx, top, bz), nrm, new Vector2(len, 0.4f), Vector2.Zero, fascia);
            mesh.Add(new Vector3(ax, top, az), nrm, new Vector2(0, 0.4f), Vector2.Zero, fascia);
            mesh.Quad(v, v + 1, v + 2, v + 3, nrm);
        }
        var tris = _earScratch ??= new List<int>();
        tris.Clear();
        Triangulate(rx, rz, tris);
        foreach (var (y, facing, col, solid) in new[] { (top, Vector3.Up, deck, true), (under, Vector3.Down, soffit, false) })
        {
            int baseV = mesh.V.Count;
            for (int i = 0; i < n; i++)
                mesh.Add(new Vector3(offX + rx[i], y, offZ + rz[i]), facing, new Vector2(rx[i], rz[i]), Vector2.Zero, col);
            for (int i = 0; i + 2 < tris.Count; i += 3)
                mesh.Tri(baseV + tris[i], baseV + tris[i + 1], baseV + tris[i + 2], facing, solid ? collision : null);
        }
        // posts at up to six corners, pulled in from the edge
        var post = new Color(0.42f, 0.42f, 0.41f, KindPlain);
        int step = Math.Max(1, (n + 5) / 6);
        for (int i = 0; i < n; i += step)
        {
            float px = offX + rx[i] + (b.Cx - rx[i]) * 0.14f, pz = offZ + rz[i] + (b.Cz - rz[i]) * 0.14f;
            mesh.Box(new Vector3(px - 0.16f, gMin - 0.4f, pz - 0.16f), new Vector3(px + 0.16f, under, pz + 0.16f), post, collision);
        }
    }

    // Vertex colour alpha selects the surface in shaders/building.gdshader.
    private const float KindWindows = 1f, KindPlain = 0.66f, KindTiles = 0.33f, KindFlatRoof = 0f;

    private static readonly Color[] HouseWalls =
    {
        new(0.62f, 0.58f, 0.50f), new(0.66f, 0.60f, 0.44f), new(0.55f, 0.53f, 0.50f), new(0.60f, 0.50f, 0.40f),
        new(0.50f, 0.52f, 0.48f), new(0.68f, 0.66f, 0.60f), new(0.47f, 0.33f, 0.26f),
    };
    private static readonly Color[] BlockWalls =
    {
        new(0.46f, 0.45f, 0.43f), new(0.52f, 0.50f, 0.46f), new(0.40f, 0.41f, 0.42f), new(0.55f, 0.52f, 0.44f), new(0.44f, 0.47f, 0.46f),
    };
    private static readonly Color[] PitchedRoofs =
    {
        new(0.30f, 0.13f, 0.09f), new(0.24f, 0.11f, 0.08f), new(0.12f, 0.12f, 0.13f), new(0.20f, 0.17f, 0.15f), new(0.16f, 0.20f, 0.17f),
    };

    private static void Palette(byte type, uint seed, out Color wall, out Color roof, out float wallKind)
    {
        var rng = new Rng(seed);
        float shade = rng.Range(0.86f, 1.08f);
        switch (type)
        {
            case BuildingKind.House:
                wall = rng.Pick(HouseWalls); roof = rng.Pick(PitchedRoofs); wallKind = KindWindows; break;
            case BuildingKind.Apartments:
                wall = rng.Pick(BlockWalls); roof = new Color(0.10f, 0.10f, 0.11f); wallKind = KindWindows; break;
            case BuildingKind.Commercial:
                wall = rng.Chance(0.5f) ? new Color(0.58f, 0.57f, 0.54f) : new Color(0.42f, 0.44f, 0.47f);
                roof = new Color(0.14f, 0.14f, 0.15f); wallKind = KindWindows; break;
            case BuildingKind.Civic:
                wall = rng.Chance(0.5f) ? new Color(0.62f, 0.57f, 0.47f) : new Color(0.50f, 0.36f, 0.29f);
                roof = new Color(0.17f, 0.13f, 0.12f); wallKind = KindWindows; break;
            case BuildingKind.Industrial:
                wall = rng.Chance(0.5f) ? new Color(0.40f, 0.43f, 0.45f) : new Color(0.50f, 0.49f, 0.46f);
                roof = new Color(0.22f, 0.23f, 0.24f); wallKind = KindPlain; break;
            case BuildingKind.Farm:
                wall = rng.Chance(0.5f) ? new Color(0.30f, 0.21f, 0.13f) : new Color(0.44f, 0.30f, 0.24f);
                roof = new Color(0.21f, 0.19f, 0.18f); wallKind = KindPlain; break;
            case BuildingKind.Shed:
                wall = rng.Chance(0.5f) ? new Color(0.36f, 0.35f, 0.33f) : new Color(0.30f, 0.24f, 0.18f);
                roof = new Color(0.17f, 0.16f, 0.16f); wallKind = KindPlain; break;
            case BuildingKind.Church:
                wall = rng.Chance(0.5f) ? new Color(0.50f, 0.27f, 0.20f) : new Color(0.66f, 0.63f, 0.56f);
                roof = new Color(0.16f, 0.22f, 0.20f); wallKind = KindPlain; break;
            case BuildingKind.Tower:
                wall = new Color(0.45f, 0.44f, 0.42f); roof = new Color(0.2f, 0.2f, 0.2f); wallKind = KindPlain; break;
            default:
                wall = rng.Pick(BlockWalls); roof = rng.Chance(0.4f) ? rng.Pick(PitchedRoofs) : new Color(0.13f, 0.13f, 0.14f);
                wallKind = KindWindows; break;
        }
        shade *= 0.82f;   // keep facades from glowing against the dark ground palette
        wall = new Color(wall.R * shade, wall.G * shade, wall.B * shade);
    }

    /// <summary>
    /// Extrudes one footprint. <paramref name="gMin"/>/<paramref name="gMax"/> are the lowest and highest
    /// ground under the outline: walls start below the low point and the floor sits at the high one.
    /// </summary>
    /// <summary>How far walls run below the lowest ground under the outline, so coarse distant terrain (which can sit
    /// a metre or more off the sampled ground between its grid points) never shows a gap under a building.</summary>
    public const float WallSink = 2.0f;

    public static void AddBuilding(MeshData mesh, List<Vector3>? collision, MapBuilding b, float offX, float offZ, float gMin, float gMax, uint seed)
    {
        if (b.IsCanopy) { AddCanopy(mesh, collision, b, offX, offZ, gMin, gMax, seed); return; }
        Palette(b.Type, seed, out var wall, out var roofCol, out float wallKind);
        if (b.Height < 3.4f || b.IsPart && b.Height - b.MinHeight < 3f) wallKind = KindPlain;
        float floor = gMax;
        // Only pieces of a multi-part building may start above the ground (an upper storey, a
        // spire). A whole outline tagged with a minimum height is almost always a mapping slip.
        float bottom = b.IsPart && b.MinHeight > 0.05f ? floor + b.MinHeight : gMin - WallSink;
        float top = floor + b.Height;
        bool pitched = b.Roof >= RoofKind.Gabled && b.RoofHeight > 0.2f;
        float eave = pitched ? top - b.RoofHeight : top;
        if (eave < bottom) eave = bottom;
        var wallC = new Color(wall.R, wall.G, wall.B, wallKind);

        // walls
        if (eave - bottom > 0.05f)
        {
            float wallH = eave - floor;
            int front = wallKind == KindWindows && !(b.IsPart && b.MinHeight > 0.05f) ? FrontWall(b) : -1;
            for (int q = 0; q < b.RX.Length; q++)
            {
                var rx = b.RX[q]; var rz = b.RZ[q];
                float run = 0f;
                for (int i = 0; i < rx.Length; i++)
                {
                    int j = (i + 1) % rx.Length;
                    float ax = offX + rx[i], az = offZ + rz[i], bx = offX + rx[j], bz = offZ + rz[j];
                    float dx = bx - ax, dz = bz - az;
                    float len = MathF.Sqrt(dx * dx + dz * dz);
                    if (len < 0.02f) continue;
                    var n = new Vector3(dz / len, 0, -dx / len);
                    // a negative length tells the shader this wall carries the front door
                    var meta = new Vector2(q == 0 && i == front ? -len : len, wallH);
                    int v = mesh.V.Count;
                    mesh.Add(new Vector3(ax, bottom, az), n, new Vector2(0, bottom - floor), meta, wallC);
                    mesh.Add(new Vector3(bx, bottom, bz), n, new Vector2(len, bottom - floor), meta, wallC);
                    mesh.Add(new Vector3(bx, eave, bz), n, new Vector2(len, eave - floor), meta, wallC);
                    mesh.Add(new Vector3(ax, eave, az), n, new Vector2(0, eave - floor), meta, wallC);
                    mesh.Quad(v, v + 1, v + 2, v + 3, n, collision);
                    run += len;
                }
            }
        }

        var ox = b.RX[0]; var oz = b.RZ[0];
        if (pitched && (b.Roof == RoofKind.Pyramidal || b.Roof == RoofKind.Dome))
        {
            var apex = new Vector3(offX + b.Cx, top, offZ + b.Cz);
            var tiles = new Color(roofCol.R, roofCol.G, roofCol.B, KindTiles);
            bool dome = b.Roof == RoofKind.Dome;
            for (int i = 0; i < ox.Length; i++)
            {
                int j = (i + 1) % ox.Length;
                var a = new Vector3(offX + ox[i], eave, offZ + oz[i]);
                var c = new Vector3(offX + ox[j], eave, offZ + oz[j]);
                if (dome)
                {
                    // one intermediate ring gives a rounded profile
                    var a2 = a.Lerp(apex, 0.42f) with { Y = eave + b.RoofHeight * 0.72f };
                    var c2 = c.Lerp(apex, 0.42f) with { Y = eave + b.RoofHeight * 0.72f };
                    RoofQuad(mesh, collision, a, c, c2, a2, tiles);
                    RoofTri(mesh, collision, a2, c2, apex, tiles);
                }
                else RoofTri(mesh, collision, a, c, apex, tiles);
            }
            return;
        }
        if (pitched && b.RX.Length == 1)
        {
            AddPitchedRoof(mesh, collision, b, offX, offZ, eave, top, roofCol, wallC);
            return;
        }

        // flat roof
        // (Dead Miles drew flat roofs at 0.8x its dark palette; under plain lighting that read as pure black)
        var flat = new Color(MathF.Min(1f, roofCol.R * 1.5f + 0.03f), MathF.Min(1f, roofCol.G * 1.5f + 0.03f), MathF.Min(1f, roofCol.B * 1.5f + 0.03f), KindFlatRoof);
        int baseV = mesh.V.Count;
        for (int q = 0; q < b.RX.Length; q++)
            for (int i = 0; i < b.RX[q].Length; i++)
                mesh.Add(new Vector3(offX + b.RX[q][i], top, offZ + b.RZ[q][i]), Vector3.Up, new Vector2(b.RX[q][i], b.RZ[q][i]), Vector2.Zero, flat);
        if (b.Tris != null)
        {
            for (int i = 0; i + 2 < b.Tris.Length; i += 3)
                mesh.Tri(baseV + b.Tris[i], baseV + b.Tris[i + 1], baseV + b.Tris[i + 2], Vector3.Up, collision);
        }
        else
        {
            var tris = _earScratch ??= new List<int>();
            tris.Clear();
            Triangulate(ox, oz, tris);
            for (int i = 0; i + 2 < tris.Count; i += 3)
                mesh.Tri(baseV + tris[i], baseV + tris[i + 1], baseV + tris[i + 2], Vector3.Up, collision);
        }
    }

    /// <summary>Index of the outline edge that gets the front door: the longest one.</summary>
    public static int FrontWall(MapBuilding b)
    {
        var rx = b.RX[0]; var rz = b.RZ[0];
        int best = 0; float bestLen = 0;
        for (int i = 0; i < rx.Length; i++)
        {
            int j = (i + 1) % rx.Length;
            float l = (rx[j] - rx[i]) * (rx[j] - rx[i]) + (rz[j] - rz[i]) * (rz[j] - rz[i]);
            if (l > bestLen) { bestLen = l; best = i; }
        }
        return best;
    }

    [ThreadStatic] private static List<int>? _earScratch;
    [ThreadStatic] private static List<int>? _earRemaining;

    private static void RoofTri(MeshData mesh, List<Vector3>? collision, Vector3 a, Vector3 b, Vector3 c, Color col)
    {
        var n = (b - a).Cross(c - a);
        if (n.LengthSquared() < 1e-8f) return;
        n = n.Normalized();
        if (n.Y < 0) n = -n;
        int v = mesh.V.Count;
        mesh.Add(a, n, new Vector2(a.X, a.Y), Vector2.Zero, col);
        mesh.Add(b, n, new Vector2(b.X, b.Y), Vector2.Zero, col);
        mesh.Add(c, n, new Vector2(c.X, c.Y), Vector2.Zero, col);
        mesh.Tri(v, v + 1, v + 2, n, collision);
    }

    private static void RoofQuad(MeshData mesh, List<Vector3>? collision, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
    {
        RoofTri(mesh, collision, a, b, c, col);
        RoofTri(mesh, collision, a, c, d, col);
    }

    /// <summary>Gabled or hipped roof over the footprint's tightest bounding rectangle.</summary>
    private static void AddPitchedRoof(MeshData mesh, List<Vector3>? collision, MapBuilding b, float offX, float offZ, float eave, float top, Color roofCol, Color wallC)
    {
        var rx = b.RX[0]; var rz = b.RZ[0];
        int n = rx.Length;
        // tightest rectangle aligned with one of the edges
        float bestArea = float.MaxValue;
        Vector2 axis = new(1, 0); float minU = 0, maxU = 0, minV = 0, maxV = 0;
        int tries = n <= 24 ? n : 1;
        int longest = 0; float longLen = 0;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            float l = (rx[j] - rx[i]) * (rx[j] - rx[i]) + (rz[j] - rz[i]) * (rz[j] - rz[i]);
            if (l > longLen) { longLen = l; longest = i; }
        }
        for (int t = 0; t < tries; t++)
        {
            int i = tries == 1 ? longest : t, j = (i + 1) % n;
            var d = new Vector2(rx[j] - rx[i], rz[j] - rz[i]);
            if (d.LengthSquared() < 1e-4f) continue;
            d = d.Normalized();
            var p = new Vector2(-d.Y, d.X);
            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            for (int k = 0; k < n; k++)
            {
                float u = rx[k] * d.X + rz[k] * d.Y, v = rx[k] * p.X + rz[k] * p.Y;
                u0 = MathF.Min(u0, u); u1 = MathF.Max(u1, u); v0 = MathF.Min(v0, v); v1 = MathF.Max(v1, v);
            }
            float area = (u1 - u0) * (v1 - v0);
            if (area < bestArea) { bestArea = area; axis = d; minU = u0; maxU = u1; minV = v0; maxV = v1; }
        }
        // make U the long axis
        var perp = new Vector2(-axis.Y, axis.X);
        if (maxV - minV > maxU - minU)
        {
            (axis, perp) = (perp, -axis);
            (minU, maxU, minV, maxV) = (minV, maxV, -maxU, -minU);
        }
        float halfL = (maxU - minU) * 0.5f, halfW = (maxV - minV) * 0.5f;
        var centre = axis * ((minU + maxU) * 0.5f) + perp * ((minV + maxV) * 0.5f);
        const float overhang = 0.3f;
        Vector3 P(float u, float v, float y) => new(offX + centre.X + axis.X * u + perp.X * v, y, offZ + centre.Y + axis.Y * u + perp.Y * v);
        var tiles = new Color(roofCol.R, roofCol.G, roofCol.B, KindTiles);
        float L = halfL + overhang, W = halfW + overhang;
        float ridge = b.Roof == RoofKind.Hipped ? MathF.Max(0f, halfL - halfW) : L;
        var r0 = P(-ridge, 0, top); var r1 = P(ridge, 0, top);
        var c00 = P(-L, -W, eave); var c01 = P(-L, W, eave); var c10 = P(L, -W, eave); var c11 = P(L, W, eave);
        RoofQuad(mesh, collision, c00, c10, r1, r0, tiles);
        RoofQuad(mesh, collision, c11, c01, r0, r1, tiles);
        if (b.Roof == RoofKind.Hipped)
        {
            RoofTri(mesh, collision, c01, c00, r0, tiles);
            RoofTri(mesh, collision, c10, c11, r1, tiles);
        }
        else
        {
            // gable ends, in wall colour, set back to the wall line
            var plain = new Color(wallC.R, wallC.G, wallC.B, KindPlain);
            foreach (float sgn in new[] { -1f, 1f })
            {
                var a = P(sgn * halfL, -halfW, eave); var c = P(sgn * halfL, halfW, eave); var t = P(sgn * halfL, 0, top - overhang * (top - eave) / MathF.Max(0.1f, W));
                var nrm = new Vector3(axis.X * sgn, 0, axis.Y * sgn);
                int v = mesh.V.Count;
                mesh.Add(a, nrm, new Vector2(0, 0), Vector2.Zero, plain);
                mesh.Add(c, nrm, new Vector2(1, 0), Vector2.Zero, plain);
                mesh.Add(t, nrm, new Vector2(0.5f, 1), Vector2.Zero, plain);
                mesh.Tri(v, v + 1, v + 2, nrm, collision);
            }
        }
    }

    /// <summary>Ear clipping for a simple polygon with positive shoelace area. Appends vertex indices.</summary>
    public static void Triangulate(float[] x, float[] z, List<int> tris)
    {
        int n = x.Length;
        if (n < 3) return;
        var rem = _earRemaining ??= new List<int>();
        rem.Clear();
        for (int i = 0; i < n; i++) rem.Add(i);
        int guard = n * n + 16;
        int at = 0;
        while (rem.Count > 3 && guard-- > 0)
        {
            int m = rem.Count;
            int ia = rem[(at + m - 1) % m], ib = rem[at % m], ic = rem[(at + 1) % m];
            float cross = (x[ib] - x[ia]) * (z[ic] - z[ia]) - (z[ib] - z[ia]) * (x[ic] - x[ia]);
            bool ear = cross > 1e-7f;
            if (ear)
            {
                for (int k = 0; k < m; k++)
                {
                    int ip = rem[k];
                    if (ip == ia || ip == ib || ip == ic) continue;
                    if (InTriangle(x[ip], z[ip], x[ia], z[ia], x[ib], z[ib], x[ic], z[ic])) { ear = false; break; }
                }
            }
            if (ear)
            {
                tris.Add(ia); tris.Add(ib); tris.Add(ic);
                rem.RemoveAt(at % m);
                if (at >= rem.Count) at = 0;
            }
            else at = (at + 1) % m;
        }
        if (rem.Count == 3) { tris.Add(rem[0]); tris.Add(rem[1]); tris.Add(rem[2]); }
        else
        {
            // degenerate outline: fall back to a fan so the roof is at least closed
            for (int i = 1; i + 1 < rem.Count; i++) { tris.Add(rem[0]); tris.Add(rem[i]); tris.Add(rem[i + 1]); }
        }
    }

    private static bool InTriangle(float px, float pz, float ax, float az, float bx, float bz, float cx, float cz)
    {
        float d1 = (px - bx) * (az - bz) - (ax - bx) * (pz - bz);
        float d2 = (px - cx) * (bz - cz) - (bx - cx) * (pz - cz);
        float d3 = (px - ax) * (cz - az) - (cx - ax) * (pz - az);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }
}
