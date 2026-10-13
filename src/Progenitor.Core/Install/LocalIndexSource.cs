using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Networking;

namespace Progenitor.Install;

/// <summary>
/// HOT1: a local folder that stands in for a package source. <c>[Install] LocalIndexPath</c> names
/// a folder holding an <c>index.json</c> in the same Thunderstore v1 shape the fetched index uses,
/// whose version <c>download_url</c> and <c>icon</c> are <c>file:///…</c> URLs. Its entries are
/// merged in front of the fetched ones, so a dummy listing can exercise the whole
/// download → inspect → admit → plan → install lane with no network at all.
/// </summary>
internal sealed class LocalIndexSource
{
    public const string IndexFileName = "index.json";
    public const string ReadmeFileName = "README.md";
    public const string ChangelogFileName = "CHANGELOG.md";

    /// <summary>Index files larger than this are refused rather than read (same ceiling as the fetched index).</summary>
    public const long MaxIndexBytes = ThunderstoreAdapter.DefaultMaxJsonBytes;

    public LocalIndexSource(string? folder)
    {
        Folder = string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder.Trim());
    }

    /// <summary>The configured folder, or null when the key is empty (the default).</summary>
    public string? Folder { get; }

    public bool Configured => Folder != null;

    // F11: how many listings the last successful Load read; the folder's own README.md stands in for a
    // listing's only when this is 1.
    private volatile int _listingCount;

    public string? IndexPath => Folder == null ? null : Path.Combine(Folder, IndexFileName);

    /// <summary>
    /// Read and parse the local index. A folder that is not configured, has no <c>index.json</c>,
    /// or holds malformed JSON produces a failed load with the reason — never an exception, and
    /// never an empty success that would look like "the source has no packages".
    /// </summary>
    public LocalIndexLoad Load()
    {
        if (Folder == null) return LocalIndexLoad.Failed(InstallReasons.LocalIndexNotConfigured);
        string path = IndexPath!;
        string json;
        try
        {
            if (!File.Exists(path)) return LocalIndexLoad.Failed(InstallReasons.LocalIndexMissing + " (" + path + ")");
            var info = new FileInfo(path);
            if (info.Length > MaxIndexBytes) return LocalIndexLoad.Failed(InstallReasons.IndexTooLarge);
            json = File.ReadAllText(path);
        }
        catch (Exception e)
        {
            return LocalIndexLoad.Failed(InstallReasons.LocalIndexUnreadable + ": " + e.Message);
        }

        string stamp = "local:" + path;
        if (!ThunderstoreAdapter.TryLoadIndex(json, stamp, out var entries, out var error))
            return LocalIndexLoad.Failed(error);

        int listings = 0;
        for (int i = 0; i < entries.Count; i++) if (!string.IsNullOrEmpty(entries[i]?.Key)) listings++;
        _listingCount = listings;
        return new LocalIndexLoad(true, null, WithDeclaredSides(entries, Folder));
    }

    /// <summary>
    /// LOCALSIDE1 (live 2026-10-07: server-only local packages listed both sides and offered "Install on
    /// both" until they were inspected): each listing whose zip is a local file under the folder takes the
    /// side its <c>progenitor.json</c> declares, so the row is right before any inspection. Lowest
    /// precedence: a verified-manifest record or an inspection of the bytes replaces it in
    /// <see cref="InstallCatalogService"/>'s merge. A zip that is missing, too big or declares no side
    /// leaves the listing as the index described it.
    /// </summary>
    private static IReadOnlyList<CatalogEntry> WithDeclaredSides(IReadOnlyList<CatalogEntry> entries, string folder)
    {
        if (entries == null || entries.Count == 0) return entries ?? Array.Empty<CatalogEntry>();
        var result = new List<CatalogEntry>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e != null && e.Side == PackageSide.Unknown)
            {
                string? zip = LocalTestSource.ZipPathOf(e.DownloadUrl, folder);
                PackageSide side = zip == null ? PackageSide.Unknown : DeclaredSideOfZip(zip);
                if (side != PackageSide.Unknown) e = e.WithInspection(e.Provenance, e.Manifest, side);
            }

            result.Add(e!);
        }

        return result;
    }

    /// <summary>Zips bigger than this are not opened for their declared side (the dummy packages are a few KiB).</summary>
    public const long MaxSideZipBytes = LocalTestSource.MaxZipBytes;

    private static readonly object SideGate = new();
    private static readonly Dictionary<string, (long Length, DateTime Write, PackageSide Side)> SideCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// LOCALSIDE1: the side a local package's root <c>progenitor.json</c> declares
    /// (<see cref="PackageInspector.ReadDeclaredSide"/>), cached by path, size and last write time.
    /// Unknown for a missing, oversized or unreadable zip. Never throws.
    /// </summary>
    internal static PackageSide DeclaredSideOfZip(string zip)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(zip);
            if (!info.Exists || info.Length > MaxSideZipBytes) return PackageSide.Unknown;
        }
        catch { return PackageSide.Unknown; }

        lock (SideGate)
        {
            if (SideCache.TryGetValue(info.FullName, out var hit) && hit.Length == info.Length && hit.Write == info.LastWriteTimeUtc)
                return hit.Side;
        }

        PackageSide side = PackageSide.Unknown;
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(info.FullName);
            side = PackageInspector.ReadDeclaredSide(archive);
        }
        catch { /* an unreadable zip declares nothing */ }

        lock (SideGate)
        {
            if (SideCache.Count > 256) SideCache.Clear();
            SideCache[info.FullName] = (info.Length, info.LastWriteTimeUtc, side);
        }

        return side;
    }

    /// <summary>
    /// The README and changelog of one local listing, per package (F11, live 2026-10-07: every listing
    /// of a folder showed the one <c>README.md</c> beside <c>index.json</c>, so ReloadExample's page
    /// showed TimedBuffExample's text). Each document comes from the listing's own zip (its zip-root
    /// <c>README.md</c> / <c>CHANGELOG.md</c>, as a Thunderstore package carries them). The files beside
    /// <c>index.json</c> stand in only when the index lists this one package; otherwise a document
    /// the zip lacks is an unavailable set naming the miss. No fetch.
    /// </summary>
    public ListingDocumentSet Documents(CatalogEntry entry)
    {
        string key = ThunderstoreListingDocuments.CacheKey(
            entry?.Owner ?? "",
            entry?.PackageName ?? "",
            entry?.Manifest.Version ?? "");
        if (Folder == null) return ListingDocumentSet.Unavailable(key, InstallReasons.LocalIndexNotConfigured);

        string? zip = LocalTestSource.ZipPathOf(entry?.DownloadUrl, Folder);
        var inZip = zip == null ? ZipDocuments.None : DocumentsOfZip(zip);
        bool single = _listingCount == 1;
        string? readme = inZip.Readme ?? (single ? TryReadText(Path.Combine(Folder, ReadmeFileName)) : null);
        string? changelog = inZip.Changelog ?? (single ? TryReadText(Path.Combine(Folder, ChangelogFileName)) : null);
        return new ListingDocumentSet(
            key,
            entry?.Manifest.Version,
            readme != null,
            readme != null ? null : InstallReasons.LocalDocumentUnavailable,
            readme ?? "",
            changelog != null,
            changelog != null ? null : InstallReasons.LocalDocumentUnavailable,
            changelog ?? "");
    }

    /// <summary>True when <paramref name="url"/> is a <c>file://</c> URL under the configured folder.</summary>
    public bool Owns(string? url)
    {
        if (Folder == null || string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var uri) || uri == null) return false;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)) return false;
        return ModManifestIds.IsUnder(Folder, Path.GetFullPath(uri.LocalPath));
    }

    /// <summary>
    /// Local entries first, then every fetched entry whose key no local entry already claims. The
    /// key rules are the fetched index's own (<c>{owner}-{name}</c>, case-insensitive), so a local
    /// listing that shadows a real one replaces it rather than appearing twice.
    /// </summary>
    public static IReadOnlyList<CatalogEntry> Merge(
        IReadOnlyList<CatalogEntry>? local,
        IReadOnlyList<CatalogEntry>? fetched,
        out int shadowed)
    {
        shadowed = 0;
        var merged = new List<CatalogEntry>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; local != null && i < local.Count; i++)
        {
            var e = local[i];
            if (string.IsNullOrEmpty(e.Key) || !keys.Add(e.Key)) continue;
            merged.Add(e);
        }

        for (int i = 0; fetched != null && i < fetched.Count; i++)
        {
            var e = fetched[i];
            if (string.IsNullOrEmpty(e.Key)) continue;
            if (!keys.Add(e.Key))
            {
                shadowed++;
                continue;
            }
            merged.Add(e);
        }

        return merged;
    }

    private static string? TryReadText(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > ThunderstoreListingDocuments.MaxBodyBytes) return null;
            return File.ReadAllText(path);
        }
        catch { return null; }
    }

    /// <summary>The zip-root README and changelog of one local package; null where it has none.</summary>
    internal readonly struct ZipDocuments
    {
        public static readonly ZipDocuments None = new(null, null);

        public ZipDocuments(string? readme, string? changelog)
        {
            Readme = readme;
            Changelog = changelog;
        }

        public string? Readme { get; }
        public string? Changelog { get; }
    }

    private static readonly object DocGate = new();
    private static readonly Dictionary<string, (long Length, DateTime Write, ZipDocuments Docs)> DocCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// F11: a local package's own zip-root <c>README.md</c> and <c>CHANGELOG.md</c>, each bounded to
    /// <see cref="ThunderstoreListingDocuments.MaxBodyBytes"/>, cached by path, size and last write time
    /// (asked on page builds). A missing, oversized or unreadable zip has none. Never throws.
    /// </summary>
    internal static ZipDocuments DocumentsOfZip(string zip)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(zip);
            if (!info.Exists || info.Length > MaxSideZipBytes) return ZipDocuments.None;
        }
        catch { return ZipDocuments.None; }

        lock (DocGate)
        {
            if (DocCache.TryGetValue(info.FullName, out var hit) && hit.Length == info.Length && hit.Write == info.LastWriteTimeUtc)
                return hit.Docs;
        }

        string? readme = null, changelog = null;
        try
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(info.FullName);
            int count = 0;
            foreach (var e in archive.Entries)
            {
                if (++count > PackageInspector.DefaultMaxEntries) break;
                if (PackageInspector.IsDirectory(e) || PackageInspector.IsSymlink(e)) continue;
                if (!PackageInspector.TryNormalizeEntryPath(e.FullName ?? "", out var relative, out _)) continue;
                string name = relative.Replace('\\', '/');
                if (name.IndexOf('/') >= 0) continue;
                bool isReadme = string.Equals(name, ReadmeFileName, StringComparison.OrdinalIgnoreCase);
                bool isChangelog = string.Equals(name, ChangelogFileName, StringComparison.OrdinalIgnoreCase);
                if (!isReadme && !isChangelog) continue;
                if (isReadme ? readme != null : changelog != null) continue;
                string? text = ReadEntryText(e);
                if (isReadme) readme = text;
                else changelog = text;
            }
        }
        catch { /* an unreadable zip has no documents */ }

        var docs = new ZipDocuments(readme, changelog);
        lock (DocGate)
        {
            if (DocCache.Count > 256) DocCache.Clear();
            DocCache[info.FullName] = (info.Length, info.LastWriteTimeUtc, docs);
        }

        return docs;
    }

    private static string? ReadEntryText(System.IO.Compression.ZipArchiveEntry entry)
    {
        if (entry.Length > ThunderstoreListingDocuments.MaxBodyBytes) return null;
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[ThunderstoreListingDocuments.MaxBodyBytes + 1];
            int read = 0, n;
            while (read < buffer.Length && (n = reader.Read(buffer, read, buffer.Length - read)) > 0) read += n;
            return read > ThunderstoreListingDocuments.MaxBodyBytes ? null : new string(buffer, 0, read);
        }
        catch { return null; }
    }
}

/// <summary>
/// HOT1: resolves <c>[Install] LocalIndexPath</c> without the menu having to pass it. Bound lazily
/// on Progenitor's own <c>ConfigFile</c> the same way <see cref="CatalogUpdateTags"/> binds
/// <c>CurrentUpdateTag</c>, so a configured folder takes effect the next time the Install tab
/// builds its catalog. Always null in tests, which pass the folder explicitly.
/// </summary>
internal static class LocalIndexConfig
{
    /// <summary>LOCALRELOAD1 tests: the configured value, standing in for <c>[Install] LocalIndexPath</c>. Null: the real key.</summary>
    internal static Func<string?>? TestOverride
    {
        get => TestOverrideLocal.Value;
        set => TestOverrideLocal.Value = value;
    }

    // Per test flow, so a test running beside another never sees its folder.
    private static readonly System.Threading.AsyncLocal<Func<string?>?> TestOverrideLocal = new();

    public static string? Resolve()
    {
        if (TestOverride is { } test) { string? v = test(); return string.IsNullOrWhiteSpace(v) ? null : v; }
        string? value = Config.ConfigHelper.LocalIndexPathValue;
        if (!string.IsNullOrWhiteSpace(value)) return value;
#if !PROGENITOR_TESTS
        if (value != null) return null;
        var file = TrySharedConfigFile();
        if (file == null) return null;
        try { return Config.ConfigHelper.BindLocalIndexPath(file).Value; }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Install: binding [" + Config.ConfigHelper.InstallSection + "] "
                                      + Config.ConfigHelper.LocalIndexPathKey + " threw (" + e.Message + "); no local index.");
            return null;
        }
#else
        return null;
#endif
    }

#if !PROGENITOR_TESTS
    private static BepInEx.Configuration.ConfigFile? TrySharedConfigFile() =>
        Progenitor.Config.SharedConfig.TryGetOrLog("[" + Config.ConfigHelper.InstallSection + "] " + Config.ConfigHelper.LocalIndexPathKey);
#endif
}

/// <summary>
/// HOT1: the most recently built <see cref="InstallCatalogService"/>, held weakly so a retired Core
/// generation's catalog is never kept alive by this static. The bridge op reads the live merge
/// through it; nothing in the install lane depends on it.
/// </summary>
internal static class LocalIndexRegistry
{
    private static readonly object Gate = new();
    private static WeakReference<InstallCatalogService>? _current;

    internal static void Register(InstallCatalogService service)
    {
        lock (Gate) _current = new WeakReference<InstallCatalogService>(service);
    }

    internal static InstallCatalogService? Current
    {
        get
        {
            WeakReference<InstallCatalogService>? weak;
            lock (Gate) weak = _current;
            if (weak != null && weak.TryGetTarget(out var service)) return service;
            return null;
        }
    }
}

/// <summary>
/// HOT1: the <c>install_local</c> bridge op's Core entry point. Core grants Dev no
/// <c>InternalsVisibleTo</c>, so Dev finds this internal type and method by reflection rather than
/// widening Progenitor's public API surface. Returns plain data: no Unity types, no entities, no
/// game contact.
/// </summary>
internal static class InstallLocalIndexOps
{
    /// <summary>
    /// <c>{action: "status"}</c> (default) reports the configured folder, what the local index
    /// contributes and any merge error; <c>{action: "reload"}</c> re-reads the folder and merges it
    /// again first.
    /// </summary>
    internal static System.Collections.Generic.Dictionary<string, object?> Status(bool reload)
    {
        var result = new System.Collections.Generic.Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ok"] = true,
            ["action"] = reload ? "reload" : "status",
        };

        var service = LocalIndexRegistry.Current;
        // LOCALRELOAD1: the reload first, so the path reported is the one the catalog now merges from.
        int? reloaded = reload && service != null ? service.ReloadLocalIndex() : null;
        string? configured = service != null ? service.LocalIndexPath : LocalIndexConfig.Resolve();
        result["localIndexPath"] = configured;
        // LOCALRELOAD1: the key as configured now (a status before a reload can differ from what the catalog merges).
        try { result["configuredNow"] = LocalIndexConfig.Resolve(); } catch { result["configuredNow"] = null; }
        result["configured"] = configured != null;
        result["indexFile"] = configured == null ? null : Path.Combine(configured, LocalIndexSource.IndexFileName);

        if (service == null)
        {
            // No catalog was built in this process yet (the Install tab was never opened). The
            // folder can still be read on its own so the op says what it would contribute.
            var standalone = new LocalIndexSource(configured).Load();
            result["catalog"] = false;
            result["entries"] = KeysOf(standalone.Entries);
            result["error"] = standalone.Error;
            return result;
        }

        int merged = reloaded ?? service.Search(null).Count;
        result["catalog"] = true;
        result["merged"] = merged;
        result["entries"] = service.LocalIndexKeys;
        result["shadowed"] = service.LocalIndexShadowed;
        result["error"] = service.LocalIndexError;
        return result;
    }

    /// <summary>
    /// HOT2 <c>{action: "qualify", zip, target, evidence}</c>: inspect the package with the ordinary
    /// non-executing pipeline and append a complete <see cref="Admission.QualificationRecord"/> to
    /// <c>BepInEx/config/progenitor-qualification.json</c> (schema 1, atomic write).
    ///
    /// <para>A developer/operator tool. The record it writes is a <b>review claim</b> — "a person
    /// accepted these exact bytes on this exact build against this pairing" — and never a
    /// certification the package earned by itself: a record found inside a package is still ignored
    /// and reported, and this op does not change that. <c>evidence</c> is required so the file says
    /// where the review lives.</para>
    ///
    /// <para><paramref name="bepInExRoot"/> defaults to the root the live catalog was built on. Reads
    /// bytes, writes one JSON file; loads nothing and contacts no game process.</para>
    /// </summary>
    internal static System.Collections.Generic.Dictionary<string, object?> Qualify(
        string? zip,
        string? target,
        string? evidence,
        string? bepInExRoot = null)
    {
        var result = new System.Collections.Generic.Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["action"] = "qualify",
            ["zip"] = zip,
        };

        if (!Admission.QualificationAuthoring.TryTarget(target, out var installTarget))
        {
            result["ok"] = false;
            result["error"] = "target must be Client, LocalServer or Both";
            return result;
        }

        string root = (bepInExRoot ?? LocalIndexRegistry.Current?.BepInExRoot ?? "").Trim();
#if !PROGENITOR_TESTS
        if (root.Length == 0)
        {
            try { root = BepInEx.Paths.BepInExRootPath; }
            catch { root = ""; }
        }
#endif

        var authored = Admission.QualificationAuthoring.Append(root, zip, installTarget, evidence);
        result["ok"] = authored.Ok;
        result["target"] = installTarget.ToString();
        result["bepInExRoot"] = root.Length == 0 ? null : root;
        if (!authored.Ok)
        {
            result["error"] = authored.Error;
            return result;
        }

        var record = authored.Record!;
        result["file"] = authored.Path;
        result["replaced"] = authored.Replaced;
        result["records"] = authored.RecordCount;
        result["image"] = authored.ImageName;
        result["embeddedRecordIgnored"] = authored.EmbeddedRecordPresent;
        result["scanValid"] = authored.Scan?.Valid;
        result["record"] = new System.Collections.Generic.Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = record.Id,
            ["pluginId"] = record.PluginId,
            ["artifactSha256"] = record.Artifact.Sha256,
            ["artifactSize"] = record.Artifact.Size,
            ["dependencyClosure"] = record.Closure.Canonical,
            [Admission.CompatibilityKeys.GameBuild] = record.GameBuild,
            [Admission.CompatibilityKeys.BootstrapContract] = record.BootstrapContract,
            [Admission.CompatibilityKeys.CoreContract] = record.CoreContract,
            ["side"] = Admission.QualificationAuthoring.SideText(record.Side),
            ["operation"] = record.Operation.ToString(),
            ["target"] = record.Target.ToString(),
            ["qualifiedUtc"] = record.QualifiedUtc,
            ["evidence"] = record.Evidence,
        };
        result["note"] =
            "a qualification record is a review claim, never self-certification: it says a person accepted "
            + "these exact bytes on this game build against this Bootstrap/Core pairing. A record embedded in "
            + "a package is still ignored.";
        return result;
    }

    private static System.Collections.Generic.List<string> KeysOf(IReadOnlyList<CatalogEntry> entries)
    {
        var keys = new System.Collections.Generic.List<string>(entries.Count);
        for (int i = 0; i < entries.Count; i++) keys.Add(entries[i].Key);
        return keys;
    }
}

/// <summary>The outcome of one <see cref="LocalIndexSource.Load"/>: entries, or the reason there are none.</summary>
internal sealed class LocalIndexLoad
{
    public LocalIndexLoad(bool ok, string? error, IReadOnlyList<CatalogEntry>? entries)
    {
        Ok = ok;
        Error = error;
        Entries = entries ?? Array.Empty<CatalogEntry>();
    }

    public bool Ok { get; }
    public string? Error { get; }
    public IReadOnlyList<CatalogEntry> Entries { get; }

    public static LocalIndexLoad Failed(string error) => new(false, error, null);
}

/// <summary>
/// HOT1: serves <c>file://</c> URLs from disk so the local dummy listing's <c>download_url</c> and
/// <c>icon</c> resolve with no network. Confined to one folder when a root is given; any other
/// scheme is refused (403) rather than read, and a missing file is a 404.
/// </summary>
internal sealed class FileCatalogTransport : ICatalogTransport
{
    private readonly string? _root;

    public FileCatalogTransport(string? root = null)
    {
        _root = string.IsNullOrWhiteSpace(root) ? null : Path.GetFullPath(root.Trim());
    }

    public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(request.Uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Refused(403));

        string path;
        try { path = Path.GetFullPath(request.Uri.LocalPath); }
        catch { return Task.FromResult(Refused(403)); }

        if (_root != null && !ModManifestIds.IsUnder(_root, path))
            return Task.FromResult(Refused(403));

        try
        {
            if (!File.Exists(path)) return Task.FromResult(Refused(404));
            var info = new FileInfo(path);
            Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Task.FromResult(new CatalogTransportResponse(200, stream, null, info.Length));
        }
        catch (Exception)
        {
            return Task.FromResult(Refused(404));
        }
    }

    private static CatalogTransportResponse Refused(int status) =>
        new(status, new MemoryStream(Array.Empty<byte>()));
}

/// <summary>
/// HOT1: one transport that serves <c>file://</c> from disk and hands everything else to the real
/// HTTPS transport, so a configured local index never changes how the fetched catalog is loaded.
/// </summary>
internal sealed class RoutingCatalogTransport : ICatalogTransport
{
    private readonly ICatalogTransport _inner;
    private readonly FileCatalogTransport _files;

    public RoutingCatalogTransport(ICatalogTransport inner, string? fileRoot = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _files = new FileCatalogTransport(fileRoot);
    }

    public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        return string.Equals(request.Uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)
            ? _files.SendAsync(request, cancellationToken)
            : _inner.SendAsync(request, cancellationToken);
    }
}
