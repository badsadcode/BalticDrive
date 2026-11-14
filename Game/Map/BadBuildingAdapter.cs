using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;
using BB = BadBuilding;
using NVec2 = System.Numerics.Vector2;

namespace BalticDrive.Map;

/// <summary>
/// Buildings from the BadBuilding library (github.com/badsadcode/BadBuilding, D29) instead of the plain extrusions:
/// each footprint goes in with its context (neighbouring footprints, nearby streets, the ground under every corner),
/// a whole building comes out (real window openings, balconies, shop fronts, gate passages into courtyards), and it is
/// baked into the chunk's building mesh. Runs on the streaming worker threads, like the rest of the map meshing.
/// <para>
/// Behind a switch (<c>--buildings=bb</c>, F7 in game) until it has been measured against the old meshes. Only for
/// what the library already does well: flat-roofed town buildings. Houses with pitched roofs, churches, towers,
/// canopies and parts of multi-part buildings keep the old meshes, and so does far detail (LOD 2).
/// </para>
/// </summary>
public static class BadBuildingAdapter
{
    /// <summary>On or off for the whole map (F7). The streamer rebuilds its cells when it changes.</summary>
    public static volatile bool Enabled;

    // vertex colour alpha selects the surface in Shaders/building.gdshader
    private const float KindPlain = 0.66f, KindGlass = 0.5f, KindFlatRoof = 0f;
    private const float Context = 40f;

    private static long _count, _ticks, _tris, _failed;

    /// <summary>"n buildings, x ms each, y triangles each": for the HUD, F9 dumps and the self-test log.</summary>
    public static string Stats()
    {
        long n = Interlocked.Read(ref _count);
        if (n == 0) return "BadBuilding: no buildings yet";
        double ms = Interlocked.Read(ref _ticks) * 1000.0 / Stopwatch.Frequency / n;
        return $"BadBuilding: {n} buildings, {ms:0.0} ms and {Interlocked.Read(ref _tris) / n} triangles each, {Interlocked.Read(ref _failed)} fell back";
    }

    public static void ResetStats() { _count = 0; _ticks = 0; _tris = 0; _failed = 0; }

    public static bool Eligible(MapBuilding b) =>
        !b.Ghost && !b.IsPart && !b.IsCanopy && b.Height >= 3.4f
        && b.Type is BuildingKind.Generic or BuildingKind.Apartments or BuildingKind.Commercial or BuildingKind.Civic
        && !(b.Roof >= RoofKind.Gabled && b.RoofHeight > 0.2f);

    /// <summary>Generates one building into the chunk mesh. False when the library could not build it (the caller then
    /// uses the old mesh).</summary>
    public static bool Add(MapContext ctx, in MapBuildingRef r, int lod, MapChunkGeometry geo, float gMin, uint seed)
    {
        var b = r.B;
        long t0 = Stopwatch.GetTimestamp();
        float ox = r.OffX + b.Cx, oz = r.OffZ + b.Cz;   // the building's own origin, in chunk-local metres

        // the footprint, around its centre
        var outer = Ring(b.RX[0], b.RZ[0], r.OffX - ox, r.OffZ - oz);
        var holes = new List<BB.Model.Ring>();
        for (int q = 1; q < b.RX.Length; q++) holes.Add(Ring(b.RX[q], b.RZ[q], r.OffX - ox, r.OffZ - oz));
        // the ground under every corner, absolute (the library sets its floor on the highest and its plinth to the lowest)
        var ground = new List<float>(b.RX[0].Length);
        for (int k = 0; k < b.RX[0].Length; k++) ground.Add(ctx.Height(r.OffX + b.RX[0][k], r.OffZ + b.RZ[0][k], 0f, out _));

        // context: footprints and streets within reach
        var neighbours = new List<BB.Model.Ring>();
        foreach (var o in ctx.Buildings)
        {
            if (ReferenceEquals(o.B, b) || o.B.Ghost) continue;
            if (o.OffX + o.B.MaxX < ox - Context + (b.MinX - b.Cx) || o.OffX + o.B.MinX > ox + Context + (b.MaxX - b.Cx)) continue;
            if (o.OffZ + o.B.MaxZ < oz - Context + (b.MinZ - b.Cz) || o.OffZ + o.B.MinZ > oz + Context + (b.MaxZ - b.Cz)) continue;
            neighbours.Add(Ring(o.B.RX[0], o.B.RZ[0], o.OffX - ox, o.OffZ - oz));
        }
        var streets = new List<BB.Model.StreetLine>();
        float reach = Context + MathF.Max(b.MaxX - b.MinX, b.MaxZ - b.MinZ) * 0.5f;
        foreach (var s in ctx.Segs)
        {
            if (s.Road.IsRail || s.Road.IsTram || s.Road.Cls > RoadClass.Service) continue;
            if (s.Distance(ox, oz, out _) > reach) continue;
            streets.Add(new BB.Model.StreetLine([new NVec2(s.Ax - ox, s.Az - oz), new NVec2(s.Bx - ox, s.Bz - oz)], s.Hw));
        }

        var req = new BB.Model.BuildingRequest
        {
            Id = $"bd:{seed:x8}",
            Seed = seed,
            Outer = outer,
            Holes = holes,
            Type = b.Type switch
            {
                BuildingKind.Apartments => "apartments",
                BuildingKind.Commercial => "commercial",
                BuildingKind.Civic => "civic",
                _ => "yes",
            },
            Height = b.Height,
            Neighbours = neighbours,
            Streets = streets,
            Ground = ground,
        };

        BB.Model.Building built;
        try { built = BB.Generation.BuildingGenerator.Generate(req); }
        catch (Exception) { Interlocked.Increment(ref _failed); return false; }
        if (built.Modules.Count == 0) { Interlocked.Increment(ref _failed); return false; }

        // bake: every module drawn at this level of detail, coloured by its material role
        var mesh = geo.Buildings;
        int lodBit = lod == 0 ? 1 : 2;
        float floor = built.Plan?.Datum ?? gMin, top = built.Bounds().Max.Y - floor;
        int tris = 0;
        foreach (var m in built.Modules)
        {
            if ((m.Lods & lodBit) == 0) continue;
            var src = m.Mesh;
            foreach (var (role, idx) in src.Groups)
            {
                var col = Colour(built, role);
                for (int t = 0; t + 2 < idx.Count; t += 3)
                {
                    int v0 = mesh.V.Count;
                    for (int c = 0; c < 3; c++)
                    {
                        var p = src.Positions[idx[t + c]];
                        var n = src.Normals[idx[t + c]];
                        mesh.Add(new Vector3(ox + p.X, p.Y, oz + p.Z), new Vector3(n.X, n.Y, n.Z),
                            new Vector2(p.X + p.Z, p.Y - floor), new Vector2(0f, top), col);
                    }
                    var nf = src.Normals[idx[t]];
                    mesh.Tri(v0, v0 + 1, v0 + 2, new Vector3(nf.X, nf.Y, nf.Z));
                    tris++;
                }
            }
        }
        // a skirt under the plinth: coarse far terrain can sit a metre or more off the sampled ground (MapMesher.WallSink)
        if (lod > 0) Skirt(mesh, b, r.OffX, r.OffZ, gMin - MapMesher.WallSink, gMin - 0.4f);

        // collision: the bare mass, with gate passages cut through it
        if (geo.BuildingFaces is { } faces)
            foreach (var m in built.Modules)
            {
                if (m.Type != "mass") continue;
                var src = m.Mesh;
                foreach (var (_, idx) in src.Groups)
                    for (int t = 0; t + 2 < idx.Count; t += 3)
                        for (int c = 0; c < 3; c++)
                        {
                            var p = src.Positions[idx[t + c]];
                            faces.Add(new Vector3(ox + p.X, p.Y, oz + p.Z));
                        }
            }

        Interlocked.Increment(ref _count);
        Interlocked.Add(ref _ticks, Stopwatch.GetTimestamp() - t0);
        Interlocked.Add(ref _tris, tris);
        return true;
    }

    private static BB.Model.Ring Ring(float[] rx, float[] rz, float dx, float dz)
    {
        var pts = new List<NVec2>(rx.Length);
        for (int i = 0; i < rx.Length; i++) pts.Add(new NVec2(rx[i] + dx, rz[i] + dz));
        return new BB.Model.Ring(pts);
    }

    /// <summary>The library's material colour (linear) as a vertex colour, with the surface kind for the shader.</summary>
    private static Color Colour(BB.Model.Building b, string role)
    {
        var c = b.Materials.TryGetValue(role, out var m) ? m.Color : new System.Numerics.Vector3(0.4f);
        float kind = role switch
        {
            "glass" or "glass_block" => KindGlass,
            "roof" => KindFlatRoof,
            _ => KindPlain,
        };
        // linear albedo, slightly lifted: the building shader darkens walls by its grime and noise terms
        const float k = 1.15f;
        return new Color(MathF.Min(1f, c.X * k), MathF.Min(1f, c.Y * k), MathF.Min(1f, c.Z * k), kind);
    }

    private static void Skirt(MeshData mesh, MapBuilding b, float offX, float offZ, float y0, float y1)
    {
        var rx = b.RX[0]; var rz = b.RZ[0];
        var col = new Color(0.22f, 0.21f, 0.2f, KindPlain);
        for (int i = 0; i < rx.Length; i++)
        {
            int j = (i + 1) % rx.Length;
            float ax = offX + rx[i], az = offZ + rz[i], bx = offX + rx[j], bz = offZ + rz[j];
            float dx = bx - ax, dz = bz - az, len = MathF.Sqrt(dx * dx + dz * dz);
            if (len < 0.02f) continue;
            var n = new Vector3(dz / len, 0, -dx / len);
            int v = mesh.V.Count;
            mesh.Add(new Vector3(ax, y0, az), n, new Vector2(0, -1), Vector2.Zero, col);
            mesh.Add(new Vector3(bx, y0, bz), n, new Vector2(len, -1), Vector2.Zero, col);
            mesh.Add(new Vector3(bx, y1, bz), n, new Vector2(len, 0), Vector2.Zero, col);
            mesh.Add(new Vector3(ax, y1, az), n, new Vector2(0, 0), Vector2.Zero, col);
            mesh.Quad(v, v + 1, v + 2, v + 3, n);
        }
    }
}
