using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7: the layout rules of the composed widgets, as numbers and row builders, so the geometry is tested without a
/// client (<c>UiWidgetLayoutTests</c> resolves every row at the pane widths the kit is laid out at and asserts no cell
/// overlaps another or leaves the row, and every caption fits).
///
/// <para>The rules (docs/ui.md "Widgets"): every widget row is a kit cell row on the panel's grid, <see cref="RowHeight"/>
/// tall with the settings list's separator, so a widget lines up with the settings rows above and below it. Small
/// actions are box-less glyph buttons <see cref="IconButtonWidth"/> wide packed at the row's right edge in a fixed order
/// (badge, up, down, expand, remove), so their columns line up from row to row. Text cells take the remainder
/// (<see cref="CellWidth.Fill"/>), and the muted note is the first cell a narrow row drops. A drawer row continues the row
/// above it (<see cref="CellRowSpec.JoinAbove"/>), its text indented to the caption's start.</para>
/// </summary>
internal static class UiWidgetLayout
{
    /// <summary>An ordinary row: the settings grid's row height.</summary>
    internal const float RowHeight = 40f;

    /// <summary>A box-less glyph button (up, down, remove, the grip).</summary>
    internal const float IconButtonWidth = 28f;

    /// <summary>The grip at a reorderable row's left.</summary>
    internal const float GripWidth = 24f;

    /// <summary>A row icon: the kit's icon well, the game item icon inside it.</summary>
    internal const float IconSize = 30f;

    /// <summary>A count badge's narrowest width (one or two digits), under the kit's 48 px side badge.</summary>
    internal const float CountBadgeMinWidth = 30f;

    /// <summary>The narrowest a caption is ever squeezed to.</summary>
    internal const float CaptionMinWidth = 120f;

    /// <summary>A drawer's line pitch and its padding (top and bottom together).</summary>
    internal const float DrawerLineHeight = 21f;

    internal const float DrawerPad = 12f;

    /// <summary>Where a drawer's text starts, from the row's left: under the caption, past the grip and the icon.</summary>
    internal static float DrawerIndent(bool grip, bool icon) =>
        (grip ? GripWidth + CellLayout.Gap : 0f) + (icon ? IconSize + CellLayout.Gap : 0f);

    /// <summary>A drawer row's height for <paramref name="lines"/> lines.</summary>
    internal static float DrawerHeight(int lines) => Math.Max(RowHeight, UiOrderedListModel.DetailLinesFor(lines) * DrawerLineHeight + DrawerPad);

    /// <summary>Glyphs, each with an ASCII fallback for a font without it.</summary>
    internal const string GripGlyph = "≡";
    internal const string GripFallback = "=";
    internal const string UpGlyph = "▲";
    internal const string UpFallback = "^";
    internal const string DownGlyph = "▼";
    internal const string DownFallback = "v";
    internal const string RemoveGlyph = "×";
    internal const string RemoveFallback = "x";
    internal const string CheckGlyph = "√";
    internal const string CheckFallback = "*";
    internal const string ExpandGlyph = "»";

    // ---- Ordered list rows -------------------------------------------------------------------

    internal const string CellGrip = "grip";
    internal const string CellIcon = "icon";
    internal const string CellTitle = "title";
    internal const string CellNote = "note";
    internal const string CellBadge = "badge";
    internal const string CellUp = "up";
    internal const string CellDown = "down";
    internal const string CellExpand = "expand";
    internal const string CellRemove = "remove";
    internal const string CellDetail = "detail";
    internal const string CellCheck = "check";
    internal const string CellReason = "reason";
    internal const string CellIconPad = "icon-pad";

    /// <summary>The cells of one ordered-list row, in the fixed order the columns line up in.</summary>
    internal static List<CellSpec> ListRowCells(bool reorder, bool remove, bool expand, bool icon)
    {
        var cells = new List<CellSpec>(9);
        if (reorder)
            cells.Add(new CellSpec(CellKind.Button, CellGrip)
            {
                MinWidth = GripWidth,
                Skin = CellSkin.Quiet,
                Hover = true,
            }.With(text: GripGlyph, width: CellWidth.Px(GripWidth)));
        if (icon)
            cells.Add(new CellSpec(CellKind.Image, CellIcon) { MinWidth = IconSize, Skin = CellSkin.IconWell }
                .With(width: CellWidth.Px(IconSize)));
        cells.Add(new CellSpec(CellKind.Text, CellTitle).With(width: CellWidth.Fill(CaptionMinWidth), minWidth: CaptionMinWidth));
        cells.Add(new CellSpec(CellKind.Muted, CellNote) { DropOrder = 1 }.With(optional: true));
        cells.Add(new CellSpec(CellKind.Badge, CellBadge).With(tone: CellTone.Client, align: CellAlign.Right, minWidth: CountBadgeMinWidth));
        if (reorder)
        {
            cells.Add(IconButton(CellUp, UpGlyph));
            cells.Add(IconButton(CellDown, DownGlyph));
        }

        if (expand)
            cells.Add(new CellSpec(CellKind.Button, CellExpand) { MinWidth = IconButtonWidth, Skin = CellSkin.Expander }
                .With(text: ExpandGlyph, width: CellWidth.Px(IconButtonWidth), align: CellAlign.Right));
        if (remove) cells.Add(IconButton(CellRemove, RemoveGlyph));
        return cells;
    }

    /// <summary>A drawer row's one wrapping, muted cell, indented under the caption.</summary>
    /// <summary>
    /// A drawer row: its text starts under the caption, past the grip and (when the list shows icons) the icon column.
    /// A kit spacer takes no width of its own (it only shares a row's remainder), so each indent is an empty muted cell
    /// of that width, as CellTiles pads a tile; the icon pad (<see cref="CellIconPad"/>) is hidden while the list shows
    /// no icons, and the cell gaps make the total exactly <see cref="DrawerIndent"/>.
    /// </summary>
    internal static List<CellSpec> DrawerCells(bool grip)
    {
        var cells = new List<CellSpec>(3);
        if (grip)
            cells.Add(new CellSpec(CellKind.Muted, "indent") { MinWidth = GripWidth }.With(text: "", width: CellWidth.Px(GripWidth)));
        cells.Add(new CellSpec(CellKind.Muted, CellIconPad) { MinWidth = IconSize }.With(text: "", width: CellWidth.Px(IconSize)));
        cells.Add(new CellSpec(CellKind.Muted, CellDetail)
            .With(width: CellWidth.Fill(CaptionMinWidth), wrap: CellWrap.Wrap, vAlign: CellVAlign.Top, minWidth: CaptionMinWidth));
        return cells;
    }

    internal static CellSpec IconButton(string key, string glyph) =>
        new CellSpec(CellKind.Button, key) { MinWidth = IconButtonWidth, Skin = CellSkin.Quiet, Hover = true }
            .With(text: glyph, width: CellWidth.Px(IconButtonWidth), align: CellAlign.Right);

    /// <summary>The panel widths every widget row is checked at: the 640-unit floating window's row and the Mods pages'.</summary>
    internal static IReadOnlyList<float> CheckedRowWidths => new[] { 560f, 760f, 1108f, 1248f };
}
