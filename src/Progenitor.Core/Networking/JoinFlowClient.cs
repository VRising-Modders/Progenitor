using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Progenitor.Networking;

/// <summary>
/// JOINFLOW1: the client half of the join screens — what the server sent for this join (the catalog body
/// and the signed join section), what this client knows (the handshake's <c>ServerAsked</c> and
/// <c>Provided</c> tables, snapshotted while they are valid), and the open <see cref="JoinFlowSession"/>.
/// <see cref="JoinFlowPanel"/> draws whatever this holds and routes every button here.
///
/// <list type="bullet">
/// <item><b>Refused join</b>: the deny report (<c>ModManifestDeny.PanelRequested</c>) opens the screens on
/// the disconnect screen; the signed descriptor that follows adds sources and the message.</item>
/// <item><b>Install window</b> (<c>Ok = 2</c>): the descriptor opens the screens; the player fixes the rows from
/// them (Thunderstore, Turn on, Use kept, Pause for this server) and the join re-answers once they load.</item>
/// <item><b>Successful join</b>: the screens open in the world, <see cref="InWorldDelaySeconds"/> after the
/// catalog arrived, when the server's page frequencies say a page shows on this join
/// (<see cref="JoinShowPolicy"/> against <see cref="JoinSeenStore"/>), and never twice in one client world
/// (a Core swap keeps the world, so it does not count as a join).</item>
/// </list>
/// </summary>
internal static partial class JoinFlowClient
{
    /// <summary>How long after the catalog the in-world message waits, so it does not land on the loading screen.</summary>
    public const double InWorldDelaySeconds = 2.0;

    private static readonly object Gate = new();
    private static JoinSection? _section;
    private static ManifestBody? _catalog;
    private static string _host = "";
    private static string _serverName = "";
    private static string _fp = "";
    private static string _spki = "";
    private static Handshake.ModRecord[] _asked = Array.Empty<Handshake.ModRecord>();
    private static Handshake.ModRecord[] _provided = Array.Empty<Handshake.ModRecord>();
    private static bool _refused;
    // Live 2026-10-13: the Core capabilities the server's refusal named (no mod row covers them).
    private static IReadOnlyList<JoinCoreRequirement> _coreRefusal = Array.Empty<JoinCoreRequirement>();
    private static bool _window;
    private static JoinFlowSession? _session;
    private static bool _messageShownThisJoin;
    private static double _inWorldDueAt = -1;

    // Live 2026-10-04: a catalog resend mid-session (an admin's setting change) opened the screens over
    // the settings page. The in-world screens are decided once per join, by the first catalog after a join
    // attempt this Core generation observed; a Core loaded mid-session never opens them by itself.
    private static bool _joinObserved;
    private static bool _inWorldDecided;

    // Live 2026-10-04: after an install-window download the in-world screens reopened on "the server's mod
    // list changed" with the installed mod tagged Changed. This join's window and what it installed are
    // remembered so the in-world decision knows the change was the player's own install.
    private static bool _windowScreensShown;
    private static readonly HashSet<string> InstalledThisJoin = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Effects LiveEffects = new();

    /// <summary>Raised when the screens should be redrawn (opened, a page changed, fresher facts arrived, closed).</summary>
    public static event Action? Changed;

    /// <summary>Seconds, monotonic. Tests pin it.</summary>
    internal static Func<double> Clock { get; set; } = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>True once the joined world can take an overlay (set by the panel; tests pin it).</summary>
    internal static Func<bool> InWorldReady { get; set; } = () => false;

    /// <summary>A loaded mod's readable name (set by the panel from the Mods inventory).</summary>
    internal static Func<string, string?>? NameLookup { get; set; }

    /// <summary>Leave the server from the world (set by the panel; the production ExitGame view).</summary>
    internal static Action? LeaveAction { get; set; }

    /// <summary>Put text on the clipboard (set by the panel; tests pin it).</summary>
    internal static Func<string, bool>? Clipboard { get; set; }

    /// <summary>Where the "don't show again" store lives.</summary>
    internal static string SeenPath => Path.Combine(ModManifest.ConfigDirectory, JoinSeenStore.FileName);

    public static JoinFlowSession? Session
    {
        get { lock (Gate) return _session; }
    }

    /// <summary>The verified join section of this join, or null.</summary>
    public static JoinSection? Section
    {
        get { lock (Gate) return _section; }
    }

    /// <summary>The joined server's time zone (from its join section), or null when no section arrived.</summary>
    public static TimeZoneInfo? ServerZone
    {
        get
        {
            JoinSection? s;
            lock (Gate) s = _section;
            if (s == null) return null;
            try { return s.Zone.Resolve(); }
            catch { return null; }
        }
    }

    /// <summary>The server key the seen store uses: <c>&lt;fingerprint&gt;@&lt;address&gt;</c>.</summary>
    public static string ServerKey
    {
        get { lock (Gate) return JoinSeenStore.ServerKey(_fp, _host); }
    }

    /// <summary>True while the in-world screens are waiting to open.</summary>
    internal static bool InWorldPending
    {
        get { lock (Gate) return _inWorldDueAt >= 0; }
    }

    // ---- lifecycle -------------------------------------------------------------------------

    /// <summary>A new join attempt (the handshake request): everything about the previous join goes.</summary>
    internal static void OnJoinAttempt()
    {
        JoinFlowSession? closing;
        bool hadNotice;
        lock (Gate)
        {
            closing = _session;
            // JOINFLOW2: the reconnect's own page stays through the join it started.
            if (closing != null && closing.Mode == JoinMode.Rejoin && !closing.Closed) closing = null;
            else _session = null;
            // A finished batch belongs to the previous join: this join's rows start fresh (an install
            // repeated over the same bytes is the catalog's idempotent "already pending"). One still
            // running keeps writing, but its result is that join's, not this one's.
            if (_batch != null)
            {
                if (_batch.Done) _batch = null;
                else _batchFinishedHandled = true;
            }

            // Something may have changed (another server, a new list): refusals are asked again on a new join.
            // A finished batch's server is not this join's: Restart and rejoin, Join now and the kept-copy offers
            // reconnect to the server this join went to (review: a stale target rejoined the previous server).
            if (_batch == null) _batchTarget = null;
            RefusedThisJoin.Clear();
            _joinGeneration++;
            _section = null;
            _catalog = null;
            _host = _serverName = _fp = _spki = "";
            _asked = Array.Empty<Handshake.ModRecord>();
            _provided = Array.Empty<Handshake.ModRecord>();
            _refused = _window = false;
            _coreRefusal = Array.Empty<JoinCoreRequirement>();
            _messageShownThisJoin = false;
            _inWorldDueAt = -1;
            _joinObserved = true;
            _inWorldDecided = false;
            _windowReAnswer = null;
            _windowScreensShown = false;
            InstalledThisJoin.Clear();
            _tsVersions = null;
            _tsVersionsKey = "";
            _passwordUsed = false;
            CapturePasswordLocked();
            ForgetTurnOnsLocked();
            ForgetClientModsLocked();
            hadNotice = ForgetChangeNoticeLocked() != null;
            // Live 2026-10-05: the address this join dialed, for Join now (never the catalog's host).
            CaptureDialedLocked();
        }

        if (hadNotice) DismissChangeNotice();
        if (closing != null) RaiseChanged();
    }

    /// <summary>
    /// The refusal ended (an accepted or deferred result, the main menu, Core going down): the screens of a
    /// refused join or an install window close; an in-world session closes too (the main menu). A pending
    /// in-world open is canceled. The stored section stays for the rest of this join.
    /// </summary>
    internal static void OnDenyEnded()
    {
        bool had, hadNotice;
        lock (Gate)
        {
            // JOINFLOW2: the reconnect's page survives the menu it passes through; the Result page of a batch
            // that finished on the refusal screen survives the player's Back to the menu too.
            bool keep = _session != null && !_session.Closed && _session.Page is JoinPage.Rejoin or JoinPage.Result;
            had = _session != null && !keep;
            if (!keep) _session = null;
            _refused = false;
            _coreRefusal = Array.Empty<JoinCoreRequirement>();
            _window = false;
            _inWorldDueAt = -1;
            hadNotice = _changeNotice != null && !InWorldNow();
            if (hadNotice) _changeNotice = null;
        }

        if (hadNotice) DismissChangeNotice();
        if (had) RaiseChanged();
    }

    /// <summary>
    /// CLIENTPOLICY3 (live 2026-10-05): the connection to the server ended while the player was in its world (a kick, a
    /// timeout). The game draws its own "Communication with server was lost … [Leave Game]" banner in that world; an
    /// open in-world page (the welcome message, the mods page) would cover it and the game would look frozen, so it
    /// closes, a pending in-world open is canceled and the change notice goes. The screens of a refused join or an
    /// install window, and the Result and reconnect pages, stay: they are meant for the disconnect screen.
    /// </summary>
    internal static bool OnConnectionLost()
    {
        // A6: the connection that an install window held ended without a refusal reaching this client.
        if (EndWindow(JoinText.ConnectionEndedReason)) return true;
        bool had, hadNotice;
        lock (Gate)
        {
            var s = _session;
            had = s != null && !s.Closed && s.Mode == JoinMode.InWorld && s.Page is not (JoinPage.Result or JoinPage.Rejoin);
            if (had) _session = null;
            _inWorldDueAt = -1;
            hadNotice = _changeNotice != null;
            if (hadNotice) _changeNotice = null;
        }

        if (hadNotice) DismissChangeNotice();
        if (had)
        {
            Plugin.Logger?.LogInfo("JoinFlow: the connection ended; the in-world screens closed so the game's own disconnect banner shows");
            RaiseChanged();
        }

        // F22: a server restarting for Progenitor (its stop request was accepted, or installs wait for its restart) is
        // offered a rejoin when it is back, instead of only the game's "Communication with server was lost".
        if (OfferRejoinWhenBack()) return true;
        return had;
    }

    /// <summary>
    /// A6 (live 2026-10-07): the join an install window held ended (the server's refusal or the connection). The window's
    /// screens become the refusal's: the reason, and Join again (the reconnect, with the loopback rule) when everything
    /// the server requires is in place now; else the mods page with the reason. False when no window's screens are open.
    /// </summary>
    internal static bool EndWindow(string? reason)
    {
        lock (Gate)
        {
            var s = _session;
            if (s == null || s.Closed || s.IsPreview || s.Mode != JoinMode.InstallWindow) return false;
            try
            {
                var live = ModManifestDeny.Provided();
                if (live.Count > 0) _provided = Copy(live);
            }
            catch { /* the snapshot stays */ }

            _window = false;
            _refused = true;
            _windowReAnswer = null;
            var model = JoinPageModel.Build(BuildFactsLocked());
            var ended = new JoinFlowSession(JoinMode.Refused, model, LiveEffects);
            if (ended.Page == JoinPage.Message) ended.ViewMods();
            _session = ended;
            string server = JoinText.ServerLabel(ServerNameLocked(), _host);
            bool fixedNow = model.AllRequiredReady;
            string text = JoinText.WindowEnded(server, reason ?? "", fixedNow);
            if (fixedNow)
            {
                var target = CurrentTargetLocked();
                bool canRejoin = target.CanReconnect && !target.HostedByThisClient;
                ended.ShowResult(new JoinResultOffer
                {
                    Kind = canRejoin ? JoinAfterInstall.JoinNow : JoinAfterInstall.Nothing,
                    CanRejoin = canRejoin,
                    Hosted = target.HostedByThisClient,
                    JoinAgain = true,
                    Summary = text + (canRejoin ? "" : " Join the server again from the server list."),
                });
            }
            else ended.Note(text);
            Plugin.Logger?.LogInfo("JoinFlow: the install window's join ended (" + (reason ?? "") + "); " + (fixedNow ? "Join again offered" : "the mods page shows why"));
        }

        RaiseChanged();
        return true;
    }

    internal static void Uninstall()
    {
        // JOINFLOW2: Core going down stops what it owns: rows not yet writing, the reconnect.
        JoinInstallBatch? batch;
        lock (Gate)
        {
            batch = _batch;
            _batch = null;
            _reconnect = null;
            _session = null;
            _rejoinRecord = null;
            ResetRejoinWaitLocked();
        }

        try { batch?.Cancel(); }
        catch { /* teardown */ }
        OnJoinAttempt();
        lock (Gate) _joinObserved = false;
        Changed = null;
        ChangeNoticeRequested = null;
        ChangeNoticeDismissed = null;
        NameLookup = null;
        ConnectedNameLookup = null;
        LeaveAction = null;
        Clipboard = null;
        InWorldReady = () => false;
        ScreensDrawn = () => true;
        InPlaceNotice = null;
        WorldStamp = () => "";
        InstallerFactory = null;
        LocalTestFolderProbe = null;
        LocalRejoinFactsProbe = null;
        ServerRestartPendingProbe = null;
        LocalFolderFactsCache.Compute = null;
        LocalFolderFactsCache.Clear();
        Progenitor.Install.LocalServerDetectCache.Clear();
        LocalTestGuidsProbe = null;
        PackageIndex = null;
        TargetProbe = null;
        Relauncher = null;
        ConnectHost = null;
        LastServerReaderForTests = null;
        LiveHostProbe = null;
        _lastServerNoted = "";
        _lastServerInputs = "";
        LastServerFullLooks = 0;
        _lastServerCheckedAt = -10;
        _joinedWithoutProgenitor = false;
        PasswordProbe = null;
        OffCopyProbe = null;
        SwitchProbe = null;
        ClientModsProbe = null;
        PausedHereProbe = null;
        PausableProbe = null;
        TurnsOffAtRestartProbe = null;
        PolicyPauseProbe = null;
        try { _turnOffConfirm?.Close(); }
        catch { /* teardown */ }
        _turnOffConfirm = null;
        _rejoinChecked = false;
        _relaunching = 0;
        ResetInstallRestart();
    }

    /// <summary>
    /// The process and client world the screens open in (set by the panel: the process id and the client
    /// world's sequence number). A Core swap keeps both, so the in-world screens never open twice in one world.
    /// Empty in tests.
    /// </summary>
    internal static Func<string> WorldStamp { get; set; } = () => "";

    // ---- inputs ----------------------------------------------------------------------------

    /// <summary>
    /// The deny report for a refused join: open the screens (replacing the read-only deny panel). Returns
    /// true when the screens took it.
    /// </summary>
    internal static bool OnDenyReport(ModDenyReport report)
    {
        if (report == null) return false;
        lock (Gate)
        {
            Snapshot();
            CapturePasswordLocked();
            _refused = true;
            _coreRefusal = JoinCoreRefusal.Parse(report.KickReason);
            if (_coreRefusal.Count > 0)
                Plugin.Logger?.LogInfo("JoinFlow: refused for Progenitor itself: " + JoinText.CoreRefusedDetail(_coreRefusal)
                    + "; server Progenitor " + (JoinText.CoreRefusedServerVersion(_coreRefusal) is { Length: > 0 } v ? v : "not named")
                    + ", this client " + OwnCoreVersion());
            if (_host.Length == 0) _host = report.Host ?? "";
            if (report.ServerName.Length > 0) _serverName = report.ServerName;
        }

        // A6 (live 2026-10-07): the join an install window held was refused: the window's page must not keep saying the
        // join continues. It shows the server's reason and, with the fix in place, Join again.
        if (EndWindow(report.KickReason))
        {
            bool shown;
            try { shown = ScreensDrawn(); }
            catch { shown = false; }
            return shown;
        }

        OpenOrRefresh(JoinMode.Refused, preferMessage: true);
        // Screens that could not be drawn must not hide the refusal: the deny panel shows it instead.
        bool drawn;
        try { drawn = ScreensDrawn(); }
        catch { drawn = false; }
        return drawn;
    }

    /// <summary>The handshake accepted this join (set by the tests; live: the last result was <c>ok</c>).</summary>
    internal static Func<bool> JoinAccepted { get; set; } =
        () => string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal);

    /// <summary>
    /// An install window that still decides this join: armed, and the join not yet accepted. The client's
    /// window stays armed after the re-answered join is accepted (until the world is ready or the
    /// connection ends); live round 3 showed a catalog in that gap opening a new window session. Every
    /// "are we in a window" question in the join flow (and the consent prompt's rate window) asks this.
    /// </summary>
    internal static bool InstallWindowOpen
    {
        get
        {
            bool accepted;
            try { accepted = JoinAccepted(); }
            catch { accepted = false; }
            return ClientInstallWindow.IsArmed && !accepted;
        }
    }

    /// <summary>A verified catalog (and its section, when it carried one) for this join.</summary>
    internal static void OnCatalog(PendingManifest? pending, JoinSection? join, bool denied)
    {
        if (pending == null || !pending.SignatureValid) return;
        // Mod compatibility C3: the verified catalog's per-mod ranges and match and its Version matching, for the pre-join
        // prompt and the cap.
        Progenitor.Install.ClientUpdates.NoteServerCatalog(pending.Envelope.Body);
        bool window = InstallWindowOpen;
        bool scheduleInWorld = false;
        bool midSession = false;
        JoinMode? open = null;
        JoinChangeNoticeState? notice = null;
        string noticeWhy = "";
        lock (Gate)
        {
            // A catalog after this join's in-world decision is a re-sign the server resent (an admin's change, a load).
            midSession = !denied && !window && _inWorldDecided;
            _catalog = pending.Envelope.Body;
            _section = join;
            _host = pending.Host ?? _host;
            _serverName = pending.Envelope.Body.ServerName ?? _serverName;
            _fp = pending.Fingerprint ?? "";
            _spki = pending.Spki ?? "";
            Snapshot();
            CapturePasswordLocked();
            ForgetClientModsLocked();
            // Live 2026-10-04 (round 3): after the window's download the re-answered join was accepted, the
            // window's screens closed, and the catalog the server sends to a handshake-ok client arrived
            // while the client's window was still armed. It opened a new install-window session, all rows
            // ready, on "mods-changed": its lists differed from the last visit only by the mod just
            // installed (a new build's version and the server sharing it), and the install-window decision
            // never applies the after-install rule. A window catalog with no screens open, after the
            // window's screens were shown, with every required mod ready, is the join completing: it is
            // decided as the in-world catalog (skipped-installed, lists recorded).
            if (!denied && JoinInWorldGate.WindowJoinCompleted(window, _session != null && !_session.Closed, _windowScreensShown,
                    () => JoinPageModel.Build(BuildFactsLocked()).AllRequiredReady))
            {
                window = false;
                _window = false;
                Plugin.Logger?.LogInfo("JoinFlow: the install window's join completed; its catalog is decided as the in-world one");
            }

            if (denied) open = _session != null ? JoinMode.Refused : (JoinMode?)null;
            else if (window)
            {
                _window = true;
                open = JoinMode.InstallWindow;
            }
            else if (_session != null && !_session.Closed && !_session.IsPreview && _session.Mode == JoinMode.InWorld) open = JoinMode.InWorld;
            else if (JoinInWorldGate.MayDecide(_joinObserved, _inWorldDecided, _inWorldDueAt >= 0))
            {
                // The first catalog of this join decides; a resend (a setting change, a plugin load) never does.
                _inWorldDecided = true;
                // JOINNOTICE: what this session saw, against which a later re-signed catalog is compared.
                NoteSeenLocked(JoinPageModel.Build(BuildFactsLocked()));
                if (ShouldShowInWorld())
                {
                    _inWorldDueAt = Clock() + InWorldDelaySeconds;
                    scheduleInWorld = true;
                }
            }
            else if (!_joinObserved && _section != null && !_inWorldDecided)
            {
                _inWorldDecided = true;
                NoteSeenLocked(JoinPageModel.Build(BuildFactsLocked()));
                Plugin.Logger?.LogInfo("JoinFlow: Core loaded during this session; the join screens do not open by themselves (Show reopens the message)");
            }

            // JOINNOTICE: a mid-session change shows a small notice when no join screens are open.
            if (midSession) notice = MidSessionChangeLocked(open != null, out noticeWhy);
        }

        // CLIENTPOLICY2: the policy this server signed, remembered for the next join's automatic pause (a refusal's section
        // carries it too, so the join after a refusal pauses before it reports).
        RememberPolicy(pending.Host ?? "", join);
        // CLIENTPOLICY3: a policy re-signed mid-session is re-checked now (the automatic pause, then a report).
        if (midSession)
        {
            try { ClientModReportClient.OnSignedPolicyMidSession(join?.HasClientPolicy == true); }
            catch (Exception e) { Plugin.Logger?.LogWarning("ClientPolicy: the mid-session re-check threw: " + e.Message); }
        }

        if (midSession)
            Plugin.Logger?.LogInfo("JoinFlow: the server re-signed its catalog mid-session; "
                + (open != null ? "the open screens refreshed" : "no join screens are open")
                + (notice != null ? "; notice shown" : noticeWhy.Length > 0 ? "; no notice (" + noticeWhy + ")" : ""));
        if (notice != null) RaiseChangeNotice(notice);
        if (open != null) OpenOrRefresh(open.Value, preferMessage: true);
        else if (scheduleInWorld) Plugin.Logger?.LogInfo("JoinFlow: the server message opens in the world in " + InWorldDelaySeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s");
    }

    /// <summary>True while joined: the client world is up and the handshake said ok.</summary>
    internal static bool InWorldNow()
    {
        try { return InWorldReady(); }
        catch { return false; }
    }

    /// <summary>The Mods-page Show button: a message arrived this join and the client is still in that world.</summary>
    internal static bool CanShowMessage => (Section?.Message.Trim().Length ?? 0) > 0 && InWorldNow();

    /// <summary>True while the screens are actually on show (set by the panel; tests leave it true).</summary>
    internal static Func<bool> ScreensDrawn { get; set; } = () => true;

    /// <summary>
    /// Once a frame from the panel: open the in-world screens when due and the world can take them, and move
    /// the batch, the reconnect and the turn-ons along. Cheap when idle (two field reads).
    /// </summary>
    internal static void Tick()
    {
        FrameOpen();
        FrameBatch();
        FrameReconnect();
        FrameInstallRestart();
        FrameLastServer();
        FrameTurnOns();
        FrameVersions();
    }

    /// <summary>
    /// The per-frame steps of <see cref="Tick"/>, in its order. The panel subscribes each to <c>Plugin.Frame</c> on its
    /// own, so the Dev <c>perf</c> op reports each as its own source (<c>core:networking/JoinFlowClient.FrameLastServer</c>)
    /// and a slow one names itself (2026-10-06: the last-server look was the whole of <c>core:networking</c>).
    /// </summary>
    internal static readonly Action[] FrameSteps = { FrameOpen, FrameBatch, FrameReconnect, FrameInstallRestart, FrameLastServer, FrameTurnOns, FrameVersions };

    /// <summary>Frame step: open the in-world screens when due and the world can take them.</summary>
    internal static void FrameOpen()
    {
        double now = Clock();
        bool openNow = false;
        lock (Gate)
        {
            // A preview on show keeps the in-world screens waiting until it closes.
            if (_inWorldDueAt >= 0 && now >= _inWorldDueAt && (_session == null || _session.Closed))
            {
                bool ready;
                try { ready = InWorldReady(); }
                catch { ready = false; }
                if (ready)
                {
                    _inWorldDueAt = -1;
                    openNow = ShouldShowInWorld();
                }
            }
        }

        if (openNow) OpenOrRefresh(JoinMode.InWorld, preferMessage: true);
    }

    /// <summary>Frame step (JOINFLOW2): the Thunderstore batch.</summary>
    internal static void FrameBatch() => TickBatch(Clock());

    /// <summary>Frame step (JOINFLOW2): the reconnect and the after-relaunch pickup.</summary>
    internal static void FrameReconnect()
    {
        TickReconnect(Clock());
        TickPromptWatch(Clock());
        TickWaitForServer(Clock());
        TickRejoinPickup();
    }

    /// <summary>Frame step (INSTALLRESTART1): the Mods window's Restart now (leave, then quit) and Restart and resume.</summary>
    internal static void FrameInstallRestart()
    {
        double now = Clock();
        TickInstallRestart(now);
        TickResume(now);
    }

    /// <summary>Frame step (SERVEREMPTY1): the last server joined, for the Server tab's Last server row.</summary>
    internal static void FrameLastServer() => TickLastServer(Clock());

    /// <summary>Frame step (JOINTURNON): hot turn-ons whose plugins are now declared move the join on.</summary>
    internal static void FrameTurnOns() => TickTurnOns(Clock());

    /// <summary>Frame step (JOINFLOW4): after leaving a server, the player's own versions come back once nothing of the join runs.</summary>
    internal static void FrameVersions()
    {
        try { ModVersionJoin.Tick(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: versions: tick threw: " + e.Message); }
    }

    // ---- the session -----------------------------------------------------------------------

    /// <summary>Open the screens for <paramref name="mode"/>, or refresh the open ones with fresher facts.</summary>
    internal static void OpenOrRefresh(JoinMode mode, bool preferMessage)
    {
        OpenOrRefresh(mode, preferMessage, forceMessage: false);
    }

    private static void OpenOrRefresh(JoinMode mode, bool preferMessage, bool forceMessage, bool modsFirst = false)
    {
        JoinFlowSession? opened = null;
        string rejoinNote = "";
        lock (Gate)
        {
            var model = JoinPageModel.Build(BuildFactsLocked());
            Decide(model, forceMessage, out var message, out var mods, out var changes);
            // JOINFLOW2: the join a reconnect started was refused (or opened a window): its screens take over
            // from the Rejoin page, which ends here.
            if (_session != null && !_session.Closed && _session.Mode == JoinMode.Rejoin && mode != JoinMode.Rejoin)
            {
                rejoinNote = EndReconnectLocked(mode == JoinMode.Refused ? "refused" : "window", model);
                _session = null;
            }

            if (_session != null && !_session.Closed && !_session.IsPreview)
            {
                _session.Refresh(model, preferMessage, message, mods, changes);
            }
            else
            {
                _session = new JoinFlowSession(mode, model, LiveEffects, message, mods, changes);
                if (rejoinNote.Length > 0) _session.Note(rejoinNote);
                Plugin.Logger?.LogInfo("JoinFlow: screens opened (" + JoinFlowSession.ModeName(mode) + ") page="
                    + JoinFlowSession.PageName(_session.Page) + " message=" + message + " mods=" + mods
                    + " required=" + model.Required.Count + " ready=" + model.RequiredReady + " recommended=" + model.Recommended.Count);
                opened = _session;
            }

            // View mods: straight to the mods page, so the message is neither shown nor recorded as seen (review round 3).
            if (modsFirst && _session.Page == JoinPage.Message) _session.ViewMods();
            if (_session.Page == JoinPage.Message) _messageShownThisJoin = true;
        }

        RaiseChanged();
        // JOINFLOW2: the version lists of the packages this join names, read in the background.
        EnsureVersions();
        // Seen state is recorded only for screens that are on show: when they could not be drawn, the deny
        // or consent panel takes over and the player has not seen this message.
        if (opened == null) return;
        bool drawn;
        try { drawn = ScreensDrawn(); }
        catch { drawn = false; }
        if (drawn) opened.Opened();
    }

    /// <summary>Rebuild the model of the open session (a download finished, the prompt changed).</summary>
    internal static void Refresh()
    {
        lock (Gate)
        {
            if (_session == null) return;
            _session.Refresh(JoinPageModel.Build(BuildFactsLocked()), preferMessage: false);
        }

        RaiseChanged();
    }

    /// <summary>
    /// Bridge and tests: open the screens now for the current server, as the in-world message would
    /// (ignores "don't show again"). False when no catalog arrived this join.
    /// </summary>
    internal static bool ShowNow()
    {
        lock (Gate)
        {
            if (_catalog == null && _section == null && _asked.Length == 0) return false;
            if (_session != null && !_session.Closed && !_session.IsPreview) return true;
            // After leaving the server (the main menu) the last join's message is not this session's.
            if (!InWorldNow()) return false;
            _session = null;
            _inWorldDueAt = -1;
        }

        // The player asked: the message page first whatever the server's frequency says.
        OpenOrRefresh(_refused ? JoinMode.Refused : _window ? JoinMode.InstallWindow : JoinMode.InWorld, preferMessage: true, forceMessage: true);
        return true;
    }

    /// <summary>
    /// The mods page now for the joined server (Progenitor's page, <b>View mods</b>): its required and recommended rows
    /// whatever its decision. False when no catalog arrived this join or the client has left. Open screens move to it.
    /// </summary>
    internal static bool ShowModsNow()
    {
        JoinFlowSession? open;
        lock (Gate)
        {
            if (_catalog == null && _section == null && _asked.Length == 0) return false;
            open = _session != null && !_session.Closed && !_session.IsPreview ? _session : null;
            if (open == null)
            {
                if (!InWorldNow()) return false;
                _session = null;
                _inWorldDueAt = -1;
            }
        }

        if (open != null)
        {
            if (open.Page == JoinPage.Message) open.ViewMods();
            RaiseChanged();
            return true;
        }

        OpenOrRefresh(_refused ? JoinMode.Refused : _window ? JoinMode.InstallWindow : JoinMode.InWorld, preferMessage: false, forceMessage: false, modsFirst: true);
        return true;
    }

    /// <summary>Progenitor's page enables View mods: a catalog arrived this join and the client is still in that world.</summary>
    internal static bool CanShowMods
    {
        get
        {
            lock (Gate) if (_catalog == null && _section == null && _asked.Length == 0) return false;
            return InWorldNow();
        }
    }

    /// <summary>
    /// Admin preview: open the message page as a player would see it now, built from the settings as this
    /// client sees them (its synced copy while joined), over the current join's facts.
    /// </summary>
    internal static bool OpenPreview(JoinSection? section)
    {
        lock (Gate)
        {
            if (_session != null && !_session.Closed && !_session.IsPreview)
            {
                Plugin.Logger?.LogInfo("JoinFlow: preview refused: the join screens are open; close them first");
                return false;
            }

            var facts = BuildFactsLocked();
            facts.Section = section ?? _section;
            _session = new JoinFlowSession(JoinMode.InWorld, JoinPageModel.Build(facts), LiveEffects)
            {
                IsPreview = true,
            };
        }

        RaiseChanged();
        return true;
    }

#if PROGENITOR_TESTS
    /// <summary>Tests: put a hand-built session on screen (the trust page, without an install window).</summary>
    internal static void AdoptSessionForTests(JoinFlowSession session)
    {
        lock (Gate) _session = session;
        RaiseChanged();
    }

    /// <summary>Tests: the effects the live session uses (to build a session that closes through them).</summary>
    internal static IJoinFlowEffects EffectsForTests => LiveEffects;
#endif

    private static JoinMode CurrentMode()
    {
        lock (Gate)
            return _session?.Mode ?? (_refused ? JoinMode.Refused : _window ? JoinMode.InstallWindow : JoinMode.InWorld);
    }

    // Called with Gate held.
    private static bool ShouldShowInWorld()
    {
        // The message already shown in this join (its install window) is skipped in Decide, not here, so the
        // mods lists of an install-window join are still decided and recorded (skipped-installed).
        if (_section == null) return false;
        JoinSeenRow? seen = SeenRow();
        string stamp = SafeStamp();
        if (stamp.Length > 0 && seen != null && string.Equals(seen.ShownIn, stamp, StringComparison.Ordinal))
        {
            Plugin.Logger?.LogInfo("JoinFlow: the screens already opened in this world (a Core swap is not a join); not shown");
            return false;
        }

        var model = JoinPageModel.Build(BuildFactsLocked());
        Decide(model, forceMessage: false, out var message, out var mods, out _);
        // Nothing is recorded while the screens are only scheduled: the decision is made again when they open
        // (Tick), and a first visit's "skipped-ready" lists are recorded when the message page is shown
        // (Effects.PageShown) or below when nothing shows at all.
        if (message.Show || mods.Show) return true;
        Plugin.Logger?.LogInfo("JoinFlow: nothing to show on this join (message " + message.Reason + ", mods " + mods.Reason + ")");
        string key = JoinSeenStore.ServerKey(_fp, _host);
        if (key.Length > 0)
        {
            var snapshot = JoinShowPolicy.RecordsListsWhenSkipped(mods.Reason) ? JoinShowPolicy.ModsSnapshot(model) : null;
            JoinSeenStore.Update(SeenPath, key, row =>
            {
                // A first visit with everything ready remembers the lists, so a later change to them is a change.
                if (snapshot != null)
                {
                    row.Mods = snapshot;
                    row.ModsHash = JoinShowPolicy.ModsHash(snapshot);
                }

                // Recorded too, so a Core swap later in this world does not decide again.
                if (stamp.Length > 0) row.ShownIn = stamp;
            }, DateTime.UtcNow);
        }

        return false;
    }

    // Called with Gate held: the page decisions for this join, against what this client last saw.
    private static void Decide(JoinPageModel model, bool forceMessage, out JoinShowDecision message, out JoinShowDecision mods,
        out IReadOnlyDictionary<string, string> changes)
    {
        var seen = SeenRow();
        var now = DateTime.UtcNow;
        bool hasMessage = (_section?.Message.Trim().Length ?? 0) > 0;
        message = forceMessage && hasMessage ? new JoinShowDecision(true, ReasonReopened)
            // A refused join always shows the message: the player is off the server and it is the admins'
            // one chance to say why or where to get what is missing. It still counts as seen.
            : _refused && hasMessage ? new JoinShowDecision(true, JoinShowPolicy.ReasonRefused)
            : JoinShowPolicy.Message(_section, seen, now);
        // Never twice in one join: an install window that showed it counts.
        if (_messageShownThisJoin && !forceMessage && !_refused && !_window && message.Show)
            message = new JoinShowDecision(false, JoinShowPolicy.ReasonShownThisJoin);
        mods = JoinShowPolicy.Mods(model, seen);
        changes = mods.Reason == JoinShowPolicy.ReasonModsChanged
            ? JoinShowPolicy.RowChanges(model, seen?.Mods)
            : new Dictionary<string, string>();
        // After this join's install window, the lists "changed" because the player installed what they
        // lacked: no mods page for that (needs-attention and Always still show), installed rows tagged so.
        if (_windowScreensShown && !_refused && !_window)
        {
            // Only the mods this join installed and now runs count as installed (staged bytes that failed
            // the install's own checks do not).
            var installed = new List<string>();
            foreach (var r in model.Required)
                if (r.Ready && InstalledThisJoin.Contains(r.Id)) installed.Add(r.Id);
            mods = JoinShowPolicy.AfterInstallWindow(mods, changes, installed);
            changes = JoinShowPolicy.MarkInstalled(mods.Show ? changes : null, installed);
        }
    }

    /// <summary>The player reopened the message (the Mods page button, the bridge's <c>show</c>).</summary>
    internal const string ReasonReopened = "reopened";

    private static JoinSeenRow? SeenRow()
    {
        try { return JoinSeenStore.Get(SeenPath, JoinSeenStore.ServerKey(_fp, _host)); }
        catch { return null; }
    }

    private static string SafeStamp()
    {
        try { return WorldStamp() ?? ""; }
        catch { return ""; }
    }

    // Called with Gate held. The two handshake tables are copied while they are valid: after a kick the
    // join's end empties ServerAsked, but the screens on the disconnect screen still describe that join.
    private static void Snapshot()
    {
        // The deny report's own accessors: the handshake tables live, or a test's stand-ins.
        var asked = ModManifestDeny.Asked();
        if (asked.Count > 0) _asked = Copy(asked);
        var provided = ModManifestDeny.Provided();
        if (provided.Count > 0) _provided = Copy(provided);
    }

    private static Handshake.ModRecord[] Copy(IReadOnlyList<Handshake.ModRecord> rows)
    {
        var a = new Handshake.ModRecord[rows.Count];
        for (int i = 0; i < a.Length; i++) a[i] = rows[i];
        return a;
    }

    /// <summary>The joined server's name as this client knows it (set by the panel: the Mods menu's server identity).</summary>
    internal static Func<string?>? ConnectedNameLookup { get; set; }

    // Called with Gate held: the catalog's name unless it is the default, else the name this client knows.
    private static string ServerNameLocked()
    {
        string known;
        try { known = ConnectedNameLookup?.Invoke() ?? ""; }
        catch { known = ""; }
        return JoinText.PickServerName(_serverName, known);
    }

    // CLIENTPOLICY2: this client's mods, the pause and the switch, for the policy rows (only with a policy).
    private static JoinFacts BuildFactsLocked() => WithPolicyFactsLocked(new JoinFacts
    {
        ServerName = ServerNameLocked(),
        Host = _host,
        Asked = _asked,
        Provided = _provided,
        Catalog = _catalog,
        Section = _section,
        Refused = _refused,
        CoreRequirements = _refused ? _coreRefusal : Array.Empty<JoinCoreRequirement>(),
        OwnCoreVersion = OwnCoreVersion(),
        UpdateVersion = _refused && _coreRefusal.Count > 0 ? OfferedProgenitorUpdate() : null,
        InstallWindow = _window,
        NameOf = NameLookup,
        LoadedVersionOf = LoadedVersion,
        // Mod compatibility C3: this client's own ranges, so the rows judge versions as the server's join check did.
        OwnRangeOf = VersionMatching.OwnRangeOf,
        ThunderstoreVersions = _tsVersions,
        // JOINFLOW4: the kept versions (null when the store is not installed: every row keeps its stage 2 action).
        Kept = ModVersionJoin.Service == null ? null : ModVersionJoin.KeptFor,
        // JOINTURNON: the player's turned-off copies (the Mods list's switch state).
        OffCopyOf = OffCopyOfForBuild(),
        // LOCALJOIN1 (debug): the local test index, only while [Install] LocalIndexPath is set.
        LocalTestOf = LocalTestOfForBuild(),
        // Mod compatibility C4: this client's own Progenitor, only when the catalog requires the server's build.
        Progenitor = ProgenitorExact.RequirementOf(_catalog) != null ? global::Progenitor.Install.SelfUpdates.ClientFacts() : null,
    });

    /// <summary>
    /// Mod compatibility C4, the Progenitor row's Use Progenitor X: put the server's build in place for it (at the title screen;
    /// the player's own build comes back after leaving). On a refused join the screens close so the swap can run now.
    /// </summary>
    private static string UseProgenitorLive(JoinRequiredRow row, JoinMode mode, out bool close)
    {
        close = false;
        ProgenitorRequirement? req;
        string server;
        lock (Gate)
        {
            req = ProgenitorExact.RequirementOf(_catalog);
            server = ServerNameLocked();
        }

        if (req == null) return "This server no longer requires a Progenitor build.";
        bool ok = global::Progenitor.Install.SelfUpdates.Place(req, server, out string message);
        Plugin.Logger?.LogInfo("JoinFlow: Use Progenitor " + req.Version + " for " + server + ": " + message);
        close = ok && mode == JoinMode.Refused;
        return message;
    }

    /// <summary>The Progenitor release a click on Update Progenitor would install now, or null (no offer, not clickable, held, running).</summary>
    private static string? OfferedProgenitorUpdate()
    {
        try
        {
            if (global::Progenitor.Install.SelfUpdates.Busy) return null;
            var offer = global::Progenitor.Install.SelfUpdates.Offer;
            if (offer?.Release == null || !offer.Clickable || offer.Held.Length > 0) return null;
            return offer.Release.Version;
        }
        catch { return null; }
    }

    /// <summary>Update Progenitor: the Install tab's Update, started from the join screens (it finishes at the title screen).</summary>
    private static string UpdateProgenitorLive(out bool started)
    {
        var offer = global::Progenitor.Install.SelfUpdates.Offer;
        string version = offer?.Release?.Version ?? "";
        started = global::Progenitor.Install.SelfUpdates.Begin(offer!, "join", byClick: true, out string message);
        Plugin.Logger?.LogInfo("JoinFlow: Update Progenitor " + version + ": " + (started ? "started" : "refused") + " (" + message + ")");
        return started ? JoinText.UpdateStarted(version)
            : "Progenitor couldn't be updated here (" + message + "). Update it from the Mods menu, then join again.";
    }

    private static string OwnCoreVersion()
    {
        string v = global::Progenitor.Ui.FrameworkVersions.Core;
        return v == global::Progenitor.Ui.FrameworkVersions.Unknown ? "" : v;
    }

    private static string? LoadedVersion(string id)
    {
        try
        {
            foreach (var mod in ModManifest.EnumerateLoaded())
                if (string.Equals(mod.Id, id, StringComparison.OrdinalIgnoreCase))
                    return string.IsNullOrEmpty(mod.Version) ? "?" : mod.Version;
        }
        catch { /* unknown is not installed */ }

        return null;
    }

    // ---- readback --------------------------------------------------------------------------

    /// <summary>The bridge's readback of the screens: the page, the rows, the message and the trust facts.</summary>
    internal static Dictionary<string, object?> Describe(DateTime nowUtc, TimeZoneInfo playerZone)
    {
        JoinFlowSession? s;
        JoinSection? section;
        string host, server, fp;
        bool pending, refused, window;
        lock (Gate)
        {
            s = _session;
            section = _section;
            host = _host;
            server = JoinText.ServerLabel(ServerNameLocked(), _host);
            fp = _fp;
            pending = _inWorldDueAt >= 0;
            refused = _refused;
            window = _window;
        }

        var d = new Dictionary<string, object?>
        {
            ["open"] = s != null && !s.Closed,
            ["page"] = s == null ? "none" : JoinFlowSession.PageName(s.Page),
            ["mode"] = s == null ? "" : JoinFlowSession.ModeName(s.Mode),
            ["preview"] = s?.IsPreview ?? false,
            ["server"] = server,
            ["host"] = host,
            ["fingerprint"] = fp,
            ["refused"] = refused,
            ["installWindow"] = window,
            ["inWorldPending"] = pending,
            ["sectionReceived"] = section != null,
        };
        DescribeStage2(d);
        // JOINNOTICE: the mid-session change notice.
        d["changeNotice"] = DescribeChangeNotice();
        // CLIENTPOLICY2: the client mod policy as this client reads it.
        d["policy"] = DescribePolicy(section, host);

        var zone = section?.Zone;
        TimeZoneInfo serverZone;
        try { serverZone = zone?.Resolve() ?? TimeZoneInfo.Utc; }
        catch { serverZone = TimeZoneInfo.Utc; }
        if (section != null)
        {
            string rendered = ResetCountdown.Render(section.Message, section.Reset, nowUtc, serverZone, playerZone, out bool live);
            d["message"] = new Dictionary<string, object?>
            {
                ["raw"] = section.Message,
                ["rendered"] = rendered,
                ["liveCountdown"] = live,
                // Whether the page draws "Times shown in your local time." (a token produced a time).
                ["timesLine"] = JoinText.ShowsTimesLine(section, nowUtc, playerZone),
                ["timesLineText"] = JoinText.MessageTimesLine,
                ["hash"] = JoinShowPolicy.MessageHash(section.Message),
                ["dismissed"] = SafeDismissed(fp, host, section.Message),
                ["frequency"] = section.ShowMessage.ToString(),
            };
            if (section.Reset != null)
            {
                bool set = ResetCountdown.TryNext(section.Reset, nowUtc, serverZone, out var next, out bool passed);
                d["reset"] = new Dictionary<string, object?>
                {
                    ["nextUtc"] = section.Reset.NextUtc,
                    ["effectiveNextUtc"] = set ? ResetCountdown.FormatUtc(next) : "",
                    ["passed"] = passed,
                    ["repeat"] = section.Reset.Repeat.ToString(),
                    ["every"] = section.Reset.Every,
                    // RESETWIPE1: the precision drawn now (automatic); the section's stored one is for earlier clients.
                    ["precision"] = set && !passed ? ResetCountdown.AutoPrecision(next - nowUtc).ToString() : "",
                    ["sentPrecision"] = section.Reset.Precision.ToString(),
                    // Whether the joined server's game schedules wipes, as far as this client knows (an admin holds its rules).
                    ["fromWipeSchedule"] = JoinServerSettings.GameWipe()?.On,
                    ["countdown"] = ResetCountdown.Render(ResetCountdown.TokenCountdown, section.Reset, nowUtc, serverZone, playerZone),
                    ["date"] = ResetCountdown.Render(ResetCountdown.TokenDate, section.Reset, nowUtc, serverZone, playerZone),
                    ["relative"] = ResetCountdown.Render(ResetCountdown.TokenRelative, section.Reset, nowUtc, serverZone, playerZone),
                };
            }

            d["serverZone"] = new Dictionary<string, object?>
            {
                ["id"] = section.Zone.Id,
                ["offsetMinutes"] = section.Zone.OffsetMinutes,
                // The OS zone id names the zone ("Eastern Standard Time" all year on Windows); the label is the
                // name in force now, as the picker shows it.
                ["label"] = ResetCountdown.ZoneLabel(serverZone, nowUtc),
            };
            d["modsFrequency"] = section.ShowMods.ToString();
        }

        if (s == null) return d;
        var model = s.Model;
        d["decision"] = new Dictionary<string, object?>
        {
            ["message"] = new Dictionary<string, object?> { ["show"] = s.MessageDecision.Show, ["reason"] = s.MessageDecision.Reason },
            ["mods"] = new Dictionary<string, object?> { ["show"] = s.ModsDecision.Show, ["reason"] = s.ModsDecision.Reason },
        };
        d["canDismiss"] = s.CanDismiss;
        d["canViewMods"] = s.CanViewMods;
        d["modsChangedLine"] = JoinFlowLayout.ModsChangedText(s);
        d["summary"] = JoinText.Summary(model);
        d["readyCount"] = JoinText.ReadyCount(model);
        d["coreRefused"] = model.CoreRefused;
        d["coreRefusedTitle"] = JoinText.CoreRefusedTitle(model.Facts.CoreRequirements);
        d["coreRefusedBody"] = JoinText.CoreRefusedBody(model.Facts.CoreRequirements, model.Facts.OwnCoreVersion);
        d["coreRefusedDetail"] = JoinText.CoreRefusedDetail(model.Facts.CoreRequirements);
        d["updateVersion"] = model.Facts.UpdateVersion;
        d["canUpdateProgenitor"] = s.OffersUpdateProgenitor;
        d["dontShowAgain"] = s.DontShowAgain;
        d["notice"] = s.Notice;
        // A6: the notice reports a success (drawn in the success tone).
        d["noticeGood"] = s.NoticeGood;
        d["installCount"] = s.InstallTargets.Count;
        // JOINTURNON: the bottom button also turns checked turned-off recommended mods on ("Apply N changes").
        d["turnOnCount"] = s.TurnOnTargets.Count;
        d["applyCount"] = s.BottomCount;
        // Empty when the bottom button is not drawn (nothing to install or turn on).
        d["installCaption"] = s.BottomCaption;
        lock (Gate) d["turnedOnThisJoin"] = DescribeTurnedOnLocked();
        d["footer"] = JoinText.CannotFixFooter(model);
        d["localTestFolder"] = LocalTestFolderNow;
        d["canGoBack"] = s.Page == JoinPage.Mods && s.HasMessage;
        var required = new List<object?>();
        foreach (var r in model.Required)
        {
            var row = new Dictionary<string, object?>
            {
                ["id"] = r.Id,
                ["name"] = r.Name,
                ["origin"] = JoinText.OriginName(r.Origin),
                ["status"] = JoinText.StatusName(r.Status),
                ["needs"] = r.RequiredVersion,
                ["have"] = r.Have,
                ["source"] = JoinText.SourceLine(r),
                ["versionLine"] = JoinText.VersionLine(r),
                ["tags"] = JoinText.TagWords(JoinText.TagsAfter(r.Tags, InstallStateOf(r.Id))),
                ["action"] = JoinText.ActionName(r.Action),
                ["actionCaption"] = JoinText.ActionCaption(r),
                ["change"] = s.Changes.TryGetValue(r.Id, out var rc) ? rc : "",
                ["package"] = r.Package ?? "",
                // LOCALJOIN1: the package is the local test index's (the server named no usable source).
                ["localTest"] = r.LocalTest,
                ["localTestNote"] = r.LocalTestNote,
                ["thunderstore"] = AvailabilityName(r.Availability),
                ["thunderstoreLatest"] = r.ThunderstoreLatest,
                ["install"] = DescribeInstall(InstallStateOf(r.Id)),
                ["kept"] = DescribeKept(r),
                // JOINTURNON: the player's turned-off copy (version, route now|restart), or null.
                ["turnOn"] = DescribeOffCopy(r.OffCopy),
                // C3/C4 wording: the server's match for a mod it runs (exact, minor, at-least, any), or empty.
                ["match"] = r.MatchRule is { } mr ? ClientModRules.Word(mr) : "",
                // C4: the Progenitor row's note (where its build comes from, or why it can't be switched here).
                ["progenitorNote"] = r.ProgenitorNote,
            };
            // CLIENTPOLICY2: kind (server, you-require, required-client), the admins' reason and rule.
            DescribePolicyRow(row, r);
            required.Add(row);
        }

        d["required"] = required;
        // CLIENTPOLICY2: the red band's "Not allowed on this server" rows, and the summary's counts.
        d["notAllowed"] = DescribeNotAllowed(model);
        d["notAllowedUnresolved"] = model.NotAllowedUnresolved;
        d["clientRequired"] = model.ClientRequiredCount;
        var recommended = new List<object?>();
        foreach (var r in model.Recommended)
        {
            recommended.Add(new Dictionary<string, object?>
            {
                ["key"] = JoinPageModel.KeyOf(r),
                ["id"] = r.Id,
                ["name"] = r.Name,
                ["package"] = r.Mod.Package,
                ["version"] = r.Mod.Version,
                ["reason"] = r.Mod.Reason,
                ["installed"] = r.Installed,
                // JOINTURNON: installed but turned off (its switch turns it on), and how.
                ["turnedOff"] = r.TurnedOff,
                ["turnOn"] = DescribeOffCopy(r.OffCopy),
                ["stateWord"] = JoinText.RecommendedWord(r),
                ["checked"] = s.IsChecked(r),
                ["change"] = s.Changes.TryGetValue(JoinPageModel.KeyOf(r), out var cc) ? cc : "",
                ["install"] = DescribeInstall(InstallStateOf(JoinPageModel.KeyOf(r))),
            });
        }

        d["recommended"] = recommended;
        DescribeStage2Session(d, s);
        return d;
    }

    private static bool SafeDismissed(string fp, string host, string message)
    {
        try
        {
            var row = JoinSeenStore.Get(SeenPath, JoinSeenStore.ServerKey(fp, host));
            return row != null && string.Equals(row.Dismissed, JoinShowPolicy.MessageHash(message), StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: redraw threw: " + e.Message); }
    }

    // ---- effects ---------------------------------------------------------------------------

    private sealed class Effects : IJoinFlowEffects
    {
        public bool CopyToClipboard(string text)
        {
            var copy = Clipboard;
            if (copy != null) return copy(text);
            try { return Progenitor.Ui.Ui.TryCopyToClipboard(text); }
            catch { return false; }
        }

        public void Leave()
        {
            Plugin.Logger?.LogInfo("JoinFlow: Leave pressed");
            JoinMode? mode;
            lock (Gate) mode = _session?.Mode;
            // On the refusal screen the client is already disconnected: the screens only close.
            if (mode == JoinMode.Refused) return;
            try { LeaveAction?.Invoke(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: leave threw: " + e.Message); }
        }

        public void Closed(bool completed)
        {
            JoinFlowSession? s;
            lock (Gate)
            {
                s = _session;
                _session = null;
            }

            Plugin.Logger?.LogInfo("JoinFlow: screens closed" + (completed ? " (join complete)" : ""));
            // JOINFLOW2: closing the Rejoin page while it still connects cancels that connect.
            if (s != null && s.Mode == JoinMode.Rejoin) ClosedRejoin();
            RaiseChanged();
            // The screens held the consent prompt while they were open in the world; a package the server
            // offers (not one it requires) is still the consent panel's question.
        }

        public void PageShown(JoinPage page)
        {
            JoinSection? section;
            JoinMode mode;
            string key;
            JoinPageModel? model;
            bool skippedReady;
            lock (Gate)
            {
                section = _section;
                mode = _session?.Mode ?? JoinMode.InWorld;
                model = _session?.Model;
                key = JoinSeenStore.ServerKey(_fp, _host);
                skippedReady = _session != null && JoinShowPolicy.RecordsListsWhenSkipped(_session.ModsDecision.Reason);
            }

            // The install window's screens were on show: the in-world decision after it knows the player saw
            // the server's lists there (JoinShowPolicy.AfterInstallWindow).
            if (mode == JoinMode.InstallWindow)
                lock (Gate) _windowScreensShown = true;
            if (key.Length == 0) return;
            // A refusal and an install window know only a narrowed catalog: their mods lists are not recorded.
            bool fullLists = mode == JoinMode.InWorld;
            if (page == JoinPage.Mods && !fullLists) return;
            if (page == JoinPage.Message && section == null) return;
            string stamp = SafeStamp();
            var now = DateTime.UtcNow;
            JoinSeenStore.Update(SeenPath, key, row =>
            {
                if (page == JoinPage.Message && section != null)
                {
                    row.MessageHash = JoinShowPolicy.MessageHash(section.Message);
                    row.ResetSeen = ModManifestJson.FormatIssued(now);
                }

                // The mods page on show, or a first visit whose mods page was skipped as ready: the lists are
                // what this client has now seen.
                if ((page == JoinPage.Mods || skippedReady) && fullLists && model != null)
                {
                    row.Mods = JoinShowPolicy.ModsSnapshot(model);
                    row.ModsHash = JoinShowPolicy.ModsHash(row.Mods);
                }

                if (mode == JoinMode.InWorld && stamp.Length > 0) row.ShownIn = stamp;
            }, now);
        }

        // ---- JOINFLOW2 ---------------------------------------------------------------------------

        public bool NeedsThunderstoreTrust(IReadOnlyList<JoinInstallRequest> requests) => ThunderstoreTrustNeeded(requests);

        public JoinThunderstoreFacts ThunderstoreFacts(IReadOnlyList<JoinInstallRequest> requests) => ThunderstoreFactsFor(requests);

        public bool ThunderstoreKeyUnchanged(JoinThunderstoreFacts shown) => JoinFlowClient.ThunderstoreKeyUnchanged(shown);

        public string StartThunderstore(IReadOnlyList<JoinInstallRequest> requests, bool acknowledge) => StartBatch(requests, acknowledge);

        public JoinInstallRowState? InstallState(string key) => InstallStateOf(key);

        public string JoinNow() => StartJoinNow();

        public string RestartAndRejoin(bool dryRun) => Relaunch(dryRun);

        public string CancelRejoin() => CancelReconnect();

        public string UseKept(JoinRequiredRow row, out JoinResultOffer? offer) => UseKeptLive(row, out offer);

        public string UseProgenitor(JoinRequiredRow row, JoinMode mode, out bool close) => UseProgenitorLive(row, mode, out close);

        public string UpdateProgenitor(out bool started) => UpdateProgenitorLive(out started);

        public string TurnOn(IReadOnlyList<JoinTurnOnTarget> targets, out JoinResultOffer? offer) => TurnOnLive(targets, out offer);

        public string PauseForServer(IReadOnlyList<JoinNotAllowedRow> rows, out JoinResultOffer? offer) => PauseForServerLive(rows, out offer);

        public string TurnOffForRestart(IReadOnlyList<JoinNotAllowedRow> rows, out JoinResultOffer? offer) => TurnOffForRestartLive(rows, out offer);

        public void MarkSeen()
        {
            JoinSection? section;
            string key;
            lock (Gate)
            {
                section = _section;
                key = JoinSeenStore.ServerKey(_fp, _host);
            }

            if (section == null || key.Length == 0) return;
            string hash = JoinShowPolicy.MessageHash(section.Message);
            if (JoinSeenStore.Update(SeenPath, key, row => { row.Dismissed = hash; row.MessageHash = hash; }, DateTime.UtcNow))
                Plugin.Logger?.LogInfo("JoinFlow: the server message is dismissed until it changes (server " + key + ")");
        }
    }
}
