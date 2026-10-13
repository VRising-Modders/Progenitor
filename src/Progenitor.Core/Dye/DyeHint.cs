using System;
using System.Collections.Generic;

namespace Progenitor.Dye;

/// <summary>One entry of the game's key-hint row, as <see cref="DyeHint.PickDonor"/> sees it.</summary>
internal readonly struct DyeHintDonor
{
    internal DyeHintDonor(bool active, string action, bool iconShown, string sprite)
    {
        Active = active;
        Action = action ?? "";
        IconShown = iconShown;
        Sprite = sprite ?? "";
    }

    /// <summary>The entry's GameObject is active (the game shows it for the current context).</summary>
    internal bool Active { get; }

    /// <summary>The entry's action caption, as drawn (localized).</summary>
    internal string Action { get; }

    /// <summary>The key glyph image is on.</summary>
    internal bool IconShown { get; }

    /// <summary>The key glyph's sprite name (<c>Icon_LMB1</c> for the left mouse button).</summary>
    internal string Sprite { get; }
}

/// <summary>
/// Game-free rules behind Dye all's key tip, <i>Hold [left mouse] Set All</i>, an entry Core adds to the game's own
/// key-hint row beside <i>[left mouse] Set Color</i> (<see cref="HintLabel"/> is the Unity half): its words, when it
/// shows, which game entry it is copied from and where it goes in the row.
/// </summary>
internal static class DyeHint
{
    /// <summary>The action caption, in the game's title case (<i>Set Color</i>, <i>Drop</i>, <i>Sort</i>).</summary>
    internal const string Action = "Set All";

    /// <summary>The game's caption for the picker's own entry (English), the preferred donor.</summary>
    internal const string SetColorAction = "Set Color";

    /// <summary>The left mouse button glyph's sprite name prefix in the game's key-hint row.</summary>
    internal const string MouseSpritePrefix = "Icon_LMB";

    /// <summary>
    /// The interaction word before the glyph, as the game writes a hold (<i>Hold [X] + [Q]</i>). A hold is always the
    /// fixed 1 s (<c>DyeAllEntries.HoldTime</c>), so the word never names a time.
    /// </summary>
    internal const string Interaction = "Hold";

    /// <summary>The whole tip as it reads on screen, glyph written as [LMB] (for logs, the readback and tests).</summary>
    internal const string Text = Interaction + " [LMB] " + Action;

    /// <summary>
    /// Whether the tip is on screen: the feature is running (not stopped or standing down), the feature is enabled, and the
    /// equipment panel's color palette is shown with at least one color swatch in it.
    /// </summary>
    internal static bool ShouldShow(bool stopped, bool enabled, bool paletteShown, bool hasColorSwatch) =>
        !stopped && enabled && paletteShown && hasColorSwatch;

    /// <summary>
    /// Which entry of the row to copy, or -1: the shown <i>Set Color</i> entry; else a shown entry with the left mouse
    /// glyph on; else the same two among hidden entries (the row is pooled, so a hidden entry still carries the style);
    /// an entry whose glyph is off comes last. Entries without the mouse sprite are never copied: the tip's glyph comes
    /// from the donor.
    /// </summary>
    internal static int PickDonor(IReadOnlyList<DyeHintDonor> entries)
    {
        if (entries == null) return -1;
        int best = -1, bestRank = int.MaxValue;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (!e.Sprite.StartsWith(MouseSpritePrefix, StringComparison.Ordinal)) continue;
            bool setColor = string.Equals(e.Action.Trim(), SetColorAction, StringComparison.OrdinalIgnoreCase);
            // A pooled entry keeps its last sprite with the glyph turned off (the Sort entry shows the R key as text),
            // so an entry whose glyph is off ranks last.
            int rank = !e.IconShown ? 4 : (e.Active ? 0 : 2) + (setColor ? 0 : 1);
            if (rank < bestRank)
            {
                best = i;
                bestRank = rank;
            }
        }

        return best;
    }

    /// <summary>
    /// The sibling index that puts the tip right after the donor at <paramref name="donorIndex"/>, given the tip is at
    /// <paramref name="tipIndex"/> now (indexes counted with the tip in the row).
    /// </summary>
    internal static int SiblingAfter(int donorIndex, int tipIndex)
    {
        if (tipIndex == donorIndex + 1) return tipIndex;
        return tipIndex < donorIndex ? donorIndex : donorIndex + 1;
    }
}
