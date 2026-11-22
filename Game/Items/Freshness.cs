using System;
using BalticDrive.Core;

namespace BalticDrive.Items;

/// <summary>
/// Food going off (D115): an item with <c>fresh</c> days in items.json is fresh that long, then stale for its
/// <c>stale</c> days, then rotten. Days are <see cref="WorldClock"/> days (an hour played each), sped up or slowed by
/// the sandbox's <c>food.spoil</c> (0: nothing spoils). Each stack knows when it was made (<see cref="ItemStack.Made"/>).
/// Stale food fills you less; rotten barely, and makes you ill; recipes won't take rotten things.
/// </summary>
public static class Freshness
{
    public const int None = 0, Fresh = 1, Stale = 2, Rotten = 3;
    /// <summary>Two stacks of one item made this close together (seconds) are one stack.</summary>
    public const double MergeSeconds = 900;

    public static bool Perishable(ItemDef? d) => d is { Fresh: > 0 };

    /// <summary>How many food days old, by the sandbox's speed.</summary>
    public static double AgeDays(double made) => made < 0 ? 0 : Math.Max(0, WorldClock.Seconds - made) / WorldClock.Day * Sandbox.Get("food.spoil");

    public static int Stage(ItemDef? d, double made)
    {
        if (!Perishable(d) || made < 0) return None;
        double age = AgeDays(made);
        return age <= d!.Fresh ? Fresh : age <= d.Fresh + d.Stale ? Stale : Rotten;
    }

    public static int Stage(ItemStack s) => Stage(ItemDb.Get(s.Id), s.Made);
    public static bool IsRotten(ItemStack s) => Stage(s) == Rotten;

    public static string Word(int stage) => stage switch { Stale => "stale", Rotten => "rotten", _ => "" };

    /// <summary>× on what it fills.</summary>
    public static float FoodFactor(int stage) => stage switch { Stale => 0.6f, Rotten => 0.25f, _ => 1f };

    /// <summary>When something found in a cupboard was made (D115): anything from just fresh to well past rotten,
    /// 312 days into it all.</summary>
    public static double FoundMade(ItemDef d, Random rng)
    {
        double spoil = Sandbox.Get("food.spoil");
        if (!Perishable(d)) return -1;
        if (spoil <= 0) return WorldClock.Seconds;
        double days = rng.NextDouble() * (d.Fresh + d.Stale) * 1.35;
        return WorldClock.Seconds - days / spoil * WorldClock.Day;
    }
}
