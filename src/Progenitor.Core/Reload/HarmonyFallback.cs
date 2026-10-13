using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ProjectM;
using Unity.Entities;

namespace Progenitor.Reload;

/// <summary>
/// The only Core file that may mention HarmonyLib types. Collectible production
/// (Bootstrap owns the patches) never creates a Harmony instance: modules fill
/// Bootstrap slots through <see cref="HarmonySlotHandoff"/>. This type exists so a
/// Core dumped without Bootstrap, or a site whose Bootstrap patch failed, can still
/// trampoline — and so that path is obviously the one that will not collect.
/// </summary>
internal static class HarmonyFallback
{
    private static Harmony? _harmony;
    private static MethodInfo? _managedUpdate;
    private static MethodInfo? _unmanagedUpdate;

    internal static Harmony? Instance => _harmony;

    internal static Harmony Ensure() =>
        _harmony ??= new Harmony(MyPluginInfo.PLUGIN_GUID);

    /// <summary>
    /// Tick postfix when Bootstrap is absent. Gated on skip? No: tick is not a content
    /// site; SkipCoreHarmony + no Bootstrap still needs a frame pump.
    /// </summary>
    internal static void PatchTick(bool isServer)
    {
        // Already Bootstrap-absent-only by construction (Plugin.cs calls this under `if
        // (!bootstrap)`), so job FHARM7 leaves the patch alone and only makes the pin sayable.
        Plugin.Logger?.LogWarning(Progenitor.Patches.CorePatchFallbackPolicy.FormatDirect(
            Progenitor.Patches.CorePatchFallbackPolicy.ModuleSystemHooks,
            isServer ? "ServerBootstrapSystem.OnUpdate (tick)" : "ClientBootstrapSystem.OnUpdate (tick)",
            Progenitor.WorldState.WorldEpoch.CoreGenerationOrZero()));
        try
        {
            Ensure().PatchAll(isServer ? typeof(ServerTick) : typeof(ClientTick));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"HarmonyFallback: tick PatchAll threw: {e}");
        }
    }

    /// <summary>
    /// Install every Core module. Harmony is created only when at least one content site
    /// still needs a Core fallback patch — not when <c>HostOwnsPatches</c> is globally true
    /// while some other site is unowned (R14 leftover). SkipCoreModules is honored inside
    /// each module's Install.
    /// </summary>
    internal static void InstallModules()
    {
        Harmony? harmony = null;
        if (HarmonySlotHandoff.CoreNeedsFallbackHarmony())
            harmony = Ensure();
        Events.EventHooks.Install(harmony);
        Hooks.SystemHooks.Install(harmony);
        Commands.CommandSystem.Install(harmony);
        Content.PrefabRegistry.Install(harmony);
        Progenitor.Config.ConfigHelper.Install(harmony);
        Persistence.ModSave.Install(harmony);
        Hosted.HostedServerLauncher.Install(harmony);
        Localization.Localization.Install();
        Systems.VirtualSystems.Install();
        Chat.Chat.Install();
        Castles.CastleReader.Install();
        Players.PlayerControl.Install();
        Ecs.ComponentIndex.Install();
    }

    internal static void UninstallSystemHooks()
    {
        try { Hooks.SystemHooks.Uninstall(_harmony); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"SystemHooks.Uninstall: {e.Message}"); }
    }

    internal static void UnpatchCore()
    {
        try { _harmony?.UnpatchSelf(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Core UnpatchSelf: {e.Message}"); }
        try { Harmony.UnpatchID(MyPluginInfo.PLUGIN_GUID); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Core UnpatchID('{MyPluginInfo.PLUGIN_GUID}'): {e.Message}"); }
        UnpatchSystemUpdates();
        _harmony = null;
        LogHarmonyLeftovers();
    }

    internal static void DiagnosePins()
    {
        int n = 0;
        try { n = CountCoreHarmonyPatches(logEach: true); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("pin-diag Harmony: " + e.Message);
            return;
        }
        Plugin.Logger?.LogInfo("pin-diag Harmony core-id patches=" + n);
    }

    /// <summary>
    /// Job FHARM7: the direct fallback for one of the two system-update seams, allowed only when
    /// <c>Progenitor.Bootstrap</c> is absent. Both seams are a prefix/postfix <b>pair</b> joined
    /// by Harmony's <c>ref bool __state</c> — the prefix pushes a call frame and the postfix pops
    /// it only for the calls that pushed one — and a slot body carries no <c>__state</c>, so the
    /// pool cannot express them. With Bootstrap present the seam is refused instead of patched
    /// directly: it is on <c>SystemBase.Update</c> at <c>Priority.First</c>, the hottest method
    /// in the process, and pinning this Core generation there is precisely the leak
    /// <c>harmony-patches.md</c> section 8 measured. Refusal means system hooks are off for this
    /// generation; the fix is a Bootstrap that owns the site, then restart both.
    /// </summary>
    private static bool MayPatchDirectly(string site)
    {
        var route = Progenitor.Patches.CorePatchFallbackPolicy.Decide(
            slotShapeFits: false,
            bootstrapPresent: Progenitor.Patches.PatchRegistry.SlotsAvailable);
        if (route == Progenitor.Patches.CoreFallbackRoute.Direct)
        {
            Plugin.Logger?.LogWarning(Progenitor.Patches.CorePatchFallbackPolicy.FormatDirect(
                Progenitor.Patches.CorePatchFallbackPolicy.ModuleSystemHooks,
                site,
                Progenitor.WorldState.WorldEpoch.CoreGenerationOrZero()));
            return true;
        }
        Plugin.Logger?.LogError(Progenitor.Patches.CorePatchFallbackPolicy.FormatRefused(
            Progenitor.Patches.CorePatchFallbackPolicy.ModuleSystemHooks, site));
        return false;
    }

    internal static bool TryPatchManagedSystemUpdate()
    {
        if (!MayPatchDirectly(HarmonySlotHandoff.SystemBaseUpdate)) return false;
        try
        {
            var original = typeof(SystemBase).GetMethod(nameof(SystemBase.Update), BindingFlags.Instance | BindingFlags.Public);
            if (original == null)
            {
                Plugin.Logger?.LogError("SystemHooks: SystemBase.Update was not found on the interop type.");
                return false;
            }
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.FallbackSiteLog(HarmonySlotHandoff.SystemBaseUpdate));
            Ensure().Patch(
                original,
                prefix: new HarmonyMethod(typeof(Hooks.SystemHooks), nameof(Hooks.SystemHooks.PrefixManaged)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(Hooks.SystemHooks), nameof(Hooks.SystemHooks.PostfixManaged)));
            _managedUpdate = original;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"SystemHooks: patch SystemBase.Update threw: {e}");
            return false;
        }
    }

    internal static bool TryPatchUnmanagedSystemUpdate()
    {
        if (!MayPatchDirectly(HarmonySlotHandoff.UnmanagedUpdate)) return false;
        try
        {
            var original = FindUnmanagedUpdate();
            if (original == null)
            {
                Plugin.Logger?.LogError("SystemHooks: WorldUnmanagedImpl.UnmanagedUpdate(void*) was not found on the interop type.");
                return false;
            }
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.FallbackSiteLog(HarmonySlotHandoff.UnmanagedUpdate));
            Ensure().Patch(
                original,
                prefix: new HarmonyMethod(typeof(Hooks.SystemHooks), nameof(Hooks.SystemHooks.PrefixUnmanaged)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(Hooks.SystemHooks), nameof(Hooks.SystemHooks.PostfixUnmanaged)));
            _unmanagedUpdate = original;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"SystemHooks: patch UnmanagedUpdate threw: {e}");
            return false;
        }
    }

    internal static void UnpatchSystemUpdates()
    {
        var harmony = _harmony;
        TryUnpatch(harmony, _managedUpdate);
        TryUnpatch(harmony, _unmanagedUpdate);
        _managedUpdate = null;
        _unmanagedUpdate = null;
    }

    /// <summary>
    /// <see cref="Harmony.UnpatchID"/> is exact-id. Dev patches as <c>GUID.chat_observe</c>
    /// / <c>GUID.extra_auth_server</c> / <c>GUID.host_capture</c>; those must go too or they
    /// pin both the plugin ALC and Core.
    /// </summary>
    internal static void UnpatchOwnerAndSuffixes(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return;
        var ids = new HashSet<string>(StringComparer.Ordinal) { guid };
        try
        {
            foreach (var method in Harmony.GetAllPatchedMethods())
            {
                var info = Harmony.GetPatchInfo(method);
                if (info == null) continue;
                Collect(info.Prefixes);
                Collect(info.Postfixes);
                Collect(info.Transpilers);
                Collect(info.Finalizers);
            }

            void Collect(IEnumerable<Patch>? patches)
            {
                if (patches == null) return;
                foreach (var p in patches)
                {
                    var owner = p.owner;
                    if (string.IsNullOrEmpty(owner)) continue;
                    if (owner == guid || owner.StartsWith(guid + ".", StringComparison.Ordinal))
                        ids.Add(owner);
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Reload: listing Harmony ids for '{guid}': {e.Message}");
        }

        foreach (var id in ids)
        {
            try { Harmony.UnpatchID(id); }
            catch (Exception e) { Plugin.Logger?.LogWarning($"Reload: Harmony.UnpatchID('{id}'): {e.Message}"); }
        }
    }

    private static MethodInfo? FindUnmanagedUpdate()
    {
        var type = typeof(WorldUnmanagedImpl);
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (method.Name != nameof(WorldUnmanagedImpl.UnmanagedUpdate)) continue;
            var args = method.GetParameters();
            if (args.Length == 1 && args[0].ParameterType == typeof(void*)) return method;
        }
        return type.GetMethod(nameof(WorldUnmanagedImpl.UnmanagedUpdate), BindingFlags.Public | BindingFlags.Static);
    }

    private static void TryUnpatch(Harmony? harmony, MethodInfo? original)
    {
        if (harmony == null || original == null) return;
        try
        {
            harmony.Unpatch(original, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(original, HarmonyPatchType.Postfix, harmony.Id);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"SystemHooks: unpatch {original.DeclaringType?.Name}.{original.Name} threw: {e}");
        }
    }

    private static void LogHarmonyLeftovers()
    {
        int leftover = 0;
        try { leftover = CountCoreHarmonyPatches(logEach: false); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Harmony leftover scan: " + e.Message);
            return;
        }
        if (leftover == 0)
            Plugin.Logger?.LogInfo("Harmony leftovers after UnpatchID(" + MyPluginInfo.PLUGIN_GUID + ")=0");
        else
            Plugin.Logger?.LogWarning("Harmony leftovers after UnpatchID(" + MyPluginInfo.PLUGIN_GUID + ")=" + leftover);
    }

    private static int CountCoreHarmonyPatches(bool logEach)
    {
        int n = 0;
        string guid = MyPluginInfo.PLUGIN_GUID;
        foreach (var method in Harmony.GetAllPatchedMethods())
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null) continue;
            n += Count(info.Prefixes);
            n += Count(info.Postfixes);
            n += Count(info.Transpilers);
            n += Count(info.Finalizers);

            int Count(IEnumerable<Patch>? patches)
            {
                if (patches == null) return 0;
                int c = 0;
                foreach (var p in patches)
                {
                    if (p == null) continue;
                    if (!IsCoreHarmonyOwner(p.owner, guid)) continue;
                    c++;
                    if (logEach)
                    {
                        Plugin.Logger?.LogInfo(
                            "  Harmony owner=" + (p.owner ?? "")
                            + " target=" + method.DeclaringType?.Name + "." + method.Name
                            + " patch=" + p.PatchMethod?.DeclaringType?.Name + "." + p.PatchMethod?.Name);
                    }
                }
                return c;
            }
        }
        return n;
    }

    /// <summary>
    /// Core's Harmony id is <c>progenitor</c>; Bootstrap's is
    /// <c>progenitor.bootstrap</c>. A naive StartsWith(guid+".") would treat
    /// Bootstrap patches as Core leftovers.
    /// </summary>
    private static bool IsCoreHarmonyOwner(string? owner, string guid)
    {
        if (string.IsNullOrEmpty(owner) || string.IsNullOrEmpty(guid)) return false;
        if (string.Equals(owner, "progenitor.bootstrap", StringComparison.Ordinal)) return false;
        if (string.Equals(owner, guid, StringComparison.Ordinal)) return true;
        return owner.StartsWith(guid + ".", StringComparison.Ordinal)
            && !owner.StartsWith("progenitor.bootstrap", StringComparison.Ordinal);
    }

    private static class ServerTick
    {
        [HarmonyPatch(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.OnUpdate))]
        [HarmonyPostfix]
        private static void Postfix() => Plugin.FrameTick();
    }

    private static class ClientTick
    {
        [HarmonyPatch(typeof(ClientBootstrapSystem), nameof(ClientBootstrapSystem.OnUpdate))]
        [HarmonyPostfix]
        private static void Postfix() => Plugin.FrameTick();
    }
}
