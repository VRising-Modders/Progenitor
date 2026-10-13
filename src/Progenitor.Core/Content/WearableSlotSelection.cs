namespace Progenitor.Content;

/// <summary>Selects the item whose appearance owns an equipment slot.</summary>
internal static class WearableSlotSelection
{
    // Client item entities can retain the vanilla donor while the slot carries the clone ID.
    internal static int ResolveGuid(int slotGuid, int itemGuid, bool registeredSlot, bool registeredItem)
        => registeredSlot ? slotGuid : registeredItem ? itemGuid : slotGuid;

    internal static int Select(int statsGuid, bool statsHidden,
        int cosmeticGuid, bool cosmeticOverride, bool cosmeticHidden)
    {
        if (cosmeticOverride) return cosmeticHidden ? 0 : cosmeticGuid;
        return statsHidden ? 0 : statsGuid;
    }
}
