using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using Progenitor.Dev;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

[Collection("MainThreadQueue")]
public class FreezeDumpTests
{
    [Fact]
    public void Advancing_frames_never_dump()
    {
        var state = default(FreezeWatchState);
        Assert.False(FreezeDump.ShouldDump(ref state, 10, 0, 10, out _));
        Assert.False(FreezeDump.ShouldDump(ref state, 11, 2_000, 10, out _));
        Assert.False(FreezeDump.ShouldDump(ref state, 12, 20_000, 10, out _));
        Assert.Equal(0, state.DumpsWritten);
        Assert.True(state.Armed);
    }

    [Fact]
    public void Frozen_ten_seconds_dumps_once()
    {
        var state = default(FreezeWatchState);
        Assert.False(FreezeDump.ShouldDump(ref state, 12, 0, 10, out _));
        Assert.False(FreezeDump.ShouldDump(ref state, 12, 9_999, 10, out _));
        Assert.True(FreezeDump.ShouldDump(ref state, 12, 10_000, 10, out long frozenMs));
        Assert.Equal(10_000, frozenMs);
        Assert.Equal(1, state.DumpsWritten);
        Assert.False(state.Armed);
        Assert.False(FreezeDump.ShouldDump(ref state, 12, 20_000, 10, out _));
        Assert.Equal(1, state.DumpsWritten);
    }

    [Fact]
    public void Rearms_after_the_frame_advances()
    {
        var state = default(FreezeWatchState);
        Assert.False(FreezeDump.ShouldDump(ref state, 12, 0, 10, out _));
        Assert.True(FreezeDump.ShouldDump(ref state, 12, 10_000, 10, out _));
        Assert.False(state.Armed);

        Assert.False(FreezeDump.ShouldDump(ref state, 13, 11_000, 10, out _));
        Assert.True(state.Armed);
        Assert.True(FreezeDump.ShouldDump(ref state, 13, 21_000, 10, out _));
        Assert.Equal(2, state.DumpsWritten);
        Assert.False(state.Armed);
    }

    [Fact]
    public void Cap_is_three_dumps()
    {
        var state = default(FreezeWatchState);
        int frame = 100;
        long t = 0;
        Assert.False(FreezeDump.ShouldDump(ref state, frame, t, 10, out _));
        for (int i = 0; i < FreezeDump.DumpCap; i++)
        {
            t += 10_000;
            Assert.True(FreezeDump.ShouldDump(ref state, frame, t, 10, out _));
            frame++;
            t += 1_000;
            Assert.False(FreezeDump.ShouldDump(ref state, frame, t, 10, out _));
        }

        t += 10_000;
        Assert.False(FreezeDump.ShouldDump(ref state, frame, t, 10, out _));
        Assert.Equal(FreezeDump.DumpCap, state.DumpsWritten);
        Assert.True(state.Armed);
    }

    [Fact]
    public void Zero_freeze_seconds_disables()
    {
        var state = default(FreezeWatchState);
        Assert.False(FreezeDump.ShouldDump(ref state, 12, 0, 0, out _));
        Assert.False(FreezeDump.ShouldDump(ref state, 12, 30_000, 0, out _));
        Assert.Equal(0, state.DumpsWritten);
        Assert.True(state.Armed);
    }

    [Fact]
    public void Frame_zero_does_not_auto_dump()
    {
        var state = default(FreezeWatchState);
        Assert.False(FreezeDump.ShouldDump(ref state, 0, 0, 10, out _));
        Assert.False(FreezeDump.ShouldDump(ref state, 0, 10_000, 10, out _));
        Assert.Equal(0, state.DumpsWritten);
    }

    [Fact]
    public void File_name_and_log_lines_match_the_contract()
    {
        var utc = new DateTime(2026, 9, 9, 12, 28, 58, 747, DateTimeKind.Utc);
        Assert.Equal("freeze-20260909T122858747Z-frame3276979.dmp", FreezeDump.FileName(utc, 3276979));
        Assert.Equal(
            "freeze dump written path=C:\\dmp.dmp frozenForMs=29924 frame=3276979",
            FreezeDump.WrittenLine("C:\\dmp.dmp", 29924, 3276979));
        Assert.Equal("freeze dump failed hr=-2147024770", FreezeDump.FailedLine(-2147024770));
        Assert.Equal(
            FreezeDump.MiniDumpNormal | FreezeDump.MiniDumpWithHandleData | FreezeDump.MiniDumpWithThreadInfo,
            FreezeDump.DumpType);
        Assert.Equal(0x00001004u, FreezeDump.DumpType);
    }

    [Fact]
    public void Bridge_status_toggles_freeze_seconds()
    {
        FreezeDump.ResetForTests();
        try
        {
            Assert.Equal(FreezeDump.DefaultFreezeSeconds, FreezeDump.FreezeSeconds);
            BridgeStatus.ApplyToggles(Json("{\"freezeSeconds\":0}"));
            Assert.Equal(0, FreezeDump.FreezeSeconds);
            BridgeStatus.ApplyToggles(Json("{\"freezeSeconds\":15}"));
            Assert.Equal(15, FreezeDump.FreezeSeconds);
            FreezeDump.Snapshot(out bool running, out int seconds, out int count, out object? last, out bool dumpInProgress);
            Assert.False(running);
            Assert.Equal(15, seconds);
            Assert.Equal(0, count);
            Assert.Null(last);
            Assert.False(dumpInProgress);
        }
        finally
        {
            FreezeDump.ResetForTests();
        }
    }

    [Fact]
    public void Dump_op_returns_started_and_writes_on_a_background_thread()
    {
        FreezeDump.ResetForTests();
        int caller = Environment.CurrentManagedThreadId;
        int writer = 0;
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        FreezeDump.WriteDump = path =>
        {
            writer = Environment.CurrentManagedThreadId;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) return -1;
            return 0;
        };
        try
        {
            var result = FreezeDump.Run(Json("{\"reason\":\"test\"}"));
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.True(doc.RootElement.GetProperty("started").GetBoolean());
            string? path = doc.RootElement.GetProperty("path").GetString();
            Assert.False(string.IsNullOrEmpty(path));
            Assert.Equal("test", doc.RootElement.GetProperty("reason").GetString());

            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            FreezeDump.Snapshot(out _, out _, out int count, out object? last, out bool dumpInProgress);
            Assert.True(dumpInProgress);
            Assert.Equal(1, count);
            Assert.Null(last);

            release.Set();
            Assert.True(WaitUntil(() =>
            {
                FreezeDump.Snapshot(out _, out _, out _, out _, out bool inFlight);
                return !inFlight;
            }));
            FreezeDump.Snapshot(out _, out _, out count, out last, out dumpInProgress);
            Assert.False(dumpInProgress);
            Assert.Equal(1, count);
            Assert.NotNull(last);
            using var lastDoc = JsonDocument.Parse(JsonSerializer.Serialize(last));
            Assert.Equal(path, lastDoc.RootElement.GetProperty("path").GetString());
            Assert.True(lastDoc.RootElement.TryGetProperty("at", out _));
            Assert.True(lastDoc.RootElement.TryGetProperty("frozenMs", out _));
            Assert.NotEqual(0, writer);
            Assert.NotEqual(caller, writer);
        }
        finally
        {
            release.Set();
            WaitUntil(() =>
            {
                FreezeDump.Snapshot(out _, out _, out _, out _, out bool inFlight);
                return !inFlight;
            });
            FreezeDump.ResetForTests();
        }
    }

    [Fact]
    public void Dump_op_failed_writer_releases_the_cap_slot()
    {
        FreezeDump.ResetForTests();
        var logs = new List<string>();
        FreezeDump.LogSink = logs.Add;
        FreezeDump.WriteDump = _ => -2147024770;
        try
        {
            var result = FreezeDump.Run(Json("{}"));
            using var started = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.True(started.RootElement.GetProperty("started").GetBoolean());

            Assert.True(WaitUntil(() =>
            {
                FreezeDump.Snapshot(out _, out _, out _, out _, out bool inFlight);
                return !inFlight;
            }));
            FreezeDump.Snapshot(out _, out _, out int count, out object? last, out bool dumpInProgress);
            Assert.False(dumpInProgress);
            Assert.Equal(0, count);
            Assert.Null(last);
            Assert.Contains(logs, l => l == FreezeDump.FailedLine(-2147024770));
        }
        finally
        {
            FreezeDump.ResetForTests();
        }
    }

    [Fact]
    public void Dump_op_cap_is_three_and_in_progress_refuses_a_second_start()
    {
        FreezeDump.ResetForTests();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        FreezeDump.WriteDump = _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) return -1;
            return 0;
        };
        try
        {
            var first = FreezeDump.Run(Json("{\"reason\":\"one\"}"));
            using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(first)))
                Assert.True(doc.RootElement.GetProperty("started").GetBoolean());
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

            var second = FreezeDump.Run(Json("{\"reason\":\"two\"}"));
            using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(second)))
            {
                Assert.False(doc.RootElement.GetProperty("started").GetBoolean());
                Assert.Equal("dump in progress", doc.RootElement.GetProperty("error").GetString());
            }
            FreezeDump.Snapshot(out _, out _, out int count, out _, out bool dumpInProgress);
            Assert.True(dumpInProgress);
            Assert.Equal(1, count);

            release.Set();
            Assert.True(WaitUntil(() =>
            {
                FreezeDump.Snapshot(out _, out _, out _, out _, out bool inFlight);
                return !inFlight;
            }));

            FreezeDump.WriteDump = _ => 0;
            for (int i = 1; i < FreezeDump.DumpCap; i++)
            {
                var more = FreezeDump.Run(Json("{}"));
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(more));
                Assert.True(doc.RootElement.GetProperty("started").GetBoolean());
                Assert.True(WaitUntil(() =>
                {
                    FreezeDump.Snapshot(out _, out _, out _, out _, out bool inFlight);
                    return !inFlight;
                }));
            }

            FreezeDump.Snapshot(out _, out _, out count, out object? last, out dumpInProgress);
            Assert.False(dumpInProgress);
            Assert.Equal(FreezeDump.DumpCap, count);
            Assert.NotNull(last);

            var capped = FreezeDump.Run(Json("{}"));
            using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(capped)))
            {
                Assert.False(doc.RootElement.GetProperty("started").GetBoolean());
                Assert.Equal("dump cap reached (3 per process)", doc.RootElement.GetProperty("error").GetString());
                Assert.Equal(FreezeDump.DumpCap, doc.RootElement.GetProperty("dumpsWritten").GetInt32());
            }
        }
        finally
        {
            release.Set();
            WaitUntil(() =>
            {
                FreezeDump.Snapshot(out _, out _, out _, out _, out bool inFlight);
                return !inFlight;
            });
            FreezeDump.ResetForTests();
        }
    }

    [Fact]
    public void Poll_starts_the_writer_off_the_calling_thread()
    {
        FreezeDump.ResetForTests();
        FreezeDump.ArmForTests();
        MainThread.LastDrainFrame = 12;
        long now = 0;
        FreezeDump.NowMs = () => now;
        int caller = Environment.CurrentManagedThreadId;
        int writer = 0;
        FreezeDump.WriteDump = _ =>
        {
            writer = Environment.CurrentManagedThreadId;
            return 0;
        };
        try
        {
            FreezeDump.Poll();
            now = 10_000;
            FreezeDump.Poll();
            Assert.True(WaitUntil(() =>
            {
                FreezeDump.Snapshot(out _, out _, out int count, out _, out bool inFlight);
                return !inFlight && count == 1;
            }));
            FreezeDump.Snapshot(out _, out _, out int written, out object? last, out bool dumpInProgress);
            Assert.False(dumpInProgress);
            Assert.Equal(1, written);
            Assert.NotNull(last);
            Assert.NotEqual(0, writer);
            Assert.NotEqual(caller, writer);
        }
        finally
        {
            FreezeDump.ResetForTests();
            MainThread.LastDrainFrame = 0;
        }
    }

    [Fact]
    public void Stop_does_not_join_a_dump_in_flight()
    {
        FreezeDump.ResetForTests();
        var entered = new ManualResetEventSlim(false);
        var release = new ManualResetEventSlim(false);
        FreezeDump.WriteDump = _ =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) return -1;
            return 0;
        };
        try
        {
            FreezeDump.Run(Json("{}"));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var sw = Stopwatch.StartNew();
            FreezeDump.Stop();
            Assert.True(sw.ElapsedMilliseconds < 500);
            FreezeDump.Snapshot(out _, out _, out _, out _, out bool dumpInProgress);
            Assert.True(dumpInProgress);
        }
        finally
        {
            release.Set();
            WaitUntil(() =>
            {
                FreezeDump.Snapshot(out _, out _, out _, out _, out bool inFlight);
                return !inFlight;
            });
            FreezeDump.ResetForTests();
        }
    }

    [Fact]
    public void File_name_marks_synthetic_dumps()
    {
        var utc = new DateTime(2026, 9, 9, 12, 28, 58, 747, DateTimeKind.Utc);
        Assert.Equal(
            "synthetic-freeze-20260909T122858747Z-frame3276979.dmp",
            FreezeDump.FileName(utc, 3276979, synthetic: true));
        Assert.Equal(
            "freeze dump cap reached dumpsWritten=3",
            FreezeDump.CapReachedLine(3));
        Assert.Equal(
            "freeze watchdog failed to start hr=-1",
            FreezeDump.WatchdogFailedLine(-1));
    }

    [Fact]
    public void Poll_after_Stop_does_not_dump()
    {
        FreezeDump.ResetForTests();
        MainThread.LastDrainFrame = 12;
        long now = 0;
        FreezeDump.NowMs = () => now;
        FreezeDump.WriteDump = _ => 0;
        try
        {
            FreezeDump.ArmForTests();
            FreezeDump.Poll();
            now = 10_000;
            FreezeDump.Stop();
            FreezeDump.Poll();
            FreezeDump.Snapshot(
                out bool running,
                out _,
                out int count,
                out object? last,
                out bool dumpInProgress,
                out string phase,
                out bool cap,
                out _,
                out _,
                out _);
            Assert.False(running);
            Assert.Equal(0, count);
            Assert.Null(last);
            Assert.False(dumpInProgress);
            Assert.Equal(FreezeDump.PhaseIdle, phase);
            Assert.False(cap);
        }
        finally
        {
            FreezeDump.ResetForTests();
            MainThread.LastDrainFrame = 0;
        }
    }

    [Fact]
    public void Watchdog_loop_dumps_on_a_stuck_frame_without_the_test_thread()
    {
        FreezeDump.ResetForTests();
        MainThread.LastDrainFrame = 12;
        long now = 0;
        FreezeDump.NowMs = () => Interlocked.Read(ref now);
        FreezeDump.PollMs = 20;
        FreezeDump.FreezeSeconds = 1;
        int caller = Environment.CurrentManagedThreadId;
        int writer = 0;
        FreezeDump.WriteDump = _ =>
        {
            writer = Environment.CurrentManagedThreadId;
            return 0;
        };
        try
        {
            FreezeDump.Start();
            Assert.True(WaitUntil(() =>
            {
                FreezeDump.Snapshot(out bool running, out _, out _, out _, out _);
                return running;
            }));
            Assert.True(WaitUntil(() =>
            {
                Interlocked.Add(ref now, 1_000);
                FreezeDump.Snapshot(
                    out _,
                    out _,
                    out int count,
                    out object? last,
                    out bool inFlight,
                    out string phase,
                    out _,
                    out _,
                    out _,
                    out string? trigger);
                return !inFlight && count == 1 && last != null && phase == FreezeDump.PhaseCompleted
                    && trigger == FreezeDump.TriggerWatchdog;
            }));
            Assert.NotEqual(0, writer);
            Assert.NotEqual(caller, writer);
        }
        finally
        {
            FreezeDump.ResetForTests();
            MainThread.LastDrainFrame = 0;
        }
    }

    [Fact]
    public void Double_Start_leaves_one_running_poller()
    {
        FreezeDump.ResetForTests();
        try
        {
            FreezeDump.Start();
            FreezeDump.Start();
            FreezeDump.Snapshot(out bool running, out _, out _, out _, out _);
            Assert.True(running);
            FreezeDump.Stop();
            FreezeDump.Snapshot(out running, out _, out _, out _, out _);
            Assert.False(running);
        }
        finally
        {
            FreezeDump.ResetForTests();
        }
    }

    [Fact]
    public void Auto_dump_cap_is_logged_and_survives_a_new_generation()
    {
        FreezeDump.ResetForTests();
        MainThread.LastDrainFrame = 100;
        long now = 0;
        FreezeDump.NowMs = () => now;
        FreezeDump.WriteDump = _ => 0;
        var logs = new List<string>();
        FreezeDump.LogSink = logs.Add;
        try
        {
            FreezeDump.ArmForTests();
            FreezeDump.Poll();
            for (int i = 0; i < FreezeDump.DumpCap; i++)
            {
                now += 10_000;
                FreezeDump.Poll();
                Assert.True(WaitUntil(() =>
                {
                    FreezeDump.Snapshot(out _, out _, out _, out _, out bool inFlight);
                    return !inFlight;
                }));
                MainThread.LastDrainFrame++;
                now += 1_000;
                FreezeDump.Poll();
            }

            FreezeDump.Snapshot(
                out _,
                out _,
                out int count,
                out _,
                out _,
                out _,
                out bool cap,
                out _,
                out _,
                out _);
            Assert.Equal(FreezeDump.DumpCap, count);
            Assert.True(cap);

            now += 10_000;
            FreezeDump.Poll();
            FreezeDump.Snapshot(out _, out _, out count, out _, out _);
            Assert.Equal(FreezeDump.DumpCap, count);
            Assert.Contains(logs, l => l == FreezeDump.CapReachedLine(FreezeDump.DumpCap));

            FreezeDump.SimulateNewGenerationForTests();
            FreezeDump.ArmForTests();
            MainThread.LastDrainFrame = 200;
            now = 0;
            FreezeDump.Poll();
            now = 10_000;
            FreezeDump.Poll();
            FreezeDump.Snapshot(
                out _,
                out _,
                out count,
                out object? last,
                out bool inFlight,
                out _,
                out cap,
                out _,
                out _,
                out _);
            Assert.False(inFlight);
            Assert.Equal(FreezeDump.DumpCap, count);
            Assert.True(cap);
            Assert.Null(last);
        }
        finally
        {
            FreezeDump.ResetForTests();
            MainThread.LastDrainFrame = 0;
        }
    }

    [Fact]
    public void Failed_dump_phase_is_not_a_successful_lastFreezeDump()
    {
        FreezeDump.ResetForTests();
        FreezeDump.WriteDump = _ => -2147024770;
        try
        {
            FreezeDump.Run(Json("{}"));
            Assert.True(WaitUntil(() =>
            {
                FreezeDump.Snapshot(out _, out _, out _, out _, out bool inFlight);
                return !inFlight;
            }));
            FreezeDump.Snapshot(
                out _,
                out _,
                out int count,
                out object? last,
                out bool dumpInProgress,
                out string phase,
                out bool cap,
                out object? error,
                out _,
                out string? trigger);
            Assert.False(dumpInProgress);
            Assert.Equal(0, count);
            Assert.Null(last);
            Assert.Equal(FreezeDump.PhaseFailed, phase);
            Assert.False(cap);
            Assert.Equal(FreezeDump.TriggerDump, trigger);
            Assert.NotNull(error);
            using var err = JsonDocument.Parse(JsonSerializer.Serialize(error));
            Assert.False(err.RootElement.GetProperty("completed").GetBoolean());
            Assert.Equal(-2147024770, err.RootElement.GetProperty("hr").GetInt32());
        }
        finally
        {
            FreezeDump.ResetForTests();
        }
    }

    [Fact]
    public void Completed_dump_phase_keeps_lastFreezeDump()
    {
        FreezeDump.ResetForTests();
        FreezeDump.WriteDump = _ => 0;
        try
        {
            var result = FreezeDump.Run(Json("{\"reason\":\"ok\"}"));
            using var started = JsonDocument.Parse(JsonSerializer.Serialize(result));
            Assert.Equal(FreezeDump.PhaseWriting, started.RootElement.GetProperty("phase").GetString());
            Assert.True(WaitUntil(() =>
            {
                FreezeDump.Snapshot(
                    out _,
                    out _,
                    out _,
                    out _,
                    out bool inFlight,
                    out string phase,
                    out _,
                    out _,
                    out _,
                    out _);
                return !inFlight && phase == FreezeDump.PhaseCompleted;
            }));
            FreezeDump.Snapshot(
                out _,
                out _,
                out int count,
                out object? last,
                out _,
                out string done,
                out _,
                out object? error,
                out _,
                out string? trigger);
            Assert.Equal(1, count);
            Assert.Equal(FreezeDump.PhaseCompleted, done);
            Assert.Equal(FreezeDump.TriggerDump, trigger);
            Assert.Null(error);
            Assert.NotNull(last);
            using var lastDoc = JsonDocument.Parse(JsonSerializer.Serialize(last));
            Assert.True(lastDoc.RootElement.GetProperty("completed").GetBoolean());
        }
        finally
        {
            FreezeDump.ResetForTests();
        }
    }

    [Fact]
    public void Bridge_status_exposes_phase_and_cap()
    {
        FreezeDump.ResetForTests();
        MainThread.ResetForTests();
        FreezeDump.WriteDump = _ => 0;
        var gate = new MainThreadOpGate();
        try
        {
            FreezeDump.Run(Json("{}"));
            Assert.True(WaitUntil(() =>
            {
                FreezeDump.Snapshot(out _, out _, out _, out _, out bool inFlight);
                return !inFlight;
            }));
            var shaped = BridgeStatus.Run(gate, Json("{}"));
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(shaped));
            var root = doc.RootElement;
            Assert.Equal(FreezeDump.PhaseCompleted, root.GetProperty("dumpPhase").GetString());
            Assert.False(root.GetProperty("dumpCapReached").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("lastDumpError").ValueKind);
            Assert.True(root.GetProperty("lastFreezeDump").GetProperty("completed").GetBoolean());
        }
        finally
        {
            FreezeDump.ResetForTests();
            MainThread.ResetForTests();
        }
    }

    [Fact]
    public void Stall_edge_begins_once_and_ends_on_the_next_advance()
    {
        var state = default(FreezeWatchState);
        // First sample only initializes (ShouldDump owns that); StallEdge sees nothing yet.
        Assert.Equal(FreezeDump.StallEdgeKind.None, FreezeDump.StallEdge(ref state, 50, 0, 10, out _));
        FreezeDump.ShouldDump(ref state, 50, 0, 10, out _);

        Assert.Equal(FreezeDump.StallEdgeKind.None, FreezeDump.StallEdge(ref state, 50, 9_999, 10, out _));
        Assert.Equal(FreezeDump.StallEdgeKind.Began, FreezeDump.StallEdge(ref state, 50, 10_000, 10, out long began));
        Assert.Equal(10_000, began);
        FreezeDump.ShouldDump(ref state, 50, 10_000, 10, out _);
        Assert.Equal(FreezeDump.StallEdgeKind.None, FreezeDump.StallEdge(ref state, 50, 400_000, 10, out _));

        // The 2026-10-10 incident: one engine frame in about 480 s, then normal frames again.
        Assert.Equal(FreezeDump.StallEdgeKind.Ended, FreezeDump.StallEdge(ref state, 51, 479_830, 10, out long ended));
        Assert.Equal(479_830, ended);
        FreezeDump.ShouldDump(ref state, 51, 479_830, 10, out _);
        Assert.Equal(FreezeDump.StallEdgeKind.None, FreezeDump.StallEdge(ref state, 52, 481_000, 10, out _));
    }

    [Fact]
    public void Short_pauses_and_disabled_watchdog_have_no_stall_edge()
    {
        var state = default(FreezeWatchState);
        FreezeDump.ShouldDump(ref state, 7, 0, 10, out _);
        Assert.Equal(FreezeDump.StallEdgeKind.None, FreezeDump.StallEdge(ref state, 8, 9_000, 10, out _));
        FreezeDump.ShouldDump(ref state, 8, 9_000, 10, out _);
        Assert.Equal(FreezeDump.StallEdgeKind.None, FreezeDump.StallEdge(ref state, 8, 60_000, 0, out _));

        var zero = default(FreezeWatchState);
        FreezeDump.ShouldDump(ref zero, 0, 0, 10, out _);
        Assert.Equal(FreezeDump.StallEdgeKind.None, FreezeDump.StallEdge(ref zero, 0, 60_000, 10, out _));
    }

    [Fact]
    public void Stall_lines_name_the_window_state_and_hint()
    {
        var hidden = new FreezeWindowState(true, minimized: true, visible: true, foreground: false, cloaked: false, userIdleSeconds: 3);
        Assert.True(hidden.Hidden);
        string began = FreezeDump.StallBeganLine(10_000, 109849, hidden);
        Assert.StartsWith(
            "freeze began frozenForMs=10000 frame=109849 window=hidden minimized=1 visible=1 cloaked=0 foreground=0 userIdleS=3 hint=",
            began);
        Assert.Contains("minimized or hidden", began);

        var idle = new FreezeWindowState(true, false, true, false, false, 600);
        Assert.False(idle.Hidden);
        Assert.Contains("no user input for 600 s", idle.Hint());

        var active = new FreezeWindowState(true, false, true, true, false, 1);
        Assert.Contains("read the dump's main thread", active.Hint());
        Assert.Equal(
            "freeze ended frozenForMs=479830 frame=109850 window=shown minimized=0 visible=1 cloaked=0 foreground=1 userIdleS=1",
            FreezeDump.StallEndedLine(479_830, 109850, active));
        Assert.Equal("window=none userIdleS=-1", FreezeWindowState.Unknown.Describe());

        // The native read never throws; the test host has no Unity player window of its own.
        FreezeWindowState.ReadOverride = null;
        var real = FreezeWindowState.Read();
        Assert.False(real.Found);
        Assert.StartsWith("window=none userIdleS=", real.Describe());
    }

    [Fact]
    public void Poll_logs_begin_and_end_of_a_stall_even_after_the_dump_cap()
    {
        FreezeDump.ResetForTests();
        MainThread.LastDrainFrame = 300;
        long now = 0;
        FreezeDump.NowMs = () => now;
        FreezeDump.WriteDump = _ => 0;
        FreezeWindowState.ReadOverride = () => new FreezeWindowState(true, true, true, false, false, 5);
        var logs = new List<string>();
        FreezeDump.LogSink = l => { lock (logs) logs.Add(l); };
        try
        {
            AppDomain.CurrentDomain.SetData(FreezeDump.ProcessCapKey, FreezeDump.DumpCap);
            FreezeDump.ArmForTests();
            FreezeDump.Poll();
            now = 10_000;
            FreezeDump.Poll();
            now = 12_000;
            FreezeDump.Poll();
            MainThread.LastDrainFrame = 301;
            now = 480_000;
            FreezeDump.Poll();

            string[] snapshot;
            lock (logs) snapshot = logs.ToArray();
            Assert.Single(snapshot, l => l.StartsWith("freeze began frozenForMs=10000 frame=300 window=hidden", StringComparison.Ordinal));
            Assert.Single(snapshot, l => l.StartsWith("freeze ended frozenForMs=480000 frame=301 window=hidden", StringComparison.Ordinal));
            Assert.DoesNotContain(snapshot, l => l.StartsWith("freeze dump written", StringComparison.Ordinal));
            FreezeDump.Snapshot(out _, out _, out int count, out object? last, out _);
            Assert.Equal(FreezeDump.DumpCap, count);
            Assert.Null(last);
        }
        finally
        {
            FreezeDump.ResetForTests();
            MainThread.LastDrainFrame = 0;
        }
    }

    private static bool WaitUntil(Func<bool> pred, int ms = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (pred()) return true;
            Thread.Sleep(10);
        }
        return pred();
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
