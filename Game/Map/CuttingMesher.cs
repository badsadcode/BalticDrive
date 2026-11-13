using System;
using System.Collections.Generic;
using Godot;

namespace BalticDrive.Map;

/// <summary>
/// Cut-and-cover (D26): where a road runs well below the natural ground (an underpass, D16, or its approach ramps),
/// the ground drops steeply at its edge (<see cref="MapContext.Height"/>) and these concrete retaining walls hide the
/// drop; where it is deep enough for a roof (≥ <see cref="RoofDepth"/>) and OSM calls it a tunnel, a slab covers the
/// slot at ground level, so from above the land looks whole and from inside it is a concrete box. Meshed per chunk
/// segment (the chunk owning the segment's midpoint), into the barrier mesh (Shaders/barrier.gdshader styles).
/// </summary>
public static class CuttingMesher
{
    public const float WallOffset = 1.3f;   // from the carriageway edge to the wall face (past the shoulder)
    public const float MinDepth = 1.0f;     // walls start where the road is this far below the natural ground
    public const float RoofDepth = 4.8f;    // a roof needs this much headroom (4.5 m clearance + slab)

    private static readonly Color Concrete = new(.5f, .49f, .46f, .3f), Ceiling = new(.34f, .33f, .31f, 0f), Lid = new(.075f, .11f, .045f, 0f);

    /// <summary>A road deck or junction surface above this point (within a metre of its edge).</summary>
    private static bool Covered(MapContext ctx, float x, float z, MapRoad cut, float cutY)
    {
        foreach (var o in ctx.Segs)
        {
            if (o.Road.IsRail || o.Road.IsTram || o.Road == cut) continue;
            float d = o.Distance(x, z, out float u);
            if (o.Road.IsBridge && d < o.Hw + 1.5f) return true;
            // a street up at ground level running over the wall line: the wall would stand on it
            if (!o.Road.IsBridge && d < o.Hw + 0.8f && o.Ya + (o.Yb - o.Ya) * u > cutY + 2f) return true;
        }
        foreach (var jr in ctx.Junctions)
        {
            if (jr.J.Paved) continue;
            float jx = x - jr.OffX, jz = z - jr.OffZ;
            if (jr.J.Contains(jx, jz) || jr.J.EdgeDistance(jx, jz, out _) < 3f) return true;
        }
        return false;
    }

    public static void AddSegment(MapContext ctx, in MapSeg s, int lod, MeshData mesh, List<Vector3>? collision)
    {
        if (lod > 1 || s.Road.IsBridge || s.Road.IsRail) return;
        var p0 = new Vector2(s.Ax, s.Az); var p1 = new Vector2(s.Bx, s.Bz);
        var d = p1 - p0;
        float len = d.Length();
        if (len < 0.05f) return;
        d /= len;
        var side = new Vector2(-d.Y, d.X);
        float off = s.Hw + WallOffset;
        // natural ground at each wall foot, and how deep the road runs below it
        float g0l = ctx.Ground(p0.X + side.X * off, p0.Y + side.Y * off), g0r = ctx.Ground(p0.X - side.X * off, p0.Y - side.Y * off);
        float g1l = ctx.Ground(p1.X + side.X * off, p1.Y + side.Y * off), g1r = ctx.Ground(p1.X - side.X * off, p1.Y - side.Y * off);
        float depth0 = MathF.Min(g0l, g0r) - s.Ya, depth1 = MathF.Min(g1l, g1r) - s.Yb;
        if (MathF.Max(depth0, depth1) < MinDepth) return;
        var uv2 = new Vector2(8f, 0);
        for (int sgn = -1; sgn <= 1; sgn += 2)
        {
            var o = side * (off * sgn);
            float top0 = (sgn > 0 ? g0l : g0r) + 0.25f, top1 = (sgn > 0 ? g1l : g1r) + 0.25f;
            if (top0 - s.Ya < MinDepth * .5f && top1 - s.Yb < MinDepth * .5f) continue;
            // no wall where a street crosses over the cutting on its deck, or a junction surface covers it (its coping would
            // be a kerb across that street — Tom, Al. Niepodległości over the Trasa)
            var foot = (p0 + p1) * .5f + o;
            if (Covered(ctx, foot.X, foot.Y, s.Road, (s.Ya + s.Yb) * .5f)) continue;
            // no wall on a median: the other carriageway of the same cutting runs alongside
            var mid = (p0 + p1) * .5f + o + side * (sgn * 1.5f);
            if (ctx.OtherLowRoad(mid.X, mid.Y, s.Road, (s.Ya + s.Yb) * .5f, 6f)) continue;
            top0 = MathF.Max(top0, s.Ya + .4f); top1 = MathF.Max(top1, s.Yb + .4f);
            var a = new Vector3(p0.X + o.X, s.Ya - .8f, p0.Y + o.Y); var b = new Vector3(p1.X + o.X, s.Yb - .8f, p1.Y + o.Y);
            var at = new Vector3(a.X, top0, a.Z); var bt = new Vector3(b.X, top1, b.Z);
            var face = new Vector3(-side.X * sgn, 0, -side.Y * sgn);   // toward the road
            int v = mesh.V.Count;
            mesh.Add(a, face, new Vector2(0, -.8f), uv2, Concrete);
            mesh.Add(b, face, new Vector2(len, -.8f), uv2, Concrete);
            mesh.Add(bt, face, new Vector2(len, top1 - s.Yb), uv2, Concrete);
            mesh.Add(at, face, new Vector2(0, top0 - s.Ya), uv2, Concrete);
            mesh.Quad(v, v + 1, v + 2, v + 3, face, collision);
            // a coping on top so the wall has thickness from above
            var back = new Vector3(side.X * sgn, 0, side.Y * sgn) * .35f;
            int c = mesh.V.Count;
            mesh.Add(at, Vector3.Up, new Vector2(0, 0), uv2, Concrete);
            mesh.Add(bt, Vector3.Up, new Vector2(len, 0), uv2, Concrete);
            mesh.Add(bt + back, Vector3.Up, new Vector2(len, .35f), uv2, Concrete);
            mesh.Add(at + back, Vector3.Up, new Vector2(0, .35f), uv2, Concrete);
            mesh.Quad(c, c + 1, c + 2, c + 3, Vector3.Up);
        }
        // the roof: tunnels only, where both ends have headroom
        bool tunnel = (s.Road.Flags & RoadFlag.Tunnel) != 0;
        if (!tunnel || MathF.Min(depth0, depth1) < RoofDepth) return;
        // (a little under the ground, so any road or junction surface over it always covers it)
        float r0 = MathF.Min(g0l, g0r) - .4f, r1 = MathF.Min(g1l, g1r) - .4f;
        float w = off + .4f;
        var l0 = new Vector3(p0.X + side.X * w, r0, p0.Y + side.Y * w); var q0 = new Vector3(p0.X - side.X * w, r0, p0.Y - side.Y * w);
        var l1 = new Vector3(p1.X + side.X * w, r1, p1.Y + side.Y * w); var q1 = new Vector3(p1.X - side.X * w, r1, p1.Y - side.Y * w);
        var lid = new Vector2(0, 0);
        int t = mesh.V.Count;
        mesh.Add(l0, Vector3.Up, lid, uv2, Lid); mesh.Add(l1, Vector3.Up, lid, uv2, Lid);
        mesh.Add(q1, Vector3.Up, lid, uv2, Lid); mesh.Add(q0, Vector3.Up, lid, uv2, Lid);
        mesh.Quad(t, t + 1, t + 2, t + 3, Vector3.Up, collision);
        // the ceiling, a slab's thickness lower, facing down
        var down = new Vector3(0, -.5f, 0);
        int u = mesh.V.Count;
        mesh.Add(l0 + down, Vector3.Down, lid, uv2, Ceiling); mesh.Add(l1 + down, Vector3.Down, lid, uv2, Ceiling);
        mesh.Add(q1 + down, Vector3.Down, lid, uv2, Ceiling); mesh.Add(q0 + down, Vector3.Down, lid, uv2, Ceiling);
        mesh.Quad(u, u + 1, u + 2, u + 3, Vector3.Down);
    }
}
