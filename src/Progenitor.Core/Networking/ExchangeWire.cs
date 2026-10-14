using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Networking;

// Requests, large messages and subscriptions (docs/modules.md, "Requests, large messages and subscriptions"): the
// 'PrXc' chunk frame, the exchange body, the per-connection reassembly and the paced send queue. Game-free: compiled
// into Core, Progenitor.Testing and the test suite. ExchangeNet.cs puts it on the wire.

/// <summary>The limits of <c>ctx.Requests</c> and <c>ctx.Subscriptions</c>. Every one is enforced by Core, not by the mod.</summary>
public static class ExchangeLimits
{
    /// <summary>The ceiling of <see cref="RequestOptions.MaxRequestBytes"/>: the largest encoded request a server accepts (256 KiB).</summary>
    public const int MaxRequestBytes = 256 * 1024;

    /// <summary>The default largest encoded request a handler accepts (16 KiB).</summary>
    public const int DefaultMaxRequestBytes = 16 * 1024;

    /// <summary>The largest encoded reply, snapshot or delta (1 MiB). Larger is refused at the sender.</summary>
    public const int MaxBodyBytes = 1024 * 1024;

    /// <summary>The largest subscription key, in UTF-8 bytes (and the ceiling of <see cref="TopicOptions.MaxKeyBytes"/>).</summary>
    public const int MaxKeyBytes = 128;

    /// <summary>The largest refusal reason sent to a client, in UTF-8 bytes; a longer one is cut.</summary>
    public const int MaxReasonBytes = 256;

    /// <summary>The default client timeout of a request, in seconds.</summary>
    public const double DefaultTimeoutSeconds = 15;

    /// <summary>The longest client timeout of a request, in seconds.</summary>
    public const double MaxTimeoutSeconds = 300;

    /// <summary>Client: requests of one mod waiting for an answer at once. More are refused with <c>Busy</c>.</summary>
    public const int MaxPendingRequestsPerMod = 64;

    /// <summary>Client: open subscriptions of one mod. More are refused with <c>Busy</c>.</summary>
    public const int MaxSubscriptionsPerMod = 32;

    /// <summary>Server: deferred requests of one user waiting for an answer at once, all mods together.</summary>
    public const int MaxPendingPerUser = 16;

    /// <summary>Server: subscriptions of one user, all mods together.</summary>
    public const int MaxSubscriptionsPerUser = 64;

    /// <summary>Server: bytes queued for one user before a send is refused (and a subscription falls back to a fresh snapshot).</summary>
    public const int ServerQueueBytesPerUser = 4 * 1024 * 1024;

    /// <summary>Client: bytes queued for the server before a send is refused.</summary>
    public const int ClientQueueBytes = 1024 * 1024;

    /// <summary>Server to one client: the paced send rate, in bytes per second (a burst of a quarter of it).</summary>
    public const int ServerSendBytesPerSecond = 512 * 1024;

    /// <summary>Client to server: at most this many exchange packets per second (a burst of <see cref="ClientSendBurstPackets"/>), under the server's receive gate.</summary>
    public const int ClientSendPacketsPerSecond = 8;

    /// <summary>Client to server: the packet burst.</summary>
    public const int ClientSendBurstPackets = 20;

    /// <summary>Seconds a subscription waits for its snapshot before it asks again (three tries, then it ends with <c>Timeout</c>).</summary>
    public const double SnapshotTimeoutSeconds = 10;

    /// <summary>Times a subscription the server ended for a retryable reason (its mod reloaded) subscribes again, after 1, 2, 4, 8 and 16 seconds.</summary>
    public const int MaxResubscribes = 5;

    /// <summary>Server: the least time between two snapshots of one subscription (a resync storm is paced, not refused).</summary>
    public const double MinSnapshotIntervalSeconds = 1;
}

/// <summary>How a request ended. Exactly one result per request.</summary>
public enum RequestStatus
{
    /// <summary>The server answered; <c>Reply</c> holds the decoded reply.</summary>
    Ok = 0,

    /// <summary>The server's handler refused it (<c>RequestRefusedException</c> or <c>Refuse</c>); <c>Reason</c> says why.</summary>
    Refused = 1,

    /// <summary>Nothing was sent; <c>NotSent</c> holds the <see cref="SendResult"/> (no server, older Progenitor, the server lacks the mod).</summary>
    NotSent = 2,

    /// <summary>No answer within the timeout.</summary>
    Timeout = 3,

    /// <summary>Cancelled on this side (<c>Cancel</c>, or this mod unloaded).</summary>
    Cancelled = 4,

    /// <summary>The client left the server before the answer arrived.</summary>
    Disconnected = 5,

    /// <summary>The server runs the mod but has no handler for this request.</summary>
    NoHandler = 6,

    /// <summary>The server's per-user rate limit for this request refused it.</summary>
    RateLimited = 7,

    /// <summary>Too many requests are waiting (on either side), or a send queue is full.</summary>
    Busy = 8,

    /// <summary>The request is over the handler's <c>MaxRequestBytes</c>, or the reply over <see cref="ExchangeLimits.MaxBodyBytes"/>.</summary>
    TooLarge = 9,

    /// <summary>The server's handler threw. The exception is logged on the server, not sent.</summary>
    Failed = 10,

    /// <summary>A version the reader cannot read: the request's or the reply's minimum reader version is above the other side's.</summary>
    Unsupported = 11,

    /// <summary>The server's mod unloaded (a hot reload) before it answered.</summary>
    Unloaded = 12,

    /// <summary>The reply could not be decoded, or was not the reply type this request expects.</summary>
    Malformed = 13,
}

/// <summary>Where a client subscription is.</summary>
public enum SubscriptionState
{
    /// <summary>Subscribed; waiting for the first snapshot.</summary>
    Pending = 0,

    /// <summary>The snapshot arrived; deltas follow in order.</summary>
    Live = 1,

    /// <summary>A delta was missing; a fresh snapshot was asked for and deltas are ignored until it arrives.</summary>
    Resyncing = 2,

    /// <summary>The server ended it for a retryable reason (its mod reloaded); it subscribes again shortly.</summary>
    Retrying = 3,

    /// <summary>Over. No callback runs again.</summary>
    Ended = 4,
}

/// <summary>Why a subscription ended.</summary>
public enum SubscriptionEndReason
{
    /// <summary>Disposed on this side, this mod unloaded, or the server's topic was disposed.</summary>
    Closed = 0,

    /// <summary>The server's snapshot function refused this user or key.</summary>
    Refused = 1,

    /// <summary>The server runs no such topic (after the retries).</summary>
    NoTopic = 2,

    /// <summary>The server's mod unloaded and did not come back within the retries.</summary>
    Unloaded = 3,

    /// <summary>The client left the server.</summary>
    Disconnected = 4,

    /// <summary>No snapshot within three tries of <see cref="ExchangeLimits.SnapshotTimeoutSeconds"/>.</summary>
    Timeout = 5,

    /// <summary>The server's per-user subscribe rate limit refused it.</summary>
    RateLimited = 6,

    /// <summary>Too many subscriptions (either side), or a send queue is full.</summary>
    Busy = 7,

    /// <summary>The server's snapshot or delta has a minimum reader version above this mod's, or another type.</summary>
    Unsupported = 8,

    /// <summary>Nothing was sent (no server, older Progenitor, the server lacks the mod).</summary>
    NotSent = 9,

    /// <summary>The server's snapshot function threw (logged on the server).</summary>
    Failed = 10,

    /// <summary>The key is too long or not valid.</summary>
    BadKey = 11,

    /// <summary>The server forgot the subscription (this client's connection was reset) and it did not come back within the retries.</summary>
    Reset = 12,
}

/// <summary>The one-byte code an error or end frame carries.</summary>
internal enum ExchangeCode : byte
{
    None = 0,
    Refused = 1,
    NoHandler = 2,
    RateLimited = 3,
    Busy = 4,
    TooLarge = 5,
    Failed = 6,
    Unsupported = 7,
    Unloaded = 8,
    Timeout = 9,
    Malformed = 10,
    NoTopic = 11,
    Closed = 12,

    /// <summary>The server forgot the subscription (this user re-handshaked, or it never knew the id). Retryable.</summary>
    Reset = 13,
    BadKey = 14,
}

/// <summary>What a body is.</summary>
internal enum ExchangeOp : byte
{
    Request = 1,
    Reply = 2,
    Error = 3,
    Cancel = 4,
    Subscribe = 5,
    Snapshot = 6,
    Delta = 7,
    Unsubscribe = 8,
    Resync = 9,
    Ended = 10,
}

/// <summary>
/// One logical exchange message: <c>[op u8][code u8][mod hash u32le][message hash u32le][message version u16le][min reader
/// version u16le][id u32le][seq u32le][text length u16le][text][payload]</c>, a 24-byte header. The hashes, version and
/// minimum reader version are those of the <c>'PrMs'</c> envelope (mod compatibility C2), so the version rules hold for
/// every request, reply, snapshot and delta. <c>id</c> is the request's correlation id or the subscription id;
/// <c>seq</c> the delta sequence; <c>text</c> a subscription key or a refusal reason (UTF-8).
/// </summary>
internal sealed class ExchangeBody
{
    internal const int HeaderBytes = 24;

    /// <summary>The longest text a body may carry (a key or a reason).</summary>
    internal const int MaxTextBytes = 256;

    internal ExchangeOp Op;
    internal ExchangeCode Code;
    internal uint ModHash;
    internal uint MessageHash;
    internal int Version = 1;
    internal int MinReader = 1;
    internal uint Id;
    internal uint Seq;
    internal string Text = "";
    internal byte[] Payload = Array.Empty<byte>();

    /// <summary>The byte offsets of <see cref="Id"/> and <see cref="Seq"/> (a fan-out patches them per subscriber).</summary>
    internal const int IdOffset = 14;
    internal const int SeqOffset = 18;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Cut <paramref name="text"/> to at most <paramref name="maxBytes"/> UTF-8 bytes on a character boundary.</summary>
    internal static string Cut(string? text, int maxBytes)
    {
        string t = text ?? "";
        if (Encoding.UTF8.GetByteCount(t) <= maxBytes) return t;
        var sb = new StringBuilder();
        int bytes = 0;
        foreach (var rune in t.EnumerateRunes())
        {
            int n = rune.Utf8SequenceLength;
            if (bytes + n > maxBytes) break;
            sb.Append(rune.ToString());
            bytes += n;
        }

        return sb.ToString();
    }

    internal byte[] Pack()
    {
        byte[] text = Encoding.UTF8.GetBytes(Text ?? "");
        if (text.Length > MaxTextBytes) throw new ArgumentException("exchange text is " + text.Length + " bytes; max is " + MaxTextBytes);
        var payload = Payload ?? Array.Empty<byte>();
        var p = new byte[HeaderBytes + text.Length + payload.Length];
        p[0] = (byte)Op;
        p[1] = (byte)Code;
        WriteU32(p, 2, ModHash);
        WriteU32(p, 6, MessageHash);
        WriteU16(p, 10, Version);
        WriteU16(p, 12, MinReader);
        WriteU32(p, IdOffset, Id);
        WriteU32(p, SeqOffset, Seq);
        WriteU16(p, 22, text.Length);
        Buffer.BlockCopy(text, 0, p, HeaderBytes, text.Length);
        Buffer.BlockCopy(payload, 0, p, HeaderBytes + text.Length, payload.Length);
        return p;
    }

    /// <summary>
    /// Read the header (and text) from the start of a body: enough for an admission decision on the first chunk.
    /// <paramref name="headerAndText"/> is how many bytes the header and text take.
    /// </summary>
    internal static bool TryPeek(ReadOnlySpan<byte> start, out ExchangeBody head, out int headerAndText, out string? error)
    {
        head = new ExchangeBody();
        headerAndText = 0;
        error = null;
        if (start.Length < HeaderBytes)
        {
            error = "the body is shorter than its header";
            return false;
        }

        byte op = start[0];
        if (op < (byte)ExchangeOp.Request || op > (byte)ExchangeOp.Ended)
        {
            error = "unknown op " + op;
            return false;
        }

        head.Op = (ExchangeOp)op;
        head.Code = (ExchangeCode)start[1];
        head.ModHash = ReadU32(start, 2);
        head.MessageHash = ReadU32(start, 6);
        head.Version = start[10] | (start[11] << 8);
        head.MinReader = start[12] | (start[13] << 8);
        head.Id = ReadU32(start, IdOffset);
        head.Seq = ReadU32(start, SeqOffset);
        int textLen = start[22] | (start[23] << 8);
        if (textLen > MaxTextBytes)
        {
            error = "text of " + textLen + " bytes is over " + MaxTextBytes;
            return false;
        }

        if (start.Length < HeaderBytes + textLen)
        {
            error = "the body is shorter than its text";
            return false;
        }

        try { head.Text = StrictUtf8.GetString(start.Slice(HeaderBytes, textLen)); }
        catch (DecoderFallbackException)
        {
            error = "the text is not valid UTF-8";
            return false;
        }

        bool versioned = head.Op is ExchangeOp.Request or ExchangeOp.Reply or ExchangeOp.Snapshot or ExchangeOp.Delta;
        if (versioned && (head.Version < 1 || head.MinReader < 1))
        {
            error = "message version 0";
            return false;
        }

        headerAndText = HeaderBytes + textLen;
        return true;
    }

    internal static bool TryParse(byte[] body, out ExchangeBody parsed, out string? error)
    {
        if (!TryPeek(body ?? Array.Empty<byte>(), out parsed, out int used, out error)) return false;
        int n = body!.Length - used;
        if (n > 0)
        {
            parsed.Payload = new byte[n];
            Buffer.BlockCopy(body, used, parsed.Payload, 0, n);
        }

        return true;
    }

    internal static void WriteU32(byte[] p, int at, uint v)
    {
        p[at] = (byte)v;
        p[at + 1] = (byte)(v >> 8);
        p[at + 2] = (byte)(v >> 16);
        p[at + 3] = (byte)(v >> 24);
    }

    internal static void WriteU16(byte[] p, int at, int v)
    {
        p[at] = (byte)v;
        p[at + 1] = (byte)(v >> 8);
    }

    internal static uint ReadU32(ReadOnlySpan<byte> p, int at) =>
        (uint)(p[at] | (p[at + 1] << 8) | (p[at + 2] << 16) | (p[at + 3] << 24));
}

/// <summary>
/// The <c>'PrXc'</c> chunk frame: <c>[frame version u8][flags u8][transfer u32le][seq u16le][count u16le][total
/// u32le][data]</c>, a 14-byte header. A body of <c>total</c> bytes travels as <c>count</c> packets of at most
/// <see cref="MaxChunkData"/> bytes each, in order, never interleaved with another body on the same connection.
/// </summary>
internal static class ExchangeChunks
{
    /// <summary><c>'PrXc'</c>. Inside the reserved range, so only Core may register or send on it.</summary>
    internal const int EventId = 0x50725863;

    internal const byte FrameVersion = 1;
    internal const int HeaderBytes = 14;

    /// <summary>The transport's single-packet limit (<c>CustomEvents.MaxPayloadBytes</c>, 15 KiB).</summary>
    internal const int MaxPacketBytes = ModMessageEnvelope.MaxPacketBytes;

    internal const int MaxChunkData = MaxPacketBytes - HeaderBytes;

    /// <summary>How many packets a body of <paramref name="bodyBytes"/> takes.</summary>
    internal static int CountFor(int bodyBytes) => bodyBytes <= 0 ? 1 : (bodyBytes + MaxChunkData - 1) / MaxChunkData;

    internal static List<byte[]> Split(uint transfer, byte[] body)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        int count = CountFor(body.Length);
        if (count > ushort.MaxValue) throw new ArgumentException("body of " + body.Length + " bytes needs more than 65535 packets");
        var list = new List<byte[]>(count);
        for (int seq = 0; seq < count; seq++)
        {
            int at = seq * MaxChunkData;
            int n = Math.Min(MaxChunkData, body.Length - at);
            var p = new byte[HeaderBytes + n];
            p[0] = FrameVersion;
            p[1] = 0;
            ExchangeBody.WriteU32(p, 2, transfer);
            ExchangeBody.WriteU16(p, 6, seq);
            ExchangeBody.WriteU16(p, 8, count);
            ExchangeBody.WriteU32(p, 10, (uint)body.Length);
            if (n > 0) Buffer.BlockCopy(body, at, p, HeaderBytes, n);
            list.Add(p);
        }

        return list;
    }

    internal static bool TryHeader(ReadOnlySpan<byte> packet, out uint transfer, out int seq, out int count, out int total, out string? error)
    {
        transfer = 0;
        seq = count = total = 0;
        error = null;
        if (packet.Length < HeaderBytes || packet.Length > MaxPacketBytes)
        {
            error = "packet length " + packet.Length + " is outside the frame";
            return false;
        }

        if (packet[0] != FrameVersion)
        {
            error = "frame version " + packet[0] + " is not " + FrameVersion;
            return false;
        }

        transfer = ExchangeBody.ReadU32(packet, 2);
        seq = packet[6] | (packet[7] << 8);
        count = packet[8] | (packet[9] << 8);
        uint t = ExchangeBody.ReadU32(packet, 10);
        if (t > int.MaxValue)
        {
            error = "total of " + t + " bytes";
            return false;
        }

        total = (int)t;
        if (count < 1 || count != CountFor(total) || seq >= count)
        {
            error = "chunk " + seq + " of " + count + " does not fit a body of " + total + " bytes";
            return false;
        }

        int expected = Math.Min(MaxChunkData, total - seq * MaxChunkData);
        if (packet.Length - HeaderBytes != expected)
        {
            error = "chunk " + seq + " carries " + (packet.Length - HeaderBytes) + " bytes, not " + expected;
            return false;
        }

        return true;
    }
}

/// <summary>What <see cref="ExchangeAssembler.Add"/> did with a packet.</summary>
internal enum ChunkResult
{
    /// <summary>Stored; more chunks follow.</summary>
    Partial,

    /// <summary>The last chunk: the body is whole.</summary>
    Complete,

    /// <summary>Malformed or out of order: dropped, and anything collected with it.</summary>
    Dropped,

    /// <summary>Part of a body the admission refused at its first chunk: consumed without storing.</summary>
    Skipped,
}

/// <summary>
/// Reassembles one connection's bodies. Chunks of one body arrive in order on the reliable channel and bodies are never
/// interleaved (the sender's queue is first in, first out), so at most one body is collected at a time: the memory one
/// connection can hold is one body, at most <see cref="MaxBody"/> bytes, allocated only after its first chunk declared a
/// size under the cap and passed the admission. A chunk out of order drops what was collected (the request then times
/// out, a subscription resyncs); a new first chunk while a body is open drops the open one.
/// </summary>
internal sealed class ExchangeAssembler
{
    internal ExchangeAssembler(int maxBody) => MaxBody = maxBody;

    internal int MaxBody { get; }

    private uint _transfer;
    private int _next;
    private int _count;
    private int _total;
    private byte[]? _buffer;
    private bool _open;
    private bool _skipping;

    /// <summary>Bytes held right now (at most <see cref="MaxBody"/>).</summary>
    internal int HeldBytes => _buffer?.Length ?? 0;

    /// <summary>
    /// Add one packet. <paramref name="admit"/> sees the first chunk's data and the declared total of a new body and
    /// returns null to accept it, or a reason to skip the whole body (the caller answers it).
    /// </summary>
    internal ChunkResult Add(ReadOnlySpan<byte> packet, Func<byte[], int, string?>? admit, out byte[] body, out string? error)
    {
        body = Array.Empty<byte>();
        error = null;
        if (!ExchangeChunks.TryHeader(packet, out uint transfer, out int seq, out int count, out int total, out error))
        {
            Reset();
            return ChunkResult.Dropped;
        }

        var data = packet.Slice(ExchangeChunks.HeaderBytes);
        if (seq == 0)
        {
            string? cut = _open ? "transfer " + _transfer + " was cut off at chunk " + _next + " of " + _count : null;
            Reset();
            if (total > MaxBody)
            {
                error = (cut != null ? cut + "; " : "") + "a body of " + total + " bytes is over " + MaxBody;
                StartSkipping(transfer, count, total);
                return ChunkResult.Skipped;
            }

            string? refusal = admit?.Invoke(data.ToArray(), total);
            if (refusal != null)
            {
                error = cut != null ? cut + "; " + refusal : refusal;
                StartSkipping(transfer, count, total);
                return ChunkResult.Skipped;
            }

            error = cut;
            if (count == 1)
            {
                body = data.ToArray();
                return ChunkResult.Complete;
            }

            _open = true;
            _transfer = transfer;
            _count = count;
            _total = total;
            _buffer = new byte[total];
            data.CopyTo(_buffer);
            _next = 1;
            return ChunkResult.Partial;
        }

        if (!_open || transfer != _transfer || seq != _next || count != _count || total != _total)
        {
            error = _open
                ? "chunk " + seq + " of transfer " + transfer + " arrived while chunk " + _next + " of transfer " + _transfer + " was due"
                : "chunk " + seq + " of transfer " + transfer + " arrived without its start";
            Reset();
            return ChunkResult.Dropped;
        }

        if (_skipping)
        {
            _next++;
            if (_next >= _count) Reset();
            return ChunkResult.Skipped;
        }

        data.CopyTo(new Span<byte>(_buffer, seq * ExchangeChunks.MaxChunkData, data.Length));
        _next++;
        if (_next < _count) return ChunkResult.Partial;
        body = _buffer!;
        Reset();
        return ChunkResult.Complete;
    }

    private void StartSkipping(uint transfer, int count, int total)
    {
        if (count <= 1) return;
        _open = true;
        _skipping = true;
        _transfer = transfer;
        _count = count;
        _total = total;
        _next = 1;
    }

    internal void Reset()
    {
        _open = false;
        _skipping = false;
        _buffer = null;
        _next = _count = _total = 0;
        _transfer = 0;
    }
}

/// <summary>A token bucket. Refills continuously; <see cref="TryTake"/> spends.</summary>
internal sealed class ExchangeBucket
{
    internal ExchangeBucket(double burst, double perSecond, double now)
    {
        Burst = Math.Max(1, burst);
        PerSecond = Math.Max(0, perSecond);
        _tokens = Burst;
        _last = now;
    }

    internal double Burst { get; }
    internal double PerSecond { get; }
    private double _tokens;
    private double _last;

    private void Refill(double now)
    {
        if (now > _last) _tokens = Math.Min(Burst, _tokens + (now - _last) * PerSecond);
        _last = now;
    }

    internal bool TryTake(double now, double amount = 1)
    {
        Refill(now);
        if (_tokens < amount) return false;
        _tokens -= amount;
        return true;
    }

    /// <summary>True when <paramref name="amount"/> is available now (or the bucket is full, for an amount over the burst).</summary>
    internal bool Has(double now, double amount)
    {
        Refill(now);
        return _tokens >= Math.Min(amount, Burst);
    }

    internal void Spend(double amount) => _tokens -= amount;
}

/// <summary>
/// One connection's send queue: packets first in, first out, all of a body or none, at most <see cref="MaxBytes"/>
/// queued, drained each frame under a byte and a packet budget. A packet the transport refuses stays at the head and is
/// tried again next frame, so order holds; after <see cref="MaxFailedDrains"/> refused frames in a row the queue is
/// dropped (the receiver then sees a cut transfer, a request times out and a subscription resyncs).
/// </summary>
internal sealed class ExchangeOutbox
{
    internal const int MaxFailedDrains = 300;

    private readonly Queue<byte[]> _queue = new();
    private readonly ExchangeBucket _bytes;
    private readonly ExchangeBucket _packets;
    private long _queuedBytes;
    private uint _nextTransfer = 1;
    private int _failedDrains;

    internal ExchangeOutbox(int maxBytes, double bytesPerSecond, double burstBytes, double packetsPerSecond, double burstPackets, double now)
    {
        MaxBytes = maxBytes;
        _bytes = new ExchangeBucket(burstBytes, bytesPerSecond, now);
        _packets = new ExchangeBucket(burstPackets, packetsPerSecond, now);
    }

    internal int MaxBytes { get; }
    internal int Count => _queue.Count;
    internal long QueuedBytes => _queuedBytes;

    /// <summary>Split <paramref name="body"/> into packets and queue them all, or none when they do not fit.</summary>
    internal bool Enqueue(byte[] body)
    {
        if (body == null || body.Length > ExchangeLimits.MaxBodyBytes + ExchangeBody.HeaderBytes + ExchangeBody.MaxTextBytes) return false;
        int packets = ExchangeChunks.CountFor(body.Length);
        long bytes = body.Length + (long)packets * ExchangeChunks.HeaderBytes;
        if (_queuedBytes + bytes > MaxBytes) return false;
        foreach (var p in ExchangeChunks.Split(_nextTransfer++, body)) _queue.Enqueue(p);
        if (_nextTransfer == 0) _nextTransfer = 1;
        _queuedBytes += bytes;
        return true;
    }

    /// <summary>
    /// Send what the budgets allow. Returns the packets sent, or -1 when the queue was dropped after
    /// <see cref="MaxFailedDrains"/> refused frames.
    /// </summary>
    internal int Drain(double now, Func<byte[], bool> send)
    {
        int sent = 0;
        while (_queue.Count > 0)
        {
            var p = _queue.Peek();
            if (!_packets.Has(now, 1) || !_bytes.Has(now, p.Length)) break;
            bool ok;
            try { ok = send(p); }
            catch { ok = false; }
            if (!ok)
            {
                if (sent == 0 && ++_failedDrains >= MaxFailedDrains)
                {
                    Clear();
                    return -1;
                }

                return sent;
            }

            _failedDrains = 0;
            _queue.Dequeue();
            _queuedBytes -= p.Length;
            _packets.Spend(1);
            _bytes.Spend(Math.Min(p.Length, _bytes.Burst));
            sent++;
        }

        return sent;
    }

    internal void Clear()
    {
        _queue.Clear();
        _queuedBytes = 0;
        _failedDrains = 0;
    }
}
