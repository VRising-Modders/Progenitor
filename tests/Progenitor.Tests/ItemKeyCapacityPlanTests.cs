using System;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public sealed class ItemKeyCapacityPlanTests
{
    [Fact]
    public void GrowthPreservesSignedHashesAndResolvesCollisionChains()
    {
        int[] hashes = { 0, 16, -16, int.MinValue, int.MaxValue };
        Assert.True(ItemKeyCapacityPlan.TryBuild(hashes, 8, 8, 9, out var plan, out var error), error);
        Assert.NotNull(plan);
        Assert.Equal(16, plan.TargetCapacity);
        Assert.Equal(hashes, plan.ListHashes);
        Assert.Equal(80, plan.MapData.Length);
        for (int row = 0; row < hashes.Length; row++)
            Assert.Equal(row, Lookup(plan, hashes[row]));
        Assert.Equal(-1, Lookup(plan, 42));
        Assert.All(plan.MapData.AsSpan(32 + hashes.Length, 16 - hashes.Length).ToArray(),
            value => Assert.Equal(-1, value));
        hashes[0] = 99;
        Assert.Equal(0, plan.ListHashes[0]);
    }

    [Theory]
    [InlineData(4, 8, 5, 8)]
    [InlineData(8, 4, 5, 8)]
    [InlineData(2048, 2048, 4096, 4096)]
    [InlineData(2048, 2048, 11087, 16384)]
    [InlineData(8192, 8192, 16384, 16384)]
    public void UsesPowerOfTwoCoveringBothContainers(int list, int map, int required, int target)
    {
        Assert.True(ItemKeyCapacityPlan.TryBuild(new[] { 0 }, list, map, required,
            out var plan, out var error), error);
        Assert.NotNull(plan);
        Assert.Equal(target, plan.TargetCapacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void SufficientCapacityIsSuccessfulNoOp(int required)
    {
        Assert.True(ItemKeyCapacityPlan.TryBuild(new[] { 0 }, 4, 8, required,
            out var plan, out var error), error);
        Assert.Null(plan);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData(0, 4, 5)]
    [InlineData(-4, 4, 5)]
    [InlineData(3, 4, 5)]
    [InlineData(4, 3, 5)]
    [InlineData(32768, 4, 5)]
    [InlineData(4, 4, -1)]
    [InlineData(4, 4, 16385)]
    public void RefusesInvalidOrExcessiveCapacity(int list, int map, int required)
    {
        Assert.False(ItemKeyCapacityPlan.TryBuild(new[] { 0 }, list, map, required,
            out var plan, out var error));
        Assert.Null(plan);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void RefusesCountsThatDoNotFitEitherContainer()
    {
        Assert.False(ItemKeyCapacityPlan.TryBuild(new[] { 1, 2, 3 }, 2, 4, 4, out _, out _));
        Assert.False(ItemKeyCapacityPlan.TryBuild(new[] { 1, 2, 3 }, 4, 2, 4, out _, out _));
        Assert.False(ItemKeyCapacityPlan.TryBuild(null!, 4, 4, 5, out _, out _));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void RejectsDuplicateHashesEvenWhenGrowthIsUnneeded(int required)
    {
        Assert.False(ItemKeyCapacityPlan.TryBuild(new[] { 0, 0 }, 4, 4, required,
            out var plan, out var error));
        Assert.Null(plan);
        Assert.Contains("Duplicate", error);
    }

    [Fact]
    public void TenThousandDefinitionsFitAndGrowthPreservesExistingRows()
    {
        // Deliberate signed collisions exercise rebuilt chains at the experiment's full count.
        var hashes = new int[1087 + 10000];
        for (int i = 0; i < hashes.Length; i++) hashes[i] = unchecked(i * 65537);
        Assert.True(ItemKeyCapacityPlan.TryBuild(hashes, 16384, 16384, hashes.Length,
            out var noOp, out var error), error);
        Assert.Null(noOp);
        Assert.False(ItemKeyCapacityPlan.TryBuild(hashes, 16384, 16384, 16385,
            out _, out _));

        // Expand before adding the remaining rows; all existing rows must retain their indices.
        var prefix = new int[8192];
        Array.Copy(hashes, prefix, prefix.Length);
        Assert.True(ItemKeyCapacityPlan.TryBuild(prefix, 8192, 8192, hashes.Length,
            out var plan, out error), error);
        Assert.NotNull(plan);
        Assert.Equal(16384, plan.TargetCapacity);
        Assert.Equal(prefix, plan.ListHashes);
        for (int row = 0; row < prefix.Length; row++) Assert.Equal(row, Lookup(plan, prefix[row]));
        hashes[^1] = hashes[0];
        Assert.False(ItemKeyCapacityPlan.TryBuild(hashes, 16384, 16384, hashes.Length,
            out _, out error));
        Assert.Contains("Duplicate", error);
    }

    private static int Lookup(ItemKeyCapacityPlan plan, int hash)
    {
        int capacity = plan.TargetCapacity;
        int slot = plan.MapData[capacity * 3 + (hash & (capacity * 2 - 1))];
        int steps = 0;
        while (slot != -1)
        {
            Assert.InRange(slot, 0, plan.ListHashes.Length - 1);
            Assert.True(++steps <= plan.ListHashes.Length, "Collision chain contains a cycle.");
            if (plan.MapData[capacity + slot] == hash) return plan.MapData[slot];
            slot = plan.MapData[capacity * 2 + slot];
        }
        return -1;
    }
}
