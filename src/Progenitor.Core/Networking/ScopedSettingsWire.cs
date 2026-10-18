using System;
using System.IO;
using System.Text;

namespace Progenitor.Networking;

/// <summary>Plain setting metadata and text values; Type is an opaque token, never a resolved CLR type.</summary>
internal sealed class ScopedSettingRecord
{
    public string Id { get; set; } = "";
    public string PluginId { get; set; } = "";
    public string ModName { get; set; } = "";
    public string Version { get; set; } = "";
    public string Section { get; set; } = "";
    public string Key { get; set; } = "";
    public string Help { get; set; } = "";
    public string Type { get; set; } = "";
    public string Default { get; set; } = "";
    public string Value { get; set; } = "";
    public bool Personal { get; set; }
    public bool Editable { get; set; }
    public string[] Choices { get; set; } = Array.Empty<string>();
    public string? Min { get; set; }
    public string? Max { get; set; }
    public int MaxLength { get; set; } = 1024;
}

internal sealed class ScopedSettingsPacket
{
    public byte Kind { get; set; }
    public Guid Generation { get; set; }
    public Guid RequestId { get; set; }
    public int Count { get; set; }
    public bool CanEdit { get; set; }
    public string ServerName { get; set; } = "";
    public ScopedSettingRecord? Row { get; set; }
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Expected { get; set; } = "";
    public string Value { get; set; } = "";
    public string Error { get; set; } = "";
    public bool Personal { get; set; }
    public bool Success { get; set; }
}

/// <summary>
/// Version 1: version byte, kind byte, generation (Guid.ToByteArray), then kind-specific fields.
/// Integers are little endian, text is u16 byte length plus strict UTF-8, booleans are 0/1.
/// Nullable bounds have a boolean presence prefix. No runtime types or game dependencies.
/// Begin: count, canEdit, serverName. Row: record. End: count. Delta: id, value, personal.
/// Write: requestId, id, type, expected, value, personal. Result: requestId, id, value, error, personal, success.
/// Record order follows its properties above. Unused packet fields must retain their defaults.
/// Snapshot lifecycle, authorization, supported type tokens and value semantics belong to the caller.
/// </summary>
internal static class ScopedSettingsWire
{
    internal const byte Version = 1;
    internal const int MaxPacketBytes = 15 * 1024;
    internal const int MaxFieldBytes = 4096;
    internal const int MaxRows = 512;
    internal const int MaxChoices = 128;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(ScopedSettingsPacket packet)
    {
        Validate(packet);
        // Fixed backing buffer prevents even an invalid combination of bounded fields growing the packet.
        var buffer = new byte[MaxPacketBytes];
        using var stream = new MemoryStream(buffer, writable: true);
        using var writer = new BinaryWriter(stream, Utf8, leaveOpen: true);
        try
        {
            writer.Write(Version);
            writer.Write(packet.Kind);
            writer.Write(packet.Generation.ToByteArray());
            switch (packet.Kind)
            {
                case 1:
                    writer.Write(packet.Count); writer.Write(packet.CanEdit); Write(writer, packet.ServerName); break;
                case 2:
                    WriteRow(writer, packet.Row!); break;
                case 3:
                    writer.Write(packet.Count); break;
                case 4:
                    Write(writer, packet.Id); Write(writer, packet.Value); writer.Write(packet.Personal); break;
                case 5:
                    writer.Write(packet.RequestId.ToByteArray()); Write(writer, packet.Id);
                    Write(writer, packet.Type); Write(writer, packet.Expected); Write(writer, packet.Value);
                    writer.Write(packet.Personal); break;
                case 6:
                    writer.Write(packet.RequestId.ToByteArray()); Write(writer, packet.Id);
                    Write(writer, packet.Value); Write(writer, packet.Error);
                    writer.Write(packet.Personal); writer.Write(packet.Success); break;
            }
            return buffer.AsSpan(0, checked((int)stream.Position)).ToArray();
        }
        catch (NotSupportedException e)
        { throw new ArgumentException("Scoped settings packet exceeds its byte limit.", nameof(packet), e); }
    }

    internal static bool TryDecode(ReadOnlySpan<byte> bytes, bool fromServer, out ScopedSettingsPacket packet)
    {
        packet = null!;
        if (bytes.Length < 18 || bytes.Length > MaxPacketBytes || bytes[0] != Version ||
            bytes[1] < 1 || bytes[1] > 6 || fromServer == (bytes[1] == 5)) return false;
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Utf8);
            reader.ReadByte();
            var result = new ScopedSettingsPacket { Kind = reader.ReadByte(), Generation = ReadGuid(reader) };
            switch (result.Kind)
            {
                case 1:
                    result.Count = reader.ReadInt32(); result.CanEdit = ReadBool(reader); result.ServerName = Read(reader); break;
                case 2:
                    result.Row = ReadRow(reader); break;
                case 3:
                    result.Count = reader.ReadInt32(); break;
                case 4:
                    result.Id = Read(reader); result.Value = Read(reader); result.Personal = ReadBool(reader); break;
                case 5:
                    result.RequestId = ReadGuid(reader); result.Id = Read(reader); result.Type = Read(reader);
                    result.Expected = Read(reader); result.Value = Read(reader); result.Personal = ReadBool(reader); break;
                case 6:
                    result.RequestId = ReadGuid(reader); result.Id = Read(reader); result.Value = Read(reader);
                    result.Error = Read(reader); result.Personal = ReadBool(reader); result.Success = ReadBool(reader); break;
            }
            if (stream.Position != stream.Length) return false;
            Validate(result);
            packet = result;
            return true;
        }
        catch (Exception e) when (e is IOException || e is InvalidDataException || e is ArgumentException)
        { return false; }
    }

    private static void Validate(ScopedSettingsPacket p)
    {
        if (p == null) throw new ArgumentNullException(nameof(p));
        Require(p.Kind >= 1 && p.Kind <= 6 && p.Generation != Guid.Empty);
        bool begin = p.Kind == 1, row = p.Kind == 2;
        bool counted = begin || p.Kind == 3, write = p.Kind == 5, result = p.Kind == 6;
        bool request = write || result;
        Require(counted ? p.Count >= 0 && p.Count <= MaxRows : p.Count == 0);
        Require(begin || (!p.CanEdit && p.ServerName == ""));
        Require(row ? p.Row != null : p.Row == null);
        Require(request ? p.RequestId != Guid.Empty : p.RequestId == Guid.Empty);
        Require(request || p.Kind == 4 || (p.Id == "" && p.Value == "" && !p.Personal));
        Require(write || (p.Type == "" && p.Expected == ""));
        Require(result || (p.Error == "" && !p.Success));
        Require(!p.Success || p.Error == "");
        Check(p.ServerName); Check(p.Id); Check(p.Type); Check(p.Expected); Check(p.Value); Check(p.Error);
        if (request || p.Kind == 4) CheckId(p.Id);
        if (write) Require(!string.IsNullOrWhiteSpace(p.Type));
        if (!row) return;
        var r = p.Row!;
        Check(r.Id); Check(r.PluginId); Check(r.ModName); Check(r.Version); Check(r.Section); Check(r.Key);
        Check(r.Help); Check(r.Type); Check(r.Default); Check(r.Value);
        CheckId(r.Id);
        Require(Part(r.PluginId) && Part(r.Section) && Part(r.Key));
        Require(r.Id == r.PluginId + "/" + r.Section + "/" + r.Key && !string.IsNullOrWhiteSpace(r.Type));
        Require(r.MaxLength > 0 && r.MaxLength <= MaxFieldBytes);
        Require(r.Choices != null && r.Choices.Length <= MaxChoices);
        foreach (var choice in r.Choices!) Check(choice);
        if (r.Min != null) Check(r.Min);
        if (r.Max != null) Check(r.Max);
    }

    private static bool Part(string value) => !string.IsNullOrWhiteSpace(value) && value.IndexOf('/') < 0;

    private static void CheckId(string value)
    {
        var parts = value.Split('/');
        Require(parts.Length == 3 && Part(parts[0]) && Part(parts[1]) && Part(parts[2]));
    }

    private static void Require(bool valid)
    { if (!valid) throw new ArgumentException("Invalid scoped settings fields."); }

    private static void Check(string value)
    { Require(value != null && value.Length <= MaxFieldBytes && Utf8.GetByteCount(value) <= MaxFieldBytes); }

    private static void Write(BinaryWriter writer, string value)
    {
        Check(value);
        var bytes = Utf8.GetBytes(value);
        writer.Write((ushort)bytes.Length);
        writer.Write(bytes);
    }

    private static string Read(BinaryReader reader)
    {
        int length = reader.ReadUInt16();
        if (length > MaxFieldBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid scoped settings text length.");
        return Utf8.GetString(reader.ReadBytes(length));
    }

    private static bool ReadBool(BinaryReader reader)
    {
        byte value = reader.ReadByte();
        if (value > 1) throw new InvalidDataException("Invalid boolean.");
        return value == 1;
    }

    private static Guid ReadGuid(BinaryReader reader) => new(reader.ReadBytes(16));

    private static void WriteRow(BinaryWriter w, ScopedSettingRecord r)
    {
        Write(w, r.Id); Write(w, r.PluginId); Write(w, r.ModName); Write(w, r.Version);
        Write(w, r.Section); Write(w, r.Key); Write(w, r.Help); Write(w, r.Type);
        Write(w, r.Default); Write(w, r.Value); w.Write(r.Personal); w.Write(r.Editable);
        w.Write((ushort)r.Choices.Length);
        foreach (var choice in r.Choices) Write(w, choice);
        w.Write(r.Min != null); if (r.Min != null) Write(w, r.Min);
        w.Write(r.Max != null); if (r.Max != null) Write(w, r.Max);
        w.Write(r.MaxLength);
    }

    private static ScopedSettingRecord ReadRow(BinaryReader r)
    {
        var row = new ScopedSettingRecord
        {
            Id = Read(r), PluginId = Read(r), ModName = Read(r), Version = Read(r),
            Section = Read(r), Key = Read(r), Help = Read(r), Type = Read(r),
            Default = Read(r), Value = Read(r), Personal = ReadBool(r), Editable = ReadBool(r),
        };
        int count = r.ReadUInt16();
        if (count > MaxChoices) throw new InvalidDataException("Too many choices.");
        row.Choices = new string[count];
        for (int i = 0; i < count; i++) row.Choices[i] = Read(r);
        row.Min = ReadBool(r) ? Read(r) : null;
        row.Max = ReadBool(r) ? Read(r) : null;
        row.MaxLength = r.ReadInt32();
        return row;
    }
}
