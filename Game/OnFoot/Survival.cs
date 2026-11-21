using System;
using System.Collections.Generic;
using System.Linq;
using BalticDrive.Items;
using BalticDrive.Map;
using BalticDrive.World;
using Godot;

namespace BalticDrive.OnFoot;

/// <summary>
/// The small survival loop (D46, the on-foot brief): health, hunger and thirst, "clear effects but no tedious
/// micromanagement". Hunger and thirst drain only on foot (a long drive shouldn't starve you), faster when
/// sprinting. Food and water you carry are used by themselves when you get hungry or thirsty; a medkit is H.
/// Low thirst stops sprinting, an empty stomach or bottle costs health, and a fed, watered body heals slowly.
/// </summary>
public sealed partial class Survival : Node
{
    private readonly Walker _w;
    public float Health = 100, Hunger = 85, Thirst = 80;   // hunger and thirst: 100 = full, 0 = starving / parched
    /// <summary>What you carry (D89): real items, as much as the weight allows.</summary>
    public readonly Inventory Pack = new(MaxCarry);
    public const float MaxCarry = 15f;

    // the old counters (D46), now views onto the pack: what there is to eat, to drink, to heal with. Setting one (the
    // self-tests do) replaces that kind of item with the plain one: beans, water, first aid kits.
    public int Food { get => Pack.CountWhere(IsFood); set => SetCount(IsFood, "beans", value); }
    public int Water { get => Pack.CountWhere(IsDrink); set => SetCount(IsDrink, "water", value); }
    public int Medkits { get => Pack.CountWhere(IsMedical); set => SetCount(IsMedical, "medkit", value); }
    private static bool IsFood(ItemDef d) => d.Category == "food";
    private static bool IsDrink(ItemDef d) => d.Category == "drink" && d.Drink > 0 && !d.Matches("tag:dirty_water");   // river water only by hand (D111)
    private static bool IsMedical(ItemDef d) => d.Category == "medical" && d.Heal > 0;
    private void SetCount(Func<ItemDef, bool> kind, string plain, int n)
    {
        foreach (var id in Pack.Stacks.Where(st => ItemDb.Get(st.Id) is { } d && kind(d)).Select(st => st.Id).Distinct().ToList()) Pack.Remove(id, int.MaxValue);
        Pack.Add(plain, n);
    }

    /// <summary>The item of a kind that best fits what's missing: the biggest that doesn't waste much, else the smallest.</summary>
    private string? Best(Func<ItemDef, bool> kind, Func<ItemDef, float> value, float missing)
    {
        var have = Pack.Stacks.Select(st => ItemDb.Get(st.Id)).OfType<ItemDef>().Where(kind).DistinctBy(d => d.Id).ToList();
        if (have.Count == 0) return null;
        var fits = have.Where(d => value(d) <= missing + 10).OrderByDescending(value).FirstOrDefault();
        return (fits ?? have.OrderBy(value).First()).Id;
    }
    /// <summary>Minutes of walking from full to empty.</summary>
    public const float HungerMinutes = 45, ThirstMinutes = 30;
    private float _healing;            // medkit health still to come

    /// <summary>A line for the HUD when something happens ("You eat a kebab").</summary>
    public event Action<string>? Said;
    public bool CanSprint => Thirst > 15 && Health > 20;

    public Survival(Walker w) { _w = w; Name = "Survival"; }

    public override void _Process(double delta)
    {
        if (!_w.Active) return;
        float dt = (float)delta;
        float effort = _w.Speed > Walker.RunSpeed + 0.5f ? 2f : _w.Speed > 0.5f ? 1.2f : 1f;
        Hunger = MathF.Max(0, Hunger - dt * effort * 100f / (HungerMinutes * 60));
        Thirst = MathF.Max(0, Thirst - dt * effort * 100f / (ThirstMinutes * 60));
        // carried food and water are used without asking, before it hurts: what fits the hunger best
        if (Hunger < 35 && Best(IsFood, d => d.Food, 100 - Hunger) is { } eat)
        {
            var d = ItemDb.Def(eat);
            Pack.Remove(eat, 1);
            Hunger = Math.Clamp(Hunger + d.Food, 0, 100); Thirst = Math.Clamp(Thirst + d.Drink, 0, 100);
            Say($"You eat a {d.Name}");
        }
        if (Thirst < 35 && Best(IsDrink, d => d.Drink, 100 - Thirst) is { } drink)
        {
            var d = ItemDb.Def(drink);
            Pack.Remove(drink, 1);
            Thirst = Math.Clamp(Thirst + d.Drink, 0, 100); Hunger = Math.Clamp(Hunger + d.Food, 0, 100);
            if (d.Leaves != null) Pack.Add(d.Leaves, 1);   // the empty bottle (D107: from the data)
            Say($"You drink a {d.Name}");
        }
        // health: empty stomach or bottle hurts, a looked-after body heals up to 70 by itself, a medkit all the way
        if (Hunger <= 0) { Health -= dt * 0.25f; _cause = "starved"; }
        if (Thirst <= 0) { Health -= dt * 0.4f; _cause = "dehydrated"; }
        if (Hunger > 50 && Thirst > 50 && Health < 70) Health = MathF.Min(70, Health + dt * 0.15f);
        if (_healing > 0) { float h = MathF.Min(_healing, dt * 12f); _healing -= h; Health = MathF.Min(100, Health + h); }
        if (Health <= 0) { Health = 0; Fall(); }
    }

    /// <summary>Can an item be used from the inventory (D89): food, drink, medicine.</summary>
    public static bool Usable(ItemDef d) => d.Category is "food" or "drink" or "fuel" or "book" || d.Category == "medical" && d.Heal > 0;

    /// <summary>Petrol from the pack (D109): litres offered, litres taken by whatever is in reach (a generator), or -1
    /// when nothing is. Set by the game.</summary>
    public static Func<float, float>? PourFuel;

    /// <summary>Eats, drinks or uses one of an item you carry (the inventory's Use button).</summary>
    public void UseItem(string id)
    {
        var d = ItemDb.Get(id);
        if (d == null || Pack.Count(id) == 0) return;
        if (d.Category == "fuel")
        {
            float took = PourFuel?.Invoke(5f) ?? -1;
            if (took < 0) { Say("Nothing to pour it into here: stand by a generator"); return; }
            if (took <= 0.01f) { Say("The tank is full"); return; }
            // a can holds 5 litres and isn't measured: less than half poured, it's still a can; more, it's used up
            if (took < 2.5f) { Say($"You top it up with {took:0.0} litres; the can's still mostly full"); return; }
            Say(took < 4.95f ? $"You pour {took:0.0} litres in; the dregs aren't worth keeping" : "You pour the whole can in");
            Pack.Remove(id, 1);
            if (d.Leaves != null) Pack.Add(d.Leaves, 1);
            return;
        }
        if (d.Category == "medical")
        {
            if (Health >= 99) { Say("You're fine"); return; }
            _healing += d.Heal * (1 + Skills.Bonus("first_aid"));   // a practised hand (D113)
            Skills.Add("first_aid", 8);
            Say($"You patch yourself up with a {d.Name}");
        }
        else
        {
            Hunger = Math.Clamp(Hunger + d.Food, 0, 100); Thirst = Math.Clamp(Thirst + d.Drink, 0, 100);
            Say(d.Category == "drink" ? $"You drink a {d.Name}" : $"You eat a {d.Name}");
            if (d.Matches("tag:dirty_water"))
            {
                // unboiled (D111): it quenches, and it costs you
                Health = MathF.Max(1, Health - 8);
                _cause = "river water";
                Say("It tastes of mud. Your stomach turns. Boil it next time.");
            }
        }
        Pack.Remove(id, 1);
        if (d.Leaves != null) Pack.Add(d.Leaves, 1);
    }

    public void UseMedkit()
    {
        if (Medkits <= 0) { Say("Nothing to patch yourself up with"); return; }
        if (Health >= 99) { Say("You're fine"); return; }
        // what fits the wound: a first aid kit for a bad one, a bandage or a plaster for less
        var id = Best(IsMedical, d => d.Heal, 100 - Health)!;
        var def = ItemDb.Def(id);
        Pack.Remove(id, 1);
        _healing += def.Heal * (1 + Skills.Bonus("first_aid"));
        Skills.Add("first_aid", 8);
        Say($"You patch yourself up with a {def.Name}");
    }

    /// <summary>A bullet or a fall (D47: bandits).</summary>
    public void Damage(float amount)
    {
        if (IsDown) return;
        Health -= amount;
        _cause = "shot";
        Hurt?.Invoke(amount);
        if (Health <= 0) { Health = 0; Fall(); }
    }

    /// <summary>Damage taken (for the red flash).</summary>
    public event Action<float>? Hurt;
    /// <summary>The player went down (D50): what did it ("shot", "starved", "dehydrated"). The body falls and the game
    /// waits for a click to wake you somewhere else (D55: <see cref="PackUp"/>, <see cref="Revive"/>).</summary>
    public event Action<string>? Down;
    public bool IsDown { get; private set; }
    private string _cause = "shot";

    /// <summary>What a cache gave you; returns the lines worth saying.</summary>
    public string Take(Loot l)
    {
        var said = new List<string>();
        // the old counted loot as items: the food by its name, plain water, first aid kits; and any real items
        var items = new List<ItemStack>(l.Items ?? []);
        if (l.Food > 0) items.Add(new ItemStack(ItemDb.All.Values.FirstOrDefault(d => d.Name == l.FoodName || d.Plural == l.FoodName)?.Id ?? "beans", l.Food));
        if (l.Water > 0) items.Add(new ItemStack("water", l.Water));
        if (l.Medkits > 0) items.Add(new ItemStack("medkit", l.Medkits));
        bool full = false;
        var got = new List<ItemStack>();
        foreach (var it in items)
        {
            int n = Pack.Add(it.Id, it.Count);
            if (n > 0) got.Add(new ItemStack(it.Id, n));
            if (n < it.Count) full = true;
        }
        if (got.Count > 0) said.Add(Inventory.Say(got));
        foreach (var (gun, rounds) in l.Ammo)
            if (_w.Guns.AddAmmo(gun, rounds) is var took and > 0)
                said.Add($"{took} rounds for the {WeaponDef.All.First(d => d.Id == gun).Name}");
        return said.Count == 0 ? (full ? "Nothing you can carry" : "Empty") : "Found " + string.Join(", ", said) + (full ? " (too heavy to take it all)" : "");
    }

    private void Fall()
    {
        if (IsDown) return;
        IsDown = true;
        _healing = 0;
        Down?.Invoke(_cause);
    }

    /// <summary>What you had on you (D55): food, water, medkits and every spare round come off into the package left
    /// where you fell. The guns and what's loaded in them stay with you (Tom's pick).</summary>
    public Loot PackUp()
    {
        var loot = new Loot(0, "", 0, 0, _w.Guns.TakeReserve(), Pack.Stacks.ToList());
        Pack.Stacks.Clear();
        return loot;
    }

    /// <summary>Waking up somewhere else (D55): whole again, fed and watered enough to walk back for your things.</summary>
    public void Revive()
    {
        IsDown = false;
        Health = 100; Hunger = MathF.Max(Hunger, 60); Thirst = MathF.Max(Thirst, 60);
    }

    public void Say(string s) => Said?.Invoke(s);
}

/// <summary>What one cache holds.</summary>
public sealed record Loot(int Food, string FoodName, int Water, int Medkits, (string Gun, int Rounds)[] Ammo, IReadOnlyList<ItemStack>? Items = null)
{
    public int ItemCount => Food + Water + Medkits + (Items?.Sum(s => s.Count) ?? 0);
}

/// <summary>
/// Outdoor supply caches (D46, the brief: "OSM points of interest help decide what might be found nearby"). Around the
/// player only: every couple of seconds, the points of interest within ~150 m are looked at; each gets a cache or
/// not by its own id (the same shop always has the same luck), and the cache is set down outside, on open ground near
/// it. What's inside follows the place: food and water at shops and diners, water and snacks and some ammo at petrol
/// stations, medkits at pharmacies, ammo at police stations, hardware shops and garages. Opened caches stay opened
/// for the session. Far ones are removed.
/// </summary>
public sealed partial class CacheField : Node3D
{
    private const string Small = "res://Assets/PolygonBattleRoyale/Prefabs/Props/SM_Prop_Crate_Small_01.tscn";
    private const string Large = "res://Assets/PolygonBattleRoyale/Prefabs/Props/SM_Prop_Crate_01.tscn";
    private const string Medical = "res://Assets/PolygonBattleRoyale/Prefabs/Props/SM_Prop_Crate_Medical_01.tscn";
    public const float Near = 150f, Far = 220f, Reach = 2.6f;
    private readonly MapWorld _world;
    private readonly Dictionary<string, Cache> _live = new();
    private readonly HashSet<string> _opened = new();
    private double _nextLook;

    public sealed partial class Cache : StaticBody3D
    {
        public string Key = "", Place = "";
        public MapPoiKind Kind;
        public Loot Loot = null!;
        public bool Opened;
        /// <summary>A lead's stash (D54): not removed with distance; <see cref="Leads"/> looks after it.</summary>
        public bool Keep;
    }

    /// <summary>A crate was opened (the crate, where): leads listen for notes (D54).</summary>
    public event Action<Cache, Vector3>? Opened;

    public IEnumerable<Cache> Live => _live.Values;

    public CacheField(MapWorld world) { _world = world; Name = "Caches"; }

    public override void _Ready() => WorldOrigin.Shifted += OnShifted;
    public override void _ExitTree() => WorldOrigin.Shifted -= OnShifted;
    private void OnShifted(double dx, double dz)
    {
        var d = new Vector3((float)dx, 0, (float)dz);
        foreach (var c in _live.Values) c.GlobalPosition -= d;
    }

    /// <summary>Looks around <paramref name="at"/> (local coordinates) every 2 s: new caches in reach, far ones gone.</summary>
    public void Update(Vector3 at, double now)
    {
        if (now < _nextLook) return;
        _nextLook = now + 2;
        var (wx, _, wz) = WorldOrigin.ToWorld(at);
        foreach (var key in _live.Where(kv => !kv.Value.Keep && kv.Value.GlobalPosition.DistanceTo(at) > Far).Select(kv => kv.Key).ToList())
        {
            _live[key].QueueFree();
            _live.Remove(key);
        }
        int tx0 = (int)Math.Floor((wx - Near) / MapTile.Size), tx1 = (int)Math.Floor((wx + Near) / MapTile.Size);
        int tz0 = (int)Math.Floor((wz - Near) / MapTile.Size), tz1 = (int)Math.Floor((wz + Near) / MapTile.Size);
        for (int tx = tx0; tx <= tx1; tx++)
            for (int tz = tz0; tz <= tz1; tz++)
            {
                if (!_world.Data.HasTile(tx, tz)) continue;
                var tile = _world.Data.Tile(tx, tz);
                foreach (var poi in tile.Pois)
                {
                    double px = tx * (double)MapTile.Size + poi.X, pz = tz * (double)MapTile.Size + poi.Z;
                    if ((px - wx) * (px - wx) + (pz - wz) * (pz - wz) > Near * Near) continue;
                    string key = string.IsNullOrEmpty(poi.Id) ? $"{tx}.{tz}.{poi.X:0}.{poi.Z:0}" : poi.Id;
                    if (_live.ContainsKey(key)) continue;
                    var rng = new Random(Hash(key));
                    if (rng.NextDouble() > Chance(poi.Kind) * Core.Sandbox.Get("caches.frequency")) continue;   // most places have nothing (sandbox, D99)
                    if (Spot(px, pz, rng) is not { } spot) continue;
                    var c = MakeCache(key, poi, rng, spot);
                    c.Opened = _opened.Contains(key);
                    _live[key] = c;
                }
            }
    }

    private static int Hash(string s) { unchecked { int h = 17; foreach (char ch in s) h = h * 31 + ch; return h; } }

    private static double Chance(MapPoiKind k) => k switch
    {
        MapPoiKind.Medical or MapPoiKind.Police or MapPoiKind.Fuel => 0.6,
        MapPoiKind.Camp => 0.7,
        _ => 0.35,
    };

    /// <summary>Open ground near the place (not inside a building, not in the carriageway), with a ray for the height.</summary>
    /// <param name="clear">Room wanted round it (D54: a stash needs a clear patch, not a fenced tree pit): nothing of
    /// buildings, props or fences within that radius.</param>
    private Vector3? Spot(double px, double pz, Random rng, float clear = 0)
    {
        var space = GetWorld3D().DirectSpaceState;
        double a0 = rng.NextDouble() * Math.Tau;
        var room = clear > 0 ? new PhysicsShapeQueryParameters3D { Shape = new SphereShape3D { Radius = clear }, CollisionMask = Bodies.Buildings | Bodies.Props | Bodies.Vehicles } : null;
        for (int ring = 0; ring < (clear > 0 ? 7 : 5); ring++)
            for (int k = 0; k < 8; k++)
            {
                double r = (clear > 0 ? 4 : 2) + ring * 3, a = a0 + k * Math.Tau / 8;
                double x = px + Math.Cos(a) * r, z = pz + Math.Sin(a) * r;
                if (_world.InBuilding(x, z, 0.8f)) continue;
                if (_world.RoadEdgeDistance(x, z, 6f) < 0.5f) continue;   // on a road: no
                var local = WorldOrigin.ToLocal(x, 0, z);
                var q = PhysicsRayQueryParameters3D.Create(local + Vector3.Up * 300, local + Vector3.Down * 300, Bodies.Ground | Bodies.Roads);
                var hit = space.IntersectRay(q);
                if (hit.Count == 0) continue;   // no collision there yet: try again on the next look
                var g = (Vector3)hit["position"];
                if (room != null)
                {
                    room.Transform = new Transform3D(Basis.Identity, g + Vector3.Up * (clear + 0.15f));
                    if (space.IntersectShape(room, 1).Count > 0) continue;
                }
                return g;
            }
        return null;
    }

    private Cache MakeCache(string key, MapPoi poi, Random rng, Vector3 at, Loot? loot = null, string? scene = null)
    {
        loot ??= Fill(poi, rng);
        var c = new Cache { Key = key, Place = poi.Name, Kind = poi.Kind, Loot = loot, CollisionLayer = Bodies.Props, CollisionMask = 0 };
        c.SetMeta("surface", "hard");
        bool medical = poi.Kind == MapPoiKind.Medical;
        scene ??= medical ? Medical : Small;
        Aabb box = new(new Vector3(-0.4f, 0, -0.22f), new Vector3(0.8f, 0.25f, 0.44f));
        if (ResourceLoader.Exists(scene) && ResourceLoader.Load<PackedScene>(scene) is { } ps)
        {
            var n = ps.Instantiate<Node3D>();
            foreach (var body in n.FindChildren("*", "CollisionObject3D", true, false)) body.Free();
            if (n is MeshInstance3D mi && mi.Mesh != null) box = mi.Mesh.GetAabb();
            c.AddChild(n);
        }
        else c.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = box.Size }, Position = box.GetCenter() });
        c.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = box.Size }, Position = box.GetCenter() });
        AddChild(c);
        c.GlobalPosition = at;
        c.Rotation = new Vector3(0, (float)(rng.NextDouble() * Math.Tau), 0);
        return c;
    }

    /// <summary>What a place holds. A kebab place gives kebabs (Tom's first wish on foot).</summary>
    private static Loot Fill(MapPoi poi, Random rng)
    {
        string name = poi.Name.ToLowerInvariant();
        string food = name.Contains("kebab") || name.Contains("kebap") ? "kebab"
            : name.Contains("pizz") ? "slice of pizza"
            : name.Contains("piekar") || name.Contains("bakery") ? "loaf of bread"
            : poi.Kind == MapPoiKind.Fuel ? "hot dog"
            : poi.Kind == MapPoiKind.Diner ? "zapiekanka"
            : "tin of beans";
        int R(int lo, int hi) => rng.Next(lo, hi + 1);
        return poi.Kind switch
        {
            MapPoiKind.Food or MapPoiKind.Shop => new Loot(R(1, 2), food, R(1, 2), 0, rng.NextDouble() < 0.15 ? new[] { ("pistol", 8) } : Array.Empty<(string, int)>()),
            MapPoiKind.Diner => new Loot(R(1, 2), food, R(0, 1), 0, Array.Empty<(string, int)>()),
            MapPoiKind.Fuel => new Loot(R(0, 1), food, R(1, 2), rng.NextDouble() < 0.3 ? 1 : 0, new[] { (rng.NextDouble() < 0.5 ? "ak" : "pistol", rng.NextDouble() < 0.5 ? 15 : 8) }),
            MapPoiKind.Medical => new Loot(0, food, R(0, 1), R(1, 2), Array.Empty<(string, int)>()),
            MapPoiKind.Police => new Loot(0, food, 0, rng.NextDouble() < 0.4 ? 1 : 0, new[] { ("ak", 30), ("pistol", 16) }),
            MapPoiKind.Hardware or MapPoiKind.Garage => new Loot(0, food, R(0, 1), 0, new[] { (rng.NextDouble() < 0.5 ? "mosin" : "ak", rng.NextDouble() < 0.5 ? 10 : 15) }),
            _ => new Loot(R(0, 1), food, R(0, 1), rng.NextDouble() < 0.3 ? 1 : 0, new[] { ("mosin", 5) }),
        };
    }

    /// <summary>A lead's stash (D54): the big crate, outside the place, kept until <see cref="Remove"/>. Null when
    /// there's no ground collision there yet (try again later).</summary>
    public Cache? PlaceStash(string key, double x, double z, Loot loot, string place)
    {
        if (_live.TryGetValue(key, out var have)) return have;
        var rng = new Random(Hash(key));
        if (Spot(x, z, rng, clear: 1.6f) is not { } spot) return null;
        var c = MakeCache(key, new MapPoi { Name = place, Kind = MapPoiKind.Camp }, rng, spot, loot, Large);
        c.Keep = true;
        c.Opened = _opened.Contains(key);
        // a pile, not one flat box you walk past: a small crate on top, a medical one beside it
        float top = c.GetChild(0) is MeshInstance3D { Mesh: { } m } ? m.GetAabb().End.Y : 0.4f;
        foreach (var (scene, at, yaw) in new[] { (Small, new Vector3(0.1f, top, 0.05f), 0.5f), (Medical, new Vector3(1.05f, 0, 0.35f), -0.3f) })
            if (ResourceLoader.Exists(scene) && ResourceLoader.Load<PackedScene>(scene) is { } ps)
            {
                var n = ps.Instantiate<Node3D>();
                foreach (var body in n.FindChildren("*", "CollisionObject3D", true, false)) body.Free();
                n.Position = at; n.Rotation = new Vector3(0, yaw, 0);
                c.AddChild(n);
            }
        _live[key] = c;
        return c;
    }

    public void Remove(Cache c)
    {
        _live.Remove(c.Key);
        c.QueueFree();
    }

    private int _deaths;

    /// <summary>Your things where you fell (D55): searchable like a body, kept however far you go until you take
    /// them, with your body lying there (a copy of the walker's, in the pose it fell in).</summary>
    public Cache PlacePackage(Vector3 at, Loot loot, Transform3D corpse, Func<Skeleton3D, bool>? pose = null)
    {
        var c = new Cache { Key = $"death:{++_deaths}", Place = "your things", Kind = MapPoiKind.Camp, Loot = loot, Keep = true, CollisionLayer = 0, CollisionMask = 0 };
        AddChild(c);
        c.GlobalPosition = at;
        var body = new Node3D { Name = "Body" };
        var (copy, tree) = CharacterRig.Build(body, "Character_Male_Hoodie", out _);
        c.AddChild(body);
        body.GlobalTransform = corpse;
        // as the ragdoll left it (D56): every bone frozen, no animation; without one, slumped as a bandit lay (D47)
        if (tree != null) tree.Active = false;   // first: switching it off afterwards could put the rest pose back
        if (copy != null && pose != null && pose(copy)) { }
        else if (tree != null) { tree.Active = true; CharacterRig.Animate(tree, Vector2.Zero, 1f, 0f); }
        _live[c.Key] = c;
        return c;
    }

    private int _drops;

    /// <summary>A searchable spot with no crate: what a fallen bandit had on them (D47).</summary>
    public void Drop(Vector3 at, Loot loot, string place)
    {
        var c = new Cache { Key = $"drop:{++_drops}", Place = place, Kind = MapPoiKind.Camp, Loot = loot, CollisionLayer = 0, CollisionMask = 0 };
        AddChild(c);
        c.GlobalPosition = at;
        _live[c.Key] = c;
    }

    /// <summary>The unopened cache in reach of a point, if any.</summary>
    public Cache? InReach(Vector3 at) =>
        _live.Values.Where(c => !c.Opened && c.GlobalPosition.DistanceTo(at) < Reach).OrderBy(c => c.GlobalPosition.DistanceTo(at)).FirstOrDefault();

    /// <summary>Opens it: the lid comes up (the crate tips a little), and it stays opened.</summary>
    public void Open(Cache c)
    {
        c.Opened = true;
        _opened.Add(c.Key);
        if (c.Key.StartsWith("death:")) c.Keep = false;   // taken: the body goes when you're far, like the bandits'
        Opened?.Invoke(c, c.GlobalPosition);
        if (c.GetChildCount() > 0 && c.GetChild(0) is MeshInstance3D look) look.RotationDegrees = new Vector3(0, 0, 4);
    }
}
