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
/// <c>EquipmentSlot.HideEquipment</c> (the picker's X cell). <see cref="Slotted"/> is the server's
/// rule for "something is equipped here": the slot's <c>SlotId</c> is nonzero, which is already true
/// while the entity still waits for sync (<see cref="Pending"/>).
/// </summary>
internal readonly struct DyeItem
{
    internal DyeItem(int index, int version, bool present, bool hasTransmog, byte activeIndex, byte numColors,
        IReadOnlyList<DyePaletteKey>? palettes = null, bool hidden = false, bool? slotted = null)
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
    /// A piece under a transmog that was left alone: dyeing it rewrites the transmog's shown color (see
    /// <see cref="DyePlanner"/>), and the transmog cannot be given its own color back (it has no colors, or
    /// putting it back would show a model the player hid).
    /// </summary>
    TransmogWouldChange,

    /// <summary>
    /// A piece under a transmog that the player hid with the X cell: its event would clear the hide flag
    /// and show it again when the transmog comes off.
    /// </summary>
    CoveredHidden,

    /// <summary>
    /// The slot holds a transmog the client cannot use yet (its entity waits for sync): the server already
    /// treats the slot as covered, so an event for the piece under it would re-key the transmog's shown
    /// color from that piece's palettes. Nothing is sent for the slot; a later dye-all catches up.
    /// </summary>
    TransmogNotSynced,
}

/// <summary>An event to send: <c>ChangeTransmogColorEvent</c> for one worn piece.</summary>
internal readonly struct DyeTarget
{
    internal DyeTarget(int equipmentType, DyeItem item, byte newIndex, bool restore = false)
    {
        EquipmentType = equipmentType;
        Item = item;
        NewIndex = newIndex;
        Restore = restore;
    }

    /// <summary>The stat type, or the cosmetic type when the visible piece is a transmog.</summary>
    internal int EquipmentType { get; }

    internal DyeItem Item { get; }

    internal byte NewIndex { get; }

    /// <summary>
    /// True for a transmog sent its own current color after the piece under it was dyed, so the shown
    /// color is rebuilt from the transmog's own palettes (see <see cref="DyePlanner"/>).
    /// </summary>
    internal bool Restore { get; }
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
            parts.Add(DyeEquipmentTypes.Name(t.EquipmentType) + (t.Restore ? " kept at " : "->") + t.NewIndex);
        foreach (var s in Skipped) parts.Add(DyeEquipmentTypes.Name(s.EquipmentType) + " skipped (" + s.Reason + ")");
        return parts.Count == 0 ? "nothing equipped" : string.Join(", ", parts);
    }
}

/// <summary>
/// Which worn pieces a dye-all touches and with which index. Game-free.
///
/// <para><b>Which piece.</b> The color picker's own rule
/// (<c>TransmogContainer.TryGetEquipmentOrCosmeticSlotFromEquipmentType</c>): the cosmetic
/// (transmog) piece when one is equipped, with the cosmetic equipment type; otherwise the stat piece
/// with the stat type. The stat piece under a transmog is dyed as well, with its stat type, unless
/// the caller opts out: the server accepts it because that piece is equipped in that slot.</para>
///
/// <para><b>Under a transmog.</b> Dye is stored on each item entity (<c>TransmogData.ActiveTransmogIndex</c>);
/// the character's <c>Equipment</c> slot keeps the shown palette key. The server's slot write
/// (<c>TransmogEventsSystem.UpdateTransmogColorForEquipmentSlot</c>) picks the slot by
/// what is shown, not by the event's type: with a transmog equipped it writes the transmog slot's key,
/// resolved from the palettes of the item the event names. So the stat event under a transmog stores the
/// color on the real gear but also re-keys the transmog's shown color from the gear's palettes. The plan
/// therefore sends the covered piece first and the transmog right after it, with the new color or, when
/// the transmog keeps its color (already that color, palette missing, index out of range), a restore of
/// its own current index. When no transmog event can follow (the transmog has no colors, its current
/// index is out of range, or a restore would show a model the player hid; the event clears the hide
/// flag), the covered piece is left alone (<see cref="DyeSkipReason.TransmogWouldChange"/>). A covered
/// piece the player hid is left alone too (<see cref="DyeSkipReason.CoveredHidden"/>): its own event would
/// clear its hide flag. "Covered" is the server's test, the transmog slot's <c>SlotId</c>; while the
/// transmog's entity still waits for sync, nothing is sent for that slot
/// (<see cref="DyeSkipReason.TransmogNotSynced"/>). The server handles events one at a time; that it
/// handles one client's events in the order sent is assumed, not verified.</para>
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
    /// <summary>
    /// Plan a dye-all of <paramref name="color"/> over <paramref name="slots"/>. With
    /// <paramref name="includeCovered"/>, a stat piece under a transmog is dyed too (with its stat
    /// type), so it already wears the color when the transmog comes off.
    /// </summary>
    internal static DyePlan Plan(IReadOnlyList<DyeSlot> slots, DyeColor color, bool includeCovered = true)
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
            // The server decides "covered" by the transmog slot's SlotId, not by the entity being usable here.
            if (slot.Cosmetic.Slotted && !slot.Cosmetic.Present)
            {
                plan.Skipped.Add(new DyeSkip(DyeEquipmentTypes.CosmeticOf(slot.StatType), DyeSkipReason.TransmogNotSynced));
                continue;
            }

            if (!Choose(slot, out int type, out var item))
            {
                plan.Skipped.Add(new DyeSkip(slot.StatType, DyeSkipReason.Empty));
                continue;
            }

            bool covered = includeCovered && type != slot.StatType && slot.Stat.Present;
            if (!covered)
            {
                Record(plan, type, Decide(type, item, color, sourceKey, offset, out var target, out bool matched),
                    target, matched);
                continue;
            }

            // Under a transmog: the covered piece first, then the transmog (its new color or a restore).
            // A covered piece the player hid is left alone: its event would clear its hide flag.
            DyeTarget under = default;
            bool underMatched = false;
            var shownReason = Decide(type, item, color, sourceKey, offset, out var shown, out bool shownMatched);
            var underReason = slot.Stat.Hidden
                ? DyeSkipReason.CoveredHidden
                : Decide(slot.StatType, slot.Stat, color, sourceKey, offset, out under, out underMatched);
            if (underReason != null)
            {
                Record(plan, slot.StatType, underReason, under, underMatched);
                Record(plan, type, shownReason, shown, shownMatched);
                continue;
            }

            if (shownReason == null)
            {
                Record(plan, slot.StatType, null, under, underMatched);
                Record(plan, type, null, shown, shownMatched);
            }
            else if (CanRestore(item, shownReason.Value))
            {
                Record(plan, slot.StatType, null, under, underMatched);
                plan.Targets.Add(new DyeTarget(type, item, item.ActiveIndex, restore: true));
                plan.Skipped.Add(new DyeSkip(type, shownReason.Value));
            }
            else
            {
                plan.Skipped.Add(new DyeSkip(slot.StatType, DyeSkipReason.TransmogWouldChange));
                plan.Skipped.Add(new DyeSkip(type, shownReason.Value));
            }
        }

        return plan;
    }

    /// <summary>
    /// Whether the transmog can be sent its own current index after the piece under it was dyed: it has
    /// colors, its index is one the server accepts, and the event would not show a hidden model.
    /// </summary>
    private static bool CanRestore(DyeItem transmog, DyeSkipReason why) =>
        why != DyeSkipReason.NotDyeable && transmog.HasTransmog && transmog.ActiveIndex < transmog.NumColors && !transmog.Hidden;

    private static void Record(DyePlan plan, int type, DyeSkipReason? reason, DyeTarget target, bool matched)
    {
        if (reason is { } why)
        {
            plan.Skipped.Add(new DyeSkip(type, why));
            return;
        }

        if (matched) plan.MatchedByPalette = true;
        plan.Targets.Add(target);
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

    /// <summary>
    /// The piece the picker shows for a slot: the cosmetic one when present (with the cosmetic
    /// type), else the stat one. False when the slot is empty.
    /// </summary>
    internal static bool Choose(DyeSlot slot, out int equipmentType, out DyeItem item)
    {
        if (slot.Cosmetic.Present)
        {
            equipmentType = DyeEquipmentTypes.CosmeticOf(slot.StatType);
            item = slot.Cosmetic;
            return equipmentType >= 0;
        }

        if (slot.Stat.Present)
        {
            equipmentType = slot.StatType;
            item = slot.Stat;
            return true;
        }

        equipmentType = slot.StatType;
        item = DyeItem.None;
        return false;
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
