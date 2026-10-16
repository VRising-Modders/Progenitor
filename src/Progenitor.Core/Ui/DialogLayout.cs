using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>What a dialog button does: the dismissal, the main action, or one of a Choose list's options.</summary>
internal enum DialogButtonRole
{
    /// <summary>No / Cancel / Close: Escape's answer. Drawn leftmost.</summary>
    Cancel = 0,

    /// <summary>The main action (Yes, OK, Submit): Enter's answer. Drawn rightmost.</summary>
    Main = 1,

    /// <summary>One option of a <c>Choose</c> dialog drawn as a button, in the caller's order.</summary>
    Option = 2,
}

/// <summary>One button of a dialog: its cell key, caption, role and tone.</summary>
internal readonly struct DialogButton
{
    internal DialogButton(string key, string caption, DialogButtonRole role, CellTone tone = CellTone.Normal)
    {
        Key = key ?? "";
        Caption = caption ?? "";
        Role = role;
        Tone = tone;
    }

    internal string Key { get; }

    internal string Caption { get; }

    internal DialogButtonRole Role { get; }

    internal CellTone Tone { get; }
}

/// <summary>
/// DIALOGS1: the one layout every Progenitor dialog uses — the framework's confirmations and a
/// mod's <c>ctx.Dialogs</c> alike. Pure arithmetic (no Unity), so the geometry the runtime draws
/// and the geometry the tests pin are the same numbers.
///
/// <para><b>Top to bottom:</b> the title (centered, bold, wrapping), the optional body (muted,
/// centered, wrapping inside <see cref="BodyMaxWidth"/>), the optional content (a text field, an
/// inline error line, or a vertical option list), the button row, and the small version footer.
/// The frame is as tall as that content plus <see cref="FrameTopPad"/> and the footer band — no
/// title band (<see cref="PanelLayout.HeaderHeight"/>) and no 200 px floor, which is what left the
/// live stage 2 dialog with an empty band above its title.</para>
///
/// <para><b>The button row</b> is centered as a group: every button is as wide as the widest
/// caption plus padding (never below <see cref="MinButtonWidth"/>), Cancel is leftmost and the
/// main action rightmost, and two equal spacers take the rest of the row
/// (<see cref="ButtonRowSpec"/>). The live dialog's pair sat 64 units right of center because the
/// old in-pane prompt packed its buttons right-aligned against a scrollbar inset.</para>
/// </summary>
internal static class DialogLayout
{
    /// <summary>Title caption size (bold). The head block's page title is 34; a dialog's is smaller.</summary>
    internal const float TitleFontSize = 24f;

    // ---- DIALOGS2: the game's popup (CommonPopup, probe D1) ---------------------------------------------

    /// <summary>The popup's title and body gray (0.9), and a destructive caption's red.</summary>
    internal const float TextGrey = Progenitor.Ui.GameArt.GameSettingsLook.PopupTextGrey;

    internal static readonly (float R, float G, float B, float A) DangerCaptionRgb = (0.93f, 0.36f, 0.36f, 1f);

    /// <summary>The title row's band for the header rule (<c>Divider_Big_02</c>, 12 high) and its air.</summary>
    internal const float TitleRuleHeight = Progenitor.Ui.GameArt.GameSettingsLook.PopupDividerHeight;

    internal const float TitleRuleBottom = 4f;

    internal const float TitleRuleBand = TitleRuleHeight + TitleRuleBottom + 8f;

    /// <summary>The rule's width: the game's 592 of a 612-unit content, of ours.</summary>
    internal static float TitleRuleWidth(float frameWidth = FrameWidth) =>
        (float)Math.Round(ContentRowWidth(frameWidth) * (Progenitor.Ui.GameArt.GameSettingsLook.PopupDividerWidth / 612f));

    /// <summary>
    /// The game's popup buttons are equal and wide (256 of 616); each of ours is at least
    /// <see cref="PreferredButtonWidth"/> when the row holds them, else the widest caption's ask.
    /// </summary>
    internal const float PreferredButtonWidth = Progenitor.Ui.GameArt.GameSettingsLook.PopupButtonWidth;

    /// <summary>Body caption size (muted): a little larger than a note (16) so a sentence reads comfortably.</summary>
    internal const float BodyFontSize = 17f;

    /// <summary>DIALOGS2: a facts block's label and value size: the body's.</summary>
    internal const float FactFontSize = BodyFontSize;

    /// <summary>
    /// DIALOGS2: one wrapped line of a fact's value. DIALOGS3: 1.2 of the font, not the body's 1.3 —
    /// the facts read as one block, at the pitch of the game's description lines.
    /// </summary>
    internal const float FactLineHeight = FactFontSize * 1.2f;

    /// <summary>DIALOGS3: air under a fact row's last line (the dialog text's <see cref="TextPad"/> is 6).</summary>
    internal const float FactRowPad = 3f;

    /// <summary>
    /// DIALOGS3: extra air above the one-line note under the facts block, so it reads as the
    /// block's footnote rather than a last fact. The note row is this much taller and its text
    /// sits at the row's bottom.
    /// </summary>
    internal const float NoteTopMargin = 8f;

    /// <summary>DIALOGS2: the facts block's inset from the row's left and right edges.</summary>
    internal const float FactInset = 24f;

    /// <summary>DIALOGS2: the label column never takes more than this share of the block.</summary>
    internal const float FactLabelMaxFraction = 0.4f;

    /// <summary>FIX-NONMODS: room a fact label's text cell keeps around its glyphs (its padding, both sides).</summary>
    internal const float FactLabelPad = 2f * TextPad + 4f;

    /// <summary>FIX-NONMODS: the narrowest a fact label column is, so a short word is never cut.</summary>
    internal const float FactLabelMinWidth = 56f;

    /// <summary>DIALOGS2: a fact's value wraps to at most this many lines (FACTWRAP1: 4, the row fitted to the measured text; the readback keeps it whole).</summary>
    internal const int MaxFactLines = 4;

    /// <summary>The inline Prompt error line, in the readable warning tone (<see cref="CellTone.Orange"/>).</summary>
    internal const float ErrorFontSize = 16f;

    /// <summary>Caption size of every dialog button: the cell rows' button size.</summary>
    internal const float ButtonFontSize = MenuPanelLayout.HeaderButtonFontSize;

    /// <summary>The narrowest a dialog button gets, so <c>OK</c> is still a comfortable target.</summary>
    internal const float MinButtonWidth = 120f;

    /// <summary>
    /// Padding around a button caption, both sides together. With <see cref="CaptionSlack"/> it is
    /// the same generosity <see cref="HeadBlockLayout.ButtonWidth"/> needed live: the plain glyph
    /// estimate cut <c>Example</c> to <c>Exam…</c>.
    /// </summary>
    internal const float ButtonPad = 40f;

    /// <summary>How much wider than the glyph estimate a caption is allowed to be.</summary>
    internal const float CaptionSlack = HeadBlockLayout.ButtonCaptionSlack;

    /// <summary>Space between two buttons: the column model's cell gap.</summary>
    internal const float ButtonGap = CellLayout.Gap;

    /// <summary>
    /// The button row's height. Buttons are drawn at the cell rows' button height and pinned to the
    /// bottom of the row (<see cref="CellVAlign.Bottom"/>), so the extra height is air above them.
    /// </summary>
    internal const float ButtonRowHeight = 52f;

    /// <summary>The widest a body paragraph runs before it wraps: easier to read than the full row.</summary>
    internal const float BodyMaxWidth = 460f;

    /// <summary>The title wraps inside the row less a little air on each side.</summary>
    internal const float TitleSideAir = 20f;

    /// <summary>A Prompt's text field.</summary>
    internal const float InputWidth = 420f;

    /// <summary>A vertical Choose list's option buttons.</summary>
    internal const float ListButtonWidth = 360f;

    /// <summary>A vertical option row: one button at the cell rows' button height, centered.</summary>
    internal const float ListRowHeight = 40f;

    /// <summary>
    /// LISTROWS1 (live 2026-10-07: the Backups list cut "Before restore · Wed, Oct 7, 2026 1:39 A…" in its 360-wide
    /// option buttons): a row option is a full-width two-line list row (the option over a muted note), as tall as the
    /// Mods list's two-line rows.
    /// </summary>
    internal const float OptionRowHeight = CellLayout.TwoLineRowHeight;

    /// <summary>LISTROWS1: a row option's text keeps the facts block's inset from both edges, so the two line up.</summary>
    internal const float OptionRowInset = FactInset;

    /// <summary>LISTROWS1: a small heading over a group of row options.</summary>
    internal const float OptionHeadingFontSize = 15f;

    /// <inheritdoc cref="OptionHeadingFontSize"/>
    internal const float OptionHeadingHeight = 24f;

    /// <summary>LISTROWS1: air above a heading that starts a later group.</summary>
    internal const float OptionHeadingGap = 12f;

    /// <summary>LISTROWS1: the inset cells either side of a row option's text (whose cell key stays <c>option-N</c>).</summary>
    internal const string OptionPadLeft = "pad-left";

    /// <inheritdoc cref="OptionPadLeft"/>
    internal const string OptionPadRight = "pad-right";

    /// <summary>
    /// LISTROWS1: one row option: an inset, the two-line text (<paramref name="title"/> over the muted
    /// <paramref name="note"/>) filling the row, and the inset again. A list row (not plain), so it has its hover tint
    /// and its whole-row click target from birth (<see cref="CellRowSpec.RowClickAtBuild"/>), and a faint rule above it.
    /// </summary>
    internal static CellRowSpec OptionRowSpec(string rowKey, string cellKey, string title, string note, float rowWidth = RowWidth)
    {
        string text = (title ?? "").Replace('\n', ' ');
        string second = (note ?? "").Replace('\n', ' ');
        var cells = new List<CellSpec>(3)
        {
            new CellSpec(CellKind.Text, OptionPadLeft).With(text: "", width: CellWidth.Px(OptionRowInset), minWidth: OptionRowInset),
            new CellSpec(CellKind.Text, cellKey).With(text: second.Length > 0 ? text + "\n" + second : text, wrap: CellWrap.TwoLine,
                align: CellAlign.Left, width: CellWidth.Fill(CellLayout.MinTextWidth), minWidth: CellLayout.MinTextWidth),
            new CellSpec(CellKind.Text, OptionPadRight).With(text: "", width: CellWidth.Px(OptionRowInset), minWidth: OptionRowInset),
        };
        return new CellRowSpec(rowKey, cells, twoLine: true, height: OptionRowHeight, soft: true, separator: true);
    }

    /// <summary>LISTROWS1: a group heading over row options: small, muted, left at the rows' inset.</summary>
    internal static CellRowSpec OptionHeadingRowSpec(string rowKey, string text, bool first)
    {
        var cells = new List<CellSpec>(3)
        {
            new CellSpec(CellKind.Text, OptionPadLeft).With(text: "", width: CellWidth.Px(OptionRowInset), minWidth: OptionRowInset),
            new CellSpec(CellKind.Text, "heading").With(text: text ?? "", align: CellAlign.Left, tone: CellTone.Muted,
                fontSize: OptionHeadingFontSize, width: CellWidth.Fill(CellLayout.MinTextWidth), minWidth: CellLayout.MinTextWidth,
                vAlign: CellVAlign.Bottom),
            new CellSpec(CellKind.Text, OptionPadRight).With(text: "", width: CellWidth.Px(OptionRowInset), minWidth: OptionRowInset),
        };
        return new CellRowSpec(rowKey, cells, height: OptionHeadingHeight, plain: true, compact: true, gapAbove: first ? -1f : OptionHeadingGap);
    }

    /// <summary>LISTROWS1: the width a row option's text is placed at in a row <paramref name="rowWidth"/> wide.</summary>
    internal static float OptionTextWidth(float rowWidth = RowWidth)
    {
        var spec = OptionRowSpec("probe", "text", "x", "y", rowWidth);
        foreach (var p in CellLayout.Resolve(rowWidth, spec.Cells, spec.RightInset))
            if (p.Key == "text") return p.Width;
        return 0f;
    }

    /// <summary>
    /// LISTROWS1: whether both lines of a row option are drawn whole at <paramref name="rowWidth"/>: the runtime clips each
    /// line to the placed width with <see cref="MenuPanelLayout.ClipLabel"/> at the two-line fonts, so this is the same test.
    /// </summary>
    internal static bool OptionRowFits(string title, string note, float rowWidth = RowWidth)
    {
        float width = OptionTextWidth(rowWidth) - 4f;
        if (width <= 0f) return false;
        string t = title ?? "", n = note ?? "";
        return MenuPanelLayout.ClipLabel(t, width, CellLayout.TwoLineTitleFontSize) == t
            && MenuPanelLayout.ClipLabel(n, width, CellLayout.TwoLineNoteFontSize) == n;
    }

    /// <summary>
    /// INSTALLSIDES2 item 4: a reader dialog's text block (the README reader): tall enough to read,
    /// short enough that the title, the block and the Close row fit a 1080-high canvas.
    /// </summary>
    internal const float ReaderHeight = 560f;

    /// <summary>A short text gets a shorter block, never under this.</summary>
    internal const float ReaderMinHeight = 160f;

    /// <summary>A Choose with more options than this is a vertical list.</summary>
    internal const int MaxButtonOptions = 3;

    /// <summary>
    /// The most options a Choose takes. Ten 40-unit rows and their gaps stay well inside 80 % of the
    /// 1080-unit canvas with a title, a capped body and the button row, so the buttons are never
    /// pushed off screen by the list; a longer list belongs on a page.
    /// </summary>
    internal const int MaxListOptions = 10;

    /// <summary>
    /// Air under the last line of a title or body. A dialog text row is its text's height plus this —
    /// not the ordinary row's 40-unit floor, which left a one-line body with ~18 units of dead space
    /// under it (the live Choose dialog's extra band).
    /// </summary>
    internal const float TextPad = 6f;

    /// <summary>A title longer than this many lines is cut with an ellipsis (the full text stays in the readback).</summary>
    internal const int MaxTitleLines = 3;

    /// <summary>The canvas height every dialog is laid out against: the game's scaler matches height to 1080.</summary>
    internal const float CanvasHeight = 1080f;

    /// <summary>The air between the frame's top edge and the content (the content adds its own <see cref="PanelLayout.PadTop"/>).</summary>
    internal const float FrameTopPad = 16f;

    /// <summary>The shortest a dialog frame gets; a one-line confirmation is taller than this.</summary>
    internal const float MinFrameHeight = 120f;

    /// <summary>Row kinds the runtime draws as dialog text (centered, wrapping, own columns).</summary>
    internal const string KindTitle = "dialog-title";

    /// <inheritdoc cref="KindTitle"/>
    internal const string KindBody = "dialog-body";

    /// <summary>DIALOGS3: the note under the facts: drawn as body text with <see cref="NoteTopMargin"/> above it.</summary>
    internal const string KindNote = "dialog-note";

    /// <summary>True for a row the runtime draws as dialog body text (the body and the note).</summary>
    internal static bool IsBodyKind(string? kind) =>
        string.Equals(kind, KindBody, StringComparison.Ordinal) || string.Equals(kind, KindNote, StringComparison.Ordinal);

    /// <summary>Line heights the estimate uses; TMP measures the real ones in game.</summary>
    internal const float TitleLineHeight = TitleFontSize * 1.25f;

    /// <inheritdoc cref="TitleLineHeight"/>
    internal const float BodyLineHeight = BodyFontSize * 1.3f;

    /// <summary>The row a dialog's content is laid out in: the ordinary panel row.</summary>
    internal const float RowWidth = UnityUiRuntimeMetrics.RowWidth;

    /// <summary>A dialog frame is the ordinary panel width.</summary>
    internal const float FrameWidth = UnityUiRuntimeMetrics.PanelWidth;

    /// <summary>The separator between the mod's name and version and the framework footer.</summary>
    internal const string FooterSeparator = " · ";

    /// <summary>The ellipsis a too-long mod name is cut with (the versions are never cut).</summary>
    internal const string Ellipsis = "…";

    /// <summary>Width the title wraps at in a row <paramref name="rowWidth"/> wide.</summary>
    /// <summary>
    /// The width of a dialog's rows: the ordinary row width, never wider than the frame's viewport
    /// (the frame less <see cref="PanelLayout.PadSide"/> each side — the insets the layout pass writes
    /// for a dialog). This is what the runtime places every dialog row at.
    /// </summary>
    internal static float ContentRowWidth(float frameWidth = FrameWidth)
    {
        float frame = MenuPanelLayout.Positive(frameWidth, FrameWidth);
        // WIDEDIALOG1: a wider frame keeps the ordinary frame's side margin (640 → 560), never the 560 cap.
        float row = frame <= FrameWidth ? RowWidth : frame - (FrameWidth - RowWidth);
        return Math.Max(1f, Math.Min(row, frame - ViewportInsetLeft - ViewportInsetRight));
    }

    /// <summary>
    /// WIDEDIALOG1 (user report 2026-10-06: the Players joining window was "too small for the info"): the frame a wide
    /// dialog asks for. The runtime gives it at most <see cref="WideScreenShare"/> of the screen and never less than the
    /// ordinary <see cref="FrameWidth"/> (<see cref="FrameWidthFor"/>), so it fits small resolutions.
    /// </summary>
    internal const float WideFrameWidth = 880f;

    /// <summary>WIDEDIALOG1: the most of the screen's width a wide dialog takes.</summary>
    internal const float WideScreenShare = 0.9f;

    /// <summary>
    /// WIDEDIALOG1: the frame width for a dialog that asked for <paramref name="wanted"/> on a canvas
    /// <paramref name="availableWidth"/> wide: the wish, capped at <see cref="WideScreenShare"/> of the canvas, never
    /// below <see cref="FrameWidth"/>. 0 or less asks for nothing: the ordinary frame.
    /// </summary>
    internal static float FrameWidthFor(float wanted, float availableWidth)
    {
        if (float.IsNaN(wanted) || wanted <= FrameWidth) return FrameWidth;
        float cap = float.IsNaN(availableWidth) || availableWidth <= 0f ? wanted : availableWidth * WideScreenShare;
        return Math.Max(FrameWidth, Math.Min(wanted, cap));
    }

    /// <summary>The viewport's insets inside the frame (the rows are centered in the viewport).</summary>
    internal const float ViewportInsetLeft = PanelLayout.PadSide;

    /// <inheritdoc cref="ViewportInsetLeft"/>
    internal const float ViewportInsetRight = PanelLayout.PadSide;

    /// <summary>
    /// Where a row's center sits relative to the frame's center: the viewport's center offset (the rows
    /// are centered in it). Zero while the insets are equal.
    /// </summary>
    internal static float RowCentreOffset => (ViewportInsetLeft - ViewportInsetRight) * 0.5f;

    internal static float TitleWidth(float rowWidth = RowWidth) =>
        Math.Max(1f, MenuPanelLayout.Positive(rowWidth, RowWidth) - TitleSideAir * 2f);

    /// <summary>Width the body wraps at: <see cref="BodyMaxWidth"/>, or the title's width on a narrower row.</summary>
    internal static float BodyWidth(float rowWidth = RowWidth) => Math.Min(BodyMaxWidth, TitleWidth(rowWidth));

    /// <summary>
    /// The drawn order: Cancel first (leftmost), the options in the caller's order, the main action
    /// last (rightmost) — whatever order they were declared in.
    /// </summary>
    internal static IReadOnlyList<DialogButton> Order(IEnumerable<DialogButton> buttons)
    {
        var cancel = new List<DialogButton>();
        var options = new List<DialogButton>();
        var main = new List<DialogButton>();
        if (buttons != null)
        {
            foreach (var b in buttons)
            {
                if (b.Caption.Length == 0) continue;
                if (b.Role == DialogButtonRole.Cancel) cancel.Add(b);
                else if (b.Role == DialogButtonRole.Main) main.Add(b);
                else options.Add(b);
            }
        }

        var ordered = new List<DialogButton>(cancel.Count + options.Count + main.Count);
        ordered.AddRange(cancel);
        ordered.AddRange(options);
        ordered.AddRange(main);
        return ordered;
    }

    /// <summary>What one caption asks for: the glyph estimate with slack, plus padding.</summary>
    internal static float CaptionWidth(string? caption) =>
        CellLayout.EstimateTextWidth(caption, ButtonFontSize) * CaptionSlack + ButtonPad;

    /// <summary>
    /// The one width every button of a row gets: the widest caption's ask, never below
    /// <see cref="MinButtonWidth"/>, and — when the row cannot hold that many — the share of the row
    /// each can have (the caption then ellipsizes rather than the row overflowing).
    /// </summary>
    internal static float ButtonWidth(IReadOnlyList<string> captions, float rowWidth = RowWidth)
    {
        float want = MinButtonWidth;
        int n = 0;
        if (captions != null)
        {
            for (int i = 0; i < captions.Count; i++)
            {
                if (string.IsNullOrEmpty(captions[i])) continue;
                n++;
                want = Math.Max(want, CaptionWidth(captions[i]));
            }
        }

        if (n == 0) return want;
        float fit = MaxEqualWidth(n, rowWidth);
        return Math.Min(want, fit);
    }

    /// <summary>
    /// The widest each of <paramref name="count"/> equal buttons can be in the row: the row less a
    /// gap between every pair and a gap either side of the group (where the two spacers sit).
    /// </summary>
    internal static float MaxEqualWidth(int count, float rowWidth = RowWidth)
    {
        if (count < 1) return MenuPanelLayout.Positive(rowWidth, RowWidth);
        float row = MenuPanelLayout.Positive(rowWidth, RowWidth);
        return Math.Max(1f, (row - (count + 1) * ButtonGap) / count);
    }

    /// <summary>True when every button of the row gets its full caption width (nothing is ellipsized).</summary>
    internal static bool ButtonsFit(IReadOnlyList<string> captions, float rowWidth = RowWidth)
    {
        if (captions == null || captions.Count == 0) return true;
        float fit = MaxEqualWidth(captions.Count, rowWidth);
        for (int i = 0; i < captions.Count; i++)
            if (Math.Max(MinButtonWidth, CaptionWidth(captions[i])) > fit + .01f) return false;
        return true;
    }

    /// <summary>
    /// The button row as a cell row: a spacer, the buttons in <see cref="Order"/> at one fixed width,
    /// and a spacer. The spacers share what is left equally, so the group's center is the row's.
    /// </summary>
    internal static CellRowSpec ButtonRowSpec(string rowKey, IEnumerable<DialogButton> buttons, float rowWidth = RowWidth)
    {
        var ordered = Order(buttons);
        var captions = new List<string>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++) captions.Add(ordered[i].Caption);
        // DIALOGS2: equal, wide buttons like the game's popup (256 each when the row holds them).
        float width = Math.Max(ButtonWidth(captions, rowWidth), Math.Min(PreferredButtonWidth, MaxEqualWidth(captions.Count, rowWidth)));
        var cells = new List<CellSpec>(ordered.Count + 2) { new CellSpec(CellKind.Spacer, "dialog-left") };
        for (int i = 0; i < ordered.Count; i++)
        {
            var b = ordered[i];
            cells.Add(new CellSpec(CellKind.Button, b.Key).With(
                text: b.Caption,
                width: CellWidth.Px(width),
                minWidth: width,
                tone: b.Tone,
                fontSize: ButtonFontSize,
                vAlign: CellVAlign.Bottom,
                skin: CellSkin.PopupButton));
        }

        cells.Add(new CellSpec(CellKind.Spacer, "dialog-right"));
        return new CellRowSpec(rowKey, cells, height: ButtonRowHeight, plain: true);
    }

    /// <summary>A single centered cell (a text field, an option button, the error line) between two spacers.</summary>
    /// <summary>TOKENTIPS2: the note's text size and line height under a prompt's editor.</summary>
    internal const float InputNoteFontSize = 14f;

    /// <inheritdoc cref="InputNoteFontSize"/>
    internal const float InputNoteLineHeight = 18f;

    /// <summary>
    /// TOKENTIPS2: a muted note under a prompt's editor (the field's Placeholders), left-aligned and wrapped, every
    /// line kept (explicit line breaks too); the row is as tall as its text (<see cref="CellRowSpec.FitWrap"/>).
    /// </summary>
    internal static CellRowSpec InputNoteRowSpec(string rowKey, string text, float width)
    {
        int lines = 0;
        foreach (string paragraph in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            lines += Math.Max(1, MenuPanelLayout.WrappedLines(paragraph, Math.Max(40f, width - 8f), InputNoteFontSize, 8));
        var cell = new CellSpec(CellKind.Text, "note").With(text: text ?? "", tone: CellTone.Muted, wrap: CellWrap.Wrap,
            fontSize: InputNoteFontSize, vAlign: CellVAlign.Top, align: CellAlign.Left);
        cell.Width = CellWidth.Px(width);
        cell.MinWidth = width;
        var cells = new List<CellSpec>(3)
        {
            new CellSpec(CellKind.Spacer, "dialog-left"),
            cell,
            new CellSpec(CellKind.Spacer, "dialog-right"),
        };
        return new CellRowSpec(rowKey, cells, height: Math.Max(1, lines) * InputNoteLineHeight + 6f, plain: true, compact: true,
            gapAbove: 4f, fitWrap: true);
    }

    internal static CellRowSpec CentredRowSpec(string rowKey, CellSpec cell, float width, float height)
    {
        cell.Width = CellWidth.Px(width);
        cell.MinWidth = width;
        var cells = new List<CellSpec>(3)
        {
            new CellSpec(CellKind.Spacer, "dialog-left"),
            cell,
            new CellSpec(CellKind.Spacer, "dialog-right"),
        };
        return new CellRowSpec(rowKey, cells, height: height, plain: true);
    }

    /// <summary>
    /// A Choose dialog draws its options as buttons on the button row when there are at most
    /// <see cref="MaxButtonOptions"/> and every caption (with Cancel's) fits at one equal width;
    /// otherwise it is a vertical list of option buttons above a Cancel row.
    /// </summary>
    internal static bool OptionsAsButtons(IReadOnlyList<string> options, string? cancel, float rowWidth = RowWidth)
    {
        if (options == null || options.Count == 0 || options.Count > MaxButtonOptions) return false;
        var captions = new List<string>(options.Count + 1);
        if (!string.IsNullOrEmpty(cancel)) captions.Add(cancel!);
        for (int i = 0; i < options.Count; i++) captions.Add(options[i] ?? "");
        return ButtonsFit(captions, rowWidth);
    }

    /// <summary>Estimated wrapped lines of a title at <see cref="TitleFontSize"/>.</summary>
    internal static int TitleLines(string? title, float rowWidth = RowWidth) =>
        MenuPanelLayout.WrappedLines(title, TitleWidth(rowWidth), TitleFontSize, 12);

    /// <summary>Estimated wrapped lines of a body at <see cref="BodyFontSize"/>.</summary>
    internal static int BodyLines(string? body, float rowWidth = RowWidth) =>
        MenuPanelLayout.WrappedLines(body, BodyWidth(rowWidth), BodyFontSize, 40);

    /// <summary>An estimated text row's height for this many lines: the lines plus <see cref="TextPad"/>.</summary>
    internal static float TextRowHeight(int lines, float lineHeight) =>
        Math.Max(1, lines) * lineHeight + TextPad;

    /// <summary>
    /// A dialog text row's height from the measured text height: at least one line, plus
    /// <see cref="TextPad"/>, and never above <paramref name="cap"/> when one is given (the text is then
    /// cut with an ellipsis on screen). The title is always capped at <see cref="MaxTitleLines"/>.
    /// </summary>
    internal static float TextRowHeight(float measured, bool title, float cap = 0f)
    {
        float line = title ? TitleLineHeight : BodyLineHeight;
        float text = float.IsNaN(measured) || float.IsInfinity(measured) || measured < line ? line : measured;
        float h = text + TextPad;
        float titleCap = MaxTitleLines * TitleLineHeight + TextPad;
        if (title && h > titleCap) h = titleCap;
        if (cap > 0f && h > cap) h = Math.Max(line + TextPad, cap);
        return Math.Min(h, PanelLayout.MaxRowHeight);
    }

    /// <summary>The floor a dialog text row is reserved with (the panel's ordinary floor is 40).</summary>
    internal const float MinTextRowHeight = 1f;

    /// <summary>
    /// The tallest the body may be so the frame never passes 80 % of the screen: what is left of the
    /// frame cap after the top pad, the footer band, the content pads, the gaps between
    /// <paramref name="rowCount"/> rows and the other rows' heights. Never below one body line. The
    /// button row and the footer therefore always stay on screen; a longer body is cut with an
    /// ellipsis and stays whole in the readback.
    /// </summary>
    internal static float BodyCap(float screenHeight, float footerBand, float otherRowsHeight, int rowCount)
    {
        if (float.IsNaN(screenHeight) || float.IsInfinity(screenHeight) || screenHeight <= 0f)
            screenHeight = PanelLayout.FallbackScreenHeight;
        float frameCap = Math.Max(MinFrameHeight, screenHeight * PanelLayout.ScreenFraction);
        float gaps = Math.Max(0, rowCount - 1) * PanelLayout.RowGap;
        float cap = frameCap - FrameTopPad - Math.Max(0f, footerBand) - PanelLayout.PadTop - PanelLayout.PadBottom
            - gaps - Math.Max(0f, otherRowsHeight);
        return Math.Max(BodyLineHeight + TextPad, cap);
    }

    /// <summary>
    /// The content's height for rows of these heights: the panel's own arithmetic
    /// (<see cref="PanelLayout.NextRow"/>: top pad, a row gap between rows, bottom pad).
    /// </summary>
    internal static float ContentHeight(IEnumerable<float> rowHeights)
    {
        var layout = new PanelLayout();
        if (rowHeights != null)
            foreach (float h in rowHeights) layout.NextRow(h, MinTextRowHeight);
        return layout.ContentHeight;
    }

    /// <summary>
    /// The frame for this much content: the top pad, the content and the footer band, capped at
    /// <see cref="PanelLayout.ScreenFraction"/> of the screen (the content scrolls past that) and never
    /// below <see cref="MinFrameHeight"/>.
    /// </summary>
    internal static float FrameHeight(float contentHeight, float footerBand, float screenHeight)
    {
        if (float.IsNaN(screenHeight) || float.IsInfinity(screenHeight) || screenHeight <= 0f)
            screenHeight = PanelLayout.FallbackScreenHeight;
        float content = float.IsNaN(contentHeight) || contentHeight < 0f ? 0f : contentHeight;
        float band = float.IsNaN(footerBand) || footerBand < 0f ? 0f : footerBand;
        float wanted = FrameTopPad + content + band;
        float cap = Math.Max(MinFrameHeight, screenHeight * PanelLayout.ScreenFraction);
        if (wanted > cap) return cap;
        return wanted < MinFrameHeight ? MinFrameHeight : wanted;
    }

    /// <summary>
    /// JOINADMIN1: the band a dialog's bottom-pinned rows take under its scrolling body (the Players joining popup's
    /// status line and Close): the bottom pad, then each row and the gap above it, stacked as the runtime places them.
    /// Zero without pinned rows. The frame and the viewport both reserve it, so those rows never scroll away.
    /// </summary>
    internal static float PinnedBottomHeight(IReadOnlyList<float>? rowHeights)
    {
        if (rowHeights == null || rowHeights.Count == 0) return 0f;
        float h = PanelLayout.PadBottom;
        foreach (float r in rowHeights) h += Math.Max(0f, float.IsNaN(r) ? 0f : r) + PanelLayout.RowGap;
        return h;
    }

    /// <summary>The scrolling viewport inside a dialog frame <paramref name="frameHeight"/> tall.</summary>
    internal static float ViewportHeight(float frameHeight, float footerBand)
    {
        float h = frameHeight - FrameTopPad - Math.Max(0f, footerBand);
        return h < 0f ? 0f : h;
    }

    /// <summary>
    /// The footer of a dialog a mod opened: <c>&lt;mod name&gt; &lt;mod version&gt; · Progenitor
    /// &lt;version&gt;</c>, one line. When it is too wide for the footer's text rect the mod's name is
    /// cut with an ellipsis — never either version. An empty name or version drops that part; with
    /// neither, the plain framework footer is returned.
    /// </summary>
    internal static string ModFooter(string? modName, string? modVersion, string frameworkFooter, float maxWidth = 0f)
    {
        string framework = (frameworkFooter ?? "").Trim();
        string name = (modName ?? "").Trim();
        string version = (modVersion ?? "").Trim();
        if (name.Length == 0 && version.Length == 0) return framework;
        float width = maxWidth > 0f ? maxWidth : PanelFooterLayout.TextWidth(FrameWidth);
        string tail = (version.Length > 0 ? (name.Length > 0 ? " " : "") + version : "")
            + (framework.Length > 0 ? FooterSeparator + framework : "");
        string full = name + tail;
        if (FooterWidth(full) <= width || name.Length == 0) return full;
        for (int keep = name.Length - 1; keep >= 1; keep--)
        {
            string cut = name.Substring(0, keep).TrimEnd() + Ellipsis + tail;
            if (FooterWidth(cut) <= width) return cut;
        }

        return Ellipsis + tail;
    }

    /// <summary>The footer text's estimated width at the footer's font size.</summary>
    internal static float FooterWidth(string? text) => CellLayout.EstimateTextWidth(text, PanelFooterLayout.FontSize);
}

/// <summary>
/// DIALOGS1: which caption and control columns a panel row gets — the one decision the runtime's
/// layout pass makes per row (<c>UnityPanel.RowColumnsFor</c> calls this), game-free so the tests pin
/// the geometry a custom page's controls are actually drawn at.
/// </summary>
internal static class RowColumnRules
{
    /// <summary>
    /// The columns of a row of <paramref name="kind"/>: a dialog title or body gets its own centered
    /// width; a search field its own; a settings-grid row the grid's (a control row) or the grid's
    /// caption column (a row without a control); anything else the menu pane's or the ordinary
    /// panel's columns.
    /// </summary>
    internal static RowColumns For(
        string? kind, bool grid, bool hasControl, bool searchField, bool menuPane, float contentRowWidth, bool sizedToRow = false)
    {
        if (string.Equals(kind, DialogLayout.KindTitle, StringComparison.Ordinal))
            return new RowColumns(DialogLayout.TitleWidth(contentRowWidth), 0f, 0f, 0f);
        if (DialogLayout.IsBodyKind(kind))
            return new RowColumns(DialogLayout.BodyWidth(contentRowWidth), 0f, 0f, 0f);
        if (searchField) return MenuPanelLayout.SearchFieldColumns(contentRowWidth);
        // MENUKIT2: a settings-grid row is the game's Options row (ModSettingsRowGrid); the caller passes
        // the grid's width (the content less the description column when the column is shown).
        if (grid) return (sizedToRow ? ModSettingsRowGrid.ForWindow(contentRowWidth) : ModSettingsRowGrid.For(contentRowWidth)).Columns(hasControl);

        // WINDOWS1: a standard window's rows take their own row's width (sizedToRow), the ordinary panel the fixed 560.
        return menuPane
            ? MenuPanelLayout.Columns(contentRowWidth, hasControl)
            : sizedToRow ? PanelLayout.Columns(hasControl, contentRowWidth) : PanelLayout.Columns(hasControl);
    }

    /// <summary>A grid row's caption is left-aligned on any pane; a dialog's text is centered.</summary>
    internal static bool? CaptionLeft(string? kind, bool grid)
    {
        if (string.Equals(kind, DialogLayout.KindTitle, StringComparison.Ordinal)
            || DialogLayout.IsBodyKind(kind))
            return false;
        if (grid) return true;
        return null;
    }
}
