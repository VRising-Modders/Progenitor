using System;
using System.Collections.Generic;
using Progenitor.Inventory;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// RETURN1, game-free: the rules of going back to the chest or station window the player had open
/// (<c>ctx.Inventory.ReopenLast</c>, <see cref="ContainerReopenPlan"/>), a container preview that takes that window's place
/// and returns to it, and the Escape order over a game window (<see cref="GameWindowUnderPanel"/>).
/// </summary>
public sealed class ContainerReturnTests
{
    // ---- the call's own answer -------------------------------------------------------------------

    [Fact]
    public void The_call_answers_in_order_world_memory_object_position()
    {
        Assert.Equal(ContainerReopenResult.NotInGame, ContainerReturnRules.Check(false, true, 0, true, false));
        Assert.Equal(ContainerReopenResult.NothingToReopen, ContainerReturnRules.Check(true, false, 0, false, true));
        Assert.Equal(ContainerReopenResult.NothingToReopen, ContainerReturnRules.Check(true, true, ContainerReturnRules.RememberSeconds + 1, true, false));
        Assert.Equal(ContainerReopenResult.NothingToReopen, ContainerReturnRules.Check(true, true, double.NaN, true, false));
        Assert.Equal(ContainerReopenResult.Gone, ContainerReturnRules.Check(true, true, 5, false, true));
        Assert.Equal(ContainerReopenResult.Moved, ContainerReturnRules.Check(true, true, 5, true, true));
        Assert.Equal(ContainerReopenResult.Started, ContainerReturnRules.Check(true, true, 5, true, false));
    }

    [Fact]
    public void Moving_more_than_a_meter_on_the_ground_counts_as_moved()
    {
        Assert.False(ContainerReturnRules.Moved(10, 20, 10.5f, 20.5f));
        Assert.False(ContainerReturnRules.Moved(10, 20, 11f, 20f)); // exactly the limit
        Assert.True(ContainerReturnRules.Moved(10, 20, 11.01f, 20f));
        Assert.True(ContainerReturnRules.Moved(10, 20, float.NaN, 20f));
    }

    [Fact]
    public void Only_a_window_opened_at_the_object_is_remembered()
    {
        Assert.True(ContainerReturnRules.Remembers(VanillaPanelKind.Container, hasInteraction: true, previewOwnsWindow: false));
        Assert.False(ContainerReturnRules.Remembers(VanillaPanelKind.Container, hasInteraction: false, previewOwnsWindow: false)); // a preview's window
        Assert.False(ContainerReturnRules.Remembers(VanillaPanelKind.Container, hasInteraction: true, previewOwnsWindow: true));
        Assert.True(ContainerReturnRules.Remembers(VanillaPanelKind.Workstation, true, false));
        Assert.True(ContainerReturnRules.Remembers(VanillaPanelKind.CastleHeart, true, false));
        Assert.False(ContainerReturnRules.Remembers(VanillaPanelKind.Character, true, false));
        Assert.False(ContainerReturnRules.Remembers(VanillaPanelKind.None, true, false));
    }

    [Theory]
    [InlineData(ContainerPreviewCloseReason.Disposed, true)]
    [InlineData(ContainerPreviewCloseReason.Escape, true)]
    [InlineData(ContainerPreviewCloseReason.WindowClosed, true)]
    [InlineData(ContainerPreviewCloseReason.Timeout, true)]
    [InlineData(ContainerPreviewCloseReason.LeaseEnded, true)]
    [InlineData(ContainerPreviewCloseReason.TargetGone, true)]
    [InlineData(ContainerPreviewCloseReason.OpenFailed, true)]
    [InlineData(ContainerPreviewCloseReason.Failed, true)]
    [InlineData(ContainerPreviewCloseReason.Replaced, false)]
    [InlineData(ContainerPreviewCloseReason.WorldChanged, false)]
    [InlineData(ContainerPreviewCloseReason.Unloaded, false)]
    [InlineData(ContainerPreviewCloseReason.None, false)]
    public void A_preview_returns_unless_replaced_the_world_changed_or_unloaded(ContainerPreviewCloseReason reason, bool returns) =>
        Assert.Equal(returns, ContainerReturnRules.ReturnsAfterPreview(reason));

    // ---- one reopen ------------------------------------------------------------------------------

    private static ContainerReopenSample Here(bool window = false, bool menu = false, bool camera = false) => new()
    { SameWorld = true, TargetPresent = true, WindowOnTarget = window, AnyMenuOpen = menu || window, CameraPreview = camera };

    [Fact]
    public void The_happy_path_settles_hovers_presses_once_and_sees_the_window()
    {
        var p = new ContainerReopenPlan(0);
        Assert.Equal(ContainerReopenStep.Wait, p.Step(0.01, Here()));
        Assert.Equal(ContainerReopenStep.StartHover, p.Step(0.02, Here())); // settled, nothing open
        for (int i = 1; i < ContainerReopenPlan.HoverFrames; i++) Assert.Equal(ContainerReopenStep.Wait, p.Step(0.02 + i * 0.01, Here()));
        Assert.Equal(ContainerReopenStep.Press, p.Step(0.1, Here()));
        Assert.True(p.PressedEver);
        Assert.Equal(ContainerReopenStep.Wait, p.Step(0.2, Here()));
        Assert.Equal(ContainerReopenStep.Finish, p.Step(0.3, Here(window: true)));
        Assert.Equal(ContainerReopenResult.Opened, p.Result);
        Assert.Equal(ContainerReopenStep.Wait, p.Step(0.4, Here(window: true))); // done stays done
    }

    [Fact]
    public void A_window_that_is_still_open_after_the_settle_is_left_alone()
    {
        var p = new ContainerReopenPlan(0);
        Assert.Equal(ContainerReopenStep.Wait, p.Step(0.01, Here(window: true)));
        Assert.Equal(ContainerReopenStep.Finish, p.Step(0.02, Here(window: true)));
        Assert.Equal(ContainerReopenResult.AlreadyOpen, p.Result);
        Assert.False(p.PressedEver);
    }

    [Fact]
    public void A_window_closing_on_the_same_key_press_is_reopened_once_it_is_gone()
    {
        // The hub's Escape: the game's window is still on screen on the first frame and gone on the next.
        var p = new ContainerReopenPlan(0);
        Assert.Equal(ContainerReopenStep.Wait, p.Step(0.01, Here(window: true)));
        Assert.Equal(ContainerReopenStep.StartHover, p.Step(0.02, Here()));
    }

    [Fact]
    public void Another_game_window_that_stays_open_ends_it_without_a_press()
    {
        var p = new ContainerReopenPlan(0);
        p.Step(0.01, Here(menu: true));
        Assert.Equal(ContainerReopenStep.Wait, p.Step(0.02, Here(menu: true)));
        Assert.Equal(ContainerReopenStep.Wait, p.Step(0.02 + ContainerReopenPlan.MenuWaitSeconds - 0.01, Here(menu: true)));
        Assert.Equal(ContainerReopenStep.Finish, p.Step(0.02 + ContainerReopenPlan.MenuWaitSeconds, Here(menu: true)));
        Assert.Equal(ContainerReopenResult.MenuOpen, p.Result);
        Assert.False(p.PressedEver);
    }

    [Fact]
    public void A_menu_closing_within_the_wait_lets_it_go_on()
    {
        var p = new ContainerReopenPlan(0);
        p.Step(0.01, Here(menu: true));
        Assert.Equal(ContainerReopenStep.Wait, p.Step(0.02, Here(menu: true))); // the preview's own window, closing
        Assert.Equal(ContainerReopenStep.StartHover, p.Step(0.3, Here()));
    }

    [Fact]
    public void A_camera_preview_is_waited_for_then_given_up_on()
    {
        var p = new ContainerReopenPlan(0);
        p.Step(0.01, Here(camera: true));
        Assert.Equal(ContainerReopenStep.Wait, p.Step(0.02, Here(camera: true)));
        Assert.Equal(ContainerReopenStep.Wait, p.Step(30, Here(camera: true))); // longer than the menu wait
        Assert.Equal(ContainerReopenStep.StartHover, p.Step(31, Here()));

        var q = new ContainerReopenPlan(0);
        q.Step(0.01, Here(camera: true));
        q.Step(0.02, Here(camera: true));
        Assert.Equal(ContainerReopenStep.Finish, q.Step(0.02 + ContainerReopenPlan.CameraWaitSeconds, Here(camera: true)));
        Assert.Equal(ContainerReopenResult.MenuOpen, q.Result);
    }

    [Fact]
    public void A_menu_that_opens_while_hovering_is_not_pressed_into()
    {
        var p = new ContainerReopenPlan(0);
        p.Step(0.01, Here());
        Assert.Equal(ContainerReopenStep.StartHover, p.Step(0.02, Here()));
        Assert.Equal(ContainerReopenStep.Finish, p.Step(0.03, Here(menu: true)));
        Assert.Equal(ContainerReopenResult.MenuOpen, p.Result);
        Assert.False(p.PressedEver);
    }

    [Fact]
    public void No_window_after_the_press_is_not_opened()
    {
        var p = Pressed(out double at);
        Assert.Equal(ContainerReopenStep.Wait, p.Step(at + ContainerReopenPlan.OpenWaitSeconds - 0.01, Here()));
        Assert.Equal(ContainerReopenStep.Finish, p.Step(at + ContainerReopenPlan.OpenWaitSeconds, Here()));
        Assert.Equal(ContainerReopenResult.NotOpened, p.Result);
    }

    [Fact]
    public void The_object_gone_the_character_moved_or_the_world_changed_ends_it()
    {
        var gone = new ContainerReopenPlan(0);
        Assert.Equal(ContainerReopenStep.Finish, gone.Step(0.01, new ContainerReopenSample { SameWorld = true }));
        Assert.Equal(ContainerReopenResult.Gone, gone.Result);

        var moved = Pressed(out double at);
        Assert.Equal(ContainerReopenStep.Finish, moved.Step(at + 0.1, new ContainerReopenSample { SameWorld = true, TargetPresent = true, Moved = true }));
        Assert.Equal(ContainerReopenResult.Moved, moved.Result);

        var world = new ContainerReopenPlan(0);
        Assert.Equal(ContainerReopenStep.Finish, world.Step(0.01, default));
        Assert.Equal(ContainerReopenResult.Cancelled, world.Result);
    }

    [Fact]
    public void Ending_it_from_outside_reports_once()
    {
        var p = new ContainerReopenPlan(0);
        Assert.Equal(ContainerReopenStep.Finish, p.End(ContainerReopenResult.InputBusy));
        Assert.Equal(ContainerReopenStep.Wait, p.End(ContainerReopenResult.Cancelled));
        Assert.Equal(ContainerReopenResult.InputBusy, p.Result);
        Assert.True(p.Done);
    }

    private static ContainerReopenPlan Pressed(out double at)
    {
        var p = new ContainerReopenPlan(0);
        double now = 0.01;
        ContainerReopenStep step;
        do { step = p.Step(now, Here()); now += 0.01; } while (step != ContainerReopenStep.Press);
        at = now - 0.01;
        return p;
    }

    // ---- a preview over the player's own window --------------------------------------------------

    private static ContainerPreviewSample Ready(bool other, bool returnable) => new()
    { SameWorld = true, TargetFound = true, InventoryReady = true, OtherMenuOpen = other, OtherMenuReturnable = returnable };

    private static ContainerPreviewMachine Agreed(ContainerPreviewOptions? options = null)
    {
        var m = new ContainerPreviewMachine(options ?? new ContainerPreviewOptions(), 0);
        Assert.True(m.OnReply(0, ok: true, ""));
        return m;
    }

    [Fact]
    public void A_ready_preview_takes_the_place_of_the_players_window_once_then_opens()
    {
        var m = Agreed();
        Assert.Equal(ContainerPreviewStep.CloseOtherWindow, m.Step(0.1, Ready(other: true, returnable: true)));
        Assert.True(m.TookPlayerWindow);
        Assert.Equal(ContainerPreviewStep.None, m.Step(0.2, Ready(other: true, returnable: true))); // still closing; asked once
        Assert.Equal(ContainerPreviewStep.OpenWindow, m.Step(0.3, Ready(other: false, returnable: false)));
    }

    [Fact]
    public void The_players_window_is_left_alone_until_the_chest_is_ready()
    {
        var m = Agreed();
        var notReady = new ContainerPreviewSample { SameWorld = true, TargetFound = true, OtherMenuOpen = true, OtherMenuReturnable = true };
        Assert.Equal(ContainerPreviewStep.None, m.Step(0.1, notReady));
        Assert.False(m.TookPlayerWindow);
    }

    [Fact]
    public void With_the_option_off_or_another_kind_of_menu_it_waits_as_before()
    {
        var off = Agreed(new ContainerPreviewOptions { ReturnToOpenWindow = false });
        Assert.Equal(ContainerPreviewStep.None, off.Step(0.1, Ready(other: true, returnable: true)));
        Assert.Equal(ContainerPreviewStep.Closed, off.Step(10, Ready(other: true, returnable: true)));
        Assert.Equal(ContainerPreviewCloseReason.OpenFailed, off.CloseReason);
        Assert.False(off.TookPlayerWindow);

        var map = Agreed();
        Assert.Equal(ContainerPreviewStep.None, map.Step(0.1, Ready(other: true, returnable: false))); // the map, a preview's window
        Assert.False(map.TookPlayerWindow);
    }

    [Fact]
    public void A_replacing_preview_inherits_the_return_unless_its_option_is_off()
    {
        var on = Agreed();
        on.InheritPlayerWindow();
        Assert.True(on.TookPlayerWindow);
        var off = Agreed(new ContainerPreviewOptions { ReturnToOpenWindow = false });
        off.InheritPlayerWindow();
        Assert.False(off.TookPlayerWindow);
    }

    // ---- the Escape order over a game window -----------------------------------------------------

    [Theory]
    [InlineData(3, true)]   // ContainerMenu
    [InlineData(4, true)]   // WorkstationMenu
    [InlineData(18, true)]  // CastleHeartMenu
    [InlineData(1, false)]  // the character menu: not an interact window
    [InlineData(0, false)]  // nothing open
    [InlineData(-1, false)] // unreadable
    public void Interact_windows_are_the_chest_station_and_heart_menus(int menu, bool interact) =>
        Assert.Equal(interact, GameWindowUnderPanel.IsInteractWindow(menu));

    [Fact]
    public void The_close_is_held_only_over_an_interact_window_with_a_panel_taking_escape()
    {
        Assert.True(GameWindowUnderPanel.HoldsClose(3, true));
        Assert.False(GameWindowUnderPanel.HoldsClose(3, false));
        Assert.False(GameWindowUnderPanel.HoldsClose(0, true));
        Assert.False(GameWindowUnderPanel.HoldsClose(1, true));
        Assert.True(GameWindowUnderPanel.HoldsInteract(3, true));
        Assert.False(GameWindowUnderPanel.HoldsInteract(3, false));
        Assert.False(GameWindowUnderPanel.HoldsInteract(0, true));
    }

    [Fact]
    public void Panels_in_the_game_windows_tabs_workspace_panes_and_passive_panels_do_not_take_escape()
    {
        // A window (a hub) takes it whatever the pointer does; a floating panel only while it takes the input.
        Assert.True(GameWindowUnderPanel.PanelTakesEscape(false, false, UiCaptureMode.WhenHovered, isWindow: true, takesInputNow: false));
        Assert.True(GameWindowUnderPanel.PanelTakesEscape(false, false, UiCaptureMode.WhileShown, isWindow: false, takesInputNow: true));
        Assert.False(GameWindowUnderPanel.PanelTakesEscape(false, false, UiCaptureMode.WhenHovered, isWindow: false, takesInputNow: false)); // a tracker
        Assert.False(GameWindowUnderPanel.PanelTakesEscape(false, false, UiCaptureMode.Never, true, true));
        Assert.False(GameWindowUnderPanel.PanelTakesEscape(false, true, UiCaptureMode.WhileShown, true, true));
        Assert.False(GameWindowUnderPanel.PanelTakesEscape(true, false, UiCaptureMode.WhileShown, true, true));
        Assert.True(GameWindowUnderPanel.SweepKeepsMounted(true, true, true));
        Assert.False(GameWindowUnderPanel.SweepKeepsMounted(true, true, false));
        Assert.False(GameWindowUnderPanel.SweepKeepsMounted(false, true, true));
        Assert.False(GameWindowUnderPanel.SweepKeepsMounted(true, false, true));
    }

    [Fact]
    public void The_hold_outlasts_the_closed_panel_while_escape_is_still_down()
    {
        // A window shown over the chest: held. Escape hides it; the press is still down: still held, so neither the press
        // nor its release reaches the chest's handler. Released on the first frame Escape is up.
        bool held = WorkspaceInputPolicy.HoldsGameClose(false, GameWindowUnderPanel.HoldsClose(3, true), escapeHeld: false, wasHeld: false);
        Assert.True(held);
        held = WorkspaceInputPolicy.HoldsGameClose(false, GameWindowUnderPanel.HoldsClose(3, false), escapeHeld: true, wasHeld: held);
        Assert.True(held);
        held = WorkspaceInputPolicy.HoldsGameClose(false, GameWindowUnderPanel.HoldsClose(3, false), escapeHeld: false, wasHeld: held);
        Assert.False(held);
    }
}

/// <summary>RETURN1: the Escape sweep with the game's chest window open under a hub and a tab panel.</summary>
[Collection("UiStatic")]
public sealed class ContainerReturnEscapeTests : IDisposable
{
    private readonly object _owner = new();
    private readonly List<IUiPanel> _panels = new();

    public ContainerReturnEscapeTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(new CellRowTests.PlainRuntime());
        UiInputCapture.ResetForTests();
    }

    public void Dispose()
    {
        foreach (var p in _panels) p.Dispose();
        GameWindowUnderPanel.MenuTypeNow = GameWindowUnderPanel.NoMenu;
        UiInputCapture.ResetForTests();
        UiApi.ResetForTests();
    }

    private IUiPanel Shown(string title, UiCaptureMode mode, bool mounted = false)
    {
        var panel = UiApi.Panel(title, _owner, "example.return." + _panels.Count);
        _panels.Add(panel);
        panel.CaptureMode = mode;
        if (mounted) ((UiPanelHandle)panel).MountedInTab = true;
        panel.Show();
        return panel;
    }

    [Fact]
    public void Over_a_chest_window_escape_hides_the_hub_and_keeps_the_tab_panel()
    {
        GameWindowUnderPanel.MenuTypeNow = () => 3;
        var tab = Shown("Input", UiCaptureMode.WhenHovered, mounted: true);
        var hub = Shown("Hub", UiCaptureMode.WhileShown);
        Assert.True(UiApi.EscapeHidesAPanelOverGameWindow());

        UiApi.PressEscapeForBridge();
        Assert.Equal(UiHandleState.Built, hub.State);
        Assert.Equal(UiHandleState.Shown, tab.State);
        Assert.False(UiApi.EscapeHidesAPanelOverGameWindow()); // the next Escape is the chest window's
    }

    [Fact]
    public void A_wide_hub_the_sweep_hides_leaves_the_tab_panel_too()
    {
        // A game window wider than the kit frame is not flowed (Flows false), so Escape reaches it through the sweep.
        GameWindowUnderPanel.MenuTypeNow = () => 3;
        var tab = Shown("Input", UiCaptureMode.WhenHovered, mounted: true);
        var hub = UiApi.Panel("Hub", _owner, "example.return.wide");
        _panels.Add(hub);
        hub.CaptureMode = UiCaptureMode.WhenHovered;
        ((UiPanelHandle)hub).Flows = false;
        ((UiPanelHandle)hub).IsGameWindow = true;
        hub.Show();

        UiApi.PressEscapeForBridge();
        Assert.Equal(UiHandleState.Built, hub.State);
        Assert.Equal(UiHandleState.Shown, tab.State);
    }

    [Fact]
    public void Without_a_game_window_the_sweep_hides_every_panel_as_before()
    {
        var tab = Shown("Input", UiCaptureMode.WhenHovered, mounted: true);
        var hub = UiApi.Panel("Hub", _owner, "example.return.wide");
        _panels.Add(hub);
        hub.CaptureMode = UiCaptureMode.WhenHovered;
        ((UiPanelHandle)hub).Flows = false;
        ((UiPanelHandle)hub).IsGameWindow = true;
        hub.Show();
        UiApi.PressEscapeForBridge();
        Assert.Equal(UiHandleState.Built, hub.State);
        Assert.Equal(UiHandleState.Built, tab.State);
    }

    [Fact]
    public void A_tracker_left_on_screen_does_not_hold_the_chest_windows_escape()
    {
        GameWindowUnderPanel.MenuTypeNow = () => 3;
        Shown("Tracker", UiCaptureMode.WhenHovered); // a kit panel, the pointer elsewhere
        Assert.False(UiApi.EscapeHidesAPanelOverGameWindow());
    }

    [Fact]
    public void With_only_the_tab_panel_the_sweep_and_the_chest_window_close_together()
    {
        GameWindowUnderPanel.MenuTypeNow = () => 3;
        var tab = Shown("Input", UiCaptureMode.WhenHovered, mounted: true);
        Assert.False(UiApi.EscapeHidesAPanelOverGameWindow()); // no hold: the chest's own Escape closes it
        UiApi.PressEscapeForBridge();
        Assert.Equal(UiHandleState.Built, tab.State);
    }
}
