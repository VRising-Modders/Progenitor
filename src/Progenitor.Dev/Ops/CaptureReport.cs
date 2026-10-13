using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free half of the client <c>capture</c> op: argument policy, the per-frame sampling
/// throttle, the frame budget accounting, the report model, and the compact summary the bridge
/// returns. Everything that touches ECS, Unity, or the interop lives in <c>CaptureOps</c>, so
/// this file compiles into <c>Progenitor.Tests</c> (net8, no game assemblies) unchanged.
///
/// Why the split matters here: the whole point of the op is to watch the GAME draw its own
/// beams while the user triggers them, which cannot be unit tested. The parts that CAN silently
/// rot — the throttle arithmetic, the budget-skip counters, the "did this endpoint move" verdict,
/// and the summary shape the main session reads — are all in this file.
/// </summary>
internal static class CaptureArgs
{
    public const double DefaultSeconds = 10;
    public const double MinSeconds = 1;

    /// <summary>
    /// Hard ceiling on a capture window. The socket waiter times out at 30 s (docs/bridge.md),
    /// so <c>start</c> returns immediately and the window is allowed to outlive that; 120 s is
    /// still short enough that a forgotten capture cannot run for the rest of the session.
    /// </summary>
    public const double MaxSeconds = 120;

    public const int DefaultMaxEntities = 2000;
    public const int MinMaxEntities = 16;
    public const int HardMaxEntities = 20_000;

    /// <summary>Substring tokens, OR-ed, matched case-insensitively against GameObject and component names.</summary>
    public const string DefaultFilter = "beam|sequence|line";

    /// <summary>Rows kept per bucket before the report flags itself truncated.</summary>
    public const int MaxAppearedRows = 500;
    public const int MaxDisappearedRows = 500;
    public const int MaxObjectRows = 200;

    /// <summary>
    /// GameObjects the "changed" detector keeps a snapshot of. The baseline fills this set (every
    /// child of the sequencer's particle parent plus every <c>LineRenderer</c> /
    /// <c>HybridLineRendererComponent</c> in the scene); mid-window discoveries that pass the name
    /// filter take whatever room is left. Overflow is counted in
    /// <see cref="CaptureObjectTracker.Dropped"/> rather than silently ignored, because a report
    /// that tracked only part of the pool must say so.
    /// </summary>
    public const int MaxTrackedObjects = 512;

    /// <summary>Transition rows kept before the report flags itself truncated.</summary>
    public const int MaxChangeRows = 400;

    /// <summary>Entities whose endpoint fields are re-read on sample frames (the "did it move" track).</summary>
    public const int MaxTrackedEntities = 32;

    /// <summary>
    /// A GameObject that appears within this many frames of an entity carrying a sequence guid is
    /// listed under that guid in the summary. The sequencer spawns the clone one or two frames
    /// after the sequence entity (research effects-module.md 17.2, 21.1), never instantly.
    ///
    /// <para>It has to be at least one sampling interval wide (R27 finding 22): a
    /// <c>lineRenderer</c>-sourced row can only carry a frame number the scene scan ran on, which
    /// is a multiple of <see cref="CaptureSampling.SampleEveryNthFrame"/>, so a four-frame window
    /// attributed the build-mode preview beams to their sequence only when the phase happened to
    /// line up. One interval plus the observed one-to-two-frame spawn delay covers every phase.</para>
    /// </summary>
    public const int SequenceObjectFrameWindow = CaptureSampling.SampleEveryNthFrame + 4;

    public static double ClampSeconds(double seconds)
    {
        if (double.IsNaN(seconds)) return DefaultSeconds;
        return Math.Clamp(seconds, MinSeconds, MaxSeconds);
    }

    public static int ClampMaxEntities(int max) => Math.Clamp(max, MinMaxEntities, HardMaxEntities);

    /// <summary>
    /// Splits <c>args.filter</c> on <c>|</c> into lowercase tokens. Null / empty / whitespace
    /// means "no filter", which <see cref="Matches"/> treats as match-everything (the entity side
    /// is always driven by the component interest set, never by this filter).
    /// </summary>
    public static string[] ParseFilter(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return Array.Empty<string>();
        var parts = filter.Split('|');
        var list = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var token = part.Trim();
            if (token.Length == 0) continue;
            list.Add(token.ToLowerInvariant());
        }
        return list.ToArray();
    }

    public static bool Matches(string[] filter, string? name)
    {
        if (filter == null || filter.Length == 0) return true;
        if (string.IsNullOrEmpty(name)) return false;
        var lower = name!.ToLowerInvariant();
        for (int i = 0; i < filter.Length; i++)
            if (lower.Contains(filter[i], StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>True when any name in the list passes the filter.</summary>
    public static bool MatchesAny(string[] filter, IReadOnlyList<string> names)
    {
        if (filter == null || filter.Length == 0) return true;
        for (int i = 0; i < names.Count; i++)
            if (Matches(filter, names[i])) return true;
        return false;
    }

    /// <summary><c>args.action</c>: <c>start</c>, <c>stop</c>, or <c>status</c>. Anything else throws.</summary>
    public static string ParseAction(JsonElement args)
    {
        string action = "status";
        if (args.ValueKind == JsonValueKind.Object
            && args.TryGetProperty("action", out var el)
            && el.ValueKind == JsonValueKind.String)
        {
            action = (el.GetString() ?? "").Trim().ToLowerInvariant();
        }

        return action switch
        {
            "start" or "stop" or "status" => action,
            "" => "status",
            _ => throw new ArgumentException($"args.action must be start, stop, or status (got '{action}')"),
        };
    }
}

/// <summary>
/// Per-frame sampling throttle. Position samples are the only thing the capture records
/// repeatedly for the same object, so they are the only thing that can grow without bound in a
/// long window: every 10th frame, at most 50 samples per object, at most 40 objects.
///
/// <para>The interval is a floor, not a constant. The scene sweep the sampling frame carries
/// (<c>FindObjectsOfType</c> over every <c>LineRenderer</c> and
/// <c>HybridLineRendererComponent</c>) costs 5 to 30 ms in a loaded client, and running it every
/// tenth frame regardless is a visible stutter for the whole window (R27 finding 8). The capture
/// measures that sweep and <see cref="IntervalFor"/> widens the interval until its amortized cost
/// per frame is back inside the budget.</para>
/// </summary>
internal static class CaptureSampling
{
    public const int SampleEveryNthFrame = 10;
    public const int MaxSamplesPerObject = 50;
    public const int MaxSeries = 40;

    /// <summary>
    /// Ceiling on the adaptive interval. At 60 fps this is one sweep every two seconds — slow, but
    /// still enough to see a beam that lives for several seconds, and it keeps a pathological
    /// scene from switching the GameObject half off entirely.
    /// </summary>
    public const int MaxSampleInterval = 120;

    /// <summary>Frame indices are 0-based, so frame 0 samples and gives the series a baseline.</summary>
    public static bool ShouldSample(int frameIndex) => ShouldSample(frameIndex, SampleEveryNthFrame);

    /// <summary>Sampling test against an interval the budget widened.</summary>
    public static bool ShouldSample(int frameIndex, int interval)
    {
        if (frameIndex < 0) return false;
        if (interval < SampleEveryNthFrame) interval = SampleEveryNthFrame;
        return frameIndex % interval == 0;
    }

    /// <summary>
    /// How many frames apart the scene sweeps have to be for a sweep costing
    /// <paramref name="scanMs"/> to amortize to <paramref name="budgetMs"/> per frame. Never
    /// narrower than <see cref="SampleEveryNthFrame"/>, never wider than
    /// <see cref="MaxSampleInterval"/>. An unmeasured sweep (0 ms) keeps the default interval so
    /// the first one always runs and produces the measurement.
    /// </summary>
    public static int IntervalFor(double scanMs, double budgetMs)
    {
        if (double.IsNaN(scanMs) || scanMs <= 0 || double.IsNaN(budgetMs) || budgetMs <= 0)
            return SampleEveryNthFrame;
        double needed = Math.Ceiling(scanMs / budgetMs);
        if (needed <= SampleEveryNthFrame) return SampleEveryNthFrame;
        if (needed >= MaxSampleInterval) return MaxSampleInterval;
        return (int)needed;
    }

    /// <summary>
    /// Ceiling on the interval the two per-frame tiers (the entity diff, the pool walk) are allowed
    /// to degrade to. Unlike the scene sweep these normally run on EVERY frame, so their floor is 1
    /// rather than <see cref="SampleEveryNthFrame"/>, and their ceiling has to stay tight enough
    /// that the tier still reports something: a tier that degrades to "never" is precisely the
    /// failure R28 finding 5 describes.
    /// </summary>
    public const int MaxTierInterval = 30;

    /// <summary>
    /// How many frames apart a per-frame tier costing <paramref name="costMs"/> has to run for its
    /// amortized cost to fit <paramref name="budgetMs"/>. 1 (every frame) while it is affordable,
    /// never wider than <paramref name="maxInterval"/>. An unmeasured tier (0 ms) runs every frame,
    /// because that first run is what produces the measurement.
    /// </summary>
    public static int TierIntervalFor(double costMs, double budgetMs, int maxInterval)
    {
        if (maxInterval < 1) maxInterval = 1;
        if (double.IsNaN(costMs) || costMs <= 0 || double.IsNaN(budgetMs) || budgetMs <= 0) return 1;
        double needed = Math.Ceiling(costMs / budgetMs);
        if (needed <= 1) return 1;
        if (needed >= maxInterval) return maxInterval;
        return (int)needed;
    }

    public static bool CanAddSample(int samplesTaken) => samplesTaken < MaxSamplesPerObject;

    /// <summary>Both gates at once: the right frame AND room left in the series.</summary>
    public static bool TrySample(int frameIndex, int samplesTaken) =>
        ShouldSample(frameIndex) && CanAddSample(samplesTaken);

    /// <summary>Samples a window of <paramref name="frames"/> frames would produce for one object.</summary>
    public static int ExpectedSamples(int frames)
    {
        if (frames <= 0) return 0;
        int ticks = ((frames - 1) / SampleEveryNthFrame) + 1;
        return Math.Min(ticks, MaxSamplesPerObject);
    }
}

/// <summary>
/// Which entities a frame's diff is allowed to use when the interest query returns more than
/// <c>maxEntities</c>.
///
/// <para>R27 finding 9: the first version took <c>ToEntityArray</c>'s chunk order and clipped it,
/// so the kept set moved every frame and the diff manufactured an appear/disappear storm that
/// burned both 500-row caps within a few frames — precisely in a busy area, where
/// <c>BuffSequence</c> sits on every live buff instance. The fix is two rules: keep a subset that
/// is stable frame to frame (sort by entity index, keep the first N), and never emit disappearance
/// rows from a frame that had to truncate, because a partial scan cannot tell "gone" from "did not
/// fit".</para>
/// </summary>
internal static class CaptureEntitySelection
{
    /// <summary>Sort key for one entity: index first, then version, both ascending.</summary>
    public static long Key(int index, int version) => ((long)(uint)index << 32) | (uint)version;

    public static int IndexOfKey(long key) => (int)(uint)(key >> 32);

    public static int VersionOfKey(long key) => (int)(uint)key;

    /// <summary>How many of <paramref name="available"/> entities the frame keeps.</summary>
    public static int Keep(int available, int max)
    {
        if (available <= 0) return 0;
        if (max <= 0) return 0;
        return available <= max ? available : max;
    }

    /// <summary>How many the frame had to drop; 0 when everything fit.</summary>
    public static int Overflow(int available, int max) => Math.Max(0, available - Keep(available, max));

    /// <summary>A truncated frame is not allowed to conclude that anything disappeared.</summary>
    public static bool ShouldSkipDisappearance(int available, int max) => Overflow(available, max) > 0;
}

/// <summary>
/// What one frame of the capture is allowed to do. Produced by
/// <see cref="CaptureBudget.PlanFrame"/> from the tier intervals and the running average, then
/// narrowed by <see cref="CaptureBudget.Confirm"/> once the cheap part of the frame has been
/// measured.
///
/// <para>The <c>Forced</c> flags are the floor R28 finding 5 asks for: a tier that the budget would
/// otherwise switch off runs anyway once it has been silent for
/// <see cref="CaptureBudget.MinScanEveryFrames"/> / <see cref="CaptureBudget.MinWalkEveryFrames"/>
/// frames. A capture that stutters is recoverable; a capture that quietly records nothing is
/// not.</para>
/// </summary>
internal readonly struct CaptureTierPlan
{
    public CaptureTierPlan(
        bool runEntityDiff, bool runWalk, bool runScan,
        bool entityDiffDue, bool walkDue, bool scanDue,
        bool forcedWalk, bool forcedScan)
    {
        RunEntityDiff = runEntityDiff;
        RunWalk = runWalk;
        RunScan = runScan;
        EntityDiffDue = entityDiffDue;
        WalkDue = walkDue;
        ScanDue = scanDue;
        ForcedWalk = forcedWalk;
        ForcedScan = forcedScan;
    }

    public bool RunEntityDiff { get; }
    public bool RunWalk { get; }
    public bool RunScan { get; }

    /// <summary>The tier's own interval said this frame was its turn.</summary>
    public bool EntityDiffDue { get; }
    public bool WalkDue { get; }
    public bool ScanDue { get; }

    /// <summary>The tier ran only because the floor was due, not because it was affordable.</summary>
    public bool ForcedWalk { get; }
    public bool ForcedScan { get; }

    /// <summary>
    /// A tier the frame did not run. The entity diff wants to run on every frame, so any frame
    /// without one is a skip; the walk and the sweep only count a skip when their own interval said
    /// this frame was their turn.
    /// </summary>
    public bool SkippedEntityDiff => !RunEntityDiff;
    public bool SkippedWalk => WalkDue && !RunWalk;
    public bool SkippedScan => ScanDue && !RunScan;
}

/// <summary>
/// Frame-time accounting for the capture, and the gate that decides how much of a frame's work is
/// affordable.
///
/// <para>Three tiers, cheapest first: the entity diff, the particle-parent walk, the scene sweep.
/// Each has <b>its own</b> measured cost, its own amortizing interval and its own skip counter, and
/// none of them can switch another off — R28 finding 5 and finding 9. Before this, tier 1 ran
/// ungated and its cost was charged to the <c>elapsedMsSoFar</c> that gated tiers 2 and 3, so a
/// crowded area (where tier 1 is expensive) switched off exactly the two tiers the op exists for,
/// and the report said <c>truncated: false</c>.</para>
///
/// <para><b>Degrade, never stop.</b> An over-budget tier widens its interval
/// (<see cref="CaptureSampling.TierIntervalFor"/>, <see cref="CaptureSampling.IntervalFor"/>) up to
/// a ceiling, and <see cref="PlanFrame"/> forces it to run anyway once it has been silent for
/// <see cref="MinWalkEveryFrames"/> / <see cref="MinScanEveryFrames"/> frames.</para>
///
/// <para><b>The running average is seeded, not sampled.</b> R28 finding 8: seeding the EWMA with
/// the first frame meant seeding it with the capture's single most expensive frame (frame 0 runs a
/// full sweep), which then blacked out the next ~80 frames — the 1.5 seconds the operator was told
/// to trigger the effect in. It now starts at <see cref="SeedFractionOfBudget"/> of the budget and
/// converges from there.</para>
///
/// <para>Every skip is counted and the skip ratio is reported, so a window that missed most of its
/// sweeps says so (<see cref="SweepSkipRatioExceeded"/>, R28 finding 7) instead of returning a
/// confident empty report.</para>
/// </summary>
internal sealed class CaptureBudget
{
    public const double DefaultBudgetMs = 2.0;

    /// <summary>
    /// Weight of the newest frame in the running average. 1/32 is roughly a half-second memory at
    /// 60 fps: long enough that one expensive sweep does not shut the sweeps off, short enough that
    /// leaving a crowded area re-opens them within a second.
    /// </summary>
    public const double AverageWeight = 1.0 / 32.0;

    /// <summary>
    /// What <see cref="RunningAverageMs"/> starts at, as a fraction of the budget. A constant, not
    /// a measurement: the first frame of a capture is the most expensive one it will ever run
    /// (baseline plus a full sweep) and seeding with it blacked out the start of every window
    /// (R28 finding 8). Half a budget is "affordable so far, no evidence otherwise".
    /// </summary>
    public const double SeedFractionOfBudget = 0.5;

    /// <summary>
    /// Hard floor on the scene sweep: however over budget the frames are, one sweep runs at least
    /// this often (two seconds at 60 fps). This is what makes the budget a throttle rather than an
    /// off switch (R28 finding 5).
    /// </summary>
    public const int MinScanEveryFrames = CaptureSampling.MaxSampleInterval;

    /// <summary>The same floor for the pool walk, which is an order of magnitude cheaper.</summary>
    public const int MinWalkEveryFrames = CaptureSampling.MaxTierInterval;

    /// <summary>
    /// Fraction of the due sweeps a window may skip before its report is flagged truncated
    /// (R28 finding 7). A quarter is generous: at that point a third of the effect's lifetime is
    /// unobserved.
    /// </summary>
    public const double MaxSweepSkipRatio = 0.25;

    /// <summary>
    /// How many budgets one scene sweep may spend before it stops early. R28 finding 28.22: there
    /// was no deadline anywhere inside the sweep, so once it started it ran to completion — an
    /// uninterruptible 20 to 40 ms hitch. Ten budgets is 20 ms, which is one dropped frame, not a
    /// stall, and the sweep that hit it says so in the report.
    /// </summary>
    public const double ScanDeadlineBudgets = 10.0;

    public CaptureBudget(double budgetMs = DefaultBudgetMs)
    {
        BudgetMs = budgetMs <= 0 ? DefaultBudgetMs : budgetMs;
        RunningAverageMs = BudgetMs * SeedFractionOfBudget;
    }

    public double BudgetMs { get; }
    public int Frames { get; private set; }
    public int SkippedWalks { get; private set; }
    public int SkippedScans { get; private set; }
    public int SkippedComparisons { get; private set; }
    public int SkippedEntityDiffs { get; private set; }
    public int OverBudgetFrames { get; private set; }
    public double TotalMs { get; private set; }
    public double MaxMs { get; private set; }

    /// <summary>Scene sweeps that actually ran, and what they cost.</summary>
    public int Scans { get; private set; }
    public double LastScanMs { get; private set; }
    public double AverageScanMs { get; private set; }
    public double MaxScanMs { get; private set; }

    /// <summary>Tier 1 (the entity diff), measured separately so it can throttle itself.</summary>
    public int EntityDiffs { get; private set; }
    public double AverageEntityDiffMs { get; private set; }
    public double MaxEntityDiffMs { get; private set; }

    /// <summary>Tier 2 (the pool walk), measured separately so it has its own budget.</summary>
    public int Walks { get; private set; }
    public double AverageWalkMs { get; private set; }
    public double MaxWalkMs { get; private set; }

    /// <summary>Frames on which a sweep / walk was due, and how long since one last ran.</summary>
    public int SweepsDue { get; private set; }
    public int FramesSinceScan { get; private set; }
    public int FramesSinceWalk { get; private set; }
    public int ForcedScans { get; private set; }
    public int ForcedWalks { get; private set; }

    /// <summary>Exponentially weighted whole-frame cost — the gate's input.</summary>
    public double RunningAverageMs { get; private set; }

    public double AverageMs => Frames == 0 ? 0 : TotalMs / Frames;

    /// <summary>Milliseconds one scene sweep may spend before it stops early.</summary>
    public double ScanDeadlineMs => BudgetMs * ScanDeadlineBudgets;

    /// <summary>
    /// Frames between scene sweeps right now: the default until a sweep has been measured, then
    /// wide enough that <see cref="AverageScanMs"/> amortizes to <see cref="BudgetMs"/> per frame.
    /// </summary>
    public int SampleInterval => CaptureSampling.IntervalFor(AverageScanMs, BudgetMs);

    /// <summary>Frames between entity diffs: 1 while it fits, wider once it does not.</summary>
    public int EntityDiffInterval =>
        CaptureSampling.TierIntervalFor(AverageEntityDiffMs, BudgetMs, CaptureSampling.MaxTierInterval);

    /// <summary>Frames between pool walks, from the walk's own measured cost.</summary>
    public int WalkInterval =>
        CaptureSampling.TierIntervalFor(AverageWalkMs, BudgetMs, CaptureSampling.MaxTierInterval);

    /// <summary>
    /// Share of the sweeps this window wanted that it did not get: skipped over skipped-plus-run.
    /// Measured against <see cref="Scans"/> rather than against <see cref="SweepsDue"/> so it is the
    /// same number whichever <c>Record</c> overload the caller used.
    /// </summary>
    public double SweepSkipRatio =>
        (SkippedScans + Scans) <= 0 ? 0 : (double)SkippedScans / (SkippedScans + Scans);

    /// <summary>True when enough sweeps were skipped that the report must call itself truncated.</summary>
    public bool SweepSkipRatioExceeded => SkippedScans > 0 && SweepSkipRatio >= MaxSweepSkipRatio;

    /// <summary>Decision point, taken after the entity diff and before the particle-parent walk.</summary>
    public bool ShouldSkipWalk(double elapsedMsSoFar) => elapsedMsSoFar >= BudgetMs;

    /// <summary>
    /// Decision point for the expensive half. True when this frame has already spent the budget OR
    /// when the running average says the recent frames are over it — the second half is what
    /// actually throttles a sweep whose own cost dwarfs everything measured before it.
    /// </summary>
    public bool ShouldSkipScan(double elapsedMsSoFar) =>
        elapsedMsSoFar >= BudgetMs || RunningAverageMs >= BudgetMs;

    /// <summary>
    /// Same gate as the sweep: the "changed" comparison rides on the sweep pass, because it needs
    /// the objects the sweep re-finds.
    /// </summary>
    public bool ShouldSkipComparison(double elapsedMsSoFar) => ShouldSkipScan(elapsedMsSoFar);

    /// <summary>
    /// What this frame may do, from the tier intervals, the running average and the floors. Pure:
    /// it reads state and allocates nothing, so the frame path calls it before doing any work.
    /// </summary>
    public CaptureTierPlan PlanFrame(int frameIndex)
    {
        if (frameIndex < 0) frameIndex = 0;
        bool entityDue = frameIndex % EntityDiffInterval == 0;
        bool walkDue = frameIndex % WalkInterval == 0;
        bool scanDue = CaptureSampling.ShouldSample(frameIndex, SampleInterval);

        bool overBudget = RunningAverageMs >= BudgetMs;
        bool forceWalk = FramesSinceWalk >= MinWalkEveryFrames;
        bool forceScan = FramesSinceScan >= MinScanEveryFrames;

        bool runWalk = forceWalk || (walkDue && !overBudget);
        bool runScan = forceScan || (scanDue && !overBudget);

        return new CaptureTierPlan(
            runEntityDiff: entityDue,
            runWalk: runWalk,
            runScan: runScan,
            entityDiffDue: entityDue,
            walkDue: walkDue,
            scanDue: scanDue,
            forcedWalk: forceWalk && !(walkDue && !overBudget),
            forcedScan: forceScan && !(scanDue && !overBudget));
    }

    /// <summary>
    /// Narrows a plan once the frame's cheap part has been measured. <paramref name="elapsedMsSoFar"/>
    /// must <b>exclude</b> the entity diff: charging tier 1 to the tiers below it is R28 finding 5.
    /// A forced tier is never narrowed away — the floor outranks the in-frame check.
    /// </summary>
    public CaptureTierPlan Confirm(CaptureTierPlan plan, double elapsedMsSoFar)
    {
        bool runWalk = plan.RunWalk && (plan.ForcedWalk || !ShouldSkipWalk(elapsedMsSoFar));
        bool runScan = plan.RunScan && (plan.ForcedScan || elapsedMsSoFar < BudgetMs);
        return new CaptureTierPlan(
            plan.RunEntityDiff, runWalk, runScan,
            plan.EntityDiffDue, plan.WalkDue, plan.ScanDue,
            plan.ForcedWalk, plan.ForcedScan);
    }

    /// <summary>One measured scene sweep.</summary>
    public void RecordScan(double scanMs)
    {
        if (double.IsNaN(scanMs) || scanMs < 0) return;
        Scans++;
        LastScanMs = scanMs;
        if (scanMs > MaxScanMs) MaxScanMs = scanMs;
        AverageScanMs = Scans == 1 ? scanMs : AverageScanMs + (AverageWeight * (scanMs - AverageScanMs));
    }

    /// <summary>One measured entity diff (tier 1).</summary>
    public void RecordEntityDiff(double ms)
    {
        if (double.IsNaN(ms) || ms < 0) return;
        EntityDiffs++;
        if (ms > MaxEntityDiffMs) MaxEntityDiffMs = ms;
        AverageEntityDiffMs = EntityDiffs == 1 ? ms : AverageEntityDiffMs + (AverageWeight * (ms - AverageEntityDiffMs));
    }

    /// <summary>One measured pool walk (tier 2).</summary>
    public void RecordWalk(double ms)
    {
        if (double.IsNaN(ms) || ms < 0) return;
        Walks++;
        if (ms > MaxWalkMs) MaxWalkMs = ms;
        AverageWalkMs = Walks == 1 ? ms : AverageWalkMs + (AverageWeight * (ms - AverageWalkMs));
    }

    /// <summary>One finished frame, described by the plan it actually executed.</summary>
    public void Record(double frameMs, CaptureTierPlan plan)
    {
        if (plan.ScanDue) SweepsDue++;
        if (plan.ForcedScan && plan.RunScan) ForcedScans++;
        if (plan.ForcedWalk && plan.RunWalk) ForcedWalks++;
        if (plan.SkippedEntityDiff) SkippedEntityDiffs++;
        FramesSinceScan = plan.RunScan ? 0 : FramesSinceScan + 1;
        FramesSinceWalk = plan.RunWalk ? 0 : FramesSinceWalk + 1;
        RecordCore(frameMs, plan.SkippedWalk, plan.SkippedScan, plan.SkippedScan);
    }

    /// <summary>
    /// One finished frame described by its skips. Kept for the callers (and the tests) that reason
    /// about skips directly rather than about a plan; a skipped scan implies a due one.
    /// </summary>
    public void Record(double frameMs, bool skippedWalk, bool skippedScan = false, bool skippedComparison = false)
    {
        if (skippedScan) SweepsDue++;
        FramesSinceScan = skippedScan ? FramesSinceScan + 1 : 0;
        FramesSinceWalk = skippedWalk ? FramesSinceWalk + 1 : 0;
        RecordCore(frameMs, skippedWalk, skippedScan, skippedComparison);
    }

    private void RecordCore(double frameMs, bool skippedWalk, bool skippedScan, bool skippedComparison)
    {
        Frames++;
        TotalMs += frameMs;
        if (frameMs > MaxMs) MaxMs = frameMs;
        if (frameMs >= BudgetMs) OverBudgetFrames++;
        if (skippedWalk) SkippedWalks++;
        if (skippedScan) SkippedScans++;
        if (skippedComparison) SkippedComparisons++;
        RunningAverageMs += AverageWeight * (frameMs - RunningAverageMs);
    }

    public Dictionary<string, object?> ToJson() => new()
    {
        ["budgetMs"] = BudgetMs,
        ["frames"] = Frames,
        ["skippedGameObjectWalks"] = SkippedWalks,
        ["skippedSceneScans"] = SkippedScans,
        ["skippedComparisons"] = SkippedComparisons,
        ["skippedEntityDiffs"] = SkippedEntityDiffs,
        ["overBudgetFrames"] = OverBudgetFrames,
        ["averageMs"] = Round(AverageMs),
        ["runningAverageMs"] = Round(RunningAverageMs),
        ["maxMs"] = Round(MaxMs),
        ["totalMs"] = Round(TotalMs),
        ["scans"] = Scans,
        ["averageScanMs"] = Round(AverageScanMs),
        ["maxScanMs"] = Round(MaxScanMs),
        ["sampleInterval"] = SampleInterval,
        ["entityDiffs"] = EntityDiffs,
        ["averageEntityDiffMs"] = Round(AverageEntityDiffMs),
        ["maxEntityDiffMs"] = Round(MaxEntityDiffMs),
        ["entityDiffInterval"] = EntityDiffInterval,
        ["walks"] = Walks,
        ["averageWalkMs"] = Round(AverageWalkMs),
        ["maxWalkMs"] = Round(MaxWalkMs),
        ["walkInterval"] = WalkInterval,
        ["sweepsDue"] = SweepsDue,
        ["sweepSkipRatio"] = Round(SweepSkipRatio),
        ["sweepSkipRatioExceeded"] = SweepSkipRatioExceeded,
        ["forcedScans"] = ForcedScans,
        ["forcedWalks"] = ForcedWalks,
    };

    internal static double Round(double value) => Math.Round(value, 3, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Deterministic truncation for the scene sweeps, the GameObject counterpart of
/// <see cref="CaptureEntitySelection"/>.
///
/// <para>R28 finding 9: the sweeps clipped raw <c>FindObjectsOfType</c> order at 400. That order is
/// unspecified and shifts as objects are created and destroyed, so an object drifted in and out of
/// the compare set, was swept as "gone", came back, was re-tracked with a fresh baseline, and its
/// next activation was reported as a first activation with a full description read — hundreds of
/// phantom rows in a busy scene. Sorting by instance id before clipping makes the kept set a pure
/// function of the available set, exactly as the entity side already did.</para>
/// </summary>
internal static class CaptureScanSelection
{
    /// <summary>
    /// Sorts <paramref name="ids"/> (carrying <paramref name="slots"/> along) ascending and returns
    /// how many of them the sweep keeps.
    /// </summary>
    public static int SortAndClip(int[] ids, int[] slots, int count, int max)
    {
        if (ids == null || slots == null) return 0;
        if (count <= 0 || max <= 0) return 0;
        if (count > ids.Length) count = ids.Length;
        if (count > slots.Length) count = slots.Length;
        Array.Sort(ids, slots, 0, count);
        return Math.Min(count, max);
    }

    /// <summary>How many objects the sweep could not look at.</summary>
    public static int Overflow(int found, int max) => Math.Max(0, found - Math.Max(0, max));
}

/// <summary>
/// How the walk of the sequencer's pool parent spends its node budget, and whether the pass it
/// produced is complete enough to diff against.
///
/// <para>R28 finding 9: one shared depth-first budget of 256 nodes meant which <b>direct</b>
/// children got visited depended on how many grandchildren the earlier siblings happened to have —
/// a quantity that changes every frame as the pool takes and returns clones. An object that never
/// moved slid past the budget and <c>DiffPoolChildren</c> reported it as "taken from the pool",
/// a false positive on the single strongest signal the op has. Two fixes: the depth-0 sibling
/// enumeration gets its own budget, so the child set is complete far more often; and a pass that
/// ran out of either budget is marked incomplete and is not diffed at all.</para>
/// </summary>
internal sealed class CaptureWalkBudget
{
    /// <summary>Direct children of the pool parent recorded per pass.</summary>
    public const int MaxRootNodes = 256;

    /// <summary>Nodes at any depth visited per pass, root children included.</summary>
    public const int MaxNodes = 512;

    /// <summary>Depth the walk descends to.</summary>
    public const int MaxDepth = 4;

    public int Nodes { get; private set; }
    public int RootNodes { get; private set; }
    public bool HitNodeCap { get; private set; }
    public bool HitRootCap { get; private set; }

    /// <summary>True when neither budget ran out, so the child set is the whole child set.</summary>
    public bool Complete => !HitNodeCap && !HitRootCap;

    public void Reset()
    {
        Nodes = 0;
        RootNodes = 0;
        HitNodeCap = false;
        HitRootCap = false;
    }

    /// <summary>Books one node about to be visited. False when this pass has no budget left.</summary>
    public bool TryVisit(int depth)
    {
        if (Nodes >= MaxNodes) { HitNodeCap = true; return false; }
        if (depth == 0 && RootNodes >= MaxRootNodes) { HitRootCap = true; return false; }
        Nodes++;
        if (depth == 0) RootNodes++;
        return true;
    }

    /// <summary>True while the walk may descend past <paramref name="depth"/>.</summary>
    public bool CanDescend(int depth) => depth < MaxDepth && Nodes < MaxNodes;

    public Dictionary<string, object?> ToJson() => new()
    {
        ["nodes"] = Nodes,
        ["rootNodes"] = RootNodes,
        ["maxNodes"] = MaxNodes,
        ["maxRootNodes"] = MaxRootNodes,
        ["complete"] = Complete,
    };
}

// The rows below are filled by CaptureOps, which is not part of the test compilation (it needs the
// game assemblies). Inside Progenitor.Tests every writer is therefore invisible and the compiler
// reports each field as never assigned; the model is deliberately plain data, so silence CS0649
// here rather than turning fields into properties nothing else needs.
#pragma warning disable CS0649

/// <summary>A world-space point in the report. Kept as three floats so the model stays game-free.</summary>
internal readonly struct CaptureVec3
{
    public CaptureVec3(float x, float y, float z) { X = x; Y = y; Z = z; }

    public readonly float X;
    public readonly float Y;
    public readonly float Z;

    public Dictionary<string, object?> ToJson() => new() { ["x"] = X, ["y"] = Y, ["z"] = Z };

    /// <summary>Stable text form; the endpoint tracks compare these strings, never floats.</summary>
    public override string ToString() =>
        "(" + X.ToString("0.###", CultureInfo.InvariantCulture)
            + "," + Y.ToString("0.###", CultureInfo.InvariantCulture)
            + "," + Z.ToString("0.###", CultureInfo.InvariantCulture) + ")";
}

/// <summary>One entity that appeared or disappeared while the capture was running.</summary>
internal sealed class CaptureEntityRow
{
    public string Entity = "";
    public int Frame;
    public double AtSeconds;
    public int? PrefabHash;
    public string? PrefabName;
    public List<string> Components = new();

    /// <summary>Component simple name → field name → value (numbers, strings, nested dictionaries).</summary>
    public Dictionary<string, Dictionary<string, object?>> Endpoints = new(StringComparer.Ordinal);

    /// <summary>Non-zero sequence guid hashes read out of <see cref="Endpoints"/> at record time.</summary>
    public List<int> SequenceGuids = new();

    public Dictionary<string, object?> ToJson()
    {
        var endpoints = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in Endpoints) endpoints[pair.Key] = pair.Value;
        return new Dictionary<string, object?>
        {
            ["entity"] = Entity,
            ["frame"] = Frame,
            ["atSeconds"] = CaptureBudget.Round(AtSeconds),
            ["prefab"] = PrefabHash.HasValue
                ? new Dictionary<string, object?> { ["hash"] = PrefabHash.Value, ["name"] = PrefabName }
                : null,
            ["components"] = Components,
            ["endpoints"] = endpoints,
            ["sequenceGuids"] = SequenceGuids,
        };
    }
}

/// <summary>The drawable state of one <c>LineRenderer</c> at the moment it was first seen.</summary>
internal sealed class CaptureLineInfo
{
    public int PositionCount;
    public List<CaptureVec3> Positions = new();
    public float StartWidth;
    public float EndWidth;
    public string? StartColor;
    public string? EndColor;
    public string? Shader;
    public bool UseWorldSpace;

    public Dictionary<string, object?> ToJson()
    {
        var pts = new List<Dictionary<string, object?>>(Positions.Count);
        foreach (var p in Positions) pts.Add(p.ToJson());
        return new Dictionary<string, object?>
        {
            ["positionCount"] = PositionCount,
            ["positions"] = pts,
            ["startWidth"] = StartWidth,
            ["endWidth"] = EndWidth,
            ["startColor"] = StartColor,
            ["endColor"] = EndColor,
            ["shader"] = Shader,
            ["useWorldSpace"] = UseWorldSpace,
        };
    }
}

internal sealed class CaptureParticleInfo
{
    public bool IsPlaying;
    public int ParticleCount;

    public Dictionary<string, object?> ToJson() => new()
    {
        ["isPlaying"] = IsPlaying,
        ["particleCount"] = ParticleCount,
    };
}

/// <summary>One GameObject that appeared under the sequencer's particle parent (or as a new line).</summary>
internal sealed class CaptureObjectRow
{
    public int InstanceId;
    public string Name = "";
    public bool Active;
    public string? Parent;

    /// <summary><c>particleParent</c>, <c>lineRenderer</c>, or <c>hybrid</c> — which walk found it.</summary>
    public string Source = "";
    public int Frame;
    public double AtSeconds;
    public List<string> Components = new();
    public CaptureLineInfo? Line;
    public CaptureParticleInfo? Particle;
    public List<CaptureVec3>? HybridPositions;

    public Dictionary<string, object?> ToJson()
    {
        List<Dictionary<string, object?>>? hybrid = null;
        if (HybridPositions != null)
        {
            hybrid = new List<Dictionary<string, object?>>(HybridPositions.Count);
            foreach (var p in HybridPositions) hybrid.Add(p.ToJson());
        }

        return new Dictionary<string, object?>
        {
            ["instanceId"] = InstanceId,
            ["name"] = Name,
            ["active"] = Active,
            ["parent"] = Parent,
            ["source"] = Source,
            ["frame"] = Frame,
            ["atSeconds"] = CaptureBudget.Round(AtSeconds),
            ["components"] = Components,
            ["line"] = Line?.ToJson(),
            ["particle"] = Particle?.ToJson(),
            ["hybridPositions"] = hybrid,
        };
    }
}

/// <summary>The transitions the "changed" detector records. Kinds are a closed set.</summary>
internal static class CaptureChangeKind
{
    public const string Activated = "activated";
    public const string Deactivated = "deactivated";
    public const string Reparented = "reparented";
    public const string LineMoved = "lineMoved";
    public const string ParticleStarted = "particleStarted";
    public const string ParticleStopped = "particleStopped";
}

/// <summary>
/// One transition of one tracked GameObject. <see cref="Before"/> and <see cref="After"/> are the
/// formatted values of whatever the kind is about (the active flags, the parent, the positions,
/// the particle state) — they are the only strings the detector allocates, and only on a
/// transition.
///
/// <para><see cref="Description"/> is the full read of the object at the moment it mattered: it is
/// filled for the first activation of an object that was inactive in the baseline (the pooled-clone
/// case the whole detector exists for — evidence 137) and for a re-parent, where the new parent's
/// name is otherwise unknown because the per-frame pass never reads names.</para>
/// </summary>
internal sealed class CaptureChangeRow
{
    public int InstanceId;
    public string Name = "";
    public int Frame;
    public double AtSeconds;
    public string Kind = "";
    public string? Before;
    public string? After;
    public CaptureObjectRow? Description;

    public Dictionary<string, object?> ToJson() => new()
    {
        ["instanceId"] = InstanceId,
        ["name"] = Name,
        ["frame"] = Frame,
        ["atSeconds"] = CaptureBudget.Round(AtSeconds),
        ["kind"] = Kind,
        ["before"] = Before,
        ["after"] = After,
        ["description"] = Description?.ToJson(),
    };
}

/// <summary>
/// The snapshot of one GameObject the detector compares against. Two instances of this class are
/// used per tracked object at most: the one the tracker keeps, and a single scratch instance the
/// live side refills each pass — hence the reused position lists and <see cref="Reset"/> rather
/// than a fresh object per frame.
///
/// <para>Nothing here is a Unity or Il2Cpp type: the live half reads the GameObject and writes
/// plain fields, so the whole comparison compiles into the test assembly (docs/modules.md
/// collectible-ALC rules: no wrapper survives a frame).</para>
/// </summary>
internal sealed class CaptureObjectState
{
    public int InstanceId;
    public string Name = "";
    public bool ActiveSelf;
    public bool ActiveInHierarchy;

    /// <summary>0 means "no parent" (a scene root).</summary>
    public int ParentInstanceId;
    public string? ParentName;

    public bool HasLine;
    public int LinePositionCount;
    public float StartWidth;
    public float EndWidth;
    public string? StartColor;
    public string? EndColor;
    public string? Shader;
    public bool UseWorldSpace;
    public readonly List<CaptureVec3> LinePositions = new();

    public bool HasParticle;
    public bool ParticleIsPlaying;
    public int ParticleCount;

    public bool HasHybrid;
    public readonly List<CaptureVec3> HybridPositions = new();

    // ---- bookkeeping, tracked copies only ----

    /// <summary>Was the object already visible when the capture took its baseline?</summary>
    public bool BaselineActive;

    /// <summary>Frame the tracker first saw this object; part of its identity (see the tracker).</summary>
    public int FirstFrame;

    /// <summary>
    /// Parent instance id when this identity was established. The third part of the identity, with
    /// the instance id and <see cref="FirstFrame"/>: a pooled clone's name is shared by every clone
    /// of the same prefab, so name plus id could not tell a recycled id apart from its predecessor
    /// (R28 finding 6). Where the object was parked is the discriminator the pool cannot forge.
    /// </summary>
    public int IdentityParentId;

    public int Activations;

    /// <summary>Frame index of the last comparison pass that saw it; -1 until the first.</summary>
    public int LastPass = -1;

    /// <summary>
    /// Ordinal of the last tracker pass that saw it (<c>CaptureObjectTracker.Passes</c>), set when
    /// it is first tracked and on every observation. The gap between this and the current pass is
    /// what tells an ordinary return to pool from instance-id reuse
    /// (<see cref="CaptureObjectIdentity.LooksRecycled"/>, R29 finding 20). Frame indices cannot do
    /// that job: the capture budget skips sweeps, so a frame gap says nothing about observation.
    /// </summary>
    public int LastPassOrdinal;

    /// <summary>Clears every field so the instance can be refilled as scratch, without allocating.</summary>
    public void Reset()
    {
        InstanceId = 0;
        Name = "";
        ActiveSelf = false;
        ActiveInHierarchy = false;
        ParentInstanceId = 0;
        ParentName = null;
        HasLine = false;
        LinePositionCount = 0;
        StartWidth = 0;
        EndWidth = 0;
        StartColor = null;
        EndColor = null;
        Shader = null;
        UseWorldSpace = false;
        LinePositions.Clear();
        HasParticle = false;
        ParticleIsPlaying = false;
        ParticleCount = 0;
        HasHybrid = false;
        HybridPositions.Clear();
    }

    /// <summary>
    /// Copies the observed half of <paramref name="other"/> over this one, reusing the position
    /// lists. Bookkeeping (<see cref="BaselineActive"/>, <see cref="FirstFrame"/>,
    /// <see cref="Activations"/>, <see cref="LastPass"/>) belongs to the tracked copy and is left
    /// alone. Names and the colour/shader block are only read on a full pass, so an empty value
    /// there means "not read this time", never "changed to empty".
    /// </summary>
    public void CopyStateFrom(CaptureObjectState other)
    {
        InstanceId = other.InstanceId;
        if (!string.IsNullOrEmpty(other.Name)) Name = other.Name;
        ActiveSelf = other.ActiveSelf;
        ActiveInHierarchy = other.ActiveInHierarchy;
        ParentInstanceId = other.ParentInstanceId;
        if (other.ParentInstanceId == 0) ParentName = null;
        else if (other.ParentName != null) ParentName = other.ParentName;

        HasLine = other.HasLine;
        LinePositionCount = other.LinePositionCount;
        StartWidth = other.StartWidth;
        EndWidth = other.EndWidth;
        if (other.StartColor != null) StartColor = other.StartColor;
        if (other.EndColor != null) EndColor = other.EndColor;
        if (other.Shader != null) Shader = other.Shader;
        UseWorldSpace = other.UseWorldSpace;
        CopyPositions(other.LinePositions, LinePositions);

        HasParticle = other.HasParticle;
        ParticleIsPlaying = other.ParticleIsPlaying;
        ParticleCount = other.ParticleCount;

        HasHybrid = other.HasHybrid;
        CopyPositions(other.HybridPositions, HybridPositions);
    }

    /// <summary>
    /// Starts a fresh identity on the same instance id: the pool handed this id to a new life
    /// (R28 finding 6). The snapshot fields keep whatever was just read; only the bookkeeping that
    /// belongs to the previous life is reset, so the very next activation is reported as a first
    /// activation and asks for its full description instead of being suppressed by an inherited
    /// <see cref="Activations"/> count.
    /// </summary>
    public void Rekey(string name, int parentInstanceId, int frameIndex)
    {
        if (!string.IsNullOrEmpty(name)) Name = name;
        IdentityParentId = parentInstanceId;
        FirstFrame = frameIndex;
        Activations = 0;
        BaselineActive = false;
    }

    /// <summary>Identity of this tracked object, for the report and for diagnostics.</summary>
    public string DescribeIdentity() =>
        CaptureObjectIdentity.Describe(InstanceId, IdentityParentId, FirstFrame);

    private static void CopyPositions(List<CaptureVec3> from, List<CaptureVec3> into)
    {
        into.Clear();
        for (int i = 0; i < from.Count; i++) into.Add(from[i]);
    }

    /// <summary>True when any sampled point differs by more than the series epsilon.</summary>
    public bool PositionsDifferFrom(CaptureObjectState other) =>
        LinePositionCount != other.LinePositionCount
        || Differ(LinePositions, other.LinePositions)
        || Differ(HybridPositions, other.HybridPositions);

    private static bool Differ(List<CaptureVec3> a, List<CaptureVec3> b)
    {
        if (a.Count != b.Count) return true;
        const float e = CaptureSampleSeries.MoveEpsilon;
        for (int i = 0; i < a.Count; i++)
        {
            if (Math.Abs(a[i].X - b[i].X) > e) return true;
            if (Math.Abs(a[i].Y - b[i].Y) > e) return true;
            if (Math.Abs(a[i].Z - b[i].Z) > e) return true;
        }
        return false;
    }

    /// <summary>The positions this state would contribute to a sample series.</summary>
    public List<CaptureVec3> SamplePositions => LinePositions.Count > 0 ? LinePositions : HybridPositions;

    // ---- formatting: called only when a transition is being recorded ----

    public string DescribeActive() =>
        "activeSelf=" + (ActiveSelf ? "true" : "false")
        + ",activeInHierarchy=" + (ActiveInHierarchy ? "true" : "false");

    public string DescribeParent() =>
        ParentInstanceId == 0 ? "<root>" : (ParentName ?? "?") + " #" + ParentInstanceId.ToString(CultureInfo.InvariantCulture);

    public string DescribeParticle() =>
        "isPlaying=" + (ParticleIsPlaying ? "true" : "false")
        + ",particleCount=" + ParticleCount.ToString(CultureInfo.InvariantCulture);

    public string DescribePositions()
    {
        var sb = new System.Text.StringBuilder();
        if (HasLine)
        {
            sb.Append("line[").Append(LinePositionCount.ToString(CultureInfo.InvariantCulture)).Append(']');
            Append(sb, LinePositions);
        }
        if (HasHybrid)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("hybrid[").Append(HybridPositions.Count.ToString(CultureInfo.InvariantCulture)).Append(']');
            Append(sb, HybridPositions);
        }
        return sb.Length == 0 ? "<no positions>" : sb.ToString();
    }

    private static void Append(System.Text.StringBuilder sb, List<CaptureVec3> points)
    {
        for (int i = 0; i < points.Count; i++)
        {
            sb.Append(i == 0 ? "=" : "|");
            sb.Append(points[i].ToString());
        }
    }
}

/// <summary>
/// Whether two observations of the same instance id are the same object.
///
/// <para>R28 finding 6: the first version compared names, and "id + name" degenerates to "id alone"
/// for the objects the whole detector exists for — pooled clones share a name by construction
/// (<c>Beam_Cardinal(Clone)</c>). When Unity handed a destroyed clone's instance id to a new one,
/// the new object inherited the dead object's entire snapshot: a spurious <c>deactivated</c> row
/// for an object never deactivated, a spurious <c>reparented</c> row naming a destroyed object's
/// parent, an inherited <c>Activations</c> count that suppressed the one full-description row the
/// op exists to produce, and a sample series whose two objects' positions interleaved so
/// <c>moved</c> was permanently true.</para>
///
/// <para>Identity is therefore instance id <b>plus the parent it was first seen under plus the
/// frame it was first seen on</b>, and two transitions are treated as identity events rather than
/// as state changes:</para>
/// <list type="bullet">
/// <item><see cref="LooksRecycled"/> — a different name, or a deactivate-and-re-parent
/// <b>across a gap in observation</b>. See that method for why the gap is load-bearing (R29
/// finding 20). The entry is retired and re-tracked.</item>
/// <item><see cref="ReactivatedUnderNewParent"/> — a parked object woke up somewhere other than
/// where this identity was established. Same id, new life: the entry is re-keyed
/// (<see cref="CaptureObjectState.Rekey"/>) so the activation is reported as a first one.</item>
/// </list>
/// </summary>
internal static class CaptureObjectIdentity
{
    /// <summary>Stable text form of an identity, for rows and diagnostics.</summary>
    public static string Describe(int instanceId, int parentInstanceId, int firstFrame) =>
        "#" + instanceId.ToString(CultureInfo.InvariantCulture)
        + "@" + parentInstanceId.ToString(CultureInfo.InvariantCulture)
        + "f" + firstFrame.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Comparison passes an entry may go unseen and still be believed to be the same object.
    ///
    /// <para>Counted in tracker passes, not frames: the capture budget skips sweeps, so a frame gap
    /// says nothing about whether the object was observed. Four, because
    /// <c>CaptureBudget.MaxSweepSkipRatio</c> tolerates one pass in four being dropped under budget
    /// pressure, so a live object can plausibly miss a pass or two; four consecutive misses is well
    /// outside that and is the shape of "this instance id was not there".</para>
    /// </summary>
    public const int RecycleUnseenPasses = 4;

    /// <summary>
    /// True when the known entry and this observation cannot be the same object.
    ///
    /// <para>A different non-empty name is decisive on its own. The other signature — deactivated
    /// <b>and</b> re-parented in one pass — is only decisive across a gap in observation.</para>
    ///
    /// <para><b>R29 finding 20.</b> The previous version treated any deactivate-and-re-parent as
    /// "cannot be the same object". But deactivate-and-re-parent in the same frame <i>is</i> the
    /// standard Unity return-to-pool: <c>SetActive(false)</c> plus
    /// <c>transform.SetParent(poolRoot)</c>. A pooled clone completing one normal round trip
    /// therefore tripped both predicates — once on return here (new identity) and once on hand-out
    /// in <see cref="ReactivatedUnderNewParent"/> (new identity again) — so a GameObject that cycled
    /// three times through the pool appeared in the report as up to seven identities with fragmented
    /// timelines, and burned a <see cref="CaptureSampleRegistry"/> slot each time, whose rotation
    /// counter then reported churn that was an artifact of the identity rule rather than of the
    /// scene. That is precisely the object class the detector exists for.</para>
    ///
    /// <para>So a return to pool observed on the very next pass is <b>the same identity going
    /// quiet</b>: the deactivation and the re-parent are recorded against it, and
    /// <see cref="ReactivatedUnderNewParent"/> mints the new identity when the pool hands it out.
    /// One identity per pool cycle instead of two. Id reuse — the R28 finding 6 case, where Unity
    /// destroys a clone and gives its instance id to a different object — is still caught, because
    /// the tracker cannot have been comparing the id while it was dead: there is a pass gap.</para>
    /// </summary>
    /// <param name="unseenPasses">
    /// Tracker passes since this entry was last compared. 0 or 1 is continuous observation.
    /// </param>
    public static bool LooksRecycled(
        string? knownName, string? scratchName,
        bool knownActive, bool scratchActive,
        int knownParent, int scratchParent,
        int unseenPasses,
        int maxUnseenPasses = RecycleUnseenPasses)
    {
        if (!string.IsNullOrEmpty(knownName)
            && !string.IsNullOrEmpty(scratchName)
            && !string.Equals(knownName, scratchName, StringComparison.Ordinal))
        {
            return true;
        }

        if (!knownActive || scratchActive) return false;      // not a deactivation transition
        if (knownParent == scratchParent) return false;       // parked in place: the same object
        return unseenPasses > maxUnseenPasses;
    }

    /// <summary>
    /// True when a parked object woke up under a parent that is not the one its identity was
    /// established under — a pooled clone being handed out, which starts a new life on the same id.
    /// </summary>
    public static bool ReactivatedUnderNewParent(
        bool knownActive, bool scratchActive, int identityParent, int scratchParent) =>
        !knownActive && scratchActive && identityParent != scratchParent;
}

/// <summary>
/// Which GameObjects the sweeps take position samples from, bounded and <b>rotating</b>.
///
/// <para>R28 finding 18: the first version was a plain dictionary with an insert guarded by
/// <c>Count &lt; 40</c> and a removal only on the id-reuse path. In a scene that spawns and
/// destroys beams the forty slots filled with dead ids in the first seconds and not one line was
/// sampled for the rest of the window — the <c>lineSamples</c> section and the <c>moved</c> verdict,
/// the question the op exists to answer, were empty and nothing said why.</para>
///
/// <para>Now: registering into a full table evicts the least recently seen entry rather than
/// failing, every eviction is counted, and the fact that the table ever filled is a sticky flag in
/// the report. Saturation is a reported condition, not an invisible stop.</para>
/// </summary>
internal sealed class CaptureSampleRegistry
{
    public const int MaxEntries = CaptureSampling.MaxSeries;

    private readonly Dictionary<int, Entry> _byId = new();

    public int Count => _byId.Count;

    /// <summary>Entries evicted to make room for a newer one.</summary>
    public int Rotations { get; private set; }

    /// <summary>Registrations that arrived while the table was full.</summary>
    public int SaturatedRegistrations { get; private set; }

    /// <summary>Sticky: the table filled at least once during this capture.</summary>
    public bool Saturated { get; private set; }

    public void Clear()
    {
        _byId.Clear();
        Rotations = 0;
        SaturatedRegistrations = 0;
        Saturated = false;
    }

    public bool TryGet(int instanceId, out string name)
    {
        if (_byId.TryGetValue(instanceId, out var entry)) { name = entry.Name; return true; }
        name = "";
        return false;
    }

    public bool Remove(int instanceId) => _byId.Remove(instanceId);

    /// <summary>Marks an entry as seen on this pass, so it is not the one rotated out next.</summary>
    public void Touch(int instanceId, int frameIndex)
    {
        if (!_byId.TryGetValue(instanceId, out var entry)) return;
        if (frameIndex <= entry.LastFrame) return;
        entry.LastFrame = frameIndex;
    }

    /// <summary>
    /// Registers an object for sampling. When the table is full the least recently seen entry is
    /// evicted, unless it was seen on this very pass — a table where every entry is live this frame
    /// is genuinely saturated and the registration is refused (and counted).
    /// </summary>
    public bool TryRegister(int instanceId, string name, int frameIndex)
    {
        if (instanceId == 0) return false;
        if (_byId.TryGetValue(instanceId, out var existing))
        {
            existing.Name = name ?? existing.Name;
            existing.LastFrame = frameIndex;
            return true;
        }

        if (_byId.Count >= MaxEntries)
        {
            Saturated = true;
            SaturatedRegistrations++;
            if (!TryEvictOldest(frameIndex)) return false;
            Rotations++;
        }

        _byId[instanceId] = new Entry { Name = name ?? "", LastFrame = frameIndex };
        return true;
    }

    private bool TryEvictOldest(int frameIndex)
    {
        int victim = 0;
        int oldest = int.MaxValue;
        foreach (var pair in _byId)
        {
            if (pair.Value.LastFrame >= frameIndex) continue;   // seen this pass: still live
            if (pair.Value.LastFrame > oldest) continue;
            oldest = pair.Value.LastFrame;
            victim = pair.Key;
        }

        if (victim == 0) return false;
        _byId.Remove(victim);
        return true;
    }

    public Dictionary<string, object?> ToJson() => new()
    {
        ["max"] = MaxEntries,
        ["count"] = Count,
        ["saturated"] = Saturated,
        ["saturatedRegistrations"] = SaturatedRegistrations,
        ["rotations"] = Rotations,
    };

    private sealed class Entry
    {
        public string Name = "";
        public int LastFrame;
    }
}

/// <summary>
/// The "changed" detector. It answers the question four live captures could not (evidence 135 to
/// 137): the sequencer <b>pools</b> its particle and line GameObjects, re-activating and
/// re-parenting objects that already exist, so an "appeared since baseline" diff sees nothing at
/// all even while the beam is on screen. This class keeps a bounded snapshot of the objects that
/// existed at baseline and reports what happened to them.
///
/// <para><b>Identity.</b> Unity recycles instance ids (R27 finding 22), so a tracked object is keyed
/// on its instance id plus the parent it was first seen under plus the frame it was first seen on
/// (<see cref="CaptureObjectIdentity"/>). Name alone was not enough — pooled clones share a name by
/// construction, which is R28 finding 6. An observation that cannot be the same object retires the
/// entry and starts a new one; a parked object that woke up under a different parent is re-keyed in
/// place, so its next activation is reported as a first one. Entries that stop being observed are
/// swept (<see cref="Sweep"/>), which also frees their series and sampling slots, so a destroyed
/// object neither holds a tracking slot nor comes back as a half-updated row.</para>
///
/// <para><b>Cost.</b> One dictionary lookup and a handful of scalar comparisons per tracked object
/// per comparison pass, no LINQ: the scratch state and the position lists are reused and strings are
/// formatted only when a transition is actually recorded. (The comparison itself allocates nothing;
/// filling the scratch on the live side does — around a dozen interop calls and one marshaled name
/// string per object, which is why the sweep has its own deadline. R28 finding 28.22.) The pass runs
/// on the throttled sweep frames, under the same budget gate as the sweep.</para>
/// </summary>
internal sealed class CaptureObjectTracker
{
    private readonly Dictionary<int, CaptureObjectState> _byId = new();

    /// <summary>Reusable buffer for <see cref="Sweep"/> so the sweep allocates nothing per pass.</summary>
    private readonly List<int> _sweepScratch = new();

    public int Tracked => _byId.Count;

    /// <summary>Objects that did not fit <see cref="CaptureArgs.MaxTrackedObjects"/>.</summary>
    public int Dropped { get; private set; }

    /// <summary>Entries retired because their id came back as a different object, or they vanished.</summary>
    public int Retired { get; private set; }

    /// <summary>
    /// Entries kept but given a fresh identity because a parked object woke up under a new parent
    /// (R28 finding 6). Distinct from <see cref="Retired"/>: nothing was lost, a new life started.
    /// </summary>
    public int Rekeyed { get; private set; }

    public int Passes { get; private set; }
    public int Comparisons { get; private set; }
    public int Transitions { get; private set; }

    public void Reset()
    {
        _byId.Clear();
        _sweepScratch.Clear();
        Dropped = 0;
        Retired = 0;
        Rekeyed = 0;
        Passes = 0;
        Comparisons = 0;
        Transitions = 0;
    }

    public bool IsTracked(int instanceId) => _byId.ContainsKey(instanceId);

    /// <summary>
    /// True when <paramref name="instanceId"/> is worth filling the scratch state for: it is
    /// tracked and has not already been compared in this pass. The live side visits the same
    /// object twice on a sweep frame (once via the particle-parent walk, once via the scene
    /// sweep); this keeps the second visit free.
    /// </summary>
    public bool NeedsObservation(int instanceId, int frameIndex) =>
        _byId.TryGetValue(instanceId, out var state) && state.LastPass != frameIndex;

    /// <summary>
    /// Starts tracking an object from a full read. Returns false when it is already tracked or the
    /// cap is full (counted in <see cref="Dropped"/>).
    /// </summary>
    public bool TryTrack(CaptureObjectState scratch, int frameIndex = 0)
    {
        if (scratch.InstanceId == 0) return false;
        if (_byId.ContainsKey(scratch.InstanceId)) return false;
        if (_byId.Count >= CaptureArgs.MaxTrackedObjects) { Dropped++; return false; }

        var state = new CaptureObjectState();
        state.CopyStateFrom(scratch);
        state.BaselineActive = scratch.ActiveInHierarchy;
        state.FirstFrame = frameIndex;
        state.IdentityParentId = scratch.ParentInstanceId;
        state.Activations = 0;
        state.LastPass = -1;
        // It was read on THIS pass, even though it has not been compared yet: the recycle test must
        // not see a phantom gap on the entry's first comparison (R29 finding 20).
        state.LastPassOrdinal = Passes;
        _byId[scratch.InstanceId] = state;
        return true;
    }

    /// <summary>The identity string of a tracked object, or an empty string when it is not tracked.</summary>
    public string IdentityOf(int instanceId) =>
        _byId.TryGetValue(instanceId, out var state) ? state.DescribeIdentity() : "";

    public void BeginPass() => Passes++;

    /// <summary>True when <see cref="Observe"/> already handled this object on this pass.</summary>
    public bool WasComparedThisPass(int instanceId, int frameIndex) =>
        _byId.TryGetValue(instanceId, out var state) && state.LastPass == frameIndex;

    /// <summary>
    /// Records a re-parent the live side inferred from set membership rather than from a read — the
    /// pool-parent diff, whose subject has by definition moved somewhere the capture cannot see.
    /// Returns the row if it fit inside the cap.
    /// </summary>
    public CaptureChangeRow? NoteParentChange(
        int instanceId, int frameIndex, double atSeconds, string before, string after, CaptureReport report)
    {
        if (!_byId.TryGetValue(instanceId, out var known)) return null;
        known.LastPass = frameIndex;
        known.LastPassOrdinal = Passes;
        var row = Row(known, frameIndex, atSeconds, CaptureChangeKind.Reparented, before, after);
        return Add(report, row) ? row : null;
    }

    /// <summary>
    /// Compares one freshly read <paramref name="scratch"/> against what is tracked and appends a row
    /// per transition. Returns the row that wants a full description read (the first activation of
    /// an object that was inactive at baseline, or a re-parent), or null.
    /// </summary>
    public CaptureChangeRow? Observe(CaptureObjectState scratch, int frameIndex, double atSeconds, CaptureReport report)
    {
        if (scratch.InstanceId == 0) return null;
        if (!_byId.TryGetValue(scratch.InstanceId, out var known))
        {
            TryTrack(scratch, frameIndex);
            return null;
        }

        // Instance-id reuse. A different name is one signature; a deactivate-and-re-parent is the
        // other, but only across a gap in observation — on the next pass it is an ordinary return to
        // pool and the same object (R28 finding 6, narrowed by R29 finding 20).
        int unseenPasses = Math.Max(0, Passes - known.LastPassOrdinal);
        if (CaptureObjectIdentity.LooksRecycled(
                known.Name, scratch.Name,
                known.ActiveInHierarchy, scratch.ActiveInHierarchy,
                known.ParentInstanceId, scratch.ParentInstanceId,
                unseenPasses))
        {
            _byId.Remove(scratch.InstanceId);
            Retired++;
            report.RetireSeries(scratch.InstanceId, known.Name);
            TryTrack(scratch, frameIndex);
            return null;
        }

        if (known.LastPass == frameIndex) return null;

        // A parked clone that woke up somewhere else is a new life on the same id: close the old
        // series and restart the bookkeeping, so the activation below is reported as a first one
        // and asks for its full description instead of inheriting a spent Activations count.
        if (CaptureObjectIdentity.ReactivatedUnderNewParent(
                known.ActiveInHierarchy, scratch.ActiveInHierarchy,
                known.IdentityParentId, scratch.ParentInstanceId))
        {
            report.RetireSeries(scratch.InstanceId, known.Name);
            known.Rekey(scratch.Name, scratch.ParentInstanceId, frameIndex);
            Rekeyed++;
        }

        known.LastPass = frameIndex;
        known.LastPassOrdinal = Passes;
        Comparisons++;

        CaptureChangeRow? describe = null;

        if (scratch.ActiveInHierarchy != known.ActiveInHierarchy)
        {
            bool activated = scratch.ActiveInHierarchy;
            var row = Row(known, frameIndex, atSeconds,
                activated ? CaptureChangeKind.Activated : CaptureChangeKind.Deactivated,
                known.DescribeActive(), scratch.DescribeActive());
            bool added = Add(report, row);
            if (activated)
            {
                known.Activations++;
                // The pooled-clone case: it was parked and inactive when the capture started, and
                // the trigger woke it. That is the one row worth a full read.
                if (added && !known.BaselineActive && known.Activations == 1) describe = row;
            }
        }

        if (scratch.ParentInstanceId != known.ParentInstanceId)
        {
            var row = Row(known, frameIndex, atSeconds, CaptureChangeKind.Reparented,
                known.DescribeParent(), scratch.DescribeParent());
            if (Add(report, row)) describe ??= row;
        }

        if ((scratch.HasLine || scratch.HasHybrid) && scratch.PositionsDifferFrom(known))
        {
            Add(report, Row(known, frameIndex, atSeconds, CaptureChangeKind.LineMoved,
                known.DescribePositions(), scratch.DescribePositions()));
            var series = report.SeriesFor(scratch.InstanceId, known.Name);
            series?.TryAdd(frameIndex, atSeconds, scratch.SamplePositions);
        }

        if (scratch.HasParticle && known.HasParticle && scratch.ParticleIsPlaying != known.ParticleIsPlaying)
        {
            Add(report, Row(known, frameIndex, atSeconds,
                scratch.ParticleIsPlaying ? CaptureChangeKind.ParticleStarted : CaptureChangeKind.ParticleStopped,
                known.DescribeParticle(), scratch.DescribeParticle()));
        }

        known.CopyStateFrom(scratch);
        return describe;
    }

    /// <summary>
    /// Retires tracked objects that no comparison pass has seen for <paramref name="missedPasses"/>
    /// passes: they were destroyed, or they left every set the capture can re-find them in. Frees
    /// the tracking slot and stops a stale snapshot from being compared against a recycled id.
    /// </summary>
    public int Sweep(int frameIndex, int interval, int missedPasses = 6, CaptureReport? report = null)
    {
        if (_byId.Count == 0) return 0;
        if (interval < CaptureSampling.SampleEveryNthFrame) interval = CaptureSampling.SampleEveryNthFrame;
        int cutoff = frameIndex - (interval * missedPasses);
        if (cutoff <= 0) return 0;

        _sweepScratch.Clear();
        foreach (var pair in _byId)
        {
            var state = pair.Value;
            // Never seen by a pass yet: judge it by when it was first tracked instead.
            int last = state.LastPass >= 0 ? state.LastPass : state.FirstFrame;
            if (last < cutoff) _sweepScratch.Add(pair.Key);
        }

        for (int i = 0; i < _sweepScratch.Count; i++)
        {
            int id = _sweepScratch[i];
            // Retiring the tracked entry has to retire everything else keyed on that id, or the
            // sampling table fills with dead ids and line sampling stops silently (R28 finding 18).
            if (report != null && _byId.TryGetValue(id, out var dying))
            {
                report.RetireSeries(id, dying.Name);
                report.Sampled.Remove(id);
            }

            _byId.Remove(id);
        }

        Retired += _sweepScratch.Count;
        int removed = _sweepScratch.Count;
        _sweepScratch.Clear();
        return removed;
    }

    public Dictionary<string, object?> ToJson() => new()
    {
        ["maxTracked"] = CaptureArgs.MaxTrackedObjects,
        ["tracked"] = Tracked,
        ["dropped"] = Dropped,
        ["retired"] = Retired,
        ["rekeyed"] = Rekeyed,
        ["passes"] = Passes,
        ["comparisons"] = Comparisons,
        ["transitions"] = Transitions,
    };

    private static CaptureChangeRow Row(
        CaptureObjectState known, int frameIndex, double atSeconds, string kind, string before, string after) =>
        new()
        {
            InstanceId = known.InstanceId,
            Name = known.Name,
            Frame = frameIndex,
            AtSeconds = atSeconds,
            Kind = kind,
            Before = before,
            After = after,
        };

    private bool Add(CaptureReport report, CaptureChangeRow row)
    {
        if (!report.TryAddChange(row)) return false;
        Transitions++;
        return true;
    }
}

internal sealed class CaptureSample
{
    public int Frame;
    public double AtSeconds;
    public List<CaptureVec3> Positions = new();

    public Dictionary<string, object?> ToJson()
    {
        var pts = new List<Dictionary<string, object?>>(Positions.Count);
        foreach (var p in Positions) pts.Add(p.ToJson());
        return new Dictionary<string, object?>
        {
            ["frame"] = Frame,
            ["atSeconds"] = CaptureBudget.Round(AtSeconds),
            ["positions"] = pts,
        };
    }
}

/// <summary>
/// Throttled position samples for one new LineRenderer. <see cref="Moved"/> is the answer to the
/// question the capture exists to answer: does this endpoint follow something, or was it written
/// once and left alone?
/// </summary>
internal sealed class CaptureSampleSeries
{
    /// <summary>Meters. Below this a difference is float noise from the transform pipeline, not motion.</summary>
    public const float MoveEpsilon = 0.01f;

    public int InstanceId;
    public string Name = "";
    public List<CaptureSample> Samples = new();
    public bool Truncated;

    /// <summary>
    /// Set when the instance id came back under a different name: Unity recycled it, so this
    /// series is closed and the new object gets its own. Without this the two objects' positions
    /// interleave and <see cref="Moved"/> is always true (R27 finding 22).
    /// </summary>
    public bool Retired;

    /// <summary>Adds a sample if the throttle allows it. Returns false when the series is full.</summary>
    public bool TryAdd(int frameIndex, double atSeconds, IReadOnlyList<CaptureVec3> positions)
    {
        if (Retired) return false;
        if (!CaptureSampling.CanAddSample(Samples.Count))
        {
            Truncated = true;
            return false;
        }

        var sample = new CaptureSample { Frame = frameIndex, AtSeconds = atSeconds };
        for (int i = 0; i < positions.Count; i++) sample.Positions.Add(positions[i]);
        Samples.Add(sample);
        return true;
    }

    /// <summary>True when any sampled position moved more than <see cref="MoveEpsilon"/> from the first sample.</summary>
    public bool Moved
    {
        get
        {
            if (Samples.Count < 2) return false;
            var first = Samples[0].Positions;
            for (int s = 1; s < Samples.Count; s++)
            {
                var cur = Samples[s].Positions;
                if (cur.Count != first.Count) return true;
                for (int i = 0; i < cur.Count; i++)
                {
                    if (Math.Abs(cur[i].X - first[i].X) > MoveEpsilon) return true;
                    if (Math.Abs(cur[i].Y - first[i].Y) > MoveEpsilon) return true;
                    if (Math.Abs(cur[i].Z - first[i].Z) > MoveEpsilon) return true;
                }
            }
            return false;
        }
    }

    public Dictionary<string, object?> ToJson()
    {
        var samples = new List<Dictionary<string, object?>>(Samples.Count);
        foreach (var s in Samples) samples.Add(s.ToJson());
        return new Dictionary<string, object?>
        {
            ["instanceId"] = InstanceId,
            ["name"] = Name,
            ["moved"] = Moved,
            ["sampleCount"] = Samples.Count,
            ["truncated"] = Truncated,
            ["retired"] = Retired,
            ["samples"] = samples,
        };
    }
}

/// <summary>
/// One endpoint field re-read across the window: first value, last value, and whether it ever
/// changed. Values are compared as formatted strings so this stays free of game types and a
/// float3 endpoint and an Entity target are handled the same way.
/// </summary>
internal sealed class CaptureEndpointTrack
{
    public string Entity = "";
    public string Component = "";
    public string Field = "";
    public string First = "";
    public string Last = "";
    public int Samples;
    public bool Changed;

    public void Observe(string value)
    {
        value ??= "";
        if (Samples == 0)
        {
            First = value;
            Last = value;
            Samples = 1;
            return;
        }

        Samples++;
        if (!string.Equals(Last, value, StringComparison.Ordinal)) Changed = true;
        Last = value;
    }

    public Dictionary<string, object?> ToJson() => new()
    {
        ["entity"] = Entity,
        ["component"] = Component,
        ["field"] = Field,
        ["first"] = First,
        ["last"] = Last,
        ["samples"] = Samples,
        ["changed"] = Changed,
    };
}

/// <summary>
/// The whole capture. <see cref="ToJson"/> is what lands in
/// <c>BepInEx/progenitor-capture/&lt;timestamp&gt;.json</c>; <see cref="Summary"/> is the compact
/// object the bridge returns beside the path so the main session does not have to read the file
/// to know whether the capture caught anything.
/// </summary>
internal sealed class CaptureReport
{
    public string Side = "client";
    public string World = "";
    public DateTime StartedUtc;
    public double RequestedSeconds;
    public double ElapsedSeconds;
    public string Filter = "";
    public int MaxEntities;
    public int BaselineEntities;
    public int BaselineObjects;
    private bool _truncated;

    /// <summary>
    /// The report's headline honesty flag. Set explicitly by every cap in this file, and — R28
    /// finding 7 — <b>derived</b> from the budget as well: a window that skipped a quarter of its
    /// due sweeps recorded almost nothing, and reporting <c>truncated: false</c> for it made a
    /// capture that saw nothing indistinguishable from one that saw everything.
    /// </summary>
    public bool Truncated
    {
        get => _truncated || Budget.SweepSkipRatioExceeded;
        set => _truncated = value;
    }

    public string StopReason = "";
    public CaptureBudget Budget = new();

    /// <summary>The "changed" detector's state for this capture. Rebuilt per capture, never static.</summary>
    public CaptureObjectTracker Tracker = new();

    /// <summary>
    /// Which objects the sweeps take position samples from. Bounded and rotating, and it says so
    /// when it saturates (R28 finding 18).
    /// </summary>
    public CaptureSampleRegistry Sampled = new();

    /// <summary>
    /// Largest number of interest entities a single frame had to drop because the match set was
    /// bigger than <c>maxEntities</c>, and how many frames that happened on. A truncated frame
    /// emits no disappearance rows (see <see cref="CaptureEntitySelection"/>), so these two numbers
    /// are the honest signal that the window under-reported, instead of the single
    /// <see cref="Truncated"/> flag five other conditions also set.
    /// </summary>
    public int EntityOverflow;
    public int TruncatedFrames;

    /// <summary>Objects a scene sweep could not look at because it hit its per-sweep cap.</summary>
    public int ScanOverflow;

    /// <summary>
    /// Pool walks that ran out of node budget, so their child set was incomplete and could not be
    /// diffed (R28 finding 9).
    /// </summary>
    public int IncompleteWalks;

    /// <summary>Scene sweeps that stopped early because they ran past their own deadline.</summary>
    public int ScanDeadlineStops;

    /// <summary>Series slots reused because the series table was full (R28 finding 18).</summary>
    public int SeriesRotations;

    /// <summary>Sticky: the live-series table filled at least once.</summary>
    public bool SeriesSaturated;

    /// <summary>Series a full table refused outright.</summary>
    public int SeriesRefused;

    public List<CaptureEntityRow> Appeared = new();
    public List<CaptureEntityRow> Disappeared = new();
    public List<CaptureObjectRow> Objects = new();
    public List<CaptureChangeRow> Changes = new();
    public List<CaptureSampleSeries> Series = new();
    public List<CaptureEndpointTrack> Endpoints = new();

    public int Frames => Budget.Frames;

    /// <summary>Records one frame that had more matching entities than it was allowed to keep.</summary>
    public void NoteEntityOverflow(int overflow)
    {
        if (overflow <= 0) return;
        Truncated = true;
        TruncatedFrames++;
        if (overflow > EntityOverflow) EntityOverflow = overflow;
    }

    /// <summary>Records one scene sweep that stopped at its object cap.</summary>
    public void NoteScanOverflow(int overflow)
    {
        if (overflow <= 0) return;
        Truncated = true;
        if (overflow > ScanOverflow) ScanOverflow = overflow;
    }

    /// <summary>Records one pool walk whose child set was cut short by its node budget.</summary>
    public void NoteIncompleteWalk()
    {
        IncompleteWalks++;
        Truncated = true;
    }

    /// <summary>Records one scene sweep that stopped early on its own deadline.</summary>
    public void NoteScanDeadline()
    {
        ScanDeadlineStops++;
        Truncated = true;
    }

    public bool TryAddAppeared(CaptureEntityRow row)
    {
        if (Appeared.Count >= CaptureArgs.MaxAppearedRows) { Truncated = true; return false; }
        Appeared.Add(row);
        return true;
    }

    public bool TryAddDisappeared(CaptureEntityRow row)
    {
        if (Disappeared.Count >= CaptureArgs.MaxDisappearedRows) { Truncated = true; return false; }
        Disappeared.Add(row);
        return true;
    }

    public bool TryAddObject(CaptureObjectRow row)
    {
        if (Objects.Count >= CaptureArgs.MaxObjectRows) { Truncated = true; return false; }
        Objects.Add(row);
        return true;
    }

    public bool TryAddChange(CaptureChangeRow row)
    {
        if (Changes.Count >= CaptureArgs.MaxChangeRows) { Truncated = true; return false; }
        Changes.Add(row);
        return true;
    }

    /// <summary>
    /// Existing series for this instance id, or a new one while there is room for more. The name is
    /// part of the identity: Unity recycles instance ids, and a series that silently absorbed the
    /// positions of a second, unrelated object always reported <c>moved: true</c> (R27 finding 22).
    /// A live series whose id comes back under another name is retired and replaced.
    /// </summary>
    public CaptureSampleSeries? SeriesFor(int instanceId, string name)
    {
        int live = 0;
        for (int i = 0; i < Series.Count; i++)
        {
            var candidate = Series[i];
            if (!candidate.Retired) live++;
            if (candidate.InstanceId != instanceId || candidate.Retired) continue;
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal)) return candidate;
            candidate.Retired = true;
            live--;
            break;
        }

        // R28 finding 18: the cap used to count RETIRED series too, so every id-reuse retirement
        // burned a permanent slot and after forty of them SeriesFor returned null forever — line
        // sampling stopped mid-capture with nothing in the report saying so. Only live series count
        // against the cap, a full table rotates its stalest entry out, and both facts are reported.
        if (live >= CaptureSampling.MaxSeries)
        {
            SeriesSaturated = true;
            Truncated = true;
            if (!TryRetireStalestSeries()) { SeriesRefused++; return null; }
            SeriesRotations++;
        }

        PruneRetiredSeries();
        var series = new CaptureSampleSeries { InstanceId = instanceId, Name = name };
        Series.Add(series);
        return series;
    }

    /// <summary>Hard ceiling on the list itself, retired series included, so it cannot grow forever.</summary>
    public const int MaxSeriesRows = CaptureSampling.MaxSeries * 4;

    /// <summary>Retires the live series with the fewest samples taken longest ago.</summary>
    private bool TryRetireStalestSeries()
    {
        CaptureSampleSeries? victim = null;
        int oldest = int.MaxValue;
        for (int i = 0; i < Series.Count; i++)
        {
            var candidate = Series[i];
            if (candidate.Retired) continue;
            int last = candidate.Samples.Count == 0 ? -1 : candidate.Samples[candidate.Samples.Count - 1].Frame;
            if (last > oldest) continue;
            oldest = last;
            victim = candidate;
        }

        if (victim == null) return false;
        victim.Retired = true;
        return true;
    }

    /// <summary>Drops the oldest retired series once the list itself is at its ceiling.</summary>
    private void PruneRetiredSeries()
    {
        while (Series.Count >= MaxSeriesRows)
        {
            int index = -1;
            for (int i = 0; i < Series.Count; i++)
            {
                if (!Series[i].Retired) continue;
                index = i;
                break;
            }

            if (index < 0) return;
            Series.RemoveAt(index);
        }
    }

    /// <summary>
    /// Closes the live series for a recycled instance id, if there is one, and drops the id from
    /// the sampling table so its slot goes back into circulation (R28 finding 18).
    /// </summary>
    public void RetireSeries(int instanceId, string name)
    {
        Sampled.Remove(instanceId);
        for (int i = 0; i < Series.Count; i++)
        {
            var candidate = Series[i];
            if (candidate.InstanceId != instanceId || candidate.Retired) continue;
            if (!string.Equals(candidate.Name, name, StringComparison.Ordinal)) continue;
            candidate.Retired = true;
            return;
        }
    }

    /// <summary>Existing track for entity/component/field, or a new one while there is room.</summary>
    public CaptureEndpointTrack? TrackFor(string entity, string component, string field)
    {
        for (int i = 0; i < Endpoints.Count; i++)
        {
            var t = Endpoints[i];
            if (t.Entity == entity && t.Component == component && t.Field == field) return t;
        }

        // One entity contributes a handful of fields; the cap is on entities, applied by the caller.
        if (Endpoints.Count >= CaptureArgs.MaxTrackedEntities * 8) { Truncated = true; return null; }
        var track = new CaptureEndpointTrack { Entity = entity, Component = component, Field = field };
        Endpoints.Add(track);
        return track;
    }

    public Dictionary<string, object?> ToJson()
    {
        var appeared = new List<Dictionary<string, object?>>(Appeared.Count);
        foreach (var r in Appeared) appeared.Add(r.ToJson());
        var disappeared = new List<Dictionary<string, object?>>(Disappeared.Count);
        foreach (var r in Disappeared) disappeared.Add(r.ToJson());
        var objects = new List<Dictionary<string, object?>>(Objects.Count);
        foreach (var o in Objects) objects.Add(o.ToJson());
        var changes = new List<Dictionary<string, object?>>(Changes.Count);
        foreach (var c in Changes) changes.Add(c.ToJson());
        var series = new List<Dictionary<string, object?>>(Series.Count);
        foreach (var s in Series) series.Add(s.ToJson());
        var endpoints = new List<Dictionary<string, object?>>(Endpoints.Count);
        foreach (var e in Endpoints) endpoints.Add(e.ToJson());

        return new Dictionary<string, object?>
        {
            ["capture"] = new Dictionary<string, object?>
            {
                ["side"] = Side,
                ["world"] = World,
                ["startedUtc"] = StartedUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                ["requestedSeconds"] = RequestedSeconds,
                ["elapsedSeconds"] = CaptureBudget.Round(ElapsedSeconds),
                ["filter"] = Filter,
                ["maxEntities"] = MaxEntities,
                ["stopReason"] = StopReason,
                ["truncated"] = Truncated,
                ["truncatedFrames"] = TruncatedFrames,
                ["truncatedBySkippedSweeps"] = Budget.SweepSkipRatioExceeded,
                ["entityOverflow"] = EntityOverflow,
                ["scanOverflow"] = ScanOverflow,
                ["incompleteWalks"] = IncompleteWalks,
                ["scanDeadlineStops"] = ScanDeadlineStops,
                ["sampling"] = Sampled.ToJson(),
                ["seriesSaturated"] = SeriesSaturated,
                ["seriesRotations"] = SeriesRotations,
                ["seriesRefused"] = SeriesRefused,
                ["baseline"] = new Dictionary<string, object?>
                {
                    ["entities"] = BaselineEntities,
                    ["gameObjects"] = BaselineObjects,
                },
                ["budget"] = Budget.ToJson(),
                ["tracking"] = Tracker.ToJson(),
            },
            ["entitiesAppeared"] = appeared,
            ["entitiesDisappeared"] = disappeared,
            ["gameObjects"] = objects,
            ["gameObjectChanges"] = changes,
            ["lineSamples"] = series,
            ["endpointTracks"] = endpoints,
        };
    }

    /// <summary>
    /// What the bridge returns next to the report path: counts, every new sequence guid with the
    /// prefab and GameObject names seen around it, and one row per endpoint component saying
    /// whether it changed while the capture watched.
    /// </summary>
    public Dictionary<string, object?> Summary()
    {
        return new Dictionary<string, object?>
        {
            ["frames"] = Frames,
            ["elapsedSeconds"] = CaptureBudget.Round(ElapsedSeconds),
            ["stopReason"] = StopReason,
            ["truncated"] = Truncated,
            // The two numbers that say WHY a capture is thin. Buried inside `counts` they were
            // unreadable next to a confident `truncated: false` (R28 finding 7).
            ["sweepSkipRatio"] = CaptureBudget.Round(Budget.SweepSkipRatio),
            ["sweepsSkipped"] = Budget.SkippedScans,
            ["samplingSaturated"] = Sampled.Saturated || SeriesSaturated,
            ["counts"] = new Dictionary<string, object?>
            {
                ["baselineEntities"] = BaselineEntities,
                ["baselineGameObjects"] = BaselineObjects,
                ["entitiesAppeared"] = Appeared.Count,
                ["entitiesDisappeared"] = Disappeared.Count,
                ["gameObjectsAppeared"] = Objects.Count,
                ["gameObjectChanges"] = Changes.Count,
                ["trackedObjects"] = Tracker.Tracked,
                ["droppedObjects"] = Tracker.Dropped,
                ["lineSeries"] = Series.Count,
                ["endpointTracks"] = Endpoints.Count,
                ["skippedGameObjectWalks"] = Budget.SkippedWalks,
                ["skippedSceneScans"] = Budget.SkippedScans,
                ["skippedComparisons"] = Budget.SkippedComparisons,
                ["skippedEntityDiffs"] = Budget.SkippedEntityDiffs,
                ["sweepsDue"] = Budget.SweepsDue,
                ["forcedScans"] = Budget.ForcedScans,
                ["incompleteWalks"] = IncompleteWalks,
                ["sampledObjects"] = Sampled.Count,
                ["sampledRotations"] = Sampled.Rotations,
                ["seriesRotations"] = SeriesRotations,
                ["rekeyedObjects"] = Tracker.Rekeyed,
            },
            ["sequences"] = SequenceRows(),
            ["endpoints"] = EndpointRows(),
            ["lines"] = LineRows(),
            ["changedObjects"] = ChangedObjectRows(),
        };
    }

    /// <summary>
    /// One row per GameObject name that changed while the capture watched: which kinds of
    /// transition it went through and how many rows it produced. This is the column to read first
    /// on a pooled effect, where <c>gameObjects</c> (the "appeared since baseline" list) is empty
    /// by construction — the sequencer re-activates objects that already existed rather than
    /// creating new ones (evidence 135 to 137).
    /// </summary>
    public List<Dictionary<string, object?>> ChangedObjectRows()
    {
        var order = new List<string>();
        var kinds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in Changes)
        {
            var name = string.IsNullOrEmpty(row.Name)
                ? "#" + row.InstanceId.ToString(CultureInfo.InvariantCulture)
                : row.Name;
            if (!kinds.ContainsKey(name))
            {
                order.Add(name);
                kinds[name] = new List<string>();
                counts[name] = 0;
            }

            counts[name]++;
            if (!kinds[name].Contains(row.Kind)) kinds[name].Add(row.Kind);
        }

        var rows = new List<Dictionary<string, object?>>(order.Count);
        foreach (var name in order)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["name"] = name,
                ["kinds"] = kinds[name],
                ["count"] = counts[name],
            });
        }
        return rows;
    }

    /// <summary>
    /// New sequence guids, each with the prefab name of the entity that carried it and the names
    /// of the GameObjects that appeared within
    /// <see cref="CaptureArgs.SequenceObjectFrameWindow"/> frames after it. That chain — guid →
    /// prefab → clone — is the lookup the research section A30 describes.
    /// </summary>
    public List<Dictionary<string, object?>> SequenceRows()
    {
        var order = new List<int>();
        var byGuid = new Dictionary<int, Dictionary<string, object?>>();
        var prefabNames = new Dictionary<int, List<string>>();
        var objectNames = new Dictionary<int, List<string>>();
        var firstFrame = new Dictionary<int, int>();

        foreach (var row in Appeared)
        {
            foreach (var guid in row.SequenceGuids)
            {
                if (guid == 0) continue;
                if (!byGuid.ContainsKey(guid))
                {
                    order.Add(guid);
                    byGuid[guid] = new Dictionary<string, object?>();
                    prefabNames[guid] = new List<string>();
                    objectNames[guid] = new List<string>();
                    firstFrame[guid] = row.Frame;
                }

                var names = prefabNames[guid];
                var label = row.PrefabName ?? (row.PrefabHash.HasValue
                    ? row.PrefabHash.Value.ToString(CultureInfo.InvariantCulture)
                    : row.Entity);
                if (!names.Contains(label)) names.Add(label);
                if (row.Frame < firstFrame[guid]) firstFrame[guid] = row.Frame;
            }
        }

        foreach (var guid in order)
        {
            int from = firstFrame[guid];
            int to = from + CaptureArgs.SequenceObjectFrameWindow;
            var names = objectNames[guid];
            foreach (var obj in Objects)
            {
                if (obj.Frame < from || obj.Frame > to) continue;
                if (!names.Contains(obj.Name)) names.Add(obj.Name);
            }
        }

        var rows = new List<Dictionary<string, object?>>(order.Count);
        foreach (var guid in order)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["guid"] = guid,
                ["frame"] = firstFrame[guid],
                ["prefabs"] = prefabNames[guid],
                ["gameObjects"] = objectNames[guid],
            });
        }
        return rows;
    }

    /// <summary>One row per endpoint component: how many entities carried it and whether it moved.</summary>
    public List<Dictionary<string, object?>> EndpointRows()
    {
        var order = new List<string>();
        var entities = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var changedFields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var allFields = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var track in Endpoints)
        {
            if (!entities.ContainsKey(track.Component))
            {
                order.Add(track.Component);
                entities[track.Component] = new List<string>();
                changedFields[track.Component] = new List<string>();
                allFields[track.Component] = new List<string>();
            }

            if (!entities[track.Component].Contains(track.Entity)) entities[track.Component].Add(track.Entity);
            if (!allFields[track.Component].Contains(track.Field)) allFields[track.Component].Add(track.Field);
            if (track.Changed && !changedFields[track.Component].Contains(track.Field))
                changedFields[track.Component].Add(track.Field);
        }

        var rows = new List<Dictionary<string, object?>>(order.Count);
        foreach (var component in order)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["component"] = component,
                ["entities"] = entities[component].Count,
                ["fields"] = allFields[component],
                ["changedFields"] = changedFields[component],
                ["changed"] = changedFields[component].Count > 0,
            });
        }
        return rows;
    }

    /// <summary>One row per sampled LineRenderer: name, sample count, and whether it moved.</summary>
    public List<Dictionary<string, object?>> LineRows()
    {
        var rows = new List<Dictionary<string, object?>>(Series.Count);
        foreach (var s in Series)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["name"] = s.Name,
                ["samples"] = s.Samples.Count,
                ["moved"] = s.Moved,
            });
        }
        return rows;
    }
}

/// <summary>
/// Where a report lands: <c>BepInEx/progenitor-capture/&lt;timestamp&gt;.json</c>, a sibling of the
/// reload folder rather than a child of it — the reloader scans its folder for plugin DLLs and a
/// JSON file dropped in there would be noise on every scan.
/// </summary>
internal static class CaptureReportPaths
{
    public const string FolderName = "progenitor-capture";

    public static string FileName(DateTime utc) =>
        utc.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".json";

    public static string Folder(string reloadFolder)
    {
        if (string.IsNullOrWhiteSpace(reloadFolder)) return FolderName;
        var parent = Path.GetDirectoryName(reloadFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(parent)
            ? Path.Combine(reloadFolder, FolderName)
            : Path.Combine(parent, FolderName);
    }

    public static string For(string reloadFolder, DateTime utc) => Path.Combine(Folder(reloadFolder), FileName(utc));
}

#pragma warning restore CS0649
