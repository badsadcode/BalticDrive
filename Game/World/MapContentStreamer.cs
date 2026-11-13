using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BalticDrive.Core;
using BalticDrive.Map;
using Godot;

namespace BalticDrive.World;

/// <summary>
/// Roads, buildings and street furniture around the focus, in 128 m cells meshed on worker threads by Dead Miles'
/// <see cref="MapMesher"/>. Cells near the focus are built at full detail with collision; farther ones without
/// collision and with less detail; a cell is rebuilt when its detail band changes. The radius stays inside the
/// terrain levels whose ground follows the road beds (<see cref="TerrainStreamer.RoadGroundReach"/>).
/// Buildings taller than <see cref="MapMesher.TallBuilding"/> come from <see cref="MapSkyline"/> instead.
/// </summary>
public partial class MapContentStreamer : Node3D
{
    public const float CellSize = 128f;
    public float Radius = 1000f;
    public float CollisionRadius = 320f;
    public double CommitBudgetMs = 4;

    private MapWorld? _world;
    private long _revision;
    private readonly int _maxJobs = Math.Max(2, System.Environment.ProcessorCount / 2);
    private readonly Dictionary<(long, long), (Node3D Node, int Lod)> _cells = new();
    private readonly Dictionary<(long, long), (Task<MapChunkGeometry> Task, int Lod, long Revision)> _jobs = new();
    private readonly Dictionary<(long, long), int> _want = new();
    private Material _road = null!, _building = null!, _furniture = null!, _barrier = null!;
    private ArrayMesh _lamp = null!, _pole = null!, _pylon = null!;
    private MapSkyline? _skyline;
    private Node3D _skyRoot = null!;

    public int Cells => _cells.Count;
    public int Pending => _jobs.Count;

    public override void _Ready()
    {
        _road = GD.Load<Material>("res://Shaders/road.tres");
        _building = GD.Load<Material>("res://Shaders/building.tres");
        _barrier = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/barrier.gdshader") };
        _furniture = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.6f, Metallic = 0.3f };
        _lamp = MapMesher.LampMesh(_furniture);
        _pole = MapMesher.PoleMesh(_furniture);
        _pylon = MapMesher.PylonMesh(_furniture);
        _skyRoot = new Node3D { Name = "Skyline" };
        AddChild(_skyRoot);
    }

    public void Configure(MapWorld world)
    {
        _revision++;
        foreach (var (node, _) in _cells.Values) node.QueueFree();
        _cells.Clear();
        foreach (Node n in _skyRoot.GetChildren()) n.QueueFree();
        _world = world;
        _skyline = new MapSkyline(world, _skyRoot, _building);
    }

    private int LodFor(double d) => d < CollisionRadius ? 0 : d < 600 ? 1 : 2;

    public void Update(double px, double pz)
    {
        if (_world == null) return;
        _skyline?.Update(px, pz);
        // Wanted cells and their detail.
        _want.Clear();
        long cx0 = (long)Math.Floor((px - Radius) / CellSize), cx1 = (long)Math.Floor((px + Radius) / CellSize);
        long cz0 = (long)Math.Floor((pz - Radius) / CellSize), cz1 = (long)Math.Floor((pz + Radius) / CellSize);
        for (long cz = cz0; cz <= cz1; cz++)
            for (long cx = cx0; cx <= cx1; cx++)
            {
                double d = DistanceTo(cx, cz, px, pz);
                if (d <= Radius) _want[(cx, cz)] = LodFor(d);
            }

        // Commit finished cells.
        var timer = System.Diagnostics.Stopwatch.StartNew();
        foreach (var key in _jobs.Where(j => j.Value.Task.IsCompleted).Select(j => j.Key).ToList())
        {
            if (timer.Elapsed.TotalMilliseconds > CommitBudgetMs) break;
            var (task, lod, rev) = _jobs[key];
            _jobs.Remove(key);
            if (task.IsFaulted) { Log.Error("MapContent", task.Exception?.ToString() ?? "cell failed"); continue; }
            if (rev != _revision || !_want.TryGetValue(key, out int want) || want != lod) continue;
            if (_cells.Remove(key, out var old)) old.Node.QueueFree();
            var node = Commit(task.Result, key);
            AddChild(node);
            _cells[key] = (node, lod);
        }

        // Schedule: missing or wrong detail, nearest first.
        if (_jobs.Count < _maxJobs)
        {
            var world = _world; long revision = _revision;
            foreach (var (key, lod) in _want
                         .Where(w => (!_cells.TryGetValue(w.Key, out var c) || c.Lod != w.Value) && !_jobs.ContainsKey(w.Key))
                         .OrderBy(w => w.Value).ThenBy(w => DistanceTo(w.Key.Item1, w.Key.Item2, px, pz))
                         .Take(_maxJobs - _jobs.Count).ToList())
            {
                long cx = key.Item1, cz = key.Item2;
                _jobs[key] = (Task.Run(() => Build(world, cx, cz, lod)), lod, revision);
            }
        }

        // Drop cells out of range.
        foreach (var key in _cells.Keys.Where(k => !_want.ContainsKey(k)).ToList())
        {
            _cells[key].Node.QueueFree();
            _cells.Remove(key);
        }
    }

    private static double DistanceTo(long cx, long cz, double px, double pz)
    {
        double x0 = cx * (double)CellSize, z0 = cz * (double)CellSize;
        double dx = Math.Max(0, Math.Max(x0 - px, px - (x0 + CellSize)));
        double dz = Math.Max(0, Math.Max(z0 - pz, pz - (z0 + CellSize)));
        return Math.Sqrt(dx * dx + dz * dz);
    }

    [ThreadStatic] private static MapContext? _ctx;

    private static MapChunkGeometry Build(MapWorld world, long cx, long cz, int lod)
    {
        var ctx = _ctx;
        if (ctx == null || ctx.World != world) _ctx = ctx = new MapContext(world);
        ctx.Reset(cx * (double)CellSize, cz * (double)CellSize, 0, 0, CellSize, CellSize);
        var geo = new MapChunkGeometry();
        MapMesher.Build(ctx, CellSize, lod, geo, lod == 0);
        return geo;
    }

    private Node3D Commit(MapChunkGeometry geo, (long, long) key)
    {
        var node = new Node3D { Name = $"C{key.Item1}_{key.Item2}" };
        WorldOrigin.Anchor(node, key.Item1 * (double)CellSize, key.Item2 * (double)CellSize);
        if (!geo.Roads.Empty) node.AddChild(new MeshInstance3D { Mesh = geo.Roads.ToMesh(_road), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
        if (!geo.Buildings.Empty) node.AddChild(new MeshInstance3D { Mesh = geo.Buildings.ToMesh(_building) });
        if (!geo.Barriers.Empty) node.AddChild(new MeshInstance3D { Mesh = geo.Barriers.ToMesh(_barrier) });
        Bodies.AddFaces(node, geo.PavedFaces, "paved", false, Bodies.Roads);
        Bodies.AddFaces(node, geo.LooseFaces, "loose", false, Bodies.Roads);
        Bodies.AddFaces(node, geo.BuildingFaces, "hard", true, Bodies.Buildings);
        AddInstances(node, _lamp, geo.Lamps, true);
        AddInstances(node, _pole, geo.Poles, false);
        AddInstances(node, _pylon, geo.Pylons, true);
        if (geo.Posts is { Count: > 0 } posts)
        {
            var body = new StaticBody3D { CollisionLayer = Bodies.Props, CollisionMask = 0 };
            foreach (var p in posts)
                body.AddChild(new CollisionShape3D
                {
                    Shape = new CylinderShape3D { Radius = p.W, Height = 8f },
                    Position = new Vector3(p.X, p.Y + 4f, p.Z),
                });
            node.AddChild(body);
        }
        return node;
    }

    private static void AddInstances(Node3D node, Mesh mesh, List<float>? buffer, bool shadows)
    {
        if (buffer == null || buffer.Count == 0) return;
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = mesh, InstanceCount = buffer.Count / 16 };
        mm.Buffer = buffer.ToArray();
        node.AddChild(new MultiMeshInstance3D
        {
            Multimesh = mm,
            CastShadow = shadows ? GeometryInstance3D.ShadowCastingSetting.On : GeometryInstance3D.ShadowCastingSetting.Off,
        });
    }
}
