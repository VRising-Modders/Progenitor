using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Progenitor.Content;

/// <summary>
/// Tells a connected client to drop a cloned-item definition, or (with
/// <see cref="KeyPlaceholderEventId"/>) to keep that GUID's ItemKeyLookup row. Fixed-size and
/// blittable so it can ride <see cref="Progenitor.Networking.CustomEvents"/> without a serializer.
/// Layout is the GUID int32 then the same 64-byte ASCII id used on the definition packet
/// (63 chars + NUL). Distinct event ids from <see cref="PrefabDefinitionPacket"/> so a definition,
/// an unregister, and a key-table placeholder cannot be mistaken for each other.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal unsafe struct PrefabUnregisterPacket
{
    /// <summary>'PrfU' — large and unused by the game; not <see cref="PrefabDefinitionPacket.EventId"/> ('PrfD').</summary>
    public const int EventId = 0x50726655;

    /// <summary>
    /// 'PrfK' — ItemKeyLookup placeholder. Same layout as this packet; a distinct event id so the
    /// client appends the GUID row without registering or unapplying a prefab. Join-time validation
    /// hashes the whole table, so a GUID that stayed on the server after unregister must occupy a
    /// row on every later joiner too.
    /// </summary>
    public const int KeyPlaceholderEventId = 0x5072664B;

    public const int IdCapacity = PrefabDefinitionPacket.IdCapacity;

    public int NewGuid;
    public fixed byte IdBytes[IdCapacity];

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
                WriteAscii(p, IdCapacity, value);
        }
    }

    public static PrefabUnregisterPacket From(PrefabDefinition def)
    {
        var packet = new PrefabUnregisterPacket { NewGuid = def.Guid.GuidHash };
        packet.Id = def.Id;
        return packet;
    }

    /// <summary>Same layout as <see cref="From"/>, used with <see cref="KeyPlaceholderEventId"/>.</summary>
    public static PrefabUnregisterPacket Placeholder(int guidHash, string? id)
    {
        var packet = new PrefabUnregisterPacket { NewGuid = guidHash };
        packet.Id = id ?? "";
        return packet;
    }

    private static string ReadAscii(byte* p, int capacity)
    {
        int len = 0;
        while (len < capacity && p[len] != 0) len++;
        return Encoding.ASCII.GetString(p, len);
    }

    private static void WriteAscii(byte* p, int capacity, string? value)
    {
        var bytes = Encoding.ASCII.GetBytes(value ?? "");
        if (bytes.Length >= capacity)
            throw new ArgumentOutOfRangeException(nameof(value), $"id '{value}' exceeds {capacity - 1} ASCII bytes");
        for (int i = 0; i < capacity; i++)
            p[i] = i < bytes.Length ? bytes[i] : (byte)0;
    }
}
