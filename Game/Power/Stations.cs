using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BalticDrive.Core;
using BalticDrive.Map;
using BalticDrive.World;
using Godot;

namespace BalticDrive.Power;

/// <summary>
/// Petrol stations that pump (D124, Tom: "Petrol stations first"). Every station OSM maps (amenity=fuel) has pump
/// islands: in a row under its canopy when it has one (the canopy's long side, an island every ~6.5 m, up to four),
/// else one island beside the point. Under the forecourt are the station's tanks: a quarter drained already, the rest
/// a few hundred to a few thousand litres (the sandbox's "Petrol in found cars" scales it too). The grid is dead (D108),
/// so a pump works only with a running generator within <see cref="PowerReach"/> m: bring one, or find the one some
/// stations keep by the canopy (D109). Powered, F at a pump fills the car standing beside it, a litre and a half a
/// second until you press F again, walk off, the car is full, the tanks run dry or the power goes; with no car, F fills
/// an empty can from the pack. The litres taken are kept per world in <c>user://stations.json</c>.
/// </summary>
public sealed partial class Stations : Node3D
{
    public const float Near = 220f, Far = 300f, Reach = 1.9f, CarReach = 7f, PowerReach = 30f, Flow = 1.5f;
    /// <summary>Tom's pump (D126), modelled 4.32 m tall: scaled to about 2.3 m. Boxes stand in when it's missing.</summary>
    public const string ModelPath = "res://Assets/Props/GasPump/gas_pump.glb";
    public const float ModelScale = 0.535f;
    private static PackedScene? _model;
    private static bool _modelTried;
    private const string FilePath = "user://stations.json";

    public sealed class Pump
    {
        public Station Station = null!;
        public double X, Z;
        public float Yaw;
        public Node3D? Node;
        public MeshInstance3D[] Screens = Array.Empty<MeshInstance3D>();
    }

    public sealed class Station
    {
        public string Id = "", Name = "";
        public double X, Z;
        public float Litres;
        public bool Canopy, Powered;
        public Color Band;
        public readonly List<Pump> Pumps = new();
    }

    private readonly MapWorld _world;
    private readonly string _map;
    private readonly Dictionary<string, Station> _live = new();
    private Dictionary<string, float> _saved = new();
    private double _next, _nextPower;
    private bool _dirty;
    /// <summary>Is a running generator near this world spot (Main: the generators).</summary>
    public Func<double, double, bool>? PoweredAt;
    /// <summary>Self-tests: nothing more is written.</summary>
    public bool Frozen;

    public IEnumerable<Station> Live => _live.Values;

    public Stations(MapWorld world, string map) { _world = world; _map = map; Name = "Stations"; Load(); }

    public override void _Ready() => WorldOrigin.Shifted += OnShifted;
    public override void _ExitTree() { WorldOrigin.Shifted -= OnShifted; if (_dirty) Save(); }
    private void OnShifted(double dx, double dz)
    {
        var d = new Vector3((float)dx, 0, (float)dz);
        foreach (var p in _live.Values.SelectMany(s => s.Pumps)) if (p.Node != null) p.Node.GlobalPosition -= d;
    }

    // ---- where they are -----------------------------------------------------------------------------------------------

    public void Update(Vector3 at, double now)
    {
        if (now >= _nextPower)
        {
            _nextPower = now + 0.5;
            foreach (var s in _live.Values)
            {
                bool on = PoweredAt?.Invoke(s.X, s.Z) == true;
                if (on != s.Powered) { s.Powered = on; foreach (var p in s.Pumps) Light(p, on && s.Litres > 0); }
            }
        }
        if (now < _next) return;
        _next = now + 2;
        if (_dirty) { Save(); _dirty = false; }
        foreach (var (id, s) in _live.ToList())
        {
            var c = WorldOrigin.ToLocal(s.X, 0, s.Z) - at;
            if (c.X * c.X + c.Z * c.Z < Far * Far) continue;
            foreach (var p in s.Pumps) { p.Node?.QueueFree(); p.Node = null; }
            _live.Remove(id);
        }
        var (wx, _, wz) = WorldOrigin.ToWorld(at);
        int tx0 = (int)Math.Floor((wx - Near) / MapTile.Size), tx1 = (int)Math.Floor((wx + Near) / MapTile.Size);
        int tz0 = (int)Math.Floor((wz - Near) / MapTile.Size), tz1 = (int)Math.Floor((wz + Near) / MapTile.Size);
        for (int tx = tx0; tx <= tx1; tx++)
            for (int tz = tz0; tz <= tz1; tz++)
            {
                if (!_world.Data.HasTile(tx, tz)) continue;
                var tile = _world.Data.Tile(tx, tz);
                foreach (var poi in tile.Pois)
                {
                    if (poi.Kind != MapPoiKind.Fuel) continue;
                    double px = tx * (double)MapTile.Size + poi.X, pz = tz * (double)MapTile.Size + poi.Z;
                    if ((px - wx) * (px - wx) + (pz - wz) * (pz - wz) > Near * Near) continue;
                    string id = string.IsNullOrEmpty(poi.Id) ? $"{tx}.{tz}.{poi.X:0}.{poi.Z:0}" : poi.Id;
                    if (_live.ContainsKey(id)) continue;
                    var s = Lay(id, poi.Name, px, pz);
                    _live[id] = s;
                    foreach (var p in s.Pumps) Build(p);
                    Log.Info("Stations", $"{(s.Name.Length > 0 ? s.Name : id)}: {s.Pumps.Count} pumps{(s.Canopy ? " under the canopy" : "")}, {s.Litres:0} L below");
                }
            }
    }

    [ThreadStatic] private static MapContext? _ctx;

    /// <summary>A station's pumps and tanks, from its id (the same every time).</summary>
    private Station Lay(string id, string name, double px, double pz)
    {
        var rng = new Random(StableHash(id));
        var s = new Station { Id = id, Name = name, X = px, Z = pz, Band = Bands[rng.Next(Bands.Length)] };
        double full = rng.NextDouble() < 0.25 ? 0 : 300 + 4700 * Math.Pow(rng.NextDouble(), 1.5);
        s.Litres = _saved.TryGetValue(id, out var kept) ? kept : (float)Math.Round(full * Sandbox.Get("fuel.found"));
        var ctx = _ctx;
        if (ctx == null || ctx.World != _world) _ctx = ctx = new MapContext(_world);
        const float R = 50f;
        ctx.Reset(px - R, pz - R, 0, 0, 2 * R, 2 * R, withPoints: false);
        // the canopy nearest the point, within 45 m
        MapBuildingRef? best = null; float bd = 45f;
        foreach (var br in ctx.Buildings)
        {
            if (!br.B.IsCanopy) continue;
            float d = MathF.Sqrt((br.OffX + br.B.Cx - R) * (br.OffX + br.B.Cx - R) + (br.OffZ + br.B.Cz - R) * (br.OffZ + br.B.Cz - R));
            if (d < bd) { bd = d; best = br; }
        }
        if (best is { } cb)
        {
            // along the canopy's long side: its longest edge gives the direction
            var b = cb.B;
            float[] rx = b.RX[0], rz = b.RZ[0];
            float ux = 1, uz = 0, longest = 0;
            for (int i = 0; i < rx.Length; i++)
            {
                int j = (i + 1) % rx.Length;
                float dx = rx[j] - rx[i], dz = rz[j] - rz[i], l = MathF.Sqrt(dx * dx + dz * dz);
                if (l > longest) { longest = l; ux = dx / l; uz = dz / l; }
            }
            float cx = cb.OffX + b.Cx, cz = cb.OffZ + b.Cz, lo = 0, hi = 0;
            for (int i = 0; i < rx.Length; i++)
            {
                float t = (cb.OffX + rx[i] - cx) * ux + (cb.OffZ + rz[i] - cz) * uz;
                lo = MathF.Min(lo, t); hi = MathF.Max(hi, t);
            }
            float span = (hi - lo) * 0.85f;
            int n = Math.Clamp((int)MathF.Round(span / 6f), 1, 4);
            float step = n > 1 ? span / n : 0, mid = (lo + hi) / 2;
            // an island runs across the canopy (cars stand either side of it): its long axis is the canopy's short one
            float yaw = MathF.Atan2(-uz, ux);   // local Z → (-uz, ux): across the canopy
            for (int i = 0; i < n; i++)
            {
                float t = mid + (i - (n - 1) / 2f) * step;
                s.Pumps.Add(new Pump { Station = s, X = ctx.Ox + cx + ux * t, Z = ctx.Oz + cz + uz * t, Yaw = yaw });
            }
            s.X = ctx.Ox + cx; s.Z = ctx.Oz + cz;
            s.Canopy = true;
        }
        else
        {
            // no canopy mapped: one island beside the point, clear of buildings and the road
            for (int tries = 0; tries < 12; tries++)
            {
                double a = rng.NextDouble() * Math.Tau, d = 6 + rng.NextDouble() * 8;
                float lx = R + (float)(Math.Cos(a) * d), lz = R + (float)(Math.Sin(a) * d);
                if (ctx.InBuilding(lx, lz, 2.5f) || !float.IsNaN(ctx.Water(lx, lz))) continue;
                bool road = false;
                foreach (var sg in ctx.Segs) if (sg.Distance(lx, lz, out _) < sg.Hw + 2f) { road = true; break; }
                if (road) continue;
                s.Pumps.Add(new Pump { Station = s, X = ctx.Ox + lx, Z = ctx.Oz + lz, Yaw = (float)a });
                s.X = ctx.Ox + lx; s.Z = ctx.Oz + lz;
                break;
            }
        }
        return s;
    }

    private static int StableHash(string s) { unchecked { int h = 17; foreach (char ch in s) h = h * 31 + ch; return h; } }

    // ---- using them ---------------------------------------------------------------------------------------------------

    /// <summary>The pump within reach of someone standing there, or null.</summary>
    public Pump? InReach(Vector3 at)
    {
        Pump? best = null; float bd = Reach;
        foreach (var p in _live.Values.SelectMany(s => s.Pumps))
            if (p.Node != null && (p.Node.GlobalPosition - at with { Y = p.Node.GlobalPosition.Y }).Length() is var d && d < bd) { bd = d; best = p; }
        return best;
    }

    /// <summary>A pump that works: power and petrol.</summary>
    public static bool Works(Pump p) => p.Station.Powered && p.Station.Litres > 0;

    /// <summary>Petrol out of the station's tanks; returns what came out.</summary>
    public float Draw(Station s, float litres)
    {
        float took = MathF.Max(0, MathF.Min(litres, s.Litres));
        s.Litres -= took;
        _saved[s.Id] = s.Litres; _dirty = true;
        if (s.Litres <= 0) foreach (var p in s.Pumps) Light(p, false);
        return took;
    }

    /// <summary>What the pump's prompt says: why it won't, or what F does.</summary>
    public static string Prompt(Pump p, Vehicle.Car? car, int emptyCans)
    {
        var s = p.Station;
        if (s.Litres <= 0) return Core.Lang.T("p.pump_dry");
        if (!s.Powered) return Core.Lang.T("p.pump_nopower");
        string below = s.Litres.ToString("0");
        if (car != null) return Core.Lang.T("p.pump_car", car.Fuel.ToString("0.0"), car.Tank.ToString("0"), below);
        if (emptyCans > 0) return Core.Lang.T("p.pump_can", emptyCans, below);
        return Core.Lang.T("p.pump_nothing", below);
    }

    // ---- filling a car: F starts it, then a litre and a half a second ----------------------------------------------------

    public (Pump Pump, Vehicle.Car Car, float Litres)? Filling { get; private set; }

    public void StartFilling(Pump p, Vehicle.Car car)
    {
        Filling = (p, car, 0);
        Log.Info("Stations", $"filling {car.Name} at {p.Station.Id}: {car.Fuel:0.0} of {car.Tank:0} L");
    }

    /// <summary>Stops filling; the note to show.</summary>
    public string StopFilling(string why = "")
    {
        if (Filling is not { } f) return "";
        Filling = null;
        Log.Info("Stations", $"stopped filling: {f.Litres:0.0} L in, {f.Car.Fuel:0.0} of {f.Car.Tank:0} L{(why.Length > 0 ? $" ({why})" : "")}");
        return Core.Lang.T(why switch
        {
            "full" => "n.pump_full", "dry" => "n.pump_ran_dry", "power" => "n.pump_power_lost", _ => "n.pump_stopped",
        }, f.Litres.ToString("0.0"), f.Car.Fuel.ToString("0.0"));
    }

    /// <summary>Each frame while filling: the petrol flows; returns a note when it stops by itself (else null).</summary>
    public string? Tick(Vector3 walker, float dt)
    {
        if (Filling is not { } f) return null;
        if (!IsInstanceValid(f.Car) || f.Pump.Node == null) return StopFilling();
        if ((f.Pump.Node.GlobalPosition - walker with { Y = f.Pump.Node.GlobalPosition.Y }).Length() > Reach * 2f
            || f.Car.GlobalPosition.DistanceTo(f.Pump.Node.GlobalPosition) > CarReach + 1) return StopFilling();
        if (!f.Pump.Station.Powered) return StopFilling("power");
        if (f.Pump.Station.Litres <= 0) return StopFilling("dry");
        if (f.Car.Fuel >= f.Car.Tank - 0.05f) return StopFilling("full");
        float want = MathF.Min(Flow * dt, f.Car.Tank - f.Car.Fuel);
        float got = Draw(f.Pump.Station, want);
        f.Car.Fuel += got;
        Filling = (f.Pump, f.Car, f.Litres + got);
        return null;
    }

    // ---- looks: a kerbed island, a dispenser with the station's colour, screens that light up when powered ---------------

    private static readonly Color[] Bands =
    {
        new(0.75f, 0.12f, 0.08f), new(0.1f, 0.3f, 0.6f), new(0.12f, 0.48f, 0.2f), new(0.85f, 0.62f, 0.08f), new(0.5f, 0.5f, 0.48f),
    };
    private static StandardMaterial3D? _kerb, _body, _dark, _black, _yellow, _lit;
    private static readonly Dictionary<Color, StandardMaterial3D> _bandMats = new();
    private static readonly BoxMesh _box = new() { Size = Vector3.One };

    private void Build(Pump p)
    {
        _kerb ??= Mat(new Color(.62f, .61f, .58f)); _body ??= Mat(new Color(.86f, .86f, .84f)); _dark ??= Mat(new Color(.05f, .06f, .07f));
        _black ??= Mat(new Color(.08f, .08f, .08f)); _yellow ??= Mat(new Color(.9f, .72f, .1f));
        if (!_bandMats.TryGetValue(p.Station.Band, out var band)) _bandMats[p.Station.Band] = band = Mat(p.Station.Band);
        var root = new Node3D { Name = "Pump" };
        AddChild(root);
        MeshInstance3D Part(float x, float y, float z, float sx, float sy, float sz, Material m)
        {
            var mi = new MeshInstance3D { Mesh = _box, MaterialOverride = m, Position = new Vector3(x, y, z), Scale = new Vector3(sx, sy, sz) };
            root.AddChild(mi);
            return mi;
        }
        // local: the island runs along Z; the dispenser faces ±X, where the cars stand
        Part(0, 0.09f, 0, 1.3f, 0.18f, 4.2f, _kerb);
        var screens = new List<MeshInstance3D>();
        if (!_modelTried) { _modelTried = true; _model = ResourceLoader.Exists(ModelPath) ? GD.Load<PackedScene>(ModelPath) : null; }
        if (_model?.Instantiate<Node3D>() is { } model)
        {
            // Tom's pump (D126) on the island; screens on its two faces light up with power
            model.Scale = Vector3.One * ModelScale;
            model.Position = new Vector3(0, 0.18f, 0);
            root.AddChild(model);
            // over the screen Tom modelled on its +X face (model 0.42 m out, 2.6-3.36 m up, 1.16 m wide)
            screens.Add(Part(0.43f * ModelScale + 0.004f, 0.18f + 2.98f * ModelScale, 0, 0.012f, 0.76f * ModelScale, 1.16f * ModelScale, _dark));
        }
        else
        {
            Part(0, 1.0f, 0, 0.5f, 1.65f, 1.0f, _body);
            Part(0, 1.93f, 0, 0.56f, 0.24f, 1.06f, band);
            foreach (float side in new[] { -1f, 1f })
            {
                screens.Add(Part(side * 0.255f, 1.42f, 0, 0.02f, 0.28f, 0.55f, _dark));
                Part(side * 0.27f, 0.95f, -0.3f, 0.06f, 0.22f, 0.1f, _black);   // the nozzles in their holsters
                Part(side * 0.27f, 0.95f, 0.3f, 0.06f, 0.22f, 0.1f, _black);
                Part(side * 0.3f, 0.55f, -0.3f, 0.03f, 0.6f, 0.03f, _black);   // their hoses hanging down
            }
        }
        Part(0, 0.62f, -1.95f, 0.16f, 0.9f, 0.16f, _yellow);   // bollards at the ends
        Part(0, 0.62f, 1.95f, 0.16f, 0.9f, 0.16f, _yellow);
        p.Screens = screens.ToArray();
        var body = new StaticBody3D { CollisionLayer = Bodies.Props, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1.3f, 0.3f, 4.2f) }, Position = new Vector3(0, 0.15f, 0) });
        body.AddChild(new CollisionShape3D { Shape = _model != null ? new BoxShape3D { Size = new Vector3(0.8f, 2.2f, 1.45f) } : new BoxShape3D { Size = new Vector3(0.6f, 2f, 1.1f) }, Position = new Vector3(0, _model != null ? 1.28f : 1.05f, 0) });
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.2f, 1.1f, 4.1f) }, Position = new Vector3(0, 0.6f, 0) });   // the bollards
        root.AddChild(body);
        float y = _world.Height(p.X, p.Z);
        root.GlobalTransform = new Transform3D(new Basis(Vector3.Up, p.Yaw), WorldOrigin.ToLocal(p.X, y, p.Z));
        p.Node = root;
        Light(p, p.Station.Powered && p.Station.Litres > 0);
    }

    private static void Light(Pump p, bool on)
    {
        _lit ??= new StandardMaterial3D { AlbedoColor = new Color(0.05f, 0.25f, 0.12f), EmissionEnabled = true, Emission = new Color(0.2f, 0.85f, 0.4f), EmissionEnergyMultiplier = 0.9f };
        foreach (var s in p.Screens) if (IsInstanceValid(s)) s.MaterialOverride = on ? _lit : _dark;
    }

    private static StandardMaterial3D Mat(Color c) => new() { AlbedoColor = c, Roughness = 0.7f };

    // ---- kept ---------------------------------------------------------------------------------------------------------

    private void Load()
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            if (!File.Exists(path)) return;
            var all = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, float>>>(File.ReadAllText(path));
            if (all != null && all.TryGetValue(_map, out var mine)) _saved = mine;
        }
        catch (Exception ex) { Log.Error("Stations", ex.Message); }
    }

    public void Save()
    {
        if (Frozen) return;
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            var all = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, float>>>(File.ReadAllText(path)) ?? new() : new();
            all[_map] = _saved;
            File.WriteAllText(path, JsonSerializer.Serialize(all));
        }
        catch (Exception ex) { Log.Error("Stations", ex.Message); }
    }
}
