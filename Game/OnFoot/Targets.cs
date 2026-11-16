using System;
using System.Collections.Generic;
using System.Linq;
using BalticDrive.World;
using Godot;

namespace BalticDrive.OnFoot;

/// <summary>A bullet arriving (D42): where, the surface normal, its velocity, its mass, and how far it flew.</summary>
public readonly record struct BulletHit(Vector3 Point, Vector3 Normal, Vector3 Velocity, float Mass, float Range, float Damage = 0);

/// <summary>Anything that reacts to being shot (D42). Bandits will be one too.</summary>
public interface IHittable
{
    /// <summary>Called once per bullet; returns a short line for the HUD ("plate, 50 m"), or null.</summary>
    string? Hit(BulletHit h);
}

/// <summary>
/// What a bullet does to what it hits (D42): an <see cref="IHittable"/> (or one above it in the tree) gets the hit;
/// any other loose rigid body gets a shove. Real momentum would send a can at 500 m/s, so the shove is scaled to
/// look right rather than be right.
/// </summary>
public static class Hits
{
    public const float Push = 0.12f;

    public static string? Apply(GodotObject? collider, BulletHit h)
    {
        for (var n = collider as Node; n != null; n = n.GetParent())
        {
            if (n is IHittable t) return t.Hit(h);
            if (n is RigidBody3D rb) { rb.ApplyImpulse(h.Velocity * h.Mass * Push, h.Point - rb.GlobalPosition); return null; }
            if (n is CollisionObject3D) break;   // a static body: nothing more to find above it
        }
        return null;
    }
}

/// <summary>
/// A loose prop to plink at (D42): a bottle that shatters into shards, a can that jumps, a barrel that rocks.
/// The Synty mesh is scaled to a believable size; the collision is a cylinder round it.
/// </summary>
public sealed partial class Plinkable : RigidBody3D, IHittable
{
    public enum Kinds { Glass, Can, Barrel }
    public Kinds Kind { get; }
    private readonly Color _glass;

    public Plinkable(Kinds kind, string scene, float scale, float massKg, Color glass = default)
    {
        Kind = kind;
        _glass = glass;
        Mass = massKg;
        CollisionLayer = Bodies.Props;
        CollisionMask = Bodies.Ground | Bodies.Roads | Bodies.Buildings | Bodies.Vehicles | Bodies.Props | Bodies.Characters;
        ContinuousCd = true;
        SetMeta("surface", kind == Kinds.Glass ? "glass" : "metal");
        Aabb box = new(new Vector3(-0.05f, 0, -0.05f), new Vector3(0.1f, 0.3f, 0.1f));
        if (ResourceLoader.Exists(scene) && ResourceLoader.Load<PackedScene>(scene) is { } ps)
        {
            var inst = ps.Instantiate<Node3D>();
            foreach (var body in inst.FindChildren("*", "CollisionObject3D", true, false)) body.Free();
            if (inst is MeshInstance3D mi && mi.Mesh != null) box = mi.Mesh.GetAabb();
            inst.Scale = Vector3.One * scale;
            AddChild(inst);
        }
        else AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.04f, BottomRadius = 0.04f, Height = 0.3f }, Position = new Vector3(0, 0.15f, 0) });
        float r = MathF.Max(box.Size.X, box.Size.Z) * 0.5f * scale, h = box.Size.Y * scale;
        AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = r, Height = h }, Position = new Vector3(0, (box.Position.Y + box.Size.Y * 0.5f) * scale, 0) });
    }

    public string? Hit(BulletHit h)
    {
        switch (Kind)
        {
            case Kinds.Glass:
                Shatter(h);
                Sfx.Play(this, "glass", h.Point);
                QueueFree();
                return "bottle";
            case Kinds.Can:
                // a can takes the whole shove, and spins
                ApplyImpulse(h.Velocity * h.Mass * Hits.Push * 0.6f + Vector3.Up * 0.6f * Mass * 6f, h.Point - GlobalPosition);
                Sfx.Play(this, "tink", h.Point);
                return "can";
            default:
                ApplyImpulse(h.Velocity * h.Mass * Hits.Push * 3f, h.Point - GlobalPosition);
                Sfx.Play(this, "clunk", h.Point);
                return "barrel";
        }
    }

    /// <summary>The bottle becomes a dozen glass shards that fly away from the bullet and fade.</summary>
    private void Shatter(BulletHit h)
    {
        var parent = GetParent();
        var mat = new StandardMaterial3D
        {
            AlbedoColor = _glass, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, Roughness = 0.1f, Metallic = 0.2f,
        };
        var rng = new Random((int)(h.Point.X * 1000) ^ (int)(h.Point.Z * 1000));
        var centre = GlobalPosition + Vector3.Up * 0.12f;
        var dir = h.Velocity.Normalized();
        for (int i = 0; i < 12; i++)
        {
            float s = 0.015f + (float)rng.NextDouble() * 0.035f;
            var shard = new RigidBody3D { Mass = 0.01f, CollisionLayer = 0, CollisionMask = Bodies.Ground | Bodies.Roads | Bodies.Buildings | Bodies.Props };
            shard.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(s, s * 1.6f, 0.004f) }, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            shard.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(s, s, 0.01f) } });
            parent.AddChild(shard);
            shard.GlobalPosition = centre + new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble(), (float)rng.NextDouble() - 0.5f) * 0.12f;
            shard.Rotation = new Vector3((float)rng.NextDouble() * 6, (float)rng.NextDouble() * 6, (float)rng.NextDouble() * 6);
            var spray = (dir * 0.6f + new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() * 0.8f, (float)rng.NextDouble() - 0.5f)).Normalized();
            shard.LinearVelocity = spray * (2f + (float)rng.NextDouble() * 4f);
            shard.AngularVelocity = new Vector3((float)rng.NextDouble() * 20 - 10, (float)rng.NextDouble() * 20 - 10, (float)rng.NextDouble() * 20 - 10);
            shard.GetTree().CreateTimer(5 + rng.NextDouble() * 2).Timeout += () => { if (IsInstanceValid(shard)) shard.QueueFree(); };
        }
    }
}

/// <summary>
/// A steel plate on a post (D42), made in code: a 30 cm disc hanging from a hinge at its top. A hit swings it back
/// and rings it; you hear and see it from 100 m.
/// </summary>
public sealed partial class SteelPlate : Node3D
{
    private readonly PlateBody _plate;

    public SteelPlate(float diameter = 0.3f)
    {
        var steel = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.26f, 0.27f), Metallic = 0.7f, Roughness = 0.45f };
        var paint = new StandardMaterial3D { AlbedoColor = new Color(0.92f, 0.9f, 0.82f), Roughness = 0.8f };
        // the frame: two posts and a bar, static
        var frame = new StaticBody3D { CollisionLayer = Bodies.Props, CollisionMask = 0 };
        frame.SetMeta("surface", "metal");
        AddChild(frame);
        float top = 1.35f, half = diameter * 0.5f + 0.15f;
        foreach (float x in new[] { -half, half })
        {
            frame.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.05f, top, 0.05f) }, Position = new Vector3(x, top * 0.5f, 0), MaterialOverride = steel });
            frame.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.05f, top, 0.05f) }, Position = new Vector3(x, top * 0.5f, 0) });
        }
        frame.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(half * 2 + 0.05f, 0.05f, 0.05f) }, Position = new Vector3(0, top, 0), MaterialOverride = steel });
        // the plate: a disc painted white (so hits show), on a short chain link to the bar
        _plate = new PlateBody(this) { Mass = 6f, CollisionLayer = Bodies.Props, CollisionMask = 0, AngularDamp = 0.6f, LinearDamp = 0.3f };
        _plate.SetMeta("surface", "metal");
        AddChild(_plate);
        _plate.Position = new Vector3(0, top - 0.12f - diameter * 0.5f, 0);
        var disc = new CylinderMesh { TopRadius = diameter * 0.5f, BottomRadius = diameter * 0.5f, Height = 0.012f, RadialSegments = 24 };
        _plate.AddChild(new MeshInstance3D { Mesh = disc, RotationDegrees = new Vector3(90, 0, 0), MaterialOverride = paint });
        _plate.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = diameter * 0.5f, Height = 0.012f }, RotationDegrees = new Vector3(90, 0, 0) });
        _plate.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(0.03f, 0.13f, 0.01f) }, Position = new Vector3(0, diameter * 0.5f + 0.06f, 0), MaterialOverride = steel });
        var hinge = new HingeJoint3D { Position = new Vector3(0, top - 0.02f, 0), RotationDegrees = new Vector3(0, 90, 0) };
        AddChild(hinge);
        hinge.NodeA = hinge.GetPathTo(frame);
        hinge.NodeB = hinge.GetPathTo(_plate);
    }

    public string? Hit(BulletHit h)
    {
        _plate.ApplyImpulse(h.Velocity * h.Mass * Hits.Push * 6f, h.Point - _plate.GlobalPosition);
        Sfx.Play(this, "clang", h.Point);
        return "plate";
    }

    /// <summary>The swinging disc: passes its hits to the plate that owns it.</summary>
    private sealed partial class PlateBody : RigidBody3D, IHittable
    {
        private readonly SteelPlate _owner;
        public PlateBody(SteelPlate owner) { _owner = owner; Name = "Plate"; }
        public string? Hit(BulletHit h) => _owner.Hit(h);
    }

    public RigidBody3D Plate => _plate;
}

/// <summary>
/// A paper target on its stand (D42; Synty Gang Warfare's range target). Hits are scored by ring, 10 in the middle
/// down to 1 at the edge, and the hole stays (the impact mark).
/// </summary>
public sealed partial class PaperTarget : StaticBody3D, IHittable
{
    private const string Scene = "res://Assets/Synty/PolygonGangWarfare/Prefabs/Props/SM_Prop_Range_Target_01.tscn";
    private const string Stand = "res://Assets/Synty/PolygonGangWarfare/Prefabs/Props/SM_Prop_Range_TargetHolder_01.tscn";
    private Vector3 _centre;
    private float _radius = 0.3f;

    public PaperTarget()
    {
        CollisionLayer = Bodies.Props;
        CollisionMask = 0;
        SetMeta("surface", "paper");
        float standTop = 0;
        if (Load(Stand) is { } stand)
        {
            var box = ((MeshInstance3D)stand).Mesh.GetAabb();
            stand.Position = new Vector3(0, -box.Position.Y, 0);
            standTop = box.Size.Y;
            AddChild(stand);
            AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = box.Size }, Position = new Vector3(0, box.Size.Y * 0.5f, 0) });
        }
        if (Load(Scene) is { } paper)
        {
            var box = ((MeshInstance3D)paper).Mesh.GetAabb();
            paper.Position = new Vector3(0, standTop - 0.05f - box.Position.Y, 0);
            AddChild(paper);
            _centre = paper.Position + box.GetCenter();
            _radius = MathF.Min(box.Size.X, box.Size.Y) * 0.5f;
            AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(box.Size.X, box.Size.Y, 0.03f) }, Position = _centre });
        }
        else
        {
            _centre = new Vector3(0, 1.0f, 0);
            AddChild(new MeshInstance3D { Mesh = new QuadMesh { Size = new Vector2(0.6f, 0.8f) }, Position = _centre, RotationDegrees = new Vector3(0, 180, 0) });
            AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(0.6f, 0.8f, 0.03f) }, Position = _centre });
        }
    }

    private static Node3D? Load(string path)
    {
        if (!ResourceLoader.Exists(path) || ResourceLoader.Load<PackedScene>(path) is not { } ps) return null;
        var n = ps.Instantiate<Node3D>();
        foreach (var body in n.FindChildren("*", "CollisionObject3D", true, false)) body.Free();
        return n;
    }

    public string? Hit(BulletHit h)
    {
        var local = ToLocal(h.Point) - _centre;
        float off = new Vector2(local.X, local.Y).Length() / _radius;
        int score = Math.Clamp(10 - (int)(off * 10), 0, 10);
        Sfx.Play(this, "paper", h.Point);
        return score > 0 ? $"target: {score}" : "target: miss";
    }
}

/// <summary>
/// A small range set up in front of the player (T, D42): cans and bottles on barrels at 8 m, the paper target at
/// 15 m, steel plates at 25, 50 and 100 m. Everything stands on the ground found by a ray, so it works anywhere.
/// </summary>
public sealed partial class ShootingRange : Node3D
{
    private const string Gang = "res://Assets/Synty/PolygonGangWarfare/Prefabs/Props/";
    private const string City = "res://Assets/Synty/PolygonCity/Prefabs/Props/";
    public readonly List<(string Name, Node3D Node, Vector3 Aim)> Targets = new();

    public ShootingRange() { Name = "ShootingRange"; }

    public override void _Ready() => WorldOrigin.Shifted += OnShifted;
    public override void _ExitTree() => WorldOrigin.Shifted -= OnShifted;

    private void OnShifted(double dx, double dz)
    {
        var d = new Vector3((float)dx, 0, (float)dz);
        foreach (var c in GetChildren().OfType<Node3D>()) c.GlobalPosition -= d;
    }

    /// <summary>Builds the range from <paramref name="from"/> (the shooter's feet) looking along <paramref name="yaw"/>.</summary>
    public void Build(Vector3 from, float yaw)
    {
        var fwd = new Vector3(-MathF.Sin(yaw), 0, -MathF.Cos(yaw));
        var right = new Vector3(-fwd.Z, 0, fwd.X);
        var space = GetWorld3D().DirectSpaceState;
        Vector3 Ground(Vector3 p)
        {
            var q = PhysicsRayQueryParameters3D.Create(p + Vector3.Up * 30, p + Vector3.Down * 60, Bodies.Ground | Bodies.Roads | Bodies.Buildings);
            var hit = space.IntersectRay(q);
            return hit.Count > 0 ? (Vector3)hit["position"] : p;
        }
        Vector3 At(float ahead, float side) => Ground(from + fwd * ahead + right * side);
        T Place<T>(T n, Vector3 at, string name, Vector3 aim) where T : Node3D
        {
            AddChild(n);
            n.GlobalPosition = at;
            n.Rotation = new Vector3(0, yaw, 0);
            Targets.Add((name, n, aim));
            return n;
        }

        // 8 m: two barrels, cans and bottles on top
        foreach (var (side, i) in new[] { (-1.2f, 0), (1.2f, 1) })
        {
            var at = At(8, side);
            var barrel = Place(new Plinkable(Plinkable.Kinds.Barrel, Gang + "SM_Prop_Barrel_Metal_01.tscn", 0.95f, 40f), at, $"barrel {i + 1}", Vector3.Up * 0.6f);
            float top = 1.04544f * 0.95f;
            var items = i == 0
                ? new[] { (Plinkable.Kinds.Can, City + "SM_Prop_Soda_02.tscn", 0.55f, 0.03f, -0.18f), (Plinkable.Kinds.Glass, Gang + "SM_Prop_Alcohol_Bottle_01.tscn", 0.7f, 0.4f, 0.02f), (Plinkable.Kinds.Can, City + "SM_Prop_Soda_02.tscn", 0.55f, 0.03f, 0.2f) }
                : new[] { (Plinkable.Kinds.Glass, Gang + "SM_Prop_Bottle_01.tscn", 0.8f, 0.5f, -0.16f), (Plinkable.Kinds.Can, City + "SM_Prop_Soda_02.tscn", 0.55f, 0.03f, 0.04f), (Plinkable.Kinds.Glass, Gang + "SM_Prop_Alcohol_Bottle_01.tscn", 0.7f, 0.4f, 0.22f) };
            int k = 0;
            foreach (var (kind, scene, scale, mass, x) in items)
            {
                var glass = scene.Contains("Alcohol") ? new Color(0.25f, 0.45f, 0.2f, 0.6f) : new Color(0.55f, 0.35f, 0.15f, 0.6f);
                Place(new Plinkable(kind, scene, scale, mass, glass), at + right * x + Vector3.Up * (top + 0.005f), $"{(kind == Plinkable.Kinds.Glass ? "bottle" : "can")} {i * 3 + ++k}",
                    Vector3.Up * (kind == Plinkable.Kinds.Glass ? 0.12f : 0.05f));
            }
        }
        // 15 m: the paper target
        var paper = Place(new PaperTarget(), At(15, 0), "paper target", Vector3.Zero);
        Targets[^1] = ("paper target", paper, Vector3.Up * 0.95f);
        // 25, 50, 100 m: steel plates
        foreach (var (d, side) in new[] { (25f, -2f), (50f, 1.5f), (100f, -1f) })
        {
            var plate = Place(new SteelPlate(), At(d, side), $"plate {d:0} m", Vector3.Zero);
            Targets[^1] = ($"plate {d:0} m", plate, Vector3.Up * (1.35f - 0.12f - 0.15f));
        }
    }
}

/// <summary>Target sounds: generated placeholders like the guns' (D40), played where they happen.</summary>
public static class Sfx
{
    private static readonly Dictionary<string, AudioStreamWav> Cache = new();

    public static void Play(Node from, string key, Vector3 at)
    {
        if (!Cache.TryGetValue(key, out var s))
            Cache[key] = s = key switch
            {
                "glass" => Synth.Glass(),
                "clang" => Synth.Ring(new[] { 820f, 1370f, 2210f, 3050f }, 1.6f),
                "tink" => Synth.Ring(new[] { 2400f, 3900f }, 0.25f),
                "clunk" => Synth.Ring(new[] { 180f, 410f, 760f }, 0.5f),
                _ => Synth.Hit(0.3f),
            };
        var p = new AudioStreamPlayer3D { Stream = s, UnitSize = 6, MaxDistance = 300, VolumeDb = -3, Bus = Audio.Buses.Sfx };
        from.GetTree().Root.AddChild(p);
        p.GlobalPosition = at;
        p.Play();
        p.Finished += p.QueueFree;
    }
}
