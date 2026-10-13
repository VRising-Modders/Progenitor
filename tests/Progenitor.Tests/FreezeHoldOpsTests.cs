using System;
using System.Text.Json;
using Progenitor.Dev;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

[Collection("MainThreadQueue")]
public class FreezeHoldOpsTests
{
    [Fact]
    public void Default_hold_crosses_the_watchdog_threshold_and_is_capped()
    {
        Assert.Equal(12_500, FreezeHoldOps.DefaultHoldMs(10, 2000));
        Assert.Equal(FreezeHoldOps.MaxHoldMs, FreezeHoldOps.DefaultHoldMs(20, 2000));
        Assert.Equal(2_500, FreezeHoldOps.DefaultHoldMs(0, 2000));
    }

    [Fact]
    public void Missing_synthetic_flag_refuses_and_does_not_arm()
    {
        FreezeDump.ResetForTests();
        FreezeHoldOps.ResetForTests();
        try
        {
            var result = FreezeHoldOps.Run(Json("{}"));
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal("synthetic=true is required", doc.RootElement.GetProperty("error").GetString());
            Assert.True(doc.RootElement.GetProperty("released").GetBoolean());
            Assert.False(SyntheticArmed());
        }
        finally
        {
            FreezeHoldOps.ResetForTests();
            FreezeDump.ResetForTests();
        }
    }

    [Fact]
    public void Hold_above_max_is_refused()
    {
        FreezeDump.ResetForTests();
        FreezeHoldOps.ResetForTests();
        try
        {
            var result = FreezeHoldOps.Run(Json("{\"synthetic\":true,\"holdMs\":20000}"));
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.Contains("exceeds max", doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
            Assert.False(SyntheticArmed());
        }
        finally
        {
            FreezeHoldOps.ResetForTests();
            FreezeDump.ResetForTests();
        }
    }

    [Fact]
    public void Hold_arms_synthetic_then_always_releases()
    {
        FreezeDump.ResetForTests();
        FreezeHoldOps.ResetForTests();
        bool sawArmed = false;
        FreezeHoldOps.WaitHold = _ =>
        {
            sawArmed = SyntheticArmed();
            return true;
        };
        try
        {
            var result = FreezeHoldOps.Run(Json("{\"synthetic\":true,\"holdMs\":50}"));
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("synthetic").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("released").GetBoolean());
            Assert.Equal(FreezeHoldOps.Label, doc.RootElement.GetProperty("label").GetString());
            Assert.Equal(50, doc.RootElement.GetProperty("holdMs").GetInt32());
            Assert.True(sawArmed);
            Assert.False(SyntheticArmed());
        }
        finally
        {
            FreezeHoldOps.ResetForTests();
            FreezeDump.ResetForTests();
        }
    }

    [Fact]
    public void Hold_releases_when_the_wait_throws()
    {
        FreezeDump.ResetForTests();
        FreezeHoldOps.ResetForTests();
        FreezeHoldOps.WaitHold = _ => throw new InvalidOperationException("synthetic wait boom");
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                FreezeHoldOps.Run(Json("{\"synthetic\":true,\"holdMs\":50}")));
            Assert.False(SyntheticArmed());
        }
        finally
        {
            FreezeHoldOps.ResetForTests();
            FreezeDump.ResetForTests();
        }
    }

    [Fact]
    public void Synthetic_hold_labels_the_watchdog_dump_path()
    {
        FreezeDump.ResetForTests();
        FreezeHoldOps.ResetForTests();
        FreezeDump.ArmForTests();
        MainThread.LastDrainFrame = 12;
        long now = 0;
        FreezeDump.NowMs = () => now;
        FreezeDump.WriteDump = _ => 0;
        FreezeHoldOps.WaitHold = _ =>
        {
            FreezeDump.Poll();
            now = 10_000;
            FreezeDump.Poll();
            return true;
        };
        try
        {
            FreezeHoldOps.Run(Json("{\"synthetic\":true,\"holdMs\":50}"));
            Assert.True(WaitUntil(() =>
            {
                FreezeDump.Snapshot(out _, out _, out int count, out object? last, out bool inFlight);
                return !inFlight && count == 1 && last != null;
            }));
            FreezeDump.Snapshot(
                out _,
                out _,
                out _,
                out object? last,
                out _,
                out string phase,
                out _,
                out _,
                out _,
                out string? trigger);
            Assert.Equal(FreezeDump.PhaseCompleted, phase);
            Assert.Equal(FreezeDump.TriggerWatchdog, trigger);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(last));
            Assert.True(doc.RootElement.GetProperty("synthetic").GetBoolean());
            Assert.True(doc.RootElement.GetProperty("completed").GetBoolean());
            string? path = doc.RootElement.GetProperty("path").GetString();
            Assert.Contains("synthetic-freeze-", path, StringComparison.Ordinal);
        }
        finally
        {
            FreezeHoldOps.ResetForTests();
            FreezeDump.ResetForTests();
            MainThread.LastDrainFrame = 0;
        }
    }

    private static bool SyntheticArmed()
    {
        FreezeDump.Snapshot(
            out bool _,
            out int _,
            out int _,
            out object? _,
            out bool _,
            out string _,
            out bool _,
            out object? _,
            out bool armed,
            out string? _);
        return armed;
    }

    private static bool WaitUntil(Func<bool> pred, int ms = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (pred()) return true;
            System.Threading.Thread.Sleep(10);
        }
        return pred();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
