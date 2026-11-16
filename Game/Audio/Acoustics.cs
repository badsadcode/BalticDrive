using System;
using System.Collections.Generic;
using BalticDrive.World;
using Godot;

namespace BalticDrive.Audio;

/// <summary>
/// The space around the listener (D53, the brief: "different acoustic character between apartment courtyards, streets
/// and forests"). Five times a second it casts 16 rays out at ear height (to 90 m) and one straight up, and from what
/// they hit works out how enclosed the place is and how far the walls are. That sets the reverb on the effects bus:
/// a courtyard rings short and bright, a street canyon longer, open ground and woods barely at all. The walls the rays
/// found are where your own shots echo from (<see cref="GunSound"/>).
/// </summary>
public sealed partial class Acoustics : Node
{
    public static Acoustics? Instance { get; private set; }
    public const int Rays = 16;
    public const float Reach = 90f;

    /// <summary>The walls found last time: where (world point) and how far.</summary>
    public readonly List<(Vector3 Point, float Distance)> Walls = new();
    /// <summary>0 = open country, 1 = a closed courtyard.</summary>
    public float Enclosure { get; private set; }
    /// <summary>Sky overhead (not under a roof or a bridge).</summary>
    public bool Sky { get; private set; } = true;
    public string Place { get; private set; } = "open";
    private double _next;
    private float _roomSize = 0.3f, _wet = 0.08f, _damping = 0.5f, _predelay = 20;

    public Acoustics() { Name = "Acoustics"; Instance = this; }
    public override void _ExitTree() { if (Instance == this) Instance = null; }

    public override void _Process(double delta)
    {
        var cam = GetViewport().GetCamera3D();
        if (cam == null) return;
        double now = Time.GetTicksMsec() / 1000.0;
        if (now >= _next)
        {
            _next = now + 0.2;
            Sense(cam.GlobalPosition);
        }
        // glide the reverb toward its target so walking round a corner doesn't jump
        if (Buses.Reverb is { } r)
        {
            float k = 1f - MathF.Exp(-3f * (float)delta);
            r.RoomSize = Mathf.Lerp(r.RoomSize, _roomSize, k);
            r.Wet = Mathf.Lerp(r.Wet, _wet, k);
            r.Damping = Mathf.Lerp(r.Damping, _damping, k);
            r.PredelayMsec = Mathf.Lerp(r.PredelayMsec, _predelay, k);
        }
    }

    private void Sense(Vector3 ear)
    {
        var space = GetViewport().World3D.DirectSpaceState;
        Walls.Clear();
        int hits = 0; float sum = 0, nearest = Reach;
        var q = new PhysicsRayQueryParameters3D { CollisionMask = Bodies.Buildings | Bodies.Ground | Bodies.Vehicles };
        for (int i = 0; i < Rays; i++)
        {
            float a = i * MathF.Tau / Rays;
            var dir = new Vector3(MathF.Cos(a), 0.02f, MathF.Sin(a));
            q.From = ear; q.To = ear + dir * Reach;
            var hit = space.IntersectRay(q);
            if (hit.Count == 0) continue;
            var p = (Vector3)hit["position"];
            float d = p.DistanceTo(ear);
            if (d < 1.2f) continue;   // the car you sit in, the wall you lean on
            hits++; sum += d; nearest = MathF.Min(nearest, d);
            Walls.Add((p, d));
        }
        q.From = ear; q.To = ear + Vector3.Up * 40f;
        Sky = space.IntersectRay(q).Count == 0;
        Enclosure = hits / (float)Rays;
        float mean = hits > 0 ? sum / hits : Reach;
        bool forest = !OpenAround(ear) && hits < 4;
        // a place: what the reverb should be
        // Outdoors there is almost no dense reverb: the space is heard in the separate echoes off the walls
        // (GunSound). A loud reverb made a street sound like a small room with an echo (Tom); only under a roof is it
        // allowed to ring.
        if (!Sky) { Place = "covered"; _roomSize = 0.6f; _wet = 0.2f; _damping = 0.4f; }
        else if (Enclosure > 0.75f) { Place = "courtyard"; _roomSize = 0.75f; _wet = 0.07f; _damping = 0.5f; }
        else if (Enclosure > 0.35f) { Place = "street"; _roomSize = 0.85f; _wet = 0.04f; _damping = 0.6f; }
        else if (forest) { Place = "woods"; _roomSize = 0.8f; _wet = 0.035f; _damping = 0.9f; }
        else { Place = "open"; _roomSize = 0.5f; _wet = 0.015f; _damping = 0.7f; }
        _predelay = Mathf.Clamp(2f * nearest / 343f * 1000f, 5f, 150f);
    }

    /// <summary>Trees all around (the tree field has no collision): checked by the land cover under the listener.</summary>
    private static bool OpenAround(Vector3 ear)
    {
        if (Main.Instance?.World is not { } w) return true;
        var (x, _, z) = WorldOrigin.ToWorld(ear);
        return w.LandAt(x, z) != Map.MapLand.Forest;
    }
}
