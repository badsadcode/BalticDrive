using System;
using BalticDrive.Map;
using BalticDrive.World;
using Godot;

namespace BalticDrive.Audio;

/// <summary>
/// The world's background (D53): a city rumble where there are buildings and roads, wind that grows in the open and
/// high up, and birds by day in parks, woods, fields and gardens. Each is a loop (or, for birds, short phrases now and
/// then from somewhere around you), its level eased toward what the place and the hour call for.
/// </summary>
public sealed partial class Ambience : Node
{
    private AudioStreamPlayer _city = null!, _wind = null!;
    private double _next, _nextBird;
    private float _cityWant, _windWant, _birdRate;
    private readonly Random _rng = new(37);

    public Ambience() { Name = "Ambience"; }

    public override void _Ready()
    {
        _city = new AudioStreamPlayer { Stream = SoundBank.Get("city", AudioSynth.City), Bus = Buses.Ambience, VolumeDb = -80 };
        _wind = new AudioStreamPlayer { Stream = SoundBank.Get("wind", AudioSynth.Wind), Bus = Buses.Ambience, VolumeDb = -80 };
        AddChild(_city); AddChild(_wind);
        _city.Play(); _wind.Play();
    }

    public override void _Process(double delta)
    {
        var cam = GetViewport().GetCamera3D();
        var main = Main.Instance;
        if (cam == null || main?.World == null) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (now >= _next)
        {
            _next = now + 0.5;
            Judge(cam.GlobalPosition, main);
        }
        float k = 1f - MathF.Exp(-1.2f * (float)delta);
        _city.VolumeDb = Mathf.Lerp(_city.VolumeDb, _cityWant > 0.01f ? Mathf.LinearToDb(_cityWant) : -60, k);
        _wind.VolumeDb = Mathf.Lerp(_wind.VolumeDb, _windWant > 0.01f ? Mathf.LinearToDb(_windWant) : -60, k);
        if (_birdRate > 0 && now >= _nextBird)
        {
            _nextBird = now + (2.0 + _rng.NextDouble() * 6.0) / _birdRate;
            // a bird somewhere 10-40 m away, up in a tree
            float a = (float)(_rng.NextDouble() * Math.Tau), r = 10 + (float)_rng.NextDouble() * 30;
            var at = cam.GlobalPosition + new Vector3(MathF.Cos(a) * r, 4 + (float)_rng.NextDouble() * 6, MathF.Sin(a) * r);
            int kind = _rng.Next(6);
            var bird = SoundBank.Get("bird" + kind, () => AudioSynth.Bird(100 + kind));
            var p = new AudioStreamPlayer3D { Stream = bird, Bus = Buses.Ambience, UnitSize = 6, MaxDistance = 80, VolumeDb = -8, PitchScale = (float)GD.RandRange(0.92, 1.08) };
            AddChild(p);
            p.GlobalPosition = at;
            p.Play();
            p.Finished += p.QueueFree;
        }
    }

    /// <summary>What this place should sound like, from the land cover around the listener and the hour.</summary>
    private void Judge(Vector3 ear, Main main)
    {
        var w = main.World!;
        var (x, _, z) = WorldOrigin.ToWorld(ear);
        int town = 0, green = 0, open = 0, n = 0;
        for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                byte land = w.LandAt(x + i * 60, z + j * 60);
                n++;
                if (land is MapLand.Residential or MapLand.Paved) town++;
                else if (land is MapLand.Park or MapLand.Forest or MapLand.Orchard or MapLand.Cemetery) green++;
                else if (land is MapLand.Field or MapLand.Meadow or MapLand.Open or MapLand.Farmyard) open++;
            }
        float hour = main.Hour;
        bool day = hour > 5 && hour < 20.5f;
        bool dawn = hour > 4.5f && hour < 8;
        float height = ear.Y - (float)w.Height(x, z);
        bool walls = (Acoustics.Instance?.Enclosure ?? 0) > 0.3f;
        _cityWant = (town / (float)n * 0.35f + (walls ? 0.1f : 0)) * (day ? 1f : 0.55f);
        _windWant = 0.04f + open / (float)n * 0.18f + Mathf.Clamp(height / 80f, 0, 0.3f);
        _birdRate = day ? (green / (float)n * 1.5f + (town > 0 ? 0.25f : 0.1f) + open / (float)n * 0.4f) * (dawn ? 2f : 1f) : 0;
    }
}
