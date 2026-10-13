using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.WorldRestore;

namespace Progenitor.Install;

/// <summary>AUTOUPDATE3: one read of the package index for a server pass.</summary>
internal sealed class ServerIndexRead
{
    public bool Ok { get; init; }
    public string Error { get; init; } = "";
    public IReadOnlyList<CatalogEntry> Entries { get; init; } = Array.Empty<CatalogEntry>();

    /// <summary>A network request was made (else the cached body answered).</summary>
    public bool Requested { get; init; }

    /// <summary>A 304: the body on disk was confirmed current.</summary>
    public bool NotModified { get; init; }

    public string Stamp { get; init; } = "";

    /// <summary>Is a key one of the operator's local index (<c>[Install] LocalIndexPath</c>).</summary>
    public Func<string, bool>? IsLocal { get; init; }
}

/// <summary>AUTOUPDATE3: the verified image of one update, ready for the version store.</summary>
internal sealed class ServerApprovedImage
{
    public byte[] Bytes { get; init; } = Array.Empty<byte>();
    public string Sha256 { get; init; } = "";
    public string Version { get; init; } = "";
    public string PluginId { get; init; } = "";
    public bool Hot { get; init; }
    public string NotHotReason { get; init; } = "";
    public ParkedLedgerRow? Ledger { get; init; }

    /// <summary>Why no image could be approved (the update cannot go through the version store), or empty.</summary>
    public string Error { get; init; } = "";
}

/// <summary>AUTOUPDATE3: the install pipeline as the server's passes use it (live: a catalog like the remote installer's).</summary>
internal sealed class ServerUpdatePipeline
{
    /// <summary>Read the index: <c>true</c> makes one conditional request, <c>false</c> serves the cached body only.</summary>
    public Func<bool, CancellationToken, Task<ServerIndexRead>> ReadIndex { get; set; } =
        (_, _) => Task.FromResult(new ServerIndexRead { Ok = false, Error = "no index reader" });

    public Func<string, CancellationToken, Task<InspectCallResult>> Inspect { get; set; } =
        (_, _) => Task.FromResult(new InspectCallResult(false, "no install catalog", null, null));

    public Func<string, CancellationToken, Task<InstallPlanSet>> Plan { get; set; } =
        (_, _) => Task.FromResult(InstallPlanSet.Refuse("no install catalog"));

    /// <summary>The approved image of a planned update (main thread), or one with <see cref="ServerApprovedImage.Error"/>.</summary>
    public Func<InstallPlanSet, string, ServerApprovedImage> Approve { get; set; } = (_, _) => new ServerApprovedImage { Error = "no install catalog" };

    public Action<string> Release { get; set; } = _ => { };

    /// <summary>Is a package installed (a dependency of an update that is not new).</summary>
    public Func<string, bool> Installed { get; set; } = _ => false;
}

/// <summary>
/// AUTOUPDATE3 (M6.3, auto-update phase 3): the server's update detection, Notify and Automatic modes, applying, rollback,
/// Restart when empty, Restart now, the pre-update world backup and the admin notices (docs/auto-update.md, Server). Every
/// decision is in the pure types of <c>ServerUpdateModel.cs</c>; this holds the state and runs the steps through seams the
/// live wiring sets (<c>ServerAutoUpdateLive</c>), so the suite drives it with fakes.
/// <list type="bullet">
/// <item><b>Checks</b> (<see cref="ServerUpdateCadence"/>): once the world has loaded, then every <c>CheckHours</c>; a read of
/// the index by another owner within the interval counts, and the pass runs on that cached body with no request.</item>
/// <item><b>Notify</b> (the default) reports; <b>Automatic</b> applies: a server-only hot update now, a hot update clients
/// need once the server has been empty for the quiet period, a restart update staged for the next start
/// (<see cref="ServerApplyPlan"/>). Old copies are kept in the version store; a hot update that does not load is rolled
/// back (a load that threw goes on the server's failed list); restart updates are judged after the next start
/// (<see cref="UpdateStartChecks"/>).</item>
/// <item><b>Restart when empty</b> (<see cref="RestartWhenEmptyRule"/>) and <b>Restart now</b> save and quit through the stop
/// request's path; the host must start the server again.</item>
/// </list>
/// </summary>
internal static class ServerAutoUpdate
{
    public const string LogPrefix = "AutoUpdate: ";

    /// <summary>The start check runs this long after the world loaded (the reloader has loaded the promoted copies).</summary>
    public const double StartCheckDelaySeconds = 20;

    /// <summary>The first pass waits this long after the world loaded.</summary>
    public const double FirstPassDelaySeconds = 30;

    private const int MaxHistory = 40;
    private const int MaxRecent = 5;

    // ---- seams ------------------------------------------------------------------------------------------------

    internal static Func<ServerUpdateSettings> SettingsProbe { get; set; } = () => ServerUpdateSettings.Unbound;
    internal static Func<string?> BepInExRootProbe { get; set; } = () => null;
    internal static Func<string?> GameRootProbe { get; set; } = () => null;
    internal static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
    internal static Func<int> OnlineProbe { get; set; } = () => 0;

    /// <summary>When the cached index body was last fetched or confirmed current by any owner (TSTRAFFIC1's stamp), or null.</summary>
    internal static Func<DateTime?> CacheStampProbe { get; set; } = () => null;

    internal static ServerUpdatePipeline Pipeline { get; set; } = new();
    internal static Func<ModVersionService?> StoreProbe { get; set; } = () => null;
    internal static Func<string, string, LoadFacts>? LoadFactsProbe { get; set; }

    /// <summary>Do clients need any of these plugins (the server's handshake asks for it, or the client mod policy requires it)? Fails safe: true.</summary>
    internal static Func<IReadOnlyList<string>, bool> ClientsNeedProbe { get; set; } = _ => true;

    internal static Func<Func<object?>, Task<object?>> RunOnMain { get; set; } = work => Task.FromResult(work());

    /// <summary>Run an action on the main thread after a delay (seconds).</summary>
    internal static Action<double, Action> Schedule { get; set; } = (_, a) => a();

    /// <summary>Run work off the main thread.</summary>
    internal static Func<Func<Task>, Task> RunOff { get; set; } = work => Task.Run(work);

    internal static Action<string>? Log { get; set; }

    /// <summary>The audit file (<see cref="ServerUpdateAudit.FileName"/>); the line also goes to <see cref="Log"/>.</summary>
    internal static Action<string>? AuditSink { get; set; }

    /// <summary>Start a pre-update backup (label of the updates; the outcome comes later on the main thread). Null when it started, else why not.</summary>
    internal static Func<string, int, Action<BackupOutcome>, string?>? StartBackup { get; set; }

    internal static Func<IReadOnlyList<WorldBackupEntry>>? BackupEntriesProbe { get; set; }

    internal static Action<AdminUpdateNotice>? NotifyAdmins { get; set; }

    internal static Action<string>? Broadcast { get; set; }

    /// <summary>Save the world and quit (the stop request's server path). Calls back with each reply and once on a refusal.</summary>
    internal static Action<Action<string>, Action<string>>? StopServer { get; set; }

    /// <summary>Another job that must not overlap a restart (a world backup, a world restore countdown).</summary>
    internal static Func<bool> OtherBusyProbe { get; set; } = () => false;

    // ---- state ------------------------------------------------------------------------------------------------

    private static readonly object Gate = new();
    private static EmptyGate Empty = new();
    private static UpdateReport _report = UpdateReport.Empty;
    private static DateTime? _lastPassUtc;
    private static DateTime? _worldLoadedUtc;
    private static bool _startChecked;
    private static string _lastCheckLine = "";
    private static string _lastCheckError = "";
    private static bool _running;
    private static string _runState = "idle";
    private static readonly Dictionary<string, string> Held = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Attempted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<ServerUpdateOutcome> Outcomes = new();
    private static readonly List<string> History = new();
    private static AdminUpdateNotice? _notice;
    private static AdminUpdateNotice? _joinNotice;
    private static string _lastNoticeSent = "";
    private static readonly Dictionary<ulong, string> AdminSeen = new();
    private static DateTime? _lastRestartFailureUtc;
    private static string _lastRestart = "";
    private static int _countdownGen;
    private static DateTime? _countdownEndsUtc;
    private static string _countdownBy = "";
    private static bool _restarting;
    private static int _generation;

    internal static UpdateReport Report { get { lock (Gate) return _report; } }

    internal static EmptyGate EmptyState => Empty;

    internal static bool Running { get { lock (Gate) return _running; } }

    internal static string RunState { get { lock (Gate) return _runState; } }

    /// <summary>The countdown's seconds left, or -1 when none runs.</summary>
    internal static int CountdownSeconds
    {
        get
        {
            lock (Gate)
            {
                if (_countdownEndsUtc is not { } ends) return -1;
                int left = (int)Math.Ceiling((ends - UtcNow()).TotalSeconds);
                return left < 0 ? 0 : left;
            }
        }
    }

    internal static AdminUpdateNotice? CurrentNotice { get { lock (Gate) return _notice; } }

    internal static IReadOnlyList<ServerUpdateOutcome> RecentOutcomes
    {
        get
        {
            lock (Gate)
            {
                int from = Math.Max(0, Outcomes.Count - MaxRecent);
                return Outcomes.GetRange(from, Outcomes.Count - from);
            }
        }
    }

    private static ServerUpdateSettings SafeSettings()
    {
        try { return SettingsProbe() ?? ServerUpdateSettings.Unbound; }
        catch { return ServerUpdateSettings.Unbound; }
    }

    private static string? Safe(Func<string?> probe)
    {
        try { return probe(); }
        catch { return null; }
    }

    // ---- logging ----------------------------------------------------------------------------------------------

    private static void Remember(string line)
    {
        lock (Gate)
        {
            History.Add(UtcNow().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + line);
            if (History.Count > MaxHistory) History.RemoveAt(0);
        }
    }

    /// <summary>One audit line: the BepInEx log and <c>progenitor-auto-update.log</c>.</summary>
    internal static void Audit(string op, string package = "", string from = "", string to = "", string route = "", string result = "ok",
        string detail = "", IReadOnlyList<string>? files = null)
    {
        string line = ServerUpdateAudit.Line(UtcNow(), op, package, from, to, route, result, detail, files);
        try { Log?.Invoke(line); }
        catch { /* */ }
        try { AuditSink?.Invoke(line); }
        catch { /* the audit never decides */ }
        Remember(op + " " + (package.Length > 0 ? package + " " : "") + result + (detail.Length > 0 ? " (" + detail + ")" : ""));
    }

    private static void Record(ServerUpdateOutcome o)
    {
        o.Utc = UtcNow();
        lock (Gate)
        {
            Outcomes.Add(o);
            if (Outcomes.Count > MaxHistory) Outcomes.RemoveAt(0);
        }
    }

    // ---- lifetime ---------------------------------------------------------------------------------------------

    /// <summary>The world loaded (or Core installed into a loaded world): the cadence and the start check count from now.</summary>
    internal static void OnWorldLoaded()
    {
        lock (Gate)
        {
            if (_worldLoadedUtc != null) return;
            _worldLoadedUtc = UtcNow();
        }

        Remember("world loaded; first check in " + FirstPassDelaySeconds + " s");
    }

    /// <summary>A player connected: the quiet period starts again when the server is empty next.</summary>
    internal static void OnPlayerConnected()
    {
        lock (Gate) Empty.Connected(UtcNow());
    }

    /// <summary>
    /// The server's tick (about once a second, main thread): the empty gate, the start check, the cadence, held updates once the
    /// server is quiet, and Restart when empty.
    /// </summary>
    internal static string Tick()
    {
        var now = UtcNow();
        int online;
        try { online = OnlineProbe(); }
        catch { online = -1; }
        bool startNow;
        lock (Gate)
        {
            if (online >= 0) Empty.Observe(online, now);
            if (_worldLoadedUtc is not { } loaded) return "waiting for the world";
            double since = (now - loaded).TotalSeconds;
            startNow = !_startChecked && since >= StartCheckDelaySeconds;
            if (startNow) _startChecked = true;
            else if (!_startChecked) return "waiting for the start check";
            else if (_lastPassUtc == null && since < FirstPassDelaySeconds) return "waiting for the first pass";
        }

        if (startNow)
        {
            try { RunStartCheck(); }
            catch (Exception e) { Audit("startcheck", result: "failed", detail: e.Message); }
            return "start check";
        }

        var s = SafeSettings();
        bool running;
        // No pass while a restart or its countdown runs (review 2026-10-09: a pass could write under the countdown).
        lock (Gate) running = _running || _restarting || _countdownEndsUtc != null;
        if (running) return "busy";

        DateTime? stamp;
        try { stamp = CacheStampProbe(); }
        catch { stamp = null; }
        DateTime? last;
        lock (Gate) last = _lastPassUtc;
        string due = s.Effective == UpdateMode.Off ? "" : ServerUpdateCadence.Due(now, last, stamp, s.CheckHours);
        if (due.Length > 0)
        {
            BeginCheck(due == ServerUpdateCadence.Request, due == ServerUpdateCadence.Request ? "cadence" : "cache");
            return "check (" + due + ")";
        }

        // Held hot updates clients need: once the server has been empty for the quiet period.
        int held;
        lock (Gate) held = Held.Count;
        if (held > 0 && s.Effective == UpdateMode.Automatic && Empty.Quiet(now, s.EmptyMinutes))
        {
            BeginCheck(false, "held");
            return "applying held updates";
        }

        // The apply journal is read only while Restart when empty can act (the tick runs every second).
        if (!s.RestartsWhenEmpty) return "idle";
        string why = RestartWhenEmptyRule.Decide(s, RestartPending().Count, Empty, now, SafeBusy(), _lastRestartFailureUtc);
        if (why.Length == 0)
        {
            string? refusal = BeginRestart("empty", "Restart when empty", warn: false);
            return refusal == null ? "restarting (empty)" : "restart refused: " + refusal;
        }

        return "idle";
    }

    private static bool SafeBusy()
    {
        try { return OtherBusyProbe(); }
        catch { return true; }
    }

    // ---- the start check (restart updates staged before this start) -------------------------------------------

    private static UpdatePins ReadFile()
    {
        string? root = Safe(BepInExRootProbe);
        return string.IsNullOrEmpty(root) ? new UpdatePins() : ClientUpdateInputs.ReadPins(root!);
    }

    internal static bool EditFile(Func<UpdatePins, bool> change, out string error)
    {
        error = "";
        string? root = Safe(BepInExRootProbe);
        if (string.IsNullOrEmpty(root)) { error = "no BepInEx folder"; return false; }
        return ClientUpdateInputs.EditPins(root!, change, line => Remember(line), out error);
    }

    /// <summary>After a start: judge each restart update staged before it (the apply journal: the file's <c>verify</c> entries).</summary>
    internal static List<string> RunStartCheck()
    {
        var file = ReadFile();
        if (file.Verify.Count == 0) return new List<string>();
        var result = UpdateStartChecks.Run(StoreProbe(), file, change => EditFile(change, out _), LoadFactsProbe, line => Log?.Invoke(line), LogPrefix);
        foreach (var l in result.Lines) Remember("start check: " + l);
        var outcomes = new List<ServerUpdateOutcome>();
        foreach (var p in result.Loaded)
        {
            Audit("startcheck", p.Package, p.Previous, p.To, "restart", "ok", "loaded after the restart");
            var o = new ServerUpdateOutcome { Package = p.Package, Name = p.Name, From = p.Previous, To = p.To, Route = "restart", Result = ServerUpdateOutcome.Applied, Detail = "loaded after the restart" };
            Record(o);
            outcomes.Add(o);
        }

        foreach (var p in result.Rolled)
        {
            Audit("rollback", p.Package, p.To, p.Previous, "restart", p.LoadFailed ? "failed(load)" : "failed(not-loaded)", p.OutcomeDetail);
            var o = new ServerUpdateOutcome
            {
                Package = p.Package, Name = p.Name, From = p.Previous, To = p.To, Route = "restart", Result = ServerUpdateOutcome.RolledBack,
                Detail = p.OutcomeDetail, LoadFailed = p.LoadFailed,
            };
            Record(o);
            outcomes.Add(o);
        }

        if (outcomes.Count > 0) SetNotice(AdminNoticeRule.Batch(outcomes, RestartPending().Count));
        return result.Lines;
    }

    /// <summary>The restart updates staged and waiting for the next start (package@version), from the apply journal.</summary>
    internal static List<string> RestartPending()
    {
        var list = new List<string>();
        foreach (var kv in ReadFile().Verify) list.Add(kv.Key + " " + kv.Value.Version);
        return list;
    }

    // ---- checks and passes ------------------------------------------------------------------------------------

    /// <summary>Start a check: read the index (one conditional request when <paramref name="request"/>, else the cached body), detect, then Notify or apply.</summary>
    internal static string BeginCheck(bool request, string origin)
    {
        lock (Gate)
        {
            if (_running) return "a check or an update is already running";
            if (_restarting) return "the server is restarting";
            if (_countdownEndsUtc != null) return "a restart countdown is running";
            _running = true;
            _runState = "checking";
            _lastPassUtc = UtcNow();
        }

        int gen = _generation;
        _ = RunOff(() => CheckAsync(request, origin, gen));
        return "started";
    }

    private static async Task CheckAsync(bool request, string origin, int gen)
    {
        var s = SafeSettings();
        try
        {
            ServerIndexRead read;
            try { read = await Pipeline.ReadIndex(request, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) { read = new ServerIndexRead { Ok = false, Error = e.GetBaseException().Message }; }
            if (gen != _generation) return;
            if (!read.Ok)
            {
                lock (Gate) _lastCheckError = read.Error;
                Audit("check", result: "failed(" + Short(read.Error) + ")", detail: "origin " + origin + (read.Requested ? "; one request" : "; cached index"));
                return;
            }

            string? gameRoot = Safe(GameRootProbe);
            string? bepRoot = Safe(BepInExRootProbe);
            UpdateReport report;
            if (string.IsNullOrEmpty(gameRoot) || string.IsNullOrEmpty(bepRoot)) report = UpdateReport.Empty;
            else report = Compute(read.Entries, read.IsLocal, gameRoot!, bepRoot!, s, Pipeline.Installed, UtcNow(), read.Stamp);
            int auto = 0;
            foreach (var i in report.Items) if (i.Auto) auto++;
            string line = report.Count + " update(s) (" + auto + " automatic), " + report.Managed + " managed package(s)";
            lock (Gate)
            {
                _report = report;
                _lastCheckLine = line;
                _lastCheckError = "";
            }

            Audit("check", result: "ok",
                detail: (report.Count == 0 ? "no updates (" + report.Managed + " managed packages)" : line) + "; origin " + origin
                    + (read.Requested ? (read.NotModified ? "; one request, HTTP 304" : "; one request") : "; cached index, no request"));
            foreach (var skip in report.Skipped)
                if (skip.Reason != UpdateDetection.SkipNotListed) Remember("skipped " + skip.Package + ": " + skip.Reason);

            var due = new List<UpdateItem>();
            var available = new List<string>();
            foreach (var i in report.Items)
            {
                if (i.Pinned) continue;
                if (i.Auto && s.Effective == UpdateMode.Automatic)
                {
                    bool tried;
                    lock (Gate) tried = Attempted.Contains(i.Package + "@" + i.Latest) && !Held.ContainsKey(i.Package);
                    if (!tried) due.Add(i);
                }
                else
                {
                    available.Add(i.Name + " " + i.Installed + " → " + i.Latest + (i.Major ? " (major)" : "") + (i.NotAutoReason.Length > 0 ? " · " + i.NotAutoReason : ""));
                    Audit("plan", i.Package, i.Installed, i.Latest, "notify", "skipped(" + (s.Effective == UpdateMode.Automatic ? Short(i.NotAutoReason) : "notify") + ")");
                }
            }

            AdminUpdateNotice? batch = null;
            if (due.Count > 0) batch = await PassAsync(due, s, origin, gen).ConfigureAwait(false);
            // The waiting notice (once per signature): what is available, held and staged. With a batch it is one notice:
            // what was done, then what still waits (live 2026-10-09: two notices in a row replaced each other on screen).
            var waiting = new List<string>(available);
            lock (Gate)
                foreach (var kv in Held) waiting.Add(kv.Key + " (" + kv.Value + ")");
            var waitingNotice = AdminNoticeRule.Waiting(waiting, RestartPending());
            lock (Gate) _joinNotice = waitingNotice;
            var notice = AdminNoticeRule.Combine(batch, waitingNotice);
            if (notice != null) SetNotice(notice);
        }
        catch (Exception e)
        {
            Audit("check", result: "failed(internal)", detail: e.GetBaseException().Message);
        }
        finally
        {
            lock (Gate)
            {
                if (gen == _generation)
                {
                    _running = false;
                    _runState = "idle";
                }
            }
        }
    }

    private static string Short(string? s)
    {
        string t = (s ?? "").Replace(' ', '-');
        return t.Length > 40 ? t.Substring(0, 40) : t;
    }

    /// <summary>Detection on the server: the ledger's managed packages against the index (pure apart from file reads).</summary>
    internal static UpdateReport Compute(IReadOnlyList<CatalogEntry> entries, Func<string, bool>? isLocal, string gameRoot, string bepRoot,
        ServerUpdateSettings s, Func<string, bool>? dependencyInstalled, DateTime nowUtc, string stamp)
    {
        var managed = ClientUpdateInputs.ReadManaged(gameRoot);
        var file = s.Overlay(ClientUpdateInputs.ReadPins(bepRoot));
        var listings = ClientUpdateInputs.ListingsOf(entries, isLocal);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in managed) keys.Add(m.Key);
        return UpdateDetection.Detect(managed, listings, file, s.Effective, s.MajorsAutomatic,
            key => keys.Contains(key) || (dependencyInstalled?.Invoke(key) ?? false), showNsfw: false, nowUtc, stamp);
    }

    /// <summary>One prepared update of a pass.</summary>
    private sealed class Prepared
    {
        public UpdateItem Item = new();
        public bool ClientsNeed;
        public InstallPlanSet? Set;
        public string Error = "";
        public ServerApprovedImage? Image;
        public ServerApplyRoute Route;
        public string HoldWhy = "";
        public PackageSide NewSide = PackageSide.Unknown;
    }

    /// <summary>Prepare, decide and write one batch; returns the batch's admin notice (sent by the caller), or null.</summary>
    private static async Task<AdminUpdateNotice?> PassAsync(List<UpdateItem> due, ServerUpdateSettings s, string origin, int gen)
    {
        lock (Gate) _runState = "preparing";
        var prepared = new List<Prepared>();
        foreach (var item in due)
        {
            var p = new Prepared { Item = item };
            prepared.Add(p);
            lock (Gate) Attempted.Add(item.Package + "@" + item.Latest);
            bool runningNeeds;
            try { runningNeeds = (bool)(await RunOnMain(() => ClientsNeedProbe(item.PluginIds)).ConfigureAwait(false))!; }
            catch { runningNeeds = true; }
            p.ClientsNeed = true;
            try
            {
                var inspected = await Pipeline.Inspect(item.Package, CancellationToken.None).ConfigureAwait(false);
                if (!inspected.Ok) { p.Error = inspected.Error ?? "the package could not be checked"; continue; }
                // The update's own side as well as the running copy's row (review 2026-10-09): unknown means clients need it.
                p.NewSide = inspected.Inspection?.Side ?? PackageSide.Unknown;
                p.ClientsNeed = ServerApplyPlan.ClientsNeed(runningNeeds, p.NewSide);
                var set = await Pipeline.Plan(item.Package, CancellationToken.None).ConfigureAwait(false);
                if (!set.Ok) { p.Error = set.Error ?? "the update cannot be planned"; continue; }
                foreach (var member in set.Members)
                {
                    string key = member?.Package ?? "";
                    if (key.Length == 0 || string.Equals(key, item.Package, StringComparison.OrdinalIgnoreCase)) continue;
                    bool there;
                    try { there = Pipeline.Installed(key); }
                    catch { there = false; }
                    if (!there) { p.Error = "it needs " + key + ", which is not installed (asks first)"; break; }
                }

                if (p.Error.Length > 0) continue;
                p.Set = set;
                var image = (ServerApprovedImage?)await RunOnMain(() => Pipeline.Approve(set, item.Latest)).ConfigureAwait(false);
                if (image == null || image.Error.Length > 0 || image.Bytes.Length == 0)
                {
                    p.Error = image?.Error is { Length: > 0 } err ? err : "the update has more than one file or no plugin id (update it from the Install tab)";
                    continue;
                }

                p.Image = image;
            }
            catch (Exception e) { p.Error = e.GetBaseException().Message; }
        }

        if (gen != _generation) return null;
        var now = UtcNow();
        int online;
        // The players are read on the main thread (the directory touches the world).
        try { online = (int)(await RunOnMain(() => OnlineProbe()).ConfigureAwait(false))!; }
        catch { online = 1; }
        bool quiet;
        lock (Gate) quiet = Empty.Quiet(now, s.EmptyMinutes);
        var writes = new List<Prepared>();
        var outcomes = new List<ServerUpdateOutcome>();
        foreach (var p in prepared)
        {
            var i = p.Item;
            if (p.Error.Length > 0 || p.Image == null)
            {
                Audit("download", i.Package, i.Installed, i.Latest, "-", "failed(" + Short(p.Error) + ")", p.Error);
                var o = new ServerUpdateOutcome { Package = i.Package, Name = i.Name, From = i.Installed, To = i.Latest, Result = ServerUpdateOutcome.Failed, Detail = p.Error, ClientsNeed = p.ClientsNeed };
                Record(o);
                outcomes.Add(o);
                SafeRelease(i.Package);
                lock (Gate) Held.Remove(i.Package);
                continue;
            }

            var (route, why) = ServerApplyPlan.Decide(p.Image.Hot, p.ClientsNeed, online, quiet);
            p.Route = route;
            p.HoldWhy = why;
            if (route == ServerApplyRoute.Hold)
            {
                lock (Gate) Held[i.Package] = why;
                Audit("plan", i.Package, i.Installed, i.Latest, "held", "skipped(held)", why + "; clients need it, so it applies once the server has been empty for " + s.EmptyMinutes + " min");
                Record(new ServerUpdateOutcome { Package = i.Package, Name = i.Name, From = i.Installed, To = i.Latest, Route = "held", Result = ServerUpdateOutcome.Held, Detail = why, ClientsNeed = true });
                SafeRelease(i.Package);
                continue;
            }

            Audit("plan", i.Package, i.Installed, i.Latest, route == ServerApplyRoute.Now ? "hot" : "restart", "ok",
                (p.ClientsNeed ? "clients need it (the new version declares side " + p.NewSide.ToString().ToLowerInvariant() + ")" : "server only")
                + (p.Image.Hot ? "" : "; " + p.Image.NotHotReason));
            writes.Add(p);
        }

        if (writes.Count > 0)
        {
            // One pre-update backup per batch, before the batch's first write.
            var labels = new List<(string, string)>();
            foreach (var p in writes) labels.Add((p.Item.Name, p.Item.Latest));
            lock (Gate) _runState = "backing up";
            string? backupRefusal = await PreUpdateBackupAsync(s, PreUpdateBackupRule.Label(labels), "batch").ConfigureAwait(false);
            if (backupRefusal != null)
            {
                foreach (var p in writes)
                {
                    var i = p.Item;
                    lock (Gate) Attempted.Remove(i.Package + "@" + i.Latest);
                    SafeRelease(i.Package);
                    Audit("apply", i.Package, i.Installed, i.Latest, p.Route == ServerApplyRoute.Now ? "hot" : "restart", "skipped(backup)",
                        "not applied: the pre-update backup failed (" + backupRefusal + "); tried again at the next check");
                    var o = new ServerUpdateOutcome { Package = i.Package, Name = i.Name, From = i.Installed, To = i.Latest, Result = ServerUpdateOutcome.Deferred, Detail = "the pre-update backup failed: " + backupRefusal };
                    Record(o);
                }

                writes.Clear();
            }
        }

        lock (Gate) _runState = "applying";
        foreach (var p in writes)
        {
            if (gen != _generation) return null;
            var o = await ApplyOneAsync(p, s).ConfigureAwait(false);
            Record(o);
            outcomes.Add(o);
        }

        return outcomes.Count > 0 ? AdminNoticeRule.Batch(outcomes, RestartPending().Count) : null;
    }

    private static void SafeRelease(string package)
    {
        try { Pipeline.Release(package); }
        catch { /* */ }
    }

    /// <summary>Write one update through the version store and judge its load (hot), or stage it (restart).</summary>
    private static async Task<ServerUpdateOutcome> ApplyOneAsync(Prepared p, ServerUpdateSettings s)
    {
        var i = p.Item;
        var image = p.Image!;
        var o = new ServerUpdateOutcome { Package = i.Package, Name = i.Name, From = i.Installed, To = i.Latest, ClientsNeed = p.ClientsNeed };
        StoreUpdateResult result;
        try
        {
            result = (StoreUpdateResult)(await RunOnMain(() =>
            {
                // The empty re-check, in the same main-thread step as the write.
                int onlineNow;
                try { onlineNow = OnlineProbe(); }
                catch { onlineNow = 1; }
                if (p.Route == ServerApplyRoute.Now && ServerApplyPlan.WriteRefusal(p.ClientsNeed, onlineNow) is { Length: > 0 } refuse)
                    return StoreUpdateResult.Defer(refuse);
                var svc = StoreProbe();
                string? game = Safe(GameRootProbe);
                // The apply journal first (review 2026-10-09): a quit between the store's write and the journal must still
                // leave the next start a record to judge. It is dropped again below unless the update was staged.
                string journalPrevious = i.Installed;
                bool journaled = EditFile(f =>
                {
                    f.Verify[i.Package] = new StartCheck
                    {
                        PluginId = image.PluginId, Version = i.Latest, Sha256 = image.Sha256, Previous = journalPrevious, File = "", Utc = UtcNow(),
                    };
                    return true;
                }, out string journalError);
                if (!journaled) return StoreUpdateResult.Fail("the apply journal could not be written: " + journalError);
                return StoreUpdate.Apply(svc, new StoreUpdateRequest
                {
                    Package = i.Package,
                    Version = image.Version.Length > 0 ? image.Version : i.Latest,
                    PluginId = image.PluginId,
                    Bytes = image.Bytes,
                    HotImage = image.Hot,
                    NotHotReason = image.NotHotReason,
                    Ledger = image.Ledger,
                }, (string package, string path, out string message) =>
                {
                    message = "";
                    return !string.IsNullOrEmpty(game) && PendingSupersede.TryRemove(game!, package, path, out message);
                });
            }).ConfigureAwait(false))!;
        }
        catch (Exception e) { result = StoreUpdateResult.Fail(e.GetBaseException().Message); }
        finally { SafeRelease(i.Package); }

        // The journal entry written before the write stays only for a staged restart update.
        if (result.Deferred || result.NotApplicable || !result.Ok || result.Route != "restart") DropJournal(i.Package, image.Sha256);

        if (result.Deferred)
        {
            lock (Gate)
            {
                Held[i.Package] = result.Message;
                Attempted.Remove(i.Package + "@" + i.Latest);
            }

            o.Route = "held";
            o.Result = ServerUpdateOutcome.Held;
            o.Detail = "not written: " + result.Message;
            Audit("apply", i.Package, i.Installed, i.Latest, "held", "skipped(" + Short(result.Message) + ")", o.Detail);
            return o;
        }

        lock (Gate) Held.Remove(i.Package);
        if (result.NotApplicable || !result.Ok)
        {
            o.Route = p.Route == ServerApplyRoute.Now ? "hot" : "restart";
            o.Result = ServerUpdateOutcome.Failed;
            o.Detail = result.NotApplicable ? "not updated automatically: " + result.Message + " (update it from the Install tab)" : result.Message;
            Audit("apply", i.Package, i.Installed, i.Latest, o.Route, "failed(" + Short(result.NotApplicable ? "not-in-store" : "store") + ")", o.Detail);
            return o;
        }

        string previous = result.Previous.Length > 0 ? result.Previous : i.Installed;
        o.From = previous;
        var files = new List<string> { result.Path };
        if (result.Route == "restart")
        {
            o.Route = "restart";
            o.Result = ServerUpdateOutcome.Staged;
            o.Detail = "staged in progenitor-pending; applies at the next start" + (result.Superseded.Length > 0 ? "; " + result.Superseded : "");
            // The apply journal: the start after the restart judges this exact image.
            EditFile(f =>
            {
                f.Verify[i.Package] = new StartCheck
                {
                    PluginId = image.PluginId, Version = i.Latest, Sha256 = result.Sha256, Previous = previous,
                    File = System.IO.Path.GetFileName(result.Path ?? ""), Utc = UtcNow(),
                };
                return true;
            }, out _);
            Audit("stage", i.Package, previous, i.Latest, "restart", "ok", o.Detail, files);
            return o;
        }

        o.Route = "hot";
        var (verdict, detail) = await WaitLoadedAsync(image.PluginId, result.Sha256).ConfigureAwait(false);
        if (verdict == LoadVerdict.Loaded || verdict == LoadVerdict.Unknown)
        {
            o.Result = ServerUpdateOutcome.Applied;
            o.Detail = verdict == LoadVerdict.Loaded ? "loaded by the reloader" : "written; whether it loaded cannot be checked here (" + detail + ")";
            Audit("apply", i.Package, previous, i.Latest, "hot", verdict == LoadVerdict.Loaded ? "ok" : "ok(unverified)", o.Detail, files);
            return o;
        }

        // A hot update that does not load goes back to the previous version; only a load that threw is a failed version.
        bool threw = verdict == LoadVerdict.Failed;
        o.LoadFailed = threw;
        string why = threw ? detail : "it did not load in time (" + detail + "); it is tried again later";
        var back = (ModVersionResult)(await RunOnMain(() =>
        {
            var svc = StoreProbe();
            return svc == null
                ? ModVersionResult.Refuse(ModVersionCode.NotFound, "no version store")
                : StoreUpdate.RollBack(svc, image.PluginId, previous, forget: threw ? i.Latest : "");
        }).ConfigureAwait(false))!;
        if (threw)
            EditFile(f => { f.AddFailed(i.Package, i.Latest, "failed to load: " + why, UtcNow()); return true; }, out _);
        else
            lock (Gate) Attempted.Remove(i.Package + "@" + i.Latest);
        o.Result = ServerUpdateOutcome.RolledBack;
        o.Detail = why + (back.Ok ? " Rolled back to " + previous + "." : " Rolling back to " + previous + " failed: " + back.Message);
        Audit("rollback", i.Package, i.Latest, previous, "hot", back.Ok ? "ok" : "failed(" + Short(back.Message) + ")",
            o.Detail + (threw ? " It goes on the server's failed list and is not tried again automatically." : ""));
        return o;
    }

    private static void DropJournal(string package, string sha)
    {
        EditFile(f => f.Verify.TryGetValue(package, out var v) && string.Equals(v.Sha256, sha, StringComparison.OrdinalIgnoreCase) && f.Verify.Remove(package), out _);
    }

    /// <summary>Wait until the image is loaded, failed, or the deadline passed (<see cref="LoadCheck"/>).</summary>
    private static Task<(LoadVerdict, string)> WaitLoadedAsync(string pluginId, string sha)
    {
        var done = new TaskCompletionSource<(LoadVerdict, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = UtcNow();
        double busySeconds = 0;
        var last = started;
        void Poll()
        {
            try
            {
                var now = UtcNow();
                var facts = LoadFactsProbe?.Invoke(pluginId, "") ?? new LoadFacts();
                if (facts.LoaderBusy) busySeconds += (now - last).TotalSeconds;
                last = now;
                var (verdict, detail) = LoadCheck.Judge(facts, sha, started);
                if (verdict is LoadVerdict.Loaded or LoadVerdict.Failed || (verdict == LoadVerdict.Unknown && (now - started).TotalSeconds >= 3))
                {
                    done.TrySetResult((verdict, detail));
                    return;
                }

                if (LoadCheck.PastDeadline((now - started).TotalSeconds, busySeconds))
                {
                    done.TrySetResult((LoadVerdict.Waiting, detail + " after " + (int)(now - started).TotalSeconds + " s"));
                    return;
                }

                Schedule(0.25, Poll);
            }
            catch (Exception e) { done.TrySetResult((LoadVerdict.Unknown, "the load could not be checked: " + e.Message)); }
        }

        Schedule(0.25, Poll);
        return done.Task;
    }

    // ---- the pre-update backup --------------------------------------------------------------------------------

    /// <summary>Null when the batch may write (a backup was made, skipped or is off), else why the backup failed.</summary>
    private static async Task<string?> PreUpdateBackupAsync(ServerUpdateSettings s, string label, string batch)
    {
        IReadOnlyList<WorldBackupEntry> entries;
        try { entries = BackupEntriesProbe?.Invoke() ?? Array.Empty<WorldBackupEntry>(); }
        catch { entries = Array.Empty<WorldBackupEntry>(); }
        var decision = PreUpdateBackupRule.Decide(s.BackupBeforeUpdate, false, entries, s.PreUpdateBackupSkipHours, UtcNow());
        if (decision.Action == "off") return null;
        if (decision.Action == "skip")
        {
            Audit("backup", result: "skipped(recent)", detail: "a backup newer than " + s.PreUpdateBackupSkipHours + " h stands in for it: " + decision.StandIn
                + "; " + batch + ": " + label);
            return null;
        }

        var start = StartBackup;
        if (start == null) return "this server cannot make a world backup from here";
        var done = new TaskCompletionSource<BackupOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? refusal = (string?)await RunOnMain(() => start(label, s.PreUpdateBackupKeep, outcome => done.TrySetResult(outcome))).ConfigureAwait(false);
        if (refusal != null)
        {
            Audit("backup", result: "failed(not-started)", detail: refusal + "; " + batch + ": " + label);
            return refusal;
        }

        var outcome = await done.Task.ConfigureAwait(false);
        if (!outcome.Ok)
        {
            Audit("backup", result: "failed", detail: outcome.Reason + "; " + batch + ": " + label);
            return outcome.Reason.Length > 0 ? outcome.Reason : "the backup failed";
        }

        Audit("backup", result: "ok", detail: outcome.FolderName + " (" + WorldBackupCatalog.FormatBytes(outcome.Bytes) + ")"
            + (outcome.Pruned.Count > 0 ? "; retention removed " + string.Join(", ", outcome.Pruned) : "") + "; " + batch + ": " + label);
        return null;
    }

    /// <summary>
    /// Bridge: one pre-update backup now under the batch rules (the setting, the skip window, the retention), as a batch would
    /// take it. Null when it was made, skipped or is off (the audit line says which), else why it failed.
    /// </summary>
    internal static Task<string?> PreUpdateBackupForBridge(string label) => RunOffValue(() => PreUpdateBackupAsync(SafeSettings(), label, "bridge"));

    private static async Task<string?> RunOffValue(Func<Task<string?>> work)
    {
        string? result = null;
        await RunOff(async () => { result = await work().ConfigureAwait(false); }).ConfigureAwait(false);
        return result;
    }

    // ---- restarts ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Restart to apply the staged updates: <paramref name="warn"/> (Restart now with players online) announces the restart at
    /// <c>WarnMinutes</c> and one minute, then (also without a warning) takes the pre-update backup when it is on, and saves and
    /// quits. Null when it started, else why not.
    /// </summary>
    internal static string? BeginRestart(string origin, string by, bool warn)
    {
        if (StopServer == null) return "this server cannot save and stop itself from here";
        if (SafeBusy()) return "a world backup or a world restore is running";
        int online;
        try { online = OnlineProbe(); }
        catch { online = 1; }
        bool countdown = warn && online != 0;
        lock (Gate)
        {
            // Claimed in the same lock as the pass check, so no pass can start between the two (review 2026-10-09).
            if (_restarting || _countdownEndsUtc != null) return "a restart is already under way";
            if (_running) return "a check or an update is running; try again in a moment";
            if (!countdown) _restarting = true;
            else _countdownEndsUtc = UtcNow().AddSeconds(ServerUpdateSettings.ClampWarnMinutes(SafeSettings().WarnMinutes) * 60);
        }

        if (countdown)
        {
            StartCountdown(origin, by);
            return null;
        }

        var decided = UtcNow();
        _ = RunOff(() => RestartAsync(origin, by, warned: false, decided));
        return null;
    }

    /// <summary>Restart now's warning: announcements at <c>WarnMinutes</c> and one minute, then the restart (warned).</summary>
    private static void StartCountdown(string origin, string by)
    {
        var s = SafeSettings();
        var pending = RestartPending();
        var mods = new List<string>();
        foreach (var kv in ReadFile().Verify) mods.Add(kv.Key);
        int total = ServerUpdateSettings.ClampWarnMinutes(s.WarnMinutes) * 60;
        int gen;
        lock (Gate)
        {
            gen = ++_countdownGen;
            _countdownEndsUtc = UtcNow().AddSeconds(total);
            _countdownBy = by;
        }

        Audit("restart", result: "countdown", detail: "by " + by + "; " + total + " s warning; " + (pending.Count > 0 ? string.Join(", ", pending) : "no staged update"));
        foreach (int at in RestartCountdown.Announcements(s.WarnMinutes))
        {
            int say = at;
            Schedule(total - say, () =>
            {
                if (gen != _countdownGen) return;
                SafeBroadcast(RestartCountdown.Line(say, mods));
            });
        }

        Schedule(total, () =>
        {
            lock (Gate)
            {
                if (gen != _countdownGen) return;
                _countdownEndsUtc = null;
                _restarting = true;
            }

            var decided = UtcNow();
            _ = RunOff(() => RestartAsync(origin, by, warned: true, decided));
        });
    }

    /// <summary>An admin cancelled Restart now during its warning. Null when cancelled, else why not.</summary>
    internal static string? CancelRestart(string by)
    {
        lock (Gate)
        {
            if (_countdownEndsUtc == null) return "no restart countdown is running";
            _countdownGen++;
            _countdownEndsUtc = null;
        }

        SafeBroadcast("The restart is cancelled.");
        Audit("restart", result: "cancelled", detail: "by " + by);
        return null;
    }

    /// <summary>
    /// Core is unloading (a hot swap, a shutdown): a running countdown or restart of this generation ends here. The sentence
    /// to tell the players and admins (the caller says it before the seams go), or null when nothing was running. The next
    /// generation does not resume it: the updates keep waiting for a restart and the admin can press Restart now again.
    /// </summary>
    internal static string? OnUnloading()
    {
        bool countdown, restarting;
        lock (Gate)
        {
            countdown = _countdownEndsUtc != null;
            restarting = _restarting;
            if (!countdown && !restarting) return null;
            _countdownGen++;
            _countdownEndsUtc = null;
        }

        Audit("restart", result: "cancelled", detail: "Progenitor reloaded during the " + (countdown ? "countdown" : "restart") + "; the updates keep waiting for a restart");
        return countdown ? "The restart is cancelled: Progenitor reloaded. Updates still wait for a restart." : null;
    }

    private static void SafeBroadcast(string text)
    {
        try { Broadcast?.Invoke(text); }
        catch { /* */ }
    }

    /// <summary>A pass that started before the restart claimed the state finishes first (at most this long).</summary>
    internal static TimeSpan PassWait { get; set; } = TimeSpan.FromMinutes(10);

    private static async Task RestartAsync(string origin, string by, bool warned, DateTime decidedUtc)
    {
        lock (Gate) _restarting = true;
        var s = SafeSettings();
        var pending = RestartPending();
        bool fallBackToCountdown = false;
        try
        {
            var waitUntil = DateTime.UtcNow + PassWait;
            while (Running)
            {
                if (DateTime.UtcNow > waitUntil)
                {
                    Audit("restart", result: "failed(busy)", detail: "a check or an update was still running");
                    lock (Gate) _lastRestartFailureUtc = UtcNow();
                    return;
                }

                await Task.Delay(250).ConfigureAwait(false);
            }

            DateTime since = decidedUtc;
            // The first look: nobody online or joining (Restart when empty), or a warning first (Restart now).
            string gate = (string)(await RunOnMain(() => (object?)StopGateNow(origin, warned, since)).ConfigureAwait(false))!;
            if (gate != RestartStopGate.Stop)
            {
                fallBackToCountdown = Refused(gate, origin);
                return;
            }

            var labels = new List<(string, string)>();
            foreach (var kv in ReadFile().Verify) labels.Add((kv.Key, kv.Value.Version));
            string? backupFailed = await PreUpdateBackupAsync(s, PreUpdateBackupRule.Label(labels), "restart").ConfigureAwait(false);
            if (backupFailed != null)
            {
                lock (Gate) _lastRestartFailureUtc = UtcNow();
                Audit("restart", result: "failed(backup)", detail: "the restart is cancelled: the pre-update backup failed (" + backupFailed + ")");
                if (warned) SafeBroadcast("The restart is cancelled.");
                SetNotice(new AdminUpdateNotice
                {
                    Kind = "failed", Text = "The restart for mod updates was cancelled: the world backup failed.", OfferRestart = pending.Count > 0,
                    Signature = "restart-failed|" + UtcNow().Ticks,
                });
                return;
            }

            var stopped = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            // The last look and the stop in one main-thread step (review 2026-10-09): the backup took minutes, and a player
            // who connected meanwhile is never dropped by a quit nobody warned them of.
            gate = (string)(await RunOnMain(() =>
            {
                string g = StopGateNow(origin, warned, since);
                if (g != RestartStopGate.Stop) return g;
                if (warned || origin != "empty") SafeBroadcast("The server is stopping now to update mods.");
                Audit("restart", result: "ok", detail: "by " + by + " (" + origin + "): save and quit; " + (pending.Count > 0 ? "applies " + string.Join(", ", pending) : "no staged update")
                    + "; the host starts the server again (" + (s.HostRestarts.Length > 0 ? s.HostRestarts : "nothing is known to") + ")");
                lock (Gate) _lastRestart = UtcNow().ToString("o", CultureInfo.InvariantCulture) + " " + origin;
                StopServer!(reply => Remember("stop: " + reply), refused => stopped.TrySetResult(refused));
                return g;
            }).ConfigureAwait(false))!;
            if (gate != RestartStopGate.Stop)
            {
                fallBackToCountdown = Refused(gate, origin);
                return;
            }

            // A refusal comes back within the save's confirmation window; a quit never answers.
            var first = await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false);
            if (first == stopped.Task && stopped.Task.Result is { } why)
            {
                lock (Gate) _lastRestartFailureUtc = UtcNow();
                Audit("restart", result: "failed(save)", detail: why + "; the server keeps running and tries again at the next empty moment");
                if (warned || origin != "empty") SafeBroadcast("The restart is cancelled: the world could not be saved.");
                SetNotice(new AdminUpdateNotice
                {
                    Kind = "failed", Text = "The restart for mod updates was cancelled: the world could not be saved (" + why + ").", OfferRestart = pending.Count > 0,
                    Signature = "restart-save-failed|" + UtcNow().Ticks,
                });
            }
        }
        catch (Exception e)
        {
            lock (Gate) _lastRestartFailureUtc = UtcNow();
            Audit("restart", result: "failed(internal)", detail: e.GetBaseException().Message);
        }
        finally
        {
            lock (Gate) _restarting = false;
            if (fallBackToCountdown)
            {
                bool claimed;
                lock (Gate)
                {
                    claimed = !_restarting && _countdownEndsUtc == null && !_running;
                    if (claimed) _countdownEndsUtc = UtcNow();
                }

                if (claimed) StartCountdown(origin, by);
            }
        }
    }

    /// <summary>The players now against <see cref="RestartStopGate"/> (main thread).</summary>
    private static string StopGateNow(string origin, bool warned, DateTime since)
    {
        int online;
        try { online = OnlineProbe(); }
        catch { online = 1; }
        DateTime? lastConnect;
        lock (Gate)
        {
            if (online >= 0) Empty.Observe(online, UtcNow());
            lastConnect = Empty.LastConnectUtc;
        }

        return RestartStopGate.Decide(origin, warned, online, lastConnect, since);
    }

    /// <summary>A restart that did not stop: audit it; true when Restart now falls back to its warned countdown.</summary>
    private static bool Refused(string gate, string origin)
    {
        if (gate == RestartStopGate.Abort)
        {
            Audit("restart", result: "skipped(player-joined)", detail: "a player connected before the " + origin + " restart; the quiet period starts again");
            return false;
        }

        Audit("restart", result: "deferred(player-joined)", detail: "a player connected before the restart; they are warned first");
        return true;
    }

    // ---- notices ------------------------------------------------------------------------------------------------

    /// <summary>Keep <paramref name="notice"/> as the current one and send it to the admins online, once per signature.</summary>
    internal static void SetNotice(AdminUpdateNotice? notice)
    {
        if (notice == null) return;
        bool send;
        lock (Gate)
        {
            _notice = notice;
            send = AdminNoticeRule.ShouldSend(notice, _lastNoticeSent);
            if (send) _lastNoticeSent = notice.Signature;
        }

        if (!send) return;
        Remember("notice: " + notice.Text);
        try { NotifyAdmins?.Invoke(notice); }
        catch (Exception e) { Remember("notice not sent: " + e.Message); }
    }

    /// <summary>
    /// An admin joined (after the handshake): the current waiting notice when updates wait and this admin has not had it, else
    /// null. Only a waiting notice is repeated (what was applied before they joined is in the Server tab's status).
    /// </summary>
    internal static AdminUpdateNotice? NoticeForJoiningAdmin(ulong steamId)
    {
        lock (Gate)
        {
            // The last check's waiting notice (also when it went out folded into a batch's notice).
            var n = _joinNotice ?? (_notice is { Kind: "available" or "restart" } w ? w : null);
            if (n == null) return null;
            AdminSeen.TryGetValue(steamId, out var seen);
            if (!AdminNoticeRule.ShouldSend(n, seen) || (seen ?? "").Contains(n.Signature, StringComparison.Ordinal)) return null;
            if (AdminSeen.Count > 256) AdminSeen.Clear();
            AdminSeen[steamId] = n.Signature;
            return n;
        }
    }

    /// <summary>An admin got <paramref name="notice"/> (the live sender notes it, so a join does not repeat it).</summary>
    internal static void NoteSent(ulong steamId, AdminUpdateNotice notice)
    {
        lock (Gate)
        {
            if (AdminSeen.Count > 256) AdminSeen.Clear();
            AdminSeen[steamId] = notice.Signature;
        }
    }

    // ---- admin actions (the Server tab's Updates group) --------------------------------------------------------

    /// <summary>Check now: one conditional request now (refused while a pass runs). Null when started, else why not.</summary>
    internal static string? CheckNow(string by)
    {
        var s = SafeSettings();
        if (s.Effective == UpdateMode.Off) return "Server updates are Off ([AutoUpdate] Mods).";
        string r = BeginCheck(true, "check now by " + by);
        return r == "started" ? null : r;
    }

    /// <summary>Restart now: with players online, the warning first. Null when started, else why not.</summary>
    internal static string? RestartNow(string by)
    {
        if (RestartPending().Count == 0) return "No update waits for a restart.";
        return BeginRestart("now", by, warn: true);
    }

    /// <summary>Roll a plugin back to its previous kept version and keep it there (pinned). The sentence.</summary>
    internal static string RollBack(string pluginId, string package)
    {
        var svc = StoreProbe();
        if (svc == null) return "Keeping versions is not available here.";
        var target = StoreUpdate.RollbackTarget(svc.Manifest().Find(pluginId));
        if (target == null) return "No earlier version of it is kept.";
        var r = StoreUpdate.RollBack(svc, pluginId, target.Version);
        if (r.Ok)
        {
            EditFile(f =>
            {
                foreach (var kv in new List<KeyValuePair<string, StartCheck>>(f.Verify))
                    if (string.Equals(kv.Value.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)) f.Verify.Remove(kv.Key);
                if (package.Length > 0) f.Pins[package] = ClientAutoUpdate.LedgerVersionOf(target) ?? target.Version;
                return true;
            }, out _);
        }

        Audit("rollback", package, "", target.Version, r.Ok ? r.Route : "-", r.Ok ? "ok" : "failed(" + ModVersionResult.CodeName(r.Code) + ")", "by admin; " + r.Message);
        return r.Ok ? "Rolled back to " + target.Version + ". " + r.Message + " It is kept at this version." : "Can't roll back to " + target.Version + ": " + r.Message;
    }

    internal static int ClearFailed(string package)
    {
        int n = 0;
        EditFile(f =>
        {
            if (!f.Failed.TryGetValue(package ?? "", out var list)) return false;
            n = list.Count;
            f.Failed.Remove(package!);
            return true;
        }, out _);
        lock (Gate) Attempted.RemoveWhere(a => a.StartsWith(package + "@", StringComparison.OrdinalIgnoreCase));
        if (n > 0) Audit("clear-failed", package ?? "", result: "ok", detail: n + " version(s)");
        return n;
    }

    /// <summary>Forget this session's attempts (the bridge), so the next pass retries the same versions.</summary>
    internal static int ForgetAttempts()
    {
        lock (Gate)
        {
            int n = Attempted.Count;
            Attempted.Clear();
            return n;
        }
    }

    // ---- status -------------------------------------------------------------------------------------------------

    /// <summary>The Server tab's status line: <c>Checked 05:00 · 1 applied live · 2 waiting for a restart</c>.</summary>
    internal static string StatusLine()
    {
        var s = SafeSettings();
        if (s.Effective == UpdateMode.Off) return s.Bound ? "Off" : "Off (the [AutoUpdate] settings are not loaded yet)";
        var parts = new List<string>();
        DateTime? last;
        int available = 0, held, applied = 0, failed = 0;
        lock (Gate)
        {
            last = _lastPassUtc;
            foreach (var i in _report.Items) if (!i.Pinned && !(i.Auto && s.Effective == UpdateMode.Automatic)) available++;
            held = Held.Count;
            foreach (var o in Outcomes)
            {
                if (o.Result == ServerUpdateOutcome.Applied && o.Route == "hot") applied++;
                if (o.Result is ServerUpdateOutcome.Failed or ServerUpdateOutcome.RolledBack) failed++;
            }
        }

        parts.Add(last is { } l ? "Checked " + l.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) : "Not checked yet");
        if (s.AutomaticHeldByRemoteInstall) parts.Add("Automatic acts as Notify (remote installs are off)");
        if (available > 0) parts.Add(available == 1 ? "1 update available" : available + " updates available");
        if (applied > 0) parts.Add(applied + " applied live");
        if (held > 0) parts.Add(held + " held until the server is empty");
        int restart = RestartPending().Count;
        if (restart > 0) parts.Add(restart + " waiting for a restart");
        if (failed > 0) parts.Add(failed + " failed");
        int countdown = CountdownSeconds;
        if (countdown >= 0) parts.Add("restarting in " + countdown + " s");
        if (parts.Count == 1 && last != null) parts.Add("no updates");
        return string.Join(" · ", parts);
    }

    /// <summary>The last results (newest last), one short sentence each.</summary>
    internal static List<string> RecentLines()
    {
        var list = new List<string>();
        foreach (var o in RecentOutcomes)
            list.Add(o.Name + " " + (o.From.Length > 0 ? o.From + " → " : "") + o.To + ": " + o.Result + (o.Detail.Length > 0 ? " (" + o.Detail + ")" : ""));
        return list;
    }

    internal static Dictionary<string, object?> Describe()
    {
        var s = SafeSettings();
        var items = new List<object?>();
        var report = Report;
        foreach (var i in report.Items)
            items.Add(new Dictionary<string, object?>
            {
                ["package"] = i.Package, ["name"] = i.Name, ["installed"] = i.Installed, ["latest"] = i.Latest, ["major"] = i.Major, ["pinnedAt"] = i.PinnedAt,
                ["auto"] = i.Auto, ["notAuto"] = i.NotAutoReason, ["pluginIds"] = new List<string>(i.PluginIds), ["newDependencies"] = new List<string>(i.NewDependencies),
                ["failedBefore"] = i.FailedBefore,
            });
        var skipped = new List<object?>();
        foreach (var sk in report.Skipped) skipped.Add(sk.Package + ": " + sk.Reason);
        var outcomes = new List<object?>();
        lock (Gate) foreach (var o in Outcomes) outcomes.Add(o.Describe());
        var file = ReadFile();
        var failed = new Dictionary<string, object?>();
        foreach (var kv in file.Failed)
        {
            var rows = new List<object?>();
            foreach (var f in kv.Value) rows.Add(f.Describe());
            failed[kv.Key] = rows;
        }

        var verify = new Dictionary<string, object?>();
        foreach (var kv in file.Verify)
            verify[kv.Key] = new Dictionary<string, object?> { ["pluginId"] = kv.Value.PluginId, ["version"] = kv.Value.Version, ["previous"] = kv.Value.Previous, ["sha256"] = kv.Value.Sha256, ["file"] = kv.Value.File };
        var now = UtcNow();
        lock (Gate)
        {
            return new Dictionary<string, object?>
            {
                ["settings"] = s.Describe(),
                ["line"] = StatusLineUnlocked(),
                ["runState"] = _runState,
                ["worldLoadedUtc"] = _worldLoadedUtc?.ToString("o", CultureInfo.InvariantCulture),
                ["lastPassUtc"] = _lastPassUtc?.ToString("o", CultureInfo.InvariantCulture),
                ["lastCheck"] = _lastCheckLine,
                ["lastCheckError"] = _lastCheckError,
                ["items"] = items,
                ["skipped"] = skipped,
                ["held"] = new Dictionary<string, string>(Held),
                ["attempted"] = new List<string>(Attempted),
                ["restartPending"] = RestartPendingUnlocked(file),
                ["online"] = Empty.Online,
                ["emptySinceUtc"] = Empty.EmptySince?.ToString("o", CultureInfo.InvariantCulture),
                ["quiet"] = Empty.Quiet(now, s.EmptyMinutes),
                ["quietRemainingSeconds"] = Empty.Remaining(now, s.EmptyMinutes) is { } left ? (int)Math.Ceiling(left.TotalSeconds) : null,
                ["restartRule"] = RestartWhenEmptyRule.Decide(s, file.Verify.Count, Empty, now, false, _lastRestartFailureUtc) is { Length: > 0 } why ? why : "would restart now",
                ["countdownSeconds"] = _countdownEndsUtc is { } ends ? Math.Max(0, (int)Math.Ceiling((ends - now).TotalSeconds)) : -1,
                ["countdownBy"] = _countdownBy,
                ["restarting"] = _restarting,
                ["lastRestart"] = _lastRestart,
                ["lastRestartFailureUtc"] = _lastRestartFailureUtc?.ToString("o", CultureInfo.InvariantCulture),
                ["notice"] = _notice?.Describe(),
                ["lastNoticeSent"] = _lastNoticeSent,
                ["outcomes"] = outcomes,
                ["failed"] = failed,
                ["verify"] = verify,
                ["history"] = new List<string>(History),
            };
        }
    }

    private static List<string> RestartPendingUnlocked(UpdatePins file)
    {
        var list = new List<string>();
        foreach (var kv in file.Verify) list.Add(kv.Key + " " + kv.Value.Version);
        return list;
    }

    // StatusLine takes the lock itself; Describe calls it with the lock held (Monitor is reentrant).
    private static string StatusLineUnlocked() => StatusLine();

    // ---- tests and teardown ------------------------------------------------------------------------------------

    /// <summary>Tests: the world is loaded and the start check is done (the next tick checks).</summary>
    internal static void SkipStartForTests()
    {
        lock (Gate)
        {
            _worldLoadedUtc = UtcNow().AddHours(-1);
            _startChecked = true;
        }
    }

    /// <summary>Forget everything (Core unloading, tests). A running pass's continuation is dropped by the generation.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _generation++;
            _countdownGen++;
            _countdownEndsUtc = null;
            _countdownBy = "";
            _report = UpdateReport.Empty;
            _lastPassUtc = null;
            _worldLoadedUtc = null;
            _startChecked = false;
            _lastCheckLine = "";
            _lastCheckError = "";
            _running = false;
            _restarting = false;
            _runState = "idle";
            Held.Clear();
            Attempted.Clear();
            Outcomes.Clear();
            History.Clear();
            _notice = null;
            _joinNotice = null;
            _lastNoticeSent = "";
            AdminSeen.Clear();
            _lastRestartFailureUtc = null;
            _lastRestart = "";
            Empty = new EmptyGate();
        }

        SettingsProbe = () => ServerUpdateSettings.Unbound;
        BepInExRootProbe = () => null;
        GameRootProbe = () => null;
        UtcNow = () => DateTime.UtcNow;
        OnlineProbe = () => 0;
        CacheStampProbe = () => null;
        Pipeline = new ServerUpdatePipeline();
        StoreProbe = () => null;
        LoadFactsProbe = null;
        ClientsNeedProbe = _ => true;
        RunOnMain = work => Task.FromResult(work());
        Schedule = (_, a) => a();
        RunOff = work => Task.Run(work);
        Log = null;
        AuditSink = null;
        StartBackup = null;
        BackupEntriesProbe = null;
        NotifyAdmins = null;
        Broadcast = null;
        StopServer = null;
        OtherBusyProbe = () => false;
        PassWait = TimeSpan.FromMinutes(10);
    }
}
