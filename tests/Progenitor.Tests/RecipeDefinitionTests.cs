using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of <c>DefineRecipe</c>: builder validation, deterministic GUID, limits,
/// apply-plan shape, and the station-row ledger (unregister restores vanilla rows).
/// </summary>
[Collection("RecipeApply")]
public class RecipeDefinitionTests : IDisposable
{
    public RecipeDefinitionTests()
    {
        RecipeApply.ResetForTests();
    }

    public void Dispose()
    {
        RecipeApply.ResetForTests();
    }

    private static readonly object Owner = new();

    private static RecipeDefinitionBuilder Valid(string id = "sample.ember.craft") =>
        RecipeDefinition.Define(id)
            .CloneOf("Recipe_Ingredient_CoarseThread")
            .Output("sample.ember", 1)
            .Input("Item_Ingredient_Crystal", 4)
            .At(RecipeStationKind.Workstation, "TM_CraftingStation_SimpleCraftingBench")
            .CraftTime(8f)
            .HudOrder(1000)
            .AlwaysUnlocked();

    [Fact]
    public void Register_stores_definition_and_deterministic_guid()
    {
        var handle = Valid().Register(Owner);
        Assert.True(RecipeApply.TryGet("sample.ember.craft", out var def));
        Assert.Equal("sample.ember.craft", def.Id);
        Assert.Equal(RecipeDefinition.GuidHashOf("sample.ember.craft"), def.GuidHash);
        Assert.NotEqual(0, def.GuidHash);
        Assert.Equal(8f, def.CraftDuration);
        Assert.Equal(1000, def.HudSortingOrder);
        Assert.True(def.AlwaysUnlocked);
        Assert.Equal(1, def.Output.Amount);
        Assert.Equal("sample.ember", def.Output.Name);
        var input = Assert.Single(def.Inputs);
        Assert.Equal("Item_Ingredient_Crystal", input.Name);
        Assert.Equal(4, input.Amount);
        Assert.Equal(RecipeStationKind.Workstation, def.Stations[0].Kind);
        Assert.Equal("TM_CraftingStation_SimpleCraftingBench", def.Stations[0].Name);
        handle.Dispose();
    }

    [Fact]
    public void Same_id_hashes_the_same_across_builders()
    {
        int a = RecipeDefinition.GuidHashOf("sample.ember.craft");
        int b = RecipeDefinition.GuidHashOf("sample.ember.craft");
        int c = RecipeDefinition.GuidHashOf("sample.cinder.craft");
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Live_guid_path_is_CalculateGuidHash()
    {
        var src = ReadContent("RecipeDefinition.cs");
        SourcePin.AssertUncommented(src, "PrefabGUID.CalculateGuidHash(id)");
    }

    [Fact]
    public void Missing_CloneOf_is_refused_at_Register()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RecipeDefinition.Define("sample.ember.craft")
                .Output("sample.ember", 1)
                .Input("Item_Ingredient_Crystal", 4)
                .At(RecipeStationKind.Workstation, "TM_CraftingStation_SimpleCraftingBench")
                .CraftTime(8f)
                .Register(Owner));
        Assert.Contains("CloneOf", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_Output_is_refused_at_Register()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RecipeDefinition.Define("sample.ember.craft")
                .CloneOf("Recipe_Ingredient_CoarseThread")
                .Input("Item_Ingredient_Crystal", 4)
                .At(RecipeStationKind.Workstation, "TM_CraftingStation_SimpleCraftingBench")
                .CraftTime(8f)
                .Register(Owner));
        Assert.Contains("exactly one Output", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_Outputs_throw()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Valid().Output("sample.cinder", 1));
        Assert.Contains("exactly one output", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Nine_inputs_are_refused_at_Register()
    {
        var builder = RecipeDefinition.Define("sample.ember.craft")
            .CloneOf("Recipe_Ingredient_CoarseThread")
            .Output("sample.ember", 1)
            .At(RecipeStationKind.Workstation, "TM_CraftingStation_SimpleCraftingBench")
            .CraftTime(8f);
        for (int i = 0; i < 9; i++)
            builder.Input("Item_Ingredient_Crystal", 1);
        var ex = Assert.Throws<InvalidOperationException>(() => builder.Register(Owner));
        Assert.Contains("8", ex.Message, StringComparison.Ordinal);
        Assert.Contains("inputs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Five_stations_are_refused_at_Register()
    {
        var builder = RecipeDefinition.Define("sample.ember.craft")
            .CloneOf("Recipe_Ingredient_CoarseThread")
            .Output("sample.ember", 1)
            .Input("Item_Ingredient_Crystal", 4)
            .CraftTime(8f);
        for (int i = 0; i < 5; i++)
            builder.At(RecipeStationKind.Workstation, "TM_CraftingStation_SimpleCraftingBench");
        var ex = Assert.Throws<InvalidOperationException>(() => builder.Register(Owner));
        Assert.Contains("4", ex.Message, StringComparison.Ordinal);
        Assert.Contains("stations", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Zero_craft_time_is_refused_at_Register()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RecipeDefinition.Define("sample.ember.craft")
                .CloneOf("Recipe_Ingredient_CoarseThread")
                .Output("sample.ember", 1)
                .Input("Item_Ingredient_Crystal", 4)
                .At(RecipeStationKind.Workstation, "TM_CraftingStation_SimpleCraftingBench")
                .CraftTime(0f)
                .Register(Owner));
        Assert.Contains("CraftTime", ex.Message, StringComparison.Ordinal);
        Assert.Contains("positive", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Named_is_refused_with_output_item_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Valid().Named("Ember Shard"));
        Assert.Contains("output item", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ManagedRecipeData", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Described_is_refused_with_output_item_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Valid().Described("Warm."));
        Assert.Contains("output item", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Double_Register_throws()
    {
        var builder = Valid();
        builder.Register(Owner);
        var ex = Assert.Throws<InvalidOperationException>(() => builder.Register(Owner));
        Assert.Contains("already been Register", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refinement_station_kind_is_refused_in_this_increment()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RecipeDefinition.Define("sample.ember.craft")
                .CloneOf("Recipe_Ingredient_CoarseThread")
                .Output("sample.ember", 1)
                .Input("Item_Ingredient_Crystal", 4)
                .At(RecipeStationKind.Refinement, "TM_CraftingStation_SimpleCraftingBench")
                .CraftTime(8f)
                .Register(Owner));
        Assert.Contains("Workstation", ex.Message, StringComparison.Ordinal);
        Assert.Contains("phase 2", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_plan_has_clone_buffers_maps_and_station_rows()
    {
        Valid().Register(Owner);
        Assert.True(RecipeApply.TryGet("sample.ember.craft", out var def));
        var steps = def.Plan.Steps;
        Assert.Contains(RecipeApplyPlan.CloneSource, steps);
        Assert.Contains(RecipeApplyPlan.WriteRecipeData, steps);
        Assert.Contains(RecipeApplyPlan.RewriteOutputBuffer, steps);
        Assert.Contains(RecipeApplyPlan.RewriteRequirementBuffer, steps);
        Assert.Contains(RecipeApplyPlan.ClearOutputUnitBuffer, steps);
        Assert.Contains(RecipeApplyPlan.ClearLinkBuffer, steps);
        Assert.Contains(RecipeApplyPlan.InsertPrefabMaps, steps);
        Assert.Contains(RecipeApplyPlan.InsertRecipeHashLookup, steps);
        Assert.Contains(RecipeApplyPlan.InsertOriginalDurations, steps);
        Assert.Contains(RecipeApplyPlan.AppendStationPrefabRow, steps);
        Assert.Contains(RecipeApplyPlan.AppendStationLiveInstances, steps);
        Assert.Equal(0, IndexOf(steps, RecipeApplyPlan.CloneSource));
        Assert.True(IndexOf(steps, RecipeApplyPlan.WriteRecipeData) > IndexOf(steps, RecipeApplyPlan.CloneSource));
        Assert.True(IndexOf(steps, RecipeApplyPlan.AppendStationPrefabRow) > IndexOf(steps, RecipeApplyPlan.InsertRecipeHashLookup));
    }

    [Fact]
    public void Unapply_plan_is_the_inverse_of_apply()
    {
        Valid().Register(Owner);
        Assert.True(RecipeApply.TryGet("sample.ember.craft", out var def));
        Assert.True(RecipeApplyPlan.IsInverseOf(def.Plan.Steps, def.Plan.UnapplySteps));
        Assert.Equal(RecipeApplyPlan.RemoveStationLiveInstances, def.Plan.UnapplySteps[0]);
        Assert.Equal(RecipeApplyPlan.RemoveStationPrefabRow, def.Plan.UnapplySteps[1]);
        Assert.Equal(RecipeApplyPlan.DestroyClone, def.Plan.UnapplySteps[def.Plan.UnapplySteps.Count - 1]);
        Assert.DoesNotContain("vanilla", string.Join(",", def.Plan.UnapplySteps), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Dispose_drops_the_registration()
    {
        var handle = Valid().Register(Owner);
        Assert.True(RecipeApply.TryGet("sample.ember.craft", out _));
        handle.Dispose();
        Assert.False(RecipeApply.TryGet("sample.ember.craft", out _));
        handle.Dispose();
    }

    [Fact]
    public void Same_guid_replace_keeps_one_entry()
    {
        Valid().Register(new object());
        Valid().Register(Owner);
        int count = 0;
        foreach (var d in RecipeApply.Definitions)
            if (d.Id == "sample.ember.craft") count++;
        Assert.Equal(1, count);
        Assert.True(RecipeApply.TryGet("sample.ember.craft", out var def));
        Assert.Same(Owner, def.Owner);
    }

    [Fact]
    public void Ledger_append_is_idempotent_and_claims_when_present()
    {
        var ledger = new RecipeStationLedger();
        var buffer = new List<int> { 10, 20 };
        var before = ledger.SnapshotGuids(buffer);
        RecipeStationLedger.ApplyAppend(buffer, ledger, station: 1, recipe: 99, owner: 1);
        Assert.Equal(new[] { 10, 20, 99 }, buffer.ToArray());
        RecipeStationLedger.ApplyAppend(buffer, ledger, station: 1, recipe: 99, owner: 1);
        Assert.Equal(new[] { 10, 20, 99 }, buffer.ToArray());
        RecipeStationLedger.ApplyAppend(buffer, ledger, station: 1, recipe: 20, owner: 1);
        Assert.Equal(new[] { 10, 20, 99 }, buffer.ToArray());
        Assert.Equal(before, new[] { 10, 20 });
    }

    [Fact]
    public void Ledger_remove_after_vanilla_or_other_mod_present_does_not_delete_the_row()
    {
        var ledger = new RecipeStationLedger();
        var buffer = new List<int> { 20 };
        RecipeStationLedger.ApplyAppend(buffer, ledger, station: 1, recipe: 20, owner: 1);
        Assert.Equal(new[] { 20 }, buffer.ToArray());
        RecipeStationLedger.ApplyRemove(buffer, ledger, station: 1, recipe: 20, owner: 1);
        Assert.Equal(new[] { 20 }, buffer.ToArray());
    }

    [Fact]
    public void Ledger_two_owners_survive_either_unload_order()
    {
        AssertTwoOwnerUnload(firstOut: 1, secondOut: 2);
        AssertTwoOwnerUnload(firstOut: 2, secondOut: 1);
    }

    [Fact]
    public void Ledger_full_unload_restores_pre_mod_contents()
    {
        var ledger = new RecipeStationLedger();
        var buffer = new List<int> { 10, 20 };
        var before = (int[])ledger.SnapshotGuids(buffer);
        RecipeStationLedger.ApplyAppend(buffer, ledger, station: 1, recipe: 99, owner: 1);
        RecipeStationLedger.ApplyAppend(buffer, ledger, station: 1, recipe: 88, owner: 2);
        RecipeStationLedger.ApplyRemove(buffer, ledger, station: 1, recipe: 99, owner: 1);
        RecipeStationLedger.ApplyRemove(buffer, ledger, station: 1, recipe: 88, owner: 2);
        Assert.Equal(before, buffer.ToArray());
    }

    [Fact]
    public void Ledger_remove_of_absent_value_is_noop()
    {
        var ledger = new RecipeStationLedger();
        var buffer = new List<int> { 10 };
        RecipeStationLedger.ApplyRemove(buffer, ledger, station: 1, recipe: 99, owner: 1);
        Assert.Equal(new[] { 10 }, buffer.ToArray());
    }

    [Fact]
    public void Ledger_removal_re_resolves_index()
    {
        var ledger = new RecipeStationLedger();
        var buffer = new List<int> { 10, 20 };
        RecipeStationLedger.ApplyAppend(buffer, ledger, station: 1, recipe: 99, owner: 1);
        Assert.Equal(new[] { 10, 20, 99 }, buffer.ToArray());
        buffer.RemoveAt(0);
        RecipeStationLedger.ApplyRemove(buffer, ledger, station: 1, recipe: 99, owner: 1);
        Assert.Equal(new[] { 20 }, buffer.ToArray());
        Assert.DoesNotContain(99, buffer);
    }

    [Fact]
    public void Sample_recipe_is_vanilla_items_at_the_simple_bench()
    {
        Assert.Equal("sample.bench.crystal-thread", Progenitor.Sample.SampleRecipe.Id);
        Assert.Equal("Recipe_Ingredient_CoarseThread", Progenitor.Sample.SampleRecipe.SourceName);
        Assert.Equal("Item_Ingredient_Crystal", Progenitor.Sample.SampleRecipe.InputName);
        Assert.Equal(-257494203, Progenitor.Sample.SampleRecipe.InputGuidHash);
        Assert.Equal(1, Progenitor.Sample.SampleRecipe.InputAmount);
        Assert.Equal("Item_Ingredient_Thread_Coarse", Progenitor.Sample.SampleRecipe.OutputName);
        Assert.Equal(1, Progenitor.Sample.SampleRecipe.OutputAmount);
        Assert.Equal("TM_CraftingStation_SimpleCraftingBench", Progenitor.Sample.SampleRecipe.StationName);
        Assert.Equal(8f, Progenitor.Sample.SampleRecipe.CraftDuration);
        Assert.Equal(1000, Progenitor.Sample.SampleRecipe.HudOrder);
        Assert.Equal("sample.ember.craft", Progenitor.Sample.SampleRecipe.CustomId);
        Assert.Equal("sample.ember", Progenitor.Sample.SampleRecipe.CustomOutputId);

        var plugin = ReadRepo("samples", "Progenitor.Sample", "Plugin.cs");
        SourcePin.AssertUncommented(plugin, "SampleRecipe.Register(ctx)");
        Assert.DoesNotContain("RegisterCustomEmber", plugin, StringComparison.Ordinal);
        var sample = ReadRepo("samples", "Progenitor.Sample", "SampleRecipe.cs");
        SourcePin.AssertUncommented(sample, "plugin.Content.DefineRecipe");
        SourcePin.AssertUncommented(sample, "RecipeStationKind.Workstation");
        SourcePin.AssertUncommented(sample, ".Output(OutputName, OutputAmount)");
        SourcePin.AssertUncommented(sample, ".Input(InputName, InputAmount)");
        SourcePin.AssertUncommented(sample, "internal static IDisposable RegisterCustomEmber");
    }

    [Fact]
    public void Write_RecipeData_patches_fields_in_place_not_whole_struct()
    {
        // Live 2026-09-24: SetComponentDataRaw refuses the typed Write<RecipeData> on size, and
        // SetComponentData<RecipeData> has no IL2CPP instantiation. Fields go in at dump offsets.
        var src = ReadContent("RecipeApply.cs");
        SourcePin.AssertUncommented(src, "em.GetComponentDataRawRW(clone, ComponentIndex.IndexOf<RecipeData>())");
        SourcePin.AssertUncommented(src, "Layout.RecipeDataCraftDurationOffset");
        SourcePin.AssertUncommented(src, "Layout.RecipeDataGuidOffset");
        Assert.DoesNotContain("clone.Write(em, data)", src, StringComparison.Ordinal);
        Assert.DoesNotContain("em.SetComponentData<RecipeData>", src, StringComparison.Ordinal);
        SourcePin.AssertUncommented(src, "GetBuffer<RecipeRequirementBuffer>");
        SourcePin.AssertUncommented(src, "GetBuffer<RecipeOutputBuffer>");
        SourcePin.AssertUncommented(src, "GetBuffer<WorkstationRecipesBuffer>");
        SourcePin.AssertUncommented(src, "Layout.WorkstationRecipesBufferStride");
        SourcePin.AssertUncommented(src, "RecipeBufferLayout.WriteOutput");
        SourcePin.AssertUncommented(src, "RecipeBufferLayout.WriteRequirement");
        SourcePin.AssertUncommented(src, "RollbackApply");
        SourcePin.AssertUncommented(src, "internal static void Uninstall()");
        SourcePin.AssertUncommented(src, "Plugin.Frame -= OnFrameRetry");
        SourcePin.AssertUncommented(src, "refusing to write a vanilla recipe GUID");
    }

    [Fact]
    public void Register_refuses_reusing_the_source_recipe_guid()
    {
        int source = RecipeDefinition.GuidHashOf("sample.collide");
        var ex = Assert.Throws<InvalidOperationException>(() =>
            RecipeDefinition.Define("sample.collide")
                .CloneOf(source)
                .Output("Item_Ingredient_Thread_Coarse", 1)
                .Input("Item_Ingredient_Crystal", 1)
                .At(RecipeStationKind.Workstation, "TM_CraftingStation_SimpleCraftingBench")
                .CraftTime(8f)
                .Register(Owner));
        Assert.Contains("must not reuse the source recipe GUID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Uninstall_drops_remaining_registrations()
    {
        Valid().Register(Owner);
        Assert.True(RecipeApply.TryGet("sample.ember.craft", out _));
        RecipeApply.Uninstall();
        Assert.False(RecipeApply.TryGet("sample.ember.craft", out _));
        Assert.Equal(0, RecipeApply.TestLedger.Count);
        RecipeApply.Uninstall();
    }

    [Fact]
    public void A_new_client_world_forgets_station_claims_but_keeps_registrations()
    {
        // FSCOPE1: a claim recorded in the first join's world made the next world's re-clone
        // skip the WorkstationRecipesBuffer row (RecordAppend is idempotent per claim).
        Valid().Register(Owner);
        Assert.True(RecipeApply.TestLedger.RecordAppend(1, 2, 3, guidAlreadyPresent: false));
        Assert.False(RecipeApply.TestLedger.RecordAppend(1, 2, 3, guidAlreadyPresent: false));

        RecipeApply.ForgetClientWorld();

        Assert.Equal(0, RecipeApply.TestLedger.Count);
        Assert.True(RecipeApply.TestLedger.RecordAppend(1, 2, 3, guidAlreadyPresent: false));
        Assert.True(RecipeApply.TryGet("sample.ember.craft", out _));
    }

    [Fact]
    public void Facade_exposes_DefineRecipe()
    {
        var src = ReadRepo("src", "Progenitor.Core", "Plugins", "PluginContext.cs");
        SourcePin.AssertStatement(src, "public RecipeDefinitionBuilder DefineRecipe(string id)");
        SourcePin.AssertUncommented(src, "RecipeDefinition.Define(id)");
        SourcePin.AssertUncommented(src, "builder.BindPlugin(_ctx.Owner, _ctx.Track)");
    }

    [Fact]
    public void Layout_declares_recipe_buffer_strides()
    {
        Assert.Equal(8, Layout.RecipeRequirementBufferStride);
        Assert.Equal(8, Layout.RecipeOutputBufferStride);
        Assert.Equal(4, Layout.WorkstationRecipesBufferStride);
        Assert.Equal(0, Layout.RecipeRequirementBufferGuidOffset);
        Assert.Equal(4, Layout.RecipeRequirementBufferAmountOffset);
        Assert.Equal(0, Layout.WorkstationRecipesBufferRecipeGuidOffset);
    }

    private static void AssertTwoOwnerUnload(int firstOut, int secondOut)
    {
        var ledger = new RecipeStationLedger();
        var buffer = new List<int> { 10 };
        RecipeStationLedger.ApplyAppend(buffer, ledger, station: 1, recipe: 99, owner: 1);
        RecipeStationLedger.ApplyAppend(buffer, ledger, station: 1, recipe: 99, owner: 2);
        Assert.Equal(new[] { 10, 99 }, buffer.ToArray());
        RecipeStationLedger.ApplyRemove(buffer, ledger, station: 1, recipe: 99, owner: firstOut);
        Assert.Equal(new[] { 10, 99 }, buffer.ToArray());
        RecipeStationLedger.ApplyRemove(buffer, ledger, station: 1, recipe: 99, owner: secondOut);
        Assert.Equal(new[] { 10 }, buffer.ToArray());
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }

    private static string ReadContent(string fileName) =>
        ReadRepo("src", "Progenitor.Core", "Content", fileName);

    private static string ReadRepo(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }
}
