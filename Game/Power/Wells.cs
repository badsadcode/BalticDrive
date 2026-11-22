using System;
using BalticDrive.Map;

namespace BalticDrive.Power;

/// <summary>
/// Clean water with the grid dead (D121): the wells, hand pumps and springs OSM maps (man_made=water_well,
/// natural=spring), drawn by the map mesher. Standing at one with an empty bottle, F fills it with clean water: no
/// boiling, unlike a river (D111).
/// </summary>
public static class Wells
{
    public const float Reach = 1.6f;
    [ThreadStatic] private static MapContext? _ctx;

    /// <summary>The nearest well, pump or spring within reach of a world spot: its kind, or null.</summary>
    public static byte? Nearest(MapWorld world, double x, double z, float reach = Reach)
    {
        var ctx = _ctx;
        if (ctx == null || ctx.World != world) _ctx = ctx = new MapContext(world);
        ctx.Reset(x - 4, z - 4, 0, 0, 8, 8, withPoints: true);
        byte? best = null; double bd = reach + 0.5;   // + the well's own size
        foreach (var p in ctx.Points)
        {
            if (p.Kind is not (MapPointKind.Well or MapPointKind.Pump or MapPointKind.Spring)) continue;
            double dx = ctx.Ox + p.X - x, dz = ctx.Oz + p.Z - z, d = Math.Sqrt(dx * dx + dz * dz);
            if (d < bd) { bd = d; best = p.Kind; }
        }
        return best;
    }

    /// <summary>"well", "hand pump", "spring" in the current language.</summary>
    public static string Name(byte kind) => Core.Lang.T(kind switch { MapPointKind.Pump => "water.pump", MapPointKind.Spring => "water.spring", _ => "water.well" });
}
