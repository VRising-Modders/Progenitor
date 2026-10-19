using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;

namespace Progenitor.Networking;

/// <summary>JOINFLOW2: how "Restart and rejoin" brings the game back (built by the live side).</summary>
internal sealed class RelaunchPlan
{
    public RelaunchMode Mode { get; set; } = RelaunchMode.Manual;

    /// <summary>Why the relaunch is manual (no Steam, no PowerShell), or empty.</summary>
    public string Why { get; set; } = "";

    public string FileName { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Script { get; set; } = "";
}

/// <summary>JOINFLOW2: the live side of "Restart and rejoin" (tests supply a fake).</summary>
internal interface IJoinRelauncher
{
    /// <summary>The relauncher for this process (probes Steam and PowerShell; starts nothing).</summary>
    RelaunchPlan Plan();

    /// <summary>
    /// Start the relauncher, detached, without blocking the caller. <paramref name="done"/> runs once, on the main
    /// thread (live: a later frame; fakes may call it at once), with whether it started and, if not, why.
    /// </summary>
    void Start(RelaunchPlan plan, Action<bool, string> done);

    /// <summary>Quit the game the safe way (<c>Application.Quit</c>) after <paramref name="delaySeconds"/>.</summary>
    void Quit(double delaySeconds);
}

/// <summary>
/// JOINFLOW2: Thunderstore installs from the join screens, Join now, and restart-and-rejoin. The batch and the
/// reconnect belong to the client, not to the screens: closing the screens never stops them.
/// </summary>
internal static partial class JoinFlowClient
{
    private static JoinInstallBatch? _batch;
    private static JoinTarget? _batchTarget;
    private static JoinMode _batchMode;
    private static bool _batchFinishedHandled;
    private static int _batchDirty;
    private static double _lastBatchRefresh;
    private static IReadOnlyDictionary<string, PackageVersions>? _tsVersions;
    private static string _tsVersionsKey = "";
    private static int _versionsDirty;

    /// <summary>Mod compatibility C4: rebuild the open screens' model at the next tick (Progenitor's releases were read).</summary>
    internal static void MarkVersionsDirty() => Interlocked.Exchange(ref _versionsDirty, 1);
    private static JoinReconnect? _reconnect;

    /// <summary>
    /// LOCALREJOIN1: what this PC knows about the reconnect target and its own server (the configured folder, its port, whether
    /// it runs here). Null (tests, an older wiring): every reconnect uses the original address.
    /// </summary>
    internal static Func<JoinTarget, JoinLocalFacts?>? LocalRejoinFactsProbe { get; set; }
    private static double _reconnectCanceledAt = -1;
    private static RejoinRecord? _rejoinRecord;
    private static bool _rejoinChecked;
    private static Dictionary<string, object?>? _lastRelaunch;
    private static string _lastPickup = "";

    /// <summary>Builds the installer of one batch (live: the Install tab's catalog machinery). Null: not available.</summary>
    internal static Func<IReadOnlyList<JoinInstallRequest>, IJoinPackageInstaller?>? InstallerFactory { get; set; }

    /// <summary>The package index the version lists come from (live: Thunderstore plus the local index).</summary>
    internal static JoinPackageIndex? PackageIndex { get; set; }

    /// <summary>The joined server's address, port, transport, password use and hosting (live: Bootstrap's connect record).</summary>
    internal static Func<JoinTarget>? TargetProbe { get; set; }

    internal static IJoinRelauncher? Relauncher { get; set; }

    internal static IJoinConnectHost? ConnectHost { get; set; }

    /// <summary><c>BepInEx/config/progenitor-rejoin.json</c>.</summary>
    internal static string RejoinPath => Path.Combine(ModManifest.ConfigDirectory, RejoinStore.FileName);

    /// <summary>Whether this join's connection used a password (live: <c>ModManifest.ReadLiveConnectUsedPassword</c>).</summary>
    internal static Func<bool>? PasswordProbe { get; set; }

    private static bool _passwordUsed;

    // Called with Gate held at each point the join is seen (the request, the catalog, the refusal): the client
    // world still exists then, and a refusal ends it. Sticky for the join; never the password itself.
    private static void CapturePasswordLocked()
    {
        try { if (PasswordProbe?.Invoke() == true) _passwordUsed = true; }
        catch { /* unknown is "no" */ }
    }

    // ---- Thunderstore batch -------------------------------------------------------------------

    private static List<string> PackagesOf(IReadOnlyList<JoinInstallRequest> requests)
    {
        var list = new List<string>();
        foreach (var r in requests ?? Array.Empty<JoinInstallRequest>())
            // LOCALJOIN1: a local test package is not Thunderstore's: no trust is asked or recorded for it.
            if (r != null && r.Package.Length > 0 && !r.LocalTest) list.Add(r.PackageVersion);
        return list;
    }

    private static bool ThunderstoreTrustNeeded(IReadOnlyList<JoinInstallRequest> requests)
    {
        string host, spki;
        lock (Gate)
        {
            host = _host;
            spki = _spki;
        }

        var packages = PackagesOf(requests);
        if (packages.Count == 0) return false; // LOCALJOIN1: only local test packages, nothing from Thunderstore
        try { return JoinThunderstoreTrust.NeedsPrompt(ModManifest.TrustForJoin(), host, spki, packages); }
        catch { return true; }
    }

    private static JoinThunderstoreFacts ThunderstoreFactsFor(IReadOnlyList<JoinInstallRequest> requests)
    {
        lock (Gate)
        {
            TrustedOperatorsFile trust;
            try { trust = ModManifest.TrustForJoin(); }
            catch { trust = new TrustedOperatorsFile(); }
            return new JoinThunderstoreFacts
            {
                ServerName = JoinText.ServerLabel(ServerNameLocked(), _host),
                Host = _host,
                Fingerprint = _fp,
                Spki = _spki,
                FirstSeen = JoinTrustPolicy.FirstSeen(trust, _host, _spki, _fp),
                PreviousFingerprint = JoinTrustPolicy.PreviousKey(trust, _host, _spki),
                PreviousSeen = JoinTrustPolicy.PreviousSeen(trust, _host, _spki),
                Requests = requests ?? Array.Empty<JoinInstallRequest>(),
            };
        }
    }

    /// <summary>The server (address and key) is still the one the confirmation was shown for.</summary>
    private static bool ThunderstoreKeyUnchanged(JoinThunderstoreFacts shown)
    {
        lock (Gate)
            return string.Equals(shown?.Host ?? "", _host, StringComparison.OrdinalIgnoreCase)
                && string.Equals(shown?.Spki ?? "", _spki, StringComparison.Ordinal);
    }

    /// <summary>A row's state in the current batch, by required id or recommended key.</summary>
    internal static JoinInstallRowState? InstallStateOf(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        JoinInstallBatch? batch;
        lock (Gate)
        {
            batch = _batch;
            var row = batch?.Row(key!);
            if (row != null) return row;
            // A refusal from an earlier batch of this join still stands: the row is not offered again.
            return RefusedThisJoin.TryGetValue(key!, out var refused) ? refused : null;
        }
    }

    /// <summary>Rows refused by an earlier batch of this join (by key); cleared on the next join attempt.</summary>
    private static readonly Dictionary<string, JoinInstallRowState> RefusedThisJoin = new(StringComparer.OrdinalIgnoreCase);

    // Which join a batch belongs to: OnJoinAttempt advances it, so a batch still running from the last join
    // never carries its refusals into this one.
    private static int _joinGeneration;
    private static int _batchJoinGeneration;

    // Called with Gate held: keep the refusals of the batch about to be replaced (this join's only).
    private static void KeepRefusalsLocked()
    {
        if (_batchJoinGeneration != _joinGeneration) return;
        foreach (var r in _batch?.Rows ?? Array.Empty<JoinInstallRowState>())
            if (r.Phase == JoinInstallPhase.Refused) RefusedThisJoin[r.Request.Key] = r;
    }

    private static string StartBatch(IReadOnlyList<JoinInstallRequest> requests, bool acknowledge)
    {
        if (requests == null || requests.Count == 0) return JoinText.NothingToInstall;
        JoinInstallBatch? running;
        lock (Gate) running = _batch;
        if (running != null && !running.Done) return JoinText.BatchBusy;
        IJoinPackageInstaller? installer = null;
        try { installer = InstallerFactory?.Invoke(requests); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: the Thunderstore installer could not be built: " + e.Message); }
        if (installer == null) return "Installing from Thunderstore is not available here. Install it from Mods > Install, then join again.";

        if (acknowledge)
        {
            string host, spki, fp;
            lock (Gate)
            {
                host = _host;
                spki = _spki;
                fp = _fp;
            }

            ModManifest.AcknowledgeThunderstore(fp, spki, host, PackagesOf(requests));
        }

        var batch = new JoinInstallBatch(requests, installer);
        lock (Gate)
        {
            KeepRefusalsLocked();
            _batch = batch;
            _batchJoinGeneration = _joinGeneration;
            _batchTarget = CurrentTargetLocked();
            _batchMode = _session?.Mode ?? (_refused ? JoinMode.Refused : _window ? JoinMode.InstallWindow : JoinMode.InWorld);
            _batchFinishedHandled = false;
        }

        // JOINTURNON: an install or update of a mod the player turned off turns it on, so the new copy is not held off.
        var fixIds = new List<string>();
        foreach (var r in requests) if (r != null && r.ModId.Length > 0) fixIds.Add(r.ModId);
        TurnOnForFix(fixIds);
        batch.Changed += () => Interlocked.Exchange(ref _batchDirty, 1);
        bool allLocal = true;
        var localKeys = new List<string>();
        foreach (var r in requests)
        {
            if (r == null) continue;
            if (r.LocalTest) localKeys.Add(r.PackageVersion);
            else allLocal = false;
        }

        if (PackagesOf(requests).Count > 0)
            Plugin.Logger?.LogInfo("JoinFlow: installing from Thunderstore: " + string.Join(", ", PackagesOf(requests))
                + (acknowledge ? " (confirmed)" : " (confirmed earlier)"));
        if (localKeys.Count > 0)
            Plugin.Logger?.LogInfo("JoinFlow: installing from the " + Install.LocalTestSource.Words + ": " + string.Join(", ", localKeys) + ".");
        batch.Start();
        int n = batch.Rows.Count;
        return "Installing " + n.ToString(CultureInfo.InvariantCulture) + (n == 1 ? " mod" : " mods")
            + (allLocal ? " from the " + Install.LocalTestSource.Words + "…" : " from Thunderstore…");
    }

    // The address this process dialed for this join (Bootstrap's connect record at the join's attempt), or null.
    private static JoinTarget? _joinDialed;

    // Called with Gate held from OnJoinAttempt: capture what this join dialed (never a guess from the catalog).
    private static void CaptureDialedLocked()
    {
        JoinTarget? t;
        try { t = TargetProbe?.Invoke(); }
        catch { t = null; }
        _joinDialed = t != null && t.Address.Trim().Length > 0
            ? new JoinTarget { Address = t.Address, Port = t.Port, Protocol = t.Protocol, HostedByThisClient = t.HostedByThisClient }
            : null;
    }

    // Called with Gate held: the server this join went to.
    private static JoinTarget CurrentTargetLocked()
    {
        JoinTarget t;
        try { t = TargetProbe?.Invoke() ?? new JoinTarget(); }
        catch { t = new JoinTarget(); }
        // Live 2026-10-05: a fresh process whose connect record was empty fell back to the catalog's host and dialed
        // the server's public address (SteamIPv4) instead of the LAN address the player used, and timed out. Only an
        // address this process dialed is reconnected: Bootstrap's record now, else the one captured at this join's
        // attempt. With neither there is no address: no Join now, "Join the server again from the server list."
        if (t.Address.Length == 0 && _joinDialed != null)
        {
            t.Address = _joinDialed.Address;
            t.Port = _joinDialed.Port;
            t.Protocol = _joinDialed.Protocol;
            t.HostedByThisClient |= _joinDialed.HostedByThisClient;
        }

        if (t.ServerName.Length == 0) t.ServerName = ServerNameLocked() ?? "";
        if (t.Fingerprint.Length == 0) t.Fingerprint = _fp;
        CapturePasswordLocked();
        t.NeedsPassword |= _passwordUsed;
        return t;
    }

    private static void TickBatch(double now)
    {
        if (Interlocked.Exchange(ref _versionsDirty, 0) == 1) Refresh();
        JoinInstallBatch? batch;
        lock (Gate) batch = _batch;
        if (batch == null) return;
        bool done = batch.Done && batch.Started;
        if (Volatile.Read(ref _batchDirty) == 1 && (done || now - _lastBatchRefresh >= 0.5))
        {
            // Progress redraws at most twice a second: the panel is rebuilt on each.
            Interlocked.Exchange(ref _batchDirty, 0);
            _lastBatchRefresh = now;
            Refresh();
        }

        bool finish = false;
        lock (Gate)
        {
            if (done && !_batchFinishedHandled && ReferenceEquals(batch, _batch))
            {
                _batchFinishedHandled = true;
                finish = true;
            }
        }

        if (finish) FinishBatch(batch);
        TickWindowReAnswer(now);
    }

    /// <summary>
    /// 2026-10-05: a Thunderstore batch that finished inside an install window with everything loaded answers the
    /// handshake again, as Turn on and Use kept do, once the installed handshake mods are declared (or after
    /// <see cref="TurnOnWaitSeconds"/>). Without it the window only said "the join continues by itself" and lapsed.
    /// </summary>
    private sealed class WindowReAnswerWait
    {
        public List<string> Ids { get; init; } = new();
        public double Deadline { get; init; }
        public int Generation { get; init; }
        public string Names { get; init; } = "";

        /// <summary>A6: where the batch installed from, for the log (Thunderstore, the local test source).</summary>
        public string Source { get; init; } = "Thunderstore";
    }

    private static WindowReAnswerWait? _windowReAnswer;

    /// <summary>The ids an in-window batch is waiting to see declared before it re-answers (readback; empty when none).</summary>
    internal static IReadOnlyList<string> WindowReAnswerPending
    {
        get { lock (Gate) return _windowReAnswer == null ? Array.Empty<string>() : _windowReAnswer.Ids.ToArray(); }
    }

    // Called with Gate held from FinishBatch: arm the re-answer for a batch whose required rows all loaded in the window.
    private static void ArmWindowReAnswerLocked(JoinPageModel model, IReadOnlyList<JoinInstallRowState> rows)
    {
        var ids = new List<string>();
        var names = new List<string>();
        foreach (var row in rows)
        {
            if (row?.Request == null || row.Phase != JoinInstallPhase.Loaded) continue;
            names.Add(row.Request.Name.Length > 0 ? row.Request.Name : row.Request.Key);
            if (row.Request.ModId.Length > 0) InstalledThisJoin.Add(row.Request.ModId);
            // Only the server's handshake rows show up in this client's answer; a required client mod is judged from the
            // mod report that follows the answer.
            var req = model.FindRequired(row.Request.Key);
            if (req != null && req.Origin == JoinRowOrigin.ServerRequires && row.Request.ModId.Length > 0) ids.Add(row.Request.ModId);
        }

        _windowReAnswer = new WindowReAnswerWait
        {
            Ids = ids,
            Deadline = Clock() + TurnOnWaitSeconds,
            Generation = _joinGeneration,
            Names = string.Join(", ", names),
            Source = BatchSourceWords(rows),
        };
    }

    private static void TickWindowReAnswer(double now)
    {
        WindowReAnswerWait? due = null;
        lock (Gate)
        {
            var w = _windowReAnswer;
            if (w == null) return;
            if (w.Generation != _joinGeneration)
            {
                _windowReAnswer = null;
                return;
            }

            if (!AllIdsDeclared(w.Ids) && now < w.Deadline) return;
            _windowReAnswer = null;
            due = w;
        }

        bool sent;
        try { sent = ReAnswerHandshake(); }
        catch { sent = false; }
        Plugin.Logger?.LogInfo("JoinFlow: re-answered the handshake after the " + due.Source + " install of " + due.Names
            + " inside the install window; sent=" + (sent ? "true" : "false"));
        // A6 (live 2026-10-07): the page said "the join continues by itself" while nothing had reached the server. It
        // now says so only once the answer went out; otherwise it says the server was not told, with Join again.
        WindowAnswerOutcome(due.Names, sent);
    }

    /// <summary>A6: the window's page after its re-answer: the join continues (sent), or the truth and Join again.</summary>
    private static void WindowAnswerOutcome(string names, bool sent)
    {
        lock (Gate)
        {
            var s = _session;
            if (s == null || s.Closed || s.IsPreview || s.Mode != JoinMode.InstallWindow) return;
            if (sent)
            {
                s.Note(JoinText.WindowLoadedNote, good: true);
                return;
            }

            var target = CurrentTargetLocked();
            bool canRejoin = target.CanReconnect && !target.HostedByThisClient;
            s.ShowResult(new JoinResultOffer
            {
                Kind = canRejoin ? JoinAfterInstall.JoinNow : JoinAfterInstall.Nothing,
                CanRejoin = canRejoin,
                Hosted = target.HostedByThisClient,
                JoinAgain = true,
                Summary = JoinText.WindowNotTold(JoinText.ServerLabel(ServerNameLocked(), _host), names)
                    + (canRejoin ? "" : " Join the server again from the server list."),
            });
        }

        RaiseChanged();
    }

    /// <summary>Every id is in this client's handshake answer with a version (the reloader loaded it and it declared itself).</summary>
    private static bool AllIdsDeclared(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return true;
        IReadOnlyList<Handshake.ModRecord> rows;
        try { rows = ModVersionJoin.Provided(); }
        catch { return false; }
        foreach (string id in ids)
        {
            bool ok = false;
            foreach (var r in rows)
                if (string.Equals(r.Guid, id, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(r.Version)) { ok = true; break; }
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>A batch ended: the Result page (Join now, or Restart and rejoin) or a line on the mods page.</summary>
    private static void FinishBatch(JoinInstallBatch batch)
    {
        var rows = batch.Rows;
        JoinResultOffer offer;
        JoinFlowSession? reopen = null;
        bool inPlace;
        lock (Gate)
        {
            // Review (MEDIUM): decide from what this client declares now, not the snapshot taken when the catalog came.
            try
            {
                var live = ModManifestDeny.Provided();
                if (live.Count > 0) _provided = Copy(live);
            }
            catch { /* the snapshot stays */ }

            var model = JoinPageModel.Build(BuildFactsLocked());
            var kind = JoinAfterInstallPolicy.Decide(model, rows);
            // JOINTURNON: a mod the same press turned on for the next start needs the restart too.
            var turnedLater = new List<string>();
            foreach (var kv in TurnedOnThisJoin) if (kv.Value.Route == "restart") turnedLater.Add(kv.Value.Name);
            if (kind == JoinAfterInstall.JoinNow && turnedLater.Count > 0) kind = JoinAfterInstall.RestartNeeded;
            // INPLACE1 (standing rule 2026-10-07): still in this server's world, so nothing is left to reconnect to. The
            // fix finishes where the player is: the handshake is answered again on this connection (the mod report follows
            // it) and the server's re-check clears its warning. Only a refusal or a disconnect takes the reconnect path.
            inPlace = kind == JoinAfterInstall.JoinNow && _batchMode != JoinMode.InstallWindow && InWorldNow();
            offer = BuildOfferLocked(kind, rows, inPlace);
            if (turnedLater.Count > 0 && kind == JoinAfterInstall.RestartNeeded)
                offer.Summary = (offer.Summary + " " + JoinText.TurnsOnAtNextStart(string.Join(", ", turnedLater))).Trim();
            Plugin.Logger?.LogInfo("JoinFlow: " + BatchSourceWords(rows) + " batch done: " + KindName(kind)
                + (inPlace ? " (finished in place)" : "") + "; " + offer.Summary);
            // 2026-10-05: inside the install window, everything loaded: answer the handshake again once declared.
            if (_batchMode == JoinMode.InstallWindow && kind == JoinAfterInstall.JoinNow && InstallWindowOpen)
                ArmWindowReAnswerLocked(model, rows);
            // An in-place result is shown after its answer went out (ShowInPlace), so the words say whether it did.
            if (!inPlace && _session != null && !_session.Closed && !_session.IsPreview && _session.Mode != JoinMode.Rejoin)
            {
                _session.ShowResult(offer);
            }
            else if (offer.Kind is JoinAfterInstall.JoinNow or JoinAfterInstall.RestartNeeded && !InWorldNow())
            {
                // The player closed the screens while the batch ran: its result comes back on its own.
                _session = new JoinFlowSession(JoinMode.Refused, model, LiveEffects);
                _session.ShowResult(offer);
                reopen = _session;
            }
        }

        if (inPlace)
        {
            FinishInPlace(offer, "the install of " + JoinText.LoadedNames(rows));
            ShowInPlace(offer);
        }

        RaiseChanged();
        if (reopen != null) Plugin.Logger?.LogInfo("JoinFlow: the batch's result reopened the screens");
    }

    // Called with Gate held.
    private static JoinResultOffer BuildOfferLocked(JoinAfterInstall kind, IReadOnlyList<JoinInstallRowState> rows, bool inPlace = false)
    {
        var target = _batchTarget ?? CurrentTargetLocked();
        var offer = new JoinResultOffer
        {
            Kind = kind,
            Hosted = target.HostedByThisClient,
            CanRejoin = target.CanReconnect && !target.HostedByThisClient,
            Summary = JoinText.BatchSummary(rows),
        };

        if (inPlace) return InPlaceOfferLocked(JoinText.LoadedNames(rows), "installed", "installed and loaded");

        // In the world, mods that loaded need nothing more; inside an install window the join continues by itself.
        if (kind == JoinAfterInstall.JoinNow && _batchMode != JoinMode.Refused)
        {
            offer.Kind = JoinAfterInstall.Nothing;
            offer.Summary = (offer.Summary + " " + (_batchMode == JoinMode.InstallWindow ? JoinText.WindowTellingNote : JoinText.InWorldLoadedNote)).Trim();
            offer.Good = true;
        }

        if (kind == JoinAfterInstall.JoinNow && !offer.CanRejoin && offer.Kind == JoinAfterInstall.JoinNow)
        {
            // Nothing to reconnect to by itself: the player joins again from the menu.
            offer.Kind = JoinAfterInstall.Nothing;
            offer.Summary = (offer.Summary + " Join the server again from the server list.").Trim();
        }

        if (offer.Kind == JoinAfterInstall.RestartNeeded && offer.CanRejoin)
        {
            try { offer.ManualRelaunch = Relauncher?.Plan().Mode != RelaunchMode.Automatic; }
            catch { offer.ManualRelaunch = true; }
        }

        return offer;
    }

    /// <summary>
    /// INPLACE1: answer the handshake again on this connection (the mod report follows the answer), so the server's
    /// re-check sees the fix; when no answer can go out, the report alone when this server asks for one. The offer says
    /// honestly when nothing reached the server.
    /// </summary>
    private static void FinishInPlace(JoinResultOffer offer, string what)
    {
        // Review (MEDIUM): the server judges a re-answer from an already-ok player like a first answer and kicks on a
        // mismatch, so this client runs that same check first and answers again only when it passes. Otherwise only the
        // mod report goes (the server re-judges the client mod policy from it; nothing in it can kick for the handshake),
        // and the page says what still mismatches instead of "you stay".
        (bool Checkable, IReadOnlyList<string> Problems) check;
        try { check = ReAnswerPreflight(); }
        catch (Exception e) { check = (false, new[] { "the check threw: " + e.Message }); }
        if (!check.Checkable || check.Problems.Count > 0)
        {
            int sentReport = 0;
            if (ClientModReportClient.ServerAsks)
            {
                try { sentReport = ClientModReportClient.Send("fixed in place after " + what + " (no re-answer: the check does not pass yet)", force: true); }
                catch { sentReport = 0; }
            }

            offer.InPlaceSent = false;
            offer.InPlaceProblems = new List<string>(check.Problems);
            offer.Summary = JoinText.InPlaceNotYet(offer.ServerName, offer.InPlaceNames, offer.InPlaceDone, check.Problems, check.Checkable);
            Plugin.Logger?.LogInfo("JoinFlow: fixed in place after " + what + " on " + offer.ServerName + ", but did not answer again: "
                + (check.Checkable ? "the server's check would still fail: " + string.Join("; ", check.Problems) : string.Join("; ", check.Problems))
                + "; report packets=" + sentReport.ToString(CultureInfo.InvariantCulture));
            return;
        }

        bool answered;
        try { answered = ReAnswerHandshake(); }
        catch { answered = false; }
        int reported = 0;
        if (!answered && ClientModReportClient.ServerAsks)
        {
            try { reported = ClientModReportClient.Send("finished in place after " + what, force: true); }
            catch { reported = 0; }
        }

        bool sent = answered || reported > 0;
        offer.InPlaceSent = sent;
        if (!sent) offer.Summary = JoinText.InPlaceNotSent(offer.ServerName, offer.InPlaceNames, offer.InPlaceDone);
        Plugin.Logger?.LogInfo("JoinFlow: finished in place after " + what + " on " + offer.ServerName + "; re-answered="
            + (answered ? "true" : "false") + (answered ? "" : ", report packets=" + reported.ToString(CultureInfo.InvariantCulture))
            + "; no rejoin");
    }

    /// <summary>
    /// INPLACE1 review: the server's own check run on the answer this client would send now (live:
    /// <see cref="Handshake.PreflightReAnswer"/>): whether the request is known in full, and the problems it finds.
    /// </summary>
    internal static Func<(bool Checkable, IReadOnlyList<string> Problems)> ReAnswerPreflight { get; set; } =
#if PROGENITOR_TESTS
        () => (true, Array.Empty<string>());
#else
        LivePreflight;

    private static (bool Checkable, IReadOnlyList<string> Problems) LivePreflight()
    {
        var problems = Handshake.PreflightReAnswer(out bool checkable);
        return (checkable, problems);
    }
#endif

    /// <summary>
    /// INPLACE1: the Result offer for a fix that finished in this server's world (Gate held): <paramref name="done"/> for the
    /// sentence (<c>installed</c>, <c>turned on</c>), <paramref name="notSentDone"/> for the one used when nothing reached the
    /// server. The caller sends it with <see cref="FinishInPlace"/> once the fix is in this client's answer.
    /// </summary>
    private static JoinResultOffer InPlaceOfferLocked(string names, string done, string notSentDone)
    {
        string server = JoinText.ServerLabel(ServerNameLocked(), _host);
        return new JoinResultOffer
        {
            Kind = JoinAfterInstall.Nothing, InPlace = true, ServerName = server, InPlaceNames = names, InPlaceDone = notSentDone,
            Summary = JoinText.SentInPlace(server, names, done),
        };
    }

    /// <summary>
    /// INPLACE1: show an in-place result: the Result page (Done) while the join screens are open, else (the player closed
    /// them while it ran) a notice in the world with the same sentence, so the outcome is never only in the log.
    /// </summary>
    private static void ShowInPlace(JoinResultOffer offer)
    {
        bool shown = false;
        lock (Gate)
        {
            var s = _session;
            if (s != null && !s.Closed && !s.IsPreview && s.Mode != JoinMode.Rejoin)
            {
                s.ShowResult(offer);
                shown = true;
            }
        }

        if (shown) return;
        try { InPlaceNotice?.Invoke(JoinText.InPlaceTitle, offer.Summary); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: the in-place notice could not be shown: " + e.Message); }
        Plugin.Logger?.LogInfo("JoinFlow: the join screens were closed; the in-place result is a notice: " + offer.Summary);
    }

    /// <summary>INPLACE1: a notice in the world (live: the mod pause notice's popup). Null: none.</summary>
    internal static Action<string, string>? InPlaceNotice { get; set; }

    /// <summary>The source words a finished batch logs: Thunderstore, the local test source, or both.</summary>
    internal static string BatchSourceWords(IReadOnlyList<JoinInstallRowState> rows)
    {
        bool local = false, remote = false;
        foreach (var r in rows ?? Array.Empty<JoinInstallRowState>())
        {
            if (r == null) continue;
            if (r.Request.LocalTest) local = true;
            else remote = true;
        }

        if (local && remote) return "Thunderstore and " + Install.LocalTestSource.Words;
        return local ? Install.LocalTestSource.Words : "Thunderstore";
    }

    /// <summary>INPLACE1: the Result page's finish mode for the bridge.</summary>
    internal static string FinishModeName(JoinResultOffer o) =>
        o == null ? "none"
        : o.InPlace ? "in-place"
        : o.Kind == JoinAfterInstall.JoinNow ? "rejoin"
        : o.Kind == JoinAfterInstall.RestartNeeded ? "restart"
        : "none";

    internal static string KindName(JoinAfterInstall kind) => kind switch
    {
        JoinAfterInstall.Incomplete => "incomplete",
        JoinAfterInstall.JoinNow => "join-now",
        JoinAfterInstall.RestartNeeded => "restart-needed",
        _ => "nothing",
    };

    // ---- version lists -------------------------------------------------------------------------

    /// <summary>Read the version lists of the packages this join names, in the background, once per set.</summary>
    private static void EnsureVersions()
    {
        var index = PackageIndex;
        if (index == null) return;
        var wanted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rules = new Dictionary<string, Plugins.NetworkVersionRule>(StringComparer.OrdinalIgnoreCase);
        string key;
        lock (Gate)
        {
            var s = _session;
            if (s == null || s.Mode == JoinMode.Rejoin) return;
            foreach (var r in s.Model.Required)
                if (!string.IsNullOrEmpty(r.Package) && !r.Ready)
                {
                    wanted[r.Package!] = r.RequiredVersion;
                    rules[r.Package!] = r.VersionRule;
                }
            foreach (var r in s.Model.Recommended)
                if (r.Mod.Package.Length > 0 && !wanted.ContainsKey(r.Mod.Package)) wanted[r.Mod.Package] = r.Mod.Version;
            if (wanted.Count == 0) return;
            var parts = new List<string>();
            foreach (var kv in wanted) parts.Add(kv.Key + "@" + kv.Value);
            parts.Sort(StringComparer.OrdinalIgnoreCase);
            key = string.Join("|", parts);
            if (string.Equals(key, _tsVersionsKey, StringComparison.Ordinal)) return;
            _tsVersionsKey = key;
        }

        Task.Run(async () =>
        {
            try
            {
                var load = await index.LoadAsync(wanted, rules, CancellationToken.None).ConfigureAwait(false);
                lock (Gate)
                {
                    if (!string.Equals(_tsVersionsKey, key, StringComparison.Ordinal)) return;
                    // Nothing read at all (offline, no cache, no local index): the stage 1 answers stand.
                    if (load.Versions.Count == 0 && load.Error.Length > 0) return;
                    _tsVersions = load.Versions;
                }

                if (load.Error.Length > 0) Plugin.Logger?.LogInfo("JoinFlow: Thunderstore index: " + load.Error);
                Interlocked.Exchange(ref _versionsDirty, 1);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: reading the Thunderstore versions threw: " + e.Message); }
        });
    }

    // ---- Restart and rejoin --------------------------------------------------------------------

    private static string Relaunch(bool dryRun)
    {
        JoinFlowSession? s;
        JoinTarget target;
        List<RejoinRow> rows;
        lock (Gate)
        {
            s = _session;
            target = _batchTarget ?? CurrentTargetLocked();
            rows = RecordRowsLocked();
        }

        var offer = s?.Result;
        if (offer == null || offer.Kind != JoinAfterInstall.RestartNeeded) return "restart is not offered";
        if (!offer.CanRejoin) return "rejoin is not offered for this server";
        var relauncher = Relauncher;
        if (relauncher == null) return "restarting is not available here";
        // Once per process: a second press while the game is quitting would start a second relauncher.
        if (!dryRun && Interlocked.CompareExchange(ref _relaunching, 1, 0) != 0) return "already restarting";

        var record = RejoinRecord.From(target, rows, DateTime.UtcNow);
        record.DryRun = dryRun;
        try { RejoinStore.Write(RejoinPath, record); }
        catch (Exception e)
        {
            if (!dryRun) Interlocked.Exchange(ref _relaunching, 0);
            Plugin.Logger?.LogWarning("JoinFlow: the rejoin record could not be written: " + e.Message);
            return "the rejoin record could not be written: " + e.Message;
        }

        RelaunchPlan plan;
        try { plan = relauncher.Plan(); }
        catch (Exception e) { plan = new RelaunchPlan { Mode = RelaunchMode.Manual, Why = e.Message }; }
        lock (Gate)
        {
            _lastRelaunch = new Dictionary<string, object?>
            {
                ["dryRun"] = dryRun,
                ["mode"] = plan.Mode == RelaunchMode.Automatic ? "automatic" : "manual",
                ["why"] = plan.Why,
                ["fileName"] = plan.FileName,
                ["arguments"] = plan.Arguments,
                ["script"] = plan.Script,
                ["recordPath"] = RejoinPath,
                ["at"] = ModManifestJson.FormatIssued(DateTime.UtcNow),
            };
        }

        Plugin.Logger?.LogInfo("JoinFlow: restart and rejoin" + (dryRun ? " (dry run)" : "") + ": record written to " + RejoinPath
            + "; relaunch " + (plan.Mode == RelaunchMode.Automatic ? "automatic" : "manual" + (plan.Why.Length > 0 ? " (" + plan.Why + ")" : "")));
        if (dryRun) return "dry-run";

        if (plan.Mode != RelaunchMode.Automatic) return AfterRestartStart(relauncher, false, plan.Why);
        return BeginRelaunch(relauncher, plan, (started, error) => AfterRestartStart(relauncher, started, error));
    }

    /// <summary>
    /// Starts the relauncher without blocking this frame and runs <paramref name="after"/> once with its answer.
    /// Returns <paramref name="after"/>'s result when the answer came at once, else <c>quitting</c> (the game quits as
    /// soon as the relauncher answers, at once or, when it failed, the manual way).
    /// </summary>
    private static string BeginRelaunch(IJoinRelauncher relauncher, RelaunchPlan plan, Func<bool, string, string> after)
    {
        string? outcome = null;
        int once = 0;
        void Done(bool started, string error)
        {
            if (Interlocked.Exchange(ref once, 1) != 0) return;
            outcome = after(started, error ?? "");
        }

        try { relauncher.Start(plan, Done); }
        catch (Exception e) { Done(false, e.Message); }
        return outcome ?? "quitting";
    }

    private static string AfterRestartStart(IJoinRelauncher relauncher, bool started, string error)
    {
        if (!started)
        {
            // Still quit, but say so first: the record reconnects a manual start.
            lock (Gate)
            {
                if (_session != null)
                {
                    if (_session.Result != null) _session.Result.ManualRelaunch = true;
                    _session.Note(JoinText.RestartManual);
                }
            }

            Plugin.Logger?.LogWarning("JoinFlow: the relauncher did not start (" + error + "); quitting for a manual start");
            RaiseChanged();
            relauncher.Quit(6.0);
            return "quitting-manual";
        }

        // At once: the relauncher is waiting for this process (Unity quits at the end of this frame).
        relauncher.Quit(0);
        return "quitting";
    }

    private static int _relaunching;

    // Called with Gate held: what the record remembers for the after-relaunch summary.
    private static List<RejoinRow> RecordRowsLocked()
    {
        var list = new List<RejoinRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in _batch?.Rows ?? Array.Empty<JoinInstallRowState>())
        {
            string id = r.Request.ModId.Length > 0 ? r.Request.ModId : r.Request.Key;
            seen.Add(id);
            list.Add(new RejoinRow
            {
                Id = id,
                Name = r.Request.Name,
                Package = r.Request.Package,
                Version = r.Request.Version,
                Required = r.Request.Required,
                Result = JoinText.InstallPhaseName(r.Phase),
            });
        }

        try
        {
            var model = _session?.Model ?? JoinPageModel.Build(BuildFactsLocked());
            foreach (var r in model.Required)
            {
                if (seen.Contains(r.Id)) continue;
                seen.Add(r.Id);
                // JOINTURNON: a turned-off copy switched on for the next start is remembered at its own version (what the
                // start will run), result "turned-on"; one not switched on yet reads "turned-off".
                bool turned = TurnedOnThisJoin.TryGetValue(r.Id, out var on);
                list.Add(new RejoinRow
                {
                    Id = r.Id,
                    Name = r.Name,
                    Package = r.Package ?? "",
                    Version = turned && on.Version.Length > 0 && !r.Ready ? on.Version : r.RequiredVersion,
                    Required = true,
                    Result = r.Ready ? "ready"
                        : turned || r.TurnOnPending ? "turned-on"
                        : r.Action == JoinRowAction.TurnOn ? "turned-off"
                        : r.Action == JoinRowAction.TellAdmin ? "server-must-update" : "missing",
                });
            }

            // JOINTURNON: recommended mods turned on for the next start count as added once that start runs them.
            foreach (var kv in TurnedOnThisJoin)
            {
                if (kv.Value.Required || seen.Contains(kv.Key)) continue;
                seen.Add(kv.Key);
                list.Add(new RejoinRow { Id = kv.Key, Name = kv.Value.Name, Version = kv.Value.Version, Required = false, Result = "turned-on" });
            }
        }
        catch { /* the summary is a convenience */ }

        return list;
    }

    // ---- Join now and the reconnect -------------------------------------------------------------

    private static string StartJoinNow()
    {
        JoinTarget target;
        lock (Gate)
        {
            // F19/F22 review (M1): a result page that names its own server (a failed rejoin, a server restarting) dials that
            // server; the target belongs to that page, so it never outlives it.
            var offer = _session != null && !_session.Closed && _session.Page == JoinPage.Result ? _session.Result : null;
            target = offer?.RetryTarget ?? _batchTarget ?? CurrentTargetLocked();
            // F22: a Rejoin when it's back keeps trying while the server restarts; any other Join ends that wait.
            if (offer is { WhenBack: true, RetryTarget: not null })
            {
                _waitTarget = offer.RetryTarget;
                _waitUntil = Clock() + WaitForServerSeconds;
                _waitAttempts = 0;
            }
            else StopWaitingLocked();
        }

        if (!target.CanReconnect) return "no address to reconnect to";
        if (ConnectHost == null) return "reconnecting is not available here";
        OpenReconnect(target, afterRestart: false, record: null);
        return "joining";
    }

    /// <summary>Open the Rejoin page and start connecting (unless the server needs its password).</summary>
    private static void OpenReconnect(JoinTarget target, bool afterRestart, RejoinRecord? record)
    {
        var host = ConnectHost;
        // F32 (live 2026-10-08): "a password was used last time" is a hint, never a reason not to connect. Every rejoin
        // path (Join now, the rejoin-when-back retry, Join again) connects without a password first; the game's own
        // prompt opens only if the server refuses that connect for its password (JOINPW1).
        if (!target.PromptForPassword) target.PromptOnPasswordRefusal = true;
        // LOCALREJOIN1: this PC's own server is reconnected through the loopback address (the original stays the fallback).
        var connectTo = target;
        JoinTarget? fallback = null;
        var probe = LocalRejoinFactsProbe;
        if (probe != null)
        {
            JoinLocalFacts? facts;
            try { facts = probe(target); }
            catch { facts = null; }
            (connectTo, fallback) = JoinLocalRoute.Decide(target, facts, out string routeLog);
            Plugin.Logger?.LogInfo(routeLog);
        }

        // Review: the loopback join keeps the original endpoint as its record key (profiles, pins, acks, last server).
        if (fallback != null) ConnectHostAlias.Set(ConnectHostKey.Endpoint(connectTo.Address, connectTo.Port), ConnectHostKey.Endpoint(target.Address, target.Port));
        else ConnectHostAlias.Clear();

        JoinFlowSession opened;
        lock (Gate)
        {
            var view = new JoinRejoinView
            {
                ServerName = JoinText.ServerLabel(target.ServerName, target.Endpoint),
                Endpoint = target.Endpoint,
                AfterRestart = afterRestart,
                Phase = "leaving",
                Status = JoinText.ConnectingTo(connectTo.Endpoint),
                Progress = 5,
            };
            if (record != null) view.Summary = RejoinSummary.From(record, LoadedVersion);
            var model = JoinPageModel.Build(new JoinFacts { ServerName = target.ServerName, Host = target.Endpoint });
            opened = new JoinFlowSession(JoinMode.Rejoin, model, LiveEffects) { RejoinView = view };
            _session = opened;
            _rejoinRecord = record;
            _reconnect = null;
            _reconnectCanceledAt = -1;
            if (host == null)
            {
                view.Phase = "failed";
                view.Status = "Reconnecting is not available here. Join the server again from the server list.";
            }
            else _reconnect = new JoinReconnect(connectTo, host, Clock(), fallback);
            // F22 review (M2): only the restarting server's own reconnect is retried; any other one ends the wait.
            if (_reconnect != null && _waitTarget != null && ReferenceEquals(target, _waitTarget)) _waitReconnect = _reconnect;
            else
            {
                _waitReconnect = null;
                if (_waitTarget != null && !ReferenceEquals(target, _waitTarget)) StopWaitingLocked();
            }
        }

        if (host == null) RejoinStore.Delete(RejoinPath);
        Plugin.Logger?.LogInfo("JoinFlow: " + (afterRestart ? "rejoining" : "joining") + " " + target.Endpoint + " (" + JoinTarget.CanonicalProtocol(target.Protocol) + ")"
            + (target.PromptForPassword ? "; the server needs its password: the game's own prompt asks for it"
                : target.PromptOnPasswordRefusal && target.NeedsPassword ? "; a password was used last time: the game's prompt opens only if the server asks for one"
                : ""));
        RaiseChanged();
        bool drawn;
        try { drawn = ScreensDrawn(); }
        catch { drawn = false; }
        if (drawn) opened.Opened();
    }

    // ---- F22: rejoin a server that restarts for Progenitor (Stop server to apply, a pending server restart) --------

    /// <summary>F22: how long after the connection ended a rejoin keeps waiting for the server to come back.</summary>
    internal const double WaitForServerSeconds = 300;

    /// <summary>F22: the pause between connect attempts while waiting.</summary>
    internal const double WaitRetrySeconds = 10;

    private static double _rejoinArmedUntil = -1;
    private static string _rejoinArmedWhy = "";
    private static bool _rejoinArmedAuto;
    private static JoinTarget? _rejoinArmedTarget;
    private static double _waitUntil = -1;
    private static double _waitRetryAt = -1;
    private static int _waitAttempts;
    private static JoinTarget? _waitTarget;
    private static JoinReconnect? _waitReconnect;

    /// <summary>F22: the connected server reports installs waiting for its restart (live: the remote status). Null: never.</summary>
    internal static Func<bool>? ServerRestartPendingProbe { get; set; }

    /// <summary>
    /// F22 (live 2026-10-07): Progenitor asked the connected server to stop (its stop request was accepted), or knows a
    /// restart is due: when this connection ends within ten minutes, the screens offer to rejoin it when it is back.
    /// F37 (live 2026-10-08): <paramref name="autoRejoin"/> is the player's own restart (Save, stop and start again): the
    /// rejoin starts by itself when the connection ends, on the Rejoin page with its Cancel, instead of asking.
    /// </summary>
    internal static void ArmRejoinWhenBack(string why, bool autoRejoin = false)
    {
        lock (Gate)
        {
            _rejoinArmedUntil = Clock() + 600;
            _rejoinArmedWhy = why ?? "";
            _rejoinArmedAuto = autoRejoin;
            _rejoinArmedTarget = CurrentTargetLocked();
        }

        Plugin.Logger?.LogInfo("JoinFlow: the connected server is restarting (" + why + "); "
            + (autoRejoin ? "it is rejoined by itself when the connection ends" : "a rejoin is offered when the connection ends"));
    }

    // Called with Gate held: Core going down (and tests) drops the armed and waiting rejoin.
    private static void ResetRejoinWaitLocked()
    {
        _rejoinArmedUntil = -1;
        _rejoinArmedAuto = false;
        _rejoinArmedTarget = null;
        _waitUntil = -1;
        _waitRetryAt = -1;
        _waitAttempts = 0;
        _waitTarget = null;
        _promptWatch = null;
    }

    // Called with Gate held: no reconnect waits for a restarting server any more.
    private static void StopWaitingLocked()
    {
        _waitReconnect = null;
        _waitUntil = -1;
        _waitRetryAt = -1;
        _waitAttempts = 0;
        _waitTarget = null;
    }

    /// <summary>F22 review (M3): the stop did not happen: no rejoin is offered for it.</summary>
    internal static void DisarmRejoinWhenBack()
    {
        bool was;
        lock (Gate)
        {
            was = _rejoinArmedUntil > 0;
            _rejoinArmedUntil = -1;
            _rejoinArmedAuto = false;
            _rejoinArmedTarget = null;
        }

        if (was) Plugin.Logger?.LogInfo("JoinFlow: the server's stop did not happen; no rejoin is offered for it");
    }

    // Called with Gate held.
    private static bool WaitingForServerLocked(double now) => _waitUntil > 0 && now < _waitUntil;

    /// <summary>F22: the connection ended: offer the rejoin when the server is restarting for Progenitor. True when offered.</summary>
    private static bool OfferRejoinWhenBack()
    {
        bool pending;
        try { pending = ServerRestartPendingProbe?.Invoke() ?? false; }
        catch { pending = false; }
        bool auto;
        string summary = "";
        lock (Gate)
        {
            double now = Clock();
            bool armed = _rejoinArmedUntil > now;
            if (!armed && !pending) return false;
            var ended = CurrentTargetLocked();
            var target = armed && _rejoinArmedTarget != null ? _rejoinArmedTarget : ended;
            auto = armed && _rejoinArmedAuto;
            _rejoinArmedUntil = -1;
            _rejoinArmedAuto = false;
            if (!target.CanReconnect || target.HostedByThisClient) return false;
            // F22 review (M3): only the connection the stop was for: the ended connection's endpoint is the armed one.
            if (ended.Address.Length > 0 && !string.Equals(ended.Endpoint, target.Endpoint, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Logger?.LogInfo("JoinFlow: the ended connection (" + ended.Endpoint + ") is not the stopped server's (" + target.Endpoint + "); no rejoin offered");
                return false;
            }
            string why = armed ? _rejoinArmedWhy : "installs wait for its restart";
            summary = JoinText.ServerRestarting(JoinText.ServerLabel(target.ServerName, target.Endpoint), why);
            var model = JoinPageModel.Build(new JoinFacts { ServerName = target.ServerName, Host = target.Endpoint });
            var s = new JoinFlowSession(JoinMode.Refused, model, LiveEffects);
            if (s.Page == JoinPage.Message) s.ViewMods();
            s.ShowResult(new JoinResultOffer
            {
                Kind = JoinAfterInstall.JoinNow,
                CanRejoin = true,
                JoinAgain = true,
                WhenBack = true,
                RetryTarget = target,
                Summary = summary,
            });
            _session = s;
            Plugin.Logger?.LogInfo("JoinFlow: the connection ended while the server restarts (" + why + "); "
                + (auto ? "rejoining it when it's back (the player started this restart)" : "Rejoin when it's back offered"));
        }

        RaiseChanged();
        // F37: the player started this restart, so the rejoin does not ask: it starts now, exactly as the button would,
        // and the Rejoin page's Cancel ends the wait. Its body keeps the reason.
        if (auto)
        {
            string started = StartJoinNow();
            lock (Gate)
            {
                if (_session is { Mode: JoinMode.Rejoin, RejoinView: { } view }) view.Why = summary;
            }

            Plugin.Logger?.LogInfo("JoinFlow: rejoin started by itself for the player's restart: " + started);
            RaiseChanged();
        }

        return true;
    }

    /// <summary>F22: the next connect attempt while waiting for the server to come back.</summary>
    private static void TickWaitForServer(double now)
    {
        JoinTarget? target;
        lock (Gate)
        {
            if (_waitRetryAt < 0 || now < _waitRetryAt || _reconnect != null) return;
            _waitRetryAt = -1;
            if (!WaitingForServerLocked(now))
            {
                _waitUntil = -1;
                return;
            }

            target = _waitTarget;
        }

        if (target != null) OpenReconnect(target, afterRestart: false, record: null);
    }

    private sealed class PromptWatch
    {
        public JoinTarget Target { get; init; } = new();
        public string Name { get; init; } = "";
        public double Deadline { get; set; }
    }

    private static PromptWatch? _promptWatch;


    /// <summary>F19: how long after the game's password prompt opened a rejoin with no join counts as failed.</summary>
    internal const double PromptWatchSeconds = 60;

    /// <summary>
    /// F19 (live 2026-10-07): after the rejoin handed over to the game's password prompt, a join ends the watch; the title
    /// screen with no world past <see cref="PromptWatchSeconds"/> (the prompt closed, or nothing connected) shows
    /// <c>Couldn't rejoin &lt;server&gt;: …</c> with Join, unless other join screens took over.
    /// </summary>
    private static void TickPromptWatch(double now)
    {
        PromptWatch? w;
        lock (Gate) w = _promptWatch;
        if (w == null) return;
        var host = ConnectHost;
        bool joined = false, world = false, menu = false;
        try
        {
            joined = host?.JoinedOk ?? false;
            world = host?.WorldExists ?? false;
            menu = host?.MainMenuReady ?? false;
        }
        catch { /* read again next frame */ }

        if (host == null || joined)
        {
            lock (Gate) _promptWatch = null;
            return;
        }

        if (world)
        {
            // A connect is under way (or in a world): its own outcome decides; watch again from here.
            w.Deadline = now + PromptWatchSeconds;
            return;
        }

        if (now < w.Deadline || !menu) return;
        lock (Gate)
        {
            _promptWatch = null;
            if (_session != null && !_session.Closed) return;
            var model = JoinPageModel.Build(new JoinFacts { ServerName = w.Target.ServerName, Host = w.Target.Endpoint });
            var s = new JoinFlowSession(JoinMode.Refused, model, LiveEffects);
            if (s.Page == JoinPage.Message) s.ViewMods();
            s.ShowResult(new JoinResultOffer
            {
                Kind = JoinAfterInstall.JoinNow,
                CanRejoin = true,
                JoinAgain = true,
                RetryTarget = w.Target,
                Summary = JoinText.RejoinFailed(w.Name, JoinText.PromptClosedReason),
            });
            _session = s;
        }

        Plugin.Logger?.LogInfo("JoinFlow: the rejoin of " + w.Name + " did not join after the game's password prompt; the result offers Join");
        RaiseChanged();
    }

    private static void TickReconnect(double now)
    {
        JoinReconnect? r;
        JoinFlowSession? s;
        lock (Gate)
        {
            r = _reconnect;
            s = _session;
        }

        if (r == null) return;
        if (r.Canceled)
        {
            r.Tick(now);
            if (!r.NeedsTicking || (_reconnectCanceledAt >= 0 && now - _reconnectCanceledAt > JoinReconnect.ConnectTimeoutSeconds))
                lock (Gate) if (ReferenceEquals(_reconnect, r)) _reconnect = null;
            return;
        }

        string before = r.Phase;
        r.Tick(now);
        if (r.TakePasswordRefusal())
        {
            Plugin.Logger?.LogInfo("JoinFlow: reconnect: " + r.Target.Endpoint + " refused the join without a password; opening the game's password prompt");
            NoteLastServerPasswordRefused(r.Target);
            if (s?.RejoinView != null && s.Mode == JoinMode.Rejoin) s.RejoinView.Status = JoinText.PasswordRefusedNote;
        }

        var view = s != null && s.Mode == JoinMode.Rejoin ? s.RejoinView : null;
        bool changed = false;
        if (view != null)
        {
            int progress = r.Progress(now);
            if (view.Phase != r.Phase || Math.Abs(view.Progress - progress) >= 10) changed = true;
            view.Phase = r.Phase == "leaving" ? "leaving" : r.Phase;
            view.Progress = progress;
        }

        if (!r.Finished)
        {
            if (changed) RaiseChanged();
            return;
        }

        JoinFlowSession? closeForPrompt = null;
        JoinFlowSession? closeJoined = null;
        lock (Gate)
        {
            if (ReferenceEquals(_reconnect, r)) _reconnect = null;
            if (view != null)
            {
                if (r.Phase == "prompted")
                {
                    // SERVEREMPTY1: the game's password prompt took over; this page steps aside so it can be seen.
                    view.Status = JoinText.PasswordPromptNote;
                    if (s != null && !s.Closed) closeForPrompt = s;
                }
                else if (r.Phase == "joined")
                {
                    _waitUntil = -1;
                    // INSTALLRESTART1: after a Restart and rejoin from the Mods window, one line says what the restart did.
                    view.Status = TakeAfterRestartLine(ClientRestartText.Rejoined(view.ServerName)) ?? "Joined " + view.ServerName + ".";
                    var joinedModel = JoinPageModel.Build(BuildFactsLocked());
                    view.Summary = RejoinSummary.FromModel(joinedModel, _rejoinRecord, LoadedVersion);
                    // Nothing required needs attention: the page closes by itself (the in-world screens still decide).
                    if (!JoinShowPolicy.JoinedPageNeeded(joinedModel) && s != null && !s.Closed) closeJoined = s;
                }
                else if (r.Phase == "failed" && r.NoServerAnswered && ReferenceEquals(r, _waitReconnect) && WaitingForServerLocked(now))
                {
                    // F22: the server is restarting; its connect fails until it is back. Try again shortly.
                    view.Status = JoinText.WaitingForServer(view.ServerName, ++_waitAttempts);
                    _waitRetryAt = now + WaitRetrySeconds;
                }
                else view.Status = "Could not reconnect: " + r.Error + ". Join the server again from the server list.";
            }
        }

        // F19 (live 2026-10-07): the game's password prompt took the rejoin over. If no join follows, the player must not
        // be left at the title screen with nothing said: a watch shows the result with Join.
        if (r.Phase == "prompted")
            lock (Gate) _promptWatch = new PromptWatch { Target = r.Target, Name = view?.ServerName ?? r.Target.Endpoint, Deadline = now + PromptWatchSeconds };
        // Review: a reconnect that did not join (failed, canceled) leaves no alias behind for a later, unrelated join.
        if (r.Phase is not ("joined" or "prompted")) ConnectHostAlias.Clear();
        RejoinStore.Delete(RejoinPath);
        Plugin.Logger?.LogInfo("JoinFlow: reconnect " + r.Phase + (r.Error.Length > 0 ? " (" + r.Error + ")" : "") + " (was " + before + ")");
        if (closeForPrompt != null)
        {
            try { closeForPrompt.Close(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: closing the Joining page for the password prompt threw: " + e.Message); }
        }

        if (closeJoined != null)
        {
            Plugin.Logger?.LogInfo("JoinFlow: " + (view?.Status ?? "joined") + " Nothing to decide; the Joining page closes");
            try { closeJoined.Close(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: closing the Joining page after the join threw: " + e.Message); }
        }

        RaiseChanged();
    }

    // Called with Gate held from OpenOrRefresh: the join a reconnect started is answered by its own screens.
    private static string EndReconnectLocked(string why, JoinPageModel model)
    {
        var r = _reconnect;
        _reconnect = null;
        r?.Refused();
        var record = _rejoinRecord;
        bool afterRestart = _session?.RejoinView?.AfterRestart ?? false;
        _rejoinRecord = null;
        RejoinStore.Delete(RejoinPath);
        Plugin.Logger?.LogInfo("JoinFlow: the reconnect's join was " + why + "; its screens take over");
        if (why != "refused") return "";
        try { return RejoinSummary.FromModel(model, record, LoadedVersion).Line(afterRestart); }
        catch { return ""; }
    }

    private static string CancelReconnect()
    {
        JoinReconnect? r;
        JoinFlowSession? s;
        lock (Gate)
        {
            r = _reconnect;
            s = _session;
            // F22 review (M2): Cancel ends a wait for a restarting server too.
            StopWaitingLocked();
            if (r != null)
            {
                r.Cancel();
                _reconnectCanceledAt = Clock();
                // A connect already launched leaves the world it reaches (TickReconnect keeps ticking it).
                if (!r.Launched) _reconnect = null;
            }

            // INSTALLRESTART1: a resume not yet pressed stops here; once Continue was pressed the game hosts it.
            _resume?.Cancel();
            _resume = null;

            if (s?.RejoinView != null && !s.RejoinView.Finished)
            {
                s.RejoinView.Phase = "canceled";
                s.RejoinView.Status = "Canceled.";
            }

            _rejoinRecord = null;
        }

        RejoinStore.Delete(RejoinPath);
        Plugin.Logger?.LogInfo("JoinFlow: reconnect canceled");
        return "canceled";
    }

    /// <summary>The Rejoin page closed (Cancel, Escape, Continue): a connect still in progress is canceled.</summary>
    private static void ClosedRejoin()
    {
        JoinReconnect? r;
        JoinResume? resume;
        lock (Gate)
        {
            r = _reconnect;
            resume = _resume;
        }

        if ((r != null && !r.Finished) || (resume != null && !resume.Finished)) CancelReconnect();
    }

    // ---- after-relaunch pickup -------------------------------------------------------------------

    /// <summary>Once per Core generation, when the main menu is ready: pick up a rejoin record a relaunch left.</summary>
    private static void TickRejoinPickup()
    {
        if (_rejoinChecked) return;
        var host = ConnectHost;
        if (host == null) return;
        bool ready;
        try { ready = host.MainMenuReady; }
        catch { ready = false; }
        if (!ready) return;
        _rejoinChecked = true;
        // INSTALLRESTART1: what the Mods window's Restart now waited on, for the after-restart line (before the rejoin opens).
        PickUpInstallRestart("start");
        var peek = RejoinStore.Peek(RejoinPath);
        if (peek != null && peek.DryRun && !peek.Used)
        {
            Plugin.Logger?.LogInfo("JoinFlow: a dry-run rejoin record is waiting; join_ui rejoin_now picks it up");
            return;
        }

        PickUp("start");
    }

    /// <summary>Take the record and rejoin (or say why not). <paramref name="source"/> is <c>start</c> or <c>bridge</c>.</summary>
    internal static string PickUp(string source)
    {
        var pickup = RejoinStore.Take(RejoinPath, DateTime.UtcNow, out var record);
        _lastPickup = RejoinStore.PickupName(pickup);
        Plugin.Logger?.LogInfo("JoinFlow: rejoin record at " + source + ": " + _lastPickup);
        if (pickup == RejoinPickup.Rejoin && record != null)
        {
            // INSTALLRESTART1: a hosted save resumes through the title screen's Continue; a server is dialed again.
            if (record.IsHosted) OpenResume(record);
            else OpenReconnect(record.ToTarget(), afterRestart: true, record);
        }
        return _lastPickup;
    }

    // ---- readback --------------------------------------------------------------------------------

    internal static string AvailabilityName(PackageVersionAvailability a) => a switch
    {
        PackageVersionAvailability.Available => "available",
        PackageVersionAvailability.VersionGone => "version-gone",
        PackageVersionAvailability.NotListed => "not-listed",
        _ => "unknown",
    };

    internal static Dictionary<string, object?>? DescribeInstall(JoinInstallRowState? state)
    {
        if (state == null) return null;
        return new Dictionary<string, object?>
        {
            ["key"] = state.Request.Key,
            ["name"] = state.Request.Name,
            ["package"] = state.Request.Package,
            ["version"] = state.Request.Version,
            ["kind"] = state.Request.Kind.ToString(),
            ["required"] = state.Request.Required,
            ["replaces"] = state.Request.Replaces,
            ["phase"] = JoinText.InstallPhaseName(state.Phase),
            ["word"] = JoinText.InstallPhaseWord(state),
            ["percent"] = state.Percent,
            ["reason"] = state.Reason,
            ["pluginId"] = state.PluginId,
            ["path"] = state.Path,
        };
    }

    internal static Dictionary<string, object?>? DescribeRecord(RejoinRecord? r)
    {
        if (r == null) return null;
        var rows = new List<object?>();
        foreach (var row in r.Rows)
            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = row.Id, ["name"] = row.Name, ["package"] = row.Package, ["version"] = row.Version,
                ["required"] = row.Required, ["result"] = row.Result,
            });
        return new Dictionary<string, object?>
        {
            ["address"] = r.Address,
            ["port"] = r.Port,
            ["protocol"] = r.Protocol,
            ["serverName"] = r.ServerName,
            ["fingerprint"] = r.Fingerprint,
            ["needsPassword"] = r.NeedsPassword,
            ["created"] = r.Created,
            ["used"] = r.Used,
            ["kind"] = r.Kind,
            ["save"] = r.Save,
            ["dryRun"] = r.DryRun,
            ["verdictNow"] = RejoinStore.PickupName(RejoinStore.Judge(r, DateTime.UtcNow)),
            ["rows"] = rows,
        };
    }

    private static void DescribeStage2(Dictionary<string, object?> d)
    {
        JoinInstallBatch? batch;
        JoinTarget? target;
        JoinReconnect? r;
        Dictionary<string, object?>? relaunch;
        bool versionsLoaded;
        string versionsKey;
        lock (Gate)
        {
            batch = _batch;
            target = _batchTarget;
            r = _reconnect;
            relaunch = _lastRelaunch;
            versionsLoaded = _tsVersions != null;
            versionsKey = _tsVersionsKey;
        }

        var rows = new List<object?>();
        if (batch != null) foreach (var row in batch.Rows) rows.Add(DescribeInstall(row));
        d["batch"] = batch == null ? null : new Dictionary<string, object?>
        {
            ["started"] = batch.Started,
            ["done"] = batch.Done,
            ["concurrency"] = batch.Concurrency,
            ["rows"] = rows,
            ["summary"] = JoinText.BatchSummary(batch.Rows),
            ["target"] = target == null ? null : new Dictionary<string, object?>
            {
                ["address"] = target.Address, ["port"] = target.Port, ["protocol"] = target.Protocol,
                ["canReconnect"] = target.CanReconnect, ["hosted"] = target.HostedByThisClient, ["needsPassword"] = target.NeedsPassword,
            },
        };
        d["thunderstoreVersions"] = new Dictionary<string, object?> { ["loaded"] = versionsLoaded, ["packages"] = versionsKey };
        d["reconnect"] = r == null ? null : new Dictionary<string, object?>
        {
            ["phase"] = r.Phase, ["error"] = r.Error, ["launched"] = r.Launched, ["canceled"] = r.Canceled, ["endpoint"] = r.Target.Endpoint,
            ["promptOnPasswordRefusal"] = r.Target.PromptOnPasswordRefusal, ["passwordRefused"] = r.PasswordRefused,
        };
        d["lastRelaunch"] = relaunch;
        d["rejoinRecord"] = DescribeRecord(RejoinStore.Peek(RejoinPath));
        d["rejoinRecordPath"] = RejoinPath;
        d["lastPickup"] = _lastPickup;
    }

    private static List<object?> TurnOnsOf(JoinThunderstoreFacts t)
    {
        var list = new List<object?>();
        foreach (var r in t.TurnOns)
            list.Add(new Dictionary<string, object?>
            {
                ["id"] = r.Id, ["name"] = r.Name, ["version"] = r.Version,
                ["route"] = r.AppliesNow && !r.TurnsOnAtNextStart ? "now" : "restart",
            });
        return list;
    }

    private static void DescribeStage2Session(Dictionary<string, object?> d, JoinFlowSession s)
    {
        if (s.TsTrust != null)
        {
            var t = s.TsTrust;
            var packages = new List<object?>();
            foreach (var req in t.Requests)
                packages.Add(new Dictionary<string, object?>
                {
                    ["key"] = req.Key, ["name"] = req.Name, ["package"] = req.Package, ["version"] = req.Version,
                    ["kind"] = req.Kind.ToString(), ["required"] = req.Required, ["replaces"] = req.Replaces,
                    ["warning"] = req.Kind == JoinInstallKind.UseVersion ? JoinText.UseVersionWarning(req) : "",
                    ["keeps"] = req.Kind == JoinInstallKind.UseVersion && req.Keeps,
                });
            d["tsTrust"] = new Dictionary<string, object?>
            {
                ["title"] = JoinText.TsTrustTitle,
                ["intro"] = JoinText.TsTrustIntro(t.ServerName, t.Requests.Count),
                ["server"] = t.ServerName,
                ["host"] = t.Host,
                ["fingerprint"] = t.Fingerprint,
                ["firstSeen"] = t.FirstSeen,
                // 2026-10-05: the key-change line (empty when the key is the pinned one); the risk band is gone.
                ["keyChanged"] = JoinText.KeyChangedLine(t.PreviousFingerprint, t.PreviousSeen),
                ["packages"] = packages,
                // JOINTURNON: the installed-but-off mods the same press turns on (no download).
                ["turnOns"] = TurnOnsOf(t),
            };
        }

        if (s.Result != null)
        {
            d["result"] = new Dictionary<string, object?>
            {
                ["kind"] = KindName(s.Result.Kind),
                ["canRejoin"] = s.Result.CanRejoin,
                ["manualRelaunch"] = s.Result.ManualRelaunch,
                ["hosted"] = s.Result.Hosted,
                ["summary"] = s.Result.Summary,
                ["title"] = JoinFlowLayout.ResultTitle(s.Result),
                ["body"] = JoinFlowLayout.ResultBody(s.Result),
                ["buttons"] = JoinFlowLayout.ResultButtonKeys(s.Result),
                // INPLACE1: how the fix finishes: in-place (answered again on this connection; Done), rejoin (Join now
                // reconnects), restart (Restart and rejoin), or none.
                ["finishMode"] = FinishModeName(s.Result),
                ["joinAgain"] = s.Result.JoinAgain,
                ["inPlaceSent"] = s.Result.InPlace ? s.Result.InPlaceSent : null,
                // Review: what still fails the server's check when the fix did not answer again.
                ["inPlaceProblems"] = s.Result.InPlace ? new List<string>(s.Result.InPlaceProblems) : null,
            };
        }

        if (s.RejoinView != null)
        {
            var v = s.RejoinView;
            var required = new List<object?>();
            if (v.Summary != null)
                foreach (var kv in v.Summary.Required)
                    required.Add(new Dictionary<string, object?> { ["name"] = kv.Key, ["state"] = kv.Value });
            d["rejoin"] = new Dictionary<string, object?>
            {
                ["title"] = v.Resume ? ClientRestartText.ResumeTitle(v.ServerName) : JoinText.RejoinTitle(v.ServerName, v.AfterRestart),
                ["resume"] = v.Resume,
                ["body"] = JoinFlowLayout.RejoinBodyText(v),
                ["afterRestart"] = v.AfterRestart,
                ["endpoint"] = v.Endpoint,
                ["phase"] = v.Phase,
                ["status"] = v.Status,
                ["progress"] = v.Progress,
                ["requiredHeading"] = v.Summary == null ? "" : JoinText.SummaryRequiredHeading(v.Summary.RequiredReady, v.Summary.RequiredTotal),
                ["required"] = required,
                ["recommendedHeading"] = v.Summary == null ? "" : JoinText.SummaryRecommendedHeading(v.Summary.RecommendedAdded),
            };
        }
    }
}
