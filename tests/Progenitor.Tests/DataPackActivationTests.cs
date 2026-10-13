using System;
using System.Collections.Generic;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public sealed class DataPackActivationTests
{
    [Fact]
    public void Large_activation_is_bounded_by_item_count_and_completes_in_order()
    {
        var applied = new List<int>();
        var job = new DataPackActivation(960, applied.Add, _ => throw new Exception("unexpected undo"));
        job.Advance(() => 0);
        Assert.Equal(DataPackActivation.ItemsPerFrame, applied.Count);
        Assert.False(job.Complete);
        for (int frame = 1; frame < 120; frame++) job.Advance(() => 0);
        Assert.True(job.Complete);
        Assert.Equal(960, applied.Count);
        for (int i = 0; i < applied.Count; i++) Assert.Equal(i, applied[i]);
    }

    [Fact]
    public void Slow_operation_finishes_but_next_operation_waits_for_next_frame()
    {
        double clock = 0;
        int applied = 0;
        var job = new DataPackActivation(3, _ => { applied++; clock += 10; }, _ => { });
        job.Advance(() => clock);
        Assert.Equal(1, applied);
        Assert.False(job.Complete);
        job.Advance(() => clock);
        Assert.Equal(2, applied);
    }

    [Fact]
    public void Failed_operation_and_earlier_operations_roll_back_in_reverse_across_frames()
    {
        var applied = new List<int>();
        var undone = new List<int>();
        double clock = 0;
        var job = new DataPackActivation(30, i =>
        {
            applied.Add(i);
            if (i == 12) throw new InvalidOperationException("registration refused");
        }, i => { undone.Add(i); clock += 5; });
        job.Advance(() => clock);
        job.Advance(() => clock);
        Assert.NotNull(job.Failure);
        Assert.False(job.Complete);
        Assert.Equal(new[] { 12 }, undone);
        while (!job.Complete) job.Advance(() => clock);
        Assert.Equal(13, applied.Count);
        Assert.Equal(13, undone.Count);
        for (int i = 0; i < undone.Count; i++) Assert.Equal(12 - i, undone[i]);
        int count = undone.Count;
        job.Advance(() => clock);
        Assert.Equal(count, undone.Count);
    }

    [Fact]
    public void Synchronous_startup_ignores_frame_budgets_and_finishes_rollback()
    {
        var undone = new List<int>();
        double clock = 0;
        var job = new DataPackActivation(960, i =>
        {
            clock += 10;
            if (i == 959) throw new Exception("last item failed");
        }, undone.Add);
        job.Advance(() => clock, synchronous: true);
        Assert.True(job.Complete);
        Assert.Equal(960, undone.Count);
        Assert.Equal(959, undone[0]);
        Assert.Equal(0, undone[959]);
    }

    [Fact]
    public void Rollback_failure_does_not_leave_earlier_changed_items_unvisited()
    {
        var undone = new List<int>();
        var job = new DataPackActivation(3, i => { if (i == 2) throw new Exception("apply"); },
            i => { undone.Add(i); if (i == 1) throw new Exception("undo"); });
        job.Advance(() => 0, synchronous: true);
        Assert.True(job.Complete);
        Assert.NotNull(job.RollbackFailure);
        Assert.Equal(new[] { 2, 1, 0 }, undone);
    }

    [Fact]
    public void Empty_activation_is_complete_without_running_callbacks()
    {
        var job = new DataPackActivation(0, _ => throw new Exception(), _ => throw new Exception());
        job.Advance(() => 0);
        Assert.True(job.Complete);
        Assert.Null(job.Failure);
    }

    [Fact]
    public void Failed_reordered_update_restores_by_identity_and_removes_added_identity()
    {
        var previous = new Dictionary<string, string> { ["a"] = "old-a", ["b"] = "old-b" };
        var working = new Dictionary<string, string>(previous);
        string[] incoming = { "b", "new", "a" };
        var job = new DataPackActivation(incoming.Length, index =>
        {
            string id = incoming[index];
            working.Remove(id);
            if (id == "a") throw new Exception("registration failed after disposing a");
            working[id] = "replacement-" + id;
        }, index =>
        {
            string id = incoming[index];
            working.Remove(id);
            if (previous.TryGetValue(id, out var old)) working[id] = old;
        });
        job.Advance(() => 0, synchronous: true);
        Assert.True(job.Complete);
        Assert.NotNull(job.Failure);
        Assert.Equal(2, working.Count);
        Assert.Equal("old-a", working["a"]);
        Assert.Equal("old-b", working["b"]);
        Assert.False(working.ContainsKey("new"));
    }
}
