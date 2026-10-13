using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Content;

/// <summary>
/// <c>PrfI</c> ('PrfI' = <c>0x50726649</c>): the server's authoritative ItemKeyLookup tail,
/// sent once per join (PrfI or reassembled PrfJ chunks) before the first <c>PrfD</c>/<c>PrfK</c>. Game-free encode/decode so
/// the clamps and the refuse-the-whole-packet decision cannot drift from the tests.
///
/// Layout (little-endian, no padding):
/// <code>
/// int32  baseLength
/// int32  count
/// int32  orderHash   // FNV-1a over the guid sequence; 0 when count is 0
/// count × {
///   int32  guid
///   uint8  idLength  // 0..63
///   bytes  id        // ASCII, idLength bytes, no terminator
/// }
/// </code>
/// A reserved (tombstone) row is just a guid; state is not sent.
/// </summary>
internal static class KeyLedgerPacket
{
    /// <summary>'PrfI' — ordered key-table tail. Distinct from PrfD/PrfU/PrfK/PrfE.</summary>
    public const int EventId = 0x50726649;

    /// <summary>Matches <c>CustomEvents.MaxPayloadBytes</c>. Larger ledgers require PrfJ chunks.</summary>
    public const int MaxPacketBytes = 15 * 1024;

    public const int MaxLedgerBytes = HeaderBytes + MaxRows * (sizeof(int) + 1 + MaxIdBytes);

    /// <summary>Hard ceiling on rows, independent of capacity. Same order as the apply budget.</summary>
    public const int MaxRows = 16384;

    public const int HeaderBytes = 3 * sizeof(int);

    public const int MaxIdBytes = PrefabWire.AsciiMaxChars;

    /// <summary>FNV-1a over the little-endian bytes of each guid, in order. Empty is 0.</summary>
    public static int HashOrder(IReadOnlyList<int> guids)
    {
        if (guids == null || guids.Count == 0) return 0;
        unchecked
        {
            const uint offset = 2166136261;
            const uint prime = 16777619;
            uint hash = offset;
            for (int i = 0; i < guids.Count; i++)
            {
                uint g = (uint)guids[i];
                hash ^= g & 0xFF; hash *= prime;
                hash ^= (g >> 8) & 0xFF; hash *= prime;
                hash ^= (g >> 16) & 0xFF; hash *= prime;
                hash ^= (g >> 24) & 0xFF; hash *= prime;
            }
            return (int)hash;
        }
    }

    public static int EncodedSize(IReadOnlyList<KeyLedgerRow> rows)
    {
        int size = HeaderBytes;
        if (rows == null) return size;
        for (int i = 0; i < rows.Count; i++)
            size += sizeof(int) + 1 + IdByteCount(rows[i].Id);
        return size;
    }

    /// <summary>
    /// Encode the tail. Returns false without writing when count, capacity, id length or
    /// packet size is out of bounds — fail closed, never a truncated list.
    /// </summary>
    public static bool TryWrite(
        byte[] dest,
        int baseLength,
        int capacity,
        IReadOnlyList<KeyLedgerRow> rows,
        out int written,
        out string error)
    {
        written = 0;
        error = "";
        rows ??= Array.Empty<KeyLedgerRow>();
        if (dest == null)
        {
            error = "destination is null";
            return false;
        }
        if (!TryValidateBounds(baseLength, capacity, rows, out error))
            return false;

        int size = EncodedSize(rows);
        if (size > dest.Length)
        {
            error = "destination is " + dest.Length.ToString(CultureInfo.InvariantCulture)
                + " byte(s); need " + size.ToString(CultureInfo.InvariantCulture) + ".";
            return false;
        }

        var guids = new int[rows.Count];
        for (int i = 0; i < rows.Count; i++)
            guids[i] = rows[i].GuidHash;

        int o = 0;
        WriteInt32(dest, ref o, baseLength);
        WriteInt32(dest, ref o, rows.Count);
        WriteInt32(dest, ref o, HashOrder(guids));
        for (int i = 0; i < rows.Count; i++)
        {
            WriteInt32(dest, ref o, rows[i].GuidHash);
            var idBytes = EncodeId(rows[i].Id, out error);
            if (idBytes == null) return false;
            dest[o++] = (byte)idBytes.Length;
            if (idBytes.Length > 0)
            {
                Buffer.BlockCopy(idBytes, 0, dest, o, idBytes.Length);
                o += idBytes.Length;
            }
        }
        written = o;
        error = "";
        return true;
    }

    /// <summary>Decode. Any bounds, hash, or ASCII failure refuses the whole packet.</summary>
    public static bool TryRead(
        byte[] src,
        int length,
        int capacity,
        out int baseLength,
        out KeyLedgerRow[] rows,
        out string error)
    {
        baseLength = 0;
        rows = Array.Empty<KeyLedgerRow>();
        error = "";
        if (src == null || length < HeaderBytes || length > src.Length || length > MaxLedgerBytes)
        {
            error = "payload length " + length.ToString(CultureInfo.InvariantCulture)
                + " is below the " + HeaderBytes.ToString(CultureInfo.InvariantCulture) + "-byte header.";
            return false;
        }
        if (capacity <= 0) capacity = KeyAgreement.DefaultCapacity;

        int o = 0;
        baseLength = ReadInt32(src, ref o);
        int count = ReadInt32(src, ref o);
        int orderHash = ReadInt32(src, ref o);
        if (count < 0 || count > MaxRows)
        {
            error = "count " + count.ToString(CultureInfo.InvariantCulture)
                + " exceeds MaxRows " + MaxRows.ToString(CultureInfo.InvariantCulture) + ".";
            return false;
        }
        if (baseLength < 0 || baseLength >= capacity)
        {
            error = "baseLength " + baseLength.ToString(CultureInfo.InvariantCulture)
                + " is outside 0.." + (capacity - 1).ToString(CultureInfo.InvariantCulture) + ".";
            return false;
        }
        if (count > capacity - baseLength)
        {
            error = KeyAgreement.HeadroomReason(count, baseLength, capacity);
            return false;
        }

        var built = count == 0 ? Array.Empty<KeyLedgerRow>() : new KeyLedgerRow[count];
        var guids = count == 0 ? Array.Empty<int>() : new int[count];
        var seen = new Dictionary<int, int>();
        for (int i = 0; i < count; i++)
        {
            if (o + sizeof(int) + 1 > length)
            {
                error = "payload truncated at row " + i.ToString(CultureInfo.InvariantCulture) + ".";
                return false;
            }
            int guid = ReadInt32(src, ref o);
            int idLen = src[o++];
            if (idLen > MaxIdBytes)
            {
                error = "id length " + idLen.ToString(CultureInfo.InvariantCulture)
                    + " at row " + i.ToString(CultureInfo.InvariantCulture) + " exceeds "
                    + MaxIdBytes.ToString(CultureInfo.InvariantCulture) + ".";
                return false;
            }
            if (o + idLen > length)
            {
                error = "payload truncated in id at row " + i.ToString(CultureInfo.InvariantCulture) + ".";
                return false;
            }
            string id = "";
            if (idLen > 0)
            {
                id = Encoding.ASCII.GetString(src, o, idLen);
                o += idLen;
                if (!PrefabWire.TryValidateAscii(id, "id", out error, out _))
                    return false;
            }
            if (seen.TryGetValue(guid, out var prev))
            {
                error = KeyAgreement.DuplicateGuidReason(guid, id, built[prev].Id, built[prev].Owner);
                return false;
            }
            seen[guid] = i;
            guids[i] = guid;
            built[i] = new KeyLedgerRow(guid, id, owner: "", tombstone: false);
        }
        if (o != length)
        {
            error = "payload has " + (length - o).ToString(CultureInfo.InvariantCulture)
                + " trailing byte(s).";
            return false;
        }
        int expected = HashOrder(guids);
        if (orderHash != expected)
        {
            error = "order hash " + orderHash.ToString(CultureInfo.InvariantCulture)
                + " != " + expected.ToString(CultureInfo.InvariantCulture)
                + " over " + count.ToString(CultureInfo.InvariantCulture) + " guid(s).";
            return false;
        }
        rows = built;
        error = "";
        return true;
    }

    public static bool TryValidateBounds(
        int baseLength, int capacity, IReadOnlyList<KeyLedgerRow> rows, out string error)
    {
        error = "";
        rows ??= Array.Empty<KeyLedgerRow>();
        if (capacity <= 0) capacity = KeyAgreement.DefaultCapacity;
        if (rows.Count > MaxRows)
        {
            error = "count " + rows.Count.ToString(CultureInfo.InvariantCulture)
                + " exceeds MaxRows " + MaxRows.ToString(CultureInfo.InvariantCulture) + ".";
            return false;
        }
        if (baseLength < 0 || baseLength >= capacity)
        {
            error = "baseLength " + baseLength.ToString(CultureInfo.InvariantCulture)
                + " is outside 0.." + (capacity - 1).ToString(CultureInfo.InvariantCulture) + ".";
            return false;
        }
        if (rows.Count > capacity - baseLength)
        {
            error = KeyAgreement.HeadroomReason(rows.Count, baseLength, capacity);
            return false;
        }
        int size = EncodedSize(rows);
        if (size > MaxLedgerBytes)
        {
            error = "encoded size " + size.ToString(CultureInfo.InvariantCulture)
                + " exceeds MaxLedgerBytes " + MaxLedgerBytes.ToString(CultureInfo.InvariantCulture) + ".";
            return false;
        }
        var seen = new Dictionary<int, int>();
        for (int i = 0; i < rows.Count; i++)
        {
            if (IdByteCount(rows[i].Id) > MaxIdBytes)
            {
                error = "id '" + rows[i].Id + "' exceeds " + MaxIdBytes.ToString(CultureInfo.InvariantCulture)
                    + " ASCII bytes.";
                return false;
            }
            if (seen.TryGetValue(rows[i].GuidHash, out var prev))
            {
                error = KeyAgreement.DuplicateGuidReason(
                    rows[i].GuidHash, rows[i].Id, rows[prev].Id, rows[prev].Owner);
                return false;
            }
            seen[rows[i].GuidHash] = i;
        }
        return true;
    }

    private static int IdByteCount(string? id)
    {
        if (string.IsNullOrEmpty(id)) return 0;
        return Encoding.ASCII.GetByteCount(id);
    }

    private static byte[]? EncodeId(string? id, out string error)
    {
        error = "";
        if (string.IsNullOrEmpty(id)) return Array.Empty<byte>();
        if (!PrefabWire.TryValidateAscii(id, "id", out error, out _))
            return null;
        return Encoding.ASCII.GetBytes(id);
    }

    private static void WriteInt32(byte[] dest, ref int o, int value)
    {
        dest[o++] = (byte)value;
        dest[o++] = (byte)(value >> 8);
        dest[o++] = (byte)(value >> 16);
        dest[o++] = (byte)(value >> 24);
    }

    private static int ReadInt32(byte[] src, ref int o)
    {
        int v = src[o]
            | (src[o + 1] << 8)
            | (src[o + 2] << 16)
            | (src[o + 3] << 24);
        o += 4;
        return v;
    }
}
