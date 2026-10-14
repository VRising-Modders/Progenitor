using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Testing;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The exchange against a hostile or unlucky peer: snapshot rebuild storms, warning floods, a peer the server cannot
/// answer, stale ids, late frames for closed subscriptions, resets, gated subscribers and stalled bodies.
/// </summary>
public class ExchangeHardeningTests
{
    private const string Mod = "progenitor.test.exchange";

    [ModMessage("board.state")]
    public sealed class BoardState
    {
        [Field(1)] public byte[] Padding = Array.Empty<byte>();
    }

    [ModMessage("board.line")]
    public sealed class BoardLine
    {
        [Field(1)] public string Line = "";
    }

    [ModMessage("blob.get")]
    public sealed class BlobGet
    {
        [Field(1)] public int Bytes;
    }

    [ModMessage("blob")]
    public sealed class Blob
    {
        [Field(1)] public byte[] Data = Array.Empty<byte>();
    }

    private static readonly MessageSchema LineSchema = MessageSchema.For(typeof(BoardLine));

    private static ExchangeType T<TMsg>()
    {
        var a = MessageSchema.For(typeof(TMsg)).Message!;
        return new ExchangeType(a.Name, a.Version, a.MinReaderVersion);
    }

    private static ExchangeServerEngine.Topic Board(ExchangeLoopback loop, Action? onBuild = null, TopicOptions? options = null, int padding = 0)
    {
        Assert.Null(loop.Server.RegisterTopic(Mod, "board", T<BoardState>(), T<BoardLine>(), options ?? new TopicOptions { MaxPerUser = 64, Burst = 100, PerSecond = 100 },
            (user, key) =>
            {
                onBuild?.Invoke();
                return MessageSchema.For(typeof(BoardState)).Encode(new BoardState { Padding = new byte[padding] });
            }, out var topic));
        return topic!;
    }

    private static ExchangeClientEngine.Sub Watch(ExchangeLoopback.Peer c, string key, List<SubscriptionEndReason>? ends = null) =>
        c.Engine.Subscribe(Mod, "board", key, T<BoardState>(), T<BoardLine>(), (p, v) => { }, (p, v) => { }, (r, t) => ends?.Add(r));

    private static byte[] Resync(uint id) =>
        new ExchangeBody { Op = ExchangeOp.Resync, ModHash = ModMessageEnvelope.ModHash(Mod), MessageHash = ModMessageEnvelope.MessageHash("board"), Id = id }.Pack();

    private static ExchangeOp? OpOf(byte[] packet) =>
        packet.Length > ExchangeChunks.HeaderBytes && packet[6] == 0 ? (ExchangeOp)packet[ExchangeChunks.HeaderBytes] : null;

    [Fact]
    public void Spammed_resyncs_with_a_full_queue_build_no_snapshots()
    {
        var loop = new ExchangeLoopback();
        int builds = 0;
        Board(loop, () => builds++);
        var c = loop.Connect(1, serverQueueBytes: 4 * 1024 * 1024);
        var subs = Enumerable.Range(0, 32).Select(i => Watch(c, "k" + i)).ToList();
        loop.Pump();
        Assert.Equal(32, builds);
        Assert.All(subs, s => Assert.Equal(SubscriptionState.Live, s.State));

        // Fill the user's queue past half, then flood resyncs for every subscription, frame after frame.
        Assert.True(c.Down.Enqueue(new byte[ExchangeLimits.MaxBodyBytes]));
        Assert.True(c.Down.Enqueue(new byte[ExchangeLimits.MaxBodyBytes]));
        Assert.True(c.Down.Enqueue(new byte[ExchangeLimits.MaxBodyBytes / 2]));
        long before = loop.Server.SnapshotBuilds;
        for (int frame = 0; frame < 120; frame++)
        {
            foreach (var s in subs) loop.Server.Receive(1, ParseBody(Resync(s.Id)));
            loop.Now += 1.0 / 60;
            loop.Server.Tick();
        }

        Assert.Equal(before, loop.Server.SnapshotBuilds);
        Assert.Equal(32, builds);
    }

    [Fact]
    public void Spammed_resyncs_are_charged_to_the_topic_rate_and_paced()
    {
        var loop = new ExchangeLoopback();
        int builds = 0;
        Board(loop, () => builds++, new TopicOptions { MaxPerUser = 64, Burst = 38, PerSecond = 2 });
        var c = loop.Connect(1);
        var subs = Enumerable.Range(0, 32).Select(i => Watch(c, "k" + i)).ToList();
        loop.Pump();
        builds = 0;
        // Ten seconds of 32 resyncs per frame: the topic budget (6 tokens left of the burst, then 2 per second) and the
        // per-frame build cap bound the work, not the flood.
        for (int frame = 0; frame < 600; frame++)
        {
            foreach (var s in subs) loop.Server.Receive(1, ParseBody(Resync(s.Id)));
            loop.Now += 1.0 / 60;
            long tickStart = loop.Server.SnapshotBuilds;
            loop.Server.Tick();
            Assert.True(loop.Server.SnapshotBuilds - tickStart <= ExchangeServerEngine.MaxSnapshotBuildsPerTick);
            c.Down.Clear();
        }

        Assert.InRange(builds, 1, 6 + 2 * 10 + 2);
        Assert.Contains(loop.Warnings, w => w.Contains("over the topic's rate"));
    }

    private static ExchangeBody ParseBody(byte[] body)
    {
        Assert.True(ExchangeBody.TryParse(body, out var b, out var error), error);
        return b;
    }

    [Fact]
    public void The_warning_limiter_keys_on_kinds_and_holds_floods_back()
    {
        double now = 0;
        var limiter = new ExchangeWarnLimiter { Clock = () => now };
        Assert.NotNull(limiter.Admit("chunk-up-Dropped", "a"));
        for (int i = 0; i < 1000; i++) Assert.Null(limiter.Admit("chunk-up-Dropped", "b" + i));
        Assert.NotNull(limiter.Admit("body-up", "another kind still logs"));
        now += ExchangeWarnLimiter.IntervalSeconds + 1;
        Assert.EndsWith("(1000 more like it held back)", limiter.Admit("chunk-up-Dropped", "c"));
        for (int i = 0; i < 500; i++) limiter.Admit("k" + i, "x");
        now += ExchangeWarnLimiter.IntervalSeconds + 1;
        Assert.NotNull(limiter.Admit("chunk-up-Dropped", "known keys are kept"));
    }

    [Fact]
    public void A_client_the_server_does_not_see_running_the_mod_gets_NotNegotiated_without_running_the_handler()
    {
        var loop = new ExchangeLoopback();
        int ran = 0;
        Assert.Null(loop.Server.RegisterHandler(Mod, T<BlobGet>(), T<Blob>(), new RequestOptions(), false, (u, p, i, r) => { ran++; r.Reply(T<Blob>(), Array.Empty<byte>()); }, out _));
        Board(loop);
        var c = loop.Connect(1);
        c.ClientRunsMod = _ => false; // the server's view of this client's table
        ExchangeOutcome? got = null;
        c.Engine.Request(Mod, T<BlobGet>(), Array.Empty<byte>(), T<Blob>(), 15, o => got = o);
        var ends = new List<SubscriptionEndReason>();
        Watch(c, "x", ends);
        loop.Pump();
        Assert.Equal(RequestStatus.NotNegotiated, got!.Status);
        Assert.Contains("feature table", got.Reason);
        Assert.Equal(0, ran);
        Assert.Equal(new[] { SubscriptionEndReason.NotNegotiated }, ends);
    }

    [Fact]
    public void A_deferred_reply_to_a_client_the_server_stopped_seeing_is_an_error_not_silence()
    {
        var loop = new ExchangeLoopback();
        ExchangeResponder? held = null;
        Assert.Null(loop.Server.RegisterHandler(Mod, T<BlobGet>(), T<Blob>(), new RequestOptions(), true, (u, p, i, r) => held = r, out _));
        var c = loop.Connect(1);
        ExchangeOutcome? got = null;
        c.Engine.Request(Mod, T<BlobGet>(), Array.Empty<byte>(), T<Blob>(), 15, o => got = o);
        loop.Pump();
        c.ClientRunsMod = _ => false;
        Assert.False(held!.Reply(T<Blob>(), Array.Empty<byte>()));
        loop.Pump();
        Assert.Equal(RequestStatus.NotNegotiated, got!.Status);
    }

    [Fact]
    public void Request_ids_start_at_a_random_point_per_engine()
    {
        var ids = new HashSet<uint>();
        for (int i = 0; i < 8; i++)
        {
            var e = new ExchangeClientEngine { Gate = _ => SendResult.Sent, Send = _ => true };
            ids.Add(e.Request(Mod, T<BlobGet>(), Array.Empty<byte>(), T<Blob>(), 15, _ => { }).Id);
        }

        Assert.True(ids.Count >= 7, "ids collided: " + string.Join(",", ids));
    }

    [Fact]
    public void Teardown_tells_the_server_about_waiting_requests_and_subscriptions()
    {
        var sent = new List<ExchangeOp>();
        var e = new ExchangeClientEngine { Gate = _ => SendResult.Sent, Send = b => { sent.Add((ExchangeOp)b[0]); return true; } };
        e.Request(Mod, T<BlobGet>(), Array.Empty<byte>(), T<Blob>(), 15, _ => throw new InvalidOperationException("no callback at teardown"));
        e.Subscribe(Mod, "board", "k", T<BoardState>(), T<BoardLine>(), (p, v) => { }, (p, v) => { }, (r, t) => throw new InvalidOperationException("no callback"));
        sent.Clear();
        e.Teardown();
        Assert.Equal(new[] { ExchangeOp.Unsubscribe, ExchangeOp.Cancel }, sent);
        Assert.Equal(0, e.PendingCount);
    }

    [Fact]
    public void Late_frames_for_a_closed_subscription_cost_no_extra_unsubscribes()
    {
        var loop = new ExchangeLoopback();
        var topic = Board(loop);
        var c = loop.Connect(1);
        var sub = Watch(c, "late");
        loop.Pump();
        var up = new List<ExchangeOp?>();
        loop.DeliverUp = (u, p) => { up.Add(OpOf(p)); return true; };
        // Closed here; the deltas the server sends before it learns that arrive afterward.
        c.Engine.Unsubscribe(sub);
        for (int i = 0; i < 5; i++) loop.Server.Publish(topic, "late", LineSchema.Encode(new BoardLine { Line = "x" + i }));
        loop.Pump();
        Assert.Equal(new ExchangeOp?[] { ExchangeOp.Unsubscribe }, up);

        // A subscription this side never had (its engine was replaced): one Unsubscribe for many frames.
        var other = Watch(c, "other");
        loop.Pump();
        up.Clear();
        c.Engine = new ExchangeClientEngine { Clock = () => loop.Now, Gate = _ => SendResult.Sent, Send = b => c.Up.Enqueue(b) };
        for (int i = 0; i < 5; i++) loop.Server.Publish(topic, "other", LineSchema.Encode(new BoardLine { Line = "y" + i }));
        loop.Pump();
        Assert.Equal(new ExchangeOp?[] { ExchangeOp.Unsubscribe }, up);
        Assert.Empty(loop.Server.Subscribers(topic, "other"));
    }

    [Fact]
    public void A_reset_answers_waiting_deferred_requests()
    {
        var loop = new ExchangeLoopback();
        Assert.Null(loop.Server.RegisterHandler(Mod, T<BlobGet>(), T<Blob>(), new RequestOptions(), true, (u, p, i, r) => { }, out _));
        var c = loop.Connect(1);
        ExchangeOutcome? got = null;
        c.Engine.Request(Mod, T<BlobGet>(), Array.Empty<byte>(), T<Blob>(), 60, o => got = o);
        loop.Pump();
        loop.Server.ResetUser(1);
        loop.Pump();
        Assert.Equal(RequestStatus.Cancelled, got!.Status);
        Assert.Equal("the server reset this connection", got.Reason);
    }

    [Fact]
    public void A_gated_subscriber_gets_a_fresh_snapshot_instead_of_a_silent_gap()
    {
        var loop = new ExchangeLoopback();
        int builds = 0;
        var topic = Board(loop, () => builds++);
        var c = loop.Connect(1);
        var lines = new List<string>();
        int snapshots = 0;
        var sub = c.Engine.Subscribe(Mod, "board", "g", T<BoardState>(), T<BoardLine>(), (p, v) => snapshots++,
            (p, v) => lines.Add(((BoardLine)LineSchema.Decode(p)).Line), null);
        loop.Pump();
        c.ClientRunsMod = _ => false;
        Assert.Equal(0, loop.Server.Publish(topic, "g", LineSchema.Encode(new BoardLine { Line = "missed" })));
        loop.Advance(2);
        Assert.Equal(1, snapshots); // nothing built while the gate is closed
        c.ClientRunsMod = _ => true;
        loop.Advance(1.5);
        loop.Pump();
        Assert.Equal(2, snapshots);
        loop.Server.Publish(topic, "g", LineSchema.Encode(new BoardLine { Line = "after" }));
        loop.Pump();
        Assert.Equal(new[] { "after" }, lines);
        Assert.Equal(SubscriptionState.Live, sub.State);
    }

    [Fact]
    public void A_body_stalled_mid_way_is_dropped_after_the_idle_time()
    {
        var asm = new ExchangeAssembler(1 << 20);
        var packets = ExchangeChunks.Split(1, new byte[40_000]);
        Assert.Equal(ChunkResult.Partial, asm.Add(packets[0], null, out _, out _, now: 10));
        Assert.True(asm.HeldBytes > 0);
        Assert.False(asm.ExpireIdle(10 + ExchangeLimits.PartialBodyIdleSeconds - 1, ExchangeLimits.PartialBodyIdleSeconds));
        Assert.True(asm.ExpireIdle(10 + ExchangeLimits.PartialBodyIdleSeconds + 1, ExchangeLimits.PartialBodyIdleSeconds));
        Assert.Equal(0, asm.HeldBytes);
        Assert.False(asm.ExpireIdle(1000, ExchangeLimits.PartialBodyIdleSeconds));
    }
}
