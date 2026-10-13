using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Progenitor.Persistence;

/// <summary>
/// Merges a store's on-disk JSON with keys written before that file was known. Disk is the base;
/// overlay keys replace or delete. Without this, an OnLoad <c>Set</c> on a store created before
/// <c>WorldReady</c> would skip the disk load (the store is already dirty) and the next save would
/// write only the in-memory keys, dropping everything else in the file.
/// Kept free of BepInEx and game types so the test project can compile it.
/// </summary>
public static class StoreMerge
{
    /// <summary>
    /// Build a new object from <paramref name="disk"/> then apply <paramref name="overlay"/>.
    /// A null overlay value deletes that key. Neither argument is mutated.
    /// </summary>
    public static JsonObject Merge(JsonObject? disk, IReadOnlyDictionary<string, JsonNode?> overlay)
    {
        var result = new JsonObject();
        if (disk != null)
        {
            foreach (var kv in disk)
                result[kv.Key] = Clone(kv.Value);
        }

        if (overlay == null) return result;

        foreach (var kv in overlay)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            if (kv.Value is null)
                result.Remove(kv.Key);
            else
                result[kv.Key] = Clone(kv.Value);
        }

        return result;
    }

    /// <summary>
    /// Three-layer merge used when relocating onto a dest file that already exists: keys only on
    /// <paramref name="older"/> are kept, <paramref name="newer"/> replaces matching keys, then
    /// <paramref name="overlay"/> (dirty in-memory keys, including deletes) wins. Neither object
    /// is mutated. Precedence is old &lt; dest &lt; dirty.
    /// </summary>
    public static JsonObject Merge(JsonObject? older, JsonObject? newer, IReadOnlyDictionary<string, JsonNode?> overlay)
    {
        return Merge(Merge(older, AsOverlay(newer)), overlay);
    }

    /// <summary>
    /// Parse a store file body. A missing, empty, or non-object document is an empty object
    /// (the overlay then becomes the whole store) rather than a hard failure.
    /// </summary>
    public static bool TryParseObject(string? json, out JsonObject obj, out string? error)
    {
        obj = new JsonObject();
        error = null;
        if (string.IsNullOrWhiteSpace(json)) return true;

        try
        {
            var node = JsonNode.Parse(json);
            if (node is JsonObject parsed)
            {
                obj = parsed;
                return true;
            }

            error = "root is not a JSON object";
            return false;
        }
        catch (Exception e)
        {
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Detach a node from its parent. <see cref="JsonNode.DeepClone"/> is .NET 8; Core targets
    /// net6 so this round-trips through JSON instead.
    /// </summary>
    public static JsonNode? Clone(JsonNode? node)
    {
        if (node is null) return null;
        return JsonNode.Parse(node.ToJsonString());
    }

    /// <summary>
    /// Treat a JSON object as a replace-overlay: every property writes, none delete. Used so
    /// dest-file keys can sit between an older file and the dirty overlay without a third Merge
    /// shape. Empty keys are skipped the same way <see cref="Merge(JsonObject?, IReadOnlyDictionary{string, JsonNode?})"/> does.
    /// </summary>
    private static Dictionary<string, JsonNode?> AsOverlay(JsonObject? obj)
    {
        if (obj == null || obj.Count == 0)
            return new Dictionary<string, JsonNode?>(StringComparer.Ordinal);

        var overlay = new Dictionary<string, JsonNode?>(obj.Count, StringComparer.Ordinal);
        foreach (var kv in obj)
        {
            if (string.IsNullOrEmpty(kv.Key)) continue;
            overlay[kv.Key] = kv.Value;
        }
        return overlay;
    }
}
