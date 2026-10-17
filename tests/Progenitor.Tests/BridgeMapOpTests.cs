using System;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>The <c>map</c> bridge op: arguments, the decision for each menu state, and the select retry rule (<see cref="MapPlan"/>).</summary>
public class BridgeMapOpTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static MapPlan.State Play(
        bool inWorld = true, string hud = "None", bool menuUp = false, int? tab = null, bool workspace = false,
        string view = "None", string probe = "FullscreenMenuProbe", bool open = false) =>
        new(inWorld, hud, menuUp, tab, workspace, view, probe, open);

    [Theory]
    [InlineData("{\"action\":\"open\"}", "Open")]
    [InlineData("{\"action\":\" Close \"}", "Close")]
    [InlineData("{\"action\":\"STATUS\"}", "Status")]
    public void Actions_parse_case_and_space_insensitively(string json, string expected) =>
        Assert.Equal(expected, MapPlan.Parse(Args(json)).ToString());

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("{\"action\":1}")]
    [InlineData("{\"action\":\"toggle\"}")]
    [InlineData("{\"action\":\"\"}")]
    public void Missing_or_unknown_actions_are_refused(string json)
    {
        var e = Assert.Throws<ArgumentException>(() => MapPlan.Parse(Args(json)));
        Assert.Contains("map", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_op_refuses_outside_a_client() =>
        Assert.Equal(MapOps.ClientOnlyMessage,
            Assert.Throws<InvalidOperationException>(() => MapOps.Run(Args("{\"action\":\"status\"}"))).Message);

    [Fact]
    public void Bad_arguments_fail_before_the_client_check() =>
        Assert.Throws<ArgumentException>(() => MapOps.Run(Args("{\"action\":\"spin\"}")));

    [Fact]
    public void Share_parses_with_or_without_a_point()
    {
        Assert.Equal(MapPlan.Action.Share, MapPlan.Parse(Args("{\"action\":\"share\"}")));
        Assert.Equal((false, 0f, 0f), MapPlan.ParsePoint(Args("{\"action\":\"share\"}")));
        Assert.Equal((true, 640f, 360.5f), MapPlan.ParsePoint(Args("{\"action\":\"share\",\"x\":640,\"y\":360.5}")));
    }

    [Theory]
    [InlineData("{\"action\":\"share\",\"x\":1}")]
    [InlineData("{\"action\":\"share\",\"y\":1}")]
    [InlineData("{\"action\":\"share\",\"x\":\"1\",\"y\":2}")]
    public void Share_refuses_half_a_point(string json) =>
        Assert.Throws<ArgumentException>(() => MapPlan.ParsePoint(Args(json)));

    [Fact]
    public void Open_from_play_raises_the_menu() =>
        Assert.Equal(MapPlan.Step.Raise, MapPlan.DecideOpen(Play()).Step);

    [Fact]
    public void Open_on_another_tab_switches_to_the_map() =>
        Assert.Equal(MapPlan.Step.Switch, MapPlan.DecideOpen(Play(hud: "FullscreenMenu", menuUp: true, tab: 2)).Step);

    [Fact]
    public void Open_is_idempotent()
    {
        var (step, why) = MapPlan.DecideOpen(Play(hud: "FullscreenMenu", menuUp: true, tab: MapPlan.MapState, open: true));
        Assert.Equal(MapPlan.Step.NoOp, step);
        Assert.Equal(MapPlan.WhyAlreadyOpen, why);
    }

    [Fact]
    public void Open_refuses_on_the_title_screen()
    {
        var (step, why) = MapPlan.DecideOpen(Play(inWorld: false, hud: "", probe: MapPlan.NullProbeName));
        Assert.Equal(MapPlan.Step.Refuse, step);
        Assert.Equal(MapPlan.WhyTitle, why);
    }

    [Fact]
    public void Open_refuses_while_the_Mods_workspace_is_open()
    {
        var (step, why) = MapPlan.DecideOpen(Play(hud: "FullscreenMenu", menuUp: true, tab: 6, workspace: true));
        Assert.Equal(MapPlan.Step.Refuse, step);
        Assert.Equal(MapPlan.WhyWorkspace, why);
    }

    [Fact]
    public void Open_refuses_while_another_HUD_menu_is_open()
    {
        var (step, why) = MapPlan.DecideOpen(Play(hud: "CharacterMenu"));
        Assert.Equal(MapPlan.Step.Refuse, step);
        Assert.Contains("CharacterMenu", why, StringComparison.Ordinal);
        Assert.StartsWith("other-menu:", why, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_refuses_over_a_full_screen_game_view()
    {
        var (step, why) = MapPlan.DecideOpen(Play(view: "Loading"));
        Assert.Equal(MapPlan.Step.Refuse, step);
        Assert.StartsWith("game-view:", why, StringComparison.Ordinal);
    }

    [Fact]
    public void Open_refuses_without_Cores_menu_probe()
    {
        var (step, why) = MapPlan.DecideOpen(Play(probe: MapPlan.NullProbeName));
        Assert.Equal(MapPlan.Step.Refuse, step);
        Assert.Equal(MapPlan.WhyNoProbe, why);
    }

    [Fact]
    public void Close_with_the_map_shown_closes_the_menu() =>
        Assert.Equal(MapPlan.Step.Close, MapPlan.DecideClose(Play(hud: "FullscreenMenu", menuUp: true, tab: MapPlan.MapState, open: true)).Step);

    [Fact]
    public void Close_leaves_another_tab_alone()
    {
        var (step, why) = MapPlan.DecideClose(Play(hud: "FullscreenMenu", menuUp: true, tab: 5));
        Assert.Equal(MapPlan.Step.NoOp, step);
        Assert.Contains("Social", why, StringComparison.Ordinal);
    }

    [Fact]
    public void Close_is_idempotent() =>
        Assert.Equal((MapPlan.Step.NoOp, MapPlan.WhyAlreadyClosed), MapPlan.DecideClose(Play()));

    [Fact]
    public void Close_refuses_on_the_title_screen() =>
        Assert.Equal((MapPlan.Step.Refuse, MapPlan.WhyTitle), MapPlan.DecideClose(Play(inWorld: false)));

    [Fact]
    public void SetState_waits_for_the_menu_view_and_stops_on_the_map()
    {
        Assert.False(MapPlan.ShouldSetState(Play(), 0, 0));                                       // menu not up yet
        Assert.False(MapPlan.ShouldSetState(Play(hud: "FullscreenMenu", menuUp: true), 0, 0));  // no view yet
        Assert.True(MapPlan.ShouldSetState(Play(hud: "FullscreenMenu", menuUp: true, tab: 6), 0, 0));
        Assert.False(MapPlan.ShouldSetState(Play(hud: "FullscreenMenu", menuUp: true, tab: MapPlan.MapState), 0, 0));
        Assert.False(MapPlan.ShouldSetState(Play(hud: "FullscreenMenu", menuUp: true, tab: 6, workspace: true), 0, 0));
    }

    [Fact]
    public void SetState_retries_are_spaced_and_bounded()
    {
        var onOther = Play(hud: "FullscreenMenu", menuUp: true, tab: 1);
        Assert.False(MapPlan.ShouldSetState(onOther, 1, MapPlan.SetStateRetryFrames - 1));
        Assert.True(MapPlan.ShouldSetState(onOther, 1, MapPlan.SetStateRetryFrames));
        Assert.False(MapPlan.ShouldSetState(onOther, MapPlan.MaxSetStateCalls, 1_000));
    }

    [Fact]
    public void The_reply_names_the_tab()
    {
        var reply = Play(hud: "FullscreenMenu", menuUp: true, tab: MapPlan.MapState, open: true).ToReply();
        Assert.Equal("Map", reply["tab"]);
        Assert.Equal(true, reply["mapOpen"]);
    }
}
