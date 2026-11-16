using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BalticDrive.Core;
using BalticDrive.Map;
using BalticDrive.World;
using Godot;

namespace BalticDrive.OnFoot;

/// <summary>
/// A reason to roam (D54, Tom's pick): leads. Now and then a crate holds a scrap of paper, or a dead bandit's radio
/// crackles, naming a real place a few kilometres away where a gang keeps a stash: "the Orlen on Grochowska". The lead
/// goes on the map (M) and the radar points to it. Near it (180 m) the stash is set down outside the place with a
/// group of three to five bandits camped round it; it's worth the fight (ammo for every gun, medkits, food, water),
/// and it often holds the next lead. The first crate you open always has one, so the hook shows up early.
/// Everything is per session and local, like the crates: nothing is simulated far away; a lead is a place and a
/// promise until you come close.
/// </summary>
public sealed partial class Leads : Node
{
    public enum Stage { Known, Guarded, Taken }

    public sealed class Lead
    {
        public int Id;
        public string Key = "", Place = "", Street = "", Gang = "";
        public MapPoiKind Kind;
        public double X, Z;                 // world metres
        public Stage Stage = Stage.Known;
        public int Guards;                  // still alive (set when you first come close)
        public CacheField.Cache? Stash;
        /// <summary>"the Orlen on Grochowska", "a petrol station on Grochowska", "the Biedronka".</summary>
        public string Where => (Place.Length > 0 ? Place : Generic(Kind)) + (Street.Length > 0 ? " on " + Street : "");
    }

    public const float Arrive = 180f, Forget = 300f;   // under the bandits' own 320 m, so guards and stash go together
    public const int MaxOpen = 3;
    private readonly MapWorld _world;
    private readonly CacheField _caches;
    private readonly BanditField _bandits;
    private readonly List<Lead> _all = new();
    private readonly HashSet<string> _used = new();
    private readonly ConcurrentQueue<(Lead? Lead, string Source)> _found = new();
    private readonly Random _rng = new();
    private int _next, _searching;
    private bool _first = true;

    /// <summary>Messages for the player (the note at the bottom of the screen).</summary>
    public event Action<string>? Said;
    public IReadOnlyList<Lead> All => _all;
    public int Taken => _all.Count(l => l.Stage == Stage.Taken);

    private static readonly string[] Gangs =
    {
        "Kuba's lot", "the Praga boys", "the Wola crew", "the Siekiera gang", "the tracksuits", "Zbyszek's people",
        "the Ursus lads", "the old garage crew",
    };

    public Leads(MapWorld world, CacheField caches, BanditField bandits)
    {
        _world = world; _caches = caches; _bandits = bandits; Name = "Leads";
        _bandits.Fell += OnBanditDied;
        _caches.Opened += OnCrateOpened;
    }

    public override void _ExitTree()
    {
        _bandits.Fell -= OnBanditDied;
        _caches.Opened -= OnCrateOpened;
    }

    // ---- where leads come from ------------------------------------------------------------------------------------

    private void OnCrateOpened(CacheField.Cache c, Vector3 at)
    {
        if (c.Key.StartsWith("lead:"))
        {
            var lead = _all.FirstOrDefault(l => l.Stash == c);
            if (lead != null) { lead.Stage = Stage.Taken; Log.Info("Leads", $"stash {lead.Id} taken ({lead.Where})"); }
            // a stash usually points on
            if (_rng.NextDouble() < 0.7) Look(at, "stash");
            return;
        }
        if (c.Key.StartsWith("drop:")) return;   // bodies: see OnBanditDied
        if (_first || _rng.NextDouble() < 0.2) Look(at, "crate");
        _first = false;
    }

    private void OnBanditDied(Bandit b)
    {
        var lead = _all.FirstOrDefault(l => l.Stage == Stage.Guarded && b.Guarding == l.Id);
        if (lead != null)
        {
            lead.Guards = Math.Max(0, lead.Guards - 1);
            if (lead.Guards == 0) Said?.Invoke($"That's the last of {lead.Gang}. The stash is yours.");
            return;
        }
        if (_rng.NextDouble() < 0.15) Look(b.GlobalPosition, "radio");
    }

    /// <summary>Finds a place for a new lead, 1.2–4 km from <paramref name="at"/>, on a worker thread (tiles may need
    /// loading); the result is picked up in <see cref="_Process"/>.</summary>
    public void Look(Vector3 at, string source, double minKm = 1.2, double maxKm = 4.0)
    {
        if (_all.Count(l => l.Stage != Stage.Taken) + _searching >= MaxOpen) return;
        var (wx, _, wz) = WorldOrigin.ToWorld(at);
        int seed = _rng.Next();
        var used = new HashSet<string>(_used);
        _searching++;
        Task.Run(() =>
        {
            Lead? found = null;
            try { found = Pick(wx, wz, minKm * 1000, maxKm * 1000, new Random(seed), used); }
            catch (Exception e) { Log.Error("Leads", e.Message); }
            _found.Enqueue((found, source));
        });
    }

    /// <summary>Kinds worth a gang's stash: places with stock and space round the back.</summary>
    private static bool Worth(MapPoiKind k) => k is MapPoiKind.Fuel or MapPoiKind.Police or MapPoiKind.Garage
        or MapPoiKind.Hardware or MapPoiKind.Medical or MapPoiKind.Shop or MapPoiKind.Camp;

    private Lead? Pick(double wx, double wz, double min, double max, Random rng, HashSet<string> used)
    {
        // a few random directions; in each, the tile at that distance (and only that: no ring of 25 tile loads)
        for (int tries = 0; tries < 10; tries++)
        {
            double a = rng.NextDouble() * Math.Tau, d = min + rng.NextDouble() * (max - min);
            double px = wx + Math.Cos(a) * d, pz = wz + Math.Sin(a) * d;
            int tx = (int)Math.Floor(px / MapTile.Size), tz = (int)Math.Floor(pz / MapTile.Size);
            if (!_world.Data.HasTile(tx, tz)) continue;
            var tile = _world.Data.Tile(tx, tz);
            var ok = tile.Pois.Where(p => Worth(p.Kind)).Select(p => (p, x: tile.OriginX + p.X, z: tile.OriginZ + p.Z))
                .Where(q => { double r = Math.Sqrt((q.x - wx) * (q.x - wx) + (q.z - wz) * (q.z - wz)); return r >= min && r <= max * 1.25; })
                .Where(q => !used.Contains(KeyOf(tile, q.p)))
                // named places first: "the Orlen" reads better than "a petrol station"
                .OrderByDescending(q => q.p.Name.Length > 0 ? 1 : 0).ThenBy(_ => rng.Next())
                .ToList();
            if (ok.Count == 0) continue;
            var (poi, x, z) = ok[0];
            return new Lead
            {
                Key = KeyOf(tile, poi), Place = poi.Name, Kind = poi.Kind, X = x, Z = z,
                Street = StreetNear(x, z), Gang = Gangs[rng.Next(Gangs.Length)],
            };
        }
        return null;
    }

    private static string KeyOf(MapTile tile, MapPoi p) => string.IsNullOrEmpty(p.Id) ? $"{tile.Tx}.{tile.Tz}.{p.X:0}.{p.Z:0}" : p.Id;

    /// <summary>The nearest named street within 80 m ("" if none): how the note says where.</summary>
    private string StreetNear(double x, double z)
    {
        string best = ""; double bd = 80 * 80;
        int tx0 = (int)Math.Floor((x - 80) / MapTile.Size), tx1 = (int)Math.Floor((x + 80) / MapTile.Size);
        int tz0 = (int)Math.Floor((z - 80) / MapTile.Size), tz1 = (int)Math.Floor((z + 80) / MapTile.Size);
        for (int tx = tx0; tx <= tx1; tx++)
            for (int tz = tz0; tz <= tz1; tz++)
            {
                if (!_world.Data.HasTile(tx, tz)) continue;
                var t = _world.Data.Tile(tx, tz);
                foreach (var r in t.Roads)
                {
                    if (r.Name.Length == 0 || r.IsRail || r.IsTram) continue;
                    for (int i = 0; i < r.X.Length; i++)
                    {
                        double dx = t.OriginX + r.X[i] - x, dz = t.OriginZ + r.Z[i] - z, dd = dx * dx + dz * dz;
                        if (dd < bd) { bd = dd; best = r.Name; }
                    }
                }
            }
        return best;
    }

    private static string Generic(MapPoiKind k) => k switch
    {
        MapPoiKind.Fuel => "a petrol station",
        MapPoiKind.Police => "a police station",
        MapPoiKind.Garage => "a garage",
        MapPoiKind.Hardware => "a hardware shop",
        MapPoiKind.Medical => "a pharmacy",
        MapPoiKind.Camp => "a camp site",
        _ => "a shop",
    };

    // ---- each frame -----------------------------------------------------------------------------------------------

    /// <summary>Takes in found leads, and near one sets down its stash and guards. <paramref name="at"/>: the player
    /// (local coordinates), null when not on foot or driving.</summary>
    public void Update(Vector3? at)
    {
        while (_found.TryDequeue(out var f))
        {
            _searching--;
            if (f.Lead is not { } lead || !_used.Add(lead.Key)) continue;
            lead.Id = ++_next;
            _all.Add(lead);
            Log.Info("Leads", $"lead {lead.Id} from a {f.Source}: {lead.Where} ({lead.Kind}), {lead.Gang}");
            Said?.Invoke(f.Source switch
            {
                "radio" => $"A radio on the body crackles: \"...{lead.Gang}, at {lead.Where}... keep the stash covered...\"  Marked on your map (M).",
                "stash" => $"A note in the stash: \"Rest of it at {lead.Where}, with {lead.Gang}.\"  Marked on your map (M).",
                _ => $"A scrap of paper in the crate: \"Stash at {lead.Where}. {Cap(lead.Gang)} are sitting on it.\"  Marked on your map (M).",
            });
        }
        if (at is not { } p) return;
        var (wx, _, wz) = WorldOrigin.ToWorld(p);
        foreach (var lead in _all)
        {
            double d = Math.Sqrt((lead.X - wx) * (lead.X - wx) + (lead.Z - wz) * (lead.Z - wz));
            if (lead.Stage == Stage.Taken) continue;
            if (d < Arrive && (lead.Stash == null || !IsInstanceValid(lead.Stash)))
            {
                lead.Stash = _caches.PlaceStash("lead:" + lead.Key, lead.X, lead.Z, Fill(), lead.Where);
                if (lead.Stash == null) continue;   // no ground collision there yet: next frame
                if (lead.Stage == Stage.Known) { lead.Stage = Stage.Guarded; lead.Guards = 3 + _rng.Next(3); }
                int n = _bandits.SpawnGuards(lead.Stash.GlobalPosition, lead.Guards, lead.Id);
                Log.Info("Leads", $"stash {lead.Id} set down at {d:0} m, {n} guards");
                if (n > 0) Said?.Invoke($"{Cap(lead.Gang)} are camped at {lead.Where}. The stash is there somewhere.");
            }
            else if (d < Arrive && lead.Stage == Stage.Guarded && lead.Guards > 0 && _bandits.GuardsOf(lead.Id) == 0)
            {
                // the guards were cleared away without being shot (you went down and came round): they're back
                _bandits.SpawnGuards(lead.Stash.GlobalPosition, lead.Guards, lead.Id);
            }
            else if (d > Forget && lead.Stash != null && IsInstanceValid(lead.Stash))
            {
                // walked away: the stash and its guards go back to being a promise (the guards that are left come back)
                _caches.Remove(lead.Stash);
                lead.Stash = null;
                _bandits.Dismiss(lead.Id);
            }
        }
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>A gang's stash: worth the fight.</summary>
    private Loot Fill()
    {
        int R(int lo, int hi) => _rng.Next(lo, hi + 1);
        return new Loot(R(1, 3), "tin of beans", R(1, 3), R(1, 2), new[] { ("ak", R(45, 75)), ("mosin", R(10, 20)), ("pistol", R(16, 32)) });
    }
}
