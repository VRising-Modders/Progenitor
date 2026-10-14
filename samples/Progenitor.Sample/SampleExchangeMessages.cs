using System;
using System.Collections.Generic;
using Progenitor.Networking;

namespace Progenitor.Sample;

// The Sample's request, reply, snapshot and delta types (ctx.Requests / ctx.Subscriptions; SampleExchange.cs). Game-free:
// tests/Progenitor.Testing.Tests links this file the way a mod's test project links its own message file.

/// <summary>Client to server: "send me <see cref="KiB"/> KiB". The handler accepts 1 to <see cref="SampleExchangeData.MaxKiB"/>.</summary>
[ModMessage("exchange.blob.get")]
internal sealed class BlobRequest
{
    [Field(1)] public int KiB = 64;
}

/// <summary>The reply: deterministic bytes and their checksum, so the client can prove the reassembly.</summary>
[ModMessage("exchange.blob")]
internal sealed class BlobReply
{
    [Field(1)] public byte[] Data = Array.Empty<byte>();
    [Field(2)] public uint Checksum;
    [Field(3)] public int KiB;
}

/// <summary>Server to client (a chat command's instruction): <c>request</c>, <c>subscribe</c> or <c>unsubscribe</c>.</summary>
[ModMessage("exchange.start")]
internal sealed class ExchangeStart
{
    [Field(1)] public string What = "";
    [Field(2)] public int KiB = 0;
    [Field(3)] public string Key = "";
}

/// <summary>Client to server: one result line, which the server whispers to the player.</summary>
[ModMessage("exchange.report")]
internal sealed class ExchangeReport
{
    [Field(1)] public string Line = "";
}

/// <summary>The <c>board</c> topic's snapshot: every line of one board, padded so it spans two packets.</summary>
[ModMessage("board.state")]
internal sealed class BoardSnapshot
{
    [Field(1)] public List<string> Lines = new();
    [Field(2)] public int Revision;
    [Field(3)] public byte[] Padding = Array.Empty<byte>();
}

/// <summary>The <c>board</c> topic's delta: one line appended.</summary>
[ModMessage("board.line")]
internal sealed class BoardLine
{
    [Field(1)] public string Line = "";
    [Field(2)] public int Revision;
}

/// <summary>The data and the checks both sides share (game-free).</summary>
internal static class SampleExchangeData
{
    /// <summary>The largest blob <c>.request</c> asks for: under the 1 MiB reply limit with room for the envelope.</summary>
    internal const int MaxKiB = 1000;

    /// <summary>The snapshot padding: enough that every snapshot spans two packets.</summary>
    internal const int SnapshotPaddingBytes = 20 * 1024;

    /// <summary>Lines one board keeps (the oldest go first).</summary>
    internal const int MaxBoardLines = 50;

    /// <summary>The longest posted line.</summary>
    internal const int MaxLineChars = 200;

    /// <summary><paramref name="kib"/> KiB of deterministic bytes (the same on every machine).</summary>
    internal static byte[] Blob(int kib)
    {
        var data = new byte[Math.Max(0, kib) * 1024];
        uint x = 2463534242;
        for (int i = 0; i < data.Length; i++)
        {
            x ^= x << 13;
            x ^= x >> 17;
            x ^= x << 5;
            data[i] = (byte)x;
        }

        return data;
    }

    /// <summary>FNV-1a 32 of <paramref name="data"/>.</summary>
    internal static uint Checksum(byte[] data)
    {
        uint h = 2166136261;
        foreach (byte b in data ?? Array.Empty<byte>())
        {
            h ^= b;
            h *= 16777619;
        }

        return h;
    }

    /// <summary>The server's reply to <c>BlobRequest</c>: refuses an out-of-range size with a reason.</summary>
    internal static BlobReply Answer(BlobRequest request)
    {
        if (request.KiB < 1 || request.KiB > SampleExchangeData.MaxKiB)
            throw new RequestRefusedException("ask for 1 to " + SampleExchangeData.MaxKiB + " KiB, not " + request.KiB);
        var data = SampleExchangeData.Blob(request.KiB);
        return new BlobReply { Data = data, Checksum = SampleExchangeData.Checksum(data), KiB = request.KiB };
    }

    /// <summary>Server state of one board.</summary>
    internal sealed class Board
    {
        internal readonly List<string> Lines = new();
        internal int Revision;

        /// <summary>Append a line (cut to the limit; the oldest goes past the limit) and return the delta.</summary>
        internal BoardLine Post(string text)
        {
            string line = (text ?? "").Trim();
            if (line.Length > SampleExchangeData.MaxLineChars) line = line.Substring(0, SampleExchangeData.MaxLineChars);
            Lines.Add(line);
            while (Lines.Count > SampleExchangeData.MaxBoardLines) Lines.RemoveAt(0);
            Revision++;
            return new BoardLine { Line = line, Revision = Revision };
        }

        internal BoardSnapshot Snapshot() => new()
        {
            Lines = new List<string>(Lines), Revision = Revision, Padding = new byte[SampleExchangeData.SnapshotPaddingBytes],
        };
    }
}
