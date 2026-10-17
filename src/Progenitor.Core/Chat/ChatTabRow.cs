using System;
using System.Collections.Generic;

namespace Progenitor.Chat;

/// <summary>How one item of the chat tab row is drawn this frame.</summary>
internal enum ChatTabItemState
{
    /// <summary>At rest: gray caption, no glow, no underline.</summary>
    Normal = 0,

    /// <summary>Under the pointer: brighter caption, a soft glow and a fainter underline.</summary>
    Hover = 1,

    /// <summary>The tab shown: white caption, the full glow and underline.</summary>
    Active = 2,

    /// <summary>The tab being dragged: it follows the pointer with the active look, slightly see-through.</summary>
    Dragged = 3,
}

/// <summary>
/// The look of one state: caption color, and the alpha of the glow behind the caption and of the underline below it.
/// The colors are 0..1 RGBA.
/// </summary>
internal readonly record struct ChatTabStyle(float CaptionR, float CaptionG, float CaptionB, float CaptionA, float GlowAlpha, float LineAlpha, float ItemAlpha)
{
    /// <summary>A plain gray (or white) caption.</summary>
    internal static ChatTabStyle Gray(float level, float glow, float line, float item = 1f) => new(level, level, level, 1f, glow, line, item);

    /// <summary>How bright the caption is (its strongest channel times its alpha), for ordering states.</summary>
    internal float CaptionLevel => Math.Max(CaptionR, Math.Max(CaptionG, CaptionB)) * CaptionA;
}

/// <summary>
/// The colors the game's own menu tab gives each state, as read from the donor (the full-screen menu's top tab bar:
/// EMOTES, VAMPIRE POWERS, SPELLBOOK...): its caption, glow (<c>Glow</c>) and underline (<c>Name/Line</c>) tints.
/// A value left null was not readable.
/// </summary>
internal sealed class ChatTabDonorColors
{
    internal (float R, float G, float B, float A)? CaptionNormal { get; set; }
    internal (float R, float G, float B, float A)? CaptionHover { get; set; }
    internal (float R, float G, float B, float A)? CaptionSelected { get; set; }
    internal float? GlowHover { get; set; }
    internal float? GlowSelected { get; set; }
    internal float? LineHover { get; set; }
    internal float? LineSelected { get; set; }
}

/// <summary>
/// CHAT12: the chat tab row's decisions, game-free (the drawing is <c>ChatTabs</c>, the art lookup <c>ChatTabArt</c>).
///
/// <list type="bullet">
/// <item><b>States</b> (<see cref="StateOf"/>, <see cref="Style"/>): normal, hover, active, and while dragging the
/// dragged tab. The active look is always stronger than the hover look, which is stronger than
/// normal, whatever the donor's colors say. The dragged tab has the active look.</item>
/// <item><b>Drag</b> (<see cref="PassedThreshold"/>, <see cref="DropSlot"/>, <see cref="TargetIndex"/>): a press on a
/// tab that moves less than <see cref="DragThreshold"/> is a click; past it the tab follows the pointer and a marker
/// shows the slot it would drop into.</item>
/// <item><b>Reorder</b> (<see cref="Move"/>): the tab list, the unread marks and the active index move together.</item>
/// </list>
/// </summary>
internal static class ChatTabRow
{
    /// <summary>How far (canvas units) a press must travel before it is a drag rather than a click.</summary>
    internal const float DragThreshold = 6f;

    /// <summary>Space between two items, in canvas units.</summary>
    internal const float Gap = 6f;

    /// <summary>Space on each side of a caption inside its item.</summary>
    internal const float Pad = 9f;

    /// <summary>The row's height, in canvas units.</summary>
    internal const float Height = 26f;

    /// <summary>The caption size the row starts from; the chat's own line size is used when it is readable.</summary>
    internal const float DefaultFontSize = 17f;

    internal const float MinFontSize = 14f;
    internal const float MaxFontSize = 21f;

    /// <summary>The game's menu tab caption at rest (0.604 gray) and selected (white), and its selected glow and underline.</summary>
    internal const float GameRestCaption = 0.604f;
    internal const float GameSelectedGlow = 0.502f;
    internal const float GameSelectedLine = 1f;

    /// <summary>Hover sits this far from normal toward the active look when the donor gives no hover tint of its own.</summary>
    internal const float HoverShare = 0.55f;

    /// <summary>The dragged tab is drawn a little see-through, so the marker and the tabs under it show.</summary>
    internal const float DraggedAlpha = 0.85f;

    /// <summary>The kit look (no donor reached): states differ by caption, a flat tint behind and a drawn underline.</summary>
    internal static readonly ChatTabStyle KitNormal = ChatTabStyle.Gray(0.62f, 0f, 0f);
    internal static readonly ChatTabStyle KitHover = ChatTabStyle.Gray(0.86f, 0.10f, 0.45f);
    internal static readonly ChatTabStyle KitActive = ChatTabStyle.Gray(1f, 0.20f, 1f);

    /// <summary>The caption size: the chat's line size within <see cref="MinFontSize"/>..<see cref="MaxFontSize"/>.</summary>
    internal static float FontSize(float chatLineSize) =>
        chatLineSize > 1f ? Math.Max(MinFontSize, Math.Min(MaxFontSize, chatLineSize)) : DefaultFontSize;

    /// <summary>
    /// The state of item <paramref name="index"/>: tabs are <c>0..tabCount-1</c>, the + and menu buttons follow and are
    /// never active. <paramref name="hovered"/> and <paramref name="dragged"/> are item indexes or -1.
    /// </summary>
    internal static ChatTabItemState StateOf(int index, int tabCount, int active, int hovered, int dragged)
    {
        if (dragged >= 0 && index == dragged) return ChatTabItemState.Dragged;
        if (index < tabCount && index == active) return ChatTabItemState.Active;
        // While a drag is on, no other item lights up under the pointer.
        if (dragged < 0 && index == hovered) return ChatTabItemState.Hover;
        return ChatTabItemState.Normal;
    }

    /// <summary>
    /// The look of <paramref name="state"/>: the donor's colors when it was found (<paramref name="donor"/> not null),
    /// else the kit's. Either way active is stronger than hover, and hover than normal.
    /// </summary>
    internal static ChatTabStyle Style(ChatTabItemState state, ChatTabDonorColors? donor)
    {
        if (donor == null)
        {
            return state switch
            {
                ChatTabItemState.Hover => KitHover,
                ChatTabItemState.Active => KitActive,
                ChatTabItemState.Dragged => KitActive with { ItemAlpha = DraggedAlpha },
                _ => KitNormal,
            };
        }

        var normalCaption = donor.CaptionNormal ?? (GameRestCaption, GameRestCaption, GameRestCaption, 1f);
        var activeCaption = donor.CaptionSelected ?? (1f, 1f, 1f, 1f);
        // A donor whose selected caption is no brighter than its rest one would make the states blur: keep the game's.
        if (Level(activeCaption) <= Level(normalCaption) + 0.1f)
        {
            normalCaption = (GameRestCaption, GameRestCaption, GameRestCaption, 1f);
            activeCaption = (1f, 1f, 1f, 1f);
        }

        float glowActive = Clamp(donor.GlowSelected ?? GameSelectedGlow, 0.2f, 1f);
        float lineActive = Clamp(donor.LineSelected ?? GameSelectedLine, 0.5f, 1f);
        var normal = new ChatTabStyle(normalCaption.R, normalCaption.G, normalCaption.B, normalCaption.A, 0f, 0f, 1f);
        var active = new ChatTabStyle(activeCaption.R, activeCaption.G, activeCaption.B, activeCaption.A, glowActive, lineActive, 1f);
        switch (state)
        {
            case ChatTabItemState.Active:
                return active;
            case ChatTabItemState.Dragged:
                return active with { ItemAlpha = DraggedAlpha };
            case ChatTabItemState.Hover:
            {
                // The donor's hover tint when it reads below the active look; otherwise part way from normal to active.
                var between = Lerp(normalCaption, activeCaption, HoverShare);
                var caption = donor.CaptionHover is { } h && Level(h) > Level(normalCaption) && Level(h) < Level(activeCaption) ? h : between;
                float glow = donor.GlowHover is { } g && g > 0.01f && g < glowActive ? g : glowActive * HoverShare;
                float line = donor.LineHover is { } l && l > 0.01f && l < lineActive ? l : lineActive * HoverShare;
                return new ChatTabStyle(caption.R, caption.G, caption.B, caption.A, glow, line, 1f);
            }
            default:
                return normal;
        }
    }

    /// <summary>Whether a press that started at (x0, y0) has moved far enough (canvas units) to be a drag.</summary>
    internal static bool PassedThreshold(float x0, float y0, float x, float y, float threshold = DragThreshold)
    {
        float dx = x - x0, dy = y - y0;
        return dx * dx + dy * dy >= threshold * threshold;
    }

    /// <summary>
    /// The left edge of each item laid out from <paramref name="start"/> with <see cref="Gap"/> between them.
    /// </summary>
    internal static float[] Layout(IReadOnlyList<float> widths, float start = 0f, float gap = Gap)
    {
        var lefts = new float[widths.Count];
        float x = start;
        for (int i = 0; i < widths.Count; i++)
        {
            lefts[i] = x;
            x += Math.Max(0f, widths[i]) + gap;
        }

        return lefts;
    }

    /// <summary>
    /// Where a dragged tab would drop: the slot before tab <c>k</c> (0..count), from the pointer's x against the centers
    /// of the tabs as laid out. Slot <c>count</c> is after the last tab.
    /// </summary>
    internal static int DropSlot(IReadOnlyList<float> lefts, IReadOnlyList<float> widths, float pointerX)
    {
        int n = Math.Min(lefts.Count, widths.Count);
        for (int i = 0; i < n; i++)
            if (pointerX < lefts[i] + widths[i] * 0.5f) return i;
        return n;
    }

    /// <summary>The index the dragged tab ends at when dropped into <paramref name="slot"/> (the list still holds it).</summary>
    internal static int TargetIndex(int from, int slot) => slot > from ? slot - 1 : slot;

    /// <summary>
    /// The x of the insertion marker for <paramref name="slot"/>: midway in the gap before tab <c>slot</c>, or just after
    /// the last tab. <paramref name="lefts"/> and <paramref name="widths"/> are the tabs' layout.
    /// </summary>
    internal static float MarkerX(IReadOnlyList<float> lefts, IReadOnlyList<float> widths, int slot, float gap = Gap)
    {
        int n = Math.Min(lefts.Count, widths.Count);
        if (n == 0) return 0f;
        if (slot <= 0) return lefts[0] - gap * 0.5f;
        if (slot >= n) return lefts[n - 1] + widths[n - 1] + gap * 0.5f;
        return lefts[slot] - gap * 0.5f;
    }

    /// <summary>
    /// Move tab <paramref name="from"/> to <paramref name="to"/>: the list, its unread marks and the active index all
    /// follow, so the same tab stays shown and the same tabs keep their marks. False when nothing moves.
    /// </summary>
    internal static bool Move<T>(List<T> tabs, bool[] unread, ref int active, int from, int to)
    {
        int n = tabs.Count;
        if (from < 0 || from >= n) return false;
        to = Math.Max(0, Math.Min(n - 1, to));
        if (to == from) return false;
        var item = tabs[from];
        tabs.RemoveAt(from);
        tabs.Insert(to, item);
        if (unread.Length >= n)
        {
            bool mark = unread[from];
            if (from < to) Array.Copy(unread, from + 1, unread, from, to - from);
            else Array.Copy(unread, to, unread, to + 1, from - to);
            unread[to] = mark;
        }

        active = MovedIndex(active, from, to);
        return true;
    }

    /// <summary>Where the item at <paramref name="index"/> is after the item at <paramref name="from"/> moved to <paramref name="to"/>.</summary>
    internal static int MovedIndex(int index, int from, int to)
    {
        if (index == from) return to;
        if (from < to && index > from && index <= to) return index - 1;
        if (from > to && index >= to && index < from) return index + 1;
        return index;
    }

    /// <summary>The index after closing tab <paramref name="closed"/> of the tab that was at <paramref name="active"/>.</summary>
    internal static int ActiveAfterClose(int active, int closed, int countAfter)
    {
        int next = active > closed ? active - 1 : active == closed ? Math.Max(0, closed - 1) : active;
        return Math.Max(0, Math.Min(Math.Max(0, countAfter - 1), next));
    }

    private static float Level((float R, float G, float B, float A) c) => Math.Max(c.R, Math.Max(c.G, c.B)) * c.A;

    private static (float R, float G, float B, float A) Lerp((float R, float G, float B, float A) a, (float R, float G, float B, float A) b, float t) =>
        (a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
}

/// <summary>
/// CHAT12: the player's tabs as the row holds them (order, unread marks, the shown tab), game-free. They are loaded
/// once from <c>[Chat] Tabs</c>; until then nothing may change or save them, so an early call (a bridge move before the
/// chat first updates, a setting not bound yet) can never overwrite the player's saved tabs with the defaults.
/// </summary>
internal sealed class ChatTabSet
{
    internal List<ChatTab> Tabs = new() { ChatTab.All(), ChatTab.Clan() };
    internal bool[] Unread = new bool[ChatTab.MaxTabs];
    internal int Active;

    internal bool Loaded { get; private set; }

    /// <summary>
    /// Load from the saved setting the first time it is readable. <paramref name="saved"/> null means the setting is not
    /// bound yet: false, and nothing is loaded.
    /// </summary>
    internal bool EnsureLoaded(string? saved, int savedActive)
    {
        if (Loaded) return true;
        if (saved == null) return false;
        Tabs = ChatTab.Load(saved);
        Active = Math.Max(0, Math.Min(Tabs.Count - 1, savedActive));
        Unread = new bool[ChatTab.MaxTabs];
        Loaded = true;
        return true;
    }

    /// <summary>Move a tab (<see cref="ChatTabRow.Move"/>); refused until the tabs are loaded.</summary>
    internal bool Move(int from, int to) => Loaded && ChatTabRow.Move(Tabs, Unread, ref Active, from, to);
}
