using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Networking;

// Mod compatibility C2: the 'PrMs' envelope, the name table, the receive routing and the send gate. Game-free and
// compiled into the test suite; ModCompatNet.cs puts it on the wire.

/// <summary>
/// The <c>'PrMs'</c> envelope: <c>[envelope version u8][flags u8][mod hash u32le][message hash u32le][message version
/// u16le][min reader version u16le][payload length u16le][payload]</c>, a 16-byte header. Hashes are FNV-1a 32 of the
/// UTF-8 mod GUID (lowercased) and of the message name.
/// </summary>
internal static class ModMessageEnvelope
{
    /// <summary><c>'PrMs'</c>. Inside the reserved range, so only Core may register or send on it.</summary>
    internal const int EventId = 0x50724D73;

    internal const byte Version = 1;
    internal const int HeaderBytes = 16;

    /// <summary>The transport's single-packet limit (<c>CustomEvents.MaxPayloadBytes</c>, 15 KiB).</summary>
    internal const int MaxPacketBytes = 15 * 1024;

    /// <summary>Largest encoded message: one packet minus the header.</summary>
    public const int MaxPayloadBytes = MaxPacketBytes - HeaderBytes;

    internal const int MaxNameChars = 64;

    internal static uint Fnv1a(string text)
    {
        uint h = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(text ?? ""))
        {
            h ^= b;
            h *= 16777619;
        }

        return h;
    }

    internal static uint ModHash(string modGuid) => Fnv1a((modGuid ?? "").Trim().ToLowerInvariant());

    internal static uint MessageHash(string name) => Fnv1a(name ?? "");

    /// <summary>Message names follow the feature-name alphabet, up to 64 characters.</summary>
    internal static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name!.Length > MaxNameChars) return false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            bool alnum = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (i == 0 ? !alnum : !(alnum || c == '.' || c == '_' || c == '-')) return false;
        }

        return true;
    }

    internal static bool IsValidVersion(int v) => v >= 1 && v <= ushort.MaxValue;

    internal static byte[] Pack(uint modHash, uint messageHash, int version, int minReaderVersion, byte[] payload)
    {
        if (payload == null) throw new ArgumentNullException(nameof(payload));
        if (payload.Length > MaxPayloadBytes) throw new MessageTooLargeException("payload is " + payload.Length + " bytes; max is " + MaxPayloadBytes);
        var p = new byte[HeaderBytes + payload.Length];
        p[0] = Version;
        p[1] = 0;
        WriteU32(p, 2, modHash);
        WriteU32(p, 6, messageHash);
        WriteU16(p, 10, version);
        WriteU16(p, 12, minReaderVersion);
        WriteU16(p, 14, payload.Length);
        Buffer.BlockCopy(payload, 0, p, HeaderBytes, payload.Length);
        return p;
    }

    internal static bool TryUnpack(ReadOnlySpan<byte> packet, out uint modHash, out uint messageHash, out int version, out int minReader,
        out byte[] payload, out string? error)
    {
        modHash = messageHash = 0;
        version = minReader = 0;
        payload = Array.Empty<byte>();
        error = null;
        if (packet.Length < HeaderBytes || packet.Length > MaxPacketBytes)
        {
            error = "packet length " + packet.Length + " is outside the envelope";
            return false;
        }

        if (packet[0] != Version)
        {
            error = "envelope version " + packet[0] + " is not " + Version;
            return false;
        }

        modHash = ReadU32(packet, 2);
        messageHash = ReadU32(packet, 6);
        version = packet[10] | (packet[11] << 8);
        minReader = packet[12] | (packet[13] << 8);
        int len = packet[14] | (packet[15] << 8);
        if (len != packet.Length - HeaderBytes)
        {
            error = "payload length " + len + " does not match the packet";
            return false;
        }

        if (version < 1 || minReader < 1)
        {
            error = "message version 0";
            return false;
        }

        payload = packet.Slice(HeaderBytes, len).ToArray();
        return true;
    }

    private static void WriteU32(byte[] p, int at, uint v)
    {
        p[at] = (byte)v;
        p[at + 1] = (byte)(v >> 8);
        p[at + 2] = (byte)(v >> 16);
        p[at + 3] = (byte)(v >> 24);
    }

    private static void WriteU16(byte[] p, int at, int v)
    {
        p[at] = (byte)v;
        p[at + 1] = (byte)(v >> 8);
    }

    private static uint ReadU32(ReadOnlySpan<byte> p, int at) =>
        (uint)(p[at] | (p[at + 1] << 8) | (p[at + 2] << 16) | (p[at + 3] << 24));
}

/// <summary>What the receive side hands a handler: the decoded payload is the handler's job.</summary>
internal delegate void ModMessageDispatch(object? from, byte[] payload, MessageInfo info);

/// <summary>
/// The name table and the handlers for <c>'PrMs'</c>. A mod GUID and a message name each hash to 32 bits; a second
/// GUID (or, within a mod, a second name) with the same hash is refused with both names, so a hash on the wire always
/// means one thing in this process. Receive drops, with one line per mod or message per Core generation, a message for
/// a mod this process does not run, for a message it has no handler for, and one whose minimum reader version is above
/// the local handler's.
/// </summary>
internal sealed class ModMessageRouter
{
    private sealed class Handler
    {
        internal string Name = "";
        internal int ReaderVersion = 1;
        internal ModMessageDispatch Dispatch = null!;
        internal object Token = new();
    }

    private sealed class Mod
    {
        internal string Guid = "";
        internal readonly Dictionary<uint, string> Names = new();
        internal readonly Dictionary<uint, Handler> Handlers = new();

        /// <summary>The version and minimum reader version of the last send of each message (this generation).</summary>
        internal readonly Dictionary<uint, (int Version, int MinReader)> Sent = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<uint, Mod> _mods = new();
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);

    /// <summary>
    /// Claim <paramref name="name"/> for <paramref name="modGuid"/>: both hashes, refusing a collision. Idempotent for
    /// the same pair. Null on success, else the refusal line.
    /// </summary>
    internal string? Claim(string modGuid, string name, out uint modHash, out uint messageHash)
    {
        modHash = messageHash = 0;
        if (string.IsNullOrWhiteSpace(modGuid)) return "Messages: a mod GUID is required.";
        if (!ModMessageEnvelope.IsValidName(name))
            return "Messages: '" + name + "' is not a valid message name (1 to " + ModMessageEnvelope.MaxNameChars + " characters of [a-z0-9._-]).";
        string guid = modGuid.Trim();
        modHash = ModMessageEnvelope.ModHash(guid);
        messageHash = ModMessageEnvelope.MessageHash(name);
        lock (_gate)
        {
            if (_mods.TryGetValue(modHash, out var mod))
            {
                if (!string.Equals(mod.Guid, guid, StringComparison.OrdinalIgnoreCase))
                    return "Messages: refused " + guid + ": its id hash 0x" + modHash.ToString("x8", CultureInfo.InvariantCulture)
                           + " collides with " + mod.Guid + ". Rename one of the two mods' GUIDs to use ctx.Messages.";
            }
            else _mods[modHash] = mod = new Mod { Guid = guid };

            if (mod.Names.TryGetValue(messageHash, out var held))
            {
                if (!string.Equals(held, name, StringComparison.Ordinal))
                    return "Messages: refused " + guid + " message '" + name + "': its hash collides with '" + held + "'. Rename one of them.";
            }
            else mod.Names[messageHash] = name;
        }

        return null;
    }

    /// <summary>Register the handler for one message. Null on success, else the refusal line (a collision, or a second handler).</summary>
    internal string? Register(string modGuid, string name, int readerVersion, ModMessageDispatch dispatch, out object token)
    {
        token = new object();
        if (dispatch == null) throw new ArgumentNullException(nameof(dispatch));
        if (!ModMessageEnvelope.IsValidVersion(readerVersion)) return "Messages: reader version " + readerVersion + " is out of range (1 to 65535).";
        string? refusal = Claim(modGuid, name, out uint modHash, out uint messageHash);
        if (refusal != null) return refusal;
        lock (_gate)
        {
            var mod = _mods[modHash];
            if (mod.Handlers.ContainsKey(messageHash))
                return "Messages: " + mod.Guid + " already handles '" + name + "'; one handler per message.";
            var h = new Handler { Name = name, ReaderVersion = readerVersion, Dispatch = dispatch, Token = token };
            mod.Handlers[messageHash] = h;
        }

        return null;
    }

    /// <summary>Remove a handler if <paramref name="token"/> still owns it.</summary>
    internal bool Unregister(string modGuid, string name, object token)
    {
        uint modHash = ModMessageEnvelope.ModHash(modGuid ?? "");
        uint messageHash = ModMessageEnvelope.MessageHash(name ?? "");
        lock (_gate)
        {
            if (!_mods.TryGetValue(modHash, out var mod) || !mod.Handlers.TryGetValue(messageHash, out var h) || !ReferenceEquals(h.Token, token))
                return false;
            mod.Handlers.Remove(messageHash);
            return true;
        }
    }

    /// <summary>Forget a mod's names and handlers (its plugin unloaded).</summary>
    internal void ForgetMod(string modGuid)
    {
        uint modHash = ModMessageEnvelope.ModHash(modGuid ?? "");
        lock (_gate)
        {
            if (_mods.TryGetValue(modHash, out var mod) && string.Equals(mod.Guid, (modGuid ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                _mods.Remove(modHash);
        }
    }

    /// <summary>
    /// Record that <paramref name="modGuid"/> sent <paramref name="name"/> at <paramref name="version"/> (after a
    /// successful <see cref="Claim"/>), for the mod page's developer details. Never throws.
    /// </summary>
    internal void NoteSent(string modGuid, string name, int version, int minReader)
    {
        uint modHash = ModMessageEnvelope.ModHash(modGuid ?? "");
        uint messageHash = ModMessageEnvelope.MessageHash(name ?? "");
        lock (_gate)
        {
            if (_mods.TryGetValue(modHash, out var mod) && mod.Names.ContainsKey(messageHash))
                mod.Sent[messageHash] = (version, minReader);
        }
    }

    /// <summary>
    /// What this process knows about <paramref name="modGuid"/>'s messages this generation, sorted by name: each name
    /// it handles (with the newest version its handler reads) or has sent (with the version and minimum reader version
    /// of the last send). Empty for a mod that registered and sent nothing.
    /// </summary>
    internal IReadOnlyList<ModMessageSummary> Describe(string modGuid)
    {
        var list = new List<ModMessageSummary>();
        uint modHash = ModMessageEnvelope.ModHash(modGuid ?? "");
        lock (_gate)
        {
            if (!_mods.TryGetValue(modHash, out var mod) || !string.Equals(mod.Guid, (modGuid ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                return list;
            foreach (var kv in mod.Names)
            {
                mod.Handlers.TryGetValue(kv.Key, out var h);
                bool sent = mod.Sent.TryGetValue(kv.Key, out var s);
                if (h == null && !sent) continue;
                list.Add(new ModMessageSummary(kv.Value, h?.ReaderVersion ?? 0, sent ? s.Version : 0, sent ? s.MinReader : 0));
            }
        }

        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return list;
    }

    internal bool HandlesAny(string modGuid)
    {
        uint modHash = ModMessageEnvelope.ModHash(modGuid ?? "");
        lock (_gate) return _mods.TryGetValue(modHash, out var mod) && mod.Handlers.Count > 0;
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _mods.Clear();
            _logged.Clear();
            _suppressed = 0;
            _lastSummary = double.NegativeInfinity;
        }
    }

    /// <summary>A received message ready to run, or why it was dropped.</summary>
    internal sealed class Routed
    {
        internal string ModGuid = "";
        internal ModMessageDispatch Dispatch = null!;
        internal byte[] Payload = Array.Empty<byte>();
        internal MessageInfo Info = new();

        /// <summary>The envelope's minimum reader version (for a recording).</summary>
        internal int MinReader = 1;
    }

    /// <summary>
    /// Decide what to do with one <c>'PrMs'</c> packet. True with <paramref name="routed"/> when a handler should run;
    /// false when it was dropped (with <paramref name="log"/> when a line is due: once per reason and key per
    /// generation). <paramref name="senderLabel"/> names the sender in the line; <paramref name="senderModVersion"/>
    /// reads the sender's version of a mod from its feature table.
    /// </summary>
    internal bool TryRoute(ReadOnlySpan<byte> packet, string senderLabel, Func<string, string?>? senderModVersion, bool fromServer, int userIndex,
        out Routed? routed, out string? log)
    {
        routed = null;
        log = null;
        if (!ModMessageEnvelope.TryUnpack(packet, out uint modHash, out uint messageHash, out int version, out int minReader, out var payload, out var error))
        {
            log = Once("bad|" + senderLabel, "Messages: dropped a malformed message from " + senderLabel + ": " + error);
            return false;
        }

        Mod? mod;
        Handler? handler;
        string? knownName = null;
        lock (_gate)
        {
            _mods.TryGetValue(modHash, out mod);
            handler = null;
            if (mod != null)
            {
                mod.Handlers.TryGetValue(messageHash, out handler);
                mod.Names.TryGetValue(messageHash, out knownName);
            }
        }

        string hashText = "0x" + modHash.ToString("x8", CultureInfo.InvariantCulture);
        if (mod == null)
        {
            log = Once("mod|" + hashText, "Messages: dropped a message for mod " + hashText + " from " + senderLabel + ": no mod with that id runs here");
            return false;
        }

        if (handler == null)
        {
            string msgText = knownName ?? "0x" + messageHash.ToString("x8", CultureInfo.InvariantCulture);
            log = Once("msg|" + mod.Guid + "|" + msgText, "Messages: dropped " + mod.Guid + " " + msgText + " v" + version + " from " + senderLabel + ": no handler for it here");
            return false;
        }

        if (minReader > handler.ReaderVersion)
        {
            log = Once("min|" + mod.Guid + "|" + handler.Name,
                "Messages: dropped " + handler.Name + " v" + version + " from " + senderLabel + ": needs reader " + minReader + ", this mod reads " + handler.ReaderVersion);
            return false;
        }

        string? modVersion = null;
        try { modVersion = senderModVersion?.Invoke(mod.Guid); }
        catch { modVersion = null; }
        routed = new Routed
        {
            ModGuid = mod.Guid,
            Dispatch = handler.Dispatch,
            Payload = payload,
            MinReader = minReader,
            Info = new MessageInfo
            {
                Name = handler.Name,
                SenderVersion = version,
                SenderModVersion = modVersion,
                FromServer = fromServer,
                UserIndex = userIndex,
            },
        };
        return true;
    }

    /// <summary>Distinct keys <see cref="Once"/> remembers. Keys can carry sender-chosen hashes, so the set is bounded.</summary>
    internal const int MaxLoggedKeys = 512;

    /// <summary>After the set is full, at most one summary line per this many seconds.</summary>
    internal const double SuppressedSummarySeconds = 60;

    private int _suppressed;
    private double _lastSummary = double.NegativeInfinity;

    /// <summary>The clock for the suppressed-lines summary (seconds). Tests replace it.</summary>
    internal Func<double> Clock { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// The line once per key per router lifetime (one Core generation), else null. Once <see cref="MaxLoggedKeys"/>
    /// keys are remembered, a new key is not stored: it is counted, and at most once per
    /// <see cref="SuppressedSummarySeconds"/> one summary line is returned instead, so a peer sending random hashes
    /// costs neither memory nor a line per packet.
    /// </summary>
    internal string? Once(string key, string line)
    {
        lock (_gate)
        {
            if (_logged.Contains(key)) return null;
            if (_logged.Count < MaxLoggedKeys)
            {
                _logged.Add(key);
                return line;
            }

            _suppressed++;
            double now = Clock();
            if (now - _lastSummary < SuppressedSummarySeconds) return null;
            _lastSummary = now;
            int n = _suppressed;
            _suppressed = 0;
            return "Messages: " + n + " more drop line(s) not logged (the last: " + line + ")";
        }
    }
}

/// <summary>One message of a mod as this process knows it (<see cref="ModMessageRouter.Describe"/>).</summary>
internal readonly struct ModMessageSummary
{
    internal ModMessageSummary(string name, int readerVersion, int sentVersion, int sentMinReader)
    {
        Name = name ?? "";
        ReaderVersion = readerVersion;
        SentVersion = sentVersion;
        SentMinReader = sentMinReader;
    }

    internal string Name { get; }

    /// <summary>The newest version this process's handler reads; 0 when it has no handler for the message.</summary>
    internal int ReaderVersion { get; }

    /// <summary>The version of the last send this generation; 0 when it has not sent the message.</summary>
    internal int SentVersion { get; }

    /// <summary>The minimum reader version of the last send; 0 when it has not sent the message.</summary>
    internal int SentMinReader { get; }
}

/// <summary>What the gate knows about one peer at send time.</summary>
internal readonly struct MessagePeerState
{
    internal MessagePeerState(bool connected, bool hasMessagesBit, bool tableKnown, bool listsMod)
    {
        Connected = connected;
        HasMessagesBit = hasMessagesBit;
        TableKnown = tableKnown;
        ListsMod = listsMod;
    }

    internal bool Connected { get; }
    internal bool HasMessagesBit { get; }
    internal bool TableKnown { get; }
    internal bool ListsMod { get; }
}

/// <summary>
/// The send gate: nothing goes to a peer that is not connected (or not past the handshake), whose answer lacks
/// <c>Handshake.Capability.ModMessages</c>, whose table has not arrived, or whose table does not list the mod.
/// </summary>
internal static class ModMessageGate
{
    internal static SendResult Decide(in MessagePeerState peer, int payloadBytes)
    {
        if (payloadBytes > ModMessageEnvelope.MaxPayloadBytes) return SendResult.TooLarge;
        if (!peer.Connected) return SendResult.NotConnected;
        if (!peer.HasMessagesBit) return SendResult.PeerTooOld;
        if (!peer.TableKnown) return SendResult.PeerNotReady;
        if (!peer.ListsMod) return SendResult.PeerLacksMod;
        return SendResult.Sent;
    }
}
