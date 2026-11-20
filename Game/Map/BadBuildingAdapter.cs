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
    private const float KindPlain = 0.66f, KindGlass = 0.5f, KindTiles = 0.33f, KindFlatRoof = 0f;
    /// <summary>Surface patterns the building shader draws (D63, BadBuilding's industrial style): roller doors,
    /// ribbed metal cladding, brick.</summary>
    private const float KindRoller = 0.6f, KindRibbed = 0.72f, KindBrick = 0.78f;
    /// <summary>A glass curtain wall (D70, BadBuilding's office style): panels, mullions and floor bands in the shader.</summary>
    private const float KindCurtain = 0.635f;
    /// <summary>Weathered wood (D73, BadBuilding's cottages): horizontal planks on walls, vertical boards on gables.</summary>
    private const float KindTimber = 0.82f, KindBoards = 0.69f;
    /// <summary>Board-marked concrete (D79, BadBuilding's brutalism).</summary>
    private const float KindBoardConcrete = 0.75f;
    /// <summary>A door: the shader hides it while the building's interior is loaded (D81).</summary>
    private const float KindDoor = 0.84f;
    /// <summary>A balcony door's glass: still glass to the shader, hidden within reach of the player inside (D83).</summary>
    private const float KindBalconyDoor = 0.52f;
    /// <summary>A preset for BadBuilding's style choice (D79): null, or "brutalist" (Shift+F7, --preset=brutalist).</summary>
    public static string? Preset;
    private const float Context = 40f;

    private static long _count, _ticks, _tris, _failed;
    [ThreadStatic] private static Dictionary<int, int>? _vertexMap;

    /// <summary>"n buildings, x ms each, y triangles each": for the HUD, F9 dumps and the self-test log.</summary>
    public static string Stats()
    {
        long n = Interlocked.Read(ref _count);
        if (n == 0) return "BadBuilding: no buildings yet";
        double ms = Interlocked.Read(ref _ticks) * 1000.0 / Stopwatch.Frequency / n;
        return $"BadBuilding: {n} buildings, {ms:0.0} ms and {Interlocked.Read(ref _tris) / n} triangles each, {Interlocked.Read(ref _failed)} fell back";
    }

    public static void ResetStats() { _count = 0; _ticks = 0; _tris = 0; _failed = 0; }

    /// <summary>What the library builds (D29; houses, barns and pitched roofs since its roofs, D37): not churches,
    /// towers, canopies, parts of multi-part buildings or domes, which keep the old meshes.</summary>
    public static bool Eligible(MapBuilding b) =>
        // an Orthodox church or a mosque, whole (not mapped in parts), on the map's word (D92/D93, BadBuilding D41/D42)
        PlaceOfWorship(b) ||
        !b.Ghost && !b.IsCanopy && b.Height >= 2.4f && b.Roof != RoofKind.Dome
        // parts too (D70: skyscrapers are mapped as parts), but not the slivers that make up spires and antenna rings
        && (!b.IsPart || (b.MaxX - b.MinX) * (b.MaxZ - b.MinZ) > 60f && b.Height - b.MinHeight > 2.4f)
        && b.Type is BuildingKind.Generic or BuildingKind.Apartments or BuildingKind.Commercial or BuildingKind.Civic
            or BuildingKind.House or BuildingKind.Farm or BuildingKind.Shed or BuildingKind.Industrial;

    /// <summary>A place of worship BadBuilding has a style for, mapped as one outline: a church the map calls orthodox
    /// (denomination=…orthodox, Christian: an orthodox synagogue is religion=jewish), a mosque (religion=muslim), or a
    /// Catholic or other Western church or chapel (D95: Christian or no religion said). Synagogues and the rest keep the
    /// classic meshes.</summary>
    public static bool PlaceOfWorship(MapBuilding b)
    {
        if (b.Type != BuildingKind.Church || b.Ghost || b.IsPart || b.Height < 2.4f) return false;
        if ((b.MaxX - b.MinX) * (b.MaxZ - b.MinZ) < 14f) return false;   // a wayside shrine, not a chapel
        var u = Use.Parse(b.Use);
        bool orthodox = b.Use.Contains("orthodox", StringComparison.OrdinalIgnoreCase);
        if (u.Religion == "muslim" || u.Type == "mosque" && u.Religion is null) return true;
        if (u.Religion is not (null or "christian")) return false;
        if (orthodox) return true;
        return u.Type is "church" or "chapel" or "cathedral" or "monastery" or "convent" or "basilica"
            || u.Religion == "christian" && u.Type is "religious" or "yes";
    }

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
        for (int k = 0; k < b.RX[0].Length; k++)
        {
            float px = r.OffX + b.RX[0][k], pz = r.OffZ + b.RZ[0][k];
            float g = ctx.Height(px, pz, 0f, out _), w = ctx.Water(px, pz);
            ground.Add(float.IsNaN(w) || w < g ? g : w);   // on the water surface, not the carved bed (barges)
        }

        // context: footprints and streets within reach
        var neighbours = new List<BB.Model.Ring>();
        foreach (var o in ctx.Buildings)
        {
            if (ReferenceEquals(o.B, b) || o.B.Ghost) continue;
            if (o.OffX + o.B.MaxX < ox - Context + (b.MinX - b.Cx) || o.OffX + o.B.MinX > ox + Context + (b.MaxX - b.Cx)) continue;
            if (o.OffZ + o.B.MaxZ < oz - Context + (b.MinZ - b.Cz) || o.OffZ + o.B.MinZ > oz + Context + (b.MaxZ - b.Cz)) continue;
            var ring = Ring(o.B.RX[0], o.B.RZ[0], o.OffX - ox, o.OffZ - oz);
            // another part of the same building: say how high it stands, so a low podium doesn't blind a tower (D70)
            if (b.Group >= 0 && o.B.Group == b.Group) ring = ring with { PartMin = o.B.MinHeight, PartTop = o.B.Height };
            else ring = ring with { Top = o.B.Height };   // a separate neighbour's height (BadBuilding D31)
            neighbours.Add(ring);
        }
        var streets = new List<BB.Model.StreetLine>();
        float reach = Context + MathF.Max(b.MaxX - b.MinX, b.MaxZ - b.MinZ) * 0.5f;
        foreach (var s in ctx.Segs)
        {
            if (s.Road.IsRail || s.Road.IsTram || s.Road.Cls > RoadClass.Service) continue;
            if (s.Distance(ox, oz, out _) > reach) continue;
            streets.Add(new BB.Model.StreetLine([new NVec2(s.Ax - ox, s.Az - oz), new NVec2(s.Bx - ox, s.Bz - oz)], s.Hw));
        }

        var use = Use.Parse(b.Use);
        var req = new BB.Model.BuildingRequest
        {
            Id = $"bd:{seed:x8}",
            Seed = seed,
            Outer = outer,
            Holes = holes,
            // the real OSM type and facts when the map has them (format 9, D72), else the game's kind
            Type = use.Type ?? b.Type switch
            {
                BuildingKind.Apartments => "apartments",
                BuildingKind.Commercial => "commercial",
                BuildingKind.Civic => "civic",
                BuildingKind.House => "house",
                BuildingKind.Farm => "barn",
                BuildingKind.Shed => "shed",
                BuildingKind.Industrial => "industrial",
                _ => "yes",
            },
            StartYear = use.Year,
            Heritage = use.Heritage,
            Architecture = use.Architecture,
            Material = use.Material is "sandstone" or "stone" ? use.Material : GroupStone(ctx, b) ?? use.Material,
            // the Shift+F7 preset wins; else the region's (D91: Podlasie's wooden houses)
            Preset = Preset ?? World.Regions.PresetAt(ctx.Ox + ox, ctx.Oz + oz, seed),
            Religion = use.Religion,
            Denomination = use.Denomination,
            Levels = use.Levels,
            // a church without a mapped height gets the importer's 16 m guess, and a mapped one often counts its tower:
            // let the style decide its walls (D92, D95)
            Height = b.Type == BuildingKind.Church && (MathF.Abs(b.Height - 16f) < 0.05f || b.Height > 20f) ? null : b.Height,
            // the roof as the map (or the importer's guess for houses) has it
            Roof = b.Roof switch { RoofKind.Flat => "flat", RoofKind.Gabled => "gabled", RoofKind.Hipped => "hipped", RoofKind.Pyramidal => "pyramidal", _ => null },
            RoofHeight = b.RoofHeight > 0.2f ? b.RoofHeight : null,
            MinHeight = b.IsPart && b.MinHeight > 0.05f ? b.MinHeight : null,   // a part in the air (D70)
            // the buildings of one school or kindergarten site share a look (D75: each wing is its own OSM building and
            // drew its own accent colour, purple next to blue): the look stage seeded by a 120 m grid cell
            StageSeeds = StageSeedsFor(use, ctx.Ox + ox, ctx.Oz + oz),
            Neighbours = neighbours,
            Streets = streets,
            Ground = ground,
        };

        BB.Model.Building built;
        try { built = BB.Generation.BuildingGenerator.Generate(req); }
        catch (Exception) { Interlocked.Increment(ref _failed); return false; }
        if (built.Modules.Count == 0) { Interlocked.Increment(ref _failed); return false; }

        // blocks of flats you can walk into (D81): the interior is planned later, when someone comes to the door
        if (lod == 0 && BB.Interior.Shell.Of(built) is { } shell) World.Interiors.Register(shell, ctx.Ox + ox, ctx.Oz + oz);

        // bake: every module drawn at this level of detail, coloured by its material role
        var mesh = geo.Buildings;
        int lodBit = lod == 0 ? 1 : 2;
        float floor = built.Plan?.Datum ?? gMin, top = built.Bounds().Max.Y - floor;
        int tris = 0;
        foreach (var m in built.Modules)
        {
            if ((m.Lods & lodBit) == 0) continue;
            // the small details (window frames and sills, trims, balconies, porches) only in the near ring: from the far
            // ring they're a few pixels, and they doubled what the houses and new blocks cost to upload (D76)
            if (lod > 0 && m.Tier == BB.Model.ModuleTier.Secondary) continue;
            var src = m.Mesh;
            foreach (var (role, idx) in src.Groups)
            {
                var col = Colour(built, role);
                if (role == "roof" && built.Plan?.RoofShape is BB.Generation.RoofShape.Gabled or BB.Generation.RoofShape.Hipped or BB.Generation.RoofShape.Pyramidal)
                    col.A = KindTiles;
                // one chunk vertex per library vertex and role, shared by every triangle that uses it
                // (was three per triangle: a third of the memory and upload now)
                var map = _vertexMap ??= new Dictionary<int, int>();
                map.Clear();
                int Vertex(int i)
                {
                    if (map.TryGetValue(i, out int v)) return v;
                    var p = src.Positions[i];
                    var n = src.Normals[i];
                    // UV.x: metres along the wall a vertex is on (its normal gives the wall's direction; the library
                    // gives every quad its own vertices), so patterns line up on walls at any angle (D70)
                    float along = MathF.Abs(n.Y) < 0.5f ? p.X * -n.Z + p.Z * n.X : p.X + p.Z;
                    v = mesh.Add(new Vector3(ox + p.X, p.Y, oz + p.Z), new Vector3(n.X, n.Y, n.Z),
                        new Vector2(along, p.Y - floor), new Vector2(0f, top), col);
                    map[i] = v;
                    return v;
                }
                for (int t = 0; t + 2 < idx.Count; t += 3)
                {
                    int va = Vertex(idx[t]), vb = Vertex(idx[t + 1]), vc = Vertex(idx[t + 2]);
                    var nf = src.Normals[idx[t]];
                    mesh.Tri(va, vb, vc, new Vector3(nf.X, nf.Y, nf.Z));
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
            "glass" or "glass_block" or "shopfront" => KindGlass,
            "balcony_door" => KindBalconyDoor,   // (balcony glass "rail_glass" stays plain: as glass it read as solid blue panels, D76)
            "roof" => KindFlatRoof,
            "cladding" or "plant" or "patch_sheet" => KindRibbed,
            // a garage row's up-and-over doors (door0 … door7, D74): pressed steel
            _ when role.Length == 5 && role.StartsWith("door") && char.IsDigit(role[4]) => KindRibbed,
            "curtain" => KindCurtain,
            "timber" => KindTimber,
            "concrete_board" or "fin" => KindBoardConcrete,
            "boards" => KindBoards,
            "brick" or "chimney" => KindBrick,
            "roller" => KindRoller,
            "door" => KindDoor,
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

    /// <summary>
    /// The OSM adapter's translation (BadBuilding D23, Baltic Drive D72): the map's use string
    /// ("yes;amenity=school;year=1965") into BadBuilding's request facts. The building value is passed as the type;
    /// an untyped building (yes) takes its type from what it's used for, where that says what kind of building it is.
    /// </summary>
    public readonly record struct Use(string? Type, int? Year, bool Heritage, string? Architecture, int? Levels, string? Material = null,
        string? Religion = null, string? Denomination = null)
    {
        public static Use Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) return default;
            var parts = s.Split(';');
            string type = parts[0];
            var f = new Dictionary<string, string>();
            for (int i = 1; i < parts.Length; i++)
            {
                int eq = parts[i].IndexOf('=');
                if (eq > 0) f[parts[i][..eq]] = parts[i][(eq + 1)..];
            }
            if (type is "yes" or "building")
                type = (f.GetValueOrDefault("amenity"), f.GetValueOrDefault("office"), f.GetValueOrDefault("shop")) switch
                {
                    ("school", _, _) => "school",
                    ("kindergarten", _, _) => "kindergarten",
                    ("college" or "university", _, _) => "university",
                    ("hospital", _, _) => "hospital",
                    ("clinic" or "doctors", _, _) => "clinic",
                    ("fire_station", _, _) => "fire_station",
                    ("police", _, _) => "police",
                    ("townhall", _, _) => "townhall",
                    ("place_of_worship", _, _) => "religious",
                    ("fuel", _, _) => "service",
                    (_, { } o, _) when o.Length > 0 => "office",
                    (_, _, { } sh) when sh.Length > 0 => sh is "supermarket" ? "supermarket" : sh is "kiosk" ? "kiosk" : "retail",
                    _ => type,
                };
            int? year = f.TryGetValue("year", out var y) && int.TryParse(y, out int yi) ? yi : null;
            bool heritage = f.ContainsKey("heritage") || f.GetValueOrDefault("historic") is "building" or "yes" or "monument";
            int? levels = f.TryGetValue("building:levels", out var lv) && float.TryParse(lv, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float lf) && lf >= 1 ? (int)MathF.Round(lf) : null;
            return new Use(type, year, heritage, f.GetValueOrDefault("building:architecture"), levels, f.GetValueOrDefault("building:material"),
                f.GetValueOrDefault("religion"), f.GetValueOrDefault("denomination"));
        }
    }

    /// <summary>Seeds shared by neighbours (D75, D77): a school site's look (120 m cells), and a district (300 m cells)
    /// for choices a whole quarter should make together, such as whether its big avenue blocks are socialist realism.</summary>
    private static Dictionary<string, ulong> StageSeedsFor(Use use, double x, double z)
    {
        var d = new Dictionary<string, ulong>
        {
            ["district"] = (ulong)BalticDrive.Core.Hash.Of((int)Math.Floor(x / 300.0), (int)Math.Floor(z / 300.0), 0, 78),
        };
        if (use.Type is "school" or "kindergarten" or "university")
            d["look"] = (ulong)BalticDrive.Core.Hash.Of((int)Math.Floor(x / 120.0), (int)Math.Floor(z / 120.0), 0, 77);
        return d;
    }

    /// <summary>Stone evidence shared by the parts of one building (D78): MDM's block has its sandstone tag on one piece
    /// only, and the rest came out as tenements.</summary>
    private static string? GroupStone(MapContext ctx, MapBuilding b)
    {
        if (!b.IsPart || b.Group < 0) return null;
        foreach (var o in ctx.Buildings)
            if (o.B.Group == b.Group && o.B.IsPart && !ReferenceEquals(o.B, b) && o.B.Use.Contains("building:material=sandstone")) return "sandstone";
        return null;
    }
}
