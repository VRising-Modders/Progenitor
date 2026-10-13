using System;
using System.IO;
using Progenitor.Install;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job LIST4: the four defects the user reported against the live Install drawer (client gen 5,
/// 3840×1080) — two stacked Install buttons, a pointer probe that called a working chooser chip
/// no-hit, a drawer that opened below the fold, and captions drawing TMP's missing-glyph box —
/// plus the duplicate information an expanded drawer repeated from the row above it.
///
/// <para>The pane-level halves of items 1 and B (the head's cells, the row's single button and
/// its <c>Choose where to install</c> caption) are driven on the fake cell runtime in
/// <see cref="InstallListViewTests"/> and <c>InstallDownloadFlowTests</c>, which own the rig.
/// What is here is the arithmetic, the copy, the dedupe rule and the pins over the Unity-only
/// pointer and scroll paths.</para>
/// </summary>
public sealed class InstallDrawerPolishTests
{
    // ---- Item 4: WGL4 glyphs ---------------------------------------------------------------

    /// <summary>
    /// The drawer's disclosure captions use the WGL4 pair, from one constant each. U+25B8
    /// (<c>▸</c>) and U+25BE (<c>▾</c>) are outside WGL4 and drew <c>More info ☐</c> live — the
    /// same defect LIST3 fixed for the sort indicator by moving to U+25BC.
    /// </summary>
    [Fact]
    public void Drawer_captions_use_the_WGL4_glyph_pair_from_one_constant_each()
    {
        Assert.Equal("»", InstallModsCopy.GlyphExpand); // U+25BA drew as a box live too; » is Latin-1
        Assert.Equal("▼", InstallModsCopy.GlyphCollapse);
        Assert.Equal("More info »", InstallModsCopy.MoreInfo);
        Assert.Equal("Less info ▼", InstallModsCopy.LessInfo);
        Assert.Equal("Configure in Mods »", InstallModsCopy.ConfigureInMods);
        Assert.EndsWith(InstallModsCopy.GlyphCollapse, CellLayout.SortIndicator, StringComparison.Ordinal);
    }

    /// <summary>
    /// No UI source draws one of the three small triangles outside WGL4 any more — the codepoint
    /// scan, not just the three captions, because the next caption to be written is the one that
    /// would quietly reintroduce the box. Comments are read too: a comment naming the glyph is
    /// how the constant above documents what it replaced, so the two lines that do are excused
    /// by name.
    /// </summary>
    [Theory]
    [InlineData("InstallModsPane.cs")]
    [InlineData("MenuPanelLayout.cs")]
    [InlineData("UnityUiRuntime.cs")]
    [InlineData("UiHandles.cs")]
    [InlineData("IUiHandle.cs")]
    public void No_owned_ui_source_draws_a_non_WGL4_triangle(string file)
    {
        string text = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", file);
        var lines = text.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            // The one exemption: a doc comment that names WGL4 is the constant's own note about
            // the glyphs it replaced. Nothing TMP draws comes from a comment, and a caption that
            // reintroduced one of these would be a string literal, which is still scanned.
            if (line.TrimStart().StartsWith("///", StringComparison.Ordinal)
                && (line.Contains("WGL4", StringComparison.Ordinal) || line.Contains("U+25B", StringComparison.Ordinal)))
                continue;
            foreach (char bad in new[] { '▸', '▹', '▾', '▿' })
                Assert.False(line.IndexOf(bad) >= 0, file + ":" + (i + 1) + " draws U+" + ((int)bad).ToString("X4") + ": " + line.Trim());
        }
    }

    // ---- Item 1: one action button ----------------------------------------------------------

    /// <summary>The drawer head's one cell states the install fact; a tool row has none to state.</summary>
    [Fact]
    public void Drawer_head_note_states_the_install_fact_and_a_tool_row_has_none()
    {
        Assert.Equal(
            InstallModsCopy.RestartRequired,
            InstallModsPane.HeadNoteText(new InstallRowAction(
                InstallListingState.None, InstallModsCopy.InstallRestart, CellTone.Gold, true, true, "")));
        Assert.Equal(
            InstallModsCopy.NoRestartNeeded,
            InstallModsPane.HeadNoteText(new InstallRowAction(
                InstallListingState.None, InstallModsCopy.Install, CellTone.Gold, true, false, "")));
        Assert.Equal(
            "",
            InstallModsPane.HeadNoteText(new InstallRowAction(
                InstallListingState.Tool, InstallModsCopy.ToolNotPlugin, CellTone.Disabled, false, false, "")));
    }

    /// <summary>
    /// The install actions live on the listing row's side cells (INSTALLSIDES1). The drawer's head
    /// carries only drawer actions — Collapse, Read full description, Configure in Mods, the remote
    /// Restart now — never a second install button, a title or a version. Pinned on the source
    /// because a head cell wired to an install is exactly the defect LIST4 removed.
    /// </summary>
    [Fact]
    public void BuildDrawer_wires_no_install_action_and_the_row_wires_no_row_click()
    {
        string pane = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        string drawer = SourcePin.MethodBody(pane, "private void BuildDrawer(");
        Assert.DoesNotContain("OnClick(CellAction", drawer, StringComparison.Ordinal);
        Assert.DoesNotContain("OnInstallClick", drawer, StringComparison.Ordinal);
        Assert.DoesNotContain("OnSideClick", drawer, StringComparison.Ordinal);
        Assert.DoesNotContain("CellTitle", drawer, StringComparison.Ordinal);
        Assert.DoesNotContain("CellVersion", drawer, StringComparison.Ordinal);
        SourcePin.AssertUncommented(drawer, "head.OnClick(CellCollapse, () => ToggleDrawer(key));");
        // Only the expand button toggles the drawer: no row click, and every button acts on its side.
        string row = SourcePin.MethodBody(pane, "private bool AddListingRow(");
        Assert.DoesNotContain("OnRowClick", row, StringComparison.Ordinal);
        SourcePin.AssertUncommented(row, "row.OnClick(CellExpand, () => ToggleDrawer(key));");
        SourcePin.AssertUncommented(row, "row.OnClick(CellClientA, () => OnSideSlot(key, server: false, slot: 0));");
        string click = SourcePin.MethodBody(pane, "public bool OnSideClick(");
        SourcePin.AssertUncommented(click, "if (PromptOpenFor(entry)) return false;");
        Assert.DoesNotContain("SetDrawer", click, StringComparison.Ordinal);
        Assert.DoesNotContain("ToggleDrawer", click, StringComparison.Ordinal);
    }

    // ---- Item B: the drawer repeats nothing the row shows ------------------------------------

    /// <summary>
    /// The user's own example: <c>Removes version watermark</c> appeared as the row's one-liner,
    /// again as the drawer's one-liner and again as the README's first paragraph, with the package
    /// name <c>VRising-RemoveBuildNumberLabel</c> under it. A block that only <em>starts</em> with
    /// one of those is the description and stays.
    /// </summary>
    [Fact]
    public void A_readme_block_equal_to_the_one_liner_or_the_package_name_is_dropped()
    {
        var entry = Watermark();
        Assert.True(InstallModsPane.IsDuplicateReadmeBlock("Removes version watermark", entry));
        Assert.True(InstallModsPane.IsDuplicateReadmeBlock("  Removes version watermark\n", entry));
        Assert.True(InstallModsPane.IsDuplicateReadmeBlock("removes   version watermark", entry));
        Assert.True(InstallModsPane.IsDuplicateReadmeBlock("VRising-RemoveBuildNumberLabel", entry));
        Assert.True(InstallModsPane.IsDuplicateReadmeBlock("RemoveBuildNumberLabel", entry));
        Assert.True(InstallModsPane.IsDuplicateReadmeBlock("Remove Version Watermark", entry));
        Assert.False(InstallModsPane.IsDuplicateReadmeBlock("Removes version watermark from the main menu.", entry));
        Assert.False(InstallModsPane.IsDuplicateReadmeBlock("Installation", entry));
        Assert.False(InstallModsPane.IsDuplicateReadmeBlock("   ", entry));

        var blocks = new System.Collections.Generic.List<MarkdownBlock>
        {
            new(MarkdownBlockKind.Heading, "Remove Version Watermark"),
            new(MarkdownBlockKind.Paragraph, "Removes version watermark"),
            new(MarkdownBlockKind.Paragraph, "Drop the dll into BepInEx/plugins."),
        };
        var kept = InstallModsPane.WithoutDuplicateBlocks(blocks, entry);
        Assert.Single(kept);
        Assert.Equal("Drop the dll into BepInEx/plugins.", kept[0].Text);
    }

    // ---- Item 3: scroll the opened drawer into view ------------------------------------------

    /// <summary>
    /// The reveal rule at both approved frames: a block that already fits does not move the list,
    /// one that runs past the bottom puts its row at the top of the viewport, a row above the fold
    /// comes back to the top, and a drawer opened on the last row clamps to the content's end
    /// instead of scrolling past it.
    /// </summary>
    [Theory]
    [InlineData(3840f, 1080f)]
    [InlineData(1600f, 1024f)]
    public void The_reveal_rule_keeps_a_fitting_drawer_still_and_otherwise_puts_the_row_at_the_top(float width, float height)
    {
        var pane = MenuPanelLayout.ForFrame(ModsWorkspaceFrame.Fallback(width, height), sidebar: false);
        float view = MenuPanelLayout.ListViewportHeight(pane.Height);
        Assert.True(view > 100f, "viewport " + view);
        float content = view * 4f;

        // Already inside the window: nothing moves.
        Assert.Equal(0f, MenuPanelLayout.RevealOffsetY(10f, view - 20f, 0f, view, content), 3);
        Assert.Equal(120f, MenuPanelLayout.RevealOffsetY(130f, 40f, 120f, view, content), 3);

        // The drawer runs past the bottom: the row goes to the top of the viewport.
        float rowTop = 120f + view - 30f;
        Assert.Equal(rowTop, MenuPanelLayout.RevealOffsetY(rowTop, 300f, 120f, view, content), 3);

        // The row is above the fold: back to the top of the row, not to the top of the list.
        Assert.Equal(40f, MenuPanelLayout.RevealOffsetY(40f, 300f, 600f, view, content), 3);

        // The last row: clamped to the content's own end, so the list never scrolls past it.
        Assert.Equal(content - view, MenuPanelLayout.RevealOffsetY(content - 30f, 400f, 0f, view, content), 3);

        // A list that fits has nowhere to go.
        Assert.Equal(0f, MenuPanelLayout.RevealOffsetY(200f, 400f, 0f, view, view - 10f), 3);
    }

    /// <summary>
    /// The live half of item 3: the runtime measures the row and the block against the scroll
    /// view and writes the offset this rule answers, and the pane asks for it after the build and
    /// after <c>Read full description</c> grows the drawer. Pinned on the source because the
    /// measurement is <c>ScrollRect</c> / <c>RectTransform</c> and cannot run in this suite.
    /// </summary>
    [Fact]
    public void The_runtime_installs_the_reveal_hook_and_the_pane_asks_for_it_after_a_build_and_a_growth()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        SourcePin.AssertStatementExactly(SourcePin.MethodBody(runtime, "static UnityUiRuntime()"), "UiReveal.Hook = TryRevealBlock;");
        string reveal = SourcePin.MethodBody(runtime, "internal static bool TryRevealBlock(");
        SourcePin.AssertUncommented(reveal, "float wanted = MenuPanelLayout.RevealOffsetFromScreen(");
        SourcePin.AssertUncommented(reveal, "rowRect.MaxY, blockBottomY, viewRect.MaxY, viewportHeight, was, contentH, toTop, out rowTop);");
        // The viewport's own rect, never the row parent's: this is the line whose absence put the
        // opened row at client y 12 with the list starting at y 280.
        SourcePin.AssertUncommented(reveal, "var viewRect = host.ViewportScreenRect;");
        SourcePin.AssertUncommented(reveal, "content.anchoredPosition = new Vector2(content.anchoredPosition.x, wanted);");

        // The kit's reveal hook stays (other panes use it). INSTALLSIDES2 (user feedback): the
        // Install pane no longer reveals; a drawer toggle anchors its row instead (the pane side is
        // InstallSidesPaneTests.The_anchor_offset_compensates_and_clamps_only_to_the_content).
        string pane = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        SourcePin.AssertStatementExactly(SourcePin.MethodBody(pane, "public void Build("), "AnchorClickedRow();");
        // INSTALLSIDES2 item 4: Read full description opens the README reader window; the drawer
        // stays compact, so nothing is rebuilt and the list does not move.
        SourcePin.AssertUncommented(SourcePin.MethodBody(pane, "public void ShowFullDescription()"), "OpenReader(entry);");
        string toggle = SourcePin.MethodBody(pane, "public void ToggleDrawer(string key)");
        SourcePin.AssertUncommented(toggle, "_anchorY = MeasureRowY(key);");
        Assert.DoesNotContain("_revealToTop", pane, StringComparison.Ordinal);
    }

    // ---- Item 2: the pointer probe covers cell rows ------------------------------------------

    /// <summary>
    /// The chip root cause. <c>mods_ui hit</c> resolved <c>clickWouldInvoke</c> from the tab
    /// strip's own button rows only, so every cell row's button — a chooser chip, a filter chip,
    /// the Install column — read back <c>no-hit</c> at its own rect center no matter how well the
    /// real pointer reached it: the probe's silence was never evidence about the pointer path (the
    /// user's later report that a real click on <c>Both</c> installed is the other half of that).
    /// The probe now walks the cell rows of every shown panel, whichever pass built them, and
    /// names the cell the way <c>widget_click</c> addresses it — <c>target:&lt;key&gt;/client</c>.
    ///
    /// <para>Pinned on the source: the walk is <c>ScreenRectOf</c> over live
    /// <c>RectTransform</c>s and the fake cell runtime in this suite models no rects at all, so
    /// there is nothing here that could hit-test one. What the live op must now report is in the
    /// job's report.</para>
    /// </summary>
    [Fact]
    public void The_pointer_probe_walks_every_cell_row_and_names_the_cell_widget_click_addresses()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        SourcePin.AssertStatement(
            SourcePin.MethodBody(runtime, "internal static object DescribePointerHit("),
            "try { DescribeCellHit(unityX, unityY, screenHeight, stripRowHit, ref clickWouldInvoke, ref clickWouldInvokeWhy); }");
        string cellHit = SourcePin.MethodBody(runtime, "private static void DescribeCellHit(");
        SourcePin.AssertUncommented(cellHit, "panel.ForEachCellButton((rowKey, cellKey, go) =>");
        SourcePin.AssertUncommented(cellHit, "string label = rowKey + \"/\" + cellKey;");
        SourcePin.AssertUncommented(cellHit, "found = ModsMenuTabDiag.ClickWouldInvoke(-1, label);");
        SourcePin.AssertUncommented(cellHit, "clickWouldInvokeWhy = null;");

        // The registry the walk reads: every live cell row of the panel, and each row's clickable
        // cells. A row inserted by a later rebuild is in _cellRows like any other, which is why
        // the chooser row needed no registration of its own.
        string panel = SourcePin.MethodBody(runtime, "internal void ForEachCellButton(");
        SourcePin.AssertUncommented(panel, "foreach (var pair in _cellRows)");
        SourcePin.AssertUncommented(panel, "row.ForEachClickableCell(body);");
        string row = SourcePin.MethodBody(runtime, "internal void ForEachClickableCell(");
        SourcePin.AssertUncommented(row, "body(_spec.Key, pair.Key, cell.Go);");

        // And the label the probe builds is the one the invoke path parses: rowKey/cellKey.
        // INSTALLUX1 item 2 removed the `target:<key>/client` chooser cells, so the pair pinned here
        // is the confirmation's own Yes, which is the cell the bridge now clicks.
        Assert.Equal("prompt:dyno-Bags/promptYes", InstallModsPane.PromptKeyPrefix + "dyno-Bags" + "/" + InstallModsPane.CellPromptYes);
    }


    /// <summary>
    /// Job LIST4 item 3, second pass — the live defect (client gen 7, deploy #63, 3840×1080),
    /// in the coordinate space the readback actually uses.
    ///
    /// <para><b>The space.</b> <c>widget_read</c> / <c>rects</c> <c>y</c> is Unity screen space:
    /// bottom-origin, y growing upward, so a row further down the content has a <i>smaller</i> y
    /// and a rect's top edge is <c>y + height</c>. The Install list viewport read
    /// <c>{x 1108, y 336, width 1624, height 708}</c> — it spans y 336‥1044.</para>
    ///
    /// <para><b>The defect.</b> Opening the bottom non-tool row <c>Nightglass-PopupCounts</c> left
    /// <c>scrolledTo {rowTop 618, viewportTop 618, viewportHeight 708}</c> with that row's rect at
    /// <c>{y 73, h 53}</c> — its top edge at y 126, about 210 px <b>below</b> the viewport's bottom
    /// edge — the drawer head at <c>{y 25}</c> and the chooser row's <c>client</c> chip center at
    /// y −3, off the screen entirely, so <c>hit</c> answered no-hit and a real click did nothing.
    /// The first row <c>BepInEx-BepInExPack_V_Rising</c> was still at <c>{y 683}</c>, inside the
    /// viewport: the content had not been scrolled nearly far enough. With the row's top edge at
    /// 126 and the viewport's at 1044 the offset that puts one on the other is 918, not 618 —
    /// the reveal measured the row 300 px from where it finally sat, because inside
    /// <c>Build</c> the rows are still an intermediate layout.
    ///
    /// <para><b>The rule, on exactly those numbers.</b> Revealing row 11 must put its top edge on
    /// the viewport's top edge (rect <c>y ≈ 991</c>) and leave the chooser row inside 336‥1044;
    /// <c>scrolledTo.rowTop</c> must be the content offset that does it.</para>
    /// </summary>
    [Fact]
    public void The_revealed_row_lands_on_the_viewports_top_edge_in_bottom_origin_screen_space()
    {
        // The live rects, bottom-origin. rect.y is the BOTTOM edge; the top edge is y + height.
        const float viewportBottomY = 336f;
        const float viewportHeight = 708f;
        const float viewportTopY = viewportBottomY + viewportHeight;   // 1044
        const float rowHeight = 53f;
        const float firstRowY = 683f;                                  // row 0, inside the viewport
        const float openedRowY = 73f;                                  // row 11, below the viewport
        const float openedRowTopY = openedRowY + rowHeight;            // 126
        const float contentHeight = 4000f;

        Assert.Equal(1044f, viewportTopY, 3);
        Assert.Equal(126f, openedRowTopY, 3);
        // The row that was opened sits below the viewport's bottom edge, which is the defect's
        // starting condition; the first row is inside it.
        Assert.True(openedRowTopY < viewportBottomY, "the opened row must start below the list");
        Assert.InRange(firstRowY + rowHeight, viewportBottomY, viewportTopY);

        // The drawer under it, in the same space: further down the content is a smaller y.
        const float drawerHeight = 300f;
        const float chooserTopBelowRow = 90f;
        const float chooserHeight = 32f;
        float blockBottomY = openedRowTopY - (rowHeight + drawerHeight);

        float offset = MenuPanelLayout.RevealOffsetFromScreen(
            openedRowTopY, blockBottomY, viewportTopY, viewportHeight, 0f, contentHeight, out float rowTop);

        // scrolledTo.rowTop IS the content offset that puts the row on the viewport's top edge:
        // 1044 − 126 = 918, the number the live run should have written instead of 618.
        Assert.Equal(918f, rowTop, 3);
        Assert.Equal(918f, offset, 3);
        Assert.Equal(rowTop, offset, 3);
        Assert.NotEqual(618f, offset);

        // After the write, the content has moved up by the offset: every row's y grows by it.
        float revealedRowY = openedRowY + offset;
        float revealedRowTopY = revealedRowY + rowHeight;
        Assert.Equal(991f, revealedRowY, 3);
        Assert.Equal(viewportTopY, revealedRowTopY, 3);

        // The chooser row is then inside the viewport, both edges, and so is the chip center the
        // bridge clicks — it was at y −3.
        float chooserTopY = revealedRowTopY - chooserTopBelowRow;
        float chooserBottomY = chooserTopY - chooserHeight;
        Assert.InRange(chooserBottomY, viewportBottomY, viewportTopY);
        Assert.InRange(chooserTopY, viewportBottomY, viewportTopY);
        float chipCentreY = chooserBottomY + chooserHeight / 2f;
        Assert.InRange(chipCentreY, viewportBottomY, viewportTopY);
        Assert.True(chipCentreY > 0f, "chip center off the screen: " + chipCentreY);

        // The first row is scrolled off the top, which is what opening the bottom row means.
        Assert.True(firstRowY + offset > viewportTopY, "the first row should have left the viewport");

        // The branch the live numbers exercised: the block would have ended 1271 px down the
        // content with only 708 px of window from the current offset, so it is not "already
        // visible" and the row goes to the top — not because the block is taller than the
        // viewport (353 px is not), but because it starts below it.
        Assert.True(rowHeight + drawerHeight < viewportHeight, "the block itself fits; it is the position that does not");
        Assert.True(rowTop + rowHeight + drawerHeight > 0f + viewportHeight, "the block must not already be inside the window");
        // A row whose block already fits inside the window does not move it.
        float settledRowTopY = viewportTopY - 40f;
        Assert.Equal(
            100f,
            MenuPanelLayout.RevealOffsetFromScreen(
                settledRowTopY, settledRowTopY - 80f, viewportTopY, viewportHeight, 100f, contentHeight, out _),
            3);
        // Revert: reading rect.y as a top-down client coordinate (the first pass's test), or
        // measuring the row inside Build before the rows are placed, reproduces the 618.
    }

    /// <summary>
    /// Job LIST4 item 3 readback contract. <c>scrolledTo.rowTop</c> is the content offset the
    /// reveal wrote — the same number as <c>viewportTop</c> whenever the rule moved the row to the
    /// top of the viewport, because scrolling by the row's own distance down the content is what
    /// puts its top edge on the viewport's. The two differ in exactly two cases, and a readback
    /// that confused them would be describing two different layouts at once.
    ///
    /// <para>Live at 3840×1080 a visibly correct reveal (row at the top of the list, drawer fully
    /// shown) reported <c>scrolledTo {rowTop 313, viewportTop 313}</c> beside that row's own rect
    /// at <c>{y 691}</c> — 300 px from where it was drawn. The offset pair was right; the row rect
    /// in the same readback predated the layout pass. That is why this asserts the pair against
    /// the written offset and never against a row rect.</para>
    /// </summary>
    [Fact]
    public void ScrolledTo_rowTop_is_the_offset_the_reveal_wrote_except_when_it_did_not_move_the_row()
    {
        const float viewportTopY = 1044f;
        const float viewportHeight = 708f;
        const float rowHeight = 53f;
        const float contentHeight = 4000f;
        const float maxOffset = contentHeight - viewportHeight;   // 3292

        // Moved the row to the top: the offset written IS rowTop.
        float rowTopEdgeY = 126f;
        float written = MenuPanelLayout.RevealOffsetFromScreen(
            rowTopEdgeY, rowTopEdgeY - (rowHeight + 300f), viewportTopY, viewportHeight, 0f, contentHeight,
            out float rowTop);
        Assert.Equal(918f, rowTop, 3);
        Assert.Equal(rowTop, written, 3);

        // Nothing to do: the block already fitted, so the offset is the one the list already held
        // and is NOT rowTop. The readback says so rather than claiming a scroll.
        float settledTopY = viewportTopY - 40f;
        float unchanged = MenuPanelLayout.RevealOffsetFromScreen(
            settledTopY, settledTopY - 80f, viewportTopY, viewportHeight, 100f, contentHeight,
            out float settledRowTop);
        Assert.Equal(100f, unchanged, 3);
        Assert.Equal(140f, settledRowTop, 3);
        Assert.NotEqual(settledRowTop, unchanged);

        // The last row: clamped to the content's end, so the written offset is SMALLER than rowTop
        // and the row stops short of the viewport's top edge — as visible as the list can make it.
        float lastRowTopEdgeY = viewportTopY - (contentHeight - 20f);
        float clamped = MenuPanelLayout.RevealOffsetFromScreen(
            lastRowTopEdgeY, lastRowTopEdgeY - rowHeight, viewportTopY, viewportHeight, 0f, contentHeight,
            out float lastRowTop);
        Assert.Equal(contentHeight - 20f, lastRowTop, 3);
        Assert.Equal(maxOffset, clamped, 3);
        Assert.True(clamped < lastRowTop, "the last row must clamp to the content's end");

        // And the runtime reports the offset the ScrollRect holds, not the one it asked for.
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string reveal = SourcePin.MethodBody(runtime, "internal static bool TryRevealBlock(");
        SourcePin.AssertUncommented(reveal, "try { viewportTop = content.anchoredPosition.y; }");
        // Revert: reporting rowTop from one read and the offset from another, or reporting the
        // asked-for offset, breaks the pair's meaning.
    }

    /// <summary>
    /// The measurement must happen after this build's rows are placed, which is why the pane posts
    /// the reveal through its own marshaler rather than measuring inside <c>Build</c>, and drops a
    /// post whose panel has since been replaced.
    /// </summary>
    [Fact]
    public void The_anchor_is_posted_after_the_build_and_a_stale_post_is_dropped()
    {
        string pane = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "InstallModsPane.cs");
        string ask = SourcePin.MethodBody(pane, "private void AnchorClickedRow()");
        SourcePin.AssertUncommented(ask, "_post(() =>");
        SourcePin.AssertUncommented(ask, "if (!ReferenceEquals(panel, _panel)) return;");
        SourcePin.AssertUncommented(ask, "AnchorRowNow(key, wanted.Value);");
    }

    private static CatalogEntry Watermark() =>
        InstallListViewTests.Listing(
            "VRising-RemoveBuildNumberLabel",
            "Remove Version Watermark",
            description: "Removes version watermark");
}
