using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Networking;

/// <summary>
/// One synced setting on the wire: plugin GUID plus BepInEx section/key, value already in
/// Toml string form. Plugin id is the handshake guid so the server can send only entries
/// the client also reported.
/// </summary>
public readonly struct ConfigSyncRecord
{
    public ConfigSyncRecord(string pluginId, string section, string key, string toml)
        : this(pluginId, section, key, toml, typeName: "")
    {
    }

    public ConfigSyncRecord(string pluginId, string section, string key, string toml, string typeName)
    {
        PluginId = pluginId ?? "";
        Section = section ?? "";
        Key = key ?? "";
        Toml = toml ?? "";
        TypeName = typeName ?? "";
    }

    public string PluginId { get; }
    public string Section { get; }
    public string Key { get; }
    public string Toml { get; }

    /// <summary>
    /// BCL type token of the server <c>ConfigEntry</c> (<c>Type.FullName</c>). Empty on
    /// a v1 packet. Under <c>NetworkVersionRule.Minor</c> an empty name is a type-gate
    /// refuse (logged, not applied); Exact/None still apply so a mixed Core swap cannot
    /// drop a snapshot.
    /// </summary>
    public string TypeName { get; }
}

/// <summary>
/// Variable-length config-sync packet. Handshake records are a fixed unmanaged struct;
/// config values are strings, so this is length-prefixed UTF-8 instead. Layout:
/// <c>[version u8][flags u8][count u16le]</c> then count times
/// <c>[len u16le][utf8]</c> for plugin id, section, key, Toml value, and (v2) type name.
/// Flag bit 0 is a join snapshot (vs a later push); bit 1 means another packet follows.
/// v1 packets (four fields, no type) still parse so a mixed Core swap cannot drop a snapshot.
/// </summary>
internal static class ConfigSyncWire
{
    public const byte Version = 2;
    public const byte VersionLegacy = 1;
    public const byte FlagSnapshot = 1 << 0;
    public const byte FlagMore = 1 << 1;

    /// <summary>
    /// Same ceiling as <c>CustomEvents.MaxPayloadBytes</c>, duplicated so tests compile
    /// this file without the game types that class pulls in.
    /// </summary>
    public const int MaxPacketBytes = 15 * 1024;

    /// <summary>Longest single UTF-8 field. Plugin ids match the handshake 63-char guid.</summary>
    public const int MaxFieldBytes = 4096;

    /// <summary>
    /// Mod compatibility C3: the type-name field of the reserved record that marks "this plugin's snapshot was sent"
    /// (empty section, key and value). Only in join snapshots to clients that declared
    /// <c>Handshake.Capability.VersionRanges</c>, so an older client never sees one. A real entry never has an empty
    /// section and key.
    /// </summary>
    public const string SentMarkerType = "progenitor:snapshot-sent";

    /// <summary>C3: the marker record for <paramref name="pluginId"/>.</summary>
    public static ConfigSyncRecord SentMarker(string pluginId) => new(pluginId, "", "", "", SentMarkerType);

    /// <summary>C3: true for a <see cref="SentMarker"/> record.</summary>
    public static bool IsSentMarker(in ConfigSyncRecord rec) =>
        rec.Section.Length == 0 && rec.Key.Length == 0 && string.Equals(rec.TypeName, SentMarkerType, StringComparison.Ordinal);

    /// <summary>
    /// Pack as many of <paramref name="records"/> as fit in <paramref name="dest"/>,
    /// starting at <paramref name="start"/>. Returns false only when the buffer is too
    /// small for the header (8 bytes) or a single record cannot fit in <see cref="MaxPacketBytes"/>.
    /// </summary>
    public static bool TryWrite(
        Span<byte> dest,
        IReadOnlyList<ConfigSyncRecord> records,
        int start,
        bool snapshot,
        bool more,
        out int bytesWritten,
        out int recordsWritten,
        out string? error)
    {
        bytesWritten = 0;
        recordsWritten = 0;
        error = null;
        if (dest.Length < 4)
        {
            error = "buffer shorter than the 4-byte header";
            return false;
        }
        if (records == null) records = Array.Empty<ConfigSyncRecord>();
        if (start < 0) start = 0;

        dest[0] = Version;
        dest[1] = (byte)((snapshot ? FlagSnapshot : 0) | (more ? FlagMore : 0));
        int pos = 4;
        int count = 0;
        for (int i = start; i < records.Count; i++)
        {
            var rec = records[i];
            if (!TryMeasure(rec, out int need, out error))
            {
                // An unsendable later record must not discard records already packed.
                // Pack will retry from this index and fail-closed that entry.
                if (count == 0)
                    return false;
                break;
            }
            if (pos + need > dest.Length)
            {
                if (count == 0)
                {
                    error = "single record exceeds packet buffer";
                    return false;
                }
                break;
            }
            WriteField(dest, ref pos, rec.PluginId);
            WriteField(dest, ref pos, rec.Section);
            WriteField(dest, ref pos, rec.Key);
            WriteField(dest, ref pos, rec.Toml);
            WriteField(dest, ref pos, rec.TypeName);
            count++;
        }

        dest[2] = (byte)count;
        dest[3] = (byte)(count >> 8);
        // If we stopped early there is another packet, even if the caller did not set more.
        if (start + count < records.Count)
            dest[1] |= FlagMore;
        else if (!more)
            dest[1] = (byte)(dest[1] & ~FlagMore);

        bytesWritten = pos;
        recordsWritten = count;
        error = null;
        return true;
    }

    /// <summary>Parse a packet. Malformed input returns false and an error; never throws.</summary>
    public static bool TryRead(
        ReadOnlySpan<byte> src,
        out bool snapshot,
        out bool more,
        out List<ConfigSyncRecord> records,
        out string? error)
    {
        snapshot = false;
        more = false;
        records = new List<ConfigSyncRecord>();
        error = null;
        if (src.Length < 4)
        {
            error = "packet shorter than the 4-byte header";
            return false;
        }
        byte version = src[0];
        if (version != Version && version != VersionLegacy)
        {
            error = "unsupported ConfigSync version " + version.ToString();
            return false;
        }
        bool hasType = version >= Version;
        byte flags = src[1];
        snapshot = (flags & FlagSnapshot) != 0;
        more = (flags & FlagMore) != 0;
        int count = src[2] | (src[3] << 8);
        if (count < 0 || count > 4096)
        {
            error = "malformed ConfigSync count " + count.ToString();
            return false;
        }

        int pos = 4;
        for (int i = 0; i < count; i++)
        {
            if (!TryReadField(src, ref pos, out var pluginId, out error)) return false;
            if (!TryReadField(src, ref pos, out var section, out error)) return false;
            if (!TryReadField(src, ref pos, out var key, out error)) return false;
            if (!TryReadField(src, ref pos, out var toml, out error)) return false;
            string typeName = "";
            if (hasType && !TryReadField(src, ref pos, out typeName, out error)) return false;
            records.Add(new ConfigSyncRecord(pluginId, section, key, toml, typeName));
        }
        return true;
    }

    /// <summary>
    /// A record <see cref="Pack"/> could not put on the wire. The rest of the snapshot
    /// still packs; this entry fails closed (not sent, caller logs).
    /// </summary>
    public readonly struct PackSkip
    {
        public PackSkip(ConfigSyncRecord record, string error)
        {
            Record = record;
            Error = error ?? "";
        }

        public ConfigSyncRecord Record { get; }
        public string Error { get; }
    }

    /// <summary>Encode every record into one or more packets that each fit <see cref="MaxPacketBytes"/>.</summary>
    public static List<byte[]> Pack(IReadOnlyList<ConfigSyncRecord> records, bool snapshot) =>
        Pack(records, snapshot, out _);

    /// <summary>
    /// Encode every record into one or more packets that each fit <see cref="MaxPacketBytes"/>.
    /// A record that cannot fit is listed in <paramref name="skipped"/> and is not packed;
    /// later records still go. Never silently dropped.
    /// </summary>
    public static List<byte[]> Pack(
        IReadOnlyList<ConfigSyncRecord> records,
        bool snapshot,
        out List<PackSkip> skipped)
    {
        var packets = new List<byte[]>();
        skipped = new List<PackSkip>();
        records ??= Array.Empty<ConfigSyncRecord>();
        int start = 0;
        if (records.Count == 0)
        {
            var empty = new byte[MaxPacketBytes];
            if (!TryWrite(empty, records, 0, snapshot, more: false, out int n, out _, out _))
                return packets;
            var slim = new byte[n];
            Buffer.BlockCopy(empty, 0, slim, 0, n);
            packets.Add(slim);
            return packets;
        }

        var scratch = new byte[MaxPacketBytes];
        while (start < records.Count)
        {
            if (!TryWrite(scratch, records, start, snapshot, more: false, out int n, out int wrote, out var error)
                || wrote <= 0)
            {
                var rec = records[start];
                skipped.Add(new PackSkip(rec, error ?? "single record exceeds packet buffer"));
                start++;
                continue;
            }
            var slim = new byte[n];
            Buffer.BlockCopy(scratch, 0, slim, 0, n);
            packets.Add(slim);
            start += wrote;
        }
        return packets;
    }

    /// <summary>
    /// Bytes this record needs after the 4-byte packet header. False when a field
    /// exceeds <see cref="MaxFieldBytes"/> or the record cannot fit in
    /// <see cref="MaxPacketBytes"/> even as the only payload.
    /// </summary>
    internal static bool TryMeasure(in ConfigSyncRecord rec, out int bytes, out string? error)
    {
        bytes = 0;
        error = null;
        int a = Utf8Len(rec.PluginId);
        int b = Utf8Len(rec.Section);
        int c = Utf8Len(rec.Key);
        int d = Utf8Len(rec.Toml);
        int e = Utf8Len(rec.TypeName);
        if (a > MaxFieldBytes || b > MaxFieldBytes || c > MaxFieldBytes
            || d > MaxFieldBytes || e > MaxFieldBytes)
        {
            error = "ConfigSync field exceeds " + MaxFieldBytes.ToString() + " UTF-8 bytes";
            return false;
        }
        bytes = 10 + a + b + c + d + e;
        if (bytes + 4 > MaxPacketBytes)
        {
            error = "record is " + (bytes + 4).ToString() + " bytes; max packet is "
                + MaxPacketBytes.ToString();
            return false;
        }
        return true;
    }

    private static int Utf8Len(string? s) => Encoding.UTF8.GetByteCount(s ?? "");

    private static void WriteField(Span<byte> dest, ref int pos, string? value)
    {
        value ??= "";
        int n = Encoding.UTF8.GetBytes(value, dest.Slice(pos + 2));
        dest[pos] = (byte)n;
        dest[pos + 1] = (byte)(n >> 8);
        pos += 2 + n;
    }

    private static bool TryReadField(ReadOnlySpan<byte> src, ref int pos, out string value, out string? error)
    {
        value = "";
        error = null;
        if (pos + 2 > src.Length)
        {
            error = "truncated ConfigSync field length";
            return false;
        }
        int n = src[pos] | (src[pos + 1] << 8);
        pos += 2;
        if (n < 0 || n > MaxFieldBytes)
        {
            error = "ConfigSync field length " + n.ToString() + " is out of range";
            return false;
        }
        if (pos + n > src.Length)
        {
            error = "truncated ConfigSync field";
            return false;
        }
        value = Encoding.UTF8.GetString(src.Slice(pos, n));
        pos += n;
        return true;
    }
}
