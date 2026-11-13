using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BalticDrive.Core;
using BalticDrive.Map;
using Godot;

namespace BalticDrive.World;

/// <summary>
/// Roads and railways beyond the detailed road meshes (D24): one light ribbon mesh per 2 km map tile out to
/// <see cref="Radius"/> tiles, drawn by Shaders/far_road.gdshader (widened with distance, pulled toward the camera in
/// depth, handed over to the detailed roads near 1 km). Main roads go to the horizon, minor streets to ~3.5 km.
/// Tunnels and trams are left out. Built on worker threads like <see cref="MapSkyline"/>.
/// </summary>
public sealed partial class FarRoads : Node3D
{
    public int Radius = 4;   // tiles: ~9 km in every direction

    private MapWorld? _map;
    private Material _material = null!;
    private readonly Dictionary<long, Node3D?> _tiles = new();
    private readonly HashSet<long> _pending = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(long Key, int Tx, int Tz, MeshData Mesh, long Revision)> _done = new();
    private readonly List<long> _remove = new();
    private long _revision;

    public FarRoads() { Name = "FarRoads"; }

    public override void _Ready()
    {
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/far_road.gdshader") };
    }

    public void Configure(MapWorld map)
    {
        _revision++;
        foreach (var n in _tiles.Values) n?.QueueFree();
        _tiles.Clear();
        _pending.Clear();
        _map = map;
    }

    private static long Key(int tx, int tz) => ((long)tx << 32) ^ (uint)tz;

    public void Update(double px, double pz)
    {
        if (_map == null) return;
        int fx = (int)Math.Floor(px / MapTile.Size), fz = (int)Math.Floor(pz / MapTile.Size);
        while (_done.TryDequeue(out var d))
        {
            if (d.Revision != _revision) continue;
            _pending.Remove(d.Key);
            if (Math.Abs(d.Tx - fx) > Radius + 1 || Math.Abs(d.Tz - fz) > Radius + 1) continue;
            Node3D? node = null;
            if (!d.Mesh.Empty)
            {
                node = new Node3D { Name = $"FarRoads{d.Tx}_{d.Tz}" };
                var mesh = d.Mesh.ToMesh(_material);
                // the shader widens and moves the ribbons: a generous box so culling never drops one early
                var box = mesh.GetAabb();
                mesh.CustomAabb = box.Grow(60f);
                node.AddChild(new MeshInstance3D { Mesh = mesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
                AddChild(node);
                WorldOrigin.Anchor(node, d.Tx * (double)MapTile.Size, d.Tz * (double)MapTile.Size);
            }
            _tiles[d.Key] = node;
        }
        for (int r = 0; r <= Radius; r++)   // nearest rings first
            for (int tz = fz - r; tz <= fz + r; tz++)
                for (int tx = fx - r; tx <= fx + r; tx++)
                {
                    if (Math.Max(Math.Abs(tx - fx), Math.Abs(tz - fz)) != r) continue;
                    long key = Key(tx, tz);
                    if (_tiles.ContainsKey(key) || _pending.Contains(key) || _pending.Count >= 2) continue;
                    if (!_map.Data.HasTile(tx, tz)) { _tiles[key] = null; continue; }
                    _pending.Add(key);
                    int ctx = tx, ctz = tz; var map = _map; long rev = _revision;
                    Task.Run(() =>
                    {
                        var mesh = new MeshData();
                        try { BuildTile(map, ctx, ctz, mesh); }
                        catch (Exception e) { Log.Error("FarRoads", $"Tile {ctx},{ctz} failed: {e}"); }
                        _done.Enqueue((key, ctx, ctz, mesh, rev));
                    });
                }
        _remove.Clear();
        foreach (var key in _tiles.Keys)
        {
            int tx = (int)(key >> 32), tz = (int)(uint)key;
            if (Math.Abs(tx - fx) > Radius + 1 || Math.Abs(tz - fz) > Radius + 1) _remove.Add(key);
        }
        foreach (var key in _remove) { _tiles[key]?.QueueFree(); _tiles.Remove(key); }
    }

    private static readonly Color Asphalt = new(0.16f, 0.165f, 0.17f), Gravel = new(0.42f, 0.38f, 0.31f), Ballast = new(0.19f, 0.175f, 0.16f);

    private static void BuildTile(MapWorld map, int tx, int tz, MeshData mesh)
    {
        var tile = map.Data.Tile(tx, tz);
        foreach (var road in tile.Roads)
        {
            if (road.IsTram || (road.Flags & RoadFlag.Tunnel) != 0) continue;
            float far = road.Cls switch
            {
                <= RoadClass.Tertiary or RoadClass.Link => 9500f,
                RoadClass.Unclassified or RoadClass.Residential => 3500f,
                RoadClass.Rail => 7000f,
                _ => 0f,
            };
            if (far <= 0f || road.X.Length < 2) continue;
            var col = road.IsRail ? Ballast : road.Unpaved ? Gravel : Asphalt;
            int n = road.X.Length, first = mesh.V.Count;
            for (int k = 0; k < n; k++)
            {
                int a = Math.Max(0, k - 1), b = Math.Min(n - 1, k + 1);
                var dir = new Vector2(road.X[b] - road.X[a], road.Z[b] - road.Z[a]);
                dir = dir.LengthSquared() > 1e-6f ? dir.Normalized() : Vector2.Right;
                var side = new Vector3(-dir.Y, 0, dir.X);
                var p = new Vector3(road.X[k], road.Y[k] + 0.15f, road.Z[k]);
                var uv2 = new Vector2(road.HalfWidthAt(k), far);
                mesh.Add(p, side, new Vector2(-1, road.Along[k]), uv2, col);
                mesh.Add(p, side, new Vector2(1, road.Along[k]), uv2, col);
            }
            for (int k = 0; k + 1 < n; k++)
            {
                int i = first + 2 * k;
                mesh.I.Add(i); mesh.I.Add(i + 2); mesh.I.Add(i + 1);
                mesh.I.Add(i + 1); mesh.I.Add(i + 2); mesh.I.Add(i + 3);
            }
        }
    }
}
