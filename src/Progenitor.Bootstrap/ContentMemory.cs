using System;
using System.Globalization;

namespace Progenitor.Bootstrap;

/// <summary>
/// What a <c>ContentMemory.Replace*</c> write did. These stores are the <b>only</b> thing that
/// crosses a collectible Core ALC unload, and they are written from the outgoing generation and
/// read by the incoming one — so a write that carries no information must leave the previous
/// generation's rows alone rather than overwrite them with nothing.
///
/// R28 finding 16: the fingerprint export was unconditional and stored an empty array as an
/// empty store. One swallowed rehydrate (a throw in <c>ImportClonesAndKeys</c> before the
/// fingerprint read) left generation N+1 with an empty map, and <em>its</em> export then wiped
/// the store for the rest of the process — every later swap compared against a null previous
/// fingerprint, which is R27 finding 1's behavior restored permanently and in silence. Since
/// the import side merges, an empty export carries no information and must not be applied.
///
/// R29 finding 11: that left no way at all to say "there genuinely are nothing left", so a
/// generation that removed its definitions could not clear the rows an older one wrote, and a
/// stale row survived to be merged into a later generation that has no business seeing it.
/// Emptiness on its own is still not information — the exporter now says <em>which kind</em> of
/// empty this is (<c>PrefabLiveRefresh.FingerprintExport</c>) and only an authoritatively empty
/// export reaches <see cref="ContentMemory.ClearFingerprints"/>. A failed or unsettled export
/// never calls it and the store is kept, exactly as before.
/// </summary>
public enum ContentMemoryStore
{
    /// <summary>Rows were offered and are now the stored set.</summary>
    Replaced = 0,

    /// <summary>Nothing was offered (null or empty). The previously stored rows are kept.</summary>
    KeptNothingOffered = 1,

    /// <summary>The parallel arrays disagreed in length. The previously stored rows are kept.</summary>
    KeptLengthMismatch = 2,

    /// <summary>
    /// The caller asserted the emptiness is real (this generation completed an apply pass and
    /// settled nothing), so the stored rows were dropped. Only <c>ClearFingerprints</c> returns
    /// this; no <c>Replace*</c> path can produce it from an empty array.
    /// </summary>
    Cleared = 3,
}

/// <summary>Log lines for <see cref="ContentMemoryStore"/>. Game-free so tests pin the exact text.</summary>
public static class ContentMemoryLog
{
    /// <summary>
    /// One line describing a <c>Replace*</c> outcome. <paramref name="what"/> names the store
    /// ("live-refresh fingerprint", "CloneOf hash"). Grep <c>ContentMemory:</c>.
    /// </summary>
    public static string FormatStore(ContentMemoryStore result, string what, int offered, int paired, int held)
    {
        string label = what ?? "";
        switch (result)
        {
            case ContentMemoryStore.Replaced:
                return "ContentMemory: stored " + N(offered) + " " + label + "(s) for the next Core generation.";
            case ContentMemoryStore.Cleared:
                return "ContentMemory: cleared " + N(held) + " stored " + label
                    + " row(s); the generation that wrote them completed an apply pass and settled none"
                    + " (an authoritatively empty export, not a failed one).";
            case ContentMemoryStore.KeptLengthMismatch:
                return "ContentMemory: refused a " + label + " snapshot whose arrays disagree (keys="
                    + N(offered) + " values=" + N(paired) + "); kept the " + N(held)
                    + " row(s) already stored.";
            default:
                return "ContentMemory: " + label + " snapshot offered nothing; kept the " + N(held)
                    + " row(s) already stored (an empty export carries no information).";
        }
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Process-lifetime clone / key-table / tombstone sets. After a collectible Core ALC unloads,
/// <c>PrefabApplicator.ClonedGuids</c> and <c>PrefabRegistry</c> tombstones are gone but the
/// ECS clones and <c>ItemKeyLookup</c> rows are not. A later <c>TryApply</c> then hits
/// <c>MustRefuseExistingGuid(haveExisting: true, clonedHere: false)</c> and refuses Ember
/// Shard as a foreign GUID. Bootstrap holds this snapshot across the swap; new Core copies
/// it in on Install. Appended rows carry the verified ItemKeyLookup index so rehydrate can
/// refuse a GUID that is no longer at that slot (the Hermit's Shawl class of bug).
/// Entries are only GUID hashes, ItemKeyLookup indices, and tombstone id strings — never
/// Core types, so this store cannot pin a collectible generation across <c>ALC.Unload</c>.
/// </summary>
public sealed class ContentMemory
{
    private readonly object _gate = new();
    private ContentSnapshot _snap = ContentSnapshot.Empty;

    public int ClonedCount
    {
        get { lock (_gate) return _snap.ClonedGuids.Length; }
    }

    public int AppendedCount
    {
        get { lock (_gate) return _snap.AppendedGuids.Length; }
    }

    public int TombstoneCount
    {
        get { lock (_gate) return _snap.TombstoneHashes.Length; }
    }

    /// <summary>Copy of the current snapshot. Arrays are not shared with the store.</summary>
    public ContentSnapshot Take()
    {
        lock (_gate) return _snap.Copy();
    }

    /// <summary>
    /// Replace the store. Null arrays become empty. Tombstone hash/id lengths must match.
    /// Appended and tombstone index arrays must be empty (unknown) or the same length as
    /// their GUID arrays.
    /// </summary>
    public void Replace(ContentSnapshot snapshot)
    {
        snapshot.Validate();
        lock (_gate) _snap = snapshot.Copy();
    }

    /// <summary>Replace from parallel arrays. Same contract as <see cref="Replace(ContentSnapshot)"/>.</summary>
    public void Replace(int[]? clonedGuids, int[]? appendedGuids, int[]? tombstoneHashes, string[]? tombstoneIds) =>
        Replace(new ContentSnapshot(clonedGuids, appendedGuids, tombstoneHashes, tombstoneIds, appendedIndices: null, tombstoneIndices: null));

    /// <summary>
    /// Replace including ItemKeyLookup indices. <paramref name="appendedIndices"/>[i] is the
    /// list slot for <paramref name="appendedGuids"/>[i] (−1 means unknown). Same for tombs.
    /// </summary>
    public void Replace(
        int[]? clonedGuids,
        int[]? appendedGuids,
        int[]? appendedIndices,
        int[]? tombstoneHashes,
        string[]? tombstoneIds,
        int[]? tombstoneIndices) =>
        Replace(new ContentSnapshot(clonedGuids, appendedGuids, tombstoneHashes, tombstoneIds, appendedIndices, tombstoneIndices));

    /// <summary>
    /// Rehydrate rule: a saved key-table row is still ours only when the ECS table reports
    /// the same index for that GUID. A miss or a different slot must not append at
    /// <c>list-&gt;Length</c> (client 659 vs server 1083).
    /// </summary>
    public static bool KeyRowStillHolds(int savedIndex, int currentIndex) =>
        savedIndex >= 0 && currentIndex == savedIndex;

    /// <summary>
    /// Last resolved <c>CloneOf</c> hash per cloned GUID. Parallel to
    /// <see cref="TakeCloneSources"/>: <c>guids[i]</c> was cloned from
    /// <c>sources[i]</c>. Survives a collectible Core ALC swap so a same-id
    /// source change is detected by hash, not by NetSnapshot schema fingerprint.
    /// Primitive ints only — cannot pin a Core type. Independent of
    /// <see cref="Replace(ContentSnapshot)"/> so the existing 6-arg Runtime
    /// handoff does not have to grow.
    /// </summary>
    private int[] _cloneSourceGuids = Array.Empty<int>();
    private int[] _cloneSourceHashes = Array.Empty<int>();

    /// <summary>How many remembered <c>CloneOf</c> hashes this store holds.</summary>
    public int CloneSourceCount
    {
        get { lock (_gate) return _cloneSourceGuids.Length; }
    }

    /// <summary>
    /// Replace the remembered <c>CloneOf</c> hashes. Nothing offered, or arrays that disagree in
    /// length, <b>keep</b> the set already stored: see <see cref="ContentMemoryStore"/> for why
    /// an empty write must never wipe it. The caller logs the returned outcome.
    /// </summary>
    public ContentMemoryStore ReplaceCloneSources(int[]? cloneGuids, int[]? sourceHashes)
    {
        if (cloneGuids == null || cloneGuids.Length == 0)
            return ContentMemoryStore.KeptNothingOffered;
        if (sourceHashes == null || sourceHashes.Length != cloneGuids.Length)
            return ContentMemoryStore.KeptLengthMismatch;

        var guids = new int[cloneGuids.Length];
        var sources = new int[sourceHashes.Length];
        Array.Copy(cloneGuids, guids, cloneGuids.Length);
        Array.Copy(sourceHashes, sources, sourceHashes.Length);
        lock (_gate)
        {
            _cloneSourceGuids = guids;
            _cloneSourceHashes = sources;
        }
        return ContentMemoryStore.Replaced;
    }

    /// <summary>
    /// Copy of the remembered <c>CloneOf</c> hashes. Arrays are not shared with the store.
    /// </summary>
    public void TakeCloneSources(out int[] cloneGuids, out int[] sourceHashes)
    {
        lock (_gate)
        {
            if (_cloneSourceGuids.Length == 0)
            {
                cloneGuids = Array.Empty<int>();
                sourceHashes = Array.Empty<int>();
                return;
            }
            cloneGuids = new int[_cloneSourceGuids.Length];
            sourceHashes = new int[_cloneSourceHashes.Length];
            Array.Copy(_cloneSourceGuids, cloneGuids, _cloneSourceGuids.Length);
            Array.Copy(_cloneSourceHashes, sourceHashes, _cloneSourceHashes.Length);
        }
    }

    /// <summary>
    /// Last settled live-refresh definition fingerprint per cloned GUID, parallel to
    /// <see cref="TakeFingerprints"/>: <c>guids[i]</c> settled at <c>fingerprints[i]</c>.
    /// Same shape and the same reason as <see cref="ReplaceCloneSources"/>, one store later:
    /// Core's fingerprint map is a static in the collectible assembly, so a Core swap used to
    /// hand the next generation a null previous fingerprint and the opt-in live refresh could
    /// never fire on a swap (R27 finding 1). Strings and ints only, so this cannot pin a Core
    /// generation across <c>ALC.Unload</c>.
    /// </summary>
    private int[] _fingerprintGuids = Array.Empty<int>();
    private string[] _fingerprints = Array.Empty<string>();

    /// <summary>How many live-refresh fingerprints this store holds.</summary>
    public int FingerprintCount
    {
        get { lock (_gate) return _fingerprintGuids.Length; }
    }

    /// <summary>
    /// Replace the remembered fingerprints. A length mismatch never pairs a fingerprint with the
    /// wrong GUID — that would destroy and re-apply the wrong live buff — and neither a mismatch
    /// nor an empty offer is allowed to <em>wipe</em> what is already stored
    /// (<see cref="ContentMemoryStore"/>). The caller logs the returned outcome.
    /// </summary>
    public ContentMemoryStore ReplaceFingerprints(int[]? guidHashes, string[]? fingerprints)
    {
        if (guidHashes == null || guidHashes.Length == 0)
            return ContentMemoryStore.KeptNothingOffered;
        if (fingerprints == null || fingerprints.Length != guidHashes.Length)
            return ContentMemoryStore.KeptLengthMismatch;

        var guids = new int[guidHashes.Length];
        var prints = new string[fingerprints.Length];
        Array.Copy(guidHashes, guids, guidHashes.Length);
        for (int i = 0; i < fingerprints.Length; i++)
            prints[i] = fingerprints[i] ?? "";
        lock (_gate)
        {
            _fingerprintGuids = guids;
            _fingerprints = prints;
        }
        return ContentMemoryStore.Replaced;
    }

    /// <summary>
    /// Drop every stored fingerprint. This is the <b>only</b> way to empty the store, and it is
    /// deliberately a separate method rather than an overload or a flag on
    /// <see cref="ReplaceFingerprints"/>: an empty array must stay meaningless there (R28
    /// finding 16), and Core reflects these names by simple name, so an overload would be an
    /// <c>AmbiguousMatchException</c> (the same rule <c>ProcessHost.TryGetOrCreateSprite</c>
    /// carries).
    ///
    /// The caller must have positive evidence that the emptiness is real — Core calls this only
    /// for <c>PrefabLiveRefresh.FingerprintExport.AuthoritativelyEmpty</c>, i.e. this generation
    /// completed an apply pass and settled no fingerprint at all. A generation whose export
    /// threw, or that unloaded before its first apply pass settled, must never reach here: it
    /// cannot tell "nothing to remember" from "I failed to look" (R29 finding 11).
    /// Returns <see cref="ContentMemoryStore.Cleared"/> so the caller logs it like any other
    /// store outcome.
    /// </summary>
    public ContentMemoryStore ClearFingerprints()
    {
        lock (_gate)
        {
            _fingerprintGuids = Array.Empty<int>();
            _fingerprints = Array.Empty<string>();
        }
        return ContentMemoryStore.Cleared;
    }

    /// <summary>Copy of the remembered fingerprints. Arrays are not shared with the store.</summary>
    public void TakeFingerprints(out int[] guidHashes, out string[] fingerprints)
    {
        lock (_gate)
        {
            if (_fingerprintGuids.Length == 0)
            {
                guidHashes = Array.Empty<int>();
                fingerprints = Array.Empty<string>();
                return;
            }
            guidHashes = new int[_fingerprintGuids.Length];
            fingerprints = new string[_fingerprints.Length];
            Array.Copy(_fingerprintGuids, guidHashes, _fingerprintGuids.Length);
            Array.Copy(_fingerprints, fingerprints, _fingerprints.Length);
        }
    }

    /// <summary>
    /// Client key-table join session (refused / accepted ledger). Independent of
    /// <see cref="Replace(ContentSnapshot)"/> and of the fingerprint / CloneOf stores so
    /// the 6-arg Runtime handoff does not have to grow. Primitive ints and strings only —
    /// a refused join must survive a collectible Core ALC swap, otherwise
    /// <c>NotifyLiveWorldAfterCoreSwap</c> appends custom items in local order (R87 finding 1).
    /// </summary>
    private bool _joinHeld;
    private int[] _joinHeader = Array.Empty<int>();
    private int[] _joinRowGuids = Array.Empty<int>();
    private string[] _joinRowIds = Array.Empty<string>();
    private int[] _joinRowIndices = Array.Empty<int>();
    private int[] _joinRowKinds = Array.Empty<int>();
    private int[] _joinRowUsable = Array.Empty<int>();
    private string[] _joinRowReasons = Array.Empty<string>();
    private int[] _joinUnknownGuids = Array.Empty<int>();
    private string[] _joinUnknownIds = Array.Empty<string>();
    private string[] _joinUnknownReasons = Array.Empty<string>();
    private string _joinReason = "";

    /// <summary>True when a join-session snapshot is stored for the next Core generation.</summary>
    public bool JoinSessionHeld
    {
        get { lock (_gate) return _joinHeld; }
    }

    /// <summary>
    /// Replace the remembered join session. A missing header, or arrays that disagree in
    /// length, keep what is already stored (an empty write carries no information — same
    /// rule as <see cref="ReplaceFingerprints"/>). A header of length 7 is a real snapshot,
    /// including the idle (not-yet-joined) state, so a generation that Reset the session
    /// can still say so.
    /// </summary>
    public ContentMemoryStore ReplaceJoinSession(
        int[]? header,
        int[]? rowGuids,
        string[]? rowIds,
        int[]? rowIndices,
        int[]? rowKinds,
        int[]? rowUsable,
        string[]? rowReasons,
        int[]? unknownGuids,
        string[]? unknownIds,
        string[]? unknownReasons,
        string? reason)
    {
        if (header == null || header.Length == 0)
            return ContentMemoryStore.KeptNothingOffered;
        if (header.Length != 7)
            return ContentMemoryStore.KeptLengthMismatch;
        int rowCount = header[6];
        if (rowCount < 0)
            return ContentMemoryStore.KeptLengthMismatch;
        if (!JoinArraysMatch(rowCount, rowGuids, rowIds, rowIndices, rowKinds, rowUsable, rowReasons))
            return ContentMemoryStore.KeptLengthMismatch;
        int unknownCount = unknownGuids == null ? 0 : unknownGuids.Length;
        if (!JoinArraysMatch(unknownCount, unknownGuids, unknownIds, unknownReasons))
            return ContentMemoryStore.KeptLengthMismatch;

        var h = CopyInts(header);
        var guids = CopyInts(rowGuids);
        var ids = CopyStrings(rowIds);
        var indices = CopyInts(rowIndices);
        var kinds = CopyInts(rowKinds);
        var usable = CopyInts(rowUsable);
        var rowWhy = CopyStrings(rowReasons);
        var unkGuids = CopyInts(unknownGuids);
        var unkIds = CopyStrings(unknownIds);
        var unkWhy = CopyStrings(unknownReasons);
        string why = reason ?? "";
        lock (_gate)
        {
            _joinHeld = true;
            _joinHeader = h;
            _joinRowGuids = guids;
            _joinRowIds = ids;
            _joinRowIndices = indices;
            _joinRowKinds = kinds;
            _joinRowUsable = usable;
            _joinRowReasons = rowWhy;
            _joinUnknownGuids = unkGuids;
            _joinUnknownIds = unkIds;
            _joinUnknownReasons = unkWhy;
            _joinReason = why;
        }
        return ContentMemoryStore.Replaced;
    }

    /// <summary>
    /// Drop the stored join session. Called when the session genuinely ends (client
    /// WorldReady / rejoin), so a later Core swap cannot restore a refuse onto a new join
    /// that will receive a good <c>PrfI</c>.
    /// </summary>
    public ContentMemoryStore ClearJoinSession()
    {
        lock (_gate)
        {
            _joinHeld = false;
            _joinHeader = Array.Empty<int>();
            _joinRowGuids = Array.Empty<int>();
            _joinRowIds = Array.Empty<string>();
            _joinRowIndices = Array.Empty<int>();
            _joinRowKinds = Array.Empty<int>();
            _joinRowUsable = Array.Empty<int>();
            _joinRowReasons = Array.Empty<string>();
            _joinUnknownGuids = Array.Empty<int>();
            _joinUnknownIds = Array.Empty<string>();
            _joinUnknownReasons = Array.Empty<string>();
            _joinReason = "";
        }
        return ContentMemoryStore.Cleared;
    }

    /// <summary>Copy of the remembered join session. Arrays are not shared with the store.</summary>
    public void TakeJoinSession(
        out int[] header,
        out int[] rowGuids,
        out string[] rowIds,
        out int[] rowIndices,
        out int[] rowKinds,
        out int[] rowUsable,
        out string[] rowReasons,
        out int[] unknownGuids,
        out string[] unknownIds,
        out string[] unknownReasons,
        out string reason)
    {
        lock (_gate)
        {
            if (!_joinHeld)
            {
                header = Array.Empty<int>();
                rowGuids = Array.Empty<int>();
                rowIds = Array.Empty<string>();
                rowIndices = Array.Empty<int>();
                rowKinds = Array.Empty<int>();
                rowUsable = Array.Empty<int>();
                rowReasons = Array.Empty<string>();
                unknownGuids = Array.Empty<int>();
                unknownIds = Array.Empty<string>();
                unknownReasons = Array.Empty<string>();
                reason = "";
                return;
            }
            header = CopyInts(_joinHeader);
            rowGuids = CopyInts(_joinRowGuids);
            rowIds = CopyStrings(_joinRowIds);
            rowIndices = CopyInts(_joinRowIndices);
            rowKinds = CopyInts(_joinRowKinds);
            rowUsable = CopyInts(_joinRowUsable);
            rowReasons = CopyStrings(_joinRowReasons);
            unknownGuids = CopyInts(_joinUnknownGuids);
            unknownIds = CopyStrings(_joinUnknownIds);
            unknownReasons = CopyStrings(_joinUnknownReasons);
            reason = _joinReason ?? "";
        }
    }

    private static bool JoinArraysMatch(int expected, params Array?[] arrays)
    {
        if (expected == 0)
        {
            for (int i = 0; i < arrays.Length; i++)
            {
                if (arrays[i] != null && arrays[i]!.Length != 0) return false;
            }
            return true;
        }
        for (int i = 0; i < arrays.Length; i++)
        {
            if (arrays[i] == null || arrays[i]!.Length != expected) return false;
        }
        return true;
    }

    private static int[] CopyInts(int[]? src)
    {
        if (src == null || src.Length == 0) return Array.Empty<int>();
        var dst = new int[src.Length];
        Array.Copy(src, dst, src.Length);
        return dst;
    }

    private static string[] CopyStrings(string[]? src)
    {
        if (src == null || src.Length == 0) return Array.Empty<string>();
        var dst = new string[src.Length];
        for (int i = 0; i < src.Length; i++)
            dst[i] = src[i] ?? "";
        return dst;
    }
}

/// <summary>
/// One snapshot of cloned GUIDs, appended ItemKeyLookup GUIDs with their indices, and
/// key-table tombstones. Parallel tombstone arrays: <see cref="TombstoneHashes"/>[i] is the
/// GUID hash for <see cref="TombstoneIds"/>[i]; <see cref="TombstoneIndices"/>[i] is the
/// saved list slot or −1 when unknown.
/// </summary>
public readonly struct ContentSnapshot
{
    public static ContentSnapshot Empty { get; } = new(
        Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>(), Array.Empty<string>(),
        Array.Empty<int>(), Array.Empty<int>());

    public ContentSnapshot(int[]? clonedGuids, int[]? appendedGuids, int[]? tombstoneHashes, string[]? tombstoneIds)
        : this(clonedGuids, appendedGuids, tombstoneHashes, tombstoneIds, appendedIndices: null, tombstoneIndices: null)
    {
    }

    public ContentSnapshot(
        int[]? clonedGuids,
        int[]? appendedGuids,
        int[]? tombstoneHashes,
        string[]? tombstoneIds,
        int[]? appendedIndices,
        int[]? tombstoneIndices)
    {
        ClonedGuids = clonedGuids ?? Array.Empty<int>();
        AppendedGuids = appendedGuids ?? Array.Empty<int>();
        TombstoneHashes = tombstoneHashes ?? Array.Empty<int>();
        TombstoneIds = tombstoneIds ?? Array.Empty<string>();
        AppendedIndices = NormalizeIndices(appendedIndices, AppendedGuids.Length);
        TombstoneIndices = NormalizeIndices(tombstoneIndices, TombstoneHashes.Length);
    }

    public int[] ClonedGuids { get; }
    public int[] AppendedGuids { get; }
    public int[] AppendedIndices { get; }
    public int[] TombstoneHashes { get; }
    public string[] TombstoneIds { get; }
    public int[] TombstoneIndices { get; }

    public void Validate()
    {
        if (TombstoneHashes.Length != TombstoneIds.Length)
        {
            throw new ArgumentException(
                "TombstoneHashes (" + TombstoneHashes.Length + ") and TombstoneIds (" + TombstoneIds.Length + ") must be the same length.");
        }
        if (AppendedIndices.Length != AppendedGuids.Length)
        {
            throw new ArgumentException(
                "AppendedIndices (" + AppendedIndices.Length + ") and AppendedGuids (" + AppendedGuids.Length + ") must be the same length.");
        }
        if (TombstoneIndices.Length != TombstoneHashes.Length)
        {
            throw new ArgumentException(
                "TombstoneIndices (" + TombstoneIndices.Length + ") and TombstoneHashes (" + TombstoneHashes.Length + ") must be the same length.");
        }
    }

    /// <summary>Shallow-copy the arrays so a later Replace cannot mutate a Take result.</summary>
    public ContentSnapshot Copy()
    {
        return new ContentSnapshot(
            CopyInts(ClonedGuids),
            CopyInts(AppendedGuids),
            CopyInts(TombstoneHashes),
            CopyStrings(TombstoneIds),
            CopyInts(AppendedIndices),
            CopyInts(TombstoneIndices));
    }

    private static int[] NormalizeIndices(int[]? indices, int guidCount)
    {
        if (guidCount == 0) return Array.Empty<int>();
        if (indices == null || indices.Length == 0)
        {
            var unknown = new int[guidCount];
            for (int i = 0; i < guidCount; i++) unknown[i] = -1;
            return unknown;
        }
        if (indices.Length != guidCount)
            return indices;
        var copy = new int[guidCount];
        Array.Copy(indices, copy, guidCount);
        return copy;
    }

    private static int[] CopyInts(int[] src)
    {
        if (src == null || src.Length == 0) return Array.Empty<int>();
        var dst = new int[src.Length];
        Array.Copy(src, dst, src.Length);
        return dst;
    }

    private static string[] CopyStrings(string[] src)
    {
        if (src == null || src.Length == 0) return Array.Empty<string>();
        var dst = new string[src.Length];
        for (int i = 0; i < src.Length; i++)
            dst[i] = src[i] ?? "";
        return dst;
    }
}
