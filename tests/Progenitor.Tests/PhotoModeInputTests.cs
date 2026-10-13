using System;
using System.Collections.Generic;
using Progenitor.Cameras;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Photo mode's gameplay-input block and the server's <c>[PhotoMode] AllowOnThisServer</c>
/// (hands-on findings 2026-10-02: the character still answered W/A/S/D, E/Q/R/F, right mouse, P/H
/// while flying; and the request for a server switch). Game-free; nothing here has run in the game.
/// </summary>
[Collection("PluginLogger")]
public class PhotoModeInputTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly ScopeBlocker _blocker = new();

    public PhotoModeInputTests()
    {
        Plugin.Logger = _log;
        UiApi.ResetForTests();
        UiApi.SetInputBlockerForTests(_blocker);
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    // ---- the exclusive mask plan ----

    [Fact]
    public void The_exclusive_plan_takes_every_gameplay_button_analog_camera_and_menu_hotkey_but_not_ui_navigation()
    {
        var plan = InputMaskPlan.For(UiMaskScope.Exclusive);
        Assert.Equal(UiMaskScope.Exclusive, plan.Scope);
        Assert.True(plan.BlockAllKeys);
        Assert.False(plan.IncludeUiNavigation);
        Assert.True(plan.BlockMoveAndAim);
        Assert.True(plan.BlockZoom);
        Assert.True(plan.BlockAllAnalog);
        Assert.True(plan.BlockMenuHotkeys);
        Assert.True(plan.BlockCameraRotation);
        Assert.False(plan.AllowCameraRotationOnKeyboardAndMouse);
        Assert.True(plan.BlockCameraRotationFallback);
        Assert.Equal(int.MaxValue, AnalogInputActions.All);
    }

    [Fact]
    public void Panel_scopes_are_unchanged()
    {
        var full = InputMaskPlan.For(UiMaskScope.Full);
        Assert.True(full.AllowCameraRotationOnKeyboardAndMouse);
        Assert.False(full.BlockAllAnalog);
        Assert.False(full.BlockMenuHotkeys);
        Assert.False(full.BlockCameraRotation);
        var close = InputMaskPlan.For(UiMaskScope.CloseKeyOnly);
        Assert.False(close.BlockAllKeys);
        Assert.False(close.BlockAllAnalog);
    }

    [Fact]
    public void The_native_mask_reads_the_exclusive_flags()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "GameInputBlocker.cs");
        SourcePin.AssertUncommented(src, "if (plan.BlockAllAnalog)");
        SourcePin.AssertUncommented(src, "state.BlockedAnalogInputActions |= AnalogInputAction.All;");
        SourcePin.AssertUncommented(src, "if (plan.BlockCameraRotation)");
        SourcePin.AssertUncommented(src, "if (MenuHotkeyGuard.HotkeysBlocked || plan.BlockMenuHotkeys)");
    }

    // ---- the exclusive hold in UiInputCapture ----

    [Fact]
    public void An_exclusive_hold_registers_the_context_and_masks_exclusive_wherever_the_pointer_is()
    {
        UiInputCapture.SetPointerBlock(false);
        UiInputCapture.AcquireExclusive();
        Assert.True(_blocker.IsRegistered);
        Assert.Equal(UiMaskScope.Exclusive, _blocker.AppliedScope);
        Assert.True(UiInputCapture.IsFullyBlocking);
        Assert.Equal(1, UiInputCapture.ExclusiveCount);
        Assert.Contains("exclusive=1", UiInputCapture.Describe());

        UiInputCapture.ReleaseExclusive();
        Assert.False(_blocker.IsRegistered);
        Assert.NotEqual(UiMaskScope.Exclusive, _blocker.AppliedScope);
        Assert.False(UiInputCapture.IsFullyBlocking);
        UiInputCapture.ReleaseExclusive(); // never below zero
        Assert.Equal(0, UiInputCapture.ExclusiveCount);
    }

    [Fact]
    public void An_exclusive_hold_survives_capture_being_disabled_and_a_panel_closing_under_it()
    {
        UiApi.CaptureInput(false);
        UiInputCapture.AcquireExclusive();
        Assert.True(_blocker.IsRegistered);
        UiInputCapture.Acquire();
        UiInputCapture.Release();
        Assert.True(_blocker.IsRegistered);
        Assert.Equal(UiMaskScope.Exclusive, _blocker.AppliedScope);
        UiInputCapture.ReleaseExclusive();
        Assert.False(_blocker.IsRegistered);
    }

    [Fact]
    public void Uninstall_drops_every_exclusive_hold()
    {
        UiInputCapture.AcquireExclusive();
        UiInputCapture.AcquireExclusive();
        UiInputCapture.Uninstall();
        Assert.Equal(0, UiInputCapture.ExclusiveCount);
        Assert.False(_blocker.IsRegistered);
    }

    // ---- photo mode's hold: enter, exit with drain, unload ----

    private sealed class HoldRig
    {
        public int Acquires, Releases;
        public bool Held;
        public long Now = 1000;
        public readonly PhotoInputHold Hold;

        public HoldRig() => Hold = new PhotoInputHold(() => Acquires++, () => Releases++, () => Held, () => Now);
    }

    [Fact]
    public void Enter_takes_the_hold_once_and_exit_releases_when_nothing_is_held()
    {
        var r = new HoldRig();
        r.Hold.Enter();
        r.Hold.Enter();
        Assert.Equal(1, r.Acquires);
        Assert.True(r.Hold.Engaged);
        r.Hold.Exit();
        Assert.Equal(1, r.Releases);
        Assert.False(r.Hold.Engaged);
        Assert.False(r.Hold.Draining);
    }

    [Fact]
    public void Keys_still_held_at_exit_keep_the_game_masked_until_released_or_the_cap()
    {
        var r = new HoldRig { Held = true };
        r.Hold.Enter();
        r.Hold.Exit();
        Assert.True(r.Hold.Draining);
        Assert.False(r.Hold.Tick());
        Assert.Equal(0, r.Releases);
        r.Held = false;
        Assert.True(r.Hold.Tick());
        Assert.Equal(1, r.Releases);

        var capped = new HoldRig { Held = true };
        capped.Hold.Enter();
        capped.Hold.Exit();
        capped.Now += PhotoInputHold.MaxDrainMs;
        Assert.True(capped.Hold.Tick());
        Assert.Equal(1, capped.Releases);
    }

    [Fact]
    public void Re_entering_while_draining_keeps_one_hold_and_unload_drops_it_at_once()
    {
        var r = new HoldRig { Held = true };
        r.Hold.Enter();
        r.Hold.Exit();
        r.Hold.Enter();
        Assert.Equal(1, r.Acquires);
        Assert.False(r.Hold.Draining);
        r.Hold.Drop();
        Assert.Equal(1, r.Releases);
        Assert.False(r.Hold.Engaged);
        r.Hold.Drop();
        Assert.Equal(1, r.Releases);
    }

    [Fact]
    public void Photo_mode_wires_the_hold_the_escape_exit_and_the_unload_drop()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Cameras", "PhotoMode.cs");
        SourcePin.AssertUncommented(src, "_hold = new PhotoInputHold(UiInputCapture.AcquireExclusive, UiInputCapture.ReleaseExclusive,");
        SourcePin.AssertUncommented(src, "try { _hold?.Enter(); }");
        SourcePin.AssertUncommented(src, "_hold?.Exit();");
        SourcePin.AssertUncommented(src, "try { _hold?.Drop(); }");
        SourcePin.AssertUncommented(src, "internal static readonly KeyChord DefaultExitKey = new(UiKey.Escape);");
        // Held flight keys are cleared on entry and exit.
        Assert.True(src.Split("ClearHeld();").Length >= 4);
    }

    // ---- the server switch ----

    [Fact]
    public void A_disallowing_server_refuses_entry_and_ends_an_active_photo_mode()
    {
        Assert.False(PhotoServerPolicy.MayEnter(PhotoServerAllows.Disallowed));
        Assert.Equal("Photo mode is disabled on this server", PhotoServerPolicy.UnavailableReason(PhotoServerAllows.Disallowed));
        Assert.True(PhotoServerPolicy.MustExit(active: true, PhotoServerAllows.Disallowed));
        Assert.False(PhotoServerPolicy.MustExit(active: false, PhotoServerAllows.Disallowed));
        Assert.False(PhotoServerPolicy.MustExit(active: true, PhotoServerAllows.Allowed));
        foreach (var s in new[] { PhotoServerAllows.NotInWorld, PhotoServerAllows.NoProgenitorServer, PhotoServerAllows.Pending, PhotoServerAllows.Allowed })
        {
            Assert.True(PhotoServerPolicy.MayEnter(s));
            Assert.Null(PhotoServerPolicy.UnavailableReason(s));
        }
    }

    [Fact]
    public void A_server_without_progenitor_cannot_enforce_and_photo_mode_stays_allowed()
    {
        Assert.Equal(PhotoServerAllows.NoProgenitorServer, PhotoServerPolicy.AfterHandshake(PhotoServerAllows.NotInWorld, inWorld: true, "failed"));
        Assert.Equal(PhotoServerAllows.NoProgenitorServer, PhotoServerPolicy.AfterHandshake(PhotoServerAllows.Pending, inWorld: true, null));
        Assert.Equal(PhotoServerAllows.Pending, PhotoServerPolicy.AfterHandshake(PhotoServerAllows.NoProgenitorServer, inWorld: true, "ok"));
        Assert.Equal(PhotoServerAllows.Disallowed, PhotoServerPolicy.AfterHandshake(PhotoServerAllows.Disallowed, inWorld: true, "ok"));
        Assert.Equal(PhotoServerAllows.NotInWorld, PhotoServerPolicy.AfterHandshake(PhotoServerAllows.Disallowed, inWorld: false, "ok"));
        Assert.True(PhotoServerPolicy.Allows(PhotoServerAllows.NoProgenitorServer));
        Assert.Equal("no-progenitor-server", PhotoServerPolicy.Source(PhotoServerAllows.NoProgenitorServer));
        Assert.Equal("server", PhotoServerPolicy.Source(PhotoServerAllows.Disallowed));
        Assert.Null(PhotoServerPolicy.Allows(PhotoServerAllows.Pending));
    }

    [Fact]
    public void The_pause_is_refused_with_disabled_on_server_when_the_server_disallows_photo_mode()
    {
        bool allowed = false;
        int freezes = 0;
        var ledger = new PhotoPauseLedger(() => { freezes++; return new Lease(); }, () => allowed);
        var reply = ledger.Handle(1, isAdmin: true, PhotoPauseRequest.For(1, pause: true));
        Assert.False(reply.IsPaused);
        Assert.Equal(PhotoPauseReason.DisabledOnServer, reply.Reason);
        Assert.Equal("disabled-on-server", PhotoServerPolicy.WireName(reply.Reason));
        Assert.Equal(PhotoServerPolicy.DisabledText, PhotoPauseLedger.Describe(reply));
        Assert.Equal(0, freezes);

        // Disallowed beats not-admin; allowed again, an admin pauses.
        Assert.Equal(PhotoPauseReason.DisabledOnServer, ledger.Handle(2, isAdmin: false, PhotoPauseRequest.For(2, true)).Reason);
        allowed = true;
        Assert.True(ledger.Handle(1, isAdmin: true, PhotoPauseRequest.For(3, true)).IsPaused);
        Assert.Equal(1, freezes);
    }

    [Fact]
    public void The_policy_event_is_PrpA_in_the_reserved_range()
    {
        Assert.Equal(0x50727041, PhotoPolicyMessage.EventId);
        Assert.True(Networking.CustomEventPolicy.IsReserved(PhotoPolicyMessage.EventId));
        Assert.NotEqual(PhotoPauseLedger.EventId, PhotoPolicyMessage.EventId);
        Assert.True(PhotoPolicyMessage.Answer(1, true).IsAllowed);
        Assert.False(PhotoPolicyMessage.Answer(1, false).IsAllowed);
    }

    [Fact]
    public void A_disallow_while_active_exits_and_a_disallow_push_ends_server_pauses()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Cameras", "PhotoMode.cs");
        SourcePin.AssertUncommented(src, "if (PhotoServerPolicy.MustExit(_active, next))");
        SourcePin.AssertUncommented(src, "if (!PhotoServerPolicy.MayEnter(_serverState))");
        SourcePin.AssertUncommented(src, "_ledger = new PhotoPauseLedger(Freeze, () => Allowed);");
        SourcePin.AssertUncommented(src, "try { _ledger?.ReleaseAll(); }");
    }

    [Fact]
    public void Photo_mode_defines_its_keybind_group_in_the_requested_order_with_the_mouse_rows()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Cameras", "PhotoMode.cs");
        int at = src.IndexOf("internal static readonly string[] GroupOrder", StringComparison.Ordinal);
        Assert.True(at > 0);
        string order = src.Substring(at, src.IndexOf("};", at, StringComparison.Ordinal) - at);
        string[] expected =
        {
            "ToggleKeybindId", "ExitKeybindId", "\"PhotoModeForward\"", "\"PhotoModeBack\"", "\"PhotoModeLeft\"",
            "\"PhotoModeRight\"", "\"PhotoModeUp\"", "\"PhotoModeDown\"", "\"PhotoModeFast\"", "\"PhotoModeSlow\"",
            "ZoomInKeybindId", "ZoomOutKeybindId", "HudKeybindId", "PauseKeybindId",
        };
        int last = -1;
        foreach (var token in expected)
        {
            int i = order.IndexOf(token, StringComparison.Ordinal);
            Assert.True(i > last, token + " out of order");
            last = i;
        }

        SourcePin.AssertUncommented(src, "_group = KeybindGroups.Define(SettingsPanel.PluginId, GroupId, GroupTitle, GroupOrder, new[]");
        Assert.Contains("\"Hold right mouse\"", src);
        Assert.Contains("\"Mouse wheel\"", src);
        SourcePin.AssertUncommented(src, "}, () => PhotoServerPolicy.UnavailableReason(_serverState));");
        SourcePin.AssertUncommented(src, "try { _group?.Dispose(); } catch { /* the registry may already be reset */ }");
        Assert.Contains("\"Pause the world (admins only).\"", src);
    }

    [Fact]
    public void The_aim_is_held_through_the_input_lease_and_released_with_it_on_every_exit()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Cameras", "PhotoMode.cs");
        // Held: the proven Aim channel of photo mode's own input lease, at the cursor aim of the first command after entry.
        SourcePin.AssertUncommented(src, "lease.AimAt(last.AimPosition);");
        SourcePin.AssertUncommented(src, "_aimEntryFrame = lease.LastSent.Seen ? lease.LastSent.ClientFrame : int.MinValue;");
        // Released: every exit path (toggle, Escape, input release key, world change, server disallow,
        // error, Core unload) goes through Exit, which disposes the lease that carries the aim.
        string exit = SourcePin.MethodBody(src, "private static void Exit(string? why)");
        SourcePin.AssertUncommented(exit, "_aimHeld = false;");
        SourcePin.AssertUncommented(exit, "try { if (lease.Active) lease.Dispose(); } catch { /* already ended */ }");
        SourcePin.AssertUncommented(exit, "Lock.Reset();");
        SourcePin.AssertUncommented(src, "try { Exit(\"Core unloaded\"); }");
        SourcePin.AssertUncommented(src, "Exit(\"the world changed\");");
        SourcePin.AssertUncommented(src, "Exit(\"disabled on this server\");");
        SourcePin.AssertUncommented(src, "() => Exit(null), null));");
    }

    private sealed class Lease : IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>Records registration and the scope pushed to it.</summary>
    private sealed class ScopeBlocker : IUiInputBlocker
    {
        public bool IsRegistered { get; private set; }
        public bool BlocksCameraRotation => false;
        public bool DenyModifications => false;
        public UiMaskScope AppliedScope { get; private set; } = UiMaskScope.Full;
        public bool MaskLive => true;

        public bool Register()
        {
            IsRegistered = true;
            return true;
        }

        public bool Remove()
        {
            IsRegistered = false;
            return true;
        }

        public void SetMaskScope(UiMaskScope scope) => AppliedScope = scope;

        public void Dispose() => IsRegistered = false;
    }
}
