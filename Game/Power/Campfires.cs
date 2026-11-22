using System;
using System.Collections.Generic;
using System.Linq;
using BalticDrive.Core;
using BalticDrive.Map;
using BalticDrive.World;
using Godot;

namespace BalticDrive.Power;

/// <summary>
/// Campfires (D111): built by the "Build a campfire" recipe in front of you, outdoors only. Two armfuls of firewood
/// burn ten minutes, each one added five more (at most half an hour of wood in it). A burning one within reach is a
/// heat source for cooking. It flickers, crackles and smokes, and at night its light carries: bandits within 60 m come
/// to look. A burnt-out ring stays until you go far away; firewood and something to light it start it again.
/// Not kept between sessions: a fire is a thing of the moment.
/// </summary>
public sealed partial class Campfires : Node3D
{
    public const float FirstWood = 600f, PerWood = 300f, MaxWood = 1800f, HeatReach = 2.2f, Reach = 1.7f;

    public sealed class Fire
    {
        public double X, Z;
        public float Fuel;
        public Node3D Node = null!;
        public Flames? Fx;
        public bool Burning => Fuel > 0;
    }

    private readonly MapWorld _world;
    private readonly List<Fire> _fires = new();
    private double _nextNoise;
    public IReadOnlyList<Fire> All => _fires;

    public Campfires(MapWorld world) { _world = world; Name = "Campfires"; }

    public override void _Ready() => WorldOrigin.Shifted += OnShifted;
    public override void _ExitTree() => WorldOrigin.Shifted -= OnShifted;
    private void OnShifted(double dx, double dz)
    {
        var d = new Vector3((float)dx, 0, (float)dz);
        foreach (var f in _fires) f.Node.GlobalPosition -= d;
    }

    /// <summary>A new fire at a spot (local coordinates), burning.</summary>
    public Fire Build(Vector3 at)
    {
        var (x, _, z) = WorldOrigin.ToWorld(at);
        var f = new Fire { X = x, Z = z, Fuel = FirstWood };
        var root = new Node3D { Name = "Campfire" };
        AddChild(root);
        float y = _world.Height(x, z);
        root.GlobalPosition = WorldOrigin.ToLocal(x, y, z);
        BuildRing(root, (uint)Hash.Of((int)Math.Floor(x * 3), (int)Math.Floor(z * 3), 404));
        f.Node = root;
        f.Fx = new Flames();
        root.AddChild(f.Fx);
        _fires.Add(f);
        Log.Info("Fire", $"campfire at {x:0.0},{z:0.0}");
        return f;
    }

    /// <summary>The nearest fire within <paramref name="r"/> metres (burning ones only, if asked).</summary>
    public Fire? Near(Vector3 at, float r, bool burningOnly)
    {
        Fire? best = null; float bd = r;
        foreach (var f in _fires)
        {
            if (burningOnly && !f.Burning) continue;
            float d = (f.Node.GlobalPosition - at with { Y = f.Node.GlobalPosition.Y }).Length();
            if (d < bd) { bd = d; best = f; }
        }
        return best;
    }

    /// <summary>One armful more (or a relight, when it's out).</summary>
    public void AddWood(Fire f)
    {
        bool was = f.Burning;
        f.Fuel = MathF.Min(MaxWood, MathF.Max(0, f.Fuel) + (was ? PerWood : FirstWood / 2));
        if (!was) { f.Fx = new Flames(); f.Node.AddChild(f.Fx); Char(f.Node, false); }
        Log.Info("Fire", $"{(was ? "fed" : "relit")}: {f.Fuel / 60:0.0} min of wood");
    }

    public void Update(Vector3 player, double now, float dt)
    {
        foreach (var f in _fires.ToList())
        {
            if (f.Burning)
            {
                f.Fuel -= dt;
                if (f.Fuel <= 0)
                {
                    f.Fuel = 0;
                    f.Fx?.QueueFree(); f.Fx = null;
                    Char(f.Node, true);
                    if (f.Node.GlobalPosition.DistanceTo(player) < 40) Main.Instance?.ShowNote(Core.Lang.T("fire.embers"));
                    Log.Info("Fire", "burnt out");
                }
            }
            if (f.Node.GlobalPosition.DistanceTo(player) > 400) { f.Node.QueueFree(); _fires.Remove(f); }
        }
        // at night the light carries (D111): a quieter lure than a generator
        if (now >= _nextNoise)
        {
            _nextNoise = now + 4;
            float h = Main.Instance?.Hour ?? 12;
            if (h > 19.5f || h < 5.5f)
                foreach (var f in _fires.Where(f => f.Burning)) Main.Instance?.Bandits?.NoiseAt(f.Node.GlobalPosition, 60f);
        }
    }

    /// <summary>Time left, for the prompt: "7 min".</summary>
    public static string Left(Fire f) => f.Fuel >= 60 ? $"{f.Fuel / 60:0} min" : $"{f.Fuel:0} s";

    // ---- looks --------------------------------------------------------------------------------------------------------

    private static StandardMaterial3D? _stone, _wood, _charred;

    private static void BuildRing(Node3D root, uint seed)
    {
        _stone ??= new StandardMaterial3D { AlbedoColor = new Color(0.48f, 0.47f, 0.45f), Roughness = 0.95f };
        _wood ??= new StandardMaterial3D { AlbedoColor = new Color(0.36f, 0.24f, 0.14f), Roughness = 0.9f };
        // stones in a ring
        for (int i = 0; i < 9; i++)
        {
            float a = i / 9f * MathF.Tau + Hash.F01(Hash.Of((int)seed, i, 1)) * 0.3f;
            float s = 0.09f + 0.05f * Hash.F01(Hash.Of((int)seed, i, 2));
            root.AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = s, Height = s * 1.3f, RadialSegments = 6, Rings = 3 },
                MaterialOverride = _stone, Position = new Vector3(MathF.Cos(a) * 0.42f, s * 0.35f, MathF.Sin(a) * 0.42f),
                Rotation = new Vector3(0, a * 2, 0.3f),
            });
        }
        // logs leaning into a little teepee
        for (int i = 0; i < 4; i++)
        {
            float a = i / 4f * MathF.Tau + 0.4f;
            var log = new MeshInstance3D
            {
                Name = "Log", Mesh = new CylinderMesh { TopRadius = 0.035f, BottomRadius = 0.045f, Height = 0.55f, RadialSegments = 6 },
                MaterialOverride = _wood,
            };
            log.Basis = new Basis(Vector3.Up, -a) * new Basis(Vector3.Right, 0.55f);
            log.Position = new Vector3(MathF.Cos(a) * 0.13f, 0.2f, MathF.Sin(a) * 0.13f);
            root.AddChild(log);
        }
    }

    private static void Char(Node3D root, bool charred)
    {
        _charred ??= new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.07f, 0.065f), Roughness = 1f };
        foreach (var c in root.GetChildren().OfType<MeshInstance3D>().Where(m => m.Name.ToString().StartsWith("Log")))
            c.MaterialOverride = charred ? _charred : _wood;
    }
}

/// <summary>A burning campfire's show (D111): flames, embers, smoke, a flickering warm light and a crackle.</summary>
public sealed partial class Flames : Node3D
{
    private OmniLight3D? _light;
    private float _t;
    private readonly float _phase = (float)new Random().NextDouble() * 10f;

    public override void _Ready()
    {
        Name = "Flames";
        AddChild(Particles(48, 0.8f, new Vector3(0, 0.12f, 0), 0.4f, 0.85f, new Color(1f, 0.55f, 0.15f, 0.9f), 0.48f, additive: true, rise: 0.7f));
        AddChild(Particles(20, 0.5f, new Vector3(0, 0.08f, 0), 0.2f, 0.45f, new Color(1f, 0.85f, 0.45f, 1f), 0.28f, additive: true, rise: 0.5f));
        AddChild(Particles(14, 1.6f, new Vector3(0, 0.15f, 0), 0.6f, 1.2f, new Color(1f, 0.75f, 0.3f, 1f), 0.035f, additive: true, rise: 1.2f));
        AddChild(Particles(16, 4.5f, new Vector3(0, 0.7f, 0), 0.35f, 0.6f, new Color(0.3f, 0.3f, 0.3f, 0.22f), 0.6f, additive: false, rise: 0.35f));
        _light = new OmniLight3D { Position = new Vector3(0, 0.6f, 0), LightColor = new Color(1f, 0.6f, 0.3f), LightEnergy = 2.2f, OmniRange = 10f, ShadowEnabled = true };
        AddChild(_light);
        AddChild(new AudioStreamPlayer3D { Stream = Audio.SoundBank.Get("campfire", Crackle), Bus = Audio.Buses.Sfx, Autoplay = true, UnitSize = 3f, MaxDistance = 30f, VolumeDb = -2f, Position = new Vector3(0, 0.3f, 0) });
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        if (_light != null)
            _light.LightEnergy = 2.0f + 0.35f * MathF.Sin(_t * 11f + _phase) + 0.25f * MathF.Sin(_t * 23f + _phase * 2) + 0.15f * MathF.Sin(_t * 5.3f);
    }

    private static ImageTexture? _soft;

    private static GpuParticles3D Particles(int amount, float life, Vector3 at, float vMin, float vMax, Color color, float size, bool additive, float rise)
    {
        if (_soft == null)
        {
            const int n = 32;
            var img = Image.CreateEmpty(n, n, false, Image.Format.Rgba8);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float dx = (x + 0.5f) / n * 2 - 1, dy = (y + 0.5f) / n * 2 - 1;
                    float a = Mathf.Clamp(1 - MathF.Sqrt(dx * dx + dy * dy), 0, 1);
                    img.SetPixel(x, y, new Color(1, 1, 1, a * a));
                }
            _soft = ImageTexture.CreateFromImage(img);
        }
        var fade = new Gradient();
        fade.SetColor(0, color);
        fade.SetColor(1, color with { A = 0 });
        return new GpuParticles3D
        {
            Amount = amount, Lifetime = life, Position = at,
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere, EmissionSphereRadius = 0.12f,
                Direction = Vector3.Up, Spread = 12f, InitialVelocityMin = vMin, InitialVelocityMax = vMax,
                Gravity = new Vector3(0, rise, 0), ScaleMin = 0.6f, ScaleMax = 1.3f,
                ColorRamp = new GradientTexture1D { Gradient = fade },
            },
            DrawPass1 = new QuadMesh
            {
                Size = new Vector2(size, size),
                Material = new StandardMaterial3D
                {
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    BlendMode = additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
                    BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles, VertexColorUseAsAlbedo = true, AlbedoTexture = _soft,
                },
            },
        };
    }

    /// <summary>A crackle as a loop: a soft roar with pops and snaps. A placeholder until Assets/Sounds/campfire.wav.</summary>
    private static AudioStream Crackle()
    {
        const int rate = Audio.AudioSynth.Rate;
        int n = rate * 4;
        var x = new float[n];
        var rng = new Random(77);
        float lp = 0;
        for (int i = 0; i < n; i++)
        {
            float noise = (float)(rng.NextDouble() * 2 - 1);
            lp += (noise - lp) * 0.05f;
            x[i] = lp * 0.5f;
        }
        // pops: short bright bursts, a few per second
        for (int k = 0; k < 26; k++)
        {
            int at = rng.Next(n - 800), len = 80 + rng.Next(500);
            float amp = 0.25f + (float)rng.NextDouble() * 0.6f;
            for (int i = 0; i < len; i++)
                x[at + i] += (float)(rng.NextDouble() * 2 - 1) * amp * MathF.Exp(-i / (len * 0.25f));
        }
        return Audio.AudioSynth.Wav(x, loop: true);
    }
}
