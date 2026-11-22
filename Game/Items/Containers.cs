using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using BalticDrive.Core;
using BB = BadBuilding;

namespace BalticDrive.Items;

/// <summary>
/// The furniture you can search (D89): every piece NoorFengShui gave a <c>loot_&lt;kind&gt;</c> marker (drawers,
/// wardrobes, fridges, kitchen cupboards, the mirror cabinet, shelves). What's inside is rolled from the loot tables
/// the first time it's opened, from the container's id, so it's the same whoever opens it first; after that what's
/// left is remembered (the world's changes are kept apart from the generated world: Noor's recon §5). Saved per map in
/// <c>user://containers.json</c>.
/// </summary>
public static class Containers
{
    private const string FilePath = "user://containers.json";
    private static Dictionary<string, Inventory>? _open;
    private static string _map = "";

    /// <summary>What a container holds now (rolled the first time).</summary>
    public static Inventory Get(string map, string id, string lootKind, string? room)
    {
        Load(map);
        if (_open!.TryGetValue(id, out var inv)) return inv;
        inv = new Inventory();
        foreach (var s in LootTables.Roll(lootKind, room, id)) inv.Add(s);
        _open[id] = inv;
        Save();
        return inv;
    }

    public static bool Searched(string map, string id) { Load(map); return _open!.ContainsKey(id); }

    /// <summary>After items moved: written out.</summary>
    public static void Save()
    {
        if (_open == null) return;
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            var all = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<object[]>>>>(File.ReadAllText(path)) ?? new() : new();
            if (_map == Main.SaveWorld) all.Remove("mazowsze");
            all[_map] = _open.ToDictionary(kv => kv.Key, kv => kv.Value.Stacks.Select(s => s.Made >= 0 ? new object[] { s.Id, s.Count, s.Made } : new object[] { s.Id, s.Count }).ToList());
            File.WriteAllText(path, JsonSerializer.Serialize(all));
        }
        catch (Exception ex) { Log.Error("Containers", ex.Message); }
    }

    private static void Load(string map)
    {
        if (_open != null && _map == map) return;
        _map = map;
        _open = new();
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            if (!File.Exists(path)) return;
            var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, List<JsonElement[]>>>>(File.ReadAllText(path));
            // before D90 the world was kept under the map's name
            if (all == null || !all.TryGetValue(map, out var mine) && !(map == Main.SaveWorld && all.TryGetValue("mazowsze", out mine))) return;
            foreach (var (id, stacks) in mine)
            {
                var inv = new Inventory();
                foreach (var s in stacks) inv.Add(s[0].GetString()!, s[1].GetInt32(), s.Length > 2 ? s[2].GetDouble() : double.NaN);
                _open[id] = inv;
            }
        }
        catch (Exception ex) { Log.Error("Containers", ex.Message); }
    }

    /// <summary>Self-tests: forget every searched container on a map.</summary>
    public static void Forget(string map) { Load(map); _open!.Clear(); Save(); }

    /// <summary>"the wardrobe": how a prompt names a piece of furniture.</summary>
    public static string Label(string furnitureKind) => Core.Lang.Pl ? furnitureKind switch
    {
        // D119: in Polish
        "nightstand" => "szafka nocna", "wardrobe" => "szafa", "desk" => "szuflady biurka", "bookshelf" => "regał z książkami",
        "tv_unit" => "szafka RTV", "fridge" => "lodówka", "counter" => "szafki kuchenne", "basin" => "szafka z lustrem",
        "shoe_cabinet" => "szafka na buty", "shelves" => "półki", _ => furnitureKind.Replace('_', ' '),
    } : furnitureKind switch
    {
        "nightstand" => "the bedside table",
        "wardrobe" => "the wardrobe",
        "desk" => "the desk drawers",
        "bookshelf" => "the bookcase",
        "tv_unit" => "the TV cabinet",
        "fridge" => "the fridge",
        "counter" => "the kitchen cupboards",
        "basin" => "the mirror cabinet",
        "shoe_cabinet" => "the shoe cabinet",
        "shelves" => "the shelves",
        _ => "the " + furnitureKind.Replace('_', ' '),
    };
}

/// <summary>A searchable piece within reach: its loot marker and furniture, in a loaded interior.</summary>
public sealed record ContainerSpot(string Id, string LootKind, string FurnitureKind, string? Room, Vector3 At);
