using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Progenitor.Models;

/// <summary>
/// Prefab name → <see cref="ModelSection"/>, from the JSON that
/// <c>tools/catalog/model-sections.mjs</c> writes for one game build:
/// <code>{ "catalogSha256": "…", "models": [ { "name": "HYB_NPCDracula_Evolved", "section": "ddfc…:373", "archive": "7111…" } ] }</code>
/// A bare array of the same rows is accepted too. The table is derived data: regenerate it after
/// a game update, because section numbers and archive ids change with the build. Parsed with
/// <see cref="JsonDocument"/> only, so no serializer type cache holds this collectible assembly.
/// </summary>
public sealed class ModelCatalog
{
    private readonly Dictionary<string, ModelSection> _byName;

    private ModelCatalog(Dictionary<string, ModelSection> byName, string? buildStamp)
    {
        _byName = byName;
        BuildStamp = buildStamp;
    }

    /// <summary>An empty catalog: every lookup misses.</summary>
    public static ModelCatalog Empty { get; } = new(new Dictionary<string, ModelSection>(StringComparer.Ordinal), null);

    /// <summary><c>catalogSha256</c> of the build the table was generated from, when present.</summary>
    public string? BuildStamp { get; }

    public int Count => _byName.Count;

    public IEnumerable<string> Names => _byName.Keys;

    public bool TryFind(string name, out ModelSection section) => _byName.TryGetValue(name, out section);

    public static ModelCatalog Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("model section table not found", path);
        return Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Parses the table. A row whose section does not parse is an error rather than a silent
    /// skip, so a truncated or hand-edited table fails loudly. A duplicate name keeps the first
    /// row (the generator writes each name once; a duplicate means two archives share a name).
    /// </summary>
    public static ModelCatalog Parse(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        JsonElement rows;
        string? stamp = null;
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            rows = doc.RootElement;
        }
        else if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("models", out rows))
        {
            if (doc.RootElement.TryGetProperty("catalogSha256", out var s) && s.ValueKind == JsonValueKind.String)
                stamp = s.GetString();
        }
        else
        {
            throw new FormatException("model section table must be an array or an object with a 'models' array.");
        }

        if (rows.ValueKind != JsonValueKind.Array)
            throw new FormatException("'models' must be an array.");

        var byName = new Dictionary<string, ModelSection>(StringComparer.Ordinal);
        int i = 0;
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String
                || !row.TryGetProperty("section", out var s) || s.ValueKind != JsonValueKind.String)
                throw new FormatException("row " + i + " needs string 'name' and 'section'.");
            string name = n.GetString()!;
            if (!ModelSection.TryParse(s.GetString(), out var section))
                throw new FormatException("row " + i + " ('" + name + "'): bad section '" + s.GetString() + "'.");
            if (!byName.ContainsKey(name)) byName[name] = section;
            i++;
        }

        return new ModelCatalog(byName, stamp);
    }
}
