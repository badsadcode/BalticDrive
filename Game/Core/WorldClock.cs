using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Godot;

namespace BalticDrive.Core;

/// <summary>
/// Time played in a world (D115), the clock food ages by. The sky's clock stands still unless you turn it ([ ]), so
/// freshness can't use it; this one runs while the game does, and stops when you quit (Zomboid-style: the world waits
/// for you). One hour played is a "day" for food (<see cref="Day"/>). Kept per world in <c>user://clock.json</c>;
/// self-tests point it at a scratch file.
/// </summary>
public static class WorldClock
{
    /// <summary>Real seconds in a food day.</summary>
    public const double Day = 3600;
    /// <summary>A new world starts on day 312 (D115; the "Day 312" of the lore brainstorm): so food found in a cupboard
    /// can be months old without its "made" going below zero (which means "doesn't go off").</summary>
    public const double StartDay = 312;
    public static string FilePath = "user://clock.json";
    private static double? _seconds;
    private static string _world = "";
    private static double _sinceSave;

    public static double Seconds { get { Load(); return _seconds!.Value; } }
    public static double Days => Seconds / Day;

    /// <summary>The game ran for <paramref name="dt"/> seconds (Main calls this every frame).</summary>
    public static void Tick(double dt)
    {
        Load();
        _seconds += dt;
        _sinceSave += dt;
        if (_sinceSave > 30) Save();
    }

    /// <summary>Self-tests: jump ahead.</summary>
    public static void Skip(double seconds) { Load(); _seconds += seconds; }

    public static void Save()
    {
        if (_seconds == null) return;
        _sinceSave = 0;
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            var all = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path)) ?? new() : new();
            all[_world] = _seconds.Value;
            File.WriteAllText(path, JsonSerializer.Serialize(all));
        }
        catch (Exception e) { Log.Error("Clock", e.Message); }
    }

    private static void Load()
    {
        if (_seconds != null && _world == Main.SaveWorld) return;
        _world = Main.SaveWorld;
        _seconds = StartDay * Day;
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            if (!File.Exists(path)) return;
            var all = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path));
            if (all != null && all.TryGetValue(_world, out var s)) _seconds = Math.Max(s, StartDay * Day);
        }
        catch (Exception e) { Log.Error("Clock", e.Message); }
    }

    /// <summary>Self-tests: forget what's in memory.</summary>
    public static void Reset() => _seconds = null;
}
