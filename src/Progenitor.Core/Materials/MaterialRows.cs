using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Progenitor.Materials;

/// <summary>
/// Reads and writes the <c>InventoryBuffer</c> rows of the game's merged material count. Offsets and
/// stride are the verified layout constants (<see cref="Layout"/>). Game-free: spans only.
/// </summary>
internal static class MaterialRows
{
    internal static int Stride => Layout.InventoryBufferStride;

    /// <summary>Sum the rows' amounts per item type into <paramref name="into"/> (cleared first).</summary>
    internal static void Sum(ReadOnlySpan<byte> rows, int count, Dictionary<int, long> into)
    {
        into.Clear();
        for (int i = 0; i < count; i++)
        {
            var row = rows.Slice(i * Stride, Stride);
            int item = BinaryPrimitives.ReadInt32LittleEndian(row.Slice(Layout.InventoryBufferItemTypeOffset));
            int amount = BinaryPrimitives.ReadInt32LittleEndian(row.Slice(Layout.InventoryBufferAmountOffset));
            if (item == 0 || amount <= 0) continue;
            into.TryGetValue(item, out long n);
            into[item] = n + amount;
        }
    }

    /// <summary>Rows <see cref="Compose"/> will write: the bag rows plus one per extra line.</summary>
    internal static int Needed(int bagCount, int extraCount) => bagCount + extraCount;

    /// <summary>
    /// Write the bag rows verbatim (item entities and all) followed by one synthetic row per extra line:
    /// no item entity, the item type, the amount, no stack override (the shape the game's own
    /// shared-storage rows have). Returns the row count. <paramref name="output"/> must hold
    /// <see cref="Needed"/> rows.
    /// </summary>
    internal static int Compose(Span<byte> output, ReadOnlySpan<byte> bagRows, int bagCount, IReadOnlyList<MaterialLine> extra)
    {
        int n = Needed(bagCount, extra.Count);
        if (output.Length < n * Stride) throw new ArgumentException("output holds " + output.Length / Stride + " rows; " + n + " needed");
        bagRows.Slice(0, bagCount * Stride).CopyTo(output);
        for (int i = 0; i < extra.Count; i++)
        {
            var row = output.Slice((bagCount + i) * Stride, Stride);
            row.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(row.Slice(Layout.InventoryBufferItemTypeOffset), extra[i].Item);
            BinaryPrimitives.WriteInt32LittleEndian(row.Slice(Layout.InventoryBufferAmountOffset), extra[i].Amount);
        }
        return n;
    }

    /// <summary>Bytes for <paramref name="rows"/> rows.</summary>
    internal static int Bytes(int rows) => checked(rows * Stride);
}

/// <summary>
/// The development trace of one governed check (bridge <c>materials</c> <c>trace</c>): what the game's
/// own count without shared storage returned, the pool lines Progenitor appended, and the totals of the
/// rows handed to the game's check. Game-free.
/// </summary>
internal static class MaterialTrace
{
    internal static string Describe(MaterialOperation operation, Progenitor.Castles.CastleId character,
        ReadOnlySpan<byte> bagRows, int bagCount, IReadOnlyList<MaterialLine> extra, ReadOnlySpan<byte> composed, int composedCount)
    {
        var bag = new Dictionary<int, long>();
        var total = new Dictionary<int, long>();
        MaterialRows.Sum(bagRows, bagCount, bag);
        MaterialRows.Sum(composed, composedCount, total);
        var sb = new System.Text.StringBuilder();
        sb.Append("materials: trace ").Append(operation).Append(" character=").Append(character)
          .Append(" rows=").Append(composedCount).Append(" bag=");
        Append(sb, bag);
        sb.Append(" pool=");
        var lines = new List<MaterialLine>(extra);
        lines.Sort((a, b) => a.Item.CompareTo(b.Item));
        for (int i = 0; i < lines.Count; i++) sb.Append(i == 0 ? "" : ",").Append(lines[i].Item).Append(':').Append(lines[i].Amount);
        sb.Append(" total=");
        Append(sb, total);
        return sb.ToString();
    }

    private static void Append(System.Text.StringBuilder sb, Dictionary<int, long> rows)
    {
        var keys = new List<int>(rows.Keys);
        keys.Sort();
        for (int i = 0; i < keys.Count; i++) sb.Append(i == 0 ? "" : ",").Append(keys[i]).Append(':').Append(rows[keys[i]]);
    }
}
