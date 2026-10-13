using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Progenitor.Install;

/// <summary>REMOTEINST1: the server's cached answer to "can this process reach Thunderstore?".</summary>
internal readonly struct RemoteReachabilityResult
{
    public RemoteReachabilityResult(RemoteReachability state, string detail, DateTime checkedUtc)
    {
        State = state;
        Detail = detail ?? "";
        CheckedUtc = checkedUtc;
    }

    public RemoteReachability State { get; }

    /// <summary>The sub-cause word of an unreachable result (<c>dns</c>, <c>timeout</c>, …).</summary>
    public string Detail { get; }

    public DateTime CheckedUtc { get; }
}

/// <summary>
/// REMOTEINST1: the install core a Progenitor server runs for an in-game admin. Game-free: the
/// BepInEx root, the transport, the catalog, the clock and the main-thread marshaler are all
/// supplied, so every refusal, every network failure, the hot lane, the pending lane, the ledger
/// and uninstall are driven by tests with a fake HTTP layer and a temp BepInEx tree.
///
/// <para><b>The same code as a local install.</b> The server builds an
/// <see cref="InstallCatalogService"/> over its own BepInEx root with the local-server target
/// pointing at that same root and <c>thisProcessIsServer</c> set, so it downloads, inspects,
/// admits, plans (dependencies included), stages and ledgers through exactly the path the Install
/// tab uses for a local dedicated server — and, because the plan's target is the process that is
/// running, a hot-loadable Progenitor plugin that admission clears goes to
/// <c>progenitor-reload/</c> and everything else to <c>progenitor-pending/</c>.</para>
///
/// <para><b>Nothing partial survives.</b> Downloads land in the fetcher's <c>.part</c> staging file
/// and are deleted at the end of every operation (<see cref="InstallCatalogService.DiscardDownloads"/>);
/// the ledger is written only by the staging step, which runs only after every check passed.</para>
/// </summary>
internal sealed class RemoteInstallService
{
    /// <summary>
    /// TSTRAFFIC1: how long an uninstall reuses the loaded index (was 10 minutes). An install or update always
    /// checks (conditionally: a 304 while Thunderstore's index is unchanged).
    /// </summary>
    public static readonly TimeSpan DefaultIndexMaxAge = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan DefaultHotLoadConfirmWindow = TimeSpan.FromSeconds(6);
    public static readonly TimeSpan ProbeMinInterval = TimeSpan.FromMinutes(1);
    public const long ProbeMaxBytes = 64 * 1024;

    private readonly string _bepInExRoot;
    private readonly Func<RemoteInstallSettings> _settings;
    private readonly RecordingCatalogTransport _transport;
    private readonly Func<ICatalogTransport, RemoteInstallSettings, InstallCatalogService> _catalogFactory;
    private readonly ICatalogClock _clock;
    private readonly object _gate = new();

    private InstallCatalogService? _catalog;
    private string _catalogIndexUrl = "";
    private DateTime _indexLoadedUtc = DateTime.MinValue;
    private bool _busy;
    private CancellationTokenSource? _running;
    private RemoteInstallStage _runningStage;
    private string _runningPackage = "";
    private bool _adminCancelled;
    private RemoteReachabilityResult _probe = new(RemoteReachability.Unknown, "", DateTime.MinValue);
    private Task<RemoteReachabilityResult>? _probeTask;

    public RemoteInstallService(
        string bepInExRoot,
        Func<RemoteInstallSettings> settings,
        ICatalogTransport transport,
        Func<ICatalogTransport, RemoteInstallSettings, InstallCatalogService> catalogFactory,
        ICatalogClock? clock = null)
    {
        if (string.IsNullOrWhiteSpace(bepInExRoot)) throw new ArgumentException("BepInEx root is required.", nameof(bepInExRoot));
        _bepInExRoot = Path.GetFullPath(bepInExRoot);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _transport = transport as RecordingCatalogTransport ?? new RecordingCatalogTransport(transport, () => SafeSettings().StallTimeout);
        _catalogFactory = catalogFactory ?? throw new ArgumentNullException(nameof(catalogFactory));
        _clock = clock ?? new SystemCatalogClock();
    }

    /// <summary>The plugin-loaded probe the hot lane confirms against (plugin GUID → loaded now). Null skips confirmation.</summary>
    internal Func<string, bool>? PluginLoaded { get; set; }

    /// <summary>
    /// Runs the one step that writes into the BepInEx tree (staging, the reload folder, the ledger) on
    /// the game's main thread, where the reloader runs. Null runs it inline (tests).
    /// </summary>
    internal Func<Func<object?>, Task<object?>>? RunOnMain { get; set; }

    /// <summary>One line per finished operation (the audit log).</summary>
    internal Action<string>? Audit { get; set; }

    /// <summary>
    /// SRVUNINST1: the plugins this server process loaded (the chainloader's report, each with its
    /// ordinary-plugin identification). Main thread. Null reports none; the reload folder is still read.
    /// </summary>
    internal Func<IReadOnlyList<ServerPluginFact>>? PluginFacts { get; set; }

    /// <summary>
    /// SRVUNINST1 review: the same report read fresh (no identification cache), for the uninstall itself.
    /// Null uses <see cref="PluginFacts"/>.
    /// </summary>
    internal Func<IReadOnlyList<ServerPluginFact>>? PluginFactsFresh { get; set; }

    /// <summary>SRVUNINST1: the GUIDs a reload-folder DLL declares. Null reads the DLL's metadata (tests replace it).</summary>
    internal Func<string, IReadOnlyList<string>>? ReloadGuids { get; set; }

    /// <summary>Seconds clock for the rate limiter. Defaults to a monotonic stopwatch.</summary>
    internal Func<double> Now { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    internal TimeSpan IndexMaxAge { get; set; } = DefaultIndexMaxAge;
    internal TimeSpan HotLoadConfirmWindow { get; set; } = DefaultHotLoadConfirmWindow;

    public RemoteInstallRateLimiter Limiter { get; } = new();

    public string BepInExRoot => _bepInExRoot;

    public bool Busy
    {
        get { lock (_gate) return _busy; }
    }

    public RemoteReachabilityResult LastProbe
    {
        get { lock (_gate) return _probe; }
    }

    private RemoteInstallSettings SafeSettings()
    {
        // Fail closed: settings that cannot be read switch remote installs off.
        try { return _settings() ?? RemoteInstallSettings.Unbound; }
        catch { return RemoteInstallSettings.Unbound; }
    }

    // ---- status and list -------------------------------------------------------------------

    /// <summary>
    /// What this server offers <paramref name="actor"/>: enabled, admin, allowed, the reachability
    /// probe, and — only for an allowed admin — the ledger rows.
    /// </summary>
    public RemoteInstallStatus StatusFor(int nonce, in RemoteInstallActor actor)
    {
        var settings = SafeSettings();
        bool allowed = RemoteInstallPolicy.Allowed(settings, actor);
        var probe = LastProbe;
        IReadOnlyList<RemoteServerRow> rows = Array.Empty<RemoteServerRow>();
        bool truncated = false;
        if (allowed)
        {
            // SRVUNINST1: the ledger rows, then one row per plugin the server identified, so a client
            // with no listing for a mod still learns what an uninstall of it would do here.
            var ledgerRows = ListRows();
            var all = new List<RemoteServerRow>(ledgerRows);
            try { all.AddRange(ServerPluginRemovals.Rows(CurrentPluginFacts(), LedgerFiles(ledgerRows))); }
            catch { /* the plugin rows are an addition; the ledger rows stand alone */ }
            var fit = new List<RemoteServerRow>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                // A row the packet cannot carry (an over-long id or version) is left out rather than
                // failing every status for every admin.
                if (System.Text.Encoding.UTF8.GetByteCount(all[i].Package) > RemoteInstallWire.MaxPackageChars
                    || System.Text.Encoding.UTF8.GetByteCount(all[i].Version) > RemoteInstallWire.MaxVersionChars)
                {
                    truncated = true;
                    continue;
                }

                fit.Add(all[i]);
            }

            truncated |= fit.Count > RemoteInstallWire.MaxRows;
            // F16: the listings this server's own local test index offers, after the ledger rows and only where they fit.
            try
            {
                int room = Math.Min(RemoteLocalOffer.MaxOffers, RemoteInstallWire.MaxRows - fit.Count);
                if (room > 0)
                    foreach (var (key, version) in LocalOffers())
                    {
                        if (room-- <= 0) break;
                        string package = RemoteLocalOffer.Of(key);
                        if (package.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(version ?? "") > RemoteInstallWire.MaxVersionChars) continue;
                        fit.Add(new RemoteServerRow(package, version ?? "", RemoteRowState.None, ""));
                    }
            }
            catch { /* the offers are an addition */ }

            rows = fit;
        }

        return new RemoteInstallStatus(nonce, settings.Enabled, actor.Authorized, allowed, probe.State, probe.Detail, allowed, rows, truncated,
            actor.SteamId, actor.AuthorizedBy, actor.OnAdminList, actor.IsAdmin);
    }

    /// <summary>
    /// F16: the (key, version) pairs this server's own <c>[Install] LocalIndexPath</c> lists (live: that index read now; empty
    /// when the key is empty). Tests set it.
    /// </summary>
    internal Func<IReadOnlyList<(string Key, string Version)>>? LocalOffersProbe { get; set; }

    private readonly object _offersGate = new();
    private IReadOnlyList<(string Key, string Version)> _offers = Array.Empty<(string, string)>();
    private string _offersStamp = "";
    private int _offersReads;

    /// <summary>Review (L5): the configured local index folder (live: <c>[Install] LocalIndexPath</c>). Tests set it.</summary>
    internal Func<string?>? LocalIndexFolderProbe { get; set; }

    /// <summary>How many times the local index file was read for status offers (tests).</summary>
    internal int OffersReads => System.Threading.Volatile.Read(ref _offersReads);

    private IReadOnlyList<(string Key, string Version)> LocalOffers()
    {
        if (LocalOffersProbe != null) return LocalOffersProbe();
        var list = new List<(string, string)>();
        string? folder;
        try { folder = LocalIndexFolderProbe != null ? LocalIndexFolderProbe() : LocalIndexConfig.Resolve(); }
        catch { folder = null; }
        if (string.IsNullOrWhiteSpace(folder)) return list;
        // Review (L5): read again only when the index file changed (each status asks).
        var source = new LocalIndexSource(folder);
        string stamp;
        try
        {
            var info = new FileInfo(source.IndexPath!);
            stamp = info.Exists ? info.FullName + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks : "";
        }
        catch { stamp = ""; }
        if (stamp.Length == 0) return list;
        lock (_offersGate)
            if (string.Equals(stamp, _offersStamp, StringComparison.Ordinal)) return _offers;
        var load = source.Load();
        System.Threading.Interlocked.Increment(ref _offersReads);
        if (load.Ok)
            foreach (var e in load.Entries)
                if (!string.IsNullOrEmpty(e.Key)) list.Add((e.Key, e.Manifest.Version ?? ""));
        lock (_offersGate)
        {
            _offers = list;
            _offersStamp = stamp;
        }

        return list;
    }

    /// <summary>One row per ledgered package on this server, read after promotes are folded in.</summary>
    public IReadOnlyList<RemoteServerRow> ListRows()
    {
        try { ServerInstall.ClearHotFlags(_bepInExRoot); } catch { /* best effort */ }
        try { ServerInstall.ReconcilePromoted(_bepInExRoot); } catch { /* best effort */ }
        var ledger = ReadLedger();
        if (ledger == null) return Array.Empty<RemoteServerRow>();
        string? gameRoot = ServerInstall.GameRootOf(_bepInExRoot);

        var order = new List<string>();
        var byPackage = new Dictionary<string, (string Version, bool Pending, bool Hot, bool Marked, bool AllPending, bool Remote)>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ledger.Files.Count; i++)
        {
            var row = ledger.Files[i];
            if (!InstalledState.TrySplitPackageSource(row.Source, out string package, out string version)) continue;
            // SRVUNINST1: an install record whose file is gone is not reported as installed.
            if (gameRoot != null && ServerInstall.IsOrphanRow(gameRoot, row)) continue;
            bool pending = IsPendingPath(row.Path);
            if (!byPackage.TryGetValue(package, out var acc))
            {
                order.Add(package);
                acc = (version, false, false, false, true, false);
            }

            acc.Pending |= pending;
            acc.AllPending &= pending;
            acc.Hot |= row.Hot;
            acc.Marked |= row.UninstallPending;
            acc.Remote |= string.Equals(row.Route, ServerInstallLedger.RouteRemote, StringComparison.Ordinal);
            if (acc.Version.Length == 0) acc.Version = version;
            byPackage[package] = acc;
        }

        var list = new List<RemoteServerRow>(order.Count);
        foreach (string package in order)
        {
            var acc = byPackage[package];
            var state = acc.Marked ? RemoteRowState.UninstallPending
                : acc.Hot ? RemoteRowState.Hot
                : acc.AllPending ? RemoteRowState.Pending
                : RemoteRowState.Loaded;
            list.Add(new RemoteServerRow(package, acc.Version, state, acc.Remote ? ServerInstallLedger.RouteRemote : ""));
        }

        return list;
    }

    private ServerInstallLedger? ReadLedger()
    {
        string? gameRoot = ServerInstall.GameRootOf(_bepInExRoot);
        return string.IsNullOrEmpty(gameRoot) ? null : ServerInstallLedger.TryRead(gameRoot!);
    }

    /// <summary>
    /// REMOTETEST1: may every loaded row of this listing be unloaded by deleting it? The rule is
    /// <see cref="HotLoadTrust.CanUnloadHot"/>: a verified hotLoadable declaration for this listing
    /// version (this server's inspection this session, else its <c>progenitor-cache/verified</c>),
    /// those exact bytes, and a clean ProgenitorPlugin image. When nothing on this server ever
    /// inspected the version, the package is downloaded and inspected once here — the same fetch an
    /// install does — so the declaration comes from the package, never from the file name.
    /// </summary>
    private async Task<(bool Trusted, string Reason)> TrustedHotCopyAsync(
        InstallCatalogService catalog, CatalogEntry entry, List<ServerInstallLedgerEntry> rows)
    {
        try
        {
            string? game = ServerInstall.GameRootOf(_bepInExRoot);
            if (string.IsNullOrEmpty(game)) return (false, "the server folder is not readable");
            var files = new List<string>();
            foreach (var row in rows)
            {
                if (IsPendingPath(row.Path)) continue;
                files.Add(ServerInstallLedger.Absolute(game!, row.Path));
            }

            if (files.Count == 0) return (false, "no loaded copy");

            var verified = catalog.VerifiedFor(entry);
            if (verified == null)
            {
                var inspected = await catalog.DownloadAndInspectAsync(entry.Key, CancellationToken.None).ConfigureAwait(false);
                if (!inspected.Ok) return (false, "the package could not be inspected: " + (inspected.Error ?? "unknown"));
                verified = catalog.VerifiedFor(entry);
                catalog.ReleaseDownload(entry.Key);
            }

            for (int i = 0; i < files.Count; i++)
            {
                if (!HotLoadTrust.CanUnloadHot(verified, files[i], out string why)) return (false, why);
            }

            return (true, "");
        }
        catch (Exception e)
        {
            return (false, "the trust check threw: " + e.Message);
        }
    }

    private static bool IsPendingPath(string relative) =>
        (relative ?? "").Replace('\\', '/').IndexOf("/" + Networking.ModManifestPending.PendingFolderName + "/", StringComparison.OrdinalIgnoreCase) >= 0
        || (relative ?? "").Replace('\\', '/').StartsWith(Networking.ModManifestPending.PendingFolderName + "/", StringComparison.OrdinalIgnoreCase);

    // ---- reachability ----------------------------------------------------------------------

    /// <summary>
    /// The cheap reachability probe: one bounded GET of the base URL. Any HTTP answer at all means
    /// reachable (the body is not read past <see cref="ProbeMaxBytes"/>); DNS, refusal, timeout and
    /// TLS failures mean unreachable, with the cause. Cached; a new probe runs at most once a minute
    /// unless the cache is empty.
    /// </summary>
    public Task<RemoteReachabilityResult> ProbeAsync(bool force = false)
    {
        lock (_gate)
        {
            var age = _clock.UtcNow - _probe.CheckedUtc;
            if (_probeTask != null && !_probeTask.IsCompleted) return _probeTask;
            if (_probe.State != RemoteReachability.Unknown && (!force || age < ProbeMinInterval))
                return Task.FromResult(_probe);
            _probeTask = RunProbeAsync();
            return _probeTask;
        }
    }

    private async Task<RemoteReachabilityResult> RunProbeAsync()
    {
        var settings = SafeSettings();
        RemoteReachabilityResult result;
        if (!settings.TryBaseUri(out var baseUri))
        {
            result = new RemoteReachabilityResult(RemoteReachability.Unreachable, "config", _clock.UtcNow);
        }
        else
        {
            var probeTransport = new RecordingCatalogTransport(_transport, settings.StallTimeout);
            var fetcher = new CatalogFetcher(probeTransport, new CatalogFetchOptions
            {
                IndexUrl = settings.IndexUrl,
                MaxBytes = ProbeMaxBytes,
                MaxRetries = 0,
                Timeout = settings.ConnectTimeout + settings.StallTimeout,
            }, _clock);
            CatalogBytesFetchResult fetched;
            try { fetched = await fetcher.FetchBytesAsync(baseUri.ToString(), ProbeMaxBytes, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) { fetched = new CatalogBytesFetchResult(false, e.Message, null, 0, 0); }

            var failure = probeTransport.LastFailure;
            bool answered = fetched.Ok || fetched.StatusCode > 0 || failure.Cause == FetchFailureCause.Http;
            result = answered
                ? new RemoteReachabilityResult(RemoteReachability.Reachable, "", _clock.UtcNow)
                : new RemoteReachabilityResult(RemoteReachability.Unreachable,
                    failure.Cause == FetchFailureCause.None ? "network" : failure.Word, _clock.UtcNow);
        }

        lock (_gate) _probe = result;
        return result;
    }

    private void NoteReachability(RemoteReachability state, string detail)
    {
        lock (_gate) _probe = new RemoteReachabilityResult(state, detail, _clock.UtcNow);
    }

    /// <summary>Cancel whatever is running, whatever its stage (the Core is unloading).</summary>
    public void CancelRunning()
    {
        CancellationTokenSource? target;
        lock (_gate) target = _running;
        // Outside the lock: Cancel() runs the download's continuations inline.
        try { target?.Cancel(); }
        catch (Exception) { /* disposed, or a callback threw: the Core is unloading either way */ }
    }

    // ---- requests --------------------------------------------------------------------------

    /// <summary>
    /// Handle one decoded request. A refusal is answered synchronously and returns null; an accepted
    /// operation returns the task running it. <paramref name="send"/> may be called from a worker
    /// thread; the caller marshals it to the sender.
    /// </summary>
    public Task? Handle(RemoteInstallRequest request, RemoteInstallActor actor, Action<RemoteInstallProgress> send)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (send == null) throw new ArgumentNullException(nameof(send));
        var settings = SafeSettings();

        if (request.Op == RemoteInstallOp.Cancel)
        {
            HandleCancel(request, actor, settings, send);
            return null;
        }

        if (request.Op is not (RemoteInstallOp.Install or RemoteInstallOp.Update or RemoteInstallOp.Uninstall))
            return null; // List is answered by StatusFor; the transport routes it.

        bool busy;
        lock (_gate) busy = _busy;
        var refusal = RemoteInstallPolicy.Check(request, actor, settings, Limiter, busy, Now());
        if (refusal != RemoteInstallReason.None)
        {
            Finish(request, actor, send, RemoteInstallStage.Refused, refusal, Detail(refusal, request), Array.Empty<string>());
            return null;
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_busy)
            {
                Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.Busy, Detail(RemoteInstallReason.Busy, request), Array.Empty<string>());
                return null;
            }

            _busy = true;
            cts = new CancellationTokenSource(settings.TotalTimeout + TimeSpan.FromSeconds(30));
            _running = cts;
            _runningStage = RemoteInstallStage.Accepted;
            _runningPackage = request.Package;
            _adminCancelled = false;
            _source = "";
        }

        Progress(request, send, RemoteInstallStage.Accepted);
        return Task.Run(async () =>
        {
            try
            {
                if (request.Op == RemoteInstallOp.Uninstall && RemotePluginRef.IsRef(request.Package))
                    await RunPluginUninstallAsync(request, actor, send).ConfigureAwait(false);
                else if (request.Op == RemoteInstallOp.Uninstall) await RunUninstallAsync(request, actor, send).ConfigureAwait(false);
                else await RunInstallAsync(request, actor, settings, send, cts.Token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Finish(request, actor, send, RemoteInstallStage.Failed, RemoteInstallReason.Internal, e.Message, Array.Empty<string>());
            }
            finally
            {
                try { _catalog?.DiscardDownloads(); } catch { /* best effort */ }
                lock (_gate)
                {
                    _busy = false;
                    _running = null;
                    _runningStage = RemoteInstallStage.None;
                    _runningPackage = "";
                }

                cts.Dispose();
            }
        });
    }

    private void HandleCancel(RemoteInstallRequest request, in RemoteInstallActor actor, RemoteInstallSettings settings, Action<RemoteInstallProgress> send)
    {
        if (!RemoteInstallPolicy.Allowed(settings, actor))
        {
            var reason = !actor.Authorized ? RemoteInstallReason.NotAdmin : !settings.Enabled ? RemoteInstallReason.Disabled : RemoteInstallReason.NotAllowed;
            send(new RemoteInstallProgress(request.Nonce, request.Op, RemoteInstallStage.Refused, reason, request.Package, request.Version, ""));
            return;
        }

        bool cancelled = false;
        CancellationTokenSource? target = null;
        lock (_gate)
        {
            // Once the write step has started the operation runs to its end: a half-staged install
            // is worse than a finished one the admin can uninstall.
            bool samePackage = request.Package.Length == 0
                || string.Equals(request.Package, _runningPackage, StringComparison.OrdinalIgnoreCase);
            if (_running != null && samePackage && _runningStage < RemoteInstallStage.Installing)
            {
                // The flag first: canceling runs the download's continuations inline, and they read it.
                _adminCancelled = true;
                target = _running;
                cancelled = true;
            }
        }

        // Outside the lock: Cancel() runs registered callbacks synchronously.
        if (target != null)
        {
            try { target.Cancel(); }
            catch (ObjectDisposedException) { /* it finished in the meantime */ }
            catch (AggregateException) { /* a callback threw; the operation still sees the flag */ }
        }

        send(new RemoteInstallProgress(request.Nonce, request.Op,
            cancelled ? RemoteInstallStage.Cancelled : RemoteInstallStage.Refused,
            RemoteInstallReason.None, request.Package, request.Version,
            cancelled ? "cancel requested" : "nothing to cancel"));
    }

    private void Progress(RemoteInstallRequest request, Action<RemoteInstallProgress> send, RemoteInstallStage stage, string detail = "")
    {
        lock (_gate)
        {
            if (_busy) _runningStage = stage;
        }

        try { send(new RemoteInstallProgress(request.Nonce, request.Op, stage, RemoteInstallReason.None, request.Package, request.Version, detail)); }
        catch { /* a dead connection costs the progress line, never the operation */ }
    }

    /// <summary>F16: where the running install's listing comes from, for its audit line (empty for other operations).</summary>
    private string _source = "";

    internal const string SourceLocalIndex = "local-index";

    private void Finish(
        RemoteInstallRequest request,
        in RemoteInstallActor actor,
        Action<RemoteInstallProgress> send,
        RemoteInstallStage stage,
        RemoteInstallReason reason,
        string detail,
        IReadOnlyList<string> files)
    {
        try { send(new RemoteInstallProgress(request.Nonce, request.Op, stage, reason, request.Package, request.Version, detail)); }
        catch { /* the audit line still records it */ }
        try { Audit?.Invoke(AuditLine(actor, request, stage, reason, detail, files, _clock.UtcNow, _source)); }
        catch { /* the audit sink must never break an operation */ }
    }

    /// <summary>One audit line: who, what, which package, which version, the result and the files touched.</summary>
    internal static string AuditLine(
        in RemoteInstallActor actor,
        RemoteInstallRequest request,
        RemoteInstallStage stage,
        RemoteInstallReason reason,
        string detail,
        IReadOnlyList<string> files,
        DateTime utc,
        string source = "")
    {
        string result = RemoteInstallWire.StageName(stage);
        if (reason != RemoteInstallReason.None) result += "(" + RemoteInstallWire.ReasonName(reason) + ")";
        string name = (actor.CharacterName ?? "").Replace("\"", "'");
        string text = (detail ?? "").Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
        return "RemoteInstall " + utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
            + " admin=\"" + name + "\" steamId=" + actor.SteamId.ToString(CultureInfo.InvariantCulture)
            + " authorizedBy=" + AuthorizedByName(actor.AuthorizedBy)
            + " op=" + RemoteInstallWire.OpName(request.Op)
            + " package=" + (request.Package.Length > 0 ? request.Package : "-")
            + " version=" + (request.Version.Length > 0 ? request.Version : "-")
            + (string.IsNullOrEmpty(source) ? "" : " source=" + source)
            + " result=" + result
            + (text.Length > 0 ? " detail=\"" + text + "\"" : "")
            + " files=[" + string.Join(";", files ?? Array.Empty<string>()) + "]";
    }

    internal static string AuthorizedByName(RemoteAuthorizedBy by) => by switch
    {
        RemoteAuthorizedBy.AdminList => "adminlist",
        RemoteAuthorizedBy.AdminAuth => "adminauth",
        RemoteAuthorizedBy.Host => "host",
        _ => "none",
    };

    private static string Detail(RemoteInstallReason reason, RemoteInstallRequest request) => reason switch
    {
        RemoteInstallReason.NotAdmin => "only players on this server's admin list (adminlist.txt) can install here",
        RemoteInstallReason.Disabled => "remote installs are off on this server ([RemoteInstall] Enabled)",
        RemoteInstallReason.NotAllowed => "this admin is not in [RemoteInstall] AllowedSteamIds",
        RemoteInstallReason.BadPackage => "not a Thunderstore Namespace-Name package id",
        RemoteInstallReason.BadVersion => "not an exact major.minor.patch version",
        RemoteInstallReason.RateLimited => "too many remote operations; wait a minute",
        RemoteInstallReason.Busy => "another remote operation is running on this server",
        _ => "",
    };

    // ---- install / update ------------------------------------------------------------------

    private async Task RunInstallAsync(
        RemoteInstallRequest request,
        RemoteInstallActor actor,
        RemoteInstallSettings settings,
        Action<RemoteInstallProgress> send,
        CancellationToken ct)
    {
        var none = Array.Empty<string>();
        if (!settings.BaseUrlValid)
        {
            Finish(request, actor, send, RemoteInstallStage.Failed, RemoteInstallReason.ThunderstoreUnreachable,
                "config: [RemoteInstall] ThunderstoreBaseUrl must be an https URL", none);
            return;
        }

        _transport.Reset();
        // TSTRAFFIC1: an admin's install is explicit: it always checks the index (a 304 costs nothing).
        var catalog = await EnsureIndexAsync(settings, ct, force: true).ConfigureAwait(false);
        if (catalog == null)
        {
            if (AdminCancelled)
            {
                Finish(request, actor, send, RemoteInstallStage.Cancelled, RemoteInstallReason.None, "canceled before anything was written", none);
                return;
            }

            // The package index is the metadata fetch: any failure to get it means the server cannot
            // reach Thunderstore for this request. Only a real reachability cause changes what the
            // status advertises to every admin.
            var failure = _transport.LastFailure;
            string word = failure.Cause == FetchFailureCause.None ? "network" : failure.Word;
            if (failure.IsReachability || failure.Cause == FetchFailureCause.None)
                NoteReachability(RemoteReachability.Unreachable, word);
            Finish(request, actor, send, RemoteInstallStage.Failed, RemoteInstallReason.ThunderstoreUnreachable, word, none);
            return;
        }

        string key = request.Package;
        // F16 (live 2026-10-07): a listing this server's own [Install] LocalIndexPath carries is the operator's, so an admin
        // may install it remotely. Only keys that index lists qualify (a client names a key, never a file); inspection,
        // qualification and hot-load admission still apply. With the key empty there are no local keys: Thunderstore only.
        bool local = catalog.IsLocalKey(key);
        // Only a local-index install names its source; a Thunderstore line keeps its format.
        _source = local ? SourceLocalIndex : "";

        if (!catalog.TryGetEntry(key, out var entry) || entry == null)
        {
            Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.NotFound,
                "not on Thunderstore's V Rising index", none);
            return;
        }

        if (!local && !string.IsNullOrEmpty(entry.ListingUrl)
            && entry.ListingUrl!.IndexOf("/c/v-rising/", StringComparison.OrdinalIgnoreCase) < 0)
        {
            Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.NotVRising,
                "the listing is not in the V Rising community", none);
            return;
        }

        string indexVersion = entry.Manifest.Version ?? "";
        if (!string.Equals(indexVersion, request.Version, StringComparison.Ordinal))
        {
            Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.VersionNotFound,
                "the server's index has " + (indexVersion.Length > 0 ? indexVersion : "no version") + "; reopen the Install tab and try again", none);
            return;
        }

        if (!local && !settings.IsThunderstoreUrl(entry.DownloadUrl))
        {
            Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.NotThunderstore,
                "the download link is not on " + settings.ThunderstoreBaseUrl, none);
            return;
        }

        var installed = (HashSet<string>)(await OnMain(() => LedgeredVersions(catalog, entry)).ConfigureAwait(false))!;
        if (installed.Contains(request.Version))
        {
            Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.AlreadyInstalled,
                request.Version + " is already ledgered on this server", none);
            return;
        }

        if (request.Op == RemoteInstallOp.Update && installed.Count == 0)
        {
            Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.NotInstalled,
                "nothing of it is ledgered on this server to update", none);
            return;
        }

        Progress(request, send, RemoteInstallStage.Downloading);
        _transport.Reset();
        InspectCallResult inspected;
        try { inspected = await catalog.DownloadAndInspectAsync(key, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { inspected = new InspectCallResult(false, InstallReasons.FetchCancelled, entry, null); }

        if (AdminCancelled)
        {
            // The admin's Cancel — never a write.
            Finish(request, actor, send, RemoteInstallStage.Cancelled, RemoteInstallReason.None, "canceled before anything was written", none);
            return;
        }

        if (!inspected.Ok)
        {
            if (inspected.Inspection != null)
            {
                Finish(request, actor, send, RemoteInstallStage.Failed, RemoteInstallReason.VerifyFailed,
                    inspected.Error ?? inspected.Inspection.Refusal ?? "the package did not pass inspection", none);
                return;
            }

            ReportFetchFailure(request, actor, send, inspected.Error);
            return;
        }

        Progress(request, send, RemoteInstallStage.Verifying);
        string? zip = catalog.ZipPathOf(key);
        // F16: a listing of this server's own local index is the operator's file: no Thunderstore size to compare.
        if (!local && entry.ListingFileSize is > 0 && zip != null)
        {
            long length;
            try { length = new FileInfo(zip).Length; }
            catch { length = -1; }
            if (length != entry.ListingFileSize.Value)
            {
                Finish(request, actor, send, RemoteInstallStage.Failed, RemoteInstallReason.VerifyFailed,
                    "size mismatch: Thunderstore lists " + entry.ListingFileSize.Value.ToString(CultureInfo.InvariantCulture)
                    + " bytes, received " + length.ToString(CultureInfo.InvariantCulture), none);
                return;
            }
        }

        _transport.Reset();
        InstallPlanSet set;
        try { set = await catalog.PlanWithDependenciesAsync(key, InstallTarget.LocalServer, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { set = InstallPlanSet.Refuse(InstallReasons.FetchCancelled); }
        if (!set.Ok || set.Root == null)
        {
            var failure = _transport.LastFailure;
            if (AdminCancelled)
                Finish(request, actor, send, RemoteInstallStage.Cancelled, RemoteInstallReason.None, "canceled before anything was written", none);
            else if (failure.Cause != FetchFailureCause.None)
                ReportFetchFailure(request, actor, send, set.Error);
            else
                Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.PlanRefused,
                    set.Error ?? InstallReasons.PlanBlocked, none);
            return;
        }

        if (AdminCancelled || ct.IsCancellationRequested)
        {
            Finish(request, actor, send, RemoteInstallStage.Cancelled, RemoteInstallReason.None,
                AdminCancelled ? "canceled before anything was written" : "took longer than the total timeout; nothing was written", none);
            return;
        }

        Progress(request, send, RemoteInstallStage.Installing);
        ExecuteSetResult written;
        try
        {
            // One main-thread step: the before-snapshot, the write and the tag cannot be split by a
            // promote fold-in (a List request) that would make an old row look new.
            written = (ExecuteSetResult)(await OnMain(() =>
            {
                // The first-class restart route only appends to a root that already carries a
                // ledger (a framework install wrote one). A server whose Progenitor was deployed by
                // hand has none, and an unledgered remote install could never be uninstalled
                // remotely, so the remote route creates one first, as the ordinary route would.
                EnsureLedger();
                var before = LedgerKeys();
                var result = catalog.ExecuteWithDependencies(set);
                try { TagRemoteRows(before, set); }
                catch { /* an untagged row is still a ledgered row: it reads as the local route */ }
                return result;
            }).ConfigureAwait(false))!;
        }
        catch (Exception e)
        {
            written = ExecuteSetResult.Refuse(e.Message);
        }

        var files = new List<string>();
        ExecuteMemberResult? root = null;
        for (int i = 0; i < written.Members.Count; i++)
        {
            var member = written.Members[i];
            if (!string.IsNullOrEmpty(member.Path)) files.Add(member.Path!);
            if (!string.IsNullOrEmpty(member.ServerPendingPath)) files.Add(member.ServerPendingPath!);
            if (member.IsRoot) root = member;
        }

        if (!written.Ok || root == null)
        {
            Finish(request, actor, send, RemoteInstallStage.Failed, RemoteInstallReason.InstallFailed,
                written.Error ?? InstallReasons.FailedWriteNothing, files);
            return;
        }

        if (!root.HotLoaded)
        {
            Finish(request, actor, send, RemoteInstallStage.PendingRestart, RemoteInstallReason.None,
                "installs at the next server restart", files);
            return;
        }

        string pluginId = set.Root.PluginId ?? "";
        bool confirmed = await ConfirmLoadedAsync(pluginId).ConfigureAwait(false);
        Finish(request, actor, send, RemoteInstallStage.HotLoaded,
            confirmed ? RemoteInstallReason.None : RemoteInstallReason.HotLoadUnconfirmed,
            confirmed
                ? "loaded by the server's reloader"
                : "written to progenitor-reload; the reloader has not reported " + (pluginId.Length > 0 ? pluginId : "it") + " loaded yet",
            files);
    }

    private bool AdminCancelled
    {
        get { lock (_gate) return _adminCancelled; }
    }

    private void ReportFetchFailure(RemoteInstallRequest request, in RemoteInstallActor actor, Action<RemoteInstallProgress> send, string? error)
    {
        var failure = _transport.LastFailure;
        var none = Array.Empty<string>();
        if (failure.IsReachability)
        {
            NoteReachability(RemoteReachability.Unreachable, failure.Word);
            Finish(request, actor, send, RemoteInstallStage.Failed, RemoteInstallReason.ThunderstoreUnreachable, failure.Word, none);
            return;
        }

        string detail = failure.Cause != FetchFailureCause.None
            ? failure.Word + (failure.Detail.Length > 0 && failure.Cause == FetchFailureCause.Truncated ? ": " + failure.Detail : "")
            : (error ?? InstallReasons.FetchTransport);
        Finish(request, actor, send, RemoteInstallStage.Failed, RemoteInstallReason.DownloadFailed, detail, none);
    }

    private async Task<bool> ConfirmLoadedAsync(string pluginId)
    {
        var probe = PluginLoaded;
        if (probe == null || pluginId.Length == 0) return false;
        var deadline = _clock.UtcNow + HotLoadConfirmWindow;
        while (true)
        {
            // The probe reads the process's plugin inventory, which belongs to the main thread.
            bool loaded;
            try { loaded = (bool)(await OnMain(() => probe(pluginId)).ConfigureAwait(false))!; }
            catch { loaded = false; }
            if (loaded) return true;
            if (_clock.UtcNow >= deadline) return false;
            await _clock.Delay(TimeSpan.FromMilliseconds(500), CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<object?> OnMain(Func<object?> work)
    {
        var marshal = RunOnMain;
        if (marshal == null) return work();
        return await marshal(work).ConfigureAwait(false);
    }

    /// <summary>
    /// The server's catalog with a fresh enough index, or null when the index could not be loaded
    /// (the transport recorded why). Rebuilt when the base URL changed.
    /// </summary>
    private async Task<InstallCatalogService?> EnsureIndexAsync(RemoteInstallSettings settings, CancellationToken ct, bool force = false)
    {
        InstallCatalogService catalog;
        bool fresh;
        lock (_gate)
        {
            // Live 2026-10-10: the settings follow cfg edits, so a changed ThunderstoreBaseUrl reaches the catalog here.
            string catalogKey = settings.IndexUrl + "|" + settings.TotalTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (_catalog == null || !string.Equals(_catalogIndexUrl, catalogKey, StringComparison.Ordinal))
            {
                try { _catalog?.DiscardDownloads(); } catch { /* replaced */ }
                _catalog = _catalogFactory(_transport, settings);
                _catalog.IndexCheckOwner = "remote install";
                _catalogIndexUrl = catalogKey;
                _indexLoadedUtc = DateTime.MinValue;
            }

            catalog = _catalog;
            fresh = _indexLoadedUtc != DateTime.MinValue && _clock.UtcNow - _indexLoadedUtc < IndexMaxAge;
        }

        if (fresh && !force) return catalog;
        CatalogLoadResult load;
        try { load = await catalog.LoadIndexAsync(ct, force).ConfigureAwait(false); }
        catch (OperationCanceledException) { return null; }
        if (!load.Ok) return null;
        NoteReachability(RemoteReachability.Reachable, "");
        lock (_gate) _indexLoadedUtc = _clock.UtcNow;
        return catalog;
    }

    /// <summary>Test seam: drop the cached index so the next operation fetches it again.</summary>
    internal void ForgetIndex()
    {
        lock (_gate) _indexLoadedUtc = DateTime.MinValue;
    }

    /// <summary>The versions of one listing this server's ledger carries, in any spelling.</summary>
    private HashSet<string> LedgeredVersions(InstallCatalogService catalog, CatalogEntry entry)
    {
        var versions = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<string> sources;
        try { sources = ServerInstall.LedgerSources(_bepInExRoot); }
        catch { return versions; }

        // SRVUNINST1 review: an install of this package first clears its records whose files are gone
        // (neither tab shows them as installed), so they never answer "already installed".
        bool dropped = false;
        for (int i = 0; i < sources.Count; i++)
        {
            if (!InstalledState.TrySplitPackageSource(sources[i], out string package, out _)) continue;
            if (!LedgerSpelling.Matches(entry, package, catalog.IdentityProbe)) continue;
            try { dropped |= ServerInstall.DropOrphanRows(_bepInExRoot, package) is { Ok: true, NoOp: false }; }
            catch { /* the check below still reads the ledger as it is */ }
        }

        if (dropped)
        {
            try { sources = ServerInstall.LedgerSources(_bepInExRoot); }
            catch { return versions; }
        }
        for (int i = 0; i < sources.Count; i++)
        {
            if (!InstalledState.TrySplitPackageSource(sources[i], out string package, out string version)) continue;
            if (LedgerSpelling.Matches(entry, package, catalog.IdentityProbe)) versions.Add(version);
        }

        return versions;
    }

    private void EnsureLedger()
    {
        string? gameRoot = ServerInstall.GameRootOf(_bepInExRoot);
        if (string.IsNullOrEmpty(gameRoot) || ServerInstallLedger.TryRead(gameRoot!) != null) return;
        if (File.Exists(ServerInstallLedger.PathFor(gameRoot!))) return; // unreadable: never overwrite it
        // The framework files this server is running, so a client that later links this folder as its
        // local server reads "installed" (or a real update) rather than an empty-hash mismatch.
        new ServerInstallLedger("", HashOrEmpty(gameRoot!, ServerInstall.BootstrapRelative),
            HashOrEmpty(gameRoot!, ServerInstall.CoreRelative), _clock.UtcNow).Write(gameRoot!);
    }

    private static string HashOrEmpty(string gameRoot, string relative)
    {
        try
        {
            string path = ServerInstallLedger.Absolute(gameRoot, relative);
            return File.Exists(path) ? CatalogFetcher.HashFile(path) : "";
        }
        catch
        {
            return "";
        }
    }

    private HashSet<string> LedgerKeys()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ledger = ReadLedger();
        if (ledger == null) return keys;
        for (int i = 0; i < ledger.Files.Count; i++) keys.Add(ledger.Files[i].Path + "|" + ledger.Files[i].Sha256);
        return keys;
    }

    /// <summary>
    /// Mark every row this operation wrote with <see cref="ServerInstallLedger.RouteRemote"/>, and
    /// spell its source with the Thunderstore key (the first-class restart route ledgers under the
    /// plugin id; the row's <c>pluginId</c> field keeps that), so a later remote update or uninstall
    /// finds it by the key the admin's client sends.
    /// </summary>
    private void TagRemoteRows(HashSet<string> before, InstallPlanSet set)
    {
        string? gameRoot = ServerInstall.GameRootOf(_bepInExRoot);
        if (string.IsNullOrEmpty(gameRoot)) return;
        var ledger = ServerInstallLedger.TryRead(gameRoot!);
        if (ledger == null) return;

        var next = new List<ServerInstallLedgerEntry>(ledger.Files.Count);
        bool changed = false;
        for (int i = 0; i < ledger.Files.Count; i++)
        {
            var row = ledger.Files[i];
            if (before.Contains(row.Path + "|" + row.Sha256) || !InstalledState.TrySplitPackageSource(row.Source, out string package, out string version))
            {
                next.Add(row);
                continue;
            }

            string source = row.Source;
            for (int m = 0; m < set.Members.Count; m++)
            {
                var member = set.Members[m];
                bool byKey = string.Equals(package, member.Package, StringComparison.OrdinalIgnoreCase);
                bool byId = !string.IsNullOrEmpty(member.Plan.PluginId)
                    && string.Equals(package, member.Plan.PluginId, StringComparison.OrdinalIgnoreCase);
                if (!byKey && !byId) continue;
                source = ServerInstallLedger.PackageSourcePrefix + member.Package + "@" + (version.Length > 0 ? version : member.Version);
                break;
            }

            next.Add(new ServerInstallLedgerEntry(row.Path, row.Sha256, row.Size, row.Category, source, row.WrittenUtc,
                row.UninstallPending, row.Hot, row.PluginId, ServerInstallLedger.RouteRemote));
            changed = true;
        }

        if (changed)
            new ServerInstallLedger(ledger.Version, ledger.BootstrapSha256, ledger.CoreSha256, ledger.InstalledUtc, next).Write(gameRoot!);
    }

    // ---- uninstall -------------------------------------------------------------------------

    private async Task RunUninstallAsync(RemoteInstallRequest request, RemoteInstallActor actor, Action<RemoteInstallProgress> send)
    {
        var settings = SafeSettings();
        // The index is only needed for the other spellings of a row; an unreachable Thunderstore
        // must not stop an uninstall, so a failed load falls back to the key spelling.
        InstallCatalogService? catalog = null;
        if (settings.BaseUrlValid)
        {
            _transport.Reset();
            try { catalog = await EnsureIndexAsync(settings, CancellationToken.None).ConfigureAwait(false); }
            catch { catalog = null; }
        }

        CatalogEntry? entry = null;
        if (catalog != null && catalog.TryGetEntry(request.Package, out var found)) entry = found;

        var rows = (List<ServerInstallLedgerEntry>)(await OnMain(() => MatchingRows(request.Package, entry, catalog)).ConfigureAwait(false))!;
        if (rows.Count == 0)
        {
            Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.NotInstalled,
                "nothing of it is ledgered on this server", Array.Empty<string>());
            return;
        }

        // SRVUNINST1: an install record whose files are already gone has nothing to delete and
        // nothing that could depend on files that are not there: the record is dropped, no index check.
        string? orphanRoot = ServerInstall.GameRootOf(_bepInExRoot);
        if (orphanRoot != null && rows.TrueForAll(r => ServerInstall.IsOrphanRow(orphanRoot, r)))
        {
            var dropped = (ServerInstallResult)(await OnMain(() =>
            {
                // One drop per spelling the matched rows carry (the key, or an older plugin-id spelling).
                var removed = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in rows)
                {
                    if (!InstalledState.TrySplitPackageSource(r.Source, out string p, out _) || !seen.Add(p)) continue;
                    var one = ServerInstall.DropOrphanRows(_bepInExRoot, p);
                    if (!one.Ok) return one;
                    removed.AddRange(one.Removed);
                }

                return new ServerInstallResult(true, "", removed, Array.Empty<string>(), noOp: removed.Count == 0, hotUnloaded: false);
            }).ConfigureAwait(false))!;
            if (dropped.Ok)
                Finish(request, actor, send, RemoteInstallStage.Removed, RemoteInstallReason.None,
                    "its files were already gone; only the install record was left, and it was cleared", dropped.Removed);
            else
                Finish(request, actor, send, RemoteInstallStage.Failed, RemoteInstallReason.InstallFailed,
                    string.IsNullOrEmpty(dropped.Message) ? "the install record could not be cleared" : dropped.Message, Array.Empty<string>());
            return;
        }

        bool allMarked = true, anyHot = false, allPending = true;
        foreach (var row in rows)
        {
            allMarked &= row.UninstallPending;
            anyHot |= row.Hot;
            allPending &= IsPendingPath(row.Path);
        }

        // REMOTETEST1 / HOTDEMO1 (user policy 2026-10-03, "trust the hotLoadable flag"): a loaded copy
        // in this server's progenitor-reload/ whose bytes are a verified hotLoadable, clean
        // ProgenitorPlugin goes now — deleting it is the unload, by this server's own reloader —
        // exactly as the local-folder route already does. Live (2026-10-03) HotDemo was marked for
        // the next restart instead. A copy already marked earlier is taken now too.
        bool trusted = false;
        string trustWhy = "";
        if (!anyHot && !allPending && catalog != null && entry != null)
        {
            var verdict = await TrustedHotCopyAsync(catalog, entry, rows).ConfigureAwait(false);
            trusted = verdict.Trusted;
            trustWhy = verdict.Reason;
            InstallLog.Info("Install: remote uninstall of " + entry.Key + ": "
                + (trusted ? "trusted hotLoadable copy, unloaded now by this server's reloader" : "marked for the next start (" + trustWhy + ")") + ".");
        }

        if (allMarked && !trusted)
        {
            Finish(request, actor, send, RemoteInstallStage.PendingRestart, RemoteInstallReason.None,
                "already marked for removal at the next start", Array.Empty<string>());
            return;
        }

        if (catalog == null && !request.Force)
        {
            // Dependents come from each installed package's index listing. Without the index nothing
            // can say what this removal would break, so it needs the admin's explicit force.
            // SRVUNINST1 (live 2026-10-07: HotDemo, a package the live index does not carry, was refused
            // with this sentence while the server could reach Thunderstore): only an index that could
            // not be loaded lands here, and the sentence says why it could not.
            Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.Unavailable,
                IndexUnreadableDetail(settings), Array.Empty<string>());
            return;
        }

        // SRVUNINST1: a package the loaded index does not list (a local or delisted package) is still
        // checked against the other installed packages' own listings; its rows go by their source.
        if (entry == null && catalog != null && !request.Force)
        {
            var dependents = (List<string>)(await OnMain(() =>
            {
                var found = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in rows)
                {
                    if (!InstalledState.TrySplitPackageSource(r.Source, out string p, out _) || !seen.Add(p)) continue;
                    foreach (string d in catalog.DependentsOf(p))
                        if (!found.Exists(x => string.Equals(x, d, StringComparison.OrdinalIgnoreCase))) found.Add(d);
                }

                return found;
            }).ConfigureAwait(false))!;
            if (dependents.Count > 0)
            {
                Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.HasDependents,
                    "needed by " + string.Join(", ", dependents), Array.Empty<string>());
                return;
            }
        }

        bool removeNow = anyHot || allPending || trusted;
        Progress(request, send, RemoteInstallStage.Installing);
        ServerInstallResult result;
        try
        {
            result = (ServerInstallResult)(await OnMain(() =>
            {
                try { ServerInstall.ClearHotFlags(_bepInExRoot); } catch { /* best effort */ }
                if (catalog != null && entry != null)
                {
                    return removeNow
                        ? catalog.RemovePackage(entry.Key, request.Force, InstallTarget.LocalServer)
                        : catalog.MarkUninstall(entry.Key, request.Force, InstallTarget.LocalServer);
                }

                return RemoveBySource(rows, removeNow);
            }).ConfigureAwait(false))!;
        }
        catch (Exception e)
        {
            result = ServerInstallResult.Refuse(e.Message);
        }

        if (result.Dependents.Count > 0 && !result.Ok)
        {
            Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.HasDependents,
                "needed by " + string.Join(", ", result.Dependents), Array.Empty<string>());
            return;
        }

        if (!result.Ok || result.NoOp)
        {
            Finish(request, actor, send, RemoteInstallStage.Failed, RemoteInstallReason.InstallFailed,
                result.Message ?? "nothing was removed", result.Removed);
            return;
        }

        Finish(request, actor, send,
            removeNow ? RemoteInstallStage.Removed : RemoteInstallStage.PendingRestart,
            RemoteInstallReason.None,
            removeNow ? (anyHot || trusted ? "unloaded by the server's reloader" : "the pending copy was deleted") : "removed at the next server restart",
            result.Removed);
    }

    /// <summary>
    /// SRVUNINST1: the refusal of an uninstall whose dependency check could not run, with the cause.
    /// <see cref="RemoteUninstallForce.Offers"/> recognizes it, and the client offers "Uninstall anyway".
    /// </summary>
    internal string IndexUnreadableDetail(RemoteInstallSettings settings)
    {
        string cause = !settings.BaseUrlValid ? "its ThunderstoreBaseUrl setting is not an https URL"
            : _transport.LastFailure.Cause == FetchFailureCause.None ? "the download failed"
            : "the download failed (" + _transport.LastFailure.Word + ")";
        return RemoteUninstallForce.IndexUnreadablePrefix + cause + ", so it cannot check what else needs this mod";
    }

    // ---- SRVUNINST1: uninstall by plugin -----------------------------------------------------

    /// <summary>
    /// The plugins this server knows of: the loaded ones (<see cref="PluginFacts"/>) and every plugin a
    /// top-level DLL in <c>progenitor-reload</c> declares, each with its reload file, whether it is
    /// Progenitor's own and whether a preloader removal is already waiting. Main thread.
    /// </summary>
    internal IReadOnlyList<ServerPluginFact> CurrentPluginFacts(bool fresh = false)
    {
        IReadOnlyList<ServerPluginFact> loaded;
        var source = fresh ? PluginFactsFresh ?? PluginFacts : PluginFacts;
        try { loaded = source?.Invoke() ?? Array.Empty<ServerPluginFact>(); }
        catch { loaded = Array.Empty<ServerPluginFact>(); }
        var reload = Ui.ReloadFolderPlugins.Map(Path.Combine(_bepInExRoot, InstalledState.ReloadFolder), ReloadGuids);
        PluginActions.PluginFileState? state = null;
        try { if (PluginActions.PluginFileActions.TryRead(_bepInExRoot, out var read, out _)) state = read; }
        catch { state = null; }
        string plugins = PluginActions.PluginFileActions.PluginsPath(_bepInExRoot);

        // Who hard-depends on whom, among the loaded plugins (the chainloader's report).
        var neededBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fact in loaded)
        {
            if (fact == null || string.IsNullOrWhiteSpace(fact.Id)) continue;
            foreach (string dep in fact.HardDependencies)
            {
                if (string.IsNullOrWhiteSpace(dep) || string.Equals(dep, fact.Id, StringComparison.OrdinalIgnoreCase)) continue;
                if (!neededBy.TryGetValue(dep, out var list)) neededBy[dep] = list = new List<string>();
                if (!list.Contains(fact.Id)) list.Add(fact.Id);
            }
        }

        IReadOnlyList<string> NeededBy(string id) =>
            neededBy.TryGetValue(id, out var l) ? l : (IReadOnlyList<string>)Array.Empty<string>();
        IReadOnlyList<string> AlsoIn(string file, string id) =>
            file.Length == 0 ? Array.Empty<string>() : Ui.ReloadFolderPlugins.AlsoDeclaredBy(file, id, ReloadGuids);

        var facts = new List<ServerPluginFact>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fact in loaded)
        {
            if (fact == null || string.IsNullOrWhiteSpace(fact.Id) || !seen.Add(fact.Id)) continue;
            string file = reload.TryGetValue(fact.Id, out string? f) ? f : "";
            bool fromPlugins = false;
            try { fromPlugins = fact.Location.Length > 0 && PluginActions.PluginFileActions.IsStrictlyUnder(plugins, fact.Location); }
            catch { fromPlugins = false; }
            facts.Add(new ServerPluginFact
            {
                Id = fact.Id,
                Name = fact.Name,
                Version = fact.Version,
                Location = fact.Location,
                ReloadFile = file,
                Framework = fact.Framework || Ui.ModMenuInventory.IsFrameworkId(fact.Id),
                Plain = fact.Plain,
                RemovalRequested = fact.RemovalRequested || RemovalWaiting(state, fact.Id),
                HardDependencies = fact.HardDependencies,
                AlsoDeclares = AlsoIn(file, fact.Id),
                NeededBy = NeededBy(fact.Id),
                LoadedFromPlugins = fromPlugins,
            });
        }

        foreach (var pair in reload)
        {
            if (!seen.Add(pair.Key)) continue;
            string version = "";
            try { version = PluginActions.PluginFileActions.PluginVersionIn(pair.Value, pair.Key); }
            catch { version = ""; }
            facts.Add(new ServerPluginFact
            {
                Id = pair.Key,
                Name = pair.Key,
                Version = version,
                ReloadFile = pair.Value,
                Framework = Ui.ModMenuInventory.IsFrameworkId(pair.Key),
                RemovalRequested = RemovalWaiting(state, pair.Key),
                AlsoDeclares = AlsoIn(pair.Value, pair.Key),
                NeededBy = NeededBy(pair.Key),
            });
        }

        return facts;
    }

    private static bool RemovalWaiting(PluginActions.PluginFileState? state, string id) =>
        state != null && PluginActions.PluginFileActions.PendingFor(state, id)?.Action == PluginActions.PluginFileAction.Remove;

    /// <summary>
    /// Every ledgered file on this server by absolute path, with its package's row state.
    /// <paramref name="rows"/> are this pass's <see cref="ListRows"/> (read again when null).
    /// </summary>
    internal List<ServerLedgerFile> LedgerFiles(IReadOnlyList<RemoteServerRow>? rows = null)
    {
        var files = new List<ServerLedgerFile>();
        string? game = ServerInstall.GameRootOf(_bepInExRoot);
        var ledger = ReadLedger();
        if (string.IsNullOrEmpty(game) || ledger == null) return files;
        var states = new Dictionary<string, RemoteRowState>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows ?? ListRows()) states[row.Package] = row.State;
        foreach (var row in ledger.Files)
        {
            if (!InstalledState.TrySplitPackageSource(row.Source, out string package, out _)) continue;
            string absolute;
            try { absolute = ServerInstallLedger.Absolute(game!, row.Path); }
            catch { continue; }
            files.Add(new ServerLedgerFile(absolute, package, states.TryGetValue(package, out var st) ? st : RemoteRowState.Loaded));
        }

        return files;
    }

    /// <summary>
    /// SRVUNINST1: an Uninstall addressed by plugin GUID (<see cref="RemotePluginRef"/>). The server
    /// identifies its own copy now (ledger, reload-folder file, ordinary plugin) and does exactly
    /// what the status row it reported promised: the package uninstall, a delete the reloader turns
    /// into an unload, or a preloader request for the next start. Anything else is refused with the
    /// identification's reason, and nothing is touched.
    /// </summary>
    private async Task RunPluginUninstallAsync(RemoteInstallRequest request, RemoteInstallActor actor, Action<RemoteInstallProgress> send)
    {
        var none = Array.Empty<string>();
        if (!RemotePluginRef.TryGuid(request.Package, out string guid))
        {
            Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.BadPackage, "not a plugin id", none);
            return;
        }

        var found = (PluginLookup)(await OnMain(() =>
        {
            ServerPluginFact? fact = null;
            foreach (var f in CurrentPluginFacts(fresh: true))
                if (string.Equals(f.Id, guid, StringComparison.OrdinalIgnoreCase)) { fact = f; break; }
            return new PluginLookup(fact, fact == null ? default : ServerPluginRemovals.Decide(fact, LedgerFiles()));
        }).ConfigureAwait(false))!;

        var target = found.Fact;
        var verdict = found.Verdict;
        if (target == null)
        {
            Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.NotInstalled,
                "this server has no plugin " + guid + " loaded or in its reload folder", none);
            return;
        }

        InstallLog.Info("Install: remote uninstall of plugin " + guid + ": " + verdict.Removal
            + (verdict.Package.Length > 0 ? " (" + verdict.Package + ")" : "")
            + (verdict.Why.Length > 0 ? " (" + verdict.Why + ")" : "") + ".");
        switch (verdict.Removal)
        {
            case ServerPluginRemoval.Ledger:
                // The ledger owns these files: the package uninstall decides marked or now.
                await RunUninstallAsync(new RemoteInstallRequest(request.Nonce, RemoteInstallOp.Uninstall, verdict.Package, "", request.Force),
                    actor, send).ConfigureAwait(false);
                return;

            case ServerPluginRemoval.ReloadNow:
            {
                // SRVUNINST1 review: a loaded plugin that hard-depends on this one breaks; only force says so.
                if (target.NeededBy.Count > 0 && !request.Force)
                {
                    Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.HasDependents,
                        "needed by " + string.Join(", ", target.NeededBy), none);
                    return;
                }

                Progress(request, send, RemoteInstallStage.Installing);
                string folder = Path.Combine(_bepInExRoot, InstalledState.ReloadFolder);
                ServerInstallResult result;
                try
                {
                    result = (ServerInstallResult)(await OnMain(() =>
                        Ui.ReloadFolderPlugins.Remove(folder, guid, target.ReloadFile, ReloadGuids, refuseShared: true)).ConfigureAwait(false))!;
                }
                catch (Exception e) { result = ServerInstallResult.Refuse(e.Message); }

                if (!result.Ok)
                {
                    Finish(request, actor, send, RemoteInstallStage.Failed, RemoteInstallReason.InstallFailed,
                        string.IsNullOrEmpty(result.Message) ? "nothing was removed" : result.Message, result.Removed);
                    return;
                }

                Finish(request, actor, send, RemoteInstallStage.Removed, RemoteInstallReason.None,
                    "deleted " + Path.GetFileName(target.ReloadFile) + " from BepInEx/" + InstalledState.ReloadFolder
                    + "; the server's reloader unloads it", result.Removed);
                return;
            }

            case ServerPluginRemoval.RemovalPending:
                Finish(request, actor, send, RemoteInstallStage.PendingRestart, RemoteInstallReason.None,
                    "already requested for the server's next start", none);
                return;

            case ServerPluginRemoval.NextStart:
            {
                var plainRequest = ServerPluginRemovals.RemoveRequest(target, _clock.UtcNow);
                string error = "";
                bool ok = false;
                if (plainRequest != null)
                {
                    try
                    {
                        ok = (bool)(await OnMain(() =>
                        {
                            bool written = PluginActions.PluginFileActions.Request(_bepInExRoot, plainRequest, out string e);
                            error = e ?? "";
                            return written;
                        }).ConfigureAwait(false))!;
                    }
                    catch (Exception e) { error = e.Message; }
                }

                if (!ok)
                {
                    Finish(request, actor, send, RemoteInstallStage.Failed, RemoteInstallReason.InstallFailed,
                        error.Length > 0 ? error : "the next-start request could not be written", none);
                    return;
                }

                Finish(request, actor, send, RemoteInstallStage.PendingRestart, RemoteInstallReason.None,
                    "moved to BepInEx/progenitor-removed at the server's next start", none);
                return;
            }

            default:
                Finish(request, actor, send, RemoteInstallStage.Refused, RemoteInstallReason.Unavailable,
                    "managed elsewhere: " + verdict.Why, none);
                return;
        }
    }

    private sealed class PluginLookup
    {
        public PluginLookup(ServerPluginFact? fact, ServerPluginVerdict verdict)
        {
            Fact = fact;
            Verdict = verdict;
        }

        public ServerPluginFact? Fact { get; }
        public ServerPluginVerdict Verdict { get; }
    }

    private List<ServerInstallLedgerEntry> MatchingRows(string key, CatalogEntry? entry, InstallCatalogService? catalog)
    {
        var rows = new List<ServerInstallLedgerEntry>();
        try { ServerInstall.ReconcilePromoted(_bepInExRoot); } catch { /* best effort */ }
        var ledger = ReadLedger();
        if (ledger == null) return rows;
        for (int i = 0; i < ledger.Files.Count; i++)
        {
            var row = ledger.Files[i];
            if (!InstalledState.TrySplitPackageSource(row.Source, out string package, out _)) continue;
            bool match = entry != null && catalog != null
                ? LedgerSpelling.Matches(entry, package, catalog.IdentityProbe)
                : string.Equals(package, key, StringComparison.OrdinalIgnoreCase);
            if (match) rows.Add(row);
        }

        return rows;
    }

    private ServerInstallResult RemoveBySource(List<ServerInstallLedgerEntry> rows, bool removeNow)
    {
        var removed = new List<string>();
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool noOp = true;
        string message = "";
        foreach (var row in rows)
        {
            if (!seen.Add(row.Source)) continue;
            var one = removeNow ? ServerInstall.RemovePackage(_bepInExRoot, row.Source) : UninstallMarkers.Mark(_bepInExRoot, row.Source);
            removed.AddRange(one.Removed);
            skipped.AddRange(one.Skipped);
            if (!one.Ok) return one;
            if (one.NoOp) continue;
            noOp = false;
            message = one.Message;
        }

        return new ServerInstallResult(true, message, removed, skipped, noOp, hotUnloaded: false, dependents: Array.Empty<string>());
    }
}

/// <summary>
/// REMOTEINST1: the audit file, <c>BepInEx/progenitor-remote-install.log</c>. One line per finished
/// operation, appended; the file rolls to <c>.1</c> past <see cref="MaxBytes"/> so a busy server
/// cannot grow it without bound.
/// </summary>
internal static class RemoteInstallAuditLog
{
    public const string FileName = "progenitor-remote-install.log";
    public const long MaxBytes = AuditLogFile.MaxBytes;

    public static string PathFor(string bepInExRoot) => Path.Combine(bepInExRoot, FileName);

    public static void Append(string bepInExRoot, string line) => AuditLogFile.Append(bepInExRoot, FileName, line);
}
