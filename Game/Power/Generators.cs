using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BalticDrive.Core;
using BalticDrive.Map;
using BalticDrive.World;
using Godot;

namespace BalticDrive.Power;

/// <summary>
/// Portable generators (D109; D108: the grid is dead, power is local). Tom's model, found beside houses, farms, sheds
/// and works, always the same ones in the same places (from the building's OSM id). Each has a 6-litre tank, found with
/// a little petrol or none. F starts and stops one; hold F to pick it up and carry it (slowly, no gun) and F again to put
/// it down; petrol cans go in from the pack (Tab, "Pour"). Running, it burns a litre every four minutes, lights a work
/// lamp, puffs smoke, thrums, and is heard: bandits within 110 m come to look, and more turn up near it. Kept per world
/// in <c>user://generators.json</c> once touched (moved, filled, started).
/// </summary>
public sealed partial class Generators : Node3D
{
    public const string ModelPath = "res://Assets/Props/Generator/generator.glb", CanPath = "res://Assets/Props/PetrolCan/petrol_can.glb";
    /// <summary>Tom's can (Meshy) is modelled 0.95 m tall; a 5-litre can stands about 35 cm.</summary>
    public const float CanScale = 0.37f;
    public const float Tank = 6f, BurnPerSecond = 1f / 240f, Reach = 1.8f, HeardWithin = 110f, Cell = 64f, Near = 200f, Far = 280f;
    private const string FilePath = "user://generators.json";

    /// <summary>One generator: where it stands (world metres), how full, running or not.</summary>
    public sealed class Gen
    {
        public string Id = "";
        public double X, Z;
        public float Yaw, Fuel;
        public bool On, Touched;
        /// <summary>A can of petrol stands beside it, to take (D109).</summary>
        public bool Can;
        [System.Text.Json.Serialization.JsonIgnore] public Node3D? Node;
        [System.Text.Json.Serialization.JsonIgnore] public Running? Fx;
        [System.Text.Json.Serialization.JsonIgnore] public Node3D? CanNode;
    }

    private readonly MapWorld _world;
    private readonly string _map;
    private readonly Dictionary<(long, long), List<Gen>> _cells = new();
    private Dictionary<string, Gen> _saved = new();
    private PackedScene? _scene, _canScene;
    private double _next, _nextNoise;
    private bool _dirty;

    /// <summary>The one you're carrying, or null.</summary>
    public Gen? Carried { get; private set; }
    /// <summary>Everything built around you (self-tests).</summary>
    public IEnumerable<Gen> Live => _cells.Values.SelectMany(l => l).Concat(Carried != null ? new[] { Carried } : Array.Empty<Gen>());
    /// <summary>A running generator is within earshot of the player (bandits turn up more often: D109).</summary>
    public bool Loud { get; private set; }

    public Generators(MapWorld world, string map)
    {
        _world = world; _map = map; Name = "Generators";
        Load();
    }

    public override void _Ready() => WorldOrigin.Shifted += OnShifted;
    public override void _ExitTree() { WorldOrigin.Shifted -= OnShifted; Save(); }
    private void OnShifted(double dx, double dz)
    {
        var d = new Vector3((float)dx, 0, (float)dz);
        foreach (var g in Live) if (g.Node != null) g.Node.GlobalPosition -= d;
    }

    // ---- where they are -----------------------------------------------------------------------------------------------

    [ThreadStatic] private static MapContext? _ctx;

    /// <summary>The generators of one square, from its buildings (the same every time).</summary>
    private List<Gen> Spots(long cx, long cz, float freq)
    {
        var res = new List<Gen>();
        double ox = cx * Cell, oz = cz * Cell;
        var ctx = _ctx;
        if (ctx == null || ctx.World != _world) _ctx = ctx = new MapContext(_world);
        ctx.Reset(ox, oz, 0, 0, Cell, Cell, withPoints: false);
        foreach (var br in ctx.Buildings)
        {
            var b = br.B;
            float bx = br.OffX + b.Cx, bz = br.OffZ + b.Cz;
            if (bx < 0 || bz < 0 || bx >= Cell || bz >= Cell) continue;   // each building belongs to one square
            double chance = b.Type switch
            {
                BuildingKind.Farm => 0.15, BuildingKind.Industrial => 0.10, BuildingKind.Shed => 0.06, BuildingKind.House => 0.035, _ => 0,
            } * freq;
            if (chance <= 0) continue;
            long key = b.OsmId != 0 ? b.OsmId : (long)Hash.Of((int)Math.Floor(ox + bx), (int)Math.Floor(oz + bz), 3);
            var rng = new Random((int)Hash.Of((int)(key & 0x7fffffff), (int)(key >> 31), 7711));
            if (rng.NextDouble() > chance) continue;
            // beside a wall: one of the box's four sides, 1.2 m out, somewhere along it
            for (int tries = 0; tries < 4; tries++)
            {
                int side = rng.Next(4);
                float t = 0.2f + 0.6f * (float)rng.NextDouble();
                float lx = side switch { 0 => br.OffX + b.MinX - 1.2f, 1 => br.OffX + b.MaxX + 1.2f, _ => br.OffX + b.MinX + (b.MaxX - b.MinX) * t };
                float lz = side switch { 2 => br.OffZ + b.MinZ - 1.2f, 3 => br.OffZ + b.MaxZ + 1.2f, _ => br.OffZ + b.MinZ + (b.MaxZ - b.MinZ) * t };
                if (ctx.InBuilding(lx, lz, 0.7f) || !float.IsNaN(ctx.Water(lx, lz))) continue;
                bool road = false;
                foreach (var s in ctx.Segs) if (s.Distance(lx, lz, out _) < s.Hw + 1.2f) { road = true; break; }
                if (road) continue;
                // facing out from the wall, a little askew
                float yaw = side switch { 0 => MathF.PI / 2, 1 => -MathF.PI / 2, 2 => 0, _ => MathF.PI } + (float)(rng.NextDouble() - 0.5) * 0.6f;
                float fuel = rng.NextDouble() < 0.45 ? 0 : MathF.Round((float)rng.NextDouble() * 3.5f, 1);
                res.Add(new Gen { Id = $"g{key}", X = ox + lx, Z = oz + lz, Yaw = yaw, Fuel = fuel, Can = rng.NextDouble() < 0.35 });
                break;
            }
        }
        return res;
    }

    /// <summary>Builds squares near you, drops far ones, burns fuel, makes noise. Call every frame.</summary>
    public void Update(Vector3 at, double now, float dt)
    {
        // running ones burn, wherever they are (only built ones exist: far away ones are paused, like everything else)
        foreach (var g in Live.Where(g => g.On).ToList())
        {
            g.Fuel = MathF.Max(0, g.Fuel - BurnPerSecond * dt);
            g.Touched = true; _dirty = true;
            if (g.Fuel <= 0)
            {
                SetOn(g, false);
                if (g.Node != null && g.Node.GlobalPosition.DistanceTo(at) < 40) Main.Instance?.ShowNote(Core.Lang.T("gen.dies"));
                Log.Info("Power", $"{g.Id}: out of petrol");
            }
        }
        if (Carried?.Node is { } cn && Main.Instance?.Walker is { } w)
        {
            // in your arms, in front of you at hip height
            var fwd = new Vector3(-MathF.Sin(w.Yaw), 0, -MathF.Cos(w.Yaw));
            cn.GlobalTransform = new Transform3D(new Basis(Vector3.Up, w.Yaw + MathF.PI), w.GlobalPosition + fwd * 0.75f + Vector3.Up * 0.35f);
        }
        Loud = Live.Any(g => g.On && g.Node != null && g.Node.GlobalPosition.DistanceTo(at) < Near);
        if (now >= _nextNoise)
        {
            _nextNoise = now + 3;
            foreach (var g in Live.Where(g => g.On && g.Node != null))
                Main.Instance?.Bandits?.NoiseAt(g.Node!.GlobalPosition, HeardWithin);
        }
        if (now < _next) return;
        _next = now + 1;
        if (_dirty) { Save(); _dirty = false; }
        foreach (var key in _cells.Keys.ToList())
        {
            var c = WorldOrigin.ToLocal((key.Item1 + 0.5) * Cell, 0, (key.Item2 + 0.5) * Cell) - at;
            if (c.X * c.X + c.Z * c.Z < Far * Far) continue;
            foreach (var g in _cells[key]) { g.Node?.QueueFree(); g.Node = null; g.Fx = null; g.CanNode = null; }
            _cells.Remove(key);
        }
        float freq = Sandbox.Get("generators.frequency");
        var (wx, _, wz) = WorldOrigin.ToWorld(at);
        long x0 = (long)Math.Floor((wx - Near) / Cell), x1 = (long)Math.Floor((wx + Near) / Cell);
        long z0 = (long)Math.Floor((wz - Near) / Cell), z1 = (long)Math.Floor((wz + Near) / Cell);
        int made = 0;
        for (long cz = z0; cz <= z1 && made < 2; cz++)
            for (long cx = x0; cx <= x1 && made < 2; cx++)
            {
                if (_cells.ContainsKey((cx, cz))) continue;
                double mx = (cx + 0.5) * Cell - wx, mz = (cz + 0.5) * Cell - wz;
                if (mx * mx + mz * mz > Near * Near) continue;
                var list = new List<Gen>();
                // the found ones, unless touched (then the saved state, wherever it went)
                foreach (var g in Spots(cx, cz, freq)) if (!_saved.ContainsKey(g.Id)) list.Add(g);
                // touched ones standing in this square now
                foreach (var g in _saved.Values)
                    if (g != Carried && (long)Math.Floor(g.X / Cell) == cx && (long)Math.Floor(g.Z / Cell) == cz) list.Add(g);
                foreach (var g in list) Build(g);
                _cells[(cx, cz)] = list;
                made++;
            }
    }

    private void Build(Gen g)
    {
        _scene ??= ResourceLoader.Exists(ModelPath) ? GD.Load<PackedScene>(ModelPath) : null;
        var root = new Node3D { Name = $"Generator_{g.Id}" };
        AddChild(root);
        if (_scene?.Instantiate<Node3D>() is { } model) root.AddChild(model);
        // a box to bump into and to stand behind, on the props layer
        var body = new StaticBody3D { CollisionLayer = Bodies.Props, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.95f, 0.8f, 0.72f) }, Position = new Vector3(0, 0.4f, 0) });
        root.AddChild(body);
        g.Node = root;
        if (g.Can)
        {
            // the can on the ground beside it, to its right, a little turned
            _canScene ??= ResourceLoader.Exists(CanPath) ? GD.Load<PackedScene>(CanPath) : null;
            if (_canScene?.Instantiate<Node3D>() is { } can)
            {
                can.Scale = Vector3.One * CanScale;
                can.Position = new Vector3(0.85f, 0, 0.1f);
                can.Rotation = new Vector3(0, 0.5f, 0);
                root.AddChild(can);
                g.CanNode = can;
            }
        }
        Place(g);
        if (g.On) SetOn(g, true);
    }

    private void Place(Gen g)
    {
        if (g.Node == null) return;
        float y = _world.Height(g.X, g.Z);
        g.Node.GlobalTransform = new Transform3D(new Basis(Vector3.Up, g.Yaw), WorldOrigin.ToLocal(g.X, y + 0.01, g.Z));
    }

    // ---- what you can do with one -------------------------------------------------------------------------------------

    /// <summary>The generator within reach of someone standing there, or null.</summary>
    public Gen? InReach(Vector3 at)
    {
        Gen? best = null; float bd = Reach;
        foreach (var g in _cells.Values.SelectMany(l => l))
            if (g.Node != null && g.Node.GlobalPosition.DistanceTo(at) is var d && d < bd) { bd = d; best = g; }
        return best;
    }

    /// <summary>A can of petrol on the ground within reach (closer than its generator), or null.</summary>
    public Gen? CanInReach(Vector3 at)
    {
        foreach (var g in _cells.Values.SelectMany(l => l))
            if (g.Can && g.CanNode != null && g.CanNode.GlobalPosition.DistanceTo(at) < 1.1f) return g;
        return null;
    }

    /// <summary>F by a can: into the pack, if it fits.</summary>
    public bool TakeCan(Gen g, Items.Inventory pack, out string said)
    {
        if (pack.Room("petrol_can") < 1) { said = Core.Lang.T("gen.too_heavy"); return false; }
        pack.Add("petrol_can", 1);
        g.Can = false; g.CanNode?.QueueFree(); g.CanNode = null;
        g.Touched = true; Remember(g);
        said = Core.Lang.T("gen.took_can");
        Log.Info("Power", $"{g.Id}: can taken");
        return true;
    }

    /// <summary>What F does with it, for the prompt.</summary>
    public static string Prompt(Gen g, int cans) =>
        Core.Lang.T(g.On ? "gen.switch_off" : g.Fuel > 0 ? "gen.start" : "gen.empty") + Core.Lang.T("gen.carry")
        + Core.Lang.T("gen.petrol", g.Fuel.ToString("0.0"), Tank.ToString("0")) + (cans > 0 && g.Fuel < Tank - 0.5f ? (cans == 1 ? Core.Lang.T("gen.pour_one") : Core.Lang.T("gen.pour_n", cans)) : "") + "]";

    /// <summary>F: start or stop. False with a reason when it won't start.</summary>
    public bool Toggle(Gen g, out string said)
    {
        if (g.On) { SetOn(g, false); said = Core.Lang.T("gen.off"); }
        else if (g.Fuel <= 0) { said = Core.Lang.T("n.no_petrol"); return false; }
        else { SetOn(g, true); said = Core.Lang.T("gen.on"); }
        g.Touched = true; Remember(g);
        Log.Info("Power", $"{g.Id}: {(g.On ? "on" : "off")}, {g.Fuel:0.0} L");
        return true;
    }

    /// <summary>Pours up to <paramref name="litres"/> in; returns what went in.</summary>
    public float Pour(Gen g, float litres)
    {
        float put = MathF.Min(litres, Tank - g.Fuel);
        if (put <= 0) return 0;
        g.Fuel += put; g.Touched = true; Remember(g);
        Log.Info("Power", $"{g.Id}: poured {put:0.0} L, now {g.Fuel:0.0} L");
        return put;
    }

    /// <summary>Hold F: up into your arms (it stops if it was running).</summary>
    public void PickUp(Gen g)
    {
        if (Carried != null) return;
        if (g.On) SetOn(g, false);
        foreach (var list in _cells.Values) list.Remove(g);
        Carried = g; g.Touched = true; Remember(g);
        foreach (var co in g.Node?.FindChildren("*", "CollisionObject3D", true, false) ?? new Godot.Collections.Array<Node>()) ((CollisionObject3D)co).CollisionLayer = 0;
        Log.Info("Power", $"{g.Id}: picked up");
    }

    /// <summary>F while carrying: down in front of you.</summary>
    public void PutDown(Vector3 at, float yaw)
    {
        if (Carried is not { } g) return;
        var fwd = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        var (x, _, z) = WorldOrigin.ToWorld(at + fwd * 0.9f);
        g.X = x; g.Z = z; g.Yaw = yaw + MathF.PI;
        Carried = null;
        Place(g);
        foreach (var co in g.Node?.FindChildren("*", "CollisionObject3D", true, false) ?? new Godot.Collections.Array<Node>()) ((CollisionObject3D)co).CollisionLayer = Bodies.Props;
        var key = ((long)Math.Floor(x / Cell), (long)Math.Floor(z / Cell));
        if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<Gen>();
        list.Add(g);
        Remember(g);
        Log.Info("Power", $"{g.Id}: put down at {x:0.0},{z:0.0}");
    }

    /// <summary>Self-tests: a generator put down right there.</summary>
    public Gen PlaceHere(Vector3 at, float yaw, float fuel, bool can = false)
    {
        var (x, _, z) = WorldOrigin.ToWorld(at);
        var g = new Gen { Id = $"test{(long)Math.Floor(x)}_{(long)Math.Floor(z)}", X = x, Z = z, Yaw = yaw, Fuel = fuel, Touched = true, Can = can };
        var key = ((long)Math.Floor(x / Cell), (long)Math.Floor(z / Cell));
        if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<Gen>();
        list.Add(g);
        Build(g);
        Remember(g);
        return g;
    }

    private void Remember(Gen g) { _saved[g.Id] = g; _dirty = true; }

    private void SetOn(Gen g, bool on)
    {
        g.On = on;
        if (g.Node == null) return;
        if (on && g.Fx == null) { g.Fx = new Running(); g.Node.AddChild(g.Fx); }
        if (!on && g.Fx != null) { g.Fx.QueueFree(); g.Fx = null; }
    }

    /// <summary>Is there a running generator within <paramref name="r"/> metres of a world spot (power at the safehouse).</summary>
    public bool PoweredAt(double x, double z, float r = 40f) =>
        Live.Any(g => g.On && (g.X - x) * (g.X - x) + (g.Z - z) * (g.Z - z) < r * r);

    // ---- kept ---------------------------------------------------------------------------------------------------------

    private void Load()
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            if (!File.Exists(path)) return;
            var all = JsonSerializer.Deserialize<Dictionary<string, List<Gen>>>(File.ReadAllText(path), new JsonSerializerOptions { IncludeFields = true });
            if (all != null && all.TryGetValue(_map, out var mine)) _saved = mine.ToDictionary(g => g.Id);
        }
        catch (Exception ex) { Log.Error("Power", ex.Message); }
    }

    /// <summary>Self-tests: nothing more is written (the test puts the player's file back itself).</summary>
    public bool Frozen;

    public void Save()
    {
        if (Frozen) return;
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            var opts = new JsonSerializerOptions { IncludeFields = true };
            var all = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, List<Gen>>>(File.ReadAllText(path), opts) ?? new() : new();
            all[_map] = _saved.Values.ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(all, opts));
        }
        catch (Exception ex) { Log.Error("Power", ex.Message); }
    }
}

/// <summary>A running generator's show (D109): the thrum, a puff of exhaust, a shiver, and a work lamp on its frame.</summary>
public sealed partial class Running : Node3D
{
    private AudioStreamPlayer3D? _sound;
    private float _t;

    public override void _Ready()
    {
        Name = "Running";
        _sound = new AudioStreamPlayer3D
        {
            Stream = Audio.SoundBank.Get("generator", Thrum), Bus = Audio.Buses.Sfx, Autoplay = true,
            UnitSize = 6f, MaxDistance = 140f, VolumeDb = 2f, Position = new Vector3(0, 0.4f, 0),
        };
        AddChild(_sound);
        // a lamp clipped to the frame, warm and bare
        AddChild(new OmniLight3D { Position = new Vector3(0, 1.6f, 0), LightColor = new Color(1f, 0.82f, 0.6f), LightEnergy = 1.8f, OmniRange = 14f, ShadowEnabled = false });
        var bulb = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 0.06f, Height = 0.12f, RadialSegments = 8, Rings = 4 },
            Position = new Vector3(0, 0.95f, 0),
            MaterialOverride = new StandardMaterial3D { EmissionEnabled = true, Emission = new Color(1f, 0.85f, 0.6f), EmissionEnergyMultiplier = 4f, AlbedoColor = new Color(1, 0.9f, 0.7f) },
        };
        AddChild(bulb);
        // exhaust: small grey puffs from the engine's back corner
        var puff = new GpuParticles3D
        {
            Amount = 24, Lifetime = 2.2f, Position = new Vector3(-0.38f, 0.5f, 0.25f),
            ProcessMaterial = new ParticleProcessMaterial
            {
                Direction = new Vector3(0, 1, 0.3f), Spread = 15f, InitialVelocityMin = 0.4f, InitialVelocityMax = 0.8f,
                Gravity = new Vector3(0, 0.25f, 0), ScaleMin = 0.6f, ScaleMax = 1.4f, Color = new Color(0.35f, 0.35f, 0.35f, 0.35f),
            },
            DrawPass1 = new QuadMesh
            {
                Size = new Vector2(0.25f, 0.25f),
                Material = new StandardMaterial3D
                {
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, VertexColorUseAsAlbedo = true, AlbedoColor = new Color(1, 1, 1, 0.5f),
                    AlbedoTexture = Puff(),
                },
            },
        };
        AddChild(puff);
    }

    public override void _Process(double delta)
    {
        // the shiver: the model (the parent's first child) jitters a millimetre or two
        _t += (float)delta;
        if (GetParent()?.GetChildOrNull<Node3D>(0) is { } model && model != this)
            model.Position = new Vector3(MathF.Sin(_t * 91f) * 0.0015f, MathF.Abs(MathF.Sin(_t * 47f)) * 0.002f, MathF.Cos(_t * 83f) * 0.0015f);
    }

    public override void _ExitTree()
    {
        if (GetParent()?.GetChildOrNull<Node3D>(0) is { } model && model != this) model.Position = Vector3.Zero;
    }

    private static ImageTexture? _puff;

    /// <summary>A soft round puff: white, fading to clear at the edge.</summary>
    private static ImageTexture Puff()
    {
        if (_puff != null) return _puff;
        const int n = 32;
        var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1;
                float a = Mathf.Clamp(1 - MathF.Sqrt(dx * dx + dy * dy), 0, 1);
                img.SetPixel(x, y, new Color(1, 1, 1, a * a));
            }
        return _puff = ImageTexture.CreateFromImage(img);
    }

    /// <summary>A small four-stroke at 3000 rpm, as a loop: 25 firings a second through a tinny muffler, with a rattle.
    /// A placeholder until a recording goes in <c>Assets/Sounds/generator.wav</c>.</summary>
    private static AudioStream Thrum()
    {
        const int rate = Audio.AudioSynth.Rate;
        int n = rate * 2;   // two seconds, a whole number of firings (25 Hz)
        var x = new float[n];
        var rng = new Random(126);
        float lp = 0, hp = 0, prev = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float ph = (t * 25f) % 1f;
            // each firing: a sharp knock dying away, a little louder every other one (the uneven single cylinder)
            float knock = MathF.Exp(-ph * 9f) * ((int)(t * 25f) % 2 == 0 ? 1f : 0.8f);
            float noise = (float)(rng.NextDouble() * 2 - 1);
            lp += (noise * knock - lp) * 0.25f;
            float tone = MathF.Sin(MathF.Tau * 50f * t) * 0.35f + MathF.Sin(MathF.Tau * 100f * t) * 0.2f + MathF.Sin(MathF.Tau * 150f * t) * 0.1f;
            float s = (lp * 1.3f + tone * knock) * 0.6f;
            // a rattle: the frame buzzing
            s += MathF.Sin(MathF.Tau * 310f * t) * 0.04f * knock;
            hp = s - prev + hp * 0.97f; prev = s;   // take the DC away
            x[i] = hp * 0.7f;
        }
        return Audio.AudioSynth.Wav(x, loop: true);
    }
}
