using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Progenitor.Castles;

namespace Progenitor.Materials;

/// <summary>
/// One player's material view as sent from the server: for one operation, the pool's spendable total
/// of every item the provider governs there (zero included), stamped with world epoch, provider
/// revision, a per-player sequence, and the network ids of the castle heart the player stands on and
/// (craft) the workstation the view was computed for. Items not listed are left to the game's own
/// shared storage. A cleared view (<see cref="Cleared"/>) tells the client to show the game's own
/// counts again.
/// </summary>
internal sealed class MaterialView
{
    internal MaterialOperation Operation { get; }
    internal int Epoch { get; }
    internal long Revision { get; }
    internal uint Sequence { get; }
    internal int Territory { get; }

    /// <summary>Network id (packed) of the castle heart at the player's standing plot; 0 for none.</summary>
    internal ulong HeartNet { get; }

    /// <summary>Network id (packed) of the workstation a Craft view was computed for; 0 otherwise.</summary>
    internal ulong StationNet { get; }

    /// <summary>
    /// The sending server publisher's generation (a fresh value per server Core install): sequences
    /// restart with it, so the client starts over when it changes.
    /// </summary>
    internal uint Generation { get; }

    internal bool Cleared { get; }
    internal bool Truncated { get; }
    internal IReadOnlyList<MaterialLine> Totals { get; }

    internal MaterialView(MaterialOperation operation, int epoch, long revision, uint sequence, int territory,
        bool cleared, bool truncated, IReadOnlyList<MaterialLine> totals, ulong heartNet = 0, ulong stationNet = 0, uint generation = 0)
    {
        Generation = generation;
        Operation = operation;
        Epoch = epoch;
        Revision = revision;
        Sequence = sequence;
        Territory = territory;
        Cleared = cleared;
        Truncated = truncated;
        Totals = totals;
        HeartNet = heartNet;
        StationNet = stationNet;
    }
}

/// <summary>
/// Wire format of <see cref="MaterialView"/> on Core's reserved event <see cref="EventId"/>
/// (<c>'PrMv'</c>). Little endian: version, operation, flags, 0, epoch (int), revision (long),
/// sequence (uint), territory (int), count (int), heart network id (ulong), station network id
/// (ulong), publisher generation (uint), then count × (item int, amount int). Version 2 (version 1 lacked the two ids and listed
/// ungoverned items too; a version 1 packet is dropped, which shows the game's own counts).
/// </summary>
internal static class MaterialViewWire
{
    /// <summary><c>'PrMv'</c>: inside Core's reserved range, so no plugin can send or take it.</summary>
    internal const int EventId = 0x50724D76;

    internal const byte Version = 2;
    internal const int HeaderBytes = 48;

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
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(28), view.HeartNet);
        BinaryPrimitives.WriteUInt64LittleEndian(s.Slice(36), view.StationNet);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(44), view.Generation);
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
        if (s.Length < 1) { reason = "empty"; return false; }
        if (s[0] != Version) { reason = "version " + s[0]; return false; }
        if (s.Length < HeaderBytes) { reason = "short header " + s.Length; return false; }
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
            (s[2] & FlagCleared) != 0, (s[2] & FlagTruncated) != 0, rows,
            BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(28)),
            BinaryPrimitives.ReadUInt64LittleEndian(s.Slice(36)),
            BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(44)));
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
    /// operation (or <paramref name="force"/>, or the last send failed); then <paramref name="sequence"/>
    /// is the next number.
    /// </summary>
    internal bool ShouldSend(CastleId user, MaterialOperation operation, ulong fingerprint, bool force, out uint sequence)
    {
        sequence = 0;
        bool retry = _retry.Remove((user, operation));
        if (!force && !retry && _last.TryGetValue((user, operation), out var prev) && prev == fingerprint) return false;
        _last[(user, operation)] = fingerprint;
        _seq.TryGetValue(user, out uint s);
        sequence = ++s;
        _seq[user] = s;
        return true;
    }

    /// <summary>True when this player has been sent a non-cleared view for this operation, or a send to them failed.</summary>
    internal bool HasView(CastleId user, MaterialOperation operation) =>
        _retry.Contains((user, operation)) || (_last.TryGetValue((user, operation), out var f) && f != ClearedFingerprint);

    /// <summary>The last send to this player failed: send again on the next refresh, even if unchanged.</summary>
    internal void Retry(CastleId user, MaterialOperation operation) => _retry.Add((user, operation));

    /// <summary>The fingerprint of a cleared view.</summary>
    internal const ulong ClearedFingerprint = 0;

    /// <summary>Forget a player (disconnect). The sequence is kept so a reconnect never goes backwards.</summary>
    internal void Forget(CastleId user)
    {
        foreach (var op in Operations)
        {
            _retry.Remove((user, op));
            _last.Remove((user, op));
        }
    }

    internal void Clear()
    {
        _retry.Clear();
        _last.Clear();
        _seq.Clear();
    }

    private static readonly MaterialOperation[] Operations = { MaterialOperation.Build, MaterialOperation.Craft };

    /// <summary>
    /// FNV-1a over territory, revision, the heart and station ids and the sorted totals. Never returns
    /// <see cref="ClearedFingerprint"/>.
    /// </summary>
    internal static ulong Fingerprint(int territory, long revision, IReadOnlyList<MaterialLine> totals, ulong heartNet = 0, ulong stationNet = 0)
    {
        ulong h = 14695981039346656037UL;
        void Mix(long v)
        {
            for (int i = 0; i < 8; i++) { h ^= (byte)(v >> (i * 8)); h *= 1099511628211UL; }
        }
        Mix(territory);
        Mix(revision);
        Mix((long)heartNet);
        Mix((long)stationNet);
        foreach (var t in totals) { Mix(t.Item); Mix(t.Amount); }
        return h == ClearedFingerprint ? 1 : h;
    }
}

/// <summary>What the server computed for one player and operation: null when not governed there.</summary>
internal sealed class MaterialViewContent
{
    internal int Territory { get; }
    internal long Revision { get; }
    internal ulong HeartNet { get; }
    internal ulong StationNet { get; }
    internal IReadOnlyList<MaterialLine> Totals { get; }

    internal MaterialViewContent(int territory, long revision, ulong heartNet, ulong stationNet, IReadOnlyList<MaterialLine> totals)
    {
        Territory = territory;
        Revision = revision;
        HeartNet = heartNet;
        StationNet = stationNet;
        Totals = totals;
    }
}

/// <summary>
/// Server side, game-free: turns each refresh's per-player content into the packet to send (or
/// nothing), on top of <see cref="MaterialViewBook"/>. A send that fails is retried on the next
/// refresh even when unchanged; a player who had a view and is no longer governed gets one cleared
/// view (retried the same way).
/// </summary>
internal sealed class MaterialViewPublisher
{
    internal MaterialViewBook Book { get; } = new();

    /// <summary>This publisher's generation, sent with every view; never 0.</summary>
    internal uint Generation { get; } = NewGeneration();

    private static uint NewGeneration()
    {
        uint g;
        do { g = (uint)System.Security.Cryptography.RandomNumberGenerator.GetInt32(int.MaxValue); } while (g == 0);
        return g;
    }

    private bool _clearEveryone;
    private readonly HashSet<(CastleId, MaterialOperation)> _clearedOnce = new();

    /// <summary>
    /// A fresh server install (a Core swap included) does not know what the previous generation sent: at
    /// the next refresh every online player not governed now gets one cleared view, so no client keeps a
    /// stale view. Call <see cref="FirstRefreshDone"/> after that refresh.
    /// </summary>
    internal void ClearEveryoneOnce()
    {
        _clearEveryone = true;
        _clearedOnce.Clear();
    }

    /// <summary>The refresh after <see cref="ClearEveryoneOnce"/> went out to every online player.</summary>
    internal void FirstRefreshDone()
    {
        _clearEveryone = false;
        _clearedOnce.Clear();
    }

    /// <summary>The packet to send for this player and operation now, or null for none.</summary>
    internal MaterialView? Next(CastleId user, MaterialOperation operation, int epoch, MaterialViewContent? content, bool force)
    {
        if (content != null)
        {
            ulong fp = MaterialViewBook.Fingerprint(content.Territory, content.Revision, content.Totals, content.HeartNet, content.StationNet);
            if (!Book.ShouldSend(user, operation, fp, force, out uint seq)) return null;
            return new MaterialView(operation, epoch, content.Revision, seq, content.Territory, false, false,
                content.Totals, content.HeartNet, content.StationNet, Generation);
        }
        bool startup = _clearEveryone && _clearedOnce.Add((user, operation));
        if (!startup && !Book.HasView(user, operation)) return null;
        if (!Book.ShouldSend(user, operation, MaterialViewBook.ClearedFingerprint, startup, out uint clearSeq)) return null;
        return new MaterialView(operation, epoch, 0, clearSeq, -1, true, false, Array.Empty<MaterialLine>(), 0, 0, Generation);
    }

    /// <summary>Report the transport result of a packet <see cref="Next"/> returned.</summary>
    internal void Sent(CastleId user, MaterialOperation operation, bool delivered)
    {
        if (!delivered) Book.Retry(user, operation);
    }

    /// <summary>The player disconnected: a reconnect starts from nothing (sequence numbers continue).</summary>
    internal void Disconnected(CastleId user) => Book.Forget(user);

    internal void Clear() => Book.Clear();
}

/// <summary>
/// Server side, game-free: when a refresh runs and whom it covers. Every <see cref="IntervalMs"/>
/// every online player is recomputed (sent only when changed); right after an invalidation or a
/// payment the marked players are; every <see cref="ResyncMs"/> (and after a provider change) every
/// player is sent their view again even when unchanged, which repairs a packet the transport accepted
/// but the client never applied (a client Core reload, a lost packet).
/// </summary>
internal sealed class MaterialRefreshPolicy
{
    internal const long IntervalMs = 1_000;
    internal const long ResyncMs = 10_000;

    private long _nextFull = long.MinValue;
    private long _nextResync = long.MinValue;
    private bool _forceNext;

    /// <summary>A provider registered or went away: resend everyone's view on the next refresh.</summary>
    internal void ForceAll()
    {
        _forceNext = true;
        _nextFull = long.MinValue;
    }

    /// <summary>
    /// Decide this frame's refresh. False: nothing to do. <paramref name="everyone"/>: recompute every
    /// online player (else only the dirty ones); <paramref name="force"/>: send even unchanged views.
    /// </summary>
    internal bool Tick(long nowMs, bool invalidateAll, bool anyDirty, out bool everyone, out bool force)
    {
        if (invalidateAll) _forceNext = true;
        everyone = false;
        force = false;
        bool full = invalidateAll || _forceNext || nowMs >= _nextFull;
        if (!full && !anyDirty) return false;
        if (full)
        {
            everyone = true;
            _nextFull = nowMs + IntervalMs;
        }
        if (_forceNext || nowMs >= _nextResync)
        {
            force = true;
            everyone = true;
            _forceNext = false;
            _nextResync = nowMs + ResyncMs;
        }
        return true;
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
    private uint _generation;

    /// <summary>
    /// Apply a received view. False when it is older than one already applied. A new world epoch or a new
    /// server publisher generation (a server Core swap) starts over.
    /// </summary>
    internal bool Apply(MaterialView view)
    {
        if (view.Epoch != _epoch || view.Generation != _generation)
        {
            _views.Clear();
            _epoch = view.Epoch;
            _generation = view.Generation;
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

    /// <summary>The governed totals for <paramref name="operation"/>; false when the server sent none (show the game's own counts).</summary>
    internal bool TryGet(MaterialOperation operation, out IReadOnlyDictionary<int, int> totals)
    {
        if (_views.TryGetValue(operation, out var v)) { totals = v.Totals; return true; }
        totals = null!;
        return false;
    }

    /// <summary>The view and its totals for <paramref name="operation"/>.</summary>
    internal bool TryGet(MaterialOperation operation, out MaterialView view, out IReadOnlyDictionary<int, int> totals)
    {
        if (_views.TryGetValue(operation, out var v)) { view = v.View; totals = v.Totals; return true; }
        view = null!;
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
        _generation = 0;
    }
}

/// <summary>What the client knows about one call of the game's merged count.</summary>
internal readonly struct MaterialClientCall
{
    /// <summary>The count is for the locally controlled character (not another player, a servant or a station).</summary>
    internal bool LocalCharacter { get; init; }

    /// <summary>Network id (packed) of the castle heart the game resolved for this count; 0 for none.</summary>
    internal ulong HeartNet { get; init; }

    /// <summary>Network id (packed) of the local character's interaction target; 0 when it has none.</summary>
    internal ulong TargetNet { get; init; }

    /// <summary>The interaction target is a castle workstation.</summary>
    internal bool TargetIsWorkstation { get; init; }

    /// <summary>The build menu is shown, or the build-mode placement update is running.</summary>
    internal bool BuildContext { get; init; }
}

/// <summary>
/// Client side, game-free: which received view (if any) may replace the game's shared rows for one
/// count, and the rows that replace them. A view applies only to the local character, only for the
/// castle heart it was computed at, and only in its own operation: Build while the build menu is shown
/// or build-mode placement runs (the build menu and the station windows are exclusive HUD menus, so a
/// lingering interaction target does not matter there), otherwise Craft at the very workstation the
/// server computed it for. Every other count (another screen, another station, another plot, another
/// entity) keeps the game's own result. The display is never authorization: the server checks again
/// and pays at action time.
/// </summary>
internal static class MaterialClientPolicy
{
    /// <summary>
    /// What the client knows about one count, built from raw game reads: whether it is for the local
    /// character, the castle heart the game resolved for it, whether the build menu is shown or build-mode
    /// placement runs, and the network id of the workstation the local character is interacting with (0
    /// for none or for anything that is not a workstation).
    /// </summary>
    internal static MaterialClientCall Call(bool localCharacter, ulong heartNet, bool buildMenuOrPlacement, ulong workstationNet) => new()
    {
        LocalCharacter = localCharacter,
        HeartNet = heartNet,
        BuildContext = buildMenuOrPlacement,
        TargetNet = workstationNet,
        TargetIsWorkstation = workstationNet != 0,
    };

    internal static bool TrySelect(MaterialOverlayStore store, in MaterialClientCall call, out MaterialOperation operation,
        out IReadOnlyDictionary<int, int> totals)
    {
        operation = default;
        totals = null!;
        if (!call.LocalCharacter) return false;
        if (call.BuildContext)
        {
            if (!store.TryGet(MaterialOperation.Build, out var build, out totals)) return false;
            if (build.HeartNet != call.HeartNet) return false;
            operation = MaterialOperation.Build;
            return true;
        }
        if (call.TargetNet == 0 || !call.TargetIsWorkstation) return false;
        if (!store.TryGet(MaterialOperation.Craft, out var craft, out totals)) return false;
        if (craft.StationNet == 0 || craft.StationNet != call.TargetNet || craft.HeartNet != call.HeartNet) return false;
        operation = MaterialOperation.Craft;
        return true;
    }

    /// <summary>
    /// The rows to append after the bag rows: each governed item's pool amount, and for every other
    /// item the game's own shared amount (<paramref name="full"/> minus <paramref name="bagOnly"/>), so
    /// an item the provider leaves to the game shows exactly what the game alone would show.
    /// </summary>
    internal static void ComposeExtra(IReadOnlyDictionary<int, long> full, IReadOnlyDictionary<int, long> bagOnly,
        IReadOnlyDictionary<int, int> governed, List<MaterialLine> extra)
    {
        extra.Clear();
        foreach (var kv in governed)
            if (kv.Value > 0) extra.Add(new MaterialLine(kv.Key, kv.Value));
        foreach (var kv in full)
        {
            if (governed.ContainsKey(kv.Key)) continue;
            bagOnly.TryGetValue(kv.Key, out long bag);
            long shared = kv.Value - bag;
            if (shared > 0) extra.Add(new MaterialLine(kv.Key, shared > int.MaxValue ? int.MaxValue : (int)shared));
        }
        extra.Sort((a, b) => a.Item.CompareTo(b.Item));
    }
}
