using System;

namespace BalticDrive.Core;

/// <summary>
/// The world's map projection (D2): Lambert conformal conic on WGS84, standard parallels 53° and 67° N, centred on
/// 58° N 20° E — the same as <c>WORLD_PROJ</c> in tools/osm_import/common.py. World X = metres east of the centre,
/// world Z = metres SOUTH (Godot's -Z is north). Scale error stays within about 1 % from 49° to 70° N.
/// Formulas: Snyder, "Map Projections — A Working Manual" (1987), §15.
/// </summary>
public static class Projection
{
    public const string Proj4 = "+proj=lcc +lat_1=53 +lat_2=67 +lat_0=58 +lon_0=20 +x_0=0 +y_0=0 +ellps=WGS84 +units=m +no_defs";

    private const double A = 6378137.0, F = 1 / 298.257223563;
    private static readonly double E = Math.Sqrt(F * (2 - F));
    private const double Lat1 = 53 * Math.PI / 180, Lat2 = 67 * Math.PI / 180, Lat0 = 58 * Math.PI / 180, Lon0 = 20 * Math.PI / 180;
    private static readonly double N, Fc, Rho0;

    static Projection()
    {
        double m1 = M(Lat1), m2 = M(Lat2), t1 = T(Lat1), t2 = T(Lat2), t0 = T(Lat0);
        N = (Math.Log(m1) - Math.Log(m2)) / (Math.Log(t1) - Math.Log(t2));
        Fc = m1 / (N * Math.Pow(t1, N));
        Rho0 = A * Fc * Math.Pow(t0, N);
    }

    private static double M(double lat) => Math.Cos(lat) / Math.Sqrt(1 - E * E * Math.Sin(lat) * Math.Sin(lat));
    private static double T(double lat)
    {
        double s = E * Math.Sin(lat);
        return Math.Tan(Math.PI / 4 - lat / 2) / Math.Pow((1 - s) / (1 + s), E / 2);
    }

    /// <summary>Degrees → world X (east) and Z (south), metres.</summary>
    public static (double X, double Z) ToWorld(double latDeg, double lonDeg)
    {
        double lat = latDeg * Math.PI / 180, lon = lonDeg * Math.PI / 180;
        double rho = A * Fc * Math.Pow(T(lat), N);
        double theta = N * (lon - Lon0);
        double east = rho * Math.Sin(theta), north = Rho0 - rho * Math.Cos(theta);
        return (east, -north);
    }

    /// <summary>World X (east) and Z (south), metres → degrees.</summary>
    public static (double Lat, double Lon) ToGeo(double x, double z)
    {
        double east = x, north = -z;
        double dy = Rho0 - north;
        double rho = Math.Sign(N) * Math.Sqrt(east * east + dy * dy);
        double theta = Math.Atan2(Math.Sign(N) * east, Math.Sign(N) * dy);
        double t = Math.Pow(rho / (A * Fc), 1 / N);
        double lat = Math.PI / 2 - 2 * Math.Atan(t);
        for (int i = 0; i < 8; i++)
        {
            double s = E * Math.Sin(lat);
            lat = Math.PI / 2 - 2 * Math.Atan(t * Math.Pow((1 - s) / (1 + s), E / 2));
        }
        return (lat * 180 / Math.PI, (theta / N + Lon0) * 180 / Math.PI);
    }

    /// <summary>Angle (radians, clockwise seen from above) from grid north (-Z) to true north at a world point:
    /// the compass turns by this much (about 0.86° per degree of longitude away from 20° E).</summary>
    public static double Convergence(double x, double z)
    {
        var (_, lon) = ToGeo(x, z);
        return N * (lon * Math.PI / 180 - Lon0);
    }
}
