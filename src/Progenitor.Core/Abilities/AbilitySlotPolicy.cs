using System;
using System.Globalization;

namespace Progenitor.Abilities;

/// <summary>
/// Game-free rules for <see cref="PluginAbilities.SetSlot"/>: which slots are on the bar, the clone
/// id for one slot assignment, the <c>ReplaceAbilityOnSlotBuff</c> element bytes, and the refusal
/// texts. Unit-tested without the game.
/// </summary>
public static class AbilitySlotPolicy
{
    /// <summary><c>AbilityGroupSlotBuffer.SLOTS_ON_BAR</c> (server metadata): slots 0-8 are the bar.</summary>
    public const int SlotsOnBar = 9;

    /// <summary><c>Buff_VBlood_Ability_Replace</c>: the vanilla buff the game applies when a player slots a V Blood power.</summary>
    public const int ReplaceBuffSource = 1171608023;

    /// <summary>
    /// Priority written on the element. A character can carry several replace buffs for one slot
    /// (a spellbook choice is one); the highest priority is meant to win. 100 sits above the
    /// vanilla buff's 0.
    /// </summary>
    public const int Priority = 100;

    /// <summary>
    /// Native stride of one <c>ReplaceAbilityOnSlotBuff</c>: <c>CopyCooldown</c>'s byte at 0x24
    /// rounded up to the 8-byte alignment of the <c>Condition</c> blob reference. Measured 40 live.
    /// </summary>
    public const int ElementSize = (Layout.ReplaceAbilityOnSlotCopyCooldownOffset + 1 + 7) & ~7;

    public static bool IsBarSlot(int slot) => slot >= 0 && slot < SlotsOnBar;

    /// <summary>
    /// Buff id for one plugin's assignment. The registering plugin is part of it: the prefab registry
    /// replaces a registration with the same id, so a shared id would let one plugin's unload take
    /// the clone out from under another's live handle.
    /// </summary>
    public static string CloneId(string plugin, int slot, int abilityGroupGuid)
    {
        if (string.IsNullOrWhiteSpace(plugin)) throw new ArgumentException("A plugin id is required.", nameof(plugin));
        return "progenitor.abilityslot." + plugin + "." + slot.ToString(CultureInfo.InvariantCulture) + "."
            + (abilityGroupGuid < 0 ? "m" + (-(long)abilityGroupGuid).ToString(CultureInfo.InvariantCulture)
                                    : abilityGroupGuid.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The element that puts <paramref name="abilityGroupGuid"/> on <paramref name="slot"/>: target
    /// 0 (<c>BuffTarget</c>), replace-group 0 (whatever is there), priority <see cref="Priority"/>,
    /// no condition, whole-cast block, no cooldown copy. Layout from the server dump; every other
    /// byte is zero.
    /// </summary>
    public static void WriteElement(Span<byte> element, int slot, int abilityGroupGuid)
    {
        if (element.Length < ElementSize)
            throw new ArgumentException("element needs " + ElementSize + " bytes", nameof(element));
        element.Slice(0, ElementSize).Clear();
        WriteInt(element, Layout.ReplaceAbilityOnSlotSlotOffset, slot);
        WriteInt(element, Layout.ReplaceAbilityOnSlotNewGroupOffset, abilityGroupGuid);
        WriteInt(element, Layout.ReplaceAbilityOnSlotPriorityOffset, Priority);
    }

    public static string NotBarSlotRefusal(int slot) =>
        "slot " + slot.ToString(CultureInfo.InvariantCulture) + " is not on the bar (0-" + (SlotsOnBar - 1) + ")";

    public static string EmptyGroupRefusal() => "ability group 0 is empty";

    public static string NoSlotsRefusal(int index, int version) =>
        "entity " + index + ":" + version + " has no ability slots (not a character)";

    private static void WriteInt(Span<byte> span, int offset, int value)
    {
        span[offset] = (byte)value;
        span[offset + 1] = (byte)(value >> 8);
        span[offset + 2] = (byte)(value >> 16);
        span[offset + 3] = (byte)(value >> 24);
    }
}
