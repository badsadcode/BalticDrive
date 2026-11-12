using System;
using System.Collections.Generic;

namespace BalticDrive.Core;

/// <summary>Stateless integer hashing used for all deterministic decisions (from Dead Miles).</summary>
public static class Hash
{
    public static uint Mix(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352dU;
        x ^= x >> 15; x *= 0x846ca68bU;
        x ^= x >> 16;
        return x;
    }

    public static uint Of(int a, int b = 0, int c = 0, int d = 0)
    {
        uint h = Mix((uint)a + 0x9e3779b9U);
        h = Mix(h ^ ((uint)b * 0x85ebca6bU + 0x01234567U));
        h = Mix(h ^ ((uint)c * 0xc2b2ae35U + 0x89abcdefU));
        h = Mix(h ^ ((uint)d * 0x27d4eb2fU + 0x3c6ef372U));
        return h;
    }

    public static float F01(uint h) => (h >> 8) * (1f / 16777216f);
}

/// <summary>Small deterministic RNG (xorshift32). Always seed it from <see cref="Hash"/>.</summary>
public struct Rng
{
    private uint _s;
    public Rng(uint seed) { _s = seed == 0 ? 0x1234abcdU : seed; }
    public uint NextU() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return _s; }
    public float F() => (NextU() >> 8) * (1f / 16777216f);
    public float Range(float a, float b) => a + (b - a) * F();
    public int Int(int n) => n <= 0 ? 0 : (int)(NextU() % (uint)n);
    public bool Chance(float p) => F() < p;
    public T Pick<T>(IReadOnlyList<T> list) => list[Int(list.Count)];
}
