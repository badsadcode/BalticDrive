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

    public Tests(Main main)
    {
        _main = main;
        _shots = Main.Args.GetValueOrDefault("shots", ProjectSettings.GlobalizePath("user://shots"));
        Directory.CreateDirectory(_shots);
        if (Main.Args.TryGetValue("views", out var v))
            foreach (var s in v.Split(';', StringSplitOptions.RemoveEmptyEntries)) _views.Add(s.Split(':'));
        _fly = Main.Args.GetValueOrDefault("autotest", "").Contains("fly");
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
        if (_drive && _main.Car is { } car && _main.World is { } map)
        {
            if (!_driveStarted)
            {
                if (_settledFor < 1.5) return;
                _driveStarted = true;
                Shot("drive_start");
                // Keep to the road: steer at the carriageway point 18 m ahead (right-hand lane), hold ~60 km/h.
                car.Autopilot = c =>
                {
                    var fwd = -c.GlobalTransform.Basis.Z with { Y = 0 };
                    fwd = fwd.Normalized();
                    var (x, _, z) = World.WorldOrigin.ToWorld(c.GlobalPosition + fwd * 18f);
                    float steer = 0;
                    if (map.NearestRoad(x, z, 25f, 1.8f, out double px, out _, out double pz, out _, Map.RoadClass.Residential))
                    {
                        var to = World.WorldOrigin.ToLocal(px, 0, pz) - c.GlobalPosition;
                        to.Y = 0;
                        float side = fwd.Cross(to.Normalized()).Y; // left positive
                        steer = Mathf.Clamp(side * 2.5f, -1f, 1f);
                    }
                    float target = 17f;
                    return (c.Speed < target ? 0.7f : 0f, c.Speed > target + 3 ? 0.3f : 0f, steer);
                };
                Log.Info("Test", "drive: start");
            }
            _driveTime += delta;
            _maxSpeed = Math.Max(_maxSpeed, car.Speed);
            if (!car.OnRoad) _offRoad += delta;
            if (Math.Abs(_driveTime - 8) < delta) Shot("drive_8s");
            if (_driveTime < 20) return;
            Shot("drive_end");
            var (wx, _, wz) = World.WorldOrigin.ToWorld(car.GlobalPosition);
            Log.Info("Test", $"drive: 20 s, top {_maxSpeed * 3.6:F0} km/h, {_offRoad:F1} s off road, ended at {wx:F0},{wz:F0}, upright {car.GlobalTransform.Basis.Y.Y:F2}");
            _drive = false;
        }
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
