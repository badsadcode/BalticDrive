using System;
using System.Collections.Generic;
using System.Linq;
using BalticDrive.Core;
using BalticDrive.Map;
using BalticDrive.World;
using Godot;

namespace BalticDrive.Vehicle;

/// <summary>
/// Parked cars (D100, Tom: "more cars on the streets, on parking lots"; Project Zomboid's full car parks): generated
/// around the player in 64 m squares, always the same cars in the same bays. In a car park (an OSM paved area that isn't
/// pedestrian) the bays run in rows along the lot's longest side, 2.6 m wide with 6 m aisles; on residential, living and
/// service streets with houses near, cars stand at the kerb every 6.5 m or so. How full: the sandbox's
/// <c>parked.frequency</c>. Light: a model and a box to bump into, no physics; burnt-out shells among them. Walk within
/// 7 m of one and it wakes up as a real car (<see cref="WreckField.Adopt"/>): worn parts, sometimes a runner you can
/// drive away (D99).
/// </summary>
public sealed partial class ParkedCars : Node3D
{
    public const float Cell = 64f, Near = 230f, Far = 320f, WakeAt = 7f;
    private const int MaxCars = 140;

    /// <summary>Models and how often: the City cars, a few from the other packs, burnt shells (never woken).</summary>
    private static readonly (string Path, double Weight, bool Burnt)[] Models =
    {
        ("res://Assets/Synty/PolygonCity/Prefabs/Vehicles/SM_Veh_Car_Sedan_01.tscn", 4, false),
        ("res://Assets/Synty/PolygonCity/Prefabs/Vehicles/SM_Veh_Car_Small_01.tscn", 4, false),
        ("res://Assets/Synty/PolygonCity/Prefabs/Vehicles/SM_Veh_Car_Medium_01.tscn", 4, false),
        ("res://Assets/Synty/PolygonCity/Prefabs/Vehicles/SM_Veh_Car_Van_01.tscn", 1.5, false),
        ("res://Assets/Synty/PolygonCity/Prefabs/Vehicles/SM_Veh_Car_Muscle_01.tscn", 0.7, false),
        ("res://Assets/Synty/PolygonCity/Prefabs/Vehicles/SM_Veh_Car_Taxi_01.tscn", 0.8, false),
        ("res://Assets/Synty/PolygonCity/Prefabs/Vehicles/SM_Veh_Car_Police_01.tscn", 0.3, false),
        ("res://Assets/PolygonBattleRoyale/Prefabs/Vehicles/SM_Veh_Car_01.tscn", 2, false),
        ("res://Assets/Synty/PolygonGangWarfare/Prefabs/Vehicles/SM_Veh_Van_01.tscn", 1, false),
        ("res://Assets/Synty/PolygonGangWarfare/Prefabs/Vehicles/SM_Veh_LowCar_01.tscn", 0.8, false),
        ("res://Assets/Synty/PolygonGangWarfare/Prefabs/Vehicles/SM_Veh_LowCar_02.tscn", 0.8, false),
        ("res://Assets/PolygonBattleRoyale/Prefabs/Vehicles/SM_Veh_Car_Destroyed_01.tscn", 1.4, true),
        ("res://Assets/PolygonBattleRoyale/Prefabs/Vehicles/SM_Veh_Light_Car_Destroyed_01.tscn", 0.8, true),
        // trucks (D101), for truck spots only (weights after TruckFrom)
        ("res://Assets/Synty/PolygonGangWarfare/Prefabs/Vehicles/SM_Veh_Truck_01.tscn", 3, false),
        ("res://Assets/PolygonBattleRoyale/Prefabs/Vehicles/SM_Veh_Truck_01.tscn", 1.5, false),
        ("res://Assets/PolygonBattleRoyale/Prefabs/Vehicles/SM_Veh_Truck_Destroyed_01.tscn", 1, true),
    };
    /// <summary>Models from this index on are trucks.</summary>
    private const int TruckFrom = 13;

    /// <summary>Where trucks were placed (self-test log).</summary>
    public static readonly List<(double X, double Z, bool Yard)> TruckLog = new();

    private sealed record Spot(string Id, int Model, double X, double Z, float Yaw, float Grime, int Palette);

    /// <summary>Paint colours (D100) for Synty's one-blue cars: hue, × saturation, × brightness (car_paint.gdshader).
    /// The colours of a Polish street: white and silver most, then black, red, maroon, green, beige, yellow, navy, and
    /// the original blue.</summary>
    public static readonly (string Name, float Hue, float Sat, float Val, float Weight)[] Paints =
    {
        ("white", 0f, 0.04f, 1.9f, 4), ("silver", 0f, 0.05f, 1.25f, 4), ("black", 0f, 0.08f, 0.28f, 2),
        ("red", 0.0f, 1.0f, 0.95f, 1.5f), ("maroon", 0.985f, 0.85f, 0.55f, 1.2f), ("green", 0.38f, 0.55f, 0.55f, 1),
        ("beige", 0.11f, 0.32f, 1.35f, 1.2f), ("yellow", 0.13f, 0.85f, 1.25f, 0.6f), ("navy", 0.62f, 0.85f, 0.55f, 1),
        ("blue", 0.6f, 1f, 1f, 1.5f),
    };

    /// <summary>A paint by weight from a seed.</summary>
    public static int PickPaint(uint seed)
    {
        float total = 0; foreach (var p in Paints) total += p.Weight;
        float r = Hash.F01(Hash.Mix(seed)) * total;
        for (int i = 0; i < Paints.Length; i++) { if (r < Paints[i].Weight) return i; r -= Paints[i].Weight; }
        return 0;
    }

    private static Shader? _paintShader;
    private static readonly Dictionary<(ulong, int), ShaderMaterial> _paints = new();

    /// <summary>Repaints a car's body (paint <paramref name="i"/>): every textured surface but the glass, wheels and plates
    /// gets the paint shader over its own texture.</summary>
    public static void Repaint(Node root, int i)
    {
        if (i < 0 || i >= Paints.Length || Paints[i].Name == "blue") return;
        _paintShader ??= GD.Load<Shader>("res://Shaders/car_paint.gdshader");
        foreach (var mi in Meshes(root))
        {
            string n = mi.Name.ToString().ToLowerInvariant();
            if (n.Contains("glass") || n.Contains("plate") || mi.Mesh == null) continue;
            // a material set on the whole mesh wins over the surfaces' own: paint that one
            if (mi.MaterialOverride is BaseMaterial3D whole && whole.AlbedoTexture != null)
            {
                mi.MaterialOverride = PaintFor(whole, i);
                continue;
            }
            for (int k = 0; k < mi.Mesh.GetSurfaceCount(); k++)
            {
                if (mi.GetActiveMaterial(k) is not BaseMaterial3D bm || bm.AlbedoTexture == null) continue;
                mi.SetSurfaceOverrideMaterial(k, PaintFor(bm, i));
            }
        }
    }

    /// <summary>A node's meshes, itself included (a Synty prefab's root is the car body).</summary>
    public static IEnumerable<MeshInstance3D> Meshes(Node root) =>
        (root is MeshInstance3D self ? new[] { self } : Array.Empty<MeshInstance3D>())
        .Concat(root.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>());

    private static ShaderMaterial PaintFor(BaseMaterial3D bm, int i)
    {
        var paint = Paints[i];
        var key = (bm.AlbedoTexture.GetRid().Id, i);
        if (_paints.TryGetValue(key, out var sm)) return sm;
        sm = new ShaderMaterial { Shader = _paintShader };
        sm.SetShaderParameter("albedo_tex", bm.AlbedoTexture);
        sm.SetShaderParameter("hue", paint.Hue);
        sm.SetShaderParameter("sat", paint.Sat);
        sm.SetShaderParameter("val", paint.Val);
        _paints[key] = sm;
        return sm;
    }
    private sealed class Placed { public Spot S = null!; public Node3D Node = null!; }

    private readonly MapWorld _world;
    private readonly WreckField _wrecks;
    private readonly Dictionary<(long, long), List<Placed>> _cells = new();
    private readonly Dictionary<int, PackedScene?> _scenes = new();
    private ShaderMaterial? _grime;
    private double _next;
    private int _count;
    /// <summary>Cars waiting to be made: a few a frame, so a big car park doesn't stutter (D100).</summary>
    private readonly Queue<((long, long) Key, Spot S)> _pending = new();
    private const int PerFrame = 3;
    public int Count => _count;

    public ParkedCars(MapWorld world, WreckField wrecks) { _world = world; _wrecks = wrecks; Name = "ParkedCars"; }

    /// <summary>Builds squares near <paramref name="at"/> (two per call), drops far ones, and wakes the nearest parked car
    /// within reach of someone on foot.</summary>
    public void Update(Vector3 at, bool onFoot, double now)
    {
        if (onFoot) Wake(at);
        for (int i = 0; i < PerFrame && _pending.Count > 0; i++)
        {
            var (key, s) = _pending.Dequeue();
            if (!_cells.TryGetValue(key, out var cell) || _count >= MaxCars) continue;   // put away meanwhile
            if (Make(s) is { } node) { cell.Add(new Placed { S = s, Node = node }); _count++; }
        }
        if (now < _next) return;
        _next = now + 1;
        float freq = Sandbox.Get("parked.frequency");
        foreach (var key in _cells.Keys.ToList())
        {
            var c = WorldOrigin.ToLocal((key.Item1 + 0.5) * Cell, 0, (key.Item2 + 0.5) * Cell) - at;
            if (c.X * c.X + c.Z * c.Z < Far * Far) continue;
            foreach (var p in _cells[key]) { p.Node.QueueFree(); _count--; }
            _cells.Remove(key);
        }
        if (freq <= 0) return;
        var (wx, _, wz) = WorldOrigin.ToWorld(at);
        long x0 = (long)Math.Floor((wx - Near) / Cell), x1 = (long)Math.Floor((wx + Near) / Cell);
        long z0 = (long)Math.Floor((wz - Near) / Cell), z1 = (long)Math.Floor((wz + Near) / Cell);
        int made = 0;
        // nearest squares first
        var todo = new List<(long, long, double)>();
        for (long cz = z0; cz <= z1; cz++)
            for (long cx = x0; cx <= x1; cx++)
            {
                if (_cells.ContainsKey((cx, cz))) continue;
                double mx = (cx + 0.5) * Cell - wx, mz = (cz + 0.5) * Cell - wz;
                if (mx * mx + mz * mz < Near * Near) todo.Add((cx, cz, mx * mx + mz * mz));
            }
        foreach (var (cx, cz, _) in todo.OrderBy(t => t.Item3))
        {
            if (made >= 2 || _count >= MaxCars) break;
            _cells[(cx, cz)] = new List<Placed>();
            foreach (var s in Spots(cx, cz, freq))
                if (!_wrecks.Adopted(s.Id) && !_wrecks.TakenParked.Contains(s.Id)) _pending.Enqueue(((cx, cz), s));
            made++;
        }
        _wrecks.PutAwayAdopted(at, c => c == Main.Instance?.Car);
    }

    /// <summary>Where the nearest parked car that can wake stands (self-tests).</summary>
    public Vector3? Nearest(Vector3 at)
    {
        Vector3? best = null; float bd = float.MaxValue;
        foreach (var list in _cells.Values)
            foreach (var p in list)
                if (!Models[p.S.Model].Burnt && IsInstanceValid(p.Node) && p.Node.GlobalPosition.DistanceTo(at) is var d && d < bd) { bd = d; best = p.Node.GlobalPosition; }
        return best;
    }

    /// <summary>The nearest parked model within reach becomes a real car (not a burnt shell).</summary>
    private void Wake(Vector3 at)
    {
        Placed? best = null; (long, long) bestKey = default; float bd = WakeAt;
        foreach (var (key, list) in _cells)
            foreach (var p in list)
            {
                if (Models[p.S.Model].Burnt || !IsInstanceValid(p.Node)) continue;
                float d = p.Node.GlobalPosition.DistanceTo(at);
                if (d < bd) { bd = d; best = p; bestKey = key; }
            }
        if (best == null) return;
        _cells[bestKey].Remove(best);
        best.Node.QueueFree();
        _count--;
        var s = best.S;
        var car = _wrecks.Adopt(s.Id, Models[s.Model].Path, s.X, s.Z, s.Yaw, s.Grime);
        Repaint(car, s.Palette);
    }

    [ThreadStatic] private static MapContext? _ctx;

    /// <summary>The parked cars of one square: in its car parks' bays and at its streets' kerbs.</summary>
    private List<Spot> Spots(long cx, long cz, float freq)
    {
        var res = new List<Spot>();
        double ox = cx * Cell, oz = cz * Cell;
        var ctx = _ctx;
        if (ctx == null || ctx.World != _world) _ctx = ctx = new MapContext(_world);
        ctx.Reset(ox, oz, 0, 0, Cell, Cell, withPoints: false);
        var rng = new Random((int)Hash.Of((int)cx, (int)cz, 9157));
        int PickFrom(int from, int to)
        {
            double total = 0;
            for (int i = from; i < to; i++) total += Models[i].Weight;
            double r = rng.NextDouble() * total;
            for (int i = from; i < to; i++) { if (r < Models[i].Weight) return i; r -= Models[i].Weight; }
            return from;
        }
        bool Clear(float lx, float lz, float roadGap)
        {
            if (ctx.InBuilding(lx, lz, 1.8f) || !float.IsNaN(ctx.Water(lx, lz))) return false;
            if (ctx.UnderCanopy(lx, lz, 1.5f)) return false;   // the pumps' place (D124)
            foreach (var s in ctx.Segs)
                if (!s.Road.IsRail && !s.Road.IsTram && s.Distance(lx, lz, out _) < s.Hw + roadGap) return false;
            return true;
        }
        void Add(float lx, float lz, float yaw, string id, bool truck = false)
        {
            if (lx < 0 || lz < 0 || lx >= Cell || lz >= Cell) return;   // each car belongs to one square
            int model = truck ? PickFrom(TruckFrom, Models.Length) : PickFrom(0, TruckFrom);
            res.Add(new Spot(id, model, ox + lx, oz + lz, yaw, 0.15f + 0.55f * (float)rng.NextDouble(),
                Models[model].Burnt ? -1 : PickPaint(Hash.Of(id.GetHashCode(), 5))));
        }

        // car parks: rows of bays along the lot's longest side
        foreach (var jr in ctx.Junctions)
        {
            var j = jr.J;
            if (!j.Paved || j.Pedestrian || j.Elevated || j.Ghost) continue;
            int n = j.RingEnds.Length > 0 ? j.RingEnds[0] : j.X.Length;
            if (n < 3) continue;
            // the longest edge sets the rows
            float bl = 0, ux = 1, uz = 0;
            for (int i = 0; i < n; i++)
            {
                int k = (i + 1) % n;
                float dx = j.X[k] - j.X[i], dz = j.Z[k] - j.Z[i], l = MathF.Sqrt(dx * dx + dz * dz);
                if (l > bl) { bl = l; ux = dx / l; uz = dz / l; }
            }
            if (bl < 8f) continue;
            float vx = -uz, vz = ux;
            float umin = float.MaxValue, umax = float.MinValue, vmin = float.MaxValue, vmax = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float u = j.X[i] * ux + j.Z[i] * uz, v = j.X[i] * vx + j.Z[i] * vz;
                umin = MathF.Min(umin, u); umax = MathF.Max(umax, u); vmin = MathF.Min(vmin, v); vmax = MathF.Max(vmax, v);
            }
            // a lot this big is more likely a square or a carriageway area than a car park: only its edges
            float yawRow = MathF.Atan2(vx, vz);
            int row = 0;
            for (float v = vmin + 2.6f; v < vmax - 2.4f; v += row % 2 == 0 ? 5f : 6f, row++)
            {
                if (row % 2 == 1) continue;   // an aisle
                for (float u = umin + 1.5f; u < umax - 1.3f; u += 2.6f)
                {
                    float px = u * ux + v * vx, pz = u * uz + v * vz;       // lot-local (the junction's own frame)
                    bool inside = j.Contains(px - ux * 0.9f - vx * 2.1f, pz - uz * 0.9f - vz * 2.1f) && j.Contains(px + ux * 0.9f + vx * 2.1f, pz + uz * 0.9f + vz * 2.1f)
                        && j.Contains(px + ux * 0.9f - vx * 2.1f, pz + uz * 0.9f - vz * 2.1f) && j.Contains(px - ux * 0.9f + vx * 2.1f, pz - uz * 0.9f + vz * 2.1f);
                    if (!inside) continue;
                    float lx = jr.OffX + px, lz = jr.OffZ + pz;
                    // the same draw for the same bay, whatever square asks
                    var bay = new Random((int)Hash.Of((int)Math.Floor((ox + lx) * 2), (int)Math.Floor((oz + lz) * 2), 31));
                    if (bay.NextDouble() > 0.32 * freq) continue;
                    if (!Clear(lx, lz, 0.6f)) continue;
                    float yaw = yawRow + (bay.NextDouble() < 0.5 ? 0 : MathF.PI) + (float)(bay.NextDouble() - 0.5) * 0.12f;
                    Add(lx, lz, yaw, $"p{(long)Math.Floor((ox + lx) * 2)}_{(long)Math.Floor((oz + lz) * 2)}");
                }
            }
        }

        // trucks (D101): in industrial yards along service roads, and now and then abandoned on a main road's shoulder
        // out of town
        bool Industrial(float lx, float lz, float r)
        {
            foreach (var br in ctx.Buildings)
                if (br.B.Type == BuildingKind.Industrial && MathF.Abs(br.OffX + br.B.Cx - lx) < r + (br.B.MaxX - br.B.MinX) * 0.5f
                    && MathF.Abs(br.OffZ + br.B.Cz - lz) < r + (br.B.MaxZ - br.B.MinZ) * 0.5f) return true;
            return false;
        }
        foreach (var s in ctx.Segs)
        {
            bool yard = s.Road.Cls is RoadClass.Service or RoadClass.Unclassified;
            bool main = s.Road.Cls is RoadClass.Trunk or RoadClass.Primary or RoadClass.Secondary;
            if (!yard && !main || s.Road.IsBridge || s.Road.Layer != 0 || s.Road.IsRail || s.Road.IsTram) continue;
            float dx = s.Bx - s.Ax, dz = s.Bz - s.Az, len = MathF.Sqrt(dx * dx + dz * dz);
            if (len < 2f) continue;
            float nx = -dz / len, nz = dx / len;
            float step = yard ? 15f : 30f;
            // roads come in pieces of about 10 m: one candidate in the middle of each, its chance scaled by its length
            {
                float u = len * 0.5f;
                float side = Hash.F01(Hash.Of((int)Math.Floor(ox + s.Ax + dx * u / len), (int)Math.Floor(oz + s.Az + dz * u / len), 41)) < 0.5f ? -1f : 1f;
                float off = s.Hw + (yard ? 1.7f : 2.3f);
                float lx = s.Ax + dx * u / len + nx * off * side, lz = s.Az + dz * u / len + nz * off * side;
                var draw = new Random((int)Hash.Of((int)Math.Floor((ox + lx) * 2), (int)Math.Floor((oz + lz) * 2), 43));
                if (draw.NextDouble() > (yard ? 0.06 : 0.012) * freq * len / step) continue;
                if (yard ? !Industrial(lx, lz, 35f) : ctx.InBuilding(lx, lz, 40f)) continue;   // a yard by works; a main road out of town
                if (!Clear(lx, lz, 0.9f)) continue;
                float yaw = MathF.Atan2(-dx, -dz) + (side < 0 ? 0 : MathF.PI) + (float)(draw.NextDouble() - 0.5) * (yard ? 0.1f : 0.25f);
                Add(lx, lz, yaw, $"t{(long)Math.Floor((ox + lx) * 2)}_{(long)Math.Floor((oz + lz) * 2)}", truck: true);
                lock (TruckLog) if (TruckLog.Count < 100) TruckLog.Add((ox + lx, oz + lz, yard));
            }
        }

        // the kerb of residential streets with houses near
        foreach (var s in ctx.Segs)
        {
            if (s.Road.Cls is not (RoadClass.Residential or RoadClass.Living or RoadClass.Service) || s.Road.IsBridge || s.Road.Layer != 0) continue;
            float dx = s.Bx - s.Ax, dz = s.Bz - s.Az, len = MathF.Sqrt(dx * dx + dz * dz);
            if (len < 2f) continue;
            float nx = -dz / len, nz = dx / len;
            {
                float u = len * 0.5f;
                foreach (float side in new[] { -1f, 1f })
                {
                    float off = s.Hw + 1.15f;
                    float lx = s.Ax + dx * u / len + nx * off * side, lz = s.Az + dz * u / len + nz * off * side;
                    var kerb = new Random((int)Hash.Of((int)Math.Floor((ox + lx) * 2), (int)Math.Floor((oz + lz) * 2), 37));
                    if (kerb.NextDouble() > 0.11 * freq * len / 6.5f) continue;
                    if (!ctx.InBuilding(lx + nx * side * 12f, lz + nz * side * 12f, 6f)) continue;   // a street with houses on it
                    if (!Clear(lx, lz, 0.75f)) continue;
                    float yaw = MathF.Atan2(-dx, -dz) + (side < 0 ? 0 : MathF.PI) + (float)(kerb.NextDouble() - 0.5) * 0.06f;
                    Add(lx, lz, yaw, $"k{(long)Math.Floor((ox + lx) * 2)}_{(long)Math.Floor((oz + lz) * 2)}");
                }
            }
        }
        return res;
    }

    /// <summary>One parked car: its model on the ground, dusty, with a box to bump into.</summary>
    private Node3D? Make(Spot s)
    {
        var m = Models[s.Model];
        if (!_scenes.TryGetValue(s.Model, out var scene))
            _scenes[s.Model] = scene = ResourceLoader.Exists(m.Path) ? GD.Load<PackedScene>(m.Path) : null;
        if (scene == null) return null;
        var root = new Node3D { Name = $"Parked_{s.Id}" };
        AddChild(root);
        var model = scene.Instantiate<Node3D>();
        // the prefab's own detailed colliders go (on the ground layer); a box on the props layer stands in
        foreach (var co in model.FindChildren("*", "CollisionObject3D", true, false)) co.Free();
        root.AddChild(model);
        Repaint(model, s.Palette);
        float y = _world.Height(s.X, s.Z);
        root.GlobalTransform = new Transform3D(new Basis(Vector3.Up, s.Yaw), WorldOrigin.ToLocal(s.X, y + 0.02, s.Z));
        // the model's box: for the collider, and a light dusting
        var meshes = Meshes(model).ToList();
        Aabb box = default; bool first = true;
        _grime ??= new ShaderMaterial { Shader = GD.Load<Shader>("res://Shaders/wreck_grime.gdshader") };
        foreach (var mi in meshes)
        {
            var a = root.GlobalTransform.AffineInverse() * mi.GlobalTransform * mi.GetAabb();
            box = first ? a : box.Merge(a); first = false;
            string n = mi.Name.ToString().ToLowerInvariant();
            if (!m.Burnt && !n.Contains("glass") && !n.Contains("wheel") && !n.Contains("plate"))
            {
                var g = (ShaderMaterial)_grime.Duplicate();
                g.SetShaderParameter("amount", s.Grime);
                mi.MaterialOverlay = g;
            }
        }
        if (!first)
        {
            var body = new StaticBody3D { CollisionLayer = Bodies.Props, CollisionMask = 0 };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = box.Size * new Vector3(0.95f, 0.85f, 0.97f) }, Position = box.GetCenter() + Vector3.Up * box.Size.Y * 0.05f });
            root.AddChild(body);
        }
        return root;
    }
}
