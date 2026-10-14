using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Progenitor.Castles;

namespace Progenitor.Materials;

/// <summary>
/// One player's material view as sent from the server: the pool's spendable totals for one
/// operation, stamped with world epoch, provider revision and a per-player sequence. A cleared view
/// (<see cref="Cleared"/>) tells the client to show the game's own counts again.
/// </summary>
internal sealed class MaterialView
{
    internal MaterialOperation Operation { get; }
    internal int Epoch { get; }
    internal long Revision { get; }
    internal uint Sequence { get; }
    internal int Territory { get; }
    internal bool Cleared { get; }
    internal bool Truncated { get; }
    internal IReadOnlyList<MaterialLine> Totals { get; }

    internal MaterialView(MaterialOperation operation, int epoch, long revision, uint sequence, int territory,
        bool cleared, bool truncated, IReadOnlyList<MaterialLine> totals)
    {
        Operation = operation;
        Epoch = epoch;
        Revision = revision;
        Sequence = sequence;
        Territory = territory;
        Cleared = cleared;
        Truncated = truncated;
        Totals = totals;
    }
}

/// <summary>
/// Wire format of <see cref="MaterialView"/> on Core's reserved event <see cref="EventId"/>
/// (<c>'PrMv'</c>). Little endian: version, operation, flags, 0, epoch (int), revision (long),
/// sequence (uint), territory (int), count (int), then count × (item int, amount int).
/// </summary>
internal static class MaterialViewWire
{
    /// <summary><c>'PrMv'</c>: inside Core's reserved range, so no plugin can send or take it.</summary>
    internal const int EventId = 0x50724D76;

    internal const byte Version = 1;
    internal const int HeaderBytes = 28;

    /// <summary>Most rows one packet carries; more are dropped and the view is flagged truncated.</summary>
    internal const int MaxRows = 1500;

    private const byte FlagCleared = 1;
    private const byte FlagTruncated = 2;

    internal static byte[] Encode(MaterialView view)
    {
        int rows = Math.Min(view.Totals.Count, MaxRows);
        bool truncated = view.Truncated || view.Totals.Count > MaxRows;
        var buf = new byte[HeaderBytes + rows * 8];
        buf[0] = Version;
        buf[1] = (byte)view.Operation;
        buf[2] = (byte)((view.Cleared ? FlagCleared : 0) | (truncated ? FlagTruncated : 0));
        var s = buf.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(4), view.Epoch);
        BinaryPrimitives.WriteInt64LittleEndian(s.Slice(8), view.Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(16), view.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(20), view.Territory);
        BinaryPrimitives.WriteInt32LittleEndian(s.Slice(24), rows);
        for (int i = 0; i < rows; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(s.Slice(HeaderBytes + i * 8), view.Totals[i].Item);
            BinaryPrimitives.WriteInt32LittleEndian(s.Slice(HeaderBytes + i * 8 + 4), view.Totals[i].Amount);
        }
        return buf;
    }

    /// <summary>Decode; false (with a reason) for a short, oversized, unknown-version or malformed packet.</summary>
    internal static bool TryDecode(ReadOnlySpan<byte> s, out MaterialView? view, out string reason)
    {
        view = null;
        if (s.Length < HeaderBytes) { reason = "short header " + s.Length; return false; }
        if (s[0] != Version) { reason = "version " + s[0]; return false; }
        if (s[1] > (byte)MaterialOperation.Other) { reason = "operation " + s[1]; return false; }
        int count = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(24));
        if (count < 0 || count > MaxRows) { reason = "row count " + count; return false; }
        if (s.Length != HeaderBytes + count * 8) { reason = "length " + s.Length + " for " + count + " rows"; return false; }
        var rows = new MaterialLine[count];
        for (int i = 0; i < count; i++)
        {
            int item = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(HeaderBytes + i * 8));
            int amount = BinaryPrimitives.ReadInt32LittleEndian(s.Slice(HeaderBytes + i * 8 + 4));
            if (amount < 0) { reason = "negative amount"; return false; }
            rows[i] = new MaterialLine(item, amount);
        }
        view = new MaterialView((MaterialOperation)s[1],
            BinaryPrimitives.ReadInt32LittleEndian(s.Slice(4)),
            BinaryPrimitives.ReadInt64LittleEndian(s.Slice(8)),
            BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(16)),
            BinaryPrimitives.ReadInt32LittleEndian(s.Slice(20)),
            (s[2] & FlagCleared) != 0, (s[2] & FlagTruncated) != 0, rows);
        reason = "";
        return true;
    }
}

/// <summary>
/// Server side: which view each player last received, so an unchanged view is not resent and a
/// changed one gets the next sequence number. Game-free.
/// </summary>
internal sealed class MaterialViewBook
{
    private readonly Dictionary<(CastleId, MaterialOperation), ulong> _last = new();
    private readonly Dictionary<CastleId, uint> _seq = new();
    private readonly HashSet<(CastleId, MaterialOperation)> _retry = new();

    /// <summary>
    /// True when <paramref name="fingerprint"/> differs from what this player last got for this
    /// operation (or <paramref name="force"/>); then <paramref name="sequence"/> is the next number.
    /// </summary>
    internal bool ShouldSend(CastleId user, MaterialOperation operation, ulong fingerprint, bool force, out uint sequence)
    {
        sequence = 0;
        if (!force && !_retry.Remove((user, operation)) && _last.TryGetValue((user, operation), out var prev) && prev == fingerprint) return false;
        _retry.Remove((user, operation));
        _last[(user, operation)] = fingerprint;
        _seq.TryGetValue(user, out uint s);
        sequence = ++s;
        _seq[user] = s;
        return true;
    }

    /// <summary>True when this player has been sent a non-cleared view for this operation.</summary>
    internal bool HasView(CastleId user, MaterialOperation operation) =>
        _retry.Contains((user, operation)) || (_last.TryGetValue((user, operation), out var f) && f != ClearedFingerprint);

    internal void Retry(CastleId user, MaterialOperation operation) => _retry.Add((user, operation));

    /// <summary>The fingerprint of a cleared view.</summary>
    internal const ulong ClearedFingerprint = 0;

    /// <summary>Forget a player (disconnect). The sequence is kept so a reconnect never goes backwards.</summary>
    internal void Forget(CastleId user)
    {
        _retry.Remove((user, MaterialOperation.Build));
        _retry.Remove((user, MaterialOperation.Craft));
        _last.Remove((user, MaterialOperation.Build));
        _last.Remove((user, MaterialOperation.Craft));
    }

    /// <summary>Forget every player's last view (world load, provider change); sequences continue.</summary>
    internal void ForgetViews() => _last.Clear();

    internal void Clear()
    {
        _retry.Clear();
        _last.Clear();
        _seq.Clear();
    }

    /// <summary>FNV-1a over territory, revision and the sorted totals. Never returns <see cref="ClearedFingerprint"/>.</summary>
    internal static ulong Fingerprint(int territory, long revision, IReadOnlyList<MaterialLine> totals)
    {
        ulong h = 14695981039346656037UL;
        void Mix(long v)
        {
            for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (i * 8)); h *= 1099511628211UL; }
        }
        Mix(territory);
        Mix(revision);
        foreach (var t in totals) { Mix(t.Item); Mix(t.Amount); }
        return h == ClearedFingerprint ? 1 : h;
    }
}

/// <summary>
/// Client side: the latest view per operation, newest sequence wins within a world epoch. Game-free.
/// </summary>
internal sealed class MaterialOverlayStore
{
    private readonly Dictionary<MaterialOperation, (MaterialView View, Dictionary<int, int> Totals)> _views = new();
    private uint _lastSequence;
    private int _epoch = int.MinValue;

    /// <summary>Apply a received view. False when it is older than one already applied.</summary>
    internal bool Apply(MaterialView view)
    {
        if (view.Epoch != _epoch)
        {
            _views.Clear();
            _epoch = view.Epoch;
            _lastSequence = 0;
        }
        else if (view.Sequence <= _lastSequence) return false;
        _lastSequence = view.Sequence;
        if (view.Cleared) { _views.Remove(view.Operation); return true; }
        var totals = new Dictionary<int, int>(view.Totals.Count);
        foreach (var t in view.Totals) totals[t.Item] = t.Amount;
        _views[view.Operation] = (view, totals);
        return true;
    }

    /// <summary>The pool totals for <paramref name="operation"/>; false when the server sent none (show the game's own counts).</summary>
    internal bool TryGet(MaterialOperation operation, out IReadOnlyDictionary<int, int> totals)
    {
        if (_views.TryGetValue(operation, out var v)) { totals = v.Totals; return true; }
        totals = null!;
        return false;
    }

    internal int Count => _views.Count;

    /// <summary>Drop everything (disconnect, world change, Core uninstall).</summary>
    internal void Clear()
    {
        _views.Clear();
        _lastSequence = 0;
        _epoch = int.MinValue;
    }
}
