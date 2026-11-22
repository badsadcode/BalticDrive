using System;
using System.Collections.Generic;
using System.Linq;

namespace BalticDrive.Items;

/// <summary>
/// Bigos, the evolved recipe (D114; Tom's agreed order after skills). A pot of bigos carries its state in its id,
/// <c>bigos:&lt;what's in it&gt;:&lt;servings&gt;:&lt;reheats&gt;</c> (bigos:KMG:6:2: sauerkraut, meat, mushrooms; six
/// servings; reheated twice), so moving, stacking, saving and loot need nothing new: to them it's an item. Its name,
/// food and weight are worked out here from the "bigos" template in items.json. Started from sauerkraut in your
/// saucepan (six servings); each ingredient added and each reheat (up to three: it's best on day three) makes it better;
/// eating takes a serving, and the last one gives the saucepan back.
/// </summary>
public static class Bigos
{
    public const string Prefix = "bigos:";
    public const int MaxReheats = 3;

    /// <summary>What can go in, by letter: K is the base (sauerkraut), the rest make it better.</summary>
    public static readonly (char Letter, string Name)[] Ingredients =
    {
        ('K', "sauerkraut"), ('C', "fresh cabbage"), ('M', "meat"), ('G', "mushrooms"), ('P', "prunes"), ('O', "onion"),
    };

    public readonly record struct Pot(string Has, int Servings, int Reheats)
    {
        /// <summary>0 (just sauerkraut, never reheated) to 8 (everything, reheated three times).</summary>
        public int Quality => Math.Max(0, Has.Length - 1) + Math.Min(Reheats, MaxReheats);
    }

    private static readonly Dictionary<string, ItemDef?> _defs = new();

    public static bool Is(string id) => id.StartsWith(Prefix);

    public static Pot? Parse(string id)
    {
        if (!Is(id)) return null;
        var p = id[Prefix.Length..].Split(':');
        if (p.Length != 3 || !int.TryParse(p[1], out int s) || !int.TryParse(p[2], out int r) || s < 1) return null;
        return new Pot(p[0], s, r);
    }

    /// <summary>A pot's id, its letters in the fixed order (so equal pots stack and compare equal).</summary>
    public static string Id(Pot p)
    {
        var has = new string(Ingredients.Select(i => i.Letter).Where(c => p.Has.Contains(c)).ToArray());
        return $"{Prefix}{has}:{p.Servings}:{Math.Min(p.Reheats, MaxReheats)}";
    }

    public static string QualityName(int q) => q switch
    {
        <= 1 => "thin", <= 3 => "decent", <= 5 => "proper", <= 7 => "very good", _ => "like babcia's",
    };

    /// <summary>The item a pot is: its name tells what's in it, how many servings are left and how often it's been
    /// reheated; a serving's food grows with the quality.</summary>
    public static ItemDef? Def(string id)
    {
        if (_defs.TryGetValue(id, out var cached)) return cached;
        ItemDef? def = null;
        if (Parse(id) is { } p && ItemDb.All.TryGetValue("bigos", out var t))
        {
            var extras = Ingredients.Where(i => i.Letter != 'K' && p.Has.Contains(i.Letter)).Select(i => i.Name).ToList();
            string reheated = p.Reheats switch { 0 => "", 1 => ", reheated once", 2 => ", reheated twice", _ => ", reheated three times" };
            // short for the lists; what's in it and the reheats in the note (the row's tooltip)
            string what = $"{p.Servings} serving{(p.Servings == 1 ? "" : "s")}, {QualityName(p.Quality)}";
            string note = "Sauerkraut" + (extras.Count > 0 ? ", " + string.Join(", ", extras) : "") + (reheated.Length > 0 ? reheated : ", not reheated yet");
            var next = p.Servings > 1 ? Id(p with { Servings = p.Servings - 1 }) : "saucepan";
            def = t with
            {
                Id = id, Name = $"pot of bigos ({what})", Plural = $"pots of bigos ({what})", Note = note,
                Food = t.Food + 4 * p.Quality, Weight = 0.8f + 0.25f * p.Servings, Leaves = next,
            };
        }
        _defs[id] = def;
        return def;
    }

    /// <summary>Can this pot take that (an ingredient letter, or "reheat").</summary>
    public static bool CanTake(Pot p, string adds) => adds == "reheat" ? p.Reheats < MaxReheats : adds.Length == 1 && !p.Has.Contains(adds[0]);

    /// <summary>What's missing for an evolve recipe, or null.</summary>
    public static string? Missing(Inventory pack, string adds)
    {
        var pots = pack.Stacks.Select(s => Parse(s.Id)).Where(p => p != null).Select(p => p!.Value).ToList();
        if (pots.Count == 0) return "a pot of bigos";
        if (pots.Any(p => CanTake(p, adds))) return null;
        return adds == "reheat" ? "a pot of bigos not yet reheated three times" : $"a pot of bigos without {Name(adds)}";
    }

    /// <summary>Changes the first pot that can take it; says what became of it.</summary>
    public static bool Evolve(Inventory pack, string adds, out string said)
    {
        var st = pack.Stacks.FirstOrDefault(s => Parse(s.Id) is { } p && CanTake(p, adds));
        if (st.Id == null || Parse(st.Id) is not { } pot) { said = "No pot of bigos that can take it"; return false; }
        var after = adds == "reheat" ? pot with { Reheats = pot.Reheats + 1 } : pot with { Has = pot.Has + adds };
        pack.Remove(st.Id, 1);
        pack.Add(Id(after), 1);
        said = adds == "reheat"
            ? after.Reheats >= MaxReheats ? "The bigos, reheated a third time: now it's right" : $"The bigos simmers again ({QualityName(after.Quality)})"
            : $"In {(Name(adds).EndsWith("s") ? "go" : "goes")} the {Name(adds)}: " + (QualityName(after.Quality) != QualityName(pot.Quality)
                ? $"the bigos is {QualityName(after.Quality)} now" : $"still {QualityName(after.Quality)}, but better");
        return true;
    }

    private static string Name(string letter) => Ingredients.FirstOrDefault(i => i.Letter.ToString() == letter).Name ?? letter;
}
