using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Content;

/// <summary>
/// Why one planned key-table row is or is not usable as the named custom item.
/// </summary>
internal enum KeyAgreementKind
{
    /// <summary>Local already has this guid at the server position.</summary>
    Agree = 0,
    /// <summary>Local has this guid at a different position; rebuild uses the server index.</summary>
    Reorder = 1,
    /// <summary>Server listed a guid this side does not have. Reserved row; item unusable.</summary>
    Missing = 2,
    /// <summary>Same id with a different guid, or same guid with a different id. Unusable.</summary>
    Conflicting = 3,
    /// <summary>Local extra guid the server did not list. Must not be appended.</summary>
    Unknown = 4,
}

/// <summary>
/// Narrow client/server item-key contract: the server's ordered tail is authoritative.
/// The client rebuilds in that order. Missing, conflicting and unknown entries are
/// refused with a logged reason and stay unusable rather than occupying the wrong index.
/// Save identity is the prefab guid; this type never migrates by index.
/// </summary>
internal static class KeyAgreement
{
    public const int DefaultCapacity = 2048;

    public static string BaseLengthMismatchReason(int serverBaseLength, int clientBaseLength) =>
        "KeyAgreement: refused the server key-table order: client vanilla length "
        + clientBaseLength.ToString(CultureInfo.InvariantCulture)
        + " != server " + serverBaseLength.ToString(CultureInfo.InvariantCulture)
        + ". Custom items stay unusable rather than taking the wrong index.";

    public static string HeadroomReason(int count, int baseLength, int capacity) =>
        "KeyAgreement: refused the server key-table order: "
        + count.ToString(CultureInfo.InvariantCulture) + " row(s) at base "
        + baseLength.ToString(CultureInfo.InvariantCulture) + " would exceed capacity "
        + capacity.ToString(CultureInfo.InvariantCulture) + ".";

    public static string DuplicateGuidReason(int guidHash, string id, string heldId, string heldOwner) =>
        "KeyAgreement: refusing guid " + guidHash.ToString(CultureInfo.InvariantCulture)
        + " id='" + (id ?? "") + "': already held as '" + (heldId ?? "")
        + "' owner='" + (heldOwner ?? "") + "'.";

    public static string MissingReason(int guidHash, string id) =>
        "KeyAgreement: refusing custom item '" + (id ?? "") + "' ("
        + guidHash.ToString(CultureInfo.InvariantCulture)
        + "): missing on this side; occupying a reserved key row so later indices stay aligned; the item stays unusable.";

    public static string ConflictingReason(int serverGuid, string serverId, int localGuid, string localId) =>
        "KeyAgreement: refusing custom item '" + (serverId ?? "") + "' ("
        + serverGuid.ToString(CultureInfo.InvariantCulture)
        + "): conflicts with local '" + (localId ?? "") + "' ("
        + localGuid.ToString(CultureInfo.InvariantCulture)
        + "); the item stays unusable rather than misassigned.";

    public static string UnknownReason(int guidHash, string id) =>
        "KeyAgreement: refusing custom item '" + (id ?? "") + "' ("
        + guidHash.ToString(CultureInfo.InvariantCulture)
        + "): unknown to the server key-table order; not appended, the item stays unusable.";

    public static string PacketRefusedReason(string detail) =>
        "KeyAgreement: refused the server key-table packet: " + (detail ?? "");

    /// <summary>
    /// <c>PrfD</c>/<c>PrfK</c> arrived and no <c>PrfI</c> was accepted. Dropping the packet
    /// is not compatibility: applying in local order would let the same inventory index
    /// resolve as a different item. Custom items stay unusable this join.
    /// </summary>
    public static string MissingLedgerReason() =>
        "KeyAgreement: refused custom items this join: no PrfI key-table order was accepted "
        + "before PrfD/PrfK. Dropping PrfI is not compatibility; items stay unusable rather "
        + "than taking a local-order index the server does not mean.";

    /// <summary>
    /// Client apply with no accepted ledger and no refusal yet. Local-order append is how
    /// the same inventory index resolves as a different item; skip until <c>PrfI</c> lands
    /// or the join ends.
    /// </summary>
    public static string NoAgreedOrderReason(int guidHash, string id) =>
        "PrefabRegistry: ignoring '" + (id ?? "") + "' ("
        + guidHash.ToString(CultureInfo.InvariantCulture)
        + "): no PrfI key-table order has been accepted this join; the item stays unusable.";

    public static string DuplicateLedgerReason() =>
        "KeyAgreement: ignoring a second PrfI this join; the first accepted order stands, "
        + "and a second packet is not applied.";

    public static string LedgerAfterDefinitionReason() =>
        "KeyAgreement: refused the server key-table order: PrfI arrived after PrfD/PrfK. "
        + "Custom items stay unusable rather than being rebuilt on top of a local-order append.";

    public static string AcceptedLog(int baseLength, int count, int reordered, int reserved, int refused) =>
        "KeyAgreement: accepted server key-table order baseLength="
        + baseLength.ToString(CultureInfo.InvariantCulture)
        + " count=" + count.ToString(CultureInfo.InvariantCulture)
        + " reordered=" + reordered.ToString(CultureInfo.InvariantCulture)
        + " reserved=" + reserved.ToString(CultureInfo.InvariantCulture)
        + " refused=" + refused.ToString(CultureInfo.InvariantCulture)
        + ".";

    /// <summary>
    /// Plan the client's tail from the server's ordered rows. <paramref name="clientBaseLength"/>
    /// must equal <paramref name="serverBaseLength"/>; a disagreement is an unsupported state
    /// and refuses the whole plan. Local extras are reported as <see cref="KeyAgreementKind.Unknown"/>
    /// and are not assigned an index.
    /// </summary>
    public static KeyAgreementDecision Plan(
        int serverBaseLength,
        int clientBaseLength,
        int capacity,
        IReadOnlyList<KeyLedgerRow> serverRows,
        IReadOnlyList<KeyLocalEntry> localEntries)
    {
        serverRows ??= Array.Empty<KeyLedgerRow>();
        localEntries ??= Array.Empty<KeyLocalEntry>();
        if (capacity <= 0) capacity = DefaultCapacity;

        if (serverBaseLength < 0 || serverBaseLength >= capacity)
        {
            return KeyAgreementDecision.Refuse(
                PacketRefusedReason(
                    "baseLength " + serverBaseLength.ToString(CultureInfo.InvariantCulture)
                    + " is outside 0.." + (capacity - 1).ToString(CultureInfo.InvariantCulture) + "."));
        }

        if (clientBaseLength != serverBaseLength)
            return KeyAgreementDecision.Refuse(BaseLengthMismatchReason(serverBaseLength, clientBaseLength));

        int count = serverRows.Count;
        if (count < 0 || serverBaseLength + count > capacity)
            return KeyAgreementDecision.Refuse(HeadroomReason(count, serverBaseLength, capacity));

        var seenGuid = new Dictionary<int, int>();
        for (int i = 0; i < count; i++)
        {
            int guid = serverRows[i].GuidHash;
            if (seenGuid.TryGetValue(guid, out var prev))
            {
                return KeyAgreementDecision.Refuse(
                    DuplicateGuidReason(guid, serverRows[i].Id, serverRows[prev].Id, serverRows[prev].Owner));
            }
            seenGuid[guid] = i;
        }

        var localByGuid = new Dictionary<int, int>();
        var localById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < localEntries.Count; i++)
        {
            var local = localEntries[i];
            if (!localByGuid.ContainsKey(local.GuidHash))
                localByGuid[local.GuidHash] = i;
            if (!string.IsNullOrEmpty(local.Id) && !localById.ContainsKey(local.Id))
                localById[local.Id] = i;
        }

        var planned = new List<KeyPlannedRow>(count);
        var unknown = new List<KeyPlannedRow>();
        int reordered = 0;
        int reserved = 0;
        int refused = 0;

        for (int i = 0; i < count; i++)
        {
            var server = serverRows[i];
            int index = KeyIndexPlan.Index(serverBaseLength, i);
            if (localByGuid.TryGetValue(server.GuidHash, out var localAt))
            {
                var local = localEntries[localAt];
                if (!string.IsNullOrEmpty(server.Id)
                    && !string.IsNullOrEmpty(local.Id)
                    && !string.Equals(server.Id, local.Id, StringComparison.Ordinal))
                {
                    planned.Add(new KeyPlannedRow(
                        server.GuidHash, server.Id, index, KeyAgreementKind.Conflicting,
                        ConflictingReason(server.GuidHash, server.Id, local.GuidHash, local.Id),
                        usable: false));
                    reserved++;
                    refused++;
                    continue;
                }

                var kind = localAt == i ? KeyAgreementKind.Agree : KeyAgreementKind.Reorder;
                if (kind == KeyAgreementKind.Reorder) reordered++;
                planned.Add(new KeyPlannedRow(
                    server.GuidHash,
                    FirstNonEmpty(server.Id, local.Id),
                    index,
                    kind,
                    "",
                    usable: !server.Tombstone));
                continue;
            }

            if (!string.IsNullOrEmpty(server.Id)
                && localById.TryGetValue(server.Id, out var byId)
                && localEntries[byId].GuidHash != server.GuidHash)
            {
                var local = localEntries[byId];
                planned.Add(new KeyPlannedRow(
                    server.GuidHash, server.Id, index, KeyAgreementKind.Conflicting,
                    ConflictingReason(server.GuidHash, server.Id, local.GuidHash, local.Id),
                    usable: false));
                reserved++;
                refused++;
                continue;
            }

            planned.Add(new KeyPlannedRow(
                server.GuidHash, server.Id, index, KeyAgreementKind.Missing,
                MissingReason(server.GuidHash, server.Id),
                usable: false));
            reserved++;
            refused++;
        }

        for (int i = 0; i < localEntries.Count; i++)
        {
            var local = localEntries[i];
            if (seenGuid.ContainsKey(local.GuidHash)) continue;
            unknown.Add(new KeyPlannedRow(
                local.GuidHash, local.Id, -1, KeyAgreementKind.Unknown,
                UnknownReason(local.GuidHash, local.Id),
                usable: false));
            refused++;
        }

        return KeyAgreementDecision.Accept(
            serverBaseLength,
            planned,
            unknown,
            AcceptedLog(serverBaseLength, count, reordered, reserved, refused));
    }

    private static string FirstNonEmpty(string a, string b) =>
        !string.IsNullOrEmpty(a) ? a : (b ?? "");
}

/// <summary>One row of a <see cref="KeyAgreement.Plan"/> result.</summary>
internal readonly struct KeyPlannedRow
{
    public KeyPlannedRow(
        int guidHash, string id, int index, KeyAgreementKind kind, string reason, bool usable)
    {
        GuidHash = guidHash;
        Id = id ?? "";
        Index = index;
        Kind = kind;
        Reason = reason ?? "";
        Usable = usable;
    }

    public int GuidHash { get; }
    public string Id { get; }
    public int Index { get; }
    public KeyAgreementKind Kind { get; }
    public string Reason { get; }
    public bool Usable { get; }

    /// <summary>True when this row must occupy the server index (live or reserved).</summary>
    public bool OccupiesRow => Kind != KeyAgreementKind.Unknown;
}

/// <summary>Whole-plan result. <see cref="Accepted"/> false means append nothing this join.</summary>
internal sealed class KeyAgreementDecision
{
    private KeyAgreementDecision(
        bool accepted,
        int baseLength,
        IReadOnlyList<KeyPlannedRow> rows,
        IReadOnlyList<KeyPlannedRow> unknown,
        string reason)
    {
        Accepted = accepted;
        BaseLength = baseLength;
        Rows = rows ?? Array.Empty<KeyPlannedRow>();
        Unknown = unknown ?? Array.Empty<KeyPlannedRow>();
        Reason = reason ?? "";
    }

    public bool Accepted { get; }
    public int BaseLength { get; }
    public IReadOnlyList<KeyPlannedRow> Rows { get; }
    public IReadOnlyList<KeyPlannedRow> Unknown { get; }
    public string Reason { get; }

    public static KeyAgreementDecision Refuse(string reason) =>
        new KeyAgreementDecision(false, -1, Array.Empty<KeyPlannedRow>(), Array.Empty<KeyPlannedRow>(), reason);

    public static KeyAgreementDecision Accept(
        int baseLength,
        IReadOnlyList<KeyPlannedRow> rows,
        IReadOnlyList<KeyPlannedRow> unknown,
        string log) =>
        new KeyAgreementDecision(true, baseLength, rows, unknown, log);

    public bool TryIndexOf(int guidHash, out int index)
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].GuidHash != guidHash) continue;
            index = Rows[i].Index;
            return Rows[i].OccupiesRow;
        }
        index = -1;
        return false;
    }

    public bool IsUsable(int guidHash)
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            if (Rows[i].GuidHash == guidHash) return Rows[i].Usable;
        }
        return false;
    }

    public bool IsUnknown(int guidHash)
    {
        for (int i = 0; i < Unknown.Count; i++)
        {
            if (Unknown[i].GuidHash == guidHash) return true;
        }
        return false;
    }
}
