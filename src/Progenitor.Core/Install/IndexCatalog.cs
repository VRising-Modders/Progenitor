using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Progenitor.Plugins;

namespace Progenitor.Install;

/// <summary>
/// INDEX1: the exact file a listing installs, from the signed Progenitor Index: an Index entry's own package, or one of its
/// dependencies pinned by sha256. Plain data on <see cref="CatalogEntry.Pin"/>.
/// </summary>
internal sealed class IndexPin
{
    /// <summary><see cref="IndexPackage.SourceGitHubRelease"/> or <see cref="IndexPackage.SourceThunderstore"/>: where the file is downloaded from.</summary>
    public string Source { get; init; } = "";

    /// <summary>The mod's GitHub repository (a release asset must be one of its own).</summary>
    public string Repo { get; init; } = "";

    public string Url { get; init; } = "";
    public string Sha256 { get; init; } = "";

    /// <summary>The signed size in bytes; 0 when the pin names none (a dependency pin).</summary>
    public long Size { get; init; }

    /// <summary>The Index entry this listing is (null for a dependency pin of a Thunderstore package).</summary>
    public IndexPackage? Package { get; init; }

    /// <summary>A takedown covering this version (Warning only: Hidden and Disable are never listed), or null.</summary>
    public IndexRevocation? Revocation { get; init; }

    public IReadOnlyList<IndexDependency> Dependencies => Package?.Dependencies ?? Array.Empty<IndexDependency>();

    /// <summary>The icon's absolute URL, or empty.</summary>
    public string IconUrl { get; init; } = "";
}

/// <summary>
/// INDEX1: the Progenitor Index as catalog rows, game-free. Each listed mod is one row (its newest version that is not
/// taken down), keyed <c>Owner-Name</c> like a Thunderstore listing; an Index row shadows a Thunderstore row of the same key
/// (<see cref="Merge"/>). Hot load is offered only for Index rows (<see cref="MayHotLoad"/>).
/// </summary>
internal static class IndexCatalog
{
    /// <summary>
    /// The rows of a verified Index. <paramref name="allVersions"/>: every listed version (the join screens and dependency
    /// pins choose one); otherwise the newest of each mod. A version taken down as Hidden or Disable is never a row; a
    /// Warning takedown is a row that carries it (<see cref="IndexPin.Revocation"/>).
    /// </summary>
    public static IReadOnlyList<CatalogEntry> ToEntries(IndexDocument? doc, string? stamp, bool allVersions = false, string pagesRoot = ProgenitorIndex.PagesRoot)
    {
        var rows = new List<CatalogEntry>();
        if (doc == null || doc.Packages.Count == 0) return rows;
        var byKey = new Dictionary<string, List<IndexPackage>>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var p in doc.Packages)
        {
            var revocation = doc.RevocationOf(p.Owner, p.Name, p.Version);
            if (revocation is { Mode: IndexRevocationMode.Hidden or IndexRevocationMode.Disable }) continue;
            if (!byKey.TryGetValue(p.Key, out var list))
            {
                byKey[p.Key] = list = new List<IndexPackage>();
                order.Add(p.Key);
            }

            list.Add(p);
        }

        foreach (string key in order)
        {
            var list = byKey[key];
            if (allVersions)
            {
                foreach (var p in list) rows.Add(EntryOf(p, doc, stamp, pagesRoot));
                continue;
            }

            var versions = new List<string>(list.Count);
            foreach (var p in list) versions.Add(p.Version);
            string? newest = ThunderstoreVersions.Highest(versions, _ => true);
            foreach (var p in list)
                if (string.Equals(p.Version, newest, StringComparison.Ordinal))
                {
                    rows.Add(EntryOf(p, doc, stamp, pagesRoot));
                    break;
                }
        }

        return rows;
    }

    /// <summary>One Index entry as a catalog row (not yet inspected: provenance stays <see cref="CatalogProvenance.AdaptedListing"/>).</summary>
    public static CatalogEntry EntryOf(IndexPackage p, IndexDocument? doc, string? stamp, string pagesRoot = ProgenitorIndex.PagesRoot)
    {
        var deps = new List<string>(p.Dependencies.Count);
        foreach (var d in p.Dependencies) if (d.Id.Length > 0) deps.Add(d.Id);
        var manifest = new ManifestMod(
            id: "",
            version: p.Version,
            level: NetworkCompatibilityLevel.NoNeedForSync,
            versionRule: NetworkVersionRule.Exact,
            // The listing's copy of progenitor.json; the install reads the package's own (same bytes: the sha256 is pinned).
            hotLoadable: p.DeclaresHotLoadable,
            ns: p.Owner,
            package: p.Key,
            sha256: null,
            size: p.Size,
            url: p.Package);
        return new CatalogEntry(
            manifest,
            CatalogProvenance.AdaptedListing,
            ProgenitorIndex.SourceId,
            ProgenitorIndex.SourceLabel,
            stamp,
            displayName: p.Name,
            author: p.Owner,
            description: p.Description,
            listingUrl: p.WebsiteUrl.Length > 0 ? p.WebsiteUrl : p.Repo,
            side: p.Side,
            foreignDependencies: deps,
            selectedVersionFullName: p.Id,
            downloadUrl: p.Package,
            listingFileSize: p.Size,
            owner: p.Owner,
            packageName: p.Name,
            releasedUtc: doc?.Generated?.ToString("o", System.Globalization.CultureInfo.InvariantCulture))
        {
            Pin = new IndexPin
            {
                Source = p.Source,
                Repo = p.Repo,
                Url = p.Package,
                Sha256 = p.Sha256,
                Size = p.Size,
                Package = p,
                Revocation = doc?.RevocationOf(p.Owner, p.Name, p.Version),
                IconUrl = ProgenitorIndex.IconUrlOf(p, pagesRoot),
            },
        };
    }

    /// <summary>
    /// Local test listings first, then the Index, then Thunderstore: a row of a key already taken is left out, so a mod in
    /// both the Index and Thunderstore is its Index row. <paramref name="shadowedThunderstore"/>: Thunderstore rows the
    /// Index replaced.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> Merge(
        IReadOnlyList<CatalogEntry>? local,
        IReadOnlyList<CatalogEntry>? index,
        IReadOnlyList<CatalogEntry>? thunderstore,
        out int shadowedLocal,
        out int shadowedThunderstore)
    {
        var fetched = LocalIndexSource.Merge(index, thunderstore, out shadowedThunderstore);
        return LocalIndexSource.Merge(local, fetched, out shadowedLocal);
    }

    /// <summary>
    /// HOT gate (decided 2026-10-20): only an install from the Progenitor Index may hot load (and only when its
    /// <c>progenitor.json</c> declares hotLoadable and the image passes the trusted-hotLoadable checks, decided after this).
    /// A Thunderstore install restarts. <paramref name="localTestListing"/>: the developer's local test source
    /// (<c>[Install] LocalIndexPath</c>) keeps the hot route it is there to test.
    /// </summary>
    public static bool MayHotLoad(CatalogEntry? entry, bool localTestListing) =>
        entry != null && (entry.FromIndex || localTestListing);

    /// <summary>The ledger <c>origin</c> of a file this listing's install writes: <c>index</c>, or empty (Thunderstore, local).</summary>
    public static string LedgerOriginOf(CatalogEntry? entry) => entry != null && entry.FromIndex ? ProgenitorIndex.LedgerOrigin : "";

    /// <summary>
    /// The listing a dependency installs as, resolved from the Index first, then Thunderstore, and pinned by its sha256.
    /// <paramref name="dep"/> is the root's signed dependency row; <paramref name="thunderstoreListing"/> the Thunderstore row of
    /// its key (null when it is not loaded). Null with <paramref name="refusal"/> set when the pin cannot be used.
    /// </summary>
    public static CatalogEntry? PinDependency(
        IndexDependency dep, IndexDocument? doc, CatalogEntry? thunderstoreListing, string? stamp, out string refusal,
        string pagesRoot = ProgenitorIndex.PagesRoot)
    {
        refusal = "";
        if (dep == null || dep.Key.Length == 0 || dep.Version.Length == 0) { refusal = "the dependency names no exact version"; return null; }
        if (!SelfFeed.IsSha256(dep.Sha256)) { refusal = dep.Id + " has no pinned sha256 in the Progenitor Index"; return null; }

        // 1. The Index: the exact listed version, its own entry.
        var listed = doc?.Find(dep.Id);
        if (listed != null || string.Equals(dep.From, IndexDependency.FromIndex, StringComparison.OrdinalIgnoreCase))
        {
            if (listed == null) { refusal = dep.Id + " is not in the Progenitor Index"; return null; }
            var revocation = doc!.RevocationOf(listed.Owner, listed.Name, listed.Version);
            if (revocation is { Mode: IndexRevocationMode.Disable or IndexRevocationMode.Hidden })
            {
                refusal = dep.Id + " was taken down from the Progenitor Index" + (revocation.Reason.Length > 0 ? ": " + revocation.Reason : "");
                return null;
            }

            if (!string.Equals(listed.Sha256, dep.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                refusal = dep.Id + ": the pinned sha256 is not the listed file's";
                return null;
            }

            return EntryOf(listed, doc, stamp, pagesRoot);
        }

        // 2. Thunderstore: that exact version's download link, pinned by the sha256 the Index recorded.
        if (!ProgenitorIndex.IsAllowedPackageUrl(IndexPackage.SourceThunderstore, null, dep.Package))
        {
            refusal = dep.Id + ": the pinned download is not a Thunderstore link";
            return null;
        }

        string owner = "", name = dep.Key;
        int dash = dep.Key.IndexOf('-');
        if (dash > 0) { owner = dep.Key.Substring(0, dash); name = dep.Key.Substring(dash + 1); }
        var t = thunderstoreListing;
        var manifest = new ManifestMod(
            id: t?.Manifest.Id ?? "",
            version: dep.Version,
            level: NetworkCompatibilityLevel.NoNeedForSync,
            versionRule: NetworkVersionRule.Exact,
            hotLoadable: false,
            ns: t?.Owner ?? owner,
            package: dep.Key,
            sha256: null,
            size: null,
            url: dep.Package);
        return new CatalogEntry(
            manifest,
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            t?.IndexStamp ?? stamp,
            displayName: t?.DisplayName ?? name,
            author: t?.Author ?? owner,
            description: t?.Description,
            listingUrl: t?.ListingUrl,
            side: PackageSide.Unknown,
            isDeprecated: t?.IsDeprecated ?? false,
            // A pinned version's own dependencies are the listing's newest version's: Thunderstore's v1 list carries them
            // per version, and the closure reads them for the version it plans.
            foreignDependencies: t != null && string.Equals(t.Manifest.Version, dep.Version, StringComparison.Ordinal) ? t.ForeignDependencies : null,
            selectedVersionFullName: dep.Id,
            downloadUrl: dep.Package,
            listingFileSize: null,
            owner: t?.Owner ?? owner,
            packageName: t?.PackageName ?? name,
            categories: t?.Categories,
            hasNsfwContent: t?.HasNsfwContent ?? false)
        {
            Pin = new IndexPin { Source = IndexPackage.SourceThunderstore, Url = dep.Package, Sha256 = dep.Sha256 },
        };
    }

    /// <summary>The root's signed dependency row for listing <paramref name="key"/>, or null.</summary>
    public static IndexDependency? DependencyFor(CatalogEntry? root, string? key)
    {
        if (root?.Pin == null || string.IsNullOrWhiteSpace(key)) return null;
        foreach (var d in root.Pin.Dependencies)
            if (string.Equals(d.Key, key!.Trim(), StringComparison.OrdinalIgnoreCase)) return d;
        return null;
    }

    /// <summary>
    /// The takedown covering an installed version of listing <paramref name="key"/> (<c>Owner-Name</c>), or null. Matches
    /// the revoked row's owner and name against the key, so a mod no longer listed is still found.
    /// </summary>
    public static IndexRevocation? RevocationForInstalled(IndexDocument? doc, string? key, string? version)
    {
        if (doc == null || string.IsNullOrWhiteSpace(key)) return null;
        IndexRevocation? worst = null;
        foreach (var r in doc.Revoked)
        {
            if (!string.Equals(r.Owner + "-" + r.Name, key!.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (r.Version != "*" && !string.Equals(r.Version, (version ?? "").Trim(), StringComparison.Ordinal)) continue;
            if (worst == null || r.Mode > worst.Mode) worst = r;
        }

        return worst;
    }

    /// <summary>The running Progenitor's version (live: Core's own; tests set it). Null or empty: not known, nothing is refused.</summary>
    internal static Func<string?> RunningProgenitorVersion { get; set; } = DefaultRunningVersion;

    private static string? DefaultRunningVersion()
    {
#if PROGENITOR_TESTS
        return null;
#else
        try { return Plugin.Version; }
        catch { return null; }
#endif
    }

    /// <summary>
    /// The Index entry's <c>progenitor</c> range (<c>&gt;=2026.10.162</c>; from its <c>progenitor.json</c>, else its
    /// Progenitor dependency) excludes the running Progenitor. <paramref name="needs"/>: the words (<c>Progenitor &gt;=X
    /// (this is Y)</c>). A range this build cannot read, <c>*</c>, or an unknown running version is never too old.
    /// </summary>
    public static bool ProgenitorTooOld(CatalogEntry? entry, out string needs)
    {
        needs = "";
        string range = (entry?.Pin?.Package?.Progenitor ?? "").Trim();
        if (range.Length == 0 || range == "*") return false;
        string? running;
        try { running = RunningProgenitorVersion(); }
        catch { running = null; }
        if (string.IsNullOrWhiteSpace(running)) return false;
        if (!Progenitor.Plugins.CompatRange.TryParse(range, out var parsed, out _) || parsed == null) return false;
        string run = running!.Trim();
        int plus = run.IndexOf('+');
        if (plus > 0) run = run.Substring(0, plus);
        if (parsed.Includes(run)) return false;
        needs = "Progenitor " + range + " (this is " + run + ")";
        return true;
    }

    /// <summary>The refusal of an install that needs a newer Progenitor.</summary>
    public static string TooOldRefusal(string needs) => "this mod needs " + needs + "; update Progenitor first";

    /// <summary>The words a takedown shows (<c>Taken down from the Progenitor Index: reason</c>).</summary>
    public static string RevocationText(IndexRevocation r) =>
        (r.Mode == IndexRevocationMode.Warning ? "Warning from the Progenitor Index" : "Taken down from the Progenitor Index")
        + (r.Reason.Length > 0 ? ": " + r.Reason : "");
}
