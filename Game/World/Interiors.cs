using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using BalticDrive.Core;
using BB = BadBuilding;
using NVec2 = System.Numerics.Vector2;
using NVec3 = System.Numerics.Vector3;

namespace BalticDrive.World;

/// <summary>
/// Buildings you can walk into (D81, BadBuilding D33). The map's worker threads hand over a small shell for every
/// block of flats they build near the player (outline, storeys, entrances, the facade's openings); when you are on
/// foot and come within reach of one, its interior is planned on a worker thread and added as one mesh and one
/// static body, anchored where the building stands. While it is loaded its street doors are open (the building
/// shader hides them), and while you are inside the outline or in a doorway the building's solid mass stops counting
/// for you (<see cref="OnFoot.Walker.Indoors"/>): the interior's own walls, floors and stairs hold you.
/// </summary>
public partial class Interiors : Node
{
    private sealed record Entry(BB.Interior.Shell Shell, double X, double Z);
    private static readonly ConcurrentDictionary<string, Entry> _shells = new();

    /// <summary>A shell from the map's worker threads; <paramref name="x"/>, <paramref name="z"/> is the building's
    /// origin in world metres (building space is the world's axes, heights absolute).</summary>
    public static void Register(BB.Interior.Shell shell, double x, double z) => _shells[shell.Id] = new Entry(shell, x, z);

    public static int Known => _shells.Count;

    private static readonly System.Text.Json.JsonSerializerOptions ShellJson = new() { IncludeFields = true };

    /// <summary>A known shell and its origin, by building id (D88: to keep with a flat safehouse).</summary>
    public static (BB.Interior.Shell Shell, double X, double Z, string Json)? Find(string id) =>
        _shells.TryGetValue(id, out var e) ? (e.Shell, e.X, e.Z, System.Text.Json.JsonSerializer.Serialize(e.Shell, ShellJson)) : null;

    /// <summary>The interior of a building right now, planned on this thread if it isn't loaded (D88: waking up in a
    /// flat safehouse far from where you died). The shell comes from the save; it is registered again too.</summary>
    public Loaded? LoadNow(string shellJson, double x, double z)
    {
        BB.Interior.Shell? shell;
        try { shell = System.Text.Json.JsonSerializer.Deserialize<BB.Interior.Shell>(shellJson, ShellJson); }
        catch (Exception ex) { Log.Error("Interiors", $"safehouse shell: {ex.Message}"); return null; }
        if (shell == null) return null;
        if (_loaded.TryGetValue(shell.Id, out var have)) return have;
        var entry = new Entry(shell, x, z);
        _shells[shell.Id] = entry;
        var m = BB.Interior.InteriorGenerator.Generate(shell, out var why);
        if (m == null) { Log.Error("Interiors", $"safehouse {shell.Id}: no interior ({why})"); return null; }
        Build(entry, m, Prepare(m, Catalog));
        _none.Remove(shell.Id);
        return _loaded[shell.Id];
    }

    /// <summary>Self-tests: drop every loaded interior (as if far away).</summary>
    public void DropAll()
    {
        foreach (var l in _loaded.Values) l.Node.QueueFree();
        _loaded.Clear();
        Inside = null;
    }

    /// <summary>Self-tests: the known shells nearest a world point, with their origins.</summary>
    public static List<(BB.Interior.Shell Shell, double X, double Z)> NearestShells(double x, double z, int count) =>
        _shells.Values.OrderBy(e => (e.X - x) * (e.X - x) + (e.Z - z) * (e.Z - z)).Take(count).Select(e => (e.Shell, e.X, e.Z)).ToList();

    /// <summary>A loaded interior and the node it lives in.</summary>
    public sealed class Loaded
    {
        public required string Id;
        public required BB.Interior.InteriorModel Model;
        public required Node3D Node;
        public double X, Z;
        public float Distance;
        /// <summary>Building space: a scene point minus the node's position.</summary>
        public NVec3 ToBuilding(Vector3 p) => new(p.X - Node.Position.X, p.Y, p.Z - Node.Position.Z);
        public Vector3 ToScene(NVec3 p) => new(p.X + Node.Position.X, p.Y, p.Z + Node.Position.Z);
    }

    private readonly Main _main;
    private readonly Dictionary<string, Loaded> _loaded = new();
    private readonly HashSet<string> _pending = new(), _none = new();
    private readonly ConcurrentQueue<(Entry E, BB.Interior.InteriorModel? M, Prepared? P, string Why)> _done = new();

    /// <summary>The Godot-ready arrays, made on the worker thread so adding an interior costs the frame little: one
    /// surface per material role, and every collider's faces as one concave shape.</summary>
    private sealed record Prepared(List<(string Role, Vector3[] V, Vector3[] N, Vector2[] Uv)> Surfaces, Vector3[] Faces,
        Dictionary<(FurnitureCatalog.Model Model, int Part), List<Transform3D>> Placed);

    private static Prepared Prepare(BB.Interior.InteriorModel m, FurnitureCatalog cat)
    {
        var surfaces = new List<(string, Vector3[], Vector3[], Vector2[])>();
        // the rooms, and the generated furniture of every kind the catalogue has no model for (D87)
        var placed = new Dictionary<(FurnitureCatalog.Model, int), List<Transform3D>>();
        var sources = new List<BB.Geometry.Mesh> { m.Mesh };
        foreach (var item in m.Furniture)
        {
            var model = cat.For(item);
            if (model == null) { sources.Add(item.Shape); continue; }
            var t = FurnitureCatalog.Place(model, item, m.Frame);
            for (int pi = 0; pi < model.Parts.Count; pi++)
                (placed.TryGetValue((model, pi), out var list) ? list : placed[(model, pi)] = new()).Add(t * model.Parts[pi].Local);
        }
        foreach (var role in sources.SelectMany(x => x.Groups.Keys).Distinct())
        {
            int count = sources.Sum(x => x.Groups.TryGetValue(role, out var g) ? g.Count : 0);
            if (count == 0) continue;
            var v = new Vector3[count]; var n = new Vector3[count]; var uv = new Vector2[count];
            int k = 0;
            foreach (var src in sources)
            {
                if (!src.Groups.TryGetValue(role, out var idx)) continue;
                foreach (int i in idx)
                {
                    var p = src.Positions[i]; var q = src.Normals[i];
                    v[k] = new Vector3(p.X, p.Y, p.Z);
                    n[k] = new Vector3(q.X, q.Y, q.Z);
                    // UVs in metres along the surface, for later textures
                    uv[k] = MathF.Abs(q.Y) > 0.5f ? new Vector2(p.X, p.Z) : new Vector2(p.X * -q.Z + p.Z * q.X, p.Y);
                    k++;
                }
            }
            // Godot draws clockwise triangles as the front: the library's wind counter-clockwise about their normal
            // (without this every face was seen from behind: floors and outer walls vanished, D83)
            for (int t = 0; t + 2 < v.Length; t += 3)
            {
                (v[t + 1], v[t + 2]) = (v[t + 2], v[t + 1]);
                (n[t + 1], n[t + 2]) = (n[t + 2], n[t + 1]);
                (uv[t + 1], uv[t + 2]) = (uv[t + 2], uv[t + 1]);
            }
            surfaces.Add((role, v, n, uv));
        }
        var faces = new Vector3[m.Colliders.Count * 36];
        int f = 0;
        foreach (var c in m.Colliders)
        {
            var x = new Vector3(c.AxisX.X, c.AxisX.Y, c.AxisX.Z) * c.Half.X;
            var y = new Vector3(c.AxisY.X, c.AxisY.Y, c.AxisY.Z) * c.Half.Y;
            var z = new Vector3(c.AxisX.X, c.AxisX.Y, c.AxisX.Z).Cross(new Vector3(c.AxisY.X, c.AxisY.Y, c.AxisY.Z)) * c.Half.Z;
            var o = new Vector3(c.Centre.X, c.Centre.Y, c.Centre.Z);
            Vector3 Corner(int i) => o + ((i & 1) != 0 ? x : -x) + ((i & 2) != 0 ? y : -y) + ((i & 4) != 0 ? z : -z);
            // six faces of two triangles (both sides collide, so the winding doesn't matter)
            foreach (var (a, b, cc, d) in new[] { (0, 1, 3, 2), (4, 6, 7, 5), (0, 4, 5, 1), (2, 3, 7, 6), (0, 2, 6, 4), (1, 5, 7, 3) })
            {
                faces[f++] = Corner(a); faces[f++] = Corner(b); faces[f++] = Corner(cc);
                faces[f++] = Corner(a); faces[f++] = Corner(cc); faces[f++] = Corner(d);
            }
        }
        return new Prepared(surfaces, faces, placed);
    }
    /// <summary>Models for furniture by kind (D87); read-only after loading, so the workers use it too.</summary>
    public FurnitureCatalog Catalog { get; }
    private double _scan;
    private static readonly Dictionary<string, StandardMaterial3D> _materials = new();

    /// <summary>How near (m from the outline) an interior is planned, and how far it's dropped again;
    /// <c>--interior-reach=M</c> for tests.</summary>
    public static float LoadReach = 22f, DropReach = 45f;

    /// <summary>The interior you're in (null outside), the room you're in, and the storey.</summary>
    public Loaded? Inside { get; private set; }
    public BB.Interior.Room? Room { get; private set; }
    public IEnumerable<Loaded> All => _loaded.Values;

    public Interiors(Main main)
    {
        _main = main; Name = "Interiors";
        Catalog = FurnitureCatalog.Load();
        if (Main.Args.TryGetValue("interior-reach", out var r) && float.TryParse(r, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rf))
        { LoadReach = rf; DropReach = rf * 2; }
    }

    private readonly Queue<(ArrayMesh Mesh, BB.Interior.InteriorModel Model, (string Role, Vector3[] V, Vector3[] N, Vector2[] Uv) S)> _uploads = new();

    /// <summary>Adds queued surfaces until a few milliseconds are spent.</summary>
    private void Upload()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (_uploads.Count > 0 && sw.Elapsed.TotalMilliseconds < 4.0)
        {
            var (mesh, m, (role, v, n, uv)) = _uploads.Dequeue();
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = v;
            arrays[(int)Mesh.ArrayType.Normal] = n;
            arrays[(int)Mesh.ArrayType.TexUV] = uv;
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, Material(m, role));
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        Upload();
        while (_done.TryDequeue(out var d))
        {
            _pending.Remove(d.E.Shell.Id);
            if (d.M == null || d.P == null) { _none.Add(d.E.Shell.Id); Log.Info("Interiors", $"{d.E.Shell.Id}: no interior ({d.Why})"); continue; }
            Build(d.E, d.M, d.P);
        }

        var w = _main.Walker;
        bool onFoot = w != null && w.Active;
        _scan -= delta;
        if (_scan <= 0 && onFoot)
        {
            _scan = 0.3;
            Scan(w!.GlobalPosition);
        }
        if (!onFoot) { if (w != null) w.Indoors = false; Inside = null; Room = null; RenderingServer.GlobalShaderParameterSet("player_near", Vector4.Zero); }
        else Track(w!);
        Doors();
        Daylight();
    }

    /// <summary>Light that bounces in through the windows, faked (D81): the screen-space ambient occlusion all but
    /// blacks out the sky's light in a 2.6 m room, so every interior surface glows a little with its own colour, as
    /// much as the daylight outside allows. The sun itself comes in only through the windows (double-sided shadows).</summary>
    private void Daylight()
    {
        float sun = _main.Atmosphere?.Sun?.LightEnergy ?? 1f;
        float k = 0.04f + 0.36f * Math.Clamp(sun, 0f, 1.6f) / 1.6f;
        if (MathF.Abs(k - _fill) < 0.003f) return;
        _fill = k;
        // ceilings catch the light the floors throw up
        foreach (var cm in Catalog.Materials) cm.EmissionEnergyMultiplier = k;
        foreach (var (key, m) in _materials)
            if (m.Transparency == BaseMaterial3D.TransparencyEnum.Disabled) m.EmissionEnergyMultiplier = key.StartsWith("ceiling") ? k * 1.6f : k;
    }
    private float _fill = -1;

    /// <summary>Plans the interiors within reach of the player, drops the far ones and forgets far shells.</summary>
    private void Scan(Vector3 at)
    {
        var (px, _, pz) = WorldOrigin.ToWorld(at);
        foreach (var (id, e) in _shells)
        {
            double dx = px - e.X, dz = pz - e.Z;
            if (dx * dx + dz * dz > 800.0 * 800.0) { _shells.TryRemove(id, out _); continue; }
            if (_loaded.ContainsKey(id) || _pending.Contains(id) || _none.Contains(id)) continue;
            float d = OutlineDistance(e.Shell.Outer, (float)dx, (float)dz);
            if (d > LoadReach) continue;
            _pending.Add(id);
            var entry = e;
            Task.Run(() =>
            {
                BB.Interior.InteriorModel? m = null; Prepared? prep = null; string why = "";
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try { m = BB.Interior.InteriorGenerator.Generate(entry.Shell, out why); if (m != null) { prep = Prepare(m, Catalog); m.Report.Metrics["ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1); } }
                catch (Exception ex) { why = ex.Message; }
                _done.Enqueue((entry, m, prep, why));
            });
        }
        foreach (var l in _loaded.Values.ToList())
        {
            double dx = px - l.X, dz = pz - l.Z;
            l.Distance = OutlineDistance(l.Model.Footprint, (float)dx, (float)dz);
            if (l.Distance > DropReach && Inside != l)
            {
                l.Node.QueueFree();
                _loaded.Remove(l.Id);
            }
        }
    }

    private static float OutlineDistance(IReadOnlyList<NVec2> ring, float x, float z)
    {
        var p = new NVec2(x, z);
        if (BB.Geometry.Polygon.Contains(ring, p)) return 0;
        float best = float.MaxValue;
        for (int i = 0; i < ring.Count; i++) best = MathF.Min(best, BB.Geometry.Polygon.DistanceToSegment(p, ring[i], ring[(i + 1) % ring.Count]));
        return best;
    }

    /// <summary>Indoors when inside an interior's outline (between its lowest floor and its roof) or in one of its
    /// doorways; which room.</summary>
    private void Track(OnFoot.Walker w)
    {
        Loaded? inside = null;
        foreach (var l in _loaded.Values)
        {
            var p = l.ToBuilding(w.GlobalPosition);
            var m = l.Model;
            if (p.Y > m.Floor - 1.5f && p.Y < m.Top + 1f && BB.Geometry.Polygon.Contains(m.Footprint, new NVec2(p.X, p.Z))) { inside = l; break; }
            // out on a balcony (or in its doorway): still the interior's (D83)
            foreach (var bs in m.Balconies)
            {
                var d = p - bs.Position;
                var o = new NVec2(bs.Out.X, bs.Out.Z);
                float outward = d.X * o.X + d.Z * o.Y, along = MathF.Abs(d.X * -o.Y + d.Z * o.X);
                if (along < MathF.Max(0.8f, bs.Width * 0.5f) && outward > -0.5f && outward < bs.Depth + 0.4f && d.Y > -0.6f && d.Y < 2.4f) { inside = l; break; }
            }
            if (inside != null) break;
            foreach (var e in m.Entrances)
            {
                var d = p - e.Position;
                var o = new NVec2(e.Out.X, e.Out.Z);
                float outward = d.X * o.X + d.Z * o.Y, along = MathF.Abs(d.X * -o.Y + d.Z * o.X);
                if (along < 0.8f && outward > -0.4f && outward < 1.4f && d.Y > -1.2f && d.Y < 2.4f) { inside = l; break; }
            }
            if (inside != null) break;
        }
        if (inside != Inside)
        {
            if (inside != null) Log.Info("Interiors", $"in {inside.Id}");
            else if (Inside != null) Log.Info("Interiors", $"out of {Inside.Id}");
        }
        Inside = inside;
        w.Indoors = inside != null;
        // balcony doors open within reach while you're inside
        var chest = w.GlobalPosition + Vector3.Up * 1.1f;
        RenderingServer.GlobalShaderParameterSet("player_near", inside != null ? new Vector4(chest.X, chest.Y, chest.Z, 2.2f) : Vector4.Zero);
        Room = inside?.Model.RoomAt(inside.ToBuilding(w.GlobalPosition + Vector3.Up * 0.3f));
    }

    /// <summary>The street doors of the loaded interiors nearest the player stand open (up to four).</summary>
    private void Doors()
    {
        var doors = _loaded.Values.OrderBy(l => l.Distance).SelectMany(l => l.Model.Entrances.Select(e => (l, e))).Take(4).ToList();
        for (int i = 0; i < 4; i++)
        {
            var v = Vector4.Zero;
            if (i < doors.Count)
            {
                var (l, e) = doors[i];
                var c = l.ToScene(e.Position + NVec3.UnitY * 1.15f - e.Out * 0.25f);
                v = new Vector4(c.X, c.Y, c.Z, 1.5f);
            }
            RenderingServer.GlobalShaderParameterSet($"open_door{i}", v);
        }
    }

    private void Build(Entry e, BB.Interior.InteriorModel m, Prepared prep)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var node = new Node3D { Name = "Interior" };
        AddChild(node);
        WorldOrigin.Anchor(node, e.X, e.Z);

        // the surfaces go up a few at a time over the next frames (D86: a furnished block is ~80k triangles, and uploading
        // it at once stalled a frame for ~50 ms); the walls' physics is there at once
        var mesh = new ArrayMesh();
        foreach (var srf in prep.Surfaces) _uploads.Enqueue((mesh, m, srf));
        // double-sided shadows: the walls, floors and ceilings keep the sun out except through the windows
        node.AddChild(new MeshInstance3D { Mesh = mesh, Name = "Rooms", CastShadow = GeometryInstance3D.ShadowCastingSetting.DoubleSided });
        // furniture models: one multimesh per model part for the whole building
        foreach (var ((model, part), list) in prep.Placed)
        {
            var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, Mesh = model.Parts[part].Mesh, InstanceCount = list.Count };
            for (int i = 0; i < list.Count; i++) mm.SetInstanceTransform(i, list[i]);
            node.AddChild(new MultiMeshInstance3D { Multimesh = mm, Name = "Furniture", CastShadow = GeometryInstance3D.ShadowCastingSetting.DoubleSided });
        }

        double tMesh = sw.Elapsed.TotalMilliseconds;
        var body = new StaticBody3D { CollisionLayer = Bodies.Interior, CollisionMask = 0, Name = "Walls" };
        body.SetMeta("surface", "hard");
        var shape = new ConcavePolygonShape3D { BackfaceCollision = true };
        shape.SetFaces(prep.Faces);
        body.AddChild(new CollisionShape3D { Shape = shape });
        node.AddChild(body);
        // --interior-dump=DIR: the plan of every interior built, for checking the layouts (D81)
        if (Main.Args.TryGetValue("interior-dump", out var dump))
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(dump, e.Shell.Id.Replace(':', '_') + ".json"), m.PlanJson(e.Shell.Style));
            System.IO.File.WriteAllText(System.IO.Path.Combine(dump, e.Shell.Id.Replace(':', '_') + ".shell.json"),
                System.Text.Json.JsonSerializer.Serialize(e.Shell, new System.Text.Json.JsonSerializerOptions { IncludeFields = true }));
        }
        var l = new Loaded { Id = e.Shell.Id, Model = m, Node = node, X = e.X, Z = e.Z };
        _loaded[e.Shell.Id] = l;
        var bySide = e.Shell.Openings.GroupBy(o => o.Kind).Select(g => $"{g.Count()} {g.Key}");
        Log.Info("Interiors", $"{e.Shell.Id}: openings {string.Join(", ", bySide)}; window panes {(m.Mesh.Groups.TryGetValue("window", out var wp) ? wp.Count / 6 : 0)}");
        Log.Info("Interiors", $"{e.Shell.Id}: {m.Rooms.Count} rooms, {m.Entrances.Count} staircases, {m.Levels} storeys, {m.Colliders.Count} boxes, {m.Mesh.TriangleCount} triangles; {m.Report.Status}; planned in {m.Report.Metrics.GetValueOrDefault("ms")} ms on a worker, added in {sw.Elapsed.TotalMilliseconds:0.0} ms ({tMesh:0.0} the mesh)");
    }

    private static StandardMaterial3D Material(BB.Interior.InteriorModel m, string role)
    {
        var def = m.Materials.TryGetValue(role, out var d) ? d : new BB.Model.MaterialDef(role, new NVec3(0.6f), 0.9f, 0f);
        // the colour is the building's own (seeded palettes), so one material per role and colour
        string key = $"{role}:{def.Color.X:0.000}:{def.Color.Y:0.000}:{def.Color.Z:0.000}";
        if (_materials.TryGetValue(key, out var mat)) return mat;
        var c = def.Color;
        mat = new StandardMaterial3D
        {
            // linear colours from the library
            AlbedoColor = new Color(c.X, c.Y, c.Z, def.Alpha).LinearToSrgb() with { A = def.Alpha },
            Roughness = def.Roughness,
            Metallic = def.Metallic,
        };
        if (def.Alpha >= 0.99f)
        {
            mat.EmissionEnabled = true;
            mat.Emission = mat.AlbedoColor;
            mat.EmissionEnergyMultiplier = 0.15f;
        }
        else
        {
            mat.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
            mat.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        }
        _materials[key] = mat;
        return mat;
    }

    /// <summary>For F9 dumps and the HUD: where you are.</summary>
    public string Describe()
    {
        if (Inside == null) return $"interiors: {_loaded.Count} loaded, {Known} buildings known";
        int st = Inside.Model.StoreyAt(Inside.ToBuilding(_main.Walker!.GlobalPosition + Vector3.Up * 0.3f).Y);
        return $"inside {Inside.Id}, storey {st}, {Room?.Kind ?? "between rooms"} ({Room?.Id ?? "-"}); {_loaded.Count} loaded";
    }
}
