using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Game-free argument and result helpers for the debug-bridge <c>prefab</c> op (and the
/// name/hash split <c>spawn</c> already uses). Live collection lookups stay in
/// <c>EntityOps</c> so this file can compile in the test project without interop.
/// </summary>
internal static class PrefabInspectArgs
{
    /// <summary>
    /// A prefab argument is either an integer PrefabGUID hash or a spawnable name the
    /// collection maps will resolve. Hash wins the same way <c>spawn</c> does: a JSON
    /// number, or a string that parses as an int, never reaches the name lookups.
    /// </summary>
    public readonly struct Key
    {
        public readonly bool IsHash;
        public readonly int Hash;
        public readonly string Name;

        public Key(int hash)
        {
            IsHash = true;
            Hash = hash;
            Name = "";
        }

        public Key(string name)
        {
            IsHash = false;
            Hash = 0;
            Name = name;
        }
    }

    /// <summary>
    /// <c>args.prefab</c> is required: JSON number, numeric string, or spawnable name.
    /// </summary>
    public static Key Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("prefab", out var el))
            throw new ArgumentException("args.prefab is required (int GUID hash or spawnable name)");

        if (el.ValueKind == JsonValueKind.Number)
        {
            if (!el.TryGetInt32(out var n))
                throw new ArgumentException("args.prefab must be an int hash or a string name");
            return new Key(n);
        }

        if (el.ValueKind == JsonValueKind.String)
        {
            var text = (el.GetString() ?? "").Trim();
            if (text.Length == 0)
                throw new ArgumentException("args.prefab is empty");
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hash))
                return new Key(hash);
            return new Key(text);
        }

        throw new ArgumentException("args.prefab must be an int hash or a string name");
    }

    /// <summary>
    /// Optional <c>args.component</c> for a field dump of one type on the prefab entity.
    /// Empty / omitted is null (no dump).
    /// </summary>
    public static string? OptionalComponent(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("component", out var el))
            return null;
        if (el.ValueKind != JsonValueKind.String)
            return null;
        var value = el.GetString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Error when a non-numeric name missed every spawn-style lookup. Lists the three
    /// that ran: PrefabLookupMap.TryGetPrefabGuidWithName, the spawnable dictionary,
    /// then int-hash parse (already failed before the name path).
    /// </summary>
    public static string UnknownNameError(string name) =>
        $"no prefab named '{name}'. Tried PrefabLookupMap.TryGetPrefabGuidWithName, _SpawnableNameToPrefabGuidDictionary, and int hash. Pass a spawnable name the PrefabLookupMap knows, or the integer PrefabGUID hash.";

    /// <summary>
    /// JSON result for a found collection entity. <paramref name="fields"/> is omitted
    /// unless the caller asked for a component dump.
    /// </summary>
    public static Dictionary<string, object?> Result(
        int guid,
        string? name,
        string entity,
        IReadOnlyList<string> components,
        IReadOnlyDictionary<string, object?>? fields)
    {
        var result = new Dictionary<string, object?>
        {
            ["guid"] = guid,
            ["name"] = name,
            ["entity"] = entity,
            ["components"] = components,
        };
        if (fields != null)
            result["fields"] = fields;
        return result;
    }
}
