using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using ProjectM;
using Progenitor.Config;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Reload;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Persistence;

/// <summary>
/// Per-mod JSON that lives and dies with the world save. The game writes
/// <c>AutoSave_N.save.gz</c> under <c>{SaveDirectoryPath}/{SaveToLoad}/</c> (on this build,
/// <c>&lt;persistentDataPath&gt;\Saves\v4\&lt;saveName&gt;</c>). Mod files sit beside those
/// gzip files in a <c>progenitor</c> subfolder so a copied save takes its mod data with it.
///
/// Load is on <see cref="WorldReady"/> (server). Save is a Harmony postfix on
/// <see cref="TriggerPersistenceSaveSystem.TriggerSave"/> — that is the one-shot entry
/// (autosave and admin/manual; <c>OnUpdate</c> runs every frame and is the wrong seam) —
/// plus a cheap flush on <see cref="PlayerDisconnected"/>. Writes are to <c>.tmp</c> then
/// move, so a crash mid-write cannot leave a half JSON as the live file.
///
/// Server only. The client returns a no-op store; nothing is read or written there.
/// </summary>
public static unsafe class ModSave
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ModStore> Stores = new(StringComparer.OrdinalIgnoreCase);

    private static Harmony? _harmony;
    private static IDisposable? _worldReady;
    private static IDisposable? _disconnected;
    private static ConfigEntry<string>? _directoryFallback;
    private static string? _currentSaveDirectory;
    private static bool _fallbackWarned;
    private static bool _missingWarned;
    private static bool _singletonFailed;
    private static bool _installed;

    /// <summary>
    /// Folder that contains the game's <c>AutoSave_N.save.gz</c> files, or null when it has
    /// not been resolved yet. Diagnostic: mods should not build paths from this themselves.
    /// </summary>
    public static string? CurrentSaveDirectory
    {
        get
        {
            lock (Gate) return _currentSaveDirectory;
        }
    }

    /// <summary>
    /// The in-memory store for <paramref name="modGuid"/>. Loaded from
    /// <c>{CurrentSaveDirectory}/progenitor/{modGuid}.json</c> when that file exists.
    /// If the save folder is not known yet the store is pending: writes stay in memory and
    /// the disk file is merged (disk base, dirty keys win) once the folder is resolved.
    /// On the client this is a silent no-op so shared plugin code does not need a side check.
    /// </summary>
    public static IModStore Store(string modGuid)
    {
        if (!Plugin.IsServerProcess) return NoOpStore.Instance;
        var guid = SanitizeGuid(modGuid);
        lock (Gate)
        {
            if (Stores.TryGetValue(guid, out var existing)) return existing;
            var path = PathFor(guid, _currentSaveDirectory);
            var store = new ModStore(guid, path);
            // Pending (no directory yet): do not invent a %TEMP% path and do not touch disk.
            // Loaded: read the real JSON now so OnLoad sees persisted keys.
            if (!store.IsPending)
                store.LoadFromDisk();
            Stores[guid] = store;
            return store;
        }
    }

    /// <summary>
    /// Core-internal: resolve the world save folder now when it is not known yet, so pending
    /// stores are merged with disk without writing anything. A Core generation installed into a
    /// running world (a Core swap) sees no <c>WorldReady</c>, and its stores would otherwise stay
    /// pending until the next save. False on a client, before install, or while the folder still
    /// cannot be determined.
    /// </summary>
    internal static bool TryResolveDirectoryNow()
    {
        if (!_installed || !Plugin.IsServerProcess) return false;
        try { return TryEnsureDirectory(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"ModSave: resolving the save folder threw: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Core-internal: every store in the world's store folder that holds <paramref name="key"/>,
    /// loaded from disk (a store already in memory is reused). Empty on a client or while the save
    /// folder is unknown. Used to find a ledger another component (the Sample) wrote under its own
    /// id, without naming that id.
    /// </summary>
    internal static List<IModStore> FindStoresHolding(string key, string? excludeGuid = null)
    {
        var found = new List<IModStore>();
        if (string.IsNullOrEmpty(key) || !TryResolveDirectoryNow()) return found;
        string? dir;
        lock (Gate) dir = _currentSaveDirectory;
        if (string.IsNullOrEmpty(dir)) return found;
        string folder = SavePaths.StoreFolder(dir);
        string[] files;
        try { files = Directory.Exists(folder) ? Directory.GetFiles(folder, "*.json") : Array.Empty<string>(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"ModSave: listing '{folder}' threw: {e.Message}");
            return found;
        }
        foreach (var file in files)
        {
            if (SavePaths.IsMergedArchive(file)) continue;
            string guid = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrEmpty(guid)) continue;
            if (excludeGuid != null && string.Equals(guid, excludeGuid, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var store = Store(guid);
                foreach (var k in store.Keys)
                {
                    if (!string.Equals(k, key, StringComparison.Ordinal)) continue;
                    found.Add(store);
                    break;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"ModSave: reading store '{guid}' threw: {e.Message}");
            }
        }
        return found;
    }

    /// <summary>
    /// Write every in-memory store to disk. Called from the game's save trigger and from a
    /// disconnect flush; exposed so a diagnostic command can force a write without waiting.
    /// Also flushes the server-level stores (<see cref="ServerSave.SaveAll"/>), which share these
    /// triggers and do not depend on the world save folder being known.
    /// </summary>
    public static void SaveAll()
    {
        if (!Plugin.IsServerProcess) return;
        ServerSave.SaveAll();
        try
        {
            if (!TryEnsureDirectory()) return;
            ModStore[] snapshot;
            lock (Gate)
            {
                RelocateStores_NoLock();
                snapshot = new ModStore[Stores.Count];
                Stores.Values.CopyTo(snapshot, 0);
            }
            int wrote = 0;
            foreach (var store in snapshot)
            {
                if (store.SaveToDisk()) wrote++;
            }
            if (wrote > 0)
                Plugin.Logger?.LogInfo($"ModSave: wrote {wrote} store(s) under {CurrentSaveDirectory}");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ModSave.SaveAll threw: {e}");
        }
    }

    /// <summary>
    /// Server: subscribe to world-ready and disconnect, patch the save trigger, bind the
    /// directory fallback. Client: no-op. Safe to call twice.
    /// </summary>
    public static void Install(Harmony? harmony)
    {
        if (_installed) return;
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.PersistenceModule))
        {
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(HarmonySlotHandoff.PersistenceModule));
            return;
        }

        _installed = true;
        _harmony = harmony;

        if (!Plugin.IsServerProcess)
        {
            Plugin.Logger?.LogInfo("ModSave: client process, persistence is a no-op.");
            return;
        }

        BindFallback();
        ServerSave.Install();
        _worldReady = EventBus.On<WorldReady>(OnWorldReady, priority: 100);
        _disconnected = EventBus.On<PlayerDisconnected>(OnPlayerDisconnected, priority: 0);

        if (HarmonySlotHandoff.SkipCoreHarmony)
        {
            // Skip log already emitted from Plugin.Install.
        }
        else
        {
            BindSaveSites(harmony);
        }

        Plugin.Logger?.LogInfo("ModSave: installed (load on WorldReady, save on TriggerSave + disconnect).");
    }

    /// <summary>
    /// What this module registered that can outlive a collectible Core generation
    /// (EventBus, Harmony fallback, in-memory stores).
    /// </summary>
    internal static string DescribePins()
    {
        var holds = new List<string>();
        try
        {
            if (_installed) holds.Add("installed");
            if (_worldReady != null) holds.Add("EventBus.WorldReady");
            if (_disconnected != null) holds.Add("EventBus.PlayerDisconnected");
            if (_harmony != null) holds.Add("HarmonyFallback");
            lock (Gate)
            {
                if (Stores.Count > 0) holds.Add("stores=" + Stores.Count);
            }
            string server = ServerSave.DescribeHolds();
            if (server.Length > 0) holds.Add(server);
        }
        catch (Exception e)
        {
            holds.Add("threw:" + e.Message);
        }
        return HarmonySlotHandoff.FormatModulePins(HarmonySlotHandoff.PersistenceModule, holds);
    }

    /// <summary>
    /// Drop the <c>WhenReady</c> callbacks this plugin registered on any store. Same shape as
    /// <c>EventBus.RemoveAll</c> / <c>Units.RemoveAll</c>: match on the owner's assembly, not on
    /// reference equality, because a plugin can register from a helper object that pins the same
    /// collectible context. Call it from a plugin's <c>Unload</c>, and from the reloader's sweep,
    /// so a callback held while a store is still pending cannot run after the ALC is gone.
    /// Returns how many were dropped.
    ///
    /// <para><paramref name="owned"/> is the plugin assembly <b>plus the rewritten dependency
    /// assemblies loaded into its collectible context</b>, the list the reloader's
    /// <c>SweepRegistrations</c> already holds (job FEX8, R59 finding 10). Without it this matched
    /// one simple name, so a callback declared in a helper library the plugin ships read as owner
    /// <c>Shared</c>, did not match, stayed pending and went on holding a delegate whose method
    /// lives in the ALC the sweep is emptying — which is the pin the sweep's call site says this
    /// line exists to remove. Null falls back to the owner's own assembly, which is the right
    /// answer for a plugin calling this from its own <c>Unload</c>.</para>
    ///
    /// <para><b>Matched by assembly identity, not by simple name</b> (job FEX9, R60 finding 3).
    /// Widening the set to the rewritten dependencies made the simple name a collision surface —
    /// the canonical vendored helper is <c>Shared.dll</c>, and two plugins that each ship one
    /// canceled each other's pending callbacks, silently, losing the read-modify-write
    /// <c>docs/modules.md</c> tells a mod to do inside <c>WhenReady</c> precisely because doing it
    /// anywhere else corrupts the file. <c>ModStore.OwnerIdOf</c> is load context + module MVID +
    /// simple name, all value data, so nothing here holds a live <c>Assembly</c> past the call:
    /// the same precision the <c>Units</c> sweep gets from an <c>Assembly</c> reference, without
    /// the reference a pending registration must not keep.</para>
    /// </summary>
    public static int RemoveAll(object owner, IReadOnlyList<Assembly>? owned = null)
    {
        if (owner == null) return 0;
        var names = new List<string>();
        var ids = ModStore.OwnerIdsFor(owner, owned, names);
        if (ids.Count == 0) return 0;

        int dropped = DropReadyCallbacks(ids);
        if (dropped > 0)
            Plugin.Logger?.LogInfo($"ModSave: dropped {dropped} WhenReady callback(s) written in {string.Join(", ", names)}; that plugin is unloading before its store became real.");
        return dropped;
    }

    /// <summary>
    /// Drop matching ready callbacks across every store. Entries are <c>ModStore.OwnerIdOf</c>
    /// identities (job FEX9); null means all of them.
    /// </summary>
    private static int DropReadyCallbacks(IReadOnlyCollection<string>? ownerIds)
    {
        ModStore[] snapshot;
        lock (Gate)
        {
            snapshot = new ModStore[Stores.Count];
            Stores.Values.CopyTo(snapshot, 0);
        }

        int dropped = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            try { dropped += snapshot[i].DropReadyCallbacks(ownerIds); }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"ModSave: dropping WhenReady callbacks on '{snapshot[i].ModGuid}' threw: {e}");
            }
        }
        return dropped;
    }

    /// <summary>Flush once, drop hooks and patches, keep in-memory stores (the save still exists).</summary>
    public static void Uninstall()
    {
        if (!_installed) return;
        try { if (Plugin.IsServerProcess) SaveAll(); }
        catch (Exception e) { Plugin.Logger?.LogError($"ModSave: flush on Uninstall threw: {e}"); }

        // The stores survive an uninstall (they belong to the save); the callbacks do not. Every
        // one of them names a method in a Core generation or a plugin that is going away, so a
        // later merge must not find them still waiting.
        try
        {
            int dropped = DropReadyCallbacks(null);
            if (dropped > 0)
                Plugin.Logger?.LogInfo($"ModSave: dropped {dropped} WhenReady callback(s) on uninstall; they belonged to this Core generation.");
        }
        catch (Exception e) { Plugin.Logger?.LogError($"ModSave: dropping WhenReady callbacks on Uninstall threw: {e}"); }

        _worldReady?.Dispose();
        _worldReady = null;
        _disconnected?.Dispose();
        _disconnected = null;

        // After the flush above (SaveAll already wrote the changed server stores): drops the
        // server stores' callbacks and their WorldReady retry.
        try { ServerSave.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogError($"ModSave: ServerSave.Uninstall threw: {e}"); }

        HarmonySlotHandoff.Clear(HarmonySlotHandoff.TriggerSave);
        HarmonySlotHandoff.Clear(HarmonySlotHandoff.BeginCreateServerWorld);
        if (_harmony != null)
        {
            TryUnpatch(typeof(TriggerPersistenceSaveSystem), nameof(TriggerPersistenceSaveSystem.TriggerSave));
            TryUnpatch(typeof(ServerWorldManager), nameof(ServerWorldManager.BeginCreateServerWorld));
        }

        _harmony = null;
        _directoryFallback = null;
        _installed = false;
    }

    // ---- events -------------------------------------------------------------------------------

    private static void OnWorldReady(WorldReady ev)
    {
        if (!ev.IsServer) return;
        try
        {
            TryCaptureFromWorld(ev.World);
            TryEnsureDirectory();
            LoadAllFromDisk();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ModSave: WorldReady handler threw: {e}");
        }
    }

    private static void OnPlayerDisconnected(PlayerDisconnected ev)
    {
        try { SaveAll(); }
        catch (Exception e) { Plugin.Logger?.LogError($"ModSave: PlayerDisconnected flush threw: {e}"); }
    }

    private static void LoadAllFromDisk()
    {
        string? dir;
        lock (Gate) dir = _currentSaveDirectory;
        if (string.IsNullOrEmpty(dir)) return;

        var folder = SavePaths.StoreFolder(dir);
        // A store written before the 2026-10 id rename is named after the plugin's old id: give it the current name
        // first, so the plugin finds its keys (the old file is kept as .bak).
        foreach (string line in Progenitor.Plugins.LegacyIds.MigrateFolder(folder, ".json"))
            Plugin.Logger?.LogInfo("ModSave: " + line);
        string[] files = Array.Empty<string>();
        if (Directory.Exists(folder))
        {
            try { files = Directory.GetFiles(folder, "*.json"); }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"ModSave: listing '{folder}' threw: {e}");
            }
        }

        int loaded = 0;
        lock (Gate)
        {
            RelocateStores_NoLock();
            foreach (var store in Stores.Values)
            {
                store.LoadFromDisk();
                loaded++;
            }
            foreach (var file in files)
            {
                if (SavePaths.IsMergedArchive(file)) continue;
                var guid = Path.GetFileNameWithoutExtension(file);
                if (string.IsNullOrEmpty(guid)) continue;
                if (Stores.ContainsKey(guid)) continue;
                var store = new ModStore(guid, file);
                Stores[guid] = store;
                store.LoadFromDisk();
                loaded++;
            }
        }
        Plugin.Logger?.LogInfo($"ModSave: loaded {loaded} store(s) from {folder}");
    }

    // ---- directory ----------------------------------------------------------------------------

    private static bool TryEnsureDirectory()
    {
        lock (Gate)
        {
            if (!string.IsNullOrEmpty(_currentSaveDirectory)) return true;
        }

        if (TryResolveDirectory(out var resolved, out var fromFallback))
        {
            SetDirectory(resolved, fromFallback);
            return true;
        }

        if (!_missingWarned)
        {
            _missingWarned = true;
            Plugin.Logger?.LogWarning(
                "ModSave: could not determine the world save folder (ServerRuntimeSettings.SaveDirectoryPath, " +
                "command line, and Persistence.Directory were all empty). Mod data will stay in memory until a path is known.");
        }
        return false;
    }

    private static void SetDirectory(string directory, bool fromFallback)
    {
        bool changed;
        lock (Gate)
        {
            changed = !string.Equals(_currentSaveDirectory, directory, StringComparison.OrdinalIgnoreCase);
            _currentSaveDirectory = directory;
            RelocateStores_NoLock();
        }
        if (!changed) return;
        if (fromFallback && !_fallbackWarned)
        {
            _fallbackWarned = true;
            Plugin.Logger?.LogWarning($"ModSave: save folder taken from Persistence.Directory ({directory}).");
        }
        else
        {
            Plugin.Logger?.LogInfo($"ModSave: save folder is {directory}");
        }
    }

    private static void RelocateStores_NoLock()
    {
        if (string.IsNullOrEmpty(_currentSaveDirectory)) return;
        foreach (var store in Stores.Values)
        {
            try
            {
                var next = PathFor(store.ModGuid, _currentSaveDirectory);
                var retargeted = store.Relocate(next);
                // Pending: directory just became known, merge disk before any save.
                // Retargeted: dest may already have JSON; Relocate merged old < dest < dirty
                // and archived the old file. LoadFromDisk applies that extra base.
                // Failed move (dest missing): FilePath is still the old populated file;
                // do not LoadFromDisk a path we did not populate.
                if (store.IsPending || retargeted)
                    store.LoadFromDisk();
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"ModSave: relocating '{store.ModGuid}' threw: {e}");
            }
        }
    }

    private static bool TryResolveDirectory(out string directory, out bool fromFallback)
    {
        directory = "";
        fromFallback = false;

        string? cached;
        lock (Gate) cached = _currentSaveDirectory;
        if (!string.IsNullOrEmpty(cached))
        {
            directory = cached;
            return true;
        }

        if (TryCaptureFromWorld(Worlds.Server))
        {
            lock (Gate) directory = _currentSaveDirectory ?? "";
            return !string.IsNullOrEmpty(directory);
        }

        var fromArgs = ResolveFromCommandLine();
        if (!string.IsNullOrEmpty(fromArgs))
        {
            directory = fromArgs;
            return true;
        }

        var fallback = _directoryFallback?.Value;
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            directory = fallback.Trim();
            fromFallback = true;
            return true;
        }

        return false;
    }

    private static string? ResolveFromCommandLine()
    {
        TryGetArg("-saveName", out var saveName);
        if (string.IsNullOrEmpty(saveName)) TryGetArg("-serverSaveName", out saveName);
        if (string.IsNullOrEmpty(saveName)) TryGetArg("-saveFileName", out saveName);

        TryGetArg("-persistentDataPath", out var persist);
        if (string.IsNullOrEmpty(persist))
        {
            try { persist = StunApplication.persistentDataPath; }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"ModSave: StunApplication.persistentDataPath threw: {e}");
            }
        }

        if (string.IsNullOrEmpty(persist) || string.IsNullOrEmpty(saveName)) return null;
        try { return SavePaths.FindSaveFolder(persist, saveName); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ModSave: scanning '{persist}' for save '{saveName}' threw: {e}");
            return null;
        }
    }

    private static bool TryGetArg(string name, out string value)
    {
        value = "";
        string[] args;
        try { args = Environment.GetCommandLineArgs(); }
        catch { return false; }
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length) return false;
                value = args[i + 1];
                return !string.IsNullOrEmpty(value);
            }
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg.Substring(name.Length + 1);
                return !string.IsNullOrEmpty(value);
            }
        }
        return false;
    }

    private static void CaptureSettings(ServerRuntimeSettings settings)
    {
        try
        {
            var dir = settings.SaveDirectoryPath.ToString();
            var name = ReadSaveToLoad(settings);
            CaptureFromPaths(dir, name);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ModSave: capturing ServerRuntimeSettings threw: {e}");
        }
    }

    /// <summary>
    /// Bootstrap extracts <c>SaveDirectoryPath</c> / <c>SaveToLoad</c> as strings so the
    /// <c>ServerRuntimeSettings</c> wrapper struct does not cross the Harmony slot.
    /// </summary>
    internal static void CaptureFromPaths(string? directory, string? saveToLoad)
    {
        try
        {
            var combined = TryGameCombinedPath(directory, saveToLoad) ?? SavePaths.CombineSaveFolder(directory, saveToLoad);
            if (string.IsNullOrEmpty(combined)) return;
            SetDirectory(combined, fromFallback: false);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ModSave: capturing save paths threw: {e}");
        }
    }

    /// <summary>
    /// The game's own combiner for <c>SaveDirectoryPath</c> + <c>SaveToLoad</c>. Preferred over
    /// reconstructing from <c>-saveName</c>, which can disagree with the singleton. Returns
    /// null when the call throws, returns empty, or would nest the save name a second time.
    /// </summary>
    private static string? TryGameCombinedPath(string? directory, string? saveName)
    {
        directory = directory?.Trim();
        saveName = saveName?.Trim();
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(saveName)) return null;
        directory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(directory), saveName, StringComparison.OrdinalIgnoreCase))
            return directory;
        try
        {
            var combined = ServerRuntimeSettings.GetCombinedFullSavePath(directory, saveName);
            if (string.IsNullOrWhiteSpace(combined)) return null;
            combined = combined.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Verified live: the game's combiner returns the save FILE (…\<save>\AutoSave_N.save.gz),
            // not the folder. Mod data lives beside the file, so take its directory.
            if (combined.EndsWith(".gz", StringComparison.OrdinalIgnoreCase)
                || combined.EndsWith(".save", StringComparison.OrdinalIgnoreCase)
                || File.Exists(combined))
            {
                var parent = Path.GetDirectoryName(combined);
                if (string.IsNullOrEmpty(parent)) return null;
                combined = parent;
            }
            return combined;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ModSave: GetCombinedFullSavePath threw: {e}");
            return null;
        }
    }

    private static string ReadSaveToLoad(ServerRuntimeSettings settings)
    {
        try
        {
            var optional = settings.SaveToLoad;
            if (!optional.HasValue) return "";
            return optional.Value.ToString() ?? "";
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ModSave: reading SaveToLoad threw: {e}");
            return "";
        }
    }

    private static bool TryCaptureFromWorld(World? world)
    {
        if (world == null || !world.IsCreated) return false;
        try
        {
            var em = world.EntityManager;
            var componentType = ComponentIndex.Of<ServerRuntimeSettings>(ComponentType.AccessMode.ReadOnly);
            if (!Singletons.TryGetEntity(em, componentType, out var entity)) return false;
            var index = ComponentIndex.IndexOf<ServerRuntimeSettings>();
            void* raw = em.GetComponentDataRawRO(entity, index);
            if (raw == null) return false;
            // Copy the explicit-layout blob. GetComponentData<ServerRuntimeSettings> is a generic
            // interop call over a game value type (not on the known-good list); the field offsets
            // match native, so a pointer copy gives SaveDirectoryPath and SaveToLoad the same way
            // BeginCreateServerWorld already passes the struct into CaptureSettings.
            var settings = *(ServerRuntimeSettings*)raw;
            CaptureSettings(settings);
            lock (Gate) return !string.IsNullOrEmpty(_currentSaveDirectory);
        }
        catch (Exception e)
        {
            if (!_singletonFailed)
            {
                _singletonFailed = true;
                Plugin.Logger?.LogError($"ModSave: ServerRuntimeSettings singleton read threw: {e}");
            }
            return false;
        }
    }

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>
    /// Bind now, or once the shared file resolves. This used to open a second
    /// <see cref="ConfigFile"/> on progenitor.cfg when the chainloader lookup missed,
    /// which it always did at process start: two live instances of one path, each able to write
    /// its own values back over the other's.
    /// </summary>
    private static void BindFallback() =>
        SharedConfig.BindWhenAvailable("[Persistence] Directory", BindFallbackFrom);

    private static void BindFallbackFrom(ConfigFile file)
    {
        try
        {
            _directoryFallback = ConfigHelper.Bind(
                file,
                "Persistence",
                "Directory",
                "",
                "Absolute path of the world save folder (the one that contains AutoSave_N.save.gz) used when the game's ServerRuntimeSettings.SaveDirectoryPath cannot be read. Leave empty to rely on game state.");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ModSave: binding Persistence.Directory threw: {e}");
        }
    }

    private static string SanitizeGuid(string modGuid) => SavePaths.SanitizeStoreId(modGuid);

    private static string PathFor(string guid, string? saveDirectory)
    {
        if (string.IsNullOrEmpty(saveDirectory)) return "";
        return SavePaths.StoreFile(saveDirectory, guid);
    }

    private static void TryPatchAll(Harmony harmony, Type hookType)
    {
        try
        {
            harmony.PatchAll(hookType);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ModSave: PatchAll({hookType.Name}) threw: {e}");
        }
    }

    private static void TryUnpatch(Type type, string method)
    {
        try
        {
            var original = AccessTools.Method(type, method);
            if (original == null || _harmony == null) return;
            _harmony.Unpatch(original, HarmonyPatchType.Postfix, _harmony.Id);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ModSave: unpatch {type.Name}.{method} threw: {e}");
        }
    }

    internal delegate void TriggerSave(bool result);
    internal delegate void BeginCreateServerWorld(string? saveDirectoryPath, string? saveToLoad);

    private static void BindSaveSites(Harmony? harmony)
    {
        ApplySite(
            HarmonySlotHandoff.TriggerSave,
            new TriggerSave(SlotTriggerSave),
            harmony,
            typeof(TriggerSaveHook),
            // TriggerSave's whole body is "if (!__result) return;" — the game declining the save
            // is the one thing it must not write through. A slot body gets __instance and __args
            // and no __result at all, so this site cannot be expressed through the pool.
            slotRegister: null);
        ApplySite(
            HarmonySlotHandoff.BeginCreateServerWorld,
            new BeginCreateServerWorld(CaptureFromPaths),
            harmony,
            typeof(BeginCreateHook),
            slotRegister: RegisterBeginCreateSlot);
    }

    /// <summary>Core's registry owner id; every Core slot registration shares it (job FHARM8).</summary>
    internal const string OwnerId = MyPluginInfo.PLUGIN_GUID + Progenitor.Patches.PatchSitePolicy.CoreOwnerSuffix;

    private static void ApplySite(string site, Delegate dispatcher, Harmony? harmony, Type hookType, Func<bool>? slotRegister)
    {
        switch (HarmonySlotHandoff.Bind(site, dispatcher))
        {
            case HarmonySlotHandoff.SiteBind.Filled:
                return;
            case HarmonySlotHandoff.SiteBind.Fallback:
                Plugin.Logger?.LogInfo(HarmonySlotHandoff.FallbackSiteLog(site));
                RouteFallback(site, harmony, hookType, slotRegister);
                return;
            case HarmonySlotHandoff.SiteBind.Inert:
                Plugin.Logger?.LogError(HarmonySlotHandoff.InertLog("ModSave", site));
                return;
            case HarmonySlotHandoff.SiteBind.Vanilla:
                Plugin.Logger?.LogError(HarmonySlotHandoff.VanillaLog("ModSave", site));
                return;
        }
    }

    /// <summary>
    /// Job FHARM8. Where one of ModSave's two fallbacks is allowed to go, when Bootstrap does not
    /// own the site. Before FHARM8 both took an unconditional <c>PatchAll</c>, handing HarmonyX a
    /// <c>Progenitor.Core</c> method and pinning this Core generation for the life of the process
    /// even after a clean unpatch (<c>harmony-patches.md</c> section 8).
    ///
    /// <para><paramref name="slotRegister"/> null <em>is</em> "the shape does not fit": the only
    /// site here that cannot be expressed as a slot is <c>TriggerSave</c>, whose body is a test of
    /// <c>__result</c>. With Bootstrap present that is
    /// <see cref="Progenitor.Patches.CoreFallbackRoute.Refused"/> — the save hook is left off and
    /// the fix is a Bootstrap that owns the typed site plus a restart of both, not a silent
    /// permanent pin. A refused <c>TriggerSave</c> means mod stores are written on world unload
    /// and shutdown but not on each autosave; the line below says the site is unpatched, and the
    /// <c>Uninstall</c>/<c>WorldReady</c> save paths are unaffected.</para>
    /// </summary>
    private static void RouteFallback(string site, Harmony? harmony, Type hookType, Func<bool>? slotRegister)
    {
        var route = Progenitor.Patches.CorePatchFallbackPolicy.Decide(
            slotShapeFits: slotRegister != null,
            bootstrapPresent: Progenitor.Patches.PatchRegistry.SlotsAvailable);

        switch (route)
        {
            case Progenitor.Patches.CoreFallbackRoute.Refused:
                Plugin.Logger?.LogError(Progenitor.Patches.CorePatchFallbackPolicy.FormatRefused(
                    Progenitor.Patches.CorePatchFallbackPolicy.ModulePersistence, site));
                return;

            case Progenitor.Patches.CoreFallbackRoute.Direct:
                Plugin.Logger?.LogWarning(Progenitor.Patches.CorePatchFallbackPolicy.FormatDirect(
                    Progenitor.Patches.CorePatchFallbackPolicy.ModulePersistence, site,
                    Progenitor.WorldState.WorldEpoch.CoreGenerationOrZero()));
                TryPatchAll(RequireHarmony(harmony), hookType);
                return;

            default:
                if (slotRegister!()) return;
                Plugin.Logger?.LogError(
                    "ModSave: the slot-routed fallback for " + site + " was refused (see the "
                    + "patches: line above); this seam is off for this Core generation.");
                return;
        }
    }

    /// <summary>
    /// The slot-routed <c>BeginCreateServerWorld</c> postfix. The shape fits: one by-value
    /// argument, no <c>__result</c>, no <c>__state</c>. The argument is found in <c>__args</c> by
    /// the original's own parameter name rather than bound by Harmony, so a game update that
    /// inserts a parameter moves the index with it and one that renames it leaves the seam
    /// unpatched with a line naming it. False (with a line already logged) when the site could
    /// not be armed.
    /// </summary>
    private static bool RegisterBeginCreateSlot()
    {
        var original = AccessTools.Method(
            typeof(ServerWorldManager), nameof(ServerWorldManager.BeginCreateServerWorld));
        if (original == null)
        {
            Plugin.Logger?.LogError(
                "ModSave: ServerWorldManager.BeginCreateServerWorld was not found on the interop "
                + "type; the save directory is not captured at world creation.");
            return false;
        }

        int index = Progenitor.Patches.PatchRegistry.ArgIndex(original, "saveConfiguration");
        if (index < 0)
        {
            Plugin.Logger?.LogError(Progenitor.Patches.SlotArgs.FormatMissingParameter(
                "ServerWorldManager.BeginCreateServerWorld", "saveConfiguration",
                "the save-directory capture is left unpatched"));
            return false;
        }

        Progenitor.Patches.Patches.Install();
        return Progenitor.Patches.PatchRegistry.PostfixSlot(
            owner: null, ownerId: OwnerId, target: original,
            postfix: (_, args) =>
            {
                if (!Progenitor.Patches.SlotArgs.TryArg<ServerRuntimeSettings>(args, index, out var settings))
                {
                    Plugin.Logger?.LogWarning(Progenitor.Patches.SlotArgs.FormatWrongArgumentType(
                        "BeginCreateServerWorld", "saveConfiguration", index,
                        args != null && index < args.Length ? args[index] : null,
                        typeof(ServerRuntimeSettings)));
                    return;
                }
                try { CaptureSettings(settings); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogError($"ModSave: BeginCreateServerWorld postfix threw: {e}");
                }
            }) != null;
    }

    private static Harmony RequireHarmony(Harmony? harmony)
    {
        if (harmony != null)
        {
            _harmony = harmony;
            return harmony;
        }
        var created = Reload.HarmonyFallback.Ensure();
        _harmony = created;
        return created;
    }

    private static void SlotTriggerSave(bool result)
    {
        if (!result) return;
        try
        {
            TryCaptureFromWorld(Worlds.Server);
            SaveAll();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ModSave: TriggerSave postfix threw: {e}");
        }
    }

    // ---- harmony ------------------------------------------------------------------------------

    /// <summary>
    /// <see cref="TriggerPersistenceSaveSystem.TriggerSave"/> is the method that fires once per
    /// save (CallerCount 2: autosave and admin/manual). <c>OnUpdate</c> runs every frame and is
    /// the wrong seam. The postfix takes only <c>__result</c>: a false return means the game
    /// declined the save (not ready) and we must not write. The large
    /// <see cref="ServerRuntimeSettings"/> argument is not in the Harmony signature on purpose
    /// (1 KB by-value struct); the path is captured from BeginCreateServerWorld and the singleton.
    /// </summary>
    private static class TriggerSaveHook
    {
        [HarmonyPatch(typeof(TriggerPersistenceSaveSystem), nameof(TriggerPersistenceSaveSystem.TriggerSave))]
        [HarmonyPostfix]
        private static void AfterTriggerSave(bool __result)
        {
            if (!__result) return;
            try
            {
                TryCaptureFromWorld(Worlds.Server);
                SaveAll();
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"ModSave: TriggerSave postfix threw: {e}");
            }
        }
    }

    /// <summary>
    /// Captures <see cref="ServerRuntimeSettings.SaveDirectoryPath"/> / <c>SaveToLoad</c> when the
    /// server world is created, which is earlier than <see cref="WorldReady"/> and is when existing
    /// JSON must be loaded. Parameters are by-value, so the by-ref-struct Harmony trap does not apply.
    /// </summary>
    private static class BeginCreateHook
    {
        [HarmonyPatch(typeof(ServerWorldManager), nameof(ServerWorldManager.BeginCreateServerWorld))]
        [HarmonyPostfix]
        private static void AfterBeginCreate(ServerRuntimeSettings saveConfiguration)
        {
            try { CaptureSettings(saveConfiguration); }
            catch (Exception e) { Plugin.Logger?.LogError($"ModSave: BeginCreateServerWorld postfix threw: {e}"); }
        }
    }
}
