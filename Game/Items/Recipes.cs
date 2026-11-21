using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using BalticDrive.Core;

namespace BalticDrive.Items;

/// <summary>One crafting recipe (D106): items used up, tools kept (each a list of alternatives), items made, and how
/// many seconds it takes (D107). Inputs and tools are item ids or "tag:x" (any item with tag x).</summary>
public sealed record Recipe(string Id, string Name, string Category, (string Item, int Count)[] Inputs,
    string[][] Tools, (string Item, int Count)[] Outputs, string About, float Seconds);

/// <summary>
/// Crafting (D106, D107), Project Zomboid style and our own data: <c>GameData/recipes.json</c>. A recipe can be made when the
/// pack holds its inputs and one of each tool's alternatives; making it takes the inputs, keeps the tools and adds the
/// outputs, if they fit the pack's weight.
/// </summary>
public static class Recipes
{
    public const string DataPath = "res://GameData/recipes.json";
    private static List<Recipe>? _all;

    public static IReadOnlyList<Recipe> All => _all ??= Load();

    private static List<Recipe> Load()
    {
        var list = new List<Recipe>();
        try
        {
            using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(DataPath));
            foreach (var r in doc.RootElement.GetProperty("recipes").EnumerateArray())
            {
                (string, int)[] Pairs(string key) => r.GetProperty(key).EnumerateArray().Select(p => (p[0].GetString()!, p[1].GetInt32())).ToArray();
                var tools = r.GetProperty("tools").EnumerateArray().Select(t => t.EnumerateArray().Select(x => x.GetString()!).ToArray()).ToArray();
                var rec = new Recipe(r.GetProperty("id").GetString()!, r.GetProperty("name").GetString()!, r.GetProperty("category").GetString()!,
                    Pairs("inputs"), tools, Pairs("outputs"), r.TryGetProperty("about", out var a) ? a.GetString() ?? "" : "",
                    r.TryGetProperty("seconds", out var sec) ? sec.GetSingle() : 0f);
                foreach (var id in rec.Inputs.Select(p => p.Item).Concat(tools.SelectMany(t => t)))
                    if (!ItemDb.Known(id)) Log.Error("Recipes", $"{rec.Id}: no item is '{id}'");
                foreach (var (id, _) in rec.Outputs)
                    if (ItemDb.Get(id) == null) Log.Error("Recipes", $"{rec.Id}: unknown item '{id}'");
                list.Add(rec);
            }
            Log.Info("Recipes", $"{list.Count} recipes");
        }
        catch (Exception e) { Log.Error("Recipes", e.Message); }
        return list;
    }

    /// <summary>What's missing to make it with this pack (inputs short, tools absent), or an empty list.</summary>
    public static List<string> Missing(Recipe r, Inventory pack)
    {
        var miss = new List<string>();
        foreach (var (item, n) in r.Inputs)
            if (pack.CountMatching(item) < n) miss.Add(n > 1 ? $"{n} × {ItemDb.Say(item)}" : ItemDb.Say(item));
        foreach (var alts in r.Tools)
            if (!alts.Any(a => pack.CountMatching(a) > 0)) miss.Add(string.Join(" or ", alts.Select(ItemDb.Say)));
        return miss;
    }

    /// <summary>Makes it: false with the reason when it can't (missing things, or too heavy to carry what it makes).</summary>
    public static bool Make(Recipe r, Inventory pack, out string said)
    {
        var miss = Missing(r, pack);
        if (miss.Count > 0) { said = "You need " + string.Join(", ", miss); return false; }
        // the weight after: what's used up goes, what's made comes
        // (for a tag, the first matching item in the pack is what goes, less what it leaves behind)
        float InW(string key)
        {
            var d = pack.Stacks.Select(s => ItemDb.Get(s.Id)).FirstOrDefault(x => x != null && x.Matches(key));
            return d == null ? 0 : d.Weight - (d.Leaves != null ? ItemDb.Get(d.Leaves)?.Weight ?? 0 : 0);
        }
        float after = pack.Weight - r.Inputs.Sum(p => InW(p.Item) * p.Count) + r.Outputs.Sum(p => (ItemDb.Get(p.Item)?.Weight ?? 0) * p.Count);
        if (after > pack.MaxWeight + 1e-3f) { said = "Too heavy to carry what it makes"; return false; }
        foreach (var (item, n) in r.Inputs) pack.UseUp(item, n);
        foreach (var (item, n) in r.Outputs) pack.Add(item, n);
        string What((string Item, int Count) o)
        {
            var d = ItemDb.Get(o.Item);
            if (o.Count > 1) return Inventory.Say(new[] { new ItemStack(o.Item, o.Count) });
            string n = d?.Name ?? o.Item;
            return ("aeiou".Contains(char.ToLowerInvariant(n[0])) ? "an " : "a ") + n;
        }
        said = "You make " + string.Join(" and ", r.Outputs.Select(What));
        Log.Info("Recipes", $"{r.Id}: {said}");
        return true;
    }
}
