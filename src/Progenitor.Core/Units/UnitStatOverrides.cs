using System;
using System.Globalization;

namespace Progenitor.Units;

/// <summary>
/// Absolute level, maximum health and power for a unit spawned through <c>ctx.Units</c>
/// (<c>ctx.Units.SetStats</c>, <see cref="UnitSpawnOptions.Stats"/>; docs/modules.md, "Unit stats").
/// A null field leaves that stat as it is. <c>ctx.Units.GetStats</c> returns one of these filled with
/// the unit's current values.
/// </summary>
public sealed class UnitStatOverrides
{
    /// <summary>Lowest accepted <see cref="Level"/>.</summary>
    public const int MinLevel = 1;

    /// <summary>Highest accepted <see cref="Level"/>.</summary>
    public const int MaxLevel = 120;

    /// <summary>Highest accepted <see cref="MaxHealth"/>; it must also be above 0.</summary>
    public const float MaxMaxHealth = 10_000_000f;

    /// <summary>Highest accepted <see cref="PhysicalPower"/> and <see cref="SpellPower"/>; they may be 0.</summary>
    public const float MaxPower = 10_000f;

    /// <summary>
    /// <c>UnitLevel.Level</c>: the level shown over the unit and used by the game's level-difference
    /// damage scaling. It does not recompute power or health; set those too. 1 to 120.
    /// </summary>
    public int? Level { get; set; }

    /// <summary>
    /// <c>Health.MaxHealth</c>. Setting it also fills current health to the new maximum; refused while
    /// the unit is dead or downed. Above 0, at most 10,000,000.
    /// </summary>
    public float? MaxHealth { get; set; }

    /// <summary><c>UnitStats.PhysicalPower</c>, which scales the unit's physical damage. 0 to 10,000.</summary>
    public float? PhysicalPower { get; set; }

    /// <summary><c>UnitStats.SpellPower</c>, which scales the unit's spell damage. 0 to 10,000.</summary>
    public float? SpellPower { get; set; }

    /// <summary>True when every field is null (nothing to change).</summary>
    public bool IsEmpty => Level == null && MaxHealth == null && PhysicalPower == null && SpellPower == null;

    /// <summary>Why these values would be refused, or null when every set field is in range.</summary>
    public string? Refusal()
    {
        if (Level is int level && (level < MinLevel || level > MaxLevel))
            return "level " + level.ToString(CultureInfo.InvariantCulture) + " is outside "
                + MinLevel.ToString(CultureInfo.InvariantCulture) + " to " + MaxLevel.ToString(CultureInfo.InvariantCulture) + ".";
        if (MaxHealth is float health && (!IsFinite(health) || health <= 0f || health > MaxMaxHealth))
            return "max health " + F(health) + " must be above 0 and at most " + F(MaxMaxHealth) + ".";
        if (PhysicalPower is float physical && !IsPower(physical))
            return "physical power " + F(physical) + " is outside 0 to " + F(MaxPower) + ".";
        if (SpellPower is float spell && !IsPower(spell))
            return "spell power " + F(spell) + " is outside 0 to " + F(MaxPower) + ".";
        return null;
    }

    /// <summary>Throw <see cref="ArgumentOutOfRangeException"/> when <see cref="Refusal"/> names a problem.</summary>
    public void Validate()
    {
        if (Refusal() is { } why) throw new ArgumentOutOfRangeException(nameof(UnitStatOverrides), why);
    }

    /// <summary>A copy, so a caller changing its object later does not change a pending request.</summary>
    public UnitStatOverrides Clone() => new()
    {
        Level = Level,
        MaxHealth = MaxHealth,
        PhysicalPower = PhysicalPower,
        SpellPower = SpellPower,
    };

    /// <inheritdoc />
    public override string ToString() =>
        "level=" + (Level?.ToString(CultureInfo.InvariantCulture) ?? "-")
        + " maxHealth=" + (MaxHealth is float h ? F(h) : "-")
        + " physicalPower=" + (PhysicalPower is float p ? F(p) : "-")
        + " spellPower=" + (SpellPower is float s ? F(s) : "-");

    private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

    private static bool IsPower(float v) => IsFinite(v) && v >= 0f && v <= MaxPower;

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
