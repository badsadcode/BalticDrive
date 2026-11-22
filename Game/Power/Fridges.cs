using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BalticDrive.Core;
using BalticDrive.Items;
using Godot;

namespace BalticDrive.Power;

/// <summary>
/// Fridges that work again (D127, Tom: "fridges first"). The grid is dead (D108), so a fridge is just a cupboard,
/// unless a generator runs within <see cref="Reach"/> m of it (an extension lead out of the window). While one runs, the
/// food inside ages three times slower (<see cref="Keeps"/>). Worked out when you open the fridge: the time since you
/// last closed it that a generator near it was running (each generator remembers when it was started and when it
/// stopped, even while you were far away, D127) is the cold time, and the perishable food inside is made younger by
/// two thirds of it. When each fridge was last closed is kept per world in <c>user://fridges.json</c>.
/// </summary>
public static class Fridges
{
    public const double Keeps = 3;
    public const float Reach = 40f;
    private const string FilePath = "user://fridges.json";
    private static Dictionary<string, double>? _seen;
    private static string _map = "";
    /// <summary>Self-tests: nothing is written.</summary>
    public static bool Frozen;

    /// <summary>A fridge opened: the food inside kept cold for the time a generator ran near it since it was last shut.
    /// Returns those seconds of cold.</summary>
    public static double Chill(string map, string id, Inventory inv, IEnumerable<(double From, double To)> runs)
    {
        Load(map);
        double now = WorldClock.Seconds;
        double from = _seen!.TryGetValue(id, out var t) ? t : now;
        // the time any of them ran, overlaps counted once
        double cold = 0, end = double.MinValue;
        foreach (var (a, b) in runs.Select(r => (Math.Max(r.From, from), Math.Min(r.To, now))).Where(s => s.Item2 > s.Item1).OrderBy(s => s.Item1))
        {
            double a2 = Math.Max(a, end);
            if (b > a2) cold += b - a2;
            end = Math.Max(end, b);
        }
        double younger = cold * (1 - 1 / Keeps);
        if (younger > 0)
            for (int i = 0; i < inv.Stacks.Count; i++)
                if (inv.Stacks[i] is { Made: >= 0 } st && Freshness.Perishable(ItemDb.Get(st.Id)))
                    inv.Stacks[i] = st with { Made = Math.Min(now, st.Made + younger) };
        if (cold > 0) Log.Info("Fridges", $"{id}: {cold / 60:0} min cold since it was shut; the food is {younger / 60:0} min younger");
        Shut(map, id);
        return cold;
    }

    /// <summary>The fridge closed: from now on, cold counts from here.</summary>
    public static void Shut(string map, string id)
    {
        Load(map);
        _seen![id] = WorldClock.Seconds;
        Save();
    }

    private static void Load(string map)
    {
        if (_seen != null && _map == map) return;
        _map = map;
        _seen = new();
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            if (!File.Exists(path)) return;
            var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(File.ReadAllText(path));
            if (all != null && all.TryGetValue(map, out var mine)) _seen = mine;
        }
        catch (Exception ex) { Log.Error("Fridges", ex.Message); }
    }

    private static void Save()
    {
        if (Frozen || _seen == null) return;
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            var all = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double>>>(File.ReadAllText(path)) ?? new() : new();
            all[_map] = _seen;
            File.WriteAllText(path, JsonSerializer.Serialize(all));
        }
        catch (Exception ex) { Log.Error("Fridges", ex.Message); }
    }
}
