using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// TABS2 part A: the Mods shortcut in a world opens through the game's FullscreenMenu on the MODS
/// tab, closes back to gameplay when it raised that menu, and falls back to the standalone window
/// (logged once) when the menu or tab never arrives. Game-free: the host is a fake, the workspace
/// is the real <see cref="ModsMenu"/> over the fake UI runtime.
/// </summary>
[Collection("PluginLogger")]
public sealed class ModsShortcutRouteTests : IDisposable
{
    private const string F10 = "shortcut:F10";
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly FakeHost _host = new();

    public ModsShortcutRouteTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
        ModsShortcutRoute.Install(_host);
    }

    public void Dispose()
    {
        ModsShortcutRoute.Reset();
        UiLayoutBatch.ResetForTests();
        ModsMenu.Close();
        ModsMenu.ResetTabBindingsForTests();
        ModsWorkspaceFrame.ResetForTests();
        ModsMenu.UninstallLifecycleWatch();
        Plugin.Logger = null;
        UiApi.ResetForTests();
    }

    private sealed class FakeHost : IModsShortcutHost
    {
        public bool InWorld { get; set; } = true;
        public bool MenuUp { get; set; }
        public bool OtherMenuUp { get; set; }
        public bool PrimaryPointerHeld { get; set; }
        public bool? MenuReadyOverride { get; set; }
        public bool MenuReady => MenuReadyOverride ?? MenuUp;
        public int Repaints;
        public void RepaintUnselectedOuterTabs() => Repaints++;
        public bool DismissTakesEffect { get; set; } = true;
        public bool NativeRouteActive { get; set; }
        public bool ImitationTabAttached { get; set; }
        public bool NativeRoutePending { get; set; }
        public bool WorkspaceOpen => ModsMenu.IsOpen;
        public bool NativeReturnPending { get; set; }
        public string LastCloseReason => ModsMenu.LastCloseReason;
        public bool RequestAccepted { get; set; } = true;
        public bool NativeOpenAccepted { get; set; } = true;
        public int Requests;
        public int Dismissals;
        public readonly List<string> Opens = new();
        public readonly List<string> Lines = new();

        public bool RequestMenu()
        {
            Requests++;
            return RequestAccepted;
        }

        public bool DismissMenu()
        {
            Dismissals++;
            bool was = MenuUp;
            if (DismissTakesEffect) MenuUp = false;
            return was;
        }

        public bool OpenNative(out string why)
        {
            if (!NativeOpenAccepted)
            {
                why = "select-refused";
                return false;
            }
            why = "";
            Opens.Add("native");
            ModsMenu.Open(ModsMenu.ReasonUserButton);
            return true;
        }

        public bool OpenImitation(string reason, out string why)
        {
            why = "";
            Opens.Add("imitation:" + reason);
            ModsMenu.Open(reason);
            return true;
        }

        public void OpenStandalone(string reason)
        {
            Opens.Add("standalone:" + reason);
            ModsMenu.Open(reason);
        }

        public void Log(string line) => Lines.Add(line);
    }

    private static void Ticks(int n)
    {
        for (int i = 0; i < n; i++) ModsShortcutRoute.Tick();
    }

    // ---- pure decisions -----------------------------------------------------------------------

    [Theory]
    [InlineData(true, false, true, (int)ShortcutPress.Close)]
    [InlineData(true, true, false, (int)ShortcutPress.Close)]
    [InlineData(false, true, true, (int)ShortcutPress.CancelPending)]
    [InlineData(false, false, false, (int)ShortcutPress.Standalone)]
    [InlineData(false, false, true, (int)ShortcutPress.ThroughMenu)]
    public void Press_decision_table(bool open, bool pending, bool inWorld, int expected) =>
        Assert.Equal((ShortcutPress)expected, ModsShortcutRoute.DecidePress(open, pending, inWorld));

    [Theory]
    [InlineData(true, true, true, true, 50, (int)ShortcutPendingStep.Adopt)]
    [InlineData(false, true, true, true, 50, (int)ShortcutPendingStep.OpenNative)]
    [InlineData(false, true, false, true, 50, (int)ShortcutPendingStep.OpenImitation)]
    [InlineData(false, false, true, true, 50, (int)ShortcutPendingStep.Wait)]
    [InlineData(false, true, false, false, 50, (int)ShortcutPendingStep.Wait)]
    [InlineData(false, false, false, false, 0, (int)ShortcutPendingStep.Fallback)]
    [InlineData(false, true, false, false, 0, (int)ShortcutPendingStep.Fallback)]
    public void Pending_decision_table(
        bool open, bool menuUp, bool native, bool imitation, int frames, int expected) =>
        Assert.Equal((ShortcutPendingStep)expected, ModsShortcutRoute.DecidePending(open, menuUp, native, imitation, frames));

    [Theory]
    [InlineData(true, true, F10, false, 0, (int)ShortcutAfterClose.Keep)]
    [InlineData(false, false, F10, false, 0, (int)ShortcutAfterClose.Forget)]
    [InlineData(false, true, "native-tab", false, 0, (int)ShortcutAfterClose.Forget)]
    [InlineData(false, true, "lifecycle:world-ended", false, 0, (int)ShortcutAfterClose.Forget)]
    [InlineData(false, true, F10, false, 0, (int)ShortcutAfterClose.Dismiss)]
    [InlineData(false, true, "user-button", false, 0, (int)ShortcutAfterClose.Dismiss)]
    [InlineData(false, true, "", false, 0, (int)ShortcutAfterClose.Dismiss)]
    [InlineData(false, true, F10, true, 0, (int)ShortcutAfterClose.Wait)]
    [InlineData(false, true, F10, true, ModsShortcutRoute.DismissWaitFrames, (int)ShortcutAfterClose.Dismiss)]
    public void After_close_decision_table(
        bool open, bool menuUp, string reason, bool returnPending, int waited, int expected) =>
        Assert.Equal((ShortcutAfterClose)expected, ModsShortcutRoute.DecideAfterClose(open, menuUp, reason, returnPending, waited));

    [Fact]
    public void Standalone_fallback_suppresses_the_in_world_menu_request()
    {
        Assert.True(NativeModsEntry.ShouldRequestFullscreenMenu(true, false, standaloneFallback: false));
        Assert.False(NativeModsEntry.ShouldRequestFullscreenMenu(true, false, standaloneFallback: true));
        Assert.False(NativeModsEntry.ShouldRequestFullscreenMenu(true, true, standaloneFallback: false));
        Assert.False(NativeModsEntry.ShouldRequestFullscreenMenu(false, false, standaloneFallback: false));
    }

    // ---- routing ------------------------------------------------------------------------------

    [Fact]
    public void Title_screen_press_opens_the_standalone_window_at_once()
    {
        _host.InWorld = false;
        ModsMenu.Toggle(F10);
        Assert.True(ModsMenu.IsOpen);
        Assert.False(ModsShortcutRoute.Pending);
        Assert.Equal(0, _host.Requests);
        Assert.Empty(_host.Opens);
        Assert.False(ModsShortcutRoute.SuppressMenuRequest);
    }

    [Fact]
    public void No_host_keeps_the_old_toggle()
    {
        ModsShortcutRoute.Reset();
        ModsMenu.Toggle(F10);
        Assert.True(ModsMenu.IsOpen);
        ModsMenu.Toggle(F10);
        Assert.False(ModsMenu.IsOpen);
    }

    [Fact]
    public void Gameplay_press_raises_the_menu_and_opens_on_the_native_mods_tab()
    {
        ModsMenu.Toggle(F10);
        Assert.Equal(1, _host.Requests);
        Assert.False(ModsMenu.IsOpen);
        Assert.True(ModsShortcutRoute.Pending);
        Assert.True(ModsMenu.ShortcutOpenPending);

        Ticks(5);
        Assert.False(ModsMenu.IsOpen);
        Assert.Equal(1, _host.Requests);

        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        ModsShortcutRoute.Tick();
        Assert.Equal(new[] { "native" }, _host.Opens);
        Assert.True(ModsMenu.IsOpen);
        Assert.False(ModsShortcutRoute.Pending);
        Assert.True(ModsShortcutRoute.RaisedMenu);
        Assert.Contains(_host.Lines, l => l == ModsShortcutRoute.LogPrefix + "opened through menu route=native raisedMenu=true reason=" + F10);
    }

    // ---- F10SETTLE: the raised menu's own tab selection does not close Mods ----------------------

    /// <summary>
    /// The native route's answer to "the mapper moved off MODS while the workspace is open" during
    /// one tick, given the route's settle state now (what NativeModsEntry.NativeRouteTick decides).
    /// </summary>
    private static NativeRouteTickAction NativeChangeNow() =>
        NativeRouteSelection.DecideTick(workspaceOpen: true, ownedIsCurrent: false, selectedByUs: true, returnFailed: false,
            settling: ModsShortcutRoute.SettleReselectAllowed);

    private void RaisedNativeOpen()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        ModsShortcutRoute.Tick();
        Assert.True(ModsMenu.IsOpen);
        Assert.True(ModsShortcutRoute.RaisedMenu);
    }

    [Theory]
    [InlineData(true, 0, 0, false, true)]
    [InlineData(true, ModsShortcutRoute.MenuSettleFrames, 1, false, true)]
    [InlineData(true, ModsShortcutRoute.MenuSettleFrames + 1, 0, false, false)]
    [InlineData(true, 3, ModsShortcutRoute.MaxSettleReselects, false, false)]
    [InlineData(true, 3, 0, true, false)]
    [InlineData(false, 3, 0, false, false)]
    [InlineData(true, -1, 0, false, false)]
    public void Settle_reselect_decision_table(bool raised, int frames, int reselects, bool pointer, bool expected) =>
        Assert.Equal(expected, ModsShortcutRoute.DecideSettleReselect(raised, frames, reselects, pointer));

    [Fact]
    public void Tick_decision_reselects_while_settling_and_closes_otherwise()
    {
        Assert.Equal(NativeRouteTickAction.ReselectOwned, NativeRouteSelection.DecideTick(true, false, true, false, settling: true));
        Assert.Equal(NativeRouteTickAction.CloseWorkspace, NativeRouteSelection.DecideTick(true, false, true, false, settling: false));
        // Settling changes nothing else: MODS current stays steady, a closed workspace still returns.
        Assert.Equal(NativeRouteTickAction.None, NativeRouteSelection.DecideTick(true, true, true, false, settling: true));
        Assert.Equal(NativeRouteTickAction.SelectOwned, NativeRouteSelection.DecideTick(true, false, false, false, settling: true));
        Assert.Equal(NativeRouteTickAction.ReturnSelection, NativeRouteSelection.DecideTick(false, false, true, false, settling: true));
    }

    /// <summary>
    /// The live sequence (client log 2026-10-03): F10 raises the menu, the workspace opens on MODS,
    /// and a few frames later the menu selects its remembered tab (Spellbook). That change is the
    /// menu's, so the native route selects MODS again and the workspace stays open.
    /// </summary>
    [Fact]
    public void Raised_menu_selecting_its_remembered_tab_while_settling_keeps_mods_open()
    {
        RaisedNativeOpen();
        Ticks(3);
        Assert.Equal(NativeRouteTickAction.ReselectOwned, NativeChangeNow());
        ModsShortcutRoute.NoteSettleReselect(5);
        Assert.True(ModsMenu.IsOpen);
        Assert.Contains(_host.Lines, l => l.StartsWith(ModsShortcutRoute.LogPrefix + "menu settle re-selected MODS over native state=5 frame=", StringComparison.Ordinal));
        // The repaint of the unselected outer tabs runs again after the re-selection.
        int repaints = _host.Repaints;
        Ticks(ModsShortcutRoute.RepaintLastFrame + 1);
        Assert.True(_host.Repaints >= repaints + 2, "repaints " + _host.Repaints);
        // Shortcut close still returns to gameplay afterwards.
        ModsMenu.Toggle(F10);
        ModsShortcutRoute.Tick();
        Assert.Equal(1, _host.Dismissals);
    }

    /// <summary>After the settle window, a vanilla tab change is the player's: Mods closes as before.</summary>
    [Fact]
    public void A_vanilla_tab_change_after_the_settle_window_closes_mods()
    {
        RaisedNativeOpen();
        Ticks(ModsShortcutRoute.MenuSettleFrames + 1);
        Assert.False(ModsShortcutRoute.SettleReselectAllowed);
        Assert.Equal(NativeRouteTickAction.CloseWorkspace, NativeChangeNow());
    }

    /// <summary>A mouse press inside the window marks the next native change as the player's click.</summary>
    [Fact]
    public void A_click_on_a_vanilla_tab_inside_the_window_still_closes_mods()
    {
        RaisedNativeOpen();
        Ticks(2);
        _host.PrimaryPointerHeld = true;
        ModsShortcutRoute.Tick();
        _host.PrimaryPointerHeld = false;
        ModsShortcutRoute.Tick();
        Assert.Equal(NativeRouteTickAction.CloseWorkspace, NativeChangeNow());
    }

    /// <summary>The window re-selects a bounded number of times, then a further change closes.</summary>
    [Fact]
    public void The_settle_window_reselects_at_most_its_budget()
    {
        RaisedNativeOpen();
        for (int i = 0; i < ModsShortcutRoute.MaxSettleReselects; i++)
        {
            Assert.Equal(NativeRouteTickAction.ReselectOwned, NativeChangeNow());
            ModsShortcutRoute.NoteSettleReselect(5);
            ModsShortcutRoute.Tick();
        }
        Assert.Equal(NativeRouteTickAction.CloseWorkspace, NativeChangeNow());
    }

    /// <summary>The menu was already up (the second press): nothing settles, a tab change closes.</summary>
    [Fact]
    public void A_press_inside_an_open_menu_has_no_settle_window()
    {
        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        ModsMenu.Toggle(F10);
        Assert.True(ModsMenu.IsOpen);
        Assert.False(ModsShortcutRoute.RaisedMenu);
        Assert.False(ModsShortcutRoute.SettleReselectAllowed);
        Assert.Equal(NativeRouteTickAction.CloseWorkspace, NativeChangeNow());
    }

    /// <summary>F10 or Escape inside the window still closes; the window ends with the workspace.</summary>
    [Fact]
    public void Toggle_close_and_escape_inside_the_window_close_and_end_it()
    {
        RaisedNativeOpen();
        ModsMenu.Toggle(F10);
        Assert.False(ModsMenu.IsOpen);
        ModsShortcutRoute.Tick();
        Assert.Equal(1, _host.Dismissals);
        Assert.False(ModsShortcutRoute.SettleReselectAllowed);

        _host.MenuUp = false;
        _host.Dismissals = 0;
        RaisedNativeOpen();
        ModsMenu.Close("escape");
        _host.MenuUp = false;
        ModsShortcutRoute.Tick();
        Assert.False(ModsShortcutRoute.SettleReselectAllowed);
    }

    [Fact]
    public void The_title_screen_press_has_no_settle_window()
    {
        _host.InWorld = false;
        ModsMenu.Toggle(F10);
        Assert.True(ModsMenu.IsOpen);
        Assert.False(ModsShortcutRoute.RaisedMenu);
        Assert.False(ModsShortcutRoute.SettleReselectAllowed);
    }

    [Fact]
    public void The_native_route_tick_reselects_through_the_settle_rule()
    {
        string entry = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "NativeModsEntry.cs");
        string tick = SourcePin.MethodBody(entry, "private static void NativeRouteTick()");
        SourcePin.AssertUncommented(tick, "bool settling = open && !ownedCurrent && ModsShortcutRoute.SettleReselectAllowed;");
        SourcePin.AssertUncommented(tick, "case NativeRouteTickAction.ReselectOwned:");
        SourcePin.AssertUncommented(tick, "ModsShortcutRoute.NoteSettleReselect(menuState);");
        SourcePin.AssertUncommented(tick, "ModsMenu.Close(NativeRouteCloseReason);");
        Assert.Contains("public bool PrimaryPointerHeld => UnityUiRuntime.PrimaryPointerHeld();", entry, StringComparison.Ordinal);
    }

    [Fact]
    public void Tab_attached_before_the_menu_is_up_does_not_open()
    {
        _host.NativeRouteActive = true;
        ModsMenu.Toggle(F10);
        Ticks(3);
        Assert.Empty(_host.Opens);
        Assert.False(ModsMenu.IsOpen);
    }

    [Fact]
    public void Imitation_route_opens_through_the_owned_button_with_the_shortcut_reason()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.ImitationTabAttached = true;
        ModsShortcutRoute.Tick();
        Assert.Equal(new[] { "imitation:" + F10 }, _host.Opens);
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal("imitation", ModsShortcutRoute.LastRoute);
    }

    [Fact]
    public void Shortcut_close_after_a_raised_open_returns_to_gameplay()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        ModsShortcutRoute.Tick();
        Assert.True(ModsMenu.IsOpen);

        ModsMenu.Toggle(F10);
        Assert.False(ModsMenu.IsOpen);
        ModsShortcutRoute.Tick();
        Assert.Equal(1, _host.Dismissals);
        Assert.False(_host.MenuUp);
        Assert.False(ModsShortcutRoute.RaisedMenu);
        Assert.Contains(_host.Lines, l => l.StartsWith(ModsShortcutRoute.LogPrefix + "closed to gameplay", StringComparison.Ordinal));
    }

    [Fact]
    public void Close_mods_button_after_a_raised_open_returns_to_gameplay()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.ImitationTabAttached = true;
        ModsShortcutRoute.Tick();
        ModsMenu.Close(ModsMenu.ReasonUserButton);
        ModsShortcutRoute.Tick();
        Assert.Equal(1, _host.Dismissals);
    }

    [Fact]
    public void Press_while_already_in_the_menu_opens_at_once_and_closes_back_to_the_menu()
    {
        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        ModsMenu.Toggle(F10);
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(0, _host.Requests);
        Assert.False(ModsShortcutRoute.RaisedMenu);

        ModsMenu.Toggle(F10);
        Ticks(3);
        Assert.Equal(0, _host.Dismissals);
        Assert.True(_host.MenuUp);
    }

    [Fact]
    public void Native_tab_navigation_keeps_the_menu_the_player_chose()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        ModsShortcutRoute.Tick();
        ModsMenu.Close(ModsMenu.ReasonNativeTab);
        Ticks(3);
        Assert.Equal(0, _host.Dismissals);
        Assert.False(ModsShortcutRoute.RaisedMenu);
    }

    [Fact]
    public void Escape_that_already_dismissed_the_menu_is_forgotten_without_a_second_dismiss()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        ModsShortcutRoute.Tick();
        ModsMenu.Close("escape");
        _host.MenuUp = false;
        Ticks(2);
        Assert.Equal(0, _host.Dismissals);
        Assert.False(ModsShortcutRoute.RaisedMenu);
    }

    [Fact]
    public void Dismiss_waits_for_the_native_return_transition_then_is_bounded()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        ModsShortcutRoute.Tick();
        ModsMenu.Toggle(F10);
        _host.NativeReturnPending = true;
        Ticks(ModsShortcutRoute.DismissWaitFrames);
        Assert.Equal(0, _host.Dismissals);
        ModsShortcutRoute.Tick();
        Assert.Equal(1, _host.Dismissals);
    }

    [Fact]
    public void Dismiss_follows_the_native_return_as_soon_as_it_lands()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        ModsShortcutRoute.Tick();
        ModsMenu.Toggle(F10);
        _host.NativeReturnPending = true;
        Ticks(2);
        Assert.Equal(0, _host.Dismissals);
        _host.NativeReturnPending = false;
        ModsShortcutRoute.Tick();
        Assert.Equal(1, _host.Dismissals);
    }

    [Fact]
    public void Second_press_while_waiting_cancels_and_dismisses_the_requested_menu()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        ModsMenu.Toggle(F10);
        Assert.False(ModsShortcutRoute.Pending);
        Assert.False(ModsMenu.IsOpen);
        Assert.Equal(1, _host.Dismissals);
        _host.NativeRouteActive = true;
        Ticks(3);
        Assert.Empty(_host.Opens);
        Assert.Contains(_host.Lines, l => l == ModsShortcutRoute.LogPrefix + "open canceled why=pressed-again");
    }

    [Fact]
    public void Workspace_opened_by_a_mods_click_while_waiting_is_adopted()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        ModsMenu.Open(ModsMenu.ReasonUserButton);
        ModsShortcutRoute.Tick();
        Assert.False(ModsShortcutRoute.Pending);
        Assert.True(ModsShortcutRoute.RaisedMenu);
        Assert.Empty(_host.Opens);
        ModsMenu.Toggle(F10);
        ModsShortcutRoute.Tick();
        Assert.Equal(1, _host.Dismissals);
    }

    // ---- fallback -----------------------------------------------------------------------------

    [Fact]
    public void Menu_that_never_arrives_falls_back_to_the_standalone_window_logged_once()
    {
        ModsMenu.Toggle(F10);
        Ticks(ModsShortcutRoute.AttachFrames - 2);
        Assert.False(ModsMenu.IsOpen);
        ModsShortcutRoute.Tick();
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(new[] { "standalone:" + F10 }, _host.Opens);
        Assert.True(ModsShortcutRoute.SuppressMenuRequest);
        Assert.Equal(ModsShortcutRoute.WhyNoMenu, ModsShortcutRoute.LastFallback);
        // The route requested the menu, so it undoes that request (a no-op live while the menu is down).
        Assert.Equal(1, _host.Dismissals);
        Assert.False(ModsShortcutRoute.RaisedMenu);
        string line = ModsShortcutRoute.LogPrefix + "fell back to standalone window why=no-fullscreen-menu reason=" + F10;
        Assert.Single(_host.Lines, l => l == line);

        // Closing the standalone window clears the suppression; a second fallback is not logged again.
        ModsMenu.Toggle(F10);
        ModsShortcutRoute.Tick();
        Assert.False(ModsShortcutRoute.SuppressMenuRequest);
        ModsMenu.Toggle(F10);
        Ticks(ModsShortcutRoute.AttachFrames);
        Assert.True(ModsMenu.IsOpen);
        Assert.Single(_host.Lines, l => l.Contains("fell back", StringComparison.Ordinal));
    }

    [Fact]
    public void Menu_up_without_a_mods_tab_dismisses_it_and_falls_back()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        Ticks(ModsShortcutRoute.AttachFrames);
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(1, _host.Dismissals);
        Assert.Equal(ModsShortcutRoute.WhyTabNotAttached, ModsShortcutRoute.LastFallback);
        Assert.False(ModsShortcutRoute.RaisedMenu);
    }

    [Fact]
    public void Refused_menu_request_falls_back_at_once()
    {
        _host.RequestAccepted = false;
        ModsMenu.Toggle(F10);
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(ModsShortcutRoute.WhyMenuRefused, ModsShortcutRoute.LastFallback);
        Assert.Equal(0, _host.Dismissals);
    }

    [Fact]
    public void Refused_native_open_falls_back_with_the_reason()
    {
        _host.NativeOpenAccepted = false;
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        ModsShortcutRoute.Tick();
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(ModsShortcutRoute.WhyNativeRefused + "select-refused", ModsShortcutRoute.LastFallback);
        Assert.Equal(1, _host.Dismissals);
    }

    [Fact]
    public void Reset_and_forget_leave_no_session_behind()
    {
        ModsMenu.Toggle(F10);
        Assert.True(ModsShortcutRoute.Pending);
        ModsShortcutRoute.ForgetSession();
        Assert.False(ModsShortcutRoute.Pending);
        Ticks(ModsShortcutRoute.AttachFrames + 1);
        Assert.Empty(_host.Opens);

        ModsShortcutRoute.Reset();
        Assert.False(ModsShortcutRoute.RaisedMenu);
        Assert.False(ModsShortcutRoute.SuppressMenuRequest);
        Assert.Equal("", ModsShortcutRoute.LastFallback);
    }

    [Fact]
    public void Close_records_its_resolved_reason()
    {
        ModsMenu.Open();
        ModsMenu.Close(ModsMenu.ReasonUserButton);
        Assert.Equal(ModsMenu.ReasonUserButton, ModsMenu.LastCloseReason);
    }

    // ---- review round 2 ------------------------------------------------------------------------

    private void OpenRoutedNative()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        ModsShortcutRoute.Tick();
        Assert.True(ModsMenu.IsOpen);
        Assert.True(ModsShortcutRoute.RaisedMenu);
    }

    [Fact]
    public void Re_press_inside_the_return_window_keeps_the_raised_menu_ours()
    {
        OpenRoutedNative();
        ModsMenu.Toggle(F10);
        _host.NativeReturnPending = true;
        ModsShortcutRoute.Tick();
        Assert.Equal(0, _host.Dismissals);

        // Re-press while the return transition is still running: opens again over the same menu.
        ModsMenu.Toggle(F10);
        Assert.True(ModsMenu.IsOpen);
        Assert.True(ModsShortcutRoute.RaisedMenu);
        _host.NativeReturnPending = false;

        ModsMenu.Toggle(F10);
        ModsShortcutRoute.Tick();
        Assert.Equal(1, _host.Dismissals);
        Assert.False(_host.MenuUp);
    }

    [Fact]
    public void No_fallback_over_the_players_own_menu()
    {
        _host.MenuUp = true;
        ModsMenu.Toggle(F10);
        Ticks(ModsShortcutRoute.AttachFrames);
        Assert.False(ModsMenu.IsOpen);
        Assert.Empty(_host.Opens);
        Assert.Equal(0, _host.Dismissals);
        Assert.True(_host.MenuUp);
        Assert.StartsWith(ModsShortcutRoute.WhyPlayersMenu, ModsShortcutRoute.LastCancel, StringComparison.Ordinal);
        Assert.False(ModsShortcutRoute.Pending);
    }

    [Fact]
    public void Refused_open_in_the_players_own_menu_is_cancelled_not_a_window_over_it()
    {
        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        _host.NativeOpenAccepted = false;
        ModsMenu.Toggle(F10);
        Assert.False(ModsMenu.IsOpen);
        Assert.Empty(_host.Opens);
        Assert.True(_host.MenuUp);
    }

    [Fact]
    public void Fallback_waits_for_the_dismissed_menu_to_go_down_before_the_window()
    {
        _host.DismissTakesEffect = false;
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        Ticks(ModsShortcutRoute.AttachFrames);
        Assert.Equal(1, _host.Dismissals);
        Assert.False(ModsMenu.IsOpen);
        Assert.True(ModsShortcutRoute.Pending);
        ModsShortcutRoute.Tick();
        Assert.False(ModsMenu.IsOpen);

        _host.MenuUp = false;
        ModsShortcutRoute.Tick();
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(new[] { "standalone:" + F10 }, _host.Opens);
        Assert.False(ModsShortcutRoute.Pending);
    }

    [Fact]
    public void Fallback_gives_up_when_the_dismissed_menu_stays_up()
    {
        _host.DismissTakesEffect = false;
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        Ticks(ModsShortcutRoute.AttachFrames + ModsShortcutRoute.FallbackDismissFrames);
        Assert.False(ModsMenu.IsOpen);
        Assert.Empty(_host.Opens);
        Assert.Equal(ModsShortcutRoute.WhyMenuStayedUp, ModsShortcutRoute.LastCancel);
        Assert.False(ModsShortcutRoute.Pending);
    }

    [Fact]
    public void Escape_before_the_tab_attaches_cancels_the_pending_open()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        ModsShortcutRoute.Tick();
        _host.MenuUp = false;
        ModsShortcutRoute.Tick();
        Assert.False(ModsShortcutRoute.Pending);
        Ticks(ModsShortcutRoute.AttachFrames + 5);
        Assert.False(ModsMenu.IsOpen);
        Assert.Empty(_host.Opens);
        Assert.Equal(ModsShortcutRoute.WhyMenuClosed, ModsShortcutRoute.LastCancel);
    }

    [Fact]
    public void Another_hud_menu_cancels_the_pending_open_after_the_grace()
    {
        ModsMenu.Toggle(F10);
        _host.OtherMenuUp = true;
        // The press itself is frame 1 of the grace.
        Ticks(ModsShortcutRoute.OtherMenuGraceFrames - 1);
        Assert.True(ModsShortcutRoute.Pending);
        Ticks(1);
        Assert.False(ModsShortcutRoute.Pending);
        Assert.Equal(ModsShortcutRoute.WhyOtherMenu, ModsShortcutRoute.LastCancel);
        Ticks(ModsShortcutRoute.AttachFrames);
        Assert.Empty(_host.Opens);
        Assert.False(ModsMenu.IsOpen);
    }

    [Fact]
    public void Another_hud_menu_after_the_fullscreen_menu_was_seen_cancels_at_once()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        ModsShortcutRoute.Tick();
        _host.MenuUp = false;
        _host.OtherMenuUp = true;
        ModsShortcutRoute.Tick();
        Assert.False(ModsShortcutRoute.Pending);
        Assert.Equal(ModsShortcutRoute.WhyOtherMenu, ModsShortcutRoute.LastCancel);
    }

    [Theory]
    [InlineData(false, true, false, false, 0, (int)ShortcutPendingStep.CancelMenuClosed)]
    [InlineData(false, false, true, true, 0, (int)ShortcutPendingStep.CancelOtherMenu)]
    [InlineData(false, false, true, false, ModsShortcutRoute.OtherMenuGraceFrames, (int)ShortcutPendingStep.CancelOtherMenu)]
    [InlineData(false, false, true, false, 0, (int)ShortcutPendingStep.Wait)]
    [InlineData(true, false, true, true, 0, (int)ShortcutPendingStep.Wait)]
    public void Cancel_decisions(bool menuUp, bool sawAndDown, bool otherMenu, bool saw, int elapsed, int expected)
    {
        bool sawMenuUp = saw || sawAndDown;
        var step = ModsShortcutRoute.DecidePending(
            workspaceOpen: false, menuUp, otherMenu && !menuUp, sawMenuUp, elapsed,
            nativeRouteActive: false, imitationTabAttached: false, framesLeft: 50);
        Assert.Equal((ShortcutPendingStep)expected, step);
    }

    [Fact]
    public void Core_unload_dismisses_a_menu_the_route_raised()
    {
        OpenRoutedNative();
        ModsShortcutRoute.ReleaseForUnload();
        Assert.Equal(1, _host.Dismissals);
        Assert.False(ModsShortcutRoute.RaisedMenu);
        Assert.Contains(_host.Lines, l => l.StartsWith(ModsShortcutRoute.LogPrefix + "core-unload dismissed", StringComparison.Ordinal));
    }

    [Fact]
    public void Core_unload_mid_request_dismisses_the_requested_menu()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        ModsShortcutRoute.ReleaseForUnload();
        Assert.Equal(1, _host.Dismissals);
        Assert.False(ModsShortcutRoute.Pending);
    }

    [Fact]
    public void Core_unload_leaves_the_players_own_menu()
    {
        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        ModsMenu.Toggle(F10);
        Assert.True(ModsMenu.IsOpen);
        ModsShortcutRoute.ReleaseForUnload();
        Assert.Equal(0, _host.Dismissals);
    }

    [Fact]
    public void Standalone_open_cancels_a_pending_route_and_suppresses_the_menu_request()
    {
        ModsMenu.Toggle(F10);
        Assert.True(ModsShortcutRoute.Pending);
        ModsMenu.OpenStandalone();
        Assert.False(ModsShortcutRoute.Pending);
        Assert.True(ModsMenu.IsOpen);
        Assert.True(ModsShortcutRoute.SuppressMenuRequest);
        Assert.Equal(1, _host.Dismissals);
        ModsMenu.Close();
        ModsShortcutRoute.Tick();
        Assert.False(ModsShortcutRoute.SuppressMenuRequest);
    }

    [Fact]
    public void Standalone_open_at_the_title_does_not_suppress_anything()
    {
        _host.InWorld = false;
        ModsMenu.OpenStandalone();
        Assert.True(ModsMenu.IsOpen);
        Assert.False(ModsShortcutRoute.SuppressMenuRequest);
    }

    // ---- round 4: open only on an active tab bar; repaint the unselected native tabs ----------

    [Fact]
    public void Routed_open_waits_until_the_tab_bar_is_active()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        _host.MenuReadyOverride = false;
        Ticks(5);
        Assert.False(ModsMenu.IsOpen);
        Assert.Empty(_host.Opens);
        _host.MenuReadyOverride = true;
        ModsShortcutRoute.Tick();
        Assert.Equal(new[] { "native" }, _host.Opens);
    }

    [Theory]
    [InlineData(true, false, (int)ShortcutPendingStep.Wait)]
    [InlineData(true, true, (int)ShortcutPendingStep.OpenNative)]
    [InlineData(false, true, (int)ShortcutPendingStep.Wait)]
    public void Open_needs_the_menu_up_and_ready(bool menuUp, bool ready, int expected) =>
        Assert.Equal((ShortcutPendingStep)expected, ModsShortcutRoute.DecidePending(
            workspaceOpen: false, menuUp, otherMenuUp: false, sawMenuUp: false, elapsed: 0,
            nativeRouteActive: true, imitationTabAttached: false, framesLeft: 50, menuReady: ready));

    [Fact]
    public void Unselected_native_tabs_are_repainted_twice_after_a_routed_open()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        ModsShortcutRoute.Tick();
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(0, _host.Repaints);
        Ticks(ModsShortcutRoute.RepaintFirstFrame);
        Assert.Equal(1, _host.Repaints);
        Ticks(ModsShortcutRoute.RepaintLastFrame);
        Assert.Equal(2, _host.Repaints);
    }

    [Fact]
    public void No_repaint_after_the_workspace_closed()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.NativeRouteActive = true;
        ModsShortcutRoute.Tick();
        ModsMenu.Close(ModsMenu.ReasonNativeTab);
        Ticks(ModsShortcutRoute.RepaintLastFrame + 2);
        Assert.Equal(0, _host.Repaints);
    }

    [Theory]
    [InlineData("Spellbook", "Mods")]
    [InlineData("Vampire Powers", "Mods")]
    [InlineData("SPELLBOOK", "MODS")]
    [InlineData("", "MODS")]
    [InlineData(null, "MODS")]
    [InlineData("123", "MODS")]
    public void Mods_caption_follows_the_native_sibling_case(string? sibling, string expected) =>
        Assert.Equal(expected, NativeTabCaption.MatchSiblingCase(sibling, "MODS"));

    // ---- round 5: wait for the native route while its retry is armed (probe I) ------------------

    [Fact]
    public void Imitation_is_not_taken_while_the_native_retry_is_armed()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.ImitationTabAttached = true;
        _host.NativeRoutePending = true;
        Ticks(30);
        Assert.False(ModsMenu.IsOpen);
        Assert.Empty(_host.Opens);

        // The retry lands: the imitation entry is dropped and the native route is up.
        _host.ImitationTabAttached = false;
        _host.NativeRoutePending = false;
        _host.NativeRouteActive = true;
        ModsShortcutRoute.Tick();
        Assert.Equal(new[] { "native" }, _host.Opens);
    }

    [Fact]
    public void Imitation_is_taken_when_the_native_retry_gives_up()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.ImitationTabAttached = true;
        _host.NativeRoutePending = true;
        Ticks(10);
        _host.NativeRoutePending = false;
        ModsShortcutRoute.Tick();
        Assert.Equal(new[] { "imitation:" + F10 }, _host.Opens);
    }

    [Fact]
    public void At_the_end_of_the_budget_the_imitation_route_beats_the_fallback()
    {
        ModsMenu.Toggle(F10);
        _host.MenuUp = true;
        _host.ImitationTabAttached = true;
        _host.NativeRoutePending = true;
        Ticks(ModsShortcutRoute.AttachFrames);
        Assert.True(ModsMenu.IsOpen);
        Assert.Equal(new[] { "imitation:" + F10 }, _host.Opens);
        Assert.Equal("", ModsShortcutRoute.LastFallback);
    }

    [Theory]
    [InlineData(true, 50, (int)ShortcutPendingStep.Wait)]
    [InlineData(false, 50, (int)ShortcutPendingStep.OpenImitation)]
    [InlineData(true, 0, (int)ShortcutPendingStep.OpenImitation)]
    public void Native_pending_decision(bool pending, int frames, int expected) =>
        Assert.Equal((ShortcutPendingStep)expected, ModsShortcutRoute.DecidePending(
            workspaceOpen: false, menuUp: true, otherMenuUp: false, sawMenuUp: true, elapsed: 0,
            nativeRouteActive: false, imitationTabAttached: true, framesLeft: frames, menuReady: true,
            nativeRoutePending: pending));
}
