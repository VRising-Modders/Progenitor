using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Progenitor.Install;

/// <summary>
/// LINK1: which rule resolved a loaded plugin to a Thunderstore listing. The order is the order of
/// the evidence, strongest first, and it is part of the readback (<c>page-listing.matched</c>)
/// because the weakest rule is only good enough to <em>show</em> a listing.
/// </summary>
internal enum ListingMatchKind
{
    /// <summary>Nothing named this plugin.</summary>
    None = 0,

    /// <summary>The install ledger's own <c>package:&lt;Owner-Name&gt;@&lt;version&gt;</c> source: Progenitor installed it.</summary>
    Source = 1,

    /// <summary>The inspected-identity cache: a prior inspection read this plugin GUID out of that package's bytes.</summary>
    Identity = 2,

    /// <summary>The listing's own <c>progenitor.json</c> id, or a package name that is the plugin id.</summary>
    Id = 3,

    /// <summary>
    /// The plugin's display name (or its assembly file stem) spells the package name. Good enough to
    /// show the listing's description, README, store link and like count — and nothing else. It is
    /// never evidence that this package's files are the ones on disk, so it must not reach Remove or
    /// a dependency decision.
    /// </summary>
    Name = 4,
}

/// <summary>The readback names for <see cref="ListingMatchKind"/>.</summary>
internal static class ListingMatchKinds
{
    internal const string None = "none";
    internal const string Source = "source";
    internal const string Identity = "identity";
    internal const string Id = "id";
    internal const string Name = "name";

    internal static string NameOf(ListingMatchKind kind) => kind switch
    {
        ListingMatchKind.Source => Source,
        ListingMatchKind.Identity => Identity,
        ListingMatchKind.Id => Id,
        ListingMatchKind.Name => Name,
        _ => None,
    };

    internal static ListingMatchKind Parse(string? name) => (name ?? "").Trim() switch
    {
        Source => ListingMatchKind.Source,
        Identity => ListingMatchKind.Identity,
        Id => ListingMatchKind.Id,
        Name => ListingMatchKind.Name,
        _ => ListingMatchKind.None,
    };

    /// <summary>A match this weak may be shown, never acted on.</summary>
    internal static bool IsDisplayOnly(ListingMatchKind kind) => kind == ListingMatchKind.Name;
}

/// <summary>
/// What is known about one plugin this process has loaded: the BepInEx GUID and name, the version it
/// reports, and the assembly file it came from when anything knows it. No game types — the caller
/// reads these from the Mods inventory, the reloader's report or an install ledger row.
/// </summary>
internal readonly struct LoadedPluginIdentity
{
    private readonly string? _guid;
    private readonly string? _displayName;
    private readonly string? _version;
    private readonly string? _assemblyFileName;

    internal LoadedPluginIdentity(string? guid, string? displayName = null, string? version = null, string? assemblyFileName = null)
    {
        _guid = (guid ?? "").Trim();
        _displayName = (displayName ?? "").Trim();
        _version = (version ?? "").Trim();
        _assemblyFileName = (assemblyFileName ?? "").Trim();
    }

    // The default struct never ran the constructor, so every accessor answers for it too: "nothing
    // is known about this plugin", which is exactly what an empty identity means.

    /// <summary>The BepInEx plugin GUID, e.g. <c>acme.PhialStore</c>.</summary>
    internal string Guid => _guid ?? "";

    /// <summary>The name the plugin registered, e.g. <c>BasinHoverTips</c>.</summary>
    internal string DisplayName => _displayName ?? "";

    /// <summary>The plugin version this process observed. Empty when unknown.</summary>
    internal string Version => _version ?? "";

    /// <summary>The DLL this plugin was loaded from, with or without its extension. Empty when unknown.</summary>
    internal string AssemblyFileName => _assemblyFileName ?? "";

    internal bool IsEmpty => Guid.Length == 0 && DisplayName.Length == 0 && AssemblyFileName.Length == 0;
}

/// <summary>One resolution: the listing, and which rule found it.</summary>
internal readonly struct PluginListingMatch
{
    internal PluginListingMatch(CatalogEntry? entry, ListingMatchKind kind)
    {
        Entry = kind == ListingMatchKind.None ? null : entry;
        Kind = entry == null ? ListingMatchKind.None : kind;
    }

    internal static PluginListingMatch None => default;

    internal CatalogEntry? Entry { get; }
    internal ListingMatchKind Kind { get; }

    internal bool Resolved => Entry != null;

    /// <summary>The readback name of the rule that matched.</summary>
    internal string KindName => ListingMatchKinds.NameOf(Kind);

    /// <summary>
    /// True when the only thing linking this plugin to the listing is a name. Show the listing;
    /// never remove files or resolve a dependency from it.
    /// </summary>
    internal bool NameOnly => ListingMatchKinds.IsDisplayOnly(Kind);
}

/// <summary>
/// LINK1: maps a plugin this process has loaded to a listing in the Install index, for the mods
/// another tool installed — the five on the user's client (BasinHoverTips, ClanResourceHUD,
/// GroundPickupGlow, HuntsAndGates, RetainedPassword) whose GUIDs no ledger, identity cache or
/// listing id names, so their pages had only a version and "no description available".
///
/// <para>The rules, in order, and no others:</para>
/// <list type="number">
/// <item>the listing's <c>Manifest.Id</c> or <c>PackageName</c> <em>is</em> the plugin GUID → <see cref="ListingMatchKind.Id"/>;</item>
/// <item>the inspected-identity cache read this GUID out of that package → <see cref="ListingMatchKind.Identity"/>;</item>
/// <item>the plugin's display name spells the package name or the package's full name, letters and
/// digits only and case-insensitively (<c>BasinHoverTips</c> ↔ <c>ExampleTeam-BasinHoverTips</c>)
/// → <see cref="ListingMatchKind.Name"/>;</item>
/// <item>the assembly file stem spells it the same way → <see cref="ListingMatchKind.Name"/>.</item>
/// </list>
///
/// <para>Two owners publishing the same package name is ambiguous: the one whose version equals the
/// loaded version wins, and if that does not single one out there is no match. Nothing here guesses,
/// and nothing here reads a file, a process or the network.</para>
/// </summary>
internal static class PluginListingMatcher
{
    /// <summary>
    /// Letters and digits only, lower-cased: what <c>ExampleTeam-BasinHoverTips</c> and
    /// <c>BasinHoverTips</c> have to share for the name rule to fire. Anything else — a dot, a dash,
    /// an underscore, a space — is punctuation an author moved around between the two spellings.
    /// </summary>
    internal static string Normalize(string? value)
    {
        string v = value ?? "";
        var sb = new StringBuilder(v.Length);
        for (int i = 0; i < v.Length; i++)
        {
            char c = v[i];
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>The listing's <c>{owner}-{name}</c>, from whichever field carries it.</summary>
    internal static string FullNameOf(CatalogEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Manifest.Package)) return entry.Manifest.Package!.Trim();
        if (!string.IsNullOrWhiteSpace(entry.Owner) && !string.IsNullOrWhiteSpace(entry.PackageName))
            return entry.Owner!.Trim() + "-" + entry.PackageName!.Trim();
        return "";
    }

    /// <summary>
    /// Resolve one loaded plugin against the index.
    /// <paramref name="identityGuid"/> answers "which plugin GUID did an inspection read out of this
    /// package" (the <c>progenitor-cache/identity</c> cache); null skips that rule rather than
    /// inventing one.
    /// </summary>
    internal static PluginListingMatch Match(
        LoadedPluginIdentity plugin,
        IReadOnlyList<CatalogEntry>? entries,
        Func<CatalogEntry, string?>? identityGuid = null)
    {
        if (entries == null || entries.Count == 0 || plugin.IsEmpty) return PluginListingMatch.None;

        if (plugin.Guid.Length > 0)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry == null) continue;
                if (Equals(entry.Manifest.Id, plugin.Guid) || Equals(entry.PackageName, plugin.Guid))
                    return new PluginListingMatch(entry, ListingMatchKind.Id);
            }

            if (identityGuid != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry == null) continue;
                    string? guid;
                    try { guid = identityGuid(entry); }
                    catch { continue; }
                    if (Equals(guid, plugin.Guid)) return new PluginListingMatch(entry, ListingMatchKind.Identity);
                }
            }
        }

        // The two spelling rules. A display name that is only the plugin GUID says nothing new, so
        // it is not tried as a name: the GUID rules above already had it.
        var byName = MatchBySpelling(plugin.DisplayName, plugin.Version, entries);
        if (byName != null) return new PluginListingMatch(byName, ListingMatchKind.Name);

        var byFile = MatchBySpelling(Stem(plugin.AssemblyFileName), plugin.Version, entries);
        if (byFile != null) return new PluginListingMatch(byFile, ListingMatchKind.Name);

        return PluginListingMatch.None;
    }

    /// <summary>
    /// The one entry whose package name or full name is spelled <paramref name="spelling"/>, or null.
    /// Several owners publishing the name is resolved by the loaded version and otherwise refused.
    /// </summary>
    private static CatalogEntry? MatchBySpelling(
        string? spelling, string loadedVersion, IReadOnlyList<CatalogEntry> entries)
    {
        string wanted = Normalize(spelling);
        if (wanted.Length == 0) return null;

        List<CatalogEntry>? hits = null;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry == null) continue;
            if (!string.Equals(Normalize(entry.PackageName), wanted, StringComparison.Ordinal)
                && !string.Equals(Normalize(FullNameOf(entry)), wanted, StringComparison.Ordinal))
                continue;
            hits ??= new List<CatalogEntry>(2);
            bool already = false;
            for (int j = 0; j < hits.Count; j++)
                if (string.Equals(hits[j].Key, entry.Key, StringComparison.OrdinalIgnoreCase)) already = true;
            if (!already) hits.Add(entry);
        }

        if (hits == null) return null;
        if (hits.Count == 1) return hits[0];

        // Ambiguous: two owners publish this name. Only the loaded version can separate them, and
        // only when it separates them completely.
        if (loadedVersion.Length == 0) return null;
        CatalogEntry? onVersion = null;
        for (int i = 0; i < hits.Count; i++)
        {
            if (!string.Equals((hits[i].Manifest.Version ?? "").Trim(), loadedVersion, StringComparison.OrdinalIgnoreCase))
                continue;
            if (onVersion != null) return null;
            onVersion = hits[i];
        }

        return onVersion;
    }

    /// <summary>A file name without its extension; anything else is returned as given.</summary>
    internal static string Stem(string? fileName)
    {
        string f = (fileName ?? "").Trim();
        if (f.Length == 0) return "";
        int dot = f.LastIndexOf('.');
        return dot > 0 ? f.Substring(0, dot) : f;
    }

    private static bool Equals(string? left, string? right) =>
        !string.IsNullOrEmpty(left)
        && !string.IsNullOrEmpty(right)
        && string.Equals(left!.Trim(), right!.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One remembered resolution of a loaded plugin, so the sweep is not repeated on every page open.
/// The plugin version is part of the record: a mod that updated under the same GUID is a different
/// package version, so its listing is resolved again rather than inherited.
/// </summary>
internal sealed class LoadedListingRecord
{
    internal LoadedListingRecord(
        string guid, string pluginVersion, string key, ListingMatchKind kind, string? matchedUtc = null)
    {
        Guid = (guid ?? "").Trim();
        PluginVersion = (pluginVersion ?? "").Trim();
        Key = (key ?? "").Trim();
        Kind = kind;
        MatchedUtc = (matchedUtc ?? "").Trim();
    }

    internal string Guid { get; }
    internal string PluginVersion { get; }

    /// <summary>The listing key this plugin resolved to, or empty for a remembered "nothing names it".</summary>
    internal string Key { get; }

    internal ListingMatchKind Kind { get; }
    internal string MatchedUtc { get; }

    /// <summary>Is this record still about the copy that is loaded now?</summary>
    internal bool IsFor(string? pluginVersion) =>
        string.Equals(PluginVersion, (pluginVersion ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// LINK1: <c>BepInEx/progenitor-cache/identity/loaded/&lt;guid&gt;.json</c> — the listing one loaded
/// plugin resolved to, and the confidence it was resolved with. A null or empty root keeps the rows
/// in memory for this process, which is what the tests use; an unreadable or unwritable folder is
/// "nothing remembered", never an error.
/// </summary>
internal sealed class LoadedListingIdentityCache
{
    internal const string LoadedFolderName = "loaded";

    private readonly object _gate = new();
    private readonly Dictionary<string, LoadedListingRecord> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly string? _root;

    internal LoadedListingIdentityCache(string? bepInExRoot)
    {
        _root = string.IsNullOrWhiteSpace(bepInExRoot)
            ? null
            : Path.Combine(
                Path.GetFullPath(bepInExRoot),
                OrdinaryIdentityCache.CacheFolderName,
                OrdinaryIdentityCache.IdentityFolderName,
                LoadedFolderName);
    }

    /// <summary>The folder the rows live in, or null when this instance is memory-only.</summary>
    internal string? Root => _root;

    internal string? PathFor(string? guid)
    {
        string id = (guid ?? "").Trim();
        if (_root == null || id.Length == 0) return null;
        return Path.Combine(_root, OrdinaryIdentityCache.Sanitize(id) + ".json");
    }

    internal LoadedListingRecord? TryRead(string? guid)
    {
        string id = (guid ?? "").Trim();
        if (id.Length == 0) return null;
        if (_root == null)
        {
            lock (_gate) return _memory.TryGetValue(id, out var row) ? row : null;
        }

        try
        {
            string? path = PathFor(id);
            if (path == null || !File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            return new LoadedListingRecord(
                Read(root, "guid"),
                Read(root, "pluginVersion"),
                Read(root, "key"),
                ListingMatchKinds.Parse(Read(root, "confidence")),
                Read(root, "matchedUtc"));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Remember one resolution. Best-effort: a failed write only costs the next sweep.</summary>
    internal bool Write(LoadedListingRecord? record)
    {
        if (record == null || record.Guid.Length == 0) return false;
        string stamp = record.MatchedUtc.Length > 0
            ? record.MatchedUtc
            : DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var stored = new LoadedListingRecord(record.Guid, record.PluginVersion, record.Key, record.Kind, stamp);
        if (_root == null)
        {
            lock (_gate) _memory[stored.Guid] = stored;
            return true;
        }

        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteNumber("schema", 1);
                w.WriteString("guid", stored.Guid);
                w.WriteString("pluginVersion", stored.PluginVersion);
                w.WriteString("key", stored.Key);
                w.WriteString("confidence", ListingMatchKinds.NameOf(stored.Kind));
                w.WriteString("matchedUtc", stored.MatchedUtc);
                w.WriteEndObject();
            }

            string? path = PathFor(stored.Guid);
            if (path == null) return false;
            InstallStaging.AtomicWrite(path, ms.ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Forget everything. Used when the index the rows were resolved against is replaced.</summary>
    internal void ClearMemory()
    {
        lock (_gate) _memory.Clear();
    }

    private static string Read(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
