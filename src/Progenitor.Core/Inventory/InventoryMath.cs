using System;
using System.Collections.Generic;

namespace Progenitor.Inventory;

/// <summary>
/// Game-free slot arithmetic shared by the live inventory backend, the transfer planner and the
/// tests. The rules mirror the game's own server add path (<c>InventoryUtilitiesServer</c> add
/// into an inventory):
/// <list type="bullet">
/// <item><description>A container's <c>RestrictedInventory</c> admits an item when both fields are
/// 0, or when (type is 0 or equals the item) and (category is 0 or shares a flag with the item's
/// <c>ItemData.ItemCategory</c>). The rule is per inventory, not per slot.</description></item>
/// <item><description>A slot's capacity is its <c>MaxAmountOverride</c> when that is above 0,
/// else the item's <c>ItemData.MaxAmount</c>.</description></item>
/// <item><description>The add stacks onto slots that already hold the same item and are below
/// capacity, then fills empty slots (item 0).</description></item>
/// </list>
/// Player bags have one difference this file does not model: the game first skips the eight
/// action-bar slots for stackables, jewels and tech items and only falls back to them later, so
/// <see cref="FreeSpace"/> is exact for chests, stations and hearts and an upper bound for a
/// player bag. Writes read back their result either way.
/// </summary>
internal static class InventoryMath
{
    /// <summary>The game's admission rule for a restricted container.</summary>
    public static bool IsAllowed(InventoryRestriction restriction, int item, long itemCategory)
    {
        if (restriction.IsOpen) return true;
        bool typeOk = restriction.Item == 0 || restriction.Item == item;
        bool categoryOk = restriction.Category == 0 || (restriction.Category & itemCategory) != 0;
        return typeOk && categoryOk;
    }

    /// <summary>Effective capacity of a slot for <paramref name="itemMaxAmount"/>.</summary>
    public static int SlotCapacity(int stackOverride, int itemMaxAmount) =>
        stackOverride > 0 ? stackOverride : Math.Max(0, itemMaxAmount);

    /// <summary>
    /// How many more of <paramref name="item"/> fit, given the slots, the item's stack size and
    /// whether the container admits it. Saturates at <see cref="int.MaxValue"/>.
    /// </summary>
    public static int FreeSpace(IReadOnlyList<InventorySlot> slots, int count, int item, int itemMaxAmount, bool allowed)
    {
        if (!allowed || item == 0 || slots == null) return 0;
        if (count > slots.Count) count = slots.Count;
        long free = 0;
        for (int i = 0; i < count; i++)
        {
            var s = slots[i];
            int capacity = SlotCapacity(s.StackOverride, itemMaxAmount);
            if (s.IsEmpty)
                free += capacity;
            else if (s.Item == item && s.Amount < capacity)
                free += capacity - s.Amount;
        }

        return free > int.MaxValue ? int.MaxValue : (int)free;
    }

    /// <inheritdoc cref="FreeSpace(IReadOnlyList{InventorySlot}, int, int, int, bool)"/>
    public static int FreeSpace(IReadOnlyList<InventorySlot> slots, int item, int itemMaxAmount, bool allowed) =>
        FreeSpace(slots, slots?.Count ?? 0, item, itemMaxAmount, allowed);

    /// <summary>Total of <paramref name="item"/> across the first <paramref name="count"/> slots.</summary>
    public static int Count(IReadOnlyList<InventorySlot> slots, int count, int item)
    {
        if (slots == null || item == 0) return 0;
        if (count > slots.Count) count = slots.Count;
        long total = 0;
        for (int i = 0; i < count; i++)
            if (slots[i].Item == item && slots[i].Amount > 0)
                total += slots[i].Amount;
        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <inheritdoc cref="Count(IReadOnlyList{InventorySlot}, int, int)"/>
    public static int Count(IReadOnlyList<InventorySlot> slots, int item) => Count(slots, slots?.Count ?? 0, item);

    /// <summary>
    /// Content fingerprint: FNV-1a over the slot count and every slot's item and amount. Two
    /// reads of an unchanged inventory give the same value; any item or amount change changes it
    /// (up to 64-bit collisions). Never 0, so 0 can mean "no reading".
    /// </summary>
    public static ulong Fingerprint(IReadOnlyList<InventorySlot> slots, int count)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong h = offset;
        if (slots == null) count = 0;
        else if (count > slots.Count) count = slots.Count;
        h = Mix(h, count, prime);
        for (int i = 0; i < count; i++)
        {
            h = Mix(h, slots![i].Item, prime);
            h = Mix(h, slots[i].Amount, prime);
        }

        return h == 0 ? 1 : h;
    }

    private static ulong Mix(ulong h, int value, ulong prime)
    {
        unchecked
        {
            uint v = (uint)value;
            for (int b = 0; b < 4; b++)
            {
                h ^= (byte)(v >> (b * 8));
                h *= prime;
            }
        }

        return h;
    }

    /// <summary>
    /// The distinct items in the first <paramref name="count"/> slots, in first-seen order,
    /// filtered by <paramref name="filter"/> (null = all). Used by <c>TransferAll</c>.
    /// </summary>
    public static List<int> DistinctItems(IReadOnlyList<InventorySlot> slots, int count, Func<int, bool>? filter)
    {
        var items = new List<int>();
        if (slots == null) return items;
        if (count > slots.Count) count = slots.Count;
        for (int i = 0; i < count; i++)
        {
            int item = slots[i].Item;
            if (item == 0 || slots[i].Amount <= 0 || items.Contains(item)) continue;
            if (filter != null && !filter(item)) continue;
            items.Add(item);
        }

        return items;
    }
}
