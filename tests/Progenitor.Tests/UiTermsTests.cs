using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.ControlsSample;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// P7b help, part A: the glossary, the term markers' hide / auto-hide / show-again rules and their persistence, keyed by
/// plugin, and a marked control through a fake runtime (the hover explanation, the drawn marker). Every assertion is an
/// outcome: the tooltip the player sees, whether the marker is drawn, what the cfg file holds.
/// </summary>
[Collection("UiStatic")]
public sealed class UiTermsTests : IDisposable
{
    private const string PluginA = "example.help";
    private const string PluginB = "example.other";

    private readonly HelpRuntimeFake _runtime = new();
    private readonly UiHelpMemoryStore _store = new();
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();
    private double _now = 100;

    public UiTermsTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        UiTerms.Teardown();
        UiTips.Teardown();
        UiHelpStore.UseForTests(_store);
        UiTerms.Clock = () => _now;
    }

    public void Dispose()
    {
        foreach (var p in _panels) p.Dispose();
        UiTerms.Teardown();
        UiTips.Teardown();
        UiWidgetLifetime.Uninstall(); // leave no help teardown registered for the next suite
        UiHelpStore.UseForTests(null);
        UiTerms.Clock = () => 0;
        CoreUiSettings.Reset();
        UiApi.ResetForTests();
    }

    private IUiPanel NewPanel(string pluginId = PluginA)
    {
        var panel = UiApi.Panel("Help", _owner, pluginId);
        _panels.Add(panel);
        return panel;
    }

    /// <summary>One hover, a little later than the last (past the debounce).</summary>
    private void Hover(string pluginId, string term)
    {
        _now += 5;
        UiTerms.NoteHover(pluginId, term);
    }

    // ---- Glossary --------------------------------------------------------------------------------

    [Fact]
    public void The_glossary_explains_a_term_without_case_or_spacing_mattering_and_rewording_replaces_it()
    {
        UiTerms.Define(PluginA, "Mass Deposit", "Where each item belongs.");
        UiTerms.Define(PluginA, "Belt", "A named list of chests.");
        Assert.Equal("Where each item belongs.", UiTerms.Explain(PluginA, "  mass   DEPOSIT "));
        Assert.True(UiTerms.IsDefined(PluginA, "belt"));
        Assert.Equal("", UiTerms.Explain(PluginB, "Belt")); // another plugin's glossary

        UiTerms.Define(PluginA, "belt", "Chests used together.");
        Assert.Equal("Chests used together.", UiTerms.Explain(PluginA, "Belt"));
        Assert.Equal(new[] { "Mass Deposit", "belt" }, UiTerms.Glossary(PluginA).Select(kv => kv.Key));

        UiTerms.Define(PluginA, "Belt", "");
        Assert.False(UiTerms.IsDefined(PluginA, "Belt"));
        Assert.Single(UiTerms.Glossary(PluginA));
        Assert.Throws<ArgumentException>(() => UiTerms.Define(PluginA, " ", "x"));
    }

    [Fact]
    public void Stored_keys_escape_their_separators_and_round_trip()
    {
        string key = UiHelpStore.Key("a:b,c", "Cap = 5%");
        Assert.Equal("a%3Ab%2Cc:cap %3D 5%25", key);
        var set = UiHelpStore.ParseSet(UiHelpStore.FormatSet(new[] { key, "x:y" }));
        Assert.Contains(key, set);
        Assert.Equal(2, set.Count);
        var counts = UiHelpStore.ParseCounts("x:y=2,bad,z:w=-1,q:r=abc," + key + "=4");
        Assert.Equal(2, counts.Count);
        Assert.Equal(4, counts[key]);
        Assert.Equal("a%3Ab%2Cc:cap %3D 5%25=4,x:y=2", UiHelpStore.FormatCounts(counts));
    }

    // ---- Markers ---------------------------------------------------------------------------------

    [Fact]
    public void A_marker_hides_itself_after_three_hovers_and_quick_repeats_count_once()
    {
        UiTerms.Define(PluginA, "Cap", "Stations stop at this many.");
        Assert.Equal(3, UiTerms.AutoHideAfter);
        Assert.True(UiTerms.MarkerShown(PluginA, "Cap"));

        Hover(PluginA, "Cap");
        _now += 0.3;
        Assert.False(UiTerms.NoteHover(PluginA, "Cap")); // within the debounce: not counted
        Assert.Equal(1, UiTerms.Hovers(PluginA, "Cap"));
        Hover(PluginA, "Cap");
        Assert.True(UiTerms.MarkerShown(PluginA, "Cap"));
        _now += 5;
        Assert.True(UiTerms.NoteHover(PluginA, "Cap"));
        Assert.False(UiTerms.MarkerShown(PluginA, "Cap"));
        Assert.Equal(0, UiTerms.Hovers(PluginA, "Cap")); // the count is dropped once hidden
        Assert.Equal("example.help:cap", _store.Read(UiHelpSlot.HiddenTerms));
        Assert.Equal("Stations stop at this many.", UiTerms.Explain(PluginA, "Cap")); // the explanation stays
    }

    [Fact]
    public void Auto_hide_zero_never_hides_and_an_undefined_term_has_no_marker()
    {
        UiTerms.Define(PluginA, "Loop", "Chests feeding each other.");
        UiTerms.AutoHideAfter = 0;
        for (int i = 0; i < 10; i++) Hover(PluginA, "Loop");
        Assert.True(UiTerms.MarkerShown(PluginA, "Loop"));
        Assert.Equal(0, UiTerms.Hovers(PluginA, "Loop"));
        Assert.False(UiTerms.MarkerShown(PluginA, "Island"));
        UiTerms.AutoHideAfter = 99;
        Assert.Equal(UiHelpSettings.MaxAutoHide, UiTerms.AutoHideAfter);
    }

    [Fact]
    public void Markers_are_keyed_by_plugin_and_show_markers_restores_only_that_plugin()
    {
        UiTerms.Define(PluginA, "Cap", "A's cap.");
        UiTerms.Define(PluginB, "Cap", "B's cap.");
        UiTerms.Define(PluginA, "Reserve", "A's reserve.");
        UiTerms.HideMarker(PluginA, "Cap");
        Hover(PluginA, "Reserve");
        UiTerms.HideMarker(PluginB, "Cap");
        Assert.False(UiTerms.MarkerShown(PluginA, "Cap"));
        Assert.False(UiTerms.MarkerShown(PluginB, "Cap"));
        Assert.Equal(1, UiTerms.Hovers(PluginA, "Reserve"));

        UiTerms.ShowMarkers(PluginA);
        Assert.True(UiTerms.MarkerShown(PluginA, "Cap"));
        Assert.Equal(0, UiTerms.Hovers(PluginA, "Reserve")); // counts start again
        Assert.False(UiTerms.MarkerShown(PluginB, "Cap")); // B untouched
    }

    [Fact]
    public void A_plugins_toggle_reads_and_writes_all_of_its_markers()
    {
        UiTerms.Define(PluginA, "Belt", "x");
        UiTerms.Define(PluginA, "Category", "y");
        int changes = 0;
        using var sub = UiTerms.OnMarkersChanged(PluginA, () => changes++);
        Assert.True(UiTerms.MarkersOn(PluginA));
        UiTerms.SetMarkers(PluginA, false);
        Assert.False(UiTerms.MarkerShown(PluginA, "Belt"));
        Assert.False(UiTerms.MarkerShown(PluginA, "Category"));
        Assert.False(UiTerms.MarkersOn(PluginA));
        UiTerms.SetMarkers(PluginA, true);
        Assert.True(UiTerms.MarkersOn(PluginA));
        Assert.Equal(2, changes);

        var panel = NewPanel();
        var toggle = panel.TermMarkersToggle();
        Assert.True(toggle.Value);
        UiTerms.HideMarker(PluginA, "Belt"); // hidden elsewhere: the toggle follows
        Assert.False(toggle.Value);
    }

    [Fact]
    public void Hidden_markers_and_counts_survive_a_Core_reload_through_the_ui_cfg()
    {
        UiHelpStore.UseForTests(null); // the real settings store
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-help-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "progenitor.ui.cfg");
            void Bind(ConfigFile f)
            {
                CoreUiSettings.HiddenTermMarkers = f.Bind(UiHelpSettings.Section, UiHelpSettings.HiddenTermsKey, "", UiHelpSettings.HiddenTermsDescription);
                CoreUiSettings.TermMarkerHovers = f.Bind(UiHelpSettings.Section, UiHelpSettings.TermHoversKey, "", UiHelpSettings.TermHoversDescription);
                CoreUiSettings.TermMarkerAutoHide = f.Bind(UiHelpSettings.Section, UiHelpSettings.AutoHideKey, UiHelpSettings.DefaultAutoHide, UiHelpSettings.AutoHideDescription);
            }

            var file = new ConfigFile(path, saveOnInit: true);
            Bind(file);
            UiTerms.Define(PluginA, "Belt", "x");
            UiTerms.Define(PluginA, "Cap", "y");
            UiTerms.HideMarker(PluginA, "Belt");
            Hover(PluginA, "Cap");
            string text = File.ReadAllText(path);
            Assert.Contains("HiddenTermMarkers = example.help:belt", text);
            Assert.Contains("TermMarkerHovers = example.help:cap=1", text);

            // The next generation: the statics are new, the same keys of the same file are bound again.
            CoreUiSettings.Reset();
            UiTerms.Teardown();
            var next = new ConfigFile(path, saveOnInit: false);
            next.Reload();
            Bind(next);
            UiTerms.Define(PluginA, "Belt", "x");
            UiTerms.Define(PluginA, "Cap", "y");
            Assert.False(UiTerms.MarkerShown(PluginA, "Belt"));
            Assert.Equal(1, UiTerms.Hovers(PluginA, "Cap"));
        }
        finally
        {
            CoreUiSettings.Reset();
            try { Directory.Delete(dir, true); } catch { /* */ }
        }
    }

    // ---- Marked controls -------------------------------------------------------------------------

    [Fact]
    public void A_marked_control_explains_the_term_on_hover_and_its_marker_goes_after_three_hovers()
    {
        UiTerms.Define(PluginA, "Belt", "A named list of chests.");
        var panel = NewPanel();
        var label = panel.Label("Ore in");
        var lease = label.Term("belt");
        var widget = _runtime.Labels.Single(w => w.Text == "Ore in");
        var marker = _runtime.Markers.Single();
        Assert.Same(widget, marker.Widget);
        Assert.Null(marker.CellKey);
        Assert.True(marker.Shown);

        for (int i = 0; i < 3; i++)
        {
            _now += 5;
            _runtime.Hover(widget, true);
            Assert.Equal(("Belt", "A named list of chests."), _runtime.Tooltip);
            _runtime.Hover(widget, false);
            Assert.Null(_runtime.Tooltip);
        }

        Assert.False(marker.Shown); // hidden after the third hover
        _now += 5;
        _runtime.Hover(widget, true);
        Assert.Equal(("Belt", "A named list of chests."), _runtime.Tooltip); // still explains

        UiTerms.ShowMarkers(PluginA);
        Assert.True(marker.Shown);
        lease.Dispose();
        Assert.True(marker.Disposed);
        Assert.Null(_runtime.Tooltip);
        Assert.False(_runtime.HasHover(widget));
        Assert.Empty(UiTerms.Marked);
    }

    [Fact]
    public void A_term_defined_after_marking_still_explains_and_draws_its_marker_then()
    {
        var panel = NewPanel();
        var button = panel.Button("Category");
        button.Term("Category");
        var marker = _runtime.Markers.Single();
        Assert.False(marker.Shown); // not in the glossary yet
        var widget = _runtime.Buttons.Single(w => w.Text == "Category");
        _runtime.Hover(widget, true);
        Assert.Null(_runtime.Tooltip);

        UiTerms.Define(PluginA, "Category", "A named set of items.");
        Assert.True(marker.Shown);
        _runtime.Hover(widget, true);
        Assert.Equal(("Category", "A named set of items."), _runtime.Tooltip);
    }

    [Fact]
    public void A_cell_is_marked_through_its_own_hover_and_a_widget_can_forward_its_hover()
    {
        UiTerms.Define(PluginA, "Reserve", "Left untouched.");
        var panel = NewPanel();
        panel.Show();
        var tabs = panel.Tabs("help-tabs", new[] { "Reserve", "Cap" });
        var row = (IUiCellRow)typeof(UiTabsHandle).GetField("_row", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(tabs)!;
        using var binding = UiTerms.MarkCell(row, "tab0", "Reserve");
        var marker = _runtime.Markers.Single();
        Assert.Equal("tab0", marker.CellKey);
        Assert.True(marker.Shown);
        Assert.True(UiCellRowHandle.TryHoverCell("help-tabs:tabs", "tab0", true));
        Assert.Equal(("Reserve", "Left untouched."), _runtime.Tooltip);
        Assert.Equal(1, UiTerms.Hovers(PluginA, "Reserve"));

        using var forwarded = UiTerms.MarkCell(row, "tab1", "Reserve", watchHover: false);
        _now += 5;
        forwarded.Hover(true);
        Assert.Equal(2, UiTerms.Hovers(PluginA, "Reserve"));
    }

    [Fact]
    public void A_tab_caption_takes_a_term_through_the_tabs_own_hover_and_gives_it_back()
    {
        UiTerms.Define(PluginA, "Belt", "Chests used together.");
        var panel = NewPanel();
        panel.Show();
        var tabs = panel.Tabs("ht", new[] { "Belts", "Categories" });
        tabs.SetTooltip(0, "plain tip");
        tabs.SetTerm(0, "Belt");
        Assert.Equal("Belt", ((UiTabsHandle)tabs).TermOf(0));
        var marker = _runtime.Markers.Single();
        Assert.Equal("tab0", marker.CellKey);
        Assert.True(marker.Shown);

        Assert.True(UiCellRowHandle.TryHoverCell("ht:tabs", "tab0", true));
        Assert.Equal(("Belt", "Chests used together."), _runtime.Tooltip); // the term wins over the plain tooltip
        Assert.Equal(1, UiTerms.Hovers(PluginA, "Belt"));
        Assert.True(UiCellRowHandle.TryHoverCell("ht:tabs", "tab0", false));
        Assert.Null(_runtime.Tooltip);

        tabs.SetTerm(0, "");
        Assert.True(marker.Disposed);
        Assert.True(UiCellRowHandle.TryHoverCell("ht:tabs", "tab0", true));
        Assert.Equal(("Belts", "plain tip"), _runtime.Tooltip); // the tab's own hover still works

        tabs.SetTerm(1, "Belt");
        tabs.Dispose();
        Assert.True(_runtime.Markers.Last().Disposed);
        Assert.Empty(UiTerms.Marked);
    }

    [Fact]
    public void A_list_rows_term_follows_its_item_when_the_rows_are_reordered()
    {
        UiTerms.Define(PluginA, "Belt", "Chests used together.");
        var panel = NewPanel();
        panel.Show();
        var list = panel.OrderedList("hl", new[]
        {
            new UiListItem("ore", "Ore in") { Term = "Belt", Tooltip = "ignored while the term explains" },
            new UiListItem("bin", "Iron bin") { Tooltip = "A single chest." },
        });
        var handle = (UiOrderedListHandle)list;
        Assert.Equal("Belt", handle.TermAt(0));
        Assert.Equal("", handle.TermAt(1));
        var first = _runtime.Markers.Single();
        Assert.Equal(UiWidgetLayout.CellTitle, first.CellKey);

        list.Move("ore", 1);
        Assert.Equal("", handle.TermAt(0));
        Assert.Equal("Belt", handle.TermAt(1)); // the marker went with the item
        Assert.True(first.Disposed);

        _now += 5;
        Assert.True(UiCellRowHandle.TryHover("hl:row1", true));
        Assert.Equal(("Belt", "Chests used together."), _runtime.Tooltip);
        Assert.True(UiCellRowHandle.TryHover("hl:row1", false));
        Assert.True(UiCellRowHandle.TryHover("hl:row0", true));
        Assert.Equal(("Iron bin", "A single chest."), _runtime.Tooltip); // a row without a term keeps its tooltip
        Assert.Equal(1, UiTerms.Hovers(PluginA, "Belt"));

        list.Dispose();
        Assert.Empty(UiTerms.Marked);
    }

    [Fact]
    public void The_Core_teardown_drops_the_glossary_and_every_marker()
    {
        UiTerms.Define(PluginA, "Cap", "x");
        var panel = NewPanel();
        panel.Label("Cap 500").Term("Cap");
        Assert.Single(UiTerms.Marked);
        Assert.Contains("help-terms", UiWidgetLifetime.Names);
        UiWidgetLifetime.Uninstall();
        Assert.Empty(UiTerms.Marked);
        Assert.False(UiTerms.IsDefined(PluginA, "Cap"));
        Assert.True(_runtime.Markers.Single().Disposed);
    }

    [Fact]
    public void Describe_reads_back_the_glossary_and_marker_state()
    {
        UiTerms.Define(PluginA, "Belt", "x");
        UiTerms.HideMarker(PluginA, "Belt");
        var read = UiTerms.Describe(PluginA);
        Assert.Equal(3, read["autoHideAfter"]);
        Assert.Equal(false, read["markersOn"]);
        var term = (Dictionary<string, object?>)((List<object?>)read["terms"]!).Single()!;
        Assert.Equal("Belt", term["term"]);
        Assert.Equal(false, term["marker"]);
    }

    // ---- Marker art ------------------------------------------------------------------------------

    [Fact]
    public void The_marker_art_is_a_ring_with_an_i_and_sits_after_the_text_inside_its_rect()
    {
        int n = UiTermMarkerArt.TextureSize;
        Assert.Equal(0f, UiTermMarkerArt.Alpha(0, 0)); // corner: outside the ring
        Assert.True(UiTermMarkerArt.Alpha(n / 2, 1) > 0.9f); // bottom of the ring
        Assert.True(UiTermMarkerArt.Alpha(n / 2, n / 2) > 0.9f); // the stem
        Assert.True(UiTermMarkerArt.Alpha(n / 2, n / 2 + (int)(n * 0.20f)) > 0.9f); // the dot
        Assert.Equal(0f, UiTermMarkerArt.Alpha(n / 2 - (int)(n * 0.25f), n / 2)); // inside the ring, beside the stem
        Assert.Equal(13f, UiTermMarkerArt.SizeFor(18f));
        Assert.Equal(10f, UiTermMarkerArt.SizeFor(8f));
        Assert.Equal(22f, UiTermMarkerArt.SizeFor(60f));
        Assert.Equal(100f + UiTermMarkerArt.Gap + 6.5f, UiTermMarkerArt.CenterX(100f, -200f, 200f, 13f));
        Assert.Equal(200f - 6.5f, UiTermMarkerArt.CenterX(199f, -200f, 200f, 13f)); // a full caption: kept inside
        Assert.Equal(-200f + UiTermMarkerArt.Gap + 6.5f, UiTermMarkerArt.CenterX(float.NaN, -200f, 200f, 13f)); // no text: at the left
    }

    // ---- The demo --------------------------------------------------------------------------------

    [Fact]
    public void The_help_demo_marks_its_terms_and_its_buttons_answer()
    {
        var lines = new List<string>();
        var hud = new FakeHelpHud();
        var demo = new WidgetsDemo(lines.Add, null) { HelpHud = hud };
        var panel = NewPanel("progenitor.controlssample");
        var tabs = panel.Tabs("demo-tabs", new[] { "List", "Help" });
        demo.BuildHelp(panel, tabs, 1);

        Assert.Equal(new[] { "Belt", "Category", "Reserve", "Cap" }, UiTerms.Glossary("progenitor.controlssample").Select(kv => kv.Key));
        Assert.Equal(8, UiTerms.Marked.Count); // Belt twice, Category, Reserve, Cap; two tab captions; one list row
        Assert.All(_runtime.Markers, m => Assert.True(m.Shown));

        UiTerms.HideMarker("progenitor.controlssample", "Belt");
        _runtime.Click("Show markers again");
        Assert.True(UiTerms.MarkerShown("progenitor.controlssample", "Belt"));
        Assert.Equal("Controls sample: widget help markers shown", lines.Last());

        _runtime.Click("Show tip");
        Assert.Equal("help-demo", hud.LastTip);
        Assert.Same(panel, hud.LastAnchor);
        Assert.Equal("Controls sample: widget help tip = Showing", lines.Last());
        _runtime.Click("Reset tips");
        Assert.Equal(1, hud.Resets);
        Assert.Equal("Controls sample: widget help tips reset", lines.Last());
        _runtime.Click("All tips");
        Assert.Equal(1, hud.Lists);
    }

    private sealed class FakeHelpHud : IHelpDemoHud
    {
        public string? LastTip;
        public IUiPanel? LastAnchor;
        public int Resets, Lists;

        public string Tip(string id, string title, string body, IUiPanel? anchor)
        {
            LastTip = id;
            LastAnchor = anchor;
            return "Showing";
        }

        public void ShowAllTips() => Lists++;

        public void ResetTips() => Resets++;
    }
}

/// <summary>
/// P7b: a client runtime that hosts panels, kit controls and cell rows, records hover watches, the tooltip and the
/// drawn term markers, and places tip cards (<see cref="IUiHelpRuntime"/>).
/// </summary>
internal sealed class HelpRuntimeFake : CellRowTests.PlainRuntime, IUiRuntime, IWidgetRuntime, IUiHelpRuntime
{
    public readonly List<CellRowTests.FakeWidget> Labels = new();
    public readonly List<CellRowTests.FakeWidget> Buttons = new();
    public readonly List<FakeMarker> Markers = new();
    public readonly List<Action> Ticks = new();
    public readonly List<(IRuntimePanel Card, ScreenRect Anchor)> Placed = new();
    private readonly Dictionary<IRuntimeWidget, Action<bool>> _hovers = new();
    public (string Title, string Body)? Tooltip;

    public readonly List<CellRowTests.FakeCellRow> Rows = new();

    public override IRuntimeCellRow? AddCellRow(IRuntimePanel panel, CellRowSpec spec, bool header)
    {
        var row = new CellRowTests.FakeCellRow(null, spec, header);
        Rows.Add(row);
        return row;
    }

    /// <summary>Click a button cell of the newest row with this key through its wired slot.</summary>
    public void ClickCell(string rowKey, string cell)
    {
        var row = Rows.Last(r => r.Text == rowKey);
        Assert.NotEqual(0, row.WiredIdOf(cell));
        UiSlotTable.BootstrapDispatch(row.WiredIdOf(cell), (byte)UiSlotKind.Click, 0f, false, null);
    }

    public new IRuntimeWidget? AddLabel(IRuntimePanel panel, string text)
    {
        var w = new CellRowTests.FakeWidget { Text = text };
        Labels.Add(w);
        return w;
    }

    public new IRuntimeWidget? AddButton(IRuntimePanel panel, string text)
    {
        var w = new CellRowTests.FakeWidget { Text = text };
        Buttons.Add(w);
        return w;
    }

    public IRuntimeWidget? AddPageButton(IRuntimePanel panel, string caption) => AddButton(panel, caption);

    /// <summary>Every kit widget is on screen at a fixed place.</summary>
    public ScreenRect WidgetRect(IRuntimeWidget widget) => ScreenRect.FromCorners(600f, 400f, 1200f, 440f);

    /// <summary>Click the newest button with this caption through its wired slot.</summary>
    public void Click(string caption)
    {
        var w = Buttons.Last(b => b.Text == caption);
        Assert.NotEqual(0, w.WiredId);
        UiSlotTable.BootstrapDispatch(w.WiredId, (byte)UiSlotKind.Click, 0f, false, null);
    }

    public void Hover(IRuntimeWidget widget, bool on)
    {
        if (_hovers.TryGetValue(widget, out var h)) h(on);
    }

    public bool HasHover(IRuntimeWidget widget) => _hovers.ContainsKey(widget);

    public void Tick()
    {
        foreach (var t in Ticks.ToArray()) t();
    }

    // IWidgetRuntime
    public void ShowTooltip(ScreenRect anchor, string title, string body, Func<bool>? stillShown = null) => Tooltip = (title, body);

    public void HideTooltip() => Tooltip = null;

    public IDisposable? WatchCell(IRuntimeCellRow row, string cellKey, Action<UiPointerEvent> handler, object? owner) => null;

    public IDisposable? WatchHover(IRuntimeWidget widget, Action<bool> onHover, object? owner)
    {
        _hovers[widget] = onHover;
        return new ActionDisposable(() => _hovers.Remove(widget));
    }

    public IDisposable EveryFrame(Action tick)
    {
        Ticks.Add(tick);
        return new ActionDisposable(() => Ticks.Remove(tick));
    }

    public string DrawableGlyph(string preferred, string fallback) => preferred;

    public new PointerSample ReadPointer() => PointerSample.At(0f, 0f);

    public bool PrimaryHeld() => false;

    public object? ItemIcon(int prefab) => null;

    public string? ItemName(int prefab) => null;

    // IUiHelpRuntime
    public IUiTermMarkerView? AttachTermMarker(IRuntimeWidget widget, string? cellKey)
    {
        var m = new FakeMarker(widget, cellKey);
        Markers.Add(m);
        return m;
    }

    public bool PlaceTipCard(IRuntimePanel card, ScreenRect anchor)
    {
        Placed.Add((card, anchor));
        return true;
    }

    internal sealed class FakeMarker : IUiTermMarkerView
    {
        public FakeMarker(IRuntimeWidget widget, string? cellKey)
        {
            Widget = widget;
            CellKey = cellKey;
        }

        public IRuntimeWidget Widget { get; }
        public string? CellKey { get; }
        public bool Shown { get; set; } = true;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
