using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Progenitor.Commands;
using Progenitor.ControlsSample;
using Progenitor.Ui;
using Progenitor.Ui.GameArt;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// WINDOWS1: the standard window presets (layout specs, version stamp, composition over a panel), the named text styles,
/// and the samples gallery. Game-free: the kit's fake runtime records what each window asked for; the Unity drawing is
/// source-checked only.
/// </summary>
[Collection("PluginLogger")]
public sealed class WindowPresetTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly object _owner = new();

    public WindowPresetTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        FrameworkVersions.ResetForTests();
        FrameworkVersions.CoreForTests = "2026.10.16";
    }

    public void Dispose()
    {
        UiDialog.ForgetForTests();
        FrameworkVersions.ResetForTests();
        UiApi.ResetForTests();
    }

    private IUiWindow Make(UiWindowPreset preset, string title = "Iron Chest", UiWindowOptions? options = null) =>
        UiApi.Window(preset, title, _owner, "example.mod", "Example Mod", "1.2.3", options);

    // ---- layout specs ------------------------------------------------------------------------------------------------

    [Fact]
    public void Basic_is_the_ordinary_tiled_frame_that_leaves_the_input_to_the_game()
    {
        var s = UiWindowLayout.Spec(UiWindowPreset.Basic);
        Assert.Equal(640f, s.FrameWidth);
        Assert.False(s.Centered);
        Assert.False(s.HeaderBand);
        Assert.False(s.ToolbarAllowed);
        Assert.False(s.TabsAllowed);
        Assert.Equal(0f, s.InfoColumnWidth);
        Assert.Equal(UiCaptureMode.WhenHovered, s.CaptureMode);
        Assert.False(s.ControllerNavigates);
        Assert.Equal(72f, s.TitleBand);
        Assert.Equal(560f, s.ContentRowWidth);
    }

    [Fact]
    public void Header_adds_the_band_toolbar_and_tabs_and_takes_the_controller()
    {
        var s = UiWindowLayout.Spec(UiWindowPreset.Header);
        Assert.Equal(640f, s.FrameWidth);
        Assert.False(s.Centered);
        Assert.True(s.HeaderBand);
        Assert.True(s.ToolbarAllowed);
        Assert.True(s.TabsAllowed);
        Assert.Equal(UiCaptureMode.WhileShown, s.CaptureMode);
        Assert.True(s.ControllerNavigates);
        Assert.Equal(560f, s.ContentRowWidth);
    }

    [Fact]
    public void Tabbed_is_a_wide_centered_frame_with_an_info_column()
    {
        var s = UiWindowLayout.Spec(UiWindowPreset.Tabbed);
        Assert.Equal(960f, s.FrameWidth);
        Assert.True(s.Centered);
        Assert.True(s.HeaderBand);
        Assert.Equal(280f, s.InfoColumnWidth);
        Assert.Equal(260f, s.InfoRowWidth);
        Assert.Equal(600f, s.ContentRowWidth); // 960 less the 40-unit margins each side less the 280 column
        Assert.Equal(UiCaptureMode.WhileShown, s.CaptureMode);

        // 1920 wide: the wish. A 1000-wide canvas: 90% of it. Never under the ordinary frame.
        Assert.Equal(960f, UiWindowLayout.FrameWidthFor(s, 1920f));
        Assert.Equal(900f, UiWindowLayout.FrameWidthFor(s, 1000f));
        Assert.Equal(640f, UiWindowLayout.FrameWidthFor(s, 500f));
        Assert.Equal(640f, UiWindowLayout.FrameWidthFor(UiWindowLayout.Spec(UiWindowPreset.Basic), 3840f));

        // The info column shrinks with the frame so the content keeps its 560 row, and goes when it would be under 160.
        Assert.Equal(280f, UiWindowLayout.InfoColumnFor(s, 960f));
        Assert.Equal(260f, UiWindowLayout.InfoColumnFor(s, 900f));
        Assert.Equal(0f, UiWindowLayout.InfoColumnFor(s, 700f));
        Assert.Equal(0f, UiWindowLayout.InfoColumnFor(UiWindowLayout.Spec(UiWindowPreset.Header), 960f));
    }

    [Fact]
    public void Ornaments_need_room_beside_the_title_and_never_hold_the_art_scans()
    {
        Assert.True(UiWindowLayout.OrnamentsFit(200f));
        Assert.True(UiWindowLayout.OrnamentsFit(336f));
        Assert.False(UiWindowLayout.OrnamentsFit(337f));
        Assert.False(UiWindowLayout.OrnamentsFit(float.NaN));
        Assert.Equal("Window_TitleSideOrnament", SettingsArtLadder.SpriteName(SettingsArtPart.WindowTitleOrnament));
        Assert.True(SettingsArtLadder.Optional(SettingsArtPart.WindowTitleOrnament));
        Assert.Equal(1, Enumerable.Range(0, SettingsArtLadder.PartCount).Count(i => SettingsArtLadder.Optional((SettingsArtPart)i)));
        Assert.False(SettingsArtLadder.Sliced(SettingsArtPart.WindowTitleOrnament));
        string donors = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "GameArt", "SettingsArtDonors.cs");
        Assert.Contains("SettingsArtLadder.Optional((SettingsArtPart)i) || Art.OwnedUsable(i)", donors);
    }

    [Fact]
    public void Pinned_rows_sit_under_the_title_band_at_the_row_pitch()
    {
        Assert.Equal(new[] { ("toolbar", 80f), ("tabs", 128f) }, UiWindowLayout.PinnedRows(toolbar: true, tabs: true));
        Assert.Equal(new[] { ("tabs", 80f) }, UiWindowLayout.PinnedRows(toolbar: false, tabs: true));
        Assert.Empty(UiWindowLayout.PinnedRows(false, false));
        Assert.Equal(80f, UiWindowLayout.ContentTop(false, false));
        Assert.Equal(128f, UiWindowLayout.ContentTop(true, false));
        Assert.Equal(176f, UiWindowLayout.ContentTop(true, true));
    }

    [Fact]
    public void Undefined_presets_and_oversized_tab_rows_and_toolbars_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => UiWindowLayout.Spec((UiWindowPreset)9));
        Assert.Null(UiWindowLayout.RefuseTabs(new[] { "A", "B", "C", "D", "E", "F" }));
        Assert.NotNull(UiWindowLayout.RefuseTabs(new[] { "A", "B", "C", "D", "E", "F", "G" }));
        Assert.NotNull(UiWindowLayout.RefuseTabs(new[] { "A", " " }));
        Assert.NotNull(UiWindowLayout.RefuseTools(new[] { new UiWindowTool("Sort everything in sight") }));
        Assert.NotNull(UiWindowLayout.RefuseTools(Enumerable.Range(0, 7).Select(i => new UiWindowTool("T" + i)).ToList()));
        Assert.Throws<ArgumentException>(() => Make(UiWindowPreset.Header, options: new UiWindowOptions
        {
            Tabs = new[] { "A", "B", "C", "D", "E", "F", "G" },
        }));
        Assert.Throws<ArgumentException>(() => Make(UiWindowPreset.Basic, title: " "));
    }

    [Fact]
    public void A_toolbar_is_refused_when_its_measured_buttons_do_not_fit_the_row()
    {
        var header = UiWindowLayout.Spec(UiWindowPreset.Header);
        var tabbed = UiWindowLayout.Spec(UiWindowPreset.Tabbed);
        Assert.Equal(560f, UiWindowLayout.ToolbarRowWidth(header));
        Assert.Equal(880f, UiWindowLayout.ToolbarRowWidth(tabbed));
        var two = new[] { new UiWindowTool("Sort"), new UiWindowTool("Locate") };
        var four = new[] { new UiWindowTool("Sort"), new UiWindowTool("Locate"), new UiWindowTool("Refresh"), new UiWindowTool("Empty") };
        // Each compact button is at least 140 wide with an 8 gap: Sort, Locate and Rename take 436 of 560.
        Assert.Equal(3 * PageButtonLayout.MinWidth + 2 * PageButtonLayout.Gap, UiWindowLayout.ToolbarWidth(two, renamable: true));
        Assert.Null(UiWindowLayout.RefuseTools(two, renamable: true, UiWindowLayout.ToolbarRowWidth(header)));
        Assert.NotNull(UiWindowLayout.RefuseTools(four, renamable: true, UiWindowLayout.ToolbarRowWidth(header)));
        Assert.Null(UiWindowLayout.RefuseTools(four, renamable: true, UiWindowLayout.ToolbarRowWidth(tabbed)));
        // Long captions are measured, not counted: three 16-character captions overflow a Header row.
        var wide = new[] { new UiWindowTool("Deposit overflow"), new UiWindowTool("Withdraw overflo"), new UiWindowTool("Sort by category") };
        Assert.True(UiWindowLayout.ToolbarWidth(wide, false) > 560f);
        Assert.NotNull(UiWindowLayout.RefuseTools(wide, false, 560f));
        Assert.Throws<ArgumentException>(() => Make(UiWindowPreset.Header, options: new UiWindowOptions { Tools = four, Renamable = true }));
        Assert.NotNull(Make(UiWindowPreset.Tabbed, "Wide", new UiWindowOptions { Tools = four, Renamable = true }));
    }

    [Fact]
    public void A_long_info_column_is_clipped_above_the_stamp_inside_the_frame()
    {
        // 20 Facts in a Tabbed window at 1080p: the frame is capped at 80% of 1080 = 864, the column starts under the
        // toolbar and the tab row (y 176), and the clip ends at the bottom pad above the 24-unit stamp band.
        float panel = PanelLayout.PanelHeightFor(10_000f, 1080f);
        Assert.Equal(864f, panel);
        float top = UiWindowLayout.ContentTop(toolbar: true, tabs: true);
        var clip = UiWindowLayout.InfoClip(960f, UiWindowLayout.InfoColumnWidth, top, panel, PanelFooterLayout.BandHeight);
        Assert.Equal(top, clip.Top);
        Assert.Equal(260f, clip.Width);
        Assert.Equal(-460f + 130f, clip.CenterX); // from x 20 to 280 of the 960 frame
        Assert.True(clip.Top + clip.Height <= panel - PanelFooterLayout.BandHeight - PanelLayout.PadBottom + 0.01f);
        var facts = Enumerable.Repeat(UnityUiRuntimeMetrics.RowHeight, 20).ToList();
        int shown = UiWindowLayout.InfoRowsShown(facts, clip.Height);
        Assert.InRange(shown, 1, 19);
        Assert.Equal((int)((clip.Height + PanelLayout.RowGap) / (UnityUiRuntimeMetrics.RowHeight + PanelLayout.RowGap)), shown);
        // The runtime parents the column's rows under the clip, and switches the clip (not the rows) off without room.
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.Windows.cs");
        Assert.Contains("TryAdd<RectMask2D>(_infoClip);", runtime);
        Assert.Contains("if (_infoClip.activeSelf != on) _infoClip.SetActive(on);", runtime);
        Assert.DoesNotContain("row.Go.SetActive(false)", runtime);
        Assert.Contains(": _nextPin == MenuRowPin.Side ? SideParent", SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs"));
    }

    [Fact]
    public void A_Basic_window_opened_while_navigating_takes_the_controller()
    {
        var was = UiWindowHandle.NavigatingNow;
        try
        {
            var basic = UiWindowLayout.Spec(UiWindowPreset.Basic);
            Assert.True(basic.ControllerHandoff);
            Assert.False(UiWindowLayout.Spec(UiWindowPreset.Header).ControllerHandoff);
            Assert.Equal(UiCaptureMode.WhileShown, UiWindowLayout.CaptureOnShow(basic, navigating: true));
            Assert.Equal(UiCaptureMode.WhenHovered, UiWindowLayout.CaptureOnShow(basic, navigating: false));

            UiWindowHandle.NavigatingNow = () => true;
            var pad = Make(UiWindowPreset.Basic, "Pad");
            pad.Show();
            Assert.Equal(UiCaptureMode.WhileShown, pad.Panel.CaptureMode);
            pad.Hide();

            UiWindowHandle.NavigatingNow = () => false;
            pad.Show();
            Assert.Equal(UiCaptureMode.WhenHovered, pad.Panel.CaptureMode);

            // The mod's own choice wins.
            UiWindowHandle.NavigatingNow = () => true;
            var fixedMode = Make(UiWindowPreset.Basic, "Fixed", new UiWindowOptions { CaptureMode = UiCaptureMode.Never });
            fixedMode.Show();
            Assert.Equal(UiCaptureMode.Never, fixedMode.Panel.CaptureMode);
        }
        finally { UiWindowHandle.NavigatingNow = was; }
    }

    [Theory]
    [InlineData(1920f, 1)]
    [InlineData(1920f, 2)]
    [InlineData(1920f, 3)]
    [InlineData(2560f, 1)]
    [InlineData(2560f, 2)]
    [InlineData(2560f, 4)]
    public void Tiled_windows_never_land_under_a_centered_Tabbed_window(float canvasWidth, int tiled)
    {
        var shown = new List<string> { "tabbed" };
        for (int i = 0; i < tiled; i++) shown.Add("basic" + i);
        var spots = UiWindowLayout.Spots(shown, p => p == "tabbed" ? 960f : 0f, _ => false);
        var tab = spots.Single(s => s.Panel == "tabbed").Spot;
        Assert.Equal(tiled, tab.Tiled);
        var tabAt = UiWindowLayout.Place(tab, 960f, canvasWidth, 1080f, centered: true);
        float tabLeft = tabAt.X - 480f, tabRight = tabAt.X + 480f;
        Assert.True(tabLeft >= -canvasWidth * 0.5f - 0.01f, "the Tabbed window left the screen");
        foreach (var (panel, spot) in spots.Where(s => s.Panel != "tabbed"))
        {
            Assert.Equal(960f, spot.CenteredWidth);
            Assert.Equal(tiled, spot.Count);
            var at = UiWindowLayout.Place(spot, 640f, canvasWidth, 1080f, centered: false);
            float left = at.X - 320f, right = at.X + 320f;
            Assert.True(left >= tabRight || right <= tabLeft, panel + " at x " + at.X + " overlaps the Tabbed window at " + tabAt.X);
            Assert.True(right <= canvasWidth * 0.5f + 0.01f, panel + " left the screen");
        }

        // Alone, it is dead center; without a centered window the set tiles as it always did.
        var alone = UiWindowLayout.Spots(new[] { "tabbed" }, p => 960f, _ => false).Single().Spot;
        Assert.Equal(PanelOffset.Centre, UiWindowLayout.Place(alone, 960f, canvasWidth, 1080f, centered: true));
        var plain = UiWindowLayout.Spots(new[] { "a", "b" }, _ => 0f, _ => false);
        Assert.Equal(PanelFlow.Offset(new PanelSpot(1, 2), 640f, canvasWidth, 1080f), UiWindowLayout.Place(plain[1].Spot, 640f, canvasWidth, 1080f, false));
    }

    [Fact]
    public void The_re_flow_gives_tiled_panels_spots_clear_of_a_shown_Tabbed_window()
    {
        var tabbed = Make(UiWindowPreset.Tabbed, "Net");
        var basic = Make(UiWindowPreset.Basic, "Gear");
        tabbed.Show();
        basic.Show();
        Assert.Equal(960f, ((UiPanelHandle)tabbed.Panel).CenteredWidth);
        Assert.Equal(0f, ((UiPanelHandle)basic.Panel).CenteredWidth);
        var gear = _runtime.SpotFor("Gear");
        var net = _runtime.SpotFor("Net");
        Assert.Equal(960f, gear.CenteredWidth);
        Assert.Equal(new PanelSpot(0, 1).Rank, gear.Rank);
        Assert.Equal(1, gear.Count);
        Assert.Equal(1, net.Tiled);
    }

    [Fact]
    public void Tabbed_always_has_a_tab_and_Basic_never_has_one()
    {
        Assert.Equal(new[] { "Network" }, UiWindowLayout.TabsFor(UiWindowPreset.Tabbed, "Network", null));
        Assert.Empty(UiWindowLayout.TabsFor(UiWindowPreset.Basic, "x", new UiWindowOptions { Tabs = new[] { "A", "B" } }));
        Assert.Empty(UiWindowLayout.TabsFor(UiWindowPreset.Header, "x", null));
    }

    // ---- version stamp -----------------------------------------------------------------------------------------------

    [Fact]
    public void Stamp_names_the_mod_and_its_version_then_the_framework()
    {
        Assert.Equal("Example Mod 1.2.3 · Progenitor 2026.10.16", UiWindowLayout.StampText("Example Mod", "1.2.3", UiApi.FrameworkFooter));
        Assert.Equal("", UiWindowLayout.StampText("Example Mod", "1.2.3", UiApi.FrameworkFooter, enabled: false));
        Assert.Equal("Progenitor 2026.10.16", UiWindowLayout.StampText("", "", UiApi.FrameworkFooter));
        // A name too long for the line is cut before either version.
        string longName = new string('W', 120);
        string cut = UiWindowLayout.StampText(longName, "1.2.3", UiApi.FrameworkFooter);
        Assert.EndsWith(" 1.2.3 · Progenitor 2026.10.16", cut);
        Assert.Contains("…", cut);
    }

    [Fact]
    public void A_window_carries_its_stamp_by_default_and_can_drop_or_replace_it()
    {
        var w = Make(UiWindowPreset.Basic);
        Assert.Equal("Example Mod 1.2.3 · Progenitor 2026.10.16", w.VersionStamp);
        Assert.Equal("Example Mod 1.2.3 · Progenitor 2026.10.16", _runtime.FooterFor("Iron Chest"));

        var off = Make(UiWindowPreset.Basic, "No stamp", new UiWindowOptions { VersionStamp = false });
        Assert.Equal("", off.VersionStamp);
        var own = Make(UiWindowPreset.Basic, "Own stamp", new UiWindowOptions { VersionText = "Example build 7" });
        Assert.Equal("Example build 7", own.VersionStamp);
        own.VersionStamp = "";
        Assert.Equal("", own.VersionStamp);
    }

    [Fact]
    public void The_public_factory_reads_the_stamp_from_the_owner_plugin_attribute()
    {
        var w = UiApi.Window(UiWindowPreset.Basic, "Attributed", new AttributedOwner(), "stamp.owner");
        Assert.Equal("Stamped Mod 4.5.6 · Progenitor 2026.10.16", w.VersionStamp);
    }

    // ---- composition -------------------------------------------------------------------------------------------------

    [Fact]
    public void Each_preset_asks_for_the_game_window_and_its_own_frame_and_input_mode()
    {
        var basic = Make(UiWindowPreset.Basic, "B");
        var header = Make(UiWindowPreset.Header, "H");
        var tabbed = Make(UiWindowPreset.Tabbed, "T");
        Assert.Equal(new[] { "B", "H", "T" }, _runtime.GameWindowAsks);
        Assert.Equal(new[] { ("B", UiWindowPreset.Basic), ("H", UiWindowPreset.Header), ("T", UiWindowPreset.Tabbed) }, _runtime.WindowAsks);
        Assert.Equal(UiCaptureMode.WhenHovered, basic.Panel.CaptureMode);
        Assert.Equal(UiCaptureMode.WhileShown, header.Panel.CaptureMode);
        Assert.Equal(UiCaptureMode.WhileShown, tabbed.Panel.CaptureMode);
        Assert.Equal(SettingsAlign.Left, basic.Panel.ContentAlignment);
        var modal = Make(UiWindowPreset.Basic, "M", new UiWindowOptions { CaptureMode = UiCaptureMode.WhileShown });
        Assert.Equal(UiCaptureMode.WhileShown, modal.Panel.CaptureMode);
        Assert.Null(basic.Info);
        Assert.Null(basic.Tabs);
        Assert.NotNull(tabbed.Info);
        Assert.Equal(new[] { "T" }, tabbed.Tabs!.Captions);
    }

    [Fact]
    public void Toolbar_then_tabs_are_pinned_on_top_and_the_info_column_on_the_side()
    {
        var w = Make(UiWindowPreset.Tabbed, "Net", new UiWindowOptions
        {
            Renamable = true,
            Tools = new[] { new UiWindowTool("Sort", "Sort it."), new UiWindowTool("Locate") },
            Tabs = new[] { "Overview", "Belts" },
        });
        w.Info!.Heading("Network");
        w.Info.Fact("Chests", "24");
        var pins = _runtime.PinAsks.Where(p => p.Title == "Net").Select(p => p.Pin).ToList();
        Assert.Equal(new[]
        {
            MenuRowPin.Top, MenuRowPin.Content, // toolbar
            MenuRowPin.Top, MenuRowPin.Content, // tabs
            MenuRowPin.Side, MenuRowPin.Content, MenuRowPin.Side, MenuRowPin.Content, // two info rows
        }, pins);
        Assert.Equal(new[] { "Sort", "Locate" }, w.Tools.Select(t => t.Text));
        var d = w.Describe();
        Assert.Equal("Tabbed", d["preset"]);
        Assert.Equal(true, d["rename"]);
        Assert.Equal(new List<string> { "Overview", "Belts" }, d["tabs"]);
        Assert.Equal(true, d["centered"]);
    }

    [Fact]
    public void Rename_sets_the_title_and_tells_the_handlers_once_each()
    {
        var w = (UiWindowHandle)Make(UiWindowPreset.Header, "Iron Chest", new UiWindowOptions { Renamable = true });
        var heard = new List<string>();
        var lease = w.OnRename(heard.Add);
        w.Rename("  Ore chest ");
        Assert.Equal("Ore chest", w.Title);
        Assert.Equal(new[] { "Ore chest" }, heard);
        w.Rename(" ");
        Assert.Single(heard);
        lease.Dispose();
        w.Rename("Third");
        Assert.Single(heard);
    }

    [Fact]
    public void Show_hide_toggle_and_dispose_follow_the_panel()
    {
        var w = Make(UiWindowPreset.Basic);
        Assert.False(w.IsOpen);
        w.Show();
        Assert.True(w.IsOpen);
        w.Toggle();
        Assert.False(w.IsOpen);
        w.Toggle();
        Assert.True(w.IsOpen);
        w.Dispose();
        Assert.True(w.IsDisposed);
        Assert.False(w.IsOpen);
        Assert.True(w.Panel.IsDisposed);
        w.Show(); // no-op after dispose
        Assert.False(w.IsOpen);
    }

    [Fact]
    public void A_server_window_is_disabled_but_valid()
    {
        _runtime.IsClient = false;
        var w = Make(UiWindowPreset.Tabbed, "Server");
        Assert.False(w.Panel.Enabled);
        w.Info!.Note("nothing drawn");
        w.Show();
        w.Dispose();
    }

    [AttributeUsage(AttributeTargets.Class)]
    private sealed class BepInPlugin : Attribute
    {
        public BepInPlugin(string guid, string name, string version)
        {
            GUID = guid;
            Name = name;
            Version = version;
        }

        public string GUID { get; }
        public string Name { get; }
        public string Version { get; }
    }

    [@BepInPlugin("stamp.owner", "Stamped Mod", "4.5.6")]
    private sealed class AttributedOwner { }

    // ---- text styles -------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_named_style_fixes_font_size_color_and_alignment()
    {
        Assert.Equal(Enum.GetValues(typeof(UiTextStyle)).Length, UiTextStyles.All.Count);
        foreach (UiTextStyle style in Enum.GetValues(typeof(UiTextStyle)))
        {
            var s = UiTextStyles.Spec(style);
            Assert.Equal(style, s.Style);
            Assert.Equal(UiTextStyles.BodyFont, s.Font);
            Assert.True(s.Size >= 16f, style + " is too small to read");
            Assert.False(string.IsNullOrWhiteSpace(s.UseWhen));
            Assert.Equal(s.Tone, UiTextStyles.ToneOf(style));
            // One source: the color a label draws (its tone) and the color a cell draws (its cell tone) are the table's.
            if (style is not UiTextStyle.Title and not UiTextStyle.Section)
            {
                Assert.Equal(s.Color, MenuPalette.LabelColor(s.Tone));
                Assert.Equal(s.Color, MenuPalette.TextColor(s.CellTone));
                Assert.Equal(UiTextStyles.BodySize, s.Size);
            }
        }

        Assert.Equal(DialogLayout.TitleFontSize, UiTextStyles.Spec(UiTextStyle.Title).Size);
        Assert.Equal(SettingsAlign.HeadingFontSize, UiTextStyles.Spec(UiTextStyle.Section).Size);
        Assert.True(UiTextStyles.Spec(UiTextStyle.Section).Bold);
        Assert.Equal(new PaletteColor(DialogLayout.TextGrey, DialogLayout.TextGrey, DialogLayout.TextGrey), UiTextStyles.Spec(UiTextStyle.Title).Color);

        Assert.Equal(UiTextAlign.Center, UiTextStyles.Spec(UiTextStyle.Title).Align);
        Assert.Equal(UiTextAlign.Right, UiTextStyles.Spec(UiTextStyle.Number).Align);
        foreach (var left in new[] { UiTextStyle.Body, UiTextStyle.Section, UiTextStyle.Muted, UiTextStyle.Warning, UiTextStyle.Error, UiTextStyle.Success })
            Assert.Equal(UiTextAlign.Left, UiTextStyles.Spec(left).Align);
        Assert.Equal(MenuPalette.Muted, UiTextStyles.Spec(UiTextStyle.Muted).Color);
        Assert.Equal(MenuPalette.Red, UiTextStyles.Spec(UiTextStyle.Error).Color);
        Assert.Equal(MenuPalette.OkText, UiTextStyles.Spec(UiTextStyle.Success).Color);
        Assert.Equal(MenuPalette.Warn, UiTextStyles.Spec(UiTextStyle.Warning).Color);
        Assert.Equal(LabelTone.Danger, UiTextStyles.Spec(UiTextStyle.Error).Tone);
        Assert.Equal(UiTextStyle.Warning, UiTextStyles.StyleOf(LabelTone.Pending));
    }

    /// <summary>The kit's own rows draw the table's numbers: label size, heading size and weight, dialog title size and color.</summary>
    [Fact]
    public void The_kit_rows_draw_from_the_style_table()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.cs");
        string addLabel = SourcePin.MethodBody(runtime, "public IRuntimeWidget? AddLabel(");
        Assert.Contains("MakeLabel(host.RowParent, text, Vector2.zero, UiTextStyles.BodySize, left: host.IsLeftAligned)", addLabel);
        string toned = SourcePin.MethodBody(runtime, "public IRuntimeWidget? AddTonedLabel(");
        Assert.Contains("AddLabel(panel, text)", toned);
        Assert.Contains("tmp.color = ToColor(MenuPalette.LabelColor(tone))", SourcePin.MethodBody(runtime, "public void StyleLabel("));
        string heading = SourcePin.MethodBody(runtime, "public IRuntimeWidget? AddHeading(");
        Assert.Contains("SettingsAlign.HeadingFontSize", heading);
        Assert.Contains("FontStyles.Bold", heading);
        string dialogText = SourcePin.MethodBody(runtime, "private IRuntimeWidget? AddDialogTextRow(");
        Assert.Contains("title ? DialogLayout.TitleFontSize", dialogText);
        Assert.Contains("DialogLayout.TextGrey", dialogText);

        // Cells take the table's size and tone, not the cell kind's own.
        foreach (UiTextStyle style in Enum.GetValues(typeof(UiTextStyle)))
        {
            var cells = UiTextStyles.Cells("Name", "1", style);
            Assert.Equal(UiTextStyles.Spec(UiTextStyle.Body).Size, cells[0].FontSize);
            Assert.Equal(UiTextStyles.Spec(style).Size, cells[1].FontSize);
            Assert.Equal(UiTextStyles.Spec(style).CellTone, cells[1].Tone);
        }
    }

    [Fact]
    public void Numbers_are_drawn_right_aligned_in_tabular_figures_and_names_left()
    {
        var single = UiTextStyles.Cells("", "1,280", UiTextStyle.Number);
        Assert.Equal(CellKind.Spacer, single[0].Kind);
        Assert.Equal(CellKind.Mono, single[1].Kind);
        Assert.Equal(CellAlign.Right, single[1].Align);

        var pair = UiTextStyles.Cells("Iron Ore", "1,280", UiTextStyle.Number);
        Assert.Equal(CellKind.Text, pair[0].Kind);
        Assert.Equal(CellAlign.Left, pair[0].Align);
        Assert.Equal(CellAlign.Right, pair[1].Align);

        var warn = UiTextStyles.Cells("Fuel", "4 min", UiTextStyle.Warning);
        Assert.Equal(CellTone.Warn, warn[1].Tone);
        Assert.Equal(CellAlign.Right, warn[1].Align); // a value is always at the row's right
    }

    [Fact]
    public void Text_draws_each_style_through_the_kit_and_a_number_can_be_rewritten()
    {
        var panel = UiApi.Panel("Styles", _owner, "example.styles");
        foreach (UiTextStyle style in Enum.GetValues(typeof(UiTextStyle)))
            Assert.NotNull(panel.Text("x", style));
        var n = panel.Text("Iron Ore", "1", UiTextStyle.Number);
        n.Text = "2";
        Assert.Equal("2", n.Text);
        // Number, and the name-and-value form, are cell rows (the right-aligned Mono cell); the rest are kit labels.
        Assert.Equal(2, _runtime.CellRows.Count);
        Assert.Contains("value", _runtime.CellRows.Last().Applied.LastOrDefault() ?? "value");
    }

    // ---- gallery -----------------------------------------------------------------------------------------------------

    [Fact]
    public void The_catalog_covers_every_category_with_complete_entries()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in GalleryCatalog.All)
        {
            Assert.True(ids.Add(e.Id), "duplicate gallery id " + e.Id);
            Assert.Contains(e.Category, GalleryCatalog.Categories);
            foreach (var field in new[] { e.Control, e.Api, e.UseWhen, e.Gallery, e.Sample, e.Style })
                Assert.False(string.IsNullOrWhiteSpace(field), e.Id + " has an empty field");
            Assert.True(e.UseWhen.EndsWith(".", StringComparison.Ordinal), e.Id + ": the use-when line is one sentence");
        }

        foreach (var c in GalleryCatalog.Categories)
            Assert.NotEmpty(GalleryCatalog.In(c));
        // The public IUiPanel surface, the widgets, ctx.Hud and ctx.Dialogs each have an entry.
        foreach (string api in new[]
        {
            "panel.Button", "panel.Toggle", "panel.Slider(", "panel.SliderField", "panel.Number", "panel.Dropdown", "panel.Radio",
            "panel.MultiSelect", "panel.Input", "panel.TextArea", "panel.ColorField", "panel.DateTimeField", "panel.KeyField",
            "panel.Info", "panel.Divider", "panel.ConfirmButton", "panel.Heading", "panel.Note", "panel.Fact", "BeginInline",
            "panel.Footer", "CaptureMode", "panel.OrderedList", "panel.Picker", "panel.Tabs", "panel.CodeArea", "panel.DataTable",
            "panel.MasterDetail", "panel.Chips", "handle.Tooltip", "handle.Term", "TermMarkersToggle", "ctx.Hud.Notify",
            "ctx.Hud.Locate", "ctx.Hud.PickInWorld", "ctx.Hud.Tip", "ctx.Dialogs.Confirm", "ctx.Dialogs.Notice",
            "ctx.Dialogs.Choose", "ctx.Dialogs.Prompt", "Ui.AttachToVanilla", "ctx.Windows.Create", "panel.Text(", "GameStyle",
            "ctx.Settings.Page", "ctx.Menu.Register", "ctx.Keybinds", "ICommandContext.Reply", "ctx.Chat.Broadcast",
        })
            Assert.True(GalleryCatalog.All.Any(e => e.Api.Contains(api, StringComparison.Ordinal)), "no gallery entry for " + api);
    }

    [Fact]
    public void The_controls_catalog_doc_lists_every_gallery_entry_and_every_style()
    {
        string doc = SourcePin.ReadFile("docs", "ui-controls.md");
        foreach (var e in GalleryCatalog.All)
        {
            Assert.Contains("| " + e.Control + " |", doc);
            Assert.Contains(e.Api.Replace("|", "\\|"), doc);
        }

        foreach (string style in Enum.GetNames(typeof(UiTextStyle)))
            Assert.Contains("| `" + style + "` |", doc);
        Assert.Contains("ui-controls.md", SourcePin.ReadFile("docs", "ui.md"));
    }

    [Fact]
    public void Every_catalog_entry_states_its_text_style()
    {
        var named = Enum.GetNames(typeof(UiTextStyle)).Concat(new[] { "centered", "No text", "chat style", "interact prompt", "own button", "As " }).ToList();
        foreach (var e in GalleryCatalog.All)
            Assert.True(named.Any(n => e.Style.Contains(n, StringComparison.Ordinal)), e.Id + " style line names no text style: " + e.Style);
    }

    [Fact]
    public void The_gallery_builds_every_entry_on_its_category_tab()
    {
        var log = new List<string>();
        var host = new FakeGalleryHost();
        var demo = new GalleryDemo(log.Add, host);
        var panel = UiApi.Panel("Gallery", _owner, "progenitor.controlssample");
        demo.Build(panel, "progenitor.controlssample", ControlsDemo.OpenBindId);
        Assert.Equal(GalleryCatalog.All.Select(e => e.Id), demo.Built);
        Assert.Equal(GalleryCatalog.Categories, demo.Tabs!.Captions);
        var d = demo.Tabs.Describe();
        var tabs = (List<Dictionary<string, object?>>)d["tabs"]!;
        Assert.All(tabs, t => Assert.True((int)t["content"]! > 0));
        demo.Report("x");
        Assert.Equal(ControlsDemo.LogPrefix + GalleryDemo.LogTag + "x", log.Last());
    }

    [Fact]
    public void The_gallery_windows_open_each_preset_with_contents_and_close_on_dispose()
    {
        var log = new List<string>();
        var made = new List<IUiWindow>();
        var windows = new GalleryWindows((p, t, o) =>
        {
            var w = UiApi.Window(p, t, _owner, "progenitor.controlssample", "Progenitor Controls Sample", "1.0.0", o);
            made.Add(w);
            return w;
        }, log.Add);
        foreach (UiWindowPreset p in Enum.GetValues(typeof(UiWindowPreset)))
        {
            Assert.Equal("opened", windows.Toggle(p));
            Assert.True(windows.Window(p)!.IsOpen);
            Assert.StartsWith("Progenitor Controls Sample 1.0.0 · Progenitor", windows.Window(p)!.VersionStamp);
            Assert.Equal("closed", windows.Toggle(p));
        }

        Assert.Equal(new[] { GalleryWindows.BasicTitle, GalleryWindows.HeaderTitle, GalleryWindows.TabbedTitle }, made.Select(w => w.Title));
        Assert.Equal(new[] { "Contents", "Rules", "Log" }, windows.Window(UiWindowPreset.Header)!.Tabs!.Captions);
        Assert.NotNull(windows.Window(UiWindowPreset.Tabbed)!.Info);
        windows.Dispose();
        Assert.All(made, w => Assert.True(w.IsDisposed));
    }

    /// <summary>
    /// Fonts, colors and alignment come from the named styles: the gallery's own words never set a color, a size or an
    /// alignment, and never draw a bare label without a style.
    /// </summary>
    [Fact]
    public void Gallery_sources_use_named_styles_and_no_raw_colors_or_sizes()
    {
        foreach (string file in new[] { "GalleryDemo.cs", "GalleryWindows.cs", "GalleryCatalog.cs", "Plugin.cs" })
        {
            string src = SourcePin.ReadFile("samples", "Progenitor.ControlsSample", file);
            var (code, strings) = SplitSource(src);
            // Code: no raw tone, color, size or alignment, and no label without a style (a literal or a variable alike).
            foreach (string raw in new[] { "LabelTone.", "new Color", "PaletteColor", "fontSize", "FontSize", "horizontalAlignment", "TextAlignmentOptions", "SetTone(" })
                Assert.False(code.Contains(raw, StringComparison.Ordinal), file + " sets a raw style in code: " + raw);
            Assert.DoesNotMatch(new Regex(@"\.Label\(\s*[^,()]*\)"), code);
            // The only alignment a gallery file sets is the kit's left default for a page.
            foreach (Match m in Regex.Matches(src, @"Alignment\s*=\s*[^;]+;"))
                Assert.Equal("Alignment = \"left\";", m.Value);
            // Strings: no rich-text styling, and the one hex color is the color field's own value, not a text color.
            foreach (string raw in new[] { "<color", "<size", "<b>", "<i>", "<font", "<align" })
                Assert.False(strings.Contains(raw, StringComparison.OrdinalIgnoreCase), file + " styles text with " + raw);
            var hex = Regex.Matches(strings, "#[0-9A-Fa-f]{6}").Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();
            Assert.True(hex.Count == 0 || hex.SequenceEqual(new[] { "#B03030" }), file + " has a raw color: " + string.Join(",", hex));
        }
    }

    /// <summary>A C# source's code (comments and string literals removed) and its string literals' contents, joined.</summary>
    private static (string Code, string Strings) SplitSource(string src)
    {
        var code = new System.Text.StringBuilder(src.Length);
        var strings = new System.Text.StringBuilder();
        int i = 0;
        while (i < src.Length)
        {
            if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
                continue;
            }

            if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? src.Length : end + 2;
                continue;
            }

            if (src[i] == '"')
            {
                i++;
                while (i < src.Length && src[i] != '"')
                {
                    if (src[i] == '\\' && i + 1 < src.Length) { strings.Append(src[i + 1]); i += 2; continue; }
                    strings.Append(src[i++]);
                }

                strings.Append('\n');
                code.Append("\"\"");
                i++;
                continue;
            }

            if (src[i] == '\'' && i + 2 < src.Length)
            {
                int end = src.IndexOf('\'', src[i + 1] == '\\' ? i + 3 : i + 2);
                if (end > i) { code.Append("' '"); i = end + 1; continue; }
            }

            code.Append(src[i++]);
        }

        return (code.ToString(), strings.ToString());
    }

    private sealed class FakeGalleryHost : IGalleryHost
    {
        public string ToggleWindow(UiWindowPreset preset) => "opened";
        public void OpenFloating() { }
        public void OpenPage(string page) { }
        public void OpenDialog(string kind) { }
        public void Notify(string text, string detail, UiNoticeKind kind, bool prefix) { }
        public void Pick(Action<string> done) => done("cancelled");
        public string LocatePicked() => "nothing picked yet";
        public string Tip(IUiPanel anchor) => "Showing";
    }
}

/// <summary>WINDOWS1: the automatic <c>&lt;Mod Name&gt;: </c> prefix and command reply formatting.</summary>
[Collection("CommandRegistry")]
public sealed class ModMessagePrefixTests
{
    [Theory]
    [InlineData("Sortmancer", "Sorted 3 chests.", true, "Sortmancer: Sorted 3 chests.")]
    [InlineData("Sortmancer", "Sortmancer: sorted.", true, "Sortmancer: sorted.")]
    [InlineData("Sortmancer", "sortmancer sorted 3 chests", true, "Sortmancer: sortmancer sorted 3 chests")]
    [InlineData("Teleport", "Teleport failed: no waypoint.", true, "Teleport: Teleport failed: no waypoint.")]
    [InlineData("Teleport", "teleport: done", true, "teleport: done")]
    [InlineData("Sortmancer", "(Sortmancer) sorted", true, "(Sortmancer) sorted")]
    [InlineData("Sortmancer", "[Sortmancer sorted", true, "Sortmancer: [Sortmancer sorted")]
    [InlineData("<color=#f00>Sortmancer</color>", "sorted", true, "Sortmancer: sorted")]
    [InlineData("<b>Sortmancer</b>", "Sortmancer: sorted", true, "Sortmancer: sorted")]
    [InlineData("Sortmancer", "[Sortmancer] sorted", true, "[Sortmancer] sorted")]
    [InlineData("Sortmancer", "<color=#fff>Sortmancer</color>: sorted", true, "<color=#fff>Sortmancer</color>: sorted")]
    [InlineData("Sortmancer", "<color=#fff>Sortmancer</color> sorted", true, "Sortmancer: <color=#fff>Sortmancer</color> sorted")]
    [InlineData("Sortmancer", "Sortmancer2 is a different mod", true, "Sortmancer: Sortmancer2 is a different mod")]
    [InlineData("Sortmancer", "Sorted 3 chests.", false, "Sorted 3 chests.")]
    [InlineData("", "Sorted.", true, "Sorted.")]
    [InlineData("  ", "Sorted.", true, "Sorted.")]
    [InlineData("Sortmancer", "", true, "")]
    [InlineData("Sortmancer", "   ", true, "   ")]
    [InlineData("Sortmancer", "Sortmancer", true, "Sortmancer: Sortmancer")]
    [InlineData(" Sortmancer ", "ok", true, "Sortmancer: ok")]
    public void Prefix_rules(string name, string text, bool prefix, string expected) =>
        Assert.Equal(expected, ModMessagePrefix.Apply(name, text, prefix));

    [Fact]
    public void Prefix_is_applied_once_even_when_applied_twice()
    {
        string once = ModMessagePrefix.Apply("Example Mod", "hello");
        Assert.Equal(once, ModMessagePrefix.Apply("Example Mod", once));
        Assert.Equal("", ModMessagePrefix.Apply("Example Mod", null));
    }

    [Fact]
    public void A_long_reply_carries_the_prefix_on_its_first_piece_only()
    {
        string text = new string('a', 1200);
        var chunks = ModMessagePrefix.ReplyChunks("Example Mod", text);
        Assert.True(chunks.Length >= 3);
        Assert.StartsWith("Example Mod: ", chunks[0]);
        Assert.All(chunks.Skip(1), c => Assert.DoesNotContain("Example Mod", c));
        Assert.Equal("Example Mod: " + text, string.Concat(chunks));
        Assert.All(chunks, c => Assert.True(System.Text.Encoding.UTF8.GetByteCount(c) <= ReplyText.MaxUtf8Bytes));
        Assert.Equal(new[] { "plain" }, ModMessagePrefix.ReplyChunks("Example Mod", "plain", prefix: false));
        Assert.Empty(ModMessagePrefix.ReplyChunks("Example Mod", ""));
    }

    [Fact]
    public void Plugin_identity_is_read_from_the_plugin_attribute()
    {
        Assert.Equal(("Reply Mod", "2.0.0"), ModMessagePrefix.PluginIdentity(new ReplyOwner()));
        Assert.Equal(("", ""), ModMessagePrefix.PluginIdentity(new object()));
        Assert.Equal(("", ""), ModMessagePrefix.PluginIdentity(null));
    }

    [Fact]
    public void A_plugin_command_reply_is_prefixed_and_can_opt_out()
    {
        using var reg = CommandRegistry.RegisterType(typeof(ReplyCommands), new ReplyOwner(), "reply.mod");
        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".wp_reply"));
        Assert.Equal(new[] { "Reply Mod: done", "Reply Mod: named once", "[own] no prefix" }, ctx.Replies);
    }

    [Fact]
    public void A_command_whose_owner_names_no_plugin_replies_unchanged()
    {
        using var reg = CommandRegistry.RegisterType(typeof(ReplyCommands), new object(), "bare.owner");
        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".wp_reply"));
        Assert.Equal(new[] { "done", "Reply Mod: named once", "[own] no prefix" }, ctx.Replies);
        // The framework's own lines (usage, denials) never carry a plugin name either.
        Assert.Same(ctx, ModReplyContext.For(ctx, new object()));
    }

    [Fact]
    public void A_long_command_reply_goes_out_split_with_the_prefix_on_the_first_line()
    {
        using var reg = CommandRegistry.RegisterType(typeof(LongReplyCommands), new ReplyOwner(), "reply.mod");
        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".wp_long"));
        Assert.True(ctx.Replies.Count >= 3);
        Assert.StartsWith("Reply Mod: ", ctx.Replies[0]);
        Assert.All(ctx.Replies.Skip(1), r => Assert.DoesNotContain("Reply Mod", r));
        Assert.All(ctx.Replies, r => Assert.True(System.Text.Encoding.UTF8.GetByteCount(r) <= ReplyText.MaxUtf8Bytes));
        Assert.Equal("Reply Mod: " + LongReplyCommands.Text, string.Concat(ctx.Replies));
        Assert.Equal(ModMessagePrefix.ReplyChunks("Reply Mod", LongReplyCommands.Text), ctx.Replies);
    }

    [Fact]
    public void A_nested_dispatch_takes_the_dispatching_plugins_name_and_never_stacks_two()
    {
        var plain = new RecordingCtx();
        var outer = ModReplyContext.For(plain, new ReplyOwner());
        var inner = ModReplyContext.For(outer, new OtherOwner());
        Assert.IsType<ModReplyContext>(inner);
        Assert.Equal("Other Mod", ((ModReplyContext)inner).ModName);
        inner.Reply("hi");
        Assert.Equal(new[] { "Other Mod: hi" }, plain.Replies);
        // The same plugin again keeps its wrapper; a built-in (no plugin) gets the plain context back.
        Assert.Same(outer, ModReplyContext.For(outer, new ReplyOwner()));
        Assert.Same(plain, ModReplyContext.For(outer, new object()));
    }

    private static class LongReplyCommands
    {
        internal static readonly string Text = string.Concat(Enumerable.Repeat("sorted the iron chest. ", 60));

        [Command("wp_long")]
        public static void Run(ICommandContext ctx) => ctx.Reply(Text);
    }

    [@BepInPlugin("other.mod", "<color=#0f0>Other Mod</color>", "1.0.0")]
    private sealed class OtherOwner { }

    private sealed class RecordingCtx : ICommandContext
    {
        public List<string> Replies { get; } = new();
        public void Reply(string text) => Replies.Add(text ?? "");
        public bool IsAdmin => true;
    }

    private static class ReplyCommands
    {
        [Command("wp_reply")]
        public static void Run(ICommandContext ctx)
        {
            ctx.Reply("done");
            ctx.Reply("Reply Mod: named once");
            ctx.Reply("[own] no prefix", prefix: false);
        }
    }

    [AttributeUsage(AttributeTargets.Class)]
    private sealed class BepInPlugin : Attribute
    {
        public BepInPlugin(string guid, string name, string version)
        {
            GUID = guid;
            Name = name;
            Version = version;
        }

        public string GUID { get; }
        public string Name { get; }
        public string Version { get; }
    }

    [@BepInPlugin("reply.mod", "Reply Mod", "2.0.0")]
    private sealed class ReplyOwner { }
}
