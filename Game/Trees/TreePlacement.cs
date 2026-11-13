using System;
using System.Collections.Generic;
using BalticDrive.Core;
using BalticDrive.Map;
using Godot;

namespace BalticDrive.Trees;

/// <summary>
/// Where the trees stand (D22): every tree OSM maps on its own (street and park trees), plus trees scattered over the
/// land cover — forests dense, parks, cemeteries and scrub sparse, wetlands a few willows and birches. The scatter is a
/// world-aligned jittered grid per land class, so a tree is in the same place, of the same species and size, whether a
/// near cell (3D) or a far cell (impostor) asks for it. Near cells also keep trees off roads, junctions and buildings
/// and stand them on the road-shaped ground; far cells use the plain elevation (a few metres off at 1 km is invisible).
/// </summary>
public static class TreePlacement
{
    public readonly record struct Tree(double X, float Y, double Z, int Species, float Scale, float Yaw);

    private readonly record struct Scatter(byte Land, float Spacing, float Keep, float MinScale, float MaxScale, int Salt);

    // Mazowsze's forests are mostly pine (Kampinos, the Mazowiecki park), with birch, oak and some spruce.
    private static readonly Scatter[] Scatters =
    {
        new(MapLand.Forest, 6.5f, 0.92f, 0.8f, 1.3f, 11),
        new(MapLand.Park, 15f, 0.5f, 0.85f, 1.25f, 12),
        new(MapLand.Cemetery, 11f, 0.55f, 0.8f, 1.2f, 13),
        new(MapLand.Scrub, 9f, 0.35f, 0.45f, 0.8f, 14),
        new(MapLand.Wetland, 13f, 0.3f, 0.6f, 1.0f, 15),
    };

    /// <summary>An independent random number in [0, 1) per (hash, channel): shifted bits of one hash are correlated.</summary>
    private static float R(uint h, int channel) => Hash.F01(Hash.Of((int)h, channel, 977));

    private static int Pick(byte land, uint h)
    {
        float r = R(h, 1);
        h = Hash.Of((int)h, 2, 977);
        switch (land)
        {
            case MapLand.Forest:
                return r < 0.50f ? TreeSpecies.PineA + (int)(h % 3) : r < 0.60f ? TreeSpecies.SpruceA + (int)(h % 2)
                     : r < 0.80f ? TreeSpecies.BirchA + (int)(h % 3) : r < 0.85f ? TreeSpecies.BirchAutumnA + (int)(h % 2)
                     : TreeSpecies.OakA + (int)(h % 3);
            case MapLand.Scrub:
                return r < 0.5f ? TreeSpecies.Sparse : r < 0.8f ? TreeSpecies.BirchA + (int)(h % 3) : TreeSpecies.PineC;
            case MapLand.Wetland:
                return r < 0.5f ? TreeSpecies.Willow : TreeSpecies.BirchA + (int)(h % 3);
            default:   // parks, cemeteries and street trees
                return r < 0.35f ? TreeSpecies.OakA + (int)(h % 3) : r < 0.60f ? TreeSpecies.BirchA + (int)(h % 3)
                     : r < 0.70f ? TreeSpecies.Poplar : r < 0.80f ? TreeSpecies.MapleAutumn : r < 0.88f ? TreeSpecies.BirchAutumnA + (int)(h % 2)
                     : r < 0.94f ? TreeSpecies.Willow : TreeSpecies.SpruceA + (int)(h % 2);
        }
    }

    /// <summary>The trees in the world square [x0, x0 + size) × [z0, z0 + size). <paramref name="ctx"/> (near cells only)
    /// has been reset over that square: roads, junctions and buildings keep the trees off.</summary>
    public static List<Tree> In(MapWorld world, double x0, double z0, double size, MapContext? ctx)
    {
        var trees = new List<Tree>();
        double x1 = x0 + size, z1 = z0 + size;
        // OSM's own trees
        int tx0 = (int)Math.Floor(x0 / MapTile.Size), tx1 = (int)Math.Floor((x1 - 1e-6) / MapTile.Size);
        int tz0 = (int)Math.Floor(z0 / MapTile.Size), tz1 = (int)Math.Floor((z1 - 1e-6) / MapTile.Size);
        for (int tz = tz0; tz <= tz1; tz++)
            for (int tx = tx0; tx <= tx1; tx++)
            {
                var tile = world.Data.Tile(tx, tz);
                if (tile.Empty) continue;
                for (int i = 0; i < tile.PointKind.Length; i++)
                {
                    if (tile.PointKind[i] != MapPointKind.Tree) continue;
                    double wx = tile.OriginX + tile.PointX[i], wz = tile.OriginZ + tile.PointZ[i];
                    if (wx < x0 || wx >= x1 || wz < z0 || wz >= z1) continue;
                    uint h = Hash.Of((int)Math.Floor(wx * 4), (int)Math.Floor(wz * 4), 21);
                    Add(trees, world, ctx, x0, z0, wx, wz, Pick(MapLand.Park, h), 0.75f + 0.45f * R(h, 3), h, strict: false);
                }
            }
        // scattered over the land cover
        foreach (var sc in Scatters)
        {
            long gx0 = (long)Math.Floor(x0 / sc.Spacing), gx1 = (long)Math.Floor((x1 - 1e-6) / sc.Spacing);
            long gz0 = (long)Math.Floor(z0 / sc.Spacing), gz1 = (long)Math.Floor((z1 - 1e-6) / sc.Spacing);
            for (long gz = gz0; gz <= gz1; gz++)
                for (long gx = gx0; gx <= gx1; gx++)
                {
                    uint h = Hash.Of((int)gx, (int)gz, sc.Salt);
                    if (R(h, 4) > sc.Keep) continue;
                    double wx = (gx + 0.1 + 0.8 * R(h, 5)) * sc.Spacing;
                    double wz = (gz + 0.1 + 0.8 * R(h, 6)) * sc.Spacing;
                    if (wx < x0 || wx >= x1 || wz < z0 || wz >= z1) continue;
                    var tile = world.Data.TileAt(wx, wz);
                    if (tile.Empty || tile.LandAt((float)(wx - tile.OriginX), (float)(wz - tile.OriginZ)) != sc.Land) continue;
                    float scale = sc.MinScale + (sc.MaxScale - sc.MinScale) * R(h, 7);
                    Add(trees, world, ctx, x0, z0, wx, wz, Pick(sc.Land, h), scale, h, strict: true);
                }
        }
        return trees;
    }

    private static void Add(List<Tree> trees, MapWorld world, MapContext? ctx, double x0, double z0, double wx, double wz,
        int species, float scale, uint h, bool strict)
    {
        var tile = world.Data.TileAt(wx, wz);
        float tlx = (float)(wx - tile.OriginX), tlz = (float)(wz - tile.OriginZ);
        if (!float.IsNaN(tile.WaterAt(tlx, tlz))) return;
        float y;
        if (ctx != null)
        {
            float lx = (float)(wx - ctx.Ox), lz = (float)(wz - ctx.Oz);
            // off the carriageway: mapped street trees may stand by the kerb, scattered ones keep a verge's distance
            float edge = ctx.NearestGroundRoad(lx, lz, out _, out var road);
            if (road != null && edge < (strict ? 2.5f : 0.6f)) return;
            if (ctx.Junctions.Count > 0 && ctx.NearestGroundJunction(lx, lz, out _) < (strict ? 2.5f : 0.6f)) return;
            if (ctx.InBuilding(lx, lz, strict ? 1.5f : 0.3f)) return;
            y = ctx.Height(lx, lz, 0f, out _);
        }
        else y = tile.GroundAt(tlx, tlz);
        trees.Add(new Tree(wx, y - 0.15f, wz, species, scale, R(h, 8) * Mathf.Tau));
    }
}
