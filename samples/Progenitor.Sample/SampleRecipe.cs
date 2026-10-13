using System;

#if !PROGENITOR_TESTS
using Progenitor.Content;
using Progenitor.Plugins;
#endif

namespace Progenitor.Sample;

/// <summary>
/// M3.7.1 sample: one workstation recipe at the vanilla simple crafting bench, using only
/// vanilla items. Display name and icon come from the output item (there is no
/// <c>ManagedRecipeData</c>).
/// </summary>
internal static class SampleRecipe
{
    /// <summary>
    /// Primary example. Spawnable id hashed into the recipe GUID. Must be identical on every
    /// peer. Vanilla input and vanilla output — does not depend on item-agreement.
    /// </summary>
    public const string Id = "sample.bench.crystal-thread";

    /// <summary>
    /// Vanilla coarse-thread recipe; the clone source that carries <c>RecipeData</c>.
    /// Identity is the spawnable name, resolved at apply by <c>PrefabLookupMap</c>.
    /// The game's metadata has no spawnable-name → GUID table; the recipe component layout
    /// (<c>RecipeData</c>) comes from it. Vanilla recipe names drop the <c>Item_</c> prefix; the test
    /// server resolves this one to <c>217985609</c> (docs/recipes.md).
    /// </summary>
    public const string SourceName = "Recipe_Ingredient_CoarseThread";

    /// <summary>Vanilla Crystal, one consumed per craft.</summary>
    public const string InputName = "Item_Ingredient_Crystal";

    /// <summary>
    /// <c>PrefabGUID.CalculateGuidHash("Item_Ingredient_Crystal")</c>. Source-pinned in
    /// <c>Plugin.EmberSource</c> and <c>SaveSafetyPolicy.KnownVanillaGuids</c>. The game's metadata does
    /// not list spawnable-name hashes (the type is <c>RecipeRequirementBuffer.Guid</c>).
    /// </summary>
    public const int InputGuidHash = -257494203;

    public const int InputAmount = 1;

    /// <summary>
    /// Vanilla Coarse Thread produced by this sample recipe. Resolved at apply by spawnable
    /// name (test server: <c>-1562867444</c>, the vanilla coarse-thread recipe's output).
    /// <c>RecipeOutputBuffer</c> is the output row written.
    /// </summary>
    public const string OutputName = "Item_Ingredient_Thread_Coarse";

    public const int OutputAmount = 1;

    /// <summary>
    /// Vanilla simple crafting bench. <c>WorkstationRecipesBuffer</c> is in the server metadata and
    /// is not networked. Identity is the spawnable name, resolved at apply (test server:
    /// <c>-1107784271</c>).
    /// </summary>
    public const string StationName = "TM_CraftingStation_SimpleCraftingBench";

    public const float CraftDuration = 8f;

    public const int HudOrder = 1000;

    /// <summary>
    /// Custom-output variant. Depends on paired item agreement for <c>sample.ember</c>.
    /// Not the primary example; <see cref="Register"/> does not register it.
    /// </summary>
    public const string CustomId = "sample.ember.craft";

    public const string CustomOutputId = "sample.ember";

    public const int CustomInputAmount = 4;

#if !PROGENITOR_TESTS
    /// <summary>
    /// Registers <see cref="Id"/> through <c>ctx.Content.DefineRecipe</c>. Vanilla station,
    /// vanilla Crystal input, vanilla Coarse Thread output. Dispose / unload detaches the
    /// station row and destroys the clone without touching vanilla recipes.
    /// </summary>
    internal static IDisposable Register(SampleContext plugin)
    {
        return plugin.Content.DefineRecipe(Id)
            .CloneOf(SourceName)
            .Output(OutputName, OutputAmount)
            .Input(InputName, InputAmount)
            .At(RecipeStationKind.Workstation, StationName)
            .CraftTime(CraftDuration)
            .HudOrder(HudOrder)
            .AlwaysUnlocked()
            .Register();
    }

    /// <summary>
    /// Optional custom-output recipe. Requires <c>sample.ember</c> to have applied on both
    /// sides (item-agreement lane). Not called from <c>Plugin.OnLoad</c>.
    /// </summary>
    internal static IDisposable RegisterCustomEmber(SampleContext plugin)
    {
        return plugin.Content.DefineRecipe(CustomId)
            .CloneOf(SourceName)
            .Output(CustomOutputId, OutputAmount)
            .Input(InputName, CustomInputAmount)
            .At(RecipeStationKind.Workstation, StationName)
            .CraftTime(CraftDuration)
            .HudOrder(HudOrder)
            .AlwaysUnlocked()
            .Register();
    }
#endif
}
