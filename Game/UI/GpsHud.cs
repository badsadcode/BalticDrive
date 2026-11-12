using System;
using Godot;

namespace BalticDrive.UI;

/// <summary>The guidance strip at the top of the screen while a route is set: distance left, the next bend.</summary>
public partial class GpsHud : Control
{
    private Navigation _nav = null!;
    private Font _font = null!;
    private static readonly Color Paper = new(0.93f, 0.91f, 0.86f), Accent = new(0.35f, 0.75f, 1f);

    public void Setup(Navigation nav)
    {
        _nav = nav;
        _font = ThemeDB.FallbackFont;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        if (_nav.Route == null && !_nav.Routing) return;
        float cx = Size.X * 0.5f, y = 18;
        string text = _nav.Routing && _nav.Route == null ? "Calculating route..."
            : _nav.NextTurnIn >= 0 ? $"{Dist(_nav.NextTurnIn)}  then {(_nav.NextTurn > 0 ? "left" : "right")}     {Dist(_nav.Remaining)} to go"
            : $"Straight on     {Dist(_nav.Remaining)} to go";
        var ts = _font.GetStringSize(text, HorizontalAlignment.Left, -1, 18);
        float w = ts.X + 70;
        DrawRect(new Rect2(cx - w * 0.5f, y, w, 40), new Color(0, 0, 0, 0.6f));
        DrawString(_font, new Vector2(cx - w * 0.5f + 52, y + 27), text, HorizontalAlignment.Left, -1, 18, Paper);
        // arrow: straight up, or bent towards the next turn
        var c = new Vector2(cx - w * 0.5f + 26, y + 20);
        float ang = _nav.NextTurnIn is >= 0 and < 400 ? -Math.Clamp(_nav.NextTurn, -1.6f, 1.6f) : 0f;
        var up = new Vector2(0, -1).Rotated(ang); var side = new Vector2(-up.Y, up.X);
        DrawColoredPolygon(new[] { c + up * 12, c - up * 8 + side * 8, c - up * 3, c - up * 8 - side * 8 }, Accent);
    }

    private static string Dist(float m) => m >= 1000 ? $"{m / 1000:0.0} km" : $"{MathF.Round(m / 10) * 10:0} m";
}
