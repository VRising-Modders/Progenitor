using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// SpawnArgs is the debug-bridge <c>spawn</c> count/xyz parse and the JSON result
/// that now includes <c>placed</c>. EntityOps.Spawn applies it against a live world;
/// these tests cover the parts that do not need the game. Argument parsing matches
/// the previous inline EntityOps helpers (count fallback + clamp, required x/y/z).
/// </summary>
public class SpawnArgsTests
{
    [Fact]
    public void ParseCount_defaults_to_one_when_omitted()
    {
        Assert.Equal(1, SpawnArgs.ParseCount(Json("{\"prefab\":\"CHAR_Critter_Rat\",\"x\":0,\"y\":0,\"z\":0}")));
        Assert.Equal(1, SpawnArgs.ParseCount(default));
    }

    [Fact]
    public void ParseCount_reads_a_json_number()
    {
        Assert.Equal(3, SpawnArgs.ParseCount(Json("{\"count\":3}")));
    }

    [Fact]
    public void ParseCount_reads_a_numeric_string()
    {
        Assert.Equal(2, SpawnArgs.ParseCount(Json("{\"count\":\"2\"}")));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-4, 1)]
    [InlineData(1, 1)]
    [InlineData(256, 256)]
    [InlineData(257, 256)]
    [InlineData(99999, 256)]
    public void ParseCount_clamps_to_one_through_max(int input, int expected)
    {
        Assert.Equal(expected, SpawnArgs.ParseCount(Json("{\"count\":" + input + "}")));
    }

    [Fact]
    public void ParseCount_unparsable_falls_back_to_one()
    {
        Assert.Equal(1, SpawnArgs.ParseCount(Json("{\"count\":true}")));
        Assert.Equal(1, SpawnArgs.ParseCount(Json("{\"count\":1.5}")));
        Assert.Equal(1, SpawnArgs.ParseCount(Json("{\"count\":\"nope\"}")));
    }

    [Fact]
    public void ParsePosition_reads_json_numbers()
    {
        SpawnArgs.ParsePosition(Json("{\"x\":-2125,\"y\":5,\"z\":-3167.6}"), out var x, out var y, out var z);
        Assert.Equal(-2125f, x);
        Assert.Equal(5f, y);
        Assert.Equal(-3167.6f, z, 3);
    }

    [Fact]
    public void ParsePosition_reads_numeric_strings()
    {
        SpawnArgs.ParsePosition(Json("{\"x\":\"1\",\"y\":\"2.5\",\"z\":\"-3\"}"), out var x, out var y, out var z);
        Assert.Equal(1f, x);
        Assert.Equal(2.5f, y);
        Assert.Equal(-3f, z);
    }

    [Fact]
    public void ParsePosition_requires_x_y_z()
    {
        var missingX = Assert.Throws<ArgumentException>(() =>
            SpawnArgs.ParsePosition(Json("{\"y\":0,\"z\":0}"), out _, out _, out _));
        Assert.Contains("args.x is required", missingX.Message);

        var missingY = Assert.Throws<ArgumentException>(() =>
            SpawnArgs.ParsePosition(Json("{\"x\":0,\"z\":0}"), out _, out _, out _));
        Assert.Contains("args.y is required", missingY.Message);

        var missingZ = Assert.Throws<ArgumentException>(() =>
            SpawnArgs.ParsePosition(Json("{\"x\":0,\"y\":0}"), out _, out _, out _));
        Assert.Contains("args.z is required", missingZ.Message);

        Assert.Throws<ArgumentException>(() => SpawnArgs.ParsePosition(default, out _, out _, out _));
    }

    [Fact]
    public void ParsePosition_refuses_a_non_number()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            SpawnArgs.ParsePosition(Json("{\"x\":true,\"y\":0,\"z\":0}"), out _, out _, out _));
        Assert.Contains("args.x must be a number", ex.Message);
    }

    [Fact]
    public void IsPlaced_is_true_within_epsilon_and_false_for_the_prefab_home_snap()
    {
        Assert.True(SpawnArgs.IsPlaced(-2125f, 5f, -3167.6f, -2125f, 5f, -3167.6f));
        Assert.True(SpawnArgs.IsPlaced(0f, 0f, 0f, 0.5f, 0f, 0f));
        Assert.False(SpawnArgs.IsPlaced(-2125f, 5f, -3167.6f, -159.6f, 0f, -479.6f));
        Assert.False(SpawnArgs.IsPlaced(0f, 0f, 0f, null, 0f, 0f));
    }

    [Fact]
    public void Result_shape_includes_placed_on_each_entity()
    {
        var row = SpawnArgs.EntityRow("88:1", -123456, "CHAR_Critter_Rat", -2125f, 5f, -3167.6f, placed: true);
        var json = JsonSerializer.Serialize(SpawnArgs.Result(
            count: 1,
            prefabHash: -123456,
            prefabName: "CHAR_Critter_Rat",
            entities: new[] { row }));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("count").GetInt32());
        Assert.Equal(-123456, root.GetProperty("prefab").GetProperty("hash").GetInt32());
        Assert.Equal("CHAR_Critter_Rat", root.GetProperty("prefab").GetProperty("name").GetString());
        var entity = root.GetProperty("entities")[0];
        Assert.Equal("88:1", entity.GetProperty("entity").GetString());
        Assert.True(entity.GetProperty("placed").GetBoolean());
        Assert.Equal(-2125f, entity.GetProperty("position").GetProperty("x").GetSingle());
        Assert.Equal(5f, entity.GetProperty("position").GetProperty("y").GetSingle());
    }

    [Fact]
    public void Result_shape_reports_placed_false_and_a_null_position()
    {
        var row = SpawnArgs.EntityRow("88:1", 1, "Item_Ingredient_Stone", null, null, null, placed: false);
        var json = JsonSerializer.Serialize(SpawnArgs.Result(1, 1, "Item_Ingredient_Stone", new[] { row }));
        using var doc = JsonDocument.Parse(json);
        var entity = doc.RootElement.GetProperty("entities")[0];
        Assert.False(entity.GetProperty("placed").GetBoolean());
        Assert.Equal(JsonValueKind.Null, entity.GetProperty("position").ValueKind);
    }

    [Fact]
    public void Readback_delay_is_the_one_second_PostDelayed_shape()
    {
        Assert.Equal(1d, SpawnArgs.ReadbackDelaySeconds);
        Assert.Equal(1000, SpawnArgs.ReadbackWaitMs);
    }

    [Fact]
    public void EntityRow_keeps_the_prefab_hash_and_name()
    {
        var row = SpawnArgs.EntityRow("1:1", 99, null, 0f, 0f, 0f, placed: true);
        Assert.Equal("1:1", row["entity"]);
        Assert.True((bool)row["placed"]!);
        var prefab = Assert.IsType<Dictionary<string, object?>>(row["prefab"]);
        Assert.Equal(99, prefab["hash"]);
        Assert.Null(prefab["name"]);
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
