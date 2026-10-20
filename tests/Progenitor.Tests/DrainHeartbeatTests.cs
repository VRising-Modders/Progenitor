using System;
using System.Collections.Generic;
using System.Diagnostics;
using Progenitor.Dev;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

[Collection("MainThreadQueue")]
public class DrainHeartbeatTests
{
    [Fact]
    public void Due_is_false_when_the_period_is_zero()
    {
        Assert.False(DrainHeartbeat.Due(10_000, long.MinValue, 0));
        Assert.False(DrainHeartbeat.Due(10_000, 0, 0));
    }

    [Fact]
    public void Due_is_true_on_the_first_sample_and_after_the_period()
    {
        Assert.True(DrainHeartbeat.Due(0, long.MinValue, 5));
        Assert.False(DrainHeartbeat.Due(4_999, 0, 5));
        Assert.True(DrainHeartbeat.Due(5_000, 0, 5));
    }

    [Fact]
    public void Format_matches_the_diagnosis_line()
    {
        Assert.Equal(
            "tick hb frame=12 drainMs=3 queued=1 delayed=2 gate=free",
            DrainHeartbeat.Format(12, 3, 1, 2, "free"));
        Assert.Equal(
            "tick hb frame=12 drainMs=3 queued=1 delayed=2 gate=busy:ping#7",
            DrainHeartbeat.Format(12, 3, 1, 2, DrainHeartbeat.FormatGate(true, "ping", 7)));
    }

    [Fact]
    public void Drain_emits_one_line_per_period_and_zero_disables()
    {
        MainThread.ResetForTests();
        var log = new List<string>();
        DrainHeartbeat.LogSink = log.Add;
        try
        {
            long t = 0;
            DrainHeartbeat.NowMs = () => t;
            MainThread.LastDrainFrame = 1;
            MainThread.Drain();
            Assert.Single(log);
            Assert.StartsWith("tick hb frame=1 drainMs=", log[0]);
            Assert.Contains("gate=free", log[0]);

            t = 4_000;
            MainThread.LastDrainFrame = 2;
            MainThread.Drain();
            Assert.Single(log);

            t = 5_000;
            MainThread.LastDrainFrame = 3;
            MainThread.Drain();
            Assert.Equal(2, log.Count);

            MainThread.HeartbeatSeconds = 0;
            t = 20_000;
            MainThread.LastDrainFrame = 4;
            MainThread.Drain();
            Assert.Equal(2, log.Count);
        }
        finally
        {
            MainThread.ResetForTests();
        }
    }

    [Fact]
    public void Same_frame_does_not_emit_twice()
    {
        MainThread.ResetForTests();
        var log = new List<string>();
        DrainHeartbeat.LogSink = log.Add;
        try
        {
            DrainHeartbeat.NowMs = () => 0;
            MainThread.LastDrainFrame = 8;
            DrainHeartbeat.NoteDrain(4);
            DrainHeartbeat.NoteFrame(8);
            Assert.Single(log);
        }
        finally
        {
            MainThread.ResetForTests();
        }
    }
}

[Collection("MainThreadQueue")]
public class HeartbeatUiTickTimingTests
{
    [Fact]
    public void Rate_limit_logs_once_per_five_seconds_when_enabled_and_over_threshold()
    {
        Assert.False(UiTickTiming.ShouldLog(false, 80, 0, long.MinValue));
        Assert.False(UiTickTiming.ShouldLog(true, 50, 0, long.MinValue));
        Assert.True(UiTickTiming.ShouldLog(true, 51, 0, long.MinValue));
        Assert.False(UiTickTiming.ShouldLog(true, 80, 4_999, 0));
        Assert.True(UiTickTiming.ShouldLog(true, 80, 5_000, 0));
        Assert.Equal("ui tick name=Relayout ms=80", UiTickTiming.Format("Relayout", 80));
    }

    [Fact]
    public void Finish_is_silent_until_enabled()
    {
        UiTickTiming.ResetForTests();
        var log = new List<string>();
        UiTickTiming.LogSink = log.Add;
        try
        {
            long t = 0;
            UiTickTiming.NowMs = () => t;
            long start = Stopwatch.GetTimestamp() - Stopwatch.Frequency;
            UiTickTiming.Finish("Tick", start);
            Assert.Empty(log);

            UiTickTiming.Enabled = true;
            t = 0;
            UiTickTiming.Finish("Tick", start);
            Assert.Single(log);
            Assert.StartsWith("ui tick name=Tick ms=", log[0]);

            t = 1_000;
            UiTickTiming.Finish("Relayout", start);
            Assert.Single(log);

            t = 5_000;
            UiTickTiming.Finish("Relayout", start);
            Assert.Equal(2, log.Count);
            Assert.StartsWith("ui tick name=Relayout ms=", log[1]);
        }
        finally
        {
            UiTickTiming.ResetForTests();
        }
    }
}

public class UiFrameCostTests
{
    [Fact]
    public void Hitch_line_names_phases_and_the_slowest_handler()
    {
        string line = UiFrameCost.Format(
            intervalMs: 253, outsideMs: 63, frameMs: 190,
            pumpMs: 1, rebuildMs: 180, layoutMs: 40, flushDirMs: 185, tickMs: 2,
            worstName: "flushDir", worstMs: 185,
            worstHandlerName: "FlushDirectoryRefresh", worstHandlerMs: 185);
        Assert.StartsWith("ui hitch intervalMs=253", line);
        Assert.Contains("outsideMs=63", line);
        Assert.Contains("frameMs=190", line);
        Assert.Contains("rebuildMs=180", line);
        Assert.Contains("flushDirMs=185", line);
        Assert.Contains("worst=flushDir", line);
        Assert.Contains("handler=FlushDirectoryRefresh", line);
        Assert.Contains("handlerMs=185", line);
    }

    [Fact]
    public void Hitch_and_immediate_thresholds()
    {
        Assert.False(UiFrameCost.IsHitch(16, 2));
        Assert.True(UiFrameCost.IsHitch(253, 2));
        Assert.True(UiFrameCost.IsHitch(16, 180));

        // Only a named Progenitor phase or handler of 250 ms or more, at most once per 5 s.
        Assert.False(UiFrameCost.IsImmediate(249, "handler:JoinFlowPanel.OnFrame", 0, long.MinValue));
        Assert.False(UiFrameCost.IsImmediate(900, "", 0, long.MinValue));
        Assert.True(UiFrameCost.IsImmediate(250, "handler:JoinFlowPanel.OnFrame", 0, long.MinValue));
        Assert.False(UiFrameCost.IsImmediate(300, "handler:X", 4_999, 0));
        Assert.True(UiFrameCost.IsImmediate(300, "handler:X", 5_000, 0));

        Assert.False(UiFrameCost.SummaryDue(0, long.MinValue));
        Assert.False(UiFrameCost.SummaryDue(29_999, 0));
        Assert.True(UiFrameCost.SummaryDue(30_000, 0));
    }

    [Fact]
    public void Median_is_the_middle_of_the_kept_samples()
    {
        var scratch = new long[8];
        Assert.Equal(0, UiFrameCost.Median(new long[8], 0, scratch));
        Assert.Equal(85, UiFrameCost.Median(new long[] { 96, 80, 85, 0, 0 }, 3, scratch));
        Assert.Equal(87, UiFrameCost.Median(new long[] { 96, 80, 85, 90 }, 4, scratch));
    }

    private static void Frame(long intervalMs, string worst = "", long worstMs = 0)
    {
        UiFrameCost.IntervalMs = intervalMs;
        UiFrameCost.OutsideMs = intervalMs - worstMs;
        UiFrameCost.FrameMs = worstMs;
        UiFrameCost.WorstName = worst;
        UiFrameCost.WorstMs = worstMs;
        UiFrameCost.WorstHandlerName = worst.StartsWith("handler:", StringComparison.Ordinal) ? worst.Substring(8) : "";
        UiFrameCost.WorstHandlerMs = worstMs;
    }

    [Fact]
    public void Slow_frames_become_one_summary_per_30_seconds_not_one_line_each()
    {
        UiFrameCost.ResetForTests();
        var lines = new List<string>();
        try
        {
            UiFrameCost.LogSink = lines.Add;
            long t = 1_000;
            // 29 s of a 12 fps game: every frame is a hitch the game made (no Progenitor phase), plus three
            // JoinFlowPanel frames under the immediate threshold and one Keybinds frame.
            for (int i = 0; i < 340; i++, t += 85)
            {
                if (i % 100 == 50) Frame(106, "handler:JoinFlowPanel.OnFrame", 85);
                else if (i == 120) Frame(90, "handler:Keybinds.Pump", 20);
                else Frame(85);
                UiFrameCost.Record(t);
            }

            Assert.Empty(lines);

            t = 31_000;
            Frame(85);
            UiFrameCost.Record(t);
            string line = Assert.Single(lines);
            Assert.StartsWith("ui hitch summary windowS=30 frames=341 hitches=341 intervalMs=median:85,max:106", line);
            Assert.Contains("outsideOnly=337", line);
            Assert.Contains("slowest=handler:JoinFlowPanel.OnFrame:85", line);
            Assert.Contains("top=[handler:JoinFlowPanel.OnFrame 255ms/3, handler:Keybinds.Pump 20ms/1]", line);

            // The window restarted: a quiet next window writes nothing.
            for (int i = 0; i < 400; i++)
            {
                t += 16;
                Frame(16);
                UiFrameCost.Record(t);
            }

            t += 30_000;
            Frame(16);
            UiFrameCost.Record(t);
            Assert.Single(lines);
        }
        finally
        {
            UiFrameCost.ResetForTests();
        }
    }

    [Fact]
    public void Console_cost_field_is_the_window_delta_and_absent_without_a_queued_console()
    {
        long f = Stopwatch.Frequency;
        // enqueued, dropped, truncated, written, writeTicks, maxWriteTicks, enqueueTicks, maxEnqueueTicks, maxPending, pending, freq
        var before = new long[] { 100, 1, 0, 100, f, 0, 0, 0, 3, 0, f };
        var now = new long[] { 512, 4, 3, 500, f + f * 93 / 100, f * 21 / 100, 0, f * 18 / 1_000_000, 57, 2, f };
        Assert.Equal(" console=lines:412,dropped:3,cut:3,writeMs:930,maxBatchMs:210,maxQueue:57,maxEnqueueUs:18",
            UiFrameCost.FormatConsoleCost(before, now));
        Assert.Equal("", UiFrameCost.FormatConsoleCost(before, null));
        Assert.Equal("", UiFrameCost.FormatConsoleCost(null, new long[3]));

        UiFrameCost.ResetForTests();
        var lines = new List<string>();
        try
        {
            int reads = 0;
            UiFrameCost.ConsoleCostProbe = reset =>
            {
                Assert.True(reset);
                reads++;
                return reads == 1 ? before : now;
            };
            UiFrameCost.LogSink = lines.Add;
            Frame(300);
            UiFrameCost.Record(1_000);
            Frame(16);
            UiFrameCost.Record(31_000);
            string line = Assert.Single(lines);
            Assert.EndsWith(" console=lines:412,dropped:3,cut:3,writeMs:930,maxBatchMs:210,maxQueue:57,maxEnqueueUs:18", line);
            Assert.Equal(2, reads);
        }
        finally
        {
            UiFrameCost.ResetForTests();
        }
    }

    [Fact]
    public void A_single_slow_Progenitor_frame_writes_at_once_and_is_rate_limited()
    {
        UiFrameCost.ResetForTests();
        var lines = new List<string>();
        try
        {
            UiFrameCost.LogSink = lines.Add;
            Frame(300, "handler:JoinFlowPanel.OnFrame", 289);
            UiFrameCost.Record(1_000);
            string line = Assert.Single(lines);
            Assert.StartsWith("ui hitch intervalMs=300", line);
            Assert.Contains("worst=handler:JoinFlowPanel.OnFrame worstMs=289", line);

            // A second one within 5 s is only counted; a slow frame the game made is never immediate.
            Frame(300, "handler:JoinFlowPanel.OnFrame", 280);
            UiFrameCost.Record(2_000);
            Frame(1_040);
            UiFrameCost.Record(3_000);
            Assert.Single(lines);
            Frame(300, "handler:JoinFlowPanel.OnFrame", 280);
            UiFrameCost.Record(6_000);
            Assert.Equal(2, lines.Count);
        }
        finally
        {
            UiFrameCost.ResetForTests();
        }
    }

    [Fact]
    public void Note_attributes_a_delayed_rebuild_to_the_frame_it_runs_on()
    {
        UiFrameCost.ResetForTests();
        try
        {
            UiFrameCost.BeginFrame();
            long start = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 5;
            UiFrameCost.Note("flushDir", start);
            UiFrameCost.NoteHandler("FlushDirectoryRefresh", Stopwatch.Frequency / 10);
            Assert.True(UiFrameCost.FlushDirMs >= 100);
            Assert.Equal("flushDir", UiFrameCost.WorstName);
            Assert.Equal("FlushDirectoryRefresh", UiFrameCost.WorstHandlerName);
        }
        finally
        {
            UiFrameCost.ResetForTests();
        }
    }

    [Fact]
    public void Outside_work_adds_up_per_name_and_joins_the_next_frame_with_its_interval()
    {
        UiFrameCost.ResetForTests();
        try
        {
            UiFrameCost.BeginFrame();
            long tenMs = Stopwatch.Frequency / 100;
            // A system hook called 9 times and a seam gate once while the game's systems ran.
            for (int i = 0; i < 9; i++) UiFrameCost.NoteOutside("hook:mod:CraftSystem.before", tenMs);
            UiFrameCost.NoteOutside("seam:materials.countGate", 3 * tenMs);
            Assert.Equal("", UiFrameCost.WorstName);
            UiFrameCost.BeginFrame();
            Assert.Equal("hook:mod:CraftSystem.before", UiFrameCost.WorstName);
            Assert.InRange(UiFrameCost.WorstMs, 89, 91);
            Assert.True(UiFrameCost.IsHitch(UiFrameCost.IntervalMs, UiFrameCost.WorstMs));
            // Folded once: the following frame starts clean.
            UiFrameCost.BeginFrame();
            Assert.Equal("", UiFrameCost.WorstName);
            Assert.Equal(0, UiFrameCost.WorstMs);
        }
        finally
        {
            UiFrameCost.ResetForTests();
        }
    }

    [Fact]
    public void Outside_work_off_the_main_thread_or_before_the_first_frame_is_ignored()
    {
        UiFrameCost.ResetForTests();
        try
        {
            long tick = Stopwatch.Frequency;
            UiFrameCost.NoteOutside("hook:early", tick);
            UiFrameCost.BeginFrame();
            Assert.Equal("", UiFrameCost.WorstName);
            var worker = new System.Threading.Thread(() => UiFrameCost.NoteOutside("hook:worker", tick));
            worker.Start(); worker.Join();
            UiFrameCost.BeginFrame();
            Assert.Equal("", UiFrameCost.WorstName);
        }
        finally
        {
            UiFrameCost.ResetForTests();
        }
    }

    [Fact]
    public void Outside_names_beyond_the_table_add_up_as_other()
    {
        UiFrameCost.ResetForTests();
        try
        {
            UiFrameCost.BeginFrame();
            long oneMs = Stopwatch.Frequency / 1000;
            for (int i = 0; i < UiFrameCost.MaxOutsideNames; i++) UiFrameCost.NoteOutside("hook:" + i, oneMs);
            for (int i = 0; i < 50; i++) UiFrameCost.NoteOutside("hook:extra" + i, 2 * oneMs);
            UiFrameCost.BeginFrame();
            Assert.Equal(UiFrameCost.OutsideOtherName, UiFrameCost.WorstName);
            Assert.InRange(UiFrameCost.WorstMs, 99, 101);
        }
        finally
        {
            UiFrameCost.ResetForTests();
        }
    }
}
