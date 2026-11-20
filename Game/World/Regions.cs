using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using BalticDrive.Core;
using BalticDrive.Map;
using BalticDrive.Trees;

namespace BalticDrive.World;

/// <summary>
/// Regions that change how the world is generated (D91, D94), from <c>GameData/regions.json</c> (outlines in
/// longitude/latitude, projected here once):
/// * <b>regions</b> (voivodeships): a BadBuilding preset ("podlasie": Podlasie's wooden houses, BadBuilding D40) and
///   tree mixes per land class; past the outline both fade out over <c>blend_km</c>, decided per building or tree, so
///   a border isn't a line where the villages and woods change;
/// * <b>zones</b> (named forests and marshes: Białowieża, Augustów, Knyszyn, Biebrza): tree mixes that win over their
///   region's inside their outline (rough outlines; coherent over exact), fading over their own <c>blend_km</c>.
/// </summary>
public static class Regions
{
    public const string DataPath = "res://GameData/regions.json";

    /// <summary>What grows on one land class: species groups by weight, and the size range.</summary>
    public sealed record TreeMix((int First, int Variants, float Weight)[] Items, float Total, float MinScale, float MaxScale)
    {
        public int Pick(float r, uint h)
        {
            float x = r * Total;
            foreach (var (first, variants, w) in Items)
            {
                if (x < w) return first + (int)(h % (uint)variants);
                x -= w;
            }
            return Items[^1].First;
        }
    }

    public sealed record Area(string Name, bool Zone, string? Preset, float Blend, double[] X, double[] Z,
        double MinX, double MinZ, double MaxX, double MaxZ, Dictionary<byte, TreeMix> Mixes);

    private static List<Area>? _all;
    private static readonly object Gate = new();

    public static IReadOnlyList<Area> All
    {
        get
        {
            if (_all != null) return _all;
            lock (Gate) return _all ??= Load();
        }
    }

    private static (int, int) Species(string name) => name switch
    {
        "pine" => (TreeSpecies.PineA, 3),
        "spruce" => (TreeSpecies.SpruceA, 2),
        "oak" => (TreeSpecies.OakA, 3),
        "birch" => (TreeSpecies.BirchA, 3),
        "birch_autumn" => (TreeSpecies.BirchAutumnA, 2),
        "maple" => (TreeSpecies.MapleAutumn, 1),
        "poplar" => (TreeSpecies.Poplar, 1),
        "willow" => (TreeSpecies.Willow, 1),
        "sparse" => (TreeSpecies.Sparse, 1),
        _ => throw new FormatException($"unknown tree '{name}'"),
    };

    private static byte LandOf(string name) => name switch
    {
        "forest" => MapLand.Forest, "wetland" => MapLand.Wetland, "scrub" => MapLand.Scrub, "park" => MapLand.Park,
        _ => throw new FormatException($"unknown land '{name}'"),
    };

    private static List<Area> Load()
    {
        var list = new List<Area>();
        try
        {
            using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(DataPath));
            // zones first: they win over their region
            if (doc.RootElement.TryGetProperty("zones", out var zones))
                foreach (var r in zones.EnumerateArray()) list.Add(Read(r, true));
            foreach (var r in doc.RootElement.GetProperty("regions").EnumerateArray()) list.Add(Read(r, false));
            Log.Info("Regions", $"{list.Count} area(s): {string.Join(", ", list.ConvertAll(r => r.Name))}");
        }
        catch (Exception e) { Log.Error("Regions", e.Message); }
        return list;
    }

    private static Area Read(JsonElement r, bool zone)
    {
        var pts = r.GetProperty("outline");
        int n = pts.GetArrayLength();
        var xs = new double[n]; var zs = new double[n];
        int i = 0;
        foreach (var p in pts.EnumerateArray())
        {
            var (x, z) = Core.Projection.ToWorld(p[1].GetDouble(), p[0].GetDouble());
            xs[i] = x; zs[i] = z; i++;
        }
        float blend = r.TryGetProperty("blend_km", out var b) ? (float)b.GetDouble() * 1000f : 0f;
        double minX = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxZ = double.MinValue;
        for (int k = 0; k < n; k++) { minX = Math.Min(minX, xs[k]); maxX = Math.Max(maxX, xs[k]); minZ = Math.Min(minZ, zs[k]); maxZ = Math.Max(maxZ, zs[k]); }
        var mixes = new Dictionary<byte, TreeMix>();
        if (r.TryGetProperty("trees", out var trees))
            foreach (var land in trees.EnumerateObject())
            {
                var items = new List<(int, int, float)>();
                float total = 0;
                foreach (var it in land.Value.GetProperty("mix").EnumerateArray())
                {
                    var (first, variants) = Species(it[0].GetString()!);
                    float w = (float)it[1].GetDouble();
                    items.Add((first, variants, w)); total += w;
                }
                var sc = land.Value.TryGetProperty("scale", out var s) ? ((float)s[0].GetDouble(), (float)s[1].GetDouble()) : (0f, 0f);
                mixes[LandOf(land.Name)] = new TreeMix(items.ToArray(), total, sc.Item1, sc.Item2);
            }
        return new Area(r.GetProperty("name").GetString()!, zone, r.TryGetProperty("preset", out var pr) ? pr.GetString() : null, blend, xs, zs,
            minX - blend, minZ - blend, maxX + blend, maxZ + blend, mixes);
    }

    /// <summary>The preset for a building at world (x, z), or null. <paramref name="seed"/> decides it in the fade.</summary>
    public static string? PresetAt(double x, double z, uint seed)
    {
        foreach (var r in All)
            if (r.Preset != null && Takes(r, x, z, seed)) return r.Preset;
        return null;
    }

    /// <summary>The areas whose reach (outline and fade) touches the square [x0, x1) × [z0, z1): what a tree cell asks.</summary>
    public static List<Area> Near(double x0, double z0, double x1, double z1)
    {
        var near = new List<Area>();
        foreach (var r in All)
            if (r.Mixes.Count > 0 && x1 >= r.MinX && x0 <= r.MaxX && z1 >= r.MinZ && z0 <= r.MaxZ) near.Add(r);
        return near;
    }

    /// <summary>The tree mix for a land class at (x, z) among <paramref name="near"/> (zones first), or null (the
    /// default, Mazowsze's). <paramref name="seed"/> decides it in a fade.</summary>
    public static TreeMix? TreesAt(List<Area> near, byte land, double x, double z, uint seed)
    {
        foreach (var r in near)
            if (r.Mixes.TryGetValue(land, out var mix) && Takes(r, x, z, seed)) return mix;
        return null;
    }

    /// <summary>The name of the region at (x, z), or null (for the HUD and tests).</summary>
    public static string? NameAt(double x, double z)
    {
        foreach (var r in All)
            if (!r.Zone && x >= r.MinX && x <= r.MaxX && z >= r.MinZ && z <= r.MaxZ && Inside(r, x, z)) return r.Name;
        return null;
    }

    /// <summary>Inside, or in the fade past the outline and chosen by the seed (70 % at the line, none at blend_km).</summary>
    private static bool Takes(Area r, double x, double z, uint seed)
    {
        if (x < r.MinX || x > r.MaxX || z < r.MinZ || z > r.MaxZ) return false;
        if (Inside(r, x, z)) return true;
        if (r.Blend <= 0) return false;
        double d = Distance(r, x, z);
        if (d >= r.Blend) return false;
        return Hash.F01(Hash.Mix(seed ^ 0x9e3779b9u)) < 0.7 * (1 - d / r.Blend);
    }

    private static bool Inside(Area r, double x, double z)
    {
        bool inside = false;
        for (int i = 0, j = r.X.Length - 1; i < r.X.Length; j = i++)
            if ((r.Z[i] > z) != (r.Z[j] > z) && x < (r.X[j] - r.X[i]) * (z - r.Z[i]) / (r.Z[j] - r.Z[i]) + r.X[i])
                inside = !inside;
        return inside;
    }

    private static double Distance(Area r, double x, double z)
    {
        double best = double.MaxValue;
        for (int i = 0, j = r.X.Length - 1; i < r.X.Length; j = i++)
        {
            double ax = r.X[j], az = r.Z[j], dx = r.X[i] - ax, dz = r.Z[i] - az;
            double l2 = dx * dx + dz * dz;
            double t = l2 > 0 ? Math.Clamp(((x - ax) * dx + (z - az) * dz) / l2, 0, 1) : 0;
            double ex = ax + t * dx - x, ez = az + t * dz - z;
            best = Math.Min(best, ex * ex + ez * ez);
        }
        return Math.Sqrt(best);
    }
}
