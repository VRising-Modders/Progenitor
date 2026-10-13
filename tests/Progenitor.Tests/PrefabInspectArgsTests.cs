using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// PrefabInspectArgs is the debug-bridge <c>prefab</c> (and spawn) name/hash split,
/// the unknown-name error that lists the three lookups, and the JSON result shape.
/// EntityOps.Prefab applies it against PrefabCollectionSystem; these tests cover the
/// parts that do not need the game.
/// </summary>
public class PrefabInspectArgsTests
{
    [Fact]
    public void Parse_json_number_is_a_hash()
    {
        var key = PrefabInspectArgs.Parse(Json("{\"prefab\":-123456}"));
        Assert.True(key.IsHash);
        Assert.Equal(-123456, key.Hash);
        Assert.Equal("", key.Name);
    }

    [Fact]
    public void Parse_numeric_string_is_a_hash()
    {
        var key = PrefabInspectArgs.Parse(Json("{\"prefab\":\"-123456\"}"));
        Assert.True(key.IsHash);
        Assert.Equal(-123456, key.Hash);
    }

    [Fact]
    public void Parse_spawnable_name_is_not_a_hash()
    {
        var key = PrefabInspectArgs.Parse(Json("{\"prefab\":\"Item_Ingredient_Stone\"}"));
        Assert.False(key.IsHash);
        Assert.Equal("Item_Ingredient_Stone", key.Name);
    }

    [Fact]
    public void Parse_trims_a_spawnable_name()
    {
        var key = PrefabInspectArgs.Parse(Json("{\"prefab\":\"  Item_Ingredient_Stone  \"}"));
        Assert.Equal("Item_Ingredient_Stone", key.Name);
    }

    [Fact]
    public void Parse_refuses_a_missing_prefab()
    {
        var ex = Assert.Throws<ArgumentException>(() => PrefabInspectArgs.Parse(Json("{}")));
        Assert.Contains("args.prefab is required", ex.Message);
        Assert.Throws<ArgumentException>(() => PrefabInspectArgs.Parse(default));
    }

    [Fact]
    public void Parse_refuses_an_empty_name()
    {
        var ex = Assert.Throws<ArgumentException>(() => PrefabInspectArgs.Parse(Json("{\"prefab\":\"  \"}")));
        Assert.Contains("args.prefab is empty", ex.Message);
    }

    [Fact]
    public void Parse_refuses_a_non_name_non_hash()
    {
        var ex = Assert.Throws<ArgumentException>(() => PrefabInspectArgs.Parse(Json("{\"prefab\":true}")));
        Assert.Contains("int hash or a string name", ex.Message);
    }

    [Fact]
    public void OptionalComponent_is_null_when_omitted_or_empty()
    {
        Assert.Null(PrefabInspectArgs.OptionalComponent(Json("{\"prefab\":\"x\"}")));
        Assert.Null(PrefabInspectArgs.OptionalComponent(Json("{\"prefab\":\"x\",\"component\":\"\"}")));
        Assert.Null(PrefabInspectArgs.OptionalComponent(Json("{\"prefab\":\"x\",\"component\":\"   \"}")));
        Assert.Null(PrefabInspectArgs.OptionalComponent(default));
    }

    [Fact]
    public void OptionalComponent_trims_a_type_name()
    {
        Assert.Equal(
            "ProjectM.ItemData",
            PrefabInspectArgs.OptionalComponent(Json("{\"prefab\":\"x\",\"component\":\"  ProjectM.ItemData  \"}")));
    }

    [Fact]
    public void UnknownNameError_lists_the_three_lookups()
    {
        var msg = PrefabInspectArgs.UnknownNameError("no-such-prefab");
        Assert.Contains("no prefab named 'no-such-prefab'", msg);
        Assert.Contains("PrefabLookupMap.TryGetPrefabGuidWithName", msg);
        Assert.Contains("_SpawnableNameToPrefabGuidDictionary", msg);
        Assert.Contains("int hash", msg);
    }

    [Fact]
    public void Result_shape_omits_fields_until_a_component_is_requested()
    {
        var components = new[] { "Unity.Entities.Prefab", "Stunlock.Core.PrefabGUID" };
        var json = JsonSerializer.Serialize(PrefabInspectArgs.Result(
            guid: -123456,
            name: "Item_Ingredient_Stone",
            entity: "12:1",
            components,
            fields: null));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(-123456, root.GetProperty("guid").GetInt32());
        Assert.Equal("Item_Ingredient_Stone", root.GetProperty("name").GetString());
        Assert.Equal("12:1", root.GetProperty("entity").GetString());
        Assert.Equal(2, root.GetProperty("components").GetArrayLength());
        Assert.Equal("Unity.Entities.Prefab", root.GetProperty("components")[0].GetString());
        Assert.False(root.TryGetProperty("fields", out _));
    }

    [Fact]
    public void Result_shape_includes_fields_when_a_component_was_dumped()
    {
        var fields = new Dictionary<string, object?> { ["ItemType"] = 1 };
        var json = JsonSerializer.Serialize(PrefabInspectArgs.Result(
            99, "Buff_General_Haste", "7:1", new[] { "ProjectM.Buff" }, fields));
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("fields", out var dumped));
        Assert.Equal(1, dumped.GetProperty("ItemType").GetInt32());
        Assert.Equal(99, doc.RootElement.GetProperty("guid").GetInt32());
        Assert.Equal("Buff_General_Haste", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public void Result_allows_a_null_name_when_the_lookup_map_has_none()
    {
        var json = JsonSerializer.Serialize(PrefabInspectArgs.Result(
            1, null, "1:1", Array.Empty<string>(), null));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("name").ValueKind);
        Assert.Equal(0, doc.RootElement.GetProperty("components").GetArrayLength());
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
