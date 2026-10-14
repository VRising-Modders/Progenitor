#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Plugins;
using Unity.Entities;
using Unity.Mathematics;

namespace Progenitor.Stations;

/// <summary>
/// <see cref="PluginContext.Stations"/>: recipe lists, the per-recipe switch, and progress for castle
/// refinement stations and crafting workstations (<see cref="Stations"/>). Server only; the client
/// throws. Stations are passed as entities, valid for the current world load only. Nothing here
/// registers anything, so unload has nothing to release.
/// </summary>
public sealed class PluginStations
{
    private readonly PluginContext _ctx;

    internal PluginStations(PluginContext ctx) =>
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    private World World()
    {
        _ctx.EnsureAttached();
        return _ctx.World ?? throw new InvalidOperationException("no world");
    }

    /// <summary>Refinement station, workstation, or <see cref="StationKind.None"/>.</summary>
    public StationKind Kind(Entity station) => Stations.KindOf(World(), station);

    /// <summary>The station's recipes in its own order; empty for anything that is not a recipe station.</summary>
    public IReadOnlyList<StationRecipe> Recipes(Entity station) => Stations.Recipes(World(), station);

    /// <summary>
    /// Switch a refinement recipe on or off through the game's per-recipe switch (replicated to every
    /// client's station window). <c>Ok</c> false with a <c>Reason</c> on a workstation, a non-station
    /// or a recipe the station does not list.
    /// </summary>
    public StationToggleResult SetRecipeEnabled(Entity station, int recipeGuid, bool on) =>
        Stations.SetRecipeEnabled(World(), station, recipeGuid, on);

    /// <summary>Whole crafts <paramref name="sources"/> (item GUID to amount) can pay for. Pure; see <see cref="StationLogic.CraftsPossible(StationRecipe, IReadOnlyDictionary{int, long})"/>.</summary>
    public int CraftsPossible(StationRecipe recipe, IReadOnlyDictionary<int, long> sources) =>
        StationLogic.CraftsPossible(recipe, sources);

    /// <summary>Whole crafts a list of stacks can pay for. Pure.</summary>
    public int CraftsPossible(StationRecipe recipe, IEnumerable<StationItem> sources) =>
        StationLogic.CraftsPossible(recipe, sources);

    /// <summary>True while the station is converting (<see cref="StationState.Working"/>).</summary>
    public bool IsWorking(Entity station) => Stations.IsWorking(World(), station);

    /// <summary>Current recipe, state, fraction and time left; null for anything that is not a recipe station.</summary>
    public StationProgress? Progress(Entity station) => Stations.Progress(World(), station);

    /// <summary>Recipe stations within <paramref name="radius"/> meters of <paramref name="position"/>, nearest first.</summary>
    public IReadOnlyList<StationInfo> Near(float3 position, float radius) => Stations.Near(World(), position, radius);
}
#endif
