using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// The game-free half of <see cref="VanillaUiSite.RecipeTooltip"/>: which output row is the product,
/// which requirement rows the target carries and in what order, where a row container goes among the
/// tooltip's own entries, and the one-hover memo that keeps a steady hover from reading the recipe's
/// buffers every frame. The Unity half (<c>VanillaPanelSiteResolver.cs</c>) reads the rows and the
/// hierarchy and hands them here.
/// </summary>
internal static class VanillaRecipeTooltip
{
    /// <summary>
    /// The most requirement rows a target carries. A recipe has up to 8 on this build (the recipe
    /// definition's own limit); anything past this is a corrupt or foreign buffer and is cut.
    /// </summary>
    internal const int MaxRequirements = 16;

    /// <summary>
    /// The product: the first output row with an item. The tooltip describes output 0
    /// (<c>RecipeOutputBuffer[0]</c>); an empty first row falls through to the next one.
    /// </summary>
    internal static bool TryProduct(IReadOnlyList<VanillaRecipeRequirement>? outputs, out int itemGuid, out int amount)
    {
        itemGuid = 0;
        amount = 0;
        if (outputs == null) return false;
        for (int i = 0; i < outputs.Count; i++)
        {
            if (outputs[i].ItemGuid == 0) continue;
            itemGuid = outputs[i].ItemGuid;
            amount = outputs[i].Amount;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The requirement rows a target carries: the recipe's rows in their own order (the order the
    /// tooltip lists them), without empty rows, at most <see cref="MaxRequirements"/>. Null when none.
    /// </summary>
    internal static VanillaRecipeRequirement[]? Requirements(IReadOnlyList<VanillaRecipeRequirement>? rows)
    {
        if (rows == null || rows.Count == 0) return null;
        int n = 0;
        for (int i = 0; i < rows.Count && n < MaxRequirements; i++)
            if (rows[i].ItemGuid != 0) n++;
        if (n == 0) return null;

        var list = new VanillaRecipeRequirement[n];
        int at = 0;
        for (int i = 0; i < rows.Count && at < n; i++)
            if (rows[i].ItemGuid != 0) list[at++] = rows[i];
        return list;
    }

    /// <summary>
    /// Where a row container goes in the tooltip's entry column: right after
    /// <paramref name="referenceIndex"/> (the description frame or the requirement block), and after
    /// any Progenitor container already there, so several plugins' rows stack in the order they were
    /// built and never land between a game entry and its own rows. -1 when the reference is not a child.
    /// </summary>
    internal static int SlotAfter(int referenceIndex, int childCount, Func<int, string?> nameAt)
    {
        if (referenceIndex < 0 || referenceIndex >= childCount || nameAt == null) return -1;
        int i = referenceIndex + 1;
        while (i < childCount && UiOwnership.IsOwned(nameAt(i) ?? "")) i++;
        return i;
    }
}

/// <summary>
/// The last hovered recipe entry and what was read for it. A hover that stays on one entry is
/// answered from here, so the recipe's buffers are read once per hover rather than once per frame;
/// a different entry, a different recipe on the same (pooled) entry or another world reads again.
/// </summary>
internal sealed class VanillaRecipeHoverMemo
{
    private long _entry;
    private int _recipe;
    private int _world;
    private bool _set;

    internal int ProductGuid { get; private set; }

    internal int ProductAmount { get; private set; }

    internal VanillaRecipeRequirement[]? Requirements { get; private set; }

    /// <summary>Whether the memo holds this entry's recipe in this world.</summary>
    internal bool Holds(long entry, int recipe, int world) =>
        _set && _entry == entry && _recipe == recipe && _world == world;

    internal void Store(long entry, int recipe, int world, int productGuid, int productAmount, VanillaRecipeRequirement[]? requirements)
    {
        _entry = entry;
        _recipe = recipe;
        _world = world;
        _set = true;
        ProductGuid = productGuid;
        ProductAmount = productAmount;
        Requirements = requirements;
    }

    internal void Clear()
    {
        _set = false;
        _entry = 0;
        _recipe = 0;
        _world = 0;
        ProductGuid = 0;
        ProductAmount = 0;
        Requirements = null;
    }
}
