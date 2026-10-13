#if !PROGENITOR_TESTS
using System;
using Progenitor.Content;
using Progenitor.Ecs;
using Progenitor.WorldState;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Ai;

/// <summary>
/// The Core-owned tag on every unit spawned through the API (<c>ctx.Units.Spawn</c> and the static
/// <c>Units.Spawn</c> forms): an inert <c>DefineBuff</c> clone of <c>Buff_General_Haste</c>, stripped
/// by Core to an infinite, effect-free buff (<see cref="AiVariants.MakeInert"/>, the variant-marker
/// path). Ally-safe hits (<see cref="AllySafety"/>) guard a hit only when its caster wears it.
///
/// <para>The id, and so the GUID, is fixed, so a unit restored from a save still wears a marker the
/// registry resolves after a restart (the same way variant markers do). Registered by Core at
/// install on the server, under a Core owner; the registry applies it once prefabs load. The spawn
/// path applies it on the spawn tick and checks again at 1 s and 5 s.</para>
///
/// <para>Progenitor clients build their own copy of the clone from their own <c>Buff_General_Haste</c>
/// and spawn the replicated buff from it; <see cref="InertClones"/> strips that copy (shimmer, speed,
/// buff-bar entry) the frame it is built.</para>
/// </summary>
internal static class ApiSpawnMarker
{
    /// <summary>Stable registry id; never change it, saved units reference its GUID.</summary>
    internal const string Id = InertCloneRules.ApiSpawnMarkerId;

    internal static readonly int Guid = PrefabGUID.CalculateGuidHash(Id);

    private static readonly double[] Rechecks = { 1.0, 5.0 };
    private static readonly object Owner = new CoreOwner();
    private static IDisposable? _registration;
    private static bool _installed;
    private static int _generation;
    private static bool _missingLogged;

    private sealed class CoreOwner
    {
        public override string ToString() => "Progenitor Core (API-spawn marker)";
    }

    internal static void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        _generation++;
        _missingLogged = false;
        try
        {
            _registration = PrefabRegistry.DefineBuff(Id).CloneOf(AiVariants.InertSources[0]).Register(Owner);
            Plugin.Logger?.LogInfo("Ai: API-spawn marker " + Id + " = " + Guid + " registered (clone of " + AiVariants.InertSources[0] + ", made inert by Core)");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Ai: API-spawn marker registration threw: " + e.Message + "; ally-safe hits cannot apply to spawned units.");
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
        // Soft unregister: the clone stays resolvable, so units wearing the marker stay valid; the
        // next Core generation registers the same id again.
        try { _registration?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ai: API-spawn marker unregister threw: " + e.Message); }
        _registration = null;
    }

    private static void OnAdvanced(int epoch) => PostInert("world load", 0.0);

    /// <summary>Make the clone inert as soon as it exists (a load re-creates nothing, but a new world does).</summary>
    private static void PostInert(string why, double delay)
    {
        int generation = _generation;
        Dev.MainThread.PostDelayed(delay, () =>
        {
            if (generation != _generation || !_installed) return;
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            if (!TryInert(world, out _) && delay < 60.0) PostInert(why, delay + 5.0);
        });
    }

    /// <summary>The marker prefab, stripped and checked. False (with why) when it is not registered or not inert.</summary>
    internal static bool TryInert(World world, out string why)
    {
        why = "";
        if (!PrefabLookup.TryGetEntity(world, new PrefabGUID(Guid), out var marker) || marker == Entity.Null)
        {
            why = "marker buff " + Id + " (" + Guid + ") is not registered yet";
            return false;
        }
        try
        {
            AiVariants.MakeInert(world, Id, marker);
            return true;
        }
        catch (Exception e)
        {
            why = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Spawn path, server: tag <paramref name="unit"/> now and check again at 1 s and 5 s (the
    /// faction write's schedule). Never throws into the spawn.
    /// </summary>
    internal static void OnSpawned(Entity unit)
    {
        if (!_installed) return;
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            Put(world, unit);
            int epoch = WorldEpoch.Current;
            int generation = _generation;
            foreach (double delay in Rechecks)
            {
                Dev.MainThread.PostDelayed(delay, () =>
                {
                    try
                    {
                        if (generation != _generation || !_installed || WorldEpoch.IsStale(epoch)) return;
                        var w = Worlds.Server;
                        if (w == null || !w.IsCreated || !w.EntityManager.Exists(unit)) return;
                        if (Has(w, unit)) return;
                        Plugin.Logger?.LogWarning("Ai: API-spawn marker missing on " + AiRuntime.Fmt(unit) + " " + delay + " s after spawn; applying again.");
                        Put(w, unit);
                    }
                    catch (Exception e)
                    {
                        Plugin.Logger?.LogWarning("Ai: API-spawn marker re-check threw: " + e.Message);
                    }
                });
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ai: API-spawn marker on " + AiRuntime.Fmt(unit) + " threw: " + e.Message);
        }
    }

    /// <summary>True when <paramref name="unit"/> wears the marker.</summary>
    internal static bool Has(World world, Entity unit) =>
        AiRuntime.FindBuff(world.EntityManager, unit, Guid) != Entity.Null;

    private static void Put(World world, Entity unit)
    {
        if (!TryInert(world, out string why))
        {
            // Never put a non-inert clone (a live Haste) on a unit.
            if (!_missingLogged)
            {
                _missingLogged = true;
                Plugin.Logger?.LogWarning("Ai: API-spawn marker not applied: " + why + ". Ally-safe hits do not cover units spawned before it is ready.");
            }
            return;
        }
        if (!Has(world, unit)) AiRuntime.ApplyBuff(world, unit, Guid);
    }
}
#endif
