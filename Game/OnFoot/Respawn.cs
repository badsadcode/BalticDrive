using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BalticDrive.Core;
using BalticDrive.Map;
using Godot;

namespace BalticDrive.OnFoot;

/// <summary>A place outside a building: where you stand (world metres), which way you face, and its address.</summary>
public readonly record struct Doorstep(double X, double Z, float Yaw, long OsmId, string Address);

/// <summary>
/// Where you wake up (D55, Tom, after Project Zomboid). Without a safehouse: outside a real home (a house or a block of
/// flats) picked at random 1.5–3 km from where you fell, so getting back to your things is a trip but not a chore.
/// Also finds the home you're standing at, for claiming it as a safehouse.
/// </summary>
public static class Homes
{
    public static bool IsHome(MapBuilding b) => !b.Ghost && b.Type is BuildingKind.House or BuildingKind.Apartments;

    /// <summary>A doorstep 1.5–3 km from (x, z): up to 12 random directions, the tile there, a home in range.</summary>
    public static Doorstep? Pick(MapWorld world, double x, double z, Random rng, double min = 1500, double max = 3000)
    {
        for (int tries = 0; tries < 12; tries++)
        {
            double a = rng.NextDouble() * Math.Tau, d = min + rng.NextDouble() * (max - min);
            double px = x + Math.Cos(a) * d, pz = z + Math.Sin(a) * d;
            int tx = (int)Math.Floor(px / MapTile.Size), tz = (int)Math.Floor(pz / MapTile.Size);
            if (!world.Data.HasTile(tx, tz)) continue;
            var tile = world.Data.Tile(tx, tz);
            var homes = tile.Buildings.Where(IsHome).Where(b =>
            {
                double bx = tile.OriginX + b.Cx - x, bz = tile.OriginZ + b.Cz - z, r = Math.Sqrt(bx * bx + bz * bz);
                return r >= min && r <= max;
            }).OrderBy(_ => rng.Next()).Take(8);
            foreach (var b in homes)
                if (Step(world, tile, b, rng) is { } s) return s;
        }
        return null;
    }

    /// <summary>A spot 1.4 m out from the middle of one of the outline's walls, on open ground (not in another building,
    /// not on a carriageway), facing away from the wall.</summary>
    public static Doorstep? Step(MapWorld world, MapTile tile, MapBuilding b, Random rng)
    {
        var rx = b.RX[0]; var rz = b.RZ[0];
        int n = rx.Length, start = rng.Next(n);
        for (int k = 0; k < n; k++)
        {
            int i = (start + k) % n, j = (i + 1) % n;
            double ex = rx[j] - rx[i], ez = rz[j] - rz[i], len = Math.Sqrt(ex * ex + ez * ez);
            if (len < 3) continue;   // a corner, not a wall
            double mx = tile.OriginX + (rx[i] + rx[j]) * 0.5, mz = tile.OriginZ + (rz[i] + rz[j]) * 0.5;
            foreach (int side in new[] { 1, -1 })
            {
                double nx = -ez / len * side, nz = ex / len * side;
                double px = mx + nx * 1.4, pz = mz + nz * 1.4;
                if (world.InBuilding(px, pz, 0.4f) || world.RoadEdgeDistance(px, pz, 6f) < 0.5f) continue;
                if (!world.InBuilding(mx - nx * 1.0, mz - nz * 1.0, 0f)) continue;   // the other side must be the building
                float yaw = MathF.Atan2(-(float)nx, -(float)nz);   // forward is (-sin, -cos): facing out, back to the wall
                return new Doorstep(px, pz, yaw, b.OsmId, Address(b));
            }
        }
        return null;
    }

    public static string Address(MapBuilding b) => b.Street.Length == 0 ? "" : b.Number.Length == 0 ? b.Street : $"{b.Street} {b.Number}";

    /// <summary>The home whose wall you're standing at (within 2.5 m), and your doorstep there.</summary>
    public static (MapBuilding Home, Doorstep Step)? At(MapWorld world, double x, double z, float yaw)
    {
        var tile = world.Data.TileAt(x, z);
        double lx = x - tile.OriginX, lz = z - tile.OriginZ;
        MapBuilding? best = null; double bd = 2.5;
        foreach (var b in tile.Buildings)
        {
            if (!IsHome(b) || lx < b.MinX - 3 || lx > b.MaxX + 3 || lz < b.MinZ - 3 || lz > b.MaxZ + 3) continue;
            var rx = b.RX[0]; var rz = b.RZ[0];
            for (int i = 0, j = rx.Length - 1; i < rx.Length; j = i++)
            {
                double d = SegDist(lx, lz, rx[j], rz[j], rx[i], rz[i]);
                if (d < bd) { bd = d; best = b; }
            }
        }
        if (best == null || world.InBuilding(x, z, 0f)) return null;
        return (best, new Doorstep(x, z, yaw, best.OsmId, Address(best)));
    }

    private static double SegDist(double px, double pz, double ax, double az, double bx, double bz)
    {
        double ex = bx - ax, ez = bz - az, l2 = ex * ex + ez * ez;
        double t = l2 < 1e-9 ? 0 : Math.Clamp(((px - ax) * ex + (pz - az) * ez) / l2, 0, 1);
        double dx = ax + ex * t - px, dz = az + ez * t - pz;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}

/// <summary>
/// The safehouse (D55): one home you've claimed, where you wake up after dying. Kept as data, not code, in
/// <c>user://safehouses.json</c> ({"safehouses":[{"x":…,"z":…,"yaw":…,"osmId":…,"address":"…","map":"mazowsze"}]}),
/// so a world editor can later place safehouse areas in the same shape (Tom: "it would be nice to have this game
/// data-driven"). One per map for now; claiming another moves it.
/// </summary>
public static class Safehouse
{
    private const string FilePath = "user://safehouses.json";
    private sealed record Entry(double X, double Z, float Yaw, long OsmId, string Address, string Map);
    private sealed record FileShape(List<Entry> Safehouses);
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static Doorstep? Get(string map)
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            if (!File.Exists(path)) return null;
            var f = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Json);
            var e = f?.Safehouses?.LastOrDefault(s => s.Map == map);
            return e == null ? null : new Doorstep(e.X, e.Z, e.Yaw, e.OsmId, e.Address);
        }
        catch (Exception ex) { Log.Error("Safehouse", ex.Message); return null; }
    }

    public static void Set(string map, Doorstep d)
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            var list = File.Exists(path) ? JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Json)?.Safehouses ?? new() : new();
            list.RemoveAll(s => s.Map == map);
            list.Add(new Entry(d.X, d.Z, d.Yaw, d.OsmId, d.Address, map));
            File.WriteAllText(path, JsonSerializer.Serialize(new FileShape(list), Json));
        }
        catch (Exception ex) { Log.Error("Safehouse", ex.Message); }
    }

    /// <summary>Self-tests: forget the safehouse so a test starts clean.</summary>
    public static void Clear(string map)
    {
        try
        {
            string path = ProjectSettings.GlobalizePath(FilePath);
            if (!File.Exists(path)) return;
            var list = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Json)?.Safehouses ?? new();
            list.RemoveAll(s => s.Map == map);
            File.WriteAllText(path, JsonSerializer.Serialize(new FileShape(list), Json));
        }
        catch (Exception ex) { Log.Error("Safehouse", ex.Message); }
    }
}
