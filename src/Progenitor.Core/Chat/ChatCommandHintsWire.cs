using System;
using System.Collections.Generic;
using System.Text;
using Progenitor.Commands;

namespace Progenitor.Chat;

/// <summary>
/// The chat command hints channel's packets, game-free. One Core-reserved event id carries both directions
/// (<c>'PrCh'</c>, <see cref="Networking.CustomEventPolicy.ChatCommandHintsEventId"/>); the kind byte keeps a packet
/// from one direction from ever decoding as the other.
///
/// <list type="bullet">
/// <item><b>Hello</b> (server to client): <c>version, kind 1, int stamp</c>. Sent after an ok handshake to a client that
/// declared <c>Handshake.Capability.CommandHints</c>, and again to those clients when the server's command list changes
/// (a new stamp). A client never sends on this id before a hello arrived, so an older server or one without Progenitor
/// is never sent an id it has no handler for.</item>
/// <item><b>Request</b> (client to server): <c>version, kind 2, int nonce</c>. Asks for the sender's catalog.</item>
/// <item><b>Page</b> (server to client): <c>version, kind 3, int nonce, int stamp, u16 index, u16 count, u16 entries</c>,
/// then per entry three strings (command, usage, help), each a u16 byte length and UTF-8. A catalog is one or more
/// pages, each at most <see cref="MaxPacketBytes"/>.</item>
/// </list>
/// Integers are little-endian. A malformed packet is dropped whole.
/// </summary>
internal static class ChatCommandHintsWire
{
    internal const byte Version = 1;
    internal const byte KindHello = 1;
    internal const byte KindRequest = 2;
    internal const byte KindPage = 3;

    /// <summary>Largest packet this channel builds; below the transport's 15 KiB payload limit.</summary>
    internal const int MaxPacketBytes = 12 * 1024;

    /// <summary>The most pages one catalog may have (200 entries at their size caps need about 20).</summary>
    internal const int MaxPages = 64;

    private const int PageHeaderBytes = 2 + 4 + 4 + 2 + 2 + 2;

    /// <summary>The largest one entry can encode to: three length prefixes and at most 3 UTF-8 bytes per character.</summary>
    internal const int MaxEntryBytes = 6 + 3 * (CommandHintCatalog.MaxCommandChars + CommandHintCatalog.MaxUsageChars + CommandHintCatalog.MaxHelpChars);

    internal static byte KindOf(ReadOnlySpan<byte> packet) =>
        packet.Length >= 2 && packet[0] == Version ? packet[1] : (byte)0;

    internal static byte[] EncodeHello(int stamp)
    {
        var bytes = new byte[6];
        bytes[0] = Version;
        bytes[1] = KindHello;
        WriteInt(bytes, 2, stamp);
        return bytes;
    }

    internal static bool TryDecodeHello(ReadOnlySpan<byte> packet, out int stamp)
    {
        stamp = 0;
        if (packet.Length != 6 || KindOf(packet) != KindHello) return false;
        stamp = ReadInt(packet, 2);
        return true;
    }

    internal static byte[] EncodeRequest(int nonce)
    {
        var bytes = new byte[6];
        bytes[0] = Version;
        bytes[1] = KindRequest;
        WriteInt(bytes, 2, nonce);
        return bytes;
    }

    internal static bool TryDecodeRequest(ReadOnlySpan<byte> packet, out int nonce)
    {
        nonce = 0;
        if (packet.Length != 6 || KindOf(packet) != KindRequest) return false;
        nonce = ReadInt(packet, 2);
        return true;
    }

    /// <summary>
    /// The catalog as pages of at most <paramref name="maxPacketBytes"/>. An empty catalog is one page with no entries.
    /// Each string is clipped to its <see cref="CommandHintCatalog"/> cap first, so one entry always fits a page. Entries
    /// past <see cref="MaxPages"/> pages are dropped.
    /// </summary>
    internal static List<byte[]> EncodePages(int nonce, int stamp, IReadOnlyList<CommandHintEntry> entries, int maxPacketBytes = MaxPacketBytes)
    {
        if (maxPacketBytes < PageHeaderBytes + MaxEntryBytes)
            throw new ArgumentOutOfRangeException(nameof(maxPacketBytes));

        var encoded = new List<byte[]>();
        foreach (var e in entries ?? (IReadOnlyList<CommandHintEntry>)Array.Empty<CommandHintEntry>())
        {
            if (e == null) continue;
            encoded.Add(EncodeEntry(e.Command, e.Usage, e.Help));
        }

        var groups = new List<List<byte[]>>();
        var current = new List<byte[]>();
        int size = PageHeaderBytes;
        foreach (var entry in encoded)
        {
            if (current.Count > 0 && size + entry.Length > maxPacketBytes)
            {
                groups.Add(current);
                if (groups.Count == MaxPages) break;
                current = new List<byte[]>();
                size = PageHeaderBytes;
            }

            current.Add(entry);
            size += entry.Length;
        }

        if (groups.Count < MaxPages && (current.Count > 0 || groups.Count == 0)) groups.Add(current);

        var pages = new List<byte[]>(groups.Count);
        for (int i = 0; i < groups.Count; i++)
        {
            int length = PageHeaderBytes;
            foreach (var entry in groups[i]) length += entry.Length;
            var bytes = new byte[length];
            bytes[0] = Version;
            bytes[1] = KindPage;
            WriteInt(bytes, 2, nonce);
            WriteInt(bytes, 6, stamp);
            WriteU16(bytes, 10, i);
            WriteU16(bytes, 12, groups.Count);
            WriteU16(bytes, 14, groups[i].Count);
            int at = PageHeaderBytes;
            foreach (var entry in groups[i])
            {
                Buffer.BlockCopy(entry, 0, bytes, at, entry.Length);
                at += entry.Length;
            }

            pages.Add(bytes);
        }

        return pages;
    }

    /// <summary>One decoded page.</summary>
    internal sealed class Page
    {
        public int Nonce;
        public int Stamp;
        public int Index;
        public int Count;
        public List<ChatCommandHint> Entries = new();
    }

    internal static bool TryDecodePage(ReadOnlySpan<byte> packet, out Page page)
    {
        page = new Page();
        if (packet.Length < PageHeaderBytes || packet.Length > CustomEventsMaxPayload || KindOf(packet) != KindPage) return false;
        page.Nonce = ReadInt(packet, 2);
        page.Stamp = ReadInt(packet, 6);
        page.Index = ReadU16(packet, 10);
        page.Count = ReadU16(packet, 12);
        int entries = ReadU16(packet, 14);
        if (page.Count < 1 || page.Count > MaxPages || page.Index >= page.Count) return false;
        int at = PageHeaderBytes;
        for (int i = 0; i < entries; i++)
        {
            if (!TryReadString(packet, ref at, CommandHintCatalog.MaxCommandChars, out string command)) return false;
            if (!TryReadString(packet, ref at, CommandHintCatalog.MaxUsageChars, out string usage)) return false;
            if (!TryReadString(packet, ref at, CommandHintCatalog.MaxHelpChars, out string help)) return false;
            if (command.Length == 0) return false;
            page.Entries.Add(new ChatCommandHint(command, usage, help));
        }

        return at == packet.Length;
    }

    /// <summary>The transport's own limit (<c>CustomEvents.MaxPayloadBytes</c>), restated here so this file stays game-free.</summary>
    internal const int CustomEventsMaxPayload = 15 * 1024;

    private static byte[] EncodeEntry(string command, string usage, string help)
    {
        byte[] c = Utf8(CommandHintCatalog.Clip(command, CommandHintCatalog.MaxCommandChars));
        byte[] u = Utf8(CommandHintCatalog.Clip(usage, CommandHintCatalog.MaxUsageChars));
        byte[] h = Utf8(CommandHintCatalog.Clip(help, CommandHintCatalog.MaxHelpChars));
        var bytes = new byte[6 + c.Length + u.Length + h.Length];
        int at = 0;
        foreach (var part in new[] { c, u, h })
        {
            WriteU16(bytes, at, part.Length);
            Buffer.BlockCopy(part, 0, bytes, at + 2, part.Length);
            at += 2 + part.Length;
        }

        return bytes;
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text ?? "");

    private static bool TryReadString(ReadOnlySpan<byte> packet, ref int at, int maxChars, out string text)
    {
        text = "";
        if (at + 2 > packet.Length) return false;
        int length = ReadU16(packet, at);
        at += 2;
        if (length > maxChars * 4 || at + length > packet.Length) return false;
        try { text = Strict.GetString(packet.Slice(at, length).ToArray()); }
        catch (DecoderFallbackException) { return false; }
        at += length;
        return text.Length <= maxChars;
    }

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static void WriteInt(byte[] bytes, int at, int value)
    {
        bytes[at] = (byte)value;
        bytes[at + 1] = (byte)(value >> 8);
        bytes[at + 2] = (byte)(value >> 16);
        bytes[at + 3] = (byte)(value >> 24);
    }

    private static int ReadInt(ReadOnlySpan<byte> bytes, int at) =>
        bytes[at] | (bytes[at + 1] << 8) | (bytes[at + 2] << 16) | (bytes[at + 3] << 24);

    private static void WriteU16(byte[] bytes, int at, int value)
    {
        bytes[at] = (byte)value;
        bytes[at + 1] = (byte)(value >> 8);
    }

    private static int ReadU16(ReadOnlySpan<byte> bytes, int at) => bytes[at] | (bytes[at + 1] << 8);
}

/// <summary>
/// Server-side request limits for the hints channel, game-free: one catalog per user per
/// <see cref="MinIntervalSeconds"/>; a faster request is dropped (the client asks again on its own timer).
/// </summary>
internal sealed class ChatCommandHintsRateLimit
{
    internal const double MinIntervalSeconds = 2;

    private readonly Dictionary<int, double> _last = new();

    internal bool Allow(int user, double now)
    {
        if (_last.TryGetValue(user, out double last) && now - last < MinIntervalSeconds && now >= last) return false;
        _last[user] = now;
        return true;
    }

    internal void Forget(int user) => _last.Remove(user);

    internal void Clear() => _last.Clear();
}
