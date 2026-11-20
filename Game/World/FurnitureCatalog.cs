using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using BalticDrive.Core;
using BB = BadBuilding;

namespace BalticDrive.World;

/// <summary>
/// Models for furniture by kind (D87): the table in <c>Assets/Furniture/furniture.json</c> says which model stands in
/// for each kind NoorFengShui places (a sofa, a wardrobe, a fridge...), how it is turned and how it is fitted to the
/// piece's footprint. Any model Godot can load works (a Synty prefab, a .glb or .tscn of our own); several per kind are
/// picked between by the piece's id. A kind with no model, or whose model isn't on disk (the licensed Synty packs aren't
/// in the public repository), keeps the library's own generated shape.
/// <para>
/// Fits: <c>stretch</c> scales the model to the footprint and the piece's height; <c>footprint</c> to the footprint,
/// keeping its own height; <c>uniform</c> keeps its proportions and fits inside the footprint, standing at the wall.
/// <c>yaw</c> (degrees) turns the model first, so its front faces +Z (the way the piece faces).
/// </para>
/// </summary>
public sealed class FurnitureCatalog
{
    public const string Path = "res://Assets/Furniture/furniture.json";

    /// <summary>One loaded model: its meshes (materials baked in) with their places in the model, and its bounds.</summary>
    public sealed class Model
    {
        public required string Source;
        public required List<(Mesh Mesh, Transform3D Local)> Parts;
        public required Aabb Bounds;
        public float Yaw;
        public string Fit = "stretch";
    }

    private readonly Dictionary<string, List<Model>> _kinds = new();
    /// <summary>The models' materials, copied so they can glow faintly indoors like the rest of the interior.</summary>
    public readonly List<StandardMaterial3D> Materials = new();

    public bool Has(string kind) => _kinds.ContainsKey(kind);
    public IEnumerable<string> Kinds => _kinds.Keys;

    public static FurnitureCatalog Load(string path = Path)
    {
        var cat = new FurnitureCatalog();
        if (!FileAccess.FileExists(path)) { Log.Info("Furniture", $"no {path}: generated furniture only"); return cat; }
        using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(path));
        if (!doc.RootElement.TryGetProperty("kinds", out var kinds)) return cat;
        var mats = new Dictionary<Material, Material>();
        foreach (var k in kinds.EnumerateObject())
            foreach (var e in k.Value.EnumerateArray())
            {
                string src = e.GetProperty("model").GetString() ?? "";
                if (!ResourceLoader.Exists(src)) continue;   // not installed (Synty): the generated piece stays
                var m = cat.LoadModel(src, mats);
                if (m == null) continue;
                if (e.TryGetProperty("yaw", out var y)) m.Yaw = y.GetSingle();
                if (e.TryGetProperty("fit", out var f)) m.Fit = f.GetString() ?? "stretch";
                (cat._kinds.TryGetValue(k.Name, out var list) ? list : cat._kinds[k.Name] = new()).Add(m);
            }
        Log.Info("Furniture", $"models for {string.Join(", ", cat._kinds.Select(kv => $"{kv.Key} ({kv.Value.Count})"))}");
        return cat;
    }

    private Model? LoadModel(string src, Dictionary<Material, Material> mats)
    {
        var res = GD.Load(src);
        Node3D? root = res switch { PackedScene ps => ps.Instantiate<Node3D>(), Mesh mesh => new MeshInstance3D { Mesh = mesh }, _ => null };
        if (root == null) return null;
        var parts = new List<(Mesh, Transform3D)>();
        var bounds = new Aabb(); bool first = true;
        void Walk(Node n, Transform3D t)
        {
            if (n is Node3D n3 && n != root) t = t * n3.Transform;
            if (n is MeshInstance3D mi && mi.Mesh != null)
            {
                // the materials as the instance has them, baked into a copy of the mesh (a multimesh can't override per surface)
                var mesh = (Mesh)mi.Mesh.Duplicate();
                for (int s = 0; s < mesh.GetSurfaceCount(); s++)
                {
                    var mat = mi.GetSurfaceOverrideMaterial(s) ?? mi.MaterialOverride ?? mesh.SurfaceGetMaterial(s);
                    if (mat != null && mesh is ArrayMesh am) am.SurfaceSetMaterial(s, Indoor(mat, mats));
                }
                parts.Add((mesh, t));
                var b = t * mesh.GetAabb();
                bounds = first ? b : bounds.Merge(b);
                first = false;
            }
            foreach (var c in n.GetChildren()) Walk(c, t);
        }
        Walk(root, Transform3D.Identity);
        root.Free();
        return parts.Count == 0 ? null : new Model { Source = src, Parts = parts, Bounds = bounds };
    }

    /// <summary>A copy of a model's material that also glows with its own colour, as much as the daylight allows (the
    /// interior's fill light; see <see cref="Interiors"/>).</summary>
    private Material Indoor(Material m, Dictionary<Material, Material> done)
    {
        if (done.TryGetValue(m, out var d)) return d;
        if (m is not StandardMaterial3D sm) { done[m] = m; return m; }
        var c = (StandardMaterial3D)sm.Duplicate();
        c.EmissionEnabled = true;
        c.Emission = c.AlbedoColor;
        c.EmissionTexture = c.AlbedoTexture;
        // multiply: the emission is the texture's own colour (add would be the colour plus the texture: white)
        c.EmissionOperator = BaseMaterial3D.EmissionOperatorEnum.Multiply;
        c.EmissionEnergyMultiplier = 0.15f;
        Materials.Add(c);
        done[m] = c;
        return c;
    }

    /// <summary>The model for a piece (picked by its id among the kind's models), or null.</summary>
    public Model? For(BB.Interior.FurnitureItem item)
    {
        if (!_kinds.TryGetValue(item.Kind, out var list) || list.Count == 0) return null;
        return list[(int)(BB.Generation.Rng.Fnv1a(item.Id) % (ulong)list.Count)];
    }

    /// <summary>Where the model goes for a piece, in building space: turned to face the way the piece faces, fitted to
    /// its footprint, its back against the wall (or centred, for a table), standing on the floor. Plain structs only, so
    /// it runs on a worker.</summary>
    public static Transform3D Place(Model m, BB.Interior.FurnitureItem item, BB.Interior.Frame frame)
    {
        var fu = frame.AxisU; var fv = frame.AxisV;
        var F = new Vector3(fu.X * item.Facing.X + fv.X * item.Facing.Y, 0, fu.Z * item.Facing.X + fv.Z * item.Facing.Y).Normalized();
        var A = Vector3.Up.Cross(F);   // the piece's x: along the wall
        bool facesV = MathF.Abs(item.Facing.Y) > 0.5f;
        float W = facesV ? item.Max.X - item.Min.X : item.Max.Y - item.Min.Y;
        float D = facesV ? item.Max.Y - item.Min.Y : item.Max.X - item.Min.X;
        // the model's bounds once turned by its yaw
        var yaw = new Basis(Vector3.Up, Mathf.DegToRad(m.Yaw));
        var b = m.Bounds;
        Vector3 lo = new(float.MaxValue, float.MaxValue, float.MaxValue), hi = new(float.MinValue, float.MinValue, float.MinValue);
        for (int i = 0; i < 8; i++)
        {
            var p = yaw * (b.Position + new Vector3((i & 1) != 0 ? b.Size.X : 0, (i & 2) != 0 ? b.Size.Y : 0, (i & 4) != 0 ? b.Size.Z : 0));
            lo = lo.Min(p); hi = hi.Max(p);
        }
        var size = hi - lo;
        float sx = W / MathF.Max(size.X, 0.01f), sz = D / MathF.Max(size.Z, 0.01f), sy = item.Height / MathF.Max(size.Y, 0.01f);
        switch (m.Fit)
        {
            case "footprint": sy = 1f; break;
            case "uniform": sx = sz = sy = MathF.Min(sx, sz); break;
        }
        var basis = new Basis(A, Vector3.Up, F) * Basis.FromScale(new Vector3(sx, sy, sz)) * yaw;
        var c = frame.P((item.Min.X + item.Max.X) * 0.5f, item.Floor, (item.Min.Y + item.Max.Y) * 0.5f);
        var centre = new Vector3(c.X, c.Y, c.Z);
        float back = item.AgainstWall ? D * 0.5f + sz * lo.Z : sz * (lo.Z + hi.Z) * 0.5f;
        var origin = centre - A * (sx * (lo.X + hi.X) * 0.5f) - F * back;
        origin.Y = item.Floor - sy * lo.Y;
        return new Transform3D(basis, origin);
    }
}
