using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class WearableModelVisibilityTests
{
    [Fact]
    public void HiddenBodyConcealsOverlays()
        // A dead player awaiting respawn: the game disables Body_GEO.
        => Assert.False(WearableModelVisibility.Visible(bodies: 1, shown: 0));

    [Fact]
    public void ShownBodyKeepsOverlays()
        => Assert.True(WearableModelVisibility.Visible(bodies: 1, shown: 1));

    [Fact]
    public void AnyShownBodyKeepsOverlays()
        => Assert.True(WearableModelVisibility.Visible(bodies: 2, shown: 1));

    [Fact]
    public void ModelWithoutBodyRendererStaysVisible()
        => Assert.True(WearableModelVisibility.Visible(bodies: 0, shown: 0));

    [Theory]
    [InlineData("Body_GEO", false, true)]
    [InlineData("Body_GEO", true, false)]
    [InlineData("MEBody_GEO", false, false)]
    [InlineData("body_geo", false, false)]
    [InlineData("RazerHood_GEO", false, false)]
    [InlineData(null, false, false)]
    public void OnlyNativeBodyRendererDecides(string? name, bool ours, bool expected)
        => Assert.Equal(expected, WearableModelVisibility.IsNativeBody(name, ours));

    [Fact]
    public void BodyRendererCannotBeHiddenByAWearable()
    {
        // The decision relies on Body_GEO being the game's state, never a wearable hide.
        Assert.False(WearableHide.ShouldHide(WearableModelVisibility.BodyRendererName,
            new[] { "Body", "Hair", "RazerHood" }, WearableSlot.Headgear));
    }
}
