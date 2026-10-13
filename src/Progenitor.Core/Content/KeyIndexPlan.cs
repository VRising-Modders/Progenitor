namespace Progenitor.Content;

/// <summary>
/// Derived ItemKeyLookup index: vanilla prefix length plus position in the mod-row ledger.
/// Absolute indices are not saved; the save stores the prefab guid.
/// </summary>
internal static class KeyIndexPlan
{
    /// <summary><c>index = baseLength + position</c>. Negative inputs stay negative so a
    /// caller can tell a refused row from index 0.</summary>
    public static int Index(int baseLength, int position)
    {
        if (baseLength < 0 || position < 0) return -1;
        return baseLength + position;
    }

    /// <summary>True when a live append landed at the index this plan predicted.</summary>
    public static bool MatchesPredicted(int predicted, int actual) =>
        predicted >= 0 && actual == predicted;
}
