using System;
using System.Collections.Generic;
using ProjectM;
using Progenitor.Ai;
using Progenitor.Ecs;
using Progenitor.Persistence;
using Progenitor.WorldState;
using Unity.Entities;

namespace Progenitor.Clock;

/// <summary>
/// The interop half of <c>ctx.Time.Freeze</c> / <c>ctx.Time.SlowMotion</c>. Server only; main thread.
///
/// <para><b>How the game runs world speed</b> (the game's native code, read 2026-09-27). The networked singleton
/// <c>ProjectM.TimeScale</c> holds <c>Value</c> and <c>IsPaused</c>. <c>TimeScaleSystem</c>
/// (<c>LateUpdateGroup</c>, server and client worlds) calls <c>UnityEngine.Time.set_timeScale</c>
/// with <c>Value</c> every frame (1 instead when a client has <c>ForceNormalTimeScale</c>, which
/// the character-creation menu uses). <c>UpdateWorldTimeSystem</c> feeds the scaled
/// <c>Time.deltaTime</c> into <c>World.Time</c>; <c>ServerTimeSystem_Server</c> adds
/// <c>World.Time.DeltaTime</c> to <c>ServerTime</c>, the clock every cooldown, buff and timer is
/// measured in; a client extrapolates its copy with its own scaled delta. The game's own writer is
/// <c>TimeScaleEventSystem</c> (server): an admin's <c>SetTimeScaleEvent</c> from the debug menu
/// writes <c>Value</c> and <c>IsPaused</c>.</para>
///
/// <para><b>What this writes.</b> While any lease is live: <c>TimeScale.Value</c> in the server
/// world, re-asserted every frame (an admin's debug-menu change is overridden while a lease lives),
/// only when it differs. <c>IsPaused</c> is never written. When the last lease is released the value
/// from before the first lease is written back. The first lease also records that prior value in a
/// plain per-save marker (<see cref="TimeScalePolicy"/>), cleared after a confirmed restore, so a
/// world saved during a freeze is not left frozen after a restart if the game saves the singleton.</para>
///
/// <para>Reads and writes go through <see cref="AiRuntime.RO{T}"/> / <see cref="AiRuntime.RW{T}"/>
/// at <c>layout.txt</c> offsets. No generic interop instantiation over a game value type is
/// introduced.</para>
/// </summary>
internal static unsafe class TimeScaleRuntime
{
    /// <summary>One plugin's lease. Disposing releases it (and only it).</summary>
    internal sealed class Lease : IDisposable
    {
        internal readonly object Owner;
        internal readonly TimeScaleOverrideKind Kind;
        internal readonly float Scale;
        internal readonly int Epoch;
        /// <summary>The owner as status prints it: a plugin GUID or an owner tag's label.</summary>
        internal readonly string Label;
        internal bool Released;
        /// <summary>Run once after the lease is released by any path (the facade untracks it).</summary>
        internal Action<Lease>? AfterRelease;

        internal Lease(object owner, TimeScaleOverrideKind kind, float scale, int epoch)
        {
            Owner = owner;
            Kind = kind;
            Scale = scale;
            Epoch = epoch;
            Label = OwnerLabel(owner);
        }

        public void Dispose() => Release(this);
    }

    private static readonly object Gate = new();
    private static readonly TimeOfDayOverrides<Lease> Stack = new();
    private static bool _hooked;
    private static bool _installed;
    private static bool _installedIntoRunningWorld;
    private static bool _recoveryPending;
    /// <summary>The value before the first live lease; restored after the last.</summary>
    private static float _before = TimeScalePolicy.Normal;
    private static int _failuresLogged;
    private static int _failureEpoch;
    private const int MaxFailureLogs = 8;

    private static int _cacheEpoch;
    private static Entity _scale = Entity.Null;

    private const int ReadExtent = Layout.TimeScaleIsPausedOffset + 1;
    private const int WriteExtent = Layout.TimeScaleValueOffset + sizeof(float);

    // ---- install -------------------------------------------------------------------------------

    /// <summary>Server: watch world loads for a leftover marker, and resolve one now on a hot Core install. Client: no-op. Idempotent.</summary>
    internal static void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        WorldEpoch.Advanced += OnAdvanced;
        var world = Worlds.Server;
        _installedIntoRunningWorld = world != null && world.IsCreated;
        lock (Gate)
        {
            _recoveryPending = true;
            UpdateHook_NoLock();
        }
    }

    private static void OnAdvanced(int epoch)
    {
        lock (Gate)
        {
            _recoveryPending = true;
            UpdateHook_NoLock();
        }
    }

    // ---- public surface (called by PluginTime) -------------------------------------------------

    internal static TimeScaleReading? Read(World? world, out string why)
    {
        if (!Ready(world, out why)) return null;
        if (!TryRead(world!.EntityManager, out float value, out bool paused))
        {
            why = "world speed: no TimeScale singleton in the server world.";
            return null;
        }
        DropStale(WorldEpoch.Current);
        TimeScaleOverrideKind kind;
        lock (Gate) kind = Stack.Current?.Kind ?? TimeScaleOverrideKind.None;
        why = "";
        return new TimeScaleReading(value, paused, EngineScale(), kind);
    }

    /// <summary>
    /// Start a lease and apply it now. Null with <paramref name="why"/> when the world has not
    /// finished loading, the singleton is missing, the marker cannot be recorded or the first
    /// write threw. Scale validation is the caller's.
    /// </summary>
    internal static Lease? Take(World? world, TimeScaleOverrideKind kind, float scale, object owner, out string why)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        if (kind == TimeScaleOverrideKind.None) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Ready(world, out why)) return null;
        if (!TimeOfDayPolicy.WorldLoadComplete(WorldEpoch.Loads, _installedIntoRunningWorld))
        {
            why = "world speed refused: the world has not finished loading. Take leases from a WorldLoaded handler or later.";
            return null;
        }
        var em = world!.EntityManager;
        int epoch = WorldEpoch.Current;
        DropStale(epoch);
        if (!TryRead(em, out float now, out _))
        {
            why = "world speed refused: no TimeScale singleton in the server world.";
            return null;
        }

        var lease = new Lease(owner, kind, scale, epoch);
        bool first;
        lock (Gate) first = Stack.Count == 0;
        if (first)
        {
            var store = MarkerStore();
            float prior = TimeScalePolicy.PriorForFirstTake(ReadMarker(store), now);
            try { store.Set(TimeScalePolicy.MarkerKey, TimeScalePolicy.Encode(prior)); }
            catch (Exception e)
            {
                why = "world speed refused: could not record the ownership marker: " + e.Message;
                return null;
            }
            lock (Gate)
            {
                _before = prior;
                _recoveryPending = false;
            }
            if (TimeScalePolicy.NeedsWrite(prior, TimeScalePolicy.Normal))
                Plugin.Logger?.LogWarning("Time: world speed was " + TimeScalePolicy.Format(prior)
                    + " before this lease (an admin's debug time control?); releasing the last lease puts that back.");
        }

        lock (Gate)
        {
            Stack.Push(lease);
            UpdateHook_NoLock();
        }
        try
        {
            Apply(em, lease);
        }
        catch (Exception e)
        {
            bool last;
            lock (Gate)
            {
                lease.Released = true;
                Stack.Remove(lease);
                last = Stack.Count == 0;
                UpdateHook_NoLock();
            }
            if (last) Restore(em, _before, "the first write threw");
            why = "world speed refused: writing TimeScale threw: " + e.Message;
            return null;
        }
        Plugin.Logger?.LogInfo("Time: world speed " + TimeScalePolicy.Format(scale) + " (" + kind + ")");
        why = "";
        return lease;
    }

    /// <summary>Every live lease, oldest first; the last one governs. Stale (earlier-epoch) leases are dropped first.</summary>
    internal static List<TimeScaleLeaseInfo> Leases()
    {
        DropStale(WorldEpoch.Current);
        List<Lease> live;
        lock (Gate) live = Stack.Snapshot();
        return TimeScaleLeaseInfo.FromStack(live, l => (l.Label, l.Kind, l.Scale));
    }

    /// <summary>A plugin's BepInEx GUID, an owner tag's label, or the owner's type name.</summary>
    internal static string OwnerLabel(object owner)
    {
        if (owner is TimeScaleOwnerTag tag) return tag.Label;
        try
        {
            var meta = BepInEx.MetadataHelper.GetMetadata(owner);
            if (meta != null && !string.IsNullOrEmpty(meta.GUID)) return meta.GUID;
        }
        catch { /* not a BepInEx plugin */ }
        return owner.GetType().FullName ?? owner.GetType().Name;
    }

    internal static bool HasOverride(object owner)
    {
        DropStale(WorldEpoch.Current);
        lock (Gate) return Stack.Any(l => ReferenceEquals(l.Owner, owner));
    }

    /// <summary>Core uninstall: release every lease, put the prior value back, drop the tick.</summary>
    internal static void Uninstall()
    {
        if (_installed)
        {
            WorldEpoch.Advanced -= OnAdvanced;
            _installed = false;
        }
        bool restore;
        float before;
        List<Lease> dropped;
        lock (Gate)
        {
            restore = Stack.Count > 0 && Stack.Current!.Epoch == WorldEpoch.Current;
            before = _before;
            dropped = Stack.DropAll();
            foreach (var l in dropped) l.Released = true;
            _recoveryPending = false;
            UpdateHook_NoLock();
        }
        if (restore)
        {
            var world = Worlds.Server;
            if (world != null && world.IsCreated) Restore(world.EntityManager, before, "Core uninstall");
        }
        NotifyReleased(dropped);
        _scale = Entity.Null;
    }

    // ---- release -------------------------------------------------------------------------------

    private static void Release(Lease lease)
    {
        Lease? next;
        bool restore;
        float before;
        lock (Gate)
        {
            if (lease.Released) return;
            lease.Released = true;
            if (!Stack.Remove(lease)) return;
            next = Stack.Current;
            restore = next == null && lease.Epoch == WorldEpoch.Current;
            before = _before;
            UpdateHook_NoLock();
        }
        Plugin.Logger?.LogInfo("Time: released world speed " + TimeScalePolicy.Format(lease.Scale)
            + (next == null ? "; normal speed returns" : "; the previous lease governs"));
        try
        {
            var world = Worlds.Server;
            if (world != null && world.IsCreated)
            {
                if (restore) Restore(world.EntityManager, before, "last lease released");
                else if (next != null && next.Epoch == WorldEpoch.Current) Apply(world.EntityManager, next);
            }
        }
        catch (Exception e)
        {
            LogFailure("release threw: " + e.Message);
        }
        NotifyReleased(new List<Lease> { lease });
    }

    /// <summary>Write the prior value back and, only when that is confirmed, clear the marker.</summary>
    private static bool Restore(EntityManager em, float value, string reason)
    {
        try
        {
            if (!TryRead(em, out float now, out _))
            {
                LogFailure("could not restore world speed (" + reason + "): no TimeScale singleton; the marker is kept");
                return false;
            }
            if (TimeScalePolicy.NeedsWrite(now, value)) Write(em, value);
            ClearMarker();
            Plugin.Logger?.LogInfo("Time: world speed restored to " + TimeScalePolicy.Format(value) + " (" + reason + ")");
            return true;
        }
        catch (Exception e)
        {
            LogFailure("restoring world speed threw (" + reason + "): " + e.Message + "; the marker is kept");
            return false;
        }
    }

    private static void DropStale(int epoch)
    {
        List<Lease> dropped;
        lock (Gate)
        {
            dropped = Stack.RemoveWhere(l => l.Epoch != epoch);
            foreach (var l in dropped) l.Released = true;
            if (dropped.Count > 0) UpdateHook_NoLock();
        }
        if (dropped.Count == 0) return;
        Plugin.Logger?.LogWarning("Time: dropped " + dropped.Count + " world-speed lease(s) from an earlier world epoch; "
            + "leases do not survive a world load.");
        NotifyReleased(dropped);
    }

    private static void NotifyReleased(List<Lease> leases)
    {
        foreach (var l in leases)
        {
            var cb = l.AfterRelease;
            l.AfterRelease = null;
            try { cb?.Invoke(l); }
            catch (Exception e) { LogFailure("release callback threw: " + e.Message); }
        }
    }

    // ---- ownership marker ----------------------------------------------------------------------

    private static IModStore MarkerStore() => ModSave.Store(TimeScalePolicy.MarkerStore);

    private static float? ReadMarker(IModStore store)
    {
        try { return TimeScalePolicy.Decode(store.Get<string>(TimeScalePolicy.MarkerKey)); }
        catch { return null; }
    }

    private static void ClearMarker()
    {
        try { MarkerStore().Remove(TimeScalePolicy.MarkerKey); }
        catch (Exception e) { LogFailure("clearing the ownership marker threw: " + e.Message); }
    }

    /// <summary>Resolve a leftover marker while no lease is live. True when done; false to retry next frame.</summary>
    private static bool TryRecover(EntityManager em)
    {
        var store = MarkerStore();
        if (store is ModStore ms && !ms.IsReady) return false;
        float? prior = ReadMarker(store);
        if (prior == null) return true;
        if (!TryRead(em, out float now, out _)) return false;
        bool live;
        lock (Gate) live = Stack.Count > 0;
        switch (TimeScalePolicy.Decide(prior, live, now))
        {
            case TimeScalePolicy.Recovery.RestoreAndClear:
                Write(em, prior.Value);
                ClearMarker();
                Plugin.Logger?.LogWarning("Time: this world came back at world speed " + TimeScalePolicy.Format(now)
                    + " with a ctx.Time lease's marker; restored " + TimeScalePolicy.Format(prior.Value) + ".");
                return true;
            case TimeScalePolicy.Recovery.ClearMarker:
                ClearMarker();
                return true;
            default:
                return true;
        }
    }

    // ---- the frame tick ------------------------------------------------------------------------

    private static void UpdateHook_NoLock()
    {
        bool want = Stack.Count > 0 || _recoveryPending;
        if (want && !_hooked)
        {
            Plugin.Frame += Tick;
            _hooked = true;
        }
        else if (!want && _hooked)
        {
            try { Plugin.Frame -= Tick; } catch { /* Frame may already be gone on uninstall */ }
            _hooked = false;
        }
    }

    private static void Tick()
    {
        Lease? current;
        bool recovery;
        lock (Gate)
        {
            current = Stack.Current;
            recovery = _recoveryPending;
        }
        if (current == null && !recovery) return;
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            int epoch = WorldEpoch.Current;
            if (current != null)
            {
                if (current.Epoch != epoch)
                {
                    DropStale(epoch);
                    return;
                }
                Apply(world.EntityManager, current);
                return;
            }
            if (!TimeOfDayPolicy.WorldLoadComplete(WorldEpoch.Loads, _installedIntoRunningWorld)) return;
            if (TryRecover(world.EntityManager))
            {
                lock (Gate)
                {
                    _recoveryPending = false;
                    UpdateHook_NoLock();
                }
            }
        }
        catch (Exception e)
        {
            LogFailure("tick threw: " + e.Message);
        }
    }

    private static void Apply(EntityManager em, Lease lease)
    {
        if (!TryRead(em, out float now, out _))
            throw new InvalidOperationException("TimeScale singleton not found");
        if (TimeScalePolicy.NeedsWrite(now, lease.Scale)) Write(em, lease.Scale);
    }

    // ---- raw reads and writes ------------------------------------------------------------------

    private static bool Ready(World? world, out string why)
    {
        if (world == null || !world.IsCreated)
        {
            why = "world speed refused: the server world is not ready.";
            return false;
        }
        why = "";
        return true;
    }

    private static byte* ReadSingleton(EntityManager em)
    {
        int epoch = WorldEpoch.Current;
        if (epoch != _cacheEpoch)
        {
            _cacheEpoch = epoch;
            _scale = Entity.Null;
        }
        byte* p = _scale == Entity.Null ? null : AiRuntime.RO<TimeScale>(em, _scale, ReadExtent);
        if (p != null) return p;
        var type = ComponentIndex.Of<TimeScale>(ComponentType.AccessMode.ReadOnly);
        var found = Query.Entities(em, includeDisabled: false, includeSystems: false, type);
        if (found.Count == 0) found = Query.Entities(em, includeDisabled: true, includeSystems: true, type);
        _scale = found.Count == 0 ? Entity.Null : found[0];
        return _scale == Entity.Null ? null : AiRuntime.RO<TimeScale>(em, _scale, ReadExtent);
    }

    private static bool TryRead(EntityManager em, out float value, out bool paused)
    {
        byte* p = ReadSingleton(em);
        value = p == null ? TimeScalePolicy.Normal : *(float*)(p + Layout.TimeScaleValueOffset);
        paused = p != null && p[Layout.TimeScaleIsPausedOffset] != 0;
        return p != null;
    }

    private static void Write(EntityManager em, float value)
    {
        byte* rw = AiRuntime.RW<TimeScale>(em, _scale, WriteExtent);
        *(float*)(rw + Layout.TimeScaleValueOffset) = value;
    }

    private static float EngineScale()
    {
        try { return UnityEngine.Time.timeScale; }
        catch { return float.NaN; }
    }

    private static void LogFailure(string line)
    {
        int epoch = WorldEpoch.Current;
        if (epoch != _failureEpoch)
        {
            _failureEpoch = epoch;
            _failuresLogged = 0;
        }
        if (_failuresLogged >= MaxFailureLogs) return;
        _failuresLogged++;
        Plugin.Logger?.LogWarning("Time: " + line
            + (_failuresLogged == MaxFailureLogs ? " (further failures this world epoch are not logged)" : ""));
    }
}
