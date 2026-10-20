using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install.Admission;

namespace Progenitor.Install;

/// <summary>
/// Concrete <see cref="IInstallCatalog"/> for the Install-mods UI. Game-free: every path is a
/// parameter, transport is injected, and nothing here touches Unity. Same-session writes into
/// <c>progenitor-reload/</c> are not implemented.
/// </summary>
internal sealed class InstallCatalogService : IInstallCatalog
{
    /// <summary>
    /// Review 2026-10-07 (LOW): the transport and the fetch options as one immutable pair, replaced whole when
    /// <c>[Install] LocalIndexPath</c> changes, so a fetcher built on another thread never pairs one route's transport with
    /// the other's options. Read it once per fetcher.
    /// </summary>
    private sealed record FetchRoute(ICatalogTransport Transport, CatalogFetchOptions Options);

    private volatile FetchRoute _route;

    /// <summary>LOCALRELOAD1: the HTTPS transport the local index's file route is put in front of.</summary>
    private readonly ICatalogTransport _baseTransport;

    /// <summary>LOCALRELOAD1: the caller named the local folder; otherwise it follows <c>[Install] LocalIndexPath</c>.</summary>
    private readonly bool _localIndexExplicit;
    private readonly ICatalogClock _clock;
    private readonly IHotLoadEligibility _eligibility;
    private readonly IAdmissionEvaluator? _admission;
    private readonly string _bepInExRoot;
    private readonly string? _configuredServerRoot;

    /// <summary>
    /// REMOTETEST1: the local server root this catalog may read and write right now. Null while
    /// ServerRootSuppressed says the connected server is being handled through the remote route
    /// (TreatLocalServerAsRemote), so no plan, write, removal, reconcile or scan touches that folder.
    /// </summary>
    private string? _serverBepInExRoot => ServerRootSuppressedNow() ? null : (ServerRootOverrideNow() ?? _configuredServerRoot);

    /// <summary>
    /// F23: a server root that replaces the configured one while it answers (live: the hosted private game's server
    /// folder while this client hosts it). Null or an empty answer: the configured root.
    /// </summary>
    internal Func<string?>? ServerRootOverride { get; set; }

    /// <summary>
    /// LOCALDETECT1: the override is the hosted private game's folder (named as such in the log), not an auto-detected local
    /// server's. Null probe: an override is the hosted one (the F23 behavior).
    /// </summary>
    internal Func<bool>? ServerRootOverrideIsHosted { get; set; }

    private bool ServerRootOverrideIsHostedNow()
    {
        var probe = ServerRootOverrideIsHosted;
        if (probe == null) return true;
        try { return probe(); }
        catch { return false; }
    }

    private string? ServerRootOverrideNow()
    {
        var probe = ServerRootOverride;
        if (probe == null) return null;
        try { return probe() is { Length: > 0 } root ? Path.GetFullPath(root) : null; }
        catch { return null; }
    }

    /// <summary>REMOTETEST1: true while the local server folder is off limits (wired by the Mods menu to the forced-remote route).</summary>
    internal Func<bool>? ServerRootSuppressed { get; set; }

    private bool ServerRootSuppressedNow()
    {
        var probe = ServerRootSuppressed;
        if (probe == null) return false;
        try { return probe(); }
        catch { return false; }
    }
    private readonly bool _localServerConfigured;
    private readonly OrdinaryIdentityCache _identityCache;
    private readonly VerifiedManifestCache _verifiedCache;
    private LocalIndexSource _localIndex;
    private readonly bool _thisProcessIsServer;
    private IReadOnlyList<CatalogEntry> _fetchedEntries = Array.Empty<CatalogEntry>();
    private readonly HashSet<string> _localKeys = new(StringComparer.OrdinalIgnoreCase);
    private string? _localIndexError;
    private int _localShadowed;

    // INDEX1: the Progenitor Index rows merged between the local test source and Thunderstore (an Index row shadows the
    // Thunderstore row of the same key), the source's list version they were built from, and how many they shadowed.
    private IReadOnlyList<CatalogEntry> _indexEntries = Array.Empty<CatalogEntry>();
    private int _indexEntriesVersion = -1;
    private int _indexShadowed;
    private ProgenitorIndexSource? _indexSource;

    /// <summary>
    /// INDEX1: the Progenitor Index this catalog lists and installs from beside Thunderstore (null: Thunderstore only, the
    /// tests' default). Set by the Install tab, the join screens and the server's catalog.
    /// </summary>
    internal ProgenitorIndexSource? IndexSource
    {
        get { lock (_gate) return _indexSource; }
        set
        {
            lock (_gate)
            {
                bool had = _indexEntries.Count > 0;
                _indexSource = value;
                _indexEntriesVersion = -1;
                _indexEntries = Array.Empty<CatalogEntry>();
                // A catalog that already merged rows takes the change now (the source's list, or none).
                if (had || _fetchedEntries.Count > 0)
                {
                    FollowIndexLocked();
                    MergeLocked();
                }
            }
        }
    }

    /// <summary>
    /// INDEX1: <c>[Mods] UseThunderstore</c> (null: on). Off, the merge leaves Thunderstore's rows out and lists the local
    /// test source and the Progenitor Index alone.
    /// </summary>
    internal Func<bool>? ThunderstoreEnabled { get; set; }

    private bool ThunderstoreOn()
    {
        try { return ThunderstoreEnabled?.Invoke() ?? true; }
        catch { return true; }
    }

    /// <summary>
    /// INDEX1: merge without reading Thunderstore (it is off): the local test source and the Index list the source holds now.
    /// Always succeeds; the count may be zero.
    /// </summary>
    internal CatalogLoadResult LoadIndexOnly()
    {
        string stamp = CatalogFetcher.FormatStamp(_clock.UtcNow);
        lock (_gate)
        {
            _indexStamp = stamp;
            FollowConfiguredLocalIndexLocked();
            FollowIndexLocked();
            int count = MergeLocked();
            _status = InstallProgress.Idle;
            return new CatalogLoadResult(true, null, count, stamp);
        }
    }

    /// <summary>INDEX1: how old the Index may be before an index load checks it again (Install tab 30 min, join 10).</summary>
    internal TimeSpan IndexRefreshAge { get; set; } = ProgenitorIndexSource.InstallTabAge;

    /// <summary>INDEX1: Thunderstore rows an Index row replaced on the last merge.</summary>
    internal int IndexShadowed
    {
        get { lock (_gate) return _indexShadowed; }
    }

    /// <summary>INDEX1: the Index rows on the last merge (newest version of each listed mod).</summary>
    internal IReadOnlyList<CatalogEntry> IndexEntries
    {
        get { lock (_gate) return _indexEntries; }
    }

    /// <summary>
    /// INDEX1: check the Progenitor Index on this catalog's cadence (<see cref="IndexRefreshAge"/>; <paramref name="force"/>
    /// checks now) and merge the list it holds afterwards: a failed check keeps the last good list. Never throws but on
    /// cancellation.
    /// </summary>
    internal async Task RefreshIndexAsync(CancellationToken cancellationToken, bool force = false)
    {
        var source = IndexSource;
        if (source == null) return;
        try { await source.RefreshAsync(IndexRefreshAge, cancellationToken, force).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { InstallLog.Warn("Install: Progenitor Index check threw: " + e.Message); }
        lock (_gate)
        {
            if (FollowIndexLocked()) MergeLocked();
        }
    }

    /// <summary>INDEX1: take the Index source's current rows when its list changed since the last merge. True when they did.</summary>
    private bool FollowIndexLocked()
    {
        var source = _indexSource;
        if (source == null)
        {
            if (_indexEntries.Count == 0) return false;
            _indexEntries = Array.Empty<CatalogEntry>();
            _indexEntriesVersion = -1;
            return true;
        }

        IReadOnlyList<CatalogEntry> rows;
        int version;
        bool enabled = source.Enabled;
        try
        {
            source.EnsureCache();
            version = enabled ? source.Version : -2;
            if (version == _indexEntriesVersion) return false;
            rows = enabled ? source.Entries() : Array.Empty<CatalogEntry>();
        }
        catch (Exception e)
        {
            InstallLog.Warn("Install: the Progenitor Index list could not be read: " + e.Message);
            return false;
        }

        _indexEntries = rows;
        _indexEntriesVersion = version;
        return true;
    }

    /// <summary>
    /// INDEX1: why a pinned listing may not be downloaded, or null (an unpinned listing, or a pin whose URL is the one the
    /// signed list names on that source's own host).
    /// </summary>
    internal static string? PinRefusal(CatalogEntry entry)
    {
        var pin = entry?.Pin;
        if (pin == null) return null;
        if (IndexCatalog.ProgenitorRangeUnreadable(entry)) return IndexCatalog.RangeUnreadable;
        if (IndexCatalog.ProgenitorTooOld(entry, out string needs)) return IndexCatalog.TooOldRefusal(needs);
        if (!string.Equals((entry!.DownloadUrl ?? "").Trim(), pin.Url.Trim(), StringComparison.Ordinal))
            return "the download is not the file the Progenitor Index names";
        if (!SelfFeed.IsSha256(pin.Sha256)) return "the Progenitor Index lists no sha256 for this file";
        if (!ProgenitorIndex.IsAllowedPackageUrl(pin.Source, pin.Repo, pin.Url, DevIndexOrigin()))
            return pin.Source == IndexPackage.SourceThunderstore
                ? "the Progenitor Index names a download that is not a Thunderstore link (" + pin.Url + ")"
                : "the Progenitor Index names a download that is not a GitHub release of " + (pin.Repo.Length > 0 ? pin.Repo : "the mod's repository") + " (" + pin.Url + ")";
        return null;
    }

    /// <summary>INDEX1: the route's options for a pinned download: redirects only to that source's hosts, cut at the signed size.</summary>
    private static CatalogFetchOptions PinnedOptions(CatalogFetchOptions o, IndexPin pin)
    {
        string? dev = DevIndexOrigin();
        string source = pin.Source;
        return new CatalogFetchOptions
        {
            IndexUrl = o.IndexUrl,
            MaxRedirects = o.MaxRedirects,
            MaxRetries = o.MaxRetries,
            // One byte over the signed size is a mismatch; the check after the download names it.
            MaxBytes = pin.Size > 0 && pin.Size < o.MaxBytes ? pin.Size + 1 : o.MaxBytes,
            Timeout = o.Timeout,
            Backoff = o.Backoff,
            UserAgent = o.UserAgent,
            AllowFileUrls = false,
            RedirectAllowed = u => ProgenitorIndex.IsAllowedRedirect(source, u, dev),
        };
    }

    private static string? DevIndexOrigin()
    {
        try { return ProgenitorIndexConfig.DevOrigin; }
        catch { return null; }
    }

    /// <summary>INDEX1: may an install of this listing hot load (an Index listing, or the developer's local test source)?</summary>
    internal bool HotSourceAllowed(CatalogEntry? entry) => IndexCatalog.MayHotLoad(entry, entry != null && IsLocalKey(entry.Key));

    /// <summary>INSTORD1 test seam: the <c>[BepInPlugin]</c> GUIDs the target has loaded. Production fills it from the inventory.</summary>
    internal Func<InstallTarget, IReadOnlyList<string>>? LoadedPluginGuids { get; set; }
    private readonly object _gate = new();

    private IReadOnlyList<CatalogEntry> _entries = Array.Empty<CatalogEntry>();
    private readonly Dictionary<string, CatalogEntry> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackageInspection> _inspections = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// AUTOUPDATE1 (live 2026-10-09): the listing version each inspection was made of. A re-merge re-applies an
    /// inspection only while the index still lists that version; a newer listing replaces it (before this, the inspected
    /// older manifest was laid over the newer listing for the rest of the session, so a published update never showed).
    /// </summary>
    private readonly Dictionary<string, string> _inspectedListing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _zipPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, InstallPlan> _plans = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _approved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// DEPS1: for each listing that was planned together with its dependencies, the packages that
    /// same install writes before it. Every later <see cref="ComputePlan"/> of that listing — the
    /// recompute inside Approve and ExecuteToPending included — reads it, so the approval a
    /// with-dependencies plan carries stays bound to the same plan hash. Cleared when the set
    /// finishes (either way) or through <see cref="ForgetClosure"/>.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _closures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ThunderstoreListingDocuments _documents;
    private InstallProgress _status = InstallProgress.Idle;
    private string? _indexStamp;

    public InstallCatalogService(
        string bepInExRoot,
        ICatalogTransport transport,
        CatalogFetchOptions? fetchOptions = null,
        ICatalogClock? clock = null,
        IHotLoadEligibility? eligibility = null,
        bool localServerConfigured = false,
        IAdmissionEvaluator? admission = null,
        string? serverBepInExRoot = null,
        string? localIndexPath = null,
        bool thisProcessIsServer = false,
        bool registerForBridge = true)
    {
        if (string.IsNullOrWhiteSpace(bepInExRoot)) throw new ArgumentException("BepInEx root is required.", nameof(bepInExRoot));
        _bepInExRoot = Path.GetFullPath(bepInExRoot);
        // TSTRAFFIC1: README bodies and remembered README 404s persist under progenitor-cache/readme (no changelog is fetched).
        _documents = new ThunderstoreListingDocuments(new ListingDiskCache(_bepInExRoot));
        if (transport == null) throw new ArgumentNullException(nameof(transport));
        // HOT1: the folder the caller passed, else the configured [Install] LocalIndexPath.
        _baseTransport = transport;
        _localIndexExplicit = !string.IsNullOrWhiteSpace(localIndexPath);
        _localIndex = new LocalIndexSource(
            string.IsNullOrWhiteSpace(localIndexPath) ? LocalIndexConfig.Resolve() : localIndexPath);
        _thisProcessIsServer = thisProcessIsServer;
        var options = fetchOptions ?? CatalogFetchOptions.Default;

        // HOT1: a configured local index folder adds a file:// route in front of the HTTPS
        // transport and lets the fetcher accept a file:// source URL. Without the key nothing
        // changes: the transport, the options and the HTTPS-only rule are exactly as before.
        ICatalogTransport chosen;
        if (_localIndex.Configured)
        {
            chosen = new RoutingCatalogTransport(transport, _localIndex.Folder);
            options = new CatalogFetchOptions
            {
                IndexUrl = options.IndexUrl,
                MaxRedirects = options.MaxRedirects,
                MaxRetries = options.MaxRetries,
                MaxBytes = options.MaxBytes,
                Timeout = options.Timeout,
                Backoff = options.Backoff,
                UserAgent = options.UserAgent,
                AllowFileUrls = true,
            };
        }
        else chosen = transport;

        // LIMITS1: package downloads stop at [Install] MaxDownloadMB unless the caller gave its own cap.
        if (options.MaxBytes == CatalogFetcher.DefaultMaxBytes && NonModPackages.DownloadLimit != options.MaxBytes)
        {
            options = new CatalogFetchOptions
            {
                IndexUrl = options.IndexUrl,
                MaxRedirects = options.MaxRedirects,
                MaxRetries = options.MaxRetries,
                MaxBytes = NonModPackages.DownloadLimit,
                Timeout = options.Timeout,
                Backoff = options.Backoff,
                UserAgent = options.UserAgent,
                AllowFileUrls = options.AllowFileUrls,
            };
        }

        _route = new FetchRoute(chosen, options);
        _clock = clock ?? new SystemCatalogClock();
        _eligibility = eligibility ?? new UnimplementedHotLoadEligibility();
        _localServerConfigured = localServerConfigured;
        _admission = admission;
        _configuredServerRoot = string.IsNullOrWhiteSpace(serverBepInExRoot) ? null : Path.GetFullPath(serverBepInExRoot);
        _identityCache = new OrdinaryIdentityCache(_bepInExRoot);
        _verifiedCache = new VerifiedManifestCache(_bepInExRoot);
        // JOINFLOW2: a catalog built for one join batch does not take the bridge's install_local pointer from the Install tab's.
        if (registerForBridge) LocalIndexRegistry.Register(this);
        ReconcileLedgers();
    }

    /// <summary>
    /// INSTALLFAIL1: forget one listing's downloaded zip and delete it when it is a <c>.part</c>
    /// this catalog fetched. A local zip it was pointed at is never deleted. The inspection stays:
    /// the row keeps what it learned (side, manifest), and a later install downloads again.
    /// </summary>
    internal void ReleaseDownload(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        string? path;
        lock (_gate)
        {
            if (!_zipPaths.TryGetValue(key!, out path)) return;
            _zipPaths.Remove(key!);
        }

        DeletePart(path);
    }

    private static void DeletePart(string? path)
    {
        if (string.IsNullOrEmpty(path) || !path!.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) { InstallLog.Warn("Install: could not delete the spent download " + path + ": " + e.Message); }
    }

    /// <summary>
    /// INSTORD2: at catalog open, and after every install and remove, each root's install ledger is
    /// brought back in step with the files — a <c>pending</c> row Bootstrap promoted now names its
    /// <c>progenitor-reload/</c> path as <c>mod</c>, and a row whose file vanished with no promoted
    /// copy is dropped. Without this the Install tab stops recognizing its own installs at the first
    /// restart after a promote.
    /// </summary>
    internal LedgerReconcileResult ReconcileLedgers()
    {
        var rewritten = new List<string>();
        var dropped = new List<string>();
        var roots = RootsFor(InstallTarget.Both);
        for (int i = 0; i < roots.Count; i++)
        {
            LedgerReconcileResult one;
            try { one = ServerInstall.ReconcilePromoted(roots[i]); }
            catch { continue; }
            for (int j = 0; j < one.Rewritten.Count; j++) rewritten.Add(one.Rewritten[j]);
            for (int j = 0; j < one.Dropped.Count; j++) dropped.Add(one.Dropped[j]);
        }

        return new LedgerReconcileResult(rewritten, dropped);
    }

    /// <summary>
    /// POLISH1 defect C: the Install tab's own re-check, run on every menu open and tab select.
    /// <see cref="ReconcileLedgers"/> runs at construction and after this session's own installs and
    /// removals, which is blind to the one thing that changes a ledger from outside: the local server
    /// restarting and its Bootstrap promoting the pending files. Live (deploy #70) the server had
    /// promoted two installs and the client's rows still read <c>pending</c> for the rest of the
    /// session, because nothing re-read that root.
    ///
    /// <para>The cost is one <see cref="FileInfo"/> per root: a root whose
    /// <c>progenitor-install.json</c> has the same write time and length as at the last pass is
    /// skipped, and only a moved stamp pays for a reconcile. An unreadable stamp reconciles (the
    /// cheap check failing must not be what hides a promote). Returns what changed, so the caller
    /// knows to re-take its scan.</para>
    /// </summary>
    internal LedgerReconcileResult ReconcileChangedLedgers()
    {
        var rewritten = new List<string>();
        var dropped = new List<string>();
        var roots = RootsFor(InstallTarget.Both);
        for (int i = 0; i < roots.Count; i++)
        {
            string root = roots[i];
            string stamp = LedgerStamp(root);
            if (stamp.Length > 0)
            {
                lock (_ledgerStampGate)
                {
                    if (_ledgerStamps.TryGetValue(root, out var seen)
                        && string.Equals(seen, stamp, StringComparison.Ordinal))
                        continue;
                }
            }

            LedgerReconcileResult one;
            try { one = ServerInstall.ReconcilePromoted(root); }
            catch { continue; }
            for (int j = 0; j < one.Rewritten.Count; j++) rewritten.Add(one.Rewritten[j]);
            for (int j = 0; j < one.Dropped.Count; j++) dropped.Add(one.Dropped[j]);
            // The reconcile rewrites the file, so the stamp is read again after it and not before:
            // storing the pre-pass stamp would make the next open reconcile the same change twice.
            string after = LedgerStamp(root);
            if (after.Length > 0)
            {
                lock (_ledgerStampGate) _ledgerStamps[root] = after;
            }
        }

        return new LedgerReconcileResult(rewritten, dropped);
    }

    private readonly object _ledgerStampGate = new();
    private readonly Dictionary<string, string> _ledgerStamps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The install ledger's write time and length under one BepInEx root. Empty when it cannot be
    /// read at all, which the caller treats as "reconcile anyway".
    /// </summary>
    private static string LedgerStamp(string bepInExRoot)
    {
        try
        {
            string? gameRoot = ServerInstall.GameRootOf(bepInExRoot);
            if (string.IsNullOrEmpty(gameRoot)) return "";
            var info = new FileInfo(ServerInstallLedger.PathFor(gameRoot!));
            return info.Exists
                ? info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/" + info.Length
                : "absent";
        }
        catch { return ""; }
    }

    /// <summary>POLISH1: the client BepInEx root this service writes to and reads its policy from.</summary>
    internal string ClientBepInExRoot => _bepInExRoot;

    /// <summary>
    /// INSTORD2: the plugin identity a prior inspection of this listing recorded, read from
    /// <c>progenitor-cache/identity</c> without a download. The exact version first, then any
    /// inspected version of the same package. Null when nothing was ever inspected.
    /// </summary>
    internal OrdinaryIdentityRecord? TryRestingIdentity(CatalogEntry? entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.Owner) || string.IsNullOrEmpty(entry.PackageName)) return null;
        string version = string.IsNullOrWhiteSpace(entry.Manifest.Version) ? "0" : entry.Manifest.Version!;
        var exact = _identityCache.TryRead(entry.Owner!, entry.PackageName!, version);
        if (exact != null && exact.PluginGuid.Length > 0) return exact;
        var all = _identityCache.TryReadAll(entry.Owner!, entry.PackageName!);
        for (int i = all.Count - 1; i >= 0; i--)
        {
            if (all[i].PluginGuid.Length > 0) return all[i];
        }

        return null;
    }

    /// <summary>INSTORD1: the validated local-server BepInEx root, when Configuration supplied one.</summary>
    public string? ServerBepInExRoot => _serverBepInExRoot;

    /// <summary>
    /// REMOTETEST1: the folder this catalog was built for, suppressed or not. Whoever asks "was this
    /// catalog built for the configured folder?" must ask this, never <see cref="ServerBepInExRoot"/>:
    /// live, the Mods menu compared the suppressed (null) root with the configured one on every
    /// Install-tab build, rebuilt the catalog each time, and the tab looped between "Loading the
    /// package index" and "The package index is not loaded" about ten times a second.
    /// </summary>
    internal string? ConfiguredServerBepInExRoot => _configuredServerRoot;

    /// <summary>Was this catalog built for <paramref name="serverBepInExRoot"/> (the configured folder's root, or null)? Independent of the forced route.</summary>
    internal bool BuiltFor(string? serverBepInExRoot)
    {
        string? want = string.IsNullOrWhiteSpace(serverBepInExRoot) ? null : Path.GetFullPath(serverBepInExRoot);
        return string.Equals(_configuredServerRoot, want, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The inspected-identity cache (<c>progenitor-cache/identity</c>) this service reads and writes.</summary>
    internal OrdinaryIdentityCache IdentityCache => _identityCache;

    /// <summary>
    /// POLISH4 item 2: this service's identity route as a probe — "which plugin GUID did a prior
    /// inspection read out of this listing's package". Handed to <see cref="InstalledState.Scan"/>
    /// and to <see cref="LedgerSpelling"/> so a ledger row an older build wrote under the plugin id
    /// resolves to its listing with no re-inspection and no download.
    /// </summary>
    internal LedgerIdentityProbe IdentityProbe => entry => TryRestingIdentity(entry)?.PluginGuid;

    /// <summary>The BepInEx roots one target writes to, in order. Empty when the target has no root.</summary>
    internal IReadOnlyList<string> RootsFor(InstallTarget target)
    {
        switch (target)
        {
            case InstallTarget.LocalServer:
                return _serverBepInExRoot == null ? Array.Empty<string>() : new[] { _serverBepInExRoot };
            case InstallTarget.Both:
                return _serverBepInExRoot == null ? new[] { _bepInExRoot } : new[] { _bepInExRoot, _serverBepInExRoot };
            default:
                return new[] { _bepInExRoot };
        }
    }

    /// <summary>
    /// INSTORD1: what the dependency check may consult on <paramref name="target"/> — the ledger
    /// rows of every root the target writes to, the GUIDs it has loaded, and the identity cache.
    /// </summary>
    internal OrdinaryDependencyView DependencyView(InstallTarget target) => DependencyView(target, null);

    /// <summary>
    /// The same view, plus (DEPS1) the packages this same ordered install writes before the one being
    /// planned, so the root of a closure stops refusing for a dependency it is installing itself.
    /// </summary>
    internal OrdinaryDependencyView DependencyView(InstallTarget target, IReadOnlyList<string>? closurePackages)
    {
        var sources = new List<string>();
        var roots = RootsFor(target);
        for (int i = 0; i < roots.Count; i++)
        {
            var rows = ServerInstall.LedgerSources(roots[i]);
            for (int j = 0; j < rows.Count; j++)
                if (!sources.Contains(rows[j])) sources.Add(rows[j]);
        }

        IReadOnlyList<string> loaded = Array.Empty<string>();
        try { loaded = LoadedPluginGuids?.Invoke(target) ?? Array.Empty<string>(); }
        catch { loaded = Array.Empty<string>(); }
        return new OrdinaryDependencyView(sources, loaded, _identityCache, closurePackages);
    }

    /// <summary>
    /// INSTORD1: remember the plugin GUID an inspection read from the bytes, so a later package
    /// that depends on this one can be satisfied by a loaded plugin without guessing a GUID from
    /// the package name.
    /// </summary>
    private void CacheIdentity(CatalogEntry entry, PackageInspection inspection)
    {
        var plugin = inspection.OrdinaryLayout?.Plugin;
        if (plugin == null || plugin.PluginGuid.Length == 0) return;
        if (string.IsNullOrEmpty(entry.Owner) || string.IsNullOrEmpty(entry.PackageName)) return;
        _identityCache.Write(new OrdinaryIdentityRecord(
            entry.Owner!,
            entry.PackageName!,
            string.IsNullOrWhiteSpace(entry.Manifest.Version) ? "0" : entry.Manifest.Version,
            plugin.PluginGuid,
            plugin.PluginVersion,
            plugin.Sha256,
            inspection.OrdinaryLayout?.LoadsIn));
    }

    /// <summary>
    /// INSTORD1 Remove: delete the ledgered pending and promoted files of one listing on every root
    /// that names it, and drop those rows. Nothing outside the ledger is touched.
    /// </summary>
    public ServerInstallResult RemovePackage(string key) => RemovePackage(key, force: false);

    /// <summary>
    /// DEPS1: the same removal, aware of what else is installed. When another ledgered install on any
    /// root lists this package among its Thunderstore dependencies, the removal is refused and the
    /// result carries those packages in <see cref="ServerInstallResult.Dependents"/> — the player is
    /// told what it would break and can ask again with <paramref name="force"/>. Nothing is deleted
    /// on the refused call.
    /// </summary>
    public ServerInstallResult RemovePackage(string key, bool force) => RemovePackage(key, force, InstallTarget.Both);

    /// <summary>
    /// <inheritdoc cref="RemovePackage(string, bool)"/>
    /// <para>POLISH3 item A: <paramref name="scope"/> narrows the removal to one side's roots, so an
    /// uninstall can delete the local server's pending copy while the client's loaded half takes the
    /// route its own state asks for (a hot unload, or the marker). <see cref="InstallTarget.Both"/>
    /// is every root this catalog writes — the behavior every earlier caller had.</para>
    /// </summary>
    public ServerInstallResult RemovePackage(string key, bool force, InstallTarget scope)
    {
        ServerInstallResult result;
        try { result = RemovePackageCore(key, force, scope); }
        catch (Exception e) { result = ServerInstallResult.Refuse(e.Message); }
        LogRemoval("removed", key, scope, result);
        return result;
    }

    /// <summary>
    /// REMOTETEST1 (live: a confirmed "Cancel installing QStacks?" left no Install: line): one log
    /// line per removal, mark or unmark, with the files it touched or the reason it did nothing.
    /// </summary>
    private void LogRemoval(string verb, string key, InstallTarget scope, ServerInstallResult result)
    {
        // SRVUNINST1 (live 2026-10-07: "client and the local server" with no server folder configured): a scope that reached
        // no server root names the client alone.
        if (scope == InstallTarget.Both && _serverBepInExRoot == null) scope = InstallTarget.Client;
        string where = InstallTargetNames.Half(scope);
        // F35 (live 2026-10-08): while this client hosts a private game, its folder is the server written to.
        if (scope != InstallTarget.Client && ServerRootOverrideNow() is { } overrideRoot)
        {
            if (ServerRootOverrideIsHostedNow())
                where = where.Replace("the local server", HostedServerFolder.Name).Replace("local server", HostedServerFolder.Name);
            else
                where += " at " + (Path.GetDirectoryName(overrideRoot) ?? overrideRoot); // LOCALDETECT1: the auto-detected folder
        }
        if (!result.Ok)
            InstallLog.Warn("Install: " + key + " was not " + verb + " (" + where + "): " + (result.Message ?? InstallReasons.FailedWriteNothing));
        else if (result.NoOp)
            InstallLog.Info("Install: nothing " + verb + " for " + key + " (" + where + "): " + (result.Message ?? ""));
        else
            InstallLog.Info("Install: " + verb + " " + key + " (" + where + "): " + string.Join(", ", result.Removed)
                + (result.HotUnloaded ? "; the reloader unloads it now" : "")
                + (result.Skipped.Count > 0 ? "; left in place (changed): " + string.Join(", ", result.Skipped) : "") + ".");
    }

    private ServerInstallResult RemovePackageCore(string key, bool force, InstallTarget scope)
    {
        if (!TryGetEntry(key, out var entry) || entry == null)
            return ServerInstallResult.Refuse(InstallReasons.UnknownEntry);

        // INSTORD2: remove what the ledger says now, after a promote has been folded in, so the
        // reload copy is deleted through its own row rather than only as a promoted companion.
        ReconcileLedgers();

        var dependents = DependentsOf(entry.Key);
        if (dependents.Count > 0 && !force)
            return ServerInstallResult.RefuseWithDependents(
                InstallReasons.RemoveHasDependents(entry.Key, dependents), dependents);

        var removed = new List<string>();
        var skipped = new List<string>();
        bool noOp = true;
        string message = ServerInstall.NotInstalledStatus;
        var roots = RootsFor(scope);
        // HOT4 defect: the first-class restart route ledgers its pending row under the *plugin id*
        // (InstallStaging.AppendServerLedger → ServerInstall.TryAppendPending) while the hot route and
        // the ordinary route use the package key. POLISH3 item A: Remove asks for every spelling the
        // ledgers on these roots actually carry for this listing, so a Both install whose client half
        // hot loaded and whose server half went pending is taken back whole — and so is a row an
        // earlier version of this code wrote.
        var asks = LedgerSpellingsOf(entry, roots);
        for (int i = 0; i < roots.Count; i++)
        {
            for (int s = 0; s < asks.Count; s++)
            {
                string ask = asks[s];
                var result = ServerInstall.RemovePackage(roots[i], ask);
                for (int j = 0; j < result.Removed.Count; j++) removed.Add(result.Removed[j]);
                DeleteSpentMarkers(roots[i], result.Removed);
                for (int j = 0; j < result.Skipped.Count; j++) skipped.Add(result.Skipped[j]);
                if (result.NoOp) continue;
                noOp = false;
                message = result.Message;
            }
        }

        // HOT1: a removed file under progenitor-reload/ is a live plugin going away, not a pending
        // file that never loaded. The reloader unloads it within a second, so the row goes back to
        // "none" rather than "removed, restart to finish".
        bool hotUnloaded = false;
        for (int i = 0; i < removed.Count; i++)
        {
            if (removed[i].Replace('\\', '/').IndexOf(
                    Progenitor.Networking.ModFolders.ReloadFolderName + "/",
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                hotUnloaded = true;
                break;
            }
        }

        if (hotUnloaded && !noOp) message = message + " — " + InstallReasons.HotUnloaded;
        return new ServerInstallResult(true, message, removed, skipped, noOp, hotUnloaded, dependents);
    }

    /// <summary>
    /// DEPS1: the ledgered packages that name <paramref name="package"/> among their Thunderstore
    /// dependencies, across every root this catalog writes. A ledger row is a
    /// <c>package:Namespace-Name@version</c> source; its dependency list comes from that package's own
    /// index listing, so a package the index no longer carries contributes nothing rather than a
    /// guess. The package being removed never counts as its own dependent.
    /// </summary>
    internal IReadOnlyList<string> DependentsOf(string package)
    {
        var dependents = new List<string>();
        if (string.IsNullOrWhiteSpace(package)) return dependents;

        var roots = RootsFor(InstallTarget.Both);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < roots.Count; i++)
        {
            IReadOnlyList<string> sources;
            try { sources = ServerInstall.LedgerSources(roots[i]); }
            catch { continue; }

            for (int j = 0; j < sources.Count; j++)
            {
                if (!InstalledState.TrySplitPackageSource(sources[j], out string installed, out _)) continue;
                if (string.Equals(installed, package, StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(installed)) continue;
                if (!TryGetEntry(installed, out var listing) || listing == null) continue;
                if (ListsDependency(listing, package)) dependents.Add(installed);
            }
        }

        return dependents;
    }

    /// <summary>
    /// INSTALL2 item 4: ask for one listing's loaded copies to be removed at the next start. Nothing
    /// is deleted — a marker goes beside each ledgered file and the row is flagged
    /// (<see cref="UninstallMarkers"/>) — so the mod stays loaded and the request can be canceled.
    /// Refused, like <see cref="RemovePackage(string, bool)"/>, while another ledgered install depends
    /// on this package, unless <paramref name="force"/> says to break it on purpose.
    /// </summary>
    internal ServerInstallResult MarkUninstall(string key, bool force = false) =>
        MarkOrCancel(key, mark: true, force, InstallTarget.Both);

    /// <summary>
    /// <inheritdoc cref="MarkUninstall(string, bool)"/>
    /// <para>POLISH3 item A: <paramref name="scope"/> narrows the mark to one side's roots.</para>
    /// </summary>
    internal ServerInstallResult MarkUninstall(string key, bool force, InstallTarget scope) =>
        MarkOrCancel(key, mark: true, force, scope);

    /// <summary>INSTALL2 item 4: Reactivate — drop the pending removal of one listing.</summary>
    internal ServerInstallResult CancelUninstall(string key) =>
        MarkOrCancel(key, mark: false, force: true, InstallTarget.Both);

    /// <inheritdoc cref="CancelUninstall(string)"/>
    internal ServerInstallResult CancelUninstall(string key, InstallTarget scope) =>
        MarkOrCancel(key, mark: false, force: true, scope);

    /// <summary>
    /// INSTALL2 item 4: the packages with a pending removal on any root this catalog writes, as
    /// <c>&lt;owner&gt;-&lt;name&gt;</c> keys (and the plugin-id spelling, when the ledger used it).
    /// One ledger read per root: the Install pane asks this once per build rather than asking
    /// <see cref="IsUninstallPending"/> for every listing.
    /// </summary>
    /// <summary>
    /// INSTALL2 defect A: the packages whose files <em>this process</em> hot loaded, as
    /// <c>&lt;owner&gt;-&lt;name&gt;</c> keys — the <c>hot</c> ledger flag the executor writes, with every
    /// stale flag cleared first. One ledger read per root; the pane asks once per build.
    /// </summary>
    internal IReadOnlyCollection<string> HotLoadedPackages()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = RootsFor(InstallTarget.Both);
        for (int i = 0; i < roots.Count; i++)
        {
            IReadOnlyList<string> sources;
            try { sources = ServerInstall.HotSources(roots[i]); }
            catch { continue; }
            for (int j = 0; j < sources.Count; j++)
            {
                if (InstalledState.TrySplitPackageSource(sources[j], out string package, out _)) set.Add(package);
            }
        }

        return set;
    }

    internal IReadOnlyCollection<string> UninstallPendingPackages() => UninstallPendingPackages(InstallTarget.Both);

    /// <summary>INSTALLSIDES1: the packages marked for removal on one side's roots (the client's, the local server's, or both).</summary>
    internal IReadOnlyCollection<string> UninstallPendingPackages(InstallTarget scope)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = RootsFor(scope);
        for (int i = 0; i < roots.Count; i++)
        {
            IReadOnlyList<string> sources;
            try { sources = UninstallMarkers.PendingSources(roots[i]); }
            catch { continue; }
            for (int j = 0; j < sources.Count; j++)
            {
                if (InstalledState.TrySplitPackageSource(sources[j], out string package, out _)) set.Add(package);
            }
        }

        return set;
    }

    /// <summary>INSTALL2 item 4: is one listing's removal already pending on any root this catalog writes?</summary>
    internal bool IsUninstallPending(string key)
    {
        if (!TryGetEntry(key, out var entry) || entry == null) return false;
        var roots = RootsFor(InstallTarget.Both);
        // POLISH3 item A: every spelling the ledgers carry, so a mark written against a server row
        // ledgered under the plugin id reads back as pending.
        foreach (string source in LedgerSpellingsOf(entry, roots))
        {
            for (int i = 0; i < roots.Count; i++)
            {
                try { if (UninstallMarkers.IsPending(roots[i], source)) return true; }
                catch { /* an unreadable root is not evidence of a pending removal */ }
            }
        }

        return false;
    }

    private ServerInstallResult MarkOrCancel(string key, bool mark, bool force, InstallTarget scope)
    {
        ServerInstallResult result;
        try { result = MarkOrCancelCore(key, mark, force, scope); }
        catch (Exception e) { result = ServerInstallResult.Refuse(e.Message); }
        LogRemoval(mark ? "marked for removal at the next start" : "unmarked (kept)", key, scope, result);
        return result;
    }

    private ServerInstallResult MarkOrCancelCore(string key, bool mark, bool force, InstallTarget scope)
    {
        if (!TryGetEntry(key, out var entry) || entry == null)
            return ServerInstallResult.Refuse(InstallReasons.UnknownEntry);

        // The same promote-aware read the removal does, so a row that named a pending file names its
        // reload copy by the time it is marked.
        ReconcileLedgers();

        var dependents = mark ? DependentsOf(entry.Key) : Array.Empty<string>();
        if (dependents.Count > 0 && !force)
            return ServerInstallResult.RefuseWithDependents(
                InstallReasons.RemoveHasDependents(entry.Key, dependents), dependents);

        var acted = new List<string>();
        var skipped = new List<string>();
        bool noOp = true;
        string message = mark ? UninstallMarkers.NothingToMark : UninstallMarkers.NothingPending;
        var roots = RootsFor(scope);
        // POLISH3 item A: the same source-completeness the removal got — a half ledgered under the
        // plugin id is a half this mark can reach.
        var asks = LedgerSpellingsOf(entry, roots);
        for (int i = 0; i < roots.Count; i++)
        {
            foreach (string source in asks)
            {
                ServerInstallResult one;
                try { one = mark ? UninstallMarkers.Mark(roots[i], source) : UninstallMarkers.Cancel(roots[i], source); }
                catch (Exception e) { return ServerInstallResult.Refuse(e.Message); }
                for (int j = 0; j < one.Removed.Count; j++) acted.Add(one.Removed[j]);
                for (int j = 0; j < one.Skipped.Count; j++) skipped.Add(one.Skipped[j]);
                if (one.NoOp) continue;
                noOp = false;
                message = one.Message;
            }
        }

        return new ServerInstallResult(true, message, acted, skipped, noOp, hotUnloaded: false, dependents: dependents);
    }

    /// <summary>
    /// Both ledger spellings of one listing: the package key and, when it differs, the plugin id the
    /// first-class restart route ledgers under (the HOT4 defect <see cref="RemovePackage(string, bool)"/>
    /// documents).
    /// </summary>
    private IEnumerable<string> SourcesOf(CatalogEntry entry)
    {
        string version = string.IsNullOrWhiteSpace(entry.Manifest.Version) ? "0" : entry.Manifest.Version!;
        yield return LedgerSpelling.SourceFor(entry, version);
        if (!string.IsNullOrWhiteSpace(entry.Manifest.Id)
            && !string.Equals(entry.Manifest.Id, entry.Key, StringComparison.OrdinalIgnoreCase))
            yield return OrdinaryDependencies.LedgerSource(entry.Manifest.Id!, version);
        // POLISH4 item 2: the plugin id this session never inspected, from the identity cache. A row
        // an older build wrote under that spelling is asked for by name even when no root can be read.
        string cached = LedgerSpelling.IdentityOf(entry, IdentityProbe);
        if (cached.Length > 0
            && !string.Equals(cached, entry.Key, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(cached, entry.Manifest.Id ?? "", StringComparison.OrdinalIgnoreCase))
            yield return OrdinaryDependencies.LedgerSource(cached, version);
    }

    /// <summary>
    /// POLISH3 item A: every spelling a ledger on <paramref name="roots"/> has actually used for this
    /// listing, not only the two <see cref="SourcesOf"/> can construct.
    ///
    /// <para>Live (deploy #74) HotDemo was installed to Both: the client half went through the hot
    /// route, which ledgers under the package key, and the local server's half through the
    /// first-class restart route, which ledgers under <c>plan.PluginId</c>
    /// (<c>InstallStaging.AppendServerLedger</c>). <see cref="SourcesOf"/> can only spell the plugin
    /// id when the <em>listing's manifest</em> carries it, so the server row was never asked for and
    /// the Uninstall took the client half alone — the row read <c>Install · restart</c> while the
    /// server's <c>progenitor-pending/</c> copy and its ledger row stayed and the Mods tab still
    /// listed the mod as <c>pendload</c>. Reading the spellings back off the ledgers and keeping the
    /// ones <see cref="InstalledState.PackageMatches"/> accepts closes that class of gap for good:
    /// what the pane can see is exactly what the uninstall can reach.</para>
    /// </summary>
    private IReadOnlyList<string> LedgerSpellingsOf(CatalogEntry entry, IReadOnlyList<string> roots)
    {
        var list = new List<string>(SourcesOf(entry));
        for (int i = 0; i < roots.Count; i++)
        {
            IReadOnlyList<string> sources;
            try { sources = ServerInstall.LedgerSources(roots[i]); }
            catch { continue; }
            for (int j = 0; j < sources.Count; j++)
            {
                string source = sources[j];
                if (!InstalledState.TrySplitPackageSource(source, out string package, out _)) continue;
                // POLISH4 item 2: the identity cache is part of this comparison, so a row spelled with
                // the plugin id is kept even when this session inspected nothing.
                if (!LedgerSpelling.Matches(entry, package, IdentityProbe)) continue;
                bool seen = false;
                for (int k = 0; k < list.Count; k++)
                {
                    if (ServerInstall.SourceNamesSamePackage(list[k], source)) seen = true;
                }

                if (!seen) list.Add(source);
            }
        }

        return list;
    }

    /// <summary>
    /// INSTALL2: the listing one loaded plugin came from — its manifest id, then the plugin GUID a
    /// prior inspection cached. Null when the index does not carry it, which is what the mod page's
    /// seam answers "not ours" to.
    /// </summary>
    internal CatalogEntry? FindEntryForPlugin(string? pluginId)
    {
        if (string.IsNullOrWhiteSpace(pluginId)) return null;
        string id = pluginId!.Trim();
        if (TryGetEntry(id, out var direct) && direct != null) return direct;
        IReadOnlyList<CatalogEntry> entries;
        try { entries = Search(null); }
        catch { return null; }
        for (int i = 0; i < entries.Count; i++)
        {
            if (!string.IsNullOrEmpty(entries[i].Manifest.Id)
                && string.Equals(entries[i].Manifest.Id, id, StringComparison.OrdinalIgnoreCase))
                return entries[i];
        }

        for (int i = 0; i < entries.Count; i++)
        {
            string? guid = TryRestingIdentity(entries[i])?.PluginGuid;
            if (!string.IsNullOrEmpty(guid) && string.Equals(guid, id, StringComparison.OrdinalIgnoreCase))
                return entries[i];
        }

        return null;
    }

    /// <summary>Does this listing's dependency list name <paramref name="package"/>, at any version?</summary>
    private static bool ListsDependency(CatalogEntry listing, string package)
    {
        for (int i = 0; i < listing.ForeignDependencies.Count; i++)
        {
            if (!OrdinaryDependencies.TrySplit(listing.ForeignDependencies[i], out string ns, out string name, out _))
                continue;
            if (string.Equals(ns + "-" + name, package, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// One planner call for Plan, Approve and ExecuteToPending. With an admission evaluator the
    /// plan is built from a fresh <see cref="AdmissionResult"/> over the zip on disk every time,
    /// so changed bytes, a changed snapshot or a changed policy produce a different plan hash and
    /// invalidate the approval. Without one, the legacy eligibility stub decides.
    /// </summary>
    private InstallPlan ComputePlan(CatalogEntry entry, PackageInspection inspection, InstallTarget target)
    {
        AdmissionResult? admission = null;
        AdmissionResult? serverAdmission = null;
        if (_admission != null)
        {
            string? zipPath;
            lock (_gate) _zipPaths.TryGetValue(entry.Key, out zipPath);
            if (!string.IsNullOrEmpty(zipPath))
            {
                // HOT4: a Both install is two processes, so it is admitted twice — the client half
                // with InstallTarget.Client and the local-server half with InstallTarget.LocalServer.
                // A record reviewed for one half therefore applies to that half (and a Both-scoped
                // record covers each, QualificationRecord.TargetCovers), where one evaluation with
                // the whole request target could match neither.
                // HOTDEMO1: the local-server half seen from a client is admitted against what the
                // client can see of that server (HotLoadTrust.ProbeLocalServer) when its reload
                // folder is watched; otherwise against this process's snapshot, which refuses it.
                var serverSnapshot = LocalServerHot(target) ? LocalServerSnapshot(inspection) : null;
                AdmissionResult? Server() => serverSnapshot != null
                    ? _admission.EvaluateWithSnapshot(entry, inspection, InstallTarget.LocalServer, zipPath!, serverSnapshot)
                    : _admission.Evaluate(entry, inspection, InstallTarget.LocalServer, zipPath!);
                if (target == InstallTarget.Both)
                {
                    admission = _admission.Evaluate(entry, inspection, InstallTarget.Client, zipPath!);
                    serverAdmission = Server();
                }
                else if (target == InstallTarget.LocalServer) admission = Server();
                else admission = _admission.Evaluate(entry, inspection, target, zipPath!);
            }
        }
        IReadOnlyList<string>? closure;
        lock (_gate) _closures.TryGetValue(entry.Key ?? "", out closure);
        return InstallPlanner.Plan(
            entry, inspection, target, _eligibility, _localServerConfigured || ServerRootOverrideNow() != null, admission, DependencyView(target, closure),
            _thisProcessIsServer, serverAdmission, LocalServerHot(target), HotSourceAllowed(entry));
    }

    /// <summary>
    /// HOTDEMO1: does <paramref name="target"/> include a local server this client process can
    /// hot-manage — the configured folder's <c>VRisingServer.exe</c> running with Progenitor and no
    /// reload allowlist? Never true in the server process itself (its own snapshot answers there).
    /// </summary>
    internal bool LocalServerHot(InstallTarget target) =>
        !_thisProcessIsServer
        && _serverBepInExRoot != null
        && target is InstallTarget.LocalServer or InstallTarget.Both
        && HotLoadTrust.ProbeLocalServer(_serverBepInExRoot).CanHotManage;

    /// <summary>
    /// HOTDEMO1: what an inspection proved about this listing version's <c>progenitor.json</c> — this
    /// session's own inspection first, else <c>progenitor-cache/verified</c>. Null when nothing did.
    /// </summary>
    internal VerifiedManifestCache.Record? VerifiedFor(CatalogEntry? entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.Owner) || string.IsNullOrEmpty(entry.PackageName)) return null;
        lock (_gate)
        {
            if (_inspections.TryGetValue(entry.Key, out var inspection)
                && inspection.Provenance == CatalogProvenance.ManifestVerified
                && inspection.ProgenitorManifest != null)
                return new VerifiedManifestCache.Record(inspection.ProgenitorManifest, inspection.Side, inspection.EffectivePeers);
        }

        string version = entry.Manifest.Version ?? "";
        if (version.Length == 0) return null;
        return _verifiedCache.ReadAll().TryGetValue(VerifiedManifestCache.Key(entry.Owner!, entry.PackageName!, version), out var record)
            ? record
            : null;
    }

    /// <summary>
    /// SRVUNINST1 (live 2026-10-07): this client's ledgered copies of <paramref name="entry"/> in its own
    /// <c>progenitor-reload</c> (installed, not pending), by absolute path; empty when there are none.
    /// </summary>
    internal List<string> ClientReloadFilesOf(CatalogEntry entry)
    {
        var files = new List<string>();
        if (entry == null) return files;
        string? game = ServerInstall.GameRootOf(_bepInExRoot);
        if (string.IsNullOrEmpty(game)) return files;
        var ledger = ServerInstallLedger.TryRead(game!);
        if (ledger == null) return files;
        string reload = Path.Combine(_bepInExRoot, InstalledState.ReloadFolder) + Path.DirectorySeparatorChar;
        foreach (var row in ledger.Files)
        {
            if (!string.Equals(row.Category, ServerInstallLedger.CategoryMod, StringComparison.Ordinal)) continue;
            if (!InstalledState.TrySplitPackageSource(row.Source, out string package, out _)) continue;
            if (!LedgerSpelling.Matches(entry, package, IdentityProbe)) continue;
            string abs;
            try { abs = Path.GetFullPath(ServerInstallLedger.Absolute(game!, row.Path)); }
            catch { continue; }
            if (abs.StartsWith(reload, StringComparison.OrdinalIgnoreCase) && File.Exists(abs)) files.Add(abs);
        }

        return files;
    }

    /// <summary>HOTDEMO1: may this installed copy be unloaded by deleting it (the trusted hotLoadable flag)?</summary>
    internal bool TrustsHotUnload(CatalogEntry entry, string? filePath, out string reason)
    {
        // INDEX1: only a copy installed from the Progenitor Index (its ledger row says so) or the local test source is
        // unloaded by deleting it; a Thunderstore install is removed at the next start, as it was installed.
        if (!(entry != null && IsLocalKey(entry.Key)) && !LedgerSaysIndex(filePath))
        {
            reason = InstallReasons.HotUnloadIndexOnly;
            return false;
        }

        return HotLoadTrust.CanUnloadHot(VerifiedFor(entry), filePath, out reason);
    }

    /// <summary>
    /// INDEX1: where this root's installed copy of listing <paramref name="key"/> came from: true for the Progenitor Index,
    /// false for anything else (Thunderstore, an earlier build), null when this root's ledger has no copy of it.
    /// </summary>
    internal bool? InstalledFromIndex(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        string? game = ServerInstall.GameRootOf(_bepInExRoot);
        var ledger = string.IsNullOrEmpty(game) ? null : ServerInstallLedger.TryRead(game!);
        if (ledger == null) return null;
        bool? found = null;
        foreach (var row in ledger.Files)
        {
            if (row.Category == ServerInstallLedger.CategoryFramework) continue;
            if (!InstalledState.TrySplitPackageSource(row.Source, out string package, out _)) continue;
            if (!string.Equals(package, key!.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            if (row.FromIndex) return true;
            found = false;
        }

        return found;
    }

    /// <summary>
    /// INDEX1: updates stay with their source. Before an update of <paramref name="key"/> downloads, the catalog's row is
    /// the one of the source the installed copy came from: an Index install keeps the Index row, a Thunderstore install takes
    /// the Thunderstore row an Index row shadows. Null when that is in place; the refusal when that source does not list it.
    /// </summary>
    internal string? UseInstalledSource(string key)
    {
        bool? fromIndex = InstalledFromIndex(key);
        if (fromIndex == null || IsLocalKey(key)) return null;
        if (!fromIndex.Value && !ThunderstoreOn()) return InstallReasons.UpdateThunderstoreOff;
        if (!TryGetEntry(key, out var row) || row == null) return InstallReasons.UnknownEntry;
        if (fromIndex.Value)
            return row.FromIndex ? null : InstallReasons.UpdateIndexGone;
        if (!row.FromIndex) return null;
        if (row.ThunderstoreRow == null) return InstallReasons.UpdateThunderstoreGone;
        UseVersionEntry(row.ThunderstoreRow);
        return null;
    }

    /// <summary>INDEX1: an update's download: the row of the installed copy's own source (<see cref="UseInstalledSource"/>), then the usual download and inspection.</summary>
    internal async Task<InspectCallResult> DownloadUpdateAndInspectAsync(string key, CancellationToken cancellationToken)
    {
        string? refusal = UseInstalledSource(key);
        if (refusal != null)
        {
            TryGetEntry(key, out var entry);
            InstallLog.Warn("Install: update of " + key + " refused: " + refusal + ".");
            return new InspectCallResult(false, refusal, entry, null);
        }

        return await DownloadAndInspectAsync(key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>INDEX1: the install ledger row of this file (on the client or the local server root) names the Progenitor Index.</summary>
    internal bool LedgerSaysIndex(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        string full;
        try { full = Path.GetFullPath(filePath!); }
        catch { return false; }
        foreach (string? root in new[] { _bepInExRoot, _serverBepInExRoot })
        {
            if (string.IsNullOrEmpty(root)) continue;
            string bep = Path.GetFullPath(root!).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(bep, StringComparison.OrdinalIgnoreCase)) continue;
            string? game = ServerInstall.GameRootOf(root!);
            var ledger = string.IsNullOrEmpty(game) ? null : ServerInstallLedger.TryRead(game!);
            if (ledger == null) return false;
            foreach (var row in ledger.Files)
            {
                string abs;
                try { abs = ServerInstallLedger.Absolute(game!, row.Path); }
                catch { continue; }
                if (string.Equals(abs, full, StringComparison.OrdinalIgnoreCase)) return row.FromIndex;
            }

            return false;
        }

        return false;
    }

    /// <summary>
    /// HOTDEMO1: does the local server's loaded copy of this listing go now? Its reload folder must be
    /// watched from here (<see cref="LocalServerHot"/>) and the copy must be a trusted hotLoadable
    /// image; then deleting it makes that server's reloader unload it. Otherwise the marker route.
    /// </summary>
    internal bool ServerUnloadsNow(CatalogEntry entry, out string reason)
    {
        reason = "";
        if (_serverBepInExRoot == null || _thisProcessIsServer)
        {
            reason = "no local server folder";
            return false;
        }

        var runtime = HotLoadTrust.ProbeLocalServer(_serverBepInExRoot);
        if (!runtime.CanHotManage)
        {
            reason = runtime.Reason;
            return false;
        }

        string id = VerifiedFor(entry)?.Manifest.Id ?? TryRestingIdentity(entry)?.PluginGuid ?? entry.Manifest.Id ?? "";
        if (id.Length == 0)
        {
            reason = "the plugin id is not known";
            return false;
        }

        string file = Path.Combine(_serverBepInExRoot, Progenitor.Networking.ModFolders.ReloadFolderName, id + ".dll");
        return TrustsHotUnload(entry, file, out reason);
    }

    /// <summary>
    /// HOTDEMO1: a removal request (<c>.uninstall</c> marker) beside a file this removal deleted is
    /// spent; a later install of the same plugin must not inherit it.
    /// </summary>
    private static void DeleteSpentMarkers(string bepInExRoot, IReadOnlyList<string> removedRelative)
    {
        string? game = ServerInstall.GameRootOf(bepInExRoot);
        if (string.IsNullOrEmpty(game)) return;
        for (int i = 0; i < removedRelative.Count; i++)
        {
            string marker;
            try { marker = UninstallMarkers.MarkerFor(ServerInstallLedger.Absolute(game!, removedRelative[i])); }
            catch { continue; }
            try { if (File.Exists(marker)) File.Delete(marker); }
            catch (Exception e) { InstallLog.Warn("Install: could not delete the spent marker " + marker + ": " + e.Message); }
        }
    }

    /// <summary>HOTDEMO1: this plan's local-server half was admitted for the hot route from here, and the server still qualifies.</summary>
    private bool ServerHalfHot(InstallPlan plan) =>
        plan.HalfFor(InstallTarget.LocalServer) is { Decision: InstallDecision.ApplyNow, ExecutorAvailable: true }
        && LocalServerHot(plan.Target);

    /// <summary>
    /// HOTDEMO1: the server-side facts the client can stand behind. The loader is taken as quiet
    /// (its state is not observable from here), and a copy of this plugin already in the server's
    /// <c>progenitor-reload/</c> stands in the way exactly as a loaded plugin does: that install is a
    /// replacement, which stays on the restart route.
    /// </summary>
    private RuntimeGenerationSnapshot LocalServerSnapshot(PackageInspection inspection)
    {
        string pluginId = inspection.ProgenitorManifest?.Id ?? "";
        LoadedPluginState? current = null;
        try
        {
            string file = Path.Combine(_serverBepInExRoot!, Progenitor.Networking.ModFolders.ReloadFolderName, pluginId + ".dll");
            if (pluginId.Length > 0 && File.Exists(file)) current = new LoadedPluginState(pluginId, null, false, false);
        }
        catch { /* unreadable is not proof of absence; the evaluator still decides */ }

        return new RuntimeGenerationSnapshot(
            Available: true,
            UnavailableReason: null,
            ProcessSide: AdmissionSide.Server,
            Generation: 0,
            LoaderBusy: false,
            AllowlistAllows: true,
            LocalServerRuntimeConnected: true,
            CurrentPlugin: current,
            Identities: Array.Empty<CompatibilityIdentity>());
    }

    /// <summary>DEPS1: drop the remembered closure for one listing (the player closed the confirmation).</summary>
    internal void ForgetClosure(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;
        lock (_gate) _closures.Remove(key!);
    }

    /// <summary>DEPS1: the packages a with-dependencies plan of this listing is writing before it.</summary>
    internal IReadOnlyList<string> RememberedClosure(string? key)
    {
        lock (_gate)
            return !string.IsNullOrEmpty(key) && _closures.TryGetValue(key!, out var packages)
                ? packages
                : Array.Empty<string>();
    }

    /// <summary>
    /// DEPS1: the dependency closure of one listing, from the loaded index and this target's view.
    /// Reads nothing off the network and plans nothing.
    /// </summary>
    internal DependencyClosureResult Closure(string key, InstallTarget target)
    {
        if (!TryGetEntry(key, out var entry) || entry == null)
            return DependencyClosureResult.Refuse(InstallReasons.UnknownEntry);
        return PackageDependencyClosure.Resolve(entry, LookupPackage, DependencyView(target));
    }

    private CatalogEntry? LookupPackage(string package) =>
        TryGetEntry(package, out var entry) ? entry : null;

    /// <summary>
    /// DEPS1: the half one dependency is installed on. A dependency whose own <c>[BepInProcess]</c>
    /// does not run on the chosen target is planned for the half it <em>does</em> run on — a
    /// server-only library needed by a client mod goes to the local server — and
    /// <paramref name="reason"/> says so. A dependency that declares no process, or one the chosen
    /// target already runs, keeps the root's target unchanged.
    /// </summary>
    internal static InstallTarget MemberTarget(PackageInspection inspection, InstallTarget requested, out string reason)
    {
        reason = "";
        var processes = inspection?.OrdinaryLayout?.Plugin?.Processes;
        if (OrdinaryPackage.TargetRunsTheProcess(processes, requested, out _)) return requested;

        string loadsIn = OrdinaryPackage.LoadsInName(processes);
        InstallTarget narrowed = loadsIn switch
        {
            "server" => InstallTarget.LocalServer,
            "client" => InstallTarget.Client,
            _ => requested,
        };

        if (narrowed == requested) return requested;
        reason = InstallReasons.ClosureMemberHalf(loadsIn, InstallTargetNames.Half(narrowed));
        return narrowed;
    }

    /// <summary>
    /// DEPS1: plan one listing together with every package it needs. The closure is resolved from the
    /// loaded index (dependencies first), each <em>missing</em> member is downloaded, inspected and
    /// planned with the same target as the root — narrowed to the half its own
    /// <c>[BepInProcess]</c> runs, see <see cref="MemberTarget"/> — and the root is planned last with
    /// its closure members counted as satisfied, so its INSTORD1 dependency rule no longer refuses
    /// for a package this same install writes.
    ///
    /// <para>Refused as a whole. A member whose download, inspection or plan is refused refuses the
    /// set with <c>Cannot install — &lt;dep&gt; refused: &lt;reason&gt;</c> and nothing is written;
    /// the closure is remembered only when every member planned.</para>
    /// </summary>
    internal async Task<InstallPlanSet> PlanWithDependenciesAsync(
        string key,
        InstallTarget target,
        CancellationToken cancellationToken)
    {
        InstallPlanSet set;
        try { set = await PlanSetCoreAsync(key, target, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { set = InstallPlanSet.Refuse(InstallReasons.PlanBlocked + ": " + e.Message); }

        if (set.Ok && set.Root != null)
            InstallLog.Info("Install: planned " + key + " for " + InstallTargetNames.Half(target) + ": "
                + set.Root.Decision + " (" + set.Members.Count + " package(s))"
                // HOTDEMO1: a restart decision names why it is not loaded now (the admission reasons).
                + (set.Root.Decision == InstallDecision.ApplyNow || string.IsNullOrWhiteSpace(set.Root.Reason)
                    ? "." : ": " + set.Root.Reason));
        else
            InstallLog.Warn("Install: cannot install " + key + " for " + InstallTargetNames.Half(target) + ": "
                + (set.Error ?? InstallReasons.PlanBlocked));
        return set;
    }

    private async Task<InstallPlanSet> PlanSetCoreAsync(
        string key,
        InstallTarget target,
        CancellationToken cancellationToken)
    {
        if (!TryGetEntry(key, out var root) || root == null)
            return InstallPlanSet.Refuse(InstallReasons.UnknownEntry);

        PackageInspection rootInspection;
        lock (_gate)
        {
            if (!_inspections.TryGetValue(root.Key, out rootInspection!))
                return InstallPlanSet.Refuse(InstallReasons.NotInspected);
        }

        var closure = PackageDependencyClosure.Resolve(root, LookupPackage, DependencyView(target));
        if (!closure.Ok) return InstallPlanSet.Refuse(closure.Refusal!);

        // Every member of the closure counts as satisfied for every plan in the set: a dependency's
        // own dependency is written before it, so it must not refuse either.
        var packages = closure.Packages;
        var missing = closure.Missing;
        var members = new List<InstallPlanMember>(missing.Count + 1);

        for (int i = 0; i < missing.Count; i++)
        {
            var member = missing[i];
            // INDEX1: a dependency of an Index listing installs the exact file the signed list pins for it (the Index's own
            // entry first, else Thunderstore's download of that version), checked by its sha256.
            string? pinRefusal = PinDependencyOf(root, member.Package, member.Version);
            if (pinRefusal != null)
            {
                ForgetClosureOf(members, root.Key);
                return InstallPlanSet.Refuse(InstallReasons.ClosureMemberRefused(member.Package, pinRefusal));
            }

            InspectCallResult inspected;
            try
            {
                inspected = await DownloadAndInspectAsync(member.Package, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ForgetClosureOf(members, root.Key);
                return InstallPlanSet.Refuse(InstallReasons.ClosureMemberRefused(member.Package, InstallReasons.FetchCancelled));
            }

            if (!inspected.Ok || inspected.Entry == null || inspected.Inspection == null)
            {
                ForgetClosureOf(members, root.Key);
                return InstallPlanSet.Refuse(
                    InstallReasons.ClosureMemberRefused(member.Package, inspected.Error ?? InstallReasons.FetchTransport));
            }

            var memberEntry = inspected.Entry;
            var memberInspection = inspected.Inspection;
            var memberTarget = MemberTarget(memberInspection, target, out string targetReason);
            lock (_gate) _closures[memberEntry.Key] = packages;
            var memberPlan = ComputePlan(memberEntry, memberInspection, memberTarget);
            if (memberPlan.Decision == InstallDecision.UnknownBlocked)
            {
                ForgetClosureOf(members, root.Key);
                lock (_gate) _closures.Remove(memberEntry.Key);
                return InstallPlanSet.Refuse(InstallReasons.ClosureMemberRefused(member.Package, memberPlan.Reason));
            }

            lock (_gate) _plans[memberPlan.PlanHash] = memberPlan;
            members.Add(new InstallPlanMember(
                member.Package,
                string.IsNullOrWhiteSpace(memberEntry.Manifest.Version) ? member.Version : memberEntry.Manifest.Version,
                memberPlan,
                isRoot: false,
                PackageDependencyClosure.BySatisfiedClosure,
                member.VersionRule,
                targetReason));
        }

        lock (_gate) _closures[root.Key] = packages;
        var rootPlan = ComputePlan(root, rootInspection, target);
        if (rootPlan.Decision == InstallDecision.UnknownBlocked)
        {
            ForgetClosureOf(members, root.Key);
            return InstallPlanSet.Refuse(rootPlan.Reason);
        }

        lock (_gate) _plans[rootPlan.PlanHash] = rootPlan;
        members.Add(new InstallPlanMember(
            root.Key,
            string.IsNullOrWhiteSpace(root.Manifest.Version) ? "0" : root.Manifest.Version,
            rootPlan,
            isRoot: true,
            "root",
            PackageDependencyClosure.RuleListed,
            ""));

        SetStatus(new InstallProgress(
            InstallStatusKind.AwaitingApproval, rootPlan.Reason, entryKey: root.Key));
        return InstallPlanSet.Planned(rootPlan, members, closure.Members, target);
    }

    /// <summary>
    /// INDEX1: put the pinned listing of <paramref name="package"/> in place when <paramref name="root"/> is an Index listing
    /// that pins it. Null when done (or nothing is pinned); the refusal otherwise.
    /// </summary>
    internal string? PinDependencyOf(CatalogEntry root, string package, string? version = null)
    {
        // M3: every member of an Index root's closure is pinned: the root's own row, another native entry's row, or the
        // Index's own entry of that package; an unpinned member refuses the install (never Thunderstore's newest).
        if (!root.FromIndex) return null;
        CatalogEntry? thunderstore = null;
        IndexDocument? doc;
        string? stamp;
        lock (_gate)
        {
            for (int i = 0; i < _fetchedEntries.Count; i++)
                if (string.Equals(_fetchedEntries[i].Key, package, StringComparison.OrdinalIgnoreCase)) { thunderstore = _fetchedEntries[i]; break; }
            doc = _indexSource?.Current;
            stamp = _indexStamp;
        }

        string pages = _indexSource?.PagesRoot ?? ProgenitorIndex.PagesRoot;
        var dep = IndexCatalog.PinAnywhere(root, doc, package, version);
        CatalogEntry? pinned;
        string refusal = "";
        if (dep != null) pinned = IndexCatalog.PinDependency(dep, doc, thunderstore, stamp, out refusal, pages);
        else
        {
            // The Index's own entry of that package (the version the closure asks for, else its newest).
            var native = doc?.NativeForKey(package);
            IndexPackage? pick = null;
            if (native != null && doc != null)
                foreach (var row in IndexCatalog.ToEntries(doc, stamp, allVersions: true, pages))
                {
                    if (!string.Equals(row.Key, package, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrWhiteSpace(version) || string.Equals(row.Manifest.Version, version!.Trim(), StringComparison.Ordinal))
                    {
                        if (pick == null || UpdateVersions.IsNewer(row.Manifest.Version, pick.Version)) pick = row.Pin!.Package;
                    }
                }

            pinned = pick != null ? IndexCatalog.EntryOf(pick, doc, stamp, pages) : null;
            if (pinned == null) refusal = InstallReasons.UnpinnedDependency(package + (string.IsNullOrWhiteSpace(version) ? "" : " " + version));
        }

        if (pinned == null) return refusal.Length > 0 ? refusal : InstallReasons.PlanBlocked;
        UseVersionEntry(pinned);
        return null;
    }

    /// <summary>M1: Thunderstore rows minus the versions a takedown hides or disables for Thunderstore.</summary>
    private IReadOnlyList<CatalogEntry> WithoutTakedownsLocked(IReadOnlyList<CatalogEntry> thunderstore)
    {
        IndexDocument? doc;
        try { doc = _indexSource?.Current; }
        catch { doc = null; }
        if (doc == null || doc.Revoked.Count == 0 || thunderstore.Count == 0) return thunderstore;
        var kept = new List<CatalogEntry>(thunderstore.Count);
        foreach (var e in thunderstore)
            if (!IndexCatalog.TakesDownThunderstore(doc, e)) kept.Add(e);
        return kept;
    }

    /// <summary>A refused set leaves no closure behind, on the root or on any member it had planned.</summary>
    private void ForgetClosureOf(List<InstallPlanMember> members, string rootKey)
    {
        lock (_gate)
        {
            _closures.Remove(rootKey ?? "");
            for (int i = 0; i < members.Count; i++) _closures.Remove(members[i].Plan.Entry.Key);
        }
    }

    /// <summary>
    /// DEPS1: write one ordered install — the dependencies first, each by its own decision (pending or
    /// hot, HOT4 halves included), then the root. The first member that fails stops the set and the
    /// result names it; the members already written are real installs and stay on disk and in their
    /// root's ledger, so <see cref="ExecuteSetResult.Members"/> is what was written.
    /// </summary>
    internal ExecuteSetResult ExecuteWithDependencies(InstallPlanSet set)
    {
        if (set == null || !set.Ok || set.Root == null)
            return ExecuteSetResult.Refuse(set?.Error ?? InstallReasons.PlanBlocked);

        var written = new List<ExecuteMemberResult>(set.Members.Count);
        ExecuteSetResult result;
        string current = set.Root.Entry.Key;
        try
        {
            result = WriteMembers(set, written, ref current);
        }
        catch (Exception e)
        {
            // INSTALLFAIL1: a throw used to reach the pane's catch with no log line at all.
            result = ExecuteSetResult.Failed(current, InstallReasons.FailedWriteNothing + ": " + e.Message, written);
        }
        finally
        {
            // The set is over either way: a later plain plan of the root must refuse again for a
            // dependency this set did not manage to write.
            lock (_gate)
            {
                for (int i = 0; i < set.Members.Count; i++) _closures.Remove(set.Members[i].Plan.Entry.Key);
            }

            // INSTALLFAIL1: the downloaded zips are spent. They used to stay in staging/index/tmp as
            // `.part` files for the life of the folder, one per click.
            for (int i = 0; i < set.Members.Count; i++) ReleaseDownload(set.Members[i].Plan.Entry.Key);
        }

        LogSetResult(set, result);
        return result;
    }

    private ExecuteSetResult WriteMembers(InstallPlanSet set, List<ExecuteMemberResult> written, ref string current)
    {
        for (int i = 0; i < set.Members.Count; i++)
        {
            var member = set.Members[i];
            current = member.Package;
            var approved = Approve(member.Plan.PlanHash);
            if (!approved.Ok)
                return ExecuteSetResult.Failed(member.Package, approved.Error ?? InstallReasons.ApprovalMissing, written);

            var one = ExecuteToPending(member.Plan.PlanHash);
            if (!one.Ok)
                return ExecuteSetResult.Failed(member.Package, one.Error ?? InstallReasons.FailedWriteNothing, written);

            written.Add(new ExecuteMemberResult(
                member.Package, member.Version, one.PendingPath, one.HotLoaded, one.ServerPendingPath, member.IsRoot));
        }

        return ExecuteSetResult.Written(written);
    }

    /// <summary>INSTALLFAIL1: one log line per member written and one per failure, with its reason.</summary>
    private static void LogSetResult(InstallPlanSet set, ExecuteSetResult result)
    {
        for (int i = 0; i < result.Members.Count; i++)
        {
            var m = result.Members[i];
            string where = m.HotLoaded ? "loaded now from " : "loads at the next restart from ";
            InstallLog.Info("Install: wrote " + m.Package + " " + m.Version + " for "
                + InstallTargetNames.Half(set.Target) + "; " + where + (m.Path ?? "?")
                + (string.IsNullOrEmpty(m.ServerPendingPath) ? "" : " and " + m.ServerPendingPath) + ".");
        }

        if (!result.Ok)
            InstallLog.Warn("Install: " + set.Root!.Entry.Key + " was not installed: " + (result.Error ?? InstallReasons.FailedWriteNothing));
    }

    public InstallProgress Status
    {
        get { lock (_gate) return _status; }
    }

    public Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken) =>
        LoadIndexAsync(cancellationToken, force: false);

    /// <summary>TSTRAFFIC1: who checks the index through this catalog, for the request log line.</summary>
    internal string IndexCheckOwner { get; set; } = "catalog";

    /// <summary>
    /// TSTRAFFIC1: this catalog's conditional index check over <c>&lt;root&gt;/progenitor-cache</c>, built on first
    /// use. Each check reads the route then current (a local-index change swaps the transport).
    /// </summary>
    internal ConditionalIndexFetch IndexCheck
    {
        get
        {
            lock (_gate)
            {
                if (_indexCheck != null) return _indexCheck;
                // The index URL is fixed for a catalog (a server rebuilds its catalog when the base URL changes);
                // the copy on disk is this URL's own.
                var cache = new IndexDiskCache(_bepInExRoot, _route.Options.IndexUrl);
                return _indexCheck = new ConditionalIndexFetch(
                    (validators, ct) =>
                    {
                        var route = _route;
                        var fetcher = new CatalogFetcher(route.Transport, route.Options, _clock);
                        return fetcher.FetchIndexTextAsync(cache.Url, CatalogFetcher.IndexMaxBytes, validators, ct);
                    },
                    cache,
                    IndexCheckOwner,
                    _clock);
            }
        }
    }

    private ConditionalIndexFetch? _indexCheck;

    /// <summary>
    /// Load the index from Thunderstore. TSTRAFFIC1: a conditional, compressed check through
    /// <see cref="IndexCheck"/>: a 304 reuses this root's cached copy. <paramref name="force"/> (an admin's
    /// install) goes out even while automatic checks are paused after repeated failures.
    /// </summary>
    public async Task<CatalogLoadResult> LoadIndexAsync(CancellationToken cancellationToken, bool force)
    {
        SetStatus(new InstallProgress(InstallStatusKind.LoadingIndex, "loading Thunderstore index"));
        string stamp = CatalogFetcher.FormatStamp(_clock.UtcNow);
        CatalogTextFetchResult fetched;
        try
        {
            // INDEX1: the Progenitor Index first (its own conditional check on this catalog's cadence), so the merge below
            // has it; a failed check keeps the last good list.
            await RefreshIndexAsync(cancellationToken, force).ConfigureAwait(false);
            fetched = await IndexCheck.FetchAsync(cancellationToken, force).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetStatus(new InstallProgress(InstallStatusKind.Cancelled, InstallReasons.FetchCancelled));
            return new CatalogLoadResult(false, InstallReasons.FetchCancelled, 0, null);
        }

        if (!fetched.Ok || fetched.Text == null)
        {
            SetStatus(new InstallProgress(InstallStatusKind.Failed, fetched.Error ?? InstallReasons.FetchTransport));
            return new CatalogLoadResult(false, fetched.Error ?? InstallReasons.FetchTransport, 0, stamp);
        }

        if (!ThunderstoreAdapter.TryLoadIndex(fetched.Text, stamp, out var entries, out var error))
        {
            SetStatus(new InstallProgress(InstallStatusKind.Failed, error));
            return new CatalogLoadResult(false, error, 0, stamp);
        }

        int count = AdoptEntries(entries, stamp);
        lock (_gate) _status = new InstallProgress(InstallStatusKind.Idle, "index loaded", entryKey: null);
        return new CatalogLoadResult(true, null, count, stamp);
    }

    /// <summary>Load from a JSON string (pre-fetched export or test fixture). No network.</summary>
    public CatalogLoadResult LoadIndexFromJson(string json, string? indexStamp)
    {
        if (!ThunderstoreAdapter.TryLoadIndex(json, indexStamp, out var entries, out var error))
        {
            SetStatus(new InstallProgress(InstallStatusKind.Failed, error));
            return new CatalogLoadResult(false, error, 0, indexStamp);
        }

        int count = AdoptEntries(entries, indexStamp);
        lock (_gate) _status = InstallProgress.Idle;
        return new CatalogLoadResult(true, null, count, indexStamp);
    }

    /// <summary>
    /// HOT1: adopt one loaded index. The configured local index (if any) is read and its entries
    /// are merged in front of the fetched ones, so a dummy listing is present however the index
    /// arrived — cache, fetch or fixture JSON. The merge never fails the load: a missing or
    /// malformed local index is recorded in <see cref="LocalIndexError"/> and the fetched entries
    /// stand on their own.
    /// </summary>
    private int AdoptEntries(IReadOnlyList<CatalogEntry> fetched, string? indexStamp)
    {
        lock (_gate)
        {
            _fetchedEntries = fetched;
            _indexStamp = indexStamp;
            // LOCALRELOAD1: every index load (the Install tab's reload too) follows a changed [Install] LocalIndexPath.
            FollowConfiguredLocalIndexLocked();
            // INDEX1: and takes the Progenitor Index list the source holds now (its verified cache on the first load).
            FollowIndexLocked();
            return MergeLocked();
        }
    }

    /// <summary>
    /// F1 (live 2026-10-07: a fresh Core's Mods page called a join-installed HotDemo "not installed by Progenitor"):
    /// a plugin-to-listing lookup must not wait for the Install tab. The configured local index is merged now when it has
    /// not been (it is a local file); the fetched index is served from its on-disk cache by the caller.
    /// </summary>
    internal void EnsureLookupReady()
    {
        lock (_gate)
        {
            FollowConfiguredLocalIndexLocked();
            if (_localIndex.Configured && _localKeys.Count == 0) MergeLocked();
        }
    }

    /// <summary>
    /// F27 (live 2026-10-07: a mod whose listing left the index could not be cancelled or uninstalled; Yes did nothing and
    /// logged nothing): act on install-ledger rows alone, by their <c>package:</c> source and root. A pending row (written,
    /// never loaded) is deleted now; a loaded copy is marked for removal at its next start (or unmarked). Every row logs
    /// what it did.
    /// </summary>
    internal ServerInstallResult ActOnLedgerRows(IReadOnlyList<InstalledLedgerRow> rows, LedgerAct act)
    {
        var removed = new List<string>();
        var skipped = new List<string>();
        var messages = new List<string>();
        bool any = false, ok = true;
        foreach (var row in rows ?? Array.Empty<InstalledLedgerRow>())
        {
            if (row.Source.Length == 0) continue;
            bool server = string.Equals(row.Root, InstalledState.ServerRoot, StringComparison.OrdinalIgnoreCase);
            string? root = server ? _serverBepInExRoot : _bepInExRoot;
            if (string.IsNullOrEmpty(root)) continue;
            bool pending = string.Equals(row.Category, ServerInstallLedger.CategoryPending, StringComparison.OrdinalIgnoreCase);
            if (act == LedgerAct.CancelInstall && !pending) continue;
            any = true;
            ServerInstallResult one;
            string verb;
            try
            {
                (one, verb) = act switch
                {
                    LedgerAct.Unmark => (UninstallMarkers.Cancel(root, row.Source), "unmarked"),
                    // M4: a pending row is cancelled alone; a loaded copy of the package stays.
                    _ when pending => (ServerInstall.RemovePendingOnly(root, row.Source), "cancelled"),
                    _ => (UninstallMarkers.Mark(root, row.Source), "marked for removal"),
                };
            }
            catch (Exception e) { (one, verb) = (ServerInstallResult.Refuse(e.Message), "acted on"); }
            LogRemoval(verb, row.Source + " (from its ledger row)", server ? InstallTarget.LocalServer : InstallTarget.Client, one);
            ok &= one.Ok;
            removed.AddRange(one.Removed);
            skipped.AddRange(one.Skipped);
            if (!one.Ok && one.Message.Length > 0) messages.Add(one.Message);
        }

        if (!any)
        {
            var none = new ServerInstallResult(true, "nothing to change: the install record has no matching copy", removed, skipped, noOp: true);
            InstallLog.Info("Install: ledger-only " + act + ": " + none.Message + ".");
            return none;
        }

        return new ServerInstallResult(ok, string.Join(" ", messages), removed, skipped, noOp: ok && removed.Count == 0);
    }

    /// <summary>F27: does any of these ledger rows carry a pending removal on its root?</summary>
    internal bool LedgerRowsPending(IReadOnlyList<InstalledLedgerRow> rows)
    {
        foreach (var row in rows ?? Array.Empty<InstalledLedgerRow>())
        {
            string? root = string.Equals(row.Root, InstalledState.ServerRoot, StringComparison.OrdinalIgnoreCase) ? _serverBepInExRoot : _bepInExRoot;
            try { if (!string.IsNullOrEmpty(root) && UninstallMarkers.IsPending(root, row.Source)) return true; }
            catch { /* not evidence */ }
        }

        return false;
    }

    /// <summary>Re-read the local index and merge it into the entries on show. Returns the merged count.</summary>
    internal int ReloadLocalIndex()
    {
        lock (_gate)
        {
            // LOCALRELOAD1 (live 2026-10-07: [Install] LocalIndexPath set from empty, the cfg reloaded, and a reload still
            // merged nothing until a Core reload): the configured folder is read again, so a set, changed or cleared key
            // takes effect here; a cleared one drops the local entries.
            FollowConfiguredLocalIndexLocked();
            return MergeLocked();
        }
    }

    /// <summary>LOCALRELOAD1: follow the configured <c>[Install] LocalIndexPath</c> (a caller-named folder stays).</summary>
    private void FollowConfiguredLocalIndexLocked()
    {
        if (_localIndexExplicit) return;
        string? now;
        try { now = LocalIndexConfig.Resolve(); }
        catch { return; }
        UseLocalIndexLocked(now);
    }

    /// <summary>
    /// LOCALRELOAD1: merge from <paramref name="folder"/> from now on (null or empty: none), with the file route and the
    /// file-URL rule that a configured folder needs, exactly as the constructor sets them. Unchanged: nothing happens.
    /// </summary>
    private void UseLocalIndexLocked(string? folder)
    {
        var next = new LocalIndexSource(folder);
        if (string.Equals(next.Folder, _localIndex.Folder, StringComparison.OrdinalIgnoreCase)) return;
        _localIndex = next;
        var o = _route.Options;
        var options = new CatalogFetchOptions
        {
            IndexUrl = o.IndexUrl,
            MaxRedirects = o.MaxRedirects,
            MaxRetries = o.MaxRetries,
            MaxBytes = o.MaxBytes,
            Timeout = o.Timeout,
            Backoff = o.Backoff,
            UserAgent = o.UserAgent,
            AllowFileUrls = next.Configured,
        };
        _route = new FetchRoute(next.Configured ? new RoutingCatalogTransport(_baseTransport, next.Folder) : _baseTransport, options);
        InstallLog.Info("Install: [Install] LocalIndexPath is now " + (next.Folder ?? "(empty)") + "; the local index is read from there.");
    }

    private int MergeLocked()
    {
        IReadOnlyList<CatalogEntry> local = Array.Empty<CatalogEntry>();
        _localIndexError = null;
        _localKeys.Clear();
        if (_localIndex.Configured)
        {
            var load = _localIndex.Load();
            if (load.Ok) local = load.Entries;
            else _localIndexError = load.Error;
            for (int i = 0; i < local.Count; i++)
                if (!string.IsNullOrEmpty(local[i].Key)) _localKeys.Add(local[i].Key);
        }

        // INDEX1: local test listings, then the Progenitor Index, then Thunderstore; a mod in the Index and on Thunderstore
        // is its Index row.
        var merged = IndexCatalog.Merge(local, _indexEntries, ThunderstoreOn() ? WithoutTakedownsLocked(_fetchedEntries) : Array.Empty<CatalogEntry>(), out _localShadowed, out _indexShadowed);
        _entries = merged;
        _byKey.Clear();
        for (int i = 0; i < merged.Count; i++)
        {
            var e = merged[i];
            if (string.IsNullOrEmpty(e.Key)) continue;
            _byKey[e.Key] = e;
        }

        // INSTALLFAIL1 follow-up: a listing this catalog has not inspected yet takes the facts an
        // earlier inspection of the same version proved (progenitor-cache/verified), so a Core swap
        // or a restart does not turn a manifest-verified `side: both` back into the listing's guess.
        var verified = _verifiedCache.ReadAll();
        if (verified.Count > 0)
        {
            for (int i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                if (string.IsNullOrEmpty(e.Key) || _inspections.ContainsKey(e.Key)) continue;
                if (e.Provenance == CatalogProvenance.ManifestVerified) continue;
                if (string.IsNullOrEmpty(e.Owner) || string.IsNullOrEmpty(e.PackageName) || string.IsNullOrEmpty(e.Manifest.Version)) continue;
                if (!verified.TryGetValue(VerifiedManifestCache.Key(e.Owner!, e.PackageName!, e.Manifest.Version!), out var record)) continue;
                var rested = e.WithInspection(CatalogProvenance.ManifestVerified, MergeManifest(e, record.Manifest), record.Side);
                _byKey[e.Key] = rested;
                ReplaceEntry(rested);
            }
        }

        // A re-merge must not forget what an inspection already learned about a listing (the
        // verified manifest, the side, the plugin dependencies): re-apply every inspection this
        // session made over the freshly merged rows.
        List<string>? stale = null;
        foreach (var kv in _inspections)
        {
            if (!_byKey.TryGetValue(kv.Key, out var known)) continue;
            if (_inspectedListing.TryGetValue(kv.Key, out var listed)
                && !string.Equals(listed, ListingTag(known), StringComparison.Ordinal))
            {
                (stale ??= new List<string>()).Add(kv.Key);
                continue;
            }

            var updated = ApplyInspection(known, kv.Value);
            _byKey[kv.Key] = updated;
            ReplaceEntry(updated);
        }

        if (stale != null)
            foreach (var key in stale)
            {
                // The index lists another version now: that inspection (and its download) describe the old one.
                _inspections.Remove(key);
                _inspectedListing.Remove(key);
                if (_zipPaths.TryGetValue(key, out var zip))
                {
                    _zipPaths.Remove(key);
                    try { if (File.Exists(zip)) File.Delete(zip); }
                    catch { /* the staging sweep removes it */ }
                }
            }

        return _entries.Count;
    }

    /// <summary>The local index folder this catalog merges from; null when the key is empty.</summary>
    internal string? LocalIndexPath => _localIndex.Folder;

    /// <summary>Why the configured local index contributed nothing, or null when it loaded (or is not configured).</summary>
    internal string? LocalIndexError
    {
        get { lock (_gate) return _localIndexError; }
    }

    /// <summary>The keys the local index contributed, in merge order.</summary>
    internal IReadOnlyList<string> LocalIndexKeys
    {
        get { lock (_gate) return new List<string>(_localKeys); }
    }

    /// <summary>How many fetched listings a local entry shadowed on the last merge.</summary>
    internal int LocalIndexShadowed
    {
        get { lock (_gate) return _localShadowed; }
    }

    /// <summary>True when this key came from the local index rather than the fetched one.</summary>
    internal bool IsLocalKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        lock (_gate) return _localKeys.Contains(key!);
    }

    public IReadOnlyList<CatalogEntry> Search(string? query)
    {
        CatalogEntry[] snapshot;
        lock (_gate) snapshot = ToArray(_entries);
        if (string.IsNullOrWhiteSpace(query)) return snapshot;

        string q = query.Trim();
        var hits = new List<CatalogEntry>();
        for (int i = 0; i < snapshot.Length; i++)
        {
            var e = snapshot[i];
            if (Contains(e.SearchLabel, q)
                || Contains(e.Manifest.Id, q)
                || Contains(e.Author, q)
                || Contains(e.Description, q)
                || Contains(e.Manifest.Package, q)
                || Contains(e.Key, q))
                hits.Add(e);
        }
        return hits;
    }

    public bool TryGetEntry(string key, out CatalogEntry? entry)
    {
        lock (_gate) return _byKey.TryGetValue(key ?? "", out entry);
    }

    /// <summary>
    /// JOINFLOW2: put the entry of one exact version of a listing in place of the one the index load chose (the
    /// newest), so the download, inspection, plan and write that follow are of that version. Forgets what this
    /// catalog inspected of the listing before. Meant for a catalog built for one join batch: the Install tab's
    /// own catalog never calls it. A later index merge restores the newest version.
    /// </summary>
    internal bool UseVersionEntry(CatalogEntry entry)
    {
        if (entry == null || string.IsNullOrEmpty(entry.Key)) return false;
        string? spent = null;
        lock (_gate)
        {
            if (_byKey.TryGetValue(entry.Key, out var current)
                && string.Equals(ListingTag(current), ListingTag(entry), StringComparison.Ordinal)
                && _inspections.ContainsKey(entry.Key)) return true;
            _byKey[entry.Key] = entry;
            ReplaceEntry(entry);
            _inspections.Remove(entry.Key);
            if (_zipPaths.TryGetValue(entry.Key, out var zip))
            {
                _zipPaths.Remove(entry.Key);
                spent = zip;
            }
        }

        DeletePart(spent);
        return true;
    }

    /// <summary>JOINFLOW2: the index URL this catalog fetches (the join screens read the same index).</summary>
    internal string IndexUrl => _route.Options.IndexUrl;

    public async Task<InspectCallResult> DownloadAndInspectAsync(string key, CancellationToken cancellationToken)
    {
        if (!TryGetEntry(key, out var entry) || entry == null)
            return new InspectCallResult(false, InstallReasons.UnknownEntry, null, null);

        if (string.IsNullOrEmpty(entry.DownloadUrl))
            return new InspectCallResult(false, InstallReasons.FetchNoUrl, entry, null);

        // FIX-NONMODS: a listing whose published package is over the download ceiling is refused
        // before any request (live: ebkr-r2modman, 363 MB, ran to the 32 MiB ceiling first). The
        // fetcher's own ceiling stays as the backstop for a listing that under-reports its size.
        if (NonModPackages.TooLarge(entry.ListingFileSize, _route.Options.MaxBytes))
        {
            string reason = InstallReasons.FetchSizeCeiling + " (" + NonModPackages.TooLargeText(entry.ListingFileSize!.Value, _route.Options.MaxBytes) + ")";
            InstallLog.Warn("Install: " + entry.Key + " refused before download: " + reason);
            SetStatus(new InstallProgress(InstallStatusKind.Failed, reason, entryKey: entry.Key));
            return new InspectCallResult(false, reason, entry, null);
        }

        // A mod manager application is not a game mod; it is never downloaded.
        if (NonModPackages.IsModManager(entry.Owner, entry.PackageName, entry.Key))
        {
            InstallLog.Warn("Install: " + entry.Key + " refused: " + NonModPackages.ModManagerText);
            SetStatus(new InstallProgress(InstallStatusKind.Failed, NonModPackages.ModManagerText, entryKey: entry.Key));
            return new InspectCallResult(false, NonModPackages.ModManagerText, entry, null);
        }

        // INDEX1: a pinned listing (an Index entry, or a dependency it pins) downloads only the URL the signed list names,
        // from that source's own host, with redirects only to that source's download hosts.
        var pin = entry.Pin;
        string? pinRefusal = PinRefusal(entry);
        if (pinRefusal != null)
        {
            InstallLog.Warn("Install: " + entry.Key + " refused before download: " + pinRefusal);
            SetStatus(new InstallProgress(InstallStatusKind.Failed, pinRefusal, entryKey: entry.Key));
            return new InspectCallResult(false, pinRefusal, entry, null);
        }

        SetStatus(new InstallProgress(InstallStatusKind.Fetching, "downloading " + entry.Key, entryKey: entry.Key));
        var route = _route;
        var fetcher = new CatalogFetcher(route.Transport, pin == null ? route.Options : PinnedOptions(route.Options, pin), _clock);
        string fileName = (entry.PackageName ?? "package") + "-" + (entry.Manifest.Version ?? "0") + ".zip";
        CatalogFetchResult fetched;
        try
        {
            fetched = await fetcher.FetchDownloadAsync(entry.DownloadUrl, _bepInExRoot, fileName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SetStatus(new InstallProgress(InstallStatusKind.Cancelled, InstallReasons.FetchCancelled, entryKey: entry.Key));
            return new InspectCallResult(false, InstallReasons.FetchCancelled, entry, null);
        }

        if (!fetched.Ok)
        {
            InstallLog.Warn("Install: download of " + entry.Key + " failed: " + (fetched.Error ?? InstallReasons.FetchTransport));
            SetStatus(new InstallProgress(InstallStatusKind.Failed, fetched.Error ?? InstallReasons.FetchTransport, entryKey: entry.Key));
            return new InspectCallResult(false, fetched.Error, entry, null);
        }

        // INDEX1: the size and sha256 the signed list names, checked before the bytes are opened or anything is written
        // outside the download buffer. A mismatch is never used again: the buffer goes.
        if (pin != null)
        {
            string mismatch = ProgenitorIndex.CheckDownload(pin.Size, pin.Sha256, fetched.ByteCount, fetched.Sha256);
            if (mismatch.Length > 0)
            {
                DeletePart(fetched.Path);
                InstallLog.Warn("Install: download of " + entry.Key + " refused: " + mismatch + ".");
                SetStatus(new InstallProgress(InstallStatusKind.Failed, mismatch, entryKey: entry.Key));
                return new InspectCallResult(false, mismatch, entry, null);
            }
        }

        SetStatus(new InstallProgress(InstallStatusKind.Inspecting, "inspecting " + entry.Key, entryKey: entry.Key));
        var inspection = PackageInspector.InspectFile(fetched.Path!, fetched.Sha256);
        if (!inspection.Ok)
        {
            // REMOTEINST1: bytes that failed inspection are never used again; nothing keeps them.
            try { if (File.Exists(fetched.Path!)) File.Delete(fetched.Path!); }
            catch { /* best effort */ }
            InstallLog.Warn("Install: " + entry.Key + " failed inspection: " + (inspection.Refusal ?? InstallReasons.InspectNotZip));
            SetStatus(new InstallProgress(InstallStatusKind.Failed, inspection.Refusal ?? InstallReasons.InspectNotZip, entryKey: entry.Key));
            return new InspectCallResult(false, inspection.Refusal, entry, inspection);
        }

        var updated = ApplyInspection(entry, inspection);
        CacheIdentity(updated, inspection);
        _verifiedCache.Write(entry, inspection);
        string? replaced = null;
        lock (_gate)
        {
            _byKey[updated.Key] = updated;
            ReplaceEntry(updated);
            _inspections[updated.Key] = inspection;
            _inspectedListing[updated.Key] = ListingTag(entry);
            // INSTALLFAIL1: one download per listing is kept; the one this replaces is spent.
            if (_zipPaths.TryGetValue(updated.Key, out var previous)
                && !string.Equals(previous, fetched.Path, StringComparison.OrdinalIgnoreCase))
                replaced = previous;
            _zipPaths[updated.Key] = fetched.Path!;
            _status = new InstallProgress(InstallStatusKind.Idle, "inspected " + updated.Key, entryKey: updated.Key);
        }

        DeletePart(replaced);
        return new InspectCallResult(true, null, updated, inspection);
    }

    /// <summary>REMOTEINST1: the downloaded (or local) zip one listing was inspected from, or null.</summary>
    internal string? ZipPathOf(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        lock (_gate) return _zipPaths.TryGetValue(key!, out var path) ? path : null;
    }

    /// <summary>
    /// REMOTEINST1: end of one remote operation. Every downloaded package this catalog staged as a
    /// <c>.part</c> file is deleted, and the inspections, plans and approvals built from them are
    /// forgotten, so a failed attempt leaves nothing behind and the next one starts clean. A zip this
    /// catalog did not download (a local file it was pointed at) is never deleted.
    /// </summary>
    internal void DiscardDownloads()
    {
        List<string> paths;
        lock (_gate)
        {
            paths = new List<string>(_zipPaths.Values);
            _zipPaths.Clear();
            _inspections.Clear();
            _plans.Clear();
            _approved.Clear();
            _closures.Clear();
        }

        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i];
            if (!path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best effort; the staging tmp folder belongs to Progenitor */ }
        }
    }

    /// <summary>
    /// INSTALLSIDES2 item 7: delete the abandoned downloads in this root's staging <c>tmp</c> folder —
    /// <c>.part</c> files older than <paramref name="maxAge"/> that this catalog does not hold (see
    /// <see cref="StagingTempSweep"/>). Called once when the Mods menu builds its catalog, off the main
    /// thread. Never throws.
    /// </summary>
    internal StagingTempSweep.Result SweepAbandonedDownloads(DateTime nowUtc, TimeSpan? maxAge = null)
    {
        List<string> held;
        lock (_gate) held = new List<string>(_zipPaths.Values);
        string dir;
        try { dir = CatalogFetcher.TempDirectory(_bepInExRoot); }
        catch { return new StagingTempSweep.Result(); }
        return StagingTempSweep.Sweep(dir, nowUtc, maxAge ?? StagingTempSweep.DefaultMaxAge, held);
    }

    /// <summary>The staging folder <see cref="SweepAbandonedDownloads"/> cleans, for its log line.</summary>
    internal string StagingTempDirectory => CatalogFetcher.TempDirectory(_bepInExRoot);

    /// <summary>Inspect a zip already on disk (local file / test fixture). No network.</summary>
    public InspectCallResult InspectLocalZip(string key, string zipPath)
    {
        if (!TryGetEntry(key, out var entry) || entry == null)
            return new InspectCallResult(false, InstallReasons.UnknownEntry, null, null);

        SetStatus(new InstallProgress(InstallStatusKind.Inspecting, "inspecting " + entry.Key, entryKey: entry.Key));
        var inspection = PackageInspector.InspectFile(zipPath);
        if (!inspection.Ok)
        {
            SetStatus(new InstallProgress(InstallStatusKind.Failed, inspection.Refusal ?? InstallReasons.InspectNotZip, entryKey: entry.Key));
            return new InspectCallResult(false, inspection.Refusal, entry, inspection);
        }

        var updated = ApplyInspection(entry, inspection);
        CacheIdentity(updated, inspection);
        _verifiedCache.Write(entry, inspection);
        lock (_gate)
        {
            _byKey[updated.Key] = updated;
            ReplaceEntry(updated);
            _inspections[updated.Key] = inspection;
            _inspectedListing[updated.Key] = ListingTag(entry);
            _zipPaths[updated.Key] = zipPath;
            _status = new InstallProgress(InstallStatusKind.Idle, "inspected " + updated.Key, entryKey: updated.Key);
        }

        return new InspectCallResult(true, null, updated, inspection);
    }

    public PlanCallResult Plan(string key, InstallTarget target)
    {
        if (!TryGetEntry(key, out var entry) || entry == null)
            return new PlanCallResult(false, InstallReasons.UnknownEntry, null);

        PackageInspection inspection;
        lock (_gate)
        {
            if (!_inspections.TryGetValue(key, out inspection!))
                return new PlanCallResult(false, InstallReasons.NotInspected, null);
        }

        SetStatus(new InstallProgress(InstallStatusKind.Planning, "planning " + key, entryKey: key));
        var plan = ComputePlan(entry, inspection, target);
        lock (_gate)
        {
            _plans[plan.PlanHash] = plan;
            _status = new InstallProgress(
                plan.CanExecuteToPending ? InstallStatusKind.AwaitingApproval : InstallStatusKind.Failed,
                plan.Reason,
                entryKey: key);
        }
        return new PlanCallResult(true, null, plan);
    }

    public ApproveCallResult Approve(string planHash)
    {
        if (string.IsNullOrWhiteSpace(planHash))
            return new ApproveCallResult(false, InstallReasons.ApprovalMissing, null, false);

        InstallPlan plan;
        lock (_gate)
        {
            if (!_plans.TryGetValue(planHash, out plan!))
                return new ApproveCallResult(false, InstallReasons.ApprovalMissing, planHash, false);
        }

        if (plan.Decision == InstallDecision.UnknownBlocked)
            return new ApproveCallResult(false, plan.Reason, planHash, false);

        if (!TryGetEntry(plan.Entry.Key, out var entry) || entry == null)
            return new ApproveCallResult(false, InstallReasons.UnknownEntry, planHash, false);

        PackageInspection inspection;
        lock (_gate)
        {
            if (!_inspections.TryGetValue(plan.Entry.Key, out inspection!))
                return new ApproveCallResult(false, InstallReasons.NotInspected, planHash, false);
        }

        var current = ComputePlan(entry, inspection, plan.Target);
        if (!string.Equals(current.PlanHash, planHash, StringComparison.OrdinalIgnoreCase))
            return new ApproveCallResult(false, InstallReasons.ApprovalInvalid, planHash, false);

        bool already;
        lock (_gate)
        {
            already = !_approved.Add(planHash);
            _status = new InstallProgress(InstallStatusKind.AwaitingApproval, "approved", entryKey: plan.Entry.Key);
        }
        return new ApproveCallResult(true, null, planHash, already);
    }

    public ExecuteCallResult ExecuteToPending(string planHash)
    {
        InstallPlan plan;
        bool approved;
        string zipPath;
        PackageInspection inspection;
        lock (_gate)
        {
            if (string.IsNullOrEmpty(planHash) || !_plans.TryGetValue(planHash, out plan!))
                return new ExecuteCallResult(false, InstallReasons.ApprovalMissing, null, false);
            approved = _approved.Contains(planHash);
            if (!_zipPaths.TryGetValue(plan.Entry.Key, out zipPath!)
                || !_inspections.TryGetValue(plan.Entry.Key, out inspection!))
                return new ExecuteCallResult(false, InstallReasons.NotInspected, null, false);
        }

        if (!approved)
            return new ExecuteCallResult(false, InstallReasons.ApprovalMissing, null, false);

        if (!TryGetEntry(plan.Entry.Key, out var entry) || entry == null)
            return new ExecuteCallResult(false, InstallReasons.UnknownEntry, null, false);

        var current = ComputePlan(entry, inspection, plan.Target);
        if (!string.Equals(current.PlanHash, planHash, StringComparison.OrdinalIgnoreCase))
            return new ExecuteCallResult(false, InstallReasons.ApprovalInvalid, null, false);

        SetStatus(new InstallProgress(InstallStatusKind.Staging, "staging " + plan.Entry.Key, entryKey: plan.Entry.Key));

        // INSTORD1: the plan's target names the roots. Both writes the client root and the
        // validated server root; the first failure is the result and the earlier write stays
        // ledgered so Remove can take it back.
        var roots = RootsFor(current.Target);
        ExecuteCallResult result = new(false, InstallReasons.NoConfiguredLocalServer, null, false);
        bool hotPlan = HotLoadExecutor.IsHotLoadPlan(current);
        string? serverPending = null;
        for (int i = 0; i < roots.Count; i++)
        {
            // HOT1: the root this process runs takes the hot-load executor when the plan was
            // admitted for it; any other root (a local server that is a different process) keeps
            // the restart route, and its pending path rides along in the readback.
            bool thisProcess = string.Equals(roots[i], _bepInExRoot, StringComparison.OrdinalIgnoreCase);
            // HOT4: and only when this root's own half was admitted for apply-now. A Both plan whose
            // client half is ApplyNow and whose server half is a restart install hot loads the one
            // root this process runs and writes the other root's pending file.
            InstallTarget halfTarget = thisProcess
                ? (_thisProcessIsServer ? InstallTarget.LocalServer : InstallTarget.Client)
                : InstallTarget.LocalServer;
            var one = hotPlan && thisProcess && HotLoadExecutor.HalfApplies(current, halfTarget)
                // HOT2: the executor rechecks the loader state through this delegate immediately
                // before its write, under the same call that performs the change (handoff item 4).
                ? HotLoadExecutor.ExecuteToReload(
                    roots[i], current, inspection, zipPath,
                    () => _admission?.Recapture(current.Entry.Manifest.Id ?? "", current.Target),
                    // The baseline is the APPROVED plan's loader state, not the recomputed one: the
                    // plan hash does not cover the runtime generation, so a Core swap between approval
                    // and write leaves `current` describing the new state and comparing it against a
                    // fresh capture would compare the new state with itself.
                    LoaderStateOf(plan))
                // HOTDEMO1: the local server's half, written into its watched reload folder so its
                // own reloader loads it within a second (no recheck: its loader is not observable).
                : !thisProcess && hotPlan && ServerHalfHot(current)
                    ? HotLoadExecutor.ExecuteToReload(roots[i], current, inspection, zipPath, crossProcess: true)
                    : InstallStaging.ExecuteToPending(roots[i], current, inspection, zipPath, ownRoot: thisProcess);
            if (!thisProcess && one.Ok && one.HotLoaded)
                InstallLog.Info("Install: wrote " + current.Entry.Key + " into the local server's reload folder; its reloader loads it now from " + one.PendingPath + ".");
            if (i == 0) result = one;
            else if (one.Ok && !one.HotLoaded) serverPending = one.PendingPath;
            if (!one.Ok)
            {
                result = one;
                break;
            }
        }

        if (result.Ok && serverPending != null)
        {
            result = new ExecuteCallResult(
                true, null, result.PendingPath, result.AlreadyPresent, result.HotLoaded, result.Sha256, serverPending,
                result.Rechecked);
        }

        // INSTORD2: a write is also a reconcile point — a second install of a package whose earlier
        // copy was promoted must not leave the old pending row naming a file that is gone.
        ReconcileLedgers();

        SetStatus(result.Ok
            ? new InstallProgress(
                InstallStatusKind.Pending,
                result.HotLoaded
                    ? (result.AlreadyPresent ? "already in progenitor-reload" : "written to progenitor-reload; the reloader loads it within a second")
                    : (result.AlreadyPresent ? "already pending" : "written to pending"),
                entryKey: plan.Entry.Key)
            : new InstallProgress(InstallStatusKind.Failed, result.Error ?? InstallReasons.FailedWriteNothing, entryKey: plan.Entry.Key));
        return result;
    }

    /// <summary>
    /// AUTOUPDATE2: the approved plugin image of a planned update, for the version store to place. The set must be the
    /// root alone (no new package to write) with one DLL (no companions); the approval is recomputed exactly as a write
    /// recomputes it, the zip is re-hashed against its inspection and the extracted image against the installable hash,
    /// so nothing changed since the plan is accepted. <paramref name="hotImage"/>: admission found the image hot-loadable
    /// and refused the plain install only because the old copy is loaded (the store's swap then decides the route).
    /// Null bytes with <paramref name="error"/> empty: this update is not the store's (it takes the install route).
    /// </summary>
    internal byte[]? ApprovedImage(InstallPlanSet set, out string sha256, out bool hotImage, out string notHotReason, out string error)
    {
        sha256 = "";
        hotImage = false;
        notHotReason = "";
        error = "";
        var plan = set?.Root;
        if (set == null || !set.Ok || plan == null) { error = set?.Error ?? InstallReasons.PlanBlocked; return null; }
        if (set.Members.Count != 1) return null;
        if (plan.Ordinary != null && plan.Ordinary.Companions.Count > 0) return null;
        if (string.IsNullOrEmpty(plan.PluginId) || string.IsNullOrEmpty(plan.PluginFileName)) return null;
        if (plan.Decision == InstallDecision.UnknownBlocked) { error = plan.Reason; return null; }

        var approved = Approve(plan.PlanHash);
        if (!approved.Ok) { error = approved.Error ?? InstallReasons.ApprovalMissing; return null; }

        string zipPath;
        PackageInspection inspection;
        lock (_gate)
        {
            if (!_zipPaths.TryGetValue(plan.Entry.Key, out zipPath!) || !_inspections.TryGetValue(plan.Entry.Key, out inspection!))
            {
                error = InstallReasons.NotInspected;
                return null;
            }
        }

        if (!File.Exists(zipPath) || !string.Equals(CatalogFetcher.HashFile(zipPath), inspection.ZipSha256, StringComparison.OrdinalIgnoreCase))
        {
            error = InstallReasons.ChangedArtifact;
            return null;
        }

        string? want = InstallStaging.InstallableDllHash(plan, inspection);
        if (string.IsNullOrEmpty(want)) { error = InstallReasons.NotInstallableNoHash; return null; }
        byte[]? bytes;
        try { bytes = InstallStaging.ExtractInstallableDll(zipPath, plan, inspection); }
        catch (Exception e) { error = InstallReasons.PreflightFailed + ": " + e.Message; return null; }
        if (bytes == null) { error = InstallReasons.NotInstallableNoHash; return null; }
        string got;
        using (var sha = System.Security.Cryptography.SHA256.Create()) got = CatalogFetcher.ToHex(sha.ComputeHash(bytes));
        if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase)) { error = InstallReasons.ChangedArtifact; return null; }

        sha256 = got;
        var admission = plan.Admission;
        hotImage = plan.Decision == InstallDecision.ApplyNow
            || (admission != null && admission.Reasons.Count > 0 && admission.Reasons[0].Code == AdmissionReasonCode.AlreadyLoaded);
        if (!hotImage) notHotReason = admission?.PrimaryText is { Length: > 0 } t ? t : plan.Reason;
        return bytes;
    }

    /// <summary>
    /// HOT2: the loader state one plan's admission was decided against, or null when it carried none.
    /// </summary>
    private static string? LoaderStateOf(InstallPlan? plan) =>
        plan?.Admission != null
        && plan.Admission.Facts.TryGetValue(Admission.BootstrapAdmissionSnapshotSource.LoaderStateFact, out string? key)
            ? key
            : null;

    public async Task<ListingDocumentSet> LoadListingDocumentsAsync(string key, CancellationToken cancellationToken)
    {
        if (!TryGetEntry(key, out var entry) || entry == null)
            return ListingDocumentSet.Unavailable(key ?? "", InstallReasons.UnknownEntry);

        string ns = entry.Owner ?? "";
        string name = entry.PackageName ?? "";
        string version = entry.Manifest.Version ?? "";
        if (string.IsNullOrWhiteSpace(ns) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
            return ListingDocumentSet.Unavailable(entry.Key, InstallReasons.DocumentIdentityMissing);

        string cacheKey = ThunderstoreListingDocuments.CacheKey(ns, name, version);
        // HOT1: a local listing has no Thunderstore document route. F11: its README and changelog are
        // its own zip's (the files beside index.json only for a one-listing index), or an honest
        // "unavailable". Asked before the fetched cache, which never holds a local package's text.
        if (IsLocalKey(entry.Key))
            return _localIndex.Documents(entry);

        // INDEX1: an Index listing's README and changelog are in the signed list; nothing is fetched for them.
        if (IndexDocuments(entry) is { } fromIndex)
            return fromIndex;

        if (_documents.TryGet(cacheKey, out var cached))
            return cached;

        var route = _route;
        var options = new CatalogFetchOptions
        {
            IndexUrl = route.Options.IndexUrl,
            MaxRedirects = route.Options.MaxRedirects,
            MaxRetries = route.Options.MaxRetries,
            MaxBytes = ThunderstoreListingDocuments.MaxBodyBytes,
            Timeout = route.Options.Timeout,
            Backoff = route.Options.Backoff,
            UserAgent = route.Options.UserAgent,
        };
        var fetcher = new CatalogFetcher(route.Transport, options, _clock);
        // TSTRAFFIC1: the release stamp keys the disk copies (a newer release refetches), as the Install pane's README files.
        string releaseStamp = entry.ReleasedUtc ?? entry.DateUpdated ?? "";
        return await _documents.LoadAsync(ns, name, version, releaseStamp, fetcher, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A fetcher over this catalog's transport with the body cap <paramref name="maxBytes"/>
    /// (LIST2: the listing-icon cache reads through it, the same retry/timeout rules as the index).
    /// </summary>
    public CatalogFetcher CreateFetcher(long maxBytes)
    {
        var route = _route;
        var options = new CatalogFetchOptions
        {
            IndexUrl = route.Options.IndexUrl,
            MaxRedirects = route.Options.MaxRedirects,
            MaxRetries = route.Options.MaxRetries,
            MaxBytes = maxBytes,
            Timeout = route.Options.Timeout,
            Backoff = route.Options.Backoff,
            UserAgent = route.Options.UserAgent,
        };
        return new CatalogFetcher(route.Transport, options, _clock);
    }

    /// <summary>The BepInEx root this catalog stages under; the LIST2 disk caches live beside it.</summary>
    public string BepInExRoot => _bepInExRoot;

    public bool TryGetCachedListingDocuments(string key, out ListingDocumentSet? documents)
    {
        documents = null;
        if (!TryGetEntry(key, out var entry) || entry == null) return false;
        // F11: a local listing's documents are its own zip's, read from disk (cached by the zip's stamp),
        // so the Mods page and the Install drawer never fall back to a disk-cached copy of another
        // package's README.
        if (IsLocalKey(entry.Key))
        {
            documents = _localIndex.Documents(entry);
            return true;
        }

        // INDEX1: an Index listing carries its README and changelog in the signed list itself.
        if (IndexDocuments(entry) is { } fromIndex)
        {
            documents = fromIndex;
            return true;
        }

        string cacheKey = ThunderstoreListingDocuments.CacheKey(
            entry.Owner ?? "",
            entry.PackageName ?? "",
            entry.Manifest.Version ?? "");
        if (!_documents.TryGet(cacheKey, out var cached)) return false;
        documents = cached;
        return true;
    }

    /// <summary>INDEX1: an Index listing's README and changelog from the signed list, or null for any other listing.</summary>
    internal static ListingDocumentSet? IndexDocuments(CatalogEntry entry)
    {
        var p = entry?.FromIndex == true ? entry.Pin?.Package : null;
        if (p == null) return null;
        return new ListingDocumentSet(entry!.Key, p.Version, true, null, p.Readme, p.Changelog != null,
            p.Changelog != null ? null : "the package has no changelog", p.Changelog ?? "");
    }

    /// <summary>
    /// INDEX1: what an inspection was made of: the version, and for a pinned listing its source and file. An inspection of
    /// a Thunderstore download is never reused for the Index row (or another pin) that replaced it, even at the same version.
    /// </summary>
    internal static string ListingTag(CatalogEntry entry) =>
        (entry.Manifest.Version ?? "") + (entry.Pin != null ? "|" + entry.SourceId + "|" + entry.Pin.Sha256 : "");

    internal static CatalogEntry ApplyInspection(CatalogEntry entry, PackageInspection inspection)
    {
        if (inspection.ProgenitorManifest == null)
            return entry.WithInspection(inspection.Provenance, entry.Manifest, inspection.Side, inspection.PluginDependencies);

        var merged = MergeManifest(entry, inspection.ProgenitorManifest);
        return entry.WithInspection(inspection.Provenance, merged, inspection.Side, inspection.PluginDependencies);
    }

    /// <summary>A verified <c>progenitor.json</c> over the listing's own manifest (the listing fills what it left out).</summary>
    private static Progenitor.Networking.ManifestMod MergeManifest(CatalogEntry entry, Progenitor.Networking.ManifestMod m) =>
        new(
            m.Id,
            string.IsNullOrEmpty(m.Version) ? entry.Manifest.Version : m.Version,
            m.Level,
            m.VersionRule,
            m.HotLoadable,
            m.Namespace ?? entry.Manifest.Namespace,
            m.Package ?? entry.Manifest.Package,
            m.Sha256,
            m.Size ?? entry.Manifest.Size,
            m.Url ?? entry.Manifest.Url);

    private void ReplaceEntry(CatalogEntry updated)
    {
        var list = new List<CatalogEntry>(_entries.Count);
        bool replaced = false;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (!replaced && string.Equals(_entries[i].Key, updated.Key, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(updated);
                replaced = true;
            }
            else list.Add(_entries[i]);
        }
        if (!replaced) list.Add(updated);
        _entries = list;
    }

    private void SetStatus(InstallProgress progress)
    {
        lock (_gate) _status = progress;
    }

    private static CatalogEntry[] ToArray(IReadOnlyList<CatalogEntry> list)
    {
        var arr = new CatalogEntry[list.Count];
        for (int i = 0; i < list.Count; i++) arr[i] = list[i];
        return arr;
    }

    private static bool Contains(string? value, string query) =>
        !string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
}

/// <summary>F27: what <see cref="InstallCatalogService.ActOnLedgerRows"/> does with each row.</summary>
internal enum LedgerAct
{
    /// <summary>Uninstall: a pending row is deleted; a loaded copy is marked for its next start.</summary>
    Uninstall = 0,

    /// <summary>Reactivate: drop the pending removal.</summary>
    Unmark = 1,

    /// <summary>Cancel install: delete the written-but-not-loaded rows only.</summary>
    CancelInstall = 2,
}
