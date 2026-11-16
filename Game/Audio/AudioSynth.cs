using System;
using Godot;

namespace BalticDrive.Audio;

/// <summary>Generated placeholder sounds for the world (D53), in the spirit of the guns' (D40).</summary>
public static class AudioSynth
{
    public const int Rate = 22050;

    public static AudioStreamWav Wav(float[] x, bool loop = false)
    {
        var bytes = new byte[x.Length * 2];
        for (int i = 0; i < x.Length; i++)
        {
            short v = (short)Math.Clamp(x[i] * 32000f, -32768f, 32767f);
            bytes[i * 2] = (byte)(v & 0xff); bytes[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
        var w = new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = Rate, Stereo = false, Data = bytes };
        if (loop) { w.LoopMode = AudioStreamWav.LoopModeEnum.Forward; w.LoopBegin = 0; w.LoopEnd = x.Length; }
        return w;
    }

    /// <summary>
    /// A shot close by: the guns' body (crack, thump, tail; D40) with the snap a close shot starts with, a few
    /// milliseconds of bright, unfiltered pressure wave. Without it the shot sounded far away (Tom).
    /// </summary>
    public static AudioStreamWav NearShot(float boom, float pitch)
    {
        var body = OnFoot.Synth.Shot(boom, pitch);
        var data = body.Data;
        int n = data.Length / 2;
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (short)(data[i * 2] | (data[i * 2 + 1] << 8)) / 32000f;
        var rng = new Random(41);
        float prev = 0;
        int snap = (int)(Rate * 0.006f);
        for (int i = 0; i < snap && i < n; i++)
        {
            float t = i / (float)snap;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            float hp = noise - prev; prev = noise;        // high-passed: all edge
            float nwave = t < 0.5f ? 1f - 4f * t : -1f + 2f * (t - 0.5f) * 2f;   // the N-shaped pressure jump
            x[i] = x[i] * 0.6f + (nwave * 0.7f + hp * 0.5f) * (1f - t * 0.6f);
        }
        for (int i = 0; i < n; i++) x[i] *= 0.85f;
        return Wav(x);
    }

    /// <summary>A shot heard from far away: the crack is gone (air eats the highs), a low boom rolls on.</summary>
    public static AudioStreamWav FarShot(float boom)
    {
        var rng = new Random(13);
        int n = (int)(Rate * (1.2f + boom));
        var x = new float[n];
        float lp = 0, lp2 = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            lp += (noise - lp) * 0.06f;
            lp2 += (lp - lp2) * 0.08f;
            float thump = MathF.Sin(MathF.Tau * (42f + 20f * MathF.Exp(-t * 18f)) * t) * MathF.Exp(-t * 9f) * boom;
            float roll = lp2 * 9f * MathF.Exp(-t * (2.2f / (0.6f + boom))) * (0.6f + 0.4f * MathF.Sin(t * 13f));
            x[i] = (thump * 0.7f + roll) * MathF.Min(1f, t * 400f);
        }
        return Wav(x);
    }

    /// <summary>A shot's slap coming back off a wall: short, dull, a little smeared.</summary>
    public static AudioStreamWav Echo()
    {
        var rng = new Random(17);
        int n = (int)(Rate * 0.45f);
        var x = new float[n];
        float lp = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            // a slap, not a thud: brighter and shorter than the first try (Tom heard it as muffled)
            lp += ((float)(rng.NextDouble() * 2 - 1) - lp) * 0.35f;
            x[i] = (lp * 1.8f * MathF.Exp(-t * 16f) + MathF.Sin(MathF.Tau * 90f * t) * MathF.Exp(-t * 20f) * 0.3f) * MathF.Min(1f, t * 900f);
        }
        return Wav(x);
    }

    /// <summary>The long roll of a shot over open country or between blocks far apart.</summary>
    public static AudioStreamWav Roll()
    {
        var rng = new Random(19);
        int n = (int)(Rate * 2.4f);
        var x = new float[n];
        float lp = 0, lp2 = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            lp += ((float)(rng.NextDouble() * 2 - 1) - lp) * 0.05f;
            lp2 += (lp - lp2) * 0.1f;
            float env = MathF.Min(1f, t * 6f) * MathF.Exp(-t * 1.6f);
            x[i] = lp2 * 10f * env * (0.7f + 0.3f * MathF.Sin(t * 7f + MathF.Sin(t * 3f)));
        }
        return Wav(x);
    }

    /// <summary>A city far and near, as a loop: deep traffic rumble with the odd swell.</summary>
    public static AudioStreamWav City()
    {
        var rng = new Random(23);
        int n = Rate * 8;
        var x = new float[n];
        float lp = 0, lp2 = 0, mid = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            float noise = (float)(rng.NextDouble() * 2 - 1);
            lp += (noise - lp) * 0.015f;
            lp2 += (lp - lp2) * 0.04f;
            mid += (noise - mid) * 0.12f;
            float swell = 0.75f + 0.25f * MathF.Sin(MathF.Tau * t / 8f) * MathF.Sin(MathF.Tau * t / 2.7f);
            x[i] = (lp2 * 14f + mid * 0.05f) * swell;
        }
        return Loop(x);
    }

    /// <summary>Wind as a loop: soft noise rising and falling in gusts.</summary>
    public static AudioStreamWav Wind()
    {
        var rng = new Random(29);
        int n = Rate * 10;
        var x = new float[n];
        float lp = 0, lp2 = 0;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)Rate;
            lp += ((float)(rng.NextDouble() * 2 - 1) - lp) * 0.04f;
            lp2 += (lp - lp2) * 0.3f;
            float gust = 0.55f + 0.45f * MathF.Sin(MathF.Tau * t / 10f * 2f) * MathF.Sin(MathF.Tau * t / 10f * 3f + 1f);
            x[i] = lp2 * 6f * gust;
        }
        return Loop(x);
    }

    /// <summary>Tyres on asphalt as a loop: a steady hiss with a low hum under it.</summary>
    public static AudioStreamWav Tyres()
    {
        var rng = new Random(31);
        int n = Rate * 4;
        var x = new float[n];
        float lp = 0, bp = 0;
        for (int i = 0; i < n; i++)
        {
            float noise = (float)(rng.NextDouble() * 2 - 1);
            lp += (noise - lp) * 0.08f;
            bp += (lp - bp) * 0.02f;
            x[i] = (lp - bp) * 2.2f + bp * 3f;
        }
        return Loop(x);
    }

    /// <summary>One small bird's phrase: a few quick chirps, each a falling or rising whistle.</summary>
    public static AudioStreamWav Bird(int seed)
    {
        var rng = new Random(seed);
        int chirps = 2 + rng.Next(5);
        float gap = 0.06f + (float)rng.NextDouble() * 0.1f, len = 0.05f + (float)rng.NextDouble() * 0.07f;
        float f0 = 2800f + (float)rng.NextDouble() * 2400f, sweep = ((float)rng.NextDouble() - 0.5f) * 2600f;
        int n = (int)(Rate * (chirps * (gap + len) + 0.1f));
        var x = new float[n];
        for (int c = 0; c < chirps; c++)
        {
            int s0 = (int)(Rate * c * (gap + len));
            float phase = 0, fc = f0 * (1f + ((float)rng.NextDouble() - 0.5f) * 0.15f);
            for (int i = 0; i < Rate * len && s0 + i < n; i++)
            {
                float u = i / (Rate * len);
                phase += MathF.Tau * (fc + sweep * u) / Rate;
                x[s0 + i] += MathF.Sin(phase) * MathF.Sin(MathF.PI * u) * 0.35f;
            }
        }
        return Wav(x);
    }

    /// <summary>A loop with its ends faded into each other, so it doesn't click where it repeats.</summary>
    private static AudioStreamWav Loop(float[] x)
    {
        int f = Rate / 2;
        for (int i = 0; i < f; i++)
        {
            float a = i / (float)f;
            x[i] = x[i] * a + x[x.Length - f + i] * (1 - a);
        }
        Array.Resize(ref x, x.Length - f);
        return Wav(x, loop: true);
    }
}
