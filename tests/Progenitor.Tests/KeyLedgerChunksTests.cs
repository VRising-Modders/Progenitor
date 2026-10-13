using System;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public sealed class KeyLedgerChunksTests
{
    [Fact]
    public void Full_1024_row_ledger_round_trips_only_after_final_chunk()
    {
        var rows = new KeyLedgerRow[1024];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new KeyLedgerRow(i + 1, new string('a', 63), "", false);
        var encoded = new byte[KeyLedgerChunks.MaxLedgerBytes];
        Assert.True(KeyLedgerPacket.TryWrite(encoded, 1, 2048, rows, out int written, out var error), error);
        Array.Resize(ref encoded, written);
        Assert.True(KeyLedgerChunks.TrySplit(encoded, 1, out var chunks, out error), error);
        var assembler = new KeyLedgerChunkAssembler();
        byte[]? completed = null;
        for (int i = 0; i < chunks.Length; i++)
        {
            Assert.InRange(chunks[i].Length, 13, KeyLedgerChunks.MaxChunkBytes);
            Assert.True(assembler.TryAccept(chunks[i], chunks[i].Length, out completed, out error), error);
            if (i < chunks.Length - 1) Assert.Null(completed);
        }
        Assert.Equal(encoded, completed);
        Assert.True(KeyLedgerPacket.TryRead(completed!, completed!.Length, 2048, out var baseLength, out var decoded, out error), error);
        Assert.Equal(1, baseLength);
        Assert.Equal(1024, decoded.Length);
        Assert.Equal(rows[1023].Id, decoded[1023].Id);
    }

    [Fact]
    public void Duplicate_reordered_and_foreign_chunks_discard_pending_bytes()
    {
        var first = Chunks(1);
        var other = Chunks(2);
        foreach (var bad in new[] { first[0], first[2], other[1] })
        {
            var assembler = new KeyLedgerChunkAssembler();
            Assert.True(assembler.TryAccept(first[0], first[0].Length, out _, out _));
            Assert.False(assembler.TryAccept(bad, bad.Length, out var completed, out _));
            Assert.Null(completed);
            Assert.False(assembler.TryAccept(first[1], first[1].Length, out completed, out _));
            Assert.Null(completed);
        }
    }

    [Fact]
    public void New_generation_replaces_partial_transfer_and_replay_is_rejected()
    {
        var old = Chunks(1);
        var fresh = Chunks(2);
        var assembler = new KeyLedgerChunkAssembler();
        Assert.True(assembler.TryAccept(old[0], old[0].Length, out _, out _));
        byte[]? completed = null;
        foreach (var chunk in fresh)
            Assert.True(assembler.TryAccept(chunk, chunk.Length, out completed, out _));
        Assert.NotNull(completed);
        Assert.All(completed!, value => Assert.Equal((byte)2, value));
        Assert.False(assembler.TryAccept(old[0], old[0].Length, out _, out _));
        Assert.False(assembler.TryAccept(fresh[0], fresh[0].Length, out _, out _));
        assembler.Reset();
        Assert.True(assembler.TryAccept(old[0], old[0].Length, out completed, out _));
        Assert.Null(completed);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, KeyLedgerChunks.MaxLedgerBytes + 1)]
    [InlineData(4, 11)]
    [InlineData(4, 40000)]
    [InlineData(8, -1)]
    [InlineData(8, int.MaxValue)]
    public void Malformed_header_rejects_and_clears_pending(int field, int value)
    {
        var chunks = Chunks(1);
        var assembler = new KeyLedgerChunkAssembler();
        Assert.True(assembler.TryAccept(chunks[0], chunks[0].Length, out _, out _));
        var bad = (byte[])chunks[1].Clone();
        var bytes = BitConverter.GetBytes(value);
        Array.Copy(bytes, 0, bad, field, 4);
        Assert.False(assembler.TryAccept(bad, bad.Length, out var completed, out _));
        Assert.Null(completed);
        Assert.False(assembler.TryAccept(chunks[1], chunks[1].Length, out _, out _));
    }

    [Fact]
    public void Truncated_chunk_cannot_complete_or_mix_with_next_offset()
    {
        var chunks = Chunks(1);
        var assembler = new KeyLedgerChunkAssembler();
        Assert.True(assembler.TryAccept(chunks[0], chunks[0].Length - 1, out var completed, out _));
        Assert.Null(completed);
        Assert.False(assembler.TryAccept(chunks[1], chunks[1].Length, out completed, out _));
        Assert.Null(completed);
        Assert.False(assembler.TryAccept(chunks[0], 12, out _, out _));
        Assert.False(assembler.TryAccept(chunks[0], chunks[0].Length + 1, out _, out _));
        Assert.False(assembler.TryAccept(new byte[KeyLedgerChunks.MaxChunkBytes + 1], KeyLedgerChunks.MaxChunkBytes + 1, out _, out _));
    }

    [Fact]
    public void Split_enforces_total_and_generation_bounds()
    {
        Assert.Equal(0x5072664A, KeyLedgerChunks.EventId);
        Assert.False(KeyLedgerChunks.TrySplit(new byte[11], 1, out _, out _));
        Assert.False(KeyLedgerChunks.TrySplit(new byte[KeyLedgerChunks.MaxLedgerBytes + 1], 1, out _, out _));
        Assert.False(KeyLedgerChunks.TrySplit(new byte[12], 0, out _, out _));
        Assert.True(KeyLedgerChunks.TrySplit(new byte[KeyLedgerChunks.MaxLedgerBytes], 1, out var chunks, out _));
        var assembler = new KeyLedgerChunkAssembler();
        byte[]? completed = null;
        foreach (var chunk in chunks)
            Assert.True(assembler.TryAccept(chunk, chunk.Length, out completed, out _));
        Assert.Equal(KeyLedgerChunks.MaxLedgerBytes, completed!.Length);
    }

    [Fact]
    public void Bytes_beyond_declared_length_are_ignored_and_final_overrun_is_rejected()
    {
        Assert.True(KeyLedgerChunks.TrySplit(new byte[12], 1, out var chunks, out _));
        var padded = new byte[chunks[0].Length + 5];
        chunks[0].CopyTo(padded, 0);
        var assembler = new KeyLedgerChunkAssembler();
        Assert.True(assembler.TryAccept(padded, chunks[0].Length, out var completed, out _));
        Assert.Equal(12, completed!.Length);
        assembler.Reset();
        Assert.False(assembler.TryAccept(padded, padded.Length, out completed, out _));
        Assert.Null(completed);
    }

    private static byte[][] Chunks(int generation)
    {
        var payload = new byte[KeyLedgerChunks.MaxChunkDataBytes * 2 + 15];
        Array.Fill(payload, (byte)generation);
        Assert.True(KeyLedgerChunks.TrySplit(payload, generation, out var chunks, out _));
        return chunks;
    }
}
