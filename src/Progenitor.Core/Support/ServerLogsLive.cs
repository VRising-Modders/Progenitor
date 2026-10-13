using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using ProjectM;
using ProjectM.Network;
using Progenitor.Dev;
using Progenitor.Install;
using Progenitor.Networking;
using Unity.Entities;

namespace Progenitor.Support;

/// <summary>
/// SUPPORT1 remote server logs, the game-bound half: the <c>'PrSL'</c> handlers on both sides, the server's bundle build
/// (off the main thread) and pacing pump, and the client's fetch with its timeout tick. The protocol and every decision
/// are in <see cref="ServerLogsServer"/> and <see cref="ServerLogsClient"/>. Everything is released on Core unload.
/// </summary>
internal static class ServerLogsLive
{
    private static readonly object Owner = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static bool _installed;
    private static ServerLogsServer? _server;
    private static readonly Dictionary<ulong, Entity> ServerUsers = new();
    private static bool _pumping;

    private static readonly ServerLogsClient Client = new();
    private static bool _ticking;

    private static double Now => Clock.Elapsed.TotalSeconds;

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        if (Plugin.IsServerProcess)
        {
            _server = new ServerLogsServer { Log = line => Plugin.Logger?.LogInfo(line) };
            unsafe { CustomEvents.OnReceiveFromClient(ServerLogsWire.EventId, OnServerPacket); }
        }
        else
        {
            unsafe { CustomEvents.OnReceive(ServerLogsWire.EventId, OnClientPacket); }
        }
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        if (_server != null)
        {
            try { _server.Clear((index, packet) => Send(Worlds.Server, index, packet)); } catch { /* teardown */ }
            unsafe
            {
                try { CustomEvents.RemoveClientHandler(ServerLogsWire.EventId, OnServerPacket); } catch { /* teardown */ }
            }
            _server = null;
            lock (ServerUsers) ServerUsers.Clear();
        }
        else
        {
            try
            {
                var cancel = Client.Cancel("Progenitor is unloading", unloading: true);
                if (cancel != null) CustomEvents.SendToServer(ServerLogsWire.EventId, new ReadOnlySpan<byte>(cancel));
            }
            catch { /* teardown */ }
            unsafe
            {
                try { CustomEvents.RemoveHandler(ServerLogsWire.EventId, OnClientPacket); } catch { /* teardown */ }
            }
        }
    }

    // ---- server ------------------------------------------------------------------------------------------------------

    private static unsafe void OnServerPacket(int _, Entity userEntity, User user, byte* payload, int length)
    {
        var server = _server;
        if (server == null || payload == null || length <= 0) return;
        if (!ServerLogsWire.TryDecode(new ReadOnlySpan<byte>(payload, length), out var p)) return;
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated || !world.EntityManager.HasComponent<User>(userEntity)) return;
            var current = world.EntityManager.GetComponentData<User>(userEntity);
            if (!current.IsConnected) return;
            ulong admin = current.PlatformId;
            switch (p.Kind)
            {
                case ServerLogsWire.KindRequest:
                {
                    bool isAdmin = ServerAdminList.AuthorizeShared(current.PlatformId, current.IsAdmin) != RemoteAuthorizedBy.None;
                    byte[] reply = server.Begin(admin, current.Index, p.TransferId, isAdmin, Now);
                    Send(world, current.Index, reply);
                    if (ServerLogsWire.TryDecode(reply, out var r) && r.Kind == ServerLogsWire.KindAccepted)
                    {
                        lock (ServerUsers) ServerUsers[admin] = userEntity;
                        BuildFor(server, admin, current.Index, p.TransferId);
                        EnsurePump();
                    }
                    break;
                }
                case ServerLogsWire.KindAck:
                    server.Ack(admin, p.TransferId, p.Index, Now);
                    break;
                case ServerLogsWire.KindCancel:
                    server.Cancel(admin, p.TransferId, "the admin cancelled");
                    break;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Support: server logs packet: " + SupportRedactor.Scrub(e.Message));
        }
    }

    /// <summary>Main thread: read the server's facts now, build its bundle (capped at 20 MB) on a worker, then hand it to the sender.</summary>
    private static void BuildFor(ServerLogsServer server, ulong admin, int userIndex, int id)
    {
        SupportBundleRequest request;
        try
        {
            request = SupportLive.Gather(server: true);
            request.MaxZipBytes = ServerLogsWire.MaxBundleBytes;
        }
        catch (Exception e)
        {
            SendIfAny(userIndex, server.Fail(admin, id, SupportRedactor.Scrub(e.Message)));
            return;
        }

        Task.Run(() =>
        {
            SupportBundleResult result;
            try { result = SupportBundle.Build(request); }
            catch (Exception e) { result = new SupportBundleResult { Ok = false, Error = SupportRedactor.Scrub(e.Message) }; }
            byte[]? zip = null;
            byte[]? sha = null;
            string error = result.Error;
            if (result.Ok)
            {
                try
                {
                    zip = File.ReadAllBytes(result.Path);
                    // The hash on this worker thread, not on the main thread or under the sender's lock.
                    if (zip.Length <= ServerLogsWire.MaxBundleBytes) sha = ServerLogsWire.Sha256Of(zip);
                }
                catch (Exception e) { error = SupportRedactor.Scrub(e.GetType().Name + ": " + e.Message); }
            }
            MainThread.Post(() =>
            {
                if (!_installed || !ReferenceEquals(server, _server)) return;
                SendIfAny(userIndex, zip != null ? server.Ready(admin, id, zip, Now, sha) : server.Fail(admin, id, error));
                EnsurePump();
            }, Owner);
        });
    }

    private static void EnsurePump()
    {
        if (_pumping || _server == null) return;
        _pumping = true;
        MainThread.PostDelayed(ServerLogsServer.PumpSeconds, PumpOnce, Owner);
    }

    private static void PumpOnce()
    {
        _pumping = false;
        var server = _server;
        if (!_installed || server == null) return;
        try
        {
            var world = Worlds.Server;
            // A packet the transport refuses is not counted as sent; the sender tries it again on the next pump.
            server.Pump(Now, (admin, index) => StillConnected(world, admin, index), (index, packet) => Send(world, index, packet));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Support: server logs pump: " + SupportRedactor.Scrub(e.Message));
        }
        if (server.ActiveCount > 0) EnsurePump();
        else lock (ServerUsers) ServerUsers.Clear();
    }

    private static bool StillConnected(World? world, ulong admin, int index)
    {
        if (world == null || !world.IsCreated) return false;
        Entity entity;
        lock (ServerUsers)
        {
            if (!ServerUsers.TryGetValue(admin, out entity)) return false;
        }
        if (!world.EntityManager.HasComponent<User>(entity)) return false;
        var user = world.EntityManager.GetComponentData<User>(entity);
        return user.IsConnected && user.Index == index && user.PlatformId == admin;
    }

    private static void SendIfAny(int userIndex, byte[]? packet)
    {
        if (packet != null) Send(Worlds.Server, userIndex, packet);
    }

    /// <summary>True when the transport took the packet.</summary>
    private static bool Send(World? world, int userIndex, byte[] packet)
    {
        try
        {
            if (world == null || !world.IsCreated) return false;
            var bootstrap = world.GetExistingSystemManaged<ServerBootstrapSystem>();
            if (bootstrap == null) return false;
            return CustomEvents.SendToUser(bootstrap, userIndex, ServerLogsWire.EventId, new ReadOnlySpan<byte>(packet));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Support: server logs send: " + SupportRedactor.Scrub(e.Message));
            return false;
        }
    }

    // ---- client ------------------------------------------------------------------------------------------------------

    /// <summary>The client fetch's progress line ("" when none runs).</summary>
    internal static string ProgressLine() => Client.ProgressLine;

    /// <summary>
    /// Main thread: ask the joined server for its bundle. <paramref name="done"/> runs once, on the main thread, with the zip
    /// or why there is none (a server without <c>Capability.ServerLogs</c> is answered at once as too old).
    /// </summary>
    internal static void Fetch(Action<ServerLogsOutcome> done)
    {
        if (!ServerLogsCaps.ServerSendsLogs)
        {
            done(new ServerLogsOutcome { Ok = false, Reason = "this server's Progenitor is too old to send its logs" });
            return;
        }
        byte[]? request = Client.Begin(Now, done);
        if (request == null)
        {
            done(new ServerLogsOutcome { Ok = false, Reason = "a fetch of the server's logs is already running" });
            return;
        }
        bool sent;
        try { sent = CustomEvents.SendToServer(ServerLogsWire.EventId, new ReadOnlySpan<byte>(request)); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Support: server logs request: " + SupportRedactor.Scrub(e.Message));
            sent = false;
        }
        // Never leave the fetch Busy: a request that did not go out ends it at once (done runs with the reason).
        if (!sent)
        {
            Client.Cancel("the request could not be sent to the server");
            return;
        }
        EnsureTick();
    }

    private static unsafe void OnClientPacket(int _, byte* payload, int length)
    {
        if (payload == null || length <= 0) return;
        try
        {
            // An acknowledgement the transport drops is said again from Tick (ServerLogsClient.ReAckSeconds).
            var reply = Client.Accept(new ReadOnlySpan<byte>(payload, length), Now);
            if (reply != null) CustomEvents.SendToServer(ServerLogsWire.EventId, new ReadOnlySpan<byte>(reply));
            if (Client.NeedsTicks) EnsureTick();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Support: server logs reply: " + SupportRedactor.Scrub(e.Message));
        }
    }

    private static void EnsureTick()
    {
        if (_ticking) return;
        _ticking = true;
        MainThread.PostDelayed(0.5, TickOnce, Owner);
    }

    private static void TickOnce()
    {
        _ticking = false;
        if (!_installed || !Client.NeedsTicks) return;
        bool connected;
        try { connected = Worlds.Client != null && string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal); }
        catch { connected = false; }
        try
        {
            var reply = Client.Tick(Now, connected);
            if (reply != null && connected) CustomEvents.SendToServer(ServerLogsWire.EventId, new ReadOnlySpan<byte>(reply));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Support: server logs tick: " + SupportRedactor.Scrub(e.Message));
        }
        if (Client.NeedsTicks) EnsureTick();
    }
}
