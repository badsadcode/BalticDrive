using System;
using System.Collections.Generic;
using Godot;

namespace BalticDrive.Trees;

/// <summary>
/// The tree species: Tom's Thornwake Woodland pack (Assets/Vegetation, three levels of detail each). The index is what
/// tree cells, impostors and placement use.
/// </summary>
public static class TreeSpecies
{
    public static readonly string[] Names =
    {
        "Pine_A", "Pine_B", "Pine_C", "Spruce_A", "Spruce_B", "Oak_A", "Oak_B", "Oak_C", "Birch_Green_A", "Birch_Green_B",
        "Birch_Green_C", "Maple_Autumn_A", "Poplar_A", "Willow_A", "Sparse_A", "Birch_Autumn_A", "Birch_Autumn_B",
    };

    public const int PineA = 0, PineB = 1, PineC = 2, SpruceA = 3, SpruceB = 4, OakA = 5, OakB = 6, OakC = 7, BirchA = 8, BirchB = 9,
        BirchC = 10, MapleAutumn = 11, Poplar = 12, Willow = 13, Sparse = 14, BirchAutumnA = 15, BirchAutumnB = 16;
}

/// <summary>Render layers for the tree batches (as Thornwake's, so its shaders and bake behave the same).</summary>
public static class TreeLayers
{
    public const uint TreesNear = 4, TreesFar = 8, Impostors = 128;
}

/// <summary>
/// Tree meshes per species and level of detail, taken once from the woodland GLBs (ported from Thornwake's
/// TreeCatalog / WoodlandAssets): leaves get the shared canopy shader, bark the tree-bark shader, both of which fade
/// across the 3D → impostor hand-over.
/// </summary>
public sealed class TreeCatalog
{
    private readonly Dictionary<(int Species, int Lod), List<(Mesh Mesh, Transform3D Transform)>> _parts = new();
    private readonly Dictionary<Material, Material> _barks = new();
    private readonly Dictionary<string, ShaderMaterial> _foliage = new();
    private Shader? _canopy, _bark;

    public List<(Mesh Mesh, Transform3D Transform)> PartsOf(int species, int lod)
    {
        if (_parts.TryGetValue((species, lod), out var parts)) return parts;
        parts = new List<(Mesh, Transform3D)>();
        string name = "TREE_" + TreeSpecies.Names[species] + (lod == 0 ? "" : "_LOD" + lod);
        var scene = GD.Load<PackedScene>("res://Assets/Vegetation/" + name + ".glb")
            ?? throw new InvalidOperationException("Missing woodland model: " + name);
        var root = scene.Instantiate<Node3D>();
        foreach (var source in Meshes(root))
        {
            var mesh = (Mesh)source.Mesh.Duplicate();
            for (int s = 0; s < mesh.GetSurfaceCount(); s++) mesh.SurfaceSetMaterial(s, Material(source.GetActiveMaterial(s)));
            Transform3D transform = source.Transform;
            Node? parent = source.GetParent();
            while (parent is Node3D p && p != root) { transform = p.Transform * transform; parent = p.GetParent(); }
            parts.Add((mesh, transform));
        }
        root.Free();
        return _parts[(species, lod)] = parts;
    }

    private Material Material(Material? material)
    {
        if (material is not BaseMaterial3D b) return material!;
        if (b.ResourceName.Contains("Bark"))
        {
            if (_barks.TryGetValue(material, out var done)) return done;
            _bark ??= GD.Load<Shader>("res://Shaders/Trees/TreeBark.gdshader");
            var bark = new ShaderMaterial { Shader = _bark };
            bark.SetShaderParameter("has_texture", b.AlbedoTexture != null);
            if (b.AlbedoTexture != null) bark.SetShaderParameter("albedo_tex", b.AlbedoTexture);
            bark.SetShaderParameter("tint", b.AlbedoColor);
            return _barks[material] = bark;
        }
        string key = b.ResourceName;
        if (!_foliage.TryGetValue(key, out var foliage))
        {
            _canopy ??= GD.Load<Shader>("res://Assets/Vegetation/Canopy.gdshader");
            foliage = new ShaderMaterial { Shader = _canopy, ResourceName = key };
            foliage.SetShaderParameter("pixel_map", b.AlbedoTexture);
            foliage.SetShaderParameter("tint", b.AlbedoColor);
            _foliage[key] = foliage;
        }
        return foliage;
    }

    public static IEnumerable<MeshInstance3D> Meshes(Node node)
    {
        if (node is MeshInstance3D mesh) yield return mesh;
        foreach (Node child in node.GetChildren())
            foreach (var found in Meshes(child)) yield return found;
    }
}

/// <summary>Packs MultiMesh instances into one float buffer (Godot's layout: 3x4 transform by rows, then custom data).</summary>
public static class MultiMeshFill
{
    public static void Put(float[] buffer, int offset, in Transform3D t)
    {
        var b = t.Basis;
        buffer[offset] = b.X.X; buffer[offset + 1] = b.Y.X; buffer[offset + 2] = b.Z.X; buffer[offset + 3] = t.Origin.X;
        buffer[offset + 4] = b.X.Y; buffer[offset + 5] = b.Y.Y; buffer[offset + 6] = b.Z.Y; buffer[offset + 7] = t.Origin.Y;
        buffer[offset + 8] = b.X.Z; buffer[offset + 9] = b.Y.Z; buffer[offset + 10] = b.Z.Z; buffer[offset + 11] = t.Origin.Z;
    }

    public static void Put(float[] buffer, int offset, Color c)
    {
        buffer[offset] = c.R; buffer[offset + 1] = c.G; buffer[offset + 2] = c.B; buffer[offset + 3] = c.A;
    }
}
