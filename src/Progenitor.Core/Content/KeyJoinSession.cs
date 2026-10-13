using System;
using System.Collections.Generic;
using System.Reflection;

namespace Progenitor.Content;

/// <summary>
/// Join-time item-key admission. Dropping an unhandled <c>PrfI</c> is not compatibility:
/// a later <c>PrfD</c>/<c>PrfK</c> must not append custom items in local registration order,
/// because that is how two sides with the same guids decode an inventory index as different
/// items. This session either accepts the server order or refuses every custom-item append
/// for the join. It does not kick the connection: a vanilla peer with no Progenitor content
/// never runs this type, and a Progenitor client that never receives a ledger keeps vanilla
/// indices only — custom items stay unusable rather than occupying the wrong row.
/// </summary>
internal sealed class KeyJoinSession
{
    private KeyAgreementDecision? _plan;
    private bool _refused;
    private bool _sawLedger;
    private bool _sawDefinition;
    private string _reason = "";

    public bool Refused => _refused;
    public bool SawLedger => _sawLedger;
    public bool SawDefinition => _sawDefinition;
    public KeyAgreementDecision? Plan => _plan;
    public string Reason => _reason;

    /// <summary>
    /// This join accepted a <c>PrfI</c> order. Distinct from <see cref="Refused"/> and
    /// from idle (neither accepted nor refused — no agreed order yet).
    /// </summary>
    public bool HasAgreedOrder => _plan != null && _plan.Accepted;

    /// <summary>
    /// True when a custom item must not take a key-table row this join. The server never
    /// sets this: it is the authority and does not consume <c>PrfI</c>. A client sets it
    /// when the ledger is corrupt, missing, late, or conflicting so a local-order append
    /// cannot misassign an index.
    /// </summary>
    public bool ShouldSkipItem => _refused;

    /// <summary>
    /// Packed header for a Bootstrap <c>ContentMemory</c> join-session row. Version, then
    /// refused / sawLedger / sawDefinition / accepted as 0 or 1, then baseLength and row
    /// count. Primitive ints only — the store cannot pin this collectible generation.
    /// </summary>
    public const int SnapshotVersion = 1;
    public const int SnapshotHeaderLength = 7;

    public void Reset()
    {
        _plan = null;
        _refused = false;
        _sawLedger = false;
        _sawDefinition = false;
        _reason = "";
    }

    /// <summary>
    /// Production skip used by <c>PrefabRegistry.ApplyOne</c>. Buffs are never skipped.
    /// A refused join skips every custom item. An accepted plan skips only unknown and
    /// conflicting rows. <paramref name="requireAgreedOrder"/> is the client live-world
    /// gate: idle (<c>plan == null</c>, not refused) must not append in local order.
    /// WorldReady keeps the gate closed and passes false so <c>NotePending</c> still
    /// runs. The server, and a host/single-player server process, pass false — they are
    /// the authority and have no <c>PrfI</c> to wait for.
    /// </summary>
    public bool ShouldSkipItemAppend(
        PrefabKind kind, int guidHash, string id, bool requireAgreedOrder, out string reason) =>
        ShouldSkipItemAppend(kind, _refused, _plan, guidHash, id, requireAgreedOrder, out reason);

    /// <summary>
    /// Game-free form of
    /// <see cref="ShouldSkipItemAppend(PrefabKind, int, string, bool, out string)"/>
    /// so tests drive the same predicate <c>ApplyOne</c> uses, including across a captured
    /// Core-swap restore.
    /// </summary>
    public static bool ShouldSkipItemAppend(
        PrefabKind kind,
        bool refused,
        KeyAgreementDecision? plan,
        int guidHash,
        string id,
        out string reason) =>
        ShouldSkipItemAppend(kind, refused, plan, guidHash, id, requireAgreedOrder: false, out reason);

    public static bool ShouldSkipItemAppend(
        PrefabKind kind,
        bool refused,
        KeyAgreementDecision? plan,
        int guidHash,
        string id,
        bool requireAgreedOrder,
        out string reason)
    {
        reason = "";
        if (!PrefabApplyBookkeeping.ShouldAppendItemKey(kind)) return false;
        if (refused)
        {
            reason = "PrefabRegistry: ignoring '" + (id ?? "") + "' (" + guidHash
                + "): key-table agreement was refused this join; the item stays unusable.";
            return true;
        }
        if (plan == null || !plan.Accepted)
        {
            if (requireAgreedOrder)
            {
                reason = KeyAgreement.NoAgreedOrderReason(guidHash, id);
                return true;
            }
            return false;
        }
        if (plan.IsUnknown(guidHash))
        {
            reason = KeyAgreement.UnknownReason(guidHash, id);
            return true;
        }
        for (int i = 0; i < plan.Rows.Count; i++)
        {
            var row = plan.Rows[i];
            if (row.GuidHash != guidHash) continue;
            if (row.Kind == KeyAgreementKind.Conflicting
                && !string.Equals(id, row.Id, StringComparison.Ordinal))
            {
                reason = row.Reason;
                return true;
            }
            return false;
        }
        return false;
    }

    /// <summary>
    /// Plain state for Bootstrap <c>ContentMemory</c>. A later Core generation restores
    /// this before <c>NotifyLiveWorldAfterCoreSwap</c> so a refused join stays refused
    /// (and an accepted ledger stays in server order) instead of appending in local
    /// registration order. No entity handle.
    /// </summary>
    public KeyJoinSnapshot Capture()
    {
        bool accepted = _plan != null && _plan.Accepted;
        var rows = accepted ? _plan!.Rows : Array.Empty<KeyPlannedRow>();
        var unknown = accepted ? _plan!.Unknown : Array.Empty<KeyPlannedRow>();
        return new KeyJoinSnapshot(
            refused: _refused,
            sawLedger: _sawLedger,
            sawDefinition: _sawDefinition,
            accepted: accepted,
            baseLength: accepted ? _plan!.BaseLength : -1,
            reason: _reason,
            rows: rows,
            unknown: unknown);
    }

    /// <summary>
    /// Rehydrate <see cref="Capture"/> into this generation's session. Idle (never
    /// joined) restores to an empty session so a healthy later <c>PrfI</c> is unaffected.
    /// </summary>
    public void Restore(KeyJoinSnapshot snapshot)
    {
        if (!snapshot.Held)
        {
            Reset();
            return;
        }

        _sawLedger = snapshot.SawLedger;
        _sawDefinition = snapshot.SawDefinition;
        _reason = snapshot.Reason ?? "";
        if (snapshot.Refused)
        {
            _refused = true;
            _plan = null;
            return;
        }

        _refused = false;
        if (snapshot.Accepted)
        {
            _plan = snapshot.ToDecision();
            return;
        }

        _plan = null;
    }

    /// <summary>
    /// First (or only) <c>PrfI</c> of the join. A second packet is ignored so a good first
    /// order is not rebuilt. A ledger after a definition packet is refused so a dropped
    /// <c>PrfI</c> cannot be "recovered" by applying local order first.
    /// </summary>
    public bool TryAcceptLedger(KeyAgreementDecision? decision, out string log)
    {
        if (_sawLedger)
        {
            log = KeyAgreement.DuplicateLedgerReason();
            return false;
        }

        if (_sawDefinition)
        {
            Refuse(KeyAgreement.LedgerAfterDefinitionReason());
            log = _reason;
            return false;
        }

        _sawLedger = true;
        if (decision == null || !decision.Accepted)
        {
            Refuse(decision?.Reason ?? KeyAgreement.PacketRefusedReason("empty decision"));
            log = _reason;
            return false;
        }

        _plan = decision;
        _refused = false;
        _reason = decision.Reason ?? "";
        log = _reason;
        return true;
    }

    /// <summary>
    /// A truncated, oversized or corrupt payload. The join has seen a ledger attempt, so a
    /// later <c>PrfD</c> cannot dump local order.
    /// </summary>
    public void RefusePacket(string reason)
    {
        _sawLedger = true;
        Refuse(reason);
    }

    /// <summary>
    /// A <c>PrfD</c> or <c>PrfK</c> arrived. If no ledger was accepted, custom items are
    /// refused for the rest of the join. Returns true when this call newly refuses.
    /// </summary>
    public bool NoteDefinitionPacket(out string reason)
    {
        _sawDefinition = true;
        reason = "";
        if (_sawLedger && !_refused) return false;
        if (_refused) return false;
        Refuse(KeyAgreement.MissingLedgerReason());
        reason = _reason;
        return true;
    }

    /// <summary>
    /// Resolve a guid to the server index only when this join accepted that row as usable.
    /// Missing, conflicting, unknown, and any refused join return false — the guid does not
    /// decode as some other item's index.
    /// </summary>
    public bool TryResolveGuid(int guidHash, out int index)
    {
        index = -1;
        if (_refused || _plan == null || !_plan.Accepted) return false;
        if (!_plan.TryIndexOf(guidHash, out index)) return false;
        if (!_plan.IsUsable(guidHash))
        {
            index = -1;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Decode an ItemKeyLookup index to the guid this join assigned there. A refused join,
    /// a reserved/unusable row, and an index the plan does not own all return false — the
    /// caller must not treat the slot as another custom item.
    /// </summary>
    public bool TryDecodeIndex(int index, out int guidHash)
    {
        guidHash = 0;
        if (_refused || _plan == null || !_plan.Accepted) return false;
        for (int i = 0; i < _plan.Rows.Count; i++)
        {
            var row = _plan.Rows[i];
            if (row.Index != index) continue;
            if (!row.Usable || !row.OccupiesRow) return false;
            guidHash = row.GuidHash;
            return true;
        }
        return false;
    }

    /// <summary>
    /// What appending in local registration order would have done. Used by tests to show
    /// the wrong-item outcome the session must make impossible.
    /// </summary>
    public static int NaiveLocalIndex(int baseLength, IReadOnlyList<int> localOrder, int guidHash)
    {
        if (localOrder == null || baseLength < 0) return -1;
        for (int i = 0; i < localOrder.Count; i++)
        {
            if (localOrder[i] == guidHash) return KeyIndexPlan.Index(baseLength, i);
        }
        return -1;
    }

    public static int NaiveDecodeIndex(int baseLength, IReadOnlyList<int> localOrder, int index)
    {
        if (localOrder == null || baseLength < 0) return 0;
        int position = index - baseLength;
        if (position < 0 || position >= localOrder.Count) return 0;
        return localOrder[position];
    }

    private void Refuse(string reason)
    {
        _refused = true;
        _plan = null;
        _reason = reason ?? "";
    }
}

/// <summary>
/// Primitive capture of <see cref="KeyJoinSession"/> for Bootstrap ContentMemory.
/// Ints and strings only; never an entity id or a Core type the store would pin.
/// </summary>
internal readonly struct KeyJoinSnapshot
{
    public KeyJoinSnapshot(
        bool refused,
        bool sawLedger,
        bool sawDefinition,
        bool accepted,
        int baseLength,
        string reason,
        IReadOnlyList<KeyPlannedRow> rows,
        IReadOnlyList<KeyPlannedRow> unknown)
    {
        Held = true;
        Refused = refused;
        SawLedger = sawLedger;
        SawDefinition = sawDefinition;
        Accepted = accepted && !refused;
        BaseLength = baseLength;
        Reason = reason ?? "";
        Rows = rows ?? Array.Empty<KeyPlannedRow>();
        Unknown = unknown ?? Array.Empty<KeyPlannedRow>();
    }

    public static KeyJoinSnapshot Empty { get; } = default;

    public bool Held { get; }
    public bool Refused { get; }
    public bool SawLedger { get; }
    public bool SawDefinition { get; }
    public bool Accepted { get; }
    public int BaseLength { get; }
    public string Reason { get; }
    public IReadOnlyList<KeyPlannedRow> Rows { get; }
    public IReadOnlyList<KeyPlannedRow> Unknown { get; }

    public KeyAgreementDecision? ToDecision()
    {
        if (!Accepted) return null;
        return KeyAgreementDecision.Accept(BaseLength, Rows, Unknown, Reason);
    }

    public void Pack(
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
        header = new[]
        {
            KeyJoinSession.SnapshotVersion,
            Refused ? 1 : 0,
            SawLedger ? 1 : 0,
            SawDefinition ? 1 : 0,
            Accepted ? 1 : 0,
            BaseLength,
            Rows == null ? 0 : Rows.Count,
        };
        PackRows(Rows, out rowGuids, out rowIds, out rowIndices, out rowKinds, out rowUsable, out rowReasons);
        PackUnknown(Unknown, out unknownGuids, out unknownIds, out unknownReasons);
        reason = Reason ?? "";
    }

    public static bool TryUnpack(
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
        string? reason,
        out KeyJoinSnapshot snapshot)
    {
        snapshot = Empty;
        if (header == null || header.Length != KeyJoinSession.SnapshotHeaderLength)
            return false;
        if (header[0] != KeyJoinSession.SnapshotVersion)
            return false;
        int rowCount = header[6];
        if (rowCount < 0) return false;
        if (!SameLen(rowCount, rowGuids, rowIds, rowIndices, rowKinds, rowUsable, rowReasons))
            return false;
        int unknownCount = unknownGuids == null ? 0 : unknownGuids.Length;
        if (!SameLen(unknownCount, unknownGuids, unknownIds, unknownReasons))
            return false;

        var rows = new KeyPlannedRow[rowCount];
        for (int i = 0; i < rowCount; i++)
        {
            rows[i] = new KeyPlannedRow(
                rowGuids![i],
                rowIds![i],
                rowIndices![i],
                (KeyAgreementKind)rowKinds![i],
                rowReasons![i],
                usable: rowUsable![i] != 0);
        }
        var unknown = new KeyPlannedRow[unknownCount];
        for (int i = 0; i < unknownCount; i++)
        {
            unknown[i] = new KeyPlannedRow(
                unknownGuids![i],
                unknownIds![i],
                -1,
                KeyAgreementKind.Unknown,
                unknownReasons![i],
                usable: false);
        }
        snapshot = new KeyJoinSnapshot(
            refused: header[1] != 0,
            sawLedger: header[2] != 0,
            sawDefinition: header[3] != 0,
            accepted: header[4] != 0,
            baseLength: header[5],
            reason: reason ?? "",
            rows: rows,
            unknown: unknown);
        return true;
    }

    private static void PackRows(
        IReadOnlyList<KeyPlannedRow>? rows,
        out int[] guids,
        out string[] ids,
        out int[] indices,
        out int[] kinds,
        out int[] usable,
        out string[] reasons)
    {
        int n = rows == null ? 0 : rows.Count;
        if (n == 0)
        {
            guids = Array.Empty<int>();
            ids = Array.Empty<string>();
            indices = Array.Empty<int>();
            kinds = Array.Empty<int>();
            usable = Array.Empty<int>();
            reasons = Array.Empty<string>();
            return;
        }
        guids = new int[n];
        ids = new string[n];
        indices = new int[n];
        kinds = new int[n];
        usable = new int[n];
        reasons = new string[n];
        for (int i = 0; i < n; i++)
        {
            var row = rows![i];
            guids[i] = row.GuidHash;
            ids[i] = row.Id ?? "";
            indices[i] = row.Index;
            kinds[i] = (int)row.Kind;
            usable[i] = row.Usable ? 1 : 0;
            reasons[i] = row.Reason ?? "";
        }
    }

    private static void PackUnknown(
        IReadOnlyList<KeyPlannedRow>? unknown,
        out int[] guids,
        out string[] ids,
        out string[] reasons)
    {
        int n = unknown == null ? 0 : unknown.Count;
        if (n == 0)
        {
            guids = Array.Empty<int>();
            ids = Array.Empty<string>();
            reasons = Array.Empty<string>();
            return;
        }
        guids = new int[n];
        ids = new string[n];
        reasons = new string[n];
        for (int i = 0; i < n; i++)
        {
            guids[i] = unknown![i].GuidHash;
            ids[i] = unknown![i].Id ?? "";
            reasons[i] = unknown![i].Reason ?? "";
        }
    }

    private static bool SameLen(int expected, params Array?[] arrays)
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
}

/// <summary>
/// Bootstrap <c>ContentMemory</c> join-session handoff. Looked up by method name so a
/// Core built against a newer Bootstrap still loads into an older one; that pair must
/// fail closed (refuse custom items), not idle-then-append in local order.
/// </summary>
internal static class KeyJoinHandoff
{
    public const string ReplaceMethodName = "ReplaceJoinSession";
    public const string TakeMethodName = "TakeJoinSession";
    public const string ClearMethodName = "ClearJoinSession";

    /// <summary>
    /// Same shape as the live-refresh fingerprint miss at
    /// <c>PrefabRegistry.TryReadFingerprintsFromMemory</c> (the
    /// "this Bootstrap has no TakeFingerprints; … until the process is restarted" line).
    /// </summary>
    public static string MissingMethodReason(string methodName) =>
        "PrefabRegistry: this Bootstrap has no " + (methodName ?? "")
        + "; the key-table join-session handoff is unavailable until the process is restarted."
        + " Custom items stay unusable rather than taking a local-order index.";

    public static string RestoreFailedReason(string detail) =>
        "PrefabRegistry: key-table join session rehydrate failed (" + (detail ?? "")
        + "); custom items stay unusable rather than taking a local-order index.";

    public static MethodInfo? FindMethod(Type type, string name)
    {
        if (type == null || string.IsNullOrEmpty(name)) return null;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
        try
        {
            var direct = type.GetMethod(name, flags);
            if (direct != null) return direct;
        }
        catch (AmbiguousMatchException)
        {
            // Unique names in ContentMemory; scan if the binder cannot pick one.
        }

        MethodInfo[] methods;
        try { methods = type.GetMethods(flags); }
        catch { return null; }
        MethodInfo? found = null;
        for (int i = 0; i < methods.Length; i++)
        {
            if (!string.Equals(methods[i].Name, name, StringComparison.Ordinal)) continue;
            if (found != null) return found;
            found = methods[i];
        }
        return found;
    }

    /// <summary>
    /// Restore <paramref name="session"/> from a ContentMemory-shaped object.
    /// <paramref name="findMethod"/> returning null, a thrown invoke, or an unpack
    /// failure is not idle: when <paramref name="failClosed"/> the session is refused.
    /// An empty stored header (nothing written) refuses only when
    /// <paramref name="liveWorld"/> is also set — a cold client start has no join yet.
    /// The server passes <paramref name="failClosed"/> false and is unaffected.
    /// </summary>
    public static bool TryRestoreInto(
        object? memory,
        Func<Type, string, MethodInfo?>? findMethod,
        KeyJoinSession session,
        bool failClosed,
        bool liveWorld,
        out string log)
    {
        log = "";
        if (session == null) return false;
        findMethod ??= FindMethod;

        if (!TryTakeSnapshot(memory, findMethod, out var snapshot, out var error, out bool nothingStored))
        {
            bool refuse = failClosed && (!nothingStored || liveWorld);
            if (refuse)
            {
                string why = string.IsNullOrEmpty(error)
                    ? RestoreFailedReason("no stored join session")
                    : error;
                session.RefusePacket(why);
                log = why;
                return false;
            }
            log = error ?? "";
            return false;
        }

        session.Restore(snapshot);
        log = "PrefabRegistry: rehydrated key-table join session refused="
            + (snapshot.Refused ? "true" : "false")
            + " accepted=" + (snapshot.Accepted ? "true" : "false")
            + " rows=" + (snapshot.Rows == null ? 0 : snapshot.Rows.Count) + ".";
        return true;
    }

    private static bool TryTakeSnapshot(
        object? memory,
        Func<Type, string, MethodInfo?> findMethod,
        out KeyJoinSnapshot snapshot,
        out string error,
        out bool nothingStored)
    {
        snapshot = KeyJoinSnapshot.Empty;
        error = "";
        nothingStored = false;
        if (memory == null)
        {
            error = RestoreFailedReason("ContentMemory lookup returned null");
            return false;
        }

        MethodInfo? mi;
        try
        {
            mi = findMethod(memory.GetType(), TakeMethodName);
        }
        catch (Exception e)
        {
            error = RestoreFailedReason(e.Message);
            return false;
        }
        if (mi == null)
        {
            error = MissingMethodReason(TakeMethodName);
            return false;
        }

        object?[] args =
        {
            null, null, null, null, null, null, null, null, null, null, null,
        };
        try
        {
            mi.Invoke(memory, args);
        }
        catch (Exception e)
        {
            string detail = e.InnerException != null ? e.InnerException.Message : e.Message;
            error = RestoreFailedReason(detail);
            return false;
        }

        var header = args[0] as int[] ?? Array.Empty<int>();
        if (header.Length == 0)
        {
            nothingStored = true;
            return false;
        }

        if (!KeyJoinSnapshot.TryUnpack(
            header,
            args[1] as int[],
            args[2] as string[],
            args[3] as int[],
            args[4] as int[],
            args[5] as int[],
            args[6] as string[],
            args[7] as int[],
            args[8] as string[],
            args[9] as string[],
            args[10] as string,
            out snapshot))
        {
            error = RestoreFailedReason("the handoff arrays disagree");
            return false;
        }
        return true;
    }
}
