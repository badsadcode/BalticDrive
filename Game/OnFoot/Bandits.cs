using System;
using System.Collections.Generic;
using System.Linq;
using BalticDrive.Map;
using BalticDrive.World;
using Godot;

namespace BalticDrive.OnFoot;

/// <summary>
/// A hostile survivor (D47, the brief: "patrol or guard, notice the player, shoot from cover when possible, react to
/// hits, and investigate loud gunfire"; no bullet sponges, no perfect aim). A Synty City character on the shared rig,
/// carrying one of the three guns, held by the shared arm IK on top of the walking animation.
/// <list type="bullet">
/// <item>Patrol: wanders slowly round the group's spot.</item>
/// <item>Senses, four times a second: sight (70 m, a 120° view, a clear line to your head; awareness builds faster
/// up close and slower if you crouch) and hearing (your shots within 150 m send them to look).</item>
/// <item>Engage: run to cover (a spot within 12 m where a crouching body is hidden from you), crouch, then stand and
/// fire a burst, crouch again; move to new cover now and then, and after a hit.</item>
/// <item>Aim is never perfect: the spread grows with range, with your speed, and after they've been hit; it tightens a
/// little as they settle on you. The first shot comes 0.6–1.1 s after they see you.</item>
/// <item>Hits: head ×2.5; a flinch spoils their aim; at 0 they fall and leave a few things to search (E).</item>
/// </list>
/// </summary>
public sealed partial class Bandit : CharacterBody3D, IHittable
{
    public enum Mode { Patrol, Investigate, Engage, Dead }
    public Mode State { get; private set; } = Mode.Patrol;
    public float Health = 90;
    public readonly WeaponDef Gun;
    public Vector3 Home;
    /// <summary>The lead whose stash this one guards (D54), 0 for none.</summary>
    public int Guarding;
    /// <summary>Synty City characters in everyday clothes (not the police one: a bandit in uniform would mislead).</summary>
    public static readonly string[] Looks = { "Character_Male_Jacket", "Character_Female_Jacket", "Character_Female_Coat" };

    private readonly BanditField _field;
    private readonly Random _rng;
    private Node3D _model = null!, _holder = null!;
    private AnimationTree? _tree;
    private WalkCycle? _cycle;
    private GunHold? _hold;
    private OmniLight3D _flash = null!;
    private float _awareness, _think, _reaction, _cooldown, _flinch, _settle, _airTime, _crouchBlend, _deadTime, _stuck;
    private int _burstLeft;
    private Vector3 _moveTo, _lastSeen, _lookAt;
    private bool _seesPlayer, _crouch, _haveCover, _running, _coverFound;
    private float _coverTimer, _peekTimer, _yaw;
    private float _fall;

    /// <summary>Puts the bandit down at <paramref name="at"/>, with its patrol spot (call after adding it).</summary>
    public void Place(Vector3 at, Vector3 home)
    {
        GlobalPosition = at;
        Home = home;
        _moveTo = at;   // (it used to start walking to the map's origin)
    }

    public Bandit(BanditField field, WeaponDef gun, int seed)
    {
        _field = field;
        Gun = gun;
        _rng = new Random(seed);
        Name = "Bandit";
    }

    public override void _Ready()
    {
        CollisionLayer = Bodies.Characters;
        CollisionMask = Bodies.Ground | Bodies.Roads | Bodies.Buildings | Bodies.Vehicles | Bodies.Props | Bodies.Characters;
        FloorMaxAngle = Mathf.DegToRad(50);
        FloorSnapLength = 0.4f;
        SetMeta("surface", "flesh");
        AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.33f, Height = 1.8f }, Position = new Vector3(0, 0.9f, 0) });
        _model = new Node3D { Name = "Model" };
        AddChild(_model);
        var (skel, tree) = CharacterRig.Build(_model, Looks[_rng.Next(Looks.Length)], out _cycle);
        _tree = tree;
        _skel = skel;
        _headBone = skel?.FindBone("Head") ?? -1;
        if (skel == null)
            _model.AddChild(new MeshInstance3D { Mesh = new CapsuleMesh { Radius = 0.33f, Height = 1.8f }, Position = new Vector3(0, 0.9f, 0),
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.4f, 0.25f, 0.2f) } });
        // the gun, as the player's: the model turned to look down -Z and scaled to metres
        _holder = new Node3D { Name = "Gun" };
        AddChild(_holder);
        if (GunModel.Make(Gun) is { } model) _holder.AddChild(model);
        _flash = new OmniLight3D { LightColor = new Color(1f, 0.75f, 0.4f), LightEnergy = 0, OmniRange = 5 };
        _holder.AddChild(_flash);
        _flash.Position = new Vector3(-Gun.Muzzle.X, Gun.Muzzle.Y, -Gun.Muzzle.Z) * Gun.Scale;
        if (skel != null)
        {
            _hold = new GunHold(skel, () => GunHold.Goals(_holder, Gun));
            skel.AddChild(_hold);
        }
        _yaw = (float)(_rng.NextDouble() * Math.Tau);
        _think = (float)_rng.NextDouble() * 0.25f;
    }

    // ---- what the player's bullets do -------------------------------------------------------------------------------

    public string? Hit(BulletHit h)
    {
        if (State == Mode.Dead) return null;
        float head = (h.Point.Y - GlobalPosition.Y) / (_crouch ? 1.15f : 1.8f);
        bool headshot = head > 0.86f;
        Health -= h.Damage * (headshot ? 2.5f : 1f);
        _flinch = 0.4f;
        _settle = 0;
        _lastSeen = h.Point - h.Velocity.Normalized() * h.Range;   // where it came from
        _awareness = 1;
        if (State != Mode.Engage) Engage();
        _haveCover = false;                      // hit: find new cover
        if (Health <= 0) { Die(h); return headshot ? "bandit down (head)" : "bandit down"; }
        return headshot ? "bandit, head" : "bandit, hit";
    }

    private void Die(BulletHit h)
    {
        State = Mode.Dead;
        CollisionLayer = 0;
        CollisionMask = Bodies.Ground | Bodies.Roads | Bodies.Buildings;
        // the animation keeps running (switching it off, or freezing it, drops the body into its T-pose): standing
        // still, slumped into the crouch, arms down; the hands let go of the gun
        if (_tree != null) CharacterRig.Animate(_tree, Vector2.Zero, 1f, 0f);
        if (_hold != null) _hold.Active = false;
        _fall = 0;
        _flash.LightEnergy = 0;
        // the body tips away from the shot
        var away = new Vector3(h.Velocity.X, 0, h.Velocity.Z).Normalized();
        _fallAxis = away.Cross(Vector3.Up).Normalized();
        _field.Died(this);
    }
    private Vector3 _fallAxis = Vector3.Right;

    // ---- senses and decisions ---------------------------------------------------------------------------------------

    /// <summary>Gunfire heard (the player's or a mate's).</summary>
    public void Heard(Vector3 at, float distance)
    {
        if (State is Mode.Dead or Mode.Engage) return;
        _lastSeen = at;
        _awareness = MathF.Max(_awareness, distance < 40 ? 0.8f : 0.4f);
        State = Mode.Investigate;
        _moveTo = at + new Vector3((float)_rng.NextDouble() * 10 - 5, 0, (float)_rng.NextDouble() * 10 - 5);
        _running = distance < 60;
    }

    private void Engage()
    {
        State = Mode.Engage;
        _reaction = 0.6f + (float)_rng.NextDouble() * 0.5f;
        _haveCover = false;
        _field.Alert(this, _lastSeen);
    }

    /// <summary>Called by the field when a mate starts shooting: come and help.</summary>
    public void MateEngaged(Vector3 enemy)
    {
        if (State is Mode.Dead or Mode.Engage) return;
        _lastSeen = enemy;
        _awareness = 1;
        Engage();
    }

    /// <summary>Where the bandit looks from: the head as the animation has it (a guessed height, switched the moment
    /// it decided to crouch, put the gun at its chest while the body was still standing).</summary>
    private Vector3 Eye
    {
        get
        {
            if (_skel != null && _headBone >= 0)
                return _skel.GlobalTransform * _skel.GetBoneGlobalPose(_headBone).Origin + Vector3.Up * 0.06f;
            return GlobalPosition + Vector3.Up * (_crouch ? 1.05f : 1.6f);
        }
    }
    private Skeleton3D? _skel;
    private int _headBone = -1;

    private void Sense(BanditField.Target? target)
    {
        _seesPlayer = false;
        if (target is not { } t) { if (State == Mode.Engage) State = Mode.Investigate; return; }
        var head = t.Head;
        var to = head - Eye;
        float d = to.Length();
        if (d > 70) return;
        var facing = new Vector3(-MathF.Sin(_yaw), 0, -MathF.Cos(_yaw));
        bool inView = d < 8 || facing.Dot(new Vector3(to.X, 0, to.Z).Normalized()) > MathF.Cos(MathF.PI * 60 / 180);
        if (!inView && State != Mode.Engage) return;
        if (!Clear(Eye, head, t.Body)) return;
        _seesPlayer = true;
        _lastSeen = t.Aim;
        if (State != Mode.Engage)
        {
            // awareness builds faster up close; crouching halves it
            // a car is noticed sooner (it's big and loud), a crouching walker later
            float rate = (d < 15 ? 3f : d < 35 ? 1.4f : 0.7f) * (t.Low ? 0.5f : 1f) * (t.InCar ? 1.6f : 1f);
            _awareness += rate * 0.25f;
            _lookAt = head;
            if (_awareness >= 1) Engage();
        }
    }

    /// <summary>Nothing in the way between two points (the player's own body doesn't count).</summary>
    private bool Clear(Vector3 a, Vector3 b, Rid ignore = default)
    {
        var q = PhysicsRayQueryParameters3D.Create(a, b, Bodies.Ground | Bodies.Roads | Bodies.Buildings | Bodies.Vehicles | Bodies.Props);
        q.Exclude = ignore.IsValid ? new Godot.Collections.Array<Rid> { GetRid(), ignore } : new Godot.Collections.Array<Rid> { GetRid() };
        return GetWorld3D().DirectSpaceState.IntersectRay(q).Count == 0;
    }

    /// <summary>A spot within 12 m where a crouching body is hidden from <paramref name="enemy"/> but standing up
    /// would see over: behind a wall corner, a car, a hedge.</summary>
    private Vector3? FindCover(Vector3 enemy)
    {
        Vector3? best = null; float bestScore = float.MaxValue;
        for (int i = 0; i < 18; i++)
        {
            float a = (float)(_rng.NextDouble() * Math.Tau), r = 3 + (float)_rng.NextDouble() * 9;
            var p = GlobalPosition + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
            var q = PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 3, p + Vector3.Down * 6, Bodies.Ground | Bodies.Roads);
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(q);
            if (hit.Count == 0) continue;
            p = (Vector3)hit["position"];
            if (p.DistanceTo(enemy) < 7) continue;
            if (Clear(p + Vector3.Up * 1.0f, enemy)) continue;          // a crouch must be hidden
            float score = p.DistanceTo(GlobalPosition) + (Clear(p + Vector3.Up * 1.6f, enemy) ? 0 : 6);   // better if standing sees
            if (score < bestScore) { bestScore = score; best = p; }
        }
        return best;
    }

    // ---- per frame ----------------------------------------------------------------------------------------------------

    public override void _PhysicsProcess(double delta)
    {
        float dt = (float)delta;
        var target = _field.Current;
        if (State == Mode.Dead) { Fall(dt); return; }

        _think -= dt;
        if (_think <= 0) { _think = 0.25f; Sense(target); }
        _flinch -= dt; _cooldown -= dt; _reaction -= dt;
        Vector3 aimAt = _lookAt;
        bool shoulder = false;

        switch (State)
        {
            case Mode.Patrol:
                _running = false; _crouch = false;
                if (GlobalPosition.DistanceTo(_moveTo) < 1.2f || _stuck > 1.5f)
                {
                    _stuck = 0;
                    if (_rng.NextDouble() < 0.02) break;   // stand a while
                    float a = (float)(_rng.NextDouble() * Math.Tau), r = (float)_rng.NextDouble() * 12;
                    _moveTo = Home + new Vector3(MathF.Cos(a) * r, 0, MathF.Sin(a) * r);
                }
                aimAt = _moveTo + Vector3.Up * 1.4f;
                _awareness = MathF.Max(0, _awareness - dt * 0.05f);
                break;
            case Mode.Investigate:
                _crouch = false;
                aimAt = _lastSeen;
                shoulder = true;
                if (GlobalPosition.DistanceTo(_moveTo) < 2f || _stuck > 2f)
                {
                    _awareness -= dt * 0.1f;
                    if (_awareness <= 0.1f) { State = Mode.Patrol; Home = GlobalPosition; }
                }
                break;
            case Mode.Engage:
                shoulder = true;
                aimAt = _lastSeen;
                if (target == null) { State = Mode.Investigate; break; }
                if (!_haveCover || _coverTimer <= 0)
                {
                    var c = FindCover(_lastSeen);
                    _haveCover = true;
                    _coverTimer = 9 + (float)_rng.NextDouble() * 6;
                    _coverFound = c != null;
                    if (c is { } cv) { _moveTo = cv; _running = true; } else _moveTo = GlobalPosition;
                }
                _coverTimer -= dt;
                bool there = GlobalPosition.DistanceTo(_moveTo) < 1.0f || _stuck > 1.5f;
                if (there)
                {
                    _running = false;
                    // in cover: crouch, then up to fire a burst, then down again
                    _peekTimer -= dt;
                    if (_burstLeft <= 0 && _peekTimer <= 0)
                    {
                        _crouch = !_crouch;
                        _peekTimer = _crouch ? 1.2f + (float)_rng.NextDouble() * 1.6f : 2.2f;
                        if (!_crouch) _burstLeft = Burst();
                    }
                }
                else _crouch = false;
                // fire when standing (or moving) with a clear line
                if (!_crouch && _seesPlayer && _reaction <= 0 && _flinch <= 0 && _cooldown <= 0 && (_burstLeft > 0 || !there))
                {
                    Shoot(target.Value);
                    if (_burstLeft > 0) _burstLeft--;
                    if (_burstLeft == 0 && there) { _peekTimer = 0.3f; }
                    // caught in the open (no cover): a breath between bursts, not a hose
                    if (_burstLeft == 0 && !_coverFound) _cooldown = 1.5f + (float)_rng.NextDouble();
                }
                break;
        }
        _lookAt = aimAt;

        // walking: straight at the spot, a sidestep when stuck
        var to = _moveTo - GlobalPosition; to.Y = 0;
        float speed = State == Mode.Patrol ? CharacterRig.WalkSpeed * 0.8f : _running ? CharacterRig.RunSpeed : CharacterRig.WalkSpeed;
        if (_crouch) speed = 0;
        var want = to.Length() > 0.6f ? to.Normalized() * speed : Vector3.Zero;
        var v = Velocity;
        var flat = new Vector3(v.X, 0, v.Z).MoveToward(want, 10f * dt);
        v = new Vector3(flat.X, IsOnFloor() && v.Y < 0 ? -0.5f : v.Y - 14f * dt, flat.Z);
        Velocity = v;
        MoveAndSlide();
        float moved = new Vector2(Velocity.X, Velocity.Z).Length();
        _stuck = want.LengthSquared() > 0.1f && moved < 0.3f ? _stuck + dt : 0;

        // face where they look when shouldering, else where they walk
        var faceTo = shoulder ? aimAt - GlobalPosition : moved > 0.3f ? Velocity : Vector3.Zero;
        if (new Vector2(faceTo.X, faceTo.Z).LengthSquared() > 0.01f)
            _yaw = Mathf.LerpAngle(_yaw, MathF.Atan2(-faceTo.X, -faceTo.Z), 1f - MathF.Exp(-8f * dt));
        Rotation = new Vector3(0, _yaw, 0);

        PlaceGun(shoulder, aimAt, dt);
        Animate(dt);
    }

    private int Burst() => Gun.Mode switch
    {
        FireMode.Auto => 3 + _rng.Next(3),
        FireMode.Semi => 2 + _rng.Next(2),
        _ => 1,
    };

    private void Shoot(BanditField.Target t)
    {
        _cooldown = Gun.Mode switch { FireMode.Auto => 60f / Gun.Rpm, FireMode.Semi => 0.35f + (float)_rng.NextDouble() * 0.2f, _ => 2.2f };
        var muzzle = _holder.ToGlobal(new Vector3(-Gun.Muzzle.X, Gun.Muzzle.Y, -Gun.Muzzle.Z) * Gun.Scale);
        var target = t.Aim;
        var dir = (target - muzzle).Normalized();
        float d = muzzle.DistanceTo(target);
        // never perfect: range, the player's speed, a recent hit; settling on the target tightens it a little
        // the first burst of a fight goes wide (surprise); then range, the player's speed, a recent hit
        float spread = 0.02f + d * 0.0006f + (t.Speed > 3 ? 0.02f : 0) + (t.Speed > 12 ? 0.02f : 0) + (_flinch > -1 ? 0.025f : 0) + (_settle < 3 ? 0.06f : 0);
        spread *= Gun.Mode == FireMode.Bolt ? 0.45f : Gun.Mode == FireMode.Semi ? 1.4f : 1f;
        spread *= Mathf.Lerp(1.3f, 0.75f, Mathf.Clamp(_settle / 4f, 0, 1));
        _settle += 1;
        var side = dir.Cross(Vector3.Up).Normalized(); var up = side.Cross(dir);
        float a = (float)(_rng.NextDouble() * Math.Tau), r = spread * MathF.Sqrt((float)_rng.NextDouble());
        dir = (dir + side * MathF.Cos(a) * r + up * MathF.Sin(a) * r).Normalized();
        // a bandit's bullet does 30 % of what the same gun does to them: a fight you can survive (an AK hit 10, the Mosin 28)
        Ballistics.Get(this).Fire(muzzle, dir * Gun.MuzzleVelocity, Gun.BulletGrams / 1000f, Gun.Damage * 0.3f, GetRid());
        Audio.GunSound.Play(this, muzzle, Gun, own: false);   // late by distance, a boom from afar, echoes (D53)
        _flash.LightEnergy = 5;
        _flashOff = 0.05f;
        _field.ShotFired(muzzle, this);
    }
    private float _flashOff;

    /// <summary>The gun: at the cheek, pointing at the target, when shouldered; low and forward otherwise.</summary>
    private void PlaceGun(bool shoulder, Vector3 aimAt, float dt)
    {
        _flashOff -= dt;
        if (_flashOff <= 0) _flash.LightEnergy = 0;
        var eye = Eye;
        var look = shoulder ? (aimAt - eye).Normalized() : new Vector3(-MathF.Sin(_yaw), -0.7f, -MathF.Cos(_yaw)).Normalized();
        var basis = Basis.LookingAt(look, Vector3.Up);
        var sight = new Vector3(-Gun.Sight.X, Gun.Sight.Y, -Gun.Sight.Z) * Gun.Scale;
        var offset = shoulder ? new Vector3(0, 0, -Gun.Eye) - sight + new Vector3(0.0f, -0.02f, 0)
                              : new Vector3(0.12f, -0.25f, -0.3f);
        var t = new Transform3D(basis, eye + basis * offset);
        _holder.GlobalTransform = _holder.GlobalTransform.InterpolateWith(t, 1f - MathF.Exp(-14f * dt));
    }


    private void Animate(float dt)
    {
        var local = Transform.Basis.Inverse() * Velocity;
        _crouchBlend = Mathf.MoveToward(_crouchBlend, _crouch ? 1f : 0f, dt * 5f);
        _airTime = IsOnFloor() ? 0f : _airTime + dt;
        if (_tree != null) CharacterRig.Animate(_tree, new Vector2(local.X, -local.Z), _crouchBlend, Mathf.Clamp((_airTime - 0.12f) * 6f, 0f, 1f));
        _cycle?.Update(dt, new Vector2(Velocity.X, Velocity.Z).Length(), _crouch, IsOnFloor());
        if (_skel != null) CharacterRig.GroundFeet(_model, _skel, _crouchBlend, dt);
    }

    private void Fall(float dt)
    {
        _deadTime += dt;
        if (_fall < 1)
        {
            _fall = MathF.Min(1, _fall + dt / 0.55f);
            float e = _fall * _fall;
            // the body pivots about the feet onto its back (or front), the gun drops
            _model.Basis = new Basis(Transform.Basis.Inverse() * _fallAxis, e * 1.45f) * Basis.FromEuler(new Vector3(0, MathF.PI, 0));
            _holder.GlobalPosition = _holder.GlobalPosition.Lerp(GlobalPosition + Vector3.Up * 0.08f + _fallAxis.Cross(Vector3.Up) * 0.6f, dt * 6);
            _holder.Rotation = _holder.Rotation.Lerp(new Vector3(0, _holder.Rotation.Y, MathF.PI / 2), dt * 6);
        }
        Velocity = new Vector3(0, IsOnFloor() ? 0 : Velocity.Y - 14f * dt, 0);
        MoveAndSlide();
    }

    public bool Dead => State == Mode.Dead;
    /// <summary>Self-tests: the gun's height above the feet, and the head bone's.</summary>
    public string Heights()
    {
        var sk = _model.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().FirstOrDefault();
        float head = sk == null ? -1 : (sk.GlobalTransform * sk.GetBoneGlobalPose(sk.FindBone("Head")).Origin).Y - GlobalPosition.Y;
        return $"gun {_holder.GlobalPosition.Y - GlobalPosition.Y:F2} m, head {head:F2} m, eye used {Eye.Y - GlobalPosition.Y:F2} m, crouch {_crouch}";
    }
    public float DeadFor => _deadTime;
}

/// <summary>
/// Puts a character's hands on its gun after the walking animation has posed the skeleton (D47; bandits, and the
/// walker in third person, D49).
/// </summary>
public sealed partial class GunHold : SkeletonModifier3D
{
    private readonly ArmIk _right, _left;
    private readonly Func<(HandGoal Right, HandGoal Left)?> _goals;

    public GunHold(Skeleton3D s, Func<(HandGoal Right, HandGoal Left)?> goals)
    {
        _goals = goals;
        _right = new ArmIk(s, "Right");
        _left = new ArmIk(s, "Left");
        Name = "GunHold";
    }

    public override void _ProcessModificationWithDelta(double delta)
    {
        if (!_right.Ok || !_left.Ok || _goals() is not { } g) return;
        _right.Solve(g.Right);
        _left.Solve(g.Left);
    }

    /// <summary>The hands' goals for a gun held by <paramref name="holder"/> (barrel along the holder's -Z): the right
    /// on the grip, the left on the handguard, or cupping the right on a pistol.</summary>
    public static (HandGoal Right, HandGoal Left) Goals(Node3D holder, WeaponDef gun)
    {
        var b = holder.GlobalTransform.Basis.Orthonormalized();
        Vector3 F = -b.Z, U = b.Y, R = b.X;
        Vector3 Local(Vector3 m) => holder.ToGlobal(new Vector3(-m.X, m.Y, -m.Z) * gun.Scale);
        var right = new HandGoal(Local(gun.RightGrip), F - U * 0.3f, -R, -U + R * 0.8f + b.Z * 0.3f, 0.9f);
        var left = gun.TwoHandedPistol
            ? new HandGoal(Local(gun.LeftGrip), F * 0.8f + R * 0.3f - U * 0.25f, R, -U - R * 0.6f, 0.85f)
            : new HandGoal(Local(gun.LeftGrip), F * 0.7f + R * 0.5f, U * 0.8f + R * 0.4f, -U - R * 0.5f, 0.8f);
        return (right, left);
    }
}

/// <summary>A gun's model for someone to hold (D47, D49): turned to look down -Z, scaled to metres, parts squared up
/// (D40), no collision, muzzle flash part hidden.</summary>
public static class GunModel
{
    public static Node3D? Make(WeaponDef gun)
    {
        if (!ResourceLoader.Exists(gun.Scene) || ResourceLoader.Load<PackedScene>(gun.Scene) is not { } ps) return null;
        var inst = ps.Instantiate<Node3D>();
        foreach (var body in inst.FindChildren("*", "CollisionObject3D", true, false)) body.Free();
        foreach (var part in inst.GetChildren().OfType<MeshInstance3D>())
            if (!part.Basis.Orthonormalized().IsEqualApprox(Basis.Identity)) part.Basis = Basis.Identity;
        if (gun.Texture != null && ResourceLoader.Load<Texture2D>(gun.Texture) is { } tex)
        {
            var mat = new StandardMaterial3D { AlbedoTexture = tex, Roughness = 0.6f };
            foreach (var mi in inst.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>()) mi.MaterialOverride = mat;
        }
        if (inst.FindChild("*Flash*", true, false) is Node3D flash) flash.Visible = false;
        var model = new Node3D { Scale = Vector3.One * gun.Scale, RotationDegrees = new Vector3(0, 180, 0) };
        model.AddChild(inst);
        return model;
    }
}

/// <summary>
/// Bandits around the player (D47; the brief: "sparse, local, and generated around the player", "a few meaningful
/// encounters beat hundreds of bots"). On foot, every 10 s: if no group is near and none was met in the last two
/// minutes, sometimes (one in three) a group of two or three appears 90–150 m away, on open ground out of your sight,
/// and patrols there. They hear your shots, call each other in, and leave a little loot when they fall. Far groups
/// and long-dead bodies are removed. B spawns a group 45 m ahead (to test, or for fun).
/// </summary>
public sealed partial class BanditField : Node3D
{
    private readonly MapWorld _world;
    private readonly CacheField? _caches;
    private readonly List<Bandit> _all = new();
    private readonly Random _rng = new();
    private double _nextLook, _lastEncounter = -999;
    public Walker? Player;
    /// <summary>The player's car (D52): while you drive, the bandits come for the car.</summary>
    public Vehicle.Car? Car;

    /// <summary>Who the bandits are after: the walker on foot, or the car you're driving (D52). Feet, head (what
    /// they look for), aim point, speed, low (crouching), the body their rays mustn't stop on, in a car.</summary>
    public readonly record struct Target(Vector3 Feet, Vector3 Head, Vector3 Aim, float Speed, bool Low, Rid Body, bool InCar);

    public Target? Current
    {
        get
        {
            if (Player is { Active: true } p)
                return new Target(p.GlobalPosition, p.FirstPersonCamera.GlobalPosition, p.GlobalPosition + Vector3.Up * (p.Crouching ? 0.8f : 1.25f),
                    p.Speed, p.Crouching, p.GetRid(), false);
            if (Car is { Driving: true } c && IsInstanceValid(c))
                return new Target(c.GlobalPosition, c.GlobalPosition + Vector3.Up * 1.3f, c.GlobalPosition + Vector3.Up * 0.9f,
                    MathF.Abs(c.Speed), false, c.GetRid(), true);
            return null;
        }
    }
    public IReadOnlyList<Bandit> All => _all;
    public void Clear() => _all.Clear();
    public void Remove(Bandit b) => _all.Remove(b);
    public int Killed;

    public BanditField(MapWorld world, CacheField? caches)
    {
        _world = world; _caches = caches; Name = "Bandits";
        Weapons.Shot += OnPlayerShot;
    }

    public override void _Ready() => WorldOrigin.Shifted += OnShifted;
    public override void _ExitTree() { WorldOrigin.Shifted -= OnShifted; Weapons.Shot -= OnPlayerShot; }

    private void OnShifted(double dx, double dz)
    {
        var d = new Vector3((float)dx, 0, (float)dz);
        foreach (var b in _all) { b.GlobalPosition -= d; b.Home -= d; }
    }

    private void OnPlayerShot(Vector3 at, WeaponDef gun) => ShotFired(at, null);

    /// <summary>A shot rang out: everyone in earshot reacts (150 m).</summary>
    public void ShotFired(Vector3 at, Bandit? by)
    {
        foreach (var b in _all)
        {
            if (b == by || b.Dead) continue;
            float d = b.GlobalPosition.DistanceTo(at);
            if (d < 150) b.Heard(by == null ? at : (Current?.Feet ?? at), d);
        }
    }

    /// <summary>One of them has seen the player: the mates within 60 m join in.</summary>
    public void Alert(Bandit who, Vector3 enemy)
    {
        _lastEncounter = Time.GetTicksMsec() / 1000.0;
        foreach (var b in _all)
            if (b != who && !b.Dead && b.GlobalPosition.DistanceTo(who.GlobalPosition) < 60) b.MateEngaged(enemy);
    }

    /// <summary>A bandit went down (after their pockets are left to search): leads count guards (D54).</summary>
    public event Action<Bandit>? Fell;

    public void Died(Bandit b)
    {
        Killed++;
        Fell?.Invoke(b);
        // what they had on them: a few rounds for their gun, sometimes food or a medkit
        var loot = new Loot(_rng.NextDouble() < 0.4 ? 1 : 0, "tin of beans", _rng.NextDouble() < 0.3 ? 1 : 0, _rng.NextDouble() < 0.25 ? 1 : 0,
            new[] { (b.Gun.Id, b.Gun.Mode == FireMode.Auto ? 20 : b.Gun.Mode == FireMode.Semi ? 10 : 5) });
        _caches?.Drop(b.GlobalPosition, loot, "the bandit's pockets");
    }

    public void Update(double now)
    {
        if (Current is not { } t) return;
        var at = t.Feet;
        foreach (var b in _all.Where(b => b.GlobalPosition.DistanceTo(at) > 320 || (b.Dead && b.DeadFor > 120)).ToList())
        {
            _all.Remove(b);
            b.QueueFree();
        }
        if (now < _nextLook) return;
        _nextLook = now + 8;
        // a little more often than at first (Tom: "not that many"), but never a horde: at most four alive near you
        if (_all.Count(b => !b.Dead && b.GlobalPosition.DistanceTo(at) < 250) >= 2) return;
        if (now - _lastEncounter < 60) return;
        if (_rng.NextDouble() > 0.5) return;
        if (t.InCar)
        {
            // driving (D52): somewhere ahead along the way you're going, 140-200 m, off the road
            var fwd = Car!.LinearVelocity with { Y = 0 };
            if (fwd.LengthSquared() < 4f) fwd = -Car.GlobalTransform.Basis.Z with { Y = 0 };
            float a = MathF.Atan2(fwd.Z, fwd.X) + (float)(_rng.NextDouble() - 0.5) * 1.2f, d = 140 + (float)_rng.NextDouble() * 60;
            if (Spot(at + new Vector3(MathF.Cos(a) * d, 0, MathF.Sin(a) * d), hidden: false) is { } q) SpawnGroup(q);
            return;
        }
        float yaw = (float)(_rng.NextDouble() * Math.Tau), r = 90 + (float)_rng.NextDouble() * 60;
        if (Spot(at + new Vector3(MathF.Cos(yaw) * r, 0, MathF.Sin(yaw) * r), hidden: true) is { } p) SpawnGroup(p);
    }

    /// <summary>B: a group 45 m ahead, in view.</summary>
    public int SpawnAhead(Vector3 from, float yaw, float distance = 45)
    {
        var fwd = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        for (int i = 0; i < 6; i++)
            if (Spot(from + fwd * distance + fwd.Cross(Vector3.Up) * (i * 4 - 10), hidden: false) is { } p) return SpawnGroup(p);
        return 0;
    }

    /// <summary>A gang camped round a stash (D54): <paramref name="n"/> of them within ~10 m, mostly AKs, one Mosin
    /// watching the approach.</summary>
    public int SpawnGuards(Vector3 at, int n, int lead) => SpawnGroup(at, n, lead, spread: 18);

    public int GuardsOf(int lead) => _all.Count(b => b.Guarding == lead && !b.Dead);

    /// <summary>The player walked off: the stash's guards go with it (the living ones are counted by the lead).</summary>
    public void Dismiss(int lead)
    {
        foreach (var b in _all.Where(b => b.Guarding == lead).ToList()) { _all.Remove(b); b.QueueFree(); }
    }

    private int SpawnGroup(Vector3 at, int n = 0, int lead = 0, float spread = 6)
    {
        if (n <= 0) n = 2 + (_rng.NextDouble() < 0.4 ? 1 : 0);
        int made = 0;
        for (int i = 0; i < n; i++)
        {
            double roll = _rng.NextDouble();
            string id = lead != 0 ? (i == 0 ? "mosin" : roll < 0.7 ? "ak" : "pistol") : roll < 0.5 ? "ak" : roll < 0.8 ? "pistol" : "mosin";
            var gun = WeaponDef.All.First(d => d.Id == id);
            var p = Spot(at + new Vector3((float)_rng.NextDouble() * spread - spread / 2, 0, (float)_rng.NextDouble() * spread - spread / 2), false) ?? at;
            var b = new Bandit(this, gun, _rng.Next()) { Guarding = lead };
            AddChild(b);
            b.Place(p + Vector3.Up * 0.2f, at);
            _all.Add(b);
            made++;
        }
        return made;
    }

    /// <summary>Open ground near a point (not in a building or on a carriageway), optionally out of the player's sight.</summary>
    private Vector3? Spot(Vector3 near, bool hidden)
    {
        var space = GetWorld3D().DirectSpaceState;
        for (int k = 0; k < 12; k++)
        {
            var p = near + new Vector3((float)_rng.NextDouble() * 16 - 8, 0, (float)_rng.NextDouble() * 16 - 8) * (k == 0 ? 0 : 1);
            var (wx, _, wz) = WorldOrigin.ToWorld(p);
            if (_world.InBuilding(wx, wz, 1f) || _world.RoadEdgeDistance(wx, wz, 6f) < 0.3f) continue;
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 300, p + Vector3.Down * 300, Bodies.Ground | Bodies.Roads));
            if (hit.Count == 0) continue;
            var g = (Vector3)hit["position"];
            if (hidden && Current is { } tt)
            {
                var q = PhysicsRayQueryParameters3D.Create(tt.Head, g + Vector3.Up * 1.5f, Bodies.Ground | Bodies.Buildings | Bodies.Roads);
                if (space.IntersectRay(q).Count == 0) continue;   // in plain view: not here
            }
            return g;
        }
        return null;
    }
}
