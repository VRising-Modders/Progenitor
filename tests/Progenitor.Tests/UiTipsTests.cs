using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// P7b help, part B: the one-time tip cards through the kit presenter on a fake runtime: shown once, one at a time,
/// never over a modal, dropped with a closed anchor, dismissed by Got it / Show all tips / Escape, reset, released on
/// unload and on the Core teardown, the seen marks persisted per plugin, and the card's placement rules.
/// </summary>
[Collection("UiStatic")]
public sealed class UiTipsTests : IDisposable
{
    private const string PluginA = "example.help";
    private const string PluginB = "example.other";

    private readonly HelpRuntimeFake _runtime = new();
    private readonly UiHelpMemoryStore _store = new();
    private readonly object _ownerA = new();
    private readonly object _ownerB = new();
    private readonly List<IUiPanel> _panels = new();
    private bool _modal;

    public UiTipsTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        UiTerms.Teardown();
        UiTips.Teardown();
        UiTips.Presenter = new UiTipCardPresenter();
        UiTips.ModalOpen = () => _modal;
        UiHelpStore.UseForTests(_store);
    }

    public void Dispose()
    {
        UiTips.Teardown();
        UiWidgetLifetime.Uninstall(); // leave no help teardown registered for the next suite
        foreach (var p in _panels) p.Dispose();
        UiTips.ModalOpen = () => false;
        UiHelpStore.UseForTests(null);
        CoreUiSettings.Reset();
        UiApi.ResetForTests();
    }

    private IUiTip Tip(string id, object? owner = null, string plugin = PluginA, IUiPanel? anchor = null, Action? onShowAll = null) =>
        UiTips.Request(owner ?? _ownerA, plugin, id, "Title " + id, "Body of " + id + ".",
            new UiTipOptions { Anchor = anchor, OnShowAll = onShowAll });

    private IUiPanel ShownPanel(string title = "Belts")
    {
        var panel = UiApi.Panel(title, _ownerA, PluginA);
        _panels.Add(panel);
        panel.Show();
        return panel;
    }

    private IUiPanel? Card => UiTips.Showing?.View?.Panel;

    // ---- Once only --------------------------------------------------------------------------------

    [Fact]
    public void A_tip_shows_once_and_Got_it_marks_it_seen()
    {
        var tip = Tip("belts");
        Assert.Equal(UiTipState.Showing, tip.State);
        Assert.Equal(UiHandleState.Shown, Card!.State);
        Assert.Equal("Title belts", Card.Text);
        Assert.Equal(UiCaptureMode.WhenHovered, Card.CaptureMode);
        Assert.Single(_runtime.Placed); // placed after the show

        _runtime.ClickCell(UiTipCopy.ButtonsRow, UiTipCopy.GotItKey);
        Assert.Equal(UiTipState.Done, tip.State);
        Assert.Null(UiTips.Showing);
        Assert.True(UiTips.IsSeen(PluginA, "belts"));
        Assert.Equal("example.help:belts", _store.Read(UiHelpSlot.SeenTips));
        Assert.False(UiTips.Ticking);

        var again = Tip("belts");
        Assert.Equal(UiTipState.Seen, again.State);
        Assert.Null(UiTips.Showing);
    }

    [Fact]
    public void The_card_is_drawn_with_the_dialog_rows_and_keeps_the_dialog_button_order()
    {
        Tip("look");
        Assert.Contains(_runtime.Labels, l => l.Text == "Title look"); // the dialog title row (with the game's title rule)
        Assert.Contains(_runtime.Labels, l => l.Text == "Body of look."); // the centered dialog body
        var row = _runtime.Rows.Last(r => r.Text == UiTipCopy.ButtonsRow);
        var buttons = row.Cells().Where(c => c.Kind == CellKind.Button).ToList();
        // The dismissal's side (left) holds Show all tips; the main answer (right) is Got it, in the accent gold.
        Assert.Equal(new[] { UiTipCopy.ShowAllKey, UiTipCopy.GotItKey }, buttons.Select(c => c.Key));
        Assert.Equal(new[] { UiTipCopy.ShowAll, UiTipCopy.GotIt }, buttons.Select(c => c.Text));
        Assert.All(buttons, c => Assert.Equal(CellSkin.PopupButton, c.Skin));
        Assert.Equal(CellTone.Gold, buttons[1].Tone);
        Assert.Equal(buttons[0].MinWidth, buttons[1].MinWidth); // one width, as the dialogs
        // Were the card ever a NAV1 screen, the dialog rule would start the focus on the dismissal side.
        Assert.Equal(UiTipCopy.ShowAllKey, UiNavHost.StartKey(UiTipCardPresenter.Content("t", "b")));
        // It is not one: it takes input only while hovered, so a controller's A never reaches it.
        Assert.Equal(UiCaptureMode.WhenHovered, Card!.CaptureMode);
    }

    [Fact]
    public void Cards_show_one_at_a_time_and_a_repeat_request_returns_the_waiting_one()
    {
        var first = Tip("one");
        var second = Tip("two", _ownerB, PluginB);
        Assert.Same(second, Tip("two", _ownerB, PluginB));
        Assert.Equal(UiTipState.Showing, first.State);
        Assert.Equal(UiTipState.Queued, second.State);
        Assert.True(UiTips.Ticking);

        _runtime.ClickCell(UiTipCopy.ButtonsRow, UiTipCopy.GotItKey);
        Assert.Equal(UiTipState.Showing, second.State);
        Assert.Equal("Title two", Card!.Text);
        Assert.False(UiTips.IsSeen(PluginB, "one")); // seen marks are keyed by plugin
    }

    // ---- Modal ------------------------------------------------------------------------------------

    [Fact]
    public void A_card_waits_while_a_modal_is_open_and_shows_when_it_closes()
    {
        _modal = true;
        var tip = Tip("wait");
        Assert.Equal(UiTipState.Queued, tip.State);
        Assert.Null(UiTips.Showing);
        _runtime.Tick();
        Assert.Equal(UiTipState.Queued, tip.State);

        _modal = false;
        _runtime.Tick();
        Assert.Equal(UiTipState.Showing, tip.State);
    }

    [Fact]
    public void A_shown_card_steps_back_for_a_modal_and_returns_at_the_head_of_the_queue()
    {
        var first = Tip("first");
        var second = Tip("second");
        var firstPanel = Card!;
        _modal = true;
        _runtime.Tick();
        Assert.Equal(UiTipState.Queued, first.State);
        Assert.True(firstPanel.IsDisposed); // nothing of it stays over the dialog
        Assert.Equal(new[] { "first", "second" }, UiTips.Waiting.Select(t => t.Id));

        _modal = false;
        _runtime.Tick();
        Assert.Equal(UiTipState.Showing, first.State);
        Assert.Equal(UiTipState.Queued, second.State);
        Assert.False(UiTips.IsSeen(PluginA, "first"));
    }

    [Fact]
    public void The_default_modal_probe_sees_a_framework_dialog()
    {
        UiTips.ModalOpen = () => UiDialog.Current != null;
        using var dialog = UiApi.Dialog("Remove the belt?", "", "Remove", LabelTone.Danger, "Cancel");
        var tip = Tip("behind-dialog");
        Assert.Equal(UiTipState.Queued, tip.State);
    }

    // ---- Anchor -----------------------------------------------------------------------------------

    [Fact]
    public void A_waiting_card_whose_panel_closed_is_dropped_unseen_and_a_shown_one_closes()
    {
        var anchor = ShownPanel();
        var blocker = Tip("blocker");
        var waiting = Tip("anchored", anchor: anchor);
        anchor.Hide();
        _runtime.ClickCell(UiTipCopy.ButtonsRow, UiTipCopy.GotItKey); // the blocker answered; the next one's panel is gone
        Assert.Equal(UiTipState.Dropped, waiting.State);
        Assert.False(UiTips.IsSeen(PluginA, "anchored"));

        anchor.Show();
        var shown = Tip("anchored", anchor: anchor);
        Assert.Equal(UiTipState.Showing, shown.State);
        Assert.True(_runtime.Placed.Last().Anchor.IsKnown); // placed against its panel's rect
        var card = Card!;
        anchor.Hide();
        _runtime.Tick();
        Assert.Equal(UiTipState.Dropped, shown.State);
        Assert.True(card.IsDisposed);
        Assert.False(UiTips.IsSeen(PluginA, "anchored"));
        Assert.Equal(UiTipState.Done, blocker.State);
    }

    // ---- Escape, show all, dispose ------------------------------------------------------------------

    [Fact]
    public void Escape_closing_the_card_counts_as_Got_it()
    {
        var tip = Tip("esc");
        Card!.Hide(); // the framework's Escape sweep hides every shown panel
        _runtime.Tick();
        Assert.Equal(UiTipState.Done, tip.State);
        Assert.True(UiTips.IsSeen(PluginA, "esc"));
    }

    [Fact]
    public void Show_all_tips_marks_seen_and_opens_the_list_or_the_plugins_own_page()
    {
        UiTips.Define(_ownerA, PluginA, "later", "Later", "Not shown yet.");
        var tip = Tip("all");
        _runtime.ClickCell(UiTipCopy.ButtonsRow, UiTipCopy.ShowAllKey);
        Assert.Equal(UiTipState.Done, tip.State);
        Assert.True(UiTips.IsSeen(PluginA, "all"));
        Assert.True(UiTips.ListOpen);
        Assert.Contains(_runtime.Labels, l => l.Text == "Not shown yet.");
        Assert.Equal(new[] { ("later", false), ("all", true) }, UiTips.Infos(PluginA).Select(i => (i.Id, i.Seen)));

        int custom = 0;
        UiTips.Teardown();
        var own = Tip("own", onShowAll: () => custom++);
        _runtime.ClickCell(UiTipCopy.ButtonsRow, UiTipCopy.ShowAllKey);
        Assert.Equal(1, custom);
        Assert.False(UiTips.ListOpen);
        Assert.Equal(UiTipState.Done, own.State);
    }

    [Fact]
    public void Disposing_a_request_drops_it_without_marking_it_seen()
    {
        var shown = Tip("a");
        var waiting = Tip("b");
        waiting.Dispose();
        Assert.Equal(UiTipState.Dropped, waiting.State);
        shown.Dispose();
        Assert.Equal(UiTipState.Dropped, shown.State);
        Assert.Null(UiTips.Showing);
        Assert.Empty(UiTips.Waiting);
        Assert.Equal("", _store.Read(UiHelpSlot.SeenTips));
    }

    // ---- Reset ------------------------------------------------------------------------------------

    [Fact]
    public void Reset_makes_tips_show_again_per_plugin_and_per_tip()
    {
        Tip("x");
        _runtime.ClickCell(UiTipCopy.ButtonsRow, UiTipCopy.GotItKey);
        Tip("y");
        _runtime.ClickCell(UiTipCopy.ButtonsRow, UiTipCopy.GotItKey);
        Tip("x", _ownerB, PluginB);
        _runtime.ClickCell(UiTipCopy.ButtonsRow, UiTipCopy.GotItKey);
        Assert.True(UiTips.IsSeen(PluginA, "x") && UiTips.IsSeen(PluginA, "y") && UiTips.IsSeen(PluginB, "x"));

        UiTips.Reset(PluginA, "x");
        Assert.False(UiTips.IsSeen(PluginA, "x"));
        Assert.True(UiTips.IsSeen(PluginA, "y"));
        Assert.Equal(UiTipState.Showing, Tip("x").State);
        _runtime.ClickCell(UiTipCopy.ButtonsRow, UiTipCopy.GotItKey);

        UiTips.ResetAll(PluginA);
        Assert.False(UiTips.IsSeen(PluginA, "x"));
        Assert.False(UiTips.IsSeen(PluginA, "y"));
        Assert.True(UiTips.IsSeen(PluginB, "x")); // the other plugin keeps its marks
    }

    // ---- Unload and teardown ------------------------------------------------------------------------

    [Fact]
    public void Unload_drops_the_plugins_queued_and_shown_cards_and_its_list()
    {
        var shown = Tip("a");
        var queuedA = Tip("b");
        var queuedB = Tip("c", _ownerB, PluginB);
        var card = Card!;
        UiTips.ShowList(_ownerA, PluginA);
        Assert.True(UiTips.ListOpen);

        Assert.Equal(2, UiTips.DropOwner(_ownerA));
        Assert.Equal(UiTipState.Dropped, shown.State);
        Assert.Equal(UiTipState.Dropped, queuedA.State);
        Assert.True(card.IsDisposed);
        Assert.False(UiTips.ListOpen);
        Assert.Empty(UiTips.Infos(PluginA));
        Assert.Equal(UiTipState.Showing, queuedB.State); // the other plugin's card moves up
        Assert.Equal("", _store.Read(UiHelpSlot.SeenTips));
    }

    [Fact]
    public void The_Core_teardown_closes_every_card_and_stops_the_tick()
    {
        var shown = Tip("a");
        var queued = Tip("b");
        var card = Card!;
        Assert.Contains("help-tips", UiWidgetLifetime.Names);
        UiWidgetLifetime.Uninstall();
        Assert.Equal(UiTipState.Dropped, shown.State);
        Assert.Equal(UiTipState.Dropped, queued.State);
        Assert.True(card.IsDisposed);
        Assert.False(UiTips.Ticking);
        Assert.Empty(_runtime.Ticks);
    }

    // ---- Persistence ------------------------------------------------------------------------------

    [Fact]
    public void Seen_tips_survive_a_Core_reload_through_the_ui_cfg()
    {
        UiHelpStore.UseForTests(null);
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-tips-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "progenitor.ui.cfg");
            CoreUiSettings.SeenTips = new ConfigFile(path, saveOnInit: true)
                .Bind(UiHelpSettings.Section, UiHelpSettings.SeenTipsKey, "", UiHelpSettings.SeenTipsDescription);
            Tip("Island Share");
            _runtime.ClickCell(UiTipCopy.ButtonsRow, UiTipCopy.GotItKey);
            Assert.Contains("SeenTips = example.help:island share", File.ReadAllText(path));

            CoreUiSettings.Reset();
            UiTips.Teardown();
            var next = new ConfigFile(path, saveOnInit: false);
            next.Reload();
            CoreUiSettings.SeenTips = next.Bind(UiHelpSettings.Section, UiHelpSettings.SeenTipsKey, "", UiHelpSettings.SeenTipsDescription);
            Assert.Equal(UiTipState.Seen, Tip("island share").State);
        }
        finally
        {
            CoreUiSettings.Reset();
            try { Directory.Delete(dir, true); } catch { /* */ }
        }
    }

    // ---- Placement --------------------------------------------------------------------------------

    public static IEnumerable<object[]> Canvases() => new[]
    {
        new object[] { 1920f, 1080f },
        new object[] { 2560f, 1440f },
        new object[] { 3840f, 2160f },
        new object[] { 3440f, 1440f },
    };

    [Theory]
    [MemberData(nameof(Canvases))]
    public void A_card_never_covers_its_panel_when_there_is_room_and_always_stays_on_the_canvas(float w, float h)
    {
        const float cardW = 640f, cardH = 220f;
        var anchors = new List<(float MinX, float MinY, float MaxX, float MaxY)>
        {
            (w / 2 - 320, h / 2 - 300, w / 2 + 320, h / 2 + 300), // a centered window
            (16, h - 16 - 700, 656, h - 16),                      // the leftmost column
            (w - 656, 100, w - 16, h - 100),                      // the rightmost column
            (0, 0, w, h),                                         // the whole screen: no side has room
        };
        foreach (var a in anchors)
        {
            var p = UiTipLayout.Place(true, a.MinX, a.MinY, a.MaxX, a.MaxY, cardW, cardH, w, h);
            Assert.InRange(p.X, UiTipLayout.Margin, w - UiTipLayout.Margin - cardW);
            Assert.InRange(p.Y, UiTipLayout.Margin, h - UiTipLayout.Margin - cardH);
            bool overlaps = p.X < a.MaxX && p.X + cardW > a.MinX && p.Y < a.MaxY && p.Y + cardH > a.MinY;
            bool room = a.MaxX + UiTipLayout.Gap + cardW <= w - UiTipLayout.Margin
                || a.MinX - UiTipLayout.Gap - cardW >= UiTipLayout.Margin
                || a.MinY - UiTipLayout.Gap - cardH >= UiTipLayout.Margin
                || a.MaxY + UiTipLayout.Gap + cardH <= h - UiTipLayout.Margin;
            if (room) Assert.False(overlaps, "card over its panel at " + w + "x" + h + " side " + p.Side);
            else Assert.Equal(UiTipLayout.Side.Corner, p.Side);
        }

        var corner = UiTipLayout.Place(false, 0, 0, 0, 0, cardW, cardH, w, h);
        Assert.Equal(UiTipLayout.Side.Corner, corner.Side);
        Assert.Equal(w - UiTipLayout.Margin - cardW, corner.X);
        Assert.Equal(UiTipLayout.Margin, corner.Y);
    }

    [Theory]
    [InlineData(1920f, 1080f)]
    [InlineData(2560f, 1440f)]
    [InlineData(3840f, 2160f)]
    public void The_mockups_card_sits_16_right_of_its_window_top_aligned_and_on_the_canvas(float w, float h)
    {
        // The tip-card mockup: a 560x520 window at x 470, top y 170 of the 1920x1080 design space (bottom-left origin
        // here), scaled with the canvas for a constant-pixel ruler.
        float k = w / 1920f;
        float minX = 470 * k, maxX = 1030 * k, maxY = h - 170 * k, minY = maxY - 520 * k;
        const float cardW = 640f, cardH = 236f;
        var p = UiTipLayout.Place(true, minX, minY, maxX, maxY, cardW, cardH, w, h);
        Assert.Equal(UiTipLayout.Side.Right, p.Side);
        Assert.Equal(maxX + UiTipLayout.Gap, p.X);
        Assert.Equal(maxY - cardH, p.Y);
        Assert.True(p.X + cardW <= w - UiTipLayout.Margin && p.Y >= UiTipLayout.Margin);
    }

    [Fact]
    public void Beside_wins_over_below_and_a_card_taller_than_the_canvas_is_clamped()
    {
        var p = UiTipLayout.Place(true, 100, 300, 740, 900, 640, 220, 1920, 1080);
        Assert.Equal(UiTipLayout.Side.Right, p.Side);
        Assert.Equal(740 + UiTipLayout.Gap, p.X);
        Assert.Equal(900 - 220, p.Y); // top edges aligned
        var tall = UiTipLayout.Place(false, 0, 0, 0, 0, 640, 5000, 1920, 1080);
        Assert.Equal(UiTipLayout.Margin, tall.Y);
    }

    [Fact]
    public void Describe_reads_back_the_shown_and_waiting_cards()
    {
        Tip("a");
        Tip("b");
        var read = UiTips.Describe();
        var showing = (Dictionary<string, object?>)read["showing"]!;
        Assert.Equal("a", showing["id"]);
        Assert.Equal("Body of a.", showing["body"]);
        Assert.Single((List<object?>)read["waiting"]!);
        Assert.Equal(false, read["modal"]);
    }
}
