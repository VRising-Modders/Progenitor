using System;
using System.Buffers.Binary;

namespace Progenitor.Castles;

/// <summary>
/// A castle heart's level and fuel facts (<see cref="ICastleHandle.HeartFuel"/>). The unlocked
/// slot count is the game's own per-level table, <c>CastleHeartLevelBlobData.ItemSlots</c> in the
/// heart's <c>CastleHeart.Data</c> blob, for the heart's current <c>CastleHeart.Level</c>: not a
/// default. Read live.
/// </summary>
public sealed class CastleHeartFuel
{
    /// <summary>True when the heart and its level table were read.</summary>
    public bool Ok => Reason.Length == 0;

    /// <summary>Why the read failed; empty when it worked.</summary>
    public string Reason { get; init; } = "";

    /// <summary><c>CastleHeart.Level</c>.</summary>
    public int Level { get; init; }

    /// <summary>
    /// <c>ItemSlots</c> of the level-table row for <see cref="Level"/>: how many of the heart's
    /// item slots (the blood-essence fuel pads) are unlocked at this level. -1 when the table
    /// could not be read.
    /// </summary>
    public int UnlockedSlots { get; init; } = -1;

    /// <summary>Rows in the heart's level table (the highest level the table describes).</summary>
    public int LevelRows { get; init; }

    /// <summary><c>CastleHeart.FuelQuantity</c>.</summary>
    public int FuelQuantity { get; init; }

    /// <inheritdoc />
    public override string ToString() =>
        Ok ? "level " + Level + ", " + UnlockedSlots + " unlocked slot(s), fuel " + FuelQuantity
           : "heart fuel unreadable: " + Reason;

    /// <summary>A failed read.</summary>
    public static CastleHeartFuel Fail(string reason) => new() { Reason = reason ?? "unreadable" };
}

/// <summary>Game-free decode of the heart level table, against the metadata layout constants.</summary>
internal static class CastleHeartLevels
{
    /// <summary>One level row: <c>RaidCosts</c> is the last field and a BlobArray is 8 bytes.</summary>
    public static int RowStride => Layout.CastleHeartLevelBlobRaidCostsOffset + 8;

    /// <summary>
    /// Find the row whose <c>Level</c> equals <paramref name="level"/> in the blob (the bytes the
    /// heart's <c>Data</c> pointer points at, <paramref name="blob"/>.Length = the blob's own
    /// length) and return its <c>ItemSlots</c>. Falls back to row index <c>level - 1</c> only when
    /// no row names the level and that index exists. False when the array header is out of range.
    /// </summary>
    public static bool TryItemSlots(ReadOnlySpan<byte> blob, int level, out int itemSlots, out int rows)
    {
        itemSlots = -1;
        rows = 0;
        int at = Layout.CastleHeartBlobLevelsOffset;
        if (at < 0 || at + 8 > blob.Length) return false;
        int rel = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(at, 4));
        int len = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(at + 4, 4));
        if (len <= 0 || len > 256) return false;
        long start = (long)at + rel;
        int stride = RowStride;
        if (start < 0 || start + (long)len * stride > blob.Length) return false;
        rows = len;
        for (int i = 0; i < len; i++)
        {
            int row = (int)start + i * stride;
            int rowLevel = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(row + Layout.CastleHeartLevelBlobLevelOffset, 4));
            if (rowLevel != level) continue;
            itemSlots = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(row + Layout.CastleHeartLevelBlobItemSlotsOffset, 4));
            return true;
        }

        if (level >= 1 && level <= len)
        {
            int row = (int)start + (level - 1) * stride;
            itemSlots = BinaryPrimitives.ReadInt32LittleEndian(blob.Slice(row + Layout.CastleHeartLevelBlobItemSlotsOffset, 4));
            return true;
        }

        return false;
    }
}
