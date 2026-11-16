using System;
using BalticDrive.Vehicle;
using Godot;

namespace BalticDrive.Audio;

/// <summary>
/// The car's voice (D53): an engine made live in code, plus tyres and wind.
/// <list type="bullet">
/// <item>Revs come from a pretend five-speed gearbox (the physics has no engine): rpm from speed in the current gear,
/// shifting up at 5,200 and down at 1,900, idling at 800. A four-cylinder fires twice a revolution; the sound is that
/// firing frequency and a few harmonics with a little roughness, louder and brighter with the throttle.</item>
/// <item>Tyres: a hiss loop that grows and rises with speed; wind: grows with the square of speed.</item>
/// </list>
/// The engine runs while you drive and dies away when you get out.
/// </summary>
public sealed partial class EngineSound : Node3D
{
    private readonly Car _car;
    private AudioStreamPlayer3D _engine = null!, _tyres = null!, _wind = null!;
    private AudioStreamGeneratorPlayback? _pb;
    private float _phase, _rpm = 800, _level, _rough, _lp;
    private int _gear = 1;
    private readonly Random _rng = new(5);
    private static readonly float[] RpmPerKmh = { 0, 125f, 72f, 50f, 38f, 30f };
    public float Rpm => _rpm;
    public int Gear => _gear;

    public EngineSound(Car car) { _car = car; Name = "EngineSound"; }

    public override void _Ready()
    {
        _engine = new AudioStreamPlayer3D
        {
            Stream = new AudioStreamGenerator { MixRate = AudioSynth.Rate, BufferLength = 0.12f },
            Bus = Buses.Engine, UnitSize = 10, MaxDistance = 250, VolumeDb = -2,
        };
        AddChild(_engine);
        _engine.Position = new Vector3(0, 0.6f, -1.4f);   // under the bonnet
        _engine.Play();
        _pb = (AudioStreamGeneratorPlayback)_engine.GetStreamPlayback();
        _tyres = new AudioStreamPlayer3D { Stream = SoundBank.Get("tyres", AudioSynth.Tyres), Bus = Buses.Engine, UnitSize = 10, MaxDistance = 150, VolumeDb = -80 };
        _wind = new AudioStreamPlayer3D { Stream = SoundBank.Get("wind", AudioSynth.Wind), Bus = Buses.Engine, UnitSize = 10, MaxDistance = 60, VolumeDb = -80 };
        AddChild(_tyres); AddChild(_wind);
        _tyres.Play(); _wind.Play();
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;
        float kmh = MathF.Abs(_car.Speed) * 3.6f;
        float throttle = _car.Driving ? _car.Throttle : 0f;
        // gearbox
        if (kmh < 4) _gear = 1;
        else
        {
            float r = 800 + kmh * RpmPerKmh[_gear];
            if (r > 5200 && _gear < 5) _gear++;
            else if (r < 1900 && _gear > 1) _gear--;
        }
        float want = _car.Driving ? MathF.Max(800 + throttle * 600 * (kmh < 4 ? 1 : 0), 800 + kmh * RpmPerKmh[_gear]) : 0;
        _rpm = Mathf.Lerp(_rpm, MathF.Min(want, 6200), 1f - MathF.Exp(-6f * dt));
        float wantLevel = _car.Driving ? 0.2f + throttle * 0.7f : 0f;   // a quiet idle, a roar on the throttle
        _level = Mathf.Lerp(_level, wantLevel, 1f - MathF.Exp(-4f * dt));
        Fill();
        float speedK = Mathf.Clamp(kmh / 120f, 0, 1);
        _tyres.VolumeDb = kmh < 2 ? -80 : Mathf.LinearToDb(0.05f + speedK * 0.6f);
        _tyres.PitchScale = 0.7f + speedK * 0.6f;
        _wind.VolumeDb = kmh < 15 ? -80 : Mathf.LinearToDb(speedK * speedK * 0.9f);
    }

    /// <summary>Fills the generator with as much engine as it has room for.</summary>
    private void Fill()
    {
        if (_pb == null) return;
        int frames = _pb.GetFramesAvailable();
        float fire = MathF.Max(1f, _rpm / 60f * 2f);   // four cylinders: two firings per revolution
        float bright = Mathf.Clamp(_level, 0, 1);
        for (int i = 0; i < frames; i++)
        {
            _phase += fire / AudioSynth.Rate;
            if (_phase > 1) { _phase -= 1; _rough = (float)(_rng.NextDouble() - 0.5) * 0.35f; }
            float p = _phase * MathF.Tau;
            // the firing pulse and its harmonics, roughened per firing
            float v = MathF.Sin(p) * 0.8f + MathF.Sin(2 * p) * 0.45f * bright + MathF.Sin(3 * p + 0.5f) * 0.25f * bright
                    + MathF.Sin(0.5f * p) * 0.35f + _rough * MathF.Exp(-_phase * 6f);
            _lp += (v - _lp) * (0.25f + 0.5f * bright);   // more throttle, brighter
            float s = _lp * _level * 0.5f;
            _pb.PushFrame(new Vector2(s, s));
        }
    }
}
