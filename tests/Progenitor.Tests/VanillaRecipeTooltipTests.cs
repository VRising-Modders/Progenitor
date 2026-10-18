using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The game-free half of <see cref="VanillaUiSite.RecipeTooltip"/>: product pick, requirement order,
/// the slot a row container takes among the tooltip's entries, and the one-hover memo.
/// </summary>
public class VanillaRecipeTooltipTests
{
    private static VanillaRecipeRequirement R(int guid, int amount) => new(guid, amount);

    [Fact]
    public void The_product_is_output_row_zero_only()
    {
        Assert.True(VanillaRecipeTooltip.TryProduct(new[] { R(500, 2), R(501, 1) }, out int g, out int a));
        Assert.Equal(500, g);
        Assert.Equal(2, a);

        // The game passes row 0 as is; an empty row 0 is no product, not the next row.
        Assert.False(VanillaRecipeTooltip.TryProduct(new[] { R(0, 0), R(501, 4) }, out g, out a));
        Assert.Equal(0, g);
        Assert.Equal(0, a);

        Assert.False(VanillaRecipeTooltip.TryProduct(new VanillaRecipeRequirement[0], out g, out a));
        Assert.False(VanillaRecipeTooltip.TryProduct(null, out g, out a));
    }

    [Theory]
    [InlineData(VanillaPanelKind.Workstation, 500, 2)]
    [InlineData(VanillaPanelKind.Character, 500, 2)]
    [InlineData(VanillaPanelKind.RefinementStation, 9001, 0)]
    public void The_tooltip_subject_is_the_product_except_at_a_refinement_station(VanillaPanelKind kind, int item, int amount)
    {
        VanillaRecipeTooltip.Subject(kind, 9001, 500, 2, out int g, out int a);
        Assert.Equal(item, g);
        Assert.Equal(amount, a);
    }

    [Fact]
    public void Requirements_keep_the_recipe_order_and_drop_empty_rows()
    {
        var got = VanillaRecipeTooltip.Requirements(new[] { R(302, 8), R(0, 5), R(301, 3) });
        Assert.Equal(new[] { R(302, 8), R(301, 3) }, got);
        Assert.Null(VanillaRecipeTooltip.Requirements(new[] { R(0, 1) }));
        Assert.Null(VanillaRecipeTooltip.Requirements(null));
    }

    [Fact]
    public void Requirements_are_capped()
    {
        var rows = new List<VanillaRecipeRequirement>();
        for (int i = 1; i <= 40; i++) rows.Add(R(i, i));
        var got = VanillaRecipeTooltip.Requirements(rows)!;
        Assert.Equal(VanillaRecipeTooltip.MaxRequirements, got.Length);
        Assert.Equal(R(1, 1), got[0]);
        Assert.Equal(R(16, 16), got[15]);
    }

    // The tooltip's entry column on 1.1.13.0, the part around the two anchors.
    private static readonly string[] Entries =
    {
        "Tooltip_ItemSetBonuses", "TooltipDescFrame", "TooltipPreSubText", "Tooltip_RecipeRequiredItems",
        "Tooltip_ItemRepairCost", "ErrorText",
    };

    [Fact]
    public void A_row_container_goes_right_after_its_reference()
    {
        Assert.Equal(2, VanillaRecipeTooltip.SlotAfter(1, Entries.Length, i => Entries[i]));
        Assert.Equal(4, VanillaRecipeTooltip.SlotAfter(3, Entries.Length, i => Entries[i]));
        Assert.Equal(-1, VanillaRecipeTooltip.SlotAfter(-1, Entries.Length, i => Entries[i]));
        Assert.Equal(-1, VanillaRecipeTooltip.SlotAfter(Entries.Length, Entries.Length, i => Entries[i]));
    }

    [Fact]
    public void A_second_plugin_stacks_after_the_first_not_between_it_and_the_game()
    {
        var names = new List<string>(Entries);
        names.Insert(4, "progenitor-ui-first.mod-RecipeTooltip-requirements");
        Assert.Equal(5, VanillaRecipeTooltip.SlotAfter(3, names.Count, i => names[i]));

        // The last entry of the column: the slot is the end.
        names.Add("progenitor-ui-x-RecipeTooltip-tooltip");
        Assert.Equal(names.Count, VanillaRecipeTooltip.SlotAfter(names.Count - 2, names.Count, i => names[i]));
    }

    // The tooltip root on 1.1.13.0: the title band, the reforge block, the entry column.
    private static readonly string[] Root = { "TooltipHeader", "Tooltip_ReforgeCost", "InformationEntries" };

    [Fact]
    public void A_header_container_goes_right_after_the_title_band_and_stacks_after_earlier_ones()
    {
        Assert.Equal(1, VanillaRecipeTooltip.SlotAfter(0, Root.Length, i => Root[i]));
        var names = new List<string>(Root);
        names.Insert(1, "progenitor-ui-first.mod-RecipeTooltip-header");
        Assert.Equal(2, VanillaRecipeTooltip.SlotAfter(0, names.Count, i => names[i]));
    }

    [Fact]
    public void The_readback_names_the_games_entries_on_either_side()
    {
        var names = new List<string>(Root);
        names.Insert(1, "progenitor-ui-first.mod-RecipeTooltip-header");
        names.Insert(2, "progenitor-ui-second.mod-RecipeTooltip-header");
        VanillaRecipeTooltip.Neighbors(2, names.Count, i => names[i], out string after, out string before);
        Assert.Equal("TooltipHeader", after);
        Assert.Equal("Tooltip_ReforgeCost", before);

        VanillaRecipeTooltip.Neighbors(names.Count - 1, names.Count, i => names[i], out after, out before);
        Assert.Equal("Tooltip_ReforgeCost", after);
        Assert.Equal("", before);
        VanillaRecipeTooltip.Neighbors(9, names.Count, i => names[i], out after, out before);
        Assert.Equal("", after);
        Assert.Equal("", before);
    }

    [Theory]
    [InlineData(120f, 100f, 1f, true, 20f)]
    [InlineData(140f, 100f, 2f, true, 20f)]
    [InlineData(100f, 100f, 1f, true, 0f)]
    [InlineData(90f, 100f, 1f, false, 0f)]
    [InlineData(400f, 100f, 1f, false, 0f)]
    [InlineData(120f, 100f, 0f, false, 0f)]
    [InlineData(float.NaN, 100f, 1f, false, 0f)]
    public void The_header_inset_is_the_title_distance_in_the_containers_units(float title, float row, float scale, bool ok, float inset)
    {
        Assert.Equal(ok, VanillaRecipeTooltip.TryHeaderInset(title, row, scale, out float got));
        Assert.Equal(inset, got, 3);
    }

    [Fact]
    public void The_memo_answers_one_hover_and_reads_again_for_anything_else()
    {
        var memo = new VanillaRecipeHoverMemo();
        Assert.False(memo.Holds(10, 9001, 1));

        var needs = new[] { R(301, 3) };
        memo.Store(10, 9001, 1, 500, 1, needs);
        Assert.True(memo.Holds(10, 9001, 1));
        Assert.Same(needs, memo.Requirements);
        Assert.Equal(500, memo.ProductGuid);

        Assert.False(memo.Holds(11, 9001, 1));   // another entry
        Assert.False(memo.Holds(10, 9002, 1));   // the pooled entry now shows another recipe
        Assert.False(memo.Holds(10, 9001, 2));   // another world

        memo.Clear();
        Assert.False(memo.Holds(10, 9001, 1));
        Assert.Null(memo.Requirements);
    }

    [Fact]
    public void A_string_converts_to_a_normal_row()
    {
        VanillaRow row = "Network: 12";
        Assert.Equal(LabelTone.Normal, row.Tone);
        Assert.Equal("Network: 12", row.Text);
        Assert.Equal("", default(VanillaRow).Text);
        Assert.Equal("Low [warn]", new VanillaRow("Low", LabelTone.Warn).ToString());
        Assert.Equal(new VanillaRow("a", LabelTone.Ok), new VanillaRow("a", LabelTone.Ok));
        Assert.NotEqual(new VanillaRow("a", LabelTone.Ok), new VanillaRow("a"));
    }
}
