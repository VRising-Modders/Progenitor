using System.Collections.Generic;
using System.Text.Json.Nodes;
using Progenitor.Persistence;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Overlay rules for a store created before the save folder is known: disk is the base,
/// keys written in memory win. This is the F2 finding 2 failure mode (OnLoad Set dirties
/// an empty store, disk load is skipped, next save wipes the rest of the file).
/// </summary>
public class StoreMergeTests
{
    [Fact]
    public void Three_layer_merge_is_old_then_dest_then_dirty()
    {
        var older = Parse("""{"onlyOld":1,"both":10,"destAndDirty":1,"allThree":1}""");
        var newer = Parse("""{"onlyDest":2,"both":20,"destAndDirty":2,"allThree":2}""");
        var overlay = new Dictionary<string, JsonNode?>
        {
            ["destAndDirty"] = JsonValue.Create(3),
            ["allThree"] = JsonValue.Create(3),
            ["onlyDirty"] = JsonValue.Create(4),
        };

        var merged = StoreMerge.Merge(older, newer, overlay);

        Assert.Equal(1, merged["onlyOld"]!.GetValue<int>());
        Assert.Equal(2, merged["onlyDest"]!.GetValue<int>());
        Assert.Equal(20, merged["both"]!.GetValue<int>());
        Assert.Equal(3, merged["destAndDirty"]!.GetValue<int>());
        Assert.Equal(3, merged["allThree"]!.GetValue<int>());
        Assert.Equal(4, merged["onlyDirty"]!.GetValue<int>());
        Assert.Equal(6, merged.Count);
    }

    [Fact]
    public void Three_layer_merge_dirty_null_deletes_even_when_dest_has_the_key()
    {
        var older = Parse("""{"keep":1,"drop":2}""");
        var newer = Parse("""{"keep":3,"drop":4}""");
        var overlay = new Dictionary<string, JsonNode?> { ["drop"] = null };

        var merged = StoreMerge.Merge(older, newer, overlay);

        Assert.Equal(3, merged["keep"]!.GetValue<int>());
        Assert.False(merged.ContainsKey("drop"));
    }

    [Fact]
    public void Overlay_replaces_matching_keys_and_keeps_the_rest_of_disk()
    {
        var disk = Parse("""{"state":{"Loads":5},"other":1,"keep":"yes"}""");
        var overlay = new Dictionary<string, JsonNode?>
        {
            ["state"] = JsonNode.Parse("""{"Loads":6}"""),
        };

        var merged = StoreMerge.Merge(disk, overlay);

        Assert.Equal(6, merged["state"]!["Loads"]!.GetValue<int>());
        Assert.Equal(1, merged["other"]!.GetValue<int>());
        Assert.Equal("yes", merged["keep"]!.GetValue<string>());
        Assert.Equal(3, merged.Count);
    }

    [Fact]
    public void Overlay_null_deletes_a_disk_key()
    {
        var disk = Parse("""{"keep":1,"drop":2}""");
        var overlay = new Dictionary<string, JsonNode?> { ["drop"] = null };

        var merged = StoreMerge.Merge(disk, overlay);

        Assert.Equal(1, merged["keep"]!.GetValue<int>());
        Assert.False(merged.ContainsKey("drop"));
    }

    [Fact]
    public void Overlay_adds_keys_that_were_not_on_disk()
    {
        var disk = Parse("""{"old":1}""");
        var overlay = new Dictionary<string, JsonNode?> { ["new"] = JsonValue.Create(2) };

        var merged = StoreMerge.Merge(disk, overlay);

        Assert.Equal(1, merged["old"]!.GetValue<int>());
        Assert.Equal(2, merged["new"]!.GetValue<int>());
    }

    [Fact]
    public void Null_or_empty_disk_becomes_the_overlay()
    {
        var overlay = new Dictionary<string, JsonNode?> { ["only"] = JsonValue.Create("mem") };

        var fromNull = StoreMerge.Merge(null, overlay);
        var fromEmpty = StoreMerge.Merge(new JsonObject(), overlay);

        Assert.Equal("mem", fromNull["only"]!.GetValue<string>());
        Assert.Equal("mem", fromEmpty["only"]!.GetValue<string>());
        Assert.Single(fromNull);
    }

    [Fact]
    public void Empty_overlay_is_a_clone_of_disk()
    {
        var disk = Parse("""{"a":1}""");
        var merged = StoreMerge.Merge(disk, new Dictionary<string, JsonNode?>());

        Assert.Equal(1, merged["a"]!.GetValue<int>());
        disk["a"] = 99;
        Assert.Equal(1, merged["a"]!.GetValue<int>());
    }

    [Fact]
    public void Merge_does_not_mutate_disk_or_overlay_nodes()
    {
        var disk = Parse("""{"a":1}""");
        var overlayNode = JsonValue.Create(2);
        var overlay = new Dictionary<string, JsonNode?> { ["a"] = overlayNode };

        var merged = StoreMerge.Merge(disk, overlay);
        merged["a"] = 3;

        Assert.Equal(1, disk["a"]!.GetValue<int>());
        Assert.Equal(2, overlayNode.GetValue<int>());
    }

    [Fact]
    public void TryParseObject_empty_or_whitespace_is_an_empty_object()
    {
        Assert.True(StoreMerge.TryParseObject(null, out var a, out var errA));
        Assert.Null(errA);
        Assert.Empty(a);

        Assert.True(StoreMerge.TryParseObject("  \n", out var b, out var errB));
        Assert.Null(errB);
        Assert.Empty(b);
    }

    [Fact]
    public void TryParseObject_non_object_root_fails()
    {
        Assert.False(StoreMerge.TryParseObject("[1,2]", out var obj, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Empty(obj);
    }

    [Fact]
    public void TryParseObject_garbage_fails_without_throwing()
    {
        Assert.False(StoreMerge.TryParseObject("{not json", out var obj, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Empty(obj);
    }

    [Fact]
    public void TryParseObject_reads_a_store_shaped_object()
    {
        Assert.True(StoreMerge.TryParseObject("""{"joins":{"Count":3}}""", out var obj, out var error));
        Assert.Null(error);
        Assert.Equal(3, obj["joins"]!["Count"]!.GetValue<int>());
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();
}
