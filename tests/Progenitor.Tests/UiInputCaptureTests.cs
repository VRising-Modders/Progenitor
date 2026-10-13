using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Progenitor.Dev;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Job FUI2. Game-free coverage of the input-capture refcount: one context for any number of
/// open panels, removal when the last one closes, the deferral that keeps us out of
/// <c>InputActionSystem</c>'s callbacks (<c>_DenyModificationsOfInputContexts</c>), and the
/// teardown paths that must not leave a context registered across a Core swap.
/// The game's ordered context list is behind <see cref="IUiInputBlocker"/>; this file fakes it.
/// </summary>
[Collection("PluginLogger")]
public class UiInputCaptureTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly UiTests.FakeUiRuntime _client = new() { IsClient = true };
    private readonly FakeInputBlocker _blocker = new();
    private readonly object _owner = new();

    public UiInputCaptureTests()
    {
        Plugin.Logger = _log;
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_client);
        UiApi.SetInputBlockerForTests(_blocker);
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    /// <summary>
    /// Job FUI20. The order used to be exactly <c>InputContextOrder.MenuInput</c> (600), which is
    /// also where <c>MenuInputSystem</c> — the system that opens the vanilla escape menu — sits,
    /// so the two were peers and which of them subtracted <c>Back</c> first came down to a sort
    /// this repo cannot read offline. It is now one below that: still under chat (99) and the HUD
    /// menus (495-502) so a vanilla menu opened on top still wins, still over Camera (1000) and
    /// Gameplay (1001), and strictly ahead of the menu system so Escape is taken deterministically
    /// rather than by tie-break.
    /// </summary>
    [Fact]
    public void Order_is_one_below_MenuInput_so_the_menu_system_never_ties_with_us()
    {
        Assert.Equal(600, UiInputCapture.MenuInputOrder);
        Assert.Equal(UiInputCapture.MenuInputOrder - 1, UiInputCapture.Order);

        // The documented neighbors, from InputContextOrder in the client metadata.
        const int chatInput = 99;
        const int hudMenu = 502;
        const int camera = 1000;
        Assert.True(UiInputCapture.Order > hudMenu, "a vanilla HUD menu must still subtract first");
        Assert.True(UiInputCapture.Order > chatInput, "chat must still subtract first");
        Assert.True(UiInputCapture.Order < camera, "the camera and gameplay contexts come after us");

        string order = "order=" + UiInputCapture.Order;
        Assert.Contains(order, UiInputCapture.RegisteredLog(maskLive: true), StringComparison.Ordinal);
        Assert.Contains(order, UiInputCapture.RegisteredLog(maskLive: false), StringComparison.Ordinal);
        Assert.Contains(order, UiInputCapture.RemovedLog, StringComparison.Ordinal);
    }

    [Fact]
    public void Showing_a_panel_registers_the_context_once_and_logs_the_order()
    {
        var panel = UiApi.Panel("A", _owner, "example.ui.test");
        UiApi.Show(panel);

        Assert.True(UiApi.IsCapturingInput);
        Assert.Equal(1, _blocker.Registers);
        Assert.Equal(1, UiInputCapture.Count);
        Assert.Contains(_log.Infos, l => l == UiInputCapture.RegisteredLog(maskLive: true));

        panel.Dispose();
    }

    /// <summary>
    /// R44 finding 3. The register line used to state the whole mask recipe unconditionally —
    /// <c>BlockAllKeys</c>, Move/Aim, Zoom, and <c>Back</c>/<c>Menu_Close</c> "so Escape closes the
    /// panel without also opening the game menu" — on every successful register. All of that is
    /// <c>GameInputBlocker.Mask</c>'s work, and <c>Mask</c> runs only if the
    /// <c>GetConsumedInputs</c> detour resolved. When it did not, <c>Register</c> still returns
    /// true (documented, non-fatal), the context blocks camera rotation only, and Escape opens the
    /// vanilla menu — job FUI20's defect 1, with the log asserting the opposite in the same run.
    ///
    /// <para>
    /// So the line reports the blocker's <see cref="IUiInputBlocker.MaskLive"/>, observed on the
    /// pass that did the register. The two states have to be distinguishable, and the one that is
    /// <i>not</i> live must not claim Escape is taken.
    /// </para>
    /// </summary>
    [Fact]
    public void The_register_log_reports_the_mask_that_was_installed_and_not_the_one_intended()
    {
        _blocker.MaskLive = false;

        var panel = UiApi.Panel("A", _owner, "example.ui.test");
        UiApi.Show(panel);

        Assert.Contains(_log.Infos, l => l == UiInputCapture.RegisteredLog(maskLive: false));
        Assert.DoesNotContain(_log.Infos, l => l == UiInputCapture.RegisteredLog(maskLive: true));

        string absent = UiInputCapture.RegisteredLog(maskLive: false);
        string live = UiInputCapture.RegisteredLog(maskLive: true);
        Assert.NotEqual(absent, live);

        // The absent line must not promise any part of the recipe, and must say the one thing a
        // player will notice.
        Assert.Contains("mask=absent", absent, StringComparison.Ordinal);
        Assert.DoesNotContain("BlockAllKeys +", absent, StringComparison.Ordinal);
        Assert.Contains("Escape is NOT consumed", absent, StringComparison.Ordinal);

        Assert.Contains("mask=live", live, StringComparison.Ordinal);
        Assert.Contains("BlockAllKeys", live, StringComparison.Ordinal);

        panel.Dispose();
    }

    [Fact]
    public void A_second_panel_does_not_register_a_second_context()
    {
        var a = UiApi.Panel("A", _owner, "example.ui.test");
        var b = UiApi.Panel("B", _owner, "example.ui.test");
        UiApi.Show(a);
        UiApi.Show(b);

        Assert.Equal(2, UiInputCapture.Count);
        Assert.Equal(1, _blocker.Registers);
        Assert.Equal(0, _blocker.Removes);

        // The first close must not release the second panel's capture.
        UiApi.Hide(a);
        Assert.Equal(1, UiInputCapture.Count);
        Assert.True(_blocker.IsRegistered);
        Assert.Equal(0, _blocker.Removes);

        UiApi.Hide(b);
        Assert.Equal(0, UiInputCapture.Count);
        Assert.False(_blocker.IsRegistered);
        Assert.Equal(1, _blocker.Removes);
        Assert.Contains(_log.Infos, l => l == UiInputCapture.RemovedLog);

        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public void Disposing_the_last_panel_removes_the_context()
    {
        var panel = UiApi.Panel("A", _owner, "example.ui.test");
        UiApi.Show(panel);
        Assert.True(_blocker.IsRegistered);

        panel.Dispose();

        Assert.False(_blocker.IsRegistered);
        Assert.Equal(0, UiInputCapture.Count);
        Assert.Equal(1, _blocker.Removes);
    }

    [Fact]
    public void Register_is_deferred_while_the_deny_flag_is_set_and_lands_on_the_next_pump()
    {
        _blocker.DenyModifications = true;
        var panel = UiApi.Panel("A", _owner, "example.ui.test");
        UiApi.Show(panel);

        Assert.Equal(0, _blocker.Registers);
        // Deferred means not called at all: the module must never reach the game's mutator while
        // the flag is set, not merely survive its refusal.
        Assert.Equal(0, _blocker.RegisterCalls);
        Assert.Equal(0, _blocker.RefusedRegisters);
        Assert.True(UiInputCapture.HasPendingChange);
        Assert.Contains(_log.Infos, l => l == UiInputCapture.DeferredLog(wanted: true));

        // Still inside the callback: the pump must not touch the list either.
        UiInputCapture.Pump();
        Assert.Equal(0, _blocker.Registers);

        _blocker.DenyModifications = false;
        UiInputCapture.Pump();
        Assert.Equal(1, _blocker.Registers);
        Assert.False(UiInputCapture.HasPendingChange);

        panel.Dispose();
    }

    [Fact]
    public void Removal_is_deferred_while_the_deny_flag_is_set()
    {
        var panel = UiApi.Panel("A", _owner, "example.ui.test");
        UiApi.Show(panel);
        Assert.True(_blocker.IsRegistered);

        _blocker.DenyModifications = true;
        UiApi.Hide(panel);

        Assert.True(_blocker.IsRegistered);
        Assert.Equal(0, _blocker.Removes);
        Assert.Equal(0, _blocker.RefusedRemoves);
        Assert.True(UiInputCapture.HasPendingChange);
        Assert.Contains(_log.Infos, l => l == UiInputCapture.DeferredLog(wanted: false));

        _blocker.DenyModifications = false;
        UiInputCapture.Pump();
        Assert.False(_blocker.IsRegistered);
        Assert.Equal(1, _blocker.Removes);
        Assert.False(UiInputCapture.HasPendingChange);

        panel.Dispose();
    }

    [Fact]
    public void A_change_that_reverses_before_the_pump_runs_never_touches_the_list()
    {
        _blocker.DenyModifications = true;
        var panel = UiApi.Panel("A", _owner, "example.ui.test");
        UiApi.Show(panel);
        UiApi.Hide(panel);

        _blocker.DenyModifications = false;
        UiInputCapture.Pump();

        Assert.Equal(0, _blocker.Registers);
        Assert.Equal(0, _blocker.Removes);
        Assert.False(UiInputCapture.HasPendingChange);

        panel.Dispose();
    }

    [Fact]
    public void Uninstall_removes_the_context_and_disposes_the_blocker()
    {
        var panel = UiApi.Panel("A", _owner, "example.ui.test");
        UiApi.Show(panel);
        Assert.True(_blocker.IsRegistered);

        UiApi.Uninstall();

        Assert.False(_blocker.IsRegistered);
        Assert.Equal(0, UiInputCapture.Count);
        Assert.False(UiInputCapture.IsRegistered);
        Assert.Equal(1, _blocker.Removes);
        Assert.Equal(0, _blocker.RefusedRemoves);
        Assert.False(_blocker.BlocksCameraRotation);
        Assert.Equal(1, _blocker.Disposes);
        Assert.True(panel.IsDisposed);
    }

    [Fact]
    public void A_teardown_removal_the_game_refuses_leaves_no_camera_block_behind()
    {
        // The game does NOT remove the context while _DenyModificationsOfInputContexts is set:
        // RemoveInputContext logs an error and returns, so the entry is leaked. What must not
        // survive is _BlockRotateCamera — a persistent field, not the per-frame mask — because
        // vanilla GetConsumedInputs blocks RotateCamera unconditionally while it is set, and
        // after Dispose nothing can reach that object to clear it (R31 finding 8).
        var panel = UiApi.Panel("A", _owner, "example.ui.test");
        UiApi.Show(panel);
        Assert.True(_blocker.BlocksCameraRotation);

        _blocker.DenyModifications = true;
        UiApi.Uninstall();

        Assert.Equal(1, _blocker.RefusedRemoves);
        Assert.Equal(0, _blocker.Removes);
        Assert.False(_blocker.BlocksCameraRotation);
        Assert.False(UiInputCapture.HasPendingChange);
        Assert.Equal(0, UiInputCapture.Count);
        Assert.Equal(1, _blocker.Disposes);
        Assert.Contains(_log.Warnings, l => l.Contains("refused removal during teardown", StringComparison.Ordinal)
                                            && l.Contains("_BlockRotateCamera=false", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Warnings, l => l.Contains("the mask is per-frame", StringComparison.Ordinal));
    }


    [Fact]
    public void RemoveAll_releases_the_capture_for_that_plugin()
    {
        var panel = UiApi.Panel("A", _owner, "example.ui.test");
        UiApi.Show(panel);

        UiApi.RemoveAll(_owner);

        Assert.False(_blocker.IsRegistered);
        Assert.Equal(0, UiInputCapture.Count);
    }

    [Fact]
    public void CaptureInput_false_opts_out_and_removes_a_live_context()
    {
        var panel = UiApi.Panel("A", _owner, "example.ui.test");
        UiApi.Show(panel);
        Assert.True(_blocker.IsRegistered);

        UiApi.CaptureInput(false);
        Assert.False(_blocker.IsRegistered);
        Assert.Equal(1, _blocker.Removes);
        Assert.Equal(1, UiInputCapture.Count);

        // A panel opened while opted out takes no input either.
        var b = UiApi.Panel("B", _owner, "example.ui.test");
        UiApi.Show(b);
        Assert.False(_blocker.IsRegistered);
        Assert.Equal(1, _blocker.Registers);

        UiApi.CaptureInput(true);
        Assert.True(_blocker.IsRegistered);
        Assert.Equal(2, _blocker.Registers);

        panel.Dispose();
        b.Dispose();
    }

    [Fact]
    public void A_register_that_fails_is_retried_on_a_later_frame_and_lands_when_the_world_comes_up()
    {
        // Register() fails for exactly the transient cases: no InputActionSystem yet (loading
        // screen or main menu) and no Client world. A panel shown then must not be stuck
        // uncaptured for as long as it stays open (R31 finding 22).
        _blocker.CanRegister = false;
        var panel = UiApi.Panel("A", _owner, "example.ui.test");
        UiApi.Show(panel);

        Assert.False(_blocker.IsRegistered);
        Assert.Equal(1, UiInputCapture.Count);
        Assert.True(UiInputCapture.HasPendingChange);
        Assert.Equal(1, _blocker.RegisterCalls);
        Assert.Contains(
            _log.Infos,
            l => l.Contains("input context not registered order=" + UiInputCapture.Order, StringComparison.Ordinal));

        _blocker.CanRegister = true;
        UiInputCapture.Pump();

        Assert.True(_blocker.IsRegistered);
        Assert.Equal(1, _blocker.Registers);
        Assert.False(UiInputCapture.HasPendingChange);
        Assert.Contains(_log.Infos, l => l == UiInputCapture.RegisteredLog(maskLive: true));

        panel.Dispose();
    }

    [Fact]
    public void A_register_that_never_succeeds_stops_after_a_bounded_number_of_attempts_and_says_so()
    {
        // A server or main-menu process must not retry forever, and the give-up line has to name
        // the deny flag so the reader can tell this apart from the unbounded deferral.
        _blocker.CanRegister = false;
        var panel = UiApi.Panel("A", _owner, "example.ui.test");
        UiApi.Show(panel);

        for (int i = 1; i < UiInputCapture.MaxRegisterAttempts; i++)
            UiInputCapture.Pump();

        Assert.Equal(UiInputCapture.MaxRegisterAttempts, _blocker.RegisterCalls);
        Assert.False(UiInputCapture.HasPendingChange);
        Assert.False(_blocker.IsRegistered);
        Assert.Equal(1, UiInputCapture.Count);
        Assert.Contains(_log.Warnings, l => l == UiInputCapture.GaveUpLog);
        Assert.Contains("_DenyModificationsOfInputContexts", UiInputCapture.GaveUpLog, StringComparison.Ordinal);

        // Given up means given up: the pump is disarmed and nothing calls Register again.
        UiInputCapture.Pump();
        Assert.Equal(UiInputCapture.MaxRegisterAttempts, _blocker.RegisterCalls);

        panel.Dispose();
    }

    [Fact]
    public void A_change_asked_for_off_the_main_thread_is_posted_there_not_executed()
    {
        // _DenyModificationsOfInputContexts is set only while the main thread is inside a context
        // callback, so it reads false off-thread and is no defense: an off-thread Show would
        // mutate _InputContexts under the game's own OnUpdate walk (R31 finding 23).
        MainThread.ResetForTests();
        int mainThreadId = Environment.CurrentManagedThreadId;
        UiInputCapture.MainThreadCheckForTests = () => Environment.CurrentManagedThreadId == mainThreadId;

        // A real thread, joined, so the assertions below run back on the captured main thread
        // (an awaited continuation could resume anywhere).
        var off = new Thread(() => UiInputCapture.Acquire());
        off.Start();
        off.Join();

        Assert.Equal(0, _blocker.RegisterCalls);
        Assert.Equal(1, UiInputCapture.Count);
        Assert.True(UiInputCapture.HasPendingChange);
        Assert.Contains(_log.Infos, l => l == UiInputCapture.PostedLog(wanted: true));

        // The real MainThread queue, drained on this thread, is what runs it.
        MainThread.Drain();

        Assert.Equal(1, _blocker.Registers);
        Assert.True(_blocker.IsRegistered);
        Assert.False(UiInputCapture.HasPendingChange);

        UiInputCapture.Release();
        MainThread.ResetForTests();
    }

    [Fact]
    public void A_blocker_that_re_enters_the_capture_on_the_same_thread_starts_no_second_game_call()
    {
        // What this one actually pins, and all it pins: the _inFlight guard. A re-entrant Acquire
        // raises the count, sees a pass already in the game, and returns without starting a second
        // concurrent call — the outer fold picks the new count up. It is NOT a deadlock test:
        // Gate is a Monitor and Monitor is re-entrant per thread, so a same-thread re-entrant
        // Acquire cannot block on a Gate this thread already holds. It passed identically against
        // the pre-FUI4 code that made the game call under Gate (R33 test-quality table). The
        // deadlock case needs another thread and is the test below.
        var reentrant = new ReentrantInputBlocker(() => UiInputCapture.Acquire());
        UiApi.SetInputBlockerForTests(reentrant);

        UiInputCapture.Acquire();

        Assert.Equal(1, reentrant.RegisterCalls);
        Assert.Equal(2, UiInputCapture.Count);
        Assert.True(UiInputCapture.IsRegistered);
        Assert.Equal(1, reentrant.MaxConcurrentCalls);

        UiApi.Uninstall();
    }

    [Fact]
    public void A_blocker_that_re_enters_the_capture_from_another_thread_during_Register_does_not_deadlock()
    {
        // R32 finding 13, staged for real this time. The hazard is a lock-order cycle, and a cycle
        // needs two threads: thread A is inside the game call holding CallGate, thread B wants
        // Gate. The module's one-directional rule (CallGate is never acquired while Gate is held,
        // and the game call runs with Gate released) is what keeps B running. Break it — take the
        // game call back under Gate, as the pre-FUI4 code did — and B blocks on a Gate that A will
        // not release until B finishes, which is a true deadlock: this fake's bounded Join returns
        // false, ReentrantAcquireCompleted stays false, and the assertion below fails instead of
        // hanging the run (R33 finding 12).
        var reentrant = new CrossThreadReentrantBlocker();
        UiApi.SetInputBlockerForTests(reentrant);

        UiInputCapture.Acquire();

        Assert.True(reentrant.ReentrantAcquireCompleted,
            "The Acquire issued from another thread while the blocker was inside Register never "
            + "returned: UiInputCapture is holding Gate across the game call, so CallGate and Gate "
            + "are now taken in both orders.");
        Assert.Equal(1, reentrant.RegisterCalls);
        Assert.Equal(1, reentrant.MaxConcurrentCalls);

        // The off-thread Acquire raised the count too, and one register covers both.
        Assert.Equal(2, UiInputCapture.Count);
        Assert.True(UiInputCapture.IsRegistered);

        UiApi.Uninstall();
    }

    [Fact]
    public void Chain_exhaustion_arms_a_pump_that_reconciles_the_context_with_what_is_wanted()
    {
        // R33 finding 11. A blocker that flips the refcount from inside its own game call
        // supersedes every pass, and the chain bound stops the spin — but it used to stop in
        // whatever state the eighth pass left, with no pump armed and nothing pending. Staged so
        // that state is the bad one: the context is REGISTERED and the panel count is zero, i.e.
        // sitting in InputActionSystem._InputContexts with nobody wanting it and nothing scheduled
        // to take it out. The churn budget then runs out, so the armed pass can settle.
        int passes = UiInputCapture.MaxChainedPassesForTests;
        var churner = new ChurningInputBlocker(churnLimit: passes);
        UiApi.SetInputBlockerForTests(churner);

        // Settle first, with no churn spent: registered, count 1.
        churner.Churning = false;
        UiInputCapture.Acquire();
        Assert.True(UiInputCapture.IsRegistered);

        // Now the last panel closes. Pass 1 is the Remove, so the odd passes are Removes and the
        // even ones Registers — the eighth is a Register, which is what leaves the context in the
        // list with the count back at zero.
        churner.Churning = true;
        UiInputCapture.Release();

        Assert.Equal(passes, churner.Churns);
        Assert.Contains(_log.Warnings, l => l.Contains("gave up chaining after", StringComparison.Ordinal));

        // The leak the finding describes, reached: registered, wanted by nobody.
        Assert.Equal(0, UiInputCapture.Count);
        Assert.True(UiInputCapture.IsRegistered);
        Assert.True(churner.IsRegistered);

        // ...but not left there. A next-frame pass is armed and pending, which is the fix.
        Assert.True(UiInputCapture.HasPendingChange);
        Assert.True(UiInputCapture.PumpArmedForTests);
        Assert.Contains(_log.Warnings, l => l.Contains("one reconciling pass is armed", StringComparison.Ordinal));

        // That pass reconciles registered against wanted, and the final state matches wanted.
        UiInputCapture.Pump();

        Assert.False(UiInputCapture.IsRegistered);
        Assert.False(churner.IsRegistered);
        Assert.False(churner.BlocksCameraRotation);
        Assert.Equal(0, UiInputCapture.Count);
        Assert.False(UiInputCapture.HasPendingChange);
        Assert.False(UiInputCapture.PumpArmedForTests);
        Assert.Contains(_log.Infos, l => l == UiInputCapture.RemovedLog);

        // And it stays settled: nothing re-arms on a quiet frame.
        int calls = churner.GameCalls;
        UiInputCapture.Pump();
        Assert.Equal(calls, churner.GameCalls);

        UiApi.Uninstall();
    }

    [Fact]
    public void A_blocker_that_churns_forever_is_bounded_after_a_few_exhaustions_and_then_makes_no_more_calls()
    {
        // R34 finding 15. The re-arm R33 added makes an exhaustion self-correcting, and with a
        // blocker that never stops churning it also makes it eternal: exhaust, re-arm, exhaust
        // again - eight InputActionSystem mutations and a warning line every frame, at 60 fps,
        // for the rest of the process. The churn budget here is unbounded, which is the field
        // case the old test could not stage (it capped the churn at exactly the pass bound).
        int passes = UiInputCapture.MaxChainedPassesForTests;
        int budget = UiInputCapture.MaxChainExhaustionsForTests;
        var churner = new ChurningInputBlocker(churnLimit: int.MaxValue);
        UiApi.SetInputBlockerForTests(churner);

        churner.Churning = false;
        UiInputCapture.Acquire();
        Assert.True(UiInputCapture.IsRegistered);

        int settled = churner.GameCalls;

        churner.Churning = true;
        UiInputCapture.Release();

        // The first exhaustion still re-arms: one bad frame is not a broken blocker.
        Assert.False(UiInputCapture.ChainGaveUpForTests);
        Assert.True(UiInputCapture.PumpArmedForTests);
        Assert.Equal(settled + passes, churner.GameCalls);

        // Each armed frame spends one more of the budget.
        for (int i = 2; i <= budget; i++)
            UiInputCapture.Pump();

        Assert.True(UiInputCapture.ChainGaveUpForTests);
        Assert.Equal(settled + (budget * passes), churner.GameCalls);

        // Bounded means bounded: no pump armed, nothing pending, and no further game call from
        // any path - the pump, another frame, or the churn still running inside the blocker.
        Assert.False(UiInputCapture.PumpArmedForTests);
        Assert.False(UiInputCapture.HasPendingChange);

        int calls = churner.GameCalls;
        for (int i = 0; i < 10; i++) UiInputCapture.Pump();
        UiInputCapture.Release();
        Assert.Equal(calls, churner.GameCalls);

        // One final warning that says it has stopped and what that leaves behind, and no per-frame
        // spam after it: at most one line per exhaustion, never one per frame forever.
        Assert.Contains(_log.Warnings, l => l.Contains("no further pass is armed", StringComparison.Ordinal));
        Assert.Equal(budget, _log.Warnings.Count(l => l.Contains("gave up chaining after", StringComparison.Ordinal)));

        // And the next panel open refills the budget: the module is not dead, it is quiet. With
        // the blocker behaving again, the open/close pair drives the game exactly as it always did.
        churner.Churning = false;
        UiInputCapture.Acquire();
        Assert.False(UiInputCapture.ChainGaveUpForTests);
        Assert.True(UiInputCapture.IsRegistered);

        UiInputCapture.Release();
        Assert.False(UiInputCapture.IsRegistered);
        Assert.False(churner.IsRegistered);
        Assert.False(churner.BlocksCameraRotation);
        Assert.True(churner.GameCalls > calls);
        Assert.Contains(_log.Infos, l => l == UiInputCapture.RemovedLog);

        UiApi.Uninstall();
    }

    [Fact]
    public void A_panel_closed_while_the_register_is_in_flight_is_answered_with_one_remove()
    {
        // The whole point of folding the result back under the lock with a re-check: the register
        // lands for a panel that is already gone. Without the follow-up remove that context stays
        // in InputActionSystem._InputContexts with nobody wanting it — a leak that eats input for
        // the rest of the process.
        var reentrant = new ReentrantInputBlocker(() => UiInputCapture.Release());
        UiApi.SetInputBlockerForTests(reentrant);

        UiInputCapture.Acquire();

        Assert.Equal(0, UiInputCapture.Count);
        Assert.False(UiInputCapture.IsRegistered);
        Assert.False(reentrant.IsRegistered);
        Assert.Equal(1, reentrant.RegisterCalls);
        Assert.Equal(1, reentrant.RemoveCalls);
        Assert.False(UiInputCapture.HasPendingChange);
        Assert.Contains(_log.Infos, l => l == UiInputCapture.SupersededLog(wanted: true));

        UiApi.Uninstall();
    }

    [Fact]
    public void A_second_panel_after_a_give_up_gets_a_fresh_retry_budget()
    {
        // GaveUpLog promises recovery "until it is hidden and shown again". A second panel shown
        // while the first is still up used to get exactly one attempt, because the counter was
        // only reset on success or on wanted == registered (R32 finding 13).
        _blocker.CanRegister = false;
        var a = UiApi.Panel("A", _owner, "example.ui.test");
        UiApi.Show(a);
        for (int i = 1; i < UiInputCapture.MaxRegisterAttempts; i++)
            UiInputCapture.Pump();

        Assert.Equal(UiInputCapture.MaxRegisterAttempts, _blocker.RegisterCalls);
        Assert.Contains(_log.Warnings, l => l == UiInputCapture.GaveUpLog);

        var b = UiApi.Panel("B", _owner, "example.ui.test");
        UiApi.Show(b);

        // One attempt for the show itself, then the rest of a full budget across later pumps.
        Assert.Equal(UiInputCapture.MaxRegisterAttempts + 1, _blocker.RegisterCalls);
        Assert.True(UiInputCapture.HasPendingChange);

        _blocker.CanRegister = true;
        UiInputCapture.Pump();
        Assert.True(_blocker.IsRegistered);
        Assert.Equal(1, _blocker.Registers);

        a.Dispose();
        b.Dispose();
    }

    /// <summary>
    /// A blocker that calls back into <see cref="UiInputCapture"/> from inside
    /// <see cref="Register"/> — the shape a real <c>GameInputBlocker</c> can take, because
    /// <c>RotateCameraBlocker.Register</c>, the detour install and Unity's own logging all run
    /// game code that can re-enter. It also records the deepest concurrent nesting, so a test can
    /// assert the module never has two game calls outstanding at once.
    /// </summary>
    private sealed class ReentrantInputBlocker : IUiInputBlocker
    {
        private readonly Action _duringRegister;
        private int _depth;

        public ReentrantInputBlocker(Action duringRegister) => _duringRegister = duringRegister;

        public bool IsRegistered { get; private set; }
        public bool BlocksCameraRotation { get; private set; }
        public bool DenyModifications => false;
        public int RegisterCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public int MaxConcurrentCalls { get; private set; }

        public bool Register()
        {
            RegisterCalls++;
            Enter();
            try
            {
                // The list mutation lands first, then the re-entrant call: that is the ordering
                // that produces the leak if the fold does not re-check the decision.
                IsRegistered = true;
                BlocksCameraRotation = true;
                _duringRegister();
                return true;
            }
            finally { Leave(); }
        }

        public bool Remove()
        {
            RemoveCalls++;
            Enter();
            try
            {
                IsRegistered = false;
                BlocksCameraRotation = false;
                return true;
            }
            finally { Leave(); }
        }

        /// <summary>
        /// Job FUI23: a fake records the scope rather than dropping it, so
        /// <see cref="IUiInputBlocker.AppliedScope"/> is a real measurement of this object. A fake
        /// that answered a constant would make every push look redundant and hide finding 1.
        /// </summary>
        public UiMaskScope AppliedScope { get; private set; } = UiMaskScope.Full;

        public bool MaskLive => true;

        public void SetMaskScope(UiMaskScope scope) => AppliedScope = scope;

        public void Dispose() => IsRegistered = false;

        private void Enter()
        {
            int depth = Interlocked.Increment(ref _depth);
            if (depth > MaxConcurrentCalls) MaxConcurrentCalls = depth;
        }

        private void Leave() => Interlocked.Decrement(ref _depth);
    }

    /// <summary>
    /// A blocker that re-enters <see cref="UiInputCapture"/> from a <b>second thread</b> while it
    /// is inside <see cref="Register"/>, and waits for that thread with a bound. This is the shape
    /// that turns a lock-order mistake into a real deadlock rather than a re-entrant
    /// <c>Monitor</c> acquisition: the calling thread is inside the game call and the re-entering
    /// one is not, so nothing is re-entrant about it. The bound is what keeps a broken build from
    /// hanging the test run — it fails the assertion instead.
    /// </summary>
    private sealed class CrossThreadReentrantBlocker : IUiInputBlocker
    {
        private int _depth;

        public bool IsRegistered { get; private set; }
        public bool BlocksCameraRotation { get; private set; }
        public bool DenyModifications => false;
        public int RegisterCalls { get; private set; }
        public int RemoveCalls { get; private set; }
        public int MaxConcurrentCalls { get; private set; }

        /// <summary>False when the off-thread <c>Acquire</c> did not return inside the bound.</summary>
        public bool ReentrantAcquireCompleted { get; private set; }

        public bool Register()
        {
            RegisterCalls++;
            Enter();
            try
            {
                IsRegistered = true;
                BlocksCameraRotation = true;

                // Background so a thread left stuck on Gate by a broken build cannot keep the
                // test process alive after the assertion has already failed.
                var other = new Thread(() => UiInputCapture.Acquire()) { IsBackground = true };
                other.Start();
                ReentrantAcquireCompleted = other.Join(TimeSpan.FromSeconds(5));
                return true;
            }
            finally { Leave(); }
        }

        public bool Remove()
        {
            RemoveCalls++;
            Enter();
            try
            {
                IsRegistered = false;
                BlocksCameraRotation = false;
                return true;
            }
            finally { Leave(); }
        }

        /// <summary>
        /// Job FUI23: a fake records the scope rather than dropping it, so
        /// <see cref="IUiInputBlocker.AppliedScope"/> is a real measurement of this object. A fake
        /// that answered a constant would make every push look redundant and hide finding 1.
        /// </summary>
        public UiMaskScope AppliedScope { get; private set; } = UiMaskScope.Full;

        public bool MaskLive => true;

        public void SetMaskScope(UiMaskScope scope) => AppliedScope = scope;

        public void Dispose() => IsRegistered = false;

        private void Enter()
        {
            int depth = Interlocked.Increment(ref _depth);
            if (depth > MaxConcurrentCalls) MaxConcurrentCalls = depth;
        }

        private void Leave() => Interlocked.Decrement(ref _depth);
    }

    /// <summary>
    /// A blocker that flips the panel count from inside its own game call, every call, for a fixed
    /// budget — so every pass is superseded and the chain runs to <c>MaxChainedPasses</c>. Once the
    /// budget is spent it behaves, which is what lets the pass armed after exhaustion settle. The
    /// churn is the opposite of the call it is inside (a <c>Register</c> releases, a <c>Remove</c>
    /// acquires), so consecutive passes alternate and the state at the bound is predictable.
    /// </summary>
    private sealed class ChurningInputBlocker : IUiInputBlocker
    {
        private readonly int _churnLimit;

        public ChurningInputBlocker(int churnLimit) => _churnLimit = churnLimit;

        public bool Churning { get; set; } = true;
        public bool IsRegistered { get; private set; }
        public bool BlocksCameraRotation { get; private set; }
        public bool DenyModifications => false;
        public int GameCalls { get; private set; }
        public int Churns { get; private set; }

        public bool Register()
        {
            GameCalls++;
            IsRegistered = true;
            BlocksCameraRotation = true;
            Churn(release: true);
            return true;
        }

        public bool Remove()
        {
            GameCalls++;
            IsRegistered = false;
            BlocksCameraRotation = false;
            Churn(release: false);
            return true;
        }

        /// <summary>
        /// Job FUI23: a fake records the scope rather than dropping it, so
        /// <see cref="IUiInputBlocker.AppliedScope"/> is a real measurement of this object. A fake
        /// that answered a constant would make every push look redundant and hide finding 1.
        /// </summary>
        public UiMaskScope AppliedScope { get; private set; } = UiMaskScope.Full;

        public bool MaskLive => true;

        public void SetMaskScope(UiMaskScope scope) => AppliedScope = scope;

        public void Dispose() => IsRegistered = false;

        private void Churn(bool release)
        {
            if (!Churning || Churns >= _churnLimit) return;
            Churns++;
            // The mutation lands before the flip, so the fold sees a real registration change that
            // the current wanted state no longer asks for.
            if (release) UiInputCapture.Release();
            else UiInputCapture.Acquire();
        }
    }

    /// <summary>
    /// Stands in for <c>GameInputBlocker</c>. <see cref="DenyModifications"/> is the fake's
    /// <c>InputActionSystem._DenyModificationsOfInputContexts</c>: while it is true, a real
    /// add/remove would be refused with a <c>Debug.LogError</c> and silently do nothing.
    /// </summary>
    private sealed class FakeInputBlocker : IUiInputBlocker
    {
        public bool IsRegistered { get; private set; }
        public bool BlocksCameraRotation { get; private set; }
        public bool DenyModifications { get; set; }
        public bool CanRegister { get; set; } = true;
        public int RegisterCalls { get; private set; }
        public int Registers { get; private set; }
        public int Removes { get; private set; }
        public int RefusedRegisters { get; private set; }
        public int RefusedRemoves { get; private set; }
        public int Disposes { get; private set; }
        public List<string> Calls { get; } = new();

        /// <summary>
        /// The deny flag is modeled on <b>both</b> mutators, identically and with the game's
        /// behavior: <c>AddInputContext</c> / <c>RemoveInputContext</c> log a Unity error and
        /// return without touching <c>_InputContexts</c>. Modeling it on <c>Register</c> only
        /// is what let a test assert that a forced removal succeeds under the flag — the
        /// opposite of what the game does (R31 finding 8 and the R31 test-quality row).
        /// </summary>
        public bool Register()
        {
            RegisterCalls++;
            if (DenyModifications)
            {
                // No state change: not registered, and nothing left blocking the camera. A real
                // GameInputBlocker also drops the poisoned context object here (finding 4).
                RefusedRegisters++;
                Calls.Add("register-refused");
                return false;
            }

            if (!CanRegister) return false;
            Registers++;
            IsRegistered = true;
            BlocksCameraRotation = true;
            Calls.Add("register");
            return true;
        }

        public bool Remove()
        {
            if (DenyModifications)
            {
                // The entry stays in the game's list. The contract is that it is left inert.
                RefusedRemoves++;
                BlocksCameraRotation = false;
                Calls.Add("remove-refused");
                return false;
            }

            Removes++;
            IsRegistered = false;
            BlocksCameraRotation = false;
            Calls.Add("remove");
            return true;
        }

        /// <summary>
        /// Deliberately does not clear <see cref="BlocksCameraRotation"/>: after Dispose the real
        /// blocker cannot reach a leaked context object any more, so the flag has to have been
        /// cleared by <see cref="Remove"/>, and a test asserting it after teardown must be
        /// asserting Remove's work, not Dispose's.
        /// </summary>
        /// <summary>Job FUI20: the pointer gate pushes a scope instead of a registration.</summary>
        public UiMaskScope Scope { get; private set; } = UiMaskScope.Full;

        /// <summary>
        /// Job FUI23 (R44 finding 4): what the mask is actually applying, which is the value the
        /// module now reports rather than its own copy of the decision.
        /// </summary>
        public UiMaskScope AppliedScope => Scope;

        /// <summary>
        /// Whether the detour that applies the recipe is installed (job FUI23, R44 finding 3).
        /// Settable, because the whole point of the finding is that a register can succeed on a
        /// build where the mask did not install and the log said otherwise.
        /// </summary>
        public bool MaskLive { get; set; } = true;

        public void SetMaskScope(UiMaskScope scope)
        {
            Scope = scope;
            Calls.Add("scope:" + scope);
        }

        public void Dispose()
        {
            Disposes++;
            IsRegistered = false;
            Calls.Add("dispose");
        }
    }
}
