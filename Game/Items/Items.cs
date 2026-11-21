using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using BalticDrive.Core;

namespace BalticDrive.Items;

/// <summary>One kind of item (D89), from <c>GameData/items.json</c>.</summary>
public sealed record ItemDef(string Id, string Name, string Plural, string Category, float Weight, int Stack,
    float Food, float Drink, float Heal, string[] Tags, string? Leaves)
{
    public string Count(int n) => n == 1 ? Name : $"{n} {Plural}";
    /// <summary>Does it answer to a recipe's need (D107): its own id, or "tag:x" when it has tag x.</summary>
    public bool Matches(string key) => key.StartsWith("tag:") ? Array.IndexOf(Tags, key[4..]) >= 0 : key == Id;
}

/// <summary>A pile of one item.</summary>
public readonly record struct ItemStack(string Id, int Count);

/// <summary>
/// Everything you can carry (D89, Tom: "the items data-driven game with crafting recipes"): the definitions are data,
/// read once at start; code refers to items by id only. A missing or broken file leaves the game without items, said
/// in the log, rather than failing.
/// </summary>
public static class ItemDb
{
    public const string ItemsPath = "res://GameData/items.json", LootPath = "res://GameData/loot.json";
    private static Dictionary<string, ItemDef>? _defs;
    private static readonly Dictionary<string, string> _tagNames = new();

    public static IReadOnlyDictionary<string, ItemDef> All => _defs ??= Load();

    public static ItemDef? Get(string id) => All.TryGetValue(id, out var d) ? d : null;
    public static ItemDef Def(string id) => Get(id) ?? throw new KeyError($"no item '{id}' in {ItemsPath}");

    /// <summary>How a recipe's need reads (D107): an item's name, or a tag's ("something to hold water").</summary>
    public static string Say(string key)
    {
        _ = All;
        if (!key.StartsWith("tag:")) return Get(key)?.Name ?? key;
        return _tagNames.TryGetValue(key[4..], out var n) ? n : key[4..].Replace('_', ' ');
    }

    /// <summary>Is there at least one item that answers to this id or tag.</summary>
    public static bool Known(string key) => key.StartsWith("tag:") ? All.Values.Any(d => d.Matches(key)) : Get(key) != null;

    private sealed class KeyError(string m) : Exception(m);

    private static Dictionary<string, ItemDef> Load()
    {
        var d = new Dictionary<string, ItemDef>();
        try
        {
            using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(ItemsPath));
            foreach (var e in doc.RootElement.GetProperty("items").EnumerateArray())
            {
                string id = e.GetProperty("id").GetString()!, name = e.GetProperty("name").GetString()!;
                float F(string k) => e.TryGetProperty(k, out var v) ? v.GetSingle() : 0f;
                d[id] = new ItemDef(id, name, e.TryGetProperty("plural", out var pl) ? pl.GetString()! : name + "s",
                    e.TryGetProperty("category", out var c) ? c.GetString()! : "junk", F("weight"),
                    e.TryGetProperty("stack", out var st) ? st.GetInt32() : 1, F("food"), F("drink"), F("heal"),
                    e.TryGetProperty("tags", out var tg) ? tg.EnumerateArray().Select(t => t.GetString()!).ToArray() : Array.Empty<string>(),
                    e.TryGetProperty("leaves", out var lv) ? lv.GetString() : null);
            }
            if (doc.RootElement.TryGetProperty("tags", out var tags))
                foreach (var t in tags.EnumerateObject()) _tagNames[t.Name] = t.Value.GetString()!;
            foreach (var x in d.Values) if (x.Leaves != null && !d.ContainsKey(x.Leaves)) Log.Error("Items", $"{x.Id} leaves an unknown item '{x.Leaves}'");
            Log.Info("Items", $"{d.Count} items, {_tagNames.Count} tags");
        }
        catch (Exception ex) { Log.Error("Items", $"{ItemsPath}: {ex.Message}"); }
        return d;
    }
}

/// <summary>
/// A list of items with a weight limit (D89): the player's pack, a wardrobe, a fridge. Stacks merge by id; a stack
/// holds at most its item's <see cref="ItemDef.Stack"/> (more go into a new stack).
/// </summary>
public sealed class Inventory
{
    public readonly List<ItemStack> Stacks = new();
    /// <summary>Kilograms it can hold (float.MaxValue: no limit).</summary>
    public float MaxWeight;
    public event Action? Changed;

    public Inventory(float maxWeight = float.MaxValue) { MaxWeight = maxWeight; }

    public float Weight => Stacks.Sum(s => (ItemDb.Get(s.Id)?.Weight ?? 0) * s.Count);
    public int Count(string id) => Stacks.Where(s => s.Id == id).Sum(s => s.Count);
    public int CountWhere(Func<ItemDef, bool> f) => Stacks.Where(s => ItemDb.Get(s.Id) is { } d && f(d)).Sum(s => s.Count);
    public bool IsEmpty => Stacks.Count == 0;
    /// <summary>How many answer to an id or "tag:x" (D107).</summary>
    public int CountMatching(string key) => CountWhere(d => d.Matches(key));

    /// <summary>Uses up <paramref name="n"/> items answering to an id or tag (D107), in pack order; what they leave
    /// behind (an empty bottle) goes back in. Returns the ids used.</summary>
    public List<string> UseUp(string key, int n, bool leftovers = true)
    {
        var used = new List<string>();
        while (n > 0)
        {
            int i = Stacks.FindIndex(s => ItemDb.Get(s.Id) is { } d && d.Matches(key));
            if (i < 0) break;
            var st = Stacks[i];
            int k = Math.Min(n, st.Count);
            Remove(st.Id, k);
            n -= k;
            for (int j = 0; j < k; j++) used.Add(st.Id);
            if (leftovers && ItemDb.Get(st.Id)?.Leaves is { } left) Add(left, k);
        }
        return used;
    }

    /// <summary>How many of an item still fit by weight.</summary>
    public int Room(string id)
    {
        var d = ItemDb.Get(id);
        if (d == null) return 0;
        if (MaxWeight == float.MaxValue || d.Weight <= 0) return int.MaxValue;
        return Math.Max(0, (int)MathF.Floor((MaxWeight - Weight) / d.Weight + 1e-4f));
    }

    /// <summary>Adds up to <paramref name="n"/>; returns how many went in.</summary>
    public int Add(string id, int n)
    {
        var d = ItemDb.Get(id);
        if (d == null || n <= 0) return 0;
        int take = Math.Min(n, Room(id)), left = take;
        for (int i = 0; i < Stacks.Count && left > 0; i++)
        {
            if (Stacks[i].Id != id || Stacks[i].Count >= d.Stack) continue;
            int add = Math.Min(left, d.Stack - Stacks[i].Count);
            Stacks[i] = Stacks[i] with { Count = Stacks[i].Count + add };
            left -= add;
        }
        while (left > 0) { int add = Math.Min(left, Math.Max(1, d.Stack)); Stacks.Add(new ItemStack(id, add)); left -= add; }
        if (take > 0) Changed?.Invoke();
        return take;
    }

    /// <summary>Takes up to <paramref name="n"/> away; returns how many it had.</summary>
    public int Remove(string id, int n)
    {
        int left = n;
        for (int i = Stacks.Count - 1; i >= 0 && left > 0; i--)
        {
            if (Stacks[i].Id != id) continue;
            int take = Math.Min(left, Stacks[i].Count);
            left -= take;
            if (take == Stacks[i].Count) Stacks.RemoveAt(i); else Stacks[i] = Stacks[i] with { Count = Stacks[i].Count - take };
        }
        if (left < n) Changed?.Invoke();
        return n - left;
    }

    /// <summary>Moves up to <paramref name="n"/> of an item into another inventory (as many as fit there).</summary>
    public int MoveTo(Inventory to, string id, int n)
    {
        int moved = to.Add(id, Math.Min(n, Count(id)));
        Remove(id, moved);
        return moved;
    }

    /// <summary>Everything that fits; returns what moved.</summary>
    public List<ItemStack> MoveAllTo(Inventory to)
    {
        var moved = new List<ItemStack>();
        foreach (var id in Stacks.Select(s => s.Id).Distinct().ToList())
        {
            int m = MoveTo(to, id, Count(id));
            if (m > 0) moved.Add(new ItemStack(id, m));
        }
        return moved;
    }

    /// <summary>"3 tins of beans, a bandage": for notes.</summary>
    public static string Say(IEnumerable<ItemStack> stacks)
    {
        var parts = stacks.GroupBy(s => s.Id).Select(g => ItemDb.Get(g.Key)?.Count(g.Sum(s => s.Count)) ?? g.Key).ToList();
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }
}

/// <summary>
/// What a container holds when first opened (D89), from <c>GameData/loot.json</c>: rolled from the container's id, so
/// the same wardrobe always held the same things; nudged by the room it stands in.
/// </summary>
public static class LootTables
{
    private sealed record Entry(string Item, float Weight, int Min, int Max);
    private sealed record Table(int Min, int Max, List<Entry> Items);
    private static Dictionary<string, Table>? _tables;
    private static Dictionary<string, Dictionary<string, float>>? _rooms;

    private static void Load()
    {
        _tables = new(); _rooms = new();
        try
        {
            using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(ItemDb.LootPath));
            foreach (var c in doc.RootElement.GetProperty("containers").EnumerateObject())
            {
                var draws = c.Value.GetProperty("draws");
                var items = c.Value.GetProperty("items").EnumerateArray().Select(e =>
                {
                    int lo = 1, hi = 1;
                    if (e.TryGetProperty("count", out var n)) { lo = n[0].GetInt32(); hi = n[1].GetInt32(); }
                    return new Entry(e.GetProperty("item").GetString()!, e.GetProperty("weight").GetSingle(), lo, hi);
                }).ToList();
                foreach (var e in items.Where(e => e.Item != "empty" && ItemDb.Get(e.Item) == null))
                    Log.Error("Items", $"{ItemDb.LootPath}: '{c.Name}' names an unknown item '{e.Item}'");
                _tables[c.Name] = new Table(draws[0].GetInt32(), draws[1].GetInt32(), items);
            }
            if (doc.RootElement.TryGetProperty("rooms", out var rooms))
                foreach (var r in rooms.EnumerateObject())
                    _rooms[r.Name] = r.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetSingle());
        }
        catch (Exception ex) { Log.Error("Items", $"{ItemDb.LootPath}: {ex.Message}"); }
    }

    public static bool Has(string container) { if (_tables == null) Load(); return _tables!.ContainsKey(container); }

    /// <summary>The contents of a container of a kind in a room, from its id.</summary>
    public static List<ItemStack> Roll(string container, string? room, string id)
    {
        if (_tables == null) Load();
        var res = new List<ItemStack>();
        if (!_tables!.TryGetValue(container, out var t) || t.Items.Count == 0) return res;
        var bias = room != null && _rooms!.TryGetValue(room, out var b) ? b : null;
        var rng = new Random((int)(BadBuilding.Generation.Rng.Fnv1a(id) & 0x7fffffff));
        float W(Entry e) => e.Weight * (bias != null && bias.TryGetValue(e.Item, out var k) ? k : 1f);
        float total = t.Items.Sum(W);
        int draws = rng.Next(t.Min, t.Max + 1);
        // sandbox (D99): more or less in everything; the fraction left over rounds up by chance (from the same id)
        double want = draws * Sandbox.Get("loot.abundance");
        draws = (int)want + (rng.NextDouble() < want - Math.Floor(want) ? 1 : 0);
        for (int i = 0; i < draws; i++)
        {
            float r = (float)rng.NextDouble() * total;
            foreach (var e in t.Items)
            {
                if ((r -= W(e)) > 0) continue;
                if (e.Item != "empty") res.Add(new ItemStack(e.Item, rng.Next(e.Min, e.Max + 1)));
                break;
            }
        }
        return res;
    }
}
