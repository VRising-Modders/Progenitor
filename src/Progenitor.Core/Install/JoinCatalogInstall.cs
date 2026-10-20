using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Networking;

namespace Progenitor.Install;

/// <summary>
/// JOINFLOW2: download progress for the join screens' rows. The package download runs inside the Install tab's
/// own fetcher; a row sets <see cref="Current"/> before it asks for its package, and
/// <see cref="ProgressCatalogTransport"/> reports the bytes of the responses read under it (the value flows into
/// the fetcher's worker through the execution context, so two rows downloading at once each see their own).
/// </summary>
internal static class JoinDownloadProgress
{
    private static readonly AsyncLocal<Action<long, long?>?> Sink = new();

    public static Action<long, long?>? Current
    {
        get => Sink.Value;
        set => Sink.Value = value;
    }

    /// <summary>0–100 of <paramref name="read"/> over <paramref name="total"/>, or -1 when the total is unknown.</summary>
    public static int Percent(long read, long? total)
    {
        if (total is not > 0) return -1;
        double p = 100.0 * read / total.Value;
        return (int)Math.Max(0, Math.Min(100, Math.Floor(p)));
    }
}

/// <summary>JOINFLOW2: a transport that counts the body bytes of a response read under <see cref="JoinDownloadProgress.Current"/>.</summary>
internal sealed class ProgressCatalogTransport : ICatalogTransport
{
    private readonly ICatalogTransport _inner;

    public ProgressCatalogTransport(ICatalogTransport inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken)
    {
        var response = await _inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var sink = JoinDownloadProgress.Current;
        if (sink == null || response.StatusCode < 200 || response.StatusCode > 299) return response;
        // TSTRAFFIC1: WithContent keeps the validators and the Content-Encoding.
        return response.WithContent(new CountingStream(response.Content, response.ContentLength, sink));
    }

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly long? _total;
        private readonly Action<long, long?> _sink;
        private long _read;

        public CountingStream(Stream inner, long? total, Action<long, long?> sink)
        {
            _inner = inner;
            _total = total;
            _sink = sink;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Note(_inner.Read(buffer, offset, count));

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Note(await _inner.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Note(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

        private int Note(int n)
        {
            if (n > 0)
            {
                _read += n;
                try { _sink(_read, _total); }
                catch { /* progress must never break a download */ }
            }

            return n;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>JOINFLOW2: the result of reading the package index for a join.</summary>
internal sealed class JoinIndexLoad
{
    public JoinIndexLoad(string json, string stamp, IReadOnlyDictionary<string, PackageVersions> versions, string error)
    {
        Json = json ?? "[]";
        Stamp = stamp ?? "";
        Versions = versions ?? new Dictionary<string, PackageVersions>();
        Error = error ?? "";
    }

    /// <summary>The fetched (or cached) Thunderstore index body, or <c>[]</c> when none could be read.</summary>
    public string Json { get; }

    public string Stamp { get; }

    /// <summary>Package → its version list (local index first), for the packages asked about.</summary>
    public IReadOnlyDictionary<string, PackageVersions> Versions { get; }

    /// <summary>Why the Thunderstore index could not be read (the local index may still have answered), or empty.</summary>
    public string Error { get; }
}

/// <summary>
/// JOINFLOW2: the package index the join screens read — the same Thunderstore v1 index the Install tab reads, through
/// the same fetcher rules, with the configured local index (<c>[Install] LocalIndexPath</c>) in front of it. A fetched
/// body is reused for <see cref="Age"/> (<see cref="MaxAge"/> on the join screens, <see cref="ServerMaxAge"/> for the
/// server's install-window read); when the fetch fails, the Install tab's cached copy
/// (<c>BepInEx/progenitor-cache/index.json</c>) answers instead. TSTRAFFIC1: in the game the fetch is a
/// <see cref="ConditionalIndexFetch"/>, so a check past the age costs a 304 while Thunderstore's index is unchanged.
/// </summary>
internal sealed class JoinPackageIndex
{
    /// <summary>The join screens' reuse window (a check past it is conditional).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    /// <summary>TSTRAFFIC1: the server's install-window read (<c>ServerPackageAvailability</c>) reuses a read this long.</summary>
    public static readonly TimeSpan ServerMaxAge = TimeSpan.FromMinutes(30);

    private readonly Func<CancellationToken, Task<CatalogTextFetchResult>> _fetch;
    private readonly Func<string?> _cached;
    private readonly Func<string?> _localIndexPath;
    private readonly Func<DateTime> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _json;
    private DateTime _fetchedUtc;

    public JoinPackageIndex(
        Func<CancellationToken, Task<CatalogTextFetchResult>> fetch,
        Func<string?>? cachedIndexJson = null,
        Func<string?>? localIndexPath = null,
        Func<DateTime>? now = null,
        TimeSpan? maxAge = null)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _cached = cachedIndexJson ?? (() => null);
        _localIndexPath = localIndexPath ?? (() => null);
        _now = now ?? (() => DateTime.UtcNow);
        Age = maxAge is { } age && age > TimeSpan.Zero ? age : MaxAge;
    }

    /// <summary>How long this index reuses a fetched body before it checks again.</summary>
    public TimeSpan Age { get; }

    /// <summary>
    /// INDEX1: every listed version of the Progenitor Index, checked on this index's cadence (null: Thunderstore only). A
    /// package the Index lists is resolved from it (after the local test source, before Thunderstore).
    /// </summary>
    public Func<CancellationToken, Task<IReadOnlyList<CatalogEntry>>>? IndexRows { get; set; }

    /// <summary>INDEX1 (M1): the Progenitor Index whose takedowns remove Thunderstore versions (null: none).</summary>
    public Func<IndexDocument?>? Takedowns { get; set; }

    /// <summary>INDEX1: <c>[Mods] UseThunderstore</c>; off, the Thunderstore index is not read (the Index still is).</summary>
    public Func<bool> ThunderstoreEnabled { get; set; } = () => true;

    /// <summary>How many times the Thunderstore index was fetched (tests).</summary>
    public int Fetches { get; private set; }

    /// <summary>
    /// AUTOUPDATE1: a fetched body (not a reused one), with its stamp. The client hands it to the Install tab's catalog
    /// so update detection sees every index this client reads. Runs on the fetching thread; must not throw.
    /// </summary>
    public Action<string, string>? OnFetched { get; set; }

    /// <summary>Read the version lists of <paramref name="wanted"/> (package → version a server named).</summary>
    public Task<JoinIndexLoad> LoadAsync(IReadOnlyDictionary<string, string> wanted, CancellationToken cancellationToken) =>
        LoadAsync(wanted, null, cancellationToken);

    /// <summary>The same, choosing each version under its mod's rule (<paramref name="rules"/>: package → rule).</summary>
    public async Task<JoinIndexLoad> LoadAsync(IReadOnlyDictionary<string, string> wanted,
        IReadOnlyDictionary<string, Plugins.NetworkVersionRule>? rules, CancellationToken cancellationToken)
    {
        var versions = new Dictionary<string, PackageVersions>(StringComparer.OrdinalIgnoreCase);
        // The local test index first: it shadows the fetched listing of the same package, as in the Install tab.
        string? localPath = SafeLocalIndex();
        if (localPath != null)
        {
            try
            {
                if (File.Exists(localPath) && new FileInfo(localPath).Length <= LocalIndexSource.MaxIndexBytes)
                    ThunderstoreVersions.TryLoad(File.ReadAllText(localPath), "local:" + localPath, wanted, versions, local: true, rules, out _);
            }
            catch { /* a broken local index leaves the fetched one */ }
        }

        // INDEX1: the Progenitor Index next: a package it lists is installed from it, never from Thunderstore.
        if (IndexRows is { } indexRows)
        {
            try { PackageVersions.AddFromIndex(await indexRows(cancellationToken).ConfigureAwait(false), wanted, versions, rules); }
            catch (OperationCanceledException) { throw; }
            catch { /* the Index could not be read: Thunderstore answers alone */ }
        }

        string error = "";
        string json = "[]";
        string stamp = "";
        bool allLocal = true;
        foreach (var kv in wanted) if (!versions.ContainsKey(kv.Key)) allLocal = false;
        bool thunderstore;
        try { thunderstore = ThunderstoreEnabled(); }
        catch { thunderstore = true; }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!thunderstore)
            {
                // INDEX1: Thunderstore is off: the local test source and the Index answer alone.
            }
            else if (_json != null && _now() - _fetchedUtc < Age)
            {
                json = _json;
                stamp = ModManifestJson.FormatIssued(_fetchedUtc);
            }
            else
            {
                CatalogTextFetchResult fetched;
                try
                {
                    Fetches++;
                    fetched = await _fetch(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) { fetched = new CatalogTextFetchResult(false, InstallReasons.FetchTransport + ": " + e.Message, null, 0, 0); }

                if (fetched.Ok && !string.IsNullOrWhiteSpace(fetched.Text))
                {
                    _json = fetched.Text;
                    _fetchedUtc = _now();
                    json = _json!;
                    stamp = ModManifestJson.FormatIssued(_fetchedUtc);
                    try { OnFetched?.Invoke(json, stamp); }
                    catch { /* a listener never breaks the join's read */ }
                }
                else
                {
                    error = fetched.Error ?? InstallReasons.FetchTransport;
                    string? cached = null;
                    try { cached = _cached(); }
                    catch { cached = null; }
                    if (!string.IsNullOrWhiteSpace(cached))
                    {
                        json = cached!;
                        stamp = "cache";
                    }
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (json != "[]")
        {
            if (!ThunderstoreVersions.TryLoad(json, stamp, wanted, versions, local: false, rules, out string parseError) && error.Length == 0)
                error = parseError;
        }

        // INDEX1 (M1): Thunderstore versions a Progenitor Index takedown hides or disables are never offered.
        if (Takedowns is { } takedowns)
        {
            IndexDocument? doc;
            try { doc = takedowns(); }
            catch { doc = null; }
            PackageVersions.RemoveTakedowns(versions, doc, rules);
        }

        // Every package found in the local index: the fetch's failure does not matter.
        if (allLocal) error = "";
        return new JoinIndexLoad(json, stamp, versions, error);
    }

    private string? SafeLocalIndex()
    {
        try
        {
            string? folder = _localIndexPath();
            return string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(folder!, LocalIndexSource.IndexFileName);
        }
        catch { return null; }
    }
}

/// <summary>JOINFLOW2: the live seams the catalog installer asks (tests supply plain values).</summary>
internal sealed class JoinInstallRules
{
    /// <summary><c>[Mods] UseThunderstore</c>.</summary>
    public Func<bool> ThunderstoreEnabled { get; set; } = () => true;

    /// <summary>INDEX1: <c>[Install] UseProgenitorIndex</c> (off in tests unless set).</summary>
    public Func<bool> IndexEnabled { get; set; } = () => false;

    /// <summary><c>[Mods] ShowNsfwContent</c>.</summary>
    public Func<bool> ShowNsfw { get; set; } = () => false;

    /// <summary>
    /// The tool that installed this listing's loaded copy when Progenitor did not (MANAGED1: "Managed elsewhere";
    /// <c>r2modman</c> when known, else <c>another tool</c>), or null when Progenitor manages it or it is not installed.
    /// </summary>
    public Func<CatalogEntry, string?> ManagedElsewhere { get; set; } = _ => null;

    /// <summary>Run the write on the game's main thread (the Install tab writes from a click, on that thread).</summary>
    public Func<Func<ExecuteSetResult>, Task<ExecuteSetResult>> RunOnMain { get; set; } = work => Task.FromResult(work());

    /// <summary>Is the plugin loaded in this process now (hot-load confirmation).</summary>
    public Func<string, bool> PluginLoaded { get; set; } = _ => false;

    /// <summary>How long a hot write waits for the reloader to report the plugin loaded.</summary>
    public TimeSpan LoadConfirmWindow { get; set; } = TimeSpan.FromSeconds(10);

    public Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (t, ct) => Task.Delay(t, ct);

    /// <summary>
    /// JOINFLOW4: before "Use version X" writes the server's version, keep (park) the newer active copy. Answers
    /// (ok, the sentence, moved: the copy was removed now and the plugin unloads within a second). Null: no store.
    /// </summary>
    public Func<JoinInstallRequest, (bool Ok, string Note, bool Moved)>? KeepBeforeReplace { get; set; }

    /// <summary>JOINFLOW4: how long the write waits for a parked plugin to unload before it plans.</summary>
    public TimeSpan UnloadWait { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// JOINFLOW2: installs one Thunderstore package for the join screens through the Install tab's own machinery — a
/// catalog service (<see cref="InstallCatalogService"/>) built for this batch: the exact version from the
/// package's version list, the download with its size cap and the inspection, the plan with its dependencies and
/// the trusted hotLoadable admission, and the write into <c>progenitor-reload</c> (loads now) or
/// <c>progenitor-pending</c> (next start) with its ledger row. Every Install tab refusal holds; the join adds the
/// ones a server-named package needs (the package must be the mod the server named, a server-only package is
/// refused, a download that is not Thunderstore's is refused).
/// </summary>
internal sealed class JoinCatalogInstaller : IJoinPackageInstaller
{
    private readonly Func<InstallCatalogService> _catalogFactory;
    private readonly JoinPackageIndex _index;
    private readonly IReadOnlyList<JoinInstallRequest> _batch;
    private readonly JoinInstallRules _rules;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _prepareGate = new();
    private Task<Prepared>? _prepared;

    public JoinCatalogInstaller(
        Func<InstallCatalogService> catalogFactory,
        JoinPackageIndex index,
        IReadOnlyList<JoinInstallRequest> batch,
        JoinInstallRules? rules = null)
    {
        _catalogFactory = catalogFactory ?? throw new ArgumentNullException(nameof(catalogFactory));
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _batch = batch ?? Array.Empty<JoinInstallRequest>();
        _rules = rules ?? new JoinInstallRules();
    }

    private sealed class Prepared
    {
        public InstallCatalogService? Catalog;
        public JoinIndexLoad? Index;
        public string Error = "";
    }

    private Task<Prepared> Prepare(CancellationToken ct)
    {
        lock (_prepareGate) return _prepared ??= PrepareCore(ct);
    }

    private async Task<Prepared> PrepareCore(CancellationToken ct)
    {
        var p = new Prepared();
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rules = new Dictionary<string, Plugins.NetworkVersionRule>(StringComparer.OrdinalIgnoreCase);
        // One version per package; a required row's (version, rule) wins over a recommended row's, as the batch keeps it.
        var requiredSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _batch)
        {
            if (r.Package.Length == 0) continue;
            if (wanted.ContainsKey(r.Package) && (requiredSet.Contains(r.Package) || !r.Required)) continue;
            wanted[r.Package] = r.Version;
            rules[r.Package] = r.VersionRule;
            if (r.Required) requiredSet.Add(r.Package);
        }

        try
        {
            p.Index = await _index.LoadAsync(wanted, rules, ct).ConfigureAwait(false);
            p.Catalog = _catalogFactory();
            var load = p.Catalog.LoadIndexFromJson(p.Index.Json, p.Index.Stamp);
            if (!load.Ok) p.Error = load.Error ?? InstallReasons.IndexEmpty;
            // Every row's exact version is in place before any row runs: a row's dependency closure that
            // names another row's package then resolves to the version the server named, never the newest.
            foreach (var pv in p.Index.Versions.Values)
            {
                // INDEX1: a package the Progenitor Index lists is that version's Index row, whatever the batch catalog merged.
                if (pv.FromIndex && pv.Entry != null && pv.Matched != null)
                {
                    p.Catalog.UseVersionEntry(pv.Entry);
                    continue;
                }

                if (pv.Entry != null && pv.Matched != null && p.Catalog.TryGetEntry(pv.Package, out var listed) && listed != null
                    && !string.Equals(listed.Manifest.Version, pv.Matched, StringComparison.Ordinal))
                    p.Catalog.UseVersionEntry(pv.Entry);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { p.Error = e.Message; }
        return p;
    }

    public async Task<JoinInstallOutcome> InstallAsync(JoinInstallRequest request, Action<JoinInstallPhase, int> progress, CancellationToken ct)
    {
        var outcome = await InstallCoreAsync(request, progress, ct).ConfigureAwait(false);
        // LOCALJOIN1: a local test package's answer names the local test source, never Thunderstore.
        return request is { LocalTest: true } && outcome.Reason.IndexOf("Thunderstore", StringComparison.Ordinal) >= 0
            ? new JoinInstallOutcome(outcome.Phase, LocalWords(outcome.Reason), outcome.PluginId, outcome.Path)
            : outcome;
    }

    /// <summary>LOCALJOIN1: a reason worded for Thunderstore, reworded for the local test source.</summary>
    internal static string LocalWords(string reason) =>
        (reason ?? "").Replace("Thunderstore's", "the " + LocalTestSource.Words + "'s").Replace("Thunderstore", "the " + LocalTestSource.Words);

    private async Task<JoinInstallOutcome> InstallCoreAsync(JoinInstallRequest request, Action<JoinInstallPhase, int> progress, CancellationToken ct)
    {
        progress ??= (_, _) => { };
        if (request == null) return Refused("no request");
        // LOCALJOIN1: [Mods] UseThunderstore is about Thunderstore; a local test package is not one.
        // INDEX1: [Mods] UseThunderstore is about Thunderstore; a package the Progenitor Index lists still installs (checked below).
        if (!request.LocalTest && !_rules.ThunderstoreEnabled() && !_rules.IndexEnabled()) return Refused(JoinInstallText.ThunderstoreOff);
        progress(JoinInstallPhase.Resolving, -1);
        var prepared = await Prepare(ct).ConfigureAwait(false);
        var catalog = prepared.Catalog;
        if (catalog == null) return Failed(JoinInstallText.IndexUnavailable(prepared.Error));

        // The exact version from the package's version list (Thunderstore keeps old versions downloadable).
        var versions = prepared.Index?.Versions;
        if (versions == null || !versions.TryGetValue(request.Package, out var pv))
            return prepared.Index != null && prepared.Index.Error.Length > 0 && prepared.Index.Json == "[]"
                ? Failed(JoinInstallText.IndexUnavailable(prepared.Index.Error))
                : Refused(JoinInstallText.NotListed(request.Package));
        if (!request.LocalTest && !pv.FromIndex && !_rules.ThunderstoreEnabled()) return Refused(JoinInstallText.ThunderstoreOff);
        string ruleNote = "";
        string? exact = request.Version.Length == 0 ? (pv.Latest.Length > 0 ? pv.Latest : null)
            : ThunderstoreVersions.Resolve(pv.Active, request.Version, request.VersionRule, out ruleNote);
        if (exact == null) return Refused(JoinInstallText.VersionGone(request.Package, request.Version, pv.Latest));
        string key = pv.Package;
        if (!catalog.TryGetEntry(key, out var listed) || listed == null) return Refused(JoinInstallText.NotListed(request.Package));
        // H1: the row is the source that answered (an Index row never stands in for a Thunderstore version, or back).
        if (!string.Equals(listed.Manifest.Version, exact, StringComparison.Ordinal) || listed.FromIndex != pv.FromIndex)
        {
            var entryForVersion = pv.Entry != null && string.Equals(pv.Matched, exact, StringComparison.Ordinal) ? pv.Entry : null;
            if (entryForVersion == null) return Refused(JoinInstallText.VersionGone(request.Package, request.Version, pv.Latest));
            catalog.UseVersionEntry(entryForVersion);
        }

        if (!catalog.TryGetEntry(key, out var entry) || entry == null) return Refused(JoinInstallText.NotListed(request.Package));
        // NotThunderstore: a Thunderstore https download, or the configured local test index.
        // INDEX1: or an Index entry, whose download the catalog checks against its signed pin.
        if (!catalog.IsLocalKey(key) && !entry.FromIndex && !IsThunderstoreUrl(entry.DownloadUrl))
            return Refused(JoinInstallText.NotThunderstore);
        if (entry.HasNsfwContent && !_rules.ShowNsfw()) return Refused(JoinInstallText.Nsfw);
        if (NonModPackages.IsModManager(entry.Owner, entry.PackageName, entry.Key)) return Refused(NonModPackages.ModManagerText);
        if (NonModPackages.TooLarge(entry.ListingFileSize, NonModPackages.DownloadLimit))
            return Refused(InstallReasons.FetchSizeCeiling + " (" + NonModPackages.TooLargeText(entry.ListingFileSize!.Value, NonModPackages.DownloadLimit) + ")");

        // Download and inspect, with this row's progress.
        progress(JoinInstallPhase.Downloading, 0);
        long? total = entry.ListingFileSize;
        JoinDownloadProgress.Current = (read, length) => progress(JoinInstallPhase.Downloading, JoinDownloadProgress.Percent(read, length ?? total));
        InspectCallResult inspected;
        try { inspected = await catalog.DownloadAndInspectAsync(key, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return Failed(JoinText.InstallCanceled); }
        finally { JoinDownloadProgress.Current = null; }
        if (!inspected.Ok || inspected.Entry == null || inspected.Inspection == null)
            return inspected.Inspection != null
                ? Refused(inspected.Error ?? inspected.Inspection.Refusal ?? InstallReasons.InspectNotZip)
                : IsRefusal(inspected.Error) ? Refused(inspected.Error!) : Failed(inspected.Error ?? InstallReasons.FetchTransport);

        var inspection = inspected.Inspection;
        var verified = inspected.Entry;
        // SideMismatch: the package must run on a client.
        if (inspection.Side == PackageSide.Server
            || string.Equals(inspection.OrdinaryLayout?.LoadsIn, "server", StringComparison.OrdinalIgnoreCase))
        {
            catalog.ReleaseDownload(key);
            return Refused(JoinInstallText.SideMismatch);
        }

        // The package must be the mod the server named (a server names a package for a plugin id).
        string pluginId = !string.IsNullOrEmpty(verified.Manifest.Id) ? verified.Manifest.Id : inspection.OrdinaryLayout?.Plugin?.PluginGuid ?? "";
        if (request.ModId.Length > 0 && pluginId.Length > 0 && !string.Equals(pluginId, request.ModId, StringComparison.OrdinalIgnoreCase))
        {
            catalog.ReleaseDownload(key);
            return Refused(JoinInstallText.WrongPlugin(request.Package, pluginId, request.ModId));
        }

        // A required row must prove it: a package whose plugin cannot be read (several plugins, none) would
        // count as the server's mod without being it.
        if (request.Required && request.ModId.Length > 0 && pluginId.Length == 0)
        {
            catalog.ReleaseDownload(key);
            return Refused(JoinInstallText.UnknownPlugin(request.Package, request.ModId));
        }

        // A copy another tool installed is updated (or replaced) with that tool, never over it.
        if (request.Kind is JoinInstallKind.Update or JoinInstallKind.UseVersion)
        {
            string? manager = null;
            try { manager = _rules.ManagedElsewhere(verified); }
            catch { manager = null; }
            if (manager != null)
            {
                catalog.ReleaseDownload(key);
                return Refused(JoinInstallText.ManagedElsewhere(manager));
            }
        }

        // The bytes are what Thunderstore lists (a truncated or swapped download is not installed).
        string? zip = catalog.ZipPathOf(key);
        if (entry.ListingFileSize is > 0 && zip != null && !catalog.IsLocalKey(key))
        {
            long length;
            try { length = new FileInfo(zip).Length; }
            catch { length = -1; }
            if (length != entry.ListingFileSize.Value)
            {
                catalog.ReleaseDownload(key);
                return Failed(JoinInstallText.SizeMismatch(entry.ListingFileSize.Value, length));
            }
        }

        progress(JoinInstallPhase.Installing, -1);
        // Plans and writes one at a time: two roots sharing a dependency must not download or write it twice.
        try { await _writeGate.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            catalog.ReleaseDownload(key);
            return Failed(JoinText.InstallCanceled);
        }

        ExecuteSetResult written;
        InstallPlanSet set;
        try
        {
            // An earlier row's write may have installed this package as its dependency and spent this row's
            // download: fetch it again (an install over the same bytes is the catalog's "already present").
            if (catalog.ZipPathOf(key) == null)
            {
                InspectCallResult again;
                try { again = await catalog.DownloadAndInspectAsync(key, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return Failed(JoinText.InstallCanceled); }
                if (!again.Ok) return Failed(again.Error ?? InstallReasons.FetchTransport);
            }

            // JOINFLOW4: "Use version X" keeps the newer copy before anything replaces it. Removed now (the reloader
            // can unload it), the plan then sees a fresh install the hot route may load this session; kept loaded and
            // marked, the server's version takes the restart route and the marked copy goes at the next start.
            if (request.Kind == JoinInstallKind.UseVersion && _rules.KeepBeforeReplace != null)
            {
                (bool Ok, string Note, bool Moved) kept;
                try { kept = _rules.KeepBeforeReplace(request); }
                catch (Exception e) { kept = (false, e.Message, false); }
                if (!kept.Ok && request.Keeps)
                {
                    catalog.ReleaseDownload(key);
                    return Refused(JoinInstallText.NotKept(request.Replaces, kept.Note));
                }

                // The server's version is never written while the kept copy is still loaded: two copies of one plugin
                // would load together (review finding 1).
                if (kept.Ok && kept.Moved && !await WaitUnloaded(request.ModId.Length > 0 ? request.ModId : pluginId, ct).ConfigureAwait(false))
                {
                    catalog.ReleaseDownload(key);
                    return Refused(JoinInstallText.NotUnloaded(request.Replaces, request.Version));
                }
            }

            try { set = await catalog.PlanWithDependenciesAsync(key, InstallTarget.Client, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return Failed(JoinText.InstallCanceled); }
            if (!set.Ok || set.Root == null)
            {
                catalog.ReleaseDownload(key);
                return Refused(set.Error ?? InstallReasons.PlanBlocked);
            }

            written = await _rules.RunOnMain(() => catalog.ExecuteWithDependencies(set)).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            return Failed(InstallReasons.FailedWriteNothing + ": " + e.Message);
        }
        finally
        {
            _writeGate.Release();
        }

        ExecuteMemberResult? root = null;
        foreach (var m in written.Members) if (m.IsRoot) root = m;
        if (!written.Ok || root == null) return Failed(written.Error ?? InstallReasons.FailedWriteNothing);
        string id = set.Root.PluginId ?? pluginId;
        // The version picked under the mod's rule is said, when it is not the one named.
        string picked = ruleNote.Length > 0 ? "installed " + ruleNote : "";
        if (!root.HotLoaded) return new JoinInstallOutcome(JoinInstallPhase.AfterRestart, picked, id, root.Path ?? "");

        // Written into progenitor-reload: the reloader loads it within a second. Confirm when it can be.
        bool confirmed = await ConfirmLoaded(id, ct).ConfigureAwait(false);
        string reason = confirmed ? picked : (picked.Length > 0 ? picked + "; " : "") + JoinInstallText.NotConfirmed;
        return new JoinInstallOutcome(JoinInstallPhase.Loaded, reason, id, root.Path ?? "");
    }

    /// <summary>
    /// JOINFLOW4: wait (at most <see cref="JoinInstallRules.UnloadWait"/>) until the parked plugin is no longer loaded.
    /// False when it still is (or the wait was canceled).
    /// </summary>
    private async Task<bool> WaitUnloaded(string pluginId, CancellationToken ct)
    {
        if (pluginId.Length == 0) return true;
        var deadline = DateTime.UtcNow + _rules.UnloadWait;
        while (true)
        {
            bool loaded;
            try { loaded = _rules.PluginLoaded(pluginId); }
            catch { loaded = true; }
            if (!loaded) return true;
            if (DateTime.UtcNow >= deadline) return false;
            try { await _rules.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
    }

    private async Task<bool> ConfirmLoaded(string pluginId, CancellationToken ct)
    {
        if (pluginId.Length == 0) return false;
        var deadline = DateTime.UtcNow + _rules.LoadConfirmWindow;
        while (true)
        {
            bool loaded;
            try { loaded = _rules.PluginLoaded(pluginId); }
            catch { loaded = false; }
            if (loaded) return true;
            if (DateTime.UtcNow >= deadline) return false;
            try { await _rules.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
    }

    /// <summary>An https download on thunderstore.io or one of its hosts (its CDN).</summary>
    internal static bool IsThunderstoreUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url!.Trim(), UriKind.Absolute, out var uri)) return false;
        // LOCALTS1: the dev Thunderstore stand-in, while one is configured and allowed.
        if (ThunderstoreMirror.IsMirrorUri(uri)) return true;
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return false;
        string host = uri.Host.ToLowerInvariant();
        return host == "thunderstore.io" || host.EndsWith(".thunderstore.io", StringComparison.Ordinal);
    }

    private static bool IsRefusal(string? error) =>
        !string.IsNullOrEmpty(error)
        && (error!.StartsWith(InstallReasons.FetchSizeCeiling, StringComparison.Ordinal)
            || string.Equals(error, NonModPackages.ModManagerText, StringComparison.Ordinal));

    private static JoinInstallOutcome Refused(string reason) => new(JoinInstallPhase.Refused, reason);

    private static JoinInstallOutcome Failed(string reason) => new(JoinInstallPhase.Failed, reason);
}

/// <summary>JOINFLOW2: the reasons a join-screen install shows on its row. US English.</summary>
internal static class JoinInstallText
{
    public const string ThunderstoreOff = "Thunderstore is turned off in Mods > Configuration.";
    public const string NotThunderstore = "Its download is not on Thunderstore, so it is not installed from here.";
    public const string Nsfw = "Its author flagged it NSFW. Turn on Show NSFW content in Mods > Configuration to install it.";
    public const string SideMismatch = "It is a server-only mod; it does not install on your game.";
    public const string NotConfirmed = "written to progenitor-reload; the reloader has not reported it loaded yet";

    public static string IndexUnavailable(string why) =>
        "Thunderstore could not be reached" + (string.IsNullOrWhiteSpace(why) ? "." : " (" + why.Trim() + ").");

    public static string NotListed(string package) => package + " is not on Thunderstore's V Rising list.";

    public static string VersionGone(string package, string version, string latest) =>
        package + " " + version + " is no longer on Thunderstore" + (string.IsNullOrEmpty(latest) ? "." : " (it has " + latest + ").");

    public static string WrongPlugin(string package, string found, string wanted) =>
        package + " holds " + found + ", not " + wanted + " that the server named.";

    public static string UnknownPlugin(string package, string wanted) =>
        package + " does not show which plugin it holds, so it cannot stand in for " + wanted + ".";

    /// <summary>JOINFLOW4: the newer copy could not be kept, so nothing replaces it.</summary>
    public static string NotKept(string have, string why) =>
        "your " + (string.IsNullOrEmpty(have) ? "newer copy" : have) + " could not be kept for other servers ("
        + (string.IsNullOrWhiteSpace(why) ? "unknown" : why.Trim().TrimEnd('.')) + "), so it was not replaced.";

    /// <summary>JOINFLOW4: the kept copy did not unload in time, so the server's version is not written beside it.</summary>
    public static string NotUnloaded(string have, string version) =>
        "your " + (string.IsNullOrEmpty(have) ? "newer copy" : have) + " is kept for other servers but had not unloaded yet, so "
        + version + " was not written beside it. Press it again.";

    public static string ManagedElsewhere(string manager) =>
        "Installed by " + (string.IsNullOrWhiteSpace(manager) ? "another tool" : manager.Trim()) + "; update it there.";

    public static string SizeMismatch(long listed, long received) =>
        "size mismatch: Thunderstore lists " + listed.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " bytes, received " + received.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
