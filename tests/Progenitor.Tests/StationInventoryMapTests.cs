using System.Collections.Generic;
using Progenitor.Castles;
using Progenitor.Inventory;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of how a station's input and output inventories are found: the station
/// component's input/output entities matched against the owner's inventory instance rows, and
/// how castle station rows carry the result. Entity ids are placeholders.
/// </summary>
[Collection("PluginLogger")]
public class StationInventoryMapTests
{
    private static readonly long In = StationInventoryMap.Key(900, 1);
    private static readonly long Out = StationInventoryMap.Key(901, 1);

    [Fact]
    public void Key_packs_index_and_version_and_null_is_zero()
    {
        Assert.Equal(0, StationInventoryMap.Key(0, 0));
        Assert.NotEqual(StationInventoryMap.Key(5, 1), StationInventoryMap.Key(5, 2));
        Assert.NotEqual(StationInventoryMap.Key(1, 5), StationInventoryMap.Key(5, 1));
    }

    [Fact]
    public void Instance_of_is_the_matching_row_and_refuses_null_missing_or_duplicate()
    {
        var rows = new List<long> { In, Out };
        Assert.Equal(0, StationInventoryMap.InstanceOf(In, rows));
        Assert.Equal(1, StationInventoryMap.InstanceOf(Out, rows));
        Assert.Equal(-1, StationInventoryMap.InstanceOf(0, rows));
        Assert.Equal(-1, StationInventoryMap.InstanceOf(StationInventoryMap.Key(7, 1), rows));
        Assert.Equal(-1, StationInventoryMap.InstanceOf(In, new List<long> { In, In }));
        Assert.Equal(-1, StationInventoryMap.InstanceOf(In, null));
    }

    [Fact]
    public void A_model_without_a_station_component_has_instance_0_and_no_output()
    {
        var r = StationInventoryMap.Resolve(StationInventoryKind.None, In, Out, new List<long> { In, Out });
        Assert.Equal(0, r.Input);
        Assert.Equal(-1, r.Output);
    }

    [Fact]
    public void Salvage_output_is_the_row_holding_the_component_output_whatever_its_index()
    {
        var spawned = StationInventoryMap.Resolve(StationInventoryKind.Salvage, In, Out, new List<long> { In, Out });
        Assert.Equal(0, spawned.Input);
        Assert.Equal(1, spawned.Output);

        var swapped = StationInventoryMap.Resolve(StationInventoryKind.Salvage, In, Out, new List<long> { Out, In });
        Assert.Equal(1, swapped.Input);
        Assert.Equal(0, swapped.Output);
    }

    [Fact]
    public void Salvage_with_an_unmatched_output_has_no_output()
    {
        var r = StationInventoryMap.Resolve(StationInventoryKind.Salvage, In, 0, new List<long> { In, Out });
        Assert.Equal(0, r.Input);
        Assert.Equal(-1, r.Output);
        var same = StationInventoryMap.Resolve(StationInventoryKind.Salvage, In, In, new List<long> { In, Out });
        Assert.Equal(-1, same.Output);
    }

    [Fact]
    public void Refinement_matches_the_component_and_keeps_instance_1_when_unmatched()
    {
        var spawned = StationInventoryMap.Resolve(StationInventoryKind.Refinement, In, Out, new List<long> { In, Out });
        Assert.Equal((0, 1), (spawned.Input, spawned.Output));

        var unreadable = StationInventoryMap.Resolve(StationInventoryKind.Refinement, 0, 0, new List<long> { In, Out });
        Assert.Equal((0, 1), (unreadable.Input, unreadable.Output));

        var swapped = StationInventoryMap.Resolve(StationInventoryKind.Refinement, In, Out, new List<long> { Out, In });
        Assert.Equal((1, 0), (swapped.Input, swapped.Output));
    }

    [Fact]
    public void Castle_rows_carry_the_reported_instances_and_keep_legacy_refinement_output()
    {
        static CastleBuildingInfo Model(int index) => new() { Entity = new CastleId(index, 1), PrefabGuid = index, PrefabName = "TM_Placeholder_" + index };
        var rows = new List<CastleInventoryRow>
        {
            new() { Building = Model(10), IsStation = true, IsRefinement = true },                                       // runtime did not say
            new() { Building = Model(11), IsStation = true, InputInstance = 1, OutputInstance = 0 },                  // salvage, rows swapped
            new() { Building = Model(12), IsStation = true, OutputInstance = -1 },                                    // crafting station
            new() { Building = Model(13), IsStation = true, IsRefinement = true, InputInstance = 0, OutputInstance = 1 },
        };

        var built = CastleContainerRows.Build(rows, 4, stations: true, epoch: 3);
        Assert.Equal(4, built.Count);
        Assert.Equal(new InventoryRef(10, 1, 0, 3), built[0].Main);
        Assert.Equal(new InventoryRef(10, 1, 1, 3), built[0].Output);
        Assert.Equal(new InventoryRef(11, 1, 1, 3), built[1].Main);
        Assert.Equal(new InventoryRef(11, 1, 0, 3), built[1].Output);
        Assert.False(built[1].IsRefinement);
        Assert.Contains("(input+output)", built[1].ToString());
        Assert.True(built[2].Output.IsNone);
        Assert.DoesNotContain("(input+output)", built[2].ToString());
        Assert.Equal(new InventoryRef(13, 1, 1, 3), built[3].Output);
    }
}
