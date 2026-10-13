using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// Where each panel row goes and how tall the panel has to be. Game-free, so the arithmetic that
/// was wrong has tests.
///
/// <para>
/// <b>Why this exists (evidence 163).</b> The panel was a fixed 640x360 frame and every row was
/// placed at a constant 48 px step from a fixed start, with no clipping. Six rows fit. The
/// settings panel lists every keybind and every non-synced config entry of every loaded plugin —
/// 32 rows with two plugins loaded, and it grows with each one — so rows 7 onward were drawn
/// outside the frame, over the world, down past the bottom of the screen. Worse, the step was
/// constant while a row's label wraps to two or three lines, so the rows that were on screen
/// overlapped each other into unreadable text. A settings panel that cannot show its own content
/// is not a landed feature.
/// </para>
///
/// <para>
/// <b>The model.</b> Rows live in a <c>content</c> rect that is top-anchored inside a clipped
/// <c>viewport</c>, and the cursor measures downward from the content's top edge. Each row
/// reserves its own measured height rather than a constant, so a three-line description pushes
/// the next row down by three lines. The panel then sizes itself to its content up to a fraction
/// of the screen, and anything past that scrolls instead of overflowing.
/// </para>
///
/// <para>
/// <b>Why the cursor rewinds (R43 finding 4).</b> The first cut ran the cursor once, as rows were
/// created, and never revisited it. That is only correct while a row's text never changes — and
/// the shipped rebind flow rewrites a row's text at runtime, appending the reason a chord was
/// refused to a label that was measured without it. The taller label then drew over the row below:
/// evidence 163's overlap, on a panel that had otherwise been fixed, at the exact moment the
/// player was being told why their rebind failed. So the layout is not a one-shot: the caller
/// keeps its rows in order, and a re-layout <see cref="Reset"/>s this cursor and walks every row
/// again with a fresh measurement. Only the arithmetic lives here; the row list and the Unity
/// calls stay with the caller.
/// </para>
/// </summary>
internal sealed class PanelLayout
{
    /// <summary>Band at the top of the panel reserved for the title; the viewport starts below it.</summary>
    internal const float HeaderHeight = 72f;

    /// <summary>Gap between the top of the content and the first row.</summary>
    internal const float PadTop = 8f;

    /// <summary>Gap after the last row, so the final row does not touch the frame.</summary>
    internal const float PadBottom = 16f;

    /// <summary>Left and right inset of the viewport inside the panel frame.</summary>
    internal const float PadSide = 20f;

    /// <summary>
    /// Width of the vertical scrollbar, and the reason <see cref="PadSide"/> is as wide as it is:
    /// the bar sits in the right-hand inset, so it never overlaps a row.
    /// </summary>
    internal const float ScrollbarWidth = 8f;

    /// <summary>Gap between the scrollbar and the panel's right edge.</summary>
    internal const float ScrollbarInset = 4f;

    /// <summary>Vertical gap between two rows.</summary>
    internal const float RowGap = 8f;

    /// <summary>A row is never shorter than one line's worth of control.</summary>
    internal const float MinRowHeight = UnityUiRuntimeMetrics.RowHeight;

    /// <summary>
    /// A row is never taller than this, and past it the caption's own text is truncated with an
    /// ellipsis so the clamp is a real boundary rather than only a smaller reservation.
    ///
    /// <para>
    /// The write is <c>UnityUiRuntime.ClampCaptionText</c>, called from <c>FitLabel</c> — the one
    /// place every row's measured caption passes through, on every layout pass. It used to be in
    /// <c>MakeLabel</c>, which is four of the five caption factories: a button row's caption is
    /// the TMP its cloned vanilla donor brought with it, and that path kept TMP's default
    /// <c>Overflow</c> and painted a long caption straight over the rows beneath it (R45
    /// finding 10). A rule stated here as universal has to be written somewhere universal.
    /// </para>
    ///
    /// <para>
    /// Raised from 200 px in evidence 167. Real config descriptions in this game are long —
    /// Progenitor's own <c>HolderScanWide</c> and <c>SkipCoreHarmony</c> entries run to ten lines
    /// — and 200 px truncated most of them. A tall row costs nothing now that the panel scrolls;
    /// the cap exists only so one pathological entry cannot fill the viewport and push every
    /// other row out of reach.
    /// </para>
    /// </summary>
    internal const float MaxRowHeight = 400f;

    /// <summary>
    /// INSTALLSIDES1 live fix: the cap for a cell row that states its own fixed height (the Install
    /// drawer's scrolling body, sized to about 80% of the list viewport). Such a row clips its own
    /// text, so it reserves exactly the height it is drawn at; <see cref="MaxRowHeight"/> would
    /// reserve less than it draws and the rows after it would slide under it (the 2026-10-03 live
    /// overlap). This bound only stops an absurd value.
    /// </summary>
    internal const float MaxFixedRowHeight = 2000f;

    /// <summary>
    /// Width of the column an editable row's control sits in, at the right-hand end of the row.
    /// See <see cref="Columns"/>.
    /// </summary>
    internal const float ControlColumnWidth = 180f;

    /// <summary>Clear space between the label column and the control column.</summary>
    internal const float ColumnGap = 12f;

    /// <summary>The panel never shrinks below this, however few rows it has.</summary>
    internal const float MinPanelHeight = 200f;

    /// <summary>The panel never grows past this share of the screen; past it, the content scrolls.</summary>
    internal const float ScreenFraction = 0.8f;

    /// <summary>Screen height assumed when the real one cannot be read.</summary>
    internal const float FallbackScreenHeight = 1080f;

    private float _cursor = PadTop;

    /// <summary>How many rows have been reserved.</summary>
    internal int RowCount { get; private set; }

    /// <summary>
    /// Reserve a row <paramref name="rowHeight"/> tall and return the distance from the content's
    /// top edge to that row's own top edge — the value a top-anchored, top-pivoted rect wants
    /// negated as its anchored Y.
    /// </summary>
    internal float NextRow(float rowHeight) => NextRow(rowHeight, MinRowHeight);

    /// <summary>
    /// DIALOGS1: <see cref="NextRow(float)"/> with its own floor — a dialog's title and body rows are
    /// as tall as their text, not the ordinary 40-unit row (<see cref="DialogLayout.TextRowHeight(float, bool, float)"/>).
    /// </summary>
    internal float NextRow(float rowHeight, float minHeight) => NextRow(rowHeight, minHeight, MaxRowHeight);

    /// <summary>
    /// <see cref="NextRow(float, float)"/> with its own cap: <see cref="MaxRowHeight"/> for measured
    /// rows, the row's own height (up to <see cref="MaxFixedRowHeight"/>) for a fixed cell row.
    /// </summary>
    internal float NextRow(float rowHeight, float minHeight, float maxHeight) => NextRow(rowHeight, minHeight, maxHeight, -1f);

    /// <summary>
    /// RADIOCLICK1: the least height the panel reserves for a cell row: <see cref="MinRowHeight"/>, or 1 for a
    /// <see cref="CellRowSpec.Compact"/> row (it reserves exactly what it asks for).
    /// </summary>
    internal static float CellRowFloor(CellRowSpec? spec) => spec is { Compact: true } ? 1f : MinRowHeight;

    /// <summary>
    /// RADIOCLICK1: where each cell row's top lands, stacked as the panel reserves them (<see cref="CellRowFloor"/>,
    /// <see cref="CellRowSpec.GapAbove"/>), from <see cref="PadTop"/>. The tests measure a radio list with it.
    /// </summary>
    internal static float[] StackCellRows(IReadOnlyList<CellRowSpec> rows)
    {
        var layout = new PanelLayout();
        var tops = new float[rows?.Count ?? 0];
        for (int i = 0; i < tops.Length; i++)
        {
            var spec = rows![i];
            float max = Math.Min(MaxFixedRowHeight, Math.Max(MaxRowHeight, spec.Height));
            tops[i] = layout.NextRow(spec.Height, CellRowFloor(spec), max, spec.GapAbove);
        }

        return tops;
    }

    /// <summary>
    /// RADIOCLICK1: <see cref="NextRow(float, float, float)"/> with this row's own gap above it in place of
    /// <see cref="RowGap"/> (<see cref="CellRowSpec.GapAbove"/>; negative: the ordinary gap). The first row has no
    /// row above it, so its gap is ignored and it starts at <see cref="PadTop"/>.
    /// </summary>
    internal float NextRow(float rowHeight, float minHeight, float maxHeight, float gapAbove)
    {
        if (RowCount > 0 && !float.IsNaN(gapAbove) && !float.IsInfinity(gapAbove) && gapAbove >= 0f)
            _cursor += gapAbove - RowGap;
        if (float.IsNaN(maxHeight) || maxHeight < MaxRowHeight) maxHeight = MaxRowHeight;
        if (maxHeight > MaxFixedRowHeight) maxHeight = MaxFixedRowHeight;
        float height = float.IsNaN(rowHeight) || float.IsInfinity(rowHeight) || rowHeight < minHeight
            ? (minHeight > 1f ? minHeight : 1f)
            : rowHeight > maxHeight ? maxHeight : rowHeight;
        if (minHeight >= MinRowHeight) height = ClampRowHeight(rowHeight, maxHeight);
        float top = _cursor;
        _cursor += height + RowGap;
        RowCount++;
        return top;
    }

    /// <summary>
    /// Rewind the cursor to the top so every row can be reserved again from scratch.
    ///
    /// <para>
    /// A re-layout pass is <c>Reset</c> followed by one <see cref="NextRow"/> per row, in the
    /// order the rows were created, with each height measured afresh. Forgetting the reset would
    /// not misplace the first row — it would place every row below the ones already there and
    /// double the content height, which is why there is a test for exactly that.
    /// </para>
    /// </summary>
    internal void Reset()
    {
        _cursor = PadTop;
        RowCount = 0;
    }

    /// <summary>
    /// Total height of the content rect. The trailing <see cref="RowGap"/> the last row added is
    /// taken back off, so the bottom padding is exactly <see cref="PadBottom"/> and not
    /// <c>PadBottom + RowGap</c>.
    /// </summary>
    internal float ContentHeight =>
        RowCount == 0 ? PadTop + PadBottom : _cursor - RowGap + PadBottom;

    /// <summary>A row height that is NaN, negative, tiny or absurd becomes a usable one.</summary>
    internal static float ClampRowHeight(float measured) => ClampRowHeight(measured, MaxRowHeight);

    /// <inheritdoc cref="ClampRowHeight(float)"/>
    internal static float ClampRowHeight(float measured, float maxHeight)
    {
        if (float.IsNaN(measured) || float.IsInfinity(measured) || measured < MinRowHeight)
            return MinRowHeight;
        return measured > maxHeight ? maxHeight : measured;
    }

    /// <summary>
    /// Panel height for this much content on a screen this tall: enough to show everything, but
    /// never below <see cref="MinPanelHeight"/> and never past <see cref="ScreenFraction"/> of
    /// the screen. An unreadable screen height falls back to <see cref="FallbackScreenHeight"/>
    /// rather than collapsing the panel to its minimum.
    /// </summary>
    internal static float PanelHeightFor(float contentHeight, float screenHeight)
    {
        if (float.IsNaN(screenHeight) || float.IsInfinity(screenHeight) || screenHeight <= 0f)
            screenHeight = FallbackScreenHeight;

        float wanted = HeaderHeight + (contentHeight < 0f ? 0f : contentHeight);
        float cap = screenHeight * ScreenFraction;
        if (cap < MinPanelHeight) cap = MinPanelHeight;
        if (wanted > cap) return cap;
        return wanted < MinPanelHeight ? MinPanelHeight : wanted;
    }

    /// <summary>Height of the clipped viewport inside a panel of <paramref name="panelHeight"/>.</summary>
    internal static float ViewportHeightFor(float panelHeight)
    {
        float h = panelHeight - HeaderHeight;
        return h < 0f ? 0f : h;
    }

    /// <summary>
    /// True when the content does not fit the viewport, i.e. the player has to scroll. Reported so
    /// the panel can say so rather than leaving rows silently below the fold.
    /// </summary>
    internal static bool Scrolls(float contentHeight, float viewportHeight) =>
        contentHeight > viewportHeight;

    /// <summary>
    /// Where a row's caption goes and where its control goes, from the single fact of whether the
    /// row <i>has</i> a control.
    ///
    /// <para>
    /// <b>Why this is one call (evidence 176).</b> Before it, a row's indent and a row's control
    /// were decided in different places and never compared. The three editor builders each shifted
    /// their caption sideways — <c>+80</c> for a checkbox, <c>-140</c> for a slider or a field —
    /// to leave room for a control that the same method then failed to build: a bare component
    /// with no <c>targetGraphic</c>, no <c>fillRect</c>, no <c>textComponent</c>. The gap was
    /// therefore the only thing on screen that a control had ever been intended, and the ragged
    /// alignment the player reported was rows indented around holes. Answering both halves from
    /// one function makes "is this row indented?" and "does this row have a control?" the same
    /// question, so a control that fails its operability check and is destroyed takes its indent
    /// with it and the row reverts to the full-width label a read-only row has always been.
    /// </para>
    ///
    /// <para>
    /// <b>Why side-by-side columns and not a control offset from the caption.</b> A caption here is
    /// an author-written config description that wraps to two, five, ten lines
    /// (<see cref="MaxRowHeight"/>), and the row grows to hold it. Columns cannot collide however
    /// tall the row gets, because they never share a horizontal span; the runtime pins the control
    /// to the row's first line so it stays beside the caption's opening line rather than drifting
    /// down to the middle of a ten-line paragraph.
    /// </para>
    /// </summary>
    internal static RowColumns Columns(bool hasControl)
    {
        float row = UnityUiRuntimeMetrics.RowWidth;
        if (!hasControl)
        {
            // No control means no reserved column and no indent — the caption owns the row.
            return new RowColumns(row, 0f, 0f, 0f);
        }

        float control = ControlColumnWidth;
        float label = row - control - ColumnGap;
        if (label < 0f) label = 0f;

        // Each column is centered in its own span, because every rect the runtime places is
        // center-pivoted: the x it wants is the column's midpoint measured from the row's midpoint.
        return new RowColumns(
            labelWidth: label,
            labelX: (-row * 0.5f) + (label * 0.5f),
            controlWidth: control,
            controlX: (row * 0.5f) - (control * 0.5f));
    }
}

/// <summary>
/// One row's two columns, as produced by <see cref="PanelLayout.Columns"/>. Widths are sizes and
/// the two x values are center offsets from the row's own center, which is the coordinate every
/// center-pivoted rect in the runtime is placed with.
/// </summary>
internal readonly struct RowColumns
{
    internal RowColumns(float labelWidth, float labelX, float controlWidth, float controlX)
    {
        LabelWidth = labelWidth;
        LabelX = labelX;
        ControlWidth = controlWidth;
        ControlX = controlX;
    }

    /// <summary>How wide the caption may be before it wraps.</summary>
    internal float LabelWidth { get; }

    /// <summary>Center of the caption column, offset from the row's center.</summary>
    internal float LabelX { get; }

    /// <summary>How wide the control column is; zero on a row that has no control.</summary>
    internal float ControlWidth { get; }

    /// <summary>Center of the control column, offset from the row's center.</summary>
    internal float ControlX { get; }

    /// <summary>True when a column was reserved for a control.</summary>
    internal bool HasControl => ControlWidth > 0f;

    /// <summary>Right-hand edge of the caption column, offset from the row's center.</summary>
    internal float LabelRight => LabelX + (LabelWidth * 0.5f);

    /// <summary>Left-hand edge of the control column, offset from the row's center.</summary>
    internal float ControlLeft => ControlX - (ControlWidth * 0.5f);
}

/// <summary>
/// The two row metrics <see cref="PanelLayout"/> shares with the client runtime. They live here
/// rather than on <c>UnityUiRuntime</c> because that type is compiled out of the test assembly,
/// and the layout arithmetic must not be.
/// </summary>
internal static class UnityUiRuntimeMetrics
{
    /// <summary>Height of an ordinary single-line row (a button, a toggle, a slider, a field).</summary>
    internal const float RowHeight = 40f;

    /// <summary>
    /// Width of the panel frame. Lives here, and is aliased by <c>UnityUiRuntime.PanelWidth</c>,
    /// so the tests can check that a row actually fits the viewport it is placed in — the
    /// viewport being the frame less <see cref="PanelLayout.PadSide"/> on each side.
    /// </summary>
    internal const float PanelWidth = 640f;

    /// <summary>
    /// Width of a row inside the panel.
    ///
    /// <para>
    /// 400 until evidence 176. The viewport is 600 wide (<see cref="PanelWidth"/> less
    /// <see cref="PanelLayout.PadSide"/> twice) and rows were using two thirds of it, which was
    /// affordable while a row was one caption and cost real space once a row is a caption
    /// <i>and</i> a control column: at 400 the caption would have been left with 208 px and every
    /// config description in the game would have wrapped to a paragraph. 560 takes the slack back
    /// and still leaves 20 px of air on each side inside the viewport.
    /// </para>
    /// </summary>
    internal const float RowWidth = 560f;
}

/// <summary>
/// The version footer's band (<see cref="IUiPanel.Footer"/>): one small muted line at the bottom of
/// a panel frame, below the scrolling viewport. Game-free so the sizing the runtime applies and the
/// arithmetic the tests pin are the same numbers.
/// </summary>
internal static class PanelFooterLayout
{
    /// <summary>Caption size: smaller than a note (<see cref="SettingsAlign.NoteFontSize"/>, 16) and a body label (18).</summary>
    internal const float FontSize = 13f;

    /// <summary>Height reserved at the bottom of the frame when a footer is shown.</summary>
    internal const float BandHeight = 24f;

    /// <summary>Widest the footer's text rect gets; a longer line ellipsizes rather than wraps.</summary>
    internal const float MaxTextWidth = UnityUiRuntimeMetrics.RowWidth;

    /// <summary>Name of the footer's GameObject under the panel frame.</summary>
    internal const string ObjectName = "version-footer";

    /// <summary>The band a panel reserves: <see cref="BandHeight"/> with a footer, nothing without.</summary>
    internal static float BandFor(string? footer) => string.IsNullOrWhiteSpace(footer) ? 0f : BandHeight;

    /// <summary>
    /// Frame height for an ordinary (runtime-sized) panel with this footer: the content plus the
    /// band, through <see cref="PanelLayout.PanelHeightFor"/>, so a panel whose rows fit without a
    /// footer still fits with one instead of growing a scrollbar.
    /// </summary>
    internal static float PanelHeightFor(float contentHeight, float screenHeight, string? footer) =>
        PanelLayout.PanelHeightFor(
            (contentHeight < 0f ? 0f : contentHeight) + BandFor(footer), screenHeight);

    /// <summary>The scrolling viewport of a panel <paramref name="panelHeight"/> tall that carries this footer.</summary>
    internal static float ViewportHeightFor(float panelHeight, string? footer)
    {
        float h = PanelLayout.ViewportHeightFor(panelHeight) - BandFor(footer);
        return h < 0f ? 0f : h;
    }

    /// <summary>Width of the footer's text rect inside a pane <paramref name="paneWidth"/> wide.</summary>
    internal static float TextWidth(float paneWidth)
    {
        float inner = paneWidth - PanelLayout.PadSide * 2f;
        if (float.IsNaN(inner) || inner < 1f) inner = 1f;
        return inner > MaxTextWidth ? MaxTextWidth : inner;
    }

    /// <summary>
    /// Horizontal offset of the footer from the pane's center so it is centered on the Mods workspace
    /// frame instead (a detail pane sits right of the sidebar), clamped so the text rect never leaves
    /// the pane. Zero for an ordinary panel, whose own center is the answer.
    /// </summary>
    internal static float CentredOffset(float frameCentreX, float paneCentreX, float paneWidth)
    {
        float offset = frameCentreX - paneCentreX;
        if (float.IsNaN(offset) || float.IsInfinity(offset)) return 0f;
        float room = (paneWidth - TextWidth(paneWidth)) * 0.5f - PanelLayout.PadSide;
        if (float.IsNaN(room) || room < 0f) room = 0f;
        if (offset > room) return room;
        return offset < -room ? -room : offset;
    }
}
