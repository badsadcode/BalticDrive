using System;
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
public partial class Car : VehicleBody3D
{
    private const float TopSpeed = 50f;           // m/s (180 km/h)
    // VehicleBody3D drives towards +Z for positive engine force; the car's nose is -Z.
    private const float DriveSign = -1f;
    private const string ModelPath = "res://Assets/Vehicles/Vehicle_Pickup.gltf";

    public float Speed { get; private set; }      // signed, m/s along the nose
    public bool OnRoad { get; private set; }
    public bool FirstPerson { get; private set; }
    public bool Driving = true;
    /// <summary>Scripted inputs (tests): throttle 0..1, brake 0..1, steer -1..1 (left positive).</summary>
    public Func<Car, (float Throttle, float Brake, float Steer)>? Autopilot;
    /// <summary>Whether ground collision is loaded under a world point (the car freezes where it is not).</summary>
    public Func<double, double, bool>? HasGround;
    public MapWorld? Map;

    private readonly VehicleWheel3D[] _wheels = new VehicleWheel3D[4]; // FL, FR, RL, RR
    private Node3D _model = null!, _rearAxle = null!;
    private Camera3D _cockpit = null!, _chase = null!;
    private readonly SpotLight3D[] _headlights = new SpotLight3D[2];
    private float _steer, _lookYaw;

    public Camera3D ActiveCamera => FirstPerson ? _cockpit : _chase;

    public override void _Ready()
    {
        Mass = 1500f;
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
        _chase.Current = true;
        WorldOrigin.Shifted += OnShifted;
    }

    public override void _ExitTree() => WorldOrigin.Shifted -= OnShifted;

    private void OnShifted(double dx, double dz)
    {
        var t = GlobalTransform;
        t.Origin -= new Vector3((float)dx, 0, (float)dz);
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
        foreach (var mi in meshes)
        {
            string name = mi.Name.ToString().ToLowerInvariant();
            for (int i = 0; i < 4; i++) if (name.EndsWith(synty[i])) wheelMesh[i] = mi;
            if (name.Contains("frontwheel_l")) wheelMesh[0] = mi;
            if (name.Contains("frontwheel_r")) wheelMesh[1] = mi;
            if (name.Contains("backwheels")) rearPair = mi;
        }

        // Wheel centres in model space and the radius.
        var centres = new Vector3[4];
        float radius = 0.44f;
        bool found = wheelMesh[0] != null && wheelMesh[1] != null && (rearPair != null || (wheelMesh[2] != null && wheelMesh[3] != null));
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
                if (Array.IndexOf(wheelMesh, mi) >= 0 || mi == rearPair || mi.Mesh == null) continue;
                var a = ToRoot(mi, car) * mi.GetAabb();
                body = first ? a : body.Merge(a);
                first = false;
            }
        // Lower part solid (bumpers to sills), upper part the cabin, narrower and shorter; in body space (nose -Z).
        var bc = turn * body.GetCenter();
        float low = MathF.Max(body.Position.Y, radius * 0.9f), mid = body.Position.Y + body.Size.Y * 0.55f, top = body.End.Y;
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(body.Size.X, mid - low, body.Size.Z) }, Position = new Vector3(bc.X, (low + mid) * 0.5f, bc.Z) });
        AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(body.Size.X * 0.85f, top - mid, body.Size.Z * 0.55f) }, Position = new Vector3(bc.X, (mid + top) * 0.5f, bc.Z) });
        CenterOfMass = new Vector3(bc.X, low - 0.05f, bc.Z); // low, near the axles: arcade-stable against roll-over
        _bodyLength = body.Size.Z;

        const float rest = 0.25f;
        for (int i = 0; i < 4; i++)
        {
            bool front = i < 2;
            var p = turn * centres[i];
            var w = new VehicleWheel3D
            {
                // the wheel hangs `rest` below its mount; mount it so the hub sits about where the model has it
                Position = new Vector3(p.X, p.Y + rest - 0.08f, p.Z), UseAsSteering = front, UseAsTraction = true,
                WheelRadius = radius, WheelRestLength = rest, SuspensionTravel = 0.22f,
                SuspensionStiffness = 32f, SuspensionMaxForce = 24000f,
                DampingCompression = 2.8f, DampingRelaxation = 3.8f,
                WheelFrictionSlip = front ? 3.3f : 3.1f, WheelRollInfluence = 0.015f,
            };
            AddChild(w);
            _wheels[i] = w;
        }
        if (car == null || !found) return;
        // Move the wheel meshes onto the physics wheels, hub on the pivot.
        for (int i = 0; i < 4; i++)
            if (wheelMesh[i] is { } mi) Reparent(mi, _wheels[i], centres[i]);
        if (rearPair != null) Reparent(rearPair, _rearAxle, new Vector3(0, centres[2].Y, centres[2].Z));

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
        foreach (var l in _headlights) l.Visible = on;
    }

    public void SetView(bool firstPerson)
    {
        FirstPerson = firstPerson;
        if (firstPerson) _cockpit.Current = true;
        else { _chase.GlobalTransform = ChaseTarget(); _chase.Current = true; }
    }

    /// <summary>Moves the car at once (the physics server's state too, or the next step undoes it).</summary>
    public void Warp(Transform3D to, bool keepVelocity = false)
    {
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
        if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
            _lookYaw = Mathf.Clamp(_lookYaw - mm.Relative.X * 0.003f, -2.4f, 2.4f);
        if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right)
        {
            Input.MouseMode = mb.Pressed ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            if (!mb.Pressed) _lookYaw = 0;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
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
        if (Autopilot != null) (throttle, brakeIn, steerIn) = Autopilot(this);
        else if (Driving)
        {
            throttle = Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up) ? 1f : 0f;
            brakeIn = Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down) ? 1f : 0f;
            steerIn = (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left) ? 1f : 0f) - (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right) ? 1f : 0f);
            handbrake = Input.IsKeyPressed(Key.Space);
        }

        int contacts = 0, paved = 0;
        foreach (var w in _wheels)
        {
            if (!w.IsInContact()) continue;
            contacts++;
            if (w.GetContactBody() is Node n && n.HasMeta("surface") && n.GetMeta("surface").AsString() == "paved") paved++;
        }
        OnRoad = contacts > 0 && paved * 2 >= contacts;

        // steering: quick at parking speed, calm at speed
        float maxSteer = Mathf.Lerp(0.6f, 0.05f, MapContext.Smooth(0f, 28f, absSpeed));
        float target = steerIn * maxSteer;
        float rate = MathF.Abs(target) < MathF.Abs(_steer) ? 4.5f : 2.6f;
        _steer = Mathf.MoveToward(_steer, target, rate * dt * (0.25f + maxSteer));
        Steering = _steer;

        float top = TopSpeed * (OnRoad ? 1f : 0.55f);
        float force = 0f, brake = 0f;
        if (throttle > 0.02f)
        {
            if (Speed < -1.0f) brake = 40f * throttle;
            else force = 9000f * throttle * Mathf.Clamp(1f - Speed / top, 0f, 1f) * (Speed < 8f ? 1.2f : 1f);
        }
        if (brakeIn > 0.02f)
        {
            if (Speed > 1.2f) { brake = 45f * brakeIn; force = 0f; }
            else force = -3500f * brakeIn * Mathf.Clamp(1f + Speed / 9f, 0f, 1f);
        }
        if (throttle < 0.02f && brakeIn < 0.02f)
        {
            brake = 1.8f;
            if (absSpeed < 1.2f) brake = 45f;  // a stopped car stays put on a slope
        }
        if (handbrake) { force = 0; brake = MathF.Max(brake, 4f); }

        float grip = OnRoad ? 1f : 0.8f;
        for (int i = 0; i < 4; i++)
        {
            var w = _wheels[i];
            bool rear = i >= 2;
            w.EngineForce = force * 0.25f * DriveSign;
            w.Brake = handbrake && rear ? 70f : brake * (rear ? 0.75f : 1f);
            w.WheelFrictionSlip = (rear ? 3.1f : 3.3f) * grip * (handbrake && rear ? 0.45f : 1f);
        }

        var v = LinearVelocity;
        ApplyCentralForce(-v * v.Length() * 0.40f);
        if (!OnRoad && contacts > 0) ApplyCentralForce(-v * 60f);

        if (Map != null && GlobalPosition.Y < Map.Height(wx, wz) - 4f) Recover();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        var rl = _wheels[2].Transform; var rr = _wheels[3].Transform;
        _rearAxle.Transform = new Transform3D(rl.Basis, (rl.Origin + rr.Origin) * 0.5f);
        if (FirstPerson) _cockpit.Rotation = new Vector3(-0.06f, _lookYaw, 0);
        else
        {
            var want = ChaseTarget();
            var pos = _chase.GlobalPosition.Lerp(want.Origin, 1f - MathF.Exp(-5f * dt));
            if (Map != null)
            {
                var (cx, _, cz) = WorldOrigin.ToWorld(pos);
                float ground = Map.Height(cx, cz) + 0.8f;
                if (pos.Y < ground) pos.Y = ground;
            }
            _chase.GlobalPosition = pos;
            _chase.LookAt(GlobalPosition + Vector3.Up * 1.4f - GlobalTransform.Basis.Z * 3f, Vector3.Up);
        }
    }

    private Transform3D ChaseTarget()
    {
        var fwd = -GlobalTransform.Basis.Z; fwd.Y = 0;
        if (fwd.LengthSquared() < 0.01f) fwd = Vector3.Forward;
        fwd = fwd.Normalized().Rotated(Vector3.Up, _lookYaw);
        return new Transform3D(Basis.Identity, GlobalPosition - fwd * (_bodyLength + 3.8f) + Vector3.Up * 3.0f);
    }
}
