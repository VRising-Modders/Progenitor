using System;
using System.Collections.Generic;

namespace Progenitor.Content;

internal readonly record struct DataPackItemIdentity(string Id, int Guid);
internal readonly record struct RegisteredItemIdentity(string Id, int Guid, object Owner);

/// <summary>Check the registry's real collision key before a pack changes any registrations.</summary>
internal static class DataPackRegistrationRules
{
    internal static bool TryValidate(
        IReadOnlyList<DataPackItemIdentity> proposed,
        IReadOnlyList<RegisteredItemIdentity> registered,
        object? previousOwner,
        out string reason)
    {
        reason = "";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var guids = new HashSet<int>();
        foreach (var item in proposed)
        {
            if (item.Guid == 0 || !ids.Add(item.Id) || !guids.Add(item.Guid))
            {
                reason = "Pack items must have distinct IDs and distinct nonzero GUID hashes: " + item.Id;
                return false;
            }
            foreach (var existing in registered)
            {
                if (existing.Id != item.Id && existing.Guid != item.Guid) continue;
                if (previousOwner != null && ReferenceEquals(existing.Owner, previousOwner)
                    && existing.Id == item.Id && existing.Guid == item.Guid) continue;
                reason = "An item ID or GUID hash is already owned: " + item.Id;
                return false;
            }
        }
        return true;
    }
}

/// <summary>Project the complete join state before changing any live pack registrations.</summary>
internal static class DataPackAdmissionRules
{
    internal static bool TryValidate(
        IReadOnlyList<DataPackItemIdentity> proposed,
        IReadOnlyList<DataPackItemIdentity> knownDefinitionsAndTombstones,
        IReadOnlyList<DataPackItemIdentity> pendingItems,
        IReadOnlyList<KeyLedgerRow> historicalRows,
        int baseLength, int capacity, out string reason, int maxIdentities = PrefabSyncBudget.DefaultMaxPerJoin)
    {
        var identities = new HashSet<int>();
        var rows = new List<KeyLedgerRow>(historicalRows);
        var rowIndices = new Dictionary<int, int>();
        for (int i = 0; i < rows.Count; i++)
        {
            identities.Add(rows[i].GuidHash);
            rowIndices[rows[i].GuidHash] = i;
        }
        foreach (var known in knownDefinitionsAndTombstones) identities.Add(known.Guid);
        // Include definitions whose item-key append is still waiting for a frame. A second
        // pack in the same scan must not consume the same remaining capacity.
        foreach (var item in pendingItems) Project(item);
        foreach (var item in proposed) Project(item);
        if (identities.Count > maxIdentities)
        {
            reason = "Pack requires " + identities.Count + " distinct client prefab identities including retained tombstones; limit is "
                + maxIdentities + ".";
            return false;
        }
        if (!KeyLedgerPacket.TryValidateBounds(baseLength, capacity, rows, out var ledgerError))
        {
            reason = "Pack would make the retained item-key ledger unsynchronizable: " + ledgerError;
            return false;
        }
        reason = "";
        return true;

        void Project(DataPackItemIdentity item)
        {
            identities.Add(item.Guid);
            var row = new KeyLedgerRow(item.Guid, item.Id, "", false);
            if (rowIndices.TryGetValue(item.Guid, out int index)) rows[index] = row;
            else { rowIndices[item.Guid] = rows.Count; rows.Add(row); }
        }
    }
}
