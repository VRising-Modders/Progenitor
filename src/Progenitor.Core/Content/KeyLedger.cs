using System;
using System.Collections.Generic;

namespace Progenitor.Content;

/// <summary>
/// Ordered mod-item rows for the ItemKeyLookup tail. Keyed on guid; one row per guid;
/// ordinary operation never reorders or deletes. Position in <see cref="Rows"/> is
/// position in the tail: <c>index = baseLength + position</c>.
/// </summary>
internal sealed class KeyLedger
{
    private readonly List<KeyLedgerRow> _rows = new();
    private readonly Dictionary<int, int> _byGuid = new();

    public int Count => _rows.Count;

    public IReadOnlyList<KeyLedgerRow> Rows => _rows;

    /// <summary>Position of <paramref name="guidHash"/> in the tail, or −1.</summary>
    public int PositionOf(int guidHash) =>
        _byGuid.TryGetValue(guidHash, out var i) ? i : -1;

    public bool TryGet(int guidHash, out KeyLedgerRow row)
    {
        if (_byGuid.TryGetValue(guidHash, out var i))
        {
            row = _rows[i];
            return true;
        }
        row = default;
        return false;
    }

    /// <summary>
    /// Append a live row at the tail. Refuses a second claimant for the same guid so the
    /// loser cannot shift later rows.
    /// </summary>
    public bool TryAdd(int guidHash, string? id, string? owner, out string error)
    {
        if (_byGuid.ContainsKey(guidHash))
        {
            var held = _rows[_byGuid[guidHash]];
            error = KeyAgreement.DuplicateGuidReason(guidHash, id ?? "", held.Id, held.Owner);
            return false;
        }

        var row = new KeyLedgerRow(guidHash, id ?? "", owner ?? "", tombstone: false);
        _byGuid[guidHash] = _rows.Count;
        _rows.Add(row);
        error = "";
        return true;
    }

    /// <summary>Mark an existing row reserved. Missing guid is a no-op success.</summary>
    public bool TryMarkTombstone(int guidHash)
    {
        if (!_byGuid.TryGetValue(guidHash, out var i)) return true;
        var row = _rows[i];
        _rows[i] = new KeyLedgerRow(row.GuidHash, row.Id, row.Owner, tombstone: true);
        return true;
    }
}

/// <summary>One ordered tail row. Identity is <see cref="GuidHash"/>; id and owner are diagnostics.</summary>
internal readonly struct KeyLedgerRow
{
    public KeyLedgerRow(int guidHash, string id, string owner, bool tombstone)
    {
        GuidHash = guidHash;
        Id = id ?? "";
        Owner = owner ?? "";
        Tombstone = tombstone;
    }

    public int GuidHash { get; }
    public string Id { get; }
    public string Owner { get; }
    public bool Tombstone { get; }
}

/// <summary>A local registration the client already knows, before it accepts the server order.</summary>
internal readonly struct KeyLocalEntry
{
    public KeyLocalEntry(int guidHash, string id)
    {
        GuidHash = guidHash;
        Id = id ?? "";
    }

    public int GuidHash { get; }
    public string Id { get; }
}
