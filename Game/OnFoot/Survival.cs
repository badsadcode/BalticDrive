using System;
using System.Collections.Generic;
using System.Linq;
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
    public int Food, Water, Medkits;
    public const int MaxFood = 4, MaxWater = 4, MaxMedkits = 3;
    /// <summary>Minutes of walking from full to empty.</summary>
    public const float HungerMinutes = 45, ThirstMinutes = 30;
    private float _healing;            // medkit health still to come
    private string _lastFood = "food";

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
        // carried food and water are used without asking, before it hurts
        if (Hunger < 35 && Food > 0) { Food--; Hunger = MathF.Min(100, Hunger + 40); Say($"You eat the {_lastFood}"); }
        if (Thirst < 35 && Water > 0) { Water--; Thirst = MathF.Min(100, Thirst + 45); Say("You drink a bottle of water"); }
        // health: empty stomach or bottle hurts, a looked-after body heals up to 70 by itself, a medkit all the way
        if (Hunger <= 0) { Health -= dt * 0.25f; _cause = "starved"; }
        if (Thirst <= 0) { Health -= dt * 0.4f; _cause = "dehydrated"; }
        if (Hunger > 50 && Thirst > 50 && Health < 70) Health = MathF.Min(70, Health + dt * 0.15f);
        if (_healing > 0) { float h = MathF.Min(_healing, dt * 12f); _healing -= h; Health = MathF.Min(100, Health + h); }
        if (Health <= 0) { Health = 0; Fall(); }
    }

    public void UseMedkit()
    {
        if (Medkits <= 0) { Say("No medkit"); return; }
        if (Health >= 99) { Say("You're fine"); return; }
        Medkits--;
        _healing += 60;
        Say("You patch yourself up");
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
    /// <summary>The player went down (D50): what did it ("shot", "starved", "dehydrated"). The game fades out, moves
    /// you to your car and calls <see cref="WakeUp"/>.</summary>
    public event Action<string>? Down;
    public bool IsDown { get; private set; }
    private string _cause = "shot";

    /// <summary>What a cache gave you; returns the lines worth saying.</summary>
    public string Take(Loot l)
    {
        var said = new List<string>();
        int f = Math.Min(l.Food, MaxFood - Food), w = Math.Min(l.Water, MaxWater - Water), m = Math.Min(l.Medkits, MaxMedkits - Medkits);
        if (f > 0) { Food += f; _lastFood = l.FoodName; said.Add(f == 1 ? l.FoodName : $"{f}× {l.FoodName}"); }
        if (w > 0) { Water += w; said.Add(w == 1 ? "water" : $"{w}× water"); }
        if (m > 0) { Medkits += m; said.Add(m == 1 ? "a medkit" : $"{m} medkits"); }
        foreach (var (gun, rounds) in l.Ammo)
            if (_w.Guns.AddAmmo(gun, rounds) is var took and > 0)
                said.Add($"{took} rounds for the {WeaponDef.All.First(d => d.Id == gun).Name}");
        bool full = (l.Food > f || l.Water > w || l.Medkits > m);
        return said.Count == 0 ? (full ? "Nothing you can carry" : "Empty") : "Found " + string.Join(", ", said) + (full ? " (pockets full)" : "");
    }

    private void Fall()
    {
        if (IsDown) return;
        IsDown = true;
        _healing = 0;
        Down?.Invoke(_cause);
    }

    /// <summary>Coming round (D50): 60 health, hunger and thirst at least 40, and the pack gone: food, water, medkits,
    /// and half the spare rounds of every gun. The guns and what's in them stay.</summary>
    public string WakeUp()
    {
        IsDown = false;
        Health = 60; Hunger = MathF.Max(Hunger, 40); Thirst = MathF.Max(Thirst, 40);
        int lost = Food + Water + Medkits;
        Food = Water = Medkits = 0;
        int rounds = _w.Guns.LoseHalfReserve();
        return lost + rounds == 0 ? "" : $"your pack is gone{(rounds > 0 ? $", and {rounds} rounds" : "")}";
    }

    private void Say(string s) => Said?.Invoke(s);
}

/// <summary>What one cache holds.</summary>
public sealed record Loot(int Food, string FoodName, int Water, int Medkits, (string Gun, int Rounds)[] Ammo);

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
    }

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
        foreach (var key in _live.Where(kv => kv.Value.GlobalPosition.DistanceTo(at) > Far).Select(kv => kv.Key).ToList())
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
                    if (rng.NextDouble() > Chance(poi.Kind)) continue;        // most places have nothing
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
    private Vector3? Spot(double px, double pz, Random rng)
    {
        var space = GetWorld3D().DirectSpaceState;
        double a0 = rng.NextDouble() * Math.Tau;
        for (int ring = 0; ring < 5; ring++)
            for (int k = 0; k < 8; k++)
            {
                double r = 2 + ring * 3, a = a0 + k * Math.Tau / 8;
                double x = px + Math.Cos(a) * r, z = pz + Math.Sin(a) * r;
                if (_world.InBuilding(x, z, 0.8f)) continue;
                if (_world.RoadEdgeDistance(x, z, 6f) < 0.5f) continue;   // on a road: no
                var local = WorldOrigin.ToLocal(x, 0, z);
                var q = PhysicsRayQueryParameters3D.Create(local + Vector3.Up * 300, local + Vector3.Down * 300, Bodies.Ground | Bodies.Roads);
                var hit = space.IntersectRay(q);
                if (hit.Count == 0) continue;   // no collision there yet: try again on the next look
                return (Vector3)hit["position"];
            }
        return null;
    }

    private Cache MakeCache(string key, MapPoi poi, Random rng, Vector3 at)
    {
        var loot = Fill(poi, rng);
        var c = new Cache { Key = key, Place = poi.Name, Kind = poi.Kind, Loot = loot, CollisionLayer = Bodies.Props, CollisionMask = 0 };
        c.SetMeta("surface", "hard");
        bool medical = poi.Kind == MapPoiKind.Medical;
        string scene = medical ? Medical : Small;
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
        if (c.GetChildCount() > 0 && c.GetChild(0) is MeshInstance3D look) look.RotationDegrees = new Vector3(0, 0, 4);
    }
}
