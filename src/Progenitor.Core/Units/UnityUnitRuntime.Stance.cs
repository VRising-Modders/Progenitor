#if !PROGENITOR_TESTS
using System;
using ProjectM;
using ProjectM.Behaviours;
using ProjectM.Shared;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Units;

/// <summary>
/// Pet stances, hold and the spawn-tick extras (<see cref="UnitSpawnOptions.Permanent"/>,
/// <see cref="UnitSpawnOptions.DropLoot"/>). Every field write is a raw overlay at a
/// <c>layout.txt</c> offset through <see cref="ComponentIndex"/>, behind a size gate; structural
/// removals are the non-generic <c>RemoveComponent</c>.
/// </summary>
internal sealed unsafe partial class UnityUnitRuntime
{
    /// <summary>Widest AggroConsumer byte these overlays touch: <c>Active._Value</c>.</summary>
    internal const int AggroConsumerMinBytes = Layout.AggroConsumerActiveOffset + Layout.ModifiableBoolValueOffset + 1;

    /// <summary>LifeTime is a float and a 4-byte enum.</summary>
    internal const int LifeTimeMinBytes = Layout.LifeTimeEndActionOffset + 4;

    /// <summary>Tree states Hold leaves by posting Idle: Alert, Combat, Return, Follow, combat relocation.</summary>
    internal const int HoldLeavesStates =
        Layout.GenericEnemyStateAlert | Layout.GenericEnemyStateCombat | Layout.GenericEnemyStateReturn
        | Layout.GenericEnemyStateFollow | Layout.GenericEnemyStateRelocateCombatArea;

    public bool? ReadAggroActive(UnitId unitId)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return null;
        try
        {
            byte* raw = AggroRaw(em, unit, write: false);
            if (raw == null) return null;
            return raw[Layout.AggroConsumerActiveOffset + Layout.ModifiableBoolValueOffset] != 0;
        }
        catch (Exception e)
        {
            LogRefuseOnce("Hold.readActive", "Units: AggroConsumer.Active read threw: " + e.Message);
            return null;
        }
    }

    public bool HasAggroBlockingBuff(UnitId unitId)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return false;
        try
        {
            if (!unit.Has<BuffBuffer>(em)) return false;
            // Raw BuffBuffer walk, the BuffSweep path: GetBufferLength / GetBufferRawRO, never GetBuffer<T>.
            var bufferType = ComponentIndex.IndexOf<BuffBuffer>();
            int reported = em.GetBufferLength(unit, bufferType);
            int walk = BuffSweep.BoundLength(reported);
            if (walk <= 0) return false;
            void* raw = em.GetBufferRawRO(unit, bufferType);
            if (raw == null) return false;
            var ids = new BuffSweep.EntityId[walk];
            int n = BuffSweep.CopyEntities(new ReadOnlySpan<byte>(raw, walk * BuffSweep.ElementStride), reported, ids);
            var modeType = ComponentIndex.IndexOf<DisableAggroBuff>();
            int modeSize = TypeSizeOf(modeType);
            if (modeSize > 0 && modeSize < Layout.DisableAggroBuffModeOffset + 4)
            {
                LogRefuseOnce("DisableAggroBuff.size", "Units: DisableAggroBuff read refused: TypeSize=" + modeSize + ".");
                return false;
            }
            for (int i = 0; i < n; i++)
            {
                var buff = new Entity { Index = ids[i].Index, Version = ids[i].Version };
                if (buff == Entity.Null || !em.Exists(buff) || !buff.Has<DisableAggroBuff>(em)) continue;
                byte* modeRaw = (byte*)em.GetComponentDataRawRO(buff, modeType);
                if (modeRaw != null && (*(int*)(modeRaw + Layout.DisableAggroBuffModeOffset) & Layout.DisableAggroBuffModeTargetDontAttackOthers) != 0)
                    return true;
            }
            return false;
        }
        catch (Exception e)
        {
            LogRefuseOnce("Hold.blockingBuff", "Units: DisableAggroBuff scan threw: " + e.Message);
            return false;
        }
    }

    public bool WalkTo(UnitId unitId, UnitVec3 position) => GoTo(unitId, position);

    /// <summary>
    /// Dead for a hold or attack step: the <c>Dead</c> component or <c>Health.IsDead</c> (@0x18). Health 0
    /// alone is not death: a unit instantiated this frame reads <c>Health.Value</c> 0 (the prefab carries
    /// 0) until the game's spawn systems fill it, and <c>SetPet</c>/<c>HoldAt</c> run their first step
    /// right after <c>Spawn</c>. Live, 2026-09-25: every unit held at spawn was dropped by the health-0 test.
    /// </summary>
    private static bool IsDeadUnit(EntityManager em, Entity e)
    {
        try
        {
            if (e.Has<Dead>(em)) return true;
            if (!e.Has<Health>(em)) return false;
            byte* h = (byte*)em.GetComponentDataRawRO(e, ComponentIndex.IndexOf<Health>());
            return h != null && h[0x18] != 0;
        }
        catch { return false; }
    }

    public string HoldTick(UnitId unitId)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return "gone";
        // A dead unit (Dead or Health.IsDead) is gone for the hold: nothing to hold or restore.
        if (IsDeadUnit(em, unit)) return "gone";
        try
        {
            // Read first so an already quiet unit is not written every step.
            byte* ro = AggroRaw(em, unit, write: false);
            if (ro != null && AggroNeedsQuiet(ro))
            {
                byte* raw = AggroRaw(em, unit, write: true);
                if (raw != null)
                {
                    raw[Layout.AggroConsumerActiveOffset + Layout.ModifiableBoolValueOffset] = 0;
                    ClearNetworkedEntity(raw + Layout.AggroConsumerAggroTargetOffset);
                    ClearNetworkedEntity(raw + Layout.AggroConsumerAlertTargetOffset);
                }
            }
            if (TryReadFollowerFollowed(em, unit, out var followed) && followed != Entity.Null)
                TryWriteFollowerFollowed(em, unit, Entity.Null, logMissing: false);
            if ((ReadBtState(em, unit) & HoldLeavesStates) != 0)
                TryPostBtRequest(em, unit, GenericEnemyState.Idle);
            return "hold";
        }
        catch (Exception e)
        {
            LogRefuseOnce("HoldTick", "Units: hold step threw on " + Fmt(unit) + ": " + e.Message);
            return "hold";
        }
    }

    public void ReleaseHold(UnitId unitId, bool? active)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return;
        try
        {
            if (active is bool value)
            {
                byte* raw = AggroRaw(em, unit, write: true);
                if (raw != null) raw[Layout.AggroConsumerActiveOffset + Layout.ModifiableBoolValueOffset] = value ? (byte)1 : (byte)0;
            }
            // The walk-back goal would keep pulling the unit to the hold spot.
            TryClearGoToGoal(em, unit);
        }
        catch (Exception e)
        {
            LogRefuseOnce("ReleaseHold", "Units: hold release threw on " + Fmt(unit) + ": " + e.Message);
        }
    }

    public string AttackTick(UnitId unitId, UnitId targetId)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return "gone";
        if (IsDeadUnit(em, unit)) return "gone";
        var target = targetId.ToEntity();
        try
        {
            if (target == Entity.Null || !IsLivingUnit(em, target)) return "done";
            if (TryReadFollowerFollowed(em, unit, out var followed) && followed != Entity.Null)
                TryWriteFollowerFollowed(em, unit, Entity.Null, logMissing: false);
            byte* ro = AggroRaw(em, unit, write: false);
            bool aggroOk = ro != null
                && ReadAggroTarget(em, unit) == target
                && ro[Layout.AggroConsumerActiveOffset + Layout.ModifiableBoolValueOffset] != 0;
            if (!aggroOk) TryWriteAggroTarget(em, unit, target);
            if ((ReadBtState(em, unit) & Layout.GenericEnemyStateCombat) == 0)
                TryPostBtRequest(em, unit, GenericEnemyState.Combat);
            return "combat";
        }
        catch (Exception e)
        {
            LogRefuseOnce("AttackTick", "Units: attack step threw on " + Fmt(unit) + ": " + e.Message);
            return "combat";
        }
    }

    /// <summary>Active on, or either target set.</summary>
    private static bool AggroNeedsQuiet(byte* raw) =>
        raw[Layout.AggroConsumerActiveOffset + Layout.ModifiableBoolValueOffset] != 0
        || *(int*)(raw + Layout.AggroConsumerAggroTargetOffset) != 0
        || *(int*)(raw + Layout.AggroConsumerAlertTargetOffset) != 0;

    /// <summary>NetworkedEntity: Entity (index, version) @0 and _WaitingForSync @8.</summary>
    private static void ClearNetworkedEntity(byte* p)
    {
        *(int*)p = 0;
        *(int*)(p + 4) = 0;
        p[Layout.NetworkedEntityWaitingForSyncOffset] = 0;
    }

    /// <summary>The unit's AggroConsumer bytes behind the size gate, or null when it has none.</summary>
    private static byte* AggroRaw(EntityManager em, Entity unit, bool write)
    {
        if (!unit.Has<AggroConsumer>(em)) return null;
        var typeIndex = ComponentIndex.IndexOf<AggroConsumer>();
        int typeSize = TypeSizeOf(typeIndex);
        int extent = typeSize > 0 ? typeSize : SizeInChunkOf(typeIndex);
        if (extent > 0 && extent < AggroConsumerMinBytes)
        {
            LogRefuseOnce("AggroConsumer.size", "Units: AggroConsumer overlay refused: extent=" + extent + " < " + AggroConsumerMinBytes + ".");
            return null;
        }
        return write
            ? (byte*)em.GetComponentDataRawRW(unit, typeIndex)
            : (byte*)em.GetComponentDataRawRO(unit, typeIndex);
    }

    /// <summary>
    /// <see cref="UnitSpawnOptions.Permanent"/>: a unit with <c>LifeTime</c> gets
    /// <c>{Duration -1, EndAction None}</c>. True when written or when the unit has no LifeTime.
    /// </summary>
    internal static bool TryMakePermanent(UnitId unitId, out bool hadLifeTime)
    {
        hadLifeTime = false;
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return false;
        try
        {
            if (!unit.Has<LifeTime>(em)) return true;
            hadLifeTime = true;
            var typeIndex = ComponentIndex.IndexOf<LifeTime>();
            int typeSize = TypeSizeOf(typeIndex);
            int extent = typeSize > 0 ? typeSize : SizeInChunkOf(typeIndex);
            if (extent > 0 && extent < LifeTimeMinBytes)
            {
                LogRefuseOnce("LifeTime.size", "Units: LifeTime overlay refused: extent=" + extent + " < " + LifeTimeMinBytes + ".");
                return false;
            }
            byte* raw = (byte*)em.GetComponentDataRawRW(unit, typeIndex);
            if (raw == null) return false;
            *(float*)(raw + Layout.LifeTimeDurationOffset) = -1f;
            *(int*)(raw + Layout.LifeTimeEndActionOffset) = Layout.LifeTimeEndActionNone;
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("LifeTime.throw", "Units: LifeTime write threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// <see cref="UnitSpawnOptions.DropLoot"/> false: remove <c>DropTableBuffer</c> (every drop table,
    /// whatever its trigger), the <c>DropTableOnDeath</c> / <c>DropTableOnDestroy</c> triggers and the
    /// <c>DropTable</c> tag, so no drop system matches the unit. All four are server-only, not networked.
    /// Returns what was removed.
    /// </summary>
    internal static bool TryDisableLoot(UnitId unitId, out string removed)
    {
        removed = "";
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return false;
        try
        {
            var list = new System.Collections.Generic.List<string>();
            if (unit.Has<DropTableBuffer>(em)) { unit.Remove<DropTableBuffer>(em); list.Add(nameof(DropTableBuffer)); }
            if (unit.Has<DropTableOnDeath>(em)) { unit.Remove<DropTableOnDeath>(em); list.Add(nameof(DropTableOnDeath)); }
            if (unit.Has<DropTableOnDestroy>(em)) { unit.Remove<DropTableOnDestroy>(em); list.Add(nameof(DropTableOnDestroy)); }
            if (unit.Has<DropTable>(em)) { unit.Remove<DropTable>(em); list.Add(nameof(DropTable)); }
            removed = string.Join(", ", list);
            return !unit.Has<DropTableBuffer>(em) && !unit.Has<DropTableOnDeath>(em) && !unit.Has<DropTableOnDestroy>(em) && !unit.Has<DropTable>(em);
        }
        catch (Exception e)
        {
            LogRefuseOnce("DropLoot.throw", "Units: drop-table removal threw: " + e.Message);
            return false;
        }
    }
}
#endif
