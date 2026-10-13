using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>BUILDSEARCH1: the build-menu search's matching rule (game-free).</summary>
public class BuildSearchMatchTests
{
    [Theory]
    [InlineData("Stone Wall", "stone wall")]
    [InlineData("  STONE\t\twall  ", "stone wall")]
    [InlineData("Wáll", "wall")]
    [InlineData("Castle_Wall_Tier02", "castle wall tier02")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("   ", "")]
    public void Normalize_lowercases_strips_accents_and_collapses_separators(string? input, string expected) =>
        Assert.Equal(expected, BuildSearchMatch.Normalize(input));

    [Fact]
    public void An_empty_query_has_no_words_and_matches_everything()
    {
        var words = BuildSearchMatch.Words("   ");
        Assert.Empty(words);
        Assert.True(BuildSearchMatch.Matches(words, ""));
        Assert.True(BuildSearchMatch.Matches(words, "anything"));
    }

    [Theory]
    [InlineData("stone wall", true)]
    [InlineData("WALL stone", true)]
    [InlineData("wáll", true)]
    [InlineData("sto", true)]
    [InlineData("tier02", true)]
    [InlineData("wood", false)]
    [InlineData("stone wood", false)]
    public void Every_word_must_occur_somewhere_in_the_cell(string query, bool expected)
    {
        string cell = BuildSearchMatch.Join(new[] { "Stone Wall", BuildSearchMatch.PrefabWords("TM_Castle_Wall_Tier02_Stone") });
        Assert.Equal(expected, BuildSearchMatch.Matches(BuildSearchMatch.Words(query), cell));
    }

    [Fact]
    public void A_word_never_spans_two_names()
    {
        string cell = BuildSearchMatch.Join(new[] { "Iron Fence", "Gate" });
        Assert.False(BuildSearchMatch.Matches(BuildSearchMatch.Words("fencegate"), cell));
        Assert.False(BuildSearchMatch.Matches(BuildSearchMatch.Words("fence gate x"), cell));
        Assert.True(BuildSearchMatch.Matches(BuildSearchMatch.Words("fence gate"), cell));
    }

    [Fact]
    public void A_cell_without_names_matches_only_the_empty_query()
    {
        Assert.False(BuildSearchMatch.Matches(BuildSearchMatch.Words("wall"), ""));
        Assert.False(BuildSearchMatch.Matches(BuildSearchMatch.Words("wall"), null));
    }

    [Theory]
    [InlineData("TM_Castle_Wall_Tier02_Stone", "castle wall tier02 stone")]
    [InlineData("tm_Workstation_Sawmill", "workstation sawmill")]
    [InlineData("Castle_TM_Thing", "castle tm thing")]
    [InlineData(null, "")]
    public void PrefabWords_drops_only_the_leading_tile_model_prefix(string? name, string expected) =>
        Assert.Equal(expected, BuildSearchMatch.PrefabWords(name));

    [Fact]
    public void Join_skips_empty_names() =>
        Assert.Equal("a\nb", BuildSearchMatch.Join(new[] { "A", "", null, "  ", "B" }));

    [Fact]
    public void CountText_reads_visible_of_total() =>
        Assert.Equal("3 of 17", BuildSearchMatch.CountText(3, 17));
}
