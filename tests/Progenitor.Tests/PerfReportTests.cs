using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The game-free half of the <c>perf</c> op (<c>src/Progenitor.Dev/Ops/PerfReport.cs</c>): argument
/// policy, ring capacity and wrap accounting, per-source accumulation, the percentile maths, and
/// the report / summary shapes the session reads. <c>PerfOps</c> itself needs Core's
/// <c>PerfProbe</c> seam and a running game, and is exercised live.
/// </summary>
[Collection(PerfProbeCollection.Name)]
public class PerfReportTests
{
    // ---- arguments ---------------------------------------------------------------------------

    [Theory]
    [InlineData(30, 30)]
    [InlineData(0, PerfArgs.MinSeconds)]
    [InlineData(-5, PerfArgs.MinSeconds)]
    [InlineData(10_000, PerfArgs.MaxSeconds)]
    public void ClampSeconds_holds_the_window_inside_the_allowed_range(double input, double expected)
    {
        Assert.Equal(expected, PerfArgs.ClampSeconds(input));
    }

    [Fact]
    public void ClampSeconds_falls_back_to_the_default_for_nan()
    {
        Assert.Equal(PerfArgs.DefaultSeconds, PerfArgs.ClampSeconds(double.NaN));
    }

    [Theory]
    [InlineData("{\"action\":\"start\"}", "start")]
    [InlineData("{\"action\":\"STOP\"}", "stop")]
    [InlineData("{\"action\":\" status \"}", "status")]
    public void ParseAction_accepts_the_three_actions_case_and_space_insensitively(string json, string expected)
    {
        Assert.Equal(expected, PerfArgs.ParseAction(Args(json)));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"action\":\"\"}")]
    [InlineData("{\"action\":\"restart\"}")]
    public void ParseAction_refuses_anything_else(string json)
    {
        Assert.Throws<ArgumentException>(() => PerfArgs.ParseAction(Args(json)));
    }

    // ---- capacity ----------------------------------------------------------------------------

    [Fact]
    public void Capacity_fits_the_requested_window_at_more_than_120_fps()
    {
        // A 30 s window on a 144 Hz client must not wrap.
        Assert.True(PerfCapacity.For(30) >= 30 * 144);
    }

    [Fact]
    public void Capacity_is_capped_at_sixty_seconds_of_120_fps()
    {
        Assert.Equal(PerfCapacity.MaxFrames, PerfCapacity.For(PerfArgs.MaxSeconds));
        Assert.Equal(60 * 120, PerfCapacity.MaxFrames);
    }

    [Fact]
    public void Capacity_never_drops_below_the_floor()
    {
        Assert.Equal(PerfCapacity.MinFrames, PerfCapacity.For(0));
        Assert.Equal(PerfCapacity.MinFrames, PerfCapacity.For(double.NaN));
    }

    // ---- closing the window (R28 findings 14 and 17) --------------------------------------------

    /// <summary>
    /// R28 finding 17. The only thing that ever closed a window was <c>Sink.CoreTick</c>, which
    /// runs inside Core's frame dispatch — and the op's own doc names the interesting cases as a
    /// boot, a main menu, or a Core-less process, none of which tick. The deadline never fired, the
    /// sink stayed installed as a Core static holding a Dev collectible object, and every later
    /// <c>perf start</c> threw "a perf window is already running". The decision is now a pure
    /// function both the Dev watchdog and the frame path call.
    /// </summary>
    [Theory]
    [InlineData(0, 10, false)]
    [InlineData(9.99, 10, false)]
    [InlineData(10, 10, true)]
    [InlineData(41, 10, true)]
    [InlineData(5, 0, false)]           // no window requested: never a deadline
    [InlineData(double.NaN, 10, false)]
    public void DeadlineReached_is_decided_without_a_frame(double elapsed, double requested, bool expected)
    {
        Assert.Equal(expected, PerfWindowPolicy.DeadlineReached(elapsed, requested));
    }

    /// <summary>
    /// R28 finding 14. The sink lives in Dev and the slot lives in Core, so a Core swap detaches
    /// them: Core's <c>Uninstall</c> clears the slot, the new generation never fills it, and the
    /// window would otherwise run forever on a dispatch that will never tick again. Any of the
    /// three signals is decisive.
    /// </summary>
    [Theory]
    // running, coreUninstalled, installedGen, currentGen, sinkStillInstalled, expected
    [InlineData(true, false, 3, 3, true, false)]     // healthy
    [InlineData(true, true, 3, 3, true, true)]       // Core said it is going away
    [InlineData(true, false, 3, 4, true, true)]      // the slot was re-installed by someone else
    [InlineData(true, false, 3, 3, false, true)]     // the installed sink is no longer ours
    [InlineData(false, true, 3, 9, false, false)]    // no window: never a swap
    public void CoreSwapped_notices_the_generation_the_sink_was_installed_into_going_away(
        bool running, bool coreUninstalled, int installed, int current, bool stillInstalled, bool expected)
    {
        Assert.Equal(
            expected,
            PerfWindowPolicy.CoreSwapped(running, coreUninstalled, installed, current, stillInstalled));
    }

    [Fact]
    public void A_core_swap_outranks_the_deadline_so_the_report_says_why_its_frames_stopped()
    {
        Assert.Equal(PerfStopReason.CoreSwap, PerfWindowPolicy.StopReasonFor(coreSwapped: true, deadlineReached: true));
        Assert.Equal(PerfStopReason.CoreSwap, PerfWindowPolicy.StopReasonFor(coreSwapped: true, deadlineReached: false));
        Assert.Equal(PerfStopReason.Deadline, PerfWindowPolicy.StopReasonFor(coreSwapped: false, deadlineReached: true));
        Assert.Null(PerfWindowPolicy.StopReasonFor(coreSwapped: false, deadlineReached: false));
        Assert.Equal("core-swap", PerfStopReason.CoreSwap);
    }

    /// <summary>
    /// The Core-side half of the same fix, driven through the real <c>PerfProbe</c> (Core code, and
    /// the only Core change this job made): the slot reports a generation that moves on every
    /// install and latches <c>CoreUninstalled</c> when Core tears the generation down. Without
    /// those two facts Dev has nothing to poll.
    /// </summary>
    [Fact]
    public void PerfProbe_reports_the_generation_and_latches_the_core_uninstall()
    {
        var probe = new NullSink();
        try
        {
            Progenitor.Dev.PerfProbe.Install(probe);
            int installed = Progenitor.Dev.PerfProbe.Generation;
            Assert.True(Progenitor.Dev.PerfProbe.Active);
            Assert.True(Progenitor.Dev.PerfProbe.IsInstalled(probe));
            Assert.False(Progenitor.Dev.PerfProbe.CoreUninstalled);
            Assert.False(
                PerfWindowPolicy.CoreSwapped(
                    true, Progenitor.Dev.PerfProbe.CoreUninstalled, installed,
                    Progenitor.Dev.PerfProbe.Generation, Progenitor.Dev.PerfProbe.IsInstalled(probe)));

            // What Core's Plugin.Uninstall does on a swap.
            Progenitor.Dev.PerfProbe.NoteCoreUninstall();

            Assert.False(Progenitor.Dev.PerfProbe.Active);
            Assert.True(Progenitor.Dev.PerfProbe.CoreUninstalled);
            Assert.NotEqual(installed, Progenitor.Dev.PerfProbe.Generation);
            Assert.False(Progenitor.Dev.PerfProbe.IsInstalled(probe));
            Assert.True(
                PerfWindowPolicy.CoreSwapped(
                    true, Progenitor.Dev.PerfProbe.CoreUninstalled, installed,
                    Progenitor.Dev.PerfProbe.Generation, Progenitor.Dev.PerfProbe.IsInstalled(probe)));

            // A fresh install clears the latch, so a later window is not mistaken for a swap.
            Progenitor.Dev.PerfProbe.Install(probe);
            Assert.False(Progenitor.Dev.PerfProbe.CoreUninstalled);
        }
        finally
        {
            Progenitor.Dev.PerfProbe.Install(null);
        }
    }

    [Fact]
    public void Silence_at_close_above_the_gap_is_endedDuringSilence()
    {
        Assert.False(PerfCloseObservation.EndedDuringSilence(PerfCloseObservation.SilenceGapMs));
        Assert.True(PerfCloseObservation.EndedDuringSilence(PerfCloseObservation.SilenceGapMs + 0.001));
        Assert.False(PerfCloseObservation.EndedDuringSilence(8));
        Assert.True(PerfCloseObservation.EndedDuringSilence(5000));
    }

    [Fact]
    public void A_capture_that_ends_mid_stall_does_not_look_like_a_clean_short_run()
    {
        var stalled = SampleReport();
        PerfCloseObservation.Stamp(stalled, sampleSilenceMsAtClose: 5000, lastFrameMs: 16);
        var clean = SampleReport();
        PerfCloseObservation.Stamp(clean, sampleSilenceMsAtClose: 8, lastFrameMs: 16);

        Assert.True(stalled.EndedDuringSilence);
        Assert.False(clean.EndedDuringSilence);

        var stalledPerf = Assert.IsType<Dictionary<string, object?>>(stalled.ToJson()["perf"]);
        var cleanPerf = Assert.IsType<Dictionary<string, object?>>(clean.ToJson()["perf"]);
        Assert.Equal(true, stalledPerf["endedDuringSilence"]);
        Assert.Equal(false, cleanPerf["endedDuringSilence"]);
        Assert.Equal(5000d, Convert.ToDouble(stalledPerf["sampleSilenceMsAtClose"]));
        Assert.Equal(16d, Convert.ToDouble(stalledPerf["lastFrameMs"]));

        var stalledFrame = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<Dictionary<string, object?>>(stalled.ToJson()["frame"])["deltaMs"]);
        Assert.True(Convert.ToDouble(stalledFrame["max"]) < 100);
        Assert.Equal(true, stalled.Summary()["endedDuringSilence"]);
    }

    [Fact]
    public void Zero_frames_with_long_silence_is_endedDuringSilence()
    {
        var report = new PerfReport { Recording = new PerfRecording(8), ElapsedSeconds = 5 };
        var progress = new PerfProgress(1, 0, 1000);
        PerfCloseObservation.Stamp(report, progress.Read(5000));
        Assert.Equal(0, report.Recording.Frames);
        Assert.True(report.EndedDuringSilence);
        Assert.Equal(5000d, report.SampleSilenceMsAtClose);
        Assert.Null(report.LastFrameMs);
    }

    [Fact]
    public void A_partial_report_says_so_in_the_json_and_the_summary()
    {
        var report = SampleReport();
        report.StopReason = PerfStopReason.CoreSwap;
        report.Partial = true;

        Assert.True(Assert.IsType<bool>(report.Summary()["partial"]));
        Assert.Equal(PerfStopReason.CoreSwap, report.Summary()["stopReason"]);

        var perf = Assert.IsType<Dictionary<string, object?>>(report.ToJson()["perf"]);
        Assert.True(Assert.IsType<bool>(perf["partial"]));
    }

    /// <summary>
    /// R28 finding 28.21: <c>sharePercent</c> is a ratio of two differently scoped quantities, so
    /// the report has to say so rather than let a reader take it for "share of Core's tick".
    /// </summary>
    [Fact]
    public void The_report_explains_the_scope_sharePercent_is_measured_over()
    {
        var progenitor = Assert.IsType<Dictionary<string, object?>>(SampleReport().ToJson()["progenitor"]);
        var note = Assert.IsType<string>(progenitor["scopeNote"]);
        Assert.Contains("coreTickMs", note);
        Assert.Contains("system update", note);
    }

    /// <summary>
    /// Every perf constant this suite reasons about, pinned <b>through the behavior it governs</b>,
    /// with the literal on the expected side.
    ///
    /// <para>R29 finding 22.2: the previous version was a block of
    /// <c>Assert.Equal(&lt;literal&gt;, &lt;the same constant&gt;)</c>, which fails when the value is
    /// edited but keeps passing if the value stops being consulted at all. Each assertion below
    /// drives the code the value is for.</para>
    /// </summary>
    [Fact]
    public void The_perf_constants_are_pinned_by_the_behaviour_they_govern()
    {
        // seconds: default when unparseable, floor and ceiling from the clamp
        Assert.Equal(30d, PerfArgs.ClampSeconds(double.NaN));
        Assert.Equal(1d, PerfArgs.ClampSeconds(0));
        Assert.Equal(300d, PerfArgs.ClampSeconds(100_000));

        // ring capacity: the ceiling really is what a 300 s window gets
        Assert.Equal(7200, PerfCapacity.For(PerfArgs.MaxSeconds));

        // the summary keeps eight source rows, however many actually carry time
        var report = new PerfReport { Side = "server", Recording = new PerfRecording(8) };
        var row = new float[PerfSourceTable.MaxSources];
        for (int i = 0; i < 12; i++) row[report.Recording.Sources.GetOrAdd("src" + i)] = 0.1f * (i + 1);
        report.Recording.AddFrame(0, 16, 0.5, row, 0, 0, 0, 0);
        var sourceRows = Assert.IsType<List<Dictionary<string, object?>>>(report.Summary()["sources"]);
        Assert.Equal(8, sourceRows.Count);

        // the stop reasons are wire strings the session's tooling reads out of the report
        report.StopReason = PerfStopReason.Deadline;
        Assert.Equal("deadline", report.Summary()["stopReason"]);
        report.StopReason = PerfStopReason.Unload;
        Assert.Equal("unload", report.Summary()["stopReason"]);
        report.StopReason = PerfStopReason.Stop;
        Assert.Equal("stop", report.Summary()["stopReason"]);
        Assert.Equal("core-swap", PerfWindowPolicy.StopReasonFor(coreSwapped: true, deadlineReached: true));
    }

    // ---- the close gate (R29 finding 5) ---------------------------------------------------------

    /// <summary>
    /// R29 finding 5. <c>CloseIfDue</c> ran on a <c>System.Threading.Timer</c> callback and called
    /// <c>Stop</c>, which took the live report and — outside every lock — walked it to build the
    /// summary and the JSON. <c>Sink.CoreTick</c>, <c>SystemHook</c> and <c>EventHandler</c> write
    /// into that same recording on the game main thread and took no lock at all, so a dictionary
    /// insert during the enumeration threw <c>InvalidOperationException</c> (reported as
    /// <c>writeError</c>, capture lost) and an array write during the copy gave a torn report.
    ///
    /// <para>This is the interleaving case, with a writer hammering the recording exactly as the
    /// sink does while the close path freezes and fully serializes it. It runs the whole
    /// <c>Summary()</c> + <c>JsonSerializer.Serialize(ToJson())</c> pair the op runs, because the
    /// dictionary enumeration is where the old code actually threw.</para>
    /// </summary>
    [Fact]
    public void Freezing_under_the_record_gate_survives_a_writer_interleaving_with_the_serialiser()
    {
        var gate = new PerfCloseGate();
        var live = new PerfRecording(128);
        var stop = false;
        Exception? writerFailure = null;

        var writer = new System.Threading.Thread(() =>
        {
            try
            {
                for (int i = 0; !System.Threading.Volatile.Read(ref stop); i++)
                {
                    lock (gate.RecordGate)
                    {
                        var row = new float[PerfSourceTable.MaxSources];
                        row[live.Sources.GetOrAdd("core:effects")] = 0.5f;
                        live.AddFrame(i * 0.016, 16, 0.5, row, i % 4, 0, 2048, 100_000);
                        live.AddHook("plugin" + (i % 96), "SomeSystem", i % 2 == 0, 0.01);
                        live.AddEvent("plugin" + (i % 96), "SomeEvent" + (i % 96), 0.02);
                    }
                }
            }
            catch (Exception e) { writerFailure = e; }
        });

        writer.Start();
        try
        {
            for (int round = 0; round < 200; round++)
            {
                var frozen = gate.Freeze(live);
                var report = new PerfReport { Side = "server", Recording = frozen };
                var summary = report.Summary();
                var json = JsonSerializer.Serialize(report.ToJson());
                Assert.NotNull(summary);
                Assert.Contains("stopReason", json);
                // The copy is a fixed point: nothing the writer does afterwards reaches it.
                int frames = frozen.Frames;
                System.Threading.Thread.Yield();
                Assert.Equal(frames, frozen.Frames);
            }
        }
        finally
        {
            System.Threading.Volatile.Write(ref stop, true);
            writer.Join(5_000);
        }

        Assert.Null(writerFailure);
    }

    /// <summary>
    /// The freeze is mutually exclusive with the sink's writes: while a writer holds the record
    /// gate, <see cref="PerfCloseGate.Freeze"/> waits rather than reading a half-written ring.
    /// </summary>
    [Fact]
    public void Freeze_waits_for_a_writer_that_holds_the_record_gate()
    {
        var gate = new PerfCloseGate();
        var live = new PerfRecording(8);
        var entered = new System.Threading.ManualResetEventSlim(false);
        var finished = new System.Threading.ManualResetEventSlim(false);

        var freezer = new System.Threading.Thread(() =>
        {
            entered.Set();
            gate.Freeze(live);
            finished.Set();
        });

        lock (gate.RecordGate)
        {
            freezer.Start();
            Assert.True(entered.Wait(5_000));
            Assert.False(finished.Wait(200));      // blocked on the gate this thread holds
        }

        Assert.True(finished.Wait(5_000));
        freezer.Join(5_000);
    }

    /// <summary>A snapshot is detached: later writes to the live recording never reach it.</summary>
    [Fact]
    public void A_snapshot_is_detached_from_the_recording_it_came_from()
    {
        var live = new PerfRecording(16);
        var row = new float[PerfSourceTable.MaxSources];
        row[live.Sources.GetOrAdd("core:effects")] = 0.5f;
        live.AddFrame(0, 16, 0.5, row, 1, 0, 2048, 100_000);
        live.AddHook("p", "S", true, 0.01);
        live.AddEvent("p", "E", 0.02);

        var frozen = live.Snapshot();

        live.AddFrame(0.016, 33, 0.5, row, 2, 0, 4096, 200_000);
        live.AddHook("p2", "S2", false, 0.05);
        live.AddEvent("p2", "E2", 0.06);
        live.Sources.GetOrAdd("core:units");

        Assert.Equal(1, frozen.Frames);
        Assert.Equal(2, live.Frames);
        Assert.Single(frozen.Hooks);
        Assert.Single(frozen.Events);
        Assert.Equal(2, live.Hooks.Count);
        Assert.Equal(16d, frozen.FrameMs(0), 3);
        Assert.Equal(0.5, frozen.SourceMs(0, frozen.Sources.IndexOf("core:effects")), 3);
        Assert.Equal(-1, frozen.Sources.IndexOf("core:units"));
        Assert.Equal(0.01, frozen.Hooks[("p", "S", true)].TotalMs, 5);
    }

    /// <summary>
    /// The rule that keeps the watchdog out of the serializer: a thread that is not the window's
    /// owner may only close a window whose recording has gone quiet. In a process that ticks — the
    /// case where a main-thread write can be in flight — it must request and let the main thread do
    /// it. On the pre-fix code no such rule existed and the timer always closed.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(0.5, false)]
    [InlineData(1.999, false)]
    [InlineData(2.0, true)]
    [InlineData(30, true)]
    [InlineData(double.NaN, false)]
    public void A_non_owner_thread_may_close_only_a_quiescent_window(double sinceLastWrite, bool expected)
    {
        Assert.Equal(expected, PerfCloseGate.MayCloseOffOwnerThread(sinceLastWrite));
        Assert.Equal(2.0, PerfCloseGate.QuiescentSeconds);
    }

    /// <summary>
    /// The first reason latched wins: a Core swap the watchdog noticed must still be reported as
    /// <c>core-swap</c> when the main thread performs the close, not relabeled by a later poll that
    /// only saw the deadline.
    /// </summary>
    [Fact]
    public void The_close_request_keeps_the_first_reason_and_is_taken_once()
    {
        var gate = new PerfCloseGate();
        Assert.Null(gate.PendingReason);
        Assert.False(gate.Request(""));

        Assert.True(gate.Request(PerfStopReason.CoreSwap));
        Assert.False(gate.Request(PerfStopReason.Deadline));
        Assert.Equal(PerfStopReason.CoreSwap, gate.PendingReason);

        Assert.Equal(PerfStopReason.CoreSwap, gate.Take());
        Assert.Null(gate.Take());
        Assert.Null(gate.PendingReason);
    }

    /// <summary>The owner is the thread that started the window, and only that thread.</summary>
    [Fact]
    public void The_owner_thread_is_the_one_that_claimed_the_gate()
    {
        var gate = new PerfCloseGate();
        Assert.False(gate.OnOwnerThread);          // nothing claimed it yet
        gate.ClaimOwner();
        Assert.True(gate.OnOwnerThread);

        bool otherSawOwnership = true;
        var other = new System.Threading.Thread(() => otherSawOwnership = gate.OnOwnerThread);
        other.Start();
        other.Join(5_000);
        Assert.False(otherSawOwnership);
    }

    /// <summary>An <c>IPerfSink</c> that records nothing: the tests only need its identity.</summary>
    private sealed class NullSink : Progenitor.Dev.IPerfSink
    {
        public void FrameHandler(Delegate handler, long ticks) { }

        public void CoreTick(long ticks, long startTimestamp) { }

        public void SystemHook(string pluginId, string systemName, bool before, long ticks) { }

        public void EventHandler(string pluginId, Type eventType, long ticks) { }
    }

    // ---- source classification ----------------------------------------------------------------

    [Theory]
    [InlineData("Progenitor.Effects.ServerBeam", "Progenitor", "core:effects")]
    [InlineData("Progenitor.Units.Units", "Progenitor", "core:units")]
    [InlineData("Progenitor.Content.PrefabRegistry", "Progenitor", "core:content")]
    [InlineData("Progenitor.Networking.ModManifest", "Progenitor", "core:networking")]
    [InlineData("Progenitor.Events.EventHooks", "Progenitor", "core:events")]
    [InlineData("Progenitor.Hooks.SystemHooks", "Progenitor", "core:hooks")]
    [InlineData("Progenitor.Systems.VirtualSystems", "Progenitor", "core:systems")]
    public void ClassifyFrameHandler_names_a_core_handler_after_its_module(string type, string asm, string expected)
    {
        Assert.Equal(expected, PerfSourceTable.ClassifyFrameHandler(type, asm));
    }

    [Fact]
    public void ClassifyFrameHandler_names_each_networking_handler_on_its_own()
    {
        // 2026-10-06: one networking subsystem was 78% of Progenitor's frame time behind a single bucket.
        Assert.Equal("core:networking/JoinFlowClient.FrameLastServer",
            PerfSourceTable.ClassifyFrameHandler("Progenitor.Networking.JoinFlowClient", "Progenitor", "FrameLastServer"));
        Assert.Equal("core:networking/JoinFlowPanel.OnFrame",
            PerfSourceTable.ClassifyFrameHandler("Progenitor.Networking.JoinFlowPanel+<>c", "Progenitor", "OnFrame"));
        // Other modules keep one bucket; without a method name networking does too.
        Assert.Equal("core:ui", PerfSourceTable.ClassifyFrameHandler("Progenitor.Ui.Keybinds", "Progenitor", "Pump"));
        Assert.Equal("core:networking", PerfSourceTable.ClassifyFrameHandler("Progenitor.Networking.ModManifest", "Progenitor", null));
        Assert.Equal("plugin:Progenitor.Sample", PerfSourceTable.ClassifyFrameHandler("Progenitor.Sample.SampleMods", "Progenitor.Sample", "Tick"));
    }

    [Fact]
    public void ClassifyFrameHandler_reads_through_a_compiler_generated_closure()
    {
        Assert.Equal("core:effects", PerfSourceTable.ClassifyFrameHandler("Progenitor.Effects.ServerBeam+<>c__DisplayClass3_0", "Progenitor"));
    }

    [Fact]
    public void ClassifyFrameHandler_calls_a_root_namespace_type_core_not_its_type_name()
    {
        // Progenitor.Plugin's first segment after the prefix is the TYPE name; naming the bucket
        // "core:plugin" from it would be a lie about which module the time belongs to.
        Assert.Equal("core:core", PerfSourceTable.ClassifyFrameHandler("Progenitor.Plugin", "Progenitor"));
    }

    [Theory]
    [InlineData("Progenitor.Dev.Ops.CaptureOps", "Progenitor.Dev", "plugin:Progenitor.Dev")]
    [InlineData("Progenitor.Sample.SampleMods", "Progenitor.Sample", "plugin:Progenitor.Sample")]
    public void ClassifyFrameHandler_names_a_consumer_handler_after_its_assembly(string type, string asm, string expected)
    {
        Assert.Equal(expected, PerfSourceTable.ClassifyFrameHandler(type, asm));
    }

    [Fact]
    public void ClassifyFrameHandler_degrades_to_unknown_without_an_assembly()
    {
        Assert.Equal(PerfSourceTable.UnknownName, PerfSourceTable.ClassifyFrameHandler("Whatever", null));
        Assert.Equal(PerfSourceTable.UnknownName, PerfSourceTable.ClassifyFrameHandler(null, "  "));
    }

    [Fact]
    public void Hook_and_event_buckets_carry_the_plugin_id_and_fall_back_to_core()
    {
        Assert.Equal("hook:progenitor.sample", PerfSourceTable.HookSource("progenitor.sample"));
        Assert.Equal("event:progenitor.sample", PerfSourceTable.EventSource("progenitor.sample"));
        Assert.Equal("hook:core", PerfSourceTable.HookSource(""));
        Assert.Equal("event:core", PerfSourceTable.EventSource(null));
    }

    // ---- source table --------------------------------------------------------------------------

    [Fact]
    public void Source_table_reserves_index_zero_for_other_and_is_stable()
    {
        var table = new PerfSourceTable();
        Assert.Equal(0, table.IndexOf(PerfSourceTable.OtherName));

        int a = table.GetOrAdd("core:effects");
        int b = table.GetOrAdd("core:units");
        Assert.Equal(a, table.GetOrAdd("core:effects"));
        Assert.NotEqual(a, b);
        Assert.False(table.Overflowed);
    }

    [Fact]
    public void Source_table_overflow_lands_in_other_and_is_flagged()
    {
        var table = new PerfSourceTable();
        for (int i = 0; i < PerfSourceTable.MaxSources + 10; i++) table.GetOrAdd("source" + i);

        Assert.True(table.Overflowed);
        Assert.Equal(PerfSourceTable.MaxSources, table.Count);
        Assert.Equal(0, table.GetOrAdd("a source that never fitted"));
    }

    // ---- percentiles ----------------------------------------------------------------------------

    [Fact]
    public void Percentile_is_nearest_rank_over_the_sorted_slice()
    {
        var values = new float[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        Assert.Equal(5, PerfStats.Percentile(values, 10, 50));
        Assert.Equal(10, PerfStats.Percentile(values, 10, 95));
        Assert.Equal(10, PerfStats.Percentile(values, 10, 100));
        Assert.Equal(1, PerfStats.Percentile(values, 10, 0));
    }

    [Fact]
    public void Percentile_only_reads_the_first_count_entries()
    {
        // The scratch buffer is reused and longer than the recording; trailing garbage must not
        // move a percentile.
        var values = new float[] { 1, 2, 3, 999, 999 };
        Assert.Equal(2, PerfStats.Percentile(values, 3, 50));
        Assert.Equal(3, PerfStats.Percentile(values, 3, 95));
    }

    [Fact]
    public void Percentile_of_an_empty_slice_is_zero()
    {
        Assert.Equal(0, PerfStats.Percentile(Array.Empty<float>(), 0, 95));
        Assert.Equal(0, PerfStats.Percentile(new float[4], 0, 50));
    }

    [Fact]
    public void Mean_sum_and_max_read_the_slice_only()
    {
        var values = new float[] { 2, 4, 6, 100 };
        Assert.Equal(4, PerfStats.Mean(values, 3));
        Assert.Equal(12, PerfStats.Sum(values, 3));
        Assert.Equal(6, PerfStats.Max(values, 3));
    }

    // ---- ring buffer ------------------------------------------------------------------------------

    [Fact]
    public void Ring_keeps_every_frame_until_it_is_full()
    {
        var recording = new PerfRecording(8);
        for (int i = 0; i < 8; i++) AddFrame(recording, atSeconds: i, frameMs: i + 1);

        Assert.Equal(8, recording.Frames);
        Assert.Equal(8, recording.Recorded);
        Assert.False(recording.Wrapped);
        Assert.Equal(1, recording.FrameMs(0));
        Assert.Equal(8, recording.FrameMs(7));
    }

    [Fact]
    public void Ring_wraps_and_keeps_the_most_recent_frames_in_order()
    {
        var recording = new PerfRecording(4);
        for (int i = 0; i < 10; i++) AddFrame(recording, atSeconds: i, frameMs: i + 1);

        Assert.Equal(10, recording.Frames);
        Assert.Equal(4, recording.Recorded);
        Assert.True(recording.Wrapped);
        // Frames 7, 8, 9, 10 (values), oldest first.
        Assert.Equal(7, recording.FrameMs(0));
        Assert.Equal(8, recording.FrameMs(1));
        Assert.Equal(9, recording.FrameMs(2));
        Assert.Equal(10, recording.FrameMs(3));
    }

    [Fact]
    public void CopySeries_returns_the_ring_in_oldest_first_order()
    {
        var recording = new PerfRecording(4);
        for (int i = 0; i < 6; i++) AddFrame(recording, atSeconds: i, frameMs: i + 1);

        var buffer = new float[16];
        int n = recording.CopySeries(PerfSeries.FrameMs, buffer);
        Assert.Equal(4, n);
        Assert.Equal(new float[] { 3, 4, 5, 6 }, new[] { buffer[0], buffer[1], buffer[2], buffer[3] });
    }

    // ---- per-source accumulation -------------------------------------------------------------------

    [Fact]
    public void Per_source_row_is_copied_out_not_retained()
    {
        var recording = new PerfRecording(4);
        int effects = recording.Sources.GetOrAdd("core:effects");

        var row = new float[PerfSourceTable.MaxSources];
        row[effects] = 2.5f;
        recording.AddFrame(0, 16, 3, row, 0, 0, 0, 0);

        // The caller reuses the row; the recording must already have its own copy.
        row[effects] = 99f;
        Assert.Equal(2.5, recording.SourceMs(0, effects), 3);
    }

    [Fact]
    public void Handler_total_per_frame_is_the_sum_of_its_sources()
    {
        var recording = new PerfRecording(4);
        int effects = recording.Sources.GetOrAdd("core:effects");
        int units = recording.Sources.GetOrAdd("core:units");

        var row = new float[PerfSourceTable.MaxSources];
        row[effects] = 1.5f;
        row[units] = 0.5f;
        recording.AddFrame(0, 16, 3, row, 0, 0, 0, 0);

        Assert.Equal(2.0, recording.HandlersMs(0), 3);
    }

    [Fact]
    public void Hook_and_event_detail_accumulate_per_key_and_stop_at_the_row_cap()
    {
        var recording = new PerfRecording(4);
        recording.AddHook("sample", "ServerBootstrapSystem", before: true, ms: 1);
        recording.AddHook("sample", "ServerBootstrapSystem", before: true, ms: 3);
        recording.AddHook("sample", "ServerBootstrapSystem", before: false, ms: 5);

        var stat = recording.Hooks[("sample", "ServerBootstrapSystem", true)];
        Assert.Equal(2, stat.Calls);
        Assert.Equal(4d, stat.TotalMs, 3);
        Assert.Equal(3d, stat.MaxMs, 3);
        // Before and After of the same system are different keys.
        Assert.Equal(2, recording.Hooks.Count);
        Assert.False(recording.HookDetailTruncated);

        for (int i = 0; i < PerfRecording.MaxDetailRows + 5; i++)
            recording.AddEvent("p" + i, "ChatMessage", 1);
        Assert.True(recording.EventDetailTruncated);
        Assert.Equal(PerfRecording.MaxDetailRows, recording.Events.Count);
    }

    [Fact]
    public void Worst_frame_is_the_one_with_the_most_progenitor_time()
    {
        var recording = new PerfRecording(8);
        int effects = recording.Sources.GetOrAdd("core:effects");
        for (int i = 0; i < 5; i++)
        {
            var row = new float[PerfSourceTable.MaxSources];
            row[effects] = i == 3 ? 12f : 0.2f;
            recording.AddFrame(i, 16, 1, row, 0, 0, 0, 0);
        }

        Assert.Equal(3, recording.WorstFrameIndex());
        Assert.Equal(new[] { 3 }, recording.WorstFrames(1).ToArray());
    }

    // ---- report shapes -------------------------------------------------------------------------------

    [Fact]
    public void Summary_reports_the_percentiles_and_the_worst_frame_breakdown()
    {
        var report = SampleReport();
        var summary = report.Summary();

        Assert.Equal(6, summary["frames"]);
        Assert.Equal("stop", summary["stopReason"]);

        var frame = Assert.IsType<Dictionary<string, object?>>(summary["frameMs"]);
        Assert.Equal(6, frame["samples"]);

        var progenitor = Assert.IsType<Dictionary<string, object?>>(summary["progenitorMs"]);
        Assert.Equal(10.0, Convert.ToDouble(progenitor["max"]));

        var sources = Assert.IsType<List<Dictionary<string, object?>>>(summary["sources"]);
        Assert.Equal("core:effects", sources[0]["name"]);        // heaviest first

        var worst = Assert.IsType<Dictionary<string, object?>>(summary["worstFrame"]);
        Assert.Equal(4, worst["frame"]);
        var breakdown = Assert.IsType<Dictionary<string, object?>>(worst["sources"]);
        Assert.Equal(10.0, Convert.ToDouble(breakdown["core:effects"]));
        Assert.False(breakdown.ContainsKey("core:units"));       // zero rows are left out
    }

    [Fact]
    public void Summary_source_list_is_capped()
    {
        var report = new PerfReport { Recording = new PerfRecording(4) };
        var row = new float[PerfSourceTable.MaxSources];
        for (int i = 0; i < PerfSourceTable.MaxSources - 1; i++)
            row[report.Recording.Sources.GetOrAdd("source" + i)] = i + 1;
        report.Recording.AddFrame(0, 16, 5, row, 0, 0, 0, 0);

        var sources = Assert.IsType<List<Dictionary<string, object?>>>(report.Summary()["sources"]);
        Assert.Equal(PerfReport.SummarySourceRows, sources.Count);
    }

    [Fact]
    public void Report_json_round_trips_and_carries_every_top_level_section()
    {
        var report = SampleReport();
        var json = JsonSerializer.Serialize(report.ToJson(), new JsonSerializerOptions { WriteIndented = true });
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        foreach (var key in new[] { "perf", "frame", "progenitor", "sources", "hooks", "events", "mainThread", "gc", "worstFrames" })
            Assert.True(root.TryGetProperty(key, out _), $"report is missing '{key}'");

        var perf = root.GetProperty("perf");
        Assert.Equal("server", perf.GetProperty("side").GetString());
        Assert.False(perf.GetProperty("wrapped").GetBoolean());
        Assert.Equal(PerfAllocApi.PerThread, perf.GetProperty("allocApi").GetString());

        // Share of the frame is derived, not stored: 16 ms frames, 12.7 ms of Progenitor over six frames.
        double share = root.GetProperty("progenitor").GetProperty("sharePercent").GetDouble();
        Assert.InRange(share, 12, 14);
    }

    [Fact]
    public void Report_json_says_when_the_ring_wrapped()
    {
        var report = new PerfReport { Recording = new PerfRecording(4), ElapsedSeconds = 1 };
        var row = new float[PerfSourceTable.MaxSources];
        for (int i = 0; i < 9; i++) report.Recording.AddFrame(i * 0.01, 8, 1, row, 0, 0, 0, 0);

        var perf = Assert.IsType<Dictionary<string, object?>>(report.ToJson()["perf"]);
        Assert.Equal(9, perf["frames"]);
        Assert.Equal(4, perf["framesRecorded"]);
        Assert.Equal(true, perf["wrapped"]);
    }

    [Fact]
    public void Allocation_rate_uses_the_span_of_the_recorded_frames()
    {
        var report = new PerfReport { Recording = new PerfRecording(8), ElapsedSeconds = 2 };
        var row = new float[PerfSourceTable.MaxSources];
        for (int i = 0; i < 5; i++) report.Recording.AddFrame(i * 0.5, 16, 1, row, 0, 0, 1000, 4096);

        Assert.Equal(5000L, report.AllocatedBytes());
        Assert.Equal(2.0, report.RecordedSeconds(), 3);       // 0.0 s to 2.0 s
        Assert.Equal(2500d, report.AllocatedBytesPerSecond(), 1);
    }

    // ---- report path ----------------------------------------------------------------------------------

    [Fact]
    public void Report_folder_is_a_sibling_of_the_reload_folder()
    {
        string reload = Path.Combine("C:", "game", "BepInEx", "progenitor-reload");
        string folder = PerfReportPaths.Folder(reload);

        Assert.Equal(Path.Combine("C:", "game", "BepInEx", PerfReportPaths.FolderName), folder);
        // Never inside the reload folder: the reloader scans that for plugin DLLs.
        Assert.DoesNotContain("progenitor-reload", folder);
    }

    [Fact]
    public void Report_folder_tolerates_a_trailing_separator_and_an_empty_reload_folder()
    {
        string reload = Path.Combine("C:", "game", "BepInEx", "progenitor-reload") + Path.DirectorySeparatorChar;
        Assert.Equal(Path.Combine("C:", "game", "BepInEx", PerfReportPaths.FolderName), PerfReportPaths.Folder(reload));
        Assert.Equal(PerfReportPaths.FolderName, PerfReportPaths.Folder(""));
    }

    [Fact]
    public void Report_file_is_named_by_the_start_timestamp()
    {
        var utc = new DateTime(2026, 9, 3, 19, 4, 5, 678, DateTimeKind.Utc);
        Assert.Equal("20260903-190405-678.json", PerfReportPaths.FileName(utc));
        Assert.EndsWith(Path.Combine(PerfReportPaths.FolderName, "20260903-190405-678.json"),
            PerfReportPaths.For(Path.Combine("C:", "game", "BepInEx", "progenitor-reload"), utc));
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void AddFrame(PerfRecording recording, double atSeconds, double frameMs)
    {
        recording.AddFrame(atSeconds, frameMs, 0.5, new float[PerfSourceTable.MaxSources], 0, 0, 0, 0);
    }

    /// <summary>
    /// Six frames at 16 ms. Effects costs 0.5 ms per frame except frame 4, which costs 10 ms
    /// (the hitch the worst-frame section is for); Units costs 0.2 ms on the first frame only.
    /// </summary>
    private static PerfReport SampleReport()
    {
        var report = new PerfReport
        {
            Side = "server",
            World = "Server",
            StartedUtc = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc),
            RequestedSeconds = 30,
            ElapsedSeconds = 0.096,
            StopReason = "stop",
            AllocApi = PerfAllocApi.PerThread,
            QueueSource = "Progenitor.Bootstrap.WorkQueue",
            Recording = new PerfRecording(64),
        };

        int effects = report.Recording.Sources.GetOrAdd("core:effects");
        int units = report.Recording.Sources.GetOrAdd("core:units");

        for (int i = 0; i < 6; i++)
        {
            var row = new float[PerfSourceTable.MaxSources];
            row[effects] = i == 4 ? 10f : 0.5f;
            if (i == 0) row[units] = 0.2f;
            report.Recording.AddFrame(i * 0.016, 16, row[effects] + row[units] + 0.05, row, i, 0, 2048, 100_000);
        }

        report.Recording.AddHook("progenitor.sample", "ServerBootstrapSystem", before: true, ms: 0.01);
        report.Recording.AddEvent("progenitor.sample", "ChatMessage", 0.02);
        return report;
    }
}
