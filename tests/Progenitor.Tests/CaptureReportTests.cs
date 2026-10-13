using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The game-free half of the client <c>capture</c> op (<c>src/Progenitor.Dev/Ops/CaptureReport.cs</c>):
/// argument policy, the every-10th-frame sampling throttle, the frame-budget skip accounting, the
/// "did this endpoint move" verdict, and the summary the bridge returns beside the report path.
/// <c>CaptureOps</c> itself needs a running client and is exercised live, not here.
/// </summary>
public class CaptureReportTests
{
    // ---- arguments ---------------------------------------------------------------------------

    [Theory]
    [InlineData(10, 10)]
    [InlineData(0, CaptureArgs.MinSeconds)]
    [InlineData(-5, CaptureArgs.MinSeconds)]
    [InlineData(10_000, CaptureArgs.MaxSeconds)]
    public void ClampSeconds_holds_the_window_inside_the_allowed_range(double input, double expected)
    {
        Assert.Equal(expected, CaptureArgs.ClampSeconds(input));
    }

    [Fact]
    public void ClampSeconds_falls_back_on_NaN()
    {
        Assert.Equal(CaptureArgs.DefaultSeconds, CaptureArgs.ClampSeconds(double.NaN));
    }

    [Theory]
    [InlineData(2000, 2000)]
    [InlineData(0, CaptureArgs.MinMaxEntities)]
    [InlineData(int.MaxValue, CaptureArgs.HardMaxEntities)]
    public void ClampMaxEntities_clamps_to_the_hard_cap(int input, int expected)
    {
        Assert.Equal(expected, CaptureArgs.ClampMaxEntities(input));
    }

    [Fact]
    public void ParseFilter_splits_on_pipe_and_lowercases()
    {
        Assert.Equal(new[] { "beam", "sequence", "line" }, CaptureArgs.ParseFilter("Beam|Sequence| LINE "));
    }

    [Fact]
    public void ParseFilter_treats_empty_as_no_filter()
    {
        Assert.Empty(CaptureArgs.ParseFilter(null));
        Assert.Empty(CaptureArgs.ParseFilter("   "));
        Assert.Empty(CaptureArgs.ParseFilter("||"));
    }

    [Fact]
    public void Matches_is_case_insensitive_substring_or()
    {
        var filter = CaptureArgs.ParseFilter(CaptureArgs.DefaultFilter);
        Assert.True(CaptureArgs.Matches(filter, "Beam_Cardinal_SummonOrb(Clone)"));
        Assert.True(CaptureArgs.Matches(filter, "HybridLineRendererComponent"));
        Assert.False(CaptureArgs.Matches(filter, "Ambient_Fog_02"));
    }

    [Fact]
    public void An_empty_filter_matches_everything_and_a_null_name_matches_nothing()
    {
        Assert.True(CaptureArgs.Matches(Array.Empty<string>(), "anything"));
        Assert.True(CaptureArgs.Matches(Array.Empty<string>(), null));
        Assert.False(CaptureArgs.Matches(CaptureArgs.ParseFilter("beam"), null));
    }

    [Fact]
    public void MatchesAny_passes_when_one_component_name_matches()
    {
        var filter = CaptureArgs.ParseFilter("beam|line");
        Assert.True(CaptureArgs.MatchesAny(filter, new List<string> { "Transform", "LineRenderer" }));
        Assert.False(CaptureArgs.MatchesAny(filter, new List<string> { "Transform", "MeshRenderer" }));
    }

    [Theory]
    [InlineData("{\"action\":\"start\"}", "start")]
    [InlineData("{\"action\":\"STOP\"}", "stop")]
    [InlineData("{\"action\":\"status\"}", "status")]
    [InlineData("{}", "status")]
    [InlineData("{\"action\":\"\"}", "status")]
    public void ParseAction_accepts_the_three_actions_and_defaults_to_status(string json, string expected)
    {
        Assert.Equal(expected, CaptureArgs.ParseAction(Json(json)));
    }

    [Fact]
    public void ParseAction_refuses_an_unknown_action()
    {
        var e = Assert.Throws<ArgumentException>(() => CaptureArgs.ParseAction(Json("{\"action\":\"spawn\"}")));
        Assert.Contains("start, stop, or status", e.Message);
    }

    // ---- sampling throttle -------------------------------------------------------------------

    [Fact]
    public void ShouldSample_fires_on_every_tenth_frame_starting_at_zero()
    {
        Assert.True(CaptureSampling.ShouldSample(0));
        Assert.False(CaptureSampling.ShouldSample(1));
        Assert.False(CaptureSampling.ShouldSample(9));
        Assert.True(CaptureSampling.ShouldSample(10));
        Assert.True(CaptureSampling.ShouldSample(120));
        Assert.False(CaptureSampling.ShouldSample(-1));
    }

    /// <summary>
    /// R28 finding 29.2: this used to assert <c>X-1 &lt; X</c> and <c>!(X &lt; X)</c> against the
    /// constant it was testing, which cannot fail for any value of the constant. The boundary is
    /// pinned to the literal instead, so changing the cap is a deliberate edit to this test.
    /// R29 finding 22.2: the <c>Assert.Equal(50, MaxSamplesPerObject)</c> line that used to open it
    /// is gone — the four calls below already fail if the cap moves, and they fail because the
    /// <i>behavior</i> moved.
    /// </summary>
    [Fact]
    public void CanAddSample_stops_at_fifty_per_object()
    {
        Assert.True(CaptureSampling.CanAddSample(0));
        Assert.True(CaptureSampling.CanAddSample(49));
        Assert.False(CaptureSampling.CanAddSample(50));
        Assert.False(CaptureSampling.CanAddSample(51));
    }

    /// <summary>
    /// Every capture constant this suite reasons about, pinned <b>through the behavior it
    /// governs</b>, with the literal on the expected side.
    ///
    /// <para>R29 finding 22.2: the previous version was a block of
    /// <c>Assert.Equal(&lt;literal&gt;, &lt;the same constant&gt;)</c>. That does fail when someone
    /// edits the constant, but it fails for the wrong reason and it says nothing about what the
    /// constant does — a constant that stops being consulted at all keeps passing. Each assertion
    /// below drives the code the value is for, so it fails when the value changes <i>and</i> when
    /// the value stops being applied.</para>
    /// </summary>
    [Fact]
    public void The_capture_constants_are_pinned_by_the_behaviour_they_govern()
    {
        // seconds: default when unparseable, floor and ceiling from the clamp
        Assert.Equal(10d, CaptureArgs.ClampSeconds(double.NaN));
        Assert.Equal(1d, CaptureArgs.ClampSeconds(0));
        Assert.Equal(120d, CaptureArgs.ClampSeconds(100_000));

        // entity cap: floor and ceiling from the clamp, default from the arg parser
        Assert.Equal(16, CaptureArgs.ClampMaxEntities(1));
        Assert.Equal(20_000, CaptureArgs.ClampMaxEntities(int.MaxValue));
        Assert.Equal(2000, CaptureArgs.DefaultMaxEntities);   // no clamp path: it is the fallback itself

        // tracked objects: the 513th is dropped and counted
        var tracker = new CaptureReport().Tracker;
        for (int i = 1; i <= 512; i++) Assert.True(tracker.TryTrack(State(i, "o" + i)));
        Assert.False(tracker.TryTrack(State(513, "o513")));
        Assert.Equal(1, tracker.Dropped);

        // row caps: each one refuses at its own ceiling and marks the report truncated
        Assert.Equal(500, FillUntilRefused(r => r.TryAddAppeared(new CaptureEntityRow())));
        Assert.Equal(500, FillUntilRefused(r => r.TryAddDisappeared(new CaptureEntityRow())));
        Assert.Equal(200, FillUntilRefused(r => r.TryAddObject(new CaptureObjectRow())));
        Assert.Equal(400, FillUntilRefused(r => r.TryAddChange(new CaptureChangeRow())));

        // tracked entities: the endpoint table holds eight fields per entity
        var endpoints = new CaptureReport();
        int tracks = 0;
        while (endpoints.TrackFor("e" + tracks, "c", "f") != null) tracks++;
        Assert.Equal(32 * 8, tracks);

        // sweep cadence: every tenth frame, never wider than 120 frames
        Assert.True(CaptureSampling.ShouldSample(10));
        Assert.False(CaptureSampling.ShouldSample(9));
        Assert.Equal(120, CaptureSampling.IntervalFor(scanMs: 1_000_000, budgetMs: 1));

        // tier cadence: never wider than 30 frames
        Assert.Equal(30, CaptureSampling.TierIntervalFor(1_000_000, 1, CaptureSampling.MaxTierInterval));

        // sample registry: the 41st distinct object on one pass is refused and counted
        var registry = new CaptureSampleRegistry();
        for (int i = 1; i <= 40; i++) Assert.True(registry.TryRegister(i, "o" + i, frameIndex: 7));
        Assert.False(registry.TryRegister(41, "o41", frameIndex: 7));
        Assert.Equal(40, registry.Count);

        // budget: 2 ms default, seeded at half of it
        var budget = new CaptureBudget();
        Assert.Equal(2.0, budget.BudgetMs);
        Assert.Equal(1.0, budget.RunningAverageMs);       // SeedFractionOfBudget = 0.5
        Assert.Equal(0.25, CaptureBudget.MaxSweepSkipRatio);
        Assert.False(new CaptureBudget().SweepSkipRatioExceeded);

        // walk budget: 256 direct children, 512 nodes overall
        var walk = new CaptureWalkBudget();
        for (int i = 0; i < 256; i++) Assert.True(walk.TryVisit(depth: 0));
        Assert.False(walk.TryVisit(depth: 0));
        Assert.True(walk.HitRootCap);
        walk.Reset();
        for (int i = 0; i < 512; i++) Assert.True(walk.TryVisit(depth: 1));
        Assert.False(walk.TryVisit(depth: 1));
        Assert.True(walk.HitNodeCap);
    }

    /// <summary>Adds rows through <paramref name="add"/> until the report refuses; returns how many fit.</summary>
    private static int FillUntilRefused(Func<CaptureReport, bool> add)
    {
        var report = new CaptureReport();
        int added = 0;
        while (add(report)) added++;
        Assert.True(report.Truncated);
        return added;
    }

    [Fact]
    public void TrySample_needs_both_the_frame_and_the_room()
    {
        Assert.True(CaptureSampling.TrySample(20, 3));
        Assert.False(CaptureSampling.TrySample(21, 3));
        Assert.False(CaptureSampling.TrySample(20, CaptureSampling.MaxSamplesPerObject));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(10, 1)]
    [InlineData(11, 2)]
    [InlineData(600, 50)]
    [InlineData(10_000, CaptureSampling.MaxSamplesPerObject)]
    public void ExpectedSamples_matches_the_throttle_and_the_cap(int frames, int expected)
    {
        Assert.Equal(expected, CaptureSampling.ExpectedSamples(frames));
    }

    /// <summary>
    /// A 10 s window at 60 fps is ~600 frames: 60 sample ticks, clipped to the 50-sample cap.
    /// That is the arithmetic that keeps a report bounded no matter how long the window is.
    /// </summary>
    [Fact]
    public void A_ten_second_window_at_sixty_fps_is_capped_by_the_per_object_limit()
    {
        Assert.Equal(CaptureSampling.MaxSamplesPerObject, CaptureSampling.ExpectedSamples(600));
    }

    /// <summary>
    /// R27 finding 22: a <c>lineRenderer</c>-sourced row can only carry a frame the scene sweep ran
    /// on, i.e. a multiple of the interval, so a window narrower than one interval attributed the
    /// build-mode preview beams to their sequence only when the phase happened to line up.
    ///
    /// <para>R28 finding 29.1: the old assertion was <c>SequenceObjectFrameWindow >=
    /// SampleEveryNthFrame</c>, and the window is <i>defined</i> as <c>SampleEveryNthFrame + 4</c> —
    /// it asserted <c>X+4 >= X</c>. This drives the actual arithmetic instead: for an entity
    /// appearing on any frame, the first sweep that can see the clone the sequencer spawns one or
    /// two frames later must fall inside the window, whatever the phase.</para>
    /// </summary>
    [Fact]
    public void The_attribution_window_covers_the_next_sweep_at_every_phase()
    {
        const int interval = CaptureSampling.SampleEveryNthFrame;
        for (int entityFrame = 0; entityFrame < 3 * interval; entityFrame++)
        {
            // The clone appears one to two frames after the sequence entity (research 17.2, 21.1).
            int cloneFrame = entityFrame + 2;
            // The first scene sweep at or after that frame is the only one that can report it.
            int sweepFrame = ((cloneFrame + interval - 1) / interval) * interval;
            Assert.True(
                sweepFrame <= entityFrame + CaptureArgs.SequenceObjectFrameWindow,
                $"entity at {entityFrame}: the sweep at {sweepFrame} falls outside the "
                + $"{CaptureArgs.SequenceObjectFrameWindow}-frame window");
        }
    }

    [Fact]
    public void ShouldSample_honours_a_widened_interval()
    {
        Assert.True(CaptureSampling.ShouldSample(0, 40));
        Assert.False(CaptureSampling.ShouldSample(10, 40));
        Assert.True(CaptureSampling.ShouldSample(40, 40));
        // An interval narrower than the floor is clamped up to it, never down.
        Assert.False(CaptureSampling.ShouldSample(3, 1));
        Assert.True(CaptureSampling.ShouldSample(20, 1));
    }

    [Theory]
    [InlineData(0, 10)]           // nothing measured yet: the default, so the first sweep runs
    [InlineData(0.5, 10)]         // cheap sweep: the floor still applies
    [InlineData(20, 10)]          // 20 ms / 2 ms = every 10th frame, which is already the floor
    [InlineData(40, 20)]          // twice as expensive, so twice as far apart
    [InlineData(85, 43)]
    [InlineData(100_000, CaptureSampling.MaxSampleInterval)]
    public void IntervalFor_amortises_the_sweep_cost_to_the_budget(double scanMs, int expected)
    {
        Assert.Equal(expected, CaptureSampling.IntervalFor(scanMs, CaptureBudget.DefaultBudgetMs));
    }

    [Fact]
    public void IntervalFor_falls_back_on_nonsense_inputs()
    {
        Assert.Equal(CaptureSampling.SampleEveryNthFrame, CaptureSampling.IntervalFor(double.NaN, 2));
        Assert.Equal(CaptureSampling.SampleEveryNthFrame, CaptureSampling.IntervalFor(20, 0));
        Assert.Equal(CaptureSampling.SampleEveryNthFrame, CaptureSampling.IntervalFor(-3, 2));
    }

    // ---- budget ------------------------------------------------------------------------------

    [Fact]
    public void ShouldSkipWalk_triggers_at_the_budget()
    {
        var budget = new CaptureBudget(2.0);
        Assert.False(budget.ShouldSkipWalk(0.5));
        Assert.False(budget.ShouldSkipWalk(1.999));
        Assert.True(budget.ShouldSkipWalk(2.0));
        Assert.True(budget.ShouldSkipWalk(7.5));
    }

    [Fact]
    public void A_nonpositive_budget_falls_back_to_the_default()
    {
        Assert.Equal(CaptureBudget.DefaultBudgetMs, new CaptureBudget(0).BudgetMs);
        Assert.Equal(CaptureBudget.DefaultBudgetMs, new CaptureBudget(-1).BudgetMs);
    }

    [Fact]
    public void Record_counts_frames_skips_and_over_budget_frames()
    {
        var budget = new CaptureBudget(2.0);
        budget.Record(0.4, skippedWalk: false);
        budget.Record(0.6, skippedWalk: false);
        budget.Record(3.5, skippedWalk: true);
        budget.Record(2.0, skippedWalk: true);

        Assert.Equal(4, budget.Frames);
        Assert.Equal(2, budget.SkippedWalks);
        Assert.Equal(2, budget.OverBudgetFrames);
        Assert.Equal(3.5, budget.MaxMs);
        Assert.Equal(6.5, budget.TotalMs, 3);
        Assert.Equal(1.625, budget.AverageMs, 3);
    }

    [Fact]
    public void An_empty_budget_reports_zero_average_rather_than_dividing_by_zero()
    {
        Assert.Equal(0, new CaptureBudget().AverageMs);
    }

    [Fact]
    public void Budget_json_carries_the_skip_count()
    {
        var budget = new CaptureBudget();
        budget.Record(0.2, skippedWalk: false);
        budget.Record(4.0, skippedWalk: true);
        var json = budget.ToJson();
        Assert.Equal(2, json["frames"]);
        Assert.Equal(1, json["skippedGameObjectWalks"]);
        Assert.Equal(1, json["overBudgetFrames"]);
        Assert.True(json.ContainsKey("skippedSceneScans"));
        Assert.True(json.ContainsKey("skippedComparisons"));
        Assert.True(json.ContainsKey("sampleInterval"));
        Assert.True(json.ContainsKey("averageScanMs"));
    }

    /// <summary>
    /// R27 finding 8. The old gate measured the microseconds spent BEFORE the sweep and compared
    /// them to the budget, so an unmeasured 20 ms sweep always passed: the client stuttered at 3 Hz
    /// for the whole window while the report said the budget held and the skip count stayed 0. The
    /// gate now reads the running whole-frame average, which the sweep's own cost feeds.
    /// </summary>
    [Fact]
    public void The_gate_throttles_the_expensive_half_even_when_the_frame_has_barely_started()
    {
        var budget = new CaptureBudget(2.0);

        // Nothing measured yet: the first sweep is allowed, and it is what produces the measurement.
        Assert.False(budget.ShouldSkipScan(0.05));
        Assert.Equal(CaptureSampling.SampleEveryNthFrame, budget.SampleInterval);

        budget.RecordScan(40.0);
        budget.Record(40.4, skippedWalk: false);

        // Cheap so far, and refused anyway — the old check could never reach this verdict.
        Assert.True(budget.RunningAverageMs >= budget.BudgetMs);
        Assert.True(budget.ShouldSkipScan(0.05));
        Assert.Equal(20, budget.SampleInterval);
        Assert.Equal(1, budget.Scans);
        Assert.Equal(40.0, budget.MaxScanMs);
    }

    [Fact]
    public void The_gate_re_opens_once_the_running_average_falls_back_under_budget()
    {
        var budget = new CaptureBudget(2.0);
        budget.RecordScan(40.0);
        budget.Record(40.4, skippedWalk: false);
        Assert.True(budget.ShouldSkipScan(0.05));

        for (int i = 0; i < 200; i++) budget.Record(0.05, skippedWalk: false, skippedScan: true);

        Assert.False(budget.ShouldSkipScan(0.05));
        Assert.Equal(200, budget.SkippedScans);
    }

    [Fact]
    public void A_frame_that_is_expensive_by_itself_is_still_refused_the_sweep()
    {
        var budget = new CaptureBudget(2.0);
        Assert.True(budget.ShouldSkipScan(2.5));
        Assert.True(budget.ShouldSkipComparison(2.5));
    }

    [Fact]
    public void Record_counts_scan_and_comparison_skips_separately_from_walk_skips()
    {
        var budget = new CaptureBudget(2.0);
        budget.Record(0.1, skippedWalk: false);
        budget.Record(0.1, skippedWalk: false, skippedScan: true, skippedComparison: true);
        budget.Record(9.0, skippedWalk: true, skippedScan: true, skippedComparison: true);

        Assert.Equal(1, budget.SkippedWalks);
        Assert.Equal(2, budget.SkippedScans);
        Assert.Equal(2, budget.SkippedComparisons);
    }

    [Fact]
    public void RecordScan_ignores_nonsense_and_keeps_the_worst_case()
    {
        var budget = new CaptureBudget();
        budget.RecordScan(double.NaN);
        budget.RecordScan(-1);
        Assert.Equal(0, budget.Scans);

        budget.RecordScan(12);
        budget.RecordScan(4);
        Assert.Equal(2, budget.Scans);
        Assert.Equal(4, budget.LastScanMs);
        Assert.Equal(12, budget.MaxScanMs);
        // The average follows slowly, so one cheap sweep does not re-open the floodgates.
        Assert.True(budget.AverageScanMs > 11);
    }

    // ---- the tier plan (R28 findings 5, 7, 8, 9) ------------------------------------------------

    /// <summary>
    /// R28 finding 8. The old seed was <c>Frames == 1 ? frameMs : ...</c>, and frame 0 of a capture
    /// runs a full sweep — so the EWMA was seeded with the single most expensive frame the window
    /// would ever have, and at weight 1/32 took 73 to 94 frames to decay back under a 2 ms budget.
    /// The <c>start</c> response tells the operator to trigger the effect now; the first 1.2 to 1.6
    /// seconds, containing the spawn, recorded nothing.
    /// </summary>
    [Fact]
    public void The_running_average_is_seeded_with_a_constant_not_with_the_first_frame()
    {
        var budget = new CaptureBudget(2.0);
        Assert.Equal(1.0, budget.RunningAverageMs, 6);

        budget.RecordScan(40.0);
        budget.Record(40.4, skippedWalk: false);

        // The old code stored 40.4 here and refused every sweep for the next ~80 frames.
        Assert.True(budget.RunningAverageMs < 3.0, $"seeded average leaked the first frame: {budget.RunningAverageMs}");
        Assert.True(budget.RunningAverageMs > budget.BudgetMs, "one 40 ms frame should still trip the gate");
    }

    /// <summary>
    /// R28 finding 5, the headline: the sweep may be throttled, never switched off. A window whose
    /// frames are permanently over budget still sweeps once every
    /// <see cref="CaptureBudget.MinScanEveryFrames"/> frames, and the plan says the run was forced.
    /// </summary>
    [Fact]
    public void An_over_budget_window_still_sweeps_on_the_floor_instead_of_stopping()
    {
        var budget = new CaptureBudget(2.0);
        for (int i = 0; i < 5; i++) { budget.RecordScan(60.0); budget.Record(60.0, skippedWalk: false); }
        Assert.True(budget.ShouldSkipScan(0.0), "the running average should be refusing sweeps by now");

        int scans = 0;
        int forced = 0;
        for (int frame = 0; frame < 400; frame++)
        {
            var plan = budget.Confirm(budget.PlanFrame(frame), elapsedMsSoFar: 5.0);
            if (plan.RunScan) { scans++; if (plan.ForcedScan) forced++; }
            budget.Record(60.0, plan);
        }

        Assert.True(scans >= 3, $"the sweep stopped entirely: {scans} scans in 400 over-budget frames");
        Assert.Equal(scans, forced);
        Assert.True(budget.ForcedScans >= 3);
    }

    /// <summary>
    /// R28 finding 5, the other half: tier 1's cost is charged to tier 1. An expensive entity diff
    /// widens its own interval and leaves the pool walk and the scene sweep alone — before, its
    /// milliseconds went into the <c>elapsedMsSoFar</c> that gated both of them, so a crowded area
    /// switched off the two tiers the op exists for.
    /// </summary>
    [Fact]
    public void An_expensive_entity_diff_throttles_itself_and_not_the_tiers_below_it()
    {
        var budget = new CaptureBudget(2.0);
        budget.RecordEntityDiff(8.0);
        Assert.Equal(4, budget.EntityDiffInterval);

        var due = budget.PlanFrame(0);
        Assert.True(due.RunEntityDiff);

        var offBeat = budget.PlanFrame(1);
        Assert.False(offBeat.RunEntityDiff);
        Assert.True(offBeat.SkippedEntityDiff);
        Assert.True(offBeat.RunWalk);

        // Frame 0 is a sweep frame, and the entity diff being expensive must not touch that.
        Assert.True(due.RunScan);
        Assert.True(budget.Confirm(due, elapsedMsSoFar: 0.1).RunScan);
    }

    [Fact]
    public void The_entity_diff_interval_never_widens_past_its_ceiling()
    {
        var budget = new CaptureBudget(2.0);
        budget.RecordEntityDiff(10_000);
        Assert.Equal(CaptureSampling.MaxTierInterval, budget.EntityDiffInterval);
        Assert.Equal(1, new CaptureBudget(2.0).EntityDiffInterval);
    }

    [Theory]
    [InlineData(0, 1)]          // unmeasured: run every frame, that is what measures it
    [InlineData(0.5, 1)]        // affordable
    [InlineData(8, 4)]          // 8 ms / 2 ms
    [InlineData(10_000, 30)]    // capped
    public void TierIntervalFor_amortises_a_per_frame_tier(double costMs, int expected)
    {
        Assert.Equal(expected, CaptureSampling.TierIntervalFor(costMs, 2.0, CaptureSampling.MaxTierInterval));
    }

    /// <summary>
    /// R28 finding 7. Before this, a window that skipped 95 % of its sweeps led its summary with
    /// <c>truncated: false</c> and buried <c>skippedSceneScans</c> inside <c>counts</c>, so a
    /// capture that recorded nothing was indistinguishable from one that recorded everything.
    /// </summary>
    [Fact]
    public void A_window_that_skipped_most_of_its_sweeps_reports_truncated_with_the_ratio()
    {
        var report = new CaptureReport();
        Assert.False(report.Truncated);

        for (int i = 0; i < 19; i++) report.Budget.Record(0.5, skippedWalk: false, skippedScan: true);
        report.Budget.RecordScan(30.0);                  // one sweep actually ran
        report.Budget.Record(30.5, skippedWalk: false);

        Assert.Equal(19, report.Budget.SkippedScans);
        Assert.Equal(1, report.Budget.Scans);
        Assert.True(report.Budget.SweepSkipRatioExceeded);
        Assert.True(report.Truncated);

        var summary = report.Summary();
        Assert.True(Assert.IsType<bool>(summary["truncated"]));
        Assert.Equal(19, summary["sweepsSkipped"]);
        Assert.True(Convert.ToDouble(summary["sweepSkipRatio"]) > CaptureBudget.MaxSweepSkipRatio);
    }

    [Fact]
    public void A_window_that_kept_up_is_not_flagged_by_the_skip_ratio()
    {
        var report = new CaptureReport();
        for (int i = 0; i < 40; i++) { report.Budget.RecordScan(1.0); report.Budget.Record(1.2, skippedWalk: false); }
        report.Budget.Record(0.5, skippedWalk: false, skippedScan: true);
        Assert.False(report.Budget.SweepSkipRatioExceeded);
        Assert.False(report.Truncated);
    }

    /// <summary>
    /// R28 finding 9, the object side. The old sweeps clipped raw <c>FindObjectsOfType</c> order,
    /// whose ordering is unspecified, so the kept set churned and objects were repeatedly swept and
    /// re-tracked with a fresh baseline. Sorting by instance id makes the kept set a pure function
    /// of the available set — and unlike the old test for the entity side, this calls the
    /// production selection rather than sorting in the test.
    /// </summary>
    [Fact]
    public void The_scene_sweep_keeps_the_same_objects_when_new_ones_appear()
    {
        var before = new[] { 900, 120, 400, 70 };
        var after = new[] { 9000, 900, 120, 4001, 400, 70, 777 };

        Assert.Equal(new[] { 70, 120, 400 }, KeptIds(before, 3));
        Assert.Equal(new[] { 70, 120, 400 }, KeptIds(after, 3));

        // And a re-ordered arrival of the same objects keeps the same subset.
        Assert.Equal(new[] { 70, 120, 400 }, KeptIds(new[] { 400, 70, 900, 120 }, 3));
    }

    [Fact]
    public void The_scene_sweep_selection_reports_what_it_dropped()
    {
        Assert.Equal(0, CaptureScanSelection.Overflow(400, 400));
        Assert.Equal(200, CaptureScanSelection.Overflow(600, 400));
        Assert.Equal(0, CaptureScanSelection.SortAndClip(new int[0], new int[0], 0, 400));
    }

    /// <summary>
    /// R28 finding 9, the walk. One shared depth-first budget meant which direct children were
    /// visited depended on how many grandchildren the earlier siblings had — so a parked clone slid
    /// past the cap and <c>DiffPoolChildren</c> called it "taken from the pool". The root children
    /// now have their own budget, and a pass that ran out of either is marked incomplete so it is
    /// not diffed at all.
    /// </summary>
    [Fact]
    public void The_pool_walk_gives_the_root_children_their_own_budget_and_says_when_it_ran_out()
    {
        var walk = new CaptureWalkBudget();
        Assert.True(walk.Complete);

        // Deep siblings must not consume the root children's budget.
        for (int i = 0; i < CaptureWalkBudget.MaxRootNodes; i++) Assert.True(walk.TryVisit(0));
        Assert.True(walk.Complete);
        Assert.False(walk.TryVisit(0));
        Assert.False(walk.Complete);
        Assert.True(walk.HitRootCap);

        var other = new CaptureWalkBudget();
        for (int i = 0; i < CaptureWalkBudget.MaxNodes; i++) other.TryVisit(i == 0 ? 0 : 1);
        Assert.False(other.TryVisit(1));
        Assert.False(other.Complete);
        Assert.False(other.CanDescend(0));
    }

    [Fact]
    public void An_incomplete_walk_is_counted_and_flags_the_report()
    {
        var report = new CaptureReport();
        report.NoteIncompleteWalk();
        Assert.Equal(1, report.IncompleteWalks);
        Assert.True(report.Truncated);

        var deadline = new CaptureReport();
        deadline.NoteScanDeadline();
        Assert.Equal(1, deadline.ScanDeadlineStops);
        Assert.True(deadline.Truncated);
        Assert.Equal(20.0, deadline.Budget.ScanDeadlineMs);
    }

    // ---- entity selection ----------------------------------------------------------------------

    [Theory]
    [InlineData(0, 2000, 0, 0)]
    [InlineData(120, 2000, 120, 0)]
    [InlineData(2000, 2000, 2000, 0)]
    [InlineData(4500, 2000, 2000, 2500)]
    public void Keep_and_Overflow_split_the_match_set_at_the_cap(int available, int max, int keep, int overflow)
    {
        Assert.Equal(keep, CaptureEntitySelection.Keep(available, max));
        Assert.Equal(overflow, CaptureEntitySelection.Overflow(available, max));
    }

    /// <summary>
    /// R27 finding 9: a frame that could not see the whole match set cannot tell "gone" from "did
    /// not fit", and emitting removals from it burned both 500-row caps within about three frames.
    /// </summary>
    [Fact]
    public void A_truncated_frame_is_not_allowed_to_report_disappearances()
    {
        Assert.False(CaptureEntitySelection.ShouldSkipDisappearance(1999, 2000));
        Assert.False(CaptureEntitySelection.ShouldSkipDisappearance(2000, 2000));
        Assert.True(CaptureEntitySelection.ShouldSkipDisappearance(2001, 2000));
    }

    [Fact]
    public void The_sort_key_orders_by_entity_index_then_version_and_round_trips()
    {
        var a = CaptureEntitySelection.Key(12, 3);
        var b = CaptureEntitySelection.Key(12, 4);
        var c = CaptureEntitySelection.Key(13, 1);
        Assert.True(a < b);
        Assert.True(b < c);
        Assert.Equal(12, CaptureEntitySelection.IndexOfKey(a));
        Assert.Equal(3, CaptureEntitySelection.VersionOfKey(a));
    }

    /// <summary>
    /// The property that makes the truncation usable: the kept subset does not move when the world
    /// grows at higher entity indices, so the diff stops manufacturing appear/disappear churn.
    /// </summary>
    [Fact]
    public void Sorting_by_key_keeps_the_same_subset_when_higher_index_entities_arrive()
    {
        var before = Keys(new[] { 90, 12, 40, 7 });
        var after = Keys(new[] { 900, 90, 12, 401, 40, 7, 77 });
        Array.Sort(before);
        Array.Sort(after);

        const int max = 3;
        var keptBefore = new List<int>();
        for (int i = 0; i < CaptureEntitySelection.Keep(before.Length, max); i++)
            keptBefore.Add(CaptureEntitySelection.IndexOfKey(before[i]));
        var keptAfter = new List<int>();
        for (int i = 0; i < CaptureEntitySelection.Keep(after.Length, max); i++)
            keptAfter.Add(CaptureEntitySelection.IndexOfKey(after[i]));

        Assert.Equal(new List<int> { 7, 12, 40 }, keptBefore);
        Assert.Equal(keptBefore, keptAfter);
    }

    [Fact]
    public void The_report_records_the_overflow_size_and_the_number_of_truncated_frames()
    {
        var report = new CaptureReport();
        report.NoteEntityOverflow(0);
        Assert.False(report.Truncated);

        report.NoteEntityOverflow(120);
        report.NoteEntityOverflow(4);
        Assert.True(report.Truncated);
        Assert.Equal(120, report.EntityOverflow);
        Assert.Equal(2, report.TruncatedFrames);

        report.NoteScanOverflow(70);
        Assert.Equal(70, report.ScanOverflow);
    }

    // ---- series ------------------------------------------------------------------------------

    [Fact]
    public void A_series_stops_at_fifty_samples_and_flags_itself_truncated()
    {
        var series = new CaptureSampleSeries { InstanceId = 1, Name = "Beam(Clone)" };
        for (int i = 0; i < CaptureSampling.MaxSamplesPerObject; i++)
            Assert.True(series.TryAdd(i * 10, i, One(0, 0, 0)));

        Assert.False(series.Truncated);
        Assert.False(series.TryAdd(9999, 99, One(0, 0, 0)));
        Assert.True(series.Truncated);
        Assert.Equal(CaptureSampling.MaxSamplesPerObject, series.Samples.Count);
    }

    [Fact]
    public void Moved_is_false_for_a_single_sample_or_a_still_line()
    {
        var series = new CaptureSampleSeries();
        Assert.False(series.Moved);
        series.TryAdd(0, 0, Two(0, 1, 0, 5, 1, 0));
        Assert.False(series.Moved);
        series.TryAdd(10, 0.16, Two(0, 1, 0, 5, 1, 0));
        Assert.False(series.Moved);
    }

    [Fact]
    public void Float_noise_below_the_epsilon_is_not_movement()
    {
        var series = new CaptureSampleSeries();
        series.TryAdd(0, 0, Two(0, 1, 0, 5, 1, 0));
        series.TryAdd(10, 0.16, Two(0.001f, 1.002f, 0, 5.003f, 1, 0));
        Assert.False(series.Moved);
    }

    [Fact]
    public void A_moving_far_end_is_movement()
    {
        var series = new CaptureSampleSeries();
        series.TryAdd(0, 0, Two(0, 1, 0, 5, 1, 0));
        series.TryAdd(10, 0.16, Two(0, 1, 0, 6.4f, 1, 0));
        Assert.True(series.Moved);
    }

    [Fact]
    public void A_changed_position_count_is_movement()
    {
        var series = new CaptureSampleSeries();
        series.TryAdd(0, 0, Two(0, 1, 0, 5, 1, 0));
        series.TryAdd(10, 0.16, One(0, 1, 0));
        Assert.True(series.Moved);
    }

    // ---- the "changed" detector ----------------------------------------------------------------

    /// <summary>
    /// The case the detector exists for (evidence 135 to 137): four live captures recorded ZERO
    /// GameObjects while beams were on screen, because the sequencer re-activates pooled clones
    /// instead of creating new ones. A parked, inactive clone waking up must produce a row — and
    /// ask for its full description, since nothing else in the report describes it.
    /// </summary>
    [Fact]
    public void A_pooled_clone_that_was_inactive_at_baseline_and_wakes_up_is_recorded_with_a_description_request()
    {
        var report = new CaptureReport();
        var scratch = State(900, "Beam_Cardinal(Clone)", active: false, parent: 10);
        Assert.True(report.Tracker.TryTrack(scratch, 0));

        scratch.ActiveSelf = true;
        scratch.ActiveInHierarchy = true;
        var row = report.Tracker.Observe(scratch, 10, 0.16, report);

        Assert.NotNull(row);
        Assert.Equal(CaptureChangeKind.Activated, row!.Kind);
        Assert.Equal(900, row.InstanceId);
        Assert.Equal("Beam_Cardinal(Clone)", row.Name);
        Assert.Equal(10, row.Frame);
        Assert.Contains("activeInHierarchy=false", row.Before);
        Assert.Contains("activeInHierarchy=true", row.After);
        Assert.Single(report.Changes);
        Assert.Equal(1, report.Tracker.Transitions);
    }

    [Fact]
    public void An_object_that_was_already_active_at_baseline_does_not_ask_for_a_description_when_it_cycles()
    {
        var report = new CaptureReport();
        var scratch = State(900, "Beam", active: true);
        report.Tracker.TryTrack(scratch, 0);

        scratch.ActiveSelf = false;
        scratch.ActiveInHierarchy = false;
        Assert.Null(report.Tracker.Observe(scratch, 10, 0.1, report));
        Assert.Equal(CaptureChangeKind.Deactivated, report.Changes[0].Kind);

        scratch.ActiveSelf = true;
        scratch.ActiveInHierarchy = true;
        Assert.Null(report.Tracker.Observe(scratch, 20, 0.2, report));
        Assert.Equal(CaptureChangeKind.Activated, report.Changes[1].Kind);
        Assert.Equal(2, report.Changes.Count);
    }

    [Fact]
    public void A_reparent_is_recorded_and_asks_for_a_description_because_the_new_parent_name_is_unread()
    {
        var report = new CaptureReport();
        var scratch = State(900, "Beam", parent: 10);
        scratch.ParentName = "ParticlePool";
        report.Tracker.TryTrack(scratch, 0);

        scratch.ParentInstanceId = 55;
        scratch.ParentName = null;
        var row = report.Tracker.Observe(scratch, 10, 0.16, report);

        Assert.NotNull(row);
        Assert.Equal(CaptureChangeKind.Reparented, row!.Kind);
        Assert.Contains("ParticlePool", row.Before);
        Assert.Contains("55", row.After);
    }

    [Fact]
    public void Line_positions_that_move_produce_a_row_and_a_sample()
    {
        var report = new CaptureReport();
        var scratch = Line(901, "Beam_Line", (0, 1, 0), (5, 1, 0));
        report.Tracker.TryTrack(scratch, 0);

        scratch.LinePositions[1] = new CaptureVec3(7.5f, 1, 0);
        report.Tracker.Observe(scratch, 10, 0.16, report);

        Assert.Single(report.Changes);
        Assert.Equal(CaptureChangeKind.LineMoved, report.Changes[0].Kind);
        Assert.Contains("(5,1,0)", report.Changes[0].Before);
        Assert.Contains("(7.5,1,0)", report.Changes[0].After);
        Assert.Single(report.Series);
        Assert.Single(report.Series[0].Samples);
        Assert.Equal(10, report.Series[0].Samples[0].Frame);
    }

    [Fact]
    public void Float_noise_on_a_line_is_not_a_transition()
    {
        var report = new CaptureReport();
        var scratch = Line(901, "Beam_Line", (0, 1, 0), (5, 1, 0));
        report.Tracker.TryTrack(scratch, 0);

        scratch.LinePositions[0] = new CaptureVec3(0.001f, 1.002f, 0);
        report.Tracker.Observe(scratch, 10, 0.16, report);

        Assert.Empty(report.Changes);
        Assert.Empty(report.Series);
    }

    [Fact]
    public void A_hybrid_line_is_compared_too()
    {
        var report = new CaptureReport();
        var scratch = State(902, "Hybrid");
        scratch.HasHybrid = true;
        scratch.HybridPositions.Add(new CaptureVec3(0, 0, 0));
        report.Tracker.TryTrack(scratch, 0);

        scratch.HybridPositions[0] = new CaptureVec3(0, 0, 3);
        report.Tracker.Observe(scratch, 10, 0.16, report);

        Assert.Equal(CaptureChangeKind.LineMoved, report.Changes[0].Kind);
        Assert.Contains("hybrid[1]", report.Changes[0].After);
    }

    [Fact]
    public void A_particle_system_that_starts_and_stops_is_recorded()
    {
        var report = new CaptureReport();
        var scratch = State(903, "Sparks");
        scratch.HasParticle = true;
        report.Tracker.TryTrack(scratch, 0);

        scratch.ParticleIsPlaying = true;
        scratch.ParticleCount = 64;
        report.Tracker.Observe(scratch, 10, 0.16, report);

        scratch.ParticleIsPlaying = false;
        scratch.ParticleCount = 0;
        report.Tracker.Observe(scratch, 20, 0.33, report);

        Assert.Equal(CaptureChangeKind.ParticleStarted, report.Changes[0].Kind);
        Assert.Contains("particleCount=64", report.Changes[0].After);
        Assert.Equal(CaptureChangeKind.ParticleStopped, report.Changes[1].Kind);
    }

    [Fact]
    public void The_same_object_is_only_compared_once_per_pass()
    {
        // The live side reaches a pooled line twice on a sweep frame: once through the pool walk
        // and once through the scene sweep.
        var report = new CaptureReport();
        var scratch = State(900, "Beam", active: false);
        report.Tracker.TryTrack(scratch, 0);

        scratch.ActiveSelf = true;
        scratch.ActiveInHierarchy = true;
        Assert.True(report.Tracker.NeedsObservation(900, 10));
        report.Tracker.Observe(scratch, 10, 0.16, report);
        Assert.False(report.Tracker.NeedsObservation(900, 10));
        Assert.True(report.Tracker.WasComparedThisPass(900, 10));

        report.Tracker.Observe(scratch, 10, 0.16, report);
        Assert.Single(report.Changes);
        Assert.Equal(1, report.Tracker.Comparisons);
    }

    [Fact]
    public void Observing_an_unknown_object_adopts_it_without_inventing_a_transition()
    {
        var report = new CaptureReport();
        Assert.Null(report.Tracker.Observe(State(900, "Beam"), 10, 0.16, report));
        Assert.Empty(report.Changes);
        Assert.Equal(1, report.Tracker.Tracked);
        Assert.False(report.Tracker.NeedsObservation(901, 10));
    }

    /// <summary>
    /// R27 finding 22: Unity recycles instance ids. Keying on the bare int made a destroyed clone's
    /// series absorb an unrelated new object's positions, so <c>moved</c> was always true.
    /// </summary>
    [Fact]
    public void A_recycled_instance_id_retires_the_old_entry_and_its_series()
    {
        var report = new CaptureReport();
        report.Tracker.TryTrack(State(900, "Old_Beam(Clone)"), 0);
        var oldSeries = report.SeriesFor(900, "Old_Beam(Clone)")!;
        oldSeries.TryAdd(0, 0, One(0, 0, 0));

        Assert.Null(report.Tracker.Observe(State(900, "New_Beam(Clone)"), 10, 0.16, report));

        Assert.True(oldSeries.Retired);
        Assert.False(oldSeries.TryAdd(10, 0.16, One(9, 9, 9)));
        Assert.Single(oldSeries.Samples);
        Assert.Equal(1, report.Tracker.Retired);
        Assert.Equal(1, report.Tracker.Tracked);
        Assert.Empty(report.Changes);

        var fresh = report.SeriesFor(900, "New_Beam(Clone)")!;
        Assert.NotSame(oldSeries, fresh);
    }

    /// <summary>
    /// R28 finding 6, the case that actually happens. Pooled clones all carry the prefab name, so
    /// "id + name" was "id alone" for exactly the objects the detector exists for. Unity hands
    /// instance 900 from a destroyed <c>Beam_Cardinal(Clone)</c> to a new one parked in the pool,
    /// and the old code compared the new object against the dead one's snapshot: a spurious
    /// <c>deactivated</c> row for an object never deactivated, and a <c>reparented</c> row whose
    /// <c>before</c> named a destroyed object's parent.
    ///
    /// <para>R29 finding 20 narrowed the signature: the deactivate-and-reparent only means id reuse
    /// when the tracker <b>stopped seeing the id</b> in between, which is what the passes below
    /// establish. The same shape without a gap is an ordinary return to pool — the test under this
    /// one.</para>
    /// </summary>
    [Fact]
    public void A_recycled_id_under_the_same_pooled_name_is_retired_instead_of_compared()
    {
        var report = new CaptureReport();
        report.Tracker.BeginPass();
        var live = State(900, "Beam_Cardinal(Clone)", active: true, parent: 55);
        report.Tracker.TryTrack(live, 0);
        var series = report.SeriesFor(900, "Beam_Cardinal(Clone)")!;
        series.TryAdd(0, 0, One(0, 0, 0));

        // The object is destroyed: passes go by with nothing on this id.
        for (int i = 0; i <= CaptureObjectIdentity.RecycleUnseenPasses; i++) report.Tracker.BeginPass();

        // Same name, now inactive and parked under the pool parent: a different object.
        var recycled = State(900, "Beam_Cardinal(Clone)", active: false, parent: 10);
        Assert.Null(report.Tracker.Observe(recycled, 10, 0.16, report));

        Assert.Empty(report.Changes);                    // no phantom deactivated / reparented rows
        Assert.Equal(1, report.Tracker.Retired);
        Assert.Equal(1, report.Tracker.Tracked);
        Assert.True(series.Retired);
    }

    /// <summary>
    /// R29 finding 20. Deactivate-and-reparent in one frame <i>is</i> the standard Unity return to
    /// pool (<c>SetActive(false)</c> + <c>SetParent(poolRoot)</c>), so treating it as "cannot be the
    /// same object" made one pooled clone mint two identities per round trip — retired on the way in,
    /// re-keyed on the way out — and burn a sample-registry slot each time, so the rotation counter
    /// reported churn that was an artifact of the identity rule rather than of the scene.
    ///
    /// <para>On the pre-fix code this fails at the first assertion: <c>Retired</c> is 1, because the
    /// return to pool retired the entry and re-tracked it.</para>
    /// </summary>
    [Fact]
    public void An_ordinary_pool_round_trip_mints_one_new_identity_not_two()
    {
        var report = new CaptureReport();
        var tracker = report.Tracker;

        tracker.BeginPass();
        tracker.TryTrack(State(900, "Beam_Cardinal(Clone)", active: true, parent: 55), 0);
        var first = tracker.IdentityOf(900);

        // Returned to the pool, observed on the very next pass: the same object going quiet.
        tracker.BeginPass();
        tracker.Observe(State(900, "Beam_Cardinal(Clone)", active: false, parent: 10), 1, 0.016, report);
        Assert.Equal(0, tracker.Retired);
        Assert.Equal(0, tracker.Rekeyed);
        Assert.Equal(first, tracker.IdentityOf(900));
        // The transitions are recorded against the surviving identity instead of being lost.
        Assert.Contains(report.Changes, r => r.Kind == CaptureChangeKind.Deactivated);
        Assert.Contains(report.Changes, r => r.Kind == CaptureChangeKind.Reparented);

        // Handed out again under a new parent: this, and only this, starts the new identity.
        tracker.BeginPass();
        var row = tracker.Observe(State(900, "Beam_Cardinal(Clone)", active: true, parent: 77), 2, 0.033, report);
        Assert.Equal(0, tracker.Retired);
        Assert.Equal(1, tracker.Rekeyed);
        Assert.NotEqual(first, tracker.IdentityOf(900));
        Assert.NotNull(row);
        Assert.Equal(CaptureChangeKind.Activated, row!.Kind);

        // Three more cycles: still exactly one new identity each, never two.
        for (int cycle = 0; cycle < 3; cycle++)
        {
            tracker.BeginPass();
            tracker.Observe(State(900, "Beam_Cardinal(Clone)", active: false, parent: 10), 3 + cycle * 2, 0.05, report);
            tracker.BeginPass();
            tracker.Observe(State(900, "Beam_Cardinal(Clone)", active: true, parent: 78 + cycle), 4 + cycle * 2, 0.06, report);
        }

        Assert.Equal(0, tracker.Retired);
        Assert.Equal(4, tracker.Rekeyed);
    }

    /// <summary>
    /// R28 finding 6, consequence (3): the new clone inherited the dead object's
    /// <c>Activations</c> count, so <c>added &amp;&amp; !BaselineActive &amp;&amp; Activations == 1</c>
    /// was false and the one full-description row the whole op exists to produce was suppressed.
    /// A parked object waking up under a parent that is not its identity's parent is a new life:
    /// re-key, and report the activation as a first one.
    /// </summary>
    [Fact]
    public void A_parked_clone_woken_under_a_new_parent_is_rekeyed_and_asks_for_its_description()
    {
        var report = new CaptureReport();
        var clone = State(900, "Beam_Cardinal(Clone)", active: false, parent: 10);
        report.Tracker.TryTrack(clone, 0);

        // First life: woken in place, described, then parked again.
        clone.ActiveSelf = clone.ActiveInHierarchy = true;
        Assert.NotNull(report.Tracker.Observe(clone, 10, 0.16, report));
        clone.ActiveSelf = clone.ActiveInHierarchy = false;
        report.Tracker.Observe(clone, 20, 0.33, report);
        Assert.Equal(0, report.Tracker.Rekeyed);

        // Second life: the pool hands the same clone out under a different parent.
        var second = State(900, "Beam_Cardinal(Clone)", active: true, parent: 77);
        var row = report.Tracker.Observe(second, 30, 0.5, report);

        Assert.Equal(1, report.Tracker.Rekeyed);
        Assert.Equal(0, report.Tracker.Retired);          // nothing was lost, a new life started
        Assert.NotNull(row);
        Assert.Equal(CaptureChangeKind.Activated, row!.Kind);
    }

    [Theory]
    // a name change is decisive on its own, gap or no gap
    [InlineData("A", "B", true, true, 1, 1, 0, true)]
    // R29 finding 20: deactivated AND reparented, seen on the next pass, is a return to pool
    [InlineData("Beam(Clone)", "Beam(Clone)", true, false, 55, 10, 1, false)]
    [InlineData("Beam(Clone)", "Beam(Clone)", true, false, 55, 10, 4, false)]
    // the same shape after the id went unobserved: now id reuse is the better explanation
    [InlineData("Beam(Clone)", "Beam(Clone)", true, false, 55, 10, 5, true)]
    // an ordinary park: same parent, so it is the same object going quiet, whatever the gap
    [InlineData("Beam(Clone)", "Beam(Clone)", true, false, 10, 10, 99, false)]
    // an ordinary reparent while still active
    [InlineData("Beam(Clone)", "Beam(Clone)", true, true, 10, 55, 99, false)]
    public void LooksRecycled_needs_an_observation_gap_not_just_a_return_to_pool(
        string knownName, string scratchName, bool knownActive, bool scratchActive,
        int knownParent, int scratchParent, int unseenPasses, bool expected)
    {
        Assert.Equal(
            expected,
            CaptureObjectIdentity.LooksRecycled(
                knownName, scratchName, knownActive, scratchActive,
                knownParent, scratchParent, unseenPasses));
    }

    /// <summary>
    /// The gap is counted in tracker passes, not frames: the capture budget skips sweeps, so a frame
    /// gap says nothing about whether the id was observed. Four is one more than the worst run of
    /// skipped passes <c>CaptureBudget.MaxSweepSkipRatio</c> tolerates.
    /// </summary>
    [Fact]
    public void The_recycle_gap_is_four_passes()
    {
        Assert.Equal(4, CaptureObjectIdentity.RecycleUnseenPasses);
        Assert.False(CaptureObjectIdentity.LooksRecycled(
            "B(Clone)", "B(Clone)", true, false, 55, 10, unseenPasses: 4));
        Assert.True(CaptureObjectIdentity.LooksRecycled(
            "B(Clone)", "B(Clone)", true, false, 55, 10, unseenPasses: 5));
    }

    [Fact]
    public void The_identity_carries_the_parent_and_the_first_frame()
    {
        var report = new CaptureReport();
        report.Tracker.TryTrack(State(900, "Beam(Clone)", parent: 10), 7);
        Assert.Equal(CaptureObjectIdentity.Describe(900, 10, 7), report.Tracker.IdentityOf(900));
        Assert.Equal("", report.Tracker.IdentityOf(901));
        // Two clones parked under different parents are different identities on the same id.
        Assert.NotEqual(CaptureObjectIdentity.Describe(900, 10, 7), CaptureObjectIdentity.Describe(900, 11, 7));
        Assert.NotEqual(CaptureObjectIdentity.Describe(900, 10, 7), CaptureObjectIdentity.Describe(900, 10, 8));
    }

    /// <summary>
    /// R28 finding 18. The sampling table used to be a plain dictionary with an insert guarded by
    /// <c>Count &lt; 40</c> and no sweep: forty dead ids in the first seconds of a busy scene and
    /// not one line was sampled for the rest of the window, with nothing in the report saying so.
    /// </summary>
    [Fact]
    public void The_sampling_table_rotates_its_stalest_entry_instead_of_saturating_forever()
    {
        var sampled = new CaptureSampleRegistry();
        for (int i = 0; i < CaptureSampleRegistry.MaxEntries; i++)
            Assert.True(sampled.TryRegister(1000 + i, "line" + i, frameIndex: 0));

        Assert.False(sampled.Saturated);
        Assert.True(sampled.TryRegister(9999, "a later beam", frameIndex: 10));
        Assert.True(sampled.Saturated);
        Assert.Equal(1, sampled.Rotations);
        Assert.Equal(CaptureSampleRegistry.MaxEntries, sampled.Count);
        Assert.True(sampled.TryGet(9999, out var name));
        Assert.Equal("a later beam", name);

        // Entries seen on this very pass are live and are not rotated out.
        var busy = new CaptureSampleRegistry();
        for (int i = 0; i < CaptureSampleRegistry.MaxEntries; i++) busy.TryRegister(1000 + i, "line" + i, 5);
        Assert.False(busy.TryRegister(9999, "no room", 5));
        Assert.True(busy.Saturated);
        Assert.Equal(0, busy.Rotations);
        Assert.Equal(1, busy.SaturatedRegistrations);
    }

    [Fact]
    public void Retiring_a_series_frees_the_objects_sampling_slot()
    {
        var report = new CaptureReport();
        Assert.True(report.Sampled.TryRegister(900, "Beam(Clone)", 0));
        report.RetireSeries(900, "Beam(Clone)");
        Assert.False(report.Sampled.TryGet(900, out _));
    }

    /// <summary>
    /// A swept object must not keep its sampling slot either, or the table fills with dead ids the
    /// same way (R28 finding 18's smallest fix).
    /// </summary>
    [Fact]
    public void Sweeping_a_tracked_object_frees_its_series_and_its_sampling_slot()
    {
        var report = new CaptureReport();
        report.Tracker.TryTrack(State(900, "Gone(Clone)"), 0);
        report.Sampled.TryRegister(900, "Gone(Clone)", 0);
        var series = report.SeriesFor(900, "Gone(Clone)")!;

        Assert.Equal(1, report.Tracker.Sweep(120, 10, report: report));
        Assert.False(report.Sampled.TryGet(900, out _));
        Assert.True(series.Retired);
    }

    [Fact]
    public void SeriesFor_keys_on_the_name_as_well_as_the_id()
    {
        var report = new CaptureReport();
        var first = report.SeriesFor(900, "A")!;
        Assert.Same(first, report.SeriesFor(900, "A"));
        var second = report.SeriesFor(900, "B")!;
        Assert.NotSame(first, second);
        Assert.True(first.Retired);
    }

    [Fact]
    public void The_tracker_stops_at_the_object_cap_and_counts_what_it_dropped()
    {
        var report = new CaptureReport();
        for (int i = 0; i < CaptureArgs.MaxTrackedObjects; i++)
            Assert.True(report.Tracker.TryTrack(State(1000 + i, "go" + i), 0));

        Assert.False(report.Tracker.TryTrack(State(99_999, "one too many"), 0));
        Assert.False(report.Tracker.TryTrack(State(99_998, "two too many"), 0));
        Assert.Equal(CaptureArgs.MaxTrackedObjects, report.Tracker.Tracked);
        Assert.Equal(2, report.Tracker.Dropped);
    }

    [Fact]
    public void Tracking_the_same_object_twice_is_a_no_op_and_is_not_counted_as_a_drop()
    {
        var report = new CaptureReport();
        Assert.True(report.Tracker.TryTrack(State(900, "Beam"), 0));
        Assert.False(report.Tracker.TryTrack(State(900, "Beam"), 0));
        Assert.Equal(0, report.Tracker.Dropped);
        Assert.Equal(1, report.Tracker.Tracked);
    }

    [Fact]
    public void Change_rows_stop_at_their_cap_and_flag_the_report_truncated()
    {
        var report = new CaptureReport();
        for (int i = 0; i < CaptureArgs.MaxChangeRows; i++)
            Assert.True(report.TryAddChange(new CaptureChangeRow { InstanceId = i, Kind = CaptureChangeKind.Activated }));

        Assert.False(report.Truncated);
        Assert.False(report.TryAddChange(new CaptureChangeRow { InstanceId = -1 }));
        Assert.True(report.Truncated);
        Assert.Equal(CaptureArgs.MaxChangeRows, report.Changes.Count);
    }

    [Fact]
    public void A_transition_that_did_not_fit_the_cap_never_asks_for_a_description()
    {
        var report = new CaptureReport();
        for (int i = 0; i < CaptureArgs.MaxChangeRows; i++)
            report.TryAddChange(new CaptureChangeRow { InstanceId = i });

        var scratch = State(900, "Beam", active: false);
        report.Tracker.TryTrack(scratch, 0);
        scratch.ActiveSelf = true;
        scratch.ActiveInHierarchy = true;
        Assert.Null(report.Tracker.Observe(scratch, 10, 0.16, report));
        Assert.Equal(0, report.Tracker.Transitions);
    }

    [Fact]
    public void NoteParentChange_records_the_pool_take_the_scene_sweep_cannot_see()
    {
        // The pool parks IDLE clones, so an id that LEFT the pool parent's child set is a spawn.
        var report = new CaptureReport();
        report.Tracker.TryTrack(State(900, "Beam_Cardinal(Clone)", parent: 10), 0);

        var row = report.Tracker.NoteParentChange(
            900, 30, 0.5, "<sequencer pool parent>", "<taken from the pool>", report);

        Assert.NotNull(row);
        Assert.Equal(CaptureChangeKind.Reparented, row!.Kind);
        Assert.Equal("Beam_Cardinal(Clone)", row.Name);
        Assert.True(report.Tracker.WasComparedThisPass(900, 30));
        Assert.Null(report.Tracker.NoteParentChange(4242, 30, 0.5, "a", "b", report));
    }

    [Fact]
    public void Sweep_retires_objects_no_pass_has_seen_for_a_while_and_frees_their_slots()
    {
        var report = new CaptureReport();
        report.Tracker.TryTrack(State(900, "Gone"), 0);
        report.Tracker.TryTrack(State(901, "Here"), 0);
        report.Tracker.Observe(State(901, "Here"), 100, 1.6, report);

        Assert.Equal(0, report.Tracker.Sweep(10, 10));      // too early to judge anything
        Assert.Equal(2, report.Tracker.Tracked);

        Assert.Equal(1, report.Tracker.Sweep(120, 10));
        Assert.Equal(1, report.Tracker.Tracked);
        Assert.Equal(1, report.Tracker.Retired);
        Assert.True(report.Tracker.IsTracked(901));
        Assert.False(report.Tracker.IsTracked(900));
    }

    [Fact]
    public void The_tracker_json_carries_the_cap_and_the_accounting()
    {
        var report = new CaptureReport();
        report.Tracker.TryTrack(State(900, "Beam"), 0);
        report.Tracker.BeginPass();
        var json = report.Tracker.ToJson();

        Assert.Equal(CaptureArgs.MaxTrackedObjects, json["maxTracked"]);
        Assert.Equal(1, json["tracked"]);
        Assert.Equal(0, json["dropped"]);
        Assert.Equal(1, json["passes"]);
    }

    // ---- endpoint tracks ---------------------------------------------------------------------

    [Fact]
    public void The_first_observation_sets_first_and_last_without_a_change()
    {
        var track = new CaptureEndpointTrack();
        track.Observe("(0,1,0)");
        Assert.Equal("(0,1,0)", track.First);
        Assert.Equal("(0,1,0)", track.Last);
        Assert.Equal(1, track.Samples);
        Assert.False(track.Changed);
    }

    [Fact]
    public void A_track_that_changes_and_returns_stays_changed()
    {
        var track = new CaptureEndpointTrack();
        track.Observe("(0,1,0)");
        track.Observe("(3,1,0)");
        track.Observe("(0,1,0)");
        Assert.True(track.Changed);
        Assert.Equal("(0,1,0)", track.First);
        Assert.Equal("(0,1,0)", track.Last);
        Assert.Equal(3, track.Samples);
    }

    [Fact]
    public void A_constant_track_never_reports_a_change()
    {
        var track = new CaptureEndpointTrack();
        for (int i = 0; i < 20; i++) track.Observe("{sequenceGuid=586432528}");
        Assert.False(track.Changed);
        Assert.Equal(20, track.Samples);
    }

    // ---- report caps -------------------------------------------------------------------------

    [Fact]
    public void Row_caps_flag_the_report_truncated_instead_of_growing_without_bound()
    {
        var report = new CaptureReport();
        for (int i = 0; i < CaptureArgs.MaxAppearedRows; i++)
            Assert.True(report.TryAddAppeared(new CaptureEntityRow { Entity = i + ":1" }));
        Assert.False(report.Truncated);
        Assert.False(report.TryAddAppeared(new CaptureEntityRow { Entity = "over:1" }));
        Assert.True(report.Truncated);
        Assert.Equal(CaptureArgs.MaxAppearedRows, report.Appeared.Count);
    }

    [Fact]
    public void Object_rows_have_their_own_cap()
    {
        var report = new CaptureReport();
        for (int i = 0; i < CaptureArgs.MaxObjectRows; i++)
            Assert.True(report.TryAddObject(new CaptureObjectRow { InstanceId = i, Name = "go" + i }));
        Assert.False(report.TryAddObject(new CaptureObjectRow { InstanceId = -1, Name = "over" }));
        Assert.True(report.Truncated);
    }

    /// <summary>
    /// R28 finding 18. The cap used to stop the table dead: once forty series existed, every later
    /// line got <c>null</c> for the rest of the window and <c>lineSamples</c> — the question the op
    /// exists to answer — was empty with nothing saying why. The cap now bounds the number of LIVE
    /// series and rotates the stalest one out, so a long capture keeps sampling; saturation is a
    /// reported condition, not a silent stop.
    /// </summary>
    [Fact]
    public void SeriesFor_reuses_by_instance_id_and_rotates_rather_than_stopping_at_the_cap()
    {
        var report = new CaptureReport();
        var first = report.SeriesFor(7, "Beam");
        Assert.NotNull(first);
        Assert.Same(first, report.SeriesFor(7, "Beam"));

        for (int i = 0; i < CaptureSampling.MaxSeries - 1; i++)
            Assert.NotNull(report.SeriesFor(1000 + i, "line" + i));

        // The forty-first line is still sampled: the oldest live series steps aside for it.
        var overflow = report.SeriesFor(99_999, "one too many");
        Assert.NotNull(overflow);
        Assert.True(report.SeriesSaturated);
        Assert.Equal(1, report.SeriesRotations);
        Assert.True(report.Truncated);

        int live = 0;
        foreach (var s in report.Series) if (!s.Retired) live++;
        Assert.Equal(CaptureSampling.MaxSeries, live);
        Assert.True(Assert.IsType<bool>(report.Summary()["samplingSaturated"]));
    }

    /// <summary>
    /// The other half of finding 18: retired series used to count against the cap, so forty
    /// id-reuse retirements burned every slot permanently even in a scene with two live lines.
    /// </summary>
    [Fact]
    public void Retired_series_do_not_burn_slots_forever()
    {
        var report = new CaptureReport();
        for (int i = 0; i < CaptureSampling.MaxSeries; i++)
        {
            Assert.NotNull(report.SeriesFor(1000 + i, "line" + i));
            report.RetireSeries(1000 + i, "line" + i);
        }

        // Every slot is retired, so a fresh line is a plain insert: no rotation, no saturation.
        Assert.NotNull(report.SeriesFor(2000, "a live line"));
        Assert.False(report.SeriesSaturated);
        Assert.Equal(0, report.SeriesRotations);
    }

    [Fact]
    public void TrackFor_reuses_the_same_entity_component_field_triple()
    {
        var report = new CaptureReport();
        var a = report.TrackFor("12:3", "SpawnSequenceForEntity", "Target");
        var b = report.TrackFor("12:3", "SpawnSequenceForEntity", "Target");
        var c = report.TrackFor("12:3", "SpawnSequenceForEntity", "SecondaryTarget");
        Assert.Same(a, b);
        Assert.NotSame(a, c);
        Assert.Equal(2, report.Endpoints.Count);
    }

    // ---- summary -----------------------------------------------------------------------------

    [Fact]
    public void Summary_reports_counts_including_the_budget_skips()
    {
        var report = BuildSampleReport();
        var summary = report.Summary();
        var counts = Assert.IsType<Dictionary<string, object?>>(summary["counts"]);

        Assert.Equal(2, counts["entitiesAppeared"]);
        Assert.Equal(1, counts["entitiesDisappeared"]);
        Assert.Equal(2, counts["gameObjectsAppeared"]);
        Assert.Equal(1, counts["skippedGameObjectWalks"]);
        Assert.Equal("stop", summary["stopReason"]);
    }

    /// <summary>
    /// The chain the research section documents: sequence guid → the prefab of the entity that
    /// carried it → the GameObjects the sequencer produced just after. Objects outside the
    /// four-frame window belong to something else and must not be attributed to this guid.
    /// </summary>
    [Fact]
    public void Sequence_rows_pair_each_new_guid_with_its_prefab_and_the_objects_around_it()
    {
        var report = BuildSampleReport();
        var rows = report.SequenceRows();

        Assert.Single(rows);
        Assert.Equal(586432528, rows[0]["guid"]);
        Assert.Equal(new List<string> { "AB_Cardinal_SummonOrb_Beam_Buff" }, rows[0]["prefabs"]);
        var objects = Assert.IsType<List<string>>(rows[0]["gameObjects"]);
        Assert.Contains("Beam_Cardinal(Clone)", objects);
        Assert.DoesNotContain("Unrelated_Late_Object", objects);
    }

    [Fact]
    public void Sequence_rows_skip_a_zero_guid()
    {
        var report = new CaptureReport();
        var row = new CaptureEntityRow { Entity = "1:1", Frame = 0 };
        row.SequenceGuids.Add(0);
        report.TryAddAppeared(row);
        Assert.Empty(report.SequenceRows());
    }

    [Fact]
    public void Endpoint_rows_say_which_component_changed_and_which_field()
    {
        var report = BuildSampleReport();
        var rows = report.EndpointRows();

        var spawn = rows.Find(r => (string?)r["component"] == "SpawnSequenceForEntity");
        Assert.NotNull(spawn);
        Assert.Equal(true, spawn!["changed"]);
        Assert.Equal(new List<string> { "Target" }, spawn["changedFields"]);
        Assert.Equal(1, spawn["entities"]);

        var coil = rows.Find(r => (string?)r["component"] == "ProfessorCoilBeam_State_Shared");
        Assert.NotNull(coil);
        Assert.Equal(false, coil!["changed"]);
        Assert.Empty(Assert.IsType<List<string>>(coil["changedFields"]));
    }

    [Fact]
    public void Line_rows_carry_the_moved_verdict_per_object()
    {
        var report = BuildSampleReport();
        var rows = report.LineRows();
        Assert.Single(rows);
        Assert.Equal("Beam_Cardinal(Clone)", rows[0]["name"]);
        Assert.Equal(true, rows[0]["moved"]);
        Assert.Equal(2, rows[0]["samples"]);
    }

    /// <summary>
    /// The column to read first on a pooled effect, where <c>gameObjects</c> is empty by
    /// construction: one row per name, the kinds it went through, and how many rows it produced.
    /// </summary>
    [Fact]
    public void Changed_objects_group_by_name_with_their_kinds_and_a_count()
    {
        var report = BuildSampleReport();
        var rows = report.ChangedObjectRows();

        Assert.Equal(2, rows.Count);
        Assert.Equal("Beam_Cardinal(Clone)", rows[0]["name"]);
        var kinds = Assert.IsType<List<string>>(rows[0]["kinds"]);
        Assert.Equal(new List<string> { CaptureChangeKind.Activated, CaptureChangeKind.LineMoved }, kinds);
        Assert.Equal(3, rows[0]["count"]);

        Assert.Equal("Sparks", rows[1]["name"]);
        Assert.Equal(new List<string> { CaptureChangeKind.ParticleStarted }, rows[1]["kinds"]);
        Assert.Equal(1, rows[1]["count"]);
    }

    [Fact]
    public void An_unnamed_change_row_groups_under_its_instance_id()
    {
        var report = new CaptureReport();
        report.TryAddChange(new CaptureChangeRow { InstanceId = 900, Kind = CaptureChangeKind.Activated });
        Assert.Equal("#900", report.ChangedObjectRows()[0]["name"]);
    }

    [Fact]
    public void Summary_counts_the_change_rows_and_the_tracked_set()
    {
        var counts = Assert.IsType<Dictionary<string, object?>>(BuildSampleReport().Summary()["counts"]);
        Assert.Equal(4, counts["gameObjectChanges"]);
        Assert.Equal(2, counts["trackedObjects"]);
        Assert.Equal(0, counts["droppedObjects"]);
        Assert.True(counts.ContainsKey("skippedSceneScans"));
        Assert.True(counts.ContainsKey("skippedComparisons"));
    }

    [Fact]
    public void The_written_report_has_the_documented_top_level_shape()
    {
        var report = BuildSampleReport();
        var json = report.ToJson();

        Assert.True(json.ContainsKey("capture"));
        Assert.True(json.ContainsKey("entitiesAppeared"));
        Assert.True(json.ContainsKey("entitiesDisappeared"));
        Assert.True(json.ContainsKey("gameObjects"));
        Assert.True(json.ContainsKey("gameObjectChanges"));
        Assert.True(json.ContainsKey("lineSamples"));
        Assert.True(json.ContainsKey("endpointTracks"));

        var header = Assert.IsType<Dictionary<string, object?>>(json["capture"]);
        Assert.Equal("client", header["side"]);
        Assert.Equal("Client_0", header["world"]);
        Assert.True(header.ContainsKey("budget"));
        Assert.True(header.ContainsKey("baseline"));
        Assert.True(header.ContainsKey("tracking"));
        Assert.True(header.ContainsKey("entityOverflow"));
        Assert.True(header.ContainsKey("scanOverflow"));
    }

    [Fact]
    public void A_change_row_serializes_the_documented_columns()
    {
        var rows = Assert.IsType<List<Dictionary<string, object?>>>(BuildSampleReport().ToJson()["gameObjectChanges"]);
        var first = rows[0];
        Assert.Equal(900, first["instanceId"]);
        Assert.Equal("Beam_Cardinal(Clone)", first["name"]);
        Assert.Equal(CaptureChangeKind.Activated, first["kind"]);
        Assert.True(first.ContainsKey("frame"));
        Assert.True(first.ContainsKey("atSeconds"));
        Assert.True(first.ContainsKey("before"));
        Assert.True(first.ContainsKey("after"));
        Assert.NotNull(first["description"]);
    }

    [Fact]
    public void The_report_serializes_to_json_without_a_cycle_or_an_unsupported_type()
    {
        var text = JsonSerializer.Serialize(BuildSampleReport().ToJson());
        Assert.Contains("\"sequenceGuids\"", text);
        Assert.Contains("Beam_Cardinal(Clone)", text);
    }

    // ---- paths -------------------------------------------------------------------------------

    [Fact]
    public void The_report_folder_is_a_sibling_of_the_reload_folder_not_a_child()
    {
        var reload = Path.Combine("C:", "Game", "BepInEx", "progenitor-reload");
        var folder = CaptureReportPaths.Folder(reload);
        Assert.Equal(Path.Combine("C:", "Game", "BepInEx", "progenitor-capture"), folder);
        Assert.DoesNotContain("progenitor-reload", folder);
    }

    [Fact]
    public void A_trailing_separator_on_the_reload_folder_does_not_shift_the_parent()
    {
        var withSlash = Path.Combine("C:", "Game", "BepInEx", "progenitor-reload") + Path.DirectorySeparatorChar;
        Assert.Equal(
            CaptureReportPaths.Folder(Path.Combine("C:", "Game", "BepInEx", "progenitor-reload")),
            CaptureReportPaths.Folder(withSlash));
    }

    [Fact]
    public void The_file_name_is_a_sortable_utc_stamp()
    {
        var name = CaptureReportPaths.FileName(new DateTime(2026, 9, 3, 19, 4, 5, 678, DateTimeKind.Utc));
        Assert.Equal("20260903-190405-678.json", name);
    }

    [Fact]
    public void For_combines_the_folder_and_the_stamp()
    {
        var reload = Path.Combine("C:", "Game", "BepInEx", "progenitor-reload");
        var stamp = new DateTime(2026, 9, 3, 19, 4, 5, 678, DateTimeKind.Utc);
        Assert.Equal(
            Path.Combine("C:", "Game", "BepInEx", "progenitor-capture", "20260903-190405-678.json"),
            CaptureReportPaths.For(reload, stamp));
    }

    // ---- helpers -----------------------------------------------------------------------------

    /// <summary>
    /// A capture shaped like the one the workflow produces: a coil beam entity and a sequence
    /// entity appear, the sequencer's clone shows up two frames later, an unrelated object shows
    /// up much later, one endpoint moves and one does not, and one frame skipped its walk.
    /// </summary>
    private static CaptureReport BuildSampleReport()
    {
        var report = new CaptureReport
        {
            Side = "client",
            World = "Client_0",
            StartedUtc = new DateTime(2026, 9, 3, 19, 0, 0, DateTimeKind.Utc),
            RequestedSeconds = 10,
            ElapsedSeconds = 10,
            Filter = CaptureArgs.DefaultFilter,
            MaxEntities = CaptureArgs.DefaultMaxEntities,
            BaselineEntities = 41,
            BaselineObjects = 118,
            StopReason = "stop",
        };

        var sequence = new CaptureEntityRow
        {
            Entity = "5120:3",
            Frame = 12,
            PrefabHash = -2014639169,
            PrefabName = "AB_Cardinal_SummonOrb_Beam_Buff",
        };
        sequence.SequenceGuids.Add(586432528);
        sequence.Components.Add("SpawnSequenceForEntity");
        sequence.Endpoints["SpawnSequenceForEntity"] = new Dictionary<string, object?>
        {
            ["SequenceGuid"] = new Dictionary<string, object?> { ["sequenceGuid"] = 586432528 },
        };
        report.TryAddAppeared(sequence);

        var coil = new CaptureEntityRow { Entity = "5121:1", Frame = 12, PrefabName = null };
        coil.Components.Add("ProfessorCoilBeam_State_Shared");
        report.TryAddAppeared(coil);

        report.TryAddDisappeared(new CaptureEntityRow { Entity = "5120:3", Frame = 300 });

        report.TryAddObject(new CaptureObjectRow
        {
            InstanceId = 900,
            Name = "Beam_Cardinal(Clone)",
            Frame = 14,
            Source = "particleParent",
            Active = true,
        });
        report.TryAddObject(new CaptureObjectRow
        {
            InstanceId = 901,
            Name = "Unrelated_Late_Object",
            Frame = 200,
            Source = "lineRenderer",
        });

        var series = report.SeriesFor(900, "Beam_Cardinal(Clone)")!;
        series.TryAdd(20, 0.33, Two(0, 1, 0, 5, 1, 0));
        series.TryAdd(30, 0.5, Two(0, 1, 0, 7.5f, 1, 0));

        var target = report.TrackFor("5120:3", "SpawnSequenceForEntity", "Target")!;
        target.Observe("{index=1,version=2,waitingForSync=false}");
        target.Observe("{index=9,version=2,waitingForSync=false}");

        var start = report.TrackFor("5121:1", "ProfessorCoilBeam_State_Shared", "Start")!;
        start.Observe("{x=0,y=1,z=0}");
        start.Observe("{x=0,y=1,z=0}");

        // The pooled clone: parked and inactive at baseline, woken by the trigger, its line moved
        // twice, and a sibling particle system started. This is what evidence 137 could not see.
        var clone = State(900, "Beam_Cardinal(Clone)", active: false, parent: 10);
        report.Tracker.TryTrack(clone, 0);
        clone.ActiveSelf = true;
        clone.ActiveInHierarchy = true;
        var activation = report.Tracker.Observe(clone, 20, 0.33, report);
        activation!.Description = new CaptureObjectRow
        {
            InstanceId = 900,
            Name = "Beam_Cardinal(Clone)",
            Source = "particleParent",
            Frame = 20,
            Active = true,
        };

        report.TryAddChange(new CaptureChangeRow
        {
            InstanceId = 900,
            Name = "Beam_Cardinal(Clone)",
            Frame = 30,
            Kind = CaptureChangeKind.LineMoved,
            Before = "line[2]=(0,1,0)|(5,1,0)",
            After = "line[2]=(0,1,0)|(7.5,1,0)",
        });
        report.TryAddChange(new CaptureChangeRow
        {
            InstanceId = 900,
            Name = "Beam_Cardinal(Clone)",
            Frame = 40,
            Kind = CaptureChangeKind.LineMoved,
        });
        report.TryAddChange(new CaptureChangeRow
        {
            InstanceId = 904,
            Name = "Sparks",
            Frame = 30,
            Kind = CaptureChangeKind.ParticleStarted,
        });
        report.Tracker.TryTrack(State(904, "Sparks"), 0);

        report.Budget.Record(0.4, skippedWalk: false);
        report.Budget.Record(0.5, skippedWalk: false);
        report.Budget.Record(3.1, skippedWalk: true);
        return report;
    }

    private static CaptureObjectState State(int instanceId, string name, bool active = true, int parent = 0)
    {
        var state = new CaptureObjectState
        {
            InstanceId = instanceId,
            Name = name,
            ActiveSelf = active,
            ActiveInHierarchy = active,
            ParentInstanceId = parent,
        };
        return state;
    }

    private static CaptureObjectState Line(int instanceId, string name, params (float X, float Y, float Z)[] points)
    {
        var state = State(instanceId, name);
        state.HasLine = true;
        state.LinePositionCount = points.Length;
        foreach (var p in points) state.LinePositions.Add(new CaptureVec3(p.X, p.Y, p.Z));
        return state;
    }

    /// <summary>Drives the production scan selection and returns the instance ids it kept.</summary>
    private static int[] KeptIds(int[] instanceIds, int max)
    {
        var ids = (int[])instanceIds.Clone();
        var slots = new int[instanceIds.Length];
        for (int i = 0; i < slots.Length; i++) slots[i] = i;
        int keep = CaptureScanSelection.SortAndClip(ids, slots, instanceIds.Length, max);
        var kept = new int[keep];
        for (int i = 0; i < keep; i++) kept[i] = instanceIds[slots[i]];
        Array.Sort(kept);
        return kept;
    }

    private static long[] Keys(int[] indices)
    {
        var keys = new long[indices.Length];
        for (int i = 0; i < indices.Length; i++) keys[i] = CaptureEntitySelection.Key(indices[i], 1);
        return keys;
    }

    private static List<CaptureVec3> One(float x, float y, float z) => new() { new CaptureVec3(x, y, z) };

    private static List<CaptureVec3> Two(float ax, float ay, float az, float bx, float by, float bz) =>
        new() { new CaptureVec3(ax, ay, az), new CaptureVec3(bx, by, bz) };

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
}
