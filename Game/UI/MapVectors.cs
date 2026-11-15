using System;
using System.Collections.Generic;
using System.Linq;
using BalticDrive.Map;
using Godot;

namespace BalticDrive.UI;

/// <summary>
/// The map's vector layer (D34, D35), shared by the map screen and the minimap: each tile's roads (true width, class
/// colours, minor first) and building footprints as triangles in tile-local metres, built once per tile and drawn onto
/// a canvas item with any transform; and street-name placement along screen-space road lines.
/// </summary>
public sealed class MapVectors
{
    /// <summary>Triangles in tile-local metres, drawn straight onto a canvas item.</summary>
    public sealed record Tris(Vector2[] Points, Color[] Colors, int[] Indices);

    private readonly Dictionary<long, (Tris? Roads, Tris? Buildings)> _cache = new();
    private readonly int _keep;
    private readonly float _widen, _minHalf;

    /// <param name="widen">Roads drawn this many times their real width (the minimap exaggerates them, like a radar).</param>
    /// <param name="minHalf">and never narrower than this half width, metres.</param>
    public MapVectors(int keep = 48, float widen = 1f, float minHalf = 2f) { _keep = keep; _widen = widen; _minHalf = minHalf; }

    public static Color BuildingColour => new(0.47f, 0.42f, 0.38f);

    public static Color RoadColour(MapRoad r) =>
        r.IsRail ? new Color(0.08f, 0.08f, 0.09f)
        : r.Cls <= RoadClass.Trunk ? new Color(0.91f, 0.59f, 0.27f)
        : r.Cls <= RoadClass.Secondary ? new Color(0.89f, 0.81f, 0.51f)
        : r.Cls <= RoadClass.Residential || r.Cls == RoadClass.Link ? new Color(0.75f, 0.74f, 0.71f)
        : r.Unpaved ? new Color(0.52f, 0.45f, 0.36f) : new Color(0.59f, 0.58f, 0.56f);

    public static long Key(int tx, int tz) => ((long)tx << 32) ^ (uint)tz;

    public void Forget(long key) => _cache.Remove(key);

    public (Tris? Roads, Tris? Buildings) Get(MapTile tile)
    {
        long key = Key(tile.Tx, tile.Tz);
        if (_cache.TryGetValue(key, out var v)) return v;
        var rv = new List<Vector2>(); var rc = new List<Color>();
        // minor roads first, so the main ones end up on top
        for (int pass = 0; pass < 3; pass++)
            foreach (var r in tile.Roads)
            {
                int rank = r.IsRail ? 0 : r.Cls <= RoadClass.Secondary ? 2 : 1;
                if (rank != pass || r.IsTram || r.X.Length < 2) continue;
                var col = RoadColour(r);
                float hw = r.IsRail ? 1.2f * _widen : MathF.Max(r.HalfWidth * _widen, _minHalf);
                for (int k = 0; k + 1 < r.X.Length; k++)
                {
                    var a = new Vector2(r.X[k], r.Z[k]); var b = new Vector2(r.X[k + 1], r.Z[k + 1]);
                    var d = b - a; float len = d.Length();
                    if (len < 0.05f) continue;
                    var n = new Vector2(-d.Y, d.X) / len * hw;
                    var e = d / len * MathF.Min(hw, len * 0.5f);   // a little past each end: closes the gaps at bends
                    rv.Add(a - e + n); rv.Add(b + e + n); rv.Add(b + e - n);
                    rv.Add(a - e + n); rv.Add(b + e - n); rv.Add(a - e - n);
                    for (int c = 0; c < 6; c++) rc.Add(col);
                }
            }
        var bv = new List<Vector2>();
        foreach (var b in tile.Buildings)
        {
            if (b.Ghost) continue;
            var ring = new Vector2[b.RX[0].Length];
            for (int i = 0; i < ring.Length; i++) ring[i] = new Vector2(b.RX[0][i], b.RZ[0][i]);
            foreach (int i in Geometry2D.TriangulatePolygon(ring)) bv.Add(ring[i]);
        }
        v = (Pack(rv, rc, default), Pack(bv, null, BuildingColour));
        _cache[key] = v;
        if (_cache.Count > _keep)
            foreach (var k in _cache.Keys.Where(k => k != key).Take(_cache.Count - _keep * 3 / 4).ToList()) _cache.Remove(k);
        return v;
    }

    private static Tris? Pack(List<Vector2> vs, List<Color>? cs, Color fill)
    {
        if (vs.Count == 0) return null;
        var cols = cs != null ? cs.ToArray() : Enumerable.Repeat(fill, vs.Count).ToArray();
        return new Tris(vs.ToArray(), cols, Enumerable.Range(0, vs.Count).ToArray());
    }

    /// <summary>Draws a tile's layer onto <paramref name="item"/>, tile-local metres mapped by <paramref name="xf"/>.</summary>
    public static void Draw(CanvasItem item, Tris? roads, Tris? buildings, Transform2D xf)
    {
        item.DrawSetTransformMatrix(xf);
        var ci = item.GetCanvasItem();
        if (buildings != null) RenderingServer.CanvasItemAddTriangleArray(ci, buildings.Indices, buildings.Points, buildings.Colors);
        if (roads != null) RenderingServer.CanvasItemAddTriangleArray(ci, roads.Indices, roads.Points, roads.Colors);
        item.DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }
}

/// <summary>
/// Street names along screen-space road lines: one label per <c>spacing</c> pixels of road, in the middle of each
/// stretch, turned along the road and never upside down, skipped where it would hit another label or repeat the same
/// name close by.
/// </summary>
public sealed class StreetLabels
{
    private readonly List<Rect2> _boxes = new();
    private readonly Dictionary<string, List<Vector2>> _placed = new();

    public readonly record struct Label(string Name, Vector2 At, float Angle);

    public void Clear() { _boxes.Clear(); _placed.Clear(); }

    /// <summary>Finds places for <paramref name="name"/> along <paramref name="pts"/>; each accepted one goes to
    /// <paramref name="output"/>. <paramref name="inside"/> says whether a screen point may carry a label.</summary>
    public void Place(string name, Vector2[] pts, Font font, int fs, float spacing, float repeat, Func<Vector2, bool> inside, List<Label> output)
    {
        float total = 0;
        for (int i = 0; i + 1 < pts.Length; i++) total += pts[i].DistanceTo(pts[i + 1]);
        float tw = font.GetStringSize(name, HorizontalAlignment.Left, -1, fs).X;
        if (total < tw + 24) return;
        int count = Math.Max(1, (int)(total / spacing));
        for (int n = 0; n < count; n++) One(name, pts, total * (n + 0.5f) / count, tw, repeat, inside, output);
    }

    private void One(string name, Vector2[] pts, float at, float tw, float repeat, Func<Vector2, bool> inside, List<Label> output)
    {
        float run = 0;
        for (int i = 0; i + 1 < pts.Length; i++)
        {
            float seg = pts[i].DistanceTo(pts[i + 1]);
            if (run + seg < at) { run += seg; continue; }
            var p = pts[i].Lerp(pts[i + 1], seg > 0 ? (at - run) / seg : 0);
            if (!inside(p)) return;
            var d = pts[i + 1] - pts[i];
            float ang = MathF.Atan2(d.Y, d.X);
            if (ang > MathF.PI / 2) ang -= MathF.PI; else if (ang < -MathF.PI / 2) ang += MathF.PI;   // never upside down
            float half = tw * 0.5f + 4;
            float ex = MathF.Abs(MathF.Cos(ang)) * half + 6, ey = MathF.Abs(MathF.Sin(ang)) * half + 8;
            var box = new Rect2(p - new Vector2(ex, ey), new Vector2(ex * 2, ey * 2));
            foreach (var l in _boxes) if (l.Intersects(box)) return;
            if (_placed.TryGetValue(name, out var same))
                foreach (var q in same) if (q.DistanceTo(p) < repeat) return;
            _boxes.Add(box);
            if (same == null) _placed[name] = same = new List<Vector2>();
            same.Add(p);
            output.Add(new Label(name, p, ang));
            return;
        }
    }

    public static void Draw(CanvasItem item, Font font, int fs, Label l, Color fill)
    {
        float tw = font.GetStringSize(l.Name, HorizontalAlignment.Left, -1, fs).X;
        item.DrawSetTransform(l.At, l.Angle, Vector2.One);
        var o = new Vector2(-tw * 0.5f, fs * 0.35f);
        item.DrawStringOutline(font, o, l.Name, HorizontalAlignment.Left, -1, fs, 4, new Color(0.04f, 0.045f, 0.05f, 0.9f));
        item.DrawString(font, o, l.Name, HorizontalAlignment.Left, -1, fs, fill);
        item.DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }
}
