using System;
using System.Collections.Generic;
using Progenitor.Install;

namespace Progenitor.Networking;

/// <summary>JOINFLOW4: what the join screens know about a mod's kept versions (plain values).</summary>
internal sealed class JoinKeptInfo
{
    /// <summary>A kept version the server's check accepts (empty when none).</summary>
    public string PickVersion { get; set; } = "";

    /// <summary><c>now</c>, <c>restart</c> or <c>refused</c> for <see cref="PickVersion"/>.</summary>
    public string PickRoute { get; set; } = "";

    /// <summary>Why the pick cannot be swapped in (refused), or why it waits for a restart.</summary>
    public string PickReason { get; set; } = "";

    /// <summary>The active copy can be kept when something replaces it (not another tool's, not loaded from plugins).</summary>
    public bool CanKeepActive { get; set; }

    /// <summary>Why the active copy cannot be kept, or empty.</summary>
    public string KeepReason { get; set; } = "";

    /// <summary>Keeping the active copy unloads it now (the reloader can), so the server's version can load this session.</summary>
    public bool KeepNow { get; set; }

    /// <summary>Every kept version, newest first.</summary>
    public IReadOnlyList<string> Kept { get; set; } = Array.Empty<string>();

    /// <summary>The player's own version (the one that comes back after leaving), or empty.</summary>
    public string Preferred { get; set; } = "";

    /// <summary>The active copy was placed for a server (its key), or empty.</summary>
    public string ActiveForServer { get; set; } = "";

    public bool HasPick => PickVersion.Length > 0;

    public bool PickUsable => HasPick && PickRoute is "now" or "restart";
}

/// <summary>
/// JOINFLOW4: the install paths that replace a mod's active copy call these first, so the copy is kept (parked) rather
/// than destroyed: stage 2's "Use version X" (Thunderstore). (The server-copy hooks went with server distribution,
/// 2026-10-05.) Without a store (no client Core, tests) every hook answers "go ahead" and the stage 2 behavior stands.
/// </summary>
internal static class ModVersionHooks
{
    /// <summary>
    /// A copy of <paramref name="modId"/> is installed for the next start and the player's active copy is marked
    /// to leave then (the restart route took place). The Result page offers Restart and rejoin only then. Null without
    /// a store (tests, no client Core): the caller keeps its own answer.
    /// </summary>
    internal static bool? WaitsForRestart(string modId)
    {
        var svc = ModVersionJoin.Service;
        if (svc == null) return null;
        try
        {
            svc.ActiveCopy(modId, out var refusal, out var others);
            return refusal is { Code: ModVersionCode.Queued } && others.Exists(o => o.Folder == "pending");
        }
        catch { return false; }
    }

    /// <summary>"Use version X": keep the newer copy before the Thunderstore version is planned. (ok, sentence, moved now).</summary>
    internal static (bool Ok, string Note, bool Moved) KeepForThunderstore(JoinInstallRequest request)
    {
        var svc = ModVersionJoin.Service;
        if (svc == null || request == null || request.ModId.Length == 0) return (true, "", false);
        var (key, name) = Server();
        var r = svc.KeepActive(request.ModId, new ActiveVersion
        {
            Version = request.Version,
            ForServer = key.Length > 0 ? key : "server",
            ForServerName = name,
            Source = ModVersionSource.Thunderstore,
        });
        if (r.Ok) return (true, r.Message, r.Route == "now");
        // Nothing active to keep: the install is a plain install.
        if (r.Code == ModVersionCode.NotFound) return (true, "", false);
        return (false, r.Message, false);
    }

    private static (string, string) Server()
    {
        try { return ModVersionJoin.ServerProbe(); }
        catch { return ("", ""); }
    }
}

/// <summary>
/// JOINFLOW4: keeping both versions when the player is ahead of a server. The version store
/// (<see cref="ModVersionService"/>) lives in Install; this is where the join uses it:
/// <list type="bullet">
/// <item><see cref="BeforeReply"/>: when the server's handshake request arrives, every kept mod whose active copy
/// fails the server's check and whose kept copy passes is swapped in (when the reloader can do it now) before the
/// answer goes out; the answer waits (at most <see cref="SwapWaitSeconds"/>) until the swapped plugins declare the
/// kept version, so the join passes without a download.</item>
/// <item><see cref="OnConnectionEnded"/> and <see cref="Tick"/>: after leaving (no world, the join screens closed, no
/// install or reconnect running), the player's preferred version comes back — now, or queued for the next start.</item>
/// <item><see cref="KeptFor"/>: what the join screens show for a row (Use kept X, the kept-for-other-servers line).</item>
/// </list>
/// </summary>
internal static class ModVersionJoin
{
    /// <summary>The answer waits this long for swapped plugins to load (the server allows about 20 s).</summary>
    internal const double SwapWaitSeconds = 8;

    internal const double SwapPollSeconds = 0.1;

    private static readonly object Gate = new();
    private static int _connectionGen;
    private static bool _restoreDue;
    private static double _nextRestoreCheck;
    private static double _nextOrphanCheck;
    private static readonly List<string> LastJoin = new();
    private static readonly List<string> LastRestore = new();
    private static readonly List<string> LastReconcile = new();

    private static ModVersionService? _service;

#if PROGENITOR_TESTS
    /// <summary>Tests: a store for the calling flow only, so parallel join tests never see another test's store.</summary>
    internal static readonly System.Threading.AsyncLocal<ModVersionService?> TestService = new();

    /// <summary>The store, set by the live side (client only); null: keeping versions is off and every hook is a no-op.</summary>
    internal static ModVersionService? Service
    {
        get => TestService.Value ?? _service;
        set => _service = value;
    }
#else
    /// <summary>The store, set by the live side (client only); null: keeping versions is off and every hook is a no-op.</summary>
    internal static ModVersionService? Service
    {
        get => _service;
        set => _service = value;
    }
#endif

    /// <summary>This client's handshake answer now (live: <c>Handshake.CopyProvided</c>).</summary>
    internal static Func<IReadOnlyList<Handshake.ModRecord>> Provided { get; set; } = () => Handshake.CopyProvided();

    /// <summary>Run on the main thread on a later frame (live: <c>MainThread.Post</c>).</summary>
    internal static Action<Action> Post { get; set; } = a => a();

    internal static Action<double, Action> PostDelayed { get; set; } = (_, a) => a();

    internal static Func<double> Clock { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>The server being joined: a key (<c>address:port</c>) and a name.</summary>
    internal static Func<(string Key, string Name)> ServerProbe { get; set; } = () => ("", "");

    /// <summary>Safe to restore: no client world, the join screens closed, no install, reconnect or rejoin under way.</summary>
    internal static Func<bool> IdleForRestore { get; set; } = () => false;

    /// <summary>
    /// A join is between its connect and the handshake answer (outside an install window): the bridge's and the menus'
    /// swaps are refused then (never mid-join); the handshake's own swap is the one that runs.
    /// </summary>
    internal static Func<bool> JoinInProgress { get; set; } = () => false;

    /// <summary>A mod the per-server mod pause holds (paused or resuming): the pause, not the store, answers for it.</summary>
    internal static Func<string, bool> PausedForJoin { get; set; } = _ => false;

    private static bool MidJoin()
    {
        try { return JoinInProgress(); }
        catch { return true; }
    }

    internal static Action<string>? Info { get; set; }
    internal static Action<string>? Warn { get; set; }

    internal static void SetReconcile(IReadOnlyList<string> lines)
    {
        lock (Gate)
        {
            LastReconcile.Clear();
            LastReconcile.AddRange(lines);
        }
    }

    // ---- the handshake --------------------------------------------------------------------------

    /// <summary>
    /// The handshake reply path (before the per-server mod pause). Calls <paramref name="reply"/> at once when nothing
    /// is kept for the asked mods; otherwise swaps the kept versions in on the next frame and replies once they are
    /// declared (or the wait ends). A join that ended meanwhile is not answered.
    /// </summary>
    internal static void BeforeReply(IReadOnlyList<Handshake.ModRecord> asked, Action reply)
    {
        if (reply == null) return;
        var svc = Service;
        if (svc == null || asked == null || asked.Count == 0)
        {
            reply();
            return;
        }

        // 2026-10-05: the start-time pass that deletes kept copies a server sent waits while a swap is being finished;
        // it runs here once that settled.
        try
        {
            foreach (var l in svc.ForgetServerCopiesIfDue()) Info?.Invoke("JoinFlow: versions: " + l);
        }
        catch (Exception e) { Warn?.Invoke("JoinFlow: versions: forgetting the copies a server sent threw: " + e.Message); }

        List<JoinSwapPlan> plans;
        try { plans = svc.PlanForJoin(asked, Provided(), PausedForJoin); }
        catch (Exception e)
        {
            Warn?.Invoke("JoinFlow: versions: planning the join's swaps threw: " + e.Message + "; answering as is");
            reply();
            return;
        }

        var now = new List<JoinSwapPlan>();
        lock (Gate)
        {
            LastJoin.Clear();
            foreach (var p in plans)
            {
                LastJoin.Add(p.Id + " " + p.Version + " " + p.Decision.RouteName + (p.Decision.Reason.Length > 0 ? " (" + p.Decision.Reason + ")" : ""));
                if (p.Decision.Route == ModVersionRoute.Now) now.Add(p);
            }
        }

        foreach (var p in plans)
            if (p.Decision.Route != ModVersionRoute.Now)
                Info?.Invoke("JoinFlow: versions: the server needs the kept " + p.Id + " " + p.Version + "; not swapped at the handshake ("
                    + p.Decision.RouteName + ": " + p.Decision.Reason + ")");
        if (now.Count == 0)
        {
            reply();
            return;
        }

        int gen;
        lock (Gate) gen = _connectionGen;
        Post(() => SwapThenReply(svc, now, gen, reply));
    }

    private static void SwapThenReply(ModVersionService svc, List<JoinSwapPlan> plans, int gen, Action reply)
    {
        double deadline = Clock() + SwapWaitSeconds;
        SwapNext(svc, plans, 0, new List<(string Id, string Version)>(), gen, deadline, reply);
    }

    /// <summary>
    /// One swap at a time: a swap under another file name waits for its displaced plugin to unload before the kept copy
    /// is written (and the store takes one operation at a time), then the next. Once all are placed, the reply waits for
    /// them to be declared.
    /// </summary>
    private static void SwapNext(ModVersionService svc, List<JoinSwapPlan> plans, int i, List<(string Id, string Version)> waitFor,
        int gen, double deadline, Action reply)
    {
        if (Stale(gen))
        {
            Info?.Invoke("JoinFlow: versions: the join ended during its swaps; no answer (a swap already started still completes)");
            return;
        }

        if (i >= plans.Count)
        {
            if (waitFor.Count == 0)
            {
                SafeReply(reply);
                return;
            }

            Info?.Invoke("JoinFlow: versions: swapped in " + Describe(waitFor) + " for this server; answering once they load");
            WaitThen(waitFor, deadline, gen, reply);
            return;
        }

        var p = plans[i];
        var (key, name) = SafeServer();
        ModVersionResult r;
        try { r = svc.Swap(p.Id, p.Version, key.Length > 0 ? key : "server", name, nowOnly: true); }
        catch (Exception e) { r = ModVersionResult.Refuse(ModVersionCode.Failed, e.Message); }
        Remember(LastJoin, p.Id + " " + p.Version + " -> " + (r.Ok ? (r.AwaitingPlace ? "swapping" : "swapped") : ModVersionResult.CodeName(r.Code) + " (" + r.Message + ")"));
        if (!r.Ok)
        {
            Warn?.Invoke("JoinFlow: versions: swapping in the kept " + p.Id + " " + p.Version + " for the join failed: " + r.Message);
            SwapNext(svc, plans, i + 1, waitFor, gen, deadline, reply);
            return;
        }

        waitFor.Add((p.Id, p.Version));
        if (!r.AwaitingPlace)
        {
            SwapNext(svc, plans, i + 1, waitFor, gen, deadline, reply);
            return;
        }

        WaitPlaced(svc, Clock() + PlaceForceSeconds, Clock() + PlaceGiveUpSeconds, (ModVersionResult placed) =>
        {
            // Undone: nothing to wait for (the reply then carries the copy that came back).
            if (!Placed(placed)) waitFor.RemoveAll(w => string.Equals(w.Id, p.Id, StringComparison.OrdinalIgnoreCase));
            SwapNext(svc, plans, i + 1, waitFor, gen, deadline, reply);
        });
    }

    /// <summary>
    /// When the reloader still holds the displaced plugin this long after its file went, the swap is given up and the
    /// displaced copy goes back (nothing is ever written beside a plugin the reloader reports loaded).
    /// </summary>
    internal const double PlaceGiveUpSeconds = 15;

    /// <summary>
    /// How long a swap under another file name waits for its displaced plugin to drop out of this client's answer before
    /// the kept copy is written anyway: by then the reloader has scanned (once a second) and unloaded the removed file.
    /// </summary>
    internal const double PlaceForceSeconds = 3;

    /// <summary>Poll the store until the waiting swap is placed (or refused, or given up), then <paramref name="then"/>.</summary>
    internal static void WaitPlaced(ModVersionService svc, double forceAt, double giveUpAt, Action then) =>
        WaitPlaced(svc, forceAt, giveUpAt, (Action<ModVersionResult>)(_ => then()));

    /// <summary>The same, handing <paramref name="then"/> the placement's answer.</summary>
    internal static void WaitPlaced(ModVersionService svc, double forceAt, double giveUpAt, Action<ModVersionResult> then)
    {
        ModVersionResult? done;
        try { done = svc.TryCompletePlace(force: Clock() >= forceAt); }
        catch (Exception e) { done = ModVersionResult.Refuse(ModVersionCode.Failed, e.Message); }
        if (done == null && Clock() >= giveUpAt)
        {
            string r = svc.AbortWaiting("the reloader still holds the displaced plugin after " + PlaceGiveUpSeconds + " s");
            done = ModVersionResult.Refuse(ModVersionCode.Busy, "The previous copy did not unload; the swap was undone (" + r + ").");
        }

        if (done == null)
        {
            PostDelayed(SwapPollSeconds, () => WaitPlaced(svc, forceAt, giveUpAt, then));
            return;
        }

        if (!done.Ok && done.Code != ModVersionCode.NothingToDo)
            Warn?.Invoke("JoinFlow: versions: placing a kept copy failed: " + done.Message);
        then(done);
    }

    /// <summary>A placement answer that means the kept copy is in place (placed now, or already by the frame's pump).</summary>
    internal static bool Placed(ModVersionResult r) => r.Ok || r.Code == ModVersionCode.NothingToDo;

    /// <summary>Call <paramref name="then"/> once every (id, version) is declared, or the deadline passed; never after the join ended.</summary>
    internal static void WaitThen(List<(string Id, string Version)> waitFor, double deadline, int gen, Action then)
    {
        if (Stale(gen))
        {
            Info?.Invoke("JoinFlow: versions: the join ended while waiting for " + Describe(waitFor) + "; no answer");
            return;
        }

        if (AllDeclared(waitFor))
        {
            SafeReply(then);
            return;
        }

        if (Clock() >= deadline)
        {
            Warn?.Invoke("JoinFlow: versions: " + Describe(waitFor) + " did not load within " + SwapWaitSeconds + " s; answering anyway");
            SafeReply(then);
            return;
        }

        PostDelayed(SwapPollSeconds, () => WaitThen(waitFor, deadline, gen, then));
    }

    /// <summary>Every (id, version) is in this client's answer at that version.</summary>
    internal static bool AllDeclared(IReadOnlyList<(string Id, string Version)> want)
    {
        IReadOnlyList<Handshake.ModRecord> rows;
        try { rows = Provided(); }
        catch { return false; }
        foreach (var (id, version) in want)
        {
            bool ok = false;
            foreach (var r in rows)
                if (string.Equals(r.Guid, id, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase))
                    ok = true;
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>The current connection's generation, for <see cref="WaitThen"/> callers.</summary>
    internal static int ConnectionGeneration
    {
        get { lock (Gate) return _connectionGen; }
    }

    private static bool Stale(int gen)
    {
        lock (Gate) return gen != _connectionGen;
    }

    // ---- leaving --------------------------------------------------------------------------------

    /// <summary>The client left a server (kick, refusal, walk to the menu): restore the player's versions once idle.</summary>
    internal static void OnConnectionEnded()
    {
        lock (Gate)
        {
            _connectionGen++;
            _restoreDue = true;
        }
    }

    /// <summary>
    /// Once a frame (the join screens' tick): place a kept copy whose displaced plugin has unloaded (any caller's swap:
    /// the menus', the bridge's, Use kept), then, after a leave, restore at most once a second and only when idle. Not
    /// at start: a copy the start promoted for a server stays until a connection ends (review finding 5).
    /// </summary>
    internal static void Tick()
    {
        var svc = Service;
        if (svc == null) return;
        if (svc.Engine.AwaitingMod.Length > 0)
        {
            try
            {
                var waited = DateTime.UtcNow - svc.Engine.AwaitingSince;
                bool force = waited > TimeSpan.FromSeconds(PlaceForceSeconds + 2);
                var placed = svc.TryCompletePlace(force);
                if (placed == null && waited > TimeSpan.FromSeconds(PlaceGiveUpSeconds + 5))
                    svc.AbortWaiting("the reloader still holds the displaced plugin");
                if (placed != null && !placed.Ok && placed.Code != ModVersionCode.NothingToDo)
                    Warn?.Invoke("JoinFlow: versions: placing a kept copy failed: " + placed.Message);
            }
            catch (Exception e) { Warn?.Invoke("JoinFlow: versions: placing a kept copy threw: " + e.Message); }
            return;
        }

        double now = Clock();
        // AUTOUPDATE2: a queued update whose pending copy a Cancel install removed must not remove the copy in use at the
        // next start. Checked every two seconds (one small manifest read).
        bool orphanCheck;
        lock (Gate)
        {
            orphanCheck = now >= _nextOrphanCheck;
            if (orphanCheck) _nextOrphanCheck = now + 2.0;
        }

        if (orphanCheck)
        {
            try
            {
                svc.Engine.UnqueueOrphans(out var dropped);
                // Review H2: a dropped queue's start check must not judge a copy that never loads.
                foreach (var id in dropped) Progenitor.Install.ClientAutoUpdate.ForgetStartCheck(id, "its queued copy was removed");
            }
            catch (Exception e) { Warn?.Invoke("JoinFlow: versions: queued-swap check threw: " + e.Message); }
        }

        lock (Gate)
        {
            if (!_restoreDue || now < _nextRestoreCheck) return;
            _nextRestoreCheck = now + 1.0;
        }

        bool idle;
        try { idle = IdleForRestore(); }
        catch { idle = false; }
        if (!idle) return;
        RestoreNow(svc, "leave", out bool more);
        // One swap at a time: a swap that waits for its unload leaves the rest for the next pass.
        lock (Gate) _restoreDue = more;
    }

    /// <summary>Restore every mod placed for a server (the bridge's <c>restore_all</c> too).</summary>
    internal static List<string> RestoreNow(ModVersionService svc, string why) => RestoreNow(svc, why, out _);

    internal static List<string> RestoreNow(ModVersionService svc, string why, out bool more)
    {
        List<string> lines;
        more = false;
        try { lines = svc.RestoreAfterLeave(out more); }
        catch (Exception e) { lines = new List<string> { "threw: " + e.Message }; }
        lock (Gate)
        {
            if (!(more && lines.Count == 0))
            {
                LastRestore.Clear();
                LastRestore.Add("at " + why);
                LastRestore.AddRange(lines);
            }
        }

        foreach (var l in lines) Info?.Invoke("JoinFlow: versions: restore (" + why + "): " + l);
        return lines;
    }

    // ---- the join screens -----------------------------------------------------------------------

    /// <summary>What the join screens show for <paramref name="id"/>, or null when nothing is kept or known.</summary>
    internal static JoinKeptInfo? KeptFor(string id, IReadOnlyList<Handshake.ModRecord> asked, IReadOnlyList<Handshake.ModRecord> provided)
    {
        var svc = Service;
        if (svc == null || string.IsNullOrEmpty(id)) return null;
        try
        {
            var entry = svc.Manifest().Find(id);
            var info = new JoinKeptInfo();
            var active = svc.ActiveCopy(id, out var refusal, out var others);
            if (refusal is { } r)
            {
                info.CanKeepActive = false;
                info.KeepReason = r.Reason;
            }
            else if (active == null)
            {
                info.CanKeepActive = false;
                info.KeepReason = "no copy of it is in progenitor-reload";
            }
            else
            {
                var reload = others.FindAll(o => o.Folder == "reload");
                var d = ModVersionRoutes.Decide(svc.Facts(id, reload, forceRestart: false, midJoin: false));
                info.CanKeepActive = d.Route != ModVersionRoute.Refused;
                info.KeepNow = d.Route == ModVersionRoute.Now;
                // Always the concrete cause: why it is refused, or why it waits for a restart (live 2026-10-04).
                info.KeepReason = d.Reason;
            }

            if (entry == null) return info;
            var kept = new List<string>();
            foreach (var p in ModVersionSelect.Candidates(entry)) kept.Add(p.Version);
            kept.Sort((a, b) => InstalledState.CompareVersions(b, a));
            info.Kept = kept;
            info.Preferred = ModVersionSelect.PreferredOf(entry);
            info.ActiveForServer = entry.Active?.ForServer ?? "";
            var pick = ModVersionSelect.PickFor(id, asked ?? Array.Empty<Handshake.ModRecord>(), provided ?? Array.Empty<Handshake.ModRecord>(), entry,
                ModVersionSelect.CurrentRanges());
            if (pick != null)
            {
                info.PickVersion = pick.Version;
                ModVersionDecision dec = refusal ?? ModVersionRoutes.Decide(svc.Facts(id, others, forceRestart: false, midJoin: false));
                if (dec.Route != ModVersionRoute.Refused)
                {
                    string? problem = svc.KeptCopyProblem(id, pick);
                    if (problem != null) dec = new ModVersionDecision(ModVersionRoute.Refused, problem, ModVersionCode.HashMismatch);
                }

                info.PickRoute = dec.RouteName;
                info.PickReason = dec.Reason;
            }

            return info;
        }
        catch (Exception e)
        {
            Warn?.Invoke("JoinFlow: versions: reading the kept versions of " + id + " threw: " + e.Message);
            return null;
        }
    }

    // ---- the Mods workspace and Settings ---------------------------------------------------------------

    /// <summary>
    /// The mod page's line: <c>Other versions kept: 1.9.0 (yours) · 1.8.2 (server's)</c>, and the version its
    /// <b>Restore</b> button swaps in (the player's own, when it is kept and not active), else empty.
    /// </summary>
    internal static (string Line, string Restore) PageLine(string pluginId)
    {
        var svc = Service;
        if (svc == null || string.IsNullOrEmpty(pluginId)) return ("", "");
        try
        {
            var e = svc.Manifest().Find(pluginId);
            if (e == null || e.Parked.Count == 0) return ("", "");
            string preferred = ModVersionSelect.PreferredOf(e);
            var list = new List<ParkedVersion>(e.Parked);
            list.Sort((a, b) => InstalledState.CompareVersions(b.Version, a.Version));
            var parts = new List<string>(list.Count);
            foreach (var p in list)
                parts.Add(p.Version + (string.Equals(p.Version, preferred, StringComparison.OrdinalIgnoreCase) || p.UserInstalled ? " (yours)"
                    : p.Source == ModVersionSource.Server ? " (server's)" : ""));
            string line = "Other versions kept: " + string.Join(" · ", parts);
            if (e.Pending != null) line += " · " + e.Pending.Version + " loads at the next start";
            string restore = e.Pending == null && preferred.Length > 0 && e.FindParked(preferred) != null ? preferred : "";
            return (line, restore);
        }
        catch { return ("", ""); }
    }

    /// <summary>The mod page's and Settings' Restore: swap <paramref name="version"/> in as the player's choice. Returns the sentence.</summary>
    internal static string RestoreFromMenu(string pluginId, string version)
    {
        var svc = Service;
        if (svc == null) return "Keeping versions is not available here.";
        var r = svc.Swap(pluginId, version, "", "", midJoin: MidJoin());
        Info?.Invoke("JoinFlow: versions: Restore " + pluginId + " " + version + " from the menu: " + (r.Ok ? r.Route : ModVersionResult.CodeName(r.Code)) + " (" + r.Message + ")");
        return r.Ok ? r.Message : "Can't restore " + version + ": " + r.Message;
    }

    /// <summary>Every mod with kept versions, for the Settings panel's group: (id, line, restore version).</summary>
    internal static List<(string Id, string Line, string Restore)> AllPageLines()
    {
        var list = new List<(string, string, string)>();
        var svc = Service;
        if (svc == null) return list;
        try
        {
            foreach (var e in svc.Manifest().Mods)
            {
                var (line, restore) = PageLine(e.Id);
                if (line.Length > 0) list.Add((e.Id, line, restore));
            }
        }
        catch { /* the group is a convenience */ }

        return list;
    }

    // ---- the dev bridge (join_ui versions actions) -------------------------------------------------

    /// <summary>The store as the bridge reads it: every mod, its copies on disk, its kept copies (hash-checked), the journal.</summary>
    internal static Dictionary<string, object?> BridgeDescribe()
    {
        var svc = Service;
        var d = new Dictionary<string, object?> { ["ok"] = true, ["available"] = svc != null };
        if (svc == null) return d;
        var engine = svc.Engine;
        var paths = engine.Env.Paths;
        var m = engine.Read();
        d["root"] = paths.Root;
        d["journal"] = new Dictionary<string, object?> { ["pending"] = engine.JournalPending, ["text"] = engine.JournalText(), ["path"] = paths.Journal };
        d["limits"] = new Dictionary<string, object?> { ["maxPerMod"] = engine.Env.Limits.MaxPerMod, ["maxTotalBytes"] = engine.Env.Limits.MaxTotalBytes };
        d["totalBytes"] = m.TotalBytes;
        d["restoreDue"] = RestoreDue;
        d["lastJoin"] = LastJoinLines;
        d["lastRestore"] = LastRestoreLines;
        d["lastReconcile"] = LastReconcileLines;
        var mods = new List<object?>();
        foreach (var e in m.Mods)
        {
            var copies = new List<object?>();
            try
            {
                foreach (var c in svc.Runtime.CopiesOf(e.Id))
                    copies.Add(new Dictionary<string, object?>
                    {
                        ["folder"] = c.Folder, ["file"] = c.Name, ["version"] = c.Version, ["path"] = c.Path,
                        ["marked"] = System.IO.File.Exists(UninstallMarkers.MarkerFor(c.Path)),
                    });
            }
            catch (Exception ex) { copies.Add("threw: " + ex.Message); }

            var parked = new List<object?>();
            foreach (var p in ModVersionSelect.Candidates(e))
            {
                string file = paths.StoredFile(e.Id, p);
                string? sha = ModVersionFiles.ShaOf(file);
                parked.Add(new Dictionary<string, object?>
                {
                    ["version"] = p.Version,
                    ["file"] = p.File,
                    ["dir"] = p.Dir,
                    ["path"] = file,
                    ["sha256"] = p.Sha256,
                    ["size"] = p.Size,
                    ["intact"] = sha != null && string.Equals(sha, p.Sha256, StringComparison.OrdinalIgnoreCase),
                    ["source"] = ModVersionManifest.SourceName(p.Source),
                    ["sourceDetail"] = p.SourceDetail,
                    ["userInstalled"] = p.UserInstalled,
                    ["parked"] = ModVersionManifest.Stamp(p.ParkedUtc),
                    ["lastUsed"] = ModVersionManifest.Stamp(p.LastUsedUtc),
                    ["ledger"] = p.Ledger?.Path ?? "",
                    ["record"] = p.Record?.HostKey ?? "",
                });
            }

            string? loaded = null;
            try { loaded = svc.Runtime.LoadedVersion(e.Id); }
            catch { loaded = null; }
            mods.Add(new Dictionary<string, object?>
            {
                ["id"] = e.Id,
                ["preferred"] = ModVersionSelect.PreferredOf(e),
                ["preferredSet"] = e.Preferred,
                ["loaded"] = loaded,
                ["active"] = e.Active == null ? null : new Dictionary<string, object?>
                {
                    ["version"] = e.Active.Version, ["file"] = e.Active.File, ["forServer"] = e.Active.ForServer,
                    ["forServerName"] = e.Active.ForServerName, ["source"] = ModVersionManifest.SourceName(e.Active.Source),
                },
                ["pending"] = e.Pending == null ? null : new Dictionary<string, object?>
                {
                    ["version"] = e.Pending.Version, ["pendingFile"] = e.Pending.PendingFile, ["file"] = e.Pending.File,
                    ["marked"] = e.Pending.Marked, ["forServer"] = e.Pending.ForServer, ["queued"] = ModVersionManifest.Stamp(e.Pending.QueuedUtc),
                },
                ["restoreTarget"] = ModVersionSelect.RestoreTarget(e, loaded ?? e.Active?.Version)?.Version ?? "",
                ["copies"] = copies,
                ["parked"] = parked,
            });
        }

        d["mods"] = mods;
        return d;
    }

    /// <summary>The bridge's actions; each answers the store's readback with <c>result</c>, <c>code</c> and <c>message</c>.</summary>
    internal static Dictionary<string, object?> BridgeRun(string action, string id, string version, bool restart, bool force, string forServer)
    {
        var svc = Service;
        if (svc == null) return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "the version store is not installed (client Core only)" };
        ModVersionResult? r = null;
        object? extra = null;
        switch (action)
        {
            case "park":
                r = svc.KeepActive(id, null, forceRestart: restart, midJoin: MidJoin());
                break;
            case "swap":
            {
                var (key, name) = SafeServer();
                string server = forServer == "current" ? (key.Length > 0 ? key : "server") : forServer;
                r = svc.Swap(id, version, server, server.Length > 0 ? name : "", forceRestart: restart, midJoin: MidJoin());
                break;
            }
            case "restore":
                r = svc.RestorePreferred(id, version.Length > 0 ? version : null, MidJoin());
                break;
            case "restore_all":
                extra = RestoreNow(svc, "bridge");
                break;
            case "forget":
                r = svc.Engine.Forget(id, version, force);
                break;
            case "prefer":
                r = svc.Engine.SetPreferred(id, version);
                break;
            case "reconcile":
            {
                var lines = svc.Engine.Reconcile();
                SetReconcile(lines);
                extra = lines;
                break;
            }
            case "evict":
                extra = svc.Engine.Evict();
                break;
            case "plan":
            {
                var plans = new List<object?>();
                foreach (var p in svc.PlanForJoin(Handshake.CopyServerAsked(), Provided(), PausedForJoin))
                    plans.Add(new Dictionary<string, object?> { ["id"] = p.Id, ["version"] = p.Version, ["route"] = p.Decision.RouteName, ["reason"] = p.Decision.Reason });
                extra = plans;
                break;
            }
            default:
                return new Dictionary<string, object?> { ["ok"] = false, ["error"] = "unknown versions action " + action };
        }

        var d = BridgeDescribe();
        d["action"] = action;
        if (r != null)
        {
            d["result"] = r.Ok ? "ok" : "refused";
            d["code"] = ModVersionResult.CodeName(r.Code);
            d["message"] = r.Message;
            d["route"] = r.Route;
        }
        else d["result"] = "ok";

        if (extra != null) d["lines"] = extra;
        return d;
    }

    // ---- readback ---------------------------------------------------------------------------------

    internal static IReadOnlyList<string> LastJoinLines
    {
        get { lock (Gate) return LastJoin.ToArray(); }
    }

    internal static IReadOnlyList<string> LastRestoreLines
    {
        get { lock (Gate) return LastRestore.ToArray(); }
    }

    internal static IReadOnlyList<string> LastReconcileLines
    {
        get { lock (Gate) return LastReconcile.ToArray(); }
    }

    internal static bool RestoreDue
    {
        get { lock (Gate) return _restoreDue; }
    }

#if PROGENITOR_TESTS
    internal static void ResetForTests()
    {
        TestService.Value = null;
        Provided = () => Handshake.CopyProvided();
        Post = a => a();
        PostDelayed = (_, a) => a();
        Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        ServerProbe = () => ("", "");
        IdleForRestore = () => false;
        JoinInProgress = () => false;
        PausedForJoin = _ => false;
        Info = null;
        Warn = null;
        lock (Gate)
        {
            _connectionGen = 0;
            _restoreDue = false;
            _nextRestoreCheck = 0;
            _nextOrphanCheck = 0;
            LastJoin.Clear();
            LastRestore.Clear();
            LastReconcile.Clear();
        }
    }
#endif

    private static void Remember(List<string> list, string line)
    {
        lock (Gate)
        {
            list.Add(line);
            if (list.Count > 50) list.RemoveAt(0);
        }
    }

    private static (string, string) SafeServer()
    {
        try { return ServerProbe(); }
        catch { return ("", ""); }
    }

    private static void SafeReply(Action reply)
    {
        try { reply(); }
        catch (Exception e) { Warn?.Invoke("JoinFlow: versions: the handshake answer threw: " + e.Message); }
    }

    private static string Describe(List<(string Id, string Version)> list)
    {
        var parts = new List<string>(list.Count);
        foreach (var (id, v) in list) parts.Add(id + " " + v);
        return string.Join(", ", parts);
    }
}
