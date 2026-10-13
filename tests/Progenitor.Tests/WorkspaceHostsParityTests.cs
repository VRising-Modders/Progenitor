using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// HOSTS1 (decided 2026-09-30): the title-screen host must look like the in-game host at every
/// resolution, cold and warm, and both hosts must answer Escape the same way. The in-game fixtures are
/// the frame log lines the coordinator captured on the Steam client (probe P1,
/// internal research notes):
/// <list type="bullet">
/// <item>1600×1024 windowed (canvas 1687.5×1080): <c>native-container applied=1664x916 at=(0,10) split=360/1288 native=1664x870</c></item>
/// <item>1920×1080: <c>applied=1664x916 at=(0,-46) split=360/1288</c></item>
/// <item>3840×1080: <c>applied=1664x916 at=(0,-46) split=360/1288</c></item>
/// </list>
/// Before HOSTS1 the title drew <c>main-menu-ratio 1644x860 at=(0,-70)</c> warm and
/// <c>fallback 1276x800 at=(0,-70)</c> cold (probes P2/P3). The after-probe (2026-09-30,
/// <c>reviews/2026-09-30-hosts-after</c>) found a <c>1920 1080 … -20</c> record written from the first,
/// unsized-canvas read of a 1600×1024 session; the settle, canvas and plausibility gates below exist for it.
/// </summary>
public sealed class WorkspaceHostsParityTests : IDisposable
{
    private const float Tolerance = 2f;

    public WorkspaceHostsParityTests()
    {
        ModsWorkspaceFrame.ResetForTests();
        WorkspaceInputPolicy.ResetForTests();
    }

    public void Dispose()
    {
        NativeFrameMemory.Directory = () => null;
        NativeFrameMemory.GameBuild = () => "tests";
        NativeFrameMemory.NowMs = () => Environment.TickCount64;
        ModsWorkspaceFrame.ResetForTests();
        WorkspaceInputPolicy.ResetForTests();
    }

    /// <summary>
    /// The canonical window's center y on a 1080-unit canvas: the vanilla window (top 128, 916 tall; Map,
    /// Social, Spellbook at screen y 127..1043), at every width (round 8).
    /// </summary>
    private const float CanonicalY = -46f;

    /// <summary>
    /// Canvas (reference 1920×1080, match height), and the in-game frame's center y as read live. Both
    /// native variants are here (round 3): -46 with the stale inner-tab row (p1 1920×1080 and 3840×1080,
    /// <c>InnerTabGroup</c> entries laid out at y 79), +10 without it (p1 1600×1024, r2 3840×1080, entries 0×0).
    /// </summary>
    public static IEnumerable<object[]> Probes()
    {
        yield return new object[] { "1600x1024 p1", 1600f / 1024f * 1080f, 1080f, 10f };
        yield return new object[] { "1920x1080 p1 (row)", 1920f, 1080f, -46f };
        yield return new object[] { "3840x1080 p1 (row)", 3840f, 1080f, -46f };
        yield return new object[] { "3840x1080 r2", 3840f, 1080f, 10f };
    }

    public static IEnumerable<object[]> ProbesColdAndWarm()
    {
        foreach (var p in Probes())
        {
            yield return p.Concat(new object[] { false }).ToArray();
            yield return p.Concat(new object[] { true }).ToArray();
        }
    }

    /// <summary>ViewParent (1664×870) and its footer-grown extent (1664×916), top-aligned, as read in game.</summary>
    private static ModsWorkspaceFrame InGame(float canvasW, float canvasH, float frameY)
    {
        float top = frameY + 916f * .5f;
        var viewParent = new NativeContentRect(true, 1664f, 870f, 0f, top - 870f * .5f, "");
        var extent = new NativeContentRect(true, 1664f, 916f, 0f, frameY, "ViewParent+ConsoleInputsGroup");
        return ModsWorkspaceFrame.Resolve(canvasW, canvasH, "top-tab", viewParent, extent);
    }

    /// <summary>The in-game workspace shown and unchanged long enough to be recorded.</summary>
    private static ModsWorkspaceFrame Settled(float canvasW, float canvasH, float frameY)
    {
        var frame = InGame(canvasW, canvasH, frameY);
        for (int i = 0; i < NativeFrameSettle.Frames; i++)
            WorkspaceHostLayout.ObserveInGame(canvasW, canvasH, frame, shownInGame: true, canvasMeasured: true);
        return frame;
    }

    private static ModsWorkspaceFrame Title(float canvasW, float canvasH) =>
        ModsWorkspaceFrame.Resolve(canvasW, canvasH, "main-menu", NativeContentRect.Missing("title-screen"));

    private static void Near(float expected, float actual, string what) =>
        Assert.True(Math.Abs(expected - actual) <= Tolerance, what + ": expected " + expected + " got " + actual);

    private static void SameGeometry(in MenuPaneGeometry a, in MenuPaneGeometry b, string what)
    {
        Near(a.Width, b.Width, what + " width");
        Near(a.Height, b.Height, what + " height");
        Near(a.X, b.X, what + " x");
        Near(a.Y, b.Y, what + " y");
    }

    [Theory]
    [MemberData(nameof(Probes))]
    public void The_in_game_fixture_reproduces_the_probe_log_line(string name, float canvasW, float canvasH, float frameY)
    {
        // As read (pin off): the live log line.
        WorkspaceHostLayout.PinInGameFrame = false;
        var frame = InGame(canvasW, canvasH, frameY);
        Assert.Equal(ModsWorkspaceFrame.PathNative, frame.Path);
        string line = ModsWorkspaceFrame.FormatLog(frame, new NativeContentRect(true, 1664f, 870f, 0f, 0f, ""), "");
        Assert.StartsWith(
            ModsWorkspaceFrame.LogPrefix + "native-container applied=1664x916 at=(0," + (int)frameY + ") split=360/1288 native=1664x870",
            line);

        // Pinned (default on; round 8: to the vanilla window): both variants draw at the vanilla place; the
        // collapsed variant (top 72) says it was moved down.
        WorkspaceHostLayout.PinInGameFrame = true;
        frame = InGame(canvasW, canvasH, frameY);
        line = ModsWorkspaceFrame.FormatLog(frame, new NativeContentRect(true, 1664f, 870f, 0f, 0f, ""), "");
        Assert.StartsWith(ModsWorkspaceFrame.LogPrefix + "native-container applied=1664x916 at=(0,-46) split=360/1288", line);
        bool collapsed = frameY > 0f;
        Assert.Equal(collapsed, line.Contains(" pinned=down56", StringComparison.Ordinal));
        Assert.Equal(collapsed ? NativeFrameModel.InnerTabRow : 0f, frame.CoverBelow);
        // The window is the pane, the whole 916: the native strip lies inside it in both layouts.
        var art = WorkspaceHostLayout.ArtworkRect(frame);
        var pane = MenuPanelLayout.ForFrame(frame, false, MenuPaneMode.FullWidth);
        SameGeometry(pane, art, name + " art is the pane");
        Near(916f, pane.Height, name + " the whole window height");
        Assert.False(WorkspaceHostLayout.HidesVacatedStrip(frame));
        Assert.Equal("ConsoleInputsGroup", WorkspaceHostLayout.VacatedStripName);
        _ = name;
    }

    [Theory]
    [MemberData(nameof(ProbesColdAndWarm))]
    public void The_title_reproduces_the_in_game_frame_strip_trim_and_panes(
        string name, float canvasW, float canvasH, float frameY, bool warm)
    {
        if (warm) Settled(canvasW, canvasH, frameY);
        // Both native variants map to one title frame: the in-game frame without the stale inner-tab row.
        ModsWorkspaceFrame.ResetFirstShow();
        var inGame = InGame(canvasW, canvasH, CanonicalY);
        var measured = new NativeFrameRecord(canvasW, canvasH, 1664f, 916f, 0f, frameY);
        Near(CanonicalY, NativeFrameModel.Canonical(measured).Y, name + " canonical of the live read");
        Assert.Equal(frameY > 0f, NativeFrameModel.IsCollapsed(measured));
        var title = Title(canvasW, canvasH);

        Assert.Equal(ModsWorkspaceFrame.PathTitleHost, title.Path);
        Assert.Equal(warm ? WorkspaceHostLayout.SourceRecorded : WorkspaceHostLayout.SourceModel, ModsWorkspaceFrame.LastTitleSource);
        Near(inGame.Width, title.Width, name + " frame width");
        Near(inGame.Height, title.Height, name + " frame height");
        Near(inGame.X, title.X, name + " frame x");
        Near(inGame.Y, title.Y, name + " frame y");
        Near(inGame.TabLift, title.TabLift, name + " strip lift");
        Assert.Equal(MenuPanelLayout.StripConsumesFrameTop(inGame), MenuPanelLayout.StripConsumesFrameTop(title));
        Assert.True(MenuPanelLayout.StripInBand(title), "the title strip must stand in the band above the window, as in game");

        SameGeometry(MenuPanelLayout.ForStrip(inGame), MenuPanelLayout.ForStrip(title), name + " strip");
        Near(MenuPanelLayout.ArtworkPaneTop(inGame), MenuPanelLayout.ArtworkPaneTop(title), name + " trim line");
        SameGeometry(MenuPanelLayout.ForFrame(inGame, true), MenuPanelLayout.ForFrame(title, true), name + " sidebar");
        SameGeometry(MenuPanelLayout.ForFrame(inGame, false), MenuPanelLayout.ForFrame(title, false), name + " detail");
        SameGeometry(
            MenuPanelLayout.ForFrame(inGame, false, MenuPaneMode.FullWidth),
            MenuPanelLayout.ForFrame(title, false, MenuPaneMode.FullWidth), name + " artwork / install pane");
        SameGeometry(
            MenuPanelLayout.ForFrame(inGame, false, MenuPaneMode.ListWithAside),
            MenuPanelLayout.ForFrame(title, false, MenuPaneMode.ListWithAside), name + " keybinds list");
        SameGeometry(
            MenuPanelLayout.ForFrame(inGame, false, MenuPaneMode.Aside),
            MenuPanelLayout.ForFrame(title, false, MenuPaneMode.Aside), name + " keybinds description");

        MenuPanelLayout.Split(title.Width, out float side, out float detail, out _);
        Near(360f, side, name + " split sidebar");
        Near(1288f, detail, name + " split detail");
    }

    [Theory]
    [MemberData(nameof(Probes))]
    public void A_cold_title_no_longer_draws_the_floating_card(string name, float canvasW, float canvasH, float frameY)
    {
        var title = Title(canvasW, canvasH);
        var card = ModsWorkspaceFrame.Fallback(canvasW, canvasH);
        Assert.NotEqual(ModsWorkspaceFrame.PathFallback, title.Path);
        Assert.True(title.Width - card.Width > 300f, name + ": title " + title.Width + " vs card " + card.Width);
        Near(CanonicalY, title.Y, name + " modeled y");
        _ = frameY;
        Assert.True(title.Y + title.Height * .5f <= canvasH * .5f - WorkspaceHostLayout.BarHeight,
            name + ": the frame must stay below the bar band");
    }

    // ---- what gets recorded (after-probe 2026-09-30) -------------------------------------------

    [Fact]
    public void The_unsized_first_read_is_never_recorded_and_the_poisoned_record_loses_to_the_model()
    {
        // The live -20 row: a 1600x1024 session's first read, filed under the 1920x1080 default canvas.
        var unsized = InGame(1920f, 1080f, -20f);
        for (int i = 0; i < NativeFrameSettle.Frames * 2; i++)
            WorkspaceHostLayout.ObserveInGame(1920f, 1080f, unsized, shownInGame: true, canvasMeasured: false);
        Assert.Empty(NativeFrameMemory.Snapshot());

        // Even if such a row is on disk, it disagrees with the model by 26 units and is ignored.
        NativeFrameMemory.Remember(new NativeFrameRecord(1920f, 1080f, 1664f, 916f, 0f, -20f));
        var title = Title(3840f, 1080f);
        Assert.Equal(WorkspaceHostLayout.SourceModel, ModsWorkspaceFrame.LastTitleSource);
        Near(CanonicalY, title.Y, "3840 title y");
        title = Title(1920f, 1080f);
        Assert.Equal(WorkspaceHostLayout.SourceModel, ModsWorkspaceFrame.LastTitleSource);
        Near(CanonicalY, title.Y, "1920 title y");
    }

    [Fact]
    public void Both_live_variants_at_one_canvas_record_the_same_canonical_frame()
    {
        // p1 3840x1080 (row, -46) then r2 3840x1080 (no row, +10): one record, one title frame.
        Settled(3840f, 1080f, -46f);
        var first = NativeFrameMemory.Snapshot().Single();
        Near(CanonicalY, first.Y, "row variant recorded canonical");
        Settled(3840f, 1080f, 10f);
        var second = NativeFrameMemory.Snapshot().Single();
        Assert.True(first.SameFrame(second), "the second variant must not rewrite the record");
        var title = Title(3840f, 1080f);
        Assert.Equal(WorkspaceHostLayout.SourceRecorded, ModsWorkspaceFrame.LastTitleSource);
        Near(CanonicalY, title.Y, "title");
        Assert.Equal(WorkspaceHostLayout.MaxTabLift, title.TabLift);
    }

    [Fact]
    public void An_exact_record_beats_a_carried_one()
    {
        Settled(1687.5f, 1080f, 10f);
        Settled(1920f, 1080f, -46f);
        var narrow = Title(1687.5f, 1080f);
        Assert.Equal(WorkspaceHostLayout.SourceRecorded, ModsWorkspaceFrame.LastTitleSource);
        Near(CanonicalY, narrow.Y, "1687.5 exact");
        // Within the one tolerance, a canvas a fraction of a unit off is still the exact record.
        Title(1687.9f, 1080f);
        Assert.Equal(WorkspaceHostLayout.SourceRecorded, ModsWorkspaceFrame.LastTitleSource);
        var ultrawide = Title(3840f, 1080f);
        Assert.Equal(WorkspaceHostLayout.SourceCarried, ModsWorkspaceFrame.LastTitleSource);
        Near(CanonicalY, ultrawide.Y, "carried 16:9 (row removed) to 32:9");
    }

    [Fact]
    public void Only_a_frame_unchanged_for_the_settle_window_while_shown_is_recorded()
    {
        var frame = InGame(1920f, 1080f, -46f);
        for (int i = 0; i < NativeFrameSettle.Frames - 1; i++)
            Assert.False(WorkspaceHostLayout.ObserveInGame(1920f, 1080f, frame, shownInGame: true, canvasMeasured: true));
        // A tab transition (the frame moves for one frame) restarts the count.
        var moving = InGame(1920f, 1080f, -30f);
        Assert.False(WorkspaceHostLayout.ObserveInGame(1920f, 1080f, moving, shownInGame: true, canvasMeasured: true));
        for (int i = 0; i < NativeFrameSettle.Frames - 1; i++)
            Assert.False(WorkspaceHostLayout.ObserveInGame(1920f, 1080f, frame, shownInGame: true, canvasMeasured: true));
        Assert.True(WorkspaceHostLayout.ObserveInGame(1920f, 1080f, frame, shownInGame: true, canvasMeasured: true));
        // Committed once: more of the same frame records nothing.
        Assert.False(WorkspaceHostLayout.ObserveInGame(1920f, 1080f, frame, shownInGame: true, canvasMeasured: true));
        // Hidden (not shown) never counts.
        var other = InGame(3840f, 1080f, -46f);
        for (int i = 0; i < NativeFrameSettle.Frames * 2; i++)
            Assert.False(WorkspaceHostLayout.ObserveInGame(3840f, 1080f, other, shownInGame: false, canvasMeasured: true));
        Assert.Single(NativeFrameMemory.Snapshot());
    }

    [Fact]
    public void A_narrow_canvas_bounds_the_record_and_keeps_side_margins()
    {
        var r = NativeFrameModel.Predict(1350f, 1080f); // 1280x1024 at match height (inferred: not probed)
        Assert.True(r.Width <= 1350f - NativeFrameModel.MinMargin * 2f + .01f);
        Assert.Equal(0f, r.X);
        Assert.True(r.Y + r.Height * .5f <= 540f);
        Assert.True(r.Y - r.Height * .5f >= -540f);

        // A record carried to a narrower canvas is bounded through Fit, never wider than the canvas.
        NativeFrameMemory.Remember(new NativeFrameRecord(1440f, 1080f, 1664f, 916f, 0f, -46f));
        var title = Title(1350f, 1080f);
        Assert.Equal(WorkspaceHostLayout.SourceCarried, ModsWorkspaceFrame.LastTitleSource);
        Assert.True(title.Width <= 1350f - NativeFrameModel.MinMargin * 2f + .01f);
    }

    // ---- the file --------------------------------------------------------------------------------

    [Fact]
    public void The_record_survives_a_restart_through_a_debounced_atomic_file_of_the_same_build()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-hosts-" + Guid.NewGuid().ToString("N"));
        long now = 1_000_000;
        try
        {
            NativeFrameMemory.Directory = () => dir;
            NativeFrameMemory.NowMs = () => now;
            NativeFrameMemory.ForgetForTests();
            string file = Path.Combine(dir, NativeFrameMemory.FileName);

            var inGame = Settled(1687.5f, 1080f, 10f);
            Assert.False(File.Exists(file)); // debounced
            now += NativeFrameMemory.DebounceMs;
            WorkspaceHostLayout.ObserveInGame(1687.5f, 1080f, inGame, shownInGame: true, canvasMeasured: true);
            Assert.True(File.Exists(file));
            Assert.False(File.Exists(file + ".tmp"));
            Assert.Contains(NativeFrameMemory.BuildKey + "\ttests", File.ReadAllText(file));

            // Another process added a canvas meanwhile: a later write keeps it.
            string other = NativeFrameMemory.Format(new[]
            {
                new NativeFrameRecord(2560f, 1080f, 1664f, 916f, 0f, -46f),
                new NativeFrameRecord(1687.5f, 1080f, 1664f, 916f, 0f, 10f),
            }, "tests");
            File.WriteAllText(file, other);
            Settled(1920f, 1080f, -46f);
            Assert.True(NativeFrameMemory.Flush(force: true));
            var disk = NativeFrameMemory.Parse(File.ReadAllText(file), "tests");
            Assert.Contains(disk, r => r.SameCanvas(2560f, 1080f));
            Assert.Contains(disk, r => r.SameCanvas(1920f, 1080f));

            // A fresh process of the same build reads it.
            NativeFrameMemory.ForgetForTests();
            ModsWorkspaceFrame.ResetFirstShow();
            var title = Title(1687.5f, 1080f);
            Assert.Equal(WorkspaceHostLayout.SourceRecorded, ModsWorkspaceFrame.LastTitleSource);
            Near(inGame.Y, title.Y, "y after restart");

            // Another game build ignores the file.
            NativeFrameMemory.GameBuild = () => "another-build";
            NativeFrameMemory.ForgetForTests();
            Title(1687.5f, 1080f);
            Assert.Equal(WorkspaceHostLayout.SourceModel, ModsWorkspaceFrame.LastTitleSource);
        }
        finally
        {
            NativeFrameMemory.Directory = () => null;
            NativeFrameMemory.GameBuild = () => "tests";
            try { Directory.Delete(dir, true); } catch { /* */ }
        }
    }

    [Fact]
    public void The_record_file_round_trips_skips_damage_needs_a_stamp_and_keeps_eight_canvases()
    {
        var records = new List<NativeFrameRecord>();
        for (int i = 0; i < 10; i++) records.Add(new NativeFrameRecord(1920f + i * 100f, 1080f, 1664f, 916f, 0f, -46f));
        string text = NativeFrameMemory.Format(records, "b1") + "garbage\tline\n1\t2\t3\n";
        var parsed = NativeFrameMemory.Parse(text, "b1");
        Assert.Equal(NativeFrameMemory.Capacity, parsed.Count);
        Assert.True(parsed[0].SameFrame(records[0]));
        Assert.Empty(NativeFrameMemory.Parse(text, "b2"));
        Assert.Empty(NativeFrameMemory.Parse("# header only\n1920\t1080\t1664\t916\t0\t-46\n", "b1")); // no stamp
        Assert.Empty(NativeFrameMemory.Parse(null, "b1"));

        for (int i = 0; i < 10; i++) NativeFrameMemory.Remember(records[i]);
        Assert.Equal(NativeFrameMemory.Capacity, NativeFrameMemory.Snapshot().Count);
        Assert.True(NativeFrameMemory.Snapshot()[0].SameFrame(records[9]));

        // Two canvases read in turn change nothing once both are held.
        Assert.False(NativeFrameMemory.Remember(records[8]));
        Assert.False(NativeFrameMemory.Remember(records[9]));
        Assert.False(NativeFrameMemory.Remember(records[8]));
    }

    // ---- title band, trim ornament ---------------------------------------------------------------

    [Theory]
    [MemberData(nameof(Probes))]
    public void The_band_sits_where_the_game_bar_does_and_the_strip_stays_below_it(
        string name, float canvasW, float canvasH, float frameY)
    {
        var inGame = InGame(canvasW, canvasH, frameY);
        var title = Title(canvasW, canvasH);
        var band = WorkspaceHostLayout.TopBand(canvasW, canvasH);
        Assert.Equal(canvasW, band.Width, 3);
        Assert.Equal(WorkspaceHostLayout.BarHeight, band.Height, 3);
        Assert.Equal(canvasH * .5f, band.Y + band.Height * .5f, 3);
        float bandBottom = band.Y - band.Height * .5f;
        foreach (var frame in new[] { inGame, title })
        {
            var strip = MenuPanelLayout.ForStrip(frame);
            Assert.True(strip.Y + strip.Height * .5f <= bandBottom + .01f, name + ": the strip reaches into the bar");
        }

        // Review M1: only the title draws the band; the standalone window in a world would cover the HUD.
        Assert.True(WorkspaceHostLayout.DrawsTopBand(WorkspaceHost.Title));
        Assert.False(WorkspaceHostLayout.DrawsTopBand(WorkspaceHost.Standalone));
        Assert.False(WorkspaceHostLayout.DrawsTopBand(WorkspaceHost.InGame));
    }

    [Theory]
    [MemberData(nameof(ProbesColdAndWarm))]
    public void The_window_is_the_vanilla_rectangle_with_Spellbook_tabs_above_and_the_notch_right_of_them(
        string name, float canvasW, float canvasH, float frameY, bool titleHost)
    {
        var frame = titleHost ? Title(canvasW, canvasH) : InGame(canvasW, canvasH, frameY);
        // The vanilla window at 1080 units: 1664x916, top 128, bottom 1044 (screen y 127..1043 at 1080p).
        var window = WorkspaceHostLayout.WindowRect(frame);
        var screen = WorkspaceHostLayout.ToScreen(window, canvasW, canvasH, canvasW, canvasH);
        Near(1664f, screen.W, name + " window width");
        Near(916f, screen.H, name + " window height");
        Near(128f, screen.Y, name + " window top");
        Near(1044f, screen.Y + screen.H, name + " window bottom");
        Near((canvasW - 1664f) * .5f, screen.X, name + " window left");

        // Round 13: Spellbook's tab row: rects 48 tall (the frame drawn y 88..128), bottom on the window top,
        // at least 176 wide, 2 apart (drawn 10 apart), starting 24 in from the window's left edge.
        Assert.True(MenuPanelLayout.StripInBand(frame), name);
        var band = WorkspaceHostLayout.TabBandRect(frame);
        var bandScreen = WorkspaceHostLayout.ToScreen(band, canvasW, canvasH, canvasW, canvasH);
        Near(128f, bandScreen.Y + bandScreen.H, name + " band bottom on the window top");
        Near(80f, bandScreen.Y, name + " band top");
        Assert.True(bandScreen.Y >= WorkspaceHostLayout.BarHeight, name + ": the tabs stay below the top bar");
        int n = ModsTabStrip.Order.Length;
        float previousRight = float.NaN;
        for (int k = 0; k < n; k++)
        {
            var tab = MenuPanelLayout.StripButton(band, k, n);
            Near(WorkspaceHostLayout.TabHeight, tab.Height, name + " tab height");
            Assert.True(tab.Width >= WorkspaceHostLayout.TabMinWidth - .01f, name + ": tab under Spellbook's width");
            float tabLeft = tab.X - tab.Width * .5f;
            if (k == 0) Near(screen.X + WorkspaceHostLayout.StripLeftInset, WorkspaceHostLayout.ToScreen(tab, canvasW, canvasH, canvasW, canvasH).X, name + " first tab left");
            else Near(previousRight + WorkspaceHostLayout.TabGap, tabLeft, name + " gap");
            previousRight = tab.X + tab.Width * .5f;
        }

        // The panes take the whole window.
        Near(916f, MenuPanelLayout.ForFrame(frame, false).Height, name + " detail pane");

        // User, 2026-10-06: the ornament is centered in the dead space of the silver line right of the last tab.
        var trim = WorkspaceHostLayout.Trim(frame, n);
        float lineRight = window.X + window.Width * .5f;
        Assert.True(trim.Split, name + ": the ornament leaves the line's center for the dead space");
        Near((previousRight + lineRight) * .5f, trim.NotchX, name + " ornament centered in the dead space");
        Near(previousRight, trim.LastTabRight, name + " last tab right");
        Assert.True(trim.NotchX - WorkspaceHostLayout.TrimNotchClearance >= previousRight, name + ": the ornament overlaps a tab");
        Near(lineRight, trim.RightTo, name + " line right");
        // The centered variant is still available when asked for.
        var centered = WorkspaceHostLayout.Trim(frame, n, centered: true);
        Assert.False(centered.Split, name);
        Near(WorkspaceHostLayout.TrimLineCenter(frame), centered.NotchX, name + " line center helper");
    }

    [Fact]
    public void Tabs_are_sized_to_their_captions_with_Spellbooks_minimum_and_fit_a_narrow_strip()
    {
        Assert.Equal(WorkspaceHostLayout.TabMinWidth, WorkspaceHostLayout.TabWidthFor("Mods", 1f));
        Assert.True(WorkspaceHostLayout.TabWidthFor("Configuration", 1f) > WorkspaceHostLayout.TabMinWidth);
        var widths = WorkspaceHostLayout.TabWidths(new[] { "Mods", "Install Mods", "Configuration", "Keybinds" }, 400f, 1f);
        float total = widths.Sum() + WorkspaceHostLayout.TabGap * 3f;
        Assert.InRange(total, 399.5f, 400.5f); // shrunk together to fit
    }

    // ---- artwork intensity -------------------------------------------------------------------------

    [Fact]
    public void The_artwork_look_matches_the_game_menus_measured_mean_and_spread()
    {
        Assert.Equal(0.9f, WorkspaceArtworkIntensity.Default);
        Assert.Equal(0.18f, WorkspaceArtworkIntensity.DefaultContrast);
        Assert.Equal(0.2f, WorkspaceArtworkIntensity.Clamp(0.05f));
        Assert.Equal(1f, WorkspaceArtworkIntensity.Clamp(3f));
        Assert.Equal(WorkspaceArtworkIntensity.Default, WorkspaceArtworkIntensity.Clamp(float.NaN));
        Assert.Equal(0.1f, WorkspaceArtworkIntensity.ClampContrast(0f));
        Assert.Equal(1f, WorkspaceArtworkIntensity.ClampContrast(2f));

        // Warm: no blue boost (the game's menus are dark red).
        var c = WorkspaceArtworkIntensity.ArtColor(WorkspaceArtworkIntensity.Default);
        Assert.Equal(1f, c.A);
        Assert.True(c.R > c.G && c.G > c.B, "red first, blue last");
        var floor = WorkspaceArtworkIntensity.FloorColor(WorkspaceArtworkIntensity.DefaultContrast);
        Assert.Equal(1f - WorkspaceArtworkIntensity.DefaultContrast, floor.A, 4);
        Assert.True(floor.R > floor.G && floor.R > floor.B);

        // The defaults predict the live target: game 23/2.2/1.5 sd 13; aim 23/2/2, sd 13-15.
        var p = WorkspaceArtworkIntensity.Predict(WorkspaceArtworkIntensity.Default, WorkspaceArtworkIntensity.DefaultContrast);
        Assert.InRange(p.R, 21.5f, 24.5f);
        Assert.InRange(p.G, 1f, 3f);
        Assert.InRange(p.B, 1f, 3f);
        Assert.InRange(p.Sd, 13f, 15f);
        // The fit reproduces the live 0.9/0.28 reading it was taken from (19.9/2.9/2.8, sd 23.1).
        var live = WorkspaceArtworkIntensity.Predict(0.9f, 0.28f);
        var floor28 = WorkspaceArtworkIntensity.FloorR * 255f * 0.72f;
        _ = floor28;
        Assert.InRange(live.Sd, 22.5f, 23.6f);
        // And the art alone is far busier than the default.
        var bright = WorkspaceArtworkIntensity.Predict(1f, 1f);
        Assert.True(bright.Sd > 2f * p.Sd);
        Assert.Equal("Workspace", WorkspaceArtworkIntensity.Section);
        Assert.Equal("BackgroundIntensity", WorkspaceArtworkIntensity.Key);
        Assert.Equal("BackgroundContrast", WorkspaceArtworkIntensity.ContrastKey);

        // The multiply is on the artwork image, the floor on the scrim; the readback reports both.
        string chrome = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsWorkspaceChrome.cs");
        string apply = SourcePin.MethodBody(chrome, "private static void ApplyArtworkIntensity(");
        SourcePin.AssertUncommented(apply, "_image.color = new Color(c.R, c.G, c.B, c.A);");
        SourcePin.AssertUncommented(apply, "_scrim.color = new Color(f.R, f.G, f.B, f.A);");
        string mods = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertUncommented(mods, "described[\"backgroundIntensity\"]");
        SourcePin.AssertUncommented(mods, "backgroundIntensity = BackgroundIntensityApplied(),");
    }

    [Fact]
    public void An_unselected_tab_hovers_lighter_than_the_selected_tab()
    {
        var normal = (0.1f, 0.1f, 0.1f, 1f);
        var lit = (0.9f, 0.1f, 0.1f, 1f);
        var hover = WorkspaceTabHover.HoverColor(normal, lit);
        Assert.True(hover.Item1 > normal.Item1 && hover.Item1 < lit.Item1);
        Assert.True(WorkspaceTabHover.DimsHover(selected: false));
        Assert.False(WorkspaceTabHover.DimsHover(selected: true));
    }

    // ---- input policy parity ----------------------------------------------------------------

    public static IEnumerable<object[]> EscapeStates()
    {
        foreach (bool capture in new[] { false, true })
        foreach (bool dialog in new[] { false, true })
        foreach (bool focused in new[] { false, true })
        foreach (string text in new[] { "", "abc" })
            yield return new object[] { capture, dialog, focused, text };
    }

    [Theory]
    [MemberData(nameof(EscapeStates))]
    public void Every_host_orders_Escape_capture_then_dialog_then_search_then_close(
        bool capture, bool dialog, bool focused, string text)
    {
        EscapeStage expected = capture ? EscapeStage.CancelCapture
            : dialog ? EscapeStage.AnswerDialog
            : focused && text.Length > 0 ? EscapeStage.ClearSearch
            : focused ? EscapeStage.LeaveSearch
            : EscapeStage.Close;
        foreach (var host in new[] { WorkspaceHost.InGame, WorkspaceHost.Title, WorkspaceHost.Standalone })
        {
            var escape = WorkspaceInputPolicy.Escape(host, capture, dialog, focused, text);
            Assert.Equal(expected, escape.Stage);
            bool ownsPress = expected is EscapeStage.CancelCapture or EscapeStage.AnswerDialog;
            Assert.Equal(ownsPress, escape.SwallowPairedBack);

            var back = WorkspaceInputPolicy.Escape(host, capture, dialog, focused, text, keyCode: WorkspaceInputPolicy.BackAction);
            Assert.Equal(expected, back.Stage);
            Assert.False(back.SwallowPairedBack);
        }

        Assert.Equal(!capture && !dialog, WorkspaceInputPolicy.SearchMayClear(capture, dialog));
    }

    [Fact]
    public void A_press_a_dialog_answered_never_also_clears_the_search()
    {
        long now = 5000;
        WorkspaceInputPolicy.NowMs = () => now;
        // The hook answered the dialog; the pump then sees no dialog, but the press is taken.
        WorkspaceInputPolicy.NotePressTaken(EscapeStage.AnswerDialog);
        Assert.False(WorkspaceInputPolicy.SearchMayClear(captureOwnsEscape: false, dialogOpen: false));
        // Released: the next press may clear the search.
        WorkspaceInputPolicy.NotePressReleased();
        Assert.True(WorkspaceInputPolicy.SearchMayClear(false, false));
        // A missed release expires after the grace.
        WorkspaceInputPolicy.NotePressTaken(EscapeStage.CancelCapture);
        now += WorkspaceInputPolicy.PressTakenGraceMs + 1;
        Assert.True(WorkspaceInputPolicy.SearchMayClear(false, false));
        // A search clear or a close does not take the press for later stages.
        WorkspaceInputPolicy.NotePressTaken(EscapeStage.ClearSearch);
        Assert.False(WorkspaceInputPolicy.PressTaken);

        string ui = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "Ui.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(ui, "private static void OnBootstrapKey("), "WorkspaceInputPolicy.NotePressTaken(outcome.Stage);");
        string mods = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(mods, "internal static void NoteEscapeReleased("), "WorkspaceInputPolicy.NotePressReleased();");
    }

    [Fact]
    public void The_game_close_hold_is_the_policy_and_the_close_dismisses_only_in_game()
    {
        foreach (bool c in new[] { false, true })
        foreach (bool d in new[] { false, true })
        foreach (bool held in new[] { false, true })
        foreach (bool was in new[] { false, true })
            Assert.Equal(WorkspaceInputPolicy.HoldsGameClose(c, d, held, was), MenuHotkeyGuard.NextCloseHold(c, d, held, was));

        Assert.True(ModsWorkspaceFrame.ShouldDismissFullscreenMenuOnEscape("top-tab", searchCleared: false));
        Assert.False(ModsWorkspaceFrame.ShouldDismissFullscreenMenuOnEscape("top-tab", searchCleared: true));
        Assert.False(ModsWorkspaceFrame.ShouldDismissFullscreenMenuOnEscape("main-menu", searchCleared: false));
        Assert.False(ModsWorkspaceFrame.ShouldDismissFullscreenMenuOnEscape("none", searchCleared: false));

        Assert.Equal(WorkspaceHost.InGame, WorkspaceHostLayout.HostOf("top-tab"));
        Assert.Equal(WorkspaceHost.Title, WorkspaceHostLayout.HostOf("main-menu"));
        Assert.Equal(WorkspaceHost.Standalone, WorkspaceHostLayout.HostOf("none"));
    }

    [Fact]
    public void The_Escape_hook_and_the_search_clear_consult_the_policy()
    {
        string ui = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "Ui.cs");
        string hook = SourcePin.MethodBody(ui, "private static void OnBootstrapKey(");
        SourcePin.AssertUncommented(hook, "WorkspaceInputPolicy.Escape(");
        SourcePin.AssertUncommented(hook, "if (outcome.Stage == EscapeStage.CancelCapture)");
        SourcePin.AssertUncommented(hook, "if (outcome.Stage == EscapeStage.AnswerDialog)");

        string mods = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(mods, "internal static bool TryConsumeEscapeHide("), "WorkspaceInputPolicy.Escape(");
        SourcePin.AssertUncommented(SourcePin.MethodBody(mods, "internal static void TryClearSearchOnEscape("), "WorkspaceInputPolicy.SearchMayClear(");
    }

    // ---- list viewport below the pinned rows, both hosts --------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_keybinds_list_viewport_starts_below_the_pinned_rows_at_both_hosts(bool editBarShown)
    {
        foreach (var p in Probes())
        {
            float canvasW = (float)p[1], canvasH = (float)p[2], frameY = (float)p[3];
            ModsWorkspaceFrame.ResetForTests();
            _ = frameY;
            var inGame = InGame(canvasW, canvasH, CanonicalY);
            ModsWorkspaceFrame.ResetForTests();
            var title = Title(canvasW, canvasH);

            float bar = UnityUiRuntimeMetrics.RowHeight + 6f;
            var measured = editBarShown ? new[] { bar, bar } : new[] { bar };
            // The toolbar as placed can be taller than it measured on an earlier pass (the title's font
            // arrived late): the viewport must still clear it.
            var placed = measured.Select((h, i) => (MenuPanelLayout.TopRowOffset(measured, i), h + 4f)).ToList();
            float chrome = MenuPanelLayout.TopChromeHeight(measured);
            float top = WorkspaceHostLayout.ListViewportTop(0f, chrome, placed);
            foreach (var (t, h) in placed) Assert.True(top >= t + h, "a list row could draw behind a pinned row");

            var gamePane = MenuPanelLayout.ForFrame(inGame, false, KeybindsTabLayout.PaneMode);
            var titlePane = MenuPanelLayout.ForFrame(title, false, KeybindsTabLayout.PaneMode);
            SameGeometry(gamePane, titlePane, "keybinds pane");
            Assert.True(top < titlePane.Height, "the viewport must stay inside the pane");
        }

        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string layout = SourcePin.MethodBody(runtime, "private void RunLayout(");
        int place = layout.IndexOf("PlaceRow(_rows[i]);", StringComparison.Ordinal);
        int refit = layout.IndexOf("WorkspaceHostLayout.RefitAfterPlacement(topPins)", StringComparison.Ordinal);
        Assert.True(place >= 0 && refit > place, "the viewport must be refitted after this pass places the pinned rows");
    }

    // ---- open without a resize, and its timing (round 4) ---------------------------------------

    [Theory]
    [MemberData(nameof(Probes))]
    public void An_in_game_open_resolves_the_same_geometry_before_and_after_the_native_read(
        string name, float canvasW, float canvasH, float frameY)
    {
        // Frame 1 of an open: the overlay has no size yet (reason=no-overlay).
        var first = ModsWorkspaceFrame.Resolve(canvasW, canvasH, "top-tab", NativeContentRect.Missing("no-overlay"));
        Assert.Equal(ModsWorkspaceFrame.PathNativePending, first.Path);
        Assert.False(ModsWorkspaceFrame.MayPresent("top-tab", nativeFound: false, first.Path), "never shown before the read");
        // Frame 2: the native read, either live variant, pinned (default on).
        var second = InGame(canvasW, canvasH, frameY);
        Assert.True(ModsWorkspaceFrame.MayPresent("top-tab", nativeFound: true, second.Path));
        // A pane laid out on frame 1 already has the size it is shown at: no small-then-grow.
        SameGeometry(MenuPanelLayout.ForStrip(first), MenuPanelLayout.ForStrip(second), name + " strip");
        SameGeometry(MenuPanelLayout.ForFrame(first, true), MenuPanelLayout.ForFrame(second, true), name + " sidebar");
        SameGeometry(MenuPanelLayout.ForFrame(first, false), MenuPanelLayout.ForFrame(second, false), name + " detail");
        // And the title's frame is the same again.
        SameGeometry(MenuPanelLayout.ForFrame(Title(canvasW, canvasH), false), MenuPanelLayout.ForFrame(second, false), name + " title");
    }

    [Fact]
    public void The_open_timing_line_names_its_spans_and_what_they_do_not_cover()
    {
        WorkspaceOpenTiming.ResetForTests();
        long now = 0;
        int frame = 100;
        WorkspaceOpenTiming.Frequency = 1000; // ticks are ms
        WorkspaceOpenTiming.Ticks = () => now;
        WorkspaceOpenTiming.Frame = () => frame;
        try
        {
            Assert.Null(WorkspaceOpenTiming.Finish("shown"));
            WorkspaceOpenTiming.Begin();
            WorkspaceOpenTiming.Span("prepare", () => now += 12);
            WorkspaceOpenTiming.Span("strip", () => now += 5);
            WorkspaceOpenTiming.Span("show-layout", () => now += 3);
            WorkspaceOpenTiming.Span("show-layout", () => now += 4);
            now += 20; // Unity's own work between frames
            frame += 2;
            string? line = WorkspaceOpenTiming.Finish("shown path=native-container");
            Assert.Equal(
                "Mods workspace: open timing total=44ms frames=2 spans=[prepare=12 strip=5 show-layout=7] unaccounted=20ms end=shown path=native-container",
                line);
            Assert.Equal(line, WorkspaceOpenTiming.LastLine);
            // Outside an open the work runs untimed and nothing is logged.
            bool ran = false;
            WorkspaceOpenTiming.Span("strip", () => ran = true);
            Assert.True(ran);
            Assert.Null(WorkspaceOpenTiming.Finish("shown"));
        }
        finally { WorkspaceOpenTiming.ResetForTests(); }

        string chrome = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsWorkspaceChrome.cs");
        // The 1.6 MB backdrop PNG is decoded once per Core generation, with one block copy.
        SourcePin.AssertUncommented(chrome, "if (TryCachedSprite(out var sprite, out var tex))");
        SourcePin.AssertUncommented(chrome, "Il2CppStructArray<byte> native = png;");
    }

    // ---- round 5: the footer at the window bottom; the kept Mods list -------------------------

    /// <summary>How far the version footer's pane (the detail pane it is drawn at the bottom of) ends above the visible window's bottom.</summary>
    private static float FooterBottomMargin(in ModsWorkspaceFrame frame)
    {
        var art = WorkspaceHostLayout.ArtworkRect(frame);
        var detail = MenuPanelLayout.ForFrame(frame, false);
        return (detail.Y - detail.Height * .5f) - (art.Y - art.Height * .5f);
    }

    [Theory]
    [MemberData(nameof(Probes))]
    public void The_version_footer_sits_at_the_visible_bottom_in_both_hosts_pinned_or_not(
        string name, float canvasW, float canvasH, float frameY)
    {
        float title = FooterBottomMargin(Title(canvasW, canvasH));
        WorkspaceHostLayout.PinInGameFrame = true;
        float pinned = FooterBottomMargin(InGame(canvasW, canvasH, frameY));
        WorkspaceHostLayout.PinInGameFrame = false;
        float unpinned = FooterBottomMargin(InGame(canvasW, canvasH, frameY));
        WorkspaceHostLayout.PinInGameFrame = true;
        Assert.InRange(pinned, title - 4f, title + 4f);
        Assert.InRange(unpinned, title - 4f, title + 4f);
        _ = name;
    }

    [Fact]
    public void The_kept_Mods_list_is_reused_only_while_it_draws_the_same_thing()
    {
        string a = ModsMenu.SidebarSignature();
        Assert.Equal(a, ModsMenu.SidebarSignature());
        Assert.Contains("search=", a);
        Assert.Contains(";width=", a);

        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        string build = SourcePin.MethodBody(menu, "private static void BuildSidebar(");
        SourcePin.AssertUncommented(build, "if (TryReuseSidebar(signature)) return;");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "private static void BuildSidebarCore("), "Ui.SetSoftHide(panel, true);");
        string close = SourcePin.MethodBody(menu, "public static void Close(");
        SourcePin.AssertUncommented(close, "ParkSidebar(Sidebar.Take());");
        // World leave and Core unload drop it, open or not.
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "internal static bool CloseForLifecycle("), "DisposeParkedList();");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "internal static void Uninstall("), "DisposeParkedList();");
        string reuse = SourcePin.MethodBody(menu, "private static bool TryReuseSidebar(");
        SourcePin.AssertUncommented(reuse, "string.Equals(signature, _parkedSignature, StringComparison.Ordinal)");
        SourcePin.AssertUncommented(reuse, "RefilterCatalogRows();");
    }

    // ---- round 6: the cheap list key, its counters, the open phases ------------------------------

    [Fact]
    public void The_list_key_is_counters_and_view_state_and_moves_when_any_of_them_does()
    {
        string Key(int inv = 1, int list = 2, int problems = 3, string ledger = "L", string search = "",
            int filter = 0, bool server = false, bool required = false, float width = 320f) =>
            ModsListKey.Compose(inv, list, problems, ledger, search, filter, server, required, width);

        string baseline = Key();
        Assert.Equal(baseline, Key());
        Assert.NotEqual(baseline, Key(inv: 9));
        Assert.NotEqual(baseline, Key(list: 9));
        Assert.NotEqual(baseline, Key(problems: 9));
        Assert.NotEqual(baseline, Key(ledger: "M"));
        Assert.NotEqual(baseline, Key(search: "blood"));
        Assert.NotEqual(baseline, Key(filter: 1));
        Assert.NotEqual(baseline, Key(server: true));
        Assert.NotEqual(baseline, Key(required: true));
        Assert.NotEqual(baseline, Key(width: 300f));
        Assert.Equal(baseline, Key(width: 320.3f)); // sub-unit layout noise is the same list
        Assert.True(baseline.Length < 200, "the key is small: no list is enumerated to make it");
    }

    [Fact]
    public void The_Mods_list_key_moves_on_real_changes_and_not_on_a_repeated_problem()
    {
        string before = ModsMenu.SidebarSignature();
        Assert.Equal(before, ModsMenu.SidebarSignature());

        // A load, unload, switch or install bumps the menu's own counter.
        ModsMenu.BumpListGeneration();
        string afterBump = ModsMenu.SidebarSignature();
        Assert.NotEqual(before, afterBump);

        // An owed inventory retake moves the inventory's counter.
        int gen = ModMenuInventory.Generation;
        ModMenuInventory.Invalidate("test");
        Assert.Equal(gen + 1, ModMenuInventory.Generation);
        string afterInvalidate = ModsMenu.SidebarSignature();
        Assert.NotEqual(afterBump, afterInvalidate);

        // A problem reported again, word for word (the start results re-read on each open), changes nothing.
        var problem = new ModProblem(ModProblemSeverity.Warning, "test-kind", "test.mod", "It broke.", "Fix it.");
        ModProblems.Report(problem);
        int version = ModProblems.Version;
        string withProblem = ModsMenu.SidebarSignature();
        ModProblems.Report(new ModProblem(ModProblemSeverity.Warning, "test-kind", "test.mod", "It broke.", "Fix it."));
        Assert.Equal(version, ModProblems.Version);
        Assert.Equal(withProblem, ModsMenu.SidebarSignature());
        // A changed problem does.
        ModProblems.Report(new ModProblem(ModProblemSeverity.Warning, "test-kind", "test.mod", "It broke again.", "Fix it."));
        Assert.NotEqual(version, ModProblems.Version);
        Assert.NotEqual(withProblem, ModsMenu.SidebarSignature());
        ModProblems.Clear("test.mod");
    }

    [Fact]
    public void The_open_timing_line_places_the_unaccounted_time_in_phases()
    {
        WorkspaceOpenTiming.ResetForTests();
        long now = 0;
        int frame = 10;
        WorkspaceOpenTiming.Frequency = 1000;
        WorkspaceOpenTiming.Ticks = () => now;
        WorkspaceOpenTiming.Frame = () => frame;
        try
        {
            WorkspaceOpenTiming.Begin();
            WorkspaceOpenTiming.Span("sidebar", () => now += 3);
            now += 2;
            WorkspaceOpenTiming.Phase("begin");
            now += 200; frame += 1;
            WorkspaceOpenTiming.Phase("to-first-tick");
            WorkspaceOpenTiming.Phase("to-first-tick"); // once per open
            now += 10;
            string? line = WorkspaceOpenTiming.Finish("shown path=native-container list=reused-parked");
            Assert.Equal(
                "Mods workspace: open timing total=215ms frames=1 spans=[sidebar=3] unaccounted=212ms"
                + " phases=[begin=5/0f to-first-tick=200/1f to-show=10/0f] end=shown path=native-container list=reused-parked",
                line);
        }
        finally { WorkspaceOpenTiming.ResetForTests(); }

        string chrome = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsWorkspaceChrome.cs");
        SourcePin.AssertUncommented(chrome, "WorkspaceOpenTiming.Phase(\"to-first-tick\");");
        // The backdrop is kept between opens; world leave and unload release it.
        SourcePin.AssertUncommented(SourcePin.MethodBody(chrome, "private static void Tick("), "Park();");
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "public static void Close("), "ParkWorkspaceChrome();");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "internal static bool CloseForLifecycle("), "ReleaseWorkspaceChrome();");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "internal static void Uninstall("), "ReleaseWorkspaceChrome();");
    }

    // ---- round 7: why every reopen rebuilt, and the per-build caches ---------------------------------

    [Fact]
    public void The_key_diff_names_the_part_that_moved()
    {
        string a = ModsListKey.Compose(12, 3, 4, "L", "", 0, false, false, 320f);
        string b = ModsListKey.Compose(13, 3, 4, "L", "", 0, false, false, 300f);
        Assert.Equal("same", ModsListKey.Diff(a, a));
        Assert.Equal("inv 12→13, width 320→300", ModsListKey.Diff(a, b));
        Assert.Contains("inv none→12", ModsListKey.Diff("", a));
    }

    [Fact]
    public void Re_adopting_the_same_server_context_does_not_move_the_list_generation()
    {
        Progenitor.Networking.Handshake.ResetTables(); // no stale-packet epoch left by another test
        ModMenuInventory.ResetForTests();
        try
        {
            var rows = new[] { new Progenitor.Networking.Handshake.ModRecord("a.mod", "1.0.0") };
            ModMenuInventory.RestoreServerContext("test", rows, null, epoch: 7);
            int gen = ModMenuInventory.Generation;
            // Every snapshot re-adopts the handshake: the same rows, catalog and epoch change nothing.
            ModMenuInventory.RestoreServerContext("test", new[] { new Progenitor.Networking.Handshake.ModRecord("a.mod", "1.0.0") }, null, epoch: 7);
            ModMenuInventory.RestoreServerContext("test", rows, null, epoch: 7);
            Assert.Equal(gen, ModMenuInventory.Generation);
            // A changed version, a new epoch, or clearing the context does.
            ModMenuInventory.RestoreServerContext("test", new[] { new Progenitor.Networking.Handshake.ModRecord("a.mod", "1.0.1") }, null, epoch: 7);
            Assert.Equal(gen + 1, ModMenuInventory.Generation);
            ModMenuInventory.RestoreServerContext("test", rows, null, epoch: 8);
            Assert.True(ModMenuInventory.Generation > gen + 1);
            int before = ModMenuInventory.Generation;
            ModMenuInventory.ClearServerContext("test");
            Assert.Equal(before + 1, ModMenuInventory.Generation);
            ModMenuInventory.ClearServerContext("test"); // already clear: no change
            Assert.Equal(before + 1, ModMenuInventory.Generation);
        }
        finally { ModMenuInventory.ResetForTests(); }

        Assert.True(ModMenuInventory.SameRows(null, Array.Empty<Progenitor.Networking.Handshake.ModRecord>()));
        Assert.False(ModMenuInventory.SameRows(
            new[] { new Progenitor.Networking.Handshake.ModRecord("a", "1") },
            new[] { new Progenitor.Networking.Handshake.ModRecord("b", "1") }));
    }

    [Fact]
    public void A_list_build_takes_one_snapshot_one_ledger_scan_and_one_index_pass()
    {
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        string build = SourcePin.MethodBody(menu, "private static void BuildSidebar(");
        SourcePin.AssertUncommented(build, "EnterBuildScope();");
        SourcePin.AssertUncommented(build, "ModsListKey.Diff(previous, signature)");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "private static ModsSidebarModel CurrentSidebarModel("), "InventorySnapshot().Rows");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "private static List<KeyValuePair<string, string>> CatalogEntries("), "InventorySnapshot().Rows");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "private static ModsInstallLink? ResolveInstallLink("), "InstallLinkMap(catalog)");
        string core = SourcePin.MethodBody(menu, "private static void BuildSidebarCore(");
        foreach (var span in new[] { "sidebar-model", "sidebar-entries", "sidebar-rows", "sidebar-groups", "sidebar-bottom" })
            SourcePin.AssertUncommented(core, "\"" + span + "\"");

        var hosts = ModsMenu.DescribeHostsStatus();
        foreach (var key in new[] { "sidebarSource", "sidebarParked", "sidebarKey", "parkedKey", "lastKeyChange", "openTiming" })
            Assert.True(hosts.ContainsKey(key), key);
        string dev = SourcePin.ReadFile("src", "Progenitor.Dev", "Ops", "ModsUiOps.cs");
        SourcePin.AssertUncommented(dev, "return ModsUiResult.WithHosts(ModsUiResult.WithFocus(");
    }

    // ---- round 9: a genuine list build reads each shared fact once -------------------------------

    private sealed class CountingSource : IModProblemSource
    {
        internal int Reads;
        public string Name => "counting";

        public IEnumerable<ModProblem> Current()
        {
            Reads++;
            return new[] { new ModProblem(ModProblemSeverity.Warning, "count-kind", "count.mod", "Counted.", "Nothing.") };
        }
    }

    [Fact]
    public void Inside_a_build_scope_the_live_problem_sources_are_read_once()
    {
        var source = new CountingSource();
        using (ModProblems.AddSource(source))
        {
            ModProblems.For("count.mod");
            ModProblems.For("other.mod");
            Assert.Equal(2, source.Reads); // outside a scope: every ask reads every source (as before)

            ModProblems.EnterScope();
            try
            {
                for (int row = 0; row < 9; row++) ModProblems.For(row % 2 == 0 ? "count.mod" : "other.mod");
                Assert.Equal(3, source.Reads); // one read for nine rows
                Assert.Single(ModProblems.For("count.mod"));
            }
            finally { ModProblems.LeaveScope(); }

            ModProblems.For("count.mod");
            Assert.Equal(4, source.Reads); // the scope is gone: live again
        }
    }

    [Fact]
    public void A_list_build_scopes_problems_switch_facts_and_handshake_rows_and_the_kept_list_follows_changes()
    {
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        string enter = SourcePin.MethodBody(menu, "private static void EnterBuildScope(");
        SourcePin.AssertUncommented(enter, "ModProblems.EnterScope();");
        SourcePin.AssertUncommented(enter, "ModSwitch.EnterScope();");
        string list = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.ModList.cs");
        string facts = SourcePin.MethodBody(list, "internal static ModFrameworkFacts FrameworkFactsFor(");
        SourcePin.AssertUncommented(facts, "foreach (var record in ProvidedRecords())");
        foreach (var span in new[] { "facts-version", "facts-sides", "facts-owner", "facts-switch", "facts-problems" })
            SourcePin.AssertUncommented(facts, "\"" + span + "\"");
        string sw = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModSwitch.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(sw, "internal static ModSwitchFacts FactsFor("), "ReadBackend(out supported, out var suspendable, out var pausedNow);");
        SourcePin.AssertUncommented(SourcePin.MethodBody(sw, "internal static ModSwitchFacts FactsFor("), "foreach (var r in DisabledOnDiskNow(root))");
        // A kept list is rebuilt on a later frame when the inventory moved (the join), not at the next open.
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "internal static void InstallListKeeper("), "ModMenuInventory.GenerationChanged += OnInventoryGenerationChanged;");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "internal static void RefreshParkedList("), "bool build = MayRefreshParked(IsOpen, false, out string why);");

        int raised = 0;
        void Count() => raised++;
        ModMenuInventory.GenerationChanged += Count;
        try
        {
            ModMenuInventory.Invalidate("test");
            Assert.Equal(1, raised);
        }
        finally { ModMenuInventory.GenerationChanged -= Count; }
    }

    // ---- round 10: the join refresh, the readback, the uninstall-pending check -----------------------

    [Fact]
    public void The_list_keeper_is_installed_at_Core_install_and_refreshes_whenever_the_workspace_is_closed()
    {
        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(entry, "internal static void Install("), "ModsMenu.InstallListKeeper();");

        Assert.True(ModsMenu.MayRefreshParked(open: false, suspended: false, out string why));
        Assert.Equal("", why);
        Assert.False(ModsMenu.MayRefreshParked(open: true, suspended: false, out why));
        Assert.Equal("workspace open", why);
        Assert.False(ModsMenu.MayRefreshParked(open: false, suspended: true, out why));

        // The refresh builds a list when none is kept yet (a join before any open) and logs both ends.
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        string refresh = SourcePin.MethodBody(menu, "internal static void RefreshParkedList(");
        SourcePin.AssertUncommented(refresh, "\"prebuilt (none kept)\"");
        SourcePin.AssertUncommented(refresh, "list refresh skipped (");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "private static void OnInventoryGenerationChanged("), "list refresh scheduled in ");
    }

    [Fact]
    public void A_readback_during_an_open_is_one_span_and_hides_the_spans_inside_it()
    {
        WorkspaceOpenTiming.ResetForTests();
        long now = 0;
        WorkspaceOpenTiming.Frequency = 1000;
        WorkspaceOpenTiming.Ticks = () => now;
        try
        {
            WorkspaceOpenTiming.Begin();
            WorkspaceOpenTiming.Span("sidebar", () => now += 20);
            int n = WorkspaceOpenTiming.Readback("readback-status", () =>
            {
                WorkspaceOpenTiming.Span("entry-facts", () => now += 300); // the status deciding every row
                return 1;
            });
            Assert.Equal(1, n);
            string? line = WorkspaceOpenTiming.Finish("shown");
            Assert.Contains("spans=[sidebar=20 readback-status=300]", line);
            Assert.DoesNotContain("entry-facts", line);
        }
        finally { WorkspaceOpenTiming.ResetForTests(); }

        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "public static object DescribeWorkspace("), "WorkspaceOpenTiming.Readback(\"readback-status\", DescribeWorkspaceCore)");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "private static object[] SidebarEntries("), "EnterBuildScope();");
    }

    [Fact]
    public void A_row_no_ledger_names_is_never_asked_whether_its_uninstall_is_pending()
    {
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "private static ModPageState SidebarStateOf("),
            "bool pending = row.Ledger.Count > 0 && IsUninstallPending(row.Id);");
        string list = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.ModList.cs");
        SourcePin.AssertUncommented(SourcePin.MethodBody(list, "private static ModsListEntry ListEntryOf("), "\"entry-state\"");
    }

    // ---- round 11: the index served again with the same listings does not move the list -------------

    private static CatalogEntry Listing(string key, string version) =>
        new(
            new ManifestMod("", version, NetworkCompatibilityLevel.NoNeedForSync,
                NetworkVersionRule.None, false, "n", key),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "2026-09-11T00:00:00Z",
            displayName: key,
            description: "",
            packageName: key);

    [Fact]
    public void The_index_content_stamp_moves_only_when_what_the_list_reads_changes()
    {
        var a = new List<CatalogEntry> { Listing("Owner-A", "1.0.0"), Listing("Owner-B", "2.0.0") };
        var same = new List<CatalogEntry> { Listing("Owner-A", "1.0.0"), Listing("Owner-B", "2.0.0") };
        var newer = new List<CatalogEntry> { Listing("Owner-A", "1.0.1"), Listing("Owner-B", "2.0.0") };
        var more = new List<CatalogEntry> { Listing("Owner-A", "1.0.0"), Listing("Owner-B", "2.0.0"), Listing("Owner-C", "1.0.0") };
        string stamp = ModsMenu.IndexContentStamp(a);
        Assert.Equal(stamp, ModsMenu.IndexContentStamp(same));   // the same index served again
        Assert.NotEqual(stamp, ModsMenu.IndexContentStamp(newer)); // a new version (the Update tag)
        Assert.NotEqual(stamp, ModsMenu.IndexContentStamp(more));  // a new listing
        Assert.Equal("none", ModsMenu.IndexContentStamp((IInstallCatalog?)null));

        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        SourcePin.AssertUncommented(menu, "NoteIndexContent(\"index-cache-served\");");
        SourcePin.AssertUncommented(menu, "NoteIndexContent(\"index-fetched\");");
        SourcePin.AssertNoStatementContaining(SourcePin.MethodBody(menu, "private static void EnsureIndexForOpen("), "BumpListGeneration(");
        SourcePin.AssertNoStatementContaining(SourcePin.MethodBody(menu, "private static void CompleteInstallIndexLoad("), "BumpListGeneration(");
    }

    [Fact]
    public void The_timing_line_carries_per_pane_show_details_outside_the_span_sums()
    {
        WorkspaceOpenTiming.ResetForTests();
        long now = 0;
        WorkspaceOpenTiming.Frequency = 1000;
        WorkspaceOpenTiming.Ticks = () => now;
        try
        {
            WorkspaceOpenTiming.Begin();
            WorkspaceOpenTiming.Span("show-layout", () => { long s0 = now; now += 30; WorkspaceOpenTiming.AddDetail("show-apply-layout", s0); });
            WorkspaceOpenTiming.NoteLast("show-list");
            string? line = WorkspaceOpenTiming.Finish("shown");
            Assert.Contains("spans=[show-layout=30] unaccounted=0ms detail=[show-apply-layout=30 show-list=30]", line);
        }
        finally { WorkspaceOpenTiming.ResetForTests(); }
    }

    // ---- round 12: the index served before the prebuild; the strip kept too ------------------------

    [Fact]
    public void The_strip_is_reused_only_when_native_and_drawing_the_same_tabs()
    {
        var rows = new[] { ("Mods", MenuRowTone.Selected), ("Install Mods", MenuRowTone.Catalog) };
        var other = new[] { ("Mods", MenuRowTone.Catalog), ("Install Mods", MenuRowTone.Selected) };
        string a = ModsMenu.StripKey(ModsTab.Mods, rows);
        Assert.Equal(a, ModsMenu.StripKey(ModsTab.Mods, rows));
        Assert.NotEqual(a, ModsMenu.StripKey(ModsTab.InstallMods, other));
        Assert.True(ModsMenu.MayReuseStrip(a, keptNative: true, a));
        Assert.False(ModsMenu.MayReuseStrip(a, keptNative: false, a)); // a flat strip is rebuilt
        Assert.False(ModsMenu.MayReuseStrip(a, keptNative: true, ModsMenu.StripKey(ModsTab.InstallMods, other)));
        Assert.False(ModsMenu.MayReuseStrip("", keptNative: true, ""));

        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        string park = SourcePin.MethodBody(menu, "private static void BuildParkedList(");
        int serve = park.IndexOf("ServeCachedIndexNow", StringComparison.Ordinal);
        int build = park.IndexOf("BuildSidebar();", StringComparison.Ordinal);
        Assert.True(serve >= 0 && build > serve, "the cached index is served before the list is prebuilt");
        SourcePin.AssertUncommented(park, "Progenitor.Dev.MainThread.Post(PrebuildStrip, Owner);");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "public static void Close("), "ParkStrip(Strip.Take());");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "internal static void DisposeParkedList("), "DisposeParkedStrip();");
        SourcePin.AssertUncommented(SourcePin.MethodBody(menu, "private static void BuildStrip("), "Ui.SetSoftHide(panel, true);");
    }
}
