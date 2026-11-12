using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BalticDrive.Core;
using BalticDrive.Map;
using Godot;

namespace BalticDrive.World;

/// <summary>
/// Streams the ground as nested LOD rings — the scheme of Thornwake's WorldStreamer: level 0 is 64 m tiles at 2 m
/// spacing around the focus; each coarser level doubles the tile size and covers the ring outside the finer one, out
/// to level <see cref="Levels"/>-1. Tiles are built on worker threads and committed on the main thread within a time
/// budget; level-0 collision is added afterwards in small pieces, nearest first, so no frame stalls. Old tiles stay
/// until everything wanted is on screen, so moving never opens holes.
/// </summary>
public partial class TerrainStreamer : Node3D
{
    public const int TopRadius = 2;
    /// <summary>The hole each level leaves for the next finer one (± tiles): a finer level covers (4k+2)² children.</summary>
    public int Detail = 2;
    /// <summary>LOD levels: 7 → ~10 km, 8 → ~20 km, 9 → ~40 km view distance.</summary>
    public int Levels = 8;
    public double CommitBudgetMs = 6, CollisionBudgetMs = 8;
    private const int GroundPieceTriangles = 1024;

    public int Resident => _tiles.Count;
    public int Pending => _jobs.Count;
    public int Missing { get; private set; }
    public double FrameCommitMs { get; private set; }
    public double MaxCommitMs { get; private set; }
    public long Triangles { get; private set; }
    public bool Settled => _world != null && Missing == 0 && _jobs.Count == 0;
    public int PendingCollision => _pendingCollision.Count;

    private MapWorld? _world;
    private long _revision;
    private readonly int _maxJobs = Math.Max(2, System.Environment.ProcessorCount - 2);
    private readonly Dictionary<TileKey, Node3D> _tiles = new();
    private readonly Dictionary<TileKey, long> _tileTriangles = new();
    private readonly Dictionary<TileKey, (Task<TerrainTileData> Task, long Revision)> _jobs = new();
    private readonly HashSet<TileKey> _desired = new();
    private readonly HashSet<TileKey> _failed = new();
    private readonly Dictionary<TileKey, PendingCollisionTile> _pendingCollision = new();
    private Material _terrain = null!, _water = null!;

    private sealed class PendingCollisionTile(Node3D root, Vector3[] faces)
    {
        public readonly Node3D Root = root;
        public readonly Vector3[] Faces = faces;
        public StaticBody3D? Body;
        public int Next;
    }

    public override void _Ready()
    {
        _terrain = GD.Load<Material>("res://Shaders/terrain.tres");
        _water = GD.Load<Material>("res://Shaders/water.tres");
    }

    public void Configure(MapWorld world)
    {
        _revision++;
        Clear();
        _world = world;
    }

    private void Clear()
    {
        foreach (var node in _tiles.Values) { RemoveChild(node); node.QueueFree(); }
        foreach (var p in _pendingCollision.Values) if (p.Body != null && !p.Body.IsInsideTree()) p.Body.Free();
        _pendingCollision.Clear();
        _tiles.Clear(); _tileTriangles.Clear(); _failed.Clear(); _desired.Clear();
        Triangles = 0; MaxCommitMs = 0;
    }

    /// <summary>True once a collision tile under the world point is resident.</summary>
    public bool HasGround(double x, double z)
    {
        var key = new TileKey(0, (long)Math.Floor(x / TerrainBuilder.BaseTile), (long)Math.Floor(z / TerrainBuilder.BaseTile));
        return _tiles.ContainsKey(key) && !_pendingCollision.ContainsKey(key);
    }

    /// <summary>Focus in world coordinates.</summary>
    public void Update(double px, double pz)
    {
        if (_world == null) return;
        Desired(px, pz);
        var timer = Stopwatch.StartNew();

        // Commit finished builds, nearest levels first, within the budget.
        foreach (var key in _jobs.Where(j => j.Value.Task.IsCompleted).Select(j => j.Key).OrderBy(k => k.Level).ToList())
        {
            if (timer.Elapsed.TotalMilliseconds > CommitBudgetMs) break;
            var (task, revision) = _jobs[key];
            _jobs.Remove(key);
            if (task.IsFaulted) { _failed.Add(key); Log.Error("Terrain", task.Exception?.ToString() ?? "tile build failed"); continue; }
            if (revision != _revision || !_desired.Contains(key)) continue;
            var commit = Stopwatch.StartNew();
            if (_tiles.Remove(key, out var old)) { DropCollision(key); RemoveChild(old); old.QueueFree(); Triangles -= _tileTriangles[key]; }
            var node = Commit(task.Result);
            AddChild(node);
            _tiles[key] = node;
            long tris = task.Result.Indices.Length / 3;
            _tileTriangles[key] = tris; Triangles += tris;
            MaxCommitMs = Math.Max(MaxCommitMs, commit.Elapsed.TotalMilliseconds);
        }

        // Collision for committed level-0 tiles: at least one piece per frame, more while the budget lasts.
        bool did = false;
        foreach (var key in _pendingCollision.Keys.OrderBy(k => Distance(k, px, pz)).ToList())
        {
            var p = _pendingCollision[key];
            if (!IsInstanceValid(p.Root) || !_tiles.TryGetValue(key, out var current) || current != p.Root) { DropCollision(key); continue; }
            int pieces = (p.Faces.Length / 3 + GroundPieceTriangles - 1) / GroundPieceTriangles;
            bool over = false;
            while (p.Next < pieces)
            {
                if (did && timer.Elapsed.TotalMilliseconds > CollisionBudgetMs) { over = true; break; }
                did = true;
                p.Body ??= new StaticBody3D { CollisionLayer = Bodies.Ground, CollisionMask = 0 };
                int start = p.Next * GroundPieceTriangles * 3, count = Math.Min(GroundPieceTriangles * 3, p.Faces.Length - start);
                p.Body.AddChild(new CollisionShape3D { Shape = new ConcavePolygonShape3D { Data = p.Faces[start..(start + count)], BackfaceCollision = true } });
                p.Next++;
            }
            if (over) break;
            p.Body!.SetMeta("surface", "ground");
            p.Root.AddChild(p.Body);
            _pendingCollision.Remove(key);
            if (timer.Elapsed.TotalMilliseconds > CollisionBudgetMs) break;
        }
        FrameCommitMs = timer.Elapsed.TotalMilliseconds;

        // Schedule missing tiles: finest level first, then nearest.
        var missing = _desired.Where(k => !_tiles.ContainsKey(k) && !_failed.Contains(k)).ToList();
        Missing = missing.Count;
        if (_jobs.Count < _maxJobs)
        {
            var world = _world; long revision = _revision;
            foreach (var key in missing.Where(k => !_jobs.ContainsKey(k)).OrderBy(k => k.Level).ThenBy(k => Distance(k, px, pz)).Take(_maxJobs - _jobs.Count))
                _jobs[key] = (Task.Run(() => TerrainBuilder.Build(world, key)), revision);
        }

        // Drop tiles no longer wanted once everything wanted is on screen (no holes while moving).
        if (Missing == 0)
            foreach (var key in _tiles.Keys.Where(k => !_desired.Contains(k)).ToList())
            {
                var node = _tiles[key]; DropCollision(key); RemoveChild(node); node.QueueFree();
                _tiles.Remove(key); Triangles -= _tileTriangles[key]; _tileTriangles.Remove(key);
            }
    }

    private void DropCollision(TileKey key)
    {
        if (!_pendingCollision.Remove(key, out var p)) return;
        if (p.Body != null && !p.Body.IsInsideTree()) p.Body.Free();
    }

    private static double Distance(TileKey k, double px, double pz)
    {
        double cx = k.MinX + k.Size / 2, cz = k.MinZ + k.Size / 2;
        return Math.Max(Math.Abs(cx - px), Math.Abs(cz - pz));
    }

    /// <summary>
    /// Nested rings. Top level: (2R+1)² tiles around the focus. Every level below fills the hole around the focus's
    /// tile of the level above with its children; level 0 keeps its whole region.
    /// </summary>
    private void Desired(double px, double pz)
    {
        _desired.Clear();
        int top = Levels - 1, k = Math.Clamp(Detail, 1, 4);
        double size = TerrainBuilder.BaseTile * (1L << top);
        long cx = (long)Math.Floor(px / size), cz = (long)Math.Floor(pz / size);
        long x0 = cx - TopRadius, x1 = cx + TopRadius, z0 = cz - TopRadius, z1 = cz + TopRadius;
        for (int level = top; level >= 0; level--)
        {
            size = TerrainBuilder.BaseTile * (1L << level);
            cx = (long)Math.Floor(px / size); cz = (long)Math.Floor(pz / size);
            for (long z = z0; z <= z1; z++)
                for (long x = x0; x <= x1; x++)
                {
                    bool hole = level > 0 && Math.Abs(x - cx) <= k && Math.Abs(z - cz) <= k;
                    if (!hole) _desired.Add(new TileKey(level, x, z));
                }
            x0 = 2 * (cx - k); x1 = 2 * (cx + k) + 1; z0 = 2 * (cz - k); z1 = 2 * (cz + k) + 1;
        }
    }

    /// <summary>How far (m) from the focus the road-shaped ground reaches in every direction, at least.</summary>
    public double RoadGroundReach => Math.Clamp(Detail, 1, 4) * TerrainBuilder.BaseTile * (1 << (TerrainBuilder.RoadLevels + 1));

    private Node3D Commit(TerrainTileData data)
    {
        var key = data.Key;
        var root = new Node3D { Name = $"T{key.Level}_{key.X}_{key.Z}" };
        WorldOrigin.Anchor(root, key.MinX, key.MinZ);
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = data.Normals;
        arrays[(int)Mesh.ArrayType.Color] = data.Colors;
        arrays[(int)Mesh.ArrayType.Index] = data.Indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        root.AddChild(new MeshInstance3D
        {
            Mesh = mesh, MaterialOverride = _terrain,
            CastShadow = key.Level <= 2 ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
        });
        if (data.WaterVertices is { } wv && data.WaterIndices is { } wi)
        {
            var w = new Godot.Collections.Array();
            w.Resize((int)Mesh.ArrayType.Max);
            w[(int)Mesh.ArrayType.Vertex] = wv;
            var up = new Vector3[wv.Length];
            Array.Fill(up, Vector3.Up);
            w[(int)Mesh.ArrayType.Normal] = up;
            if (data.WaterColors != null) w[(int)Mesh.ArrayType.Color] = data.WaterColors;
            w[(int)Mesh.ArrayType.Index] = wi;
            var wm = new ArrayMesh();
            wm.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, w);
            root.AddChild(new MeshInstance3D { Name = "Water", Mesh = wm, MaterialOverride = _water, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        }
        if (data.CollisionFaces != null) _pendingCollision[key] = new PendingCollisionTile(root, data.CollisionFaces);
        return root;
    }
}
