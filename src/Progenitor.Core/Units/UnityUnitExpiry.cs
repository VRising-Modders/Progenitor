#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM;
using Progenitor.Ecs;
using Progenitor.WorldState;
using Unity.Entities;

namespace Progenitor.Units;

/// <summary>
/// Live half of the unit lifetime contract (<see cref="UnitExpiryPolicy"/>; docs/modules.md,
/// "Lifetime across reload and restart").
///
/// <para><b>The stamp is on the entity.</b> <c>LifetimeSeconds</c> / <c>TrySetLifetime</c> write
/// <c>LifeTime {Duration = Age + seconds, Destroy}</c> and make sure the unit has <c>Age</c>. The
/// game's <c>LifeTimeSystem</c> ends the unit when <c>Duration &lt;= Age.Value</c>
/// (<c>Handle</c>), and its query is <c>WithAllRW&lt;Age&gt;</c>
/// (<c>__AssignQueries</c>), so a unit whose prefab has no <c>Age</c> was never
/// ended. Neither component carries a persistence exclusion in the server metadata, so
/// a world save, a Core swap and a restart all keep the stamp; nothing in Core's memory is needed.</para>
///
/// <para><b>Backstop sweep.</b> At Core install, after every world load and every
/// <see cref="UnitExpiryPolicy.SweepIntervalSeconds"/> seconds, Core queries units carrying
/// <c>LifeTime</c> and <c>Health</c> (disabled ones included). For an API-spawned unit (the
/// <c>progenitor.core.api-spawned</c> marker, or a live handle of this generation) with a destroying
/// lifetime it despawns the ones more than <see cref="UnitExpiryPolicy.GraceSeconds"/> past their
/// end through the ordinary Units path, and adds <c>Age</c> at 0 to the ones without it (units saved
/// before this fix). Ids are read and used inside one pass, never kept.</para>
/// </summary>
internal sealed unsafe partial class UnityUnitRuntime
{
    /// <summary>Age is one float.</summary>
    internal const int AgeMinBytes = Layout.AgeValueOffset + 4;

    /// <summary>
    /// Make sure <paramref name="unit"/> has <c>Age</c> (added at 0 when missing) and read it. False
    /// when the add or the guarded read is refused.
    /// </summary>
    private static bool TryEnsureAge(EntityManager em, Entity unit, out float age)
    {
        age = 0f;
        if (!unit.Has<Age>(em))
            unit.Add<Age>(em);
        return TryReadAge(em, unit, out age);
    }

    /// <summary><c>Age.Value</c> behind the size gate. False when the unit has no <c>Age</c> or the read is refused.</summary>
    private static bool TryReadAge(EntityManager em, Entity unit, out float age)
    {
        age = 0f;
        if (!unit.Has<Age>(em)) return false;
        var typeIndex = ComponentIndex.IndexOf<Age>();
        int typeSize = TypeSizeOf(typeIndex);
        int extent = typeSize > 0 ? typeSize : SizeInChunkOf(typeIndex);
        if (extent > 0 && extent < AgeMinBytes)
        {
            LogRefuseOnce("Age.size", "Units: Age read refused: extent=" + extent + " < " + AgeMinBytes + ".");
            return false;
        }
        byte* raw = (byte*)em.GetComponentDataRawRO(unit, typeIndex);
        if (raw == null) return false;
        age = *(float*)(raw + Layout.AgeValueOffset);
        return true;
    }

    /// <summary><c>LifeTime</c> bytes behind the size gate, or null when the unit has none or the gate refuses.</summary>
    private static byte* LifeTimeRaw(EntityManager em, Entity unit, bool write)
    {
        if (!unit.Has<LifeTime>(em)) return null;
        var typeIndex = ComponentIndex.IndexOf<LifeTime>();
        int typeSize = TypeSizeOf(typeIndex);
        int extent = typeSize > 0 ? typeSize : SizeInChunkOf(typeIndex);
        if (extent > 0 && extent < LifeTimeMinBytes)
        {
            LogRefuseOnce("LifeTime.size", "Units: LifeTime overlay refused: extent=" + extent + " < " + LifeTimeMinBytes + ".");
            return null;
        }
        return write
            ? (byte*)em.GetComponentDataRawRW(unit, typeIndex)
            : (byte*)em.GetComponentDataRawRO(unit, typeIndex);
    }

    /// <summary>The facts <see cref="UnitExpiryPolicy.Decide"/> needs, read without writing anything.</summary>
    internal static UnitExpiryFacts ReadExpiryFacts(EntityManager em, Entity unit, bool apiSpawned)
    {
        if (unit == Entity.Null || !em.Exists(unit))
            return new UnitExpiryFacts(false, false, apiSpawned, false, 0f, 0, false, 0f);
        bool destroying = unit.Has<DestroyTag>(em);
        bool hasLifeTime = false;
        float duration = 0f;
        int endAction = 0;
        byte* life = LifeTimeRaw(em, unit, write: false);
        if (life != null)
        {
            hasLifeTime = true;
            duration = *(float*)(life + Layout.LifeTimeDurationOffset);
            endAction = *(int*)(life + Layout.LifeTimeEndActionOffset);
        }
        bool hasAge = TryReadAge(em, unit, out float age);
        return new UnitExpiryFacts(true, destroying, apiSpawned, hasLifeTime, duration, endAction, hasAge, age);
    }

    /// <summary><see cref="IUnitExpiryWorld.StartClock"/>: add <c>Age</c> at 0. The unit's <c>Duration</c> then counts from now.</summary>
    internal static bool TryStartExpiryClock(EntityManager em, Entity unit)
    {
        try
        {
            if (!em.Exists(unit) || unit.Has<Age>(em)) return false;
            return TryEnsureAge(em, unit, out _);
        }
        catch (Exception e)
        {
            LogRefuseOnce("Age.add", "Units: Age add refused: " + e.Message);
            return false;
        }
    }
}

/// <summary>Schedules <see cref="UnitExpirySweep"/> on the server and adapts it to the live world.</summary>
internal static class UnityUnitExpiry
{
    private static readonly object Owner = new();
    private static int _generation;
    private static bool _installed;

    internal static void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        int generation = ++_generation;
        WorldEpoch.Advanced += OnAdvanced;
        // The first pass covers units an earlier Core generation or an earlier process left behind.
        Post(generation, 1.0, "core install", periodic: false);
        Post(generation, UnitExpiryPolicy.SweepIntervalSeconds, "periodic", periodic: true);
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        WorldEpoch.Advanced -= OnAdvanced;
        _installed = false;
        _generation++;
    }

    /// <summary>A load re-creates every entity: sweep once the loaded world has settled.</summary>
    private static void OnAdvanced(int epoch) => Post(_generation, 5.0, "world load", periodic: false);

    private static void Post(int generation, double delay, string why, bool periodic)
    {
        try
        {
            Dev.MainThread.PostDelayed(delay, () =>
            {
                if (generation != _generation || !_installed) return;
                try { RunOnce(why, quiet: periodic); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Units: lifetime sweep (" + why + ") threw: " + e.Message); }
                if (periodic) Post(generation, UnitExpiryPolicy.SweepIntervalSeconds, why, periodic: true);
            }, Owner);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: lifetime sweep defer threw: " + e.Message);
        }
    }

    /// <summary>One pass now. Logs when it acted, or always when <paramref name="quiet"/> is false.</summary>
    internal static UnitExpirySweepResult RunOnce(string why, bool quiet)
    {
        if (Units.IsSkipped || Units.Runtime is not UnityUnitRuntime runtime) return default;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return default;
        var adapter = new LiveWorld(world.EntityManager, runtime, WorldEpoch.Current);
        var result = UnitExpirySweep.Run(adapter);
        if (result.Acted || (!quiet && result.Limited > 0))
            Plugin.Logger?.LogInfo(UnitExpirySweep.Format(result, why));
        return result;
    }

    private sealed class LiveWorld : IUnitExpiryWorld
    {
        private readonly EntityManager _em;
        private readonly UnityUnitRuntime _runtime;
        private readonly int _epoch;
        private readonly HashSet<UnitId> _handles;

        internal LiveWorld(EntityManager em, UnityUnitRuntime runtime, int epoch)
        {
            _em = em;
            _runtime = runtime;
            _epoch = epoch;
            _handles = Units.LiveUnitIds();
        }

        public IReadOnlyList<UnitId> Candidates()
        {
            var entities = Query.Entities(
                _em,
                includeDisabled: true,
                includeSystems: false,
                ComponentIndex.Of<LifeTime>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<Health>(ComponentType.AccessMode.ReadOnly));
            var ids = new List<UnitId>(entities.Count);
            for (int i = 0; i < entities.Count; i++) ids.Add(UnitId.From(entities[i]));
            return ids;
        }

        public UnitExpiryFacts Read(UnitId unit)
        {
            var entity = unit.ToEntity();
            if (WorldEpoch.IsStale(_epoch) || !_em.Exists(entity))
                return new UnitExpiryFacts(false, false, false, false, 0f, 0, false, 0f);
            bool api = _handles.Contains(unit);
            if (!api)
            {
                var world = Worlds.Server;
                api = world != null && world.IsCreated && Progenitor.Ai.ApiSpawnMarker.Has(world, entity);
            }
            // Only an API-spawned unit's LifeTime is read; any other entity is left alone.
            if (!api) return new UnitExpiryFacts(true, false, false, false, 0f, 0, false, 0f);
            return UnityUnitRuntime.ReadExpiryFacts(_em, entity, apiSpawned: true);
        }

        public bool StartClock(UnitId unit)
        {
            if (WorldEpoch.IsStale(_epoch)) return false;
            bool ok = UnityUnitRuntime.TryStartExpiryClock(_em, unit.ToEntity());
            if (ok) Plugin.Logger?.LogInfo("Units: " + unit + " had a lifetime but no Age, so the game never ended it; Age added, it now ends after its LifeTime.Duration.");
            return ok;
        }

        public bool Despawn(UnitId unit)
        {
            if (WorldEpoch.IsStale(_epoch)) return false;
            bool ok = Units.ExpireUnit(unit, _runtime);
            if (ok) Plugin.Logger?.LogInfo("Units: " + unit + " outlived its lifetime; despawning it.");
            return ok;
        }
    }
}
#endif
