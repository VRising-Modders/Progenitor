using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Progenitor.Dev;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The outside-the-tick half of the <c>perf</c> report: IL2CPP heap growth and per-source bytes,
/// the collector counters and the frames they ran in, timed patch bodies, seam gaps, and the
/// <c>slowestFrames</c> list ordered by frame time. The numbers come from a live game; what is
/// tested here is the accounting and the report shape.
/// </summary>
[Collection(PerfProbeCollection.Name)]
public class PerfIl2CppTests
{
    private static double[] NoSeams()
    {
        var seams = new double[PerfSeams.Count];
        for (int i = 0; i < seams.Length; i++) seams[i] = double.NaN;
        return seams;
    }

    private static PerfFrameExtra Extra(
        long used,
        int il2cppGc = 0,
        int gen0 = 0,
        long[]? sourceBytes = null,
        double[]? seams = null,
        double windowMs = 10,
        double[]? patchMs = null,
        int[]? patchCalls = null) => new()
        {
            Il2CppUsedBytes = used,
            Il2CppHeapBytes = 300_000_000,
            Il2CppCollections = il2cppGc,
            Gen0 = gen0,
            Il2CppSourceBytes = sourceBytes,
            SeamOffsetMs = seams ?? NoSeams(),
            WindowMs = windowMs,
            PatchMs = patchMs,
            PatchCalls = patchCalls,
        };

    // ---- seam gaps ------------------------------------------------------------------------------

    [Fact]
    public void Largest_gap_names_the_two_seams_around_the_slow_stretch()
    {
        var seams = NoSeams();
        seams[PerfSeams.SimulationBefore] = 2;
        seams[PerfSeams.SimulationAfter] = 6;
        seams[PerfSeams.PresentationBefore] = 7;
        seams[PerfSeams.PresentationAfter] = 75;   // presentation held 68 ms
        seams[PerfSeams.LateFrame] = 76;

        double gap = PerfSeamGaps.Largest(seams, windowMs: 80, out int from, out int to);

        Assert.Equal(68, gap, 3);
        Assert.Equal("presentation.before", PerfSeamGaps.Label(from));
        Assert.Equal("presentation.after", PerfSeamGaps.Label(to));
    }

    [Fact]
    public void A_seam_that_did_not_fire_is_left_out_and_the_window_bounds_take_its_place()
    {
        var seams = NoSeams();
        seams[PerfSeams.LateFrame] = 5;

        double gap = PerfSeamGaps.Largest(seams, windowMs: 85, out int from, out int to);

        Assert.Equal(80, gap, 3);
        Assert.Equal("lateFrame", PerfSeamGaps.Label(from));
        Assert.Equal(PerfSeamGaps.WindowEndName, PerfSeamGaps.Label(to));
    }

    [Fact]
    public void An_unusable_window_has_no_gap()
    {
        Assert.Equal(0, PerfSeamGaps.Largest(NoSeams(), double.NaN, out int from, out int to));
        Assert.Equal(PerfSeamGaps.WindowStart, from);
        Assert.Equal(PerfSeamGaps.WindowEnd, to);
    }

    [Fact]
    public void Seam_offsets_outside_the_window_are_clamped_into_it()
    {
        var seams = NoSeams();
        seams[PerfSeams.SimulationBefore] = -3;
        seams[PerfSeams.SimulationAfter] = 500;

        double gap = PerfSeamGaps.Largest(seams, windowMs: 20, out int from, out int to);

        Assert.Equal(20, gap, 3);
        Assert.Equal("simulation.before", PerfSeamGaps.Label(from));
        Assert.Equal("simulation.after", PerfSeamGaps.Label(to));
    }

    // ---- il2cpp accounting -----------------------------------------------------------------------

    [Fact]
    public void Growth_counts_only_frames_without_an_il2cpp_collection_and_the_rest_is_outside()
    {
        var recording = new PerfRecording(64);
        int src = recording.Sources.GetOrAdd("core:ui");
        var row = new float[PerfSourceTable.MaxSources];
        var bytes = new long[PerfSourceTable.MaxSources];

        recording.AddFrame(0.00, 10, 0.1, row, 0, 0, 0, 0, Extra(used: 1_000_000));
        bytes[src] = 4_000;
        recording.AddFrame(0.01, 10, 0.1, row, 0, 0, 0, 0, Extra(used: 1_016_384, sourceBytes: bytes));
        Array.Clear(bytes);
        // A collection: used size drops; this frame's growth must not count.
        recording.AddFrame(0.02, 85, 0.1, row, 0, 0, 0, 0, Extra(used: 600_000, il2cppGc: 1));
        recording.AddFrame(0.03, 10, 0.1, row, 0, 0, 0, 0, Extra(used: 608_192, gen0: 1));

        var d = recording.Detail;
        Assert.True(d.Il2CppAvailable);
        Assert.Equal(16_384 + 8_192, d.Il2CppGrowthNonGc);
        Assert.Equal(4_000, d.Il2CppAttributedNonGc);
        Assert.Equal(4_000, d.SourceIl2CppBytes[src]);
        Assert.Equal(1, d.Il2CppCollections);
        Assert.Equal(1, d.Gen0Collections);
        Assert.Equal(2, d.GcEventCount);
        Assert.Equal(PerfGcKinds.Il2Cpp, d.GcEvents[0].Kinds);
        Assert.Equal(85f, d.GcEvents[0].FrameMs);
        Assert.Equal(1_016_384, d.GcEvents[0].Il2CppUsedBefore);
        Assert.Equal(600_000, d.GcEvents[0].Il2CppUsedAfter);
        Assert.Equal(PerfGcKinds.Gen0, d.GcEvents[1].Kinds);
    }

    [Fact]
    public void An_unavailable_reading_records_no_growth()
    {
        var recording = new PerfRecording(16);
        var row = new float[PerfSourceTable.MaxSources];
        recording.AddFrame(0, 16, 0.1, row, 0, 0, 0, 0);
        recording.AddFrame(0.016, 16, 0.1, row, 0, 0, 0, 0);

        Assert.False(recording.Detail.Il2CppAvailable);
        Assert.Equal(0, recording.Detail.Il2CppGrowthNonGc);
        Assert.Equal(0, recording.Detail.GcEventCount);
    }

    [Fact]
    public void Gc_events_past_the_cap_are_counted_not_kept()
    {
        var recording = new PerfRecording(PerfCapacity.MinFrames);
        var row = new float[PerfSourceTable.MaxSources];
        int frames = PerfFrameDetail.MaxGcEvents + 5;
        for (int i = 0; i < frames; i++)
            recording.AddFrame(i * 0.01, 10, 0.1, row, 0, 0, 0, 0, Extra(used: 1000, gen0: 1));

        Assert.Equal(PerfFrameDetail.MaxGcEvents, recording.Detail.GcEventCount);
        Assert.Equal(5, recording.Detail.GcEventsDropped);
        Assert.Equal(frames, recording.Detail.Gen0Collections);
    }

    [Fact]
    public void Patch_bodies_accumulate_calls_totals_and_the_worst_frame()
    {
        var recording = new PerfRecording(16);
        var row = new float[PerfSourceTable.MaxSources];
        var ms = new double[PerfPatchTimer.Count];
        var calls = new int[PerfPatchTimer.Count];
        ms[PerfPatchTimer.SystemPrefixManaged] = 0.5; calls[PerfPatchTimer.SystemPrefixManaged] = 900;
        recording.AddFrame(0, 10, 0.1, row, 0, 0, 0, 0, Extra(used: 10, patchMs: ms, patchCalls: calls));
        ms[PerfPatchTimer.SystemPrefixManaged] = 1.5;
        recording.AddFrame(0.01, 10, 0.1, row, 0, 0, 0, 0, Extra(used: 10, patchMs: ms, patchCalls: calls));

        var d = recording.Detail;
        Assert.Equal(2.0, d.PatchTotalMs[PerfPatchTimer.SystemPrefixManaged], 6);
        Assert.Equal(1800, d.PatchCalls[PerfPatchTimer.SystemPrefixManaged]);
        Assert.Equal(1.5, d.PatchMaxFrameMs[PerfPatchTimer.SystemPrefixManaged], 6);
    }

    [Fact]
    public void Snapshot_carries_the_detail()
    {
        var recording = new PerfRecording(16);
        var row = new float[PerfSourceTable.MaxSources];
        recording.AddFrame(0, 10, 0.1, row, 0, 0, 0, 0, Extra(used: 100));
        recording.AddFrame(0.01, 90, 0.1, row, 0, 0, 0, 0, Extra(used: 50, il2cppGc: 1));

        var copy = recording.Snapshot();
        recording.AddFrame(0.02, 10, 0.1, row, 0, 0, 0, 0, Extra(used: 60, il2cppGc: 1));

        Assert.Equal(1, copy.Detail.Il2CppCollections);
        Assert.Equal(1, copy.Detail.GcEventCount);
        Assert.Equal(PerfGcKinds.Il2Cpp, copy.Detail.GcKinds(copy.Slot(1)));
    }

    // ---- report shape ---------------------------------------------------------------------------

    private static PerfReport ReportWithHitch()
    {
        var report = new PerfReport { Side = "client", Recording = new PerfRecording(64), ElapsedSeconds = 1 };
        var recording = report.Recording;
        int ui = recording.Sources.GetOrAdd("core:ui");
        var row = new float[PerfSourceTable.MaxSources];
        var bytes = new long[PerfSourceTable.MaxSources];
        long used = 200_000_000;
        for (int i = 0; i < 40; i++)
        {
            bool hitch = i == 20;
            row[ui] = 0.2f;
            bytes[ui] = 1_000;
            used += 16_384;
            var seams = NoSeams();
            seams[PerfSeams.SimulationBefore] = 1;
            seams[PerfSeams.SimulationAfter] = 4;
            seams[PerfSeams.PresentationBefore] = 5;
            seams[PerfSeams.PresentationAfter] = 8;
            seams[PerfSeams.LateFrame] = 9;
            seams[PerfSeams.BootstrapDrain] = hitch ? 10 : 10;
            double window = hitch ? 85 : 11;
            recording.AddFrame(i * 0.016, hitch ? 86 : 12, 0.3, row, 0, 0, 512, 1,
                Extra(used: hitch ? 150_000_000 : used, il2cppGc: hitch ? 1 : 0, sourceBytes: bytes, seams: seams, windowMs: window));
            if (hitch) used = 150_000_000;
        }
        return report;
    }

    [Fact]
    public void Report_json_has_the_il2cpp_gc_patch_seam_and_slowest_frame_sections_and_serializes()
    {
        var report = ReportWithHitch();
        var json = report.ToJson();

        foreach (var key in new[] { "il2cpp", "clrGc", "patches", "seams", "slowestFrames", "worstFrames" })
            Assert.True(json.ContainsKey(key), key);

        string text = JsonSerializer.Serialize(json);
        using var doc = JsonDocument.Parse(text);
        var il2cpp = doc.RootElement.GetProperty("il2cpp");
        Assert.True(il2cpp.GetProperty("available").GetBoolean());
        Assert.Equal(1, il2cpp.GetProperty("gcCount").GetInt64());
        var gcFrames = il2cpp.GetProperty("gcFrames");
        Assert.Equal(1, gcFrames.GetArrayLength());
        Assert.Equal(86, gcFrames[0].GetProperty("frameMs").GetDouble(), 3);
        Assert.Equal("il2cpp", gcFrames[0].GetProperty("kinds")[0].GetString());
        var bySource = il2cpp.GetProperty("bytesPerSecondBySource");
        Assert.True(bySource.TryGetProperty("core:ui", out _));
        Assert.True(bySource.TryGetProperty("outside", out var outside));
        Assert.True(outside.GetDouble() > 0);
    }

    [Fact]
    public void Slowest_frames_are_ordered_by_frame_time_and_name_the_gap_and_the_collector()
    {
        var report = ReportWithHitch();
        var rows = report.SlowestFrameRows(3, withSources: true);

        Assert.Equal(86, Convert.ToDouble(rows[0]["frameMs"]), 3);
        Assert.True(Convert.ToDouble(rows[0]["frameMs"]) >= Convert.ToDouble(rows[1]["frameMs"]));
        var gc = Assert.IsType<List<string>>(rows[0]["gc"]);
        Assert.Contains("il2cpp", gc);
        var gap = Assert.IsType<Dictionary<string, object?>>(rows[0]["largestGap"]);
        Assert.Equal("bootstrapTick.drain", gap["from"]);
        Assert.Equal(PerfSeamGaps.WindowEndName, gap["to"]);
        Assert.Equal(75.0, Convert.ToDouble(gap["ms"]), 3);
        Assert.True(Convert.ToDouble(rows[0]["outsideMs"]) > 80);
    }

    [Fact]
    public void Summary_carries_the_compact_il2cpp_section_and_slowest_frames()
    {
        var summary = ReportWithHitch().Summary();
        Assert.True(summary.ContainsKey("il2cpp"));
        Assert.True(summary.ContainsKey("slowestFrames"));
        Assert.True(summary.ContainsKey("clrGc"));
        JsonSerializer.Serialize(summary);
    }

    [Fact]
    public void A_report_without_detail_still_serializes()
    {
        var report = new PerfReport { Recording = new PerfRecording(16) };
        var row = new float[PerfSourceTable.MaxSources];
        report.Recording.AddFrame(0, 16, 0.5, row, 0, 0, 0, 0);
        report.Recording.AddFrame(0.016, 16, 0.5, row, 0, 0, 0, 0);
        JsonSerializer.Serialize(report.ToJson());
        JsonSerializer.Serialize(report.Summary());
    }

    // ---- Core counters ------------------------------------------------------------------------------

    private sealed class NullSink : IPerfSink
    {
        public void FrameHandler(Delegate handler, long ticks) { }
        public void CoreTick(long ticks, long startTimestamp) { }
        public void SystemHook(string pluginId, string systemName, bool before, long ticks) { }
        public void EventHandler(string pluginId, Type eventType, long ticks) { }
    }

    [Fact]
    public void Patch_timer_and_seams_are_inert_without_a_capture_and_count_with_one()
    {
        PerfProbe.Install(null);
        Assert.Equal(0L, PerfPatchTimer.Start());
        PerfSeams.Stamp(PerfSeams.LateFrame);
        var first = new long[PerfSeams.Count];
        var last = new long[PerfSeams.Count];
        var count = new int[PerfSeams.Count];
        PerfSeams.Drain(first, last, count);
        Assert.Equal(0, count[PerfSeams.LateFrame]);

        try
        {
            PerfProbe.Install(new NullSink());
            long start = PerfPatchTimer.Start();
            Assert.NotEqual(0L, start);
            PerfPatchTimer.End(PerfPatchTimer.SystemPrefixManaged, start);
            PerfPatchTimer.End(PerfPatchTimer.SystemPrefixManaged, PerfPatchTimer.Start());
            PerfSeams.Stamp(PerfSeams.LateFrame);
            PerfSeams.Stamp(PerfSeams.LateFrame);

            var ticks = new long[PerfPatchTimer.Count];
            var calls = new int[PerfPatchTimer.Count];
            PerfPatchTimer.Drain(ticks, calls);
            Assert.Equal(2, calls[PerfPatchTimer.SystemPrefixManaged]);
            PerfPatchTimer.Drain(ticks, calls);
            Assert.Equal(0, calls[PerfPatchTimer.SystemPrefixManaged]);

            PerfSeams.Drain(first, last, count);
            Assert.Equal(2, count[PerfSeams.LateFrame]);
            Assert.True(last[PerfSeams.LateFrame] >= first[PerfSeams.LateFrame]);
            Assert.NotEqual(0L, first[PerfSeams.LateFrame]);
        }
        finally
        {
            PerfProbe.Install(null);
        }
        Assert.False(PerfPatchTimer.On);
        Assert.False(PerfSeams.On);
    }

    [Fact]
    public void The_default_begin_callback_lets_an_older_sink_load()
    {
        IPerfSink sink = new NullSink();
        sink.BeginCallback();   // default interface method: no-op, no throw
    }

    [Fact]
    public void Report_names_every_patch_and_seam()
    {
        Assert.Equal(PerfPatchTimer.Count, PerfPatchTimer.Names.Length);
        Assert.Equal(PerfSeams.Count, PerfSeams.Names.Length);
        Assert.Equal(PerfSeams.Names.Length, PerfSeams.Names.Distinct().Count());
    }
}

/// <summary>Tests that install a sink into the process-wide <c>PerfProbe</c> slot run one at a time.</summary>
[CollectionDefinition(Name)]
public sealed class PerfProbeCollection
{
    public const string Name = "PerfProbe static slot";
}
