using System;
using System.Collections.Generic;
using Progenitor.SharedResourcesTest;
using Xunit;

public sealed class FixtureTests
{
    [Fact]
    public void DisabledBaselineExpectsZeroDebitDespiteRemoteStock()
    {
        var f = Setup(); f.Begin(new[] { "a", "b" }, (s, i) => 10, expectPayment: false);
        Assert.False(f.ExpectedSuccess); Assert.Empty(f.Verify((s, i) => 10));
        Assert.NotEmpty(f.Verify((s, i) => 9));
    }
    private static Fixture Setup()
    {
        var f = new Fixture();
        f.Rank(1, "a", 0); f.Rank(1, "b", 10);
        f.Rank(2, "a", 10); f.Rank(2, "b", 0);
        f.Cost(1, 8, 2); f.Cost(2, 6, 0);
        return f;
    }

    [Fact]
    public void OppositeIngredientPreferencesPayFromDifferentChests()
    {
        var f = Setup();
        Assert.Equal(new[] { "b", "a" }, f.Order(1));
        Assert.Equal(new[] { "a", "b" }, f.Order(2));
        f.Begin(new[] { "a", "b", "bag" }, (s, i) => s == "bag" ? 0 : 10);
        Assert.True(f.ExpectedSuccess);
        Assert.Empty(f.Verify((s, i) => s == "bag" ? 0 : (s, i) switch { ("b", 1) => 2, ("a", 2) => 4, _ => 10 }));
    }

    [Fact]
    public void SingleFixedOrderFailsTheSecondIngredient()
    {
        var f = Setup(); f.Begin(new[] { "a", "b" }, (s, i) => 10);
        Assert.Equal(2, f.Verify((s, i) => s == "b" ? (i == 1 ? 2 : 4) : 10).Length);
    }

    [Fact]
    public void ReserveIsAcrossThePoolNotPerChest()
    {
        var f = new Fixture(); f.Rank(1, "a", 0); f.Rank(1, "b", 1); f.Cost(1, 15, 5);
        f.Begin(new[] { "a", "b" }, (s, i) => 10);
        Assert.True(f.ExpectedSuccess);
        Assert.Empty(f.Verify((s, i) => s == "a" ? 5 : 0));
    }

    [Fact]
    public void OneInsufficientIngredientRequiresZeroDebitForEveryIngredient()
    {
        var f = Setup(); f.Begin(new[] { "a", "b" }, (s, i) => i == 1 ? 10 : 1);
        Assert.False(f.ExpectedSuccess);
        Assert.Empty(f.Verify((s, i) => i == 1 ? 10 : 1));
        Assert.NotEmpty(f.Verify((s, i) => i == 1 ? 6 : 1));
    }

    [Fact]
    public void BagStagingAndDebitingUnselectedSourcesAreMismatches()
    {
        var f = Setup(); f.Begin(new[] { "a", "b", "bag" }, (s, i) => s == "bag" ? 0 : 10);
        Assert.NotEmpty(f.Verify((s, i) => s == "bag" ? 8 : 10));
    }

    [Fact]
    public void InvalidReadsDoNotLeaveAnArmedCapture()
    {
        var f = Setup(); Assert.Throws<InvalidOperationException>(() => f.Begin(new[] { "a", "b" }, (s, i) => -1));
        Assert.False(f.Active);
    }

    [Fact]
    public void CannotReconfigureOrOverwriteActiveEvidence()
    {
        var f = Setup(); f.Begin(new[] { "a", "b" }, (s, i) => 10);
        Assert.Throws<InvalidOperationException>(() => f.Cost(1, 9, 0));
        Assert.Throws<InvalidOperationException>(() => f.Rank(1, "a", 20));
        Assert.Throws<InvalidOperationException>(() => f.Begin(new[] { "a", "b" }, (s, i) => 0));
        f.End(); f.Cost(1, 9, 0); Assert.False(f.Active);
    }

    [Fact]
    public void DuplicateOrUnboundAliasesCannotInflateAvailableStock()
    {
        var f = Setup();
        Assert.Throws<InvalidOperationException>(() => f.Begin(new[] { "a", "a" }, (s, i) => 10));
        Assert.Throws<InvalidOperationException>(() => f.Begin(new[] { "a" }, (s, i) => 10));
    }

    [Fact]
    public void ClearInvalidatesPreviousCaptureAndPolicies()
    {
        var f = Setup(); f.Begin(new[] { "a", "b" }, (s, i) => 10); long r = f.Revision;
        f.Clear(); Assert.False(f.Active); Assert.Empty(f.Order(1)); Assert.Empty(f.Costs);
        Assert.True(f.Revision > r); Assert.Throws<InvalidOperationException>(() => f.Verify((s, i) => 0));
    }

    [Theory]
    [InlineData(0, 1, 0)] [InlineData(1, 0, 0)] [InlineData(1, -1, 0)] [InlineData(1, 1, -1)]
    public void InvalidCostsAreRejected(int item, int amount, int reserve) =>
        Assert.Throws<ArgumentException>(() => new Fixture().Cost(item, amount, reserve));

    [Fact]
    public void CountsLargerThanIntAcrossThePoolDoNotOverflow()
    {
        var f = new Fixture(); f.Rank(1, "a", 0); f.Rank(1, "b", 1); f.Cost(1, int.MaxValue, int.MaxValue);
        f.Begin(new[] { "a", "b" }, (s, i) => int.MaxValue);
        Assert.True(f.ExpectedSuccess); Assert.Empty(f.Verify((s, i) => s == "b" ? 0 : int.MaxValue));
    }
}
