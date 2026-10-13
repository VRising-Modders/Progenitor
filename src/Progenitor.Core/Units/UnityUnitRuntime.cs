#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ProjectM;
using ProjectM.Behaviours;
using ProjectM.Network;
using ProjectM.Shared;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;

namespace Progenitor.Units;

/// <summary>
/// Live server runtime. Spawn is Instantiate + <see cref="UnitSpawnPlacement"/>
/// (Translation, LastTranslation, SpawnTransform.Position; evidence §11 / 108).
/// Follow is the Follower byte* overlay at Followed @0 / Mode @24 / Stationary @28 plus
/// ExternalBehaviourTreeTransitionRequest Follow (evidence 33). Prefabs that do not ship
/// Follower (CHAR_Critter_Rat, evidence 116) get the component added here and removed on
/// GoTo / Despawn. GoTo is the ApproachPosition.Tick
/// AiMove_Server field set via overlay, never SetComponentDataRaw of a guessed blob
/// (evidence 46 / §32). Despawn refuses PlayerCharacter/User, then DIAG5 two-phase:
/// DestroyBuffs this frame; wait until every copied buff id is gone (stale BuffBuffer
/// rows are ignored; a new live row is destroyed now and starts another cycle, capped
/// at BuffSweep.MaxCycles, then stuck without tagging); only then DestroyUtility.Destroy
/// the unit, alone, so the removal replicates (BuffSweep.DestroyUnitReplicated; DestroyTag
/// is a logged fallback after BuffSweep.UnitDestroyFallbackFrames). The unit is never
/// destroyed while a listed buff Exists.
/// </summary>
internal sealed unsafe partial class UnityUnitRuntime : IUnitRuntime, IUnitBehaviourRuntime, IUnitStanceRuntime
{
    /// <summary>ApproachPosition node min used by the spike. A 6 m goal sits outside the band so Tick keeps Running.</summary>
    internal const float ApproachMin = 0f;

    /// <summary>ApproachPosition node max / ArrivalDistance. Inner becomes (min+max)*0.5 when goal is -1.</summary>
    internal const float ApproachMax = 1f;

    internal const float ApproachGoal = -1f;

    /// <summary>Stationary lives at Follower offset 28; a known-smaller slot cannot take that store.</summary>
    internal const int FollowerMinSlot = 29;

    /// <summary>FreezeRotationWhenStationary is AiMove_Server @77; TargettingMode is @72.</summary>
    internal const int AiMoveMinSlot = 78;

    public bool IsServer => true;

    public string? LastMissReason { get; private set; }

    /// <summary>Units whose prefab had no Follower; we added it and must remove on GoTo / Despawn.</summary>
    private readonly HashSet<UnitId> _addedFollower = new();

    /// <summary>
    /// Entities whose removal this runtime started: world epoch and start time (ms). A removal
    /// normally finishes in about 2 s; an entry older than <see cref="DespawnPendingMs"/> no longer
    /// blocks a new attempt (a DIAG5 despawn that ended stuck can be retried).
    /// </summary>
    private readonly Dictionary<Entity, (int Epoch, long Started)> _despawnPending = new();

    private const long DespawnPendingMs = 10_000;

    /// <summary>
    /// Despawns started here whose unit destroy has not been issued yet, with the unit's PrefabGUID
    /// hash. Core uninstall drops the poll that would finish them, so <c>Units.Uninstall</c> hands
    /// these to Bootstrap (<see cref="InFlightDespawns"/>).
    /// </summary>
    private readonly Dictionary<Entity, int> _inFlight = new();

    /// <summary>Despawns with a poll queued right now: the work Core uninstall would drop.</summary>
    private readonly HashSet<Entity> _awaitingPoll = new();

    private readonly UnityClearArea.SummonSweep _summons;

    internal UnityUnitRuntime() => _summons = new UnityClearArea.SummonSweep(this);

    /// <summary>
    /// Record <paramref name="entity"/> as being removed. False when a removal of the same entity is
    /// already in flight in this world. Prunes entries whose entity is gone, whose world reloaded, or
    /// that expired.
    /// </summary>
    private bool MarkDespawnPending(EntityManager em, Entity entity)
    {
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        long now = Environment.TickCount64;
        if (_despawnPending.TryGetValue(entity, out var at)
            && at.Epoch == epoch && now - at.Started < DespawnPendingMs && SafeExists(em, entity))
            return false;
        if (_despawnPending.Count >= 256)
        {
            var gone = new List<Entity>();
            foreach (var kv in _despawnPending)
                if (kv.Value.Epoch != epoch || now - kv.Value.Started >= DespawnPendingMs || !SafeExists(em, kv.Key))
                    gone.Add(kv.Key);
            foreach (var e in gone) _despawnPending.Remove(e);
        }
        _despawnPending[entity] = (epoch, now);
        return true;
    }

    private static bool SafeExists(EntityManager em, Entity e)
    {
        try { return em.Exists(e); }
        catch { return false; }
    }

    public UnitId Spawn(string prefabName, int prefabGuid, UnitVec3 position)
    {
        LastMissReason = null;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated)
        {
            LastMissReason = "Units: no Server world; returning a disabled handle.";
            return UnitId.None;
        }

        var em = world.EntityManager;
        if (!TryResolvePrefab(world, em, prefabName, prefabGuid, out var prefab, out var resolvedName))
            return UnitId.None;

        Entity unit;
        try { unit = em.Instantiate(prefab); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Units: Instantiate '" + resolvedName + "' threw: " + e);
            LastMissReason = "Units: Instantiate '" + resolvedName + "' failed; returning a disabled handle.";
            return UnitId.None;
        }

        if (unit == Entity.Null || !em.Exists(unit))
        {
            LastMissReason = "Units: Instantiate '" + resolvedName + "' returned a dead entity.";
            return UnitId.None;
        }

        var pos = new float3(position.X, position.Y, position.Z);
        if (!UnitSpawnPlacement.TryPlace(em, unit, pos))
        {
            LastMissReason = "Units: Translation write failed on '" + resolvedName + "'; returning a disabled handle.";
            TryDespawnEntity(em, unit);
            return UnitId.None;
        }

        return UnitId.From(unit);
    }

    public bool Follow(UnitId unitId, UnitId targetId)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit))
            return false;
        var target = targetId.ToEntity();
        if (target == Entity.Null || !em.Exists(target))
            return false;

        if (!UnitSpawnPlacement.TryReadTranslation(em, unit, out var start))
            return false;

        UnitSpawnPlacement.TryWriteSpawnTransform(em, unit, start);
        UnitSpawnPlacement.TryWriteLastTranslation(em, unit, start);
        // Follow after GoTo: drop ApproachPosition so FollowerSystem is the only writer.
        TryClearGoToGoal(em, unit);
        bool sameTarget = TryReadFollowerFollowed(em, unit, out var current) && current == target;

        bool addedThisCall = false;
        try
        {
            if (!unit.Has<Follower>(em))
            {
                if (!TryAddFollower(em, unit))
                    return false;
                addedThisCall = true;
                _addedFollower.Add(unitId);
                Plugin.Logger?.LogInfo("Units: added Follower to " + Fmt(unit) + " (prefab had none)");
                sameTarget = false;
            }
        }
        catch (Exception e)
        {
            LogRefuseOnce("Follower.add.throw", "Units: Follower add refused on " + Fmt(unit) + ": " + e.Message);
            return false;
        }

        bool overlay = TryWriteFollowerFollowed(em, unit, target);
        if (!overlay)
        {
            if (addedThisCall)
            {
                TryRemoveFollower(em, unit);
                _addedFollower.Remove(unitId);
            }
            Plugin.Logger?.LogWarning("Units: Follower overlay refused on " + unitId + " (logged once per type).");
        }
        // Re-post Follow only when the target changed (R25 #6); a second Follow(same) would leak request entities.
        bool followReq = overlay && (sameTarget || TryPostBtRequest(em, unit, GenericEnemyState.Follow));
        return overlay && followReq;
    }

    /// <summary>
    /// Start a fight. Combat transition plus aggro target. The prefab's
    /// <c>Immortal</c> flag is left alone: some bosses clear it as a stage.
    /// </summary>
    public UnitEngageResult Engage(UnitId unitId, UnitId targetId)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit))
            return default;
        var target = targetId.ToEntity();
        if (target == Entity.Null || !em.Exists(target))
            return default;
        bool combat = TryPostBtRequest(em, unit, GenericEnemyState.Combat);
        bool aggro = TryWriteAggroTarget(em, unit, target);
        return new UnitEngageResult(combat, aggro);
    }

    /// <summary>
    /// <c>AggroConsumer.AggroTarget</c> is a <c>NetworkedEntity</c> at 0x30.
    /// <c>Active</c> is the <c>ModifiableBool</c> at 0x50. Raw write, not
    /// <c>GetComponentData&lt;AggroConsumer&gt;</c>.
    /// </summary>
    private static bool TryWriteAggroTarget(EntityManager em, Entity unit, Entity target)
    {
        const int targetOffset = Layout.AggroConsumerAggroTargetOffset;
        const int activeOffset = Layout.AggroConsumerActiveOffset + Layout.ModifiableBoolValueOffset;
        const int minBytes = AggroConsumerMinBytes;
        try
        {
            if (!unit.Has<AggroConsumer>(em))
            {
                LogRefuseOnce("AggroConsumer.missing", "Units: no AggroConsumer on " + Fmt(unit) + "; Engage left aggro unset.");
                return false;
            }

            var typeIndex = ComponentIndex.IndexOf<AggroConsumer>();
            int typeSize = TypeSizeOf(typeIndex);
            int sizeInChunk = SizeInChunkOf(typeIndex);
            int extent = typeSize > 0 ? typeSize : sizeInChunk;
            if (extent > 0 && extent < minBytes)
            {
                LogRefuseOnce("AggroConsumer.size", "Units: AggroConsumer overlay refused: extent=" + extent + " < " + minBytes + ".");
                return false;
            }

            byte* raw = (byte*)em.GetComponentDataRawRW(unit, typeIndex);
            if (raw == null)
            {
                LogRefuseOnce("AggroConsumer.null", "Units: AggroConsumer GetComponentDataRawRW returned null.");
                return false;
            }

            *(int*)(raw + targetOffset) = target.Index;
            *(int*)(raw + targetOffset + 4) = target.Version;
            *(raw + targetOffset + Layout.NetworkedEntityWaitingForSyncOffset) = 0;
            *(raw + activeOffset) = 1;
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("AggroConsumer.throw", "Units: AggroConsumer write threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// One pet step (see <c>ctx.Units.SetPet</c>). With a live aggro target that is not the owner, the
    /// unit leaves Follow (Follower.Followed cleared, Combat posted) so it fights; otherwise it follows
    /// the owner (Follow posted only when it was not already following them). Walk speed is kept equal
    /// to run speed so following uses the unit's full speed. Returns "combat", "follow" or "gone".
    /// </summary>
    public string PetTick(UnitId petId, UnitId ownerId)
    {
        if (!TryWorld(out var em) || !TryAlive(em, petId, out var pet)) return "gone";
        var owner = ownerId.ToEntity();
        try
        {
            if (pet.Has<AiMoveSpeeds>(em))
            {
                float* speeds = (float*)em.GetComponentDataRawRW(pet, ComponentIndex.IndexOf<AiMoveSpeeds>());
                if (speeds != null && speeds[0] != speeds[1]) speeds[0] = speeds[1];
            }

            var target = ReadAggroTarget(em, pet);
            bool fight = target != Entity.Null && target != owner && IsLivingUnit(em, target);
            int state = ReadBtState(em, pet);
            if (fight)
            {
                if (TryReadFollowerFollowed(em, pet, out var followed) && followed != Entity.Null)
                    TryWriteFollowerFollowed(em, pet, Entity.Null, logMissing: false);
                if ((state & (int)GenericEnemyState.Combat) == 0)
                    TryPostBtRequest(em, pet, GenericEnemyState.Combat);
                return "combat";
            }

            if (owner == Entity.Null || !em.Exists(owner)) return "follow";
            bool following = TryReadFollowerFollowed(em, pet, out var current) && current == owner;
            if (!following || (state & (int)GenericEnemyState.Follow) == 0)
            {
                if (following) TryWriteFollowerFollowed(em, pet, Entity.Null, logMissing: false);
                Follow(petId, ownerId);
            }
            return "follow";
        }
        catch (Exception e)
        {
            LogRefuseOnce("PetTick", "Units: pet step threw on " + Fmt(pet) + ": " + e.Message);
            return "follow";
        }
    }

    /// <summary><c>AggroConsumer.AggroTarget</c> (NetworkedEntity @0x30: index, version).</summary>
    private static Entity ReadAggroTarget(EntityManager em, Entity unit)
    {
        if (!unit.Has<AggroConsumer>(em)) return Entity.Null;
        byte* raw = (byte*)em.GetComponentDataRawRO(unit, ComponentIndex.IndexOf<AggroConsumer>());
        if (raw == null) return Entity.Null;
        var e = new Entity { Index = *(int*)(raw + Layout.AggroConsumerAggroTargetOffset), Version = *(int*)(raw + Layout.AggroConsumerAggroTargetOffset + 4) };
        return e.Index > 0 ? e : Entity.Null;
    }

    /// <summary>Exists, has no <c>Dead</c>, and <c>Health</c> (Value @0x10, IsDead @0x18) is above 0 and not dead.</summary>
    private static bool IsLivingUnit(EntityManager em, Entity e)
    {
        try
        {
            if (!em.Exists(e) || e.Has<Dead>(em)) return false;
            if (!e.Has<Health>(em)) return true;
            byte* h = (byte*)em.GetComponentDataRawRO(e, ComponentIndex.IndexOf<Health>());
            return h != null && h[0x18] == 0 && *(float*)(h + 0x10) > 0f;
        }
        catch { return false; }
    }

    /// <summary><c>BehaviourTreeState.Value</c> (int @0); 0 when absent.</summary>
    private static int ReadBtState(EntityManager em, Entity unit)
    {
        if (!unit.Has<ProjectM.Behaviours.BehaviourTreeState>(em)) return 0;
        int* raw = (int*)em.GetComponentDataRawRO(unit, ComponentIndex.IndexOf<ProjectM.Behaviours.BehaviourTreeState>());
        return raw == null ? 0 : *raw;
    }

    public bool AddedFollower(UnitId unit) => _addedFollower.Contains(unit);

    public bool GoTo(UnitId unitId, UnitVec3 position)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit))
            return false;
        if (!UnitSpawnPlacement.TryReadTranslation(em, unit, out var start))
            return false;

        UnitSpawnPlacement.TryWriteSpawnTransform(em, unit, start);
        UnitSpawnPlacement.TryWriteLastTranslation(em, unit, start);
        // GoTo after Follow: FollowerSystem / ApproachEntity keep rewriting AiMove until Followed is Null
        // and the tree is Idle (Follower Followed / ModeModifiable / Stationary, AiMove TargettingMode).
        // A Follower we added (prefab had none) is removed so FollowerSystem stops iterating the unit.
        TryClearFollowState(em, unit);
        if (_addedFollower.Remove(unitId))
            TryRemoveFollower(em, unit);
        bool ok = TryWriteAiMoveApproachPosition(em, unit, new float3(position.X, position.Y, position.Z));
        if (!ok)
            LogRefuseOnce("GoTo.overlayRefused", "Units: AiMove_Server overlay refused on " + unitId + " (logged once).");
        return ok;
    }

    public bool Despawn(UnitId unitId)
    {
        if (!TryWorld(out var em)) return false;
        var unit = unitId.ToEntity();
        if (unit == Entity.Null || !em.Exists(unit)) return false;
        if (_addedFollower.Remove(unitId))
            TryRemoveFollower(em, unit);
        bool ok = TryDespawnEntity(em, unit);
        // Live summons of this unit (EntityOwner = the unit, Minion / IsMinion) go the same way,
        // over a few passes because a boss keeps summoning while it is being removed.
        if (ok) _summons.Retire(unit);
        return ok;
    }

    /// <summary>
    /// Despawn an entity that has no handle (a summon, a drop, another NPC) on the same path as
    /// <see cref="Despawn"/>: player / user refusal, DIAG5 buff sweep, then
    /// <see cref="BuffSweep.DestroyUnitReplicated"/>. True when the removal was started now or is
    /// already in flight; false when the entity is gone, already tagged, or refused.
    /// </summary>
    internal bool DespawnEntity(EntityManager em, Entity entity)
    {
        try
        {
            if (entity == Entity.Null || !em.Exists(entity)) return false;
            if (entity.Has<DestroyTag>(em)) return false;
        }
        catch { return false; }
        return TryDespawnEntity(em, entity);
    }

    public bool Exists(UnitId unitId)
    {
        if (unitId.IsNone || !TryWorld(out var em)) return false;
        try { return em.Exists(unitId.ToEntity()); }
        catch { return false; }
    }

    public bool TryGetPosition(UnitId unitId, out UnitVec3 position)
    {
        position = default;
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit))
            return false;
        if (!UnitSpawnPlacement.TryReadTranslation(em, unit, out var pos))
            return false;
        position = new UnitVec3(pos.x, pos.y, pos.z);
        return true;
    }

    private static bool TryWorld(out EntityManager em)
    {
        em = default;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return false;
        em = world.EntityManager;
        return true;
    }

    private static bool TryAlive(EntityManager em, UnitId id, out Entity entity)
    {
        entity = id.ToEntity();
        try { return entity != Entity.Null && em.Exists(entity); }
        catch { return false; }
    }

    private bool TryResolvePrefab(World world, EntityManager em, string prefabName, int prefabGuid, out Entity prefab, out string resolvedName)
    {
        prefab = Entity.Null;
        resolvedName = prefabName ?? "";
        PrefabGUID guid = default;
        bool haveGuid = false;

        if (!string.IsNullOrEmpty(prefabName)
            && !int.TryParse(prefabName, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            try
            {
                if (PrefabLookup.TryGetGuidByName(world, prefabName, out guid))
                    haveGuid = true;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Units: TryGetGuidByName '" + prefabName + "' threw: " + e.Message);
            }
        }

        if (!haveGuid && prefabGuid != 0)
        {
            guid = new PrefabGUID(prefabGuid);
            haveGuid = true;
        }

        if (!haveGuid)
        {
            LastMissReason = "Units: no prefab named '" + prefabName + "' on this server; returning a disabled handle.";
            return false;
        }

        try
        {
            if (!PrefabLookup.TryGetEntity(world, guid, out prefab) || prefab == Entity.Null || !em.Exists(prefab))
            {
                LastMissReason = "Units: prefab guid=" + guid._Value + " is not in _PrefabGuidToEntityMap; returning a disabled handle.";
                prefab = Entity.Null;
                return false;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Units: TryGetEntity guid=" + guid._Value + " threw: " + e);
            LastMissReason = "Units: prefab lookup threw; returning a disabled handle.";
            return false;
        }

        if (PrefabLookup.TryGetName(world, guid, out var name) && !string.IsNullOrEmpty(name))
            resolvedName = name;
        return true;
    }

    private static bool TryPostBtRequest(EntityManager em, Entity unit, GenericEnemyState want)
    {
        Entity req = Entity.Null;
        try { req = em.CreateEntity(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: CreateEntity for BT request failed: " + e.Message);
            return false;
        }

        var payload = new ExternalBehaviourTreeTransitionRequest { State = want, Entity = unit };
        try
        {
            req.Add<ExternalBehaviourTreeTransitionRequest>(em);
            req.Write(em, in payload);
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("ExternalBehaviourTreeTransitionRequest", "Units: BT request write refused: " + e.Message);
            try { if (em.Exists(req)) em.DestroyEntity(req); } catch { /* */ }
            return false;
        }
    }

    /// <summary>
    /// Drop Follow so ApproachPosition can own AiMove: Followed Null (@0x0),
    /// Mode Unit (@0x18), Stationary 0 (@0x1C, spike walk value), Idle BT,
    /// TargettingMode None + TargetEntity_Movement Null. Best-effort.
    /// </summary>
    private static void TryClearFollowState(EntityManager em, Entity unit)
    {
        TryWriteFollowerFollowed(em, unit, Entity.Null, logMissing: false);
        TryPostBtRequest(em, unit, GenericEnemyState.Idle);
        TryClearAiMoveFollowTarget(em, unit);
    }

    /// <summary>
    /// Drop a live GoTo so Follow's ApproachEntity can own AiMove: TargetPosition xz 0,
    /// TargetEntity_Movement Null (@0x18), TargettingMode None
    /// (@0x48). Best-effort.
    /// </summary>
    private static void TryClearGoToGoal(EntityManager em, Entity unit)
    {
        TryClearAiMoveFollowTarget(em, unit);
        TryZeroAiMoveTargetPosition(em, unit);
    }

    /// <summary>
    /// Non-generic <c>AddComponent</c> of Follower (Followed @0 / Mode @0x18 /
    /// Stationary @0x1C). Not <c>AddComponentData&lt;Follower&gt;</c>: the struct carries bools
    /// so it is not C# unmanaged, and a generic add over a game value type can hard-crash
    /// (trap 1). Size gate matches the overlay (FollowerMinSlot). FollowerSystem's IFE_0
    /// query is only Follower on this entity; FollowerBuffer is on the
    /// followed target and FollowerSystem adds it via ECB if missing.
    /// </summary>
    private static bool TryAddFollower(EntityManager em, Entity unit)
    {
        try
        {
            var typeIndex = ComponentIndex.IndexOf<Follower>();
            int typeSize = TypeSizeOf(typeIndex);
            int sizeInChunk = SizeInChunkOf(typeIndex);
            if (sizeInChunk > 0 && sizeInChunk < FollowerMinSlot)
            {
                LogRefuseOnce(
                    "Follower.add.size",
                    "Units: Follower add refused: SizeInChunk=" + sizeInChunk + " < " + FollowerMinSlot + ".");
                return false;
            }

            if (typeSize > 0 && typeSize < FollowerMinSlot)
            {
                LogRefuseOnce(
                    "Follower.add.size",
                    "Units: Follower add refused: TypeSize=" + typeSize + " < " + FollowerMinSlot + ".");
                return false;
            }

            unit.Add<Follower>(em);
            if (!unit.Has<Follower>(em))
            {
                LogRefuseOnce("Follower.add.missing", "Units: Follower add on " + Fmt(unit) + " did not stick.");
                return false;
            }

            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("Follower.add.throw", "Units: Follower add refused on " + Fmt(unit) + ": " + e.Message);
            return false;
        }
    }

    private static void TryRemoveFollower(EntityManager em, Entity unit)
    {
        try
        {
            if (unit.Has<Follower>(em))
                unit.Remove<Follower>(em);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: Follower remove on " + Fmt(unit) + " threw: " + e.Message);
        }
    }

    /// <summary>
    /// Evidence 33 / step 35: byte* stores at Followed @0 / ModeModifiable @24 / Stationary @28.
    /// Never SetComponentDataRaw of 30 or 32 (evidence §32).
    /// Logs once if refused. <paramref name="followed"/> Null is the GoTo-after-Follow clear.
    /// </summary>
    private static bool TryWriteFollowerFollowed(EntityManager em, Entity unit, Entity followed, bool logMissing = true)
    {
        try
        {
            if (!unit.Has<Follower>(em))
            {
                if (logMissing)
                    LogRefuseOnce("Follower.missing", "Units: no Follower on " + Fmt(unit) + "; Follow refused.");
                return false;
            }

            var typeIndex = ComponentIndex.IndexOf<Follower>();
            int typeSize = TypeSizeOf(typeIndex);
            int sizeInChunk = SizeInChunkOf(typeIndex);
            if (sizeInChunk > 0 && sizeInChunk < FollowerMinSlot)
            {
                LogRefuseOnce(
                    "Follower.size",
                    "Units: Follower overlay refused: SizeInChunk=" + sizeInChunk + " < " + FollowerMinSlot + ".");
                return false;
            }

            if (typeSize > 0 && typeSize < FollowerMinSlot)
            {
                LogRefuseOnce(
                    "Follower.size",
                    "Units: Follower overlay refused: TypeSize=" + typeSize + " < " + FollowerMinSlot + ".");
                return false;
            }

            void* raw = em.GetComponentDataRawRW(unit, typeIndex);
            if (raw == null)
            {
                LogRefuseOnce("Follower.null", "Units: Follower GetComponentDataRawRW returned null.");
                return false;
            }

            var p = (byte*)raw;
            *(Entity*)(p + UnitSteering.FollowerFollowedOffset) = followed;
            *(int*)(p + UnitSteering.FollowerModeOffset) = UnitSteering.FollowModeUnit;
            p[UnitSteering.FollowerStationaryOffset] = 0;
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("Follower.throw", "Units: Follower overlay refused: " + e.Message);
            return false;
        }
    }

    private static bool TryReadFollowerFollowed(EntityManager em, Entity unit, out Entity followed)
    {
        followed = Entity.Null;
        try
        {
            if (!unit.Has<Follower>(em)) return false;
            var typeIndex = ComponentIndex.IndexOf<Follower>();
            void* raw = em.GetComponentDataRawRO(unit, typeIndex);
            if (raw == null) return false;
            followed = *(Entity*)((byte*)raw + UnitSteering.FollowerFollowedOffset);
            return followed != Entity.Null;
        }
        catch
        {
            followed = Entity.Null;
            return false;
        }
    }

    private static bool TryClearAiMoveFollowTarget(EntityManager em, Entity unit)
    {
        try
        {
            if (!unit.Has<AiMove_Server>(em)) return false;
            var typeIndex = ComponentIndex.IndexOf<AiMove_Server>();
            void* raw = em.GetComponentDataRawRW(unit, typeIndex);
            if (raw == null) return false;
            var p = (byte*)raw;
            *(Entity*)(p + UnitSteering.AiMoveTargetEntityMovementOffset) = Entity.Null;
            p[UnitSteering.AiMoveTargettingModeOffset] = UnitSteering.TargettingNone;
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("AiMove.clearFollow", "Units: AiMove_Server follow-target clear refused: " + e.Message);
            return false;
        }
    }

    private static bool TryZeroAiMoveTargetPosition(EntityManager em, Entity unit)
    {
        try
        {
            if (!unit.Has<AiMove_Server>(em)) return false;
            var typeIndex = ComponentIndex.IndexOf<AiMove_Server>();
            void* raw = em.GetComponentDataRawRW(unit, typeIndex);
            if (raw == null) return false;
            var p = (byte*)raw;
            *(float*)(p + UnitSteering.AiMoveTargetPositionOffset) = 0f;
            *(float*)(p + UnitSteering.AiMoveTargetPositionOffset + 4) = 0f;
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("AiMove.clearGoal", "Units: AiMove_Server goal clear refused: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Evidence 46 / ApproachPosition.Tick: poke TargetPosition.xz, distances, TargettingMode=Position.
    /// Bool-carrying struct is not toolkit Write&lt;T&gt;; overlay, never a guessed blob.
    /// </summary>
    private static bool TryWriteAiMoveApproachPosition(EntityManager em, Entity unit, float3 goal)
    {
        try
        {
            if (!unit.Has<AiMove_Server>(em))
            {
                LogRefuseOnce("AiMove.missing", "Units: no AiMove_Server on " + Fmt(unit) + "; GoTo refused.");
                return false;
            }

            var typeIndex = ComponentIndex.IndexOf<AiMove_Server>();
            int typeSize = TypeSizeOf(typeIndex);
            int sizeInChunk = SizeInChunkOf(typeIndex);
            if (sizeInChunk > 0 && sizeInChunk < AiMoveMinSlot)
            {
                LogRefuseOnce(
                    "AiMove.size",
                    "Units: AiMove_Server overlay refused: SizeInChunk=" + sizeInChunk + " < " + AiMoveMinSlot + ".");
                return false;
            }

            if (typeSize > 0 && typeSize < AiMoveMinSlot)
            {
                LogRefuseOnce(
                    "AiMove.size",
                    "Units: AiMove_Server overlay refused: TypeSize=" + typeSize + " < " + AiMoveMinSlot + ".");
                return false;
            }

            try
            {
                RawWriteGuard.EnsureTypedWrite(
                    typeSize,
                    Marshal.SizeOf<AiMove_Server>(),
                    sizeInChunk,
                    typeof(AiMove_Server).FullName ?? nameof(AiMove_Server));
            }
            catch (Exception e)
            {
                LogRefuseOnce("AiMove.guard", "Units: AiMove_Server write refused: " + e.Message);
                return false;
            }

            void* raw = em.GetComponentDataRawRW(unit, typeIndex);
            if (raw == null)
            {
                LogRefuseOnce("AiMove.null", "Units: AiMove_Server GetComponentDataRawRW returned null.");
                return false;
            }

            var p = (byte*)raw;
            *(float*)p = goal.x;
            *(float*)(p + 4) = goal.z;
            float inner = ApproachGoal < 0f ? (ApproachMin + ApproachMax) * 0.5f : ApproachGoal;
            *(float*)(p + 48) = ApproachMin;
            *(float*)(p + 52) = inner;
            *(float*)(p + 56) = -1f;
            *(float*)(p + 60) = ApproachMax;
            *(Entity*)(p + UnitSteering.AiMoveTargetEntityMovementOffset) = Entity.Null;
            p[UnitSteering.AiMoveTargettingModeOffset] = UnitSteering.TargettingPosition;
            p[77] = 0;
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("AiMove.throw", "Units: AiMove_Server overlay refused: " + e.Message);
            return false;
        }
    }

    private bool TryDespawnEntity(EntityManager em, Entity entity)
    {
        var playerType = ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly);
        var userType = ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly);
        if (BuffSweep.RefuseAsPlayerOrUser(
                em.HasComponent(entity, playerType),
                em.HasComponent(entity, userType)))
        {
            Plugin.Logger?.LogWarning(BuffSweep.FormatPlayerRefuseLog(entity.Index, entity.Version));
            return false;
        }

        // One removal per entity: a clear, the summon sweep and a handle can all reach the same one.
        if (!MarkDespawnPending(em, entity)) return true;

        // DIAG5 phase buffs: DestroyUtility.Destroy(TryRemoveBuff) alone on each still-existing
        // BuffBuffer row, so the game removes the row too. Do not strip Attach. Do not destroy or tag the
        // unit. Later FrameTicker ticks wait until every listed id Exists=false, then
        // replicated DestroyUtility.Destroy of the unit (no immediate DestroyTag). DIAG5: "destroyed without being
        // detached" is DetachSystem logging a live detach; it is not a failure.
        int prefab = ReadPrefabHash(em, entity);
        var sweep = BuffSweep.Sweep(em, entity);
        if (!sweep.Complete)
            Plugin.Logger?.LogWarning(BuffSweep.FormatIncompleteUnitWarning(entity.Index, entity.Version, sweep.Reason));

        Plugin.Logger?.LogInfo(
            "Units: despawn " + entity.Index + ":" + entity.Version
            + " phase=buffs buffsFound=" + sweep.Found + " buffsDestroyed=" + sweep.Destroyed
            + " stale=" + sweep.Stale
            + " buffSweepComplete=" + sweep.Complete);

        _inFlight[entity] = prefab;
        DeferDespawnPhase(
            em,
            entity,
            sweep.BuffIds ?? Array.Empty<BuffSweep.EntityId>(),
            sweep.Complete,
            BuffSweep.DestroyTagDeferFrames,
            cycle: 1);
        return true;
    }

    private void DeferDespawnPhase(
        EntityManager em,
        Entity entity,
        BuffSweep.EntityId[] buffs,
        bool sweepComplete,
        int framesLeft,
        int cycle)
    {
        try
        {
            _awaitingPoll.Add(entity);
            Dev.MainThread.PostDelayed(
                0,
                () => PollDespawnPhase(em, entity, buffs, sweepComplete, framesLeft, cycle),
                this);
        }
        catch (Exception e)
        {
            _awaitingPoll.Remove(entity);
            Plugin.Logger?.LogWarning("Units: DIAG5 despawn defer threw: " + e.Message);
        }
    }

    private void PollDespawnPhase(
        EntityManager em,
        Entity entity,
        BuffSweep.EntityId[] buffs,
        bool sweepComplete,
        int framesLeft,
        int cycle)
    {
        _awaitingPoll.Remove(entity);
        try
        {
            if (!em.Exists(entity)) return;
            if (entity.Has<DestroyTag>(em)) return;

            if (buffs == null)
                buffs = Array.Empty<BuffSweep.EntityId>();
            if (cycle < 1) cycle = 1;
            bool absent = BuffSweep.AllAbsent(em, buffs);
            int pending = CountPending(em, buffs);
            // A row whose buff is gone must not outlive this despawn: if the unit is left behind
            // (stuck, or the unit destroy never lands), the autosave keeps a row naming no entity.
            if (absent) BuffSweep.PruneDanglingRows(em, entity, "Units: despawn");

            var staleBuf = new BuffSweep.EntityId[BuffSweep.MaxElements];
            var newLiveBuf = new BuffSweep.EntityId[BuffSweep.MaxElements];
            var scan = BuffSweep.ScanBuffer(
                em,
                entity,
                buffs,
                staleBuf,
                newLiveBuf,
                out _,
                out int newLiveCount);

            // Listed ids are gone. A live row not in that set is a new buff: another
            // destroy cycle, or stuck once MaxCycles is reached. Stale rows do not
            // block tagging.
            if (absent && scan.Complete && newLiveCount > 0)
            {
                if (cycle >= BuffSweep.MaxCycles)
                {
                    Plugin.Logger?.LogWarning(
                        "Units: despawn " + entity.Index + ":" + entity.Version
                        + " phase=stuck reason=" + BuffSweep.FormatStuckReason(cycle));
                    return;
                }

                var nextListed = CopyNewLive(newLiveBuf, newLiveCount);
                BuffSweep.DestroyBuffs(em, nextListed);
                Plugin.Logger?.LogInfo(
                    "Units: despawn " + entity.Index + ":" + entity.Version
                    + " phase=buffs cycle=" + (cycle + 1)
                    + " newBuffs=" + newLiveCount);
                DeferDespawnPhase(
                    em,
                    entity,
                    nextListed,
                    sweepComplete,
                    BuffSweep.DestroyTagDeferFrames,
                    cycle + 1);
                return;
            }

            bool ready = sweepComplete && absent && scan.Complete && newLiveCount == 0;
            if (!ready && framesLeft > 1)
            {
                DeferDespawnPhase(em, entity, buffs, sweepComplete, framesLeft - 1, cycle);
                return;
            }

            if (!ready)
            {
                Plugin.Logger?.LogWarning(
                    BuffSweep.FormatDeferTimeoutWarning(
                        entity.Index,
                        entity.Version,
                        BuffSweep.DestroyTagDeferFrames,
                        pending,
                        "buffs"));
                // DIAG5: never DestroyUtility.Destroy the unit while a copied buff Exists.
                // An unread buffer is the same refuse: do not tag on a failed scan.
                if (!absent || !scan.Complete)
                {
                    DeferDespawnPhase(
                        em,
                        entity,
                        buffs,
                        sweepComplete,
                        BuffSweep.DestroyTagDeferFrames,
                        cycle);
                    return;
                }
            }

            if (!sweepComplete)
            {
                Plugin.Logger?.LogWarning(
                    BuffSweep.FormatIncompleteUnitWarning(entity.Index, entity.Version, "incomplete sweep; unit not destroyed"));
                return;
            }

            // Replicated removal: DestroyUtility.Destroy alone lets the game's destroy flow finish
            // the unit and tell clients. An immediate DestroyTag removed it on the server only
            // (live 2026-09-24: 397 of 400 Frostmaws stayed on the client). DestroyTag is a
            // logged fallback after BuffSweep.UnitDestroyFallbackFrames.
            try
            {
                BuffSweep.DestroyUnitReplicated(em, entity, DestroyDebugReason.None, "Units: despawn");
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Units: DestroyUtility.Destroy threw: " + e.Message);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: DIAG5 despawn poll threw: " + e.Message);
        }
        finally
        {
            // Finished, gave up, or issued the unit destroy: nothing left to hand over.
            if (!_awaitingPoll.Contains(entity)) _inFlight.Remove(entity);
        }
    }

    /// <summary>
    /// Despawns whose next step is a queued poll, as (index, version, PrefabGUID hash). A unit
    /// whose destroy was already issued is not listed: the game's destroy flow finishes it.
    /// </summary>
    internal List<(int Index, int Version, int Prefab)> InFlightDespawns()
    {
        var rows = new List<(int Index, int Version, int Prefab)>(_awaitingPoll.Count);
        foreach (var entity in _awaitingPoll)
            rows.Add((entity.Index, entity.Version, _inFlight.TryGetValue(entity, out int prefab) ? prefab : 0));
        return rows;
    }

    /// <summary>The entity's PrefabGUID hash, or 0 when it has none or the read fails.</summary>
    internal static int ReadPrefabHash(EntityManager em, Entity entity)
    {
        try
        {
            return entity.Has<PrefabGUID>(em) ? em.GetComponentData<PrefabGUID>(entity).GuidHash : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static BuffSweep.EntityId[] CopyNewLive(BuffSweep.EntityId[] buf, int count)
    {
        if (buf == null || count <= 0) return Array.Empty<BuffSweep.EntityId>();
        if (count > buf.Length) count = buf.Length;
        var copy = new BuffSweep.EntityId[count];
        Array.Copy(buf, copy, count);
        return copy;
    }

    private static int CountPending(EntityManager em, ReadOnlySpan<BuffSweep.EntityId> buffs)
    {
        int pending = 0;
        for (int i = 0; i < buffs.Length; i++)
        {
            if (buffs[i].IsNull) continue;
            var buff = new Entity { Index = buffs[i].Index, Version = buffs[i].Version };
            try
            {
                if (!em.Exists(buff)) continue;
                pending++;
            }
            catch { pending++; }
        }

        return pending;
    }

    private static int TypeSizeOf(TypeIndex typeIndex)
    {
        try { return TypeManager.GetTypeInfo(typeIndex).TypeSize; }
        catch { return 0; }
    }

    private static int SizeInChunkOf(TypeIndex typeIndex)
    {
        try { return TypeManager.GetTypeInfo(typeIndex).SizeInChunk; }
        catch { return 0; }
    }

    private static readonly object RefuseGate = new();
    private static readonly System.Collections.Generic.HashSet<string> LoggedRefusals = new(StringComparer.Ordinal);

    private static void LogRefuseOnce(string key, string message)
    {
        lock (RefuseGate)
        {
            if (!LoggedRefusals.Add(key)) return;
        }

        Plugin.Logger?.LogWarning(message);
    }

    private static string Fmt(Entity e) => e.Index + ":" + e.Version;
}
#endif
