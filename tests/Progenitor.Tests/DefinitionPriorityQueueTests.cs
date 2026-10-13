using System;
using System.Collections.Generic;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public sealed class DefinitionPriorityQueueTests
{
    [Fact]
    public void CapacityCountsUniquePendingAndDeliveredIdentities()
    {
        var queue = new DefinitionPriorityQueue(2);
        Assert.True(queue.Enqueue(1));
        Assert.True(queue.Enqueue(1));
        Assert.True(queue.Enqueue(2));
        Assert.False(queue.Enqueue(3));
        Assert.Equal(2, queue.PendingCount);
        Assert.True(queue.TryDequeue(out int first));
        Assert.Equal(1, first);
        Assert.True(queue.IsDelivered(1));
        Assert.False(queue.Enqueue(1, DefinitionPriority.InventoryOrEquipment));
        Assert.False(queue.Enqueue(3));
        Assert.True(queue.Forget(1));
        Assert.True(queue.Enqueue(3));
    }

    [Fact]
    public void PromotionOrdersInventoryVisibleNearbyThenBackground()
    {
        var queue = new DefinitionPriorityQueue(10);
        for (int i = 1; i <= 6; i++) queue.Enqueue(i);
        queue.Enqueue(6, DefinitionPriority.InventoryOrEquipment);
        queue.Enqueue(5, DefinitionPriority.ActiveContainerOrVisible);
        queue.Enqueue(4, DefinitionPriority.Nearby, 100);
        queue.Enqueue(3, DefinitionPriority.Nearby, 4);
        queue.Enqueue(4, DefinitionPriority.Nearby, 1);
        queue.Enqueue(6); // A subsequent background refresh must not demote inventory.
        Assert.Equal(new[] { 6, 5, 4, 3, 1, 2 }, Drain(queue));
    }

    [Fact]
    public void EqualPriorityAndDistanceRetainOriginalInsertionOrderAfterPromotion()
    {
        var queue = new DefinitionPriorityQueue(4);
        queue.Enqueue(3);
        queue.Enqueue(1);
        queue.Enqueue(2);
        queue.Enqueue(2, DefinitionPriority.Nearby, 25);
        queue.Enqueue(3, DefinitionPriority.Nearby, 25);
        queue.Enqueue(1, DefinitionPriority.Nearby, 25);
        Assert.Equal(new[] { 3, 1, 2 }, Drain(queue));
    }

    [Fact]
    public void ContinuousForegroundArrivalsCannotStarveBackground()
    {
        var queue = new DefinitionPriorityQueue(100, foregroundBurst: 3);
        queue.Enqueue(1);
        queue.Enqueue(2);
        var seen = new HashSet<int>();
        for (int i = 0; i < 8; i++)
        {
            queue.Enqueue(100 + i, DefinitionPriority.InventoryOrEquipment);
            Assert.True(queue.TryDequeue(out int next));
            Assert.True(seen.Add(next));
            if (i == 3) Assert.Equal(1, next);
            if (i == 7) Assert.Equal(2, next);
        }
        Assert.False(queue.ContainsPending(1));
        Assert.False(queue.ContainsPending(2));
    }

    [Fact]
    public void TenThousandDefinitionsAreClaimedExactlyOnce()
    {
        var queue = new DefinitionPriorityQueue(10000);
        for (int i = 0; i < 10000; i++) Assert.True(queue.Enqueue(i));
        for (int i = 0; i < 10000; i += 2) queue.Enqueue(i, DefinitionPriority.Nearby, 10000 - i);
        var claimed = Drain(queue);
        Assert.Equal(10000, claimed.Count);
        Assert.Equal(10000, new HashSet<int>(claimed).Count);
        Assert.False(queue.Enqueue(9999));
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void ForgetAndClearPermitReplacementAndNewConnection()
    {
        var queue = new DefinitionPriorityQueue(2);
        queue.Enqueue(1);
        queue.Enqueue(2);
        Assert.True(queue.Forget(2));
        Assert.False(queue.ContainsPending(2));
        Assert.Equal(new[] { 1 }, Drain(queue));
        Assert.True(queue.Forget(1));
        queue.Enqueue(1);
        Assert.Equal(new[] { 1 }, Drain(queue));
        queue.Clear();
        Assert.False(queue.IsDelivered(1));
        Assert.Equal(0, queue.PendingCount);
        Assert.True(queue.Enqueue(1));
        Assert.True(queue.Enqueue(2));
    }

    [Fact]
    public void ResetPrioritiesDropsOldLocationAndReappliesCurrentDemandWithoutResending()
    {
        var queue = new DefinitionPriorityQueue(10);
        queue.Enqueue(1); // Original background age must survive resets.
        queue.Enqueue(2, DefinitionPriority.Nearby, 1); // Old location.
        queue.Enqueue(3);
        queue.Enqueue(4, DefinitionPriority.InventoryOrEquipment);
        queue.Enqueue(5);
        Assert.True(queue.TryDequeue(out int delivered));
        Assert.Equal(4, delivered);
        queue.ResetPriorities();
        queue.ResetPriorities(); // Empty reset is harmless.
        queue.Enqueue(3, DefinitionPriority.Nearby, 25); // New location outranks old nearer hint.
        queue.Enqueue(5, DefinitionPriority.ActiveContainerOrVisible); // Current demand can reapply.
        Assert.False(queue.Enqueue(4, DefinitionPriority.InventoryOrEquipment));
        Assert.Equal(new[] { 5, 3, 1, 2 }, Drain(queue));
    }

    [Fact]
    public void RepeatedPriorityResetPreservesFairnessCycle()
    {
        var queue = new DefinitionPriorityQueue(20, foregroundBurst: 2);
        queue.Enqueue(1);
        for (int i = 0; i < 3; i++)
        {
            queue.ResetPriorities();
            queue.Enqueue(10 + i, DefinitionPriority.InventoryOrEquipment);
            Assert.True(queue.TryDequeue(out int next));
            Assert.Equal(i == 2 ? 1 : 10 + i, next);
        }
        Assert.True(queue.IsDelivered(1));
    }

    [Fact]
    public void InvalidBoundsAndPrioritiesAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DefinitionPriorityQueue(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DefinitionPriorityQueue(1, 0));
        var queue = new DefinitionPriorityQueue(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(1, (DefinitionPriority)4));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(1, DefinitionPriority.Nearby, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Enqueue(1, DefinitionPriority.Nearby, -1));
    }

    private static List<int> Drain(DefinitionPriorityQueue queue)
    {
        var result = new List<int>();
        while (queue.TryDequeue(out int guid)) result.Add(guid);
        return result;
    }
}
