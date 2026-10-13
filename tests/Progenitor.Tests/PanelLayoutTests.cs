using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FUI13 (research evidence 163). Panel row placement and panel sizing.
///
/// <para>
/// The bug these cover: the panel was a fixed 640x360 frame and every row was placed at a
/// constant 48 px step with no clipping, so the settings panel — which lists every keybind and
/// every non-synced config entry of every loaded plugin, 32 rows with two plugins — drew six
/// rows inside the frame and the rest over the world. The constant step also ignored wrapped
/// labels, so the rows that were visible overlapped each other.
/// </para>
/// </summary>
public class PanelLayoutTests
{
    /// <summary>An empty panel still reserves its padding, and reports no rows.</summary>
    [Fact]
    public void An_empty_panel_is_just_padding()
    {
        var layout = new PanelLayout();

        Assert.Equal(0, layout.RowCount);
        Assert.Equal(PanelLayout.PadTop + PanelLayout.PadBottom, layout.ContentHeight);
    }

    /// <summary>The first row starts at the top padding, not at zero and not at the center.</summary>
    [Fact]
    public void The_first_row_starts_at_the_top_padding()
    {
        var layout = new PanelLayout();

        Assert.Equal(PanelLayout.PadTop, layout.NextRow(UnityUiRuntimeMetrics.RowHeight));
        Assert.Equal(1, layout.RowCount);
    }

    /// <summary>
    /// The core of the fix: a row's own height, not a constant, decides where the next one starts.
    /// A three-line label pushes the row after it down by three lines.
    /// </summary>
    [Fact]
    public void A_tall_row_pushes_the_next_row_down_by_its_own_height()
    {
        var layout = new PanelLayout();

        float first = layout.NextRow(120f);
        float second = layout.NextRow(UnityUiRuntimeMetrics.RowHeight);

        Assert.Equal(PanelLayout.PadTop, first);
        Assert.Equal(first + 120f + PanelLayout.RowGap, second);
    }

    /// <summary>Rows never overlap, whatever heights they are given.</summary>
    [Theory]
    [InlineData(40f, 40f, 40f)]
    [InlineData(40f, 130f, 40f)]
    [InlineData(200f, 40f, 90f)]
    public void Rows_never_overlap(float a, float b, float c)
    {
        var layout = new PanelLayout();

        float topA = layout.NextRow(a);
        float topB = layout.NextRow(b);
        float topC = layout.NextRow(c);

        Assert.True(topB >= topA + PanelLayout.ClampRowHeight(a), "row B overlaps row A");
        Assert.True(topC >= topB + PanelLayout.ClampRowHeight(b), "row C overlaps row B");
    }

    /// <summary>
    /// The trailing gap after the last row is taken back off, so the bottom padding is exactly
    /// the padding and not padding plus a gap.
    /// </summary>
    [Fact]
    public void Content_height_does_not_keep_the_trailing_row_gap()
    {
        var layout = new PanelLayout();
        layout.NextRow(40f);
        layout.NextRow(40f);

        Assert.Equal(PanelLayout.PadTop + 40f + PanelLayout.RowGap + 40f + PanelLayout.PadBottom,
            layout.ContentHeight);
    }

    /// <summary>A measured height that is nonsense becomes one usable row.</summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-10f)]
    [InlineData(0f)]
    [InlineData(1f)]
    public void A_nonsense_measurement_becomes_one_row(float measured)
    {
        Assert.Equal(PanelLayout.MinRowHeight, PanelLayout.ClampRowHeight(measured));
    }

    /// <summary>
    /// One pathological label must not become taller than the screen and push every other row
    /// out of reach; its text clips instead.
    /// </summary>
    [Fact]
    public void A_pathological_label_is_capped()
    {
        Assert.Equal(PanelLayout.MaxRowHeight, PanelLayout.ClampRowHeight(5000f));
    }

    /// <summary>A short panel is sized to its content, not left at some fixed height.</summary>
    [Fact]
    public void A_short_panel_is_sized_to_its_content()
    {
        var layout = new PanelLayout();
        for (int i = 0; i < 6; i++) layout.NextRow(40f);

        float height = PanelLayout.PanelHeightFor(layout.ContentHeight, 1080f);

        Assert.Equal(PanelLayout.HeaderHeight + layout.ContentHeight, height);
        Assert.False(PanelLayout.Scrolls(layout.ContentHeight, PanelLayout.ViewportHeightFor(height)));
    }

    /// <summary>
    /// The real case. Thirty-two rows do not fit an 1080p screen, so the panel stops growing at
    /// its cap and the list scrolls — rather than 26 rows being drawn over the world.
    /// </summary>
    [Fact]
    public void The_settings_panel_row_count_scrolls_instead_of_overflowing()
    {
        var layout = new PanelLayout();
        for (int i = 0; i < 32; i++) layout.NextRow(40f);

        float height = PanelLayout.PanelHeightFor(layout.ContentHeight, 1080f);

        Assert.Equal(1080f * PanelLayout.ScreenFraction, height);
        Assert.True(PanelLayout.Scrolls(layout.ContentHeight, PanelLayout.ViewportHeightFor(height)),
            "32 rows must scroll on a 1080p screen");
    }

    /// <summary>A nearly empty panel does not collapse to a sliver.</summary>
    [Fact]
    public void A_panel_never_shrinks_below_its_minimum()
    {
        Assert.Equal(PanelLayout.MinPanelHeight, PanelLayout.PanelHeightFor(0f, 1080f));
    }

    /// <summary>
    /// The cap never wins over the minimum: on an absurdly short screen the panel is still usable
    /// rather than a few pixels tall.
    /// </summary>
    [Fact]
    public void The_screen_cap_never_beats_the_minimum()
    {
        Assert.Equal(PanelLayout.MinPanelHeight, PanelLayout.PanelHeightFor(2000f, 100f));
    }

    /// <summary>An unreadable screen height falls back rather than collapsing the panel.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void An_unreadable_screen_height_uses_the_fallback(float screenHeight)
    {
        Assert.Equal(
            PanelLayout.FallbackScreenHeight * PanelLayout.ScreenFraction,
            PanelLayout.PanelHeightFor(5000f, screenHeight));
    }

    /// <summary>The viewport is the panel minus its title band, and never negative.</summary>
    [Fact]
    public void The_viewport_is_the_panel_minus_the_header()
    {
        Assert.Equal(400f - PanelLayout.HeaderHeight, PanelLayout.ViewportHeightFor(400f));
        Assert.Equal(0f, PanelLayout.ViewportHeightFor(10f));
    }

    /// <summary>
    /// Content exactly the size of the viewport does not scroll: an off-by-one here shows a
    /// scrollbar on a list that fits.
    /// </summary>
    [Fact]
    public void Content_that_exactly_fits_does_not_scroll()
    {
        Assert.False(PanelLayout.Scrolls(300f, 300f));
        Assert.True(PanelLayout.Scrolls(300.5f, 300f));
    }

    /// <summary>
    /// INSTALLSIDES1 live fix (2026-10-03): the Install drawer's scrolling body is a fixed cell row
    /// taller than <see cref="PanelLayout.MaxRowHeight"/>. It is drawn at its own height, so it must
    /// reserve that height: reserving the 400 px cap put the next listing rows under the drawer.
    /// Measured rows keep the cap.
    /// </summary>
    [Fact]
    public void A_fixed_row_taller_than_the_cap_reserves_its_own_height()
    {
        var layout = new PanelLayout();
        float bodyTop = layout.NextRow(466f, PanelLayout.MinRowHeight, 466f);
        float next = layout.NextRow(53f);
        Assert.Equal(bodyTop + 466f + PanelLayout.RowGap, next, 3);

        var measured = new PanelLayout();
        float top = measured.NextRow(466f);
        Assert.Equal(top + PanelLayout.MaxRowHeight + PanelLayout.RowGap, measured.NextRow(53f), 3);

        // A smaller or absurd cap never goes under the measured cap or past the fixed-row bound.
        var odd = new PanelLayout();
        float a = odd.NextRow(5000f, PanelLayout.MinRowHeight, 100f);
        float b = odd.NextRow(5000f, PanelLayout.MinRowHeight, 99999f);
        float c = odd.NextRow(40f);
        Assert.Equal(a + PanelLayout.MaxRowHeight + PanelLayout.RowGap, b, 3);
        Assert.Equal(b + PanelLayout.MaxFixedRowHeight + PanelLayout.RowGap, c, 3);
    }

    /// <summary>The live placer hands a fixed cell row's own height to the reservation as its cap.</summary>
    [Fact]
    public void The_row_placer_reserves_a_cell_row_at_its_fixed_height()
    {
        string placer = MethodBody(UiSource("UnityUiRuntime.cs"), "private void PlaceRow(");
        SourcePin.AssertUncommented(placer, "maxHeight = Math.Min(PanelLayout.MaxFixedRowHeight, Math.Max(PanelLayout.MaxRowHeight, height));");
        // RADIOCLICK1: the floor is the cell row's own (40 px, or 1 for a compact row), and its gap above is its own.
        SourcePin.AssertUncommented(placer, "float minHeight = PanelLayout.MinRowHeight, gapAbove = -1f;");
        SourcePin.AssertUncommented(placer, "minHeight = PanelLayout.CellRowFloor(cellRowFixed.Spec);");
        SourcePin.AssertUncommented(placer, "gapAbove = cellRowFixed.Spec.GapAbove;");
        SourcePin.AssertUncommented(placer, "minHeight, maxHeight, gapAbove);");
    }

    /// <summary>
    /// RADIOCLICK1: a compact row reserves exactly its height (not the 40 px floor), and a row's own gap above
    /// replaces <see cref="PanelLayout.RowGap"/>; the first row ignores its gap and starts at the top pad.
    /// </summary>
    [Fact]
    public void A_compact_row_reserves_its_own_height_and_its_own_gap()
    {
        var cells = new List<CellSpec> { new CellSpec(CellKind.Text, "t").With(text: "x") };
        var ordinary = new CellRowSpec("a", cells, height: 22f);
        var compact = new CellRowSpec("b", cells, height: 22f, compact: true);
        var tight = new CellRowSpec("c", cells, height: 19f, compact: true, gapAbove: 2f);

        Assert.Equal(PanelLayout.MinRowHeight, PanelLayout.CellRowFloor(ordinary));
        Assert.Equal(1f, PanelLayout.CellRowFloor(compact));

        var tops = PanelLayout.StackCellRows(new[] { tight, ordinary, compact, tight });
        Assert.Equal(PanelLayout.PadTop, tops[0]);                                     // the first row's gap is ignored
        Assert.Equal(tops[0] + 19f + PanelLayout.RowGap, tops[1]);                     // compact: its 19 px, the ordinary gap after
        Assert.Equal(tops[1] + PanelLayout.MinRowHeight + PanelLayout.RowGap, tops[2]); // ordinary: raised to 40 px
        Assert.Equal(tops[2] + 22f + 2f, tops[3]);                                    // its own 2 px gap above
    }

    // ---- the layout is re-runnable (job FUI17, R43 finding 4) ------------------------------

    /// <summary>The cursor goes back to the top padding, and the row count with it.</summary>
    [Fact]
    public void Reset_rewinds_the_cursor_to_the_top()
    {
        var layout = new PanelLayout();
        layout.NextRow(120f);
        layout.NextRow(40f);

        layout.Reset();

        Assert.Equal(0, layout.RowCount);
        Assert.Equal(PanelLayout.PadTop + PanelLayout.PadBottom, layout.ContentHeight);
        Assert.Equal(PanelLayout.PadTop, layout.NextRow(40f));
    }

    /// <summary>
    /// The property a re-layout rests on: replaying the same heights from a reset cursor puts
    /// every row back exactly where it was. Without it, "lay the panel out again" would move rows
    /// that had no reason to move.
    /// </summary>
    [Fact]
    public void A_relayout_over_the_same_heights_reproduces_the_same_layout()
    {
        float[] heights = { 40f, 96f, 40f, 152f, 40f };
        var layout = new PanelLayout();

        var first = Place(layout, heights);
        float firstContent = layout.ContentHeight;

        layout.Reset();
        var second = Place(layout, heights);

        Assert.Equal(first, second);
        Assert.Equal(firstContent, layout.ContentHeight);
        Assert.Equal(heights.Length, layout.RowCount);
    }

    /// <summary>
    /// The bug, in arithmetic. A keybind row's label grows when the rebind flow appends the reason
    /// a chord was refused; the rows above it must not move and the rows below it must move down
    /// by exactly what it gained — which is what the old one-shot cursor could not do, so the
    /// grown label drew over the row beneath instead.
    /// </summary>
    [Fact]
    public void A_row_that_grew_pushes_the_rows_below_it_down_and_leaves_the_ones_above_alone()
    {
        var layout = new PanelLayout();
        var before = Place(layout, new[] { 40f, 40f, 40f, 40f });

        layout.Reset();
        var after = Place(layout, new[] { 40f, 40f, 100f, 40f });

        Assert.Equal(before[0], after[0]);
        Assert.Equal(before[1], after[1]);
        Assert.Equal(before[2], after[2]);
        Assert.Equal(before[3] + 60f, after[3]);
    }

    /// <summary>
    /// And back again when the notice is cleared: a re-layout is not a ratchet, so the panel does
    /// not keep the space a refusal message needed once the message is gone.
    /// </summary>
    [Fact]
    public void A_row_that_shrank_pulls_the_rows_below_it_back_up()
    {
        var layout = new PanelLayout();
        var grown = Place(layout, new[] { 40f, 100f, 40f });

        layout.Reset();
        var shrunk = Place(layout, new[] { 40f, 40f, 40f });

        Assert.Equal(grown[1], shrunk[1]);
        Assert.Equal(grown[2] - 60f, shrunk[2]);
    }

    /// <summary>
    /// A re-layout that forgot to rewind would not misplace the first row — it would stack the
    /// whole panel below itself and double the content height, which is a panel that scrolls
    /// through an empty second half. Pinned because the reset is one line and easy to lose.
    /// </summary>
    [Fact]
    public void A_relayout_does_not_accumulate_the_content_height()
    {
        float[] heights = { 40f, 40f, 40f };
        var layout = new PanelLayout();
        Place(layout, heights);
        float once = layout.ContentHeight;

        for (int pass = 0; pass < 4; pass++)
        {
            layout.Reset();
            Place(layout, heights);
        }

        Assert.Equal(once, layout.ContentHeight);
        Assert.Equal(heights.Length, layout.RowCount);
    }

    private static float[] Place(PanelLayout layout, IReadOnlyList<float> heights)
    {
        var tops = new float[heights.Count];
        for (int i = 0; i < heights.Count; i++) tops[i] = layout.NextRow(heights[i]);
        return tops;
    }

    // ---- every row type measures its own caption (job FUI17, R43 finding 3) -----------------
    //
    // R43's fair criticism of the tests above: they hand PanelLayout its heights directly, so a
    // caller that passes a constant is invisible to them. That is not a hypothetical — it is how
    // AddButton stayed on a constant 40 px through the batch whose commit subject was "every row
    // measures its own label". The callers live in `#if !PROGENITOR_TESTS` code and cannot be
    // driven from this assembly, so the rule is checked against the source: there is exactly one
    // place that reserves a row, every row builder goes through it, and it measures.

    /// <summary>
    /// Every row builder the runtime interface declares registers its row through the one
    /// measured path, and none of them reserves a height of its own. A sixth row type added
    /// later fails this the same way the fifth would have.
    ///
    /// <para>KIT1 added builders that decorate an existing row rather than build one
    /// (<c>AddMarkedButton</c> puts a marker and a dot on <c>AddButton</c>'s row;
    /// <c>AddTonedLabel</c> recolors <c>AddLabel</c>'s). Those satisfy the rule by delegation, so
    /// the check follows one hop into the builder they call and requires <i>that</i> one to reach
    /// <c>AddRow</c>. The property is unchanged — the caption is still measured exactly once, by
    /// the row that owns it — and a builder that reaches neither still fails.</para>
    /// </summary>
    [Fact]
    public void Every_row_builder_registers_its_row_through_the_measured_path()
    {
        string contract = UiSource("IUiRuntime.cs");
        string runtime = UiSource("UnityUiRuntime.cs");

        var builders = new List<string>();
        foreach (Match m in Regex.Matches(contract, @"IRuntimeWidget\?\s+(Add\w+)\(IRuntimePanel panel"))
            builders.Add(m.Groups[1].Value);

        Assert.Contains("AddButton", builders);
        Assert.True(builders.Count >= 5, "expected at least the five row builders, found " + builders.Count);

        foreach (var name in builders)
        {
            string body = MethodBody(runtime, "public IRuntimeWidget? " + name + "(IRuntimePanel panel");
            Assert.False(
                body.Contains("NextRow(", StringComparison.Ordinal),
                name + " reserves a row height of its own instead of going through the shared measured path");
            if (body.Contains("host.AddRow(", StringComparison.Ordinal)) continue;

            // One hop: a decorator must hand the row to a builder that does measure it.
            string? delegated = builders.FirstOrDefault(
                other => other != name && body.Contains(other + "(panel, ", StringComparison.Ordinal));
            Assert.True(
                delegated != null,
                name + " does not register its row through UnityPanel.AddRow, so nothing ever measures its caption");
            string inner = MethodBody(runtime, "public IRuntimeWidget? " + delegated + "(IRuntimePanel panel");
            Assert.True(
                inner.Contains("host.AddRow(", StringComparison.Ordinal),
                name + " delegates to " + delegated + ", which does not measure its caption either");
        }
    }

    /// <summary>
    /// Exactly one call site reserves a row, so "does this row type measure?" has one answer for
    /// the whole runtime rather than one per builder.
    /// </summary>
    [Fact]
    public void Only_the_shared_row_placer_reserves_a_row()
    {
        string runtime = UiSource("UnityUiRuntime.cs");

        int reservations = Regex.Matches(runtime, @"_layout\.NextRow\(").Count;
        Assert.Equal(1, reservations);

        string placer = MethodBody(runtime, "private void PlaceRow(");
        SourcePin.AssertUncommented(placer, "_layout.NextRow(");
        SourcePin.AssertUncommented(placer, "FitLabel(");
        // A measurement that cannot be taken is one ordinary row, never a throw out of a
        // main-thread Unity callback.
        SourcePin.AssertStatement(placer, "catch { height = RowHeight; }");
    }

    /// <summary>
    /// The literal shape of R43 finding 3: a row reserved at the constant instead of at what its
    /// own text measured.
    /// </summary>
    [Fact]
    public void No_row_is_reserved_at_the_constant_row_height()
    {
        Assert.DoesNotContain("NextRow(RowHeight)", UiSource("UnityUiRuntime.cs"));
    }

    /// <summary>
    /// Registering a row without handing over the object whose text decides its height would pass
    /// every check above and still reserve a constant, so the label argument is required too.
    /// </summary>
    [Fact]
    public void Every_registered_row_hands_over_a_caption_to_measure()
    {
        string runtime = UiSource("UnityUiRuntime.cs");
        var calls = Regex.Matches(runtime, @"host\.AddRow\(([^;]*)\);");

        Assert.True(calls.Count >= 5, "expected one AddRow call per row builder, found " + calls.Count);
        foreach (Match call in calls)
        {
            Assert.True(
                call.Groups[1].Value.Contains(",", StringComparison.Ordinal),
                "AddRow(" + call.Groups[1].Value + ") passes no caption, so the row falls back to one constant height");
        }
    }

    // ---- a text change re-runs the layout (job FUI17, R43 finding 4) ------------------------

    /// <summary>
    /// The rebind flow rewrites a row's text long after the panel was laid out — "press a key…",
    /// then the sentence saying why an AltGr chord was refused. The setter that writes it has to
    /// ask the panel to lay out again, or the grown label draws over the row below.
    /// </summary>
    [Fact]
    public void A_row_whose_text_changes_asks_its_panel_to_lay_out_again()
    {
        string runtime = UiSource("UnityUiRuntime.cs");
        string property = Between(runtime, "public string Text", "public bool Visible");

        Assert.Contains("tmp.text = value", property);
        Assert.Contains("Relayout()", property);
    }

    /// <summary>
    /// And that the re-layout is a whole pass, not a patch-up of one row: a row's height is a
    /// function of its own text, but where it goes is a function of every row above it.
    /// </summary>
    [Fact]
    public void The_layout_pass_re_places_every_row_from_the_top()
    {
        string pass = MethodBody(UiSource("UnityUiRuntime.cs"), "private void RunLayout(");

        SourcePin.AssertStatement(pass, "_layout.Reset()");
        SourcePin.AssertUncommented(pass, "_rows.Count");
        SourcePin.AssertStatement(pass, "PlaceRow(");
    }

    /// <summary>
    /// Everything the one-shot layout already got right survives the restructure: the panel is
    /// still capped and still says, once per show, how many rows it laid out and whether they
    /// fit. The wording is the live-check grep string.
    /// </summary>
    [Fact]
    public void The_show_path_still_reports_what_it_laid_out()
    {
        string runtime = UiSource("UnityUiRuntime.cs");
        string pass = MethodBody(runtime, "private void RunLayout(");

        SourcePin.AssertUncommented(pass, "PanelLayout.PanelHeightFor(");
        SourcePin.AssertUncommented(pass, "PanelLayout.Scrolls(");
        SourcePin.AssertUncommented(pass, "\"laid out\"");
        SourcePin.AssertUncommented(pass, "\" rows=\"");
        SourcePin.AssertUncommented(pass, "\" content=\"");
        SourcePin.AssertUncommented(pass, "\" (scrolls)\" : \" (fits)\"");

        // Job FUI26 (R45 finding 22): the pass no longer activates the bar. ScrollRect's AutoHide
        // is the single owner of its visibility (evidence 175) and this verdict is our arithmetic,
        // recorded for the read-back that compares the two. See UiControlBuildTests.
        Assert.DoesNotContain("_scrollbar.SetActive(", pass);
        SourcePin.AssertStatement(pass, "_verdictScrolls = scrolls");
    }

    /// <summary>
    /// A show starts at the top of the list; a re-layout triggered by a row's text changing does
    /// not, because it fires while the player is looking at that row.
    /// </summary>
    [Fact]
    public void Only_a_show_resets_the_scroll_position()
    {
        string runtime = UiSource("UnityUiRuntime.cs");

        SourcePin.AssertUncommented(runtime, "ApplyLayout() => RunLayout(resetScroll: true");
        string relayout = MethodBody(runtime, "public void Relayout()");
        SourcePin.AssertUncommented(relayout, "RunLayout(resetScroll: false");
        string pass = MethodBody(runtime, "private void RunLayout(");
        SourcePin.AssertUncommented(pass, "SidebarScrollLayout.Apply(");
        SourcePin.AssertUncommented(pass, "PeekSidebarScrollPreserve(");
        SourcePin.AssertUncommented(pass, "if (hasPending && !keepPending) ClearSidebarScrollPreserve()");
        SourcePin.AssertUncommented(pass, "FormatPreserveLog(");
        SourcePin.AssertUncommented(runtime, "ClearSidebarScrollPreserve()");

        // Candidate-1 live: selecting a row rebuilt the sidebar through Ui.Show, which is a
        // show and did reset. The contract only held for Relayout. Begin (already open) and
        // RebuildSidebar must capture and restore around that Show. Restore re-arms so the
        // settle pass that follows can re-apply; the wheel pump clears pending so it is
        // the only writer after the user scrolls.
        string menu = UiSource("ModsMenu.cs");
        string begin = MethodBody(menu, "private static IUiPanel Begin(");
        SourcePin.AssertUncommented(begin, "bool preserveScroll = IsOpen;");
        SourcePin.AssertUncommented(begin, "if (preserveScroll) CaptureScroll()");
        SourcePin.AssertUncommented(begin, "if (preserveScroll) RestoreScroll()");
        SourcePin.AssertUncommented(begin, "UnityUiRuntime.ClearSidebarScrollPreserve()");
        string rebuild = MethodBody(menu, "internal static void RebuildSidebar(");
        SourcePin.AssertUncommented(rebuild, "CaptureScroll()");
        SourcePin.AssertUncommented(rebuild, "RestoreScroll()");
        SourcePin.AssertUncommented(MethodBody(menu, "private static void BuildSidebarCore("), "Ui.Show(panel)");
        string restore = MethodBody(menu, "private static void RestoreOne(");
        SourcePin.AssertUncommented(restore, "UnityUiRuntime.ArmSidebarScrollPreserve(npos, y)");
        SourcePin.AssertUncommented(restore, "SidebarScrollLayout.FormatPreserveLog(");
        string pump = MethodBody(runtime, "private static bool ApplyScrollOffset(");
        SourcePin.AssertUncommented(pump, "ClearSidebarScrollPreserve()");
    }

    /// <summary>
    /// The fallback path evidence 163 promised: a runtime that could not build a viewport still
    /// parents rows onto the frame and shows something.
    /// </summary>
    [Fact]
    public void A_panel_with_no_viewport_still_parents_its_rows_somewhere()
    {
        string runtime = UiSource("UnityUiRuntime.cs");
        SourcePin.AssertUncommented(runtime, "_nextPin == MenuRowPin.Content ? (_content ?? GameObject) : GameObject");
        SourcePin.AssertUncommented(runtime, "TryAdd<RectMask2D>(go)");
        SourcePin.AssertUncommented(runtime, "image.raycastTarget = true");
        SourcePin.AssertUncommented(runtime, "FitChromeInsets(");
    }

    // ---- one column rule, and controls that can actually be operated (job FUI24, evidence 176)
    //
    // The defect these cover: the settings panel was display-only. AddToggle, AddSlider and
    // AddInput each created one bare component and shifted their caption sideways — +80 for a
    // checkbox, -140 for a slider or a field — to leave room for a control that was never built.
    // A Toggle with no targetGraphic has nothing to click; a Slider with no fillRect/handleRect
    // draws nothing to grab; a TMP_InputField with no textComponent silently swallows every
    // keystroke and with no background is not even a raycast target, so it can never be focused.
    // The offsets were the only trace on screen that a control had ever been intended, and they
    // are the "ragged alignment" the player reported: rows indented around holes.

    /// <summary>A row with no control is not indented, and its caption owns the whole row.</summary>
    [Fact]
    public void A_row_with_no_control_gives_the_whole_row_to_its_caption()
    {
        var columns = PanelLayout.Columns(hasControl: false);

        Assert.False(columns.HasControl);
        Assert.Equal(UnityUiRuntimeMetrics.RowWidth, columns.LabelWidth);
        Assert.Equal(0f, columns.LabelX);
        Assert.Equal(0f, columns.ControlWidth);
    }

    /// <summary>A row with a control splits into two columns that exactly fill the row.</summary>
    [Fact]
    public void A_row_with_a_control_splits_the_row_into_a_caption_and_a_control_column()
    {
        var columns = PanelLayout.Columns(hasControl: true);

        Assert.True(columns.HasControl);
        Assert.Equal(PanelLayout.ControlColumnWidth, columns.ControlWidth);
        Assert.True(
            Math.Abs((columns.LabelWidth + PanelLayout.ColumnGap + columns.ControlWidth) - UnityUiRuntimeMetrics.RowWidth) < 0.001f,
            "the two columns and the gap between them do not add up to the row");
    }

    /// <summary>
    /// The property that makes a wrapped caption safe: the two columns never share a horizontal
    /// span, so a label that wraps to ten lines cannot reach its control however tall the row gets.
    /// </summary>
    [Fact]
    public void The_caption_column_and_the_control_column_never_overlap()
    {
        var columns = PanelLayout.Columns(hasControl: true);

        Assert.True(
            columns.LabelRight <= columns.ControlLeft,
            "caption right edge " + columns.LabelRight + " reaches into the control column at " + columns.ControlLeft);
        Assert.True(
            Math.Abs((columns.ControlLeft - columns.LabelRight) - PanelLayout.ColumnGap) < 0.001f,
            "the gap between the columns is not the gap the rule reserves");
    }

    /// <summary>Neither column hangs off the end of the row it is inside.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Both_columns_stay_inside_the_row(bool hasControl)
    {
        var columns = PanelLayout.Columns(hasControl);
        float half = UnityUiRuntimeMetrics.RowWidth * 0.5f;

        Assert.True(columns.LabelX - (columns.LabelWidth * 0.5f) >= -half - 0.001f, "caption starts left of the row");
        Assert.True(columns.LabelRight <= half + 0.001f, "caption ends right of the row");
        if (!hasControl) return;
        Assert.True(columns.ControlLeft >= -half - 0.001f, "control starts left of the row");
        Assert.True(columns.ControlX + (columns.ControlWidth * 0.5f) <= half + 0.001f, "control ends right of the row");
    }

    /// <summary>
    /// The regression that names the bug. An indent exists only because a control column was
    /// reserved, so a row cannot be indented for a control it does not have — which is precisely
    /// what the three builders did, every row, for as long as the panel existed.
    /// </summary>
    [Fact]
    public void The_indent_comes_from_the_control_and_from_nothing_else()
    {
        var without = PanelLayout.Columns(hasControl: false);
        var with = PanelLayout.Columns(hasControl: true);

        Assert.Equal(0f, without.LabelX);
        Assert.True(with.LabelX < 0f, "a row with a control does not shift its caption out of the control's way");
        Assert.True(with.LabelWidth < without.LabelWidth, "the control column costs the caption no width");
        // Stated as the biconditional, because the bug was the two halves disagreeing: an
        // indent existed on rows that had no control, on every row, for the panel's whole life.
        Assert.Equal(with.HasControl, with.LabelX != 0f);
        Assert.Equal(without.HasControl, without.LabelX != 0f);
    }

    /// <summary>Same question, same answer: the rule is a function of one fact and nothing else.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_column_rule_is_a_pure_function_of_whether_the_row_has_a_control(bool hasControl)
    {
        var a = PanelLayout.Columns(hasControl);
        var b = PanelLayout.Columns(hasControl);

        Assert.Equal(a.LabelWidth, b.LabelWidth);
        Assert.Equal(a.LabelX, b.LabelX);
        Assert.Equal(a.ControlWidth, b.ControlWidth);
        Assert.Equal(a.ControlX, b.ControlX);
    }

    /// <summary>
    /// A row fits the viewport it is placed in, and takes back the slack it was leaving. The
    /// viewport is the frame less <see cref="PanelLayout.PadSide"/> on each side; a 400 px row in
    /// a 600 px viewport was wasting a third of it, which stopped being affordable the moment a
    /// row had to hold a caption <i>and</i> a control column.
    /// </summary>
    [Fact]
    public void A_row_fits_the_viewport_and_does_not_waste_it()
    {
        float viewport = UnityUiRuntimeMetrics.PanelWidth - (2f * PanelLayout.PadSide);

        Assert.True(
            UnityUiRuntimeMetrics.RowWidth <= viewport,
            "a " + UnityUiRuntimeMetrics.RowWidth + " px row does not fit a " + viewport + " px viewport");
        Assert.True(
            viewport - UnityUiRuntimeMetrics.RowWidth <= 2f * PanelLayout.PadSide,
            "the row leaves more than a side padding of the viewport unused on each side");
        Assert.True(PanelLayout.ControlColumnWidth < UnityUiRuntimeMetrics.RowWidth, "the control column is not a column");
    }

    /// <summary>
    /// The acceptance criterion evidence 176 established: a widget is not the component, it is the
    /// component plus the objects it draws through, and the only part of "operable" a program can
    /// check about itself is a reference to something visible. Each builder sets the ones its
    /// control cannot work without.
    /// </summary>
    [Theory]
    [InlineData("private static GameObject? BuildToggle(", new[] { "targetGraphic", "graphic =" })]
    [InlineData("private static GameObject? BuildSlider(", new[] { "fillRect", "handleRect", "targetGraphic", "direction" })]
    [InlineData("private static GameObject? BuildInput(", new[] { "textComponent", "textViewport", "targetGraphic" })]
    public void Every_editor_control_sets_the_references_that_make_it_operable(string signature, string[] references)
    {
        string body = MethodBody(UiSource("UnityUiRuntime.cs"), signature);

        foreach (var reference in references)
            Assert.True(body.Contains(reference, StringComparison.Ordinal), signature + " never sets " + reference);
    }

    /// <summary>
    /// The literal grep that found the defect. Before this, <c>targetGraphic</c> and
    /// <c>handleRect</c> appeared in the whole runtime only inside <c>MakeScrollbar</c> — the one
    /// control that had been built properly — and that single fact was the whole diagnosis.
    /// </summary>
    [Fact]
    public void The_operability_references_are_not_only_in_the_scrollbar()
    {
        string runtime = UiSource("UnityUiRuntime.cs");
        string scrollbar = MethodBody(runtime, "private static GameObject? MakeScrollbar(");
        string elsewhere = runtime.Replace(scrollbar, "");

        SourcePin.AssertUncommented(elsewhere, "targetGraphic");
        SourcePin.AssertUncommented(elsewhere, "handleRect");
        SourcePin.AssertUncommented(elsewhere, "textComponent");
        SourcePin.AssertUncommented(elsewhere, "fillRect");
    }

    /// <summary>
    /// Build-order trap 1. <c>Toggle</c> applies its checkmark's alpha in <c>OnEnable</c> and
    /// <c>TMP_InputField</c> creates its caret there, and a component added to an already-active
    /// GameObject runs <c>OnEnable</c> immediately — with every reference still null. So a control
    /// is created inactive and activated exactly once, at the end.
    /// </summary>
    [Fact]
    public void Every_editor_control_is_built_inactive_and_activated_at_the_end()
    {
        string runtime = UiSource("UnityUiRuntime.cs");
        string cradle = MethodBody(runtime, "private static GameObject NewControl(");

        SourcePin.AssertStatement(cradle, "go.SetActive(false);");
        Assert.True(
            cradle.IndexOf("SetActive(false)", StringComparison.Ordinal)
                < cradle.IndexOf("SetParent", StringComparison.Ordinal),
            "the control root is parented before it is deactivated");

        foreach (var maker in new[] { "MakeToggle(", "MakeSlider(", "MakeInput(" })
        {
            string body = MethodBody(runtime, "private static GameObject? " + maker);
            // Job FUI26: the local is `built` now that the clone and the build fallback both land
            // in it. The ordering rule is unchanged and is pinned harder in UiControlBuildTests,
            // which also asserts there is exactly one activation per builder.
            Assert.True(body.Contains("Activate(built)", StringComparison.Ordinal), maker + " never activates its control");
        }
    }

    /// <summary>
    /// A control that cannot be operated is destroyed, not left on the panel as an ornament. The
    /// row then reverts to the full-width caption a read-only row has always been, which is the
    /// same shape and reached through the same column rule.
    /// </summary>
    [Theory]
    [InlineData("MakeToggle(", "ToggleOperable")]
    [InlineData("MakeSlider(", "SliderOperable")]
    [InlineData("MakeInput(", "InputOperable")]
    public void A_control_that_is_not_operable_is_destroyed_rather_than_kept(string maker, string predicate)
    {
        string body = MethodBody(UiSource("UnityUiRuntime.cs"), "private static GameObject? " + maker);

        SourcePin.AssertUncommented(body, predicate);
        SourcePin.AssertUncommented(body, "DropUnoperableControl(");
    }

    /// <summary>
    /// Build-order trap 2. <c>Instantiate</c> remaps only references inside the copied subtree, so
    /// a clone keeps the game's inspector-wired <c>UnityEvent</c> calls — which survive
    /// <c>RemoveAllListeners</c> and still point at the game's own objects — and a cloned toggle
    /// arrives still enrolled in the vanilla <c>ToggleGroup</c> it came from. Every clone is
    /// re-checked against the same operability predicate, un-enrolled and silenced.
    /// </summary>
    [Fact]
    public void Every_clone_is_un_enrolled_silenced_and_re_checked()
    {
        string runtime = UiSource("UnityUiRuntime.cs");
        string cloner = MethodBody(runtime, "private static GameObject? CloneDonor(");

        SourcePin.AssertStatement(cloner, "UnEnrolToggleGroups(");
        SourcePin.AssertStatement(cloner, "SilenceClone(");
        SourcePin.AssertUncommented(cloner, "SetActive(false)");

        // Persistent listeners are not in the runtime list, so RemoveAllListeners alone is the
        // mistake this pins: they have to be switched off one at a time.
        string silence = MethodBody(runtime, "private static void Silence(");
        SourcePin.AssertUncommented(silence, "SetPersistentListenerState(");
        SourcePin.AssertUncommented(silence, "UnityEventCallState.Off");

        // The button clone predates the editor controls and had exactly the same defect. Job FUI30
        // moved it into a clone step of its own, CloneButton, so that ControlBuildPlan can run it —
        // and the same three lines have to be in the new home.
        string button = MethodBody(runtime, "private static GameObject? CloneButton(");
        SourcePin.AssertStatementExactly(button, "SilenceClone(clone);");
        SourcePin.AssertStatementExactly(button, "clone.SetActive(false);");
    }

    /// <summary>
    /// The three editor rows ask the column rule for their geometry and tell the row registrar
    /// whether the control actually survived, so the indent and the control are the same decision.
    /// </summary>
    [Theory]
    [InlineData("AddToggle")]
    [InlineData("AddSlider")]
    [InlineData("AddInput")]
    public void Every_editor_row_takes_its_geometry_from_the_one_column_rule(string builder)
    {
        string runtime = UiSource("UnityUiRuntime.cs");
        string body = MethodBody(runtime, "public IRuntimeWidget? " + builder + "(IRuntimePanel panel");

        SourcePin.AssertUncommented(body, "host.Columns(");
        SourcePin.AssertUncommented(body, "hasControl: control != null");
        SourcePin.AssertUncommented(runtime, "MenuPanelLayout.Columns(ContentRowWidth, hasControl)");
        SourcePin.AssertUncommented(runtime, "PanelLayout.Columns(hasControl)");
    }

    /// <summary>
    /// No hand-written caption offsets survive anywhere. These two literals <i>were</i> the bug on
    /// screen: room left for controls that were never built.
    /// </summary>
    [Fact]
    public void No_caption_is_shifted_by_a_hand_written_offset()
    {
        string runtime = UiSource("UnityUiRuntime.cs");

        Assert.DoesNotContain("new Vector2(80f, 0f)", runtime);
        Assert.DoesNotContain("new Vector2(-140f, 0f)", runtime);

        // The caption's x is written in exactly one place, and it comes from the column rule.
        string fit = MethodBody(runtime, "private static float FitLabel(");
        SourcePin.AssertUncommented(fit, "columns.LabelX");
        SourcePin.AssertUncommented(fit, "columns.LabelWidth");
    }

    /// <summary>
    /// Evidence 176 could not confirm offline whether the vanilla <c>SettingsEntry_*</c> widgets
    /// are instantiated while the player is in the world, so the runtime reports which path it
    /// took per control type. That is how the donor question gets answered from a log line
    /// instead of from a guess, and the answer is still owed to the research log.
    /// </summary>
    [Fact]
    public void The_runtime_says_whether_it_cloned_a_donor_or_built_the_control()
    {
        string runtime = UiSource("UnityUiRuntime.cs");

        foreach (var maker in new[] { "MakeToggle(", "MakeSlider(", "MakeInput(" })
        {
            string body = MethodBody(runtime, "private static GameObject? " + maker);
            Assert.True(body.Contains("LogControlSource(", StringComparison.Ordinal), maker + " never reports its source");
        }

        string log = MethodBody(runtime, "private static void LogControlSource(");
        SourcePin.AssertStatement(log, "if (logged) return;");

        // Job FUI26 (R45 finding 2): the sentences moved into ControlBuildPlan.Describe, which has
        // one per outcome including the two that only exist because a clone was rejected — a bool
        // could not say "we cloned one, it came out inert, so we built one". The wording and the
        // level are asserted in UiControlBuildTests; what belongs here is that this is still a
        // once-per-panel-build latch over the plan's answer.
        SourcePin.AssertUncommented(log, "ControlBuildPlan.Describe(");
        SourcePin.AssertUncommented(log, "ControlBuildPlan.IsWarning(");
    }

    /// <summary>
    /// R34 finding 1, re-pinned because AddInput was rebuilt around it. onValueChanged fires once
    /// per character, so the panel would parse, write, save the cfg and push the parsed value back
    /// into the field on every keystroke — which makes "1.5" and "-3" untypeable.
    /// </summary>
    [Fact]
    public void A_text_field_still_commits_on_end_edit_and_not_per_keystroke()
    {
        string body = MethodBody(UiSource("UnityUiRuntime.cs"), "public IRuntimeWidget? AddInput(IRuntimePanel panel");

        SourcePin.AssertUncommented(body, "field.onEndEdit");
        Assert.DoesNotContain("field.onValueChanged", body);
    }

    /// <summary>
    /// A row the player may not edit — a server-synced setting, another mod's entry — stays a
    /// plain caption and gains no control it could not have used. That was always the read-only
    /// shape; it is now also the shape a failed control falls back to.
    /// </summary>
    [Fact]
    public void A_read_only_row_gains_no_control()
    {
        string readOnly = Between(
            UiSource("SettingsPanel.cs"),
            "case SettingsEditorKind.ReadOnly:",
            "default:");

        SourcePin.AssertUncommented(readOnly, "panel.Label(");
        Assert.DoesNotContain("panel.Toggle(", readOnly);
        Assert.DoesNotContain("panel.Input(", readOnly);
        Assert.DoesNotContain("panel.Slider(", readOnly);
    }

    // ---- source helpers ----------------------------------------------------------------------

    private static string UiSource(string fileName) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Progenitor.Core", "Ui", fileName));

    private static string Between(string source, string from, string to)
    {
        int start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, "not found: " + from);
        int end = source.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, "not found after '" + from + "': " + to);
        return source.Substring(start, end - start);
    }

    /// <summary>The braces-matched body of the member whose signature starts with <paramref name="signature"/>.</summary>
    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "not found in UnityUiRuntime.cs: " + signature);
        int open = source.IndexOf('{', start);
        Assert.True(open > start, "no body for " + signature);

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source.Substring(open, i - open + 1);
        }

        Assert.Fail("unbalanced braces after " + signature);
        return "";
    }

    private static string FindRepoRoot()
    {
        string dir = Path.GetFullPath(AppContext.BaseDirectory);
        for (int i = 0; i < 8; i++)
        {
            if (File.Exists(Path.Combine(dir, "Progenitor.sln"))) return dir;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }
}
