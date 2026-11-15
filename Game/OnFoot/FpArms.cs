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
    private ArmIk? _right, _left;

    public bool Ready => _s != null && _right!.Ok && _left!.Ok;

    /// <summary>Synty's hands are chunky; seen this close they fill the view.</summary>
    public const float HandScale = 0.85f;
    /// <summary>Self-tests: the right arm's shoulder, elbow and wrist in the eye's frame, written while <see cref="DebugOn"/>.</summary>
    public string DebugRight = "";
    public bool DebugOn;

    public FpArms()
    {
        Name = "FpArms";
        Visible = false;   // shown only while a gun is out (Weapons.PoseArms)
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
            if (!mi.Visible) mi.QueueFree();
        }
        // the character faces +Z, the camera looks down -Z
        var holder = new Node3D { Name = "Body", RotationDegrees = new Vector3(0, 180, 0) };
        AddChild(holder);
        holder.AddChild(skel);
        _s = skel;
        _right = new ArmIk(skel, "Right") { HandScale = HandScale };
        _left = new ArmIk(skel, "Left") { HandScale = HandScale };
        // seen from inside the head: no head, no legs
        foreach (var b in new[] { "Head", "LeftUpperLeg", "RightUpperLeg" })
            if (skel.FindBone(b) is var i and >= 0) skel.SetBonePoseScale(i, Vector3.One * 0.001f);
        // the eye sits where the head was, far enough ahead of the face that the collapsed head stays behind the camera
        var eye = skel.GetBoneGlobalRest(skel.FindBone("Head")).Origin + new Vector3(0, 0.06f, 0.14f);
        holder.Position = new Vector3(eye.X, -eye.Y, eye.Z);   // (turned 180°: the character's +Z is our -Z)
    }

    /// <summary>Poses both arms for this frame.</summary>
    public void Pose(HandGoal right, HandGoal left)
    {
        if (!Ready) return;
        _right!.Solve(right);
        _left!.Solve(left);
        if (DebugOn)
        {
            var eyeT = GetParent<Node3D>().GlobalTransform.AffineInverse() * _s!.GlobalTransform;
            DebugRight = $"shoulder {eyeT * _right.Shoulder} elbow {eyeT * _right.Elbow} wrist {eyeT * _right.Wrist} reach {_right.Reach:F2}";
        }
    }
}
