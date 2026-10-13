using System.Runtime.InteropServices;

namespace Progenitor.Content;

/// <summary>
/// Server-authored equipment bonuses for an already received item definition.
/// Reliable ordering places this after PrfD on initial sync and definition updates.
/// Requires matching Core support on both peers; the pack itself is server-only.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ItemStatPacket
{
    internal const int EventId = 0x50726653; // PrfS
    internal const int CurrentVersion = 1;
    public int Version;
    public int NewGuid;
    public int SourceGuid;
    public float MaxHealth;
    public float PhysicalPower;
    public float SpellPower;

#if !PROGENITOR_TESTS
    internal static ItemStatPacket From(PrefabDefinition definition)
    {
        var bonuses = definition.StatBonuses ?? ItemStatBonuses.Empty;
        return new ItemStatPacket
        {
            Version = CurrentVersion,
            NewGuid = definition.Guid.GuidHash,
            SourceGuid = definition.Source.GuidHash,
            MaxHealth = bonuses.MaxHealth,
            PhysicalPower = bonuses.PhysicalPower,
            SpellPower = bonuses.SpellPower,
        };
    }
#endif

    internal bool TryBonuses(out ItemStatBonuses bonuses)
    {
        bonuses = ItemStatBonuses.Empty;
        if (Version != CurrentVersion || NewGuid == 0 || SourceGuid == 0 || NewGuid == SourceGuid || !Valid(MaxHealth)
            || !Valid(PhysicalPower) || !Valid(SpellPower))
            return false;
        bonuses = new ItemStatBonuses(MaxHealth, PhysicalPower, SpellPower);
        return true;
    }

    private static bool Valid(float value) => float.IsFinite(value) && value >= -10000 && value <= 10000;
}
