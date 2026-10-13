using System;
using System.Collections.Generic;
using System.Threading;
using ProjectM;
using Progenitor.Events;
using Progenitor.Hooks;
using Progenitor.Plugins;
using Unity.Entities;

namespace Progenitor.Systems;

/// <summary>
/// Named managed callbacks that run from a single <see cref="SystemHooks"/> Before/After per
/// vanilla system type. This is <c>ctx.Systems.Add</c>: work that belongs in a group (or after a
/// specific system), not an injected <c>SystemBase</c>. Reload is dispose. One Harmony relay per
/// distinct (type, phase); N plugins stacking on the same anchor share it and run in
/// (order, registration) sequence with per-callback exception isolation. Relays are refcounted
/// per anchor and disposed when that anchor's live slot count hits 0.
/// </summary>
public static class VirtualSystems
{
    /// <summary>
    /// Plugin.Frame ticks to wait, after the side's world exists, for
    /// <see cref="SimulationSystemGroup"/> before assuming that group does not go through
    /// <c>SystemBase.Update</c> on this build, and falling back to After/Before
    /// <see cref="ServerBootstrapSystem"/> / <see cref="ClientBootstrapSystem"/>.
    /// </summary>
    public const int DefaultAnchorFallbackTicks = DefaultAnchorWatch.FallbackTicks;

    internal const string DefaultAfterKey = "default:after-sim";
    internal const string DefaultBeforeKey = "default:before-sim";

    private static readonly object Gate = new();
    private static readonly VirtualSystemDispatcher Dispatcher = new();
    private static readonly Dictionary<RelayId, Relay> Relays = new();
    private static readonly DefaultAnchorWatch Watch = new();

    private static IDisposable? _fallbackAfterServer;
    private static IDisposable? _fallbackAfterClient;
    private static IDisposable? _fallbackBeforeServer;
    private static IDisposable? _fallbackBeforeClient;
    private static IDisposable? _worldReadySub;
    private static bool _frameHooked;

    /// <summary>
    /// Called from Core Install so SkipCoreModules can log a skip. Relays are created
    /// on first <see cref="Add"/>; skip makes Add a no-op so Plugin.Frame is never hooked.
    /// </summary>
    internal static void Install()
    {
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.SystemsModule))
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(HarmonySlotHandoff.SystemsModule));
    }

    /// <summary>
    /// What this module registered that can outlive a collectible Core generation
    /// (Plugin.Frame fallback watch, EventBus, SystemHooks relays).
    /// </summary>
    internal static string DescribePins()
    {
        var holds = new List<string>();
        try
        {
            if (_frameHooked) holds.Add("Plugin.Frame");
            if (_worldReadySub != null) holds.Add("EventBus.WorldReady");
            if (_fallbackAfterServer != null) holds.Add("fallbackAfterServer");
            if (_fallbackAfterClient != null) holds.Add("fallbackAfterClient");
            if (_fallbackBeforeServer != null) holds.Add("fallbackBeforeServer");
            if (_fallbackBeforeClient != null) holds.Add("fallbackBeforeClient");
            holds.Add(Describe());
        }
        catch (Exception e)
        {
            holds.Add("threw:" + e.Message);
        }
        return HarmonySlotHandoff.FormatModulePins(HarmonySlotHandoff.SystemsModule, holds);
    }

    /// <summary>
    /// Run <paramref name="onUpdate"/> after <see cref="SimulationSystemGroup"/> (with the
    /// bootstrap-system fallback). Dispose or <see cref="RemoveAll"/> to stop.
    /// </summary>
    public static IDisposable Add(string name, Action<VirtualSystemContext> onUpdate, object owner, int order = 0)
        => Add(name, VirtualSystemAnchor.AfterSimulation, onUpdate, owner, order);

    /// <summary>
    /// Run <paramref name="onUpdate"/> at <paramref name="anchor"/>. Does not inject a type or
    /// create a game system. Duplicate <paramref name="name"/> from the same
    /// <paramref name="owner"/> replaces. Lower <paramref name="order"/> runs first.
    /// </summary>
    public static IDisposable Add(
        string name,
        VirtualSystemAnchor anchor,
        Action<VirtualSystemContext> onUpdate,
        object owner,
        int order = 0)
    {
        if (onUpdate == null) throw new ArgumentNullException(nameof(onUpdate));
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.SystemsModule))
            return ClosedRegistration.Instance;
        if (anchor.SystemType == null)
            throw new ArgumentException("Anchor is missing a system type.", nameof(anchor));

        var side = anchor.Side;
        var key = anchor.DispatcherKey;
        var display = anchor.DisplayName;

        var inner = Dispatcher.Add(name, key, slot =>
        {
            var world = DispatchAmbient.World as World;
            if (world == null) return;
            if (!WorldMatches(side, world)) return;
            var ctx = new VirtualSystemContext(world, slot, DispatchAmbient.Delta);
            onUpdate(ctx);
        }, owner, order, display);

        try
        {
            EnsureRelay(anchor.SystemType, anchor.IsBefore);
            if (anchor.IsDefaultSimulation)
            {
                if (Watch.UseFallback)
                    InstallFallbackHooks();
                else if (!Watch.GroupFired)
                    EnsureFallbackWatch();
            }
        }
        catch
        {
            inner.Dispose();
            ReleaseIdleRelays();
            throw;
        }

        return new Registration(inner);
    }

    /// <summary>Named After on a vanilla system. Thin wrapper over <see cref="Add"/> with <see cref="VirtualSystemAnchor.After{TSystem}"/>.</summary>
    public static IDisposable AddAfter<TSystem>(string name, Action<VirtualSystemContext> onUpdate, object owner, int order = 0)
        => Add(name, VirtualSystemAnchor.After<TSystem>(), onUpdate, owner, order);

    /// <summary>Named Before on a vanilla system. Thin wrapper over <see cref="Add"/> with <see cref="VirtualSystemAnchor.Before{TSystem}"/>.</summary>
    public static IDisposable AddBefore<TSystem>(string name, Action<VirtualSystemContext> onUpdate, object owner, int order = 0)
        => Add(name, VirtualSystemAnchor.Before<TSystem>(), onUpdate, owner, order);

    /// <summary>Remove every virtual system registered with this owner. Returns how many were dropped.</summary>
    public static int RemoveAll(object owner)
    {
        int n = Dispatcher.RemoveAll(owner);
        ReleaseIdleRelays();
        return n;
    }

    /// <summary>
    /// Diagnostic string for the debug-bridge status op: which default-simulation anchor is live
    /// (group / bootstrap / pending), then each live slot (name, anchor, order, frames, last
    /// duration, last error).
    /// </summary>
    public static string Describe()
    {
        try
        {
            return "fallback=" + Watch.DescribeLabel()
                   + " pluginFrames=" + Watch.FramesAfterWorld
                   + " groupFired=" + Watch.GroupFired
                   + " worldReady=" + Watch.WorldExists
                   + "; " + Dispatcher.Describe();
        }
        catch (Exception e)
        {
            return "VirtualSystems.Describe threw: " + e.Message;
        }
    }

    internal static string ExplicitKey(bool before, Type type)
        => (before ? "Before:" : "After:") + (type.FullName ?? type.Name);

    // ---- relays -------------------------------------------------------------------------------

    private static void EnsureRelay(Type systemType, bool before)
    {
        var id = new RelayId(systemType, before);
        lock (Gate)
        {
            if (Relays.ContainsKey(id)) return;

            Action<SystemHookContext> handler = hook => OnRelay(systemType, before, hook);
            IDisposable hook = before
                ? SystemHooks.Before(systemType, handler)
                : SystemHooks.After(systemType, handler);
            Relays[id] = new Relay(hook);
        }
    }

    private static void OnRelay(Type systemType, bool before, SystemHookContext hook)
    {
        try
        {
            if (systemType == typeof(SimulationSystemGroup))
            {
                bool recovered = Watch.NotifyGroupFired();
                if (recovered)
                {
                    Plugin.Logger?.LogInfo(
                        "VirtualSystems: SimulationSystemGroup ticked; default anchors switched back from bootstrap fallback.");
                    ReleaseIdleRelays();
                    // Skip default-key dispatch this tick: bootstrap children of this group
                    // already ran the fallback seam. Next group After/Before is the live anchor.
                }
                else if (!Watch.UseFallback)
                {
                    DispatchKey(before ? DefaultBeforeKey : DefaultAfterKey, hook.World);
                }
            }

            DispatchKey(ExplicitKey(before, systemType), hook.World);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"VirtualSystems: relay {systemType.Name} threw: {e}");
        }
    }

    private static void OnFallback(bool before, SystemHookContext hook)
    {
        try
        {
            if (!Watch.UseFallback) return;
            DispatchKey(before ? DefaultBeforeKey : DefaultAfterKey, hook.World);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"VirtualSystems: bootstrap fallback threw: {e}");
        }
    }

    private static void DispatchKey(string key, World world)
    {
        if (world == null || !world.IsCreated) return;
        // Save/restore, do not null: a nested relay (Harmony re-entry, forced Update) runs
        // DispatchKey on this thread. Nulling in finally would drop the outer world, and
        // later slots of the outer snapshot would skip in the Add wrapper.
        var previous = DispatchAmbient.Push(world, ReadDelta(world));
        try
        {
            Dispatcher.Dispatch(key);
        }
        finally
        {
            DispatchAmbient.Restore(previous);
        }
    }

    private static float ReadDelta(World world)
    {
        try
        {
            return world.Time.DeltaTime;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"VirtualSystems: World.Time.DeltaTime threw: {e}");
            return 0f;
        }
    }

    private static bool WorldMatches(Side side, World world)
    {
        try
        {
            if (world == null || !world.IsCreated) return false;
            string name = world.Name;
            if ((side & Side.Server) != 0 && name == Worlds.ServerName) return true;
            if ((side & Side.Client) != 0 && name == Worlds.ClientName) return true;
            return false;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"VirtualSystems: world filter threw: {e}");
            return false;
        }
    }

    // ---- fallback -----------------------------------------------------------------------------

    private static void EnsureFallbackWatch()
    {
        bool worldExists;
        lock (Gate)
        {
            if (_frameHooked) return;
            Plugin.Frame += OnPluginFrame;
            try
            {
                _worldReadySub = EventBus.On<WorldReady>(OnWorldReady);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"VirtualSystems: WorldReady subscribe threw: {e}");
            }

            _frameHooked = true;
            worldExists = SideWorldExists();
        }

        // Watch lock is separate from Gate; do not take it while holding Gate.
        if (worldExists)
            Watch.NotifyWorldReady();
    }

    private static void OnWorldReady(WorldReady _)
    {
        try
        {
            Watch.NotifyWorldReady();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"VirtualSystems: WorldReady watch threw: {e}");
        }
    }

    private static void OnPluginFrame()
    {
        try
        {
            if (Watch.GroupFired || Watch.UseFallback)
            {
                lock (Gate) UnhookFallbackWatch_NoLock();
                return;
            }

            bool engaged = Watch.NotifyFrame(SideWorldExists());
            if (!engaged) return;

            Plugin.Logger?.LogWarning(
                "VirtualSystems: SimulationSystemGroup never ticked within "
                + DefaultAnchorFallbackTicks
                + " Plugin.Frame ticks after the world existed; default virtual systems now run After/Before "
                + "ServerBootstrapSystem (server) and ClientBootstrapSystem (client). "
                + "Groups may not go through SystemBase.Update on this build.");

            InstallFallbackHooks();
            lock (Gate) UnhookFallbackWatch_NoLock();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"VirtualSystems: fallback watch threw: {e}");
        }
    }

    private static bool SideWorldExists()
    {
        try
        {
            var server = Worlds.Server;
            if (server != null && server.IsCreated) return true;
            var client = Worlds.Client;
            return client != null && client.IsCreated;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"VirtualSystems: world lookup threw: {e}");
            return false;
        }
    }

    private static void InstallFallbackHooks()
    {
        lock (Gate)
        {
            try
            {
                _fallbackAfterServer ??= SystemHooks.After<ServerBootstrapSystem>(hook => OnFallback(false, hook));
                _fallbackAfterClient ??= SystemHooks.After<ClientBootstrapSystem>(hook => OnFallback(false, hook));
                _fallbackBeforeServer ??= SystemHooks.Before<ServerBootstrapSystem>(hook => OnFallback(true, hook));
                _fallbackBeforeClient ??= SystemHooks.Before<ClientBootstrapSystem>(hook => OnFallback(true, hook));
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"VirtualSystems: installing bootstrap fallback hooks threw: {e}");
            }
        }
    }

    /// <summary>
    /// Drop any relay whose live slot count is 0, and drop bootstrap fallback hooks when they
    /// are not the live default-simulation anchor (or when no default slot remains).
    /// </summary>
    private static void ReleaseIdleRelays()
    {
        List<IDisposable>? toDispose = null;
        lock (Gate)
        {
            List<RelayId>? dead = null;
            foreach (var kv in Relays)
            {
                int n = LiveCountForRelay(kv.Key);
                kv.Value.Count = n;
                if (n > 0) continue;
                dead ??= new List<RelayId>();
                toDispose ??= new List<IDisposable>();
                dead.Add(kv.Key);
                toDispose.Add(kv.Value.Hook);
            }

            if (dead != null)
            {
                for (int i = 0; i < dead.Count; i++)
                    Relays.Remove(dead[i]);
            }

            bool anyDefault = Dispatcher.CountFor(DefaultAfterKey) > 0
                              || Dispatcher.CountFor(DefaultBeforeKey) > 0;
            if (!anyDefault)
                UnhookFallbackWatch_NoLock();

            // Keep bootstrap relays only while fallback is the live anchor and someone is listening.
            if (!Watch.UseFallback || !anyDefault)
                CollectFallbacks(ref toDispose);
        }

        if (toDispose == null) return;
        for (int i = 0; i < toDispose.Count; i++)
            DisposeQuiet(toDispose[i]);
    }

    /// <summary>
    /// Slots that keep this relay alive: explicit Before/After of the type, plus the default
    /// simulation keys when the type is <see cref="SimulationSystemGroup"/> (those share the group
    /// relay so we can observe it firing and switch back from fallback).
    /// </summary>
    private static int LiveCountForRelay(RelayId id)
    {
        int n = Dispatcher.CountFor(ExplicitKey(id.Before, id.Type));
        if (id.Type == typeof(SimulationSystemGroup))
            n += Dispatcher.CountFor(id.Before ? DefaultBeforeKey : DefaultAfterKey);
        return n;
    }

    private static void UnhookFallbackWatch_NoLock()
    {
        if (!_frameHooked) return;
        Plugin.Frame -= OnPluginFrame;
        var sub = _worldReadySub;
        _worldReadySub = null;
        _frameHooked = false;
        if (sub == null) return;
        try { sub.Dispose(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"VirtualSystems: WorldReady unsubscribe threw: {e}");
        }
    }

    private static void CollectFallbacks(ref List<IDisposable>? into)
    {
        CollectFallback(ref into, ref _fallbackAfterServer);
        CollectFallback(ref into, ref _fallbackAfterClient);
        CollectFallback(ref into, ref _fallbackBeforeServer);
        CollectFallback(ref into, ref _fallbackBeforeClient);
    }

    private static void CollectFallback(ref List<IDisposable>? into, ref IDisposable? hook)
    {
        if (hook == null) return;
        into ??= new List<IDisposable>();
        into.Add(hook);
        hook = null;
    }

    private static void DisposeQuiet(IDisposable hook)
    {
        try { hook.Dispose(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"VirtualSystems: relay dispose threw: {e}");
        }
    }

    private readonly struct RelayId : IEquatable<RelayId>
    {
        public readonly Type Type;
        public readonly bool Before;

        public RelayId(Type type, bool before)
        {
            Type = type;
            Before = before;
        }

        public bool Equals(RelayId other) => Type == other.Type && Before == other.Before;
        public override bool Equals(object? obj) => obj is RelayId other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Type, Before);
    }

    /// <summary>
    /// One SystemHooks registration, kept while <see cref="Count"/> (live slots that need this
    /// anchor) is greater than 0. Count is rewritten from the dispatcher snapshot on each
    /// <see cref="ReleaseIdleRelays"/> pass.
    /// </summary>
    private sealed class Relay
    {
        public readonly IDisposable Hook;
        public int Count;
        public Relay(IDisposable hook) => Hook = hook;
    }

    private sealed class ClosedRegistration : IDisposable
    {
        public static readonly ClosedRegistration Instance = new();
        public void Dispose() { }
    }

    private sealed class Registration : IDisposable
    {
        private IDisposable? _inner;

        public Registration(IDisposable inner) => _inner = inner;

        public void Dispose()
        {
            var inner = Interlocked.Exchange(ref _inner, null);
            if (inner == null) return;
            try { inner.Dispose(); }
            finally { ReleaseIdleRelays(); }
        }
    }
}
