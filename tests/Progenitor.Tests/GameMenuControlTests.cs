using System;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// GAMEMENU1 (user, 2026-10-03: the bridge could not close the game's own menu — "seems like an odd
/// gap. Fix it?"): <c>mods_ui menu {op: status|open|close, tab?}</c>. Game-free: argument parsing,
/// the plan each state takes, the state reply, the no-op and title-screen cases, and the
/// verification rules. The live paths (GoToMenu, the native tab's click, the shortcut route) are
/// source-pinned.
/// </summary>
public sealed class GameMenuControlTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    private static GameMenuPlan.State World(bool menuUp, int? tab = null, bool workspace = false, bool ready = true) =>
        new(true, menuUp ? "FullscreenMenu" : "None", menuUp, tab, workspace, ready);

    private static readonly GameMenuPlan.State Title = new(false, "", false, null, false);

    // ---- parsing ----------------------------------------------------------------------------

    [Fact]
    public void No_op_keeps_the_read_only_probe()
    {
        var request = ModsUiArgs.Parse(Json("{\"action\":\"menu\"}"));
        Assert.Equal("menu", request.Action);
        Assert.Null(request.Op);
        Assert.Null(request.Tab);
        Assert.False(ModsUiArgs.IsLightRead("menu", null));
    }

    [Theory]
    [InlineData("status", null, null)]
    [InlineData("STATUS", null, null)]
    [InlineData("close", null, null)]
    [InlineData("open", null, null)]
    [InlineData("open", "Spellbook", "spellbook")]
    [InlineData("open", "spell-book", "spellbook")]
    [InlineData("open", "SpellBook", "spellbook")]
    [InlineData("open", "vampire powers", "vampire-powers")]
    [InlineData("open", "V_Blood", "vblood")]
    [InlineData("open", "map", "map")]
    [InlineData("open", "MODS", "mods")]
    public void Ops_and_tabs_parse_to_canonical_names(string op, string? tab, string? canonical)
    {
        string json = "{\"action\":\"menu\",\"op\":\"" + op + "\"" + (tab != null ? ",\"tab\":\"" + tab + "\"" : "") + "}";
        var request = ModsUiArgs.Parse(Json(json));
        Assert.Equal(op.ToLowerInvariant(), request.Op);
        Assert.Equal(canonical, request.Tab);
    }

    [Theory]
    [InlineData("{\"action\":\"menu\",\"op\":\"toggle\"}", GameMenuPlan.OpUsage)]
    [InlineData("{\"action\":\"menu\",\"op\":\"open\",\"tab\":\"inventory\"}", GameMenuPlan.TabUsage)]
    [InlineData("{\"action\":\"menu\",\"op\":\"close\",\"tab\":\"map\"}", GameMenuPlan.TabNeedsOpen)]
    [InlineData("{\"action\":\"menu\",\"op\":\"status\",\"tab\":\"map\"}", GameMenuPlan.TabNeedsOpen)]
    [InlineData("{\"action\":\"menu\",\"tab\":\"map\"}", GameMenuPlan.TabNeedsOpen)]
    public void Bad_arguments_are_refused_with_the_usage(string json, string message)
    {
        var e = Assert.Throws<ArgumentException>(() => ModsUiArgs.Parse(Json(json)));
        Assert.StartsWith(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_is_a_light_read_and_open_and_close_are_not()
    {
        Assert.True(ModsUiArgs.IsLightRead("menu", GameMenuPlan.OpStatus));
        Assert.False(ModsUiArgs.IsLightRead("menu", GameMenuPlan.OpOpen));
        Assert.False(ModsUiArgs.IsLightRead("menu", GameMenuPlan.OpClose));
    }

    [Fact]
    public void Tab_states_follow_the_games_fullscreen_menu_enum()
    {
        // FullscreenMenuState (client metadata): Emotes 0 … System 6; the owned MODS tab is 7.
        string[] names = { "emotes", "vampire-powers", "spellbook", "map", "vblood", "social", "system", "mods" };
        for (int i = 0; i < names.Length; i++)
        {
            Assert.True(GameMenuPlan.TryParseTab(names[i], out var name, out int state));
            Assert.Equal(names[i], name);
            Assert.Equal(i, state);
            Assert.Equal(names[i], GameMenuPlan.TabName(i));
        }
        Assert.Equal("SpellBook", GameMenuPlan.NativeEnumName(2));
        Assert.Null(GameMenuPlan.NativeEnumName(GameMenuPlan.ModsState));
        Assert.Null(GameMenuPlan.TabName(9));
        Assert.False(GameMenuPlan.TryParseTab("", out _, out _));
    }

    // ---- state reply ------------------------------------------------------------------------

    [Fact]
    public void State_reply_names_the_menu_tab_and_workspace()
    {
        var reply = World(menuUp: true, tab: 2).ToReply();
        Assert.Equal(true, reply["inWorld"]);
        Assert.Equal("FullscreenMenu", reply["hudMenu"]);
        Assert.Equal(true, reply["menuOpen"]);
        Assert.Equal("spellbook", reply["tab"]);
        Assert.Equal(2, reply["tabState"]);
        Assert.Equal(false, reply["workspaceOpen"]);

        // No tab is reported while the menu is down, whatever was last read.
        var down = World(menuUp: false, tab: 2, workspace: true).ToReply();
        Assert.Equal(false, down["menuOpen"]);
        Assert.Null(down["tab"]);
        Assert.Equal(true, down["workspaceOpen"]);

        Assert.Equal(false, Title.ToReply()["inWorld"]);
    }

    // ---- plans and no-ops ---------------------------------------------------------------------

    [Fact]
    public void Close_is_a_no_op_without_a_menu_and_at_the_title()
    {
        Assert.Equal((GameMenuPlan.Step.NoOp, GameMenuPlan.WhyNoMenu), GameMenuPlan.DecideClose(World(menuUp: false)));
        // The standalone Mods window without the game menu is not this op's to close.
        Assert.Equal((GameMenuPlan.Step.NoOp, GameMenuPlan.WhyNoMenu), GameMenuPlan.DecideClose(World(menuUp: false, workspace: true)));
        Assert.Equal((GameMenuPlan.Step.NoOp, GameMenuPlan.WhyTitle), GameMenuPlan.DecideClose(Title));
    }

    [Fact]
    public void Close_dismisses_the_menu_and_closes_the_workspace_first_when_it_is_open()
    {
        Assert.Equal(GameMenuPlan.Step.Dismiss, GameMenuPlan.DecideClose(World(menuUp: true, tab: 3)).Step);
        Assert.Equal(GameMenuPlan.Step.CloseWorkspaceThenDismiss, GameMenuPlan.DecideClose(World(menuUp: true, tab: 7, workspace: true)).Step);
    }

    [Fact]
    public void Open_at_the_title_says_there_is_no_game_menu()
    {
        foreach (int? want in new int?[] { null, 2, GameMenuPlan.ModsState })
            Assert.Equal((GameMenuPlan.Step.NoWorld, GameMenuPlan.WhyTitle), GameMenuPlan.DecideOpen(Title, want));
    }

    [Fact]
    public void Open_plans_follow_the_menu_state()
    {
        // No tab: raise, or nothing when the menu is up.
        Assert.Equal(GameMenuPlan.Step.Raise, GameMenuPlan.DecideOpen(World(false), null).Step);
        Assert.Equal((GameMenuPlan.Step.NoOp, GameMenuPlan.WhyAlready), GameMenuPlan.DecideOpen(World(true, 2), null));
        // A native tab: raise then select, select, or already there.
        Assert.Equal(GameMenuPlan.Step.RaiseThenSelect, GameMenuPlan.DecideOpen(World(false), 3).Step);
        Assert.Equal(GameMenuPlan.Step.Select, GameMenuPlan.DecideOpen(World(true, 2), 3).Step);
        Assert.Equal(GameMenuPlan.Step.Select, GameMenuPlan.DecideOpen(World(true, 7, workspace: true), 3).Step);
        Assert.Equal((GameMenuPlan.Step.NoOp, GameMenuPlan.WhyAlready), GameMenuPlan.DecideOpen(World(true, 3), 3));
        // MODS: the shortcut route, already there, or refused over the standalone window.
        Assert.Equal(GameMenuPlan.Step.OpenMods, GameMenuPlan.DecideOpen(World(false), GameMenuPlan.ModsState).Step);
        Assert.Equal(GameMenuPlan.Step.OpenMods, GameMenuPlan.DecideOpen(World(true, 2), GameMenuPlan.ModsState).Step);
        Assert.Equal((GameMenuPlan.Step.NoOp, GameMenuPlan.WhyAlready), GameMenuPlan.DecideOpen(World(true, 7, workspace: true), GameMenuPlan.ModsState));
        Assert.Equal((GameMenuPlan.Step.Refuse, GameMenuPlan.WhyWorkspaceElsewhere), GameMenuPlan.DecideOpen(World(false, workspace: true), GameMenuPlan.ModsState));
    }

    // ---- verification -----------------------------------------------------------------------

    [Fact]
    public void Reached_is_gameplay_for_close_and_the_tab_for_open()
    {
        Assert.True(GameMenuPlan.Reached(GameMenuPlan.OpClose, null, World(false)));
        Assert.False(GameMenuPlan.Reached(GameMenuPlan.OpClose, null, World(false, workspace: true)));
        Assert.False(GameMenuPlan.Reached(GameMenuPlan.OpClose, null, World(true, 3)));

        Assert.True(GameMenuPlan.Reached(GameMenuPlan.OpOpen, null, World(true, 2)));
        Assert.False(GameMenuPlan.Reached(GameMenuPlan.OpOpen, null, World(true, 2, ready: false)));
        Assert.True(GameMenuPlan.Reached(GameMenuPlan.OpOpen, 3, World(true, 3)));
        Assert.False(GameMenuPlan.Reached(GameMenuPlan.OpOpen, 3, World(true, 2)));
        Assert.False(GameMenuPlan.Reached(GameMenuPlan.OpOpen, 3, World(true, 3, workspace: true)));
        // MODS needs the workspace open on the owned tab: the F10 bug (menu on Spellbook, workspace
        // closed by the menu's own pick) is not success.
        Assert.True(GameMenuPlan.Reached(GameMenuPlan.OpOpen, GameMenuPlan.ModsState, World(true, 7, workspace: true)));
        Assert.False(GameMenuPlan.Reached(GameMenuPlan.OpOpen, GameMenuPlan.ModsState, World(true, 2)));
        Assert.True(GameMenuPlan.StableFrames >= 4 && GameMenuPlan.VerifyFrames > GameMenuPlan.StableFrames * 4);
    }

    [Fact]
    public void A_native_tab_is_clicked_after_the_menu_settles_and_retried_a_bounded_number_of_times()
    {
        var onSpellbook = World(true, 2);
        Assert.False(GameMenuPlan.ShouldClick(3, onSpellbook, 0, GameMenuPlan.SelectDelayFrames - 1));
        Assert.True(GameMenuPlan.ShouldClick(3, onSpellbook, 0, GameMenuPlan.SelectDelayFrames));
        Assert.False(GameMenuPlan.ShouldClick(3, World(true, 2, ready: false), 0, 99));
        Assert.False(GameMenuPlan.ShouldClick(3, World(true, 3), 0, 99));
        Assert.True(GameMenuPlan.ShouldClick(3, onSpellbook, GameMenuPlan.MaxSelectAttempts - 1, 99));
        Assert.False(GameMenuPlan.ShouldClick(3, onSpellbook, GameMenuPlan.MaxSelectAttempts, 99));
        // MODS and "no tab" are never clicked: MODS goes through the shortcut route.
        Assert.False(GameMenuPlan.ShouldClick(GameMenuPlan.ModsState, onSpellbook, 0, 99));
        Assert.False(GameMenuPlan.ShouldClick(null, onSpellbook, 0, 99));
    }

    // ---- live paths (source pins) -----------------------------------------------------------

    [Fact]
    public void The_live_op_uses_the_games_own_menu_paths_and_verifies_before_replying()
    {
        string ops = SourcePin.ReadFile("src", "Progenitor.Dev", "Ops", "ModsUiOps.cs");
        string exec = SourcePin.MethodBody(ops, "private static object ExecuteGameMenu(");
        SourcePin.AssertUncommented(exec, "ModsMenu.Close(GameMenuPlan.ReasonClose);");
        SourcePin.AssertUncommented(exec, "detail[\"dismissed\"] = ModsMenu.TryDismissFullscreenMenu();");
        SourcePin.AssertUncommented(exec, "detail[\"raised\"] = ModsMenu.TryRequestFullscreenMenu();");
        SourcePin.AssertUncommented(exec, "ModsMenu.ToggleFrom(GameMenuPlan.ReasonOpen);");
        SourcePin.AssertUncommented(exec, "var (clickOk, clickDetail) = DoNativeTab(new ModsUiArgs.Request");
        SourcePin.AssertUncommented(exec, "detail[\"readback\"] = \"verified-after-frames\";");
        SourcePin.AssertUncommented(exec, "if (++stable >= GameMenuPlan.StableFrames)");
        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        Assert.Contains("public static bool TryDismissFullscreenMenu() => NativeModsEntry.TryRestoreFullscreenMenu();", menu, StringComparison.Ordinal);
        Assert.Contains("public static void ToggleFrom(string reason) => Toggle(", menu, StringComparison.Ordinal);
        // The Escape handling dismisses through the same call.
        string escape = SourcePin.MethodBody(menu, "internal static void NotifyHiddenByEscape()");
        Assert.Contains("NativeModsEntry.TryRestoreFullscreenMenu()", escape, StringComparison.Ordinal);
    }
}
