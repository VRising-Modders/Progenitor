using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Install;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>One mod the recommend picker offers: from this PC's mods or the Install tab's catalog.</summary>
internal sealed class JoinAdminPick
{
    public string Id { get; set; } = "";
    public string Package { get; set; } = "";
    public string Version { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Installed { get; set; }

    public string Key => Id.Length > 0 ? Id : Package;
}

/// <summary>
/// JOINADMIN1 (join flow stage 3): "Players joining this server", the admin popup in the game's popup frame. It
/// lists only the admin's choices — the refused-join alert, the recommended client mods (with inline reasons), the
/// client mod policy, and the install window. JOINDRAFT1 (requested 2026-10-07): every edit is staged in a draft
/// (<see cref="JoinAdminDraft"/>) and shown with its marker and in the Changes list above the buttons; <b>Apply</b> sends
/// the draft to the server (<see cref="JoinAdminApplyRun"/> over <see cref="JoinAdminClient"/>), which decides, stores and
/// audits each request as before; <b>Discard</b> returns to the server's state; <b>Close</b> with unapplied changes asks
/// first. Opened from the "Players joining this server" row on Progenitor's page (Server settings,
/// <see cref="JoinServerSettings"/>), shown only to admins of the joined server. Main thread.
/// </summary>
internal static class JoinAdminPanel
{
    private const string PluginId = "progenitor";
    internal const int PickSlots = 10;

    internal const string RowAlertHead = "ja-alert-head";
    internal const string RowRecHead = "ja-rec-head";
    internal const string RowWindow = "ja-window";
    internal const string RowStatus = "ja-status";
    internal const string RowButtons = "ja-buttons";
    internal const string RowSearch = "ja-search";
    internal const string RowChangesHead = "ja-changes-head";
    internal const string RowNoChanges = "ja-changes-none";
    internal const string RowMoreChanges = "ja-changes-more";
    internal const string CellText = "text";
    internal const string CellName = JoinAdminRowLayout.CellName;
    internal const string CellReason = JoinAdminRowLayout.CellReason;
    internal const string CellRemove = JoinAdminRowLayout.CellRemove;
    internal const string CellUndo = JoinAdminRowLayout.CellUndo;
    internal const string CellMark = JoinAdminRowLayout.CellMark;
    internal const string CellAction = "action";
    internal const string CellInput = "input";
    internal const string ButtonClose = JoinAdminRowLayout.ButtonClose;
    internal const string ButtonBack = "back";
    internal const string ButtonAddRecommend = "add-recommend";

    // JOINDRAFT1: the main page's buttons, and the close prompt's.
    internal const string ButtonApply = JoinAdminRowLayout.ButtonApply;
    internal const string ButtonDiscard = JoinAdminRowLayout.ButtonDiscard;
    internal const string ButtonCancel = JoinAdminRowLayout.ButtonCancel;

    internal const string RowReqHead = "ja-req-head";
    internal const string RowPolicyHead = "ja-policy-head";
    internal const string CellRule = JoinAdminRowLayout.CellRule;
    internal const string ButtonAddRequired = "add-required";
    internal const string ButtonAddApproved = "add-approved";
    internal const string ButtonApproveAll = "approve-all";
    internal const string ButtonConfirm = "confirm";

    // RADIO1: the three pick-one choices are radio lists (rows <key>-0, <key>-1 and their -about rows).
    internal const string RadioMode = "ja-mode";
    internal const string RadioUnverified = "ja-unverified";
    internal const string RadioMid = "ja-mid";

    // Mod compatibility C3: Version matching's radio list, and the rows of the mods the server runs (ja-smr-<id>).
    internal const string RadioMatch = "ja-match";
    internal const string RowMatchHead = "ja-match-head";
    internal const string RowServerModsHead = "ja-smr-head";
    internal const string ServerModRowPrefix = "ja-smr-";

    // CLIENTPOLICY3: the alert line's one-click approval.
    internal const string CellApprove = "approve";

    /// <summary>RADIO1: the radio lists the last draw put on the panel (the readback's <c>radios</c>).</summary>
    private static readonly List<UiRadioList> Radios = new();

    internal enum Page { Main, PickRecommend, PickRequire, PickApprove, ConfirmApproveAll, ConfirmClose }

    private static readonly object Owner = new();
    private static readonly OwnedPanel Owned = new();
    private static Page _page = Page.Main;
    private static bool _wanted;
    private static IDisposable? _escape;
    private static bool _subscribed;

    /// <summary>JOINDRAFT1: the staged edits over the server's last state; the operator's lock list refuses a locked edit.</summary>
    internal static readonly JoinAdminDraft Draft = new() { LockedBy = LockNoteForOp };

    private static JoinAdminApplyRun? _run;
    private static bool _closeAfterApply;
    private static string _applyStatus = "";
    private static CellTone _applyTone = CellTone.Muted;

    private static readonly Dictionary<string, IUiCellRow> RecRows = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IUiCellRow> ReqRows = new(StringComparer.OrdinalIgnoreCase);

    // WIDEDIALOG1: the name lines of the two-line mod rows (the reason lines are RecRows and ReqRows), by row key.
    private static readonly Dictionary<string, IUiCellRow> HeadRows = new(StringComparer.Ordinal);

    // JOINDRAFT1: the pinned Changes list (rewritten in place, so typing a reason keeps the field's focus).
    private static readonly List<IUiCellRow> ChangeRows = new();
    private static IUiCellRow? _changesHead;
    private static IUiCellRow? _noChangesRow;
    private static IUiCellRow? _moreChangesRow;
    private static IUiCellRow? _buttonsRow;

    private static int _approvedFrom;
    private static List<JoinAdminBulkItem> _confirmItems = new();
    private static readonly List<IUiCellRow> Slots = new();
    private static readonly List<JoinAdminPick> Picks = new();
    private static string _windowDraft = "";
    private static string _search = "";
    private static string _localStatus = "";
    private static IUiCellRow? _statusRow;
    private static IUiCellRow? _windowRow;
    private static Page _builtPage = Page.Main;
    private static string _builtSignature = "";

    internal static bool IsOpen => Owned.IsOpen;
    internal static Page CurrentPage => _page;
    internal static bool Applying => _run is { Running: true };

    /// <summary>The button's action: open on the main page and ask the server for the state.</summary>
    internal static string Open()
    {
        if (!JoinAdminClient.IsAdmin) return "not offered: the joined server has no panel, or you are not an admin there";
        if (!_subscribed)
        {
            JoinAdminClient.Changed += OnChanged;
            _subscribed = true;
        }

        _wanted = true;
        _page = Page.Main;
        _localStatus = "";
        _applyStatus = "";
        _closeAfterApply = false;
        if (!Applying) Draft.Reset(JoinAdminClient.State);
        _approvedFrom = 0;
        _confirmItems = new List<JoinAdminBulkItem>();
        _windowDraft = "";
        Send(new JoinAdminRequest { Op = JoinAdminOps.Get });
        Redraw();
        return "opened";
    }

    /// <summary>
    /// Close. JOINDRAFT1: with unapplied changes this asks first (<see cref="Page.ConfirmClose"/>: Apply / Discard /
    /// Cancel); <paramref name="force"/> (the connection ended) closes and drops the draft.
    /// </summary>
    internal static string Close(bool force = false)
    {
        switch (JoinAdminCloseRule.Decide(Draft.HasChanges, Applying, force || !_wanted))
        {
            case JoinAdminCloseAction.Wait:
                _localStatus = JoinAdminText.WaitForApply;
                RefreshStatus();
                return "not closed: " + _localStatus;
            case JoinAdminCloseAction.Ask:
                _page = Page.ConfirmClose;
                Redraw();
                return "asked: " + JoinAdminText.CloseBody(Draft.Count);
        }

        _wanted = false;
        _closeAfterApply = false;
        Draft.Reset(null);
        Drop();
        return "closed";
    }

    private static void OnChanged()
    {
        if (!_wanted) return;
        // The connection ended, or this player is no longer an admin there: the panel goes.
        if (!JoinAdminClient.ServerSupports)
        {
            Close(force: true);
            return;
        }

        var s = JoinAdminClient.State;
        // JOINDRAFT1: the server's state is the draft's new base (another admin's edits are rebased onto it).
        Draft.Rebase(s);
        if (s != null) _approvedFrom = s.ApprovedFrom;

        // An answer that changes no row is written into the drawn rows in place, so a field being typed in keeps its
        // focus and caret (a rebuild would recreate it).
        if (Owned.IsOpen && _builtPage == _page)
        {
            if (IsModPicker(_page))
            {
                RebuildPicks();
                FillSlots();
                RefreshStatus();
                return;
            }

            if (_page == Page.Main && s != null && string.Equals(_builtSignature, MainSignature(s, Draft), StringComparison.Ordinal))
            {
                UpdateMainInPlace(s);
                RefreshStatus();
                return;
            }
        }

        Redraw();
    }

    /// <summary>
    /// What a main-page rebuild depends on: the rows and their shape, not the editable values. JOINDRAFT1: the draft's
    /// changes count too, except a typed reason and the window (written in place, so the field keeps its focus).
    /// </summary>
    internal static string MainSignature(JoinAdminState? s, JoinAdminDraft? draft = null)
    {
        if (s == null) return "none";
        var sb = new System.Text.StringBuilder();
        sb.Append(s.RefusedJoins).Append('|');
        foreach (var a in s.Alert) sb.Append(a.Text).Append('\u0001').Append(a.Mod).Append('\u0002');
        sb.Append('|');
        foreach (var r in s.Recommended) sb.Append(r.Key).Append(r.FromFile ? "*" : "").Append(r.Version).Append(r.Package).Append(r.FromFile ? r.Reason : "").Append('\u0002');
        sb.Append('|');
        // CLIENTPOLICY1: the policy's rows and choices (a reason is edited in place, like a recommendation's).
        sb.Append('|').Append(s.PolicySupported).Append(s.PolicyMode).Append(s.Unverified).Append(s.MidSession).Append(s.ApprovedFrom).Append('/').Append(s.ApprovedTotal);
        foreach (var r in s.RequiredClient) sb.Append(r.Key).Append(r.Version).Append(r.Rule).Append(r.Package).Append(r.Name).Append('\u0002');
        sb.Append('|');
        foreach (var r in s.Approved) sb.Append(r.Key).Append(r.Version).Append(r.Rule).Append(r.Name).Append('\u0002');
        // C3: Version matching and the server's mods with their rules.
        sb.Append('|').Append(s.MatchingSupported).Append(s.VersionMatching).Append(s.VersionMatchingForced);
        foreach (var r in s.ServerMods) sb.Append(r.Id).Append(r.Version).Append(r.Rule).Append(r.Runs).Append(r.Name).Append('\u0002');
        if (draft != null)
        {
            sb.Append("|draft");
            foreach (var c in draft.Changes)
            {
                if (c.Kind is JoinAdminChangeKind.RecommendReason or JoinAdminChangeKind.RequiredReason or JoinAdminChangeKind.Window) continue;
                sb.Append((int)c.Kind).Append(c.Key).Append(c.Value).Append(c.Rule).Append(c.Version).Append('\u0002');
            }
        }

        return sb.ToString();
    }

    private static bool IsModPicker(Page p) => p == Page.PickRecommend || p == Page.PickRequire || p == Page.PickApprove;

    private static void UpdateMainInPlace(JoinAdminState s)
    {
        foreach (var d in Draft.RecommendedRows())
        {
            var r = d.Row;
            if (r.FromFile || d.Mark != JoinAdminRowMark.None || !RecRows.TryGetValue(r.Key, out var row)) continue;
            try { row.SetCell(CellReason, text: r.Reason); }
            catch { /* the next draw shows it */ }
        }

        foreach (var d in Draft.RequiredRows())
        {
            if (d.Mark != JoinAdminRowMark.None || !ReqRows.TryGetValue(d.Row.Key, out var row)) continue;
            try { row.SetCell(CellReason, text: d.Row.Reason); }
            catch { /* the next draw shows it */ }
        }

        if (_windowRow != null && _windowDraft.Length == 0)
        {
            try { _windowRow.SetCell(CellInput, text: Draft.WindowSeconds.ToString(CultureInfo.InvariantCulture)); }
            catch { /* the next draw shows it */ }
        }

        RefreshMarks();
        RefreshChanges();
    }

    // ---- actions (the buttons and the bridge share these) ----------------------------------

    /// <summary>
    /// The entry of the joined server's lock list that locks list or choice <paramref name="key"/> of this popup
    /// (<see cref="SettingPlaces"/> <c>recommended</c>, <c>required-client</c>, <c>policy-mode</c>, <c>approved</c>,
    /// <c>unverified</c>, <c>mid-session</c>, <c>window</c>), or null.
    /// </summary>
    internal static string? LockedBy(string key) => JoinAdminRows.LockedBy(SettingPlaces.JoinAdminId(key));

    /// <summary>JOINDRAFT1: the lock note of a request operation, or null (the draft refuses to stage a locked edit).</summary>
    private static string? LockNoteForOp(string op) =>
        ServerSettingsLock.JoinAdminSettingId(op) is { } id && JoinAdminRows.LockedBy(id) is { } by ? ServerSettingsLock.NoteFor(by) : null;

    /// <summary>WIDEDIALOG1: <see cref="JoinAdminRowLayout.SectionHeading"/>.</summary>
    private static CellRowSpec SectionHeading(string key, string title, string count) => JoinAdminRowLayout.SectionHeading(key, title, count);

    /// <summary>The note line under a locked section's heading.</summary>
    private static void LockedLine(IUiPanel panel, string key, float width)
    {
        if (LockedBy(key) == null) return;
        panel.CellRow(JoinFlowLayout.Line("ja-locked-" + key, LockNote(key), CellTone.Amber, width, align: CellAlign.Left, fontSize: 14f));
    }

    /// <summary>
    /// The reason a list or choice of this popup is read-only (<see cref="LockedLine"/>'s text), or null when editable.
    /// The popup is offered only to a player the server authorizes for these edits (<see cref="JoinAdminClient.IsAdmin"/>:
    /// the hello's or the remote install status's <c>ServerAdminList.AuthorizeShared</c> answer, the same rule every
    /// request is checked against), so the operator's lock list is the only per-choice reason left; a refusal for
    /// authorization drops the state and the popup shows that refusal instead of any choice.
    /// </summary>
    internal static string? LockNote(string key) => LockedBy(key) is { } by ? ServerSettingsLock.NoteFor(by) : null;

    internal static string Send(JoinAdminRequest request) => Send(request, honorLock: true);

    /// <summary>
    /// Send one request at once (the state <c>get</c>, the approved list's pages, and the bridge's raw requests; edits are
    /// staged and sent by <see cref="Apply"/>). <paramref name="honorLock"/> false is the bridge's "client that skips the
    /// panel": the request goes to the server even when the lock list names it, so the server's own refusal can be tested.
    /// </summary>
    internal static string Send(JoinAdminRequest request, bool honorLock)
    {
        // The operator's lock list: a locked list or choice sends nothing (the server would refuse it anyway).
        if (honorLock && ServerSettingsLock.JoinAdminSettingId(request.Op) is { } settingId && JoinAdminRows.LockedBy(settingId) is { } lockedBy)
        {
            _localStatus = ServerSettingsLock.NoteFor(lockedBy);
            RefreshStatus();
            return "not sent: " + _localStatus;
        }

        // CLIENTPOLICY1: every answer carries the approved list's window this panel shows.
        if (request.ApprovedFrom == 0) request.ApprovedFrom = _approvedFrom;
        if (JoinAdminClient.Request(request, out string refusal))
        {
            _localStatus = "";
            RefreshStatus();
            return "sent " + request.Op;
        }

        _localStatus = refusal;
        RefreshStatus();
        return "not sent: " + refusal;
    }

    /// <summary>
    /// JOINDRAFT1: the result of one staged edit. <paramref name="redraw"/> false for an edit written in place (a typed
    /// reason or window), so the field being typed in keeps its focus.
    /// </summary>
    private static string Staged(string? refusal, string what, bool redraw = true)
    {
        if (refusal != null)
        {
            _localStatus = refusal;
            RefreshStatus();
            return "not staged: " + refusal;
        }

        _localStatus = "";
        _applyStatus = "";
        if (redraw) Redraw();
        else
        {
            RefreshMarks();
            RefreshChanges();
            RefreshStatus();
        }

        return "staged " + what + " (" + Draft.Count.ToString(CultureInfo.InvariantCulture) + " change(s); Apply sends them)";
    }

    /// <summary>JOINDRAFT1: an edit while Apply runs is refused (the draft is being sent).</summary>
    private static string? Busy() => Applying ? JoinAdminText.WaitForApply : null;

    internal static string AddRecommendation(string id, string package, string version, string reason, string name = "")
    {
        if (_page == Page.PickRecommend) _page = Page.Main;
        return Staged(Busy() ?? Draft.Recommend(id, package, version, reason, name), "recommend");
    }

    internal static string RemoveRecommendation(string key) => Staged(Busy() ?? Draft.Unrecommend(key), "unrecommend");

    internal static string SetReason(string key, string reason) => Staged(Busy() ?? Draft.SetRecommendedReason(key, reason ?? ""), "reason", redraw: false);

    internal static string SetWindow(int seconds)
    {
        _windowDraft = "";
        return Staged(Busy() ?? Draft.SetWindow(seconds), "window", redraw: false);
    }

    internal static string Refresh() => Send(new JoinAdminRequest { Op = JoinAdminOps.Get });

    // ---- CLIENTPOLICY1: the client mod policy's actions (the buttons and the bridge share these) ----------------

    internal static string AddRequired(string id, string package, string version, string rule, string reason, string name = "")
    {
        if (_page == Page.PickRequire) _page = Page.Main;
        return Staged(Busy() ?? Draft.Require(id, package, version, rule, reason, name), "require");
    }

    internal static string RemoveRequired(string key) => Staged(Busy() ?? Draft.Unrequire(key), "unrequire");

    internal static string SetRequiredReason(string key, string reason) =>
        Staged(Busy() ?? Draft.SetRequiredReason(key, reason ?? ""), "require-reason", redraw: false);

    /// <summary>Set a required row's rule (and optionally its version); an empty <paramref name="rule"/> cycles to the next one.</summary>
    internal static string SetRequiredRule(string key, string rule, string version = "")
    {
        string word = (rule ?? "").Trim();
        if (word.Length == 0)
        {
            JoinAdminClientRow? row = null;
            foreach (var d in Draft.RequiredRows())
                if (JoinAdminDraft.SameMod(d.Row.Id, d.Row.Package, key, key)) row = d.Row;
            var next = ClientModRules.Next(row?.RuleValue ?? ClientModRule.AtLeast);
            // A row without a version cannot hold a versioned rule: it stays Any.
            if ((row?.Version ?? "").Length == 0 && (version ?? "").Length == 0) next = ClientModRule.Any;
            word = ClientModRules.Word(next);
        }

        return Staged(Busy() ?? Draft.SetRequiredRule(key, word, version ?? ""), "require-rule");
    }

    internal static string SetPolicyMode(string mode) => Staged(Busy() ?? Draft.SetPolicyMode(mode), "policy-mode");

    internal static string SetUnverified(string value) => Staged(Busy() ?? Draft.SetUnverified(value), "unverified");

    /// <summary>CLIENTPOLICY3: Warn and pause it (<c>warn</c>) or Disconnect the player (<c>disconnect</c>).</summary>
    internal static string SetMidSession(string value) => Staged(Busy() ?? Draft.SetMidSession(value), "mid-session");

    /// <summary>Mod compatibility C3: Version matching (<c>range</c>, <c>minor</c>, <c>exact</c>).</summary>
    internal static string SetVersionMatching(string value) => Staged(Busy() ?? Draft.SetVersionMatching(value), "version-matching");

    /// <summary>
    /// C3: a server mod's rule (<c>exact</c>, <c>at-least</c>, <c>minor</c>, <c>any</c>, or <c>default</c> for the server
    /// setting); null is the row's Rule button, which cycles (the server setting, Exact, or newer, Same minor, Any version).
    /// </summary>
    internal static string SetServerModRule(string id, string? rule)
    {
        string? word = rule;
        if (word == null)
        {
            string current = "";
            foreach (var d in Draft.ServerModRows())
                if (string.Equals(d.Row.Id, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) current = d.Row.Rule;
            word = VersionMatchText.NextRule(current);
        }

        return Staged(Busy() ?? Draft.SetServerModRule(id, word), "server-mod-rule");
    }

    /// <summary>
    /// CLIENTPOLICY3: an alert line's <b>Approve</b>: stages the existing <c>approve-add</c> for the line's mod (a plain
    /// plugin id, any version), so Apply sends it and the server decides, stores, signs and audits it as any approval; the
    /// answer's alert then reads <c>approved now</c>. Refused here for a line that offers no Approve.
    /// </summary>
    internal static string ApproveFromAlert(string mod)
    {
        var s = JoinAdminClient.State;
        JoinAdminAlertRow? line = null;
        if (s != null)
            foreach (var a in s.Alert)
                if (string.Equals(a.Mod, (mod ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) { line = a; break; }
        if (line == null) return "not staged: no alert line for " + mod;
        if (!line.CanApprove) return "not staged: that line offers no Approve" + (line.Approved ? " (already approved)" : "");
        string name = line.Name.Length > 0 && !string.Equals(line.Name, line.Mod, StringComparison.OrdinalIgnoreCase) ? ClipName(line.Name) : "";
        return Staged(Busy() ?? Draft.Approve(line.Mod, "", "", "", name), "approve");
    }

    internal static string AddApproved(string id, string package, string version, string rule, string name = "")
    {
        if (_page == Page.PickApprove) _page = Page.Main;
        return Staged(Busy() ?? Draft.Approve(id, package, version, rule, ClipName(name)), "approve");
    }

    internal static string RemoveApproved(string key) => Staged(Busy() ?? Draft.Unapprove(key), "unapprove");

    /// <summary>Approve everything I have: build the list of this PC's client mods and show the confirmation that lists them.</summary>
    internal static string ApproveEverything()
    {
        if (!_wanted) return "the panel is closed";
        _confirmItems = ClientPolicyPanelModel.ApproveEverything(LocalMods(), Draft.Effective());
        _page = Page.ConfirmApproveAll;
        Redraw();
        return _confirmItems.Count.ToString(CultureInfo.InvariantCulture) + " mod(s) to approve; confirm to stage them";
    }

    /// <summary>
    /// The confirmation's Approve all: stages one approval per mod. Apply sends them as <c>approve-bulk</c> requests (at
    /// most 64 rows each, each fitting one packet: <see cref="JoinAdminWire.BulkRequests"/>).
    /// </summary>
    internal static string ConfirmApproveEverything()
    {
        if (_confirmItems.Count == 0)
        {
            _page = Page.Main;
            Redraw();
            return "nothing to approve";
        }

        if (Busy() is { } busy) return Staged(busy, "approve");
        int staged = 0;
        string? last = null;
        foreach (var item in _confirmItems)
        {
            string? why = Draft.Approve(item.Id, item.Package, "", "", item.Name);
            if (why == null) staged++;
            else last = why;
        }

        _confirmItems = new List<JoinAdminBulkItem>();
        _page = Page.Main;
        _localStatus = staged == 0 && last != null ? last : "";
        Redraw();
        return "staged " + staged.ToString(CultureInfo.InvariantCulture) + " approval(s)" + (last != null ? "; one not staged: " + last : "");
    }

    /// <summary>Page the approved list (its rows past what one answer carries).</summary>
    internal static string PageApproved(bool forward)
    {
        var s = JoinAdminClient.State;
        if (s == null) return "no state";
        _approvedFrom = ClientPolicyPanelModel.PageFrom(s.ApprovedFrom, s.Approved.Count, s.ApprovedTotal, forward);
        return Send(new JoinAdminRequest { Op = JoinAdminOps.Get, ApprovedFrom = _approvedFrom });
    }

    // ---- JOINDRAFT1: Apply, Discard, undo -----------------------------------------------------------------------

    /// <summary>Apply: send the whole draft in order (<see cref="JoinAdminDraft.Steps"/>), one request after another's answer.</summary>
    internal static string Apply()
    {
        if (!_wanted) return "the panel is closed";
        if (Applying) return "already applying";
        if (!Draft.HasChanges)
        {
            _closeAfterApply = false;
            _applyStatus = JoinAdminDraft.NoChanges + ".";
            _applyTone = CellTone.Muted;
            RefreshStatus();
            return "nothing to apply";
        }

        if (_page == Page.ConfirmClose) _page = Page.Main;
        _localStatus = "";
        _applyStatus = "";
        _run = new JoinAdminApplyRun(Draft, SendForApply, OnApplied);
        int total = _run.Total;
        Redraw();
        _run.Start();
        if (Applying) RefreshAfterStep();
        return "applying " + total.ToString(CultureInfo.InvariantCulture) + " change(s)";
    }

    /// <summary>One Apply request: the lock list and the approved window as <see cref="Send(JoinAdminRequest, bool)"/>; null when sent.</summary>
    private static string? SendForApply(JoinAdminRequest request, Action<JoinAdminResponse?> answer)
    {
        if (request.Op != JoinAdminOps.Get && LockNoteForOp(request.Op) is { } locked) return locked;
        if (request.ApprovedFrom == 0) request.ApprovedFrom = _approvedFrom;
        return JoinAdminClient.Request(request, out string refusal, r =>
        {
            answer(r);
            RefreshAfterStep();
        }) ? null : refusal;
    }

    private static void RefreshAfterStep()
    {
        RefreshChanges();
        RefreshStatus();
    }

    private static void OnApplied(JoinAdminApplyRun run)
    {
        _applyStatus = run.Summary();
        _applyTone = run.Failed == 0 ? CellTone.Ok : CellTone.Orange;
        Plugin.Logger?.LogInfo("JoinAdmin: Apply: " + _applyStatus);
        bool close = _closeAfterApply && run.Failed == 0;
        _closeAfterApply = false;
        if (close)
        {
            Close(force: true);
            return;
        }

        Redraw();
    }

    /// <summary>Discard: the draft returns to the server's state.</summary>
    internal static string Discard()
    {
        if (Applying) return Staged(JoinAdminText.WaitForApply, "discard");
        int n = Draft.Count;
        Draft.Reset(JoinAdminClient.State);
        _windowDraft = "";
        _localStatus = "";
        _applyStatus = "";
        if (_page == Page.ConfirmClose) return Close(force: true);
        Redraw();
        return "discarded " + n.ToString(CultureInfo.InvariantCulture) + " change(s)";
    }

    /// <summary>The Changes list's ×: drop change <paramref name="index"/>.</summary>
    internal static string UndoChange(int index)
    {
        if (Applying) return Staged(JoinAdminText.WaitForApply, "undo");
        if (!Draft.Undo(index)) return "no change " + index.ToString(CultureInfo.InvariantCulture);
        _windowDraft = "";
        _localStatus = "";
        Redraw();
        return "undone; " + Draft.Count.ToString(CultureInfo.InvariantCulture) + " change(s) left";
    }

    /// <summary>A removed row's Undo.</summary>
    private static string UndoRemoval(JoinAdminChangeKind kind, string key)
    {
        if (Applying) return Staged(JoinAdminText.WaitForApply, "undo");
        Draft.UndoRemoval(kind, key);
        Redraw();
        return "undone";
    }

    /// <summary>The close prompt's Apply: apply, then close when every change was accepted.</summary>
    internal static string ApplyAndClose()
    {
        _closeAfterApply = true;
        return Apply();
    }

    /// <summary>The client mods loaded on this PC (the pickers' and Approve everything's source).</summary>
    private static List<ClientPolicyLocalMod> LocalMods()
    {
        var list = new List<ClientPolicyLocalMod>();
        try
        {
            foreach (var row in ModMenuInventory.Snapshot().Rows)
            {
                if (row.LocalVersion.Length == 0 || row.Id.Length == 0 || IsFramework(row.Id)) continue;
                var entry = ModsMenu.JoinAdminEntryForPlugin(row.Id);
                string package = entry != null && PackageSideDerivation.Derive(entry).Side != PackageSide.Server ? entry.Key : "";
                list.Add(new ClientPolicyLocalMod(row.Id, row.DisplayName.Length > 0 ? row.DisplayName : row.Id, row.LocalVersion, package));
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: this PC's mods for the client policy threw: " + e.Message); }
        return list;
    }

    private static string ClipName(string? name)
    {
        string n = (name ?? "").Trim();
        return n.Length > JoinSectionLimits.MaxNameChars ? n.Substring(0, JoinSectionLimits.MaxNameChars) : n;
    }

    /// <summary>The alert row's "Update on server": hand off to the Install tab's server update for that mod.</summary>
    internal static string UpdateOnServer(string modId)
    {
        // JOINDRAFT1: the hand-off closes the panel; unapplied changes would be lost.
        if (Draft.HasChanges || Applying)
        {
            _localStatus = Applying ? JoinAdminText.WaitForApply : JoinAdminText.ApplyOrDiscardFirst;
            RefreshStatus();
            return "not started: " + _localStatus;
        }

        if (!ModsMenu.JoinAdminServerUpdate(modId, start: true, out string key, out string latest, out string why))
        {
            _localStatus = why;
            RefreshStatus();
            return "not started: " + why;
        }

        Close(force: true);
        return "handed off to the Install tab: " + key + " " + latest;
    }

    internal static string ShowPage(Page page)
    {
        if (!_wanted) return "the panel is closed";
        _page = page;
        if (IsModPicker(page)) RebuildPicks();
        if (page == Page.ConfirmApproveAll && _confirmItems.Count == 0)
            _confirmItems = ClientPolicyPanelModel.ApproveEverything(LocalMods(), Draft.Effective());
        Redraw();
        return "page " + PageName(page);
    }

    internal static string SetSearch(string text)
    {
        _search = text ?? "";
        RebuildPicks();
        FillSlots();
        return Picks.Count.ToString(CultureInfo.InvariantCulture) + " match(es)";
    }

    /// <summary>Escape: a sub-page goes back (the close prompt's Cancel); the main page closes as Close does.</summary>
    internal static string Escape()
    {
        if (!_wanted) return "closed";
        if (_page != Page.Main)
        {
            _confirmItems = new List<JoinAdminBulkItem>();
            return ShowPage(Page.Main);
        }

        return Close();
    }

    internal static string PageName(Page p) => p switch
    {
        Page.PickRecommend => "pick-recommend",
        Page.PickRequire => "pick-require",
        Page.PickApprove => "pick-approve",
        Page.ConfirmApproveAll => "confirm-approve-all",
        Page.ConfirmClose => "confirm-close",
        _ => "main",
    };

    // ---- the picker ------------------------------------------------------------------------

    /// <summary>
    /// The recommend picker's mods: this PC's loaded mods the server does not require (client-side mods), then the
    /// Install tab's catalog listings that are client-only, matching the search; minus what is already recommended
    /// and what the server requires (JOINDRAFT1: as the draft has them).
    /// </summary>
    internal static void RebuildPicks()
    {
        Picks.Clear();
        var state = Draft.Effective() ?? JoinAdminClient.State;
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // CLIENTPOLICY1: the same picker serves Require (minus what is required; a recommended mod moves) and Approve
        // (minus what is approved and what is always allowed). The server's own required mods stay out of Recommend only.
        if (state != null)
        {
            if (_page == Page.PickRecommend)
            {
                foreach (string id in state.Required) required.Add(id);
                foreach (var r in state.Recommended)
                {
                    if (r.Id.Length > 0) taken.Add(r.Id);
                    if (r.Package.Length > 0) taken.Add(r.Package);
                }
            }

            foreach (var r in _page == Page.PickApprove ? state.Approved : state.RequiredClient)
            {
                if (r.Id.Length > 0) taken.Add(r.Id);
                if (r.Package.Length > 0) taken.Add(r.Package);
            }
        }

        if (_page == Page.PickApprove)
            foreach (string id in ClientModJudge.ProgenitorIds) taken.Add(id);

        string q = (_search ?? "").Trim();
        bool Matches(string a, string b, string c) =>
            q.Length == 0 || a.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || b.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
            || c.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

        try
        {
            foreach (var row in ModMenuInventory.Snapshot().Rows)
            {
                if (Picks.Count >= 40) break;
                if (row.LocalVersion.Length == 0 || row.ServerAsked || row.Id.Length == 0) continue;
                if (IsFramework(row.Id) || required.Contains(row.Id) || taken.Contains(row.Id)) continue;
                var entry = ModsMenu.JoinAdminEntryForPlugin(row.Id);
                string package = entry != null && PackageSideDerivation.Derive(entry).Side != PackageSide.Server ? entry.Key : "";
                if (package.Length > 0 && taken.Contains(package)) continue;
                string name = row.DisplayName.Length > 0 ? row.DisplayName : row.Id;
                if (!Matches(name, row.Id, package)) continue;
                Picks.Add(new JoinAdminPick { Id = row.Id, Package = package, Version = row.LocalVersion, Name = name, Installed = true });
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: installed mods for the picker threw: " + e.Message); }

        if (q.Length > 0)
        {
            try
            {
                foreach (var entry in ModsMenu.JoinAdminCatalogSearch(q))
                {
                    if (Picks.Count >= 40) break;
                    if (PackageSideDerivation.Derive(entry).Side != PackageSide.Client) continue;
                    string key = entry.Key;
                    if (key.Length == 0 || !RemoteInstallWire.IsValidPackage(key) || taken.Contains(key)) continue;
                    string id = entry.Manifest.Id ?? "";
                    if (id.Length > 0 && (required.Contains(id) || taken.Contains(id))) continue;
                    bool dup = false;
                    foreach (var p in Picks)
                        if (string.Equals(p.Package, key, StringComparison.OrdinalIgnoreCase) || (id.Length > 0 && string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)))
                            dup = true;
                    if (dup) continue;
                    Picks.Add(new JoinAdminPick
                    {
                        Id = id, Package = key, Version = entry.Manifest.Version ?? "",
                        Name = entry.DisplayName ?? entry.PackageName ?? key,
                    });
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: catalog search for the picker threw: " + e.Message); }
        }
    }

    private static bool IsFramework(string id) =>
        string.Equals(id, PluginId, StringComparison.OrdinalIgnoreCase)
        || id.StartsWith(PluginId + ".bootstrap", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith(PluginId + ".dev", StringComparison.OrdinalIgnoreCase);

    /// <summary>Pick the <paramref name="index"/>th shown candidate (the picker's button).</summary>
    internal static string PickAt(int index)
    {
        if (IsModPicker(_page))
        {
            if (index < 0 || index >= Picks.Count) return "no candidate " + index;
            var p = Picks[index];
            if (_page == Page.PickRequire) return AddRequired(p.Id, p.Package, p.Version, "", "", p.Name);
            if (_page == Page.PickApprove) return AddApproved(p.Id, p.Package, "", "", p.Name);
            return AddRecommendation(p.Id, p.Package, p.Version, "", p.Name);
        }

        return "not on a picker page";
    }

    // ---- drawing ---------------------------------------------------------------------------

    private static void Redraw()
    {
        if (!_wanted)
        {
            Drop();
            return;
        }

        try { Build(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("JoinAdmin: the panel could not be drawn: " + e.Message);
            Drop();
        }
    }

    private static void ForgetRows()
    {
        RecRows.Clear();
        ReqRows.Clear();
        HeadRows.Clear();
        Radios.Clear();
        Slots.Clear();
        ChangeRows.Clear();
        _changesHead = null;
        _noChangesRow = null;
        _moreChangesRow = null;
        _buttonsRow = null;
        _statusRow = null;
        _windowRow = null;
    }

    private static void Drop()
    {
        _escape?.Dispose();
        _escape = null;
        ForgetRows();
        var held = Owned.Take();
        if (held == null) return;
        try { held.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinAdmin: panel dispose threw: " + e.Message); }
    }

    private static void Build()
    {
        var old = Owned.Take();
        if (old != null)
        {
            try { old.Dispose(); }
            catch { /* a stale handle must not stop the redraw */ }
        }

        ForgetRows();
        _builtPage = _page;
        _builtSignature = MainSignature(JoinAdminClient.State, Draft);
        if (IsModPicker(_page)) RebuildPicks();
        string title = _page switch
        {
            Page.PickRecommend => JoinAdminText.PickRecommendTitle,
            Page.PickRequire => ClientPolicyText.PickRequireTitle,
            Page.PickApprove => ClientPolicyText.PickApproveTitle,
            Page.ConfirmApproveAll => ClientPolicyText.ConfirmTitle,
            Page.ConfirmClose => JoinAdminText.CloseTitle,
            _ => JoinAdminText.Title,
        };
        var panel = Ui.Ui.Panel(title, Owner, PluginId);
        Owned.Adopt(panel);
        if (!panel.Enabled) return;
        var content = new DialogContent
        {
            Shape = UiDialogShape.Notice,
            // WIDEDIALOG1: wide enough for the mod rows (capped to the screen by the runtime).
            FrameWidth = DialogLayout.WideFrameWidth,
            Title = title,
            Footer = Ui.Ui.FrameworkFooter,
            RowKey = RowButtons,
            Body = _page switch
            {
                Page.PickRecommend => JoinAdminText.PickRecommendBody,
                Page.PickRequire => ClientPolicyText.PickRequireBody,
                Page.PickApprove => ClientPolicyText.PickApproveBody,
                Page.ConfirmApproveAll => ClientPolicyText.ConfirmBody(_confirmItems),
                Page.ConfirmClose => JoinAdminText.CloseBody(Draft.Count),
                _ => "",
            },
        };
        UiDialogView.Build(panel, content);
        float width = DialogLayout.ContentRowWidth(DialogLayout.WideFrameWidth);
        switch (_page)
        {
            case Page.PickRecommend:
            case Page.PickRequire:
            case Page.PickApprove: BuildPickRecommend(panel, width); break;
            case Page.ConfirmApproveAll: BuildConfirmApproveAll(panel, width); break;
            case Page.ConfirmClose: BuildConfirmClose(panel, width); break;
            default: BuildMain(panel, width); break;
        }

        Ui.Ui.Show(panel);
        _escape ??= Ui.Ui.PushEscape(() =>
        {
            if (!Owned.IsOpen) return false;
            Escape();
            return true;
        });
    }

    private static void BuildMain(IUiPanel panel, float width)
    {
        var s = JoinAdminClient.State;
        if (s == null || Draft.Base == null)
        {
            panel.CellRow(JoinFlowLayout.Line("ja-loading",
                JoinAdminClient.PendingCount > 0 ? JoinAdminText.Loading : (JoinAdminClient.LastError.Length > 0 ? JoinAdminClient.LastError : JoinAdminText.Loading),
                CellTone.Muted, width));
            StatusAndButtons(panel, width, back: false);
            return;
        }

        // 1. The alert, only when there is something to report.
        if (s.RefusedJoins > 0)
        {
            panel.CellRow(JoinFlowLayout.Line(RowAlertHead, JoinAdminText.RefusedLine(s.RefusedJoins, s.RefusedDays), CellTone.Amber, width,
                align: CellAlign.Left, fontSize: 18f));
            for (int i = 0; i < s.Alert.Count; i++)
            {
                var a = s.Alert[i];
                bool update = ModsMenu.JoinAdminServerUpdate(a.Mod, start: false, out _, out _, out _);
                // CLIENTPOLICY3: a not-approved line approves its mod in one click (it then reads "approved now").
                bool approve = a.CanApprove && LockedBy(SettingPlaces.OpApproved) == null;
                var cells = new List<CellSpec>
                {
                    new CellSpec(CellKind.Text, CellText).With(text: a.Text, wrap: CellWrap.Wrap, fontSize: 15f, width: CellWidth.Fill(120f),
                        tone: a.Approved ? CellTone.Ok : CellTone.Orange),
                };
                if (update)
                    cells.Add(new CellSpec(CellKind.Button, CellAction).With(text: JoinAdminText.UpdateOnServer, width: CellWidth.Px(190f),
                        fontSize: DialogLayout.ButtonFontSize, skin: CellSkin.PopupButton, tone: CellTone.Gold));
                if (approve)
                    cells.Add(new CellSpec(CellKind.Button, CellApprove).With(text: ClientPolicyText.AlertApprove, width: CellWidth.Px(130f),
                        fontSize: DialogLayout.ButtonFontSize, skin: CellSkin.PopupButton, tone: CellTone.Gold));
                float buttons = (update ? 210f : 0f) + (approve ? 150f : 0f);
                int lines = Math.Max(1, MenuPanelLayout.WrappedLines(a.Text, Math.Max(80f, width - (buttons > 0 ? buttons : 10f)), 15f, 4));
                var row = panel.CellRow(new CellRowSpec("ja-alert-" + i.ToString(CultureInfo.InvariantCulture), cells,
                    height: Math.Max(update || approve ? 40f : 24f, lines * 15f * 1.3f + 8f), plain: true));
                string mod = a.Mod;
                if (update) row.OnClick(CellAction, () => UpdateOnServer(mod));
                if (approve) row.OnClick(CellApprove, () => ApproveFromAlert(mod));
            }
        }

        // 2. Recommended client mods (JOINDRAFT1: as the draft has them, each with its marker).
        var recRows = Draft.RecommendedRows();
        int shown = 0;
        foreach (var r in recRows) if (r.Mark != JoinAdminRowMark.Removed) shown++;
        panel.CellRow(SectionHeading(RowRecHead, JoinAdminText.RecommendedHeading, JoinAdminText.ShownCount(shown)));
        panel.CellRow(JoinFlowLayout.Line("ja-rec-help", JoinAdminText.RecommendedHelp, CellTone.Muted, width, align: CellAlign.Left, fontSize: 14f));
        LockedLine(panel, SettingPlaces.OpRecommended, width);
        bool recLocked = LockedBy(SettingPlaces.OpRecommended) != null;
        foreach (var r in recRows) RecommendedRow(panel, r.Row, r.Mark, recLocked);
        SingleButton(panel, ButtonAddRecommend, JoinAdminText.AddRecommend, width, () => ShowPage(Page.PickRecommend),
            enabled: shown < JoinAdminLimits.MaxRecommended && !recLocked);

        // CLIENTPOLICY1: required client mods, then which client mods players may use.
        if (s.PolicySupported) BuildPolicySections(panel, s, width);

        // 3. The install window (JOINDRAFT1: the typed seconds are staged; no Apply of its own).
        panel.CellRow(SectionHeading("ja-window-head", JoinAdminText.WindowHeading, ""));
        LockedLine(panel, SettingPlaces.OpWindow, width);
        bool windowLocked = LockedBy(SettingPlaces.OpWindow) != null;
        string value = _windowDraft.Length > 0 ? _windowDraft : Draft.WindowSeconds.ToString(CultureInfo.InvariantCulture);
        var window = panel.CellRow(new CellRowSpec(RowWindow, new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellText).With(text: JoinAdminText.WindowLabel, fontSize: 16f, width: CellWidth.Fill(120f)),
            new CellSpec(CellKind.Input, CellInput) { MaxLength = 3 }.With(text: value, width: CellWidth.Px(90f), skin: CellSkin.ValueBox),
        }, height: 40f, plain: true));
        _windowRow = window;
        if (!windowLocked) window.OnChanged(CellInput, text => TypedWindow(text ?? ""));
        panel.CellRow(JoinFlowLayout.Line("ja-window-help", JoinAdminText.WindowHelp, CellTone.Muted, width, align: CellAlign.Left, fontSize: 14f));
        panel.CellRow(JoinFlowLayout.Line("ja-window-fixes", JoinAdminText.WindowFixes, CellTone.Muted, width, align: CellAlign.Left, fontSize: 14f));
        StatusAndButtons(panel, width, back: false);
    }

    /// <summary>The window field's text: a whole number is staged (clamped to 0 to 300); anything else says so.</summary>
    private static void TypedWindow(string text)
    {
        _windowDraft = text;
        string t = text.Trim();
        if (t.Length == 0) return;
        if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
        {
            _localStatus = "Type a whole number of seconds, 0 to " + JoinAdminLimits.MaxWindowSeconds + ".";
            RefreshStatus();
            return;
        }

        Staged(Busy() ?? Draft.SetWindow(n), "window", redraw: false);
    }

    private static void RecommendedRow(IUiPanel panel, JoinAdminRecRow r, JoinAdminRowMark mark, bool locked = false)
    {
        string key = r.Key;
        var second = new List<string>();
        if (r.Version.Length > 0) second.Add(r.Version);
        if (r.Package.Length > 0) second.Add("Thunderstore · " + r.Package);
        if (r.FromFile) second.Add(JoinAdminText.FromFile);
        string name = r.Name.Length > 0 ? r.Name : r.Id.Length > 0 ? r.Id : r.Package;
        if (r.Name.Length > 0 && r.Id.Length > 0) second.Insert(0, r.Id);
        // WIDEDIALOG1: two lines (JoinAdminRowLayout).
        string headKey = "ja-rec-" + key;
        var head = panel.CellRow(JoinAdminRowLayout.Head(headKey, name, string.Join(" · ", second), null, removable: !r.FromFile, locked, mark));
        HeadRows[headKey] = head;
        if (r.FromFile)
        {
            RecRows[key] = panel.CellRow(JoinAdminRowLayout.FileReason(headKey, r.Reason));
            return;
        }

        if (mark == JoinAdminRowMark.Removed)
        {
            RecRows[key] = panel.CellRow(JoinAdminRowLayout.RemovedReason(headKey, r.Reason));
            if (!locked) head.OnClick(CellUndo, () => UndoRemoval(JoinAdminChangeKind.RecommendRemove, key));
            return;
        }

        var row = panel.CellRow(JoinAdminRowLayout.Reason(headKey, r.Reason));
        RecRows[key] = row;
        if (locked) return;
        head.OnClick(CellRemove, () => RemoveRecommendation(key));
        // JOINDRAFT1: what is typed is the draft's reason (no Save); the marker and the Changes list follow in place.
        row.OnChanged(CellReason, text => SetReason(key, text ?? ""));
    }

    // ---- CLIENTPOLICY1: the two client mod policy sections ------------------------------------------------------

    private static void BuildPolicySections(IUiPanel panel, JoinAdminState s, float width)
    {
        // Mod compatibility C3: Version matching above the required client mods (a server older than C3 sends none: no rows).
        if (s.MatchingSupported) BuildVersionMatching(panel, s, width);

        // Required client mods.
        var reqRows = Draft.RequiredRows();
        int required = 0;
        foreach (var r in reqRows) if (r.Mark != JoinAdminRowMark.Removed) required++;
        panel.CellRow(SectionHeading(RowReqHead, ClientPolicyText.RequiredHeading, ClientPolicyText.RequiredCount(required)));
        panel.CellRow(JoinFlowLayout.Line("ja-req-help", ClientPolicyText.RequiredHelp, CellTone.Muted, width, align: CellAlign.Left, fontSize: 14f));
        LockedLine(panel, SettingPlaces.OpRequiredClient, width);
        bool reqLocked = LockedBy(SettingPlaces.OpRequiredClient) != null;
        foreach (var r in reqRows) RequiredRow(panel, r.Row, r.Mark, reqLocked);
        SingleButton(panel, ButtonAddRequired, ClientPolicyText.AddRequired, width, () => ShowPage(Page.PickRequire),
            enabled: required < ClientModPolicyLimits.MaxRequired && !reqLocked);

        // Which client mods players may use.
        bool approved = string.Equals(Draft.Word(JoinAdminChangeKind.PolicyMode), "approved", StringComparison.OrdinalIgnoreCase);
        panel.CellRow(SectionHeading(RowPolicyHead, ClientPolicyText.PolicyHeading, ""));
        LockedLine(panel, SettingPlaces.OpPolicyMode, width);
        Radios.Add(UiRadioList.Draw(panel, RadioMode, ClientPolicyText.ModeOptions, approved ? "approved" : "any", width,
            LockedBy(SettingPlaces.OpPolicyMode) != null, v => SetPolicyMode(v), LockNote(SettingPlaces.OpPolicyMode)));
        if (approved)
        {
            int total = Draft.ApprovedTotalAfter();
            panel.CellRow(SectionHeading("ja-approved-head", ClientPolicyText.ApprovedHeading(total), ""));
            LockedLine(panel, SettingPlaces.OpApproved, width);
            bool appLocked = LockedBy(SettingPlaces.OpApproved) != null;
            foreach (var r in Draft.ApprovedRows()) ApprovedRow(panel, r.Row, r.Mark, appLocked);
            if (s.ApprovedTotal > s.Approved.Count)
            {
                var pager = panel.CellRow(new CellRowSpec("ja-approved-pager", new List<CellSpec>
                {
                    new CellSpec(CellKind.Button, "earlier").With(text: ClientPolicyText.Earlier, width: CellWidth.Px(140f), fontSize: DialogLayout.ButtonFontSize,
                        skin: CellSkin.PopupButton, tone: s.ApprovedFrom > 0 ? CellTone.Normal : CellTone.Disabled),
                    new CellSpec(CellKind.Muted, CellText).With(text: ClientPolicyText.Pager(s.ApprovedFrom, s.Approved.Count, s.ApprovedTotal), width: CellWidth.Fill(120f)),
                    new CellSpec(CellKind.Button, "more").With(text: ClientPolicyText.More, width: CellWidth.Px(140f), fontSize: DialogLayout.ButtonFontSize,
                        skin: CellSkin.PopupButton, tone: s.ApprovedFrom + s.Approved.Count < s.ApprovedTotal ? CellTone.Normal : CellTone.Disabled),
                }, height: 40f, plain: true));
                if (s.ApprovedFrom > 0) pager.OnClick("earlier", () => PageApproved(forward: false));
                if (s.ApprovedFrom + s.Approved.Count < s.ApprovedTotal) pager.OnClick("more", () => PageApproved(forward: true));
            }

            SingleButton(panel, ButtonAddApproved, ClientPolicyText.AddApproved, width, () => ShowPage(Page.PickApprove),
                enabled: total < ClientModPolicyLimits.MaxApproved && !appLocked);
            SingleButton(panel, ButtonApproveAll, ClientPolicyText.ApproveEverything, width, ApproveEverything,
                enabled: total < ClientModPolicyLimits.MaxApproved && !appLocked);
            panel.CellRow(JoinFlowLayout.Line("ja-always", ClientPolicyText.AlwaysApproved, CellTone.Muted, width, align: CellAlign.Left, fontSize: 14f));
            panel.CellRow(SectionHeading("ja-unverified-head", ClientPolicyText.UnverifiedHeading, ""));
            LockedLine(panel, SettingPlaces.OpUnverified, width);
            bool refuse = string.Equals(Draft.Word(JoinAdminChangeKind.Unverified), "refuse", StringComparison.OrdinalIgnoreCase);
            Radios.Add(UiRadioList.Draw(panel, RadioUnverified, ClientPolicyText.UnverifiedOptions, refuse ? "refuse" : "allow", width,
                LockedBy(SettingPlaces.OpUnverified) != null, v => SetUnverified(v), LockNote(SettingPlaces.OpUnverified)));
        }

        // CLIENTPOLICY3: what a mid-session re-check does (a server older than stage 3 sends no choice: no row).
        if (s.MidSession.Length > 0)
        {
            bool disconnect = string.Equals(Draft.Word(JoinAdminChangeKind.MidSession), ClientModRules.WordDisconnect, StringComparison.OrdinalIgnoreCase);
            panel.CellRow(SectionHeading("ja-mid-head", ClientPolicyText.MidSessionHeading, ""));
            LockedLine(panel, SettingPlaces.OpMidSession, width);
            Radios.Add(UiRadioList.Draw(panel, RadioMid, ClientPolicyText.MidSessionOptions,
                disconnect ? ClientModRules.WordDisconnect : ClientModRules.WordWarn, width,
                LockedBy(SettingPlaces.OpMidSession) != null, v => SetMidSession(v), LockNote(SettingPlaces.OpMidSession)));
        }

        panel.CellRow(JoinFlowLayout.Line("ja-honesty", ClientPolicyText.Honesty, CellTone.Amber, width, align: CellAlign.Left, fontSize: 14f));
    }

    /// <summary>
    /// C3: the Version matching radio list (read-only under the lock list or the server's cfg), then the mods the server runs,
    /// each with a Rule button that cycles its own rule (staged like every edit here).
    /// </summary>
    private static void BuildVersionMatching(IUiPanel panel, JoinAdminState s, float width)
    {
        panel.CellRow(SectionHeading(RowMatchHead, VersionMatchingText.Heading, ""));
        LockedLine(panel, SettingPlaces.OpVersionMatching, width);
        string? forcedNote = MatchingForcedNote(s);
        if (forcedNote != null && LockedBy(SettingPlaces.OpVersionMatching) == null)
            panel.CellRow(JoinFlowLayout.Line("ja-match-forced", forcedNote, CellTone.Amber, width, align: CellAlign.Left, fontSize: 14f));
        bool locked = LockedBy(SettingPlaces.OpVersionMatching) != null;
        Radios.Add(UiRadioList.Draw(panel, RadioMatch, VersionMatchText.Options, Draft.Word(JoinAdminChangeKind.VersionMatching), width,
            locked || forcedNote != null, v => SetVersionMatching(v), LockNote(SettingPlaces.OpVersionMatching) ?? forcedNote));
        // Mod compatibility C4: what Exact means for Progenitor on this server (its build, or why it is not enforced).
        string progenitorLine = ProgenitorExact.AdminLine(s, Draft.Word(JoinAdminChangeKind.VersionMatching));
        if (progenitorLine.Length > 0)
            panel.CellRow(JoinFlowLayout.Line("ja-match-progenitor", progenitorLine,
                progenitorLine.StartsWith(ProgenitorExact.NotEnforcedPrefix, StringComparison.Ordinal) ? CellTone.Amber : CellTone.Muted,
                width, align: CellAlign.Left, fontSize: 14f));

        var rows = Draft.ServerModRows();
        int rules = 0;
        foreach (var r in rows) if (r.Row.Rule.Length > 0) rules++;
        panel.CellRow(SectionHeading(RowServerModsHead, VersionMatchText.ModsHeading, VersionMatchText.RuleCount(rules)));
        panel.CellRow(JoinFlowLayout.Line("ja-smr-help", VersionMatchingText.ModRulesHelp, CellTone.Muted, width, align: CellAlign.Left, fontSize: 14f));
        if (rows.Count == 0)
            panel.CellRow(JoinFlowLayout.Line("ja-smr-none", VersionMatchText.NoMods, CellTone.Muted, width, align: CellAlign.Left, fontSize: 14f));
        foreach (var r in rows) ServerModRow(panel, r.Row, r.Mark, locked);
    }

    /// <summary>C3: the note while the server's cfg forces Version matching, or null.</summary>
    internal static string? MatchingForcedNote(JoinAdminState? s) =>
        s is { MatchingSupported: true, VersionMatchingForced: true } && VersionMatchingText.TryParse(s.VersionMatching, out var mode)
            ? VersionMatchCfg.ForcedNote(mode) : null;

    private static void ServerModRow(IUiPanel panel, JoinAdminServerModRow r, JoinAdminRowMark mark, bool locked)
    {
        string id = r.Id;
        string headKey = ServerModRowPrefix + id;
        // A stored rule for a mod the server no longer runs can only be cleared: its button sets the server setting.
        var head = panel.CellRow(JoinAdminRowLayout.Head(headKey, r.Display, VersionMatchText.Second(r), VersionMatchText.RuleCaption(r.Rule),
            removable: false, locked, mark));
        HeadRows[headKey] = head;
        if (locked) return;
        bool runs = r.Runs;
        head.OnClick(CellRule, () => SetServerModRule(id, runs ? null : VersionMatchText.WordDefault));
    }

    private static void RequiredRow(IUiPanel panel, JoinAdminClientRow r, JoinAdminRowMark mark, bool locked = false)
    {
        string key = r.Key;
        // WIDEDIALOG1: two lines (JoinAdminRowLayout).
        string headKey = "ja-req-" + key;
        var head = panel.CellRow(JoinAdminRowLayout.Head(headKey, ClientPolicyText.NameOf(r), ClientPolicyText.RequiredSecond(r),
            ClientPolicyText.RuleCaption(r.RuleValue), removable: true, locked, mark));
        HeadRows[headKey] = head;
        if (mark == JoinAdminRowMark.Removed)
        {
            ReqRows[key] = panel.CellRow(JoinAdminRowLayout.RemovedReason(headKey, r.Reason));
            if (!locked) head.OnClick(CellUndo, () => UndoRemoval(JoinAdminChangeKind.RequiredRemove, key));
            return;
        }

        var row = panel.CellRow(JoinAdminRowLayout.Reason(headKey, r.Reason));
        ReqRows[key] = row;
        if (locked) return;
        head.OnClick(CellRule, () => SetRequiredRule(key, ""));
        head.OnClick(CellRemove, () => RemoveRequired(key));
        row.OnChanged(CellReason, text => SetRequiredReason(key, text ?? ""));
    }

    private static void ApprovedRow(IUiPanel panel, JoinAdminClientRow r, JoinAdminRowMark mark, bool locked = false)
    {
        string key = r.Key;
        // WIDEDIALOG1: the same name line as Required (no reason: approved mods have none).
        string headKey = "ja-app-" + key;
        var row = panel.CellRow(JoinAdminRowLayout.Head(headKey, ClientPolicyText.NameOf(r), ClientPolicyText.ApprovedSecond(r), null, removable: true, locked, mark));
        HeadRows[headKey] = row;
        if (locked) return;
        if (mark == JoinAdminRowMark.Removed) row.OnClick(CellUndo, () => UndoRemoval(JoinAdminChangeKind.ApproveRemove, key));
        else row.OnClick(CellRemove, () => RemoveApproved(key));
    }

    private static void BuildConfirmApproveAll(IUiPanel panel, float width)
    {
        Ui.Ui.PinMenuRows(panel, MenuRowPin.Bottom);
        _statusRow = panel.CellRow(JoinFlowLayout.Line(RowStatus, StatusText(out var tone), tone, width));
        var buttons = new List<DialogButton> { new DialogButton(ButtonBack, JoinAdminText.Back, DialogButtonRole.Cancel) };
        if (_confirmItems.Count > 0) buttons.Add(new DialogButton(ButtonConfirm, ClientPolicyText.ConfirmApprove, DialogButtonRole.Main));
        var row = panel.CellRow(DialogLayout.ButtonRowSpec(RowButtons, buttons, width));
        row.OnClick(ButtonBack, () => Escape());
        if (_confirmItems.Count > 0) row.OnClick(ButtonConfirm, () => ConfirmApproveEverything());
    }

    /// <summary>JOINDRAFT1: Close with unapplied changes: the changes, then Cancel / Discard / Apply.</summary>
    private static void BuildConfirmClose(IUiPanel panel, float width)
    {
        var lines = Draft.Lines();
        for (int i = 0; i < lines.Count; i++)
            panel.CellRow(JoinFlowLayout.Line("ja-close-line-" + i.ToString(CultureInfo.InvariantCulture), "· " + lines[i], CellTone.Normal, width,
                align: CellAlign.Left, fontSize: 15f));
        Ui.Ui.PinMenuRows(panel, MenuRowPin.Bottom);
        _statusRow = panel.CellRow(JoinFlowLayout.Line(RowStatus, StatusText(out var tone), tone, width));
        var row = panel.CellRow(DialogLayout.ButtonRowSpec(RowButtons, JoinAdminRowLayout.CloseButtons(), width));
        _buttonsRow = row;
        row.OnClick(ButtonCancel, () => ShowPage(Page.Main));
        row.OnClick(ButtonDiscard, () => Discard());
        row.OnClick(ButtonApply, () => ApplyAndClose());
    }

    private static void BuildPickRecommend(IUiPanel panel, float width)
    {
        var search = panel.CellRow(new CellRowSpec(RowSearch, new List<CellSpec>
        {
            new CellSpec(CellKind.Input, CellInput) { MaxLength = 64 }.With(text: _search, width: CellWidth.Fill(200f), skin: CellSkin.ValueBox,
                placeholder: JoinAdminText.SearchPlaceholder),
        }, height: 40f, plain: true));
        search.OnChanged(CellInput, text => SetSearch(text ?? ""));
        // A fixed set of rows rewritten in place as the search changes, so typing never rebuilds the panel.
        for (int i = 0; i < PickSlots; i++)
        {
            int index = i;
            var row = panel.CellRow(new CellRowSpec("ja-pick-" + i.ToString(CultureInfo.InvariantCulture), new List<CellSpec>
            {
                new CellSpec(CellKind.Text, CellName).With(text: " \n ", wrap: CellWrap.TwoLine, width: CellWidth.Fill(150f)),
                new CellSpec(CellKind.Button, CellAction).With(text: PickCaption(), width: CellWidth.Px(150f),
                    fontSize: DialogLayout.ButtonFontSize, skin: CellSkin.PopupButton, tone: CellTone.Gold),
            }, twoLine: true, plain: true));
            row.OnClick(CellAction, () => PickAt(index));
            Slots.Add(row);
        }

        FillSlots();
        StatusAndButtons(panel, width, back: true);
    }

    private static string PickCaption() => _page switch
    {
        Page.PickRequire => ClientPolicyText.Require,
        Page.PickApprove => ClientPolicyText.Approve,
        _ => JoinAdminText.Recommend,
    };

    private static void FillSlots()
    {
        for (int i = 0; i < Slots.Count; i++)
        {
            var row = Slots[i];
            try
            {
                if (i < Picks.Count)
                {
                    var p = Picks[i];
                    var parts = new List<string> { p.Installed ? "Installed" : "Thunderstore" };
                    if (p.Package.Length > 0) parts.Add(p.Package);
                    if (p.Version.Length > 0) parts.Add(p.Version);
                    row.SetCell(CellName, text: p.Name + "\n" + string.Join(" · ", parts), visible: true);
                    row.SetCell(CellAction, visible: true);
                }
                else
                {
                    row.SetCell(CellName, text: i == 0 ? JoinAdminText.NoCandidates + "\n " : " \n ", visible: i == 0);
                    row.SetCell(CellAction, visible: false);
                }
            }
            catch { /* a row that went away with a rebuild */ }
        }
    }

    private static void SingleButton(IUiPanel panel, string key, string caption, float width, Func<string> action, bool enabled)
    {
        float w = Math.Max(DialogLayout.CaptionWidth(caption) + 40f, 220f);
        var row = panel.CellRow(new CellRowSpec("ja-" + key, new List<CellSpec>
        {
            new CellSpec(CellKind.Button, key).With(text: caption, width: CellWidth.Px(w), fontSize: DialogLayout.ButtonFontSize,
                skin: CellSkin.PopupButton, tone: enabled ? CellTone.Normal : CellTone.Disabled),
            new CellSpec(CellKind.Spacer, "rest"),
        }, height: 40f, plain: true));
        if (enabled) row.OnClick(key, () => action());
    }

    private static void StatusAndButtons(IUiPanel panel, float width, bool back)
    {
        // Live 2026-10-04: with the alert shown the body was taller than the popup and Close was cut off under the
        // version footer. The status line and the buttons are pinned under the scrolling body, so they always show.
        Ui.Ui.PinMenuRows(panel, MenuRowPin.Bottom);
        if (!back) BuildChanges(panel, width);
        _statusRow = panel.CellRow(JoinFlowLayout.Line(RowStatus, StatusText(out var tone), tone, width));
        // JOINDRAFT1: Apply / Discard / Close on the main page.
        var buttons = back
            ? new List<DialogButton> { new DialogButton(ButtonBack, JoinAdminText.Back, DialogButtonRole.Cancel) }
            : JoinAdminRowLayout.MainButtons(Draft.HasChanges && !Applying);
        var row = panel.CellRow(DialogLayout.ButtonRowSpec(RowButtons, buttons, width));
        _buttonsRow = row;
        if (back) row.OnClick(ButtonBack, () => ShowPage(Page.Main));
        else
        {
            row.OnClick(ButtonClose, () => Close());
            row.OnClick(ButtonDiscard, () => Discard());
            row.OnClick(ButtonApply, () => Apply());
        }
    }

    /// <summary>
    /// JOINDRAFT1: the Changes list above the buttons: a heading with the count, "No changes" or one line per change with
    /// its ×, and <c>+N more</c> past <see cref="JoinAdminRowLayout.ChangeSlots"/>. The lines are fixed rows rewritten in
    /// place (<see cref="RefreshChanges"/>), so staging a typed reason never rebuilds the panel.
    /// </summary>
    private static void BuildChanges(IUiPanel panel, float width)
    {
        // CHANGESFIT1: one-line rows sized to their text; the unused ones are hidden and take no room in the footer.
        _changesHead = panel.CellRow(JoinAdminRowLayout.ChangesHeading(RowChangesHead, Draft.Count));
        _noChangesRow = panel.CellRow(JoinAdminRowLayout.FooterLine(RowNoChanges, JoinAdminDraft.NoChanges, CellTone.Muted));
        for (int i = 0; i < JoinAdminRowLayout.ChangeSlots; i++)
        {
            int index = i;
            var row = panel.CellRow(JoinAdminRowLayout.ChangeLine(i, " ", CellTone.Normal));
            row.OnClick(CellUndo, () => UndoChange(index));
            ChangeRows.Add(row);
        }

        _moreChangesRow = panel.CellRow(JoinAdminRowLayout.FooterLine(RowMoreChanges, " ", CellTone.Muted));
        RefreshChanges();
    }

    /// <summary>JOINDRAFT1: write the Changes list, the count and the buttons' tones from the draft, in place.</summary>
    private static void RefreshChanges()
    {
        int n = Draft.Count;
        try { _changesHead?.SetCell(JoinFlowLayout.CellCount, text: JoinAdminText.ChangesCount(n)); }
        catch { /* the next draw shows it */ }
        SetRowVisible(_noChangesRow, n == 0);
        // CHANGESFIT1: a clean draft collapses to the single "No changes" line.
        SetRowVisible(_changesHead, n > 0);
        for (int i = 0; i < ChangeRows.Count; i++)
        {
            var row = ChangeRows[i];
            bool shown = i < n;
            try
            {
                if (shown)
                {
                    var c = Draft.Changes[i];
                    row.SetCell(CellText, text: JoinAdminDraft.LineOf(c), tone: JoinAdminRowLayout.ChangeTone(c));
                    row.SetCell(CellUndo, visible: !Applying);
                }
            }
            catch { /* a row that went away */ }
            SetRowVisible(row, shown);
        }

        int more = n - ChangeRows.Count;
        if (_moreChangesRow != null)
        {
            try { _moreChangesRow.SetCell(CellText, text: more > 0 ? JoinAdminText.MoreChanges(more) : " "); }
            catch { /* */ }
            SetRowVisible(_moreChangesRow, more > 0);
        }

        if (_buttonsRow != null && _page == Page.Main)
        {
            bool can = n > 0 && !Applying;
            try
            {
                _buttonsRow.SetCell(ButtonApply, tone: can ? CellTone.Gold : CellTone.Disabled);
                _buttonsRow.SetCell(ButtonDiscard, tone: can ? CellTone.Normal : CellTone.Disabled);
            }
            catch { /* */ }
        }
    }

    private static void SetRowVisible(IUiCellRow? row, bool visible)
    {
        if (row == null) return;
        try { if (row.Visible != visible) row.Visible = visible; }
        catch { /* a row that went away */ }
    }

    /// <summary>JOINDRAFT1: each drawn mod row's marker from the draft (a typed reason marks its row Changed in place).</summary>
    private static void RefreshMarks()
    {
        void Mark(string headKey, JoinAdminRowMark mark)
        {
            if (!HeadRows.TryGetValue(headKey, out var head)) return;
            try { head.SetCell(CellMark, text: JoinAdminRowLayout.MarkText(mark), tone: JoinAdminRowLayout.MarkTone(mark), visible: mark != JoinAdminRowMark.None); }
            catch { /* the next draw shows it */ }
        }

        foreach (var r in Draft.RecommendedRows()) Mark("ja-rec-" + r.Row.Key, r.Mark);
        foreach (var r in Draft.RequiredRows()) Mark("ja-req-" + r.Row.Key, r.Mark);
        foreach (var r in Draft.ApprovedRows()) Mark("ja-app-" + r.Row.Key, r.Mark);
        foreach (var r in Draft.ServerModRows()) Mark(ServerModRowPrefix + r.Row.Id, r.Mark);
    }

    /// <summary>The line above the buttons: a local refusal, Apply's progress or result, else the server's last error.</summary>
    internal static string StatusText(out CellTone tone)
    {
        tone = CellTone.Muted;
        if (_localStatus.Length > 0)
        {
            tone = CellTone.Orange;
            return _localStatus;
        }

        if (_run is { Running: true } run)
            return JoinAdminText.Applying + " " + (run.Applied + run.Failed).ToString(CultureInfo.InvariantCulture) + " of "
                + run.Total.ToString(CultureInfo.InvariantCulture);
        if (_applyStatus.Length > 0)
        {
            tone = _applyTone;
            return _applyStatus;
        }

        if (JoinAdminClient.PendingCount > 0) return JoinAdminText.Loading;
        if (!JoinAdminClient.LastOk && JoinAdminClient.LastOp.Length > 0 && JoinAdminClient.LastError.Length > 0)
        {
            tone = CellTone.Orange;
            return JoinAdminClient.LastError;
        }

        return " ";
    }

    private static void RefreshStatus()
    {
        if (_statusRow == null) return;
        try
        {
            string text = StatusText(out var tone);
            _statusRow.SetCell(CellText, text: text, tone: tone);
        }
        catch { /* the next draw shows it */ }
    }

    // ---- readback --------------------------------------------------------------------------

    /// <summary>
    /// The panel as the bridge reads it: what is drawn and the state behind it. The top-level lists are the server's
    /// state; JOINDRAFT1's <c>draft</c> is the staged state (rows with their marks) and <c>changes</c> the Changes list.
    /// </summary>
    internal static Dictionary<string, object?> Describe()
    {
        var s = JoinAdminClient.State;
        var d = new Dictionary<string, object?>
        {
            ["open"] = Owned.IsOpen,
            ["page"] = PageName(_page),
            ["buttonVisible"] = JoinAdminClient.IsAdmin,
            ["serverSupports"] = JoinAdminClient.ServerSupports,
            ["helloAdmin"] = JoinAdminClient.HelloAdmin,
            ["pending"] = JoinAdminClient.PendingCount,
            ["answers"] = JoinAdminClient.Answers,
            ["lastOp"] = JoinAdminClient.LastOp,
            ["lastOk"] = JoinAdminClient.LastOk,
            ["lastError"] = JoinAdminClient.LastError,
            ["lastNote"] = JoinAdminClient.LastNote,
            ["status"] = StatusText(out _),
            ["hasState"] = s != null,
            ["applying"] = Applying,
            ["hasChanges"] = Draft.HasChanges,
            ["changeCount"] = Draft.Count,
            ["changes"] = Draft.Describe(),
            ["changesLines"] = Draft.HasChanges ? Draft.Lines() : new List<string> { JoinAdminDraft.NoChanges },
        };
        if (_run != null)
            d["apply"] = new Dictionary<string, object?>
            {
                ["running"] = _run.Running, ["finished"] = _run.Finished, ["total"] = _run.Total, ["applied"] = _run.Applied, ["failed"] = _run.Failed,
                ["results"] = new List<string>(_run.Results), ["summary"] = _run.Finished ? _run.Summary() : null,
                ["requests"] = StepOps(_run),
            };
        // RADIO1: the pick-one choices as drawn (kind radio, current, options with their explanations).
        var radios = new List<object?>(Radios.Count);
        foreach (var r in Radios) radios.Add(r.Describe());
        d["radios"] = radios;
        var drawnChanges = new List<object?>();
        foreach (var row in ChangeRows)
        {
            try { if (row.Visible) drawnChanges.Add(row.Describe()); }
            catch { /* a row that went away */ }
        }

        d["drawnChanges"] = drawnChanges;
        if (s == null) return d;
        var alert = new List<object?>();
        foreach (var a in s.Alert)
        {
            bool update = false;
            string latest = "";
            try { update = ModsMenu.JoinAdminServerUpdate(a.Mod, start: false, out _, out latest, out _); }
            catch { update = false; }
            alert.Add(new Dictionary<string, object?>
            {
                ["mod"] = a.Mod, ["name"] = a.Name, ["required"] = a.Required, ["had"] = a.Had, ["kind"] = a.Kind,
                ["count"] = a.Count, ["text"] = a.Text, ["updateOnServer"] = update, ["latest"] = latest,
                ["approved"] = a.Approved, ["approveButton"] = a.CanApprove,
            });
        }

        var rec = new List<object?>();
        foreach (var r in s.Recommended)
            rec.Add(new Dictionary<string, object?>
            {
                ["key"] = r.Key, ["id"] = r.Id, ["name"] = r.Name, ["package"] = r.Package, ["version"] = r.Version, ["reason"] = r.Reason,
                ["fromFile"] = r.FromFile,
            });
        var picks = new List<object?>();
        foreach (var p in Picks)
            picks.Add(new Dictionary<string, object?> { ["id"] = p.Id, ["package"] = p.Package, ["version"] = p.Version, ["name"] = p.Name, ["installed"] = p.Installed });
        d["alert"] = new Dictionary<string, object?> { ["refusedJoins"] = s.RefusedJoins, ["days"] = s.RefusedDays, ["rows"] = alert };
        d["recommended"] = rec;
        d["required"] = new List<string>(s.Required);
        d["windowSeconds"] = s.WindowSeconds;
        d["search"] = _search;
        d["picks"] = picks;
        var drawn = new List<object?>();
        foreach (var pair in RecRows)
        {
            try { drawn.Add(pair.Value.Describe()); }
            catch { /* a row that went away */ }
        }

        d["drawnRecommended"] = drawn;
        // WIDEDIALOG1: the name lines of the two-line mod rows (ja-rec-, ja-req-, ja-app-), and the frame asked for.
        var heads = new List<object?>();
        foreach (var pair in HeadRows)
        {
            try { heads.Add(pair.Value.Describe()); }
            catch { /* a row that went away */ }
        }

        d["drawnHeads"] = heads;
        d["frameWidth"] = DialogLayout.WideFrameWidth;
        d["contentWidth"] = DialogLayout.ContentRowWidth(DialogLayout.WideFrameWidth);

        // CLIENTPOLICY1.
        var required = new List<object?>();
        foreach (var r in s.RequiredClient)
            required.Add(new Dictionary<string, object?>
            {
                ["key"] = r.Key, ["id"] = r.Id, ["package"] = r.Package, ["version"] = r.Version, ["rule"] = r.Rule, ["reason"] = r.Reason,
                ["name"] = r.Name, ["second"] = ClientPolicyText.RequiredSecond(r), ["ruleLabel"] = ClientModRules.Label(r.RuleValue, r.Version),
            });
        var approved = new List<object?>();
        foreach (var r in s.Approved)
            approved.Add(new Dictionary<string, object?>
            {
                ["key"] = r.Key, ["id"] = r.Id, ["package"] = r.Package, ["version"] = r.Version, ["rule"] = r.Rule, ["name"] = r.Name,
                ["second"] = ClientPolicyText.ApprovedSecond(r),
            });
        var confirm = new List<object?>();
        foreach (var x in _confirmItems) confirm.Add(new Dictionary<string, object?> { ["id"] = x.Id, ["package"] = x.Package, ["name"] = x.Name });
        var drawnRequired = new List<object?>();
        foreach (var pair in ReqRows)
        {
            try { drawnRequired.Add(pair.Value.Describe()); }
            catch { /* a row that went away */ }
        }

        d["clientPolicy"] = new Dictionary<string, object?>
        {
            ["supported"] = s.PolicySupported, ["mode"] = s.PolicyMode, ["unverified"] = s.Unverified,
            ["midSession"] = s.MidSession,
            ["midSessionHelp"] = s.MidSession.Length == 0 ? null
                : string.Equals(s.MidSession, ClientModRules.WordDisconnect, StringComparison.OrdinalIgnoreCase) ? ClientPolicyText.MidSessionDisconnectHelp : ClientPolicyText.MidSessionWarnHelp,
            ["required"] = required, ["approved"] = approved, ["approvedFrom"] = s.ApprovedFrom, ["approvedTotal"] = s.ApprovedTotal,
            ["confirmItems"] = confirm, ["confirmBody"] = _page == Page.ConfirmApproveAll ? ClientPolicyText.ConfirmBody(_confirmItems) : null,
            ["alwaysApprovedLine"] = ClientPolicyText.AlwaysApproved, ["honestyLine"] = ClientPolicyText.Honesty,
            ["drawnRequired"] = drawnRequired,
        };
        d["versionMatching"] = DescribeMatching(s);
        d["draft"] = DescribeDraft();
        d["report"] = ClientModReportClient.Describe(withEntries: false);
        // The joined server's lock list: each of this popup's lists and choices, with the entry that locks it (null: editable).
        var locked = new Dictionary<string, object?>();
        foreach (string key in new[] { SettingPlaces.OpRecommended, SettingPlaces.OpRequiredClient, SettingPlaces.OpPolicyMode, SettingPlaces.OpApproved,
                     SettingPlaces.OpUnverified, SettingPlaces.OpMidSession, SettingPlaces.OpWindow, SettingPlaces.OpVersionMatching })
            locked[key] = LockedBy(key);
        d["locked"] = locked;
        d["lock"] = new List<string>(s.Lock);
        return d;
    }

    /// <summary>
    /// C3: Version matching as the server said it (<c>mode</c>, <c>forced</c> and its note, the help of the mode, why it is
    /// read-only), and the mods the server runs with their rules (<c>mods</c>) and their drawn rows (<c>drawnMods</c>).
    /// </summary>
    internal static Dictionary<string, object?> DescribeMatching(JoinAdminState s)
    {
        var mods = new List<object?>();
        foreach (var r in s.ServerMods)
            mods.Add(new Dictionary<string, object?>
            {
                ["id"] = r.Id, ["name"] = r.Name, ["version"] = r.Version, ["rule"] = r.Rule.Length > 0 ? r.Rule : VersionMatchText.WordDefault,
                ["ruleLabel"] = VersionMatchText.RuleLabel(r.Rule, r.Version), ["caption"] = VersionMatchText.RuleCaption(r.Rule),
                ["second"] = VersionMatchText.Second(r), ["runs"] = r.Runs,
            });
        var drawn = new List<object?>();
        foreach (var pair in HeadRows)
        {
            if (!pair.Key.StartsWith(ServerModRowPrefix, StringComparison.Ordinal)) continue;
            try { drawn.Add(pair.Value.Describe()); }
            catch { /* a row that went away */ }
        }

        string? forced = MatchingForcedNote(s);
        string? lockedBy = LockedBy(SettingPlaces.OpVersionMatching);
        return new Dictionary<string, object?>
        {
            ["supported"] = s.MatchingSupported,
            ["mode"] = s.VersionMatching,
            ["label"] = VersionMatchingText.TryParse(s.VersionMatching, out var m) && s.MatchingSupported ? VersionMatchingText.Label(m) : null,
            ["help"] = s.MatchingSupported && VersionMatchingText.TryParse(s.VersionMatching, out var h) ? VersionMatchingText.Help(h) : null,
            ["forced"] = s.VersionMatchingForced,
            ["forcedNote"] = forced,
            ["lockedWhy"] = lockedBy != null ? ServerSettingsLock.NoteFor(lockedBy) : forced,
            ["modRulesHelp"] = VersionMatchingText.ModRulesHelp,
            // C4: the server's Progenitor line (what Exact means for Progenitor here), whatever the mode.
            ["progenitor"] = s.ProgenitorExactLine,
            ["mods"] = mods,
            ["drawnMods"] = drawn,
        };
    }

    /// <summary>JOINDRAFT1: the staged state: each list's rows with their marks, and the choices.</summary>
    internal static Dictionary<string, object?> DescribeDraft()
    {
        static Dictionary<string, object?> Client(JoinAdminDraftRow<JoinAdminClientRow> x) => new()
        {
            ["key"] = x.Row.Key, ["id"] = x.Row.Id, ["package"] = x.Row.Package, ["version"] = x.Row.Version, ["rule"] = x.Row.Rule,
            ["reason"] = x.Row.Reason, ["name"] = x.Row.Name, ["mark"] = JoinAdminDraft.MarkName(x.Mark),
            ["ruleLabel"] = ClientModRules.Label(x.Row.RuleValue, x.Row.Version),
        };

        var rec = new List<object?>();
        foreach (var x in Draft.RecommendedRows())
            rec.Add(new Dictionary<string, object?>
            {
                ["key"] = x.Row.Key, ["id"] = x.Row.Id, ["package"] = x.Row.Package, ["version"] = x.Row.Version, ["reason"] = x.Row.Reason,
                ["name"] = x.Row.Name, ["fromFile"] = x.Row.FromFile, ["mark"] = JoinAdminDraft.MarkName(x.Mark),
            });
        var req = new List<object?>();
        foreach (var x in Draft.RequiredRows()) req.Add(Client(x));
        var app = new List<object?>();
        foreach (var x in Draft.ApprovedRows()) app.Add(Client(x));
        var smr = new List<object?>();
        foreach (var x in Draft.ServerModRows())
            smr.Add(new Dictionary<string, object?>
            {
                ["id"] = x.Row.Id, ["name"] = x.Row.Name, ["version"] = x.Row.Version,
                ["rule"] = x.Row.Rule.Length > 0 ? x.Row.Rule : VersionMatchText.WordDefault, ["ruleLabel"] = VersionMatchText.RuleLabel(x.Row.Rule, x.Row.Version),
                ["runs"] = x.Row.Runs, ["mark"] = JoinAdminDraft.MarkName(x.Mark),
            });
        return new Dictionary<string, object?>
        {
            ["recommended"] = rec, ["required"] = req, ["approved"] = app, ["approvedTotal"] = Draft.ApprovedTotalAfter(),
            ["mode"] = Draft.Word(JoinAdminChangeKind.PolicyMode), ["unverified"] = Draft.Word(JoinAdminChangeKind.Unverified),
            ["midSession"] = Draft.Word(JoinAdminChangeKind.MidSession), ["windowSeconds"] = Draft.WindowSeconds,
            ["versionMatching"] = Draft.Word(JoinAdminChangeKind.VersionMatching), ["serverMods"] = smr,
            ["equalsServer"] = !Draft.HasChanges,
        };
    }

    private static List<object?> StepOps(JoinAdminApplyRun run)
    {
        var list = new List<object?>();
        foreach (var step in run.StepsSent)
            list.Add(new Dictionary<string, object?>
            {
                ["op"] = step.Request.Op, ["changes"] = step.Changes.Count, ["bulk"] = step.Bulk,
            });
        return list;
    }

    internal static void Uninstall()
    {
        if (_subscribed)
        {
            JoinAdminClient.Changed -= OnChanged;
            _subscribed = false;
        }

        _wanted = false;
        Drop();
        Draft.Reset(null);
        _run = null;
        _confirmItems = new List<JoinAdminBulkItem>();
        Picks.Clear();
    }
}
