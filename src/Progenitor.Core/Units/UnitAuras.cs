#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Progenitor.Ai;
using Progenitor.Content;
using Progenitor.Ecs;
using Progenitor.WorldState;
using ProjectM;
using ProjectM.Shared;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Units;

/// <summary>
/// Live half of <c>ctx.Units.SetAura</c> / <c>GetAura</c> / <c>ClearAura</c> (docs/modules.md, "Unit aura").
/// Server only; every entry point runs on the main thread.
///
/// <para><b>Clones.</b> For each <see cref="UnitAuraTable"/> row Core registers a <c>DefineBuff</c>
/// clone of the row's vanilla source under a Core owner and a constant id, so the GUID is stable
/// across restarts (the API-spawn marker's pattern, <see cref="ApiSpawnMarker"/>). Core strips the
/// row's components from the clone's <b>server</b> prefab and checks the result; a clone that is not
/// verified inert is never applied. Clients build their own clone from the registry's definition
/// packet, so its visual sequence (client-world only) stays.</para>
///
/// <para><b>Restored units.</b> The buff instance is saved with the unit and the fixed GUID resolves
/// again after a restart. After every world load, once the prefab is stripped, every live instance
/// of an aura clone is stripped the same way and checked; one that cannot be made inert is removed.</para>
/// </summary>
internal static class UnitAuras
{
    private sealed class CoreOwner
    {
        public override string ToString() => "Progenitor Core (unit auras)";
    }

    private sealed class State
    {
        public UnitAura Aura;
    }

    private static readonly object Owner = new CoreOwner();
    private static readonly double[] Rechecks = { 1.0, 5.0 };
    private static readonly ConditionalWeakTable<IUnitHandle, State> States = new();
    private static readonly Dictionary<int, int> GuidByAura = new();
    private static readonly List<IDisposable> Registrations = new();
    private static readonly HashSet<UnitAura> WarnedNotInert = new();

    /// <summary>Typed strip and check for every name in <see cref="UnitAuraTable.KnownStripComponents"/>.</summary>
    private static readonly Dictionary<string, (Func<EntityManager, Entity, bool> Has, Action<EntityManager, Entity> Remove)> Components =
        new(StringComparer.Ordinal)
        {
            ["CreateGameplayEventsOnTick"] = Handler<CreateGameplayEventsOnTick>(),
            ["CreateGameplayEventsOnSpawn"] = Handler<CreateGameplayEventsOnSpawn>(),
            ["GameplayEventIdMapping"] = Handler<GameplayEventIdMapping>(),
            ["SpawnPrefabOnGameplayEvent"] = Handler<SpawnPrefabOnGameplayEvent>(),
            ["ApplyBuffOnGameplayEvent"] = Handler<ApplyBuffOnGameplayEvent>(),
            ["GameplayEventListeners"] = Handler<GameplayEventListeners>(),
            ["ModifyUnitLevelBuff"] = Handler<ModifyUnitLevelBuff>(),
            ["Affixes"] = Handler<Affixes>(),
        };

    private static bool _installed;
    private static int _generation;

    private static (Func<EntityManager, Entity, bool>, Action<EntityManager, Entity>) Handler<T>() =>
        ((em, e) => e.Has<T>(em), (em, e) => e.Remove<T>(em));

    /// <summary>The clone GUID of <paramref name="row"/>: <c>CalculateGuidHash</c> of its fixed id.</summary>
    internal static int GuidOf(UnitAuraRow row)
    {
        lock (GuidByAura)
        {
            if (!GuidByAura.TryGetValue((int)row.Aura, out int guid))
            {
                guid = PrefabGUID.CalculateGuidHash(row.Id);
                GuidByAura[(int)row.Aura] = guid;
            }
            return guid;
        }
    }

    // ---- lifecycle ------------------------------------------------------------------------------

    internal static void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        _generation++;
        WarnedNotInert.Clear();
        foreach (string name in UnitAuraTable.KnownStripComponents)
            if (!Components.ContainsKey(name))
                Plugin.Logger?.LogError("Units: aura strip component '" + name + "' has no handler; auras listing it are never applied.");
        foreach (var row in UnitAuraTable.Rows)
        {
            try
            {
                Registrations.Add(PrefabRegistry.DefineBuff(row.Id).CloneOf(row.Source).Register(Owner));
                Plugin.Logger?.LogInfo("Units: aura " + row.Aura + " " + row.Id + " = " + GuidOf(row) + " registered (clone of " + row.Source + ", made inert on the server by Core)");
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError("Units: aura " + row.Aura + " registration threw: " + e.Message + "; SetAura(" + row.Aura + ") will refuse.");
            }
        }
        WorldEpoch.Advanced += OnAdvanced;
        PostInert("core install", 0.0);
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        WorldEpoch.Advanced -= OnAdvanced;
        _installed = false;
        _generation++;
        // Soft unregister: the clones stay resolvable, so units wearing an aura stay valid; the next
        // Core generation registers the same ids again.
        foreach (var registration in Registrations)
        {
            try { registration.Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Units: aura unregister threw: " + e.Message); }
        }
        Registrations.Clear();
    }

    private static void OnAdvanced(int epoch) => PostInert("world load", 0.0);

    /// <summary>
    /// Strip every aura clone as soon as it exists, then strip the live instances a save restored
    /// (they were copied from the prefab before Core could strip it). Retries every 5 s up to a minute.
    /// </summary>
    private static void PostInert(string why, double delay)
    {
        int generation = _generation;
        Dev.MainThread.PostDelayed(delay, () =>
        {
            if (generation != _generation || !_installed) return;
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            bool all = true;
            foreach (var row in UnitAuraTable.Rows)
                if (!TryInert(world, row, out _)) all = false;
            if (all) SweepInstances(world, why);
            else if (delay < 60.0) PostInert(why, delay + 5.0);
        });
    }

    // ---- inertness ------------------------------------------------------------------------------

    /// <summary>The aura's server prefab, stripped and checked. False (with why) when it is not registered or not inert.</summary>
    internal static bool TryInert(World world, UnitAuraRow row, out string why)
    {
        why = "";
        if (!PrefabLookup.TryGetEntity(world, new PrefabGUID(GuidOf(row)), out var prefab) || prefab == Entity.Null)
        {
            why = "aura buff " + row.Id + " (" + GuidOf(row) + ") is not registered yet";
            return false;
        }
        var em = world.EntityManager;
        Strip(em, prefab, row);
        var problems = Problems(em, prefab, row);
        if (problems.Count == 0) return true;
        why = "aura buff " + row.Id + " could not be made inert: " + string.Join(", ", problems);
        return false;
    }

    /// <summary>Remove the row's components. Prefab or live instance; never touches the visual sequence.</summary>
    private static void Strip(EntityManager em, Entity e, UnitAuraRow row)
    {
        foreach (string name in row.Strip)
        {
            if (!Components.TryGetValue(name, out var handler)) continue;
            try { if (handler.Has(em, e)) handler.Remove(em, e); }
            catch (Exception ex) { Plugin.Logger?.LogWarning("Units: aura strip " + name + " threw: " + ex.Message); }
        }
    }

    /// <summary>
    /// What stops <paramref name="e"/> being inert: a listed component still present or without a
    /// handler, no <c>Buff</c>, or a finite <c>LifeTime</c> (the source has none: permanent).
    /// </summary>
    private static List<string> Problems(EntityManager em, Entity e, UnitAuraRow row)
    {
        var problems = new List<string>();
        foreach (string name in row.Strip)
        {
            if (!Components.TryGetValue(name, out var handler)) problems.Add("no handler for " + name);
            else if (handler.Has(em, e)) problems.Add("still has " + name);
        }
        if (!e.Has<Buff>(em)) problems.Add("no Buff");
        if (e.Has<LifeTime>(em) && e.TryRead(em, out LifeTime life) && life.Duration >= 0f && life.EndAction != LifeTimeEndAction.None)
            problems.Add("LifeTime is " + life.Duration + "/" + life.EndAction);
        return problems;
    }

    /// <summary>Strip and check every live instance of an aura clone; destroy one that stays live.</summary>
    private static void SweepInstances(World world, string why)
    {
        try
        {
            var em = world.EntityManager;
            var byGuid = new Dictionary<int, UnitAuraRow>();
            foreach (var row in UnitAuraTable.Rows) byGuid[GuidOf(row)] = row;
            int stripped = 0, removed = 0;
            var buffs = Query.Entities(
                em,
                includeDisabled: true,
                includeSystems: false,
                ComponentIndex.Of<Buff>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly));
            foreach (var instance in buffs)
            {
                if (!byGuid.TryGetValue(AiRuntime.PrefabGuidOf(em, instance), out var row)) continue;
                Strip(em, instance, row);
                var problems = Problems(em, instance, row);
                if (problems.Count == 0)
                {
                    stripped++;
                    continue;
                }
                Plugin.Logger?.LogWarning("Units: aura " + row.Aura + " instance " + AiRuntime.Fmt(instance) + " could not be made inert (" + string.Join(", ", problems) + "); removing it.");
                DestroyUtility.Destroy(em, instance, DestroyDebugReason.TryRemoveBuff);
                removed++;
            }
            if (stripped > 0 || removed > 0)
                Plugin.Logger?.LogInfo("Units: aura instances after " + why + ": " + stripped + " checked inert, " + removed + " removed.");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Units: aura instance sweep (" + why + ") threw: " + e.Message);
        }
    }

    // ---- per unit -------------------------------------------------------------------------------

    /// <summary>
    /// Put <paramref name="aura"/> on the unit, replacing any other aura. <see cref="UnitAura.None"/>
    /// clears. False when the clone is not verified inert yet (one warning per aura per generation);
    /// nothing was put on the unit then. Checked again at 1 s and 5 s.
    /// </summary>
    internal static bool Set(IUnitHandle handle, UnitAura aura, object? owner)
    {
        UnitAuraTable.Validate(aura, nameof(aura));
        var (world, entity) = Resolve(handle, "SetAura");
        if (aura == UnitAura.None)
        {
            ClearCore(world, handle, entity);
            return true;
        }
        UnitAuraTable.TryGet(aura, out var row);
        var state = States.GetValue(handle, _ => new State());
        if (!TryInert(world, row, out string why))
        {
            lock (WarnedNotInert)
                if (WarnedNotInert.Add(aura))
                    Plugin.Logger?.LogWarning("Units: aura " + aura + " not applied to " + handle.Entity + ": " + why + ". A clone that is not verified inert is never applied.");
            return false;
        }
        var em = world.EntityManager;
        foreach (int other in UnitAuraTable.ToRemove(AiRuntime.BuffGuids(em, entity), aura, GuidOf))
            AiRuntime.RemoveBuff(world, entity, other);
        state.Aura = aura;
        if (!Has(em, entity, row)) AiRuntime.ApplyBuff(world, entity, GuidOf(row));
        ScheduleChecks(handle, entity, aura, owner);
        return true;
    }

    /// <summary>The aura the unit wears now, read from its buffs (so a restored unit reports it too).</summary>
    internal static UnitAura Get(IUnitHandle handle)
    {
        var (world, entity) = Resolve(handle, "GetAura");
        return UnitAuraTable.FromBuffs(AiRuntime.BuffGuids(world.EntityManager, entity), GuidOf);
    }

    /// <summary>The aura <paramref name="unit"/> wears now; any server entity, such as a unit restored from a save.</summary>
    internal static UnitAura Get(Entity unit)
    {
        Units.EnsureMainThread("GetAura");
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) throw new InvalidOperationException("GetAura is server-only and there is no server world.");
        if (unit == Entity.Null || !world.EntityManager.Exists(unit)) throw new InvalidOperationException("entity " + AiRuntime.Fmt(unit) + " does not exist.");
        return UnitAuraTable.FromBuffs(AiRuntime.BuffGuids(world.EntityManager, unit), GuidOf);
    }

    /// <summary>Remove every aura buff instance from the unit. Nothing happens when it has none.</summary>
    internal static void Clear(IUnitHandle handle)
    {
        var (world, entity) = Resolve(handle, "ClearAura");
        ClearCore(world, handle, entity);
    }

    private static void ClearCore(World world, IUnitHandle handle, Entity entity)
    {
        if (States.TryGetValue(handle, out var state)) state.Aura = UnitAura.None;
        foreach (int guid in UnitAuraTable.ToRemove(AiRuntime.BuffGuids(world.EntityManager, entity), UnitAura.None, GuidOf))
            AiRuntime.RemoveBuff(world, entity, guid);
    }

    private static bool Has(EntityManager em, Entity unit, UnitAuraRow row) =>
        AiRuntime.FindBuff(em, unit, GuidOf(row)) != Entity.Null;

    /// <summary>
    /// Re-check at 1 s and 5 s (the faction write's schedule): put the aura on again when it is still
    /// wanted and missing. Skipped once the handle is disposed, the aura changed, or the world reloaded.
    /// </summary>
    private static void ScheduleChecks(IUnitHandle handle, Entity entity, UnitAura aura, object? owner)
    {
        int epoch = WorldEpoch.Current;
        int generation = _generation;
        foreach (double delay in Rechecks)
        {
            double d = delay;
            Dev.MainThread.PostDelayed(d, () =>
            {
                try
                {
                    if (generation != _generation || !_installed) return;
                    if (handle.IsDisposed || !handle.Enabled || WorldEpoch.IsStale(epoch)) return;
                    if (!States.TryGetValue(handle, out var state) || state.Aura != aura) return;
                    var world = Worlds.Server;
                    if (world == null || !world.IsCreated || !world.EntityManager.Exists(entity)) return;
                    if (!UnitAuraTable.TryGet(aura, out var row) || Has(world.EntityManager, entity, row)) return;
                    if (!TryInert(world, row, out _)) return;
                    Plugin.Logger?.LogWarning("Units: aura " + aura + " missing on " + handle.Entity + " " + d + " s after it was set; applying again.");
                    AiRuntime.ApplyBuff(world, entity, GuidOf(row));
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Units: aura re-check threw: " + e.Message);
                }
            }, owner);
        }
    }

    private static (World World, Entity Entity) Resolve(IUnitHandle handle, string op)
    {
        Units.EnsureMainThread(op);
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        if (handle.IsDisposed) throw new InvalidOperationException("the unit handle is disposed.");
        if (!handle.Enabled) throw new InvalidOperationException("the unit handle is disabled.");
        if (!_installed) throw new InvalidOperationException(op + " is server-only and unit auras are not installed in this process.");
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) throw new InvalidOperationException(op + " is server-only and there is no server world.");
        var entity = handle.Entity.ToEntity();
        if (!world.EntityManager.Exists(entity)) throw new InvalidOperationException("unit " + handle.Entity + " no longer exists.");
        return (world, entity);
    }
}
#endif
