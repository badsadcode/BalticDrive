using System;
using System.Collections.Generic;

namespace BalticDrive.Map;

/// <summary>One road segment gathered for a query, in the query's local frame.</summary>
public struct MapSeg
{
    public float Ax, Az, Bx, Bz;    // local metres
    public float Ya, Yb;            // carriageway height at each end (absolute)
    public float Hw;                // half width
    public MapRoad Road;
    public int Index;               // segment index within the road
    public float OffX, OffZ;        // tile origin relative to the query origin

    /// <summary>Distance from a point to the segment; <paramref name="u"/> is the 0..1 position of the closest point.</summary>
    public readonly float Distance(float x, float z, out float u)
    {
        float bx = Bx - Ax, bz = Bz - Az;
        float len2 = bx * bx + bz * bz;
        u = len2 > 1e-6f ? Math.Clamp(((x - Ax) * bx + (z - Az) * bz) / len2, 0f, 1f) : 0f;
        float ex = x - (Ax + bx * u), ez = z - (Az + bz * u);
        return MathF.Sqrt(ex * ex + ez * ez);
    }
}

public struct MapJunctionRef
{
    public MapJunction J;
    public float OffX, OffZ;
}

public struct MapBuildingRef
{
    public MapBuilding B;
    public float OffX, OffZ;
}

/// <summary>
/// Everything man-made around one spot, gathered once so that many height / occupancy queries (a terrain tile, a
/// grass cell) do not each walk the tile buckets. Coordinates are float metres relative to the double-precision
/// origin given to <see cref="Reset"/>, so they stay exact anywhere on the Baltic map. Not thread-safe: one context
/// per worker.
/// </summary>
public sealed class MapContext
{
    /// <summary>How far a road shapes the ground beyond its edge.</summary>
    public const float Shoulder = 9f;
    private const float Reach = 26f;        // how far a road can influence the ground, plus slack

    public readonly MapWorld World;
    public double Ox, Oz;
    public readonly List<MapSeg> Segs = new();
    public readonly List<MapBuildingRef> Buildings = new();
    public readonly List<MapJunctionRef> Junctions = new();
    public readonly List<(MapBarrier B, float OffX, float OffZ)> Barriers = new();
    /// <summary>Mapped trees, lamps, poles and pylons: X/Z local metres, Y holds the <see cref="MapPointKind"/>.</summary>
    public readonly List<(float X, byte Kind, float Z)> Points = new();
    private readonly HashSet<long> _seen = new();
    private MapTile? _tile;

    public MapContext(MapWorld world) { World = world; }

    /// <summary>Gathers roads, buildings and points touching the local rectangle (plus the road influence margin).</summary>
    public void Reset(double ox, double oz, float minX, float minZ, float maxX, float maxZ, bool withPoints = true)
    {
        Ox = ox; Oz = oz;
        Segs.Clear(); Buildings.Clear(); Junctions.Clear(); Points.Clear(); Barriers.Clear(); _seen.Clear(); _tile = null;
        double wx0 = ox + minX - Reach, wx1 = ox + maxX + Reach, wz0 = oz + minZ - Reach, wz1 = oz + maxZ + Reach;
        int tx0 = (int)Math.Floor(wx0 / MapTile.Size), tx1 = (int)Math.Floor(wx1 / MapTile.Size);
        int tz0 = (int)Math.Floor(wz0 / MapTile.Size), tz1 = (int)Math.Floor(wz1 / MapTile.Size);
        for (int tz = tz0; tz <= tz1; tz++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var tile = World.Data.Tile(tx, tz);
                if (tile.Empty) continue;
                float offX = (float)(tile.OriginX - ox), offZ = (float)(tile.OriginZ - oz);
                int cx0 = Math.Clamp((int)Math.Floor((wx0 - tile.OriginX) / MapTile.CellSize), 0, MapTile.Cells - 1);
                int cx1 = Math.Clamp((int)Math.Floor((wx1 - tile.OriginX) / MapTile.CellSize), 0, MapTile.Cells - 1);
                int cz0 = Math.Clamp((int)Math.Floor((wz0 - tile.OriginZ) / MapTile.CellSize), 0, MapTile.Cells - 1);
                int cz1 = Math.Clamp((int)Math.Floor((wz1 - tile.OriginZ) / MapTile.CellSize), 0, MapTile.Cells - 1);
                long tileKey = ((long)(tx & 0xffff) << 48) | ((long)(tz & 0xffff) << 32);
                for (int cz = cz0; cz <= cz1; cz++)
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        var rc = tile.RoadCell(cx, cz);
                        if (rc != null)
                            foreach (int packed in rc)
                            {
                                if (!_seen.Add(tileKey | (uint)packed)) continue;
                                var road = tile.Roads[packed >> 12];
                                int k = packed & 4095;
                                Segs.Add(new MapSeg
                                {
                                    Ax = offX + road.X[k], Az = offZ + road.Z[k], Bx = offX + road.X[k + 1], Bz = offZ + road.Z[k + 1],
                                    Ya = road.Y[k], Yb = road.Y[k + 1], Hw = MathF.Max(road.HalfWidthAt(k), road.HalfWidthAt(k + 1)), Road = road, Index = k, OffX = offX, OffZ = offZ,
                                });
                            }
                        if (withPoints && tile.PointCell(cx, cz) is { } pc)
                            foreach (int pi in pc)
                                Points.Add((offX + tile.PointX[pi], tile.PointKind[pi], offZ + tile.PointZ[pi]));
                        if (tile.JunctionCell(cx, cz) is { } jc)
                            foreach (int ji in jc)
                            {
                                if (!_seen.Add(tileKey | 0x40000000u | (uint)ji)) continue;
                                Junctions.Add(new MapJunctionRef { J = tile.Junctions[ji], OffX = offX, OffZ = offZ });
                            }
                        if (withPoints && tile.BarrierCell(cx, cz) is { } brc)
                            foreach (int bi in brc)
                                if (_seen.Add(tileKey | 0x20000000u | (uint)bi)) Barriers.Add((tile.Barriers[bi], offX, offZ));
                        if (tile.BuildingCell(cx, cz) is { } bc)
                            foreach (int bi in bc)
                            {
                                if (!_seen.Add(tileKey | 0x80000000u | (uint)bi)) continue;
                                Buildings.Add(new MapBuildingRef { B = tile.Buildings[bi], OffX = offX, OffZ = offZ });
                            }
                    }
            }
    }

    private MapTile TileFor(float lx, float lz, out float tlx, out float tlz)
    {
        double wx = Ox + lx, wz = Oz + lz;
        var t = _tile;
        if (t == null || wx < t.OriginX || wx >= t.OriginX + MapTile.Size || wz < t.OriginZ || wz >= t.OriginZ + MapTile.Size)
            _tile = t = World.Data.TileAt(wx, wz);
        tlx = (float)(wx - t.OriginX); tlz = (float)(wz - t.OriginZ);
        return t;
    }

    /// <summary>The nearest road that shapes the ground (bridges do not). Returns its edge distance, or a large value.</summary>
    public float NearestGroundRoad(float lx, float lz, out float roadY, out MapRoad? road)
    {
        float best = 1e9f; roadY = 0; road = null;
        var segs = Segs;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (s.Road.IsBridge || s.Road.IsTram) continue;   // trams are laid into whatever they cross
            float pad = s.Hw + Shoulder + 1f;
            if (lx < MathF.Min(s.Ax, s.Bx) - pad || lx > MathF.Max(s.Ax, s.Bx) + pad ||
                lz < MathF.Min(s.Az, s.Bz) - pad || lz > MathF.Max(s.Az, s.Bz) + pad) continue;
            float edge = s.Distance(lx, lz, out float u) - s.Hw;
            if (edge < best) { best = edge; roadY = s.Ya + (s.Yb - s.Ya) * u; road = s.Road; }
        }
        return best;
    }

    /// <summary>True when another ground road (not <paramref name="road"/>) at about the same height lies within
    /// <paramref name="reach"/> m of its edge: the point is in a median between two sunk carriageways.</summary>
    public bool OtherLowRoad(float lx, float lz, MapRoad road, float y, float reach)
    {
        var segs = Segs;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            if (s.Road == road || s.Road.IsBridge || s.Road.IsTram || s.Road.IsRail) continue;
            if (lx < MathF.Min(s.Ax, s.Bx) - s.Hw - reach || lx > MathF.Max(s.Ax, s.Bx) + s.Hw + reach ||
                lz < MathF.Min(s.Az, s.Bz) - s.Hw - reach || lz > MathF.Max(s.Az, s.Bz) + s.Hw + reach) continue;
            float e = s.Distance(lx, lz, out float u) - s.Hw;
            if (e < reach && MathF.Abs(s.Ya + (s.Yb - s.Ya) * u - y) < 1.5f) return true;
        }
        return false;
    }

    /// <summary>The nearest junction surface that shapes the ground (not elevated ones). Returns the distance to its
    /// outline (negative inside) and the surface height there, or a large value.</summary>
    public float NearestGroundJunction(float lx, float lz, out float y)
    {
        float best = 1e9f; y = 0;
        var list = Junctions;
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            var j = r.J;
            if (j.Elevated) continue;
            float x = lx - r.OffX, z = lz - r.OffZ;
            if (x < j.MinX - Shoulder || x > j.MaxX + Shoulder || z < j.MinZ - Shoulder || z > j.MaxZ + Shoulder) continue;
            if (j.Contains(x, z)) { y = j.HeightAt(x, z); return -j.EdgeDistance(x, z, out _); }
            float d = j.EdgeDistance(x, z, out float ey);
            if (d < best) { best = d; y = ey; }
        }
        return best;
    }

    /// <summary>Natural ground (DEM, water beds carved) at a local point, before roads shape it.</summary>
    public float Ground(float lx, float lz)
    {
        var tile = TileFor(lx, lz, out float tlx, out float tlz);
        return tile.GroundAt(tlx, tlz);
    }

    /// <summary>Water surface at a local point, or NaN when dry.</summary>
    public float Water(float lx, float lz)
    {
        var tile = TileFor(lx, lz, out float tlx, out float tlz);
        return tile.WaterAt(tlx, tlz);
    }

    /// <summary>
    /// Final ground height at a local point. Roads cut a level bed into the ground (or sit on an embankment);
    /// <paramref name="depress"/> sinks the ground under the carriageway so the road mesh never fights the terrain
    /// mesh; <paramref name="dirt"/> is the bare-earth mask.
    /// </summary>
    public float Height(float lx, float lz, float depress, out float dirt)
    {
        var tile = TileFor(lx, lz, out float tlx, out float tlz);
        float h = tile.GroundAt(tlx, tlz);
        dirt = tile.Empty ? 0f : tile.LandBlend(tlx, tlz, MapWorld.DirtLut);
        float edge = NearestGroundRoad(lx, lz, out float roadY, out var road);
        if (Junctions.Count > 0)
        {
            float je = NearestGroundJunction(lx, lz, out float jy);
            if (je < edge) { edge = je; roadY = jy; road = null; }
        }
        if (edge < Shoulder)
        {
            // A road sunk well below the land (an underpass and its ramps) has retaining walls: the ground drops almost
            // straight down behind them instead of sloping over 9 m (D26, Map/CuttingMesher)
            float cut = road != null ? Smooth(CuttingMesher.MinDepth, CuttingMesher.MinDepth + 1.5f, h - roadY) : 0f;
            // between two carriageways of the same sunk road (a median) the ground stays at road level: one cutting
            if (cut > 0f && edge > 0f && OtherLowRoad(lx, lz, road!, roadY, 7f)) { h = roadY; cut = 1f; edge = 0f; }
            float s0 = Lerp(1.5f, CuttingMesher.WallOffset - 0.1f, cut), s1 = Lerp(Shoulder, CuttingMesher.WallOffset + 1.4f, cut);
            h = Lerp(roadY, h, Smooth(s0, s1, edge));
            bool loose = road != null && (road.Unpaved || road.IsRail);
            dirt = MathF.Max(dirt, loose ? 1f - Smooth(-0.2f, 1.1f, edge) : 1f - Smooth(0.3f, 1.7f, edge));
            if (depress > 0f) h -= depress * (1f - Smooth(0.15f, 1.25f, edge));
        }
        return h;
    }

    public void Biome(float lx, float lz, out float forest, out float field)
    {
        var tile = TileFor(lx, lz, out float tlx, out float tlz);
        if (tile.Empty) { forest = 0.1f; field = 0f; return; }
        forest = tile.LandBlend(tlx, tlz, MapWorld.ForestLut);
        field = tile.LandBlend(tlx, tlz, MapWorld.FieldLut);
    }

    public byte Land(float lx, float lz)
    {
        var tile = TileFor(lx, lz, out float tlx, out float tlz);
        return tile.LandAt(tlx, tlz);
    }

    public float LandBlend(float lx, float lz, float[] lut)
    {
        var tile = TileFor(lx, lz, out float tlx, out float tlz);
        return tile.LandBlend(tlx, tlz, lut);
    }

    /// <summary>True when no road, railway or building is within <paramref name="margin"/> metres.</summary>
    public bool IsWild(float lx, float lz, float margin)
    {
        var segs = Segs;
        for (int i = 0; i < segs.Count; i++)
        {
            var s = segs[i];
            float pad = s.Hw + 1.6f + margin;
            if (lx < MathF.Min(s.Ax, s.Bx) - pad || lx > MathF.Max(s.Ax, s.Bx) + pad ||
                lz < MathF.Min(s.Az, s.Bz) - pad || lz > MathF.Max(s.Az, s.Bz) + pad) continue;
            if (s.Distance(lx, lz, out _) < pad) return false;
        }
        return !InBuilding(lx, lz, margin);
    }

    public bool InBuilding(float lx, float lz, float margin)
    {
        var list = Buildings;
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            var b = r.B;
            if (b.IsCanopy) continue;   // open underneath
            float x = lx - r.OffX, z = lz - r.OffZ;
            if (x < b.MinX - margin || x > b.MaxX + margin || z < b.MinZ - margin || z > b.MaxZ + margin) continue;
            if (b.Contains(x, z)) return true;
            if (margin <= 0f) continue;
            var rx = b.RX[0]; var rz = b.RZ[0];
            for (int k = 0, j = rx.Length - 1; k < rx.Length; j = k++)
            {
                float bx = rx[k] - rx[j], bz = rz[k] - rz[j];
                float len2 = bx * bx + bz * bz;
                float u = len2 > 1e-6f ? Math.Clamp(((x - rx[j]) * bx + (z - rz[j]) * bz) / len2, 0f, 1f) : 0f;
                float ex = x - (rx[j] + bx * u), ez = z - (rz[j] + bz * u);
                if (ex * ex + ez * ez < margin * margin) return true;
            }
        }
        return false;
    }

    public static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>The real map: height, land cover, roads and places from the tile packs. Thread-safe.</summary>
public sealed class MapWorld
{
    public readonly MapData Data;

    public MapWorld(MapData data) { Data = data; }

    // Per land class: how wooded, how cultivated and how bare the ground is.
    public static readonly float[] ForestLut = Lut((MapLand.Forest, 1f), (MapLand.Residential, 0.13f), (MapLand.Park, 0.32f),
        (MapLand.Orchard, 0.36f), (MapLand.Scrub, 0.24f), (MapLand.Cemetery, 0.3f), (MapLand.Wetland, 0.1f), (MapLand.Open, 0.05f));
    public static readonly float[] FieldLut = Lut((MapLand.Field, 1f));
    public static readonly float[] DirtLut = Lut((MapLand.Paved, 0.85f), (MapLand.Farmyard, 0.8f), (MapLand.Sand, 1f));
    public static readonly float[] WaterLut = Lut((MapLand.Water, 1f));

    private static float[] Lut(params (byte cls, float v)[] entries)
    {
        var lut = new float[256];
        foreach (var (cls, v) in entries) lut[cls] = v;
        return lut;
    }

    [ThreadStatic] private static MapContext? _point;

    /// <summary>A small context around one world point, reused per thread (valid until the next call on this thread).</summary>
    public MapContext Point(double x, double z, float radius = 0f)
    {
        var c = _point;
        if (c == null || c.World != this) _point = c = new MapContext(this);
        c.Reset(x, z, -radius, -radius, radius, radius, withPoints: false);
        return c;
    }

    public float Height(double x, double z) => Point(x, z).Height(0, 0, 0, out _);
    public float Ground(double x, double z) => Point(x, z).Ground(0, 0);
    public float Water(double x, double z) => Point(x, z).Water(0, 0);
    public byte LandAt(double x, double z) => Point(x, z).Land(0, 0);
    public bool InBuilding(double x, double z, float margin) => Point(x, z, margin + 1f).InBuilding(0, 0, margin);

    /// <summary>Distance to the edge of the nearest drivable road (negative when on it).</summary>
    public float RoadEdgeDistance(double x, double z, float search = 30f)
    {
        var c = Point(x, z, search);
        float best = 1e9f;
        foreach (var s in c.Segs)
        {
            if (!s.Road.Drivable) continue;
            float e = s.Distance(0, 0, out _) - s.Hw;
            if (e < best) best = e;
        }
        return best;
    }

    /// <summary>
    /// Finds a drivable road near a point: (<paramref name="px"/>, <paramref name="py"/>, <paramref name="pz"/>) is on
    /// the carriageway, <paramref name="lateral"/> metres right of the travel direction <paramref name="yaw"/>
    /// (radians, 0 = facing north / -Z, Godot's Y rotation).
    /// </summary>
    public bool NearestRoad(double x, double z, float search, float lateral, out double px, out double py, out double pz, out float yaw, int maxClass = RoadClass.Link)
    {
        var c = Point(x, z, search);
        float best = 1e9f; MapSeg bs = default; float bu = 0; bool found = false;
        foreach (var s in c.Segs)
        {
            if (!s.Road.Drivable || s.Road.IsBridge || s.Road.Cls > maxClass) continue;
            float d = s.Distance(0, 0, out float u);
            if (d < best) { best = d; bs = s; bu = u; found = true; }
        }
        px = py = pz = 0; yaw = 0;
        if (!found) return false;
        float dx = bs.Bx - bs.Ax, dz = bs.Bz - bs.Az;
        float len = MathF.Max(1e-3f, MathF.Sqrt(dx * dx + dz * dz));
        dx /= len; dz /= len;
        float side = MathF.Min(lateral, MathF.Max(0f, bs.Hw - 1.0f));
        // right of travel (dx,dz) is (-dz, dx) in Godot's X/Z plane
        px = x + bs.Ax + (bs.Bx - bs.Ax) * bu - dz * side;
        pz = z + bs.Az + (bs.Bz - bs.Az) * bu + dx * side;
        py = bs.Ya + (bs.Yb - bs.Ya) * bu;
        yaw = MathF.Atan2(-dx, -dz);
        return true;
    }

    /// <summary>The nearest named settlement of at least the given rank, or null.</summary>
    public MapPlace? NearestPlace(double x, double z, double maxDist, int maxRank = 2)
    {
        MapPlace? best = null;
        double bestD = maxDist * maxDist;
        foreach (var p in Data.Places)
        {
            if (p.Rank > maxRank) continue;
            double d = (p.X - x) * (p.X - x) + (p.Z - z) * (p.Z - z);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }
}
