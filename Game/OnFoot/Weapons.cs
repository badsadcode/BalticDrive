using System;
using System.Collections.Generic;
using System.Linq;
using BalticDrive.World;
using Godot;

namespace BalticDrive.OnFoot;

/// <summary>How a gun fires (D40).</summary>
public enum FireMode { Semi, Auto, Bolt }

/// <summary>
/// One gun's identity (D40, the on-foot brief: a Mosin-style bolt-action, an AK-style rifle, a pistol). Model space:
/// the barrel along +Z, Y up (Synty's convention); <see cref="Sight"/> is the point on the rear sight that the eye
/// looks along when aiming; <see cref="Muzzle"/> the barrel's end; both in the model's own units.
/// </summary>
public sealed record WeaponDef(
    string Id, string Name, string Scene, float Scale, FireMode Mode, float Rpm, float MuzzleVelocity,
    int Magazine, int Reserve, float ReloadTime, float RecoilPitch, float RecoilYaw, float Kick, float AimFov,
    Vector3 Hip, Vector3 Sight, Vector3 Muzzle, Vector3 Ejection, string? Slide, string? Mag, string? Bolt, string? Flash,
    float Boom, float Pitch, string? Texture = null, float Eye = 0.18f,
    Vector3 RightGrip = default, Vector3 LeftGrip = default, bool TwoHandedPistol = false, float BulletGrams = 8f, float Damage = 30f)
{
    public static readonly WeaponDef[] All =
    {
        new("mosin", "Mosin rifle", "res://Assets/Synty/PolygonWar/SM_Wep_Russian_Rifle_01.fbx", 0.111f, FireMode.Bolt, 50, 860,
            5, 25, 3.4f, 0.055f, 0.012f, 0.09f, 55, new Vector3(0.17f, -0.17f, -0.32f), new Vector3(0, 0.61f, 2.6f), new Vector3(0, 0.4f, 8.2f),
            new Vector3(0.3f, 0.45f, 0.9f), null, null, "SM_Wep_Russian_Rifle_01_Bolt", null, 1.0f, 0.75f, "res://Assets/Synty/PolygonWar/PolygonWar_Texture_01_A.png", Eye: 0.55f,
            RightGrip: new Vector3(0, -0.22f, -0.25f), LeftGrip: new Vector3(0, 0.12f, 3.5f), BulletGrams: 9.6f, Damage: 95f),
        new("ak", "AK rifle", "res://Assets/PolygonBattleRoyale/Prefabs/Weapons/SM_Wep_Rifle_Assault_03.tscn", 1f, FireMode.Auto, 600, 715,
            30, 90, 2.6f, 0.012f, 0.008f, 0.035f, 58, new Vector3(0.16f, -0.16f, -0.3f), new Vector3(0, 0.142f, 0.02f), new Vector3(0, 0.1f, 0.62f),
            new Vector3(0.03f, 0.12f, 0.2f), "SM_Wep_Rifle_Assault_Slide_03", "SM_Wep_Rifle_Assault_Mag_03", null, "SM_Wep_Rifle_Assault_Flash_03", 0.75f, 1.0f,
            RightGrip: new Vector3(0, -0.02f, 0.0f), LeftGrip: new Vector3(0, 0.06f, 0.32f), BulletGrams: 7.9f, Damage: 34f),
        new("pistol", "Pistol", "res://Assets/PolygonBattleRoyale/Prefabs/Weapons/SM_Wep_Pistol_Heavy_01.tscn", 1f, FireMode.Semi, 400, 360,
            8, 32, 1.8f, 0.03f, 0.01f, 0.05f, 62, new Vector3(0.15f, -0.14f, -0.36f), new Vector3(0, 0.1f, -0.06f), new Vector3(0, 0.07f, 0.13f),
            new Vector3(0.02f, 0.08f, 0.0f), "SM_Wep_Pistol_Heavy_Slide_01", "SM_Wep_Pistol_Heavy_Mag_01", null, null, 0.55f, 1.35f, Eye: 0.42f,
            RightGrip: new Vector3(0, -0.045f, -0.02f), LeftGrip: new Vector3(0, -0.065f, -0.02f), TwoHandedPistol: true, Damage: 24f),
    };
}

/// <summary>
/// First-person gun handling (D40), all procedural: the gun hangs in front of the eye camera and gets its life from
/// code: aim down the sights (RMB, always first person), sway against the look, bob with the walk, recoil that kicks
/// the view and the gun and springs back, a slide or bolt that cycles, a magazine that drops on reload, muzzle flash,
/// ejected casings, bullets that fly with travel time and drop, and impacts that throw dust coloured by what they hit.
/// Sounds are generated placeholders, layered (shot, mechanism, tail) so real recordings can replace them.
/// </summary>
public partial class Weapons : Node3D
{
    private readonly Walker _w;
    private Camera3D _eye = null!;
    private Node3D _holder = null!, _model = null!;
    private WeaponDef? _def;
    private readonly Dictionary<string, (int Mag, int Reserve)> _ammo = new();
    private Node3D? _slide, _mag, _bolt, _flash;
    private Vector3 _slideRest, _magRest, _boltRest;
    private Quaternion _boltRot;
    private OmniLight3D _flashLight = null!;
    private FpArms _arms = null!;
    public FpArms Arms => _arms;
    private AudioStreamPlayer _shot = null!, _mech = null!;
    private readonly Dictionary<string, AudioStreamWav> _sounds = new();

    // state
    private float _aim, _cooldown, _reload = -1, _boltCycle = -1, _flashTime, _swayX, _swayY, _bobPhase;
    private Vector3 _kickPos; private Vector3 _kickRot;
    private bool _triggerHeld, _needBolt;
    public bool Aiming { get; private set; }
    public string Status => _def == null ? "" : $"{_def.Name}  {_ammo[_def.Id].Mag} / {_ammo[_def.Id].Reserve}" + (_reload >= 0 ? "  reloading" : _needBolt ? "  (bolt)" : "");
    public WeaponDef? Current => _def;
    /// <summary>Shot within the last half second (the third-person gun comes up to the shoulder, D49).</summary>
    public bool Firing => Time.GetTicksMsec() - _lastShotMs < 500;
    private ulong _lastShotMs;
    /// <summary>Self-tests: hold the trigger / the aim from code.</summary>
    public bool TestTrigger, TestAim;
    /// <summary>Self-tests: shots fired, bullets that hit something, the last hit (where, what, how far, flight time).</summary>
    public int Fired, Hits, TargetHits;
    /// <summary>A shot was fired (where, which gun): bandits hear it (D47).</summary>
    public static event Action<Vector3, WeaponDef>? Shot;
    public string LastTarget = "";
    public string LastHit = "";
    private Label _hudAmmo = null!, _hitNote = null!;
    private float _hitTime = -1;
    private Control _cross = null!;
    private CanvasLayer _ui = null!;

    /// <summary>Getting in the car: the crosshair, ammo and hit note go (Tom: the crosshair stayed on screen). The
    /// walker stops processing then, so this can't wait for the next frame.</summary>
    public void HideHud() { if (_ui != null) _ui.Visible = false; }

    private readonly List<Casing> _casings = new();
    private sealed class Casing { public Node3D N = null!; public Vector3 V, Spin; public float Life; }

    public Weapons(Walker w) { _w = w; }

    public override void _Ready()
    {
        _eye = _w.FirstPersonCamera;
        _holder = new Node3D { Name = "Holder" };
        _eye.AddChild(_holder);
        // the walker's own arms on the gun (D41)
        _arms = new FpArms();
        _eye.AddChild(_arms);
        _flashLight = new OmniLight3D { LightColor = new Color(1f, 0.75f, 0.4f), LightEnergy = 0, OmniRange = 6, ShadowEnabled = false };
        _holder.AddChild(_flashLight);
        _shot = new AudioStreamPlayer { Bus = "Master", VolumeDb = -4 };
        _mech = new AudioStreamPlayer { Bus = "Master", VolumeDb = -8 };
        AddChild(_shot); AddChild(_mech);
        foreach (var d in WeaponDef.All) _ammo[d.Id] = (d.Magazine, d.Reserve);
        // HUD: a small crosshair (hip fire only: aiming uses the sights) and the ammo
        var ui = _ui = new CanvasLayer { Name = "GunHud", Layer = 2 };
        AddChild(ui);
        _cross = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        _cross.SetAnchorsPreset(Control.LayoutPreset.Center);
        _cross.Draw += () =>
        {
            var c = new Color(1, 1, 1, 0.8f);
            foreach (var d in new[] { Vector2.Up, Vector2.Down, Vector2.Left, Vector2.Right })
                _cross.DrawLine(d * 6, d * 13, c, 2);
            _cross.DrawCircle(Vector2.Zero, 1.5f, c);
        };
        ui.AddChild(_cross);
        _hudAmmo = new Label { HorizontalAlignment = HorizontalAlignment.Right };
        _hudAmmo.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.BottomRight, Control.LayoutPresetMode.Minsize, 10);
        _hudAmmo.OffsetBottom = -36;
        _hudAmmo.GrowHorizontal = Control.GrowDirection.Begin;
        _hudAmmo.GrowVertical = Control.GrowDirection.Begin;
        _hudAmmo.AddThemeFontSizeOverride("font_size", 22);
        _hudAmmo.AddThemeColorOverride("font_shadow_color", Colors.Black);
        ui.AddChild(_hudAmmo);
        // a hit: the crosshair grows an X for a moment and a note says what it was (D42)
        _hitNote = new Label { HorizontalAlignment = HorizontalAlignment.Center, Modulate = new Color(1, 1, 1, 0) };
        _hitNote.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.Center);
        _hitNote.OffsetTop = 40; _hitNote.OffsetBottom = 70; _hitNote.OffsetLeft = -200; _hitNote.OffsetRight = 200;
        _hitNote.AddThemeFontSizeOverride("font_size", 20);
        _hitNote.AddThemeColorOverride("font_shadow_color", Colors.Black);
        ui.AddChild(_hitNote);
        var marker = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        marker.SetAnchorsPreset(Control.LayoutPreset.Center);
        marker.Draw += () =>
        {
            if (_hitTime < 0) return;
            var c = new Color(1, 1, 1, Mathf.Clamp(_hitTime / 0.2f, 0, 1));
            foreach (var d in new[] { new Vector2(1, 1), new Vector2(-1, 1), new Vector2(1, -1), new Vector2(-1, -1) })
                marker.DrawLine(d * 8, d * 16, c, 2.5f);
        };
        ui.AddChild(marker);
        _hitMarker = marker;
        WorldOrigin.Shifted += OnShifted;
    }

    public override void _ExitTree() => WorldOrigin.Shifted -= OnShifted;

    private void OnShifted(double dx, double dz)
    {
        var d = new Vector3((float)dx, 0, (float)dz);
        foreach (var c in _casings) if (IsInstanceValid(c.N)) c.N.GlobalPosition -= d;
    }

    // ---- equipping -----------------------------------------------------------------------------------------------

    public void Equip(string? id)
    {
        bool same = _def?.Id == id;   // the same key again holsters the gun
        if (_model != null) { _model.QueueFree(); _model = null!; }
        _def = id == null || same ? null : WeaponDef.All.FirstOrDefault(d => d.Id == id);
        _slide = _mag = _bolt = _flash = null;
        _reload = -1; _boltCycle = -1; _needBolt = false;
        if (_def == null) return;
        var model = new Node3D { Name = _def.Id };
        if (ResourceLoader.Exists(_def.Scene) && ResourceLoader.Load<PackedScene>(_def.Scene) is { } scene)
        {
            var inst = scene.Instantiate<Node3D>();
            foreach (var body in inst.FindChildren("*", "CollisionObject3D", true, false)) body.QueueFree();   // no collision on a held gun
            model.AddChild(inst);
            if (_def.Texture != null && ResourceLoader.Load<Texture2D>(_def.Texture) is { } tex)
            {
                var mat = new StandardMaterial3D { AlbedoTexture = tex, Roughness = 0.6f };
                foreach (var mi in inst.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>()) mi.MaterialOverride = mat;
            }
            // some Battle Royale prefabs (Rifle_Assault_03) come with their parts turned 90° about Y: the magazine
            // sideways. The parts are modelled in the gun's own frame (as in every other prefab), so square them up.
            foreach (var part in inst.GetChildren().OfType<MeshInstance3D>())
                if (!part.Basis.Orthonormalized().IsEqualApprox(Basis.Identity)) part.Basis = Basis.Identity;
            Node3D? Part(string? name) => name == null ? null : inst.FindChild(name, true, false) as Node3D;
            _slide = Part(_def.Slide); _mag = Part(_def.Mag); _bolt = Part(_def.Bolt); _flash = Part(_def.Flash);
            if (_slide != null) _slideRest = _slide.Position;
            if (_mag != null) _magRest = _mag.Position;
            if (_bolt != null) { _boltRest = _bolt.Position; _boltRot = _bolt.Quaternion; }
            if (_flash != null) _flash.Visible = false;
        }
        else model.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, 0.08f, 0.5f) }, Position = new Vector3(0, 0, 0.25f) });
        // the barrel along the eye's -Z: turn the model round, scale it to metres
        model.Scale = Vector3.One * _def.Scale;
        model.RotationDegrees = new Vector3(0, 180, 0);
        foreach (var gi in model.FindChildren("*", "GeometryInstance3D", true, false).OfType<GeometryInstance3D>())
            gi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        _holder.AddChild(model);
        _model = model;
        _flashLight.Position = MuzzleLocal();
        Play(_mech, "equip");
    }

    /// <summary>The muzzle in the holder's frame (the model is turned and scaled inside it).</summary>
    private Vector3 MuzzleLocal() => _def == null ? Vector3.Zero : new Vector3(-_def.Muzzle.X, _def.Muzzle.Y, -_def.Muzzle.Z) * _def.Scale;
    private Vector3 SightLocal() => _def == null ? Vector3.Zero : new Vector3(-_def.Sight.X, _def.Sight.Y, -_def.Sight.Z) * _def.Scale;

    // ---- input ---------------------------------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent e)
    {
        if (!_w.Active) return;
        if (e is InputEventKey { Pressed: true, Echo: false } k)
        {
            if (k.Keycode == Key.Key1) Equip("mosin");
            else if (k.Keycode == Key.Key2) Equip("ak");
            else if (k.Keycode == Key.Key3) Equip("pistol");
            else if (k.Keycode == Key.R) StartReload();
        }
        if (e is InputEventMouseButton mb && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            if (mb.ButtonIndex == MouseButton.Left) _triggerHeld = mb.Pressed;
            if (mb.ButtonIndex == MouseButton.Right) Aiming = mb.Pressed;
        }
        if (e is InputEventMouseMotion mm && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            // sway: the gun lags behind the look
            _swayX = Mathf.Clamp(_swayX - mm.Relative.X * 0.0006f, -0.06f, 0.06f);
            _swayY = Mathf.Clamp(_swayY + mm.Relative.Y * 0.0006f, -0.06f, 0.06f);
        }
    }

    /// <summary>Rounds from a cache (D46), up to twice the starting reserve; returns how many were taken.</summary>
    public int AddAmmo(string gun, int rounds)
    {
        var d = WeaponDef.All.FirstOrDefault(x => x.Id == gun);
        if (d == null) return 0;
        var (mag, res) = _ammo[gun];
        int take = Math.Clamp(d.Reserve * 2 - res, 0, rounds);
        _ammo[gun] = (mag, res + take);
        return take;
    }

    /// <summary>Going down (D50): half of every gun's spare rounds are gone; returns how many.</summary>
    public int LoseHalfReserve()
    {
        int lost = 0;
        foreach (var id in _ammo.Keys.ToList())
        {
            var (mag, res) = _ammo[id];
            lost += res - res / 2;
            _ammo[id] = (mag, res / 2);
        }
        return lost;
    }

    public void StartReload()
    {
        if (_def == null || _reload >= 0 || _boltCycle >= 0) return;
        var (mag, res) = _ammo[_def.Id];
        if (mag >= _def.Magazine || res <= 0) return;
        _reload = 0;
        Play(_mech, "reload");
    }

    // ---- per frame -----------------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        UpdateHitHud(dt);
        UpdateCasings(dt);
        _ui.Visible = true;
        bool show = _def != null && _w.Active;
        _hudAmmo.Visible = show;
        _hudAmmo.Text = Status;
        if (!show) { _holder.Visible = false; _arms.Visible = false; _cross.Visible = false;   // no gun: no arms rig either (Tom's T-pose twin)
            _w.AimOverride = false; return; }
        bool aim = (Aiming || TestAim) && _reload < 0;
        // aiming is always first person (the brief); the walker goes back to its own view on release
        _w.AimOverride = aim;
        _holder.Visible = _w.FirstPerson || aim;
        _aim = Mathf.MoveToward(_aim, aim ? 1f : 0f, dt / 0.16f);
        _cross.Visible = _aim < 0.5f;
        _eye.Fov = Mathf.Lerp(75f, _def.AimFov, Smooth(_aim));

        _cooldown -= dt;
        bool trigger = _triggerHeld || TestTrigger;
        if (trigger && _cooldown <= 0 && _reload < 0 && _boltCycle < 0 && !_needBolt)
        {
            if (_ammo[_def.Id].Mag > 0) Fire();
            else if (!_dryClicked) { Play(_mech, "dry"); _dryClicked = true; }
        }
        if (!trigger) _dryClicked = false;
        if (_def.Mode != FireMode.Auto && !trigger) _semiReady = true;
        // the bolt: worked once the trigger is let go (Mosin)
        if (_needBolt && !trigger && _cooldown <= 0 && _reload < 0) { _needBolt = false; _boltCycle = 0; Play(_mech, "bolt"); }
        if (_boltCycle >= 0) { _boltCycle += dt / 0.85f; if (_boltCycle >= 1) _boltCycle = -1; }
        if (_reload >= 0)
        {
            _reload += dt / _def.ReloadTime;
            if (_reload >= 1)
            {
                var (mag, res) = _ammo[_def.Id];
                int take = Math.Min(_def.Magazine - mag, res);
                _ammo[_def.Id] = (mag + take, res - take);
                _reload = -1;
                _needBolt = false;   // loading works the bolt
            }
        }

        // the gun's pose: hip or sights, then sway, bob, recoil, reload dip
        var hip = _def.Hip;
        var ads = new Vector3(0, 0, -_def.Eye) - SightLocal();        // the rear sight on the eye's line (a rifle at the cheek, a pistol at arm's length)
        var pos = hip.Lerp(ads, Smooth(_aim));
        float walk = _w.Speed > 0.3f && _w.IsOnFloor() ? 1f : 0f;
        _bobPhase += dt * _w.Speed * 2.1f;
        float bobAmt = walk * Mathf.Lerp(1f, 0.25f, _aim) * Mathf.Clamp(_w.Speed / 4.2f, 0, 1.5f);
        pos += new Vector3(MathF.Cos(_bobPhase) * 0.012f, -MathF.Abs(MathF.Sin(_bobPhase)) * 0.014f, 0) * bobAmt;
        _swayX = Mathf.Lerp(_swayX, 0, 1f - MathF.Exp(-8f * dt));
        _swayY = Mathf.Lerp(_swayY, 0, 1f - MathF.Exp(-8f * dt));
        float swayK = Mathf.Lerp(1f, 0.35f, _aim);
        pos += new Vector3(_swayX, _swayY, 0) * swayK;
        // recoil springs back
        _kickPos = _kickPos.Lerp(Vector3.Zero, 1f - MathF.Exp(-14f * dt));
        _kickRot = _kickRot.Lerp(Vector3.Zero, 1f - MathF.Exp(-11f * dt));
        pos += _kickPos;
        var rot = new Vector3(_kickRot.X, _kickRot.Y + _swayX * 2f * swayK, _kickRot.Z - _swayX * 3f * swayK);
        // reload: the gun dips and tilts, the magazine drops out and comes back
        if (_reload >= 0)
        {
            float r = MathF.Sin(MathF.Min(1f, _reload) * MathF.PI);
            pos += new Vector3(-0.04f, -0.09f, 0.03f) * r;
            rot += new Vector3(0.15f, 0.05f, 0.3f) * r;
            // the magazine drops 25 cm out of the gun in the middle of the reload
            float drop = Mathf.Clamp(r * 1.6f - 0.6f, 0f, 1f);
            if (_mag != null) _mag.Position = _magRest + new Vector3(0, -0.25f * drop / _def.Scale, 0);
        }
        else if (_mag != null) _mag.Position = _magRest;
        // bolt cycle (Mosin): up, back, forward, down; the rifle tilts toward it
        if (_boltCycle >= 0 && _bolt != null)
        {
            float c = _boltCycle;
            float up = c < 0.2f ? c / 0.2f : c > 0.8f ? (1 - c) / 0.2f : 1f;
            float back = c < 0.2f ? 0 : c < 0.5f ? (c - 0.2f) / 0.3f : c < 0.8f ? 1 - (c - 0.5f) / 0.3f : 0;
            _bolt.Quaternion = _boltRot * new Quaternion(Vector3.Forward, -1.3f * up);
            _bolt.Position = _boltRest + new Vector3(0, 0, -1.1f * back);
            rot += new Vector3(0.05f, 0, 0.35f) * MathF.Sin(c * MathF.PI);
            pos += new Vector3(0.02f, -0.06f, 0.03f) * MathF.Sin(c * MathF.PI);   // the rifle dips off the cheek while the hand works
        }
        // slide (pistol, AK): snaps back on the shot
        if (_slide != null) _slide.Position = _slideRest + new Vector3(0, 0, -_slideBack / _def.Scale);
        _slideBack = Mathf.MoveToward(_slideBack, 0, dt * 0.9f);

        _holder.Position = pos;
        _holder.Rotation = rot;
        PoseArms();
        _flashTime -= dt;
        _flashLight.LightEnergy = _flashTime > 0 ? 6f : 0f;
        if (_flash != null) _flash.Visible = _flashTime > 0;
    }

    private bool _dryClicked, _semiReady = true;
    private float _slideBack;

    /// <summary>
    /// Hands on the gun (D41): the right on the grip, the left on the handguard (a pistol: cupping the right hand). On
    /// a reload the left hand goes down to the magazine; on the Mosin's bolt cycle the right hand works the bolt.
    /// Directions are in the holder's frame: forward -Z, up +Y, right +X.
    /// </summary>
    private void PoseArms()
    {
        _arms.Visible = _holder.Visible && _arms.Ready;
        if (!_arms.Visible || _def == null) return;
        var d = _def;
        var b = _holder.GlobalTransform.Basis;
        Vector3 F = -b.Z, U = b.Y, R = b.X;
        Vector3 Local(Vector3 m) => _holder.ToGlobal(new Vector3(-m.X, m.Y, -m.Z) * d.Scale);
        var right = new HandGoal(Local(d.RightGrip), F - U * 0.3f, -R, -U + R * 0.8f + b.Z * 0.3f, 0.9f);
        var left = d.TwoHandedPistol
            ? new HandGoal(Local(d.LeftGrip), F * 0.8f + R * 0.3f - U * 0.25f, R, -U - R * 0.6f, 0.85f)
            : new HandGoal(Local(d.LeftGrip), F * 0.7f + R * 0.5f, U * 0.8f + R * 0.4f, -U - R * 0.5f, 0.8f);
        // reload: the left hand down to the magazine and back
        if (_reload >= 0 && _mag != null)
        {
            float w = Mathf.Clamp(MathF.Sin(MathF.Min(1f, _reload) * MathF.PI) * 1.6f, 0f, 1f);
            left = left with { Grip = left.Grip.Lerp(_mag.GlobalPosition - U * 0.04f, w), Dir = left.Dir.Lerp(F * 0.3f + U, w), Palm = left.Palm.Lerp(R, w) };
        }
        // the bolt (Mosin): the right hand leaves the grip for the bolt knob
        if (_boltCycle >= 0 && _bolt != null)
        {
            float w = Mathf.Clamp(MathF.Sin(_boltCycle * MathF.PI) * 2f, 0f, 1f);
            var knob = _bolt.ToGlobal(new Vector3(-0.5f, -0.02f, -1.3f));   // the handle's knob: right side, rear end of the bolt (bolt mesh z -1.55..-0.2)
            // the hand comes to the knob from behind, fingers forward-down; the elbow stays low and back (a forward
            // elbow put the forearm across the face: Tom saw it bend the wrong way)
            right = right with
            {
                Grip = right.Grip.Lerp(knob, w), Dir = right.Dir.Lerp(F - U * 0.45f, w), Palm = right.Palm.Lerp(-R * 0.6f - U * 0.4f, w),
                // straight down: the arm here points up, forward and in, so "back" lay along it and the elbow flipped up
                Pole = right.Pole.Lerp(-U + R * 0.15f, w), Curl = Mathf.Lerp(0.9f, 0.6f, w),
            };
        }
        _arms.Pose(right, left);
    }

    private static float Smooth(float t) => t * t * (3 - 2 * t);

    private Control _hitMarker = null!;

    private void ShowHit(string text)
    {
        _hitNote.Text = text;
        _hitTime = 1.6f;
    }

    private void UpdateHitHud(float dt)
    {
        if (_hitTime >= 0) _hitTime -= dt;
        _hitNote.Modulate = new Color(1, 1, 1, Mathf.Clamp(_hitTime, 0, 1));
        _hitMarker.QueueRedraw();
    }

    private void Fire()
    {
        var d = _def!;
        if (d.Mode != FireMode.Auto && !_semiReady) return;
        _semiReady = false;
        var (mag, res) = _ammo[d.Id];
        _ammo[d.Id] = (mag - 1, res);
        _cooldown = d.Mode == FireMode.Bolt ? 0.3f : 60f / d.Rpm;   // a bolt gun waits for its bolt instead
        if (d.Mode == FireMode.Bolt) _needBolt = true;
        // the bullet leaves along the view (where the sights point), with the eye's spread when hip-firing
        var basis = _eye.GlobalTransform.Basis;
        var dir = -basis.Z;
        float spread = Mathf.Lerp(0.012f, 0.0006f, _aim);
        dir = (dir + basis.X * (float)GD.RandRange(-spread, spread) + basis.Y * (float)GD.RandRange(-spread, spread)).Normalized();
        var from = _aim > 0.5f ? _eye.GlobalPosition : _holder.ToGlobal(MuzzleLocal());
        // the shared bullets (D47): what it hits answers back here, for the HUD and the self-tests
        Ballistics.Get(this).Fire(from, dir * d.MuzzleVelocity + _w.Velocity, d.BulletGrams / 1000f, d.Damage, _w.GetRid(), (said, h, surface) =>
        {
            if (said != null) { ShowHit($"{said}, {h.Range:0} m"); TargetHits++; LastTarget = said; }
            Hits++;
            LastHit = $"{surface} at {h.Range:F1} m";
        });
        Shot?.Invoke(from, d);
        Fired++;
        _lastShotMs = Time.GetTicksMsec();
        // recoil: the view climbs and wanders, the gun punches back and up
        _w.AddRecoil(d.RecoilPitch * (float)GD.RandRange(0.8, 1.2), d.RecoilYaw * (float)GD.RandRange(-1.0, 1.0));
        _kickPos += new Vector3(0, 0.01f, d.Kick);
        _kickRot += new Vector3(d.RecoilPitch * 2.2f, 0, (float)GD.RandRange(-0.03, 0.03));
        _slideBack = d.Slide != null ? 0.035f : 0;
        _flashTime = 0.045f;
        if (_flash != null) _flash.Rotation = _flash.Rotation with { Z = (float)GD.RandRange(0, MathF.Tau) };
        Play(_shot, d.Id + "_shot");
        if (d.Mode != FireMode.Bolt) EjectCasing();
    }

    // ---- bullets, impacts, casings -------------------------------------------------------------------------------

    private static Mesh? _casingMesh;

    private void EjectCasing()
    {
        _casingMesh ??= new CylinderMesh { TopRadius = 0.0055f, BottomRadius = 0.0055f, Height = 0.03f, RadialSegments = 6, Rings = 1,
            Material = new StandardMaterial3D { AlbedoColor = new Color(0.78f, 0.6f, 0.25f), Metallic = 0.8f, Roughness = 0.35f } };
        var c = new MeshInstance3D { Mesh = _casingMesh, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        GetTree().Root.AddChild(c);
        var d = _def!;
        c.GlobalPosition = _holder.ToGlobal(new Vector3(MathF.Abs(d.Ejection.X), d.Ejection.Y, -d.Ejection.Z) * d.Scale);   // out of the right side
        var basis = _eye.GlobalTransform.Basis;
        _casings.Add(new Casing
        {
            N = c, Life = 1.2f,
            V = basis.X * (float)GD.RandRange(1.6, 2.4) + basis.Y * (float)GD.RandRange(1.0, 1.6) + basis.Z * 0.3f + _w.Velocity,
            Spin = new Vector3((float)GD.RandRange(-20, 20), (float)GD.RandRange(-20, 20), (float)GD.RandRange(-20, 20)),
        });
    }

    private void UpdateCasings(float dt)
    {
        for (int i = _casings.Count - 1; i >= 0; i--)
        {
            var c = _casings[i];
            c.Life -= dt;
            if (c.Life <= 0 || !IsInstanceValid(c.N)) { if (IsInstanceValid(c.N)) c.N.QueueFree(); _casings.RemoveAt(i); continue; }
            c.V += Vector3.Down * 9.81f * dt;
            c.N.GlobalPosition += c.V * dt;
            c.N.Rotation += c.Spin * dt;
        }
    }

    // ---- placeholder sounds (D40): generated, layered, replaceable -------------------------------------------------

    private void Play(AudioStreamPlayer p, string key)
    {
        p.Stream = Sound(key);
        p.PitchScale = (float)GD.RandRange(0.95, 1.05);
        p.Play();
    }

    private AudioStreamWav Sound(string key)
    {
        if (_sounds.TryGetValue(key, out var s)) return s;
        var def = WeaponDef.All.FirstOrDefault(d => key.StartsWith(d.Id + "_"));
        s = key switch
        {
            _ when key.EndsWith("_shot") && def != null => Synth.Shot(def.Boom, def.Pitch),
            "bolt" => Synth.Clicks(new[] { 0.0f, 0.18f, 0.42f, 0.66f }, 1.0f),
            "reload" => Synth.Clicks(new[] { 0.1f, 0.6f, 1.2f }, 0.8f),
            "equip" => Synth.Clicks(new[] { 0.0f, 0.12f }, 0.6f),
            "dry" => Synth.Clicks(new[] { 0.0f }, 1.4f),
            "hit_hard" => Synth.Hit(0.9f),
            _ => Synth.Hit(0.5f),
        };
        _sounds[key] = s;
        return s;
    }
}

/// <summary>Generated placeholder sounds ("NoorSounds bleeps and bloops", the brief): a shot is a noise crack, a low
/// thump and a decaying tail; mechanisms are short metallic clicks.</summary>
public static class Synth
{
    private const int Rate = 22050;

    private static AudioStreamWav Wav(float[] x)
    {
        var bytes = new byte[x.Length * 2];
        for (int i = 0; i < x.Length; i++)
        {
            short v = (short)Math.Clamp(x[i] * 32000f, -32768f, 32767f);
            bytes[i * 2] = (byte)(v & 0xff); bytes[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = bytes };
    }

    public static AudioStreamWav Shot(float boom, float pitch)
    {
        var rng = new Random(7);
        int n = (int)(Rate * (0.35f + boom * 0.9f));
        var x = new float[n];
        float lp = 0, lp2 = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            lp += (noise - lp) * 0.35f * pitch;      // the crack: bright noise, gone in a few milliseconds
            lp2 += (noise - lp2) * 0.04f;            // the tail: darker noise, slower
            float crack = lp * MathF.Exp(-t * 90f / pitch);
            float thump = MathF.Sin(MathF.Tau * (55f + 40f * MathF.Exp(-t * 30f)) * t / pitch) * MathF.Exp(-t * 22f) * boom;
            float tail = lp2 * 3f * MathF.Exp(-t * (6f / boom)) * boom * 0.5f;
            x[i] = (crack * 1.4f + thump * 0.9f + tail) * MathF.Min(1f, t * 3000f);
        }
        return Wav(x);
    }

    public static AudioStreamWav Clicks(float[] at, float sharp)
    {
        var rng = new Random(3);
        int n = (int)(Rate * (at.Max() + 0.15f));
        var x = new float[n];
        foreach (var t0 in at)
        {
            int s = (int)(t0 * Rate);
            for (int i = 0; i < Rate * 0.06f && s + i < n; i++)
            {
                float t = i / (float)Rate;
                x[s + i] += ((float)(rng.NextDouble() * 2 - 1) * 0.5f + MathF.Sin(MathF.Tau * 2600f * sharp * t) * 0.5f) * MathF.Exp(-t * 120f) * 0.6f;
            }
        }
        return Wav(x);
    }

    /// <summary>A struck metal object: a few inharmonic partials ringing down over <paramref name="decay"/> s.</summary>
    public static AudioStreamWav Ring(float[] partials, float decay)
    {
        int n = (int)(Rate * decay * 1.2f);
        var x = new float[n];
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate, v = 0;
            for (int k = 0; k < partials.Length; k++)
                v += MathF.Sin(MathF.Tau * partials[k] * t) * MathF.Exp(-t * (3f + k * 2f) / decay) / (k + 1);
            x[i] = v * 0.5f * MathF.Min(1f, t * 4000f);
        }
        return Wav(x);
    }

    /// <summary>Breaking glass: a bright crash and a scatter of little tinkles.</summary>
    public static AudioStreamWav Glass()
    {
        var rng = new Random(5);
        int n = (int)(Rate * 0.7f);
        var x = new float[n];
        float hp = 0, prev = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            hp = 0.6f * (hp + noise - prev); prev = noise;   // high-passed noise
            x[i] = hp * MathF.Exp(-t * 14f) * 0.9f;
        }
        for (int k = 0; k < 14; k++)
        {
            int s0 = (int)(Rate * (0.02 + rng.NextDouble() * 0.5));
            float f = 3000f + (float)rng.NextDouble() * 4000f;
            for (int i = 0; i < Rate * 0.05f && s0 + i < n; i++)
            {
                float t = i / (float)Rate;
                x[s0 + i] += MathF.Sin(MathF.Tau * f * t) * MathF.Exp(-t * 90f) * 0.25f;
            }
        }
        return Wav(x);
    }

    public static AudioStreamWav Hit(float hard)
    {
        var rng = new Random(11);
        int n = (int)(Rate * 0.25f);
        var x = new float[n];
        float lp = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            lp += ((float)(rng.NextDouble() * 2 - 1) - lp) * (0.1f + 0.4f * hard);
            x[i] = lp * MathF.Exp(-t * 40f) * 0.8f;
        }
        return Wav(x);
    }
}
