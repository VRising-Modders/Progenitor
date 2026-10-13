using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.SelfUpdate;

namespace Progenitor.Install;

/// <summary>
/// AUTOUPDATE5 (M6.5): Progenitor updates itself. Detection reads Progenitor's own versions from the index body this process
/// already cached (<see cref="SelfIndex"/>; no request of its own). A release is applied through the version store for
/// Progenitor (<see cref="SelfStore"/>): the running copy and the new release are kept, then
/// <list type="bullet">
/// <item>a Core-only release whose protocol matches the running Bootstrap is staged as <c>progenitor-core/Progenitor.dll.next</c>
/// and swapped in place in the apply window (client: the title screen, <see cref="ApplyWindow"/>; server: no player online),
/// and confirmed from Bootstrap's reload status and the loaded image; a swap that does not load it is a failed version;</item>
/// <item>a release with a new Bootstrap stages <c>Progenitor.Bootstrap.dll.next</c>; Bootstrap puts the release's Core in place
/// at the next launch and counts its starts (<see cref="BootHealth"/>), restoring the previous copy when it does not start.</item>
/// </list>
/// Core marks a release healthy (a client running 30 s, a server with a loaded world), which ends its trial and writes the
/// self-install record. Game-free: the live probes are set by <c>SelfUpdatesLive</c>.
/// </summary>
internal static partial class SelfUpdates
{
    public const string LogPrefix = "AutoUpdate: progenitor ";

    private const string DownloadError = "download: ";

    /// <summary>A client is healthy after running this long; a server also needs a loaded world.</summary>
    public const double HealthySeconds = 30;

    /// <summary>A requested swap that Bootstrap never reported is given up after this long.</summary>
    public const double SwapGiveUpSeconds = 90;

    private static readonly object Gate = new();
    private static readonly HashSet<string> Attempted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Releases whose images were read this session and change a protocol (version → what changed): breaking, so notify only
    /// (decided 2026-10-09). Also kept in <see cref="SelfHeldFile"/> (requested 2026-10-09), so a later session shows
    /// the release as held without downloading it again.
    /// </summary>
    private static readonly Dictionary<string, string> ProtocolChanges = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> History = new();
    private static SelfOffer _offer = SelfOffer.None;
    private static List<SelfRelease> _releases = new();
    private static string _indexStamp = "";
    private static bool _detecting;
    private static bool _running;
    private static string _runState = "idle";
    private static string _lastResult = "";
    private static double _nextDetect;
    private static bool _journalBusy;
    private static CancellationTokenSource _cancel = new();
    private static double _nextRecompute;
    private static double _nextStagedCheck;

    /// <summary>
    /// A click (the Install tab's Update, the bridge's <c>self_apply</c>) that the apply window deferred: that version is
    /// installed as a click once the window opens (live 2026-10-10: a server's click deferred by its empty wait was dropped,
    /// because a server's Notify offer is never applied by a pass). Empty when none waits. This session only.
    /// </summary>
    private static string _deferredClick = "";

    /// <summary>The placement hold the current offer was computed with (C4), so a change recomputes it.</summary>
    private static string _offerHold = "";

    /// <summary>The offer is recomputed this often without an index change (a setting or a file changed).</summary>
    public const double RecomputeSeconds = 15;
    private static double _started = -1;
    private static int _version;

    internal static Func<string?>? RootProbe { get; set; }
    internal static Func<bool> IsServerProbe { get; set; } = () => false;

    /// <summary>The running copy's facts (versions, image shas, record, ledger, flags, mode); the state file is read here.</summary>
    internal static Func<SelfFacts>? FactsProbe { get; set; }

    /// <summary>The cached index body's path (the index URL this process reads), or null.</summary>
    internal static Func<string?>? IndexPathProbe { get; set; }

    /// <summary>
    /// <b>Check now</b> (the bridge's <c>self_check</c> with <c>fetch</c>): one conditional index request of this process's own
    /// (owner <c>auto-update</c>, its failure backoff respected), then detection. Returns what happened.
    /// </summary>
    internal static Func<CancellationToken, Task<string>>? CheckIndex { get; set; }

    /// <summary>Check now: one conditional request, then detection on the body it leaves in the cache.</summary>
    public static Task<string> CheckNow()
    {
        var check = CheckIndex;
        if (check == null) return Task.FromResult("no index check here");
        Task<string> t;
        try { t = check(CancellationToken.None); }
        catch (Exception e) { t = Task.FromResult("failed: " + e.Message); }
        return t.ContinueWith(r =>
        {
            string line = r.Status == TaskStatus.RanToCompletion ? r.Result : "failed: " + (r.Exception?.GetBaseException().Message ?? "cancelled");
            PostMain(() =>
            {
                Write("op=check result=\"" + line + "\"");
                lock (Gate) _indexStamp = "";
                Detect("check");
            });
            return line;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Download a release (https on Thunderstore's hosts, or the dev stand-in), at most the given bytes.</summary>
    internal static Func<string, long, CancellationToken, Task<(byte[]? Bytes, string Error)>>? Download { get; set; }

    /// <summary>Ask Bootstrap for a Core swap (reason); returns its answer (<c>scheduled …</c> or a refusal).</summary>
    internal static Func<string, string>? RequestSwap { get; set; }

    /// <summary>Bootstrap's reload status: in flight, the last outcome and its reason.</summary>
    internal static Func<(bool InFlight, string Outcome, string Reason)>? ReloadStatus { get; set; }

    /// <summary>Why applying now is not allowed (empty: the window is open). Client: the apply window; server: empty for <c>[AutoUpdate] EmptyMinutes</c>.</summary>
    internal static Func<string>? WindowClosed { get; set; }

    /// <summary>The health condition beyond the running time (a server: a loaded world).</summary>
    internal static Func<bool> WorldReady { get; set; } = () => true;

    /// <summary>The hash allowlist's verdict for a file (true: no list, or listed).</summary>
    internal static Func<string, string, bool> Allowed { get; set; } = (_, _) => true;

    /// <summary>The running Bootstrap's file (for its <c>.next</c>).</summary>
    internal static Func<string?>? BootstrapPathProbe { get; set; }

    /// <summary>The running Core image (to keep it in the store before an update).</summary>
    internal static Func<byte[]?>? RunningCoreBytes { get; set; }

    internal static Action<Action> PostMain { get; set; } = a => a();
    internal static Func<Func<List<SelfRelease>>, Task<List<SelfRelease>>> RunOff { get; set; } = work => Task.Run(work);
    internal static Action<string>? Log { get; set; }

    /// <summary>The title screen's notice: updated, failed, detail (live: <see cref="ClientAutoUpdate.SetNotice"/>).</summary>
    internal static Action<int, int, string>? Notice { get; set; }

    internal static Func<double> Clock { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>Wall-clock time for the held notes' age.</summary>
    internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public static SelfOffer Offer { get { lock (Gate) return _offer; } }

    /// <summary>Moves when the offer or a run changes (the Install tab rebuilds on it).</summary>
    public static int Version { get { lock (Gate) return _version; } }

    public static bool Busy { get { lock (Gate) return _running; } }

    public static string RunState { get { lock (Gate) return _runState; } }

    /// <summary>
    /// The title screen line's share: 1 while a Progenitor update waits for the player (Notify, a protocol change, a hand or
    /// mod-manager install); 0 for an automatic one (it applies itself) and for a development build (it only reports).
    /// </summary>
    public static int WaitingCount
    {
        get
        {
            var o = Offer;
            if (!o.Available || o.Auto || o.Kind == SelfInstallKind.Development || o.Held.Length > 0) return 0;
            return 1;
        }
    }

    /// <summary>Progenitor first, then mods: an automatic mod pass waits while Progenitor's own update is due or running.</summary>
    public static bool HoldsModPasses
    {
        get
        {
            // C4 review fix: while a build is placed for a server Progenitor's own update waits (PlacementHold), so it
            // must not hold the mod passes either.
            if (PlacementHold().Length > 0) return false;
            lock (Gate)
            {
                if (_running) return true;
                if (_journalBusy) return false;
                return _offer.Auto && _offer.Release != null && !Attempted.Contains(_offer.Release.Version);
            }
        }
    }

    private static string? Root()
    {
        try { return RootProbe?.Invoke(); }
        catch { return null; }
    }

    private static void Remember(string line)
    {
        lock (Gate)
        {
            History.Add(DateTime.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + line);
            if (History.Count > 40) History.RemoveAt(0);
        }
    }

    private static void Write(string line)
    {
        Remember(line);
        try { Log?.Invoke(LogPrefix + line); }
        catch { /* */ }
    }

    private static void Bump()
    {
        lock (Gate) _version++;
    }

    private static SelfFacts? _factsCache;
    private static double _factsAt = double.MinValue;

    /// <summary>The probe's facts are read at most every <see cref="FactsSeconds"/> (it hashes and reads files); the state file always fresh.</summary>
    public const double FactsSeconds = 3;

    public static SelfFacts Facts()
    {
        SelfFacts f;
        double now = Clock();
        SelfFacts? cached;
        lock (Gate) cached = now - _factsAt < FactsSeconds && now >= _factsAt ? _factsCache : null;
        if (cached != null) f = Copy(cached);
        else
        {
            try { f = FactsProbe?.Invoke() ?? new SelfFacts(); }
            catch { f = new SelfFacts(); }
            lock (Gate)
            {
                _factsCache = Copy(f);
                _factsAt = now;
            }
        }

        string? root = Root();
        f.State = string.IsNullOrEmpty(root) ? new SelfState() : SelfFiles.ReadState(root!);
        List<SelfRelease> releases;
        Dictionary<string, string> changes;
        lock (Gate)
        {
            releases = _releases;
            changes = new Dictionary<string, string>(ProtocolChanges, StringComparer.OrdinalIgnoreCase);
        }

        // The held notes of earlier sessions that still apply (this session's own reading wins).
        if (!string.IsNullOrEmpty(root) && releases.Count > 0)
            foreach (var kv in SelfHeldStore.Read(root!).Changes(releases, BootstrapLinkProtocol(), HandshakeProtocol(), SafeUtcNow()))
                if (!changes.ContainsKey(kv.Key)) changes[kv.Key] = kv.Value;
        f.ProtocolChanges = changes;
        return f;
    }

    private static SelfFacts Copy(SelfFacts f) => new()
    {
        IsServer = f.IsServer,
        CoreVersion = f.CoreVersion,
        BootstrapVersion = f.BootstrapVersion,
        CoreSha = f.CoreSha,
        BootstrapSha = f.BootstrapSha,
        Record = f.Record,
        LedgerFramework = f.LedgerFramework,
        ModManager = f.ModManager,
        DevBridge = f.DevBridge,
        DevOverride = f.DevOverride,
        Mode = f.Mode,
    };

    /// <summary>Drop the cached facts (after a write that changes them).</summary>
    public static void Invalidate()
    {
        lock (Gate) _factsAt = double.MinValue;
    }

    // ---- the tick -------------------------------------------------------------------------------------------

    /// <summary>Main thread, about once a second (both sides): confirm a swap, mark health, detect, apply.</summary>
    public static string Tick()
    {
        double now = Clock();
        lock (Gate) if (_started < 0) _started = now;
        string? root = Root();
        if (string.IsNullOrEmpty(root)) return "no BepInEx folder";
        var state = SelfFiles.ReadState(root!);
        var j = state.Journal;
        lock (Gate) _journalBusy = j != null && j.InProgress;
        if (j != null && j.State == SelfJournal.Swapping) ConfirmSwap(root!, j);
        else if (j != null && j.State == SelfJournal.Trial) MarkHealth(root!, j, now);
        else if (j != null && j.State == SelfJournal.Staged && now >= _nextStagedCheck)
        {
            lock (Gate) _nextStagedCheck = now + 10;
            CheckStaged(root!, j);
        }

        DetectIfChanged(now);
        // Mod compatibility C4: a build placed for a server (client only): commit a prepared placement, notice that it
        // loaded, put the player's own copy back after the join it was for.
        string placed = IsServer() ? "" : TickPlacement(root!);
        // C4: the offer's readback follows the placement hold (a placement began or ended).
        string holds = PlacementHold();
        bool holdMoved;
        lock (Gate) holdMoved = !string.Equals(holds, _offerHold, StringComparison.Ordinal);
        if (holdMoved) Recompute("placement");
        if (placed.Length > 0) return placed;
        return AutoApply(holds);
    }

    private static string AutoApply(string holds)
    {
        SelfOffer offer;
        // C4: no automatic update while a build is placed for a server (it would undo the placement).
        if (holds.Length > 0) return holds;
        string deferred;
        lock (Gate)
        {
            offer = _offer;
            if (_running) return "running";
            deferred = _deferredClick;
        }

        if (deferred.Length > 0) return ApplyDeferredClick(offer, deferred);
        lock (Gate)
        {
            if (!offer.Auto || offer.Release == null) return "nothing due";
            if (Attempted.Contains(offer.Release.Version)) return "attempted this session";
        }

        var facts = Facts();
        if (facts.State.Journal is { InProgress: true } busy) return "an update is in progress (" + busy.State + ")";
        string closed = SafeWindow();
        if (closed.Length > 0) return "window closed: " + closed;
        return Begin(offer, "auto", byClick: false, out string message) ? "started: " + message : "not started: " + message;
    }

    /// <summary>
    /// A click the window deferred: installed as a click once the window opens, while the offer still names that version and
    /// a click may install it; dropped (and logged) when the offer moved on.
    /// </summary>
    private static string ApplyDeferredClick(SelfOffer offer, string version)
    {
        string drop = offer.Release == null ? "no update is offered any more"
            : !string.Equals(offer.Release.Version, version, StringComparison.OrdinalIgnoreCase) ? "the offer is now " + offer.Release.Version
            : !offer.Clickable ? (offer.NotAutoReason.Length > 0 ? offer.NotAutoReason : "it cannot be installed from here")
            : "";
        if (drop.Length > 0)
        {
            lock (Gate) _deferredClick = "";
            Write("op=deferred version=" + version + " result=dropped why=\"" + drop + "\"");
            return "deferred click dropped: " + drop;
        }

        var facts = Facts();
        if (facts.State.Journal is { InProgress: true } busy) return "an update is in progress (" + busy.State + ")";
        string closed = SafeWindow();
        if (closed.Length > 0) return "window closed: " + closed + " (a click waits)";
        lock (Gate) _deferredClick = "";
        bool ok = Begin(offer, "deferred click", byClick: true, out string message);
        Write("op=deferred version=" + version + " result=" + (ok ? "started" : "refused") + " why=\"" + message + "\"");
        return ok ? "started: " + message : "not started: " + message;
    }

    private static string SafeWindow()
    {
        try { return WindowClosed?.Invoke() ?? "no window probe"; }
        catch (Exception e) { return "the window could not be read (" + e.Message + ")"; }
    }

    // ---- detection ------------------------------------------------------------------------------------------

    private static void DetectIfChanged(double now)
    {
        string? path;
        lock (Gate)
        {
            if (_detecting || now < _nextDetect) return;
            _nextDetect = now + 2;
        }

        try { path = IndexPathProbe?.Invoke(); }
        catch { path = null; }
        string stamp = "";
        try
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var info = new FileInfo(path!);
                stamp = path + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            }
        }
        catch { stamp = ""; }

        bool changed, recompute;
        lock (Gate)
        {
            changed = !string.Equals(stamp, _indexStamp, StringComparison.Ordinal);
            recompute = now >= _nextRecompute;
            if (recompute || changed) _nextRecompute = now + RecomputeSeconds;
        }

        if (changed) Detect("index", path, stamp);
        else if (recompute) Recompute("tick");
    }

    /// <summary>Re-read the cached index (off the main thread) and recompute the offer.</summary>
    public static void Detect(string why, string? path = null, string? stamp = null)
    {
        if (path == null)
        {
            try { path = IndexPathProbe?.Invoke(); }
            catch { path = null; }
        }

        lock (Gate)
        {
            if (_detecting) return;
            _detecting = true;
            if (stamp != null) _indexStamp = stamp;
        }

        string? p = path;
        Task<List<SelfRelease>> task;
        try
        {
            task = RunOff(() =>
            {
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return new List<SelfRelease>();
                return SelfIndex.Parse(File.ReadAllText(p!));
            });
        }
        catch (Exception e) { task = Task.FromException<List<SelfRelease>>(e); }

        task.ContinueWith(t =>
        {
            var list = t.Status == TaskStatus.RanToCompletion ? t.Result : new List<SelfRelease>();
            PostMain(() =>
            {
                lock (Gate)
                {
                    _releases = list;
                    _detecting = false;
                    // C4: the join screens' Progenitor row (where the server's build can come from) depends on the releases.
                    _version++;
                }

                Recompute(why);
                try { ReleasesRead?.Invoke(); }
                catch { /* a redraw request only */ }
            });
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static string _lastOfferLine = "";

    /// <summary>Recompute the offer from the releases already read (settings or files changed).</summary>
    public static void Recompute(string why)
    {
        List<SelfRelease> releases;
        lock (Gate) releases = _releases;
        PruneHeld(releases);
        string hold = PlacementHold();
        var offer = SelfDetection.Hold(SelfDetection.Detect(Facts(), releases), hold);
        string line = offer.Available
            ? offer.Line + " (" + offer.Kind + (offer.Auto ? ", automatic" : ", " + offer.NotAutoReason) + ")"
            : "no update (" + offer.Kind + ", " + releases.Count + " release(s) listed" + (offer.FailedBefore.Length > 0 ? ", " + offer.FailedBefore + " failed before" : "") + ")";
        bool moved;
        lock (Gate)
        {
            moved = !string.Equals(line, _lastOfferLine, StringComparison.Ordinal);
            _offer = offer;
            _offerHold = hold;
            if (moved)
            {
                _lastOfferLine = line;
                _version++;
            }
        }

        if (moved) Write(line + " [" + why + "]");
    }

    private static DateTime SafeUtcNow()
    {
        try { return UtcNow(); }
        catch { return DateTime.UtcNow; }
    }

    /// <summary>
    /// Drop the held notes that no longer apply: the running protocols changed (the release is read again), the release left
    /// the index (judged only on a read index), or the note is older than <see cref="SelfHeldFile.MaxAgeDays"/>. Writes only
    /// when something is dropped.
    /// </summary>
    private static void PruneHeld(IReadOnlyList<SelfRelease> releases)
    {
        string? root = Root();
        if (string.IsNullOrEmpty(root) || !File.Exists(SelfHeldStore.PathOf(root!))) return;
        int boot = BootstrapLinkProtocol(), hs = HandshakeProtocol();
        var now = SafeUtcNow();
        var listed = releases.Count > 0 ? releases : null;
        // Read first: the usual pass finds nothing stale and never takes the write path (a corrupt file is left for an edit).
        var current = SelfHeldStore.Read(root!);
        bool stale = false;
        foreach (var e in current.Releases)
            if (SelfHeldFile.WhyStale(e, listed, boot, hs, now).Length > 0) { stale = true; break; }
        if (!stale) return;
        List<(string Version, string Why)> removed = new();
        SelfHeldStore.Edit(root!, h =>
        {
            removed = h.Prune(listed, boot, hs, now);
            return removed.Count > 0;
        }, l => Write("op=held " + l), out _);
        foreach (var (version, reason) in removed) Write("op=held version=" + version + " result=dropped why=\"" + reason + "\"");
    }

    /// <summary>Note a release that changes a protocol, so a later session holds it without downloading it again.</summary>
    private static void RememberHeld(SelfRelease release, SelfImage image, string change, int runningBootstrap, int runningHandshake)
    {
        string? root = Root();
        if (string.IsNullOrEmpty(root)) return;
        var entry = new SelfHeldRelease
        {
            Package = release.Package,
            Version = release.Version,
            DownloadUrl = release.DownloadUrl,
            Size = release.Size,
            PackageSha = image.PackageSha,
            CoreSha = image.CoreSha,
            BootstrapSha = image.BootstrapSha,
            BootstrapProtocol = image.BootstrapProtocol,
            CoreProtocol = image.CoreProtocol,
            HandshakeProtocol = image.HandshakeProtocol,
            RunningBootstrapProtocol = runningBootstrap,
            RunningHandshakeProtocol = runningHandshake,
            Change = change,
            CheckedUtc = SafeUtcNow(),
        };
        if (!SelfHeldStore.Edit(root!, h =>
            {
                h.Put(entry);
                return true;
            }, l => Write("op=held " + l), out string error))
            Write("op=held version=" + release.Version + " result=\"not noted: " + error + "\"");
    }

    // ---- applying -------------------------------------------------------------------------------------------

    /// <summary>
    /// Apply the offered release: automatic (<paramref name="byClick"/> false: only when the policy allows) or the Install
    /// tab's <b>Update</b> / the bridge (a managed or hand install). The download and inspection run off the main thread; the
    /// writes run on it, after the window is checked again.
    /// </summary>
    public static bool Begin(SelfOffer offer, string origin, bool byClick, out string message)
    {
        message = "";
        var release = offer?.Release;
        if (release == null) { message = "no Progenitor update is offered"; return false; }
        if (!byClick && !offer!.Auto) { message = offer.NotAutoReason; return false; }
        if (byClick && !offer!.Clickable) { message = offer.NotAutoReason.Length > 0 ? offer.NotAutoReason : "it cannot be installed from here"; return false; }
        // C4: a build placed for a server stays until the player leaves it; and the server this client is on, when it holds
        // players to its own Progenitor build, would refuse another one at the next join.
        string placedHold = PlacementHold();
        if (placedHold.Length > 0) { message = placedHold; return false; }
        string connected = ConnectedExactReason(release.Version);
        if (connected.Length > 0) { message = connected; return false; }
        if (Download == null) { message = "no downloader"; return false; }
        lock (Gate)
        {
            if (_running) { message = "an update of Progenitor is already running"; return false; }
            _running = true;
            _runState = "downloading";
            Attempted.Add(release.Version);
            _version++;
        }

        Write("op=prepare version=" + release.Version + " origin=" + origin + " url=" + release.DownloadUrl);
        long max = release.Size > 0 ? release.Size + 1024 : NonModPackages.DownloadLimit;
        Task<(byte[]? Bytes, string Error)> download;
        CancellationToken token;
        lock (Gate) token = _cancel.Token;
        try { download = Download(release.DownloadUrl, Math.Min(max, NonModPackages.DownloadLimit), token); }
        catch (Exception e) { download = Task.FromResult<(byte[]?, string)>((null, e.Message)); }
        download.ContinueWith(t =>
        {
            SelfImage image;
            string error = "";
            if (t.Status != TaskStatus.RanToCompletion) error = DownloadError + (t.Exception?.GetBaseException().Message ?? "failed");
            else if (t.Result.Bytes == null) error = DownloadError + (t.Result.Error.Length > 0 ? t.Result.Error : "failed");
            else if (release.Size > 0 && t.Result.Bytes.LongLength != release.Size) error = "the download is " + t.Result.Bytes.LongLength + " bytes, the listing says " + release.Size;
            image = error.Length > 0 ? new SelfImage { Error = error } : SelfImageReader.Read(t.Result.Bytes!);
            if (error.Length == 0) image.PackageSha = SelfFiles.Sha256(t.Result.Bytes!);
            if (token.IsCancellationRequested) return; // Core is unloading: nothing is written by this generation
            PostMain(() => Commit(release, image, origin, byClick));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        message = "downloading Progenitor " + release.Version;
        return true;
    }

    /// <summary>Main thread: keep, route and write. Always ends the run.</summary>
    internal static void Commit(SelfRelease release, SelfImage image, string origin, bool byClick)
    {
        string result;
        try { result = CommitCore(release, image, origin, byClick); }
        catch (Exception e) { result = "failed: " + e.Message; }
        lock (Gate)
        {
            _running = false;
            _runState = "idle";
            _lastResult = result;
            _version++;
        }

        Write("op=apply version=" + release.Version + " result=\"" + result + "\"");
        Invalidate();
        Recompute("applied");
    }

    private static string CommitCore(SelfRelease release, SelfImage image, string origin, bool byClick)
    {
        string? root = Root();
        if (string.IsNullOrEmpty(root)) return "failed: no BepInEx folder";
        // A failed download is tried again next session; a package that cannot be used is a failed version.
        if (!image.Ok) return Fail(root!, release.Version, "the release could not be used: " + image.Error, listFailed: !image.Error.StartsWith(DownloadError, StringComparison.Ordinal));
        string closed = SafeWindow();
        if (closed.Length > 0)
        {
            lock (Gate)
            {
                Attempted.Remove(release.Version);
                // A click is kept and installed when the window opens; an automatic pass simply tries again then.
                if (byClick) _deferredClick = release.Version;
            }

            return "deferred: " + closed + (byClick ? "; it installs when the window opens" : "");
        }

        var facts = Facts();
        if (facts.State.Journal is { InProgress: true } j0) return "not now: an update is in progress (" + j0.State + ")";
        if (facts.BootstrapSha.Length == 0) return "not now: the running Bootstrap does not report its image (restart once with this Bootstrap)";
        int protocol = BootstrapLinkProtocol();
        var (route, why) = SelfRoute.Decide(image, facts.CoreSha, facts.BootstrapSha, protocol);
        Write("op=route version=" + release.Version + " route=" + route + " why=\"" + why + "\" core=" + Short(image.CoreSha) + " bootstrap=" + Short(image.BootstrapSha));
        if (route == SelfRouteKind.Refused) return Fail(root!, release.Version, why, listFailed: true);

        // Decided 2026-10-09: Progenitor's versions are dates, so only a protocol change makes a release breaking.
        // An automatic pass holds such a release (nothing written) and offers it as notify only; a click installs it, by
        // the restart route when the Bootstrap protocol changed (SelfRoute above).
        int handshake = HandshakeProtocol();
        string change = SelfProtocols.ChangeOf(image, protocol, handshake);
        if (change.Length > 0)
        {
            lock (Gate) ProtocolChanges[release.Version] = change;
            RememberHeld(release, image, change, protocol, handshake);
            Write("op=protocol version=" + release.Version + " change=\"" + change + "\"" + (byClick ? " (installed by a click)" : " (held: notify only)"));
            if (!byClick) return "held: Progenitor " + release.Version + " changes a protocol (" + change + "); notify only";
        }

        // Keep the running copy and the new release (mod-compatibility decision 7: old Cores stay for Exact servers).
        string prevDir = KeepRunning(root!, facts, route, out string keepError);
        if (prevDir.Length == 0) return "failed: the running copy could not be kept (" + keepError + "); nothing was changed";
        string newDir = SelfStore.Keep(root!, new SelfKept
        {
            Version = release.Version,
            Package = release.Package,
            CoreSha = image.CoreSha,
            BootstrapSha = image.BootstrapSha,
            Protocol = image.CoreProtocol,
            Source = "thunderstore",
        }, image.Core, image.Bootstrap, image.Tables, out string newError);
        if (newDir.Length == 0) return "failed: the release could not be kept (" + newError + "); nothing was changed";

        if (route == SelfRouteKind.Current)
        {
            WriteRecord(root!, release, image);
            return "current: the running copy is already " + release.Version + "; recorded";
        }

        if (!Allowed(SelfPaths.CoreFile, image.CoreSha) || (route == SelfRouteKind.Restart && !Allowed(SelfPaths.BootstrapFile, image.BootstrapSha))
            // Review M3: a restore puts the previous copy back through the same gate.
            || !Allowed(SelfPaths.CoreFile, facts.CoreSha) || (route == SelfRouteKind.Restart && !Allowed(SelfPaths.BootstrapFile, facts.BootstrapSha)))
            return Fail(root!, release.Version, "its sha256 (or the running copy's, which a rollback restores) is not in BepInEx/config/progenitor-allow.txt", listFailed: false);

        var journal = new SelfJournal
        {
            Version = release.Version,
            Previous = SelfDetection.InstalledOf(facts, SelfDetection.KindOf(facts)),
            Package = release.Package,
            NewDir = newDir,
            PrevDir = prevDir,
            NewCore = image.CoreSha,
            NewBootstrap = route == SelfRouteKind.Restart ? image.BootstrapSha : "",
            PrevCore = facts.CoreSha,
            PrevBootstrap = facts.BootstrapSha,
            Route = route == SelfRouteKind.Hot ? "hot" : "restart",
            Utc = DateTime.UtcNow,
        };

        if (route == SelfRouteKind.Hot)
        {
            journal.State = SelfJournal.Swapping;
            journal.Starts = 1;
            if (!SaveJournal(root!, journal, out string jerr)) return "failed: the journal could not be written (" + jerr + "); nothing was changed";
            if (!SelfFiles.WriteAtomic(SelfPaths.CoreNext(root!), image.Core, out string werr))
            {
                ClearJournal(root!, "the staged Core could not be written: " + werr);
                return "failed: " + werr;
            }

            SelfFiles.CopyTables(SelfPaths.Absolute(root!, newDir), SelfPaths.Tables(root!));
            string answer;
            try { answer = RequestSwap?.Invoke("self-update " + release.Version) ?? "no swap seam"; }
            catch (Exception e) { answer = "threw: " + e.Message; }
            if (!answer.StartsWith("scheduled", StringComparison.OrdinalIgnoreCase))
            {
                UndoStagedCore(root!, image.CoreSha);
                ClearJournal(root!, "the swap was not scheduled: " + answer);
                // Review H1: some refusals last the whole process (a generation that did not collect, a plugin that pins
                // Core); the version stays attempted, so it is tried again next session, never every tick.
                return "not swapped: " + answer + " (tried again next session)";
            }

            return "swap scheduled: " + answer;
        }

        // A new Bootstrap: staged beside the running one; Bootstrap puts the release's Core in place at the next launch.
        string bootPath = SafeBootstrapPath(root!);
        journal.State = SelfJournal.Staged;
        journal.Starts = 0;
        if (!SaveJournal(root!, journal, out string sjerr)) return "failed: the journal could not be written (" + sjerr + "); nothing was changed";
        if (!SelfFiles.WriteAtomic(bootPath + SelfPaths.NextSuffix, image.Bootstrap, out string berr))
        {
            ClearJournal(root!, "the staged Bootstrap could not be written: " + berr);
            return "failed: " + berr;
        }

        try { Notice?.Invoke(1, 0, "Progenitor " + release.Version + " starts at the next launch."); }
        catch { /* */ }
        return "staged: Progenitor " + release.Version + " starts at the next launch (" + why + ")";
    }

    private static string Short(string sha) => (sha ?? "").Length >= 12 ? sha.Substring(0, 12) : sha ?? "";

    private static int BootstrapLinkProtocol()
    {
        try { return ProtocolProbe?.Invoke() ?? 0; }
        catch { return 0; }
    }

    /// <summary>The running Bootstrap/Core protocol (live: Core's own <c>BootstrapLink.ProtocolVersion</c>, which Bootstrap accepted).</summary>
    internal static Func<int>? ProtocolProbe { get; set; }

    /// <summary>The running Core's handshake protocol (<c>Networking.Handshake.ProtocolVersion</c>).</summary>
    internal static int HandshakeProtocol() => Progenitor.Networking.Handshake.ProtocolVersion;

    private static string SafeBootstrapPath(string root)
    {
        try { return BootstrapPathProbe?.Invoke() is { Length: > 0 } p ? p : SelfPaths.DefaultBootstrap(root); }
        catch { return SelfPaths.DefaultBootstrap(root); }
    }

    /// <summary>Keep the running copy in the store (its Core image, its Bootstrap file when it is the running one, its tables).</summary>
    private static string KeepRunning(string root, SelfFacts facts, SelfRouteKind route, out string error)
    {
        error = "";
        byte[]? core = null;
        try
        {
            string disk = SelfPaths.CoreDll(root);
            if (string.Equals(SelfFiles.Sha256File(disk), facts.CoreSha, StringComparison.OrdinalIgnoreCase)) core = File.ReadAllBytes(disk);
        }
        catch { core = null; }

        if (core == null)
        {
            try { core = RunningCoreBytes?.Invoke(); }
            catch { core = null; }
        }

        if (core == null || core.Length == 0 || !string.Equals(SelfFiles.Sha256(core), facts.CoreSha, StringComparison.OrdinalIgnoreCase))
        {
            error = "the running Core image is not readable";
            return "";
        }

        byte[] boot = Array.Empty<byte>();
        try
        {
            string path = SafeBootstrapPath(root);
            if (string.Equals(SelfFiles.Sha256File(path), facts.BootstrapSha, StringComparison.OrdinalIgnoreCase)) boot = File.ReadAllBytes(path);
        }
        catch { boot = Array.Empty<byte>(); }

        if (boot.Length == 0 && route == SelfRouteKind.Restart)
        {
            error = "the running Bootstrap's file was replaced since it started (restart first)";
            return "";
        }

        string version = SelfDetection.InstalledOf(facts, SelfDetection.KindOf(facts));
        return SelfStore.Keep(root, new SelfKept
        {
            Version = version,
            Package = facts.Record?.Package ?? "",
            CoreSha = facts.CoreSha,
            BootstrapSha = boot.Length > 0 ? facts.BootstrapSha : "",
            Protocol = BootstrapLinkProtocol(),
            Source = facts.Record != null && facts.Record.Matches(facts.CoreSha, facts.BootstrapSha) ? "thunderstore" : "installed",
        }, core, boot, SelfStore.LiveTables(root), out error);
    }

    private static bool SaveJournal(string root, SelfJournal journal, out string error) =>
        SelfFiles.EditState(root, s =>
        {
            s.Journal = journal;
            return true;
        }, out error);

    private static void ClearJournal(string root, string note)
    {
        SelfFiles.EditState(root, s =>
        {
            if (s.Journal == null) return false;
            s.Journal.State = SelfJournal.Superseded;
            s.Journal.Note = note;
            return true;
        }, out _);
    }

    private static void UndoStagedCore(string root, string sha)
    {
        try
        {
            string next = SelfPaths.CoreNext(root);
            if (File.Exists(next) && string.Equals(SelfFiles.Sha256File(next), sha, StringComparison.OrdinalIgnoreCase)) File.Delete(next);
        }
        catch { /* the watcher may have taken it; the confirmation judges what loaded */ }
    }

    private static string Fail(string root, string version, string why, bool listFailed)
    {
        if (listFailed)
            SelfFiles.EditState(root, s =>
            {
                s.AddFailed(version, why, DateTime.UtcNow);
                return true;
            }, out _);
        try { Notice?.Invoke(0, 1, "Progenitor " + version + " could not be installed: " + why + "."); }
        catch { /* */ }
        return "failed: " + why + (listFailed ? " (not tried again automatically)" : "");
    }

    private static void WriteRecord(string root, SelfRelease release, SelfImage image) =>
        SelfFiles.WriteRecord(root, new SelfInstallRecord
        {
            Package = release.Package,
            Version = release.Version,
            CoreSha = image.CoreSha,
            BootstrapSha = image.BootstrapSha,
            InstalledUtc = DateTime.UtcNow,
            Source = "thunderstore",
        }, out _);

    // ---- after the swap, after a start ----------------------------------------------------------------------

    /// <summary>A requested Core swap: once Bootstrap reports it done, judge it on the loaded image.</summary>
    internal static void ConfirmSwap(string root, SelfJournal j)
    {
        (bool InFlight, string Outcome, string Reason) status;
        try { status = ReloadStatus?.Invoke() ?? (false, "none", ""); }
        catch { status = (false, "none", ""); }
        double age = (DateTime.UtcNow - j.Utc).TotalSeconds;
        // Review L2: a reload tick that threw leaves "in flight" set; past twice the give-up time it is judged anyway.
        if (status.InFlight && age < 2 * SwapGiveUpSeconds) return;
        Invalidate();
        var facts = Facts();
        // Review H2: a restored or failed swap records Bootstrap's summary as its reason, not the request's; the outcome
        // alone (with the new image not loaded) says this swap failed.
        bool failedOutcome = string.Equals(status.Outcome, "restored", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status.Outcome, "failed", StringComparison.OrdinalIgnoreCase);
        bool ours = failedOutcome || (status.Reason ?? "").IndexOf("self-update " + j.Version, StringComparison.OrdinalIgnoreCase) >= 0;
        if (string.Equals(facts.CoreSha, j.NewCore, StringComparison.OrdinalIgnoreCase))
        {
            SelfFiles.EditState(root, s =>
            {
                if (s.Journal == null) return false;
                s.Journal.State = SelfJournal.Trial;
                s.Journal.Starts = Math.Max(1, s.Journal.Starts);
                s.Journal.Note = "swapped in place (" + status.Outcome + ")";
                return true;
            }, out _);
            Write("op=confirm version=" + j.Version + " result=loaded core=" + Short(j.NewCore) + " outcome=" + status.Outcome);
            try { Notice?.Invoke(1, 0, "Progenitor " + j.Version + " is installed and loaded now."); }
            catch { /* */ }
            Bump();
            return;
        }

        if (!ours && age < SwapGiveUpSeconds) return;
        bool transient = !failedOutcome;
        RestoreDiskCore(root, j);
        UndoStagedCore(root, j.NewCore);
        string why = ours ? "the Core swap ended " + status.Outcome + (status.Reason.Length > 0 ? " (" + status.Reason + ")" : "") : "the Core swap did not run";
        // C4 review fix: the restore of a build placed for a server swaps the player's own copy back; a failure of that swap
        // never lists the player's own version as a failed release.
        bool restoreJournal = IsRestoreJournal(root, j);
        SelfFiles.EditState(root, s =>
        {
            if (!transient && !restoreJournal) s.AddFailed(j.Version, why, DateTime.UtcNow);
            if (s.Journal == null) return true;
            s.Journal.State = transient ? SelfJournal.Superseded : SelfJournal.Failed;
            s.Journal.Note = why;
            return true;
        }, out _);
        Write("op=confirm version=" + j.Version + " result=" + (transient ? "not-swapped" : "failed") + " why=\"" + why + "\"" + (transient ? " (tried again next session)" : "; it is not applied automatically again"));
        if (!transient)
        {
            try { Notice?.Invoke(0, 1, "Progenitor " + j.Version + " failed to load and the running copy stays: " + why + "."); }
            catch { /* */ }
        }

        Bump();
    }

    /// <summary>A failed swap left the new image on disk (Bootstrap promoted it): put the previous Core back for the next start.</summary>
    private static void RestoreDiskCore(string root, SelfJournal j)
    {
        try
        {
            string disk = SelfPaths.CoreDll(root);
            if (!string.Equals(SelfFiles.Sha256File(disk), j.NewCore, StringComparison.OrdinalIgnoreCase)) return;
            string prev = Path.Combine(SelfPaths.Absolute(root, j.PrevDir), SelfPaths.CoreFile);
            if (File.Exists(prev)) SelfFiles.WriteAtomic(disk, File.ReadAllBytes(prev), out _);
            SelfFiles.CopyTables(SelfPaths.Absolute(root, j.PrevDir), SelfPaths.Tables(root));
        }
        catch { /* the start-loop guard is the next line of defense */ }
    }

    /// <summary>The release on trial runs and the process is healthy: end the trial and record the release.</summary>
    internal static void MarkHealth(string root, SelfJournal j, double now)
    {
        var facts = Facts();
        bool coreNew = string.Equals(facts.CoreSha, j.NewCore, StringComparison.OrdinalIgnoreCase);
        bool bootNew = j.NewBootstrap.Length == 0 || string.Equals(facts.BootstrapSha, j.NewBootstrap, StringComparison.OrdinalIgnoreCase);
        if (!coreNew || !bootNew) return; // Bootstrap judges a start that runs other bytes (superseded) at the next start.
        double started;
        lock (Gate) started = _started;
        if (now - started < HealthySeconds) return;
        bool world;
        try { world = WorldReady(); }
        catch { world = false; }
        if (!world) return;
        var kept = SelfFiles.ReadKept(SelfPaths.Absolute(root, j.NewDir));
        SelfFiles.EditState(root, s =>
        {
            if (s.Journal == null) return false;
            s.Journal.State = SelfJournal.Ok;
            s.Journal.Starts = 0;
            s.Journal.Note = "started cleanly";
            s.Journal.Utc = DateTime.UtcNow;
            return true;
        }, out _);
        SelfFiles.WriteRecord(root, new SelfInstallRecord
        {
            Package = kept?.Package is { Length: > 0 } p ? p : j.Package,
            Version = j.Version,
            CoreSha = facts.CoreSha,
            BootstrapSha = facts.BootstrapSha,
            InstalledUtc = DateTime.UtcNow,
            Source = "thunderstore",
        }, out _);
        // C4: the copy a build placed for a server will give back is never pruned.
        var protect = new List<string> { j.NewDir, j.PrevDir };
        string placedPrev = SelfPlacedStore.Read(root)?.PreviousDir ?? "";
        if (placedPrev.Length > 0) protect.Add(placedPrev);
        int pruned = SelfStore.Prune(root, protect);
        Invalidate();
        Write("op=healthy version=" + j.Version + " (the trial ended; the record names it" + (pruned > 0 ? "; " + pruned + " old kept release(s) removed" : "") + ")");
        Recompute("healthy");
    }

    /// <summary>A staged release whose Bootstrap <c>.next</c> went away (quarantined, or another deploy) is dropped.</summary>
    internal static void CheckStaged(string root, SelfJournal j)
    {
        string path = SafeBootstrapPath(root);
        string disk = SelfFiles.Sha256File(path);
        string next = SelfFiles.Sha256File(path + SelfPaths.NextSuffix);
        if (string.Equals(disk, j.NewBootstrap, StringComparison.OrdinalIgnoreCase) || string.Equals(next, j.NewBootstrap, StringComparison.OrdinalIgnoreCase)) return;
        SelfFiles.EditState(root, s =>
        {
            if (s.Journal == null) return false;
            s.Journal.State = SelfJournal.Superseded;
            s.Journal.Note = "the staged Bootstrap is gone (refused at promote, or another copy was deployed)";
            return true;
        }, out _);
        Write("op=staged version=" + j.Version + " result=dropped (the staged Bootstrap is gone)");
        Bump();
    }

    /// <summary>
    /// The pending-restart bar's Progenitor row: a release is staged and this process still runs the previous Bootstrap.
    /// Null when nothing waits.
    /// </summary>
    public static (string Version, string Name)? PendingRestart()
    {
        string? root = Root();
        if (string.IsNullOrEmpty(root)) return null;
        var j = SelfFiles.ReadState(root!).Journal;
        if (j == null || j.State != SelfJournal.Staged) return null;
        var f = Facts();
        if (string.Equals(f.BootstrapSha, j.NewBootstrap, StringComparison.OrdinalIgnoreCase)) return null;
        return (j.Version, "Progenitor update");
    }

    // ---- bridge and readback --------------------------------------------------------------------------------

    public static int ClearFailed()
    {
        string? root = Root();
        if (string.IsNullOrEmpty(root)) return 0;
        int n = 0;
        SelfFiles.EditState(root!, s =>
        {
            n = s.Failed.Count;
            s.Failed.Clear();
            return n > 0;
        }, out _);
        lock (Gate) Attempted.Clear();
        if (n > 0) Write("cleared " + n + " failed version(s)");
        Recompute("failed-cleared");
        return n;
    }

    public static Dictionary<string, object?> Describe()
    {
        string? root = Root();
        var facts = Facts();
        // C4: the hold as of this read (a placement that began since the last recompute is already shown).
        var offer = SelfDetection.Hold(Offer, PlacementHold());
        var kept = new List<object?>();
        if (!string.IsNullOrEmpty(root))
            foreach (var k in SelfStore.List(root!)) kept.Add(k.Describe());
        var failed = new List<object?>();
        foreach (var f in facts.State.Failed)
            failed.Add(new Dictionary<string, object?> { ["version"] = f.Version, ["reason"] = f.Reason, ["utc"] = f.Utc.ToString("o", CultureInfo.InvariantCulture) });
        List<SelfRelease> releases;
        lock (Gate) releases = _releases;
        var listed = new List<object?>();
        foreach (var r in releases) listed.Add(r.Version);
        var held = new List<object?>();
        if (!string.IsNullOrEmpty(root))
            foreach (var h in SelfHeldStore.Read(root!).Releases) held.Add(h.Describe());
        lock (Gate)
        {
            return new Dictionary<string, object?>
            {
                ["side"] = facts.IsServer ? "server" : "client",
                ["mode"] = UpdateSettings.Word(facts.Mode),
                ["devOverride"] = facts.DevOverride,
                ["kind"] = SelfDetection.KindOf(facts).ToString(),
                ["running"] = new Dictionary<string, object?>
                {
                    ["coreVersion"] = facts.CoreVersion,
                    ["bootstrapVersion"] = facts.BootstrapVersion,
                    ["coreSha256"] = facts.CoreSha,
                    ["bootstrapSha256"] = facts.BootstrapSha,
                    ["protocol"] = BootstrapLinkProtocol(),
                    ["handshakeProtocol"] = HandshakeProtocol(),
                    ["devBridge"] = facts.DevBridge,
                    ["modManager"] = facts.ModManager,
                },
                ["record"] = facts.Record?.Describe(),
                ["offer"] = offer.Describe(),
                ["waitingCount"] = WaitingCount,
                ["listed"] = listed,
                ["journal"] = facts.State.Journal?.Describe(),
                ["failed"] = failed,
                ["kept"] = kept,
                ["runState"] = _runState,
                ["lastResult"] = _lastResult,
                ["window"] = SafeWindow() is { Length: > 0 } w ? w : "open",
                ["pendingRestart"] = PendingRestart() is { } pr ? pr.Version : null,
                ["attempted"] = new List<string>(Attempted),
                ["deferredClick"] = _deferredClick.Length > 0 ? _deferredClick : null,
                ["protocolChanges"] = new Dictionary<string, string>(ProtocolChanges),
                ["held"] = held,
                // C4: a build placed for a server, and a placement being prepared.
                ["placement"] = string.IsNullOrEmpty(root) ? null : SelfPlacedStore.Read(root!)?.Describe(),
                ["placing"] = DescribePlacing(),
                ["history"] = new List<string>(History),
            };
        }
    }

    /// <summary>Bridge: forget this session's attempts (a pass may retry the same version).</summary>
    public static int ForgetAttempts()
    {
        lock (Gate)
        {
            int n = Attempted.Count;
            Attempted.Clear();
            return n;
        }
    }

    internal static void Reset()
    {
        lock (Gate)
        {
            try { _cancel.Cancel(); }
            catch { /* */ }
            _cancel = new CancellationTokenSource();
            _journalBusy = false;
            Attempted.Clear();
            ProtocolChanges.Clear();
            History.Clear();
            _offer = SelfOffer.None;
            _releases = new List<SelfRelease>();
            _indexStamp = "";
            _detecting = false;
            _running = false;
            _runState = "idle";
            _lastResult = "";
            _nextDetect = 0;
            _nextRecompute = 0;
            _nextStagedCheck = 0;
            _factsCache = null;
            _factsAt = double.MinValue;
            _started = -1;
            _lastOfferLine = "";
            _deferredClick = "";
            _offerHold = "";
            _place = null;
            _restoreTriedThisVisit = false;
            IndexAsked.Clear();
        }

        ConnectedRequirement = null;
        ReleasesRead = null;
        RootProbe = null;
        IsServerProbe = () => false;
        FactsProbe = null;
        IndexPathProbe = null;
        CheckIndex = null;
        Download = null;
        RequestSwap = null;
        ReloadStatus = null;
        WindowClosed = null;
        WorldReady = () => true;
        Allowed = (_, _) => true;
        BootstrapPathProbe = null;
        RunningCoreBytes = null;
        ProtocolProbe = null;
        PostMain = a => a();
        RunOff = work => Task.Run(work);
        Log = null;
        Notice = null;
        Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        UtcNow = () => DateTime.UtcNow;
    }
}
