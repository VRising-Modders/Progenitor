using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;

namespace Progenitor.Dev;

/// <summary>
/// Optional owner contract: queued work dropped by <see cref="MainThread.DropOwnedBy"/> (or
/// refused because the owner is already retired) calls this so a waiter's TCS is not left
/// pending. <c>DeferredResult</c> implements it so a <see cref="MainThread.PostDelayed"/> whose
/// owner is the deferred completes with the drop error instead of hanging the bridge.
/// </summary>
public interface IMainThreadDropTarget
{
    /// <summary>This owner's queued work was discarded because the plugin is unloading.</summary>
    void OnMainThreadDropped(Exception exception);
}

/// <summary>
/// Runs work on the game's main thread. Anything that touches worlds, entities, or systems must go
/// through here when it originates on another thread (the debug bridge's socket thread, for one).
/// Drained once per frame from Core's tick hooks.
///
/// Each item carries an optional owner, the source assembly's full name (a string, never a live
/// <see cref="Assembly"/> — that would pin a collectible load context), and the collectible
/// load-context name taken from the <em>delegate</em> even when the owner object lives in Core.
/// The reloader drops queued work for an unloading plugin so a later Drain cannot invoke a
/// collectible assembly after its ALC is gone. Core-owned delegates (declaring type in a
/// non-collectible context) are not retired by a plugin unload.
/// </summary>
public static class MainThread
{
    private readonly struct WorkItem
    {
        public readonly Action Execute;
        public readonly Action? OnDrop;
        public readonly object? Owner;
        public readonly string? SourceAssemblyName;
        public readonly string? ContextName;
        public readonly int Generation;
        /// <summary>
        /// Declaring assembly of the posted delegate, even when <see cref="Owner"/>
        /// lives in a plugin. DropCoreWork matches this so a Core instance method
        /// posted with a plugin owner is dropped with Core.
        /// </summary>
        public readonly string? DelegateAssemblyName;

        public WorkItem(Action execute, Action? onDrop, object? owner, string? sourceAssemblyName, string? contextName, int generation, string? delegateAssemblyName)
        {
            Execute = execute;
            OnDrop = onDrop;
            Owner = owner;
            SourceAssemblyName = sourceAssemblyName;
            ContextName = contextName;
            Generation = generation;
            DelegateAssemblyName = delegateAssemblyName;
        }
    }

    private static readonly ConcurrentQueue<WorkItem> Queue = new();
    private static readonly ConcurrentDictionary<string, int> ContextGeneration = new(StringComparer.Ordinal);
    /// <summary>
    /// Collectible ALC <em>instance</em> that <see cref="DropOwnedBy"/> retired, keyed by name.
    /// A Core-swap reload of the same file reuses the ALC name (<c>progenitor-reload:Dev:{ticks}</c>);
    /// the new context is a different instance and must not inherit this mark (evidence 53).
    /// Weak so the unloaded ALC is not pinned.
    /// </summary>
    private static readonly ConcurrentDictionary<string, WeakReference<AssemblyLoadContext>> RetiredContexts = new(StringComparer.Ordinal);
    private static int _mainThreadId = -1;

    /// <summary>Drain heartbeat period in seconds. 0 disables.</summary>
    public static int HeartbeatSeconds = 5;

    /// <summary>
    /// <c>free</c> or <c>busy:&lt;op&gt;#&lt;id&gt;</c>. The Dev gate writes a string so this
    /// Core static cannot pin a collectible Dev delegate.
    /// </summary>
    public static string HeartbeatGateLabel = "free";

    /// <summary>When true, Mods Tick/Relayout log one line if they take more than 50 ms.</summary>
    public static bool UiTickTimingEnabled;

    /// <summary>Unity <c>frameCount</c> of the last heartbeat sample (0 before the first tick).</summary>
    public static int LastDrainFrame { get; internal set; }

    /// <summary>Milliseconds of the last <see cref="Drain"/> call.</summary>
    public static long LastDrainMs { get; internal set; }

    /// <summary>UTC time of the last <see cref="Drain"/>; default if Drain has not run.</summary>
    public static DateTime LastDrainAtUtc { get; internal set; }

    /// <summary>Queued and delayed depths of the live drain (leftover queue, or Bootstrap when attached).</summary>
    public static void QueueCounts(out int queued, out int delayed) =>
        DrainHeartbeat.Counts(out queued, out delayed);

    public static bool IsMainThread =>
#if !PROGENITOR_TESTS
        BootstrapLink.MainThread?.IsMainThread ??
#endif
        Environment.CurrentManagedThreadId == _mainThreadId;

    /// <summary>
    /// Queue an action for the next frame. Pass <paramref name="owner"/> (the plugin instance, or
    /// any object from that assembly) so <see cref="DropOwnedBy"/> can discard this work when the
    /// plugin unloads instead of invoking it after the collectible context is gone. The owner
    /// object is the generation token: a later load of the same plugin is a new instance and is
    /// not retired because the previous instance unloaded. A null owner still tags the work with
    /// <c>action.Method.DeclaringType.Assembly</c>'s full name so an unowned
    /// <c>Post(() => ...)</c> from a reloadable plugin is dropped with that assembly. The
    /// delegate's own collectible context is always recorded, so a plugin lambda posted with a
    /// Core owner is still dropped when that plugin unloads.
    /// </summary>
    public static void Post(Action action, object? owner = null)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
#if !PROGENITOR_TESTS
        if (BootstrapLink.MainThread is { } host)
        {
            host.Post(action, owner);
            return;
        }
#endif
        if (TryUnwrapHandoff(action, out var inner, out var onDrop, out var ctx, out var gen))
        {
            if (IsRetired(owner, inner))
            {
                FireDrop(owner, onDrop);
                return;
            }
            Queue.Enqueue(Create(inner, owner, onDrop, sourceWork: inner, ctx, gen));
            return;
        }
        if (IsRetired(owner, action))
        {
            FireDrop(owner, onDrop: null);
            return;
        }
        Queue.Enqueue(Create(action, owner, onDrop: null, sourceWork: action));
    }

    /// <summary>
    /// Run a function on the main thread and await its result from any thread.
    /// <paramref name="owner"/> is the same as on <see cref="Post"/>: dropped on unload, and a
    /// call after the owner is retired returns a faulted task instead of hanging. A null owner
    /// tags the work with <c>func.Method.DeclaringType.Assembly</c> (not the wrapper queued
    /// here) so DropOwnedBy can still drop it. The func's collectible context is recorded even
    /// when <paramref name="owner"/> lives in Core.
    /// </summary>
    public static Task<T> Run<T>(Func<T> func, object? owner = null)
    {
        if (func == null) throw new ArgumentNullException(nameof(func));
#if !PROGENITOR_TESTS
        if (BootstrapLink.MainThread is { } host)
            return host.Run(func, owner);
#endif
        if (IsRetired(owner, func))
        {
            FireDrop(owner, onDrop: null);
            return Task.FromException<T>(DroppedException());
        }

        if (IsMainThread)
        {
            try { return Task.FromResult(func()); }
            catch (Exception e) { return Task.FromException<T>(e); }
        }
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Enqueue(Create(
            () =>
            {
                try { tcs.SetResult(func()); }
                catch (Exception e) { tcs.SetException(e); }
            },
            owner,
            () => tcs.TrySetException(DroppedException()),
            sourceWork: func));
        return tcs.Task;
    }

    private static readonly List<(double due, WorkItem item)> Delayed = new();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Run an action on the main thread after a delay in seconds (checked once per frame).</summary>
    public static void PostDelayed(double seconds, Action action, object? owner = null)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
#if !PROGENITOR_TESTS
        if (BootstrapLink.MainThread is { } host)
        {
            host.PostDelayed(seconds, action, owner);
            return;
        }
#endif
        if (TryUnwrapHandoff(action, out var inner, out var onDrop, out var ctx, out var gen))
        {
            if (IsRetired(owner, inner))
            {
                FireDrop(owner, onDrop);
                return;
            }
            var handed = Create(inner, owner, onDrop, sourceWork: inner, ctx, gen);
            lock (Delayed) Delayed.Add((Clock.Elapsed.TotalSeconds + seconds, handed));
            return;
        }
        if (IsRetired(owner, action))
        {
            FireDrop(owner, onDrop: null);
            return;
        }
        var item = Create(action, owner, onDrop: null, sourceWork: action);
        lock (Delayed) Delayed.Add((Clock.Elapsed.TotalSeconds + seconds, item));
    }

    /// <summary>
    /// Drop queued and delayed work owned by this plugin (or by any object whose assembly full
    /// name matches <paramref name="assembly"/>). Completes waiting <see cref="Run{T}"/> tasks
    /// with <see cref="DroppedBecauseUnloading"/> and calls <see cref="IMainThreadDropTarget"/>
    /// on the owner so a <c>DeferredResult</c> / bridge waiter is not left hanging. Call from
    /// plugin <c>Unload</c> (Dev does this in a <c>finally</c>) and from the reloader after
    /// <c>Unload</c> — and bump the context generation — so a later Drain cannot call into the
    /// collectible ALC. Retirement is the ALC <em>instance</em> plus that generation, not the
    /// name: a reload of the same file reuses <c>progenitor-reload:…:{ticks}</c> and must be
    /// able to Post again. <paramref name="contextName"/> is the reloader ALC's name when the
    /// caller already has it; used so Retire does not depend on <c>GetLoadContext</c> succeeding
    /// for an assembly that is about to unload.
    /// </summary>
    public static void DropOwnedBy(object? plugin, Assembly? assembly, string? contextName = null)
    {
#if !PROGENITOR_TESTS
        if (BootstrapLink.MainThread is { } host)
        {
            host.DropOwnedBy(plugin, assembly, contextName);
            return;
        }
#endif
        Retire(assembly);
        RetireNamed(contextName);

        string? unloadingName = SafeFullName(assembly);
        var keep = new List<WorkItem>();
        while (Queue.TryDequeue(out var item))
        {
            if (MatchesUnload(in item, plugin, unloadingName))
                Abort(in item);
            else
                keep.Add(item);
        }
        for (int i = 0; i < keep.Count; i++)
            Queue.Enqueue(keep[i]);

        lock (Delayed)
        {
            for (int i = Delayed.Count - 1; i >= 0; i--)
            {
                var item = Delayed[i].item;
                if (!MatchesUnload(in item, plugin, unloadingName)) continue;
                Delayed.RemoveAt(i);
                Abort(in item);
            }
        }
    }

    /// <summary>
    /// Move every queued and delayed item onto <paramref name="post"/> /
    /// <paramref name="postDelayed"/> in enqueue order, then leave this queue empty.
    /// Called when Core attaches to Bootstrap so <see cref="Post"/> work enqueued before
    /// <c>TryAttach</c> (static constructors, anything earlier in <c>Plugin.Load</c>) is
    /// not stranded on this leftover list after <c>Plugin.Tick</c> stops draining it.
    /// </summary>
    internal static void HandoffPending(Action<Action, object?> post, Action<double, Action, object?> postDelayed)
    {
        if (post == null) throw new ArgumentNullException(nameof(post));
        if (postDelayed == null) throw new ArgumentNullException(nameof(postDelayed));

        var pending = new List<WorkItem>();
        while (Queue.TryDequeue(out var item))
            pending.Add(item);

        List<(double due, WorkItem item)> delayed;
        lock (Delayed)
        {
            delayed = new List<(double due, WorkItem item)>(Delayed);
            Delayed.Clear();
        }

        for (int i = 0; i < pending.Count; i++)
        {
            var item = pending[i];
            try { post(WrapForHandoff(in item), item.Owner); }
            catch (Exception e)
            {
                Queue.Enqueue(item);
                Plugin.Logger?.LogError($"MainThread handoff Post threw: {e}");
            }
        }

        double now = Clock.Elapsed.TotalSeconds;
        for (int i = 0; i < delayed.Count; i++)
        {
            var (due, item) = delayed[i];
            double remaining = due - now;
            try
            {
                var wrapped = WrapForHandoff(in item);
                if (remaining <= 0)
                    post(wrapped, item.Owner);
                else
                    postDelayed(remaining, wrapped, item.Owner);
            }
            catch (Exception e)
            {
                lock (Delayed) Delayed.Add((due, item));
                Plugin.Logger?.LogError($"MainThread handoff PostDelayed threw: {e}");
            }
        }
    }

    /// <summary>
    /// Carrier so a destination <c>Post(Action, owner)</c> (Bootstrap's two-arg seam)
    /// still receives OnDrop / ALC generation / context. WorkQueue duck-types
    /// <c>IsMainThreadHandoff</c>; Core's leftover queue unwraps the concrete type.
    /// </summary>
    internal sealed class HandoffEnvelope
    {
        public Action Execute { get; }
        public Action? OnDrop { get; }
        public string? ContextName { get; }
        public int Generation { get; }
        public bool IsMainThreadHandoff => true;

        public HandoffEnvelope(Action execute, Action? onDrop, string? contextName, int generation)
        {
            Execute = execute;
            OnDrop = onDrop;
            ContextName = contextName;
            Generation = generation;
        }

        public void Run() => Execute();
    }

    private static Action WrapForHandoff(in WorkItem item) =>
        new HandoffEnvelope(item.Execute, item.OnDrop, item.ContextName, item.Generation).Run;

    private static bool TryUnwrapHandoff(Action action, out Action execute, out Action? onDrop, out string? contextName, out int generation)
    {
        execute = action;
        onDrop = null;
        contextName = null;
        generation = 0;
        if (action.Target is HandoffEnvelope env)
        {
            execute = env.Execute;
            onDrop = env.OnDrop;
            contextName = env.ContextName;
            generation = env.Generation;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Local leftover-queue items matching a Core ALC name or assembly. When Bootstrap
    /// owns the queue this list is empty (work lives on the host); Bootstrap dumps that
    /// side from <c>WorkQueue.DescribeOwned</c>.
    /// </summary>
    internal static string DescribeOwned(string? contextNamePrefix, string? assemblyFullName)
    {
        // Never enumerate the ConcurrentQueue (PINS1): enumeration preserves its segments, and a preserved segment keeps
        // every item dequeued after it referenced, plugin delegates included, until the segment is dropped. Count does not.
        int queued = Queue.Count;
        int delayed = 0;
        lock (Delayed)
        {
            for (int i = 0; i < Delayed.Count; i++)
            {
                var item = Delayed[i].item;
                if (MatchesDescribe(in item, contextNamePrefix, assemblyFullName))
                    delayed++;
            }
        }
        return "pin-diag MainThread queuedTotal=" + queued + " delayed=" + delayed;
    }

    private static bool MatchesDescribe(in WorkItem item, string? contextPrefix, string? assemblyFullName)
    {
        if (!string.IsNullOrEmpty(contextPrefix)
            && !string.IsNullOrEmpty(item.ContextName)
            && item.ContextName.StartsWith(contextPrefix, StringComparison.Ordinal))
            return true;
        if (!string.IsNullOrEmpty(assemblyFullName)
            && string.Equals(item.SourceAssemblyName, assemblyFullName, StringComparison.Ordinal))
            return true;
        return false;
    }

#if PROGENITOR_TESTS
    /// <summary>Drop leftover work, retired ALC names, and the captured thread id so tests do not leak across facts.</summary>
    internal static void ResetForTests()
    {
        while (Queue.TryDequeue(out _)) { }
        lock (Delayed) Delayed.Clear();
        RetiredContexts.Clear();
        ContextGeneration.Clear();
        _mainThreadId = -1;
        DrainHeartbeat.ResetForTests();
        HeartbeatSeconds = 5;
        HeartbeatGateLabel = "free";
        UiTickTimingEnabled = false;
        LastDrainFrame = 0;
        LastDrainMs = 0;
        LastDrainAtUtc = default;
    }
#endif

    /// <summary>Called from the frame tick. Runs everything queued so far, each in its own try.</summary>
    internal static void Drain()
    {
        long start = Stopwatch.GetTimestamp();
#if !PROGENITOR_TESTS
        if (BootstrapLink.MainThread is { } host)
        {
            host.Drain();
            DrainHeartbeat.NoteDrain(ElapsedMs(start));
            return;
        }
#endif
        if (_mainThreadId < 0) _mainThreadId = Environment.CurrentManagedThreadId;
        if (Delayed.Count > 0)
        {
            double now = Clock.Elapsed.TotalSeconds;
            lock (Delayed)
            {
                for (int i = Delayed.Count - 1; i >= 0; i--)
                {
                    if (Delayed[i].due > now) continue;
                    Queue.Enqueue(Delayed[i].item);
                    Delayed.RemoveAt(i);
                }
            }
        }
        int budget = 256;
        while (Queue.TryDequeue(out var item))
        {
            if (IsStale(in item))
            {
                Abort(in item);
                continue;
            }
            if (budget-- <= 0)
            {
                Queue.Enqueue(item);
                break;
            }
            try { item.Execute(); }
            catch (Exception e) { Plugin.Logger?.LogError($"MainThread action threw: {e}"); }
        }
        DrainHeartbeat.NoteDrain(ElapsedMs(start));
    }

    private static long ElapsedMs(long startTimestamp) =>
        (long)((Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency);

    /// <summary>Leftover-queue depths. Production with Bootstrap attached reports 0 here; <see cref="DrainHeartbeat"/> then reads the host.</summary>
    internal static void LeftoverCounts(out int queued, out int delayed)
    {
        queued = Queue.Count;
        lock (Delayed) delayed = Delayed.Count;
    }

    /// <summary>Once-per-frame sample after Drain so a Bootstrap-owned queue still emits the heartbeat.</summary>
    internal static void NoteFrame(int frame) => DrainHeartbeat.NoteFrame(frame);

    private static WorkItem Create(Action execute, object? owner, Action? onDrop, Delegate? sourceWork) =>
        Create(execute, owner, onDrop, sourceWork, contextOverride: null, generationOverride: null);

    private static WorkItem Create(
        Action execute,
        object? owner,
        Action? onDrop,
        Delegate? sourceWork,
        string? contextOverride,
        int? generationOverride)
    {
        Delegate work = sourceWork ?? execute;
        string? sourceName = SafeFullName(AssemblyTag(owner, work));
        // Delegate's own collectible context, even when owner lives in Core. IsStale uses this
        // so a plugin lambda posted with a Core owner is dropped on that plugin's unload.
        string? ctx = contextOverride ?? DelegateContextName(work);
        int gen;
        if (generationOverride.HasValue)
            gen = generationOverride.Value;
        else
        {
            gen = 0;
            if (ctx != null) ContextGeneration.TryGetValue(ctx, out gen);
        }
        return new WorkItem(execute, ComposeDrop(owner, onDrop), owner, sourceName, ctx, gen, SafeFullName(DelegateAssembly(work)));
    }

    /// <summary>
    /// Combine the item's own OnDrop (Run's TCS) with <see cref="IMainThreadDropTarget"/> on the
    /// owner so a deferred result owned by the dropped work completes instead of hanging.
    /// Production WorkQueue composes the same contract by duck-typing
    /// <c>OnMainThreadDropped(Exception)</c>.
    /// </summary>
    private static Action? ComposeDrop(object? owner, Action? onDrop)
    {
        var target = owner as IMainThreadDropTarget;
        if (target == null) return onDrop;
        return () =>
        {
            try { onDrop?.Invoke(); }
            catch (Exception e) { Plugin.Logger?.LogWarning($"MainThread drop callback threw: {e.Message}"); }
            try { target.OnMainThreadDropped(DroppedException()); }
            catch (Exception e) { Plugin.Logger?.LogWarning($"MainThread drop target threw: {e.Message}"); }
        };
    }

    private static void FireDrop(object? owner, Action? onDrop)
    {
        var drop = ComposeDrop(owner, onDrop);
        if (drop == null) return;
        try { drop(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"MainThread drop callback threw: {e.Message}"); }
    }

    /// <summary>
    /// Assembly that owns queued work for <see cref="DropOwnedBy"/> matching. An explicit
    /// <paramref name="owner"/> keeps the existing rule (that object's type assembly). A null
    /// owner uses the delegate's declaring type so <c>Post(() => ...)</c> / <c>Run(() => ...)</c>
    /// from a reloadable plugin is still dropped when that plugin's assembly unloads. The live
    /// <see cref="Assembly"/> is only used at enqueue to take a full-name string; it is not
    /// stored on the queued item.
    /// </summary>
    internal static Assembly? AssemblyTag(object? owner, Delegate? work)
    {
        if (owner != null)
        {
            try { return owner.GetType().Assembly; }
            catch { return null; }
        }
        return DelegateAssembly(work);
    }

    /// <summary>Full name stored on a queued item for <see cref="DropOwnedBy"/> matching.</summary>
    internal static string? AssemblyNameTag(object? owner, Delegate? work) =>
        SafeFullName(AssemblyTag(owner, work));

    /// <summary>
    /// Collectible ALC name taken from <paramref name="work"/> itself, ignoring any owner. This
    /// is what a queued item records as its context so <c>IsStale</c> drops it when that
    /// context unloads even if the owner object lives in Core.
    /// </summary>
    internal static string? DelegateContextName(Delegate? work) =>
        ReloadContextName(DelegateAssembly(work));

    /// <summary>
    /// Whether one queued item should be dropped because <paramref name="plugin"/> or
    /// <paramref name="unloadingName"/> is going away. Collectible-ALC generation is handled by
    /// the caller. Owner-null items match only on the tagged source assembly full name; an
    /// explicit owner still matches by instance or by that owner's assembly full name. Core
    /// declaring-type names do not match a plugin assembly name, so Core-owned work survives.
    /// </summary>
    internal static bool MatchesOwnerOrAssembly(object? itemOwner, string? itemSourceName, object? plugin, string? unloadingName)
    {
        if (plugin != null && itemOwner != null && ReferenceEquals(itemOwner, plugin)) return true;
        if (string.IsNullOrEmpty(unloadingName)) return false;
        if (itemOwner != null)
        {
            try { return NamesEqual(SafeFullName(itemOwner.GetType().Assembly), unloadingName); }
            catch { return false; }
        }
        return NamesEqual(itemSourceName, unloadingName);
    }

    private static Assembly? DelegateAssembly(Delegate? work)
    {
        if (work == null) return null;
        try { return work.Method.DeclaringType?.Assembly; }
        catch { return null; }
    }

    private static void Retire(Assembly? assembly)
    {
        var alc = GetCollectibleAlc(assembly);
        RetireNamed(alc?.Name ?? ReloadContextName(assembly), alc);
    }

    /// <summary>
    /// Reverse a collectible-ALC <see cref="DropOwnedBy"/> so a restored generation (same
    /// ALC instance, failed collect) can Post again. A <em>new</em> ALC with the same name
    /// is already allowed: retirement is the instance, not the name.
    /// </summary>
    internal static void UnretireNamed(string? ctx)
    {
        if (string.IsNullOrEmpty(ctx)) return;
        RetiredContexts.TryRemove(ctx, out _);
#if PROGENITOR_TESTS
        global::Progenitor.Bootstrap.WorkQueue.UnretireNamed(ctx);
#else
        ForwardUnretireToHost(ctx);
#endif
    }

#if !PROGENITOR_TESTS
    /// <summary>
    /// Production restore talks to WorkQueue directly; a Core caller of this method
    /// must still un-retire the process-lifetime queue (R14 finding 6).
    /// </summary>
    private static void ForwardUnretireToHost(string ctx)
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(asm.GetName().Name, "Progenitor.Bootstrap", StringComparison.Ordinal))
                    continue;
                var type = asm.GetType("Progenitor.Bootstrap.ProcessHost");
                var method = type?.GetMethod("UnretireContext", BindingFlags.Public | BindingFlags.Static);
                method?.Invoke(null, new object?[] { ctx });
                return;
            }
        }
        catch
        {
            // Best-effort: leftover-queue un-retire already ran.
        }
    }
#endif

    private static void RetireNamed(string? ctx, AssemblyLoadContext? alc = null)
    {
        if (string.IsNullOrEmpty(ctx)) return;
        ContextGeneration.AddOrUpdate(ctx, 1, (_, g) => g + 1);
        if (alc != null)
        {
            // Explicit instance (from the unloading assembly) always wins. A later
            // name-only RetireNamed from the same DropOwnedBy must not FindAlc a
            // reload that is already in flight.
            RetiredContexts[ctx] = new WeakReference<AssemblyLoadContext>(alc);
            return;
        }
        if (RetiredContexts.ContainsKey(ctx)) return;
        var found = FindCollectibleAlc(ctx);
        if (found != null)
            RetiredContexts[ctx] = new WeakReference<AssemblyLoadContext>(found);
    }

    private static bool IsRetired(object? owner, Delegate? work)
    {
        if (ContextInstanceIsRetired(AssemblyTag(owner, work))) return true;
        return ContextInstanceIsRetired(DelegateAssembly(work));
    }

    /// <summary>
    /// True only when <paramref name="assembly"/> still lives in the ALC instance
    /// <see cref="DropOwnedBy"/> retired. A new collectible context with the same name
    /// (Core-swap reload of the same file write stamp) is not retired.
    /// </summary>
    private static bool ContextInstanceIsRetired(Assembly? assembly)
    {
        var alc = GetCollectibleAlc(assembly);
        if (alc == null) return false;
        var name = alc.Name;
        if (string.IsNullOrEmpty(name)) return false;
        if (!RetiredContexts.TryGetValue(name, out var weak)) return false;
        if (!weak.TryGetTarget(out var retired) || retired == null)
        {
            RetiredContexts.TryRemove(name, out _);
            return false;
        }
        return ReferenceEquals(alc, retired);
    }

    private static bool IsStale(in WorkItem item)
    {
        if (item.ContextName == null) return false;
        return ContextGeneration.TryGetValue(item.ContextName, out var now) && item.Generation < now;
    }

    private static bool MatchesUnload(in WorkItem item, object? plugin, string? unloadingName)
    {
        if (IsStale(in item)) return true;
        if (MatchesOwnerOrAssembly(item.Owner, item.SourceAssemblyName, plugin, unloadingName))
            return true;
        return MatchesDelegateAssembly(item.DelegateAssemblyName, unloadingName);
    }

    /// <summary>
    /// True when the posted delegate was declared in <paramref name="unloadingName"/>.
    /// Plugin-owned Core instance methods match here even though
    /// <see cref="MatchesOwnerOrAssembly"/> prefers the owner assembly.
    /// </summary>
    internal static bool MatchesDelegateAssembly(string? delegateAssemblyName, string? unloadingName)
    {
        if (string.IsNullOrEmpty(unloadingName) || string.IsNullOrEmpty(delegateAssemblyName))
            return false;
        return NamesEqual(delegateAssemblyName, unloadingName);
    }

    private static void Abort(in WorkItem item)
    {
        try { item.OnDrop?.Invoke(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"MainThread drop callback threw: {e.Message}"); }
    }

    /// <summary>
    /// Exception message used when <see cref="DropOwnedBy"/> discards queued work. A
    /// <see cref="DeferredResult"/> / <see cref="Run{T}"/> waiter should complete with this
    /// rather than hang across a plugin unload.
    /// </summary>
    public const string DroppedBecauseUnloading =
        "MainThread work was dropped because its owning plugin is unloading";

    private static Exception DroppedException() =>
        new InvalidOperationException(DroppedBecauseUnloading);

    private static string? SafeFullName(Assembly? assembly)
    {
        if (assembly == null) return null;
        try
        {
            var name = assembly.FullName;
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private static bool NamesEqual(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>
    /// Collectible load-context name, or null for the default context. Reloader ALCs are named
    /// <c>progenitor-reload:...</c>; storing the name (not the Assembly) does not pin the ALC.
    /// </summary>
    private static string? ReloadContextName(Assembly? assembly)
    {
        var alc = GetCollectibleAlc(assembly);
        if (alc == null) return null;
        var name = alc.Name;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static AssemblyLoadContext? GetCollectibleAlc(Assembly? assembly)
    {
        if (assembly == null) return null;
        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(assembly);
            if (alc == null || !alc.IsCollectible) return null;
            return alc;
        }
        catch
        {
            return null;
        }
    }

    private static AssemblyLoadContext? FindCollectibleAlc(string name)
    {
        try
        {
            foreach (var alc in AssemblyLoadContext.All)
            {
                if (alc == null || !alc.IsCollectible) continue;
                if (string.Equals(alc.Name, name, StringComparison.Ordinal))
                    return alc;
            }
        }
        catch
        {
            // Enumeration can throw if a context unloads mid-walk.
        }
        return null;
    }
}
