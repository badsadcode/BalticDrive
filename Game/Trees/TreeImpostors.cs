using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace BalticDrive.Trees;

/// <summary>
/// Tree impostors (round 30, docs/PERFORMANCE_TREES.md). Beyond a few hundred metres a tree is a few pixels tall, yet a
/// 3D tree still sends 200–700 leaf triangles (with alpha cut-outs) through the GPU; that is what made the 3.2 km tree
/// range cost half the frame rate. An impostor draws the same tree as one quad with a picture of its own model.
/// At start-up every species is rendered (unlit) from 8 sides around and from straight above, colour and normal,
/// <see cref="Resolution"/> pixels square, into two texture arrays (layer = species × 9 + view): about a second, during
/// world generation. TreeImpostor.gdshader blends the two side views nearest to the camera and lights them.
/// </summary>
public sealed class TreeImpostors
{
    public const int Views = 8, Frames = Views + 1, Resolution = 128;

    public bool Ready { get; private set; }
    public bool Baking { get; private set; }
    public ShaderMaterial Material { get; } = new() { Shader = GD.Load<Shader>("res://Shaders/Trees/TreeImpostor.gdshader") };
    /// <summary>Unit quad (x in [-0.5, 0.5], y in [0, 1]); the shader places it per instance.</summary>
    public ArrayMesh Quad { get; }
    private readonly Vector4[] _sizes = new Vector4[32];

    public TreeImpostors()
    {
        Quad = new ArrayMesh();
        using var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { new Vector3(-.5f, 0, 0), new Vector3(.5f, 0, 0), new Vector3(.5f, 1, 0), new Vector3(-.5f, 1, 0) };
        arrays[(int)Mesh.ArrayType.TexUV] = new[] { new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0), new Vector2(0, 0) };
        arrays[(int)Mesh.ArrayType.Normal] = new[] { Vector3.Back, Vector3.Back, Vector3.Back, Vector3.Back };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 2, 1, 0, 3, 2 };
        Quad.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        // Moved in the vertex shader: a box generous enough for any tree, so culling never drops one too early.
        Quad.CustomAabb = new Aabb(new Vector3(-30, -5, -30), new Vector3(60, 70, 60));
    }

    /// <summary>Render every species' views (asynchronously, a few frames per species) under <paramref name="host"/>.</summary>
    public async void Bake(Node host, TreeCatalog catalog)
    {
        if (Ready || Baking) return;
        // Headless runs (smoke tests) draw nothing: no bake, and nothing to wait for.
        if (DisplayServer.GetName() == "headless" || RenderingServer.GetCurrentRenderingDriverName() is "" or "dummy") { Ready = true; return; }
        Baking = true;
        var started = Time.GetTicksMsec();
        var bakeShader = GD.Load<Shader>("res://Shaders/Trees/ImpostorBake.gdshader");
        int species = TreeSpecies.Names.Length;
        var albedoAt = new Image[species * Frames];
        var normalAt = new Image[species * Frames];
        bool trace = OS.GetCmdlineUserArgs().Any(a => a.StartsWith("--impostor-dump="));
        // A batch of species at a time, each in its own little world with its 9 cameras: two rendered frames per batch
        // (colour, then normals) instead of two per species.
        const int Batch = 10;
        for (int first = 0; first < species; first += Batch)
        {
            int count = Math.Min(Batch, species - first);
            var viewports = new List<SubViewport>();
            var materials = new List<ShaderMaterial>();
            for (int s = first; s < first + count; s++)
            {
                if (trace) GD.Print($"impostor bake {s} {TreeSpecies.Names[s]}");
                var world = new World3D();
                var root = new Node3D();
                Aabb box = default;
                bool any = false;
                foreach (var (mesh, transform) in catalog.PartsOf(s, 0))
                {
                    var instance = new MeshInstance3D { Mesh = mesh, Transform = transform, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                    for (int surface = 0; surface < mesh.GetSurfaceCount(); surface++)
                    {
                        var bake = BakeMaterial(bakeShader, mesh.SurfaceGetMaterial(surface));
                        instance.SetSurfaceOverrideMaterial(surface, bake);
                        materials.Add(bake);
                    }
                    root.AddChild(instance);
                    var b = transform * mesh.GetAabb();
                    box = any ? box.Merge(b) : b;
                    any = true;
                }
                float half = Math.Max(Math.Max(Math.Abs(box.Position.X), Math.Abs(box.End.X)), Math.Max(Math.Abs(box.Position.Z), Math.Abs(box.End.Z)));
                float size = Math.Max(1, Math.Max(box.Size.Y, half * 2) * 1.04f);
                float bottom = box.Position.Y - size * .01f;
                var centre = new Vector3(0, bottom + size * .5f, 0);
                _sizes[s] = new Vector4(size, bottom, 0, 0);
                for (int v = 0; v < Frames; v++)
                {
                    var vp = new SubViewport
                    {
                        Size = new Vector2I(Resolution, Resolution), TransparentBg = true, OwnWorld3D = false, World3D = world,
                        RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled, Msaa3D = Viewport.Msaa.Disabled,
                        ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled, UseTaa = false,
                    };
                    var camera = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Current = true, Size = size, Near = .05f, Far = size * 4 };
                    vp.AddChild(camera);
                    if (v == 0) vp.AddChild(root);
                    host.AddChild(vp);
                    if (v < Views)
                    {
                        float a = v * Mathf.Tau / Views;
                        camera.LookAtFromPosition(centre + new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)) * size * 2, centre, Vector3.Up);
                    }
                    else camera.LookAtFromPosition(centre + Vector3.Up * size * 2, centre, Vector3.Forward);
                    viewports.Add(vp);
                }
            }
            for (int mode = 0; mode < 2; mode++)
            {
                foreach (var m in materials) m.SetShaderParameter("mode", mode);
                foreach (var vp in viewports) vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
                await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                await host.ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                for (int i = 0; i < viewports.Count; i++)
                {
                    var image = viewports[i].GetTexture().GetImage();
                    image.Convert(Image.Format.Rgba8);
                    (mode == 0 ? albedoAt : normalAt)[first * Frames + i] = image;
                }
            }
            foreach (var vp in viewports) vp.QueueFree();
        }
        var albedo = albedoAt.ToList();
        var normals = normalAt.ToList();
        // Fill the transparent pixels with the nearest leaf colour, so the smaller mip levels do not darken the edges.
        foreach (var image in albedo) Dilate(image);
        foreach (var image in normals) Dilate(image);
        foreach (var image in albedo.Concat(normals)) image.GenerateMipmaps();
        var albedoArray = new Texture2DArray();
        albedoArray.CreateFromImages(new Godot.Collections.Array<Image>(albedo));
        var normalArray = new Texture2DArray();
        normalArray.CreateFromImages(new Godot.Collections.Array<Image>(normals));
        Material.SetShaderParameter("albedo_frames", albedoArray);
        Material.SetShaderParameter("normal_frames", normalArray);
        Material.SetShaderParameter("species_size", _sizes);
        Material.SetShaderParameter("views", Views);
        if (OS.GetCmdlineUserArgs().FirstOrDefault(a => a.StartsWith("--impostor-dump=")) is { } dump)
        {
            string dir = dump[16..];
            System.IO.Directory.CreateDirectory(dir);
            for (int i = 0; i < albedo.Count; i += Frames) { Strip(albedo, i).SavePng($"{dir}/albedo-{i / Frames:00}.png"); Strip(normals, i).SavePng($"{dir}/normal-{i / Frames:00}.png"); }
        }
        Ready = true; Baking = false;
        GD.Print($"Tree impostors baked: {species} species × {Frames} views in {Time.GetTicksMsec() - started} ms");
    }

    /// <summary>An unlit bake material showing the same texture and tint as the tree's own material.</summary>
    private static ShaderMaterial BakeMaterial(Shader shader, Material? source)
    {
        var bake = new ShaderMaterial { Shader = shader };
        Texture2D? texture = null;
        var tint = Colors.White;
        switch (source)
        {
            case BaseMaterial3D b:
                texture = b.AlbedoTexture; tint = b.AlbedoColor; break;
            case ShaderMaterial m:
                // Leaves and placeholder plants: pixel_map; bark (TreeBark.gdshader): albedo_tex.
                texture = (m.GetShaderParameter("pixel_map").AsGodotObject() ?? m.GetShaderParameter("albedo_tex").AsGodotObject()) as Texture2D;
                var t = m.GetShaderParameter("tint");
                if (t.VariantType == Variant.Type.Color) tint = t.AsColor();
                break;
        }
        bake.SetShaderParameter("has_texture", texture != null);
        if (texture != null) bake.SetShaderParameter("albedo_tex", texture);
        bake.SetShaderParameter("tint", tint);
        return bake;
    }

    private static void Dilate(Image image)
    {
        int w = image.GetWidth(), h = image.GetHeight();
        var data = image.GetData();
        for (int pass = 0; pass < 6; pass++)
        {
            var next = (byte[])data.Clone();
            bool changed = false;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (data[i + 3] > 0) continue;
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                            int j = (yy * w + xx) * 4;
                            if (data[j + 3] == 0) continue;
                            r += data[j]; g += data[j + 1]; b += data[j + 2]; n++;
                        }
                    if (n == 0) continue;
                    // Colour only; the pixel stays transparent (alpha 1 of 255 marks it filled for the next pass).
                    next[i] = (byte)(r / n); next[i + 1] = (byte)(g / n); next[i + 2] = (byte)(b / n); next[i + 3] = 1;
                    changed = true;
                }
            data = next;
            if (!changed) break;
        }
        for (int i = 3; i < data.Length; i += 4) if (data[i] == 1) data[i] = 0;
        image.SetData(w, h, false, Image.Format.Rgba8, data);
    }

    private static Image Strip(List<Image> images, int start)
    {
        var strip = Image.CreateEmpty(Resolution * Frames, Resolution, false, Image.Format.Rgba8);
        for (int v = 0; v < Frames; v++)
        {
            var img = (Image)images[start + v].Duplicate();
            img.ClearMipmaps();
            strip.BlitRect(img, new Rect2I(0, 0, Resolution, Resolution), new Vector2I(v * Resolution, 0));
        }
        return strip;
    }
}
