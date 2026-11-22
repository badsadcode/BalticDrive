using System;
using System.Collections.Generic;
using System.Linq;
using BalticDrive.Core;
using BalticDrive.Map;
using BalticDrive.World;
using Godot;

namespace BalticDrive.Power;

/// <summary>
/// Wells in village yards (D122, Tom: "Yes, add village wells"): OSM maps few wells (79 in all Podlaskie), while real
/// villages have one in half the yards, so the game adds its own, like the generators (D109): beside rural houses (about
/// one in six) and farms (one in three), seldom by a town house, never among blocks of flats; three in four are bucket
/// wells, the rest hand pumps. Placed from the building's OSM id, so always the same ones; a few metres out from a wall,
/// not on a road or in water. Built around you in 64 m squares; F fills a bottle with clean water (see
/// <see cref="Wells"/>). Coherent over exact (Tom's rule): believable, not surveyed.
/// </summary>
public sealed partial class VillageWells : Node3D
{
    public const float Cell = 64f, Near = 160f, Far = 220f;

    public sealed class Well
    {
        public double X, Z;
        public float Yaw;
        public bool Pump;
        public Node3D? Node;
    }

    private readonly MapWorld _world;
    private readonly Dictionary<(long, long), List<Well>> _cells = new();
    private double _next;
    [ThreadStatic] private static MapContext? _ctx;

    public VillageWells(MapWorld world) { _world = world; Name = "VillageWells"; }

    public override void _Ready() => WorldOrigin.Shifted += OnShifted;
    public override void _ExitTree() => WorldOrigin.Shifted -= OnShifted;
    private void OnShifted(double dx, double dz)
    {
        var d = new Vector3((float)dx, 0, (float)dz);
        foreach (var w in _cells.Values.SelectMany(l => l)) if (w.Node != null) w.Node.GlobalPosition -= d;
    }

    public IEnumerable<Well> Live => _cells.Values.SelectMany(l => l);

    /// <summary>The wells of one square: by the village houses and farms in it.</summary>
    private List<Well> Spots(long cx, long cz)
    {
        var res = new List<Well>();
        double ox = cx * Cell, oz = cz * Cell;
        var ctx = _ctx;
        if (ctx == null || ctx.World != _world) _ctx = ctx = new MapContext(_world);
        ctx.Reset(ox - 64, oz - 64, 0, 0, Cell + 128, Cell + 128, withPoints: false);
        // a village, not a town: no blocks of flats near, and not too many houses packed in
        int flats = ctx.Buildings.Count(b => b.B.Type == BuildingKind.Apartments);
        // a small plain "building" in the countryside is a house too (OSM maps most village houses that way: D104)
        static bool Home(MapBuilding b) => b.Type == BuildingKind.House || b.Type == BuildingKind.Generic && (b.MaxX - b.MinX) * (b.MaxZ - b.MinZ) < 240f;
        int houses = ctx.Buildings.Count(b => Home(b.B));
        if (flats > 0) return res;
        bool town = houses > 45;   // in the 192 m square around: a town's grid of houses
        foreach (var br in ctx.Buildings)
        {
            var b = br.B;
            float bx = br.OffX + b.Cx - 64, bz = br.OffZ + b.Cz - 64;   // in this square's frame
            if (bx < 0 || bz < 0 || bx >= Cell || bz >= Cell) continue;
            double chance = b.Type == BuildingKind.Farm ? 0.33 : Home(b) ? (town ? 0.03 : 0.17) : 0;
            if (chance <= 0) continue;
            long key = b.OsmId != 0 ? b.OsmId : (long)Hash.Of((int)Math.Floor(ox + bx), (int)Math.Floor(oz + bz), 5);
            var rng = new Random((int)Hash.Of((int)(key & 0x7fffffff), (int)(key >> 31), 4411));
            if (rng.NextDouble() > chance) continue;
            for (int tries = 0; tries < 5; tries++)
            {
                // in the yard: 3 m out from a side, somewhere along it
                int side = rng.Next(4);
                float t = 0.15f + 0.7f * (float)rng.NextDouble(), gap = 2.8f + (float)rng.NextDouble() * 2f;
                float lx = side switch { 0 => br.OffX + b.MinX - gap, 1 => br.OffX + b.MaxX + gap, _ => br.OffX + b.MinX + (b.MaxX - b.MinX) * t };
                float lz = side switch { 2 => br.OffZ + b.MinZ - gap, 3 => br.OffZ + b.MaxZ + gap, _ => br.OffZ + b.MinZ + (b.MaxZ - b.MinZ) * t };
                if (ctx.InBuilding(lx, lz, 1.2f) || !float.IsNaN(ctx.Water(lx, lz))) continue;
                bool road = false;
                foreach (var s in ctx.Segs) if (s.Distance(lx, lz, out _) < s.Hw + 2.5f) { road = true; break; }
                if (road) continue;
                res.Add(new Well { X = ctx.Ox + lx, Z = ctx.Oz + lz, Yaw = (float)(rng.NextDouble() * Math.Tau), Pump = rng.NextDouble() < 0.25 });
                break;
            }
        }
        return res;
    }

    /// <summary>Builds squares near you (two a call, once a second), drops far ones.</summary>
    public void Update(Vector3 at, double now)
    {
        if (now < _next) return;
        _next = now + 1;
        foreach (var key in _cells.Keys.ToList())
        {
            var c = WorldOrigin.ToLocal((key.Item1 + 0.5) * Cell, 0, (key.Item2 + 0.5) * Cell) - at;
            if (c.X * c.X + c.Z * c.Z < Far * Far) continue;
            foreach (var w in _cells[key]) { w.Node?.QueueFree(); w.Node = null; }
            _cells.Remove(key);
        }
        var (wx, _, wz) = WorldOrigin.ToWorld(at);
        long x0 = (long)Math.Floor((wx - Near) / Cell), x1 = (long)Math.Floor((wx + Near) / Cell);
        long z0 = (long)Math.Floor((wz - Near) / Cell), z1 = (long)Math.Floor((wz + Near) / Cell);
        int made = 0;
        for (long cz = z0; cz <= z1 && made < 3; cz++)
            for (long cx = x0; cx <= x1 && made < 3; cx++)
            {
                if (_cells.ContainsKey((cx, cz))) continue;
                double mx = (cx + 0.5) * Cell - wx, mz = (cz + 0.5) * Cell - wz;
                if (mx * mx + mz * mz > Near * Near) continue;
                var list = Spots(cx, cz);
                foreach (var w in list) Build(w);
                _cells[(cx, cz)] = list;
                made++;
            }
    }

    /// <summary>The nearest village well within reach of a world spot (local coordinates), or null.</summary>
    public Well? Near_(Vector3 at, float reach)
    {
        Well? best = null; float bd = reach + 0.5f;
        foreach (var w in Live)
            if (w.Node != null && (w.Node.GlobalPosition - at with { Y = w.Node.GlobalPosition.Y }).Length() is var d && d < bd) { bd = d; best = w; }
        return best;
    }

    // ---- looks: the same shapes the map mesher draws for the mapped ones (D121) --------------------------------------

    private static StandardMaterial3D? _concrete, _wood, _roof, _green, _dark;
    private static readonly BoxMesh _box = new() { Size = Vector3.One };

    private void Build(Well w)
    {
        _concrete ??= Mat(new Color(.6f, .58f, .54f)); _wood ??= Mat(new Color(.38f, .27f, .17f));
        _roof ??= Mat(new Color(.3f, .23f, .17f)); _green ??= Mat(new Color(.17f, .36f, .24f)); _dark ??= Mat(new Color(.08f, .09f, .1f));
        var root = new Node3D { Name = w.Pump ? "VillagePump" : "VillageWell" };
        AddChild(root);
        void Part(float x, float y, float z, float hx, float hy, float hz, Material m) =>
            root.AddChild(new MeshInstance3D { Mesh = _box, MaterialOverride = m, Position = new Vector3(x, y, z), Scale = new Vector3(hx * 2, hy * 2, hz * 2) });
        float r;
        if (!w.Pump)
        {
            Part(0, 0.32f, 0, 0.52f, 0.42f, 0.52f, _concrete);
            Part(0, 0.75f, 0, 0.42f, 0.02f, 0.42f, _dark);
            Part(-0.5f, 0.95f, 0, 0.05f, 0.95f, 0.05f, _wood); Part(0.5f, 0.95f, 0, 0.05f, 0.95f, 0.05f, _wood);
            Part(0, 1.35f, 0, 0.5f, 0.04f, 0.04f, _wood); Part(0.62f, 1.25f, 0, 0.02f, 0.14f, 0.02f, _wood);
            Part(0, 1.98f, 0, 0.72f, 0.05f, 0.55f, _roof);
            r = 0.55f;
        }
        else
        {
            Part(0, 0.08f, 0, 0.35f, 0.08f, 0.35f, _concrete);
            Part(0, 0.65f, 0, 0.09f, 0.6f, 0.09f, _green); Part(0, 1.27f, 0, 0.12f, 0.05f, 0.12f, _green);
            Part(0.18f, 0.85f, 0, 0.12f, 0.035f, 0.035f, _green); Part(-0.3f, 1.2f, 0, 0.28f, 0.025f, 0.025f, _green);
            r = 0.2f;
        }
        var body = new StaticBody3D { CollisionLayer = Bodies.Props, CollisionMask = 0 };
        body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = r, Height = 1.6f }, Position = new Vector3(0, 0.8f, 0) });
        root.AddChild(body);
        float y = _world.Height(w.X, w.Z);
        root.GlobalTransform = new Transform3D(new Basis(Vector3.Up, w.Yaw), WorldOrigin.ToLocal(w.X, y, w.Z));
        w.Node = root;
    }

    private static StandardMaterial3D Mat(Color c) => new() { AlbedoColor = c, Roughness = 0.9f };
}
