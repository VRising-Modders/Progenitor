using System;
using System.Collections.Generic;

namespace Progenitor.Stations;

/// <summary>Which recipe machinery a castle station runs. Game-free.</summary>
public enum StationKind
{
    /// <summary>Not a recipe station (or the entity does not exist).</summary>
    None = 0,

    /// <summary>
    /// A refinement station (sawmill, furnace, tannery, loom, stonecutter, blood press, ...): it
    /// converts whatever matching input sits in its input inventory, one batch at a time, and has a
    /// per-recipe on/off switch in its window.
    /// </summary>
    Refinement = 1,

    /// <summary>
    /// A crafting workstation (workbench, smithy, tailor bench, ...): it works through a queue of
    /// crafts players started, and has no per-recipe switch.
    /// </summary>
    Workstation = 2,
}

/// <summary>What a station is doing, mapped from the game's own status enum. Game-free.</summary>
public enum StationState
{
    /// <summary>The status value was not one this framework knows.</summary>
    Unknown = 0,

    /// <summary>Nothing to do: no matching input (refinement) or an empty queue (workstation).</summary>
    Idle = 1,

    /// <summary>Converting now.</summary>
    Working = 2,

    /// <summary>A batch is done and the output inventory has no room for it.</summary>
    OutputFull = 3,

    /// <summary>The castle heart is not burning blood essence.</summary>
    NotPowered = 4,

    /// <summary>The station is damaged.</summary>
    Damaged = 5,

    /// <summary>The station has no owner.</summary>
    NoOwner = 6,
}

/// <summary>An item prefab GUID and an amount. Game-free.</summary>
public readonly struct StationItem : IEquatable<StationItem>
{
    /// <summary>Item prefab GUID hash.</summary>
    public int ItemGuid { get; }

    /// <summary>How many.</summary>
    public int Amount { get; }

    /// <summary>Make one.</summary>
    public StationItem(int itemGuid, int amount)
    {
        ItemGuid = itemGuid;
        Amount = amount;
    }

    /// <inheritdoc />
    public bool Equals(StationItem other) => ItemGuid == other.ItemGuid && Amount == other.Amount;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is StationItem other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (ItemGuid * 397) ^ Amount;

    /// <inheritdoc />
    public override string ToString() => Amount + "x" + ItemGuid;
}

/// <summary>
/// One recipe a station offers, as the station instance holds it. Inputs and outputs are the
/// recipe prefab's base amounts; a station bonus that lowers input cost (matching floor) is not
/// applied. Game-free.
/// </summary>
public sealed class StationRecipe
{
    /// <summary>Recipe prefab GUID hash. This is the id <c>SetRecipeEnabled</c> takes.</summary>
    public int RecipeGuid { get; init; }

    /// <summary>Recipe prefab name, for example <c>Recipe_Ingredient_Plank</c>; empty when unknown.</summary>
    public string Name { get; init; } = "";

    /// <summary>The station kind this row came from.</summary>
    public StationKind Kind { get; init; }

    /// <summary>Items one craft consumes (<c>RecipeRequirementBuffer</c>).</summary>
    public IReadOnlyList<StationItem> Inputs { get; init; } = Array.Empty<StationItem>();

    /// <summary>Items one craft produces (<c>RecipeOutputBuffer</c>).</summary>
    public IReadOnlyList<StationItem> Outputs { get; init; } = Array.Empty<StationItem>();

    /// <summary>Seconds per craft from the recipe prefab's <c>RecipeData.CraftDuration</c>, before station speed bonuses.</summary>
    public float DurationSeconds { get; init; }

    /// <summary>
    /// False when the station's per-recipe switch is off (refinement only). Always true on a
    /// workstation, which has no switch.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>True when this row has a vanilla per-recipe switch (<see cref="StationKind.Refinement"/>).</summary>
    public bool CanToggle { get; init; }

    /// <summary>
    /// Refinement: the station row's <c>Unlocked</c> flag. Workstation: true (unlocks are per
    /// player there, not per station).
    /// </summary>
    public bool Unlocked { get; init; } = true;

    /// <summary><c>RecipeData.HideInStation</c>: the station window does not list this recipe.</summary>
    public bool HiddenInStation { get; init; }
}

/// <summary>A station's current work. Game-free.</summary>
public sealed class StationProgress
{
    /// <summary>The station kind.</summary>
    public StationKind Kind { get; init; }

    /// <summary>What the station is doing.</summary>
    public StationState State { get; init; }

    /// <summary>The game's raw status value (<c>RefinementStatus</c> or <c>CraftingStatus</c>).</summary>
    public int RawStatus { get; init; }

    /// <summary>True exactly when <see cref="State"/> is <see cref="StationState.Working"/>.</summary>
    public bool IsWorking => State == StationState.Working;

    /// <summary>The recipe in progress (refinement: the current recipe; workstation: the head of the queue). 0 when none.</summary>
    public int RecipeGuid { get; init; }

    /// <summary>Progress toward the current craft, 0..1.</summary>
    public float Fraction { get; init; }

    /// <summary>Seconds of work done on the current craft.</summary>
    public float ElapsedSeconds { get; init; }

    /// <summary>The current recipe's <c>CraftDuration</c> in seconds; 0 when there is no current recipe.</summary>
    public float DurationSeconds { get; init; }

    /// <summary>
    /// <see cref="DurationSeconds"/> minus <see cref="ElapsedSeconds"/>, at least 0. Station speed
    /// bonuses (a closed room) are not applied, so a boosted station finishes sooner than this.
    /// </summary>
    public float RemainingSeconds { get; init; }

    /// <summary>Workstation: crafts in the queue, including the current one. Refinement: 0.</summary>
    public int QueueLength { get; init; }
}

/// <summary>Outcome of <c>SetRecipeEnabled</c>. Game-free.</summary>
public sealed class StationToggleResult
{
    /// <summary>True when the recipe is now in the requested state.</summary>
    public bool Ok { get; init; }

    /// <summary>True when the call changed at least one row (false when it was already in that state).</summary>
    public bool Changed { get; init; }

    /// <summary>The recipe's enabled state after the call.</summary>
    public bool Enabled { get; init; }

    /// <summary>The recipe asked for.</summary>
    public int RecipeGuid { get; init; }

    /// <summary>
    /// Other rows on the same station switched with it because the recipe prefab links them
    /// (<c>RecipeLinkBuffer</c>), the way the station window's checkbox does.
    /// </summary>
    public int LinkedRowsChanged { get; init; }

    /// <summary>Why the call refused or what it did, in one sentence.</summary>
    public string Reason { get; init; } = "";
}
