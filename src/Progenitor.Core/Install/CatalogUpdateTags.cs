using System;
using System.Collections.Generic;
using Progenitor.Config;

namespace Progenitor.Install;

/// <summary>
/// The resolved current game update: the tag (<c>&lt;name&gt; Update</c>) or null when unknown,
/// and where it came from (<see cref="CatalogUpdateTags.SourceConfig"/> /
/// <see cref="CatalogUpdateTags.SourceAdapter"/> / <see cref="CatalogUpdateTags.SourceUnknown"/>).
/// </summary>
internal readonly struct CurrentUpdateResolution
{
    public CurrentUpdateResolution(string? tag, string source)
    {
        Tag = string.IsNullOrWhiteSpace(tag) ? null : tag!.Trim();
        Source = Tag == null ? CatalogUpdateTags.SourceUnknown : source;
    }

    public string? Tag { get; }
    public string Source { get; }
    public bool IsKnown => Tag != null;

    public static CurrentUpdateResolution Unknown => new(null, CatalogUpdateTags.SourceUnknown);
}

/// <summary>
/// Game-update tags on a listing index. Thunderstore tags a package with the update it
/// was published for (<c>&lt;name&gt; Update</c>). The current update is never inferred
/// from the index: it is the explicit <c>[Install] CurrentUpdateTag</c> when set, else the
/// game adapter's mapping for the detected game build, else <b>unknown</b>. Unknown hides
/// nothing and warns about nothing. Tags are author declarations, so a package outside the
/// current update warns and is never blocked (job TAG1; the earlier latest-<c>date_updated</c>
/// heuristic let an edit to an old-tagged package flip the current update and hide the
/// current listings).
/// </summary>
internal static class CatalogUpdateTags
{
    public const string Suffix = " Update";

    public const string SourceConfig = "config";
    public const string SourceAdapter = "adapter";
    public const string SourceUnknown = "unknown";

    /// <summary>Config value that forces the unknown state (case-insensitive).</summary>
    public const string UnknownConfigValue = "unknown";

    /// <summary>
    /// Process wiring for <see cref="ResolveCurrent"/>. Defaults: the bound config key, the
    /// last Resilience report's build key, and the V Rising table. Tests replace them and
    /// call <see cref="ResetProvidersForTests"/>.
    /// </summary>
    public static Func<string?> ConfiguredTagProvider { get; set; } = DefaultConfiguredTag;
    public static Func<string?> BuildProvider { get; set; } = DefaultBuild;
    public static Func<string?, string?> Adapter { get; set; } = VRising.VRisingUpdateTagMap.TagForBuild;

    internal static void ResetProvidersForTests()
    {
        ConfiguredTagProvider = DefaultConfiguredTag;
        BuildProvider = DefaultBuild;
        Adapter = VRising.VRisingUpdateTagMap.TagForBuild;
    }

    /// <summary>Resolve with the process wiring. Never throws: a provider that throws counts as unknown.</summary>
    public static CurrentUpdateResolution ResolveCurrent()
    {
        string? configured = null, build = null;
        Func<string?, string?>? adapter = Adapter;
        try { configured = ConfiguredTagProvider?.Invoke(); } catch { }
        try { build = BuildProvider?.Invoke(); } catch { }
        try { return Resolve(configured, build, adapter); } catch { return CurrentUpdateResolution.Unknown; }
    }

    /// <summary>
    /// Resolution order: a non-empty <paramref name="configuredTag"/> (source <c>config</c>;
    /// the literal <see cref="UnknownConfigValue"/> yields unknown) → the adapter's tag for
    /// <paramref name="gameBuild"/> (source <c>adapter</c>) → unknown.
    /// </summary>
    public static CurrentUpdateResolution Resolve(string? configuredTag, string? gameBuild, Func<string?, string?>? adapter)
    {
        if (!string.IsNullOrWhiteSpace(configuredTag))
        {
            string c = configuredTag!.Trim();
            if (string.Equals(c, UnknownConfigValue, StringComparison.OrdinalIgnoreCase))
                return CurrentUpdateResolution.Unknown;
            return new CurrentUpdateResolution(c, SourceConfig);
        }

        if (adapter != null && !string.IsNullOrWhiteSpace(gameBuild))
        {
            string? mapped = adapter(gameBuild);
            if (!string.IsNullOrWhiteSpace(mapped)) return new CurrentUpdateResolution(mapped, SourceAdapter);
        }

        return CurrentUpdateResolution.Unknown;
    }

    private static string? DefaultConfiguredTag()
    {
        string? value = ConfigHelper.CurrentUpdateTagValue;
        if (value != null) return value;
#if !PROGENITOR_TESTS
        var file = TrySharedConfigFile();
        if (file == null) return null;
        try { return ConfigHelper.BindCurrentUpdateTag(file).Value; }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Install: binding [" + ConfigHelper.InstallSection + "] "
                                      + ConfigHelper.CurrentUpdateTagKey + " threw (" + e.Message + "); resolving from the adapter.");
            return null;
        }
#else
        return null;
#endif
    }

#if !PROGENITOR_TESTS
    private static BepInEx.Configuration.ConfigFile? TrySharedConfigFile() =>
        Progenitor.Config.SharedConfig.TryGetOrLog("[" + ConfigHelper.InstallSection + "] " + ConfigHelper.CurrentUpdateTagKey);
#endif

    /// <summary>The Resilience pass's build key (<c>client-247473480-…</c>), null until the pass has run.</summary>
    private static string? DefaultBuild()
    {
        var last = Resilience.Resilience.Last;
        var current = last?.Current;
        if (current == null || string.IsNullOrEmpty(current.Build)) return null;
        return current.Key;
    }

    /// <summary>True for a category of the form <c>&lt;name&gt; Update</c>.</summary>
    public static bool IsUpdateTag(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return false;
        string c = category.Trim();
        return c.Length > Suffix.Length && c.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every update tag on the entry, trimmed, in listing order.</summary>
    public static List<string> UpdateTagsOf(CatalogEntry entry)
    {
        var list = new List<string>();
        if (entry == null) return list;
        for (int i = 0; i < entry.Categories.Count; i++)
        {
            string? c = entry.Categories[i];
            if (IsUpdateTag(c)) list.Add(c!.Trim());
        }

        return list;
    }

    public static bool HasAnyUpdateTag(CatalogEntry entry) => UpdateTagsOf(entry).Count > 0;

    /// <summary>True when any of the entry's update tags equals <paramref name="updateTag"/>; a multi-tagged package counts.</summary>
    public static bool IsTagged(CatalogEntry entry, string? updateTag)
    {
        if (entry == null || string.IsNullOrEmpty(updateTag)) return false;
        // INDEX1 (live 2026-10-20: a merged Index row never showed): update tags are Thunderstore categories, which an Index
        // row has none of. Its compatibility is its own progenitor range, so the "Current update only" filters and the stale
        // warning never judge it by Thunderstore's tags.
        if (entry.FromIndex) return true;
        var tags = UpdateTagsOf(entry);
        for (int i = 0; i < tags.Count; i++)
        {
            if (string.Equals(tags[i], updateTag, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// Player-facing warning for a package outside the current update, or null when the
    /// package is tagged for it or the current update is unknown (nothing to judge by).
    /// </summary>
    public static string? StaleWarning(CatalogEntry entry, string? currentTag)
    {
        if (entry == null || string.IsNullOrEmpty(currentTag)) return null;
        if (IsTagged(entry, currentTag)) return null;
        return NotCurrentUpdate(currentTag);
    }

    public static string NotCurrentUpdate(string currentTag) =>
        "Not marked for the current game update (" + (currentTag ?? "").Trim() + ") — may be incompatible";
}
