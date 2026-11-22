using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using BalticDrive.Core;

namespace BalticDrive.Items;

/// <summary>One kind of item (D89), from <c>GameData/items.json</c>.</summary>
public sealed record ItemDef(string Id, string Name, string Plural, string Category, float Weight, int Stack,
    float Food, float Drink, float Heal, string[] Tags, string? Leaves, string[] Teaches, string Note = "", float Fresh = 0, float Stale = 0)
{
    public string Count(int n) => n == 1 ? Name : $"{n} {Plural}";
    /// <summary>Does it answer to a recipe's need (D107): its own id, or "tag:x" when it has tag x.</summary>
    public bool Matches(string key) => key.StartsWith("tag:") ? Array.IndexOf(Tags, key[4..]) >= 0 : key == Id;
}

/// <summary>A pile of one item. <paramref name="Made"/>: when it was made, in <see cref="Core.WorldClock"/> seconds, for food
/// that goes off (D115); -1 for things that don't.</summary>
public readonly record struct ItemStack(string Id, int Count, double Made = -1);

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

    public static ItemDef? Get(string id) => All.TryGetValue(id, out var d) ? d : Evolved.Is(id) ? Evolved.Def(id) : null;   // a pot of an evolved dish is made up from its id (D114, D116)
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
                    e.TryGetProperty("leaves", out var lv) ? lv.GetString() : null,
                    e.TryGetProperty("teaches", out var te) ? te.EnumerateArray().Select(t => t.GetString()!).ToArray() : Array.Empty<string>(),
                    "", F("fresh"), F("stale"));
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
/// holds at most its item's <see cref="ItemDef.Stack"/> (more go into a new stack). Food that goes off (D115) merges
/// only with food made about the same time, and the oldest is used first; moving keeps when it was made.
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
    /// <summary>How many answer to an id or "tag:x" (D107); rotten food doesn't count (D115: it's only good for the bin).</summary>
    public int CountMatching(string key) => Stacks.Where(s => ItemDb.Get(s.Id) is { } d && d.Matches(key) && !Freshness.IsRotten(s)).Sum(s => s.Count);

    /// <summary>Uses up <paramref name="n"/> items answering to an id or tag (D107), never rotten ones, the oldest
    /// first; what they leave behind (an empty bottle) goes back in. Returns the ids used.</summary>
    public List<string> UseUp(string key, int n, bool leftovers = true)
    {
        var used = new List<string>();
        while (n > 0)
        {
            int i = -1;
            for (int j = 0; j < Stacks.Count; j++)
                if (ItemDb.Get(Stacks[j].Id) is { } d && d.Matches(key) && !Freshness.IsRotten(Stacks[j]) && (i < 0 || Stacks[j].Made < Stacks[i].Made)) i = j;
            if (i < 0) break;
            var st = Stacks[i];
            int k = Math.Min(n, st.Count);
            TakeAt(i, k);
            n -= k;
            for (int j = 0; j < k; j++) used.Add(st.Id);
            if (leftovers && ItemDb.Get(st.Id)?.Leaves is { } left) Add(left, k, st.Made);
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

    /// <summary>Adds up to <paramref name="n"/>; returns how many went in. <paramref name="made"/>: when food was made
    /// (default: now); things that don't go off ignore it.</summary>
    public int Add(string id, int n, double made = double.NaN)
    {
        var d = ItemDb.Get(id);
        if (d == null || n <= 0) return 0;
        bool perishable = Freshness.Perishable(d);
        if (!perishable) made = -1;
        else if (double.IsNaN(made) || made < 0) made = Core.WorldClock.Seconds;
        int take = Math.Min(n, Room(id)), left = take;
        for (int i = 0; i < Stacks.Count && left > 0; i++)
        {
            var st = Stacks[i];
            if (st.Id != id || st.Count >= d.Stack) continue;
            if (perishable && Math.Abs(st.Made - made) > Freshness.MergeSeconds) continue;   // different ages stay apart
            int add = Math.Min(left, d.Stack - st.Count);
            Stacks[i] = st with { Count = st.Count + add, Made = perishable ? Math.Min(st.Made, made) : -1 };
            left -= add;
        }
        while (left > 0) { int add = Math.Min(left, Math.Max(1, d.Stack)); Stacks.Add(new ItemStack(id, add, made)); left -= add; }
        if (take > 0) Changed?.Invoke();
        return take;
    }

    /// <summary>Adds a stack as it is (keeping its age).</summary>
    public int Add(ItemStack s) => Add(s.Id, s.Count, s.Made < 0 ? double.NaN : s.Made);

    private void TakeAt(int i, int k)
    {
        if (k >= Stacks[i].Count) Stacks.RemoveAt(i); else Stacks[i] = Stacks[i] with { Count = Stacks[i].Count - k };
        Changed?.Invoke();
    }

    /// <summary>Takes up to <paramref name="n"/> away, the oldest first; returns how many it had.</summary>
    public int Remove(string id, int n)
    {
        int left = n;
        while (left > 0)
        {
            int i = -1;
            for (int j = 0; j < Stacks.Count; j++)
                if (Stacks[j].Id == id && (i < 0 || Stacks[j].Made < Stacks[i].Made)) i = j;
            if (i < 0) break;
            int take = Math.Min(left, Stacks[i].Count);
            TakeAt(i, take);
            left -= take;
        }
        return n - left;
    }

    /// <summary>One of an item at a freshness <paramref name="stage"/> (null: any but rotten), the oldest of those;
    /// returns the stack it came from (count 1), or null.</summary>
    public ItemStack? TakeOne(string id, int? stage = null)
    {
        int i = -1;
        for (int j = 0; j < Stacks.Count; j++)
        {
            if (Stacks[j].Id != id) continue;
            int st = Freshness.Stage(Stacks[j]);
            if (stage != null ? st != stage : st == Freshness.Rotten) continue;
            if (i < 0 || Stacks[j].Made < Stacks[i].Made) i = j;
        }
        if (i < 0) return null;
        var s = Stacks[i] with { Count = 1 };
        TakeAt(i, 1);
        return s;
    }

    /// <summary>Moves up to <paramref name="n"/> of an item (at a freshness stage, if given) into another inventory, as
    /// many as fit there, the oldest first, keeping when it was made.</summary>
    public int MoveTo(Inventory to, string id, int n, int? stage = null)
    {
        int moved = 0;
        while (moved < n)
        {
            int i = -1;
            for (int j = 0; j < Stacks.Count; j++)
                if (Stacks[j].Id == id && (stage == null || Freshness.Stage(Stacks[j]) == stage) && (i < 0 || Stacks[j].Made < Stacks[i].Made)) i = j;
            if (i < 0) break;
            var st = Stacks[i];
            int k = to.Add(st.Id, Math.Min(n - moved, st.Count), st.Made < 0 ? double.NaN : st.Made);
            if (k == 0) break;
            TakeAt(i, k);
            moved += k;
        }
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
                if (e.Item != "empty")
                    res.Add(new ItemStack(e.Item, rng.Next(e.Min, e.Max + 1), ItemDb.Get(e.Item) is { } fd ? Freshness.FoundMade(fd, rng) : -1));   // found food has an age (D115)
                break;
            }
        }
        return res;
    }
}
