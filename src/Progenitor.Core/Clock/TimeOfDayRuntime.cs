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
/// The interop half of <c>ctx.Time</c>. Server only; main thread.
///
/// <para><b>How the game runs the clock</b> (the server's native code, read 2026-09-25).
/// <c>DayNightCycleSystem.OnUpdate</c> runs on server and client. While
/// <c>SyncedServerDebugSettings.DayNightCycleDisabled</c> is false it sets, on each side,
/// <c>DayNightCycle.Time = DayNightCycleStartTime + ServerTime.Time</c>: every process derives
/// its own clock from the shared game clock, and <c>DayNightCycleStartTime</c> is not a synced
/// field, so a server-only write to it (or to <c>Time</c>) is overwritten next frame and never
/// reaches a client. While the flag is true the server leaves <c>Time</c> alone and copies it
/// into the <c>[SyncDouble]</c> field <c>DayNightCycle.ServerTime</c>; a client copies that field
/// back into its own <c>Time</c>. The flag itself is a <c>[SyncBool]</c> on a networked
/// singleton, so both halves reach every connected client through ordinary replication.</para>
///
/// <para><b>What this writes.</b> While any override is live: the flag byte
/// (<see cref="Layout.SyncedServerDebugSettingsDayNightCycleDisabledOffset"/>) to 1, and
/// <c>DayNightCycle.Time</c> and <c>DayNightCycle.ServerTime</c> to the override's target, each
/// only when it differs (so a hold does not churn the change version). A hold writes one value
/// and then nothing; a shift writes <c>DayNightCycleStartTime + ServerTime.Time + offset</c> every
/// frame, which is what the game itself computes, moved by a constant. When the last override
/// is released the flag is put back to what it was before the first one, and the game resumes
/// <c>StartTime + ServerTime.Time</c> — the clock returns to where the world would have been.</para>
///
/// <para><b>Ownership marker.</b> The game saves the flag with the world, so the first override
/// also records the flag's prior value in a plain per-save marker (<see cref="TimeOfDayPolicy"/>),
/// cleared after a successful restore. After every world load and on a Core install into a running
/// world, a marker with no live override is resolved: the prior value is written back and the
/// marker cleared.</para>
///
/// <para><b>Why not the game's own <c>SetTimeOfDayEvent</c>.</b> Its handler
/// (<c>VariousMigratedDebugEventsSystem.HandleSetTimeOfDayEvent</c>, lambda jobs 0-2) moves
/// <c>ServerTime.Time</c> — the global game clock every cooldown, buff and timer is measured in —
/// forward to the requested hour of the <i>next</i> day. That ages the whole world on every call
/// and cannot hold. It does not write <c>DayNightCycleStartTime</c> or the flag.</para>
///
/// <para>Reads and writes go through <see cref="AiRuntime.RO{T}"/> / <see cref="AiRuntime.RW{T}"/>
/// at <c>layout.txt</c> offsets. No generic interop instantiation over a game value type is
/// introduced.</para>
/// </summary>
internal static unsafe class TimeOfDayRuntime
{
    /// <summary>One plugin's override. Disposing releases it (and only it).</summary>
    internal sealed class Lease : IDisposable
    {
        internal readonly object Owner;
        internal readonly TimeOfDayOverrideKind Kind;
        internal readonly double Hour;
        internal readonly int Epoch;
        /// <summary>
        /// Hold: the requested hour on the day the hold was taken (<c>CycleTimeFor</c>). The value
        /// written is resolved from it by <see cref="TimeOfDayMath.HoldTarget"/> against the live
        /// <c>NextBloodMoonDay</c> each time the hold governs.
        /// </summary>
        internal readonly double Target;
        /// <summary>Hold: true to hold the blood-moon night, false to avoid it.</summary>
        internal readonly bool BloodMoon;
        /// <summary>Hold: the value last resolved and written.</summary>
        internal double Resolved;
        /// <summary>Shift: seconds added to the game's own clock value.</summary>
        internal readonly double Offset;
        internal bool Released;
        /// <summary>Run once after the lease is released by any path (the facade untracks it).</summary>
        internal Action<Lease>? AfterRelease;

        internal Lease(object owner, TimeOfDayOverrideKind kind, double hour, int epoch, double target, double offset, bool bloodMoon, double resolved)
        {
            BloodMoon = bloodMoon;
            Resolved = resolved;
            Owner = owner;
            Kind = kind;
            Hour = hour;
            Epoch = epoch;
            Target = target;
            Offset = offset;
        }

        public void Dispose() => Release(this);
    }

    /// <summary>The fields one tick needs, read raw.</summary>
    private struct CycleRead
    {
        internal double Time;
        internal double SyncedTime;
        internal double StartTime;
        internal double DayLength;
        internal double DayStart;
        internal double DayDuration;
        internal int NextBloodMoonDay;
    }

    private static readonly object Gate = new();
    private static readonly TimeOfDayOverrides<Lease> Stack = new();
    private static bool _hooked;
    private static bool _installed;
    private static bool _installedIntoRunningWorld;
    /// <summary>A marker may need resolving (set on install and on every world load).</summary>
    private static bool _recoveryPending;
    /// <summary>The flag as it was before the first live override; restored after the last.</summary>
    private static bool _flagBefore;
    private static int _failuresLogged;
    private static int _failureEpoch;
    private const int MaxFailureLogs = 8;

    // Cached singletons, re-found when the epoch moves or a read misses.
    private static int _cacheEpoch;
    private static Entity _cycle = Entity.Null;
    private static Entity _settings = Entity.Null;
    private static Entity _serverTime = Entity.Null;

    private const int CycleReadExtent = Layout.DayNightCycleNextBloodMoonDayOffset + sizeof(int);
    private const int CycleWriteExtent = Layout.DayNightCycleServerTimeOffset + sizeof(double);
    private const int FlagExtent = Layout.SyncedServerDebugSettingsDayNightCycleDisabledOffset + 1;
    private const int ServerTimeExtent = Layout.ServerTimeTimeOffset + sizeof(double);

    // ---- install -------------------------------------------------------------------------------

    /// <summary>
    /// Server: watch world loads for a leftover ownership marker, and resolve one now if Core is
    /// being installed into a running world (a hot swap). Client: no-op. Idempotent.
    /// </summary>
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

    /// <summary>Read the clock. Null with <paramref name="why"/> when the world is not ready.</summary>
    internal static TimeOfDayReading? Read(World? world, out string why)
    {
        if (!Ready(world, out why)) return null;
        var em = world!.EntityManager;
        if (!TryReadCycle(em, out var c, out why)) return null;
        bool stopped = TryReadFlag(em, out bool flag) && flag;
        DropStale(WorldEpoch.Current);
        TimeOfDayOverrideKind kind;
        lock (Gate) kind = Stack.Current?.Kind ?? TimeOfDayOverrideKind.None;
        why = "";
        return new TimeOfDayReading(
            TimeOfDayMath.DayOf(c.Time, c.DayLength),
            TimeOfDayMath.HourOf(c.Time, c.DayLength),
            TimeOfDayMath.IsDay(c.Time, c.DayLength, c.DayStart, c.DayDuration),
            c.DayLength,
            TimeOfDayMath.HourForSeconds(c.DayStart, c.DayLength),
            TimeOfDayMath.HourForSeconds(c.DayStart + c.DayDuration, c.DayLength) % TimeOfDayMath.HoursPerDay,
            stopped,
            kind,
            TimeOfDayMath.IsBloodMoonNight(c.Time, c.DayLength, c.DayStart, c.DayDuration, c.NextBloodMoonDay),
            c.NextBloodMoonDay);
    }

    // The client world's own singletons, cached apart from the server's: a client-hosted process
    // has both worlds, and a cached entity that stops resolving is simply looked up again.
    private static Entity _clientCycle = Entity.Null;
    private static Entity _clientSettings = Entity.Null;
    // A missing client singleton costs two full scans to find out; remember the miss for a second.
    private const long ClientMissRetryMs = 1000;
    private static long _clientCycleMissUntil;
    private static long _clientSettingsMissUntil;
    private static ulong _clientMissWorld;

    /// <summary>
    /// Read the client world's clock: the client copies the server's replicated
    /// <c>DayNightCycle.ServerTime</c> into its own <c>DayNightCycle</c>, so this is the hour its
    /// sun and lighting show. <see cref="TimeOfDayReading.Override"/> is always
    /// <see cref="TimeOfDayOverrideKind.None"/> here (override leases live on the server);
    /// <see cref="TimeOfDayReading.WorldClockStopped"/> is the replicated flag when the client has
    /// it, else false. Null with <paramref name="why"/> when the world or its clock is not there.
    /// </summary>
    internal static TimeOfDayReading? ReadClient(World? world, out string why)
    {
        if (world == null || !world.IsCreated)
        {
            why = "time-of-day: the client world is not ready.";
            return null;
        }
        var em = world.EntityManager;
        long now = Environment.TickCount64;
        ulong seq = world.SequenceNumber;
        if (seq != _clientMissWorld)
        {
            _clientMissWorld = seq;
            _clientCycleMissUntil = _clientSettingsMissUntil = 0;
        }
        if (now < _clientCycleMissUntil)
        {
            why = "time-of-day refused: no DayNightCycle singleton on the client (checked within the last second).";
            return null;
        }
        if (!TryReadCycle(em, ref _clientCycle, out var c, out why))
        {
            _clientCycleMissUntil = now + ClientMissRetryMs;
            return null;
        }
        bool stopped = false;
        if (now >= _clientSettingsMissUntil)
        {
            if (TryReadFlag(em, ref _clientSettings, out bool flag)) stopped = flag;
            else _clientSettingsMissUntil = now + ClientMissRetryMs;
        }
        why = "";
        return new TimeOfDayReading(
            TimeOfDayMath.DayOf(c.Time, c.DayLength),
            TimeOfDayMath.HourOf(c.Time, c.DayLength),
            TimeOfDayMath.IsDay(c.Time, c.DayLength, c.DayStart, c.DayDuration),
            c.DayLength,
            TimeOfDayMath.HourForSeconds(c.DayStart, c.DayLength),
            TimeOfDayMath.HourForSeconds(c.DayStart + c.DayDuration, c.DayLength) % TimeOfDayMath.HoursPerDay,
            stopped,
            TimeOfDayOverrideKind.None,
            TimeOfDayMath.IsBloodMoonNight(c.Time, c.DayLength, c.DayStart, c.DayDuration, c.NextBloodMoonDay),
            c.NextBloodMoonDay);
    }

    /// <summary>
    /// Start an override and apply it now. Null with <paramref name="why"/> when the world has not
    /// finished loading, or the clock, the game clock or the debug-settings singleton cannot be
    /// found, or the first write threw. Throws for an hour outside [0, 24).
    /// </summary>
    internal static Lease? Take(World? world, TimeOfDayOverrideKind kind, double hour, object owner, out string why, bool bloodMoon = false)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        TimeOfDayMath.RequireHour(hour);
        if (kind == TimeOfDayOverrideKind.None) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!Ready(world, out why)) return null;
        if (!TimeOfDayPolicy.WorldLoadComplete(WorldEpoch.Loads, _installedIntoRunningWorld))
        {
            why = "time-of-day refused: the world has not finished loading. Take overrides from a WorldLoaded handler.";
            return null;
        }
        var em = world!.EntityManager;
        int epoch = WorldEpoch.Current;
        DropStale(epoch);

        if (!TryReadCycle(em, out var c, out why)) return null;
        if (!TryReadFlag(em, out bool flagNow))
        {
            why = "time-of-day refused: no SyncedServerDebugSettings singleton, so clients could not be made to follow.";
            return null;
        }
        if (!TryReadGameClock(em, out double gameClock))
        {
            why = "time-of-day refused: no ServerTime singleton.";
            return null;
        }

        double target = TimeOfDayMath.CycleTimeFor(c.Time, c.DayLength, hour);
        double offset = target - (c.StartTime + gameClock);
        double resolved = target;
        string note = "";
        if (kind == TimeOfDayOverrideKind.Hold)
        {
            double? hold = TimeOfDayMath.HoldTarget(target, c.DayLength, c.DayStart, c.DayDuration,
                c.NextBloodMoonDay, hour, bloodMoon, out note);
            if (hold == null)
            {
                why = "time-of-day refused: " + note + ".";
                return null;
            }
            resolved = hold.Value;
        }
        var lease = new Lease(owner, kind, hour, epoch, kind == TimeOfDayOverrideKind.Hold ? target : 0, offset,
            kind == TimeOfDayOverrideKind.Hold && bloodMoon, resolved);

        bool first;
        lock (Gate) first = Stack.Count == 0;
        if (first)
        {
            var store = MarkerStore();
            bool prior = TimeOfDayPolicy.PriorForFirstTake(ReadMarker(store), flagNow);
            try { store.Set(TimeOfDayPolicy.MarkerKey, TimeOfDayPolicy.Encode(prior)); }
            catch (Exception e)
            {
                why = "time-of-day refused: could not record the ownership marker: " + e.Message;
                return null;
            }
            lock (Gate)
            {
                _flagBefore = prior;
                _recoveryPending = false; // the marker is consumed by this override
            }
            if (prior)
                Plugin.Logger?.LogWarning("Time: the world's day/night cycle was already stopped "
                    + "(DayNightCycleDisabled) before this override; releasing it will leave it stopped.");
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
            // Never leave an orphaned lease: take it back off, and if it was the only one, give
            // the flag back and clear the marker.
            bool last;
            lock (Gate)
            {
                lease.Released = true;
                Stack.Remove(lease);
                last = Stack.Count == 0;
                UpdateHook_NoLock();
            }
            if (last) RestoreFlag(em, _flagBefore, "the first write threw");
            why = "time-of-day refused: writing the clock threw: " + e.Message;
            return null;
        }
        Plugin.Logger?.LogInfo("Time: " + (kind == TimeOfDayOverrideKind.Hold ? "hold" : "set")
            + " " + TimeOfDayMath.FormatHour(hour) + " (day " + TimeOfDayMath.DayOf(resolved, c.DayLength)
            + (note.Length > 0 ? "; " + note : "") + ")");
        why = "";
        return lease;
    }

    /// <summary>
    /// Clear <c>DayNightCycleDisabled</c> when no override is live: the recovery for a world
    /// saved while an override held the flag, or one an admin stopped. Also clears a leftover
    /// ownership marker. False with a reason otherwise.
    /// </summary>
    internal static bool ResumeWorldClock(World? world, out string why)
    {
        DropStale(WorldEpoch.Current);
        lock (Gate)
        {
            if (Stack.Count > 0)
            {
                why = "resume refused: " + Stack.Count + " time-of-day override(s) are live; release them instead.";
                return false;
            }
        }
        if (!Ready(world, out why)) return false;
        var em = world!.EntityManager;
        if (!TryReadFlag(em, out bool flag))
        {
            why = "resume refused: no SyncedServerDebugSettings singleton.";
            return false;
        }
        if (flag) WriteFlag(em, false);
        ClearMarker();
        lock (Gate) _recoveryPending = false;
        Plugin.Logger?.LogInfo("Time: world clock " + (flag ? "resumed" : "was already running"));
        why = "";
        return true;
    }

    /// <summary>True when <paramref name="owner"/> has a live override.</summary>
    internal static bool HasOverride(object owner)
    {
        DropStale(WorldEpoch.Current);
        lock (Gate) return Stack.Any(l => ReferenceEquals(l.Owner, owner));
    }

    /// <summary>Core uninstall: release every override, put the flag back, drop the tick.</summary>
    internal static void Uninstall()
    {
        if (_installed)
        {
            WorldEpoch.Advanced -= OnAdvanced;
            _installed = false;
        }
        bool restore;
        bool flag;
        List<Lease> dropped;
        lock (Gate)
        {
            restore = Stack.Count > 0 && Stack.Current!.Epoch == WorldEpoch.Current;
            flag = _flagBefore;
            dropped = Stack.DropAll();
            foreach (var l in dropped) l.Released = true;
            _recoveryPending = false;
            UpdateHook_NoLock();
        }
        if (restore)
        {
            var world = Worlds.Server;
            if (world != null && world.IsCreated) RestoreFlag(world.EntityManager, flag, "Core uninstall");
        }
        NotifyReleased(dropped);
        _cycle = _settings = _serverTime = Entity.Null;
    }

    // ---- release -------------------------------------------------------------------------------

    private static void Release(Lease lease)
    {
        Lease? next;
        bool restore;
        bool flag;
        lock (Gate)
        {
            if (lease.Released) return;
            lease.Released = true;
            if (!Stack.Remove(lease)) return;
            next = Stack.Current;
            restore = next == null && lease.Epoch == WorldEpoch.Current;
            flag = _flagBefore;
            UpdateHook_NoLock();
        }
        Plugin.Logger?.LogInfo("Time: released " + TimeOfDayMath.FormatHour(lease.Hour)
            + (next == null ? "; the game runs the clock again" : "; the previous override governs"));
        try
        {
            var world = Worlds.Server;
            if (world != null && world.IsCreated)
            {
                if (restore) RestoreFlag(world.EntityManager, flag, "last override released");
                else if (next != null && next.Epoch == WorldEpoch.Current) Apply(world.EntityManager, next);
            }
        }
        catch (Exception e)
        {
            LogFailure("release threw: " + e.Message);
        }
        NotifyReleased(new List<Lease> { lease });
    }

    /// <summary>
    /// Put the flag back and, only when that is confirmed, clear the ownership marker. A failed
    /// restore keeps the marker so the next world load (or Core install) resolves it.
    /// </summary>
    private static bool RestoreFlag(EntityManager em, bool value, string reason)
    {
        try
        {
            if (!TryReadFlag(em, out bool now))
            {
                LogFailure("could not restore DayNightCycleDisabled (" + reason + "): no SyncedServerDebugSettings singleton; the marker is kept");
                return false;
            }
            if (now != value) WriteFlag(em, value);
            ClearMarker();
            Plugin.Logger?.LogInfo("Time: DayNightCycleDisabled restored to " + value + " (" + reason + ")");
            return true;
        }
        catch (Exception e)
        {
            LogFailure("restoring DayNightCycleDisabled threw (" + reason + "): " + e.Message + "; the marker is kept");
            return false;
        }
    }

    /// <summary>
    /// Overrides from an earlier world epoch belong to a world that no longer exists. They are
    /// dropped without touching the new world; the marker (if that world was saved during one)
    /// is resolved by the post-load recovery.
    /// </summary>
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
        Plugin.Logger?.LogWarning("Time: dropped " + dropped.Count + " override(s) from an earlier world epoch; "
            + "overrides do not survive a world load.");
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

    private static IModStore MarkerStore() => ModSave.Store(TimeOfDayPolicy.MarkerStore);

    private static bool? ReadMarker(IModStore store)
    {
        try { return TimeOfDayPolicy.Decode(store.Get<string>(TimeOfDayPolicy.MarkerKey)); }
        catch { return null; }
    }

    private static void ClearMarker()
    {
        try { MarkerStore().Remove(TimeOfDayPolicy.MarkerKey); }
        catch (Exception e) { LogFailure("clearing the ownership marker threw: " + e.Message); }
    }

    /// <summary>
    /// Resolve a leftover marker while no override is live. Returns true when done (nothing to do,
    /// or resolved); false to retry next frame (store or singleton not ready yet).
    /// </summary>
    private static bool TryRecover(EntityManager em)
    {
        var store = MarkerStore();
        if (store is ModStore ms && !ms.IsReady) return false;
        bool? prior = ReadMarker(store);
        if (prior == null) return true;
        if (!TryReadFlag(em, out bool flag)) return false;
        bool live;
        lock (Gate) live = Stack.Count > 0;
        switch (TimeOfDayPolicy.Decide(prior, live, flag))
        {
            case TimeOfDayPolicy.Recovery.RestoreAndClear:
                WriteFlag(em, prior.Value);
                ClearMarker();
                Plugin.Logger?.LogWarning("Time: this world was saved while a time-of-day override owned "
                    + "DayNightCycleDisabled; restored it to " + prior.Value + ".");
                return true;
            case TimeOfDayPolicy.Recovery.ClearMarker:
                ClearMarker();
                return true;
            default:
                return true;
        }
    }

    // ---- the frame tick ------------------------------------------------------------------------

    /// <summary>Hooked exactly while an override is live or a marker may need resolving.</summary>
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

    /// <summary>Make the world show <paramref name="lease"/>'s time: flag on, Time and ServerTime at target.</summary>
    private static void Apply(EntityManager em, Lease lease)
    {
        if (!TryReadFlag(em, out bool flag))
            throw new InvalidOperationException("SyncedServerDebugSettings singleton not found");
        // Re-asserted every frame: an admin's own day/night toggle is overridden while this lives.
        if (!flag) WriteFlag(em, true);

        if (!TryReadCycle(em, out var c, out string why))
            throw new InvalidOperationException(why);
        double target = lease.Resolved;
        if (lease.Kind == TimeOfDayOverrideKind.Hold)
        {
            // Re-resolved against the live schedule: constant while this hold governs (a hold
            // freezes TotalDays, so NextBloodMoonDay does not re-roll), but correct when the stack
            // pops back to an older hold after a shift moved the schedule. A force that no longer
            // resolves keeps the last value.
            double? hold = TimeOfDayMath.HoldTarget(lease.Target, c.DayLength, c.DayStart, c.DayDuration,
                c.NextBloodMoonDay, lease.Hour, lease.BloodMoon, out _);
            if (hold != null) lease.Resolved = hold.Value;
            target = lease.Resolved;
        }
        else if (lease.Kind == TimeOfDayOverrideKind.Shift)
        {
            if (!TryReadGameClock(em, out double gameClock))
                throw new InvalidOperationException("ServerTime singleton not found");
            target = c.StartTime + gameClock + lease.Offset;
        }
        if (!TimeOfDayMath.NeedsWrite(c.Time, target) && !TimeOfDayMath.NeedsWrite(c.SyncedTime, target)) return;
        byte* rw = AiRuntime.RW<DayNightCycle>(em, _cycle, CycleWriteExtent);
        *(double*)(rw + Layout.DayNightCycleTimeOffset) = target;
        *(double*)(rw + Layout.DayNightCycleServerTimeOffset) = target;
    }

    // ---- raw reads -----------------------------------------------------------------------------

    private static bool Ready(World? world, out string why)
    {
        if (world == null || !world.IsCreated)
        {
            why = "time-of-day refused: the server world is not ready.";
            return false;
        }
        why = "";
        return true;
    }

    private static void RefreshCacheEpoch()
    {
        int epoch = WorldEpoch.Current;
        if (epoch == _cacheEpoch) return;
        _cacheEpoch = epoch;
        _cycle = _settings = _serverTime = Entity.Null;
    }

    private static Entity FindSingleton<T>(EntityManager em)
    {
        var type = ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly);
        var found = Query.Entities(em, includeDisabled: false, includeSystems: false, type);
        if (found.Count == 0) found = Query.Entities(em, includeDisabled: true, includeSystems: true, type);
        return found.Count == 0 ? Entity.Null : found[0];
    }

    private static byte* ReadSingleton<T>(EntityManager em, ref Entity cached, int need)
    {
        RefreshCacheEpoch();
        byte* p = cached == Entity.Null ? null : AiRuntime.RO<T>(em, cached, need);
        if (p != null) return p;
        cached = FindSingleton<T>(em);
        return cached == Entity.Null ? null : AiRuntime.RO<T>(em, cached, need);
    }

    private static bool TryReadCycle(EntityManager em, out CycleRead c, out string why) =>
        TryReadCycle(em, ref _cycle, out c, out why);

    private static bool TryReadCycle(EntityManager em, ref Entity cache, out CycleRead c, out string why)
    {
        c = default;
        byte* p = ReadSingleton<DayNightCycle>(em, ref cache, CycleReadExtent);
        if (p == null)
        {
            why = "time-of-day refused: no DayNightCycle singleton.";
            return false;
        }
        c.Time = *(double*)(p + Layout.DayNightCycleTimeOffset);
        c.SyncedTime = *(double*)(p + Layout.DayNightCycleServerTimeOffset);
        c.StartTime = *(float*)(p + Layout.DayNightCycleStartTimeOffset);
        c.DayLength = *(float*)(p + Layout.DayNightCycleDayDurationOffset);
        c.DayStart = *(float*)(p + Layout.DayNightCycleDayTimeStartOffset);
        c.DayDuration = *(float*)(p + Layout.DayNightCycleDayTimeDurationOffset);
        c.NextBloodMoonDay = *(int*)(p + Layout.DayNightCycleNextBloodMoonDayOffset);
        if (!TimeOfDayMath.IsUsableDayLength(c.DayLength))
        {
            why = "time-of-day refused: DayNightCycle.DayDurationInSeconds is " + c.DayLength + ".";
            return false;
        }
        why = "";
        return true;
    }

    private static bool TryReadFlag(EntityManager em, out bool flag) => TryReadFlag(em, ref _settings, out flag);

    private static bool TryReadFlag(EntityManager em, ref Entity cache, out bool flag)
    {
        byte* p = ReadSingleton<SyncedServerDebugSettings>(em, ref cache, FlagExtent);
        flag = p != null && p[Layout.SyncedServerDebugSettingsDayNightCycleDisabledOffset] != 0;
        return p != null;
    }

    private static void WriteFlag(EntityManager em, bool value)
    {
        byte* rw = AiRuntime.RW<SyncedServerDebugSettings>(em, _settings, FlagExtent);
        rw[Layout.SyncedServerDebugSettingsDayNightCycleDisabledOffset] = value ? (byte)1 : (byte)0;
    }

    private static bool TryReadGameClock(EntityManager em, out double time)
    {
        byte* p = ReadSingleton<ServerTime>(em, ref _serverTime, ServerTimeExtent);
        time = p == null ? 0 : *(double*)(p + Layout.ServerTimeTimeOffset);
        return p != null;
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
