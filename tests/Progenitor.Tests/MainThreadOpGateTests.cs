using System;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Dev;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the bridge one-op slot. Evidence 50: after a Core reload dry-run
/// unloaded Dev mid-cascade, the first later call got "MainThread work was dropped…" and every
/// subsequent call got "already running" until process restart. The gate must abort on unload
/// (and on a 30 s timeout) so the next op can enter. Serialized with the MainThread queue tests
/// because <see cref="DeferredResult.AbortAll"/> is process-wide.
/// </summary>
[Collection("MainThreadQueue")]
public class MainThreadOpGateTests
{
    [Fact]
    public void Second_enter_is_rejected_until_exit()
    {
        var gate = new MainThreadOpGate();
        Assert.True(gate.TryEnter(out int ticket, out var reject));
        Assert.Null(reject);
        Assert.True(gate.IsEntered);

        Assert.False(gate.TryEnter(out var second));
        Assert.Equal(MainThreadOpGate.AlreadyRunningMessage, second);

        gate.Exit(ticket);
        Assert.False(gate.IsEntered);
        Assert.True(gate.TryEnter(out _));
    }

    [Fact]
    public void Reloaded_instance_starts_with_a_clean_slot()
    {
        var previous = new MainThreadOpGate();
        Assert.True(previous.TryEnter(out _));
        var reloaded = new MainThreadOpGate();
        Assert.True(reloaded.TryEnter(out var reject));
        Assert.Null(reject);
        Assert.True(previous.IsEntered);
    }

    [Fact]
    public void Abort_on_unload_frees_the_slot_so_the_next_op_runs()
    {
        var gate = new MainThreadOpGate();
        Assert.True(gate.TryEnter(out _));

        var deferred = new DeferredResult(5_000);
        gate.AttachDeferred(deferred);

        gate.Abort(MainThreadOpGate.UnloadMessage);

        Assert.False(gate.IsEntered);
        Assert.True(deferred.Completion.Task.IsFaulted);
        var ex = Assert.IsType<InvalidOperationException>(deferred.Completion.Task.Exception!.InnerException);
        Assert.Equal(MainThreadOpGate.UnloadMessage, ex.Message);

        Assert.True(gate.TryEnter(out var reject));
        Assert.Null(reject);
    }

    [Fact]
    public void Exit_after_abort_does_not_wipe_the_next_enter()
    {
        var gate = new MainThreadOpGate();
        Assert.True(gate.TryEnter(out int first, out _));
        gate.Abort();
        Assert.True(gate.TryEnter(out int second, out _));
        Assert.True(gate.IsEntered);

        gate.Exit(first);
        Assert.True(gate.IsEntered);

        gate.Exit(second);
        Assert.False(gate.IsEntered);
    }

    [Fact]
    public void Expired_slot_is_not_stolen_while_the_original_work_is_held()
    {
        var gate = new MainThreadOpGate(timeoutMs: 20);
        Assert.True(gate.TryEnter(out int ticket, out _));
        var deferred = new DeferredResult(1_000);
        gate.AttachDeferred(deferred);

        Thread.Sleep(80);
        Assert.Equal(0, gate.RemainingWaitMs());

        Assert.False(gate.TryEnter(out var reject));
        Assert.Equal(MainThreadOpGate.AlreadyRunningMessage, reject);
        Assert.True(gate.IsEntered);

        gate.Abandon(ticket, MainThreadOpGate.TimeoutMessage);
        Assert.True(gate.IsAbandoned(ticket));
        Assert.True(gate.IsEntered);
        Assert.True(deferred.Completion.Task.IsFaulted);
        Assert.IsType<TimeoutException>(deferred.Completion.Task.Exception!.InnerException);

        Assert.False(gate.TryEnter(out _));
        gate.NoteRunFinished(ticket);
        Assert.False(gate.IsEntered);
        Assert.True(gate.TryEnter(out var after, out var afterReject));
        Assert.Null(afterReject);
        Assert.NotEqual(ticket, after);
    }

    [Fact]
    public void Abort_of_an_old_ticket_does_not_wipe_the_next_enter()
    {
        var gate = new MainThreadOpGate();
        Assert.True(gate.TryEnter(out int first, out _));
        gate.Abort();
        Assert.True(gate.TryEnter(out int second, out _));
        Assert.True(gate.IsEntered);

        gate.Abort(first, MainThreadOpGate.TimeoutMessage);
        Assert.True(gate.IsEntered);

        gate.Exit(second);
        Assert.False(gate.IsEntered);
    }

    [Fact]
    public void AbortAll_fails_every_live_deferred_and_clears_the_static_set()
    {
        var a = new DeferredResult(100);
        var b = new DeferredResult(100);
        Assert.True(DeferredResult.LiveCount >= 2);

        DeferredResult.AbortAll(new InvalidOperationException(MainThreadOpGate.UnloadMessage));

        Assert.Equal(0, DeferredResult.LiveCount);
        Assert.True(a.Completion.Task.IsFaulted);
        Assert.True(b.Completion.Task.IsFaulted);
        a.SetResult("late");
        b.SetException(new InvalidOperationException("late"));
        Assert.Equal(0, DeferredResult.LiveCount);
    }

    [Fact]
    public void RemainingWaitMs_is_the_timeout_budget()
    {
        var gate = new MainThreadOpGate(timeoutMs: 1_000);
        Assert.Equal(1_000, gate.RemainingWaitMs());
        Assert.True(gate.TryEnter(out _));
        Assert.InRange(gate.RemainingWaitMs(), 0, 1_000);
    }
}

/// <summary>
/// Unload-with-pending-op: DropOwnedBy of a queued <see cref="MainThread.Run{T}"/> must complete
/// the waiter with the drop error, abort the gate, and leave the next Run free to execute.
/// </summary>
[Collection("MainThreadQueue")]
public class BridgeUnloadPendingOpTests
{
    private static readonly object Gate = new();

    [Fact]
    public async Task Unload_with_pending_run_completes_the_waiter_and_the_next_op_runs()
    {
        lock (Gate)
        {
            MainThread.ResetForTests();
        }

        try
        {
            var opGate = new MainThreadOpGate();
            var owner = new DropOwner(opGate);
            Task<int> pending;
            lock (Gate)
            {
                Assert.True(opGate.TryEnter(out _));
                pending = MainThread.Run(() => 1, owner);
                Assert.False(pending.IsCompleted);

                MainThread.DropOwnedBy(owner, owner.GetType().Assembly);
            }

            var dropped = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            Assert.Equal(MainThread.DroppedBecauseUnloading, dropped.Message);
            Assert.False(opGate.IsEntered);

            Task<int> next;
            lock (Gate)
            {
                Assert.True(opGate.TryEnter(out int ticket, out var reject));
                Assert.Null(reject);
                next = MainThread.Run(() => 7, owner);
                MainThread.Drain();
                opGate.Exit(ticket);
            }

            Assert.Equal(7, await next);
        }
        finally
        {
            lock (Gate) MainThread.ResetForTests();
            DeferredResult.AbortAll(new InvalidOperationException("test teardown"));
        }
    }

    [Fact]
    public async Task Drop_of_a_post_owned_by_a_deferred_completes_it_with_the_drop_error()
    {
        lock (Gate)
        {
            MainThread.ResetForTests();
        }

        try
        {
            var deferred = new DeferredResult(30_000);
            lock (Gate)
            {
                MainThread.PostDelayed(30, () => deferred.SetResult("late"), deferred);
                Assert.False(deferred.Completion.Task.IsCompleted);
                MainThread.DropOwnedBy(deferred, deferred.GetType().Assembly);
            }

            var dropped = await Assert.ThrowsAsync<InvalidOperationException>(() => deferred.Completion.Task);
            Assert.Equal(MainThread.DroppedBecauseUnloading, dropped.Message);
        }
        finally
        {
            lock (Gate) MainThread.ResetForTests();
            DeferredResult.AbortAll(new InvalidOperationException("test teardown"));
        }
    }

    private sealed class DropOwner : IMainThreadDropTarget
    {
        private readonly MainThreadOpGate _gate;

        public DropOwner(MainThreadOpGate gate) => _gate = gate;

        public void OnMainThreadDropped(Exception exception) =>
            _gate.Abort(exception?.Message ?? MainThreadOpGate.UnloadMessage);
    }
}

/// <summary>
/// R13 finding 1: a 30 s cap must not let Drain invoke the original lambda after the
/// waiter has already failed. Serialized with the MainThread queue tests.
/// </summary>
[Collection("MainThreadQueue")]
public class MainThreadOpAbandonTests
{
    private static readonly object Gate = new();

    [Fact]
    public async Task Abandoned_run_is_skipped_on_drain_and_the_slot_is_then_freed()
    {
        lock (Gate) MainThread.ResetForTests();
        try
        {
            var opGate = new MainThreadOpGate();
            int ran = 0;
            Task<int> pending;
            lock (Gate)
            {
                Assert.True(opGate.TryEnter(out int ticket, out _));
                pending = MainThreadOpInvoke.Run(opGate, ticket, () => { ran++; return 9; }, owner: null);
                Assert.False(pending.IsCompleted);
                opGate.Abandon(ticket, MainThreadOpGate.TimeoutMessage);
                Assert.True(opGate.IsEntered);
                Assert.True(opGate.ShouldSkip(ticket));
                MainThread.Drain();
            }

            Assert.Equal(0, ran);
            Assert.Equal(0, await pending);
            Assert.False(opGate.IsEntered);
            Assert.True(opGate.TryEnter(out _));
        }
        finally
        {
            lock (Gate) MainThread.ResetForTests();
            DeferredResult.AbortAll(new InvalidOperationException("test teardown"));
        }
    }

    [Fact]
    public async Task Drop_of_an_abandoned_run_still_completes_the_waiter()
    {
        lock (Gate) MainThread.ResetForTests();
        try
        {
            var opGate = new MainThreadOpGate();
            var owner = new DropOwner(opGate);
            Task<int> pending;
            lock (Gate)
            {
                Assert.True(opGate.TryEnter(out int ticket, out _));
                pending = MainThreadOpInvoke.Run(opGate, ticket, () => 1, owner);
                opGate.Abandon(ticket, MainThreadOpGate.TimeoutMessage);
                MainThread.DropOwnedBy(owner, owner.GetType().Assembly);
            }

            var dropped = await Assert.ThrowsAsync<InvalidOperationException>(() => pending);
            Assert.Equal(MainThread.DroppedBecauseUnloading, dropped.Message);
            Assert.False(opGate.IsEntered);
        }
        finally
        {
            lock (Gate) MainThread.ResetForTests();
            DeferredResult.AbortAll(new InvalidOperationException("test teardown"));
        }
    }

    private sealed class DropOwner : IMainThreadDropTarget
    {
        private readonly MainThreadOpGate _gate;
        public DropOwner(MainThreadOpGate gate) => _gate = gate;
        public void OnMainThreadDropped(Exception exception) =>
            _gate.Abort(exception?.Message ?? MainThreadOpGate.UnloadMessage);
    }
}

public class BridgeListenPolicyTests
{
    [Fact]
    public void Bind_is_exclusive_and_does_not_reuse_the_address()
    {
        Assert.True(BridgeListenPolicy.ExclusiveAddressUse);
        Assert.False(BridgeListenPolicy.ReuseAddress);
        Assert.True(BridgeListenPolicy.BindRetryAttempts > 1);
        Assert.True(BridgeListenPolicy.BindRetryDelayMs > 0);
        Assert.True(BridgeListenPolicy.BindRetryDelayMaxMs >= BridgeListenPolicy.BindRetryDelayMs);
        Assert.Equal(0, BridgeListenPolicy.AcceptJoinTimeoutMs);
        Assert.True(BridgeListenPolicy.BindUntilCancelled);
    }

    [Fact]
    public void Chat_observe_window_stays_under_the_op_timeout_minus_slack()
    {
        Assert.Equal(50, ChatObserveWindow.MinMs);
        Assert.Equal(1_500, ChatObserveWindow.DefaultMs);
        Assert.Equal(15_000, ChatObserveWindow.MaxMs);
        Assert.True(ChatObserveWindow.MaxMs + 5_000 <= MainThreadOpGate.DefaultTimeoutMs);
        Assert.Equal(50, ChatObserveWindow.Clamp(0));
        Assert.Equal(15_000, ChatObserveWindow.Clamp(60_000));
        Assert.Equal(1_500, ChatObserveWindow.Clamp(1_500));
    }

    [Fact]
    public void Unload_abort_runs_in_finally_even_when_the_first_action_throws()
    {
        int abortAll = 0;
        int abortOps = 0;
        int stop = 0;
        DevUnload.AbortPendingInFinally(
            () => { abortAll++; throw new InvalidOperationException("shutdown"); },
            () => abortOps++,
            () => stop++);
        Assert.Equal(1, abortAll);
        Assert.Equal(1, abortOps);
        Assert.Equal(1, stop);
    }

    [Fact]
    public void Unload_drop_runs_first_even_when_it_throws()
    {
        var order = new System.Collections.Generic.List<string>();
        DevUnload.AbortPendingInFinally(
            () => order.Add("abortAll"),
            () => order.Add("abortOps"),
            () => order.Add("stop"),
            () =>
            {
                order.Add("drop");
                throw new InvalidOperationException("drop");
            });
        Assert.Equal(new[] { "drop", "abortAll", "abortOps", "stop" }, order.ToArray());
    }
}

[Collection("MainThreadQueue")]
public class DeferredTimeoutAndFinishTests
{
    private static readonly object Gate = new();

    [Fact]
    public void Finish_is_idempotent_after_the_tcs_completes()
    {
        var deferred = new DeferredResult(1_000);
        deferred.SetException(new TimeoutException(MainThreadOpGate.TimeoutMessage));
        Assert.True(deferred.Completion.Task.IsCompleted);
        Assert.False(deferred.ShouldFinish(generation: 1, currentGeneration: 1));
    }

    [Fact]
    public void Finish_skips_when_the_gate_abandoned_the_ticket()
    {
        var opGate = new MainThreadOpGate();
        Assert.True(opGate.TryEnter(out int ticket, out _));
        var deferred = new DeferredResult(1_000);
        opGate.AttachDeferred(deferred);
        opGate.Abandon(ticket, MainThreadOpGate.TimeoutMessage);
        Assert.True(deferred.Completion.Task.IsCompleted);
        Assert.True(opGate.ShouldSkip(ticket));
        Assert.False(deferred.ShouldFinish(generation: 1, currentGeneration: 1));
    }

    [Fact]
    public async Task Deferred_timeout_drops_on_the_main_thread_and_holds_the_slot_until_then()
    {
        lock (Gate) MainThread.ResetForTests();
        try
        {
            var opGate = new MainThreadOpGate();
            Assert.True(opGate.TryEnter(out int ticket, out _));
            var deferred = new DeferredResult(30_000);
            opGate.AttachDeferred(deferred);
            lock (Gate)
            {
                MainThread.PostDelayed(30, () => deferred.SetResult("late"), deferred);
                DeferredTimeout.OnTimeout(opGate, ticket, deferred, owner: null);
                Assert.True(opGate.IsEntered);
                Assert.True(opGate.IsAbandoned(ticket));
                Assert.False(opGate.TryEnter(out _));
                MainThread.Drain();
            }

            Assert.False(opGate.IsEntered);
            var dropped = await Assert.ThrowsAsync<TimeoutException>(() => deferred.Completion.Task);
            Assert.Equal(MainThreadOpGate.TimeoutMessage, dropped.Message);
        }
        finally
        {
            lock (Gate) MainThread.ResetForTests();
            DeferredResult.AbortAll(new InvalidOperationException("test teardown"));
        }
    }

    /// <summary>
    /// Deploy-hold defect (2026-10-06): a Dev build whose gate logging referenced a Core API the
    /// loaded Core lacked threw TypeLoadException from inside TryEnter, after the CAS and before
    /// the caller's try/finally, so the slot stayed "busy:reload_status#1" for the life of the
    /// process. The throw is simulated where the JIT puts it (at the Log call site, outside Log's
    /// own try). Mutation check: calling Log instead of SafeLog in TryEnter fails this test.
    /// </summary>
    [Fact]
    public void A_logging_failure_inside_TryEnter_does_not_strand_the_slot()
    {
        MainThreadOpGate.TestLogThrows = () => new TypeLoadException("Could not load type 'Progenitor.DiagnosticLog'");
        try
        {
            var gate = new MainThreadOpGate();
            Assert.True(gate.TryEnter("reload_status", 1, out int first, out var reject));
            Assert.Null(reject);
            gate.Exit(first);
            Assert.False(gate.IsEntered);

            Assert.True(gate.TryEnter("ping", 2, out int second, out reject));
            Assert.Null(reject);
            gate.Abort(second, "test");
            Assert.False(gate.IsEntered);
            Assert.Equal("free", MainThread.HeartbeatGateLabel);
        }
        finally
        {
            MainThreadOpGate.TestLogThrows = null;
        }
    }
}
