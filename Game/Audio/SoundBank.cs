using System;
using System.Collections.Generic;
using Godot;

namespace BalticDrive.Audio;

/// <summary>
/// Every sound by name (D53). Each is generated in code (placeholders: "NoorSounds bleeps and bloops are fine", the
/// brief), unless a recording with the same name sits in <c>res://Assets/Sounds/</c> (.ogg or .wav): then that one
/// plays instead. So real recordings replace the placeholders one at a time without touching code.
/// Names: <c>shot_&lt;gun&gt;</c>, <c>shot_far_&lt;gun&gt;</c>, <c>echo</c>, <c>roll</c>, <c>hit_hard</c>, <c>hit_soft</c>,
/// <c>city</c>, <c>wind</c>, <c>bird</c>, <c>tyres</c>.
/// </summary>
public static class SoundBank
{
    public const string Folder = "res://Assets/Sounds/";
    private static readonly Dictionary<string, AudioStream> Cache = new();

    public static AudioStream Get(string key, Func<AudioStream> make)
    {
        if (Cache.TryGetValue(key, out var s)) return s;
        foreach (var ext in new[] { ".ogg", ".wav" })
            if (ResourceLoader.Exists(Folder + key + ext) && ResourceLoader.Load<AudioStream>(Folder + key + ext) is { } file)
                return Cache[key] = file;
        return Cache[key] = make();
    }
}

/// <summary>The audio buses (D53): sound effects through a reverb that follows the space you're in (<see cref="Acoustics"/>),
/// ambience and the engine on their own.</summary>
public static class Buses
{
    public const string Sfx = "SFX", Ambience = "Ambience", Engine = "Engine";
    public static AudioEffectReverb? Reverb;

    public static void Setup()
    {
        if (AudioServer.GetBusIndex(Sfx) >= 0) return;
        foreach (var name in new[] { Sfx, Ambience, Engine })
        {
            AudioServer.AddBus();
            int i = AudioServer.BusCount - 1;
            AudioServer.SetBusName(i, name);
            AudioServer.SetBusSend(i, "Master");
        }
        Reverb = new AudioEffectReverb { RoomSize = 0.3f, Damping = 0.5f, Wet = 0.08f, Dry = 1f, PredelayMsec = 20, Spread = 1f, Hipass = 0.1f };
        AudioServer.AddBusEffect(AudioServer.GetBusIndex(Sfx), Reverb);
        AudioServer.SetBusVolumeDb(AudioServer.GetBusIndex(Ambience), -6);
        AudioServer.SetBusVolumeDb(AudioServer.GetBusIndex(Engine), -4);
    }
}
