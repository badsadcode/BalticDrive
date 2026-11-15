using System;
using System.Linq;
using Godot;

namespace BalticDrive.OnFoot;

/// <summary>One hand's goal, in world space: where the grip is, which way the hand points (wrist to knuckles), which
/// way the palm faces, which way the elbow should go, and how far the fingers curl (0..1).</summary>
public readonly record struct HandGoal(Vector3 Grip, Vector3 Dir, Vector3 Palm, Vector3 Pole, float Curl);

/// <summary>
/// Two-bone IK for one arm of a humanoid skeleton (D41, D43, D45; shared by the first-person arms and bandits, D47).
/// The elbow goes on the circle where both bones reach, toward the pole; each bone's rest direction is turned onto
/// its new one; the hand gets its direction and palm; 80 % of the hand's roll goes into the forearm; fingers and
/// thumb curl round the grip. All in the skeleton's own space, from the parent's current global pose (so it works
/// on top of an animation, inside a <see cref="SkeletonModifier3D"/>).
/// </summary>
public sealed class ArmIk
{
    private readonly Skeleton3D _s;
    private readonly int _up, _lo, _hand, _mid, _thumb;
    private readonly float _l1, _l2;
    private readonly int[][] _fingers;
    public float HandScale = 1f;
    /// <summary>The last solve's shoulder, elbow and wrist (skeleton space), for self-tests.</summary>
    public Vector3 Shoulder, Elbow, Wrist;

    public ArmIk(Skeleton3D s, string side)
    {
        _s = s;
        _up = s.FindBone(side + "UpperArm"); _lo = s.FindBone(side + "LowerArm"); _hand = s.FindBone(side + "Hand");
        _mid = s.FindBone(side + "MiddleProximal");
        _thumb = s.FindBone(side + "ThumbProximal");
        _l1 = _up < 0 || _lo < 0 ? 0 : s.GetBoneGlobalRest(_up).Origin.DistanceTo(s.GetBoneGlobalRest(_lo).Origin);
        _l2 = _lo < 0 || _hand < 0 ? 0 : s.GetBoneGlobalRest(_lo).Origin.DistanceTo(s.GetBoneGlobalRest(_hand).Origin);
        // each finger as its chain of joints, knuckle first (Synty hands: index, and "middle" for the other three)
        _fingers = new[] { "Index", "Middle" }.Select(f => new[] { "Proximal", "Intermediate", "Distal" }.Select(p => s.FindBone(side + f + p))
            .Where(i => i >= 0).ToArray()).Where(c => c.Length > 0).ToArray();
    }

    public bool Ok => _up >= 0 && _lo >= 0 && _hand >= 0 && _mid >= 0 && _l1 > 0 && _l2 > 0;
    public float Reach => _l1 + _l2;

    public void Solve(HandGoal g)
    {
        if (!Ok) return;
        var s = _s;
        var toSkel = s.GlobalTransform.AffineInverse();
        Vector3 Dir(Vector3 v) => (toSkel.Basis * v).Normalized();
        var dir = Dir(g.Dir);
        var palm = Dir(g.Palm);
        palm = (palm - dir * palm.Dot(dir)).Normalized();
        // the wrist sits behind the grip: the palm's centre is ~6 cm along the hand, ~3 cm off the gun
        var grip = toSkel * g.Grip;
        var wrist = grip - dir * 0.065f - palm * 0.03f;

        int parent = s.GetBoneParent(_up);
        var pg = parent >= 0 ? s.GetBoneGlobalPose(parent) : Transform3D.Identity;
        var restUp = s.GetBoneRest(_up);
        var shoulder = pg * restUp.Origin;

        // a gun held out further than the arm reaches stretches the arm a little (up to 30 %, the old view-model
        // trick) rather than letting go
        var to = wrist - shoulder;
        float reach = (_l1 + _l2) * 0.97f;
        float stretch = Mathf.Clamp(to.Length() / reach, 1f, 1.3f);
        float l1 = _l1 * stretch, l2 = _l2 * stretch;
        s.SetBonePoseScale(_up, Vector3.One * stretch);
        s.SetBonePoseScale(_hand, Vector3.One * HandScale / stretch);   // the hand keeps its size
        float d = Mathf.Clamp(to.Length(), 0.05f, (l1 + l2) * 0.999f);
        var u = to.Normalized();
        float along = (l1 * l1 - l2 * l2 + d * d) / (2 * d);
        float h = MathF.Sqrt(MathF.Max(0, l1 * l1 - along * along));
        var pole = Dir(g.Pole);
        var n = pole - u * pole.Dot(u);
        n = n.LengthSquared() < 1e-6f ? Vector3.Down : n.Normalized();
        var elbow = shoulder + u * along + n * h;
        var hand = shoulder + u * d;
        Shoulder = shoulder; Elbow = elbow; Wrist = wrist;

        // upper arm: turn its rest direction onto shoulder→elbow
        var upAtRest = pg.Basis * restUp.Basis;
        var gUp = Turn(upAtRest * s.GetBoneRest(_lo).Origin, elbow - shoulder) * upAtRest;
        s.SetBonePoseRotation(_up, (pg.Basis.Inverse() * gUp).Orthonormalized().GetRotationQuaternion());
        // forearm: onto elbow→wrist
        var restLo = s.GetBoneRest(_lo);
        var loAtRest = gUp * restLo.Basis;
        var gLo = Turn(loAtRest * s.GetBoneRest(_hand).Origin, hand - elbow) * loAtRest;
        // hand: rest (wrist→knuckles, palm down in the T-pose) onto the goal's direction and palm
        var restHand = s.GetBoneRest(_hand);
        var handAtRest = gLo * restHand.Basis;
        var restDir = (handAtRest * s.GetBoneRest(_mid).Origin).Normalized();
        var restPalm = (gLo * restHand.Basis * (s.GetBoneGlobalRest(_hand).Basis.Inverse() * Vector3.Down)).Normalized();
        var gHand = Frame(dir, palm) * Frame(restDir, restPalm).Transposed() * handAtRest;
        // the roll about the forearm goes into the forearm, not the wrist (D43)
        var delta = ((gLo.Inverse() * gHand).Orthonormalized() * restHand.Basis.Orthonormalized().Inverse()).GetRotationQuaternion();
        var twist = Twist(delta, restHand.Origin.Normalized());
        gLo *= new Basis(Quaternion.Identity.Slerp(twist, 0.8f));
        s.SetBonePoseRotation(_lo, (gUp.Inverse() * gLo).Orthonormalized().GetRotationQuaternion());
        s.SetBonePoseRotation(_hand, (gLo.Inverse() * gHand).Orthonormalized().GetRotationQuaternion());

        // fingers: each joint bends a little more toward the palm; the chain is carried here, not read back from the
        // skeleton, whose global poses update later
        var across = dir.Cross(palm).Normalized();   // (checked: this way the fingers wrap the grip)
        foreach (var chain in _fingers)
        {
            var parentG = gHand;
            foreach (var bone in chain) parentG = Bend(bone, parentG, across, 0.55f * g.Curl);
        }
        if (_thumb >= 0 && s.GetBoneParent(_thumb) is var tp and >= 0)
        {
            var meta = gHand * s.GetBoneRest(tp).Basis;
            if (tp != _hand) s.SetBonePoseRotation(tp, s.GetBoneRest(tp).Basis.GetRotationQuaternion());
            // the thumb rolls over the top of the grip toward the palm side, then curls (D45)
            var thumbRoll = new Basis(dir, -1.1f * g.Curl);
            var parentT = tp == _hand ? gHand : meta;
            var gT = thumbRoll * new Basis(across, 0.45f * g.Curl) * parentT * s.GetBoneRest(_thumb).Basis;
            s.SetBonePoseRotation(_thumb, (parentT.Inverse() * gT).Orthonormalized().GetRotationQuaternion());
        }
    }

    private Basis Bend(int bone, Basis parentG, Vector3 axis, float angle)
    {
        var g = new Basis(axis, angle) * parentG * _s.GetBoneRest(bone).Basis;
        _s.SetBonePoseRotation(bone, (parentG.Inverse() * g).Orthonormalized().GetRotationQuaternion());
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
    public static Basis Turn(Vector3 from, Vector3 to)
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
