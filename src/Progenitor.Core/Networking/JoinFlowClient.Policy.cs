using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Networking;

/// <summary>CLIENTPOLICY2: what <c>ServerModPause.PauseForPolicy</c> did for the join screens (or the bridge).</summary>
internal sealed class JoinPauseResult
{
    public bool Ok { get; init; }

    /// <summary>The plugins now paused (the closure: DLL siblings and dependents go too).</summary>
    public IReadOnlyList<string> Taken { get; init; } = Array.Empty<string>();

    /// <summary>Their readable names.</summary>
    public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();

    /// <summary>Why nothing was paused, or empty.</summary>
    public string Message { get; init; } = "";

    public static JoinPauseResult Fail(string message) => new() { Ok = false, Message = message ?? "" };
}

/// <summary>
/// CLIENTPOLICY2 (docs/client-mod-policy.md, "The player's join screens"): the join screens' part of the client mod
/// policy — this client's mod list for the required client rows and the "Not allowed on this server" rows, the
/// per-server pause behind <b>Pause for this server</b>, the Mods list's switch behind <b>Turn off and restart</b>, and the
/// policy remembered per server for the next join's automatic pause. The live side sets the probes
/// (<see cref="JoinFlowLive"/>); tests pin them.
/// </summary>
internal static partial class JoinFlowClient
{
    /// <summary>This client's mods as its report lists them (live: <c>ClientModReportClient.CurrentEntries</c>).</summary>
    internal static Func<IReadOnlyList<ClientModEntry>>? ClientModsProbe { get; set; }

    /// <summary>The mods the per-server pause holds now (live: Bootstrap's pause table).</summary>
    internal static Func<IReadOnlyList<ClientModEntry>>? PausedHereProbe { get; set; }

    /// <summary>The plugins Bootstrap's reloader can pause now (live: its suspendable GUIDs).</summary>
    internal static Func<IReadOnlyCollection<string>>? PausableProbe { get; set; }

    /// <summary>A mod the player switched off that goes at the next start (live: the Mods list's switch state).</summary>
    internal static Func<string, bool>? TurnsOffAtRestartProbe { get; set; }

    /// <summary>Pause these plugins for the joined server (live: <c>ServerModPause.PauseForPolicy</c>).</summary>
    internal static Func<IReadOnlyList<string>, JoinPauseResult>? PolicyPauseProbe { get; set; }

    // The inventory read is not free (it walks the loaded assemblies): one read serves the model builds of a second.
    private static IReadOnlyList<ClientModEntry>? _clientModsCache;
    private static double _clientModsAt = double.NegativeInfinity;
    internal const double ClientModsCacheSeconds = 1.0;

    // Called with Gate held, from BuildFactsLocked: the policy facts, only when this join's section carries a policy.
    private static JoinFacts WithPolicyFactsLocked(JoinFacts facts)
    {
        if (_section == null || !_section.HasClientPolicy) return facts;
        facts.ClientMods = ClientModsNow();
        try { facts.PausedHere = PausedHereProbe?.Invoke(); }
        catch { facts.PausedHere = null; }
        IReadOnlyCollection<string>? pausable = null;
        try { pausable = PausableProbe?.Invoke(); }
        catch { pausable = null; }
        if (pausable != null)
        {
            var set = new HashSet<string>(pausable, StringComparer.OrdinalIgnoreCase);
            facts.Pausable = id => set.Contains(id);
        }

        facts.TurnsOffAtRestart = TurnsOffAtRestartProbe;
        return facts;
    }

    private static IReadOnlyList<ClientModEntry>? ClientModsNow()
    {
        var probe = ClientModsProbe;
        if (probe == null) return null;
        double now = Clock();
        if (_clientModsCache != null && now - _clientModsAt < ClientModsCacheSeconds) return _clientModsCache;
        try { _clientModsCache = probe(); }
        catch { _clientModsCache = null; }
        _clientModsAt = now;
        return _clientModsCache;
    }

    // A pause or a switch changed what runs: the next build reads the inventory again.
    private static void ForgetClientModsLocked()
    {
        _clientModsCache = null;
        _clientModsAt = double.NegativeInfinity;
    }

    /// <summary>
    /// A verified section for <paramref name="host"/>: remember its client mod policy (or forget one it no longer carries) in
    /// the server's profile, so the next join's automatic pause knows it before the report.
    /// </summary>
    private static void RememberPolicy(string host, JoinSection? section)
    {
        if (section == null || string.IsNullOrWhiteSpace(host)) return;
        try { ServerProfiles.NotePolicy(host, ClientPolicyView.Remembered(section), DateTime.UtcNow); }
        catch (Exception e) { Plugin.Logger?.LogWarning("ClientPolicy: remembering this server's policy threw: " + e.Message); }
    }

    /// <summary>The effects' Pause for this server: the per-server pause, then what the join needs next.</summary>
    private static string PauseForServerLive(IReadOnlyList<JoinNotAllowedRow> rows, out JoinResultOffer? offer)
    {
        offer = null;
        var probe = PolicyPauseProbe;
        if (probe == null) return ClientPolicyView.PauseNotAvailable;
        var ids = new List<string>();
        var names = new List<string>();
        foreach (var r in rows ?? Array.Empty<JoinNotAllowedRow>())
            if (r != null && r.Action == JoinNotAllowedAction.Pause && r.Id.Length > 0)
            {
                ids.Add(r.Id);
                names.Add(r.Name);
            }

        if (ids.Count == 0) return "";
        JoinPauseResult result;
        try { result = probe(ids) ?? JoinPauseResult.Fail("the pause did not answer"); }
        catch (Exception e) { result = JoinPauseResult.Fail(e.Message); }
        Plugin.Logger?.LogInfo("ClientPolicy: pause for this server " + string.Join(", ", ids) + " -> "
            + (result.Ok ? "paused " + string.Join(", ", result.Taken) : "refused: " + result.Message));
        lock (Gate) ForgetClientModsLocked();
        Refresh();
        if (!result.Ok) return ClientPolicyView.PauseFailed(string.Join(", ", names), result.Message);
        string pausedNames = string.Join(", ", result.Names.Count > 0 ? result.Names : names);
        // 2026-10-05 (client mod policy rule 4): inside an install window the server is waiting for a fresh answer and
        // report; once nothing else on the page needs attention, answer again (the report follows the answer).
        bool reAnswer;
        lock (Gate)
        {
            var s = _session;
            var model = s?.Model ?? JoinPageModel.Build(BuildFactsLocked());
            reAnswer = s != null && s.Mode == JoinMode.InstallWindow && model.NeedAttention == 0 && model.NotAllowedUnresolved == 0;
        }

        if (reAnswer && InstallWindowOpen)
        {
            bool sent;
            try { sent = ReAnswerHandshake(); }
            catch { sent = false; }
            Plugin.Logger?.LogInfo("ClientPolicy: re-answered the handshake after pausing " + pausedNames + " inside the install window; sent=" + (sent ? "true" : "false"));
            return ClientPolicyView.PausedNotice(result.Names.Count > 0 ? result.Names : names);
        }

        bool inPlace = false;
        lock (Gate)
        {
            var s = _session;
            var model = s?.Model ?? JoinPageModel.Build(BuildFactsLocked());
            if (s != null && s.Mode == JoinMode.Refused && model.NeedAttention == 0)
            {
                // INPLACE1: still in this server's world: the pause finishes here (answered again below), no rejoin.
                if (InWorldNow())
                {
                    inPlace = true;
                    offer = InPlaceOfferLocked(pausedNames, "paused for this server", "paused for this server");
                }
                else
                {
                    var target = CurrentTargetLocked();
                    bool canRejoin = target.CanReconnect && !target.HostedByThisClient;
                    offer = new JoinResultOffer
                    {
                        Kind = canRejoin ? JoinAfterInstall.JoinNow : JoinAfterInstall.Nothing,
                        CanRejoin = canRejoin,
                        Hosted = target.HostedByThisClient,
                        Summary = ClientPolicyView.PausedJoinNow(pausedNames) + (canRejoin ? "" : " Join the server again from the server list."),
                    };
                }
            }
        }

        if (inPlace && offer != null) FinishInPlace(offer, "pausing " + pausedNames);
        return offer?.Summary ?? ClientPolicyView.PausedNotice(result.Names.Count > 0 ? result.Names : names);
    }

    private static readonly object ConfirmOwner = new();
    private static Progenitor.Ui.UiDialog? _turnOffConfirm;

    /// <summary>True while the Turn off and restart confirmation is on screen (the join screens wait to redraw under it).</summary>
    internal static bool TurnOffConfirmOpen => _turnOffConfirm?.IsOpen == true;

    /// <summary>
    /// The effects' Turn off and restart: a confirmation first (the game's popup, with the sentence the row is too narrow
    /// for: "It stays off until you turn it back on in the Mods list."), then the Mods list's switch for the next start and
    /// Restart and rejoin. Without a client UI to draw the confirmation, it acts at once.
    /// </summary>
    private static string TurnOffForRestartLive(IReadOnlyList<JoinNotAllowedRow> rows, out JoinResultOffer? offer)
    {
        offer = null;
        if (SwitchProbe == null) return JoinText.TurnOnNotAvailable;
        var targets = new List<JoinNotAllowedRow>();
        foreach (var r in rows ?? Array.Empty<JoinNotAllowedRow>())
            if (r != null && r.Action == JoinNotAllowedAction.TurnOffRestart && r.Id.Length > 0) targets.Add(r);
        if (targets.Count == 0) return "";
        var names = new List<string>(targets.Count);
        var ids = new List<string>(targets.Count);
        foreach (var t in targets)
        {
            names.Add(t.Name);
            ids.Add(t.Id);
        }

        string joined = string.Join(", ", names);
        Progenitor.Ui.UiDialog? dialog = null;
        try
        {
            dialog = Progenitor.Ui.UiDialog.Open(
                title => Progenitor.Ui.Ui.Panel(title, ConfirmOwner, JoinFlowPanel.PluginId),
                "join-turn-off", "turn-off-restart", string.Join(",", ids),
                ClientPolicyView.TurnOffConfirmTitle,
                Progenitor.Ui.UiRichText.Literal(ClientPolicyView.TurnOffConfirmBody(joined, targets.Count)),
                ClientPolicyView.TurnOffRestartButton, Progenitor.Ui.LabelTone.Orange, JoinText.CancelButton);
            dialog.OnYes(() => AnswerTurnOff(targets, yes: true)).OnNo(() => AnswerTurnOff(targets, yes: false));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("ClientPolicy: the turn-off confirmation could not open: " + e.Message); }

        if (dialog == null || !dialog.IsOpen) return TurnOffNow(targets, out offer);
        _turnOffConfirm = dialog;
        return ClientPolicyView.TurnOffAsked(joined);
    }

    private static void AnswerTurnOff(List<JoinNotAllowedRow> targets, bool yes)
    {
        _turnOffConfirm = null;
        JoinResultOffer? offer = null;
        string line = yes ? TurnOffNow(targets, out offer) : ClientPolicyView.NothingTurnedOff;
        lock (Gate)
        {
            var s = _session;
            if (s != null && !s.Closed && !s.IsPreview)
            {
                if (offer != null) s.ShowResult(offer);
                else s.Note(line);
            }
        }

        RaiseChanged();
    }

    /// <summary>The switch itself, once confirmed.</summary>
    private static string TurnOffNow(IReadOnlyList<JoinNotAllowedRow> rows, out JoinResultOffer? offer)
    {
        offer = null;
        var parts = new List<string>();
        var later = new List<string>();
        foreach (var r in rows ?? Array.Empty<JoinNotAllowedRow>())
        {
            if (r == null || r.Action != JoinNotAllowedAction.TurnOffRestart || r.Id.Length == 0) continue;
            var sw = SafeSwitch(r.Id, on: false);
            Plugin.Logger?.LogInfo("ClientPolicy: turn off " + r.Id + " " + r.Version + " -> " + (sw.Ok ? (sw.Now ? "now" : "restart") : "refused")
                + (sw.Message.Length > 0 ? ": " + sw.Message : ""));
            if (!sw.Ok) parts.Add(ClientPolicyView.TurnOffFailed(r.Name, sw.Message));
            else later.Add(r.Name);
        }

        lock (Gate) ForgetClientModsLocked();
        Refresh();
        if (later.Count > 0) parts.Insert(0, ClientPolicyView.TurnsOffAtNextStart(string.Join(", ", later)));
        string summary = string.Join(" ", parts);
        if (later.Count == 0) return summary;
        lock (Gate) offer = RestartOfferAfterPolicyLocked(summary);
        return offer?.Summary ?? summary;
    }

    // Called with Gate held: Restart and rejoin once nothing else on the page needs a fix of its own (a pausable mod is
    // paused by the next join's automatic pause), else null (the line says what was done).
    private static JoinResultOffer? RestartOfferAfterPolicyLocked(string summary)
    {
        var model = _session?.Model ?? JoinPageModel.Build(BuildFactsLocked());
        foreach (var r in model.Required)
        {
            if (r.Ready || r.TurnOnPending) continue;
            var state = InstallStateOfLocked(r.Id);
            if (state != null && state.Succeeded) continue;
            return null;
        }

        foreach (var na in model.NotAllowed)
            if (na.Action == JoinNotAllowedAction.TurnOffRestart) return null;

        var target = _batchTarget ?? CurrentTargetLocked();
        bool canRejoin = target.CanReconnect && !target.HostedByThisClient;
        var offer = new JoinResultOffer
        {
            Kind = JoinAfterInstall.RestartNeeded,
            CanRejoin = canRejoin,
            Hosted = target.HostedByThisClient,
            Summary = summary,
        };
        if (canRejoin)
        {
            try { offer.ManualRelaunch = Relauncher?.Plan().Mode != RelaunchMode.Automatic; }
            catch { offer.ManualRelaunch = true; }
        }

        return offer;
    }

    /// <summary>The readback of a required row's policy part (kind, reason, rule).</summary>
    private static void DescribePolicyRow(Dictionary<string, object?> d, JoinRequiredRow r)
    {
        d["kind"] = r.Origin == JoinRowOrigin.ClientPolicy ? "required-client" : r.Origin == JoinRowOrigin.YouRequire ? "you-require" : "server";
        d["reason"] = r.Reason;
        d["rule"] = r.PolicyRule == null ? "" : ClientModRules.Word(r.PolicyRule.Value);
        d["needsWords"] = r.PolicyRule == null ? "" : JoinText.NeedsWords(r);
        d["modId"] = r.ModId;
    }

    /// <summary>The readback of the "Not allowed on this server" rows.</summary>
    private static List<object?> DescribeNotAllowed(JoinPageModel model)
    {
        var list = new List<object?>();
        float width = JoinFlowLayout.SectionRowWidth(Progenitor.Ui.DialogLayout.ContentRowWidth());
        foreach (var r in model.NotAllowed)
        {
            // What the row draws at the popup's width (live 2026-10-05: cut text), and whether all of it fits.
            float textWidth = JoinFlowLayout.NotAllowedTextWidth(r, width);
            JoinFlowLayout.NotAllowedLine(r, textWidth, out string drawnName, out string drawnLead, out string drawnNote);
            list.Add(new Dictionary<string, object?>
            {
                ["drawnName"] = drawnName,
                ["drawnLine"] = drawnNote.Length > 0 ? drawnLead + " · " + drawnNote : drawnLead,
                ["textWidth"] = textWidth,
                ["fits"] = JoinFlowLayout.NotAllowedFits(r, width),
                ["id"] = r.Id,
                ["name"] = r.Name,
                ["version"] = r.Version,
                ["kind"] = "not-allowed",
                ["source"] = r.Source,
                ["state"] = ClientPolicyView.StateName(r.State),
                ["action"] = ClientPolicyView.ActionName(r.Action),
                ["actionCaption"] = ClientPolicyView.ActionCaption(r),
                ["pausable"] = r.Pausable,
                ["lead"] = ClientPolicyView.RowLead(r),
                ["line"] = ClientPolicyView.RowLine(r),
            });
        }

        return list;
    }

    /// <summary>The policy part of the readback: what the section says, what this client would report, what is remembered.</summary>
    private static Dictionary<string, object?> DescribePolicy(JoinSection? section, string host)
    {
        var d = new Dictionary<string, object?>
        {
            ["checksClientMods"] = section?.HasClientPolicy ?? false,
            ["mode"] = section == null ? "" : ClientModRules.ModeWord(section.PolicyMode),
            ["requiredClient"] = section?.RequiredClient.Count ?? 0,
            ["approved"] = section?.Approved.Count ?? 0,
            ["serverRuns"] = section?.ServerRuns.Count ?? 0,
            ["turnOffConfirm"] = TurnOffConfirmOpen,
        };
        string remembered = "";
        try { remembered = host.Length > 0 ? ServerProfiles.PolicyOf(host) : ""; }
        catch { remembered = ""; }
        d["remembered"] = remembered.Length > 0;
        d["rememberedFor"] = host;
        return d;
    }
}
