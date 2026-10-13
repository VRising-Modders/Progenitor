using System;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Dump-offset writes for recipe buffers. A wrapper sizeof or a swapped Guid/Amount
/// offset fails these round-trips.
/// </summary>
public class RecipeBufferLayoutTests
{
    [Fact]
    public void Requirement_and_output_use_the_dump_verified_layout_constants()
    {
        Assert.Equal(Layout.RecipeRequirementBufferGuidOffset, RecipeBufferLayout.RequirementGuidOffset);
        Assert.Equal(Layout.RecipeRequirementBufferAmountOffset, RecipeBufferLayout.RequirementAmountOffset);
        Assert.Equal(Layout.RecipeRequirementBufferStride, RecipeBufferLayout.RequirementStride);
        Assert.Equal(Layout.RecipeOutputBufferGuidOffset, RecipeBufferLayout.OutputGuidOffset);
        Assert.Equal(Layout.RecipeOutputBufferAmountOffset, RecipeBufferLayout.OutputAmountOffset);
        Assert.Equal(Layout.RecipeOutputBufferStride, RecipeBufferLayout.OutputStride);
        Assert.Equal(Layout.RecipeOutputUnitBufferGuidOffset, RecipeBufferLayout.OutputUnitGuidOffset);
        Assert.Equal(Layout.RecipeOutputUnitBufferStacksOffset, RecipeBufferLayout.OutputUnitStacksOffset);
        Assert.Equal(Layout.RecipeOutputUnitBufferStride, RecipeBufferLayout.OutputUnitStride);
        Assert.Equal(Layout.RecipeLinkBufferGuidOffset, RecipeBufferLayout.LinkGuidOffset);
        Assert.Equal(Layout.RecipeLinkBufferStride, RecipeBufferLayout.LinkStride);
        Assert.Equal(Layout.WorkstationRecipesBufferRecipeGuidOffset, RecipeBufferLayout.WorkstationRecipeGuidOffset);
        Assert.Equal(Layout.WorkstationRecipesBufferStride, RecipeBufferLayout.WorkstationStride);
        Assert.Equal(0, RecipeBufferLayout.RequirementGuidOffset);
        Assert.Equal(4, RecipeBufferLayout.RequirementAmountOffset);
        Assert.Equal(8, RecipeBufferLayout.RequirementStride);
        Assert.Equal(4, RecipeBufferLayout.WorkstationStride);
    }

    [Fact]
    public void Requirement_round_trip_lands_guid_at_offset_0_and_amount_at_offset_4()
    {
        var storage = new byte[RecipeBufferLayout.RequirementBytes(2)];
        RecipeBufferLayout.WriteRequirement(storage, 0, guidHash: -257494203, amount: 4);
        RecipeBufferLayout.WriteRequirement(storage, 1, guidHash: 99, amount: 2);
        Assert.True(RecipeBufferLayout.TryReadRequirement(storage, 0, out int guid0, out int amt0));
        Assert.Equal(-257494203, guid0);
        Assert.Equal(4, amt0);
        Assert.True(RecipeBufferLayout.TryReadRequirement(storage, 1, out int guid1, out int amt1));
        Assert.Equal(99, guid1);
        Assert.Equal(2, amt1);
        Assert.Equal(-257494203, BitConverter.ToInt32(storage, RecipeBufferLayout.RequirementGuidOffset));
        Assert.Equal(4, BitConverter.ToInt32(storage, RecipeBufferLayout.RequirementAmountOffset));
        Assert.Equal(99, BitConverter.ToInt32(storage, RecipeBufferLayout.RequirementStride + RecipeBufferLayout.RequirementGuidOffset));
    }

    [Fact]
    public void Output_round_trip_uses_dump_stride()
    {
        var encoded = RecipeBufferLayout.EncodeOutputs(guidHash: 123, amount: 7);
        Assert.Equal(RecipeBufferLayout.OutputStride, encoded.Length);
        Assert.True(RecipeBufferLayout.TryReadOutput(encoded, 0, out int guid, out int amount));
        Assert.Equal(123, guid);
        Assert.Equal(7, amount);
        Assert.Equal(123, BitConverter.ToInt32(encoded, RecipeBufferLayout.OutputGuidOffset));
        Assert.Equal(7, BitConverter.ToInt32(encoded, RecipeBufferLayout.OutputAmountOffset));
    }

    [Fact]
    public void Unit_and_link_offsets_are_load_bearing_on_clear()
    {
        var units = new byte[RecipeBufferLayout.OutputUnitBytes(1)];
        RecipeBufferLayout.WriteOutputUnit(units, 0, guidHash: 50, stacks: 3);
        Assert.True(RecipeBufferLayout.TryReadOutputUnit(units, 0, out int guid, out int stacks));
        Assert.Equal(50, guid);
        Assert.Equal(3, stacks);
        Assert.Equal(50, BitConverter.ToInt32(units, RecipeBufferLayout.OutputUnitGuidOffset));
        Assert.Equal(3, BitConverter.ToInt32(units, RecipeBufferLayout.OutputUnitStacksOffset));
        var clearedUnits = RecipeBufferLayout.ClearElements(
            units, RecipeBufferLayout.OutputUnitStride, RecipeBufferLayout.OutputUnitGuidOffset);
        Assert.Empty(clearedUnits);

        var links = new byte[RecipeBufferLayout.LinkBytes(2)];
        RecipeBufferLayout.WriteLink(links, 0, 11);
        RecipeBufferLayout.WriteLink(links, 1, 22);
        Assert.True(RecipeBufferLayout.TryReadLink(links, 1, out int link));
        Assert.Equal(22, link);
        Assert.Equal(11, BitConverter.ToInt32(links, RecipeBufferLayout.LinkGuidOffset));
        Assert.Empty(RecipeBufferLayout.ClearElements(
            links, RecipeBufferLayout.LinkStride, RecipeBufferLayout.LinkGuidOffset));
    }

    [Fact]
    public void Workstation_rows_round_trip_and_restore_byte_identical()
    {
        var baseline = RecipeBufferLayout.EncodeWorkstationRows(new[] { 10, 20 });
        var withOwned = RecipeBufferLayout.EncodeWorkstationRows(new[] { 10, 20, 99 });
        Assert.NotEqual(baseline, withOwned);
        var restored = RecipeBufferLayout.EncodeWorkstationRows(new[] { 10, 20 });
        Assert.Equal(baseline, restored);
        Assert.Equal(new[] { 10, 20, 99 }, RecipeBufferLayout.DecodeWorkstationRows(withOwned));
        Assert.Equal(10, BitConverter.ToInt32(baseline, RecipeBufferLayout.WorkstationRecipeGuidOffset));
        Assert.Equal(20, BitConverter.ToInt32(baseline, RecipeBufferLayout.WorkstationStride));
    }
}
