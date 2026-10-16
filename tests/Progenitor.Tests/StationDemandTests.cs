using System;
using System.Collections.Generic;
using Progenitor.Stations;
using Xunit;

namespace Progenitor.Tests;

public class StationDemandTests
{
    [Fact]
    public void Unverified_reservations_are_not_an_empty_known_demand()
    {
        var demand = new StationDemand(StationKind.Workstation,
            new[] { new StationQueuedCraft { RecipeGuid = 42, ProgressSeconds = 2 } }, true, "unverified");
        Assert.False(demand.IsKnown);
        Assert.True(demand.ProtectAllInputs);
        Assert.Single(demand.Queue);
    }

    [Fact]
    public void Queue_snapshot_cannot_be_changed_through_original_list()
    {
        var rows = new List<StationQueuedCraft> { new() { RecipeGuid = 42 } };
        var demand = new StationDemand(StationKind.Workstation, rows, true);
        rows.Clear();
        Assert.Single(demand.Queue);
        Assert.Throws<NotSupportedException>(() => ((IList<StationQueuedCraft>)demand.Queue).Clear());
    }

    [Fact]
    public void Read_failure_never_authorizes_returning_ingredients()
    {
        var demand = new StationDemand(StationKind.Workstation, Array.Empty<StationQueuedCraft>(), false, "unreadable");
        Assert.False(demand.IsKnown);
        Assert.True(demand.ProtectAllInputs);
    }

    [Fact]
    public void Known_empty_queue_has_no_reserved_inputs()
    {
        var demand = new StationDemand(StationKind.Workstation, Array.Empty<StationQueuedCraft>(), false);
        Assert.True(demand.IsKnown);
        Assert.False(demand.ProtectAllInputs);
        Assert.Empty(demand.ReservedInputs);
    }
}
