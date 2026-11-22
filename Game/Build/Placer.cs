using System;
using System.Linq;
using BalticDrive.Core;
using BalticDrive.Items;
using BalticDrive.World;
using Godot;

namespace BalticDrive.Build;

/// <summary>
/// Placing what you build (D128, Tom: "ghost preview"). A build recipe from the notebook closes the window and puts
/// a see-through ghost of the thing where you look, within 4 m: green where it fits (on fairly level ground or floor,
/// clear of walls, cars, people and other things; a barricade may touch walls, it's nailed to them), red where it
/// doesn't, with the reason. R turns it 22.5°; the mouse wheel lifts a barricade up to 1.2 m (over a window); left
/// click or F builds it there; Esc or right click puts it away. Then you build: the recipe's seconds (faster with its
/// skill), hammering; a step away or a hit stops it, and nothing is used up until it's done.
/// </summary>
public sealed partial class Placer : Node3D
{
    public const float MaxReach = 4f, TurnStep = MathF.PI / 8, StepAway = 2.5f;

    public Recipe? Recipe { get; private set; }
    public PieceDef? Def { get; private set; }
    public bool Placing => Recipe != null && Job == null;
    /// <summary>Building: where, how far along.</summary>
    public (Transform3D At, float Time, float Done, Vector3 From, float Health)? Job { get; private set; }
    public string Why { get; private set; } = "";
    public bool Fits { get; private set; }
    public Transform3D At { get; private set; }

    private Node3D? _ghost;
    private float _turn, _lift;
    private StandardMaterial3D? _ok, _bad, _frame;
    private PhysicsShapeQueryParameters3D? _query;
    private PhysicsRayQueryParameters3D? _ray;

    /// <summary>A build recipe was picked: its ghost appears.</summary>
    public void Begin(Recipe r, PieceDef d)
    {
        Cancel();
        Recipe = r; Def = d; _turn = 0; _lift = 0;
        _ok ??= Ghost(new Color(0.3f, 1f, 0.4f, 0.35f)); _bad ??= Ghost(new Color(1f, 0.25f, 0.2f, 0.35f)); _frame ??= Ghost(new Color(0.9f, 0.8f, 0.5f, 0.45f));
        _ghost = Built.Shape(d, _ok);
        AddChild(_ghost);
        Log.Info("Build", $"placing {d.Id}");
    }

    public void Cancel()
    {
        _ghost?.QueueFree(); _ghost = null;
        Recipe = null; Def = null; Job = null;
    }

    public void Turn(int dir) => _turn += dir * TurnStep;
    public void Lift(float dy) { if (Def?.Kind == "barricade") _lift = Math.Clamp(_lift + dy, 0, 1.2f); }

    /// <summary>Each frame while placing: the ghost follows your look.</summary>
    public void Follow(OnFoot.Walker w, Camera3D cam)
    {
        if (!Placing || _ghost == null || Def == null) return;
        var space = GetWorld3D().DirectSpaceState;
        var from = cam.GlobalPosition;
        var dir = -cam.GlobalBasis.Z;
        _ray ??= new PhysicsRayQueryParameters3D { CollisionMask = Bodies.Ground | Bodies.Roads | Bodies.Buildings | Bodies.Interior };
        _ray.From = from; _ray.To = from + dir * 12f;
        _ray.Exclude = new Godot.Collections.Array<Rid> { w.GetRid() };
        var hit = space.IntersectRay(_ray);
        var feet = w.GlobalPosition;
        Vector3 spot; Vector3 normal = Vector3.Up;
        if (hit.Count > 0) { spot = (Vector3)hit["position"]; normal = (Vector3)hit["normal"]; }
        else spot = feet + (dir with { Y = 0 }).Normalized() * 2.5f;
        // within reach: pulled back towards you along the ground
        var flat = (spot - feet) with { Y = 0 };
        if (flat.Length() > MaxReach) { var f2 = flat.Normalized() * MaxReach; spot = new Vector3(feet.X + f2.X, spot.Y, feet.Z + f2.Z); }
        bool wall = normal.Y < 0.6f;
        float yaw = w.Yaw + MathF.PI + _turn;   // facing you
        if (wall)
        {
            // against a wall: a barricade lies flat on it, anything else stands in front of it
            yaw = MathF.Atan2(normal.X, normal.Z) + _turn;
            spot += normal * (Def.Size.Z / 2 + 0.03f);
        }
        // down to the floor or ground under it
        _ray.From = spot + Vector3.Up * 0.6f; _ray.To = spot + Vector3.Down * 3f;
        var down = space.IntersectRay(_ray);
        float floorY = down.Count > 0 ? ((Vector3)down["position"]).Y : spot.Y;
        var floorN = down.Count > 0 ? (Vector3)down["normal"] : Vector3.Up;
        var at = new Transform3D(new Basis(Vector3.Up, yaw), new Vector3(spot.X, floorY + _lift, spot.Z));
        At = at;
        _ghost.GlobalTransform = at;
        // does it fit?
        Why = "";
        if (down.Count == 0) Why = Lang.T("build.no_ground");
        else if (floorN.Y < 0.9f && _lift <= 0) Why = Lang.T("build.slope");
        else if ((at.Origin - feet).Length() < 0.6f) Why = Lang.T("build.too_close");
        else if (Def.Kind == "collector" && Main.Instance?.Indoors == true) Why = Lang.T("build.outdoors");
        else if (WorldOrigin.ToWorld(at.Origin) is var (wx, _, wz) && !float.IsNaN(Main.Instance?.World?.Water(wx, wz) ?? float.NaN)) Why = Lang.T("build.water");
        else
        {
            var size = Def.Size;
            uint mask = Bodies.Props | Bodies.Vehicles | Bodies.Characters | (Def.Kind == "barricade" ? 0 : Bodies.Buildings | Bodies.Interior);
            _query ??= new PhysicsShapeQueryParameters3D();
            _query.Shape = new BoxShape3D { Size = new Vector3(MathF.Max(0.1f, size.X - 0.1f), MathF.Max(0.1f, size.Y - 0.15f), MathF.Max(0.05f, size.Z - 0.04f)) };
            _query.Transform = new Transform3D(at.Basis, at.Origin + Vector3.Up * (size.Y / 2 + 0.06f));
            _query.CollisionMask = mask;
            _query.Exclude = new Godot.Collections.Array<Rid> { w.GetRid() };
            if (space.IntersectShape(_query, 1).Count > 0) Why = Lang.T("build.in_the_way");
        }
        Fits = Why.Length == 0;
        SetGhost(Fits ? _ok! : _bad!);
    }

    /// <summary>Left click or F: start building it where the ghost stands.</summary>
    public bool Confirm(OnFoot.Walker w)
    {
        if (!Placing || !Fits || Recipe == null) return false;
        if (Recipes.Missing(Recipe, w.Body.Pack) is { Count: > 0 } miss) { Why = Lang.T("inv.you_need", string.Join(", ", miss)); return false; }
        Job = (At, Recipes.SecondsFor(Recipe), 0, w.GlobalPosition, w.Body.Health);
        SetGhost(_frame!);
        Log.Info("Build", $"building {Def!.Id}: {Job.Value.Time:0} s");
        return true;
    }

    /// <summary>Each frame while building: done → the inputs used and the thing built; returns a note when it ended.</summary>
    public string? Work(OnFoot.Walker w, Built built, float dt)
    {
        if (Job is not { } j || Recipe == null || Def == null) return null;
        if (w.Body.Health < j.Health - 0.5f) { var n = Lang.T("build.hit", Def.Name); Cancel(); return n; }
        if ((w.GlobalPosition - j.From).Length() > StepAway) { var n = Lang.T("build.stopped", Def.Name); Cancel(); return n; }
        j.Done += dt;
        Job = j;
        if (_ghost != null) _ghost.Scale = new Vector3(1, Math.Clamp(0.3f + 0.7f * j.Done / j.Time, 0.3f, 1f), 1);   // it rises as you work
        if (j.Done < j.Time) return null;
        if (Recipes.Missing(Recipe, w.Body.Pack) is { Count: > 0 } miss) { Cancel(); return Lang.T("inv.you_need", string.Join(", ", miss)); }
        foreach (var (item, n) in Recipe.Inputs) w.Body.Pack.UseUp(item, n, Recipe.Leftovers);
        built.Add(Def, j.At);
        Skills.Add(Recipe.Skill, 12);
        var said = Lang.T("build.done", Def.Name);
        Cancel();
        return said;
    }

    public float Progress => Job is { } j ? Math.Clamp(j.Done / j.Time, 0, 1) : 0;

    private void SetGhost(Material m)
    {
        if (_ghost == null) return;
        foreach (var mi in _ghost.FindChildren("*", "MeshInstance3D", true, false).OfType<MeshInstance3D>()) mi.MaterialOverride = m;
    }

    private static StandardMaterial3D Ghost(Color c) => new()
    {
        AlbedoColor = c, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        CullMode = BaseMaterial3D.CullModeEnum.Disabled, NoDepthTest = false,
    };
}
