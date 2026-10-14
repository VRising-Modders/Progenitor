using System;
using System.Collections.Generic;
using Progenitor.Networking;

namespace Progenitor.Testing;

/// <summary>
/// A server engine and any number of client engines wired through Core's real send queues, chunk frames and
/// reassemblers, with a fake clock. Only the game transport is replaced (a packet a queue sends is handed to the other
/// side's reassembler at once). Used by <see cref="ExchangeCompat"/> and by Progenitor's own tests.
/// </summary>
internal sealed class ExchangeLoopback
{
    internal sealed class Peer
    {
        internal int User;
        internal ExchangeClientEngine Engine = new();
        internal ExchangeOutbox Up = null!;
        internal ExchangeOutbox Down = null!;
        internal ExchangeAssembler AtServer = null!;
        internal ExchangeAssembler AtClient = null!;
        internal bool Connected = true;

        /// <summary>Does the server's feature table list the mod (client gate)? Default: every mod.</summary>
        internal Func<string, bool> ServerRunsMod = _ => true;

        /// <summary>Does this client's feature table list the mod (server gate)? Default: every mod.</summary>
        internal Func<string, bool> ClientRunsMod = _ => true;

        internal int PacketsUp;
        internal int PacketsDown;
    }

    internal double Now = 1000;
    internal readonly ModMessageRouter Router = new();
    internal readonly ExchangeServerEngine Server;
    internal readonly Dictionary<int, Peer> Clients = new();
    internal readonly List<string> Warnings = new();

    /// <summary>Return false to lose a packet on its way to a client (the transport is otherwise reliable and ordered).</summary>
    internal Func<int, byte[], bool>? DeliverDown { get; set; }

    /// <summary>Return false to lose a packet on its way to the server.</summary>
    internal Func<int, byte[], bool>? DeliverUp { get; set; }

    /// <summary>Seconds the clock moves per pump round.</summary>
    internal double Step = 0.05;

    internal ExchangeLoopback()
    {
        Server = new ExchangeServerEngine(Router)
        {
            Clock = () => Now,
            Gate = (user, mod) => Clients.TryGetValue(user, out var p) && p.Connected
                ? (p.ClientRunsMod(mod) ? SendResult.Sent : SendResult.PeerLacksMod)
                : SendResult.NotConnected,
            Send = (user, body) => Clients.TryGetValue(user, out var p) && p.Down.Enqueue(body),
            PeerModVersion = (_, _) => "1.0.0",
            Warn = (_, line) => Warnings.Add(line),
        };
    }

    internal Peer Connect(int user, int serverQueueBytes = ExchangeLimits.ServerQueueBytesPerUser)
    {
        var p = new Peer { User = user };
        p.Up = new ExchangeOutbox(ExchangeLimits.ClientQueueBytes, 4 * 1024 * 1024, 4 * 1024 * 1024,
            ExchangeLimits.ClientSendPacketsPerSecond, ExchangeLimits.ClientSendBurstPackets, Now);
        p.Down = new ExchangeOutbox(serverQueueBytes, ExchangeLimits.ServerSendBytesPerSecond, ExchangeLimits.ServerSendBytesPerSecond / 4.0, 1000, 200, Now);
        p.AtServer = new ExchangeAssembler(ExchangeLimits.MaxRequestBytes + ExchangeBody.HeaderBytes + ExchangeBody.MaxTextBytes);
        p.AtClient = new ExchangeAssembler(ExchangeLimits.MaxBodyBytes + ExchangeBody.HeaderBytes + ExchangeBody.MaxTextBytes);
        p.Engine.Clock = () => Now;
        p.Engine.Gate = mod => !p.Connected ? SendResult.NotConnected : p.ServerRunsMod(mod) ? SendResult.Sent : SendResult.PeerLacksMod;
        p.Engine.Send = body => p.Up.Enqueue(body);
        p.Engine.Warn = (_, line) => Warnings.Add(line);
        Clients[user] = p;
        return p;
    }

    /// <summary>The client leaves: both sides forget it.</summary>
    internal void Disconnect(int user)
    {
        if (!Clients.TryGetValue(user, out var p)) return;
        p.Connected = false;
        p.Up.Clear();
        p.Down.Clear();
        p.Engine.Disconnected("left");
        Server.UserLeft(user);
    }

    private void DeliverToServer(Peer p, byte[] packet)
    {
        p.PacketsUp++;
        if (DeliverUp != null && !DeliverUp(p.User, packet)) return;
        var r = p.AtServer.Add(packet, (first, total) => Server.Admit(p.User, first, total), out var body, out var error);
        if (error != null) Warnings.Add("server<-" + p.User + ": " + error);
        if (r != ChunkResult.Complete) return;
        if (ExchangeBody.TryParse(body, out var parsed, out var bad)) Server.Receive(p.User, parsed);
        else Warnings.Add("server<-" + p.User + ": " + bad);
    }

    private void DeliverToClient(Peer p, byte[] packet)
    {
        p.PacketsDown++;
        if (DeliverDown != null && !DeliverDown(p.User, packet)) return;
        var r = p.AtClient.Add(packet, null, out var body, out var error);
        if (error != null) Warnings.Add(p.User + "<-server: " + error);
        if (r != ChunkResult.Complete) return;
        if (ExchangeBody.TryParse(body, out var parsed, out var bad)) p.Engine.Receive(parsed);
        else Warnings.Add(p.User + "<-server: " + bad);
    }

    /// <summary>
    /// Run frames until no queue holds anything (or <paramref name="maxRounds"/>): each round moves the clock by
    /// <see cref="Step"/>, ticks every engine and drains every queue. Returns the rounds run.
    /// </summary>
    internal int Pump(int maxRounds = 2000)
    {
        int rounds = 0;
        while (rounds < maxRounds)
        {
            rounds++;
            Now += Step;
            Server.Tick();
            foreach (var p in Clients.Values) if (p.Connected) p.Engine.Tick();
            int moved = 0;
            bool queued = false;
            foreach (var p in new List<Peer>(Clients.Values))
            {
                if (!p.Connected) continue;
                var peer = p;
                moved += Math.Max(0, p.Up.Drain(Now, pk => { DeliverToServer(peer, pk); return true; }));
                moved += Math.Max(0, p.Down.Drain(Now, pk => { DeliverToClient(peer, pk); return true; }));
                queued |= p.Up.Count > 0 || p.Down.Count > 0;
            }

            if (moved == 0 && !queued) break;
        }

        return rounds;
    }

    /// <summary>Move the clock by <paramref name="seconds"/> in frames, ticking and draining as the game would.</summary>
    internal void Advance(double seconds)
    {
        double end = Now + seconds;
        while (Now < end)
        {
            Now += Step;
            Server.Tick();
            foreach (var p in Clients.Values) if (p.Connected) p.Engine.Tick();
            foreach (var p in new List<Peer>(Clients.Values))
            {
                if (!p.Connected) continue;
                var peer = p;
                p.Up.Drain(Now, pk => { DeliverToServer(peer, pk); return true; });
                p.Down.Drain(Now, pk => { DeliverToClient(peer, pk); return true; });
            }
        }
    }
}
