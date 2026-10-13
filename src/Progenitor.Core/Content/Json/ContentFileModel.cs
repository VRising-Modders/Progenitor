using System;
using System.Collections.Generic;

namespace Progenitor.Content.Json;

/// <summary>
/// The parsed shape of one JSON content definition file (roadmap M4.6, <c>docs/spec.html</c>
/// "Data files"). Phase 1 carries items only.
///
/// <para>Everything in this file is game-free on purpose: the model, the parse, the validation
/// text and the mapping to builder calls are all testable without a world, and the only
/// game-touching part of the feature is <c>ContentFiles</c>, which reads the folder and drives
/// <see cref="IContentFileSink"/>.</para>
/// </summary>
internal static class ContentFileSchema
{
    /// <summary>The only <c>schema</c> value this build accepts.</summary>
    public const int Current = 1;

    /// <summary>Folder name probed next to <c>localization</c> and <c>assets</c>.</summary>
    public const string FolderName = "content";

    /// <summary>Root object fields phase 1 knows.</summary>
    public static readonly IReadOnlyList<string> RootFields = new[] { "schema", "items" };

    /// <summary>Item object fields phase 1 knows.</summary>
    public static readonly IReadOnlyList<string> ItemFields =
        new[] { "id", "cloneOf", "name", "description", "stack", "statBonuses" };
}

/// <summary>
/// One item entry. <see cref="CloneOfName"/> and <see cref="CloneOfGuidHash"/> are exclusive:
/// a JSON string is a spawnable name (resolved at apply time, exactly as
/// <c>DefineItem(...).CloneOf(string)</c> does), a JSON number is a raw
/// <c>PrefabGUID.GuidHash</c>.
/// </summary>
internal sealed class ContentItemSpec
{
    public ContentItemSpec(
        int index,
        string id,
        string? cloneOfName,
        int? cloneOfGuidHash,
        string? name,
        string? description,
        int? stack,
        ItemStatBonuses? statBonuses = null)
    {
        Index = index;
        Id = id;
        CloneOfName = cloneOfName;
        CloneOfGuidHash = cloneOfGuidHash;
        Name = name;
        Description = description;
        Stack = stack;
        StatBonuses = statBonuses;
    }

    /// <summary>Position in the file's <c>items</c> array. Every error line names it.</summary>
    public int Index { get; }

    /// <summary>Spawnable name and hash input; 1–63 ASCII, the registry's own rule.</summary>
    public string Id { get; }

    /// <summary>Spawnable name to clone, or null when the file gave a numeric GUID.</summary>
    public string? CloneOfName { get; }

    /// <summary>Raw GUID hash to clone, or null when the file gave a spawnable name.</summary>
    public int? CloneOfGuidHash { get; }

    /// <summary>Display name, or null to keep the source prefab's name.</summary>
    public string? Name { get; }

    /// <summary>Tooltip text, or null to keep the source's (or <c>&lt;id&gt;.desc</c> from <c>en.json</c>).</summary>
    public string? Description { get; }

    /// <summary>Stack size, or null to keep the source's <c>ItemData.MaxAmount</c>.</summary>
    public int? Stack { get; }

    /// <summary>Additive equipment stats, or null to keep the source unchanged.</summary>
    public ItemStatBonuses? StatBonuses { get; }
}

/// <summary>One file's accepted content. Only produced when the file had no errors at all.</summary>
internal sealed class ContentFileDocument
{
    public ContentFileDocument(int schema, IReadOnlyList<ContentItemSpec> items)
    {
        Schema = schema;
        Items = items;
    }

    public int Schema { get; }

    public IReadOnlyList<ContentItemSpec> Items { get; }
}

/// <summary>
/// One file as the loader sees it: a label used verbatim in every log line
/// (<c>Progenitor.Sample/content/items.json</c>) and its text.
/// </summary>
internal readonly struct ContentFileSource
{
    public ContentFileSource(string label, string json)
    {
        Label = label ?? throw new ArgumentNullException(nameof(label));
        Json = json ?? "";
    }

    public string Label { get; }

    public string Json { get; }
}

/// <summary>What one <c>ContentFileLoad.Run</c> did. Info lines are logged at info, errors at error.</summary>
internal sealed class ContentFileLoadResult
{
    public ContentFileLoadResult(
        IReadOnlyList<string> info,
        IReadOnlyList<string> errors,
        int filesAccepted,
        int filesRefused,
        int itemsDefined)
    {
        Info = info;
        Errors = errors;
        FilesAccepted = filesAccepted;
        FilesRefused = filesRefused;
        ItemsDefined = itemsDefined;
    }

    public IReadOnlyList<string> Info { get; }

    public IReadOnlyList<string> Errors { get; }

    public int FilesAccepted { get; }

    public int FilesRefused { get; }

    public int ItemsDefined { get; }
}
