using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

using BalticDrive.Core;
using BalticDrive.World;

namespace BalticDrive.Map;

/// <summary>
/// Tall buildings (tower blocks, church spires, the Palace of Culture) are landmarks: they must be
/// visible from much further away than the ordinary chunk radius. This layer draws every building
/// above <see cref="MapMesher.TallBuilding"/> for whole map tiles around the player, with collision,
/// and the chunk mesher leaves those buildings out.
/// </summary>
public sealed class MapSkyline
{
    private const int Radius = 2;   // tiles: about 3 km of skyline in every direction

    private readonly MapWorld _map;
    private readonly Node3D _root;
    private readonly Material _material, _nearCut;
    /// <summary>Within this distance of the camera the content streamer draws the buildings that it takes over from the
    /// skyline (tall blocks of flats under BadBuilding, D32), so the skyline hides its copy of them there.</summary>
    public const float NearCut = 950f;
    private readonly Dictionary<long, Node3D?> _tiles = new();
    private readonly HashSet<long> _pending = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<(long key, int tx, int tz, MeshData mesh, MeshData near, List<Vector3> faces)> _done = new();
    private readonly List<long> _remove = new();

    public MapSkyline(MapWorld map, Node3D root, Material material)
    {
        _map = map; _root = root; _material = material;
        _nearCut = (Material)material.Duplicate();
        if (_nearCut is ShaderMaterial sm) sm.SetShaderParameter("near_cut", NearCut);
    }

    private static long Key(int tx, int tz) => ((long)tx << 32) ^ (uint)tz;

    /// <summary>Focus in world coordinates (metres, doubles).</summary>
    public void Update(double px, double pz)
    {
        int fx = (int)Math.Floor(px / MapTile.Size), fz = (int)Math.Floor(pz / MapTile.Size);
        while (_done.TryDequeue(out var d))
        {
            _pending.Remove(d.key);
            if (Math.Abs(d.tx - fx) > Radius + 1 || Math.Abs(d.tz - fz) > Radius + 1) continue;
            Node3D? node = null;
            if (!d.mesh.Empty || !d.near.Empty)
            {
                node = new Node3D { Name = $"Skyline{d.tx}_{d.tz}" };
                if (!d.mesh.Empty) node.AddChild(new MeshInstance3D { Mesh = d.mesh.ToMesh(_material) });
                // no shadows from the far copies: in the shadow pass the distance cut would be measured from the light
                if (!d.near.Empty) node.AddChild(new MeshInstance3D { Mesh = d.near.ToMesh(_nearCut), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
                Bodies.AddFaces(node, d.faces, "hard", true, Bodies.Buildings);
                _root.AddChild(node);
                WorldOrigin.Anchor(node, d.tx * (double)MapTile.Size, d.tz * (double)MapTile.Size);
            }
            _tiles[d.key] = node;
        }
        for (int tz = fz - Radius; tz <= fz + Radius; tz++)
        {
            for (int tx = fx - Radius; tx <= fx + Radius; tx++)
            {
                long key = Key(tx, tz);
                if (_tiles.ContainsKey(key) || _pending.Contains(key) || _pending.Count >= 2) continue;
                if (!_map.Data.HasTile(tx, tz)) { _tiles[key] = null; continue; }
                _pending.Add(key);
                int ctx = tx, ctz = tz;
                var map = _map;
                Task.Run(() =>
                {
                    var mesh = new MeshData();
                    var near = new MeshData();
                    var faces = new List<Vector3>();
                    try { BuildTile(map, ctx, ctz, mesh, near, faces); }
                    catch (Exception e) { Log.Error("MapSkyline", $"Tile {ctx},{ctz} failed: {e}"); }
                    _done.Enqueue((key, ctx, ctz, mesh, near, faces));
                });
            }
        }
        _remove.Clear();
        foreach (var key in _tiles.Keys)
        {
            int tx = (int)(key >> 32), tz = (int)(uint)key;
            if (Math.Abs(tx - fx) > Radius + 1 || Math.Abs(tz - fz) > Radius + 1) _remove.Add(key);
        }
        foreach (var key in _remove) { _tiles[key]?.QueueFree(); _tiles.Remove(key); }
    }

    private static void BuildTile(MapWorld map, int tx, int tz, MeshData mesh, MeshData near, List<Vector3> faces)
    {
        var tile = map.Data.Tile(tx, tz);
        MapContext? ctx = null;
        foreach (var b in tile.Buildings)
        {
            if (b.Ghost || b.Height <= MapMesher.TallBuilding) continue;
            ctx ??= new MapContext(map);
            // Ground under the outline, queried in a frame centred on the building.
            ctx.Reset(tile.OriginX + b.Cx, tile.OriginZ + b.Cz, b.MinX - b.Cx, b.MinZ - b.Cz, b.MaxX - b.Cx, b.MaxZ - b.Cz);
            float gMin = float.MaxValue, gMax = float.MinValue;
            var rx = b.RX[0]; var rz = b.RZ[0];
            for (int k = 0; k < rx.Length; k++)
            {
                float h = ctx.Height(rx[k] - b.Cx, rz[k] - b.Cz, 0f, out _);
                gMin = MathF.Min(gMin, h); gMax = MathF.Max(gMax, h);
                gMin = MathF.Min(gMin, ctx.Ground(rx[k] - b.Cx, rz[k] - b.Cz));
            }
            // a building the near streamer draws itself: only far copies here, no collision (the near cells have it)
            bool handed = MapMesher.NearHandled(b);
            MapMesher.AddBuilding(handed ? near : mesh, handed ? null : faces, b, 0f, 0f, gMin, gMax, Hash.Of(tx, tz, b.Index, 92));
        }
    }
}
