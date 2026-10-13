using System;
using System.Collections.Generic;
using System.IO;

namespace Progenitor.Persistence;

/// <summary>
/// The in-memory server-level stores behind <see cref="ServerSave"/>, for one persistent data
/// root. Game-free (the root is handed in), so the test project drives it over a temp folder.
///
/// <para>Each store is the same <see cref="ModStore"/> a world store is: typed JSON values, the
/// pending overlay, <see cref="IModStore.WhenReady"/>, and a write to <c>.tmp</c> then a move.
/// What differs is the folder and the lifecycle. The root is the process's persistent data path,
/// which is normally known when Core installs, so a server store is usually real from its first
/// <c>Store</c> call. A store handed out before the root is known is pending exactly like a world
/// store, and is merged with disk (disk base, dirty keys win) when <see cref="SetRoot"/> lands.</para>
///
/// <para>The root is set once. A later, different root is refused with a warning rather than
/// moving files, because nothing in the game changes the persistent data path of a running
/// process; a second answer means a resolver disagreed, and following it would split the data.</para>
/// </summary>
internal sealed class ServerStoreSet
{
    /// <summary>Log prefix for the set and for every store it creates.</summary>
    internal const string Area = "ServerSave";

    private readonly object _gate = new();
    private readonly Dictionary<string, ModStore> _stores = new(StringComparer.OrdinalIgnoreCase);
    private string? _root;
    private bool _rootConflictWarned;

    /// <summary>The persistent data root, or null until <see cref="SetRoot"/>.</summary>
    internal string? Root
    {
        get { lock (_gate) return _root; }
    }

    /// <summary>The <c>progenitor-server</c> folder under <see cref="Root"/>, or null.</summary>
    internal string? Folder
    {
        get
        {
            string? root = Root;
            return string.IsNullOrEmpty(root) ? null : ServerSavePaths.StoreFolder(root!);
        }
    }

    /// <summary>How many stores are in memory.</summary>
    internal int Count
    {
        get { lock (_gate) return _stores.Count; }
    }

    /// <summary>
    /// The store for <paramref name="modGuid"/>, created on first use. Loaded from disk when the
    /// root is known; otherwise pending. Throws for an id that names a path.
    /// </summary>
    internal IModStore Store(string modGuid)
    {
        string guid = SavePaths.SanitizeStoreId(modGuid);
        lock (_gate)
        {
            if (_stores.TryGetValue(guid, out var existing)) return existing;
            string path = string.IsNullOrEmpty(_root) ? "" : ServerSavePaths.StoreFile(_root!, guid);
            var store = new ModStore(guid, path, Area);
            // Loaded before anyone else can see it, so no caller reads a half-loaded store. A new
            // store has no WhenReady registrations yet, so the load runs no callback here.
            if (!store.IsPending) store.LoadFromDisk();
            _stores[guid] = store;
            return store;
        }
    }

    /// <summary>
    /// Set the persistent data root once. Renames store files written under a plugin's old id
    /// (the identifier migration every per-plugin file gets), then points every pending store at
    /// its file and merges it with disk. True when the root was set by this call.
    /// </summary>
    internal bool SetRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;
        string trimmed = root.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        ModStore[] pending;
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(_root))
            {
                if (!string.Equals(_root, trimmed, StringComparison.OrdinalIgnoreCase) && !_rootConflictWarned)
                {
                    _rootConflictWarned = true;
                    Plugin.Logger?.LogWarning(
                        $"{Area}: a second persistent data root '{trimmed}' was resolved after '{_root}'; keeping the first so the stores are not split.");
                }
                return false;
            }
            _root = trimmed;
            pending = new ModStore[_stores.Count];
            _stores.Values.CopyTo(pending, 0);
        }

        string folder = ServerSavePaths.StoreFolder(trimmed);
        try
        {
            foreach (string line in Progenitor.Plugins.LegacyIds.MigrateFolder(folder, ".json"))
                Plugin.Logger?.LogInfo(Area + ": " + line);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"{Area}: renaming old store ids in '{folder}' threw: {e.Message}");
        }

        for (int i = 0; i < pending.Length; i++)
        {
            try
            {
                pending[i].Relocate(ServerSavePaths.StoreFile(trimmed, pending[i].ModGuid));
                pending[i].LoadFromDisk();
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"{Area}: loading '{pending[i].ModGuid}' threw: {e}");
            }
        }

        Plugin.Logger?.LogInfo($"{Area}: server store folder is {folder} (outside every world save; wipes and world restores do not touch it).");
        return true;
    }

    /// <summary>
    /// Write every store that changed since it was last written. Pending stores are skipped (no
    /// root yet; their keys stay in memory and are merged when it arrives). Returns how many were
    /// written.
    /// </summary>
    internal int SaveAll()
    {
        ModStore[] snapshot;
        lock (_gate)
        {
            if (string.IsNullOrEmpty(_root)) return 0;
            snapshot = new ModStore[_stores.Count];
            _stores.Values.CopyTo(snapshot, 0);
        }

        int wrote = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            var store = snapshot[i];
            if (store.IsPending || !store.IsDirty) continue;
            if (store.SaveToDisk()) wrote++;
        }
        return wrote;
    }

    /// <summary>
    /// Drop matching <see cref="IModStore.WhenReady"/> registrations across every store
    /// (<see cref="ModStore.OwnerIdOf"/> identities; null means all of them).
    /// </summary>
    internal int DropReadyCallbacks(IReadOnlyCollection<string>? ownerIds)
    {
        ModStore[] snapshot;
        lock (_gate)
        {
            snapshot = new ModStore[_stores.Count];
            _stores.Values.CopyTo(snapshot, 0);
        }

        int dropped = 0;
        for (int i = 0; i < snapshot.Length; i++)
        {
            try { dropped += snapshot[i].DropReadyCallbacks(ownerIds); }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"{Area}: dropping WhenReady callbacks on '{snapshot[i].ModGuid}' threw: {e}");
            }
        }
        return dropped;
    }
}
