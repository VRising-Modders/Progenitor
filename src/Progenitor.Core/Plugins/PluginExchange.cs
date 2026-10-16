using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Progenitor.Networking;
using Unity.Entities;

namespace Progenitor.Plugins;

/// <summary>Message-type plumbing shared by <see cref="PluginRequests"/> and <see cref="PluginSubscriptions"/>.</summary>
internal sealed class ExchangeTypes
{
    private readonly string _guid;
    private readonly Dictionary<Type, (MessageSchema Schema, ExchangeType Type)> _cache = new();

    internal ExchangeTypes(string guid) => _guid = guid;

    internal (MessageSchema Schema, ExchangeType Type) Of(Type type)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(type, out var hit)) return hit;
            var schema = MessageSchema.For(type);
            var attr = PluginMessages.Attr(schema);
            string guid = _guid;
            schema.UnknownEnum = what =>
            {
                string field = what.Split(new[] { " = " }, StringSplitOptions.None)[0];
                var line = ModCompatNet.Router.Once("enum|" + guid + "|" + field, "Messages: " + guid + " received an unknown enum value " + what + "; using the field's default");
                if (line != null) Plugin.Logger?.LogWarning(line);
            };
            hit = (schema, new ExchangeType(attr.Name, attr.Version, attr.MinReaderVersion));
            _cache[type] = hit;
            return hit;
        }
    }

    internal static void RequireMainThread(string what)
    {
        if (!Progenitor.Dev.MainThread.IsMainThread)
            throw new InvalidOperationException(what + " runs on the game's main thread only; post it with MainThread.Post.");
    }

    /// <summary>Tracked once per facade: unload ends this mod's requests and subscriptions on both sides and forgets its names.</summary>
    internal static void TrackUnload(PluginContext ctx)
    {
        string guid = ctx.Guid;
        ctx.Track(new ActionDisposable(() =>
        {
            try
            {
                ExchangeNet.Client.ForgetMod(guid);
                ExchangeNet.Server.ForgetMod(guid);
            }
            finally { ModCompatNet.Router.ForgetMod(guid); }
        }));
    }
}

/// <summary>How one request ended. <see cref="Reply"/> is set only when <see cref="Status"/> is <see cref="RequestStatus.Ok"/>.</summary>
public sealed class RequestResult<TReply> where TReply : class
{
    internal RequestResult(RequestStatus status, TReply? reply, string reason, SendResult notSent, MessageInfo? info, bool unloading = false)
    {
        Unloading = unloading;
        Status = status;
        Reply = reply;
        Reason = reason ?? "";
        NotSent = notSent;
        Info = info;
    }

    public RequestStatus Status { get; }

    /// <summary>
    /// True when the request ended because this mod is unloading (<see cref="RequestStatus.Cancelled"/>, or
    /// <see cref="RequestStatus.NotSent"/> for a send made during unload). The callback still runs so an awaited
    /// task completes; there is nothing to show the player, and the mod should not touch its UI.
    /// </summary>
    public bool Unloading { get; }

    /// <summary>True when the server answered with a reply this mod could read.</summary>
    public bool Ok => Status == RequestStatus.Ok;

    public TReply? Reply { get; }

    /// <summary>Why it did not succeed: the server's refusal reason, or a description of the local failure. Empty on success.</summary>
    public string Reason { get; }

    /// <summary>With <see cref="RequestStatus.NotSent"/>: why nothing went out. Otherwise <see cref="SendResult.Sent"/>.</summary>
    public SendResult NotSent { get; }

    /// <summary>On success: the reply's name, the version the server wrote and its mod version.</summary>
    public MessageInfo? Info { get; }

    public override string ToString() => Ok ? "Ok" : Status + (Reason.Length > 0 ? " (" + Reason + ")" : "");
}

/// <summary>A request on its way. <see cref="Cancel"/> (or <see cref="Dispose"/>) ends it with <see cref="RequestStatus.Cancelled"/>.</summary>
public sealed class RequestHandle : IDisposable
{
    private readonly ExchangeClientEngine.Pending _pending;

    internal RequestHandle(ExchangeClientEngine.Pending pending) => _pending = pending;

    /// <summary>The callback has run.</summary>
    public bool IsDone => _pending.Finished;

    /// <summary>End the request now with <see cref="RequestStatus.Cancelled"/> and tell the server. False when it already ended.</summary>
    public bool Cancel()
    {
        ExchangeTypes.RequireMainThread("RequestHandle.Cancel");
        return ExchangeNet.Client.Cancel(_pending);
    }

    public void Dispose() => Cancel();
}

/// <summary>Answers one request of a deferred handler (<c>ctx.Requests.HandleDeferred</c>). Main thread. The first answer counts.</summary>
public sealed class RequestResponder<TReply> where TReply : class
{
    private readonly ExchangeResponder _inner;
    private readonly MessageSchema _schema;
    private readonly ExchangeType _type;

    internal RequestResponder(ExchangeResponder inner, MessageSchema schema, ExchangeType type, Entity user)
    {
        _inner = inner;
        _schema = schema;
        _type = type;
        User = user;
    }

    /// <summary>The user entity that sent the request.</summary>
    public Entity User { get; }

    /// <summary>False once answered, refused, timed out, cancelled by the client, or the user left.</summary>
    public bool IsOpen => !_inner.Done;

    /// <summary>Send the reply. False when no longer open, or when it could not be queued (the client gets an error).</summary>
    public bool Reply(TReply reply)
    {
        ExchangeTypes.RequireMainThread("RequestResponder.Reply");
        if (reply == null) throw new ArgumentNullException(nameof(reply));
        if (_inner.Done) return false;
        byte[] payload;
        try { payload = _schema.Encode(reply); }
        catch (MessageTooLargeException e)
        {
            _inner.Fail(ExchangeCode.TooLarge, "the reply has a string over 4 KiB");
            Plugin.Logger?.LogError("Requests: a reply could not be encoded: " + e.Message);
            return false;
        }

        return _inner.Reply(_type, payload);
    }

    /// <summary>Refuse: the client gets <see cref="RequestStatus.Refused"/> with <paramref name="reason"/>. False when no longer open.</summary>
    public bool Refuse(string reason)
    {
        ExchangeTypes.RequireMainThread("RequestResponder.Refuse");
        return _inner.Fail(ExchangeCode.Refused, string.IsNullOrWhiteSpace(reason) ? "refused" : reason);
    }
}

/// <summary>
/// <c>ctx.Requests</c>: typed request and reply on top of versioned messages (mod compatibility C2). The client sends a
/// <c>[ModMessage]</c> request; the server's handler answers with a <c>[ModMessage]</c> reply, or refuses with a reason.
/// Requests and replies may be larger than one packet (a request up to its handler's <see cref="RequestOptions.MaxRequestBytes"/>,
/// a reply up to <see cref="ExchangeLimits.MaxBodyBytes"/>): Core splits and reassembles them. Correlation, timeouts,
/// cancellation on disconnect and unload, and the server's per-user size, rate and concurrency limits are Core's. Sends
/// are gated like <c>ctx.Messages</c>: only to a peer whose Progenitor reads the exchange and whose feature table lists
/// this mod. Handlers and callbacks run on the main thread; handlers are tracked and unload removes them.
/// </summary>
public sealed class PluginRequests
{
    private readonly PluginContext _ctx;
    private readonly ExchangeTypes _types;

    internal PluginRequests(PluginContext ctx)
    {
        _ctx = ctx;
        _types = new ExchangeTypes(ctx.Guid);
        ExchangeTypes.TrackUnload(ctx);
    }

    /// <summary>
    /// Server: answer <typeparamref name="TRequest"/> with the <typeparamref name="TReply"/> <paramref name="handler"/>
    /// returns. Throw <see cref="RequestRefusedException"/> to refuse with a reason; any other exception is logged and the
    /// client gets <see cref="RequestStatus.Failed"/>. <c>from</c> is the sending user's entity. Throws
    /// <see cref="InvalidOperationException"/> on a client, for an invalid message type or option, for a request and reply
    /// with the same name, for a second handler of the same request, and for a name whose hash collides (the
    /// <c>ctx.Messages.On</c> contract).
    /// </summary>
    public IDisposable Handle<TRequest, TReply>(Func<Entity, TRequest, MessageInfo, TReply> handler, RequestOptions? options = null)
        where TRequest : class, new() where TReply : class
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var (reqSchema, req) = _types.Of(typeof(TRequest));
        var (repSchema, rep) = _types.Of(typeof(TReply));
        ExchangeServerEngine.HandlerRun run = (user, payload, info, responder) =>
        {
            var request = (TRequest)reqSchema.Decode(payload);
            var reply = handler(ExchangeNet.EntityOf(user), request, info)
                        ?? throw new InvalidOperationException("the handler of " + req.Name + " returned null");
            responder.Reply(rep, repSchema.Encode(reply));
        };
        return Register(req, rep, options, deferred: false, run);
    }

    /// <summary>
    /// Server: answer <typeparamref name="TRequest"/> later through the <see cref="RequestResponder{TReply}"/> (for data that
    /// is not ready this frame). A request not answered within <see cref="RequestOptions.TimeoutSeconds"/> is answered
    /// <see cref="RequestStatus.Timeout"/>; at most <see cref="RequestOptions.MaxPending"/> per user wait at once. The
    /// handler may also answer at once, or throw as in <see cref="Handle{TRequest, TReply}"/>.
    /// </summary>
    public IDisposable HandleDeferred<TRequest, TReply>(Action<Entity, TRequest, MessageInfo, RequestResponder<TReply>> handler, RequestOptions? options = null)
        where TRequest : class, new() where TReply : class
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var (reqSchema, req) = _types.Of(typeof(TRequest));
        var (repSchema, rep) = _types.Of(typeof(TReply));
        ExchangeServerEngine.HandlerRun run = (user, payload, info, responder) =>
        {
            var request = (TRequest)reqSchema.Decode(payload);
            var entity = ExchangeNet.EntityOf(user);
            handler(entity, request, info, new RequestResponder<TReply>(responder, repSchema, rep, entity));
        };
        return Register(req, rep, options, deferred: true, run);
    }

    private IDisposable Register(ExchangeType req, ExchangeType rep, RequestOptions? options, bool deferred, ExchangeServerEngine.HandlerRun run)
    {
        if (!_ctx.IsServer) throw new InvalidOperationException("ctx.Requests.Handle runs on the server; a client sends with ctx.Requests.Send.");
        ExchangeTypes.RequireMainThread("ctx.Requests.Handle");
        string guid = _ctx.Guid;
        string? refusal = ExchangeNet.Server.RegisterHandler(guid, req, rep, options ?? new RequestOptions(), deferred, run, out object token);
        if (refusal != null) throw new InvalidOperationException(refusal);
        var handle = new ActionDisposable(() => ExchangeNet.Server.UnregisterHandler(guid, req, token));
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>
    /// Client, main thread: send <paramref name="request"/> to the joined server. <paramref name="done"/> runs exactly once
    /// on the main thread: with the reply, the server's refusal, or why it failed (also synchronously, before this returns,
    /// when nothing could be sent). <paramref name="timeoutSeconds"/> is clamped to <see cref="ExchangeLimits.MaxTimeoutSeconds"/>.
    /// </summary>
    public RequestHandle Send<TRequest, TReply>(TRequest request, Action<RequestResult<TReply>> done, double timeoutSeconds = ExchangeLimits.DefaultTimeoutSeconds)
        where TRequest : class where TReply : class, new()
    {
        ExchangeTypes.RequireMainThread("ctx.Requests.Send");
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (done == null) throw new ArgumentNullException(nameof(done));
        var (reqSchema, req) = _types.Of(request.GetType());
        var (repSchema, rep) = _types.Of(typeof(TReply));
        string guid = _ctx.Guid;
        Action<ExchangeOutcome> complete = o =>
        {
            if (o.Status != RequestStatus.Ok)
            {
                done(new RequestResult<TReply>(o.Status, null, o.Reason, o.NotSent, null, o.Unloading));
                return;
            }

            TReply reply;
            try { reply = (TReply)repSchema.Decode(o.Payload); }
            catch (FormatException e)
            {
                done(new RequestResult<TReply>(RequestStatus.Malformed, null, "the reply could not be decoded: " + e.Message, SendResult.Sent, null));
                return;
            }

            var info = new MessageInfo
            {
                Name = rep.Name, SenderVersion = o.Version, FromServer = true, UserIndex = -1,
                SenderModVersion = ModCompatNet.PeerModVersion(ModCompatNet.ServerPeer, guid),
            };
            done(new RequestResult<TReply>(RequestStatus.Ok, reply, "", SendResult.Sent, info));
        };

        if (!_ctx.IsClient)
        {
            var ended = new ExchangeClientEngine.Pending { Finished = true };
            complete(new ExchangeOutcome { Status = RequestStatus.NotSent, Reason = "ctx.Requests.Send runs on a client", NotSent = SendResult.NotConnected });
            return new RequestHandle(ended);
        }

        // PINS1: unload already ran ForgetMod for this mod. A request queued now would keep the
        // callback (a plugin closure) in the client engine after the plugin's context unloads.
        if (_ctx.IsRetired)
        {
            var ended = new ExchangeClientEngine.Pending { Finished = true };
            complete(new ExchangeOutcome { Status = RequestStatus.NotSent, Reason = "this mod is unloading", NotSent = SendResult.Invalid, Unloading = true });
            return new RequestHandle(ended);
        }

        byte[] payload;
        try { payload = reqSchema.Encode(request); }
        catch (MessageTooLargeException e)
        {
            var ended = new ExchangeClientEngine.Pending { Finished = true };
            complete(new ExchangeOutcome { Status = RequestStatus.TooLarge, Reason = e.Message });
            return new RequestHandle(ended);
        }

        return new RequestHandle(ExchangeNet.Client.Request(guid, req, payload, rep, timeoutSeconds, complete));
    }

    /// <summary>
    /// Client, main thread: <see cref="Send{TRequest, TReply}"/> as a task. The task completes on the main thread (inside
    /// the receive or the frame tick); it never faults: failures are a <see cref="RequestResult{TReply}"/> status.
    /// </summary>
    public Task<RequestResult<TReply>> SendAsync<TRequest, TReply>(TRequest request, double timeoutSeconds = ExchangeLimits.DefaultTimeoutSeconds)
        where TRequest : class where TReply : class, new()
    {
        var tcs = new TaskCompletionSource<RequestResult<TReply>>();
        Send<TRequest, TReply>(request, r => tcs.TrySetResult(r), timeoutSeconds);
        return tcs.Task;
    }
}

/// <summary>
/// A server topic (<c>ctx.Subscriptions.Topic</c>): clients subscribe to a key, get a snapshot from the topic's snapshot
/// function, then every delta <see cref="Publish"/> sends for that key, in order, until they unsubscribe or leave. Main
/// thread. Dispose (or unload) ends every subscription.
/// </summary>
public sealed class SubscriptionTopic<TSnapshot, TDelta> : IDisposable where TSnapshot : class where TDelta : class
{
    private readonly ExchangeServerEngine.Topic _topic;
    private readonly MessageSchema _delta;

    internal SubscriptionTopic(ExchangeServerEngine.Topic topic, MessageSchema delta)
    {
        _topic = topic;
        _delta = delta;
        topic.Opened = (user, key) => Opened?.Invoke(ExchangeNet.EntityOf(user), key);
        topic.Closed = (user, key) => Closed?.Invoke(ExchangeNet.EntityOf(user), key);
    }

    public string Name => _topic.Name;

    /// <summary>A user's subscription to a key opened (after its snapshot was queued). Main thread.</summary>
    public event Action<Entity, string>? Opened;

    /// <summary>A user's subscription closed: unsubscribed, left, or reset. Not raised when the topic itself is disposed. Main thread.</summary>
    public event Action<Entity, string>? Closed;

    /// <summary>
    /// Send <paramref name="delta"/> to every subscriber of <paramref name="key"/>. Encoded once; each subscriber gets the
    /// next number of its own sequence. A subscriber whose send queue is full gets a fresh snapshot once there is room
    /// instead. Returns the subscribers it was queued for.
    /// </summary>
    public int Publish(string key, TDelta delta)
    {
        ExchangeTypes.RequireMainThread("SubscriptionTopic.Publish");
        if (delta == null) throw new ArgumentNullException(nameof(delta));
        if (_topic.Disposed || !_topic.ByKey.ContainsKey(key ?? "")) return 0;
        return ExchangeNet.Server.Publish(_topic, key ?? "", _delta.Encode(delta));
    }

    /// <summary>Send every subscriber of <paramref name="key"/> a fresh snapshot (at most one per second each). Returns how many.</summary>
    public int Resync(string key)
    {
        ExchangeTypes.RequireMainThread("SubscriptionTopic.Resync");
        return ExchangeNet.Server.Resync(_topic, key ?? "");
    }

    /// <summary>The keys with at least one subscriber, sorted.</summary>
    public IReadOnlyList<string> Keys => ExchangeNet.Server.Keys(_topic);

    /// <summary>The user entities subscribed to <paramref name="key"/>.</summary>
    public IReadOnlyList<Entity> Subscribers(string key)
    {
        var list = new List<Entity>();
        foreach (int user in ExchangeNet.Server.Subscribers(_topic, key ?? "")) list.Add(ExchangeNet.EntityOf(user));
        return list;
    }

    public void Dispose()
    {
        if (_topic.Disposed) return;
        ExchangeNet.Server.UnregisterTopic(_topic);
    }
}

/// <summary>
/// A client subscription (<c>ctx.Subscriptions.Subscribe</c>). The snapshot callback runs first (and again after a resync);
/// deltas follow in the server's order. Dispose to unsubscribe (no end callback runs then). Unload disposes it.
/// </summary>
public sealed class Subscription<TSnapshot, TDelta> : IDisposable where TSnapshot : class where TDelta : class
{
    private readonly ExchangeClientEngine.Sub _sub;

    internal Subscription(ExchangeClientEngine.Sub sub) => _sub = sub;

    public string Topic => _sub.Topic;
    public string Key => _sub.Key;
    public SubscriptionState State => _sub.State;

    /// <summary>Deltas applied since the first snapshot.</summary>
    public long Deltas => _sub.Deltas;

    /// <summary>Why it ended, once <see cref="State"/> is <see cref="SubscriptionState.Ended"/>.</summary>
    public SubscriptionEndReason? EndReason => _sub.EndReason;

    /// <summary>The server's reason or a description of the local end; empty while open.</summary>
    public string EndText => _sub.EndText;

    public void Dispose()
    {
        ExchangeTypes.RequireMainThread("Subscription.Dispose");
        ExchangeNet.Client.Unsubscribe(_sub);
    }
}

/// <summary>
/// <c>ctx.Subscriptions</c>: snapshot then deltas for an open panel. The server declares a topic of keyed state (a
/// container, a board); a client subscribes to one key and gets a snapshot, then deltas in order until it unsubscribes or
/// leaves; a missing delta asks for a fresh snapshot. Snapshots and deltas are <c>[ModMessage]</c> types (version rules as
/// in <c>ctx.Messages</c>) and may be larger than one packet. Main thread; tracked.
/// </summary>
public sealed class PluginSubscriptions
{
    private readonly PluginContext _ctx;
    private readonly ExchangeTypes _types;

    internal PluginSubscriptions(PluginContext ctx)
    {
        _ctx = ctx;
        _types = new ExchangeTypes(ctx.Guid);
        ExchangeTypes.TrackUnload(ctx);
    }

    /// <summary>
    /// Server: declare topic <paramref name="name"/>. <paramref name="snapshot"/> builds the state of one key for one user;
    /// throw <see cref="RequestRefusedException"/> from it to refuse the subscription (it runs again for every resync, so
    /// access is re-checked). Throws <see cref="InvalidOperationException"/> on a client, for invalid types or options, for a
    /// name not distinct from the snapshot and delta names, for a second topic of the same name and for a hash collision.
    /// </summary>
    public SubscriptionTopic<TSnapshot, TDelta> Topic<TSnapshot, TDelta>(string name, Func<Entity, string, TSnapshot> snapshot, TopicOptions? options = null)
        where TSnapshot : class where TDelta : class
    {
        if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
        if (!_ctx.IsServer) throw new InvalidOperationException("ctx.Subscriptions.Topic runs on the server; a client subscribes with ctx.Subscriptions.Subscribe.");
        ExchangeTypes.RequireMainThread("ctx.Subscriptions.Topic");
        if (!ModMessageEnvelope.IsValidName(name))
            throw new InvalidOperationException("Subscriptions: '" + name + "' is not a valid topic name (1 to 64 characters of [a-z0-9._-]).");
        var (snapSchema, snap) = _types.Of(typeof(TSnapshot));
        var (deltaSchema, delta) = _types.Of(typeof(TDelta));
        ExchangeServerEngine.SnapshotRun open = (user, key) =>
        {
            var state = snapshot(ExchangeNet.EntityOf(user), key) ?? throw new InvalidOperationException("the snapshot of " + name + " returned null");
            return snapSchema.Encode(state);
        };
        string? refusal = ExchangeNet.Server.RegisterTopic(_ctx.Guid, name, snap, delta, options ?? new TopicOptions(), open, out var topic);
        if (refusal != null) throw new InvalidOperationException(refusal);
        var handle = new SubscriptionTopic<TSnapshot, TDelta>(topic!, deltaSchema);
        // Unload ends the topic as Unloaded, which subscribers retry (the next OnLoad declares it again); Dispose is Closed.
        var t = topic!;
        _ctx.Track(new ActionDisposable(() => ExchangeNet.Server.UnregisterTopic(t, ExchangeCode.Unloaded, "the server's mod unloaded")));
        return handle;
    }

    /// <summary>
    /// Client, main thread: subscribe to <paramref name="key"/> of the server's topic <paramref name="topic"/>.
    /// <paramref name="onSnapshot"/> gets the whole state (first, and after every resync); <paramref name="onDelta"/> each
    /// change after it, in order. <paramref name="onEnded"/> runs once when the server or the connection ends it (not when
    /// you dispose it); when nothing could be sent it runs before this returns and the subscription comes back ended.
    /// </summary>
    public Subscription<TSnapshot, TDelta> Subscribe<TSnapshot, TDelta>(string topic, string key, Action<TSnapshot, MessageInfo> onSnapshot,
        Action<TDelta, MessageInfo> onDelta, Action<SubscriptionEndReason, string>? onEnded = null)
        where TSnapshot : class, new() where TDelta : class, new()
    {
        ExchangeTypes.RequireMainThread("ctx.Subscriptions.Subscribe");
        if (onSnapshot == null) throw new ArgumentNullException(nameof(onSnapshot));
        if (onDelta == null) throw new ArgumentNullException(nameof(onDelta));
        if (!ModMessageEnvelope.IsValidName(topic))
            throw new InvalidOperationException("Subscriptions: '" + topic + "' is not a valid topic name (1 to 64 characters of [a-z0-9._-]).");
        var (snapSchema, snap) = _types.Of(typeof(TSnapshot));
        var (deltaSchema, delta) = _types.Of(typeof(TDelta));
        string guid = _ctx.Guid;
        MessageInfo Info(ExchangeType t, int version) => new()
        {
            Name = t.Name, SenderVersion = version, FromServer = true, UserIndex = -1,
            SenderModVersion = ModCompatNet.PeerModVersion(ModCompatNet.ServerPeer, guid),
        };
        Action<byte[], int> snapRun = (payload, version) => onSnapshot((TSnapshot)snapSchema.Decode(payload), Info(snap, version));
        Action<byte[], int> deltaRun = (payload, version) => onDelta((TDelta)deltaSchema.Decode(payload), Info(delta, version));
        if (!_ctx.IsClient)
        {
            var ended = new ExchangeClientEngine.Sub { Topic = topic, Key = key ?? "", State = SubscriptionState.Ended, EndReason = SubscriptionEndReason.NotSent, EndText = "ctx.Subscriptions.Subscribe runs on a client" };
            onEnded?.Invoke(SubscriptionEndReason.NotSent, ended.EndText);
            return new Subscription<TSnapshot, TDelta>(ended);
        }

        // PINS1: after unload's ForgetMod nothing would end this subscription, and its callbacks are
        // plugin closures. Come back ended without running anything.
        if (_ctx.IsRetired)
            return new Subscription<TSnapshot, TDelta>(new ExchangeClientEngine.Sub
            {
                Topic = topic, Key = key ?? "", State = SubscriptionState.Ended,
                EndReason = SubscriptionEndReason.NotSent, EndText = "this mod is unloading",
            });

        var sub = ExchangeNet.Client.Subscribe(guid, topic, key ?? "", snap, delta, snapRun, deltaRun, onEnded);
        // Not tracked one by one (a panel opened a thousand times would grow the bag): unload ends every subscription of
        // this mod through ForgetMod.
        return new Subscription<TSnapshot, TDelta>(sub);
    }
}
