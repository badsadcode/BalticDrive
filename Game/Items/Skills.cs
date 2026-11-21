using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BalticDrive.Core;
using Godot;

namespace BalticDrive.Items;

/// <summary>
/// Skills (D113), learnt by doing: cooking, first aid, woodcraft, mechanics, shooting. Levels 0-5 at the XP in
/// <c>GameData/skills.json</c>; each level is worth its skill's <c>per_level</c> fraction of something (faster, more,
/// steadier: the code that uses a skill knows what). Kept per world in <c>user://skills.json</c>; dying doesn't take
/// them.
/// </summary>
public static class Skills
{
    public const string DataPath = "res://GameData/skills.json";
    /// <summary>Where XP is kept: user://skills.json; self-tests point it at a scratch file so they never touch yours.</summary>
    public static string FilePath = "user://skills.json";
    public const int MaxLevel = 5;

    public sealed record Def(string Id, string Name, float PerLevel, string Raises, string Effect);

    private static Dictionary<string, Def>? _defs;
    private static int[] _levels = { 50, 120, 220, 360, 550 };
    private static Dictionary<string, float>? _xp;
    private static string _world = "";

    /// <summary>A skill went up a level: (skill, new level).</summary>
    public static event Action<Def, int>? LevelUp;

    public static IReadOnlyDictionary<string, Def> All { get { LoadDefs(); return _defs!; } }

    public static float Xp(string id) { LoadXp(); return _xp!.TryGetValue(id, out var x) ? x : 0; }

    public static int Level(string? id)
    {
        if (id == null) return 0;
        float x = Xp(id);
        int l = 0;
        while (l < _levels.Length && x >= _levels[l]) l++;
        return Math.Min(l, MaxLevel);
    }

    /// <summary>The skill's fraction per level × its level: 0.3 for cooking at level 3.</summary>
    public static float Bonus(string? id) => id != null && All.TryGetValue(id, out var d) ? d.PerLevel * Level(id) : 0f;

    /// <summary>× on the time something takes: 1 at level 0, 0.7 at cooking 3.</summary>
    public static float TimeFactor(string? id) => 1f - Bonus(id);

    /// <summary>XP to the next level, or null at the top.</summary>
    public static float? Next(string id) { int l = Level(id); return l >= MaxLevel || l >= _levels.Length ? null : _levels[l]; }

    /// <summary>Practice: XP added, a level-up announced (and returned).</summary>
    public static int? Add(string? id, float xp)
    {
        if (id == null || xp <= 0 || !All.ContainsKey(id)) return null;
        LoadXp();
        int before = Level(id);
        _xp![id] = Xp(id) + xp;
        int after = Level(id);
        Save();
        if (after <= before) return null;
        Log.Info("Skills", $"{id}: level {after} ({Xp(id):0} XP)");
        LevelUp?.Invoke(All[id], after);
        return after;
    }

    /// <summary>"cooking 30% faster".</summary>
    public static string Says(Def d, int level) => d.Effect.Replace("{p}", $"{d.PerLevel * level * 100:0}%");

    /// <summary>"Cooking 2 · First aid 0 · ...": for the inventory window.</summary>
    public static string Line() => string.Join("  ·  ", All.Values.Select(d => $"{d.Name} {Level(d.Id)}"));

    private static void LoadDefs()
    {
        if (_defs != null) return;
        _defs = new();
        try
        {
            using var doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(DataPath));
            if (doc.RootElement.TryGetProperty("levels", out var lv)) _levels = lv.EnumerateArray().Select(e => e.GetInt32()).ToArray();
            foreach (var s in doc.RootElement.GetProperty("skills").EnumerateObject())
                _defs[s.Name] = new Def(s.Name, s.Value.GetProperty("name").GetString()!, s.Value.GetProperty("per_level").GetSingle(),
                    s.Value.GetProperty("raises").GetString()!, s.Value.GetProperty("effect").GetString()!);
            Log.Info("Skills", $"{_defs.Count} skills");
        }
        catch (Exception e) { Log.Error("Skills", $"{DataPath}: {e.Message}"); }
    }

    private static void LoadXp()
    {
        if (_xp != null && _world == Main.SaveWorld) return;
        _world = Main.SaveWorld;
        _xp = new();
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            if (!File.Exists(path)) return;
            var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, float>>>(File.ReadAllText(path));
            if (all != null && all.TryGetValue(_world, out var mine)) _xp = mine;
        }
        catch (Exception e) { Log.Error("Skills", $"{FilePath}: {e.Message}"); }
    }

    private static void Save()
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            var all = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, float>>>(File.ReadAllText(path)) ?? new() : new();
            all[_world] = _xp!;
            File.WriteAllText(path, JsonSerializer.Serialize(all));
        }
        catch (Exception e) { Log.Error("Skills", $"{FilePath}: {e.Message}"); }
    }

    /// <summary>Self-tests: forget what's in memory (the file is the test's to back up and restore).</summary>
    public static void Reset() => _xp = null;
}
