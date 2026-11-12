using System.Collections.Generic;
using Godot;

namespace BalticDrive.World;

/// <summary>Physics layers and small helpers for static collision built from triangle lists.</summary>
public static class Bodies
{
    public const uint Ground = 1, Roads = 2, Buildings = 4, Vehicles = 8, Props = 16;

    /// <summary>Adds a static body with one concave shape from <paramref name="faces"/> (3 vertices per triangle).</summary>
    public static StaticBody3D? AddFaces(Node3D node, List<Vector3>? faces, string surface, bool bothSides, uint layer)
    {
        if (faces == null || faces.Count == 0) return null;
        var body = new StaticBody3D { CollisionLayer = layer, CollisionMask = 0 };
        var shape = new ConcavePolygonShape3D { BackfaceCollision = bothSides };
        shape.SetFaces(faces.ToArray());
        body.AddChild(new CollisionShape3D { Shape = shape });
        body.SetMeta("surface", surface);
        node.AddChild(body);
        return body;
    }
}
