using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Requests, large messages and subscriptions, the wire half: the <c>'PrXc'</c> chunk frame, reassembly with its memory
/// bound and loss handling, the exchange body, and the paced all-or-nothing send queue.
/// </summary>
public class ExchangeWireTests
{
    private static byte[] Body(int n, int seed = 7)
    {
        var b = new byte[n];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public void The_id_is_in_the_reserved_range_and_the_header_leaves_room()
    {
        Assert.Equal(0x50725863, ExchangeChunks.EventId);
        Assert.True(CustomEventPolicy.IsReserved(ExchangeChunks.EventId));
        Assert.Equal(15 * 1024, ExchangeChunks.MaxPacketBytes);
        Assert.Equal(15 * 1024 - 14, ExchangeChunks.MaxChunkData);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(15 * 1024 - 14)]
    [InlineData(15 * 1024 - 13)]
    [InlineData(100_000)]
    [InlineData(1024 * 1024)]
    public void Split_then_reassemble_gives_the_body_back(int size)
    {
        var body = Body(size);
        var packets = ExchangeChunks.Split(42, body);
        Assert.Equal(ExchangeChunks.CountFor(size), packets.Count);
        Assert.All(packets, p => Assert.True(p.Length <= ExchangeChunks.MaxPacketBytes));
        var asm = new ExchangeAssembler(2 * 1024 * 1024);
        byte[] whole = Array.Empty<byte>();
        for (int i = 0; i < packets.Count; i++)
        {
            var r = asm.Add(packets[i], null, out var b, out var error);
            Assert.Null(error);
            if (i < packets.Count - 1) Assert.Equal(ChunkResult.Partial, r);
            else
            {
                Assert.Equal(ChunkResult.Complete, r);
                whole = b;
            }
        }

        Assert.Equal(body, whole);
        Assert.Equal(0, asm.HeldBytes);
    }

    [Fact]
    public void A_chunk_out_of_order_drops_the_body_and_the_next_body_still_arrives()
    {
        var a = ExchangeChunks.Split(1, Body(40_000));
        var asm = new ExchangeAssembler(1 << 20);
        Assert.Equal(ChunkResult.Partial, asm.Add(a[0], null, out _, out _));
        Assert.Equal(ChunkResult.Dropped, asm.Add(a[2], null, out _, out var error));
        Assert.Contains("chunk 2", error);
        Assert.Equal(0, asm.HeldBytes);
        // The rest of the cut body is dropped too, without its start.
        Assert.Equal(ChunkResult.Dropped, asm.Add(a[1], null, out _, out _));
        var b = ExchangeChunks.Split(2, Body(20_000, 3));
        Assert.Equal(ChunkResult.Partial, asm.Add(b[0], null, out _, out _));
        Assert.Equal(ChunkResult.Complete, asm.Add(b[1], null, out var whole, out _));
        Assert.Equal(Body(20_000, 3), whole);
    }

    [Fact]
    public void A_new_body_while_one_is_open_reports_the_cut_and_keeps_the_new_one()
    {
        var a = ExchangeChunks.Split(1, Body(40_000));
        var b = ExchangeChunks.Split(2, Body(10));
        var asm = new ExchangeAssembler(1 << 20);
        asm.Add(a[0], null, out _, out _);
        Assert.Equal(ChunkResult.Complete, asm.Add(b[0], null, out var whole, out var error));
        Assert.Contains("cut off", error);
        Assert.Equal(Body(10), whole);
    }

    [Fact]
    public void A_body_over_the_cap_is_skipped_without_storing_it()
    {
        var big = ExchangeChunks.Split(9, Body(100_000));
        var asm = new ExchangeAssembler(50_000);
        Assert.Equal(ChunkResult.Skipped, asm.Add(big[0], null, out _, out var error));
        Assert.Contains("over 50000", error);
        for (int i = 1; i < big.Count; i++)
        {
            Assert.Equal(ChunkResult.Skipped, asm.Add(big[i], null, out _, out _));
            Assert.Equal(0, asm.HeldBytes);
        }

        Assert.Equal(ChunkResult.Complete, asm.Add(ExchangeChunks.Split(10, Body(5))[0], null, out var next, out _));
        Assert.Equal(Body(5), next);
    }

    [Fact]
    public void The_admission_sees_the_first_chunk_and_the_declared_size()
    {
        var packets = ExchangeChunks.Split(3, Body(40_000));
        var asm = new ExchangeAssembler(1 << 20);
        int seen = -1;
        Assert.Equal(ChunkResult.Skipped, asm.Add(packets[0], (first, total) => { seen = total; return "no"; }, out _, out var error));
        Assert.Equal(40_000, seen);
        Assert.Equal("no", error);
        Assert.Equal(ChunkResult.Skipped, asm.Add(packets[1], null, out _, out _));
        Assert.Equal(ChunkResult.Skipped, asm.Add(packets[2], null, out _, out _));
        Assert.Equal(0, asm.HeldBytes);
    }

    [Fact]
    public void Malformed_frames_are_dropped()
    {
        var asm = new ExchangeAssembler(1 << 20);
        var p = ExchangeChunks.Split(1, Body(100))[0];
        var badVersion = (byte[])p.Clone();
        badVersion[0] = 9;
        Assert.Equal(ChunkResult.Dropped, asm.Add(badVersion, null, out _, out var e1));
        Assert.Contains("frame version", e1);
        var badCount = (byte[])p.Clone();
        badCount[8] = 5;
        Assert.Equal(ChunkResult.Dropped, asm.Add(badCount, null, out _, out var e2));
        Assert.Contains("does not fit", e2);
        Assert.Equal(ChunkResult.Dropped, asm.Add(p.Take(p.Length - 1).ToArray(), null, out _, out var e3));
        Assert.Contains("carries", e3);
        Assert.Equal(ChunkResult.Dropped, asm.Add(new byte[3], null, out _, out _));
        Assert.Equal(ChunkResult.Dropped, asm.Add(new byte[ExchangeChunks.MaxPacketBytes + 1], null, out _, out _));
    }

    [Fact]
    public void A_body_round_trips_and_bad_text_is_refused()
    {
        var b = new ExchangeBody
        {
            Op = ExchangeOp.Subscribe, Code = ExchangeCode.None, ModHash = 1, MessageHash = 2, Version = 3, MinReader = 2, Id = 77, Seq = 9,
            Text = "chest-é", Payload = new byte[] { 1, 2, 3 },
        };
        Assert.True(ExchangeBody.TryParse(b.Pack(), out var back, out var error), error);
        Assert.Equal(ExchangeOp.Subscribe, back.Op);
        Assert.Equal((1u, 2u, 3, 2, 77u, 9u, "chest-é"), (back.ModHash, back.MessageHash, back.Version, back.MinReader, back.Id, back.Seq, back.Text));
        Assert.Equal(new byte[] { 1, 2, 3 }, back.Payload);

        var packed = b.Pack();
        packed[ExchangeBody.HeaderBytes] = 0xFF; // invalid UTF-8
        Assert.False(ExchangeBody.TryParse(packed, out _, out var e1));
        Assert.Contains("UTF-8", e1);
        packed = b.Pack();
        packed[0] = 99;
        Assert.False(ExchangeBody.TryParse(packed, out _, out var e2));
        Assert.Contains("unknown op", e2);
        packed = b.Pack();
        ExchangeBody.WriteU16(packed, 22, 300);
        Assert.False(ExchangeBody.TryParse(packed, out _, out _));
        Assert.Throws<ArgumentException>(() => new ExchangeBody { Text = new string('x', 300) }.Pack());
        Assert.False(ExchangeBody.TryParse(new byte[5], out _, out _));
        var req = new ExchangeBody { Op = ExchangeOp.Request, Version = 0 }.Pack();
        Assert.False(ExchangeBody.TryParse(req, out _, out var e3));
        Assert.Contains("version 0", e3);
    }

    [Fact]
    public void Cut_keeps_whole_characters()
    {
        Assert.Equal("abc", ExchangeBody.Cut("abc", 10));
        string cut = ExchangeBody.Cut(new string('é', 200), 255);
        Assert.True(Encoding.UTF8.GetByteCount(cut) <= 255);
        Assert.Equal(127, cut.Length);
    }

    [Fact]
    public void The_queue_takes_a_whole_body_or_nothing()
    {
        var q = new ExchangeOutbox(40_000, 1e9, 1e9, 1e9, 1e9, 0);
        Assert.True(q.Enqueue(Body(30_000)));
        Assert.Equal(2, q.Count);
        Assert.False(q.Enqueue(Body(20_000)));
        Assert.Equal(2, q.Count);
        Assert.True(q.Enqueue(Body(5_000)));
        Assert.Equal(3, q.Count);
    }

    [Fact]
    public void The_queue_paces_packets_and_keeps_order_through_refusals()
    {
        var q = new ExchangeOutbox(10 << 20, 1e9, 1e9, 8, 20, 0);
        for (int i = 0; i < 30; i++) Assert.True(q.Enqueue(new[] { (byte)i }));
        var sent = new List<byte[]>();
        Assert.Equal(20, q.Drain(0, p => { sent.Add(p); return true; }));     // the burst
        Assert.Equal(0, q.Drain(0, p => { sent.Add(p); return true; }));
        Assert.Equal(8, q.Drain(1, p => { sent.Add(p); return true; }));      // one second later
        // The transport refuses: the head stays queued and goes first next time.
        Assert.Equal(0, q.Drain(2, _ => false));
        Assert.Equal(2, q.Drain(2, p => { sent.Add(p); return true; }));
        var bodies = sent.Select(p => p[ExchangeChunks.HeaderBytes]).ToList();
        Assert.Equal(Enumerable.Range(0, 30).Select(i => (byte)i), bodies);
    }

    [Fact]
    public void A_queue_the_transport_keeps_refusing_is_dropped()
    {
        var q = new ExchangeOutbox(1 << 20, 1e9, 1e9, 1e9, 1e9, 0);
        q.Enqueue(Body(10));
        for (int i = 0; i < ExchangeOutbox.MaxFailedDrains - 1; i++) Assert.Equal(0, q.Drain(i, _ => false));
        Assert.Equal(-1, q.Drain(1000, _ => false));
        Assert.Equal(0, q.Count);
        Assert.Equal(0, q.QueuedBytes);
    }

    [Fact]
    public void The_byte_budget_spreads_a_large_body_over_time()
    {
        var q = new ExchangeOutbox(4 << 20, 512 * 1024, 128 * 1024, 1000, 200, 0);
        Assert.True(q.Enqueue(Body(1024 * 1024)));
        int total = 0;
        double t = 0;
        int frames = 0;
        while (q.Count > 0 && frames < 10_000)
        {
            total += q.Drain(t, _ => true);
            t += 1.0 / 60;
            frames++;
        }

        Assert.Equal(ExchangeChunks.CountFor(1024 * 1024), total);
        Assert.InRange(t, 1.5, 2.5); // about 1 MiB at 512 KiB/s after a 128 KiB burst
    }
}
