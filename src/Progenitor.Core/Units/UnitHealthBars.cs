#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Ai;
using Progenitor.Content;
using Progenitor.Ecs;
using Progenitor.WorldState;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Units;

/// <summary>
/// Live half of <c>ctx.Units.SetHealthBarHidden</c> / <c>IsHealthBarHidden</c> and
/// <see cref="UnitSpawnOptions.HideHealthBar"/> (docs/modules.md, "Hide a unit's health bar").
///
/// <para><b>Mechanism.</b> The client's <c>GetCharacterHUDSystem</c> skips the HUD (health bar and name)
/// of every unit that wears a buff carrying <c>HideTargetHUD</c>. Core registers one inert
/// <c>DefineBuff</c> clone of <see cref="InertCloneRules.MarkerSource"/> under a fixed id
/// (<see cref="InertCloneRules.HideHudId"/>): stripped exactly like the API-spawn marker
/// (<see cref="AiVariants.MakeInert"/>) on the server prefab here. Only the client's copy gets
/// <c>HideTargetHUD { Type = -1 }</c> (<see cref="InertClones"/>): the HUD job runs on the client, and the
/// server instantiates <c>BuffUtility.HasBuff&lt;HideTargetHUD&gt;</c> for a reader nobody has identified,
/// so the server prefab is left without it. What a client without Progenitor shows is not verified.</para>
///
/// <para><b>Lifetime.</b> Applied through <see cref="AiRuntime.ApplyBuff"/>, checked again at 1 s and
/// 5 s. The buff is saved with the unit (fixed GUID). Plugin unload removes it from every unit that
/// plugin hid and that still exists. Server only; main thread.</para>
/// </summary>
internal static class UnitHealthBars
{
    private sealed class CoreOwner
    {
        public override string ToString() => "Progenitor Core (hide health bar)";
    }

    private sealed class Request
    {
        public string PluginId = "";
        public int Epoch;
    }

    internal static readonly int Guid = PrefabGUID.CalculateGuidHash(InertCloneRules.HideHudId);

    private static readonly object Owner = new CoreOwner();
    private static readonly double[] Rechecks = { 1.0, 5.0 };
    private static readonly Dictionary<UnitId, Request> Requests = new();
    private static IDisposable? _registration;
    private static bool _installed;
    private static int _generation;
    private static bool _notReadyLogged;

    internal static void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        _generation++;
        _notReadyLogged = false;
        try
        {
            _registration = PrefabRegistry.DefineBuff(InertCloneRules.HideHudId).CloneOf(InertCloneRules.MarkerSource).Register(Owner);
            Plugin.Logger?.LogInfo("Units: hide-health-bar buff " + InertCloneRules.HideHudId + " = " + Guid + " registered (clone of " + InertCloneRules.MarkerSource + ", made inert by Core; HideTargetHUD on the client copy only)");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Units: hide-health-bar buff registration threw: " + e.Message + "; SetHealthBarHidden will refuse.");
        }
        WorldEpoch.Advanced += OnAdvanced;
        PostPrepare(0.0);
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        WorldEpoch.Advanced -= OnAdvanced;
        _installed = false;
        _generation++;
        Requests.Clear();
        // Soft unregister: the clone stays resolvable, so units wearing it stay valid.
        try { _registration?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Units: hide-health-bar unregister threw: " + e.Message); }
        _registration = null;
    }

    internal static string DescribePins() =>
        HarmonySlotHandoff.FormatModulePins(Units.ModuleName + ".HealthBars",
            Requests.Count > 0 ? new List<string> { "hidden=" + Requests.Count } : new List<string>());

    private static void OnAdvanced(int epoch)
    {
        Requests.Clear(); // entity ids of the old world mean nothing now
        PostPrepare(0.0);
    }

    private static void PostPrepare(double delay)
    {
        int generation = _generation;
        Dev.MainThread.PostDelayed(delay, () =>
        {
            if (generation != _generation || !_installed) return;
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            if (!TryPrepare(world, out _) && delay < 60.0) PostPrepare(delay + 5.0);
        });
    }

    /// <summary>The clone's server prefab, stripped and checked. False (with why) when it is not ready.</summary>
    internal static bool TryPrepare(World world, out string why)
    {
        why = "";
        if (!PrefabLookup.TryGetEntity(world, new PrefabGUID(Guid), out var prefab) || prefab == Entity.Null)
        {
            why = "hide-health-bar buff " + InertCloneRules.HideHudId + " (" + Guid + ") is not registered yet";
            return false;
        }
        try
        {
            AiVariants.MakeInert(world, InertCloneRules.HideHudId, prefab);
            return true;
        }
        catch (Exception e)
        {
            why = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Hide or show the unit's health bar. True when the unit now has the requested state (or the buff
    /// was queued); false when the clone is not ready yet (one warning per generation; nothing changed).
    /// </summary>
    internal static bool Set(IUnitHandle handle, bool hidden, object? owner, string pluginId)
    {
        var (world, entity) = Resolve(handle, "SetHealthBarHidden");
        var em = world.EntityManager;
        var id = handle.Entity;
        if (!hidden)
        {
            Requests.Remove(id);
            AiRuntime.RemoveBuff(world, entity, Guid);
            return true;
        }
        if (!TryPrepare(world, out string why))
        {
            if (!_notReadyLogged)
            {
                _notReadyLogged = true;
                Plugin.Logger?.LogWarning("Units: health bar of " + id + " not hidden: " + why + ". A clone that is not verified inert is never applied.");
            }
            return false;
        }
        Prune(em);
        Requests[id] = new Request { PluginId = pluginId ?? "", Epoch = WorldEpoch.Current };
        if (AiRuntime.FindBuff(em, entity, Guid) == Entity.Null) AiRuntime.ApplyBuff(world, entity, Guid);
        ScheduleChecks(handle, id, entity, owner);
        return true;
    }

    /// <summary>True when the unit wears the hide-health-bar buff (so a unit restored from a save reports it too).</summary>
    internal static bool IsHidden(IUnitHandle handle)
    {
        var (world, entity) = Resolve(handle, "IsHealthBarHidden");
        return AiRuntime.FindBuff(world.EntityManager, entity, Guid) != Entity.Null;
    }

    /// <summary>Plugin unload: show the health bar again on every unit this plugin hid that still exists.</summary>
    internal static void ReleaseOwnedBy(string pluginId)
    {
        if (!_installed || Requests.Count == 0) return;
        var mine = new List<KeyValuePair<UnitId, Request>>();
        foreach (var pair in Requests)
            if (string.Equals(pair.Value.PluginId, pluginId ?? "", StringComparison.Ordinal)) mine.Add(pair);
        var world = Worlds.Server;
        foreach (var pair in mine)
        {
            Requests.Remove(pair.Key);
            if (world == null || !world.IsCreated || WorldEpoch.IsStale(pair.Value.Epoch)) continue;
            try
            {
                var entity = pair.Key.ToEntity();
                if (world.EntityManager.Exists(entity)) AiRuntime.RemoveBuff(world, entity, Guid);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Units: health bar release of " + pair.Key + " threw: " + e.Message);
            }
        }
    }

    private static void Prune(EntityManager em)
    {
        if (Requests.Count < 64) return;
        var gone = new List<UnitId>();
        foreach (var pair in Requests)
            if (WorldEpoch.IsStale(pair.Value.Epoch) || !em.Exists(pair.Key.ToEntity())) gone.Add(pair.Key);
        foreach (var id in gone) Requests.Remove(id);
    }

    private static void ScheduleChecks(IUnitHandle handle, UnitId id, Entity entity, object? owner)
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
                    if (!Requests.ContainsKey(id)) return;
                    var world = Worlds.Server;
                    if (world == null || !world.IsCreated || !world.EntityManager.Exists(entity)) return;
                    if (AiRuntime.FindBuff(world.EntityManager, entity, Guid) != Entity.Null) return;
                    if (!TryPrepare(world, out _)) return;
                    Plugin.Logger?.LogWarning("Units: hide-health-bar buff missing on " + id + " " + d + " s after it was set; applying again.");
                    AiRuntime.ApplyBuff(world, entity, Guid);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Units: hide-health-bar re-check threw: " + e.Message);
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
        if (!_installed) throw new InvalidOperationException(op + " is server-only and the hide-health-bar buff is not installed in this process.");
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) throw new InvalidOperationException(op + " is server-only and there is no server world.");
        var entity = handle.Entity.ToEntity();
        if (!world.EntityManager.Exists(entity)) throw new InvalidOperationException("unit " + handle.Entity + " no longer exists.");
        return (world, entity);
    }
}
#endif
