using System;
using System.Collections.Generic;
using System.Diagnostics;
using BalticDrive.Map;
using Godot;

namespace BalticDrive.World;

/// <summary>Address of a terrain tile in the nested LOD grid. Level 0 tiles are <see cref="TerrainBuilder.BaseTile"/> m.</summary>
public readonly record struct TileKey(int Level, long X, long Z)
{
    public double Size => TerrainBuilder.BaseTile * (double)(1L << Level);
    public double MinX => X * Size;
    public double MinZ => Z * Size;
}

/// <summary>CPU-side terrain tile, built on a worker thread; engine resources are made on the main thread.</summary>
public sealed class TerrainTileData
{
    public required TileKey Key { get; init; }
    public required Vector3[] Vertices { get; init; }
    public required Vector3[] Normals { get; init; }
    public required Color[] Colors { get; init; }
    public required int[] Indices { get; init; }
    public Vector3[]? WaterVertices { get; init; }
    /// <summary>Per water vertex: R = depth / 20 m, G = kind (0.5 lake: Thornwake's water shader), B = plume.</summary>
    public Color[]? WaterColors { get; init; }
    public int[]? WaterIndices { get; init; }
    /// <summary>Ground triangles for collision (level 0 only), 3 vertices each.</summary>
    public Vector3[]? CollisionFaces { get; init; }
    public double BuildMs { get; init; }
}

/// <summary>
/// Builds one LOD tile of the real ground: a 33×33 grid (from 2 m spacing at level 0 to 256 m at level 7) with skirts
/// that hide cracks between neighbours of any resolution — Thornwake's TileBuilder layout, fed from the map's DEM grid
/// and land cover instead of a procedural sampler. Up to level <see cref="RoadLevels"/> the ground is shaped by the
/// roads (level beds, embankments) exactly as the road meshes expect.
/// </summary>
public static class TerrainBuilder
{
    public const double BaseTile = 64;
    public const int Cells = 32, Side = Cells + 1;
    /// <summary>Levels whose ground follows the road beds (tiles up to 256 m, 8 m spacing): the road meshes are drawn
    /// within their range only.</summary>
    public const int RoadLevels = 2;
    private const float RoadDepress = 0.18f; // ground sunk under a carriageway so the road ribbon never fights it

    [ThreadStatic] private static MapContext? _ctx;

    public static TerrainTileData Build(MapWorld world, TileKey key)
    {
        var watch = Stopwatch.StartNew();
        double size = key.Size, x0 = key.MinX, z0 = key.MinZ;
        float spacing = (float)(size / Cells);
        // Samples with a one-cell border all round, for normals that match across tile edges.
        const int S = Side + 2;
        var h = new float[S * S];
        var color = new Color[Side * Side];
        var water = new float[Side * Side];
        bool anyWater = false;

        MapContext? ctx = null;
        if (key.Level <= RoadLevels)
        {
            ctx = _ctx;
            if (ctx == null || ctx.World != world) _ctx = ctx = new MapContext(world);
            ctx.Reset(x0, z0, -spacing, -spacing, (float)size + spacing, (float)size + spacing, withPoints: false);
        }

        for (int j = 0; j < S; j++)
            for (int i = 0; i < S; i++)
            {
                float lx = (i - 1) * spacing, lz = (j - 1) * spacing;
                bool inner = i >= 1 && i <= Side && j >= 1 && j <= Side;
                float dirt;
                if (ctx != null) h[j * S + i] = ctx.Height(lx, lz, RoadDepress, out dirt);
                else
                {
                    double wx = x0 + lx, wz = z0 + lz;
                    var tile = world.Data.TileAt(wx, wz);
                    float tlx = (float)(wx - tile.OriginX), tlz = (float)(wz - tile.OriginZ);
                    h[j * S + i] = tile.GroundAt(tlx, tlz);
                    dirt = inner && !tile.Empty ? tile.LandBlend(tlx, tlz, MapWorld.DirtLut) : 0f;
                }
                if (!inner) continue;
                int v = (j - 1) * Side + (i - 1);
                {
                    double wx = x0 + lx, wz = z0 + lz;
                    var tile = world.Data.TileAt(wx, wz);
                    float tlx = (float)(wx - tile.OriginX), tlz = (float)(wz - tile.OriginZ);
                    float forest = tile.Empty ? 0.1f : tile.LandBlend(tlx, tlz, MapWorld.ForestLut);
                    float field = tile.Empty ? 0f : tile.LandBlend(tlx, tlz, MapWorld.FieldLut);
                    float wet = tile.Empty ? 0f : tile.LandBlend(tlx, tlz, MapWorld.WaterLut);
                    color[v] = new Color(forest, field, dirt, wet);
                    float wl = tile.WaterAt(tlx, tlz);
                    water[v] = wl;
                    if (!float.IsNaN(wl)) anyWater = true;
                }
            }

        // Vertices, normals (central differences over the bordered grid), skirts.
        int skirt = Side * 4;
        var vertices = new Vector3[Side * Side + skirt];
        var normals = new Vector3[Side * Side + skirt];
        var colors = new Color[Side * Side + skirt];
        for (int j = 0; j < Side; j++)
            for (int i = 0; i < Side; i++)
            {
                int v = j * Side + i, s = (j + 1) * S + (i + 1);
                vertices[v] = new Vector3(i * spacing, h[s], j * spacing);
                float dx = h[s + 1] - h[s - 1], dz = h[s + S] - h[s - S];
                normals[v] = new Vector3(-dx, 2f * spacing, -dz).Normalized();
                colors[v] = color[v];
            }
        var edge = new int[skirt];
        for (int k = 0; k < Side; k++)
        {
            edge[k] = k;                                  // north edge, west → east
            edge[Side + k] = k * Side + Cells;            // east edge, north → south
            edge[2 * Side + k] = Cells * Side + (Cells - k); // south edge, east → west
            edge[3 * Side + k] = (Cells - k) * Side;      // west edge, south → north
        }
        float drop = MathF.Max(1.5f, spacing * 0.6f);
        for (int e = 0; e < skirt; e++)
        {
            int src = edge[e];
            vertices[Side * Side + e] = vertices[src] - new Vector3(0, drop, 0);
            normals[Side * Side + e] = normals[src];
            colors[Side * Side + e] = colors[src];
        }

        var indices = new List<int>(Cells * Cells * 6 + skirt * 12);
        for (int j = 0; j < Cells; j++)
            for (int i = 0; i < Cells; i++)
            {
                int a = j * Side + i, b = a + 1, c = a + Side, d = c + 1;
                // Godot fronts are clockwise seen from the front (above).
                if (((i + j) & 1) == 0) { indices.Add(a); indices.Add(b); indices.Add(d); indices.Add(a); indices.Add(d); indices.Add(c); }
                else { indices.Add(a); indices.Add(b); indices.Add(c); indices.Add(b); indices.Add(d); indices.Add(c); }
            }
        for (int side = 0; side < 4; side++)
            for (int k = 0; k < Cells; k++)
            {
                int e0 = side * Side + k, e1 = e0 + 1;
                int t0 = edge[e0], t1 = edge[e1], b0 = Side * Side + e0, b1 = Side * Side + e1;
                // double-sided strip: seen from either side depending on the neighbour
                indices.Add(t0); indices.Add(t1); indices.Add(b1); indices.Add(t0); indices.Add(b1); indices.Add(b0);
                indices.Add(t0); indices.Add(b1); indices.Add(t1); indices.Add(t0); indices.Add(b0); indices.Add(b1);
            }

        Vector3[]? faces = null;
        if (key.Level == 0)
        {
            faces = new Vector3[Cells * Cells * 6];
            for (int t = 0; t < Cells * Cells * 6; t++) faces[t] = vertices[indices[t]];
        }

        Vector3[]? wv = null; int[]? wi = null; Color[]? wc = null;
        if (anyWater) BuildWater(water, vertices, spacing, out wv, out wi, out wc);

        return new TerrainTileData
        {
            Key = key, Vertices = vertices, Normals = normals, Colors = colors, Indices = indices.ToArray(),
            WaterVertices = wv, WaterIndices = wi, WaterColors = wc, CollisionFaces = faces, BuildMs = watch.Elapsed.TotalMilliseconds,
        };
    }

    /// <summary>A water sheet over every cell with at least one wet corner; dry corners take the mean of the wet ones
    /// (the sheet runs a little way up the bank and disappears under the ground there).</summary>
    private static void BuildWater(float[] level, Vector3[] ground, float spacing, out Vector3[]? vertices, out int[]? indices, out Color[]? colors)
    {
        var map = new int[Side * Side];
        Array.Fill(map, -1);
        var v = new List<Vector3>();
        var col = new List<Color>();
        var idx = new List<int>();
        Span<int> corner = stackalloc int[4];
        for (int j = 0; j < Cells; j++)
            for (int i = 0; i < Cells; i++)
            {
                corner[0] = j * Side + i; corner[1] = corner[0] + 1; corner[2] = corner[0] + Side; corner[3] = corner[2] + 1;
                float sum = 0; int wet = 0;
                for (int k = 0; k < 4; k++)
                    if (!float.IsNaN(level[corner[k]])) { sum += level[corner[k]]; wet++; }
                if (wet == 0) continue;
                float mean = sum / wet;
                for (int k = 0; k < 4; k++)
                {
                    int c = corner[k];
                    if (map[c] >= 0) continue;
                    float y = float.IsNaN(level[c]) ? mean : level[c];
                    map[c] = v.Count;
                    v.Add(new Vector3(c % Side * spacing, y, c / Side * spacing));
                    col.Add(new Color(Math.Clamp((y - ground[c].Y) / 20f, 0f, 1f), 0.5f, 0f, 1f));
                }
                idx.Add(map[corner[0]]); idx.Add(map[corner[1]]); idx.Add(map[corner[3]]);
                idx.Add(map[corner[0]]); idx.Add(map[corner[3]]); idx.Add(map[corner[2]]);
            }
        vertices = v.Count > 0 ? v.ToArray() : null;
        indices = v.Count > 0 ? idx.ToArray() : null;
        colors = v.Count > 0 ? col.ToArray() : null;
    }
}
