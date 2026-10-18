namespace Progenitor.Dev.Ops;

internal static class StressTransferRules
{
    internal static bool Protected(int plot) => plot is 45 or 67 or 86 or 92 or 125 or 129 or 136;
    internal static bool AllowedIdentity(ulong id) => StressIdentity.IsStressPlatformId(id)
        || (id >= 90000000000020001UL && id <= 90000000000020099UL);

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
