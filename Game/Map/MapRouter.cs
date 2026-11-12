using System;
using System.Collections.Generic;
using Godot;

using BalticDrive.Core;

namespace BalticDrive.Map;

/// <summary>A driving route across the real map: world X/Z points with the distance along it.</summary>
public sealed class MapRoute
{
    public Vector2[] Points = Array.Empty<Vector2>();
    public float[] Along = Array.Empty<float>();
    public Vector2 Destination;
    public float Length => Along.Length > 0 ? Along[^1] : 0f;

    /// <summary>Index of the route point nearest to a position, searching around a previous index first.</summary>
    public int Nearest(Vector2 pos, int hint, out float distance)
    {
        int best = 0; float bestD = float.MaxValue;
        int from = Math.Max(0, hint - 40), to = Math.Min(Points.Length - 1, hint + 400);
        for (int pass = 0; pass < 2; pass++)
        {
            for (int i = from; i <= to; i++)
            {
                float d = Points[i].DistanceSquaredTo(pos);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (bestD < 90f * 90f) break;
            from = 0; to = Points.Length - 1;   // lost the thread: look along the whole route
        }
        distance = MathF.Sqrt(bestD);
        return best;
    }
}

/// <summary>
/// Shortest-time routing over the map's road network (A*). The graph is assembled tile by tile
/// from the same road data the world is drawn from: every road point is a node, keyed by its
/// position in decimetres, so roads that meet at a junction share a node automatically while a
/// bridge passing over a road does not. Runs on a worker thread; one router per search.
/// </summary>
public sealed class MapRouter
{
    private const int TileDm = (int)(MapTile.Size * 10);   // tile size in decimetres
    private const int MaxExpansions = 6_000_000;
    /// <summary>Beyond this distance from both ends only through roads are considered, like a real sat-nav.</summary>
    private const float LocalRadius = 5000f;
    /// <summary>Slight over-estimate of the remaining time: far fewer roads examined for a route a few percent longer.</summary>
    private const float Greed = 1.25f;

    private sealed class TileGraph
    {
        public readonly Dictionary<long, int> Index = new();
        public readonly List<long> Keys = new();
        public int[] Start = Array.Empty<int>();
        public int[] To = Array.Empty<int>();
        public float[] Cost = Array.Empty<float>();
        public bool[] Minor = Array.Empty<bool>();
    }

    private readonly MapData _data;
    private readonly Dictionary<long, TileGraph> _graphs = new();

    public MapRouter(MapData data) { _data = data; }

    private static long Key(long xdm, long zdm) => (xdm << 32) ^ (zdm & 0xffffffffL);
    private static long KeyX(long key) => key >> 32;
    private static long KeyZ(long key) => (int)(key & 0xffffffffL);
    private static long TileKey(int tx, int tz) => ((long)tx << 32) ^ (uint)tz;

    /// <summary>Typical speed in m/s, used as the cost of travelling a road.</summary>
    private static float Speed(MapRoad r)
    {
        float v = r.Cls switch
        {
            RoadClass.Motorway => 33f, RoadClass.Trunk => 28f, RoadClass.Primary => 22f, RoadClass.Secondary => 19f,
            RoadClass.Tertiary => 16f, RoadClass.Link => 14f, RoadClass.Unclassified => 13f, RoadClass.Residential => 10f,
            RoadClass.Living => 6f, RoadClass.Service => 6f, _ => 5f,
        };
        return r.Unpaved ? v * 0.6f : v;
    }

    private TileGraph Graph(int tx, int tz)
    {
        long tk = TileKey(tx, tz);
        if (_graphs.TryGetValue(tk, out var g)) return g;
        g = new TileGraph();
        var tile = _data.Tile(tx, tz);
        var edges = new List<(int from, int to, float cost, bool minor)>();
        long ox = (long)tx * TileDm, oz = (long)tz * TileDm;
        int Node(float x, float z)
        {
            long key = Key(ox + (long)MathF.Round(x * 10f), oz + (long)MathF.Round(z * 10f));
            if (!g.Index.TryGetValue(key, out int i)) { i = g.Keys.Count; g.Index[key] = i; g.Keys.Add(key); }
            return i;
        }
        foreach (var r in tile.Roads)
        {
            if (!r.Drivable) continue;
            float speed = Speed(r);
            bool oneway = (r.Flags & RoadFlag.Oneway) != 0;
            bool minor = r.Cls > RoadClass.Tertiary && r.Cls != RoadClass.Link;
            int prev = Node(r.X[0], r.Z[0]);
            for (int k = 1; k < r.X.Length; k++)
            {
                int cur = Node(r.X[k], r.Z[k]);
                if (cur != prev)
                {
                    float cost = (r.Along[k] - r.Along[k - 1]) / speed;
                    edges.Add((prev, cur, cost, minor));
                    if (!oneway) edges.Add((cur, prev, cost, minor));
                }
                prev = cur;
            }
        }
        int n = g.Keys.Count;
        g.Start = new int[n + 1];
        foreach (var e in edges) g.Start[e.from + 1]++;
        for (int i = 0; i < n; i++) g.Start[i + 1] += g.Start[i];
        g.To = new int[edges.Count]; g.Cost = new float[edges.Count]; g.Minor = new bool[edges.Count];
        var fill = new int[n];
        foreach (var e in edges)
        {
            int at = g.Start[e.from] + fill[e.from]++;
            g.To[at] = e.to; g.Cost[at] = e.cost; g.Minor[at] = e.minor;
        }
        _graphs[tk] = g;
        return g;
    }

    /// <summary>The road node nearest to a world position (within about 600 m), or false.</summary>
    private bool NearestNode(float x, float z, out long key)
    {
        key = 0;
        float best = 600f * 600f;
        bool found = false;
        int tx0 = (int)Math.Floor((x - 600f) / MapTile.Size), tx1 = (int)Math.Floor((x + 600f) / MapTile.Size);
        int tz0 = (int)Math.Floor((z - 600f) / MapTile.Size), tz1 = (int)Math.Floor((z + 600f) / MapTile.Size);
        for (int tz = tz0; tz <= tz1; tz++)
        {
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var tile = _data.Tile(tx, tz);
                float lx = (float)(x - tile.OriginX), lz = (float)(z - tile.OriginZ);
                foreach (var r in tile.Roads)
                {
                    if (!r.Drivable || r.IsBridge) continue;
                    for (int k = 0; k < r.X.Length; k++)
                    {
                        float d = (r.X[k] - lx) * (r.X[k] - lx) + (r.Z[k] - lz) * (r.Z[k] - lz);
                        if (d >= best) continue;
                        best = d; found = true;
                        key = Key((long)tx * TileDm + (long)MathF.Round(r.X[k] * 10f), (long)tz * TileDm + (long)MathF.Round(r.Z[k] * 10f));
                    }
                }
            }
        }
        return found;
    }

    private static int FloorDiv(long a, int b) => (int)Math.Floor(a / (double)b);

    /// <summary>
    /// Finds a route between two world positions. Returns null when either end is nowhere near a
    /// road, the two are not connected, or the search grows too large. <paramref name="cancelled"/>
    /// is polled so a newer request can abandon this one.
    /// </summary>
    public MapRoute? Find(Vector2 from, Vector2 to, Func<bool> cancelled)
    {
        if (!NearestNode(from.X, from.Y, out long start) || !NearestNode(to.X, to.Y, out long goal)) return null;
        var gScore = new Dictionary<long, float> { [start] = 0f };
        var cameFrom = new Dictionary<long, long>();
        var open = new PriorityQueue<long, float>();
        open.Enqueue(start, 0f);
        long gx = KeyX(goal), gz = KeyZ(goal);
        long sx = KeyX(start), sz = KeyZ(start);
        float localDm = LocalRadius * 10f;
        float Heuristic(long key)
        {
            float dx = (KeyX(key) - gx) * 0.1f, dz = (KeyZ(key) - gz) * 0.1f;
            return MathF.Sqrt(dx * dx + dz * dz) / 33f * Greed;
        }
        int expansions = 0;
        bool reached = false;
        while (open.TryDequeue(out long cur, out float priority))
        {
            if (cur == goal) { reached = true; break; }
            float g = gScore[cur];
            if (priority > g + Heuristic(cur) + 1e-2f) continue;   // stale queue entry
            if (++expansions > MaxExpansions) return null;
            if ((expansions & 4095) == 0 && cancelled()) return null;
            long x = KeyX(cur), z = KeyZ(cur);
            int tx = FloorDiv(x, TileDm), tz = FloorDiv(z, TileDm);
            // a node on a tile border belongs to both tiles
            int txA = x % TileDm == 0 ? tx - 1 : tx, tzA = z % TileDm == 0 ? tz - 1 : tz;
            bool local = Math.Abs(x - sx) < localDm && Math.Abs(z - sz) < localDm || Math.Abs(x - gx) < localDm && Math.Abs(z - gz) < localDm;
            for (int ttz = tzA; ttz <= tz; ttz++)
            {
                for (int ttx = txA; ttx <= tx; ttx++)
                {
                    var graph = Graph(ttx, ttz);
                    if (!graph.Index.TryGetValue(cur, out int i)) continue;
                    for (int e = graph.Start[i]; e < graph.Start[i + 1]; e++)
                    {
                        if (!local && graph.Minor[e]) continue;
                        long next = graph.Keys[graph.To[e]];
                        float ng = g + graph.Cost[e];
                        if (gScore.TryGetValue(next, out float old) && old <= ng) continue;
                        gScore[next] = ng;
                        cameFrom[next] = cur;
                        open.Enqueue(next, ng + Heuristic(next));
                    }
                }
            }
        }
        if (!reached) return null;

        var keys = new List<long> { goal };
        for (long k = goal; cameFrom.TryGetValue(k, out long prev); k = prev) keys.Add(prev);
        keys.Reverse();
        var route = new MapRoute { Destination = to, Points = new Vector2[keys.Count], Along = new float[keys.Count] };
        for (int i = 0; i < keys.Count; i++)
        {
            route.Points[i] = new Vector2(KeyX(keys[i]) * 0.1f, KeyZ(keys[i]) * 0.1f);
            route.Along[i] = i == 0 ? 0f : route.Along[i - 1] + route.Points[i].DistanceTo(route.Points[i - 1]);
        }
        return route;
    }
}
