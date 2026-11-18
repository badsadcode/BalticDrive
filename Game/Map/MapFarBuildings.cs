using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BalticDrive.Core;
using BalticDrive.World;
using Godot;

namespace BalticDrive.Map;

/// <summary>
/// The city beyond the near cells (D61, Tom and Sage: "buildings are important to deal with"). The content streamer
/// draws ordinary buildings to 1 km and the skyline adds towers to ~4 km; past that the city just ended. This layer
/// draws every building for whole map tiles out to <see cref="Radius"/> tiles (~8–9 km), cheaply:
/// * hidden within <see cref="NearCut"/> of the camera (the near cells draw them there; building shader near_cut);
/// * outlines with more than 12 corners simplified (Douglas–Peucker, 1.5 m), and drawn 1 % smaller about their
///   centre and 15 cm lower, so where both layers overlap (910–1090 m) the near copy hides this one;
/// * standing on the building's stored base height (no ground queries), no collision, no shadows;
/// * small buildings left out with distance: under 30 m² near, under 120 m² from the third tile out;
/// * towers too, for the tiles outside the skyline's own radius.
/// </summary>
public sealed class MapFarBuildings
{
    public const int Radius = 4;
    /// <summary>Within this distance the near cells (to 1000 m by cell centre, so at least 909 m everywhere) draw
    /// every building; this layer shows beyond it.</summary>
    public const float NearCut = 905f;
    private const int SkylineRadius = 2;

    private readonly MapWorld _map;
    private readonly Node3D _root;
    private readonly Material _material;
    private readonly Dictionary<long, Node3D?> _tiles = new();
    private readonly HashSet<long> _pending = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(long Key, int Tx, int Tz, MeshData[] Parts, int Count)> _done = new();
    /// <summary>Pieces of tiles waiting for their upload: one a frame (a whole tile at once was a 50–60 ms hitch).</summary>
    private readonly Queue<(Node3D Node, MeshData Mesh)> _uploads = new();
    private const int Split = 4;   // a tile is uploaded as 4 × 4 pieces of 512 m
    private readonly List<long> _remove = new();
    public int Buildings { get; private set; }
    public int Pending => _pending.Count + _uploads.Count;

    public MapFarBuildings(MapWorld map, Node3D root, Material building)
    {
        _map = map; _root = root;
        _material = (Material)building.Duplicate();
        if (_material is ShaderMaterial sm) sm.SetShaderParameter("near_cut", NearCut);
    }

    private static long Key(int tx, int tz) => ((long)tx << 32) ^ (uint)tz;

    public void Update(double px, double pz)
    {
        int fx = (int)Math.Floor(px / MapTile.Size), fz = (int)Math.Floor(pz / MapTile.Size);
        while (_done.TryDequeue(out var d))
        {
            _pending.Remove(d.Key);
            if (Math.Abs(d.Tx - fx) > Radius + 1 || Math.Abs(d.Tz - fz) > Radius + 1) continue;
            Node3D? node = null;
            if (d.Count > 0)
            {
                node = new Node3D { Name = $"Far{d.Tx}_{d.Tz}" };
                _root.AddChild(node);
                WorldOrigin.Anchor(node, d.Tx * (double)MapTile.Size, d.Tz * (double)MapTile.Size);
                Buildings += d.Count;
                node.SetMeta("count", d.Count);
                foreach (var part in d.Parts) if (!part.Empty) _uploads.Enqueue((node, part));
            }
            _tiles[d.Key] = node;
        }
        while (_uploads.Count > 0)
        {
            var (node, mesh) = _uploads.Dequeue();
            if (!GodotObject.IsInstanceValid(node) || node.IsQueuedForDeletion()) continue;   // its tile went meanwhile
            node.AddChild(new MeshInstance3D { Mesh = mesh.ToMesh(_material), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            break;
        }
        // nearest tiles first
        for (int ring = 0; ring <= Radius; ring++)
            for (int tz = fz - ring; tz <= fz + ring; tz++)
                for (int tx = fx - ring; tx <= fx + ring; tx++)
                {
                    if (Math.Max(Math.Abs(tx - fx), Math.Abs(tz - fz)) != ring) continue;
                    long key = Key(tx, tz);
                    if (_tiles.ContainsKey(key) || _pending.Contains(key) || _pending.Count >= 2) continue;
                    if (!_map.Data.HasTile(tx, tz)) { _tiles[key] = null; continue; }
                    _pending.Add(key);
                    int ctx = tx, ctz = tz;
                    bool towers = ring > SkylineRadius;
                    float minArea = ring >= 2 ? 120f : 30f;
                    var map = _map;
                    Task.Run(() =>
                    {
                        var parts = new MeshData[Split * Split];
                        for (int i = 0; i < parts.Length; i++) parts[i] = new MeshData();
                        int count = 0;
                        try { count = BuildTile(map, ctx, ctz, parts, towers, minArea); }
                        catch (Exception e) { Log.Error("MapFarBuildings", $"Tile {ctx},{ctz} failed: {e.Message}"); }
                        _done.Enqueue((key, ctx, ctz, parts, count));
                    });
                }
        _remove.Clear();
        foreach (var key in _tiles.Keys)
        {
            int tx = (int)(key >> 32), tz = (int)(uint)key;
            if (Math.Abs(tx - fx) > Radius + 1 || Math.Abs(tz - fz) > Radius + 1) _remove.Add(key);
        }
        foreach (var key in _remove)
        {
            if (_tiles[key] is { } n) { Buildings -= n.GetMeta("count", 0).AsInt32(); n.QueueFree(); }
            _tiles.Remove(key);
        }
    }

    /// <summary>Shows or hides what's built (the benchmark compares).</summary>
    public void SetVisible(bool on) { foreach (var n in _tiles.Values) if (n != null) n.Visible = on; }

    public void Clear()
    {
        foreach (var n in _tiles.Values) n?.QueueFree();
        _tiles.Clear();
        _uploads.Clear();
        Buildings = 0;
    }

    private static int BuildTile(MapWorld map, int tx, int tz, MeshData[] parts, bool towers, float minArea)
    {
        var tile = map.Data.Tile(tx, tz);
        int count = 0;
        foreach (var b in tile.Buildings)
        {
            if (b.Ghost || b.IsCanopy) continue;
            bool tall = b.Height > MapMesher.TallBuilding;
            if (tall && !towers) continue;   // the skyline draws those
            if (!tall && (b.MaxX - b.MinX) * (b.MaxZ - b.MinZ) < minArea) continue;
            var far = Simplified(b);
            int px = Math.Clamp((int)(b.Cx / (MapTile.Size / Split)), 0, Split - 1), pz = Math.Clamp((int)(b.Cz / (MapTile.Size / Split)), 0, Split - 1);
            MapMesher.AddBuilding(parts[pz * Split + px], null, far, 0f, 0f, b.Base - 1.0f - 0.15f, b.Base - 0.15f, Hash.Of(tx, tz, b.IsPart && b.Group >= 0 ? b.Group : b.Index, 91));
            count++;
        }
        return count;
    }

    /// <summary>A copy 1 % smaller about its centre and 15 cm lower (see the class notes), its outline simplified when
    /// it has more than 12 corners and no courtyards.</summary>
    private static MapBuilding Simplified(MapBuilding b)
    {
        var rx = b.RX; var rz = b.RZ;
        if (b.Tris == null && rx.Length == 1 && rx[0].Length > 12)
        {
            var (sx, sz) = DouglasPeucker(rx[0], rz[0], 1.5f);
            if (sx.Length >= 3) { rx = new[] { sx }; rz = new[] { sz }; }
        }
        var nx = new float[rx.Length][]; var nz = new float[rz.Length][];
        for (int r = 0; r < rx.Length; r++)
        {
            nx[r] = new float[rx[r].Length]; nz[r] = new float[rz[r].Length];
            for (int i = 0; i < rx[r].Length; i++)
            {
                nx[r][i] = b.Cx + (rx[r][i] - b.Cx) * 0.99f;
                nz[r][i] = b.Cz + (rz[r][i] - b.Cz) * 0.99f;
            }
        }
        return b.WithRings(nx, nz);
    }

    private static (float[] X, float[] Z) DouglasPeucker(float[] x, float[] z, float tol)
    {
        int n = x.Length;
        var keep = new bool[n];
        keep[0] = true;
        // split the ring at the point farthest from the first one, simplify both halves
        int far = 0; float fd = -1;
        for (int i = 1; i < n; i++) { float d = (x[i] - x[0]) * (x[i] - x[0]) + (z[i] - z[0]) * (z[i] - z[0]); if (d > fd) { fd = d; far = i; } }
        keep[far] = true;
        void Run(int a, int b)
        {
            int bb = b % n;
            float best = tol; int idx = -1;
            for (int i = a + 1; i < b; i++)
            {
                int ii = i % n;
                float ex = x[bb] - x[a], ez = z[bb] - z[a], l2 = ex * ex + ez * ez;
                float t = l2 < 1e-9f ? 0 : Math.Clamp(((x[ii] - x[a]) * ex + (z[ii] - z[a]) * ez) / l2, 0, 1);
                float dx = x[a] + ex * t - x[ii], dz = z[a] + ez * t - z[ii], d = MathF.Sqrt(dx * dx + dz * dz);
                if (d > best) { best = d; idx = i; }
            }
            if (idx < 0) return;
            keep[idx % n] = true;
            Run(a, idx); Run(idx, b);
        }
        Run(0, far); Run(far, n);
        var ox = new List<float>(); var oz = new List<float>();
        for (int i = 0; i < n; i++) if (keep[i]) { ox.Add(x[i]); oz.Add(z[i]); }
        return (ox.ToArray(), oz.ToArray());
    }
}
