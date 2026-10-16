using Progenitor.Stations;
using Xunit;

namespace Progenitor.Tests;

public class StationRedistributionOwnershipTests
{
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public void Only_known_disabled_routing_allows_competing_automation(bool known, bool enabled, bool suspended)
    {
        var read = new StationRedistributionOwnership(known, enabled, known ? "" : "unreadable");
        Assert.Equal(suspended, read.SuspendAutomation);
        Assert.Equal(known, read.Known);
        Assert.Equal(enabled, read.AutoRouteItems);
        if (!known) Assert.NotEmpty(read.Reason);
    }
}
