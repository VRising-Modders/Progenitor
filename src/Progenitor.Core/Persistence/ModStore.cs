using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Progenitor.Persistence;

/// <summary>
/// In-memory JSON object for one mod. The on-disk file is a JSON object whose property names are
/// the keys; <see cref="System.Text.Json"/> is the only serializer because Core cannot take a
/// second runtime dependency.
///
/// A store created before the world save folder is known is <see cref="IsPending"/>: writes stay
/// in memory, <see cref="SaveToDisk"/> is a no-op, and the first load (when the folder arrives)
/// takes disk JSON as the base then overlays the keys written so far. Dirty keys win, so an
/// OnLoad increment cannot wipe the rest of the file.
/// </summary>
internal sealed class ModStore : IModStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        IncludeFields = true,
    };

    private static bool _saveSkippedWarned;

    private readonly object _gate = new();
    private JsonObject _data = new();
    private readonly HashSet<string> _dirtyKeys = new(StringComparer.Ordinal);
    private readonly List<ReadyRegistration> _ready = new();
    private bool _dirty;
    private bool _pending;
    private bool _relocateFailedLogged;
    private JsonObject? _extraBase;

    public string ModGuid { get; }
    public string FilePath { get; private set; }

    /// <summary>
    /// True until the save directory is known and disk has been merged. Pending stores must not
    /// be written: there is no path yet, and a write would shadow the real JSON.
    /// </summary>
    internal bool IsPending
    {
        get { lock (_gate) return _pending; }
    }

    /// <summary>
    /// True once this store is backed by a real file under the world save folder: the pending
    /// merge has run and <see cref="FilePath"/> names a path. This — not "not pending" alone —
    /// is what <see cref="WhenReady"/> waits for, because a store that lost its pending flag
    /// with no path behind it still cannot answer a read from disk.
    /// </summary>
    internal bool IsReady
    {
        get { lock (_gate) return !_pending && !string.IsNullOrEmpty(FilePath); }
    }

    /// <summary>
    /// Log prefix for this store's lines: <c>ModSave</c> for a world store, <c>ServerSave</c> for a
    /// server-level one, so an operator reading a failed load knows which folder to look in.
    /// </summary>
    internal string Area { get; }

    public ModStore(string modGuid, string? filePath, string area = "ModSave")
    {
        ModGuid = modGuid;
        FilePath = filePath ?? "";
        Area = string.IsNullOrEmpty(area) ? "ModSave" : area;
        _pending = string.IsNullOrEmpty(FilePath);
    }

    /// <summary>
    /// Point this store at a new world-save path. <see cref="FilePath"/> is retargeted only
    /// after a successful move, when the destination already exists, or when there is no old
    /// file to bring along (a pending store that just learned the folder). When dest already
    /// exists and the old file is still there, old-file keys are merged as an extra base
    /// (old &lt; dest &lt; dirty) and the old file is renamed to a <c>.merged-</c> sidecar
    /// rather than left as a silent orphan. A failed move that leaves the dest missing keeps
    /// the old path so a later <see cref="LoadFromDisk"/> cannot treat a missing dest as empty
    /// and drop every non-dirty key.
    /// </summary>
    /// <returns>True when <see cref="FilePath"/> was changed to <paramref name="filePath"/>.</returns>
    internal bool Relocate(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return false;

        string old;
        lock (_gate)
        {
            old = FilePath;
            if (string.Equals(old, filePath, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        bool destAlreadyExisted = File.Exists(filePath);
        bool oldExists = !string.IsNullOrEmpty(old) && File.Exists(old);
        bool moved = SavePaths.TryMoveStoreFile(old, filePath);

        // Dest already had JSON and the old file is still there (move refused to overwrite).
        if (!moved && destAlreadyExisted && oldExists)
        {
            MergeOldFileUnderDestination(old, filePath);
            lock (_gate)
            {
                FilePath = filePath;
                _relocateFailedLogged = false;
            }
            return true;
        }

        bool destExists = destAlreadyExisted;
        if (!destExists)
            destExists = File.Exists(filePath);

        // Successful File.Move, or dest already had JSON with no old file to merge.
        if (moved || destExists)
        {
            lock (_gate)
            {
                FilePath = filePath;
                _relocateFailedLogged = false;
            }
            return true;
        }

        // No old file (pending, or never written): assigning the path is not a failed move.
        if (!oldExists)
        {
            lock (_gate) FilePath = filePath;
            return true;
        }

        // Old file still there, dest missing, move failed. Keep the populated path (log once).
        bool log;
        lock (_gate)
        {
            log = !_relocateFailedLogged;
            _relocateFailedLogged = true;
        }
        if (log)
        {
            Plugin.Logger?.LogWarning(
                $"{Area}: could not move '{old}' to '{filePath}'; keeping the old path so a missing dest is not loaded as empty.");
        }
        return false;
    }

    /// <summary>
    /// Dest already exists so the old file cannot be moved onto it. Merge old-file keys as an
    /// extra base under dest and the dirty overlay (old &lt; dest &lt; dirty), mark old-only
    /// keys dirty so a later dest load cannot drop them, and rename the old file to a sidecar.
    /// </summary>
    private void MergeOldFileUnderDestination(string oldPath, string destPath)
    {
        var oldObj = ReadObjectOrNull(oldPath);
        var destObj = ReadObjectOrNull(destPath);

        var keptFromOld = new List<string>();
        var destOverrode = new List<string>();
        List<string> dirtyWins;
        lock (_gate)
        {
            dirtyWins = new List<string>(_dirtyKeys);
            if (oldObj != null)
            {
                foreach (var kv in oldObj)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    bool destHas = destObj != null && destObj.ContainsKey(kv.Key);
                    if (destHas)
                    {
                        if (!_dirtyKeys.Contains(kv.Key))
                            destOverrode.Add(kv.Key);
                        continue;
                    }

                    if (!_dirtyKeys.Contains(kv.Key))
                        _data[kv.Key] = StoreMerge.Clone(kv.Value);
                    _dirtyKeys.Add(kv.Key);
                    keptFromOld.Add(kv.Key);
                }
            }

            var overlay = SnapshotOverlay_NoLock();
            _data = StoreMerge.Merge(oldObj, destObj, overlay);
            _extraBase = oldObj;
            _dirty = overlay.Count > 0;
            if (!_dirty) _dirtyKeys.Clear();
        }

        if (oldObj == null)
        {
            Plugin.Logger?.LogWarning(
                $"ModSave: could not parse '{oldPath}' while merging onto existing '{destPath}'; dest and dirty overlay still apply.");
        }

        var sidecar = SavePaths.TryArchiveMergedStoreFile(oldPath);
        Plugin.Logger?.LogInfo(
            $"ModSave: merged '{oldPath}' under existing '{destPath}' (old < dest < dirty; " +
            $"kept from old: {FormatKeys(keptFromOld)}; dest overrode: {FormatKeys(destOverrode)}; " +
            $"dirty overlay wins: {FormatKeys(dirtyWins)}). " +
            (sidecar != null
                ? $"Old file renamed to '{sidecar}'."
                : "Old file could not be renamed and was left in place."));

        if (sidecar == null)
        {
            Plugin.Logger?.LogWarning(
                $"ModSave: could not rename '{oldPath}' after merging onto '{destPath}'; leaving the original so it is not orphaned.");
        }
    }

    private static JsonObject? ReadObjectOrNull(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (!StoreMerge.TryParseObject(text, out var parsed, out var error))
            {
                Plugin.Logger?.LogWarning(
                    $"ModSave: '{path}' {error ?? "is not a JSON object"}; skipping as merge base.");
                return null;
            }
            return parsed;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"ModSave: reading '{path}' for relocate merge threw: {e}");
            return null;
        }
    }

    private static string FormatKeys(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0) return "(none)";
        return string.Join(", ", keys);
    }

    internal bool IsDirty
    {
        get { lock (_gate) return _dirty; }
    }

    public T? Get<T>(string key)
    {
        if (TryRead(key, out T? value)) return value;
        return default;
    }

    public T Get<T>(string key, T fallback)
    {
        if (TryRead(key, out T? value) && value is not null) return value;
        return fallback;
    }

    public void Set<T>(string key, T value)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key is required.", nameof(key));
        try
        {
            var node = JsonSerializer.SerializeToNode(value, JsonOptions);
            lock (_gate)
            {
                _data[key] = node;
                _dirtyKeys.Add(key);
                _dirty = true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"{Area}: Set<{typeof(T).Name}>({ModGuid}, {key}) threw: {e}");
        }
    }

    public bool Remove(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        lock (_gate)
        {
            var had = _data.Remove(key);
            // A pending Remove of a key that only exists on disk still has to win the overlay.
            _dirtyKeys.Add(key);
            _dirty = true;
            return had;
        }
    }

    public IReadOnlyList<string> Keys
    {
        get
        {
            lock (_gate)
            {
                if (_data.Count == 0) return Array.Empty<string>();
                var keys = new string[_data.Count];
                int i = 0;
                foreach (var kv in _data) keys[i++] = kv.Key;
                return keys;
            }
        }
    }

    /// <inheritdoc />
    public IDisposable WhenReady(Action<IModStore> callback)
    {
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        return WhenReadyAs(callback, OwnerIdOf(AssemblyOf(callback)));
    }

    /// <summary>
    /// <see cref="WhenReady"/> with the owner identity supplied rather than read off the
    /// delegate. Job FEX9 (R60 finding 3) needs a seam here: the collision the fix is about is two
    /// assemblies with the <em>same simple name</em> in different load contexts, and one test
    /// project cannot build two, so the tests register with a synthetic identity of the same
    /// shape <see cref="OwnerIdOf"/> mints. Production never calls this with anything else.
    /// </summary>
    internal IDisposable WhenReadyAs(Action<IModStore> callback, string ownerId)
    {
        if (callback == null) throw new ArgumentNullException(nameof(callback));

        var registration = new ReadyRegistration(this, callback, ownerId);
        bool ready;
        lock (_gate)
        {
            ready = !_pending && !string.IsNullOrEmpty(FilePath);
            if (!ready) _ready.Add(registration);
        }

        // Already backed by the folder: the answer is available now, so do not make the caller
        // wait a frame for a value it could have read on this line.
        if (ready) registration.Fire(this, allowInline: true);
        return registration;
    }

    /// <summary>
    /// Registrations still waiting for the merge. Diagnostic (and the assertion a test needs to
    /// see that a drop actually dropped something).
    /// </summary>
    internal int PendingReadyCallbacks
    {
        get { lock (_gate) return _ready.Count; }
    }

    /// <summary>
    /// Drop the <see cref="WhenReady"/> registrations written in any of
    /// <paramref name="ownerIds"/>, or every one of them when it is null. Returns how many
    /// were dropped. Called from <see cref="ModSave.RemoveAll"/> (plugin unload) and
    /// <see cref="ModSave.Uninstall"/> (Core uninstall): a callback held across either would run
    /// code from an assembly that is gone.
    ///
    /// <para>A <em>set</em> rather than one entry (job FEX8, R59 finding 10). A plugin's
    /// collectible context holds the plugin assembly plus every rewritten dependency loaded into
    /// it, and the reloader already knows that list; matching only the plugin's own assembly left
    /// a callback declared in a helper library waiting, holding a delegate whose method lives in
    /// the ALC the sweep is emptying — the exact pin the sweep call site says it exists to
    /// remove.</para>
    ///
    /// <para><b>The entries are <see cref="OwnerIdOf"/> identities, not simple names</b> (job
    /// FEX9, R60 finding 3). Widening the set to the rewritten dependencies made the simple name
    /// a collision surface: the canonical vendored helper is called <c>Shared.dll</c>, so
    /// unloading plugin A canceled plugin B's pending callback because both had a
    /// <c>Shared</c> — B's read-modify-write against its own save file then never ran, silently.
    /// The identity carries the load-context name and the module MVID as well, so two files that
    /// merely share a name do not match, and the same file loaded into two collectible contexts
    /// does not either.</para>
    /// </summary>
    internal int DropReadyCallbacks(IReadOnlyCollection<string>? ownerIds)
    {
        if (ownerIds != null && ownerIds.Count == 0) return 0;

        List<ReadyRegistration> dropped = new();
        lock (_gate)
        {
            for (int i = _ready.Count - 1; i >= 0; i--)
            {
                var reg = _ready[i];
                if (ownerIds != null && !NamedIn(ownerIds, reg.OwnerId)) continue;
                _ready.RemoveAt(i);
                dropped.Add(reg);
            }
        }

        for (int i = 0; i < dropped.Count; i++) dropped[i].Cancel();
        return dropped.Count;
    }

    private static bool NamedIn(IReadOnlyCollection<string> ids, string ownerId)
    {
        if (ids is HashSet<string> set) return set.Contains(ownerId);
        foreach (var id in ids)
            if (string.Equals(id, ownerId, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Single-identity convenience: an owner that ships one assembly, and the tests.</summary>
    internal int DropReadyCallbacks(string? ownerId) =>
        DropReadyCallbacks(ownerId == null ? null : new[] { ownerId });

    /// <summary>
    /// The identity a pending callback is matched on: <c>load context | module MVID | simple
    /// name</c>, all three, as one ordinal string. Job FEX9 (R60 finding 3).
    ///
    /// <para><b>Why not the <see cref="Assembly"/> itself</b>, which cannot collide: a pending
    /// registration outlives the callback's owner by definition — it is waiting for a merge that
    /// may never come — so a live reference here would pin the collectible load context this
    /// whole mechanism exists to release. The three parts are all value data.</para>
    ///
    /// <para><b>Why all three.</b> The simple name alone collides between two plugins that each
    /// vendor a <c>Shared.dll</c>. The MVID alone collides when two plugins vendor the
    /// <em>same build</em> of a helper — same bytes, same MVID, two contexts. The load-context
    /// name (the reloader mints one per plugin file, <c>progenitor-reload:{name}:{ticks}</c>)
    /// separates those, and is <c>"Default"</c> for the default context (measured:
    /// <c>AssemblyLoadContext.Default.Name</c> is <c>"Default"</c>, not empty — R61 finding 5), so
    /// a default-context identity reads <c>Default|{mvid}|{name}</c> and it is the MVID and the
    /// name that separate two of those.</para>
    /// </summary>
    internal static string OwnerIdOf(Assembly? assembly)
    {
        if (assembly == null) return "";

        string context = "";
        try { context = AssemblyLoadContext.GetLoadContext(assembly)?.Name ?? ""; }
        catch { /* an unreadable context is not a reason to fail the identity */ }

        string mvid = "";
        try { mvid = assembly.ManifestModule.ModuleVersionId.ToString("N"); }
        catch { /* dynamic assemblies and anything without a manifest module */ }

        string name = "";
        try { name = assembly.GetName().Name ?? ""; }
        catch { /* an assembly that cannot be named still gets the other two parts */ }

        if (context.Length == 0 && mvid.Length == 0 && name.Length == 0) return "";
        return context + "|" + mvid + "|" + name;
    }

    /// <summary>
    /// The <see cref="OwnerIdOf"/> identities of <paramref name="owner"/>'s assembly plus every
    /// assembly in <paramref name="owned"/>, and their simple names in <paramref name="names"/> for
    /// the log line. Shared by <c>ModSave.RemoveAll</c> and <c>ServerSave.RemoveAll</c>, so the two
    /// stores drop the same set on a plugin unload.
    /// </summary>
    internal static HashSet<string> OwnerIdsFor(object? owner, IReadOnlyList<Assembly>? owned, List<string> names)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (owned != null)
        {
            for (int i = 0; i < owned.Count; i++)
            {
                try
                {
                    var asm = owned[i];
                    if (asm == null) continue;
                    string id = OwnerIdOf(asm);
                    if (id.Length > 0 && ids.Add(id)) names.Add(asm.GetName().Name ?? id);
                }
                catch { /* an assembly that cannot be identified cannot be matched either */ }
            }
        }
        if (owner == null) return ids;
        try
        {
            var own = owner.GetType().Assembly;
            string id = OwnerIdOf(own);
            if (id.Length > 0 && ids.Add(id)) names.Add(own.GetName().Name ?? id);
        }
        catch { /* the owned list may still carry the answer */ }
        return ids;
    }

    /// <summary>The assembly a callback was written in: its target's, else its method's.</summary>
    internal static Assembly? AssemblyOf(Delegate callback)
    {
        try { return callback.Target?.GetType().Assembly ?? callback.Method.DeclaringType?.Assembly; }
        catch { return null; }
    }

    private void RemoveReadyRegistration(ReadyRegistration registration)
    {
        lock (_gate) _ready.Remove(registration);
    }

    /// <summary>
    /// The merge has run and the store now answers from disk: hand every waiting registration to
    /// the main-thread queue. Posted rather than called in line, so the callback does not run
    /// inside the load's call stack.
    ///
    /// <para><b>The list is snapshotted, not emptied</b> (job FEX8, R59 finding 3). Emptying it
    /// here — and exchanging the callback to null here — retired all three named droppers at
    /// <em>post</em> time: for the frame between the merge and the drain, <c>Dispose</c>,
    /// <c>ModSave.RemoveAll</c> and <c>ModSave.Uninstall</c> walked a list the registration had
    /// already left, and <c>Cancel</c> nulled a field the queued closure had already copied into a
    /// local. A registration leaves the list when it actually runs, so a plugin that unloads
    /// inside that frame is still dropped by the sweep. Posting twice is harmless: the exchange
    /// inside the invoke lets exactly one of them through.</para>
    /// </summary>
    private void FireReady()
    {
        ReadyRegistration[] due;
        lock (_gate)
        {
            if (_ready.Count == 0) return;
            due = _ready.ToArray();
        }

        for (int i = 0; i < due.Length; i++) due[i].Fire(this, allowInline: false);
    }

    /// <summary>
    /// One <see cref="WhenReady"/> registration: the callback, the identity of the assembly that
    /// wrote it (three pieces of value data, never a live <c>Assembly</c>, which would pin a
    /// collectible load context), and the owner token handed to the main-thread queue so the
    /// reloader's own drop applies. Fires at most once; <see cref="Dispose"/> and
    /// <see cref="Cancel"/> are idempotent.
    /// </summary>
    private sealed class ReadyRegistration : IDisposable
    {
        private readonly ModStore _store;
        private Action<IModStore>? _callback;

        internal ReadyRegistration(ModStore store, Action<IModStore> callback, string ownerId)
        {
            _store = store;
            _callback = callback;
            OwnerToken = callback.Target;
            OwnerId = ownerId ?? "";
        }

        /// <summary>
        /// <see cref="OwnerIdOf"/> of the assembly that declared the callback, or "" when it was
        /// unreadable. Matched ordinally by <see cref="DropReadyCallbacks(IReadOnlyCollection{string})"/>;
        /// job FEX9 (R60 finding 3) replaced the simple name here, which two plugins can share.
        /// </summary>
        internal string OwnerId { get; }

        /// <summary>
        /// The delegate's target, passed to <c>MainThread.Post</c> as the owner. When it is
        /// non-null — a lambda, an instance method: nearly every callback — the queue tags the
        /// posted work with <em>that object's</em> assembly, so <c>DropOwnedBy</c> and the
        /// retired-context refusal both apply to the plugin.
        ///
        /// <para><b>Null for a static method group</b>, and Core cannot invent an object from the
        /// plugin's assembly to stand in for it, so for that shape the queue's tag is the posted
        /// closure's — and that closure is declared here, in Core. The queue is therefore not the
        /// dropper for a static callback; <see cref="OwnerId"/> is, through the sweep, which is
        /// why <see cref="Fire"/> leaves the registration cancellable until it runs (job FEX8, R59
        /// finding 3: the <c>IModStore</c> paragraph used to offer the queue's retired-context
        /// check as the backstop for a plugin unload, and that check reads Core's context).</para>
        /// </summary>
        private object? OwnerToken { get; }

        /// <summary>
        /// Run the callback, or queue it. <b>The callback is taken at invoke time, never at post
        /// time</b> (job FEX8, R59 finding 3): the registration stays cancellable for the whole
        /// window, so a <see cref="Dispose"/>, a plugin sweep or a Core uninstall that lands
        /// between the merge and the next drain skips the run instead of losing a race with a
        /// closure that already holds the delegate in a local.
        /// </summary>
        internal void Fire(ModStore store, bool allowInline)
        {
            if (System.Threading.Volatile.Read(ref _callback) == null) return;

            if (allowInline && Progenitor.Dev.MainThread.IsMainThread)
            {
                InvokeOnce(store);
                return;
            }

            Progenitor.Dev.MainThread.Post(() => InvokeOnce(store), OwnerToken);
        }

        /// <summary>
        /// Take the callback if it is still there, stop tracking this registration, and run it.
        /// The exchange is the once-guard: two posts (a merge, then a second <c>LoadFromDisk</c>
        /// before the drain) let exactly one through.
        /// </summary>
        private void InvokeOnce(ModStore store)
        {
            var callback = System.Threading.Interlocked.Exchange(ref _callback, null);
            if (callback == null) return;
            _store.RemoveReadyRegistration(this);
            Invoke(store, callback);
        }

        /// <summary>Drop without running: the owner is unloading, or Core is going away.</summary>
        internal void Cancel() => System.Threading.Interlocked.Exchange(ref _callback, null);

        public void Dispose()
        {
            Cancel();
            _store.RemoveReadyRegistration(this);
        }

        private static void Invoke(ModStore store, Action<IModStore> callback)
        {
            try { callback(store); }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"{store.Area}: WhenReady callback for '{store.ModGuid}' threw: {e}");
            }
        }

    }

    /// <summary>
    /// Load disk JSON as the base, then overlay keys written since the last clean load. Always
    /// reads the file when it exists: being dirty is not a reason to skip, because the dirty
    /// keys are exactly what must win over disk. A missing file on an already-loaded store is
    /// not an empty document — it is "keep what is in memory" — so a failed relocate cannot
    /// wipe every non-dirty key by pointing at a dest that was never populated. After a
    /// dest-exists Relocate the old file is an extra base under dest (old &lt; dest &lt; dirty).
    /// </summary>
    internal void LoadFromDisk()
    {
        string path;
        lock (_gate) path = FilePath;

        JsonObject? disk = null;
        bool fileMissing = string.IsNullOrEmpty(path);
        try
        {
            if (!fileMissing && File.Exists(path))
            {
                var text = File.ReadAllText(path);
                if (!StoreMerge.TryParseObject(text, out var parsed, out var error))
                {
                    Plugin.Logger?.LogWarning(
                        $"{Area}: '{path}' {error ?? "is not a JSON object"}; using in-memory keys only.");
                }
                else
                {
                    disk = parsed;
                }
            }
            else
            {
                fileMissing = true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"{Area}: load '{path}' threw: {e}");
        }

        bool becameReady;
        lock (_gate)
        {
            if (fileMissing && !_pending && _extraBase == null)
            {
                // Already-loaded store, file not there. Merging a null disk would drop every
                // non-dirty key (R3 finding 2). Dirty keys already live in _data.
                return;
            }

            var overlay = SnapshotOverlay_NoLock();
            var extra = _extraBase;
            _extraBase = null;
            // Extra base is the old file from a dest-exists Relocate (old < dest < dirty).
            _data = extra != null
                ? StoreMerge.Merge(extra, disk, overlay)
                : StoreMerge.Merge(disk, overlay);
            _pending = false;
            // Overlay keys still differ from disk until the next write, so they stay dirty.
            _dirty = overlay.Count > 0;
            if (!_dirty) _dirtyKeys.Clear();
            becameReady = !string.IsNullOrEmpty(FilePath);
        }

        // Outside the lock: a callback reads and writes this store.
        if (becameReady) FireReady();
    }

    internal bool SaveToDisk()
    {
        string json;
        string path;
        lock (_gate)
        {
            if (_pending || string.IsNullOrEmpty(FilePath))
            {
                WarnSaveSkipped_NoLock();
                return false;
            }
            json = _data.ToJsonString(JsonOptions);
            path = FilePath;
        }

        try
        {
            AtomicWrite(path, json);
            lock (_gate)
            {
                _dirty = false;
                _dirtyKeys.Clear();
            }
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"{Area}: save '{path}' threw: {e}");
            return false;
        }
    }

    private Dictionary<string, JsonNode?> SnapshotOverlay_NoLock()
    {
        var overlay = new Dictionary<string, JsonNode?>(_dirtyKeys.Count, StringComparer.Ordinal);
        foreach (var key in _dirtyKeys)
        {
            _data.TryGetPropertyValue(key, out var node);
            overlay[key] = node;
        }
        return overlay;
    }

    private static void WarnSaveSkipped_NoLock()
    {
        if (_saveSkippedWarned) return;
        _saveSkippedWarned = true;
        Plugin.Logger?.LogWarning(
            "ModSave: save directory is not known yet; skipping disk write (no %TEMP% fallback).");
    }

    private bool TryRead<T>(string key, out T? value)
    {
        value = default;
        if (string.IsNullOrEmpty(key)) return false;
        JsonNode? node;
        lock (_gate)
        {
            if (!_data.TryGetPropertyValue(key, out node) || node is null) return false;
            node = StoreMerge.Clone(node);
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<T>(node, JsonOptions);
            value = parsed;
            return parsed is not null || default(T) is null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"{Area}: Get<{typeof(T).Name}>({ModGuid}, {key}) threw: {e}");
            return false;
        }
    }

    internal static void AtomicWrite(string path, string contents)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        try
        {
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort cleanup */ }
            throw;
        }
    }

    /// <summary>Test hook so each case can see the one-time skip warning.</summary>
    internal static void ResetSaveSkippedWarning() => _saveSkippedWarned = false;
}

/// <summary>
/// Client-side stand-in so mods can call <see cref="ModSave.Store"/> (and <c>ServerSave.Store</c>)
/// without a side check.
/// </summary>
internal sealed class NoOpStore : IModStore
{
    internal static readonly NoOpStore Instance = new();

    public T? Get<T>(string key) => default;
    public T Get<T>(string key, T fallback) => fallback;
    public void Set<T>(string key, T value) { }
    public bool Remove(string key) => false;
    public IReadOnlyList<string> Keys => Array.Empty<string>();

    /// <summary>
    /// Never runs: there is no save folder on the client, so this store never becomes real and a
    /// callback that ran here would read defaults and write them nowhere. Shared plugin code can
    /// still call it unconditionally, which is the whole point of the no-op store.
    /// </summary>
    public IDisposable WhenReady(Action<IModStore> callback)
    {
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        return NeverReady.Instance;
    }

    /// <summary>Handle for a registration that can never fire.</summary>
    private sealed class NeverReady : IDisposable
    {
        internal static readonly NeverReady Instance = new();
        public void Dispose() { }
    }
}
