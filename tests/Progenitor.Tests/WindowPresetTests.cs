using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Progenitor.Commands;
using Progenitor.ControlsSample;
using Progenitor.Ui;
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
            Assert.Contains(s.Font, new[] { UiTextStyles.TitleFont, UiTextStyles.BodyFont });
            Assert.True(s.Size >= 16f, style + " is too small to read");
            Assert.False(string.IsNullOrWhiteSpace(s.UseWhen));
        }

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
        foreach (string file in new[] { "GalleryDemo.cs", "GalleryWindows.cs" })
        {
            string src = SourcePin.ReadFile("samples", "Progenitor.ControlsSample", file);
            foreach (string raw in new[] { "new Color", "PaletteColor", "fontSize", "FontSize", "<color", "<size", "<b>", "\"centre\"", "horizontalAlignment" })
                Assert.DoesNotContain(raw, src, StringComparison.Ordinal);
            // The only alignment a gallery file sets is the kit's left default for a page.
            foreach (Match m in Regex.Matches(src, @"Alignment\s*=\s*[^;]+;"))
                Assert.Equal("Alignment = \"left\";", m.Value);
            // The one hex color is the color field's own value, not a text color.
            var hex = Regex.Matches(src, "#[0-9A-Fa-f]{6}").Select(m => m.Value).Distinct().ToList();
            Assert.True(hex.Count == 0 || hex.SequenceEqual(new[] { "#B03030" }), file + " has a raw color: " + string.Join(",", hex));
            // Text goes through panel.Text(..., UiTextStyle.X) or a tone label; a bare panel.Label("...") has no style.
            Assert.DoesNotMatch(new Regex(@"\.Label\(""[^""]*""\)"), src);
        }
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
    [InlineData("Sortmancer", "sortmancer sorted 3 chests", true, "sortmancer sorted 3 chests")]
    [InlineData("Sortmancer", "[Sortmancer] sorted", true, "[Sortmancer] sorted")]
    [InlineData("Sortmancer", "<color=#fff>Sortmancer</color> sorted", true, "<color=#fff>Sortmancer</color> sorted")]
    [InlineData("Sortmancer", "Sortmancer2 is a different mod", true, "Sortmancer: Sortmancer2 is a different mod")]
    [InlineData("Sortmancer", "Sorted 3 chests.", false, "Sorted 3 chests.")]
    [InlineData("", "Sorted.", true, "Sorted.")]
    [InlineData("  ", "Sorted.", true, "Sorted.")]
    [InlineData("Sortmancer", "", true, "")]
    [InlineData("Sortmancer", "   ", true, "   ")]
    [InlineData("Sortmancer", "Sortmancer", true, "Sortmancer")]
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
