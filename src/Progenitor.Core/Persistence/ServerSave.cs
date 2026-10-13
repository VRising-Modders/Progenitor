using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Progenitor.Events;
using ProjectM;
using Stunlock.Core;

namespace Progenitor.Persistence;

/// <summary>
/// Per-mod JSON that belongs to the <b>server</b> and survives wipes and world restores: season
/// stats, a hall of fame, bans, rewards carried across a wipe. The sibling of <see cref="ModSave"/>,
/// which belongs to one world.
///
/// <para><b>Where.</b> <c>&lt;persistentDataPath&gt;\progenitor-server\&lt;modGuid&gt;.json</c>,
/// beside the game's <c>Saves\</c> folder and never inside a world folder. The root is the
/// server's <c>-persistentDataPath</c> argument, else the game's own
/// <c>StunApplication.persistentDataPath</c>, else the root above the world save folder
/// <see cref="ModSave"/> resolved.</para>
///
/// <para><b>What does not touch it.</b> The game's scheduled wipe archives the world folder
/// (<c>Saves\v4\&lt;save&gt;</c> to <c>&lt;save&gt;_&lt;ticks&gt;</c>) and starts a new one, and
/// a Progenitor world restore replaces the world folder. Both act on the world folder only, so a
/// server store keeps its keys through both. That is the point of the store, and also its
/// responsibility: a value that must go back in time with a restored world belongs in
/// <see cref="ModSave"/>.</para>
///
/// <para><b>When it is written.</b> On the same triggers as <see cref="ModSave"/> (the game's
/// save trigger, a player disconnect, Core unload; <see cref="ModSave.SaveAll"/> flushes both), and
/// whenever <see cref="SaveAll"/> is called. Only stores that changed are written; each write goes
/// to <c>.tmp</c> then moves over the file.</para>
///
/// <para><b>Sides.</b> Server process only, hosted private games included (the game's bundled
/// <c>VRisingServer.exe</c> is a server process): there the root is that server's persistent data
/// path, so the store is shared by every private game hosted on that machine. In a client process
/// <see cref="Store"/> returns the same silent no-op store <see cref="ModSave"/> does. A player's
/// own preferences belong in the plugin's client config, not here.</para>
/// </summary>
public static class ServerSave
{
    private static readonly ServerStoreSet Stores = new();
    private static IDisposable? _worldReady;
    private static bool _installed;
    private static bool _missingWarned;

    /// <summary>
    /// The <c>progenitor-server</c> folder, or null while the persistent data root is unknown (and
    /// always on a client). Diagnostic: mods should not build paths from it themselves.
    /// </summary>
    public static string? CurrentDirectory => Stores.Folder;

    /// <summary>
    /// The server-level store for <paramref name="modGuid"/>, loaded from
    /// <c>progenitor-server\{modGuid}.json</c> when that file exists. Pending, with writes held in
    /// memory and merged when the root is known, in the rare case it is not known yet. A client
    /// process gets a silent no-op store, so shared plugin code needs no side check.
    /// </summary>
    public static IModStore Store(string modGuid)
    {
        if (!Plugin.IsServerProcess) return NoOpStore.Instance;
        if (Stores.Root == null) TryResolveRoot();
        return Stores.Store(modGuid);
    }

    /// <summary>Write every server store that changed. A no-op on a client.</summary>
    public static void SaveAll()
    {
        if (!Plugin.IsServerProcess) return;
        try
        {
            if (Stores.Root == null) TryResolveRoot();
            int wrote = Stores.SaveAll();
            if (wrote > 0)
                Plugin.Logger?.LogInfo($"ServerSave: wrote {wrote} store(s) under {Stores.Folder}");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ServerSave.SaveAll threw: {e}");
        }
    }

    /// <summary>
    /// Drop the <see cref="IModStore.WhenReady"/> callbacks this plugin registered on a server
    /// store. Same contract as <see cref="ModSave.RemoveAll"/>: matched on the identity of the
    /// owner's assembly and of every assembly in <paramref name="owned"/>; the reloader calls it on
    /// every unload. Returns how many were dropped.
    /// </summary>
    public static int RemoveAll(object owner, IReadOnlyList<Assembly>? owned = null)
    {
        if (owner == null) return 0;
        var names = new List<string>();
        var ids = ModStore.OwnerIdsFor(owner, owned, names);
        if (ids.Count == 0) return 0;
        int dropped = Stores.DropReadyCallbacks(ids);
        if (dropped > 0)
            Plugin.Logger?.LogInfo($"ServerSave: dropped {dropped} WhenReady callback(s) written in {string.Join(", ", names)}; that plugin is unloading before its store became real.");
        return dropped;
    }

    /// <summary>
    /// Server: resolve the root now and retry on <see cref="WorldReady"/> if it is not known yet.
    /// Called from <see cref="ModSave.Install"/>, so it shares that module's skip switch. Client:
    /// no-op. Safe to call twice.
    /// </summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        if (!Plugin.IsServerProcess) return;
        TryResolveRoot();
        if (Stores.Root == null)
            _worldReady = EventBus.On<WorldReady>(OnWorldReady, priority: 100);
    }

    /// <summary>Flush, drop every pending callback (they belong to this Core generation or its plugins), unsubscribe.</summary>
    internal static void Uninstall()
    {
        if (!_installed) return;
        try { SaveAll(); }
        catch (Exception e) { Plugin.Logger?.LogError($"ServerSave: flush on Uninstall threw: {e}"); }
        try
        {
            int dropped = Stores.DropReadyCallbacks(null);
            if (dropped > 0)
                Plugin.Logger?.LogInfo($"ServerSave: dropped {dropped} WhenReady callback(s) on uninstall; they belonged to this Core generation.");
        }
        catch (Exception e) { Plugin.Logger?.LogError($"ServerSave: dropping WhenReady callbacks on Uninstall threw: {e}"); }
        _worldReady?.Dispose();
        _worldReady = null;
        _installed = false;
    }

    /// <summary>What this module holds that can outlive a Core generation, for the pins line.</summary>
    internal static string DescribeHolds()
    {
        var holds = new List<string>();
        if (_worldReady != null) holds.Add("EventBus.WorldReady(ServerSave)");
        int count = Stores.Count;
        if (count > 0) holds.Add("serverStores=" + count);
        return string.Join(",", holds);
    }

    private static void OnWorldReady(WorldReady ev)
    {
        if (!ev.IsServer) return;
        try
        {
            if (TryResolveRoot())
            {
                _worldReady?.Dispose();
                _worldReady = null;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ServerSave: WorldReady handler threw: {e}");
        }
    }

    /// <summary>
    /// Resolve and set the root: <c>-persistentDataPath</c>, then the game's value, then the root
    /// above <see cref="ModSave.CurrentSaveDirectory"/>. False while none of them answers.
    /// </summary>
    private static bool TryResolveRoot()
    {
        if (Stores.Root != null) return true;
        string? root = FromCommandLine() ?? FromGame() ?? ServerSavePaths.RootFromSaveFolder(ModSave.CurrentSaveDirectory);
        if (!string.IsNullOrEmpty(root))
        {
            Stores.SetRoot(root!);
            return Stores.Root != null;
        }

        if (!_missingWarned)
        {
            _missingWarned = true;
            Plugin.Logger?.LogWarning(
                "ServerSave: the persistent data root is not known yet (no -persistentDataPath, no game value, no world save folder). Server stores stay in memory until it is.");
        }
        return false;
    }

    private static string? FromCommandLine()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string? value = null;
                if (arg.Equals("-persistentDataPath", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    value = args[i + 1];
                else if (arg.StartsWith("-persistentDataPath=", StringComparison.OrdinalIgnoreCase))
                    value = arg.Substring("-persistentDataPath=".Length);
                if (!string.IsNullOrWhiteSpace(value)) return Path.GetFullPath(value!.Trim());
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"ServerSave: reading -persistentDataPath threw: {e.Message}");
        }
        return null;
    }

    private static string? FromGame()
    {
        try
        {
            string path = StunApplication.persistentDataPath;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"ServerSave: StunApplication.persistentDataPath threw: {e.Message}");
            return null;
        }
    }
}
