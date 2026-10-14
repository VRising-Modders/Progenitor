using System;
using System.Collections.Generic;
using Progenitor.Sample;
using Progenitor.Stations;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// ctx.Stations, game-free: whole-craft counting, status mapping, progress math, the toggle plan
/// that mirrors the game's per-recipe checkbox, and the sample's parsing and replies.
/// Item and recipe GUIDs here are placeholders.
/// </summary>
public class StationLogicTests
{
    private const int Wood = 101, Plank = 102, Sawdust = 103, Iron = 104, Ore = 105;

    private static StationRecipe Recipe(params (int Item, int Amount)[] inputs)
    {
        var list = new List<StationItem>();
        foreach (var i in inputs) list.Add(new StationItem(i.Item, i.Amount));
        return new StationRecipe { RecipeGuid = 9, Inputs = list, Outputs = new[] { new StationItem(Plank, 1) }, DurationSeconds = 20 };
    }

    [Fact]
    public void CraftsPossible_is_the_limiting_input_rounded_down()
    {
        var r = Recipe((Plank, 4), (Iron, 4));
        var have = new Dictionary<int, long> { [Plank] = 41, [Iron] = 9, [Wood] = 1000 };
        Assert.Equal(2, StationLogic.CraftsPossible(r, have));
    }

    [Fact]
    public void CraftsPossible_is_zero_when_an_input_is_missing_or_short()
    {
        var r = Recipe((Wood, 20));
        Assert.Equal(0, StationLogic.CraftsPossible(r, new Dictionary<int, long>()));
        Assert.Equal(0, StationLogic.CraftsPossible(r, new Dictionary<int, long> { [Wood] = 19 }));
        Assert.Equal(1, StationLogic.CraftsPossible(r, new Dictionary<int, long> { [Wood] = 20 }));
    }

    [Fact]
    public void CraftsPossible_adds_up_an_input_listed_twice()
    {
        var r = Recipe((Ore, 10), (Ore, 10));
        Assert.Equal(2, StationLogic.CraftsPossible(r, new Dictionary<int, long> { [Ore] = 59 }));
    }

    [Fact]
    public void CraftsPossible_sums_stacks_across_sources()
    {
        var r = Recipe((Wood, 20));
        var stacks = new[] { new StationItem(Wood, 15), new StationItem(Wood, 15), new StationItem(Wood, 15), new StationItem(Sawdust, 99), new StationItem(Wood, -5) };
        Assert.Equal(2, StationLogic.CraftsPossible(r, stacks));
    }

    [Fact]
    public void CraftsPossible_with_no_positive_input_is_zero_and_never_overflows()
    {
        Assert.Equal(0, StationLogic.CraftsPossible(Recipe(), new Dictionary<int, long> { [Wood] = 5 }));
        Assert.Equal(0, StationLogic.CraftsPossible(Recipe((Wood, 0)), new Dictionary<int, long> { [Wood] = 5 }));
        Assert.Equal(int.MaxValue, StationLogic.CraftsPossible(Recipe((Wood, 1)), new Dictionary<int, long> { [Wood] = long.MaxValue }));
        Assert.Throws<ArgumentNullException>(() => StationLogic.CraftsPossible((StationRecipe)null!, new Dictionary<int, long>()));
    }

    [Theory]
    [InlineData(0, StationState.Idle)]
    [InlineData(1, StationState.Working)]
    [InlineData(2, StationState.OutputFull)]
    [InlineData(3, StationState.NotPowered)]
    [InlineData(4, StationState.Damaged)]
    [InlineData(5, StationState.NoOwner)]
    [InlineData(9, StationState.Unknown)]
    public void Refinement_status_maps(int raw, StationState expected) =>
        Assert.Equal(expected, StationLogic.FromRefinementStatus(raw));

    [Theory]
    [InlineData(0, 1, StationState.Working)]
    [InlineData(1, 1, StationState.OutputFull)]
    [InlineData(2, 3, StationState.NotPowered)]
    [InlineData(3, 1, StationState.Damaged)]
    [InlineData(4, 1, StationState.NoOwner)]
    [InlineData(2, 0, StationState.Idle)]
    public void Crafting_status_maps_and_an_empty_queue_is_idle(int raw, int queue, StationState expected) =>
        Assert.Equal(expected, StationLogic.FromCraftingStatus(raw, queue));

    [Fact]
    public void Refinement_progress_counts_from_the_start_stamp_while_processing()
    {
        var p = StationLogic.RefinementProgress(1, 9, now: 1005, start: 1000, duration: 20);
        Assert.True(p.IsWorking);
        Assert.Equal(0.25f, p.Fraction, 3);
        Assert.Equal(5f, p.ElapsedSeconds, 3);
        Assert.Equal(15f, p.RemainingSeconds, 3);
        Assert.Equal(StationKind.Refinement, p.Kind);
    }

    [Fact]
    public void Refinement_progress_clamps_and_handles_stale_stamps()
    {
        Assert.Equal(1f, StationLogic.RefinementProgress(1, 9, 1100, 1000, 20).Fraction);
        Assert.Equal(0f, StationLogic.RefinementProgress(1, 9, 900, 1000, 20).Fraction);
        Assert.Equal(1f, StationLogic.RefinementProgress(2, 9, 0, 1000, 20).Fraction);
        var stopped = StationLogic.RefinementProgress(3, 9, 1100, 1000, 20);
        Assert.Equal(0f, stopped.Fraction);
        Assert.False(stopped.IsWorking);
        Assert.Equal(20f, stopped.RemainingSeconds);
        var none = StationLogic.RefinementProgress(0, 0, 1100, 1000, 20);
        Assert.Equal(0f, none.DurationSeconds);
        Assert.Equal(0f, none.RemainingSeconds);
    }

    [Fact]
    public void Workstation_progress_reads_the_queue_head()
    {
        var p = StationLogic.WorkstationProgress(0, 3, 9, progressTime: 12, duration: 30);
        Assert.True(p.IsWorking);
        Assert.Equal(0.4f, p.Fraction, 3);
        Assert.Equal(18f, p.RemainingSeconds, 3);
        Assert.Equal(3, p.QueueLength);
        var unpowered = StationLogic.WorkstationProgress(2, 1, 9, 12, 30);
        Assert.Equal(StationState.NotPowered, unpowered.State);
        Assert.Equal(0.4f, unpowered.Fraction, 3);
        var empty = StationLogic.WorkstationProgress(0, 0, 0, 0, 0);
        Assert.Equal(StationState.Idle, empty.State);
        Assert.False(empty.IsWorking);
    }

    private static readonly (int, bool)[] Rows = { (11, false), (12, true), (13, false), (14, false) };

    [Fact]
    public void Toggle_writes_the_target_row_and_linked_rows_to_the_same_state()
    {
        var writes = StationLogic.PlanToggle(Rows, 11, enable: false, new[] { 13, 99, 11 }, out int target, out int linked, out string reason);
        Assert.NotNull(writes);
        Assert.Equal(new[] { 0, 2 }, writes);
        Assert.Equal(0, target);
        Assert.Equal(1, linked);
        Assert.Contains("disabled", reason);
    }

    [Fact]
    public void Toggle_is_explicit_not_a_flip_so_repeating_it_changes_nothing()
    {
        var writes = StationLogic.PlanToggle(Rows, 12, enable: false, null, out _, out _, out string reason);
        Assert.NotNull(writes);
        Assert.Empty(writes!);
        Assert.Equal("already disabled", reason);
        var on = StationLogic.PlanToggle(Rows, 12, enable: true, null, out _, out _, out _);
        Assert.Equal(new[] { 1 }, on);
    }

    [Fact]
    public void Toggle_refuses_a_recipe_the_station_does_not_list()
    {
        Assert.Null(StationLogic.PlanToggle(Rows, 77, true, null, out int target, out _, out string reason));
        Assert.Equal(-1, target);
        Assert.Contains("not in this station's recipe list", reason);
        Assert.Null(StationLogic.PlanToggle(Rows, 0, true, null, out _, out _, out _));
    }

    [Fact]
    public void Workstations_get_a_clear_refusal()
    {
        Assert.Contains("no per-recipe switch", StationLogic.NoToggleReason(StationKind.Workstation));
        Assert.Equal("not a recipe station", StationLogic.NoToggleReason(StationKind.None));
    }

    // ---- sample ---------------------------------------------------------------------------------

    [Fact]
    public void Sample_parses_each_verb()
    {
        Assert.True(SampleStations.TryParse("list", "", "", "", out var r));
        Assert.Equal(SampleStations.NearestRadius, r.Radius);
        Assert.True(SampleStations.TryParse("list", "50", "", "", out r));
        Assert.Equal(50f, r.Radius);
        Assert.False(SampleStations.TryParse("list", "500", "", "", out _));
        Assert.True(SampleStations.TryParse("recipes", "1234:2", "", "", out r));
        Assert.Equal(1234, r.StationIndex);
        Assert.Equal(2, r.StationVersion);
        Assert.True(SampleStations.TryParse("toggle", "Plank", "off", "", out r));
        Assert.Equal(SampleStations.Kind.Toggle, r.Kind);
        Assert.False(r.On);
        Assert.False(r.HasStation);
        Assert.True(SampleStations.TryParse("toggle", "-7510953", "on", "5:1", out r));
        Assert.True(r.On);
        Assert.False(SampleStations.TryParse("toggle", "Plank", "maybe", "", out _));
        Assert.False(SampleStations.TryParse("status", "notanid", "", "", out _));
        Assert.False(SampleStations.TryParse("explode", "", "", "", out _));
    }

    [Fact]
    public void Sample_matches_recipes_by_guid_exact_name_or_unique_fragment()
    {
        var recipes = new[]
        {
            new StationRecipe { RecipeGuid = 1, Name = "Recipe_Ingredient_Plank" },
            new StationRecipe { RecipeGuid = 2, Name = "Recipe_Ingredient_ReinforcedPlank" },
            new StationRecipe { RecipeGuid = 3, Name = "Recipe_Ingredient_Sulfur" },
        };
        Assert.Equal(3, SampleStations.MatchRecipe(recipes, "sulfur", out _)!.RecipeGuid);
        Assert.Equal(1, SampleStations.MatchRecipe(recipes, "recipe_ingredient_plank", out _)!.RecipeGuid);
        Assert.Equal(2, SampleStations.MatchRecipe(recipes, "2", out _)!.RecipeGuid);
        Assert.Null(SampleStations.MatchRecipe(recipes, "Plank", out string why));
        Assert.Contains("matches 2 recipes", why);
        Assert.Null(SampleStations.MatchRecipe(recipes, "Glass", out why));
    }

    [Fact]
    public void Sample_replies_name_the_switch_state()
    {
        var recipe = new StationRecipe
        {
            RecipeGuid = 1, Name = "Recipe_Ingredient_Plank", CanToggle = true, Enabled = false, DurationSeconds = 20,
            Inputs = new[] { new StationItem(Wood, 20) }, Outputs = new[] { new StationItem(Plank, 1), new StationItem(Sawdust, 1) },
        };
        var lines = SampleStations.RecipeLines(new[] { recipe }, g => g == Wood ? "Item_Ingredient_Wood_Standard" : g.ToString());
        Assert.Equal("[off] Plank: 20 Wood_Standard -> 1 102 + 1 103, 20 s", lines[0]);

        var ok = new StationToggleResult { Ok = true, Changed = true, Enabled = false, LinkedRowsChanged = 1 };
        Assert.Equal("Plank is now off; 1 linked recipe(s) switched with it.", SampleStations.ToggleReply(ok, "Recipe_Ingredient_Plank"));
        var refused = new StationToggleResult { Ok = false, Reason = StationLogic.NoToggleReason(StationKind.Workstation) };
        Assert.StartsWith("Toggle refused for Plank: crafting workstations", SampleStations.ToggleReply(refused, "Recipe_Ingredient_Plank"));

        var status = SampleStations.StatusReply(StationLogic.RefinementProgress(1, 1, 1005, 1000, 20), "Recipe_Ingredient_Plank");
        Assert.Equal("Refinement: working, Plank 25%, 15 s left (working=yes, raw status 1).", status);
        Assert.Equal("Not a recipe station.", SampleStations.StatusReply(null, ""));
    }
}
