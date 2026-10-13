using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Ui;

namespace Progenitor.Networking;

/// <summary>
/// WIDEDIALOG1 (user report 2026-10-06: "seems like this window might be too small for the info"): the Players joining
/// panel's mod rows in two lines, game-free so a test can hold every cell inside the content width. Line 1: the mod's
/// name over its second line, the version rule button (Required only), and Remove at the right. Line 2: the reason at
/// the full width (its placeholder whole), Save beside it while a draft differs. Before, one 53 px row packed name, rule,
/// a 240 px reason and two buttons, which clipped the name, the reason and the last button at the 560 px content width.
/// JOINDRAFT1: the reason line has no Save (the reason is part of the draft, sent by Apply); the name line carries a small
/// marker (Added, Removed, Changed), and a removed row's name is struck through with Undo where Remove was.
/// </summary>
internal static class JoinAdminRowLayout
{
    /// <summary>The reason line's key: the head row's key plus this.</summary>
    internal const string ReasonSuffix = "-reason";

    // The cells (JoinAdminPanel uses the same keys).
    internal const string CellName = "name";
    internal const string CellRule = "rule";
    internal const string CellReason = "reason";
    internal const string CellSave = "save";
    internal const string CellRemove = "remove";
    internal const string CellMark = "mark";
    internal const string CellUndo = "undo";

    /// <summary>The space above a section heading (it sat 8 px under the previous section's last control).</summary>
    internal const float SectionGap = 24f;

    /// <summary>A section heading with <see cref="SectionGap"/> above it.</summary>
    internal static CellRowSpec SectionHeading(string key, string title, string count) =>
        JoinFlowLayout.Heading(key, title, count, gapAbove: SectionGap);

    internal const float RuleWidth = 130f;
    internal const float RemoveWidth = 48f;
    internal const float MarkWidth = 80f;
    internal const float UndoWidth = 90f;

    /// <summary>The reason line's height (the input box) and its gap under the name line.</summary>
    internal const float ReasonHeight = 38f;
    internal const float ReasonGap = 4f;

    /// <summary>
    /// The name line: name and second line, the draft marker, the rule button when <paramref name="rule"/> is given, Remove
    /// at the right. JOINDRAFT1: a <see cref="JoinAdminRowMark.Removed"/> row draws its name struck through and Undo in
    /// Remove's place (the rule button grayed).
    /// </summary>
    internal static CellRowSpec Head(string key, string name, string second, string? rule, bool removable, bool locked,
        JoinAdminRowMark mark = JoinAdminRowMark.None)
    {
        bool removed = mark == JoinAdminRowMark.Removed;
        var cells = new List<CellSpec>
        {
            new CellSpec(CellKind.Text, CellName) { Strike = removed }.With(text: name + "\n" + second, wrap: CellWrap.TwoLine, width: CellWidth.Fill(120f),
                tone: removed ? CellTone.Faint : null),
            new CellSpec(CellKind.Text, CellMark) { Visible = mark != JoinAdminRowMark.None }.With(text: MarkText(mark), width: CellWidth.Px(MarkWidth),
                tone: MarkTone(mark), fontSize: 14f, align: CellAlign.Right, vAlign: CellVAlign.Middle),
        };
        if (rule != null)
            cells.Add(new CellSpec(CellKind.Button, CellRule).With(text: rule, width: CellWidth.Px(RuleWidth),
                fontSize: DialogLayout.ButtonFontSize, skin: CellSkin.PopupButton, tone: locked || removed ? CellTone.Disabled : CellTone.Normal));
        if (removable && removed)
            cells.Add(new CellSpec(CellKind.Button, CellUndo).With(text: JoinAdminText.Undo, width: CellWidth.Px(UndoWidth),
                fontSize: DialogLayout.ButtonFontSize, skin: CellSkin.PopupButton, tone: locked ? CellTone.Disabled : CellTone.Gold,
                align: CellAlign.Right));
        else if (removable)
            cells.Add(new CellSpec(CellKind.Button, CellRemove).With(text: JoinAdminText.Remove, width: CellWidth.Px(RemoveWidth),
                fontSize: DialogLayout.ButtonFontSize, skin: CellSkin.PopupButton, tone: locked ? CellTone.Disabled : CellTone.Normal,
                align: CellAlign.Right));
        return new CellRowSpec(key, cells, twoLine: true, plain: true);
    }

    /// <summary>JOINDRAFT1: the marker's word.</summary>
    internal static string MarkText(JoinAdminRowMark mark) => mark switch
    {
        JoinAdminRowMark.Added => JoinAdminText.MarkAdded,
        JoinAdminRowMark.Removed => JoinAdminText.MarkRemoved,
        JoinAdminRowMark.Changed => JoinAdminText.MarkChanged,
        _ => "",
    };

    /// <summary>JOINDRAFT1: the marker's color: green added, red removed, amber changed.</summary>
    internal static CellTone MarkTone(JoinAdminRowMark mark) => mark switch
    {
        JoinAdminRowMark.Added => CellTone.OkBright,
        JoinAdminRowMark.Removed => CellTone.Danger,
        JoinAdminRowMark.Changed => CellTone.Amber,
        _ => CellTone.Muted,
    };

    /// <summary>The reason line: the field at the full width. JOINDRAFT1: no Save; what is typed is staged.</summary>
    internal static CellRowSpec Reason(string key, string draft)
    {
        return new CellRowSpec(key + ReasonSuffix, new List<CellSpec>
        {
            new CellSpec(CellKind.Input, CellReason) { MaxLength = JoinAdminLimits.MaxReasonChars }
                .With(text: draft, width: CellWidth.Fill(200f), skin: CellSkin.ValueBox, placeholder: JoinAdminLimits.PlaceholderReason),
        }, height: ReasonHeight, plain: true, compact: true, gapAbove: ReasonGap);
    }

    // JOINDRAFT1: the button rows (JoinAdminPanel uses the same keys).
    internal const string ButtonApply = "apply";
    internal const string ButtonDiscard = "discard";
    internal const string ButtonClose = "close";
    internal const string ButtonCancel = "cancel";

    /// <summary>JOINDRAFT1: the main page's buttons: Close, Discard, Apply (the last two grayed with nothing staged or while Apply runs).</summary>
    internal static List<DialogButton> MainButtons(bool canApply) => new()
    {
        new DialogButton(ButtonClose, JoinAdminText.Close, DialogButtonRole.Cancel),
        new DialogButton(ButtonDiscard, JoinAdminText.Discard, DialogButtonRole.Option, canApply ? CellTone.Normal : CellTone.Disabled),
        new DialogButton(ButtonApply, JoinAdminText.Apply, DialogButtonRole.Main, canApply ? CellTone.Gold : CellTone.Disabled),
    };

    /// <summary>JOINDRAFT1: Close with unapplied changes: Cancel (Escape's answer), Discard, Apply.</summary>
    internal static List<DialogButton> CloseButtons() => new()
    {
        new DialogButton(ButtonCancel, JoinAdminText.Cancel, DialogButtonRole.Cancel),
        new DialogButton(ButtonDiscard, JoinAdminText.Discard, DialogButtonRole.Option),
        new DialogButton(ButtonApply, JoinAdminText.Apply, DialogButtonRole.Main, CellTone.Gold),
    };

    /// <summary>JOINDRAFT1: how many Changes lines the pinned list shows (then <c>+N more</c>).</summary>
    internal const int ChangeSlots = 5;

    internal const string RowChangePrefix = "ja-change-";

    /// <summary>
    /// CHANGESFIT1 (live 2026-10-07: the Changes footer took ~170 px for two lines and left the body ~150 px): every
    /// footer line is one 22 px text line; hidden lines take no room (the runtime skips inactive pinned rows).
    /// </summary>
    internal const float ChangeLineHeight = 22f;

    internal const float ChangeFontSize = 15f;

    /// <summary>CHANGESFIT1: the status line's height with one line of text (<c>JoinFlowLayout.Line</c> at 15 px; it grows only for a long refusal).</summary>
    internal const float StatusLineHeight = 15f * 1.3f + 8f;

    /// <summary>JOINDRAFT1: one line of the Changes list: the change in plain words and its undo (×, a box-less link-like button).</summary>
    internal static CellRowSpec ChangeLine(int index, string text, CellTone tone) =>
        new(RowChangePrefix + index.ToString(CultureInfo.InvariantCulture), new List<CellSpec>
        {
            new CellSpec(CellKind.Text, JoinFlowLayout.CellText).With(text: text ?? "", wrap: CellWrap.Ellipsis, fontSize: ChangeFontSize, width: CellWidth.Fill(120f),
                tone: tone, vAlign: CellVAlign.Middle),
            new CellSpec(CellKind.Button, CellUndo).With(text: JoinAdminText.Remove, width: CellWidth.Px(32f),
                fontSize: ChangeFontSize + 2f, skin: CellSkin.Quiet, align: CellAlign.Right),
        }, height: ChangeLineHeight, plain: true, compact: true);

    /// <summary>CHANGESFIT1: the Changes heading: <c>Changes · 2 changes, not sent yet</c> on one compact line (shown only with changes).</summary>
    internal static CellRowSpec ChangesHeading(string key, int count) =>
        new(key, new List<CellSpec>
        {
            new CellSpec(CellKind.Text, JoinFlowLayout.CellText).With(text: JoinAdminText.ChangesHeading, fontSize: 16f, width: CellWidth.Px(90f),
                vAlign: CellVAlign.Middle),
            new CellSpec(CellKind.Muted, JoinFlowLayout.CellCount).With(text: JoinAdminText.ChangesCount(count), width: CellWidth.Fill(80f),
                vAlign: CellVAlign.Middle),
        }, height: ChangeLineHeight, plain: true, compact: true);

    /// <summary>CHANGESFIT1: a one-line muted footer line (<c>No changes</c>, <c>+N more</c>, the status line).</summary>
    internal static CellRowSpec FooterLine(string key, string text, CellTone tone, float height = ChangeLineHeight) =>
        new(key, new List<CellSpec>
        {
            new CellSpec(CellKind.Text, JoinFlowLayout.CellText).With(text: text ?? "", tone: tone, align: CellAlign.Left, wrap: CellWrap.Ellipsis,
                fontSize: 14f, width: CellWidth.Fill(40f), vAlign: CellVAlign.Middle),
        }, height: height, plain: true, compact: true);

    /// <summary>
    /// CHANGESFIT1: the heights of the footer rows that show with <paramref name="changes"/> staged, top to bottom: no
    /// changes is the one <c>No changes</c> line; else the heading, up to <see cref="ChangeSlots"/> lines and
    /// <c>+N more</c>; then the status line and the buttons.
    /// </summary>
    internal static List<float> FooterHeights(int changes)
    {
        var list = new List<float>();
        if (changes <= 0) list.Add(ChangeLineHeight);
        else
        {
            list.Add(ChangeLineHeight);
            for (int i = 0; i < Math.Min(changes, ChangeSlots); i++) list.Add(ChangeLineHeight);
            if (changes > ChangeSlots) list.Add(ChangeLineHeight);
        }

        list.Add(StatusLineHeight);
        list.Add(DialogLayout.ButtonRowHeight);
        return list;
    }

    /// <summary>JOINDRAFT1: a Changes line's color: a refusal orange, a conflict amber, else the body color.</summary>
    internal static CellTone ChangeTone(JoinAdminChange c) =>
        c.Error.Length > 0 ? CellTone.Orange : c.Conflict.Length > 0 ? CellTone.Amber : CellTone.Normal;

    /// <summary>JOINDRAFT1: a removed row's reason, read-only and struck through (no field to type in).</summary>
    internal static CellRowSpec RemovedReason(string key, string reason) =>
        new(key + ReasonSuffix, new List<CellSpec>
        {
            new CellSpec(CellKind.Muted, CellReason) { Strike = true }.With(text: "“" + reason + "”", width: CellWidth.Fill(200f), tone: CellTone.Faint),
        }, height: 24f, plain: true, compact: true, gapAbove: ReasonGap);

    /// <summary>A reason from the operator's file: read-only, quoted, muted, at the full width.</summary>
    internal static CellRowSpec FileReason(string key, string reason) =>
        new(key + ReasonSuffix, new List<CellSpec>
        {
            new CellSpec(CellKind.Muted, CellReason).With(text: "“" + reason + "”", width: CellWidth.Fill(200f)),
        }, height: 24f, plain: true, compact: true, gapAbove: ReasonGap);
}

/// <summary>JOINADMIN1: the panel's words.</summary>
internal static class JoinAdminText
{
    internal const string Title = "Players joining this server";
    internal const string PickRecommendTitle = "Recommend a mod";
    internal const string RecommendedHeading = "Recommended client mods";
    internal const string RecommendedHelp = "Optional mods players see when they join. Only mods listed here are recommended.";
    internal const string WindowHeading = "Install window";
    internal const string WindowLabel = "Seconds to install needed mods";
    // INSTALLWINDOW-COPY (user report 2026-10-06): "0 turns it off" read as "players can play without the required mods".
    // 0 means no extra time: the join is refused at once, as without the window (Handshake.ClientPolicy: seconds <= 0 is
    // never deferred).
    internal const string WindowHelp =
        "How long a player who is missing a required mod may stay on the join screens to install it before being disconnected. "
        + "0: no extra time; they are refused at once and can rejoin after fixing it. Max 300.";

    /// <summary>The second muted line: what a player can fix in the window.</summary>
    internal const string WindowFixes =
        "In the window they can install it from Thunderstore, turn on or swap in a copy they have, or pause a mod you don't allow.";
    internal const string AddRecommend = "+ Recommend a mod";
    internal const string UpdateOnServer = "Update on server";
    internal const string Apply = "Apply";

    // JOINDRAFT1: staged edits.
    internal const string Discard = "Discard";
    internal const string Undo = "Undo";
    internal const string MarkAdded = "Added";
    internal const string MarkRemoved = "Removed";
    internal const string MarkChanged = "Changed";
    internal const string ChangesHeading = "Changes";
    internal const string Applying = "Applying…";
    internal const string CloseTitle = "Unapplied changes";
    internal const string WaitForApply = "Wait for Apply to finish.";
    internal const string ApplyOrDiscardFirst = "Apply or discard your changes first.";
    internal const string Cancel = "Cancel";

    internal static string CloseBody(int n) =>
        "Apply your " + (n == 1 ? "1 change" : n.ToString(CultureInfo.InvariantCulture) + " changes") + "?";

    internal static string MoreChanges(int n) => "+" + n.ToString(CultureInfo.InvariantCulture) + " more (Discard, or undo some above)";

    internal static string ChangesCount(int n) => n == 0 ? "" : n == 1 ? "1 change, not sent yet" : n.ToString(CultureInfo.InvariantCulture) + " changes, not sent yet";
    internal const string Remove = "×";
    internal const string Recommend = "Recommend";
    internal const string Close = "Close";
    internal const string Back = "Back";
    internal const string RequiredToJoin = "Required to join";
    internal const string FromFile = "from progenitor-manifest.json";
    internal const string Loading = "Asking the server…";
    internal const string SearchPlaceholder = "Search client mods (installed or on Thunderstore)";
    internal const string PickRecommendBody = "Client mods only: installed on this PC, or found on Thunderstore. Players see the reason you add next.";
    internal const string NoCandidates = "No client mod matches. Type part of a name to search Thunderstore.";

    internal static string ShownCount(int n) => n == 1 ? "1 shown to players" : n.ToString(CultureInfo.InvariantCulture) + " shown to players";

    internal static string ModsCount(int n) => n == 1 ? "1 mod" : n.ToString(CultureInfo.InvariantCulture) + " mods";

    internal static string RefusedLine(int joins, int days) =>
        (joins == 1 ? "1 join" : joins.ToString(CultureInfo.InvariantCulture) + " joins") + " refused "
        + (days == 7 ? "this week" : "in the last " + days.ToString(CultureInfo.InvariantCulture) + " days");

    internal static string Size(long bytes) =>
        bytes <= 0 ? "" : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB"
            : (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
}

