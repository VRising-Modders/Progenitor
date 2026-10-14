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
                Server.Send = (user, body) => LinkOf(user).Out.Enqueue(body);
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
        // Best effort, straight to the transport (the queues die with this generation): subscribers learn the server's
        // mods are going away and resubscribe to the next Core; the server learns this client's subscriptions are gone.
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
        Client.Warn = null;
        Server.Warn = null;
    }

    private static void Warn(string key, string line)
    {
        var once = ModCompatNet.Router.Once("xc|" + key, line);
        if (once != null) Plugin.Logger?.LogWarning(once);
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
            listsMod: table != null && table.Mods.ContainsKey(modGuid));
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
            Warn("notok|" + index, "Exchange: dropped a packet from user " + index.ToString(CultureInfo.InvariantCulture) + ": its handshake has not passed");
            return;
        }

        if (!user.IsConnected) return;
        UserEntities[index] = userEntity;
        var link = LinkOf(index);
        var result = link.In.Add(new ReadOnlySpan<byte>(payload, length), (first, total) => Server.Admit(index, first, total), out var body, out var error);
        if (error != null) Warn("chunk|" + index + "|" + error, "Exchange: user " + index.ToString(CultureInfo.InvariantCulture) + ": " + error);
        if (result != ChunkResult.Complete) return;
        if (!ExchangeBody.TryParse(body, out var parsed, out var bad))
        {
            Warn("body|" + index, "Exchange: dropped a malformed body from user " + index.ToString(CultureInfo.InvariantCulture) + ": " + bad);
            return;
        }

        try { Server.Receive(index, parsed); }
        catch (Exception e) { Plugin.Logger?.LogError("Exchange: handling a " + parsed.Op + " from user " + index + " threw: " + e); }
    }

    private static void OnClientPacket(int _, byte* payload, int length)
    {
        if (payload == null || length <= 0) return;
        var link = ServerLink();
        var result = link.In.Add(new ReadOnlySpan<byte>(payload, length), null, out var body, out var error);
        if (error != null) Warn("chunk|server|" + error, "Exchange: from the server: " + error);
        if (result != ChunkResult.Complete) return;
        if (!ExchangeBody.TryParse(body, out var parsed, out var bad))
        {
            Warn("body|server", "Exchange: dropped a malformed body from the server: " + bad);
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
        catch (Exception e) { Warn("tick|" + e.GetType().Name, "Exchange: the frame tick threw: " + e.Message); }
    }

    private static void Drain()
    {
        double now = Now();
        if (_server)
        {
            if (UserLinks.Count == 0) return;
            var world = Worlds.Server;
            var bootstrap = world == null || !world.IsCreated ? null : world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap == null) return;
            foreach (var kv in UserLinks)
            {
                if (kv.Value.Out.Count == 0) continue;
                int index = kv.Key;
                int sent = kv.Value.Out.Drain(now, p => CustomEvents.SendToUser(bootstrap, index, ExchangeChunks.EventId, new ReadOnlySpan<byte>(p)));
                if (sent < 0) Warn("dropq|" + index, "Exchange: the transport refused user " + index + "'s packets for " + ExchangeOutbox.MaxFailedDrains + " frames; their queue was dropped");
            }
        }
        else if (_serverLink != null && _serverLink.Out.Count > 0)
        {
            int sent = _serverLink.Out.Drain(now, p => CustomEvents.SendToServer(ExchangeChunks.EventId, new ReadOnlySpan<byte>(p)));
            if (sent < 0) Warn("dropq|server", "Exchange: the transport refused packets to the server for " + ExchangeOutbox.MaxFailedDrains + " frames; the queue was dropped");
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
