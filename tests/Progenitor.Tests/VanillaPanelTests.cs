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
                     VanillaUiSite.CastleHeartPanel, VanillaUiSite.InventoryTooltip, VanillaUiSite.RecipeTooltip,
                 })
        {
            Assert.True(VanillaUiSites.IsDefined(site));
            Assert.True(VanillaUiSites.IsPanel(site));
        }

        Assert.False(VanillaUiSites.IsPanel(VanillaUiSite.MapWarEventInfo));
        Assert.False(VanillaUiSites.IsDefined((VanillaUiSite)8));
    }

    [Theory]
    [InlineData(VanillaUiSite.MapWarEventInfo)]
    [InlineData(VanillaUiSite.ContainerPanel)]
    [InlineData(VanillaUiSite.StationPanel)]
    [InlineData(VanillaUiSite.InventoryPanel)]
    [InlineData(VanillaUiSite.CastleHeartPanel)]
    [InlineData(VanillaUiSite.InventoryTooltip)]
    [InlineData(VanillaUiSite.RecipeTooltip)]
    public void Every_site_watch_runs_on_frames_its_mapper_skips(VanillaUiSite site)
    {
        // Every driving mapper (the map card's included) updates only while its queries match. A
        // watch with the default OnlyWhenSystemRuns = true skips exactly the frames on which a
        // window closes, so the close and the detach are lost. Both resolvers install with this.
        var options = VanillaUiSites.WatchOptions(site);
        Assert.False(options.OnlyWhenSystemRuns);
        Assert.Equal(1, options.EveryNFrames);
        Assert.NotSame(options, VanillaUiSites.WatchOptions(site));
    }

    [Fact]
    public void Both_resolvers_install_their_watches_with_the_site_watch_options()
    {
        // Source pin on the Unity half, which this project does not compile: no After<...> in
        // either resolver may be installed without the site's watch options.
        foreach (var file in new[] { "MapWarEventSiteResolver.cs", "VanillaPanelSiteResolver.cs" })
        {
            string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", file);
            int afters = System.Text.RegularExpressions.Regex.Matches(src, @"SystemHooks\.After<\w+>\(").Count;
            int withOptions = System.Text.RegularExpressions.Regex.Matches(src, @"VanillaUiSites\.WatchOptions\(site\)").Count;
            Assert.True(afters > 0, file);
            Assert.Equal(afters, withOptions);
        }
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
    [InlineData(VanillaUiSite.InventoryTooltip, VanillaPanelAnchor.TooltipRequirements, false, VanillaPanelAnchor.Default)]
    [InlineData(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.Default, true, VanillaPanelAnchor.TooltipBody)]
    [InlineData(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.TooltipRequirements, true, VanillaPanelAnchor.TooltipRequirements)]
    [InlineData(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.LeftEdge, false, VanillaPanelAnchor.Default)]
    [InlineData(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.TooltipRequirementLine, true, VanillaPanelAnchor.TooltipRequirementLine)]
    [InlineData(VanillaUiSite.InventoryTooltip, VanillaPanelAnchor.TooltipRequirementLine, false, VanillaPanelAnchor.Default)]
    [InlineData(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.TooltipRequirementLine, false, VanillaPanelAnchor.Default)]
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
    public void Only_a_resolver_failure_makes_a_site_absent_a_missing_piece_is_logged_once()
    {
        // A missing donor or anchor group (degraded): one line per site and piece, site still live.
        Assert.True(VanillaSiteHealth.LogOnce(VanillaUiSite.ContainerPanel, "anchor rename"));
        Assert.False(VanillaSiteHealth.LogOnce(VanillaUiSite.ContainerPanel, "anchor rename"));
        Assert.True(VanillaSiteHealth.LogOnce(VanillaUiSite.ContainerPanel, "body text"));
        Assert.False(VanillaSiteHealth.IsAbsent(VanillaUiSite.ContainerPanel));
        Assert.Equal("", VanillaSiteHealth.Describe());

        // The resolver threw (a member the game removed): absent for the generation, one line.
        Assert.True(VanillaSiteHealth.MarkAbsent(VanillaUiSite.StationPanel, "MissingFieldException"));
        Assert.False(VanillaSiteHealth.MarkAbsent(VanillaUiSite.StationPanel, "MissingFieldException"));
        Assert.True(VanillaSiteHealth.IsAbsent(VanillaUiSite.StationPanel));
        Assert.False(VanillaSiteHealth.IsAbsent(VanillaUiSite.ContainerPanel));
        Assert.Equal("missing=StationPanel:MissingFieldException", VanillaSiteHealth.Describe());
        Assert.StartsWith("Ui: site StationPanel not found (x)", VanillaSiteHealth.Line(VanillaUiSite.StationPanel, "x"));

        VanillaSiteHealth.Clear();
        Assert.False(VanillaSiteHealth.IsAbsent(VanillaUiSite.StationPanel));
        Assert.True(VanillaSiteHealth.LogOnce(VanillaUiSite.ContainerPanel, "anchor rename"));
    }

    [Fact]
    public void An_absent_site_suppresses_the_registry_no_watch_line()
    {
        VanillaSiteHealth.MarkAbsent(VanillaUiSite.StationPanel, "watch: TypeLoadException");
        _client.RefuseWatch = true;
        var h = UiApi.AttachToVanilla(VanillaUiSite.StationPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");
        Assert.DoesNotContain(Lines, l => l.Contains("has no frame watch"));
        h.Dispose();
    }

    [Fact]
    public void A_missing_anchor_group_fails_only_that_handle_not_the_site_or_other_plugins()
    {
        // The rename group is gone after an update: the BesideRename handle gets no container and
        // says so once; the left column of the same site, and another plugin's, still build.
        _client.RefuseAnchors.Add(VanillaPanelAnchor.BesideRename);
        var rename = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.BesideRename, _owner, "example.panels");
        var left = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");
        var other = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.RightEdge, new object(), "example.other");
        rename.Button("Options");
        left.Button("Deposit");
        other.Button("Output");

        for (int i = 0; i < 3; i++) _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));

        Assert.False(rename.IsAttached);
        Assert.True(left.IsAttached);
        Assert.True(other.IsAttached);
        Assert.True(rename.IsOpen);
        Assert.Single(Lines, l => l.Contains("could not build its container") && l.Contains("example.panels"));
        Assert.False(VanillaSiteHealth.IsAbsent(VanillaUiSite.ContainerPanel));
        rename.Dispose();
        left.Dispose();
        other.Dispose();
    }

    [Fact]
    public void An_unmeasurable_fit_never_hides_and_is_asked_again_next_frame()
    {
        // Review finding 2: an inline container measured before the group laid it out.
        var h = UiApi.AttachToVanilla(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.BesideSort, _owner, "example.panels");
        h.Button("Stash");
        _client.Fit = new VanillaFitResult(VanillaFit.Unknown, "pending");
        _client.Fire(VanillaUiSite.InventoryPanel, Open(21, Character()));
        Assert.True(_client.ContainerVisible);
        int calls = _client.FitCalls;

        _client.Fit = new VanillaFitResult(VanillaFit.Fits, "ok");
        _client.Fire(VanillaUiSite.InventoryPanel, Open(21, Character()));
        Assert.Equal(calls + 1, _client.FitCalls);
        Assert.True(_client.ContainerVisible);
        h.Dispose();
    }

    [Fact]
    public void An_inline_refusal_is_final_for_its_container_and_asked_again_on_reopen()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.BesideSort, _owner, "example.panels");
        h.Button("Stash");
        _client.Fit = new VanillaFitResult(VanillaFit.Blocked, "overflow:DropAllButton");
        _client.Fire(VanillaUiSite.InventoryPanel, Open(21, Character()));
        int calls = _client.FitCalls;
        for (int i = 0; i < 100; i++) _client.Fire(VanillaUiSite.InventoryPanel, Open(21, Character()));
        Assert.Equal(calls, _client.FitCalls);
        Assert.False(_client.ContainerVisible);

        _client.Fit = new VanillaFitResult(VanillaFit.Fits, "ok");
        _client.Fire(VanillaUiSite.InventoryPanel, VanillaSiteSample.Absent);
        _client.Fire(VanillaUiSite.InventoryPanel, Open(22, Character()));
        Assert.True(_client.ContainerVisible);
        h.Dispose();
    }

    [Fact]
    public void A_kept_container_is_refit_on_change_and_otherwise_rarely()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");
        h.Button("Deposit");
        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
        int start = _client.FitCalls;

        for (int i = 0; i < 29; i++) _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
        Assert.Equal(start, _client.FitCalls);

        h.Row("status");
        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
        Assert.Equal(start + 1, _client.FitCalls);

        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest(900)));
        Assert.Equal(start + 2, _client.FitCalls);

        h.SetRows(new[] { "same count" });
        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest(900)));
        Assert.Equal(start + 2, _client.FitCalls);
        h.Dispose();
    }

    [Fact]
    public void Visible_false_is_kept_across_close_and_reopen()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");
        h.Button("Deposit");
        h.Visible = false;
        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
        Assert.False(_client.ContainerVisible);
        _client.Fire(VanillaUiSite.ContainerPanel, VanillaSiteSample.Absent);
        _client.Fire(VanillaUiSite.ContainerPanel, Open(12, Chest()));
        Assert.False(_client.ContainerVisible);
        h.Visible = true;
        Assert.True(_client.ContainerVisible);
        h.Dispose();
    }

    [Fact]
    public void A_later_OnClick_replaces_the_earlier_handler()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");
        var b = (VanillaAttachButton)h.Button("Deposit");
        int first = 0, second = 0;
        var lease = b.OnClick(() => first++);
        b.OnClick(() => second++);
        lease.Dispose();
        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
        UiSlotTable.BootstrapDispatch(b.WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Equal(0, first);
        Assert.Equal(1, second);
        h.Dispose();
    }

    private static VanillaPanelTarget Character() => new(VanillaPanelKind.Character, 0, 0);

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

    private static VanillaPanelTarget Recipe(int recipe, int product = 500, int amount = 1, VanillaPanelKind kind = VanillaPanelKind.Workstation, params VanillaRecipeRequirement[] needs) =>
        new(kind, kind == VanillaPanelKind.Character ? 0 : 812, kind == VanillaPanelKind.Character ? 0 : 3,
            kind == VanillaPanelKind.Character ? 0 : 40, kind == VanillaPanelKind.Character ? 0 : 1, kind == VanillaPanelKind.Character ? 0 : 1,
            recipe, kind == VanillaPanelKind.RefinementStation ? recipe : product, kind == VanillaPanelKind.RefinementStation ? 0 : amount,
            product, amount, needs);

    [Fact]
    public void Each_hovered_recipe_is_a_new_target_with_its_product_and_requirements()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.TooltipRequirements, _owner, "example.recipes");
        var seen = new List<VanillaPanelEvent>();
        h.OnPanel(seen.Add);

        var glass = new VanillaRecipeRequirement(301, 3);
        var plank = new VanillaRecipeRequirement(302, 8);
        _client.Fire(VanillaUiSite.RecipeTooltip, Open(61, Recipe(9001, 500, 1, VanillaPanelKind.Workstation, glass, plank)));
        _client.Fire(VanillaUiSite.RecipeTooltip, Open(61, Recipe(9001, 500, 1, VanillaPanelKind.Workstation, glass, plank)));
        _client.Fire(VanillaUiSite.RecipeTooltip, Open(61, Recipe(9002, 501, 2, VanillaPanelKind.Workstation, plank)));
        _client.Fire(VanillaUiSite.RecipeTooltip, VanillaSiteSample.Absent);

        Assert.Equal(new[] { VanillaPanelChange.Opened, VanillaPanelChange.TargetChanged, VanillaPanelChange.Closed }, seen.Select(e => e.Change).ToArray());
        var first = seen[0].Target;
        Assert.Equal(9001, first.RecipeGuid);
        Assert.Equal(new VanillaRecipeRequirement(500, 1), first.Product);
        Assert.Equal(new[] { glass, plank }, first.Requirements.ToArray());
        Assert.Equal(812, first.EntityIndex);
        Assert.True(first.HasNetworkId);
        Assert.Equal(9002, seen[1].Target.RecipeGuid);
        Assert.Equal(9001, seen[1].Previous.RecipeGuid);
        Assert.Equal(9002, seen[2].Previous.RecipeGuid);

        // Per-hover edges are the plugin's to log, as on the item tooltip; the attach line is said once.
        Assert.DoesNotContain(Lines, l => l.StartsWith("Ui: panel RecipeTooltip"));
        Assert.Single(Lines, l => l.StartsWith("Ui: attached RecipeTooltip anchor=requirements"));
        Assert.Equal(new[] { VanillaPanelAnchor.TooltipRequirements }, _client.BuiltAnchors.ToArray());
        h.Dispose();
    }

    [Fact]
    public void A_requirement_line_attachment_builds_under_its_own_line_and_keeps_its_rows_across_rewrites()
    {
        var glass = new VanillaRecipeRequirement(301, 3);
        var plank = new VanillaRecipeRequirement(302, 8);
        var line1 = UiApi.AttachToRequirementLine(1, _owner, "example.recipes");
        Assert.True(line1.Enabled);
        Assert.Equal(VanillaPanelAnchor.TooltipRequirementLine, line1.Anchor);
        Assert.Equal(1, line1.RequirementLine);
        Assert.EndsWith("RecipeTooltip-line1", line1.ContainerName);
        line1.OnPanel(e =>
        {
            if (e.Change == VanillaPanelChange.Closed) return;
            var needs = e.Target.Requirements;
            line1.SetRows(new VanillaRow[] { needs.Count > 1 ? "Network: 40 x" + needs[1].ItemGuid : "" });
        });

        // The game rewrites the tooltip every frame; the same hovered recipe is one target and the
        // rows stay one set (never dropped, never duplicated).
        for (int frame = 0; frame < 5; frame++)
            _client.Fire(VanillaUiSite.RecipeTooltip, Open(64, Recipe(9010, 500, 1, VanillaPanelKind.RefinementStation, glass, plank)));
        Assert.Equal(new[] { "Network: 40 x302" }, _client.RowTexts);
        Assert.Equal(new[] { 1 }, _client.BuiltLines.Distinct().ToArray());

        var described = ((VanillaAttachHandle)line1).Readback().Describe();
        Assert.Equal(1, described["requirementLine"]);
        Assert.Equal("TooltipRequirementLine", described["anchor"]);

        // Another recipe: rebuilt under the same line, one set of rows again.
        _client.Fire(VanillaUiSite.RecipeTooltip, Open(64, Recipe(9011, 500, 1, VanillaPanelKind.RefinementStation, plank, glass)));
        Assert.Equal(new[] { "Network: 40 x301" }, _client.RowTexts);
        line1.Dispose();
    }

    [Fact]
    public void A_requirement_line_outside_the_tooltip_is_a_disabled_handle()
    {
        var h = UiApi.AttachToRequirementLine(VanillaUiSites.MaxRequirementLines, _owner, "example.recipes");
        Assert.False(h.Enabled);
        Assert.Contains(Lines, l => l.Contains("requirement line 6 is outside 0..5"));
        h.Dispose();
        var neg = UiApi.AttachToRequirementLine(-1, _owner, "example.recipes");
        Assert.False(neg.Enabled);
        neg.Dispose();

        // The plain anchor overload is line 0; other anchors have no line.
        var zero = UiApi.AttachToVanilla(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.TooltipRequirementLine, _owner, "example.recipes");
        Assert.Equal(0, zero.RequirementLine);
        zero.Dispose();
        var block = UiApi.AttachToVanilla(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.TooltipRequirements, _owner, "example.recipes");
        Assert.Equal(-1, block.RequirementLine);
        block.Dispose();
    }

    [Fact]
    public void A_requirement_line_container_shows_only_with_its_game_line()
    {
        Assert.True(VanillaUiSites.LineShown(wanted: true, gameLineShown: true));
        Assert.False(VanillaUiSites.LineShown(wanted: true, gameLineShown: false));
        Assert.False(VanillaUiSites.LineShown(wanted: false, gameLineShown: true));
        Assert.True(VanillaUiSites.IsTooltipAnchor(VanillaPanelAnchor.TooltipRequirementLine));
        Assert.Equal("line", VanillaUiSites.AnchorKey(VanillaPanelAnchor.TooltipRequirementLine));
        Assert.Equal("line3", VanillaUiSites.LineKey(3));
        Assert.True(VanillaUiSites.IsRequirementLine(0));
        Assert.True(VanillaUiSites.IsRequirementLine(5));
        Assert.False(VanillaUiSites.IsRequirementLine(6));
    }

    [Fact]
    public void The_crafting_tab_target_has_no_station()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.RecipeTooltip, _owner, "example.recipes");
        Assert.Equal(VanillaPanelAnchor.TooltipBody, h.Anchor);
        _client.Fire(VanillaUiSite.RecipeTooltip, Open(62, Recipe(9003, kind: VanillaPanelKind.Character)));
        Assert.Equal(VanillaPanelKind.Character, h.Target.Kind);
        Assert.False(h.Target.HasEntity);
        Assert.False(h.Target.HasNetworkId);
        Assert.Empty(h.Target.Requirements);
        h.Dispose();
    }

    [Fact]
    public void Toned_rows_carry_their_color_and_a_plain_SetRows_resets_it()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.TooltipBody, _owner, "example.recipes");
        h.OnPanel(e =>
        {
            if (e.Change == VanillaPanelChange.Closed) return;
            h.SetRows(new VanillaRow[] { "Network: 12 Glass", new("Not enough Planks", LabelTone.Warn) });
        });
        _client.Fire(VanillaUiSite.RecipeTooltip, Open(63, Recipe(9004)));

        Assert.Equal(new[] { "Network: 12 Glass", "Not enough Planks" }, _client.RowTexts);
        Assert.Equal(new[] { LabelTone.Normal, LabelTone.Warn }, _client.RowTones);
        int writes = _client.ToneWrites;

        // The same tones again write nothing; a plain string list is all Normal.
        h.SetRows(new VanillaRow[] { "Network: 12 Glass", new("Not enough Planks", LabelTone.Warn) });
        Assert.Equal(writes, _client.ToneWrites);
        h.SetRows(new[] { "a", "b", "c" });
        Assert.Equal(new[] { LabelTone.Normal, LabelTone.Normal, LabelTone.Normal }, _client.RowTones);

        var described = (string[])((VanillaAttachHandle)h).Readback().Describe()["rowTones"]!;
        Assert.Equal(new[] { "normal", "normal", "normal" }, described);

        h.ClearRows();
        Assert.Empty(_client.RowTexts);
        Assert.Equal(0, h.RowCount);
        h.Dispose();
    }

    [Fact]
    public void The_recipe_tooltip_hands_back_disabled_buttons()
    {
        var h = UiApi.AttachToVanilla(VanillaUiSite.RecipeTooltip, VanillaPanelAnchor.TooltipRequirements, _owner, "example.recipes");
        Assert.True(h.Enabled);
        Assert.False(h.Button("x").Enabled);
        h.Dispose();
    }

    [Fact]
    public void Recipe_targets_compare_by_value_and_print_their_recipe()
    {
        var a = Recipe(9001, 500, 1, VanillaPanelKind.RefinementStation, new VanillaRecipeRequirement(301, 3));
        var b = Recipe(9001, 500, 1, VanillaPanelKind.RefinementStation, new VanillaRecipeRequirement(301, 3));
        var c = Recipe(9001, 500, 1, VanillaPanelKind.RefinementStation, new VanillaRecipeRequirement(301, 4));
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, Recipe(9001, 500, 1, VanillaPanelKind.RefinementStation));
        Assert.Equal("kind=RefinementStation entity=812:3 net=40:1 recipe=9001 item=9001x0 makes=500x1 needs=301x3", a.ToString());
        Assert.Equal(new VanillaRecipeRequirement(500, 1), a.Product);
        Assert.Equal(9001, a.ItemGuid);
        Assert.Equal("kind=Workstation entity=812:3 net=40:1 recipe=9001 item=500x1 needs=301x3", Recipe(9001, 500, 1, VanillaPanelKind.Workstation, new VanillaRecipeRequirement(301, 3)).ToString());

        // The public constructor copies the list; the old constructor carries no recipe.
        var list = new List<VanillaRecipeRequirement> { new(301, 3) };
        var d = new VanillaPanelTarget(VanillaPanelKind.Workstation, 1, 1, 0, 0, 0, 9001, 500, 1, 500, 1, list);
        list[0] = new VanillaRecipeRequirement(999, 9);
        Assert.Equal(301, d.Requirements[0].ItemGuid);
        var item = new VanillaPanelTarget(VanillaPanelKind.Character, 0, 0, itemGuid: 9, itemAmount: 2);
        Assert.Equal(0, item.RecipeGuid);
        Assert.Empty(item.Requirements);
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

        // The room appears (the other window closed): an edge column is refit on the periodic
        // pass, not every frame, and comes back.
        _client.Fit = new VanillaFitResult(VanillaFit.Fits, "ok");
        for (int i = 0; i < 30; i++) _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
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

    // ---------------------------------------------------------------- ui_read readback

    private static Dictionary<string, object?> ReadbackOf(IVanillaAttachHandle h) =>
        VanillaAttachRegistry.Snapshot().Single(x => ReferenceEquals(x, h)).Readback().Describe();

    [Fact]
    public void The_readback_says_why_nothing_is_drawn_and_what_is_once_attached()
    {
        // ui_read panels lists every attachment under "attached": a bridge test reads whether the
        // window is open, whether a container exists, why not, and each button's state.
        var left = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");
        left.Button("Deposit");
        left.Row("status");

        var closed = ReadbackOf(left);
        Assert.Equal("ContainerPanel", closed["site"]);
        Assert.Equal("LeftEdge", closed["anchor"]);
        Assert.Equal(false, closed["open"]);
        Assert.Equal(false, closed["attached"]);
        Assert.Equal("site", closed["withheld"]);

        // The window opens without a button donor: the plan withholds the container and says why.
        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest(), donor: false));
        var noDonor = ReadbackOf(left);
        Assert.Equal(true, noDonor["open"]);
        Assert.Equal(false, noDonor["attached"]);
        Assert.Equal("donor", noDonor["withheld"]);

        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
        var open = ReadbackOf(left);
        Assert.Equal(true, open["attached"]);
        Assert.Equal(true, open["shown"]);
        Assert.Equal("", open["withheld"]);
        Assert.Equal("ok", open["fit"]);
        var button = Assert.IsType<Dictionary<string, object?>>(Assert.Single((List<object?>)open["buttons"]!));
        Assert.Equal("Deposit", button["caption"]);
        Assert.Equal(true, button["built"]);
        Assert.Equal(true, button["shown"]);
        Assert.Equal(new List<string> { "status" }, open["rows"]);
        left.Dispose();
    }

    [Fact]
    public void The_readback_names_a_missing_anchor_group_as_container()
    {
        _client.RefuseAnchors.Add(VanillaPanelAnchor.BesideRename);
        var rename = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.BesideRename, _owner, "example.panels");
        rename.Button("Locate");
        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
        var d = ReadbackOf(rename);
        Assert.Equal(true, d["open"]);
        Assert.Equal(false, d["attached"]);
        Assert.Equal("container", d["withheld"]);
        rename.Dispose();
    }

    [Fact]
    public void Buttons_declared_before_any_open_are_built_on_open_and_rebuilt_on_reopen()
    {
        // A plugin declares its handles and buttons at load, long before a chest is opened.
        var left = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");
        var rename = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.BesideRename, _owner, "example.panels");
        left.Button("Deposit");
        rename.Button("Locate");
        Assert.Equal(0, _client.LiveButtons);

        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
        Assert.Equal(2, _client.LiveButtons);
        foreach (var h in new[] { left, rename })
        {
            var d = ReadbackOf(h);
            Assert.Equal(true, d["attached"]);
            var b = Assert.IsType<Dictionary<string, object?>>(Assert.Single((List<object?>)d["buttons"]!));
            Assert.Equal(true, b["built"]);
            Assert.Equal(true, b["shown"]);
        }

        _client.Fire(VanillaUiSite.ContainerPanel, VanillaSiteSample.Absent);
        Assert.Equal(0, _client.LiveButtons);
        Assert.Equal(false, ReadbackOf(left)["attached"]);

        // A reopen builds a new container and every declared button in it again, exactly once.
        _client.Fire(VanillaUiSite.ContainerPanel, Open(12, Chest()));
        _client.Fire(VanillaUiSite.ContainerPanel, Open(12, Chest()));
        Assert.Equal(2, _client.LiveButtons);
        var reopened = (Dictionary<string, object?>)((List<object?>)ReadbackOf(rename)["buttons"]!)[0]!;
        Assert.Equal(true, reopened["built"]);
        left.Dispose();
        rename.Dispose();
    }

    [Fact]
    public void A_button_whose_clone_failed_on_the_build_is_asked_for_again_while_the_window_stays_open()
    {
        var left = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");
        left.Button("Deposit");
        _client.RefuseClones = 1;
        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
        Assert.Equal(0, _client.LiveButtons);
        var first = (Dictionary<string, object?>)((List<object?>)ReadbackOf(left)["buttons"]!)[0]!;
        Assert.Equal(false, first["built"]);

        for (int i = 0; i < 40; i++) _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
        Assert.Equal(1, _client.LiveButtons);
        left.Dispose();
    }

    [Fact]
    public void Every_panel_site_samples_on_all_five_window_mappers_and_a_frame_watchdog()
    {
        // Source pin on the Unity half (not compiled here). Live 1.1.13.0: the inventory site, on
        // InventoryMenuMapper alone, never sampled while a chest was open, and a window can close
        // together with the only mapper that would have seen it close.
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "VanillaPanelSiteResolver.cs");
        foreach (var mapper in new[] { "ContainerMenuMapper", "WorkstationMenuMapper", "RefinementstationMenuMapper", "CastleHeartMenuMapper", "InventoryMenuMapper" })
            Assert.Contains("SystemHooks.After<" + mapper + ">(frame, VanillaUiSites.WatchOptions(site))", src);
        Assert.Contains("Plugin.Frame += watchdog;", src);
        Assert.Contains("if (now == watch.Frame) return;", src);
    }

    [Fact]
    public void Closing_a_chest_and_its_bag_in_one_frame_raises_exactly_one_Closed_per_open()
    {
        // Escape closes the chest and the bag beside it together. Each site's watch reads closed on
        // the next frame it samples (a mapper hook or the frame watchdog): one Closed per Opened,
        // never a second one on the frames after, and a reopen starts the count again.
        var chest = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.LeftEdge, _owner, "example.panels");
        var bag = UiApi.AttachToVanilla(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.BesideSort, _owner, "example.panels");
        var events = new List<string>();
        chest.OnPanel(e => events.Add("chest " + e.Change));
        bag.OnPanel(e => events.Add("bag " + e.Change));
        var bagTarget = new VanillaPanelTarget(VanillaPanelKind.Container, 812, 3, 40, 1, 0);

        for (int round = 0; round < 2; round++)
        {
            for (int i = 0; i < 3; i++)
            {
                _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
                _client.Fire(VanillaUiSite.InventoryPanel, Open(21, bagTarget));
            }

            // The same frame: both windows gone.
            _client.Fire(VanillaUiSite.ContainerPanel, VanillaSiteSample.Absent);
            _client.Fire(VanillaUiSite.InventoryPanel, VanillaSiteSample.Absent);
            for (int i = 0; i < 5; i++)
            {
                _client.Fire(VanillaUiSite.ContainerPanel, VanillaSiteSample.Absent);
                _client.Fire(VanillaUiSite.InventoryPanel, VanillaSiteSample.Absent);
            }

            Assert.False(chest.IsOpen);
            Assert.False(bag.IsOpen);
            Assert.Equal(0, _client.LiveContainers);
        }

        Assert.Equal(new[]
        {
            "chest Opened", "bag Opened", "chest Closed", "bag Closed",
            "chest Opened", "bag Opened", "chest Closed", "bag Closed",
        }, events);
        Assert.Equal(2, Lines.Count(l => l.StartsWith("Ui: panel ContainerPanel closed", StringComparison.Ordinal)));
        chest.Dispose();
        bag.Dispose();
    }

    [Fact]
    public void A_BesideSort_handle_with_three_buttons_is_one_row_and_is_hidden_whole_when_it_does_not_fit()
    {
        // Three captions on one handle are one row in one container: the fit is for the row, so
        // either all three show or none does (with the reason), never a partial row.
        _client.Fit = new VanillaFitResult(VanillaFit.Blocked, "overflow:mod/overlap:ParentContainerInventory");
        var bag = UiApi.AttachToVanilla(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.BesideSort, _owner, "example.panels");
        bag.Button("Stash");
        bag.Button("Inventory");
        bag.Button("Find");
        _client.Fire(VanillaUiSite.InventoryPanel, Open(21, new VanillaPanelTarget(VanillaPanelKind.Character, 0, 0)));
        var d = ReadbackOf(bag);
        Assert.Equal(true, d["attached"]);
        Assert.Equal(false, d["shown"]);
        Assert.Equal("blocked", d["fit"]);
        Assert.Equal("overflow:mod/overlap:ParentContainerInventory", d["fitReason"]);
        Assert.Equal(3, ((List<object?>)d["buttons"]!).Count);
        Assert.Single(Lines, l => l.Contains("anchor=sort hidden reason=overflow:mod/overlap:ParentContainerInventory"));
        bag.Dispose();
    }

    [Fact]
    public void A_placed_container_refused_on_open_is_shown_once_a_later_refit_fits()
    {
        // A joined container's Blocked is final (hiding it reflows the group); a placed one's carries
        // Retry and is asked again on the 30-frame refit, so the slot measured mid open motion does
        // not hide the button for the whole session.
        _client.Fit = new VanillaFitResult(VanillaFit.Blocked, "overflow:mod", retry: true);
        var bag = UiApi.AttachToVanilla(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.BesideSort, _owner, "example.panels");
        bag.Button("Storage");
        var character = new VanillaPanelTarget(VanillaPanelKind.Character, 0, 0);
        _client.Fire(VanillaUiSite.InventoryPanel, Open(21, character));
        Assert.Equal(false, ReadbackOf(bag)["shown"]);

        _client.Fit = new VanillaFitResult(VanillaFit.Fits, "ok", retry: true);
        for (int i = 0; i < 31; i++) _client.Fire(VanillaUiSite.InventoryPanel, Open(21, character));
        var d = ReadbackOf(bag);
        Assert.Equal(true, d["shown"]);
        Assert.Equal("ok", d["fit"]);
        Assert.Contains(Lines, l => l.Contains("anchor=sort shown again"));
        bag.Dispose();
    }

    [Fact]
    public void A_joined_container_refused_after_layout_stays_hidden_until_rebuilt()
    {
        _client.Fit = new VanillaFitResult(VanillaFit.Blocked, "overflow:DropAllButton");
        var rename = UiApi.AttachToVanilla(VanillaUiSite.ContainerPanel, VanillaPanelAnchor.BesideRename, _owner, "example.panels");
        rename.Button("Locate");
        _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
        int calls = _client.FitCalls;
        _client.Fit = new VanillaFitResult(VanillaFit.Fits, "ok");
        for (int i = 0; i < 61; i++) _client.Fire(VanillaUiSite.ContainerPanel, Open(11, Chest()));
        Assert.Equal(calls, _client.FitCalls);
        Assert.Equal(false, ReadbackOf(rename)["shown"]);
        rename.Dispose();
    }

    [Fact]
    public void A_placed_container_is_never_shown_while_its_window_slides_in()
    {
        // The runtime says Settling while the window moves: the container stays hidden, nothing is
        // logged as a refusal, and it is measured again on the next frame until the window is at rest.
        _client.FitQueue.Enqueue(VanillaFitResult.Moving);
        _client.FitQueue.Enqueue(VanillaFitResult.Moving);
        _client.FitQueue.Enqueue(VanillaFitResult.Moving);
        _client.Fit = new VanillaFitResult(VanillaFit.Fits, "ok", retry: true);
        var bag = UiApi.AttachToVanilla(VanillaUiSite.InventoryPanel, VanillaPanelAnchor.BesideSort, _owner, "example.panels");
        bag.Button("Storage", VanillaButtonIcon.Item(123));
        var character = new VanillaPanelTarget(VanillaPanelKind.Character, 0, 0);

        var seen = new List<bool>();
        for (int i = 0; i < 4; i++)
        {
            _client.Fire(VanillaUiSite.InventoryPanel, Open(21, character));
            seen.Add(_client.LiveContainerVisible);
        }

        Assert.Equal(new[] { false, false, false, true }, seen);
        Assert.Equal(4, _client.FitCalls);
        Assert.DoesNotContain(Lines, l => l.Contains("hidden reason"));
        Assert.Equal("ok", ReadbackOf(bag)["fit"]);
        bag.Dispose();
    }

    [Fact]
    public void An_icon_buttons_tooltip_stays_up_without_a_mod_panel_and_says_why_once_when_it_cannot()
    {
        // Source pins on the Unity half (not compiled here). Live c24906668: hovering the icon in the
        // character window showed no tooltip and logged nothing. The kit tooltip's frame watch hid every
        // box when no mod panel was shown, which inside a game window is always: up one frame, gone the next.
        string widgets = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "UnityUiRuntime.Widgets.cs");
        Assert.Contains("(!_tipNeedsPanel || AnyPanelShown())", widgets);
        Assert.Contains("internal void ShowGameControlTooltip(", widgets);
        string resolver = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "VanillaPanelSiteResolver.cs");
        Assert.Contains("_runtime.ShowGameControlTooltip(anchor, caption", resolver);
        Assert.Contains("icon tooltip not shown: ", resolver);
        Assert.Contains("[\"hoverEvents\"] = tipped.HoverEvents", resolver);
    }

    [Fact]
    public void A_button_icon_is_an_item_or_a_drawn_mark_and_the_chest_by_default()
    {
        Assert.Equal("mark:chest", VanillaButtonIcon.Default.ToString());
        Assert.Equal("item:1234", VanillaButtonIcon.Item(1234).ToString());
        Assert.Equal("mark:check", VanillaButtonIcon.Drawn("check").ToString());
        Assert.Equal(VanillaButtonIcon.Default, VanillaButtonIcon.Drawn(" "));
        Assert.True(UiDrawnMark.TryParse("chest", out var chest) && chest.IsIcon);
        Assert.Equal("chest", UiDrawnMark.ChestMark.Name);
        // The chest is drawn: its outline and its lid line have coverage, the middle of the lid does not.
        Assert.True(UiDrawnMarkArt.Alpha(UiDrawnMark.ChestMark, 24, (int)(0.58f * 48)) > 0.5f);
        Assert.True(UiDrawnMarkArt.Alpha(UiDrawnMark.ChestMark, 12, 34) < 0.1f);
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

        public int FitCalls { get; private set; }

        /// <summary>Verdicts handed out before <see cref="Fit"/>, one per fit.</summary>
        public Queue<VanillaFitResult> FitQueue { get; } = new();

        /// <summary>The containers, for a test that checks one was never visible.</summary>
        public bool LiveContainerVisible => _containers.Exists(c => c.Alive && c.Visible);

        public HashSet<VanillaPanelAnchor> RefuseAnchors { get; } = new();

        /// <summary>The next this-many button clones fail (a donor the clone gate refuses).</summary>
        public int RefuseClones { get; set; }

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

        public List<int> BuiltLines { get; } = new();

        public IRuntimeWidget? BuildPanelContainer(VanillaUiSite site, VanillaPanelAnchor anchor, string containerName, int line)
        {
            BuiltLines.Add(line);
            return BuildPanelContainer(site, anchor, containerName);
        }

        public IRuntimeWidget? BuildPanelContainer(VanillaUiSite site, VanillaPanelAnchor anchor, string containerName)
        {
            if (RefuseAnchors.Contains(anchor)) return null;
            ContainerBuilds++;
            BuiltAnchors.Add(anchor);
            var c = new FakeWidget(containerName, false);
            _containers.Add(c);
            return c;
        }

        public IRuntimeWidget? ClonePanelButton(VanillaUiSite site, IRuntimeWidget container, string caption)
        {
            if (container is not FakeWidget c || !c.Alive) return null;
            if (RefuseClones > 0)
            {
                RefuseClones--;
                return null;
            }

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

        public int ToneWrites { get; private set; }

        public void StyleAttachRow(IRuntimeWidget row, LabelTone tone)
        {
            if (row is not FakeWidget w || w.Tone == tone) return;
            w.Tone = tone;
            ToneWrites++;
        }

        public List<LabelTone> RowTones
        {
            get
            {
                var list = new List<LabelTone>();
                foreach (var c in _containers)
                    if (c.Alive) foreach (var r in c.Children) if (r.Alive && !r.IsButton) list.Add(r.Tone);
                return list;
            }
        }

        /// <summary>The anchors containers were built for, in build order.</summary>
        public List<VanillaPanelAnchor> BuiltAnchors { get; } = new();

        public VanillaFitResult FitAttachContainer(
            VanillaUiSite site, VanillaPanelAnchor anchor, IRuntimeWidget container, int rows, int buttons)
        {
            FitCalls++;
            if (FitQueue.Count > 0) return FitQueue.Dequeue();
            return Fit;
        }

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

            internal LabelTone Tone { get; set; }

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
