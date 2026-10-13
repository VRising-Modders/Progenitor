using System;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Policy for debug-bridge entity queries: refuse an unconstrained scan unless the caller
/// opts in, stop after <c>limit</c> rows unless a total is requested, and never walk more
/// than <see cref="HardScanCap"/> entities. Kept free of ECS types so tests can cover it.
/// </summary>
internal static class QueryArgs
{
    /// <summary>
    /// Absolute ceiling on entities visited in one query. A PrefabGUID query can still match
    /// hundreds of thousands of entities; walking past this on the Unity main thread freezes the sim.
    /// </summary>
    public const int HardScanCap = 200_000;

    public const int DefaultLimit = 50;
    public const int MaxLimit = 2_000;

    /// <summary>
    /// Scan/row limits derived from a query request. <see cref="Continue"/> is the loop
    /// condition; <see cref="IsTruncated"/> is the result flag for hitting the hard cap.
    /// </summary>
    public readonly struct Limits
    {
        public readonly int RowLimit;
        public readonly bool WantTotal;
        public readonly int ScanCap;

        public Limits(int rowLimit, bool wantTotal, int scanCap = HardScanCap)
        {
            RowLimit = rowLimit;
            WantTotal = wantTotal;
            ScanCap = scanCap;
        }

        /// <summary>
        /// Keep walking the match set? <paramref name="visited"/> is entities already looked at;
        /// <paramref name="accepted"/> is rows that passed the near filter (and count toward limit/total).
        /// </summary>
        public bool Continue(int visited, int accepted)
        {
            if (visited >= ScanCap) return false;
            if (!WantTotal && accepted >= RowLimit) return false;
            return true;
        }

        /// <summary>
        /// True only when the hard scan cap stopped us before the match set was exhausted.
        /// Stopping at <see cref="RowLimit"/> because the caller did not ask for a total is not truncation.
        /// </summary>
        public bool IsTruncated(int visited, int matchCount) =>
            visited >= ScanCap && visited < matchCount;
    }

    /// <summary>
    /// An empty <c>all</c> list with IncludeDisabledEntities matches every entity in the world.
    /// Refuse that unless the caller set <paramref name="allowUnbounded"/>.
    /// </summary>
    public static void RequireAllFilter(int allComponentCount, bool allowUnbounded)
    {
        if (allComponentCount <= 0 && !allowUnbounded)
        {
            throw new ArgumentException(
                "query requires at least one args.all component unless args.allowUnbounded is true; an empty all list matches every entity and stalls the simulation");
        }
    }

    public static int ClampLimit(int limit) => Math.Clamp(limit, 1, MaxLimit);
}
