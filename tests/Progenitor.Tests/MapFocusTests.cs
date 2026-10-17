using System.Text.Json;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Map focus (<see cref="MapFocusLogic"/>): a chat location link's Set marker opens the world map centered on the
/// spot, when the game would open the map. Game-free.
/// </summary>
public class MapFocusTests
{
    private static MapFocusState Play(
        bool inWorld = true, bool probe = true, string hud = "None", bool menu = false, bool map = false,
        bool mapTab = false, bool workspace = false, string view = "None", bool chat = false) =>
        new(inWorld, probe, hud, menu, map, mapTab, workspace, view, chat);

    /// <summary>A world-to-map matrix: world X and Z scaled into the rect, centered on (cx, cz), Z on the rect's Y.</summary>
    private static float[] WorldToMap(float scale, float cx, float cz)
    {
        var m = new float[16];
        m[0] = scale;
        m[12] = -scale * cx;
        m[9] = scale;
        m[13] = -scale * cz;
        m[6] = 1f;
        m[15] = 1f;
        return m;
    }

    // ---- when the map opens ----------------------------------------------------------------

    [Fact]
    public void In_play_the_menu_is_raised() =>
        Assert.Equal(MapFocusStart.Raise, MapFocusLogic.DecideStart(Play()).Step);

    [Fact]
    public void On_another_menu_tab_the_map_tab_is_picked() =>
        Assert.Equal(MapFocusStart.SelectMap, MapFocusLogic.DecideStart(Play(hud: "FullscreenMenu", menu: true, mapTab: true)).Step);

    [Fact]
    public void With_the_map_shown_it_only_centers() =>
        Assert.Equal(MapFocusStart.Center, MapFocusLogic.DecideStart(Play(hud: "FullscreenMenu", menu: true, map: true)).Step);

    [Theory]
    [InlineData(false, true, "None", false, false, "None", false, MapFocusLogic.WhyNoWorld)]
    [InlineData(true, false, "None", false, false, "None", false, MapFocusLogic.WhyNoProbe)]
    [InlineData(true, true, "FullscreenMenu", true, true, "None", false, MapFocusLogic.WhyWorkspace)]
    [InlineData(true, true, "None", false, false, "None", true, MapFocusLogic.WhyChat)]
    public void It_refuses_where_the_game_would_not_open_the_map(bool inWorld, bool probe, string hud, bool menu,
        bool workspace, string view, bool chat, string why)
    {
        var (step, reason) = MapFocusLogic.DecideStart(Play(inWorld, probe, hud, menu, false, false, workspace, view, chat));
        Assert.Equal(MapFocusStart.Refuse, step);
        Assert.Equal(why, reason);
    }

    [Fact]
    public void Another_hud_menu_or_a_game_view_blocks_it_like_the_m_key()
    {
        var (step, why) = MapFocusLogic.DecideStart(Play(hud: "CharacterInfo"));
        Assert.Equal(MapFocusStart.Refuse, step);
        Assert.Contains("CharacterInfo", why);
        (step, why) = MapFocusLogic.DecideStart(Play(view: "Loading"));
        Assert.Equal(MapFocusStart.Refuse, step);
        Assert.Contains("Loading", why);
    }

    [Fact]
    public void Typing_in_chat_never_has_its_line_ended()
    {
        // Ending the chat edit would send the player's line, so the map stays closed while the chat input is active.
        Assert.Equal(MapFocusStart.Refuse, MapFocusLogic.DecideStart(Play(chat: true)).Step);
        Assert.Equal(MapFocusStart.Refuse, MapFocusLogic.DecideStart(Play(hud: "FullscreenMenu", menu: true, chat: true)).Step);
    }

    // ---- the opening frames ------------------------------------------------------------------

    [Fact]
    public void The_map_tab_is_picked_once_it_is_there_and_retried_while_the_menu_settles()
    {
        var waiting = Play(hud: "FullscreenMenu", menu: true, mapTab: false);
        Assert.Equal(MapFocusOpenStep.Wait, MapFocusLogic.OpenTick(waiting, false, 1, 0, 1, 0, true).Step);

        var onSpells = Play(hud: "FullscreenMenu", menu: true, mapTab: true);
        Assert.Equal(MapFocusOpenStep.SelectMap, MapFocusLogic.OpenTick(onSpells, false, 2, 0, 2, 0, true).Step);
        Assert.Equal(MapFocusOpenStep.Wait, MapFocusLogic.OpenTick(onSpells, false, 5, 1, MapFocusLogic.SelectRetryFrames - 1, 0, true).Step);
        Assert.Equal(MapFocusOpenStep.SelectMap, MapFocusLogic.OpenTick(onSpells, false, 15, 1, MapFocusLogic.SelectRetryFrames, 0, true).Step);
        Assert.Equal(MapFocusOpenStep.Wait, MapFocusLogic.OpenTick(onSpells, false, 40, MapFocusLogic.MaxSelects, 30, 0, true).Step);
    }

    [Fact]
    public void Centering_starts_after_the_map_has_held_with_its_data()
    {
        var shown = Play(hud: "FullscreenMenu", menu: true, map: true, mapTab: true);
        int stable = 0;
        for (int i = 0; i < MapFocusLogic.StableFrames - 1; i++) stable = MapFocusLogic.NextStable(stable, true, true);
        Assert.Equal(MapFocusOpenStep.Wait, MapFocusLogic.OpenTick(shown, true, 10, 1, 5, stable, true).Step);
        stable = MapFocusLogic.NextStable(stable, true, true);
        Assert.Equal(MapFocusOpenStep.Center, MapFocusLogic.OpenTick(shown, true, 11, 1, 6, stable, true).Step);
        Assert.Equal(0, MapFocusLogic.NextStable(stable, true, false));
    }

    [Fact]
    public void The_player_closing_the_menu_or_a_slow_open_ends_the_request()
    {
        Assert.Equal(MapFocusOpenStep.GiveUp, MapFocusLogic.OpenTick(Play(), false, 20, 1, 10, 0, sawMenu: true).Step);
        Assert.Equal(MapFocusOpenStep.Wait, MapFocusLogic.OpenTick(Play(), false, 2, 0, 2, 0, sawMenu: false).Step);
        var (step, why) = MapFocusLogic.OpenTick(Play(), false, MapFocusLogic.OpenFrames, 0, 0, 0, sawMenu: false);
        Assert.Equal(MapFocusOpenStep.GiveUp, step);
        Assert.Equal(MapFocusLogic.WhyNotOpened, why);
        Assert.Equal(MapFocusOpenStep.GiveUp, MapFocusLogic.OpenTick(Play(inWorld: false), false, 3, 0, 3, 0, true).Step);
    }

    // ---- the pan ---------------------------------------------------------------------------

    [Fact]
    public void The_pan_moves_the_offset_to_minus_the_spots_map_point()
    {
        // 0.5 map units per meter around (-1000, -2000): world (-900, -1800) is map point (50, 100).
        var m = WorldToMap(0.5f, -1000f, -2000f);
        Assert.True(MapFocusLogic.TryPan(m, -900f, -1800f, -500f, -500f, 1000f, 1000f, 20f, -30f,
            out float px, out float py, out float tx, out float ty, out bool onMap));
        Assert.True(onMap);
        Assert.Equal(-50f, tx, 3);
        Assert.Equal(-100f, ty, 3);
        Assert.Equal(-70f, px, 3);   // -50 - 20
        Assert.Equal(-70f, py, 3);   // -100 - (-30)
    }

    [Fact]
    public void A_spot_on_another_zones_map_is_not_panned_to()
    {
        var m = WorldToMap(0.5f, 0f, 0f);
        Assert.False(MapFocusLogic.TryPan(m, 5000f, 0f, -500f, -500f, 1000f, 1000f, 0f, 0f,
            out _, out _, out _, out _, out bool onMap));
        Assert.False(onMap);
    }

    [Fact]
    public void A_bad_matrix_or_spot_gives_no_pan()
    {
        Assert.False(MapFocusLogic.TryPan(new float[16], 0f, 0f, -1f, -1f, 2f, 2f, 0f, 0f, out _, out _, out _, out _, out _));
        Assert.False(MapFocusLogic.TryPan(WorldToMap(1f, 0f, 0f), float.NaN, 0f, -1f, -1f, 2f, 2f, 0f, 0f, out _, out _, out _, out _, out _));
        Assert.False(MapFocusLogic.TryPan(new float[3], 0f, 0f, -1f, -1f, 2f, 2f, 0f, 0f, out _, out _, out _, out _, out _));
    }

    // ---- the centering frames ----------------------------------------------------------------

    private static (MapFocusCenterStep Step, string Why) Center(bool open = true, bool ready = true, bool canPan = true,
        bool onMap = true, int frames = 1, int pans = 0, int sincePan = 0, (float, float) before = default,
        (float, float) offset = default, (float, float) target = default) =>
        MapFocusLogic.CenterTick(open, ready, canPan, onMap, frames, pans, sincePan, before, offset, target);

    [Fact]
    public void A_ready_map_is_panned_and_a_landed_pan_is_done()
    {
        Assert.Equal(MapFocusCenterStep.Pan, Center().Step);
        Assert.Equal(MapFocusCenterStep.Wait, Center(pans: 1, sincePan: MapFocusLogic.PanSettleFrames - 1).Step);
        var (step, why) = Center(pans: 1, sincePan: MapFocusLogic.PanSettleFrames, before: (0f, 0f), offset: (-50f, -100.5f), target: (-50f, -100f));
        Assert.Equal(MapFocusCenterStep.Done, step);
        Assert.Equal("centered", why);
    }

    [Fact]
    public void A_pan_stopped_by_the_maps_edge_is_done_and_a_dropped_one_is_asked_again()
    {
        var (step, why) = Center(pans: 1, sincePan: 5, before: (0f, 0f), offset: (-300f, 0f), target: (-900f, 0f));
        Assert.Equal(MapFocusCenterStep.Done, step);
        Assert.Equal("at the map's edge", why);

        Assert.Equal(MapFocusCenterStep.Pan, Center(pans: 1, sincePan: 5, before: (10f, 10f), offset: (10f, 10f), target: (-50f, 0f)).Step);
        var (last, reason) = Center(pans: MapFocusLogic.MaxPans, sincePan: 5, before: (10f, 10f), offset: (10f, 10f), target: (-50f, 0f));
        Assert.Equal(MapFocusCenterStep.GiveUp, last);
        Assert.Equal(MapFocusLogic.WhyNoPan, reason);
    }

    [Fact]
    public void Centering_waits_for_map_data_and_stops_when_the_map_closes_or_the_spot_is_elsewhere()
    {
        Assert.Equal(MapFocusCenterStep.Wait, Center(ready: false, canPan: false, frames: 10).Step);
        Assert.Equal((MapFocusCenterStep.GiveUp, MapFocusLogic.WhyNoData), Center(ready: false, canPan: false, frames: MapFocusLogic.ReadyFrames));
        Assert.Equal((MapFocusCenterStep.GiveUp, MapFocusLogic.WhyOffMap), Center(canPan: false, onMap: false));
        Assert.Equal((MapFocusCenterStep.GiveUp, MapFocusLogic.WhyMapClosed), Center(open: false, pans: 1, sincePan: 5));
    }

    // ---- the bridge op ---------------------------------------------------------------------

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void The_bridge_focus_takes_a_world_spot()
    {
        Assert.Equal(MapPlan.Action.Focus, MapPlan.Parse(Args("{\"action\":\"focus\",\"x\":1,\"z\":2}")));
        Assert.Equal((-1804f, -1697.5f), MapPlan.ParseWorld(Args("{\"action\":\"focus\",\"x\":-1804,\"z\":-1697.5}")));
        Assert.Throws<System.ArgumentException>(() => MapPlan.ParseWorld(Args("{\"action\":\"focus\",\"x\":1}")));
        Assert.Throws<System.ArgumentException>(() => MapPlan.ParseWorld(Args("{\"action\":\"focus\",\"x\":\"1\",\"z\":2}")));
    }
}
