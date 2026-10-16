using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Testing;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Requests and subscriptions end to end, game-free: Core's client and server engines talk through the real send queues,
/// chunk frames and reassemblers (<see cref="ExchangeLoopback"/>); only the game transport and the clock are fake.
/// </summary>
public class ExchangeEngineTests
{
    private const string Mod = "progenitor.test.exchange";

    [ModMessage("blob.get")]
    public sealed class BlobGet
    {
        [Field(1)] public int Bytes;
        [Field(2)] public string Tag = "";
    }

    [ModMessage("blob.get", Version = 2, MinReaderVersion = 2)]
    public sealed class BlobGetV2Breaking
    {
        [Field(1)] public long Bytes;
    }

    [ModMessage("blob")]
    public sealed class Blob
    {
        [Field(1)] public byte[] Data = Array.Empty<byte>();
        [Field(2)] public string Tag = "";
    }

    [ModMessage("board.state")]
    public sealed class BoardState
    {
        [Field(1)] public List<string> Lines = new();
        [Field(2)] public byte[] Padding = Array.Empty<byte>();
    }

    [ModMessage("board.line")]
    public sealed class BoardLine
    {
        [Field(1)] public string Line = "";
    }

    private static readonly MessageSchema GetSchema = MessageSchema.For(typeof(BlobGet));
    private static readonly MessageSchema BlobSchema = MessageSchema.For(typeof(Blob));
    private static readonly MessageSchema StateSchema = MessageSchema.For(typeof(BoardState));
    private static readonly MessageSchema LineSchema = MessageSchema.For(typeof(BoardLine));

    private static ExchangeType T<TMsg>()
    {
        var a = MessageSchema.For(typeof(TMsg)).Message!;
        return new ExchangeType(a.Name, a.Version, a.MinReaderVersion);
    }

    private static byte[] Bytes(int n)
    {
        var b = new byte[n];
        new Random(n).NextBytes(b);
        return b;
    }

    /// <summary>A server with a blob handler (sync), and one connected client.</summary>
    private static (ExchangeLoopback Loop, ExchangeLoopback.Peer Client, List<int> Calls) BlobServer(RequestOptions? options = null)
    {
        var loop = new ExchangeLoopback();
        var calls = new List<int>();
        Assert.Null(loop.Server.RegisterHandler(Mod, T<BlobGet>(), T<Blob>(), options ?? new RequestOptions(), false, (user, payload, info, r) =>
        {
            var req = (BlobGet)GetSchema.Decode(payload);
            calls.Add(user);
            if (req.Tag == "refuse") throw new RequestRefusedException("not yours");
            if (req.Tag == "throw") throw new InvalidOperationException("secret detail");
            r.Reply(T<Blob>(), BlobSchema.Encode(new Blob { Data = Bytes(req.Bytes), Tag = req.Tag + " v" + info.SenderVersion + " u" + info.UserIndex }));
        }, out _));
        return (loop, loop.Connect(1), calls);
    }

    private static ExchangeOutcome? Ask(ExchangeLoopback.Peer c, BlobGet req, double timeout = 15) => Ask(c, T<BlobGet>(), GetSchema.Encode(req), timeout);

    private static ExchangeOutcome? Ask(ExchangeLoopback.Peer c, ExchangeType type, byte[] payload, double timeout = 15)
    {
        ExchangeOutcome? got = null;
        c.Engine.Request(Mod, type, payload, T<Blob>(), timeout, o =>
        {
            Assert.Null(got); // exactly once
            got = o;
        });
        return got; // non-null only for a refusal at send
    }

    [Fact]
    public void A_reply_larger_than_one_packet_is_split_and_reassembled()
    {
        var (loop, c, calls) = BlobServer();
        ExchangeOutcome? got = null;
        c.Engine.Request(Mod, T<BlobGet>(), GetSchema.Encode(new BlobGet { Bytes = 200_000, Tag = "big" }), T<Blob>(), 15, o => got = o);
        loop.Pump();
        Assert.NotNull(got);
        Assert.Equal(RequestStatus.Ok, got!.Status);
        var blob = (Blob)BlobSchema.Decode(got.Payload);
        Assert.Equal(Bytes(200_000), blob.Data);
        Assert.Equal("big v1 u1", blob.Tag);
        Assert.Equal(1, got.Version);
        Assert.Equal(new[] { 1 }, calls);
        Assert.True(c.PacketsDown >= 14, "the reply took " + c.PacketsDown + " packets");
        Assert.Equal(1, c.PacketsUp);
        Assert.Equal(0, c.Engine.PendingCount);
    }

    [Fact]
    public void A_request_larger_than_one_packet_is_reassembled_on_the_server()
    {
        var (loop, c, calls) = BlobServer(new RequestOptions { MaxRequestBytes = 100_000 });
        var req = new BlobGet { Bytes = 10, Tag = new string('t', 4000) };
        // Tag is capped at 4 KiB; pad the request with a hand-written unknown field instead.
        var payload = GetSchema.Encode(req).Concat(new MessageWriter().Bytes(99, Bytes(60_000)).ToArray()).ToArray();
        ExchangeOutcome? got = null;
        c.Engine.Request(Mod, T<BlobGet>(), payload, T<Blob>(), 15, o => got = o);
        loop.Pump();
        Assert.Equal(RequestStatus.Ok, got!.Status);
        Assert.True(c.PacketsUp > 1, "the request took " + c.PacketsUp + " packet(s)");
        Assert.Single(calls);
    }

    [Fact]
    public void Answers_find_their_requests_whatever_the_order()
    {
        var loop = new ExchangeLoopback();
        var waiting = new List<(BlobGet Req, ExchangeResponder R)>();
        Assert.Null(loop.Server.RegisterHandler(Mod, T<BlobGet>(), T<Blob>(), new RequestOptions(), true,
            (user, payload, info, r) => waiting.Add(((BlobGet)GetSchema.Decode(payload), r)), out _));
        var c = loop.Connect(1);
        var got = new Dictionary<string, ExchangeOutcome>();
        foreach (var tag in new[] { "a", "b", "c" })
            c.Engine.Request(Mod, T<BlobGet>(), GetSchema.Encode(new BlobGet { Bytes = 1, Tag = tag }), T<Blob>(), 15, o => got[tag] = o);
        loop.Pump();
        Assert.Equal(3, waiting.Count);
        Assert.Empty(got);
        foreach (var (req, r) in Enumerable.Reverse(waiting))
            Assert.True(r.Reply(T<Blob>(), BlobSchema.Encode(new Blob { Tag = "answer to " + req.Tag })));
        loop.Pump();
        foreach (var tag in new[] { "a", "b", "c" })
            Assert.Equal("answer to " + tag, ((Blob)BlobSchema.Decode(got[tag].Payload)).Tag);
    }

    [Fact]
    public void A_refusal_carries_its_reason_and_a_throw_does_not_leak_its_text()
    {
        var (loop, c, _) = BlobServer();
        ExchangeOutcome? refused = null, failed = null;
        c.Engine.Request(Mod, T<BlobGet>(), GetSchema.Encode(new BlobGet { Tag = "refuse" }), T<Blob>(), 15, o => refused = o);
        c.Engine.Request(Mod, T<BlobGet>(), GetSchema.Encode(new BlobGet { Tag = "throw" }), T<Blob>(), 15, o => failed = o);
        loop.Pump();
        Assert.Equal(RequestStatus.Refused, refused!.Status);
        Assert.Equal("not yours", refused.Reason);
        Assert.Equal(RequestStatus.Failed, failed!.Status);
        Assert.DoesNotContain("secret", failed.Reason);
        Assert.Contains(loop.Warnings, w => w.Contains("handler threw") && w.Contains("secret detail"));
    }

    [Fact]
    public void A_request_nobody_handles_is_answered_NoHandler()
    {
        var (loop, c, _) = BlobServer();
        ExchangeOutcome? got = null;
        c.Engine.Request(Mod, new ExchangeType("other.get", 1, 1), Array.Empty<byte>(), T<Blob>(), 15, o => got = o);
        ExchangeOutcome? noMod = null;
        c.Engine.Request("progenitor.test.absent", T<BlobGet>(), Array.Empty<byte>(), T<Blob>(), 15, o => noMod = o);
        loop.Pump();
        Assert.Equal(RequestStatus.NoHandler, got!.Status);
        Assert.Equal(RequestStatus.NoHandler, noMod!.Status);
    }

    [Fact]
    public void A_request_the_server_cannot_read_is_answered_Unsupported()
    {
        var (loop, c, calls) = BlobServer();
        ExchangeOutcome? got = null;
        var v2 = T<BlobGetV2Breaking>();
        c.Engine.Request(Mod, v2, MessageSchema.For(typeof(BlobGetV2Breaking)).Encode(new BlobGetV2Breaking { Bytes = 5 }), T<Blob>(), 15, o => got = o);
        loop.Pump();
        Assert.Equal(RequestStatus.Unsupported, got!.Status);
        Assert.Contains("needs reader 2", got.Reason);
        Assert.Empty(calls);
    }

    [Fact]
    public void A_reply_the_client_cannot_read_is_Unsupported_and_another_type_is_Malformed()
    {
        var loop = new ExchangeLoopback();
        Assert.Null(loop.Server.RegisterHandler(Mod, T<BlobGet>(), new ExchangeType("blob", 3, 3), new RequestOptions(), false,
            (u, p, i, r) => r.Reply(new ExchangeType("blob", 3, 3), Array.Empty<byte>()), out _));
        Assert.Null(loop.Server.RegisterHandler(Mod, new ExchangeType("odd.get", 1, 1), new ExchangeType("odd", 1, 1), new RequestOptions(), false,
            (u, p, i, r) => r.Reply(new ExchangeType("odd", 1, 1), Array.Empty<byte>()), out _));
        var c = loop.Connect(1);
        ExchangeOutcome? tooNew = null, other = null;
        c.Engine.Request(Mod, T<BlobGet>(), Array.Empty<byte>(), T<Blob>(), 15, o => tooNew = o);
        c.Engine.Request(Mod, new ExchangeType("odd.get", 1, 1), Array.Empty<byte>(), T<Blob>(), 15, o => other = o);
        loop.Pump();
        Assert.Equal(RequestStatus.Unsupported, tooNew!.Status);
        Assert.Equal(RequestStatus.Malformed, other!.Status);
    }

    [Fact]
    public void A_request_over_the_handler_cap_is_refused_at_its_first_chunk()
    {
        var (loop, c, calls) = BlobServer(new RequestOptions { MaxRequestBytes = 1000 });
        var payload = GetSchema.Encode(new BlobGet()).Concat(new MessageWriter().Bytes(99, Bytes(50_000)).ToArray()).ToArray();
        ExchangeOutcome? got = null;
        c.Engine.Request(Mod, T<BlobGet>(), payload, T<Blob>(), 15, o => got = o);
        loop.Pump();
        Assert.Equal(RequestStatus.TooLarge, got!.Status);
        Assert.Contains("at most 1000", got.Reason);
        Assert.Empty(calls);
        Assert.Equal(0, c.AtServer.HeldBytes);
        // The connection still works.
        ExchangeOutcome? next = null;
        c.Engine.Request(Mod, T<BlobGet>(), GetSchema.Encode(new BlobGet { Bytes = 3 }), T<Blob>(), 15, o => next = o);
        loop.Pump();
        Assert.Equal(RequestStatus.Ok, next!.Status);
    }

    [Fact]
    public void A_request_over_the_ceiling_is_refused_at_send()
    {
        var (_, c, _) = BlobServer();
        var got = Ask(c, T<BlobGet>(), new byte[ExchangeLimits.MaxRequestBytes + 1]);
        Assert.Equal(RequestStatus.TooLarge, got!.Status);
    }

    [Fact]
    public void The_per_user_rate_limit_refuses_past_the_burst()
    {
        var (loop, c, calls) = BlobServer(new RequestOptions { Burst = 3, PerSecond = 0.001 });
        var got = new List<ExchangeOutcome>();
        for (int i = 0; i < 5; i++) c.Engine.Request(Mod, T<BlobGet>(), GetSchema.Encode(new BlobGet { Bytes = 1 }), T<Blob>(), 15, got.Add);
        loop.Pump();
        Assert.Equal(3, got.Count(o => o.Status == RequestStatus.Ok));
        Assert.Equal(2, got.Count(o => o.Status == RequestStatus.RateLimited));
        Assert.Equal(3, calls.Count);
        // Another user has a bucket of its own.
        var other = loop.Connect(2);
        ExchangeOutcome? o2 = null;
        other.Engine.Request(Mod, T<BlobGet>(), GetSchema.Encode(new BlobGet { Bytes = 1 }), T<Blob>(), 15, o => o2 = o);
        loop.Pump();
        Assert.Equal(RequestStatus.Ok, o2!.Status);
    }

    [Fact]
    public void Deferred_requests_are_bounded_and_time_out_on_the_server()
    {
        var loop = new ExchangeLoopback();
        var held = new List<ExchangeResponder>();
        Assert.Null(loop.Server.RegisterHandler(Mod, T<BlobGet>(), T<Blob>(), new RequestOptions { MaxPending = 2, TimeoutSeconds = 5 }, true,
            (u, p, i, r) => held.Add(r), out _));
        var c = loop.Connect(1);
        var got = new List<ExchangeOutcome>();
        for (int i = 0; i < 3; i++) c.Engine.Request(Mod, T<BlobGet>(), Array.Empty<byte>(), T<Blob>(), 60, got.Add);
        loop.Pump();
        Assert.Equal(2, held.Count);
        Assert.Equal(RequestStatus.Busy, Assert.Single(got).Status);
        Assert.Equal(2, loop.Server.PendingCount(1));
        loop.Advance(6);
        Assert.Equal(3, got.Count);
        Assert.Equal(2, got.Count(o => o.Status == RequestStatus.Timeout));
        Assert.Equal(0, loop.Server.PendingCount(1));
        Assert.False(held[0].Reply(T<Blob>(), Array.Empty<byte>())); // too late
    }

    [Fact]
    public void The_client_times_out_cancels_and_ignores_a_late_answer()
    {
        var loop = new ExchangeLoopback();
        ExchangeResponder? held = null;
        Assert.Null(loop.Server.RegisterHandler(Mod, T<BlobGet>(), T<Blob>(), new RequestOptions { TimeoutSeconds = 100 }, true,
            (u, p, i, r) => held = r, out _));
        var c = loop.Connect(1);
        var got = new List<ExchangeOutcome>();
        c.Engine.Request(Mod, T<BlobGet>(), Array.Empty<byte>(), T<Blob>(), 2, got.Add);
        loop.Pump();
        loop.Advance(3);
        Assert.Equal(RequestStatus.Timeout, Assert.Single(got).Status);
        // The client's timeout tells the server, which stops waiting.
        Assert.True(held!.Cancelled);
        Assert.Equal(0, loop.Server.PendingCount(1));
        Assert.False(held.Reply(T<Blob>(), Array.Empty<byte>()));
        loop.Pump();
        Assert.Single(got);
    }

    [Fact]
    public void Cancel_ends_the_request_once_and_tells_the_server()
    {
        var loop = new ExchangeLoopback();
        ExchangeResponder? held = null;
        Assert.Null(loop.Server.RegisterHandler(Mod, T<BlobGet>(), T<Blob>(), new RequestOptions(), true, (u, p, i, r) => held = r, out _));
        var c = loop.Connect(1);
        var got = new List<ExchangeOutcome>();
        var pending = c.Engine.Request(Mod, T<BlobGet>(), Array.Empty<byte>(), T<Blob>(), 15, got.Add);
        loop.Pump();
        Assert.True(c.Engine.Cancel(pending));
        Assert.False(c.Engine.Cancel(pending));
        loop.Pump();
        Assert.Equal(RequestStatus.Cancelled, Assert.Single(got).Status);
        Assert.True(held!.Cancelled);
    }

    [Fact]
    public void A_disconnect_ends_every_request_and_subscription()
    {
        var (loop, c, _) = BlobServer();
        var topic = BoardTopic(loop);
        var ends = new List<SubscriptionEndReason>();
        c.Engine.Subscribe(Mod, "board", "lobby", T<BoardState>(), T<BoardLine>(), (p, v) => { }, (p, v) => { }, (r, t) => ends.Add(r));
        loop.Pump();
        var got = new List<ExchangeOutcome>();
        c.Engine.Request(Mod, T<BlobGet>(), Array.Empty<byte>(), T<Blob>(), 15, got.Add);
        loop.Disconnect(1);
        Assert.Equal(RequestStatus.Disconnected, Assert.Single(got).Status);
        Assert.Equal(new[] { SubscriptionEndReason.Disconnected }, ends);
        Assert.Equal(0, loop.Server.SubscriptionCount(1));
        Assert.Empty(loop.Server.Subscribers(topic, "lobby"));
    }

    [Fact]
    public void Nothing_is_sent_to_a_server_that_lacks_the_mod()
    {
        var (loop, c, _) = BlobServer();
        c.ServerRunsMod = _ => false;
        var got = Ask(c, new BlobGet());
        Assert.Equal(RequestStatus.NotSent, got!.Status);
        Assert.Equal(SendResult.PeerLacksMod, got.NotSent);
        loop.Pump();
        Assert.Equal(0, c.PacketsUp);
    }

    [Fact]
    public void Unloading_ends_requests_on_both_sides()
    {
        var loop = new ExchangeLoopback();
        ExchangeResponder? held = null;
        Assert.Null(loop.Server.RegisterHandler(Mod, T<BlobGet>(), T<Blob>(), new RequestOptions(), true, (u, p, i, r) => held = r, out _));
        var c = loop.Connect(1);
        var got = new List<ExchangeOutcome>();
        c.Engine.Request(Mod, T<BlobGet>(), Array.Empty<byte>(), T<Blob>(), 15, got.Add);
        loop.Pump();
        loop.Server.ForgetMod(Mod);
        loop.Pump();
        Assert.Equal(RequestStatus.Unloaded, Assert.Single(got).Status);
        Assert.True(held!.Done);

        // The client's own unload: Cancelled, the callback still runs.
        Assert.Null(loop.Server.RegisterHandler(Mod, T<BlobGet>(), T<Blob>(), new RequestOptions(), true, (u, p, i, r) => { }, out _));
        var mine = new List<ExchangeOutcome>();
        c.Engine.Request(Mod, T<BlobGet>(), Array.Empty<byte>(), T<Blob>(), 15, mine.Add);
        c.Engine.ForgetMod(Mod);
        Assert.Equal(RequestStatus.Cancelled, Assert.Single(mine).Status);
        Assert.Equal("this mod unloaded", mine[0].Reason);
        Assert.True(mine[0].Unloading); // PINS1: marked, so the mod can skip any player-facing failure

        // A plain Cancel is not an unload.
        var cancelled = new List<ExchangeOutcome>();
        var p = c.Engine.Request(Mod, T<BlobGet>(), Array.Empty<byte>(), T<Blob>(), 15, cancelled.Add);
        c.Engine.Cancel(p);
        Assert.False(Assert.Single(cancelled).Unloading);
    }

    [Fact]
    public void Registration_refuses_mistakes()
    {
        var loop = new ExchangeLoopback();
        Assert.Null(loop.Server.RegisterHandler(Mod, T<BlobGet>(), T<Blob>(), new RequestOptions(), false, (u, p, i, r) => { }, out _));
        Assert.Contains("one handler per request", loop.Server.RegisterHandler(Mod, T<BlobGet>(), T<Blob>(), new RequestOptions(), false, (u, p, i, r) => { }, out _));
        Assert.Contains("different message names", loop.Server.RegisterHandler(Mod, T<Blob>(), T<Blob>(), new RequestOptions(), false, (u, p, i, r) => { }, out _));
        Assert.Contains("MaxRequestBytes", loop.Server.RegisterHandler(Mod, new ExchangeType("x.get", 1, 1), T<Blob>(), new RequestOptions { MaxRequestBytes = 300_000 }, false, (u, p, i, r) => { }, out _));
        Assert.Contains("three different names", loop.Server.RegisterTopic(Mod, "board", T<BoardState>(), T<BoardState>(), new TopicOptions(), (u, k) => Array.Empty<byte>(), out _));
        Assert.Contains("request name", loop.Server.RegisterTopic(Mod, "blob.get", T<BoardState>(), T<BoardLine>(), new TopicOptions(), (u, k) => Array.Empty<byte>(), out _));
        Assert.Contains("MaxKeyBytes", loop.Server.RegisterTopic(Mod, "board", T<BoardState>(), T<BoardLine>(), new TopicOptions { MaxKeyBytes = 500 }, (u, k) => Array.Empty<byte>(), out _));
    }

    // ---- subscriptions ------------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, List<string>> Boards = new();

    private static ExchangeServerEngine.Topic BoardTopic(ExchangeLoopback loop, int paddingBytes = 0, TopicOptions? options = null, Func<int, string, bool>? allow = null)
    {
        Assert.Null(loop.Server.RegisterTopic(Mod, "board", T<BoardState>(), T<BoardLine>(), options ?? new TopicOptions(), (user, key) =>
        {
            if (allow != null && !allow(user, key)) throw new RequestRefusedException("not your board");
            lock (Boards)
            {
                if (!Boards.TryGetValue(key, out var lines)) Boards[key] = lines = new List<string>();
                return StateSchema.Encode(new BoardState { Lines = new List<string>(lines), Padding = new byte[paddingBytes] });
            }
        }, out var topic));
        return topic!;
    }

    private sealed class Watcher
    {
        internal readonly List<BoardState> Snapshots = new();
        internal readonly List<string> Lines = new();
        internal readonly List<(SubscriptionEndReason Reason, string Text)> Ends = new();
        internal ExchangeClientEngine.Sub Sub = null!;
    }

    private static Watcher Watch(ExchangeLoopback.Peer c, string key)
    {
        var w = new Watcher();
        w.Sub = c.Engine.Subscribe(Mod, "board", key, T<BoardState>(), T<BoardLine>(),
            (p, v) => w.Snapshots.Add((BoardState)StateSchema.Decode(p)),
            (p, v) => w.Lines.Add(((BoardLine)LineSchema.Decode(p)).Line),
            (r, t) => w.Ends.Add((r, t)));
        return w;
    }

    private static byte[] Line(string s) => LineSchema.Encode(new BoardLine { Line = s });

    [Fact]
    public void A_subscription_gets_a_large_snapshot_then_deltas_in_order_and_fans_out()
    {
        var loop = new ExchangeLoopback();
        var topic = BoardTopic(loop, paddingBytes: 40_000);
        var opened = new List<(int, string)>();
        var closed = new List<(int, string)>();
        topic.Opened = (u, k) => opened.Add((u, k));
        topic.Closed = (u, k) => closed.Add((u, k));
        var a = Watch(loop.Connect(1), "fanout");
        var b = Watch(loop.Connect(2), "fanout");
        var other = Watch(loop.Connect(3), "elsewhere");
        loop.Pump();
        Assert.Equal(SubscriptionState.Live, a.Sub.State);
        Assert.Single(a.Snapshots);
        Assert.Equal(40_000, a.Snapshots[0].Padding.Length);
        Assert.True(loop.Clients[1].PacketsDown >= 3);
        Assert.Equal(new[] { 1, 2 }, loop.Server.Subscribers(topic, "fanout"));
        Assert.Equal(new[] { (1, "fanout"), (2, "fanout"), (3, "elsewhere") }, opened);

        for (int i = 1; i <= 5; i++) Assert.Equal(2, loop.Server.Publish(topic, "fanout", Line("line " + i)));
        loop.Pump();
        var expected = Enumerable.Range(1, 5).Select(i => "line " + i).ToList();
        Assert.Equal(expected, a.Lines);
        Assert.Equal(expected, b.Lines);
        Assert.Empty(other.Lines);
        Assert.Equal(5, a.Sub.Deltas);

        loop.Clients[2].Engine.Unsubscribe(b.Sub);
        loop.Pump();
        Assert.Equal(new[] { (2, "fanout") }, closed);
        Assert.Equal(1, loop.Server.Publish(topic, "fanout", Line("after")));
        loop.Pump();
        Assert.Equal("after", a.Lines.Last());
        Assert.Equal(5, b.Lines.Count);
        Assert.Empty(a.Ends);
        Assert.Empty(b.Ends); // closing from the client runs no end callback
    }

    [Fact]
    public void A_lost_delta_is_noticed_and_a_fresh_snapshot_resyncs()
    {
        var loop = new ExchangeLoopback();
        var topic = BoardTopic(loop);
        var c = loop.Connect(1);
        var w = Watch(c, "gap");
        loop.Pump();
        loop.Server.Publish(topic, "gap", Line("one"));
        loop.Pump();
        // Lose the next delta in the transport (a body the receiver never sees).
        bool lose = true;
        loop.DeliverDown = (user, packet) => { bool keep = !lose; lose = false; return keep; };
        loop.Server.Publish(topic, "gap", Line("two"));
        loop.Pump();
        loop.DeliverDown = null;
        loop.Server.Publish(topic, "gap", Line("three"));
        loop.Pump();
        Assert.Equal(SubscriptionState.Resyncing, w.Sub.State);
        loop.Advance(1.5); // the server paces snapshots (one per second per subscription)
        loop.Pump();
        Assert.Equal(SubscriptionState.Live, w.Sub.State);
        Assert.Equal(2, w.Snapshots.Count);
        Assert.Equal(new[] { "one" }, w.Lines);
        Assert.Contains(loop.Warnings, l => l.Contains("missed a delta"));
        loop.Server.Publish(topic, "gap", Line("four"));
        loop.Pump();
        Assert.Equal(new[] { "one", "four" }, w.Lines);
    }

    [Fact]
    public void A_full_send_queue_turns_deltas_into_a_later_snapshot()
    {
        var loop = new ExchangeLoopback();
        var topic = BoardTopic(loop);
        var c = loop.Connect(1, serverQueueBytes: 20_000);
        var w = Watch(c, "slow");
        loop.Pump();
        int queued = 0;
        for (int i = 0; i < 10; i++) queued += loop.Server.Publish(topic, "slow", LineSchema.Encode(new BoardLine { Line = new string('x', 4000) + i }));
        Assert.True(queued < 10, "some deltas found the queue full");
        loop.Pump();
        loop.Advance(1.5);
        loop.Pump();
        Assert.Equal(SubscriptionState.Live, w.Sub.State);
        Assert.Equal(2, w.Snapshots.Count); // the first, and the one that covered the dropped deltas
        Assert.Equal(queued, w.Lines.Count);
    }

    [Fact]
    public void A_refused_subscription_ends_with_the_reason()
    {
        var loop = new ExchangeLoopback();
        BoardTopic(loop, allow: (u, k) => k != "secret");
        var c = loop.Connect(1);
        var w = Watch(c, "secret");
        loop.Pump();
        Assert.Equal(SubscriptionState.Ended, w.Sub.State);
        Assert.Equal((SubscriptionEndReason.Refused, "not your board"), Assert.Single(w.Ends));
        Assert.Equal(0, loop.Server.SubscriptionCount(1));
    }

    [Fact]
    public void Keys_and_subscriptions_per_user_are_bounded()
    {
        var loop = new ExchangeLoopback();
        BoardTopic(loop, options: new TopicOptions { MaxKeyBytes = 8, MaxPerUser = 2 });
        var c = loop.Connect(1);
        var longKey = Watch(c, "much-too-long-key");
        var w1 = Watch(c, "k1");
        var w2 = Watch(c, "k2");
        var w3 = Watch(c, "k3");
        loop.Pump();
        Assert.Equal(SubscriptionEndReason.BadKey, longKey.Ends.Single().Reason);
        Assert.Equal(SubscriptionState.Live, w1.Sub.State);
        Assert.Equal(SubscriptionState.Live, w2.Sub.State);
        Assert.Equal(SubscriptionEndReason.Busy, w3.Ends.Single().Reason);
        c.Engine.Unsubscribe(w1.Sub);
        loop.Pump();
        var w4 = Watch(c, "k4");
        loop.Pump();
        Assert.Equal(SubscriptionState.Live, w4.Sub.State);
        Assert.Equal(2, loop.Server.SubscriptionCount(1));
    }

    [Fact]
    public void Subscribes_past_the_burst_are_rate_limited()
    {
        var loop = new ExchangeLoopback();
        BoardTopic(loop, options: new TopicOptions { Burst = 3, PerSecond = 0.001 });
        var c = loop.Connect(1);
        var w = Enumerable.Range(1, 4).Select(i => Watch(c, "r" + i)).ToList();
        loop.Pump();
        Assert.Equal(3, w.Count(x => x.Sub.State == SubscriptionState.Live));
        Assert.Equal(SubscriptionEndReason.RateLimited, w[3].Ends.Single().Reason);
    }

    [Fact]
    public void A_server_mod_reload_ends_with_Unloaded_and_the_client_resubscribes()
    {
        var loop = new ExchangeLoopback();
        BoardTopic(loop);
        var c = loop.Connect(1);
        var w = Watch(c, "reload");
        loop.Pump();
        loop.Server.ForgetMod(Mod);
        loop.Pump();
        Assert.Equal(SubscriptionState.Retrying, w.Sub.State);
        Assert.Empty(w.Ends);
        // The next generation registers the topic again; the client's backoff (1 s) brings it back.
        var topic = BoardTopic(loop);
        loop.Advance(1.5);
        loop.Pump();
        Assert.Equal(SubscriptionState.Live, w.Sub.State);
        Assert.Equal(2, w.Snapshots.Count);
        loop.Server.Publish(topic, "reload", Line("back"));
        loop.Pump();
        Assert.Equal(new[] { "back" }, w.Lines);
    }

    [Fact]
    public void A_topic_that_never_comes_back_ends_after_the_retries()
    {
        var loop = new ExchangeLoopback();
        BoardTopic(loop);
        var c = loop.Connect(1);
        var w = Watch(c, "gone");
        loop.Pump();
        loop.Server.ForgetMod(Mod);
        loop.Advance(120);
        Assert.Equal(SubscriptionState.Ended, w.Sub.State);
        Assert.Equal(SubscriptionEndReason.NoTopic, Assert.Single(w.Ends).Reason);
    }

    [Fact]
    public void A_server_reset_of_the_user_resubscribes()
    {
        var loop = new ExchangeLoopback();
        var topic = BoardTopic(loop);
        var c = loop.Connect(1);
        var w = Watch(c, "reset");
        loop.Pump();
        loop.Server.ResetUser(1);
        loop.Pump();
        Assert.Equal(SubscriptionState.Retrying, w.Sub.State);
        loop.Advance(1.5);
        loop.Pump();
        Assert.Equal(SubscriptionState.Live, w.Sub.State);
        Assert.Equal(new[] { 1 }, loop.Server.Subscribers(topic, "reset"));
    }

    [Fact]
    public void A_lost_snapshot_is_asked_for_again_then_times_out()
    {
        var loop = new ExchangeLoopback();
        BoardTopic(loop);
        var c = loop.Connect(1);
        loop.DeliverDown = (u, p) => false;
        var w = Watch(c, "void");
        loop.Pump();
        Assert.Equal(SubscriptionState.Pending, w.Sub.State);
        loop.Advance(ExchangeLimits.SnapshotTimeoutSeconds + 0.5);
        Assert.Equal(SubscriptionState.Pending, w.Sub.State); // asked again; that snapshot is lost too
        loop.DeliverDown = null;
        loop.Advance(ExchangeLimits.SnapshotTimeoutSeconds + 0.5);
        loop.Pump();
        Assert.Equal(SubscriptionState.Live, w.Sub.State); // the third try arrived
        Assert.Single(w.Snapshots);

        loop.DeliverDown = (u, p) => false;
        var lost = Watch(c, "void2");
        loop.Advance(3 * ExchangeLimits.SnapshotTimeoutSeconds + 1);
        Assert.Equal(SubscriptionEndReason.Timeout, Assert.Single(lost.Ends).Reason);
    }

    [Fact]
    public void A_delta_the_client_cannot_read_ends_the_subscription()
    {
        var loop = new ExchangeLoopback();
        Assert.Null(loop.Server.RegisterTopic(Mod, "board", T<BoardState>(), new ExchangeType("board.line", 2, 2), new TopicOptions(),
            (u, k) => StateSchema.Encode(new BoardState()), out var topic));
        var c = loop.Connect(1);
        var w = Watch(c, "v");
        loop.Pump();
        loop.Server.Publish(topic!, "v", Line("x"));
        loop.Pump();
        Assert.Equal(SubscriptionEndReason.Unsupported, Assert.Single(w.Ends).Reason);
        Assert.Contains("needs reader 2", w.Ends[0].Text);
        Assert.Equal(0, loop.Server.SubscriptionCount(1)); // the client told the server
    }

    [Fact]
    public void Disposing_a_topic_ends_its_subscriptions_as_Closed()
    {
        var loop = new ExchangeLoopback();
        var topic = BoardTopic(loop);
        var c = loop.Connect(1);
        var w = Watch(c, "x");
        loop.Pump();
        loop.Server.UnregisterTopic(topic);
        loop.Pump();
        Assert.Equal(SubscriptionEndReason.Closed, Assert.Single(w.Ends).Reason);
        Assert.Equal(0, loop.Server.Publish(topic, "x", Line("late")));
    }

    [Fact]
    public void A_user_leaving_closes_their_subscriptions_on_the_server()
    {
        var loop = new ExchangeLoopback();
        var topic = BoardTopic(loop);
        var closed = new List<(int, string)>();
        topic.Closed = (u, k) => closed.Add((u, k));
        Watch(loop.Connect(1), "a");
        Watch(loop.Connect(2), "a");
        loop.Pump();
        loop.Disconnect(1);
        Assert.Equal(new[] { (1, "a") }, closed);
        Assert.Equal(new[] { 2 }, loop.Server.Subscribers(topic, "a"));
    }

    [Fact]
    public void A_hostile_client_cannot_send_server_frames_or_huge_control_frames()
    {
        var loop = new ExchangeLoopback();
        BoardTopic(loop);
        var c = loop.Connect(1);
        // A client sending a Reply or a Snapshot is dropped with a line; a 50 KB "subscribe" is refused at its first chunk.
        c.Up.Enqueue(new ExchangeBody { Op = ExchangeOp.Snapshot, ModHash = ModMessageEnvelope.ModHash(Mod), Version = 1, MinReader = 1 }.Pack());
        c.Up.Enqueue(new ExchangeBody { Op = ExchangeOp.Subscribe, ModHash = ModMessageEnvelope.ModHash(Mod), Payload = Bytes(50_000) }.Pack());
        loop.Pump();
        Assert.Contains(loop.Warnings, w => w.Contains("dropped a Snapshot frame"));
        Assert.Contains(loop.Warnings, w => w.Contains("control frames are small"));
        Assert.Equal(0, c.AtServer.HeldBytes);
        Assert.Equal(0, loop.Server.SubscriptionCount(1));
    }
}
