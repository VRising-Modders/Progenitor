using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Ui;

namespace Progenitor.Inventory;

/// <summary>Which of the game's two sort requests the client sent. Game-free.</summary>
public enum InventorySortScope
{
    /// <summary><c>SortSingleInventoryEvent</c>: sort one inventory.</summary>
    Single = 1,

    /// <summary><c>SortAllInventoriesEvent</c>: the game's "sort all" request, naming one inventory.</summary>
    All = 2,
}

/// <summary>Whose inventory a sort named, as far as the client can tell. Game-free.</summary>
public enum InventorySortTarget
{
    /// <summary>The client had no local character to compare with, or the id was empty.</summary>
    Unknown = 0,

    /// <summary>The local player's own character or its bag.</summary>
    Own = 1,

    /// <summary>Anything else: a container, a station, a castle heart. Compare with an open panel's target to tell which.</summary>
    Other = 2,
}

/// <summary>
/// Client event: the player used the game's sort (the sort key or a sort button) and the client is
/// sending the request to the server. Subscribe with <c>ctx.Events.On&lt;InventorySortUsed&gt;</c>
/// from a client plugin. Raised on the main thread, once per request, before it goes out.
///
/// <para>The game sends the same request for the key and for the button, so which of them was used
/// is not knowable from it; nothing here reads input.</para>
///
/// <para><b>Naming the inventory.</b> <see cref="NetworkId0"/>..<see cref="NetworkId2"/> are the
/// sorted inventory's <c>NetworkId</c> in the same three words <see cref="VanillaPanelTarget"/> uses
/// (padding cleared), so <see cref="IsFor"/> tells whether the sort was for the panel a
/// <c>VanillaUiSite</c> handle shows; send them to the server to resolve them there. A session-local
/// id: never persist it.</para>
/// </summary>
public sealed class InventorySortUsed
{
    /// <summary>Make one. Public so tests and samples can describe one.</summary>
    public InventorySortUsed(
        InventorySortScope scope,
        InventorySortTarget target,
        int networkId0,
        int networkId1,
        int networkId2,
        long timestampMs,
        DateTime utcTime,
        long sequence,
        long sincePreviousMs)
    {
        Scope = scope;
        Target = target;
        NetworkId0 = networkId0;
        NetworkId1 = networkId1;
        NetworkId2 = networkId2;
        TimestampMs = timestampMs;
        UtcTime = utcTime;
        Sequence = sequence;
        SincePreviousMs = sincePreviousMs;
    }

    /// <summary>Which request: one inventory, or the game's sort-all.</summary>
    public InventorySortScope Scope { get; }

    /// <summary>Own inventory, another one, or unknown.</summary>
    public InventorySortTarget Target { get; }

    /// <summary>The <c>NetworkId</c>'s first word (<c>Normal_Index</c> for a normal id).</summary>
    public int NetworkId0 { get; }

    /// <summary>The second word (<c>Normal_Generation</c> for a normal id, padding cleared).</summary>
    public int NetworkId1 { get; }

    /// <summary>The id's type (0 none, 1 normal, 2 static map object).</summary>
    public int NetworkId2 { get; }

    /// <summary>True when the request named an inventory id.</summary>
    public bool HasNetworkId => NetworkId0 != 0 || NetworkId1 != 0 || NetworkId2 != 0;

    /// <summary>When the request was seen: <c>Environment.TickCount64</c> milliseconds (monotonic, this process).</summary>
    public long TimestampMs { get; }

    /// <summary>The same moment as wall-clock UTC, for logs.</summary>
    public DateTime UtcTime { get; }

    /// <summary>1 for the first sort this Core generation saw, counting up.</summary>
    public long Sequence { get; }

    /// <summary>
    /// Milliseconds since the previous sort of the same <see cref="Scope"/> and inventory, or -1 for
    /// the first one. A double-tap detector compares this with its window (e.g. 400 ms).
    /// </summary>
    public long SincePreviousMs { get; }

    /// <summary>True when this sort named the inventory <paramref name="target"/> (a vanilla panel's target) shows.</summary>
    public bool IsFor(VanillaPanelTarget target) =>
        HasNetworkId && target.HasNetworkId
        && target.NetworkId0 == NetworkId0 && target.NetworkId1 == NetworkId1 && target.NetworkId2 == NetworkId2;

    /// <summary><c>sort Single Own net=40:1 seq=3 since=250ms</c>.</summary>
    public override string ToString() =>
        "sort " + Scope + " " + Target + " net="
        + (HasNetworkId
            ? NetworkId0.ToString(CultureInfo.InvariantCulture) + ":" + (NetworkId1 & 0xFF).ToString(CultureInfo.InvariantCulture)
            : "none")
        + " seq=" + Sequence.ToString(CultureInfo.InvariantCulture)
        + " since=" + (SincePreviousMs < 0 ? "first" : SincePreviousMs.ToString(CultureInfo.InvariantCulture) + "ms");
}

/// <summary>
/// Game-free half of the sort publisher: the <c>NetworkId</c> word normalization, the duplicate
/// filter and the per-inventory interval. One instance per Core generation, main thread only.
///
/// <para><b>Duplicates.</b> The publisher sees each outgoing request entity before the game sends
/// it. The same entity seen again (a frame where the send did not run) is dropped by identity, and a
/// second request of the same scope for the same inventory within <see cref="DuplicateWindowMs"/>
/// is dropped as the game reacting to one press twice. The window is far below a human double-tap,
/// so a deliberate double press still raises two events.</para>
/// </summary>
internal sealed class InventorySortFilter
{
    /// <summary>Two requests for the same inventory and scope closer than this are one.</summary>
    internal const long DuplicateWindowMs = 50;

    /// <summary>Entity identities remembered for the identity check.</summary>
    internal const int RememberedEntities = 32;

    private readonly Queue<long> _seenOrder = new();
    private readonly HashSet<long> _seen = new();
    private readonly Dictionary<(InventorySortScope, int, int, int), long> _last = new();
    private long _sequence;

    /// <summary>How many requests were dropped as duplicates (diagnostics).</summary>
    internal int Dropped { get; private set; }

    /// <summary>
    /// The three words of a raw 12-byte <c>NetworkId</c>, padding cleared the way
    /// <see cref="VanillaPanelTarget"/> reads them: word 2 keeps its low byte (the type), and for a
    /// normal id (type 1) word 1 keeps its low byte (the generation).
    /// </summary>
    internal static (int, int, int) Normalize(int raw0, int raw1, int raw2)
    {
        int type = raw2 & 0xFF;
        return (raw0, type == 1 ? raw1 & 0xFF : raw1, type);
    }

    /// <summary>
    /// Admit one request seen at <paramref name="nowMs"/>. Null when it is a duplicate; otherwise the
    /// event to publish (sequence and interval filled in).
    /// </summary>
    internal InventorySortUsed? Admit(long entityKey, InventorySortScope scope, InventorySortTarget target,
        int net0, int net1, int net2, long nowMs, DateTime utcNow)
    {
        if (_seen.Contains(entityKey))
        {
            Dropped++;
            return null;
        }
        Remember(entityKey);
        var key = (scope, net0, net1, net2);
        long since = -1;
        if (_last.TryGetValue(key, out long previous))
        {
            since = Math.Max(0, nowMs - previous);
            if (since < DuplicateWindowMs)
            {
                Dropped++;
                return null;
            }
        }
        _last[key] = nowMs;
        if (_last.Count > 256) TrimOlderThan(nowMs - 60_000);
        _sequence++;
        return new InventorySortUsed(scope, target, net0, net1, net2, nowMs, utcNow, _sequence, since);
    }

    /// <summary>Forget everything (a client world change).</summary>
    internal void Clear()
    {
        _seen.Clear();
        _seenOrder.Clear();
        _last.Clear();
    }

    private void Remember(long entityKey)
    {
        _seen.Add(entityKey);
        _seenOrder.Enqueue(entityKey);
        while (_seenOrder.Count > RememberedEntities) _seen.Remove(_seenOrder.Dequeue());
    }

    private void TrimOlderThan(long cutoff)
    {
        var stale = new List<(InventorySortScope, int, int, int)>();
        foreach (var pair in _last)
            if (pair.Value < cutoff) stale.Add(pair.Key);
        foreach (var k in stale) _last.Remove(k);
    }
}
