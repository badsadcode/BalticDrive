using System;
using System.Linq;
using Godot;

namespace BalticDrive.OnFoot;

/// <summary>
/// First-person arms (D41): a second copy of the walker's own character rides with the eye camera, head and legs
/// collapsed, torso just behind the eye (out of view), and its arms are bent by two-bone IK each frame so the hands sit
/// on the gun: the right hand on the grip, the left on the handguard (or cupping the right hand on a pistol), fingers
/// curled. The sleeves are the same hoodie as in third person. All in the skeleton's own space.
/// </summary>
public sealed partial class FpArms : Node3D
{
    private Skeleton3D? _s;
    private Arm _right, _left;
    private readonly int[][] _rightFingers = Array.Empty<int[]>(), _leftFingers = Array.Empty<int[]>();
    private int _rThumb = -1, _lThumb = -1;

    /// <summary>One hand's goal, in world space: where the grip is, which way the hand points (wrist to knuckles),
    /// which way the palm faces, which way the elbow should go, and how far the fingers curl (0..1).</summary>
    public readonly record struct HandGoal(Vector3 Grip, Vector3 Dir, Vector3 Palm, Vector3 Pole, float Curl);

    private readonly struct Arm
    {
        public readonly int Up, Lo, Hand, Mid;
        public readonly float L1, L2;
        public Arm(Skeleton3D s, string side)
        {
            Up = s.FindBone(side + "UpperArm"); Lo = s.FindBone(side + "LowerArm"); Hand = s.FindBone(side + "Hand");
            Mid = s.FindBone(side + "MiddleProximal");
            L1 = Up < 0 || Lo < 0 ? 0 : s.GetBoneGlobalRest(Up).Origin.DistanceTo(s.GetBoneGlobalRest(Lo).Origin);
            L2 = Lo < 0 || Hand < 0 ? 0 : s.GetBoneGlobalRest(Lo).Origin.DistanceTo(s.GetBoneGlobalRest(Hand).Origin);
        }
        public bool Ok => Up >= 0 && Lo >= 0 && Hand >= 0 && Mid >= 0 && L1 > 0 && L2 > 0;
    }

    public bool Ready => _s != null && _right.Ok && _left.Ok;

    public FpArms()
    {
        Name = "FpArms";
        if (!ResourceLoader.Exists(Walker.CharacterScene) || ResourceLoader.Load<PackedScene>(Walker.CharacterScene) is not { } scene) return;
        var prefab = scene.Instantiate<Node3D>();
        var skel = prefab.GetNodeOrNull<Skeleton3D>("GeneralSkeleton");
        if (skel == null) { prefab.QueueFree(); return; }
        prefab.RemoveChild(skel);
        prefab.QueueFree();
        foreach (var mi in skel.GetChildren().OfType<MeshInstance3D>())
        {
            mi.Visible = mi.Name == "Character_Male_Hoodie";
            mi.Skeleton = new NodePath("..");
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
        // the character faces +Z, the camera looks down -Z
        var holder = new Node3D { Name = "Body", RotationDegrees = new Vector3(0, 180, 0) };
        AddChild(holder);
        holder.AddChild(skel);
        _s = skel;
        _right = new Arm(skel, "Right");
        _left = new Arm(skel, "Left");
        // seen from inside the head: no head, no legs
        foreach (var b in new[] { "Head", "LeftUpperLeg", "RightUpperLeg" })
            if (skel.FindBone(b) is var i and >= 0) skel.SetBonePoseScale(i, Vector3.One * 0.001f);
        _rightFingers = Fingers(skel, "Right");
        _leftFingers = Fingers(skel, "Left");
        _rThumb = skel.FindBone("RightThumbProximal");
        _lThumb = skel.FindBone("LeftThumbProximal");
        // the eye sits where the head was, a little ahead of the face so the hood stays behind the camera
        var eye = skel.GetBoneGlobalRest(skel.FindBone("Head")).Origin + new Vector3(0, 0.06f, 0.14f);   // far enough forward that the collapsed head stays behind the camera
        holder.Position = new Vector3(eye.X, -eye.Y, eye.Z);   // (turned 180°: the character's +Z is our -Z)
    }

    /// <summary>Each finger as its chain of joints, knuckle first (Synty hands: index, and "middle" for the other three).</summary>
    private static int[][] Fingers(Skeleton3D s, string side) =>
        new[] { "Index", "Middle" }.Select(f => new[] { "Proximal", "Intermediate", "Distal" }.Select(p => s.FindBone(side + f + p))
            .Where(i => i >= 0).ToArray()).Where(c => c.Length > 0).ToArray();

    /// <summary>Poses both arms for this frame.</summary>
    public void Pose(HandGoal right, HandGoal left)
    {
        if (!Ready) return;
        var toSkel = _s!.GlobalTransform.AffineInverse();
        Solve(_right, right, toSkel, _rightFingers, _rThumb);
        Solve(_left, left, toSkel, _leftFingers, _lThumb);
    }

    private void Solve(Arm a, HandGoal g, Transform3D toSkel, int[][] fingers, int thumb)
    {
        var s = _s!;
        Vector3 Dir(Vector3 v) => (toSkel.Basis * v).Normalized();
        var dir = Dir(g.Dir);
        var palm = Dir(g.Palm);
        palm = (palm - dir * palm.Dot(dir)).Normalized();
        // the wrist sits behind the grip: the palm's centre is ~6 cm along the hand, ~3 cm off the gun
        var grip = toSkel * g.Grip;
        var wrist = grip - dir * 0.065f - palm * 0.03f;

        int parent = s.GetBoneParent(a.Up);
        var pg = parent >= 0 ? s.GetBoneGlobalPose(parent) : Transform3D.Identity;
        var restUp = s.GetBoneRest(a.Up);
        var shoulder = pg * restUp.Origin;

        // two-bone IK: the elbow on the circle where both bones reach, toward the pole. A gun held out further than
        // the arm reaches stretches the arm a little (up to 30 %, the old view-model trick) rather than letting go.
        var to = wrist - shoulder;
        float reach = (a.L1 + a.L2) * 0.97f;
        float stretch = Mathf.Clamp(to.Length() / reach, 1f, 1.3f);
        float l1 = a.L1 * stretch, l2 = a.L2 * stretch;
        s.SetBonePoseScale(a.Up, Vector3.One * stretch);
        s.SetBonePoseScale(a.Hand, Vector3.One / stretch);   // the hand keeps its size
        float d = Mathf.Clamp(to.Length(), 0.05f, (l1 + l2) * 0.999f);
        var u = to.Normalized();
        float along = (l1 * l1 - l2 * l2 + d * d) / (2 * d);
        float h = MathF.Sqrt(MathF.Max(0, l1 * l1 - along * along));
        var pole = Dir(g.Pole);
        var n = (pole - u * pole.Dot(u));
        n = n.LengthSquared() < 1e-6f ? Vector3.Down : n.Normalized();
        var elbow = shoulder + u * along + n * h;
        var hand = shoulder + u * d;

        // upper arm: turn its rest direction onto shoulder→elbow
        var upAtRest = pg.Basis * restUp.Basis;
        var gUp = Turn(upAtRest * s.GetBoneRest(a.Lo).Origin, elbow - shoulder) * upAtRest;
        s.SetBonePoseRotation(a.Up, (pg.Basis.Inverse() * gUp).Orthonormalized().GetRotationQuaternion());
        // forearm: onto elbow→wrist
        var restLo = s.GetBoneRest(a.Lo);
        var loAtRest = gUp * restLo.Basis;
        var gLo = Turn(loAtRest * s.GetBoneRest(a.Hand).Origin, hand - elbow) * loAtRest;
        s.SetBonePoseRotation(a.Lo, (gUp.Inverse() * gLo).Orthonormalized().GetRotationQuaternion());
        // hand: rest (wrist→knuckles, palm down in the T-pose) onto the goal's direction and palm
        var restHand = s.GetBoneRest(a.Hand);
        var handAtRest = gLo * restHand.Basis;
        var restDir = (handAtRest * s.GetBoneRest(a.Mid).Origin).Normalized();
        var restPalm = (gLo * restHand.Basis * (s.GetBoneGlobalRest(a.Hand).Basis.Inverse() * Vector3.Down)).Normalized();
        var gHand = Frame(dir, palm) * Frame(restDir, restPalm).Transposed() * handAtRest;
        // the roll about the forearm goes into the forearm, not the wrist: a real forearm twists along its length
        // (the radius turns over the ulna); all of it at the wrist pinches the skin like a sweet wrapper (Tom spotted it)
        var delta = ((gLo.Inverse() * gHand).Orthonormalized() * restHand.Basis.Orthonormalized().Inverse()).GetRotationQuaternion();
        var twist = Twist(delta, restHand.Origin.Normalized());
        gLo *= new Basis(Quaternion.Identity.Slerp(twist, 0.8f));
        s.SetBonePoseRotation(a.Lo, (gUp.Inverse() * gLo).Orthonormalized().GetRotationQuaternion());
        s.SetBonePoseRotation(a.Hand, (gLo.Inverse() * gHand).Orthonormalized().GetRotationQuaternion());

        // fingers: each joint bends a little more toward the palm (the bends add up along the finger); the chain is
        // carried here, not read back from the skeleton, whose global poses update later
        var across = dir.Cross(palm).Normalized();   // (checked: this way the fingers wrap the grip)
        foreach (var chain in fingers)
        {
            var parentG = gHand;
            foreach (var bone in chain) parentG = Bend(bone, parentG, across, 0.55f * g.Curl);
        }
        if (thumb >= 0 && s.GetBoneParent(thumb) is var tp and >= 0)
        {
            // the thumb's own parent (the metacarpal) under the posed hand
            var meta = gHand * s.GetBoneRest(tp).Basis;
            if (tp != a.Hand) s.SetBonePoseRotation(tp, s.GetBoneRest(tp).Basis.GetRotationQuaternion());
            Bend(thumb, tp == a.Hand ? gHand : meta, across, 0.45f * g.Curl);
        }
    }

    /// <summary>Bends a bone about a skeleton-space axis on top of its rest pose under a parent whose global basis is
    /// <paramref name="parentG"/>; returns the bone's new global basis.</summary>
    private Basis Bend(int bone, Basis parentG, Vector3 axis, float angle)
    {
        var s = _s!;
        var g = new Basis(axis, angle) * parentG * s.GetBoneRest(bone).Basis;
        s.SetBonePoseRotation(bone, (parentG.Inverse() * g).Orthonormalized().GetRotationQuaternion());
        return g.Orthonormalized();
    }

    /// <summary>The twist part of <paramref name="q"/> about <paramref name="axis"/> (swing-twist decomposition).</summary>
    private static Quaternion Twist(Quaternion q, Vector3 axis)
    {
        var v = new Vector3(q.X, q.Y, q.Z);
        var p = axis * v.Dot(axis);
        var t = new Quaternion(p.X, p.Y, p.Z, q.W);
        return t.LengthSquared() < 1e-10f ? Quaternion.Identity : t.Normalized();
    }

    /// <summary>The rotation turning <paramref name="from"/> onto <paramref name="to"/> (shortest arc).</summary>
    private static Basis Turn(Vector3 from, Vector3 to)
    {
        var a = from.Normalized(); var b = to.Normalized();
        float c = a.Dot(b);
        if (c > 0.99999f) return Basis.Identity;
        var axis = a.Cross(b);
        if (axis.LengthSquared() < 1e-10f) axis = a.Cross(Vector3.Up).LengthSquared() > 1e-6f ? a.Cross(Vector3.Up) : a.Cross(Vector3.Right);
        return new Basis(axis.Normalized(), MathF.Acos(Mathf.Clamp(c, -1f, 1f)));
    }

    /// <summary>An orthonormal frame from a direction and a roughly perpendicular second vector.</summary>
    private static Basis Frame(Vector3 dir, Vector3 second)
    {
        var x = dir.Normalized();
        var z = x.Cross(second).Normalized();
        var y = z.Cross(x);
        return new Basis(x, y, z);
    }
}
