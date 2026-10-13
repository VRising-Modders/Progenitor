using System;
using System.IO;
using System.Text;

namespace Progenitor.Networking;

internal readonly struct ServerSettingsMessage
{
    internal ServerSettingsMessage(Guid id, string rowId, string type, string expected, string value, bool success, string error)
    { Id = id; RowId = rowId; Type = type; Expected = expected; Value = value; Success = success; Error = error; }
    internal Guid Id { get; }
    internal string RowId { get; }
    internal string Type { get; }
    internal string Expected { get; }
    internal string Value { get; }
    internal bool Success { get; }
    internal string Error { get; }
    internal ServerSettingsMessage WithResult(ServerSettingResult result) =>
        new(Id, RowId, "", "", result.Value, result.Success, result.Error);
}

/// <summary>Bounded, versioned UTF-8 packet. Invalid UTF-8, truncation and trailing bytes are rejected.</summary>
internal static class ServerSettingsWire
{
    internal const int MaxTextBytes = 2048;
    internal const int MaxPacketBytes = 12 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static void CheckText(string value)
    {
        if (value == null || Utf8.GetByteCount(value) > MaxTextBytes)
            throw new ArgumentException("A server settings field exceeds " + MaxTextBytes + " UTF-8 bytes.");
    }

    internal static byte[] Encode(ServerSettingsMessage message, bool response)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        writer.Write((byte)1);
        writer.Write((byte)(response ? 1 : 0));
        writer.Write(message.Id.ToByteArray());
        Write(writer, message.RowId);
        if (response)
        {
            writer.Write((byte)(message.Success ? 1 : 0));
            Write(writer, message.Value);
            Write(writer, message.Error);
        }
        else
        {
            Write(writer, message.Type);
            Write(writer, message.Expected);
            Write(writer, message.Value);
        }
        if (stream.Length > MaxPacketBytes) throw new ArgumentException("Settings packet is too large.");
        return stream.ToArray();
    }

    private static void Write(BinaryWriter writer, string text)
    {
        CheckText(text);
        var bytes = Utf8.GetBytes(text);
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    internal static bool TryDecode(ReadOnlySpan<byte> packet, bool response, out ServerSettingsMessage message)
    {
        message = default;
        if (packet.Length < 20 || packet.Length > MaxPacketBytes) return false;
        try
        {
            using var stream = new MemoryStream(packet.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Utf8);
            if (reader.ReadByte() != 1 || reader.ReadByte() != (response ? 1 : 0)) return false;
            var id = new Guid(reader.ReadBytes(16));
            if (id == Guid.Empty) return false;
            string row = Read(reader);
            if (string.IsNullOrEmpty(row)) return false;
            if (response)
            {
                byte success = reader.ReadByte();
                if (success > 1) return false;
                message = new(id, row, "", "", Read(reader), success == 1, Read(reader));
            }
            // TEXTLIMIT1: a request's expected and requested values are read up to the packet's own
            // bound, not the field limit, so an over-long value from a client that skipped its own
            // check reaches ServerSettings.Decide and is refused there, answered and audited, instead
            // of being dropped unseen. Every field the server writes back stays within MaxTextBytes.
            else message = new(id, row, Read(reader), Read(reader, MaxPacketBytes), Read(reader, MaxPacketBytes), false, "");
            return stream.Position == stream.Length;
        }
        catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException || e is DecoderFallbackException)
        { message = default; return false; }
    }

    private static string Read(BinaryReader reader, int max = MaxTextBytes)
    {
        int count = reader.ReadUInt16();
        if (count > max || count > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid settings field length.");
        return Utf8.GetString(reader.ReadBytes(count));
    }
}
