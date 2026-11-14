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
        _speed = double.Parse(Main.Args.GetValueOrDefault("speed", "40"), CultureInfo.InvariantCulture);
    }

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
                view.SetZoom(1);
            }
            _mapTime += delta;
            if (nav.Routing && _mapTime < 60) return;
            if (_mapTime < 1.5) return;
            if (_mapTime < 100) { Shot("map_near"); view.SetZoom(3); _mapTime = 100; return; }
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
