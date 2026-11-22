using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BalticDrive.Core;
using Godot;

namespace BalticDrive.Items;

/// <summary>
/// Evolved dishes (D114, made data in D116; bigos first): a pot that grows with what you add and gets better each time
/// it's reheated. Everything about a dish is data: its template item in items.json has an <c>evolved</c> block (its
/// ingredients by letter, the base one, servings, reheats, food per quality step, the quality names, what the empty pot
/// becomes), and its recipes in recipes.json say <c>"evolve": "&lt;template id&gt;"</c> and what they <c>adds</c>. A pot
/// carries its state in its id, <c>&lt;template&gt;:&lt;letters in it&gt;:&lt;servings&gt;:&lt;reheats&gt;</c>
/// (bigos:KMGPO:6:3), so moving, stacking, saving and loot need nothing new. A new dish (a żurek, a krupnik) is a
/// template and some recipes: no code.
/// </summary>
public static class Evolved
{
    public sealed record Dish(string Id, (char Letter, string Name)[] Ingredients, char Base, int Servings, int MaxReheats,
        float FoodPerQuality, string[] QualityNames, string EmptiesTo);

    private static Dictionary<string, Dish>? _dishes;
    private static readonly Dictionary<string, ItemDef?> _defs = new();

    public readonly record struct Pot(string Dish, string Has, int Servings, int Reheats);

    /// <summary>The dishes, from the templates' "evolved" blocks in items.json.</summary>
    public static IReadOnlyDictionary<string, Dish> Dishes => _dishes ??= Load();

    /// <summary>The language changed (D119): names are read again.</summary>
    public static void Reload() { _dishes = null; _defs.Clear(); }

    private static Dictionary<string, Dish> Load()
    {
        var res = new Dictionary<string, Dish>();
        try
        {
            using var doc = JsonDocument.Parse(Godot.FileAccess.GetFileAsString(ItemDb.ItemsPath));
            foreach (var e in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                if (!e.TryGetProperty("evolved", out var ev)) continue;
                string id = e.GetProperty("id").GetString()!;
                var ing = (Core.Lang.Pl && ev.TryGetProperty("ingredients_pl", out var ipl) ? ipl : ev.GetProperty("ingredients")).EnumerateObject().Select(p => (p.Name[0], p.Value.GetString()!)).ToArray();
                res[id] = new Dish(id, ing, ev.GetProperty("base").GetString()![0], ev.GetProperty("servings").GetInt32(),
                    ev.GetProperty("max_reheats").GetInt32(), ev.GetProperty("food_per_quality").GetSingle(),
                    (Core.Lang.Pl && ev.TryGetProperty("quality_names_pl", out var qpl) ? qpl : ev.GetProperty("quality_names")).EnumerateArray().Select(q => q.GetString()!).ToArray(), ev.GetProperty("empties_to").GetString()!);
            }
            Log.Info("Items", $"{res.Count} evolved dish(es): {string.Join(", ", res.Keys)}");
        }
        catch (Exception ex) { Log.Error("Items", $"evolved dishes: {ex.Message}"); }
        return res;
    }

    public static bool Is(string id) => id.IndexOf(':') is var i && i > 0 && Dishes.ContainsKey(id[..i]);

    public static Pot? Parse(string id)
    {
        if (!Is(id)) return null;
        var p = id.Split(':');
        if (p.Length != 4 || !int.TryParse(p[2], out int s) || !int.TryParse(p[3], out int r) || s < 1) return null;
        return new Pot(p[0], p[1], s, r);
    }

    /// <summary>A pot's id, its letters in the dish's order (so equal pots stack and compare equal).</summary>
    public static string Id(Pot p)
    {
        var d = Dishes[p.Dish];
        var has = new string(d.Ingredients.Select(i => i.Letter).Where(c => p.Has.Contains(c)).ToArray());
        return $"{p.Dish}:{has}:{p.Servings}:{Math.Min(p.Reheats, d.MaxReheats)}";
    }

    /// <summary>The first pot a recipe starts: the base, all servings, never reheated.</summary>
    public static string Start(string dish) => Id(new Pot(dish, Dishes[dish].Base.ToString(), Dishes[dish].Servings, 0));

    /// <summary>0 (just the base, never reheated) up to every extra plus every reheat.</summary>
    public static int Quality(Pot p) => Math.Max(0, p.Has.Length - 1) + Math.Min(p.Reheats, Dishes[p.Dish].MaxReheats);

    public static string QualityName(Pot p)
    {
        var names = Dishes[p.Dish].QualityNames;
        return names[Math.Clamp(Quality(p), 0, names.Length - 1)];
    }

    /// <summary>The item a pot is, from its template: the name says servings and quality, the note what's in it and the
    /// reheats; a serving's food grows with the quality.</summary>
    public static ItemDef? Def(string id)
    {
        if (_defs.TryGetValue(id, out var cached)) return cached;
        ItemDef? def = null;
        if (Parse(id) is { } p && ItemDb.All.TryGetValue(p.Dish, out var t))
        {
            var dish = Dishes[p.Dish];
            var extras = dish.Ingredients.Where(i => i.Letter != dish.Base && p.Has.Contains(i.Letter)).Select(i => i.Name).ToList();
            string baseName = dish.Ingredients.First(i => i.Letter == dish.Base).Name;
            string reheated = p.Reheats switch { 0 => Core.Lang.T("evo.reheated0"), 1 => Core.Lang.T("evo.reheated1"), 2 => Core.Lang.T("evo.reheated2"), var n => Core.Lang.T("evo.reheatedN", n) };
            string servings = Core.Lang.T(Core.Lang.Plural(p.Servings, "evo.servings", "evo.servings_few", "evo.servings_many"), p.Servings);
            string what = $"{servings}, {QualityName(p)}";
            var next = p.Servings > 1 ? Id(p with { Servings = p.Servings - 1 }) : dish.EmptiesTo;
            def = t with
            {
                Id = id, Name = $"{t.Name} ({what})", Plural = $"{t.Plural} ({what})",
                Note = char.ToUpper(baseName[0]) + baseName[1..] + (extras.Count > 0 ? ", " + string.Join(", ", extras) : "") + reheated,
                Food = t.Food + dish.FoodPerQuality * Quality(p), Weight = t.Weight - 0.25f * dish.Servings + 0.25f * p.Servings, Leaves = next,
            };
        }
        _defs[id] = def;
        return def;
    }

    /// <summary>Can this pot take that (an ingredient letter, or "reheat").</summary>
    public static bool CanTake(Pot p, string adds) => adds == "reheat" ? p.Reheats < Dishes[p.Dish].MaxReheats : adds.Length == 1 && !p.Has.Contains(adds[0]);

    private static string Name(string dish, string letter) => Dishes[dish].Ingredients.FirstOrDefault(i => i.Letter.ToString() == letter).Name ?? letter;

    /// <summary>What's missing for an evolve recipe, or null.</summary>
    public static string? Missing(Inventory pack, string dish, string adds)
    {
        var pots = pack.Stacks.Select(s => Parse(s.Id)).Where(p => p != null && p.Value.Dish == dish).Select(p => p!.Value).ToList();
        string pot = ItemDb.Get(dish)?.Name ?? dish;
        if (pots.Count == 0) return Core.Lang.T("evo.no_pot", pot);
        if (pots.Any(p => CanTake(p, adds))) return null;
        return adds == "reheat" ? Core.Lang.T("evo.not_reheated", pot, Dishes[dish].MaxReheats) : Core.Lang.T("evo.without", pot, Name(dish, adds));
    }

    /// <summary>Changes the first pot that can take it; says what became of it. A reheat makes it fresh again (D115).</summary>
    public static bool Evolve(Inventory pack, string dish, string adds, out string said)
    {
        var st = pack.Stacks.FirstOrDefault(s => Parse(s.Id) is { } p && p.Dish == dish && CanTake(p, adds));
        string pot = ItemDb.Get(dish)?.Name ?? dish, food = Core.Lang.Pl ? char.ToUpper(dish[0]) + dish[1..] : pot.Split(' ').Last();
        if (st.Id == null || Parse(st.Id) is not { } before) { said = Core.Lang.T("evo.none", pot); return false; }
        var after = adds == "reheat" ? before with { Reheats = before.Reheats + 1 } : before with { Has = before.Has + adds };
        pack.Remove(st.Id, 1);
        pack.Add(Id(after), 1, adds == "reheat" ? double.NaN : st.Made);
        string q = QualityName(after);
        if (adds == "reheat")
            said = after.Reheats >= Dishes[dish].MaxReheats ? Core.Lang.T("evo.last_reheat", food) : Core.Lang.T("evo.reheat", food, q);
        else
        {
            string n = Name(dish, adds);
            said = Core.Lang.T("evo.add", Core.Lang.T(n.EndsWith("s") ? "evo.go" : "evo.goes"), n) + (q != QualityName(before) ? Core.Lang.T("evo.now", food, q) : Core.Lang.T("evo.still", q));
        }
        return true;
    }
}
