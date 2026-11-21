using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Generic;
using BalticDrive.Core;
using BalticDrive.Map;
using BalticDrive.World;
using Godot;

namespace BalticDrive.Vehicle;

/// <summary>
/// The car: Godot's raycast VehicleBody3D with speed-sensitive steering, a power curve, surface grip and aero drag —
/// the driving model of Dead Miles' VehicleController without its survival parts (fuel, damage, trunk). Model:
/// Quaternius' pickup (CC0). Keys: W/S or arrows drive and brake, A/D steer, Space handbrake, C camera, H lights,
/// R recover onto the nearest road. Survives origin jumps by being warped with its velocity kept.
/// </summary>
public partial class Car : VehicleBody3D, OnFoot.IHittable
{
    /// <summary>How this car drives (D98): weight, pull, top speed, drive, grip, steering, brakes, springs, anti-roll.
    /// From GameData/cars.json by the model's file name; <c>--carspec=id</c> picks one.</summary>
    public CarSpec Spec { get; private set; } = CarSpec.Fallback;
    /// <summary>Tyre grip (D97): the wheels' friction slip, a friction coefficient (lateral force up to grip × load).
    /// About 1.4 g of cornering: the tyres slide before a car this tall would roll (it used to be 3.3, ~3 g).</summary>

    /// <summary>The arcade anti-roll bars (D97): torque per radian of roll and per rad/s of roll rate, per kg.</summary>
    private const float RollStiffness = 22f, RollDamping = 5f, MaxRollRate = 2.2f;
    // VehicleBody3D drives towards +Z for positive engine force; the car's nose is -Z.
    private const float DriveSign = -1f;
    private const string ModelPath = "res://Assets/Vehicles/Vehicle_Pickup.gltf";

    public float Speed { get; private set; }      // signed, m/s along the nose
    public bool OnRoad { get; private set; }
    public bool FirstPerson { get; private set; }
    public bool Driving = true;
    /// <summary>A wreck (D69): frozen where it stands, no physics, no camera of its own.</summary>
    public bool Parked;
    /// <summary>The throttle applied last physics step (0..1), for the engine's sound (D53).</summary>
    public float Throttle { get; private set; }

    /// <summary>A bullet hit the car while you drive (D52): the damage that reaches the driver (D67: through the part
    /// it hit; 0.2 of the bullet through intact panels and glass, more through broken ones).</summary>
    public event Action<float>? Shot;
    /// <summary>Things worth telling the driver (a part broke, the engine won't start).</summary>
    public event Action<string>? Said;

    /// <summary>The car's parts and their condition (D67).</summary>
    public readonly CarParts Parts = new();

    public string? Hit(OnFoot.BulletHit h)
    {
        var part = PartAt(ToLocal(h.Point), out int side);
        float wear = part.Kind switch
        {
            PartKind.Windshield or PartKind.Window => h.Damage * 1.4f,
            PartKind.Tyre => h.Damage * 1.6f,
            _ => h.Damage * 0.5f,
        };
        Break(part, wear);
        // through a broken hood a bullet reaches the engine (and sometimes the battery)
        if (part.Kind == PartKind.Hood && Parts["hood"].Health < 0.5f) Break(Parts[GD.Randf() < 0.75f ? "engine" : "battery"], h.Damage * 0.6f);
        if (Driving)
        {
            float exposure = part.Kind switch
            {
                PartKind.Windshield => 0.2f + 0.25f * (1 - Parts["windshield"].Health),
                PartKind.Window or PartKind.Door => Parts.Exposure(side),
                PartKind.Tyre or PartKind.Hood or PartKind.Trunk => 0.05f,
                _ => 0.2f,
            };
            Shot?.Invoke(h.Damage * exposure / 0.2f);   // Main takes a fifth of it (D52)
        }
        return $"car: {part.Name.ToLowerInvariant()}";
    }

    /// <summary>Wears a part; says so when it breaks.</summary>
    private void Break(CarPart p, float amount)
    {
        if (Parts.Wear(p, amount)) Said?.Invoke(p.Kind switch
        {
            PartKind.Tyre => $"{p.Name}: flat",
            PartKind.Windshield or PartKind.Window => $"{p.Name}: shattered",
            PartKind.Engine => "The engine is wrecked",
            PartKind.Battery => "The battery is dead",
            _ => $"{p.Name}: broken",
        });
    }

    /// <summary>The part at a point on the car (body space, nose -Z): glass above the waistline (windshield in front,
    /// windows at the sides), tyres near the wheels, hood at the nose, trunk at the tail, doors at the sides.</summary>
    private CarPart PartAt(Vector3 p, out int side)
    {
        side = p.X < _bodyCentre.X ? 0 : 1;
        for (int i = 0; i < 4; i++)
            if (_wheels[i] != null && new Vector2(p.X - _wheels[i].Position.X, p.Z - _wheels[i].Position.Z).Length() < _wheelRadius * 1.25f && p.Y < _waist)
                return Parts.Tyre(i);
        float along = (p.Z - _bodyCentre.Z) / MathF.Max(0.5f, _bodyHalf.Z);   // -1 nose … +1 tail
        if (p.Y >= _waist)
        {
            if (along < -0.2f) return Parts["windshield"];
            if (along > 0.55f) return Parts["trunk"];
            return Parts[$"window.{side}"];
        }
        if (along < -0.5f) return Parts["hood"];
        if (along > 0.6f) return Parts["trunk"];
        return Parts[$"door.{side}"];
    }

    /// <summary>Where a part sits on the car, in body space (the workshop's dots, D68).</summary>
    public Vector3 PartAnchor(CarPart p)
    {
        float hx = _bodyHalf.X, hz = _bodyHalf.Z, cx = _bodyCentre.X, cz = _bodyCentre.Z;
        float sx = p.Slot == 0 ? cx - hx : cx + hx;   // left or right side
        Vector3 Wheel(int i) => _wheels[i] != null ? _wheels[i].Position : new Vector3(i % 2 == 0 ? cx - hx : cx + hx, 0.4f, i < 2 ? cz - hz * 0.7f : cz + hz * 0.7f);
        return p.Kind switch
        {
            PartKind.Engine => new Vector3(cx, _waist - 0.15f, cz - hz * 0.72f),
            PartKind.Battery => new Vector3(cx + hx * 0.55f, _waist - 0.2f, cz - hz * 0.62f),
            PartKind.Tyre => Wheel(p.Slot) + new Vector3(p.Slot % 2 == 0 ? -0.15f : 0.15f, 0, 0),
            PartKind.Brakes => (p.Slot == 0 ? (Wheel(0) + Wheel(1)) : (Wheel(2) + Wheel(3))) * 0.5f + new Vector3(0, -0.05f, 0),
            PartKind.Suspension => (p.Slot == 0 ? (Wheel(0) + Wheel(1)) : (Wheel(2) + Wheel(3))) * 0.5f + new Vector3(0, 0.3f, 0.35f),
            PartKind.Windshield => new Vector3(cx, _waist + 0.35f, cz - hz * 0.3f),
            PartKind.Window => new Vector3(sx, _waist + 0.3f, cz),
            PartKind.Door => new Vector3(sx, _waist - 0.35f, cz - hz * 0.05f),
            PartKind.Hood => new Vector3(cx, _waist + 0.05f, cz - hz * 0.8f),
            PartKind.Trunk => new Vector3(cx, _waist + 0.05f, cz + hz * 0.85f),
            PartKind.Seat => _cockpit != null ? _cockpit.Position + new Vector3(0, -0.6f, 0.15f) : new Vector3(cx - 0.4f, _waist, cz),
            _ => _bodyCentre,
        };
    }

    /// <summary>A part was taken off or fitted (the workshop): wheels show or hide, lights follow the battery.</summary>
    public void PartsChanged()
    {
        for (int i = 0; i < 4; i++)
            if (_wheels[i] != null)
                foreach (var c in _wheels[i].GetChildren()) if (c is Node3D n && !n.HasMeta("ghost")) n.Visible = !Parts.Tyre(i).Missing;
        SetLights(_headlights[0].Visible);
    }

    /// <summary>Dirt and rust over the body (a wreck, D69): an overlay on every mesh but the glass and the wheels.</summary>
    public void Grime(float amount)
    {
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/wreck_grime.gdshader") };
        m.SetShaderParameter("amount", amount);
        foreach (var mi in _model.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>())
        {
            string n = mi.Name.ToString().ToLowerInvariant();
            if (n.Contains("glass") || n.Contains("wheel") || n.Contains("plate")) continue;
            mi.MaterialOverlay = m;
        }
    }

    // ---- the workshop's ghost highlight (D68) ----------------------------------------------------------------------
    private readonly List<(MeshInstance3D Ghost, string Kind, int Wheel)> _ghosts = new();
    private readonly Dictionary<string, MeshInstance3D> _ghostBoxes = new();
    private Shader? _ghostShader;

    /// <summary>Shows one part as a ghost in its colour (null: none). Body and glass parts are the real surface cut to
    /// the part's region; tyres the wheel's own shape; parts inside (engine, battery, brakes, suspension, seat) a box.</summary>
    public void ShowGhost(CarPart? part)
    {
        if (_ghosts.Count == 0 && part != null) BuildGhosts();
        foreach (var (g, _, _) in _ghosts) g.Visible = false;
        foreach (var b in _ghostBoxes.Values) b.Visible = false;
        if (part == null) return;
        var col = part.Missing ? new Color(0.65f, 0.65f, 0.65f) : new Color(1f - part.Health, 0.25f + 0.7f * part.Health, 0.2f);
        if (!part.Missing && part.Condition <= 0) col = new Color(1f, 0.12f, 0.08f);
        var toCar = GlobalTransform.AffineInverse();
        var proj = new Godot.Projection(toCar);
        void Show(MeshInstance3D g, bool box, Vector3 min, Vector3 max)
        {
            var m = (ShaderMaterial)g.MaterialOverride;
            m.SetShaderParameter("tint", col);
            m.SetShaderParameter("use_box", box);
            m.SetShaderParameter("box_min", min);
            m.SetShaderParameter("box_max", max);
            m.SetShaderParameter("to_car", proj);
            g.Visible = true;
        }
        float hx = _bodyHalf.X, hz = _bodyHalf.Z, cx = _bodyCentre.X, cz = _bodyCentre.Z, w = _waist;
        (Vector3, Vector3) Side(int s, float z0, float z1, float y0, float y1) =>
            s == 0 ? (new Vector3(cx - hx - 0.4f, y0, z0), new Vector3(cx - hx * 0.35f, y1, z1)) : (new Vector3(cx + hx * 0.35f, y0, z0), new Vector3(cx + hx + 0.4f, y1, z1));
        switch (part.Kind)
        {
            case PartKind.Tyre:
                foreach (var (g, k, wi) in _ghosts) if (k == "wheel" && wi == part.Slot) Show(g, false, default, default);
                return;
            case PartKind.Hood: Body(new Vector3(cx - hx - 0.4f, -2, cz - hz - 0.5f), new Vector3(cx + hx + 0.4f, w + 0.3f, cz - hz * 0.5f)); return;
            case PartKind.Trunk: Body(new Vector3(cx - hx - 0.4f, -2, cz + hz * 0.6f), new Vector3(cx + hx + 0.4f, w + 0.35f, cz + hz + 0.5f)); return;
            case PartKind.Door: { var (a, b) = Side(part.Slot, cz - hz * 0.45f, cz + hz * 0.5f, -2, w + 0.05f); Body(a, b); return; }
            case PartKind.Windshield: Glass(new Vector3(cx - hx - 1, w - 0.3f, cz - hz - 1), new Vector3(cx + hx + 1, 9, cz - hz * 0.15f)); return;
            case PartKind.Window: { var (a, b) = Side(part.Slot, cz - hz * 0.15f, cz + hz * 0.55f, w - 0.3f, 9); Glass(a, b); return; }
        }
        // inside the car: a box at the part
        var size = part.Kind switch
        {
            PartKind.Engine => new Vector3(1.0f, 0.55f, 0.8f), PartKind.Battery => new Vector3(0.32f, 0.24f, 0.26f),
            PartKind.Brakes => new Vector3(hx * 2 - 0.2f, 0.3f, 0.3f), PartKind.Suspension => new Vector3(hx * 2 - 0.1f, 0.25f, 0.5f),
            PartKind.Seat => new Vector3(0.55f, 0.9f, 0.6f), _ => new Vector3(0.5f, 0.5f, 0.5f),
        };
        if (!_ghostBoxes.TryGetValue(part.Id, out var box))
        {
            // seen through the body (an x-ray of what's inside)
            box = new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/part_ghost_xray.gdshader") }, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            box.SetMeta("ghost", true);
            AddChild(box);
            box.Position = PartAnchor(part);
            _ghostBoxes[part.Id] = box;
        }
        Show(box, false, default, default);

        void Body(Vector3 a, Vector3 b) { foreach (var (g, k, _) in _ghosts) if (k == "body") Show(g, true, a, b); }
        void Glass(Vector3 a, Vector3 b) { foreach (var (g, k, _) in _ghosts) if (k == "glass") Show(g, true, a, b); }
    }

    /// <summary>Ghost copies of the car's meshes (sharing them, with the ghost material), hidden until asked for.</summary>
    private void BuildGhosts()
    {
        _ghostShader ??= GD.Load<Shader>("res://Shaders/part_ghost.gdshader");
        foreach (var mi in FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>().ToList())
        {
            if (mi.HasMeta("ghost") || mi.Mesh == null || mi.GetParent() is CpuParticles3D) continue;
            string name = mi.Name.ToString().ToLowerInvariant();
            if (name.Contains("plate") || name.Contains("steer")) continue;
            int wheel = Array.FindIndex(_wheels, w => w != null && mi.GetParent() == w);
            string kind = wheel >= 0 ? "wheel" : name.Contains("glass") ? "glass" : "body";
            var g = new MeshInstance3D { Mesh = mi.Mesh, Skeleton = mi.Skeleton, MaterialOverride = new ShaderMaterial { Shader = _ghostShader },
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Visible = false };
            g.SetMeta("ghost", true);
            mi.GetParent().AddChild(g);
            g.Transform = mi.Transform;
            _ghosts.Add((g, kind, wheel));
        }
    }

    private Vector3 _bodyCentre, _bodyHalf = new(0.9f, 0.7f, 2.2f);
    private float _waist = 1.1f, _wheelRadius = 0.44f;
    private Vector3 _lastVelocity, _lastPosition;
    private int _skipImpact = 4;
    private double _saidAt;
    private float _cough;
    private CpuParticles3D? _smoke;

    /// <summary>Has the car's body reached a point (a person's middle)? Its box grown by <paramref name="margin"/>
    /// (D80: the car never collides with people, they'd be walls to it, so each one asks).</summary>
    public bool Touches(Vector3 p, float margin)
    {
        var l = ToLocal(p) - _bodyCentre;
        return MathF.Abs(l.X) <= _bodyHalf.X + margin && MathF.Abs(l.Z) <= _bodyHalf.Z + margin
            && l.Y > -_bodyHalf.Y - 1.2f && l.Y < _bodyHalf.Y + 0.4f;
    }

    /// <summary>A person hit at <paramref name="speed"/> m/s (D80): a dent in the hood, the windshield too at speed
    /// (they come over the bonnet), and the car loses a little of its speed.</summary>
    public void HitPerson(float speed)
    {
        Break(Parts["hood"], speed * 0.5f);
        if (speed > 12f) Break(Parts["windshield"], (speed - 12f) * 1.5f);
        LinearVelocity *= 1f - MathF.Min(0.08f, 70f / (Mass + 70f));
        _skipImpact = Math.Max(_skipImpact, 2);   // not a crash of its own
    }

    /// <summary>A crash: a sudden change of velocity over 5 m/s wears the parts at the end that hit (D67).</summary>
    private void Impacts(float dt)
    {
        var v = LinearVelocity;
        var pos = GlobalPosition;
        // a teleport (Warp, Recover, an origin shift) isn't a crash: the body can report its old velocity for several
        // steps afterwards; it's recognised by a jump further than the car could have moved
        bool jumped = pos.DistanceTo(_lastPosition) > (v.Length() + _lastVelocity.Length()) * dt + 0.5f;
        _lastPosition = pos;
        if (jumped) _skipImpact = Math.Max(_skipImpact, 6);
        if (_skipImpact > 0 || Freeze) { _skipImpact = Math.Max(0, _skipImpact - 1); _lastVelocity = v; return; }
        var dv = v - _lastVelocity - Vector3.Down * 9.8f * dt;
        _lastVelocity = v;
        var local = GlobalBasis.Inverse() * dv;
        float hard = new Vector2(local.X, local.Z).Length(), drop = -local.Y;
        if (hard > 5f)
        {
            Log.Info("Car", $"crash: {hard:0.0} m/s change (local {local.X:0.0}, {local.Z:0.0}) at {Speed * 3.6f:0} km/h");
            float a = (hard - 5f) * 6f;
            if (MathF.Abs(local.Z) >= MathF.Abs(local.X))
            {
                if (local.Z > 0)   // the nose stopped: a hit in front
                {
                    Break(Parts["hood"], a); Break(Parts["engine"], a * 0.45f); Break(Parts["suspension.front"], a * 0.3f);
                    Break(Parts["battery"], a * 0.15f);
                    if (hard > 11f) Break(Parts["windshield"], a * 0.5f);
                }
                else { Break(Parts["trunk"], a); Break(Parts["suspension.rear"], a * 0.3f); }
            }
            else
            {
                int s = local.X > 0 ? 0 : 1;   // pushed right: hit on the left
                Break(Parts[$"door.{s}"], a); Break(Parts[$"window.{s}"], a * 0.4f);
                Break(Parts.Tyre(s == 0 ? 0 : 1), a * 0.2f);
            }
        }
        if (drop > 7f)   // a hard landing
        {
            float a = (drop - 7f) * 5f;
            Break(Parts["suspension.front"], a); Break(Parts["suspension.rear"], a);
        }
    }
    /// <summary>Scripted inputs (tests): throttle 0..1, brake 0..1, steer -1..1 (left positive).</summary>
    public Func<Car, (float Throttle, float Brake, float Steer)>? Autopilot;
    /// <summary>The handbrake when an autopilot drives (self-tests).</summary>
    public bool AutoHandbrake;
    /// <summary>Whether ground collision is loaded under a world point (the car freezes where it is not).</summary>
    public Func<double, double, bool>? HasGround;
    private PhysicsRayQueryParameters3D? _wheelRay;
    public MapWorld? Map;

    private readonly VehicleWheel3D[] _wheels = new VehicleWheel3D[4]; // FL, FR, RL, RR
    private Node3D _model = null!, _rearAxle = null!;
    /// <summary>A truck's rear wheel meshes (D101): each turns and bounces with a physics wheel (2, 3, or -1: the axle
    /// between them), offset to its own hub.</summary>
    private readonly List<(Node3D Holder, int Wheel, Vector3 Offset)> _followers = new();
    private Camera3D _cockpit = null!, _chase = null!;
    private readonly SpotLight3D[] _headlights = new SpotLight3D[2];
    private float _steer, _lookYaw, _lookPitch = DefaultPitch, _camYaw, _lookIdle = 99;
    /// <summary>The chase camera's elevation behind the car (radians).</summary>
    private const float DefaultPitch = 0.22f;

    public Camera3D ActiveCamera => FirstPerson ? _cockpit : _chase;

    public override void _Ready()
    {
        Spec = Main.Args.TryGetValue("carspec", out var cs) ? CarSpec.ById(cs) : CarSpec.For(Model);
        Mass = Spec.Mass;
        Core.Log.Info("Car", $"{Model.GetFile()}: drives as '{Spec.Id}' ({Spec.Mass:0} kg, {Spec.Drive} drive, grip {Spec.GripFront:0.00}/{Spec.GripRear:0.00})");
        CollisionLayer = Bodies.Vehicles;
        CollisionMask = Bodies.Ground | Bodies.Roads | Bodies.Buildings | Bodies.Props | Bodies.Vehicles;
        CenterOfMassMode = CenterOfMassModeEnum.Custom;
        LinearDampMode = DampMode.Replace;
        AngularDampMode = DampMode.Replace;
        AngularDamp = 0.35f;
        LinearDamp = 0f;
        ContinuousCd = true;
        CanSleep = false;

        BuildModelAndWheels();
        for (int i = 0; i < 2; i++)
        {
            var l = new SpotLight3D
            {
                Position = new Vector3(i == 0 ? -0.75f : 0.75f, 0.95f, -2.55f), Rotation = new Vector3(-0.05f, 0, 0),
                SpotRange = 90f, SpotAngle = 32f, LightEnergy = 10f, LightColor = new Color(1f, 0.93f, 0.8f),
                ShadowEnabled = i == 0, Visible = false,
            };
            AddChild(l);
            _headlights[i] = l;
        }
        _cockpit = new Camera3D { Position = new Vector3(-0.45f, 1.62f, -0.35f), Near = 0.05f, Far = 30000f, Fov = 72f };
        AddChild(_cockpit);
        _chase = new Camera3D { TopLevel = true, Near = 0.1f, Far = 30000f, Fov = 70f };
        AddChild(_chase);
        if (!Parked) _chase.Current = true;
        else Freeze = true;
        WorldOrigin.Shifted += OnShifted;
    }

    public override void _ExitTree() => WorldOrigin.Shifted -= OnShifted;

    private void OnShifted(double dx, double dz)
    {
        var t = GlobalTransform;
        t.Origin -= new Vector3((float)dx, 0, (float)dz);
        _lastPosition -= new Vector3((float)dx, 0, (float)dz);   // the origin moved, the car didn't
        var v = LinearVelocity; var w = AngularVelocity;
        Warp(t, keepVelocity: true);
        LinearVelocity = v; AngularVelocity = w;
        _chase.GlobalPosition -= new Vector3((float)dx, 0, (float)dz);
    }

    /// <summary>The model to drive: a Synty POLYGON City car when that (licensed, git-ignored) pack is installed,
    /// else Quaternius' CC0 pickup. <c>--car=res://...tscn</c> picks another.</summary>
    public static string ChooseModel(string? wanted)
    {
        if (!string.IsNullOrEmpty(wanted) && ResourceLoader.Exists(wanted)) return wanted;
        const string synty = "res://Assets/Synty/PolygonCity/Prefabs/Vehicles/SM_Veh_Car_Sedan_01.tscn";
        return ResourceLoader.Exists(synty) ? synty : ModelPath;
    }

    public string Model = ModelPath;
    private float _bodyLength = 4.5f;

    /// <summary>Transform of a node relative to an ancestor (inclusive of the ancestor's own transform).</summary>
    private static Transform3D ToRoot(Node3D n, Node3D root)
    {
        var t = Transform3D.Identity;
        for (Node? p = n; p != null && p != root.GetParent(); p = p.GetParent())
            if (p is Node3D q) t = q.Transform * t;
        return t;
    }

    /// <summary>
    /// Loads the model (nose +Z, as both Synty and Quaternius author them; turned to the body's -Z), strips any
    /// colliders it brings, finds its four wheels and puts physics wheels where they are, with the wheel meshes on them.
    /// Synty cars: separate meshes *_Wheel_fl/fr/rl/rr pivoted at the hub. Quaternius pickup: FrontWheel_L/R and one
    /// BackWheels mesh. The body collision boxes are fitted to the remaining meshes.
    /// </summary>
    private void BuildModelAndWheels()
    {
        var turn = new Basis(Vector3.Up, Mathf.Pi);
        _model = new Node3D { Rotation = new Vector3(0, Mathf.Pi, 0) };
        AddChild(_model);
        _rearAxle = new Node3D();
        AddChild(_rearAxle);
        var scene = ResourceLoader.Exists(Model) ? GD.Load<PackedScene>(Model) : null;
        Node3D? car = scene?.Instantiate<Node3D>();
        if (car == null) Log.Warn("Car", $"Missing {Model}; using a placeholder body");
        else
        {
            _model.AddChild(car);
            foreach (var n in car.FindChildren("*", "CollisionObject3D", true, false)) n.Free();
        }

        var meshes = new List<MeshInstance3D>();
        if (car != null)
        {
            if (car is MeshInstance3D rootMesh) meshes.Add(rootMesh);
            foreach (var n in car.FindChildren("*", "MeshInstance3D", true, false)) meshes.Add((MeshInstance3D)n);
        }
        string[] synty = { "wheel_fl", "wheel_fr", "wheel_rl", "wheel_rr" };
        var wheelMesh = new MeshInstance3D?[4];
        MeshInstance3D? rearPair = null;
        // trucks (D101): two rear axles, *_Wheel_rl_01/_02 and rr_01/_02 (or one mesh per axle, both sides)
        var rearL = new List<MeshInstance3D>(); var rearR = new List<MeshInstance3D>();
        var rearRx = new System.Text.RegularExpressions.Regex(@"wheel_(rl|rr)_\d+$");
        foreach (var mi in meshes)
        {
            string name = mi.Name.ToString().ToLowerInvariant();
            for (int i = 0; i < 4; i++) if (name.EndsWith(synty[i])) wheelMesh[i] = mi;
            if (name.Contains("frontwheel_l")) wheelMesh[0] = mi;
            if (name.Contains("frontwheel_r")) wheelMesh[1] = mi;
            if (name.Contains("backwheels")) rearPair = mi;
            if (rearRx.Match(name) is { Success: true } m) (m.Groups[1].Value == "rl" ? rearL : rearR).Add(mi);
        }
        // the rear wheels of a truck: the physics wheel between its axles, each axle's meshes following it
        var rearFollow = new List<(MeshInstance3D Mesh, Vector3 Centre, bool Axle, int Side)>();
        if (car != null && wheelMesh[2] == null && wheelMesh[3] == null && rearL.Count + rearR.Count > 0)
        {
            foreach (var mi in rearL.Concat(rearR))
            {
                var a = ToRoot(mi, car) * mi.GetAabb();
                bool axle = a.Size.X > 1.2f;   // one mesh across both sides
                rearFollow.Add((mi, a.GetCenter(), axle, axle ? -1 : (rearL.Contains(mi) ? 2 : 3)));
            }
        }

        // Wheel centres in model space and the radius.
        var centres = new Vector3[4];
        float radius = 0.44f;
        bool found = wheelMesh[0] != null && wheelMesh[1] != null && (rearPair != null || (wheelMesh[2] != null && wheelMesh[3] != null) || rearFollow.Count > 0);
        if (found && car != null)
        {
            for (int i = 0; i < 4; i++)
            {
                var mi = wheelMesh[i];
                if (mi == null) continue;
                var aabb = ToRoot(mi, car) * mi.GetAabb();
                centres[i] = aabb.GetCenter();
                radius = aabb.Size.Y * 0.5f;
            }
            if (rearPair != null)
            {
                var aabb = ToRoot(rearPair, car) * rearPair.GetAabb();
                var c = aabb.GetCenter();
                centres[2] = new Vector3(centres[0].X, c.Y, c.Z);
                centres[3] = new Vector3(centres[1].X, c.Y, c.Z);
            }
            if (rearFollow.Count > 0)
            {
                // between the rear axles, on the front wheels' track
                float y = rearFollow.Average(f => f.Centre.Y), z = rearFollow.Average(f => f.Centre.Z);
                centres[2] = new Vector3(centres[0].X, y, z);
                centres[3] = new Vector3(centres[1].X, y, z);
            }
        }
        else
        {
            radius = 0.4f;
            centres = new[] { new Vector3(0.85f, 0.4f, 1.5f), new Vector3(-0.85f, 0.4f, 1.5f), new Vector3(0.85f, 0.4f, -1.3f), new Vector3(-0.85f, 0.4f, -1.3f) };
            if (car == null) _model.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.9f, 1.2f, 4.6f) }, Position = new Vector3(0, 1.0f, 0) });
        }

        // Body boxes from everything that is not a wheel.
        var body = new Aabb(new Vector3(-0.9f, 0.3f, -2.2f), new Vector3(1.8f, 1.1f, 4.4f));
        bool first = true;
        if (car != null)
            foreach (var mi in meshes)
            {
                if (Array.IndexOf(wheelMesh, mi) >= 0 || mi == rearPair || mi.Mesh == null || rearFollow.Any(f => f.Mesh == mi)) continue;
                var a = ToRoot(mi, car) * mi.GetAabb();
                body = first ? a : body.Merge(a);
                first = false;
            }
        // Lower part solid (bumpers to sills), upper part the cabin, narrower and shorter; in body space (nose -Z).
        var bc = turn * body.GetCenter();
        float low = MathF.Max(body.Position.Y, radius * 0.9f), mid = body.Position.Y + body.Size.Y * 0.55f, top = body.End.Y;
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(body.Size.X, mid - low, body.Size.Z) }, Position = new Vector3(bc.X, (low + mid) * 0.5f, bc.Z) });
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(body.Size.X * 0.85f, top - mid, body.Size.Z * 0.55f) }, Position = new Vector3(bc.X, (mid + top) * 0.5f, bc.Z) });
        CenterOfMass = new Vector3(bc.X, low - 0.05f + Spec.ComY, bc.Z); // low, near the axles: arcade-stable against roll-over
        _bodyLength = body.Size.Z;
        _bodyCentre = bc; _bodyHalf = body.Size * 0.5f; _waist = mid; _wheelRadius = radius;   // for the parts (D67)

        const float rest = 0.25f;
        for (int i = 0; i < 4; i++)
        {
            bool front = i < 2;
            var p = turn * centres[i];
            var w = new VehicleWheel3D
            {
                // the wheel hangs `rest` below its mount; mount it so the hub sits about where the model has it
                Position = new Vector3(p.X, p.Y + rest - 0.08f, p.Z), UseAsSteering = front, UseAsTraction = front ? Spec.FrontDriven : Spec.RearDriven,
                WheelRadius = radius, WheelRestLength = rest, SuspensionTravel = 0.22f,
                SuspensionStiffness = Spec.Suspension, SuspensionMaxForce = 16f * Spec.Mass,
                DampingCompression = Spec.DampCompression, DampingRelaxation = Spec.DampRebound,
                WheelFrictionSlip = front ? Spec.GripFront : Spec.GripRear, WheelRollInfluence = 0.015f,
            };
            AddChild(w);
            _wheels[i] = w;
        }
        if (car == null || !found) return;
        // Move the wheel meshes onto the physics wheels, hub on the pivot.
        for (int i = 0; i < 4; i++)
            if (wheelMesh[i] is { } mi) Reparent(mi, _wheels[i], centres[i]);
        if (rearPair != null) Reparent(rearPair, _rearAxle, new Vector3(0, centres[2].Y, centres[2].Z));
        // a truck's rear axles: each mesh on a holder that turns with the physics wheel, at its own hub
        foreach (var (mi, c, axle, side) in rearFollow)
        {
            var holder = new Node3D();
            AddChild(holder);
            var hub = axle ? new Vector3(0, c.Y, c.Z) : c;
            Reparent(mi, holder, hub);
            var own = turn * hub;
            var at = axle ? turn * new Vector3(0, centres[2].Y, centres[2].Z) : turn * centres[side];
            _followers.Add((holder, side, own - at));
        }

        void Reparent(MeshInstance3D mi, Node3D target, Vector3 hub)
        {
            var toRoot = ToRoot(mi, car!);
            mi.GetParent().RemoveChild(mi);
            target.AddChild(mi);
            // model space → turned body space, shifted so the hub sits on the pivot
            mi.Transform = new Transform3D(turn, -(turn * hub)) * toRoot;
        }
    }

    public void SetLights(bool on)
    {
        // a flat battery: dim lights (D67)
        foreach (var l in _headlights) { l.Visible = on; l.LightEnergy = Parts["battery"].Health > 0.08f ? 10f : 1.5f; }
    }

    public void SetView(bool firstPerson)
    {
        FirstPerson = firstPerson;
        _lookYaw = 0; _lookPitch = firstPerson ? 0 : DefaultPitch;
        if (firstPerson) _cockpit.Current = true;
        else { _camYaw = MathF.Atan2(GlobalTransform.Basis.Z.X, GlobalTransform.Basis.Z.Z); _chase.GlobalTransform = ChaseTarget(); _chase.Current = true; }
        if (Driving && Main.CaptureMouse) Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    /// <summary>Moves the car at once (the physics server's state too, or the next step undoes it).</summary>
    public void Warp(Transform3D to, bool keepVelocity = false)
    {
        _skipImpact = 4;   // a teleport isn't a crash
        GlobalTransform = to;
        var rid = GetRid();
        PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.Transform, to);
        if (!keepVelocity)
        {
            PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.LinearVelocity, Vector3.Zero);
            PhysicsServer3D.BodySetState(rid, PhysicsServer3D.BodyState.AngularVelocity, Vector3.Zero);
            LinearVelocity = Vector3.Zero;
            AngularVelocity = Vector3.Zero;
        }
        ResetPhysicsInterpolation();
    }

    /// <summary>Places the car on the nearest road to its current spot (or a given world point), facing along it.</summary>
    public bool Recover(double? atX = null, double? atZ = null)
    {
        if (Map == null) return false;
        var (wx, _, wz) = WorldOrigin.ToWorld(GlobalPosition);
        wx = atX ?? wx; wz = atZ ?? wz;
        if (!Map.NearestRoad(wx, wz, 600f, 1.8f, out double px, out double py, out double pz, out float yaw, RoadClass.Residential))
        {
            py = Map.Height(wx, wz); px = wx; pz = wz; yaw = 0;
        }
        Warp(new Transform3D(new Basis(Vector3.Up, yaw), WorldOrigin.ToLocal(px, py + 0.5, pz)));
        return true;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Driving) return;
        if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            if (k.Keycode == Key.C) SetView(!FirstPerson);
            else if (k.Keycode == Key.H) SetLights(!_headlights[0].Visible);
            else if (k.Keycode == Key.R) Recover();
        }
        // free look (D51, Tom): the mouse turns the camera all the way round the car and up and down (never under
        // the ground), no button to hold; left alone while driving, it drifts back behind the car. Esc frees the
        // mouse, a click takes it back.
        if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _lookYaw = Mathf.Wrap(_lookYaw - mm.Relative.X * 0.003f, -MathF.PI, MathF.PI);
            _lookPitch = Mathf.Clamp(_lookPitch + mm.Relative.Y * 0.003f, FirstPerson ? -0.6f : -0.05f, FirstPerson ? 0.6f : 1.2f);
            _lookIdle = 0;
        }
        if (e is InputEventMouseButton { Pressed: true } && Input.MouseMode != Input.MouseModeEnum.Captured && Main.CaptureMouse)
            Input.MouseMode = Input.MouseModeEnum.Captured;
        if (e is InputEventKey { Pressed: true, Keycode: Key.Escape }) Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        if (Parked) { if (!Freeze) Freeze = true; return; }
        // Never simulate over ground that has no collision loaded.
        var (wx, _, wz) = WorldOrigin.ToWorld(GlobalPosition);
        bool solid = HasGround?.Invoke(wx, wz) ?? true;
        if (Freeze == solid) Freeze = !solid;
        if (!solid) return;

        var basis = GlobalTransform.Basis;
        Speed = LinearVelocity.Dot(-basis.Z);
        float absSpeed = MathF.Abs(Speed);

        float throttle = 0, brakeIn = 0, steerIn = 0;
        bool handbrake = false;
        if (Autopilot != null) { (throttle, brakeIn, steerIn) = Autopilot(this); handbrake = AutoHandbrake; }
        else if (Driving)
        {
            throttle = Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up) ? 1f : 0f;
            brakeIn = Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down) ? 1f : 0f;
            steerIn = (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left) ? 1f : 0f) - (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right) ? 1f : 0f);
            handbrake = Input.IsKeyPressed(Key.Space);
        }
        else
        {
            // nobody at the wheel (the driver got out, D38): the car stays put
            brakeIn = 1f;
            handbrake = true;
        }

        // What each wheel stands on. Not VehicleWheel3D.GetContactBody(): after a teleport or when the streamer unloads
        // the ground under a wheel it can hand back a freed body, and touching that crashed the game (fly far, press F).
        // A short ray from the wheel only ever returns live bodies.
        int contacts = 0, paved = 0;
        var space = GetWorld3D().DirectSpaceState;
        foreach (var w in _wheels)
        {
            if (!w.IsInContact()) continue;
            contacts++;
            var from = w.GlobalPosition;
            _wheelRay ??= new PhysicsRayQueryParameters3D { CollisionMask = World.Bodies.Ground | World.Bodies.Roads | World.Bodies.Buildings, Exclude = new Godot.Collections.Array<Rid> { GetRid() } };
            _wheelRay.From = from;
            _wheelRay.To = from - GlobalTransform.Basis.Y * (w.WheelRadius + w.WheelRestLength + 0.4f);
            var hit = space.IntersectRay(_wheelRay);
            if (hit.Count > 0 && hit["collider"].AsGodotObject() is Node n && IsInstanceValid(n)
                && n.HasMeta("surface") && n.GetMeta("surface").AsString() == "paved") paved++;
        }
        OnRoad = contacts > 0 && paved * 2 >= contacts;

        Impacts(dt);
        // parts (D67): an engine that won't start, coughs when it's bad
        if (Driving && throttle > 0.02f && !Parts.EngineRuns && Time.GetTicksMsec() / 1000.0 > _saidAt)
        {
            _saidAt = Time.GetTicksMsec() / 1000.0 + 4;
            Said?.Invoke(Parts["engine"].Health <= 0 ? "The engine is wrecked: it won't start" : "The battery is flat: the engine won't start");
        }
        _cough -= dt;
        if (_cough <= 0) _cough = Parts["engine"].Health < 0.35f && GD.Randf() < (0.35f - Parts["engine"].Health) * 2.5f ? -0.25f : 0.3f;
        UpdateSmoke();

        // steering: quick at parking speed, calm at speed
        float maxSteer = Mathf.Lerp(Spec.Steer, 0.05f, MapContext.Smooth(0f, 28f, absSpeed));
        float target = steerIn * maxSteer;
        float rate = MathF.Abs(target) < MathF.Abs(_steer) ? 4.5f : 2.6f;
        _steer = Mathf.MoveToward(_steer, target, rate * dt * (0.25f + maxSteer));
        // a flat front tyre pulls to its side
        float pull = (Parts.Flat(0) ? 0.035f : 0f) - (Parts.Flat(1) ? 0.035f : 0f);
        Steering = _steer + pull * Mathf.Clamp(absSpeed / 8f, 0, 1);

        float top = Spec.TopSpeed * (OnRoad ? 1f : 0.55f);
        float force = 0f, brake = 0f;
        Throttle = Driving ? throttle : 0f;
        if (throttle > 0.02f)
        {
            if (Speed < -1.0f) brake = Spec.Brakes * 0.9f * throttle;
            else force = Spec.Power * throttle * Mathf.Clamp(1f - Speed / top, 0f, 1f) * (Speed < 8f ? 1.2f : 1f);
        }
        if (brakeIn > 0.02f)
        {
            if (Speed > 1.2f) { brake = Spec.Brakes * brakeIn; force = 0f; }
            else force = -Spec.Power * 0.4f * brakeIn * Mathf.Clamp(1f + Speed / 9f, 0f, 1f);
        }
        if (throttle < 0.02f && brakeIn < 0.02f)
        {
            brake = 1.8f;
            if (absSpeed < 1.2f) brake = Spec.Brakes;  // a stopped car stays put on a slope
        }
        if (handbrake) { force = 0; brake = MathF.Max(brake, 4f); }
        if (!Parts.EngineRuns || _cough < 0) force = 0;
        else force *= Parts.Power;

        float grip = OnRoad ? 1f : 0.8f;
        for (int i = 0; i < 4; i++)
        {
            var w = _wheels[i];
            bool rear = i >= 2;
            // the pull shared by the driven wheels (D98: front, rear or all)
            int driven = (Spec.FrontDriven ? 2 : 0) + (Spec.RearDriven ? 2 : 0);
            w.EngineForce = (rear ? Spec.RearDriven : Spec.FrontDriven) ? force / driven * DriveSign : 0f;
            w.Brake = handbrake && rear ? 70f : brake * (rear ? 0.75f : 1f) * (brake > 2f ? Parts.Brakes(rear) : 1f);
            w.WheelFrictionSlip = (rear ? Spec.GripRear : Spec.GripFront) * grip * (handbrake && rear ? 0.45f : 1f) * Parts.Grip(i);
            // a flat runs on its rim; worn suspension is soft, low and bouncy
            w.WheelRadius = Parts.Flat(i) ? _wheelRadius * 0.82f : _wheelRadius;
            float s = Parts.Suspension(rear);
            w.SuspensionStiffness = Spec.Suspension * (0.45f + 0.55f * s);
            w.DampingCompression = Spec.DampCompression * (0.35f + 0.65f * s);
            w.DampingRelaxation = Spec.DampRebound * (0.35f + 0.65f * s);
            w.WheelRestLength = 0.25f * (0.7f + 0.3f * s);
        }

        Stabilise(contacts);

        var v = LinearVelocity;
        ApplyCentralForce(-v * v.Length() * 0.40f);
        if (!OnRoad && contacts > 0) ApplyCentralForce(-v * 60f);

        if (Map != null && GlobalPosition.Y < Map.Height(wx, wz) - 4f) Recover();
    }

    /// <summary>
    /// Anti-roll (D97): while wheels touch the ground, a torque round the car's length pulls it back toward its wheels
    /// (like stiff anti-roll bars) and damps rolling, and the roll rate is capped so a kerb strike can't throw it over
    /// in one step. Only roll: the car still pitches, climbs and jumps. Off the ground (or past 75°, already going
    /// over) it does nothing, so a real crash still rolls.
    /// </summary>
    private void Stabilise(int contacts)
    {
        if (contacts == 0 || Spec.AntiRoll <= 0) return;
        var b = GlobalTransform.Basis;
        var fwd = -b.Z;
        // the roll: how far the car's up leans sideways, about its own length
        float side = b.X.Dot(Vector3.Up);                  // sin of the roll (0 upright)
        if (b.Y.Y < 0.26f) return;                         // already over (past ~75°): let it fall
        float rate = AngularVelocity.Dot(fwd);
        float k = Mass * Spec.AntiRoll * (contacts >= 2 ? 1f : 0.5f);
        // turning about fwd (= −Z) by +a lowers X's y by a, so +side needs a positive torque to come back
        float torque = side * RollStiffness * k - rate * RollDamping * k;
        ApplyTorque(fwd * torque);
        if (MathF.Abs(rate) > MaxRollRate)
            AngularVelocity -= fwd * (rate - MathF.Sign(rate) * MaxRollRate);
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var rl = _wheels[2].Transform; var rr = _wheels[3].Transform;
        _rearAxle.Transform = new Transform3D(rl.Basis, (rl.Origin + rr.Origin) * 0.5f);
        foreach (var (h, i, off) in _followers)
            h.Transform = i < 0 ? new Transform3D(rl.Basis, (rl.Origin + rr.Origin) * 0.5f + off) : new Transform3D(_wheels[i].Transform.Basis, _wheels[i].Transform.Origin + off);
        _lookIdle += dt;
        if (Driving && _lookIdle > 1.5f && MathF.Abs(Speed) > 3f)
        {
            float k = 1f - MathF.Exp(-1.6f * dt);
            _lookYaw = Mathf.LerpAngle(_lookYaw, 0, k);
            _lookPitch = Mathf.Lerp(_lookPitch, FirstPerson ? 0 : DefaultPitch, k);
        }
        if (FirstPerson) _cockpit.Rotation = new Vector3(-0.06f - _lookPitch * (FirstPerson ? 1 : 0), _lookYaw, 0);
        else
        {
            var want = ChaseTarget();
            // round the car the camera goes straight to its angle; only the distance is smoothed
            var pos = want.Origin;
            if (Map != null)
            {
                var (cx, _, cz) = WorldOrigin.ToWorld(pos);
                float ground = Map.Height(cx, cz) + 0.8f;
                if (pos.Y < ground) pos.Y = ground;
            }
            _chase.GlobalPosition = pos;
            _chase.LookAt(GlobalPosition + Vector3.Up * 1.4f, Vector3.Up);
        }
    }

    /// <summary>Smoke from under the hood: grey below 35 %, black and thick when the engine is wrecked.</summary>
    private void UpdateSmoke()
    {
        float e = Parts["engine"].Health;
        bool want = e < 0.35f;
        if (want && _smoke == null)
        {
            _smoke = new CpuParticles3D
            {
                Amount = 40, Lifetime = 2.2, Direction = Vector3.Up, Spread = 20, InitialVelocityMin = 0.6f, InitialVelocityMax = 1.4f,
                Gravity = new Vector3(0, 0.6f, 0), ScaleAmountMin = 1.5f, ScaleAmountMax = 3.5f,
                Mesh = new SphereMesh { Radius = 0.12f, Height = 0.24f, RadialSegments = 6, Rings = 3,
                    Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 1, ShadingMode = BaseMaterial3D.ShadingModeEnum.PerVertex } },
            };
            AddChild(_smoke);
            _smoke.Position = new Vector3(_bodyCentre.X, _waist, _bodyCentre.Z - _bodyHalf.Z * 0.7f);
        }
        if (_smoke == null) return;
        _smoke.Emitting = want;
        float dark = e <= 0 ? 0.08f : 0.45f;
        _smoke.Color = new Color(dark, dark, dark, e <= 0 ? 0.75f : 0.45f);
        _smoke.Amount = e <= 0 ? 60 : 30;
    }

    /// <summary>The chase camera: on a sphere round the car (behind it by default, the car's heading smoothed so the
    /// camera swings round bends instead of snapping), turned by the free look.</summary>
    private Transform3D ChaseTarget()
    {
        var fwd = -GlobalTransform.Basis.Z; fwd.Y = 0;
        if (fwd.LengthSquared() < 0.01f) fwd = Vector3.Forward;
        float heading = MathF.Atan2(-fwd.X, -fwd.Z);
        float dt = (float)GetProcessDeltaTime();
        _camYaw = dt > 0 ? Mathf.LerpAngle(_camYaw, heading, 1f - MathF.Exp(-5f * dt)) : heading;
        float yaw = _camYaw + _lookYaw;
        var dir = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        float r = _bodyLength + 4.4f;
        var target = GlobalPosition + Vector3.Up * 1.4f;
        return new Transform3D(Basis.Identity, target - dir * (r * MathF.Cos(_lookPitch)) + Vector3.Up * (r * MathF.Sin(_lookPitch)));
    }
}
