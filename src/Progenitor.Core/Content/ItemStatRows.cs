using System;
using System.Buffers.Binary;

namespace Progenitor.Content;

/// <summary>Builds a fresh authored buffer solely from vanilla donor bytes and current bonuses.</summary>
internal static class ItemStatRows
{
    internal const int Stride = Layout.ItemStatRowStride;
    internal const int MaxRows = 4096;

    internal static byte[] Build(byte[] inherited, ItemStatBonuses bonuses)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        ArgumentNullException.ThrowIfNull(bonuses);
        if (inherited.Length % Stride != 0)
            throw new ArgumentException("Donor stat bytes do not contain complete native rows.", nameof(inherited));
        int bonusRows = (bonuses.MaxHealth != 0 ? 1 : 0) + (bonuses.PhysicalPower != 0 ? 1 : 0) + (bonuses.SpellPower != 0 ? 1 : 0);
        if (inherited.Length / Stride + bonusRows > MaxRows)
            throw new ArgumentException("Authored item stat row count exceeds the guarded limit.", nameof(inherited));
        var authored = new byte[checked(inherited.Length + bonusRows * Stride)];
        inherited.CopyTo(authored, 0);
        int offset = inherited.Length;
        Add(Layout.ItemStatMaxHealth, bonuses.MaxHealth);
        Add(Layout.ItemStatPhysicalPower, bonuses.PhysicalPower);
        Add(Layout.ItemStatSpellPower, bonuses.SpellPower);
        return authored;

        void Add(int stat, float value)
        {
            if (value == 0) return;
            Span<byte> row = authored.AsSpan(offset, Stride);
            BinaryPrimitives.WriteInt32LittleEndian(row.Slice(Layout.ItemStatCapOffset), Layout.ItemStatUncapped);
            row[Layout.ItemStatTypeOffset] = checked((byte)stat);
            row[Layout.ItemStatOperationOffset] = checked((byte)Layout.ItemStatAdd);
            BinaryPrimitives.WriteInt32LittleEndian(row.Slice(Layout.ItemStatValueOffset), BitConverter.SingleToInt32Bits(value));
            // Native ModifyUnitStats.Apply computes Modifier * Value before registering the row.
            BinaryPrimitives.WriteInt32LittleEndian(row.Slice(Layout.ItemStatModifierOffset), BitConverter.SingleToInt32Bits(1));
            // Id starts empty and is passed by address to the native modification registry.
            offset += Stride;
        }
    }
}
