using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

public sealed class ItemManagementPolicyTests
{
    [Theory]
    [InlineData(2, false, 1, "mod", "restore")]
    [InlineData(1, true, 1, "mod", "restore")]
    [InlineData(1, false, 0, "mod", "restore")]
    [InlineData(1, false, 1, null, "restore")]
    [InlineData(1, false, 1, "mod", null)]
    public void MalformedOrOppositeDirectionRequestsAreRejected(int version, bool response, int nonce, string? owner, string? decision) =>
        Assert.False(ItemManagementPolicy.ValidRequest(version, response, nonce, owner, decision));

    [Fact]
    public void RequestFieldsHaveHardBounds()
    {
        Assert.True(ItemManagementPolicy.ValidRequest(1, false, 1, new string('m', 256), "restore"));
        Assert.False(ItemManagementPolicy.ValidRequest(1, false, 1, new string('m', 257), "restore"));
        Assert.False(ItemManagementPolicy.ValidRequest(1, false, 1, "mod", new string('x', 17)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("restore")]
    [InlineData("retry")]
    [InlineData("keep")]
    [InlineData("discard")]
    public void ReadsAndEveryEditRequireBothHandshakeAndAdmin(string decision)
    {
        string owner = decision.Length == 0 ? "" : "mod";
        Assert.NotEmpty(ItemManagementPolicy.Refusal(false, true, owner, decision));
        Assert.NotEmpty(ItemManagementPolicy.Refusal(true, false, owner, decision));
        Assert.Empty(ItemManagementPolicy.Refusal(true, true, owner, decision));
    }

    [Fact]
    public void DecisionCannotBeUnknownOrNameless()
    {
        Assert.NotEmpty(ItemManagementPolicy.Refusal(true, true, "mod", "delete"));
        Assert.NotEmpty(ItemManagementPolicy.Refusal(true, true, " ", "discard"));
        Assert.NotEmpty(ItemManagementPolicy.Refusal(true, true, "mod", ""));
    }

    [Fact]
    public void ResponseMustMatchAnActiveRequestAndDirection()
    {
        Assert.True(ItemManagementPolicy.MatchesResponse(true, 7, 7));
        Assert.True(ItemManagementPolicy.MatchesResponse(true, 7, 7, "current", "current"));
        Assert.False(ItemManagementPolicy.MatchesResponse(true, 7, 7, "old-core", "current"));
        Assert.False(ItemManagementPolicy.MatchesResponse(true, 7, 7, null, "current"));
        Assert.False(ItemManagementPolicy.MatchesResponse(false, 7, 7));
        Assert.False(ItemManagementPolicy.MatchesResponse(true, 6, 7));
        Assert.False(ItemManagementPolicy.MatchesResponse(true, 0, 0));
        Assert.False(ItemManagementPolicy.MatchesResponse(true, 7, 0));
    }

    [Fact]
    public void ReasonsAreBoundedWithoutBreakingSurrogatePairs()
    {
        Assert.Equal(512, ItemManagementPolicy.Clip(new string('r', 600), 512).Length);
        Assert.Equal(new string('r', 511), ItemManagementPolicy.Clip(new string('r', 511) + "😀x", 512));
        Assert.Equal("", ItemManagementPolicy.Clip(null, 512));
    }
}
