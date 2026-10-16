using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Networking;

// Requests, large messages and subscriptions: the two state machines, with the transport, the clock and the peer gate
// passed in. Game-free: compiled into Core, Progenitor.Testing and the test suite, which drives a client and a server
// engine against each other through the real chunk frames. ExchangeNet.cs owns the live instances.

/// <summary>
/// Limits of one request handler (<c>ctx.Requests.Handle</c>). Every request is untrusted input: the size cap is checked
/// on the first chunk, before the rest is stored, and the rate limit per user before the handler runs.
/// </summary>
public sealed class RequestOptions
{
    /// <summary>The largest encoded request accepted, 1 to <see cref="ExchangeLimits.MaxRequestBytes"/> (default 16 KiB).</summary>
    public int MaxRequestBytes { get; set; } = ExchangeLimits.DefaultMaxRequestBytes;

    /// <summary>Requests one user may burst (default 10).</summary>
    public int Burst { get; set; } = 10;

    /// <summary>Steady requests per second per user (default 5).</summary>
    public double PerSecond { get; set; } = 5;

    /// <summary>Deferred handlers: requests of one user waiting for an answer at once (default 4, at most 16).</summary>
    public int MaxPending { get; set; } = 4;

    /// <summary>Deferred handlers: seconds before an unanswered request is answered <c>Timeout</c> (default 30, at most 300).</summary>
    public double TimeoutSeconds { get; set; } = 30;

    internal string? Problem()
    {
        if (MaxRequestBytes < 1 || MaxRequestBytes > ExchangeLimits.MaxRequestBytes)
            return "MaxRequestBytes " + MaxRequestBytes + " is outside 1 to " + ExchangeLimits.MaxRequestBytes;
        if (Burst < 1 || Burst > 1000) return "Burst " + Burst + " is outside 1 to 1000";
        if (!(PerSecond > 0) || PerSecond > 1000) return "PerSecond " + PerSecond + " is outside (0, 1000]";
        if (MaxPending < 1 || MaxPending > ExchangeLimits.MaxPendingPerUser) return "MaxPending " + MaxPending + " is outside 1 to " + ExchangeLimits.MaxPendingPerUser;
        if (!(TimeoutSeconds > 0) || TimeoutSeconds > ExchangeLimits.MaxTimeoutSeconds) return "TimeoutSeconds " + TimeoutSeconds + " is outside (0, " + ExchangeLimits.MaxTimeoutSeconds + "]";
        return null;
    }

    internal RequestOptions Copy() => new()
    {
        MaxRequestBytes = MaxRequestBytes, Burst = Burst, PerSecond = PerSecond, MaxPending = MaxPending, TimeoutSeconds = TimeoutSeconds,
    };
}

/// <summary>Limits of one topic (<c>ctx.Subscriptions.Topic</c>). Subscribe and resync requests are untrusted input.</summary>
public sealed class TopicOptions
{
    /// <summary>The longest key, 1 to <see cref="ExchangeLimits.MaxKeyBytes"/> UTF-8 bytes (default 64).</summary>
    public int MaxKeyBytes { get; set; } = 64;

    /// <summary>Subscriptions of one user to this topic at once (default 8, at most 64).</summary>
    public int MaxPerUser { get; set; } = 8;

    /// <summary>Subscribes one user may burst (default 10).</summary>
    public int Burst { get; set; } = 10;

    /// <summary>Steady subscribes per second per user (default 2).</summary>
    public double PerSecond { get; set; } = 2;

    internal string? Problem()
    {
        if (MaxKeyBytes < 1 || MaxKeyBytes > ExchangeLimits.MaxKeyBytes) return "MaxKeyBytes " + MaxKeyBytes + " is outside 1 to " + ExchangeLimits.MaxKeyBytes;
        if (MaxPerUser < 1 || MaxPerUser > ExchangeLimits.MaxSubscriptionsPerUser) return "MaxPerUser " + MaxPerUser + " is outside 1 to " + ExchangeLimits.MaxSubscriptionsPerUser;
        if (Burst < 1 || Burst > 1000) return "Burst " + Burst + " is outside 1 to 1000";
        if (!(PerSecond > 0) || PerSecond > 1000) return "PerSecond " + PerSecond + " is outside (0, 1000]";
        return null;
    }

    internal TopicOptions Copy() => new() { MaxKeyBytes = MaxKeyBytes, MaxPerUser = MaxPerUser, Burst = Burst, PerSecond = PerSecond };
}

/// <summary>
/// Thrown by a request handler or a topic's snapshot function to refuse: the client gets <c>Refused</c> with
/// <see cref="Exception.Message"/> as the reason (cut to <see cref="ExchangeLimits.MaxReasonBytes"/>). Any other exception
/// is logged on the server and the client gets <c>Failed</c> without its text.
/// </summary>
public sealed class RequestRefusedException : Exception
{
    public RequestRefusedException(string reason) : base(string.IsNullOrWhiteSpace(reason) ? "refused" : reason)
    {
    }
}

/// <summary>A message type's identity on the wire: its name, hash, version and minimum reader version.</summary>
internal sealed class ExchangeType
{
    internal ExchangeType(string name, int version, int minReader)
    {
        Name = name;
        Hash = ModMessageEnvelope.MessageHash(name);
        Version = version;
        MinReader = minReader;
    }

    internal string Name { get; }
    internal uint Hash { get; }

    /// <summary>The version this build writes, which is also the newest it reads.</summary>
    internal int Version { get; }

    internal int MinReader { get; }
}

/// <summary>How one request ended (the engine's form; the facade decodes the payload).</summary>
internal sealed class ExchangeOutcome
{
    internal RequestStatus Status;
    internal string Reason = "";
    internal byte[] Payload = Array.Empty<byte>();
    internal int Version;
    internal SendResult NotSent = SendResult.Sent;

    /// <summary>The request ended because its mod is unloading (PINS1): a result for an awaiting task, not for the player.</summary>
    internal bool Unloading;
}

internal static class ExchangeCodes
{
    internal static RequestStatus ToStatus(ExchangeCode code) => code switch
    {
        ExchangeCode.Refused => RequestStatus.Refused,
        ExchangeCode.NoHandler => RequestStatus.NoHandler,
        ExchangeCode.RateLimited => RequestStatus.RateLimited,
        ExchangeCode.Busy => RequestStatus.Busy,
        ExchangeCode.TooLarge => RequestStatus.TooLarge,
        ExchangeCode.Unsupported => RequestStatus.Unsupported,
        ExchangeCode.Unloaded => RequestStatus.Unloaded,
        ExchangeCode.Timeout => RequestStatus.Timeout,
        ExchangeCode.Malformed => RequestStatus.Malformed,
        ExchangeCode.NotNegotiated => RequestStatus.NotNegotiated,
        ExchangeCode.Reset => RequestStatus.Cancelled,
        _ => RequestStatus.Failed,
    };

    internal static SubscriptionEndReason ToEnd(ExchangeCode code) => code switch
    {
        ExchangeCode.Refused => SubscriptionEndReason.Refused,
        ExchangeCode.NoTopic => SubscriptionEndReason.NoTopic,
        ExchangeCode.NoHandler => SubscriptionEndReason.NoTopic,
        ExchangeCode.Unloaded => SubscriptionEndReason.Unloaded,
        ExchangeCode.RateLimited => SubscriptionEndReason.RateLimited,
        ExchangeCode.Busy => SubscriptionEndReason.Busy,
        ExchangeCode.Unsupported => SubscriptionEndReason.Unsupported,
        ExchangeCode.Closed => SubscriptionEndReason.Closed,
        ExchangeCode.Reset => SubscriptionEndReason.Reset,
        ExchangeCode.BadKey => SubscriptionEndReason.BadKey,
        ExchangeCode.Timeout => SubscriptionEndReason.Timeout,
        ExchangeCode.NotNegotiated => SubscriptionEndReason.NotNegotiated,
        _ => SubscriptionEndReason.Failed,
    };

    /// <summary>The server ended a subscription for a reason that a later subscribe may not meet (its mod reloads, it forgot us).</summary>
    internal static bool Retryable(ExchangeCode code) => code is ExchangeCode.Unloaded or ExchangeCode.NoTopic or ExchangeCode.Reset;
}

/// <summary>
/// The client half: requests waiting for answers (correlated by id, with a timeout) and subscriptions (snapshot, then
/// deltas in sequence, resync on a gap, resubscribe after a retryable end). Main thread. Every request ends exactly once
/// through its callback; a subscription's end callback runs once unless it was disposed on this side.
/// </summary>
internal sealed class ExchangeClientEngine
{
    internal Func<double> Clock { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>May <c>mod</c> send to the server now (connected, the server's Progenitor reads the exchange, its table lists the mod)?</summary>
    internal Func<string, SendResult> Gate { get; set; } = _ => SendResult.NotConnected;

    /// <summary>Queue one body for the server; false when the send queue is full.</summary>
    internal Func<byte[], bool> Send { get; set; } = _ => false;

    /// <summary>A line worth logging (a callback threw, a frame was dropped). The caller rate-limits.</summary>
    internal Action<string, string>? Warn { get; set; }

    internal sealed class Pending
    {
        internal uint Id;
        internal string Mod = "";
        internal uint ModHash;
        internal ExchangeType Reply = null!;
        internal double Deadline;
        internal Action<ExchangeOutcome> Done = _ => { };
        internal bool Finished;
    }

    internal sealed class Sub
    {
        internal uint Id;
        internal string Mod = "";
        internal uint ModHash;
        internal string Topic = "";
        internal uint TopicHash;
        internal string Key = "";
        internal ExchangeType Snapshot = null!;
        internal ExchangeType Delta = null!;
        internal SubscriptionState State;
        internal uint LastSeq;
        internal double Deadline;
        internal int Attempts;
        internal int Resubscribes;
        internal long Deltas;
        internal SubscriptionEndReason? EndReason;
        internal string EndText = "";
        internal Action<byte[], int> OnSnapshot = (_, _) => { };
        internal Action<byte[], int> OnDelta = (_, _) => { };
        internal Action<SubscriptionEndReason, string>? OnEnded;
    }

    private readonly Dictionary<uint, Pending> _pending = new();
    private readonly Dictionary<uint, Sub> _subs = new();
    // Ids start at a random point per engine (one per Core generation), so an answer the server still holds for the
    // previous generation's request cannot be taken for a new request with the same id.
    private uint _nextRequest = RandomStart();
    private uint _nextSub = RandomStart();

    /// <summary>Subscription ids closed lately: a frame for one gets one Unsubscribe, not one per frame.</summary>
    private readonly HashSet<uint> _closedRecently = new();
    private readonly Queue<uint> _closedOrder = new();
    internal const int ClosedIdsKept = 256;

    private static uint RandomStart()
    {
        var b = new byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(b);
        return BitConverter.ToUInt32(b, 0);
    }

    /// <summary>Remember <paramref name="id"/> as closed. False when it was already remembered.</summary>
    private bool NoteClosed(uint id)
    {
        if (!_closedRecently.Add(id)) return false;
        _closedOrder.Enqueue(id);
        while (_closedOrder.Count > ClosedIdsKept) _closedRecently.Remove(_closedOrder.Dequeue());
        return true;
    }

    internal int PendingCount => _pending.Count;
    internal int SubscriptionCount => _subs.Count;

    private uint NextRequestId()
    {
        do { _nextRequest++; } while (_nextRequest == 0 || _pending.ContainsKey(_nextRequest));
        return _nextRequest;
    }

    private uint NextSubId()
    {
        do { _nextSub++; } while (_nextSub == 0 || _subs.ContainsKey(_nextSub));
        return _nextSub;
    }

    private int CountPending(string mod)
    {
        int n = 0;
        foreach (var p in _pending.Values) if (string.Equals(p.Mod, mod, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    private int CountSubs(string mod)
    {
        int n = 0;
        foreach (var s in _subs.Values) if (string.Equals(s.Mod, mod, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }

    private void Finish(Pending p, ExchangeOutcome outcome)
    {
        if (p.Finished) return;
        p.Finished = true;
        _pending.Remove(p.Id);
        try { p.Done(outcome); }
        catch (Exception e) { Warn?.Invoke("cb|" + p.Mod, "Requests: a " + p.Mod + " request callback threw: " + e.Message); }
    }

    private static ExchangeOutcome Outcome(RequestStatus status, string reason, SendResult notSent = SendResult.Sent) =>
        new() { Status = status, Reason = reason, NotSent = notSent };

    /// <summary>
    /// Send a request. The callback runs exactly once, also for a refusal at send (synchronously, before this returns).
    /// </summary>
    internal Pending Request(string mod, ExchangeType request, byte[] payload, ExchangeType reply, double timeoutSeconds, Action<ExchangeOutcome> done)
    {
        var p = new Pending { Mod = mod, ModHash = ModMessageEnvelope.ModHash(mod), Reply = reply, Done = done ?? (_ => { }) };
        if (payload.Length > ExchangeLimits.MaxRequestBytes)
        {
            Finish(p, Outcome(RequestStatus.TooLarge, "the request is " + payload.Length + " bytes; the most a server accepts is " + ExchangeLimits.MaxRequestBytes));
            return p;
        }

        var gate = Gate(mod);
        if (gate != SendResult.Sent)
        {
            Finish(p, Outcome(RequestStatus.NotSent, "not sent: " + gate, gate));
            return p;
        }

        if (CountPending(mod) >= ExchangeLimits.MaxPendingRequestsPerMod)
        {
            Finish(p, Outcome(RequestStatus.Busy, ExchangeLimits.MaxPendingRequestsPerMod + " requests of this mod are already waiting"));
            return p;
        }

        p.Id = NextRequestId();
        double timeout = timeoutSeconds > 0 ? Math.Min(timeoutSeconds, ExchangeLimits.MaxTimeoutSeconds) : ExchangeLimits.DefaultTimeoutSeconds;
        p.Deadline = Clock() + timeout;
        var body = new ExchangeBody
        {
            Op = ExchangeOp.Request, ModHash = p.ModHash, MessageHash = request.Hash, Version = request.Version, MinReader = request.MinReader,
            Id = p.Id, Payload = payload,
        };
        if (!Send(body.Pack()))
        {
            Finish(p, Outcome(RequestStatus.Busy, "the send queue to the server is full"));
            return p;
        }

        _pending[p.Id] = p;
        return p;
    }

    /// <summary>Cancel a waiting request: its callback runs with <c>Cancelled</c> and the server is told (best effort).</summary>
    internal bool Cancel(Pending p, string reason = "cancelled", bool unloading = false)
    {
        if (p == null || p.Finished) return false;
        var outcome = Outcome(RequestStatus.Cancelled, reason);
        outcome.Unloading = unloading;
        Finish(p, outcome);
        try { Send(new ExchangeBody { Op = ExchangeOp.Cancel, ModHash = p.ModHash, Id = p.Id }.Pack()); }
        catch { /* best effort */ }
        return true;
    }

    /// <summary>Subscribe. On a refusal at send the subscription comes back ended (and its end callback has run).</summary>
    internal Sub Subscribe(string mod, string topic, string key, ExchangeType snapshot, ExchangeType delta, Action<byte[], int> onSnapshot,
        Action<byte[], int> onDelta, Action<SubscriptionEndReason, string>? onEnded)
    {
        var s = new Sub
        {
            Mod = mod, ModHash = ModMessageEnvelope.ModHash(mod), Topic = topic, TopicHash = ModMessageEnvelope.MessageHash(topic), Key = key ?? "",
            Snapshot = snapshot, Delta = delta, OnSnapshot = onSnapshot, OnDelta = onDelta, OnEnded = onEnded, State = SubscriptionState.Pending,
        };
        if (System.Text.Encoding.UTF8.GetByteCount(s.Key) > ExchangeLimits.MaxKeyBytes)
        {
            End(s, SubscriptionEndReason.BadKey, "the key is over " + ExchangeLimits.MaxKeyBytes + " bytes", notify: true);
            return s;
        }

        var gate = Gate(mod);
        if (gate != SendResult.Sent)
        {
            End(s, SubscriptionEndReason.NotSent, "not sent: " + gate, notify: true);
            return s;
        }

        if (CountSubs(mod) >= ExchangeLimits.MaxSubscriptionsPerMod)
        {
            End(s, SubscriptionEndReason.Busy, ExchangeLimits.MaxSubscriptionsPerMod + " subscriptions of this mod are already open", notify: true);
            return s;
        }

        s.Id = NextSubId();
        _subs[s.Id] = s;
        s.Attempts = 1;
        s.Deadline = Clock() + ExchangeLimits.SnapshotTimeoutSeconds;
        if (!SendSubscribe(s)) End(s, SubscriptionEndReason.Busy, "the send queue to the server is full", notify: true);
        return s;
    }

    private bool SendSubscribe(Sub s) =>
        Send(new ExchangeBody { Op = ExchangeOp.Subscribe, ModHash = s.ModHash, MessageHash = s.TopicHash, Id = s.Id, Text = s.Key }.Pack());

    private bool SendResync(Sub s) =>
        Send(new ExchangeBody { Op = ExchangeOp.Resync, ModHash = s.ModHash, MessageHash = s.TopicHash, Id = s.Id, Seq = s.LastSeq }.Pack());

    private void SendUnsubscribe(uint modHash, uint topicHash, uint id)
    {
        try { Send(new ExchangeBody { Op = ExchangeOp.Unsubscribe, ModHash = modHash, MessageHash = topicHash, Id = id }.Pack()); }
        catch { /* best effort */ }
    }

    /// <summary>Close a subscription from this side: the server is told, no end callback runs.</summary>
    internal void Unsubscribe(Sub s)
    {
        if (s == null || s.State == SubscriptionState.Ended) return;
        bool wasOpen = _subs.Remove(s.Id);
        s.State = SubscriptionState.Ended;
        s.EndReason = SubscriptionEndReason.Closed;
        s.EndText = "closed";
        if (!wasOpen) return;
        NoteClosed(s.Id);
        if (Gate(s.Mod) == SendResult.Sent) SendUnsubscribe(s.ModHash, s.TopicHash, s.Id);
    }

    private void End(Sub s, SubscriptionEndReason reason, string text, bool notify)
    {
        if (s.State == SubscriptionState.Ended) return;
        if (_subs.Remove(s.Id)) NoteClosed(s.Id);
        s.State = SubscriptionState.Ended;
        s.EndReason = reason;
        s.EndText = text ?? "";
        if (!notify || s.OnEnded == null) return;
        try { s.OnEnded(reason, s.EndText); }
        catch (Exception e) { Warn?.Invoke("cb|" + s.Mod, "Subscriptions: a " + s.Mod + " end callback threw: " + e.Message); }
    }

    /// <summary>One body from the server.</summary>
    internal void Receive(ExchangeBody b)
    {
        switch (b.Op)
        {
            case ExchangeOp.Reply:
            case ExchangeOp.Error:
                OnAnswer(b);
                return;
            case ExchangeOp.Snapshot:
            case ExchangeOp.Delta:
            case ExchangeOp.Ended:
                OnSubFrame(b);
                return;
            default:
                Warn?.Invoke("op-down", "Requests: dropped a " + b.Op + " frame from the server (a client never receives one)");
                return;
        }
    }

    private void OnAnswer(ExchangeBody b)
    {
        // A late answer (after a timeout or a cancel) has no waiting request: dropped quietly.
        if (!_pending.TryGetValue(b.Id, out var p) || p.ModHash != b.ModHash) return;
        if (b.Op == ExchangeOp.Error)
        {
            Finish(p, Outcome(ExchangeCodes.ToStatus(b.Code), b.Text));
            return;
        }

        if (b.MessageHash != p.Reply.Hash)
        {
            Finish(p, Outcome(RequestStatus.Malformed, "the server answered with another message than " + p.Reply.Name));
            return;
        }

        if (b.MinReader > p.Reply.Version)
        {
            Finish(p, Outcome(RequestStatus.Unsupported, p.Reply.Name + " v" + b.Version + " needs reader " + b.MinReader + "; this mod reads " + p.Reply.Version));
            return;
        }

        Finish(p, new ExchangeOutcome { Status = RequestStatus.Ok, Payload = b.Payload, Version = b.Version });
    }

    private void OnSubFrame(ExchangeBody b)
    {
        if (!_subs.TryGetValue(b.Id, out var s) || s.ModHash != b.ModHash)
        {
            // The server still sends to a subscription this side never had (a Core swap): tell it to stop, once per id. An id
            // this side closed itself was told when it closed; frames already on their way cost nothing more.
            if (b.Op != ExchangeOp.Ended && NoteClosed(b.Id)) SendUnsubscribe(b.ModHash, 0, b.Id);

            return;
        }

        double now = Clock();
        if (b.Op == ExchangeOp.Ended)
        {
            var reason = ExchangeCodes.ToEnd(b.Code);
            if (ExchangeCodes.Retryable(b.Code) && s.Resubscribes < ExchangeLimits.MaxResubscribes)
            {
                s.State = SubscriptionState.Retrying;
                s.Deadline = now + Math.Pow(2, s.Resubscribes);
                s.Resubscribes++;
                s.EndText = b.Text;
                return;
            }

            End(s, reason, b.Text, notify: true);
            return;
        }

        var type = b.Op == ExchangeOp.Snapshot ? s.Snapshot : s.Delta;
        if (b.MessageHash != type.Hash || b.MinReader > type.Version)
        {
            string why = b.MessageHash != type.Hash
                ? "the server sent another message than " + type.Name
                : type.Name + " v" + b.Version + " needs reader " + b.MinReader + "; this mod reads " + type.Version;
            SendUnsubscribe(s.ModHash, s.TopicHash, s.Id);
            End(s, SubscriptionEndReason.Unsupported, why, notify: true);
            return;
        }

        if (b.Op == ExchangeOp.Snapshot)
        {
            s.LastSeq = b.Seq;
            s.State = SubscriptionState.Live;
            s.Attempts = 0;
            s.Resubscribes = 0;
            try { s.OnSnapshot(b.Payload, b.Version); }
            catch (Exception e) { Warn?.Invoke("snap|" + s.Mod + "|" + s.Topic, "Subscriptions: " + s.Mod + " " + s.Topic + " snapshot handler threw: " + e.Message); }
            return;
        }

        if (s.State != SubscriptionState.Live) return; // deltas before the (fresh) snapshot are covered by it
        if (b.Seq != unchecked(s.LastSeq + 1))
        {
            s.State = SubscriptionState.Resyncing;
            s.Attempts = 1;
            s.Deadline = now + ExchangeLimits.SnapshotTimeoutSeconds;
            Warn?.Invoke("gap|" + s.Mod + "|" + s.Topic, "Subscriptions: " + s.Mod + " " + s.Topic + " missed a delta (expected "
                                                          + unchecked(s.LastSeq + 1) + ", got " + b.Seq + "); asking for a fresh snapshot");
            SendResync(s);
            return;
        }

        s.LastSeq = b.Seq;
        s.Deltas++;
        try { s.OnDelta(b.Payload, b.Version); }
        catch (Exception e) { Warn?.Invoke("delta|" + s.Mod + "|" + s.Topic, "Subscriptions: " + s.Mod + " " + s.Topic + " delta handler threw: " + e.Message); }
    }

    /// <summary>Timeouts, snapshot retries and resubscribes. Call once per frame.</summary>
    internal void Tick()
    {
        double now = Clock();
        if (_pending.Count > 0)
        {
            List<Pending>? due = null;
            foreach (var p in _pending.Values) if (now >= p.Deadline) (due ??= new()).Add(p);
            if (due != null)
                foreach (var p in due)
                {
                    Finish(p, Outcome(RequestStatus.Timeout, "no answer within the timeout"));
                    try { Send(new ExchangeBody { Op = ExchangeOp.Cancel, ModHash = p.ModHash, Id = p.Id }.Pack()); }
                    catch { /* best effort */ }
                }
        }

        if (_subs.Count == 0) return;
        foreach (var s in new List<Sub>(_subs.Values))
        {
            if (s.State is not (SubscriptionState.Pending or SubscriptionState.Resyncing or SubscriptionState.Retrying) || now < s.Deadline) continue;
            if (s.State == SubscriptionState.Retrying)
            {
                if (Gate(s.Mod) != SendResult.Sent)
                {
                    // The server's table may be between a reload's withdraw and offer: wait for the next backoff step.
                    if (s.Resubscribes >= ExchangeLimits.MaxResubscribes) End(s, SubscriptionEndReason.Unloaded, "the server's mod did not come back", notify: true);
                    else s.Deadline = now + Math.Pow(2, s.Resubscribes++);
                    continue;
                }

                s.State = SubscriptionState.Pending;
                s.Attempts = 1;
                s.Deadline = now + ExchangeLimits.SnapshotTimeoutSeconds;
                if (!SendSubscribe(s)) End(s, SubscriptionEndReason.Busy, "the send queue to the server is full", notify: true);
                continue;
            }

            if (s.Attempts >= 3)
            {
                SendUnsubscribe(s.ModHash, s.TopicHash, s.Id);
                End(s, SubscriptionEndReason.Timeout, "no snapshot after 3 tries", notify: true);
                continue;
            }

            s.Attempts++;
            s.Deadline = now + ExchangeLimits.SnapshotTimeoutSeconds;
            bool ok = s.State == SubscriptionState.Pending ? SendSubscribe(s) : SendResync(s);
            if (!ok) End(s, SubscriptionEndReason.Busy, "the send queue to the server is full", notify: true);
        }
    }

    /// <summary>The client left the server: every request ends <c>Disconnected</c>, every subscription too.</summary>
    internal void Disconnected(string reason)
    {
        foreach (var p in new List<Pending>(_pending.Values)) Finish(p, Outcome(RequestStatus.Disconnected, reason));
        foreach (var s in new List<Sub>(_subs.Values)) End(s, SubscriptionEndReason.Disconnected, reason, notify: true);
    }

    /// <summary>
    /// A mod unloaded: its requests end <c>Cancelled</c> (callbacks run, so an awaited task completes), its subscriptions
    /// end <c>Closed</c> and the server is told.
    /// </summary>
    internal void ForgetMod(string mod)
    {
        foreach (var p in new List<Pending>(_pending.Values))
            if (string.Equals(p.Mod, mod, StringComparison.OrdinalIgnoreCase)) Cancel(p, "this mod unloaded", unloading: true);
        foreach (var s in new List<Sub>(_subs.Values))
        {
            if (!string.Equals(s.Mod, mod, StringComparison.OrdinalIgnoreCase)) continue;
            if (Gate(s.Mod) == SendResult.Sent) SendUnsubscribe(s.ModHash, s.TopicHash, s.Id);
            End(s, SubscriptionEndReason.Closed, "this mod unloaded", notify: true);
        }
    }

    /// <summary>Core is going away (a Core swap): tell the server to drop every subscription, without callbacks.</summary>
    internal void Teardown()
    {
        foreach (var s in new List<Sub>(_subs.Values))
        {
            SendUnsubscribe(s.ModHash, s.TopicHash, s.Id);
            End(s, SubscriptionEndReason.Closed, "Progenitor is reloading", notify: false);
        }

        // Waiting requests: the server is told (so it stops holding them); no callbacks, the plugins are going away too.
        foreach (var p in new List<Pending>(_pending.Values))
        {
            p.Finished = true;
            _pending.Remove(p.Id);
            try { Send(new ExchangeBody { Op = ExchangeOp.Cancel, ModHash = p.ModHash, Id = p.Id }.Pack()); }
            catch { /* best effort */ }
        }
    }
}

/// <summary>Replies to one request from a server handler. Main thread. Only the first answer counts.</summary>
internal sealed class ExchangeResponder
{
    private readonly ExchangeServerEngine _engine;

    internal ExchangeResponder(ExchangeServerEngine engine, int user, uint id, uint modHash, ExchangeServerEngine.Handler handler, double deadline)
    {
        _engine = engine;
        User = user;
        Id = id;
        ModHash = modHash;
        Handler = handler;
        Deadline = deadline;
    }

    internal int User { get; }
    internal uint Id { get; }
    internal uint ModHash { get; }
    internal ExchangeServerEngine.Handler Handler { get; }
    internal double Deadline { get; }

    /// <summary>Answered, refused, timed out or cancelled.</summary>
    internal bool Done { get; private set; }

    /// <summary>The client cancelled (or timed out on its side), or the user left, or the mod unloaded.</summary>
    internal bool Cancelled { get; private set; }

    internal void MarkCancelled()
    {
        Cancelled = true;
        Done = true;
    }

    /// <summary>Send the reply. False when already answered or cancelled, or when the reply is too large or cannot be queued (the client then gets an error).</summary>
    internal bool Reply(ExchangeType type, byte[] payload)
    {
        if (Done) return false;
        Done = true;
        return _engine.SendReply(this, type, payload);
    }

    internal bool Fail(ExchangeCode code, string reason)
    {
        if (Done) return false;
        Done = true;
        return _engine.SendError(User, ModHash, Id, code, reason);
    }
}

/// <summary>
/// The server half: request handlers (with per-user size, rate and concurrency limits), deferred answers with a server
/// timeout, and topics (snapshot on subscribe, deltas fanned out to every subscriber of a key with a per-subscription
/// sequence, a fresh snapshot after a gap or a full send queue). Main thread.
/// </summary>
internal sealed class ExchangeServerEngine
{
    internal ExchangeServerEngine(ModMessageRouter names) => Names = names ?? throw new ArgumentNullException(nameof(names));

    /// <summary>The C2 name table: request, reply, topic, snapshot and delta names are claimed there, with its collision rules.</summary>
    internal ModMessageRouter Names { get; }

    internal Func<double> Clock { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>May the server send <c>mod</c>'s traffic to this user now?</summary>
    internal Func<int, string, SendResult> Gate { get; set; } = (_, _) => SendResult.NotConnected;

    /// <summary>
    /// Queue one body for a user; false when that user's send queue is full. The body is copied before this returns (a
    /// fan-out patches and reuses one buffer).
    /// </summary>
    internal Func<int, byte[], bool> Send { get; set; } = (_, _) => false;

    /// <summary>Does this user's send queue have room for a snapshot now? Checked before a snapshot is built, so a full queue costs no build.</summary>
    internal Func<int, bool> HasRoom { get; set; } = _ => true;

    /// <summary>Snapshots built so far (every call of a topic's snapshot function). Diagnostics and tests.</summary>
    internal long SnapshotBuilds { get; private set; }

    /// <summary>At most this many snapshots are built per <see cref="Tick"/>; the rest wait for the next frame.</summary>
    internal const int MaxSnapshotBuildsPerTick = 8;

    /// <summary>The user can be reached on the exchange channel, but the server does not see the mod in their feature table.</summary>
    private static bool NotNegotiated(SendResult gate) => gate is SendResult.PeerLacksMod or SendResult.PeerNotReady;

    private static string NotNegotiatedText(string mod, SendResult gate) =>
        "the server does not see " + mod + " in your client's feature table (" + gate + ")";

    /// <summary>The user's version of a mod, from its feature table (for <see cref="MessageInfo.SenderModVersion"/>).</summary>
    internal Func<int, string, string?>? PeerModVersion { get; set; }

    /// <summary>A line worth logging, with a key the caller rate-limits on.</summary>
    internal Action<string, string>? Warn { get; set; }

    internal delegate void HandlerRun(int user, byte[] payload, MessageInfo info, ExchangeResponder responder);

    /// <summary>Build a topic's snapshot for a user and key. Throws <see cref="RequestRefusedException"/> to refuse.</summary>
    internal delegate byte[] SnapshotRun(int user, string key);

    internal sealed class Handler
    {
        internal string Mod = "";
        internal ExchangeType Request = null!;
        internal RequestOptions Options = new();
        internal bool Deferred;
        internal HandlerRun Run = null!;
        internal object Token = new();
    }

    internal sealed class Topic
    {
        internal string Mod = "";
        internal uint ModHash;
        internal string Name = "";
        internal uint Hash;
        internal ExchangeType Snapshot = null!;
        internal ExchangeType Delta = null!;
        internal TopicOptions Options = new();
        internal SnapshotRun Open = null!;
        internal Action<int, string>? Opened { get; set; }
        internal Action<int, string>? Closed { get; set; }
        internal readonly Dictionary<string, HashSet<Sub>> ByKey = new(StringComparer.Ordinal);
        internal bool Disposed;
    }

    internal sealed class Sub
    {
        internal int User;
        internal uint Id;
        internal Topic Topic = null!;
        internal string Key = "";
        internal uint Seq;
        internal bool NeedsSnapshot;
        internal double LastSnapshot = double.NegativeInfinity;
    }

    private sealed class Mod
    {
        internal string Guid = "";
        internal readonly Dictionary<uint, Handler> Handlers = new();
        internal readonly Dictionary<uint, Topic> Topics = new();
    }

    private sealed class UserState
    {
        internal readonly Dictionary<uint, Sub> Subs = new();
        internal readonly Dictionary<(uint Mod, uint Id), ExchangeResponder> Pending = new();
        internal readonly Dictionary<object, ExchangeBucket> Buckets = new();
    }

    private readonly Dictionary<uint, Mod> _mods = new();
    private readonly Dictionary<int, UserState> _users = new();
    private readonly HashSet<Sub> _resync = new();

    private UserState UserOf(int user)
    {
        if (!_users.TryGetValue(user, out var u)) _users[user] = u = new UserState();
        return u;
    }

    private string? ClaimAll(string mod, params string[] names)
    {
        foreach (var n in names)
        {
            string? refusal = Names.Claim(mod, n, out _, out _);
            if (refusal != null) return refusal;
        }

        return null;
    }

    /// <summary>Register a handler. Null on success, else the refusal line (a bad option, a collision, a second handler).</summary>
    internal string? RegisterHandler(string mod, ExchangeType request, ExchangeType reply, RequestOptions options, bool deferred, HandlerRun run, out object token)
    {
        token = new object();
        if (run == null) throw new ArgumentNullException(nameof(run));
        string? problem = (options ?? new RequestOptions()).Problem();
        if (problem != null) return "Requests: " + mod + " " + request.Name + ": " + problem + ".";
        if (string.Equals(request.Name, reply.Name, StringComparison.Ordinal))
            return "Requests: " + mod + " " + request.Name + ": the request and the reply need different message names.";
        string? refusal = ClaimAll(mod, request.Name, reply.Name);
        if (refusal != null) return refusal;
        uint modHash = ModMessageEnvelope.ModHash(mod);
        if (!_mods.TryGetValue(modHash, out var m)) _mods[modHash] = m = new Mod { Guid = mod.Trim() };
        if (m.Handlers.ContainsKey(request.Hash)) return "Requests: " + mod + " already handles '" + request.Name + "'; one handler per request.";
        if (m.Topics.ContainsKey(request.Hash)) return "Requests: " + mod + " '" + request.Name + "' is already a topic name.";
        m.Handlers[request.Hash] = new Handler { Mod = m.Guid, Request = request, Options = options!.Copy(), Deferred = deferred, Run = run, Token = token };
        return null;
    }

    internal bool UnregisterHandler(string mod, ExchangeType request, object token)
    {
        if (!_mods.TryGetValue(ModMessageEnvelope.ModHash(mod), out var m) || !m.Handlers.TryGetValue(request.Hash, out var h) || !ReferenceEquals(h.Token, token))
            return false;
        m.Handlers.Remove(request.Hash);
        DropBuckets(h);
        foreach (var u in _users.Values)
            foreach (var kv in new List<KeyValuePair<(uint, uint), ExchangeResponder>>(u.Pending))
                if (ReferenceEquals(kv.Value.Handler, h))
                {
                    kv.Value.Fail(ExchangeCode.Unloaded, "the handler was removed");
                    u.Pending.Remove(kv.Key);
                }

        return true;
    }

    /// <summary>Register a topic. Null on success with <paramref name="topic"/>, else the refusal line.</summary>
    internal string? RegisterTopic(string mod, string name, ExchangeType snapshot, ExchangeType delta, TopicOptions options, SnapshotRun open, out Topic? topic)
    {
        topic = null;
        if (open == null) throw new ArgumentNullException(nameof(open));
        string? problem = (options ?? new TopicOptions()).Problem();
        if (problem != null) return "Subscriptions: " + mod + " " + name + ": " + problem + ".";
        if (string.Equals(snapshot.Name, delta.Name, StringComparison.Ordinal) || string.Equals(name, snapshot.Name, StringComparison.Ordinal)
            || string.Equals(name, delta.Name, StringComparison.Ordinal))
            return "Subscriptions: " + mod + " " + name + ": the topic, its snapshot and its delta need three different names.";
        string? refusal = ClaimAll(mod, name, snapshot.Name, delta.Name);
        if (refusal != null) return refusal;
        uint modHash = ModMessageEnvelope.ModHash(mod);
        if (!_mods.TryGetValue(modHash, out var m)) _mods[modHash] = m = new Mod { Guid = mod.Trim() };
        uint hash = ModMessageEnvelope.MessageHash(name);
        if (m.Topics.ContainsKey(hash)) return "Subscriptions: " + mod + " already has a topic '" + name + "'.";
        if (m.Handlers.ContainsKey(hash)) return "Subscriptions: " + mod + " '" + name + "' is already a request name.";
        topic = new Topic
        {
            Mod = m.Guid, ModHash = modHash, Name = name, Hash = hash, Snapshot = snapshot, Delta = delta, Options = options!.Copy(), Open = open,
        };
        m.Topics[hash] = topic;
        return null;
    }

    /// <summary>Remove a topic: every subscriber gets an end frame with <paramref name="code"/>.</summary>
    internal void UnregisterTopic(Topic topic, ExchangeCode code = ExchangeCode.Closed, string reason = "the topic was closed")
    {
        if (topic == null || topic.Disposed) return;
        topic.Disposed = true;
        if (_mods.TryGetValue(topic.ModHash, out var m) && m.Topics.TryGetValue(topic.Hash, out var t) && ReferenceEquals(t, topic)) m.Topics.Remove(topic.Hash);
        foreach (var set in new List<HashSet<Sub>>(topic.ByKey.Values))
            foreach (var s in new List<Sub>(set))
            {
                SendEnded(s.User, topic.ModHash, s.Id, code, reason);
                RemoveSub(s, notify: false);
            }

        topic.ByKey.Clear();
        DropBuckets(topic);
    }

    private void DropBuckets(object key)
    {
        foreach (var u in _users.Values) u.Buckets.Remove(key);
    }

    // ---- receive ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The first chunk of a body from <paramref name="user"/> declared <paramref name="total"/> bytes: null to collect it,
    /// or a reason to skip the rest (a request over its handler's cap is answered <c>TooLarge</c> here).
    /// </summary>
    internal string? Admit(int user, byte[] firstChunk, int total)
    {
        if (!ExchangeBody.TryPeek(firstChunk, out var head, out int used, out var error)) return "a malformed exchange body (" + error + ")";
        if (head.Op != ExchangeOp.Request)
            return total > 4096 ? "a " + head.Op + " frame of " + total + " bytes (a client's control frames are small)" : null;
        if (!_mods.TryGetValue(head.ModHash, out var m) || !m.Handlers.TryGetValue(head.MessageHash, out var h))
        {
            // Answered now, and the rest is not stored: a request nothing handles costs no reassembly memory.
            SendError(user, head.ModHash, head.Id, ExchangeCode.NoHandler,
                m == null ? "the server does not run that mod" : "no handler for that request on the server");
            return "a request for " + (m == null ? "a mod this server does not run" : m.Guid + " that it has no handler for");
        }

        int payload = total - used;
        if (payload <= h.Options.MaxRequestBytes) return null;
        SendError(user, head.ModHash, head.Id, ExchangeCode.TooLarge, "the request is " + payload + " bytes; " + h.Request.Name + " accepts at most " + h.Options.MaxRequestBytes);
        return m.Guid + " " + h.Request.Name + " request of " + payload + " bytes is over its " + h.Options.MaxRequestBytes + "-byte cap";
    }

    /// <summary>One whole body from a user.</summary>
    internal void Receive(int user, ExchangeBody b)
    {
        switch (b.Op)
        {
            case ExchangeOp.Request: OnRequest(user, b); return;
            case ExchangeOp.Cancel: OnCancel(user, b); return;
            case ExchangeOp.Subscribe: OnSubscribe(user, b); return;
            case ExchangeOp.Unsubscribe: OnUnsubscribe(user, b); return;
            case ExchangeOp.Resync: OnResync(user, b); return;
            default:
                Warn?.Invoke("op-up", "Requests: dropped a " + b.Op + " frame from user " + user + " (a server never receives one)");
                return;
        }
    }

    private bool TakeToken(UserState u, object key, double burst, double perSecond)
    {
        double now = Clock();
        if (!u.Buckets.TryGetValue(key, out var bucket)) u.Buckets[key] = bucket = new ExchangeBucket(burst, perSecond, now);
        return bucket.TryTake(now);
    }

    private void OnRequest(int user, ExchangeBody b)
    {
        if (!_mods.TryGetValue(b.ModHash, out var m))
        {
            SendError(user, b.ModHash, b.Id, ExchangeCode.NoHandler, "the server does not run that mod");
            return;
        }

        if (!m.Handlers.TryGetValue(b.MessageHash, out var h))
        {
            SendError(user, b.ModHash, b.Id, ExchangeCode.NoHandler, "no handler for that request on the server");
            return;
        }

        if (b.MinReader > h.Request.Version)
        {
            SendError(user, b.ModHash, b.Id, ExchangeCode.Unsupported,
                h.Request.Name + " v" + b.Version + " needs reader " + b.MinReader + "; the server reads " + h.Request.Version);
            return;
        }

        if (b.Payload.Length > h.Options.MaxRequestBytes)
        {
            SendError(user, b.ModHash, b.Id, ExchangeCode.TooLarge, "the request is " + b.Payload.Length + " bytes; " + h.Request.Name + " accepts at most " + h.Options.MaxRequestBytes);
            return;
        }

        var gate = Gate(user, m.Guid);
        if (gate != SendResult.Sent)
        {
            // The handler would run for nothing: its reply could not go back.
            if (NotNegotiated(gate)) SendError(user, b.ModHash, b.Id, ExchangeCode.NotNegotiated, NotNegotiatedText(m.Guid, gate));
            return;
        }

        var u = UserOf(user);
        if (!TakeToken(u, h, h.Options.Burst, h.Options.PerSecond))
        {
            SendError(user, b.ModHash, b.Id, ExchangeCode.RateLimited, "too many " + h.Request.Name + " requests; at most " + h.Options.PerSecond.ToString(CultureInfo.InvariantCulture) + " per second");
            return;
        }

        if (u.Pending.ContainsKey((b.ModHash, b.Id)))
        {
            Warn?.Invoke("dup", "Requests: user " + user + " reused a waiting request id; dropped");
            return;
        }

        if (h.Deferred)
        {
            int mine = 0;
            foreach (var r in u.Pending.Values) if (ReferenceEquals(r.Handler, h)) mine++;
            if (mine >= h.Options.MaxPending || u.Pending.Count >= ExchangeLimits.MaxPendingPerUser)
            {
                SendError(user, b.ModHash, b.Id, ExchangeCode.Busy, "too many requests are waiting for an answer");
                return;
            }
        }

        var responder = new ExchangeResponder(this, user, b.Id, b.ModHash, h, Clock() + h.Options.TimeoutSeconds);
        var info = new MessageInfo
        {
            Name = h.Request.Name, SenderVersion = b.Version, FromServer = false, UserIndex = user,
            SenderModVersion = SafeModVersion(user, m.Guid),
        };
        if (h.Deferred) u.Pending[(b.ModHash, b.Id)] = responder;
        try { h.Run(user, b.Payload, info, responder); }
        catch (RequestRefusedException e) { responder.Fail(ExchangeCode.Refused, e.Message); }
        catch (FormatException e)
        {
            responder.Fail(ExchangeCode.Malformed, "the request could not be decoded");
            Warn?.Invoke("decode|" + m.Guid + "|" + h.Request.Name, "Requests: " + m.Guid + " " + h.Request.Name + " v" + b.Version + " from user " + user + " could not be decoded: " + e.Message);
        }
        catch (Exception e)
        {
            responder.Fail(ExchangeCode.Failed, "the server's handler failed");
            var inner = e is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
            Warn?.Invoke("threw|" + m.Guid + "|" + h.Request.Name, "Requests: " + m.Guid + " " + h.Request.Name + " handler threw: " + inner);
        }

        if (!h.Deferred && !responder.Done)
            responder.Fail(ExchangeCode.Failed, "the server's handler did not answer");
        if (responder.Done) u.Pending.Remove((b.ModHash, b.Id));
    }

    private string? SafeModVersion(int user, string mod)
    {
        try { return PeerModVersion?.Invoke(user, mod); }
        catch { return null; }
    }

    private void OnCancel(int user, ExchangeBody b)
    {
        if (!_users.TryGetValue(user, out var u) || !u.Pending.TryGetValue((b.ModHash, b.Id), out var r)) return;
        r.MarkCancelled();
        u.Pending.Remove((b.ModHash, b.Id));
    }

    internal bool SendReply(ExchangeResponder r, ExchangeType type, byte[] payload)
    {
        if (_users.TryGetValue(r.User, out var u)) u.Pending.Remove((r.ModHash, r.Id));
        if (payload.Length > ExchangeLimits.MaxBodyBytes)
        {
            SendError(r.User, r.ModHash, r.Id, ExchangeCode.TooLarge, "the reply is " + payload.Length + " bytes; at most " + ExchangeLimits.MaxBodyBytes + " can be sent");
            return false;
        }

        var gate = Gate(r.User, r.Handler.Mod);
        if (gate != SendResult.Sent)
        {
            if (NotNegotiated(gate)) SendError(r.User, r.ModHash, r.Id, ExchangeCode.NotNegotiated, NotNegotiatedText(r.Handler.Mod, gate));
            return false;
        }

        var body = new ExchangeBody
        {
            Op = ExchangeOp.Reply, ModHash = r.ModHash, MessageHash = type.Hash, Version = type.Version, MinReader = type.MinReader, Id = r.Id, Payload = payload,
        };
        if (Send(r.User, body.Pack())) return true;
        SendError(r.User, r.ModHash, r.Id, ExchangeCode.Busy, "the server's send queue for you is full");
        return false;
    }

    internal bool SendError(int user, uint modHash, uint id, ExchangeCode code, string reason)
    {
        if (_users.TryGetValue(user, out var u)) u.Pending.Remove((modHash, id));
        return Send(user, new ExchangeBody { Op = ExchangeOp.Error, Code = code, ModHash = modHash, Id = id, Text = ExchangeBody.Cut(reason, ExchangeLimits.MaxReasonBytes) }.Pack());
    }

    private bool SendEnded(int user, uint modHash, uint id, ExchangeCode code, string reason) =>
        Send(user, new ExchangeBody { Op = ExchangeOp.Ended, Code = code, ModHash = modHash, Id = id, Text = ExchangeBody.Cut(reason, ExchangeLimits.MaxReasonBytes) }.Pack());

    private void OnSubscribe(int user, ExchangeBody b)
    {
        if (!_mods.TryGetValue(b.ModHash, out var m) || !m.Topics.TryGetValue(b.MessageHash, out var topic))
        {
            SendEnded(user, b.ModHash, b.Id, ExchangeCode.NoTopic, "the server runs no such topic");
            return;
        }

        var gate = Gate(user, topic.Mod);
        if (gate != SendResult.Sent)
        {
            if (NotNegotiated(gate)) SendEnded(user, b.ModHash, b.Id, ExchangeCode.NotNegotiated, NotNegotiatedText(topic.Mod, gate));
            return;
        }

        var u = UserOf(user);
        if (System.Text.Encoding.UTF8.GetByteCount(b.Text) > topic.Options.MaxKeyBytes)
        {
            SendEnded(user, b.ModHash, b.Id, ExchangeCode.BadKey, "the key is over " + topic.Options.MaxKeyBytes + " bytes");
            return;
        }

        if (!TakeToken(u, topic, topic.Options.Burst, topic.Options.PerSecond))
        {
            SendEnded(user, b.ModHash, b.Id, ExchangeCode.RateLimited, "too many subscribes to " + topic.Name);
            return;
        }

        if (u.Subs.TryGetValue(b.Id, out var existing))
        {
            if (ReferenceEquals(existing.Topic, topic) && string.Equals(existing.Key, b.Text, StringComparison.Ordinal))
            {
                // The client asked again (its snapshot did not arrive): a fresh snapshot, at the paced rate.
                existing.NeedsSnapshot = true;
                _resync.Add(existing);
                return;
            }

            RemoveSub(existing, notify: true);
        }

        int onTopic = 0;
        foreach (var s in u.Subs.Values) if (ReferenceEquals(s.Topic, topic)) onTopic++;
        if (onTopic >= topic.Options.MaxPerUser || u.Subs.Count >= ExchangeLimits.MaxSubscriptionsPerUser)
        {
            SendEnded(user, b.ModHash, b.Id, ExchangeCode.Busy, "too many subscriptions");
            return;
        }

        var sub = new Sub { User = user, Id = b.Id, Topic = topic, Key = b.Text };
        if (!SendSnapshot(sub, first: true)) return;
        u.Subs[sub.Id] = sub;
        if (!topic.ByKey.TryGetValue(sub.Key, out var set)) topic.ByKey[sub.Key] = set = new HashSet<Sub>();
        set.Add(sub);
        if (topic.Opened != null)
        {
            try { topic.Opened(user, sub.Key); }
            catch (Exception e) { Warn?.Invoke("opened|" + topic.Mod + "|" + topic.Name, "Subscriptions: " + topic.Mod + " " + topic.Name + " Opened handler threw: " + e.Message); }
        }
    }

    /// <summary>
    /// Build and queue a snapshot. On a refusal, a throw or a full queue the client gets an end frame and, for a live
    /// subscription, it is removed. False when no snapshot went out.
    /// </summary>
    private bool SendSnapshot(Sub sub, bool first)
    {
        var topic = sub.Topic;
        // Every attempt counts toward the one-per-second pacing, whatever its outcome, and nothing is built for a user
        // who cannot take it now: a full queue or a closed gate costs a check, not a snapshot.
        sub.LastSnapshot = Clock();
        var gate = Gate(sub.User, topic.Mod);
        if (gate != SendResult.Sent || !HasRoom(sub.User))
        {
            if (first)
            {
                if (gate == SendResult.Sent) SendEnded(sub.User, topic.ModHash, sub.Id, ExchangeCode.Busy, "the server's send queue for you is full");
                else if (NotNegotiated(gate)) SendEnded(sub.User, topic.ModHash, sub.Id, ExchangeCode.NotNegotiated, NotNegotiatedText(topic.Mod, gate));
                return false;
            }

            sub.NeedsSnapshot = true;
            _resync.Add(sub);
            return false;
        }

        SnapshotBuilds++;
        byte[] payload;
        ExchangeCode fail = ExchangeCode.None;
        string reason = "";
        try { payload = topic.Open(sub.User, sub.Key) ?? Array.Empty<byte>(); }
        catch (RequestRefusedException e)
        {
            payload = Array.Empty<byte>();
            fail = ExchangeCode.Refused;
            reason = e.Message;
        }
        catch (Exception e)
        {
            payload = Array.Empty<byte>();
            fail = ExchangeCode.Failed;
            reason = "the server's snapshot failed";
            var inner = e is System.Reflection.TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
            Warn?.Invoke("snapthrew|" + topic.Mod + "|" + topic.Name, "Subscriptions: " + topic.Mod + " " + topic.Name + " snapshot threw: " + inner);
        }

        if (fail == ExchangeCode.None && payload.Length > ExchangeLimits.MaxBodyBytes)
        {
            fail = ExchangeCode.TooLarge;
            reason = "the snapshot is over " + ExchangeLimits.MaxBodyBytes + " bytes";
            Warn?.Invoke("snapbig|" + topic.Mod + "|" + topic.Name, "Subscriptions: " + topic.Mod + " " + topic.Name + " snapshot is " + payload.Length + " bytes; the limit is " + ExchangeLimits.MaxBodyBytes);
        }

        if (fail == ExchangeCode.None)
        {
            var body = new ExchangeBody
            {
                Op = ExchangeOp.Snapshot, ModHash = topic.ModHash, MessageHash = topic.Snapshot.Hash, Version = topic.Snapshot.Version,
                MinReader = topic.Snapshot.MinReader, Id = sub.Id, Seq = sub.Seq, Payload = payload,
            };
            if (Send(sub.User, body.Pack()))
            {
                sub.NeedsSnapshot = false;
                _resync.Remove(sub);
                return true;
            }

            // A full queue on the first snapshot ends the subscription; a live one waits for room.
            if (!first)
            {
                sub.NeedsSnapshot = true;
                _resync.Add(sub);
                return false;
            }

            fail = ExchangeCode.Busy;
            reason = "the server's send queue for you is full";
        }

        SendEnded(sub.User, topic.ModHash, sub.Id, fail, reason);
        if (!first) RemoveSub(sub, notify: true);
        return false;
    }

    private void OnUnsubscribe(int user, ExchangeBody b)
    {
        if (!_users.TryGetValue(user, out var u) || !u.Subs.TryGetValue(b.Id, out var s) || s.Topic.ModHash != b.ModHash) return;
        RemoveSub(s, notify: true);
    }

    private void OnResync(int user, ExchangeBody b)
    {
        if (!_users.TryGetValue(user, out var u) || !u.Subs.TryGetValue(b.Id, out var s) || s.Topic.ModHash != b.ModHash)
        {
            SendEnded(user, b.ModHash, b.Id, ExchangeCode.Reset, "the server does not know that subscription");
            return;
        }

        // A resync costs a snapshot, so it spends the topic's subscribe budget. Over it, the request is dropped: the client
        // asks again after its snapshot timeout.
        if (!TakeToken(u, s.Topic, s.Topic.Options.Burst, s.Topic.Options.PerSecond))
        {
            Warn?.Invoke("resync-rate", "Subscriptions: a user's resyncs of " + s.Topic.Mod + " " + s.Topic.Name + " are over the topic's rate; dropped until the next try");
            return;
        }

        s.NeedsSnapshot = true;
        _resync.Add(s);
    }

    private void RemoveSub(Sub s, bool notify)
    {
        _resync.Remove(s);
        if (_users.TryGetValue(s.User, out var u) && u.Subs.TryGetValue(s.Id, out var held) && ReferenceEquals(held, s)) u.Subs.Remove(s.Id);
        if (s.Topic.ByKey.TryGetValue(s.Key, out var set))
        {
            set.Remove(s);
            if (set.Count == 0) s.Topic.ByKey.Remove(s.Key);
        }

        if (!notify || s.Topic.Closed == null) return;
        try { s.Topic.Closed(s.User, s.Key); }
        catch (Exception e) { Warn?.Invoke("closed|" + s.Topic.Mod + "|" + s.Topic.Name, "Subscriptions: " + s.Topic.Mod + " " + s.Topic.Name + " Closed handler threw: " + e.Message); }
    }

    // ---- publish ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Send one delta to every subscriber of <paramref name="key"/>. Encoded once; each subscriber gets its own sequence
    /// number. A subscriber whose queue is full gets a fresh snapshot instead once there is room. Returns the subscribers
    /// it was queued for.
    /// </summary>
    internal int Publish(Topic topic, string key, byte[] payload)
    {
        if (topic == null || topic.Disposed || !topic.ByKey.TryGetValue(key ?? "", out var set) || set.Count == 0) return 0;
        if (payload.Length > ExchangeLimits.MaxBodyBytes)
        {
            Warn?.Invoke("deltabig|" + topic.Mod + "|" + topic.Name, "Subscriptions: " + topic.Mod + " " + topic.Name + " delta is " + payload.Length + " bytes; the limit is " + ExchangeLimits.MaxBodyBytes + "; not sent");
            return 0;
        }

        // One buffer for every subscriber: only the id and the sequence differ, and Send copies before it returns.
        byte[] body = new ExchangeBody
        {
            Op = ExchangeOp.Delta, ModHash = topic.ModHash, MessageHash = topic.Delta.Hash, Version = topic.Delta.Version, MinReader = topic.Delta.MinReader,
            Payload = payload,
        }.Pack();
        int sent = 0;
        foreach (var s in new List<Sub>(set))
        {
            if (s.NeedsSnapshot) continue; // the coming snapshot covers this delta
            if (Gate(s.User, topic.Mod) != SendResult.Sent)
            {
                // Not reachable for this mod now: a fresh snapshot once it is, instead of a silent gap.
                s.NeedsSnapshot = true;
                _resync.Add(s);
                continue;
            }

            uint seq = unchecked(s.Seq + 1);
            ExchangeBody.WriteU32(body, ExchangeBody.IdOffset, s.Id);
            ExchangeBody.WriteU32(body, ExchangeBody.SeqOffset, seq);
            if (Send(s.User, body))
            {
                s.Seq = seq;
                sent++;
            }
            else
            {
                s.NeedsSnapshot = true;
                _resync.Add(s);
            }
        }

        return sent;
    }

    /// <summary>Send every subscriber of <paramref name="key"/> a fresh snapshot (paced). Returns how many were marked.</summary>
    internal int Resync(Topic topic, string key)
    {
        if (topic == null || topic.Disposed || !topic.ByKey.TryGetValue(key ?? "", out var set)) return 0;
        foreach (var s in set)
        {
            s.NeedsSnapshot = true;
            s.LastSnapshot = double.NegativeInfinity;
            _resync.Add(s);
        }

        return set.Count;
    }

    internal IReadOnlyList<int> Subscribers(Topic topic, string key)
    {
        var list = new List<int>();
        if (topic != null && topic.ByKey.TryGetValue(key ?? "", out var set))
            foreach (var s in set) list.Add(s.User);
        list.Sort();
        return list;
    }

    internal IReadOnlyList<string> Keys(Topic topic)
    {
        var list = topic == null ? new List<string>() : new List<string>(topic.ByKey.Keys);
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    // ---- time and lifetime --------------------------------------------------------------------------------------------

    /// <summary>Deferred timeouts and paced snapshots. Call once per frame.</summary>
    internal void Tick()
    {
        double now = Clock();
        foreach (var kv in _users)
        {
            if (kv.Value.Pending.Count == 0) continue;
            foreach (var r in new List<ExchangeResponder>(kv.Value.Pending.Values))
                if (now >= r.Deadline) r.Fail(ExchangeCode.Timeout, "the server did not answer in time");
        }

        if (_resync.Count == 0) return;
        long budgetEnd = SnapshotBuilds + MaxSnapshotBuildsPerTick;
        foreach (var s in new List<Sub>(_resync))
        {
            if (!s.NeedsSnapshot || s.Topic.Disposed)
            {
                _resync.Remove(s);
                continue;
            }

            if (now - s.LastSnapshot < ExchangeLimits.MinSnapshotIntervalSeconds) continue;
            if (SnapshotBuilds >= budgetEnd) break;
            SendSnapshot(s, first: false);
        }
    }

    /// <summary>A user left: their subscriptions close (Closed handlers run), their waiting requests are cancelled.</summary>
    internal void UserLeft(int user)
    {
        if (!_users.TryGetValue(user, out var u)) return;
        _users.Remove(user);
        foreach (var r in u.Pending.Values) r.MarkCancelled();
        foreach (var s in new List<Sub>(u.Subs.Values)) RemoveSub(s, notify: true);
    }

    /// <summary>
    /// A user handshaked again (a client Core swap, or a refresh): what the server held for them belongs to the client's
    /// previous state. Live subscriptions are told <c>Reset</c> (a client that still has them subscribes again) and
    /// dropped; waiting deferred requests are answered <c>Reset</c> (the client sees <c>Cancelled</c>).
    /// </summary>
    internal void ResetUser(int user)
    {
        if (!_users.TryGetValue(user, out var u)) return;
        foreach (var s in new List<Sub>(u.Subs.Values)) SendEnded(user, s.Topic.ModHash, s.Id, ExchangeCode.Reset, "the server reset this connection");
        // Waiting deferred requests are answered (the client gets Cancelled with this reason) rather than left to time out.
        foreach (var r in new List<ExchangeResponder>(u.Pending.Values)) r.Fail(ExchangeCode.Reset, "the server reset this connection");
        UserLeft(user);
    }

    /// <summary>A mod unloaded: its subscribers are told <c>Unloaded</c> (they resubscribe), its waiting requests are answered <c>Unloaded</c>.</summary>
    internal void ForgetMod(string mod)
    {
        uint modHash = ModMessageEnvelope.ModHash(mod ?? "");
        if (!_mods.TryGetValue(modHash, out var m) || !string.Equals(m.Guid, (mod ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return;
        _mods.Remove(modHash);
        foreach (var h in m.Handlers.Values) DropBuckets(h);
        foreach (var t in new List<Topic>(m.Topics.Values)) UnregisterTopic(t, ExchangeCode.Unloaded, "the server's mod unloaded");
        foreach (var u in _users.Values)
            foreach (var kv in new List<KeyValuePair<(uint Mod, uint Id), ExchangeResponder>>(u.Pending))
                if (kv.Key.Mod == modHash) kv.Value.Fail(ExchangeCode.Unloaded, "the server's mod unloaded");
    }

    /// <summary>Core is going away: tell every subscriber <c>Unloaded</c> and answer every waiting request (best effort).</summary>
    internal void Teardown()
    {
        foreach (var m in new List<Mod>(_mods.Values)) ForgetMod(m.Guid);
        _users.Clear();
        _resync.Clear();
    }

    internal int UserCount => _users.Count;

    internal int SubscriptionCount(int user) => _users.TryGetValue(user, out var u) ? u.Subs.Count : 0;

    internal int PendingCount(int user) => _users.TryGetValue(user, out var u) ? u.Pending.Count : 0;
}
