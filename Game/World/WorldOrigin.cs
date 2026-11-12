using System;
using System.Collections.Generic;
using Godot;

namespace BalticDrive.World;

/// <summary>
/// The moving origin (the idea of Thornwake's WorldCoordinates / WorldStreamer.Rebase): world positions are doubles
/// (metres in the world projection, up to ~2,000 km across the Baltic loop); the scene holds float offsets from
/// <see cref="X"/>, <see cref="Z"/>. When the focus wanders more than <see cref="RebaseDistance"/> from the origin, the
/// origin jumps (snapped to a grid) and every anchored node is shifted by the same amount, so float precision never
/// degrades. Shaders get <c>world_offset</c> = origin modulo <see cref="ShaderPeriod"/> to keep procedural patterns fixed
/// to the ground across a jump.
/// </summary>
public static class WorldOrigin
{
    public const double Snap = 1024;
    public const double RebaseDistance = 2048;
    /// <summary>Procedural shader patterns repeat every this many metres (a faint seam every 64 km).</summary>
    public const double ShaderPeriod = 65536;

    public static double X { get; private set; }
    public static double Z { get; private set; }

    /// <summary>Called after a jump with the shift (new origin minus old). Physics bodies and anything not under an
    /// anchored root listen and move themselves by -shift.</summary>
    public static event Action<double, double>? Shifted;

    private static readonly List<WeakReference<Node3D>> _anchored = new();

    public static Vector3 ToLocal(double x, double y, double z) => new((float)(x - X), (float)y, (float)(z - Z));
    public static (double X, double Y, double Z) ToWorld(Vector3 local) => (local.X + X, local.Y, local.Z + Z);

    /// <summary>Places a streamed node whose local origin is the world point (wx, wz), and keeps it placed on jumps.</summary>
    public static void Anchor(Node3D node, double wx, double wz, float y = 0f)
    {
        node.SetMeta("wx", wx);
        node.SetMeta("wz", wz);
        node.Position = new Vector3((float)(wx - X), y, (float)(wz - Z));
        _anchored.Add(new WeakReference<Node3D>(node));
    }

    /// <summary>Sets the origin outright (new game, teleport) without notifying listeners: callers rebuild.</summary>
    public static void Reset(double x, double z)
    {
        X = Math.Round(x / Snap) * Snap;
        Z = Math.Round(z / Snap) * Snap;
        _anchored.Clear();
        UpdateShader();
    }

    /// <summary>Jumps the origin if the focus (world coordinates) is too far from it. Returns true after a jump.</summary>
    public static bool Follow(double fx, double fz)
    {
        if (Math.Abs(fx - X) < RebaseDistance && Math.Abs(fz - Z) < RebaseDistance) return false;
        double nx = Math.Round(fx / Snap) * Snap, nz = Math.Round(fz / Snap) * Snap;
        double dx = nx - X, dz = nz - Z;
        X = nx; Z = nz;
        for (int i = _anchored.Count - 1; i >= 0; i--)
        {
            if (!_anchored[i].TryGetTarget(out var node) || !GodotObject.IsInstanceValid(node))
            {
                _anchored.RemoveAt(i);
                continue;
            }
            double wx = node.GetMeta("wx").AsDouble(), wz = node.GetMeta("wz").AsDouble();
            node.Position = new Vector3((float)(wx - X), node.Position.Y, (float)(wz - Z));
        }
        UpdateShader();
        Shifted?.Invoke(dx, dz);
        return true;
    }

    private static void UpdateShader()
    {
        static double Mod(double v) => v - Math.Floor(v / ShaderPeriod) * ShaderPeriod;
        RenderingServer.GlobalShaderParameterSet("world_offset", new Vector2((float)Mod(X), (float)Mod(Z)));
    }
}
