using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Castles;
using Progenitor.Content;
using Progenitor.Inventory;
using Progenitor.Materials;
using Progenitor.Perf;
using Progenitor.Persistence;
using Progenitor.Servants;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The server frame budgets: the budget and carry-over primitives, the incremental shared-storage
/// index, the budgeted material view refresh, the servant hook's skip and caches, the save guard's
/// off-thread stamp sampling, the item-removal DLL metadata cache and the work counters the hitch
/// summary prints. Game-free.
/// </summary>
[Collection("UiFrameCost")]
public class FrameBudgetTests
{
    /// <summary>A clock that moves one tick (one millisecond at frequency 1000) every time it is read.</summary>
    private sealed class StepClock
    {
        public long Now;
        public long Read() => Now++;
    }

    private static FrameBudget Budget(StepClock clock, double ms) => new(ms, clock.Read, 1000);

    // ---- FrameBudget / BudgetedQueue -------------------------------------------------------------

    [Fact]
    public void Budget_IsExhaustedOnceItsAllowanceHasPassed()
    {
        long now = 0;
        var budget = new FrameBudget(3, () => now, 1000);
        Assert.False(budget.Exhausted);
        now = 2;
        Assert.False(budget.Exhausted);
        now = 3;
        Assert.True(budget.Exhausted);
        Assert.Equal(3.0, budget.ElapsedMs, 3);
    }

    [Fact]
    public void Queue_StopsAtTheBudgetAndCarriesTheRestOver()
    {
        long now = 0;
        var seen = new List<int>();
        var queue = new BudgetedQueue<int>(i => { seen.Add(i); now += 1; return true; });
        for (int i = 0; i < 10; i++) queue.Enqueue(i);

        var run = queue.Run(new FrameBudget(3, () => now, 1000));
        Assert.Equal(3, run.Completed);
        Assert.Equal(7, run.Remaining);
        Assert.Equal(new[] { 0, 1, 2 }, seen);

        // The next frame continues where this one stopped.
        run = queue.Run(new FrameBudget(3, () => now, 1000));
        Assert.Equal(3, run.Completed);
        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5 }, seen);
    }

    [Fact]
    public void Queue_AlwaysTakesOneStepEvenWithTheBudgetSpent()
    {
        long now = 0;
        var queue = new BudgetedQueue<int>(_ => { now += 50; return true; });
        queue.Enqueue(1);
        queue.Enqueue(2);
        var spent = new FrameBudget(0, () => now, 1000);
        var run = queue.Run(spent);
        Assert.Equal(1, run.Steps);
        Assert.Equal(1, run.Remaining);
    }

    [Fact]
    public void Queue_ItemTakesSeveralStepsAcrossFrames()
    {
        long now = 0;
        var left = new Dictionary<string, int> { ["a"] = 5, ["b"] = 1 };
        var queue = new BudgetedQueue<string>(k => { now++; return --left[k] == 0; });
        queue.Enqueue("a");
        queue.Enqueue("b");
        var run = queue.Run(new FrameBudget(2, () => now, 1000));
        Assert.Equal(2, run.Steps);
        Assert.Equal(0, run.Completed);
        Assert.Equal(2, run.Remaining);
        while (queue.Count > 0) queue.Run(new FrameBudget(2, () => now, 1000));
        Assert.Equal(0, left["a"]);
        Assert.Equal(0, left["b"]);
    }

    [Fact]
    public void Queue_DropsAnItemWhoseStepThrows()
    {
        var queue = new BudgetedQueue<int>(i => i == 1 ? throw new InvalidOperationException() : true);
        queue.Enqueue(1);
        queue.Enqueue(2);
        var run = queue.Run(new FrameBudget(100));
        Assert.Equal(0, run.Remaining);
        Assert.Equal(2, run.Completed);
    }

    // ---- IncrementalGrouping -----------------------------------------------------------------------

    private static IncrementalGrouping<int, int, int> ByTens() =>
        new(n => (n >= 0, n / 10, n), (a, b) => b.CompareTo(a));

    [Fact]
    public void Grouping_BuildsAcrossFramesAndKeepsTheOldIndexUntilDone()
    {
        var clock = new StepClock();
        var index = ByTens();
        Assert.False(index.Ready(1));
        Assert.True(index.Due(1, 0, 10_000));

        index.Begin(new[] { 1, 2, 11, 12, 13, -5, 21 }, epoch: 1, nowMs: 0);
        Assert.True(index.Building);
        Assert.False(index.Due(1, 0, 10_000));
        Assert.False(index.Step(Budget(clock, 2)));
        Assert.True(index.Remaining > 0);
        Assert.False(index.Ready(1));
        int frames = 1;
        while (!index.Step(Budget(clock, 2))) frames++;
        Assert.True(frames > 1);
        Assert.True(index.Ready(1));
        Assert.Equal(1, index.Builds);

        Assert.True(index.TryGet(1, 1, out var tens));
        Assert.Equal(new[] { 13, 12, 11 }, tens); // sorted by the given order
        Assert.True(index.TryGet(1, 9, out var none));
        Assert.Empty(none);

        // A rebuild in progress: the finished index keeps answering.
        index.Begin(new[] { 15 }, epoch: 1, nowMs: 20_000);
        Assert.True(index.TryGet(1, 1, out tens));
        Assert.Equal(3, tens.Count);
        Assert.True(index.Step(Budget(clock, 5)));
        Assert.True(index.TryGet(1, 1, out tens));
        Assert.Equal(new[] { 15 }, tens);
    }

    [Fact]
    public void Grouping_IsDueWhenOldOrFromAnotherWorldLoad()
    {
        var index = ByTens();
        index.Begin(new[] { 1 }, epoch: 1, nowMs: 1_000);
        index.Step(new FrameBudget(100));
        Assert.False(index.Due(1, 5_000, 10_000));
        Assert.True(index.Due(1, 11_000, 10_000));
        Assert.True(index.Due(2, 5_000, 10_000));
        Assert.False(index.Ready(2));
        Assert.False(index.TryGet(2, 0, out _));
    }

    [Fact]
    public void Grouping_DropsASourceWhoseClassifyThrows()
    {
        var index = new IncrementalGrouping<int, int, int>(n => n == 3 ? throw new Exception() : (true, 0, n));
        index.Begin(new[] { 1, 3, 4 }, 1, 0);
        Assert.True(index.Step(new FrameBudget(100)));
        Assert.True(index.TryGet(1, 0, out var values));
        Assert.Equal(new[] { 1, 4 }, values);
    }

    // ---- MaterialRefreshPass -----------------------------------------------------------------------

    private static (MaterialViewRefreshBenchTests.BenchWorld World, MaterialService Service) BenchService()
    {
        var world = new MaterialViewRefreshBenchTests.BenchWorld();
        var provider = new MaterialViewRefreshBenchTests.BenchProvider(world.ChestIds);
        var registry = new MaterialSourceRegistry { HookState = () => (true, "") };
        registry.Register(provider, "bench");
        return (world, new MaterialService(registry, world, _ => { }));
    }

    [Fact]
    public void Pass_ReadsWhatTheCachedViewDependedOnAcrossFramesThenReusesIt()
    {
        var (world, service) = BenchService();
        var reads = new MaterialStockReads(world);
        var outcomes = new List<MaterialViewOutcome>();
        long readsAtPush = -1;
        var pass = new MaterialRefreshPass(item =>
        {
            readsAtPush = reads.WorldReads;
            outcomes.Add(service.RefreshView(item.Character, item.Operation, reads, item.Recompute, out _));
            readsAtPush = reads.WorldReads - readsAtPush;
        });

        MaterialPushItem Item()
        {
            var item = new MaterialPushItem { Character = world.Player, User = world.User, Operation = MaterialOperation.Build };
            service.CachedDependencies(world.Player, MaterialOperation.Build, item.WarmIds, item.WarmStocks);
            return item;
        }

        // First pass: nothing cached, so the push computes in one step.
        var first = Item();
        Assert.Empty(first.WarmStocks);
        pass.Start(new[] { first }, everyone: true, reads);
        pass.Run(new FrameBudget(1000));
        Assert.False(pass.Active);
        Assert.Equal(MaterialViewOutcome.Computed, outcomes[^1]);

        // Second pass: every dependency is read a step at a time, carried over frames under a 3 ms budget.
        var second = Item();
        Assert.True(second.WarmStocks.Count >= MaterialViewRefreshBenchTests.Chests);
        Assert.NotEmpty(second.WarmIds);
        pass.Start(new[] { second }, everyone: true, reads);
        var clock = new StepClock();
        int frames = 0;
        while (pass.Active)
        {
            var run = pass.Run(Budget(clock, 3));
            Assert.True(run.Steps <= 3);
            frames++;
        }
        Assert.True(frames > 50);
        Assert.Equal(MaterialViewOutcome.Reused, outcomes[^1]);
        Assert.Equal(0, readsAtPush); // the reuse check found every inventory already read
        Assert.Equal(second.WarmIds.Count + second.WarmStocks.Count, pass.WarmSteps);
    }

    [Fact]
    public void Pass_ReadsAnInventoryOncePerPassForEveryItem()
    {
        var (world, service) = BenchService();
        var reads = new MaterialStockReads(world);
        var pass = new MaterialRefreshPass(item => service.RefreshView(item.Character, item.Operation, reads, item.Recompute, out _));
        var ops = new[] { MaterialOperation.Build, MaterialOperation.Craft, MaterialOperation.Upgrade };
        List<MaterialPushItem> Items()
        {
            var list = new List<MaterialPushItem>();
            foreach (var op in ops)
            {
                var item = new MaterialPushItem { Character = world.Player, User = world.User, Operation = op };
                service.CachedDependencies(world.Player, op, item.WarmIds, item.WarmStocks);
                list.Add(item);
            }
            return list;
        }
        pass.Start(Items(), true, reads);
        pass.Run(new FrameBudget(1000));
        long before = reads.WorldReads;
        pass.Start(Items(), true, reads);
        while (pass.Active) pass.Run(new FrameBudget(1000));
        // The three views share the chests: each is read once in the pass, not once per view.
        Assert.True(reads.WorldReads - before <= MaterialViewRefreshBenchTests.Chests + 3);
    }

    [Fact]
    public void Pass_StartReplacesAPassInProgressAndClearsItsReads()
    {
        var (world, _) = BenchService();
        var reads = new MaterialStockReads(world);
        int pushed = 0;
        var pass = new MaterialRefreshPass(_ => pushed++);
        var item = new MaterialPushItem();
        item.WarmStocks.AddRange(world.ChestRefs);
        pass.Start(new[] { item }, true, reads);
        pass.Run(Budget(new StepClock(), 2));
        Assert.True(pass.Active);
        pass.Start(new[] { new MaterialPushItem() }, false, reads);
        Assert.Equal(1, pass.Remaining);
        Assert.False(pass.Everyone);
        pass.Run(new FrameBudget(1000));
        Assert.Equal(1, pushed);
        pass.Clear();
        Assert.False(pass.Active);
    }

    // ---- servants ----------------------------------------------------------------------------------

    private static MissionRow Row(long start, float seconds) => new() { MissionGuid = 1, StartTicks = start, LengthSeconds = seconds };

    [Fact]
    public void Servants_EarliestCompletionAndTheSkip()
    {
        long s = ServantLogic.TicksPerSecond;
        var rows = new[] { Row(100 * s, 600), Row(50 * s, 300), Row(0, 3600) };
        long earliest = ServantLogic.EarliestCompletion(rows);
        Assert.Equal(350 * s, earliest);
        Assert.Equal(long.MaxValue, ServantLogic.EarliestCompletion(Array.Empty<MissionRow>()));

        // Not within the slack: skip. Within it, or unknown, or timers off: scan.
        Assert.False(ServantLogic.AnyDueBy(earliest, 340 * s, 2 * s, false));
        Assert.True(ServantLogic.AnyDueBy(earliest, 348 * s, 2 * s, false));
        Assert.True(ServantLogic.AnyDueBy(earliest, 400 * s, 2 * s, false));
        Assert.True(ServantLogic.AnyDueBy(long.MinValue, 0, 2 * s, false));
        Assert.True(ServantLogic.AnyDueBy(earliest, 0, 2 * s, true));
        Assert.False(ServantLogic.AnyDueBy(long.MaxValue, long.MaxValue - 10 * s, 2 * s, false));

        // The skip agrees with the per-row rule it replaces.
        foreach (long now in new[] { 0L, 340 * s, 348 * s, 349 * s, 351 * s, 4000 * s })
        {
            bool anyRow = false;
            foreach (var r in rows) anyRow |= ServantLogic.IsDue(r, now, 2 * s, false);
            Assert.Equal(anyRow, ServantLogic.AnyDueBy(earliest, now, 2 * s, false));
        }
    }

    [Fact]
    public void Servants_TerritoryCacheKeepsKnownTerritoriesAndRetriesMisses()
    {
        long s = ServantLogic.TicksPerSecond;
        Assert.True(ServantLogic.TerritoryCacheHit(12, 0, 3600 * s, 5 * s));
        Assert.True(ServantLogic.TerritoryCacheHit(-1, 0, 4 * s, 5 * s));
        Assert.False(ServantLogic.TerritoryCacheHit(-1, 0, 5 * s, 5 * s));
    }

    // ---- save guard stamp sampling -----------------------------------------------------------------

    [Fact]
    public void Poller_SamplesOffTheCallerAndHandsTheResultBackOnce()
    {
        var queued = new List<Action>();
        var stamp = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var poller = new SaveStampPoller(_ => stamp, queued.Add);

        poller.Request("folder", 1.0);
        Assert.True(poller.Busy);
        poller.Request("folder", 1.5); // one sample at a time
        Assert.Single(queued);
        Assert.False(poller.TryTake(out _, out _));

        queued[0]();
        Assert.True(poller.TryTake(out var got, out double askedAt));
        Assert.Equal(stamp, got);
        Assert.Equal(1.0, askedAt);
        Assert.False(poller.TryTake(out _, out _));
        Assert.False(poller.Busy);
    }

    [Fact]
    public void Poller_ResetDropsASampleAskedForBeforeIt()
    {
        var queued = new List<Action>();
        var poller = new SaveStampPoller(_ => DateTime.UtcNow, queued.Add);
        poller.Request("f", 1);
        poller.Reset();
        queued[0]();
        Assert.False(poller.TryTake(out _, out _));
        Assert.False(poller.Busy);
        poller.Request("f", 2);
        Assert.Equal(2, queued.Count);
    }

    [Fact]
    public void Poller_AThrowingSampleReadsAsNoStamp()
    {
        var poller = new SaveStampPoller(_ => throw new IOException("busy"), work => work());
        poller.Request("f", 3);
        Assert.True(poller.TryTake(out var got, out _));
        Assert.Equal(DateTime.MinValue, got);
    }

    [Fact]
    public void Poller_DrivesTheSuspensionToTheSameResumeAsInlineSampling()
    {
        // The save file's stamp moves at t=2 s and stands still; samples are asked every 0.25 s and land a tick later.
        var before = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var after = before.AddMinutes(1);
        double now = 0;
        var queued = new Queue<Action>();
        var poller = new SaveStampPoller(_ => now >= 2 ? after : before, queued.Enqueue);
        var suspension = new SyncSaveSuspension();
        suspension.Begin(0, before);
        double resumedAt = double.NaN;
        for (int tick = 0; tick < 40 && double.IsNaN(resumedAt); tick++)
        {
            now = tick * 0.25;
            bool resume = poller.TryTake(out var stamp, out double askedAt)
                ? suspension.ShouldResume(askedAt, stamp)
                : suspension.ShouldResume(now, DateTime.MinValue);
            if (resume) { resumedAt = now; break; }
            if (!poller.Busy) poller.Request("f", now);
            while (queued.Count > 0) queued.Dequeue()(); // the sample runs between frames
        }
        Assert.False(double.IsNaN(resumedAt));
        Assert.False(suspension.TimedOut);
        // Never before the stamp has stood still for the settle time.
        Assert.True(resumedAt >= 2 + SyncSaveSuspension.SettleSeconds);
    }

    // ---- item removal DLL metadata -----------------------------------------------------------------

    [Fact]
    public void GuidCache_ReadsAnUnchangedOldFileOnce()
    {
        var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var cache = new ItemOwnerDiskPlugins.GuidCache(() => now);
        int reads = 0;
        IReadOnlyList<string> Read(string _) { reads++; return new[] { "a.b" }; }
        var written = now.AddMinutes(-5);
        Assert.Equal(new[] { "a.b" }, cache.Get("x.dll", 100, written, Read));
        Assert.Equal(new[] { "a.b" }, cache.Get("X.DLL", 100, written, Read));
        Assert.Equal(1, reads);
        cache.Get("x.dll", 101, written, Read); // changed length
        cache.Get("x.dll", 101, written.AddSeconds(1), Read); // changed write time
        Assert.Equal(3, reads);
    }

    [Fact]
    public void GuidCache_DoesNotKeepAFreshOrUnreadableFile()
    {
        var now = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
        var cache = new ItemOwnerDiskPlugins.GuidCache(() => now);
        int reads = 0;
        cache.Get("new.dll", 1, now.AddMilliseconds(-500), _ => { reads++; return new[] { "n" }; });
        cache.Get("new.dll", 1, now.AddMilliseconds(-500), _ => { reads++; return new[] { "n" }; });
        cache.Get("bad.dll", 1, now.AddHours(-1), _ => { reads++; return null; });
        cache.Get("bad.dll", 1, now.AddHours(-1), _ => { reads++; return null; });
        Assert.Equal(4, reads);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void DiskPlugins_WithACacheGiveTheSameAnswerWithoutRereading()
    {
        string root = Path.Combine(Path.GetTempPath(), "progenitor-guidcache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var plugins = Path.Combine(root, "plugins", "Mod");
            Directory.CreateDirectory(plugins);
            var dll = Path.Combine(plugins, "Mod.dll");
            File.WriteAllBytes(dll, new byte[] { 1, 2, 3 });
            File.SetLastWriteTimeUtc(dll, DateTime.UtcNow.AddHours(-1));
            var ledger = new ItemOwnerLedger();
            Assert.True(ledger.Register("gone.mod/item1", 1, "gone.mod", "plugins/Gone.dll"));
            int reads = 0;
            IReadOnlyList<string> Read(string _) { reads++; return new[] { "other.mod" }; }
            var cache = new ItemOwnerDiskPlugins.GuidCache();
            bool first = new ItemOwnerDiskPlugins(root, Read, cache).Absent(ledger, "gone.mod");
            bool second = new ItemOwnerDiskPlugins(root, Read, cache).Absent(ledger, "gone.mod");
            Assert.Equal(first, second);
            Assert.Equal(1, reads);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // ---- work counters -----------------------------------------------------------------------------

    [Fact]
    public void FrameWork_FormatsPerSubsystemCostAndCarryOverAndResets()
    {
        FrameWork.ResetForTests();
        Assert.Equal("", FrameWork.Format());
        long ms = System.Diagnostics.Stopwatch.Frequency / 1000;
        FrameWork.Note("materials", 2 * ms, 12);
        FrameWork.Note("materials", 3 * ms, 4);
        FrameWork.NoteSkip("servants.hook");
        FrameWork.NoteSkip("servants.hook");
        string line = FrameWork.Format();
        Assert.Equal(" work=[materials frames:2 ms:5 max:3 carried:12 skips:0, servants.hook frames:0 ms:0 max:0 carried:0 skips:2]", line);
        FrameWork.ResetWindow();
        Assert.Equal("", FrameWork.Format());
        FrameWork.ResetForTests();
    }

    [Fact]
    public void HitchSummary_CarriesTheWorkCounters()
    {
        UiFrameCost.ResetForTests();
        FrameWork.ResetForTests();
        var lines = new List<string>();
        UiFrameCost.LogSink = lines.Add;
        long nowMs = 0;
        UiFrameCost.NowMs = () => nowMs;
        try
        {
            FrameWork.Note("materials", System.Diagnostics.Stopwatch.Frequency / 1000, 3);
            UiFrameCost.IntervalMs = 0;
            UiFrameCost.Record(nowMs);
            nowMs = 10;
            UiFrameCost.IntervalMs = 200; // a hitch, so the window writes a summary
            UiFrameCost.Record(nowMs);
            nowMs = UiFrameCost.SummaryEveryMs + 20;
            UiFrameCost.IntervalMs = 0;
            UiFrameCost.Record(nowMs);
            var summary = lines.Find(l => l.StartsWith("ui hitch summary", StringComparison.Ordinal));
            Assert.NotNull(summary);
            Assert.EndsWith(" work=[materials frames:1 ms:1 max:1 carried:3 skips:0]", summary);
            Assert.Equal("", FrameWork.Format()); // the window was reset with the summary
        }
        finally
        {
            UiFrameCost.ResetForTests();
            FrameWork.ResetForTests();
        }
    }
}
