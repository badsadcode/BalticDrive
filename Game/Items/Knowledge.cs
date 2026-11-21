using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BalticDrive.Core;
using Godot;

namespace BalticDrive.Items;

/// <summary>
/// What you know how to make (D112): every recipe without a book, plus those of the books you've read. Knowledge is
/// yours for good: it isn't in the pack, so dying doesn't lose it and the book can be passed on or burnt. Kept per world
/// in <c>user://knowledge.json</c>.
/// </summary>
public static class Knowledge
{
    private const string FilePath = "user://knowledge.json";
    private static HashSet<string>? _learnt;
    private static string _world = "";

    private static HashSet<string> Learnt { get { Load(); return _learnt!; } }

    public static bool Known(Recipe r) => r.Book == null || Learnt.Contains(r.Id);

    /// <summary>Has every recipe of this book been learnt already.</summary>
    public static bool Read(ItemDef book) => book.Teaches.All(id => Learnt.Contains(id));

    /// <summary>Reads a book: the recipes it teaches that you didn't know yet.</summary>
    public static List<Recipe> Learn(ItemDef book)
    {
        var fresh = Recipes.All.Where(r => book.Teaches.Contains(r.Id) && !Learnt.Contains(r.Id)).ToList();
        foreach (var r in fresh) Learnt.Add(r.Id);
        if (fresh.Count > 0) Save();
        Log.Info("Recipes", $"read {book.Id}: learnt {string.Join(", ", fresh.Select(r => r.Id))}");
        return fresh;
    }

    private static void Load()
    {
        if (_learnt != null && _world == Main.SaveWorld) return;
        _world = Main.SaveWorld;
        _learnt = new();
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            if (!File.Exists(path)) return;
            var all = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path));
            if (all != null && all.TryGetValue(_world, out var mine)) _learnt = new HashSet<string>(mine);
        }
        catch (Exception e) { Log.Error("Recipes", $"{FilePath}: {e.Message}"); }
    }

    private static void Save()
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            var all = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(path)) ?? new() : new();
            all[_world] = _learnt!.OrderBy(x => x).ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(all));
        }
        catch (Exception e) { Log.Error("Recipes", $"{FilePath}: {e.Message}"); }
    }

    /// <summary>Self-tests: forget what's in memory (the file is the test's to back up and restore).</summary>
    public static void Reset() => _learnt = null;
}
