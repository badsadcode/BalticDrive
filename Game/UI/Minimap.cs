using System;
using System.Collections.Generic;
using BalticDrive.Map;
using Godot;

namespace BalticDrive.UI;

/// <summary>
/// The round radar in the corner (D35, Tom: "GTA style"): the roads, buildings and street names around you from the
/// map's vector layer (<see cref="MapVectors"/>), turned so that ahead is up, zooming out a little with speed, with
/// the GPS route, a north mark and your arrow in the middle. N hides and shows it; it hides while the map is open.
/// The disc clips its child (the drawing) to a circle.
/// </summary>
public partial class Minimap : Control
{
    public const float Radius = 115f;
    private static readonly Color Paper = new(0.93f, 0.91f, 0.86f), Accent = new(0.98f, 0.72f, 0.25f);

    private MapWorld _map = null!;
    private Navigation _nav = null!;
    private Func<(double X, double Z, Vector2 Forward, float Speed)> _here = null!;
    private Drawing _drawing = null!;
    private Font _font = null!;

    public void Setup(MapWorld map, Navigation nav, Func<(double X, double Z, Vector2 Forward, float Speed)> here)
    {
        _map = map; _nav = nav; _here = here;
        _font = ThemeDB.FallbackFont;
        SetAnchorsPreset(LayoutPreset.BottomLeft);
        Size = new Vector2(Radius * 2, Radius * 2);
        Position = new Vector2(24, GetViewportRect().Size.Y - Radius * 2 - 40);
        ClipChildren = ClipChildrenMode.Only;   // the disc drawn below is the mask; the map is drawn by the child
        MouseFilter = MouseFilterEnum.Ignore;
        _drawing = new Drawing(this) { Size = Size, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_drawing);
        var frame = new Frame(this) { Size = Size, MouseFilter = MouseFilterEnum.Ignore };
        // the frame must not be clipped: give it its own layer above the disc
        AddSibling(frame);
        _frame = frame;
    }

    private Frame _frame = null!;

    /// <summary>Things to mark (D50): world x, z and a kind. Kinds: 0 a crate, 1 a medical crate, 2 a body to search,
    /// 3 a lead (D54: a red diamond, held at the rim when it's further than the disc shows), 4 your things where you
    /// fell and 5 your safehouse (D55, also held at the rim);
    /// <c>Done</c> greys it out (opened).</summary>
    public Func<IEnumerable<(double X, double Z, int Kind, bool Done)>>? Markers;

    public override void _Draw() => DrawCircle(new Vector2(Radius, Radius), Radius, Colors.White);   // the clip mask

    public override void _Process(double delta)
    {
        if (_map == null) return;
        Position = new Vector2(24, GetViewportRect().Size.Y - Radius * 2 - 40);
        _frame.Position = Position;
        _frame.Visible = Visible;
        if (!Visible) return;
        _drawing.QueueRedraw();
        _frame.QueueRedraw();
    }

    /// <summary>The map, drawn in the disc's space with "ahead" up.</summary>
    private sealed partial class Drawing : Control
    {
        private readonly Minimap _m;
        private readonly MapVectors _vec = new(16, widen: 1.8f, minHalf: 4f);
        private readonly StreetLabels _labeller = new();
        private readonly List<StreetLabels.Label> _labels = new();
        private double _labelsAt;
        private float _mpp = 2.2f;

        public Drawing(Minimap m) { _m = m; }

        public override void _Draw()
        {
            var (x, z, fwd, speed) = _m._here();
            var c = new Vector2(Radius, Radius);
            // a little further out at speed: 2.2 m per pixel parked, 4.5 at motorway speed
            float want = Mathf.Clamp(2.2f + MathF.Abs(speed) * 0.075f, 2.2f, 4.5f);
            _mpp = Mathf.Lerp(_mpp, want, 0.05f);
            float mpp = _mpp;
            float heading = MathF.Atan2(fwd.X, -fwd.Y);              // 0 = north (-Z), clockwise
            DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.2f, 0.25f, 0.19f, 0.93f));
            // world -> disc: centre on you, turn so ahead is up, scale
            Vector2 ToDisc(double wx, double wz) => c + new Vector2((float)((wx - x) / mpp), (float)((wz - z) / mpp)).Rotated(-heading);
            double reach = Radius * 1.5 * mpp;
            int tx0 = (int)Math.Floor((x - reach) / MapTile.Size), tx1 = (int)Math.Floor((x + reach) / MapTile.Size);
            int tz0 = (int)Math.Floor((z - reach) / MapTile.Size), tz1 = (int)Math.Floor((z + reach) / MapTile.Size);
            var tiles = new List<MapTile>();
            for (int tz = tz0; tz <= tz1; tz++)
                for (int tx = tx0; tx <= tx1; tx++)
                {
                    if (!_m._map.Data.HasTile(tx, tz)) continue;
                    var tile = _m._map.Data.Tile(tx, tz);
                    tiles.Add(tile);
                    var (roads, buildings) = _vec.Get(tile);
                    var basis = new Transform2D(-heading, Vector2.Zero).ScaledLocal(new Vector2(1f / mpp, 1f / mpp));
                    var origin = ToDisc(tile.OriginX, tile.OriginZ);
                    MapVectors.Draw(this, roads, buildings, new Transform2D(basis.X, basis.Y, origin));
                }
            // the route
            var route = _m._nav.Route;
            if (route != null && route.Points.Length > 1)
            {
                var line = new List<Vector2> { c };
                float r2 = (float)(reach * reach);
                // from the car onwards only (D36)
                for (int k = Math.Clamp(_m._nav.Progress + 1, 0, route.Points.Length - 1); k < route.Points.Length; k++)
                {
                    var p = route.Points[k];
                    double dx = p.X - x, dz = p.Y - z;
                    if (dx * dx + dz * dz < r2 * 1.2f) line.Add(ToDisc(p.X, p.Y));
                    else if (line.Count > 1) break;
                }
                if (line.Count > 1)
                {
                    DrawPolyline(line.ToArray(), new Color(0, 0, 0, 0.7f), 6f, true);
                    DrawPolyline(line.ToArray(), new Color(0.35f, 0.75f, 1f), 3.5f, true);
                }
            }
            // street names: placed in world terms four times a second, drawn turned with the map every frame
            if (Time.GetTicksMsec() / 1000.0 > _labelsAt)
            {
                _labelsAt = Time.GetTicksMsec() / 1000.0 + 0.25;
                _labeller.Clear(); _labels.Clear();
                bool Inside(Vector2 p) => p.DistanceTo(c) < Radius - 18;
                foreach (var tile in tiles)
                    foreach (var r in tile.Roads)
                    {
                        if (r.Name.Length == 0 || r.IsRail || r.IsTram || r.X.Length < 2) continue;
                        var pts = new Vector2[r.X.Length];
                        bool near = false;
                        for (int i = 0; i < pts.Length; i++)
                        {
                            pts[i] = ToDisc(tile.OriginX + r.X[i], tile.OriginZ + r.Z[i]);
                            near |= pts[i].DistanceTo(c) < Radius;
                        }
                        if (near) _labeller.Place(r.Name, pts, _m._font, 11, 260f, 220f, Inside, _labels);
                    }
            }
            foreach (var l in _labels) StreetLabels.Draw(this, _m._font, 11, l, Paper);
            // supplies (D50): a little box, a red cross for medical ones, a mark for a body; grey once opened
            if (_m.Markers != null)
                foreach (var (mx, mz, kind, done) in _m.Markers())
                {
                    var p = ToDisc(mx, mz);
                    if (kind is 4 or 5)
                    {
                        if (p.DistanceTo(c) > Radius - 10) p = c + (p - c).Normalized() * (Radius - 10);
                        var ink2 = kind == 4 ? new Color(0.95f, 0.95f, 0.92f) : new Color(0.35f, 0.85f, 0.45f);
                        DrawRect(new Rect2(p - new Vector2(7, 7), new Vector2(14, 14)), new Color(0, 0, 0, 0.85f));
                        if (kind == 4) DrawRect(new Rect2(p - new Vector2(5, 5), new Vector2(10, 10)), ink2);
                        else DrawColoredPolygon(new[] { p + new Vector2(-6, 0), p + new Vector2(0, -6), p + new Vector2(6, 0), p + new Vector2(4, 0), p + new Vector2(4, 5), p + new Vector2(-4, 5), p + new Vector2(-4, 0) }, ink2);
                        continue;
                    }
                    if (kind == 3)
                    {
                        if (p.DistanceTo(c) > Radius - 10) p = c + (p - c).Normalized() * (Radius - 10);
                        var red = new Color(0.95f, 0.3f, 0.2f);
                        DrawColoredPolygon(new[] { p + new Vector2(0, -8), p + new Vector2(8, 0), p + new Vector2(0, 8), p + new Vector2(-8, 0) }, new Color(0, 0, 0, 0.85f));
                        DrawColoredPolygon(new[] { p + new Vector2(0, -6), p + new Vector2(6, 0), p + new Vector2(0, 6), p + new Vector2(-6, 0) }, red);
                        continue;
                    }
                    if (p.DistanceTo(c) > Radius - 6) continue;
                    var ink = done ? new Color(0.55f, 0.55f, 0.52f, 0.8f) : kind == 1 ? new Color(0.95f, 0.95f, 0.95f) : new Color(0.98f, 0.72f, 0.25f);
                    if (kind == 2)
                    {
                        DrawLine(p - new Vector2(4, 4), p + new Vector2(4, 4), new Color(0, 0, 0, 0.8f), 4f);
                        DrawLine(p - new Vector2(4, -4), p + new Vector2(4, -4), new Color(0, 0, 0, 0.8f), 4f);
                        DrawLine(p - new Vector2(4, 4), p + new Vector2(4, 4), ink, 2f);
                        DrawLine(p - new Vector2(4, -4), p + new Vector2(4, -4), ink, 2f);
                        continue;
                    }
                    DrawRect(new Rect2(p - new Vector2(5.5f, 4.5f), new Vector2(11, 9)), new Color(0, 0, 0, 0.8f));
                    DrawRect(new Rect2(p - new Vector2(4.5f, 3.5f), new Vector2(9, 7)), ink);
                    if (kind == 1 && !done)
                    {
                        DrawRect(new Rect2(p - new Vector2(1f, 3f), new Vector2(2, 6)), new Color(0.85f, 0.15f, 0.15f));
                        DrawRect(new Rect2(p - new Vector2(3f, 1f), new Vector2(6, 2)), new Color(0.85f, 0.15f, 0.15f));
                    }
                }
        }
    }

    /// <summary>The ring, the north mark and your arrow, over the disc.</summary>
    private sealed partial class Frame : Control
    {
        private readonly Minimap _m;
        public Frame(Minimap m) { _m = m; }

        public override void _Draw()
        {
            var (_, _, fwd, _) = _m._here();
            var c = new Vector2(Radius, Radius);
            DrawArc(c, Radius, 0, MathF.Tau, 96, new Color(0, 0, 0, 0.85f), 5f, true);
            DrawArc(c, Radius - 3, 0, MathF.Tau, 96, new Color(0.93f, 0.91f, 0.86f, 0.55f), 1.5f, true);
            // north: where -Z sits on the turned disc
            float heading = MathF.Atan2(fwd.X, -fwd.Y);
            var n = c + new Vector2(0, -(Radius - 12)).Rotated(-heading);
            DrawCircle(n, 9, new Color(0, 0, 0, 0.8f));
            var ts = _m._font.GetStringSize("N", HorizontalAlignment.Left, -1, 13);
            DrawString(_m._font, n + new Vector2(-ts.X * 0.5f, 5), "N", HorizontalAlignment.Left, -1, 13, Paper);
            // you
            var up = new Vector2(0, -1); var side = new Vector2(1, 0);
            var arrow = new[] { c + up * 11, c - up * 7 + side * 8, c - up * 3, c - up * 7 - side * 8 };
            DrawColoredPolygon(arrow, new Color(0, 0, 0, 0.8f));
            DrawColoredPolygon(new[] { arrow[0] * 0.85f + c * 0.15f, arrow[1] * 0.85f + c * 0.15f, arrow[2] * 0.85f + c * 0.15f, arrow[3] * 0.85f + c * 0.15f }, Accent);
        }
    }
}
