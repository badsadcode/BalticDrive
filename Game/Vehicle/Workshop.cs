using System;
using System.Collections.Generic;
using System.Linq;
using BalticDrive.Core;
using Godot;

namespace BalticDrive.Vehicle;

/// <summary>A car part off a car (D68): carried by the player, fitted into a car later.</summary>
public sealed record LoosePart(PartKind Kind, string Name, float Condition);

/// <summary>
/// The workshop view (D68, Tom: "like the car workshop simulator games"; hold F at the car). The camera orbits the car
/// (A/D or the mouse dragged with the left button round it, W/S up and down, the wheel in and out); every part shows as
/// a dot at its place, green → red by condition, grey when missing. Hover a part for its name and condition; click to
/// pick it, then Remove (it goes into your hands) or Fit one you carry of the same kind. Each takes a few seconds (a
/// tyre 5, an engine 30); moving away or Esc cancels. Esc or F closes the view.
/// </summary>
public sealed partial class Workshop : Control
{
    public Car? Car { get; private set; }
    public bool Open => Car != null;
    /// <summary>The camera's angle round the car (radians, world); the view starts at the car's front left, the
    /// driver's side.</summary>
    public float Yaw { get => _yaw; set => _yaw = value; }
    /// <summary>Parts the player carries (the car's boot, for now: a spare tyre to start with).</summary>
    public readonly List<LoosePart> Carried = new() { new LoosePart(PartKind.Tyre, Core.Lang.T("part.spare"), 100) };
    public event Action<string>? Said;

    private Camera3D _cam = null!;
    private Camera3D? _before;
    private float _yaw = 0.6f, _pitch = 0.35f, _dist = 7f;
    private CarPart? _hover, _picked;
    private (CarPart Part, LoosePart? Fit, float Time, float Done)? _job;
    private bool _dragging;
    private Vector2 _mouse;
    private readonly List<(Rect2 Box, Action Act)> _buttons = new();
    private Font _font = null!;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        Visible = false;
        _font = ThemeDB.FallbackFont;
        _cam = new Camera3D { Name = "WorkshopCamera", Fov = 60, Near = 0.05f, Far = 20000f };
    }

    public void Begin(Car car)
    {
        Car = car;
        _before = GetViewport().GetCamera3D();
        if (_cam.GetParent() == null) car.GetParent().AddChild(_cam);
        var fl = car.GlobalBasis * new Vector3(-1, 0, -0.8f);   // front left
        _yaw = MathF.Atan2(fl.X, fl.Z);
        _cam.Current = true;
        Visible = true;
        _picked = null; _job = null;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        Log.Info("Workshop", "open");
    }

    public void End()
    {
        if (Car == null) return;
        Car.ShowGhost(null);
        Car = null;
        Visible = false;
        _job = null;
        if (_before != null && IsInstanceValid(_before)) _before.Current = true;
        if (Main.CaptureMouse) Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    /// <summary>How long a job takes (seconds).</summary>
    public static float JobTime(PartKind k) => k switch
    {
        PartKind.Engine => 30, PartKind.Suspension => 12, PartKind.Windshield => 10, PartKind.Brakes => 8, PartKind.Door => 8,
        PartKind.Window => 6, PartKind.Hood => 6, PartKind.Trunk => 6, PartKind.Seat => 6, PartKind.Tyre => 5, PartKind.Battery => 4,
        _ => 5,
    };

    /// <summary>Starts removing or fitting (self-tests call it directly).</summary>
    public bool StartJob(CarPart part, LoosePart? fit = null)
    {
        if (Car == null || _job != null) return false;
        if (fit == null && part.Missing) return false;
        if (fit != null && (!part.Missing || fit.Kind != part.Kind || !Carried.Contains(fit))) return false;
        _job = (part, fit, JobTime(part.Kind) * Items.Skills.TimeFactor("mechanics"), 0);   // D113
        return true;
    }

    public bool Busy => _job != null;

    public override void _Process(double delta)
    {
        if (Car == null) return;
        if (!IsInstanceValid(Car)) { End(); return; }
        float dt = (float)delta;
        if (Input.IsKeyPressed(Key.A) || Input.IsKeyPressed(Key.Left)) _yaw += dt * 1.6f;
        if (Input.IsKeyPressed(Key.D) || Input.IsKeyPressed(Key.Right)) _yaw -= dt * 1.6f;
        if (Input.IsKeyPressed(Key.W) || Input.IsKeyPressed(Key.Up)) _pitch = Mathf.Clamp(_pitch + dt, -0.05f, 1.3f);
        if (Input.IsKeyPressed(Key.S) || Input.IsKeyPressed(Key.Down)) _pitch = Mathf.Clamp(_pitch - dt, -0.05f, 1.3f);
        var target = Car.GlobalPosition + Vector3.Up * 0.8f;
        var dir = new Vector3(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), MathF.Cos(_yaw) * MathF.Cos(_pitch));
        _cam.GlobalPosition = target + dir * _dist;
        _cam.LookAt(target, Vector3.Up);
        if (_job is { } j)
        {
            j.Done += dt;
            if (j.Done >= j.Time) { Finish(j.Part, j.Fit); _job = null; }
            else _job = j;
        }
        // the part under the mouse
        _hover = null;
        float best = 22f;
        foreach (var p in Car.Parts.All)
        {
            var at = Car.ToGlobal(Car.PartAnchor(p));
            if (_cam.IsPositionBehind(at)) continue;
            float d = _cam.UnprojectPosition(at).DistanceTo(_mouse);
            if (d < best) { best = d; _hover = p; }
        }
        Car.ShowGhost(_hover ?? _picked);
        QueueRedraw();
    }

    /// <summary>Picks a part as a click would (self-tests).</summary>
    public void Pick(CarPart? p) => _picked = p;

    private void Finish(CarPart part, LoosePart? fit)
    {
        if (Car == null) return;
        if (fit == null)
        {
            Carried.Add(new LoosePart(part.Kind, part.Name, part.Condition));
            part.Missing = true;
            Said?.Invoke(Core.Lang.T("ws.removed", part.Name, part.Condition.ToString("0")));
            Log.Info("Workshop", $"removed {part.Name} at {part.Condition:0} %");
        }
        else
        {
            Carried.Remove(fit);
            part.Missing = false;
            part.Condition = fit.Condition;
            Said?.Invoke(Core.Lang.T("ws.fitted", part.Name, fit.Condition.ToString("0")));
            Log.Info("Workshop", $"fitted {fit.Name} as {part.Name} at {fit.Condition:0} %");
        }
        Car.PartsChanged();
        Items.Skills.Add("mechanics", fit == null ? 8 : 12);   // practice (D113)
    }

    public override void _Input(InputEvent e)
    {
        if (Car == null) return;
        if (e is InputEventMouseMotion mm)
        {
            _mouse = mm.Position;
            if (_dragging) { _yaw -= mm.Relative.X * 0.008f; _pitch = Mathf.Clamp(_pitch + mm.Relative.Y * 0.006f, -0.05f, 1.3f); }
        }
        else if (e is InputEventMouseButton mb)
        {
            _mouse = mb.Position;
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) _dist = MathF.Max(3.5f, _dist - 0.5f);
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) _dist = MathF.Min(14f, _dist + 0.5f);
            else if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed)
                {
                    foreach (var (box, act) in _buttons) if (box.HasPoint(mb.Position)) { act(); GetViewport().SetInputAsHandled(); return; }
                    if (_hover != null) _picked = _hover; else _dragging = true;
                }
                else _dragging = false;
            }
        }
        else if (e is InputEventKey { Pressed: true, Echo: false } k && k.Keycode is Key.Escape or Key.F)
        {
            if (_job != null && k.Keycode == Key.Escape) _job = null;   // Esc cancels a job first
            else End();
        }
        GetViewport().SetInputAsHandled();   // nothing else gets input while the workshop is open
    }

    public override void _Draw()
    {
        if (Car == null) return;
        _buttons.Clear();
        var shadow = new Color(0, 0, 0, 0.85f);
        foreach (var p in Car.Parts.All)
        {
            var at = Car.ToGlobal(Car.PartAnchor(p));
            if (_cam.IsPositionBehind(at)) continue;
            var s = _cam.UnprojectPosition(at);
            var col = p.Missing ? new Color(0.5f, 0.5f, 0.5f) : new Color(1f - p.Health, 0.25f + 0.65f * p.Health, 0.2f).Lerp(new Color(0.9f, 0.15f, 0.1f), p.Health <= 0 ? 1 : 0);
            bool on = p == _hover || p == _picked;
            DrawCircle(s, on ? 11 : 8, shadow);
            DrawCircle(s, on ? 9 : 6, col);
            if (on)
            {
                string label = p.Missing ? $"{p.Name}: missing" : $"{p.Name}  {p.Condition:0} %";
                DrawString(_font, s + new Vector2(14, 6) + Vector2.One, label, HorizontalAlignment.Left, -1, 16, shadow);
                DrawString(_font, s + new Vector2(14, 6), label, HorizontalAlignment.Left, -1, 16, Colors.White);
            }
        }
        // the panel
        var size = Size;
        var panel = new Rect2(20, size.Y - 210, 520, 190);
        DrawRect(panel, new Color(0.05f, 0.06f, 0.07f, 0.85f));
        float y = panel.Position.Y + 26;
        void Line(string t, Color c, int fs = 15) { DrawString(_font, new Vector2(panel.Position.X + 14, y), t, HorizontalAlignment.Left, -1, fs, c); y += fs + 7; }
        void Button(string t, Action act)
        {
            var box = new Rect2(panel.Position.X + 14, y - 16, 300, 24);
            bool over = box.HasPoint(_mouse);
            DrawRect(box, over ? new Color(0.35f, 0.5f, 0.4f) : new Color(0.2f, 0.28f, 0.24f));
            DrawString(_font, box.Position + new Vector2(8, 17), t, HorizontalAlignment.Left, -1, 15, Colors.White);
            _buttons.Add((box, act));
            y += 30;
        }
        Line(Core.Lang.T("ws.help"), new Color(0.75f, 0.78f, 0.72f), 13);
        if (_job is { } j)
        {
            Line(Core.Lang.T(j.Fit == null ? "ws.removing" : "ws.fitting", j.Part.Name.ToLowerInvariant(), j.Done.ToString("0"), j.Time.ToString("0")), Colors.White, 16);
            var bar = new Rect2(panel.Position.X + 14, y - 8, 490, 12);
            DrawRect(bar, new Color(0.15f, 0.15f, 0.15f));
            DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * j.Done / j.Time, bar.Size.Y)), new Color(0.4f, 0.75f, 0.45f));
            y += 22;
        }
        else if (_picked is { } p)
        {
            Line(p.Missing ? $"{p.Name}: missing" : $"{p.Name}: {p.Condition:0} %", Colors.White, 17);
            if (!p.Missing) Button(Core.Lang.T("ws.remove", (JobTime(p.Kind) * Items.Skills.TimeFactor("mechanics")).ToString("0")), () => StartJob(p));
            else
            {
                var fits = Carried.Where(c => c.Kind == p.Kind).Take(3).ToList();
                if (fits.Count == 0) Line(Core.Lang.T("ws.nothing_fits"), new Color(0.8f, 0.6f, 0.5f));
                foreach (var f in fits) Button(Core.Lang.T("ws.fit", f.Name.ToLowerInvariant(), f.Condition.ToString("0"), (JobTime(p.Kind) * Items.Skills.TimeFactor("mechanics")).ToString("0")), () => StartJob(p, f));
            }
        }
        else Line(Core.Lang.T("ws.click"), new Color(0.8f, 0.8f, 0.75f));
        // what you carry
        var inv = new Rect2(size.X - 300, size.Y - 210, 280, 190);
        DrawRect(inv, new Color(0.05f, 0.06f, 0.07f, 0.85f));
        DrawString(_font, inv.Position + new Vector2(12, 24), Core.Lang.T("ws.carried"), HorizontalAlignment.Left, -1, 15, new Color(0.85f, 0.85f, 0.8f));
        int row = 0;
        foreach (var c in Carried.Take(8))
            DrawString(_font, inv.Position + new Vector2(12, 48 + row++ * 18), $"{c.Name} {c.Condition:0} %", HorizontalAlignment.Left, -1, 14, Colors.White);
        if (Carried.Count == 0) DrawString(_font, inv.Position + new Vector2(12, 48), "(nothing)", HorizontalAlignment.Left, -1, 14, new Color(0.6f, 0.6f, 0.6f));
    }
}
