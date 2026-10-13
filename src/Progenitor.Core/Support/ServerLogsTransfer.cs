using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Progenitor.Support;

/// <summary>Why a server did not send its logs (the wire's refusal code).</summary>
internal enum ServerLogsRefusal : byte
{
    None = 0,

    /// <summary>The requester is not an admin of this server (admin list, <c>adminauth</c> or the private game's host).</summary>
    NotAdmin = 1,

    /// <summary>A transfer for this admin is already running.</summary>
    Busy = 2,

    /// <summary>A request came sooner than <see cref="ServerLogsServer.MinSecondsBetweenRequests"/> after the last one.</summary>
    RateLimited = 3,

    /// <summary>The server could not build its bundle.</summary>
    Failed = 4,

    /// <summary>The server's bundle came out larger than <see cref="ServerLogsWire.MaxBundleBytes"/>.</summary>
    TooLarge = 5,

    /// <summary>The transfer was dropped (the admin stopped acknowledging, or the server is unloading).</summary>
    Cancelled = 6,
}

/// <summary>One decoded <c>'PrSL'</c> packet.</summary>
internal sealed class ServerLogsPacket
{
    public byte Kind { get; set; }
    public int TransferId { get; set; }

    /// <summary>Chunk: its index. Ack: the number of chunks received in order.</summary>
    public int Index { get; set; }

    /// <summary>Start: the zip's length in bytes.</summary>
    public int Total { get; set; }

    /// <summary>Start: the chunk count.</summary>
    public int Chunks { get; set; }

    /// <summary>Start: the zip's SHA-256.</summary>
    public byte[] Sha256 { get; set; } = Array.Empty<byte>();

    /// <summary>Chunk: its bytes.</summary>
    public byte[] Body { get; set; } = Array.Empty<byte>();

    public ServerLogsRefusal Refusal { get; set; }

    /// <summary>Refuse: the server's words (already redacted by the server).</summary>
    public string Message { get; set; } = "";
}

/// <summary>
/// SUPPORT1 remote server logs: the <c>'PrSL'</c> channel (<see cref="EventId"/>), both directions over Progenitor's
/// custom events, which carry one payload of at most <see cref="MaxPacketBytes"/> per packet (the same 15 KiB
/// <c>CustomEvents.MaxPayloadBytes</c> every Core channel uses). Every packet starts with a version byte, a kind byte and
/// the transfer id (int32, little-endian):
/// <list type="bullet">
/// <item>client → server: <b>Request</b> (kind 1), <b>Ack</b> (kind 2, + int32 chunks received in order), <b>Cancel</b> (kind 3);</item>
/// <item>server → client: <b>Accepted</b> (kind 10: the server is building its bundle), <b>Start</b> (kind 11, + int32
/// length, int32 chunk count, 32-byte SHA-256), <b>Chunk</b> (kind 12, + int32 index, then up to <see cref="MaxChunkBody"/>
/// bytes), <b>Refuse</b> (kind 13, + refusal code byte, then a UTF-8 reason of at most <see cref="MaxMessageBytes"/>).</item>
/// </list>
/// Game-free.
/// </summary>
internal static class ServerLogsWire
{
    /// <summary><c>'PrSL'</c>. Inside the reserved <c>0x5072____</c> range, so only Core may register or send on it.</summary>
    internal const int EventId = 0x5072534C;

    internal const byte Version = 1;
    internal const byte KindRequest = 1;
    internal const byte KindAck = 2;
    internal const byte KindCancel = 3;
    internal const byte KindAccepted = 10;
    internal const byte KindStart = 11;
    internal const byte KindChunk = 12;
    internal const byte KindRefuse = 13;

    /// <summary>The largest custom-event payload (<c>CustomEvents.MaxPayloadBytes</c>, 15 KiB under the game's 16 KiB packet buffer).</summary>
    internal const int MaxPacketBytes = 15 * 1024;

    internal const int HeaderBytes = 6;
    internal const int ChunkHeaderBytes = HeaderBytes + 4;
    internal const int MaxChunkBody = MaxPacketBytes - ChunkHeaderBytes;
    internal const int MaxMessageBytes = 400;

    /// <summary>The largest server bundle sent (decided 2026-10-10: 20 MB; the server halves its per-log cap to fit).</summary>
    internal const int MaxBundleBytes = 20 * 1024 * 1024;

    /// <summary>The most chunks a bundle of <see cref="MaxBundleBytes"/> needs.</summary>
    internal static int MaxChunks => (MaxBundleBytes + MaxChunkBody - 1) / MaxChunkBody;

    internal static int ChunkCount(int length) => Math.Max(1, (length + MaxChunkBody - 1) / MaxChunkBody);

    internal static byte[] EncodeRequest(int id) => Head(KindRequest, id, 0);
    internal static byte[] EncodeCancel(int id) => Head(KindCancel, id, 0);
    internal static byte[] EncodeAccepted(int id) => Head(KindAccepted, id, 0);

    internal static byte[] EncodeAck(int id, int received)
    {
        var p = Head(KindAck, id, 4);
        WriteInt(p, HeaderBytes, received);
        return p;
    }

    internal static byte[] EncodeStart(int id, int total, int chunks, byte[] sha256)
    {
        if (sha256 == null || sha256.Length != 32) throw new ArgumentException("A SHA-256 is 32 bytes.", nameof(sha256));
        var p = Head(KindStart, id, 8 + 32);
        WriteInt(p, HeaderBytes, total);
        WriteInt(p, HeaderBytes + 4, chunks);
        Buffer.BlockCopy(sha256, 0, p, HeaderBytes + 8, 32);
        return p;
    }

    /// <summary>Chunk <paramref name="index"/> of <paramref name="zip"/>.</summary>
    internal static byte[] EncodeChunk(int id, int index, byte[] zip)
    {
        int start = index * MaxChunkBody;
        int len = Math.Max(0, Math.Min(MaxChunkBody, zip.Length - start));
        var p = Head(KindChunk, id, 4 + len);
        WriteInt(p, HeaderBytes, index);
        if (len > 0) Buffer.BlockCopy(zip, start, p, ChunkHeaderBytes, len);
        return p;
    }

    internal static byte[] EncodeRefuse(int id, ServerLogsRefusal refusal, string? message)
    {
        byte[] text = Utf8Cut(message ?? "", MaxMessageBytes);
        var p = Head(KindRefuse, id, 1 + text.Length);
        p[HeaderBytes] = (byte)refusal;
        Buffer.BlockCopy(text, 0, p, HeaderBytes + 1, text.Length);
        return p;
    }

    /// <summary>Decode one packet. False for anything not of this shape (wrong version, unknown kind, wrong length, over the caps).</summary>
    internal static bool TryDecode(ReadOnlySpan<byte> packet, out ServerLogsPacket decoded)
    {
        decoded = new ServerLogsPacket();
        if (packet.Length < HeaderBytes || packet.Length > MaxPacketBytes || packet[0] != Version) return false;
        decoded.Kind = packet[1];
        decoded.TransferId = ReadInt(packet, 2);
        var rest = packet.Slice(HeaderBytes);
        switch (decoded.Kind)
        {
            case KindRequest:
            case KindCancel:
            case KindAccepted:
                return rest.Length == 0;
            case KindAck:
                if (rest.Length != 4) return false;
                decoded.Index = ReadInt(rest, 0);
                return decoded.Index >= 0;
            case KindStart:
                if (rest.Length != 40) return false;
                decoded.Total = ReadInt(rest, 0);
                decoded.Chunks = ReadInt(rest, 4);
                decoded.Sha256 = rest.Slice(8, 32).ToArray();
                return decoded.Total >= 0 && decoded.Chunks >= 1;
            case KindChunk:
                if (rest.Length < 4) return false;
                decoded.Index = ReadInt(rest, 0);
                decoded.Body = rest.Slice(4).ToArray();
                return decoded.Index >= 0;
            case KindRefuse:
                if (rest.Length < 1 || rest.Length > 1 + MaxMessageBytes) return false;
                decoded.Refusal = (ServerLogsRefusal)rest[0];
                try { decoded.Message = new UTF8Encoding(false, true).GetString(rest.Slice(1).ToArray()); }
                catch (DecoderFallbackException) { return false; }
                return true;
            default:
                return false;
        }
    }

    internal static byte[] Sha256Of(byte[] data)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }

    /// <summary>The words a refusal code reads as when the server sent none.</summary>
    internal static string Describe(ServerLogsRefusal refusal) => refusal switch
    {
        ServerLogsRefusal.NotAdmin => "you are not an admin of this server",
        ServerLogsRefusal.Busy => "the server is already sending you its logs",
        ServerLogsRefusal.RateLimited => "logs were asked for moments ago; try again in a few seconds",
        ServerLogsRefusal.Failed => "the server could not collect its logs",
        ServerLogsRefusal.TooLarge => "the server's logs were larger than 20 MB",
        ServerLogsRefusal.Cancelled => "the server stopped the transfer",
        _ => "the server refused",
    };

    private static byte[] Head(byte kind, int id, int extra)
    {
        var p = new byte[HeaderBytes + extra];
        p[0] = Version;
        p[1] = kind;
        WriteInt(p, 2, id);
        return p;
    }

    private static void WriteInt(byte[] p, int at, int value)
    {
        p[at] = (byte)value;
        p[at + 1] = (byte)(value >> 8);
        p[at + 2] = (byte)(value >> 16);
        p[at + 3] = (byte)(value >> 24);
    }

    private static int ReadInt(ReadOnlySpan<byte> p, int at) => p[at] | (p[at + 1] << 8) | (p[at + 2] << 16) | (p[at + 3] << 24);

    private static byte[] Utf8Cut(string text, int max)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= max) return bytes;
        int n = max;
        while (n > 0 && (bytes[n] & 0xC0) == 0x80) n--; // never split a character
        var cut = new byte[n];
        Buffer.BlockCopy(bytes, 0, cut, 0, n);
        return cut;
    }
}

/// <summary>
/// SUPPORT1 remote server logs, server side: one transfer per admin, paced and windowed. Game-free: the live half
/// (<c>ServerLogsLive</c>) feeds it packets and the clock, builds the bundle (and its SHA-256) off the main thread, and
/// gives <see cref="Pump"/> a send function that says whether the transport took the packet.
///
/// <para><b>Pacing.</b> <see cref="Pump"/> runs every <see cref="PumpSeconds"/> and sends at most
/// <see cref="PacketsPerPump"/> packets per transfer: 20 packets a second, about 300 KB/s. That is the same envelope
/// Progenitor's server receive gate allows one connection in the other direction (<c>ReceiveGate.DefaultPerSecond</c>,
/// 20 packets of up to 15 KiB a second), so one admin's transfer never puts more on the reliable channel than the framework
/// already treats as a sustainable per-connection rate; a typical 1 to 3 MB bundle takes 3 to 10 seconds, 20 MB about
/// 70. At most <see cref="WindowChunks"/> chunks are sent ahead of the client's last acknowledgement (about 600 KB), so a
/// client that stops reading does not make the server queue the whole bundle.</para>
///
/// <para><b>Loss.</b> A packet the transport refuses is not counted as sent and goes again on the next pump. When the
/// window is full (or everything was sent) and no acknowledgement advanced for <see cref="ResendSeconds"/>, sending starts
/// again from the last acknowledged chunk (go-back-N), so one lost chunk or one lost acknowledgement recovers; the client
/// ignores chunks it already has.</para>
///
/// <para><b>Refusal log lines</b> are rate-limited per sender like the receive gate's drop lines: every refusal is answered, but at most
/// one log line per sender per <see cref="RefusalWindowSeconds"/> is written; the next line counts the ones held back.</para>
/// </summary>
internal sealed class ServerLogsServer
{
    internal const double PumpSeconds = 0.1;
    internal const int PacketsPerPump = 2;
    internal const int WindowChunks = 40;

    /// <summary>No acknowledgement for this long with the window full: resend from the last acknowledged chunk.</summary>
    internal const double ResendSeconds = 2;

    /// <summary>A transfer with no acknowledged progress for this long is dropped.</summary>
    internal const double StallSeconds = 30;

    /// <summary>A building bundle that takes longer than this is dropped.</summary>
    internal const double BuildSeconds = 120;

    internal const double MinSecondsBetweenRequests = 10;

    /// <summary>At most one refusal reply and log line per sender in this many seconds.</summary>
    internal const double RefusalWindowSeconds = 60;

    internal sealed class Transfer
    {
        public ulong Admin;
        public int UserIndex;
        public int Id;
        public byte[]? Zip;
        public byte[] Sha = Array.Empty<byte>();
        public int Chunks;
        public int NextToSend;
        public int Acked;
        public int HighestSent;
        public bool StartSent;
        public double LastProgress;
        public double LastAckOrRewind;
        public double Began;
        public int Rewinds;
    }

    private sealed class RefusalLimit
    {
        public double Since;
        public int Suppressed;
    }

    private readonly Dictionary<ulong, Transfer> _active = new();
    private readonly Dictionary<ulong, double> _lastRequest = new();
    private readonly Dictionary<ulong, RefusalLimit> _refusals = new();
    private readonly object _gate = new();

    /// <summary>One line per request and per outcome (the live half writes it to the server log).</summary>
    public Action<string>? Log { get; set; }

    public int ActiveCount
    {
        get { lock (_gate) return _active.Count; }
    }

    /// <summary>The admin as a log names them: the last four digits of the SteamID only.</summary>
    internal static string Who(ulong steamId)
    {
        string s = steamId.ToString(CultureInfo.InvariantCulture);
        return "admin …" + (s.Length > 4 ? s.Substring(s.Length - 4) : s);
    }

    /// <summary>
    /// A request. Returns the packet to send back: Accepted (and the caller builds the bundle and calls
    /// <see cref="Ready"/> or <see cref="Fail"/>), or a Refuse (its log line held back to one per sender a minute).
    /// </summary>
    public byte[] Begin(ulong admin, int userIndex, int id, bool isAdmin, double now)
    {
        lock (_gate)
        {
            ServerLogsRefusal refusal = ServerLogsRefusal.None;
            if (!isAdmin) refusal = ServerLogsRefusal.NotAdmin;
            else if (_active.ContainsKey(admin)) refusal = ServerLogsRefusal.Busy;
            else if (_lastRequest.TryGetValue(admin, out double last) && now - last < MinSecondsBetweenRequests) refusal = ServerLogsRefusal.RateLimited;

            if (refusal != ServerLogsRefusal.None)
            {
                // The reply always goes (the receive gate already limits the packets); only the log line is held back.
                if (_refusals.TryGetValue(admin, out var limit) && now - limit.Since < RefusalWindowSeconds)
                {
                    limit.Suppressed++;
                    return ServerLogsWire.EncodeRefuse(id, refusal, ServerLogsWire.Describe(refusal));
                }
                int held = limit?.Suppressed ?? 0;
                if (_refusals.Count > 1024) _refusals.Clear();
                _refusals[admin] = new RefusalLimit { Since = now };
                Log?.Invoke("Support: server logs for " + Who(admin) + " refused: " + refusal
                    + (held > 0 ? " (" + held.ToString(CultureInfo.InvariantCulture) + " more refused since the last report)" : ""));
                return ServerLogsWire.EncodeRefuse(id, refusal, ServerLogsWire.Describe(refusal));
            }

            _lastRequest[admin] = now;
            _active[admin] = new Transfer { Admin = admin, UserIndex = userIndex, Id = id, Began = now, LastProgress = now, LastAckOrRewind = now };
            Log?.Invoke("Support: server logs for " + Who(admin) + " requested; building the bundle.");
            return ServerLogsWire.EncodeAccepted(id);
        }
    }

    /// <summary>
    /// The bundle is built, with its SHA-256 computed off the main thread (<paramref name="sha256"/>; null computes it here,
    /// for tests). It is sent from the next <see cref="Pump"/>. Returns a refusal packet when it is over the cap.
    /// </summary>
    public byte[]? Ready(ulong admin, int id, byte[] zip, double now, byte[]? sha256 = null)
    {
        if (zip != null && zip.Length <= ServerLogsWire.MaxBundleBytes && (sha256 == null || sha256.Length != 32))
            sha256 = ServerLogsWire.Sha256Of(zip);
        lock (_gate)
        {
            if (!_active.TryGetValue(admin, out var t) || t.Id != id) return null; // cancelled meanwhile
            if (zip == null || zip.Length > ServerLogsWire.MaxBundleBytes)
            {
                _active.Remove(admin);
                Log?.Invoke("Support: server logs for " + Who(admin) + " not sent: the bundle is " + (zip?.Length ?? 0) + " bytes, over the cap.");
                return ServerLogsWire.EncodeRefuse(id, ServerLogsRefusal.TooLarge, ServerLogsWire.Describe(ServerLogsRefusal.TooLarge));
            }
            t.Zip = zip;
            t.Sha = sha256!;
            t.Chunks = ServerLogsWire.ChunkCount(zip.Length);
            t.LastProgress = now;
            t.LastAckOrRewind = now;
            return null;
        }
    }

    /// <summary>Building failed: the refusal to send (the reason is the server's, already redacted).</summary>
    public byte[]? Fail(ulong admin, int id, string reason)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(admin, out var t) || t.Id != id) return null;
            _active.Remove(admin);
            Log?.Invoke("Support: server logs for " + Who(admin) + " failed: " + reason);
            return ServerLogsWire.EncodeRefuse(id, ServerLogsRefusal.Failed, ServerLogsWire.Describe(ServerLogsRefusal.Failed) + ": " + reason);
        }
    }

    /// <summary>An acknowledgement: progress, and the transfer is complete on the first one that covers every chunk.</summary>
    public void Ack(ulong admin, int id, int received, double now)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(admin, out var t) || t.Id != id || t.Zip == null) return;
            int clamped = Math.Min(Math.Max(received, 0), Math.Max(t.HighestSent, t.NextToSend));
            if (clamped > t.Acked)
            {
                t.Acked = clamped;
                t.LastProgress = now;
                t.LastAckOrRewind = now; // only progress counts: a repeated acknowledgement of a gap must still let the resend happen
                if (t.NextToSend < t.Acked) t.NextToSend = t.Acked;
            }
            if (t.Acked >= t.Chunks)
            {
                _active.Remove(admin);
                Log?.Invoke("Support: server logs for " + Who(admin) + " sent: " + t.Zip.Length + " bytes in " + t.Chunks + " chunks, "
                    + (now - t.Began).ToString("0.0", CultureInfo.InvariantCulture) + " s"
                    + (t.Rewinds > 0 ? ", " + t.Rewinds.ToString(CultureInfo.InvariantCulture) + " resend(s)" : "") + ".");
            }
        }
    }

    public void Cancel(ulong admin, int id, string why)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(admin, out var t) || t.Id != id) return;
            _active.Remove(admin);
            Log?.Invoke("Support: server logs for " + Who(admin) + " cancelled: " + why);
        }
    }

    /// <summary>
    /// One pacing step: for each transfer, drop it when its admin left (<paramref name="connected"/> false) or it stalled,
    /// else send Start and then chunks through <paramref name="send"/> (user index, packet; false when the transport did
    /// not take it), at most <see cref="PacketsPerPump"/> and never more than <see cref="WindowChunks"/> ahead of the last
    /// acknowledgement. A refused packet is not counted and goes again next time. Returns how many packets were sent.
    /// </summary>
    public int Pump(double now, Func<ulong, int, bool> connected, Func<int, byte[], bool> send)
    {
        int sent = 0;
        lock (_gate)
        {
            var drop = new List<(ulong, string)>();
            foreach (var t in _active.Values)
            {
                bool here;
                try { here = connected(t.Admin, t.UserIndex); }
                catch { here = false; }
                if (!here) { drop.Add((t.Admin, DisconnectedReason)); continue; }
                if (t.Zip == null)
                {
                    if (now - t.Began > BuildSeconds) drop.Add((t.Admin, "building took longer than " + BuildSeconds + " s"));
                    continue;
                }
                if (now - t.LastProgress > StallSeconds) { drop.Add((t.Admin, "no acknowledgement for " + StallSeconds + " s")); continue; }

                // Go-back-N: nothing more may go out (window full, or all sent) and the client has said nothing for a while.
                bool blocked = t.NextToSend >= t.Chunks || t.NextToSend >= t.Acked + WindowChunks;
                if (blocked && t.Acked < t.Chunks && now - t.LastAckOrRewind > ResendSeconds)
                {
                    t.NextToSend = t.Acked;
                    t.LastAckOrRewind = now;
                    t.Rewinds++;
                    if (t.Acked == 0) t.StartSent = false; // the Start itself may be what was lost
                }

                int budget = PacketsPerPump;
                if (!t.StartSent)
                {
                    if (!TrySend(send, t.UserIndex, ServerLogsWire.EncodeStart(t.Id, t.Zip.Length, t.Chunks, t.Sha))) continue;
                    t.StartSent = true;
                    sent++;
                    budget--;
                }
                while (budget > 0 && t.NextToSend < t.Chunks && t.NextToSend < t.Acked + WindowChunks)
                {
                    if (!TrySend(send, t.UserIndex, ServerLogsWire.EncodeChunk(t.Id, t.NextToSend, t.Zip))) break;
                    t.NextToSend++;
                    if (t.NextToSend > t.HighestSent) t.HighestSent = t.NextToSend;
                    sent++;
                    budget--;
                }
            }
            foreach (var (admin, why) in drop)
            {
                if (_active.TryGetValue(admin, out var gone))
                    // Best effort: tell the client, so it stops waiting and says why (not for a disconnected admin).
                    if (why != DisconnectedReason) TrySend(send, gone.UserIndex, CancelledPacket(gone.Id, why));
                _active.Remove(admin);
                Log?.Invoke("Support: server logs for " + Who(admin) + " dropped: " + why + ".");
            }
        }
        return sent;
    }

    private static bool TrySend(Func<int, byte[], bool> send, int userIndex, byte[] packet)
    {
        try { return send(userIndex, packet); }
        catch { return false; }
    }

    /// <summary>Core unload: drop every transfer (nothing more is sent).</summary>
    private const string DisconnectedReason = "the admin disconnected";

    /// <summary>The Refuse(Cancelled) a dropped transfer sends its client: <c>the server stopped the transfer: &lt;why&gt;</c>.</summary>
    internal static byte[] CancelledPacket(int id, string why) =>
        ServerLogsWire.EncodeRefuse(id, ServerLogsRefusal.Cancelled, ServerLogsWire.Describe(ServerLogsRefusal.Cancelled) + ": " + why);

    /// <summary>
    /// Core unload: drop every transfer; <paramref name="send"/> (best effort) tells each client the transfer stopped.
    /// </summary>
    public void Clear(Func<int, byte[], bool>? send = null)
    {
        lock (_gate)
        {
            foreach (var t in _active.Values)
            {
                if (send != null) TrySend(send, t.UserIndex, CancelledPacket(t.Id, "Progenitor is unloading on the server"));
                Log?.Invoke("Support: server logs for " + Who(t.Admin) + " dropped: Progenitor is unloading.");
            }
            _active.Clear();
        }
    }
}

/// <summary>How a fetch of the server's logs ended.</summary>
internal sealed class ServerLogsOutcome
{
    /// <summary>Every chunk arrived with the announced length. The SHA-256 is checked by <see cref="Verify"/>, off the main thread.</summary>
    public bool Ok { get; set; }
    public byte[]? Zip { get; set; }

    /// <summary>The SHA-256 the server announced for <see cref="Zip"/>.</summary>
    public byte[] ExpectedSha256 { get; set; } = Array.Empty<byte>();

    /// <summary>Why the server part is missing (empty when <see cref="Ok"/>).</summary>
    public string Reason { get; set; } = "";

    /// <summary>The fetch ended because Progenitor is unloading: nothing more may be started for it.</summary>
    public bool Unloading { get; set; }

    /// <summary>
    /// Worker thread: true when <see cref="Zip"/> matches <see cref="ExpectedSha256"/>. On a mismatch the zip is dropped and
    /// <see cref="Reason"/> says so.
    /// </summary>
    public bool Verify()
    {
        if (!Ok || Zip == null) return false;
        if (ExpectedSha256.Length == 32 && FixedEquals(ServerLogsWire.Sha256Of(Zip), ExpectedSha256)) return true;
        Ok = false;
        Zip = null;
        Reason = "the server's logs failed their SHA-256 check";
        return false;
    }

    private static bool FixedEquals(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}

/// <summary>
/// SUPPORT1 remote server logs, client side: one fetch at a time. Request, then Accepted, Start (length, chunk count,
/// SHA-256), the chunks, an acknowledgement every <see cref="AckEvery"/> chunks, again after <see cref="ReAckSeconds"/>
/// without a new chunk, and <see cref="FinalAcks"/> times at the end (<see cref="FinalAckSeconds"/> apart), so a lost
/// acknowledgement never stalls the server. The SHA-256 is checked off the main thread
/// (<see cref="ServerLogsOutcome.Verify"/>). Every way it can end (refusal, timeout, stall, disconnect, cancel, a bad
/// packet) completes it exactly once with a reason. Game-free: the live half sends what it returns and calls
/// <see cref="Tick"/>.
/// </summary>
internal sealed class ServerLogsClient
{
    internal const int AckEvery = 8;
    internal const double ReAckSeconds = 1;
    internal const int FinalAcks = 3;
    internal const double FinalAckSeconds = 0.5;

    /// <summary>No answer to the request: an older server, or one that dropped it.</summary>
    internal const double AnswerSeconds = 15;

    /// <summary>Accepted, but no Start: the server takes too long to build.</summary>
    internal const double BuildSeconds = 150;

    /// <summary>No chunk for this long while receiving.</summary>
    internal const double StallSeconds = 30;

    private enum Phase { Idle, Requested, Building, Receiving }

    private readonly object _gate = new();
    private Phase _phase = Phase.Idle;
    private int _id;
    private int _nextId = 1;
    private double _since;
    private double _lastAck;
    private byte[]? _buffer;
    private bool[] _have = Array.Empty<bool>();
    private int _total;
    private int _chunks;
    private int _contiguous;
    private int _received;
    private int _sinceAck;
    private long _bytes;
    private byte[] _sha = Array.Empty<byte>();
    private Action<ServerLogsOutcome>? _done;
    private byte[]? _finalAck;
    private int _finalAcksLeft;
    private double _lastFinalAck;

    /// <summary>True while a fetch runs.</summary>
    public bool Busy
    {
        get { lock (_gate) return _phase != Phase.Idle; }
    }

    /// <summary>True while a fetch runs or the final acknowledgement still has repeats to send (the live half keeps ticking).</summary>
    public bool NeedsTicks
    {
        get { lock (_gate) return _phase != Phase.Idle || _finalAcksLeft > 0; }
    }

    /// <summary>The progress line: <c>Getting server logs... 1.2 / 3.4 MB</c>, or the waiting line before the first chunk.</summary>
    public string ProgressLine
    {
        get
        {
            lock (_gate)
            {
                return _phase switch
                {
                    Phase.Requested => "Asking the server for its logs...",
                    Phase.Building => "The server is collecting its logs...",
                    Phase.Receiving => "Getting server logs... " + Mb(_bytes) + " / " + Mb(_total) + " MB",
                    _ => "",
                };
            }
        }
    }

    private static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Start a fetch; returns the Request packet, or null when one already runs.</summary>
    public byte[]? Begin(double now, Action<ServerLogsOutcome> done)
    {
        lock (_gate)
        {
            if (_phase != Phase.Idle) return null;
            _id = _nextId++;
            _phase = Phase.Requested;
            _since = now;
            _done = done;
            _buffer = null;
            _bytes = 0;
            _finalAck = null;
            _finalAcksLeft = 0;
            return ServerLogsWire.EncodeRequest(_id);
        }
    }

    /// <summary>A packet from the server. Returns a packet to send back (an acknowledgement or a cancel), or null.</summary>
    public byte[]? Accept(ReadOnlySpan<byte> packet, double now)
    {
        if (!ServerLogsWire.TryDecode(packet, out var p)) return null;
        Action? finish = null;
        byte[]? reply = null;
        lock (_gate)
        {
            if (_phase == Phase.Idle || p.TransferId != _id) return null;
            switch (p.Kind)
            {
                case ServerLogsWire.KindAccepted when _phase == Phase.Requested:
                    _phase = Phase.Building;
                    _since = now;
                    break;
                case ServerLogsWire.KindRefuse:
                    finish = End(false, null, string.IsNullOrWhiteSpace(p.Message) ? ServerLogsWire.Describe(p.Refusal) : p.Message);
                    break;
                case ServerLogsWire.KindStart when _phase is Phase.Requested or Phase.Building:
                    if (p.Total > ServerLogsWire.MaxBundleBytes || p.Chunks != ServerLogsWire.ChunkCount(p.Total))
                    {
                        reply = ServerLogsWire.EncodeCancel(_id);
                        finish = End(false, null, "the server announced a bundle of " + p.Total + " bytes in " + p.Chunks + " chunks, which breaks the limits");
                        break;
                    }
                    _total = p.Total;
                    _chunks = p.Chunks;
                    _sha = p.Sha256;
                    _buffer = new byte[p.Total];
                    _have = new bool[p.Chunks];
                    _contiguous = _received = _sinceAck = 0;
                    _phase = Phase.Receiving;
                    _since = now;
                    _lastAck = now;
                    break;
                case ServerLogsWire.KindChunk when _phase == Phase.Receiving:
                    if (p.Index >= _chunks || _have[p.Index]) break; // a resent chunk this client already has
                    int start = p.Index * ServerLogsWire.MaxChunkBody;
                    int expected = Math.Min(ServerLogsWire.MaxChunkBody, _total - start);
                    if (p.Body.Length != expected)
                    {
                        reply = ServerLogsWire.EncodeCancel(_id);
                        finish = End(false, null, "chunk " + p.Index + " has " + p.Body.Length + " bytes, expected " + expected);
                        break;
                    }
                    Buffer.BlockCopy(p.Body, 0, _buffer!, start, p.Body.Length);
                    _have[p.Index] = true;
                    _received++;
                    _bytes += p.Body.Length;
                    _since = now;
                    while (_contiguous < _chunks && _have[_contiguous]) _contiguous++;
                    _sinceAck++;
                    if (_received == _chunks)
                    {
                        reply = ServerLogsWire.EncodeAck(_id, _contiguous);
                        // Repeated from Tick, so a lost final acknowledgement still completes the server's side.
                        _finalAck = reply;
                        _finalAcksLeft = FinalAcks - 1;
                        _lastFinalAck = now;
                        finish = End(true, _buffer, "");
                    }
                    else if (_sinceAck >= AckEvery)
                    {
                        _sinceAck = 0;
                        _lastAck = now;
                        reply = ServerLogsWire.EncodeAck(_id, _contiguous);
                    }
                    break;
            }
        }
        finish?.Invoke();
        return reply;
    }

    /// <summary>
    /// Timeouts, the disconnect check (<paramref name="connected"/> false while a fetch runs ends it), the repeated
    /// acknowledgement while receiving and the repeated final one. Returns a packet to send (an Ack or a Cancel) or null.
    /// </summary>
    public byte[]? Tick(double now, bool connected)
    {
        Action? finish = null;
        byte[]? reply = null;
        lock (_gate)
        {
            if (_phase == Phase.Idle)
            {
                if (_finalAcksLeft > 0 && connected && now - _lastFinalAck >= FinalAckSeconds)
                {
                    _finalAcksLeft--;
                    _lastFinalAck = now;
                    return _finalAck;
                }
                if (!connected) _finalAcksLeft = 0;
                return null;
            }
            double waited = now - _since;
            if (!connected) finish = End(false, null, "this client left the server");
            else if (_phase == Phase.Requested && waited > AnswerSeconds)
                finish = End(false, null, "the server did not answer (its Progenitor may be too old to send logs)");
            else if (_phase == Phase.Building && waited > BuildSeconds)
            {
                reply = ServerLogsWire.EncodeCancel(_id);
                finish = End(false, null, "the server took longer than " + BuildSeconds + " s to collect its logs");
            }
            else if (_phase == Phase.Receiving && waited > StallSeconds)
            {
                reply = ServerLogsWire.EncodeCancel(_id);
                finish = End(false, null, "the transfer stalled for " + StallSeconds + " s");
            }
            else if (_phase == Phase.Receiving && now - _since >= ReAckSeconds && now - _lastAck >= ReAckSeconds)
            {
                // No new chunk for a second: say again what arrived, in case the last acknowledgement was lost.
                _lastAck = now;
                reply = ServerLogsWire.EncodeAck(_id, _contiguous);
            }
        }
        finish?.Invoke();
        return reply;
    }

    /// <summary>
    /// Cancel a running fetch. With <paramref name="unloading"/> the outcome says so, and nothing may be started for it.
    /// Returns the Cancel packet, or null when nothing runs.
    /// </summary>
    public byte[]? Cancel(string why, bool unloading = false)
    {
        Action? finish;
        int id;
        lock (_gate)
        {
            _finalAcksLeft = 0;
            if (_phase == Phase.Idle) return null;
            id = _id;
            finish = End(false, null, why, unloading);
        }
        finish?.Invoke();
        return ServerLogsWire.EncodeCancel(id);
    }

    /// <summary>Under the lock: back to idle, and the completion to run outside it.</summary>
    private Action? End(bool ok, byte[]? zip, string reason, bool unloading = false)
    {
        var done = _done;
        _done = null;
        _phase = Phase.Idle;
        _buffer = null;
        _have = Array.Empty<bool>();
        if (done == null) return null;
        var outcome = new ServerLogsOutcome { Ok = ok, Zip = ok ? zip : null, ExpectedSha256 = ok ? _sha : Array.Empty<byte>(), Reason = reason, Unloading = unloading };
        return () => done(outcome);
    }
}


/// <summary>SUPPORT1: whether the joined server declared <c>Handshake.Capability.ServerLogs</c> (noted at each join). Game-free.</summary>
internal static class ServerLogsCaps
{
    private static volatile bool _serverSends;

    internal static bool ServerSendsLogs => _serverSends;

    internal static void NoteServerSendsLogs(bool sends) => _serverSends = sends;
}
