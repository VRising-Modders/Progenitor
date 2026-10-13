using System;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// GiveArgs is the debug-bridge <c>give</c> policy: who receives the stack, the amount clamp,
/// how "how many actually landed" is derived from whatever the game's inventory add reported
/// as left over, the log line and the JSON result. GiveOps applies it against a live
/// inventory; these tests cover the parts that do not need the game. The prefab argument is
/// the shared <see cref="PrefabInspectArgs"/> parse, covered by its own suite.
/// </summary>
public class DevGiveArgsTests
{
    [Fact]
    public void ParseTarget_user_name()
    {
        var t = GiveArgs.ParseTarget(Json("{\"user\":\"TestAdmin\",\"prefab\":\"Item_Ingredient_Stone\"}"));
        Assert.Equal(GiveArgs.TargetKind.UserName, t.Kind);
        Assert.Equal("TestAdmin", t.Value);
    }

    [Fact]
    public void ParseTarget_trims_the_user_name()
    {
        Assert.Equal("TestAdmin", GiveArgs.ParseTarget(Json("{\"user\":\"  TestAdmin  \"}")).Value);
    }

    [Fact]
    public void ParseTarget_entity_wins_when_both_are_set()
    {
        var t = GiveArgs.ParseTarget(Json("{\"user\":\"TestAdmin\",\"entity\":\"45:1\"}"));
        Assert.Equal(GiveArgs.TargetKind.Entity, t.Kind);
        Assert.Equal("45:1", t.Value);
    }

    [Fact]
    public void ParseTarget_refuses_neither()
    {
        var ex = Assert.Throws<ArgumentException>(() => GiveArgs.ParseTarget(Json("{\"prefab\":\"Item_Ingredient_Stone\"}")));
        Assert.Contains("args.user or args.entity is required", ex.Message);
        Assert.Throws<ArgumentException>(() => GiveArgs.ParseTarget(default));
        Assert.Throws<ArgumentException>(() => GiveArgs.ParseTarget(Json("{\"user\":\"  \",\"entity\":\"\"}")));
    }

    [Fact]
    public void ParseTarget_refuses_a_bare_integer_entity()
    {
        var ex = Assert.Throws<ArgumentException>(() => GiveArgs.ParseTarget(Json("{\"entity\":\"45\"}")));
        Assert.Contains("must be 'index:version'", ex.Message);
    }

    [Fact]
    public void ParseAmount_defaults_to_one()
    {
        Assert.Equal(1, GiveArgs.ParseAmount(Json("{\"user\":\"A\",\"prefab\":\"Item_Ingredient_Stone\"}")));
        Assert.Equal(1, GiveArgs.ParseAmount(default));
    }

    [Fact]
    public void ParseAmount_reads_a_number_or_a_numeric_string()
    {
        Assert.Equal(25, GiveArgs.ParseAmount(Json("{\"amount\":25}")));
        Assert.Equal(25, GiveArgs.ParseAmount(Json("{\"amount\":\"25\"}")));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-9, 1)]
    [InlineData(1, 1)]
    [InlineData(10_000, 10_000)]
    [InlineData(10_001, 10_000)]
    [InlineData(int.MaxValue, 10_000)]
    public void ParseAmount_clamps_to_one_through_max(int input, int expected)
    {
        Assert.Equal(expected, GiveArgs.ParseAmount(Json("{\"amount\":" + input + "}")));
    }

    [Fact]
    public void ParseAmount_unparsable_falls_back_to_one()
    {
        Assert.Equal(1, GiveArgs.ParseAmount(Json("{\"amount\":\"lots\"}")));
        Assert.Equal(1, GiveArgs.ParseAmount(Json("{\"amount\":true}")));
        Assert.Equal(1, GiveArgs.ParseAmount(Json("{\"amount\":2.5}")));
    }

    [Fact]
    public void AddedFromRemainder_is_requested_minus_what_did_not_fit()
    {
        Assert.Equal(25, GiveArgs.AddedFromRemainder(25, 0));
        Assert.Equal(20, GiveArgs.AddedFromRemainder(25, 5));
        Assert.Equal(0, GiveArgs.AddedFromRemainder(25, 25));
    }

    [Fact]
    public void AddedFromRemainder_clamps_a_nonsense_remainder()
    {
        // The game's add reports what is left over; a negative or oversized value must never
        // turn into a negative `added` or one larger than the request.
        Assert.Equal(25, GiveArgs.AddedFromRemainder(25, -3));
        Assert.Equal(0, GiveArgs.AddedFromRemainder(25, 900));
    }

    [Fact]
    public void LogLine_reports_requested_added_and_what_did_not_fit()
    {
        Assert.Equal(
            "Dev: give TestAdmin Item_Ingredient_Stone requested=25 added=20 didNotFit=5",
            GiveArgs.LogLine("TestAdmin", "Item_Ingredient_Stone", 25, 20, 5));
    }

    [Fact]
    public void Result_added_plus_didNotFit_equals_requested()
    {
        var result = GiveArgs.Result("45:1", "TestAdmin", -1234, "Item_Ingredient_Stone", 25, 20, 5, "inventory full");
        Assert.Equal("45:1", result["entity"]);
        Assert.Equal("TestAdmin", result["name"]);
        Assert.Equal(25, result["requested"]);
        Assert.Equal(20, result["added"]);
        Assert.Equal(5, result["didNotFit"]);
        Assert.Equal("inventory full", result["note"]);
        Assert.Equal(25, (int)result["added"]! + (int)result["didNotFit"]!);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
}
