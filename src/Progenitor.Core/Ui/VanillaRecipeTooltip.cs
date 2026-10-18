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
    /// The product: output row 0 (<c>RecipeOutputBuffer[0]</c>), exactly the row the workstation and
    /// the crafting tab pass to their tooltip. No fall-through to a later row: the game has none.
    /// </summary>
    internal static bool TryProduct(IReadOnlyList<VanillaRecipeRequirement>? outputs, out int itemGuid, out int amount)
    {
        itemGuid = 0;
        amount = 0;
        if (outputs == null || outputs.Count == 0 || outputs[0].ItemGuid == 0) return false;
        itemGuid = outputs[0].ItemGuid;
        amount = outputs[0].Amount;
        return true;
    }

    /// <summary>
    /// What the game's tooltip describes, per window: the workstation and the crafting tab pass the
    /// product (output row 0); the refinement station passes the recipe's own <c>PrefabGUID</c>
    /// (<c>RefinementstationRecipeEntry.EntryId</c>). The amount is the product's, or 0 for the recipe.
    /// </summary>
    internal static void Subject(VanillaPanelKind kind, int recipeGuid, int productGuid, int productAmount, out int itemGuid, out int itemAmount)
    {
        if (kind == VanillaPanelKind.RefinementStation)
        {
            itemGuid = recipeGuid;
            itemAmount = 0;
            return;
        }

        itemGuid = productGuid;
        itemAmount = productAmount;
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

    /// <summary>The container-name suffix of a <see cref="VanillaPanelAnchor.TooltipFooter"/> container.</summary>
    internal const string FooterSuffix = "-footer";

    /// <summary>
    /// Where a row container that belongs at the end of the tooltip's text column goes when footers may be there:
    /// before the first <see cref="VanillaPanelAnchor.TooltipFooter"/> container of the column's trailing run of
    /// Progenitor containers, so footer rows stay the very last; -1 (the end) when there is none.
    /// </summary>
    internal static int SlotBeforeFooters(int childCount, Func<int, string?> nameAt)
    {
        if (nameAt == null || childCount <= 0) return -1;
        int start = childCount;
        while (start > 0 && UiOwnership.IsOwned(nameAt(start - 1) ?? "")) start--;
        for (int i = start; i < childCount; i++)
            if ((nameAt(i) ?? "").EndsWith(FooterSuffix, StringComparison.Ordinal)) return i;
        return -1;
    }

    /// <summary>The largest title inset a header row accepts; a wider reading is a rect not laid out yet.</summary>
    internal const float MaxHeaderInset = 200f;

    /// <summary>
    /// The left inset a <see cref="VanillaPanelAnchor.TooltipHeader"/> container gives its rows so they start where
    /// the title's text starts: the distance from the container's left edge to the title's, both in world space,
    /// divided by the container's world scale. False (keep the current inset) when the scale is not positive or
    /// the distance is outside 0..<see cref="MaxHeaderInset"/>, which is what an unlaid rect reads.
    /// </summary>
    internal static bool TryHeaderInset(float titleLeft, float rowLeft, float scale, out float inset)
    {
        inset = 0f;
        if (!(scale > 0f) || float.IsNaN(titleLeft) || float.IsNaN(rowLeft)) return false;
        float d = (titleLeft - rowLeft) / scale;
        if (float.IsNaN(d) || d < 0f || d > MaxHeaderInset) return false;
        inset = d;
        return true;
    }

    /// <summary>
    /// The game's own entries on either side of a container at <paramref name="index"/> among
    /// <paramref name="childCount"/> siblings, skipping Progenitor's containers (readback: <c>after</c> and
    /// <c>before</c>). Empty when there is none on that side.
    /// </summary>
    internal static void Neighbors(int index, int childCount, Func<int, string?> nameAt, out string after, out string before)
    {
        after = "";
        before = "";
        if (nameAt == null || index < 0 || index >= childCount) return;
        for (int i = index - 1; i >= 0; i--)
        {
            string n = nameAt(i) ?? "";
            if (UiOwnership.IsOwned(n)) continue;
            after = n;
            break;
        }

        for (int i = index + 1; i < childCount; i++)
        {
            string n = nameAt(i) ?? "";
            if (UiOwnership.IsOwned(n)) continue;
            before = n;
            break;
        }
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
