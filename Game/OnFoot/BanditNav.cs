using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BalticDrive.Map;
using BalticDrive.World;
using Godot;

namespace BalticDrive.OnFoot;

/// <summary>
/// Finding a way round (D62: bandits used to walk straight at their goal and pile up against walls and fences).
/// A route is searched on a 1 m grid around the bandit and its goal (at most 220 m across), built from the map
/// itself, not the physics world: buildings (with 0.5 m to spare), water, and fences, walls and hedges (their gates
/// left open). A* with diagonal steps, then pulled taut into a few straight legs (only corners where the line of sight
/// breaks). On a worker thread; the bandit keeps walking meanwhile. Points are world metres (doubles), so the floating
/// origin can move freely.
/// </summary>
public static class BanditNav
{
    /// <summary>Off: bandits walk straight as before (the self-test compares; <c>--nav=0</c>).</summary>
    public static bool Enabled = true;
    private const float Cell = 1f;
    private const int MaxSide = 220;

    [ThreadStatic] private static MapContext? _ctx;

    /// <summary>A route from (ax, az) to (bx, bz) in world metres, as waypoints after the start (the last is the goal,
    /// or the nearest reachable cell to it); null when there's none or nothing is in the way of a straight line.</summary>
    public static Task<List<(double X, double Z)>?> Find(MapWorld world, double ax, double az, double bx, double bz)
        => Task.Run(() => Search(world, ax, az, bx, bz));

    private static List<(double X, double Z)>? Search(MapWorld world, double ax, double az, double bx, double bz)
    {
        // the window: both ends with 30 m around, capped (a far goal is aimed at through the window's edge)
        double dx = bx - ax, dz = bz - az, dist = Math.Sqrt(dx * dx + dz * dz);
        if (dist > MaxSide * 0.8) { bx = ax + dx / dist * MaxSide * 0.8; bz = az + dz / dist * MaxSide * 0.8; }
        double x0 = Math.Min(ax, bx) - 30, z0 = Math.Min(az, bz) - 30, x1 = Math.Max(ax, bx) + 30, z1 = Math.Max(az, bz) + 30;
        int w = Math.Min(MaxSide, (int)Math.Ceiling((x1 - x0) / Cell)), h = Math.Min(MaxSide, (int)Math.Ceiling((z1 - z0) / Cell));
        var ctx = _ctx;
        if (ctx == null || ctx.World != world) _ctx = ctx = new MapContext(world);
        ctx.Reset(x0, z0, 0, 0, w * Cell, h * Cell, withPoints: true);
        var blocked = new bool[w * h];
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                float lx = (i + 0.5f) * Cell, lz = (j + 0.5f) * Cell;
                if (ctx.InBuilding(lx, lz, 0.5f) || !float.IsNaN(ctx.Water(lx, lz))) blocked[j * w + i] = true;
            }
        foreach (var (b, ox, oz) in ctx.Barriers)
        {
            if (b.Raised) continue;
            for (int k = 0; k + 1 < b.X.Length; k++)
            {
                float sx = ox + b.X[k], sz = oz + b.Z[k], ex = ox + b.X[k + 1], ez = oz + b.Z[k + 1];
                float len = MathF.Sqrt((ex - sx) * (ex - sx) + (ez - sz) * (ez - sz));
                int steps = Math.Max(1, (int)(len / (Cell * 0.5f)));
                for (int s = 0; s <= steps; s++)
                {
                    float t = s / (float)steps, px = sx + (ex - sx) * t, pz = sz + (ez - sz) * t;
                    // a gate at either end leaves its width open
                    if (b.Gap[k] > 0 && t * len < b.Gap[k] * 0.5f + 0.3f) continue;
                    if (b.Gap[k + 1] > 0 && (1 - t) * len < b.Gap[k + 1] * 0.5f + 0.3f) continue;
                    int ci = (int)(px / Cell), cj = (int)(pz / Cell);
                    if (ci >= 0 && cj >= 0 && ci < w && cj < h) blocked[cj * w + ci] = true;
                }
            }
        }
        int start = Index(ax - x0, az - z0, w, h), goal = Index(bx - x0, bz - z0, w, h);
        if (start < 0 || goal < 0) return null;
        blocked[start] = false;   // standing in a fence's cell, or against a wall
        if (blocked[goal]) goal = NearestOpen(blocked, w, h, goal);
        if (goal < 0) return null;
        if (Visible(blocked, w, start, goal)) return null;   // nothing in the way
        var path = AStar(blocked, w, h, start, goal);
        if (path == null) return null;
        // pull taut: from each kept point, the farthest later point in plain sight
        var legs = new List<(double, double)>();
        int at = 0;
        while (at < path.Count - 1)
        {
            int next = path.Count - 1;
            while (next > at + 1 && !Visible(blocked, w, path[at], path[next])) next--;
            at = next;
            legs.Add((x0 + (path[at] % w + 0.5) * Cell, z0 + (path[at] / w + 0.5) * Cell));
        }
        return legs;
    }

    private static int Index(double x, double z, int w, int h)
    {
        int i = (int)(x / Cell), j = (int)(z / Cell);
        return i < 0 || j < 0 || i >= w || j >= h ? -1 : j * w + i;
    }

    private static int NearestOpen(bool[] blocked, int w, int h, int c)
    {
        int ci = c % w, cj = c / w;
        for (int r = 1; r < 25; r++)
            for (int j = cj - r; j <= cj + r; j++)
                for (int i = ci - r; i <= ci + r; i++)
                {
                    if (Math.Max(Math.Abs(i - ci), Math.Abs(j - cj)) != r || i < 0 || j < 0 || i >= w || j >= h) continue;
                    if (!blocked[j * w + i]) return j * w + i;
                }
        return -1;
    }

    /// <summary>A straight line between two cells crosses no blocked cell (a fat line: the cells either side of each
    /// step count too, so a route doesn't shave corners).</summary>
    private static bool Visible(bool[] blocked, int w, int a, int b)
    {
        float ax = a % w + 0.5f, az = a / w + 0.5f, bx = b % w + 0.5f, bz = b / w + 0.5f;
        float len = MathF.Sqrt((bx - ax) * (bx - ax) + (bz - az) * (bz - az));
        int steps = Math.Max(1, (int)(len * 2));
        float nx = -(bz - az) / MathF.Max(len, 1e-3f) * 0.35f, nz = (bx - ax) / MathF.Max(len, 1e-3f) * 0.35f;
        int h = blocked.Length / w;
        for (int s = 0; s <= steps; s++)
        {
            float t = s / (float)steps, x = ax + (bx - ax) * t, z = az + (bz - az) * t;
            for (int side = -1; side <= 1; side++)
            {
                int i = (int)(x + nx * side), j = (int)(z + nz * side);
                if (i < 0 || j < 0 || i >= w || j >= h || blocked[j * w + i]) return false;
            }
        }
        return true;
    }

    private static List<int>? AStar(bool[] blocked, int w, int h, int start, int goal)
    {
        var cost = new float[w * h];
        Array.Fill(cost, float.MaxValue);
        var from = new int[w * h];
        var open = new PriorityQueue<int, float>();
        cost[start] = 0;
        int gx = goal % w, gz = goal / w;
        float Heur(int c) { int dx = Math.Abs(c % w - gx), dz = Math.Abs(c / w - gz); return Math.Max(dx, dz) + 0.414f * Math.Min(dx, dz); }
        open.Enqueue(start, Heur(start));
        int expanded = 0;
        while (open.TryDequeue(out int c, out _))
        {
            if (c == goal) break;
            if (++expanded > 40000) return null;
            int ci = c % w, cj = c / w;
            for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    if (di == 0 && dj == 0) continue;
                    int ni = ci + di, nj = cj + dj;
                    if (ni < 0 || nj < 0 || ni >= w || nj >= h) continue;
                    int n = nj * w + ni;
                    if (blocked[n]) continue;
                    if (di != 0 && dj != 0 && (blocked[cj * w + ni] || blocked[nj * w + ci])) continue;   // no squeezing past corners
                    float nc = cost[c] + (di != 0 && dj != 0 ? 1.414f : 1f);
                    if (nc >= cost[n]) continue;
                    cost[n] = nc; from[n] = c;
                    open.Enqueue(n, nc + Heur(n));
                }
        }
        if (cost[goal] == float.MaxValue) return null;
        var path = new List<int>();
        for (int c = goal; c != start; c = from[c]) path.Add(c);
        path.Add(start);
        path.Reverse();
        return path;
    }
}
