using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Progenitor.Install;

/// <summary>
/// On-demand Thunderstore README for one listing version.
/// In-memory cache is per <c>{namespace}/{name}/{version}</c> for the process
/// lifetime of this instance.
///
/// <para>TSTRAFFIC1: the changelog is not fetched: nothing displays it, so the set's changelog half
/// reads unavailable with <see cref="InstallReasons.ChangelogNotFetched"/> (a local listing's changelog
/// still comes from its zip, <see cref="LocalIndexSource"/>). With a <see cref="ListingDiskCache"/> and
/// the listing's release stamp, the README persists under
/// <c>progenitor-cache/readme/&lt;ns&gt;/&lt;name&gt;/&lt;version&gt;.md</c> and a 404 is remembered beside
/// it (<c>&lt;version&gt;.readme.missing</c>) for <see cref="ListingDiskCache.MissingFor"/> or until the
/// release stamp changes, so it is not asked again on the next game start. Requests ask for a gzip
/// body and log one line each.</para>
/// </summary>
internal sealed class ThunderstoreListingDocuments
{
    public const int MaxBodyBytes = 256 * 1024;

    private readonly Dictionary<string, ListingDocumentSet> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly ListingDiskCache? _disk;
    private readonly Func<DateTime> _now;
    private int _requests;

    public ThunderstoreListingDocuments(ListingDiskCache? disk = null, Func<DateTime>? now = null)
    {
        _disk = disk != null && disk.Enabled ? disk : null;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Network requests made (tests).</summary>
    public int Requests { get { lock (_gate) return _requests; } }

    public static string CacheKey(string ns, string name, string version) =>
        (ns ?? "").Trim() + "/" + (name ?? "").Trim() + "/" + (version ?? "").Trim();

    public bool TryGet(string cacheKey, out ListingDocumentSet set)
    {
        lock (_gate) return _cache.TryGetValue(cacheKey ?? "", out set!);
    }

    public int CachedCount
    {
        get { lock (_gate) return _cache.Count; }
    }

    public Task<ListingDocumentSet> LoadAsync(
        string ns,
        string name,
        string version,
        CatalogFetcher fetcher,
        CancellationToken cancellationToken) =>
        LoadAsync(ns, name, version, null, fetcher, cancellationToken);

    /// <summary>
    /// The same; <paramref name="releaseStamp"/> (the listing's release stamp, as the README files use it)
    /// enables the disk copies and the 404 memory. Null: memory only, as before.
    /// </summary>
    public async Task<ListingDocumentSet> LoadAsync(
        string ns,
        string name,
        string version,
        string? releaseStamp,
        CatalogFetcher fetcher,
        CancellationToken cancellationToken)
    {
        if (fetcher == null) throw new ArgumentNullException(nameof(fetcher));
        string key = CacheKey(ns, name, version);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var hit)) return hit;
        }

        var readme = await FetchReadme(
                ns, name, version, releaseStamp, fetcher, cancellationToken)
            .ConfigureAwait(false);
        // TSTRAFFIC1: no changelog request (nothing displays it).
        var set = new ListingDocumentSet(
            key,
            version,
            readme.Ok,
            readme.Error,
            readme.Markdown,
            false,
            InstallReasons.ChangelogNotFetched,
            "");
        lock (_gate) _cache[key] = set;
        return set;
    }

    private async Task<(bool Ok, string? Error, string Markdown)> FetchReadme(
        string ns,
        string name,
        string version,
        string? releaseStamp,
        CatalogFetcher fetcher,
        CancellationToken cancellationToken)
    {
        if (!ThunderstoreAdapter.TryBuildReadmeUrl(ns, name, version, out var url, out var error))
            return (false, error, "");

        const string document = ThunderstoreAdapter.ReadmeDocument;
        var disk = releaseStamp == null ? null : _disk;
        if (disk != null)
        {
            if (disk.TryReadReadmeMarkdown(ns, name, version, releaseStamp, out string cached))
                return (true, null, cached);
            if (disk.IsMarkedMissing(ns, name, version, document, releaseStamp, _now()))
                return (false, InstallReasons.HttpStatus(404), "");
        }

        CatalogTextFetchResult fetched;
        lock (_gate) _requests++;
        try
        {
            fetched = await fetcher.FetchTextAsync(url, MaxBodyBytes, compressed: true, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (false, InstallReasons.FetchCancelled, "");
        }

        string label = ns + "-" + name + " " + version + " " + document;
        bool missing = fetched.StatusCode == 404 || fetched.StatusCode == 410;
        if (!fetched.Ok)
        {
            if (!string.Equals(fetched.Error, InstallReasons.FetchCancelled, StringComparison.Ordinal))
                InstallLog.Info("Thunderstore: " + label + " failed"
                    + (fetched.StatusCode > 0 ? " (HTTP " + fetched.StatusCode.ToString(CultureInfo.InvariantCulture) + ")" : "")
                    + ": " + (fetched.Error ?? InstallReasons.FetchTransport)
                    + (disk != null && missing ? "; remembered for 24 h or until a new release" : "") + ".");
            // TSTRAFFIC1: a README Thunderstore does not have is not asked for again for a day (or until a new release).
            if (disk != null && missing)
                disk.WriteMissing(ns, name, version, document, releaseStamp, _now());
            return (false, fetched.Error ?? InstallReasons.FetchTransport, "");
        }

        InstallLog.Info("Thunderstore: " + label + " HTTP " + fetched.StatusCode.ToString(CultureInfo.InvariantCulture)
            + ", " + fetched.WireBytes.ToString(CultureInfo.InvariantCulture) + " bytes on the wire ("
            + (fetched.ContentEncoding ?? "identity") + "), " + fetched.ByteCount.ToString(CultureInfo.InvariantCulture) + " decoded.");

        if (!ThunderstoreAdapter.TryReadMarkdown(fetched.Text, out var markdown, out var parseError))
            return (false, parseError, "");

        if (disk != null)
        {
            disk.WriteReadmeMarkdown(ns, name, version, releaseStamp, markdown);
            disk.ClearMissing(ns, name, version, document);
        }

        return (true, null, markdown);
    }
}

/// <summary>README and changelog for one listing version. Empty markdown is a successful fetch.</summary>
internal sealed class ListingDocumentSet
{
    public ListingDocumentSet(
        string key,
        string? version,
        bool readmeOk,
        string? readmeError,
        string? readmeMarkdown,
        bool changelogOk,
        string? changelogError,
        string? changelogMarkdown)
    {
        Key = key ?? "";
        Version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();
        ReadmeOk = readmeOk;
        ReadmeError = readmeOk ? null : (readmeError ?? InstallReasons.FetchTransport);
        ReadmeMarkdown = readmeMarkdown ?? "";
        ChangelogOk = changelogOk;
        ChangelogError = changelogOk ? null : (changelogError ?? InstallReasons.FetchTransport);
        ChangelogMarkdown = changelogMarkdown ?? "";
    }

    public string Key { get; }
    public string? Version { get; }
    public bool ReadmeOk { get; }
    public string? ReadmeError { get; }
    public string ReadmeMarkdown { get; }
    public bool ChangelogOk { get; }
    public string? ChangelogError { get; }
    public string ChangelogMarkdown { get; }

    public static ListingDocumentSet Unavailable(string key, string error) =>
        new(key, null, false, error, "", false, error, "");
}

/// <summary><c>mods_ui widget_read</c> listing-page document state.</summary>
internal readonly struct ListingDocReadback
{
    public ListingDocReadback(string state, int chars, string? reason)
    {
        State = state ?? "";
        Chars = chars;
        Reason = reason;
    }

    /// <summary><c>loading</c>, <c>ready</c>, or <c>unavailable</c>.</summary>
    public string State { get; }

    public int Chars { get; }

    public string? Reason { get; }

    public object ToWidget() => new { state = State, chars = Chars, reason = Reason };
}
