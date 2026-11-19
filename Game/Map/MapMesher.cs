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
    /// <summary>Tall blocks of flats up to this height stay with the near cells when BadBuilding is on (D32).</summary>
    public const float TallHousing = 62f;

    /// <summary>A tall building the near cells draw (BadBuilding's PRL towers and slabs) while the skyline only keeps
    /// its far copy.</summary>
    public static bool NearHandled(MapBuilding b) =>
        BadBuildingAdapter.Enabled && b.Height > TallBuilding && !b.Ghost && BadBuildingAdapter.Eligible(b)
        && (b.Height <= TallHousing && b.Type is BuildingKind.Apartments or BuildingKind.Generic
            // offices and towers (D70): BadBuilding's office style draws them near; the skyline keeps the far copy
            || b.Height <= TallTower && b.Type is BuildingKind.Commercial or BuildingKind.Generic);

    /// <summary>Towers up to this height are drawn by BadBuilding near the player (D70).</summary>
    public const float TallTower = 330f;

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

    // ---- who gives way where roads overlap (D57) -----------------------------------------------------------------
    // Driveways and car-park lanes are not joined to streets by junction surfaces (tools/osm_import/junctions.py), and
    // roads that cross without a shared node aren't either: their ribbons just overlap, each at its own solved height.
    // The higher one showed through the other, a service road's sloping dirt shoulder poked up through a street as a
    // wedge, and two at almost the same height flickered (Tom's dumps, 17 Nov). Now the lesser road gives way: where
    // its centre line lies on a greater road's carriageway it tucks 2.5 cm under that surface and drops its
    // shoulders, so the greater road's asphalt is what you see.

    [ThreadStatic] private static Dictionary<long, List<int>>? _grid;
    [ThreadStatic] private static MapContext? _gridCtx;
    [ThreadStatic] private static int _gridCount;
    private const float GridCell = 16f;

    private static long Cell(float x, float z) => ((long)MathF.Floor(x / GridCell) << 32) ^ (uint)(int)MathF.Floor(z / GridCell);

    private static void BuildGrid(MapContext ctx)
    {
        _grid ??= new Dictionary<long, List<int>>();
        foreach (var l in _grid.Values) l.Clear();
        var segs = ctx.Segs;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            float pad = s.Hw + 1f;
            int x0 = (int)MathF.Floor((MathF.Min(s.Ax, s.Bx) - pad) / GridCell), x1 = (int)MathF.Floor((MathF.Max(s.Ax, s.Bx) + pad) / GridCell);
            int z0 = (int)MathF.Floor((MathF.Min(s.Az, s.Bz) - pad) / GridCell), z1 = (int)MathF.Floor((MathF.Max(s.Az, s.Bz) + pad) / GridCell);
            for (int x = x0; x <= x1; x++)
                for (int z = z0; z <= z1; z++)
                {
                    long key = ((long)x << 32) ^ (uint)z;
                    if (!_grid.TryGetValue(key, out var list)) _grid[key] = list = new List<int>();
                    list.Add(i);
                }
        }
        _gridCtx = ctx; _gridCount = segs.Count;
    }

    private static int Rank(byte cls) => cls switch
    {
        RoadClass.Link => 4,                       // between tertiary and unclassified
        RoadClass.Unclassified => 5, RoadClass.Residential => 6, RoadClass.Living => 7,
        RoadClass.Service => 8, RoadClass.Track => 9,
        _ => cls,
    };

    /// <summary>Road <paramref name="a"/> (segment <paramref name="sa"/>) wins over <paramref name="b"/> (segment
    /// <paramref name="sb"/>): a greater class, then wider, then a fixed order by where it starts (both sides of an
    /// overlap must agree).</summary>
    private static bool Outranks(in MapSeg sa, in MapSeg sb)
    {
        int ra = Rank(sa.Road.Cls), rb = Rank(sb.Road.Cls);
        if (ra != rb) return ra < rb;
        if (MathF.Abs(sa.Road.HalfWidth - sb.Road.HalfWidth) > 0.05f) return sa.Road.HalfWidth > sb.Road.HalfWidth;
        float xa = sa.OffX + sa.Road.X[0], xb = sb.OffX + sb.Road.X[0];
        if (MathF.Abs(xa - xb) > 0.01f) return xa > xb;
        return sa.OffZ + sa.Road.Z[0] > sb.OffZ + sb.Road.Z[0];
    }

    /// <summary>The height a point of road <paramref name="s"/> must stay under because it lies on a greater road's
    /// carriageway (NaN: on nothing greater). Pieces of the same road meeting end to end (a tile edge, a taper) are its
    /// own continuation, not something to give way to.</summary>
    /// <param name="end">The point is where this road ends: it gives way to any road carrying on through there,
    /// whatever their classes and widths (a stub ending on a lane goes under the lane, not over it: Tom's dump at
    /// Wyczółki, where a wide entrance lane lay over the bend it ends on like a dome).</param>
    private static float GiveWay(MapContext ctx, in MapSeg s, float px, float pz, Vector2 dir, bool end = false)
    {
        if (_gridCtx != ctx || _gridCount != ctx.Segs.Count || _grid == null) BuildGrid(ctx);
        if (!_grid!.TryGetValue(Cell(px, pz), out var list)) return float.NaN;
        var road = s.Road;
        float under = float.NaN;
        var segs = ctx.Segs;
        foreach (int i in list)
        {
            var o = segs[i];
            var or = o.Road;
            if (or == road || or.IsRail || or.IsTram || or.Layer != road.Layer || or.IsBridge != road.IsBridge) continue;
            float d = o.Distance(px, pz, out float u);
            if (d > o.Hw - 0.1f) continue;
            bool atA = MathF.Abs(o.Ax - px) < 0.05f && MathF.Abs(o.Az - pz) < 0.05f, atB = MathF.Abs(o.Bx - px) < 0.05f && MathF.Abs(o.Bz - pz) < 0.05f;
            // the other road carries on through here (this point isn't one of its own ends)
            bool through = !(atA && o.Index == 0) && !(atB && o.Index == or.X.Length - 2);
            if (!(end && through) && !Outranks(o, s)) continue;
            // end to end and lined up: the same road carrying on
            if (atA || atB)
            {
                var od = new Vector2(o.Bx - o.Ax, o.Bz - o.Az);
                if (od.LengthSquared() > 1e-4f && MathF.Abs(od.Normalized().Dot(dir)) > 0.7f) continue;
            }
            float oy = o.Ya + (o.Yb - o.Ya) * u + ClassBias(or.Cls) - 0.025f;
            under = float.IsNaN(under) ? oy : MathF.Min(under, oy);
        }
        // a junction surface: everything that isn't cut back at its edge (driveways, car-park lanes) goes under it
        // (paved areas and squares already lie under the roads, D32)
        if (!road.IsBridge && road.Layer == 0)
            foreach (var jr in ctx.Junctions)
            {
                var j = jr.J;
                if (j.Ghost || j.Elevated || j.Paved || j.Pedestrian) continue;
                float jx = px - jr.OffX, jz = pz - jr.OffZ;
                if (!j.Contains(jx, jz)) continue;
                float jy = j.HeightAt(jx, jz) + JunctionBias - 0.025f;
                under = float.IsNaN(under) ? jy : MathF.Min(under, jy);
            }
        return under;
    }

    /// <summary>A junction surface's lift over its solved heights (paved areas and squares lie lower, see AddJunction).</summary>
    private const float JunctionBias = 0.105f;

    /// <summary>The point lies on some other road's carriageway (any class; not the same road carrying on): an end
    /// there gets no rounded cap and no shoulders, which would bulge out over the other road (Tom's dump at Wyczółki:
    /// a wide lane's round end hanging over the bend it ends on).</summary>
    private static bool OnOtherRoad(MapContext ctx, in MapSeg s, float px, float pz, Vector2 dir)
    {
        if (_gridCtx != ctx || _gridCount != ctx.Segs.Count || _grid == null) BuildGrid(ctx);
        if (!_grid!.TryGetValue(Cell(px, pz), out var list)) return false;
        var segs = ctx.Segs;
        foreach (int i in list)
        {
            var o = segs[i];
            if (o.Road == s.Road || o.Road.IsRail || o.Road.IsTram || o.Road.Layer != s.Road.Layer) continue;
            if (o.Distance(px, pz, out _) > o.Hw - 0.2f) continue;
            bool atA = MathF.Abs(o.Ax - px) < 0.05f && MathF.Abs(o.Az - pz) < 0.05f, atB = MathF.Abs(o.Bx - px) < 0.05f && MathF.Abs(o.Bz - pz) < 0.05f;
            if (atA || atB)
            {
                var od = new Vector2(o.Bx - o.Ax, o.Bz - o.Az);
                if (od.LengthSquared() > 1e-4f && MathF.Abs(od.Normalized().Dot(dir)) > 0.7f) continue;
            }
            return true;
        }
        return false;
    }

    public static void Build(MapContext ctx, float size, int lod, MapChunkGeometry geo, bool collide)
    {
        _gridCtx = null;   // a new context (or the same one refilled): rebuild the grid on first use
        _walk ??= new Dictionary<(MapRoad, int, int), Walk>();
        _walk.Clear();
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
                AddRoadSegment(geo, s, lod, ctx);
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
        AddGapFills(ctx, size, lod, geo);
        AddFurniture(ctx, size, lod, geo, collide);
        if (lod <= 1) { AddStreetMarks(ctx, size, geo, collide); AddTramWires(ctx, size, geo); }
        BarrierMesher.AddBarriers(ctx, size, lod, geo.Barriers, collide ? geo.BuildingFaces ??= new List<Vector3>() : null);
        var list = ctx.Buildings;
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            var b = r.B;
            if (b.Ghost || b.Height > TallBuilding && !NearHandled(b)) continue;
            if (BlocksRoad(ctx, r)) continue;
            float cx = r.OffX + b.Cx, cz = r.OffZ + b.Cz;
            if (cx < 0 || cx >= size || cz < 0 || cz >= size) continue;
            float gMin = float.MaxValue, gMax = float.MinValue;
            var rx = b.RX[0]; var rz = b.RZ[0];
            for (int k = 0; k < rx.Length; k++)
            {
                float px = r.OffX + rx[k], pz = r.OffZ + rz[k];
                // over water (moored barges, pontoons, quays) a building stands on the surface, not on the carved bed
                float w = ctx.Water(px, pz);
                float h = ctx.Height(px, pz, 0f, out _);
                if (!float.IsNaN(w) && w > h) h = w;
                gMin = MathF.Min(gMin, h); gMax = MathF.Max(gMax, h);
                // far terrain shows the natural ground, not the road-shaped one: walls reach down to both
                float nat = ctx.Ground(px, pz);
                if (float.IsNaN(w) || nat >= w) gMin = MathF.Min(gMin, nat);
            }
            // the parts of one building share a look (D51)
            uint seed = Hash.Of((int)Math.Round((ctx.Ox + r.OffX) / MapTile.Size), (int)Math.Round((ctx.Oz + r.OffZ) / MapTile.Size), b.IsPart && b.Group >= 0 ? b.Group : b.Index, 91);
            // whole buildings from the BadBuilding library near the player (D29), the plain extrusions everywhere else
            if (BadBuildingAdapter.Enabled && lod <= 1 && BadBuildingAdapter.Eligible(b)
                && BadBuildingAdapter.Add(ctx, r, lod, geo, gMin, seed)) continue;
            AddBuilding(geo.Buildings, geo.BuildingFaces, b, r.OffX, r.OffZ, gMin, gMax, seed);
        }
    }

    /// <summary>
    /// A building that a public road's centre line runs through (D36): a mapping slip, usually a village barn traced
    /// a few metres off or a road drawn over a yard (Tom's dump: a house in the middle of the road). It is left out.
    /// Living and pedestrian streets, service roads, tracks and paths do not count: arcades, driveways and gate passages
    /// really go under buildings; nor do
    /// bridges and tunnels, which pass over or under them.
    /// </summary>
    public static bool BlocksRoad(MapContext ctx, in MapBuildingRef r)
    {
        var b = r.B;
        float x0 = r.OffX + b.MinX, x1 = r.OffX + b.MaxX, z0 = r.OffZ + b.MinZ, z1 = r.OffZ + b.MaxZ;
        foreach (var s in ctx.Segs)
        {
            if (s.Road.Cls > RoadClass.Residential && s.Road.Cls != RoadClass.Link || s.Road.IsRail || s.Road.IsTram || s.Road.IsBridge || s.Road.Layer < 0) continue;
            if (MathF.Max(s.Ax, s.Bx) < x0 || MathF.Min(s.Ax, s.Bx) > x1 || MathF.Max(s.Az, s.Bz) < z0 || MathF.Min(s.Az, s.Bz) > z1) continue;
            float len = MathF.Sqrt((s.Bx - s.Ax) * (s.Bx - s.Ax) + (s.Bz - s.Az) * (s.Bz - s.Az));
            int steps = Math.Max(1, (int)(len / 1.5f));
            for (int k = 0; k <= steps; k++)
            {
                float t = k / (float)steps;
                if (b.Contains(s.Ax + (s.Bx - s.Ax) * t - r.OffX, s.Az + (s.Bz - s.Az) * t - r.OffZ)) return true;
            }
        }
        return false;
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
        // paved and pedestrian areas (squares like Plac Konstytucji) sit under every road that crosses them; junction
        // surfaces over them (a pedestrian area used to cover its roads, tram tracks and markings, D32)
        // a junction of streets that are all laid on a square goes under the square with them (D52, Tom's dump on Plac
        // Grzybowski: a dark asphalt triangle on the light paving)
        bool onSquare = !j.Paved && !j.Pedestrian && ArmsAllOnPaved(ctx, jr);
        float bias = (j.Paved || j.Pedestrian ? 0.045f : onSquare ? 0.02f : JunctionBias) + (lod > 0 ? 0.06f * lod : 0f);
        // The road shader reads UV.x as the offset from the centre line and COLOR.b * 16 as the half width (beyond it:
        // gravel shoulder). A junction is all carriageway: half width 16 m, UV.x kept within ±10 m (wrapping every 40 m).
        // pedestrian areas (squares, wide pavements mapped as areas) are paved with slabs like the pavements, so a wide
        // pavement doesn't read as a missing one (Tom's dump on Świętokrzyska, 19 Nov)
        var col = new Color(j.Pedestrian ? SurfSlab : SurfAsphalt, 0f, 1f, LaneCode(2, 0));
        var uv2 = new Vector2(1000f, 0f);             // far from any junction: no stop lines, and marking style 0 anyway
        static float U(float x, float z) => ((x * 0.5f + z * 0.5f) % 20f + 30f) % 20f - 10f;
        // slabs square to the map: UV = metres east (wrapping every 20 m, a whole number of slabs) and south
        static float W(float x) => (x % 20f + 30f) % 20f - 10f;
        bool slabs = j.Pedestrian;
        int baseV = mesh.V.Count;
        int n = j.X.Length;
        for (int k = 0; k < n; k++)
            mesh.Add(new Vector3(jr.OffX + j.X[k], j.Y[k] + bias, jr.OffZ + j.Z[k]), Vector3.Up,
                slabs ? new Vector2(W(j.X[k]), j.Z[k]) : new Vector2(U(j.X[k], j.Z[k]), j.Z[k] - j.X[k]), uv2, col);
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
                if (lod <= 1 && !j.Paved && !onSquare && start == 0 && CornerPavement(ctx, a, b, outv, out float cw))
                {
                    // a street corner between two pavements (D27): kerb up, slabs round the corner, slope to the ground
                    AddCornerPavement(mesh, geo.PavedFaces, a, b, outv, cw);
                    continue;
                }
                var drop = new Vector3(outv.X * ShoulderWidth, -ShoulderDrop, outv.Y * ShoulderWidth);
                var nrm = new Vector3(outv.X * 0.28f, 1f, outv.Y * 0.28f).Normalized();
                // shoulder: UV.x from the carriageway edge (16 m) outwards, as on the road ribbons; on paved ground it
                // stays asphalt (D57)
                float u0 = ctx.Land((a.X + b.X) * 0.5f, (a.Z + b.Z) * 0.5f) == MapLand.Paved ? 14f : 16f, u1 = u0 == 16f ? 16f + ShoulderWidth : 15.9f;
                int v0 = mesh.Add(a, Vector3.Up, new Vector2(u0, 0), uv2, col);
                int v1 = mesh.Add(b, Vector3.Up, new Vector2(u0, len), uv2, col);
                int v2 = mesh.Add(b + drop, nrm, new Vector2(u1, len), uv2, col);
                int v3 = mesh.Add(a + drop, nrm, new Vector2(u1, 0), uv2, col);
                mesh.Quad(v0, v1, v2, v3, Vector3.Up, geo.PavedFaces);
            }
            start = end;
        }
    }

    /// <summary>A pavement piece: none, clear, carrying on over a driveway (a lowered kerb), or blocked.</summary>
    private enum Walk : byte { None, Clear, Driveway, Blocked }
    [ThreadStatic] private static Dictionary<(MapRoad, int, int), Walk>? _walk;

    /// <summary>The pavement state of piece <paramref name="k"/> of the segment's road on one side, worked out once.</summary>
    private static Walk WalkAt(MapContext ctx, in MapSeg s, int k, int sgn)
    {
        var road = s.Road;
        if (k < 0 || k > road.X.Length - 2) return Walk.None;
        if (sgn < 0 ? !road.WalkLeft : !road.WalkRight) return Walk.None;
        var key = (road, k, sgn);
        if (_walk != null && _walk.TryGetValue(key, out var w)) return w;
        var seg = k == s.Index ? s : new MapSeg
        {
            Ax = s.OffX + road.X[k], Az = s.OffZ + road.Z[k], Bx = s.OffX + road.X[k + 1], Bz = s.OffZ + road.Z[k + 1],
            Hw = road.HalfWidthAt(k), Road = road, Index = k, OffX = s.OffX, OffZ = s.OffZ,
        };
        w = WalkState(ctx, seg, sgn);
        if (_walk != null) _walk[key] = w;
        return w;
    }

    /// <summary>Kerb heights at the piece's two ends (D57): full where the pavement carries on, low over a driveway,
    /// and sloping down to the road where the pavement really stops (the next piece is blocked or the road ends
    /// without a junction), so it doesn't end like a block. Over a driveway the kerb is lowered for cars.</summary>
    private static (float Start, float End) Kerbs(MapContext ctx, in MapSeg s, int k, int sgn, Walk here)
    {
        if (here == Walk.Driveway) return (DroppedKerb, DroppedKerb);
        var road = s.Road;
        bool startJ = k == 0 && (road.Flags2 & RoadFlag.JunctionStart) != 0, endJ = k == road.X.Length - 2 && (road.Flags2 & RoadFlag.JunctionEnd) != 0;
        var before = WalkAt(ctx, s, k - 1, sgn);
        var after = WalkAt(ctx, s, k + 1, sgn);
        bool openStart = !startJ && before is Walk.None or Walk.Blocked && !(k == 0 && TileEdge(road, 0));
        bool openEnd = !endJ && after is Walk.None or Walk.Blocked && !(k == road.X.Length - 2 && TileEdge(road, road.X.Length - 1));
        if (openStart && openEnd) return (DroppedKerb, DroppedKerb);
        return (openStart ? DroppedKerb : Kerb, openEnd ? DroppedKerb : Kerb);
    }

    /// <summary>A road point on the tile's edge: the road carries on in the next tile (its pavement too).</summary>
    private static bool TileEdge(MapRoad r, int i)
    {
        float x = r.X[i], z = r.Z[i];
        return x < 0.05f || z < 0.05f || x > MapTile.Size - 0.05f || z > MapTile.Size - 0.05f;
    }

    /// <summary>Lowered kerb (a driveway, the end of a pavement): 3 cm.</summary>
    public const float DroppedKerb = 0.03f;

    private static Walk WalkState(MapContext ctx, in MapSeg s, int sgn) =>
        WalkBlocked(ctx, s, sgn, out bool driveway) ? Walk.Blocked : driveway ? Walk.Driveway : Walk.Clear;

    internal static bool WalkBlocked(MapContext ctx, in MapSeg s, int sgn) => WalkBlocked(ctx, s, sgn, out _);

    /// <summary>
    /// A side road, junction surface or paved area crosses this segment's pavement on side <paramref name="sgn"/>
    /// (−1 left, +1 right of the point order): leave the pavement out there (Tom's dumps: slabs across a side road).
    /// A driveway crossing it doesn't (<paramref name="driveway"/>, D57).
    /// </summary>
    internal static bool WalkBlocked(MapContext ctx, in MapSeg s, int sgn, out bool driveway)
    {
        driveway = false;
        var road = s.Road;
        float dx = s.Bx - s.Ax, dz = s.Bz - s.Az, len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 1e-3f) return false;
        float rx = -dz / len * sgn, rz = dx / len * sgn;
        float off = s.Hw + road.WalkWidth * .5f;
        for (float t = 0f; t <= 1.001f; t += .5f)
        {
            float px = s.Ax + dx * t + rx * off, pz = s.Az + dz * t + rz * off;
            foreach (var o in ctx.Segs)
            {
                if (o.Road == road || o.Road.IsRail || o.Road.IsBridge) continue;
                if (o.Distance(px, pz, out _) >= o.Hw + .3f) continue;
                // a driveway or track crossing the pavement (D57): the pavement carries on over it with a lowered
                // kerb, as it does in town; one running along the street (a car-park aisle) still takes its place
                if (o.Road.Cls is RoadClass.Service or RoadClass.Track && o.Road.HalfWidth <= 3f)
                {
                    float ox = o.Bx - o.Ax, oz = o.Bz - o.Az, ol = MathF.Sqrt(ox * ox + oz * oz);
                    if (ol > 1e-3f && MathF.Abs((ox * dx + oz * dz) / (ol * len)) < 0.8f) { driveway = true; continue; }
                }
                return true;
            }
            foreach (var jr in ctx.Junctions)
                if (!jr.J.Elevated && jr.J.Contains(px - jr.OffX, pz - jr.OffZ)) return true;
        }
        return PavedBehind(ctx, s, sgn);
    }

    /// <summary>Just behind the slabs on side <paramref name="sgn"/> lies a paved area or a square: the paving is the
    /// pavement there (D52, Tom's dump on Świętokrzyska: a kerbed strip standing alone in a sea of car park asphalt).</summary>
    private static bool PavedBehind(MapContext ctx, in MapSeg s, int sgn)
    {
        var road = s.Road;
        float dx = s.Bx - s.Ax, dz = s.Bz - s.Az, len = MathF.Sqrt(dx * dx + dz * dz);
        if (len < 1e-3f) return false;
        float rx = -dz / len * sgn, rz = dx / len * sgn;
        float behind = s.Hw + road.WalkWidth + 1.5f;
        int paved = 0;
        for (float t = .25f; t <= .76f; t += .25f)
        {
            float px = s.Ax + dx * t + rx * behind, pz = s.Az + dz * t + rz * behind;
            if (ctx.Land(px, pz) == MapLand.Paved) { paved++; continue; }   // asphalt ground cover (car parks, yards)
            foreach (var jr in ctx.Junctions)
                if ((jr.J.Paved || jr.J.Pedestrian) && !jr.J.Elevated && jr.J.Contains(px - jr.OffX, pz - jr.OffZ)) { paved++; break; }
        }
        return paved >= 2;
    }

    /// <summary>
    /// A crossing split into two junction surfaces (its nodes too far apart to cluster) left a small hole between
    /// them, framed by the two surfaces and the short connector roads: grass with a ring of gravel shoulder in the
    /// middle of the carriageway (Tom's dump at Bogatki). Where two junction surfaces come within 7 m of each other and
    /// a connector runs between them, the gap is covered: the convex hull of the two outlines' facing parts (each
    /// within 14 m of the other), at their own heights, a hair under the junctions. Only a small hull (under 400 m²)
    /// with no building in it; drawn by the chunk holding the midpoint of the two centres.
    /// </summary>
    private static void AddGapFills(MapContext ctx, float size, int lod, MapChunkGeometry geo)
    {
        var js = ctx.Junctions;
        for (int a = 0; a < js.Count; a++)
            for (int b = a + 1; b < js.Count; b++)
            {
                var ja = js[a]; var jb = js[b];
                if (!Fillable(ja.J) || !Fillable(jb.J)) continue;
                float mx = (ja.OffX + ja.J.Cx + jb.OffX + jb.J.Cx) * 0.5f, mz = (ja.OffZ + ja.J.Cz + jb.OffZ + jb.J.Cz) * 0.5f;
                if (mx < 0 || mx >= size || mz < 0 || mz >= size) continue;
                if (ja.OffX + ja.J.MaxX + 7 < jb.OffX + jb.J.MinX || jb.OffX + jb.J.MaxX + 7 < ja.OffX + ja.J.MinX
                    || ja.OffZ + ja.J.MaxZ + 7 < jb.OffZ + jb.J.MinZ || jb.OffZ + jb.J.MaxZ + 7 < ja.OffZ + ja.J.MinZ) continue;
                var pa = Outline(ja); var pb = Outline(jb);
                float gap = float.MaxValue;
                foreach (var p in pa) foreach (var q in pb) gap = MathF.Min(gap, new Vector2(p.X - q.X, p.Z - q.Z).Length());
                if (gap > 7f) continue;
                // a connector: a short road cut back at both ends, from one surface to the other
                bool linked = false;
                foreach (var s in ctx.Segs)
                {
                    var r = s.Road;
                    if ((r.Flags2 & RoadFlag.JunctionStart) == 0 || (r.Flags2 & RoadFlag.JunctionEnd) == 0 || r.Along[^1] - r.Along[0] > 30f) continue;
                    float x0 = s.OffX + r.X[0], z0 = s.OffZ + r.Z[0], x1 = s.OffX + r.X[^1], z1 = s.OffZ + r.Z[^1];
                    bool fwd = Near(pa, x0, z0) && Near(pb, x1, z1), back = Near(pb, x0, z0) && Near(pa, x1, z1);
                    if (fwd || back) { linked = true; break; }
                }
                if (!linked) continue;
                var pts = new List<Vector3>();
                foreach (var p in pa) if (MinDist(pb, p) < 14f) pts.Add(p);
                foreach (var q in pb) if (MinDist(pa, q) < 14f) pts.Add(q);
                var hull = Hull(pts);
                if (hull.Count < 3) continue;
                float area = 0; var c = Vector3.Zero;
                for (int i = 0, k = hull.Count - 1; i < hull.Count; k = i++) { area += hull[k].X * hull[i].Z - hull[i].X * hull[k].Z; c += hull[i]; }
                area = MathF.Abs(area) * 0.5f; c /= hull.Count;
                if (area > 400f) continue;
                bool building = false;
                foreach (var br in ctx.Buildings) if (!br.B.Ghost && br.B.Contains(c.X - br.OffX, c.Z - br.OffZ)) { building = true; break; }
                if (building) continue;
                var mesh = geo.Roads;
                var col = new Color(SurfAsphalt, 0f, 1f, LaneCode(2, 0));
                var uv2 = new Vector2(1000f, 0f);
                float bias = JunctionBias - 0.005f + (lod > 0 ? 0.06f * lod : 0f);
                int first = mesh.V.Count;
                foreach (var h in hull)
                    mesh.Add(h + Vector3.Up * bias, Vector3.Up, new Vector2(((h.X * 0.5f + h.Z * 0.5f) % 20f + 30f) % 20f - 10f, h.Z - h.X), uv2, col);
                // the hull is counter-clockwise in x/z seen from above; Godot fronts are clockwise from the front
                for (int i = 1; i + 1 < hull.Count; i++) mesh.Tri(first, first + i + 1, first + i, Vector3.Up, geo.PavedFaces);
            }

        static bool Fillable(MapJunction j) => !j.Ghost && !j.Elevated && !j.Paved && !j.Pedestrian;
        static List<Vector3> Outline(in MapJunctionRef jr)
        {
            var j = jr.J;
            int end = j.RingEnds.Length > 0 ? j.RingEnds[0] : j.X.Length;
            var l = new List<Vector3>(end);
            for (int i = 0; i < end; i++) l.Add(new Vector3(jr.OffX + j.X[i], j.Y[i], jr.OffZ + j.Z[i]));
            return l;
        }
        static float MinDist(List<Vector3> l, Vector3 p)
        {
            float m = float.MaxValue;
            foreach (var q in l) m = MathF.Min(m, new Vector2(p.X - q.X, p.Z - q.Z).Length());
            return m;
        }
        static bool Near(List<Vector3> l, float x, float z) => MinDist(l, new Vector3(x, 0, z)) < 4f;
    }

    // ---- crossings, traffic lights, tram wires (D58) -----------------------------------------------------------------

    private static readonly Color Paint = new(0.78f, 0.78f, 0.74f, 0f);

    /// <summary>The drawn surface height of road segment <paramref name="s"/> at a point beside or on it, or of the
    /// junction surface the point lies in.</summary>
    private static float SurfaceY(MapContext ctx, in MapSeg s, float x, float z)
    {
        foreach (var jr in ctx.Junctions)
        {
            var j = jr.J;
            if (j.Ghost || j.Elevated || j.Paved || j.Pedestrian) continue;
            if (j.Contains(x - jr.OffX, z - jr.OffZ)) return j.HeightAt(x - jr.OffX, z - jr.OffZ) + JunctionBias;
        }
        s.Distance(x, z, out float u);
        return s.Ya + (s.Yb - s.Ya) * u + ClassBias(s.Road.Cls) + 0.009f;
    }

    /// <summary>The drivable road segment nearest a point, within <paramref name="reach"/> of its edge.</summary>
    private static int NearestRoad(MapContext ctx, float x, float z, float reach)
    {
        int best = -1; float bd = float.MaxValue;
        var segs = ctx.Segs;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (!s.Road.Drivable || s.Road.IsRail || s.Road.IsTram || s.Road.Layer < 0) continue;
            float d = s.Distance(x, z, out _) - s.Hw;
            if (d < reach && d < bd) { bd = d; best = i; }
        }
        return best;
    }

    /// <summary>A flat painted quad on the road: centre, half extents along <paramref name="d"/> and across.</summary>
    private static void PaintQuad(MapChunkGeometry geo, MapContext ctx, in MapSeg s, Vector2 c, Vector2 d, float along, float across)
    {
        var n = new Vector2(-d.Y, d.X);
        Span<Vector2> q = stackalloc Vector2[] { c - d * along - n * across, c + d * along - n * across, c + d * along + n * across, c - d * along + n * across };
        int first = geo.Barriers.V.Count;
        foreach (var p in q)
            geo.Barriers.Add(new Vector3(p.X, SurfaceY(ctx, s, p.X, p.Y) + 0.012f, p.Y), Vector3.Up, new Vector2(0, 0), new Vector2(0.1f, 0), Paint);
        geo.Barriers.Quad(first, first + 1, first + 2, first + 3, Vector3.Up);
    }

    /// <summary>
    /// Zebra crossings and traffic lights from the map's points (D58, Tom's side-by-side comparisons).
    /// A crossing is painted as Polish zebras are: white bars 0.5 m wide and 4 m long, lying along the road, 0.5 m
    /// apart across the whole carriageway. With traffic signals within 25 m, a stop line 2 m before it on each
    /// approach (the incoming half of a two-way road). Traffic signals stand on the right of each approach, 6 m short
    /// of the signal node: a 3.4 m pole and a head of three lamps facing the traffic; the lamps cycle (green 12 s,
    /// amber 3 s, red 15 s), the crossing direction out of step. Poles within 4 m of one already placed are skipped
    /// (OSM often tags every approach of a junction).
    /// </summary>
    private static void AddStreetMarks(MapContext ctx, float size, MapChunkGeometry geo, bool collide)
    {
        var pts = ctx.Points;
        var signals = new List<Vector2>();
        foreach (var p in pts) if (p.Kind == MapPointKind.Signals) signals.Add(new Vector2(p.X, p.Z));
        var poles = new List<Vector2>();
        foreach (var p in pts)
        {
            if (p.X < 0 || p.X >= size || p.Z < 0 || p.Z >= size) continue;
            if (p.Kind == MapPointKind.Zebra)
            {
                int si = NearestRoad(ctx, p.X, p.Z, 1f);
                if (si < 0) continue;
                var s = ctx.Segs[si];
                s.Distance(p.X, p.Z, out float u);
                var c = new Vector2(s.Ax + (s.Bx - s.Ax) * u, s.Az + (s.Bz - s.Az) * u);   // on the centre line
                var d = new Vector2(s.Bx - s.Ax, s.Bz - s.Az);
                if (d.LengthSquared() < 1e-4f) continue;
                d = d.Normalized();
                float width = 2f * s.Hw - 0.4f;
                int bars = Math.Max(1, (int)((width + 0.5f) / 1.0f));
                float start = -(bars - 1) * 0.5f;
                var n = new Vector2(-d.Y, d.X);
                for (int b = 0; b < bars; b++) PaintQuad(geo, ctx, s, c + n * (start + b), d, 2f, 0.25f);
                bool signalled = false;
                foreach (var sg in signals) if (sg.DistanceTo(c) < 25f) { signalled = true; break; }
                if (signalled)
                {
                    bool oneway = s.Road.Oneway;
                    // traffic along +d stops before the zebra on its right half; along -d, beyond it, on the other half
                    // (n is the right of +d: we drive on the right)
                    PaintQuad(geo, ctx, s, c - d * 4.2f + (oneway ? Vector2.Zero : n * s.Hw * 0.5f), d, 0.2f, oneway ? s.Hw - 0.2f : s.Hw * 0.5f - 0.1f);
                    if (!oneway) PaintQuad(geo, ctx, s, c + d * 4.2f - n * s.Hw * 0.5f, d, 0.2f, s.Hw * 0.5f - 0.1f);
                }
            }
            else if (p.Kind == MapPointKind.Signals)
            {
                var at = new Vector2(p.X, p.Z);
                // every road near the node gets lights on its approaches
                var done = new HashSet<MapRoad>();
                var segs = ctx.Segs;
                for (int i = 0; i < segs.Count; i++)
                {
                    var s = segs[i];
                    if (!s.Road.Drivable || s.Road.IsRail || s.Road.IsTram || s.Road.Layer < 0 || done.Contains(s.Road)) continue;
                    if (s.Distance(at.X, at.Y, out float u) > 20f) continue;
                    done.Add(s.Road);
                    var d = new Vector2(s.Bx - s.Ax, s.Bz - s.Az);
                    if (d.LengthSquared() < 1e-4f) continue;
                    d = d.Normalized();
                    var c = new Vector2(s.Ax + (s.Bx - s.Ax) * u, s.Az + (s.Bz - s.Az) * u);
                    int group = ((int)MathF.Round(MathF.Atan2(d.Y, d.X) / (MathF.PI / 2f)) & 1);
                    for (int dir = 1; dir >= (s.Road.Oneway ? 1 : -1); dir -= 2)
                    {
                        var go = d * dir;                                   // the way traffic moves
                        var right = new Vector2(-go.Y, go.X);               // (x, z): right of travel
                        var pole = c - go * 6f + right * (s.Hw + 0.9f);
                        if (pole.X < 0 || pole.X >= size || pole.Y < 0 || pole.Y >= size) continue;
                        bool near = false;
                        foreach (var q in poles) if (q.DistanceTo(pole) < 4f) { near = true; break; }
                        if (near || ctx.InBuilding(pole.X, pole.Y, 0.2f)) continue;
                        if (NearestRoad(ctx, pole.X, pole.Y, -0.3f) >= 0) continue;   // standing on another carriageway
                        poles.Add(pole);
                        AddSignal(geo, ctx, pole, -go, group, collide);
                    }
                }
            }
        }
    }

    /// <summary>A traffic light: a grey pole and a black head at 2.4–3.4 m facing <paramref name="face"/>, with three
    /// lamps (style 0.9 in the barrier shader: UV2 = lamp 0 red, 1 amber, 2 green; and the cycle offset).</summary>
    private static void AddSignal(MapChunkGeometry geo, MapContext ctx, Vector2 at, Vector2 face, int group, bool collide)
    {
        float y = ctx.Height(at.X, at.Y, 0f, out _);
        var b = geo.Barriers;
        var grey = new Color(0.32f, 0.33f, 0.34f, 0f);
        b.Box(new Vector3(at.X - 0.06f, y - 0.2f, at.Y - 0.06f), new Vector3(at.X + 0.06f, y + 3.45f, at.Y + 0.06f), grey);
        float yaw = MathF.Atan2(face.X, face.Y);               // the head's +Z toward the traffic
        var basis = new Basis(Vector3.Up, yaw);
        var headC = new Vector3(at.X, y + 2.9f, at.Y) + basis * new Vector3(0, 0, 0.12f);
        b.Box(headC - new Vector3(0.15f, 0.48f, 0.11f), headC + new Vector3(0.15f, 0.48f, 0.11f), new Color(0.04f, 0.04f, 0.045f, 0f), null, yaw);
        for (int k = 0; k < 3; k++)
        {
            var lc = headC + basis * new Vector3(0, 0.3f - k * 0.3f, 0.12f);
            b.Box(lc - new Vector3(0.09f, 0.09f, 0.025f), lc + new Vector3(0.09f, 0.09f, 0.025f), new Color(1, 1, 1, 0.9f), null, yaw, new Vector2(k, group * 15f));
        }
        if (collide) (geo.Posts ??= new List<Vector4>()).Add(new Vector4(at.X, y, at.Y, 0.1f));
    }

    /// <summary>
    /// Overhead wires over tram tracks (D58): a contact wire 5.6 m up along every piece of track, and a steel pole
    /// every 35 m along it (by distance along the track, so poles don't double up at chunk edges) 3.2 m to the side,
    /// with a span wire out to the contact wire. The pole goes on the side away from a neighbouring track (double
    /// tracks are two ways side by side), or the left if there's none.
    /// </summary>
    private static void AddTramWires(MapContext ctx, float size, MapChunkGeometry geo)
    {
        var b = geo.Barriers;
        var wire = new Color(0.08f, 0.08f, 0.08f, 0f);
        var steel = new Color(0.36f, 0.37f, 0.36f, 0f);
        var segs = ctx.Segs;
        foreach (var s in segs)
        {
            var r = s.Road;
            if (!r.IsTram || r.Layer < 0) continue;
            float mx = (s.Ax + s.Bx) * 0.5f, mz = (s.Az + s.Bz) * 0.5f;
            if (mx < 0 || mx >= size || mz < 0 || mz >= size) continue;
            float dx = s.Bx - s.Ax, dz = s.Bz - s.Az, len = MathF.Sqrt(dx * dx + dz * dz);
            if (len < 0.05f) continue;
            var d = new Vector2(dx, dz) / len;
            float ya = s.Ya + 5.6f, yb = s.Yb + 5.6f;
            if (r.IsBridge) { ya += 0.3f; yb += 0.3f; }
            float yaw = MathF.Atan2(-dz, dx);
            var mid = new Vector3(mx, (ya + yb) * 0.5f, mz);
            b.Box(mid - new Vector3(len * 0.5f + 0.05f, 0.012f, 0.012f), mid + new Vector3(len * 0.5f + 0.05f, 0.012f, 0.012f), wire, null, yaw);
            // poles at whole multiples of 35 m along the track that fall on this piece
            float a0 = r.Along[s.Index], a1 = r.Along[s.Index + 1];
            for (float a = MathF.Ceiling(a0 / 35f) * 35f; a < a1; a += 35f)
            {
                float t = (a - a0) / MathF.Max(0.01f, a1 - a0);
                var c = new Vector2(s.Ax + dx * t, s.Az + dz * t);
                // the left side, or the right if that's taken; clear of every other track, road and building, or none
                var n = new Vector2(-d.Y, d.X);
                Vector2? spot = null;
                foreach (int side in new[] { 1, -1 })
                {
                    var q = c + n * side * 3.2f;
                    bool clear = !ctx.InBuilding(q.X, q.Y, 0.1f) && NearestRoad(ctx, q.X, q.Y, -0.3f) < 0;
                    if (clear)
                        foreach (var o in segs)
                            if (o.Road != r && o.Road.IsTram && o.Distance(q.X, q.Y, out _) < 2.2f) { clear = false; break; }
                    if (clear) { spot = q; n *= side; break; }
                }
                if (spot is not { } pole) continue;
                float gy = ctx.Height(pole.X, pole.Y, 0f, out _), wy = s.Ya + (s.Yb - s.Ya) * t + 5.6f;
                b.Box(new Vector3(pole.X - 0.1f, gy - 0.2f, pole.Y - 0.1f), new Vector3(pole.X + 0.1f, wy + 0.9f, pole.Y + 0.1f), steel);
                // the span wire from the pole to above the track
                var spanMid = new Vector3((pole.X + c.X) * 0.5f, wy + 0.25f, (pole.Y + c.Y) * 0.5f);
                b.Box(spanMid - new Vector3(1.65f, 0.012f, 0.012f), spanMid + new Vector3(1.65f, 0.012f, 0.012f), wire, null, MathF.Atan2(-n.Y, n.X));
            }
        }
    }

    /// <summary>Convex hull in x/z (monotone chain), counter-clockwise.</summary>
    private static List<Vector3> Hull(List<Vector3> pts)
    {
        pts.Sort((p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Z.CompareTo(q.Z));
        static float Cross(Vector3 o, Vector3 a, Vector3 b) => (a.X - o.X) * (b.Z - o.Z) - (a.Z - o.Z) * (b.X - o.X);
        var h = new List<Vector3>();
        foreach (var p in pts) { while (h.Count >= 2 && Cross(h[^2], h[^1], p) <= 0) h.RemoveAt(h.Count - 1); h.Add(p); }
        int lower = h.Count + 1;
        for (int i = pts.Count - 2; i >= 0; i--) { var p = pts[i]; while (h.Count >= lower && Cross(h[^2], h[^1], p) <= 0) h.RemoveAt(h.Count - 1); h.Add(p); }
        if (h.Count > 1) h.RemoveAt(h.Count - 1);
        return h;
    }

    /// <summary>Every road that ends at this junction is laid on a paved area (and there is at least one).</summary>
    private static bool ArmsAllOnPaved(MapContext ctx, in MapJunctionRef jr)
    {
        var j = jr.J;
        int arms = 0;
        foreach (var s in ctx.Segs)
        {
            if (s.Road.IsRail) continue;
            bool touches = j.Contains(s.Ax - jr.OffX, s.Az - jr.OffZ) || j.Contains(s.Bx - jr.OffX, s.Bz - jr.OffZ);
            if (!touches) continue;
            if (!s.Road.OnPaved) return false;
            arms++;
        }
        return arms > 0;
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
            // (streets laid on a square draw no pavements: no corners from them either, D52)
            if (r.Walk == 0 || r.IsBridge || r.IsRail || r.IsTram || r.OnPaved) continue;
            float d = s.Distance(mx, mz, out _) - s.Hw;
            if (d >= r.WalkWidth + 8f) continue;
            float side = (mx - s.Ax) * -(s.Bz - s.Az) + (mz - s.Az) * (s.Bx - s.Ax);
            if (side >= 0 ? !r.WalkRight : !r.WalkLeft) continue;
            // the street's own pavement on that side was left out (a square or paved area behind it, a side road): no
            // corner either, or it stands alone as an island (Tom's dumps on Plac Bankowy and Plac Małachowskiego)
            if (PavedBehind(ctx, s, side >= 0 ? 1 : -1)) continue;
            width = r.WalkWidth;
            // and none where the corner itself lies on a square or paved area: the paving is the pavement there
            float cx = mx + outv.X * (width + 1f), cz = mz + outv.Y * (width + 1f);
            if (ctx.Land(cx, cz) == MapLand.Paved) return false;
            foreach (var jr in ctx.Junctions)
                if ((jr.J.Paved || jr.J.Pedestrian) && !jr.J.Elevated && jr.J.Contains(cx - jr.OffX, cz - jr.OffZ)) return false;
            return true;
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

    /// <summary>The direction of road piece <paramref name="k"/>. A zero-length piece (a duplicate point, as where a
    /// road is cut at a junction surface) borrows its nearest real neighbour's: it used to point due north, which bent
    /// the corner beside it and threw a pavement diagonally across the road (Tom's dump on Tamka).</summary>
    private static Vector2 Dir(MapRoad r, int k)
    {
        int last = r.X.Length - 2;
        for (int step = 0; step <= last; step++)
            foreach (int i in new[] { k - step, k + step })
            {
                if (i < 0 || i > last) continue;
                var d = new Vector2(r.X[i + 1] - r.X[i], r.Z[i + 1] - r.Z[i]);
                float len = d.Length();
                if (len > 0.01f) return d / len;
            }
        return new Vector2(0, 1);
    }

    /// <summary>Sideways (right of travel) offset direction at a road point, lengthened on bends so the edges stay
    /// parallel. On a bend tighter than the road is wide, the inner edge would run past the end of the shorter piece
    /// beside it and fold back over the carriageway as a thin fin (Tom's dumps; 290 such bends within 3 km of the
    /// centre, mostly driveways): the lengthening is capped so the outer edge of the shoulder (half width + shoulder)
    /// moves along each piece by at most 90 % of the shorter one (D57).</summary>
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
        float stretch = 1f / MathF.Max(0.45f, m.Dot(sb));
        // how far along the pieces the stretched corner reaches: offset * tan(half the turn) = offset * sqrt(stretch² - 1)
        float la = Piece(r, point - 1), lb = Piece(r, point), offset = r.HalfWidth + ShoulderWidth;
        float room = 0.9f * MathF.Min(la, lb) / offset;
        float cap = MathF.Sqrt(1f + room * room);
        return m * MathF.Min(stretch, cap);
    }

    private static float Piece(MapRoad r, int k)
    {
        float dx = r.X[k + 1] - r.X[k], dz = r.Z[k + 1] - r.Z[k];
        float l = MathF.Sqrt(dx * dx + dz * dz);
        return l > 0.01f ? l : 10f;   // a zero-length piece (a cut at a junction) doesn't limit the corner
    }

    private static void AddRoadSegment(MapChunkGeometry geo, in MapSeg s, int lod, MapContext ctx)
    {
        var road = s.Road;
        // a zero-length piece has nothing to draw (its neighbours meet at the shared point), unless it carries the
        // road's end cap
        bool capped = (s.Index == 0 && (road.Flags & RoadFlag.StartCap) != 0) || (s.Index == road.X.Length - 2 && (road.Flags & RoadFlag.EndCap) != 0);
        if (!capped && (s.Bx - s.Ax) * (s.Bx - s.Ax) + (s.Bz - s.Az) * (s.Bz - s.Az) < 1e-4f) return;
        int k = s.Index;
        var mesh = geo.Roads;
        bool bridge = road.IsBridge;
        bool loose = road.Unpaved || road.IsRail;
        float surf = road.IsRail ? SurfRail : road.Unpaved ? SurfLoose : SurfAsphalt;
        var col = new Color(surf, Marking(road), road.HalfWidth / 16f, LaneCode(road.Lanes, road.LanesForward));
        // in a car park or a yard (paved ground, D57) an unmarked road's shoulder is asphalt like the ground round it,
        // not a gravel band outlining every aisle
        if (!loose && Marking(road) == 0f && ctx.Land((s.Ax + s.Bx) * 0.5f, (s.Az + s.Bz) * 0.5f) == MapLand.Paved)
            col.B = MathF.Min(1f, (road.HalfWidth + ShoulderWidth + 0.1f) / 16f);
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
        // on a greater road's carriageway: tucked under it, no shoulders there (D57)
        bool under0 = false, under1 = false;
        bool startCap = k == 0 && (road.Flags & RoadFlag.StartCap) != 0, endCap = k == road.X.Length - 2 && (road.Flags & RoadFlag.EndCap) != 0;
        if (!road.IsRail)
        {
            float g0 = GiveWay(ctx, s, p0.X, p0.Y, d, k == 0), g1 = GiveWay(ctx, s, p1.X, p1.Y, d, k == road.X.Length - 2);
            if (!float.IsNaN(g0) && g0 < y0 + 0.3f) { under0 = true; y0 = MathF.Min(y0, g0); }
            if (!float.IsNaN(g1) && g1 < y1 + 0.3f) { under1 = true; y1 = MathF.Min(y1, g1); }
            // an end lying on another road, even a lesser one: no rounded cap, no shoulders (D57)
            if (startCap && !under0 && OnOtherRoad(ctx, s, p0.X, p0.Y, d)) { under0 = true; startCap = false; }
            if (endCap && !under1 && OnOtherRoad(ctx, s, p1.X, p1.Y, d)) { under1 = true; endCap = false; }
            if (under0) startCap = false;
            if (under1) endCap = false;
            // crossing it with both ends outside (no shared node): the middle decides, by a few centimetres at most
            if (!under0 && !under1)
            {
                var mid = (p0 + p1) * 0.5f;
                float gm = GiveWay(ctx, s, mid.X, mid.Y, d), ym = (y0 + y1) * 0.5f;
                if (!float.IsNaN(gm) && gm < ym) { float sink = MathF.Min(ym - gm, 0.04f); y0 -= sink; y1 -= sink; }
            }
        }
        float grade = (y1 - y0) / MathF.Max(0.01f, (p1 - p0).Length());
        var up = new Vector3(-grade * d.X, 1f, -grade * d.Y).Normalized();

        var wl = road.WalkLeft && lod <= 1 && !onPaved ? WalkAt(ctx, s, k, -1) : Walk.None;
        var wr = road.WalkRight && lod <= 1 && !onPaved ? WalkAt(ctx, s, k, 1) : Walk.None;
        bool walkL = wl is Walk.Clear or Walk.Driveway, walkR = wr is Walk.Clear or Walk.Driveway;
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
            // a side with a pavement has no shoulder: the kerb stands at the carriageway edge (D27); nor does an end
            // lying on a greater road (D57)
            bool under = e == 0 ? under0 : under1;
            if (walkL || under) { off[0] = off[1]; dy[0] = 0f; }
            if (walkR || under) { off[3] = off[2]; dy[3] = 0f; }
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
            if (startCap) AddCap(mesh, p0, -d, y0, road.HalfWidthAt(k), road.Along[0], col, collision, !bridge && !onPaved);
            if (endCap) AddCap(mesh, p1, d, y1, road.HalfWidthAt(k + 1), road.Along[k + 1], col, collision, !bridge && !onPaved);
        }
        if (bridge) AddBridgeSides(mesh, geo.PavedFaces, p0, p1, m0, m1, y0, y1, MathF.Max(road.HalfWidthAt(k), road.HalfWidthAt(k + 1)));
        if (walkL) AddPavement(mesh, geo.PavedFaces, road, k, p0, p1, m0, m1, y0, y1, -1, Kerbs(ctx, s, k, -1, wl));
        if (walkR) AddPavement(mesh, geo.PavedFaces, road, k, p0, p1, m0, m1, y0, y1, 1, Kerbs(ctx, s, k, 1, wr));
    }

    /// <summary>
    /// A pavement along one side of a road segment (D27): a 12 cm concrete kerb at the carriageway edge, a strip of grey
    /// slabs (<see cref="MapRoad.WalkWidth"/>), and a short slope down to the ground behind it.
    /// </summary>
    private static void AddPavement(MeshData mesh, List<Vector3>? collision, MapRoad road, int k, Vector2 p0, Vector2 p1,
        Vector2 m0, Vector2 m1, float y0, float y1, int sgn, (float Start, float End) kerb)
    {
        float w = road.WalkWidth;
        var kerbCol = new Color(SurfConcrete, 0f, 1f, LaneCode(1, 0));
        var slabCol = new Color(SurfSlab, 0f, 1f, LaneCode(1, 0));
        var earthCol = new Color(SurfLoose, 0f, 1f, LaneCode(1, 0));
        var uv2 = new Vector2(1000f, 0f);
        // profile across the pavement, outward from the carriageway edge: (offset beyond the edge, height above the road)
        Span<float> o = stackalloc float[] { 0f, 0f, 0f, w, w, w + 0.7f };
        Span<float> h = stackalloc float[6];
        int first = mesh.V.Count;
        for (int e = 0; e < 2; e++)
        {
            float kb = e == 0 ? kerb.Start : kerb.End;
            h[0] = -0.02f; h[1] = kb; h[2] = kb; h[3] = kb + 0.03f; h[4] = kb + 0.03f; h[5] = -0.45f;
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
            if (kind is MapPointKind.Zebra or MapPointKind.Signals) continue; // painted and stood up by AddStreetMarks (D58)
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
        // a part that starts in the air (min_height: an overhang, a bridge between towers, Złota 44's upper wing)
        // gets an underside, or you look up into it (D66, Tom's dump)
        if (b.IsPart && b.MinHeight > 0.05f)
        {
            var under = new Color(wallC.R * 0.8f, wallC.G * 0.8f, wallC.B * 0.8f, KindPlain);
            int ub = mesh.V.Count;
            for (int q = 0; q < b.RX.Length; q++)
                for (int i = 0; i < b.RX[q].Length; i++)
                    mesh.Add(new Vector3(offX + b.RX[q][i], bottom, offZ + b.RZ[q][i]), Vector3.Down, new Vector2(b.RX[q][i], b.RZ[q][i]), Vector2.Zero, under);
            if (b.Tris != null)
                for (int i = 0; i + 2 < b.Tris.Length; i += 3) mesh.Tri(ub + b.Tris[i], ub + b.Tris[i + 1], ub + b.Tris[i + 2], Vector3.Down, collision);
            else
            {
                var ut = _earScratch ??= new List<int>();
                ut.Clear();
                Triangulate(ox, oz, ut);
                for (int i = 0; i + 2 < ut.Count; i += 3) mesh.Tri(ub + ut[i], ub + ut[i + 1], ub + ut[i + 2], Vector3.Down, collision);
            }
        }
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
