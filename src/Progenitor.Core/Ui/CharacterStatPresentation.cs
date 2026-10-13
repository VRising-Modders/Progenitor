using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Ui;

/// <summary>Fallback presentation for native character attributes; native UI settings take precedence.</summary>
internal sealed record CharacterStatPresentationRow(
    int NativeId, string Label, string Description, string Format, bool MultiplyBy100, string Category);

/// <summary>
/// Getter-supported attributes with source-checked units. Reading a value does not establish
/// live equipment, timing, or combat qualification. Category factors are totals, not bonuses.
/// </summary>
internal static class CharacterStatPresentation
{
    internal static IReadOnlyList<CharacterStatPresentationRow> Rows { get; } = Array.AsReadOnly(new[]
    {
        Points(1, "Resource Power", "Power used when damaging resources. Resource type and attack modifiers also affect the result.", "Resources"),
        Points(2, "Siege Power", "Power used when damaging structures. Structure rules and siege permissions still apply.", "Resources"),
        Factor(3, "Resource Yield", "Multiplier for resources collected, with 1× as the normal amount. Resource and server rules also affect the amount collected.", "Resources"),
        new CharacterStatPresentationRow(5, "Movement Speed", "Current walking, shapeshift or mount speed in world units per second. Casting and movement restrictions can reduce actual travel speed.", "0.00' units/s'", false, "Movement"),
        Percent(26, "Passive Health Regeneration", "Percentage of maximum health restored per eligible regeneration tick, rather than per second. Requires blood and missing health, and does not run while downed.", "Recovery"),
        Percent(8, "Physical Resistance", "Reduces incoming physical damage by this percentage, up to 100%. Other damage and defensive modifiers also affect the result.", "Defense"),
        Percent(23, "Spell Resistance", "Reduces incoming spell damage by this percentage, up to 100%. Other damage and defensive modifiers also affect the result.", "Defense"),
        Percent(95, "Corruption Resistance", "Reduces incoming corruption damage by this percentage, up to 100%. Other damage and defensive modifiers also affect the result.", "Defense"),
        Rate(7, "General Cooldown Recovery", "eligible abilities", "Abilities"),
        Rate(91, "Feed Cooldown Recovery", "feeding abilities", "Abilities"),
        Factor(90, "Weapon Skill Power", "Multiplier for eligible physical damage from weapon abilities. 1.1× means 10% more damage before other modifiers.", "Offense"),
        Damage(38, "Physical Damage vs Undead", "physical damage against undead units"),
        Damage(39, "Physical Damage vs Humans", "physical damage against human units"),
        Damage(40, "Damage vs Demons", "physical and spell damage against demon and Shadow V Blood units"),
        Damage(41, "Physical Damage vs Mechanical", "physical damage against mechanical units"),
        Damage(42, "Physical Damage vs Beasts", "physical damage against beast units"),
        Damage(43, "Physical Damage vs Castle Objects", "physical damage against castle objects"),
        Damage(44, "Damage vs Vampires", "physical and spell damage against player and PvE vampire units"),
        Damage(56, "Damage vs V Blood", "physical and spell damage against V Blood targets"),
        Damage(52, "Physical Damage vs Wood", "physical damage against wood resources"),
        Damage(53, "Physical Damage vs Minerals", "physical damage against mineral resources"),
        Percent(58, "Resource Durability Loss Reduction", "Reduces durability loss from gathering resources. Other durability costs are unaffected by this bonus.", "Resources"),
        new CharacterStatPresentationRow(66, "Base Blood Drain", "Base blood consumption in points per second. Server settings and blood-drain modifiers affect final consumption.", "0.00' points/s'", false, "Blood"),
        Percent(92, "Blood Mend Healing", "Increases healing from Blood Mend. 25% adds 25% healing before other healing modifiers.", "Recovery"),
    });

    internal static float ToDisplayValue(CharacterStatPresentationRow row, float nativeValue)
        => row.MultiplyBy100 ? nativeValue * 100f : nativeValue;

    internal static string FormatValue(CharacterStatPresentationRow row, float nativeValue, IFormatProvider? provider = null)
        => ToDisplayValue(row, nativeValue).ToString(row.Format, provider ?? CultureInfo.CurrentCulture);

    private static CharacterStatPresentationRow Points(int id, string label, string description, string category)
        => new(id, label, description, "0.##", false, category);

    private static CharacterStatPresentationRow Percent(int id, string label, string description, string category)
        => new(id, label, description, "0.#'%'", true, category);

    private static CharacterStatPresentationRow Factor(int id, string label, string description, string category)
        => new(id, label, description, "0.##'×'", false, category);

    private static CharacterStatPresentationRow Rate(int id, string label, string scope, string category)
        => Percent(id, label, $"Speeds up cooldown recovery for {scope}. 25% recovery alone makes cooldowns 20% shorter.", category);

    private static CharacterStatPresentationRow Damage(int id, string label, string scope)
        => Factor(id, label, $"Multiplier for eligible {scope}. 1.1× means 10% more damage before other modifiers.", "Offense");
}
