using System;
using System.Collections.Generic;
using BalticDrive.Core;
using Godot;

namespace BalticDrive.Map;

/// <summary>
/// Fences, walls, hedges and guard rails (D25), meshed per map chunk on the worker threads. Each line is walked in
/// steps of at most <see cref="Step"/> m standing on the road-shaped ground; it leaves a gap at gates (OSM gate nodes)
/// and wherever it would cross a road or junction surface. Thin kinds (wire mesh, railings, concrete panels, guard
/// rails) are double-sided panels with posts; thick kinds (walls, hedges, jersey barriers) are boxes with caps.
/// Drawn by Shaders/barrier.gdshader (style in COLOR.a); near chunks get collision.
/// </summary>
public static class BarrierMesher
{
    private const float Step = 2.0f;

    private readonly record struct Look(float Thick, float Style, Color Col, float PostEvery, float PostSize, Color PostCol, float Lift);

    private static Look LookFor(MapBarrier b, uint h)
    {
        float r = Hash.F01(h);
        switch (b.Kind)
        {
            case BarrierKind.Railing:
                return new(0, .2f, new Color(.13f, .14f, .15f), 2.5f, .05f, new Color(.12f, .13f, .14f), 0);
            case BarrierKind.ConcreteFence:
                return new(0.08f, .3f, new Color(.52f, .51f, .48f), 2f, .14f, new Color(.48f, .47f, .44f), 0);
            case BarrierKind.Wall:
                return r < .45f ? new(0.3f, .5f, r < .2f ? new Color(.72f, .68f, .6f) : new Color(.62f, .62f, .6f), 0, 0, default, 0)
                     : r < .8f ? new(0.3f, .4f, new Color(.48f, .22f, .16f), 0, 0, default, 0)
                     : new(0.25f, .3f, new Color(.5f, .5f, .47f), 0, 0, default, 0);
            case BarrierKind.Noise:
                return new(0.22f, .8f, new Color(.36f, .42f, .43f), 4f, .2f, new Color(.3f, .32f, .33f), 0);
            case BarrierKind.Hedge:
                return new(0.9f, .6f, r < .5f ? new Color(.13f, .22f, .08f) : new Color(.1f, .19f, .09f), 0, 0, default, 0);
            case BarrierKind.Retaining:
                return new(0.4f, .3f, new Color(.5f, .49f, .46f), 0, 0, default, 0);
            case BarrierKind.Guard:
                return new(0, .7f, new Color(.66f, .68f, .7f), 4f, .1f, new Color(.55f, .57f, .6f), 0.42f);
            case BarrierKind.CityWall:
                return new(1.4f, .4f, new Color(.5f, .25f, .17f), 0, 0, default, 0);
            case BarrierKind.Jersey:
                return new(0.6f, .3f, new Color(.62f, .61f, .57f), 0, 0, default, 0);
            default:   // wire mesh fence: dark green or galvanised
                return r < .55f ? new(0, .1f, new Color(.12f, .2f, .14f), 2.5f, .06f, new Color(.1f, .17f, .12f), 0)
                                : new(0, .1f, new Color(.42f, .44f, .45f), 2.5f, .06f, new Color(.36f, .38f, .39f), 0);
        }
    }

    public static void AddBarriers(MapContext ctx, float size, int lod, MeshData mesh, List<Vector3>? collision)
    {
        if (lod > 1) return;
        var xs = new List<float>(); var zs = new List<float>(); var al = new List<float>(); var keep = new List<bool>();
        foreach (var (b, offX, offZ) in ctx.Barriers)
        {
            int n = b.X.Length;
            if (n < 2) continue;
            // (each segment is drawn by the chunk its start lies in, so a line crossing chunks is drawn once)
            var look = LookFor(b, Hash.Of((int)(b.X[0] * 10), (int)(b.Z[0] * 10), b.Index, 77));
            float height = b.Height;
            if (b.Kind == BarrierKind.Guard) height = 0.32f;   // the band itself (it stands on posts at Lift)
            // resample: points every ≤ Step m with their distance along the line
            xs.Clear(); zs.Clear(); al.Clear(); keep.Clear();
            float along = 0;
            for (int k = 0; k < n; k++)
            {
                float x = offX + b.X[k], z = offZ + b.Z[k];
                if (k > 0)
                {
                    float px = xs[^1], pz = zs[^1];
                    float len = MathF.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                    int parts = Math.Max(1, (int)MathF.Ceiling(len / Step));
                    for (int s = 1; s <= parts; s++)
                    {
                        float t = s / (float)parts;
                        xs.Add(px + (x - px) * t); zs.Add(pz + (z - pz) * t); al.Add(along + len * t);
                    }
                    along += len;
                }
                else { xs.Add(x); zs.Add(z); al.Add(0); }
            }
            // gate positions along the line
            Span<float> gateAt = stackalloc float[16]; Span<float> gateW = stackalloc float[16]; int gates = 0;
            {
                float acc = 0;
                for (int k = 0; k < n && gates < 16; k++)
                {
                    if (k > 0) acc += MathF.Sqrt((b.X[k] - b.X[k - 1]) * (b.X[k] - b.X[k - 1]) + (b.Z[k] - b.Z[k - 1]) * (b.Z[k] - b.Z[k - 1]));
                    if (b.Gap[k] > 0) { gateAt[gates] = acc; gateW[gates] = b.Gap[k]; gates++; }
                }
            }
            int m = xs.Count;
            for (int i = 0; i < m; i++)
            {
                bool ok = true;
                for (int g = 0; g < gates && ok; g++) if (MathF.Abs(al[i] - gateAt[g]) < gateW[g] * .5f) ok = false;
                if (ok)
                {
                    float edge = ctx.NearestGroundRoad(xs[i], zs[i], out _, out var road);
                    if (road != null && road.Drivable && edge < 0.4f + look.Thick * .5f) ok = false;
                }
                if (ok && ctx.Junctions.Count > 0 && ctx.NearestGroundJunction(xs[i], zs[i], out _) < 0.4f) ok = false;
                keep.Add(ok);
            }
            // emit runs of kept segments whose start lies in this chunk
            int start = -1;
            for (int i = 0; i < m - 1; i++)
            {
                bool seg = keep[i] && keep[i + 1] && xs[i] >= 0 && xs[i] < size && zs[i] >= 0 && zs[i] < size;
                if (seg && start < 0) start = i;
                if ((!seg || i == m - 2) && start >= 0)
                {
                    int end = seg ? i + 1 : i;
                    if (end > start) EmitRun(ctx, mesh, collision, xs, zs, al, start, end, look, height, b.Kind, lod);
                    start = -1;
                }
            }
        }
    }

    private static void EmitRun(MapContext ctx, MeshData mesh, List<Vector3>? collision, List<float> xs, List<float> zs, List<float> al,
        int i0, int i1, Look look, float h, byte kind, int lod)
    {
        int count = i1 - i0 + 1;
        Span<float> ys = count <= 512 ? stackalloc float[count] : new float[count];
        for (int i = 0; i < count; i++) ys[i] = ctx.Height(xs[i0 + i], zs[i0 + i], 0f, out _) - 0.05f;
        var uv2 = new Vector2(h, 0);
        var col = new Color(look.Col.R, look.Col.G, look.Col.B, look.Style);
        float lift = look.Lift;
        if (look.Thick <= 0f || lod > 0 && look.Thick < 0.2f)
        {
            // a panel: one strip, drawn from both sides by the shader
            int first = mesh.V.Count;
            for (int i = 0; i < count; i++)
            {
                int k = i0 + i;
                var dir = Dir(xs, zs, k, i0, i1);
                var nrm = new Vector3(-dir.Y, 0, dir.X);
                mesh.Add(new Vector3(xs[k], ys[i] + lift, zs[k]), nrm, new Vector2(al[k], 0), uv2, col);
                mesh.Add(new Vector3(xs[k], ys[i] + lift + h, zs[k]), nrm, new Vector2(al[k], h), uv2, col);
            }
            for (int i = 0; i + 1 < count; i++)
            {
                int a = first + 2 * i;
                mesh.I.Add(a); mesh.I.Add(a + 1); mesh.I.Add(a + 2);
                mesh.I.Add(a + 1); mesh.I.Add(a + 3); mesh.I.Add(a + 2);
                if (collision != null && kind != BarrierKind.Hedge)
                {
                    // a solid sheet to the car, from the ground to the top (guard rails too: their posts would stop it)
                    var p0 = new Vector3(xs[i0 + i], ys[i], zs[i0 + i]); var p1 = new Vector3(xs[i0 + i + 1], ys[i + 1], zs[i0 + i + 1]);
                    var up = new Vector3(0, lift + h, 0);
                    collision.Add(p0); collision.Add(p0 + up); collision.Add(p1);
                    collision.Add(p0 + up); collision.Add(p1 + up); collision.Add(p1);
                }
            }
            if (lod == 0 && look.PostEvery > 0)
                Posts(mesh, xs, zs, al, ys, i0, i1, look, h + lift);
            return;
        }
        // a box: two sides, the top, and caps at the ends
        float t = look.Thick * .5f;
        int side0 = mesh.V.Count;
        for (int s = -1; s <= 1; s += 2)
        {
            int baseV = mesh.V.Count;
            for (int i = 0; i < count; i++)
            {
                int k = i0 + i;
                var dir = Dir(xs, zs, k, i0, i1);
                var nrm = new Vector3(-dir.Y * s, 0, dir.X * s);
                var p = new Vector3(xs[k] - dir.Y * t * s, ys[i], zs[k] + dir.X * t * s);
                // walls run a little into the ground on slopes (the lowest ground along the line is not known here)
                mesh.Add(p - new Vector3(0, 0.4f, 0), nrm, new Vector2(al[k], -0.4f), uv2, col);
                mesh.Add(p + new Vector3(0, h, 0), nrm, new Vector2(al[k], h), uv2, col);
            }
            for (int i = 0; i + 1 < count; i++)
            {
                int a = baseV + 2 * i;
                if (s > 0) { mesh.I.Add(a); mesh.I.Add(a + 2); mesh.I.Add(a + 1); mesh.I.Add(a + 1); mesh.I.Add(a + 2); mesh.I.Add(a + 3); }
                else { mesh.I.Add(a); mesh.I.Add(a + 1); mesh.I.Add(a + 2); mesh.I.Add(a + 1); mesh.I.Add(a + 3); mesh.I.Add(a + 2); }
                if (collision != null)
                {
                    collision.Add(mesh.V[a]); collision.Add(mesh.V[a + 1]); collision.Add(mesh.V[a + 2]);
                    collision.Add(mesh.V[a + 1]); collision.Add(mesh.V[a + 3]); collision.Add(mesh.V[a + 2]);
                }
            }
        }
        // top
        int topV = mesh.V.Count;
        for (int i = 0; i < count; i++)
        {
            int k = i0 + i;
            var dir = Dir(xs, zs, k, i0, i1);
            var l = new Vector3(xs[k] + dir.Y * t, ys[i] + h, zs[k] - dir.X * t);
            var r = new Vector3(xs[k] - dir.Y * t, ys[i] + h, zs[k] + dir.X * t);
            mesh.Add(l, Vector3.Up, new Vector2(al[k], h), uv2, col);
            mesh.Add(r, Vector3.Up, new Vector2(al[k], h - look.Thick), uv2, col);
        }
        for (int i = 0; i + 1 < count; i++)
        {
            int a = topV + 2 * i;
            mesh.Tri(a, a + 1, a + 2, Vector3.Up); mesh.Tri(a + 1, a + 3, a + 2, Vector3.Up);
        }
        // end caps
        foreach (int e in new[] { 0, count - 1 })
        {
            int k = i0 + e;
            var dir = Dir(xs, zs, k, i0, i1) * (e == 0 ? -1 : 1);
            var l = new Vector3(xs[k] + dir.Y * t, ys[e], zs[k] - dir.X * t);
            var r = new Vector3(xs[k] - dir.Y * t, ys[e], zs[k] + dir.X * t);
            var f = new Vector3(dir.X, 0, dir.Y);
            int c = mesh.Add(l - new Vector3(0, .4f, 0), f, new Vector2(0, -.4f), uv2, col);
            mesh.Add(r - new Vector3(0, .4f, 0), f, new Vector2(look.Thick, -.4f), uv2, col);
            mesh.Add(r + new Vector3(0, h, 0), f, new Vector2(look.Thick, h), uv2, col);
            mesh.Add(l + new Vector3(0, h, 0), f, new Vector2(0, h), uv2, col);
            mesh.Quad(c, c + 1, c + 2, c + 3, f, collision);
        }
    }

    private static void Posts(MeshData mesh, List<float> xs, List<float> zs, List<float> al, Span<float> ys, int i0, int i1, Look look, float top)
    {
        float a0 = al[i0], a1 = al[i1];
        var pc = new Color(look.PostCol.R, look.PostCol.G, look.PostCol.B, 0f);   // style 0: solid
        float s = look.PostSize * .5f;
        for (float a = MathF.Ceiling(a0 / look.PostEvery) * look.PostEvery; a <= a1 + 1e-3f; a += look.PostEvery)
        {
            int k = i0;
            while (k < i1 && al[k + 1] < a) k++;
            float t = k < i1 ? (a - al[k]) / MathF.Max(1e-4f, al[k + 1] - al[k]) : 0;
            int kk = Math.Min(k + 1, i1);
            float x = xs[k] + (xs[kk] - xs[k]) * t, z = zs[k] + (zs[kk] - zs[k]) * t;
            float y = ys[k - i0] + (ys[kk - i0] - ys[k - i0]) * t;
            mesh.Box(new Vector3(x - s, y - .3f, z - s), new Vector3(x + s, y + top + .04f, z + s), pc);
        }
    }

    private static Vector2 Dir(List<float> xs, List<float> zs, int k, int i0, int i1)
    {
        int a = Math.Max(i0, k - 1), b = Math.Min(i1, k + 1);
        var d = new Vector2(xs[b] - xs[a], zs[b] - zs[a]);
        return d.LengthSquared() > 1e-8f ? d.Normalized() : Vector2.Right;
    }
}
