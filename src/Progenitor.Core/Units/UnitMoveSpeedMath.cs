using System;
using System.Globalization;

namespace Progenitor.Units;

// Game-free half of ctx.Units.SetMoveSpeed (docs/modules.md, "Hitbox scale and move speed"). The
// live half (UnitBody.cs) reads Movement.Speed and the four AiMoveSpeeds values off the unit and its
// prefab, asks Plan what to write, and writes it.
//
// Evidence (internal research notes, 2026-09-25 creature customization review, "Hitbox and movement
// speed"): chase speed follows Movement.Speed, and the game layers temporary modifiers on top of the
// written value (7 read 14 during a movement effect, then 7 again). So a multiplier is applied as a
// ratio to the current value, and the factor already baked in is tracked so calls never compound.

/// <summary>What <see cref="UnitMoveSpeedMath.Plan"/> decided for one set of speed values.</summary>
internal enum UnitMoveSpeedOutcome
{
    /// <summary>The requested factor is already the applied one; nothing to write.</summary>
    Unchanged,

    /// <summary>Write the values in the output span where the write flag is set.</summary>
    Write,

    /// <summary>A value that should be scaled is 0 right now (a root or stun); try again later.</summary>
    Deferred,
}

/// <summary>
/// Per-unit bookkeeping: the multiplier the caller asked for and the one currently baked into the
/// unit's speed values. They differ only while an apply is <see cref="UnitMoveSpeedOutcome.Deferred"/>.
/// </summary>
internal sealed class UnitMoveSpeedState
{
    public float Requested { get; set; } = 1f;

    public float Applied { get; set; } = 1f;

    public bool Pending => Requested != Applied;
}

internal static class UnitMoveSpeedMath
{
    /// <summary>Smallest accepted multiplier.</summary>
    public const float MinMultiplier = 0.1f;

    /// <summary>Largest accepted multiplier. Live measurements covered 0.67x to 2.7x of Frostmaw's 4.5 m/s.</summary>
    public const float MaxMultiplier = 4f;

    /// <summary>Relative tolerance for "this value is the prefab's value times the applied factor".</summary>
    public const float Tolerance = 1e-4f;

    public static bool IsValidMultiplier(float multiplier) =>
        !float.IsNaN(multiplier) && !float.IsInfinity(multiplier)
        && multiplier >= MinMultiplier && multiplier <= MaxMultiplier;

    public static string MultiplierRangeMessage(float multiplier) =>
        "move speed multiplier " + multiplier.ToString(CultureInfo.InvariantCulture) + " is outside "
        + MinMultiplier.ToString(CultureInfo.InvariantCulture) + " to " + MaxMultiplier.ToString(CultureInfo.InvariantCulture)
        + " (1 restores the vanilla speed).";

    public static bool Near(float a, float b) => Math.Abs(a - b) <= Tolerance * Math.Max(1f, Math.Abs(b));

    /// <summary>
    /// Plan the write that moves every value from factor <paramref name="applied"/> to
    /// <paramref name="requested"/>. For each index: <paramref name="current"/> is the unit's value,
    /// <paramref name="prefab"/> the prefab's (or a negative number when unknown).
    /// <list type="bullet">
    /// <item>When the unit's value is exactly prefab x applied, no modifier is active and the new value
    /// is prefab x requested: repeated calls are idempotent and float error never accumulates.</item>
    /// <item>Otherwise a modifier is layered on it, and the value is scaled by requested / applied, so
    /// the game's own removal of the modifier lands on the scaled base. Exact for multiplicative
    /// modifiers; an additive modifier +a leaves an error of a x (requested / applied - 1) after it ends.</item>
    /// <item>A value of 0 (or less) whose prefab value is positive cannot carry a ratio: the whole
    /// set is <see cref="UnitMoveSpeedOutcome.Deferred"/> so every value keeps one applied factor.
    /// A value that is 0 on the prefab too, or unreadable, is skipped.</item>
    /// </list>
    /// </summary>
    public static UnitMoveSpeedOutcome Plan(
        ReadOnlySpan<float> current, ReadOnlySpan<float> prefab, float applied, float requested,
        Span<float> write, Span<bool> doWrite)
    {
        if (current.Length != prefab.Length || write.Length < current.Length || doWrite.Length < current.Length)
            throw new ArgumentException("current, prefab, write and doWrite must describe the same values.");
        if (!(applied > 0f)) throw new ArgumentOutOfRangeException(nameof(applied), "the applied factor must be positive.");
        if (!IsValidMultiplier(requested)) throw new ArgumentOutOfRangeException(nameof(requested), MultiplierRangeMessage(requested));

        for (int i = 0; i < current.Length; i++) { doWrite[i] = false; write[i] = current[i]; }
        if (requested == applied) return UnitMoveSpeedOutcome.Unchanged;

        for (int i = 0; i < current.Length; i++)
        {
            float c = current[i];
            if (float.IsNaN(c) || float.IsInfinity(c)) continue;
            if (c <= 0f)
            {
                if (prefab[i] > 0f) return Defer(current, write, doWrite);
                continue;
            }
            float p = prefab[i];
            write[i] = p > 0f && Near(c, p * applied) ? p * requested : c * requested / applied;
            doWrite[i] = true;
        }
        return UnitMoveSpeedOutcome.Write;
    }

    /// <summary>
    /// The 1 s / 5 s check after an apply: a value that reads exactly the prefab's value although a
    /// factor other than 1 is applied was reset by something (a spawn system); write prefab x applied
    /// again. Values under a modifier, or already scaled, are left alone.
    /// </summary>
    public static bool PlanReapply(ReadOnlySpan<float> current, ReadOnlySpan<float> prefab, float applied, Span<float> write, Span<bool> doWrite)
    {
        if (current.Length != prefab.Length || write.Length < current.Length || doWrite.Length < current.Length)
            throw new ArgumentException("current, prefab, write and doWrite must describe the same values.");
        bool any = false;
        for (int i = 0; i < current.Length; i++)
        {
            write[i] = current[i];
            doWrite[i] = false;
            float p = prefab[i];
            if (applied == 1f || !(p > 0f)) continue;
            if (Near(p * applied, p)) continue;
            if (!Near(current[i], p)) continue;
            write[i] = p * applied;
            doWrite[i] = true;
            any = true;
        }
        return any;
    }

    private static UnitMoveSpeedOutcome Defer(ReadOnlySpan<float> current, Span<float> write, Span<bool> doWrite)
    {
        for (int i = 0; i < current.Length; i++) { doWrite[i] = false; write[i] = current[i]; }
        return UnitMoveSpeedOutcome.Deferred;
    }
}
