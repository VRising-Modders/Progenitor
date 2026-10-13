using System;
using System.Collections.Generic;
using System.Threading;

namespace Progenitor.Content;

/// <summary>
/// In-generation store of prefab editors. Holds plugin-side intent plus the live editor
/// token (a delegate). The token must not be copied into Bootstrap <c>ContentMemory</c> —
/// it lives in the plugin ALC and is re-registered when plugins come back after a Core swap.
/// </summary>
internal sealed class PrefabEditTable
{
    private readonly object _gate = new();
    private readonly List<Row> _rows = new();
    private readonly Dictionary<string, int> _loadOrder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _appliedFingerprint = new();
    private readonly Dictionary<int, PrefabEditRecord[]> _lastRecords = new();
    private readonly Dictionary<int, int> _generation = new();
    private readonly HashSet<int> _running = new();
    private readonly HashSet<int> _rerunAfter = new();
    private readonly HashSet<int> _pendingRuns = new();
    /// <summary>
    /// GUIDs whose editor set shrank (Dispose / plugin unload / Unrequire sweep) and which
    /// therefore need one <see cref="PrefabEditTrigger.EditorsChanged"/> pass — a restore of
    /// the removed editor's components followed by the remaining editors, or a restore-only
    /// when nothing is left. Separate from <see cref="_pendingRuns"/> because a removal must
    /// still be serviced when the GUID has no editors at all.
    /// </summary>
    private readonly HashSet<int> _pendingRemovals = new();
    /// <summary>
    /// Handshake guids that have been observed as ServerMustHave + Exact/Minor in this
    /// generation. Distinguishes Unrequire (was live, now missing) from OnLoad-before-DeclareSelf
    /// (never in the table; attribute fallback still applies).
    /// </summary>
    private readonly HashSet<string> _handshakeAllowed = new(StringComparer.OrdinalIgnoreCase);
    private int _nextLoad;
    private int _nextIndex;

    /// <summary>Once-per-generation conflict log. Cleared on <see cref="Clear"/> (Core reload).</summary>
    public PrefabEditConflictLog Conflicts { get; } = new();

    /// <summary>
    /// Vanilla values of every component the editors of this generation overwrote. Restored
    /// before every pass except <see cref="PrefabEditTrigger.NewClone"/>, and on the last
    /// editor's removal. Cleared with the table.
    /// </summary>
    public PrefabEditBaselines Baselines { get; } = new();

    /// <summary>
    /// The <c>PrfE</c> half of the same write sites (job FEDW1): a reader closure per component
    /// the editors wrote, the last payload hash sent per prefab, and on a client the server layer
    /// that wins over local editors. Keyed like <see cref="Baselines"/> and dropped with them.
    /// </summary>
    public PrefabEditWireLayer Wire { get; } = new();

    /// <summary>
    /// Called (outside the lock) when a removal queued work in <see cref="DrainPendingRemovals"/>.
    /// The registry points this at its coalesced next-frame edit flush; nothing is scheduled in
    /// tests, which drain by hand.
    /// </summary>
    public Action? RemovalScheduled { get; set; }

    /// <summary>
    /// Thrown-editor errors, once per plugin id + guid per Core generation. A throwing pass
    /// is not fingerprinted, so CollectionReady retries; this set stops the Error line repeating.
    /// </summary>
    public PrefabEditOnceLog Throws { get; } = new();

    /// <summary>
    /// Vanilla-GUID local-only Warn, once per plugin id + guid per Core generation.
    /// Edited stats are not on the wire (R19 finding 3).
    /// </summary>
    public PrefabEditOnceLog VanillaWarns { get; } = new();

    /// <summary>
    /// Decoy-owner refuse, once per plugin id + guid per Core generation.
    /// </summary>
    public PrefabEditOnceLog OwnerWarns { get; } = new();

    public int Count
    {
        get { lock (_gate) return _rows.Count; }
    }

    /// <summary>
    /// Accept an editor. <paramref name="loadOrder"/> of −1 assigns the next first-seen slot
    /// for <paramref name="pluginId"/>; a restored intent passes the saved slot so a simulated
    /// (or real) reload keeps the same tiebreak. A before/after cycle refuses: the row is not
    /// kept, <paramref name="refuseLog"/> is the Warn line, and the handle is already invalid.
    /// </summary>
    public bool TryAdd(
        int guidHash,
        string pluginId,
        string pluginName,
        int priority,
        string[]? before,
        string[]? after,
        object owner,
        object editor,
        int loadOrder,
        out IDisposable handle,
        out string? refuseLog,
        string? version = null,
        string? handshakeId = null)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        if (editor == null) throw new ArgumentNullException(nameof(editor));
        if (string.IsNullOrWhiteSpace(pluginId))
            throw new ArgumentException("Plugin id is required.", nameof(pluginId));

        var rowHandle = new RowHandle();
        handle = rowHandle;
        refuseLog = null;
        string handshake = string.IsNullOrWhiteSpace(handshakeId) ? pluginId : handshakeId!;

        string[] beforeCopy = CopyIds(before);
        string[] afterCopy = CopyIds(after);
        string name = string.IsNullOrEmpty(pluginName) ? pluginId : pluginName;

        lock (_gate)
        {
            int assignedLoad = loadOrder >= 0 ? loadOrder : AssignLoadOrderNoLock(pluginId);
            if (loadOrder >= 0)
            {
                if (!_loadOrder.ContainsKey(pluginId) || _loadOrder[pluginId] > loadOrder)
                    _loadOrder[pluginId] = loadOrder;
                if (loadOrder >= _nextLoad) _nextLoad = loadOrder + 1;
            }

            int index = _nextIndex;
            var spec = new PrefabEditSpec(
                index,
                guidHash,
                pluginId,
                name,
                priority,
                beforeCopy,
                afterCopy,
                assignedLoad,
                version);

            // Order is per prefab. A before/after id on another GUID must not create a
            // cross-prefab edge (or a false cycle) with this row. TryAccept so the
            // refuse line names the candidate (same plugin EventBus / SystemHooks log).
            int same = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Spec.GuidHash == guidHash) same++;
            }

            var existing = new PrefabEditSpec[same];
            int w = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Spec.GuidHash != guidHash) continue;
                existing[w++] = _rows[i].Spec;
            }

            if (!PrefabEditOrder.TryAccept(existing, in spec, out refuseLog))
            {
                rowHandle.Invalidate();
                return false;
            }

            _nextIndex = index + 1;
            var row = new Row(spec, owner, editor, rowHandle, handshake);
            _rows.Add(row);
            rowHandle.Bind(this, row);
            NoteEditorSetChangedNoLock(guidHash);
            return true;
        }
    }

    public int UnregisterOwner(object owner)
    {
        if (owner == null) return 0;
        return RemoveWhere(row => ReferenceEquals(row.Owner, owner));
    }

    /// <summary>
    /// Drop every row whose handshake guid matches <paramref name="handshakeId"/>
    /// (live Require/Unrequire sweep). Case-insensitive.
    /// </summary>
    public int UnregisterHandshake(string? handshakeId)
    {
        if (string.IsNullOrEmpty(handshakeId)) return 0;
        return RemoveWhere(row =>
            string.Equals(row.HandshakeId, handshakeId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Unique handshake guids currently holding an editor (order undefined).</summary>
    public string[] UniqueHandshakeIds()
    {
        lock (_gate)
        {
            if (_rows.Count == 0) return Array.Empty<string>();
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _rows.Count; i++)
            {
                string id = _rows[i].HandshakeId;
                if (!string.IsNullOrEmpty(id)) set.Add(id);
            }
            var arr = new string[set.Count];
            set.CopyTo(arr);
            return arr;
        }
    }

    /// <summary>
    /// Drop every row. Load-order map is cleared so a fresh Core generation re-assigns from
    /// plugin re-registration order (or from restored <see cref="PrefabEditIntent.LoadOrder"/>).
    /// </summary>
    public void Clear()
    {
        List<Row> removed;
        lock (_gate)
        {
            removed = _rows.Count == 0 ? new List<Row>() : new List<Row>(_rows);
            _rows.Clear();
            _loadOrder.Clear();
            _appliedFingerprint.Clear();
            _lastRecords.Clear();
            _generation.Clear();
            _running.Clear();
            _rerunAfter.Clear();
            _pendingRuns.Clear();
            _pendingRemovals.Clear();
            _handshakeAllowed.Clear();
            _nextLoad = 0;
            _nextIndex = 0;
        }
        Conflicts.Clear();
        Throws.Clear();
        VanillaWarns.Clear();
        OwnerWarns.Clear();
        // The registry restores the baselines against the live world before calling Clear
        // (Core uninstall). Anything still here belongs to a generation that is going away.
        Baselines.Clear();
        // The readers close over this generation's entities and the sent hashes describe a layer
        // the next generation has not sent. A received server layer is not dropped here, but that
        // preserves nothing: this table is a Core static, so the next generation builds an empty
        // one regardless (R52 finding 3). A client that swaps Core gets its layers back from the
        // server's resend, not from here.
        Wire.ClearOwned();

        for (int i = 0; i < removed.Count; i++)
            removed[i].Handle.Invalidate();
    }

    /// <summary>
    /// A new client world: the entities every recorded pass wrote are gone. Drops the
    /// success fingerprints and write records (so the next <see cref="PrefabEditTrigger.CollectionReady"/>
    /// runs the editors against the new world's entities) and the captured baselines (their entity
    /// identities are stale). The editors themselves stay registered. Without this, a client's own
    /// edit of a vanilla prefab ran in the first join's world only: every later world matched the
    /// old fingerprint and skipped it. Returns how many prefabs had a recorded pass.
    /// </summary>
    public int ForgetWorld()
    {
        int n;
        lock (_gate)
        {
            n = _appliedFingerprint.Count;
            _appliedFingerprint.Clear();
            _lastRecords.Clear();
        }
        Baselines.Clear();
        return n;
    }

    /// <summary>Intent only: plugin id, guid, priority, before/after, load order. No delegates.</summary>
    public PrefabEditIntent[] ExportIntents()
    {
        lock (_gate)
        {
            if (_rows.Count == 0) return Array.Empty<PrefabEditIntent>();
            var copy = new PrefabEditIntent[_rows.Count];
            for (int i = 0; i < _rows.Count; i++)
                copy[i] = new PrefabEditIntent(_rows[i].Spec);
            return copy;
        }
    }

    public int[] UniqueGuidHashes()
    {
        lock (_gate)
        {
            if (_rows.Count == 0) return Array.Empty<int>();
            var set = new HashSet<int>();
            for (int i = 0; i < _rows.Count; i++)
                set.Add(_rows[i].Spec.GuidHash);
            var arr = new int[set.Count];
            set.CopyTo(arr);
            return arr;
        }
    }

    public bool HasGuid(int guidHash)
    {
        lock (_gate)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Spec.GuidHash == guidHash) return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Ordered live rows for <paramref name="guidHash"/>. False only if a cycle slipped in
    /// (TryAdd already refuses those); the caller logs <paramref name="cycleLog"/> and skips.
    /// </summary>
    public bool TrySnapshotOrdered(int guidHash, out PrefabEditRegistration[] ordered, out string? cycleLog)
    {
        PrefabEditSpec[] specs;
        Row[] rows;
        lock (_gate)
        {
            int count = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Spec.GuidHash == guidHash) count++;
            }

            if (count == 0)
            {
                ordered = Array.Empty<PrefabEditRegistration>();
                cycleLog = null;
                return true;
            }

            specs = new PrefabEditSpec[count];
            rows = new Row[count];
            int w = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Spec.GuidHash != guidHash) continue;
                specs[w] = _rows[i].Spec;
                rows[w] = _rows[i];
                w++;
            }
        }

        if (!PrefabEditOrder.TryOrder(specs, out var indices, out cycleLog))
        {
            ordered = Array.Empty<PrefabEditRegistration>();
            return false;
        }

        ordered = new PrefabEditRegistration[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            var row = rows[indices[i]];
            ordered[i] = new PrefabEditRegistration(row.Spec, row.Owner, row.Editor);
        }
        return true;
    }

    /// <summary>
    /// True when <paramref name="trigger"/> should invoke editors for this GUID given the
    /// current ordered set and the last successful fingerprint.
    /// </summary>
    public bool ShouldRun(int guidHash, PrefabEditTrigger trigger, PrefabEditRegistration[] ordered)
    {
        string current;
        string? applied;
        lock (_gate)
        {
            current = PrefabEditApply.Fingerprint(ordered, GenerationNoLock(guidHash));
            _appliedFingerprint.TryGetValue(guidHash, out applied);
        }
        return PrefabEditApply.ShouldRun(trigger, applied, current);
    }

    /// <summary>
    /// Current fingerprint of <paramref name="ordered"/> including this GUID's invalidation
    /// generation and per-row version tokens. Tests compare this before/after
    /// <see cref="Invalidate"/>.
    /// </summary>
    public string CurrentFingerprint(int guidHash, PrefabEditRegistration[] ordered)
    {
        lock (_gate) return PrefabEditApply.Fingerprint(ordered, GenerationNoLock(guidHash));
    }

    /// <summary>
    /// Build-stable identity of the ordered editor set for <paramref name="guidHash"/>:
    /// plugin id, priority and optional <c>Version</c> per row, in apply order. Empty when
    /// this GUID has no editors. Unlike <see cref="CurrentFingerprint"/> it carries neither
    /// the monotonic registration index nor the invalidation generation, because both move
    /// when the same plugin build re-registers after a Core swap or a plugin reload. Live
    /// refresh puts this in the definition hash, so re-running the same editors is not a
    /// change and does not destroy live buffs (R26 finding 4, fixed in FU).
    /// </summary>
    public string Stamp(int guidHash)
    {
        if (!TrySnapshotOrdered(guidHash, out var ordered, out _))
            return "";
        return StableStamp(ordered);
    }

    /// <summary>
    /// <see cref="Stamp"/> of an already-ordered editor set. Static so tests can build one
    /// without a table.
    /// </summary>
    public static string StableStamp(PrefabEditRegistration[]? ordered)
    {
        if (ordered == null || ordered.Length == 0) return "";
        var rows = new PrefabLiveRefresh.EditIdentityRow[ordered.Length];
        for (int i = 0; i < ordered.Length; i++)
        {
            var spec = ordered[i].Spec;
            rows[i] = new PrefabLiveRefresh.EditIdentityRow(spec.PluginId, spec.Priority, spec.Version);
        }
        return PrefabLiveRefresh.EditIdentity(rows);
    }

    /// <summary>Invalidation generation for <paramref name="guidHash"/> (0 until first Invalidate).</summary>
    public int Generation(int guidHash)
    {
        lock (_gate) return GenerationNoLock(guidHash);
    }

    /// <summary>
    /// Captured editor inputs for <paramref name="guidHash"/> changed. Bumps the generation
    /// so the fingerprint no longer matches a remembered CollectionReady / InputsChanged
    /// pass. An invalidate during an apply pass marks the prefab dirty and queues one
    /// re-run after that pass instead of re-entering. Does not re-arm conflict logs
    /// (the editor set is the same). Returns the new generation.
    /// </summary>
    public int Invalidate(int guidHash)
    {
        lock (_gate)
        {
            InvalidateNoLock(guidHash);
            QueueRunNoLock(guidHash);
            return GenerationNoLock(guidHash);
        }
    }

    /// <summary>
    /// <see cref="Invalidate"/> every GUID this <paramref name="owner"/> has an editor on.
    /// Returns the unique guid hashes that were bumped (empty when owner has no rows).
    /// Many SettingChanged events (a cfg Reload) coalesce to one pending run per GUID.
    /// </summary>
    public int[] InvalidateOwner(object owner)
    {
        if (owner == null) return Array.Empty<int>();
        lock (_gate)
        {
            var set = new HashSet<int>();
            for (int i = 0; i < _rows.Count; i++)
            {
                if (!ReferenceEquals(_rows[i].Owner, owner)) continue;
                set.Add(_rows[i].Spec.GuidHash);
            }
            if (set.Count == 0) return Array.Empty<int>();
            var arr = new int[set.Count];
            set.CopyTo(arr);
            for (int i = 0; i < arr.Length; i++)
            {
                InvalidateNoLock(arr[i]);
                QueueRunNoLock(arr[i]);
            }
            return arr;
        }
    }

    /// <summary>
    /// Mark <paramref name="guidHash"/> as in an apply pass. Nested
    /// <see cref="Invalidate"/> then queues a re-run after <see cref="EndRun"/> instead
    /// of re-entering. False when this GUID is already running.
    /// </summary>
    public bool TryBeginRun(int guidHash)
    {
        lock (_gate) return _running.Add(guidHash);
    }

    /// <summary>
    /// End an apply pass. True when an Invalidate landed during the pass: the caller
    /// should run once more (or drain <see cref="DrainPendingRuns"/>).
    /// </summary>
    public bool EndRun(int guidHash)
    {
        lock (_gate)
        {
            _running.Remove(guidHash);
            if (!_rerunAfter.Remove(guidHash)) return false;
            _pendingRuns.Add(guidHash);
            return true;
        }
    }

    /// <summary>
    /// Unique GUIDs waiting for an InputsChanged pass. Clears the set. A cfg Reload that
    /// fires many SettingChanged events produces one entry per affected prefab.
    /// </summary>
    public int[] DrainPendingRuns()
    {
        lock (_gate)
        {
            if (_pendingRuns.Count == 0) return Array.Empty<int>();
            var arr = new int[_pendingRuns.Count];
            _pendingRuns.CopyTo(arr);
            _pendingRuns.Clear();
            return arr;
        }
    }

    /// <summary>
    /// Record a successful pass so a later CollectionReady sweep can skip this GUID.
    /// When <paramref name="anyThrew"/> is true the pass is <em>not</em> fingerprinted: a
    /// stale success fingerprint is dropped so the next trigger retries, and describe does
    /// not claim a completed run (R19 finding 12). An Invalidate during this pass also
    /// skips the fingerprint so the coalesced re-run still sees a mismatch.
    /// </summary>
    public void RememberRun(
        int guidHash,
        PrefabEditRegistration[] ordered,
        PrefabEditRecord[] records,
        bool anyThrew = false)
    {
        if (anyThrew)
        {
            lock (_gate)
            {
                _appliedFingerprint.Remove(guidHash);
                _lastRecords.Remove(guidHash);
            }
            return;
        }

        lock (_gate)
        {
            if (_rerunAfter.Contains(guidHash))
            {
                _appliedFingerprint.Remove(guidHash);
                _lastRecords.Remove(guidHash);
                return;
            }

            string fp = PrefabEditApply.Fingerprint(ordered, GenerationNoLock(guidHash));
            _appliedFingerprint[guidHash] = fp;
            _lastRecords[guidHash] = records ?? Array.Empty<PrefabEditRecord>();
        }
    }

    /// <summary>
    /// This handshake guid was observed as ServerMustHave + Exact/Minor. A later missing
    /// row is Unrequire, not "never DeclareSelf'd".
    /// </summary>
    public void RememberHandshakeAllowed(string? handshakeId)
    {
        if (string.IsNullOrEmpty(handshakeId)) return;
        lock (_gate) _handshakeAllowed.Add(handshakeId);
    }

    /// <summary>True when <see cref="RememberHandshakeAllowed"/> ran for this guid this generation.</summary>
    public bool WasHandshakeAllowed(string? handshakeId)
    {
        if (string.IsNullOrEmpty(handshakeId)) return false;
        lock (_gate) return _handshakeAllowed.Contains(handshakeId);
    }

    /// <summary>Drop the Unrequire marker when the guid no longer has editors, or after a drop.</summary>
    public void ForgetHandshakeAllowed(string? handshakeId)
    {
        if (string.IsNullOrEmpty(handshakeId)) return;
        lock (_gate) _handshakeAllowed.Remove(handshakeId);
    }

    /// <summary>
    /// Already-disposed handle for a refused registration (cycle, handshake declaration).
    /// Dispose / unload is a no-op.
    /// </summary>
    public static IDisposable InvalidHandle()
    {
        var handle = new RowHandle();
        handle.Invalidate();
        return handle;
    }

    /// <summary>
    /// Per-prefab status fragment: <c>edits=N</c> plus unique component type names written
    /// by the last successful pass (empty components if the pass has not run yet).
    /// </summary>
    public string FormatDescribe(int guidHash)
    {
        PrefabEditRecord[]? records;
        int count;
        lock (_gate)
        {
            count = 0;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_rows[i].Spec.GuidHash == guidHash) count++;
            }
            _lastRecords.TryGetValue(guidHash, out records);
        }
        if (count <= 0) return "";
        List<string>? comps = null;
        if (records != null)
        {
            comps = new List<string>();
            for (int i = 0; i < records.Length; i++)
            {
                var c = records[i].Components;
                if (c == null) continue;
                for (int j = 0; j < c.Length; j++)
                    comps.Add(c[j]);
            }
        }
        return PrefabEditLog.EditSummary(count, comps);
    }

    private int GenerationNoLock(int guidHash)
    {
        return _generation.TryGetValue(guidHash, out int g) ? g : 0;
    }

    /// <summary>Caller holds <c>_gate</c>.</summary>
    private void InvalidateNoLock(int guidHash)
    {
        _generation.TryGetValue(guidHash, out int g);
        _generation[guidHash] = g + 1;
        _appliedFingerprint.Remove(guidHash);
    }

    /// <summary>Caller holds <c>_gate</c>. Nested during a pass → re-run after; else pending.</summary>
    private void QueueRunNoLock(int guidHash)
    {
        if (_running.Contains(guidHash))
            _rerunAfter.Add(guidHash);
        else
            _pendingRuns.Add(guidHash);
    }

    private int RemoveWhere(Func<Row, bool> match)
    {
        List<Row> removed = new();
        lock (_gate)
        {
            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                if (!match(_rows[i])) continue;
                removed.Add(_rows[i]);
                _rows.RemoveAt(i);
            }
            for (int i = 0; i < removed.Count; i++)
            {
                NoteEditorSetChangedNoLock(removed[i].Spec.GuidHash);
                _pendingRemovals.Add(removed[i].Spec.GuidHash);
            }
            ForgetOrphanHandshakeIdsNoLock(removed);
        }

        for (int i = 0; i < removed.Count; i++)
            removed[i].Handle.Invalidate();
        if (removed.Count > 0) NotifyRemovalScheduled();
        return removed.Count;
    }

    /// <summary>
    /// GUIDs whose editor set shrank since the last drain. Clears the set. The registry runs
    /// one <see cref="PrefabEditTrigger.EditorsChanged"/> pass per entry, which restores the
    /// captured baselines and then re-runs whatever editors are left (none = restore-only).
    /// </summary>
    public int[] DrainPendingRemovals()
    {
        lock (_gate)
        {
            if (_pendingRemovals.Count == 0) return Array.Empty<int>();
            var arr = new int[_pendingRemovals.Count];
            _pendingRemovals.CopyTo(arr);
            _pendingRemovals.Clear();
            return arr;
        }
    }

    /// <summary>
    /// Put a drained removal back because the pass it was handed to did not restore anything
    /// (R49 finding 6). Deliberately silent: the caller decides whether to re-arm the flush and
    /// owns the retry budget, so this must not go through <see cref="NotifyRemovalScheduled"/>
    /// and re-arm a hook the caller has just decided not to.
    /// </summary>
    public void RequeueRemoval(int guidHash)
    {
        lock (_gate) _pendingRemovals.Add(guidHash);
    }

    /// <summary>
    /// A removal comes from plugin code (handle <c>Dispose</c> in an <c>Unload</c>), so the
    /// scheduling hook must not throw back into it. The registry's callback logs its own
    /// failures; this catch is the backstop for a hook torn down mid-unload.
    /// </summary>
    private void NotifyRemovalScheduled()
    {
        var callback = RemovalScheduled;
        if (callback == null) return;
        try { callback(); }
        catch (Exception) { }
    }

    /// <summary>Caller holds <c>_gate</c>.</summary>
    private void ForgetOrphanHandshakeIdsNoLock(List<Row> removed)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < removed.Count; i++)
        {
            string id = removed[i].HandshakeId;
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
        }
        foreach (string id in ids)
        {
            bool still = false;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (!string.Equals(_rows[i].HandshakeId, id, StringComparison.OrdinalIgnoreCase)) continue;
                still = true;
                break;
            }
            if (!still) _handshakeAllowed.Remove(id);
        }
    }

    private int AssignLoadOrderNoLock(string pluginId)
    {
        if (_loadOrder.TryGetValue(pluginId, out int existing))
            return existing;
        int assigned = _nextLoad++;
        _loadOrder[pluginId] = assigned;
        return assigned;
    }

    private void RemoveRow(Row row)
    {
        bool removed = false;
        lock (_gate)
        {
            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_rows[i], row)) continue;
                _rows.RemoveAt(i);
                NoteEditorSetChangedNoLock(row.Spec.GuidHash);
                _pendingRemovals.Add(row.Spec.GuidHash);
                removed = true;
                ForgetOrphanHandshakeIdsNoLock(new List<Row> { row });
                break;
            }
        }
        row.Handle.Invalidate();
        if (removed) NotifyRemovalScheduled();
    }

    /// <summary>
    /// Editor set for this GUID changed: the next pass may re-log conflicts, and a
    /// CollectionReady sweep must not skip on a stale fingerprint. Caller holds <c>_gate</c>.
    /// </summary>
    private void NoteEditorSetChangedNoLock(int guidHash)
    {
        _appliedFingerprint.Remove(guidHash);
        _lastRecords.Remove(guidHash);
        Conflicts.InvalidateGuid(guidHash);
    }

    private static string[] CopyIds(string[]? src)
    {
        if (src == null || src.Length == 0) return Array.Empty<string>();
        var dst = new string[src.Length];
        for (int i = 0; i < src.Length; i++)
            dst[i] = src[i] ?? "";
        return dst;
    }

    internal sealed class Row
    {
        public PrefabEditSpec Spec;
        public readonly object Owner;
        public readonly object Editor;
        public readonly RowHandle Handle;
        public readonly string HandshakeId;

        public Row(PrefabEditSpec spec, object owner, object editor, RowHandle handle, string handshakeId)
        {
            Spec = spec;
            Owner = owner;
            Editor = editor;
            Handle = handle;
            HandshakeId = handshakeId ?? spec.PluginId;
        }
    }

    internal sealed class RowHandle : IDisposable
    {
        private PrefabEditTable? _table;
        private Row? _row;
        private int _disposed;

        internal void Bind(PrefabEditTable table, Row row)
        {
            _table = table;
            _row = row;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var table = _table;
            var row = _row;
            _table = null;
            _row = null;
            if (table != null && row != null)
                table.RemoveRow(row);
        }

        internal void Invalidate() => Interlocked.Exchange(ref _disposed, 1);
    }
}

/// <summary>One ordered editor ready to run. <see cref="Editor"/> is the plugin delegate boxed as object.</summary>
internal readonly struct PrefabEditRegistration
{
    public PrefabEditRegistration(PrefabEditSpec spec, object owner, object editor)
    {
        Spec = spec;
        Owner = owner;
        Editor = editor;
    }

    public PrefabEditSpec Spec { get; }
    public object Owner { get; }
    public object Editor { get; }
}
