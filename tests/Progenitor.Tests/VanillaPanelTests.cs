using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Spec P6: the vanilla panel sites. Game-free: the catalog, the open/close/target tracker, the
/// placement rules and the handle's buttons, events and fit are driven against a fake runtime. The
/// Unity half (<c>VanillaPanelSiteResolver.cs</c>) is not compiled here.
/// </summary>
[Collection("PluginLogger")]
public class VanillaPanelTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly FakePanelRuntime _client = new();
    private readonly object _owner = new();

    private List<string> Lines => _log.Infos.Concat(_log.Warnings).Concat(_log.Errors).ToList();

    public VanillaPanelTests()
    {
        Plugin.Logger = _log;
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_client);
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    private static VanillaPanelTarget Chest(int index = 812, int net = 40) =>
        new(VanillaPanelKind.Container, index, 3, net, 1, 0);

    private static VanillaSiteSample Open(int instance, VanillaPanelTarget target, bool donor = true) =>
        new(resolved: true, instanceId: instance, donorPresent: donor, parentLayoutGroup: false, parentName: "SubMenusParent", target: target);

    // ---------------------------------------------------------------- catalog

    [Fact]
    public void Every_panel_site_is_defined_and_the_next_integer_is_not()
    {
        foreach (var site in new[]
                 {
                     VanillaUiSite.ContainerPanel, VanillaUiSite.StationPanel, VanillaUiSite.InventoryPanel,
                     VanillaUiSite.CastleHeartPanel, VanillaUiSite.InventoryTooltip,
                 })
        {
            Assert.True(VanillaUiSites.IsDefined(site));
            Assert.True(VanillaUiSites.IsPanel(site));
        }

        Assert.False(VanillaUiSites.IsPanel(VanillaUiSite.MapWarEventInfo));
        Assert.False(VanillaUiSites.IsDefined((VanillaUiSite)7));
    }

    [Fact]
    public void ThronePanel_is_the_castle_heart_window_under_its_requirements_name()
    {
        Assert.Equal(VanillaUiSite.CastleHeartPanel, VanillaUiSite.ThronePanel);
        Assert.Equal("CastleHeartPanel", VanillaUiSites.Key(VanillaUiSite.ThronePanel));
    }

    [Theory]
    [InlineData(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.Default, true, VanillaPanelAnchor.LeftEdge)]
    [InlineData(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.RightEdge, true, VanillaPanelAnchor.RightEdge)]
    [InlineData(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.BesideRename, true, VanillaPanelAnchor.BesideRename)]
    [InlineData(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.BesideSort, false, VanillaPanelAnchor.Default)]
    [InlineData(VanillaUiSite.StationPanel, VanillaPanelAnchor.RightEdge, true, VanillaPanelAnchor.RightEdge)]
    [InlineData(VanillaUiSite.StationPanel, VanillaPanelAnchor.BesideRename, false, VanillaPanelAnchor.Default)]
    [InlineData(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.Default, true, VanillaPanelAnchor.BesideSort)]
    [InlineData(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.LeftEdge, false, VanillaPanelAnchor.Default)]
    [InlineData(VanillaUiSite.CastleHeartPanel, VanillaPanelAnchor.RightEdge, true, VanillaPanelAnchor.RightEdge)]
    [InlineData(VanillaUiSite.InventoryTooltip, VanillaPanelAnchor.Default, true, VanillaPanelAnchor.TooltipBody)]
    [InlineData(VanillaUiSite.MapWarEventInfo, VanillaPanelAnchor.Default, true, VanillaPanelAnchor.Default)]
    [InlineData(VanillaUiSite.MapWarEventInfo, VanillaPanelAnchor.LeftEdge, false, VanillaPanelAnchor.Default)]
    public void Anchors_resolve_per_site(VanillaUiSite site, VanillaPanelAnchor asked, bool ok, VanillaPanelAnchor resolved)
    {
        Assert.Equal(ok, VanillaUiSites.TryResolveAnchor(site, asked, out var got));
        if (ok) Assert.Equal(resolved, got);
    }

    [Fact]
    public void An_anchor_the_site_does_not_accept_gives_a_disabled_handle_and_one_line()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");

        Assert.False(h.Enabled);
        Assert.Equal(0, _client.WatchCount);
        Assert.Contains(Lines, l => l.Contains("anchor LeftEdge is not an anchor this site accepts"));
        Assert.False(h.Button("x").Enabled);
        h.Dispose();
    }

    [Fact]
    public void The_container_name_carries_the_site_and_the_anchor()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.RightEdge, _owner, "example.panels");
        Assert.Equal("progenitor-ui-example.panels-ContainerPanel-right", h.ContainerName);
        Assert.Equal(VanillaPanelAnchor.RightEdge, h.Anchor);
        h.Dispose();
    }

    // ---------------------------------------------------------------- tracker

    [Fact]
    public void The_tracker_turns_a_level_into_open_target_and_close_edges()
    {
        var t = new VanillaPanelTracker();
        Assert.Null(t.Observe(false, default, out _));
        Assert.Equal(VanillaPanelChange.Opened, t.Observe(true, Chest(1), out var prev));
        Assert.Equal(VanillaPanelTarget.None, prev);
        Assert.Null(t.Observe(true, Chest(1), out _));
        Assert.Equal(VanillaPanelChange.TargetChanged, t.Observe(true, Chest(2), out prev));
        Assert.Equal(Chest(1), prev);
        Assert.Equal(VanillaPanelChange.Closed, t.Observe(false, default, out prev));
        Assert.Equal(Chest(2), prev);
        Assert.False(t.IsOpen);
        Assert.Equal(VanillaPanelTarget.None, t.Target);
    }

    [Fact]
    public void A_target_reports_its_network_id_words_and_prints_one_grep_line()
    {
        var t = new VanillaPanelTarget(VanillaPanelKind.Workstation, 812, 3, 40, 0x0102, 7);
        Assert.Equal(40, t.NetworkIdIndex);
        Assert.Equal(2, t.NetworkIdGeneration);
        Assert.True(t.HasEntity);
        Assert.True(t.HasNetworkId);
        Assert.Equal("kind=Workstation entity=812:3 net=40:2", t.ToString());
        var item = new VanillaPanelTarget(VanillaPanelKind.Character, 0, 0, itemGuid: -257494203, itemAmount: 37);
        Assert.False(item.HasEntity);
        Assert.Equal("kind=Character entity=0:0 net=none item=-257494203x37", item.ToString());
    }

    // ---------------------------------------------------------------- placement

    // Canvas units. The game's scaler matches height, so 1920x1080, 2560x1440 and 3840x2160 all
    // lay the HUD out on a 1080-unit-tall canvas; the pixel rects are the same rects scaled. The
    // scale factor here runs the rules in pixel space at each resolution, which must agree.
    public static IEnumerable<object[]> Scales() => new[]
    {
        new object[] { 1.0f },        // 1920x1080
        new object[] { 4f / 3f },     // 2560x1440
        new object[] { 2.0f },        // 3840x2160
    };

    private static UiRect R(float x, float y, float w, float h, float s) => new(x * s, y * s, w * s, h * s);

    [Theory]
    [MemberData(nameof(Scales))]
    public void A_left_column_sits_outside_the_frame_on_the_grid_top(float s)
    {
        // Canvas centered on its pivot: x in [-960, 960], y in [-540, 540].
        var bounds = R(-960, -540, 1920, 1080, s);
        var chest = R(-640, -400, 600, 820, s);          // container half, left of center
        var inventory = R(20, -400, 600, 820, s);        // inventory half, right of center
        var r = VanillaPanelPlacement.PlaceEdge(
            VanillaPanelAnchor.LeftEdge, chest, gridTop: 250 * s, width: 114 * s, height: 76 * s, bounds,
            new[] { new UiObstacle("InventorySubMenu", inventory) });

        Assert.Equal(VanillaFit.Fits, r.Fit);
        Assert.Equal(chest.X - VanillaPanelPlacement.EdgeGap - 114 * s, r.Rect.X, 3);
        Assert.Equal(250 * s, r.Rect.YMax, 3);
        Assert.False(r.Rect.Overlaps(chest));
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void A_right_column_that_would_cover_the_inventory_is_hidden_not_moved(float s)
    {
        var bounds = R(-960, -540, 1920, 1080, s);
        var chest = R(-640, -400, 600, 820, s);
        var inventory = R(-20, -400, 600, 820, s);       // 20 units from the chest's right edge
        var r = VanillaPanelPlacement.PlaceEdge(
            VanillaPanelAnchor.RightEdge, chest, null, 114 * s, 34 * s, bounds,
            new[] { new UiObstacle("InventorySubMenu", inventory) });

        Assert.Equal(VanillaFit.Blocked, r.Fit);
        Assert.Equal("overlap:InventorySubMenu", r.Reason);
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void A_column_that_would_leave_the_canvas_is_hidden(float s)
    {
        var bounds = R(-960, -540, 1920, 1080, s);
        var nearEdge = R(-900, -400, 600, 820, s);
        var r = VanillaPanelPlacement.PlaceEdge(VanillaPanelAnchor.LeftEdge, nearEdge, null, 114 * s, 34 * s, bounds, null);
        Assert.Equal(VanillaFit.Blocked, r.Fit);
        Assert.Equal("offscreen", r.Reason);
    }

    [Fact]
    public void A_wider_canvas_gives_room_a_16_by_9_canvas_does_not()
    {
        // 21:9 on the same 1080-unit height is 2520 wide: a panel hugging the 16:9 edge has room.
        var panel = new UiRect(-900, -400, 600, 820);
        var narrow = VanillaPanelPlacement.PlaceEdge(VanillaPanelAnchor.LeftEdge, panel, null, 114, 34, new UiRect(-960, -540, 1920, 1080), null);
        var wide = VanillaPanelPlacement.PlaceEdge(VanillaPanelAnchor.LeftEdge, panel, null, 114, 34, new UiRect(-1260, -540, 2520, 1080), null);
        Assert.Equal(VanillaFit.Blocked, narrow.Fit);
        Assert.Equal(VanillaFit.Fits, wide.Fit);
    }

    [Fact]
    public void A_grid_top_outside_the_frame_falls_back_to_the_frame_top()
    {
        var panel = new UiRect(0, 0, 600, 800);
        var r = VanillaPanelPlacement.PlaceEdge(VanillaPanelAnchor.RightEdge, panel, 2000f, 100, 30, default, null);
        Assert.Equal(800, r.Rect.YMax, 3);
    }

    [Fact]
    public void Column_size_stacks_buttons_then_rows_with_spacing()
    {
        var (w, h) = VanillaPanelPlacement.ColumnSize(2, 1, 114, 34, 24, 8);
        Assert.Equal(114, w);
        Assert.Equal(34 * 2 + 24 + 8 * 2, h);
        Assert.Equal((0f, 0f), VanillaPanelPlacement.ColumnSize(0, 0, 114, 34, 24, 8));
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void Beside_sort_fits_when_the_bar_shares_its_width_among_five_buttons(float s)
    {
        // Measured on a 1080p capture of the character inventory: four 114-wide buttons with 11
        // between them span 83..568 of a frame that runs 18..640. Five in the same span are 88.
        var frame = R(18, 0, 622, 960, s);
        float w = 88 * s, gap = 11 * s, x0 = 83 * s, y = 852 * s, h = 34 * s;
        var members = new List<UiObstacle>();
        for (int i = 0; i < 5; i++)
        {
            if (i == 2) continue;
            members.Add(new UiObstacle("b" + i, new UiRect(x0 + i * (w + gap), y, w, h)));
        }

        var ours = new UiRect(x0 + 2 * (w + gap), y, w, h);
        Assert.Equal(VanillaFit.Fits, VanillaPanelPlacement.CheckInline(ours, frame, members).Fit);
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void Beside_sort_is_hidden_when_the_bar_grows_past_the_frame(float s)
    {
        // A bar that does not shrink its children: five 114-wide buttons overflow the frame.
        var frame = R(18, 0, 622, 960, s);
        float w = 114 * s, gap = 11 * s, x0 = 13 * s, y = 852 * s, h = 34 * s;
        var members = new List<UiObstacle>();
        for (int i = 0; i < 5; i++)
        {
            if (i == 2) continue;
            members.Add(new UiObstacle("b" + i, new UiRect(x0 + i * (w + gap), y, w, h)));
        }

        var ours = new UiRect(x0 + 2 * (w + gap), y, w, h);
        var r = VanillaPanelPlacement.CheckInline(ours, frame, members);
        Assert.Equal(VanillaFit.Blocked, r.Fit);
        Assert.StartsWith("overflow:", r.Reason);
    }

    [Fact]
    public void An_inline_button_that_covers_a_vanilla_button_is_hidden()
    {
        var frame = new UiRect(0, 0, 600, 900);
        var ours = new UiRect(100, 10, 100, 30);
        var r = VanillaPanelPlacement.CheckInline(ours, frame, new[] { new UiObstacle("SortButton", new UiRect(150, 10, 100, 30)) });
        Assert.Equal("overlap:SortButton", r.Reason);
    }

    [Fact]
    public void The_tooltip_line_needs_a_laid_out_column()
    {
        Assert.Equal(VanillaFit.Fits, VanillaPanelPlacement.CheckTooltip(true).Fit);
        Assert.Equal("unlaid", VanillaPanelPlacement.CheckTooltip(false).Reason);
    }

    [Fact]
    public void Inline_width_is_n_donor_buttons_and_the_group_spacing()
    {
        Assert.Equal(0f, VanillaPanelPlacement.InlineWidth(0, 88, 11));
        Assert.Equal(88f, VanillaPanelPlacement.InlineWidth(1, 88, 11));
        Assert.Equal(88f * 2 + 11, VanillaPanelPlacement.InlineWidth(2, 88, 11));
    }

    // ---------------------------------------------------------------- missing sites

    [Fact]
    public void A_missing_site_is_reported_once_per_generation()
    {
        Assert.True(VanillaSiteHealth.ReportMissing(VanillaUiSite.ContainerPanel, "SelfSortButton"));
        Assert.False(VanillaSiteHealth.ReportMissing(VanillaUiSite.ContainerPanel, "SelfSortButton"));
        Assert.False(VanillaSiteHealth.ReportMissing(VanillaUiSite.ContainerPanel, "other"));
        Assert.True(VanillaSiteHealth.IsMissing(VanillaUiSite.ContainerPanel));
        Assert.Equal("missing=ContainerPanel:SelfSortButton", VanillaSiteHealth.Describe());
        Assert.Contains("Ui: site ContainerPanel not found (SelfSortButton)", VanillaSiteHealth.Line(VanillaUiSite.ContainerPanel, "SelfSortButton"));
        VanillaSiteHealth.Clear();
        Assert.False(VanillaSiteHealth.IsMissing(VanillaUiSite.ContainerPanel));
    }

    [Fact]
    public void A_missing_site_suppresses_the_registry_no_watch_line()
    {
        VanillaSiteHealth.ReportMissing(VanillaUiSite.StationPanel, "watch: TypeLoadException");
        _client.RefuseWatch = true;
        var h = UiApi.AttachToVanilla(VanillaUiSite.StationPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");
        Assert.DoesNotContain(Lines, l => l.Contains("has no frame watch"));
        h.Dispose();
    }

    // ---------------------------------------------------------------- the handle

    [Fact]
    public void Open_target_change_and_close_reach_the_plugin_with_the_entity()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");
        var seen = new List<VanillaPanelEvent>();
        h.OnPanel(seen.Add);

        _client.Fire(VanillaUiSite.ContainerPanel, VanillaSiteSample.Absent);
        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest(812)));
        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest(812)));
        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest(900, 41)));
        _client.Fire(VanillaUiSite.ContainerPanel, VanillaSiteSample.Absent);

        Assert.Equal(3, seen.Count);
        Assert.Equal(VanillaPanelChange.Opened, seen[0].Change);
        Assert.Equal(812, seen[0].Target.EntityIndex);
        Assert.Equal(VanillaPanelChange.TargetChanged, seen[1].Change);
        Assert.Equal(41, seen[1].Target.NetworkIdIndex);
        Assert.Equal(VanillaPanelChange.Closed, seen[2].Change);
        Assert.Equal(900, seen[2].Previous.EntityIndex);
        Assert.False(h.IsOpen);
        Assert.Single(Lines, l => l.StartsWith("Ui: panel ContainerPanel opened kind=Container entity=812:3"));
        Assert.Single(Lines, l => l.StartsWith("Ui: panel ContainerPanel closed kind=Container entity=900:3"));
        h.Dispose();
    }

    [Fact]
    public void Subscribing_while_open_delivers_the_open_straight_away()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");
        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest(5)));
        VanillaPanelEvent? got = null;
        h.OnPanel(e => got = e);
        Assert.NotNull(got);
        Assert.Equal(VanillaPanelChange.Opened, got!.Value.Change);
        Assert.Equal(5, got.Value.Target.EntityIndex);
        h.Dispose();
    }

    [Fact]
    public void A_handler_sets_rows_and_they_show_on_the_same_frame()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.InventoryTooltip, VanillaPanelAnchor.TooltipBody, _owner, "example.panels");
        h.OnPanel(e => h.SetRows(new[] { "In storage: " + e.Target.ItemAmount }));
        _client.Fire(VanillaUiSite.InventoryTooltip, Open(31, new VanillaPanelTarget(VanillaPanelKind.Character, 0, 0, itemGuid: 9, itemAmount: 37)));
        Assert.Equal(new[] { "In storage: 37" }, _client.RowTexts);
        h.Dispose();
    }

    [Fact]
    public void A_button_survives_close_and_reopen_on_the_same_click_slot()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");
        var b = (VanillaAttachButton)h.Button("Deposit");
        int clicks = 0;
        b.OnClick(() => clicks++);

        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
        Assert.Equal(1, _client.LiveButtons);
        Assert.Equal(b.WidgetId, _client.LastWiredId);

        UiSlotTable.BootstrapDispatch(b.WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Equal(1, clicks);

        _client.Fire(VanillaUiSite.ContainerPanel, VanillaSiteSample.Absent);
        Assert.Equal(0, _client.LiveButtons);
        _client.Fire(VanillaUiSite.ContainerPanel, Open(12, Chest()));
        Assert.Equal(1, _client.LiveButtons);
        Assert.Equal(2, _client.Wires);
        Assert.Equal(b.WidgetId, _client.LastWiredId);
        Assert.Equal("Deposit", _client.ButtonCaptions[0]);

        b.Text = "Deposit all";
        Assert.Equal("Deposit all", _client.ButtonCaptions[0]);

        UiSlotTable.BootstrapDispatch(b.WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Equal(2, clicks);

        h.Dispose();
        Assert.Contains(b.WidgetId, _client.Unwired);
        UiSlotTable.BootstrapDispatch(b.WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Equal(2, clicks);
        Assert.Equal(0, _client.LiveButtons);
    }

    [Fact]
    public void A_button_that_cannot_be_wired_is_dropped_not_shown_dead()
    {
        _client.RefuseWire = true;
        var h = UiApi.AttachToVanilla(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.BesideSort, _owner, "example.panels");
        h.Button("Stash");
        _client.Fire(VanillaUiSite.InventoryPanel, Open(21, new VanillaPanelTarget(VanillaPanelKind.Character, 0, 0)));
        Assert.Equal(0, _client.LiveButtons);
        Assert.Contains(Lines, l => l.Contains("button 'Stash' could not be wired; dropped"));
        h.Dispose();
    }

    [Fact]
    public void A_blocked_placement_hides_the_container_and_says_so_once()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.RightEdge, _owner, "example.panels");
        h.Button("Output");
        _client.Fit = new VanillaFitResult(VanillaFit.Blocked, "overlap:InventorySubMenu");
        for (int i = 0; i < 5; i++) _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));

        Assert.False(_client.ContainerVisible);
        Assert.Single(Lines, l => l.Contains("hidden reason=overlap:InventorySubMenu"));

        _client.Fit = new VanillaFitResult(VanillaFit.Fits, "ok");
        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
        Assert.True(_client.ContainerVisible);
        Assert.Single(Lines, l => l.Contains("shown again"));

        h.Visible = false;
        Assert.False(_client.ContainerVisible);
        h.Dispose();
    }

    [Fact]
    public void A_missing_donor_still_reports_the_open_but_builds_nothing()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.CastleHeartPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");
        h.Button("Castles");
        bool opened = false;
        h.OnPanel(e => opened |= e.Change == VanillaPanelChange.Opened);
        _client.Fire(VanillaUiSite.CastleHeartPanel, Open(41, new VanillaPanelTarget(VanillaPanelKind.CastleHeart, 7, 1), donor: false));
        Assert.True(opened);
        Assert.Equal(0, _client.ContainerBuilds);
        h.Dispose();
    }

    [Fact]
    public void The_tooltip_and_the_map_card_hand_back_disabled_buttons()
    {
        var tip = UiApi.AttachToVanilla(VanillaUiSite.InventoryTooltip, _owner, "example.panels");
        var map = UiApi.AttachToVanilla(VanillaUiSite.MapWarEventInfo, _owner, "example.panels");
        Assert.False(tip.Button("x").Enabled);
        Assert.False(map.Button("x").Enabled);
        Assert.Equal(0, tip.ButtonCount);
        tip.Dispose();
        map.Dispose();
    }

    [Fact]
    public void RemoveAll_takes_panel_attachments_their_buttons_and_the_watch()
    {
        var a = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");
        var b = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.BesideRename, _owner, "example.panels");
        var btn = (VanillaAttachButton)a.Button("Deposit");
        b.Button("Options");
        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
        Assert.Equal(2, _client.LiveButtons);
        Assert.Equal(1, _client.WatchCount);

        UiApi.RemoveAll(_owner);

        Assert.True(a.IsDisposed);
        Assert.True(b.IsDisposed);
        Assert.Equal(0, _client.LiveButtons);
        Assert.Equal(0, _client.LiveContainers);
        Assert.Equal(0, _client.WatchCount);
        Assert.Contains(btn.WidgetId, _client.Unwired);
    }

    [Fact]
    public void A_throwing_handler_does_not_stop_the_frame()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.StationPanel, _owner, "example.panels");
        h.Button("Inputs");
        h.OnPanel(_ => throw new InvalidOperationException("plugin bug"));
        _client.Fire(VanillaUiSite.StationPanel, Open(51, new VanillaPanelTarget(VanillaPanelKind.RefinementStation, 9, 1)));
        Assert.Equal(1, _client.LiveButtons);
        Assert.Contains(Lines, l => l.Contains("handler threw"));
        h.Dispose();
    }

    // ---------------------------------------------------------------- fake

    private sealed class FakePanelRuntime : IUiRuntime
    {
        private readonly Dictionary<VanillaUiSite, Action<VanillaSiteSample>> _watches = new();
        private readonly List<FakeWidget> _containers = new();
        private readonly List<FakeWidget> _buttons = new();

        public bool IsClient => true;

        public bool RefuseWatch { get; set; }

        public bool RefuseWire { get; set; }

        public VanillaFitResult Fit { get; set; } = new(VanillaFit.Fits, "ok");

        public int WatchCount => _watches.Count;

        public int ContainerBuilds { get; private set; }

        public int Wires { get; private set; }

        public ushort LastWiredId { get; private set; }

        public List<ushort> Unwired { get; } = new();

        public int LiveContainers => _containers.FindAll(c => c.Alive).Count;

        public int LiveButtons => _buttons.FindAll(c => c.Alive).Count;

        public bool ContainerVisible
        {
            get
            {
                foreach (var c in _containers) if (c.Alive) return c.Visible;
                return false;
            }
        }

        public List<string> ButtonCaptions => _buttons.FindAll(b => b.Alive).ConvertAll(b => b.Text);

        public List<string> RowTexts
        {
            get
            {
                var list = new List<string>();
                foreach (var c in _containers)
                    if (c.Alive) foreach (var r in c.Children) if (r.Alive && !r.IsButton) list.Add(r.Text);
                return list;
            }
        }

        public void Fire(VanillaUiSite site, VanillaSiteSample sample)
        {
            if (_watches.TryGetValue(site, out var cb)) cb(sample);
        }

        public IDisposable? WatchSite(VanillaUiSite site, Action<VanillaSiteSample> onFrame)
        {
            if (RefuseWatch) return null;
            _watches[site] = onFrame;
            return new Lease(() => _watches.Remove(site));
        }

        public IRuntimeWidget? BuildPanelContainer(VanillaUiSite site, VanillaPanelAnchor anchor, string containerName)
        {
            ContainerBuilds++;
            var c = new FakeWidget(containerName, false);
            _containers.Add(c);
            return c;
        }

        public IRuntimeWidget? ClonePanelButton(VanillaUiSite site, IRuntimeWidget container, string caption)
        {
            if (container is not FakeWidget c || !c.Alive) return null;
            var b = new FakeWidget(caption, true) { Click = new object() };
            c.Children.Add(b);
            _buttons.Add(b);
            return b;
        }

        public IRuntimeWidget? CloneDonorRow(VanillaUiSite site, IRuntimeWidget container, string text)
        {
            if (container is not FakeWidget c || !c.Alive) return null;
            var r = new FakeWidget(text, false);
            c.Children.Add(r);
            return r;
        }

        public VanillaFitResult FitAttachContainer(
            VanillaUiSite site, VanillaPanelAnchor anchor, IRuntimeWidget container, int rows, int buttons) => Fit;

        public bool TryWire(ushort widgetId, UiSlotKind kind, IRuntimeWidget widget)
        {
            if (RefuseWire || widget.ClickEvent == null) return false;
            Wires++;
            LastWiredId = widgetId;
            return true;
        }

        public void Unwire(ushort widgetId) => Unwired.Add(widgetId);

        public void UnwireMany(IReadOnlyList<ushort> ids)
        {
            foreach (var id in ids) Unwired.Add(id);
        }

        public IRuntimePanel? CreatePanel(string title, string rootName) => null;

        public IRuntimeWidget? AddButton(IRuntimePanel panel, string text) => null;

        public IRuntimeWidget? AddToggle(IRuntimePanel panel, string text, bool initial) => null;

        public IRuntimeWidget? AddLabel(IRuntimePanel panel, string text) => null;

        public IRuntimeWidget? AddSlider(IRuntimePanel panel, string text, float min, float max, float initial) => null;

        public IRuntimeWidget? AddInput(IRuntimePanel panel, string text, string initial) => null;

        public void PlacePanel(IRuntimePanel panel, PanelSpot spot) { }

        public void Show(IRuntimePanel panel) { }

        public void Hide(IRuntimePanel panel) { }

        public int DestroyRoot(string rootName) => 0;

        public int DestroyByPrefix(string prefix) => 0;

        public void SetCapture(bool capture) { }

        public ScreenRect PanelRect(IRuntimePanel panel) => ScreenRect.Unknown;

        public PointerSample ReadPointer() => PointerSample.Unreadable;

        private sealed class Lease : IDisposable
        {
            private Action? _off;

            internal Lease(Action off) => _off = off;

            public void Dispose()
            {
                var off = _off;
                _off = null;
                off?.Invoke();
            }
        }

        private sealed class FakeWidget : IRuntimeWidget, IReleaseVerdict
        {
            private bool _alive = true;

            internal FakeWidget(string text, bool button)
            {
                Text = text;
                IsButton = button;
            }

            internal bool IsButton { get; }

            internal object? Click { get; set; }

            internal List<FakeWidget> Children { get; } = new();

            public string Text { get; set; }

            public bool Visible { get; set; } = true;

            public bool BoolValue { get; set; }

            public float FloatValue { get; set; }

            public string StringValue { get; set; } = "";

            public bool Alive => _alive;

            public object? ClickEvent => _alive ? Click : null;

            public object? FloatEvent => null;

            public object? BoolEvent => null;

            public object? TextEvent => null;

            public object? PointerEnterEvent => null;

            public object? PointerExitEvent => null;

            public UiDestroyVerdict LastRelease { get; private set; } = UiDestroyVerdict.Failed;

            public void Release()
            {
                _alive = false;
                LastRelease = UiDestroyVerdict.Destroyed;
                foreach (var c in Children) c.Release();
            }
        }
    }
}
