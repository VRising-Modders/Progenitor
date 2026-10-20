using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.Install;

/// <summary>
/// Parses the Thunderstore community v1 package JSON into <see cref="CatalogEntry"/> values.
/// A Thunderstore row is always <see cref="CatalogProvenance.AdaptedListing"/> and is not
/// installable: the schema publishes no checksum, identity is <c>{owner}-{name}</c> not a
/// plugin GUID, and dependencies are <c>{owner}-{name}-{version}</c> strings. This adapter
/// never invents a plugin GUID from those strings.
/// </summary>
internal static class ThunderstoreAdapter
{
    public const int DefaultMaxEntries = 2048;
    public const int DefaultMaxDescriptionChars = 2048;
    public const int DefaultMaxVersionsPerPackage = 32;
    public const int DefaultMaxJsonBytes = 8 * 1024 * 1024;
    public const string ExperimentalPackageRoot = "https://thunderstore.io/api/experimental/package/";
    public const string ReadmeDocument = "readme";
    public const int DefaultMaxDocumentBytes = ThunderstoreListingDocuments.MaxBodyBytes;

    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 16,
    };

    /// <summary>
    /// Parse a v1 package-index JSON array. Returns false with a reason on malformed input
    /// rather than throwing into the caller. Unknown keys are ignored. <paramref name="indexStamp"/>
    /// is the fetch time recorded on every produced entry.
    /// </summary>
    public static bool TryLoadIndex(
        string? json,
        string? indexStamp,
        out IReadOnlyList<CatalogEntry> entries,
        out string error,
        int maxEntries = DefaultMaxEntries,
        int maxDescriptionChars = DefaultMaxDescriptionChars)
    {
        entries = Array.Empty<CatalogEntry>();
        error = "";

        if (string.IsNullOrWhiteSpace(json))
        {
            error = InstallReasons.IndexEmpty;
            return false;
        }

        if (json.Length > DefaultMaxJsonBytes)
        {
            error = InstallReasons.IndexTooLarge;
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, ParseOptions);
        }
        catch (JsonException e)
        {
            error = IsTruncated(e) ? InstallReasons.IndexTruncated : InstallReasons.MalformedJson + ": " + e.Message;
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                error = InstallReasons.IndexNotArray;
                return false;
            }

            var list = new List<CatalogEntry>();
            foreach (var package in doc.RootElement.EnumerateArray())
            {
                if (list.Count >= maxEntries)
                {
                    error = InstallReasons.IndexTooLarge;
                    entries = Array.Empty<CatalogEntry>();
                    return false;
                }

                if (package.ValueKind != JsonValueKind.Object) continue;
                if (!TryAdaptOne(package, indexStamp, maxDescriptionChars, out var entry, out _))
                    continue;
                list.Add(entry);
            }

            entries = list;
            return true;
        }
    }

    /// <summary>Adapt one raw package object. False when it has no stable listing identity and version.</summary>
    public static bool TryAdaptOne(
        JsonElement raw,
        string? indexStamp,
        int maxDescriptionChars,
        out CatalogEntry entry,
        out string error) =>
        TryAdaptOne(raw, indexStamp, maxDescriptionChars, null, out entry, out error);

    /// <summary>
    /// JOINFLOW2: the same, describing the version whose <c>version_number</c> is exactly
    /// <paramref name="pinnedVersion"/> instead of the newest active one (Thunderstore keeps old versions
    /// downloadable). False when the package has no such version. A null pin is the newest active version.
    /// </summary>
    internal static bool TryAdaptOne(
        JsonElement raw,
        string? indexStamp,
        int maxDescriptionChars,
        string? pinnedVersion,
        out CatalogEntry entry,
        out string error)
    {
        entry = null!;
        error = "";
        if (raw.ValueKind != JsonValueKind.Object)
        {
            error = InstallReasons.MissingListingIdentity;
            return false;
        }

        string name = ReadString(raw, "name") ?? "";
        string fullName = ReadString(raw, "full_name") ?? "";
        string owner = ReadString(raw, "owner") ?? "";
        string packageUrl = ReadString(raw, "package_url") ?? "";
        bool deprecated = ReadBool(raw, "is_deprecated");
        string[] categories = ReadStringArray(raw, "categories");
        string? dateUpdated = ReadString(raw, "date_updated");
        // LINK1: `rating_score` is published on the package, not on a version — the number
        // Thunderstore's own listing shows as its like count. A missing or negative value is
        // "the provider published none", never zero likes.
        int? ratingScore = ReadInt32(raw, "rating_score");
        // INSTALLSIDES1: the author's own NSFW flag, package-level in the v1 list. Absent reads false.
        bool nsfw = ReadBool(raw, "has_nsfw_content");

        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(fullName))
        {
            error = InstallReasons.MissingListingIdentity;
            return false;
        }

        if (!TryReadLatestVersion(raw, maxDescriptionChars, pinnedVersion, out var version, out error))
            return false;

        if (string.IsNullOrWhiteSpace(fullName) && !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(name))
            fullName = owner + "-" + name;

        // Never invent a plugin GUID from {owner}-{name}. Id stays empty; the foreign id lives on Package.
        var manifest = new ManifestMod(
            id: "",
            version: version.VersionNumber,
            level: NetworkCompatibilityLevel.NoNeedForSync,
            versionRule: NetworkVersionRule.Exact,
            hotLoadable: false,
            ns: EmptyToNull(owner),
            package: EmptyToNull(fullName),
            sha256: null,
            size: version.FileSize,
            url: EmptyToNull(version.DownloadUrl));

        entry = new CatalogEntry(
            manifest,
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            indexStamp,
            displayName: EmptyToNull(name),
            author: EmptyToNull(owner),
            description: version.Description,
            listingUrl: EmptyToNull(packageUrl),
            side: PackageSide.Unknown,
            isDeprecated: deprecated,
            foreignDependencies: version.Dependencies,
            pluginDependencies: Array.Empty<PluginDependency>(),
            selectedVersionFullName: EmptyToNull(version.FullName),
            downloadUrl: EmptyToNull(version.DownloadUrl),
            listingFileSize: version.FileSize,
            owner: EmptyToNull(owner),
            packageName: EmptyToNull(name),
            categories: categories,
            dateUpdated: EmptyToNull(dateUpdated),
            releasedUtc: version.DateCreated,
            downloads: version.Downloads,
            ratingScore: ratingScore,
            hasNsfwContent: nsfw);
        return true;
    }

    public static bool TryAdaptOne(
        string? jsonObject,
        string? indexStamp,
        out CatalogEntry entry,
        out string error,
        int maxDescriptionChars = DefaultMaxDescriptionChars)
    {
        entry = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(jsonObject))
        {
            error = InstallReasons.MalformedJson;
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonObject, ParseOptions);
        }
        catch (JsonException e)
        {
            error = IsTruncated(e) ? InstallReasons.IndexTruncated : InstallReasons.MalformedJson + ": " + e.Message;
            return false;
        }

        using (doc)
            return TryAdaptOne(doc.RootElement, indexStamp, maxDescriptionChars, out entry, out error);
    }

    /// <summary>
    /// Versioned experimental README URL. <paramref name="ns"/> is the v1 <c>owner</c>.
    /// Unversioned forms return 400 from Thunderstore and are not built.
    /// </summary>
    public static bool TryBuildReadmeUrl(string? ns, string? name, string? version, out string url, out string error) =>
        TryBuildDocumentUrl(ns, name, version, ReadmeDocument, out url, out error);

    /// <summary>Thunderstore's icon CDN: one 256×256 PNG per version, named by the version's <c>full_name</c>.</summary>
    public const string IconCdnRoot = "https://gcdn.thunderstore.io/live/repository/icons/";

    /// <summary>
    /// The listing icon URL for the LIST2 image cell: <see cref="IconCdnRoot"/> +
    /// <c>{owner}-{name}-{version}.png</c> (the v1 <c>versions[].icon</c> value, rebuilt from the
    /// identity the index already carries). False without a version full name.
    /// </summary>
    public static bool TryBuildIconUrl(CatalogEntry? entry, out string url)
    {
        url = "";
        if (entry == null) return false;
        // INDEX1: an Index listing's icon is the one the Index publishes beside its list.
        if (entry.FromIndex)
        {
            url = entry.Pin!.IconUrl;
            return url.Length > 0;
        }

        string? fullName = entry.SelectedVersionFullName;
        if (string.IsNullOrWhiteSpace(fullName))
        {
            if (string.IsNullOrWhiteSpace(entry.Owner) || string.IsNullOrWhiteSpace(entry.PackageName)
                || string.IsNullOrWhiteSpace(entry.Manifest.Version))
                return false;
            fullName = entry.Owner + "-" + entry.PackageName + "-" + entry.Manifest.Version;
        }
        url = ThunderstoreMirror.IconRoot + Uri.EscapeDataString(fullName.Trim()) + ".png";
        return true;
    }

    public static bool TryBuildDocumentUrl(
        string? ns,
        string? name,
        string? version,
        string document,
        out string url,
        out string error)
    {
        url = "";
        error = "";
        if (string.IsNullOrWhiteSpace(ns)
            || string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(version)
            || string.IsNullOrWhiteSpace(document))
        {
            error = InstallReasons.DocumentIdentityMissing;
            return false;
        }

        url = ThunderstoreMirror.ExperimentalPackageRoot
            + Uri.EscapeDataString(ns.Trim()) + "/"
            + Uri.EscapeDataString(name.Trim()) + "/"
            + Uri.EscapeDataString(version.Trim()) + "/"
            + document.Trim() + "/";
        return true;
    }

    /// <summary>
    /// Read <c>{"markdown":"..."}</c>. An empty string or JSON null is success with empty
    /// markdown. A missing <c>markdown</c> key is unavailable.
    /// </summary>
    public static bool TryReadMarkdown(string? json, out string markdown, out string error)
    {
        markdown = "";
        error = "";
        if (string.IsNullOrWhiteSpace(json))
        {
            error = InstallReasons.DocumentMalformedJson;
            return false;
        }

        if (json.Length > DefaultMaxDocumentBytes)
        {
            error = InstallReasons.FetchSizeCeiling;
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, ParseOptions);
        }
        catch (JsonException e)
        {
            error = IsTruncated(e) ? InstallReasons.IndexTruncated : InstallReasons.DocumentMalformedJson + ": " + e.Message;
            return false;
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = InstallReasons.DocumentNotObject;
                return false;
            }

            if (!doc.RootElement.TryGetProperty("markdown", out var prop))
            {
                error = InstallReasons.DocumentMissingMarkdown;
                return false;
            }

            if (prop.ValueKind == JsonValueKind.Null || prop.ValueKind == JsonValueKind.Undefined)
                return true;

            if (prop.ValueKind != JsonValueKind.String)
            {
                error = InstallReasons.DocumentMissingMarkdown;
                return false;
            }

            markdown = prop.GetString() ?? "";
            return true;
        }
    }

    /// <summary>JOINFLOW2: how many versions of one package a pinned lookup walks (old versions stay listed).</summary>
    public const int MaxPinnedVersionsPerPackage = 1024;

    private static bool TryReadLatestVersion(
        JsonElement package,
        int maxDescriptionChars,
        string? pinnedVersion,
        out VersionRow version,
        out string error)
    {
        version = default;
        error = "";
        if (!package.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Array)
        {
            error = InstallReasons.MissingListingIdentity;
            return false;
        }

        JsonElement? chosen = null;
        JsonElement? first = null;
        int seen = 0;
        long? downloads = null;
        bool pinned = !string.IsNullOrWhiteSpace(pinnedVersion);
        int cap = pinned ? MaxPinnedVersionsPerPackage : DefaultMaxVersionsPerPackage;
        foreach (var v in versions.EnumerateArray())
        {
            if (v.ValueKind != JsonValueKind.Object) continue;
            seen++;
            if (seen > cap) break;
            // LIST2: the package's download count is the sum over its versions (the figure
            // Thunderstore's own listing shows); a version without one adds nothing.
            long? count = ReadInt64(v, "downloads");
            if (count != null) downloads = (downloads ?? 0) + count.Value;
            if (pinned)
            {
                // JOINFLOW2: exactly the pinned version, and only while Thunderstore still serves it.
                if (chosen == null && IsActive(v)
                    && string.Equals((ReadString(v, "version_number") ?? "").Trim(), pinnedVersion!.Trim(), StringComparison.Ordinal))
                    chosen = v;
                continue;
            }

            // The v1 payload lists newest first. Prefer the first active row, else the first row.
            if (first == null) first = v;
            if (chosen == null && IsActive(v))
                chosen = v;
        }
        if (!pinned) chosen ??= first;

        if (chosen == null)
        {
            error = InstallReasons.MissingListingIdentity;
            return false;
        }

        var row = chosen.Value;
        string versionNumber = ReadString(row, "version_number") ?? "";
        if (string.IsNullOrWhiteSpace(versionNumber))
        {
            error = InstallReasons.MissingListingIdentity;
            return false;
        }

        string? description = ReadString(row, "description");
        if (description != null && description.Length > maxDescriptionChars)
            description = description.Substring(0, maxDescriptionChars);

        version = new VersionRow(
            versionNumber,
            ReadString(row, "full_name"),
            description,
            ReadString(row, "download_url"),
            ReadInt64(row, "file_size"),
            ReadStringArray(row, "dependencies"),
            EmptyToNull(ReadString(row, "date_created")),
            downloads);
        return true;
    }

    /// <summary>A version row Thunderstore still serves: <c>is_active</c> true, or absent (older exports).</summary>
    internal static bool IsActive(JsonElement version) =>
        ReadBool(version, "is_active") || !version.TryGetProperty("is_active", out _);

    /// <summary>JOINFLOW2: the raw <c>string</c> property <paramref name="name"/>, or null.</summary>
    internal static string? ReadRawString(JsonElement el, string name) => ReadString(el, name);

    private static string[] ReadStringArray(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        var list = new List<string>();
        foreach (var item in p.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            string? s = item.GetString();
            if (string.IsNullOrWhiteSpace(s)) continue;
            list.Add(s.Trim());
        }
        return list.ToArray();
    }

    private static string? ReadString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return null;
        if (p.ValueKind == JsonValueKind.Null || p.ValueKind == JsonValueKind.Undefined) return null;
        if (p.ValueKind == JsonValueKind.String) return p.GetString();
        return null;
    }

    private static bool ReadBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Read a size as <see cref="long"/>. The community index has published sizes above
    /// <see cref="int.MaxValue"/>; using <c>GetInt32</c> would throw or truncate.
    /// </summary>
    private static long? ReadInt64(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number) return null;
        if (p.TryGetInt64(out long n)) return n < 0 ? null : n;
        return null;
    }

    /// <summary>
    /// LINK1: read a count that the UI shows as an <see cref="int"/>. A value outside
    /// <see cref="int"/> range, or a negative one, is "not published" rather than a clamped
    /// number the page would present as the store's own figure.
    /// </summary>
    private static int? ReadInt32(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Number) return null;
        if (p.TryGetInt32(out int n)) return n < 0 ? null : n;
        return null;
    }

    private static bool IsTruncated(JsonException e)
    {
        string m = e.Message ?? "";
        return m.IndexOf("end of data", StringComparison.OrdinalIgnoreCase) >= 0
            || m.IndexOf("Expected end of", StringComparison.OrdinalIgnoreCase) >= 0
            || m.IndexOf("incomplete", StringComparison.OrdinalIgnoreCase) >= 0
            || m.IndexOf("unterminated", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private readonly struct VersionRow
    {
        public VersionRow(
            string versionNumber,
            string? fullName,
            string? description,
            string? downloadUrl,
            long? fileSize,
            IReadOnlyList<string> dependencies,
            string? dateCreated = null,
            long? downloads = null)
        {
            DateCreated = dateCreated;
            Downloads = downloads;
            VersionNumber = versionNumber;
            FullName = fullName;
            Description = description;
            DownloadUrl = downloadUrl;
            FileSize = fileSize;
            Dependencies = dependencies;
        }

        public string VersionNumber { get; }
        public string? FullName { get; }
        public string? Description { get; }
        public string? DownloadUrl { get; }
        public long? FileSize { get; }
        public IReadOnlyList<string> Dependencies { get; }

        /// <summary>Version <c>date_created</c> (ISO 8601): the listing's release date.</summary>
        public string? DateCreated { get; }

        /// <summary>Download count summed over the package's versions (LIST2), or null when unpublished.</summary>
        public long? Downloads { get; }
    }
}
