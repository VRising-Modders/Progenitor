using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Shared.Systems;
using Progenitor.Ai;
using Progenitor.Castles;
using Progenitor.Ecs;
using Progenitor.Hooks;
using Progenitor.Persistence;
using Progenitor.WorldState;
using Unity.Entities;

namespace Progenitor.Servants;

/// <summary>
/// The success cap (<see cref="ServantSendRequest.MaxSuccess"/>). Data only, inside the existing
/// <c>ServantMissionUpdateSystem</c> before/after hooks: no game method is called or patched.
///
/// <para><b>Mechanism.</b> Before the update, for each capped hunt the game finishes this update,
/// each servant's vanilla chance <c>c</c> is computed from the live inputs the game reads next; with
/// probability <c>1 - cap / c</c> the servant's <c>ServantPower.GearLevel</c> is set to
/// <see cref="ServantCapMath.ForcedGearLevel"/> for this update, so the game's own roll fails and
/// the servant fails the game's way. After the update every temporary write is undone and read
/// back. A capped hunt due within the clock slack but not yet due by Progenitor's clock is held this
/// update (its row length raised for the update only), so no servant is ever touched in an update
/// that does not finish its hunt and the cap cannot be skipped by the clock race.</para>
///
/// <para><b>Restore, always.</b> The journal is drained after the update; if that hook does not run
/// (the update threw), after the update group (which catches its systems' exceptions), before the
/// persistence group of the next frame (before any save system), on the next frame tick, and at Core
/// unload. Every late restore is logged as an error. A gear write is also recorded in the server
/// store before it is made, so a later Core generation or process restores it (or, when the world
/// on disk cannot hold it, drops the record and says so).</para>
///
/// <para><b>Why a save cannot see the forced value.</b> The write and its restore are both inside
/// one <c>ServantMissionUpdateSystem.Update</c> on the main thread, in <c>UpdateGroup</c>; the game's
/// save systems run in <c>PersistenceGroup</c>, a different group in an earlier part of the frame
/// (<c>StartSimulationGroup</c>). The raw read/write accessors complete the jobs that use the
/// component first, so a job still reading <c>ServantPower</c> finishes before the write.</para>
/// </summary>
internal static unsafe partial class ServantRuntime
{
    /// <summary>The server store that holds the cap book and the pending restores.</summary>
    internal const string CapStoreGuid = "progenitor.servants";

    private const long CapExpireEveryTicks = 60 * ServantLogic.TicksPerSecond;

    private static readonly ServantCapBook CapBook = new();
    private static readonly TransientJournal Journal = new();
    private static readonly HoldTracker Holds = new();
    private static readonly Random CapRandom = new();
    private static IModStore? _capStore;
    private static IDisposable? _capReady, _guardGroup, _guardPersist;
    private static bool _capLoaded;
    private static long _forcedFailures;
    private static long _capExpireStamp;
    private static int _sweptEpoch = int.MinValue;
    private static bool _timersHoldWarned;
    private static string? _processId;

    private static string ProcessId
    {
        get
        {
            if (_processId != null) return _processId;
            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                _processId = p.Id + ":" + p.StartTime.ToUniversalTime().Ticks;
            }
            catch { _processId = Environment.ProcessId + ":0"; }
            return _processId;
        }
    }

    internal static ServantCapStatus CapStatus() => new()
    {
        Ready = _capLoaded,
        Caps = CapBook.Count,
        ForcedFailures = _forcedFailures,
        Restores = Journal.Restores,
        LateRestores = Journal.LateRestores,
        Outstanding = Journal.Count,
        Held = Holds.Held,
        ReleasedUncapped = Holds.Released,
    };

    private static void InstallCaps()
    {
        _capStore = ServerSave.Store(CapStoreGuid);
        if (_capStore is ModStore ms && ms.IsReady) LoadCapBook(_capStore);
        else _capReady = _capStore.WhenReady(LoadCapBook);
        _guardGroup = SystemHooks.After<UpdateGroup>(_ => GuardRestore("after the update group"));
        _guardPersist = SystemHooks.Before<PersistenceGroup>(_ => GuardRestore("before the persistence group"));
    }

    private static void UninstallCaps()
    {
        // The world may still be up (a Core swap): undo anything outstanding while it is.
        GuardRestore("at Core unload");
        foreach (var h in new[] { _capReady, _guardGroup, _guardPersist })
        {
            try { h?.Dispose(); } catch { }
        }
        _capReady = _guardGroup = _guardPersist = null;
        if (Journal.Count > 0)
            Plugin.Logger?.LogError("servants: " + Journal.Count + " temporary cap write(s) could not be undone at Core unload (no server world); "
                + "the pending-restore records in the server store are kept for the next Core");
        Journal.Clear();
        try { ServerSave.SaveAll(); } catch { }
        CapBook.Clear();
        Holds.Clear();
        _capLoaded = false;
        _capStore = null;
        _sweptEpoch = int.MinValue;
    }

    private static void LoadCapBook(IModStore store)
    {
        try
        {
            CapBook.Clear();
            foreach (var key in store.Keys)
            {
                if (ServantCapBook.FromStoreKey(key) == null) continue;
                var r = store.Get<CapRecord>(key);
                if (r != null && ServantCapMath.IsValidCap(r.MaxSuccess) && r.MissionGuid != 0) CapBook.Add(r);
            }
            _capLoaded = true;
            Holds.Clear();
            Plugin.Logger?.LogInfo("servants: cap book loaded (" + CapBook.Count + " capped hunt(s))");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("servants: the cap book could not be loaded; due hunts are held up to "
                + HoldTracker.TimeoutTicks / ServantLogic.TicksPerSecond + " s, then finish uncapped: " + e.Message);
        }
    }

    /// <summary>Record a send's cap in the book and the server store, flushed to disk now.</summary>
    private static void RecordCap(MissionRow row, int territory, float cap)
    {
        var record = new CapRecord
        {
            MissionGuid = row.MissionGuid,
            StartTicks = row.StartTicks,
            LengthSeconds = row.LengthSeconds,
            Territory = territory,
            MaxSuccess = cap,
            RecordedTicks = DateTime.UtcNow.Ticks,
        };
        string key = CapBook.Add(record);
        try
        {
            _capStore?.Set(key, record);
            ServerSave.SaveAll();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("servants: the cap of mission " + row.MissionGuid + " could not be written to the server store; it holds until Core unloads: " + e.Message);
        }
    }

    private static void ForgetCap(int missionGuid, long startTicks)
    {
        if (!CapBook.Remove(missionGuid, startTicks)) return;
        try
        {
            _capStore?.Remove(ServantCapBook.StoreKey(missionGuid, startTicks));
            ServerSave.SaveAll();
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("servants: dropping a finished hunt's cap from the server store failed: " + e.Message); }
    }

    private static float? CapOf(MissionRow row, Entity heart)
    {
        if (CapBook.Count == 0) return null;
        return CapBook.TryGet(row.MissionGuid, row.StartTicks, TerritoryOf(heart), out var r, out _) ? r!.MaxSuccess : null;
    }

    private static int TerritoryOf(Entity heart)
    {
        try
        {
            var castle = Progenitor.Castles.Castles.ForHeart(CastleId.From(heart));
            return castle.Enabled ? castle.Territory.Index : -1;
        }
        catch { return -1; }
    }

    // ---- before the update --------------------------------------------------------------------------

    /// <summary>The cap's work before <c>ServantMissionUpdateSystem</c>: holds and vetoes.</summary>
    private static void BeforeUpdateCaps(SystemHookContext hook)
    {
        var world = hook.World;
        var em = world.EntityManager;
        if (_sweptEpoch != WorldEpoch.Current)
        {
            _sweptEpoch = WorldEpoch.Current;
            SweepPendingRestores(em);
        }
        if (_capLoaded && CapBook.Count == 0) return;

        long now = DateTime.UtcNow.Ticks;
        bool timers = DebugFlags(em).TimersDisabled;
        if (timers) _heartsDirty = true;
        RefreshHearts(em, now);
        if (_capLoaded && now - _capExpireStamp > CapExpireEveryTicks) ExpireCaps(em, now);
        foreach (var heart in MissionHearts)
        {
            if (!em.Exists(heart)) continue;
            var rows = ReadRows(em, heart);
            int territory = -2;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (!ServantLogic.IsDue(r, now, DueSlackTicks, timers)) continue;
                bool dueNow = ServantLogic.IsDue(r, now, 0, timers);
                var heartId = CastleId.From(heart);
                if (!_capLoaded)
                {
                    if (timers)
                    {
                        if (!_timersHoldWarned)
                            Plugin.Logger?.LogWarning("servants: the cap book is not loaded and the game's servant timers are off, so due hunts cannot be held; they finish uncapped");
                        _timersHoldWarned = true;
                        continue;
                    }
                    if (Holds.Decide(heartId, r.Key, now, out bool firstRelease) == HoldDecision.Hold)
                        HoldRow(em, heart, i, r, "the cap book is not loaded yet");
                    else if (firstRelease)
                        Plugin.Logger?.LogError("servants: the cap book is still not loaded after " + HoldTracker.TimeoutTicks / ServantLogic.TicksPerSecond
                            + " s; mission " + r.MissionGuid + " of heart " + heartId + " finishes WITHOUT its cap, if it had one");
                    continue;
                }
                if (territory == -2) territory = TerritoryOf(heart);
                if (!CapBook.TryGet(r.MissionGuid, r.StartTicks, territory, out var cap, out bool mismatch))
                {
                    if (mismatch)
                        Plugin.Logger?.LogWarning("servants: a cap for mission " + r.MissionGuid + " start " + r.StartTicks
                            + " names another castle territory than heart " + heartId + "'s; not applied");
                    continue;
                }
                if (!dueNow)
                {
                    // Due within the slack but not by this clock: the game may or may not finish it in
                    // this update. Hold it, so the veto happens in the update that finishes it.
                    HoldRow(em, heart, i, r, "a capped hunt due within the clock slack");
                    continue;
                }
                VetoRow(world, em, heartId, r, cap!, territory);
            }
        }
    }

    /// <summary>The veto for one capped row the game finishes this update.</summary>
    private static void VetoRow(World world, EntityManager em, CastleId heart, MissionRow row, CapRecord cap, int territory)
    {
        var chances = ChancesFor(world, em, row.MissionGuid, row.DataSlot, row.Servants(), cap.MaxSuccess, out string why);
        var roll = RollOf(heart, row);
        roll.Chances = chances;
        CapDue.Add((heart.ToEntity(), row));
        if (chances.Count == 0)
        {
            Plugin.Logger?.LogError("servants: the chance of capped mission " + row.MissionGuid + " of heart " + heart
                + " could not be read (" + why + "); it finishes WITHOUT its cap");
            return;
        }
        var parts = new List<string>();
        foreach (var c in chances)
        {
            double q = ServantCapMath.VetoProbability(c.Vanilla, cap.MaxSuccess);
            double u = CapRandom.NextDouble();
            bool veto = u < q;
            if (veto && ForceFailure(em, c.Servant, territory))
            {
                roll.Forced.Add(c.Servant);
                _forcedFailures++;
            }
            parts.Add(c.Servant + " " + Pct(c.Vanilla) + " q=" + q.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + (veto ? (roll.Forced.Contains(c.Servant) ? " FORCED FAILURE" : " veto not applied") : ""));
        }
        Plugin.Logger?.LogInfo("servants: cap " + Pct(cap.MaxSuccess) + " on mission " + row.MissionGuid + " of heart " + heart + ": " + string.Join(", ", parts));
    }

    private static string Pct(float v) => (v * 100f).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";

    /// <summary>Force one servant's roll to fail this update: journal, record, write, read back.</summary>
    private static bool ForceFailure(EntityManager em, CastleId servant, int territory)
    {
        var s = servant.ToEntity();
        try
        {
            byte* ro = AiRuntime.RO<ServantPower>(em, s, Layout.ServantPowerGearLevelOffset + 4);
            if (ro == null) return false;
            float original = *(float*)(ro + Layout.ServantPowerGearLevelOffset);
            if (original.Equals(ServantCapMath.ForcedGearLevel))
            {
                Plugin.Logger?.LogError("servants: servant " + servant + " already reads the forced gear level before the cap wrote it; not touched");
                return false;
            }
            var coffin = CoffinOfServant(em, s);
            byte* c = coffin == Entity.Null ? null : AiRuntime.RO<ServantCoffinstation>(em, coffin, CoffinNeed);
            long ticks = DateTime.UtcNow.Ticks;
            var write = new TransientWrite
            {
                Kind = TransientKind.GearLevel,
                Target = servant,
                Epoch = WorldEpoch.Current,
                NetworkId = NetworkIdOf(em, s),
                Original = original,
                Written = ServantCapMath.ForcedGearLevel,
                WrittenTicks = ticks,
                Territory = territory,
                Name = c == null ? "" : CoffinName(c),
            };
            // Journal and record first: whatever happens after this line, a restore knows about it.
            Journal.Add(write);
            write.StoreKey = GearRestoreRecord.StoreKey(servant, ticks);
            try
            {
                _capStore?.Set(write.StoreKey, new GearRestoreRecord
                {
                    Process = ProcessId,
                    Epoch = write.Epoch,
                    Index = servant.Index,
                    Version = servant.Version,
                    NetworkId = GearRestoreRecord.Hex(write.NetworkId),
                    Territory = territory,
                    Name = write.Name,
                    Original = original,
                    Written = write.Written,
                    WrittenTicks = ticks,
                });
                ServerSave.SaveAll();
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("servants: the pending-restore record of servant " + servant + " could not be written (the in-process restore still runs): " + e.Message);
            }
            byte* rw = AiRuntime.RW<ServantPower>(em, s, Layout.ServantPowerGearLevelOffset + 4);
            *(float*)(rw + Layout.ServantPowerGearLevelOffset) = ServantCapMath.ForcedGearLevel;
            byte* back = AiRuntime.RO<ServantPower>(em, s, Layout.ServantPowerGearLevelOffset + 4);
            bool ok = back != null && (*(float*)(back + Layout.ServantPowerGearLevelOffset)).Equals(ServantCapMath.ForcedGearLevel);
            if (!ok) Plugin.Logger?.LogError("servants: the forced gear level of servant " + servant + " did not read back; the after-update restore undoes whatever is there");
            return ok;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("servants: forcing the failure of servant " + servant + " failed; its roll is the game's: " + e.Message);
            return false;
        }
    }

    /// <summary>Hold a due row this update: raise its length so the game's finish check is false, journaled.</summary>
    private static void HoldRow(EntityManager em, Entity heart, int index, MissionRow row, string why)
    {
        try
        {
            int stride = Layout.ActiveServantMissionStride;
            byte* raw = AiRuntime.Buffer<ActiveServantMission>(em, heart, stride, write: true, out int length);
            if (raw == null || index >= length) return;
            byte* p = raw + index * stride;
            var current = ServantLogic.DecodeRow(new ReadOnlySpan<byte>(p, stride));
            if (current.Key != row.Key) return;
            float original = *(float*)(p + Layout.ActiveServantMissionLengthOffset);
            float written = ServantCapLengthHold(original);
            Journal.Add(new TransientWrite
            {
                Kind = TransientKind.MissionLength,
                Target = CastleId.From(heart),
                Epoch = WorldEpoch.Current,
                RowKey = row.Key,
                Original = original,
                Written = written,
                WrittenTicks = DateTime.UtcNow.Ticks,
            });
            *(float*)(p + Layout.ActiveServantMissionLengthOffset) = written;
            Plugin.Logger?.LogDebug("servants: held mission " + row.MissionGuid + " of heart " + CastleId.From(heart) + " this update (" + why + ")");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("servants: holding mission " + row.MissionGuid + " failed (" + why + "): " + e.Message);
        }
    }

    /// <summary>The held length: the slack plus a second more than the original, so the game's clock cannot reach it this update.</summary>
    internal static float ServantCapLengthHold(float original) => original + DueSlackTicks / (float)ServantLogic.TicksPerSecond + 1f;

    // ---- after the update ---------------------------------------------------------------------------

    /// <summary>A restore later than the update's own after-hook (logged as an error when there was anything to undo).</summary>
    private static void GuardRestore(string when)
    {
        if (Journal.Count == 0) return;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        RestoreAll(world.EntityManager, when, late: true);
    }

    /// <summary>Undo every journaled write, read each back, drop the pending-restore records.</summary>
    private static void RestoreAll(EntityManager em, string when, bool late)
    {
        if (Journal.Count == 0) return;
        var writes = Journal.Drain(late);
        bool storeChanged = false;
        foreach (var w in writes)
        {
            string result;
            try { result = RestoreOne(em, w); }
            catch (Exception e)
            {
                bool again = Journal.Retry(w);
                Plugin.Logger?.LogError("servants: restoring " + w + " " + when + " threw (" + e.Message + "); "
                    + (again ? "retried by the next guard" : "given up; its pending-restore record is kept for the next Core"));
                continue;
            }
            if (w.StoreKey.Length > 0)
            {
                try { _capStore?.Remove(w.StoreKey); storeChanged = true; } catch { }
            }
            if (late)
                Plugin.Logger?.LogError("servants: LATE RESTORE " + when + " (the update's own after-hook did not run): " + w + ": " + result);
            else if (w.Kind == TransientKind.GearLevel || !result.StartsWith("restored", StringComparison.Ordinal))
                Plugin.Logger?.LogInfo("servants: restored " + w + ": " + result);
        }
        if (storeChanged)
        {
            try { ServerSave.SaveAll(); } catch { }
        }
    }

    private static string RestoreOne(EntityManager em, TransientWrite w)
    {
        if (w.Kind == TransientKind.GearLevel)
        {
            var s = w.Target.ToEntity();
            byte* ro = AiRuntime.RO<ServantPower>(em, s, Layout.ServantPowerGearLevelOffset + 4);
            bool found = ro != null;
            bool identity = found && w.Epoch == WorldEpoch.Current && ServantLogic.SameNetworkId(NetworkIdOf(em, s), w.NetworkId);
            float current = found ? *(float*)(ro + Layout.ServantPowerGearLevelOffset) : 0f;
            switch (TransientJournal.Decide(w, found, identity, current))
            {
                case RestoreAction.Write:
                    byte* rw = AiRuntime.RW<ServantPower>(em, s, Layout.ServantPowerGearLevelOffset + 4);
                    *(float*)(rw + Layout.ServantPowerGearLevelOffset) = w.Original;
                    byte* back = AiRuntime.RO<ServantPower>(em, s, Layout.ServantPowerGearLevelOffset + 4);
                    float now = back == null ? float.NaN : *(float*)(back + Layout.ServantPowerGearLevelOffset);
                    if (!now.Equals(w.Original))
                    {
                        Plugin.Logger?.LogError("servants: the gear level of servant " + w.Target + " reads " + now + " after its restore to " + w.Original);
                        return "restore did NOT read back";
                    }
                    return "restored, read back";
                case RestoreAction.AlreadyOriginal: return "restored already";
                case RestoreAction.TargetGone: return "servant gone (destroyed by the game's failure); nothing to restore";
                case RestoreAction.IdentityMismatch:
                    Plugin.Logger?.LogError("servants: the entity at servant " + w.Target + " is not the servant the cap wrote (epoch or network id differ); not touched");
                    return "identity mismatch, not touched";
                default:
                    Plugin.Logger?.LogWarning("servants: the gear level of servant " + w.Target + " changed to " + current + " since the cap wrote it; left as is");
                    return "changed elsewhere, left";
            }
        }

        var heart = w.Target.ToEntity();
        int stride = Layout.ActiveServantMissionStride;
        int length = 0;
        byte* raw = null;
        if (w.Epoch == WorldEpoch.Current && em.Exists(heart))
            raw = AiRuntime.Buffer<ActiveServantMission>(em, heart, stride, write: false, out length);
        int at = -1;
        for (int i = 0; raw != null && i < length && i < 256; i++)
            if (ServantLogic.DecodeRow(new ReadOnlySpan<byte>(raw + i * stride, stride)).Key == w.RowKey) { at = i; break; }
        float cur = at < 0 ? 0f : *(float*)(raw + at * stride + Layout.ActiveServantMissionLengthOffset);
        switch (TransientJournal.Decide(w, at >= 0, true, cur))
        {
            case RestoreAction.Write:
                byte* rwb = AiRuntime.Buffer<ActiveServantMission>(em, heart, stride, write: true, out _);
                *(float*)(rwb + at * stride + Layout.ActiveServantMissionLengthOffset) = w.Original;
                return "restored";
            case RestoreAction.AlreadyOriginal: return "restored already";
            case RestoreAction.TargetGone:
                Plugin.Logger?.LogError("servants: a held hunt row of heart " + w.Target + " is gone after the update it was held in");
                return "row gone";
            default:
                Plugin.Logger?.LogWarning("servants: a held hunt row's length changed since the hold; left as is");
                return "changed elsewhere, left";
        }
    }

    /// <summary>
    /// The cap's work after the update: undo every write, then drop the caps of the hunts that
    /// finished (<paramref name="finished"/>, from the return detector, may be empty when nothing
    /// subscribed: the caps of rows gone from their heart are then dropped by the expiry).
    /// </summary>
    private static void AfterUpdateCaps(SystemHookContext hook)
    {
        RestoreAll(hook.World.EntityManager, "after the update", late: false);
    }

    /// <summary>After the update: the caps of the vetoed rows the game finished go.</summary>
    private static void CapRowsGone(EntityManager em)
    {
        if (CapDue.Count == 0) return;
        foreach (var (heart, row) in CapDue)
        {
            bool still = false;
            if (em.Exists(heart))
                foreach (var r in ReadRows(em, heart))
                    if (r.Key == row.Key) { still = true; break; }
            if (!still) ForgetCap(row.MissionGuid, row.StartTicks);
            else Plugin.Logger?.LogInfo("servants: capped mission " + row.MissionGuid + " was due but the game did not finish it this update; the cap is drawn again next update");
        }
        CapDue.Clear();
    }

    /// <summary>Drop caps whose hunt ended long ago and is in no heart's record.</summary>
    private static void ExpireCaps(EntityManager em, long now)
    {
        _capExpireStamp = now;
        try
        {
            var out_ = new HashSet<(int, long)>();
            var type = ComponentIndex.Of<ActiveServantMission>(ComponentType.AccessMode.ReadOnly);
            foreach (var heart in Query.Entities(em, includeDisabled: true, includeSystems: false, type))
                foreach (var r in ReadRows(em, heart)) out_.Add((r.MissionGuid, r.StartTicks));
            foreach (var r in CapBook.Expired(now, (m, s) => out_.Contains((m, s))))
            {
                Plugin.Logger?.LogInfo("servants: cap of mission " + r.MissionGuid + " start " + r.StartTicks + " expired (the hunt is in no castle's record)");
                ForgetCap(r.MissionGuid, r.StartTicks);
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("servants: cap expiry failed: " + e.Message); }
    }

    /// <summary>
    /// Once per world load, before any cap work: act on pending-restore records a previous Core
    /// generation or process left (it stopped between a write and its restore). Same process and
    /// world load: located by entity, validated by network id, restored when it still reads the
    /// forced value. Otherwise: only a servant whose gear level reads exactly the forced value and
    /// whose castle territory and coffin name match is restored; the rest is logged and dropped.
    /// </summary>
    private static void SweepPendingRestores(EntityManager em)
    {
        if (_capStore == null || !(_capStore is ModStore ms && ms.IsReady)) { _sweptEpoch = int.MinValue; return; }
        var keys = new List<string>();
        foreach (var k in _capStore.Keys) if (k.StartsWith(GearRestoreRecord.StorePrefix, StringComparison.Ordinal)) keys.Add(k);
        if (keys.Count == 0) return;
        Plugin.Logger?.LogError("servants: " + keys.Count + " pending cap restore record(s) found from an earlier Core or process; acting on them now");
        List<(Entity Servant, int Territory, string Name, float Gear)>? forced = null;
        foreach (var key in keys)
        {
            var rec = _capStore.Get<GearRestoreRecord>(key);
            _capStore.Remove(key);
            if (rec == null) continue;
            string outcome;
            if (rec.Process == ProcessId && rec.Epoch == WorldEpoch.Current)
            {
                var w = new TransientWrite
                {
                    Kind = TransientKind.GearLevel,
                    Target = new CastleId(rec.Index, rec.Version),
                    Epoch = rec.Epoch,
                    NetworkId = FromHex(rec.NetworkId),
                    Original = rec.Original,
                    Written = rec.Written,
                };
                outcome = RestoreOne(em, w);
            }
            else
            {
                forced ??= ForcedServants(em);
                var view = new List<(int, string, float)>();
                foreach (var f in forced) view.Add((f.Territory, f.Name, f.Gear));
                int at = rec.MatchForeign(view);
                if (at < 0)
                    outcome = "no servant reads the forced value in that castle (the world on disk never held it); dropped";
                else
                {
                    var s = forced[at].Servant;
                    byte* rw = AiRuntime.RW<ServantPower>(em, s, Layout.ServantPowerGearLevelOffset + 4);
                    *(float*)(rw + Layout.ServantPowerGearLevelOffset) = rec.Original;
                    byte* back = AiRuntime.RO<ServantPower>(em, s, Layout.ServantPowerGearLevelOffset + 4);
                    outcome = back != null && (*(float*)(back + Layout.ServantPowerGearLevelOffset)).Equals(rec.Original)
                        ? "restored servant " + CastleId.From(s) + " to " + rec.Original + ", read back"
                        : "restore of servant " + CastleId.From(s) + " did NOT read back";
                    forced.RemoveAt(at);
                }
            }
            Plugin.Logger?.LogError("servants: pending restore of '" + rec.Name + "' (territory " + rec.Territory + "): " + outcome);
        }
        if (forced != null)
            foreach (var f in forced)
                Plugin.Logger?.LogError("servants: servant " + CastleId.From(f.Servant) + " ('" + f.Name + "', territory " + f.Territory
                    + ") reads the forced gear level with no matching restore record; not touched. Report this.");
        try { ServerSave.SaveAll(); } catch { }
    }

    private static List<(Entity Servant, int Territory, string Name, float Gear)> ForcedServants(EntityManager em)
    {
        var list = new List<(Entity, int, string, float)>();
        foreach (var s in Query.Entities(em, includeDisabled: true, includeSystems: false, ComponentIndex.Of<ServantPower>(ComponentType.AccessMode.ReadOnly)))
        {
            byte* p = AiRuntime.RO<ServantPower>(em, s, Layout.ServantPowerGearLevelOffset + 4);
            if (p == null) continue;
            float gear = *(float*)(p + Layout.ServantPowerGearLevelOffset);
            if (!gear.Equals(ServantCapMath.ForcedGearLevel)) continue;
            var coffin = CoffinOfServant(em, s);
            byte* c = coffin == Entity.Null ? null : AiRuntime.RO<ServantCoffinstation>(em, coffin, CoffinNeed);
            var heart = coffin == Entity.Null ? Entity.Null : HeartOf(em, coffin);
            list.Add((s, heart == Entity.Null ? -1 : TerritoryOf(heart), c == null ? "" : CoffinName(c), gear));
        }
        return list;
    }

    private static byte[] FromHex(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return Array.Empty<byte>();
        var b = new byte[hex.Length / 2];
        for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return b;
    }
}
