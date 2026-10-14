using System;
using System.Collections.Generic;

namespace Progenitor.Stations;

/// <summary>
/// The game-free half of <c>ctx.Stations</c>: craft counting, status mapping, progress math and the
/// toggle plan. The live reader calls these, so tests and the game cannot drift.
/// </summary>
public static class StationLogic
{
    /// <summary>
    /// How many whole crafts of <paramref name="recipe"/> the <paramref name="sources"/> can pay for.
    /// <paramref name="sources"/> maps item GUID hash to the amount available (summed over every
    /// container you count). Inputs that name the same item twice add up. Inputs with an amount of
    /// 0 or less are ignored; a recipe with no positive input returns 0.
    /// </summary>
    public static int CraftsPossible(StationRecipe recipe, IReadOnlyDictionary<int, long> sources)
    {
        if (recipe == null) throw new ArgumentNullException(nameof(recipe));
        return CraftsPossible(recipe.Inputs, sources);
    }

    /// <summary><see cref="CraftsPossible(StationRecipe, IReadOnlyDictionary{int, long})"/> over a list of stacks; stacks of the same item add up.</summary>
    public static int CraftsPossible(StationRecipe recipe, IEnumerable<StationItem> sources)
    {
        if (recipe == null) throw new ArgumentNullException(nameof(recipe));
        return CraftsPossible(recipe.Inputs, Sum(sources));
    }

    /// <summary>The count over a bare input list.</summary>
    public static int CraftsPossible(IReadOnlyList<StationItem> inputs, IReadOnlyDictionary<int, long> sources)
    {
        if (inputs == null) throw new ArgumentNullException(nameof(inputs));
        if (sources == null) throw new ArgumentNullException(nameof(sources));
        var need = new Dictionary<int, long>();
        foreach (var input in inputs)
        {
            if (input.Amount <= 0) continue;
            need.TryGetValue(input.ItemGuid, out long n);
            need[input.ItemGuid] = n + input.Amount;
        }
        if (need.Count == 0) return 0;

        long best = long.MaxValue;
        foreach (var kv in need)
        {
            sources.TryGetValue(kv.Key, out long have);
            if (have <= 0) return 0;
            long crafts = have / kv.Value;
            if (crafts < best) best = crafts;
            if (best == 0) return 0;
        }
        return best > int.MaxValue ? int.MaxValue : (int)best;
    }

    /// <summary>Add up stacks by item GUID. Non-positive amounts are skipped.</summary>
    public static Dictionary<int, long> Sum(IEnumerable<StationItem> stacks)
    {
        if (stacks == null) throw new ArgumentNullException(nameof(stacks));
        var totals = new Dictionary<int, long>();
        foreach (var s in stacks)
        {
            if (s.Amount <= 0 || s.ItemGuid == 0) continue;
            totals.TryGetValue(s.ItemGuid, out long n);
            totals[s.ItemGuid] = n + s.Amount;
        }
        return totals;
    }

    /// <summary>Map the game's <c>RefinementStatus</c> value.</summary>
    public static StationState FromRefinementStatus(int status) => status switch
    {
        Layout.RefinementStatusNoInput => StationState.Idle,
        Layout.RefinementStatusProcessing => StationState.Working,
        Layout.RefinementStatusOutputFull => StationState.OutputFull,
        Layout.RefinementStatusNotPowered => StationState.NotPowered,
        Layout.RefinementStatusDamaged => StationState.Damaged,
        Layout.RefinementStatusNoOwner => StationState.NoOwner,
        _ => StationState.Unknown,
    };

    /// <summary>
    /// Map the game's <c>CraftingStatus</c> on the head of a workstation queue. An empty queue is
    /// <see cref="StationState.Idle"/> whatever the status reads.
    /// </summary>
    public static StationState FromCraftingStatus(int status, int queueLength)
    {
        if (queueLength <= 0) return StationState.Idle;
        return status switch
        {
            Layout.CraftingStatusNormal => StationState.Working,
            Layout.CraftingStatusOutputFull => StationState.OutputFull,
            Layout.CraftingStatusNotPowered => StationState.NotPowered,
            Layout.CraftingStatusDamaged => StationState.Damaged,
            Layout.CraftingStatusNoOwner => StationState.NoOwner,
            _ => StationState.Unknown,
        };
    }

    /// <summary>Lower-case words for a state, for logs and chat.</summary>
    public static string Describe(StationState state) => state switch
    {
        StationState.Idle => "idle",
        StationState.Working => "working",
        StationState.OutputFull => "output full",
        StationState.NotPowered => "not powered",
        StationState.Damaged => "damaged",
        StationState.NoOwner => "no owner",
        _ => "unknown",
    };

    /// <summary>
    /// Refinement progress. The game stamps <c>RefiningStartTime</c> (server time) when a batch
    /// starts and finishes it once <c>now &gt;= start + duration / speed</c>; there is no stored
    /// fraction. While <see cref="StationState.Working"/>, elapsed is <c>now - start</c>, clamped to
    /// the duration. <see cref="StationState.OutputFull"/> means a finished batch is waiting, so it is
    /// complete. Any other state has no batch running and reads 0.
    /// </summary>
    public static StationProgress RefinementProgress(int rawStatus, int recipeGuid, double now, double start, float duration)
    {
        var state = FromRefinementStatus(rawStatus);
        if (recipeGuid == 0) duration = 0;
        float elapsed = 0;
        if (duration > 0)
        {
            if (state == StationState.Working)
            {
                double e = now - start;
                if (double.IsNaN(e) || e < 0) e = 0;
                elapsed = e >= duration ? duration : (float)e;
            }
            else if (state == StationState.OutputFull)
            {
                elapsed = duration;
            }
        }
        return Build(StationKind.Refinement, state, rawStatus, recipeGuid, elapsed, duration, 0);
    }

    /// <summary>
    /// Workstation progress from the head of the queue: <c>ProgressTime</c> counts up toward the
    /// recipe's <c>CraftDuration</c> and is kept while the station is stopped.
    /// </summary>
    public static StationProgress WorkstationProgress(int rawStatus, int queueLength, int recipeGuid, float progressTime, float duration)
    {
        var state = FromCraftingStatus(rawStatus, queueLength);
        if (queueLength <= 0)
            return Build(StationKind.Workstation, StationState.Idle, rawStatus, 0, 0, 0, 0);
        float elapsed = float.IsNaN(progressTime) || progressTime < 0 ? 0 : progressTime;
        if (duration > 0 && elapsed > duration) elapsed = duration;
        return Build(StationKind.Workstation, state, rawStatus, recipeGuid, elapsed, duration < 0 ? 0 : duration, queueLength);
    }

    private static StationProgress Build(StationKind kind, StationState state, int raw, int recipe, float elapsed, float duration, int queue)
    {
        float fraction = duration > 0 ? elapsed / duration : 0;
        if (fraction < 0) fraction = 0;
        if (fraction > 1) fraction = 1;
        float remaining = duration - elapsed;
        if (remaining < 0) remaining = 0;
        return new StationProgress
        {
            Kind = kind,
            State = state,
            RawStatus = raw,
            RecipeGuid = recipe,
            Fraction = fraction,
            ElapsedSeconds = elapsed,
            DurationSeconds = duration,
            RemainingSeconds = remaining,
            QueueLength = queue,
        };
    }

    /// <summary>
    /// The rows a <c>SetRecipeEnabled</c> call writes, given the station's rows (recipe GUID and
    /// <c>Disabled</c>, in buffer order) and the recipe prefab's linked recipe GUIDs. Mirrors the
    /// game's toggle: the target row, then every other row whose recipe is linked, all set to the
    /// same value. Unlike the game's checkbox, which flips, this sets an explicit state, so calling it
    /// twice is harmless. Returns null and a reason when the recipe is not on this station.
    /// </summary>
    public static IReadOnlyList<int>? PlanToggle(
        IReadOnlyList<(int RecipeGuid, bool Disabled)> rows,
        int recipeGuid,
        bool enable,
        IReadOnlyList<int>? linkedRecipes,
        out int targetRow,
        out int linkedChanged,
        out string reason)
    {
        if (rows == null) throw new ArgumentNullException(nameof(rows));
        targetRow = -1;
        linkedChanged = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].RecipeGuid == recipeGuid)
            {
                targetRow = i;
                break;
            }
        }
        if (recipeGuid == 0 || targetRow < 0)
        {
            reason = "recipe " + recipeGuid + " is not in this station's recipe list";
            return null;
        }

        bool disabled = !enable;
        var writes = new List<int>();
        if (rows[targetRow].Disabled != disabled) writes.Add(targetRow);
        if (linkedRecipes != null)
        {
            foreach (int link in linkedRecipes)
            {
                if (link == 0 || link == recipeGuid) continue;
                for (int i = 0; i < rows.Count; i++)
                {
                    if (i == targetRow || rows[i].RecipeGuid != link) continue;
                    if (rows[i].Disabled != disabled && !writes.Contains(i))
                    {
                        writes.Add(i);
                        linkedChanged++;
                    }
                }
            }
        }
        reason = writes.Count == 0
            ? "already " + (enable ? "enabled" : "disabled")
            : (enable ? "enabled" : "disabled") + (linkedChanged > 0 ? " with " + linkedChanged + " linked row(s)" : "");
        return writes;
    }

    /// <summary>
    /// Whether <paramref name="recipeGuid"/>'s row is enabled right now (false when the station does
    /// not list it). A refused <c>SetRecipeEnabled</c> reports this, the state nothing changed.
    /// </summary>
    public static bool IsEnabled(IReadOnlyList<(int RecipeGuid, bool Disabled)> rows, int recipeGuid)
    {
        if (rows == null) throw new ArgumentNullException(nameof(rows));
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].RecipeGuid == recipeGuid) return !rows[i].Disabled;
        return false;
    }

    /// <summary>The refusal <c>SetRecipeEnabled</c> gives for a station kind with no per-recipe switch.</summary>
    public static string NoToggleReason(StationKind kind) => kind switch
    {
        StationKind.Workstation => "crafting workstations have no per-recipe switch in the game; only refinement stations do",
        StationKind.None => "not a recipe station",
        _ => "",
    };
}
