namespace Progenitor.Dev.Ops;

internal static class StressTransferRules
{
    internal static bool Protected(int plot) => StressProtectedPlots.Contains(plot);
    internal static bool AllowedIdentity(ulong id) => id >= 90000000000020010UL && id <= 90000000000020099UL;

    // TypeSize can be unavailable through interop. The field layout is verified separately.
    internal static bool CanReadCount(System.Func<int> readSize, int requiredBytes)
    {
        int size;
        try { size = readSize(); }
        catch { size = 0; }
        return size == 0 || size >= requiredBytes;
    }

    internal static string? Refusal(int plot, bool oldIsStress, bool allowedTarget,
        bool complete, bool offline, bool noClan, bool links, int owned, int? heartCount, int limit)
    {
        if (Protected(plot) || !oldIsStress) return "not_stress_castle";
        if (!allowedTarget) return "target_identity";
        if (!complete) return "target_shape";
        if (!offline) return "connected";
        if (!noClan) return "clan_not_supported";
        if (!links) return "team_links";
        if (heartCount != owned) return "heart_count_mismatch";
        if (limit <= 0 || owned >= limit) return "castle_limit";
        return null;
    }
}
