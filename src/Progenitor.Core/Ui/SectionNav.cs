using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Ui;

/// <summary>
/// Three homes (docs/settings-organization.md, "Layout rules"): the left list of a tab's groups. A click scrolls the
/// content to the group (opening it when it is a closed drawer) and highlights the item; the highlight then follows the
/// scroll: the group whose heading is at, or last passed, the top of the view. Game-free.
/// </summary>
internal static class SectionNav
{
    /// <summary>A left-list row's key prefix: <c>nav/&lt;slug&gt;</c> (<c>widget_click label=nav/sources-and-downloads</c>).</summary>
    internal const string KeyPrefix = "nav/";

    /// <summary>Readback key of a left-list row: <c>sectionNav {tab, group, slug, highlighted, jumped}</c>.</summary>
    internal const string RowSectionNav = "sectionNav";

    /// <summary>
    /// How far below the view's top edge a heading may sit and still count as "at the top" (canvas units): a heading the
    /// jump put on the top edge lands a few units low after the layout settles.
    /// </summary>
    internal const float TopTolerance = 24f;

    /// <summary>A group's word for keys, the history and the bridge: lower case, words joined by <c>-</c>.</summary>
    internal static string Slug(string? text)
    {
        var sb = new StringBuilder();
        bool dash = false;
        foreach (char c in (text ?? "").Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                if (dash && sb.Length > 0) sb.Append('-');
                sb.Append(c);
                dash = false;
            }
            else dash = true;
        }

        return sb.ToString();
    }

    /// <summary>The left-list row's key for <paramref name="group"/>.</summary>
    internal static string Key(string? group) => KeyPrefix + Slug(group);

    /// <summary>A left-list row under a mod in the Mods list (Progenitor's page's groups) is set in this far.</summary>
    internal const float SubRowIndent = 28f;

    internal const float SubRowHeight = 36f;

    /// <summary>
    /// A left-list row: the Mods list's row look (MENUKIT2 round D), keyed <c>nav/&lt;slug&gt;</c>. A sub-row (Progenitor's
    /// groups under its entry in the Mods list) is set in and a little shorter.
    /// </summary>
    internal static CellRowSpec Row(string group, float rowWidth, bool subRow = false)
    {
        float indent = subRow ? SubRowIndent : 0f;
        float width = Math.Max(CellLayout.MinTextWidth, rowWidth - MenuPalette.SelectedStripeWidth - 8f - indent);
        var cells = new List<CellSpec>(2)
        {
            new CellSpec(subRow ? CellKind.Muted : CellKind.Text, ModsListLayout.CellName).With(
                text: group ?? "", align: CellAlign.Left, width: CellWidth.Px(width), minWidth: CellLayout.MinTextWidth),
            new CellSpec(CellKind.Spacer, "gap"),
        };
        return new CellRowSpec(Key(group), cells, height: subRow ? SubRowHeight : ModsListLayout.NavRowHeight, soft: true, indent: indent);
    }

    /// <summary>
    /// SERVEREMPTY1: a left-list row shown dimmed (the Server tab's groups while it draws none): the same look and key as
    /// <see cref="Row"/>, its words in the muted tone, and a plain row, so it never lights under the pointer, takes no click
    /// and is never highlighted.
    /// </summary>
    internal static CellRowSpec DimmedRow(string group, float rowWidth)
    {
        float width = Math.Max(CellLayout.MinTextWidth, rowWidth - MenuPalette.SelectedStripeWidth - 8f);
        var cells = new List<CellSpec>(2)
        {
            new CellSpec(CellKind.Muted, ModsListLayout.CellName).With(
                text: group ?? "", align: CellAlign.Left, width: CellWidth.Px(width), minWidth: CellLayout.MinTextWidth, tone: CellTone.Muted),
            new CellSpec(CellKind.Spacer, "gap"),
        };
        return new CellRowSpec(Key(group), cells, height: ModsListLayout.NavRowHeight, plain: true);
    }

    /// <summary>
    /// The group in <paramref name="groups"/> that <paramref name="nameOrSlug"/> names (its caption, any case, or its slug),
    /// or null. A bridge label <c>nav/&lt;group&gt;</c> is accepted with its prefix.
    /// </summary>
    internal static string? Resolve(IReadOnlyList<string>? groups, string? nameOrSlug)
    {
        if (groups == null || string.IsNullOrWhiteSpace(nameOrSlug)) return null;
        string n = nameOrSlug!.Trim();
        if (n.StartsWith(KeyPrefix, StringComparison.OrdinalIgnoreCase)) n = n.Substring(KeyPrefix.Length);
        string slug = Slug(n);
        foreach (string g in groups)
            if (string.Equals(g, n, StringComparison.OrdinalIgnoreCase) || string.Equals(Slug(g), slug, StringComparison.Ordinal))
                return g;
        // LISTING1: a renamed group still answers to its old name's slug (bridge recipes and saved scripts).
        foreach (var (old, now) in RenamedGroups)
            if (string.Equals(slug, old, StringComparison.Ordinal))
                foreach (string g in groups)
                    if (string.Equals(g, now, StringComparison.Ordinal)) return g;
        return null;
    }

    /// <summary>LISTING1: groups renamed since the bridge first named them: (old slug, new title).</summary>
    internal static readonly IReadOnlyList<(string OldSlug, string Title)> RenamedGroups = new[]
    {
        ("server-list", ProgenitorPage.ServerListTitle),
    };

    /// <summary>
    /// The group at the top of the view: the last heading whose top (<paramref name="headingTops"/>, units below the
    /// view's top edge; negative above it; null when not drawn or hidden) is at most <see cref="TopTolerance"/> below the
    /// top. Before the first heading reaches the top, the first drawn group. -1 when no heading is drawn.
    /// </summary>
    internal static int ActiveIndex(IReadOnlyList<float?> headingTops, float tolerance = TopTolerance)
    {
        if (headingTops == null) return -1;
        int first = -1, active = -1;
        for (int i = 0; i < headingTops.Count; i++)
        {
            if (headingTops[i] is not { } top) continue;
            if (first < 0) first = i;
            if (top <= tolerance) active = i;
        }

        return active >= 0 ? active : first;
    }
}

/// <summary>
/// Three homes: one tab's left-list highlight. A jump highlights its group at once and holds it while the jump's own
/// scroll settles (<see cref="SettleFrames"/>): a group near the end of a short page cannot reach the top, and must not
/// hand the highlight to the group above it. The first scroll after that follows the view again. Game-free.
/// </summary>
internal sealed class SectionNavTracker
{
    /// <summary>Frames after a jump in which a moving offset is the jump's own scroll, not the player's.</summary>
    internal const int SettleFrames = 10;

    private string? _pinned;
    private int _pinnedFrame;
    private float? _pinnedOffset;
    private float? _lastOffset;

    /// <summary>The highlighted group, or empty.</summary>
    internal string Highlighted { get; private set; } = "";

    /// <summary>The group the last jump asked for, or empty (the history's place).</summary>
    internal string Jumped { get; private set; } = "";

    /// <summary>True while a jump holds the highlight.</summary>
    internal bool Pinned => _pinned != null;

    /// <summary>A jump to <paramref name="group"/> on frame <paramref name="frame"/>: highlighted until the player scrolls.</summary>
    internal void Jump(string group, int frame)
    {
        _pinned = group ?? "";
        _pinnedFrame = frame;
        _pinnedOffset = null;
        Highlighted = group ?? "";
        Jumped = group ?? "";
    }

    /// <summary>Forget the jump and the highlight (the tab was rebuilt for another view).</summary>
    internal void Reset()
    {
        _pinned = null;
        _pinnedOffset = null;
        _lastOffset = null;
        Highlighted = "";
        Jumped = "";
    }

    /// <summary>
    /// The view's scroll offset on frame <paramref name="frame"/>. True when the caller should work out the group at the
    /// top (<see cref="Follow"/>): the offset moved, and no jump holds the highlight any more.
    /// </summary>
    internal bool Observe(float offset, int frame)
    {
        bool moved = _lastOffset is not { } last || Math.Abs(last - offset) > 0.5f;
        _lastOffset = offset;
        if (_pinned != null)
        {
            if (frame - _pinnedFrame <= SettleFrames)
            {
                _pinnedOffset = offset;
                return false;
            }

            if (_pinnedOffset is not { } held) _pinnedOffset = held = offset;
            if (Math.Abs(held - offset) <= 0.5f) return false;
            _pinned = null;
            return true;
        }

        return moved;
    }

    /// <summary>The scroll says <paramref name="group"/> is at the top. True when the highlight changed.</summary>
    internal bool Follow(string? group)
    {
        if (_pinned != null || string.IsNullOrEmpty(group)) return false;
        if (string.Equals(Highlighted, group, StringComparison.Ordinal)) return false;
        Highlighted = group!;
        return true;
    }

    internal Dictionary<string, object?> Readback() => new(StringComparer.Ordinal)
    {
        ["highlighted"] = Highlighted,
        ["jumped"] = Jumped,
        ["pinned"] = Pinned,
        ["offset"] = _lastOffset.HasValue ? Math.Round(_lastOffset.Value, 1).ToString(CultureInfo.InvariantCulture) : null,
    };
}
