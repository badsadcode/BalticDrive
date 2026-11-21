using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BalticDrive.Core;
using BalticDrive.Map;
using BalticDrive.World;
using BalticDrive.Trees;
using Godot;

namespace BalticDrive.Power;

/// <summary>
/// Firewood from trees (D111, Tom: "chopping down trees"): stand at a tree with an axe and F chops its dead branches
/// for six seconds (a hit or walking off stops it) into two or three armfuls of firewood. The tree stays standing (the
/// trees are instanced from the map: felling them is for another day) and gives wood once; the trees you've used are
/// kept per world in <c>user://trees_used.json</c>.
/// </summary>
public static class Woodland
{
    public const float ChopSeconds = 6f, Reach = 1.6f;
    private const string FilePath = "user://trees_used.json";
    private static HashSet<string>? _used;
    private static string _map = "";
    [ThreadStatic] private static MapContext? _ctx;

    /// <summary>A tree's key: its trunk to the decimetre (trees are on a fixed world grid, so this never changes).</summary>
    public static string Key(TreePlacement.Tree t) => $"{Math.Round(t.X, 1):0.0}_{Math.Round(t.Z, 1):0.0}";

    /// <summary>The nearest tree trunk within reach of a world spot, or null.</summary>
    public static TreePlacement.Tree? Nearest(MapWorld world, double x, double z, float reach = Reach)
    {
        var ctx = _ctx;
        if (ctx == null || ctx.World != world) _ctx = ctx = new MapContext(world);
        double x0 = x - 6, z0 = z - 6;
        ctx.Reset(x0, z0, 0, 0, 12, 12, withPoints: false);
        TreePlacement.Tree? best = null; double bd = reach + 0.3;   // + the trunk's own radius
        foreach (var t in TreePlacement.In(world, x0, z0, 12, ctx))
        {
            double d = Math.Sqrt((t.X - x) * (t.X - x) + (t.Z - z) * (t.Z - z));
            if (d < bd) { bd = d; best = t; }
        }
        return best;
    }

    public static bool Used(string map, string key) { Load(map); return _used!.Contains(key); }

    public static void Use(string map, string key)
    {
        Load(map);
        _used!.Add(key);
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            var all = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path)) ?? new() : new();
            all[map] = _used.ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(all));
        }
        catch (Exception e) { Log.Error("Wood", e.Message); }
    }

    /// <summary>How many armfuls a tree gives: two or three, the same every time.</summary>
    public static int Yield(TreePlacement.Tree t) => 2 + (int)(Hash.Of((int)Math.Floor(t.X * 10), (int)Math.Floor(t.Z * 10), 55) % 2);

    private static void Load(string map)
    {
        if (_used != null && _map == map) return;
        _map = map;
        _used = new();
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            if (!File.Exists(path)) return;
            var all = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path));
            if (all != null && all.TryGetValue(map, out var mine)) _used = new HashSet<string>(mine);
        }
        catch (Exception e) { Log.Error("Wood", e.Message); }
    }

    /// <summary>Self-tests: forget the trees used on a map (the player's file is backed up by the test).</summary>
    public static void Reset() { _used = null; }

    /// <summary>A chop: a dull knock with a woody ring. A placeholder until Assets/Sounds/chop.wav.</summary>
    public static AudioStream Thunk()
    {
        const int rate = Audio.AudioSynth.Rate;
        int n = rate * 35 / 100;
        var x = new float[n];
        var rng = new Random(9);
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)rate;
            float env = MathF.Exp(-t * 28f);
            x[i] = env * (0.6f * MathF.Sin(MathF.Tau * 180f * t) + 0.3f * MathF.Sin(MathF.Tau * 410f * t) + 0.4f * (float)(rng.NextDouble() * 2 - 1) * MathF.Exp(-t * 90f));
        }
        return Audio.AudioSynth.Wav(x);
    }
}
