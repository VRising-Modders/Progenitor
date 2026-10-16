using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using ProjectM.Shared.Systems;
using ProjectM.Terrain;
using Progenitor.Ai;
using Progenitor.Castles;
using Progenitor.Ecs;
using Progenitor.WorldState;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Servants;

/// <summary>
/// The Dev bridge half of <see cref="ServantRuntime"/> (<see cref="ServantBridge"/>): the zones a
/// castle can hunt, the game's servant-timers debug setting, and making an active hunt end now.
/// Not a plugin API.
/// </summary>
internal static unsafe partial class ServantRuntime
{
    private const long TimerRestoreMaxTicks = 10 * ServantLogic.TicksPerSecond;

    private static int TimersExtent => Layout.SyncedServerDebugSettingsServantTimersDisabledOffset + 1;

    /// <summary>A pending restore of the servant-timers setting after a <c>finish</c> turned it on.</summary>
    private sealed class TimerRestore
    {
        public readonly List<(CastleId Heart, (int, long, CastleId) Key)> Targets = new();
        public long DeadlineTicks;
    }

    private static TimerRestore? _timerRestore;

    internal static bool TimerRestorePending => _timerRestore != null;

    // ---- zones ------------------------------------------------------------------------------------

    /// <summary>The zones with a servant mission, as a castle's throne could send to them.</summary>
    internal static ServantZonesReport ZonesReport(World world, Entity heart, CastleId user, bool all)
    {
        var em = world.EntityManager;
        var (skip, timers) = DebugFlags(em);
        var rows = ReadRows(em, heart);
        var active = new HashSet<int>();
        foreach (var r in rows) active.Add(r.MissionGuid);

        var who = user.IsNone ? OwnerOf(heart) : user.ToEntity();
        var discovered = skip ? null : DiscoveredZones(em, who, out _);
        bool readable = skip || discovered != null;

        var slotsByMission = new Dictionary<int, int>();
        var zones = new List<ServantZoneInfo>();
        int total = 0;
        foreach (var (zone, mission) in Zones(em))
        {
            if (mission == 0) continue;
            total++;
            bool? found = skip ? true : discovered == null ? null : discovered.Contains(zone);
            if (!all && found != true) continue;
            if (!slotsByMission.TryGetValue(mission, out int slots))
            {
                slots = -1;
                if (PrefabLookup.TryGetEntity(world, new PrefabGUID(mission), out var prefab))
                {
                    byte* md = AiRuntime.RO<MissionData>(em, prefab, Layout.MissionDataServantSlotsOffset + 4);
                    if (md != null) slots = *(int*)(md + Layout.MissionDataServantSlotsOffset);
                }
                slotsByMission[mission] = slots;
            }
            zones.Add(new ServantZoneInfo
            {
                Zone = zone,
                MissionGuid = mission,
                MissionName = PrefabName(world, mission),
                Discovered = found,
                Active = active.Contains(mission),
                ServantSlots = slots,
            });
        }

        var lengths = new List<ServantDurationSlot>();
        var settings = Singleton<ServantMissionSettingsSingleton>(em, ref _missionSettings);
        if (settings != Entity.Null)
        {
            byte* raw = AiRuntime.Buffer<ServantMissionSetting>(em, settings, Layout.ServantMissionSettingStride, write: false, out int count);
            for (int i = 0; raw != null && i < count && i < 64; i++)
            {
                float len = *(float*)(raw + i * Layout.ServantMissionSettingStride + Layout.ServantMissionSettingMissionLengthOffset);
                lengths.Add(new ServantDurationSlot { Slot = i, SettingSeconds = len, LengthSeconds = ServantLogic.HuntLength(len, timers) });
            }
        }

        return new ServantZonesReport
        {
            Heart = CastleId.From(heart),
            User = CastleId.From(who),
            DiscoveryReadable = readable,
            AllProgressionUnlocked = skip,
            TimersDisabled = timers,
            ZonesWithMission = total,
            Zones = zones,
            DurationSlots = lengths,
            ActiveHunts = rows.Count,
        };
    }

    /// <summary>Every zone <paramref name="user"/> discovered; null when the progression is unreadable.</summary>
    private static HashSet<MissionZone>? DiscoveredZones(EntityManager em, Entity user, out int count)
    {
        count = 0;
        if (user == Entity.Null || !em.Exists(user)) return null;
        byte* pm = AiRuntime.RO<ProgressionMapper>(em, user, Layout.NetworkedEntityWaitingForSyncOffset + 1);
        if (pm == null || pm[Layout.ProgressionMapperEntityOffset + Layout.NetworkedEntityWaitingForSyncOffset] != 0) return null;
        var progression = AiRuntime.ReadEntity(pm + Layout.ProgressionMapperEntityOffset + Layout.NetworkedEntityEntityOffset);
        if (progression == Entity.Null || !em.Exists(progression)) return null;
        if (!em.HasComponent(progression, ComponentIndex.Of<DiscoveredMapZoneElement>(ComponentType.AccessMode.ReadOnly))) return null;
        var set = new HashSet<MissionZone>();
        int stride = Layout.DiscoveredMapZoneElementStride;
        byte* raw = AiRuntime.Buffer<DiscoveredMapZoneElement>(em, progression, stride, write: false, out int length);
        if (raw == null) return set;
        length = Math.Min(length, 4096);
        for (int i = 0; i < length; i++)
            set.Add(ServantLogic.ReadZone(new ReadOnlySpan<byte>(raw + i * stride, stride)));
        count = set.Count;
        return set;
    }

    // ---- the servant-timers debug setting ---------------------------------------------------------

    internal static bool? ReadTimersDisabled(EntityManager em)
    {
        var e = Singleton<SyncedServerDebugSettings>(em, ref _debugSettings);
        byte* p = e == Entity.Null ? null : AiRuntime.RO<SyncedServerDebugSettings>(em, e, TimersExtent);
        return p == null ? null : p[Layout.SyncedServerDebugSettingsServantTimersDisabledOffset] != 0;
    }

    private static void WriteTimersDisabled(EntityManager em, bool value)
    {
        var e = Singleton<SyncedServerDebugSettings>(em, ref _debugSettings);
        if (e == Entity.Null) throw new InvalidOperationException("no SyncedServerDebugSettings singleton in the server world");
        byte* rw = AiRuntime.RW<SyncedServerDebugSettings>(em, e, TimersExtent);
        rw[Layout.SyncedServerDebugSettingsServantTimersDisabledOffset] = value ? (byte)1 : (byte)0;
    }

    /// <summary>Set the game's servant-timers debug setting. Cancels a restore a <c>finish</c> scheduled.</summary>
    internal static ServantTimersStatus SetTimers(World world, bool disabled)
    {
        var em = world.EntityManager;
        bool? before = ReadTimersDisabled(em);
        if (before == null) throw new InvalidOperationException("the server's SyncedServerDebugSettings could not be read; nothing changed");
        bool cancelled = _timerRestore != null;
        _timerRestore = null;
        if (before.Value != disabled) WriteTimersDisabled(em, disabled);
        if (disabled) _heartsDirty = true;
        Plugin.Logger?.LogInfo("servants: servant timers " + (disabled ? "disabled" : "enabled") + " by the Dev bridge (was "
            + (before.Value ? "disabled" : "enabled") + ")" + (cancelled ? "; the pending finish restore was cancelled" : ""));
        return TimersStatus(world, before, cancelled);
    }

    internal static ServantTimersStatus TimersStatus(World world, bool? before = null, bool cancelledRestore = false)
    {
        var em = world.EntityManager;
        return new ServantTimersStatus
        {
            TimersDisabled = ReadTimersDisabled(em),
            WasDisabled = before,
            RestorePending = _timerRestore != null,
            CancelledRestore = cancelledRestore,
        };
    }

    private static void TickTimerRestore()
    {
        var pending = _timerRestore;
        if (pending == null) return;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated)
        {
            _timerRestore = null;
            Plugin.Logger?.LogWarning("servants: the server world went away while a finish waited; servant timers were not restored");
            return;
        }
        var em = world.EntityManager;
        long now = DateTime.UtcNow.Ticks;
        bool left = false;
        try
        {
            foreach (var (heart, key) in pending.Targets)
            {
                var h = heart.ToEntity();
                if (!em.Exists(h)) continue;
                foreach (var r in ReadRows(em, h))
                    if (r.Key == key) { left = true; break; }
                if (left) break;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("servants: finish check failed (" + e.Message + "); restoring servant timers now");
            left = false;
        }
        bool timedOut = now >= pending.DeadlineTicks;
        if (left && !timedOut) return;
        _timerRestore = null;
        try
        {
            if (ReadTimersDisabled(em) == true) WriteTimersDisabled(em, false);
            if (left) Plugin.Logger?.LogWarning("servants: finish: the hunt had not returned after 10 s; servant timers restored (enabled) anyway");
            else Plugin.Logger?.LogInfo("servants: finish: the hunt returned; servant timers restored (enabled)");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("servants: restoring servant timers failed: " + e.Message + ". Turn them back on (servants timers off:false).");
        }
    }

    private static void RestoreTimersOnUninstall()
    {
        if (_timerRestore == null) return;
        _timerRestore = null;
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;
            if (ReadTimersDisabled(em) == true) WriteTimersDisabled(em, false);
            Plugin.Logger?.LogInfo("servants: Core unload during a finish; servant timers restored (enabled)");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("servants: restoring servant timers on unload failed: " + e.Message);
        }
    }

    // ---- finish -----------------------------------------------------------------------------------

    /// <summary>
    /// Make the heart's active hunts (or the one on <paramref name="missionGuid"/>) end now; the
    /// game returns them on its next update with its own roll. <paramref name="viaTimers"/>: turn
    /// the game's servant-timers debug setting on until the hunts are gone (server-wide: every
    /// hunt returns), then back off. Otherwise rewrite only the matching rows' start ticks.
    /// </summary>
    internal static ServantFinishResult Finish(World world, Entity heart, int missionGuid, bool viaTimers)
    {
        var em = world.EntityManager;
        string method = viaTimers ? "timers" : "row";
        if (!HasMissionRecord(em, heart))
            return new ServantFinishResult { Ok = false, Method = method, Reason = "entity " + CastleId.From(heart) + " is not a castle heart with a hunt record" };
        var rows = ReadRows(em, heart);
        var targets = new List<int>();
        for (int i = 0; i < rows.Count; i++)
            if (missionGuid == 0 || rows[i].MissionGuid == missionGuid) targets.Add(i);
        if (targets.Count == 0)
            return new ServantFinishResult
            {
                Ok = false, Method = method,
                Reason = missionGuid == 0 ? "the castle has no active hunt" : "the castle has no active hunt on mission " + missionGuid,
            };

        var before = new List<ServantMission>();
        foreach (int i in targets) before.Add(ToMission(world, em, heart, rows[i], i));
        var heartId = CastleId.From(heart);

        if (viaTimers)
        {
            bool? on = ReadTimersDisabled(em);
            if (on == null)
                return new ServantFinishResult { Ok = false, Method = method, Reason = "the server's SyncedServerDebugSettings could not be read; nothing changed", Hunts = before };
            if (on.Value && _timerRestore != null)
            {
                // An earlier finish turned them on and still waits: wait for these hunts too.
                foreach (int i in targets) _timerRestore.Targets.Add((heartId, rows[i].Key));
                _timerRestore.DeadlineTicks = DateTime.UtcNow.Ticks + TimerRestoreMaxTicks;
                return new ServantFinishResult
                {
                    Ok = true, Method = method, Hunts = before, RestorePending = true,
                    Reason = "servant timers are already disabled by an earlier finish; the game returns these hunts on its next update, then Progenitor turns the timers back on",
                };
            }
            if (on.Value)
                return new ServantFinishResult
                {
                    Ok = true, Method = method, Hunts = before, TimersWereDisabled = true,
                    Reason = "servant timers were already disabled; the game returns every hunt on its next update. Nothing changed and nothing will be restored",
                };
            WriteTimersDisabled(em, true);
            _heartsDirty = true;
            var pending = _timerRestore ?? new TimerRestore();
            foreach (int i in targets) pending.Targets.Add((heartId, rows[i].Key));
            pending.DeadlineTicks = DateTime.UtcNow.Ticks + TimerRestoreMaxTicks;
            _timerRestore = pending;
            Plugin.Logger?.LogInfo("servants: finish by the Dev bridge: servant timers disabled until " + targets.Count + " hunt(s) of heart " + heartId
                + " return (server-wide: every active hunt returns on the next update)");
            return new ServantFinishResult
            {
                Ok = true, Method = method, Hunts = before, RestorePending = true,
                Reason = "servant timers disabled; the game returns every active hunt on the server on its next update, then Progenitor turns the timers back on (at most 10 s)",
            };
        }

        int stride = Layout.ActiveServantMissionStride;
        byte* raw = AiRuntime.Buffer<ActiveServantMission>(em, heart, stride, write: true, out int length);
        if (raw == null || length != rows.Count)
            return new ServantFinishResult { Ok = false, Method = method, Reason = "the hunt record changed while it was read; nothing written", Hunts = before };
        long now = DateTime.UtcNow.Ticks;
        var after = new List<ServantMission>();
        foreach (int i in targets)
        {
            var current = ServantLogic.DecodeRow(new ReadOnlySpan<byte>(raw + i * stride, stride));
            if (current.Key != rows[i].Key)
                return new ServantFinishResult { Ok = false, Method = method, Reason = "row " + i + " changed while it was read; stopped", Hunts = before, Finished = after };
            long start = ServantLogic.FinishNowStartTicks(current.StartTicks, current.LengthSeconds, now);
            *(long*)(raw + i * stride + Layout.ActiveServantMissionStartTicksOffset) = start;
            var back = ServantLogic.DecodeRow(new ReadOnlySpan<byte>(raw + i * stride, stride));
            if (back.StartTicks != start)
                return new ServantFinishResult { Ok = false, Method = method, Reason = "row " + i + " did not read back the new start", Hunts = before, Finished = after };
            after.Add(ToMission(world, em, heart, back, i));
        }
        _heartsDirty = true;
        Plugin.Logger?.LogInfo("servants: finish by the Dev bridge: " + after.Count + " hunt(s) of heart " + heartId
            + " made due now (row start rewritten); the game returns them on its next update");
        return new ServantFinishResult
        {
            Ok = true, Method = method, Hunts = before, Finished = after,
            Reason = "the hunts' start was moved back so they are due now; the game returns them on its next update with its own roll",
        };
    }
}
