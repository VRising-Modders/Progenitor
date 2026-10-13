using System;
using System.Threading;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

public class PerfProgressTests
{
    [Fact]
    public void No_samples_reports_silence_without_inventing_a_frame()
    {
        var progress = new PerfProgress(1, 100, 1000);
        var sample = progress.Read(30100);
        Assert.Equal(30000, sample.sampleSilenceMs);
        Assert.Equal(0, sample.frames);
        Assert.Null(sample.lastFrameMs);
        Assert.Null(sample.lastCoreTickMs);
        Assert.Null(sample.maxFrameMs);
    }

    [Fact]
    public void Stall_age_grows_without_changing_completed_measurements()
    {
        var progress = new PerfProgress(1, 0, 1000);
        progress.Record(1, 20, 20, 3);
        progress.Record(1, 40, 16, 2);
        var stalled = progress.Read(30040);
        Assert.Equal(30000, stalled.sampleSilenceMs);
        Assert.Equal(16, stalled.lastFrameMs);
        Assert.Equal(2, stalled.lastCoreTickMs);
        Assert.Equal(20, stalled.maxFrameMs);
        Assert.Equal(2, stalled.frames);
        progress.Record(1, 30060, 30020, 5);
        Assert.Equal(30020, progress.Read(30060).maxFrameMs);
        Assert.Equal(0, progress.Read(30060).sampleSilenceMs);
    }

    [Fact]
    public void New_capture_does_not_inherit_previous_capture_metrics()
    {
        var previous = new PerfProgress(1, 0, 1000);
        previous.Record(1, 10, 10, 1);
        var next = new PerfProgress(2, 100, 1000);
        var snap = next.Read(110);
        Assert.Null(snap.maxFrameMs);
        Assert.Null(snap.lastFrameMs);
        Assert.Equal(0, snap.frames);
        Assert.Equal(10, snap.sampleSilenceMs);
        Assert.Equal(1, previous.Read(110).frames);
        var live = PerfProgress.LiveStatus(next, 110);
        Assert.True(live.running);
        Assert.False(live.historical);
        Assert.Equal(2, live.captureId);
    }

    [Fact]
    public void Finished_capture_is_historical_and_ignores_late_records()
    {
        var progress = new PerfProgress(7, 0, 1000);
        progress.Record(7, 10, 16.5, 0.4);
        progress.Complete();
        progress.Record(7, 20, 999, 999);
        var live = PerfProgress.LiveStatus(progress, 30);
        Assert.False(live.running);
        Assert.True(live.historical);
        Assert.Equal(7, live.captureId);
        Assert.Equal(1, live.progress!.frames);
        Assert.Equal(16.5, live.progress.lastFrameMs);
        Assert.Equal(0.4, live.progress.lastCoreTickMs);
        Assert.Equal(16.5, live.progress.maxFrameMs);
        Assert.Equal(20, live.progress.sampleSilenceMs);
    }

    [Fact]
    public void Late_record_with_old_capture_id_cannot_update_a_new_capture()
    {
        var next = new PerfProgress(2, 100, 1000);
        next.Record(1, 110, 999, 999);
        var before = next.Read(120);
        Assert.Equal(0, before.frames);
        Assert.Null(before.lastFrameMs);
        next.Record(2, 130, 16, 1);
        Assert.Equal(16, next.Read(130).lastFrameMs);
        Assert.Equal(1, next.Read(130).frames);
    }

    [Fact]
    public void LiveStatus_returns_while_a_main_thread_op_gate_is_held()
    {
        // Fake of Dev's MainThreadOpGate (the one-op slot). Production perf_status is not in
        // Handlers.MainThreadOps and must return while that slot is held.
        var mainThreadGate = new object();
        var progress = new PerfProgress(4, 0, 1000);
        progress.Record(4, 10, 16.5, 0.3);

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);

        // Dedicated threads, not the thread pool, and a deadlock bound rather than a speed bound:
        // under a loaded 2-vCPU CI runner a pool task could be queued or descheduled for more than
        // the old 2 s and fail a correct build (seen on unrelated branches, e.g. 2026-09-19). If
        // LiveStatus ever took the gate it would block until `release`, which is only set after the
        // join below, so the join still fails on the regression the test exists for.
        var holder = new Thread(() =>
        {
            lock (mainThreadGate)
            {
                entered.Set();
                release.Wait();
            }
        }) { IsBackground = true };
        holder.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        Assert.False(Monitor.TryEnter(mainThreadGate));

        PerfProgress.PerfLiveStatus? result = null;
        var reader = new Thread(() => result = PerfProgress.LiveStatus(progress, 20)) { IsBackground = true };
        reader.Start();
        Assert.True(reader.Join(TimeSpan.FromSeconds(30)), "LiveStatus blocked while the main-thread gate was held");
        Assert.True(holder.IsAlive, "the gate must still be held when LiveStatus returns");
        var live = result!;
        Assert.True(live.running);
        Assert.False(live.historical);
        Assert.Equal(4, live.captureId);
        Assert.Equal(1, live.progress!.frames);
        Assert.Equal(16.5, live.progress.lastFrameMs);
        Assert.Equal(PerfProgress.LiveNote, live.note);

        release.Set();
        Assert.True(holder.Join(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void LiveStatus_does_not_wait_on_an_external_gate()
    {
        var gate = new object();
        var progress = new PerfProgress(5, 0, 1000);
        progress.Record(5, 8, 16, 2);

        using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        // A dedicated thread and a deadlock bound, for the reason given in the test above.
        var holder = new Thread(() =>
        {
            lock (gate)
            {
                entered.Set();
                release.Wait();
            }
        }) { IsBackground = true };
        holder.Start();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        Assert.False(Monitor.TryEnter(gate));

        var live = PerfProgress.LiveStatus(progress, 18);
        Assert.Equal(5, live.captureId);
        Assert.True(live.running);
        Assert.Equal(16, live.progress!.lastFrameMs);

        release.Set();
        Assert.True(holder.Join(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void LiveStatus_without_a_capture_is_empty_not_historical()
    {
        var live = PerfProgress.LiveStatus(null, 50);
        Assert.False(live.running);
        Assert.False(live.historical);
        Assert.Equal(0, live.captureId);
        Assert.Null(live.progress);
    }

    [Fact]
    public void Capture_lifetime_drops_the_release_delegate_once()
    {
        int releases = 0;
        Action? held = () => releases++;
        var lifetime = new PerfCaptureLifetime(held);
        held = null;
        lifetime.Dispose();
        lifetime.Dispose();
        Assert.Equal(1, releases);
        Assert.True(lifetime.Disposed);
    }
}
