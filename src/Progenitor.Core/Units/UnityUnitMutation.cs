#if !PROGENITOR_TESTS
using System;
using ProjectM;
using ProjectM.Shared;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Units;

/// <summary>
/// Live instance mutation writes for <see cref="IUnitHandle"/> helpers: Health / UnitStats /
/// UnitLevel / Team / LifeTime / VBlood strip. Instance-only � never the shared prefab.
/// </summary>
internal sealed unsafe partial class UnityUnitRuntime
{
    /// <summary>
    /// Layout pin for <c>FactionReference.FactionGuid</c>. Team writes today use
    /// <see cref="Layout.TeamFactionIndexOffset"/> only; keep this offset linked so layoutgen
    /// drift is caught when a FactionGuid instance sync is added.
    /// </summary>
    internal const int FactionReferenceGuidPin = Layout.FactionReferenceGuidOffset;
    public bool TryGetHealth(UnitId unitId, out float maxHealth, out float value, out bool isDead)
    {
        maxHealth = 0f; value = 0f; isDead = false;
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return false;
        try
        {
            if (!unit.Has<Health>(em)) return false;
            byte* p = (byte*)em.GetComponentDataRawRW(unit, ComponentIndex.IndexOf<Health>());
            if (p == null) return false;
            maxHealth = *(float*)(p + Layout.HealthMaxOffset + Layout.ModifiableFloatValueOffset);
            value = *(float*)(p + Layout.HealthValueOffset);
            isDead = *(p + Layout.HealthIsDeadOffset) != 0;
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("Health.read", "Units: Health read refused: " + e.Message);
            return false;
        }
    }

    public bool HasDeadComponent(UnitId unitId)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return false;
        try { return unit.Has<Dead>(em); }
        catch { return false; }
    }

    public bool TrySetHealthPools(UnitId unitId, float maxHealth, float value, float maxRecovery)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return false;
        try
        {
            if (!unit.Has<Health>(em)) return false;
            byte* p = (byte*)em.GetComponentDataRawRW(unit, ComponentIndex.IndexOf<Health>());
            if (p == null) return false;
            *(float*)(p + Layout.HealthMaxOffset + Layout.ModifiableFloatValueOffset) = maxHealth;
            *(float*)(p + Layout.HealthValueOffset) = value;
            *(float*)(p + Layout.HealthMaxRecoveryOffset) = maxRecovery;
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("Health.write", "Units: Health write refused: " + e.Message);
            return false;
        }
    }

    public bool TryGetUnitPower(UnitId unitId, out float physicalPower, out float spellPower)
    {
        physicalPower = 0f; spellPower = 0f;
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return false;
        try
        {
            if (!unit.Has<UnitStats>(em)) return false;
            byte* p = (byte*)em.GetComponentDataRawRO(unit, ComponentIndex.IndexOf<UnitStats>());
            if (p == null) return false;
            physicalPower = *(float*)(p + Layout.UnitStatsPhysicalPowerOffset + Layout.ModifiableFloatValueOffset);
            spellPower = *(float*)(p + Layout.UnitStatsSpellPowerOffset + Layout.ModifiableFloatValueOffset);
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("UnitStats.read", "Units: UnitStats read refused: " + e.Message);
            return false;
        }
    }

    public bool TrySetUnitPower(UnitId unitId, float? physicalPower, float? spellPower)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return false;
        try
        {
            if (!unit.Has<UnitStats>(em)) return false;
            byte* p = (byte*)em.GetComponentDataRawRW(unit, ComponentIndex.IndexOf<UnitStats>());
            if (p == null) return false;
            if (physicalPower is float phys)
                *(float*)(p + Layout.UnitStatsPhysicalPowerOffset + Layout.ModifiableFloatValueOffset) = phys;
            if (spellPower is float spell)
                *(float*)(p + Layout.UnitStatsSpellPowerOffset + Layout.ModifiableFloatValueOffset) = spell;
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("UnitStats.write", "Units: UnitStats write refused: " + e.Message);
            return false;
        }
    }

    public bool TryGetLevel(UnitId unitId, out int level)
    {
        level = 0;
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return false;
        try
        {
            if (!unit.Has<UnitLevel>(em)) return false;
            byte* p = (byte*)em.GetComponentDataRawRO(unit, ComponentIndex.IndexOf<UnitLevel>());
            if (p == null) return false;
            level = *(int*)(p + Layout.UnitLevelLevelOffset + Layout.ModifiableIntValueOffset);
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("UnitLevel.read", "Units: UnitLevel read refused: " + e.Message);
            return false;
        }
    }

    public bool TrySetLevel(UnitId unitId, int level)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return false;
        try
        {
            if (!unit.Has<UnitLevel>(em)) return false;
            byte* p = (byte*)em.GetComponentDataRawRW(unit, ComponentIndex.IndexOf<UnitLevel>());
            if (p == null) return false;
            *(int*)(p + Layout.UnitLevelLevelOffset + Layout.ModifiableIntValueOffset) = level;
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("UnitLevel.write", "Units: UnitLevel write refused: " + e.Message);
            return false;
        }
    }

    public bool TrySetLifetime(UnitId unitId, float seconds)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return false;
        float? clamped = UnitInstanceMutation.ClampLifetime(seconds);
        if (clamped == null) return false;
        try
        {
            // LifeTimeSystem only sees entities with Age (WithAllRW<Age>) and
            // ends one when Duration <= Age.Value (Handle). A prefab without Age was
            // never ended, so add it; and stamp Duration against the current Age so the unit ends
            // `seconds` from now. Both components are saved with the unit (UnityUnitExpiry).
            if (!TryEnsureAge(em, unit, out float age)) return false;
            if (!unit.Has<LifeTime>(em))
                unit.Add<LifeTime>(em);
            byte* p = LifeTimeRaw(em, unit, write: true);
            if (p == null) return false;
            *(float*)(p + Layout.LifeTimeDurationOffset) = UnitExpiryPolicy.StampDuration(age, clamped.Value);
            *(int*)(p + Layout.LifeTimeEndActionOffset) = Layout.LifeTimeEndActionDestroy;
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("LifeTime.write", "Units: LifeTime write refused: " + e.Message);
            return false;
        }
    }

    public bool TrySetTeam(UnitId unitId, int? teamValue, int? factionIndex)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit)) return false;
        try
        {
            if (!unit.Has<Team>(em)) return false;
            byte* p = (byte*)em.GetComponentDataRawRW(unit, ComponentIndex.IndexOf<Team>());
            if (p == null) return false;
            if (teamValue is int tv)
                *(int*)(p + Layout.TeamValueOffset) = tv;
            if (factionIndex is int fi)
                *(int*)(p + Layout.TeamFactionIndexOffset) = fi;
            return true;
        }
        catch (Exception e)
        {
            LogRefuseOnce("Team.write", "Units: Team write refused: " + e.Message);
            return false;
        }
    }

    public UnitStripVBloodResult StripVBlood(UnitId unitId)
    {
        if (!TryWorld(out var em) || !TryAlive(em, unitId, out var unit))
            return default;

        bool cleared = true;
        int removed = 0;
        try
        {
            if (unit.Has<BloodConsumeSource>(em))
            {
                byte* p = (byte*)em.GetComponentDataRawRW(unit, ComponentIndex.IndexOf<BloodConsumeSource>());
                if (p == null)
                    cleared = false;
                else
                    *(p + Layout.BloodConsumeSourceCanBeConsumedOffset) = 0;
            }
        }
        catch (Exception e)
        {
            LogRefuseOnce("BloodConsume.CanBeConsumed", "Units: CanBeConsumed clear refused: " + e.Message);
            cleared = false;
        }

        try
        {
            if (unit.Has<VBloodUnit>(em))
            {
                unit.Remove<VBloodUnit>(em);
                removed++;
            }
        }
        catch (Exception e)
        {
            LogRefuseOnce("Strip.VBloodUnit", "Units: remove VBloodUnit refused: " + e.Message);
        }

        try
        {
            if (unit.Has<VBloodConsumeSource>(em))
            {
                unit.Remove<VBloodConsumeSource>(em);
                removed++;
            }
        }
        catch (Exception e)
        {
            LogRefuseOnce("Strip.VBloodConsumeSource", "Units: remove VBloodConsumeSource refused: " + e.Message);
        }

        return new UnitStripVBloodResult(cleared, removed, ok: true);
    }
}
#endif
