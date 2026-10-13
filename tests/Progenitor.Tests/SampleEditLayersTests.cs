using System;
using Progenitor.Content;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Pins the Sample two-process vanilla-prefab edit so a Core log-format change cannot drift
/// from <c>samples/Progenitor.Sample/SampleEditLayers.cs</c> and the Content section of
/// <c>docs/modules.md</c>.
/// </summary>
public class SampleEditLayersTests
{
    [Fact]
    public void Constants_match_the_documented_conflict_line_and_effective_value()
    {
        Assert.Equal(-257494203, SampleEditLayers.GuidHash);
        Assert.Equal("Item_Ingredient_Crystal", SampleEditLayers.SpawnableName);
        Assert.Equal("ItemData", SampleEditLayers.Component);
        Assert.Equal("ProjectM.ItemData", SampleEditLayers.ComponentFullName);
        Assert.Equal(10, SampleEditLayers.DefaultAmount);
        Assert.Equal(40, SampleEditLayers.LateAmount);
        Assert.Equal(40, SampleEditLayers.ExpectedEffective);
        Assert.Equal(
            SampleEditLayers.ExpectedConflict,
            PrefabEditLog.Conflict(
                SampleEditLayers.GuidHash,
                SampleEditLayers.Component,
                SampleEditLayers.SampleName,
                SampleEditLayers.SampleId,
                SampleEditLayers.LateName,
                SampleEditLayers.LateId,
                PrefabEditLog.ReasonPriority(
                    (int)PrefabEditPriority.Late,
                    (int)PrefabEditPriority.Default)));
        Assert.True(PrefabEditWireTypes.IsVouched(SampleEditLayers.ComponentFullName));
    }

    [Fact]
    public void Register_uses_Content_Edit_then_a_tracked_Late_editor()
    {
        string src = SourcePin.ReadFile("samples", "Progenitor.Sample", "SampleEditLayers.cs");
        SourcePin.AssertStatement(src, "ctx.Content.Edit(Guid, EditDefault, PrefabEditPriority.Default);");
        SourcePin.AssertStatement(src, "ctx.Track(PrefabRegistry.Edit(");
        SourcePin.AssertUncommented(src, "pluginId: LateId");
        SourcePin.AssertUncommented(src, "PrefabEditPriority.Late");
        SourcePin.AssertStatement(src, "data.MaxAmount = maxAmount;");
        SourcePin.AssertStatement(src, "prefab.SetComponent(data);");
        Assert.DoesNotContain("UnsafeHarmony", src, StringComparison.Ordinal);

        string plugin = SourcePin.ReadFile("samples", "Progenitor.Sample", "Plugin.cs");
        SourcePin.AssertStatement(plugin, "SampleEditLayers.Register(ctx);");
    }

    [Fact]
    public void Effective_value_from_the_sample_constants_is_the_Late_write()
    {
        Assert.Equal(
            SampleEditLayers.ExpectedEffective,
            PrefabEditEffective.Int32(
                baseline: 1,
                new[] { SampleEditLayers.DefaultAmount, SampleEditLayers.LateAmount },
                SampleEditLayers.LateAmount));
    }
}
