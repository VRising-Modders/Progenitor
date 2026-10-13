using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Progenitor.Native;

/// <summary>
/// One side's address map for one GameAssembly.dll build. The JSON is produced by
/// <c>tools/addresslib/build.mjs</c> and shipped with Core (embedded resource, or the
/// exact <c>{side}-{build}.json</c> next to Core in <c>BepInEx/progenitor-core/</c>). Parsing never throws: a
/// truncated file must not take down plugin load the way a missing native offset would.
/// <see cref="AddressLibrary"/> refuses a table whose <see cref="Side"/> / <see cref="Build"/>
/// do not match the running process even when the filename does.
/// </summary>
public sealed class AddressTable
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public AddressTable(
        string side,
        string build,
        long imageBase,
        string generatedUtc,
        IReadOnlyDictionary<string, long> entries)
    {
        Side = side ?? "";
        Build = build ?? "";
        ImageBase = imageBase;
        GeneratedUtc = generatedUtc ?? "";
        Entries = entries ?? new Dictionary<string, long>(StringComparer.Ordinal);
        Count = Entries.Count;
    }

    public string Side { get; }
    public string Build { get; }
    public long ImageBase { get; }
    public string GeneratedUtc { get; }
    public int Count { get; }
    public IReadOnlyDictionary<string, long> Entries { get; }

    /// <summary>Look up a relative address by stable id. Missing ids return false; they never throw.</summary>
    public bool TryGetImageOffset(string id, out long imageOffset)
    {
        imageOffset = 0;
        if (string.IsNullOrEmpty(id)) return false;
        return Entries.TryGetValue(id, out imageOffset);
    }

    /// <summary>
    /// Parse a generator JSON object. Header fields that are missing become empty/zero;
    /// <c>entries</c> that are not an object are treated as empty. Returns false with a
    /// reason instead of throwing, because Core loads this from a hook-adjacent path.
    /// </summary>
    public static bool TryParse(string json, out AddressTable? table, out string? error)
    {
        table = null;
        error = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            error = "JSON is empty.";
            return false;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: ParseOptions);
        }
        catch (Exception e)
        {
            error = "JSON did not parse: " + e.Message;
            return false;
        }

        if (root is not JsonObject obj)
        {
            error = "JSON root is not an object.";
            return false;
        }

        var side = ReadString(obj, "side");
        var build = ReadString(obj, "build");
        var generatedUtc = ReadString(obj, "generatedUtc");
        var imageBase = ReadInt64(obj, "imageBase", AddressId.DefaultImageBase);
        var entries = ReadEntries(obj);

        table = new AddressTable(side, build, imageBase, generatedUtc, entries);
        return true;
    }

    /// <summary>Same shape the generator writes, keys sorted so a round trip is stable.</summary>
    public string ToJson()
    {
        var entries = new JsonObject();
        var keys = new List<string>(Entries.Keys);
        keys.Sort(StringComparer.Ordinal);
        foreach (var key in keys)
            entries[key] = JsonValue.Create(Entries[key]);

        var obj = new JsonObject
        {
            ["side"] = Side,
            ["build"] = Build,
            ["imageBase"] = ImageBase,
            ["generatedUtc"] = GeneratedUtc,
            ["count"] = Count,
            ["entries"] = entries,
        };
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>Read a UTF-8 file with <see cref="TryParse"/>. Missing files are a false, not an exception to the caller.</summary>
    public static bool TryLoadFile(string path, out AddressTable? table, out string? error)
    {
        table = null;
        error = null;
        if (string.IsNullOrEmpty(path))
        {
            error = "path is empty.";
            return false;
        }
        try
        {
            if (!File.Exists(path))
            {
                error = "file not found: " + path;
                return false;
            }
            return TryParse(File.ReadAllText(path), out table, out error);
        }
        catch (Exception e)
        {
            error = "failed to read " + path + ": " + e.Message;
            table = null;
            return false;
        }
    }

    private static string ReadString(JsonObject obj, string name)
    {
        if (!TryGetProperty(obj, name, out var node) || node is null) return "";
        if (node is JsonValue value && value.TryGetValue<string>(out var s)) return s ?? "";
        return node.ToString() ?? "";
    }

    private static long ReadInt64(JsonObject obj, string name, long fallback)
    {
        if (!TryGetProperty(obj, name, out var node) || node is null) return fallback;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var n)) return n;
            if (value.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
            {
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex))
                    return hex;
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec))
                    return dec;
            }
        }
        return fallback;
    }

    private static Dictionary<string, long> ReadEntries(JsonObject obj)
    {
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        if (!TryGetProperty(obj, "entries", out var node) || node is not JsonObject entries)
            return map;
        foreach (var kv in entries)
        {
            if (string.IsNullOrEmpty(kv.Key) || kv.Value is null) continue;
            if (kv.Value is JsonValue value && value.TryGetValue<long>(out var imageOffset))
                map[kv.Key] = imageOffset;
        }
        return map;
    }

    private static bool TryGetProperty(JsonObject obj, string name, out JsonNode? node)
    {
        if (obj.TryGetPropertyValue(name, out node)) return true;
        foreach (var kv in obj)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                node = kv.Value;
                return true;
            }
        }
        node = null;
        return false;
    }
}
