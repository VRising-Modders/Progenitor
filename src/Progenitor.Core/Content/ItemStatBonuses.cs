using System;

namespace Progenitor.Content;

/// <summary>Additive equipment bonuses, in native stat units. Experimental until equip/unequip qualification.</summary>
public sealed class ItemStatBonuses
{
    public static ItemStatBonuses Empty { get; } = new();
    public ItemStatBonuses(float maxHealth = 0, float physicalPower = 0, float spellPower = 0)
    {
        MaxHealth = Validate(maxHealth, nameof(maxHealth));
        PhysicalPower = Validate(physicalPower, nameof(physicalPower));
        SpellPower = Validate(spellPower, nameof(spellPower));
    }
    public float MaxHealth { get; }
    public float PhysicalPower { get; }
    public float SpellPower { get; }
    private static float Validate(float value, string name) => float.IsFinite(value) && value >= -10000 && value <= 10000
        ? value : throw new ArgumentOutOfRangeException(name, "Stat bonuses must be finite and between -10000 and 10000.");
}
