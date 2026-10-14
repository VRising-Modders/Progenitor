using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Asserts that work grows sub-quadratically with its input, without an absolute time budget that a slow or busy CI
/// runner can blow. The work runs at size n and 4n: linear work takes about 4x as long, quadratic work about 16x.
/// Each size is timed as the best of several runs after a warm-up, and a high ratio is measured again before the
/// assertion fails, so one preempted run cannot fail it while a real quadratic regression fails every attempt.
/// </summary>
internal static class ScalingAssert
{
    /// <summary>Default ceiling for t(4n) / t(n): well above linear (4) and n log n (~4.5), well below quadratic (16).</summary>
    internal const double DefaultMaxRatio = 9.0;

    /// <summary>
    /// <paramref name="prepare"/> builds the input for a size outside the timer and returns the work to time.
    /// <paramref name="n"/> should make the small run take at least a few milliseconds, so timer resolution and
    /// fixed costs do not dominate the ratio.
    /// </summary>
    internal static void SubQuadratic(string label, Func<int, Action> prepare, int n, double maxRatio = DefaultMaxRatio,
        int attempts = 3, int runsPerSize = 3)
    {
        Action small = prepare(n);
        Action large = prepare(checked(n * 4));
        for (int i = 0; i < runsPerSize; i++) small(); // warm-up: JIT tiers, regex caches, first-touch allocation
        var ratios = new List<string>();
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            // Large first: if the code is still warming up, the inflated time lands on the large size and can only
            // fail an attempt (which is retried), never pass a quadratic one by inflating the small size.
            double tLarge = BestOf(large, runsPerSize);
            double tSmall = BestOf(small, runsPerSize);
            double ratio = tLarge / Math.Max(tSmall, 0.001);
            ratios.Add(string.Format(CultureInfo.InvariantCulture, "{0:0.0}x ({1:0.#} ms -> {2:0.#} ms)", ratio, tSmall, tLarge));
            if (ratio <= maxRatio) return;
        }
        Assert.Fail(label + ": time grew faster than n log n when the input grew 4x (limit "
            + maxRatio.ToString(CultureInfo.InvariantCulture) + "x): " + string.Join("; ", ratios));
    }

    private static double BestOf(Action work, int runs)
    {
        double best = double.MaxValue;
        for (int i = 0; i < runs; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long t0 = Stopwatch.GetTimestamp();
            work();
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            best = Math.Min(best, ms);
        }
        return best;
    }
}
