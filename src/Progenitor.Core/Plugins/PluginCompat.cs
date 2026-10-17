using System;
using System.Collections.Generic;
using System.Reflection;
using Progenitor.Networking;
using Unity.Entities;

namespace Progenitor.Plugins;

/// <summary>
/// <c>ctx.Features</c> (mod compatibility C1): what this mod can do, as the other side sees it through
/// <c>ctx.Peer</c> / <c>ctx.Peers</c>. Names are scoped to this mod's GUID. Every offer is tracked: unload withdraws it,
/// and the next <c>OnLoad</c> offers it again.
/// </summary>
public sealed class PluginFeatures
{
    private readonly PluginContext _ctx;

    internal PluginFeatures(PluginContext ctx) => _ctx = ctx;

    /// <summary>
    /// Offer <paramref name="name"/> at <paramref name="level"/> (default 1), or raise it: the level peers see is the
    /// highest of this mod's live offers of that name. Dispose the handle (or unload) to withdraw it. Throws
    /// <see cref="ArgumentException"/> for a name outside <c>[a-z0-9][a-z0-9._-]*</c> or over 48 characters, a level
    /// outside 1 to 65535, or a 33rd distinct feature.
    /// </summary>
    public IDisposable Offer(string name, int level = 1)
    {
        var handle = ModCompatNet.Offers.Add(_ctx.Guid, name, level);
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>This mod's current features: name to level.</summary>
    public IReadOnlyDictionary<string, int> Offered => ModCompatNet.Offers.Of(_ctx.Guid);
}

/// <summary>
/// <c>ctx.Peer</c> (mod compatibility C1), client: the joined server's features and mod versions. On the server this
/// view always answers <see cref="Known"/> = false (use <c>ctx.Peers.Of(user)</c> there).
/// </summary>
public sealed class PluginPeer : IPeerView
{
    private readonly PluginContext _ctx;
    private readonly IPeerView _view;

    internal PluginPeer(PluginContext ctx)
    {
        _ctx = ctx;
        _view = ctx.IsClient
            ? new PeerTableView(ctx.Guid, () => ModCompatNet.PeerTables.Get(ModCompatNet.ServerPeer), ModCompatNet.ServerRowVersion)
            : PeerTableView.Unknown;
    }

    public bool Known => _view.Known;

    public bool Has(string feature, int minLevel = 1) => _view.Has(feature, minLevel);

    public bool Has(string modGuid, string feature, int minLevel = 1) => _view.Has(modGuid, feature, minLevel);

    public string? ModVersion(string modGuid) => _view.ModVersion(modGuid);

    public string? CoreVersion => _view.CoreVersion;

    /// <summary>
    /// Client: the server's table arrived, changed (the server hot-reloaded a mod) or went away (leave). Main thread.
    /// The subscription is tracked: unload removes it. Never raised on the server.
    /// </summary>
    public event Action Changed
    {
        add
        {
            if (value == null) return;
            ModCompatNet.ClientChanged += value;
            _ctx.Track(new ActionDisposable(() => ModCompatNet.ClientChanged -= value));
        }
        remove
        {
            if (value != null) ModCompatNet.ClientChanged -= value;
        }
    }
}

/// <summary>
/// <c>ctx.Peers</c> (mod compatibility C1), server: one view per connected user. On a client every view answers
/// <see cref="IPeerView.Known"/> = false.
/// </summary>
public sealed class PluginPeers
{
    private readonly PluginContext _ctx;

    internal PluginPeers(PluginContext ctx) => _ctx = ctx;

    /// <summary>The view of <paramref name="userEntity"/>'s client. Read it when you need it; it follows the user's table.</summary>
    public IPeerView Of(Entity userEntity)
    {
        if (!_ctx.IsServer) return PeerTableView.Unknown;
        int index = ModCompatNet.UserIndexOf(userEntity);
        if (index < 0) return PeerTableView.Unknown;
        // NOKICK1: a mod this player was kept in place without (or at a version this server does not accept) reads as absent.
        return new PeerTableView(_ctx.Guid, () => ModCompatNet.PeerTables.Get(index), masked: g => HandshakeInPlaceMask.Shared.IsMasked(index, g));
    }

    /// <summary>
    /// Server: that user's table arrived or changed, or it left. Main thread. Tracked: unload removes it. Never raised on a
    /// client.
    /// </summary>
    public event Action<Entity> Changed
    {
        add
        {
            if (value == null) return;
            ModCompatNet.ServerChanged += value;
            _ctx.Track(new ActionDisposable(() => ModCompatNet.ServerChanged -= value));
        }
        remove
        {
            if (value != null) ModCompatNet.ServerChanged -= value;
        }
    }
}

/// <summary>
/// <c>ctx.Messages</c> (mod compatibility C2): versioned, tolerant messages on Core's <c>'PrMs'</c> envelope. A message
/// is a class with <see cref="ModMessageAttribute"/> and numbered <see cref="FieldAttribute"/> members; unknown fields
/// are skipped and missing fields keep their initializer, so two versions of a mod can talk. Sends go only to a peer
/// whose Progenitor reads the envelope and whose feature table lists this mod; the result says why anything else was
/// not sent. Handlers run on the main thread and are tracked: unload removes them.
/// </summary>
public sealed class PluginMessages
{
    private readonly PluginContext _ctx;
    private readonly Dictionary<Type, MessageSchema> _schemas = new();

    internal PluginMessages(PluginContext ctx)
    {
        _ctx = ctx;
        // Unload forgets this mod's names and handlers in Core, so a message for it is dropped there afterwards.
        string guid = ctx.Guid;
        _ctx.Track(new ActionDisposable(() => ModCompatNet.Router.ForgetMod(guid)));
    }

    private MessageSchema Schema(Type type)
    {
        lock (_schemas)
        {
            if (_schemas.TryGetValue(type, out var s)) return s;
            s = MessageSchema.For(type);
            string guid = _ctx.Guid;
            s.UnknownEnum = what =>
            {
                string field = what.Split(new[] { " = " }, StringSplitOptions.None)[0];
                var line = ModCompatNet.Router.Once("enum|" + guid + "|" + field, "Messages: " + guid + " received an unknown enum value " + what + "; using the field's default");
                if (line != null) Plugin.Logger?.LogWarning(line);
            };
            _schemas[type] = s;
            return s;
        }
    }

    internal static ModMessageAttribute Attr(MessageSchema schema)
    {
        var attr = schema.Message ?? throw new InvalidOperationException(schema.Type.FullName + " has no [ModMessage] attribute.");
        if (!ModMessageEnvelope.IsValidName(attr.Name))
            throw new InvalidOperationException(schema.Type.FullName + ": '" + attr.Name + "' is not a valid message name (1 to 64 characters of [a-z0-9._-]).");
        if (!ModMessageEnvelope.IsValidVersion(attr.Version) || attr.MinReaderVersion < 1 || attr.MinReaderVersion > attr.Version)
            throw new InvalidOperationException(schema.Type.FullName + ": Version " + attr.Version + " / MinReaderVersion " + attr.MinReaderVersion
                                                + " is not valid (1 <= MinReaderVersion <= Version <= 65535).");
        return attr;
    }


    /// <summary>
    /// Handle <typeparamref name="T"/>. <c>from</c> is the sending user's entity on the server and <see cref="Entity.Null"/>
    /// on the client. Throws <see cref="InvalidOperationException"/> when <typeparamref name="T"/> is not a valid message
    /// type, and when the registration is refused: a second handler for the same message, a name or GUID whose hash
    /// collides with one already registered, or a bad name or version (the same contract as <c>ctx.Features.Offer</c>).
    /// </summary>
    public IDisposable On<T>(Action<Entity, T, MessageInfo> handler) where T : class, new()
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var schema = Schema(typeof(T));
        var attr = Attr(schema);
        ModMessageDispatch dispatch = (from, payload, info) => handler(from is Entity e ? e : Entity.Null, (T)schema.Decode(payload), info);
        return Register(attr.Name, attr.Version, dispatch);
    }

    /// <summary>
    /// Handle <paramref name="name"/> with the hand-written reader (hot paths). <paramref name="readerVersion"/> is the
    /// newest version this handler reads; a message whose minimum reader version is above it is dropped. Throws
    /// <see cref="InvalidOperationException"/> when refused, as <see cref="On{T}"/> does.
    /// </summary>
    public IDisposable On(string name, Action<Entity, MessageReader, MessageInfo> handler, int readerVersion = 1)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        ModMessageDispatch dispatch = (from, payload, info) => handler(from is Entity e ? e : Entity.Null, new MessageReader(payload), info);
        return Register(name, readerVersion, dispatch);
    }

    private IDisposable Register(string name, int readerVersion, ModMessageDispatch dispatch)
    {
        string guid = _ctx.Guid;
        string? refusal = ModCompatNet.Router.Register(guid, name, readerVersion, dispatch, out object token);
        // Refused: thrown, like Offer, so the author sees it at OnLoad instead of a handler that silently never runs.
        if (refusal != null) throw new InvalidOperationException(refusal);

        var handle = new ActionDisposable(() => ModCompatNet.Router.Unregister(guid, name, token));
        _ctx.Track(handle);
        return handle;
    }

    /// <summary>
    /// Sends touch the game's network systems and Core's peer tables, so they run on the game's main thread only (the
    /// thread <c>OnLoad</c>, event handlers, message handlers and <c>MainThread.Post</c> callbacks run on). From any
    /// other thread they throw <see cref="InvalidOperationException"/>; post the send with <c>MainThread.Post</c>.
    /// </summary>
    private static void RequireMainThread()
    {
        if (!Progenitor.Dev.MainThread.IsMainThread)
            throw new InvalidOperationException("ctx.Messages sends run on the game's main thread only; post the send with MainThread.Post.");
    }

    private SendResult Encode<T>(T message, out ModMessageAttribute? attr, out byte[] payload) where T : class
    {
        RequireMainThread();
        attr = null;
        payload = Array.Empty<byte>();
        if (message == null) return SendResult.Invalid;
        try
        {
            var schema = Schema(message.GetType());
            attr = Attr(schema);
            payload = schema.Encode(message);
            return payload.Length > ModMessageEnvelope.MaxPayloadBytes ? SendResult.TooLarge : SendResult.Sent;
        }
        catch (MessageTooLargeException) { return SendResult.TooLarge; }
        catch (InvalidOperationException e)
        {
            var line = ModCompatNet.Router.Once("schema|" + _ctx.Guid + "|" + message.GetType().FullName, "Messages: " + _ctx.Guid + " cannot send " + message.GetType().Name + ": " + e.Message);
            if (line != null) Plugin.Logger?.LogError(line);
            return SendResult.Invalid;
        }
    }

    /// <summary>The message's field schema for a recording (only while the Dev bridge records); null otherwise.</summary>
    private IReadOnlyList<FieldShape>? ShapeOf(object message)
    {
        if (!ModMessageRecordBridge.Recording) return null;
        try { return Schema(message.GetType()).Shape(); }
        catch { return null; }
    }

    /// <summary>Client, main thread: send to the joined server. On the server this returns <see cref="SendResult.NotConnected"/>.</summary>
    public SendResult SendToServer<T>(T message) where T : class
    {
        var r = Encode(message, out var attr, out var payload);
        return r != SendResult.Sent ? r : ModCompatNet.SendToServer(_ctx.Guid, attr!.Name, attr.Version, attr.MinReaderVersion, payload, ShapeOf(message));
    }

    /// <summary>Server, main thread: send to one user. On a client this returns <see cref="SendResult.NotConnected"/>.</summary>
    public SendResult SendTo<T>(Entity userEntity, T message) where T : class
    {
        var r = Encode(message, out var attr, out var payload);
        return r != SendResult.Sent ? r : ModCompatNet.SendToUser(_ctx.Guid, attr!.Name, attr.Version, attr.MinReaderVersion, payload, userEntity, ShapeOf(message));
    }

    /// <summary>Server, main thread: send to every connected user whose client runs this mod. Returns how many sends went out.</summary>
    public int SendToAll<T>(T message) where T : class
    {
        var r = Encode(message, out var attr, out var payload);
        return r != SendResult.Sent ? 0 : ModCompatNet.SendToAll(_ctx.Guid, attr!.Name, attr.Version, attr.MinReaderVersion, payload, ShapeOf(message));
    }

    /// <summary>Client, main thread: send a hand-written message to the joined server.</summary>
    public SendResult SendToServer(string name, MessageWriter body, int version = 1, int minReaderVersion = 1)
    {
        RequireMainThread();
        if (body == null) return SendResult.Invalid;
        return ModCompatNet.SendToServer(_ctx.Guid, name, version, minReaderVersion, body.ToArray());
    }

    /// <summary>Server, main thread: send a hand-written message to one user.</summary>
    public SendResult SendTo(Entity userEntity, string name, MessageWriter body, int version = 1, int minReaderVersion = 1)
    {
        RequireMainThread();
        if (body == null) return SendResult.Invalid;
        return ModCompatNet.SendToUser(_ctx.Guid, name, version, minReaderVersion, body.ToArray(), userEntity);
    }

    /// <summary>Server, main thread: send a hand-written message to every connected user whose client runs this mod.</summary>
    public int SendToAll(string name, MessageWriter body, int version = 1, int minReaderVersion = 1)
    {
        RequireMainThread();
        if (body == null) return 0;
        return ModCompatNet.SendToAll(_ctx.Guid, name, version, minReaderVersion, body.ToArray());
    }
}
