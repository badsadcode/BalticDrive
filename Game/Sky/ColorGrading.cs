using System;
using Godot;

namespace BalticDrive.Sky;

/// <summary>
/// Colour grading presets (D192; Tom and Noor, docs/ATMOSPHERE_IDEAS_BACKLOG.md: "Natural, Cinematic, Fantasy, Dreamy,
/// Rustic, Dark fantasy, Vintage, Vibrant"). Each preset is a 3D colour lookup table (32³) built here from a few
/// grading controls and applied as the environment's colour correction (after tone mapping, on display colours).
/// Natural uses none: the game as before. Strength blends each preset with the identity.
/// </summary>
public static class ColorGrading
{
    public static readonly string[] Names = { "Natural", "Cinematic", "Fantasy", "Dreamy", "Rustic", "Dark fantasy", "Vintage", "Vibrant" };

    /// <summary>The grading controls of a preset.</summary>
    private readonly record struct Grade(
        float Temperature,      // − cool … + warm
        float Tint,             // − green … + magenta
        float Saturation,       // 1 = unchanged
        float Contrast,         // 1 = unchanged (around mid grey)
        float Lift,             // raises the blacks (faded look)
        float Gain,             // highlights
        Color Shadows,          // split toning: shadow colour (mixed by ShadowAmount)
        float ShadowAmount,
        Color Highlights,
        float HighlightAmount,
        float GreenShift);      // − pulls greens toward yellow-brown (rustic)

    private static Grade For(int preset) => preset switch
    {
        1 => new(.05f, 0, 1.05f, 1.12f, 0, 1.02f, new Color(.1f, .35f, .45f), .12f, new Color(1f, .75f, .5f), .1f, 0),       // Cinematic: teal shadows, warm highlights
        2 => new(.06f, .03f, 1.25f, 1.06f, .02f, 1.04f, new Color(.4f, .25f, .6f), .12f, new Color(1f, .9f, .6f), .1f, 0),    // Fantasy: rich, violet shadows, golden light
        3 => new(.06f, .04f, .85f, .88f, .08f, 1.0f, new Color(.55f, .5f, .7f), .1f, new Color(1f, .9f, .85f), .12f, 0),     // Dreamy: soft, lifted, pastel
        4 => new(.12f, -.01f, .82f, 1.05f, .03f, 1.0f, new Color(.35f, .25f, .15f), .14f, new Color(1f, .85f, .6f), .08f, -.35f), // Rustic: warm, earthy greens
        5 => new(-.06f, .0f, .7f, 1.18f, -.02f, .96f, new Color(.12f, .18f, .28f), .18f, new Color(.85f, .82f, .75f), .05f, 0), // Dark fantasy: cold, muted, deep
        6 => new(.1f, .02f, .75f, .9f, .07f, .97f, new Color(.3f, .3f, .2f), .1f, new Color(1f, .9f, .7f), .12f, -.15f),     // Vintage: faded, yellowed
        7 => new(.02f, 0, 1.35f, 1.12f, 0, 1.03f, new Color(.2f, .3f, .5f), .05f, new Color(1f, .95f, .85f), .05f, 0),      // Vibrant: saturated, punchy
        _ => new(0, 0, 1, 1, 0, 1, Colors.Black, 0, Colors.White, 0, 0),
    };

    private const int N = 32;
    private static readonly ImageTexture3D?[] Cache = new ImageTexture3D?[Names.Length * 11];

    /// <summary>The lookup table for a preset at a strength (0..1, cached in tenths), or null for none.</summary>
    public static Texture3D? Lut(int preset, float strength)
    {
        if (preset <= 0 || preset >= Names.Length || strength <= .001f) return null;
        int s = (int)Math.Round(Math.Clamp(strength, 0, 1) * 10);
        if (s == 0) return null;
        int key = preset * 11 + s;
        return Cache[key] ??= Build(For(preset), s / 10f);
    }

    private static ImageTexture3D Build(Grade g, float k)
    {
        var slices = new Godot.Collections.Array<Image>();
        for (int b = 0; b < N; b++)
        {
            var bytes = new byte[N * N * 3];
            for (int gr = 0; gr < N; gr++)
                for (int r = 0; r < N; r++)
                {
                    var c = new Vector3(r, gr, b) / (N - 1);
                    var o = c.Lerp(Apply(g, c), k);
                    int i = (gr * N + r) * 3;
                    bytes[i] = (byte)Math.Round(Math.Clamp(o.X, 0, 1) * 255);
                    bytes[i + 1] = (byte)Math.Round(Math.Clamp(o.Y, 0, 1) * 255);
                    bytes[i + 2] = (byte)Math.Round(Math.Clamp(o.Z, 0, 1) * 255);
                }
            slices.Add(Image.CreateFromData(N, N, false, Image.Format.Rgb8, bytes));
        }
        var tex = new ImageTexture3D();
        tex.Create(Image.Format.Rgb8, N, N, N, false, slices);
        return tex;
    }

    private static float Luma(Vector3 c) => c.X * .2126f + c.Y * .7152f + c.Z * .0722f;

    private static Vector3 Apply(Grade g, Vector3 c)
    {
        // White balance: temperature (red ↔ blue) and tint (green ↔ magenta).
        c *= new Vector3(1 + g.Temperature, 1 - g.Tint * .5f, 1 - g.Temperature) + new Vector3(g.Tint * .25f, 0, g.Tint * .25f);
        // Greens toward yellow-brown (rustic, vintage): move the green channel's excess over red and blue toward red.
        float excess = Math.Max(0, c.Y - Math.Max(c.X, c.Z));
        c += new Vector3(-g.GreenShift * excess * .6f, g.GreenShift * excess * .5f, 0);
        // Lift / gain, contrast around mid grey (an S-curve-ish linear slope).
        c = new Vector3(g.Lift, g.Lift, g.Lift) + c * (g.Gain - g.Lift);
        c = (c - new Vector3(.45f, .45f, .45f)) * g.Contrast + new Vector3(.45f, .45f, .45f);
        // Saturation around the luma.
        float l = Luma(c);
        c = new Vector3(l, l, l) + (c - new Vector3(l, l, l)) * g.Saturation;
        // Split toning: shadows and highlights tinted (multiplied toward the tone's colour, luma kept).
        l = Math.Clamp(Luma(c), 0, 1);
        float sh = (1 - Mathf.SmoothStep(0, .55f, l)) * g.ShadowAmount, hi = Mathf.SmoothStep(.45f, 1, l) * g.HighlightAmount;
        Vector3 Tone(Color t) { var v = new Vector3(t.R, t.G, t.B); return v / Math.Max(Luma(v), .01f); }
        c = c.Lerp(c * Tone(g.Shadows), sh);
        c = c.Lerp(c * Tone(g.Highlights), hi);
        return c;
    }
}
