using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BalticDrive.Core;
using BalticDrive.Map;
using BalticDrive.World;
using Godot;

namespace BalticDrive.Build;

/// <summary>A kind of thing you can build (D128), from <c>GameData/build.json</c>.</summary>
public sealed record PieceDef(string Id, string Name, Vector3 Size, string Kind, float Holds, int Bottles, float MinutesPerBottle, int Hp)
{
    public const string DataPath = "res://GameData/build.json";
    private static Dictionary<string, PieceDef>? _all;

    public static PieceDef? Get(string id) { _all ??= Load(); return _all.GetValueOrDefault(id); }
    public static IEnumerable<PieceDef> All { get { _all ??= Load(); return _all.Values; } }
    /// <summary>The language changed: names read again.</summary>
    public static void Reload() => _all = null;

    private static Dictionary<string, PieceDef> Load()
    {
        var res = new Dictionary<string, PieceDef>();
        try
        {
            using var doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(DataPath));
            foreach (var p in doc.RootElement.GetProperty("pieces").EnumerateArray())
            {
                var s = p.GetProperty("size");
                float F(string k) => p.TryGetProperty(k, out var v) ? v.GetSingle() : 0;
                var d = new PieceDef(p.GetProperty("id").GetString()!, Lang.Pick(p, "name") ?? "", new Vector3(s[0].GetSingle(), s[1].GetSingle(), s[2].GetSingle()),
                    p.GetProperty("kind").GetString()!, F("holds"), (int)F("bottles"), F("minutes_per_bottle"), (int)F("hp"));
                res[d.Id] = d;
            }
        }
        catch (Exception ex) { Log.Error("Build", ex.Message); }
        return res;
    }
}

/// <summary>
/// What you've built (D128, Tom: building, part one): workbenches, crates, rain collectors and barricades, standing
/// where you put them, kept per world in <c>user://built.json</c>. Built around you (250 m) and put away beyond. Stand-in
/// shapes until Tom's models (docs/BUILD_MODELS_BRIEF.md). A crate is a container (its contents kept with the others,
/// <see cref="Items.Containers"/>); a rain collector fills over play time; a barricade blocks the way and bullets, and
/// gives way after <see cref="PieceDef.Hp"/> hits.
/// </summary>
public sealed partial class Built : Node3D
{
    public const float Near = 250f, Far = 320f, Reach = 1.7f, BenchReach = 2.5f;
    private const string FilePath = "user://built.json";

    public sealed class Piece
    {
        public string Id = "", Def = "";
        public double X, Y, Z;
        public float Yaw;
        public int Hp;
        /// <summary>A rain collector: bottles of water in it when last counted, and when (play clock).</summary>
        public float Water;
        public double WaterAt = -1;
        [System.Text.Json.Serialization.JsonIgnore] public Node3D? Node;
    }

    private readonly MapWorld _world;
    private readonly string _map;
    private List<Piece> _all = new();
    private double _next;
    /// <summary>Self-tests: nothing is written.</summary>
    public bool Frozen;
    public event Action<string>? Said;

    public IReadOnlyList<Piece> All => _all;

    public Built(MapWorld world, string map) { _world = world; _map = map; Name = "Built"; Load(); }

    public override void _Ready() => WorldOrigin.Shifted += OnShifted;
    public override void _ExitTree() => WorldOrigin.Shifted -= OnShifted;
    private void OnShifted(double dx, double dz)
    {
        var d = new Vector3((float)dx, 0, (float)dz);
        foreach (var p in _all) if (p.Node != null) p.Node.GlobalPosition -= d;
    }

    public void Update(Vector3 at, double now)
    {
        if (now < _next) return;
        _next = now + 1;
        var (wx, _, wz) = WorldOrigin.ToWorld(at);
        foreach (var p in _all)
        {
            double d2 = (p.X - wx) * (p.X - wx) + (p.Z - wz) * (p.Z - wz);
            if (p.Node == null && d2 < Near * Near) Make(p);
            else if (p.Node != null && d2 > Far * Far) { p.Node.QueueFree(); p.Node = null; }
        }
    }

    /// <summary>Builds one (the placer's job done): there to stay.</summary>
    public Piece Add(PieceDef def, Transform3D at)
    {
        var (x, y, z) = WorldOrigin.ToWorld(at.Origin);
        var fwd = at.Basis.Z;
        var p = new Piece
        {
            Id = $"{def.Id}_{(long)(WorldClock.Seconds * 10)}_{(long)Math.Floor(x)}_{(long)Math.Floor(z)}", Def = def.Id, X = x, Y = y, Z = z,
            Yaw = MathF.Atan2(fwd.X, fwd.Z), Hp = def.Hp, WaterAt = WorldClock.Seconds,
        };
        _all.Add(p);
        Make(p);
        Save();
        Log.Info("Build", $"built {def.Id} at {x:0.0},{y:0.0},{z:0.0}");
        return p;
    }

    public void Remove(Piece p)
    {
        p.Node?.QueueFree(); p.Node = null;
        _all.Remove(p);
        Save();
    }

    /// <summary>The piece within reach of someone standing there (any height within 1.5 m), or null.</summary>
    public Piece? InReach(Vector3 at, float reach = Reach)
    {
        Piece? best = null; float bd = reach;
        foreach (var p in _all)
        {
            if (p.Node == null || PieceDef.Get(p.Def) is not { } d) continue;
            var c = p.Node.GlobalPosition;
            if (MathF.Abs(at.Y - c.Y) > 1.5f + d.Size.Y) continue;
            // to its footprint's edge, not its middle
            var local = p.Node.GlobalTransform.Basis.Inverse() * ((at - c) with { Y = 0 });
            float dx = MathF.Max(0, MathF.Abs(local.X) - d.Size.X / 2), dz = MathF.Max(0, MathF.Abs(local.Z) - d.Size.Z / 2);
            float dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist < bd) { bd = dist; best = p; }
        }
        return best;
    }

    /// <summary>A workbench within <see cref="BenchReach"/> m (recipes that need one, D128).</summary>
    public bool BenchNear(Vector3 at) => _all.Any(p => p.Def == "workbench" && p.Node != null && (p.Node.GlobalPosition - at).Length() < BenchReach + 0.8f);

    // ---- what they do --------------------------------------------------------------------------------------------------

    /// <summary>A rain collector's bottles of water now.</summary>
    public static int Bottles(Piece p)
    {
        if (PieceDef.Get(p.Def) is not { Kind: "collector" } d) return 0;
        double since = p.WaterAt < 0 ? 0 : WorldClock.Seconds - p.WaterAt;
        return (int)Math.Min(d.Bottles, p.Water + since / (d.MinutesPerBottle * 60));
    }

    /// <summary>One bottle's worth out of a collector.</summary>
    public void TakeBottle(Piece p)
    {
        if (PieceDef.Get(p.Def) is not { Kind: "collector" } d) return;
        double since = p.WaterAt < 0 ? 0 : WorldClock.Seconds - p.WaterAt;
        p.Water = (float)Math.Min(d.Bottles, p.Water + since / (d.MinutesPerBottle * 60)) - 1;
        p.WaterAt = WorldClock.Seconds;
        Save();
    }

    /// <summary>A crate's contents, kept with the containers.</summary>
    public Items.Inventory Crate(Piece p)
    {
        var inv = Items.Containers.Get(_map, "built:" + p.Id, "none", null);
        inv.MaxWeight = PieceDef.Get(p.Def)?.Holds ?? 50;
        return inv;
    }

    /// <summary>A bullet hit a barricade: it gives way after its hits.</summary>
    public void Hit(Piece p)
    {
        p.Hp--;
        if (p.Hp > 0) { Save(); return; }
        Log.Info("Build", $"{p.Id} gave way");
        Said?.Invoke(Lang.T("n.barricade_gone"));
        Remove(p);
    }

    // ---- looks: stand-ins until Tom's models -----------------------------------------------------------------------------

    private static StandardMaterial3D? _wood, _dark, _blue, _tarp, _metal;
    private static readonly BoxMesh _box = new() { Size = Vector3.One };
    private static readonly CylinderMesh _cyl = new() { TopRadius = 0.5f, BottomRadius = 0.5f, Height = 1, RadialSegments = 14 };

    /// <summary>A piece's shape (a model when there is one, else boxes), its origin at the bottom middle, facing +Z.
    /// <paramref name="ghost"/>: one see-through material for all of it (the placer).</summary>
    public static Node3D Shape(PieceDef d, Material? ghost = null)
    {
        _wood ??= Mat(new Color(.55f, .4f, .25f)); _dark ??= Mat(new Color(.32f, .23f, .14f)); _blue ??= Mat(new Color(.15f, .35f, .65f));
        _tarp ??= Mat(new Color(.2f, .32f, .22f)); _metal ??= Mat(new Color(.4f, .4f, .42f));
        var root = new Node3D { Name = d.Id };
        string path = $"res://Assets/Props/Built/{d.Id}.glb";
        if (ResourceLoader.Exists(path) && GD.Load<PackedScene>(path)?.Instantiate<Node3D>() is { } model)
        {
            root.AddChild(model);
            if (ghost != null) foreach (var mi in model.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>()) mi.MaterialOverride = ghost;
            return root;
        }
        void Box(float x, float y, float z, float sx, float sy, float sz, Material m, float yaw = 0, float tilt = 0) =>
            root.AddChild(new MeshInstance3D { Mesh = _box, MaterialOverride = ghost ?? m, Position = new Vector3(x, y, z), Rotation = new Vector3(tilt, yaw, 0), Scale = new Vector3(sx, sy, sz) });
        var s = d.Size;
        switch (d.Kind)
        {
            case "workbench":
                Box(0, s.Y - 0.04f, 0, s.X, 0.08f, s.Z, _wood);   // the top
                foreach (float lx in new[] { -1f, 1f }) foreach (float lz in new[] { -1f, 1f })
                    Box(lx * (s.X / 2 - 0.08f), (s.Y - 0.08f) / 2, lz * (s.Z / 2 - 0.08f), 0.08f, s.Y - 0.08f, 0.08f, _dark);
                Box(0, 0.22f, 0, s.X - 0.2f, 0.04f, s.Z - 0.15f, _wood);   // the shelf under
                Box(0.4f, s.Y + 0.03f, 0.05f, 0.35f, 0.03f, 0.04f, _metal);   // a hammer left on it
                break;
            case "crate":
                Box(0, s.Y / 2, 0, s.X, s.Y, s.Z, _wood);
                for (int i = 0; i < 3; i++) Box(0, 0.1f + i * (s.Y - 0.2f) / 2, s.Z / 2 + 0.005f, s.X + 0.01f, 0.06f, 0.02f, _dark);   // slats
                break;
            case "collector":
                root.AddChild(new MeshInstance3D { Mesh = _cyl, MaterialOverride = ghost ?? _blue, Position = new Vector3(0, 0.45f, 0), Scale = new Vector3(0.6f, 0.9f, 0.6f) });
                foreach (float lx in new[] { -1f, 1f }) foreach (float lz in new[] { -1f, 1f })
                    Box(lx * (s.X / 2 - 0.05f), s.Y / 2, lz * (s.Z / 2 - 0.05f), 0.06f, s.Y, 0.06f, _dark);   // posts
                Box(0, s.Y - 0.2f, 0, s.X, 0.03f, s.Z, _tarp, 0, 0.12f);   // the sheet, sloping to the barrel
                break;
            case "barricade":
                foreach (float lx in new[] { -1f, 1f }) Box(lx * (s.X / 2 - 0.15f), s.Y / 2, -0.03f, 0.08f, s.Y, 0.05f, _dark);   // battens
                for (int i = 0; i < 4; i++) Box(0, 0.25f + i * (s.Y - 0.4f) / 3, 0.02f, s.X, 0.16f, 0.04f, _wood, 0, 0);
                Box(0, s.Y * 0.55f, 0.045f, s.X * 1.02f, 0.14f, 0.03f, _wood, 0, 0);
                root.GetChild<Node3D>(root.GetChildCount() - 1).RotationDegrees = new Vector3(0, 0, 38);   // one across
                break;
            default:
                Box(0, s.Y / 2, 0, s.X, s.Y, s.Z, _wood);
                break;
        }
        return root;
    }

    private void Make(Piece p)
    {
        if (PieceDef.Get(p.Def) is not { } d) return;
        var root = Shape(d);
        root.Name = $"Built_{p.Id}";
        AddChild(root);
        var body = d.Kind == "barricade" ? new BarricadeBody { Owner_ = this, Piece = p } : new StaticBody3D();
        body.CollisionLayer = Bodies.Props; body.CollisionMask = 0;
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = d.Size }, Position = new Vector3(0, d.Size.Y / 2, 0) });
        root.AddChild(body);
        root.GlobalTransform = new Transform3D(new Basis(Vector3.Up, p.Yaw), WorldOrigin.ToLocal(p.X, p.Y, p.Z));
        p.Node = root;
    }

    private static StandardMaterial3D Mat(Color c) => new() { AlbedoColor = c, Roughness = 0.85f };

    // ---- kept ---------------------------------------------------------------------------------------------------------

    private void Load()
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            if (!File.Exists(path)) return;
            var all = JsonSerializer.Deserialize<Dictionary<string, List<Piece>>>(File.ReadAllText(path), new JsonSerializerOptions { IncludeFields = true });
            if (all != null && all.TryGetValue(_map, out var mine)) _all = mine;
            Log.Info("Build", $"{_all.Count} things built in {_map}");
        }
        catch (Exception ex) { Log.Error("Build", ex.Message); }
    }

    public void Save()
    {
        if (Frozen) return;
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            var opts = new JsonSerializerOptions { IncludeFields = true };
            var all = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, List<Piece>>>(File.ReadAllText(path), opts) ?? new() : new();
            all[_map] = _all;
            File.WriteAllText(path, JsonSerializer.Serialize(all, opts));
        }
        catch (Exception ex) { Log.Error("Build", ex.Message); }
    }
}

/// <summary>A barricade's body: bullets that hit it wear it down (D128).</summary>
public sealed partial class BarricadeBody : StaticBody3D, OnFoot.IHittable
{
    public Built Owner_ = null!;
    public Built.Piece Piece = null!;
    public string? Hit(OnFoot.BulletHit h) { Owner_.Hit(Piece); return null; }
}
