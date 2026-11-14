using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BalticDrive.Map;
using BalticDrive.World;
using Godot;

namespace BalticDrive;

/// <summary>
/// F9 (Tom's idea, D28): a screenshot plus a text file with everything needed to find and reproduce what is on screen —
/// where (world, lat/lon, tile), the camera, the time of day, the car, the GPS, the build and map, frame timings, and
/// what the map holds around the focus: the roads (class, flags, lanes, width, height, lift, pavements), junctions,
/// barriers, buildings and points within 40 m. Written to %APPDATA%\BalticDrive\dumps\dump-YYYYMMDD-HHMMSS.png/.txt;
/// a note at the bottom of the screen says where.
/// </summary>
public static class DebugDump
{
    public static string Folder => Path.Combine(OS.GetUserDataDir(), "dumps");

    public static string Take(Main main)
    {
        Directory.CreateDirectory(Folder);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string png = Path.Combine(Folder, $"dump-{stamp}.png"), txt = Path.Combine(Folder, $"dump-{stamp}.txt");
        main.GetViewport().GetTexture().GetImage().SavePng(png);
        File.WriteAllText(txt, Describe(main), Encoding.UTF8);
        return txt;
    }

    private static string F(double v, int d = 1) => v.ToString("F" + d, CultureInfo.InvariantCulture);

    private static string Describe(Main main)
    {
        var sb = new StringBuilder();
        var (fx, fz) = main.Focus;
        var (lat, lon) = Core.Projection.ToGeo(fx, fz);
        var cam = main.GetViewport().GetCamera3D();
        sb.AppendLine($"Baltic Drive debug dump  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Build: {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version}  Godot {Engine.GetVersionInfo()["string"]}  map format {MapData.Format}");
        sb.AppendLine($"Focus: x {F(fx)} z {F(fz)}  ({F(lat, 6)} N, {F(lon, 6)} E)  tile {Math.Floor(fx / MapTile.Size)},{Math.Floor(fz / MapTile.Size)}");
        sb.AppendLine($"Google Maps: https://www.google.com/maps/@{F(lat, 6)},{F(lon, 6)},19z");
        sb.AppendLine($"Repeat this view: --start={F(fx, 0)},{F(fz, 0)} or --views=\"{F(fx, 0)}:{F(fz, 0)}:<above>:<yaw>:<pitch>\"");
        sb.AppendLine($"Origin: {F(WorldOrigin.X, 0)}, {F(WorldOrigin.Z, 0)}");
        if (cam != null)
        {
            var (cx, cy, cz) = WorldOrigin.ToWorld(cam.GlobalPosition);
            var r = cam.GlobalRotationDegrees;
            sb.AppendLine($"Camera: {cam.Name} at x {F(cx)} y {F(cy)} z {F(cz)}  yaw {F(r.Y)}° pitch {F(r.X)}°  fov {F(cam.Fov)}");
        }
        var atm = main.Atmosphere;
        sb.AppendLine($"Time: {(int)atm.Hour:00}:{(int)(atm.Hour % 1 * 60):00} (hour {F(atm.Hour, 3)}) day of year {F(atm.DayOfYear, 0)}  clouds {F(atm.Coverage, 2)}  sun energy {F(atm.Sun.LightEnergy, 2)}");
        sb.AppendLine($"Frame: {Engine.GetFramesPerSecond()} fps  {F(Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000, 2)} ms process  " +
                      $"{Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame)} draw calls  {F(Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed) / 1048576, 0)} MB video");
        sb.AppendLine($"Driving: {(main.Flying ? "no (free camera)" : "yes")}");
        if (main.Car is { } car)
        {
            var (x, y, z) = WorldOrigin.ToWorld(car.GlobalPosition);
            var up = car.GlobalTransform.Basis.Y;
            sb.AppendLine($"Car: x {F(x)} y {F(y)} z {F(z)}  speed {F(car.Speed * 3.6f)} km/h  on road {car.OnRoad}  steering {F(car.Steering, 2)}  upright {F(up.Y, 2)}  heading {F(-car.GlobalRotationDegrees.Y)}°");
        }
        if (main.Nav is { } nav)
            sb.AppendLine($"GPS: destination {(nav.Destination is { } d ? $"{F(d.X, 0)},{F(d.Y, 0)}" : "none")}  {(nav.Route != null ? $"{F(nav.Remaining / 1000, 2)} km left" : "no route")}");

        // the map around the focus
        if (main.World is { } world)
        {
            var ctx = new MapContext(world);
            const float R = 40f;
            ctx.Reset(fx, fz, -R, -R, R, R);
            sb.AppendLine();
            float g = ctx.Ground(0, 0), h = ctx.Height(0, 0, 0f, out _);
            sb.AppendLine($"Ground here: natural {F(g, 2)} m, shaped {F(h, 2)} m, water {(float.IsNaN(ctx.Water(0, 0)) ? "none" : F(ctx.Water(0, 0), 2))}");
            var tile = world.Data.TileAt(fx, fz);
            if (!tile.Empty) sb.AppendLine($"Land cover here: {tile.LandAt((float)(fx - tile.OriginX), (float)(fz - tile.OriginZ))}");
            sb.AppendLine($"Roads within {R} m:");
            foreach (var grp in ctx.Segs.GroupBy(s => s.Road))
            {
                var r = grp.Key;
                float best = grp.Min(s => s.Distance(0, 0, out _));
                if (best > R) continue;
                var near = grp.OrderBy(s => s.Distance(0, 0, out _)).First();
                string flags = string.Concat(new[] { r.IsBridge ? "bridge " : "", (r.Flags & RoadFlag.Tunnel) != 0 ? "tunnel " : "", r.Oneway ? "oneway " : "",
                    r.Unpaved ? "unpaved " : "", r.IsRail ? "rail " : "", r.OnPaved ? "on-paved " : "", r.Hw != null ? "taper " : "",
                    r.StartsAtJunction ? "j-start " : "", r.EndsAtJunction ? "j-end " : "" });
                sb.AppendLine($"  {ClassName(r.Cls),-12} {flags}lanes {r.Lanes}{(r.LanesForward > 0 ? $" ({r.LanesForward} fwd)" : "")} width {F(r.HalfWidth * 2)} layer {r.Layer} " +
                              $"walk {(r.WalkLeft ? "L" : "")}{(r.WalkRight ? "R" : "")}{(r.Walk == 0 ? "-" : "")}  y {F(r.Y.Min(), 2)}..{F(r.Y.Max(), 2)} lift {F(r.Lift.Max(), 1)}  " +
                              $"nearest {F(best)} m (y there {F(near.Ya, 2)}), {r.X.Length} points");
            }
            int jn = 0;
            foreach (var jr in ctx.Junctions)
            {
                var j = jr.J;
                float dx = jr.OffX + j.Cx, dz = jr.OffZ + j.Cz;
                if (MathF.Sqrt(dx * dx + dz * dz) > R) continue;
                if (jn++ == 0) sb.AppendLine("Junctions and paved areas:");
                sb.AppendLine($"  {(j.Paved ? (j.Pedestrian ? "square" : "paved area") : "junction")}{(j.Elevated ? " elevated" : "")}{(j.Ghost ? " ghost" : "")} centre {F(dx)},{F(dz)} from here, " +
                              $"y {F(j.Y.Min(), 2)}..{F(j.Y.Max(), 2)}, {j.X.Length} points");
            }
            int bn = 0;
            foreach (var (b, ox, oz) in ctx.Barriers)
            {
                float best = float.MaxValue;
                for (int k = 0; k < b.X.Length; k++) best = MathF.Min(best, MathF.Sqrt((ox + b.X[k]) * (ox + b.X[k]) + (oz + b.Z[k]) * (oz + b.Z[k])));
                if (best > R) continue;
                if (bn++ == 0) sb.AppendLine("Barriers:");
                sb.AppendLine($"  kind {b.Kind}{(b.Raised ? " raised" : "")} height {F(b.Height)}  nearest {F(best)} m, {b.X.Length} points, gates {b.Gap.Count(gp => gp > 0)}");
            }
            int bu = ctx.Buildings.Count(br => MathF.Abs(br.OffX + br.B.Cx) < R && MathF.Abs(br.OffZ + br.B.Cz) < R);
            int pts = ctx.Points.Count(p => MathF.Abs(p.X) < R && MathF.Abs(p.Z) < R);
            sb.AppendLine($"Buildings within {R} m: {bu}  points (trees, lamps, poles, bollards): {pts}");
        }
        sb.AppendLine();
        sb.AppendLine("Notes (write what is wrong here):");
        return sb.ToString();
    }

    private static string ClassName(byte c) => c switch
    {
        RoadClass.Motorway => "motorway", RoadClass.Trunk => "trunk", RoadClass.Primary => "primary", RoadClass.Secondary => "secondary",
        RoadClass.Tertiary => "tertiary", RoadClass.Unclassified => "unclassified", RoadClass.Residential => "residential",
        RoadClass.Living => "living", RoadClass.Service => "service", RoadClass.Track => "track", RoadClass.Link => "link",
        RoadClass.Rail => "rail", RoadClass.Tram => "tram", _ => c.ToString(),
    };
}
