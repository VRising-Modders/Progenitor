using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Progenitor.Hooks;

/// <summary>
/// Immutable set of ECS system type indices. <see cref="SystemHooks"/> rebuilds one whenever a
/// hook is added or removed and publishes it with <c>Volatile.Write</c> so the Harmony prefix on
/// every system update can reject an unhooked system with no lock and no allocation.
///
/// 0 is never a member: Unity's TypeManager uses 0 as "not a system". The table is open-addressed
/// with linear probing and at least half the slots empty, so a miss always hits a 0 and returns
/// without scanning the whole array.
/// </summary>
internal sealed class HookedIndexSet
{
    /// <summary>Shared empty instance so a process with no hooks does not allocate a table.</summary>
    public static readonly HookedIndexSet Empty = new(Array.Empty<int>(), mask: 0, count: 0);

    // Fibonacci hash: sequential TypeManager indices (1, 2, 3, ...) must not share a probe start.
    private const uint Golden = 2654435769u;

    private readonly int[] _slots;
    private readonly int _mask;
    private readonly int _count;

    private HookedIndexSet(int[] slots, int mask, int count)
    {
        _slots = slots;
        _mask = mask;
        _count = count;
    }

    /// <summary>Number of distinct non-zero indices. 0 means every <see cref="Contains"/> is false.</summary>
    public int Count => _count;

    /// <summary>
    /// Build a set from <paramref name="typeIndices"/>. Null, empty, 0, and duplicates are ignored
    /// so a registration snapshot can pass <c>Dictionary.Keys</c> without a pre-pass.
    /// </summary>
    public static HookedIndexSet Create(IEnumerable<int>? typeIndices)
    {
        if (typeIndices == null) return Empty;

        List<int>? unique = null;
        foreach (int index in typeIndices)
        {
            if (index == 0) continue;
            unique ??= new List<int>();
            if (unique.Contains(index)) continue;
            unique.Add(index);
        }

        if (unique == null || unique.Count == 0) return Empty;
        return FromUnique(unique);
    }

    /// <summary>
    /// True if <paramref name="typeIndex"/> was in the set at construction. Never allocates.
    /// A 0 or an empty set returns false without probing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(int typeIndex)
    {
        if (typeIndex == 0 || _count == 0) return false;

        int[] slots = _slots;
        int mask = _mask;
        int i = Mix(typeIndex) & mask;
        while (true)
        {
            int v = slots[i];
            if (v == typeIndex) return true;
            if (v == 0) return false;
            i = (i + 1) & mask;
        }
    }

    private static HookedIndexSet FromUnique(List<int> unique)
    {
        int count = unique.Count;
        int cap = 4;
        while (cap < count * 2) cap <<= 1;

        var slots = new int[cap];
        int mask = cap - 1;
        for (int n = 0; n < count; n++)
        {
            int value = unique[n];
            int i = Mix(value) & mask;
            while (slots[i] != 0) i = (i + 1) & mask;
            slots[i] = value;
        }

        return new HookedIndexSet(slots, mask, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Mix(int typeIndex) => (int)((uint)typeIndex * Golden);
}
