using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Progenitor.Content;

/// <summary>
/// One cloned-item or cloned-buff definition on the reliable events channel. Fixed-size and
/// blittable so it can ride <see cref="Progenitor.Networking.CustomEvents"/> without a serializer.
/// Layout is four ints then three 64-byte ASCII fields (id, localized name, localized description;
/// 63 chars + NUL each, <see cref="PrefabWire.PacketSize"/> bytes total). The fourth int is
/// <see cref="PrefabKind"/> (0 = item, 1 = buff). The event id is distinct from spike 2's
/// <c>Prf2</c>, from <see cref="PrefabUnregisterPacket.EventId"/> (<c>PrfU</c>), and from
/// <see cref="PrefabUnregisterPacket.KeyPlaceholderEventId"/> (<c>PrfK</c>) so a definition, an
/// unregister, and a key-table placeholder cannot be mistaken for each other.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct PrefabDefinitionPacket
{
    /// <summary>'PrfD' — definitions must land before join-time item-key validation, so the id is large and unused by the game.</summary>
    public const int EventId = 0x50726644;

    public const int IdCapacity = PrefabWire.AsciiCapacity;
    public const int NameCapacity = PrefabWire.AsciiCapacity;
    public const int DescriptionCapacity = PrefabWire.AsciiCapacity;

    public int NewGuid;
    public int SourceGuid;
    public int MaxAmount;
    /// <summary><see cref="PrefabKind"/> as int. 0 = item (default), 1 = buff.</summary>
    public int Kind;
    public fixed byte IdBytes[IdCapacity];
    public fixed byte NameBytes[NameCapacity];
    public fixed byte DescriptionBytes[DescriptionCapacity];

    public string Id
    {
        get
        {
            fixed (byte* p = IdBytes)
                return ReadAscii(p, IdCapacity);
        }
        set
        {
            fixed (byte* p = IdBytes)
            {
                if (!TryWriteAscii(p, IdCapacity, value, "id", out var error))
                    throw new ArgumentOutOfRangeException(nameof(value), error);
            }
        }
    }

    /// <summary>ASCII display name; empty when the definition has no <see cref="PrefabDefinition.LocalizedName"/>.</summary>
    public string Name
    {
        get
        {
            fixed (byte* p = NameBytes)
                return ReadAscii(p, NameCapacity);
        }
        set
        {
            fixed (byte* p = NameBytes)
            {
                if (!TryWriteAscii(p, NameCapacity, value, "name", out var error))
                    throw new ArgumentOutOfRangeException(nameof(value), error);
            }
        }
    }

    /// <summary>ASCII tooltip text; empty when the definition has no <see cref="PrefabDefinition.LocalizedDescription"/>.</summary>
    public string Description
    {
        get
        {
            fixed (byte* p = DescriptionBytes)
                return ReadAscii(p, DescriptionCapacity);
        }
        set
        {
            fixed (byte* p = DescriptionBytes)
            {
                if (!TryWriteAscii(p, DescriptionCapacity, value, "description", out var error))
                    throw new ArgumentOutOfRangeException(nameof(value), error);
            }
        }
    }

    /// <summary>
    /// Non-throwing encode used by the connect loop and by live sends. Returns false when
    /// <see cref="PrefabDefinition.Id"/>, <see cref="PrefabDefinition.LocalizedName"/>, or
    /// <see cref="PrefabDefinition.LocalizedDescription"/> would not fit the 64-byte ASCII slots;
    /// the caller skips that definition and continues.
    /// </summary>
    public static bool TryFrom(PrefabDefinition def, out PrefabDefinitionPacket packet, out string error)
    {
        packet = default;
        error = "";
        if (def == null)
        {
            error = "definition is null";
            return false;
        }
        if (!PrefabWire.TryValidateAscii(def.Id, "id", out error, out _))
            return false;
        if (!PrefabWire.TryValidateOptionalAscii(def.LocalizedName, "name", out error))
            return false;
        if (!PrefabWire.TryValidateOptionalAscii(def.LocalizedDescription, "description", out error))
            return false;
        if (!PrefabWire.TryValidateKind((int)def.Kind, out error))
            return false;

        var built = new PrefabDefinitionPacket
        {
            NewGuid = def.Guid.GuidHash,
            SourceGuid = def.Source.GuidHash,
            MaxAmount = def.MaxAmount,
            Kind = (int)def.Kind,
        };
        if (!TryWriteAscii(built.IdBytes, IdCapacity, def.Id, "id", out var writeError))
        {
            packet = default;
            error = writeError;
            return false;
        }
        if (!TryWriteAscii(built.NameBytes, NameCapacity, def.LocalizedName ?? "", "name", out writeError))
        {
            packet = default;
            error = writeError;
            return false;
        }
        if (!TryWriteAscii(built.DescriptionBytes, DescriptionCapacity, def.LocalizedDescription ?? "", "description", out writeError))
        {
            packet = default;
            error = writeError;
            return false;
        }
        packet = built;
        error = "";
        return true;
    }

    /// <summary>
    /// Encode a definition for the wire. Never throws: a field that will not fit is left empty
    /// and logged so a connect-loop <c>foreach</c> cannot abort later definitions. Prefer
    /// <see cref="TryFrom"/> when the caller can skip.
    /// </summary>
    public static PrefabDefinitionPacket From(PrefabDefinition def)
    {
        if (TryFrom(def, out var packet, out var error))
            return packet;
        Plugin.Logger?.LogWarning($"PrefabDefinitionPacket.From: {error}");
        return packet;
    }

    private static string ReadAscii(byte* p, int capacity)
    {
        int len = 0;
        while (len < capacity && p[len] != 0) len++;
        return Encoding.ASCII.GetString(p, len);
    }

    private static bool TryWriteAscii(byte* p, int capacity, string? value, string field, out string error)
    {
        error = "";
        var text = value ?? "";
        var bytes = Encoding.ASCII.GetBytes(text);
        if (bytes.Length >= capacity)
        {
            error = $"{field} '{value}' exceeds {capacity - 1} ASCII bytes";
            return false;
        }
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] > 0x7F)
            {
                error = $"{field} '{value}' is not ASCII";
                return false;
            }
        }
        for (int i = 0; i < capacity; i++)
            p[i] = i < bytes.Length ? bytes[i] : (byte)0;
        return true;
    }
}
