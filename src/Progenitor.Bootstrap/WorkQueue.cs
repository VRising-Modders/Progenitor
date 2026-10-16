using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;

namespace Progenitor.Bootstrap;

/// <summary>
/// Process-lifetime main-thread queue. Core's <c>Progenitor.Dev.MainThread</c> is the public
/// facade and forwards here when Bootstrap is installed. Work items store assembly/ALC name
/// strings, never live <see cref="Assembly"/> objects, so a collectible plugin context can
/// unload. Same ownership rules as Core's queue (owner instance, source assembly full name,
/// delegate collectible context). Owners that expose <c>OnMainThreadDropped(Exception)</c>
/// (Core's IMainThreadDropTarget, duck-typed via <c>ProcessHost.BindMainThreadDropTarget</c>)
/// are completed when this queue drops their work.
/// </summary>
internal static class WorkQueue
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
        /// lives in a plugin. DropCoreWork matches this so an Effects
        /// <c>PostDelayed</c> poll owned by a plugin is dropped with Core.
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
    /// Plugin and Core reloads reuse the ALC name; a new context with that name must not inherit
    /// the mark (evidence 53). Weak so the unloaded ALC is not pinned.
    /// </summary>
    private static readonly ConcurrentDictionary<string, WeakReference<AssemblyLoadContext>> RetiredContexts = new(StringComparer.Ordinal);
    private static int _mainThreadId = -1;
    private static readonly List<(double due, WorkItem item)> Delayed = new();
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    public static bool IsMainThread => Environment.CurrentManagedThreadId == _mainThreadId;

    /// <summary>
    /// Record the Unity main thread at Bootstrap <c>Load</c> so <see cref="IsMainThread"/> is
    /// true during chainloader <c>Load</c>, not only after the first <see cref="Drain"/>. A
    /// <c>Run</c> from that path otherwise queues and blocks the same thread.
    /// </summary>
    internal static void CaptureMainThread()
    {
        if (_mainThreadId < 0)
            _mainThreadId = Environment.CurrentManagedThreadId;
    }

    public static void Post(Action action, object? owner = null)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
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

    public static Task<T> Run<T>(Func<T> func, object? owner = null)
    {
        if (func == null) throw new ArgumentNullException(nameof(func));
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

    public static void PostDelayed(double seconds, Action action, object? owner = null)
    {
        if (action == null) throw new ArgumentNullException(nameof(action));
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

    public static void DropOwnedBy(object? plugin, Assembly? assembly, string? contextName = null)
    {
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

    public static void Drain()
    {
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
            catch (Exception e) { LogError($"MainThread action threw: {e}"); }
        }
    }

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
    /// Recover OnDrop / context / generation smuggled through a two-arg Post
    /// (Core <c>HandoffEnvelope</c>). Duck-typed so Bootstrap does not reference Core.
    /// </summary>
    private static bool TryUnwrapHandoff(Action action, out Action execute, out Action? onDrop, out string? contextName, out int generation)
    {
        execute = action;
        onDrop = null;
        contextName = null;
        generation = 0;
        object? target;
        try { target = action.Target; }
        catch { return false; }
        if (target == null) return false;
        Type type = target.GetType();
        PropertyInfo? marker;
        try
        {
            marker = type.GetProperty("IsMainThreadHandoff", BindingFlags.Instance | BindingFlags.Public);
        }
        catch
        {
            return false;
        }
        if (marker == null) return false;
        object? flag;
        try { flag = marker.GetValue(target); }
        catch { return false; }
        if (flag is not true) return false;

        Action? inner = ReadProp<Action>(target, type, "Execute");
        if (inner == null) return false;
        execute = inner;
        onDrop = ReadProp<Action>(target, type, "OnDrop");
        contextName = ReadProp<string>(target, type, "ContextName");
        try
        {
            var genProp = type.GetProperty("Generation", BindingFlags.Instance | BindingFlags.Public);
            if (genProp?.GetValue(target) is int gi)
                generation = gi;
        }
        catch
        {
            // Keep generation 0.
        }
        return true;
    }

    private static T? ReadProp<T>(object target, Type type, string name) where T : class
    {
        try
        {
            var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            return prop?.GetValue(target) as T;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Combine the item's own OnDrop (Run's TCS) with duck-typed
    /// <c>OnMainThreadDropped(Exception)</c> on the owner (Core's IMainThreadDropTarget)
    /// so a DeferredResult / bridge waiter completes when production-queue work is dropped.
    /// </summary>
    private static Action? ComposeDrop(object? owner, Action? onDrop)
    {
        var target = BindDropTarget(owner);
        if (target == null) return onDrop;
        return () =>
        {
            try { onDrop?.Invoke(); }
            catch (Exception e) { LogWarning($"MainThread drop callback threw: {e.Message}"); }
            try { target(DroppedException()); }
            catch (Exception e) { LogWarning($"MainThread drop target threw: {e.Message}"); }
        };
    }

    private static void FireDrop(object? owner, Action? onDrop)
    {
        var drop = ComposeDrop(owner, onDrop);
        if (drop == null) return;
        try { drop(); }
        catch (Exception e) { LogWarning($"MainThread drop callback threw: {e.Message}"); }
    }

    private static Action<Exception>? BindDropTarget(object? owner)
    {
#if PROGENITOR_TESTS
        return BindDropTargetCore(owner);
#else
        return ProcessHost.BindMainThreadDropTarget(owner);
#endif
    }

#if PROGENITOR_TESTS
    private static Action<Exception>? BindDropTargetCore(object? owner)
    {
        if (owner == null) return null;
        MethodInfo? method;
        try
        {
            method = owner.GetType().GetMethod(
                "OnMainThreadDropped",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(Exception) },
                modifiers: null);
        }
        catch
        {
            return null;
        }
        if (method == null) return null;
        return ex => method.Invoke(owner, new object[] { ex });
    }

    /// <summary>Drop leftover work, retired ALC instances, and the captured thread id so tests do not leak across facts.</summary>
    internal static void ResetForTests()
    {
        while (Queue.TryDequeue(out _)) { }
        lock (Delayed) Delayed.Clear();
        RetiredContexts.Clear();
        ContextGeneration.Clear();
        _mainThreadId = -1;
    }
#endif

    private static Assembly? AssemblyTag(object? owner, Delegate? work)
    {
        if (owner != null)
        {
            try { return owner.GetType().Assembly; }
            catch { return null; }
        }
        return DelegateAssembly(work);
    }

    private static string? DelegateContextName(Delegate? work) =>
        ReloadContextName(DelegateAssembly(work));

    private static bool MatchesOwnerOrAssembly(object? itemOwner, string? itemSourceName, object? plugin, string? unloadingName)
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

    private static void RetireNamed(string? ctx, AssemblyLoadContext? alc = null)
    {
        if (string.IsNullOrEmpty(ctx)) return;
        ContextGeneration.AddOrUpdate(ctx, 1, (_, g) => g + 1);
        if (alc != null)
        {
            RetiredContexts[ctx] = new WeakReference<AssemblyLoadContext>(alc);
            return;
        }
        if (RetiredContexts.ContainsKey(ctx)) return;
        var found = FindCollectibleAlc(ctx);
        if (found != null)
            RetiredContexts[ctx] = new WeakReference<AssemblyLoadContext>(found);
    }

    /// <summary>
    /// Allow a restored Core generation to <see cref="Post"/> again. DropOwnedBy retires the
    /// ALC <em>instance</em>; a failed collect keeps that ALC alive, so the instance must come
    /// off the retired set or every later Post from gen N is discarded. A new ALC with the
    /// same name (dry-run reload, plugin file with the same write stamp) is already allowed.
    /// </summary>
    public static void UnretireNamed(string? ctx)
    {
        if (string.IsNullOrEmpty(ctx)) return;
        RetiredContexts.TryRemove(ctx, out _);
    }

    /// <summary>Queued + delayed items whose context or source assembly matches Core.</summary>
    public static string DescribeOwned(string? contextName, string? assemblyFullName)
    {
        // Never enumerate the ConcurrentQueue: enumeration marks its segments preserved for observation, and a preserved
        // segment keeps every item dequeued after it (delegates of plugins long unloaded) referenced until the segment
        // itself is dropped. Count does not. Queued work runs next frame, so the total is enough here.
        int queuedTotal = Queue.Count;
        int delayed = 0;
        var names = new List<string>();
        lock (Delayed)
        {
            for (int i = 0; i < Delayed.Count; i++)
            {
                var item = Delayed[i].item;
                if (!MatchesCore(in item, contextName, assemblyFullName)) continue;
                delayed++;
                if (names.Count < 8)
                    names.Add(item.ContextName ?? item.SourceAssemblyName ?? "?");
            }
        }
        return "pin-diag MainThread queuedTotal=" + queuedTotal + " delayed=" + delayed
            + (names.Count == 0 ? "" : " samples=[" + string.Join(",", names) + "]");
    }

    private static bool MatchesCore(in WorkItem item, string? contextName, string? assemblyFullName)
    {
        if (!string.IsNullOrEmpty(contextName) && string.Equals(item.ContextName, contextName, StringComparison.Ordinal))
            return true;
        if (!string.IsNullOrEmpty(assemblyFullName) && NamesEqual(item.SourceAssemblyName, assemblyFullName))
            return true;
        return false;
    }

    private static bool IsRetired(object? owner, Delegate? work)
    {
        if (ContextInstanceIsRetired(AssemblyTag(owner, work))) return true;
        return ContextInstanceIsRetired(DelegateAssembly(work));
    }

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
    /// Plugin-owned Core instance methods (Effects sequence poll) match here even
    /// though <c>MatchesOwnerOrAssembly</c> prefers the owner assembly.
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
        catch (Exception e) { LogWarning($"MainThread drop callback threw: {e.Message}"); }
    }

    private static Exception DroppedException() =>
        new InvalidOperationException("MainThread work was dropped because its owning plugin is unloading");

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
        }
        return null;
    }

    private static void LogError(string message)
    {
#if !PROGENITOR_TESTS
        ProcessHost.Log?.LogError(message);
#endif
    }

    private static void LogWarning(string message)
    {
#if !PROGENITOR_TESTS
        ProcessHost.Log?.LogWarning(message);
#endif
    }
}
