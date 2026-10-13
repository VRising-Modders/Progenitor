using System;

namespace Progenitor.Content;

/// <summary>PrfJ transports the unchanged ledger encoding in ordered bounded chunks.
/// Each chunk starts with little-endian int32 generation, total bytes and offset.
/// Generations are positive and increase within one connection.</summary>
internal static class KeyLedgerChunks
{
    public const int EventId = 0x5072664A;
    public const int HeaderBytes = 3 * sizeof(int);
    public const int MaxLedgerBytes = KeyLedgerPacket.MaxLedgerBytes;
    public const int MaxChunkBytes = KeyLedgerPacket.MaxPacketBytes;
    public const int MaxChunkDataBytes = MaxChunkBytes - HeaderBytes;

    public static bool TrySplit(byte[] payload, int generation, out byte[][] chunks, out string error)
    {
        chunks = Array.Empty<byte[]>();
        error = "";
        if (generation <= 0 || payload == null || payload.Length < KeyLedgerPacket.HeaderBytes
            || payload.Length > MaxLedgerBytes)
        {
            error = "invalid ledger generation or payload size";
            return false;
        }
        chunks = new byte[(payload.Length + MaxChunkDataBytes - 1) / MaxChunkDataBytes][];
        for (int i = 0, offset = 0; i < chunks.Length; i++)
        {
            int count = Math.Min(MaxChunkDataBytes, payload.Length - offset);
            var chunk = new byte[HeaderBytes + count];
            WriteInt32(chunk, 0, generation);
            WriteInt32(chunk, 4, payload.Length);
            WriteInt32(chunk, 8, offset);
            Buffer.BlockCopy(payload, offset, chunk, HeaderBytes, count);
            chunks[i] = chunk;
            offset += count;
        }
        return true;
    }

    internal static int ReadInt32(byte[] bytes, int offset) => bytes[offset]
        | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24;

    private static void WriteInt32(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)value;
        bytes[offset + 1] = (byte)(value >> 8);
        bytes[offset + 2] = (byte)(value >> 16);
        bytes[offset + 3] = (byte)(value >> 24);
    }
}

/// <summary>One connection's ledger transfer. No bytes escape until all chunks arrive.
/// Invalid input discards pending bytes; the generation watermark rejects stale starts.
/// Reset at the connection boundary; abort pending bytes for a legacy PrfI ledger.</summary>
internal sealed class KeyLedgerChunkAssembler
{
    private byte[]? _pending;
    private int _received;
    private int _generation;

    public void Reset()
    {
        _pending = null;
        _received = 0;
        _generation = 0;
    }

    public bool TryAccept(byte[] chunk, int length, out byte[]? completed, out string error)
    {
        completed = null;
        error = "";
        if (chunk == null || length <= KeyLedgerChunks.HeaderBytes || length > chunk.Length
            || length > KeyLedgerChunks.MaxChunkBytes)
            return Reject("invalid chunk length", out error);

        int generation = KeyLedgerChunks.ReadInt32(chunk, 0);
        int total = KeyLedgerChunks.ReadInt32(chunk, 4);
        int offset = KeyLedgerChunks.ReadInt32(chunk, 8);
        int count = length - KeyLedgerChunks.HeaderBytes;
        if (generation <= 0 || total < KeyLedgerPacket.HeaderBytes || total > KeyLedgerChunks.MaxLedgerBytes
            || offset < 0 || offset > total || count > total - offset)
            return Reject("invalid chunk header", out error);

        if (offset == 0)
        {
            if (generation <= _generation)
                return Reject("duplicate or stale ledger generation", out error);
            _generation = generation;
            _pending = new byte[total];
            _received = 0;
        }
        if (_pending == null || generation != _generation || total != _pending.Length || offset != _received)
            return Reject("chunk does not continue the current ledger", out error);

        Buffer.BlockCopy(chunk, KeyLedgerChunks.HeaderBytes, _pending, offset, count);
        _received += count;
        if (_received == total)
        {
            completed = _pending;
            _pending = null;
            _received = 0;
        }
        return true;
    }

    private bool Reject(string reason, out string error)
    {
        _pending = null;
        _received = 0;
        error = reason;
        return false;
    }
}
