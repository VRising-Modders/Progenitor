using System;
using System.Collections.Generic;

namespace Progenitor.Content;

/// <summary>
/// Element strides and field offsets from the server metadata for the four recipe buffers and the
/// workstation recipe list. Writes and reads go through these constants, never
/// <c>sizeof</c> and never <c>TypeManager</c> sizes. <see cref="Layout"/> is the checked-in copy.
/// </summary>
internal static class RecipeBufferLayout
{
    public const int RequirementGuidOffset = Layout.RecipeRequirementBufferGuidOffset;
    public const int RequirementAmountOffset = Layout.RecipeRequirementBufferAmountOffset;
    public const int RequirementStride = Layout.RecipeRequirementBufferStride;

    public const int OutputGuidOffset = Layout.RecipeOutputBufferGuidOffset;
    public const int OutputAmountOffset = Layout.RecipeOutputBufferAmountOffset;
    public const int OutputStride = Layout.RecipeOutputBufferStride;

    public const int OutputUnitGuidOffset = Layout.RecipeOutputUnitBufferGuidOffset;
    public const int OutputUnitStacksOffset = Layout.RecipeOutputUnitBufferStacksOffset;
    public const int OutputUnitStride = Layout.RecipeOutputUnitBufferStride;

    public const int LinkGuidOffset = Layout.RecipeLinkBufferGuidOffset;
    public const int LinkStride = Layout.RecipeLinkBufferStride;

    public const int WorkstationRecipeGuidOffset = Layout.WorkstationRecipesBufferRecipeGuidOffset;
    public const int WorkstationStride = Layout.WorkstationRecipesBufferStride;

    public const int MaxElements = 64;

    public static int RequirementBytes(int count) => checked(count * RequirementStride);

    public static int OutputBytes(int count) => checked(count * OutputStride);

    public static int OutputUnitBytes(int count) => checked(count * OutputUnitStride);

    public static int LinkBytes(int count) => checked(count * LinkStride);

    public static int WorkstationBytes(int count) => checked(count * WorkstationStride);

    public static void WriteRequirement(byte[] storage, int index, int guidHash, int amount)
    {
        int at = checked(index * RequirementStride);
        WriteInt32(storage, at + RequirementGuidOffset, guidHash);
        WriteInt32(storage, at + RequirementAmountOffset, amount);
    }

    public static bool TryReadRequirement(byte[] storage, int index, out int guidHash, out int amount)
    {
        guidHash = 0;
        amount = 0;
        int at = checked(index * RequirementStride);
        return TryReadInt32(storage, at + RequirementGuidOffset, out guidHash)
            && TryReadInt32(storage, at + RequirementAmountOffset, out amount);
    }

    public static void WriteOutput(byte[] storage, int index, int guidHash, int amount)
    {
        int at = checked(index * OutputStride);
        WriteInt32(storage, at + OutputGuidOffset, guidHash);
        WriteInt32(storage, at + OutputAmountOffset, amount);
    }

    public static bool TryReadOutput(byte[] storage, int index, out int guidHash, out int amount)
    {
        guidHash = 0;
        amount = 0;
        int at = checked(index * OutputStride);
        return TryReadInt32(storage, at + OutputGuidOffset, out guidHash)
            && TryReadInt32(storage, at + OutputAmountOffset, out amount);
    }

    public static void WriteOutputUnit(byte[] storage, int index, int guidHash, int stacks)
    {
        int at = checked(index * OutputUnitStride);
        WriteInt32(storage, at + OutputUnitGuidOffset, guidHash);
        WriteInt32(storage, at + OutputUnitStacksOffset, stacks);
    }

    public static bool TryReadOutputUnit(byte[] storage, int index, out int guidHash, out int stacks)
    {
        guidHash = 0;
        stacks = 0;
        int at = checked(index * OutputUnitStride);
        return TryReadInt32(storage, at + OutputUnitGuidOffset, out guidHash)
            && TryReadInt32(storage, at + OutputUnitStacksOffset, out stacks);
    }

    public static void WriteLink(byte[] storage, int index, int guidHash)
    {
        int at = checked(index * LinkStride);
        WriteInt32(storage, at + LinkGuidOffset, guidHash);
    }

    public static bool TryReadLink(byte[] storage, int index, out int guidHash) =>
        TryReadInt32(storage, checked(index * LinkStride) + LinkGuidOffset, out guidHash);

    public static void WriteWorkstationRow(byte[] storage, int index, int recipeGuidHash)
    {
        int at = checked(index * WorkstationStride);
        WriteInt32(storage, at + WorkstationRecipeGuidOffset, recipeGuidHash);
    }

    public static bool TryReadWorkstationRow(byte[] storage, int index, out int recipeGuidHash) =>
        TryReadInt32(storage, checked(index * WorkstationStride) + WorkstationRecipeGuidOffset, out recipeGuidHash);

    /// <summary>
    /// Walk leftover unit/link rows at the dump stride and return a truncated empty buffer.
    /// Used after a clone so a source's unit or link rows cannot leak onto the new recipe.
    /// </summary>
    public static byte[] ClearElements(byte[] source, int stride, int guidOffset)
    {
        if (source == null || source.Length == 0) return Array.Empty<byte>();
        if (stride <= 0 || source.Length % stride != 0) return Array.Empty<byte>();
        int n = source.Length / stride;
        for (int i = 0; i < n; i++)
            TryReadInt32(source, (i * stride) + guidOffset, out _);
        return Array.Empty<byte>();
    }

    public static byte[] EncodeRequirements(IReadOnlyList<int> guidHashes, IReadOnlyList<int> amounts)
    {
        if (guidHashes == null) throw new ArgumentNullException(nameof(guidHashes));
        if (amounts == null) throw new ArgumentNullException(nameof(amounts));
        if (guidHashes.Count != amounts.Count)
            throw new ArgumentException("Requirement guid and amount counts must match.");
        var storage = new byte[RequirementBytes(guidHashes.Count)];
        for (int i = 0; i < guidHashes.Count; i++)
            WriteRequirement(storage, i, guidHashes[i], amounts[i]);
        return storage;
    }

    public static byte[] EncodeOutputs(int guidHash, int amount)
    {
        var storage = new byte[OutputBytes(1)];
        WriteOutput(storage, 0, guidHash, amount);
        return storage;
    }

    public static byte[] EncodeWorkstationRows(IReadOnlyList<int> recipeGuidHashes)
    {
        if (recipeGuidHashes == null) throw new ArgumentNullException(nameof(recipeGuidHashes));
        var storage = new byte[WorkstationBytes(recipeGuidHashes.Count)];
        for (int i = 0; i < recipeGuidHashes.Count; i++)
            WriteWorkstationRow(storage, i, recipeGuidHashes[i]);
        return storage;
    }

    public static int[] DecodeWorkstationRows(byte[] storage)
    {
        if (storage == null || storage.Length == 0) return Array.Empty<int>();
        if (storage.Length % WorkstationStride != 0)
            throw new ArgumentException("Workstation buffer length is not a multiple of the dump stride.");
        int n = storage.Length / WorkstationStride;
        var rows = new int[n];
        for (int i = 0; i < n; i++)
        {
            if (!TryReadWorkstationRow(storage, i, out rows[i]))
                throw new ArgumentException("Workstation row " + i + " is unreadable.");
        }
        return rows;
    }

#if !PROGENITOR_TESTS
    public static unsafe void WriteRequirement(byte* raw, int index, int guidHash, int amount)
    {
        byte* row = raw + (index * RequirementStride);
        *(int*)(row + RequirementGuidOffset) = guidHash;
        *(int*)(row + RequirementAmountOffset) = amount;
    }

    public static unsafe bool TryReadRequirement(byte* raw, int index, out int guidHash, out int amount)
    {
        byte* row = raw + (index * RequirementStride);
        guidHash = *(int*)(row + RequirementGuidOffset);
        amount = *(int*)(row + RequirementAmountOffset);
        return true;
    }

    public static unsafe void WriteOutput(byte* raw, int index, int guidHash, int amount)
    {
        byte* row = raw + (index * OutputStride);
        *(int*)(row + OutputGuidOffset) = guidHash;
        *(int*)(row + OutputAmountOffset) = amount;
    }

    public static unsafe bool TryReadOutput(byte* raw, int index, out int guidHash, out int amount)
    {
        byte* row = raw + (index * OutputStride);
        guidHash = *(int*)(row + OutputGuidOffset);
        amount = *(int*)(row + OutputAmountOffset);
        return true;
    }

    public static unsafe bool TryReadOutputUnit(byte* raw, int index, out int guidHash, out int stacks)
    {
        byte* row = raw + (index * OutputUnitStride);
        guidHash = *(int*)(row + OutputUnitGuidOffset);
        stacks = *(int*)(row + OutputUnitStacksOffset);
        return true;
    }

    public static unsafe bool TryReadLink(byte* raw, int index, out int guidHash)
    {
        guidHash = *(int*)(raw + (index * LinkStride) + LinkGuidOffset);
        return true;
    }
#endif

    private static void WriteInt32(byte[] storage, int offset, int value)
    {
        if (storage == null) throw new ArgumentNullException(nameof(storage));
        if ((uint)offset > storage.Length - 4)
            throw new ArgumentOutOfRangeException(nameof(offset));
        storage[offset] = (byte)value;
        storage[offset + 1] = (byte)(value >> 8);
        storage[offset + 2] = (byte)(value >> 16);
        storage[offset + 3] = (byte)(value >> 24);
    }

    private static bool TryReadInt32(byte[] storage, int offset, out int value)
    {
        value = 0;
        if (storage == null || (uint)offset > storage.Length - 4) return false;
        value = storage[offset]
            | (storage[offset + 1] << 8)
            | (storage[offset + 2] << 16)
            | (storage[offset + 3] << 24);
        return true;
    }
}
