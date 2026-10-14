using System;
using System.Collections.Generic;
using Progenitor.Networking;

namespace Progenitor.Testing;

/// <summary>The outcome of one <see cref="ExchangeCompat.Call{TRequest, TReply}"/>.</summary>
public sealed class ExchangeCall<TReply> where TReply : class
{
    internal ExchangeCall(RequestStatus status, TReply? reply, string reason, int requestBytes, int replyBytes, int packetsUp, int packetsDown)
    {
        Status = status;
        Reply = reply;
        Reason = reason;
        RequestBytes = requestBytes;
        ReplyBytes = replyBytes;
        PacketsUp = packetsUp;
        PacketsDown = packetsDown;
    }

    public RequestStatus Status { get; }
    public bool Ok => Status == RequestStatus.Ok;

    /// <summary>The reply as the client decoded it (only when <see cref="Ok"/>).</summary>
    public TReply? Reply { get; }

    public string Reason { get; }

    /// <summary>The encoded request and reply payloads, in bytes.</summary>
    public int RequestBytes { get; }

    public int ReplyBytes { get; }

    /// <summary>Exchange packets client to server and server to client: more than one each way means the payload was chunked.</summary>
    public int PacketsUp { get; }

    public int PacketsDown { get; }

    public override string ToString() => Status + (Reason.Length > 0 ? " (" + Reason + ")" : "") + ", " + RequestBytes + " B up in " + PacketsUp
                                         + " packet(s), " + ReplyBytes + " B down in " + PacketsDown + " packet(s)";
}

/// <summary>A game-free subscription to a <see cref="ExchangeCompat.Topic{TSnapshot, TDelta}"/>: what the client received.</summary>
public sealed class TopicClient<TSnapshot, TDelta> where TSnapshot : class where TDelta : class
{
    internal ExchangeClientEngine.Sub Sub = null!;

    /// <summary>Every snapshot received, oldest first (one at the start, one per resync).</summary>
    public List<TSnapshot> Snapshots { get; } = new();

    /// <summary>Every delta received, in order.</summary>
    public List<TDelta> Deltas { get; } = new();

    public SubscriptionState State => Sub.State;
    public SubscriptionEndReason? EndReason => Sub.EndReason;
    public string EndText => Sub.EndText;
}

/// <summary>
/// A game-free topic: Core's real server and client subscription engines, send queues and chunk frames, with the snapshot
/// function and delta types of a mod. Subscribe, publish, and read what each client received, in order.
/// </summary>
public sealed class TopicHarness<TSnapshot, TDelta> where TSnapshot : class, new() where TDelta : class, new()
{
    private readonly ExchangeLoopback _loop;
    private readonly ExchangeServerEngine.Topic _topic;
    private readonly MessageSchema _snap;
    private readonly MessageSchema _delta;
    private readonly ExchangeType _snapType;
    private readonly ExchangeType _deltaType;
    private readonly string _mod;
    private readonly string _name;

    internal TopicHarness(ExchangeLoopback loop, ExchangeServerEngine.Topic topic, string mod, string name)
    {
        _loop = loop;
        _topic = topic;
        _mod = mod;
        _name = name;
        (_snap, var sa) = MessageCompat.SchemaOf(typeof(TSnapshot));
        (_delta, var da) = MessageCompat.SchemaOf(typeof(TDelta));
        _snapType = new ExchangeType(sa.Name, sa.Version, sa.MinReaderVersion);
        _deltaType = new ExchangeType(da.Name, da.Version, da.MinReaderVersion);
    }

    /// <summary>User <paramref name="user"/> (a small number standing for a player) subscribes to <paramref name="key"/>; runs until quiet.</summary>
    public TopicClient<TSnapshot, TDelta> Subscribe(int user, string key)
    {
        if (!_loop.Clients.TryGetValue(user, out var peer)) peer = _loop.Connect(user);
        var client = new TopicClient<TSnapshot, TDelta>();
        client.Sub = peer.Engine.Subscribe(_mod, _name, key, _snapType, _deltaType,
            (payload, _) => client.Snapshots.Add((TSnapshot)_snap.Decode(payload)),
            (payload, _) => client.Deltas.Add((TDelta)_delta.Decode(payload)),
            null);
        _loop.Pump();
        return client;
    }

    /// <summary>Publish <paramref name="delta"/> to every subscriber of <paramref name="key"/> and run until quiet. Returns the subscribers it went to.</summary>
    public int Publish(string key, TDelta delta)
    {
        int n = _loop.Server.Publish(_topic, key, MessageCompat.EncodeWith(_delta, delta, ExchangeLimits.MaxBodyBytes));
        _loop.Pump();
        return n;
    }

    /// <summary>The client closes the subscription; runs until quiet.</summary>
    public void Unsubscribe(int user, TopicClient<TSnapshot, TDelta> client)
    {
        if (_loop.Clients.TryGetValue(user, out var peer)) peer.Engine.Unsubscribe(client.Sub);
        _loop.Pump();
    }

    /// <summary>Subscribers of <paramref name="key"/> on the server.</summary>
    public int SubscriberCount(string key) => _loop.Server.Subscribers(_topic, key).Count;

    /// <summary>Lines Core would log (drops, gaps, handler exceptions).</summary>
    public IReadOnlyList<string> Warnings => _loop.Warnings;
}

/// <summary>
/// Game-free checks for <c>ctx.Requests</c> and <c>ctx.Subscriptions</c> types: request and reply pairs, topics, and
/// round trips through Core's real exchange engines and chunk frames (so a reply larger than one packet is split and
/// reassembled exactly as in the game). Request, reply, snapshot and delta types are ordinary <c>[ModMessage]</c> classes:
/// record them with <see cref="MessageFixtures.SaveRelease"/> and replay them with <see cref="MessageCompat.Replay{T}"/>
/// like any other message.
/// </summary>
public static class ExchangeCompat
{
    private const string TestMod = "progenitor.testing.exchange";

    private static ExchangeType TypeOf(Type t)
    {
        var (_, attr) = MessageCompat.SchemaOf(t);
        return new ExchangeType(attr.Name, attr.Version, attr.MinReaderVersion);
    }

    /// <summary>
    /// Both are valid message types with different names (what <c>ctx.Requests.Handle</c> requires). Throws
    /// <see cref="MessageCompatException"/> otherwise.
    /// </summary>
    public static void AssertRequestPair<TRequest, TReply>() where TRequest : class where TReply : class
    {
        var req = TypeOf(typeof(TRequest));
        var rep = TypeOf(typeof(TReply));
        if (string.Equals(req.Name, rep.Name, StringComparison.Ordinal))
            throw new MessageCompatException(typeof(TRequest).Name + " and " + typeof(TReply).Name + " share the message name '" + req.Name + "'; a request and its reply need different names.");
    }

    /// <summary>The topic name, the snapshot and the delta are valid and distinct (what <c>ctx.Subscriptions.Topic</c> requires).</summary>
    public static void AssertTopic<TSnapshot, TDelta>(string topic) where TSnapshot : class where TDelta : class
    {
        if (!ModMessageEnvelope.IsValidName(topic)) throw new MessageCompatException("'" + topic + "' is not a valid topic name (1 to 64 characters of [a-z0-9._-]).");
        var snap = TypeOf(typeof(TSnapshot));
        var delta = TypeOf(typeof(TDelta));
        if (snap.Name == delta.Name || snap.Name == topic || delta.Name == topic)
            throw new MessageCompatException("topic '" + topic + "', snapshot '" + snap.Name + "' and delta '" + delta.Name + "' need three different names.");
    }

    /// <summary>The encoded payload of a request, reply, snapshot or delta (up to <see cref="ExchangeLimits.MaxBodyBytes"/>).</summary>
    public static byte[] Encode<T>(T message) where T : class
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        return MessageCompat.EncodeWith(MessageCompat.SchemaOf(message.GetType()).Schema, message, ExchangeLimits.MaxBodyBytes);
    }

    /// <summary>How many packets <paramref name="message"/> takes as a request or reply (one per 15 KiB, roughly).</summary>
    public static int PacketsFor(object message)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        var (schema, attr) = MessageCompat.SchemaOf(message.GetType());
        byte[] payload = MessageCompat.EncodeWith(schema, message, ExchangeLimits.MaxBodyBytes);
        return ExchangeChunks.CountFor(ExchangeBody.HeaderBytes + payload.Length);
    }

    /// <summary>
    /// Send <paramref name="request"/> through Core's client engine, chunk frames and reassembly to a server engine that runs
    /// <paramref name="handler"/> (throw <see cref="RequestRefusedException"/> to refuse), and back. Returns what the
    /// client got. The handler sees a fresh request decoded from the wire, so a field the codec cannot carry shows here.
    /// </summary>
    public static ExchangeCall<TReply> Call<TRequest, TReply>(TRequest request, Func<TRequest, MessageInfo, TReply> handler, RequestOptions? options = null)
        where TRequest : class, new() where TReply : class, new()
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        AssertRequestPair<TRequest, TReply>();
        var (reqSchema, _) = MessageCompat.SchemaOf(typeof(TRequest));
        var (repSchema, _) = MessageCompat.SchemaOf(typeof(TReply));
        var req = TypeOf(typeof(TRequest));
        var rep = TypeOf(typeof(TReply));
        var loop = new ExchangeLoopback();
        string? refusal = loop.Server.RegisterHandler(TestMod, req, rep, options ?? new RequestOptions(), false, (user, payload, info, responder) =>
        {
            var decoded = (TRequest)reqSchema.Decode(payload);
            var reply = handler(decoded, info) ?? throw new InvalidOperationException("the handler returned null");
            responder.Reply(rep, repSchema.Encode(reply));
        }, out _);
        if (refusal != null) throw new MessageCompatException(refusal);
        var peer = loop.Connect(1);
        byte[] payloadUp = MessageCompat.EncodeWith(reqSchema, request, ExchangeLimits.MaxRequestBytes);
        ExchangeOutcome? outcome = null;
        peer.Engine.Request(TestMod, req, payloadUp, rep, ExchangeLimits.DefaultTimeoutSeconds, o => outcome = o);
        loop.Pump();
        if (outcome == null) loop.Advance(ExchangeLimits.DefaultTimeoutSeconds + 1);
        var o = outcome ?? new ExchangeOutcome { Status = RequestStatus.Timeout, Reason = "no answer" };
        TReply? decodedReply = null;
        var status = o.Status;
        string reason = o.Reason;
        if (status == RequestStatus.Ok)
        {
            try { decodedReply = (TReply)repSchema.Decode(o.Payload); }
            catch (FormatException e)
            {
                status = RequestStatus.Malformed;
                reason = e.Message;
            }
        }

        foreach (var w in loop.Warnings)
            if (w.Contains("handler threw", StringComparison.Ordinal)) reason = reason.Length > 0 ? reason + "; " + w : w;
        return new ExchangeCall<TReply>(status, decodedReply, reason, payloadUp.Length, o.Payload.Length, peer.PacketsUp, peer.PacketsDown);
    }

    /// <summary>
    /// A game-free topic named <paramref name="name"/> whose snapshot function is <paramref name="snapshot"/> (user number,
    /// key). Subscribe clients and publish deltas on the returned harness.
    /// </summary>
    public static TopicHarness<TSnapshot, TDelta> Topic<TSnapshot, TDelta>(string name, Func<int, string, TSnapshot> snapshot, TopicOptions? options = null)
        where TSnapshot : class, new() where TDelta : class, new()
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        AssertTopic<TSnapshot, TDelta>(name);
        var (snapSchema, _) = MessageCompat.SchemaOf(typeof(TSnapshot));
        var loop = new ExchangeLoopback();
        string? refusal = loop.Server.RegisterTopic(TestMod, name, TypeOf(typeof(TSnapshot)), TypeOf(typeof(TDelta)), options ?? new TopicOptions(),
            (user, key) => MessageCompat.EncodeWith(snapSchema, snapshot(user, key) ?? throw new InvalidOperationException("the snapshot returned null"), ExchangeLimits.MaxBodyBytes),
            out var topic);
        if (refusal != null) throw new MessageCompatException(refusal);
        return new TopicHarness<TSnapshot, TDelta>(loop, topic!, TestMod, name);
    }
}
