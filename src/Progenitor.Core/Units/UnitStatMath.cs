using System;
using System.Globalization;

namespace Progenitor.Units;

// Game-free half of ctx.Units.SetStats (docs/modules.md, "Unit stats"). The live half
// (UnitStatsBody.cs) reads Health, UnitStats and UnitLevel off the unit, asks this class what to
// write, and writes it.
//
// Two facts shape it:
// - The spawn computation fills Health and the powers from the unit's UnitSpawnData settings some
//   frames after Instantiate; a fresh instance (and its prefab) reads MaxHealth 0. A write made
//   before that is overwritten, so a request waits until MaxHealth reads above 0.
// - The game layers temporary modifiers onto ModifiableFloat / ModifiableInt values in place
//   (live: Movement.Speed 7 read 14 during a movement effect, then 7 again). When the value on the
//   unit is not the one this API last wrote, a modifier is assumed and the new target is applied
//   relative to it, as UnitMoveSpeedMath does for speed, so the game's own removal lands on the target.

/// <summary>The stats <see cref="UnitStatOverrides"/> names, in slot order.</summary>
internal enum UnitStatKind
{
    Level = 0,
    MaxHealth = 1,
    PhysicalPower = 2,
    SpellPower = 3,
}

/// <summary>Bookkeeping for one stat of one unit.</summary>
internal sealed class UnitStatSlot
{
    /// <summary>Value asked for and not yet written (the unit was not ready).</summary>
    public float? Requested { get; set; }

    /// <summary>Value last written as the stat's base, or null when this API never wrote it.</summary>
    public float? Applied { get; set; }

    /// <summary>What the first write replaced: the game's own value, used to spot a later reset.</summary>
    public float? Displaced { get; set; }
}

/// <summary>Per-unit bookkeeping for <c>SetStats</c>, one slot per <see cref="UnitStatKind"/>.</summary>
internal sealed class UnitStatState
{
    private readonly UnitStatSlot[] _slots = { new(), new(), new(), new() };

    public UnitStatSlot this[UnitStatKind kind] => _slots[(int)kind];

    public bool AnyPending
    {
        get
        {
            foreach (var slot in _slots) if (slot.Requested != null) return true;
            return false;
        }
    }

    public bool AnyApplied
    {
        get
        {
            foreach (var slot in _slots) if (slot.Applied != null) return true;
            return false;
        }
    }

    /// <summary>Record a request. Set fields replace an earlier pending value; null fields keep it.</summary>
    public void Request(UnitStatOverrides stats)
    {
        if (stats == null) throw new ArgumentNullException(nameof(stats));
        if (stats.Level is int level) this[UnitStatKind.Level].Requested = level;
        if (stats.MaxHealth is float health) this[UnitStatKind.MaxHealth].Requested = health;
        if (stats.PhysicalPower is float physical) this[UnitStatKind.PhysicalPower].Requested = physical;
        if (stats.SpellPower is float spell) this[UnitStatKind.SpellPower].Requested = spell;
    }
}

internal static class UnitStatMath
{
    /// <summary>Relative tolerance for "this is the value this API wrote".</summary>
    public const float Tolerance = 1e-4f;

    public static bool Near(float a, float b) => Math.Abs(a - b) <= Tolerance * Math.Max(1f, Math.Abs(b));

    /// <summary>
    /// True once the spawn computation has filled the unit's health: <c>Health.MaxHealth</c> reads
    /// above 0 (a fresh instance reads 0, as its prefab does).
    /// </summary>
    public static bool SpawnComputed(float maxHealth) => !float.IsNaN(maxHealth) && !float.IsInfinity(maxHealth) && maxHealth > 0f;

    /// <summary>
    /// Why a max-health write (which fills current health) is refused, or null. Dead is
    /// <c>Health.IsDead</c> or a <c>Dead</c> component; downed is current health at or below 0 once
    /// the spawn computation has run.
    /// </summary>
    public static string? DeadOrDownedRefusal(string unit, bool isDead, bool hasDeadComponent, float value, float maxHealth)
    {
        if (isDead || hasDeadComponent)
            return "unit " + unit + " is dead; setting MaxHealth would revive it, so it was refused.";
        if (SpawnComputed(maxHealth) && !(value > 0f))
            return "unit " + unit + " is downed (health " + F(value) + "); setting MaxHealth would revive it, so it was refused.";
        return null;
    }

    /// <summary>
    /// The value to write so the stat's base becomes <paramref name="requested"/>.
    /// <list type="bullet">
    /// <item>Nothing written before, or the unit reads exactly the value last written: write
    /// <paramref name="requested"/>. The first write cannot tell a modifier from the game's base value.</item>
    /// <item>Otherwise a modifier is layered on the value. A float is scaled by requested / applied,
    /// exact for a multiplicative modifier (an additive +a leaves a x (requested / applied - 1) after it
    /// ends). An integer (<see cref="UnitStatKind.Level"/>) is shifted by requested - applied, exact
    /// for an additive modifier.</item>
    /// <item>A current or applied value at or below 0 carries no ratio: write <paramref name="requested"/>.</item>
    /// </list>
    /// </summary>
    public static float PlanWrite(UnitStatKind kind, float current, float? applied, float requested)
    {
        float result;
        if (applied is not float a || float.IsNaN(current) || float.IsInfinity(current) || Near(current, a))
            result = requested;
        else if (kind == UnitStatKind.Level)
            result = current + (requested - a);
        else if (current > 0f && a > 0f)
            result = current * requested / a;
        else
            result = requested;
        if (kind == UnitStatKind.Level) result = Math.Max(UnitStatOverrides.MinLevel, (float)Math.Round(result, MidpointRounding.AwayFromZero));
        return result;
    }

    /// <summary>
    /// The 1 s / 5 s check: a stat that reads exactly what the first write displaced, although a
    /// different value was applied, was reset by the game (a late spawn system); write the applied
    /// value again. A value under a modifier, or already right, is left alone.
    /// </summary>
    public static bool PlanReapply(float current, float applied, float displaced, out float write)
    {
        write = current;
        if (Near(applied, displaced)) return false;
        if (!Near(current, displaced)) return false;
        write = applied;
        return true;
    }

    /// <summary>
    /// Current and recoverable health after max health moved from <paramref name="oldMax"/> to
    /// <paramref name="newMax"/> on a re-apply: a full pool stays full, a partial one keeps its value
    /// capped at the new maximum. (A <c>SetStats</c> call itself fills both to the new maximum.)
    /// </summary>
    public static float HealthAfterReset(float value, float oldMax, float newMax)
    {
        if (!(value > 0f)) return value;
        if (oldMax > 0f && value >= oldMax * (1f - Tolerance)) return newMax;
        return Math.Min(value, newMax);
    }

    public static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
