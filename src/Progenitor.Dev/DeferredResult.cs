using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Progenitor.Dev;

/// <summary>
/// A main-thread op that cannot finish in the same Drain as it started. The handler returns this
/// after scheduling work with <see cref="MainThread.PostDelayed"/>; the socket thread waits on
/// <see cref="Completion"/> instead of treating the first return as the JSON result. Needed
/// because blocking the game frame (Sleep) would stall simulation, and a plain
/// <see cref="MainThread.Run{T}"/> cannot wait for a later Drain without deadlocking.
/// Live instances are tracked so Dev <c>Unload</c> can fail every waiter instead of leaving the
/// bridge blocked on a TCS whose delayed post was dropped.
/// </summary>
internal sealed class DeferredResult : IMainThreadDropTarget
{
    private static readonly ConcurrentDictionary<DeferredResult, byte> Live = new();

    public readonly TaskCompletionSource<object?> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How long the op asked to wait, in milliseconds. The bridge adds slack on top.</summary>
    public int WaitMs { get; }

    public DeferredResult(int waitMs)
    {
        WaitMs = waitMs;
        Live.TryAdd(this, 0);
    }

    public void SetResult(object? result) => Complete(tcs => tcs.TrySetResult(result));

    public void SetException(Exception exception) => Complete(tcs => tcs.TrySetException(exception));

    /// <summary>One-op ticket stamped after <see cref="MainThreadOpGate.AttachDeferred"/>.</summary>
    internal MainThreadOpGate? BoundGate { get; private set; }

    internal int BoundTicket { get; private set; }

    internal void BindGate(MainThreadOpGate gate, int ticket)
    {
        BoundGate = gate ?? throw new ArgumentNullException(nameof(gate));
        BoundTicket = ticket;
    }

    /// <summary>
    /// True when <c>chat_observe</c> Finish (or any delayed completer) should run its
    /// side effects. A completed TCS or an abandoned ticket must no-op: Disarm of a
    /// later arm is the R14 finding 4 retry hole.
    /// </summary>
    internal bool ShouldFinish(int generation, int currentGeneration)
    {
        if (Completion.Task.IsCompleted) return false;
        if (BoundGate != null && BoundGate.ShouldSkip(BoundTicket)) return false;
        return generation == currentGeneration;
    }

    /// <summary>
    /// <see cref="MainThread.DropOwnedBy"/> path: a Post/PostDelayed/Run whose owner is this
    /// deferred (or which composed this target) was discarded because the plugin is unloading.
    /// Production WorkQueue duck-types this method into each item's OnDrop. Completing here
    /// is what stops the socket thread hanging after the delayed body is gone.
    /// </summary>
    public void OnMainThreadDropped(Exception exception) => SetException(exception);

    /// <summary>How many deferred ops have not yet completed. Tests assert Unload clears this.</summary>
    internal static int LiveCount => Live.Count;

    /// <summary>
    /// Fail every still-pending deferred op. Dev <c>Unload</c> must call this so a Core swap
    /// that unloads the bridge mid-observe cannot leave <see cref="Completion"/> hanging, and
    /// so this static set cannot pin the old collectible ALC.
    /// </summary>
    internal static void AbortAll(Exception exception)
    {
        if (exception == null) throw new ArgumentNullException(nameof(exception));
        foreach (var pending in Live.Keys)
            pending.SetException(exception);
    }

    private void Complete(Action<TaskCompletionSource<object?>> setter)
    {
        try { setter(Completion); }
        finally { Live.TryRemove(this, out _); }
    }
}

/// <summary>
/// One in-flight main-thread bridge op. Instance-scoped so a reloaded Dev plugin starts clean:
/// the live failure (evidence 50) was the old generation answering
/// "MainThread work was dropped because its owning plugin is unloading" once, then
/// "a main-thread op is already running" forever, because the dropped <see cref="MainThread.Run{T}"/>
/// never cleared the slot and Unload of a static-less new instance could not see it.
///
/// The 30 s cap fails the waiter and marks the ticket abandoned so Drain will not invoke the
/// original lambda a second time. It does <em>not</em> free the slot while that lambda is still
/// queued or running (R13 finding 1): a retry would otherwise <c>TryEnter</c> and queue a second
/// <c>.ember</c> / spawn / <c>set</c>. <see cref="NoteRunFinished"/> or <see cref="Abort"/>
/// releases the slot once the stamped work has completed, skipped, or been dropped.
/// </summary>
internal sealed class MainThreadOpGate
{
    /// <summary>Hard cap for one bridge op, including deferred waits.</summary>
    public const int DefaultTimeoutMs = 30_000;

    internal const string AlreadyRunningMessage =
        "a main-thread op is already running; retry after it finishes";

    internal const string TimeoutMessage =
        "timed out waiting for the main thread (is the world running?). The waiter was failed after 30 s; the original work is abandoned and will not run a second time. The in-flight slot stays held until that work completes or is dropped.";

    internal const string UnloadMessage =
        "the debug bridge is unloading; pending main-thread op was canceled";

    private readonly int _timeoutMs;
    private int _inFlight;
    private int _epoch;
    private int _holder;
    private int _abandoned;
    private long _acquiredTicks;
    private long _enteredAtUtcTicks;
    private string _op = "";
    private long _id;
    private DeferredResult? _deferred;

    public MainThreadOpGate() : this(DefaultTimeoutMs) { }

    /// <param name="timeoutMs">Positive milliseconds; tests pass a short value so abandon is observable.</param>
    public MainThreadOpGate(int timeoutMs)
    {
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        _timeoutMs = timeoutMs;
    }

    /// <summary>True when this instance currently holds the one-op slot.</summary>
    public bool IsEntered => Volatile.Read(ref _inFlight) != 0;

    /// <summary>
    /// Take the slot. A still-held ticket — including one whose waiter already timed out —
    /// rejects the caller with <see cref="AlreadyRunningMessage"/>. The 30 s cap does not
    /// steal: that was the double-execution path (R13 finding 1).
    /// Pass the ticket to <see cref="Exit"/> / <see cref="Abort(int, string)"/> so an Unload
    /// abort cannot have that Exit wipe a later enter on the same instance.
    /// </summary>
    public bool TryEnter(out string? rejectReason) => TryEnter(out _, out rejectReason);

    public bool TryEnter(out int ticket, out string? rejectReason) =>
        TryEnter("", 0, out ticket, out rejectReason);

    public bool TryEnter(string op, long id, out int ticket, out string? rejectReason)
    {
        ticket = 0;
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            rejectReason = AlreadyRunningMessage;
            return false;
        }

        // NOTHING BETWEEN THE CAS AND THE RETURN MAY LEAVE THE SLOT HELD (deploy-hold defect,
        // 2026-10-06). The caller reaches its try/finally only after TryEnter returns, so a throw
        // here stranded _inFlight = 1 with the heartbeat on "busy:<op>#<id>", and every later op
        // was answered "already running" until the process restarted. The live throw was a
        // TypeLoadException from the gate's own logging: a Dev built against a newer Core, loaded
        // against the old one. Logging is SafeLog (it cannot throw); anything else that throws
        // releases the slot before the exception leaves.
        int next = 0;
        try
        {
            next = Interlocked.Increment(ref _epoch);
            if (next == 0) next = Interlocked.Increment(ref _epoch);
            Volatile.Write(ref _holder, next);
            Volatile.Write(ref _abandoned, 0);
            Volatile.Write(ref _acquiredTicks, Environment.TickCount64);
            Volatile.Write(ref _enteredAtUtcTicks, DateTime.UtcNow.Ticks);
            _op = op ?? "";
            Volatile.Write(ref _id, id);
            Interlocked.Exchange(ref _deferred, null);
            MainThread.HeartbeatGateLabel = "busy:" + _op + "#" + id;
        }
        catch
        {
            ReleaseAfterFailedEnter(next);
            throw;
        }
        ticket = next;
        rejectReason = null;
        SafeLog("bridge gate enter op=" + _op + " id=" + id, console: false);
        return true;
    }

    private void ReleaseAfterFailedEnter(int ticket)
    {
        if (ticket != 0) Interlocked.CompareExchange(ref _holder, 0, ticket);
        Volatile.Write(ref _abandoned, 0);
        Interlocked.Exchange(ref _inFlight, 0);
        try { MainThread.HeartbeatGateLabel = "free"; } catch { }
    }

    /// <summary>Socket-thread snapshot of the one-op slot. Does not take the gate.</summary>
    internal Snapshot Describe()
    {
        bool entered = IsEntered;
        long id = Volatile.Read(ref _id);
        string op = _op;
        long start = Volatile.Read(ref _acquiredTicks);
        long elapsed = 0;
        DateTime? at = null;
        if (entered && start != 0)
        {
            elapsed = Math.Max(0, Environment.TickCount64 - start);
            long ticks = Volatile.Read(ref _enteredAtUtcTicks);
            if (ticks > 0) at = new DateTime(ticks, DateTimeKind.Utc);
        }
        bool abandoned = entered && Volatile.Read(ref _abandoned) != 0;
        return new Snapshot
        {
            Entered = entered,
            Op = entered ? op : null,
            Id = entered ? id : 0,
            EnteredAtUtc = at,
            Abandoned = abandoned,
            ElapsedMs = elapsed,
        };
    }

    internal readonly struct Snapshot
    {
        public bool Entered { get; init; }
        public string? Op { get; init; }
        public long Id { get; init; }
        public DateTime? EnteredAtUtc { get; init; }
        public bool Abandoned { get; init; }
        public long ElapsedMs { get; init; }
    }

    /// <summary>Remember the deferred waiter so <see cref="Abort"/> / abandon can complete it.</summary>
    public void AttachDeferred(DeferredResult deferred)
    {
        if (deferred == null) throw new ArgumentNullException(nameof(deferred));
        Interlocked.Exchange(ref _deferred, deferred);
        deferred.BindGate(this, Volatile.Read(ref _holder));
    }

    /// <summary>Milliseconds left in the 30 s budget. 0 means the caller should fail immediately.</summary>
    public int RemainingWaitMs()
    {
        long start = Volatile.Read(ref _acquiredTicks);
        if (start == 0) return _timeoutMs;
        long elapsed = Environment.TickCount64 - start;
        if (elapsed < 0) return _timeoutMs;
        if (elapsed >= _timeoutMs) return 0;
        return _timeoutMs - (int)elapsed;
    }

    /// <summary>
    /// True when <paramref name="ticket"/> was abandoned by the 30 s cap. The socket waiter
    /// skips <see cref="Exit"/> in that case so the stamped lambda (or a drop) is what frees
    /// the slot.
    /// </summary>
    public bool IsAbandoned(int ticket)
    {
        if (ticket == 0) return false;
        return Volatile.Read(ref _abandoned) == ticket;
    }

    /// <summary>
    /// True when Drain must not invoke the stamped lambda: the ticket is no longer holder, or
    /// the 30 s cap abandoned it. The original work then no-ops instead of double-running.
    /// </summary>
    public bool ShouldSkip(int ticket)
    {
        if (ticket == 0) return true;
        if (Volatile.Read(ref _holder) != ticket) return true;
        return Volatile.Read(ref _abandoned) == ticket;
    }

    /// <summary>
    /// Release the slot only if <paramref name="ticket"/> is still the holder. A later
    /// <see cref="Abort"/> invalidates the ticket so this cannot clear a subsequent enter.
    /// </summary>
    public void Exit(int ticket)
    {
        if (ticket == 0) return;
        if (Interlocked.CompareExchange(ref _holder, 0, ticket) != ticket) return;
        Interlocked.Exchange(ref _deferred, null);
        bool abandoned = Volatile.Read(ref _abandoned) == ticket;
        if (abandoned)
            Volatile.Write(ref _abandoned, 0);
        long elapsed = ElapsedMs();
        string op = _op;
        long id = Volatile.Read(ref _id);
        Interlocked.Exchange(ref _inFlight, 0);
        MainThread.HeartbeatGateLabel = "free";
        SafeLog("bridge gate finish op=" + op + " id=" + id + " elapsedMs=" + elapsed + " abandoned=" + (abandoned ? "true" : "false"), console: false);
    }

    /// <summary>
    /// Stamped <see cref="MainThread.Run{T}"/> finished (ran, skipped, or threw). If the 30 s
    /// cap already abandoned this ticket, this is what frees the slot so a retry can enter.
    /// A still-live waiter owns Exit itself so a handler that returns a
    /// <see cref="DeferredResult"/> does not drop the gate mid-observe.
    /// </summary>
    public void NoteRunFinished(int ticket)
    {
        if (ticket == 0) return;
        if (Volatile.Read(ref _abandoned) == ticket)
            Exit(ticket);
    }

    /// <summary>
    /// Fail the waiter and mark the ticket abandoned without freeing the slot. The original
    /// lambda stays the holder until <see cref="NoteRunFinished"/> or <see cref="Abort"/> so a
    /// retry cannot queue a second invoke.
    /// </summary>
    public void Abandon(int ticket, string reason)
    {
        if (ticket == 0) return;
        if (Volatile.Read(ref _holder) != ticket) return;
        var message = string.IsNullOrWhiteSpace(reason) ? TimeoutMessage : reason;
        Volatile.Write(ref _abandoned, ticket);
        var leftover = Volatile.Read(ref _deferred);
        leftover?.SetException(new TimeoutException(message));
        SafeLog("bridge gate abandon op=" + _op + " id=" + Volatile.Read(ref _id) + " waitedMs=" + ElapsedMs());
    }

    /// <summary>
    /// Fail the attached deferred (if any) and free the slot. Dev Unload and a drop callback
    /// go through here so the next generation never inherits a held flag.
    /// Invalidates any in-flight <see cref="Exit"/> ticket.
    /// </summary>
    public void Abort(string reason)
    {
        var message = string.IsNullOrWhiteSpace(reason) ? UnloadMessage : reason;
        var leftover = Interlocked.Exchange(ref _deferred, null);
        leftover?.SetException(new InvalidOperationException(message));
        bool abandoned = Volatile.Read(ref _abandoned) != 0;
        long elapsed = ElapsedMs();
        string op = _op;
        long id = Volatile.Read(ref _id);
        Interlocked.Exchange(ref _abandoned, 0);
        Interlocked.Exchange(ref _holder, 0);
        int was = Interlocked.Exchange(ref _inFlight, 0);
        if (was == 0) return;
        MainThread.HeartbeatGateLabel = "free";
        SafeLog("bridge gate finish op=" + op + " id=" + id + " elapsedMs=" + elapsed + " abandoned=" + (abandoned ? "true" : "false"), console: false);
    }

    /// <summary>
    /// Ticket-scoped abort: same CAS as <see cref="Exit"/> so a late Unload abort cannot wipe
    /// a later enter on the same instance (R13 finding 1).
    /// </summary>
    public void Abort(int ticket, string reason)
    {
        if (ticket == 0)
        {
            Abort(reason);
            return;
        }

        if (Interlocked.CompareExchange(ref _holder, 0, ticket) != ticket) return;
        var message = string.IsNullOrWhiteSpace(reason) ? UnloadMessage : reason;
        var leftover = Interlocked.Exchange(ref _deferred, null);
        leftover?.SetException(new InvalidOperationException(message));
        bool abandoned = Volatile.Read(ref _abandoned) == ticket;
        if (abandoned)
            Volatile.Write(ref _abandoned, 0);
        long elapsed = ElapsedMs();
        string op = _op;
        long id = Volatile.Read(ref _id);
        Interlocked.Exchange(ref _inFlight, 0);
        MainThread.HeartbeatGateLabel = "free";
        SafeLog("bridge gate finish op=" + op + " id=" + id + " elapsedMs=" + elapsed + " abandoned=" + (abandoned ? "true" : "false"), console: false);
    }

    public void Abort() => Abort(UnloadMessage);

    private long ElapsedMs()
    {
        long start = Volatile.Read(ref _acquiredTicks);
        if (start == 0) return 0;
        long elapsed = Environment.TickCount64 - start;
        return elapsed < 0 ? 0 : elapsed;
    }

#if PROGENITOR_TESTS
    internal static Action<string>? TestLog;

    /// <summary>
    /// Test hook: thrown from <see cref="Log"/> outside its own try. That is the shape a JIT-time
    /// <c>TypeLoadException</c> takes at the call site when this Dev build's logging references a
    /// Core API the loaded Core does not have.
    /// </summary>
    internal static Func<Exception>? TestLogThrows;
#endif

    /// <summary>
    /// Every gate line goes through here. <see cref="Log"/> is a separate, non-inlined method so a
    /// JIT failure of its body (a Core API missing from the Core that is actually loaded) is thrown
    /// at THIS call site, where it is caught, and never inside <c>TryEnter</c> after the slot has
    /// been taken. Falls back to a plain BepInEx Info line.
    /// </summary>
    private static void SafeLog(string line, bool console = true)
    {
        try
        {
            Log(line, console);
        }
        catch
        {
            try { Plugin.Logger?.LogInfo(line); }
            catch { }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Log(string line, bool console = true)
    {
#if PROGENITOR_TESTS
        var fail = TestLogThrows;
        if (fail != null) throw fail();
        try { TestLog?.Invoke(line); }
        catch { }
#endif
        try
        {
            if (console) Plugin.Logger?.LogInfo(line);
            else Plugin.Logger?.LogDiag(line);
        }
        catch { }
    }
}

/// <summary>
/// Stamps a <see cref="MainThread.Run{T}"/> with the one-op ticket so Drain skips invoke
/// after the 30 s cap abandoned it, and so the slot is released when that lambda actually
/// finishes or is dropped (R13 finding 1).
/// </summary>
internal static class MainThreadOpInvoke
{
    public static Task<T> Run<T>(MainThreadOpGate gate, int ticket, Func<T> work, object? owner)
    {
        if (gate == null) throw new ArgumentNullException(nameof(gate));
        if (work == null) throw new ArgumentNullException(nameof(work));
        return MainThread.Run(() =>
        {
            try
            {
                if (gate.ShouldSkip(ticket))
                    return default!;
                return work();
            }
            finally
            {
                gate.NoteRunFinished(ticket);
            }
        }, owner);
    }
}

/// <summary>
/// Loopback bind rules. Dual-listen on Windows is what <c>ReuseAddress</c> plus
/// <c>ExclusiveAddressUse = false</c> allowed after a Core swap (R13 finding 5). Retry delay
/// must not run on the game main thread.
/// </summary>
internal static class BridgeListenPolicy
{
    public const bool ExclusiveAddressUse = true;
    public const bool ReuseAddress = false;
    /// <summary>First-wave delay stays this long; bind then backs off until cancel (R14 finding 5).</summary>
    public const int BindRetryAttempts = 25;
    public const int BindRetryDelayMs = 80;
    public const int BindRetryDelayMaxMs = 1_000;
    /// <summary>Stop does not Join the accept thread (0 = never block the Unload/main thread).</summary>
    public const int AcceptJoinTimeoutMs = 0;
    public const bool BindUntilCancelled = true;
}

/// <summary>
/// Deferred-op timeout. Abandon keeps the one-op slot; DropOwnedBy of the delayed
/// body runs on the main thread so it cannot race Drain (R14 finding 4).
/// </summary>
internal static class DeferredTimeout
{
    public static void OnTimeout(MainThreadOpGate gate, int ticket, DeferredResult deferred, object? owner)
    {
        if (gate == null) throw new ArgumentNullException(nameof(gate));
        if (deferred == null) throw new ArgumentNullException(nameof(deferred));
        gate.Abandon(ticket, MainThreadOpGate.TimeoutMessage);
        try
        {
            MainThread.Post(() =>
            {
                try { MainThread.DropOwnedBy(deferred, assembly: null); }
                catch (Exception e)
                {
                    try { Plugin.Logger?.LogWarning($"bridge: drop deferred after timeout: {e.Message}"); }
                    catch { }
                }
                gate.NoteRunFinished(ticket);
            }, owner);
        }
        catch
        {
            gate.NoteRunFinished(ticket);
        }
    }
}

/// <summary>
/// <c>chat_observe</c> window. Capped under the 30 s op budget minus slack so an observe
/// cannot outlive the waiter, free the slot, and inject a second chat line (R13 finding 1).
/// </summary>
internal static class ChatObserveWindow
{
    public const int MinMs = 50;
    public const int DefaultMs = 1500;

    /// <summary>
    /// Hard cap. Matches <c>docs/bridge-ops.md</c> (<c>chat_observe</c>). Must stay under
    /// <see cref="MainThreadOpGate.DefaultTimeoutMs"/> minus the 5 s deferred slack.
    /// </summary>
    public const int MaxMs = 15_000;

    public static int Clamp(int windowMs) => Math.Clamp(windowMs, MinMs, MaxMs);
}

/// <summary>
/// Dev <c>Unload</c> abort. Shutdown of individual ops can throw; <see cref="MainThread.DropOwnedBy"/>
/// must still run so production-queue work completes its <see cref="DeferredResult"/> with the
/// drop error, then deferred waiters and the one-op slot are failed so the socket thread and the
/// next generation are not stuck (R13 finding 6).
/// </summary>
internal static class DevUnload
{
    public static void AbortPendingInFinally(Action abortAll, Action abortOps, Action stop, Action? dropOwnedBy = null)
    {
        if (abortAll == null) throw new ArgumentNullException(nameof(abortAll));
        if (abortOps == null) throw new ArgumentNullException(nameof(abortOps));
        if (stop == null) throw new ArgumentNullException(nameof(stop));
        // Drop first so a DeferredResult completes with DroppedBecauseUnloading rather than
        // losing the race to AbortAll's unload string.
        if (dropOwnedBy != null)
        {
            try { dropOwnedBy(); } catch { }
        }
        try { abortAll(); } catch { }
        try { abortOps(); } catch { }
        try { stop(); } catch { }
    }
}
