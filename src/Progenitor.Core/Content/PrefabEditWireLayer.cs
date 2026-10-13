using System;
using System.Collections.Generic;

namespace Progenitor.Content;

/// <summary>
/// The per-generation state behind <c>PrfE</c> (job FEDW1): the reader closures the write site
/// registered, the last payload hash sent per prefab, and — on a client — the server layer that
/// was received and must win over any local editor.
///
/// <para><b>Why the readers live here and not in <see cref="PrefabEditBaselines"/>.</b> They are
/// keyed identically (prefab guid + component type name) and dropped with the baselines, but they
/// are the opposite direction: a baseline is a value captured once and written back, a reader is
/// a closure invoked every time the layer is serialized and must see the <em>current</em> value.
/// Keeping them apart means the baselines store stays exactly "vanilla, captured once" and cannot
/// grow a second lifetime rule; <see cref="DropGuid"/> and <see cref="Clear"/> are called from the
/// same places <c>Baselines.Drop</c> / <c>Clear</c> are, so the two never drift.</para>
///
/// <para>Game-free by construction: a reader is <c>Func&lt;object, byte[]?&gt;</c> over an opaque
/// context (the live <c>World</c>), built at the write site while <c>T</c> was still known,
/// exactly as the baseline restore callback is.</para>
/// </summary>
internal sealed class PrefabEditWireLayer
{
    private readonly object _gate = new();

    /// <summary>Per guid, the readers in registration order (which is the wire order).</summary>
    private readonly Dictionary<int, List<Reader>> _readers = new();

    /// <summary>Per guid, the payload hash of the last layer actually sent.</summary>
    private readonly Dictionary<int, int> _sentHash = new();

    /// <summary>Per guid, the server layer this client received and re-applies after every pass.</summary>
    private readonly Dictionary<int, PrefabEditComponentBytes[]> _received = new();

    /// <summary>Guids already charged against the per-join sync budget (client side).</summary>
    private readonly HashSet<int> _charged = new();

    /// <summary>Per guid, the payload hash of the last layer this client actually applied.</summary>
    private readonly Dictionary<int, int> _appliedHash = new();

    /// <summary>Per guid, when the last packet went out (a monotonic millisecond clock).</summary>
    private readonly Dictionary<int, long> _lastSentMs = new();

    /// <summary>
    /// Per user index, the payload hash of the last layer that user was sent per guid (job FEDW4,
    /// R52 finding 3). The broadcast dedupe (<see cref="_sentHash"/>) is per prefab and says
    /// nothing about one user, so it cannot answer "does <em>this</em> client still have the
    /// layer?" — the question a resend after a client Core swap asks. Keeping the answer per user
    /// is what makes the resend idempotent and what stops the join send and the handshake-ok edge
    /// from shipping every layer twice on every join.
    /// </summary>
    private readonly Dictionary<int, Dictionary<int, int>> _sentToUser = new();

    /// <summary>Guids whose send was suppressed by the rate floor and is owed a trailing send.</summary>
    private readonly HashSet<int> _deferred = new();

    /// <summary>
    /// Least time between two <c>PrfE</c> packets for the same prefab. An editor whose value
    /// changes every pass (a synced setting being dragged, a plugin reading a clock, an editor
    /// bug) used to produce one packet per frame per handshaken user, up to 15 KiB each, with no
    /// bound anywhere on the send path (R51 findings 2 and 8). One per second is far more than a
    /// balance change needs, and the suppressed send is not dropped — it is coalesced into a
    /// trailing one, so the last value always lands.
    /// </summary>
    public const int MinSendIntervalMs = 1000;

    // ---- readers (server side) -------------------------------------------------------------

    /// <summary>
    /// Register the reader for one component the first time an editor writes it. Later writes of
    /// the same component in the same generation keep the first closure: it reads the live value,
    /// so a second one would be identical work.
    /// </summary>
    public bool RegisterReader(int guidHash, string? typeFullName, Func<object, byte[]?>? reader)
    {
        if (string.IsNullOrEmpty(typeFullName) || reader == null) return false;
        if (PrefabEditWire.IsRefusedTypeName(typeFullName, out _)) return false;
        // The boundary again, at the earliest point on the sending side (job FEDW4, R52 finding 1):
        // a component Core does not vouch for never gets a reader, so it is never even serialized.
        // The write site logs the refusal once per type; here it is silent by design, because this
        // is called from every edit write.
        if (!PrefabEditWireTypes.IsVouched(typeFullName)) return false;
        lock (_gate)
        {
            if (!_readers.TryGetValue(guidHash, out var list))
            {
                list = new List<Reader>();
                _readers[guidHash] = list;
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].TypeFullName, typeFullName, StringComparison.Ordinal))
                    return false;
            }
            list.Add(new Reader(typeFullName!, reader));
            return true;
        }
    }

    /// <summary>Component type names with a reader for this prefab, in registration order.</summary>
    public string[] ReaderTypes(int guidHash)
    {
        lock (_gate)
        {
            if (!_readers.TryGetValue(guidHash, out var list) || list.Count == 0)
                return Array.Empty<string>();
            var names = new string[list.Count];
            for (int i = 0; i < list.Count; i++) names[i] = list[i].TypeFullName;
            return names;
        }
    }

    /// <summary>Prefabs with at least one reader (order undefined).</summary>
    public int[] ReaderGuids()
    {
        lock (_gate)
        {
            if (_readers.Count == 0) return Array.Empty<int>();
            var arr = new int[_readers.Count];
            _readers.Keys.CopyTo(arr, 0);
            return arr;
        }
    }

    /// <summary>
    /// Invoke every reader of one prefab against <paramref name="context"/> (the live world) and
    /// return the records in registration order. A reader that throws or answers null is skipped
    /// and reported through <paramref name="onError"/>; the rest still serialize.
    /// </summary>
    public List<PrefabEditComponentBytes> Serialize(
        int guidHash,
        object context,
        Action<string, Exception>? onError = null)
    {
        Reader[] run;
        lock (_gate)
        {
            if (!_readers.TryGetValue(guidHash, out var list) || list.Count == 0)
                return new List<PrefabEditComponentBytes>();
            run = list.ToArray();
        }

        var records = new List<PrefabEditComponentBytes>(run.Length);
        for (int i = 0; i < run.Length; i++)
        {
            try
            {
                var bytes = run[i].Read(context);
                if (bytes == null || bytes.Length == 0) continue;
                records.Add(new PrefabEditComponentBytes(run[i].TypeFullName, bytes));
            }
            catch (Exception e)
            {
                onError?.Invoke(run[i].TypeFullName, e);
            }
        }
        return records;
    }

    // ---- dedupe (server side) ---------------------------------------------------------------

    /// <summary>
    /// True when <paramref name="payloadHash"/> differs from the last layer sent for this prefab,
    /// and records it as sent. A re-run that writes the same bytes answers false and sends
    /// nothing. <paramref name="force"/> is the join path: a new client needs the layer even
    /// though nothing changed.
    /// </summary>
    public bool ShouldSend(int guidHash, int payloadHash, bool force = false)
    {
        lock (_gate)
        {
            if (!force && _sentHash.TryGetValue(guidHash, out int last) && last == payloadHash)
                return false;
            _sentHash[guidHash] = payloadHash;
            return true;
        }
    }

    /// <summary>Forget what was last sent for this prefab, so the next pass sends again.</summary>
    public void ForgetSent(int guidHash)
    {
        lock (_gate) _sentHash.Remove(guidHash);
    }

    /// <summary>
    /// The content half of the dedupe with <b>no</b> side effect: true when these bytes differ
    /// from the last layer sent. Asking before the expensive half is what lets a no-change pass
    /// skip the type resolution, the <c>Marshal.SizeOf</c> walk and the framing rather than only
    /// the transport (R51 finding 8); <see cref="RecordSent"/> records the answer afterwards, so a
    /// build that fails does not leave the prefab deduped against a packet nobody ever saw.
    /// </summary>
    public bool WouldSend(int guidHash, int payloadHash)
    {
        lock (_gate)
            return !(_sentHash.TryGetValue(guidHash, out int last) && last == payloadHash);
    }

    /// <summary>Record a layer that actually went out to at least one user.</summary>
    public void RecordSent(int guidHash, int payloadHash, long nowMs)
    {
        lock (_gate)
        {
            _sentHash[guidHash] = payloadHash;
            _lastSentMs[guidHash] = nowMs;
            _deferred.Remove(guidHash);
        }
    }

    /// <summary>True when this prefab has a layer out on the wire that a withdraw would retract.</summary>
    public bool HasSent(int guidHash)
    {
        lock (_gate) return _sentHash.ContainsKey(guidHash);
    }

    /// <summary>
    /// Claim the right to withdraw this prefab's layer: true once, for a prefab that has one, and
    /// it stops having one. Idempotent by construction, so the two seams that can retire a prefab
    /// (the last editor removed, and a pass that stopped touching it) cannot both send a withdraw.
    /// </summary>
    public bool TakeSentForWithdraw(int guidHash)
    {
        lock (_gate)
        {
            bool had = _sentHash.Remove(guidHash);
            _lastSentMs.Remove(guidHash);
            _deferred.Remove(guidHash);
            return had;
        }
    }

    /// <summary>
    /// The rate floor. True when this prefab may send now; false records it as owed a trailing
    /// send, which <see cref="DueDeferred"/> hands back once the interval has passed.
    /// </summary>
    public bool TryTakeSendSlot(int guidHash, long nowMs, int minIntervalMs = MinSendIntervalMs)
    {
        lock (_gate)
        {
            if (_lastSentMs.TryGetValue(guidHash, out long last)
                && nowMs >= last
                && nowMs - last < minIntervalMs)
            {
                _deferred.Add(guidHash);
                return false;
            }
            return true;
        }
    }

    /// <summary>Prefabs whose suppressed send is now due. Order undefined; the set is not cleared
    /// (a send clears its own entry through <see cref="RecordSent"/>, and a still-too-early guid
    /// stays owed).</summary>
    public int[] DueDeferred(long nowMs, int minIntervalMs = MinSendIntervalMs)
    {
        lock (_gate)
        {
            if (_deferred.Count == 0) return Array.Empty<int>();
            var due = new List<int>(_deferred.Count);
            foreach (int guid in _deferred)
            {
                if (!_lastSentMs.TryGetValue(guid, out long last)) { due.Add(guid); continue; }
                if (nowMs < last || nowMs - last >= minIntervalMs) due.Add(guid);
            }
            return due.Count == 0 ? Array.Empty<int>() : due.ToArray();
        }
    }

    // ---- per-user dedupe (server side, R52 finding 3) ----------------------------------------

    /// <summary>
    /// True when this user has not been sent this payload for this prefab. No side effect;
    /// <see cref="RecordSentToUser"/> records it after the packet actually went out.
    /// </summary>
    public bool WouldSendToUser(int userIndex, int guidHash, int payloadHash)
    {
        lock (_gate)
            return !(_sentToUser.TryGetValue(userIndex, out var perGuid)
                && perGuid.TryGetValue(guidHash, out int last)
                && last == payloadHash);
    }

    /// <summary>Record a layer this user actually received.</summary>
    public void RecordSentToUser(int userIndex, int guidHash, int payloadHash)
    {
        lock (_gate)
        {
            if (!_sentToUser.TryGetValue(userIndex, out var perGuid))
            {
                perGuid = new Dictionary<int, int>();
                _sentToUser[userIndex] = perGuid;
            }
            perGuid[guidHash] = payloadHash;
        }
    }

    /// <summary>
    /// Forget everything one user was sent. Called when that user <b>joins</b> — a user index is
    /// reused, so a fresh join must not inherit the previous occupant's record — and nowhere else:
    /// <c>PrefabEditWireApply.SendAllToUser</c>'s <c>freshJoin</c> branch is the one caller.
    ///
    /// <para>Departure is handled by <see cref="RetainUsers"/>, called every
    /// <c>PollHandshakeResends</c> with the connected set, which prunes
    /// every index that is no longer connected, because a disconnect has no hook in this module
    /// (job FEDW5, R53 finding 10: this sentence used to promise a leave-time caller that does not
    /// exist, and the next reader auditing the bound greps for it, does not find it, and concludes
    /// the memory is unbounded).</para>
    /// </summary>
    public void ForgetUser(int userIndex)
    {
        lock (_gate) _sentToUser.Remove(userIndex);
    }

    /// <summary>Drop the per-user records of every user that is no longer connected.</summary>
    public void RetainUsers(ICollection<int>? connected)
    {
        lock (_gate)
        {
            if (_sentToUser.Count == 0) return;
            if (connected == null || connected.Count == 0)
            {
                _sentToUser.Clear();
                return;
            }
            List<int>? gone = null;
            foreach (int index in _sentToUser.Keys)
            {
                if (connected.Contains(index)) continue;
                (gone ??= new List<int>()).Add(index);
            }
            if (gone == null) return;
            for (int i = 0; i < gone.Count; i++) _sentToUser.Remove(gone[i]);
        }
    }

    /// <summary>Users with at least one recorded layer (diagnostics and tests).</summary>
    public int TrackedUserCount
    {
        get { lock (_gate) return _sentToUser.Count; }
    }

    /// <summary>Deferred sends still owed (diagnostics and tests).</summary>
    public int DeferredCount
    {
        get { lock (_gate) return _deferred.Count; }
    }

    // ---- received layer (client side) --------------------------------------------------------

    /// <summary>Store (or replace) the server layer for one prefab.</summary>
    public void SetReceived(int guidHash, IReadOnlyList<PrefabEditComponentBytes>? records)
    {
        lock (_gate)
        {
            if (records == null || records.Count == 0)
            {
                _received.Remove(guidHash);
                return;
            }
            var copy = new PrefabEditComponentBytes[records.Count];
            for (int i = 0; i < records.Count; i++) copy[i] = records[i];
            _received[guidHash] = copy;
        }
    }

    /// <summary>
    /// Receive-side dedupe (job FEDW3, R51 finding 2): true when this payload differs from the
    /// last one applied for this prefab, and records it. An identical repeat is dropped before the
    /// store, the entity writes and the log lines — which is what turns a per-guid packet flood
    /// from "unlimited entity writes and unlimited attacker-authored log text" into a parse.
    /// </summary>
    public bool ShouldApplyReceived(int guidHash, int payloadHash)
    {
        lock (_gate)
        {
            if (_appliedHash.TryGetValue(guidHash, out int last) && last == payloadHash) return false;
            _appliedHash[guidHash] = payloadHash;
            return true;
        }
    }

    /// <summary>The stored server layer for one prefab, or an empty array.</summary>
    public PrefabEditComponentBytes[] Received(int guidHash)
    {
        lock (_gate)
            return _received.TryGetValue(guidHash, out var recs)
                ? recs
                : Array.Empty<PrefabEditComponentBytes>();
    }

    /// <summary>True when this client holds a server layer for the prefab.</summary>
    public bool HasReceived(int guidHash)
    {
        lock (_gate) return _received.ContainsKey(guidHash);
    }

    /// <summary>Prefabs with a stored server layer (order undefined).</summary>
    public int[] ReceivedGuids()
    {
        lock (_gate)
        {
            if (_received.Count == 0) return Array.Empty<int>();
            var arr = new int[_received.Count];
            _received.Keys.CopyTo(arr, 0);
            return arr;
        }
    }

    /// <summary>
    /// Charge one prefab against the per-join synced-row budget, once per join per prefab. A
    /// server pushing new values for a prefab it already sent does not spend more budget: the cap
    /// bounds how many prefabs a server may reach into, not how often it corrects them.
    /// </summary>
    public bool NeedsBudgetCharge(int guidHash)
    {
        lock (_gate) return _charged.Add(guidHash);
    }

    /// <summary>Undo a charge that the budget then refused, so a later join can try again.</summary>
    public void ReleaseBudgetCharge(int guidHash)
    {
        lock (_gate) _charged.Remove(guidHash);
    }

    /// <summary>New join / new client world: nothing carried over is still owed a charge.</summary>
    public void ResetBudgetCharges()
    {
        lock (_gate) _charged.Clear();
    }

    // ---- lifetime -----------------------------------------------------------------------------

    /// <summary>
    /// Drop everything for one prefab. Called wherever <c>Baselines.Drop</c> is: the readers
    /// belong to the editors that are going away, and the sent hash must not survive them or the
    /// next pass would dedupe against a layer that no longer exists.
    /// </summary>
    public void DropGuid(int guidHash)
    {
        lock (_gate)
        {
            _readers.Remove(guidHash);
            _sentHash.Remove(guidHash);
            _lastSentMs.Remove(guidHash);
            _deferred.Remove(guidHash);
            foreach (var perGuid in _sentToUser.Values) perGuid.Remove(guidHash);
        }
    }

    /// <summary>Drop only the received server layer for one prefab (a server unregister, or a
    /// withdraw packet). The applied-payload fingerprint goes with it, so a later push of the same
    /// bytes is applied rather than deduped away.</summary>
    public bool DropReceived(int guidHash)
    {
        lock (_gate)
        {
            _appliedHash.Remove(guidHash);
            return _received.Remove(guidHash);
        }
    }

    /// <summary>Core uninstall, table clear, or a new client world.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _readers.Clear();
            _sentHash.Clear();
            _received.Clear();
            _charged.Clear();
            _appliedHash.Clear();
            _lastSentMs.Clear();
            _deferred.Clear();
            _sentToUser.Clear();
        }
    }

    /// <summary>
    /// Core generation swap: forget this generation's readers (they close over its delegates),
    /// every sent hash (the next generation has sent nothing) and every per-user record.
    ///
    /// <para><b>A received server layer does not survive a Core swap</b> (R52 finding 3 corrected
    /// the paragraph that used to stand here). This object hangs off <c>PrefabEditTable</c>, which
    /// is a Core static: the next generation constructs a new table with an empty <c>_received</c>
    /// whatever this method does, and the only caller is <c>PrefabEditTable.Clear</c> at Core
    /// uninstall. Not clearing <c>_received</c> here therefore preserves nothing; the recovery is
    /// the server's resend — <c>PrefabRegistry.ResendEditLayersTo</c>, driven by the client
    /// re-answering the handshake after the swap.</para>
    /// </summary>
    public void ClearOwned()
    {
        lock (_gate)
        {
            _readers.Clear();
            _sentHash.Clear();
            _lastSentMs.Clear();
            _deferred.Clear();
            _sentToUser.Clear();
        }
    }

    /// <summary>Everything a client received this session (the new-world drop keeps readers).</summary>
    public void ClearReceived()
    {
        lock (_gate)
        {
            _received.Clear();
            _charged.Clear();
            _appliedHash.Clear();
        }
    }

    private readonly struct Reader
    {
        public Reader(string typeFullName, Func<object, byte[]?> read)
        {
            TypeFullName = typeFullName;
            Read = read;
        }

        public string TypeFullName { get; }
        public Func<object, byte[]?> Read { get; }
    }
}
