using System;
using System.Collections.Generic;
using BalticDrive.World;
using Godot;

namespace BalticDrive.OnFoot;

/// <summary>
/// Every bullet in flight, whoever fired it (D47; it was the player's alone, D40): muzzle velocity plus the shooter's
/// own, gravity and a little drag, ray-stepped each frame against the world and against characters (the player and
/// bandits), never against the shooter. What it hits reacts (<see cref="Hits"/>); the shooter hears back what it was.
/// Impacts throw dust coloured by the surface and leave a mark.
/// </summary>
public sealed partial class Ballistics : Node
{
    public static Ballistics? Instance { get; private set; }

    private sealed class Bullet
    {
        public Vector3 P, From, V;
        public float Life, Mass, Damage;
        public Rid Shooter;
        public Action<string?, BulletHit, string>? OnHit;
    }

    private readonly List<Bullet> _bullets = new();
    private readonly Queue<Node3D> _marks = new();
    private static readonly Dictionary<string, AudioStreamWav> Sounds = new();
    private PhysicsRayQueryParameters3D? _q;

    public Ballistics() { Name = "Ballistics"; }

    /// <summary>The one instance, made on first use under the scene root.</summary>
    public static Ballistics Get(Node anyNode)
    {
        if (Instance != null && IsInstanceValid(Instance)) return Instance;
        Instance = new Ballistics();
        anyNode.GetTree().Root.AddChild(Instance);
        return Instance;
    }

    public override void _Ready() => WorldOrigin.Shifted += OnShifted;
    public override void _ExitTree() { WorldOrigin.Shifted -= OnShifted; if (Instance == this) Instance = null; }

    private void OnShifted(double dx, double dz)
    {
        var d = new Vector3((float)dx, 0, (float)dz);
        foreach (var b in _bullets) { b.P -= d; b.From -= d; }
        foreach (var m in _marks) if (IsInstanceValid(m)) m.GlobalPosition -= d;
    }

    /// <summary>Sends a bullet off. <paramref name="onHit"/> hears what it hit (the target's line or null, the hit,
    /// the surface).</summary>
    public void Fire(Vector3 from, Vector3 velocity, float massKg, float damage, Rid shooter, Action<string?, BulletHit, string>? onHit = null) =>
        _bullets.Add(new Bullet { P = from, From = from, V = velocity, Life = 4f, Mass = massKg, Damage = damage, Shooter = shooter, OnHit = onHit });

    public override void _Process(double delta)
    {
        if (_bullets.Count == 0) return;
        float dt = (float)delta;
        var space = GetViewport().World3D.DirectSpaceState;
        _q ??= new PhysicsRayQueryParameters3D
        {
            CollisionMask = Bodies.Ground | Bodies.Roads | Bodies.Buildings | Bodies.Vehicles | Bodies.Props | Bodies.Characters | Bodies.Ragdoll,
        };
        for (int i = _bullets.Count - 1; i >= 0; i--)
        {
            var b = _bullets[i];
            var v = b.V + Vector3.Down * 9.81f * dt;           // drop
            v *= 1f - 0.0006f * v.Length() * dt;               // a touch of drag
            var next = b.P + v * dt;
            _q.From = b.P; _q.To = next;
            _q.Exclude = new Godot.Collections.Array<Rid> { b.Shooter };
            var hit = space.IntersectRay(_q);
            b.Life -= dt;
            if (hit.Count > 0)
            {
                var at = (Vector3)hit["position"]; var n = (Vector3)hit["normal"];
                var collider = hit["collider"].AsGodotObject();
                string surface = collider is Node c && c.HasMeta("surface") ? c.GetMeta("surface").AsString() : "hard";
                var bh = new BulletHit(at, n, v, b.Mass, at.DistanceTo(b.From), b.Damage);
                var said = Hits.Apply(collider, bh);
                b.OnHit?.Invoke(said, bh, surface);
                Impact(at, n, surface, collider as Node3D);
                _bullets.RemoveAt(i);
                continue;
            }
            b.P = next; b.V = v;
            if (b.Life <= 0) _bullets.RemoveAt(i);
        }
    }

    /// <param name="on">What was hit: the hole is stuck to it (D65: holes in a car stayed hanging in the air where
    /// it had been, and holes left in the scene root drifted when the floating origin moved).</param>
    private void Impact(Vector3 at, Vector3 n, string surface, Node3D? on = null)
    {
        var dust = surface switch
        {
            "ground" or "loose" => new Color(0.42f, 0.33f, 0.22f),
            "paved" => new Color(0.55f, 0.55f, 0.53f),
            "metal" => new Color(1f, 0.75f, 0.35f),     // sparks
            "glass" => new Color(0.8f, 0.9f, 0.85f),
            "paper" => new Color(0.95f, 0.93f, 0.85f),
            "flesh" => new Color(0.45f, 0.05f, 0.05f),
            _ => new Color(0.72f, 0.7f, 0.65f),
        };
        if (surface == "glass") return;   // the bottle shatters instead
        var p = new CpuParticles3D
        {
            Emitting = false, OneShot = true, Amount = 14, Lifetime = 0.6, Explosiveness = 0.95f,
            Direction = Vector3.Up, Spread = 35, InitialVelocityMin = 1.5f, InitialVelocityMax = 4.5f, Gravity = new Vector3(0, -9, 0),
            ScaleAmountMin = 0.6f, ScaleAmountMax = 1.4f, Color = dust,
            Mesh = new SphereMesh { Radius = 0.025f, Height = 0.05f, RadialSegments = 6, Rings = 3, Material = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 1 } },
        };
        GetTree().Root.AddChild(p);
        p.GlobalPosition = at;
        p.LookAt(at + n + new Vector3(0.001f, 0.0013f, 0), Vector3.Up);
        p.RotateObjectLocal(Vector3.Right, -MathF.PI / 2);
        p.Emitting = true;
        GetTree().CreateTimer(1.0).Timeout += () => { if (IsInstanceValid(p)) p.QueueFree(); };
        if (surface == "flesh") return;   // no bullet hole on a person
        // a mark that stays a while
        var mark = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = new Vector2(0.07f, 0.07f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.06f, 0.055f, 0.05f), Roughness = 1, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        if (on != null && IsInstanceValid(on)) on.AddChild(mark); else GetTree().Root.AddChild(mark);
        mark.GlobalPosition = at + n * 0.01f;
        if (MathF.Abs(n.Dot(Vector3.Up)) > 0.98f) mark.LookAt(mark.GlobalPosition - n, Vector3.Forward); else mark.LookAt(mark.GlobalPosition - n, Vector3.Up);
        _marks.Enqueue(mark);
        while (_marks.Count > 60) { var old = _marks.Dequeue(); if (IsInstanceValid(old)) old.QueueFree(); }
        if (surface is not ("metal" or "paper")) Play3D(this, at, surface == "hard" ? "hit_hard" : "hit_soft", -6);   // targets ring on their own
    }

    /// <summary>A generated sound at a place (shared by impacts and distant shots).</summary>
    public static void Play3D(Node from, Vector3 at, string key, float db = 0, float maxDistance = 120)
    {
        if (!Sounds.TryGetValue(key, out var s))
        {
            var def = WeaponDef.All.FirstOrDefaultId(key);
            Sounds[key] = s = key switch
            {
                "hit_hard" => Synth.Hit(0.9f),
                "hit_soft" => Synth.Hit(0.5f),
                _ when def != null => Synth.Shot(def.Boom, def.Pitch),
                _ => Synth.Hit(0.5f),
            };
        }
        var p = new AudioStreamPlayer3D { Stream = s, UnitSize = 4, MaxDistance = maxDistance, VolumeDb = db, PitchScale = (float)GD.RandRange(0.95, 1.05), Bus = Audio.Buses.Sfx };
        from.GetTree().Root.AddChild(p);
        p.GlobalPosition = at;
        p.Play();
        p.Finished += p.QueueFree;
    }
}

internal static class WeaponDefLookup
{
    /// <summary>The weapon a sound key like "ak_shot" belongs to.</summary>
    public static WeaponDef? FirstOrDefaultId(this WeaponDef[] all, string key)
    {
        foreach (var d in all) if (key.StartsWith(d.Id + "_")) return d;
        return null;
    }
}
