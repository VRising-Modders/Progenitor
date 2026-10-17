using System;
using System.Collections.Generic;

namespace Progenitor.Dye;

/// <summary>
/// The game's <c>ProjectM.EquipmentType</c> values Dye all uses (client dump 463762). Kept as
/// ints so the planner is game-free.
/// </summary>
internal static class DyeEquipmentTypes
{
    internal const int Chest = 1;
    internal const int Footgear = 4;
    internal const int Legs = 5;
    internal const int Cloak = 8;
    internal const int Gloves = 9;
    internal const int CosmeticChest = 12;
    internal const int CosmeticFootgear = 13;
    internal const int CosmeticLegs = 14;
    internal const int CosmeticCloak = 15;
    internal const int CosmeticGloves = 16;

    /// <summary>
    /// The slots the equipment panel's color picker serves, in the order it lists them.
    /// <c>TransmogContainer.TryGetEquipmentOrCosmeticSlotFromEquipmentType</c>
    /// answers only these five; headgear, weapon, magic source and bag return false there, so the
    /// game never offers colors for them.
    /// </summary>
    internal static readonly int[] Dyeable = { Chest, Legs, Footgear, Gloves, Cloak };

    /// <summary>The cosmetic (transmog) type paired with a stat type, or -1.</summary>
    internal static int CosmeticOf(int statType) => statType switch
    {
        Chest => CosmeticChest,
        Footgear => CosmeticFootgear,
        Legs => CosmeticLegs,
        Cloak => CosmeticCloak,
        Gloves => CosmeticGloves,
        _ => -1,
    };

    /// <summary>A short name for logs.</summary>
    internal static string Name(int type) => type switch
    {
        Chest => "chest",
        Footgear => "boots",
        Legs => "legs",
        Cloak => "cloak",
        Gloves => "gloves",
        CosmeticChest => "chest (transmog)",
        CosmeticFootgear => "boots (transmog)",
        CosmeticLegs => "legs (transmog)",
        CosmeticCloak => "cloak (transmog)",
        CosmeticGloves => "gloves (transmog)",
        _ => "type " + type,
    };
}

/// <summary>One palette an item offers: <c>TransmogAssetKey</c> (client dump 506457).</summary>
internal readonly struct DyePaletteKey : IEquatable<DyePaletteKey>
{
    internal DyePaletteKey(int assetGuid, byte startIndex, byte numColors)
    {
        AssetGuid = assetGuid;
        StartIndex = startIndex;
        NumColors = numColors;
    }

    /// <summary>The palette's <c>TransmogPaletteData</c> prefab GUID.</summary>
    internal int AssetGuid { get; }

    /// <summary>Where its colors start in the item's option list.</summary>
    internal byte StartIndex { get; }

    /// <summary>How many colors it adds.</summary>
    internal byte NumColors { get; }

    public bool Equals(DyePaletteKey other) =>
        AssetGuid == other.AssetGuid && StartIndex == other.StartIndex && NumColors == other.NumColors;

    public override bool Equals(object? obj) => obj is DyePaletteKey other && Equals(other);

    public override int GetHashCode() => (AssetGuid * 397) ^ (StartIndex << 8) ^ NumColors;
}

/// <summary>
/// One equipped item as the planner needs it. <see cref="Present"/> follows the game's rule: an
/// entity that is not null and not waiting for sync. <see cref="NumColors"/> is
/// <c>TransmogData.NumColorOptions</c>; 0 (or no <c>TransmogData</c>) means not dyeable.
/// <see cref="Palettes"/> is null when not read. <see cref="Hidden"/> is the slot's
/// <c>EquipmentSlot.HideEquipment</c> (the picker's X cell) and <see cref="Broken"/> its
/// <c>EquipmentSlot.IsBroken</c>. <see cref="Slotted"/> is the server's rule for "something is equipped
/// here": the slot's <c>SlotId</c> is nonzero, which is already true while the entity still waits for
/// sync (<see cref="Pending"/>).
/// </summary>
internal readonly struct DyeItem
{
    internal DyeItem(int index, int version, bool present, bool hasTransmog, byte activeIndex, byte numColors,
        IReadOnlyList<DyePaletteKey>? palettes = null, bool hidden = false, bool? slotted = null, bool broken = false)
    {
        Index = index;
        Version = version;
        Present = present;
        HasTransmog = hasTransmog;
        ActiveIndex = activeIndex;
        NumColors = numColors;
        Palettes = palettes;
        Hidden = hidden;
        Slotted = slotted ?? present;
        Broken = broken;
    }

    /// <summary>An empty slot.</summary>
    internal static DyeItem None => default;

    /// <summary>A slot whose <c>SlotId</c> is set while its entity is not usable yet (waiting for sync).</summary>
    internal static DyeItem Pending(bool hidden = false) =>
        new(0, 0, present: false, hasTransmog: false, 0, 0, null, hidden, slotted: true);

    /// <summary>The slot's <c>SlotId</c> is nonzero (the server's test for an equipped piece).</summary>
    internal bool Slotted { get; }

    internal int Index { get; }

    internal int Version { get; }

    internal bool Present { get; }

    internal bool HasTransmog { get; }

    internal byte ActiveIndex { get; }

    internal byte NumColors { get; }

    internal IReadOnlyList<DyePaletteKey>? Palettes { get; }

    /// <summary>The player hid this piece's model with the picker's X cell.</summary>
    internal bool Hidden { get; }

    /// <summary>The piece is broken; the game then shows the slot's default model, not the piece.</summary>
    internal bool Broken { get; }
}

/// <summary>One of the five dyeable slots: its stat item and its cosmetic (transmog) item.</summary>
internal readonly struct DyeSlot
{
    internal DyeSlot(int statType, DyeItem stat, DyeItem cosmetic)
    {
        StatType = statType;
        Stat = stat;
        Cosmetic = cosmetic;
    }

    internal int StatType { get; }

    internal DyeItem Stat { get; }

    internal DyeItem Cosmetic { get; }
}

/// <summary>Why a slot got no event.</summary>
internal enum DyeSkipReason
{
    /// <summary>Nothing equipped there.</summary>
    Empty,

    /// <summary>The worn piece has no colors (no <c>TransmogData</c> or zero options).</summary>
    NotDyeable,

    /// <summary>The piece already wears that color.</summary>
    AlreadyThatColor,

    /// <summary>The piece does not offer the swatch's palette.</summary>
    PaletteMissing,

    /// <summary>The index is past the piece's last color; the server would refuse it.</summary>
    IndexOutOfRange,

    /// <summary>
    /// The piece that shows in this slot is hidden with the picker's X cell. Its color is left alone: the
    /// color event would also clear the hide flag and show the model again.
    /// </summary>
    Hidden,

    /// <summary>The piece that would show in this slot is broken, so the game shows the default model instead.</summary>
    Broken,

    /// <summary>
    /// The slot holds a transmog the client cannot use yet (its entity waits for sync). The transmog is what
    /// shows, but its item cannot be named in an event yet, so nothing is sent for the slot; a later dye-all
    /// catches up.
    /// </summary>
    TransmogNotSynced,
}

/// <summary>An event to send: <c>ChangeTransmogColorEvent</c> for one worn piece.</summary>
internal readonly struct DyeTarget
{
    internal DyeTarget(int equipmentType, DyeItem item, byte newIndex)
    {
        EquipmentType = equipmentType;
        Item = item;
        NewIndex = newIndex;
    }

    /// <summary>The stat type, or the cosmetic type when the visible piece is a transmog.</summary>
    internal int EquipmentType { get; }

    internal DyeItem Item { get; }

    internal byte NewIndex { get; }
}

/// <summary>A slot that got no event, and why.</summary>
internal readonly struct DyeSkip
{
    internal DyeSkip(int equipmentType, DyeSkipReason reason)
    {
        EquipmentType = equipmentType;
        Reason = reason;
    }

    internal int EquipmentType { get; }

    internal DyeSkipReason Reason { get; }
}

/// <summary>What to dye: the color as the clicked swatch names it.</summary>
internal readonly struct DyeColor
{
    internal DyeColor(byte index, IReadOnlyList<DyePaletteKey>? sourcePalettes)
    {
        Index = index;
        SourcePalettes = sourcePalettes;
    }

    /// <summary>The swatch's index in the selected piece's option list (<c>Data.Index</c>).</summary>
    internal byte Index { get; }

    /// <summary>The selected piece's palettes, when read; null applies the same index everywhere.</summary>
    internal IReadOnlyList<DyePaletteKey>? SourcePalettes { get; }
}

/// <summary>The planner's answer.</summary>
internal sealed class DyePlan
{
    internal List<DyeTarget> Targets { get; } = new();

    internal List<DyeSkip> Skipped { get; } = new();

    /// <summary>True when any target was mapped through a shared palette rather than the raw index.</summary>
    internal bool MatchedByPalette { get; set; }

    /// <summary>One line for the log.</summary>
    internal string Describe()
    {
        var parts = new List<string>();
        foreach (var t in Targets)
            parts.Add(DyeEquipmentTypes.Name(t.EquipmentType) + "->" + t.NewIndex);
        foreach (var s in Skipped) parts.Add(DyeEquipmentTypes.Name(s.EquipmentType) + " skipped (" + s.Reason + ")");
        return parts.Count == 0 ? "nothing equipped" : string.Join(", ", parts);
    }
}

/// <summary>
/// Which worn pieces a dye-all touches and with which index. Game-free.
///
/// <para><b>Only what shows.</b> One piece per slot at most: the one the character visibly wears there
/// (<see cref="Visible"/>). That is the game's own render rule (its hybrid equipment pass, the same rule
/// the wearable code follows): the transmog (cosmetic) piece when the cosmetic slot's <c>SlotId</c> is
/// set, otherwise the stat piece; then that piece's hide and broken state. So a transmog that shows is
/// dyed with its cosmetic equipment type and the gear under it keeps its own color; a slot whose shown
/// piece the player hid with the X cell is left alone (<see cref="DyeSkipReason.Hidden"/>), and so is a
/// broken one (<see cref="DyeSkipReason.Broken"/>). A hidden gear piece under a shown transmog does not
/// matter: the transmog is what shows. While a transmog's entity still waits for sync, the transmog is what
/// shows but cannot be named, so nothing is sent for that slot (<see cref="DyeSkipReason.TransmogNotSynced"/>).
/// Each event names only the shown piece: the server writes the color on that item, re-keys the slot that
/// shows it from that item's own palettes and clears that slot's hide flag, which a shown piece does not
/// have set.</para>
///
/// <para><b>Which index.</b> An index names a color within one piece's option list, which the piece's
/// palettes (<c>TransmogAssetKey</c> rows) lay out. With the source piece's palettes known, the
/// swatch's palette and offset are found there and looked up in each target's palettes; a target
/// whose palettes were read but lack that palette is skipped, and a target whose palettes could not
/// be read gets the raw index. Without the source palettes, the raw index is sent to every piece;
/// that is only the same color where the pieces carry the same palettes in the same order (not
/// verified live; see modules.md, Dye all). Either way an index at or
/// past the target's <c>NumColorOptions</c> is skipped, because the server
/// (<c>TransmogEventsSystem</c> lambda 0) refuses it.</para>
/// </summary>
internal static class DyePlanner
{
    /// <summary>Plan a dye-all of <paramref name="color"/> over <paramref name="slots"/>: the shown piece of each slot.</summary>
    internal static DyePlan Plan(IReadOnlyList<DyeSlot> slots, DyeColor color)
    {
        if (slots == null) throw new ArgumentNullException(nameof(slots));
        var plan = new DyePlan();
        DyePaletteKey? sourceKey = null;
        int offset = 0;
        if (color.SourcePalettes != null && TryLocate(color.SourcePalettes, color.Index, out var key, out int at))
        {
            sourceKey = key;
            offset = at;
        }

        foreach (var slot in slots)
        {
            var notShown = Visible(slot, out int type, out var item);
            if (notShown is { } hidden)
            {
                plan.Skipped.Add(new DyeSkip(type, hidden));
                continue;
            }

            if (Decide(type, item, color, sourceKey, offset, out var target, out bool matched) is { } why)
            {
                plan.Skipped.Add(new DyeSkip(type, why));
                continue;
            }

            if (matched) plan.MatchedByPalette = true;
            plan.Targets.Add(target);
        }

        return plan;
    }

    /// <summary>
    /// The piece that shows in <paramref name="slot"/> and the equipment type to name it with, or why none
    /// does (null: <paramref name="item"/> shows). A set cosmetic <c>SlotId</c> means the transmog shows,
    /// whatever the gear under it is; then the shown piece's hide (X cell) and broken state count.
    /// </summary>
    internal static DyeSkipReason? Visible(DyeSlot slot, out int equipmentType, out DyeItem item)
    {
        if (slot.Cosmetic.Slotted)
        {
            equipmentType = DyeEquipmentTypes.CosmeticOf(slot.StatType);
            item = slot.Cosmetic;
            if (equipmentType < 0) return DyeSkipReason.Empty;
            if (!item.Present) return DyeSkipReason.TransmogNotSynced;
        }
        else
        {
            equipmentType = slot.StatType;
            item = slot.Stat;
            if (!item.Present) return DyeSkipReason.Empty;
        }

        if (item.Hidden) return DyeSkipReason.Hidden;
        if (item.Broken) return DyeSkipReason.Broken;
        return null;
    }

    /// <summary>The event for one piece, or why it gets none (null reason: send <paramref name="target"/>).</summary>
    private static DyeSkipReason? Decide(int type, DyeItem item, DyeColor color, DyePaletteKey? sourceKey, int offset,
        out DyeTarget target, out bool matched)
    {
        target = default;
        matched = false;
        if (!item.HasTransmog || item.NumColors == 0) return DyeSkipReason.NotDyeable;

        int index = color.Index;
        if (sourceKey is { } source && item.Palettes != null)
        {
            if (!TryFind(item.Palettes, source.AssetGuid, offset, out index)) return DyeSkipReason.PaletteMissing;
            matched = true;
        }

        if (index < 0 || index >= item.NumColors || index > byte.MaxValue) return DyeSkipReason.IndexOutOfRange;
        if (item.ActiveIndex == index) return DyeSkipReason.AlreadyThatColor;
        target = new DyeTarget(type, item, (byte)index);
        return null;
    }

    /// <summary>The palette (and the offset within it) that option <paramref name="index"/> comes from.</summary>
    internal static bool TryLocate(IReadOnlyList<DyePaletteKey> palettes, int index, out DyePaletteKey key, out int offset)
    {
        foreach (var p in palettes)
        {
            if (index >= p.StartIndex && index < p.StartIndex + p.NumColors)
            {
                key = p;
                offset = index - p.StartIndex;
                return true;
            }
        }

        key = default;
        offset = 0;
        return false;
    }

    private static bool TryFind(IReadOnlyList<DyePaletteKey> palettes, int assetGuid, int offset, out int index)
    {
        foreach (var p in palettes)
        {
            if (p.AssetGuid != assetGuid || offset >= p.NumColors) continue;
            index = p.StartIndex + offset;
            return true;
        }

        index = -1;
        return false;
    }
}
