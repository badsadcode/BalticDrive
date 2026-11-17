using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using BalticDrive.Core;
using BalticDrive.World;
using Godot;

namespace BalticDrive;

/// <summary>
/// Scripted checks that drive the real game (like Dead Miles' DebugHarness). After <c>--</c>:
/// <list type="bullet">
/// <item><c>--views=x:z:above:yawDeg:pitchDeg;...</c> — for each view: go there, wait until everything has streamed in,
/// save a screenshot to <c>--shots=dir</c>.</item>
/// <item><c>--autotest=fly</c> — fly 6 km in a straight line at <c>--speed=</c> m/s (default 40, ~145 km/h) from the
/// start and report frame times and holes (frames where the ground under the camera was missing).</item>
/// <item><c>--autotest=walk</c> — out of the car (E), walk 5 s, sprint 3 s, crouch 2 s, first person, walk back
/// to the car and get in, with screenshots on the way (D38).</item>
/// <item>Self-tests leave the mouse free; <c>--capture-mouse</c> lets them take it (the shoot test then also checks
/// that a right click keeps it captured).</item>
/// <item><c>--autotest=shoot</c> — out of the car, then each gun in turn (D40): hip view, aim down the sights, fire
/// (a burst for the AK), reload; screenshots of each and the bullets' hits in the log.</item>
/// <item><c>--autotest=range</c> — out of the car, set up the shooting range (D42), shoot each target with the AK
/// (aimed straight at it, the 100 m plate with the Mosin), screenshots before and after, and what each hit said.</item>
/// <item><c>--autotest=loot</c> — out of the car, wait for supply crates by the nearby shops (D46), walk up to the
/// nearest, open it; then hunger, thirst and a medkit: are carried food and water used, does the medkit heal.</item>
/// <item><c>--autotest=bandits</c> — out of the car, a group of bandits 45 m ahead (D47): stand and watch them notice
/// you and fight (their states and your health logged), then shoot back with the AK, then search a body.</item>
/// <item><c>--autotest=banditlook</c> — one bandit 6 m ahead, photographed relaxed and then aiming at you (how they
/// hold their guns).</item>
/// <item><c>--autotest=tpgun</c> — third person with each gun (D49): standing, walking, sprinting, firing, seen
/// from the side.</item>
/// <item><c>--autotest=death</c> (D55: the fall, waking far away, your things, a safehouse) — — crates on the radar (D50), then going down: the screen goes dark, you come round
/// by the car with the pack gone.</item>
/// <item><c>--autotest=crouch</c> — third person from the side: standing, crouched still, crouch-walking (D52).</item>
/// <item><c>--autotest=carbandits</c> — sitting in the car, a group of bandits 30 m away (D52): do they come for the
/// car and shoot, and does it hurt the driver.</item>
/// <item><c>--autotest=sound</c> — records the game's own sound to <c>sound.wav</c> (D53): a pistol shot by the
/// buildings, one out in the open, then a drive (engine revs, gears); the places the acoustics found, and when each
/// sound should land, go to the log.</item>
/// <item>Test runs play muted (the master bus at -80 dB; a recording still hears it), unless <c>--sound</c>.</item>
/// <item><c>--autotest=leads</c> — a lead (D54): one is found 300–600 m away, shown on the map; then you're put
/// 150 m from it: the stash is set down with its guards; the guards are shot, the stash opened (a note may lead on).</item>
/// <item><c>--autotest=lean</c> — leaning (D55): first person upright, left, right; third person left and right
/// with the AK out; logs how far the eye moved and the room a wall left.</item>
/// <item><c>--autotest=ragdoll</c> — ragdolls (D56): a bandit shot from the side falls (photographed as it goes), the
/// body checked for flying off or sinking, the world origin moved under it and back; then the walker is shot and the
/// fall seen through its eyes.</item>
/// <item><c>--autotest=roadscan [--radius=3000]</c> — looks through the roads around the start for bends tighter than
/// the road is wide (the inner edge folds over itself: Tom's wobbly service roads), logs the worst with a
/// <c>--views=</c> line to photograph them from above, and quits.</item>
/// <item><c>--autotest=respawn</c> — the "fly far, press F" check: from the car, switch to the free camera, fly
/// <c>--distance=</c> m (default 3000) at <c>--speed=</c>, press F (drive here), wait, and repeat <c>--rounds=</c> times
/// (default 6) in different directions.</item>
/// </list>
/// The game quits when done; results go to the log as lines starting with <c>[Test]</c>.
/// </summary>
public sealed class Tests
{
    private readonly Main _main;
    private readonly string _shots;
    private readonly List<string[]> _views = new();
    private int _view = -1;
    private double _settledFor, _elapsed;
    private int _frames;
    private bool _fly;
    private double _flown, _speed;
    private readonly List<double> _frameMs = new();
    private int _holes;
    private bool _flyStarted;
    private bool _drive;
    private double _driveTime, _maxSpeed, _offRoad;
    private bool _driveStarted;
    private double _driveLimit = 20;
    private int _routeHint;
    private float _stuckTime, _reverseLeft, _reverseSteer;   // drive test: a car pinned against a kerb backs off and retries
    private int _reversals;
    private double _driven;
    private bool _map;
    private bool _respawn;
    private bool _walk, _shoot, _rangeTest, _loot, _banditTest;
    private double _banditTime = -1;
    private OnFoot.Bandit? _banditTarget;
    private int _banditShot;
    private double _lootTime = -1;
    private OnFoot.CacheField.Cache? _lootCache;
    private double _rangeTime = -1;
    private int _rangeTarget;
    private double _shootTime = -1;
    private int _shootShot;
    private Input.MouseModeEnum _mouseBefore;
    private double _walkTime = -1;
    private int _walkShot;
    private Vector3 _walkFrom;
    private int _round, _rounds;
    private double _roundFlown, _roundWait = -1, _distance;
    private double _mapTime = -1;

    public Tests(Main main)
    {
        _main = main;
        _shots = Main.Args.GetValueOrDefault("shots", ProjectSettings.GlobalizePath("user://shots"));
        Directory.CreateDirectory(_shots);
        if (Main.Args.TryGetValue("views", out var v))
            foreach (var s in v.Split(';', StringSplitOptions.RemoveEmptyEntries)) _views.Add(s.Split(':'));
        _fly = Main.Args.GetValueOrDefault("autotest", "").Contains("fly");
        _map = Main.Args.GetValueOrDefault("autotest", "").Contains("map");
        _drive = Main.Args.GetValueOrDefault("autotest", "").Contains("drive");
        _respawn = Main.Args.GetValueOrDefault("autotest", "").Contains("respawn");
        _walk = Main.Args.GetValueOrDefault("autotest", "").Contains("walk");
        _shoot = Main.Args.GetValueOrDefault("autotest", "").Contains("shoot");
        _rangeTest = Main.Args.GetValueOrDefault("autotest", "").Contains("range");
        _loot = Main.Args.GetValueOrDefault("autotest", "").Contains("loot");
        _banditTest = Main.Args.GetValueOrDefault("autotest", "").Split(',').Contains("bandits");
        _banditLook = Main.Args.GetValueOrDefault("autotest", "").Contains("banditlook");
        _tpGun = Main.Args.GetValueOrDefault("autotest", "").Contains("tpgun");
        _death = Main.Args.GetValueOrDefault("autotest", "").Contains("death");
        _crouchTest = Main.Args.GetValueOrDefault("autotest", "").Contains("crouch");
        _carBandits = Main.Args.GetValueOrDefault("autotest", "").Contains("carbandits");
        _soundTest = Main.Args.GetValueOrDefault("autotest", "").Contains("sound");
        _leadTest = Main.Args.GetValueOrDefault("autotest", "").Contains("leads");
        _leanTest = Main.Args.GetValueOrDefault("autotest", "").Contains("lean");
        _ragdollTest = Main.Args.GetValueOrDefault("autotest", "").Contains("ragdoll");
        // muted: Tom works while tests run
        if (!Main.Args.ContainsKey("sound")) AudioServer.SetBusVolumeDb(0, -80);
        _rounds = int.Parse(Main.Args.GetValueOrDefault("rounds", "6"), CultureInfo.InvariantCulture);
        _distance = double.Parse(Main.Args.GetValueOrDefault("distance", "3000"), CultureInfo.InvariantCulture);
        _speed = double.Parse(Main.Args.GetValueOrDefault("speed", "40"), CultureInfo.InvariantCulture);
    }

    /// <summary>The shooting test (D40): 8 s per gun.</summary>
    private void Shoot(double delta)
    {
        if (_shootTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { Log.Error("Test", "shoot: could not get out of the car"); _shoot = false; return; }
            _main.Walker!.SetView(true);
            _shootTime = 0;
            return;
        }
        var g = _main.Walker!.Guns;
        double before = _shootTime;
        _shootTime += delta;
        string[] ids = { "mosin", "ak", "pistol" };
        int gun = (int)(_shootTime / 8);
        if (gun >= ids.Length)
        {
            Log.Info("Test", $"shoot: done, {g.Fired} fired, {g.Hits} hit");
            g.TestAim = g.TestTrigger = false;
            _shoot = false;
            return;
        }
        double t = _shootTime - gun * 8, b = before - gun * 8;
        bool Now(double x) => b < x && t >= x;
        string id = ids[gun];
        if (g.Current?.Id != id && t < 0.5) { g.Equip(id); Log.Info("Test", $"shoot: {id} equipped"); }
        // the aim button must not let the mouse go (Tom: the free camera freed it on release)
        g.Arms.DebugOn = true;
        if (Main.CaptureMouse && gun == 0 && Now(0.2)) { Input.MouseMode = Input.MouseModeEnum.Captured; _mouseBefore = Input.MouseMode; }
        if (Main.CaptureMouse && gun == 0 && Now(0.3)) Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = true });
        if (Main.CaptureMouse && gun == 0 && Now(0.5)) Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Right, Pressed = false });
        if (Main.CaptureMouse && gun == 0 && Now(0.7)) Log.Info("Test", $"shoot: mouse {_mouseBefore} before a right click, {Input.MouseMode} after it");
        if (Now(0.8)) Shot($"shoot_{id}_hip");
        // level the view on the street 15 m ahead before each gun: recoil from the last one would carry over
        if (Now(0.9)) { var w = _main.Walker!; w.Face(w.GlobalPosition + new Vector3(-MathF.Sin(w.Yaw), 0, -MathF.Cos(w.Yaw)) * 15f); }
        if (Now(1.0)) g.TestAim = true;
        if (Now(1.8)) Shot($"shoot_{id}_aim");
        if (Now(2.0)) g.TestTrigger = true;
        if (Now(id == "ak" ? 2.5 : 2.05)) g.TestTrigger = false;
        if (Now(2.02)) Shot($"shoot_{id}_fire");
        if (id == "mosin" && (Now(2.55) || Now(2.75) || Now(2.95))) { Shot($"shoot_mosin_bolt_{(Now(2.55) ? 1 : Now(2.75) ? 2 : 3)}"); Log.Info("Test", "arm bolt " + g.Arms.DebugRight); }   // the bolt cycle, three moments
        if (Now(1.8)) Log.Info("Test", $"arm {id} aim " + g.Arms.DebugRight);
        if (Now(3.2)) { Log.Info("Test", $"shoot: {id}: {g.Status}; fired {g.Fired}, hits {g.Hits}, last: {g.LastHit}"); g.TestAim = false; }
        if (Now(3.6)) g.StartReload();
        if (Now(3.6 + (g.Current?.ReloadTime ?? 2) * 0.5)) Shot($"shoot_{id}_reload");
        if (Now(7.6)) Log.Info("Test", $"shoot: {id} after reload: {g.Status}");
    }

    /// <summary>The range test (D42): 2.5 s per target: face it, aim, fire one shot, look.</summary>
    private void RangeTest(double delta)
    {
        if (_rangeTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { Log.Error("Test", "range: could not get out of the car"); _rangeTest = false; return; }
            _main.Walker!.SetView(true);
            _rangeTime = 0;
            return;
        }
        var w = _main.Walker!;
        var g = w.Guns;
        double before = _rangeTime;
        _rangeTime += delta;
        bool Now(double t) => before < t && _rangeTime >= t;
        if (Now(0.5))
        {
            var r = _main.SetUpRange();
            Log.Info("Test", $"range: {r?.Targets.Count ?? 0} targets set up");
            g.Equip("ak");
        }
        if (Now(2.0))
        {
            // the whole range, from a step back and a little up
            w.Face(_main.Range!.Targets[^1].Node.GlobalPosition + Vector3.Up * 1.2f);
            Shot("range_overview");
        }
        var targets = _main.Range?.Targets;
        if (targets == null || _rangeTime < 3) return;
        int i = (int)((_rangeTime - 3) / 2.5);
        double t = (_rangeTime - 3) - i * 2.5, b = (before - 3) - i * 2.5;
        bool At(double x) => b < x && t >= x;
        if (i >= targets.Count)
        {
            g.TestAim = g.TestTrigger = false;
            Shot("range_after");
            Log.Info("Test", $"range: done, {g.Fired} fired, {g.TargetHits} target hits");
            _rangeTest = false;
            return;
        }
        var (name, node, aim) = targets[i];
        if (At(0.01))
        {
            if (name.StartsWith("plate 100")) g.Equip("mosin");
            g.TestAim = true;
        }
        // keep looking at the target (it may have moved)
        if (t < 1.3 && GodotObject.IsInstanceValid(node)) w.Face(node.GlobalPosition + aim + Vector3.Up * (name.StartsWith("plate 100") ? 0.06f : 0f));
        if (At(1.0)) { g.TestTrigger = true; _rangeHitsBefore = g.TargetHits; }
        if (At(1.06)) g.TestTrigger = false;
        if (At(1.25) && (name.StartsWith("bottle 2") || name.StartsWith("plate 25") || name.StartsWith("paper"))) Shot($"range_{name.Replace(' ', '_')}");
        if (At(2.3)) Log.Info("Test", $"range: {name}: {(g.TargetHits > _rangeHitsBefore ? "hit, " + g.LastTarget : "missed")} ({g.LastHit})");
    }
    private int _rangeHitsBefore;

    /// <summary>Bends where a road's inner edge folds: the sideways shift of the edge at a corner (half width times
    /// tan of half the turn) is longer than the shorter piece beside it.</summary>
    private void RoadScan()
    {
        var w = _main.World!;
        var (fx, fz) = _main.Focus;
        double radius = Num(Main.Args.GetValueOrDefault("radius", "3000"));
        var found = new List<(double X, double Z, int Folds, string What)>();
        int roads = 0, bends = 0;
        int tx0 = (int)Math.Floor((fx - radius) / Map.MapTile.Size), tx1 = (int)Math.Floor((fx + radius) / Map.MapTile.Size);
        int tz0 = (int)Math.Floor((fz - radius) / Map.MapTile.Size), tz1 = (int)Math.Floor((fz + radius) / Map.MapTile.Size);
        for (int tx = tx0; tx <= tx1; tx++)
            for (int tz = tz0; tz <= tz1; tz++)
            {
                if (!w.Data.HasTile(tx, tz)) continue;
                var tile = w.Data.Tile(tx, tz);
                foreach (var r in tile.Roads)
                {
                    if (r.IsRail || r.IsTram || r.X.Length < 3) continue;
                    roads++;
                    float hw = r.HalfWidth + 0.6f;   // with the shoulder
                    int folds = 0; double worstX = 0, worstZ = 0; float worstTurn = 0;
                    for (int i = 1; i < r.X.Length - 1; i++)
                    {
                        var a = new Vector2(r.X[i] - r.X[i - 1], r.Z[i] - r.Z[i - 1]);
                        var b = new Vector2(r.X[i + 1] - r.X[i], r.Z[i + 1] - r.Z[i]);
                        float la = a.Length(), lb = b.Length();
                        if (la < 0.05f || lb < 0.05f) continue;
                        float turn = MathF.Acos(Mathf.Clamp(a.Dot(b) / (la * lb), -1, 1));
                        if (turn < 0.05f) continue;
                        bends++;
                        if (hw * MathF.Tan(MathF.Min(turn, 2.8f) / 2) > MathF.Min(la, lb))
                        {
                            folds++;
                            if (turn > worstTurn) { worstTurn = turn; worstX = tile.OriginX + r.X[i]; worstZ = tile.OriginZ + r.Z[i]; }
                        }
                    }
                    if (folds > 0 && (worstX - fx) * (worstX - fx) + (worstZ - fz) * (worstZ - fz) < radius * radius)
                        found.Add((worstX, worstZ, folds, $"class {r.Cls} '{r.Name}' width {r.HalfWidth * 2:0.0} m, worst turn {worstTurn * 180 / MathF.PI:0} deg"));
                }
            }
        // --find=tram: a few tram tracks to look at (D58)
        if (Main.Args.GetValueOrDefault("find", "") == "tram")
        {
            var trams = new List<string>();
            for (int tx = tx0; tx <= tx1 && trams.Count < 4; tx++)
                for (int tz = tz0; tz <= tz1 && trams.Count < 4; tz++)
                {
                    if (!w.Data.HasTile(tx, tz)) continue;
                    var tile = w.Data.Tile(tx, tz);
                    foreach (var r in tile.Roads)
                        if (r.IsTram && r.Layer == 0 && r.X.Length > 3 && trams.Count < 4)
                        {
                            int m = r.X.Length / 2;
                            double x = tile.OriginX + r.X[m], z = tile.OriginZ + r.Z[m];
                            float yaw = MathF.Atan2(-(r.X[m + 1] - r.X[m]), -(r.Z[m + 1] - r.Z[m])) * 180 / MathF.PI;
                            trams.Add($"{x + 6:0}:{z:0}:4:{yaw + 20:0}:-6");
                        }
                }
            Log.Info("Test", "roadscan: trams --views=\"" + string.Join(";", trams) + "\"");
        }
        Log.Info("Test", $"roadscan: {roads} roads, {bends} bends, {found.Count} roads with folding bends within {radius:0} m");
        foreach (var cls in found.GroupBy(f => f.What.Split(' ')[1]).OrderByDescending(g => g.Count()))
            Log.Info("Test", $"roadscan: class {cls.Key}: {cls.Count()} roads, {cls.Sum(f => f.Folds)} folding bends");
        var worst = found.OrderByDescending(f => f.Folds).Take(10).ToList();
        foreach (var f in worst) Log.Info("Test", $"roadscan: {f.X:0},{f.Z:0}: {f.Folds} folding bends, {f.What}");
        Log.Info("Test", "roadscan: --views=\"" + string.Join(";", worst.Take(6).Select(f => $"{f.X:0}:{f.Z:0}:35:0:-80")) + "\"");
    }

    private double _gbTime = -1;
    private (double X, double Z) _gbAt;
    private readonly List<double> _gpuOn = new(), _gpuOff = new();

    /// <summary>--autotest=grassbench (D59): the grassiest spot within 2 km of the start (meadow or park all round), at
    /// eye height; a slow full turn with grass on, then off, measuring the GPU's time per frame; screenshots.</summary>
    private void GrassBench(double delta)
    {
        var rid = _main.GetViewport().GetViewportRid();
        if (_gbTime < 0)
        {
            if (_settledFor < 1.5) return;
            var w = _main.World!;
            var (fx, fz) = _main.Focus;
            int best = -1;
            for (int i = -20; i <= 20; i++)
                for (int j = -20; j <= 20; j++)
                {
                    double x = fx + i * 100, z = fz + j * 100;
                    int score = 0;
                    for (int a = -2; a <= 2; a++)
                        for (int b = -2; b <= 2; b++)
                        {
                            byte l = w.LandAt(x + a * 15, z + b * 15);
                            if (l == Map.MapLand.Meadow || l == Map.MapLand.Park) score++;
                        }
                    if (score > best) { best = score; _gbAt = (x, z); }
                }
            Log.Info("Test", $"grassbench: grassiest spot {_gbAt.X:0},{_gbAt.Z:0} ({best}/25 samples meadow or park)");
            if (!_main.Flying) _main.ToggleFreeCamera();
            _main.Teleport(_gbAt.X, _gbAt.Z, 1.7f, 0, -8);
            RenderingServer.ViewportSetMeasureRenderTime(rid, true);
            _gbTime = 0; _settledFor = 0;
            return;
        }
        if (_settledFor < 2 && _gbTime == 0) return;
        double before = _gbTime;
        _gbTime += delta;
        bool Now(double t) => before < t && _gbTime >= t;
        var g = _main.Grass;
        // 0-10 s grass on, 10-12 settle, 12-22 off
        if (_gbTime < 10 || (_gbTime > 12 && _gbTime < 22))
        {
            double turn = (_gbTime % 12) / 10 * 360;
            _main.Camera.Yaw = Mathf.DegToRad((float)turn);
            (_gbTime < 10 ? _gpuOn : _gpuOff).Add(RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid));
        }
        if (Now(1)) Shot("grass_on_a");
        if (Now(4)) Shot("grass_on_b");
        if (Now(10)) { Log.Info("Test", $"grassbench: {g.Instances} clumps in {g.Pending} pending"); g.On = false; }
        if (Now(13)) Shot("grass_off_a");
        if (Now(22))
        {
            double Med(List<double> l) { var c = l.Skip(10).OrderBy(x => x).ToList(); return c.Count == 0 ? 0 : c[c.Count / 2]; }
            double P95(List<double> l) { var c = l.Skip(10).OrderBy(x => x).ToList(); return c.Count == 0 ? 0 : c[(int)(c.Count * 0.95)]; }
            Log.Info("Test", $"grassbench: GPU ms per frame, grass on: median {Med(_gpuOn):F2}, p95 {P95(_gpuOn):F2}; off: median {Med(_gpuOff):F2}, p95 {P95(_gpuOff):F2}");
            Log.Info("Test", "done"); _main.GetTree().Quit(0);
        }
    }

    private bool _ragdollTest;
    private double _ragTime = -1;
    private OnFoot.Bandit? _ragBandit;
    private (double X, double Y, double Z) _ragWorld;

    /// <summary>The ragdoll test (D56).</summary>
    private void RagdollTest(double delta)
    {
        if (_ragTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { Log.Error("Test", "ragdoll: could not get out of the car"); _ragdollTest = false; return; }
            _ragTime = 0;
            return;
        }
        var w = _main.Walker!;
        double before = _ragTime;
        _ragTime += delta;
        bool Now(double t) => before < t && _ragTime >= t;
        string Pos(Vector3 v) { var (x, y, z) = WorldOrigin.ToWorld(v); return $"{x:0.0},{y:0.00},{z:0.0}"; }
        if (Now(0.5))
        {
            int n = _main.Bandits!.SpawnAhead(w.GlobalPosition, w.Yaw, 9);
            _ragBandit = _main.Bandits.All.LastOrDefault();
            Log.Info("Test", $"ragdoll: {n} bandits 9 m ahead");
        }
        if (Now(1.5) && _ragBandit != null)
        {
            Log.Info("Test", $"ragdoll: {Engine.GetFramesPerSecond()} fps before the shot (4 ragdolls built, asleep)");
            // the bullet comes from where you stood; you watch from 7 m to the side
            var dir = (_ragBandit.GlobalPosition - w.GlobalPosition) with { Y = 0 };
            var side = dir.Normalized().Cross(Vector3.Up);
            w.GlobalPosition = _ragBandit.GlobalPosition + side * 7f + Vector3.Up * 0.3f;
            w.SetView(true); w.Face(_ragBandit.GlobalPosition + Vector3.Up * 0.6f);
            _ragBandit.Hit(new OnFoot.BulletHit(_ragBandit.GlobalPosition + Vector3.Up * 1.3f, -dir.Normalized(), dir.Normalized() * 800, 0.008f, 9, 500));
            Log.Info("Test", $"ragdoll: shot; feet at {Pos(_ragBandit.GlobalPosition)}, body at {Pos(_ragBandit.BodyAt)}");
        }
        if (_ragBandit != null)
        {
            if (Now(1.65)) Shot("ragdoll_0_15s");
            if (Now(1.9)) Shot("ragdoll_0_4s");
            if (Now(2.5)) Shot("ragdoll_1s");
            if (Now(5.0)) Log.Info("Test", $"ragdoll: {Engine.GetFramesPerSecond()} fps with a ragdoll lying there");
            if (Now(5.5))
            {
                Shot("ragdoll_4s");
                float ground = _main.World!.Height(WorldOrigin.ToWorld(_ragBandit.BodyAt).X, WorldOrigin.ToWorld(_ragBandit.BodyAt).Z);
                Log.Info("Test", $"ragdoll: after 4 s the body is at {Pos(_ragBandit.BodyAt)}, {_ragBandit.BodyAt.DistanceTo(_ragBandit.GlobalPosition):0.00} m from the feet, {WorldOrigin.ToWorld(_ragBandit.BodyAt).Y - ground:0.00} m above the ground");
                _ragWorld = WorldOrigin.ToWorld(_ragBandit.BodyAt);
                // the floating origin jumps 3 km away (the walker's own position will pull it back next frame)
                WorldOrigin.Follow(WorldOrigin.X + 3500, WorldOrigin.Z);
                var (x, y, z) = WorldOrigin.ToWorld(_ragBandit.BodyAt);
                Log.Info("Test", $"ragdoll: origin moved: body now at {x:0.0},{y:0.00},{z:0.0} (was {_ragWorld.X:0.0},{_ragWorld.Y:0.00},{_ragWorld.Z:0.0})");
            }
            if (Now(6.5))
            {
                var (x, y, z) = WorldOrigin.ToWorld(_ragBandit.BodyAt);
                Log.Info("Test", $"ragdoll: a second later, origin back: body at {x:0.0},{y:0.00},{z:0.0}, moved {Math.Sqrt((x - _ragWorld.X) * (x - _ragWorld.X) + (z - _ragWorld.Z) * (z - _ragWorld.Z)):0.00} m");
                Shot("ragdoll_after_shift");
            }
        }
        if (Now(7))
        {
            foreach (var b in _main.Bandits!.All) b.QueueFree();
            _main.Bandits.Clear();
            w.CameraOrbit = 0; w.SetView(true);
            w.LastShot = new Vector3(-MathF.Sin(w.Yaw), 0, -MathF.Cos(w.Yaw)) * -800;   // from ahead: falls backwards
            w.Body.Damage(500);
        }
        if (Now(7.2)) Shot("ragdoll_eyes_0_2s");
        if (Now(7.6)) Shot("ragdoll_eyes_0_6s");
        if (Now(8.9)) Log.Info("Test", $"ragdoll: {Engine.GetFramesPerSecond()} fps while you lie there");
        if (Now(9.0)) { Shot("ragdoll_eyes_2s"); Log.Info("Test", $"ragdoll: you fell, body at {Pos(w.BodyAt)}, eye at {Pos(w.FirstPersonCamera.GlobalPosition)}"); }
        if (Now(9.5)) _main.Respawn();
        if (Now(14))
        {
            var pkg = _main.Caches!.Live.FirstOrDefault(c => c.Key.StartsWith("death:"));
            if (pkg != null)
            {
                w.GlobalPosition = pkg.GlobalPosition + new Vector3(2.2f, 0.5f, 1.2f);
                w.SetView(false);
            }
        }
        if (Now(18))
        {
            var pkg = _main.Caches!.Live.FirstOrDefault(c => c.Key.StartsWith("death:"));
            if (pkg != null) w.Face(pkg.GlobalPosition);
        }
        if (Now(18.4))
        {
            var pkg = _main.Caches!.Live.FirstOrDefault(c => c.Key.StartsWith("death:"));
            var sk = pkg?.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().FirstOrDefault();
            if (sk != null)
            {
                float hips = (sk.GlobalTransform * sk.GetBoneGlobalPose(sk.FindBone("Hips")).Origin).Y, head = (sk.GlobalTransform * sk.GetBoneGlobalPose(sk.FindBone("Head")).Origin).Y;
                Log.Info("Test", $"ragdoll: the body left behind: hips {hips - pkg!.GlobalPosition.Y:0.00} m, head {head - pkg.GlobalPosition.Y:0.00} m above the package; you {w.GlobalPosition.DistanceTo(pkg.GlobalPosition):0.0} m away, {w.DebugPose()[..60]}");
            }
        }
        if (Now(18.5)) { Shot("ragdoll_left_behind"); Log.Info("Test", "ragdoll: done"); _ragdollTest = false; }
    }

    private bool _leanTest;
    private double _leanTime = -1;

    /// <summary>The lean test (D55).</summary>
    private void LeanTest(double delta)
    {
        if (_leanTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { Log.Error("Test", "lean: could not get out of the car"); _leanTest = false; return; }
            _main.Walker!.SetView(true);
            _main.Walker.Guns.Equip("ak");
            _leanTime = 0;
            return;
        }
        var w = _main.Walker!;
        double before = _leanTime;
        _leanTime += delta;
        bool Now(double t) => before < t && _leanTime >= t;
        void Report(string what) => Log.Info("Test", $"lean: {what}: lean {w.Lean:0.00}, eye {w.LeanOffset:0.00} m to the side, camera at {w.FirstPersonCamera.GlobalPosition - w.GlobalPosition}");
        if (Now(1.0)) { Shot("lean_fp_upright"); Report("upright"); w.TestLean = -1; }
        if (Now(2.0)) { Shot("lean_fp_left"); Report("left"); w.TestLean = 1; }
        if (Now(3.0)) { Shot("lean_fp_right"); Report("right"); w.TestLean = 0; w.SetView(false); w.CameraOrbit = MathF.PI; }
        if (Now(4.0)) { Shot("lean_tp_front_upright"); w.TestLean = -1; }
        if (Now(5.0)) { Shot("lean_tp_front_left"); Report("third person, left"); w.TestLean = 1; }
        if (Now(6.0)) { Shot("lean_tp_front_right"); Report("third person, right"); w.CameraOrbit = 0; }
        if (Now(7.0)) { Shot("lean_tp_back_right"); w.TestLean = -1; }
        if (Now(8.0)) { Shot("lean_tp_back_left"); w.TestLean = null; Log.Info("Test", "lean: done"); _leanTest = false; }
    }

    private bool _leadTest;
    private double _leadTime = -1;

    /// <summary>The leads test (D54).</summary>
    private void LeadTest(double delta)
    {
        if (_leadTime < 0)
        {
            if (_settledFor < 1.5 || _main.Leads == null) return;
            if (!_main.ToggleOnFoot()) { Log.Error("Test", "leads: could not get out of the car"); _leadTest = false; return; }
            _leadTime = 0;
            return;
        }
        var w = _main.Walker!;
        var leads = _main.Leads!;
        double before = _leadTime;
        _leadTime += delta;
        bool Now(double t) => before < t && _leadTime >= t;
        if (Now(0.5)) leads.Look(w.GlobalPosition, "crate", 0.3, 0.6);
        var lead = leads.All.FirstOrDefault();
        if (Now(3))
        {
            if (lead == null) { Log.Error("Test", "leads: no lead found"); _leadTest = false; return; }
            Log.Info("Test", $"leads: lead {lead.Id}: {lead.Where} ({lead.Kind}), {lead.Gang}");
            _main.MapView!.Toggle(); _main.MapView.SetZoom(1);
        }
        if (Now(4.5)) Shot("leads_map");
        if (Now(5)) _main.MapView!.Toggle();
        if (Now(5.5) && lead != null)
        {
            // 150 m short of it, on the line from where you are
            var target = WorldOrigin.ToLocal(lead.X, 0, lead.Z);
            var from = w.GlobalPosition; from.Y = 0; target.Y = 0;
            var p = target + (from - target).Normalized() * 150f;
            var hit = w.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 300, p + Vector3.Down * 300, Bodies.Ground | Bodies.Roads));
            // no ground collision that far out yet: keep the height you're at (the town is flat) until it streams in
            w.GlobalPosition = (hit.Count > 0 ? (Vector3)hit["position"] : p with { Y = w.GlobalPosition.Y + 1f }) + Vector3.Up * 0.5f;
            w.Velocity = Vector3.Zero;
        }
        if (Now(9) && lead != null)
        {
            var (px, _, pz) = WorldOrigin.ToWorld(w.GlobalPosition);
            Log.Info("Test", $"leads: you are {Math.Sqrt((px - lead.X) * (px - lead.X) + (pz - lead.Z) * (pz - lead.Z)):0} m from the place");
            if (lead.Stash != null) { var (sx, sy, sz) = WorldOrigin.ToWorld(lead.Stash.GlobalPosition); Log.Info("Test", $"leads: place {lead.X:0},{lead.Z:0}; stash {sx:0},{sy:0},{sz:0} (local {lead.Stash.GlobalPosition}); you {px:0},{pz:0} (local {w.GlobalPosition})"); }
            var guards = _main.Bandits!.All.Where(b => b.Guarding == lead.Id).ToList();
            Log.Info("Test", $"leads: stage {lead.Stage}, stash {(lead.Stash != null ? $"at {lead.Stash.GlobalPosition.DistanceTo(w.GlobalPosition):0} m" : "none")}, {guards.Count} guards: "
                + string.Join(", ", guards.Select(b => $"{b.Gun.Id} {b.GlobalPosition.DistanceTo(lead.Stash?.GlobalPosition ?? b.GlobalPosition):0} m from it")));
            if (lead.Stash != null)
            {
                // 12 m off, third person, looking at the camp
                var s = lead.Stash.GlobalPosition;
                var away = w.GlobalPosition - s; away.Y = 0;
                w.GlobalPosition = s + away.Normalized() * 12f + Vector3.Up * 0.5f;
                w.SetView(false);
            }
        }
        if (Now(9.3) && lead?.Stash != null) w.Face(lead.Stash.GlobalPosition);
        if (Now(10)) Shot("leads_camp");
        if (Now(10.5) && lead != null)
        {
            foreach (var b in _main.Bandits!.All.Where(b => b.Guarding == lead.Id && !b.Dead).ToList())
                b.Hit(new OnFoot.BulletHit(b.GlobalPosition + Vector3.Up * 1.0f, Vector3.Up, Vector3.Forward * 800, 0.008f, 12, 500));
            Log.Info("Test", $"leads: guards shot, {lead.Guards} left");
        }
        if (Now(11) && lead?.Stash != null)
        {
            var s = lead.Stash.GlobalPosition;
            var away = w.GlobalPosition - s; away.Y = 0;
            w.GlobalPosition = s + away.Normalized() * 3.5f + Vector3.Up * 0.3f;
            Log.Info("Test", $"leads: stash look: {string.Join(", ", lead.Stash.GetChildren().Select(c => c.GetType().Name + (c is MeshInstance3D m && m.Mesh != null ? " " + m.Mesh.GetAabb().Size : "")))}");
        }
        if (Now(11.3) && lead?.Stash != null) { w.SetView(true); w.Face(lead.Stash.GlobalPosition); }   // first person: the camp can be cramped
        if (Now(12)) Shot("leads_stash");
        if (Now(12.5))
        {
            bool opened = lead?.Stash != null && _main.TryOpenCache(lead.Stash);
            Log.Info("Test", $"leads: stash opened {opened}, stage {lead?.Stage}; carrying food {w.Body.Food}, water {w.Body.Water}, medkits {w.Body.Medkits}; {w.Guns.Status}");
        }
        if (Now(13.2)) Shot("leads_taken");
        if (Now(15))
        {
            Log.Info("Test", $"leads: {leads.All.Count} leads now: " + string.Join("; ", leads.All.Select(l => $"{l.Id} {l.Stage} {l.Where}")));
            _main.MapView!.Toggle(); _main.MapView.SetZoom(3);
        }
        if (Now(16)) { Shot("leads_map_after"); _main.MapView!.Toggle(); Log.Info("Test", "leads: done"); _leadTest = false; }
    }

    /// <summary>The survival test (D46).</summary>
    private void LootTest(double delta)
    {
        if (_lootTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { Log.Error("Test", "loot: could not get out of the car"); _loot = false; return; }
            _lootTime = 0;
            return;
        }
        var w = _main.Walker!;
        var b = w.Body;
        double before = _lootTime;
        _lootTime += delta;
        bool Now(double t) => before < t && _lootTime >= t;
        if (Now(5))
        {
            var all = _main.Caches?.Live.ToList() ?? new();
            Log.Info("Test", $"loot: {all.Count} crates within reach of the streamer: " + string.Join("; ", all.Take(8).Select(c => $"{c.Kind} '{c.Place}' {c.GlobalPosition.DistanceTo(w.GlobalPosition):0} m")));
            _lootCache = all.OrderBy(c => c.GlobalPosition.DistanceTo(w.GlobalPosition)).FirstOrDefault();
            if (_lootCache == null) { Log.Error("Test", "loot: no crate anywhere near"); _loot = false; return; }
            // walk up to it (a step away, facing it), third person to see it
            var to = _lootCache.GlobalPosition - w.GlobalPosition; to.Y = 0;
            w.GlobalPosition = _lootCache.GlobalPosition - to.Normalized() * 1.8f + Vector3.Up * 0.3f;
            w.SetView(false);
        }
        if (Now(5.3) && _lootCache != null) w.Face(_lootCache.GlobalPosition);
        if (Now(6.5)) Shot("loot_crate");
        if (Now(7))
        {
            var start = (b.Food, b.Water, b.Medkits);
            bool opened = _main.TryOpenCache();
            Log.Info("Test", $"loot: opened {opened} ({_lootCache?.Kind} '{_lootCache?.Place}'): food {start.Food}→{b.Food}, water {start.Water}→{b.Water}, medkits {start.Medkits}→{b.Medkits}");
        }
        if (Now(7.4)) Shot("loot_opened");
        if (Now(8))
        {
            b.Food = Math.Max(b.Food, 1); b.Water = Math.Max(b.Water, 1); b.Medkits = Math.Max(b.Medkits, 1);
            b.Hunger = 30; b.Thirst = 30;
        }
        if (Now(8.5)) Log.Info("Test", $"loot: hungry and thirsty → hunger {b.Hunger:0}, thirst {b.Thirst:0}, food {b.Food}, water {b.Water} (used by themselves)");
        if (Now(9)) { b.Damage(55); b.UseMedkit(); Log.Info("Test", $"loot: hurt to {b.Health:0}, medkit used ({b.Medkits} left)"); }
        if (Now(15))
        {
            Log.Info("Test", $"loot: health after the medkit {b.Health:0}");
            Shot("loot_hud");
            Log.Info("Test", "loot: done");
            _loot = false;
        }
    }

    private bool _banditLook, _tpGun, _death, _crouchTest, _carBandits, _soundTest;
    private double _soundTime = -1;
    private AudioEffectRecord? _rec;
    private double _carBanditTime = -1;
    private double _crouchTime = -1;
    private double _deathTime = -1;
    private double _tpTime = -1;
    private double _lookTime = -1;

    /// <summary>The game's own sound, recorded (D53).</summary>
    private void SoundTest(double delta)
    {
        if (_soundTime < 0)
        {
            if (_settledFor < 2.0) return;
            _rec = new AudioEffectRecord { Format = AudioStreamWav.FormatEnum.Format16Bits };
            AudioServer.AddBusEffect(0, _rec, 0);
            _rec.SetRecordingActive(true);
            _soundTime = 0;
            return;
        }
        var c = _main.Car!;
        double before = _soundTime;
        _soundTime += delta;
        bool Now(double t) => before < t && _soundTime >= t;
        var ac = Audio.Acoustics.Instance;
        if (Now(1)) Log.Info("Test", $"sound: in the car: place {ac?.Place}, enclosure {ac?.Enclosure:F2}, walls {ac?.Walls.Count}");
        if (Now(2)) { _main.ToggleOnFoot(); _main.Walker!.Guns.Equip(Main.Args.GetValueOrDefault("gun", "pistol")); }
        if (Now(3.5))
        {
            Log.Info("Test", $"sound: on foot: place {ac?.Place}, enclosure {ac?.Enclosure:F2}; shot at {_soundTime:F2} s; echoes due at " +
                string.Join(", ", ac!.Walls.OrderBy(w => w.Distance).Where((_, i) => i % 3 == 0).Take(5).Select(w => $"{_soundTime + 2 * w.Distance / 343:F2}")));
            _main.Walker!.Guns.TestTrigger = true;
        }
        if (Now(3.6)) _main.Walker!.Guns.TestTrigger = false;
        if (Now(6))
        {
            // out into the open: the free-camera spot over the river meadows would do; here, 60 m up for a crude "open"
            _main.ToggleOnFoot();
            c.Autopilot = car => (_soundTime < 16 ? 1f : 0f, _soundTime >= 16 ? 1f : 0f, 0f);
        }
        foreach (var t in new[] { 7.0, 9.0, 11.0, 13.0, 15.0, 17.0 })
            if (Now(t)) Log.Info("Test", $"sound: driving {MathF.Abs(c.Speed) * 3.6f:F0} km/h, throttle {c.Throttle:F1}, rpm {c.GetNodeOrNull<Audio.EngineSound>("EngineSound")?.Rpm:F0}, gear {c.GetNodeOrNull<Audio.EngineSound>("EngineSound")?.Gear}, place {ac?.Place}");
        if (Now(18))
        {
            _rec!.SetRecordingActive(false);
            var wav = _rec.GetRecording();
            string path = Path.Combine(_shots, "sound.wav");
            wav?.SaveToWav(path);
            Log.Info("Test", $"sound: recorded {wav?.GetLength():F1} s to {path}");
            c.Autopilot = null;
            _soundTest = false;
        }
    }

    /// <summary>Bandits and the car (D52).</summary>
    private void CarBanditTest(double delta)
    {
        if (_carBanditTime < 0)
        {
            if (_settledFor < 2.5 || _main.Bandits == null) return;
            _carBanditTime = 0;
            var c = _main.Car!;
            int n = _main.Bandits.SpawnAhead(c.GlobalPosition, c.Rotation.Y, 30);
            Log.Info("Test", $"carbandits: {n} spawned 30 m from the car; driving {c.Driving}; your health {_main.Walker?.Body.Health:0}");
            return;
        }
        double before = _carBanditTime;
        _carBanditTime += delta;
        bool Now(double t) => before < t && _carBanditTime >= t;
        var f = _main.Bandits!;
        string States() => string.Join(", ", f.All.Select(b => $"{b.Gun.Id} {b.State}"));
        foreach (var t in new[] { 3.0, 6.0, 10.0 })
            if (Now(t)) Log.Info("Test", $"carbandits: {t:0} s: {States()}; your health {_main.Walker?.Body.Health:0}");
        if (Now(6)) Shot("carbandits");
        if (Now(10.5)) { Log.Info("Test", "carbandits: done"); _carBandits = false; }
    }

    /// <summary>Crouching in third person (D52).</summary>
    private void CrouchTest(double delta)
    {
        if (_crouchTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { _crouchTest = false; return; }
            _crouchTime = 0;
            var wk = _main.Walker!;
            wk.SetView(false);
            wk.GlobalPosition += new Vector3(-MathF.Sin(wk.Yaw), 0, -MathF.Cos(wk.Yaw)) * 8 + Vector3.Up * 0.3f;
            wk.Autopilot = w => (_crouchTime > 4 && _crouchTime < 6 ? new Vector2(0, 1) : Vector2.Zero, false);
            return;
        }
        var w = _main.Walker!;
        double before = _crouchTime;
        _crouchTime += delta;
        bool Now(double t) => before < t && _crouchTime >= t;
        w.CameraOrbit = 1.4f;
        if (Now(1)) { Shot("crouch_stand"); w.Crouch(true); }
        if (Now(3)) { Shot("crouch_still"); Log.Info("Test", $"crouch: crouching {w.Crouching}, {w.DebugPose()}"); }
        if (Now(5.5)) { Shot("crouch_walk"); Log.Info("Test", $"crouch: walking {w.Speed:F1} m/s"); }
        if (Now(6.5)) { w.Crouch(false); w.Autopilot = null; Log.Info("Test", "crouch: done"); _crouchTest = false; }
    }

    private OnFoot.Doorstep? _deathHome;
    private Vector3 _fellAt;

    /// <summary>Dying and waking (D55): the fall seen from the eyes, waking 1.5-3 km away, your things on the map,
    /// going back for them; then a safehouse claimed at a home's wall, dying again and waking there.</summary>
    private void DeathTest(double delta)
    {
        if (_deathTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { _death = false; return; }
            OnFoot.Safehouse.Clear(Main.MapName);
            _deathTime = 0;
            return;
        }
        var w = _main.Walker!;
        double before = _deathTime;
        _deathTime += delta;
        bool Now(double t) => before < t && _deathTime >= t;
        string Where(Vector3 local) { var (x, _, z) = WorldOrigin.ToWorld(local); return $"{x:0},{z:0}"; }
        if (Now(3))
        {
            // a full pack and some spare rounds, then a bullet from ahead-left
            w.Body.Food = 3; w.Body.Water = 2; w.Body.Medkits = 1;
            w.Guns.AddAmmo("ak", 40);
            w.SetView(false);
            Log.Info("Test", $"death: before: food {w.Body.Food}, water {w.Body.Water}, medkits {w.Body.Medkits}, at {Where(w.GlobalPosition)}");
            w.LastShot = new Vector3(-MathF.Sin(w.Yaw) + 0.5f, 0, -MathF.Cos(w.Yaw)) * -800;
            w.Body.Damage(500);
            _fellAt = w.GlobalPosition;
        }
        if (Now(3.2)) Shot("death_falling");
        if (Now(4.0)) Shot("death_down");
        if (Now(4.5))
        {
            Log.Info("Test", $"death: down {_main.PlayerDown}, dead {w.Dead}");
            _main.Respawn();
            Log.Info("Test", $"death: woke at {Where(w.GlobalPosition)}; carrying food {w.Body.Food}, water {w.Body.Water}, medkits {w.Body.Medkits}; health {w.Body.Health:0}");
        }
        if (Now(12)) Shot("death_awake");
        if (Now(12.5)) { _main.MapView!.Toggle(); _main.MapView.SetZoom(3); }
        if (Now(13.5)) { Shot("death_map"); _main.MapView!.Toggle(); }
        if (Now(14))
        {
            // back to the body
            var pkg = _main.Caches!.Live.FirstOrDefault(c => c.Key.StartsWith("death:"));
            if (pkg == null) { Log.Error("Test", "death: no package"); _death = false; return; }
            Log.Info("Test", $"death: package at {Where(pkg.GlobalPosition)}, {pkg.GlobalPosition.DistanceTo(w.GlobalPosition):0} m away");
            var away = new Vector3(1, 0, 0.4f).Normalized();
            w.GlobalPosition = pkg.GlobalPosition + away * 2.6f + Vector3.Up * 0.5f;
        }
        if (Now(20))
        {
            var pkg = _main.Caches!.Live.FirstOrDefault(c => c.Key.StartsWith("death:"));
            if (pkg != null) { w.Face(pkg.GlobalPosition + Vector3.Up * 0.3f); Shot("death_body"); }
        }
        if (Now(20.5))
        {
            var pkg = _main.Caches!.Live.FirstOrDefault(c => c.Key.StartsWith("death:"));
            bool took = pkg != null && _main.TryOpenCache(pkg);
            Log.Info("Test", $"death: took your things {took}: food {w.Body.Food}, water {w.Body.Water}, medkits {w.Body.Medkits}");
        }
        if (Now(21))
        {
            // a home nearby: stand at its wall and hold K
            var (x, _, z) = WorldOrigin.ToWorld(w.GlobalPosition);
            _deathHome = OnFoot.Homes.Pick(_main.World!, x, z, new Random(5), 20, 250);
            if (_deathHome is not { } h) { Log.Error("Test", "death: no home near"); _death = false; return; }
            w.GlobalPosition = WorldOrigin.ToLocal(h.X, _main.World!.Height(h.X, h.Z) + 0.5, h.Z);
            w.Face(w.GlobalPosition + new Vector3(MathF.Sin(h.Yaw), 0, MathF.Cos(h.Yaw)) * 3 + Vector3.Up * 1.5f);   // the wall
            Log.Info("Test", $"death: at a home: {h.Address} ({h.X:0},{h.Z:0})");
        }
        if (Now(25)) { Shot("death_claim_prompt"); _main.TestClaim = true; }
        if (Now(26)) Shot("death_claiming");
        if (Now(27.6)) { _main.TestClaim = false; Log.Info("Test", $"death: safehouse {(_main.SafehouseAt is { } sh ? $"{sh.Address} at {sh.X:0},{sh.Z:0}" : "none")}"); }
        if (Now(28)) { w.LastShot = Vector3.Zero; w.Body.Damage(500); }
        if (Now(30)) _main.Respawn();
        if (Now(34))
        {
            var (x, _, z) = WorldOrigin.ToWorld(w.GlobalPosition);
            var sh = _main.SafehouseAt;
            Log.Info("Test", $"death: woke {(sh is { } h2 ? Math.Sqrt((x - h2.X) * (x - h2.X) + (z - h2.Z) * (z - h2.Z)) : -1):0.0} m from the safehouse door");
            Shot("death_safehouse");
            OnFoot.Safehouse.Clear(Main.MapName);
            Log.Info("Test", "death: done");
            _death = false;
        }
    }

    /// <summary>The gun in the walker's hands in third person (D49).</summary>
    private void TpGunTest(double delta)
    {
        if (_tpTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { _tpGun = false; return; }
            _tpTime = 0;
            var wk = _main.Walker!;
            wk.SetView(false);
            wk.Autopilot = w =>
            {
                double t = _tpTime % 6;
                return t < 1.5 ? (Vector2.Zero, false) : t < 3 ? (new Vector2(0, 0.45f), false) : t < 4.5 ? (new Vector2(0, 1), true) : (Vector2.Zero, false);
            };
            return;
        }
        var w = _main.Walker!;
        var g = w.Guns;
        double before = _tpTime;
        _tpTime += delta;
        bool Now(double t) => before < t && _tpTime >= t;
        string[] guns = { "ak", "pistol", "mosin" };
        int i = (int)(_tpTime / 6);
        if (i >= guns.Length) { w.Autopilot = null; g.TestTrigger = false; Log.Info("Test", "tpgun: done"); _tpGun = false; return; }
        double l = _tpTime - i * 6, b = before - i * 6;
        bool At(double x) => b < x && l >= x;
        if (g.Current?.Id != guns[i] && l < 0.5) g.Equip(guns[i]);
        w.CameraOrbit = 1.35f;   // from the side
        if (At(1.0)) Shot($"tpgun_{guns[i]}_stand");
        if (At(2.6)) Shot($"tpgun_{guns[i]}_walk");
        if (At(4.2)) Shot($"tpgun_{guns[i]}_sprint");
        if (At(4.8)) g.TestTrigger = true;
        if (At(5.0)) { Shot($"tpgun_{guns[i]}_fire"); g.TestTrigger = false; }
        if (At(5.5)) Log.Info("Test", $"tpgun: {guns[i]}: {g.Status}, first person {w.FirstPerson}");
    }

    /// <summary>A close look at one bandit (D47): the gun low, then shouldered at the player.</summary>
    private void BanditLook(double delta)
    {
        if (_lookTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { _banditLook = false; return; }
            _lookTime = 0;
            return;
        }
        var w = _main.Walker!;
        var f = _main.Bandits!;
        double before = _lookTime;
        _lookTime += delta;
        bool Now(double t) => before < t && _lookTime >= t;
        if (Now(0.3))
        {
            // clear of the car: 8 m up the street
            var fwd = new Vector3(-MathF.Sin(w.Yaw), 0, -MathF.Cos(w.Yaw));
            w.GlobalPosition += fwd * 8 + Vector3.Up * 0.3f;
        }
        if (Now(0.6))
        {
            w.SetView(false); w.Crouch(true);
            Log.Info("Test", $"banditlook: {f.SpawnAhead(w.GlobalPosition, w.Yaw, 6)} spawned");
            while (f.All.Count > 1) { var x = f.All[^1]; x.QueueFree(); f.Remove(x); }
            var fwd = new Vector3(-MathF.Sin(w.Yaw), 0, -MathF.Cos(w.Yaw));
            if (f.All.Count > 0) f.All[0].Place(w.GlobalPosition + fwd * 5, w.GlobalPosition + fwd * 5);
        }
        if (_lookTime > 0.7 && f.All.Count > 0) { w.Face(f.All[0].GlobalPosition + Vector3.Up * 1.0f); w.CameraOrbit = 0.5f; }
        if (Now(1.0)) { Shot("banditlook_relaxed"); Log.Info("Test", $"banditlook: {f.All[0].State}, {f.All[0].Gun.Id}"); }
        if (Now(1.2)) w.Crouch(false);
        if (Now(4.0)) { Shot("banditlook_aiming"); Log.Info("Test", $"banditlook: {f.All[0].State}; you {w.Body.Health:0} hp; {f.All[0].Heights()}"); }
        if (Now(4.5)) { w.CameraOrbit = 2.6f; }
        if (Now(5.0)) { Shot("banditlook_side"); Log.Info("Test", "banditlook: done"); _banditLook = false; }
    }

    /// <summary>The bandit test (D47).</summary>
    private void BanditTest(double delta)
    {
        if (_banditTime < 0)
        {
            if (_settledFor < 1.5) return;
            if (!_main.ToggleOnFoot()) { Log.Error("Test", "bandits: could not get out of the car"); _banditTest = false; return; }
            _banditTime = 0;
            return;
        }
        var w = _main.Walker!;
        var f = _main.Bandits!;
        double before = _banditTime;
        _banditTime += delta;
        bool Now(double t) => before < t && _banditTime >= t;
        string States() => string.Join(", ", f.All.Select(b => $"{b.Gun.Id} {b.State} {b.Health:0} hp {b.GlobalPosition.DistanceTo(w.GlobalPosition):0} m"));
        if (Now(1))
        {
            w.SetView(false);
            int n = f.SpawnAhead(w.GlobalPosition, w.Yaw, 25);
            Log.Info("Test", $"bandits: {n} spawned ahead: {States()}");
        }
        if (Now(1.5) && f.All.Count > 0) w.Face(f.All[0].GlobalPosition + Vector3.Up * 1.2f);
        if (Now(2)) Shot("bandits_spawned");
        // a close look in first person, aimed at one of them
        if (Now(3.5) && f.All.Count > 0) { w.SetView(true); if (w.Guns.Current?.Id != "ak") w.Guns.Equip("ak"); w.Guns.TestAim = true; }
        if (_banditTime is > 3.6 and < 4.6 && f.All.Count > 0) w.Face(f.All[0].GlobalPosition + Vector3.Up * 1.2f);
        if (Now(4.5)) Shot("bandits_sights");
        foreach (var t in new[] { 3.0, 5.0, 7.0, 12.0, 18.0 })
            if (Now(t)) Log.Info("Test", $"bandits: {t:0} s: {States()}; you {w.Body.Health:0} hp");
        // fight back: the AK, aimed at each one still standing in turn
        if (_banditTime > 4.6 && _banditTime < 30)
        {
            if (_banditTarget == null || _banditTarget.Dead || !GodotObject.IsInstanceValid(_banditTarget))
                _banditTarget = f.All.Where(b => !b.Dead).OrderBy(b => b.GlobalPosition.DistanceTo(w.GlobalPosition)).FirstOrDefault();
            var tb = _banditTarget;
            if (tb == null) { w.Guns.TestTrigger = false; if (_banditShot == 0) { _banditShot = 1; _banditTime = 30; } }
            else
            {
                w.Face(tb.GlobalPosition + Vector3.Up * (tb.State == OnFoot.Bandit.Mode.Engage ? 1.1f : 1.25f));
                // short bursts
                double ph = (_banditTime * 2.5) % 1.0;
                w.Guns.TestTrigger = ph < 0.3;
                if (w.Guns.Current != null && w.Guns.Status.Contains(" 0 /")) w.Guns.StartReload();
            }
        }
        if (Now(12)) Shot("bandits_fight");
        if (Now(30.2))
        {
            w.Guns.TestTrigger = false; w.Guns.TestAim = false;
            Log.Info("Test", $"bandits: after the fight: {States()}; killed {f.Killed}; you {w.Body.Health:0} hp; your shots {w.Guns.Fired}, hits on bandits {w.Guns.TargetHits} (last: {w.Guns.LastTarget})");
            var body = f.All.FirstOrDefault(b => b.Dead);
            if (body != null)
            {
                w.SetView(false);
                var to = body.GlobalPosition - w.GlobalPosition; to.Y = 0;
                w.GlobalPosition = body.GlobalPosition - to.Normalized() * 1.5f + Vector3.Up * 0.3f;
            }
        }
        if (Now(30.6)) { var body = f.All.FirstOrDefault(b => b.Dead); if (body != null) w.Face(body.GlobalPosition); }
        if (Now(31.5)) Shot("bandits_body");
        if (Now(32)) { bool opened = _main.TryOpenCache(); Log.Info("Test", $"bandits: searched a body: {opened}; carrying food {w.Body.Food}, water {w.Body.Water}, medkits {w.Body.Medkits}; {w.Guns.Status}"); }
        if (Now(32.5)) { Shot("bandits_searched"); Log.Info("Test", "bandits: done"); _banditTest = false; }
    }

    private bool Settled => _main.Terrain.Settled && _main.Content.Pending == 0 && _main.Terrain.PendingCollision == 0;

    public void Process(double delta)
    {
        _elapsed += delta;
        _frames++;
        if (_elapsed > 600) { Log.Error("Test", "timed out"); _main.GetTree().Quit(2); return; }
        _settledFor = Settled ? _settledFor + delta : 0;

        if (Main.Args.GetValueOrDefault("autotest", "") == "grassbench") { GrassBench(delta); return; }
        if (Main.Args.GetValueOrDefault("autotest", "") == "roadscan") { if (_settledFor > 1) { RoadScan(); _main.GetTree().Quit(); } return; }
        if (_view < _views.Count && _views.Count > 0)
        {
            if (_view >= 0 && _settledFor < 1.5) return;
            if (_view >= 0) { Shot($"view{_view:00}"); if (Main.Args.ContainsKey("dump")) Log.Info("Test", "dump: " + DebugDump.Take(_main)); }
            _view++;
            if (_view < _views.Count)
            {
                var p = _views[_view];
                double x = Num(p[0]), z = Num(p[1]);
                _main.Teleport(x, z, (float)Num(p.Length > 2 ? p[2] : "40"), (float)Num(p.Length > 3 ? p[3] : "0"), (float)Num(p.Length > 4 ? p[4] : "-15"));
                _settledFor = 0;
                Log.Info("Test", $"view {_view}: {string.Join(":", p)}");
            }
            return;
        }

        if (_fly)
        {
            if (!_flyStarted)
            {
                if (_settledFor < 1.5) return;
                _flyStarted = true;
                Shot("fly_start");
                Log.Info("Test", $"fly: start, {_speed} m/s");
            }
            _frameMs.Add(delta * 1000);
            var cam = _main.Camera;
            var step = -cam.Basis.Z with { Y = 0 };
            cam.Position += step.Normalized() * (float)(_speed * delta);
            cam.Position = cam.Position with { Y = cam.GroundHeight + 40f };
            _flown += _speed * delta;
            var (fx, fz) = _main.Focus;
            if (!_main.Terrain.HasGround(fx, fz)) _holes++;
            if (_flown >= 6000)
            {
                Shot("fly_end");
                _frameMs.Sort();
                double p50 = _frameMs[_frameMs.Count / 2], p99 = _frameMs[(int)(_frameMs.Count * 0.99)], max = _frameMs[^1];
                int over33 = _frameMs.FindAll(m => m > 33.4).Count;
                Log.Info("Test", $"fly: {_frameMs.Count} frames, median {p50:F1} ms, p99 {p99:F1} ms, worst {max:F1} ms, {over33} frames over 33 ms, {_holes} frames without collision ground");
                _fly = false;
            }
            else return;
        }
        if (_walk && _main.Car is { } wcar)
        {
            if (_walkTime < 0)
            {
                if (_settledFor < 1.5) return;
                if (!_main.ToggleOnFoot()) { Log.Error("Test", "walk: could not get out of the car"); _walk = false; return; }
                _walkTime = 0;
                _walkFrom = _main.Walker!.GlobalPosition;
                Log.Info("Test", "walk: out of the car");
                _main.Walker.Autopilot = w =>
                {
                    double t = _walkTime;
                    if (t < 5) return (new Vector2(0, 1), false);          // walk... (run speed)
                    if (t < 8) return (new Vector2(0, 1), true);           // sprint
                    if (t < 10) return (new Vector2(0.6f, 0.8f), false);   // a curve
                    return (new Vector2(0, 0), false);
                };
                return;
            }
            _walkTime += delta;
            var wk = _main.Walker!;
            double[] shots = { 0.6, 2.5, 6.5, 9.5, 11.0 };
            wk.CameraOrbit = _walkTime is > 1.5 and < 7.5 ? 1.5f : 0f;   // side view for the run and the sprint shots
            if (_walkShot < shots.Length && _walkTime >= shots[_walkShot])
            {
                Shot($"walk_{_walkShot}");
                Log.Info("Test", $"walk: {_walkTime:F1} s, {wk.Speed * 3.6f:F1} km/h, {wk.GlobalPosition.DistanceTo(_walkFrom):F1} m from where it got out, on floor {wk.IsOnFloor()}");
                _walkShot++;
                if (_walkShot == 1) Log.Info("Test", "walk pose: " + wk.DebugPose());
                if (_walkShot == 4)
                {
                    // the real keys, through Godot's input (V broke once without anyone noticing)
                    bool before = wk.FirstPerson;
                    Input.ParseInputEvent(new InputEventKey { Keycode = Key.V, Pressed = true });
                    Input.ParseInputEvent(new InputEventKey { Keycode = Key.V, Pressed = false });
                    Input.FlushBufferedEvents();
                    Log.Info("Test", $"walk: V switches the view: {(wk.FirstPerson != before ? "yes" : "NO")} (first person {before} → {wk.FirstPerson})");
                    wk.Body.Medkits = 1; wk.Body.Damage(30);
                    Input.ParseInputEvent(new InputEventKey { Keycode = Key.H, Pressed = true });
                    Input.ParseInputEvent(new InputEventKey { Keycode = Key.H, Pressed = false });
                    Input.FlushBufferedEvents();
                    Log.Info("Test", $"walk: H uses a medkit: {(wk.Body.Medkits == 0 ? "yes" : "NO")}");
                }
            }
            if (_walkTime < 12) return;
            // back to the car the quick way, then in
            wk.Autopilot = null;
            wk.GlobalPosition = wcar.GlobalPosition - wcar.GlobalTransform.Basis.X * 1.8f;
            Log.Info("Test", _main.ToggleOnFoot() ? "walk: back in the car" : "walk: could not get back in");
            Log.Info("Test", $"walk: done, driving {wcar.Driving}");
            _walk = false;
        }
        if (_shoot && _main.Car != null) { Shoot(delta); return; }
        if (_rangeTest && _main.Car != null) { RangeTest(delta); return; }
        if (_loot && _main.Car != null) { LootTest(delta); return; }
        if (_banditTest && _main.Car != null) { BanditTest(delta); return; }
        if (_banditLook && _main.Car != null) { BanditLook(delta); return; }
        if (_tpGun && _main.Car != null) { TpGunTest(delta); return; }
        if (_death && _main.Car != null) { DeathTest(delta); return; }
        if (_crouchTest && _main.Car != null) { CrouchTest(delta); return; }
        if (_carBandits && _main.Car != null) { CarBanditTest(delta); return; }
        if (_soundTest && _main.Car != null) { SoundTest(delta); return; }
        if (_leadTest && _main.Car != null) { LeadTest(delta); return; }
        if (_leanTest && _main.Car != null) { LeanTest(delta); return; }
        if (_ragdollTest && _main.Car != null) { RagdollTest(delta); return; }
        if (_respawn)
        {
            if (_main.Car == null)
            {
                if (_settledFor < 1.5) return;
                var (sx, sz) = _main.Focus;
                _main.SpawnCar(sx, sz);
                Log.Info("Test", "respawn: car placed");
                return;
            }
            if (!_main.Flying)
            {
                if (_roundWait >= 0 && (_roundWait += delta) < 4) return;   // drive here, then sit for a moment
                if (_round >= _rounds) { _respawn = false; Log.Info("Test", $"respawn: {_rounds} rounds, no crash"); }
                else
                {
                    _main.ToggleFreeCamera();
                    var cam = _main.Camera;
                    cam.Yaw = _round * 1.1f; cam.Rotation = cam.Rotation with { Y = cam.Yaw };
                    _roundFlown = 0; _roundWait = -1;
                    Log.Info("Test", $"respawn: round {_round + 1}, free camera, flying {_distance:F0} m");
                    return;
                }
            }
            else
            {
                var cam = _main.Camera;
                var step = -cam.Basis.Z with { Y = 0 };
                double move = Math.Min(_speed * delta, _distance - _roundFlown);
                cam.Position += step.Normalized() * (float)move;
                cam.Position = cam.Position with { Y = cam.GroundHeight + 40f };
                _roundFlown += move;
                if (_roundFlown < _distance) return;
                var (fx, fz) = _main.Focus;
                Log.Info("Test", $"respawn: round {_round + 1}, pressing F at {fx:F0},{fz:F0}");
                _main.ToggleFreeCamera();
                _round++;
                _roundWait = 0;
                return;
            }
        }
        if (_map && _main.MapView is { } view && _main.Nav is { } nav)
        {
            // --autotest=map [--dest=x,z]: open the map, route to the destination, screenshot at two zooms.
            if (_mapTime < 0)
            {
                if (_settledFor < 1.5) return;
                _mapTime = 0;
                var (fx, fz) = _main.Focus;
                if (Main.Args.TryGetValue("dest", out var d))
                {
                    var p = d.Split(',');
                    nav.SetDestination(new Vector2((float)Num(p[0]), (float)Num(p[1])), new Vector2((float)fx, (float)fz));
                }
                view.Toggle();
                view.SetZoom(0);   // closest: vector roads, buildings, every street name
            }
            _mapTime += delta;
            if (nav.Routing && _mapTime < 60) return;
            if (_mapTime < 1.5) return;
            if (_mapTime < 100) { Shot("map_near"); view.SetZoom(2); _mapTime = 100; return; }
            if (_mapTime < 101.5) return;
            Shot("map_far");
            Log.Info("Test", $"map: route {(nav.Route != null ? $"{nav.Route.Length / 1000f:F1} km" : "none")}");
            view.Toggle();
            _map = false;
        }
        if (_drive && _main.Car is { } car && _main.World is { } map)
        {
            if (!_driveStarted)
            {
                if (_settledFor < 1.5) return;
                _driveStarted = true;
                Shot("drive_start");
                // With --dest: follow the GPS route (steer at the route point ~20 m ahead, slow for bends).
                // Without: keep to the nearest road, steering at the carriageway point 18 m ahead.
                if (Main.Args.TryGetValue("dest", out var dest) && _main.Nav is { } nav0)
                {
                    var p = dest.Split(',');
                    var (fx, fz) = _main.Focus;
                    nav0.SetDestination(new Vector2((float)Num(p[0]), (float)Num(p[1])), new Vector2((float)fx, (float)fz));
                    _driveLimit = double.Parse(Main.Args.GetValueOrDefault("seconds", "180"), CultureInfo.InvariantCulture);
                }
                car.Autopilot = c =>
                {
                    var fwd = (-c.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
                    var (cx, _, cz) = World.WorldOrigin.ToWorld(c.GlobalPosition);
                    float steer = 0, target = 17f;
                    if (_main.Nav?.Route is { } route)
                    {
                        var here = new Vector2((float)cx, (float)cz);
                        _routeHint = route.Nearest(here, _routeHint, out _);
                        // a short look-ahead at low speed so sharp town corners are not cut into the kerb
                        float look = Math.Clamp(Math.Abs(c.Speed) * 0.9f, 7f, 40f);
                        int i = _routeHint;
                        while (i + 1 < route.Points.Length && route.Along[i] - route.Along[_routeHint] < look) i++;
                        // keep right: aim 1.8 m to the right of the centre line
                        var seg = route.Points[Math.Min(i + 1, route.Points.Length - 1)] - route.Points[Math.Max(i - 1, 0)];
                        var right = seg.LengthSquared() > 0.01f ? new Vector2(-seg.Y, seg.X).Normalized() : Vector2.Zero;
                        var aim = route.Points[i] + right * 1.8f;
                        var to = World.WorldOrigin.ToLocal(aim.X, 0, aim.Y) - c.GlobalPosition;
                        to.Y = 0;
                        steer = Mathf.Clamp(fwd.Cross(to.Normalized()).Y * 2.2f, -1f, 1f);
                        // speed from the sharpest bend in the next 120 m: v = sqrt(a * r) with a = 3.5 m/s²
                        float maxAng = 0;
                        for (int k = _routeHint; k + 2 < route.Points.Length && route.Along[k] - route.Along[_routeHint] < 120f; k++)
                        {
                            var a = route.Points[k + 1] - route.Points[k];
                            int m = k + 1;
                            while (m + 1 < route.Points.Length && route.Points[m].DistanceTo(route.Points[k + 1]) < 15f) m++;
                            var b = route.Points[m] - route.Points[k + 1];
                            if (a.LengthSquared() > 0.01f && b.LengthSquared() > 0.01f) maxAng = Math.Max(maxAng, Math.Abs(a.AngleTo(b)));
                        }
                        float radius = 15f / Math.Max(0.02f, maxAng);
                        target = Math.Clamp(MathF.Sqrt(3.5f * radius), 6f, 38f);
                    }
                    else if (map.NearestRoad(cx + fwd.X * 18, cz + fwd.Z * 18, 25f, 1.8f, out double px, out _, out double pz, out _, Map.RoadClass.Residential))
                    {
                        var to = World.WorldOrigin.ToLocal(px, 0, pz) - c.GlobalPosition;
                        to.Y = 0;
                        steer = Mathf.Clamp(fwd.Cross(to.Normalized()).Y * 2.5f, -1f, 1f);
                    }
                    // pinned (wanting to go, not moving): back off for a moment with the wheel the other way
                    float dt = 1f / Engine.PhysicsTicksPerSecond;
                    if (_reverseLeft > 0f)
                    {
                        _reverseLeft -= dt;
                        return (0f, 0.7f, _reverseSteer);
                    }
                    if (MathF.Abs(c.Speed) < 0.6f) _stuckTime += dt; else _stuckTime = 0f;
                    if (_stuckTime > 2.5f)
                    {
                        _stuckTime = 0f; _reverseLeft = 2.5f; _reverseSteer = -MathF.Sign(steer == 0 ? 1f : steer); _reversals++;
                        Log.Info("Test", $"drive: stuck at {cx:0},{cz:0}, backing off");
                    }
                    return (c.Speed < target ? 0.8f : 0f, c.Speed > target + 2 ? 0.5f : 0f, steer);
                };
                Log.Info("Test", "drive: start");
            }
            _driveTime += delta;
            _driven += Math.Abs(car.Speed) * delta;
            _maxSpeed = Math.Max(_maxSpeed, car.Speed);
            if (_main.Nav is { } nv && nv.Route == null && !nv.Routing && nv.Destination == null && _driveLimit > 20) _driveTime = _driveLimit; // arrived
            if (!car.OnRoad) _offRoad += delta;
            if (Math.Abs(_driveTime - 8) < delta) Shot("drive_8s");
            if (_driveTime >= 60 && (int)(_driveTime / 60) != (int)((_driveTime - delta) / 60)) Shot($"drive_{(int)_driveTime}s");
            if (_driveTime < _driveLimit) return;
            Shot("drive_end");
            var (wx, _, wz) = World.WorldOrigin.ToWorld(car.GlobalPosition);
            Log.Info("Test", $"drive: {_driveTime:F0} s, {_driven / 1000:F2} km, {(_main.Nav?.Destination == null && _driveLimit > 20 ? "ARRIVED, " : "")}{(_main.Nav?.Route != null ? $"{_main.Nav.Remaining / 1000:F2} km left, " : "")}top {_maxSpeed * 3.6:F0} km/h, {_offRoad:F1} s off road, {_reversals} times stuck, ended at {wx:F0},{wz:F0}, upright {car.GlobalTransform.Basis.Y.Y:F2}");
            _drive = false;
        }
        if (Map.BadBuildingAdapter.Enabled) Log.Info("Test", Map.BadBuildingAdapter.Stats());
        Log.Info("Test", "done");
        _main.GetTree().Quit(0);
    }

    private void Shot(string name)
    {
        var img = _main.GetViewport().GetTexture().GetImage();
        string path = Path.Combine(_shots, name + ".png");
        img.SavePng(path);
        Log.Info("Test", $"saved {path}");
    }

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);
}
