#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Globalization;
using ProjectM;
using ProjectM.Network;
using Unity.Entities;

namespace Progenitor.Networking;

/// <summary>
/// Requests, large messages and subscriptions, live half: the <c>'PrXc'</c> id on the reliable events channel, one send
/// queue and one reassembler per connection, the peer gate and the per-frame drain. The decisions are game-free
/// (<see cref="ExchangeClientEngine"/>, <see cref="ExchangeServerEngine"/>, <see cref="ExchangeAssembler"/>,
/// <see cref="ExchangeOutbox"/>); this type only sends, receives and keeps per-user bookkeeping. Main thread. The id is
/// Core's and registered through the owned-id path, so a peer without a handler drops the packet in Core.
/// </summary>
internal static unsafe class ExchangeNet
{
    internal static readonly ExchangeClientEngine Client = new();
    internal static readonly ExchangeServerEngine Server = new(ModCompatNet.Router);

    private sealed class Link
    {
        internal ExchangeOutbox Out = null!;
        internal ExchangeAssembler In = null!;
    }

    private static readonly Dictionary<int, Link> UserLinks = new();
    private static readonly Dictionary<int, Entity> UserEntities = new();
    private static Link? _serverLink;
    private static readonly ExchangeWarnLimiter Warnings = new();

    private static bool _installed;
    private static bool _server;
    private static IDisposable? _connected;
    private static IDisposable? _disconnected;

    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    internal static bool IsServer => _server;

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        _server = Plugin.IsServerProcess;
        Client.Clock = Now;
        Server.Clock = Now;
        Client.Warn = Warn;
        Server.Warn = Warn;
        try
        {
            if (_server)
            {
                Server.Gate = UserGate;
                Server.Send = (user, body) => TotalQueued() + body.Length <= ExchangeLimits.ServerQueueBytesTotal && LinkOf(user).Out.Enqueue(body);
                Server.HasRoom = HasRoom;
                Server.PeerModVersion = ModCompatNet.PeerModVersion;
                CustomEvents.OnReceiveFromClient(ExchangeChunks.EventId, OnServerPacket);
                _connected = Events.EventBus.On<Events.PlayerConnected>(ev => ForgetUser(ev.UserIndex), priority: 895);
                _disconnected = Events.EventBus.On<Events.PlayerDisconnected>(ev => ForgetUser(ev.User.Index), priority: 895);
            }
            else
            {
                Client.Gate = ServerGate;
                Client.Send = body => ServerLink().Out.Enqueue(body);
                CustomEvents.OnReceive(ExchangeChunks.EventId, OnClientPacket);
                Handshake.ServerContextCleared += OnClientContextCleared;
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Exchange: registering the 'PrXc' channel threw: " + e.Message); }

        Plugin.Frame += Tick;
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        // Best effort, straight to the transport (the queues die with this generation). Server: every subscriber gets an end
        // frame and every waiting request an error, so clients resubscribe to the next Core. Client: the server gets an
        // Unsubscribe per open subscription and a Cancel per waiting request, as far as the client's packet burst allows.
        try
        {
            if (_server) Server.Teardown();
            else Client.Teardown();
            FlushNow();
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Exchange: teardown threw: " + e.Message); }

        _installed = false;
        try { Plugin.Frame -= Tick; } catch { /* teardown */ }
        try
        {
            if (_server) CustomEvents.RemoveClientHandler(ExchangeChunks.EventId);
            else
            {
                CustomEvents.RemoveHandler(ExchangeChunks.EventId);
                Handshake.ServerContextCleared -= OnClientContextCleared;
            }
        }
        catch { /* teardown */ }

        _connected?.Dispose();
        _disconnected?.Dispose();
        _connected = _disconnected = null;
        UserLinks.Clear();
        UserEntities.Clear();
        _serverLink = null;
        Client.Gate = _ => SendResult.NotConnected;
        Client.Send = _ => false;
        Server.Gate = (_, _) => SendResult.NotConnected;
        Server.Send = (_, _) => false;
        Server.PeerModVersion = null;
        Server.HasRoom = _ => true;
        Warnings.Clear();
        Client.Warn = null;
        Server.Warn = null;
    }

    /// <summary>Keys are fixed kinds, never peer-chosen numbers or texts (see <see cref="ExchangeWarnLimiter"/>).</summary>
    private static void Warn(string key, string line)
    {
        var admitted = Warnings.Admit(key, line);
        if (admitted != null) Plugin.Logger?.LogWarning(admitted);
    }

    /// <summary>Server: bytes queued for every user together.</summary>
    private static long TotalQueued()
    {
        long n = 0;
        foreach (var l in UserLinks.Values) n += l.Out.QueuedBytes;
        return n;
    }

    /// <summary>Server: room for a snapshot (at most 1 MiB) in this user's queue and in the shared budget.</summary>
    private static bool HasRoom(int user)
    {
        long queued = UserLinks.TryGetValue(user, out var l) ? l.Out.QueuedBytes : 0;
        return queued + ExchangeLimits.MaxBodyBytes + 64 * 1024 <= ExchangeLimits.ServerQueueBytesPerUser
               && TotalQueued() + ExchangeLimits.MaxBodyBytes + 64 * 1024 <= ExchangeLimits.ServerQueueBytesTotal;
    }

    // ---- links ----------------------------------------------------------------------------------------------------------

    private static Link LinkOf(int user)
    {
        if (!UserLinks.TryGetValue(user, out var l))
        {
            double now = Now();
            UserLinks[user] = l = new Link
            {
                Out = new ExchangeOutbox(ExchangeLimits.ServerQueueBytesPerUser, ExchangeLimits.ServerSendBytesPerSecond,
                    ExchangeLimits.ServerSendBytesPerSecond / 4.0, 1000, 200, now),
                In = new ExchangeAssembler(ExchangeLimits.MaxRequestBytes + ExchangeBody.HeaderBytes + ExchangeBody.MaxTextBytes),
            };
        }

        return l;
    }

    private static Link ServerLink()
    {
        if (_serverLink == null)
        {
            // Packets, not bytes, are what the server's receive gate counts: stay well under its default budget.
            _serverLink = new Link
            {
                Out = new ExchangeOutbox(ExchangeLimits.ClientQueueBytes, 4 * 1024 * 1024, 4 * 1024 * 1024,
                    ExchangeLimits.ClientSendPacketsPerSecond, ExchangeLimits.ClientSendBurstPackets, Now()),
                In = new ExchangeAssembler(ExchangeLimits.MaxBodyBytes + ExchangeBody.HeaderBytes + ExchangeBody.MaxTextBytes),
            };
        }

        return _serverLink;
    }

    private static void ForgetUser(int index)
    {
        Server.UserLeft(index);
        UserLinks.Remove(index);
        UserEntities.Remove(index);
    }

    private static void OnClientContextCleared(string reason)
    {
        Client.Disconnected("left the server" + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")"));
        _serverLink = null;
    }

    /// <summary>Server: a user's handshake passed (again). What was held for that user's previous client state is reset.</summary>
    internal static void OnHandshakeOk(Entity userEntity, int userIndex)
    {
        if (!_installed || !_server) return;
        try
        {
            UserEntities[userIndex] = userEntity;
            if (Server.SubscriptionCount(userIndex) > 0 || Server.PendingCount(userIndex) > 0) Server.ResetUser(userIndex);
            if (UserLinks.TryGetValue(userIndex, out var l)) l.In.Reset();
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Exchange: resetting a user at its handshake threw: " + e.Message); }
    }

    /// <summary>Server: the entity of a user index this channel has seen, or <see cref="Entity.Null"/>.</summary>
    internal static Entity EntityOf(int userIndex) => UserEntities.TryGetValue(userIndex, out var e) ? e : Entity.Null;

    // ---- gates ----------------------------------------------------------------------------------------------------------

    private static SendResult ServerGate(string modGuid)
    {
        if (!_installed || _server) return SendResult.NotConnected;
        var table = ModCompatNet.PeerTables.Get(ModCompatNet.ServerPeer);
        var state = new MessagePeerState(
            connected: Worlds.Client != null && string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal),
            hasMessagesBit: (ModCompatCaps.ServerCaps & Handshake.Capability.ModExchange) != 0,
            tableKnown: table != null,
            listsMod: table != null && table.Mods.ContainsKey(modGuid));
        return ModMessageGate.Decide(state, 0);
    }

    private static SendResult UserGate(int userIndex, string modGuid)
    {
        if (!_installed || !_server) return SendResult.NotConnected;
        var table = ModCompatNet.PeerTables.Get(userIndex);
        var state = new MessagePeerState(
            connected: Handshake.UserAnswerStatus(userIndex) == "ok",
            hasMessagesBit: (Handshake.CapsOf(userIndex) & Handshake.Capability.ModExchange) != 0,
            tableKnown: table != null,
            listsMod: HandshakeInPlaceMask.ListsMod(table, userIndex, modGuid));
        return ModMessageGate.Decide(state, 0);
    }

    // ---- receive --------------------------------------------------------------------------------------------------------

    private static void OnServerPacket(int _, Entity userEntity, User user, byte* payload, int length)
    {
        if (ReceiveGate.HandlerMustRefuse(userEntity == Entity.Null, user.Index))
        {
            Plugin.Logger?.LogWarning(ReceiveGate.HandlerRefusedLog("Exchange", ReceiveGate.Fourcc(ExchangeChunks.EventId)));
            return;
        }

        if (payload == null || length <= 0) return;
        int index = user.Index;
        if (Handshake.UserAnswerStatus(index) != "ok")
        {
            Warn("notok", "Exchange: dropped a packet from user " + index.ToString(CultureInfo.InvariantCulture) + ": its handshake has not passed");
            return;
        }

        if (!user.IsConnected) return;
        UserEntities[index] = userEntity;
        var link = LinkOf(index);
        var result = link.In.Add(new ReadOnlySpan<byte>(payload, length), (first, total) => Server.Admit(index, first, total), out var body, out var error, Now());
        if (error != null) Warn("chunk-up-" + result, "Exchange: user " + index.ToString(CultureInfo.InvariantCulture) + ": " + error);
        if (result != ChunkResult.Complete) return;
        if (!ExchangeBody.TryParse(body, out var parsed, out var bad))
        {
            Warn("body-up", "Exchange: dropped a malformed body from user " + index.ToString(CultureInfo.InvariantCulture) + ": " + bad);
            return;
        }

        try { Server.Receive(index, parsed); }
        catch (Exception e) { Plugin.Logger?.LogError("Exchange: handling a " + parsed.Op + " from user " + index + " threw: " + e); }
    }

    private static void OnClientPacket(int _, byte* payload, int length)
    {
        if (payload == null || length <= 0) return;
        var link = ServerLink();
        var result = link.In.Add(new ReadOnlySpan<byte>(payload, length), null, out var body, out var error, Now());
        if (error != null) Warn("chunk-down-" + result, "Exchange: from the server: " + error);
        if (result != ChunkResult.Complete) return;
        if (!ExchangeBody.TryParse(body, out var parsed, out var bad))
        {
            Warn("body-down", "Exchange: dropped a malformed body from the server: " + bad);
            return;
        }

        try { Client.Receive(parsed); }
        catch (Exception e) { Plugin.Logger?.LogError("Exchange: handling a " + parsed.Op + " from the server threw: " + e); }
    }

    // ---- per frame ------------------------------------------------------------------------------------------------------

    private static void Tick()
    {
        if (!_installed) return;
        try
        {
            if (_server) Server.Tick();
            else Client.Tick();
            Drain();
        }
        catch (Exception e) { Warn("tick", "Exchange: the frame tick threw: " + e.Message); }
    }

    private static void Drain()
    {
        double now = Now();
        if (_server)
        {
            if (UserLinks.Count == 0) return;
            foreach (var kv in UserLinks)
                if (kv.Value.In.ExpireIdle(now, ExchangeLimits.PartialBodyIdleSeconds))
                    Warn("idle-up", "Exchange: user " + kv.Key + " stopped sending in the middle of a body; dropped it after " + ExchangeLimits.PartialBodyIdleSeconds + " s");
            var world = Worlds.Server;
            var bootstrap = world == null || !world.IsCreated ? null : world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap == null) return;
            foreach (var kv in UserLinks)
            {
                if (kv.Value.Out.Count == 0) continue;
                int index = kv.Key;
                int sent = kv.Value.Out.Drain(now, p => CustomEvents.SendToUser(bootstrap, index, ExchangeChunks.EventId, new ReadOnlySpan<byte>(p)));
                if (sent < 0) Warn("dropq-up", "Exchange: the transport refused user " + index + "'s packets for " + ExchangeOutbox.MaxFailedDrains + " frames; their queue was dropped");
            }
        }
        else if (_serverLink != null)
        {
            if (_serverLink.In.ExpireIdle(now, ExchangeLimits.PartialBodyIdleSeconds))
                Warn("idle-down", "Exchange: the server stopped sending in the middle of a body; dropped it after " + ExchangeLimits.PartialBodyIdleSeconds + " s");
            if (_serverLink.Out.Count == 0) return;
            int sent = _serverLink.Out.Drain(now, p => CustomEvents.SendToServer(ExchangeChunks.EventId, new ReadOnlySpan<byte>(p)));
            if (sent < 0) Warn("dropq-down", "Exchange: the transport refused packets to the server for " + ExchangeOutbox.MaxFailedDrains + " frames; the queue was dropped");
        }
    }

    /// <summary>At teardown: send what is queued now, without pacing (small end and unsubscribe frames).</summary>
    private static void FlushNow()
    {
        double far = Now() + 3600;
        if (_server)
        {
            var world = Worlds.Server;
            var bootstrap = world == null || !world.IsCreated ? null : world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap == null) return;
            foreach (var kv in UserLinks)
            {
                int index = kv.Key;
                kv.Value.Out.Drain(far, p => CustomEvents.SendToUser(bootstrap, index, ExchangeChunks.EventId, new ReadOnlySpan<byte>(p)));
            }
        }
        else _serverLink?.Out.Drain(far, p => CustomEvents.SendToServer(ExchangeChunks.EventId, new ReadOnlySpan<byte>(p)));
    }
}
#endif
