using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class WearableSlotSelectionTests
{
    [Theory]
    [InlineData(-354298506, 114259912, true, false, -354298506)]
    [InlineData(114259912, -354298506, false, true, -354298506)]
    [InlineData(77, 88, false, false, 77)]
    [InlineData(42, 43, true, true, 42)]
    public void CloneIdentitySurvivesDonorItemOrSlot(int slot, int item, bool knownSlot, bool knownItem, int expected)
        => Assert.Equal(expected, WearableSlotSelection.ResolveGuid(slot, item, knownSlot, knownItem));

    [Fact]
    public void CosmeticItemOverridesStatsItem()
    {
        Assert.Equal(42, WearableSlotSelection.Select(11, false, 42, true, false));
        Assert.Equal(0, WearableSlotSelection.Select(11, false, 99, true, true));
    }

    [Fact]
    public void VanillaCosmeticSuppressesCustomStatsOverlay()
    {
        Assert.Equal(77, WearableSlotSelection.Select(11, false, 77, true, false));
    }

    [Fact]
    public void StatsItemAppearsOnlyWithoutCosmeticOverride()
    {
        Assert.Equal(11, WearableSlotSelection.Select(11, false, 0, false, false));
        Assert.Equal(0, WearableSlotSelection.Select(11, true, 0, false, false));
        // A bare cosmetic hide flag does not select that slot in the native pass.
        Assert.Equal(11, WearableSlotSelection.Select(11, false, 0, false, true));
        // Once occupied, the cosmetic still owns the slot over hidden stats gear.
        Assert.Equal(42, WearableSlotSelection.Select(11, true, 42, true, false));
    }
}
