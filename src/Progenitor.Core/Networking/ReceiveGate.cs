using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Networking;

/// <summary>What the server dispatcher must do with one Progenitor packet.</summary>
internal enum ReceiveVerdict
{
    /// <summary>Run the registered handler now.</summary>
    Dispatch,

    /// <summary>Hold the payload for a short bounded window and retry resolution.</summary>
    Queue,

    /// <summary>Drop and consume the packet.</summary>
    Drop,
}

/// <summary>
/// Resolves a connection to the approved-user row the game holds for it. The one implementation
/// that matters wraps <c>EventHooks.TryFindUser</c>; the tests supply a fake so
/// <see cref="ReceiveGate.Admit"/> — the real decision — is what they drive.
/// </summary>
internal interface IReceiveResolver
{
    /// <summary>
    /// True when <paramref name="connection"/> has an approved-user row. <paramref name="platformId"/>
    /// is that row's platform id (never 0 for a real user). Implementations must not throw.
    /// </summary>
    bool TryResolve(long connection, out ulong platformId);
}

/// <summary>
/// The hold list for packets the gate answered <see cref="ReceiveVerdict.Queue"/>, and the whole
/// policy that governs it. Game-free on purpose (R29 finding 22.4): the list, the attach state and
/// the drain generation used to live as three statics inside <c>CustomEvents</c>, which is
/// <c>unsafe</c> and needs game types, so none of it could be tested and R29 finding 14 lived there.
///
/// <para><b>The detached drain (R29 finding 14).</b> <c>DrainHeld</c> snapshots the list, clears it,
/// and re-holds anything still unresolved and inside its grace window. A held packet's handler can
/// trigger a Core reload, so <c>UninstallServerReceive</c> — and therefore <see cref="Detach"/> —
/// is reachable from the middle of that loop. Nothing coordinated the two: the detach cleared an
/// already-emptied list and the remaining iterations re-held their packets into a list with no
/// <c>Plugin.Frame</c> subscription behind it. Those packets were never drained and never released,
/// so <c>_queuedPackets</c> stuck at its cap (every genuinely first-seen connection refused for the
/// life of the process) and their rows, having <c>Queued &gt; 0</c>, were skipped by both passes of
/// <c>PruneConnections</c> and so could never be evicted either.
///
/// <para>A drain pass captures <see cref="Generation"/> at entry and passes it back to
/// <see cref="TryRequeue"/>. <see cref="Detach"/> bumps the generation, so a re-hold from a pass
/// that started before the detach is refused and the caller drops the packet — one log for the
/// batch — instead of parking it forever.</para>
/// </summary>
/// <typeparam name="T">The caller's packet record. Opaque here: this type is pure policy.</typeparam>
internal sealed class ReceiveHoldQueue<T> where T : class
{
    private readonly object _gate = new();
    private readonly List<T> _held = new();
    private int _generation;
    private bool _attached;

    /// <summary>Packets currently held.</summary>
    public int Count
    {
        get { lock (_gate) return _held.Count; }
    }

    /// <summary>True while a per-frame drain is subscribed.</summary>
    public bool Attached
    {
        get { lock (_gate) return _attached; }
    }

    /// <summary>Bumped by every <see cref="Detach"/>. A drain pass carries the value it started on.</summary>
    public int Generation
    {
        get { lock (_gate) return _generation; }
    }

    /// <summary>
    /// Mark the drain attached. True when this call is the one that must subscribe the per-frame
    /// callback (so the caller never double-subscribes).
    /// </summary>
    public bool TryAttach()
    {
        lock (_gate)
        {
            if (_attached) return false;
            _attached = true;
            return true;
        }
    }

    /// <summary>
    /// Detach the drain and take everything it was holding, so the caller can release each packet's
    /// gate accounting. <paramref name="wasAttached"/> is false when there was nothing subscribed.
    /// The generation is bumped either way, which is what makes a drain pass already in flight drop
    /// its remaining packets rather than re-hold them.
    /// </summary>
    public T[] Detach(out bool wasAttached)
    {
        lock (_gate)
        {
            wasAttached = _attached;
            _attached = false;
            _generation++;
            if (_held.Count == 0) return Array.Empty<T>();
            var held = _held.ToArray();
            _held.Clear();
            return held;
        }
    }

    /// <summary>Hold one packet the gate answered <see cref="ReceiveVerdict.Queue"/>.</summary>
    public void Add(T packet)
    {
        if (packet == null) return;
        lock (_gate) _held.Add(packet);
    }

    /// <summary>
    /// Take everything for one drain pass. <paramref name="generation"/> is what
    /// <see cref="TryRequeue"/> must be given for every packet of this pass.
    /// </summary>
    public T[] BeginDrain(out int generation)
    {
        lock (_gate)
        {
            generation = _generation;
            if (_held.Count == 0) return Array.Empty<T>();
            var pending = _held.ToArray();
            _held.Clear();
            return pending;
        }
    }

    /// <summary>
    /// Re-hold a packet that is still unresolved and inside its grace window. <b>False</b> when the
    /// drain was detached under this pass: the caller must release the packet's gate accounting and
    /// drop it, because nothing will ever drain the list again (R29 finding 14).
    /// </summary>
    public bool TryRequeue(T packet, int generation)
    {
        lock (_gate)
        {
            if (!_attached || _generation != generation) return false;
            _held.Add(packet);
            return true;
        }
    }

    /// <summary>The one line a batch of packets dropped by a detached drain produces.</summary>
    public static string DetachedDropLog(int dropped) =>
        "CustomEvents: dropping " + dropped.ToString(CultureInfo.InvariantCulture)
        + " held packet(s); the receive drain was detached (Core swap or uninstall).";
}

/// <summary>
/// Admission control for the server-side custom-event receive path (threat model surface 1).
/// Game-free on purpose: <see cref="CustomEvents.TryDispatchServer"/> makes exactly one call to
/// <see cref="Admit"/> and does what the verdict says, so the unit tests drive the production
/// decision rather than a copy of it (R28 findings 3 and 29).
///
/// <para><b>Three stages, in this order (R29 finding 3).</b></para>
/// <list type="number">
/// <item><b>Per-sender rate.</b> One token per packet from the sender's own bucket — the player's
/// bucket once the connection has resolved once, the connection's pre-approval bucket before that.
/// An empty bucket drops the packet <i>here</i>, with at most one log line per sender per
/// <see cref="LogWindowSeconds"/> (the log itself was a DoS vector: threat model section 14).</item>
/// <item><b>Global backstop.</b> One O(1) bucket, sized
/// <see cref="GlobalCapacity"/> = <c>burst x (GlobalPerPlayer x players + GlobalFloorPlayers)</c>.
/// It is charged only by packets that were already <i>within</i> their sender's own allowance, so
/// it is a ceiling on the whole receive path and never a budget senders compete for.</item>
/// <item><b>Resolution.</b> <see cref="IReceiveResolver.TryResolve"/> looks the connection up in the
/// game's approved-user table. Everything downstream keys on the <b>platform id</b> it returns; the
/// first packet on a handle has its stage-1 charge <i>moved</i> from the pre-approval bucket onto
/// that player's bucket, so a churning handle cannot mint budget.</item>
/// </list>
///
/// <para><b>Fair share (R29 finding 3).</b> EQ8 charged one server-wide bucket <i>first</i>, for
/// every packet, before per-sender limiting. One client sending 400 packets/s drained it and every
/// other player was dropped at stage 1 with a line that named nobody, while the per-sender limiter —
/// the entire point of the gate — never ran; and 40 honest players at 8 packets/s each hit the same
/// ceiling with no misbehavior at all. The order is inverted here and the sizing is per player:
/// a sender over its own limit is dropped <b>before</b> the backstop is consulted, so its flood
/// costs the backstop nothing and cannot take another player's packet with it. Because every
/// resolved player contributes <see cref="GlobalPerPlayer"/> whole per-sender budgets to the
/// backstop while it can spend at most one, the honest population can never exhaust it: the
/// backstop only trips on traffic that no per-sender bucket accounted for.</para>
///
/// <para><b>Why the platform id and not the connection pointer (R28 finding 3).</b>
/// <c>Stunlock.Network.NetConnectionId</c> is an <i>abstract class with no fields</i>
/// (server metadata); identity lives in the concrete subclass —
/// <c>SteamNetConnectionId.ID</c>, <c>LidgrenNetConnectionId.IPEndPoint</c>,
/// <c>EosNetConnectionId.RemoteUserId</c> — and every one of
/// those overrides <c>Equals</c>/<c>GetHashCode</c> with content semantics. The game keys three
/// dictionaries by the object (<c>ServerBootstrapSystem._NetEndPointToApprovedUserIndex</c>,
/// <c>ConnectionQueue._Lookup</c>,
/// <c>ServerNetworkLayer._ConnectionIdToUserState</c>) and therefore never relies on
/// reference identity. Native analysis shows the instance <i>is</i> in fact reused per connection
/// today (<c>ServerSteamTransportLayer.ProcessNewMessages</c> takes it out of
/// <c>_ActiveConnections</c> rather than allocating one per message), but that is an observation,
/// not a contract, and a pointer is also recycled across connections. The approved row's
/// <c>PlatformId</c> (<c>ServerBootstrapSystem.ServerClient</c>, written
/// by <c>ApproveClient</c>) is the identity the game itself guarantees, so that is what the bucket
/// is keyed on. The raw pointer is still used, but only as a short-lived pre-approval handle whose
/// traffic is additionally capped by the global bucket, so a churning pointer cannot mint budget.
/// R28 finding 28.3 falls out of the same change: a bucket now follows the player, so a reconnect
/// on a recycled pointer never inherits a stranger's bucket, and
/// <see cref="ForgetPlatform"/> has a production caller on disconnect.</para>
///
/// <para><b>Unresolved senders (R28 finding 21).</b>
/// <c>OnDataReceivedServer</c> is the entry for every packet a client sends.
/// <c>EventHooks.TryFindUser</c> returns false for a connection with no approved-user row, leaving
/// <c>user = default</c> — <c>User.Index == 0</c>, a real assignable index, not "no user".
/// Dispatching that would let an unapproved peer drive user 0's state. EQ7 dropped those outright.
/// Native analysis says a legitimate join cannot land there: <c>ServerNetworkLayer.OnDataReceived</c>
/// forwards to <c>_Callbacks.OnDataReceived</c> (our seam) <b>only</b> in
/// <c>UserConnectState.Connected</c>, the transition into which fires <c>OnUserConnected</c>, which
/// is itself only reachable after <c>ApproveClient</c> wrote the row. The rule is still made fail
/// safe rather than fail closed, because the hosted-child row is documented as carrying a null
/// <c>NetConnectionId</c> (<c>Handshake.cs</c>) which no <c>Equals</c> can match: a connection seen
/// for the first time within <see cref="UnresolvedGraceSeconds"/> is <see cref="ReceiveVerdict.Queue"/>d
/// — bounded by <see cref="MaxQueuedPackets"/>, <see cref="MaxQueuedPerConnection"/> and
/// <see cref="MaxQueuedBytes"/> — and retried; past the grace window, or with the queue full, it is
/// dropped with the same one-line-per-window budget.</para>
///
/// <para><b>Visibility.</b> <see cref="TryTakeMetricLine"/> emits
/// <c>ReceiveGate: N connections tracked, M dropped this window</c> once per
/// <see cref="MetricWindowSeconds"/> in which any Progenitor packet arrived, so a limiter that never
/// limits — or a table churning to its cap — is visible in the operator's log instead of silent.</para>
/// </summary>
internal sealed class ReceiveGate
{
    /// <summary>Bucket capacity: how many Progenitor packets one sender may burst.</summary>
    public const int DefaultBurst = 60;

    /// <summary>Steady-state refill, in Progenitor packets per second per sender.</summary>
    public const int DefaultPerSecond = 20;

    /// <summary>
    /// Whole per-sender budgets the global backstop gains for each player it currently tracks.
    /// Greater than 1 on purpose: a player can spend at most one per-sender budget, so the honest
    /// population always leaves the backstop with headroom to spare (R29 finding 3).
    /// </summary>
    public const int GlobalPerPlayer = 2;

    /// <summary>
    /// Per-sender budgets the backstop holds with nobody resolved yet. This is the pre-approval
    /// floor: it bounds packets from connections that have no approved-user row (a joining player,
    /// a hosted child, or a peer churning connection handles) without borrowing from any player.
    /// </summary>
    public const int GlobalFloorPlayers = 16;

    /// <summary>At most one drop line per sender per this many seconds, per reason.</summary>
    public const double LogWindowSeconds = 60.0;

    /// <summary>At most one metric line per this many seconds.</summary>
    public const double MetricWindowSeconds = 60.0;

    /// <summary>Connections (and senders) tracked before the least recently seen one is evicted.</summary>
    public const int MaxTrackedConnections = 256;

    /// <summary>A row not seen for this long is forgotten on the next insert.</summary>
    public const double IdleForgetSeconds = 300.0;

    /// <summary>
    /// How long after a connection is first seen its packets may be queued while the approved-user
    /// row is still missing. Short on purpose: the ordinary join already has its row.
    /// </summary>
    public const double UnresolvedGraceSeconds = 5.0;

    /// <summary>Total packets held for unresolved connections.</summary>
    public const int MaxQueuedPackets = 32;

    /// <summary>Packets held for any one unresolved connection.</summary>
    public const int MaxQueuedPerConnection = 4;

    /// <summary>Total payload bytes held for unresolved connections.</summary>
    public const int MaxQueuedBytes = 128 * 1024;

    private readonly object _gate = new();
    private readonly Dictionary<long, ConnRow> _connections = new();
    private readonly Dictionary<ulong, Bucket> _senders = new();
    private readonly Bucket _global = new();
    private double _globalLastLog = double.NegativeInfinity;
    private int _burst = DefaultBurst;
    private int _perSecond = DefaultPerSecond;
    // R29 finding 21.2: this is the "no window open yet" sentinel. `<= double.NegativeInfinity`
    // worked but read as a bug; double.IsNegativeInfinity says what is meant.
    private double _metricWindowStart = double.NegativeInfinity;
    private long _packetsThisWindow;
    private long _droppedThisWindow;
    private int _queuedPackets;
    private int _queuedBytes;

    /// <summary>Bucket capacity. Values below 1 are clamped to 1.</summary>
    public int Burst
    {
        get { lock (_gate) return _burst; }
        set { lock (_gate) _burst = value < 1 ? 1 : value; }
    }

    /// <summary>Refill rate. Values below 1 are clamped to 1.</summary>
    public int PerSecond
    {
        get { lock (_gate) return _perSecond; }
        set { lock (_gate) _perSecond = value < 1 ? 1 : value; }
    }

    /// <summary>Connections currently tracked. Diagnostics, the metric line and tests.</summary>
    public int TrackedConnections
    {
        get { lock (_gate) return _connections.Count; }
    }

    /// <summary>Approved senders currently tracked. Diagnostics and tests.</summary>
    public int TrackedSenders
    {
        get { lock (_gate) return _senders.Count; }
    }

    /// <summary>Packets currently held for unresolved connections.</summary>
    public int QueuedPackets
    {
        get { lock (_gate) return _queuedPackets; }
    }

    /// <summary>
    /// Payload bytes currently charged against the hold budget. Diagnostics and tests: this is the
    /// number R29 finding 21.3 said could drift until the hold queue was permanently "full".
    /// </summary>
    public int QueuedBytes
    {
        get { lock (_gate) return _queuedBytes; }
    }

    /// <summary>
    /// The whole server-side admission decision for one Progenitor packet, in one call. The caller
    /// does exactly what the verdict says and logs <paramref name="log"/> when it is non-null.
    /// </summary>
    /// <param name="connection">Raw connection handle; a pre-approval key only, never the budget key.</param>
    /// <param name="eventId">Fourcc, for the refusal line.</param>
    /// <param name="payloadBytes">Payload length, charged against the queue budget.</param>
    /// <param name="now">Monotonic seconds.</param>
    /// <param name="resolver">Approved-user lookup. Must not throw.</param>
    /// <param name="log">At most one line per sender per <see cref="LogWindowSeconds"/> per reason.</param>
    public ReceiveVerdict Admit(
        long connection,
        int eventId,
        int payloadBytes,
        double now,
        IReceiveResolver? resolver,
        out string? log)
    {
        log = null;

        ConnRow row;
        Bucket charged;
        lock (_gate)
        {
            _packetsThisWindow++;
            if (double.IsNegativeInfinity(_metricWindowStart)) _metricWindowStart = now;

            row = GetOrAddConnection(connection, now);
            row.LastSeen = now;

            // Stage 1 — per-sender rate, FIRST. The player's bucket once this handle has resolved
            // once (approved traffic is charged to the platform id, so a re-boxed or recycled
            // handle cannot mint a fresh full bucket), the connection's pre-approval bucket before
            // that. R29 finding 3: a sender over its own limit dies here, without ever touching the
            // backstop, so its flood cannot drop anybody else's packet.
            charged = row.PlatformId != 0 ? GetOrAddSender(row.PlatformId, now) : row.Bucket;
            charged.LastSeen = now;
            Refill(charged, now, _perSecond, _burst);
            if (charged.Tokens < 1.0)
            {
                _droppedThisWindow++;
                // R29 finding 21.1: the throttle lives on the BUCKET, so on the resolved path it is
                // one line per player. On the row it was one line per connection handle, and a
                // re-boxed handle bought a fresh log allowance.
                if (ShouldLog(ref charged.LastRateLog, now))
                    log = RateLimitedLog(connection, row.PlatformId, _burst, _perSecond);
                return ReceiveVerdict.Drop;
            }
            charged.Tokens -= 1.0;

            // Stage 2 — global backstop. Only packets already within their sender's allowance get
            // here, and the capacity grows with the tracked population, so honest traffic can never
            // exhaust it. Nothing below can be driven faster than this.
            int players = _senders.Count;
            int globalBurst = GlobalCapacity(_burst, players);
            int globalPerSecond = GlobalRefill(_perSecond, players);
            Refill(_global, now, globalPerSecond, globalBurst);
            if (_global.Tokens < 1.0)
            {
                // No work happened, so the sender keeps its token.
                Give(charged, 1.0, _burst);
                _droppedThisWindow++;
                if (ShouldLog(ref _globalLastLog, now))
                    log = GlobalLimitedLog(globalBurst, globalPerSecond);
                return ReceiveVerdict.Drop;
            }
            _global.Tokens -= 1.0;
        }

        // Stage 3 — resolution, outside the lock: the resolver reaches into the game's tables.
        // The platform id is the identity the game guarantees.
        ulong platformId = 0;
        bool resolved = false;
        if (resolver != null)
        {
            try { resolved = resolver.TryResolve(connection, out platformId); }
            catch { resolved = false; platformId = 0; }
        }
        if (platformId == 0) resolved = false;

        lock (_gate)
        {
            // The row cannot normally vanish across the gap (one receive thread), but an eviction
            // from another caller must not silently orphan this packet's accounting.
            if (!_connections.ContainsKey(connection)) _connections[connection] = row;
            row.LastSeen = now;

            if (resolved)
            {
                var sender = GetOrAddSender(platformId, now);
                sender.LastSeen = now;
                if (!ReferenceEquals(charged, sender))
                {
                    // First resolution on this handle (or an identity change). Stage 1 charged the
                    // pre-approval bucket; move the charge onto the player's bucket, which is the
                    // budget that follows them across handles. Without this move a peer that
                    // re-boxes its handle every packet would spend a fresh full bucket each time.
                    Give(charged, 1.0, _burst);
                    Refill(sender, now, _perSecond, _burst);
                    if (sender.Tokens < 1.0)
                    {
                        row.PlatformId = platformId;
                        _droppedThisWindow++;
                        if (ShouldLog(ref sender.LastRateLog, now))
                            log = RateLimitedLog(connection, platformId, _burst, _perSecond);
                        return ReceiveVerdict.Drop;
                    }
                    sender.Tokens -= 1.0;
                }
                row.PlatformId = platformId;
                return ReceiveVerdict.Dispatch;
            }

            // Unresolved. Queue briefly rather than drop, so a connection whose approved row this
            // process cannot match (hosted child, null NetConnectionId) is not silently cut off.
            bool inGrace = now - row.FirstSeen <= UnresolvedGraceSeconds;
            bool room = payloadBytes >= 0
                && row.Queued < MaxQueuedPerConnection
                && _queuedPackets < MaxQueuedPackets
                && _queuedBytes + payloadBytes <= MaxQueuedBytes;
            if (inGrace && room)
            {
                row.Queued++;
                _queuedPackets++;
                _queuedBytes += payloadBytes;
                // R29 finding 21.3: remember what we charged. NoteDequeued releases one of THESE
                // numbers, never a number the caller supplies, so _queuedBytes cannot drift.
                (row.Charges ??= new List<int>(MaxQueuedPerConnection)).Add(payloadBytes);
                return ReceiveVerdict.Queue;
            }

            _droppedThisWindow++;
            if (ShouldLog(ref row.LastUnresolvedLog, now))
                log = UnresolvedLog(connection, eventId, inGrace);
            return ReceiveVerdict.Drop;
        }
    }

    /// <summary>
    /// Capacity of the global backstop for <paramref name="players"/> tracked senders:
    /// <c>burst x (GlobalPerPlayer x players + GlobalFloorPlayers)</c>. Public so the doc, the
    /// refusal line and the tests all read the same number (R29 finding 3).
    /// </summary>
    public static int GlobalCapacity(int burst, int players) =>
        (burst < 1 ? 1 : burst) * PlayerBudgets(players);

    /// <summary>Refill of the global backstop, per second, for <paramref name="players"/> senders.</summary>
    public static int GlobalRefill(int perSecond, int players) =>
        (perSecond < 1 ? 1 : perSecond) * PlayerBudgets(players);

    private static int PlayerBudgets(int players)
    {
        if (players < 0) players = 0;
        if (players > MaxTrackedConnections) players = MaxTrackedConnections;
        return GlobalPerPlayer * players + GlobalFloorPlayers;
    }

    /// <summary>
    /// Release one queued packet's accounting. Call once for every <see cref="ReceiveVerdict.Queue"/>,
    /// whether it was finally dispatched or expired.
    /// </summary>
    public void NoteDequeued(long connection, int payloadBytes)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(connection, out var row))
            {
                // The row (and its byte charges) were already released by Forget/ForgetPlatform.
                // Subtracting the caller's number here is what would drive _queuedBytes negative.
                return;
            }
            if (row.Queued > 0) row.Queued--;
            if (_queuedPackets > 0) _queuedPackets--;
            // R29 finding 21.3: release one charge THIS gate made, not the number the caller
            // passed. The caller's count happens to match today; a future caller that passed the
            // wire length instead of the copy length would otherwise drift _queuedBytes until the
            // hold queue was permanently "full".
            ReleaseCharge(row, payloadBytes);
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>. Prefers the matching charge, else the oldest.</summary>
    private void ReleaseCharge(ConnRow row, int hint)
    {
        var charges = row.Charges;
        if (charges == null || charges.Count == 0) return;
        int at = charges.IndexOf(hint);
        if (at < 0) at = 0;
        _queuedBytes -= charges[at];
        charges.RemoveAt(at);
        if (_queuedBytes < 0) _queuedBytes = 0;
    }

    /// <summary>Caller holds <see cref="_gate"/>. Release every charge a row still holds.</summary>
    private void ReleaseRow(ConnRow row)
    {
        _queuedPackets -= row.Queued;
        if (_queuedPackets < 0) _queuedPackets = 0;
        row.Queued = 0;
        var charges = row.Charges;
        if (charges == null) return;
        for (int i = 0; i < charges.Count; i++) _queuedBytes -= charges[i];
        charges.Clear();
        if (_queuedBytes < 0) _queuedBytes = 0;
    }

    /// <summary>
    /// One line per <see cref="MetricWindowSeconds"/> in which any Progenitor packet arrived. This
    /// is what makes "the limiter never limits" and "the table is churning" visible.
    /// </summary>
    public bool TryTakeMetricLine(double now, out string? line)
    {
        line = null;
        lock (_gate)
        {
            if (_packetsThisWindow == 0) return false;
            if (_metricWindowStart > double.NegativeInfinity && now - _metricWindowStart < MetricWindowSeconds)
                return false;
            line = MetricLine(_connections.Count, _droppedThisWindow);
            _metricWindowStart = now;
            _packetsThisWindow = 0;
            _droppedThisWindow = 0;
            return true;
        }
    }

    /// <summary>Drop one connection row (its queue accounting goes with it).</summary>
    public void Forget(long connection)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(connection, out var row)) return;
            ReleaseRow(row);
            _connections.Remove(connection);
        }
    }

    /// <summary>
    /// Drop one player's bucket. Called on disconnect so an honest reconnect starts with a full
    /// bucket instead of the tail of its own last session (R28 finding 28.3).
    /// </summary>
    public void ForgetPlatform(ulong platformId)
    {
        if (platformId == 0) return;
        lock (_gate)
        {
            _senders.Remove(platformId);
            List<long>? drop = null;
            foreach (var kv in _connections)
            {
                if (kv.Value.PlatformId != platformId) continue;
                (drop ??= new List<long>()).Add(kv.Key);
            }
            if (drop == null) return;
            for (int i = 0; i < drop.Count; i++)
            {
                if (_connections.TryGetValue(drop[i], out var row)) ReleaseRow(row);
                _connections.Remove(drop[i]);
            }
        }
    }

    /// <summary>Drop every bucket. Uninstall and tests.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _connections.Clear();
            _senders.Clear();
            _global.Tokens = 0;
            _global.LastRefill = 0;
            _global.LastSeen = 0;
            _globalLastLog = double.NegativeInfinity;
            _metricWindowStart = double.NegativeInfinity;
            _packetsThisWindow = 0;
            _droppedThisWindow = 0;
            _queuedPackets = 0;
            _queuedBytes = 0;
        }
    }

    /// <summary>The per-window metric line.</summary>
    public static string MetricLine(int tracked, long dropped) =>
        "ReceiveGate: " + tracked.ToString(CultureInfo.InvariantCulture)
        + " connections tracked, " + dropped.ToString(CultureInfo.InvariantCulture)
        + " dropped this window";

    /// <summary>The one line a rate-limited sender produces per window.</summary>
    public static string RateLimitedLog(long connection, ulong platformId, int burst, int perSecond) =>
        "CustomEvents: rate limit tripped for " + DescribeSender(connection, platformId)
        + "; dropping Progenitor packets from it ("
        + perSecond.ToString(CultureInfo.InvariantCulture) + "/s, burst "
        + burst.ToString(CultureInfo.InvariantCulture) + "). One line per "
        + LogWindowSeconds.ToString("0", CultureInfo.InvariantCulture) + " s.";

    /// <summary>The one line the global stage produces per window.</summary>
    public static string GlobalLimitedLog(int burst, int perSecond) =>
        "CustomEvents: server-wide Progenitor receive budget exhausted ("
        + perSecond.ToString(CultureInfo.InvariantCulture) + "/s, burst "
        + burst.ToString(CultureInfo.InvariantCulture)
        + "); dropping packets from every connection. One line per "
        + LogWindowSeconds.ToString("0", CultureInfo.InvariantCulture) + " s.";

    /// <summary>The one line an unresolved connection produces per window.</summary>
    public static string UnresolvedLog(long connection, int eventId, bool queueFull) =>
        "CustomEvents: refusing event " + Fourcc(eventId) + " from unresolved connection "
        + Describe(connection) + " (no approved-user row"
        + (queueFull ? ", hold queue full" : ", past the " + UnresolvedGraceSeconds.ToString("0", CultureInfo.InvariantCulture) + " s hold window")
        + "); no handler ran. One line per "
        + LogWindowSeconds.ToString("0", CultureInfo.InvariantCulture) + " s.";

    /// <summary>The line the dispatcher writes when a held packet expires unresolved.</summary>
    public static string QueueExpiredLog(long connection, int eventId) =>
        "CustomEvents: dropping held event " + Fourcc(eventId) + " from connection "
        + Describe(connection) + "; no approved-user row appeared within "
        + UnresolvedGraceSeconds.ToString("0", CultureInfo.InvariantCulture) + " s.";

    /// <summary>
    /// Belt and braces for a server-side handler: true when the sender did not resolve to a user
    /// entity and the handler must do nothing. The dispatcher already refuses these, so a true
    /// here means a handler was reached by some other path (a plugin, a future seam) and must not
    /// act as <c>default(User)</c> — <c>User.Index == 0</c> is a real user.
    /// </summary>
    public static bool HandlerMustRefuse(bool userEntityIsNull, int userIndex) =>
        userEntityIsNull || userIndex < 0;

    /// <summary>The line a handler writes when <see cref="HandlerMustRefuse"/> is true.</summary>
    public static string HandlerRefusedLog(string module, string fourcc) =>
        (module ?? "") + ": " + (fourcc ?? "") + " from an unresolved connection (no user entity); ignoring.";

    /// <summary>Four-character wire tag of an event id, for logs (falls back to hex).</summary>
    public static string Fourcc(int eventId)
    {
        var chars = new char[4];
        for (int i = 0; i < 4; i++)
        {
            int b = (eventId >> (8 * (3 - i))) & 0xFF;
            if (b < 0x20 || b > 0x7E) return "0x" + eventId.ToString("X8", CultureInfo.InvariantCulture);
            chars[i] = (char)b;
        }
        return new string(chars);
    }

    private static string Describe(long connection) =>
        connection == 0 ? "<none>" : "0x" + connection.ToString("X", CultureInfo.InvariantCulture);

    private static string DescribeSender(long connection, ulong platformId) =>
        platformId != 0
            ? "player " + platformId.ToString(CultureInfo.InvariantCulture)
            : "connection " + Describe(connection);

    private static bool ShouldLog(ref double last, double now)
    {
        if (last > double.NegativeInfinity && now - last < LogWindowSeconds) return false;
        last = now;
        return true;
    }

    private static void Refill(Bucket bucket, double now, int perSecond, int burst)
    {
        double elapsed = now - bucket.LastRefill;
        if (elapsed <= 0) return;
        bucket.Tokens += elapsed * perSecond;
        if (bucket.Tokens > burst) bucket.Tokens = burst;
        bucket.LastRefill = now;
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private ConnRow GetOrAddConnection(long connection, double now)
    {
        if (_connections.TryGetValue(connection, out var row)) return row;
        PruneConnections(now);
        row = new ConnRow
        {
            FirstSeen = now,
            LastSeen = now,
            LastUnresolvedLog = double.NegativeInfinity,
            Bucket = NewBucket(now),
        };
        _connections[connection] = row;
        return row;
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private Bucket GetOrAddSender(ulong platformId, double now)
    {
        if (_senders.TryGetValue(platformId, out var bucket)) return bucket;
        PruneSenders(now);
        bucket = NewBucket(now);
        _senders[platformId] = bucket;
        // R29 finding 3: a player arriving raises the backstop's capacity, so it brings its own
        // allowance with it. Without this the backstop would keep whatever it was clamped to when
        // the server was empty, and a full server would be throttled by an empty server's ceiling.
        Give(_global, (double)_burst * GlobalPerPlayer, GlobalCapacity(_burst, _senders.Count));
        return bucket;
    }

    private Bucket NewBucket(double now) => new()
    {
        Tokens = _burst,
        LastRefill = now,
        LastSeen = now,
        LastRateLog = double.NegativeInfinity,
    };

    private static void Give(Bucket bucket, double tokens, int cap)
    {
        bucket.Tokens += tokens;
        if (bucket.Tokens > cap) bucket.Tokens = cap;
    }

    /// <summary>
    /// Caller holds <see cref="_gate"/>. Bound the table: drop idle rows first, then the least
    /// recently seen one.
    /// </summary>
    private void PruneConnections(double now)
    {
        if (_connections.Count < MaxTrackedConnections) return;

        List<long>? stale = null;
        foreach (var kv in _connections)
        {
            if (kv.Value.Queued > 0) continue;
            if (now - kv.Value.LastSeen < IdleForgetSeconds) continue;
            (stale ??= new List<long>()).Add(kv.Key);
        }
        if (stale != null)
            for (int i = 0; i < stale.Count; i++) _connections.Remove(stale[i]);
        if (_connections.Count < MaxTrackedConnections) return;

        long oldestKey = 0;
        double oldestSeen = double.MaxValue;
        bool found = false;
        foreach (var kv in _connections)
        {
            if (kv.Value.Queued > 0) continue;
            if (found && kv.Value.LastSeen >= oldestSeen) continue;
            oldestKey = kv.Key;
            oldestSeen = kv.Value.LastSeen;
            found = true;
        }
        if (found) _connections.Remove(oldestKey);
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void PruneSenders(double now)
    {
        if (_senders.Count < MaxTrackedConnections) return;

        List<ulong>? stale = null;
        foreach (var kv in _senders)
        {
            if (now - kv.Value.LastSeen < IdleForgetSeconds) continue;
            (stale ??= new List<ulong>()).Add(kv.Key);
        }
        if (stale != null)
            for (int i = 0; i < stale.Count; i++) _senders.Remove(stale[i]);
        if (_senders.Count < MaxTrackedConnections) return;

        ulong oldestKey = 0;
        double oldestSeen = double.MaxValue;
        bool found = false;
        foreach (var kv in _senders)
        {
            if (found && kv.Value.LastSeen >= oldestSeen) continue;
            oldestKey = kv.Key;
            oldestSeen = kv.Value.LastSeen;
            found = true;
        }
        if (found) _senders.Remove(oldestKey);
    }

    private sealed class Bucket
    {
        public double Tokens;
        public double LastRefill;
        public double LastSeen;

        /// <summary>
        /// R29 finding 21.1: the rate-limit log throttle belongs to the bucket, not the connection
        /// row. On the resolved path the bucket is the PLAYER's, so "one line per 60 s per sender"
        /// is really per sender; on the row it was per connection handle and a re-boxed handle
        /// bought a fresh allowance.
        /// </summary>
        public double LastRateLog = double.NegativeInfinity;
    }

    private sealed class ConnRow
    {
        public double FirstSeen;
        public double LastSeen;
        public double LastUnresolvedLog;
        public ulong PlatformId;
        public int Queued;
        public Bucket Bucket = new();

        /// <summary>
        /// The byte charges this gate made for currently held packets, oldest first. R29 finding
        /// 21.3: releasing one of these instead of the caller's number is what keeps
        /// <c>_queuedBytes</c> from drifting. Allocated only for a row that actually queues.
        /// </summary>
        public List<int>? Charges;
    }
}
