using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BalticDrive.Core;
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
/// <item><c>--autotest=shoot</c> — out of the car, then each gun in turn (D40): hip view, aim down the sights, fire
/// (a burst for the AK), reload; screenshots of each and the bullets' hits in the log.</item>
/// <item><c>--autotest=range</c> — out of the car, set up the shooting range (D42), shoot each target with the AK
/// (aimed straight at it, the 100 m plate with the Mosin), screenshots before and after, and what each hit said.</item>
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
    private bool _walk, _shoot, _rangeTest;
    private double _rangeTime = -1;
    private int _rangeTarget;
    private double _shootTime = -1;
    private int _shootShot;
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
        if (Now(0.8)) Shot($"shoot_{id}_hip");
        if (Now(1.0)) g.TestAim = true;
        if (Now(1.8)) Shot($"shoot_{id}_aim");
        if (Now(2.0)) g.TestTrigger = true;
        if (Now(id == "ak" ? 2.5 : 2.05)) g.TestTrigger = false;
        if (Now(2.02)) Shot($"shoot_{id}_fire");
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

    private bool Settled => _main.Terrain.Settled && _main.Content.Pending == 0 && _main.Terrain.PendingCollision == 0;

    public void Process(double delta)
    {
        _elapsed += delta;
        _frames++;
        if (_elapsed > 600) { Log.Error("Test", "timed out"); _main.GetTree().Quit(2); return; }
        _settledFor = Settled ? _settledFor + delta : 0;

        if (_view < _views.Count && _views.Count > 0)
        {
            if (_view >= 0 && _settledFor < 1.5) return;
            if (_view >= 0) Shot($"view{_view:00}");
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
                if (_walkShot == 4) wk.SetView(true);
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
