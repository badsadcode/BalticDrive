using System;
using System.Collections.Generic;
using System.Linq;
using BalticDrive.Core;
using BalticDrive.World;
using Godot;

namespace BalticDrive.OnFoot;

/// <summary>
/// A ragdoll for a character's skeleton (D56, Tom: "on death, ragdolls"). Built once with the character but asleep:
/// a capsule per main bone (hips, spine, chest, head, upper and lower arms, upper and lower legs, feet) on cone joints
/// with limits, under a <see cref="PhysicalBoneSimulator3D"/> that is the skeleton's last modifier, so while it
/// simulates it overrides the animation and the gun-holding. On death <see cref="Start"/> lets it go with a push from
/// the bullet at the bone nearest the hit; gravity does the rest. Bones without a body (neck, shoulders, hands,
/// fingers) keep their animated pose relative to their parent.
/// </summary>
public sealed class Ragdoll
{
    public readonly PhysicalBoneSimulator3D Sim;
    private readonly Skeleton3D _skel;
    private readonly Dictionary<string, PhysicalBone3D> _bones = new();
    public bool Running { get; private set; }

    /// <summary>The bodies' bones: name, the bone its capsule reaches to (null: a sphere), radius (metres), mass (kg),
    /// how far the joint swings and twists (degrees).</summary>
    private static readonly (string Bone, string? To, float Radius, float Mass, float Swing, float Twist)[] Spec =
    {
        ("Hips", "Spine", 0.13f, 12f, 0, 0),
        ("Spine", "Chest", 0.12f, 8f, 20, 12),
        ("Chest", "Neck", 0.15f, 14f, 20, 12),
        ("Head", null, 0.11f, 5f, 35, 30),
        ("LeftUpperArm", "LeftLowerArm", 0.055f, 2.5f, 70, 35),
        ("LeftLowerArm", "LeftHand", 0.045f, 1.6f, 60, 10),
        ("RightUpperArm", "RightLowerArm", 0.055f, 2.5f, 70, 35),
        ("RightLowerArm", "RightHand", 0.045f, 1.6f, 60, 10),
        ("LeftUpperLeg", "LeftLowerLeg", 0.08f, 8f, 45, 12),
        ("LeftLowerLeg", "LeftFoot", 0.06f, 4f, 60, 5),
        ("LeftFoot", "LeftToes", 0.05f, 1f, 25, 5),
        ("RightUpperLeg", "RightLowerLeg", 0.08f, 8f, 45, 12),
        ("RightLowerLeg", "RightFoot", 0.06f, 4f, 60, 5),
        ("RightFoot", "RightToes", 0.05f, 1f, 25, 5),
    };
    private static readonly HashSet<string> Core = new() { "Hips", "Spine", "Chest", "Head", "LeftUpperArm", "RightUpperArm", "LeftUpperLeg", "RightUpperLeg" };

    private Ragdoll(Skeleton3D skel, PhysicalBoneSimulator3D sim) { _skel = skel; Sim = sim; }

    /// <summary>Builds the bodies under <paramref name="skel"/>; null if the skeleton lacks the bones. Call after the
    /// other modifiers are added (it must be the last).</summary>
    public static Ragdoll? Build(Skeleton3D skel)
    {
        if (Spec.Any(s => skel.FindBone(s.Bone) < 0)) return null;
        var sim = new PhysicalBoneSimulator3D { Name = "Ragdoll", Active = false };
        skel.AddChild(sim);
        var r = new Ragdoll(skel, sim);
        // the skeleton may be scaled (an imported rig): sizes in metres become bone-space units
        float scale = skel.GlobalBasis.Scale.X;
        if (scale <= 0) scale = 1;
        foreach (var (name, to, radius, mass, swing, twist) in Spec)
        {
            int b = skel.FindBone(name);
            var rest = skel.GetBoneGlobalRest(b);
            var pb = new PhysicalBone3D
            {
                Name = name, Mass = mass, Friction = 0.9f, Bounce = 0f,
                LinearDamp = 0.15f, AngularDamp = 1.2f,
                CollisionLayer = Bodies.Ragdoll, CollisionMask = Bodies.Ground | Bodies.Roads | Bodies.Buildings | Bodies.Props | Bodies.Vehicles | Bodies.Interior,
                JointType = name == "Hips" ? PhysicalBone3D.JointTypeEnum.None : PhysicalBone3D.JointTypeEnum.Cone,
                // the cone's axis is the joint's X; the bone runs along its own Y
                JointRotation = new Vector3(0, 0, MathF.PI / 2),
            };
            pb.Set("bone_name", name);
            pb.SetMeta("surface", "flesh");   // bullets that land on a body lying down (D80)
            if (name != "Hips")
            {
                pb.Set("joint_constraints/swing_span", swing);
                pb.Set("joint_constraints/twist_span", twist);
                pb.Set("joint_constraints/bias", 0.3f);
                pb.Set("joint_constraints/softness", 0.8f);
                pb.Set("joint_constraints/relaxation", 1f);
            }
            // the shape, in the bone's own space: a capsule from the bone to the next one, or a sphere a little up
            // the bone (the head)
            Vector3 along = to != null ? (rest.AffineInverse() * skel.GetBoneGlobalRest(skel.FindBone(to))).Origin : Vector3.Up * (0.12f / scale);
            float len = along.Length();
            // the cone's axis is the joint's X: turned onto the bone (Synty's bones run along Y: the old quarter turn)
            if (len > 1e-5f) pb.JointRotation = (AlignY(along / len) * new Basis(Vector3.Back, MathF.PI / 2)).GetEuler();
            sim.AddChild(pb);
            Shape3D shape = to == null
                ? new SphereShape3D { Radius = radius / scale }
                : new CapsuleShape3D { Radius = radius / scale, Height = MathF.Max(len, 2.2f * radius / scale) };
            var dir = len > 1e-5f ? along / len : Vector3.Up;
            var basis = AlignY(dir);
            pb.AddChild(new CollisionShape3D { Shape = shape, Transform = new Transform3D(basis, along * 0.5f) });
            r._bones[name] = pb;
        }
        // limbs mustn't fight the trunk they overlap at the joints; neighbours mustn't fight each other
        foreach (var a in r._bones)
            foreach (var c in r._bones)
            {
                if (string.CompareOrdinal(a.Key, c.Key) >= 0) continue;
                bool neighbours = Parent(skel, a.Key) == c.Key || Parent(skel, c.Key) == a.Key;
                if (neighbours || (Core.Contains(a.Key) && Core.Contains(c.Key))) a.Value.AddCollisionExceptionWith(c.Value);
            }
        Log.Info("Ragdoll", $"{r._bones.Count} bodies, skeleton scale {scale:0.###}");
        return r;
    }

    /// <summary>The nearest ancestor with a body.</summary>
    private static string? Parent(Skeleton3D skel, string bone)
    {
        int p = skel.GetBoneParent(skel.FindBone(bone));
        while (p >= 0)
        {
            string n = skel.GetBoneName(p);
            if (Spec.Any(s => s.Bone == n)) return n;
            p = skel.GetBoneParent(p);
        }
        return null;
    }

    private static Basis AlignY(Vector3 dir)
    {
        var up = Vector3.Up;
        var axis = up.Cross(dir);
        float d = Mathf.Clamp(up.Dot(dir), -1f, 1f);
        if (axis.LengthSquared() < 1e-8f) return d > 0 ? Basis.Identity : new Basis(Vector3.Right, MathF.PI);
        return new Basis(axis.Normalized(), MathF.Acos(d));
    }

    /// <summary>Lets go: the bodies take the pose the skeleton is in and fall, pushed by <paramref name="impulse"/>
    /// (N·s, world) at the body nearest <paramref name="at"/>.</summary>
    public void Start(Vector3 impulse, Vector3 at) => Start(impulse, at, Vector3.Zero);

    /// <summary>As <see cref="Start(Vector3, Vector3)"/>, and every body also given <paramref name="bodyVelocity"/>
    /// (a car's hit moves the whole person, not one bone; D80).</summary>
    public void Start(Vector3 impulse, Vector3 at, Vector3 bodyVelocity)
    {
        if (Running) return;
        Running = true;
        Sim.Active = true;
        Sim.PhysicalBonesStartSimulation();
        if (bodyVelocity.LengthSquared() > 1e-6f)
            foreach (var b in _bones.Values) b.ApplyCentralImpulse(bodyVelocity * b.Mass);
        if (impulse.LengthSquared() < 1e-6f) return;
        var hit = _bones.Values.OrderBy(b => b.GlobalPosition.DistanceSquaredTo(at)).First();
        hit.ApplyCentralImpulse(impulse);
        // a share to the trunk too: one bone yanked alone looks like a puppet's string
        if (_bones.TryGetValue("Chest", out var chest) && chest != hit) chest.ApplyCentralImpulse(impulse * 0.5f);
    }

    /// <summary>Back to the animation (waking up).</summary>
    public void Stop()
    {
        if (!Running) return;
        Running = false;
        Sim.PhysicalBonesStopSimulation();
        Sim.Active = false;
    }

    /// <summary>Freezes the pose the bodies are in onto another skeleton of the same rig (the body left behind):
    /// bones with a body take its transform, the rest keep their animated pose under their parent.</summary>
    public void CopyPoseTo(Skeleton3D dst)
    {
        int n = _skel.GetBoneCount();
        if (dst.GetBoneCount() != n) return;
        dst.GlobalTransform = _skel.GlobalTransform;
        var inv = _skel.GlobalTransform.AffineInverse();
        var global = new Transform3D[n];
        for (int i = 0; i < n; i++)   // parents come before children in a skeleton's bone order
        {
            int p = _skel.GetBoneParent(i);
            var parent = p >= 0 ? global[p] : Transform3D.Identity;
            if (Running && _bones.TryGetValue(_skel.GetBoneName(i), out var pb)) global[i] = inv * pb.GlobalTransform;
            else global[i] = parent * _skel.GetBonePose(i);
            var local = parent.AffineInverse() * global[i];
            dst.SetBonePosePosition(i, local.Origin);
            dst.SetBonePoseRotation(i, local.Basis.Orthonormalized().GetRotationQuaternion());
        }
    }

    public PhysicalBone3D? Head => _bones.GetValueOrDefault("Head");
    public PhysicalBone3D? Hips => _bones.GetValueOrDefault("Hips");

    /// <summary>The world moved under everything (floating origin): bodies are simulated in world space, so they're
    /// moved by hand.</summary>
    public void Shift(Vector3 d)
    {
        if (!Running) return;
        // straight to the physics server: a simulated bone's node transform is overwritten by the body each step
        foreach (var b in _bones.Values)
        {
            var t = (Transform3D)PhysicsServer3D.BodyGetState(b.GetRid(), PhysicsServer3D.BodyState.Transform);
            PhysicsServer3D.BodySetState(b.GetRid(), PhysicsServer3D.BodyState.Transform, t with { Origin = t.Origin - d });
        }
    }
}
