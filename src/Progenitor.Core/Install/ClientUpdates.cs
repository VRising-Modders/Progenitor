using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Progenitor.Install;

/// <summary>One update the runner prepared (downloaded, inspected and planned) and will apply on a Yes.</summary>
internal sealed class PreparedUpdate
{
    public string Package { get; set; } = "";
    public string Name { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public bool Major { get; set; }

    public IReadOnlyList<string> PluginIds { get; set; } = Array.Empty<string>();

    /// <summary>Hot writes of this update's plugins, for the caller to wait on.</summary>
    public List<HotWrite> HotWrites { get; } = new();

    /// <summary>The members of the plan that are not installed yet (new dependencies), by package key.</summary>
    public List<string> NewPackages { get; } = new();

    /// <summary>The first lines of the new version's changelog (from the downloaded package), or empty.</summary>
    public string Changelog { get; set; } = "";

    /// <summary>Why it cannot update (inspection or plan refused), or empty.</summary>
    public string Error { get; set; } = "";

    /// <summary>After applying: <c>loaded</c>, <c>restart</c> (loads at the next start) or <c>failed</c>.</summary>
    public string Outcome { get; set; } = "";

    public string OutcomeDetail { get; set; } = "";

    /// <summary>AUTOUPDATE2: <c>store</c> (through the version store) or <c>install</c> (the install pipeline's own write).</summary>
    public string Route { get; set; } = "";

    /// <summary>AUTOUPDATE2: the version the update replaced, kept in the version store (store route), or empty.</summary>
    public string Previous { get; set; } = "";

    /// <summary>AUTOUPDATE2: the update failed to load and the previous version was put back.</summary>
    public bool RolledBack { get; set; }

    /// <summary>Review M1: the load threw (a real failure, entered in the failed list); a timeout leaves this false.</summary>
    public bool LoadFailed { get; set; }

    /// <summary>Review M2: written, but nothing could tell whether it loaded.</summary>
    public bool Unverified { get; set; }

    /// <summary>Review H1: written only while the apply window is open (an automatic pass); the store and the install route check it.</summary>
    public bool RequireWindow { get; set; }

    /// <summary>AUTOUPDATE2: what the write superseded (a copy waiting for the next start), or empty.</summary>
    public string Superseded { get; set; } = "";

    public bool Ok => Error.Length == 0;

    public Dictionary<string, object?> Describe() => new()
    {
        ["package"] = Package,
        ["name"] = Name,
        ["from"] = From,
        ["to"] = To,
        ["major"] = Major,
        ["newPackages"] = new List<string>(NewPackages),
        ["changelog"] = Changelog,
        ["error"] = Error,
        ["outcome"] = Outcome,
        ["detail"] = OutcomeDetail,
        ["route"] = Route,
        ["previous"] = Previous,
        ["rolledBack"] = RolledBack,
        ["superseded"] = Superseded,
        ["loadFailed"] = LoadFailed,
        ["unverified"] = Unverified,
    };
}

/// <summary>The install pipeline as the runner uses it (live: the Install tab's <see cref="InstallCatalogService"/>).</summary>
internal sealed class UpdatePipeline
{
    public Func<string, CancellationToken, Task<InspectCallResult>> Inspect { get; set; } =
        (_, _) => Task.FromResult(new InspectCallResult(false, "no install catalog", null, null));

    public Func<string, CancellationToken, Task<InstallPlanSet>> Plan { get; set; } =
        (_, _) => Task.FromResult(InstallPlanSet.Refuse("no install catalog"));

    /// <summary>Writes one planned set. Runs on the game's main thread (<see cref="RunOnMain"/>).</summary>
    public Func<InstallPlanSet, ExecuteSetResult> Execute { get; set; } = _ => ExecuteSetResult.Refuse("no install catalog");

    public Action<string> Release { get; set; } = _ => { };

    public Func<string, string?> ZipPath { get; set; } = _ => null;

    public Func<Func<ExecuteSetResult>, Task<ExecuteSetResult>> RunOnMain { get; set; } = work => Task.FromResult(work());

    /// <summary>Post work to the main thread (results, dialogs).</summary>
    public Action<Action> Post { get; set; } = a => a();

    /// <summary>Is this package already installed (a plan member that is not new).</summary>
    public Func<string, bool> Installed { get; set; } = _ => false;

    /// <summary>
    /// AUTOUPDATE2: apply one planned update through the version store (main thread). Null, or a result with
    /// <see cref="StoreUpdateResult.NotApplicable"/>: this update takes the install pipeline (<see cref="Execute"/>).
    /// </summary>
    public Func<PreparedUpdate, InstallPlanSet, Task<StoreUpdateResult?>> ApplyStore { get; set; } =
        (_, _) => Task.FromResult<StoreUpdateResult?>(null);

    /// <summary>
    /// AUTOUPDATE2: wait until the image (sha256) is loaded for the plugin, it failed, or the deadline passed
    /// (<see cref="LoadCheck"/>: 15 s, longer while the loader is busy). <see cref="LoadVerdict.Waiting"/> at the end is a
    /// timeout; <see cref="LoadVerdict.Unknown"/> means nothing can tell (unverified, not success).
    /// </summary>
    public Func<string, string, Task<(LoadVerdict Verdict, string Detail)>> WaitLoaded { get; set; } =
        (_, _) => Task.FromResult((LoadVerdict.Loaded, ""));

    /// <summary>
    /// AUTOUPDATE2 review H1: why the apply window is closed right now (main thread), or empty. Read immediately before
    /// each write of an automatic pass; a closed window defers the update to the next window.
    /// </summary>
    public Func<string> WindowClosed { get; set; } = () => "";

    /// <summary>AUTOUPDATE2: put the previous version back after a failed load (main thread). Returns the sentence.</summary>
    public Func<PreparedUpdate, string, Task<string>> RollBackFailed { get; set; } =
        (_, _) => Task.FromResult("nothing was put back");

    /// <summary>AUTOUPDATE2: a restart update to check after the next start (the update, the image's sha256, the queued file).</summary>
    public Action<PreparedUpdate, string, string> NoteRestart { get; set; } = (_, _, _) => { };
}

/// <summary>
/// AUTOUPDATE1 (M6.1): the client's update state. Detection runs on every index body the client already reads (the Install
/// tab's cache serve and checks, the join screens' reads) and after installs, pins and setting changes; it never makes a
/// request. The badge, the Install tab's update list and the pre-join prompt read <see cref="Report"/>. <b>Update</b> and
/// <b>Update all</b> run the existing install pipeline (download, inspection, dependency plan, ledgered write) after a
/// confirmation; nothing is applied without a click in this phase. Game-free: the live probes are set by <c>ModsMenu</c>.
/// </summary>
internal static class ClientUpdates
{
    public const string LogPrefix = "AutoUpdate: ";
    public const int MaxChangelogChars = 240;

    private static readonly object Gate = new();
    private static UpdateReport _report = UpdateReport.Empty;
    private static int _version;
    private static bool _computing;
    private static string? _again;
    private static string _lastWhy = "";
    private static string _lastLogged = "";
    private static string _signature = "";

    // Live probes (ModsMenu wires them; tests set their own).
    internal static Func<IReadOnlyList<CatalogEntry>>? EntriesProbe { get; set; }
    internal static Func<string, bool>? LocalKeyProbe { get; set; }
    internal static Func<string?>? GameRootProbe { get; set; }
    internal static Func<string?>? BepInExRootProbe { get; set; }
    internal static Func<UpdateMode> ModeProbe { get; set; } = () => UpdateSettings.Parse(UpdateSettings.DefaultMode);
    internal static Func<bool> PromptProbe { get; set; } = () => UpdateSettings.DefaultPrompt;

    /// <summary>AUTOUPDATE2: <c>[AutoUpdate] MajorVersions</c> is Automatic.</summary>
    internal static Func<bool> MajorProbe { get; set; } = () => UpdateSettings.MajorAutomatic(UpdateSettings.DefaultMajor);
    internal static Func<bool> NsfwProbe { get; set; } = () => false;

    /// <summary>INDEX1 (L4): [Mods] UseThunderstore is off (a Thunderstore install's update says so).</summary>
    internal static Func<bool> ThunderstoreOffProbe { get; set; } = () => false;
    internal static Func<string, bool>? DependencyInstalledProbe { get; set; }
    internal static Action<Action> PostMain { get; set; } = a => a();
    internal static Func<Func<UpdateReport>, Task<UpdateReport>> RunOff { get; set; } = work => Task.Run(work);
    internal static Action<string>? Log { get; set; }
    internal static UpdatePipeline Pipeline { get; set; } = new();

    /// <summary>The handshake's request on this client (live: the pre-join prompt remembers it per server).</summary>
    internal static Action<IReadOnlyList<ServerRequirement>>? ServerAsked { get; set; }

    /// <summary>
    /// Called by the handshake with the rows a server asked about. Never throws. Mod compatibility C3: the server's
    /// Version matching comes from its request bits (noted by the handshake just before), so the rows are remembered with
    /// it; a new request also forgets the previous server's catalog facts.
    /// </summary>
    internal static void NoteServerAsked(IEnumerable<Networking.Handshake.ModRecord> rows)
    {
        lock (Gate)
        {
            _connectedCatalog = new List<ServerRequirement>();
            _connectedMatching = null;
        }
        var hook = ServerAsked;
        if (hook == null) return;
        try { hook(ServerRequirements.Of(rows, ServerMatchingMode())); }
        catch (Exception e)
        {
            try { Log?.Invoke(LogPrefix + "could not note the server's requirements: " + e.Message); }
            catch { /* */ }
        }
    }

    /// <summary>C3: the joined server's Version matching, from its request bits (declared ranges when unreadable).</summary>
    internal static Networking.VersionMatchingMode ServerMatchingMode()
    {
        try { return Networking.VersionMatchJudge.ModeOf(Networking.ModCompatCaps.ServerCaps); }
        catch { return Networking.VersionMatchingMode.Range; }
    }

    /// <summary>
    /// C3: the catalog's per-mod facts on this client, and the server-wide Version matching the catalog was signed with
    /// (<see cref="ServerRequirements.CatalogMatching"/>: null when the catalog did not say). Live: the pre-join prompt
    /// merges them into the remembered requirements.
    /// </summary>
    internal static Action<IReadOnlyList<ServerRequirement>, string?>? ServerCatalog { get; set; }

    private static List<ServerRequirement> _connectedCatalog = new();

    // C3: the Version matching of the last verified catalog this session (exact, minor, "" for ranges), or null when no
    // catalog said. A re-signed catalog carries the server's mode now; the request bits only the mode at the join.
    private static string? _connectedMatching;

    /// <summary>
    /// C3: called with a verified catalog body from the joined server: its per-mod <c>peers</c> and <c>match</c> and its
    /// <c>matching</c> (extended form; absent from an older server) are remembered for the next pre-join prompt and used
    /// while connected. A catalog re-signed mid-session (an admin changed Version matching or a per-mod rule) replaces
    /// what the join's request bits said. Never throws.
    /// </summary>
    internal static void NoteServerCatalog(Networking.ManifestBody? body) =>
        NoteServerCatalog(body?.Mods, body?.Matching);

    /// <summary><see cref="NoteServerCatalog(Networking.ManifestBody?)"/> from the rows and the body's <c>matching</c> word.</summary>
    internal static void NoteServerCatalog(IEnumerable<Networking.ManifestMod>? mods, string? matchingWord = null)
    {
        List<ServerRequirement> facts;
        string? matching;
        try
        {
            facts = ServerRequirements.FromCatalog(mods);
            matching = ServerRequirements.CatalogMatching(matchingWord);
        }
        catch { return; }
        lock (Gate)
        {
            _connectedCatalog = facts;
            _connectedMatching = matching;
        }
        var hook = ServerCatalog;
        if (hook == null || (facts.Count == 0 && matching == null)) return;
        try { hook(facts, matching); }
        catch (Exception e)
        {
            try { Log?.Invoke(LogPrefix + "could not note the server's catalog ranges: " + e.Message); }
            catch { /* */ }
        }
    }

    /// <summary>
    /// C3: the connected server's requirements: its live request rows with the Version matching from its bits and the
    /// catalog facts seen this session. The latest catalog's Version matching, when it carried one, wins over the bits.
    /// </summary>
    internal static List<ServerRequirement> ConnectedRequirements(IEnumerable<Networking.Handshake.ModRecord>? rows)
    {
        var list = ServerRequirements.Of(rows, ServerMatchingMode());
        List<ServerRequirement> catalog;
        string? matching;
        lock (Gate)
        {
            catalog = _connectedCatalog;
            matching = _connectedMatching;
        }
        ServerRequirements.Merge(list, catalog);
        ServerRequirements.ApplyMatching(list, matching);
        return list;
    }

    /// <summary>Tests replace it: the effective peer range of a package version, or null. Default: the verified-inspection cache.</summary>
    internal static Func<string, string, string?>? CandidateRangeProbe { get; set; }

    /// <summary>
    /// C3: the effective peer range of <paramref name="item"/>'s package at <paramref name="version"/>, read from its
    /// verified inspection (<c>progenitor-cache/verified/…</c>); null when that version was never inspected.
    /// </summary>
    internal static string? CandidateRange(UpdateItem item, string version)
    {
        if (item == null || string.IsNullOrEmpty(version)) return null;
        try
        {
            var probe = CandidateRangeProbe;
            if (probe != null) return probe(item.Package, version);
            string? root = BepInExRootProbe?.Invoke();
            if (string.IsNullOrEmpty(root)) return null;
            return new VerifiedManifestCache(root!).EffectivePeersOf(item.Package, version);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Opens the Update all confirmation (live: a <c>UiDialog</c>). Arguments: title, body, the per-mod facts
    /// (label, value), Yes caption, then what Yes and No do. Returns false when no dialog could open.
    /// </summary>
    internal static Func<string, string, IReadOnlyList<(string Label, string Value)>, string, Action, Action, bool>? OpenConfirm { get; set; }

    /// <summary>Shows the outcome of a batch (live: the same dialog kit). Title, body.</summary>
    internal static Action<string, string>? ShowResult { get; set; }

    /// <summary>
    /// Review item 5: the live handshake rows of the server this client is connected to (null when not in a world or
    /// no request was received). Live: <c>Handshake.CopyServerAsked</c>.
    /// </summary>
    internal static Func<IReadOnlyList<ServerRequirement>?>? ConnectedProbe { get; set; }

    /// <summary>Why <paramref name="item"/> is not updated now (the connected server checks another version), or empty.</summary>
    public static string BlockedReason(UpdateItem item)
    {
        IReadOnlyList<ServerRequirement>? rows;
        try { rows = ConnectedProbe?.Invoke(); }
        catch { rows = null; }
        return item == null ? "" : PreJoinOffers.BlockedBy(item, rows, CandidateRange);
    }

    /// <summary>The Install tab's result dialog for a batch (nothing after a Cancel: the player chose it).</summary>
    public static void ShowOutcome(IReadOnlyList<PreparedUpdate> prepared, UpdateRunEnd end)
    {
        if (end == UpdateRunEnd.Cancelled) return;
        bool applied = AnyApplied(prepared);
        string title = applied ? (prepared.Count == 1 ? "Mod updated" : "Mods updated") : "Nothing was updated";
        try { ShowResult?.Invoke(title, ResultBody(prepared)); }
        catch (Exception e)
        {
            try { Log?.Invoke(LogPrefix + "result dialog failed: " + e.Message); }
            catch { /* */ }
        }
    }

    /// <summary>A batch wrote at least one update.</summary>
    public static bool AnyApplied(IReadOnlyList<PreparedUpdate> prepared)
    {
        foreach (var p in prepared) if (p.Outcome is "loaded" or "restart") return true;
        return false;
    }

    /// <summary>The last detection result.</summary>
    public static UpdateReport Report { get { lock (Gate) return _report; } }

    /// <summary>Moves on every new report and pin change (the badge and the Install list compare it).</summary>
    public static int Version { get { lock (Gate) return _version; } }

    public static UpdateMode Mode
    {
        get
        {
            try { return ModeProbe(); }
            catch { return UpdateMode.Automatic; }
        }
    }

    public static bool PromptOn
    {
        get
        {
            try { return PromptProbe(); }
            catch { return UpdateSettings.DefaultPrompt; }
        }
    }

    /// <summary>The badge's N (0 while <b>Update mods</b> is Off).</summary>
    public static int BadgeCount => Mode == UpdateMode.Off ? 0 : Report.Count;

    public static string BadgeText => BadgeTextFor(BadgeCount);

    public static string BadgeTextFor(int n) =>
        n <= 0 ? "" : n == 1 ? "1 update available" : n.ToString(CultureInfo.InvariantCulture) + " updates available";

    /// <summary>The update list's header: <c>Updates · Checked 14:05 · 2 updates available</c>.</summary>
    public static string HeaderText(DateTime? localNow = null)
    {
        var r = Report;
        if (!r.HasRun) return "Updates · not checked yet";
        string when = r.ComputedUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        int n = r.Count;
        string count = n == 0 ? "up to date" : BadgeTextFor(n);
        return "Updates · Checked " + when + " · " + count;
    }

    /// <summary>
    /// Run detection now (off the main thread) on the index the client already holds. Coalesced: a call while a pass runs
    /// runs one more pass after it. Never makes a request.
    /// </summary>
    public static void Recheck(string why)
    {
        lock (Gate)
        {
            if (_computing) { _again = why; return; }
            _computing = true;
            _lastWhy = why ?? "";
        }

        IReadOnlyList<CatalogEntry> entries;
        try { entries = EntriesProbe?.Invoke() ?? Array.Empty<CatalogEntry>(); }
        catch { entries = Array.Empty<CatalogEntry>(); }
        bool nsfw;
        try { nsfw = NsfwProbe(); }
        catch { nsfw = false; }
        var localProbe = LocalKeyProbe;
        string? gameRoot = Safe(GameRootProbe);
        string? bepRoot = Safe(BepInExRootProbe);
        string stamp = entries.Count.ToString(CultureInfo.InvariantCulture);
        var mode = Mode;
        bool majors;
        try { majors = MajorProbe(); }
        catch { majors = false; }

        Task<UpdateReport> task;
        try
        {
            task = RunOff(() => Compute(entries, localProbe, gameRoot, bepRoot, nsfw, stamp, mode, majors));
        }
        catch (Exception e)
        {
            task = Task.FromException<UpdateReport>(e);
        }

        task.ContinueWith(t =>
        {
            UpdateReport report = t.Status == TaskStatus.RanToCompletion ? t.Result : UpdateReport.Empty;
            string? error = t.Exception?.GetBaseException().Message;
            PostMain(() => Adopt(report, error));
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    internal static UpdateReport Compute(IReadOnlyList<CatalogEntry> entries, Func<string, bool>? isLocal, string? gameRoot,
        string? bepRoot, bool nsfw, string stamp, UpdateMode mode = UpdateMode.Automatic, bool majorsAutomatic = false)
    {
        if (string.IsNullOrEmpty(gameRoot)) return UpdateReport.Empty;
        var managed = ClientUpdateInputs.ReadManaged(gameRoot!);
        var pins = string.IsNullOrEmpty(bepRoot) ? new UpdatePins() : ClientUpdateInputs.ReadPins(bepRoot!);
        var listings = ClientUpdateInputs.ListingsOf(entries, isLocal);
        var managedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in managed) managedKeys.Add(m.Key);
        return UpdateDetection.Detect(managed, listings, pins, mode, majorsAutomatic, managedKeys.Contains, nsfw, DateTime.UtcNow, stamp, ThunderstoreOff());
    }

    private static bool ThunderstoreOff()
    {
        try { return ThunderstoreOffProbe(); }
        catch { return false; }
    }

    private static void Adopt(UpdateReport report, string? error)
    {
        // A dependency another tool installed is not in Progenitor's ledger: ask the Install tab's own installed state
        // (main thread) before calling it new.
        var probe = DependencyInstalledProbe;
        if (probe != null)
            foreach (var item in report.Items)
            {
                if (item.NewDependencies.Count == 0) continue;
                var still = new List<string>();
                foreach (var d in item.NewDependencies)
                {
                    bool there;
                    try { there = probe(d); }
                    catch { there = false; }
                    if (!there) still.Add(d);
                }
                item.NewDependencies = still;
            }

        string? again;
        string signature = error == null ? SignatureOf(report) : _signature;
        lock (Gate)
        {
            if (error == null) _report = report;
            // Only a changed list moves the version: the Install tab rebuilds on it, and a build asks for a pass.
            if (!string.Equals(signature, _signature, StringComparison.Ordinal))
            {
                _signature = signature;
                _version++;
            }
            _computing = false;
            again = _again;
            _again = null;
        }

        string line = error != null
            ? "detection failed (" + error + ")"
            : report.Count + " update(s), " + (report.Items.Count - report.Count) + " kept, " + report.Managed + " managed package(s)";
        if (!string.Equals(line, _lastLogged, StringComparison.Ordinal))
        {
            _lastLogged = line;
            try { Log?.Invoke(LogPrefix + line + " [" + _lastWhy + "]"); }
            catch { /* */ }
        }

        if (again != null) Recheck(again);
    }

    internal static string SignatureOf(UpdateReport report)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(report.Managed).Append('|');
        foreach (var i in report.Items)
        {
            sb.Append(i.Package).Append('@').Append(i.Installed).Append('>').Append(i.Latest).Append(':').Append(i.PinnedAt);
            foreach (var d in i.NewDependencies) sb.Append('+').Append(d);
            sb.Append(';');
        }

        return sb.ToString();
    }

    // ---- Keep this version (decision 9) ----

    /// <summary>Pin <paramref name="package"/> at its installed version, or unpin it. The file write is off the main thread.</summary>
    public static bool SetPinned(string package, bool pinned, out string message)
    {
        message = "";
        string? root = Safe(BepInExRootProbe);
        if (string.IsNullOrEmpty(root)) { message = "no BepInEx folder"; return false; }
        var item = Report.Find(package ?? "");
        if (pinned && item == null) { message = "no update is listed for " + package; return false; }
        bool was = true;
        // Review L1: the one locked, atomic edit of the auto-update file (modes, failed list and start checks survive).
        if (!ClientUpdateInputs.EditPins(root!, pins =>
            {
                if (pinned) pins.Pins[item!.Package] = item.Installed;
                else was = pins.Pins.Remove(package ?? "");
                return pinned || was;
            }, line => { try { Log?.Invoke(LogPrefix + line); } catch { /* */ } }, out string error))
        {
            message = "could not save: " + error;
            return false;
        }

        if (!was) { message = package + " is not kept at a version"; return false; }
        message = pinned ? "kept at " + item!.Installed : "unpinned";
        try { Log?.Invoke(LogPrefix + (pinned ? "keep " + item!.Package + " at " + item.Installed : "unpin " + package)); }
        catch { /* */ }
        Recheck(pinned ? "pin" : "unpin");
        return true;
    }

    // ---- Update and Update all (decision 7) ----

    private static readonly object RunGate = new();
    private static string _runState = "idle";
    private static string _runOrigin = "";
    private static List<PreparedUpdate> _prepared = new();
    private static int _runToken;

    /// <summary><c>idle</c>, <c>preparing</c>, <c>confirming</c>, <c>applying</c>, <c>done</c> or <c>cancelled</c>.</summary>
    public static string RunState { get { lock (RunGate) return _runState; } }

    public static bool Busy
    {
        get
        {
            string s = RunState;
            return s == "preparing" || s == "confirming" || s == "applying";
        }
    }

    public static IReadOnlyList<PreparedUpdate> Prepared { get { lock (RunGate) return _prepared.ToArray(); } }

    /// <summary>
    /// Update the listed packages through the install pipeline: download, inspect and plan each (off the main thread),
    /// then — when <paramref name="confirm"/> — one confirmation listing every update (versions, changelog lines, new
    /// mods it installs, refusals); Yes writes each, dependencies first. <paramref name="done"/> runs on the main thread
    /// with the prepared list (each carries its outcome) and whether anything was applied.
    /// </summary>
    public static bool BeginUpdate(IReadOnlyList<string> packages, string origin, bool confirm,
        Action<IReadOnlyList<PreparedUpdate>, UpdateRunEnd>? done, out string message)
    {
        message = "";
        var list = new List<UpdateItem>();
        var report = Report;
        foreach (var p in packages ?? Array.Empty<string>())
        {
            var item = report.Find(p);
            if (item != null && !list.Contains(item)) list.Add(item);
        }

        if (list.Count == 0) { message = "nothing to update"; return false; }

        // Review item 5: while connected, a mod the server checks at another version is not updated.
        var blocked = new List<string>();
        for (int i = list.Count - 1; i >= 0; i--)
        {
            string why = BlockedReason(list[i]);
            if (why.Length == 0) continue;
            blocked.Add(list[i].Name + " (" + why + ")");
            list.RemoveAt(i);
        }

        if (list.Count == 0) { message = "not updated: " + string.Join(", ", blocked); return false; }
        if (blocked.Count > 0)
        {
            try { Log?.Invoke(LogPrefix + "not updated while connected: " + string.Join(", ", blocked)); }
            catch { /* */ }
        }

        int token;
        lock (RunGate)
        {
            if (_runState is "preparing" or "confirming" or "applying") { message = "an update is already running"; return false; }
            _runState = "preparing";
            _runOrigin = origin ?? "";
            _prepared = new List<PreparedUpdate>();
            token = ++_runToken;
        }

        try { Log?.Invoke(LogPrefix + "op=prepare origin=" + origin + " packages=" + Join(list)); }
        catch { /* */ }
        _ = PrepareAsync(list, confirm, done, token);
        message = "preparing " + list.Count + " update(s)";
        return true;
    }

    private static string Join(List<UpdateItem> list)
    {
        var parts = new List<string>();
        foreach (var i in list) parts.Add(i.Package + "@" + i.Installed + "->" + i.Latest);
        return string.Join(",", parts);
    }

    private static async Task PrepareAsync(List<UpdateItem> items, bool confirm,
        Action<IReadOnlyList<PreparedUpdate>, UpdateRunEnd>? done, int token)
    {
        var pipe = Pipeline;
        var prepared = new List<PreparedUpdate>();
        foreach (var item in items)
        {
            var p = new PreparedUpdate
            {
                Package = item.Package, Name = item.Name, From = item.Installed, To = item.Latest, Major = item.Major, PluginIds = item.PluginIds,
                RequireWindow = AutomaticOrigin(_runOrigin),
            };
            prepared.Add(p);
            try
            {
                var inspected = await pipe.Inspect(item.Package, CancellationToken.None).ConfigureAwait(false);
                if (!inspected.Ok) { p.Error = inspected.Error ?? "the package could not be checked"; continue; }
                p.Changelog = ChangelogOf(pipe.ZipPath(item.Package));
                var set = await pipe.Plan(item.Package, CancellationToken.None).ConfigureAwait(false);
                if (!set.Ok) { p.Error = set.Error ?? "the update cannot be planned"; continue; }
                foreach (var member in set.Members)
                {
                    string key = member?.Package ?? "";
                    if (key.Length == 0 || string.Equals(key, item.Package, StringComparison.OrdinalIgnoreCase)) continue;
                    bool installed;
                    try { installed = pipe.Installed(key); }
                    catch { installed = false; }
                    if (!installed && !p.NewPackages.Contains(key)) p.NewPackages.Add(key);
                }
            }
            catch (Exception e)
            {
                p.Error = e.GetBaseException().Message;
            }
        }

        pipe.Post(() => AfterPrepare(prepared, confirm, done, token));
    }

    /// <summary>An automatic pass (origin <c>auto</c>, <c>auto-…</c>): its writes need the apply window open at the moment of the write.</summary>
    internal static bool AutomaticOrigin(string? origin) =>
        (origin ?? "").Equals("auto", StringComparison.Ordinal) || (origin ?? "").StartsWith("auto-", StringComparison.Ordinal);

    /// <summary>The first lines of the newest entry of a package's changelog, plain text, or empty.</summary>
    internal static string ChangelogOf(string? zip)
    {
        if (string.IsNullOrEmpty(zip)) return "";
        try
        {
            string? text = LocalIndexSource.DocumentsOfZip(zip!).Changelog;
            if (string.IsNullOrWhiteSpace(text)) return "";
            MarkdownPlain.SplitLatest(text, out string latest, out _);
            string plain = MarkdownPlain.Render(latest, MaxChangelogChars * 2);
            var lines = new List<string>();
            foreach (var raw in plain.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                lines.Add(line);
                if (lines.Count >= 3) break;
            }

            return MarkdownPlain.Truncate(string.Join(" · ", lines), MaxChangelogChars);
        }
        catch { return ""; }
    }

    private static void AfterPrepare(List<PreparedUpdate> prepared, bool confirm,
        Action<IReadOnlyList<PreparedUpdate>, UpdateRunEnd>? done, int token)
    {
        lock (RunGate)
        {
            if (token != _runToken) return;
            _prepared = prepared;
        }

        int ok = 0;
        foreach (var p in prepared) if (p.Ok) ok++;
        bool unaskedNew = false;
        foreach (var p in prepared) if (p.Ok && p.NewPackages.Count > 0) unaskedNew = true;

        if (ok == 0)
        {
            // The caller shows the result (the Install tab a dialog, the pre-join prompt its own question).
            Finish(prepared, UpdateRunEnd.NothingPrepared, done, token, "cancelled");
            return;
        }

        // Decision 3: an update that needs a mod the player does not have asks first, even from the pre-join prompt.
        if (!confirm && !unaskedNew)
        {
            _ = ApplyAsync(prepared, done, token);
            return;
        }

        lock (RunGate) _runState = "confirming";
        var facts = new List<(string, string)>();
        foreach (var p in prepared)
        {
            if (!p.Ok)
            {
                facts.Add((p.Name, "cannot update: " + p.Error));
                continue;
            }

            facts.Add((p.Name, p.From + " → " + p.To + (p.Major ? " · major" : "")));
            if (p.NewPackages.Count > 0) facts.Add(("", "also installs " + string.Join(", ", p.NewPackages)));
            if (p.Changelog.Length > 0) facts.Add(("", p.Changelog));
        }

        string title = ok == 1 ? "Update 1 mod?" : "Update " + ok + " mods?";
        string body = "Downloaded and checked. Hot-loadable mods load now; the rest load at the next start.";
        bool opened = false;
        try
        {
            opened = OpenConfirm?.Invoke(title, body, facts, ok == 1 ? "Update" : "Update all",
                () => { _ = ApplyAsync(prepared, done, token); },
                // Cancel, Escape, or the dialog dropped without an answer: nothing is written.
                () =>
                {
                    foreach (var p in prepared) SafeRelease(p.Package);
                    Finish(prepared, UpdateRunEnd.Cancelled, done, token, "cancelled");
                }) ?? false;
        }
        catch (Exception e)
        {
            try { Log?.Invoke(LogPrefix + "the confirmation could not open: " + e.Message); }
            catch { /* */ }
        }

        if (!opened)
        {
            foreach (var p in prepared) SafeRelease(p.Package);
            Finish(prepared, UpdateRunEnd.Cancelled, done, token, "cancelled");
        }
    }

    private static async Task ApplyAsync(List<PreparedUpdate> prepared, Action<IReadOnlyList<PreparedUpdate>, UpdateRunEnd>? done, int token)
    {
        lock (RunGate)
        {
            if (token != _runToken) return;
            _runState = "applying";
        }

        var pipe = Pipeline;
        foreach (var p in prepared)
        {
            if (!p.Ok) { p.Outcome = "skipped"; continue; }
            try
            {
                // Planned again right before the write: an earlier member may have installed a shared dependency, which
                // changes this plan (and its approval hash).
                var set = await pipe.Plan(p.Package, CancellationToken.None).ConfigureAwait(false);
                if (!set.Ok)
                {
                    p.Outcome = "failed";
                    p.OutcomeDetail = set.Error ?? "the update cannot be planned";
                    SafeRelease(p.Package);
                    continue;
                }

                // AUTOUPDATE2: through the version store when it holds the active copy (the previous version is kept).
                StoreUpdateResult? store = null;
                try { store = await pipe.ApplyStore(p, set).ConfigureAwait(false); }
                catch (Exception e) { store = StoreUpdateResult.Fail(e.GetBaseException().Message); }
                if (store != null && store.Deferred)
                {
                    Defer(p, store.Message);
                    continue;
                }

                if (store != null && !store.NotApplicable)
                {
                    SafeRelease(p.Package);
                    p.Route = "store";
                    p.Superseded = store.Superseded;
                    if (!store.Ok)
                    {
                        p.Outcome = "failed";
                        p.OutcomeDetail = store.Message.Length > 0 ? store.Message : "the version store refused it";
                    }
                    else
                    {
                        p.Previous = store.Previous;
                        if (store.Route == "now")
                        {
                            foreach (var id in p.PluginIds)
                                p.HotWrites.Add(new HotWrite(id, store.Path, p.Name, store.Sha256));
                            var (verdict, detail) = await pipe.WaitLoaded(p.PluginIds.Count > 0 ? p.PluginIds[0] : "", store.Sha256).ConfigureAwait(false);
                            if (verdict == LoadVerdict.Loaded)
                            {
                                p.Outcome = "loaded";
                                p.OutcomeDetail = "loaded";
                            }
                            else if (verdict == LoadVerdict.Unknown)
                            {
                                // Review M2: nothing can tell which image is loaded (an older Bootstrap): unverified, kept.
                                p.Outcome = "loaded";
                                p.Unverified = true;
                                p.OutcomeDetail = "written; whether it loaded cannot be checked here (" + detail + ")";
                            }
                            else
                            {
                                // Requirement 3: a hot update that does not load goes back to the previous version. Only a
                                // load that threw is a failed version (never applied automatically again); a timeout is
                                // tried again next session (review M1).
                                p.LoadFailed = verdict == LoadVerdict.Failed;
                                if (!p.LoadFailed) detail = "It did not load in time (" + detail + "); it is tried again next session.";
                                string back = await pipe.RollBackFailed(p, detail).ConfigureAwait(false);
                                p.Outcome = "failed";
                                p.RolledBack = true;
                                p.HotWrites.Clear();
                                p.OutcomeDetail = detail + " " + back;
                            }
                        }
                        else
                        {
                            p.Outcome = "restart";
                            p.OutcomeDetail = "loads at the next start";
                            try { pipe.NoteRestart(p, store.Sha256, store.Path); }
                            catch { /* the start check is a safety net */ }
                        }
                    }

                    LogApply(p);
                    continue;
                }

                p.Route = "install";
                // Review H1: the window is read again on the main thread in the same step as the write.
                var result = await pipe.RunOnMain(() =>
                {
                    string closed = p.RequireWindow ? SafeClosed(pipe) : "";
                    return closed.Length > 0 ? ExecuteSetResult.Refuse(DeferredPrefix + closed) : pipe.Execute(set);
                }).ConfigureAwait(false);
                if (result != null && !result.Ok && (result.Error ?? "").StartsWith(DeferredPrefix, StringComparison.Ordinal))
                {
                    Defer(p, result.Error!.Substring(DeferredPrefix.Length));
                    continue;
                }

                if (result == null || !result.Ok)
                {
                    p.Outcome = "failed";
                    p.OutcomeDetail = result?.Error ?? "the write failed";
                    continue;
                }

                bool hot = true;
                foreach (var m in result.Members)
                {
                    if (m == null || !m.IsRoot) continue;
                    if (!m.HotLoaded) { hot = false; continue; }
                    // Review item 3: the caller waits for this image to be the loaded one, not for a timer.
                    foreach (var id in p.PluginIds)
                        if (!string.IsNullOrEmpty(m.Path)) p.HotWrites.Add(new HotWrite(id, m.Path!, p.Name));
                }
                p.Outcome = hot ? "loaded" : "restart";
                p.OutcomeDetail = hot ? "written; loads within seconds" : "loads at the next start";
            }
            catch (Exception e)
            {
                p.Outcome = "failed";
                p.OutcomeDetail = e.GetBaseException().Message;
            }

            LogApply(p);
        }

        pipe.Post(() =>
        {
            Finish(prepared, UpdateRunEnd.Applied, done, token, "done");
            Recheck("updated");
        });
    }

    internal const string DeferredPrefix = "deferred: ";

    /// <summary>Review H1: the apply window closed before this write: nothing written, tried at the next window.</summary>
    private static void Defer(PreparedUpdate p, string why)
    {
        SafeRelease(p.Package);
        p.Outcome = "deferred";
        p.OutcomeDetail = "not applied now: " + why;
        LogApply(p);
    }

    /// <summary>The window as the pipeline reads it (main thread); a probe that throws reads as closed.</summary>
    internal static string SafeClosed(UpdatePipeline pipe)
    {
        try { return pipe.WindowClosed() ?? ""; }
        catch (Exception e) { return "the window could not be read (" + e.Message + ")"; }
    }

    /// <summary>
    /// Review H1: an update is being downloaded or written (not waiting for a confirmation). The pre-join gate holds a
    /// join until it ends.
    /// </summary>
    public static bool Writing
    {
        get
        {
            string s = RunState;
            return s == "preparing" || s == "applying";
        }
    }

    private static void LogApply(PreparedUpdate p)
    {
        string result = p.RolledBack ? "rolledback" : p.Outcome;
        try
        {
            Log?.Invoke(LogPrefix + "op=apply origin=" + _runOrigin + " package=" + p.Package + " from=" + p.From + " to=" + p.To
                + " route=" + (p.Route.Length > 0 ? p.Route : "-") + " result=" + result
                + (p.OutcomeDetail.Length > 0 ? " detail=\"" + p.OutcomeDetail + "\"" : "")
                + (p.Superseded.Length > 0 ? " superseded=\"" + p.Superseded + "\"" : ""));
        }
        catch { /* */ }
    }

    private static void SafeRelease(string package)
    {
        try { Pipeline.Release(package); }
        catch { /* */ }
    }

    private static void Finish(List<PreparedUpdate> prepared, UpdateRunEnd end, Action<IReadOnlyList<PreparedUpdate>, UpdateRunEnd>? done, int token, string state)
    {
        lock (RunGate)
        {
            if (token != _runToken) return;
            _runState = state;
        }

        try { done?.Invoke(prepared, end); }
        catch (Exception e)
        {
            try { Log?.Invoke(LogPrefix + "completion threw: " + e.Message); }
            catch { /* */ }
        }
    }

    /// <summary>The result line: <c>ChatSkin 1.3.0 loaded now. BroaderMap 2.0.2 loads at the next start.</c></summary>
    public static string ResultBody(IReadOnlyList<PreparedUpdate> prepared)
    {
        var parts = new List<string>();
        foreach (var p in prepared)
        {
            if (!p.Ok) parts.Add(p.Name + " could not update: " + p.Error + ".");
            else if (p.Outcome == "loaded" && p.Unverified) parts.Add(p.Name + " " + p.To + " is installed; whether it loaded could not be checked.");
            else if (p.Outcome == "loaded") parts.Add(p.Name + " " + p.To + " is installed and loads now.");
            else if (p.Outcome == "restart") parts.Add(p.Name + " " + p.To + " loads at the next start.");
            else if (p.RolledBack) parts.Add(p.Name + " " + p.To + " failed to load and was rolled back"
                + (p.Previous.Length > 0 ? " to " + p.Previous : "")
                + (p.LoadFailed ? ". It won't be tried again automatically." : " (it did not load in time; it is tried again next session)."));
            else if (p.Outcome == "failed") parts.Add(p.Name + " " + p.To + " failed: " + p.OutcomeDetail + ".");
            else if (p.Outcome == "deferred") parts.Add(p.Name + " " + p.To + " was not updated now (" + p.OutcomeDetail + ").");
        }

        return string.Join(" ", parts);
    }

    public static Dictionary<string, object?> DescribeRun()
    {
        var prepared = new List<object?>();
        foreach (var p in Prepared) prepared.Add(p.Describe());
        lock (RunGate)
            return new Dictionary<string, object?>
            {
                ["state"] = _runState,
                ["origin"] = _runOrigin,
                ["prepared"] = prepared,
            };
    }

    public static Dictionary<string, object?> Describe()
    {
        var r = Report;
        var items = new List<object?>();
        foreach (var i in r.Items) items.Add(i.Describe());
        var skipped = new List<object?>();
        foreach (var s in r.Skipped) skipped.Add(new Dictionary<string, object?> { ["package"] = s.Package, ["reason"] = s.Reason });
        var mode = Mode;
        return new Dictionary<string, object?>
        {
            ["mode"] = UpdateSettings.Word(mode),
            ["modeEffect"] = UpdateSettings.Effect(mode),
            ["majorVersions"] = SafeMajor() ? "Automatic" : "Notify",
            ["promptBeforeJoin"] = PromptOn,
            ["checked"] = r.HasRun ? r.ComputedUtc.ToString("o", CultureInfo.InvariantCulture) : null,
            ["header"] = HeaderText(),
            ["count"] = r.Count,
            ["badge"] = new Dictionary<string, object?> { ["count"] = BadgeCount, ["text"] = BadgeText },
            ["managed"] = r.Managed,
            ["items"] = items,
            ["skipped"] = skipped,
            ["version"] = Version,
            ["run"] = DescribeRun(),
        };
    }

    /// <summary>Core unload: forget this generation's state and probes (nothing is persisted here but the pin file).</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _report = UpdateReport.Empty;
            _signature = "";
            _computing = false;
            _again = null;
            _version++;
        }

        lock (RunGate)
        {
            _runState = "idle";
            _prepared = new List<PreparedUpdate>();
            _runToken++;
        }

        EntriesProbe = null;
        LocalKeyProbe = null;
        GameRootProbe = null;
        BepInExRootProbe = null;
        DependencyInstalledProbe = null;
        ConnectedProbe = null;
        CandidateRangeProbe = null;
        ServerCatalog = null;
        lock (Gate)
        {
            _connectedCatalog = new List<ServerRequirement>();
            _connectedMatching = null;
        }
        OpenConfirm = null;
        ShowResult = null;
        Log = null;
        Pipeline = new UpdatePipeline();
        PostMain = a => a();
        RunOff = work => Task.Run(work);
        ModeProbe = () => UpdateSettings.Parse(UpdateSettings.DefaultMode);
        PromptProbe = () => UpdateSettings.DefaultPrompt;
        MajorProbe = () => UpdateSettings.MajorAutomatic(UpdateSettings.DefaultMajor);
        NsfwProbe = () => false;
        ThunderstoreOffProbe = () => false;
    }

    private static bool SafeMajor()
    {
        try { return MajorProbe(); }
        catch { return false; }
    }

    private static string? Safe(Func<string?>? probe)
    {
        try { return probe?.Invoke(); }
        catch { return null; }
    }
}
