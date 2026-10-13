using System.Collections.Generic;

namespace Progenitor.Units;

/// <summary>What the next Core generation does with one handed-off despawn.</summary>
internal enum DespawnResume
{
    /// <summary>Despawn it again: it still exists, is not being destroyed, and has the same prefab.</summary>
    Resume = 0,

    /// <summary>Nothing to do: the entity is gone or the game is already destroying it.</summary>
    Gone = 1,

    /// <summary>Leave it alone: the id now names an entity of another prefab.</summary>
    OtherPrefab = 2,
}

/// <summary>
/// Game-free half of the Units despawn hand-off across a Core swap. The live halves are
/// <c>Units.Uninstall</c> (hand-off), <c>Units.Install</c> (resume) and Bootstrap's
/// <c>DespawnHandoffBook</c>, which drops rows from a replaced or reloaded world before Core
/// sees them.
/// </summary>
internal static class DespawnHandoffPolicy
{
    /// <summary>Ints per row exchanged with Bootstrap: index, version, PrefabGUID hash.</summary>
    internal const int Stride = 3;

    /// <summary>The decision for one row, from what the world says about its entity now.</summary>
    internal static DespawnResume Decide(bool exists, bool beingDestroyed, bool samePrefab)
    {
        if (!exists || beingDestroyed) return DespawnResume.Gone;
        return samePrefab ? DespawnResume.Resume : DespawnResume.OtherPrefab;
    }

    /// <summary>Flatten (index, version, prefab) rows into the array Bootstrap takes.</summary>
    internal static int[] Pack(IReadOnlyList<(int Index, int Version, int Prefab)> rows)
    {
        var packed = new int[rows.Count * Stride];
        for (int i = 0; i < rows.Count; i++)
        {
            packed[i * Stride] = rows[i].Index;
            packed[i * Stride + 1] = rows[i].Version;
            packed[i * Stride + 2] = rows[i].Prefab;
        }

        return packed;
    }

    /// <summary>Rows in a packed array; a trailing partial row is ignored.</summary>
    internal static int RowCount(int[]? packed) => packed == null ? 0 : packed.Length / Stride;

    internal static string FormatHandOff(int pending, int accepted) =>
        accepted < 0
            ? "Units: " + pending + " despawn(s) still in flight at uninstall; this Bootstrap cannot hold them, so those units stay in the world"
            : "Units: handed " + accepted + " of " + pending + " despawn(s) still in flight to Bootstrap for the next Core generation";

    internal static string FormatResume(int resumed, int gone, int otherPrefab, int refused, int stale) =>
        "Units: resumed " + resumed + " despawn(s) handed over by the previous Core generation (already gone "
        + gone + ", id now another prefab " + otherPrefab + ", refused " + refused
        + ", dropped by Bootstrap for a reloaded or replaced world " + stale + ")";
}
