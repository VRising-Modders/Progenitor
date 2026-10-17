using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// CHECKLIST1: the checklist's rows as kit cell specs, and its layout rules as numbers (tested without a client).
///
/// <para>Rules (docs/ui.md "Checklist"): the search row is the game's value box filling the row, then the filter's
/// caption and the game's dropdown bar packed at the right (the compact dropdown's sizes, <see cref="UiCompactChoiceLayout"/>).
/// The header and the body are the data table's (<see cref="UiDataTableLayout"/>): the same column widths, the same
/// packing (text left of and including the fill column from the left, the rest from the right, so numbers line up on
/// their last digit), every line one 40-unit settings-grid row with <see cref="UiDataTableLayout.ScrollInset"/> free for
/// the scroll bar. Every entry line starts with the game's checkbox (<see cref="CellKind.Check"/>), then the icon well or
/// its equal blank, so the check column and the captions line up on every line; the header keeps blanks of the same
/// widths. A section line hides the entry cells and shows the fold arrow and the gold heading instead.</para>
/// </summary>
internal static class UiChecklistLayout
{
    internal const string CellSearch = "search";
    internal const string CellFilterLabel = "filter-label";
    internal const string CellFilter = "filter";
    internal const string CellCheck = UiWidgetLayout.CellCheck;
    internal const string CellFold = "fold";
    internal const string CellSection = "section";
    internal const string CellIcon = UiWidgetLayout.CellIcon;
    internal const string CellIconPad = UiWidgetLayout.CellIconPad;
    internal const string CellView = "view";
    internal const string CellViewPad = "view-pad";
    internal const string CellStatus = "status";
    internal const string CellSelectShown = "select-shown";
    internal const string CellClear = "clear";
    internal const string CellCancel = "cancel";
    internal const string CellConfirm = "confirm";

    /// <summary>The status line's two actions (sentence case).</summary>
    internal const string SelectShownCaption = "Select shown";

    internal const string ClearCaption = "Clear";

    /// <summary>The check column: the game's Options checkbox size.</summary>
    internal const float CheckWidth = CellLayout.CheckWidth;

    /// <summary>A filter chip's key (by the option's place: ids are the caller's words).</summary>
    internal static string ChipKey(int index) => "f:" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The row width the Cancel and Confirm buttons are sized for (the narrowest checked row).</summary>
    internal const float ButtonsBudget = 560f;

    /// <summary>The count the Confirm button is sized for, so a growing count never ellipsizes its caption.</summary>
    internal const int ConfirmSizedFor = 9999;

    /// <summary>The preview button's width: its caption at the button's size and the button's padding.</summary>
    internal static float ViewWidth(string caption) =>
        (float)Math.Ceiling(CellLayout.EstimateTextWidth(caption, MenuPanelLayout.HeaderButtonFontSize) + CellLayout.ButtonPad);

    /// <summary>The search row: the value box (fill), and with filters the caption and the dropdown bar at the right.</summary>
    internal static List<CellSpec> SearchCells(string placeholder, bool filters, string caption, IEnumerable<string> labels)
    {
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Input, CellSearch) { Clearable = true, Skin = CellSkin.ValueBox }
                .With(width: CellWidth.Fill(CellLayout.MinInputWidth), placeholder: string.IsNullOrWhiteSpace(placeholder) ? "Search" : placeholder),
        };
        if (!filters) return cells;
        float label = UiCompactChoiceLayout.LabelWidth(caption);
        if (label > 0f)
            cells.Add(new CellSpec(CellKind.Muted, CellFilterLabel).With(text: caption.Trim(), width: CellWidth.Px(label), minWidth: label,
                align: CellAlign.Right));
        float bar = UiCompactChoiceLayout.BarWidth(labels);
        cells.Add(new CellSpec(CellKind.Button, CellFilter) { MinWidth = bar }
            .With(text: "", width: CellWidth.Px(bar), align: CellAlign.Right, skin: CellSkin.Dropdown));
        return cells;
    }

    /// <summary>The filter's options, open: one chip per option (lit: the selected one), packed into rows of <paramref name="budget"/>.</summary>
    internal static List<List<CellSpec>> ChipRows(IReadOnlyList<UiChecklistFilter> filters, float budget = UiDataTableLayout.ChooserBudget)
    {
        var rows = new List<List<CellSpec>>();
        var row = new List<CellSpec>();
        float used = 0f;
        for (int i = 0; i < filters.Count; i++)
        {
            var chip = InstallModsPane.Chip(ChipKey(i), filters[i].Label, on: i == 0);
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

    private static CellAlign AlignOf(int index, int fill) => index <= fill ? CellAlign.Left : CellAlign.Right;

    private static float ColumnWidth(IReadOnlyList<UiTableColumn> columns, UiTableColumn col, bool fill) =>
        UiDataTableLayout.WidthOf(col, fill) + (UiDataTableLayout.NeedsLeftJustify(columns, col) ? UiDataTableLayout.LeftTextLead : 0f);

    /// <summary>
    /// One body slot row: [check] [fold] [icon | icon pad] the columns [section heading] [view | view pad]. An entry line
    /// shows the check, the icon or its pad, the columns and (with a preview) the view button; a section line shows the
    /// fold arrow and the heading alone.
    /// </summary>
    internal static List<CellSpec> RowCells(IReadOnlyList<UiTableColumn> columns, bool preview, string previewCaption)
    {
        int fill = UiDataTableLayout.FillIndex(columns);
        var cells = new List<CellSpec>(columns.Count + 8)
        {
            new CellSpec(CellKind.Check, CellCheck).With(text: CellLayout.CheckOff, width: CellWidth.Px(CheckWidth), minWidth: CheckWidth),
            new CellSpec(CellKind.Button, CellFold) { MinWidth = UiWidgetLayout.IconButtonWidth, Skin = CellSkin.Expander, Visible = false }
                .With(text: UiWidgetLayout.ExpandGlyph, width: CellWidth.Px(UiWidgetLayout.IconButtonWidth)),
            new CellSpec(CellKind.Image, CellIcon) { MinWidth = UiWidgetLayout.IconSize, Skin = CellSkin.IconWell, Visible = false }
                .With(width: CellWidth.Px(UiWidgetLayout.IconSize)),
            new CellSpec(CellKind.Muted, CellIconPad) { MinWidth = UiWidgetLayout.IconSize, Visible = false }
                .With(text: "", width: CellWidth.Px(UiWidgetLayout.IconSize)),
        };
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            bool isFill = i == fill;
            float w = ColumnWidth(columns, col, isFill);
            cells.Add(new CellSpec(CellKind.Text, UiDataTableLayout.ViewKey(col.Key)) { MinWidth = w }
                .With(text: "", width: isFill ? CellWidth.Fill(w) : CellWidth.Px(w), align: AlignOf(i, fill), optional: col.Hideable && !isFill));
        }

        cells.Add(new CellSpec(CellKind.Text, CellSection) { Visible = false, MinWidth = UiWidgetLayout.CaptionMinWidth }
            .With(text: "", tone: CellTone.Gold, width: CellWidth.Fill(UiWidgetLayout.CaptionMinWidth)));
        if (preview)
        {
            float vw = ViewWidth(previewCaption);
            cells.Add(new CellSpec(CellKind.Button, CellView) { MinWidth = vw, Skin = CellSkin.Quiet, Hover = true }
                .With(text: previewCaption, width: CellWidth.Px(vw), align: CellAlign.Right));
        }

        return cells;
    }

    /// <summary>The header for <see cref="RowCells"/>: blanks as wide as the check, the icon and the view button, and the captions.</summary>
    internal static List<CellSpec> HeaderCells(IReadOnlyList<UiTableColumn> columns, bool preview, string previewCaption)
    {
        int fill = UiDataTableLayout.FillIndex(columns);
        var cells = new List<CellSpec>(columns.Count + 3)
        {
            new CellSpec(CellKind.Text, CellCheck) { MinWidth = CheckWidth }.With(text: "", width: CellWidth.Px(CheckWidth)),
            new CellSpec(CellKind.Text, CellIcon) { MinWidth = UiWidgetLayout.IconSize, Visible = false }.With(text: "", width: CellWidth.Px(UiWidgetLayout.IconSize)),
        };
        for (int i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            bool isFill = i == fill;
            float w = ColumnWidth(columns, col, isFill);
            var cell = new CellSpec(CellKind.Text, UiDataTableLayout.ViewKey(col.Key))
                {
                    MinWidth = w, SmallCaps = true, FontSizeOverride = InstallColumns.HeaderFontSize,
                }
                .With(text: col.Caption, width: isFill ? CellWidth.Fill(w) : CellWidth.Px(w), align: AlignOf(i, fill), optional: col.Hideable && !isFill);
            if (col.Sortable) cell.SortKey = col.Key;
            cells.Add(cell);
        }

        if (preview)
        {
            float vw = ViewWidth(previewCaption);
            cells.Add(new CellSpec(CellKind.Text, CellViewPad) { MinWidth = vw }.With(text: "", width: CellWidth.Px(vw), align: CellAlign.Right));
        }

        return cells;
    }

    /// <summary>The status line: the muted count (fill), then the Select shown and Clear links at the right.</summary>
    internal static List<CellSpec> StatusCells() => new()
    {
        new CellSpec(CellKind.Muted, CellStatus).With(width: CellWidth.Fill(UiWidgetLayout.CaptionMinWidth), minWidth: UiWidgetLayout.CaptionMinWidth),
        LinkCell(CellSelectShown, SelectShownCaption),
        LinkCell(CellClear, ClearCaption),
    };

    private static CellSpec LinkCell(string key, string caption)
    {
        float w = (float)Math.Ceiling(CellLayout.EstimateTextWidth(caption, MenuPanelLayout.HeaderButtonFontSize) + CellLayout.ButtonPad);
        return new CellSpec(CellKind.Button, key) { Skin = CellSkin.Link, Visible = false, MinWidth = w }
            .With(text: caption, width: CellWidth.Px(w), align: CellAlign.Right);
    }

    /// <summary>The Cancel and Confirm buttons: the dialogs' button row (the game's popup buttons, Cancel left, centered).</summary>
    internal static CellRowSpec ButtonRow(string rowKey, string cancel, string confirmFormat) =>
        DialogLayout.ButtonRowSpec(rowKey, new[]
        {
            new DialogButton(CellCancel, string.IsNullOrWhiteSpace(cancel) ? "Cancel" : cancel, DialogButtonRole.Cancel),
            new DialogButton(CellConfirm, UiChecklistModel.ConfirmCaption(confirmFormat, ConfirmSizedFor), DialogButtonRole.Main),
        }, ButtonsBudget);
}
