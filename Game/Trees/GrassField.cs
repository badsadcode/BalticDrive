using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BalticDrive.Core;
using BalticDrive.Map;
using BalticDrive.World;
using Godot;

namespace BalticDrive.Trees;

/// <summary>
/// Grass blades around the camera (D59, Tom: "I know from Thornwake that this is a game changer"; his worry: it might
/// hit performance badly). Kept on a budget:
/// * only within <see cref="Range"/> (60 m) of the camera, in 24 m cells built on worker threads;
/// * one small clump mesh (14 bent blades, 42 triangles) drawn as a MultiMesh per cell, no shadows;
/// * density by land cover: thick in meadows, parks and wetland, thin in gardens and under trees, none on fields,
///   paving, water, in buildings or on roads and their verges;
/// * the far third of the range thins out per clump in the shader (Grass.gdshader), so there's no edge.
/// G toggles it; <c>--grass=0</c> starts without it.
/// </summary>
public sealed partial class GrassField : Node3D
{
    public const double CellSize = 24;
    public const float Range = 60f;
    private const float Spacing = 0.42f;     // metres between clumps at full density
    private const int Stride = 16;           // floats per instance: transform 12 + custom 4 (colour tint, random)

    public bool On = true;
    public long Instances { get; private set; }
    public int Pending => _jobs.Count;

    private MapWorld? _world;
    private long _revision;
    private Mesh? _clump;
    private ShaderMaterial? _material;
    private readonly Dictionary<(long X, long Z), (Node3D Node, int Count)> _cells = new();
    private readonly Dictionary<(long X, long Z), (Task<(float[] Buffer, int Count)> Task, long Revision)> _jobs = new();
    private readonly int _maxJobs = Math.Max(2, System.Environment.ProcessorCount / 4);

    public GrassField() { Name = "Grass"; }

    public void Configure(MapWorld world)
    {
        Clear();
        _world = world;
        _material ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/Grass.gdshader") };
        _material.SetShaderParameter("range", Range);
        _clump ??= MakeClump(_material);
    }

    public void Clear()
    {
        _revision++;
        foreach (var c in _cells.Values) { RemoveChild(c.Node); c.Node.QueueFree(); }
        _cells.Clear();
        Instances = 0;
    }

    /// <summary>Per frame, with the camera's world position (x, z) and height above the ground.</summary>
    public void Update(double cx, double cz, float above)
    {
        if (_world == null) return;
        Visible = On;
        // flying high: nothing near enough to see
        bool want = On && above < 25f;   // (from higher up blades are specks, not worth building)
        var desired = new HashSet<(long, long)>();
        if (want)
        {
            double reach = Range + CellSize * 0.71;
            long x0 = (long)Math.Floor((cx - reach) / CellSize), x1 = (long)Math.Floor((cx + reach) / CellSize);
            long z0 = (long)Math.Floor((cz - reach) / CellSize), z1 = (long)Math.Floor((cz + reach) / CellSize);
            for (long z = z0; z <= z1; z++)
                for (long x = x0; x <= x1; x++)
                {
                    double dx = (x + 0.5) * CellSize - cx, dz = (z + 0.5) * CellSize - cz;
                    if (Math.Sqrt(dx * dx + dz * dz) < reach) desired.Add((x, z));
                }
        }
        int commits = 0;
        foreach (var key in _jobs.Where(j => j.Value.Task.IsCompleted).Select(j => j.Key).ToList())
        {
            if (commits >= 2) break;   // a cell's upload is a few hundred KB: two a frame keeps frames even
            var (task, rev) = _jobs[key];
            _jobs.Remove(key);
            if (task.IsFaulted) { Log.Error("Grass", task.Exception?.InnerException?.Message ?? "cell failed"); continue; }
            if (rev != _revision || !desired.Contains(key)) continue;
            Commit(key, task.Result.Buffer, task.Result.Count);
            commits++;
        }
        foreach (var key in _cells.Keys.Where(k => !desired.Contains(k)).ToList())
        {
            var c = _cells[key];
            Instances -= c.Count;
            RemoveChild(c.Node); c.Node.QueueFree(); _cells.Remove(key);
        }
        if (_jobs.Count >= _maxJobs) return;
        var world = _world; long revision = _revision;
        foreach (var key in desired.Where(k => !_cells.ContainsKey(k) && !_jobs.ContainsKey(k))
                     .OrderBy(k => Math.Abs((k.Item1 + 0.5) * CellSize - cx) + Math.Abs((k.Item2 + 0.5) * CellSize - cz))
                     .Take(_maxJobs - _jobs.Count))
            _jobs[key] = (Task.Run(() => Build(world, key)), revision);
    }

    private void Commit((long X, long Z) key, float[] buffer, int count)
    {
        var node = new Node3D { Name = $"G{key.X}_{key.Z}" };
        AddChild(node);
        WorldOrigin.Anchor(node, key.X * CellSize, key.Z * CellSize);
        if (count > 0)
        {
            var multi = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseCustomData = true, Mesh = _clump };
            multi.InstanceCount = count;
            multi.Buffer = buffer;
            node.AddChild(new MultiMeshInstance3D
            {
                Multimesh = multi, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                VisibilityRangeEnd = Range + (float)CellSize,
            });
        }
        _cells[key] = (node, count);
        Instances += count;
    }

    /// <summary>How thick grass grows on a land class (0..1).</summary>
    private static float Density(byte land) => land switch
    {
        MapLand.Meadow or MapLand.Wetland => 1f,
        MapLand.Park => 0.85f,
        MapLand.Open or MapLand.Scrub or MapLand.Orchard => 0.8f,
        MapLand.Cemetery => 0.6f,
        MapLand.Residential => 0.5f,
        MapLand.Forest => 0.3f,
        MapLand.Farmyard => 0.25f,
        MapLand.Sand => 0.05f,
        _ => 0f,   // fields (stubble and crops are the terrain's), paving, water
    };

    [ThreadStatic] private static MapContext? _ctx;

    /// <summary>Worker thread: the clumps of one cell, positions relative to the cell's corner.</summary>
    private static (float[] Buffer, int Count) Build(MapWorld world, (long X, long Z) key)
    {
        double ox = key.X * CellSize, oz = key.Z * CellSize;
        var ctx = _ctx;
        if (ctx == null || ctx.World != world) _ctx = ctx = new MapContext(world);
        ctx.Reset(ox, oz, 0, 0, (float)CellSize, (float)CellSize, withPoints: false);
        int n = (int)(CellSize / Spacing);
        var buf = new List<float>(n * n * Stride / 2);
        int count = 0;
        uint seed = Hash.Of((int)key.X, (int)key.Z, 77);
        for (int j = 0; j < n; j++)
            for (int i = 0; i < n; i++)
            {
                uint h = Hash.Of(i, j, (int)seed);
                float jx = Hash.F01(h), jz = Hash.F01(h * 2654435761u + 1), keep = Hash.F01(h * 40503u + 7);
                float lx = (i + jx) * Spacing, lz = (j + jz) * Spacing;
                float dens = Density(ctx.Land(lx, lz));
                if (dens <= 0f || keep > dens) continue;
                if (!float.IsNaN(ctx.Water(lx, lz)) || ctx.InBuilding(lx, lz, 0.2f)) continue;
                float y = ctx.Height(lx, lz, 0.18f, out float dirt);
                if (dirt > 0.3f) continue;   // roads, their verges, tracks, yards
                float yaw = Hash.F01(h * 7919u + 3) * Mathf.Tau;
                float scale = 0.75f + 0.55f * Hash.F01(h * 104729u + 5);
                float tall = scale * (0.8f + 0.5f * dens);   // meadows taller than lawns
                var basis = new Basis(Vector3.Up, yaw).Scaled(new Vector3(scale, tall, scale));
                buf.Add(basis.X.X); buf.Add(basis.Y.X); buf.Add(basis.Z.X); buf.Add(lx);
                buf.Add(basis.X.Y); buf.Add(basis.Y.Y); buf.Add(basis.Z.Y); buf.Add(y - 0.04f);
                buf.Add(basis.X.Z); buf.Add(basis.Y.Z); buf.Add(basis.Z.Z); buf.Add(lz);
                // custom: dryness (yellower in thin, open grass), random, density
                buf.Add(Hash.F01(h * 31337u + 9) * (1.1f - dens)); buf.Add(Hash.F01(h * 9973u + 11)); buf.Add(dens); buf.Add(0f);
                count++;
            }
        return (buf.ToArray(), count);
    }

    /// <summary>One clump: 14 blades leaning out from a 25 cm patch, each two bent segments (a quad and a tip), about
    /// 35 cm tall; vertex colour R = height along the blade (0 root, 1 tip) for the shader.</summary>
    private static Mesh MakeClump(Material material)
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var rng = new Random(5);
        for (int b = 0; b < 14; b++)
        {
            float a = b * 2.39996f + (float)rng.NextDouble() * 0.4f;
            var outv = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            var side = new Vector3(-outv.Z, 0, outv.X);
            var root = outv * (0.03f + 0.2f * (float)rng.NextDouble());
            float h = 0.2f + 0.18f * (float)rng.NextDouble(), lean = 0.06f + 0.14f * (float)rng.NextDouble(), w = 0.024f + 0.014f * (float)rng.NextDouble();
            var mid = root + outv * lean * 0.35f + Vector3.Up * h * 0.55f;
            var tip = root + outv * lean + Vector3.Up * h;
            var n = (Vector3.Up * 0.6f + outv).Normalized();
            void V(Vector3 p, float t) { st.SetColor(new Color(t, 0, 0)); st.SetNormal(n); st.AddVertex(p); }
            // root quad, then the tip triangle
            V(root - side * w, 0); V(root + side * w, 0); V(mid + side * w * 0.6f, 0.55f);
            V(root - side * w, 0); V(mid + side * w * 0.6f, 0.55f); V(mid - side * w * 0.6f, 0.55f);
            V(mid - side * w * 0.6f, 0.55f); V(mid + side * w * 0.6f, 0.55f); V(tip, 1);
        }
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, material);
        return mesh;
    }
}
