using System;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// InspectArgs is the debug-bridge <c>inspect</c> policy: the entity argument, the buffer
/// <c>depth</c> and byte-budget clamps, the <c>only</c> filter, and the budget that stops the
/// walk once the cap is reached. InspectOps applies it against a live entity; these tests
/// cover the parts that do not need the game.
/// </summary>
public class DevInspectArgsTests
{
    [Fact]
    public void Parse_requires_an_entity()
    {
        var ex = Assert.Throws<ArgumentException>(() => InspectArgs.Parse(Json("{}")));
        Assert.Contains("args.entity is required", ex.Message);
        Assert.Throws<ArgumentException>(() => InspectArgs.Parse(default));
    }

    [Fact]
    public void Parse_refuses_an_entity_that_is_not_index_version()
    {
        var ex = Assert.Throws<ArgumentException>(() => InspectArgs.Parse(Json("{\"entity\":\"45\"}")));
        Assert.Contains("must be 'index:version'", ex.Message);
        Assert.Throws<ArgumentException>(() => InspectArgs.Parse(Json("{\"entity\":\"45:\"}")));
        Assert.Throws<ArgumentException>(() => InspectArgs.Parse(Json("{\"entity\":\":1\"}")));
        Assert.Throws<ArgumentException>(() => InspectArgs.Parse(Json("{\"entity\":\"  \"}")));
    }

    [Fact]
    public void Parse_defaults_depth_and_max()
    {
        var r = InspectArgs.Parse(Json("{\"entity\":\"45:1\"}"));
        Assert.Equal("45:1", r.Entity);
        Assert.Equal(InspectArgs.DefaultDepth, r.Depth);
        Assert.Equal(InspectArgs.DefaultMaxBytes, r.MaxBytes);
        Assert.Empty(r.Only);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(8, 8)]
    [InlineData(256, 256)]
    [InlineData(9999, 256)]
    public void Parse_clamps_depth(int input, int expected)
    {
        var r = InspectArgs.Parse(Json("{\"entity\":\"45:1\",\"depth\":" + input + "}"));
        Assert.Equal(expected, r.Depth);
    }

    [Theory]
    [InlineData(0, InspectArgs.MinMaxBytes)]
    [InlineData(16, InspectArgs.MinMaxBytes)]
    [InlineData(4096, 4096)]
    [InlineData(99_999_999, InspectArgs.MaxMaxBytes)]
    public void Parse_clamps_max_bytes(int input, int expected)
    {
        var r = InspectArgs.Parse(Json("{\"entity\":\"45:1\",\"max\":" + input + "}"));
        Assert.Equal(expected, r.MaxBytes);
    }

    [Fact]
    public void Parse_reads_numeric_strings_for_depth_and_max()
    {
        var r = InspectArgs.Parse(Json("{\"entity\":\"45:1\",\"depth\":\"4\",\"max\":\"2048\"}"));
        Assert.Equal(4, r.Depth);
        Assert.Equal(2048, r.MaxBytes);
    }

    [Fact]
    public void Parse_only_accepts_a_string_or_an_array()
    {
        Assert.Equal(new[] { "Health" }, InspectArgs.Parse(Json("{\"entity\":\"45:1\",\"only\":\"Health\"}")).Only);
        Assert.Equal(
            new[] { "Health", "ProjectM.Blood" },
            InspectArgs.Parse(Json("{\"entity\":\"45:1\",\"only\":[\"Health\",\" ProjectM.Blood \"]}")).Only);
        Assert.Throws<ArgumentException>(() => InspectArgs.Parse(Json("{\"entity\":\"45:1\",\"only\":7}")));
        Assert.Throws<ArgumentException>(() => InspectArgs.Parse(Json("{\"entity\":\"45:1\",\"only\":[1]}")));
    }

    [Fact]
    public void Wants_matches_everything_when_only_is_empty()
    {
        var r = InspectArgs.Parse(Json("{\"entity\":\"45:1\"}"));
        Assert.True(r.Wants("ProjectM.Health"));
        Assert.True(r.Wants("Unity.Transforms.Translation"));
    }

    [Fact]
    public void Wants_matches_a_simple_name_against_the_full_name()
    {
        var r = InspectArgs.Parse(Json("{\"entity\":\"45:1\",\"only\":[\"Health\"]}"));
        Assert.True(r.Wants("ProjectM.Health"));
        Assert.True(r.Wants("Health"));
        Assert.False(r.Wants("ProjectM.Blood"));
        // A suffix that is not a namespace boundary must not match.
        Assert.False(r.Wants("ProjectM.MaxHealth"));
    }

    [Fact]
    public void Wants_is_case_insensitive_and_matches_a_full_name()
    {
        var r = InspectArgs.Parse(Json("{\"entity\":\"45:1\",\"only\":[\"projectm.health\"]}"));
        Assert.True(r.Wants("ProjectM.Health"));
    }

    [Fact]
    public void Budget_spends_down_to_the_cap()
    {
        var budget = new InspectArgs.Budget(4096);
        Assert.True(budget.TryTake(4000));
        Assert.Equal(4000, budget.Used);
        Assert.False(budget.Exhausted);
        Assert.True(budget.TryTake(96));
        Assert.Equal(4096, budget.Used);
        Assert.False(budget.Exhausted);
    }

    [Fact]
    public void Budget_refuses_and_latches_once_the_cap_is_reached()
    {
        var budget = new InspectArgs.Budget(2048);
        Assert.True(budget.TryTake(2000));
        Assert.False(budget.TryTake(100));
        Assert.True(budget.Exhausted);
        // Latched: a small component later in the walk must not sneak past the cap and make
        // the result look complete.
        Assert.False(budget.TryTake(1));
        Assert.Equal(2000, budget.Used);
    }

    [Fact]
    public void Budget_clamps_its_own_cap()
    {
        Assert.Equal(InspectArgs.MinMaxBytes, new InspectArgs.Budget(1).MaxBytes);
        Assert.Equal(InspectArgs.MaxMaxBytes, new InspectArgs.Budget(int.MaxValue).MaxBytes);
    }

    [Fact]
    public void Budget_treats_a_negative_request_as_zero()
    {
        var budget = new InspectArgs.Budget(1024);
        Assert.True(budget.TryTake(-8));
        Assert.Equal(0, budget.Used);
    }

    [Fact]
    public void ComponentRow_omits_fields_and_skipped_when_absent()
    {
        var row = InspectArgs.ComponentRow("ProjectM.Health", "component", 32, null, null);
        Assert.Equal("ProjectM.Health", row["component"]);
        Assert.Equal("component", row["kind"]);
        Assert.Equal(32, row["size"]);
        Assert.False(row.ContainsKey("fields"));
        Assert.False(row.ContainsKey("skipped"));
    }

    [Fact]
    public void BufferRow_flags_truncated_elements()
    {
        var full = InspectArgs.BufferRow("ProjectM.BuffBuffer", 16, 2, new object?[] { 1, 2 }, null);
        Assert.False(full.ContainsKey("elementsTruncated"));

        var partial = InspectArgs.BufferRow("ProjectM.BuffBuffer", 16, 40, new object?[] { 1, 2 }, null);
        Assert.Equal(true, partial["elementsTruncated"]);
        Assert.Equal(40, partial["length"]);
    }

    [Fact]
    public void Result_reports_the_budget_and_where_it_stopped()
    {
        var budget = new InspectArgs.Budget(2048);
        budget.TryTake(2000);
        budget.TryTake(100);
        var result = InspectArgs.Result("45:1", "TestAdmin", null, 12, 4, budget, "ProjectM.Blood", Array.Empty<System.Collections.Generic.Dictionary<string, object?>>());

        Assert.Equal("45:1", result["entity"]);
        Assert.Equal("TestAdmin", result["name"]);
        Assert.Equal(12, result["count"]);
        Assert.Equal(4, result["depth"]);
        Assert.Equal(2000, result["bytes"]);
        Assert.Equal(2048, result["maxBytes"]);
        Assert.Equal(true, result["truncated"]);
        Assert.Equal("ProjectM.Blood", result["truncatedAt"]);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
}
