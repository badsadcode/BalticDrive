using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using BalticDrive.Core;
using BalticDrive.Map;
using Godot;

namespace BalticDrive.UI;

/// <summary>
/// (Ported from Dead Miles.) The road atlas for the real-map world: a north-up map centred on the player, drawn from the
/// same tiles the world is built from. Each tile is baked once into a small image on a worker
/// thread (land cover, buildings, roads, railways); fuel stations and place names are drawn on top.
/// </summary>
public partial class MapScreen : Control
{
    private const int TexRes = 256;                         // pixels per tile: 8 m per pixel
    private static readonly float[] Zooms = { 2.5f, 5f, 10f, 20f };   // metres per screen pixel

    private static readonly Color Paper = new(0.93f, 0.91f, 0.86f), Accent = new(0.98f, 0.72f, 0.25f), Dim = new(0.75f, 0.74f, 0.70f);
    private MapWorld _map = null!;
    private Navigation _nav = null!;
    /// <summary>Where the driver is (world x, z) and which way they face (x, z of the forward vector).</summary>
    private Func<(double X, double Z, Vector2 Forward)> _here = null!;
    private Action<double, double> _travel = null!;
    private readonly List<Vector2> _markers = new();
    private int _zoom = 1;
    private double _redraw;
    private readonly Dictionary<long, ImageTexture> _textures = new();
    private readonly HashSet<long> _pending = new();
    private readonly ConcurrentQueue<(long key, byte[] rgb)> _done = new();
    private Font _font = null!;
    private Vector2 _pan;                 // world metres the view is shifted from the player
    private Vector2 _mouse;               // cursor position on screen
    private bool _dragging, _dragged;
    private Vector2 _pressAt;

    public void Setup(MapWorld map, Navigation nav, Func<(double X, double Z, Vector2 Forward)> here, Action<double, double> travel)
    {
        _map = map; _nav = nav; _here = here; _travel = travel;
        ProcessMode = ProcessModeEnum.Always;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;   // the map takes the mouse while it is open
        Visible = false;
        _font = ThemeDB.FallbackFont;
    }

    /// <summary>0 = closest. Used by the automated tests.</summary>
    public void SetZoom(int level) { _zoom = Math.Clamp(level, 0, Zooms.Length - 1); QueueRedraw(); }

    public void Toggle()
    {
        Visible = !Visible;
        if (Visible)
        {
            _pan = Vector2.Zero;
            _mouse = Size * 0.5f;
            Input.MouseMode = Input.MouseModeEnum.Visible;
            QueueRedraw();
        }
        else Close();
    }

    private void Close()
    {
        Visible = false;
        _dragging = false;
    }

    private Vector2 FocusXZ()
    {
        var h = _here();
        return new Vector2((float)h.X, (float)h.Z) + _pan;
    }

    /// <summary>World position under a screen point.</summary>
    private Vector2 ToWorld(Vector2 screen) => FocusXZ() + (screen - Size * 0.5f) * Zooms[_zoom];

    private void Zoom(int step)
    {
        // keep the spot under the cursor still while zooming
        var before = ToWorld(_mouse);
        _zoom = Math.Clamp(_zoom + step, 0, Zooms.Length - 1);
        _pan += before - ToWorld(_mouse);
        QueueRedraw();
    }

    private int MarkerAt(Vector2 screen)
    {
        var w = ToWorld(screen);
        float reach = 12f * Zooms[_zoom];
        for (int i = 0; i < _markers.Count; i++)
            if (_markers[i].DistanceTo(w) < reach) return i;
        return -1;
    }

    public override void _GuiInput(InputEvent e)
    {
        if (e is InputEventMouseMotion motion)
        {
            _mouse = motion.Position;
            if (_dragging)
            {
                if (motion.Position.DistanceTo(_pressAt) > 5f) _dragged = true;
                if (_dragged) _pan -= motion.Relative * Zooms[_zoom];
            }
            QueueRedraw();
        }
        else if (e is InputEventMouseButton mb)
        {
            _mouse = mb.Position;
            if (mb.ButtonIndex == MouseButton.WheelUp && mb.Pressed) Zoom(-1);
            else if (mb.ButtonIndex == MouseButton.WheelDown && mb.Pressed) Zoom(1);
            else if (mb.ButtonIndex == MouseButton.Left)
            {
                if (mb.Pressed) { _dragging = true; _dragged = false; _pressAt = mb.Position; }
                else
                {
                    // a click (not a drag) sets the GPS destination
                    if (_dragging && !_dragged) _nav.SetDestination(ToWorld(mb.Position), FocusXZ() - _pan);
                    _dragging = false;
                }
            }
            else if (mb.ButtonIndex == MouseButton.Right && mb.Pressed)
            {
                int hit = MarkerAt(mb.Position);
                if (hit >= 0) _markers.RemoveAt(hit);
                else _markers.Add(ToWorld(mb.Position));
            }
            QueueRedraw();
        }
        AcceptEvent();
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Visible) return;
        if (e is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode is Key.Escape or Key.M) { Close(); GetViewport().SetInputAsHandled(); return; }
        switch (key.Keycode)
        {
            case Key.Equal or Key.Plus or Key.KpAdd: Zoom(-1); break;
            case Key.Minus or Key.KpSubtract: Zoom(1); break;
            case Key.Space or Key.Home: _pan = Vector2.Zero; break;                      // back to where you are
            case Key.Delete or Key.Backspace: _nav.SetDestination(null, FocusXZ() - _pan); break;            // cancel the route
            case Key.T:                                                                   // test-phase fast travel
                var w = ToWorld(_mouse);
                Close();
                _travel(w.X, w.Y);
                break;
            default: return;
        }
        QueueRedraw();
        GetViewport().SetInputAsHandled();
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        bool fresh = false;
        while (_done.TryDequeue(out var d))
        {
            _pending.Remove(d.key);
            var img = Image.CreateFromData(TexRes, TexRes, false, Image.Format.Rgb8, d.rgb);
            _textures[d.key] = ImageTexture.CreateFromImage(img);
            fresh = true;
        }
        _redraw -= delta;
        if (fresh || _redraw <= 0) { _redraw = 0.2; QueueRedraw(); }
    }

    private static long TileKey(int tx, int tz) => ((long)tx << 32) ^ (uint)tz;

    public override void _Draw()
    {
        var size = Size;
        float mpp = Zooms[_zoom];
        var h = _here();
        var here = new Vector3((float)h.X, 0, (float)h.Z);
        double focusX = h.X + _pan.X, focusZ = h.Z + _pan.Y;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.05f, 0.055f, 0.06f, 0.97f));

        Vector2 ToScreen(double wx, double wz) => new((float)((wx - focusX) / mpp) + size.X * 0.5f, (float)((wz - focusZ) / mpp) + size.Y * 0.5f);

        double halfW = size.X * 0.5 * mpp, halfH = size.Y * 0.5 * mpp;
        int tx0 = (int)Math.Floor((focusX - halfW) / MapTile.Size), tx1 = (int)Math.Floor((focusX + halfW) / MapTile.Size);
        int tz0 = (int)Math.Floor((focusZ - halfH) / MapTile.Size), tz1 = (int)Math.Floor((focusZ + halfH) / MapTile.Size);
        float tilePx = MapTile.Size / mpp;
        for (int tz = tz0; tz <= tz1; tz++)
        {
            for (int tx = tx0; tx <= tx1; tx++)
            {
                long key = TileKey(tx, tz);
                if (_textures.TryGetValue(key, out var tex))
                    DrawTextureRect(tex, new Rect2(ToScreen(tx * (double)MapTile.Size, tz * (double)MapTile.Size), new Vector2(tilePx + 0.5f, tilePx + 0.5f)), false);
                else if (_map.Data.HasTile(tx, tz) && _pending.Count < 6 && _pending.Add(key))
                {
                    int btx = tx, btz = tz;
                    var data = _map.Data;
                    Task.Run(() =>
                    {
                        byte[] rgb;
                        try { rgb = Bake(data.Tile(btx, btz)); }
                        catch (Exception ex) { Log.Error("MapScreen", $"Tile {btx},{btz}: {ex.Message}"); rgb = new byte[TexRes * TexRes * 3]; }
                        _done.Enqueue((key, rgb));
                    });
                }
            }
        }
        if (_textures.Count > 160) Evict(tx0, tx1, tz0, tz1);

        // places to stop: fuel always, shops and help when zoomed in
        for (int tz = tz0; tz <= tz1; tz++)
        {
            for (int tx = tx0; tx <= tx1; tx++)
            {
                if (!_textures.ContainsKey(TileKey(tx, tz))) continue;
                var tile = _map.Data.Tile(tx, tz);
                foreach (var p in tile.Pois)
                {
                    Color? c = p.Kind switch
                    {
                        MapPoiKind.Fuel => new Color(0.95f, 0.6f, 0.15f),
                        MapPoiKind.Medical when mpp <= 5f => new Color(0.85f, 0.25f, 0.2f),
                        MapPoiKind.Garage when mpp <= 5f => new Color(0.55f, 0.65f, 0.8f),
                        MapPoiKind.Food when mpp <= 2.5f => new Color(0.5f, 0.8f, 0.45f),
                        _ => null,
                    };
                    if (c == null) continue;
                    var s = ToScreen(tile.OriginX + p.X, tile.OriginZ + p.Z);
                    float r = p.Kind == MapPoiKind.Fuel ? 4.5f : 3f;
                    DrawRect(new Rect2(s - new Vector2(r + 1, r + 1), new Vector2(r * 2 + 2, r * 2 + 2)), new Color(0, 0, 0, 0.8f));
                    DrawRect(new Rect2(s - new Vector2(r, r), new Vector2(r * 2, r * 2)), c.Value);
                }
            }
        }

        // the GPS route, under the names and pins
        var route = _nav.Route;
        if (route != null && route.Points.Length > 1)
        {
            int stride = Math.Max(1, (int)(mpp / 2.5f));
            var line = new List<Vector2>(route.Points.Length / stride + 2);
            for (int i = 0; i < route.Points.Length; i += stride) line.Add(ToScreen(route.Points[i].X, route.Points[i].Y));
            line.Add(ToScreen(route.Points[^1].X, route.Points[^1].Y));
            DrawPolyline(line.ToArray(), new Color(0, 0, 0, 0.75f), 7f, true);
            DrawPolyline(line.ToArray(), new Color(0.35f, 0.75f, 1f), 4f, true);
        }

        // names
        foreach (var p in _map.Data.Places)
        {
            if (p.Rank == 2 && mpp > 5f || p.Rank == 1 && mpp > 10f) continue;
            var s = ToScreen(p.X, p.Z);
            if (s.X < -100 || s.X > size.X + 20 || s.Y < 0 || s.Y > size.Y) continue;
            int fs = p.Rank == 0 ? 22 : p.Rank == 1 ? 17 : 13;
            var ts = _font.GetStringSize(p.Name, HorizontalAlignment.Left, -1, fs);
            var at = s + new Vector2(-ts.X * 0.5f, 5);
            DrawString(_font, at + new Vector2(1, 1), p.Name, HorizontalAlignment.Left, -1, fs, new Color(0, 0, 0, 0.85f));
            DrawString(_font, at, p.Name, HorizontalAlignment.Left, -1, fs, p.Rank == 2 ? new Color(0.85f, 0.83f, 0.76f) : Paper);
        }

        // pins dropped by the player
        foreach (var m in _markers)
        {
            var s = ToScreen(m.X, m.Y);
            DrawColoredPolygon(new[] { s + new Vector2(0, -11), s + new Vector2(9, 0), s + new Vector2(0, 11), s + new Vector2(-9, 0) }, new Color(0, 0, 0, 0.85f));
            DrawColoredPolygon(new[] { s + new Vector2(0, -8), s + new Vector2(6.5f, 0), s + new Vector2(0, 8), s + new Vector2(-6.5f, 0) }, new Color(0.3f, 0.9f, 0.85f));
        }
        // destination flag
        if (_nav.Destination is { } dest)
        {
            var s = ToScreen(dest.X, dest.Y);
            DrawLine(s, s + new Vector2(0, -22), new Color(0, 0, 0, 0.9f), 4f);
            DrawLine(s, s + new Vector2(0, -22), Paper, 2f);
            DrawColoredPolygon(new[] { s + new Vector2(1, -22), s + new Vector2(17, -17), s + new Vector2(1, -11) }, new Color(0.9f, 0.25f, 0.2f));
            DrawCircle(s, 3.5f, Paper);
        }

        // you are here: an arrow pointing where you face
        var dir = h.Forward;
        dir = dir.LengthSquared() > 1e-4f ? dir.Normalized() : new Vector2(0, -1);
        var perp = new Vector2(-dir.Y, dir.X);
        var c0 = ToScreen(here.X, here.Z);
        DrawColoredPolygon(new[] { c0 + dir * 15, c0 - dir * 10 + perp * 9, c0 - dir * 4, c0 - dir * 10 - perp * 9 }, new Color(0, 0, 0, 0.9f));
        DrawColoredPolygon(new[] { c0 + dir * 12, c0 - dir * 8 + perp * 7, c0 - dir * 3, c0 - dir * 8 - perp * 7 }, Accent);

        // scale bar, compass and legend
        float barM = mpp <= 2.5f ? 200f : mpp <= 5f ? 500f : mpp <= 10f ? 1000f : 2000f;
        var b0 = new Vector2(40, size.Y - 44);
        DrawRect(new Rect2(b0 - new Vector2(10, 26), new Vector2(barM / mpp + 20, 44)), new Color(0, 0, 0, 0.6f));
        DrawLine(b0, b0 + new Vector2(barM / mpp, 0), Paper, 2f);
        DrawLine(b0 + new Vector2(0, -5), b0 + new Vector2(0, 5), Paper, 2f);
        DrawLine(b0 + new Vector2(barM / mpp, -5), b0 + new Vector2(barM / mpp, 5), Paper, 2f);
        DrawString(_font, b0 + new Vector2(0, -9), barM >= 1000 ? $"{barM / 1000:0} km" : $"{barM:0} m", HorizontalAlignment.Left, -1, 14, Paper);
        DrawString(_font, new Vector2(size.X - 44, 44), "N", HorizontalAlignment.Left, -1, 22, Paper);
        DrawLine(new Vector2(size.X - 36, 52), new Vector2(size.X - 36, 78), Paper, 2f);
        // cursor: crosshair, with how far that spot is from you
        var cw = ToWorld(_mouse);
        DrawLine(_mouse - new Vector2(13, 0), _mouse - new Vector2(4, 0), Paper, 2f);
        DrawLine(_mouse + new Vector2(4, 0), _mouse + new Vector2(13, 0), Paper, 2f);
        DrawLine(_mouse - new Vector2(0, 13), _mouse - new Vector2(0, 4), Paper, 2f);
        DrawLine(_mouse + new Vector2(0, 4), _mouse + new Vector2(0, 13), Paper, 2f);
        float away = cw.DistanceTo(new Vector2(here.X, here.Z));
        string tip = away >= 1000f ? $"{away / 1000f:0.0} km" : $"{away:0} m";
        DrawString(_font, _mouse + new Vector2(17, -7) + Vector2.One, tip, HorizontalAlignment.Left, -1, 14, new Color(0, 0, 0, 0.85f));
        DrawString(_font, _mouse + new Vector2(17, -7), tip, HorizontalAlignment.Left, -1, 14, Paper);

        string status = _nav.Routing ? "Calculating route..."
            : route != null ? $"Route: {_nav.Remaining / 1000f:0.0} km to go"
            : _nav.Destination != null ? "No route to the flag" : "";
        if (status.Length > 0)
        {
            var ss = _font.GetStringSize(status, HorizontalAlignment.Left, -1, 17);
            DrawRect(new Rect2(size.X * 0.5f - ss.X * 0.5f - 12, 14, ss.X + 24, 30), new Color(0, 0, 0, 0.65f));
            DrawString(_font, new Vector2(size.X * 0.5f - ss.X * 0.5f, 36), status, HorizontalAlignment.Left, -1, 17, Paper);
        }

        string[] help =
        {
            "[M] close    wheel or +/- zoom    drag to move    [Space] back to you",
            "click: GPS destination    [Del] cancel route    right-click: pin on / off    [T] travel to cursor",
        };
        float hw = 0;
        foreach (var line in help) hw = MathF.Max(hw, _font.GetStringSize(line, HorizontalAlignment.Left, -1, 13).X);
        DrawRect(new Rect2(size.X - hw - 34, size.Y - 62, hw + 20, 46), new Color(0, 0, 0, 0.65f));
        for (int i = 0; i < help.Length; i++)
            DrawString(_font, new Vector2(size.X - hw - 24, size.Y - 43 + i * 19), help[i], HorizontalAlignment.Left, -1, 13, Dim);
    }

    private void Evict(int tx0, int tx1, int tz0, int tz1)
    {
        var drop = new List<long>();
        foreach (long key in _textures.Keys)
        {
            int tx = (int)(key >> 32), tz = (int)(uint)key;
            if (tx < tx0 - 2 || tx > tx1 + 2 || tz < tz0 - 2 || tz > tz1 + 2) drop.Add(key);
        }
        foreach (long key in drop) _textures.Remove(key);
    }

    // ---- baking (worker thread) -------------------------------------------------------------

    private static readonly (byte r, byte g, byte b)[] LandColours =
    {
        (58, 66, 48),    // open
        (30, 54, 34),    // forest
        (88, 84, 54),    // field
        (62, 78, 50),    // meadow
        (38, 62, 86),    // water
        (70, 66, 60),    // residential
        (74, 72, 70),    // paved
        (50, 62, 42),    // scrub
        (44, 66, 62),    // wetland
        (112, 100, 72),  // sand
        (58, 74, 44),    // orchard
        (46, 72, 44),    // park
        (56, 64, 52),    // cemetery
        (84, 76, 60),    // farmyard
    };

    private static byte[] Bake(MapTile tile)
    {
        var px = new byte[TexRes * TexRes * 3];
        float cell = MapTile.Size / TexRes;
        for (int j = 0; j < TexRes; j++)
        {
            int lj = Math.Min(MapTile.LandRes - 1, j * MapTile.LandRes / TexRes);
            for (int i = 0; i < TexRes; i++)
            {
                byte cls = tile.Land[lj * MapTile.LandRes + Math.Min(MapTile.LandRes - 1, i * MapTile.LandRes / TexRes)];
                var c = LandColours[cls < LandColours.Length ? cls : 0];
                int o = (j * TexRes + i) * 3;
                px[o] = c.r; px[o + 1] = c.g; px[o + 2] = c.b;
            }
        }
        foreach (var b in tile.Buildings)
        {
            if (b.Ghost) continue;
            var rx = b.RX[0]; var rz = b.RZ[0];
            for (int i = 0, j = rx.Length - 1; i < rx.Length; j = i++)
                Line(px, rx[j] / cell, rz[j] / cell, rx[i] / cell, rz[i] / cell, 120, 108, 96, 1);
            Plot(px, (int)(b.Cx / cell), (int)(b.Cz / cell), 120, 108, 96);
        }
        // minor roads first so the main ones stay on top
        for (int pass = 0; pass < 3; pass++)
        {
            foreach (var r in tile.Roads)
            {
                int rank = r.IsRail ? 0 : r.Cls <= RoadClass.Secondary ? 2 : 1;
                if (rank != pass || r.IsTram) continue;
                (byte cr, byte cg, byte cb, int w) = r.IsRail ? ((byte)20, (byte)20, (byte)22, 1)
                    : r.Cls <= RoadClass.Trunk ? ((byte)232, (byte)150, (byte)70, 2)
                    : r.Cls <= RoadClass.Secondary ? ((byte)226, (byte)206, (byte)130, 2)
                    : r.Cls <= RoadClass.Residential || r.Cls == RoadClass.Link ? ((byte)190, (byte)188, (byte)180, 1)
                    : r.Unpaved ? ((byte)132, (byte)116, (byte)92, 1) : ((byte)150, (byte)148, (byte)142, 1);
                for (int k = 0; k + 1 < r.X.Length; k++)
                    Line(px, r.X[k] / cell, r.Z[k] / cell, r.X[k + 1] / cell, r.Z[k + 1] / cell, cr, cg, cb, w);
            }
        }
        return px;
    }

    private static void Plot(byte[] px, int x, int y, byte r, byte g, byte b)
    {
        if ((uint)x >= TexRes || (uint)y >= TexRes) return;
        int o = (y * TexRes + x) * 3;
        px[o] = r; px[o + 1] = g; px[o + 2] = b;
    }

    private static void Line(byte[] px, float x0, float y0, float x1, float y1, byte r, byte g, byte b, int width)
    {
        float dx = x1 - x0, dy = y1 - y0;
        int steps = (int)MathF.Ceiling(MathF.Max(MathF.Abs(dx), MathF.Abs(dy))) + 1;
        for (int s = 0; s <= steps; s++)
        {
            float t = s / (float)steps;
            int x = (int)MathF.Floor(x0 + dx * t), y = (int)MathF.Floor(y0 + dy * t);
            Plot(px, x, y, r, g, b);
            if (width > 1) { Plot(px, x + 1, y, r, g, b); Plot(px, x, y + 1, r, g, b); Plot(px, x + 1, y + 1, r, g, b); }
        }
    }
}
