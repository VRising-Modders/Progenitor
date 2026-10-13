using System;
using System.Collections.Generic;

namespace Progenitor.Content;

/// <summary>Builds replacement item tables without touching native memory.</summary>
internal sealed class ItemKeyCapacityPlan
{
    internal const int MaximumCapacity = 16384;

    public int TargetCapacity { get; }
    public int[] ListHashes { get; }
    // Values, keys, next links, then twice as many buckets; all offsets are in ints.
    public int[] MapData { get; }

    private ItemKeyCapacityPlan(int capacity, int[] hashes, int[] mapData)
    {
        TargetCapacity = capacity;
        ListHashes = hashes;
        MapData = mapData;
    }

    public static bool TryBuild(IReadOnlyList<int> hashes, int currentListCapacity,
        int currentMapCapacity, int required, out ItemKeyCapacityPlan? plan, out string error)
    {
        plan = null;
        error = string.Empty;
        if (hashes == null)
        {
            error = "Item hashes are missing.";
            return false;
        }
        if (!ValidCapacity(currentListCapacity) || !ValidCapacity(currentMapCapacity))
        {
            error = "Current capacities must be positive powers of two within the experimental limit.";
            return false;
        }
        if (required < 0 || hashes.Count < 0 || hashes.Count > currentListCapacity
            || hashes.Count > currentMapCapacity)
        {
            error = "Item counts do not fit the current capacities.";
            return false;
        }
        if (required > MaximumCapacity)
        {
            error = $"Required item capacity exceeds the experimental limit of {MaximumCapacity} rows.";
            return false;
        }

        var seen = new HashSet<int>();
        var snapshot = new int[hashes.Count];
        for (int i = 0; i < snapshot.Length; i++)
        {
            int hash = hashes[i];
            if (!seen.Add(hash))
            {
                error = $"Duplicate item GUID hash at row {i}.";
                return false;
            }
            snapshot[i] = hash;
        }
        if (required <= Math.Min(currentListCapacity, currentMapCapacity))
            return true;

        int wanted = Math.Max(required, Math.Max(currentListCapacity, currentMapCapacity));
        int capacity = 1;
        while (capacity < wanted) capacity <<= 1;
        var map = new int[capacity * 5];
        Array.Fill(map, -1, capacity * 2, capacity * 3);
        int mask = capacity * 2 - 1;
        for (int i = 0; i < snapshot.Length; i++)
        {
            int bucketOffset = capacity * 3 + (snapshot[i] & mask);
            map[i] = i;
            map[capacity + i] = snapshot[i];
            map[capacity * 2 + i] = map[bucketOffset];
            map[bucketOffset] = i;
        }
        plan = new ItemKeyCapacityPlan(capacity, snapshot, map);
        return true;
    }

    private static bool ValidCapacity(int value) => value > 0 && value <= MaximumCapacity
        && (value & (value - 1)) == 0;
}
