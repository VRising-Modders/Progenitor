using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Progenitor.Castles;
using Xunit;
using Xunit.Abstractions;
using CastlesApi = Progenitor.Castles.Castles;

namespace Progenitor.Tests;

/// <summary>
/// The castle inventory membership index behind Containers()/Stations() and
/// ICastleHandle.StructureRevision. Entity ids are placeholders.
/// </summary>
public sealed class CastleInventoryIndexTests
{
    private static readonly CastleId HeartA = new(500, 1);
    private static readonly CastleId HeartB = new(900, 1);

    private sealed class FakeWorld
    {
        public long? Stamp = 1;
        public long Now;
        public int ScanCalls;
        public readonly List<(CastleId Model, CastleId Heart)> Models = new();

        public CastleInventoryIndex Index() => new(() => Stamp, () => { ScanCalls++; return Models.ToArray(); }, () => Now);

        public void Build(int index, CastleId heart) { Models.Add((new CastleId(index, 1), heart)); Stamp++; }

        public void Destroy(int index) { Models.RemoveAll(m => m.Model.Index == index); Stamp++; }
    }

    [Fact]
    public void Membership_is_grouped_by_heart_sorted_and_reused_while_the_stamp_holds()
    {
        var world = new FakeWorld();
        world.Build(12, HeartA); world.Build(10, HeartA); world.Build(11, HeartB);
        var index = world.Index();
        Assert.Equal(new[] { 10, 12 }, index.Of(HeartA, 1).Select(m => m.Index));
        Assert.Equal(new[] { 11 }, index.Of(HeartB, 1).Select(m => m.Index));
        for (int i = 0; i < 100; i++) index.Of(HeartA, 1);
        Assert.Equal(1, world.ScanCalls);
        Assert.Empty(index.Of(new CastleId(1, 1), 1));
    }

    [Fact]
    public void Revision_moves_only_for_the_plot_whose_container_was_built_or_destroyed()
    {
        var world = new FakeWorld();
        world.Build(10, HeartA); world.Build(20, HeartB);
        var index = world.Index();
        long a0 = index.RevisionOf(HeartA, 1), b0 = index.RevisionOf(HeartB, 1);
        Assert.NotEqual(0, a0); Assert.NotEqual(a0, b0);

        world.Build(11, HeartA);
        long a1 = index.RevisionOf(HeartA, 1);
        Assert.NotEqual(a0, a1);
        Assert.Equal(b0, index.RevisionOf(HeartB, 1));

        world.Destroy(11);
        long a2 = index.RevisionOf(HeartA, 1);
        Assert.NotEqual(a1, a2); Assert.NotEqual(a0, a2); // never reused
        Assert.Equal(new[] { 10 }, index.Of(HeartA, 1).Select(m => m.Index));

        // A stamp move that changes nothing on the plot (a building elsewhere) leaves it alone.
        world.Stamp++;
        Assert.Equal(a2, index.RevisionOf(HeartA, 1));
        Assert.Equal(b0, index.RevisionOf(HeartB, 1));
    }

    [Fact]
    public void The_last_container_destroyed_and_the_first_built_both_move_the_revision()
    {
        var world = new FakeWorld();
        world.Build(10, HeartA);
        var index = world.Index();
        long empty = index.RevisionOf(HeartB, 1);
        world.Build(20, HeartB);
        long one = index.RevisionOf(HeartB, 1);
        Assert.NotEqual(empty, one);
        world.Destroy(20);
        Assert.NotEqual(one, index.RevisionOf(HeartB, 1));
        Assert.Empty(index.Of(HeartB, 1));
    }

    [Fact]
    public void A_world_load_rescans_and_moves_every_revision()
    {
        var world = new FakeWorld();
        world.Build(10, HeartA);
        var index = world.Index();
        long before = index.RevisionOf(HeartA, 1);
        Assert.NotEqual(before, index.RevisionOf(HeartA, 2));
        Assert.Equal(2, world.ScanCalls);
    }

    [Fact]
    public void An_unreadable_stamp_rescans_every_read_and_an_old_scan_is_redone()
    {
        var world = new FakeWorld { Stamp = null };
        world.Models.Add((new CastleId(10, 1), HeartA));
        var index = world.Index();
        index.Of(HeartA, 1); index.Of(HeartA, 1);
        Assert.Equal(2, world.ScanCalls);

        world.Stamp = 5;
        index.Of(HeartA, 1); index.Of(HeartA, 1);
        Assert.Equal(3, world.ScanCalls);
        // A change the stamp missed is still found after MaxAgeMs.
        world.Models.Add((new CastleId(11, 1), HeartA));
        Assert.Single(index.Of(HeartA, 1));
        world.Now += CastleInventoryIndex.MaxAgeMs;
        Assert.Equal(2, index.Of(HeartA, 1).Count);
    }

    [Fact]
    public void Reads_without_a_scan_are_counted_as_hits_and_each_scan_by_its_cause()
    {
        var world = new FakeWorld();
        world.Build(10, HeartA);
        var index = world.Index();
        index.Of(HeartA, 1);              // first
        index.Of(HeartA, 1);              // hit
        index.RevisionOf(HeartA, 1);      // hit
        world.Build(11, HeartA);
        index.Of(HeartA, 1);              // stamp
        world.Now += CastleInventoryIndex.MaxAgeMs;
        index.Of(HeartA, 1);              // age
        world.Stamp = null;
        index.Of(HeartA, 1);              // unreadable
        Assert.Equal(4, index.Scans);
        Assert.Equal(2, index.Hits);
        Assert.Equal((1, 1, 1, 1), (index.FirstScans, index.StampScans, index.AgeScans, index.UnreadableScans));
        var status = index.Describe();
        Assert.Equal(4, status["scans"]);
        Assert.Equal(2L, status["hits"]);
    }

    [Fact]
    public void A_new_index_after_a_Core_swap_hands_out_larger_revisions_than_the_old_one()
    {
        var world = new FakeWorld();
        world.Build(10, HeartA);
        var old = world.Index();
        long last = 0;
        for (int i = 0; i < 5; i++) { world.Build(100 + i, HeartA); last = old.RevisionOf(HeartA, 1); }
        System.Threading.Thread.Sleep(20);
        var replacement = world.Index();
        Assert.True(replacement.RevisionOf(HeartA, 1) > last);
    }

    [Fact]
    public void A_throwing_stamp_counts_as_unreadable()
    {
        int scans = 0;
        var index = new CastleInventoryIndex(() => throw new InvalidOperationException("no world"),
            () => { scans++; return new[] { (new CastleId(10, 1), HeartA) }; }, () => 0);
        Assert.Single(index.Of(HeartA, 1));
        Assert.Single(index.Of(HeartA, 1));
        Assert.Equal(2, scans);
    }
}

/// <summary>
/// Cost model for the 1,267-building plot: the old read visited every castle building on the
/// server for each Containers() and each Stations() call; the index visits only inventory-bearing
/// models, once per structural change.
/// </summary>
public sealed class CastleInventoryIndexBench
{
    private readonly ITestOutputHelper _out;
    public CastleInventoryIndexBench(ITestOutputHelper output) => _out = output;

    [Fact]
    public void One_scan_serves_a_burst_of_reads_on_a_large_plot()
    {
        var heart = new CastleId(1, 1);
        // Three castles, 1,267 buildings on the big plot; 180 of its models hold an inventory.
        var models = new List<(CastleId, CastleId)>();
        for (int i = 0; i < 180; i++) models.Add((new CastleId(10_000 + i, 1), heart));
        for (int i = 0; i < 120; i++) models.Add((new CastleId(20_000 + i, 1), new CastleId(2, 1)));
        int visits = 0;
        long stamp = 1;
        var index = new CastleInventoryIndex(() => stamp, () => { visits += models.Count; return models; }, () => 0);

        var clock = Stopwatch.StartNew();
        for (int refresh = 0; refresh < 60; refresh++)
        {
            Assert.Equal(180, index.Of(heart, 1).Count); // Containers()
            Assert.Equal(180, index.Of(heart, 1).Count); // Stations()
            index.RevisionOf(heart, 1);
        }
        _out.WriteLine($"60 refreshes: {clock.Elapsed.TotalMilliseconds:F2} ms, {visits} model visits");
        // Before: 2 calls × 60 refreshes × ~2,000 castle buildings server-wide.
        Assert.Equal(models.Count, visits);

        stamp++;
        models.Add((new CastleId(10_500, 1), heart));
        Assert.Equal(181, index.Of(heart, 1).Count);
        Assert.Equal(2 * 300 + 1, visits);
    }
}

[Collection("PluginLogger")]
public sealed class CastleStructureRevisionHandleTests : IDisposable
{
    private static readonly CastleId Heart = new(500, 1);
    private readonly CastlesTests.FakeCastleRuntime _server = new() { IsServer = true };

    public CastleStructureRevisionHandleTests()
    {
        Plugin.Logger = new TestLogSource();
        Progenitor.WorldState.WorldEpoch.ResetForTests();
        CastlesApi.ResetForTests();
        _server.Castles.Add(new CastleSnapshot
        {
            Heart = Heart,
            Territory = new CastleTerritoryInfo { Index = 4, Entity = new CastleId(501, 1), MinX = 0, MinY = 0, MaxX = 10, MaxY = 10 },
        });
        CastlesApi.SetRuntimeForTests(_server);
    }

    public void Dispose()
    {
        CastlesApi.ResetForTests();
        Progenitor.WorldState.WorldEpoch.ResetForTests();
        Plugin.Logger = null;
    }

    [Fact]
    public void The_handle_reports_the_runtime_revision_and_zero_when_disabled()
    {
        _server.Revisions[Heart] = 7;
        var castle = CastlesApi.ForTerritory(4);
        Assert.Equal(7, castle.StructureRevision);
        _server.Revisions[Heart] = 8;
        Assert.Equal(8, castle.StructureRevision);
        Assert.Equal(0, CastlesApi.ForTerritory(99).StructureRevision);
    }
}
