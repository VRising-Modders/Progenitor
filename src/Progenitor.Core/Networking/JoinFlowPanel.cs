using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// JOINFLOW1: the row specs of the join screens, game-free so the geometry and the keys have tests. Every
/// untrusted string (the message, mod names, the admins' reasons) goes into a cell, whose text the runtime
/// always escapes (<c>UiRichText.Literal</c>), so a message cannot carry markup.
/// </summary>
internal static class JoinFlowLayout
{
    internal const string MessageRow = "join-message";
    internal const string MessageReaderRow = "join-message-reader";
    internal const string CellText = "text";
    internal const string MessageFooterRow = "join-message-footer";
    internal const string SummaryRow = "join-summary";
    internal const string DontShowRow = "join-dont-show";
    internal const string CellCheck = "check";
    internal const string CellLabel = "label";
    internal const string RequiredHeadRow = "join-required-head";
    internal const string RecommendedHeadRow = "join-recommended-head";
    internal const string CellCount = "count";
    internal const string NoticeRow = "join-notice";
    internal const string ModsChangedRow = "join-mods-changed";
    internal const string CoreRefusedRow = "join-core-refused";
    internal const string CoreBodyRow = "join-core-body";
    internal const string ButtonUpdateProgenitor = "update-progenitor";
    internal const string FooterRow = "join-footer";
    internal const string CellAction = "action";
    internal const string CellName = "name";
    internal const string TrustErrorRow = "join-trust-error";
    internal const string ButtonsRow = "join-buttons";
    internal const string ButtonLeave = "leave";
    internal const string ButtonContinue = "continue";

    /// <summary>The message page's quiet "View mods" (offered only when the mods page would not follow Continue).</summary>
    internal const string ButtonViewMods = "view-mods";

    /// <summary>
    /// Whether the message page offers Leave: only before the player is in the world (a refused join or an install
    /// window), where it is the way out. In the world and in an admin's preview it is not drawn (requested 2026-10-06:
    /// "There shouldn't be a leave button here. It takes the player back to the title screen."); Continue closes the
    /// page and the game's own menu leaves.
    /// </summary>
    internal static bool OffersLeave(JoinFlowSession session) =>
        !session.IsPreview && session.Mode != JoinMode.InWorld;

    /// <summary>The message page's buttons: Leave (only before the world), View mods when offered, Continue.</summary>
    internal static List<DialogButton> MessageButtons(JoinFlowSession session)
    {
        var list = new List<DialogButton>(3);
        if (OffersLeave(session)) list.Add(new DialogButton(ButtonLeave, JoinText.LeaveButton, DialogButtonRole.Cancel));
        if (session.CanViewMods) list.Add(new DialogButton(ButtonViewMods, JoinText.ViewModsButton, DialogButtonRole.Option, CellTone.Normal));
        // A Core refusal the self-update can fix: Update Progenitor is the main button, Continue (to the mods page) steps back.
        bool update = session.OffersUpdateProgenitor;
        list.Add(new DialogButton(ButtonContinue, JoinText.ContinueButton, update ? DialogButtonRole.Option : DialogButtonRole.Main,
            update ? CellTone.Normal : CellTone.Gold));
        if (update) list.Add(new DialogButton(ButtonUpdateProgenitor, JoinText.UpdateProgenitorButton, DialogButtonRole.Main, CellTone.Gold));
        return list;
    }
    internal const string ButtonBack = "back";
    internal const string ButtonClose = "close";
    internal const string ButtonInstall = "install";

    // JOINFLOW2
    internal const string ButtonTsInstall = "ts-install";
    internal const string ButtonJoinNow = "join-now";
    internal const string ButtonRestart = "restart";
    internal const string ButtonLater = "later";
    internal const string ButtonCancel = "cancel";
    internal const string TsKeyChangedRow = "join-ts-key-changed";
    internal const string TsWarningRow = "join-ts-warning";
    internal const string ResultBodyRow = "join-result-body";
    internal const string RejoinStatusRow = "join-rejoin-status";
    internal const string RejoinRequiredHeadRow = "join-rejoin-required-head";
    internal const string RejoinRecommendedRow = "join-rejoin-recommended";

    internal static string RejoinSummaryRow(int i) => "join-rejoin-row-" + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>JOINFLOW2: the Result page's title.</summary>
    internal static string ResultTitle(JoinResultOffer o) =>
        o != null && o.InPlace ? JoinText.InPlaceTitle :
        o != null && o.WhenBack ? JoinText.RestartingTitle(o.RetryTarget == null ? "" : JoinText.ServerLabel(o.RetryTarget.ServerName, o.RetryTarget.Endpoint)) :
        o != null && o.Kind == JoinAfterInstall.JoinNow ? JoinText.JoinNowTitle : JoinText.RestartTitle;

    /// <summary>JOINFLOW2: the Result page's body (with the manual-start sentence when the relauncher cannot run).</summary>
    internal static string ResultBody(JoinResultOffer o)
    {
        if (o == null) return "";
        if (o.InPlace) return o.Summary;
        if (o.WhenBack) return JoinText.RestartingBody;
        if (o.Kind == JoinAfterInstall.JoinNow) return JoinText.JoinNowBody;
        if (o.Hosted) return JoinText.RestartHosted;
        if (!o.CanRejoin) return JoinText.RestartNoAddress;
        return o.ManualRelaunch ? JoinText.RestartBody + " " + JoinText.RestartManual : JoinText.RestartBody;
    }

    /// <summary>JOINFLOW2: the Result page's button keys, in order.</summary>
    internal static IReadOnlyList<string> ResultButtonKeys(JoinResultOffer o)
    {
        if (o == null) return new[] { ButtonLater };
        if (o.Kind == JoinAfterInstall.JoinNow) return new[] { ButtonLater, ButtonJoinNow };
        if (o.Kind == JoinAfterInstall.RestartNeeded && o.CanRejoin) return new[] { ButtonLater, ButtonRestart };
        return new[] { ButtonLater };
    }

    internal static List<DialogButton> ResultButtons(JoinResultOffer o)
    {
        var keys = ResultButtonKeys(o);
        var list = new List<DialogButton>(keys.Count);
        foreach (string k in keys)
        {
            list.Add(k switch
            {
                ButtonJoinNow => new DialogButton(k, o?.WhenBack == true ? JoinText.RejoinWhenBackButton : o?.JoinAgain == true ? JoinText.JoinAgainButton : JoinText.JoinNowButton, DialogButtonRole.Main, CellTone.Gold),
                ButtonRestart => new DialogButton(k, JoinText.RestartRejoinButton, DialogButtonRole.Main, CellTone.Gold),
                _ => new DialogButton(k, o?.InPlace == true ? JoinText.DoneButton : keys.Count == 1 ? JoinText.CloseButton : JoinText.LaterButton, keys.Count == 1 ? DialogButtonRole.Main : DialogButtonRole.Cancel),
            });
        }

        return list;
    }

    /// <summary>JOINFLOW2: the Thunderstore confirmation's facts: the server, its key, then each package and version.</summary>
    internal static List<DialogFact> TsTrustFacts(JoinThunderstoreFacts t)
    {
        var facts = new List<DialogFact>
        {
            new("Server", ServerFact(t.ServerName, t.Host)),
            new("Signed by", t.Fingerprint.Length > 0
                ? "key " + ModText.ShortKey(t.Fingerprint) + " · " + t.FirstSeen
                : "no operator key"),
        };
        bool first = true;
        foreach (var r in t.Requests)
        {
            string kind = r.Kind switch
            {
                JoinInstallKind.Update => " · update",
                // JOINFLOW4: the newer copy is kept for other servers; replaced only when it cannot be kept.
                JoinInstallKind.UseVersion => r.Keeps
                    ? " · keeps your " + (r.Replaces.Length > 0 ? r.Replaces : "newer copy")
                    : " · replaces " + (r.Replaces.Length > 0 ? r.Replaces : "your newer copy"),
                JoinInstallKind.Recommended => " · recommended",
                _ => "",
            };
            facts.Add(new(first ? (t.Requests.Count == 1 ? "Mod" : "Mods") : "",
                r.Name + " " + (r.Version.Length > 0 ? r.Version : "newest") + " · " + r.Package + kind,
                r.Kind == JoinInstallKind.UseVersion && !r.Keeps ? DialogFactTone.Warn : DialogFactTone.Normal));
            first = false;
        }

        facts.AddRange(JoinText.TurnOnFacts(t));
        return facts;
    }

    /// <summary>JOINFLOW2: the "Use version X" warnings of a confirmation, one sentence each, or empty.</summary>
    internal static string TsWarnings(JoinThunderstoreFacts t)
    {
        var parts = new List<string>();
        foreach (var r in t?.Requests ?? Array.Empty<JoinInstallRequest>())
            if (r.Kind == JoinInstallKind.UseVersion) parts.Add(JoinText.UseVersionWarning(r));
        return string.Join(" ", parts);
    }

    /// <summary>
    /// JOINFLOW2: the Rejoin page's body: <c>V Rising restarted to finish installing.</c> (after a restart; empty for Join
    /// now) while it runs — the status line under it is the one <c>Connecting to … 30%</c> —
    /// runs, then the outcome (<c>Joined …</c>, the failure, <c>Canceled.</c>, the password sentence). Live
    /// 2026-10-05: the body kept saying Connecting after the join.
    /// </summary>
    internal static string RejoinBodyText(JoinRejoinView v) =>
        // Live 2026-10-05 (user screenshot): the body repeated the status line's "Connecting to …". While it runs the
        // status line alone says where it connects (with its percentage); the body only says why after a restart.
        // F37: a rejoin that started by itself for a restarting server says why it restarted.
        v == null ? "" : v.Finished ? v.Status : v.AfterRestart ? JoinText.RejoinRestartedLine : v.Why;

    /// <summary>JOINFLOW2: the required rows of the summary on one wrapped line (<c>Alpha: Ready · Beta: Still missing</c>).</summary>
    internal static string RejoinRequiredLine(RejoinSummary s)
    {
        if (s == null) return "";
        var parts = new List<string>(s.Required.Count);
        foreach (var kv in s.Required) parts.Add(kv.Key + ": " + kv.Value);
        return string.Join(" · ", parts);
    }

    /// <summary>JOINFLOW2: the Rejoin page's status line with its percentage while it runs.</summary>
    internal static string RejoinStatus(JoinRejoinView v)
    {
        if (v == null) return "";
        if (v.Finished) return v.Status;
        string what = v.Resume ? ClientRestartText.ResumeStarting
            : v.Phase == "leaving" ? "Leaving for the main menu…" : JoinText.ConnectingTo(v.Endpoint);
        return what + " " + v.Progress.ToString(CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>JOINFLOW2: the Rejoin page's one button: Cancel while it runs, then Continue (joined) or Close.</summary>
    internal static DialogButton RejoinButton(JoinRejoinView v) =>
        v == null || !v.Finished
            ? new DialogButton(ButtonCancel, JoinText.CancelButton, DialogButtonRole.Cancel)
            : new DialogButton(ButtonCancel, v.Phase == "joined" ? JoinText.ContinueButton : JoinText.CloseButton, DialogButtonRole.Main, CellTone.Gold);

    /// <summary>The message box wraps this many lines at most; a longer message scrolls in a reader block.</summary>
    internal const int MaxBoxLines = 14;

    internal const float MessageFontSize = DialogLayout.BodyFontSize;

    internal const float LineHeight = DialogLayout.BodyLineHeight;

    internal const float ActionWidth = 170f;

    internal static string RequiredRow(string id) => "join-required-" + id;

    internal static string RecommendedRow(string key) => "join-rec-" + key;

    /// <summary>Wrapped lines of the message at the box's width: each paragraph wraps on its own.</summary>
    internal static int MessageLines(string text, float width)
    {
        int lines = 0;
        foreach (string paragraph in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            lines += Math.Max(1, MenuPanelLayout.WrappedLines(paragraph, Math.Max(40f, width - 24f), MessageFontSize, 200));
        return Math.Max(1, lines);
    }

    /// <summary>True when the message fits the framed box (and so can tick live).</summary>
    internal static bool FitsBox(string text, float width) => MessageLines(text, width) <= MaxBoxLines;

    /// <summary>The framed message box: one wrapping cell on a solid band.</summary>
    internal static CellRowSpec MessageBox(string text, float width)
    {
        int lines = Math.Min(MaxBoxLines, MessageLines(text, width));
        var cell = new CellSpec(CellKind.Text, CellText).With(
            text: text ?? "", width: CellWidth.Fill(80f), wrap: CellWrap.Wrap, fontSize: MessageFontSize, vAlign: CellVAlign.Top);
        // JOINLINKS1: the admins' links are drawn pale blue and underlined, and open behind a confirmation.
        cell.Linkify = true;
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Spacer, "pad-left").With(width: CellWidth.Px(10f)),
            cell,
            new CellSpec(CellKind.Spacer, "pad-right").With(width: CellWidth.Px(10f)),
        };
        return new CellRowSpec(MessageRow, cells, height: lines * LineHeight + 20f, band: true);
    }

    /// <summary>A long message as a scrolling reader block (plain lines; drawn without markup).</summary>
    internal static CellRowSpec MessageReader(string text, float width)
    {
        var lines = new List<CellLine>();
        foreach (string paragraph in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            lines.Add(new CellLine(CellLineKind.Body, paragraph));
        var block = new CellSpec(CellKind.ScrollText, CellText) { Lines = lines, Linkify = true };
        return DialogLayout.CentredRowSpec(MessageReaderRow, block, width, MaxBoxLines * LineHeight + 20f);
    }

    // ---- JOINLINKS1: the link confirmation and the mods page's section bands --------------------

    /// <summary>The link confirmation's full URL: pale blue, wrapping, as many lines as it needs (never cut).</summary>
    internal static CellRowSpec LinkUrl(string url, float width)
    {
        const float size = 16f;
        float textWidth = Math.Max(40f, width - 24f);
        int lines = Math.Max(1, MenuPanelLayout.WrappedLines(url, textWidth, size, 400));
        // One spare line: the estimate is a glyph average and the wrap cell's overflow mode cuts at its bottom.
        lines += 1;
        var cell = new CellSpec(CellKind.Text, CellText).With(text: url ?? "", tone: CellTone.Info, align: CellAlign.Center,
            wrap: CellWrap.Wrap, fontSize: size, width: CellWidth.Fill(80f), vAlign: CellVAlign.Top);
        return new CellRowSpec(JoinLinkDialog.UrlRow, new List<CellSpec>
        {
            new CellSpec(CellKind.Spacer, "pad-left").With(width: CellWidth.Px(10f)),
            cell,
            new CellSpec(CellKind.Spacer, "pad-right").With(width: CellWidth.Px(10f)),
        }, height: lines * size * 1.3f + 8f, plain: true);
    }

    /// <summary>
    /// A section's heading row on its band: the title in capitals in the section's heading color, the note
    /// (<c>4 of 6 ready</c>) at the right in its note color. The first row of the section.
    /// </summary>
    internal static CellRowSpec SectionHeading(string key, string title, string count, CellSection section, bool alone = false)
    {
        bool red = section == CellSection.Red;
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellText).With(text: (title ?? "").ToUpperInvariant(), fontSize: 18f, width: CellWidth.Fill(80f),
                tone: red ? CellTone.SectionRedHead : CellTone.SectionGreenHead),
            new CellSpec(CellKind.Muted, CellCount).With(text: count ?? "", align: CellAlign.Right, fontSize: 15f,
                tone: red ? CellTone.SectionRedNote : CellTone.SectionGreenNote),
        };
        return new CellRowSpec(key, cells, height: 34f, plain: true)
            .InSection(section, alone ? CellSectionEdge.First | CellSectionEdge.Last : CellSectionEdge.First);
    }

    // ---- CLIENTPOLICY2: "Not allowed on this server" inside the red band ---------------------------

    internal const string NotAllowedHeadRow = "join-notallowed-head";
    internal const string ChecksRow = "join-checks";

    internal static string NotAllowedRowKey(string id) => "join-notallowed-" + id;

    internal const string NotAllowedGapRow = "join-notallowed-gap";

    /// <summary>The space above the sub-heading inside the red band (live 2026-10-05 it sat right under the last required row).</summary>
    internal const float NotAllowedGap = 14f;

    /// <summary>The gap row before the sub-heading: a middle row of the red band with nothing in it.</summary>
    internal static CellRowSpec NotAllowedSpacer() =>
        new CellRowSpec(NotAllowedGapRow, new List<CellSpec> { new CellSpec(CellKind.Spacer, "gap").With(width: CellWidth.Fill(40f)) },
            height: NotAllowedGap, plain: true).InSection(CellSection.Red, CellSectionEdge.Middle);

    /// <summary>
    /// The sub-heading inside the red band, drawn like a section heading (capitals, the heading color, the count at the
    /// right): <c>NOT ALLOWED ON THIS SERVER · 2 mods</c>. A middle row of the section.
    /// </summary>
    internal static CellRowSpec NotAllowedHeading(int count, bool last = false)
    {
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellText).With(text: ClientPolicyView.NotAllowedHeading.ToUpperInvariant(), fontSize: 18f,
                width: CellWidth.Fill(80f), tone: CellTone.SectionRedHead),
            new CellSpec(CellKind.Muted, CellCount).With(text: ServerModPauseText.Mods(count), align: CellAlign.Right, fontSize: 15f,
                tone: CellTone.SectionRedNote),
        };
        return new CellRowSpec(NotAllowedHeadRow, cells, height: 34f, plain: true)
            .InSection(CellSection.Red, last ? CellSectionEdge.Last : CellSectionEdge.Middle);
    }

    /// <summary>The action cell's width: the longest caption a not-allowed row can show, at least <see cref="ActionWidth"/>.</summary>
    internal static float NotAllowedActionWidth()
    {
        float w = ActionWidth;
        foreach (string caption in new[] { ClientPolicyView.PauseButton, ClientPolicyView.TurnOffRestartButton })
            w = Math.Max(w, (float)Math.Ceiling(CellLayout.EstimateTextWidth(caption, DialogLayout.ButtonFontSize)) + 28f);
        return w;
    }

    /// <summary>The action cell of a not-allowed row: the wide button width while it has a button, else the ready-word width.</summary>
    internal static float NotAllowedActionWidthOf(JoinNotAllowedRow row) => row != null && row.Unresolved ? NotAllowedActionWidth() : ActionWidth;

    /// <summary>The width a not-allowed row's name and line get inside <paramref name="width"/> (the action cell and padding taken off).</summary>
    internal static float NotAllowedTextWidth(JoinNotAllowedRow row, float width) => Math.Max(80f, width - NotAllowedActionWidthOf(row) - 24f);

    /// <summary>
    /// A not-allowed row's drawn name and second line, fitted to <paramref name="textWidth"/> as the required rows' are
    /// (live 2026-10-05 at 3840x1080: "Not allowed · It stays off until you…", "Progenitor API Validation …"): the lead
    /// (<c>Not allowed</c>, <c>Paused for this server</c>, <c>Turns off at restart</c>), then the version, then the short
    /// detail, each only while the whole line fits; a lead that alone is too long, and a long name, are cut with <c>…</c>.
    /// The readback's <c>line</c> keeps every part; the long "stays off" sentence is the Turn off and restart confirmation's.
    /// </summary>
    internal static void NotAllowedLine(JoinNotAllowedRow row, float textWidth, out string name, out string lead, out string note)
    {
        name = MenuPanelLayout.ClipLabel(row?.Name ?? "", textWidth, CellLayout.TextFontSize);
        lead = row == null ? "" : ClientPolicyView.RowLead(row);
        note = "";
        if (row == null) return;
        string fullLead = MenuPanelLayout.ClipLabel(lead, textWidth, CellLayout.TwoLineNoteFontSize);
        if (!string.Equals(fullLead, lead, StringComparison.Ordinal))
        {
            lead = fullLead;
            return;
        }

        string head = lead + " · ";
        bool Fits(List<string> p) =>
            string.Equals(MenuPanelLayout.ClipLabel(head + string.Join(" · ", p), textWidth, CellLayout.TwoLineNoteFontSize),
                head + string.Join(" · ", p), StringComparison.Ordinal);
        // The version and the detail when both fit; else the detail (what to expect says more than the version); else
        // the version; else the lead alone.
        string detail = ClientPolicyView.RowDetail(row);
        var candidates = new List<List<string>>();
        if (row.Version.Length > 0) candidates.Add(new List<string> { row.Version, detail });
        candidates.Add(new List<string> { detail });
        if (row.Version.Length > 0) candidates.Add(new List<string> { row.Version });
        foreach (var parts in candidates)
        {
            if (!Fits(parts)) continue;
            note = string.Join(" · ", parts);
            break;
        }
    }

    /// <summary>True when every drawn part of the row fits its width (the readback's <c>fits</c>; a test asserts it).</summary>
    internal static bool NotAllowedFits(JoinNotAllowedRow row, float width)
    {
        float textWidth = NotAllowedTextWidth(row, width);
        NotAllowedLine(row, textWidth, out string name, out string lead, out string note);
        string line = note.Length > 0 ? lead + " · " + note : lead;
        return CellLayout.EstimateTextWidth(name, CellLayout.TextFontSize) <= textWidth + 0.5f
            && CellLayout.EstimateTextWidth(line, CellLayout.TwoLineNoteFontSize) <= textWidth + 0.5f;
    }

    /// <summary>One not-allowed row: the name over its lead and note, then Pause for this server / Turn off and restart, or the resolved word.</summary>
    internal static CellRowSpec NotAllowed(JoinNotAllowedRow row) => NotAllowed(row, SectionRowWidth(DialogLayout.ContentRowWidth()));

    /// <summary>The same, fitted to <paramref name="width"/> (the row's width in the band).</summary>
    internal static CellRowSpec NotAllowed(JoinNotAllowedRow row, float width)
    {
        float actionWidth = NotAllowedActionWidthOf(row);
        NotAllowedLine(row, NotAllowedTextWidth(row, width), out string shownName, out string lead, out string note);
        var name = new CellSpec(CellKind.Text, CellName).With(text: shownName + "\n" + note, wrap: CellWrap.TwoLine,
            width: CellWidth.Fill(120f));
        name.NoteLead = lead;
        name.NoteLeadTone = row.State == JoinNotAllowedState.Loaded ? CellTone.Amber : CellTone.Info;
        CellSpec action = row.Unresolved
            ? new CellSpec(CellKind.Button, CellAction).With(text: ClientPolicyView.ActionCaption(row), width: CellWidth.Px(actionWidth),
                fontSize: DialogLayout.ButtonFontSize, skin: CellSkin.PopupButton,
                tone: row.Action == JoinNotAllowedAction.Pause ? CellTone.Gold : CellTone.Normal)
            : new CellSpec(CellKind.Text, CellAction).With(text: ClientPolicyView.ActionCaption(row), tone: CellTone.Info, align: CellAlign.Right,
                width: CellWidth.Px(actionWidth));
        return new CellRowSpec(NotAllowedRowKey(row.Id), new List<CellSpec> { name, action }, twoLine: true, plain: true);
    }

    /// <summary>The section a mods-page row belongs to, with its last row's bottom rim.</summary>
    internal static CellRowSpec InSection(CellRowSpec row, CellSection section, bool last) =>
        row.InSection(section, last ? CellSectionEdge.Last : CellSectionEdge.Middle);

    /// <summary>The width a section row's cells get (its padding taken off both sides).</summary>
    internal static float SectionRowWidth(float width) => Math.Max(120f, width - 2f * CellRowSpec.SectionPad);

    /// <summary>One plain centered or left line (muted, warn, danger…), wrapping when long.</summary>
    internal static CellRowSpec Line(string key, string text, CellTone tone, float width, CellAlign align = CellAlign.Center, float fontSize = 15f)
    {
        int lines = Math.Max(1, MenuPanelLayout.WrappedLines(text, Math.Max(40f, width - 8f), fontSize, 12));
        var cell = new CellSpec(CellKind.Text, CellText).With(
            text: text ?? "", tone: tone, align: align, wrap: CellWrap.Wrap, fontSize: fontSize, width: CellWidth.Fill(40f));
        return new CellRowSpec(key, new List<CellSpec> { cell }, height: lines * fontSize * 1.3f + 8f, plain: true);
    }

    /// <summary>A section heading with a count at the right (<c>Required to join · 4 of 6 ready</c>).</summary>
    internal static CellRowSpec Heading(string key, string title, string count, float gapAbove = -1f)
    {
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellText).With(text: title, fontSize: 20f, width: CellWidth.Fill(80f)),
            new CellSpec(CellKind.Muted, CellCount).With(text: count, align: CellAlign.Right, fontSize: 15f),
        };
        return new CellRowSpec(key, cells, height: 34f, plain: true, gapAbove: gapAbove);
    }

    /// <summary>A switch and its caption on one row (the "don't show again" and trust boxes).</summary>
    internal static CellRowSpec Check(string key, string caption, bool on, float width, CellTone tone = CellTone.Normal)
    {
        int lines = Math.Max(1, MenuPanelLayout.WrappedLines(caption, Math.Max(40f, width - 70f), 15f, 4));
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Switch, CellCheck) { Clickable = true }.With(text: on ? "On" : "Off", width: CellWidth.Px(44f), tone: on ? CellTone.Gold : CellTone.Muted),
            new CellSpec(CellKind.Text, CellLabel).With(text: caption, tone: tone, wrap: CellWrap.Wrap, fontSize: 15f, width: CellWidth.Fill(80f)),
        };
        return new CellRowSpec(key, cells, height: Math.Max(32f, lines * 15f * 1.3f + 10f), plain: true);
    }

    /// <summary>
    /// One required row: the name over its source, version line and tags (two lines), then its action — a
    /// steel button, or <c>✓ Ready</c> in the ok tone.
    /// </summary>
    internal static CellRowSpec Required(JoinRequiredRow row) => Required(row, "");

    /// <summary>Same, with the row's <c>New</c> / <c>Changed</c> tag (<see cref="JoinShowPolicy.RowChanges"/>) leading its tags.</summary>
    internal static CellRowSpec Required(JoinRequiredRow row, string change) => Required(row, change, DialogLayout.ContentRowWidth());

    /// <summary>Same, fitted to <paramref name="width"/> (the row's width in the popup).</summary>
    internal static CellRowSpec Required(JoinRequiredRow row, string change, float width) => Required(row, change, width, null);

    /// <summary>
    /// JOINFLOW2: the same with the row's install in the current batch: while it runs, and once it loaded or waits
    /// for the restart, its state (<c>Downloading 62%</c>, <c>Loaded</c>, <c>After restart</c>) stands where the
    /// button was; a refused row shows <c>Refused</c> there (it is not offered again this join) and leads its
    /// line with the reason; a failed row keeps its button (to try again) and leads its line with the reason.
    /// The tags follow what the install did (<see cref="JoinText.TagsAfter"/>), not the catalog's prediction.
    /// </summary>
    internal static CellRowSpec Required(JoinRequiredRow row, string change, float width, JoinInstallRowState? state)
    {
        row = row.WithTags(JoinText.TagsAfter(row.Tags, state));
        if (state != null && row.Thunderstore && (!state.Terminal || state.Succeeded || state.Phase == JoinInstallPhase.Refused))
        {
            float nw = Math.Max(80f, width - ActionWidth - 24f);
            AttentionLine(row, JoinShowPolicy.TagWord(change), nw, out string l, out string n, out CellTone t);
            var nameCell = new CellSpec(CellKind.Text, CellName).With(text: row.Name + "\n" + n, wrap: CellWrap.TwoLine, width: CellWidth.Fill(120f));
            if (state.Phase == JoinInstallPhase.Refused && state.Reason.Length > 0)
            {
                l = "Refused: " + MenuPanelLayout.ClipLabel(state.Reason, nw * 0.8f, CellLayout.TwoLineNoteFontSize);
                t = CellTone.Orange;
            }

            if (l.Length > 0)
            {
                nameCell.NoteLead = l;
                nameCell.NoteLeadTone = t;
            }

            // JOINLINKS1: the light green (OkBright) reads at 4.5:1 on the red section band; the darker Ok does not.
            var tone = state.Phase == JoinInstallPhase.Loaded ? CellTone.OkBright
                : state.Phase == JoinInstallPhase.AfterRestart ? CellTone.Info
                : state.Phase == JoinInstallPhase.Refused ? CellTone.Orange
                : CellTone.Gold;
            var status = new CellSpec(CellKind.Text, CellAction).With(text: JoinText.InstallPhaseWord(state), tone: tone, align: CellAlign.Right,
                width: CellWidth.Px(ActionWidth));
            return new CellRowSpec(RequiredRow(row.Id), new List<CellSpec> { nameCell, status }, twoLine: true, plain: true);
        }

        var spec = RequiredCore(row, change, width);
        if (state != null && row.Thunderstore && state.Terminal && !state.Succeeded && state.Reason.Length > 0)
        {
            foreach (var cell in spec.Cells)
            {
                if (cell.Key != CellName) continue;
                cell.NoteLead = "Failed: "
                    + MenuPanelLayout.ClipLabel(state.Reason, Math.Max(80f, width - ActionWidth - 24f) * 0.8f, CellLayout.TwoLineNoteFontSize);
                cell.NoteLeadTone = CellTone.Orange;
            }
        }

        return spec;
    }

    private static CellRowSpec RequiredCore(JoinRequiredRow row, string change, float width)
    {
        // Live 2026-10-04 (two runs): status, tags, source and version on one line were cut off ("You h…"),
        // and a separate source row below floated with a row's minimum height under it. The row is one
        // two-line cell: the name, then a line built to fit (AttentionLine): the status and tags in color,
        // the version, and the source when there is room. The bridge readback keeps every part in full.
        string tag = JoinShowPolicy.TagWord(change);
        float actionWidth = RequiredActionWidth(row);
        float noteWidth = Math.Max(80f, width - actionWidth - 24f);
        AttentionLine(row, tag, noteWidth, out string lead, out string note, out CellTone tone);
        var name = new CellSpec(CellKind.Text, CellName).With(
            text: row.Name + "\n" + note, wrap: CellWrap.TwoLine, width: CellWidth.Fill(120f));
        if (lead.Length > 0)
        {
            name.NoteLead = lead;
            name.NoteLeadTone = tone;
        }

        CellSpec action = row.Ready
            ? new CellSpec(CellKind.Text, CellAction).With(text: JoinText.ReadyWord, tone: CellTone.OkBright, align: CellAlign.Right,
                width: CellWidth.Px(ActionWidth))
            : new CellSpec(CellKind.Button, CellAction).With(text: JoinText.ActionCaption(row), width: CellWidth.Px(actionWidth),
                fontSize: DialogLayout.ButtonFontSize, skin: CellSkin.PopupButton,
                tone: row.Action is JoinRowAction.Install or JoinRowAction.Update or JoinRowAction.UseVersion
                    or JoinRowAction.UseKept or JoinRowAction.TurnOn or JoinRowAction.UseProgenitor
                    ? CellTone.Gold : CellTone.Normal);
        if (row.Ready) action.Glyph = "✓";
        return new CellRowSpec(RequiredRow(row.Id), new List<CellSpec> { name, action }, twoLine: true, plain: true);
    }

    /// <summary>
    /// The action cell's width of a required row: <see cref="ActionWidth"/>, or the Use Progenitor caption's width when it
    /// is longer (C4, live 2026-10-10: <c>Use Progenitor 2026.10.900</c> was cut to <c>Use Progenitor 2…</c> in 170 px).
    /// </summary>
    internal static float RequiredActionWidth(JoinRequiredRow row)
    {
        if (row == null || row.Ready || row.Action != JoinRowAction.UseProgenitor) return ActionWidth;
        return Math.Max(ActionWidth, (float)Math.Ceiling(CellLayout.EstimateTextWidth(JoinText.ActionCaption(row), DialogLayout.ButtonFontSize)) + 28f);
    }

    /// <summary>
    /// C4, the Progenitor row's line (live 2026-10-10: <c>Newer than the server's · you have 2026.10.90…</c> hid the build
    /// the server needs): what it needs comes first; then the player's version, then the status lead, while they fit.
    /// </summary>
    private static void ProgenitorLine(JoinRequiredRow row, float noteWidth, ref string lead, out string note)
    {
        string needs = "needs " + JoinText.NeedsWords(row);
        string both = needs + ", you have " + (row.ProgenitorSameVersion ? "another build" : row.Have ?? "another build");
        foreach (var (l, n) in new[] { (lead, both), ("", both), (lead, needs) })
        {
            string drawn = (l.Length > 0 ? l + " · " : "") + n;
            if (!string.Equals(MenuPanelLayout.ClipLabel(drawn, noteWidth, CellLayout.TwoLineNoteFontSize), drawn, StringComparison.Ordinal)) continue;
            lead = l;
            note = n;
            return;
        }

        lead = "";
        note = needs;
    }

    /// <summary>
    /// A required row's second line, fitted to <paramref name="noteWidth"/>: the colored lead (the status of
    /// a row that needs attention, then the <c>New</c>/<c>Changed</c>/<c>Installed</c> tag) and the note
    /// (the version; then, while they fit, the source and the restart tag). A ready row keeps the full
    /// version line with its tags.
    /// </summary>
    internal static void AttentionLine(JoinRequiredRow row, string tag, float noteWidth, out string lead, out string note, out CellTone tone)
    {
        var leadParts = new List<string>(3);
        string status = StatusWord(row);
        if (status.Length > 0) leadParts.Add(status);
        if (tag.Length > 0) leadParts.Add(tag);
        // CLIENTPOLICY2: the admins' reason for a required client mod, in quotes, when it fits the line.
        string quoted = row.Reason.Length > 0 ? "“" + row.Reason + "”" : "";
        if (row.Ready)
        {
            foreach (string t in JoinText.TagWords(row.Tags)) leadParts.Add(t);
            lead = string.Join(" · ", leadParts);
            note = JoinText.VersionLine(row);
            if (quoted.Length > 0)
            {
                string withReason = note + " · " + quoted;
                string full0 = lead.Length > 0 ? lead + " · " : "";
                if (string.Equals(MenuPanelLayout.ClipLabel(full0 + withReason, noteWidth, CellLayout.TwoLineNoteFontSize), full0 + withReason, StringComparison.Ordinal))
                    note = withReason;
            }

            tone = tag.Length > 0 ? CellTone.Gold : (row.Tags & JoinRowTags.NeedsRestart) != 0 ? CellTone.Amber : CellTone.Info;
            return;
        }

        lead = string.Join(" · ", leadParts);
        tone = CellTone.Amber;
        if (row.Origin == JoinRowOrigin.Progenitor)
        {
            ProgenitorLine(row, noteWidth, ref lead, out note);
            return;
        }

        // JOINFLOW4: the player's newer copy is kept when the server's version goes in: the approved sentence
        // ("Use server's 1.8.2 · your 1.9.0 is kept for other servers") is the line.
        string keep = JoinText.KeepLine(row);
        // Live 2026-10-04: the concrete cause of a restart route or a copy that cannot be kept is on the row.
        string why = JoinText.KeepReasonLine(row);
        if (keep.Length > 0)
        {
            note = why.Length > 0 ? keep + " · " + why : keep;
            return;
        }

        var parts = new List<string> { CompactVersion(row) };
        string full = lead.Length > 0 ? lead + " · " : "";
        bool Fits(List<string> p) =>
            string.Equals(MenuPanelLayout.ClipLabel(full + string.Join(" · ", p), noteWidth, CellLayout.TwoLineNoteFontSize),
                full + string.Join(" · ", p), StringComparison.Ordinal);

        if (row.Action == JoinRowAction.UseKept && row.Kept != null)
        {
            parts.Add(row.Kept.PickRoute == "now" ? "kept " + row.Kept.PickVersion + " loads now" : "kept " + row.Kept.PickVersion + " loads after a restart");
            if (!Fits(parts)) parts[parts.Count - 1] = "kept " + row.Kept.PickVersion;
            note = string.Join(" · ", parts) + (why.Length > 0 ? " · " + why : "");
            return;
        }

        // Not kept (two copies, another tool's): say why, so the action's refusal is no surprise.
        if (why.Length > 0) parts.Add(why);

        if (quoted.Length > 0)
        {
            parts.Add(quoted);
            if (!Fits(parts)) parts.RemoveAt(parts.Count - 1);
        }

        if (row.Kept is { HasPick: true, PickRoute: "refused" })
        {
            parts.Add("kept " + row.Kept.PickVersion + " can't be used");
            if (!Fits(parts)) parts.RemoveAt(parts.Count - 1);
        }

        // The longest source wording that fits, then shorter ones (the readback keeps the full source line).
        foreach (string source in ShortSources(row))
        {
            parts.Add(source);
            if (Fits(parts)) break;
            parts.RemoveAt(parts.Count - 1);
        }

        if ((row.Tags & JoinRowTags.NeedsRestart) != 0)
        {
            parts.Add("needs restart");
            if (!Fits(parts)) parts.RemoveAt(parts.Count - 1);
        }

        note = string.Join(" · ", parts);
    }

    /// <summary>The version part of a row that needs attention: <c>needs 1.2</c>, <c>you have 1.1, needs 1.2</c>, <c>you have 1.3, server 1.2</c>.</summary>
    internal static string CompactVersion(JoinRequiredRow row)
    {
        // CLIENTPOLICY2: a required client mod states the admins' rule; C3/C4: a mod the server runs states the server's
        // match when it has one (exactly 1.2.0, 1.2.x, 1.2.0 or newer).
        string needs = JoinText.NeedsWords(row);
        // C4: the Progenitor row (the same release number can be another build).
        if (row.Origin == JoinRowOrigin.Progenitor)
            return (row.ProgenitorSameVersion ? "another build" : "you have " + (row.Have ?? "?")) + ", needs " + needs;
        if ((row.Origin == JoinRowOrigin.ClientPolicy || row.MatchRule != null) && row.Status == JoinRowStatus.Newer)
            return "you have " + (row.Have ?? "?") + ", needs " + needs;
        if (row.Origin == JoinRowOrigin.YouRequire || row.Status == JoinRowStatus.ServerLacks)
            return "you have " + (row.Have ?? "it");
        return row.Status switch
        {
            JoinRowStatus.Missing => "needs " + needs,
            JoinRowStatus.TurnedOff => JoinText.CompactOffVersion(row),
            JoinRowStatus.Newer => "you have " + (row.Have ?? "?") + ", server " + needs,
            _ => "you have " + (row.Have ?? "?") + ", needs " + needs,
        };
    }

    /// <summary>The source in a few words, longest first: <c>Thunderstore · Owner</c> then <c>Thunderstore</c>, <c>no download source</c>.</summary>
    internal static IReadOnlyList<string> ShortSources(JoinRequiredRow row)
    {
        if (row.Origin == JoinRowOrigin.YouRequire) return Array.Empty<string>();
        // C4: where Use Progenitor takes the build from, or why it can't be switched here.
        if (row.Origin == JoinRowOrigin.Progenitor) return row.ProgenitorNote.Length > 0 ? new[] { row.ProgenitorNote } : Array.Empty<string>();
        if ((row.Source & JoinRowSource.LocalTest) != 0)
            return new[] { Install.LocalTestSource.Words + " · " + (row.Package ?? ""), Install.LocalTestSource.Words };
        bool ts = (row.Source & JoinRowSource.Thunderstore) != 0;
        if (ts) return new[] { "Thunderstore · " + JoinText.Owner(row.Package), "Thunderstore" };
        return new[] { "no download source", "no source" };
    }

    /// <summary>
    /// The mods page's buttons. Live 2026-10-04: "Install 0 mods" was drawn with nothing to install. The
    /// install button shows only when something can be installed; with every required mod ready the
    /// closing button reads Continue; ← Server message when the server has one.
    /// </summary>
    internal static List<DialogButton> ModsButtons(JoinFlowSession session) => ModsButtonsCore(session);

    /// <summary>
    /// The line under the mods page's subtitle: <c>Installed 1 mod. You're ready to play.</c> when this join
    /// installed mods and everything is ready, else the lists-changed line when the page opened for a
    /// change, else empty.
    /// </summary>
    internal static string ModsChangedText(JoinFlowSession session)
    {
        int installed = 0;
        foreach (var pair in session.Changes)
            if (pair.Value == JoinShowPolicy.ChangeInstalled) installed++;
        if (installed > 0 && session.Model.AllRequiredReady) return JoinShowPolicy.InstalledLine(installed);
        return session.ModsDecision.Reason == JoinShowPolicy.ReasonModsChanged ? JoinShowPolicy.ModsChangedLine : "";
    }

    private static List<DialogButton> ModsButtonsCore(JoinFlowSession session)
    {
        var list = new List<DialogButton>(3);
        int install = session.BottomCount;
        if (session.HasMessage) list.Add(new DialogButton(ButtonBack, JoinText.BackToMessageButton, DialogButtonRole.Cancel));
        bool ready = session.Model.AllRequiredReady;
        list.Add(new DialogButton(ButtonClose, ready ? JoinText.ContinueButton : JoinText.CloseButton,
            install > 0 ? DialogButtonRole.Option : ready ? DialogButtonRole.Main : DialogButtonRole.Cancel,
            ready && install == 0 ? CellTone.Gold : CellTone.Normal));
        if (install > 0) list.Add(new DialogButton(ButtonInstall, session.BottomCaption, DialogButtonRole.Main, CellTone.Gold));
        if (session.OffersUpdateProgenitor)
            list.Add(new DialogButton(ButtonUpdateProgenitor, JoinText.UpdateProgenitorButton, install > 0 ? DialogButtonRole.Option : DialogButtonRole.Main, CellTone.Gold));
        return list;
    }

    /// <summary>The status word a row that needs attention leads with: <c>Missing</c>, <c>Update needed</c>, <c>Newer than the server's</c>, <c>Server doesn't run it</c>.</summary>
    internal static string StatusWord(JoinRequiredRow row) => row.Status switch
    {
        // C4: the server's Progenitor build; the same release number can be another build.
        JoinRowStatus.Older or JoinRowStatus.Newer when row.Origin == JoinRowOrigin.Progenitor =>
            row.ProgenitorSameVersion ? "Another build" : row.Status == JoinRowStatus.Newer ? "Newer than the server's" : "Older than the server's",
        JoinRowStatus.Missing => "Missing",
        JoinRowStatus.Older => "Update needed",
        // CLIENTPOLICY2: the admins' rule, not the server's copy, is what a required client mod is newer than.
        JoinRowStatus.Newer when row.Origin == JoinRowOrigin.ClientPolicy => "Newer than allowed",
        JoinRowStatus.Newer => "Newer than the server's",
        JoinRowStatus.ServerLacks => "Server doesn't run it",
        JoinRowStatus.TurnedOff => JoinText.StatusWordOf(row),
        _ => "",
    };

    /// <summary>One recommended row: its checkbox, the name over the admin's reason and its source, and <c>Installed</c>.</summary>
    internal static CellRowSpec Recommended(JoinRecommendedRow row, bool on) => Recommended(row, on, "");

    /// <summary>Same, with the row's <c>New</c> / <c>Changed</c> tag.</summary>
    internal static CellRowSpec Recommended(JoinRecommendedRow row, bool on, string change) => Recommended(row, on, change, null);

    /// <summary>JOINFLOW2: the same with the row's install in the current batch, its state where <c>Installed</c> goes.</summary>
    internal static CellRowSpec Recommended(JoinRecommendedRow row, bool on, string change, JoinInstallRowState? state)
    {
        bool refused = state?.Phase == JoinInstallPhase.Refused;
        var spec = RecommendedCore(row, on && !refused, change);
        if (state == null) return spec;
        if (refused)
        {
            // Not offered again this join: the switch goes (a spacer, so nothing to click) and the reason leads the line.
            var cells = new List<CellSpec>(spec.Cells.Count);
            foreach (var cell in spec.Cells)
            {
                if (cell.Key == CellCheck) cells.Add(new CellSpec(CellKind.Spacer, CellCheck).With(width: CellWidth.Px(44f)));
                else
                {
                    if (cell.Key == CellName && state!.Reason.Length > 0)
                    {
                        cell.NoteLead = "Refused: " + MenuPanelLayout.ClipLabel(state.Reason, 360f, CellLayout.TwoLineNoteFontSize);
                        cell.NoteLeadTone = CellTone.Orange;
                    }

                    cells.Add(cell);
                }
            }

            spec = new CellRowSpec(spec.Key, cells, twoLine: true, plain: true);
        }

        foreach (var cell in spec.Cells)
        {
            if (cell.Key != CellAction) continue;
            cell.Text = JoinText.InstallPhaseWord(state);
            cell.Tone = state.Phase == JoinInstallPhase.Loaded ? CellTone.OkBright
                : state.Phase == JoinInstallPhase.AfterRestart ? CellTone.Info
                : state.Terminal ? CellTone.Orange : CellTone.Gold;
        }

        return spec;
    }

    private static CellRowSpec RecommendedCore(JoinRecommendedRow row, bool on, string change)
    {
        var parts = new List<string>(3);
        if (row.Mod.Reason.Length > 0) parts.Add("“" + row.Mod.Reason + "”");
        if (row.Mod.Package.Length > 0) parts.Add("Thunderstore · " + JoinText.Owner(row.Mod.Package));
        if (row.Mod.Version.Length > 0) parts.Add(row.Mod.Version);
        var cells = new List<CellSpec>();
        if (row.Installed)
            cells.Add(new CellSpec(CellKind.Spacer, CellCheck).With(width: CellWidth.Px(44f)));
        else
            cells.Add(new CellSpec(CellKind.Switch, CellCheck) { Clickable = true }.With(text: on ? "On" : "Off", width: CellWidth.Px(44f),
                tone: on ? CellTone.Gold : CellTone.Muted));
        var nameCell = new CellSpec(CellKind.Text, CellName).With(text: row.Name + "\n" + string.Join(" · ", parts),
            wrap: CellWrap.TwoLine, width: CellWidth.Fill(120f));
        string tag = JoinShowPolicy.TagWord(change);
        if (tag.Length > 0)
        {
            nameCell.NoteLead = tag;
            nameCell.NoteLeadTone = CellTone.Gold;
        }

        cells.Add(nameCell);
        // JOINTURNON (live 2026-10-05): any off, waiting or paused copy reads amber; the cell fits the longest state word.
        bool offCopy = row.OffCopy != null;
        cells.Add(new CellSpec(CellKind.Text, CellAction).With(text: JoinText.RecommendedWord(row),
            tone: offCopy ? CellTone.Amber : CellTone.OkBright, align: CellAlign.Right,
            width: CellWidth.Px(JoinText.RecommendedWordWidth(CellLayout.TextFontSize))));
        return new CellRowSpec(RecommendedRow(JoinPageModel.KeyOf(row)), cells, twoLine: true, plain: true);
    }

    /// <summary>
    /// The trust page's Server fact: the name, then the address when there is one (<c>1.2.3.4:9876</c>), or
    /// <c>via Steam</c> when the connection is a Steam relay identity. Live 2026-10-04: the raw relay id was
    /// shown in parentheses as if it were an address.
    /// </summary>
    internal static string ServerFact(string serverName, string host)
    {
        string name = serverName ?? "";
        string h = (host ?? "").Trim();
        if (h.Length == 0 || h == name) return name;
        return JoinText.LooksLikeAddress(h) ? name + " (" + h + ")" : name + " (via Steam)";
    }

}

/// <summary>
/// JOINFLOW1: draws the join screens (<see cref="JoinFlowClient"/>'s session) in the game's popup look — the
/// dialog chrome, the steel popup buttons and the game serif — and routes their buttons, Escape and the
/// dev bridge to the session. One panel at a time, rebuilt when the page or its facts change, disposed
/// (never hidden) when the screens close.
/// </summary>
internal static class JoinFlowPanel
{
    public const string PluginId = "progenitor";

    private static readonly object Owner = new();
    private static readonly OwnedPanel Owned = new();
    private static bool _installed;
    private static IDisposable? _escape;
    private static readonly List<IUiCellRow> RecommendedRows = new();
    private static IUiCellRow? _messageRow;

    /// <summary>JOINLINKS1: the long message's reader block (links are read back from it), or null.</summary>
    private static IUiCellRow? _readerRow;

    /// <summary>JOINLINKS1: the link confirmation drawn over the message page, or null.</summary>
    private static JoinLinkDialog? _link;

    /// <summary>JOINLINKS1: the section bands the mods page drew (row key to "red" / "green"), for the readback.</summary>
    private static readonly Dictionary<string, string> DrawnSections = new(StringComparer.Ordinal);

    private static bool _liveCountdown;
    private static double _nextTick;

    /// <summary>The server name the drawn title used, and when the name is checked again (once a second).</summary>
    private static string _drawnLabel = "";

    private static double _nextNameCheck;

    /// <summary>The server a page title names: the name this client knows now, else the model's, else the address.</summary>
    internal static string TitleLabel(JoinPageModel model) =>
        JoinText.PageLabel(model.Facts.ServerName, JoinFlowClient.ServerNameNowFor(model.Facts.Host), model.Facts.Host);

    /// <summary>
    /// The message or mods page drawn with an address (or another name) while a better one is known now: redraw once so
    /// the title names the server.
    /// </summary>
    private static void RefreshTitleName()
    {
        if (_link != null || !Owned.IsOpen || (_drawnPage != JoinPage.Message && _drawnPage != JoinPage.Mods)) return;
        double now = Now();
        if (now < _nextNameCheck) return;
        _nextNameCheck = now + 1.0;
        var session = JoinFlowClient.Session;
        if (session == null || session.Closed || session.Page != _drawnPage) return;
        string label;
        try { label = TitleLabel(session.Model); }
        catch { return; }
        if (string.Equals(label, _drawnLabel, StringComparison.Ordinal)) return;
        Plugin.Logger?.LogInfo("JoinFlow: the server's name is known now (" + label + "); the page title is redrawn");
        Build(session);
    }
    private static JoinPage _drawnPage;

    /// <summary>True while a screen is on show.</summary>
    public static bool IsOpen => Owned.IsOpen;

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        ModManifestDeny.PanelRequested += OnDenyReport;
        JoinFlowClient.Changed += Redraw;
        JoinFlowClient.ScreensDrawn = () => Owned.IsOpen;
#if !PROGENITOR_TESTS
        JoinFlowClient.WorldStamp = LiveWorldStamp;
#endif
#if !PROGENITOR_TESTS
        JoinFlowClient.InWorldReady = LiveInWorldReady;
        // INPLACE1: an in-place result after the player closed the join screens.
        JoinFlowClient.InPlaceNotice = (title, text) => ServerModPauseNotice.Open(title, text);
        JoinFlowClient.NameLookup = LoadedName;
        JoinFlowClient.ConnectedNameLookup = ConnectedServerName;
        JoinFlowClient.LeaveAction = LeaveToMenu;
        // JOINFLOW2: Thunderstore installs, Join now, restart and rejoin.
        JoinFlowLive.Install();
        // The client's frame steps first (each its own subscriber, so perf names a slow one), then the panel's.
        foreach (var step in JoinFlowClient.FrameSteps)
        {
            try { Plugin.Frame += step; }
            catch { /* without a frame hook the screens still open; that step does not tick */ }
        }

        try { Plugin.Frame += OnFrame; }
        catch { /* without a frame hook the screens still open; nothing ticks */ }
#endif
    }

    internal static void Uninstall()
    {
        if (_installed)
        {
            ModManifestDeny.PanelRequested -= OnDenyReport;
            JoinFlowClient.Changed -= Redraw;
#if !PROGENITOR_TESTS
            try { Plugin.Frame -= OnFrame; }
            catch { /* teardown */ }
            foreach (var step in JoinFlowClient.FrameSteps)
            {
                try { Plugin.Frame -= step; }
                catch { /* teardown */ }
            }
            JoinFlowLive.Uninstall();
#endif
        }

        _installed = false;
        Drop();
    }

    private static void OnDenyReport(ModDenyReport report)
    {
        try
        {
            if (JoinFlowClient.OnDenyReport(report)) return;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("JoinFlow: the screens could not take the refusal (" + e.Message + "); showing the deny panel");
        }

        // The fallback: the read-only refusal panel, exactly as before the join screens.
        try { ModManifestDenyPanel.Open(report); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: deny panel open threw: " + e.Message); }
    }

    /// <summary>The admin's Preview button: the message page as a player sees it now, from the settings as this client sees them.</summary>
    public static void OpenPreview() => TryOpenPreview();

    /// <summary>The player's "Server message" button on Progenitor's page: the message page again (and the mods page behind it).</summary>
    public static void ShowServerMessage()
    {
        if (!JoinFlowClient.ShowNow())
            Plugin.Logger?.LogInfo("JoinFlow: no server message this join (not joined, or the server sent none)");
    }

    /// <summary>Progenitor's page, <b>View mods</b>: the joined server's mods page (required and recommended mods).</summary>
    public static void ShowServerMods()
    {
        if (!JoinFlowClient.ShowModsNow())
            Plugin.Logger?.LogInfo("JoinFlow: no mods page this join (not joined, or the server sent no catalog)");
    }

    internal static bool TryOpenPreview()
    {
        JoinSection? section = null;
#if !PROGENITOR_TESTS
        try { section = JoinServerSettings.CurrentValues(JoinFlowClient.Section?.Recommended, JoinFlowClient.ServerZone ?? TimeZoneInfo.Local); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: preview build threw: " + e.Message); }
        if (section != null && JoinFlowClient.Section != null)
            section = new JoinSection(section.Message, section.Reset, section.Recommended, JoinFlowClient.Section.Zone,
                showMessage: section.ShowMessage, showMods: section.ShowMods,
                names: JoinFlowClient.Section.Names, versions: JoinFlowClient.Section.Versions);
#endif
        return JoinFlowClient.OpenPreview(section);
    }

    /// <summary>Redraw the current page (or close when the session closed).</summary>
    internal static void Redraw()
    {
        var session = JoinFlowClient.Session;
        if (session == null || session.Closed)
        {
            Drop();
            return;
        }

        // CLIENTPOLICY2: a rebuilt panel would be drawn over the Turn off and restart confirmation; the answer redraws.
        if (JoinFlowClient.TurnOffConfirmOpen) return;

        try { Build(session); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("JoinFlow: the screens could not be drawn: " + e.Message);
            Drop();
        }
    }

    private static void Drop()
    {
        _escape?.Dispose();
        _escape = null;
        _messageRow = null;
        _readerRow = null;
        _link = null;
        DrawnSections.Clear();
        _liveCountdown = false;
        RecommendedRows.Clear();
        _drawnPage = JoinPage.None;
        var held = Owned.Take();
        if (held == null) return;
        try { held.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: panel dispose threw: " + e.Message); }
    }

    private static void Build(JoinFlowSession session)
    {
        var old = Owned.Take();
        if (old != null)
        {
            try { old.Dispose(); }
            catch { /* a stale handle must not stop the redraw */ }
        }

        _messageRow = null;
        _readerRow = null;
        DrawnSections.Clear();
        _liveCountdown = false;
        RecommendedRows.Clear();
        var model = session.Model;
        // JOINLINKS1: the link confirmation belongs to the message page; any other page drops it.
        if (session.Page != JoinPage.Message) _link = null;
        string label = TitleLabel(model);
        _drawnLabel = label;
        string title = _link != null ? JoinLinkText.Title : session.Page switch
        {
            JoinPage.Message => JoinText.MessageTitle(label),
            JoinPage.ThunderstoreTrust => JoinText.TsTrustTitle,
            JoinPage.Result => session.Result == null ? JoinText.RestartTitle : JoinFlowLayout.ResultTitle(session.Result),
            JoinPage.Rejoin => session.RejoinView?.Resume == true
                ? ClientRestartText.ResumeTitle(session.RejoinView.ServerName)
                : JoinText.RejoinTitle(session.RejoinView?.ServerName ?? model.ServerName, session.RejoinView?.AfterRestart ?? false),
            _ => JoinText.ModsTitle(label),
        };

        var panel = Ui.Ui.Panel(title, Owner, PluginId);
        Owned.Adopt(panel);
        if (!panel.Enabled)
        {
            _drawnPage = session.Page;
            return;
        }

        var content = new DialogContent
        {
            Shape = UiDialogShape.Notice,
            Title = UiRichText.Literal(title),
            Footer = Ui.Ui.FrameworkFooter,
            RowKey = JoinFlowLayout.ButtonsRow,
        };
        float width = DialogLayout.ContentRowWidth();
        switch (session.Page)
        {
            case JoinPage.Message when _link != null:
                content.Body = UiRichText.Literal(_link.Body);
                UiDialogView.Build(panel, content);
                BuildLinkDialog(panel, _link, width);
                break;
            case JoinPage.Message:
                UiDialogView.Build(panel, content);
                BuildMessage(panel, session, width);
                break;
            case JoinPage.ThunderstoreTrust:
                content.Body = session.TsTrust == null ? "" : UiRichText.Literal(JoinText.TsTrustIntro(session.TsTrust.ServerName, session.TsTrust.Requests.Count));
                if (session.TsTrust != null) content.Facts = JoinFlowLayout.TsTrustFacts(session.TsTrust);
                UiDialogView.Build(panel, content);
                BuildTsTrust(panel, session, width);
                break;
            case JoinPage.Result:
                content.Body = session.Result == null ? "" : UiRichText.Literal(JoinFlowLayout.ResultBody(session.Result));
                UiDialogView.Build(panel, content);
                BuildResult(panel, session, width);
                break;
            case JoinPage.Rejoin:
                content.Body = session.RejoinView == null ? "" : UiRichText.Literal(JoinFlowLayout.RejoinBodyText(session.RejoinView));
                UiDialogView.Build(panel, content);
                BuildRejoin(panel, session, width);
                break;
            default:
                content.Body = JoinText.ModsSubtitle;
                UiDialogView.Build(panel, content);
                BuildMods(panel, session, width);
                break;
        }

        Ui.Ui.Show(panel);
        _drawnPage = session.Page;
        _escape ??= Ui.Ui.PushEscape(OnEscape);
        Plugin.Logger?.LogInfo("JoinFlow: page " + JoinFlowSession.PageName(session.Page) + " drawn");
    }

    private static void BuildMessage(IUiPanel panel, JoinFlowSession session, float width)
    {
        var section = session.Model.Facts.Section;
        string rendered = Rendered(section, out bool live, out bool hasTime);
        if (JoinFlowLayout.FitsBox(rendered, width))
        {
            var box = panel.CellRow(JoinFlowLayout.MessageBox(rendered, width));
            _messageRow = box;
            _liveCountdown = live;
            _nextTick = Now() + 1.0;
            // JOINLINKS1: a click on a link (TMP's hit test names it) opens the confirmation.
            box.OnClick(JoinFlowLayout.CellText, () => ClickedLink(box));
        }
        else
        {
            // A message longer than the box scrolls in a reader block; its countdown is drawn when the
            // page is drawn (not ticked), which the docs state.
            var reader = panel.CellRow(JoinFlowLayout.MessageReader(rendered, width));
            _readerRow = reader;
            reader.OnClick(JoinFlowLayout.CellText, () => ClickedLink(reader));
        }

        // Requested 2026-10-05: the line only when a token drew a time; otherwise no row at all (no gap).
        _timesLine = hasTime;
        if (hasTime) panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.MessageFooterRow, JoinText.MessageTimesLine, CellTone.Muted, width));
        panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.SummaryRow, JoinText.Summary(session.Model),
            session.Model.CoreRefused ? CellTone.Orange : session.Model.AllRequiredReady ? CellTone.Normal : CellTone.Amber, width, fontSize: 17f));
        if (session.Model.CoreRefused)
        {
            panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.CoreBodyRow,
                JoinText.CoreRefusedBody(session.Model.Facts.CoreRequirements, session.Model.Facts.OwnCoreVersion), CellTone.Normal, width));
            // Update Progenitor's answer (the mods page draws it under its rows; this page has no other place for it).
            if (session.Notice.Length > 0)
                panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.NoticeRow, session.Notice, session.NoticeGood ? CellTone.Ok : CellTone.Orange, width));
        }
        // CLIENTPOLICY2: the server asks this client for its mod list (the privacy line of docs/client-mod-policy.md).
        if (section?.HasClientPolicy == true)
            panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.ChecksRow, ClientPolicyView.ChecksLine, CellTone.Muted, width));
        if (session.CanDismiss)
        {
            var check = panel.CellRow(JoinFlowLayout.Check(JoinFlowLayout.DontShowRow, JoinText.DontShowAgain, session.DontShowAgain, width));
            check.OnClick(JoinFlowLayout.CellCheck, () => Act(s => { s.DontShowAgain = !s.DontShowAgain; return "toggled"; }));
        }

        var buttons = panel.CellRow(DialogLayout.ButtonRowSpec(JoinFlowLayout.ButtonsRow, JoinFlowLayout.MessageButtons(session), width));
        if (JoinFlowLayout.OffersLeave(session)) buttons.OnClick(JoinFlowLayout.ButtonLeave, () => Press(JoinFlowLayout.ButtonLeave));
        buttons.OnClick(JoinFlowLayout.ButtonContinue, () => Press(JoinFlowLayout.ButtonContinue));
        if (session.CanViewMods) buttons.OnClick(JoinFlowLayout.ButtonViewMods, () => Press(JoinFlowLayout.ButtonViewMods));
    }

    /// <summary>
    /// JOINLINKS1: "Open this link?" on the message page's panel: the body names the server's admins, then the
    /// exact URL that opens (pale blue, wrapping, never cut), the copy status, and Cancel / Copy link / Open in browser.
    /// </summary>
    private static void BuildLinkDialog(IUiPanel panel, JoinLinkDialog link, float width)
    {
        panel.CellRow(JoinFlowLayout.LinkUrl(link.Url, width));
        if (link.Status.Length > 0)
            panel.CellRow(JoinFlowLayout.Line(JoinLinkDialog.StatusRow, link.Status,
                link.Status == JoinLinkText.Copied ? CellTone.OkBright : CellTone.Orange, width));
        var buttons = panel.CellRow(DialogLayout.ButtonRowSpec(JoinLinkDialog.ButtonsRow, JoinLinkDialog.Buttons(), width));
        buttons.OnClick(JoinLinkDialog.ButtonCancel, () => LinkButton("cancel"));
        buttons.OnClick(JoinLinkDialog.ButtonCopy, () => LinkButton("copy"));
        buttons.OnClick(JoinLinkDialog.ButtonOpen, () => LinkButton("open"));
    }

    private static void BuildMods(IUiPanel panel, JoinFlowSession session, float width)
    {
        var model = session.Model;
        string changedText = JoinFlowLayout.ModsChangedText(session);
        if (changedText.Length > 0)
            panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.ModsChangedRow, changedText, CellTone.Gold, width));
        // Live 2026-10-13: a refusal for this client's Core, which no row below can fix, leads the page.
        if (model.CoreRefused)
        {
            panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.CoreRefusedRow, JoinText.CoreRefusedTitle(model.Facts.CoreRequirements),
                CellTone.Orange, width, fontSize: 17f));
            panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.CoreBodyRow,
                JoinText.CoreRefusedBody(model.Facts.CoreRequirements, model.Facts.OwnCoreVersion), CellTone.Normal, width));
        }
        // JOINLINKS1: "Required to join" on a darkened red band, "Recommended by this server" on a green one;
        // each section's rows sit inside its band (the rim round the whole block, the fill through the gaps).
        float inner = JoinFlowLayout.SectionRowWidth(width);
        panel.CellRow(JoinFlowLayout.SectionHeading(JoinFlowLayout.RequiredHeadRow, JoinText.RequiredHeading, JoinText.ReadyCount(model),
            CellSection.Red));
        DrawnSections[JoinFlowLayout.RequiredHeadRow] = UiCellRowHandle.SectionName(CellSection.Red);
        // CLIENTPOLICY2: the "Not allowed on this server" rows follow in the same band, under their own sub-heading.
        bool notAllowed = model.NotAllowed.Count > 0;
        for (int i = 0; i < model.Required.Count; i++)
        {
            var row = model.Required[i];
            var state = JoinFlowClient.InstallStateOf(row.Id);
            var spec = JoinFlowLayout.Required(row, session.Changes.TryGetValue(row.Id, out var change) ? change : "", inner, state);
            var cells = panel.CellRow(JoinFlowLayout.InSection(spec, CellSection.Red, last: i == model.Required.Count - 1 && !notAllowed));
            string id = row.Id;
            bool settled = state != null && row.Thunderstore && (!state.Terminal || state.Succeeded || state.Phase == JoinInstallPhase.Refused);
            if (!row.Ready && !settled) cells.OnClick(JoinFlowLayout.CellAction, () => RowAction(id));
        }

        if (model.Required.Count == 0)
            panel.CellRow(JoinFlowLayout.InSection(
                JoinFlowLayout.Line("join-required-none", "This server requires no mods.", CellTone.Muted, inner), CellSection.Red, last: !notAllowed));

        if (notAllowed)
        {
            // Live 2026-10-05: the sub-heading needs the section headings' room, not to sit right under the last row.
            panel.CellRow(JoinFlowLayout.NotAllowedSpacer());
            panel.CellRow(JoinFlowLayout.NotAllowedHeading(model.NotAllowed.Count));
            DrawnSections[JoinFlowLayout.NotAllowedHeadRow] = UiCellRowHandle.SectionName(CellSection.Red);
            for (int i = 0; i < model.NotAllowed.Count; i++)
            {
                var row = model.NotAllowed[i];
                var cells = panel.CellRow(JoinFlowLayout.InSection(JoinFlowLayout.NotAllowed(row, inner), CellSection.Red,
                    last: i == model.NotAllowed.Count - 1));
                string id = row.Id;
                if (row.Unresolved) cells.OnClick(JoinFlowLayout.CellAction, () => NotAllowedAction(id));
            }
        }

        if (model.Recommended.Count > 0)
        {
            panel.CellRow(JoinFlowLayout.SectionHeading(JoinFlowLayout.RecommendedHeadRow, JoinText.RecommendedHeading,
                JoinText.RecommendedSubheading, CellSection.Green));
            DrawnSections[JoinFlowLayout.RecommendedHeadRow] = UiCellRowHandle.SectionName(CellSection.Green);
            for (int i = 0; i < model.Recommended.Count; i++)
            {
                var row = model.Recommended[i];
                string key = JoinPageModel.KeyOf(row);
                var spec = JoinFlowLayout.Recommended(row, session.IsChecked(row),
                    session.Changes.TryGetValue(key, out var change) ? change : "", JoinFlowClient.InstallStateOf(key));
                var cells = panel.CellRow(JoinFlowLayout.InSection(spec, CellSection.Green, last: i == model.Recommended.Count - 1));
                if (!row.Installed && JoinFlowClient.InstallStateOf(key)?.Phase != JoinInstallPhase.Refused)
                    cells.OnClick(JoinFlowLayout.CellCheck, () => ToggleRecommended(key));
                RecommendedRows.Add(cells);
            }
        }

        if (session.Notice.Length > 0)
            panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.NoticeRow, session.Notice, session.NoticeGood ? CellTone.Ok : CellTone.Orange, width));
        string footer = JoinText.CannotFixFooter(model);
        if (footer.Length > 0)
            panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.FooterRow, footer, CellTone.Muted, width));

        var list = JoinFlowLayout.ModsButtons(session);
        var buttons = panel.CellRow(DialogLayout.ButtonRowSpec(JoinFlowLayout.ButtonsRow, list, width));
        foreach (var b in list)
        {
            string key = b.Key;
            buttons.OnClick(key, () => Press(key));
        }
    }

    /// <summary>
    /// JOINFLOW2: the Thunderstore confirmation: the key-change line when this address's key changed, the warnings, Back
    /// and Install. (The red risk band went 2026-10-05 on request: the title already says the mods come from
    /// Thunderstore.)
    /// </summary>
    private static void BuildTsTrust(IUiPanel panel, JoinFlowSession session, float width)
    {
        var t = session.TsTrust;
        string keyChanged = t == null ? "" : JoinText.KeyChangedLine(t.PreviousFingerprint, t.PreviousSeen);
        if (keyChanged.Length > 0) panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.TsKeyChangedRow, keyChanged, CellTone.Normal, width));
        string warnings = t == null ? "" : JoinFlowLayout.TsWarnings(t);
        if (warnings.Length > 0) panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.TsWarningRow, warnings, CellTone.Orange, width));
        if (session.TrustError.Length > 0)
            panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.TrustErrorRow, session.TrustError, CellTone.Orange, width));
        var buttons = panel.CellRow(DialogLayout.ButtonRowSpec(JoinFlowLayout.ButtonsRow, new[]
        {
            new DialogButton(JoinFlowLayout.ButtonBack, JoinText.BackButton, DialogButtonRole.Cancel),
            new DialogButton(JoinFlowLayout.ButtonTsInstall, JoinText.TsInstallButton, DialogButtonRole.Main, CellTone.Gold),
        }, width));
        buttons.OnClick(JoinFlowLayout.ButtonBack, () => Press(JoinFlowLayout.ButtonBack));
        buttons.OnClick(JoinFlowLayout.ButtonTsInstall, () => Press(JoinFlowLayout.ButtonTsInstall));
    }

    /// <summary>JOINFLOW2: Restart needed (Later / Restart and rejoin) or Ready to join (Later / Join now).</summary>
    private static void BuildResult(IUiPanel panel, JoinFlowSession session, float width)
    {
        if (session.Notice.Length > 0)
            panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.NoticeRow, session.Notice, CellTone.Muted, width));
        var list = JoinFlowLayout.ResultButtons(session.Result!);
        var buttons = panel.CellRow(DialogLayout.ButtonRowSpec(JoinFlowLayout.ButtonsRow, list, width));
        foreach (var b in list)
        {
            string key = b.Key;
            buttons.OnClick(key, () => Press(key));
        }
    }

    /// <summary>JOINFLOW2: Rejoining / Joining: the progress line, the summary when there is one, Cancel (then Continue or Close).</summary>
    private static void BuildRejoin(IUiPanel panel, JoinFlowSession session, float width)
    {
        var v = session.RejoinView;
        if (v != null)
        {
            // The outcome is the body once it ended; the progress line only while it runs.
            if (!v.Finished)
                panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.RejoinStatusRow, JoinFlowLayout.RejoinStatus(v), CellTone.Gold, width, fontSize: 17f));
            if (v.Summary != null)
            {
                // Plain wrapped lines only (no fixed-height cell rows), so the page's height arithmetic and the
                // drawn rects agree (live 2026-10-05: the scroll read-back disagreed on this page).
                panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.RejoinRequiredHeadRow,
                    JoinText.SummaryRequiredHeading(v.Summary.RequiredReady, v.Summary.RequiredTotal), CellTone.Normal, width, CellAlign.Left, 17f));
                string line = JoinFlowLayout.RejoinRequiredLine(v.Summary);
                if (line.Length > 0)
                    panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.RejoinSummaryRow(0), line,
                        v.Summary.RequiredReady == v.Summary.RequiredTotal ? CellTone.Ok : CellTone.Amber, width, CellAlign.Left));
                panel.CellRow(JoinFlowLayout.Line(JoinFlowLayout.RejoinRecommendedRow,
                    JoinText.SummaryRecommendedHeading(v.Summary.RecommendedAdded), CellTone.Muted, width, CellAlign.Left));
            }
        }

        var button = JoinFlowLayout.RejoinButton(v!);
        var buttons = panel.CellRow(DialogLayout.ButtonRowSpec(JoinFlowLayout.ButtonsRow, new[] { button }, width));
        buttons.OnClick(JoinFlowLayout.ButtonCancel, () => Press(JoinFlowLayout.ButtonCancel));
    }

    private static string Rendered(JoinSection? section, out bool live) => Rendered(section, out live, out _);

    private static string Rendered(JoinSection? section, out bool live, out bool hasTime)
    {
        live = false;
        hasTime = false;
        if (section == null) return "";
        TimeZoneInfo serverZone;
        try { serverZone = section.Zone.Resolve(); }
        catch { serverZone = TimeZoneInfo.Utc; }
        return ResetCountdown.Render(section.Message, section.Reset, DateTime.UtcNow, serverZone, TimeZoneInfo.Local, out live, out hasTime);
    }

    // Whether the message page on show draws "Times shown in your local time." (readback drawn.timesLine).
    private static bool _timesLine;

    // ---- the handlers every button and the bridge share ------------------------------------

    /// <summary>Press a screen button by its key (<see cref="JoinFlowLayout"/>'s <c>Button*</c>). Returns what happened.</summary>
    internal static string Press(string button)
    {
        return Act(s => (button ?? "").Trim().ToLowerInvariant() switch
        {
            JoinFlowLayout.ButtonContinue => s.Continue(),
            JoinFlowLayout.ButtonViewMods => s.CanViewMods ? s.ViewMods() : "not offered: the mods page follows Continue",
            JoinFlowLayout.ButtonLeave => s.Page != JoinPage.Message ? "not on the message page"
                : !JoinFlowLayout.OffersLeave(s) ? "Leave is not offered in the world or in a preview" : s.Leave(),
            JoinFlowLayout.ButtonBack => s.Page == JoinPage.ThunderstoreTrust ? s.TsTrustBack()
                : s.BackToMessage(),
            JoinFlowLayout.ButtonClose => s.Page is JoinPage.Result or JoinPage.Rejoin ? "not on this page" : s.Close(),
            JoinFlowLayout.ButtonInstall => s.InstallSelected(),
            JoinFlowLayout.ButtonUpdateProgenitor => s.UpdateProgenitor(),
            JoinFlowLayout.ButtonTsInstall => s.TsTrustInstall(),
            JoinFlowLayout.ButtonJoinNow => s.ResultJoinNow(),
            JoinFlowLayout.ButtonRestart => s.ResultRestart(dryRun: false),
            JoinFlowLayout.ButtonLater => s.ResultLater(),
            JoinFlowLayout.ButtonCancel => s.RejoinCancel(),
            _ => "unknown button " + button,
        });
    }

    /// <summary>JOINFLOW2: Restart and rejoin with the bridge's dry run (the record is written; nothing quits).</summary>
    internal static string Restart(bool dryRun) => Act(s => s.ResultRestart(dryRun));

    internal static string RowAction(string id) => Act(s => s.RowAction(id));

    /// <summary>CLIENTPOLICY2: a "Not allowed on this server" row's action (Pause for this server, Turn off and restart).</summary>
    internal static string NotAllowedAction(string id) => Act(s => s.NotAllowedAction(id));

    internal static string ToggleRecommended(string key) => Act(s => s.ToggleRecommended(key) ? "toggled" : "not a recommended row that can be checked");

    internal static string SetDontShowAgain(bool on) => Act(s =>
    {
        if (s.Page != JoinPage.Message) return "not on the message page";
        if (!s.CanDismiss) return "not offered: the server shows its message on every join";
        s.DontShowAgain = on;
        return on ? "checked" : "unchecked";
    });

    private static bool OnEscape()
    {
        var s = JoinFlowClient.Session;
        if (s == null || s.Closed || !Owned.IsOpen) return false;
        if (_link != null)
        {
            LinkButton("cancel");
            return true;
        }

        Act(x => x.Escape());
        return true;
    }

    /// <summary>Escape on the screens (the bridge drives the same path as the key). Escape on the link confirmation cancels it.</summary>
    internal static string Escape()
    {
        var s = JoinFlowClient.Session;
        if (s == null || s.Closed) return "closed";
        if (_link != null) return LinkButton("cancel");
        return Act(x => x.Escape());
    }

    // ---- JOINLINKS1: links in the server message --------------------------------------------------

    /// <summary>
    /// Production: copy to the clipboard (the bridge's test clipboard while one is armed, never the player's).
    /// Tests replace it.
    /// </summary>
    internal static Func<string, bool> CopyToClipboard { get; set; } = DefaultCopy;

    /// <summary>Production: <c>Application.OpenURL</c>. Tests replace it; it is only ever handed an http(s) URL.</summary>
    internal static Action<string> OpenUrl { get; set; } = DefaultOpen;

    private static bool DefaultCopy(string text)
    {
        if (UiTextInjection.Clipboard != null)
        {
            UiTextInjection.Clipboard = text ?? "";
            return true;
        }

        return Ui.Ui.TryCopyToClipboard(text ?? "");
    }

    private static void DefaultOpen(string url)
    {
#if !PROGENITOR_TESTS
        UnityEngine.Application.OpenURL(url);
#endif
    }

    /// <summary>The links of the message as drawn on the message page now (what <see cref="OpenLink"/> indexes).</summary>
    internal static IReadOnlyList<UiTextLink> MessageLinks()
    {
        var s = JoinFlowClient.Session;
        if (s == null || s.Closed) return Array.Empty<UiTextLink>();
        return UiLinks.Find(Rendered(s.Model.Facts.Section, out _));
    }

    /// <summary>A click on the message box or reader: the link TMP found under the pointer opens the confirmation.</summary>
    private static void ClickedLink(IUiCellRow row)
    {
        int index;
        try { index = row.LinkAt(JoinFlowLayout.CellText); }
        catch { index = -1; }
        if (index >= 0) OpenLink(index);
    }

    /// <summary>
    /// Open the confirmation for the message's link <paramref name="index"/> (the on-screen click and the
    /// bridge's <c>link_click</c>). Only on the message page.
    /// </summary>
    internal static string OpenLink(int index)
    {
        var s = JoinFlowClient.Session;
        if (s == null || s.Closed) return "no join screen is open";
        if (s.Page != JoinPage.Message) return "not on the message page";
        var links = MessageLinks();
        if (index < 0 || index >= links.Count)
            return "no link " + index.ToString(CultureInfo.InvariantCulture) + " (the message has " + links.Count.ToString(CultureInfo.InvariantCulture) + ")";
        _link = new JoinLinkDialog(links[index], s.Model.ServerName);
        Plugin.Logger?.LogInfo("JoinFlow: link " + index.ToString(CultureInfo.InvariantCulture) + " asks to open " + _link.Url);
        Redraw();
        return "link dialog";
    }

    /// <summary>A link confirmation button: <c>copy</c>, <c>cancel</c> or <c>open</c>.</summary>
    internal static string LinkButton(string button)
    {
        var link = _link;
        if (link == null) return "no link dialog is open";
        string result;
        switch ((button ?? "").Trim().ToLowerInvariant())
        {
            case "copy":
            case JoinLinkDialog.ButtonCopy:
                result = link.Copy(CopyToClipboard);
                break;
            case "cancel":
            case JoinLinkDialog.ButtonCancel:
                _link = null;
                result = "canceled";
                break;
            case "open":
            case JoinLinkDialog.ButtonOpen:
                result = link.Open(OpenUrl);
                _link = null;
                break;
            default:
                return "unknown link button " + button;
        }

        Plugin.Logger?.LogInfo("JoinFlow: link " + result);
        Redraw();
        return result;
    }

    private static string Act(Func<JoinFlowSession, string> action)
    {
        var s = JoinFlowClient.Session;
        if (s == null || s.Closed) return "no join screen is open";
        string result;
        try { result = action(s); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("JoinFlow: button threw: " + e.Message);
            return "threw: " + e.Message;
        }

        Plugin.Logger?.LogInfo("JoinFlow: " + result + " (page " + JoinFlowSession.PageName(s.Page) + ")");
        if (s.Closed) Drop();
        else Redraw();
        return result;
    }

    // ---- frame ------------------------------------------------------------------------------

    private static double Now() => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

    /// <summary>
    /// Per frame, after the client's own frame steps (<see cref="JoinFlowClient.FrameSteps"/>, subscribed
    /// separately): the deny panel's sweep, a panel hidden by someone else, and the live countdown once a second
    /// while the message page with a ticking <c>{reset}</c> is on show. Nothing else.
    /// </summary>
    internal static void OnFrame()
    {
        // The fallback deny panel's own sweep (its frame hook is not installed while the screens own the refusal).
        try { ModManifestDenyPanel.DismissHidden(); }
        catch { /* a sweep must not break the frame */ }
        // Something other than our Escape hid the panel (another sweep): the screens close rather than
        // stay open behind an invisible panel.
        if (Owned.Held != null && !Owned.IsOpen)
        {
            var open = JoinFlowClient.Session;
            if (open != null && !open.Closed) open.Close();
            Drop();
            return;
        }

        try { RefreshTitleName(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: title name refresh threw: " + e.Message); }
        if (!_liveCountdown || _messageRow == null || !Owned.IsOpen) return;
        double now = Now();
        if (now < _nextTick) return;
        _nextTick = now + 1.0;
        var session = JoinFlowClient.Session;
        if (session == null || session.Page != JoinPage.Message) return;
        try
        {
            string text = Rendered(session.Model.Facts.Section, out bool live);
            _messageRow.SetCell(JoinFlowLayout.CellText, text: text);
            _liveCountdown = live;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("JoinFlow: countdown refresh threw: " + e.Message); }
    }

    /// <summary>The drawn rows, for the bridge (<c>join_ui read</c>): what is actually on screen.</summary>
    internal static Dictionary<string, object?> DescribeDrawn()
    {
        var d = new Dictionary<string, object?>
        {
            ["shown"] = Owned.IsOpen,
            ["page"] = JoinFlowSession.PageName(_drawnPage),
            ["liveCountdown"] = _liveCountdown,
            ["timesLine"] = _drawnPage == JoinPage.Message && _timesLine,
        };
        try
        {
            if (_messageRow != null)
                foreach (var cell in _messageRow.Cells)
                    if (cell.Key == JoinFlowLayout.CellText) d["messageText"] = cell.Text;
        }
        catch { /* the readback must not throw */ }

        // JOINLINKS1: the links the message box or reader draws now ({index, text, url}), the confirmation
        // when it is up, Open in browser's dry-run switch and record, and the mods page's section bands.
        try
        {
            var drawn = _messageRow ?? _readerRow;
            CellSpec? linked = null;
            if (drawn != null)
                foreach (var cell in drawn.Cells)
                    if (cell.Key == JoinFlowLayout.CellText) linked = cell;
            d["links"] = linked == null ? new List<object?>() : UiLinks.Describe(linked.LinksNow());
            d["linkDialog"] = _link?.Describe() ?? new Dictionary<string, object?> { ["open"] = false };
            d["linkOpener"] = JoinLinkOpener.Describe();
            var sections = new Dictionary<string, object?>();
            if (DrawnSections.TryGetValue(JoinFlowLayout.RequiredHeadRow, out var red)) sections["required"] = red;
            if (DrawnSections.TryGetValue(JoinFlowLayout.RecommendedHeadRow, out var green)) sections["recommended"] = green;
            if (DrawnSections.TryGetValue(JoinFlowLayout.NotAllowedHeadRow, out var na)) sections["notAllowed"] = na;
            d["sections"] = sections;
        }
        catch { /* the readback must not throw */ }

        return d;
    }

#if !PROGENITOR_TESTS
    private static string LiveWorldStamp()
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return "";
        return System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ":"
            + world.SequenceNumber.ToString(CultureInfo.InvariantCulture);
    }

    private static bool LiveInWorldReady()
    {
        try
        {
            var world = Worlds.Client;
            return world != null && world.IsCreated && string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    private static string? LoadedName(string id)
    {
        var rows = Ui.ModMenuInventory.Snapshot().Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Id, id, StringComparison.OrdinalIgnoreCase) && rows[i].DisplayName.Length > 0)
                return rows[i].DisplayName;
        }

        return null;
    }

    /// <summary>The joined server's name as the Mods menu knows it (its folder on this PC, else its catalog), or empty.</summary>
    private static string? ConnectedServerName()
    {
        try
        {
            var identity = Ui.ModsMenu.CurrentServerIdentity(remote: true);
            if (identity.HasName) return identity.Name;
            // The server rules channel reports the name in force (ServerHostSettings Name, or -serverName).
            return ServerRulesClient.State?.Row(ServerRules.Name)?.Loaded?.Trim() ?? "";
        }
        catch { return ""; }
    }

    /// <summary>
    /// Leave from the join screens. In a world (joined, or connected inside an install window) this is the
    /// game's own Leave: the production <c>ExitGame</c> view (the path the dev bridge's <c>disconnect</c>
    /// uses), which returns to the main menu. On the disconnect screen the client is already disconnected
    /// and the screens only close; the game's Back button returns to the menu.
    /// </summary>
    private static void LeaveToMenu()
    {
        var world = Worlds.Client;
        if (world == null || !world.IsCreated) return;
        ProjectM.UI.ViewManager.GoToView(ProjectM.UI.ViewLocation.ExitGame);
    }
#endif
}
