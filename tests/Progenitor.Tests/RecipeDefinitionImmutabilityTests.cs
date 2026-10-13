using System;
using System.Collections.Generic;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// A registered recipe is a snapshot. Its <c>Inputs</c> and <c>Stations</c> used to be the
/// builder's own lists and the plan's steps the shared static order arrays, so a caller could
/// cast them to <see cref="IList{T}"/>, write past the builder's validation, redirect an already
/// registered recipe to another station, or rewrite every recipe's apply order.
/// </summary>
[Collection("RecipeApply")]
public sealed class RecipeDefinitionImmutabilityTests : IDisposable
{
    private static readonly object Owner = new();

    public RecipeDefinitionImmutabilityTests() => RecipeApply.ResetForTests();

    public void Dispose() => RecipeApply.ResetForTests();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_recipe_consumer_cannot_mutate_the_shared_apply_or_unapply_order(bool unapply)
    {
        using var first = Builder("immutable.plan.first").Register(Owner);
        using var second = Builder("immutable.plan.second").Register(Owner);
        var plan = Get("immutable.plan.first").Plan;
        var otherPlan = Get("immutable.plan.second").Plan;
        var steps = unapply ? plan.UnapplySteps : plan.Steps;
        var otherSteps = unapply ? otherPlan.UnapplySteps : otherPlan.Steps;
        var list = Assert.IsAssignableFrom<IList<string>>(steps);
        string original = list[0];
        try
        {
            Assert.Throws<NotSupportedException>(() => list[0] = "corrupted-by-consumer");
            Assert.Equal(original, otherSteps[0]);
        }
        finally
        {
            // A pre-fix run must not corrupt later tests through the shared array.
            if (steps is string[] array) array[0] = original;
        }
    }

    [Fact]
    public void Registered_inputs_reject_mutation_that_bypasses_builder_validation()
    {
        Builder("immutable.inputs").Register(Owner);
        var definition = Get("immutable.inputs");
        var rows = Assert.IsAssignableFrom<IList<RecipeItemRef>>(definition.Inputs);
        var original = rows[0];
        Assert.Throws<NotSupportedException>(() => rows[0] = default);
        Assert.Throws<NotSupportedException>(() => rows.Clear());
        Assert.Equal(original, Get(definition.Id).Inputs[0]);
    }

    [Fact]
    public void Registered_stations_reject_mutation_that_redirects_an_existing_recipe()
    {
        Builder("immutable.stations").Register(Owner);
        var definition = Get("immutable.stations");
        var rows = Assert.IsAssignableFrom<IList<RecipeStationRef>>(definition.Stations);
        var original = rows[0];
        Assert.Throws<NotSupportedException>(() => rows[0] = default);
        Assert.Throws<NotSupportedException>(() => rows.Clear());
        Assert.Equal(original, Get(definition.Id).Stations[0]);
    }

    [Fact]
    public void Adding_to_the_builder_after_Register_does_not_reach_the_registered_definition()
    {
        var builder = Builder("immutable.builder");
        builder.Register(Owner);
        int inputs = Get("immutable.builder").Inputs.Count;
        int stations = Get("immutable.builder").Stations.Count;

        builder.Input("Item_Ingredient_Stone", 2);
        builder.At(RecipeStationKind.Workstation, "TM_Workstation_SimpleCraftingbench");

        Assert.Equal(inputs, Get("immutable.builder").Inputs.Count);
        Assert.Equal(stations, Get("immutable.builder").Stations.Count);
    }

    private static RecipeDefinitionBuilder Builder(string id) =>
        RecipeDefinition.Define(id)
            .CloneOf("Recipe_Item_Ingredient_CoarseThread")
            .Output("immutable.output", 1)
            .Input("Item_Ingredient_Crystal", 1)
            .At(RecipeStationKind.Workstation, "TM_Workstation_SimpleCraftingbench")
            .CraftTime(4f);

    private static RecipeDefinition Get(string id)
    {
        Assert.True(RecipeApply.TryGet(id, out var def), "expected '" + id + "' to be registered");
        return def;
    }
}
