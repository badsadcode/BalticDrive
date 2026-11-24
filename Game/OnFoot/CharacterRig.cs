using System;
using System.Linq;
using Godot;

namespace BalticDrive.OnFoot;

/// <summary>
/// A Synty City character on the humanoid skeleton with the Base Locomotion clips blended by velocity (D38, D39), made
/// reusable for bandits (D47). <see cref="Build"/> puts one of the City pack's characters under a node (turned to face
/// -Z like every Godot node); <see cref="Animate"/> feeds its AnimationTree each frame.
/// </summary>
public static class CharacterRig
{
    public const string Scene = "res://Assets/Synty/PolygonCity/Prefabs/Characters/Character_Male_Hoodie.tscn";
    public const string ClipFolder = "res://Assets/Synty/AnimationBaseLocomotion/";
    public const float WalkSpeed = 1.7f, RunSpeed = 4.2f, SprintSpeed = 6.6f, CrouchSpeed = 1.5f;

    /// <summary>Builds the character <paramref name="mesh"/> (e.g. "Character_Male_Hoodie") under <paramref name="model"/>.
    /// Returns its skeleton and animation tree; the tree is null when the clips aren't installed (then
    /// <paramref name="cycle"/> is a procedural walk), the skeleton null when the pack isn't (no model at all).</summary>
    public static (Skeleton3D? Skel, AnimationTree? Tree) Build(Node3D model, string mesh, out WalkCycle? cycle)
    {
        cycle = null;
        if (!ResourceLoader.Exists(Scene) || ResourceLoader.Load<PackedScene>(Scene) is not { } scene) return (null, null);
        var prefab = scene.Instantiate<Node3D>();
        var skel = prefab.GetNodeOrNull<Skeleton3D>("GeneralSkeleton");
        if (skel == null) { prefab.QueueFree(); return (null, null); }
        prefab.RemoveChild(skel);
        prefab.QueueFree();
        // the prefab carries every City character; keep the one asked for
        foreach (var child in skel.GetChildren())
            if (child is MeshInstance3D mi)
            {
                mi.Visible = mi.Name == mesh;
                mi.Skeleton = new NodePath("..");   // the prefab leaves it empty: unbound, the mesh stays in its rest pose
                if (!mi.Visible) mi.QueueFree();
            }
        ClearOwner(skel);
        model.AddChild(skel);
        // the clips address it as %GeneralSkeleton: a unique name under the model
        skel.Owner = model;
        skel.UniqueNameInOwner = true;
        // Synty characters face +Z; Godot nodes face -Z
        model.RotationDegrees = new Vector3(0, 180, 0);
        var tree = Animations(model);
        if (tree == null) cycle = new WalkCycle(skel);
        return (skel, tree);
    }

    /// <summary>A BadCharacters person (D129): the GLB under <paramref name="model"/> (it faces +Z like Synty's, so the
    /// same half turn), its own skeleton (Unity-named bones, as ours) and its own clips in the same locomotion tree.
    /// BadCharacters has no backward or sideways walks: back is the forward clip played backwards, sideways the
    /// forward one.</summary>
    public static (Skeleton3D? Skel, AnimationTree? Tree) BuildPerson(Node3D model, string glb)
    {
        if (!ResourceLoader.Exists(glb) || ResourceLoader.Load<PackedScene>(glb) is not { } scene) return (null, null);
        var root = scene.Instantiate<Node3D>();
        model.AddChild(root);
        model.RotationDegrees = new Vector3(0, 180, 0);
        var skel = root.FindChildren("*", "Skeleton3D", true, false).OfType<Skeleton3D>().FirstOrDefault();
        var player = root.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().FirstOrDefault();
        if (skel == null) { root.QueueFree(); return (null, null); }
        if (player == null) return (skel, null);
        foreach (var c in new[] { "Idle", "Walk", "Run", "Sprint", "Crouch", "CrouchWalk" })
            if (player.HasAnimation(c)) player.GetAnimation(c).LoopMode = Animation.LoopModeEnum.Linear;
        (string, bool) Map(string key) => key switch
        {
            "idle" => ("Idle", false), "walk" or "walk_l" or "walk_r" => ("Walk", false), "run" or "run_l" or "run_r" => ("Run", false),
            "sprint" => ("Sprint", false), "walk_b" => ("Walk", true), "run_b" => ("Run", true),
            "crouch_idle" => ("Crouch", false), "crouch_f" => ("CrouchWalk", false), "crouch_b" => ("CrouchWalk", true),
            _ => ("Idle", false),   // falling: standing still in the air
        };
        return (skel, Tree(root, player, Map));
    }

    private static void ClearOwner(Node n)
    {
        n.Owner = null;
        foreach (var c in n.GetChildren()) ClearOwner(c);
    }

    private static AnimationLibrary? _lib;

    /// <summary>The clips, loaded once and shared by every character.</summary>
    private static AnimationLibrary? Library()
    {
        if (_lib != null) return _lib;
        var lib = new AnimationLibrary();
        bool Clip(string key, string file, bool loop = true)
        {
            string path = ClipFolder + file + ".fbx";
            if (!ResourceLoader.Exists(path) || ResourceLoader.Load<PackedScene>(path) is not { } scene) return false;
            var inst = scene.Instantiate<Node>();
            var player = inst.FindChildren("*", "AnimationPlayer", true, false).OfType<AnimationPlayer>().FirstOrDefault();
            var name = player?.GetAnimationList().FirstOrDefault();
            if (player == null || name == null) { inst.QueueFree(); return false; }
            var anim = (Animation)player.GetAnimation(name).Duplicate();
            inst.QueueFree();
            anim.LoopMode = loop ? Animation.LoopModeEnum.Linear : Animation.LoopModeEnum.None;
            lib.AddAnimation(key, anim);
            return true;
        }
        if (!Clip("idle", "A_Idle_Standing_Masc") || !Clip("walk", "A_Walk_F_Masc") || !Clip("run", "A_Run_F_Masc")) return null;
        Clip("sprint", "A_Sprint_F_Masc"); Clip("walk_b", "A_Walk_BckStrafeB_Masc"); Clip("run_b", "A_Run_BckStrafeB_Masc");
        Clip("walk_l", "A_Walk_FwdStrafeL_Masc"); Clip("walk_r", "A_Walk_FwdStrafeR_Masc");
        Clip("run_l", "A_Run_FwdStrafeL_Masc"); Clip("run_r", "A_Run_FwdStrafeR_Masc");
        Clip("crouch_idle", "A_Idle_Crouching_Masc"); Clip("crouch_f", "A_Crouch_FwdStrafeF_Masc"); Clip("crouch_b", "A_Crouch_BckStrafeB_Masc");
        Clip("fall", "A_InAir_FallShort_Masc");
        return _lib = lib;
    }

    /// <summary>
    /// The locomotion tree (D39): standing (idle, walk, run, sprint forwards; walk and run back and sideways) and
    /// crouching (idle, forward, back) blend spaces by the velocity in the body's frame, a crouch and a fall blend.
    /// </summary>
    private static AnimationTree? Animations(Node3D model)
    {
        var lib = Library();
        if (lib == null) return null;
        var animPlayer = new AnimationPlayer { Name = "AnimationPlayer" };
        model.AddChild(animPlayer);
        animPlayer.RootNode = new NodePath("..");
        animPlayer.AddAnimationLibrary("", lib);
        return Tree(model, animPlayer, key => (lib.HasAnimation(key) ? key : "idle", false));
    }

    /// <summary>The tree over <paramref name="animPlayer"/>, under <paramref name="parent"/>; <paramref name="clip"/>
    /// names the clip for each of our keys, and whether it plays backwards.</summary>
    private static AnimationTree Tree(Node parent, AnimationPlayer animPlayer, Func<string, (string Name, bool Back)> clip)
    {
        AnimationNodeAnimation A(string key)
        {
            var (name, back) = clip(key);
            return new() { Animation = name, PlayMode = back ? AnimationNodeAnimation.PlayModeEnum.Backward : AnimationNodeAnimation.PlayModeEnum.Forward };
        }
        var stand = new AnimationNodeBlendSpace2D { MinSpace = new Vector2(-7, -7), MaxSpace = new Vector2(7, 7) };
        stand.AddBlendPoint(A("idle"), Vector2.Zero);
        stand.AddBlendPoint(A("walk"), new Vector2(0, WalkSpeed));
        stand.AddBlendPoint(A("run"), new Vector2(0, RunSpeed));
        stand.AddBlendPoint(A("sprint"), new Vector2(0, SprintSpeed));
        stand.AddBlendPoint(A("walk_b"), new Vector2(0, -WalkSpeed));
        stand.AddBlendPoint(A("run_b"), new Vector2(0, -RunSpeed));
        stand.AddBlendPoint(A("walk_l"), new Vector2(-WalkSpeed, 0));
        stand.AddBlendPoint(A("walk_r"), new Vector2(WalkSpeed, 0));
        stand.AddBlendPoint(A("run_l"), new Vector2(-RunSpeed, 0));
        stand.AddBlendPoint(A("run_r"), new Vector2(RunSpeed, 0));
        // crouching: a 1D blend on forward speed. (It was a 2D blend space whose three points all lie on one line: no
        // triangle, so it played nothing and the body fell into its T-pose, D52, Tom: "crouching doesn't work".)
        var crouch = new AnimationNodeBlendSpace1D { MinSpace = -3, MaxSpace = 3 };
        crouch.AddBlendPoint(A("crouch_b"), -CrouchSpeed);
        crouch.AddBlendPoint(A("crouch_idle"), 0);
        crouch.AddBlendPoint(A("crouch_f"), CrouchSpeed);
        var bt = new AnimationNodeBlendTree();
        bt.AddNode("stand", stand);
        bt.AddNode("crouchbs", crouch);
        bt.AddNode("crouch", new AnimationNodeBlend2());
        bt.AddNode("fall", A("fall"));
        bt.AddNode("air", new AnimationNodeBlend2());
        bt.ConnectNode("crouch", 0, "stand");
        bt.ConnectNode("crouch", 1, "crouchbs");
        bt.ConnectNode("air", 0, "crouch");
        bt.ConnectNode("air", 1, "fall");
        bt.ConnectNode("output", 0, "air");
        var tree = new AnimationTree { Name = "AnimationTree", TreeRoot = bt };
        parent.AddChild(tree);
        tree.AnimPlayer = tree.GetPathTo(animPlayer);
        tree.Active = true;
        return tree;
    }

    /// <summary>
    /// Keeps the feet on the ground while crouching (D52): Synty's crouch clips bend the legs but leave the hips at
    /// standing height, so the body hovered. The model is lowered until the lower toe sits where it does standing,
    /// weighted by <paramref name="weight"/> (the crouch blend); returns the drop for the next frame.
    /// </summary>
    public static void GroundFeet(Node3D model, Skeleton3D skel, float weight, float dt)
    {
        int l = skel.FindBone("LeftToes"), r = skel.FindBone("RightToes");
        if (l < 0 || r < 0) return;
        float baseY = model.Position.Y;
        float Y(int b) => (skel.GlobalTransform * skel.GetBoneGlobalPose(b).Origin).Y - model.GlobalPosition.Y + baseY;
        float RestY(int b) => skel.GetBoneGlobalRest(b).Origin.Y * skel.Scale.Y;
        float lift = MathF.Min(Y(l), Y(r)) - baseY - MathF.Min(RestY(l), RestY(r));
        float want = weight > 0.01f ? -MathF.Max(0, lift) * weight : 0;
        model.Position = model.Position with { Y = Mathf.Lerp(baseY, want, 1f - MathF.Exp(-20f * dt)) };
    }

    /// <summary>One frame of the tree: <paramref name="v"/> is the velocity in the body's frame (x right, y forward).</summary>
    public static void Animate(AnimationTree tree, Vector2 v, float crouch, float air)
    {
        tree.Set("parameters/stand/blend_position", v);
        tree.Set("parameters/crouchbs/blend_position", v.Y);
        tree.Set("parameters/crouch/blend_amount", crouch);
        tree.Set("parameters/air/blend_amount", air);
    }
}
