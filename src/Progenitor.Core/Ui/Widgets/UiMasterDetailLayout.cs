using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7b: the master-detail widget's geometry as numbers and row builders (game-free, <c>UiMasterDetailTests</c>).
///
/// <para><b>One row, two halves.</b> A kit panel is a stack of full-width rows, so the widget has no columns of its own:
/// each of its rows carries the master cells on the left, a thin divider, and the detail cells on the right. Every row
/// has the same master cells with the same widths (a cell's visibility is decided for all rows at once, never per row),
/// so the divider sits at the same x on every row and both halves line up on the 40-unit row grid.</para>
///
/// <para><b>The split.</b> Master: a lead (<see cref="MarkWidth"/>, room for the selection band's stripe), the icon (30, only while some entry has
/// one), the caption (<see cref="TitleShare"/> of the master fraction of the row, clamped to
/// <see cref="TitleMin"/>..<see cref="TitleMax"/>), the note (<see cref="NoteShare"/>, clamped to
/// <see cref="NoteMin"/>..<see cref="NoteMax"/>), the count (<see cref="CountWidth"/>, only while some entry has one), then
/// the divider (<see cref="DividerWidth"/>). The detail takes the rest: its cells are packed from the divider, the member
/// row's count badge and glyph buttons from the row's right edge, and the one fill cell (the text or the link) takes the
/// space between, which also puts a title line's buttons at the right edge. The split is fixed:
/// a draggable divider would need every row's cells re-placed while dragging and has no controller equivalent, and the
/// fraction already adapts to the host's width.</para>
///
/// <para><b>Narrow rows.</b> A detail row that does not fit drops, in order: the member note and count, the grip, the
/// member icon, then the expander. The master cells never drop or shrink at the checked widths, so the divider stays put.</para>
///
/// <para><b>Selection and the divider</b> are not glyphs: the master cells of an entry row are one tile
/// (<see cref="MasterTile"/>) and the divider another (<see cref="DividerTile"/>) of a <see cref="CellRowSpec.TileStates"/>
/// row, so the runtime draws the Mods list's entry band (hover, selected, selected and hovered; <see cref="UiTileLook"/>)
/// behind the master half only, and a 1-unit rule down the divider that joins the rows above and below.</para>
/// </summary>
internal static class UiMasterDetailLayout
{
    internal const float DefaultFraction = 0.32f;
    internal const float MinFraction = 0.2f;
    internal const float MaxFraction = 0.45f;

    /// <summary>The caption's and the note's shares of the master fraction.</summary>
    internal const float TitleShare = 0.7f;

    internal const float NoteShare = 0.3f;

    internal const float TitleMin = UiWidgetLayout.CaptionMinWidth;
    internal const float TitleMax = 300f;
    internal const float NoteMin = 40f;
    internal const float NoteMax = 140f;

    internal const float MarkWidth = 16f;
    internal const float CountWidth = 36f;
    internal const float DividerWidth = 12f;

    internal const int DefaultRows = 14;
    internal const int MinRows = 6;
    internal const int MaxRows = 30;

    /// <summary>Lines one wheel notch moves (the panel's own step is two rows).</summary>
    internal const int LinesPerNotch = 2;

    /// <summary>A detail note's width bounds (estimated from its text).</summary>
    internal const float DetailNoteMin = 40f;

    internal const float DetailNoteMax = 200f;

    /// <summary>The tile number of an entry row's master cells (its selection band) and of every row's divider (its rule).</summary>
    internal const int MasterTile = 1;

    internal const int DividerTile = 2;

    /// <summary>A master count's characters: right-aligned in tabular figures (<see cref="CellKind.Mono"/>) by padding to this many.</summary>
    internal const int CountChars = 3;

    /// <summary>A master count as drawn: right-aligned in its column (<c>"  7"</c>, <c>" 12"</c>, <c>"99+"</c>), cut to <see cref="CountChars"/>.</summary>
    internal static string CountText(string? badge)
    {
        string t = (badge ?? "").Trim();
        if (t.Length > CountChars) t = t.Substring(0, CountChars);
        return t.Length == 0 ? "" : t.PadLeft(CountChars);
    }

    // ---- cell keys ----------------------------------------------------------------------------

    internal const string CellMark = "m-mark";
    internal const string CellMIcon = "m-icon";
    internal const string CellMTitle = "m-title";
    internal const string CellMSearch = "m-search";
    internal const string CellMNote = "m-note";
    internal const string CellMCount = "m-count";
    internal const string CellDivider = "divider";

    internal const string CellIndent = "d-indent";
    internal const string CellGrip = "d-grip";
    internal const string CellDIcon = "d-icon";
    internal const string CellText = "d-text";
    internal const string CellLink = "d-link";
    internal const string CellDNote = "d-note";
    internal const string CellDCount = "d-count";
    internal const string CellUp = "d-up";
    internal const string CellDown = "d-down";
    internal const string CellExpand = "d-expand";
    internal const string CellRemove = "d-remove";

    internal static string CellButton(int i) => "d-btn" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // ---- the split ----------------------------------------------------------------------------

    internal static float FractionFor(float requested) =>
        float.IsNaN(requested) || requested <= 0f ? DefaultFraction
        : requested < MinFraction ? MinFraction
        : requested > MaxFraction ? MaxFraction
        : requested;

    internal static int RowsFor(int requested) =>
        requested <= 0 ? DefaultRows : requested < MinRows ? MinRows : requested > MaxRows ? MaxRows : requested;

    private static float Clamp(float v, float min, float max) => v < min ? min : v > max ? max : v;

    /// <summary>The master caption's width in a row <paramref name="rowWidth"/> wide.</summary>
    internal static float TitleWidth(float rowWidth, float fraction) => Clamp(rowWidth * fraction * TitleShare, TitleMin, TitleMax);

    /// <summary>The master note's width in a row <paramref name="rowWidth"/> wide.</summary>
    internal static float NoteWidth(float rowWidth, float fraction) => Clamp(rowWidth * fraction * NoteShare, NoteMin, NoteMax);

    /// <summary>The master half's width, from the row's left edge to the divider's left edge (the gap before it included).</summary>
    internal static float MasterWidth(float rowWidth, float fraction, bool icons, bool counts)
    {
        float g = CellLayout.Gap;
        float w = MarkWidth + g + TitleWidth(rowWidth, fraction) + g + NoteWidth(rowWidth, fraction) + g;
        if (icons) w += UiWidgetLayout.IconSize + g;
        if (counts) w += CountWidth + g;
        return w;
    }

    /// <summary>Where the detail half starts, from the row's left edge.</summary>
    internal static float DetailLeft(float rowWidth, float fraction, bool icons, bool counts) =>
        MasterWidth(rowWidth, fraction, icons, counts) + DividerWidth + CellLayout.Gap;

    /// <summary>The detail half's width.</summary>
    internal static float DetailWidth(float rowWidth, float fraction, bool icons, bool counts) =>
        Math.Max(0f, rowWidth - DetailLeft(rowWidth, fraction, icons, counts));

    /// <summary>A detail button's width: its caption, never under the kit's button floor.</summary>
    internal static float ButtonWidth(string caption) =>
        Math.Max(CellLayout.MinButtonWidth, CellLayout.EstimateTextWidth(caption, MenuPanelLayout.HeaderButtonFontSize) + CellLayout.ButtonPad);

    /// <summary>A detail note's width: its text, between <see cref="DetailNoteMin"/> and <see cref="DetailNoteMax"/>.</summary>
    internal static float DetailNoteWidth(string note) =>
        Clamp(CellLayout.EstimateTextWidth(note, CellLayout.MutedFontSize) + 4f, DetailNoteMin, DetailNoteMax);

    // ---- rows -----------------------------------------------------------------------------------

    /// <summary>
    /// One row's cells: the master cells (a search field in place of the caption on <paramref name="searchRow"/>), the
    /// divider, then every detail cell (hidden until a line uses it).
    /// </summary>
    internal static List<CellSpec> RowCells(float fraction, bool searchRow, string placeholder)
    {
        float f = FractionFor(fraction);
        var cells = new List<CellSpec>(24)
        {
            new CellSpec(CellKind.Muted, CellMark) { MinWidth = MarkWidth }
                .With(text: "", width: CellWidth.Px(MarkWidth)),
            searchRow
                ? new CellSpec(CellKind.Muted, CellMIcon) { MinWidth = UiWidgetLayout.IconSize, Visible = false }
                    .With(text: "", width: CellWidth.Px(UiWidgetLayout.IconSize))
                : new CellSpec(CellKind.Image, CellMIcon) { MinWidth = UiWidgetLayout.IconSize, Skin = CellSkin.IconWell, Visible = false }
                    .With(width: CellWidth.Px(UiWidgetLayout.IconSize)),
        };

        CellSpec main = searchRow
            ? new CellSpec(CellKind.Input, CellMSearch) { Clearable = true, Skin = CellSkin.ValueBox }
                .With(placeholder: string.IsNullOrEmpty(placeholder) ? "Search" : placeholder)
            : new CellSpec(CellKind.Text, CellMTitle);
        main.Width = CellWidth.Fraction(f * TitleShare);
        main.MinWidth = TitleMin;
        main.MaxWidth = TitleMax;
        cells.Add(main);
        cells.Add(new CellSpec(CellKind.Muted, CellMNote) { MinWidth = NoteMin, MaxWidth = NoteMax }
            .With(text: "", width: CellWidth.Fraction(f * NoteShare)));
        cells.Add(new CellSpec(CellKind.Mono, CellMCount) { MinWidth = CountWidth, Visible = false }
            .With(text: "", width: CellWidth.Px(CountWidth), tone: CellTone.Client));
        if (!searchRow)
            foreach (var c in cells) c.Tile = MasterTile; // the selection band spans the master half
        cells.Add(new CellSpec(CellKind.Muted, CellDivider) { MinWidth = DividerWidth, Tile = DividerTile }
            .With(text: "", width: CellWidth.Px(DividerWidth)));

        // Detail: packed from the divider; the member row's badge and glyph buttons from the right edge; the visible fill
        // cell (text or link) takes the space between.
        cells.Add(new CellSpec(CellKind.Muted, CellIndent) { MinWidth = 1f, Visible = false }.With(text: "", width: CellWidth.Px(1f)));
        cells.Add(new CellSpec(CellKind.Button, CellGrip) { MinWidth = UiWidgetLayout.GripWidth, Skin = CellSkin.Quiet, Hover = true, Visible = false, DropOrder = 2 }
            .With(text: UiWidgetLayout.GripGlyph, width: CellWidth.Px(UiWidgetLayout.GripWidth), optional: true));
        cells.Add(new CellSpec(CellKind.Image, CellDIcon) { MinWidth = UiWidgetLayout.IconSize, Skin = CellSkin.IconWell, Visible = false, DropOrder = 3 }
            .With(width: CellWidth.Px(UiWidgetLayout.IconSize), optional: true));
        cells.Add(new CellSpec(CellKind.Text, CellText) { Visible = false }
            .With(text: "", width: CellWidth.Fill(UiWidgetLayout.CaptionMinWidth), minWidth: UiWidgetLayout.CaptionMinWidth));
        cells.Add(new CellSpec(CellKind.Button, CellLink) { Skin = CellSkin.Link, Visible = false }
            .With(text: "", width: CellWidth.Fill(UiWidgetLayout.CaptionMinWidth), minWidth: UiWidgetLayout.CaptionMinWidth));
        cells.Add(new CellSpec(CellKind.Muted, CellDNote) { MinWidth = DetailNoteMin, Visible = false, DropOrder = 1 }
            .With(text: "", width: CellWidth.Px(DetailNoteMin), optional: true));
        cells.Add(new CellSpec(CellKind.Badge, CellDCount) { Visible = false, DropOrder = 1 }
            .With(tone: CellTone.Client, minWidth: UiWidgetLayout.CountBadgeMinWidth, optional: true));
        cells.Add(Hidden(UiWidgetLayout.IconButton(CellUp, UiWidgetLayout.UpGlyph)));
        cells.Add(Hidden(UiWidgetLayout.IconButton(CellDown, UiWidgetLayout.DownGlyph)));
        var expand = new CellSpec(CellKind.Button, CellExpand) { MinWidth = UiWidgetLayout.IconButtonWidth, Skin = CellSkin.Expander, Visible = false, DropOrder = 4 }
            .With(text: UiWidgetLayout.ExpandGlyph, width: CellWidth.Px(UiWidgetLayout.IconButtonWidth), optional: true);
        cells.Add(expand);
        cells.Add(Hidden(UiWidgetLayout.IconButton(CellRemove, UiWidgetLayout.RemoveGlyph)));
        for (int i = 0; i < UiDetailLine.MaxButtons; i++)
            cells.Add(new CellSpec(CellKind.Button, CellButton(i)) { Skin = CellSkin.PopupButton, Visible = false, MinWidth = CellLayout.MinButtonWidth }
                .With(text: "", width: CellWidth.Px(CellLayout.MinButtonWidth)));

        // Everything but the member row's right-hand group (badge, up, down, expand, remove) sits from the left.
        foreach (var c in cells)
            c.Align = Array.IndexOf(RightKeys, c.Key) >= 0 ? CellAlign.Right : CellAlign.Left;
        return cells;
    }

    private static CellSpec Hidden(CellSpec spec)
    {
        spec.Visible = false;
        return spec;
    }

    /// <summary>The member row's group packed from the row's right edge, in the ordered list's fixed order.</summary>
    internal static readonly string[] RightKeys = { CellDCount, CellUp, CellDown, CellExpand, CellRemove };

    /// <summary>The detail cell keys (everything after the divider).</summary>
    internal static readonly string[] DetailKeys =
    {
        CellIndent, CellGrip, CellDIcon, CellText, CellLink, CellDNote, CellDCount, CellUp, CellDown, CellExpand, CellRemove,
        CellButton(0), CellButton(1), CellButton(2),
    };
}
