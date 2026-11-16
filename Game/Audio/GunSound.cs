using System;
using System.Linq;
using BalticDrive.OnFoot;
using Godot;

namespace BalticDrive.Audio;

/// <summary>
/// A gunshot as it reaches the listener (D53):
/// <list type="bullet">
/// <item>sound takes its time: it arrives d / 343 s after the flash;</item>
/// <item>near (under 70 m) it's the full crack and thump, placed where it was fired; far, the crack is gone and a low
/// boom carries for kilometres, with the air's own muffling (Godot's attenuation filter);</item>
/// <item>off the walls round the listener (<see cref="Acoustics"/>) the shot comes back as echoes, each placed on the
/// wall it bounced off and late by the extra way it travelled; out in the open a long roll follows instead.</item>
/// </list>
/// The layers (shot, far shot, echo, roll) are separate sounds in <see cref="SoundBank"/>, each replaceable by a file.
/// </summary>
public static class GunSound
{
    public const float SpeedOfSound = 343f;

    public static void Play(Node from, Vector3 at, WeaponDef gun, bool own)
    {
        var tree = from.GetTree();
        var cam = from.GetViewport().GetCamera3D();
        var ear = cam?.GlobalPosition ?? at;
        float d = own ? 0 : at.DistanceTo(ear);
        float arrive = d / SpeedOfSound;

        if (d < 70f)
        {
            var near = SoundBank.Get("shot_" + gun.Id, () => AudioSynth.NearShot(gun.Boom, gun.Pitch));
            // your own shot is right at your ear: dry (no reverb) and full strength; it sounded far away through the
            // reverb (Tom)
            if (own) After(tree, 0, () => Play2D(from, near, 0, "Master"));
            else After(tree, arrive, () => Play3D(from, at, near, 4, 400, 22000));
        }
        // the distant boom: always there beyond 40 m, blending in as the crack fades
        if (d > 40f)
        {
            var far = SoundBank.Get("shot_far_" + gun.Id, () => AudioSynth.FarShot(gun.Boom));
            After(tree, arrive, () => Play3D(from, at, far, 10 + gun.Boom * 4, 3000, 2500));
        }

        // echoes off the walls near the listener (the shot reaches the wall and comes back): for shots close enough
        // that their echo matters
        var ac = Acoustics.Instance;
        if (ac != null && d < 150f)
        {
            var echo = SoundBank.Get("echo", AudioSynth.Echo);
            // the walls spread round, the nearest few first, at most five
            foreach (var (wall, dist) in ac.Walls.OrderBy(w => w.Distance).Where((_, i) => i % 3 == 0).Take(5))
            {
                float path = at.DistanceTo(wall) + wall.DistanceTo(ear);
                float delay = path / SpeedOfSound;
                float db = -3f - path * 0.1f + gun.Boom * 3f;
                After(tree, delay, () => Play3D(from, wall, echo, db, 600, 12000));
            }
            // open ground: no walls to answer, the shot rolls away over the land instead
            if (ac.Enclosure < 0.35f && ac.Sky)
            {
                var roll = SoundBank.Get("roll", AudioSynth.Roll);
                After(tree, arrive + 0.25f, () => Play2D(from, roll, -20f + gun.Boom * 4f - d * 0.02f));
            }
        }
    }

    private static void After(SceneTree tree, float seconds, Action a)
    {
        if (seconds <= 0.005f) { a(); return; }
        tree.CreateTimer(seconds).Timeout += a;
    }

    public static void Play2D(Node from, AudioStream s, float db, string bus = Buses.Sfx)
    {
        if (!GodotObject.IsInstanceValid(from)) return;
        var p = new AudioStreamPlayer { Stream = s, VolumeDb = db, Bus = bus, PitchScale = (float)GD.RandRange(0.96, 1.04) };
        from.GetTree().Root.AddChild(p);
        p.Play();
        p.Finished += p.QueueFree;
    }

    public static void Play3D(Node from, Vector3 at, AudioStream s, float db, float maxDistance, float muffleHz)
    {
        if (!GodotObject.IsInstanceValid(from)) return;
        var p = new AudioStreamPlayer3D
        {
            Stream = s, VolumeDb = db, MaxDistance = maxDistance, UnitSize = 8, Bus = Buses.Sfx,
            AttenuationFilterCutoffHz = muffleHz, AttenuationFilterDb = -18,
            PitchScale = (float)GD.RandRange(0.96, 1.04),
        };
        from.GetTree().Root.AddChild(p);
        p.GlobalPosition = at;
        p.Play();
        p.Finished += p.QueueFree;
    }
}
