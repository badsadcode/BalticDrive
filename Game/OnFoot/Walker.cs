using System;
using System.Linq;
using BalticDrive.World;
using Godot;

namespace BalticDrive.OnFoot;

/// <summary>
/// The player on foot (D38, the on-foot brief): a 1.8 m capsule that walks, runs, crouches and jumps over the streamed
/// world, wearing a Synty POLYGON City character (a capsule when the licensed pack is not installed). Third person on a
/// spring arm (pulled in by walls) or first person, V toggles; the mouse looks around. A procedural walk cycle stands
/// in until real animations are retargeted onto the humanoid skeleton.
/// </summary>
public partial class Walker : CharacterBody3D, IHittable
{
    public const float Radius = 0.35f, Height = 1.8f;
    public const float WalkSpeed = 1.7f, RunSpeed = 4.2f, SprintSpeed = 6.6f, CrouchSpeed = 1.5f, JumpSpeed = 4.6f, Gravity = 14f;
    public const string CharacterScene = CharacterRig.Scene;

    /// <summary>Whether ground collision is loaded under a world point (the walker waits where it is not).</summary>
    public Func<double, double, bool>? HasGround;
    /// <summary>Scripted input (self-tests): move (x right, y forward, -1..1), sprint.</summary>
    public Func<Walker, (Vector2 Move, bool Sprint)>? Autopilot;
    public bool Active { get; private set; }
    public bool FirstPerson { get; private set; }
    public bool Crouching { get; private set; }
    public float Speed { get; private set; }
    public float Yaw { get; private set; }
    public Camera3D ActiveCamera => FirstPerson || _aimOverride ? _fp : _tp;
    public Camera3D FirstPersonCamera => _fp;
    /// <summary>The guns (D40).</summary>
    public Weapons Guns { get; private set; } = null!;
    /// <summary>Health, hunger and thirst (D46).</summary>
    public Survival Body { get; private set; } = null!;
    private bool _aimOverride;
    /// <summary>Aiming is always first person (the brief): while set, the eye camera is used whatever the chosen view.</summary>
    public bool AimOverride
    {
        get => _aimOverride;
        set
        {
            if (_aimOverride == value) return;
            _aimOverride = value;
            _model.Visible = !(FirstPerson || value);
            if (Active) ActiveCamera.Current = true;
        }
    }

    /// <summary>Self-tests: crouch from code (the keyboard decides otherwise).</summary>
    public void Crouch(bool on) => Crouching = on;

    /// <summary>A bandit's bullet (D47): it hurts.</summary>
    public string? Hit(BulletHit h)
    {
        if (!Active) return null;
        Body.Damage(h.Damage);
        return null;
    }

    /// <summary>Turns the view to look at a world point (self-tests).</summary>
    public void Face(Vector3 point)
    {
        var d = point - _fp.GlobalPosition;
        Yaw = MathF.Atan2(-d.X, -d.Z);
        _pitch = Mathf.Clamp(MathF.Atan2(d.Y, new Vector2(d.X, d.Z).Length()), -1.35f, 1.2f);
        UpdateCamera(0);
    }

    /// <summary>A shot's kick on the view: up by <paramref name="pitch"/>, sideways by <paramref name="yaw"/> (radians).</summary>
    public void AddRecoil(float pitch, float yaw)
    {
        _pitch = Mathf.Clamp(_pitch + pitch, -1.35f, 1.2f);
        Yaw += yaw;
    }
    /// <summary>Extra yaw for the third-person camera only (self-test side views); walking still follows <see cref="Yaw"/>.</summary>
    public float CameraOrbit;

    private Node3D _model = null!, _pivot = null!;
    private SpringArm3D _arm = null!;
    private Camera3D _tp = null!, _fp = null!;
    private float _pitch = -0.15f, _bodyYaw;
    private WalkCycle? _cycle;
    private AnimationTree? _tree;
    private float _airTime, _crouchBlend;

    public override void _Ready()
    {
        CollisionLayer = Bodies.Characters;
        CollisionMask = Bodies.Ground | Bodies.Roads | Bodies.Buildings | Bodies.Vehicles | Bodies.Props;
        FloorMaxAngle = Mathf.DegToRad(50);
        FloorSnapLength = 0.4f;
        AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = Radius, Height = Height },
            Position = new Vector3(0, Height * 0.5f, 0),
        });
        _model = new Node3D { Name = "Model" };
        AddChild(_model);
        BuildModel();
        _pivot = new Node3D { Name = "CameraPivot", Position = new Vector3(0, 1.55f, 0), TopLevel = true };
        AddChild(_pivot);
        _arm = new SpringArm3D { SpringLength = 3.4f, Margin = 0.25f, CollisionMask = Bodies.Ground | Bodies.Buildings | Bodies.Roads, Position = new Vector3(0.45f, 0, 0) };
        _arm.AddExcludedObject(GetRid());
        _pivot.AddChild(_arm);
        _tp = new Camera3D { Name = "ThirdPerson", Fov = 70, Far = 20000 };
        _arm.AddChild(_tp);
        _fp = new Camera3D { Name = "FirstPerson", Fov = 75, Far = 20000, Position = new Vector3(0, 1.66f, -0.12f) };
        AddChild(_fp);
        Guns = new Weapons(this) { Name = "Weapons" };
        AddChild(Guns);
        Body = new Survival(this);
        AddChild(Body);
        WorldOrigin.Shifted += OnShifted;
        Deactivate();
    }

    public override void _ExitTree() => WorldOrigin.Shifted -= OnShifted;

    private void OnShifted(double dx, double dz)
    {
        Position -= new Vector3((float)dx, 0, (float)dz);
        _pivot.GlobalPosition -= new Vector3((float)dx, 0, (float)dz);
    }

    private void BuildModel()
    {
        // the shared rig (D47): the hoodie guy with the locomotion clips, or a procedural walk without them
        var (skel, tree) = CharacterRig.Build(_model, "Character_Male_Hoodie", out _cycle);
        _tree = tree;
        _skel = skel;
        if (skel != null)
        {
            // the gun in the hands, seen in third person (D49): its own model, the hands put on it after the animation
            _tpGun = new Node3D { Name = "TpGun", Visible = false };
            AddChild(_tpGun);
            _tpHold = new GunHold(skel, () => _tpDef != null && _tpGun.Visible ? GunHold.Goals(_tpGun, _tpDef) : null) { Active = false };
            skel.AddChild(_tpHold);
            return;
        }
        // no licensed pack: a plain capsule stands in
        _model.AddChild(new MeshInstance3D
        {
            Mesh = new CapsuleMesh { Radius = Radius, Height = Height },
            Position = new Vector3(0, Height * 0.5f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.3f, 0.45f) },
        });
    }

    private Skeleton3D? _skel;
    private Node3D? _tpGun;
    private GunHold? _tpHold;
    private WeaponDef? _tpDef;
    private float _shoulder;

    /// <summary>The gun out of first person (D49): carried at the ready across the chest, lower when sprinting,
    /// brought up to the shoulder toward where the camera looks while firing. Its own model, swapped with the gun.</summary>
    private void UpdateThirdPersonGun(float dt)
    {
        if (_tpGun == null) return;
        var def = Guns.Current;
        bool show = def != null && Active && !FirstPerson && !_aimOverride;
        if (def != _tpDef)
        {
            foreach (var c in _tpGun.GetChildren()) c.QueueFree();
            _tpDef = def;
            if (def != null && GunModel.Make(def) is { } m)
            {
                _tpGun.AddChild(m);
                foreach (var gi in m.FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>())
                    gi.CastShadow = GeometryInstance3D.ShadowCastingSetting.On;
            }
        }
        _tpGun.Visible = show;
        if (_tpHold != null) _tpHold.Active = show;
        if (!show || def == null) return;
        // firing raises it for a moment
        _shoulder = Guns.Firing ? 1f : Mathf.MoveToward(_shoulder, 0f, dt / 0.8f);
        float sprint = Speed > RunSpeed + 0.5f ? 1f : 0f;
        var body = new Basis(Vector3.Up, _bodyYaw);
        var chest = GlobalPosition + Vector3.Up * (Crouching ? 0.95f : 1.32f);
        // at the ready: forward and down from the chest; shouldered: along the look, from the right shoulder
        float pitch = Mathf.Lerp(-0.45f - 0.35f * sprint, _pitch, _shoulder);
        var look = body * new Basis(Vector3.Right, pitch);
        bool pistol = def.TwoHandedPistol;
        var at = chest + body * new Vector3(pistol ? 0.02f : 0.1f, Mathf.Lerp(-0.05f, 0.12f, _shoulder), -0.28f - (pistol ? 0.12f : 0f) - 0.1f * _shoulder);
        // the rear of a rifle sits at the hold point, a pistol's grip at arm's length
        var grip = new Vector3(-def.RightGrip.X, def.RightGrip.Y, -def.RightGrip.Z) * def.Scale;
        var t = new Transform3D(look, at - look * grip);
        _tpGun.GlobalTransform = dt > 0 ? _tpGun.GlobalTransform.InterpolateWith(t, 1f - MathF.Exp(-16f * dt)) : t;
    }

    private void UpdateAnimations(float dt)
    {
        if (_tree == null) return;
        // velocity in the body's frame: x to the right, y forward
        var local = Transform.Basis.Inverse() * Velocity;
        _crouchBlend = Mathf.MoveToward(_crouchBlend, Crouching ? 1f : 0f, dt * 5f);
        _airTime = IsOnFloor() ? 0f : _airTime + dt;
        CharacterRig.Animate(_tree, new Vector2(local.X, -local.Z), _crouchBlend, Mathf.Clamp((_airTime - 0.12f) * 6f, 0f, 1f));
    }

    /// <summary>Puts the walker at a point (local coordinates) facing <paramref name="yaw"/> and gives it the controls.</summary>
    public void Activate(Vector3 at, float yaw)
    {
        Active = true;
        Visible = true;
        ProcessMode = ProcessModeEnum.Inherit;
        CollisionLayer = Bodies.Characters;
        GlobalPosition = at;
        Velocity = Vector3.Zero;
        Yaw = _bodyYaw = yaw;
        _pitch = -0.15f;
        UpdateCamera(0);
        ActiveCamera.Current = true;
        if (Main.CaptureMouse) Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public void Deactivate()
    {
        Active = false;
        Visible = false;
        ProcessMode = ProcessModeEnum.Disabled;
        CollisionLayer = 0;
        if (Input.MouseMode == Input.MouseModeEnum.Captured) Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    public void SetView(bool firstPerson)
    {
        FirstPerson = firstPerson;
        _model.Visible = !(firstPerson || _aimOverride);   // (a body seen from inside its own head: later, with arms and weapons)
        ActiveCamera.Current = true;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Active) return;
        if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            Yaw -= mm.Relative.X * 0.0025f;
            _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * 0.0025f, -1.35f, 1.2f);
        }
        else if (e is InputEventMouseButton { Pressed: true } && Input.MouseMode != Input.MouseModeEnum.Captured)
        {
            // (braces: without them the "else" below belonged to this "if", and V, Esc and H stopped working)
            if (Main.CaptureMouse) Input.MouseMode = Input.MouseModeEnum.Captured;
        }
        else if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            if (k.Keycode == Key.V) SetView(!FirstPerson);
            else if (k.Keycode == Key.Escape) Input.MouseMode = Input.MouseModeEnum.Visible;
            else if (k.Keycode == Key.H) Body.UseMedkit();
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        var (wx, _, wz) = WorldOrigin.ToWorld(GlobalPosition);
        if (HasGround != null && !HasGround(wx, wz)) { Velocity = Vector3.Zero; return; }   // wait for the ground

        Vector2 move; bool sprint;
        if (Autopilot != null) (move, sprint) = Autopilot(this);
        else
        {
            move = new Vector2(
                (Input.IsKeyPressed(Key.D) ? 1 : 0) - (Input.IsKeyPressed(Key.A) ? 1 : 0),
                (Input.IsKeyPressed(Key.W) ? 1 : 0) - (Input.IsKeyPressed(Key.S) ? 1 : 0));
            sprint = Input.IsKeyPressed(Key.Shift) && Body.CanSprint;   // too thirsty or hurt: no sprinting (D46)
            Crouching = Input.IsKeyPressed(Key.Ctrl);
            if (Input.IsKeyPressed(Key.Space) && IsOnFloor() && !Crouching) Velocity = Velocity with { Y = JumpSpeed };
        }
        if (move.LengthSquared() > 1) move = move.Normalized();
        bool walk = Autopilot == null && Input.IsKeyPressed(Key.Alt);
        float top = Crouching ? CrouchSpeed : walk ? WalkSpeed : sprint && move.Y > 0.3f ? SprintSpeed : RunSpeed;
        // camera-relative: forward is where the camera looks
        var fwd = new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
        var right = new Vector3(-fwd.Z, 0, fwd.X);
        var want = (fwd * move.Y + right * move.X) * top;
        var v = Velocity;
        float accel = IsOnFloor() ? 14f : 3f;
        var flat = new Vector3(v.X, 0, v.Z).MoveToward(want, accel * dt);
        v = new Vector3(flat.X, IsOnFloor() && v.Y < 0 ? -0.5f : v.Y - Gravity * dt, flat.Z);
        Velocity = v;
        MoveAndSlide();
        Speed = new Vector2(Velocity.X, Velocity.Z).Length();

        // the body turns toward where it walks (third person), or with the camera (first person)
        // first person, aiming, or a gun out: the body faces where you look and strafes (D49)
        if (FirstPerson || _aimOverride || Guns.Current != null) _bodyYaw = Mathf.LerpAngle(_bodyYaw, Yaw, FirstPerson || _aimOverride ? 1f : 1f - MathF.Exp(-12f * dt));
        else if (Speed > 0.3f) _bodyYaw = Mathf.LerpAngle(_bodyYaw, MathF.Atan2(-Velocity.X, -Velocity.Z), 1f - MathF.Exp(-10f * dt));
        Rotation = new Vector3(0, _bodyYaw, 0);
    }

    /// <summary>For the self-test: is the walk cycle reaching the skeleton and the mesh.</summary>
    public string DebugPose()
    {
        var sk = _model.GetChildOrNull<Skeleton3D>(0);
        if (sk == null) return "no skeleton";
        int b = sk.FindBone("LeftUpperLeg");
        var mesh = sk.GetChildren().OfType<MeshInstance3D>().FirstOrDefault(m => m.Visible);
        return $"bone {b}: pose {sk.GetBonePoseRotation(b)} rest {sk.GetBoneRest(b).Basis.GetRotationQuaternion()}; mesh {mesh?.Name} skeleton '{mesh?.Skeleton}' skin {(mesh?.Skin != null)}; cycle {(_cycle != null)}";
    }

    public override void _Process(double delta)
    {
        UpdateCamera((float)delta);
        _cycle?.Update((float)delta, Speed, Crouching, IsOnFloor());
        UpdateAnimations((float)delta);
        UpdateThirdPersonGun((float)delta);
    }

    private void UpdateCamera(float dt)
    {
        float eye = Crouching ? 1.05f : 1.55f;
        var target = GlobalPosition + new Vector3(0, eye, 0);
        _pivot.GlobalPosition = dt > 0 ? _pivot.GlobalPosition.Lerp(target, 1f - MathF.Exp(-18f * dt)) : target;
        _pivot.Rotation = new Vector3(_pitch, Yaw + CameraOrbit, 0);
        // first person: the eye, turned with the look (the body follows the yaw)
        _fp.Position = new Vector3(0, Crouching ? 1.12f : 1.66f, -0.12f);
        _fp.GlobalRotation = new Vector3(_pitch, Yaw, 0);
    }
}

/// <summary>
/// A walk cycle in code (D38) for the humanoid skeleton while there are no animation clips: the arms come down from the
/// rest pose, legs and arms swing with the speed, knees bend on the forward swing, the body bobs. All in model space
/// (rotations applied to each bone's rest direction), so it does not depend on how the rig's bone axes are set up.
/// </summary>
public sealed class WalkCycle
{
    private readonly Skeleton3D _s;
    private readonly int _hips, _lUp, _lLo, _rUp, _rLo, _lArm, _lFore, _rArm, _rFore;
    private float _phase, _amount;
    private readonly Vector3 _hipsRest;

    public WalkCycle(Skeleton3D s)
    {
        _s = s;
        _hips = s.FindBone("Hips");
        _lUp = s.FindBone("LeftUpperLeg"); _lLo = s.FindBone("LeftLowerLeg");
        _rUp = s.FindBone("RightUpperLeg"); _rLo = s.FindBone("RightLowerLeg");
        _lArm = s.FindBone("LeftUpperArm"); _lFore = s.FindBone("LeftLowerArm");
        _rArm = s.FindBone("RightUpperArm"); _rFore = s.FindBone("RightLowerArm");
        _hipsRest = _hips >= 0 ? s.GetBoneRest(_hips).Origin : Vector3.Zero;
    }

    public void Update(float dt, float speed, bool crouch, bool onFloor)
    {
        if (_lUp < 0 || _rUp < 0) return;
        // one full stride (two steps) every ~1.5 m at walking pace, longer strides when running
        float stride = Mathf.Lerp(1.5f, 2.6f, Mathf.Clamp((speed - 1.7f) / 4.9f, 0f, 1f));
        _phase = (_phase + speed / stride * MathF.Tau * dt) % MathF.Tau;
        _amount = Mathf.MoveToward(_amount, Mathf.Clamp(speed / 4.2f, 0f, 1.4f), dt * 4f);
        float a = _amount;
        float s = MathF.Sin(_phase);
        float swing = 0.5f * a, knee = 0.9f * a, arm = 0.45f * a;
        float crouchBend = crouch ? 0.7f : 0f;
        float air = onFloor ? 0f : 0.5f;
        // legs: thigh forward/back around the side axis; the knee bends while the leg swings forward
        Leg(_lUp, _lLo, s * swing + crouchBend + air * 0.6f, MathF.Max(0, MathF.Cos(_phase)) * knee + crouchBend * 1.6f + air);
        Leg(_rUp, _rLo, -s * swing + crouchBend, MathF.Max(0, -MathF.Cos(_phase)) * knee + crouchBend * 1.6f + air * 0.3f);
        // arms: down from the rest pose, swinging opposite to the legs, elbows a little bent
        Arm(_lArm, _lFore, +1, -s * arm, 0.25f + 0.25f * a);
        Arm(_rArm, _rFore, -1, s * arm, 0.25f + 0.25f * a);
        // the body: a bob twice per stride, lower when crouching
        if (_hips >= 0)
        {
            float bob = MathF.Abs(MathF.Cos(_phase)) * 0.035f * a - (crouch ? 0.35f : 0f);
            _s.SetBonePosePosition(_hips, _hipsRest + new Vector3(0, bob / MathF.Max(0.01f, _s.MotionScale), 0));
        }
    }

    /// <summary>Global (model space) rest basis of a bone.</summary>
    private Basis GRest(int b) => _s.GetBoneGlobalRest(b).Basis;

    /// <summary>Gives <paramref name="bone"/> the model-space rotation <paramref name="global"/> (its rest rotated),
    /// under a parent whose model-space rotation is <paramref name="parentGlobal"/>.</summary>
    private void Set(int bone, Basis global, Basis parentGlobal) =>
        _s.SetBonePoseRotation(bone, (parentGlobal.Inverse() * global).Orthonormalized().GetRotationQuaternion());

    private void Leg(int up, int lo, float thigh, float kneeBend)
    {
        int parent = _s.GetBoneParent(up);
        var pg = parent >= 0 ? GRest(parent) : Basis.Identity;
        // model space: the character faces +Z; a positive turn about +X moves the foot forward (+Z)
        var dThigh = new Basis(Vector3.Right, -thigh);
        var gUp = dThigh * GRest(up);
        Set(up, gUp, pg);
        if (lo < 0) return;
        var gLo = dThigh * new Basis(Vector3.Right, kneeBend) * GRest(lo);
        Set(lo, gLo, gUp);
    }

    private void Arm(int up, int lo, int side, float swing, float elbow)
    {
        if (up < 0) return;
        int parent = _s.GetBoneParent(up);
        var pg = parent >= 0 ? GRest(parent) : Basis.Identity;
        // the rest direction of the upper arm (toward the elbow), and where it should point: down, a little out
        var restDir = lo >= 0 ? (_s.GetBoneGlobalRest(lo).Origin - _s.GetBoneGlobalRest(up).Origin).Normalized() : new Vector3(side, 0, 0);
        var down = new Vector3(side * 0.22f, -1f, 0).Normalized();
        var lower = new Basis(new Quaternion(restDir, down));
        var gUp = new Basis(Vector3.Right, -swing) * lower * GRest(up);
        Set(up, gUp, pg);
        if (lo < 0) return;
        // the elbow bends forward once the arm hangs down
        var gLo = new Basis(Vector3.Right, -swing) * new Basis(Vector3.Right, -elbow) * lower * GRest(lo);
        Set(lo, gLo, gUp);
    }
}
