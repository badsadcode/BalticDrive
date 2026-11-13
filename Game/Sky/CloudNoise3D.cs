using System;
using System.Threading.Tasks;
using Godot;

namespace BalticDrive.Sky;

/// <summary>
/// A tiling 3D noise texture for the volumetric clouds (round 18; Tom: "they look flat… made of spheres, very regular").
/// Channels, every one periodic over the cube so the clouds tile seamlessly:
///   R — Perlin-Worley: soft value-noise fbm pushed into billows by inverted Worley cells (the classic base for
///       cumulus: rounded lumps that join smoothly, no repeating rows);
///   G, B, A — inverted Worley fbm at 4, 8 and 16 cells per side (lumps on lumps on lumps: the cauliflower detail that
///       eats into the cloud's edges).
/// About 0.2 s for 64³ on start-up; deterministic.
/// </summary>
public partial class CloudNoise3D : RefCounted
{
    /// <summary>For GDScript test harnesses: <c>CloudNoise3D.new().Build(64)</c>.</summary>
    public ImageTexture3D Build(int size) => Create(size);
    /// <summary>For harnesses: the game's 2D cloud map (Atmosphere), with mipmaps.</summary>
    public ImageTexture Build2D(int size) => ImageTexture.CreateFromImage(Atmosphere.WithMipmaps(Atmosphere.CloudNoise(size, 4242)));

    public static ImageTexture3D Create(int size = 64, uint seed = 9173)
    {
        var data = new byte[size * size * size * 4];
        Parallel.For(0, size, z =>
        {
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size, v = y / (float)size, w = z / (float)size;
                    float w4 = WorleyFbm(u, v, w, 4, seed), w8 = WorleyFbm(u, v, w, 8, seed + 11), w16 = WorleyFbm(u, v, w, 16, seed + 23);
                    float perlin = ValueFbm(u, v, w, 4, 4, seed + 31);
                    // Perlin-Worley: the fbm, remapped so Worley cells carve billows into it.
                    float pw = Math.Clamp(Remap(perlin, 0, 1, w4 * .85f - .15f, 1), 0, 1);
                    int i = ((z * size + y) * size + x) * 4;
                    data[i] = (byte)(pw * 255); data[i + 1] = (byte)(w4 * 255); data[i + 2] = (byte)(w8 * 255); data[i + 3] = (byte)(w16 * 255);
                }
        });
        var slices = new Godot.Collections.Array<Image>();
        int slice = size * size * 4;
        for (int z = 0; z < size; z++)
        {
            var bytes = new byte[slice];
            Array.Copy(data, z * slice, bytes, 0, slice);
            slices.Add(Image.CreateFromData(size, size, false, Image.Format.Rgba8, bytes));
        }
        var tex = new ImageTexture3D();
        tex.Create(Image.Format.Rgba8, size, size, size, false, slices);
        return tex;
    }

    /// <summary>
    /// Finer detail noise (D199, Tom: the clouds "look posterized", flat lobes): 128³, channels R/G/B = inverted Worley fbm
    /// at 8, 16 and 32 cells per side (lumps on lumps, four times finer than <see cref="Create"/>'s detail), A = a value
    /// fbm for soft variation. ~1–2 s on start-up, only while the Cloud detail setting asks for it.
    /// </summary>
    public static ImageTexture3D CreateDetail(int size = 128, uint seed = 5521)
    {
        var data = new byte[size * size * size * 4];
        Parallel.For(0, size, z =>
        {
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size, v = y / (float)size, w = z / (float)size;
                    int i = ((z * size + y) * size + x) * 4;
                    data[i] = (byte)(WorleyFbm(u, v, w, 8, seed) * 255);
                    data[i + 1] = (byte)(WorleyFbm(u, v, w, 16, seed + 11) * 255);
                    data[i + 2] = (byte)(Worley(u, v, w, 32, seed + 23) * 255);
                    data[i + 3] = (byte)(ValueFbm(u, v, w, 8, 4, seed + 31) * 255);
                }
        });
        var slices = new Godot.Collections.Array<Image>();
        int slice = size * size * 4;
        for (int z = 0; z < size; z++)
        {
            var bytes = new byte[slice];
            Array.Copy(data, z * slice, bytes, 0, slice);
            slices.Add(Image.CreateFromData(size, size, false, Image.Format.Rgba8, bytes));
        }
        var tex = new ImageTexture3D();
        tex.Create(Image.Format.Rgba8, size, size, size, false, slices);
        return tex;
    }

    private static float Remap(float v, float a, float b, float c, float d) => c + (v - a) / Math.Max(b - a, 1e-4f) * (d - c);

    private static uint Hash(int x, int y, int z, uint salt)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)z * 2147483647u + salt * 2246822519u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return h ^ (h >> 16);
    }

    /// <summary>Inverted Worley (1 at cell points, 0 between): three octaves, periodic over the unit cube.</summary>
    private static float WorleyFbm(float u, float v, float w, int cells, uint salt)
        => Worley(u, v, w, cells, salt) * .625f + Worley(u, v, w, cells * 2, salt + 1) * .25f + Worley(u, v, w, cells * 4, salt + 2) * .125f;

    private static float Worley(float u, float v, float w, int cells, uint salt)
    {
        float x = u * cells, y = v * cells, z = w * cells;
        int cx = (int)MathF.Floor(x), cy = (int)MathF.Floor(y), cz = (int)MathF.Floor(z);
        float best = 9;
        for (int dz = -1; dz <= 1; dz++)
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int ix = cx + dx, iy = cy + dy, iz = cz + dz;
                    // Wrap the cell index (periodic), not the position.
                    int wx = ((ix % cells) + cells) % cells, wy = ((iy % cells) + cells) % cells, wz = ((iz % cells) + cells) % cells;
                    uint h = Hash(wx, wy, wz, salt);
                    float px = ix + (h & 1023) / 1023f, py = iy + ((h >> 10) & 1023) / 1023f, pz = iz + ((h >> 20) & 1023) / 1023f;
                    float d = (px - x) * (px - x) + (py - y) * (py - y) + (pz - z) * (pz - z);
                    if (d < best) best = d;
                }
        return Math.Clamp(1 - MathF.Sqrt(best), 0, 1);
    }

    private static float ValueFbm(float u, float v, float w, int period, int octaves, uint salt)
    {
        float sum = 0, amp = 1, norm = 0;
        for (int o = 0; o < octaves; o++) { sum += amp * Value(u, v, w, period << o, salt + (uint)o); norm += amp; amp *= .5f; }
        return sum / norm;
    }

    private static float Value(float u, float v, float w, int period, uint salt)
    {
        float x = u * period, y = v * period, z = w * period;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y), z0 = (int)MathF.Floor(z);
        float fx = x - x0, fy = y - y0, fz = z - z0;
        fx = fx * fx * (3 - 2 * fx); fy = fy * fy * (3 - 2 * fy); fz = fz * fz * (3 - 2 * fz);
        float L(int a, int b, int c) => (Hash(((a % period) + period) % period, ((b % period) + period) % period, ((c % period) + period) % period, salt) & 65535) / 65535f;
        float x00 = L(x0, y0, z0) + (L(x0 + 1, y0, z0) - L(x0, y0, z0)) * fx;
        float x10 = L(x0, y0 + 1, z0) + (L(x0 + 1, y0 + 1, z0) - L(x0, y0 + 1, z0)) * fx;
        float x01 = L(x0, y0, z0 + 1) + (L(x0 + 1, y0, z0 + 1) - L(x0, y0, z0 + 1)) * fx;
        float x11 = L(x0, y0 + 1, z0 + 1) + (L(x0 + 1, y0 + 1, z0 + 1) - L(x0, y0 + 1, z0 + 1)) * fx;
        float y0v = x00 + (x10 - x00) * fy, y1v = x01 + (x11 - x01) * fy;
        return y0v + (y1v - y0v) * fz;
    }
}
