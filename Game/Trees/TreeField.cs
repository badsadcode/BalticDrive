using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using BalticDrive.Map;
using BalticDrive.Core;
using BalticDrive.World;
using Godot;

namespace BalticDrive.Trees;

/// <summary>
/// The trees around the camera (ported from Thornwake's TreeField, round 30; D22), in world-aligned cells:
/// * near cells (128 m): 3D trees, one MultiMesh per species and mesh part. Close cells carry the detailed model
///   (LOD1, shadows); Godot and the shaders hand over to the lighter model (LOD2) across a dithered band;
/// * far cells (1024 m): the same trees as impostors (pictures of each species from 8 sides, baked at start-up).
/// The hand-overs are dithered on circles around the camera (TreeFade.gdshaderinc), so cell edges never show.
/// Trees come from <see cref="TreePlacement"/>: the same tree whichever cell asks.
/// </summary>
public sealed partial class TreeField : Node3D
{
    private const double NearCell = 128, FarCell = 1024;
    private const float Fade = 40, LodBand = 20;
    private const int Stride = 16;   // floats per 3D instance: transform 12 + custom 4 (x = hand-over role)

    public float NearRange = 200, DetailRange = 700, ImpostorRange = 3000;
    public int Pending => _jobs.Count;
    public int Missing { get; private set; }
    public long Instances3D { get; private set; }
    public long ImpostorInstances { get; private set; }

    private readonly TreeCatalog _catalog = new();
    private readonly TreeImpostors _impostors = new();
    private MapWorld? _world;
    private long _revision;
    private readonly Dictionary<(bool Far, long X, long Z), Cell> _cells = new();
    private readonly Dictionary<(bool Far, long X, long Z), (Task<CellData> Task, long Revision)> _jobs = new();
    private readonly HashSet<(bool Far, long X, long Z)> _desired = new();
    private readonly Dictionary<(int Species, int Lod), (Mesh Mesh, Transform3D Transform)[]> _parts = new();
    private readonly int _maxJobs = Math.Max(2, System.Environment.ProcessorCount / 3);

    private sealed class Cell { public required Node3D Node; public bool Near; public long Revision; public long Count; }

    private sealed class CellData
    {
        public (bool Far, long X, long Z) Key;
        public bool Near;
        public List<(int Species, int Lod, int Part, float[] Buffer, int Count)> Batches = new();
        public float[]? Impostors;
        public int ImpostorCount;
        public long Trees;
        /// <summary>Near cells: trunks for the car to hit (x, base y, z relative to the cell centre; w = radius).</summary>
        public List<Vector4>? Trunks;
    }

    public TreeField() { Name = "Trees"; }

    public void Configure(MapWorld world)
    {
        Clear();
        _world = world;
        if (_parts.Count == 0)
            for (int s = 0; s < TreeSpecies.Names.Length; s++)
                foreach (int lod in new[] { 1, 2 })
                    _parts[(s, lod)] = _catalog.PartsOf(s, lod).ToArray();
        _impostors.Bake(this, _catalog);
        RenderingServer.GlobalShaderParameterSet("tree_ranges", new Vector4(DetailRange, ImpostorRange, Fade, 1));
        RenderingServer.GlobalShaderParameterSet("tree_lod", new Vector4(NearRange, LodBand, 0, 0));
    }

    public void Clear()
    {
        _revision++;
        foreach (var c in _cells.Values) { RemoveChild(c.Node); c.Node.QueueFree(); }
        _cells.Clear();
        _desired.Clear();
        Instances3D = ImpostorInstances = 0;
    }

    private static double Size(bool far) => far ? FarCell : NearCell;

    private static double Dist((bool Far, long X, long Z) k, double px, double pz)
    {
        double s = Size(k.Far), cx = (k.X + 0.5) * s, cz = (k.Z + 0.5) * s;
        double dx = Math.Max(0, Math.Abs(cx - px) - s / 2), dz = Math.Max(0, Math.Abs(cz - pz) - s / 2);
        return Math.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>Per frame: <paramref name="px"/>, <paramref name="pz"/> the focus in world metres, <paramref name="camera"/>
    /// the camera's scene position (the hand-overs are measured from it).</summary>
    public void Update(double px, double pz, Vector3 camera)
    {
        RenderingServer.GlobalShaderParameterSet("tree_camera", new Vector4(camera.X, camera.Y, camera.Z, 0));
        if (_world == null || !_impostors.Ready) return;
        _desired.Clear();
        AddCells(false, px, pz, 0, DetailRange + Fade);
        AddCells(true, px, pz, DetailRange - Fade, ImpostorRange + Fade);

        var timer = Stopwatch.StartNew();
        foreach (var key in _jobs.Where(j => j.Value.Task.IsCompleted).Select(j => j.Key).OrderBy(k => Dist(k, px, pz)).ToList())
        {
            if (timer.Elapsed.TotalMilliseconds > 4) break;
            var (task, revision) = _jobs[key];
            _jobs.Remove(key);
            if (task.IsFaulted) { GD.PushError(task.Exception?.ToString() ?? "tree cell failed"); continue; }
            if (revision != _revision || !_desired.Contains(key)) continue;
            Commit(task.Result, revision);
        }

        var missing = _desired.Where(k => !_cells.TryGetValue(k, out var c) || c.Revision != _revision
            || (!k.Far && !c.Near && Dist(k, px, pz) < NearRange + Fade)).ToList();
        Missing = missing.Count;
        if (_jobs.Count < _maxJobs)
        {
            var world = _world; long rev = _revision;
            double nearRange = NearRange + Fade + NearCell;
            foreach (var key in missing.Where(k => !_jobs.ContainsKey(k)).OrderBy(k => k.Far).ThenBy(k => Dist(k, px, pz)).Take(_maxJobs - _jobs.Count))
            {
                bool near = !key.Far && Dist(key, px, pz) < nearRange;
                _jobs[key] = (Task.Run(() => Build(world, key, near)), rev);
            }
        }

        if (Missing == 0)
            foreach (var key in _cells.Keys.Where(k => !_desired.Contains(k)).ToList())
            {
                var c = _cells[key];
                if (key.Far) ImpostorInstances -= c.Count; else Instances3D -= c.Count;
                RemoveChild(c.Node); c.Node.QueueFree(); _cells.Remove(key);
            }
    }

    private void AddCells(bool far, double px, double pz, double inner, double outer)
    {
        double size = Size(far);
        long x0 = (long)Math.Floor((px - outer) / size), x1 = (long)Math.Floor((px + outer) / size);
        long z0 = (long)Math.Floor((pz - outer) / size), z1 = (long)Math.Floor((pz + outer) / size);
        for (long z = z0; z <= z1; z++)
            for (long x = x0; x <= x1; x++)
            {
                var key = (far, x, z);
                double near = Dist(key, px, pz);
                double cx = (x + 0.5) * size, cz = (z + 0.5) * size;
                double farthest = Math.Sqrt(Math.Pow(Math.Abs(cx - px) + size / 2, 2) + Math.Pow(Math.Abs(cz - pz) + size / 2, 2));
                if (near <= outer && farthest >= inner) _desired.Add(key);
            }
    }

    /// <summary>Trunk radius at scale 1 by species (pines and spruces stout, birches slim).</summary>
    private static float TrunkRadius(int species) => species switch
    {
        <= TreeSpecies.SpruceB => 0.28f,
        <= TreeSpecies.OakC => 0.4f,
        TreeSpecies.Willow => 0.35f,
        TreeSpecies.Poplar or TreeSpecies.MapleAutumn => 0.3f,
        _ => 0.18f,
    };

    private readonly Dictionary<int, CylinderShape3D> _trunkShapes = new();

    private CylinderShape3D Trunk(float radius)
    {
        int k = Math.Clamp((int)MathF.Round(radius * 20f), 2, 20);   // shared shapes in 5 cm steps
        if (!_trunkShapes.TryGetValue(k, out var shape)) _trunkShapes[k] = shape = new CylinderShape3D { Radius = k / 20f, Height = 6f };
        return shape;
    }

    [ThreadStatic] private static MapContext? _ctx;

    /// <summary>Worker thread: the cell's trees and their instance buffers (positions relative to the cell centre).</summary>
    private CellData Build(MapWorld world, (bool Far, long X, long Z) key, bool near)
    {
        double size = Size(key.Far), x0 = key.X * size, z0 = key.Z * size;
        MapContext? ctx = null;
        if (!key.Far)
        {
            ctx = _ctx;
            if (ctx == null || ctx.World != world) _ctx = ctx = new MapContext(world);
            ctx.Reset(x0, z0, 0, 0, (float)size, (float)size, withPoints: false);
        }
        var trees = TreePlacement.In(world, x0, z0, size, ctx);
        double cx = x0 + size / 2, cz = z0 + size / 2;
        var data = new CellData { Key = key, Near = near, Trees = trees.Count };
        Transform3D Xf(in TreePlacement.Tree t) =>
            new(new Basis(Vector3.Up, t.Yaw).Scaled(Vector3.One * t.Scale), new Vector3((float)(t.X - cx), t.Y, (float)(t.Z - cz)));
        if (!key.Far)
        {
            if (near)
            {
                data.Trunks = new List<Vector4>(trees.Count);
                foreach (var t in trees)
                    data.Trunks.Add(new Vector4((float)(t.X - cx), t.Y, (float)(t.Z - cz), TrunkRadius(t.Species) * t.Scale));
            }
            foreach (var group in trees.GroupBy(t => t.Species))
            {
                var list = group.ToArray();
                foreach (int lod in near ? new[] { 1, 2 } : new[] { 2 })
                {
                    var parts = _parts[(group.Key, lod)];
                    // hand-over role (TreeFade.gdshaderinc): 1 = detailed model, 2 = the lighter one in a near cell, 0 = none
                    float role = lod == 1 ? 1 : near ? 2 : 0;
                    for (int p = 0; p < parts.Length; p++)
                    {
                        var buffer = new float[list.Length * Stride];
                        for (int i = 0; i < list.Length; i++)
                        {
                            MultiMeshFill.Put(buffer, i * Stride, Xf(list[i]) * parts[p].Transform);
                            buffer[i * Stride + 12] = role;
                        }
                        data.Batches.Add((group.Key, lod, p, buffer, list.Length));
                    }
                }
            }
        }
        else
        {
            data.Impostors = new float[trees.Count * 16];
            for (int i = 0; i < trees.Count; i++)
            {
                MultiMeshFill.Put(data.Impostors, i * 16, Xf(trees[i]));
                MultiMeshFill.Put(data.Impostors, i * 16 + 12, new Color(trees[i].Species, 0, 0, 0));
            }
            data.ImpostorCount = trees.Count;
        }
        return data;
    }

    private void Commit(CellData data, long revision)
    {
        var key = data.Key;
        if (_cells.Remove(key, out var old))
        {
            if (key.Far) ImpostorInstances -= old.Count; else Instances3D -= old.Count;
            RemoveChild(old.Node); old.Node.QueueFree();
        }
        double size = Size(key.Far);
        var node = new Node3D { Name = $"T{(key.Far ? "F" : "N")}{key.X}_{key.Z}" };
        AddChild(node);
        WorldOrigin.Anchor(node, (key.X + 0.5) * size, (key.Z + 0.5) * size);
        float diagonal = (float)(size * 0.5 * Math.Sqrt(2));
        float lodPad = Mathf.Sqrt(diagonal * diagonal + 30 * 30);
        foreach (var (species, lod, part, buffer, count) in data.Batches)
        {
            var mesh = _parts[(species, lod)][part].Mesh;
            var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = mesh };
            multi.InstanceCount = count;
            multi.Buffer = buffer;
            var mmi = new MultiMeshInstance3D { Multimesh = multi, Layers = lod == 1 ? TreeLayers.TreesNear : TreeLayers.TreesFar };
            if (lod == 1)
            {
                mmi.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
                mmi.VisibilityRangeEnd = NearRange + LodBand + lodPad;
            }
            else
            {
                mmi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                if (data.Near) mmi.VisibilityRangeBegin = Math.Max(0, NearRange - LodBand - lodPad);
                mmi.VisibilityRangeEnd = DetailRange + Fade + diagonal;
            }
            node.AddChild(mmi);
        }
        if (data.Impostors != null && data.ImpostorCount > 0)
        {
            var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = _impostors.Quad };
            multi.InstanceCount = data.ImpostorCount;
            multi.Buffer = data.Impostors;
            node.AddChild(new MultiMeshInstance3D
            {
                Multimesh = multi, MaterialOverride = _impostors.Material, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Layers = TreeLayers.Impostors, VisibilityRangeEnd = ImpostorRange + Fade + diagonal,
            });
        }
        if (data.Trunks is { Count: > 0 } trunks)
        {
            var body = new StaticBody3D { CollisionLayer = Bodies.Props, CollisionMask = 0 };
            foreach (var t in trunks)
                body.AddChild(new CollisionShape3D { Shape = Trunk(t.W), Position = new Vector3(t.X, t.Y + 3f, t.Z) });
            node.AddChild(body);
        }
        _cells[key] = new Cell { Node = node, Near = data.Near, Revision = revision, Count = data.Trees };
        if (key.Far) ImpostorInstances += data.Trees; else Instances3D += data.Trees;
    }
}
