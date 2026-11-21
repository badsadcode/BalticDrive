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
    /// <summary>Something heavy in your arms (D109: a generator): 1 walks slowly, no running, no jumping, no gun.</summary>
    public float Burden;
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
        if (!Active || Dead) return null;
        LastShot = h.Velocity;
        Body.Damage(h.Damage);
        return null;
    }

    // ---- dying (D55, Tom) ------------------------------------------------------------------------------------------
    /// <summary>Down and not yet woken: no controls, the body falls, the view goes down with the eyes.</summary>
    public bool Dead { get; private set; }
    /// <summary>The last bullet that hit (its velocity): the body falls away from it.</summary>
    public Vector3 LastShot;
    private float _deadT;
    private Vector3 _fallAxis = Vector3.Right;
    private Ragdoll? _ragdoll;
    private Transform3D _eyeOnHead;
    /// <summary>Where the body lies: the ragdoll's hips once it has fallen, else the feet.</summary>
    public Vector3 BodyAt => _ragdoll?.Hips is { } h && _ragdoll.Running ? h.GlobalPosition with { Y = h.GlobalPosition.Y - 0.15f } : GlobalPosition;
    /// <summary>Puts a skeleton of the same rig in the pose the body lies in (the body left behind, D56).</summary>
    public bool CopyPose(Skeleton3D dst)
    {
        if (_ragdoll is not { Running: true }) return false;
        _ragdoll.CopyPoseTo(dst);
        return true;
    }
    private static readonly Basis Faced = Basis.FromEuler(new Vector3(0, MathF.PI, 0));   // Synty faces +Z

    /// <summary>Where the body lies once fallen: for the one left behind (a copy, after waking elsewhere).</summary>
    public Transform3D CorpseTransform => _model.GlobalTransform;

    public void Die()
    {
        if (Dead) return;
        Dead = true;
        _deadT = 0;
        Lean = 0; TestLean = null;
        AimOverride = false;
        Guns.Equip(null);
        Guns.HideHud();
        Guns.ProcessMode = ProcessModeEnum.Disabled;
        var away = new Vector3(LastShot.X, 0, LastShot.Z);
        if (away.LengthSquared() < 1e-4f) away = new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));   // starved: forward
        _fallAxis = Vector3.Up.Cross(away.Normalized()).Normalized();   // turning about up x away tips the head away from the shot
        _fp.Current = true;
        if (_ragdoll != null && _skel != null)
        {
            // a ragdoll (D56): the body you see falling is your own, the eye rides the head
            if (_tpHold != null) _tpHold.Active = false;
            if (_bend != null) _bend.Angle = 0;
            if (_tpGun != null) _tpGun.Visible = false;
            _model.Visible = true;
            _ragdoll.Start(LastShot.LengthSquared() > 1e-4f ? LastShot.Normalized() * 45f : Vector3.Zero, _fp.GlobalPosition + Vector3.Down * 0.4f);
            if (_ragdoll.Head is { } head) _eyeOnHead = head.GlobalTransform.AffineInverse() * _fp.GlobalTransform;
            _fp.Near = 0.03f;
        }
        else _model.Visible = false;   // no ragdoll: the view is the eyes alone
    }

    /// <summary>Wakes up somewhere else (local coordinates), upright, with the controls back.</summary>
    public void Revive(Vector3 at, float yaw)
    {
        Dead = false;
        LastShot = Vector3.Zero;
        _ragdoll?.Stop();
        _fp.Near = 0.05f;
        _model.Basis = Faced;
        Guns.ProcessMode = ProcessModeEnum.Inherit;
        Activate(at, yaw);
        SetView(FirstPerson);
    }

    /// <summary>The fall: quicker than a bandit's (0.42 s), tipping from the feet away from the shot; the first-person
    /// eye goes down with the head, looking wherever the head looks.</summary>
    private void UpdateDeath(float dt)
    {
        _deadT = MathF.Min(1, _deadT + dt / 0.42f);
        if (_ragdoll is { Running: true, Head: { } head })
        {
            _fp.GlobalTransform = head.GlobalTransform * _eyeOnHead;
            return;
        }
        float angle = _deadT * _deadT * 1.45f;
        var tip = new Basis(_fallAxis, angle);
        _model.Basis = new Basis(GlobalBasis.Orthonormalized().Inverse() * _fallAxis, angle) * Faced;
        var fwd = new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
        _fp.GlobalPosition = GlobalPosition + tip * (Vector3.Up * 1.62f + fwd * 0.1f) + Vector3.Up * 0.08f * _deadT;
        _fp.GlobalBasis = tip * Basis.FromEuler(new Vector3(_pitch, Yaw, 0));
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
    /// <summary>The sights' drift while aiming (D65, radians: yaw, pitch), set by the guns: the eye turns with it, so
    /// the bullet goes where the drift points.</summary>
    public Vector2 AimSway;
    /// <summary>Extra yaw for the third-person camera only (self-test side views); walking still follows <see cref="Yaw"/>.</summary>
    public float CameraOrbit;

    private Node3D _model = null!, _pivot = null!;
    private SpringArm3D _arm = null!;
    private Camera3D _tp = null!, _fp = null!;
    private float _pitch = -0.15f, _bodyYaw;
    private WalkCycle? _cycle;
    private AnimationTree? _tree;
    private float _airTime, _crouchBlend;

    // ---- leaning (D55, Tom: "looking around the corner") ----------------------------------------------------------
    /// <summary>Lean held: -1 left (Q), +1 right (E), 0 upright; eased, and cut short by a wall at the head's side.</summary>
    public float Lean { get; private set; }
    /// <summary>For the self-test: overrides Q/E while set.</summary>
    public float? TestLean;
    public const float LeanReach = 0.34f, LeanRoll = 0.17f, LeanBend = 0.45f;
    private float _leanRoom = LeanReach;
    private CollisionShape3D _head = null!;
    private LeanBendModifier? _bend;

    /// <summary>Sideways from the upright eye (metres, to the right of where you look).</summary>
    public float LeanOffset => Lean * MathF.Min(LeanReach, _leanRoom);

    private void UpdateLean(float dt)
    {
        float want = TestLean ?? (Autopilot != null ? 0 : (Input.IsKeyPressed(Key.E) ? 1 : 0) - (Input.IsKeyPressed(Key.Q) ? 1 : 0));
        if (Speed > RunSpeed + 0.5f) want = 0;   // no leaning at a sprint
        Lean = Mathf.MoveToward(Lean, want, dt * 5f);
        // room at the side of the head: a wall stops the lean before the eye goes through it
        float eye = Crouching ? 1.12f : 1.62f;
        var right = new Vector3(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));
        var from = GlobalPosition + Vector3.Up * eye;
        _leanRoom = LeanReach;
        if (MathF.Abs(Lean) > 0.01f)
        {
            var q = PhysicsRayQueryParameters3D.Create(from, from + right * MathF.Sign(Lean) * (LeanReach + 0.18f), Bodies.Buildings | Bodies.Ground | Bodies.Props | Bodies.Vehicles | Bodies.Interior);
            q.Exclude = new Godot.Collections.Array<Rid> { GetRid() };
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count > 0) _leanRoom = MathF.Max(0, ((Vector3)hit["position"]).DistanceTo(from) - 0.18f);
        }
        // the head that can be shot goes where the eye goes (the capsule stays put)
        _head.Disabled = MathF.Abs(LeanOffset) < 0.08f;
        _head.GlobalPosition = from + right * LeanOffset;
        if (_bend != null) _bend.Angle = LeanBend * Lean * MathF.Min(1f, _leanRoom / LeanReach);
    }

    public override void _Ready()
    {
        CollisionLayer = Bodies.Characters;
        CollisionMask = Bodies.Ground | Bodies.Roads | Bodies.Buildings | Bodies.Vehicles | Bodies.Props | Bodies.Interior;
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
        _arm = new SpringArm3D { SpringLength = 3.4f, Margin = 0.25f, CollisionMask = Bodies.Ground | Bodies.Buildings | Bodies.Roads | Bodies.Interior, Position = new Vector3(0.45f, 0, 0) };
        _arm.AddExcludedObject(GetRid());
        _pivot.AddChild(_arm);
        _tp = new Camera3D { Name = "ThirdPerson", Fov = 70, Far = 20000 };
        _arm.AddChild(_tp);
        _fp = new Camera3D { Name = "FirstPerson", Fov = 75, Far = 20000, Position = new Vector3(0, 1.66f, -0.12f) };
        AddChild(_fp);
        _head = new CollisionShape3D { Name = "LeaningHead", Shape = new SphereShape3D { Radius = 0.13f }, Disabled = true, TopLevel = true };
        AddChild(_head);
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
        _ragdoll?.Shift(new Vector3((float)dx, 0, (float)dz));
    }

    private void BuildModel()
    {
        // the shared rig (D47): the hoodie guy with the locomotion clips, or a procedural walk without them
        var (skel, tree) = CharacterRig.Build(_model, "Character_Male_Hoodie", out _cycle);
        _tree = tree;
        _skel = skel;
        if (skel != null)
        {
            // the lean bends the spine before the hands find the gun (modifiers run in order)
            _bend = new LeanBendModifier(skel, () => -GlobalBasis.Z);
            skel.AddChild(_bend);
            // the gun in the hands, seen in third person (D49): its own model, the hands put on it after the animation
            _tpGun = new Node3D { Name = "TpGun", Visible = false };
            AddChild(_tpGun);
            _tpHold = new GunHold(skel, () => _tpDef != null && _tpGun.Visible ? GunHold.Goals(_tpGun, _tpDef) : null) { Active = false };
            skel.AddChild(_tpHold);
            _ragdoll = Ragdoll.Build(skel);   // last: it overrides the lean and the hands once it runs (D56)
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
        var chest = GlobalPosition + Vector3.Up * (Crouching ? 0.95f : 1.32f) + body.X * (LeanOffset * 0.6f);
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
        Guns?.HideHud();
        Active = false;
        Visible = false;
        ProcessMode = ProcessModeEnum.Disabled;
        CollisionLayer = 0;
        if (Input.MouseMode == Input.MouseModeEnum.Captured) Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    /// <summary>The highest ledge the walker steps onto without jumping (D85): a kerb, a doorstep, the first step.</summary>
    public const float StepHeight = 0.42f;

    /// <summary>Walking into a low ledge: when the way ahead is blocked by something upright but clear
    /// <see cref="StepHeight"/> higher, the body is lifted onto it (the floor snap settles it). Godot's character body
    /// slides along a step's face instead of climbing it (D85).</summary>
    private void StepUp(Vector3 flat)
    {
        if (!IsOnFloor() || flat.LengthSquared() < 0.25f) return;
        var ahead = flat.Normalized() * 0.3f;
        var t = GlobalTransform;
        var hit = new KinematicCollision3D();
        if (!TestMove(t, ahead, hit) || MathF.Abs(hit.GetNormal().Y) > 0.4f) return;
        var up = Vector3.Up * StepHeight;
        if (TestMove(t, up)) return;                        // no room overhead
        var raised = t.Translated(up);
        if (TestMove(raised, ahead)) return;                // still blocked up there: a wall, not a step
        GlobalPosition += up;
    }

    /// <summary>Inside a building (D81): the building's solid mass stops counting for the body and the camera, its
    /// interior's walls take over.</summary>
    public bool Indoors
    {
        get => _indoors;
        set
        {
            if (_indoors == value) return;
            _indoors = value;
            if (value) { CollisionMask &= ~Bodies.Buildings; _arm.CollisionMask &= ~Bodies.Buildings; }
            else { CollisionMask |= Bodies.Buildings; _arm.CollisionMask |= Bodies.Buildings; }
        }
    }
    private bool _indoors;

    public void SetView(bool firstPerson)
    {
        FirstPerson = firstPerson;
        _model.Visible = !(firstPerson || _aimOverride);   // (a body seen from inside its own head: later, with arms and weapons)
        ActiveCamera.Current = true;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Active || Dead) return;
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
        if (Dead)
        {
            Velocity = new Vector3(0, IsOnFloor() ? -0.5f : Velocity.Y - Gravity * dt, 0);
            MoveAndSlide();
            Speed = 0;
            return;
        }

        Vector2 move; bool sprint;
        if (Autopilot != null) (move, sprint) = Autopilot(this);
        else
        {
            move = new Vector2(
                (Input.IsKeyPressed(Key.D) ? 1 : 0) - (Input.IsKeyPressed(Key.A) ? 1 : 0),
                (Input.IsKeyPressed(Key.W) ? 1 : 0) - (Input.IsKeyPressed(Key.S) ? 1 : 0));
            sprint = Input.IsKeyPressed(Key.Shift) && Body.CanSprint && !_aimOverride;   // too thirsty or hurt: no sprinting (D46); aiming: Shift holds the breath (D65)
            Crouching = Input.IsKeyPressed(Key.Ctrl);
            if (Burden > 0) sprint = false;
            if (Input.IsKeyPressed(Key.Space) && IsOnFloor() && !Crouching && Burden <= 0) Velocity = Velocity with { Y = JumpSpeed };
        }
        if (move.LengthSquared() > 1) move = move.Normalized();
        bool walk = Autopilot == null && Input.IsKeyPressed(Key.Alt);
        float top = Crouching ? CrouchSpeed : walk ? WalkSpeed : sprint && move.Y > 0.3f ? SprintSpeed : RunSpeed;
        if (Burden > 0) top = MathF.Min(top, 2.1f);
        // camera-relative: forward is where the camera looks
        var fwd = new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
        var right = new Vector3(-fwd.Z, 0, fwd.X);
        var want = (fwd * move.Y + right * move.X) * top;
        var v = Velocity;
        float accel = IsOnFloor() ? 14f : 3f;
        var flat = new Vector3(v.X, 0, v.Z).MoveToward(want, accel * dt);
        v = new Vector3(flat.X, IsOnFloor() && v.Y < 0 ? -0.5f : v.Y - Gravity * dt, flat.Z);
        Velocity = v;
        StepUp(flat);
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
        if (Dead) { UpdateDeath((float)delta); return; }
        UpdateLean((float)delta);
        UpdateCamera((float)delta);
        _cycle?.Update((float)delta, Speed, Crouching, IsOnFloor());
        UpdateAnimations((float)delta);
        UpdateThirdPersonGun((float)delta);
        if (_skel != null) CharacterRig.GroundFeet(_model, _skel, _crouchBlend, (float)delta);
    }

    private void UpdateCamera(float dt)
    {
        float eye = Crouching ? 1.05f : 1.55f;
        var right = new Vector3(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));
        var target = GlobalPosition + new Vector3(0, eye, 0) + right * (LeanOffset * 0.8f);
        _pivot.GlobalPosition = dt > 0 ? _pivot.GlobalPosition.Lerp(target, 1f - MathF.Exp(-18f * dt)) : target;
        _pivot.Rotation = new Vector3(_pitch, Yaw + CameraOrbit, 0);
        // third person: leaning left swaps the camera to the left shoulder, so you see round the corner you lean at
        _arm.Position = new Vector3(Mathf.Lerp(0.45f, -0.45f, Mathf.Clamp(-Lean, 0, 1)), 0, 0);
        // first person: the eye, turned with the look (the body follows the yaw); leaning moves it sideways and a
        // little down, and tilts the view
        float up = Crouching ? 1.12f : 1.66f;
        var fwd = new Vector3(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
        _fp.GlobalPosition = GlobalPosition + Vector3.Up * (up - MathF.Abs(LeanOffset) * 0.15f) + fwd * 0.12f + right * LeanOffset;
        _fp.GlobalRotation = new Vector3(_pitch + AimSway.Y, Yaw + AimSway.X, -LeanRoll * Lean * MathF.Min(1f, _leanRoom / LeanReach));
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

/// <summary>
/// The lean in the body (D55): bends spine, chest and neck about the way the body faces, after the animation and
/// before the hands are put on the gun. <see cref="Angle"/> in radians, positive to the right.
/// </summary>
public sealed partial class LeanBendModifier : SkeletonModifier3D
{
    public float Angle;
    private readonly Func<Vector3> _forward;
    private readonly (int Bone, float Share)[] _bones;

    public LeanBendModifier(Skeleton3D s, Func<Vector3> forward)
    {
        Name = "LeanBend";
        _forward = forward;
        _bones = new[] { ("Spine", 0.4f), ("Chest", 0.4f), ("Neck", 0.2f) }
            .Select(b => (s.FindBone(b.Item1), b.Item2)).Where(b => b.Item1 >= 0).ToArray();
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (MathF.Abs(Angle) < 0.002f || GetSkeleton() is not { } sk) return;
        var axis = (sk.GlobalBasis.Orthonormalized().Inverse() * _forward()).Normalized();   // in the skeleton's space
        foreach (var (bone, share) in _bones)
        {
            int parent = sk.GetBoneParent(bone);
            var parentBasis = parent >= 0 ? sk.GetBoneGlobalPose(parent).Basis.Orthonormalized() : Basis.Identity;
            var local = (parentBasis.Inverse() * axis).Normalized();
            sk.SetBonePoseRotation(bone, new Quaternion(local, Angle * share) * sk.GetBonePoseRotation(bone));
        }
    }
}
