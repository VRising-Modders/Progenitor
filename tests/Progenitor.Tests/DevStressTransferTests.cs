using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

public class DevStressTransferTests
{
    [Theory]
    [InlineData(45)] [InlineData(67)] [InlineData(86)] [InlineData(92)]
    [InlineData(125)] [InlineData(129)] [InlineData(136)]
    public void OriginalCastlesAlwaysRefused(int plot) =>
        Assert.Equal("not_stress_castle", StressTransferRules.Refusal(plot, true, true, true, true, true, true, 0, 0, 5));

    [Fact]
    public void IncompleteOrForeignOwnershipCannotReachNativeClaim()
    {
        Assert.Equal("not_stress_castle", Decide(oldStress: false));
        Assert.Equal("target_identity", Decide(allowed: false));
        Assert.Equal("target_shape", Decide(complete: false));
        Assert.Equal("connected", Decide(offline: false));
        Assert.Equal("clan_not_supported", Decide(noClan: false));
        Assert.Equal("team_links", Decide(links: false));
        Assert.Equal("heart_count_mismatch", Decide(count: null));
        Assert.Equal("heart_count_mismatch", Decide(count: 1));
        Assert.Equal("castle_limit", Decide(limit: 0));
        Assert.Null(Decide());
    }

    [Fact]
    public void LastAllowedHeartFitsButNextOneDoesNot()
    {
        Assert.Null(StressTransferRules.Refusal(0, true, true, true, true, true, true, 4, 4, 5));
        Assert.Equal("castle_limit", StressTransferRules.Refusal(0, true, true, true, true, true, true, 5, 5, 5));
    }

    [Theory]
    [InlineData(76561198000000000UL, false)]
    [InlineData(90000000000020000UL, false)]
    [InlineData(90000000000020010UL, true)]
    [InlineData(90000000000020099UL, true)]
    [InlineData(90000000000020100UL, false)]
    [InlineData(90000000000101810UL, false)]
    [InlineData(90000000000020001UL, false)]
    [InlineData(90000000000020002UL, false)]
    [InlineData(90000000000020003UL, false)]
    [InlineData(90000000000020009UL, false)]
    public void IdentityBandsStaySynthetic(ulong id, bool expected) => Assert.Equal(expected, StressTransferRules.AllowedIdentity(id));

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    [InlineData(8, true)]
    [InlineData(-1, false)]
    public void CountReadUsesVerifiedLayoutWhenSizeIsUnknown(int size, bool expected) =>
        Assert.Equal(expected, StressTransferRules.CanReadCount(() => size, 4));

    [Fact]
    public void UnavailableTypeInfoDoesNotHidePresentHeartCount() =>
        Assert.True(StressTransferRules.CanReadCount(() => throw new System.InvalidOperationException(), 4));

    private static string? Decide(bool oldStress = true, bool allowed = true, bool complete = true,
        bool offline = true, bool noClan = true, bool links = true, int? count = 0, int limit = 5) =>
        StressTransferRules.Refusal(0, oldStress, allowed, complete, offline, noClan, links, 0, count, limit);
}
