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
/// <c>ServantMissionUpdateSystem</c> before/after hooks (registered last and first): no game method
/// is called or patched.
///
/// <para><b>Mechanism.</b> The game finishes at most one row per heart per update: the first due row
/// in record order (<c>FindFinishedMissionsJob</c>). Before the update, <see cref="CapSelection"/>
/// picks, per heart, the row the game will finish; when it is capped, each servant's vanilla chance
/// <c>c</c> is computed from the live inputs the game reads next, and with probability
/// <c>1 - cap / c</c> the servant's <c>ServantPower.GearLevel</c> is set to
/// <see cref="ServantCapMath.ForcedGearLevel"/> for this update, so the game's own roll fails. After
/// the update every temporary write is undone and read back. A capped row the game may or may not
/// finish this update (due only within the clock slack, after an uncertain row, or while a save is
/// in flight) is held this update instead: its row length is raised for the update only.</para>
///
/// <para><b>Saves.</b> The game's save (<c>SerializePersistenceSystemV2.TriggerSave</c>) schedules its
/// serialization jobs and keeps the handle in <c>_WriteToDiskDependency</c> until a later update of
/// that system completes and finalizes them; those jobs hold no <c>ServantPower</c> handle, so a raw
/// write does not wait for them. So no veto is made while that handle is set (the row is held), and
/// a pending-restore record is kept until a save that started after the restore has completed.</para>
///
/// <para><b>Restore, always.</b> The journal is drained after the update; if that hook does not run
/// (the update threw), after <c>UpdateGroup</c> (which catches its systems' exceptions), before the
/// next <c>PersistenceGroup</c>, on the next frame tick, before the next update and at Core unload;
/// every late restore is logged as an error.</para>
///
/// <para><b>Scope.</b> The server store is machine-wide (hosted games share it; it survives wipes),
/// so caps and pending-restore records are keyed by the world's id, kept in the world's own
/// <see cref="ModSave"/> store (a wipe or another save starts a new id). The cap expiry and the
/// pending-restore sweep run only once the world is loaded, and only over that world's keys.</para>
/// </summary>
internal static unsafe partial class ServantRuntime
{
    /// <summary>The server store (and the world store) that hold the cap book, the pending restores and the world's id.</summary>
    internal const string CapStoreGuid = "progenitor.servants";

    private const string WorldIdKey = "worldId";

    /// <summary>The world store is loaded and holds no id: no capped send was made in this world, so it has no caps.</summary>
    private const string NoWorldId = "-";
    private const long CapExpireEveryTicks = 60 * ServantLogic.TicksPerSecond;

    /// <summary>
    /// <c>Nullable&lt;JobHandle&gt;.hasValue</c> inside <c>_WriteToDiskDependency</c>: the instantiated
    /// <c>System_Nullable_JobHandle__Fields</c> is <c>{ bool hasValue; JobHandle value; }</c> (server
    /// metadata header), so the flag is at +0 and the 8-aligned 12-byte handle at +8; the field spans
    /// 0x18 bytes, which is where <c>_Initialized</c> starts (<see cref="Layout.SerializePersistenceSystemV2InitializedOffset"/>).
    /// </summary>
    private const int NullableHasValueOffset = 0;

    private static readonly ServantCapBook CapBook = new();
    private static readonly TransientJournal Journal = new();
    private static readonly HoldTracker Holds = new();
    private static readonly SaveTracker Saves = new();
    private static readonly RetainedRestores Retained = new();
    private static readonly Random CapRandom = new();
    private static IModStore? _capStore;
    private static IDisposable? _guardGroup, _guardPersist, _saveWatch;
    private static long _forcedFailures;
    private static long _capExpireStamp;
    private static int _sweptEpoch = int.MinValue;
    private static bool _timersHoldWarned, _saveUnreadableWarned, _installedIntoRunningWorld;
    private static string? _processId;
    private static string? _worldId;
    private static int _worldIdEpoch = int.MinValue;
    private static bool? _saveOffsetChecked;

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

    private static bool CapLoaded => !string.IsNullOrEmpty(_worldId) && CapBook.World == _worldId && StoreReady;

    private static bool StoreReady => _capStore is ModStore ms && ms.IsReady;

    internal static ServantCapStatus CapStatus() => new()
    {
        Ready = CapLoaded,
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
        var world = Worlds.Server;
        _installedIntoRunningWorld = world != null && world.IsCreated;
        _guardGroup = SystemHooks.After<UpdateGroup>(_ => GuardRestore("after the update group"), int.MaxValue);
        _guardPersist = SystemHooks.Before<PersistenceGroup>(_ => GuardRestore("before the persistence group"), int.MaxValue);
        _saveWatch = SystemHooks.After<SerializePersistenceSystemV2>(ObserveSave, int.MaxValue);
    }

    private static void UninstallCaps()
    {
        // The world may still be up (a Core swap): undo anything outstanding while it is.
        GuardRestore("at Core unload");
        foreach (var h in new[] { _guardGroup, _guardPersist, _saveWatch })
        {
            try { h?.Dispose(); } catch { }
        }
        _guardGroup = _guardPersist = _saveWatch = null;
        if (Journal.Count > 0)
            Plugin.Logger?.LogError("servants: " + Journal.Count + " temporary cap write(s) could not be undone at Core unload (no server world); "
                + "their pending-restore records stay in the server store for the next Core");
        Journal.Clear();
        // Kept records stay in the store; the next Core's sweep re-keeps them until a later save.
        Retained.Clear();
        try { ServerSave.SaveAll(); } catch { }
        CapBook.Clear();
        CapBook.World = "";
        Holds.Clear();
        _capStore = null;
        _worldId = null;
        _worldIdEpoch = int.MinValue;
        _sweptEpoch = int.MinValue;
    }

    /// <summary>
    /// The world's id, from its own <see cref="ModSave"/> store (created on first need), cached per
    /// world load; null while that store is not loaded.
    /// </summary>
    private static string? WorldId(bool create)
    {
        if (_worldIdEpoch == WorldEpoch.Current && _worldId != null && (_worldId != NoWorldId || !create)) return _worldId;
        var store = ModSave.Store(CapStoreGuid);
        if (!(store is ModStore ms && ms.IsReady)) return null;
        string id = store.Get<string>(WorldIdKey) ?? "";
        if (id.Length == 0)
        {
            if (!create)
            {
                // No capped send was ever made in this world: it has no caps (and no records) at all.
                _worldId = NoWorldId;
                _worldIdEpoch = WorldEpoch.Current;
                return NoWorldId;
            }
            id = Guid.NewGuid().ToString("N").Substring(0, 16);
            store.Set(WorldIdKey, id);
            try { ModSave.SaveAll(); } catch (Exception e) { Plugin.Logger?.LogWarning("servants: saving the world's cap id failed: " + e.Message); }
            Plugin.Logger?.LogInfo("servants: this world's cap id is " + id);
        }
        _worldId = id;
        _worldIdEpoch = WorldEpoch.Current;
        return id;
    }

    /// <summary>Load the book for the current world when it is not loaded; true when caps can be read.</summary>
    private static bool EnsureBook(bool create = false)
    {
        if (!StoreReady) return false;
        var id = WorldId(create);
        if (string.IsNullOrEmpty(id)) return false;
        if (CapBook.World == id) return true;
        if (id == NoWorldId)
        {
            CapBook.Clear();
            CapBook.World = NoWorldId;
            Holds.Clear();
            return true;
        }
        try
        {
            CapBook.Clear();
            CapBook.World = id!;
            foreach (var key in _capStore!.Keys)
            {
                if (ServantCapBook.FromStoreKey(key, id!) == null) continue;
                var r = _capStore.Get<CapRecord>(key);
                if (r != null && ServantCapMath.IsValidCap(r.MaxSuccess) && r.MissionGuid != 0) CapBook.Add(r);
            }
            Holds.Clear();
            Plugin.Logger?.LogInfo("servants: cap book loaded for world " + id + " (" + CapBook.Count + " capped hunt(s))");
            return true;
        }
        catch (Exception e)
        {
            CapBook.Clear();
            CapBook.World = "";
            Plugin.Logger?.LogError("servants: the cap book could not be loaded; due hunts are held up to "
                + HoldTracker.TimeoutTicks / ServantLogic.TicksPerSecond + " s, then finish uncapped: " + e.Message);
            return false;
        }
    }

    /// <summary>For a capped send: the book is readable (or can be created) now.</summary>
    private static bool CapBookWritable() => EnsureBook(create: true);

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
            _capStore?.Remove(CapBook.StoreKey(missionGuid, startTicks));
            ServerSave.SaveAll();
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("servants: dropping a finished hunt's cap from the server store failed: " + e.Message); }
    }

    /// <summary>The Dev bridge's finish moved a row's start: carry its cap along.</summary>
    internal static void RekeyCap(int missionGuid, long oldStart, long newStart)
    {
        if (!CapLoaded) return;
        var moved = CapBook.Rekey(missionGuid, oldStart, newStart);
        if (moved == null) return;
        try
        {
            _capStore?.Remove(moved.Value.Old);
            _capStore?.Set(moved.Value.New, moved.Value.Record);
            ServerSave.SaveAll();
            Plugin.Logger?.LogInfo("servants: the cap of mission " + missionGuid + " follows its row to the new start " + newStart);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("servants: moving a cap to its row's new start failed: " + e.Message); }
    }

    private static float? CapOf(MissionRow row, Entity heart)
    {
        if (!CapLoaded || CapBook.Count == 0) return null;
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

    // ---- the game's save in flight ------------------------------------------------------------------

    /// <summary>
    /// True while the game's save has serialization jobs scheduled and not yet finalized
    /// (<c>SerializePersistenceSystemV2._WriteToDiskDependency.HasValue</c>, the system's own
    /// <c>IsSaving</c>), read raw from the managed system object at the declared offset, after
    /// checking that offset once against the runtime's own field offset. Null when unreadable.
    /// </summary>
    private static bool? SaveInFlight(World world)
    {
        try
        {
            var system = world.GetExistingSystemManaged<SerializePersistenceSystemV2>();
            if (system == null) return null;
            if (_saveOffsetChecked == null)
            {
                uint runtime = Progenitor.Access.AccessResolver.FieldOffset(typeof(SerializePersistenceSystemV2), "SerializePersistenceSystemV2", "_WriteToDiskDependency");
                _saveOffsetChecked = runtime == (uint)Layout.SerializePersistenceSystemV2WriteToDiskDependencyOffset;
                if (_saveOffsetChecked == false)
                    Plugin.Logger?.LogError("servants: the save system's in-flight field is at 0x" + runtime.ToString("X") + ", not the declared 0x"
                        + Layout.SerializePersistenceSystemV2WriteToDiskDependencyOffset.ToString("X") + "; capped hunts finish uncapped until the layout is fixed");
            }
            if (_saveOffsetChecked != true) return null;
            byte* p = (byte*)system.Pointer;
            if (p == null) return null;
            return p[Layout.SerializePersistenceSystemV2WriteToDiskDependencyOffset + NullableHasValueOffset] != 0;
        }
        catch (Exception e)
        {
            if (!_saveUnreadableWarned)
                Plugin.Logger?.LogError("servants: the game's save state could not be read; capped hunts finish uncapped: " + e.Message);
            _saveUnreadableWarned = true;
            return null;
        }
    }

    /// <summary>After the save system's update: count saves started and finished, release kept records.</summary>
    private static void ObserveSave(SystemHookContext hook)
    {
        try
        {
            var inFlight = SaveInFlight(hook.World);
            if (inFlight == null) return;
            Saves.Observe(inFlight.Value);
            if (Retained.Count == 0) return;
            var done = Retained.Release(Saves);
            if (done.Count == 0) return;
            foreach (var key in done) _capStore?.Remove(key);
            ServerSave.SaveAll();
            Plugin.Logger?.LogInfo("servants: " + done.Count + " pending cap restore record(s) dropped: a save that started after the restore completed");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("servants: watching the game's save failed: " + e.Message); }
    }

    // ---- before the update --------------------------------------------------------------------------

    /// <summary>The cap's work before <c>ServantMissionUpdateSystem</c>: the sweep, expiry, holds and vetoes.</summary>
    private static void BeforeUpdateCaps(SystemHookContext hook)
    {
        var world = hook.World;
        var em = world.EntityManager;
        bool ready = EnsureBook();
        bool loaded = CapGate.WorldLoaded(WorldEpoch.Loads, _installedIntoRunningWorld);
        if (_sweptEpoch != WorldEpoch.Current && CapGate.CanSweep(loaded, StoreReady, ready && _worldId != NoWorldId ? _worldId : null))
        {
            _sweptEpoch = WorldEpoch.Current;
            SweepPendingRestores(em, _worldId!);
        }
        if (ready && CapBook.Count == 0) return;
        if (Singleton<ServantMissionSettingsSingleton>(em, ref _missionSettings) == Entity.Null) return; // the game finishes nothing without it

        long now = DateTime.UtcNow.Ticks;
        bool timers = DebugFlags(em).TimersDisabled;
        if (timers) _heartsDirty = true;
        RefreshHearts(em, now);
        if (ready && loaded && now - _capExpireStamp > CapExpireEveryTicks) ExpireCaps(em, now);

        bool? saving = SaveInFlight(world);
        if (saving != null) Saves.Observe(saving.Value);
        if (!ready && timers && !_timersHoldWarned)
        {
            Plugin.Logger?.LogWarning("servants: the cap book is not loaded and the game's servant timers are off, so due hunts cannot be held; they finish uncapped");
            _timersHoldWarned = true;
        }

        foreach (var heart in MissionHearts)
        {
            if (!em.Exists(heart)) continue;
            var rows = ReadRows(em, heart);
            if (rows.Count == 0) continue;
            var heartId = CastleId.From(heart);
            int territory = TerritoryOf(heart);
            var states = new List<CapRowState>(rows.Count);
            var caps = new CapRecord?[rows.Count];
            foreach (var r in rows)
            {
                bool dueSoon = ServantLogic.IsDue(r, now, DueSlackTicks, timers);
                bool dueNow = ServantLogic.IsDue(r, now, 0, timers);
                bool capped = false, mustHold = false;
                if (dueSoon && ready)
                {
                    capped = CapBook.TryGet(r.MissionGuid, r.StartTicks, territory, out var rec, out bool mismatch);
                    caps[states.Count] = rec;
                    if (mismatch)
                        Plugin.Logger?.LogWarning("servants: a cap for mission " + r.MissionGuid + " start " + r.StartTicks
                            + " names another castle territory than heart " + heartId + "'s; not applied");
                }
                else if (dueSoon && !timers)
                {
                    mustHold = Holds.Decide(heartId, r.Key, now, out bool firstRelease) == HoldDecision.Hold;
                    if (firstRelease)
                        Plugin.Logger?.LogError("servants: the cap book is still not loaded after " + HoldTracker.TimeoutTicks / ServantLogic.TicksPerSecond
                            + " s; mission " + r.MissionGuid + " of heart " + heartId + " finishes WITHOUT its cap, if it had one");
                }
                if (capped && saving == null)
                {
                    capped = false; // fail toward vanilla: the save state is unknown
                    if (!_saveUnreadableWarned)
                        Plugin.Logger?.LogError("servants: the game's save state is unreadable; capped hunts finish uncapped");
                    _saveUnreadableWarned = true;
                }
                states.Add(new CapRowState(dueSoon, dueNow, capped, mustHold));
            }
            var actions = CapSelection.Select(states, timers, saving == true, out bool uncapped);
            if (uncapped && saving == true)
                Plugin.Logger?.LogWarning("servants: a capped hunt of heart " + heartId + " finishes during a save with the game's servant timers off (it cannot be held); it finishes WITHOUT its cap");
            for (int i = 0; i < actions.Length; i++)
            {
                if (actions[i] == CapRowAction.Hold)
                    HoldRow(em, heart, i, rows[i], !ready ? "the cap book is not loaded yet" : saving == true ? "a save is in flight" : "a capped hunt the game may not finish this update");
                else if (actions[i] == CapRowAction.Veto && caps[i] != null)
                    VetoRow(world, em, heartId, rows[i], caps[i]!, territory);
            }
        }
    }

    /// <summary>The veto for the one capped row of a heart the game finishes this update.</summary>
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
            bool veto = CapRandom.NextDouble() < q;
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
            if (ro == null || string.IsNullOrEmpty(_worldId) || _worldId == NoWorldId) return false;
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
                StoreKey = GearRestoreRecord.StoreKey(_worldId!, servant, ticks),
            };
            // Journal and record first: whatever happens after this line, a restore knows about it.
            Journal.Add(write);
            try
            {
                _capStore?.Set(write.StoreKey, RecordOf(write));
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

    private static GearRestoreRecord RecordOf(TransientWrite w, bool restored = false) => new()
    {
        Process = ProcessId,
        World = _worldId ?? "",
        Epoch = w.Epoch,
        Index = w.Target.Index,
        Version = w.Target.Version,
        NetworkId = GearRestoreRecord.Hex(w.NetworkId),
        Territory = w.Territory,
        Name = w.Name,
        Original = w.Original,
        Written = w.Written,
        WrittenTicks = w.WrittenTicks,
        Restored = restored,
    };

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

    /// <summary>
    /// Undo every journaled write and read each back. A gear write's pending-restore record is
    /// marked restored and kept until a save that started after now completes; a restore that threw
    /// is retried, one that did not read back keeps its record unrestored.
    /// </summary>
    private static void RestoreAll(EntityManager em, string when, bool late)
    {
        if (Journal.Count == 0) return;
        var writes = Journal.Drain(late);
        bool storeChanged = false;
        foreach (var w in writes)
        {
            RestoreResult result;
            try { result = RestoreOne(em, w); }
            catch (Exception e)
            {
                bool again = Journal.Retry(w);
                Plugin.Logger?.LogError("servants: restoring " + w + " " + when + " threw (" + e.Message + "); "
                    + (again ? "retried by the next guard" : "given up; its pending-restore record is kept for the next Core"));
                continue;
            }
            if (w.StoreKey.Length > 0 && result.Done)
            {
                try
                {
                    _capStore?.Set(w.StoreKey, RecordOf(w, restored: true));
                    Retained.Keep(w.StoreKey, Saves.Mark());
                    storeChanged = true;
                }
                catch { }
            }
            if (late)
                Plugin.Logger?.LogError("servants: LATE RESTORE " + when + " (the update's own after-hook did not run): " + w + ": " + result.Text);
            else if (w.Kind == TransientKind.GearLevel || !result.Done)
                Plugin.Logger?.LogInfo("servants: restored " + w + ": " + result.Text);
        }
        if (storeChanged)
        {
            try { ServerSave.SaveAll(); } catch { }
        }
    }

    private readonly struct RestoreResult
    {
        internal RestoreResult(bool done, string text) { Done = done; Text = text; }

        /// <summary>The target holds no forced value any more (restored, already original, gone, or changed by the game).</summary>
        internal bool Done { get; }

        internal string Text { get; }
    }

    private static RestoreResult RestoreOne(EntityManager em, TransientWrite w)
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
                        return new RestoreResult(false, "restore did NOT read back; its record stays for the next Core");
                    }
                    return new RestoreResult(true, "restored, read back");
                case RestoreAction.AlreadyOriginal: return new RestoreResult(true, "restored already");
                case RestoreAction.TargetGone: return new RestoreResult(true, "servant gone (destroyed by the game's failure); nothing to restore");
                case RestoreAction.IdentityMismatch:
                    Plugin.Logger?.LogError("servants: the entity at servant " + w.Target + " is not the servant the cap wrote (epoch or network id differ); not touched");
                    return new RestoreResult(false, "identity mismatch, not touched; its record stays for the next Core");
                default:
                    Plugin.Logger?.LogWarning("servants: the gear level of servant " + w.Target + " changed to " + current + " since the cap wrote it; left as is");
                    return new RestoreResult(true, "changed elsewhere, left");
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
                return new RestoreResult(true, "restored");
            case RestoreAction.AlreadyOriginal: return new RestoreResult(true, "restored already");
            case RestoreAction.TargetGone:
                Plugin.Logger?.LogError("servants: a held hunt row of heart " + w.Target + " is gone after the update it was held in");
                return new RestoreResult(true, "row gone");
            default:
                Plugin.Logger?.LogWarning("servants: a held hunt row's length changed since the hold; left as is");
                return new RestoreResult(true, "changed elsewhere, left");
        }
    }

    /// <summary>The cap's work after the update: undo every write.</summary>
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

    /// <summary>Drop this world's caps whose hunt ended long ago and is in no heart's record.</summary>
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
    /// Once per world load, once the world is loaded, before any cap work: act on this world's
    /// pending-restore records. Same process and world load (a Core swap): located by entity,
    /// validated by network id, restored when it still reads the forced value, then kept until a
    /// later save completes. Otherwise (a restart): only a servant whose gear level reads exactly
    /// the forced value and whose castle territory and coffin name match one record is restored
    /// (then kept the same way); a record that matches none is dropped, because the loaded world
    /// does not hold the forced value.
    /// </summary>
    private static void SweepPendingRestores(EntityManager em, string world)
    {
        if (_capStore == null) return;
        var keys = new List<string>();
        foreach (var k in _capStore.Keys) if (GearRestoreRecord.IsKeyOf(k, world)) keys.Add(k);
        if (keys.Count == 0) return;
        Plugin.Logger?.LogWarning("servants: " + keys.Count + " pending cap restore record(s) of this world found; checking them now");
        List<(Entity Servant, int Territory, string Name, float Gear)>? forced = null;
        foreach (var key in keys)
        {
            var rec = _capStore.Get<GearRestoreRecord>(key);
            if (rec == null) { _capStore.Remove(key); continue; }
            string outcome;
            bool keep;
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
                var r = RestoreOne(em, w);
                outcome = r.Text;
                keep = true;
                if (r.Done) rec.Restored = true;
            }
            else
            {
                forced ??= ForcedServants(em);
                var view = new List<(int, string, float)>();
                foreach (var f in forced) view.Add((f.Territory, f.Name, f.Gear));
                int at = rec.MatchForeign(view);
                if (at < 0)
                {
                    outcome = "no servant reads the forced value in that castle (the loaded world does not hold it); dropped";
                    keep = false;
                }
                else
                {
                    var s = forced[at].Servant;
                    byte* rw = AiRuntime.RW<ServantPower>(em, s, Layout.ServantPowerGearLevelOffset + 4);
                    *(float*)(rw + Layout.ServantPowerGearLevelOffset) = rec.Original;
                    byte* back = AiRuntime.RO<ServantPower>(em, s, Layout.ServantPowerGearLevelOffset + 4);
                    bool ok = back != null && (*(float*)(back + Layout.ServantPowerGearLevelOffset)).Equals(rec.Original);
                    outcome = ok
                        ? "restored servant " + CastleId.From(s) + " to " + rec.Original + ", read back (the loaded world held the forced value)"
                        : "restore of servant " + CastleId.From(s) + " did NOT read back";
                    rec.Restored = ok;
                    keep = true;
                    forced.RemoveAt(at);
                }
            }
            if (keep)
            {
                _capStore.Set(key, rec);
                if (rec.Restored) Retained.Keep(key, Saves.Mark());
            }
            else _capStore.Remove(key);
            Plugin.Logger?.LogWarning("servants: pending restore of '" + rec.Name + "' (territory " + rec.Territory + "): " + outcome);
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
