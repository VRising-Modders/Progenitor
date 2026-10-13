#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Commands;
using Progenitor.Persistence;
using Progenitor.Plugins;

namespace Progenitor.Debugging;

/// <summary>
/// Core's built-in admin debug tools: the debug buff (<see cref="DebugBuff"/>), its map powers
/// (<see cref="DebugMap"/>) and the <c>.debug</c> command (<see cref="DebugCommands"/>). Installed
/// and uninstalled with Core; nothing is a plugin, so nothing here depends on the Sample.
///
/// <para><b>Lifetime.</b> Core is only uninstalled by a Core swap or at process exit, and both
/// bring the world back with Core, so <see cref="Uninstall"/> runs the debug buff's unload with
/// <see cref="PluginUnloadReason.Swap"/>: the buff entity and the grant ledger are kept, while
/// <c>Immortal</c> and the kill-path guard it granted are cleared on every unload, as before.
/// The next generation's world-ready reconcile re-arms both from the ledger.</para>
///
/// <para><b>No server-wide switch.</b> Every command is <c>adminOnly</c>; a player cannot reach
/// any of it. There is no <c>[Debug] Enabled</c> key.</para>
///
/// <para><b>Build mode</b> (<see cref="DebugBuildRuntime"/>) is the one server-wide effect: the
/// game's own build debug settings for every player. A Core swap keeps it; a world load restores
/// what it changed.</para>
/// </summary>
internal static class DebugTools
{
    private sealed class CoreOwner
    {
        public override string ToString() => "Progenitor debug tools";
    }

    /// <summary>Owner of every registration this module makes (definition, systems, events, keybind, commands).</summary>
    internal static readonly object Owner = new CoreOwner();

    /// <summary>
    /// ModSave store id for the grant ledger: <c>progenitor/progenitor.debug.json</c> in the world's
    /// save folder. Neutral on purpose, like <see cref="DebugBuff.Id"/>.
    /// </summary>
    internal const string StoreId = "progenitor.debug";

    private static readonly List<IDisposable> Registrations = new();
    private static bool _installed;

    /// <summary>The grant ledger's store. A no-op store on a client.</summary>
    internal static IModStore Store => ModSave.Store(StoreId);

    /// <summary>
    /// True when the grant ledger is backed by the world save folder, resolving the folder first
    /// if it is not known yet (a Core swap into a running world gets no <c>WorldReady</c>). A
    /// pending store reads empty, and a read-modify-write on it would overwrite the ledger on
    /// disk when it merges, so the reconcile and <c>.debug on</c> wait for this.
    /// </summary>
    internal static bool StoreReady
    {
        get
        {
            if (!Plugin.IsServerProcess) return false;
            ModSave.TryResolveDirectoryNow();
            return Store is ModStore store && store.IsReady;
        }
    }

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            DebugBuff.Register(Registrations);
            DebugMap.Attach(Registrations);
            if (Plugin.IsServerProcess)
            {
                // .debug build: no-op on a client; on the server it watches world loads to restore a saved build mode.
                DebugBuildRuntime.Install(Registrations);
                Registrations.Add(CommandRegistry.RegisterType(typeof(DebugCommands), Owner, CommandRoutingSettings.BuiltInOwnerId));
                Registrations.Add(CommandRegistry.RegisterType(typeof(DebugBuildCommands), Owner, CommandRoutingSettings.BuiltInOwnerId));
            }
            Plugin.Logger?.LogInfo("Debug: installed (" + (Plugin.IsServerProcess ? "server: .debug, buff " : "client: buff ")
                + DebugBuff.Id + ", map powers).");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Debug: install threw; debug tools are off for this generation: " + e);
        }
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        if (Plugin.IsServerProcess)
        {
            try { DebugBuff.Reset(PluginUnloadReason.Swap); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Debug: unload clear threw: " + e.Message); }
        }

        for (int i = Registrations.Count - 1; i >= 0; i--)
        {
            try { Registrations[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Debug: unregister threw: " + e.Message); }
        }
        Registrations.Clear();
    }
}
#endif
