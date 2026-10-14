using System;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Sample;
using Progenitor.Testing;
using Xunit;

namespace Progenitor.Testing.Tests.Samples.Sample;

/// <summary>
/// The Sample's <c>.request</c> and <c>board</c> topic as a mod author tests them: the message file linked as source, the
/// game-free handler and board run through <see cref="ExchangeCompat"/> (Core's real exchange engines and chunk frames),
/// and the request, reply, snapshot and delta recorded as fixtures and replayed.
/// </summary>
public class SampleExchangeRoundTripTests
{
    [Fact]
    public void The_types_are_a_valid_request_pair_and_topic()
    {
        ExchangeCompat.AssertRequestPair<BlobRequest, BlobReply>();
        ExchangeCompat.AssertTopic<BoardSnapshot, BoardLine>("board");
    }

    [Fact]
    public void A_64_KiB_request_round_trips_in_several_packets()
    {
        var call = ExchangeCompat.Call<BlobRequest, BlobReply>(new BlobRequest { KiB = 64 }, (req, info) => SampleExchangeData.Answer(req));
        Assert.True(call.Ok, call.ToString());
        Assert.Equal(1, call.PacketsUp);
        Assert.Equal(5, call.PacketsDown);
        Assert.Equal(64 * 1024, call.Reply!.Data.Length);
        Assert.Equal(SampleExchangeData.Checksum(call.Reply.Data), call.Reply.Checksum);
        Assert.Equal(5, ExchangeCompat.PacketsFor(call.Reply));
    }

    [Fact]
    public void An_out_of_range_request_is_refused_with_the_handler_s_reason()
    {
        var call = ExchangeCompat.Call<BlobRequest, BlobReply>(new BlobRequest { KiB = 0 }, (req, info) => SampleExchangeData.Answer(req));
        Assert.Equal(RequestStatus.Refused, call.Status);
        Assert.Equal("ask for 1 to 1000 KiB, not 0", call.Reason);
    }

    [Fact]
    public void The_largest_request_still_fits_one_reply()
    {
        var call = ExchangeCompat.Call<BlobRequest, BlobReply>(new BlobRequest { KiB = SampleExchangeData.MaxKiB }, (req, info) => SampleExchangeData.Answer(req));
        Assert.True(call.Ok, call.ToString());
        Assert.True(call.ReplyBytes <= ExchangeLimits.MaxBodyBytes);
    }

    [Fact]
    public void The_board_topic_sends_a_two_packet_snapshot_then_lines_to_every_subscriber()
    {
        var board = new SampleExchangeData.Board();
        var topic = ExchangeCompat.Topic<BoardSnapshot, BoardLine>("board", (user, key) => board.Snapshot());
        var a = topic.Subscribe(1, "lobby");
        var b = topic.Subscribe(2, "lobby");
        Assert.Equal(SubscriptionState.Live, a.State);
        Assert.Equal(SampleExchangeData.SnapshotPaddingBytes, a.Snapshots.Single().Padding.Length);
        Assert.Equal(2, ExchangeCompat.PacketsFor(a.Snapshots[0]));
        Assert.Equal(2, topic.Publish("lobby", board.Post("hello")));
        Assert.Equal(2, topic.Publish("lobby", board.Post("again")));
        Assert.Equal(new[] { "hello", "again" }, a.Deltas.Select(d => d.Line));
        Assert.Equal(new[] { 1, 2 }, b.Deltas.Select(d => d.Revision));
        topic.Unsubscribe(2, b);
        Assert.Equal(1, topic.Publish("lobby", board.Post("third")));
        Assert.Equal(2, b.Deltas.Count);
        Assert.Equal(3, a.Deltas.Count);
        Assert.Empty(topic.Warnings);
    }

    [Fact]
    public void Requests_replies_and_topic_messages_record_and_replay_as_fixtures()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-exchange-fixtures-" + Guid.NewGuid().ToString("N"));
        try
        {
            var board = new SampleExchangeData.Board();
            board.Post("one");
            // A plain ctx.Messages fixture stays at one packet: the 64 KiB reply is refused there.
            Assert.Throws<MessageCompatException>(() =>
                MessageFixtures.Save(dir, "progenitor.sample", "0.9.0", SampleExchangeData.Answer(new BlobRequest { KiB = 64 })));
            ExchangeCompat.SaveRelease(dir, "progenitor.sample", "1.0.0", new object[]
            {
                new BlobRequest { KiB = 64 },
                SampleExchangeData.Answer(new BlobRequest { KiB = 64 }), // 64 KiB: larger than one ctx.Messages packet
                board.Snapshot(),
                board.Post("two"),
            });
            var fixtures = MessageFixtures.Load(dir, "progenitor.sample");
            Assert.Equal(4, fixtures.All.Count);
            MessageCompat.Replay<BlobRequest>(fixtures).ThrowIfFailed();
            MessageCompat.Replay<BlobReply>(fixtures, (reply, f) => Assert.Equal(SampleExchangeData.Checksum(reply.Data), reply.Checksum)).ThrowIfFailed();
            MessageCompat.Replay<BoardSnapshot>(fixtures).ThrowIfFailed();
            MessageCompat.Replay<BoardLine>(fixtures).ThrowIfFailed();
            Assert.True(fixtures.For<BlobReply>().Single().Payload.Length > 15 * 1024 - 16);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* temp */ }
        }
    }
}
