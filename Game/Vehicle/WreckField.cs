using System;
using System.Collections.Generic;
using System.Linq;
using BalticDrive.Core;
using BalticDrive.Map;
using BalticDrive.World;
using Godot;

namespace BalticDrive.Vehicle;

/// <summary>
/// Abandoned cars to strip for parts (D69, step 3 of the workshop; Tom: parts from wrecks). Generated around the player
/// like the crates: the ground is cut into 48 m squares, and about one square in eight has a wreck, always the same
/// one (by the square). It stands in a car park (OSM paved areas) or at the side of a residential, service or minor
/// road, never on the carriageway or in a building. Its parts are worn (most 10–80 %) and some are gone, picked over
/// before you came. Wrecks are parked (frozen, no physics) and you work on them in the workshop (hold F). What you take
/// stays taken for the session. At most eight near you; far ones are put away.
/// </summary>
public sealed partial class WreckField : Node3D
{
    public const float Cell = 48f, Near = 170f, Far = 260f;
    private const int MaxLive = 8;
    private static readonly string[] Models =
    {
        "SM_Veh_Car_Sedan_01", "SM_Veh_Car_Small_01", "SM_Veh_Car_Medium_01", "SM_Veh_Car_Muscle_01", "SM_Veh_Car_Van_01",
        "SM_Veh_Car_Taxi_01", "SM_Veh_Car_Police_01", "SM_Veh_Car_Ambo_01",
    };
    private readonly MapWorld _world;
    private readonly Dictionary<(long, long), Car> _live = new();
    /// <summary>The parts of every wreck met this session, kept when it's put away.</summary>
    private readonly Dictionary<(long, long), CarParts> _seen = new();
    private readonly HashSet<(long, long)> _empty = new();
    /// <summary>Squares whose wreck the player drove away (D99): no new one there this session.</summary>
    private readonly HashSet<(long, long)> _taken = new();
    /// <summary>Cars the player left behind (D99): kept where they stand, never put away, still in <see cref="Live"/>.</summary>
    private readonly List<Car> _left = new();
    /// <summary>Parked cars driven away (D100): their bays stay empty this session.</summary>
    public readonly HashSet<string> TakenParked = new();
    private double _next;
    public IEnumerable<Car> Live => _live.Values.Concat(_left.Where(IsInstanceValid)).Concat(_adopted.Values.Where(IsInstanceValid)).Distinct();

    /// <summary>The player drives this wreck away (D99): it stops being a wreck (no more putting away) and its square
    /// stays empty.</summary>
    public void Take(Car car)
    {
        foreach (var kv in _live.Where(kv => kv.Value == car).ToList()) { _live.Remove(kv.Key); _taken.Add(kv.Key); }
        foreach (var kv in _adopted.Where(kv => kv.Value == car).ToList()) { _adopted.Remove(kv.Key); TakenParked.Add(kv.Key); }
        _left.Remove(car);
    }

    /// <summary>A car the player left: parked where it stands for the rest of the session, still yours to come back to.</summary>
    public void Leave(Car car) { if (!_left.Contains(car)) _left.Add(car); }

    public WreckField(MapWorld world) { _world = world; Name = "Wrecks"; }

    public void Update(Vector3 at, double now)
    {
        if (now < _next) return;
        _next = now + 2;
        foreach (var key in _live.Where(kv => kv.Value.GlobalPosition.DistanceTo(at) > Far).Select(kv => kv.Key).ToList())
        {
            _live[key].QueueFree();
            _live.Remove(key);
        }
        var (wx, _, wz) = WorldOrigin.ToWorld(at);
        long x0 = (long)Math.Floor((wx - Near) / Cell), x1 = (long)Math.Floor((wx + Near) / Cell);
        long z0 = (long)Math.Floor((wz - Near) / Cell), z1 = (long)Math.Floor((wz + Near) / Cell);
        int made = 0;
        for (long cz = z0; cz <= z1 && made < 2; cz++)
            for (long cx = x0; cx <= x1 && made < 2; cx++)
            {
                var key = (cx, cz);
                if (_live.ContainsKey(key) || _empty.Contains(key) || _live.Count >= MaxLive) continue;
                double mx = (cx + 0.5) * Cell - wx, mz = (cz + 0.5) * Cell - wz;
                if (mx * mx + mz * mz > Near * Near) continue;
                var rng = new Random((int)Hash.Of((int)cx, (int)cz, 4711));
                if (_taken.Contains(key) || rng.NextDouble() > 0.13 * Sandbox.Get("wrecks.frequency") || Spot(cx, cz, rng) is not { } spot) { _empty.Add(key); continue; }
                Spawn(key, spot, rng);
                made++;
            }
    }

    [ThreadStatic] private static MapContext? _ctx;

    /// <summary>A place for a wreck in this square: in a car park, else at the side of a minor road, facing along it.</summary>
    private (double X, double Z, float Yaw)? Spot(long cx, long cz, Random rng)
    {
        double ox = cx * Cell, oz = cz * Cell;
        var ctx = _ctx;
        if (ctx == null || ctx.World != _world) _ctx = ctx = new MapContext(_world);
        ctx.Reset(ox, oz, 0, 0, Cell, Cell, withPoints: false);
        bool Free(float lx, float lz)
        {
            if (lx < 0 || lz < 0 || lx > Cell || lz > Cell) return false;
            if (ctx.InBuilding(lx, lz, 2.5f) || !float.IsNaN(ctx.Water(lx, lz))) return false;
            foreach (var s in ctx.Segs)
                if (s.Distance(lx, lz, out _) < s.Hw + 1.1f) return false;   // off every carriageway
            foreach (var jr in ctx.Junctions)
                if (!jr.J.Paved && !jr.J.Pedestrian && jr.J.Contains(lx - jr.OffX, lz - jr.OffZ)) return false;
            return true;
        }
        // a car park
        foreach (var jr in ctx.Junctions)
        {
            if (!jr.J.Paved || jr.J.Pedestrian || jr.J.Elevated) continue;
            for (int t = 0; t < 12; t++)
            {
                float lx = jr.OffX + jr.J.MinX + (float)rng.NextDouble() * (jr.J.MaxX - jr.J.MinX);
                float lz = jr.OffZ + jr.J.MinZ + (float)rng.NextDouble() * (jr.J.MaxZ - jr.J.MinZ);
                if (!jr.J.Contains(lx - jr.OffX, lz - jr.OffZ) || !Free(lx, lz)) continue;
                return (ox + lx, oz + lz, (float)(rng.Next(4) * Math.PI / 2 + rng.NextDouble() * 0.3));
            }
        }
        // the side of a minor road
        var minor = ctx.Segs.Where(s => s.Road.Cls is RoadClass.Residential or RoadClass.Service or RoadClass.Unclassified or RoadClass.Track or RoadClass.Living
            && !s.Road.IsBridge && s.Road.Layer == 0).ToList();
        for (int t = 0; t < 10 && minor.Count > 0; t++)
        {
            var s = minor[rng.Next(minor.Count)];
            float u = 0.2f + 0.6f * (float)rng.NextDouble();
            float dx = s.Bx - s.Ax, dz = s.Bz - s.Az, len = MathF.Sqrt(dx * dx + dz * dz);
            if (len < 6f) continue;
            float side = rng.NextDouble() < 0.5 ? -1 : 1;
            float off = s.Hw + 1.6f;
            float lx = s.Ax + dx * u + -dz / len * off * side, lz = s.Az + dz * u + dx / len * off * side;
            if (!Free(lx, lz)) continue;
            float yaw = MathF.Atan2(-dx, -dz) + (rng.NextDouble() < 0.5 ? 0 : MathF.PI) + (float)(rng.NextDouble() - 0.5) * 0.25f;
            return (ox + lx, oz + lz, yaw);
        }
        return null;
    }

    private void Spawn((long, long) key, (double X, double Z, float Yaw) spot, Random rng)
    {
        string model = $"res://Assets/Synty/PolygonCity/Prefabs/Vehicles/{Models[rng.Next(Models.Length)]}.tscn";
        var car = new Car { Name = $"Wreck{key.Item1}_{key.Item2}", Model = Car.ChooseModel(model), Map = _world, Parked = true, Driving = false };
        AddChild(car);
        if (_seen.TryGetValue(key, out var kept)) { car.Parts.CopyFrom(kept); _seen[key] = car.Parts; }
        else
        {
            RollParts(car, rng, 1f);
            _seen[key] = car.Parts;
        }
        Place(car, spot.X, spot.Z, spot.Yaw);
        car.Grime(0.45f + 0.4f * (float)rng.NextDouble());
        _live[key] = car;
        Log.Info("Wrecks", $"a {model.Split('/')[^1].Replace(".tscn", "")} at {spot.X:0},{spot.Z:0}: {car.Parts.All.Count(p => p.Missing)} parts missing");
    }

    /// <summary>
    /// An abandoned car's parts (D69, D99): worn, some gone (what scavengers want first most often), windows now and then
    /// smashed; a runner (the sandbox's share, × <paramref name="runners"/>) keeps engine, battery and wheels fit to drive.
    /// </summary>
    public static void RollParts(Car car, Random rng, float runners)
    {
        {
            bool runner = rng.NextDouble() < Sandbox.Get("wrecks.runners") * runners;
            float wear = Sandbox.Get("wrecks.condition"), picked = Sandbox.Get("wrecks.picked_over");
            foreach (var p in car.Parts.All)
            {
                // worn, some gone: what scavengers want first is missing most often
                double gone = p.Kind switch
                {
                    PartKind.Battery => 0.45, PartKind.Tyre => 0.25, PartKind.Engine => 0.15, PartKind.Seat => 0.1,
                    PartKind.Windshield or PartKind.Window => 0.0, _ => 0.08,
                };
                p.Condition = (float)Math.Min(100, Math.Round((5 + 80 * Math.Pow(rng.NextDouble(), 1.3)) * wear));
                if (p.Kind is PartKind.Windshield or PartKind.Window && rng.NextDouble() < 0.3) p.Condition = 0;   // smashed
                p.Missing = rng.NextDouble() < gone * picked;
                if (runner && p.Kind is PartKind.Engine or PartKind.Battery or PartKind.Tyre)
                {
                    p.Missing = false;
                    p.Condition = Math.Max(p.Condition, (float)Math.Round(35 + 45 * rng.NextDouble()));
                }
            }
            // petrol (D123): a third bone dry (siphoned or run out), the rest a little, mostly near empty; one that still
            // runs has enough to get somewhere. The sandbox's "Petrol in found cars" scales it.
            double level = rng.NextDouble() < 0.33 ? 0 : 0.02 + 0.33 * Math.Pow(rng.NextDouble(), 2);
            if (runner) level = Math.Max(level, 0.08 + 0.2 * rng.NextDouble());
            car.Parts.Fuel = (float)Math.Round(Math.Min(1, level * Sandbox.Get("fuel.found")) * car.Spec.Tank, 1);
        }
    }

    /// <summary>Stands a parked car at a world spot, sagging onto the corners that have no wheel.</summary>
    private void Place(Car car, double x, double z, float yaw)
    {
        float y = _world.Height(x, z);
        // sagging onto the corners with no wheel: nose or tail down, leaning to the side
        float pitch = 0, roll = 0, sink = 0;
        for (int i = 0; i < 4; i++)
            if (car.Parts.Tyre(i).Missing)
            {
                pitch += i < 2 ? -0.045f : 0.045f;   // front gone: nose down
                roll += i % 2 == 0 ? 0.035f : -0.035f;
                sink += 0.07f;
            }
        var basis = new Basis(Vector3.Up, yaw) * new Basis(Vector3.Right, pitch) * new Basis(Vector3.Back, roll);
        car.Warp(new Transform3D(basis, WorldOrigin.ToLocal(x, y + 0.05 - sink, z)));
        car.PartsChanged();
    }

    // ---- parked cars woken up (D100) -----------------------------------------------------------------------------
    private readonly Dictionary<string, Car> _adopted = new();
    private readonly Dictionary<string, CarParts> _seenParked = new();
    public bool Adopted(string id) => _adopted.ContainsKey(id);

    /// <summary>A parked car (D100) the player walked up to becomes a real one: its parts rolled from its id (the same
    /// every time; kept for the session once met), standing where the parked model stood.</summary>
    public Car Adopt(string id, string model, double x, double z, float yaw, float grime)
    {
        var car = new Car { Name = $"Parked_{id}", Model = Car.ChooseModel(model), Map = _world, Parked = true, Driving = false };
        AddChild(car);
        if (_seenParked.TryGetValue(id, out var kept)) car.Parts.CopyFrom(kept);
        else RollParts(car, new Random((int)Hash.Of(id.GetHashCode(), 77, 4711)), 1f);
        _seenParked[id] = car.Parts;
        Place(car, x, z, yaw);
        car.Grime(grime);
        _adopted[id] = car;
        return car;
    }

    /// <summary>Adopted cars far away go back to being parked models (their parts remembered); the one being driven and
    /// the ones left (<see cref="Leave"/>) stay.</summary>
    public void PutAwayAdopted(Vector3 at, Func<Car, bool> keep)
    {
        foreach (var (id, car) in _adopted.ToList())
        {
            if (!IsInstanceValid(car)) { _adopted.Remove(id); continue; }
            if (keep(car) || _left.Contains(car) || car.GlobalPosition.DistanceTo(at) < Far) continue;
            _adopted.Remove(id);
            car.QueueFree();
        }
    }
}
