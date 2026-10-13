#if !PROGENITOR_TESTS
using System;
using System.Runtime.CompilerServices;
using System.Text;
using Progenitor.Ai;
using ProjectM;
using Unity.Entities;

namespace Progenitor.Units;

/// <summary>
/// Live half of <c>ctx.Units.SetStats</c> / <c>GetStats</c> (docs/modules.md, "Unit stats"). Server
/// only; every entry point runs on the main thread. Field pokes at the declared layout offsets
/// through <see cref="AiRuntime.RW{T}"/>: <c>Health.MaxHealth</c> / <c>Value</c> /
/// <c>MaxRecoveryHealth</c>, <c>UnitStats.PhysicalPower</c> / <c>SpellPower</c> and
/// <c>UnitLevel.Level</c>. <c>UnitLevel.HideLevel</c> and every other field are never touched.
///
/// <para><b>When.</b> A fresh instance reads <c>Health.MaxHealth</c> 0; the game's spawn computation
/// fills health and power from the unit's <c>UnitSpawnData</c> settings on a later frame. A request
/// made before then is recorded and written by the first check that sees <c>MaxHealth</c> above 0
/// (<see cref="CheckDelays"/>); the later checks write a value again if the game reset it to what the
/// first write replaced.</para>
///
/// <para><b>Persistence.</b> <c>Health</c>, <c>UnitStats</c> and <c>UnitLevel</c> carry no
/// <c>ExcludeComponent</c> attribute (dump) and are not in <c>PersistenceV2Utility..cctor</c>'s
/// custom exclusions, so they are saved: a unit restored from a save keeps the values, with no
/// handle.</para>
/// </summary>
internal static unsafe class UnitStatsBody
{
    private static readonly ConditionalWeakTable<IUnitHandle, UnitStatState> States = new();

    /// <summary>
    /// Check delays after a <c>SetStats</c> or a spawn option: 0.25 s so a spawn-time request waits as
    /// little as possible for the spawn computation, then the faction write's 1 s and 5 s.
    /// </summary>
    internal static readonly double[] CheckDelays = { 0.25, 1.0, 5.0 };

    private static int HealthNeed => Layout.HealthIsDeadOffset + 1;
    private static int StatsNeed => Math.Max(Layout.UnitStatsPhysicalPowerOffset, Layout.UnitStatsSpellPowerOffset) + Layout.ModifiableFloatValueOffset + sizeof(float);
    private static int LevelNeed => Layout.UnitLevelLevelOffset + Layout.ModifiableIntValueOffset + sizeof(int);

    private static UnitStatState StateOf(IUnitHandle handle) => States.GetValue(handle, _ => new UnitStatState());

    /// <summary>
    /// Record <paramref name="stats"/> and write it now when the unit's spawn computation has run.
    /// Returns false when the write waits for it. Throws on a refusal; nothing is recorded then.
    /// </summary>
    internal static bool SetStats(IUnitHandle handle, UnitStatOverrides stats)
    {
        if (stats == null) throw new ArgumentNullException(nameof(stats));
        stats.Validate();
        var (em, entity) = Resolve(handle, "SetStats");
        if (!TryReadHealth(em, entity, out var health))
            throw new InvalidOperationException("unit " + handle.Entity + " has no Health.");
        if (stats.MaxHealth != null
            && UnitStatMath.DeadOrDownedRefusal(handle.Entity.ToString(), health.IsDead, HasDead(em, entity), health.Value, health.Max) is { } why)
            throw new InvalidOperationException(why);
        if ((stats.PhysicalPower != null || stats.SpellPower != null) && !Has<UnitStats>(em, entity))
            throw new InvalidOperationException("unit " + handle.Entity + " has no UnitStats.");
        if (stats.Level != null && !Has<UnitLevel>(em, entity))
            throw new InvalidOperationException("unit " + handle.Entity + " has no UnitLevel (a player character has none).");

        var state = StateOf(handle);
        state.Request(stats);
        return Apply(handle, em, entity, state);
    }

    /// <summary>Current and maximum health, and dead, for <c>ctx.Units.GetHealth</c>.</summary>
    internal static UnitHealth GetHealth(IUnitHandle handle)
    {
        var (em, entity) = Resolve(handle, "GetHealth");
        if (!TryReadHealth(em, entity, out var health)) throw new InvalidOperationException("unit " + handle.Entity + " has no Health.");
        return new UnitHealth(health.Value, health.Max, health.IsDead || HasDead(em, entity));
    }

    /// <summary>The unit's current values; <see cref="UnitStatOverrides.Level"/> is null when it has no <c>UnitLevel</c>.</summary>
    internal static UnitStatOverrides GetStats(IUnitHandle handle)
    {
        var (em, entity) = Resolve(handle, "GetStats");
        var result = new UnitStatOverrides();
        if (TryReadHealth(em, entity, out var health)) result.MaxHealth = health.Max;
        byte* stats = AiRuntime.RO<UnitStats>(em, entity, StatsNeed);
        if (stats != null)
        {
            result.PhysicalPower = *(float*)(stats + Layout.UnitStatsPhysicalPowerOffset + Layout.ModifiableFloatValueOffset);
            result.SpellPower = *(float*)(stats + Layout.UnitStatsSpellPowerOffset + Layout.ModifiableFloatValueOffset);
        }
        byte* level = AiRuntime.RO<UnitLevel>(em, entity, LevelNeed);
        if (level != null) result.Level = *(int*)(level + Layout.UnitLevelLevelOffset + Layout.ModifiableIntValueOffset);
        return result;
    }

    /// <summary>
    /// Check at 0.25 s, 1 s and 5 s: write a request still waiting for the spawn computation, and
    /// write an applied value again if the game reset it. Owned by <paramref name="owner"/>, so plugin
    /// unload drops the posts.
    /// </summary>
    internal static void ScheduleChecks(IUnitHandle handle, object owner)
    {
        int epoch = WorldState.WorldEpoch.Current;
        foreach (double delay in CheckDelays)
        {
            bool last = delay == CheckDelays[CheckDelays.Length - 1];
            Dev.MainThread.PostDelayed(delay, () => Check(handle, epoch, last), owner);
        }
    }

    private static void Check(IUnitHandle handle, int epoch, bool last)
    {
        try
        {
            if (handle.IsDisposed || !handle.Enabled) return;
            // A world reload re-numbers entities: the id may name another unit now.
            if (WorldState.WorldEpoch.IsStale(epoch)) return;
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;
            var entity = handle.Entity.ToEntity();
            if (!em.Exists(entity)) return;
            if (!States.TryGetValue(handle, out var state)) return;

            if (state.AnyPending)
            {
                if (!Apply(handle, em, entity, state) && last)
                    Plugin.Logger?.LogWarning("Units: stats for " + handle.Entity + " are still waiting: its Health.MaxHealth has read 0 since they were set,"
                        + " so the game's spawn computation has not run. They stay pending until the next SetStats.");
                return;
            }
            if (state.AnyApplied) Reapply(handle, em, entity, state);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: stats re-check of " + handle.Entity + " threw: " + e.Message);
        }
    }

    // ---- writes ---------------------------------------------------------------------------------

    private static bool Apply(IUnitHandle handle, EntityManager em, Entity entity, UnitStatState state)
    {
        if (!TryReadHealth(em, entity, out var health)) throw new InvalidOperationException("unit " + handle.Entity + " has no Health.");
        bool dead = health.IsDead || HasDead(em, entity);
        if (!dead && !UnitStatMath.SpawnComputed(health.Max)) return false;

        var log = new StringBuilder();
        var healthSlot = state[UnitStatKind.MaxHealth];
        if (healthSlot.Requested is float requestedMax)
        {
            if (UnitStatMath.DeadOrDownedRefusal(handle.Entity.ToString(), health.IsDead, HasDead(em, entity), health.Value, health.Max) is { } why)
            {
                healthSlot.Requested = null;
                Plugin.Logger?.LogWarning("Units: " + why + " The other stats are still written.");
            }
            else
            {
                byte* p = AiRuntime.RW<Health>(em, entity, HealthNeed);
                float current = *(float*)(p + Layout.HealthMaxOffset + Layout.ModifiableFloatValueOffset);
                float write = UnitStatMath.PlanWrite(UnitStatKind.MaxHealth, current, healthSlot.Applied, requestedMax);
                *(float*)(p + Layout.HealthMaxOffset + Layout.ModifiableFloatValueOffset) = write;
                *(float*)(p + Layout.HealthValueOffset) = write;
                *(float*)(p + Layout.HealthMaxRecoveryOffset) = write;
                Commit(healthSlot, current);
                Note(log, "MaxHealth", current, write);
            }
        }

        var physical = state[UnitStatKind.PhysicalPower];
        var spell = state[UnitStatKind.SpellPower];
        if (physical.Requested != null || spell.Requested != null)
        {
            byte* p = AiRuntime.RW<UnitStats>(em, entity, StatsNeed);
            WriteFloat(p + Layout.UnitStatsPhysicalPowerOffset + Layout.ModifiableFloatValueOffset, UnitStatKind.PhysicalPower, physical, "PhysicalPower", log);
            WriteFloat(p + Layout.UnitStatsSpellPowerOffset + Layout.ModifiableFloatValueOffset, UnitStatKind.SpellPower, spell, "SpellPower", log);
        }

        var level = state[UnitStatKind.Level];
        if (level.Requested is float requestedLevel)
        {
            byte* p = AiRuntime.RW<UnitLevel>(em, entity, LevelNeed);
            int* slot = (int*)(p + Layout.UnitLevelLevelOffset + Layout.ModifiableIntValueOffset);
            float current = *slot;
            float write = UnitStatMath.PlanWrite(UnitStatKind.Level, current, level.Applied, requestedLevel);
            *slot = (int)write;
            Commit(level, current);
            Note(log, "Level", current, write);
        }

        if (log.Length > 0) Plugin.Logger?.LogInfo("Units: stats of " + handle.Entity + ":" + log + ".");
        return true;
    }

    private static void WriteFloat(byte* field, UnitStatKind kind, UnitStatSlot slot, string name, StringBuilder log)
    {
        if (slot.Requested is not float requested) return;
        float current = *(float*)field;
        float write = UnitStatMath.PlanWrite(kind, current, slot.Applied, requested);
        *(float*)field = write;
        Commit(slot, current);
        Note(log, name, current, write);
    }

    private static void Commit(UnitStatSlot slot, float replaced)
    {
        slot.Displaced ??= replaced;
        slot.Applied = slot.Requested;
        slot.Requested = null;
    }

    private static void Reapply(IUnitHandle handle, EntityManager em, Entity entity, UnitStatState state)
    {
        var log = new StringBuilder();
        var healthSlot = state[UnitStatKind.MaxHealth];
        if (healthSlot.Applied is float max && healthSlot.Displaced is float maxWas
            && TryReadHealth(em, entity, out var health) && !health.IsDead
            && UnitStatMath.PlanReapply(health.Max, max, maxWas, out float writeMax))
        {
            byte* p = AiRuntime.RW<Health>(em, entity, HealthNeed);
            *(float*)(p + Layout.HealthMaxOffset + Layout.ModifiableFloatValueOffset) = writeMax;
            *(float*)(p + Layout.HealthValueOffset) = UnitStatMath.HealthAfterReset(health.Value, health.Max, writeMax);
            float recovery = *(float*)(p + Layout.HealthMaxRecoveryOffset);
            *(float*)(p + Layout.HealthMaxRecoveryOffset) = UnitStatMath.HealthAfterReset(recovery, health.Max, writeMax);
            Note(log, "MaxHealth", health.Max, writeMax);
        }

        var physical = state[UnitStatKind.PhysicalPower];
        var spell = state[UnitStatKind.SpellPower];
        if (physical.Applied != null || spell.Applied != null)
        {
            byte* p = AiRuntime.RO<UnitStats>(em, entity, StatsNeed) == null ? null : AiRuntime.RW<UnitStats>(em, entity, StatsNeed);
            if (p != null)
            {
                ReapplyFloat(p + Layout.UnitStatsPhysicalPowerOffset + Layout.ModifiableFloatValueOffset, physical, "PhysicalPower", log);
                ReapplyFloat(p + Layout.UnitStatsSpellPowerOffset + Layout.ModifiableFloatValueOffset, spell, "SpellPower", log);
            }
        }

        var level = state[UnitStatKind.Level];
        if (level.Applied is float lv && level.Displaced is float lvWas)
        {
            byte* p = AiRuntime.RO<UnitLevel>(em, entity, LevelNeed) == null ? null : AiRuntime.RW<UnitLevel>(em, entity, LevelNeed);
            if (p != null)
            {
                int* slot = (int*)(p + Layout.UnitLevelLevelOffset + Layout.ModifiableIntValueOffset);
                float current = *slot;
                if (UnitStatMath.PlanReapply(current, lv, lvWas, out float write))
                {
                    *slot = (int)write;
                    Note(log, "Level", current, write);
                }
            }
        }

        if (log.Length > 0)
            Plugin.Logger?.LogWarning("Units: stats of " + handle.Entity + " were reset after they were set; writing them again:" + log + ".");
    }

    private static void ReapplyFloat(byte* field, UnitStatSlot slot, string name, StringBuilder log)
    {
        if (slot.Applied is not float applied || slot.Displaced is not float displaced) return;
        float current = *(float*)field;
        if (!UnitStatMath.PlanReapply(current, applied, displaced, out float write)) return;
        *(float*)field = write;
        Note(log, name, current, write);
    }

    private static void Note(StringBuilder log, string name, float from, float to) =>
        log.Append(' ').Append(name).Append(' ').Append(UnitStatMath.F(from)).Append(" -> ").Append(UnitStatMath.F(to));

    // ---- reads ----------------------------------------------------------------------------------

    private readonly struct HealthRead
    {
        public HealthRead(float max, float value, bool isDead)
        {
            Max = max;
            Value = value;
            IsDead = isDead;
        }

        public float Max { get; }
        public float Value { get; }
        public bool IsDead { get; }
    }

    private static bool TryReadHealth(EntityManager em, Entity entity, out HealthRead health)
    {
        health = default;
        byte* p = AiRuntime.RO<Health>(em, entity, HealthNeed);
        if (p == null) return false;
        health = new HealthRead(
            *(float*)(p + Layout.HealthMaxOffset + Layout.ModifiableFloatValueOffset),
            *(float*)(p + Layout.HealthValueOffset),
            p[Layout.HealthIsDeadOffset] != 0);
        return true;
    }

    private static bool Has<T>(EntityManager em, Entity entity) =>
        em.HasComponent(entity, Ecs.ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly));

    private static bool HasDead(EntityManager em, Entity entity) => Has<Dead>(em, entity);

    private static (EntityManager Em, Entity Entity) Resolve(IUnitHandle handle, string op)
    {
        Units.EnsureMainThread(op);
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        if (handle.IsDisposed) throw new InvalidOperationException("the unit handle is disposed.");
        if (!handle.Enabled) throw new InvalidOperationException("the unit handle is disabled.");
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) throw new InvalidOperationException(op + " is server-only and there is no server world.");
        var em = world.EntityManager;
        var entity = handle.Entity.ToEntity();
        if (!em.Exists(entity)) throw new InvalidOperationException("unit " + handle.Entity + " no longer exists.");
        return (em, entity);
    }
}
#endif
