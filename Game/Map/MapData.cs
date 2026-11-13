using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using BalticDrive.Core;

namespace BalticDrive.Map;

// Reader for the tile packs written by tools/osm_import/build.py (format 3, docs/TILE_FORMAT.md). The binary layout
// and the numeric ids below must stay in step with that script. Plain C# (no Godot types), so the headless tests
// can read packs too.

/// <summary>Land-cover classes of the 4 m raster.</summary>
public static class MapLand
{
    public const byte Open = 0, Forest = 1, Field = 2, Meadow = 3, Water = 4, Residential = 5, Paved = 6,
        Scrub = 7, Wetland = 8, Sand = 9, Orchard = 10, Park = 11, Cemetery = 12, Farmyard = 13;
}

public static class RoadFlag
{
    public const byte Bridge = 1, Tunnel = 2, Oneway = 4, Unpaved = 8, Lit = 16, Rail = 32, StartCap = 64, EndCap = 128;
    /// <summary>Second flag byte: the piece starts / ends at the edge of a junction surface.</summary>
    public const byte JunctionStart = 1, JunctionEnd = 2, OnPaved = 64, Taper = 128;   // flags2 (bits 2..5: lane split)
}

/// <summary>Road classes, most important first. Rail is last and is not drivable.</summary>
public static class RoadClass
{
    public const byte Motorway = 0, Trunk = 1, Primary = 2, Secondary = 3, Tertiary = 4, Unclassified = 5,
        Residential = 6, Living = 7, Service = 8, Track = 9, Link = 10, Rail = 11, Tram = 12;
}

public static class BuildingKind
{
    public const byte Generic = 0, House = 1, Apartments = 2, Commercial = 3, Industrial = 4, Farm = 5,
        Shed = 6, Church = 7, Civic = 8, Tower = 9, Canopy = 10;
}

public static class RoofKind
{
    public const byte Flat = 1, Gabled = 2, Hipped = 3, Pyramidal = 4, Dome = 5;
}

/// <summary>Small things mapped one by one.</summary>
public static class MapPointKind
{
    public const byte Tree = 0, Lamp = 1, Pole = 2, Pylon = 3;
}

public enum MapPoiKind : byte { Fuel, Food, Shop, Medical, Diner, Garage, Police, Hardware, Camp }

/// <summary>A stretch of road inside one tile. Points are tile-local metres, at most 12 m apart.</summary>
public sealed class MapRoad
{
    public byte Cls, Flags, Flags2, MappedLanes;
    public sbyte Layer;
    public float HalfWidth;
    /// <summary>Tapered pieces only (D21; else null): the half width at each point, and the offset that keeps lane
    /// markings in place relative to the edge that does not move (UV.x = lateral offset + this).</summary>
    public float[]? Hw, UOff;
    public float HalfWidthAt(int k) => Hw != null ? Hw[k] : HalfWidth;
    public float[] X = null!, Z = null!;
    /// <summary>Carriageway height at each point (absolute metres; on bridges the deck).</summary>
    public float[] Y = null!;
    /// <summary>How far the carriageway rides above the natural ground (bridges and their ramps).</summary>
    public float[] Lift = null!;
    /// <summary>Distance along the whole road at each point, for lane markings.</summary>
    public float[] Along = null!;
    /// <summary>Metres from each point to the edge of the nearest junction, measured along the road
    /// (negative inside the junction). Lane markings stop where this reaches zero.</summary>
    public float[] Clear = null!;
    /// <summary>+1 where that junction lies ahead (towards later points), -1 where it lies behind.</summary>
    public float[] JunctionAhead = null!;
    /// <summary>Lanes across the whole carriageway: the mapped count when there is one, else from the width.</summary>
    public int Lanes => MappedLanes > 0 ? MappedLanes : Math.Clamp((int)MathF.Floor((HalfWidth * 2f - 0.6f) / 3.3f + 0.35f), 1, 8);
    public bool Oneway => (Flags & RoadFlag.Oneway) != 0;
    public bool IsBridge => (Flags & RoadFlag.Bridge) != 0 && (Flags & RoadFlag.Tunnel) == 0;
    public bool IsRail => (Flags & RoadFlag.Rail) != 0;
    public bool IsTram => Cls == RoadClass.Tram;
    public bool Unpaved => (Flags & RoadFlag.Unpaved) != 0;
    public bool Drivable => !IsRail;
    /// <summary>Lanes in the direction of the points on a two-way road when OSM says (D19); 0 = even split.</summary>
    public int LanesForward => (Flags2 >> 2) & 15;
    /// <summary>Runs across a paved area (a parking aisle, a lane over a square): drawn without verges (D20).</summary>
    public bool OnPaved => (Flags2 & RoadFlag.OnPaved) != 0;
    public bool StartsAtJunction => (Flags2 & RoadFlag.JunctionStart) != 0;
    public bool EndsAtJunction => (Flags2 & RoadFlag.JunctionEnd) != 0;
}

/// <summary>
/// A junction surface (format 4, D11): the area where roads meet, with rounded kerb corners. Outline rings in
/// tile-local metres (ring 0 the outside, the rest islands), a height per vertex and a triangulation.
/// </summary>
public sealed class MapJunction
{
    public float[] X = null!, Z = null!, Y = null!;
    /// <summary>End index (exclusive) of each ring in X/Z/Y.</summary>
    public int[] RingEnds = null!;
    public ushort[] Tris = null!;
    public bool Ghost, Elevated;
    /// <summary>A paved area (parking, square, carriageway area; D20), not a junction: no routing or stop lines.</summary>
    public bool Paved, Pedestrian;
    public float MinX, MinZ, MaxX, MaxZ, Cx, Cz;

    public bool Contains(float x, float z)
    {
        if (x < MinX || x > MaxX || z < MinZ || z > MaxZ) return false;
        bool inside = false;
        int start = 0;
        foreach (int end in RingEnds)
        {
            for (int i = start, j = end - 1; i < end; j = i++)
                if ((Z[i] > z) != (Z[j] > z) && x < (X[j] - X[i]) * (z - Z[i]) / (Z[j] - Z[i]) + X[i])
                    inside = !inside;
            start = end;
        }
        return inside;
    }

    /// <summary>Surface height at a point inside (barycentric over the triangle holding it), else at the nearest vertex.</summary>
    public float HeightAt(float x, float z)
    {
        for (int t = 0; t + 2 < Tris.Length; t += 3)
        {
            int a = Tris[t], b = Tris[t + 1], c = Tris[t + 2];
            float d = (Z[b] - Z[c]) * (X[a] - X[c]) + (X[c] - X[b]) * (Z[a] - Z[c]);
            if (MathF.Abs(d) < 1e-9f) continue;
            float wa = ((Z[b] - Z[c]) * (x - X[c]) + (X[c] - X[b]) * (z - Z[c])) / d;
            float wb = ((Z[c] - Z[a]) * (x - X[c]) + (X[a] - X[c]) * (z - Z[c])) / d;
            float wc = 1f - wa - wb;
            if (wa >= -1e-4f && wb >= -1e-4f && wc >= -1e-4f) return wa * Y[a] + wb * Y[b] + wc * Y[c];
        }
        EdgeDistance(x, z, out float y);
        return y;
    }

    /// <summary>Distance from a point to the outline (any ring); <paramref name="y"/> = height at the nearest outline point.</summary>
    public float EdgeDistance(float x, float z, out float y)
    {
        float best = float.MaxValue; y = Y.Length > 0 ? Y[0] : 0;
        int start = 0;
        foreach (int end in RingEnds)
        {
            for (int i = start, j = end - 1; i < end; j = i++)
            {
                float bx = X[i] - X[j], bz = Z[i] - Z[j];
                float len2 = bx * bx + bz * bz;
                float u = len2 > 1e-8f ? Math.Clamp(((x - X[j]) * bx + (z - Z[j]) * bz) / len2, 0f, 1f) : 0f;
                float ex = x - (X[j] + bx * u), ez = z - (Z[j] + bz * u);
                float d = ex * ex + ez * ez;
                if (d < best) { best = d; y = Y[j] + (Y[i] - Y[j]) * u; }
            }
            start = end;
        }
        return MathF.Sqrt(best);
    }
}

public sealed class MapBuilding
{
    public int Index;
    public byte Type, Flags, Roof;
    public float Height, MinHeight, RoofHeight;
    /// <summary>Rings in tile-local metres. Ring 0 is the outline (positive shoelace area in X,Z); the rest are courtyards.</summary>
    public float[][] RX = null!, RZ = null!;
    /// <summary>Roof triangulation over all rings' vertices in order; only present for outlines with courtyards.</summary>
    public ushort[]? Tris;
    public float MinX, MinZ, MaxX, MaxZ, Cx, Cz;
    /// <summary>Ground level the walls stand on (lowest natural ground under the outline), set when the tile loads.</summary>
    public float Base;

    public bool Ghost => (Flags & 1) != 0;
    public bool IsPart => (Flags & 4) != 0;
    /// <summary>A roof on posts (fuel station canopy, carport): open underneath.</summary>
    public bool IsCanopy => Type == BuildingKind.Canopy;

    public bool Contains(float x, float z)
    {
        if (x < MinX || x > MaxX || z < MinZ || z > MaxZ) return false;
        var rx = RX[0]; var rz = RZ[0];
        bool inside = false;
        for (int i = 0, j = rx.Length - 1; i < rx.Length; j = i++)
            if ((rz[i] > z) != (rz[j] > z) && x < (rx[j] - rx[i]) * (z - rz[i]) / (rz[j] - rz[i]) + rx[i])
                inside = !inside;
        return inside;
    }
}

public sealed class MapPoi
{
    public string Id = "";
    public MapPoiKind Kind;
    public float X, Z;      // tile-local
    public string Name = "";
}

/// <summary>One 2048 m square of the map: roads, buildings, points of interest, land cover, ground and water heights.</summary>
public sealed class MapTile
{
    public const float Size = 2048f;
    public const int LandRes = 512;
    public const float LandCell = Size / LandRes;
    public const int HRes = 256;                   // height cells per side; (HRes + 1)^2 corner values
    public const float HCell = Size / HRes;
    public const int Cells = 32;                   // bucket grid: 64 m, one bucket per finest terrain tile
    public const float CellSize = Size / Cells;
    private const float MaxSegment = 12f;

    public readonly int Tx, Tz;
    public readonly double OriginX, OriginZ;
    public readonly bool Empty;
    public MapRoad[] Roads = Array.Empty<MapRoad>();
    public MapJunction[] Junctions = Array.Empty<MapJunction>();
    public MapBuilding[] Buildings = Array.Empty<MapBuilding>();
    public MapPoi[] Pois = Array.Empty<MapPoi>();
    /// <summary>Mapped trees, lamps, poles and pylons (tile-local metres).</summary>
    public byte[] PointKind = Array.Empty<byte>();
    public float[] PointX = Array.Empty<float>(), PointZ = Array.Empty<float>();
    public byte[] Land;
    /// <summary>Natural ground (water beds carved in), metres, (HRes+1)^2 corners row by row (Z), north-west first.</summary>
    public float[] Ground;
    /// <summary>Water surface at each corner (NaN where dry), or null when the tile has no water at all.</summary>
    public float[]? Water;
    private readonly List<int>?[] _roadCells = new List<int>?[Cells * Cells];
    private readonly List<int>?[] _buildingCells = new List<int>?[Cells * Cells];
    private readonly List<int>?[] _pointCells = new List<int>?[Cells * Cells];
    private readonly List<int>?[] _junctionCells = new List<int>?[Cells * Cells];

    private static readonly byte[] EmptyLand = new byte[LandRes * LandRes];

    /// <summary>A tile with no data: open grassland at the given height. Used outside the mapped region.</summary>
    public MapTile(int tx, int tz, float height)
    {
        Tx = tx; Tz = tz; OriginX = tx * (double)Size; OriginZ = tz * (double)Size;
        Land = EmptyLand; Empty = true;
        Ground = new float[(HRes + 1) * (HRes + 1)];
        Array.Fill(Ground, height);
    }

    public MapTile(int tx, int tz, byte[] raw, float hUnit, float hBase)
    {
        Tx = tx; Tz = tz; OriginX = tx * (double)Size; OriginZ = tz * (double)Size;
        using var ms = new MemoryStream(raw);
        using var r = new BinaryReader(ms);
        if (r.ReadUInt32() != 0x35544442u) throw new InvalidDataException("bad tile magic"); // "BDT5"

        int nRoads = (int)r.ReadUInt32();
        var roads = new List<MapRoad>(nRoads);
        var xs = new List<float>(); var zs = new List<float>(); var ys = new List<float>(); var ls = new List<float>(); var al = new List<float>();
        var hs = new List<float>(); var us = new List<float>();
        for (int i = 0; i < nRoads; i++)
        {
            var road = new MapRoad { Cls = r.ReadByte(), Flags = r.ReadByte(), Flags2 = r.ReadByte() };
            road.HalfWidth = r.ReadByte() * 0.05f;
            road.Layer = r.ReadSByte();
            road.MappedLanes = r.ReadByte();
            float along = r.ReadSingle();
            int n = r.ReadUInt16();
            xs.Clear(); zs.Clear(); ys.Clear(); ls.Clear(); al.Clear(); hs.Clear(); us.Clear();
            bool taper = (road.Flags2 & RoadFlag.Taper) != 0;
            float px = 0, pz = 0, py = 0, pl = 0, ph = 0, pu = 0;
            for (int k = 0; k < n; k++)
            {
                float x = r.ReadInt16() * 0.1f, z = r.ReadInt16() * 0.1f, y = r.ReadUInt16() * hUnit + hBase, l = r.ReadByte() * 0.1f;
                float h = road.HalfWidth, u = 0f;
                if (taper) { h = r.ReadByte() * 0.05f; u = r.ReadSByte() * 0.1f; }
                if (k > 0)
                {
                    float len = MathF.Sqrt((x - px) * (x - px) + (z - pz) * (z - pz));
                    int parts = Math.Max(1, (int)MathF.Ceiling(len / MaxSegment));
                    for (int s = 1; s <= parts; s++)
                    {
                        float t = s / (float)parts;
                        xs.Add(px + (x - px) * t); zs.Add(pz + (z - pz) * t); ys.Add(py + (y - py) * t); ls.Add(pl + (l - pl) * t);
                        hs.Add(ph + (h - ph) * t); us.Add(pu + (u - pu) * t);
                        al.Add(along + len * t);
                    }
                    along += len;
                }
                else { xs.Add(x); zs.Add(z); ys.Add(y); ls.Add(l); al.Add(along); hs.Add(h); us.Add(u); }
                px = x; pz = z; py = y; pl = l; ph = h; pu = u;
            }
            road.X = xs.ToArray(); road.Z = zs.ToArray(); road.Y = ys.ToArray(); road.Lift = ls.ToArray(); road.Along = al.ToArray();
            if (taper) { road.Hw = hs.ToArray(); road.UOff = us.ToArray(); }
            // Railways and trams in tunnels run underground: nothing to draw or drive on.
            if (road.IsRail && (road.Flags & RoadFlag.Tunnel) != 0) continue;
            int index = roads.Count;
            roads.Add(road);
            int segs = Math.Min(road.X.Length - 1, 4095);
            for (int k = 0; k < segs; k++)
                Bucket(_roadCells, MathF.Min(road.X[k], road.X[k + 1]), MathF.Min(road.Z[k], road.Z[k + 1]),
                    MathF.Max(road.X[k], road.X[k + 1]), MathF.Max(road.Z[k], road.Z[k + 1]), (index << 12) | k);
        }
        Roads = roads.ToArray();
        FindJunctions();
        MarkJunctionEnds();

        int nJunctions = (int)r.ReadUInt32();
        Junctions = new MapJunction[nJunctions];
        for (int i = 0; i < nJunctions; i++)
        {
            byte jf = r.ReadByte();
            int rings = r.ReadByte();
            var ends = new int[rings];
            int total = 0;
            for (int q = 0; q < rings; q++) { total += r.ReadUInt16(); ends[q] = total; }
            var j = new MapJunction { RingEnds = ends, Ghost = (jf & 1) != 0, Elevated = (jf & 2) != 0, Paved = (jf & 4) != 0, Pedestrian = (jf & 8) != 0, X = new float[total], Z = new float[total], Y = new float[total] };
            j.MinX = j.MinZ = float.MaxValue; j.MaxX = j.MaxZ = float.MinValue;
            for (int k = 0; k < total; k++)
            {
                j.X[k] = r.ReadInt16() * 0.1f; j.Z[k] = r.ReadInt16() * 0.1f; j.Y[k] = r.ReadUInt16() * hUnit + hBase;
                j.MinX = MathF.Min(j.MinX, j.X[k]); j.MaxX = MathF.Max(j.MaxX, j.X[k]);
                j.MinZ = MathF.Min(j.MinZ, j.Z[k]); j.MaxZ = MathF.Max(j.MaxZ, j.Z[k]);
            }
            int outer = ends.Length > 0 ? ends[0] : total;
            for (int k = 0; k < outer; k++) { j.Cx += j.X[k]; j.Cz += j.Z[k]; }
            if (outer > 0) { j.Cx /= outer; j.Cz /= outer; }
            int nt = (int)r.ReadUInt32();
            j.Tris = new ushort[nt];
            for (int k = 0; k < nt; k++) j.Tris[k] = r.ReadUInt16();
            Junctions[i] = j;
            Bucket(_junctionCells, j.MinX - 10f, j.MinZ - 10f, j.MaxX + 10f, j.MaxZ + 10f, i);
        }

        int nBuildings = (int)r.ReadUInt32();
        Buildings = new MapBuilding[nBuildings];
        for (int i = 0; i < nBuildings; i++)
        {
            var b = new MapBuilding { Index = i, Type = r.ReadByte(), Flags = r.ReadByte(), Roof = r.ReadByte() };
            b.Height = r.ReadUInt16() * 0.1f;
            b.MinHeight = r.ReadUInt16() * 0.1f;
            b.RoofHeight = r.ReadUInt16() * 0.1f;
            int rings = r.ReadByte();
            b.RX = new float[rings][]; b.RZ = new float[rings][];
            b.MinX = b.MinZ = float.MaxValue; b.MaxX = b.MaxZ = float.MinValue;
            for (int q = 0; q < rings; q++)
            {
                int n = r.ReadUInt16();
                var rx = new float[n]; var rz = new float[n];
                for (int k = 0; k < n; k++)
                {
                    rx[k] = r.ReadInt16() * 0.1f; rz[k] = r.ReadInt16() * 0.1f;
                    if (q == 0)
                    {
                        b.MinX = MathF.Min(b.MinX, rx[k]); b.MaxX = MathF.Max(b.MaxX, rx[k]);
                        b.MinZ = MathF.Min(b.MinZ, rz[k]); b.MaxZ = MathF.Max(b.MaxZ, rz[k]);
                        b.Cx += rx[k]; b.Cz += rz[k];
                    }
                }
                if (q == 0 && n > 0) { b.Cx /= n; b.Cz /= n; }
                b.RX[q] = rx; b.RZ[q] = rz;
            }
            if ((b.Flags & 2) != 0)
            {
                int n = r.ReadUInt16();
                b.Tris = new ushort[n];
                for (int k = 0; k < n; k++) b.Tris[k] = r.ReadUInt16();
            }
            Buildings[i] = b;
            Bucket(_buildingCells, b.MinX, b.MinZ, b.MaxX, b.MaxZ, i);
        }

        int nPois = r.ReadUInt16();
        Pois = new MapPoi[nPois];
        for (int i = 0; i < nPois; i++)
        {
            var p = new MapPoi { Kind = (MapPoiKind)r.ReadByte(), X = r.ReadUInt16() * 0.1f, Z = r.ReadUInt16() * 0.1f };
            int len = r.ReadByte();
            p.Name = Encoding.UTF8.GetString(r.ReadBytes(len));
            p.Id = $"m{tx}_{tz}_{i}";
            Pois[i] = p;
        }

        int nPoints = (int)r.ReadUInt32();
        PointKind = new byte[nPoints]; PointX = new float[nPoints]; PointZ = new float[nPoints];
        for (int i = 0; i < nPoints; i++)
        {
            PointKind[i] = r.ReadByte();
            PointX[i] = r.ReadUInt16() * 0.1f; PointZ[i] = r.ReadUInt16() * 0.1f;
            Bucket(_pointCells, PointX[i], PointZ[i], PointX[i], PointZ[i], i);
        }

        Land = r.ReadBytes(LandRes * LandRes);
        const int corners = (HRes + 1) * (HRes + 1);
        Ground = new float[corners];
        for (int i = 0; i < corners; i++) Ground[i] = r.ReadUInt16() * hUnit + hBase;
        if (r.ReadByte() == 1)
        {
            Water = new float[corners];
            for (int i = 0; i < corners; i++)
            {
                int v = r.ReadUInt16();
                Water[i] = v == 0 ? float.NaN : v * hUnit + hBase;
            }
        }
        foreach (var b in Buildings) b.Base = FootprintBase(b);
    }

    /// <summary>Lowest natural ground under an outline's corners and centre (walls go down to it; a slope shows plinth).</summary>
    private float FootprintBase(MapBuilding b)
    {
        float low = GroundAt(b.Cx, b.Cz);
        var rx = b.RX[0]; var rz = b.RZ[0];
        int step = Math.Max(1, rx.Length / 24);
        for (int k = 0; k < rx.Length; k += step) low = MathF.Min(low, GroundAt(rx[k], rz[k]));
        return low;
    }

    /// <summary>
    /// Works out where roads meet, so markings can stop short of junctions. A junction is a point where three or more
    /// road arms come together; driveways and field tracks do not count, nor a road that continues under another name.
    /// </summary>
    private void FindJunctions()
    {
        static bool Counts(MapRoad r) => r.Drivable && r.Cls != RoadClass.Service && r.Cls != RoadClass.Track;
        static long Key(float x, float z) => ((long)MathF.Round(x * 10f) << 32) ^ ((long)MathF.Round(z * 10f) & 0xffffffffL);
        var arms = new Dictionary<long, (int count, float reach)>();
        foreach (var r in Roads)
        {
            if (!Counts(r)) continue;
            int n = r.X.Length;
            for (int k = 0; k < n; k++)
            {
                long key = Key(r.X[k], r.Z[k]);
                arms.TryGetValue(key, out var a);
                a.count += k == 0 || k == n - 1 ? 1 : 2;
                a.reach = MathF.Max(a.reach, r.HalfWidth);
                arms[key] = a;
            }
        }
        foreach (var r in Roads)
        {
            int n = r.X.Length;
            r.Clear = new float[n];
            r.JunctionAhead = new float[n];
            Array.Fill(r.Clear, 1000f);
            if (!Counts(r)) continue;
            float since = float.MaxValue;
            for (int k = 0; k < n; k++)
            {
                if (k > 0 && since < float.MaxValue) since += r.Along[k] - r.Along[k - 1];
                if (arms.TryGetValue(Key(r.X[k], r.Z[k]), out var a) && a.count >= 3) since = -(a.reach + 1.2f);
                if (since < r.Clear[k]) { r.Clear[k] = since; r.JunctionAhead[k] = -1f; }
            }
            since = float.MaxValue;
            for (int k = n - 1; k >= 0; k--)
            {
                if (k < n - 1 && since < float.MaxValue) since += r.Along[k + 1] - r.Along[k];
                if (arms.TryGetValue(Key(r.X[k], r.Z[k]), out var a) && a.count >= 3) since = -(a.reach + 1.2f);
                if (since < r.Clear[k]) { r.Clear[k] = since; r.JunctionAhead[k] = 1f; }
            }
        }
    }

    /// <summary>Road pieces that start or end at a junction surface: markings stop there (with a stop line).</summary>
    private void MarkJunctionEnds()
    {
        foreach (var r in Roads)
        {
            int n = r.X.Length;
            if (r.StartsAtJunction)
                for (int k = 0; k < n; k++)
                {
                    float d = r.Along[k] - r.Along[0];
                    if (d < r.Clear[k]) { r.Clear[k] = d; r.JunctionAhead[k] = -1f; }
                }
            if (r.EndsAtJunction)
                for (int k = 0; k < n; k++)
                {
                    float d = r.Along[n - 1] - r.Along[k];
                    if (d < r.Clear[k]) { r.Clear[k] = d; r.JunctionAhead[k] = 1f; }
                }
        }
    }

    private static void Bucket(List<int>?[] cells, float x0, float z0, float x1, float z1, int value)
    {
        int cx0 = Math.Clamp((int)MathF.Floor(x0 / CellSize), 0, Cells - 1), cx1 = Math.Clamp((int)MathF.Floor(x1 / CellSize), 0, Cells - 1);
        int cz0 = Math.Clamp((int)MathF.Floor(z0 / CellSize), 0, Cells - 1), cz1 = Math.Clamp((int)MathF.Floor(z1 / CellSize), 0, Cells - 1);
        for (int cz = cz0; cz <= cz1; cz++)
            for (int cx = cx0; cx <= cx1; cx++)
                (cells[cz * Cells + cx] ??= new List<int>(8)).Add(value);
    }

    public List<int>? RoadCell(int cx, int cz) => _roadCells[cz * Cells + cx];
    public List<int>? BuildingCell(int cx, int cz) => _buildingCells[cz * Cells + cx];
    public List<int>? PointCell(int cx, int cz) => _pointCells[cz * Cells + cx];
    public List<int>? JunctionCell(int cx, int cz) => _junctionCells[cz * Cells + cx];

    // ---- rasters (tile-local coordinates; lookups clamp to this tile so they never need a neighbour) ----

    public byte LandAt(float lx, float lz)
    {
        int i = Math.Clamp((int)(lx / LandCell), 0, LandRes - 1), j = Math.Clamp((int)(lz / LandCell), 0, LandRes - 1);
        return Land[j * LandRes + i];
    }

    /// <summary>Bilinear blend of a per-class value (see the look-up tables in <see cref="MapWorld"/>).</summary>
    public float LandBlend(float lx, float lz, float[] lut)
    {
        float fx = lx / LandCell - 0.5f, fz = lz / LandCell - 0.5f;
        int i0 = (int)MathF.Floor(fx), j0 = (int)MathF.Floor(fz);
        float u = fx - i0, v = fz - j0;
        int i1 = Math.Clamp(i0 + 1, 0, LandRes - 1), j1 = Math.Clamp(j0 + 1, 0, LandRes - 1);
        i0 = Math.Clamp(i0, 0, LandRes - 1); j0 = Math.Clamp(j0, 0, LandRes - 1);
        float a = lut[Land[j0 * LandRes + i0]], b = lut[Land[j0 * LandRes + i1]];
        float c = lut[Land[j1 * LandRes + i0]], d = lut[Land[j1 * LandRes + i1]];
        return (a + (b - a) * u) * (1f - v) + (c + (d - c) * u) * v;
    }

    /// <summary>
    /// Natural ground at a tile-local point: Catmull-Rom over the 8 m grid (smooth slopes, no facets at 1–2 m mesh
    /// spacing). Along a tile edge it uses only that edge's values, so neighbouring tiles meet exactly.
    /// </summary>
    public float GroundAt(float lx, float lz)
    {
        float fx = Math.Clamp(lx / HCell, 0f, HRes), fz = Math.Clamp(lz / HCell, 0f, HRes);
        int i = Math.Min((int)fx, HRes - 1), j = Math.Min((int)fz, HRes - 1);
        float u = fx - i, v = fz - j;
        Span<float> col = stackalloc float[4];
        for (int m = 0; m < 4; m++)
        {
            int jj = Math.Clamp(j - 1 + m, 0, HRes);
            int row = jj * (HRes + 1);
            col[m] = CatmullRom(Ground[row + Math.Clamp(i - 1, 0, HRes)], Ground[row + i], Ground[row + i + 1], Ground[row + Math.Clamp(i + 2, 0, HRes)], u);
        }
        return CatmullRom(col[0], col[1], col[2], col[3], v);
    }

    private static float CatmullRom(float p0, float p1, float p2, float p3, float t)
        => p1 + 0.5f * t * (p2 - p0 + t * (2f * p0 - 5f * p1 + 4f * p2 - p3 + t * (3f * (p1 - p2) + p3 - p0)));

    /// <summary>Water surface at a tile-local point, or NaN when dry (all four surrounding corners must be wet).</summary>
    public float WaterAt(float lx, float lz)
    {
        if (Water == null) return float.NaN;
        float fx = Math.Clamp(lx / HCell, 0f, HRes - 0.0001f), fz = Math.Clamp(lz / HCell, 0f, HRes - 0.0001f);
        int i = (int)fx, j = (int)fz;
        float u = fx - i, v = fz - j;
        int w = HRes + 1;
        float a = Water[j * w + i], b = Water[j * w + i + 1], c = Water[(j + 1) * w + i], d = Water[(j + 1) * w + i + 1];
        return (a + (b - a) * u) * (1f - v) + (c + (d - c) * u) * v; // NaN propagates when any corner is dry
    }
}

/// <summary>A named settlement, for the HUD and for choosing where a drive starts.</summary>
public sealed class MapPlace
{
    public string Name = "";
    public int Rank;       // 0 city, 1 town, 2 village
    public double X, Z;
    public int Population;
}

/// <summary>Opens a map (index + pack files) and hands out tiles on demand. Safe to call from worker threads.</summary>
public sealed class MapData : IDisposable
{
    public const int Format = 5;
    public readonly string Name;
    public readonly string Directory;
    public readonly List<MapPlace> Places = new();
    public string Projection = "";
    public float HUnit, HBase;
    public int TileCount => _index.Count;
    public int MinTx { get; private set; } = int.MaxValue;
    public int MaxTx { get; private set; } = int.MinValue;
    public int MinTz { get; private set; } = int.MaxValue;
    public int MaxTz { get; private set; } = int.MinValue;
    /// <summary>Ground height given to tiles outside the map (the median of the map's edge would be nicer; later).</summary>
    public float OutsideHeight = 100f;

    private readonly struct Entry(int pak, uint offset, uint length, uint raw)
    {
        public readonly int Pak = pak; public readonly uint Offset = offset, Length = length, RawLength = raw;
    }

    private readonly Dictionary<long, Entry> _index = new();
    private readonly Dictionary<int, FileStream> _paks = new();
    private readonly object _ioLock = new();
    private readonly ConcurrentDictionary<long, Lazy<MapTile>> _tiles = new();

    private static long Key(int tx, int tz) => ((long)tx << 32) ^ (uint)tz;

    public static string IndexPath(string dir, string name) => Path.Combine(dir, name + ".idx");

    /// <param name="dir">Folder holding <c>name.idx</c>, <c>name.NN.pak</c> and <c>name.places.json</c> (an OS path).</param>
    public MapData(string dir, string name)
    {
        Name = name; Directory = dir;
        var bytes = File.ReadAllBytes(IndexPath(dir, name));
        using var r = new BinaryReader(new MemoryStream(bytes));
        if (r.ReadUInt32() != 0x58494442u) throw new InvalidDataException("bad map index magic"); // "BDIX"
        uint version = r.ReadUInt32();
        if (version != Format) throw new InvalidDataException($"map '{name}' is format {version}; this build reads format {Format}: rebuild it with tools/osm_import");
        float tile = r.ReadSingle();
        uint landRes = r.ReadUInt32(), hRes = r.ReadUInt32();
        HUnit = r.ReadSingle(); HBase = r.ReadSingle();
        Projection = Encoding.UTF8.GetString(r.ReadBytes(r.ReadUInt16()));
        if (tile != MapTile.Size || landRes != MapTile.LandRes || hRes != MapTile.HRes)
            throw new InvalidDataException($"map '{name}' has tiles of {tile} m / {landRes} / {hRes}; expected {MapTile.Size} / {MapTile.LandRes} / {MapTile.HRes}");
        if (Projection != Core.Projection.Proj4)
            throw new InvalidDataException($"map '{name}' uses another projection ({Projection})");
        int count = (int)r.ReadUInt32();
        for (int i = 0; i < count; i++)
        {
            int tx = r.ReadInt32(), tz = r.ReadInt32(), pak = r.ReadUInt16();
            uint off = r.ReadUInt32(), len = r.ReadUInt32(), raw = r.ReadUInt32();
            _index[Key(tx, tz)] = new Entry(pak, off, len, raw);
            MinTx = Math.Min(MinTx, tx); MaxTx = Math.Max(MaxTx, tx); MinTz = Math.Min(MinTz, tz); MaxTz = Math.Max(MaxTz, tz);
        }
        Places.AddRange(ReadPlaces(dir, name));
        Log.Info("MapData", $"Map '{name}': {count} tiles, {Places.Count} named places");
    }

    /// <summary>Named settlements of a map, biggest first. Readable without opening the whole map.</summary>
    public static List<MapPlace> ReadPlaces(string dir, string name)
    {
        var list = new List<MapPlace>();
        string path = Path.Combine(dir, name + ".places.json");
        if (!File.Exists(path)) return list;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            foreach (var e in doc.RootElement.EnumerateArray())
                list.Add(new MapPlace
                {
                    Name = e.GetProperty("n").GetString() ?? "", Rank = e.GetProperty("r").GetInt32(),
                    X = e.GetProperty("x").GetDouble(), Z = e.GetProperty("z").GetDouble(), Population = e.GetProperty("p").GetInt32(),
                });
        }
        catch (Exception ex) { Log.Warn("MapData", $"Could not read {path}: {ex.Message}"); }
        return list;
    }

    public MapPlace? FindPlace(string name)
        => Places.Find(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool HasTile(int tx, int tz) => _index.ContainsKey(Key(tx, tz));

    public MapTile Tile(int tx, int tz)
        => _tiles.GetOrAdd(Key(tx, tz), _ => new Lazy<MapTile>(() => Load(tx, tz))).Value;

    public MapTile TileAt(double wx, double wz)
        => Tile((int)Math.Floor(wx / MapTile.Size), (int)Math.Floor(wz / MapTile.Size));

    private MapTile Load(int tx, int tz)
    {
        if (!_index.TryGetValue(Key(tx, tz), out var e)) return new MapTile(tx, tz, OutsideHeight);
        try
        {
            var comp = new byte[e.Length];
            lock (_ioLock)
            {
                if (!_paks.TryGetValue(e.Pak, out var f))
                {
                    string path = Path.Combine(Directory, $"{Name}.{e.Pak:00}.pak");
                    f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    _paks[e.Pak] = f;
                }
                f.Seek(e.Offset, SeekOrigin.Begin);
                f.ReadExactly(comp);
            }
            var raw = new byte[e.RawLength];
            using (var z = new ZLibStream(new MemoryStream(comp), CompressionMode.Decompress))
                z.ReadExactly(raw);
            return new MapTile(tx, tz, raw, HUnit, HBase);
        }
        catch (Exception ex)
        {
            Log.Error("MapData", $"Tile {tx},{tz} could not be read: {ex.Message}");
            return new MapTile(tx, tz, OutsideHeight);
        }
    }

    /// <summary>Drops tiles far from the focus. Workers that still hold one keep it alive until they finish.</summary>
    public void Trim(double wx, double wz, int keepRadius)
    {
        int fx = (int)Math.Floor(wx / MapTile.Size), fz = (int)Math.Floor(wz / MapTile.Size);
        foreach (var key in _tiles.Keys)
        {
            int tx = (int)(key >> 32), tz = (int)(uint)key;
            if (Math.Abs(tx - fx) > keepRadius || Math.Abs(tz - fz) > keepRadius) _tiles.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        lock (_ioLock)
        {
            foreach (var f in _paks.Values) f.Dispose();
            _paks.Clear();
        }
    }
}
