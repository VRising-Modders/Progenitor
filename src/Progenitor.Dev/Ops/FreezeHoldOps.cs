using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Main-thread <c>freeze_hold</c> op. Occupies the Unity main thread for a bounded,
/// explicitly-released interval so the freeze watchdog can fire on a synthetic stall.
/// Requires <c>synthetic: true</c>. Never unbounded. A timer plus <c>finally</c> always
/// release the synthetic label, even if the wait throws.
/// </summary>
internal static class FreezeHoldOps
{
    internal const int MaxHoldMs = 15_000;
    internal const string Label = "synthetic";

    /// <summary>Test hook: replace to avoid a real wait. Production waits up to <paramref name="ms"/>.</summary>
    internal static Func<int, bool> WaitHold = WaitBounded;

#if PROGENITOR_TESTS
    internal static void ResetForTests() => WaitHold = WaitBounded;
#endif

    internal static int DefaultHoldMs(int freezeSeconds, int pollMs)
    {
        if (freezeSeconds <= 0) return Math.Min(pollMs + 500, MaxHoldMs);
        long hold = freezeSeconds * 1000L + pollMs + 500L;
        if (hold < 1) hold = 1;
        if (hold > MaxHoldMs) hold = MaxHoldMs;
        return (int)hold;
    }

    internal static object Run(JsonElement args)
    {
        if (!ReadSynthetic(args))
        {
            return new
            {
                ok = false,
                error = "synthetic=true is required",
                synthetic = true,
                released = true,
                label = Label,
            };
        }

        int requested = ReadHoldMs(args);
        int holdMs = requested > 0
            ? requested
            : DefaultHoldMs(FreezeDump.FreezeSeconds, FreezeDump.PollMs);
        if (holdMs > MaxHoldMs)
        {
            return new
            {
                ok = false,
                error = "holdMs exceeds max " + MaxHoldMs.ToString(CultureInfo.InvariantCulture),
                synthetic = true,
                released = true,
                holdMs,
                maxHoldMs = MaxHoldMs,
                label = Label,
            };
        }
        if (holdMs < 1)
        {
            return new
            {
                ok = false,
                error = "holdMs must be at least 1",
                synthetic = true,
                released = true,
                label = Label,
            };
        }

        FreezeDump.BeginSyntheticCapture();
        bool released = false;
        long heldMs = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            WaitHold(holdMs);
            heldMs = sw.ElapsedMilliseconds;
        }
        finally
        {
            heldMs = sw.ElapsedMilliseconds;
            FreezeDump.EndSyntheticCapture();
            released = true;
        }

        return new
        {
            ok = true,
            synthetic = true,
            released,
            holdMs,
            heldMs,
            maxHoldMs = MaxHoldMs,
            label = Label,
        };
    }

    private static bool WaitBounded(int ms)
    {
        using var done = new ManualResetEventSlim(false);
        using var timer = new Timer(_ =>
        {
            try { done.Set(); }
            catch { }
        }, null, ms, Timeout.Infinite);
        return done.Wait(ms);
    }

    private static bool ReadSynthetic(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return false;
        if (!args.TryGetProperty("synthetic", out var el)) return false;
        return el.ValueKind == JsonValueKind.True;
    }

    private static int ReadHoldMs(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return 0;
        if (!args.TryGetProperty("holdMs", out var el) || el.ValueKind != JsonValueKind.Number)
            return 0;
        if (!el.TryGetInt32(out int n)) return 0;
        return n;
    }
}
