using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7b: the data table's rows as kit cell specs, and its layout rules as numbers (tested at every checked width without a
/// client).
///
/// <para>Rules (docs/ui.md "Data table"): every body line is one settings-grid row (<see cref="UiWidgetLayout.RowHeight"/>,
/// the separator rule, the Mods list's gold entry band and stripe for the selection), so a scroll rewrites cells in place and never changes a
/// row's height. The header is the Install list's header (<c>CellHeader</c>, small caps at
/// <see cref="InstallColumns.HeaderFontSize"/>), cell for cell the same widths as the body. Columns run in the caller's
/// order: up to and including the fill column they are packed from the left; after it from the right, so number columns
/// line up on their last digit and every column sits under its caption. A column is never narrower than its caption with
/// the sort mark and its order digit. Hideable columns are optional: a row too narrow for every column drops them from the
/// right; always-shown columns are never dropped. Every row keeps <see cref="ScrollInset"/> free at its right end for the
/// table's own scroll bar.</para>
/// </summary>
internal static class UiDataTableLayout
{
    internal const string CellCheck = UiWidgetLayout.CellCheck;
    internal const string CellIcon = UiWidgetLayout.CellIcon;
    internal const string CellIconPad = UiWidgetLayout.CellIconPad;
    internal const string CellDetail = UiWidgetLayout.CellDetail;
    internal const string CellExpand = UiWidgetLayout.CellExpand;
    internal const string CellExpandPad = "expand-pad";
    internal const string CellCheckPad = "check-pad";
    internal const string CellStatus = "status";
    internal const string CellColumns = "columns";
    internal const string CellReason = "reason";

    /// <summary>A column's view cell (text or tabular figures).</summary>
    internal static string ViewKey(string column) => "c:" + column;

    /// <summary>An editable column's value box.</summary>
    internal static string EditKey(string column) => "e:" + column;

    /// <summary>
    /// A choice column's click target on a row that can change: a box-less button drawn as the option's words in their
    /// tone (<see cref="CellSkin.Choice"/>), as wide as the view cell it replaces.
    /// </summary>
    internal static string ChoiceKey(string column) => "ch:" + column;

    /// <summary>A column chooser chip.</summary>
    internal static string ChipKey(string column) => "col:" + column;

    /// <summary>An actions column's icon button number <paramref name="index"/> (0 the leftmost).</summary>
    internal static string ActionKey(string column, int index) => "a:" + column + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The blank as wide as an action button, drawn where a line has no action there (the columns line up).</summary>
    internal static string ActionPadKey(string column, int index) => "ap:" + column + ":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The most icon buttons an actions column has room for.</summary>
    internal const int MaxActionSlots = 6;

    /// <summary>An actions column's button count: its <see cref="UiTableColumn.ActionSlots"/>, 1 to <see cref="MaxActionSlots"/>.</summary>
    internal static int ActionSlotsOf(UiTableColumn column) =>
        column.ActionSlots < 1 ? 1 : column.ActionSlots > MaxActionSlots ? MaxActionSlots : column.ActionSlots;

    /// <summary>An actions column's width: its buttons (28 units each) and the cell gaps between them.</summary>
    internal static float ActionsWidth(int slots) =>
        Math.Max(1, slots) * UiWidgetLayout.IconButtonWidth + (Math.Max(1, slots) - 1) * CellLayout.Gap;

    /// <summary>The drawn mark of a row action's icon.</summary>
    internal static UiDrawnMark MarkOf(UiRowActionIcon icon) => icon switch
    {
        UiRowActionIcon.Locate => UiDrawnMark.LocateMark,
        UiRowActionIcon.Up => UiDrawnMark.UpMark,
        UiRowActionIcon.Down => UiDrawnMark.DownMark,
        UiRowActionIcon.Remove => UiDrawnMark.RemoveMark,
        UiRowActionIcon.Chest => UiDrawnMark.ChestMark,
        UiRowActionIcon.Check => UiDrawnMark.CheckMark,
        UiRowActionIcon.View => UiDrawnMark.EyeMark,
        UiRowActionIcon.Block => UiDrawnMark.BlockMark,
        UiRowActionIcon.Log => UiDrawnMark.LogMark,
        _ => UiDrawnMark.LocateMark,
    };

    /// <summary>The drawn mark before a choice's words, or none.</summary>
    internal static UiDrawnMark MarkOf(UiTableChoice? choice) => choice?.Icon is { } icon ? MarkOf(icon) : UiDrawnMark.None;

    /// <summary>The room a choice icon takes before the words: the mark at the body text's size and its gap.</summary>
    internal static float ChoiceIconRoom => UiDrawnMarkArt.IconHeight(CellLayout.TextFontSize) + UiDrawnMarkArt.Gap;

    /// <summary>
    /// The width a choice column's longest option needs: its words at the body size, the icon's room when it has one, and
    /// the text's own margins, so no option is ever cut.
    /// </summary>
    internal static float ChoiceNeed(UiTableColumn column)
    {
        float need = 0f;
        foreach (var c in column.Choices)
        {
            if (c == null) continue;
            float w = CellLayout.EstimateTextWidth(c.Text, CellLayout.TextFontSize) + (c.Icon != null ? ChoiceIconRoom : 0f) + ChoiceTextPad;
            if (w > need) need = w;
        }

        return (float)Math.Ceiling(need);
    }

    /// <summary>The text's side margins in a choice cell (both sides together) and a little air.</summary>
    internal const float ChoiceTextPad = 8f;

    /// <summary>
    /// Whether the player may hide a column (the chooser offers it) and a narrow row may drop it:
    /// <see cref="UiTableColumn.Hideable"/>, ignored on an actions column, which is always shown.
    /// </summary>
    internal static bool Hideable(UiTableColumn column) => column.Hideable && column.Kind != UiTableColumnKind.Actions;

    /// <summary>The table's actions column (the first one), or null.</summary>
    internal static UiTableColumn? ActionsColumn(IReadOnlyList<UiTableColumn> columns)
    {
        foreach (var c in columns) if (c.Kind == UiTableColumnKind.Actions) return c;
        return null;
    }

    /// <summary>The right band every table row keeps free for the scroll bar (the bar, its padding and one gap).</summary>
    internal const float ScrollInset = 18f;

    /// <summary>The scroll bar's width inside <see cref="ScrollInset"/> (the game's thin bar, as the README reader's).</summary>
    internal const float ScrollBarWidth = PanelLayout.ScrollbarWidth;

    /// <summary>The narrowest editable number column: the value box with five digits.</summary>
    internal const float MinEditWidth = 84f;

    /// <summary>The narrowest number column.</summary>
    internal const float MinNumberWidth = 64f;

    /// <summary>The narrowest text column that is not the fill column.</summary>
    internal const float MinTextWidth = CellLayout.MinTextWidth;

    /// <summary>The Columns button under the table.</summary>
    internal const float ColumnsButtonWidth = 190f;

    /// <summary>The panel width the column chooser's chips are packed for (the narrowest checked row).</summary>
    internal const float ChooserBudget = 560f;

    /// <summary>The waiting spinner's frames per second (a drawn mark, <see cref="UiDrawnMark.Spinner"/>).</summary>
    internal const double SpinnerRate = 4.0;

    /// <summary>The spinner frame shown <paramref name="seconds"/> after the clock's start (0–3, four a second).</summary>
    internal static int SpinnerFrame(double seconds) =>
        double.IsNaN(seconds) || seconds < 0 ? 0 : (int)((long)(seconds * SpinnerRate) % 4);

    /// <summary>
    /// Which column fills (and so where right-packing starts): the first with no width, else the first text column; but
    /// never one after a number column, so every number column is packed from the right and its figures line up on their
    /// last digit. With a number column first, the first column. An actions column never fills.
    /// </summary>
    internal static int FillIndex(IReadOnlyList<UiTableColumn> columns)
    {
        int firstNumber = columns.Count;
        for (int i = 0; i < columns.Count; i++)
            if (columns[i].Kind == UiTableColumnKind.Number) { firstNumber = i; break; }
        for (int i = 0; i < firstNumber; i++)
            if (columns[i].Width <= 0f && columns[i].Kind != UiTableColumnKind.Actions) return i;
        for (int i = 0; i < firstNumber; i++) if (columns[i].Kind == UiTableColumnKind.Text) return i;
        return 0;
    }

    /// <summary>
    /// The width a header caption needs: its small caps and, on a sortable column, the room of the widest drawn sort mark
    /// (the arrow and an order digit, <see cref="UiDrawnMarkArt.SortRoom"/>), so a sort never makes a caption ellipsize.
    /// </summary>
    internal static float HeaderNeed(UiTableColumn column) =>
        InstallColumns.HeaderCaptionWidth(column.Caption, false, smallCaps: true, InstallColumns.HeaderFontSize)
        + TermRoom(column)
        + (column.Sortable ? UiDrawnMarkArt.SortRoom(InstallColumns.HeaderFontSize) : 0f);

    /// <summary>
    /// The room a column's term marker takes after its caption (<see cref="UiTermMarkerArt"/>: its side at the header's
    /// size and its gap), 0 without a term. The order after a caption is: the caption, the term marker, the sort mark.
    /// </summary>
    internal static float TermRoom(UiTableColumn column) =>
        string.IsNullOrWhiteSpace(column.Term) ? 0f : UiTermMarkerArt.SizeFor(InstallColumns.HeaderFontSize) + UiTermMarkerArt.Gap;

    /// <summary>
    /// The sort mark a column's caption carries for <paramref name="sort"/>: none when it is not a key; the arrow alone with
    /// one key; the arrow and the key's order (1, 2 …) with two keys or more.
    /// </summary>
    internal static UiDrawnMark SortMark(IReadOnlyList<UiTableSortKey> sort, string column)
    {
        for (int k = 0; k < sort.Count; k++)
        {
            if (!string.Equals(sort[k].Column, column, StringComparison.Ordinal)) continue;
            return UiDrawnMark.Sort(sort[k].Ascending, sort.Count >= 2 ? k + 1 : 0);
        }

        return UiDrawnMark.None;
    }

    /// <summary>
    /// A text column packed from the right (after the fill column) whose text must still read left-justified (the
    /// runtime justifies it apart from the packing). Numbers are right-justified everywhere.
    /// </summary>
    /// <summary>
    /// The room a left-justified text column after a right-justified one keeps before its text (header and body), so its
    /// caption never butts against the previous column's caption, term marker or sort mark.
    /// </summary>
    internal const float LeftTextLead = 12f;

    internal static bool NeedsLeftJustify(IReadOnlyList<UiTableColumn> columns, UiTableColumn column)
    {
        if (column.Kind != UiTableColumnKind.Text && column.Kind != UiTableColumnKind.Choice) return false;
        int fill = FillIndex(columns);
        for (int i = 0; i < columns.Count; i++)
            if (ReferenceEquals(columns[i], column)) return i > fill;
        return false;
    }

    /// <summary>
    /// Whether <paramref name="text"/> is cut by its column (a fixed-width text column; the fill column only ever grows):
    /// with <see cref="UiDataTableOptions.OverflowTooltips"/> the row's tooltip then shows the value whole. Text cells never wrap: every body line is one fixed-height row.
    /// </summary>
    internal static bool IsCut(IReadOnlyList<UiTableColumn> columns, UiTableColumn column, string text)
    {
        if (column.Kind != UiTableColumnKind.Text || string.IsNullOrEmpty(text)) return false;
        int fill = FillIndex(columns);
        for (int i = 0; i < columns.Count; i++)
        {
            if (!ReferenceEquals(columns[i], column)) continue;
            if (i == fill) return false;
            var probe = new CellSpec(CellKind.Text, "probe").With(text: text);
            return !CellLayout.CaptionFits(probe, WidthOf(column, fill: false));
        }

        return false;
    }

    /// <summary>
    /// The room a term marker keeps after a caption that also carries a drawn sort mark: its side and gap when the marker
    /// is wanted (its term defined and not hidden by the player), whether or not it is drawn this frame. A marker is drawn
    /// only where it fits, so making the room depend on the marker being drawn left a header built while hidden (a tab not
    /// yet shown) with no room and no marker until a later redraw.
    /// </summary>
    internal static float TermRoomFor(bool wanted, float fontSize) =>
        wanted ? UiTermMarkerArt.SizeFor(fontSize) + UiTermMarkerArt.Gap : 0f;

    /// <summary>The word a pending clear shows (sentence case, the value box's gray hint look): back to the inherited value.</summary>
    internal const string InheritedHint = "Inherited";

    /// <summary>The row tooltip's words for an inherited cell: <c>Reserve 200: inherited from category Ore</c>.</summary>
    internal static string InheritedLine(UiTableColumn column, UiTableCell cell)
    {
        string value = UiDataTableModel.Display(cell, column);
        string from = string.IsNullOrWhiteSpace(cell.Source) ? "inherited" : "inherited from " + cell.Source.Trim();
        return column.Caption + (value.Length > 0 ? " " + value : "") + ": " + from;
    }

    /// <summary>A column's width: its ask, never under its caption's need or its kind's floor. The fill column's floor.</summary>
    internal static float WidthOf(UiTableColumn column, bool fill)
    {
        // The buttons alone decide an actions column's width, so its header lines up with them exactly.
        if (column.Kind == UiTableColumnKind.Actions) return ActionsWidth(ActionSlotsOf(column));
        float need = HeaderNeed(column);
        if (fill) return Math.Max(Math.Max(UiWidgetLayout.CaptionMinWidth, need), column.Kind == UiTableColumnKind.Choice ? ChoiceNeed(column) : 0f);
        float floor = column.Editable && column.Kind == UiTableColumnKind.Number ? MinEditWidth
            : column.Kind == UiTableColumnKind.Number ? MinNumberWidth
            : column.Kind == UiTableColumnKind.Choice ? Math.Max(MinTextWidth, ChoiceNeed(column)) : MinTextWidth;
        return Math.Max(Math.Max(column.Width, floor), need);
    }

    private static CellAlign AlignOf(int index, int fill) => index <= fill ? CellAlign.Left : CellAlign.Right;

    private static CellWidth WidthSpec(UiTableColumn column, bool fill, float width) =>
        fill ? CellWidth.Fill(width) : CellWidth.Px(width);

    /// <summary>
    /// One body slot row: [check] [icon | icon pad] the columns (a view cell, and a value box for an editable one) [the
    /// drawer text] [expand | expand pad]. Which of each pair shows is decided per line; both of a pair are as wide, so the
    /// columns line up whichever is drawn.
    /// </summary>
    internal static List<CellSpec> RowCells(IReadOnlyList<UiTableColumn> columns, bool multi)
    {
        int fill = FillIndex(columns);
        var cells = new List<CellSpec>(columns.Count * 2 + 6);
        if (multi)
        {
            cells.Add(new CellSpec(CellKind.Button, CellCheck) { MinWidth = UiWidgetLayout.IconButtonWidth, Skin = CellSkin.Quiet }
                .With(text: "", width: CellWidth.Px(UiWidgetLayout.IconButtonWidth)));
            // A filler row draws this blank instead of the check cell (no dead click or navigation stop), as wide.
            cells.Add(new CellSpec(CellKind.Muted, CellCheckPad) { MinWidth = UiWidgetLayout.IconButtonWidth, Visible = false }
                .With(text: "", width: CellWidth.Px(UiWidgetLayout.IconButtonWidth)));
        }
        cells.Add(new CellSpec(CellKind.Image, CellIcon) { MinWidth = UiWidgetLayout.IconSize, Skin = CellSkin.IconWell }
            .With(width: CellWidth.Px(UiWidgetLayout.IconSize)));
        cells.Add(new CellSpec(CellKind.Muted, CellIconPad) { MinWidth = UiWidgetLayout.IconSize, Visible = false }
            .With(text: "", width: CellWidth.Px(UiWidgetLayout.IconSize)));
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            bool isFill = i == fill;
            float w = WidthOf(col, isFill) + (NeedsLeftJustify(columns, col) ? LeftTextLead : 0f);
            var align = AlignOf(i, fill);
            bool optional = Hideable(col) && !isFill;
            if (col.Kind == UiTableColumnKind.Actions)
            {
                // Each place is a pair, as the icon and the expander: the box-less icon button (its face a drawn mark,
                // UiIconMarks) or a blank as wide, so a line without that action keeps every button in its column.
                int n = ActionSlotsOf(col);
                for (int a = 0; a < n; a++)
                {
                    var button = UiWidgetLayout.IconButton(ActionKey(col.Key, a));
                    button.Align = align;
                    button.Optional = optional;
                    cells.Add(button);
                    cells.Add(new CellSpec(CellKind.Muted, ActionPadKey(col.Key, a)) { MinWidth = UiWidgetLayout.IconButtonWidth, Visible = false }
                        .With(text: "", width: CellWidth.Px(UiWidgetLayout.IconButtonWidth), align: align, optional: optional));
                }

                continue;
            }

            // Numbers are proportional body text, right-justified: the monospace run spaced the thousands separator
            // ("266 , 400"); the game's figures are lining, and right-justified they line up on their last digit.
            var kind = CellKind.Text;
            cells.Add(new CellSpec(kind, ViewKey(col.Key)) { MinWidth = w }
                .With(text: "", width: WidthSpec(col, isFill, w), align: align, optional: optional));
            if (col.Editable && col.Kind == UiTableColumnKind.Number)
            {
                // A single space keeps the field's hint object: an inherited value is drawn as the hint (gray).
                cells.Add(new CellSpec(CellKind.Input, EditKey(col.Key)) { MinWidth = w, Skin = CellSkin.ValueBox, Visible = false, MaxLength = 18 }
                    .With(text: "", width: WidthSpec(col, isFill, w), align: align, optional: optional, placeholder: " "));
            }

            if (col.Editable && col.Kind == UiTableColumnKind.Choice)
            {
                // The click target that replaces the view cell on a row that can change: the same words, the same place.
                cells.Add(new CellSpec(CellKind.Button, ChoiceKey(col.Key)) { MinWidth = w, Skin = CellSkin.Choice, Visible = false, FontSizeOverride = CellLayout.TextFontSize }
                    .With(text: "", width: WidthSpec(col, isFill, w), align: align, optional: optional));
            }
        }

        cells.Add(new CellSpec(CellKind.Muted, CellDetail) { Visible = false, MinWidth = UiWidgetLayout.CaptionMinWidth }
            .With(width: CellWidth.Fill(UiWidgetLayout.CaptionMinWidth)));
        cells.Add(new CellSpec(CellKind.Button, CellExpand) { MinWidth = UiWidgetLayout.IconButtonWidth, Skin = CellSkin.Expander }
            .With(text: UiWidgetLayout.ExpandGlyph, width: CellWidth.Px(UiWidgetLayout.IconButtonWidth), align: CellAlign.Right));
        cells.Add(new CellSpec(CellKind.Muted, CellExpandPad) { MinWidth = UiWidgetLayout.IconButtonWidth, Visible = false }
            .With(text: "", width: CellWidth.Px(UiWidgetLayout.IconButtonWidth), align: CellAlign.Right));
        return cells;
    }

    /// <summary>The header for <see cref="RowCells"/>: the same keys and widths, small caps captions, sortable ones clickable.</summary>
    internal static List<CellSpec> HeaderCells(IReadOnlyList<UiTableColumn> columns, bool multi)
    {
        int fill = FillIndex(columns);
        var cells = new List<CellSpec>(columns.Count + 3);
        if (multi)
            cells.Add(new CellSpec(CellKind.Text, CellCheck) { MinWidth = UiWidgetLayout.IconButtonWidth }
                .With(text: "", width: CellWidth.Px(UiWidgetLayout.IconButtonWidth)));
        cells.Add(new CellSpec(CellKind.Text, CellIcon) { MinWidth = UiWidgetLayout.IconSize }
            .With(text: "", width: CellWidth.Px(UiWidgetLayout.IconSize)));
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            bool isFill = i == fill;
            float w = WidthOf(col, isFill) + (NeedsLeftJustify(columns, col) ? LeftTextLead : 0f);
            var cell = new CellSpec(CellKind.Text, ViewKey(col.Key)) { MinWidth = w, SmallCaps = true, FontSizeOverride = InstallColumns.HeaderFontSize }
                .With(text: col.Caption, width: WidthSpec(col, isFill, w), align: AlignOf(i, fill), optional: Hideable(col) && !isFill);
            if (col.Sortable && col.Kind != UiTableColumnKind.Actions) cell.SortKey = col.Key;
            cells.Add(cell);
        }

        cells.Add(new CellSpec(CellKind.Text, CellExpand) { MinWidth = UiWidgetLayout.IconButtonWidth }
            .With(text: "", width: CellWidth.Px(UiWidgetLayout.IconButtonWidth), align: CellAlign.Right));
        return cells;
    }

    /// <summary>The footer: the muted status line and the Columns button (the game's dropdown bar) at its right.</summary>
    internal static List<CellSpec> FooterCells(bool chooser, int hideable = 9)
    {
        float button = ColumnsButtonWidthFor(hideable);
        return new List<CellSpec>
        {
            new CellSpec(CellKind.Muted, CellStatus).With(width: CellWidth.Fill(UiWidgetLayout.CaptionMinWidth), minWidth: UiWidgetLayout.CaptionMinWidth),
            new CellSpec(CellKind.Button, CellColumns) { MinWidth = button, Visible = chooser }
                .With(text: "Columns", width: CellWidth.Px(button), align: CellAlign.Right, skin: CellSkin.Dropdown),
        };
    }

    /// <summary>The dropdown bar's arrow and its padding, right of the caption.</summary>
    internal const float DropdownArrowRoom = 34f;

    /// <summary>
    /// The Columns button's width: its longest caption (every hideable column hidden) at the button's size, the button's
    /// padding and the dropdown arrow, never under <see cref="ColumnsButtonWidth"/>, so no caption ever ellipsizes.
    /// </summary>
    internal static float ColumnsButtonWidthFor(int hideable)
    {
        string longest = ColumnsCaption(Math.Max(1, hideable));
        float need = CellLayout.EstimateTextWidth(longest, MenuPanelLayout.HeaderButtonFontSize) + CellLayout.ButtonPad + DropdownArrowRoom;
        return Math.Max(ColumnsButtonWidth, (float)Math.Ceiling(need));
    }

    /// <summary>The refusal line under the footer: one orange sentence.</summary>
    internal static List<CellSpec> ReasonCells() => new()
    {
        new CellSpec(CellKind.Text, CellReason).With(width: CellWidth.Fill(UiWidgetLayout.CaptionMinWidth), tone: CellTone.Orange,
            minWidth: UiWidgetLayout.CaptionMinWidth),
    };

    /// <summary>
    /// The column chooser: one filter chip per hideable column (the Install tab's chips: lit while the column shows), packed
    /// into rows that fit <paramref name="budget"/>.
    /// </summary>
    internal static List<List<CellSpec>> ChooserRows(IReadOnlyList<UiTableColumn> columns, float budget = ChooserBudget)
    {
        var rows = new List<List<CellSpec>>();
        var row = new List<CellSpec>();
        float used = 0f;
        foreach (var col in columns)
        {
            var chip = InstallModsPane.Chip(ChipKey(col.Key), col.Caption, on: !col.Hidden);
            float w = chip.Width.Value;
            float need = row.Count == 0 ? w : used + CellLayout.Gap + w;
            if (row.Count > 0 && need > budget)
            {
                rows.Add(row);
                row = new List<CellSpec>();
                need = w;
            }

            row.Add(chip);
            used = need;
        }

        if (row.Count > 0) rows.Add(row);
        return rows;
    }

    /// <summary>The Columns button caption: how many are hidden.</summary>
    internal static string ColumnsCaption(int hidden) => hidden <= 0 ? "Columns" : "Columns · " + hidden + " hidden";
}
