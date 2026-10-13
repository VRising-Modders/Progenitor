using System;
using System.Collections.Generic;
using System.Reflection;
using Progenitor.Plugins;

namespace Progenitor.Events;

/// <summary>An event a handler may stop. Later handlers still see it, with <see cref="IsCancelled"/> set.</summary>
public interface ICancellable
{
    bool IsCancelled { get; }
    void Cancel();
}

/// <summary>
/// Typed publish/subscribe for game events. Progenitor installs each game hook once and republishes
/// it here, so mods subscribe instead of patching and never fight over the same Harmony target.
/// Handlers run in descending priority on the main thread, then Harmony-style before/after plugin
/// ids, then plugin load order, then registration order (see <c>docs/hooks-ordering.md</c>). A
/// before/after cycle drops only the registration that would close it; every acyclic edge at
/// that event stays. An exception in one is logged and does not stop the others. Subscriptions
/// are IDisposable so a reloadable plugin can drop them in Unload. Per-event order is
/// re-resolved on subscribe and on dispose/RemoveAll of that event type only.
/// </summary>
public static class EventBus
{
    private sealed class Subscription : IDisposable
    {
        public Type EventType = typeof(object);
        public Delegate Handler = () => { };
        public int Priority;
        public object? Owner;
        public string PluginId = "";
        public string[] Before = Array.Empty<string>();
        public string[] After = Array.Empty<string>();
        public int Index;
        public int LoadOrder;
        private bool _disposed;

        public OrderSpec ToSpec() => new(Index, PluginId, Priority, Before, After, LoadOrder);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (Lock)
            {
                if (!ByType.TryGetValue(EventType, out var list)) return;
                if (!list.Remove(this)) return;
                if (list.Count == 0) ReleaseTypeNoLock(EventType);
                else ResortNoLock(list);
            }
        }

        public void Refuse() => _disposed = true;
    }

    private static readonly object Lock = new();
    private static readonly Dictionary<Type, List<Subscription>> ByType = new();
    private static readonly HashSet<Type> NestedLogged = new();
    private static int _nextIndex;

    [ThreadStatic]
    private static HashSet<Type>? Publishing;

    /// <summary>
    /// Subscribe. Higher <paramref name="priority"/> runs first (Harmony). <paramref name="before"/>
    /// / <paramref name="after"/> are plugin ids this handler must precede or follow; missing ids
    /// are ignored. A cycle is refused with one Warn line naming this registration, which is not
    /// kept; every acyclic edge at this event stays. <paramref name="pluginId"/> is this plugin's
    /// BepInEx GUID so others can name it and so equal-priority order is load-order stable across
    /// a plugin reload (<c>ctx.Events.On</c> passes it). Omit it only for priority-only
    /// registrations. <paramref name="owner"/> lets <see cref="RemoveAll"/> drop everything a
    /// plugin registered. A refused subscribe still returns a no-op <see cref="IDisposable"/>.
    /// </summary>
    public static IDisposable On<T>(
        Action<T> handler,
        int priority = 0,
        object? owner = null,
        string[]? before = null,
        string[]? after = null,
        string? pluginId = null)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var sub = new Subscription
        {
            EventType = typeof(T),
            Handler = handler,
            Priority = priority,
            Owner = owner,
            PluginId = pluginId ?? "",
            Before = OrderSorter.CopyIds(before),
            After = OrderSorter.CopyIds(after),
            LoadOrder = OrderSorter.AssignLoadOrder(pluginId),
        };
        string? cycleLog = null;
        lock (Lock)
        {
            sub.Index = _nextIndex++;
            if (!ByType.TryGetValue(typeof(T), out var list)) ByType[typeof(T)] = list = new List<Subscription>();
            var existing = SpecsOf(list);
            if (!OrderSorter.TryAccept(existing, sub.ToSpec(), higherPriorityFirst: true, out var indices, out var droppedPlugin, out var cycleIds))
            {
                sub.Refuse();
                if (list.Count == 0) ReleaseTypeNoLock(typeof(T));
                cycleLog = EventOrder.CycleDropped(typeof(T).Name, droppedPlugin, cycleIds ?? Array.Empty<string>());
            }
            else
            {
                list.Add(sub);
                OrderSorter.ApplyPermutation(list, indices);
            }
        }
        if (cycleLog != null)
            Plugin.Logger?.LogWarning(cycleLog);
        return sub;
    }

    /// <summary>Remove every subscription registered with this owner.</summary>
    public static int RemoveAll(object owner)
    {
        int removed = 0;
        lock (Lock)
        {
            foreach (var kv in ByType)
            {
                int before = kv.Value.Count;
                kv.Value.RemoveAll(s => ReferenceEquals(s.Owner, owner));
                int n = before - kv.Value.Count;
                if (n == 0) continue;
                removed += n;
                ResortNoLock(kv.Value);
            }
            ReleaseEmptyTypesNoLock();
        }
        return removed;
    }

    /// <summary>
    /// Drop subscriptions whose owner or handler was declared in <paramref name="assembly"/>.
    /// Reload sweep uses this after <see cref="RemoveAll(object)"/> so a handler registered
    /// with <c>owner: null</c> cannot pin a collectible plugin ALC.
    /// </summary>
    public static int RemoveDeclaredIn(Assembly assembly)
    {
        if (assembly == null) return 0;
        int removed = 0;
        lock (Lock)
        {
            foreach (var kv in ByType)
            {
                int before = kv.Value.Count;
                kv.Value.RemoveAll(s =>
                    s.Handler?.Method?.DeclaringType?.Assembly == assembly
                    || (s.Owner != null && s.Owner.GetType().Assembly == assembly));
                int n = before - kv.Value.Count;
                if (n == 0) continue;
                removed += n;
                ResortNoLock(kv.Value);
            }
            ReleaseEmptyTypesNoLock();
        }
        return removed;
    }

    /// <summary>
    /// Drop every event type whose last subscription is gone. Caller holds <see cref="Lock"/>, and
    /// calls it only after enumerating <see cref="ByType"/> has finished.
    /// </summary>
    private static void ReleaseEmptyTypesNoLock()
    {
        List<Type>? empty = null;
        foreach (var pair in ByType)
            if (pair.Value.Count == 0) (empty ??= new List<Type>()).Add(pair.Key);
        if (empty == null) return;
        foreach (var type in empty) ReleaseTypeNoLock(type);
    }

    /// <summary>
    /// An event type with no subscriptions left. Its <see cref="ByType"/> key (and a
    /// <see cref="NestedLogged"/> entry) is a strong reference to the <see cref="Type"/>, which
    /// roots the collectible assembly that declared it: a plugin that defines its own event type
    /// and unsubscribes cleanly would otherwise stay pinned after unload. Caller holds
    /// <see cref="Lock"/>.
    /// </summary>
    private static void ReleaseTypeNoLock(Type type)
    {
        ByType.Remove(type);
        lock (NestedLogged) NestedLogged.Remove(type);
    }

    /// <summary>
    /// Deliver an event to its subscribers. Returns the event so callers can read cancellation.
    /// Nested Publish of the same <typeparamref name="T"/> on this thread is
    /// refused (logged once while the type stays subscribed; the note resets when its last
    /// subscription goes) so a handler that republishes cannot stack-overflow the
    /// main thread. Other event types may nest. Handlers of the outer publish still run after
    /// a refused nested call returns.
    /// </summary>
    public static T Publish<T>(T ev)
    {
        var publishing = Publishing ??= new HashSet<Type>();
        if (!publishing.Add(typeof(T)))
        {
            LogNestedOnce(typeof(T));
            return ev;
        }
        try
        {
            Subscription[] snapshot;
            lock (Lock)
            {
                if (!ByType.TryGetValue(typeof(T), out var list) || list.Count == 0) return ev;
                snapshot = list.ToArray();
            }
            // The Dev `perf` op times handlers here: this is the one funnel that still knows the
            // owning plugin id (Dev/PerfProbe.cs). No sink installed is one static read per handler.
            var sink = Dev.PerfProbe.Sink;
            foreach (var sub in snapshot)
            {
                if (sink != null)
                {
                    try { sink.BeginCallback(); }
                    catch { /* a profiler must never break an event */ }
                }
                long start = sink == null ? 0L : System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    ((Action<T>)sub.Handler)(ev);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogError($"Event handler for {typeof(T).Name} threw: {e}");
                }
                if (sink == null) continue;
                try { sink.EventHandler(sub.PluginId, typeof(T), System.Diagnostics.Stopwatch.GetTimestamp() - start); }
                catch { /* a profiler must never break an event */ }
            }
            return ev;
        }
        finally
        {
            publishing.Remove(typeof(T));
            // A handler can drop the last subscription and then publish again (refused, logged).
            // That log note must not keep the type once the outer publish is done.
            lock (Lock)
            {
                if (!ByType.ContainsKey(typeof(T)))
                    lock (NestedLogged) NestedLogged.Remove(typeof(T));
            }
        }
    }

    private static void LogNestedOnce(Type type)
    {
        lock (NestedLogged)
        {
            if (!NestedLogged.Add(type)) return;
        }
        Plugin.Logger?.LogWarning($"EventBus: refused nested Publish of {type.Name} (already publishing on this thread).");
    }

    /// <summary>
    /// Re-sort <paramref name="list"/> in place after a removal. Caller holds <see cref="Lock"/>.
    /// Remaining handlers were each accepted, so the graph is acyclic; closers are refused at
    /// subscribe, not here.
    /// </summary>
    private static void ResortNoLock(List<Subscription> list)
    {
        if (list.Count <= 1) return;
        var specs = SpecsOf(list);
        if (!OrderSorter.TryOrder(specs, higherPriorityFirst: true, out var indices, out _, out _))
            return;
        OrderSorter.ApplyPermutation(list, indices);
    }

    private static OrderSpec[] SpecsOf(List<Subscription> list)
    {
        var specs = new OrderSpec[list.Count];
        for (int i = 0; i < list.Count; i++)
            specs[i] = list[i].ToSpec();
        return specs;
    }

    public static int SubscriberCount<T>()
    {
        lock (Lock) return ByType.TryGetValue(typeof(T), out var list) ? list.Count : 0;
    }

    /// <summary>
    /// Resolved handler order for <typeparamref name="T"/> as a multi-line dump a future
    /// <c>hooks_order</c> op can print. Re-resolved on every subscribe/unload, so this is
    /// the live order Publish will use.
    /// </summary>
    public static string DescribeOrder<T>()
    {
        OrderSpec[] specs;
        lock (Lock)
        {
            if (!ByType.TryGetValue(typeof(T), out var list) || list.Count == 0)
                return EventOrder.Describe(typeof(T).Name, Array.Empty<OrderSpec>());
            specs = new OrderSpec[list.Count];
            for (int i = 0; i < list.Count; i++)
                specs[i] = list[i].ToSpec();
        }
        return EventOrder.Describe(typeof(T).Name, specs);
    }

    /// <summary>Total handlers across every event type. Used by Core-reload pin dumps.</summary>
    public static int TotalSubscribers()
    {
        lock (Lock)
        {
            int n = 0;
            foreach (var list in ByType.Values)
                n += list.Count;
            return n;
        }
    }

    /// <summary>
    /// Compact "Type=count" list after plugin sweep so a Core swap log shows what still
    /// holds this generation (Handshake, PrefabRegistry, CommandSystem, …).
    /// </summary>
    public static string Describe()
    {
        lock (Lock)
        {
            int total = 0;
            var parts = new List<string>(ByType.Count);
            foreach (var kv in ByType)
            {
                int n = kv.Value.Count;
                if (n == 0) continue;
                total += n;
                parts.Add(kv.Key.Name + "=" + n);
            }
            parts.Sort(StringComparer.Ordinal);
            return "subscribers=" + total + (parts.Count == 0 ? "" : " [" + string.Join(" ", parts) + "]");
        }
    }
}
