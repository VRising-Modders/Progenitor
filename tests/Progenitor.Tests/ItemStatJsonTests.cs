using System.Collections.Generic;
using Progenitor.Content.Json;
using Xunit;

namespace Progenitor.Tests;

public class ItemStatJsonTests
{
    private const string Label = "Stats/content/items.json";
    private static string Item(string fields = "") =>
        "{\"id\":\"stats.sword\",\"cloneOf\":\"Item_Weapon_Sword_T01_Bone\"" + fields + "}";
    private static string File(string item) => "{\"schema\":1,\"items\":[" + item + "]}";

    [Fact]
    public void Three_additive_stats_reach_the_load_sink()
    {
        var sink = new Sink();
        var result = ContentFileLoad.Run(new[] { new ContentFileSource(Label,
            File(Item(",\"statBonuses\":{\"maxHealth\":100,\"physicalPower\":100,\"spellPower\":100}"))) }, sink);
        Assert.Empty(result.Errors);
        var stats = Assert.Single(sink.Items).StatBonuses!;
        Assert.Equal(100f, stats.MaxHealth);
        Assert.Equal(100f, stats.PhysicalPower);
        Assert.Equal(100f, stats.SpellPower);
    }

    [Fact]
    public void Omitted_stat_object_preserves_source_and_partial_object_defaults_to_zero()
    {
        Assert.True(ContentFileParser.TryParse(Label, File(Item()), out var omitted, out _));
        Assert.Null(Assert.Single(omitted!.Items).StatBonuses);
        Assert.True(ContentFileParser.TryParse(Label,
            File(Item(",\"statBonuses\":{\"physicalPower\":-12.5}")), out var partial, out _));
        var stats = Assert.Single(partial!.Items).StatBonuses!;
        Assert.Equal(0f, stats.MaxHealth);
        Assert.Equal(-12.5f, stats.PhysicalPower);
        Assert.Equal(0f, stats.SpellPower);
    }

    [Theory]
    [InlineData("null", "statBonuses")]
    [InlineData("[]", "statBonuses")]
    [InlineData("{\"maxHeath\":100}", "statBonuses.maxHeath")]
    [InlineData("{\"physicalPower\":1,\"physicalPower\":2}", "statBonuses.physicalPower")]
    [InlineData("{\"maxHealth\":10001}", "statBonuses.maxHealth")]
    [InlineData("{\"maxHealth\":10000.0001}", "statBonuses.maxHealth")]
    [InlineData("{\"spellPower\":-10001}", "statBonuses.spellPower")]
    [InlineData("{\"spellPower\":1e1000}", "statBonuses.spellPower")]
    [InlineData("{\"maxHealth\":\"NaN\"}", "statBonuses.maxHealth")]
    [InlineData("{\"physicalPower\":true}", "statBonuses.physicalPower")]
    public void Invalid_stats_refuse_the_whole_file(string stats, string field)
    {
        var sink = new Sink();
        var json = "{\"schema\":1,\"items\":[" + Item() + ","
            + Item(",\"statBonuses\":" + stats).Replace("stats.sword", "stats.bad") + "]}";
        Assert.False(ContentFileParser.TryParse(Label, json, out var doc, out var errors));
        Assert.Null(doc);
        Assert.Contains(errors, e => e.Contains(Label) && e.Contains("item 1") && e.Contains(field));
        var result = ContentFileLoad.Run(new[] { new ContentFileSource(Label, json) }, sink);
        Assert.Empty(sink.Items);
        Assert.Equal(1, result.FilesRefused);
        Assert.Equal(0, result.ItemsDefined);
    }

    [Fact]
    public void Duplicate_stat_object_is_refused_instead_of_last_writer_winning()
    {
        Assert.False(ContentFileParser.TryParse(Label,
            File(Item(",\"statBonuses\":{},\"statBonuses\":{\"maxHealth\":100}")), out var doc, out var errors));
        Assert.Null(doc);
        Assert.Contains(errors, e => e.Contains("statBonuses") && e.Contains("defined twice"));
    }

    [Fact]
    public void Inclusive_bounds_are_allowed()
    {
        Assert.True(ContentFileParser.TryParse(Label,
            File(Item(",\"statBonuses\":{\"maxHealth\":10000,\"physicalPower\":-10000}")), out _, out _));
    }

    private sealed class Sink : IContentFileSink
    {
        public List<ContentItemSpec> Items { get; } = new();
        public bool IsDefined(string id) => false;
        public void DefineItem(ContentItemSpec spec) => Items.Add(spec);
    }
}
