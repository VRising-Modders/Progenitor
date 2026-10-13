using System;
using System.Collections.Generic;

namespace Progenitor.Networking;

/// <summary>
/// Chunked catalog packet. Handshake records are a fixed unmanaged struct; the
/// manifest is UTF-8 JSON, so this is the same 15 KiB budget as
/// <c>ConfigSyncWire</c> with an explicit seq/count so a client can reassemble
/// without holding a handshake slot. Layout:
/// <c>[version u8][flags u8][seq u16le][count u16le][nbytes u16le][bytes]</c>.
/// Flag bit 0 means another chunk follows. Receive slots stay with Bootstrap;
/// this type only packs and parses.
/// </summary>
internal static class ModManifestWire
{
    public const byte Version = 1;
    public const byte FlagMore = 1 << 0;

    /// <summary>
    /// Same ceiling as <c>CustomEvents.MaxPayloadBytes</c>, duplicated so tests compile
    /// this file without the game types that class pulls in.
    /// </summary>
    public const int MaxPacketBytes = 15 * 1024;

    public const int HeaderBytes = 8;
    public const int MaxEnvelopeBytes = 256 * 1024;
    public const int MaxSliceBytes = MaxPacketBytes - HeaderBytes;

    public static List<byte[]> Pack(ReadOnlySpan<byte> envelope)
    {
        var packets = new List<byte[]>();
        if (envelope.Length > MaxEnvelopeBytes)
            throw new ArgumentOutOfRangeException(nameof(envelope), "manifest exceeds " + MaxEnvelopeBytes + " bytes");
        int count = envelope.Length == 0 ? 1 : (envelope.Length + MaxSliceBytes - 1) / MaxSliceBytes;
        if (count < 1) count = 1;
        if (count > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(envelope), "manifest needs more than 65535 chunks");
        int offset = 0;
        for (int seq = 0; seq < count; seq++)
        {
            int n = envelope.Length - offset;
            if (n > MaxSliceBytes) n = MaxSliceBytes;
            if (n < 0) n = 0;
            var packet = new byte[HeaderBytes + n];
            packet[0] = Version;
            packet[1] = (byte)(seq + 1 < count ? FlagMore : 0);
            packet[2] = (byte)seq;
            packet[3] = (byte)(seq >> 8);
            packet[4] = (byte)count;
            packet[5] = (byte)(count >> 8);
            packet[6] = (byte)n;
            packet[7] = (byte)(n >> 8);
            if (n > 0) envelope.Slice(offset, n).CopyTo(packet.AsSpan(HeaderBytes));
            packets.Add(packet);
            offset += n;
        }
        return packets;
    }

    public static bool TryRead(
        ReadOnlySpan<byte> src,
        out int seq,
        out int count,
        out bool more,
        out byte[] slice,
        out string? error)
    {
        seq = 0;
        count = 0;
        more = false;
        slice = Array.Empty<byte>();
        error = null;
        if (src.Length < HeaderBytes)
        {
            error = "packet shorter than the 8-byte header";
            return false;
        }
        byte version = src[0];
        if (version != Version)
        {
            error = "unsupported ModManifest version " + version.ToString();
            return false;
        }
        more = (src[1] & FlagMore) != 0;
        seq = src[2] | (src[3] << 8);
        count = src[4] | (src[5] << 8);
        int n = src[6] | (src[7] << 8);
        if (count < 1 || count > 4096)
        {
            error = "malformed ModManifest count " + count.ToString();
            return false;
        }
        if (seq < 0 || seq >= count)
        {
            error = "ModManifest seq " + seq.ToString() + " is out of range";
            return false;
        }
        if (n < 0 || n > MaxSliceBytes)
        {
            error = "ModManifest slice " + n.ToString() + " is out of range";
            return false;
        }
        if (HeaderBytes + n > src.Length)
        {
            error = "truncated ModManifest slice";
            return false;
        }
        slice = new byte[n];
        if (n > 0) src.Slice(HeaderBytes, n).CopyTo(slice);
        return true;
    }

    /// <summary>
    /// In-order or out-of-order reassembly of one envelope. A new catalog starts at
    /// <c>seq == 0</c> once this transfer already has a seq-0 (or has completed), so a
    /// second 1-chunk envelope with the same <c>count</c> is not treated as a duplicate of
    /// the first. Out-of-order first packets (seq 1 of 2 before seq 0) still allocate
    /// without resetting. After a complete envelope the in-flight state is cleared; a
    /// duplicate of those bytes never re-emits.
    /// </summary>
    internal sealed class Reassembly
    {
        private byte[]?[] _parts = Array.Empty<byte[]?>();
        private int _count;
        private int _got;
        private int _total;
        private byte[]? _lastComplete;
        private bool _completed;

        public bool TryAdd(int seq, int count, byte[] slice, out byte[]? envelope, out string? error)
        {
            envelope = null;
            error = null;
            if (count < 1 || seq < 0 || seq >= count)
            {
                error = "chunk index out of range";
                return false;
            }

            // seq 0 starts a new transfer when we already have a seq-0 (in-flight resend of
            // a new catalog, or a completed envelope). Seq 0 after an out-of-order seq>0
            // does not reset — that is the proven 2-chunk arrival order.
            bool haveSeq0 = _parts.Length > 0 && _parts[0] != null;
            if (seq == 0 && (_completed || haveSeq0))
                ResetInFlight(count);
            else if (_count != count || _parts.Length != count)
                ResetInFlight(count);

            if (_parts[seq] != null)
            {
                // Duplicate of a reliable resend: ignore. Never re-emit a completed envelope.
                return true;
            }
            int add = slice?.Length ?? 0;
            if (_total + add > MaxEnvelopeBytes)
            {
                error = "reassembled manifest exceeds " + MaxEnvelopeBytes + " bytes";
                Reset();
                return false;
            }
            _parts[seq] = slice ?? Array.Empty<byte>();
            _got++;
            _total += add;
            if (_got != _count) return true;

            byte[] all = Concat();
            bool duplicate = _lastComplete != null && BytesEqual(_lastComplete, all);
            ResetInFlight(0);
            _completed = true;
            if (duplicate) return true;
            _lastComplete = all;
            envelope = all;
            return true;
        }

        public void Reset()
        {
            ResetInFlight(0);
            _lastComplete = null;
            _completed = false;
        }

        private void ResetInFlight(int count)
        {
            _parts = count > 0 ? new byte[]?[count] : Array.Empty<byte[]?>();
            _count = count;
            _got = 0;
            _total = 0;
            _completed = false;
        }

        private byte[] Concat()
        {
            var all = new byte[_total];
            int pos = 0;
            for (int i = 0; i < _parts.Length; i++)
            {
                var part = _parts[i] ?? Array.Empty<byte>();
                if (part.Length == 0) continue;
                Buffer.BlockCopy(part, 0, all, pos, part.Length);
                pos += part.Length;
            }
            return all;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
