using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Production close/finalize path for <c>perf</c>. Tests drive <see cref="PerfFinalize"/> —
/// the type <c>PerfOps.PerformClose</c> calls after snapshot and uninstall — not a parallel
/// fake. This is a diagnostic close-path fix, not an attribution of any observed freeze.
/// </summary>
public class PerfFinalizeTests
{
    [Fact]
    public void StartRefusal_is_the_overlap_policy_the_op_calls()
    {
        Assert.Equal(PerfFinalize.BusyRefusal, PerfFinalize.StartRefusal(false, false, true));
        Assert.Equal(PerfFinalize.RunningRefusal, PerfFinalize.StartRefusal(true, false, false));
        Assert.Equal(PerfFinalize.RunningRefusal, PerfFinalize.StartRefusal(false, true, false));
        Assert.Equal(PerfFinalize.BusyRefusal, PerfFinalize.StartRefusal(true, true, true));
        Assert.Null(PerfFinalize.StartRefusal(false, false, false));
    }

    [Fact]
    public void TryStart_returns_before_the_writer_finishes_and_state_is_writing()
    {
        PerfFinalize.ResetForTests();
        int caller = Environment.CurrentManagedThreadId;
        int writer = 0;
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        PerfFinalize.WriteFile = (_, _) =>
        {
            writer = Environment.CurrentManagedThreadId;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(30))) return "test writer timed out";
            return null;
        };
        try
        {
            var report = TinyReport();
            long t0 = Stopwatch.GetTimestamp();
            Assert.True(PerfFinalize.TryStart(11, report, TempPath("writing.json"), closeMs: 1.25));
            double tryStartMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var snap = PerfFinalize.Read();
            Assert.Equal(PerfFinalize.StateWriting, snap.State);
            Assert.Equal(11, snap.CaptureId);
            Assert.True(PerfFinalize.IsBusy);
            // Waiting for the write would take the held writer's full 30 s; the bound only has to tell the two apart.
            Assert.True(tryStartMs < 10_000, $"TryStart waited {tryStartMs:0.###} ms; the bounded portion must not wait for the write");
            Assert.NotEqual(0, writer);
            Assert.NotEqual(caller, writer);

            release.Set();
            Assert.True(WaitUntil(() => PerfFinalize.Read().State == PerfFinalize.StateCompleted));
            var done = PerfFinalize.Read();
            Assert.Equal(PerfFinalize.StateCompleted, done.State);
            Assert.Null(done.WriteError);
            Assert.Equal(1.25, done.CloseMs, 3);
            Assert.True(done.FinalizeMs > 0);
            Assert.NotNull(done.Summary);
            Assert.Equal(11L, done.Summary!["captureId"]);
            Assert.False(PerfFinalize.IsBusy);
        }
        finally
        {
            release.Set();
            WaitUntil(() => !PerfFinalize.IsBusy);
            PerfFinalize.ResetForTests();
        }
    }

    [Fact]
    public void A_later_capture_is_refused_while_the_previous_report_is_still_writing()
    {
        PerfFinalize.ResetForTests();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        PerfFinalize.WriteFile = (_, _) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) return "test writer timed out";
            return null;
        };
        try
        {
            Assert.True(PerfFinalize.TryStart(1, TinyReport(), TempPath("one.json"), 0.5));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(PerfFinalize.TryStart(2, TinyReport(), TempPath("two.json"), 0.5));
            Assert.Equal(PerfFinalize.BusyRefusal, PerfFinalize.StartRefusal(false, false, PerfFinalize.IsBusy));
            Assert.Equal(1, PerfFinalize.Read().CaptureId);
            Assert.Equal(PerfFinalize.StateWriting, PerfFinalize.Read().State);

            release.Set();
            Assert.True(WaitUntil(() => PerfFinalize.Read().State == PerfFinalize.StateCompleted));
            Assert.True(PerfFinalize.TryStart(2, TinyReport(), TempPath("two.json"), 0.5));
            Assert.True(WaitUntil(() => PerfFinalize.Read().State == PerfFinalize.StateCompleted && PerfFinalize.Read().CaptureId == 2));
            Assert.Equal(2, PerfFinalize.Read().CaptureId);
        }
        finally
        {
            release.Set();
            WaitUntil(() => !PerfFinalize.IsBusy);
            PerfFinalize.ResetForTests();
        }
    }

    [Fact]
    public void Write_failure_is_retained_as_failed_with_writeError()
    {
        PerfFinalize.ResetForTests();
        PerfFinalize.WriteFile = (_, _) => "disk full";
        try
        {
            Assert.True(PerfFinalize.TryStart(4, TinyReport(), TempPath("fail.json"), 0.8));
            Assert.True(WaitUntil(() => PerfFinalize.Read().State == PerfFinalize.StateFailed));
            var snap = PerfFinalize.Read();
            Assert.Equal(PerfFinalize.StateFailed, snap.State);
            Assert.Equal("disk full", snap.WriteError);
            Assert.Null(snap.Path);
            Assert.NotNull(snap.Summary);
            Assert.Equal(4L, snap.Summary!["captureId"]);
            Assert.False(PerfFinalize.IsBusy);

            var envelope = PerfFinalize.LiveView(
                PerfProgress.LiveStatus(null, 0), snap);
            Assert.Equal(PerfFinalize.StateFailed, envelope.reportState);
            Assert.Equal("disk full", envelope.writeError);
            Assert.Equal(4, envelope.reportCaptureId);
        }
        finally
        {
            WaitUntil(() => !PerfFinalize.IsBusy);
            PerfFinalize.ResetForTests();
        }
    }

    [Fact]
    public void Real_disk_write_failure_is_retained_rather_than_swallowed()
    {
        PerfFinalize.ResetForTests();
        string blocker = Path.GetTempFileName();
        string path = Path.Combine(blocker, "report.json");
        try
        {
            Assert.True(PerfFinalize.TryStart(5, TinyReport(), path, 0.4));
            Assert.True(WaitUntil(() => PerfFinalize.Read().State == PerfFinalize.StateFailed));
            var snap = PerfFinalize.Read();
            Assert.Equal(PerfFinalize.StateFailed, snap.State);
            Assert.False(string.IsNullOrEmpty(snap.WriteError));
            Assert.Contains("failed", snap.WriteError, StringComparison.OrdinalIgnoreCase);
            Assert.Null(snap.Path);
            Assert.NotNull(snap.Summary);
        }
        finally
        {
            WaitUntil(() => !PerfFinalize.IsBusy);
            PerfFinalize.ResetForTests();
            try { File.Delete(blocker); } catch { }
        }
    }

    [Fact]
    public void Successful_write_lands_the_file_and_records_latencies()
    {
        PerfFinalize.ResetForTests();
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-perf-" + Guid.NewGuid().ToString("n"));
        string path = Path.Combine(dir, "ok.json");
        try
        {
            var report = TinyReport();
            PerfCloseObservation.Stamp(report, sampleSilenceMsAtClose: 8, lastFrameMs: 16.5);
            Assert.True(PerfFinalize.TryStart(9, report, path, closeMs: 2.5));
            Assert.True(WaitUntil(() => PerfFinalize.Read().State == PerfFinalize.StateCompleted));
            var snap = PerfFinalize.Read();
            Assert.Equal(path, snap.Path);
            Assert.True(File.Exists(path));
            Assert.Equal(2.5, snap.CloseMs, 3);
            Assert.True(snap.FinalizeMs >= 0);
            Assert.False(snap.EndedDuringSilence);

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var perf = doc.RootElement.GetProperty("perf");
            Assert.Equal(9, perf.GetProperty("captureId").GetInt64());
            Assert.Equal(2.5, perf.GetProperty("closeMs").GetDouble());
            Assert.False(perf.GetProperty("endedDuringSilence").GetBoolean());
            Assert.Equal(8, perf.GetProperty("sampleSilenceMsAtClose").GetDouble());
        }
        finally
        {
            WaitUntil(() => !PerfFinalize.IsBusy);
            PerfFinalize.ResetForTests();
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Detach_drops_an_in_flight_publish_so_the_next_capture_does_not_inherit_it()
    {
        PerfFinalize.ResetForTests();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        PerfFinalize.WriteFile = (_, _) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) return "test writer timed out";
            return null;
        };
        try
        {
            Assert.True(PerfFinalize.TryStart(7, TinyReport(), TempPath("old.json"), 0.3));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            PerfFinalize.Detach();
            release.Set();
            Assert.True(WaitUntil(() => !PerfFinalize.IsBusy));
            var after = PerfFinalize.Read();
            Assert.NotEqual(7, after.CaptureId);
            Assert.NotEqual(PerfFinalize.StateCompleted, after.State);
            Assert.Null(after.Path);
            Assert.Null(after.Summary);

            PerfFinalize.WriteFile = (_, _) => null;
            Assert.True(PerfFinalize.TryStart(8, TinyReport(), TempPath("new.json"), 0.3));
            Assert.True(WaitUntil(() => PerfFinalize.Read().State == PerfFinalize.StateCompleted));
            Assert.Equal(8, PerfFinalize.Read().CaptureId);
            Assert.Null(PerfFinalize.Read().WriteError);
        }
        finally
        {
            release.Set();
            WaitUntil(() => !PerfFinalize.IsBusy);
            PerfFinalize.ResetForTests();
        }
    }

    [Fact]
    public void LiveView_exposes_writing_completed_and_failed_without_an_external_gate()
    {
        PerfFinalize.ResetForTests();
        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var progress = new PerfProgress(3, 0, 1000);
        progress.Record(3, 10, 16.5, 0.4);
        progress.Complete();
        var live = PerfProgress.LiveStatus(progress, 20);

        PerfFinalize.WriteFile = (_, _) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) return "test writer timed out";
            return null;
        };
        try
        {
            Assert.True(PerfFinalize.TryStart(3, TinyReport(), TempPath("live.json"), 1.0));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            var writing = PerfFinalize.LiveView(live, PerfFinalize.Read());
            Assert.Equal(PerfFinalize.StateWriting, writing.reportState);
            Assert.Equal(3, writing.captureId);
            Assert.Equal(3, writing.reportCaptureId);
            Assert.False(writing.running);
            Assert.True(writing.historical);
            Assert.Null(writing.finalizeMs);

            release.Set();
            Assert.True(WaitUntil(() => PerfFinalize.Read().State == PerfFinalize.StateCompleted));
            var done = PerfFinalize.LiveView(live, PerfFinalize.Read());
            Assert.Equal(PerfFinalize.StateCompleted, done.reportState);
            Assert.NotNull(done.finalizeMs);
            Assert.Null(done.writeError);
        }
        finally
        {
            release.Set();
            WaitUntil(() => !PerfFinalize.IsBusy);
            PerfFinalize.ResetForTests();
        }
    }

    [Fact]
    public void A_capture_that_ends_mid_stall_is_flagged_in_the_written_report()
    {
        PerfFinalize.ResetForTests();
        string? json = null;
        PerfFinalize.WriteFile = (_, body) =>
        {
            json = body;
            return null;
        };
        try
        {
            var report = TinyReport();
            // Completed frames are ordinary 16 ms ticks. The capture then ends after 5 s of
            // silence, so the omitted interval must not look like "no long frames".
            PerfCloseObservation.Stamp(report, sampleSilenceMsAtClose: 5000, lastFrameMs: 16);
            Assert.True(report.EndedDuringSilence);
            Assert.True(PerfFinalize.TryStart(12, report, TempPath("stall.json"), 0.2));
            Assert.True(WaitUntil(() => PerfFinalize.Read().State == PerfFinalize.StateCompleted));
            Assert.False(string.IsNullOrEmpty(json));
            using var doc = JsonDocument.Parse(json!);
            var perf = doc.RootElement.GetProperty("perf");
            Assert.True(perf.GetProperty("endedDuringSilence").GetBoolean());
            Assert.Equal(5000, perf.GetProperty("sampleSilenceMsAtClose").GetDouble());
            Assert.Equal(16, perf.GetProperty("lastFrameMs").GetDouble());
            var frameMax = doc.RootElement.GetProperty("frame").GetProperty("deltaMs").GetProperty("max").GetDouble();
            Assert.True(frameMax < 100, "the recorded frames themselves are not long; the stall is the omitted interval");
            Assert.True(PerfFinalize.Read().EndedDuringSilence);
        }
        finally
        {
            WaitUntil(() => !PerfFinalize.IsBusy);
            PerfFinalize.ResetForTests();
        }
    }

    [Fact]
    public void Result_distinguishes_writing_from_completed_from_failed()
    {
        var writing = new ReportWriteSnapshot(
            PerfFinalize.StateWriting, 1, "pending.json", null, null, 1, 0, false, 8);
        var completed = new ReportWriteSnapshot(
            PerfFinalize.StateCompleted, 1, "done.json", null, new Dictionary<string, object?>(), 1, 4, false, 8);
        var failed = new ReportWriteSnapshot(
            PerfFinalize.StateFailed, 1, null, "disk full", new Dictionary<string, object?>(), 1, 4, false, 8);

        Assert.Equal(PerfFinalize.StateWriting, PerfFinalize.Result(writing)["reportState"]);
        Assert.Null(PerfFinalize.Result(writing)["finalizeMs"]);
        Assert.Equal(PerfFinalize.StateCompleted, PerfFinalize.Result(completed)["reportState"]);
        Assert.Equal(4d, PerfFinalize.Result(completed)["finalizeMs"]);
        Assert.Equal("disk full", PerfFinalize.Result(failed)["writeError"]);
        Assert.Equal(PerfFinalize.StateFailed, PerfFinalize.Result(failed)["reportState"]);
    }

    private static PerfReport TinyReport()
    {
        var report = new PerfReport
        {
            Side = "server",
            StartedUtc = new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc),
            RequestedSeconds = 1,
            ElapsedSeconds = 0.032,
            StopReason = "stop",
            Recording = new PerfRecording(8),
        };
        var row = new float[PerfSourceTable.MaxSources];
        report.Recording.AddFrame(0, 16, 0.5, row, 0, 0, 0, 0);
        report.Recording.AddFrame(0.016, 16, 0.5, row, 0, 0, 0, 0);
        return report;
    }

    private static string TempPath(string name) =>
        Path.Combine(Path.GetTempPath(), "progenitor-perf-" + name);

    private static bool WaitUntil(Func<bool> pred, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (pred()) return true;
            Thread.Sleep(10);
        }
        return pred();
    }
}
