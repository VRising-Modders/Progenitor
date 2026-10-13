using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Progenitor.Networking;

/// <summary>Which join screen is on show.</summary>
internal enum JoinPage
{
    None = 0,
    Message = 1,
    Mods = 2,

    // 3 was Trust ("Download from this server?"). Removed 2026-10-05: servers no longer send mod files.

    /// <summary>JOINFLOW2: "Install N mods from Thunderstore that &lt;server&gt; lists?"</summary>
    ThunderstoreTrust = 4,

    /// <summary>JOINFLOW2: after a batch — Restart and rejoin / Later, or Join now.</summary>
    Result = 5,

    /// <summary>JOINFLOW2: "Rejoining &lt;server&gt;" (after a relaunch) or "Joining &lt;server&gt;" (Join now).</summary>
    Rejoin = 6,
}

/// <summary>When the screens were opened.</summary>
internal enum JoinMode
{
    /// <summary>The server refused the join (the disconnect screen).</summary>
    Refused = 0,

    /// <summary>The server opened an install window for this join (<c>Ok = 2</c>).</summary>
    InstallWindow = 1,

    /// <summary>The join succeeded; the message shows once in the world.</summary>
    InWorld = 2,

    /// <summary>JOINFLOW2: reconnecting (Join now, or the rejoin after a relaunch); only the Rejoin page.</summary>
    Rejoin = 3,
}

/// <summary>JOINFLOW2: what the Result page offers after a batch.</summary>
internal sealed class JoinResultOffer
{
    public JoinAfterInstall Kind { get; set; }

    /// <summary>"Restart and rejoin" is offered (a server this client can reconnect to, not a private game it hosts).</summary>
    public bool CanRejoin { get; set; }

    /// <summary>The relauncher cannot run (no Steam, no PowerShell): the game closes and the player starts it.</summary>
    public bool ManualRelaunch { get; set; }

    /// <summary>The server is a private game this client hosts.</summary>
    public bool Hosted { get; set; }

    /// <summary>The batch's summary line.</summary>
    public string Summary { get; set; } = "";

    /// <summary>
    /// INPLACE1: the player is still in this server's world, so the fix finished where they are: the handshake was answered
    /// again on this connection and the Result page only confirms it (Done). False: a reconnect or a restart finishes it.
    /// </summary>
    public bool InPlace { get; set; }

    /// <summary>INPLACE1: the server the in-place answer went to, as the screens name it.</summary>
    public string ServerName { get; set; } = "";

    /// <summary>INPLACE1: false when the in-place answer could not be sent on this connection.</summary>
    public bool InPlaceSent { get; set; } = true;

    /// <summary>INPLACE1: the names the in-place sentence carries.</summary>
    public string InPlaceNames { get; set; } = "";

    /// <summary>A6: the summary reports a success (everything loaded): drawn in the success tone.</summary>
    public bool Good { get; set; }

    /// <summary>A6: the join ended (or was never told) after the fix: Join now reads <c>Join again</c>.</summary>
    public bool JoinAgain { get; set; }

    /// <summary>F22: Join now waits for the server to come back (it is restarting), trying again until it answers.</summary>
    public bool WhenBack { get; set; }

    /// <summary>F19/F22 (review M1): the server this page's Join dials (a failed rejoin's, a restarting one's); null: the join's own.</summary>
    public JoinTarget? RetryTarget { get; set; }

    /// <summary>INPLACE1: what was done in place, for the not-sent sentence (<c>installed and loaded</c>, <c>paused for this server</c>).</summary>
    public string InPlaceDone { get; set; } = "installed and loaded";

    /// <summary>INPLACE1 review: what still fails the server's check when the fix did not answer again (empty otherwise).</summary>
    public IReadOnlyList<string> InPlaceProblems { get; set; } = Array.Empty<string>();
}

/// <summary>JOINFLOW2: the Rejoin page's state (the reconnect's progress and, at the end, the summary).</summary>
internal sealed class JoinRejoinView
{
    public string ServerName { get; set; } = "";
    public string Endpoint { get; set; } = "";
    public bool AfterRestart { get; set; }

    /// <summary>INSTALLRESTART1: resuming a private game this client hosts (the title screen's Continue), not dialing a server.</summary>
    public bool Resume { get; set; }

    /// <summary><c>waiting</c>, <c>leaving</c>, <c>connecting</c>, <c>joined</c>, <c>failed</c>, <c>canceled</c>, <c>password</c>.</summary>
    public string Phase { get; set; } = "waiting";

    /// <summary>The progress or result line.</summary>
    public string Status { get; set; } = "";

    /// <summary>0–100, the progress bar's fill.</summary>
    public int Progress { get; set; }

    public RejoinSummary? Summary { get; set; }

    /// <summary>F37: why the server restarted (<c>Why: Save, stop and start again.</c>), the body while a rejoin waits for it.</summary>
    public string Why { get; set; } = "";

    public bool Finished => Phase is "joined" or "failed" or "canceled" or "password" or "prompted";
}

/// <summary>
/// The operator key facts the Thunderstore confirmation shows (the key that signed, when it was first seen, whether it
/// changed for this address). The download trust prompt these were written for is gone (2026-10-05: servers no longer
/// send mod files); the key facts stay because the confirmation names the server that chose the packages.
/// </summary>
internal static class JoinTrustPolicy
{
    /// <summary>The fingerprint pinned for this address under another key, or empty.</summary>
    public static string PreviousKey(TrustedOperatorsFile? file, string host, string spki)
    {
        var row = ModManifestTrust.FindByHost(file, host ?? "");
        if (row == null || string.IsNullOrEmpty(row.Spki) || string.Equals(row.Spki, spki, StringComparison.Ordinal)) return "";
        return row.Fp ?? "";
    }

    /// <summary>When the key pinned for this address under another key was first seen (<c>Oct 4, 2026</c>), or empty.</summary>
    public static string PreviousSeen(TrustedOperatorsFile? file, string host, string spki)
    {
        var row = ModManifestTrust.FindByHost(file, host ?? "");
        if (row == null || string.IsNullOrEmpty(row.Spki) || string.Equals(row.Spki, spki, StringComparison.Ordinal)) return "";
        return ModManifestJson.TryParseIssued(row.Seen, out var seen) ? seen.ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US")) : "";
    }

    /// <summary><c>first seen Oct 4, 2026</c> from the pin, or <c>first time on this computer</c>.</summary>
    public static string FirstSeen(TrustedOperatorsFile? file, string host, string spki, string fp)
    {
        var pin = ModManifestTrust.FindPin(file, host ?? "", spki ?? "", (fp ?? "").ToLowerInvariant());
        if (pin != null && ModManifestJson.TryParseIssued(pin.Seen, out var seen))
            return "first seen " + seen.ToString("MMM d, yyyy", CultureInfo.GetCultureInfo("en-US"));
        return "first time on this computer";
    }
}

/// <summary>The live side of the join screens; tests supply a fake.</summary>
internal interface IJoinFlowEffects
{
    /// <summary>Put <paramref name="text"/> on the system clipboard; false when there is none.</summary>
    bool CopyToClipboard(string text);

    /// <summary>Leave the server (in the world: exit to the main menu).</summary>
    void Leave();

    /// <summary>The screens closed; <paramref name="completed"/> when Continue finished the join.</summary>
    void Closed(bool completed);

    /// <summary>Remember the message as seen for this server (don't show again until it changes).</summary>
    void MarkSeen();

    /// <summary>A page is now on show (the message or the mods page): remember what it showed.</summary>
    void PageShown(JoinPage page);

    // ---- JOINFLOW2 (defaults keep stage 1 fakes compiling) ------------------------------------

    /// <summary>Whether this batch of Thunderstore packages needs the "Install from Thunderstore?" confirmation.</summary>
    bool NeedsThunderstoreTrust(IReadOnlyList<JoinInstallRequest> requests) => true;

    /// <summary>The confirmation's facts (server, key, first seen) for <paramref name="requests"/>.</summary>
    JoinThunderstoreFacts ThunderstoreFacts(IReadOnlyList<JoinInstallRequest> requests) => new() { Requests = requests };

    /// <summary>
    /// Start installing <paramref name="requests"/> from Thunderstore (in the background; closing the screens does
    /// not stop it). <paramref name="acknowledge"/> records the confirmation. Returns the sentence the page shows.
    /// </summary>
    string StartThunderstore(IReadOnlyList<JoinInstallRequest> requests, bool acknowledge) => JoinText.NothingInstalled;

    /// <summary>The server's address and key are still those the confirmation <paramref name="shown"/> named.</summary>
    bool ThunderstoreKeyUnchanged(JoinThunderstoreFacts shown) => true;

    /// <summary>A row's install state in the current batch (by required id or recommended key), or null.</summary>
    JoinInstallRowState? InstallState(string key) => null;

    /// <summary>Join now: reconnect to the same server at once. Returns what happened.</summary>
    string JoinNow() => "not available";

    /// <summary>Restart and rejoin: write the rejoin record, start the relauncher and quit (or not, on a dry run).</summary>
    string RestartAndRejoin(bool dryRun) => "not available";

    /// <summary>Cancel the reconnect on show.</summary>
    string CancelRejoin() => "not available";

    // ---- JOINFLOW4 -----------------------------------------------------------------------------

    /// <summary>
    /// Swap in the kept copy <paramref name="row"/> offers (<see cref="JoinKeptInfo.PickVersion"/>). Returns the
    /// sentence the page shows; <paramref name="offer"/> is what the Result page offers (Join now, Restart and
    /// rejoin), or null when nothing more is needed.
    /// </summary>
    string UseKept(JoinRequiredRow row, out JoinResultOffer? offer)
    {
        offer = null;
        return "Keeping versions is not available here.";
    }

    // ---- Mod compatibility C4 ---------------------------------------------------------------------------

    /// <summary>
    /// Put the server's Progenitor build in place for this server (the Progenitor row's Use Progenitor X): it is swapped in at
    /// the title screen. Returns the sentence the page shows; <paramref name="close"/> asks the screens to close so the swap
    /// can run now (on a refused join; in the world it waits until the player leaves).
    /// </summary>
    string UseProgenitor(JoinRequiredRow row, JoinMode mode, out bool close)
    {
        close = false;
        return "Switching Progenitor is not available here.";
    }

    /// <summary>
    /// Update Progenitor (a Core refusal for an out-of-date Progenitor): start the self-update of the offered release. It
    /// downloads now and is put in place at the title screen. Returns the sentence the page shows.
    /// </summary>
    string UpdateProgenitor(out bool started)
    {
        started = false;
        return "Updating Progenitor is not available here.";
    }

    // ---- JOINTURNON ----------------------------------------------------------------------------

    /// <summary>
    /// Turn <paramref name="targets"/> on through the Mods list's own switch (the same enable state, so the list shows
    /// them on afterwards): a hot-loadable mod loads now, anything else is recorded for the next start. Returns the
    /// sentence the page shows; <paramref name="offer"/> is what the Result page offers (Join now, Restart and rejoin),
    /// or null when nothing more is needed.
    /// </summary>
    string TurnOn(IReadOnlyList<JoinTurnOnTarget> targets, out JoinResultOffer? offer)
    {
        offer = null;
        return JoinText.TurnOnNotAvailable;
    }

    // ---- CLIENTPOLICY2 -------------------------------------------------------------------------

    /// <summary>
    /// <b>Pause for this server</b>: pause <paramref name="rows"/> with the per-server mod pause (unloaded now, back when
    /// the player leaves). <paramref name="offer"/> is what the Result page offers (Join now on a refused join once nothing
    /// else needs attention), or null.
    /// </summary>
    string PauseForServer(IReadOnlyList<JoinNotAllowedRow> rows, out JoinResultOffer? offer)
    {
        offer = null;
        return ClientPolicyView.PauseNotAvailable;
    }

    /// <summary>
    /// <b>Turn off and restart</b>: the Mods list's switch turns <paramref name="rows"/> off for the next start;
    /// <paramref name="offer"/> is Restart and rejoin once nothing else needs attention, or null.
    /// </summary>
    string TurnOffForRestart(IReadOnlyList<JoinNotAllowedRow> rows, out JoinResultOffer? offer)
    {
        offer = null;
        return JoinText.TurnOnNotAvailable;
    }
}

/// <summary>
/// JOINFLOW1: the join screens' state and navigation, game-free. The panel draws whatever page this says
/// and routes every button here; the bridge does the same, so a bridge press is the button's own path.
///
/// <list type="bullet">
/// <item><b>Message</b> (the first screen of a join with a message): Leave; Continue closes when every
/// required mod is ready (completing the join, or just closing in the world) and opens Mods otherwise.</item>
/// <item><b>Mods</b>: row actions, recommended checkboxes, <c>Install N mods</c>, Close, and
/// <c>← Server message</c> back to the message.</item>
/// <item><b>Escape</b> is Back where there is a back, else Close.</item>
/// </list>
/// </summary>
internal sealed class JoinFlowSession
{
    private readonly IJoinFlowEffects _effects;
    private readonly HashSet<string> _checked = new(StringComparer.OrdinalIgnoreCase);

    public JoinFlowSession(JoinMode mode, JoinPageModel model, IJoinFlowEffects effects)
        : this(mode, model, effects, null, null, null)
    {
    }

    /// <summary>
    /// With the server's page decisions (<see cref="JoinShowPolicy"/>): the screens open on the message when
    /// its decision says show, else on the mods page. Without decisions the message shows whenever there is one
    /// and the mods page whenever a required mod needs attention.
    /// </summary>
    public JoinFlowSession(
        JoinMode mode, JoinPageModel model, IJoinFlowEffects effects,
        JoinShowDecision? message, JoinShowDecision? mods, IReadOnlyDictionary<string, string>? changes)
    {
        Mode = mode;
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _effects = effects ?? throw new ArgumentNullException(nameof(effects));
        MessageDecision = message ?? (HasMessage ? new JoinShowDecision(true, JoinShowPolicy.ReasonAlways) : new JoinShowDecision(false, JoinShowPolicy.ReasonNoMessage));
        ModsDecision = mods ?? (model.AllRequiredReady ? new JoinShowDecision(false, JoinShowPolicy.ReasonUnchanged) : new JoinShowDecision(true, JoinShowPolicy.ReasonNeedsAttention));
        Changes = changes ?? new Dictionary<string, string>();
        Page = mode == JoinMode.Rejoin ? JoinPage.Rejoin : HasMessage && MessageDecision.Show ? JoinPage.Message : JoinPage.Mods;
    }

    // ---- JOINFLOW2 state ---------------------------------------------------------------------

    /// <summary>The Thunderstore confirmation on show, or null.</summary>
    public JoinThunderstoreFacts? TsTrust { get; private set; }

    /// <summary>The Result page's offer, or null.</summary>
    public JoinResultOffer? Result { get; private set; }

    /// <summary>The Rejoin page's state (set by the live side), or null.</summary>
    public JoinRejoinView? RejoinView { get; set; }

    // JOINTURNON: turned-off mods the same press turns on, applied once the confirmation's Install is pressed.
    private IReadOnlyList<JoinTurnOnTarget> _pendingTurnOns = Array.Empty<JoinTurnOnTarget>();

    /// <summary>
    /// A row a batch is handling or handled: queued, running, loaded, waiting for the restart, or refused (a
    /// refusal is a rule, so the row is not offered again this join). A failed row is offered again.
    /// </summary>
    private bool Settled(string key)
    {
        var s = _effects.InstallState(key);
        return s != null && (!s.Terminal || s.Succeeded || s.Phase == JoinInstallPhase.Refused);
    }

    /// <summary>A row a batch is still working on.</summary>
    private bool InFlight(string key)
    {
        var s = _effects.InstallState(key);
        return s != null && !s.Terminal;
    }

    /// <summary>A required row's Thunderstore request.</summary>
    internal static JoinInstallRequest RequestFor(JoinRequiredRow row) => new(
        row.Id, row.Name, row.Package ?? "", row.RequiredVersion,
        row.Action switch
        {
            JoinRowAction.Update => JoinInstallKind.Update,
            JoinRowAction.UseVersion => JoinInstallKind.UseVersion,
            _ => JoinInstallKind.Install,
        },
        required: true,
        replaces: row.Action == JoinRowAction.UseVersion ? row.Have : null,
        // CLIENTPOLICY2: a required client mod named by its package alone has no plugin id to check.
        modId: row.ModId,
        versionRule: row.VersionRule)
    {
        // JOINFLOW4: the newer copy is kept unless the store says it cannot be (another tool's, a plugins copy).
        // No store facts (no client Core store): nothing is kept, so the stage 2 replacing sentence stands (review round 3).
        Keeps = row.Action != JoinRowAction.UseVersion || (row.Kept != null && row.Kept.CanKeepActive),
        KeepsNow = row.Kept == null || row.Kept.KeepNow,
        KeepReason = row.Action == JoinRowAction.UseVersion && row.Kept != null && !row.Kept.CanKeepActive ? row.Kept.KeepReason : "",
        LocalTest = row.LocalTest,
    };

    /// <summary>A checked recommended row's Thunderstore request.</summary>
    internal static JoinInstallRequest RequestFor(JoinRecommendedRow row) => new(
        JoinPageModel.KeyOf(row), row.Name, row.Mod.Package, row.Mod.Version, JoinInstallKind.Recommended, required: false, modId: row.Id);

    /// <summary>Whether the message page showed on this join, and why.</summary>
    public JoinShowDecision MessageDecision { get; private set; }

    /// <summary>Whether the mods page showed on this join, and why.</summary>
    public JoinShowDecision ModsDecision { get; private set; }

    /// <summary>Rows that are <c>new</c> or <c>changed</c> since the mods page was last shown (by row id or recommended key).</summary>
    public IReadOnlyDictionary<string, string> Changes { get; private set; }

    /// <summary>The page the screens opened on; the live side reports it as shown.</summary>
    public void Opened()
    {
        _opened = true;
        if (!IsPreview && Page is JoinPage.Message or JoinPage.Mods) _effects.PageShown(Page);
    }

    // Set once the screens are on show (Opened): pages are recorded as seen from then on.
    private bool _opened;

    /// <summary>"Don't show again until it changes" is offered: not in Always mode, not in a preview.</summary>
    public bool CanDismiss => !IsPreview && (Model.Facts.Section?.ShowMessage ?? MessageFrequency.WhenChanged) != MessageFrequency.Always;

    private void Go(JoinPage page)
    {
        Page = page;
        if (_opened && !IsPreview && page is JoinPage.Message or JoinPage.Mods) _effects.PageShown(page);
    }

    public JoinMode Mode { get; }

    public JoinPageModel Model { get; private set; }

    public JoinPage Page { get; private set; }

    public bool HasMessage => (Model.Facts.Section?.Message ?? "").Trim().Length > 0;

    /// <summary>The "Don't show again until it changes" box.</summary>
    public bool DontShowAgain { get; set; }

    /// <summary>The Thunderstore confirmation's inline note (the server's key or address changed while it was open), or empty.</summary>
    public string TrustError { get; private set; } = "";

    /// <summary>The last answer the Mods page shows inline (a copy, a later-update note, an install), or empty.</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            _notice = value ?? "";
            NoticeGood = false;
        }
    }

    private string _notice = "";

    /// <summary>A6: the Notice reports a success (drawn in the success tone, not the warning one).</summary>
    public bool NoticeGood { get; private set; }

    public bool Closed => Page == JoinPage.None;

    public IReadOnlyCollection<string> CheckedRecommended => _checked;

    /// <summary>An admin's preview: Leave only closes, and nothing is remembered as seen.</summary>
    public bool IsPreview { get; set; }

    /// <summary>True once the player pressed anything; fresher facts then never move the page under them.</summary>
    public bool Interacted { get; private set; }

    /// <summary>Update Progenitor was pressed and its download started this join: the button is not offered again.</summary>
    public bool UpdateStarted { get; private set; }

    /// <summary>The pages offer Update Progenitor: a Core refusal the self-update can fix, not already started.</summary>
    public bool OffersUpdateProgenitor => !IsPreview && !UpdateStarted && Model.CanUpdateProgenitor;

    /// <summary>Update Progenitor: start the self-update. Returns what happened, for the readback.</summary>
    public string UpdateProgenitor()
    {
        Interacted = true;
        if (!OffersUpdateProgenitor) return "not offered";
        bool started;
        string notice;
        try { notice = _effects.UpdateProgenitor(out started); }
        catch (Exception e)
        {
            notice = "Progenitor couldn't be updated here: " + e.Message;
            started = false;
        }

        UpdateStarted = started;
        Note(notice ?? "", good: started);
        return started ? "update-progenitor" : "update-progenitor (not started)";
    }

    /// <summary>Rebuild from fresher facts (the signed descriptor landed, a download finished) keeping the page and choices.</summary>
    public void Refresh(JoinPageModel model) => Refresh(model, preferMessage: false);

    /// <summary>
    /// Same; with <paramref name="preferMessage"/>, a message that arrived after the screens opened on the
    /// mods page (the refusal opens at once, the signed message lands a frame later) moves the page to the
    /// message — but only while the player has not pressed anything yet.
    /// </summary>
    public void Refresh(JoinPageModel model, bool preferMessage) => Refresh(model, preferMessage, null, null, null);

    /// <summary>Same, with fresher page decisions (a message that arrived later decides for itself).</summary>
    public void Refresh(
        JoinPageModel model, bool preferMessage, JoinShowDecision? message, JoinShowDecision? mods, IReadOnlyDictionary<string, string>? changes)
    {
        Model = model ?? Model;
        // The decisions are made when the screens open and kept: re-deciding against the seen state the open
        // pages just wrote would turn a shown page into "skipped-unchanged" and drop its New/Changed tags.
        // Only a message that arrived after the screens opened decides for itself.
        if (MessageDecision.Reason == JoinShowPolicy.ReasonNoMessage && HasMessage)
            MessageDecision = message ?? new JoinShowDecision(true, JoinShowPolicy.ReasonAlways);
        if (!_opened)
        {
            if (mods != null) ModsDecision = mods.Value;
            if (changes != null) Changes = changes;
        }

        var before = Page;
        if (Page == JoinPage.Message && !HasMessage) Go(JoinPage.Mods);
        else if (preferMessage && !Interacted && Page == JoinPage.Mods && HasMessage && MessageDecision.Show) Go(JoinPage.Message);
        // A page on show whose facts changed (a newer catalog) is what the player has now seen.
        if (_opened && Page == before && !IsPreview && Page is JoinPage.Message or JoinPage.Mods) _effects.PageShown(Page);
    }

    /// <summary>Set the inline answer the Mods page shows (a finished download).</summary>
    public void Note(string text) => Notice = text ?? "";

    /// <summary>A6: the same, marked as a success when <paramref name="good"/>.</summary>
    public void Note(string text, bool good)
    {
        Notice = text ?? "";
        NoticeGood = good;
    }

    /// <summary>The Install button's count and targets (rows a batch is handling or handled are left out).</summary>
    public IReadOnlyList<string> InstallTargets => Model.InstallTargets(_checked, Settled);

    /// <summary>JOINTURNON: the checked recommended rows the bottom button turns on (installed, turned off by the player).</summary>
    public IReadOnlyList<JoinRecommendedRow> TurnOnTargets => Model.TurnOnTargets(_checked);

    /// <summary>JOINTURNON: what the bottom button does in all: downloads plus turn-ons (0: no button).</summary>
    public int BottomCount => InstallTargets.Count + TurnOnTargets.Count;

    /// <summary>JOINTURNON: <c>Install N mods</c>, or <c>Apply N changes</c> once a checked row turns a mod on; empty with no button.</summary>
    public string BottomCaption => JoinText.BottomButton(InstallTargets.Count, TurnOnTargets.Count);

    public string Continue()
    {
        Interacted = true;
        if (Page != JoinPage.Message) return "not on the message page";
        // The mods page follows when a required mod needs attention, or when the server's mod lists say it
        // shows on this join (always, a first visit, a change). Otherwise Continue finishes.
        if (Model.AllRequiredReady && !(Mode == JoinMode.InWorld && ModsDecision.Show))
        {
            // Close remembers a ticked "don't show again".
            Close(completed: true);
            return "closed";
        }

        if (DontShowAgain && CanDismiss) _effects.MarkSeen();
        Go(JoinPage.Mods);
        return "mods";
    }

    /// <summary>Continue on the message page goes on to the mods page (a required mod needs attention, or the page is due).</summary>
    public bool ModsPageDue => !Model.AllRequiredReady || (Mode == JoinMode.InWorld && ModsDecision.Show);

    /// <summary>
    /// Stage 2 live run: with everything ready and nothing changed the mods page is skipped and Continue closes, so the
    /// recommended mods were out of reach. The message page then offers a quiet <b>View mods</b>.
    /// </summary>
    public bool CanViewMods => Page == JoinPage.Message && !ModsPageDue;

    /// <summary><b>View mods</b> on the message page: the mods page, whatever its decision.</summary>
    public string ViewMods()
    {
        Interacted = true;
        if (Page != JoinPage.Message) return "not on the message page";
        if (DontShowAgain && CanDismiss) _effects.MarkSeen();
        Go(JoinPage.Mods);
        return "mods";
    }

    public string BackToMessage()
    {
        Interacted = true;
        if (Page != JoinPage.Mods) return "not on the mods page";
        if (!HasMessage) return "no message";
        Go(JoinPage.Message);
        return "message";
    }

    public string Leave()
    {
        Interacted = true;
        if (IsPreview) return Close(completed: false);
        if (Page == JoinPage.None) return "closed";
        if (DontShowAgain && CanDismiss) _effects.MarkSeen();
        Page = JoinPage.None;
        TsTrust = null;
        _effects.Leave();
        _effects.Closed(false);
        return "left";
    }

    public string Close()
    {
        Interacted = true;
        return Close(completed: false);
    }

    private string Close(bool completed)
    {
        if (Page == JoinPage.None) return "closed";
        if (DontShowAgain && CanDismiss && Page == JoinPage.Message) _effects.MarkSeen();
        Page = JoinPage.None;
        TsTrust = null;
        _effects.Closed(completed);
        return "closed";
    }

    /// <summary>Escape: Back where there is one, else Close (Later on the Result page, Cancel while reconnecting).</summary>
    public string Escape()
    {
        Interacted = true;
        switch (Page)
        {
            case JoinPage.ThunderstoreTrust: return TsTrustBack();
            case JoinPage.Result: return ResultLater();
            case JoinPage.Rejoin: return RejoinCancel();
            case JoinPage.Mods: return HasMessage ? BackToMessage() : Close();
            case JoinPage.Message: return Close();
            default: return "closed";
        }
    }

    public bool ToggleRecommended(string key)
    {
        Interacted = true;
        var row = Model.FindRecommended(key);
        if (row == null || row.Installed) return false;
        string k = JoinPageModel.KeyOf(row);
        if (_effects.InstallState(k)?.Phase == JoinInstallPhase.Refused)
        {
            _checked.Remove(k);
            return false;
        }

        if (!_checked.Remove(k)) _checked.Add(k);
        return true;
    }

    public bool IsChecked(JoinRecommendedRow row) =>
        row != null && _checked.Contains(JoinPageModel.KeyOf(row))
        && _effects.InstallState(JoinPageModel.KeyOf(row))?.Phase != JoinInstallPhase.Refused;

    /// <summary>A row's action button. Returns what happened, for the readback.</summary>
    public string RowAction(string id)
    {
        Interacted = true;
        if (Page != JoinPage.Mods) return "not on the mods page";
        var row = Model.FindRequired(id);
        if (row == null) return "no required row " + id;
        switch (row.Action)
        {
            case JoinRowAction.Ready:
                Notice = "";
                return "ready";
            case JoinRowAction.Install:
            case JoinRowAction.Update:
            case JoinRowAction.UseVersion:
                if (Settled(row.Id))
                {
                    Notice = InFlight(row.Id) ? JoinText.BatchBusy : JoinText.InstallPhaseWord(_effects.InstallState(row.Id)!) + ".";
                    return InFlight(row.Id) ? "busy" : "done";
                }

                return Thunderstore(new[] { RequestFor(row) }, Array.Empty<JoinTurnOnTarget>());
            case JoinRowAction.UseKept:
            {
                // JOINFLOW4: swap the kept copy in (now, or queued for the next start); the Result page offers
                // Join now or Restart and rejoin where the join needs one.
                string notice;
                JoinResultOffer? offer;
                try { notice = _effects.UseKept(row, out offer); }
                catch (Exception e)
                {
                    notice = "Can't use the kept copy: " + e.Message;
                    offer = null;
                }

                Notice = notice;
                if (offer != null && !IsPreview) ShowResult(offer);
                return "use-kept";
            }
            case JoinRowAction.AskAdmin:
                if (row.VersionGone)
                {
                    // JOINFLOW2: say plainly that the exact version is gone; the admins have to update.
                    Notice = JoinText.VersionGoneAnswer(row);
                    return "version-gone";
                }

                return Copy(JoinText.AskAdminMessage(row, Model.ServerName));
            case JoinRowAction.TellAdmin:
                return Copy(JoinText.TellAdminMessage(row, Model.ServerName));
            case JoinRowAction.TurnOff:
                Notice = JoinText.TurnOffAnswer(row);
                return "turn-off";
            case JoinRowAction.TurnOn:
                // JOINTURNON: the Mods list's own switch, no download; the Result page offers Join now or Restart and
                // rejoin where the join needs one (as Use kept does).
                Notice = ApplyTurnOns(new[] { JoinTurnOnTarget.From(row) }, showResult: true);
                return "turn-on";
            case JoinRowAction.UseProgenitor:
            {
                // Mod compatibility C4: the server's Progenitor build, put in place at the title screen. On a refused join
                // the screens close so the swap can run; the title screen says when it is ready to join again.
                bool close;
                string notice;
                try { notice = _effects.UseProgenitor(row, Mode, out close); }
                catch (Exception e)
                {
                    notice = "Can't use Progenitor " + row.RequiredVersion + ": " + e.Message;
                    close = false;
                }

                Notice = notice ?? "";
                if (close && !IsPreview)
                {
                    Close();
                    return "use-progenitor (closed)";
                }

                return "use-progenitor";
            }
            default:
                return "none";
        }
    }

    /// <summary>
    /// CLIENTPOLICY2: a "Not allowed on this server" row's action: <b>Pause for this server</b> (the per-server pause) or
    /// <b>Turn off and restart</b> (the Mods list's switch for the next start, then Restart and rejoin). Returns what
    /// happened, for the readback.
    /// </summary>
    public string NotAllowedAction(string id)
    {
        Interacted = true;
        if (Page != JoinPage.Mods) return "not on the mods page";
        var row = Model.FindNotAllowed(id);
        if (row == null) return "no not-allowed row " + id;
        string notice;
        JoinResultOffer? offer;
        switch (row.Action)
        {
            case JoinNotAllowedAction.Pause:
                try { notice = _effects.PauseForServer(new[] { row }, out offer); }
                catch (Exception e)
                {
                    notice = ClientPolicyView.PauseFailed(row.Name, e.Message);
                    offer = null;
                }

                break;
            case JoinNotAllowedAction.TurnOffRestart:
                try { notice = _effects.TurnOffForRestart(new[] { row }, out offer); }
                catch (Exception e)
                {
                    notice = ClientPolicyView.TurnOffFailed(row.Name, e.Message);
                    offer = null;
                }

                break;
            default:
                Notice = "";
                return "done";
        }

        Notice = notice ?? "";
        if (offer != null && !IsPreview) ShowResult(offer);
        return ClientPolicyView.ActionName(row.Action);
    }

    /// <summary>
    /// JOINTURNON: turn <paramref name="targets"/> on through the effects. With <paramref name="showResult"/> the Result
    /// page follows when the effects offer one; a press that also starts installs leaves that to the batch's result.
    /// </summary>
    private string ApplyTurnOns(IReadOnlyList<JoinTurnOnTarget> targets, bool showResult)
    {
        if (targets == null || targets.Count == 0) return "";
        string notice;
        JoinResultOffer? offer;
        try { notice = _effects.TurnOn(targets, out offer); }
        catch (Exception e)
        {
            notice = JoinText.TurnOnFailed(targets.Count == 1 ? targets[0].Name : "these mods", e.Message);
            offer = null;
        }

        if (showResult && offer != null && !IsPreview)
        {
            ShowResult(offer);
            return Notice;
        }

        return notice ?? "";
    }

    private static string Join(string a, string b) =>
        a.Length == 0 ? b : b.Length == 0 ? a : a + " " + b;

    private static List<JoinTurnOnTarget> TargetsOf(IReadOnlyList<JoinRecommendedRow> rows)
    {
        var list = new List<JoinTurnOnTarget>(rows.Count);
        foreach (var r in rows) list.Add(JoinTurnOnTarget.From(r));
        return list;
    }

    /// <summary>
    /// <c>Install N mods</c>: every Thunderstore row (required Install / Update / Use version, and checked
    /// recommended rows) behind one confirmation, installed in the background.
    /// </summary>
    public string InstallSelected()
    {
        Interacted = true;
        if (Page != JoinPage.Mods) return "not on the mods page";
        var targets = InstallTargets;
        // JOINTURNON: checked recommended rows whose mod is installed but turned off are turned on, not downloaded. Alone
        // they need no confirmation; with installs they ride on the installs' one confirmation ("Apply N changes").
        var turnOns = TargetsOf(TurnOnTargets);
        if (targets.Count == 0 && turnOns.Count > 0)
        {
            Notice = ApplyTurnOns(turnOns, showResult: true);
            return "turn-on";
        }

        if (targets.Count == 0)
        {
            Notice = JoinText.NothingToInstall;
            return "nothing";
        }

        var thunderstore = new List<JoinInstallRequest>();
        foreach (string id in targets)
        {
            var row = Model.FindRequired(id);
            if (row != null)
            {
                if (row.Thunderstore && !string.IsNullOrEmpty(row.Package)) thunderstore.Add(RequestFor(row));
                continue;
            }

            var rec = Model.FindRecommended(id);
            if (rec != null && rec.Mod.Package.Length > 0) thunderstore.Add(RequestFor(rec));
        }

        if (thunderstore.Count > 0) return Thunderstore(thunderstore, turnOns);
        string turned = ApplyTurnOns(turnOns, showResult: true);
        if (turnOns.Count > 0)
        {
            if (Page == JoinPage.Mods) Notice = turned;
            return "turn-on";
        }

        Notice = JoinText.NothingToInstall;
        return "nothing";
    }

    /// <summary>
    /// JOINFLOW2: Thunderstore installs a server named. One confirmation per server per batch (unless this server's
    /// key already confirmed every package), then the batch starts in the background. JOINTURNON: the turn-ons the
    /// same press asked for are listed on the same confirmation.
    /// </summary>
    private string Thunderstore(IReadOnlyList<JoinInstallRequest> requests, IReadOnlyList<JoinTurnOnTarget> turnOns)
    {
        foreach (var r in requests)
            if (InFlight(r.Key))
            {
                Notice = JoinText.BatchBusy;
                return "busy";
            }

        bool ask;
        try { ask = _effects.NeedsThunderstoreTrust(requests); }
        catch { ask = true; }
        if (ask)
        {
            JoinThunderstoreFacts facts;
            try { facts = _effects.ThunderstoreFacts(requests) ?? new JoinThunderstoreFacts(); }
            catch { facts = new JoinThunderstoreFacts(); }
            facts.Requests = requests;
            facts.TurnOns = turnOns ?? Array.Empty<JoinTurnOnTarget>();
            if (facts.ServerName.Length == 0) facts.ServerName = Model.ServerName;
            TsTrust = facts;
            TrustError = "";
            _pendingTurnOns = facts.TurnOns;
            Page = JoinPage.ThunderstoreTrust;
            return "ts-trust";
        }

        // JOINTURNON: the turn-ons first (the batch's result decides the Result page for the whole press).
        string turned = ApplyTurnOns(turnOns ?? Array.Empty<JoinTurnOnTarget>(), showResult: false);
        Notice = Join(turned, _effects.StartThunderstore(requests, acknowledge: false));
        return "install";
    }

    /// <summary>JOINFLOW2: the confirmation's Install: record it and start the batch.</summary>
    public string TsTrustInstall()
    {
        Interacted = true;
        if (Page != JoinPage.ThunderstoreTrust || TsTrust == null) return "not on the Thunderstore confirmation";
        // A catalog signed by another key (or for another address) landed while this was open: the player
        // confirms the server they are shown, so the prompt is shown again with the current facts.
        bool same;
        try { same = _effects.ThunderstoreKeyUnchanged(TsTrust); }
        catch { same = false; }
        if (!same)
        {
            var requestsShown = TsTrust.Requests;
            JoinThunderstoreFacts fresh;
            try { fresh = _effects.ThunderstoreFacts(requestsShown) ?? new JoinThunderstoreFacts(); }
            catch { fresh = new JoinThunderstoreFacts(); }
            fresh.Requests = requestsShown;
            fresh.TurnOns = TsTrust.TurnOns;
            if (fresh.ServerName.Length == 0) fresh.ServerName = Model.ServerName;
            TsTrust = fresh;
            TrustError = JoinText.TrustOfferChanged;
            return "changed";
        }

        TrustError = "";
        var requests = TsTrust.Requests;
        var turnOns = _pendingTurnOns;
        TsTrust = null;
        _pendingTurnOns = Array.Empty<JoinTurnOnTarget>();
        Go(JoinPage.Mods);
        // JOINTURNON: the turn-ons the confirmation listed, then the batch.
        string turned = ApplyTurnOns(turnOns, showResult: false);
        Notice = Join(turned, _effects.StartThunderstore(requests, acknowledge: true));
        return "install";
    }

    /// <summary>JOINFLOW2: the confirmation's Back (and Escape): nothing is installed (JOINTURNON: and nothing turned on).</summary>
    public string TsTrustBack()
    {
        Interacted = true;
        if (Page != JoinPage.ThunderstoreTrust) return "not on the Thunderstore confirmation";
        TsTrust = null;
        _pendingTurnOns = Array.Empty<JoinTurnOnTarget>();
        Go(JoinPage.Mods);
        Notice = JoinText.NothingInstalled;
        return "declined";
    }

    /// <summary>JOINFLOW2: a batch ended; the Result page offers Join now or Restart and rejoin (or the page notes it).</summary>
    public string ShowResult(JoinResultOffer offer)
    {
        if (offer == null || Closed || IsPreview) return "closed";
        if (offer.InPlace)
        {
            // INPLACE1: the fix finished in the world; the Result page confirms it (Done) and nothing reconnects.
            if (Page is JoinPage.ThunderstoreTrust or JoinPage.Message)
            {
                Notice = offer.Summary;
                return "noted";
            }

            Notice = "";
            Result = offer;
            Page = JoinPage.Result;
            return "in-place";
        }

        Notice = offer.Summary;
        NoticeGood = offer.Good;
        if (offer.Kind is JoinAfterInstall.JoinNow or JoinAfterInstall.RestartNeeded)
        {
            // The trust prompts and the message are never pushed aside by a batch that ended.
            if (Page is JoinPage.ThunderstoreTrust or JoinPage.Message) return "noted";
            Result = offer;
            Page = JoinPage.Result;
            return "result";
        }

        return "noted";
    }

    /// <summary>JOINFLOW2: Join now on the Result page.</summary>
    public string ResultJoinNow()
    {
        Interacted = true;
        if (Page != JoinPage.Result || Result == null) return "not on the result page";
        if (Result.Kind != JoinAfterInstall.JoinNow) return "join now is not offered";
        return _effects.JoinNow();
    }

    /// <summary>JOINFLOW2: Restart and rejoin on the Result page (<paramref name="dryRun"/>: write the record, do not quit).</summary>
    public string ResultRestart(bool dryRun)
    {
        Interacted = true;
        if (Page != JoinPage.Result || Result == null) return "not on the result page";
        if (Result.Kind != JoinAfterInstall.RestartNeeded) return "restart is not offered";
        return _effects.RestartAndRejoin(dryRun);
    }

    /// <summary>JOINFLOW2: Later on the Result page: the screens close; what was installed stays.</summary>
    public string ResultLater()
    {
        Interacted = true;
        if (Page != JoinPage.Result) return "not on the result page";
        Result = null;
        return Close(completed: false);
    }

    /// <summary>JOINFLOW2: Cancel (or Close, once it ended) on the Rejoin page.</summary>
    public string RejoinCancel()
    {
        Interacted = true;
        if (Page != JoinPage.Rejoin) return "not on the rejoin page";
        if (RejoinView != null && !RejoinView.Finished)
        {
            string r = _effects.CancelRejoin();
            if (RejoinView != null && !RejoinView.Finished) return r;
        }

        return Close(completed: RejoinView?.Phase == "joined");
    }

    private string Copy(string text)
    {
        bool ok;
        try { ok = _effects.CopyToClipboard(text); }
        catch { ok = false; }
        Notice = ok ? JoinText.CopiedForAdmins : JoinText.ClipboardUnavailable + text;
        return ok ? "copied" : "shown";
    }

    public static string PageName(JoinPage page) => page switch
    {
        JoinPage.Message => "message",
        JoinPage.Mods => "mods",
        JoinPage.ThunderstoreTrust => "ts-trust",
        JoinPage.Result => "result",
        JoinPage.Rejoin => "rejoin",
        _ => "none",
    };

    public static string ModeName(JoinMode mode) => mode switch
    {
        JoinMode.InstallWindow => "install-window",
        JoinMode.InWorld => "in-world",
        JoinMode.Rejoin => "rejoin",
        _ => "refused",
    };
}

