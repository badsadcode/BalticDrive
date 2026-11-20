using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using BalticDrive.Core;

namespace BalticDrive.World;

/// <summary>
/// Regions that change how the world is generated (D91): each has an outline and a BadBuilding preset ("podlasie":
/// Podlasie's painted wooden houses, BadBuilding D40). Inside the outline every building gets the preset; past it the
/// chance fades to nothing over <c>blend_km</c>, decided per building, so a voivodeship border isn't a line where the
/// villages change. Data: <c>GameData/regions.json</c> (outlines in longitude/latitude, projected here once).
/// </summary>
public static class Regions
{
    public const string DataPath = "res://GameData/regions.json";

    private sealed record Region(string Name, string Preset, float Blend, double[] X, double[] Z,
        double MinX, double MinZ, double MaxX, double MaxZ);

    private static List<Region>? _all;
    private static readonly object Gate = new();

    private static List<Region> All
    {
        get
        {
            if (_all != null) return _all;
            lock (Gate) return _all ??= Load();
        }
    }

    private static List<Region> Load()
    {
        var list = new List<Region>();
        try
        {
            using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(DataPath));
            foreach (var r in doc.RootElement.GetProperty("regions").EnumerateArray())
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
                list.Add(new Region(r.GetProperty("name").GetString()!, r.GetProperty("preset").GetString()!, blend, xs, zs,
                    minX - blend, minZ - blend, maxX + blend, maxZ + blend));
            }
            Log.Info("Regions", $"{list.Count} region(s): {string.Join(", ", list.ConvertAll(r => $"{r.Name} ({r.Preset})"))}");
        }
        catch (Exception e) { Log.Error("Regions", e.Message); }
        return list;
    }

    /// <summary>The preset for a building at world (x, z), or null. <paramref name="seed"/> decides it in the fade.</summary>
    public static string? PresetAt(double x, double z, uint seed)
    {
        foreach (var r in All)
        {
            if (x < r.MinX || x > r.MaxX || z < r.MinZ || z > r.MaxZ) continue;
            if (Inside(r, x, z)) return r.Preset;
            if (r.Blend <= 0) continue;
            double d = Distance(r, x, z);
            if (d >= r.Blend) continue;
            // fading out: 70 % just past the border, none at blend_km
            double chance = 0.7 * (1 - d / r.Blend);
            if (Hash.F01(Hash.Mix(seed ^ 0x9e3779b9u)) < chance) return r.Preset;
        }
        return null;
    }

    /// <summary>The name of the region at (x, z), or null (for the HUD and tests).</summary>
    public static string? NameAt(double x, double z)
    {
        foreach (var r in All)
            if (x >= r.MinX && x <= r.MaxX && z >= r.MinZ && z <= r.MaxZ && Inside(r, x, z)) return r.Name;
        return null;
    }

    private static bool Inside(Region r, double x, double z)
    {
        bool inside = false;
        for (int i = 0, j = r.X.Length - 1; i < r.X.Length; j = i++)
            if ((r.Z[i] > z) != (r.Z[j] > z) && x < (r.X[j] - r.X[i]) * (z - r.Z[i]) / (r.Z[j] - r.Z[i]) + r.X[i])
                inside = !inside;
        return inside;
    }

    private static double Distance(Region r, double x, double z)
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
