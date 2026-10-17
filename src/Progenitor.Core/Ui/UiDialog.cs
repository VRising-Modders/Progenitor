using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>What a dialog asks: a yes/no, a notice, a pick from options, or a line of text.</summary>
internal enum UiDialogShape
{
    Confirm = 0,
    Notice = 1,
    Choose = 2,
    Prompt = 3,
}

/// <summary>
/// DIALOGS1: everything one dialog draws, independent of who opened it. <see cref="UiDialogView"/>
/// turns it into rows with the shared <see cref="DialogLayout"/>.
/// </summary>
internal sealed class DialogContent
{
    internal UiDialogShape Shape { get; set; } = UiDialogShape.Confirm;

    internal string Title { get; set; } = "";

    /// <summary>
    /// WIDEDIALOG1: the frame width this dialog asks for (0: the ordinary <see cref="DialogLayout.FrameWidth"/>); the
    /// runtime caps it to the screen (<see cref="DialogLayout.FrameWidthFor"/>).
    /// </summary>
    internal float FrameWidth { get; set; }

    /// <summary>TOKENTIPS2: a muted, wrapped note under a prompt's text box (the field's Placeholders); empty for none.</summary>
    internal string InputNote { get; set; } = "";

    internal string Body { get; set; } = "";

    /// <summary>Main action (Yes / OK / Submit), or empty for none.</summary>
    internal string MainCaption { get; set; } = "";

    internal CellTone MainTone { get; set; } = CellTone.Gold;

    /// <summary>
    /// WIPECONFIRM1: a Prompt whose main action stays disabled until this word is typed (case-insensitive); empty: none.
    /// </summary>
    internal string ConfirmWord { get; set; } = "";

    /// <summary>
    /// WORLDRESTORE1: the extra answer buttons wait for <see cref="ConfirmWord"/> too (a second destructive answer, such as
    /// "Restore and restart now").
    /// </summary>
    internal bool ExtrasNeedWord { get; set; }

    /// <summary>
    /// WORLDRESTORE1: a date-time field under the facts (<see cref="IUiPanel.DateTimeField"/>) with this caption; empty for
    /// none. <see cref="DateValue"/> is its first value (ISO UTC), <see cref="DateZone"/> the zone it is shown in.
    /// </summary>
    internal string DateCaption { get; set; } = "";

    internal string DateValue { get; set; } = "";

    internal TimeZoneInfo? DateZone { get; set; }

    /// <summary>WIPECONFIRM1: whether <paramref name="typed"/> unlocks the main action (always, without a word).</summary>
    internal bool Unlocks(string? typed) =>
        ConfirmWord.Length == 0 || string.Equals((typed ?? "").Trim(), ConfirmWord, StringComparison.OrdinalIgnoreCase);

    /// <summary>A destructive confirmation: Enter does not answer it; the main button needs a click.</summary>
    internal bool Danger { get; set; }

    /// <summary>The dismissal (No / Cancel / Close), or empty for none.</summary>
    internal string CancelCaption { get; set; } = "";

    /// <summary>Row key of the button row; a framework caller keeps its published <c>widget_click</c> handle.</summary>
    internal string RowKey { get; set; } = UiDialogs.ButtonsRow;

    internal string MainKey { get; set; } = UiDialogs.CellMain;

    internal string CancelKey { get; set; } = UiDialogs.CellCancel;

    /// <summary>A Choose dialog's options, in order.</summary>
    internal IReadOnlyList<string> Options { get; set; } = Array.Empty<string>();

    /// <summary>
    /// LISTROWS1: a muted second line per option (index-aligned with <see cref="Options"/>). When given, the options are
    /// full-width two-line rows (<see cref="OptionsAsRows"/>) instead of buttons: the option is the first line, its note the
    /// second, and the whole row is the click target.
    /// </summary>
    internal IReadOnlyList<string> OptionNotes { get; set; } = Array.Empty<string>();

    /// <summary>LISTROWS1: small headings drawn above the option rows they name (row mode only).</summary>
    internal IReadOnlyList<DialogOptionHeading> OptionHeadings { get; set; } = Array.Empty<DialogOptionHeading>();

    /// <summary>LISTROWS1: a Choose whose options are drawn as full-width two-line rows.</summary>
    internal bool OptionsAsRows => Shape == UiDialogShape.Choose && OptionNotes.Count > 0 && Options.Count > 0;

    /// <summary>LISTROWS1: option <paramref name="index"/>'s second line, or empty.</summary>
    internal string OptionNote(int index) => index >= 0 && index < OptionNotes.Count ? OptionNotes[index] ?? "" : "";

    /// <summary>A Prompt's field: its first value, empty hint and length cap (0: none).</summary>
    internal string InputInitial { get; set; } = "";

    internal string InputPlaceholder { get; set; } = "";

    internal int MaxLength { get; set; }

    /// <summary>
    /// TEXTEXPAND1: a Prompt whose field is a multi-line editor this many pixels tall (the text pipeline
    /// of every settings field, scrolled by the wheel and its scroll bar); 0 keeps the one-line field.
    /// </summary>
    internal float EditorHeight { get; set; }

    /// <summary>TEXTEXPAND1: the editor shows the text and takes no edits (a value the player may read, not change).</summary>
    internal bool InputReadOnly { get; set; }

    /// <summary>TEXTEXPAND1: a live <c>1,234 / 4,096</c> counter under the editor (<see cref="UiTextExpandLayout.CounterText"/>).</summary>
    internal bool Counter { get; set; }

    /// <summary>
    /// TEXTEXPAND1: the limit the counter shows (the setting's own), when it differs from
    /// <see cref="MaxLength"/>, the field's hard cap — raised to a stored value already longer, so the
    /// field never cuts it. 0 uses <see cref="MaxLength"/>.
    /// </summary>
    internal int CounterLimit { get; set; }

    /// <summary>TEXTEXPAND1: the number the counter divides by.</summary>
    internal int ShownLimit => CounterLimit > 0 ? CounterLimit : MaxLength;

    /// <summary>
    /// FORMDIALOG1: a caption on the left of a Prompt's one-line field (<c>Name</c>), which then fills the content row from
    /// the form's left edge (<see cref="DialogForm.CaptionedFieldRowSpec"/>); empty keeps the centered field.
    /// </summary>
    internal string InputCaption { get; set; } = "";

    /// <summary>
    /// FORMDIALOG1: a Prompt's check boxes, under the field (<see cref="DialogForm"/>), in their current state: the dialog
    /// replaces the list as the player flips one, so a dialog rebuilt after a framework dialog stepped in keeps them.
    /// </summary>
    internal IReadOnlyList<DialogCheck> Checks { get; set; } = Array.Empty<DialogCheck>();

    /// <summary>FORMDIALOG1: the small heading over the check boxes (<c>Show messages from</c>); empty for none.</summary>
    internal string ChecksHeading { get; set; } = "";

    /// <summary>FORMDIALOG1: the main action waits until at least one box is checked.</summary>
    internal bool ChecksRequireOne { get; set; }

    /// <summary>FORMDIALOG1: the line under the boxes while none is checked and one must be.</summary>
    internal string ChecksHint { get; set; } = DialogForm.DefaultNoneHint;

    /// <summary>FORMDIALOG1: whether the boxes as they stand may be saved.</summary>
    internal bool ChecksOk => DialogForm.Satisfied(Checks, ChecksRequireOne);

    /// <summary>Whether the main action is enabled for <paramref name="typed"/>: the confirmation word and the boxes.</summary>
    internal bool MainEnabled(string? typed) => Unlocks(typed) && ChecksOk;

    /// <summary>The version footer line.</summary>
    internal string Footer { get; set; } = "";

    /// <summary>
    /// Plain lines under the body, one label row each (centered, wrapping, the text color) — a
    /// panel with more to say than one paragraph (the join-refusal panel's summary and details).
    /// </summary>
    internal IReadOnlyList<string> Lines { get; set; } = Array.Empty<string>();

    /// <summary>
    /// DIALOGS2: label/value rows drawn under the body (<see cref="DialogFacts"/>): left-aligned, the
    /// labels muted in a column as wide as the widest label, the values bright.
    /// </summary>
    internal IReadOnlyList<DialogFact> Facts { get; set; } = Array.Empty<DialogFact>();

    /// <summary>WIPEPOLISH1: the body and the facts as text (<c>Label: value</c> lines), for logs and tests.</summary>
    internal string AllText
    {
        get
        {
            var parts = new List<string>();
            if (Body.Length > 0) parts.Add(Body);
            foreach (var f in Facts) parts.Add(f.ToString());
            return string.Join("\n", parts);
        }
    }

    /// <summary>DIALOGS2: one short sentence under the facts (muted, centered, wrapping), or empty.</summary>
    internal string Note { get; set; } = "";

    /// <summary>
    /// INSTALLSIDES2 item 4: a reader — long text drawn in one fixed-height block that scrolls inside
    /// the dialog with the game's scroll bar (headings, body, code; <see cref="CellLine"/>). Empty for
    /// every other dialog.
    /// </summary>
    internal IReadOnlyList<CellLine> Reader { get; set; } = Array.Empty<CellLine>();

    /// <summary>The reader block's height (px); 0 uses <see cref="DialogLayout.ReaderHeight"/>.</summary>
    internal float ReaderHeight { get; set; }

    /// <summary>Further buttons on the row, between the dismissal and the main action (the refusal panel's Show details).</summary>
    internal List<DialogButton> ExtraButtons { get; } = new();

    /// <summary>
    /// LIMITS1: the extra answer buttons do not fit the button row beside the dismissal and the main
    /// action at one equal width (three popup buttons with long captions in the 640-wide frame), so
    /// they are drawn as full-width popup buttons in rows above it instead.
    /// </summary>
    internal bool ExtrasStacked
    {
        get
        {
            if (ExtraButtons.Count == 0 || Shape == UiDialogShape.Choose) return false;
            var captions = new List<string>(ExtraButtons.Count + 2);
            if (CancelCaption.Length > 0) captions.Add(CancelCaption);
            foreach (var b in ExtraButtons) captions.Add(b.Caption);
            if (MainCaption.Length > 0) captions.Add(MainCaption);
            return !DialogLayout.ButtonsFit(captions, DialogLayout.ContentRowWidth());
        }
    }

    /// <summary>True when a Choose draws its options on the button row rather than as a list.</summary>
    internal bool OptionsAsButtons =>
        Shape == UiDialogShape.Choose && !OptionsAsRows && DialogLayout.OptionsAsButtons(Options, CancelCaption);

    /// <summary>The logical buttons of the button row (<see cref="DialogLayout.Order"/> draws them).</summary>
    internal List<DialogButton> RowButtons()
    {
        var list = new List<DialogButton>(4);
        if (CancelCaption.Length > 0) list.Add(new DialogButton(CancelKey, CancelCaption, DialogButtonRole.Cancel));
        if (OptionsAsButtons)
        {
            for (int i = 0; i < Options.Count; i++)
                list.Add(new DialogButton(UiDialogs.OptionKey(i), Options[i] ?? "", DialogButtonRole.Option));
        }

        if (!ExtrasStacked)
        {
            bool extrasOn = !ExtrasNeedWord || Unlocks(InputInitial);
            foreach (var b in ExtraButtons) list.Add(extrasOn ? b : new DialogButton(b.Key, b.Caption, b.Role, CellTone.Disabled));
        }

        if (Shape != UiDialogShape.Choose && MainCaption.Length > 0)
            list.Add(new DialogButton(MainKey, MainCaption, DialogButtonRole.Main, MainEnabled(InputInitial) ? MainTone : CellTone.Disabled));
        return list;
    }
}

/// <summary>LISTROWS1: a small heading drawn above option <see cref="Before"/> of a row-option Choose.</summary>
internal readonly struct DialogOptionHeading
{
    internal DialogOptionHeading(int before, string text)
    {
        Before = before;
        Text = text ?? "";
    }

    /// <summary>The index of the first option under the heading.</summary>
    internal int Before { get; }

    internal string Text { get; }
}

/// <summary>
/// DIALOGS1: the rows of one dialog on one panel, built from a <see cref="DialogContent"/> with the
/// shared <see cref="DialogLayout"/>: the title and body as dialog text rows, the Prompt field and
/// its (hidden) error line, a vertical option list for a long Choose, and the centered button row.
/// Also used by panels that keep their own lifecycle but want the dialog look (the join-refusal
/// panel).
/// </summary>
internal sealed class UiDialogView
{
    private UiDialogView() { }

    internal IUiLabel? Heading { get; private set; }

    internal IUiLabel? Body { get; private set; }

    /// <summary>The <see cref="DialogContent.Lines"/> rows.</summary>
    internal List<IUiLabel> LineRows { get; } = new();

    /// <summary>DIALOGS2: the <see cref="DialogContent.Facts"/> rows, in order.</summary>
    internal List<IUiCellRow> FactRows { get; } = new();

    /// <summary>DIALOGS2: the <see cref="DialogContent.Note"/> row.</summary>
    internal IUiLabel? NoteRow { get; private set; }

    /// <summary>INSTALLSIDES2 item 4: the <see cref="DialogContent.Reader"/> block.</summary>
    internal IUiCellRow? ReaderRow { get; private set; }

    /// <summary>WORLDRESTORE1: the <see cref="DialogContent.DateCaption"/> field.</summary>
    internal IUiDateTimeField? DateField { get; private set; }

    internal IUiCellRow? Input { get; private set; }

    /// <summary>TEXTEXPAND1: the counter row under the editor (<see cref="DialogContent.Counter"/>).</summary>
    internal IUiCellRow? CounterRow { get; private set; }

    /// <summary>TOKENTIPS2: the note under the editor (<see cref="DialogContent.InputNote"/>).</summary>
    internal IUiCellRow? InputNoteRow { get; private set; }

    internal IUiCellRow? Error { get; private set; }

    /// <summary>FORMDIALOG1: the heading over the check boxes (<see cref="DialogContent.ChecksHeading"/>).</summary>
    internal IUiCellRow? ChecksHeadingRow { get; private set; }

    /// <summary>FORMDIALOG1: one row per check box, in order.</summary>
    internal List<IUiCellRow> CheckRows { get; } = new();

    /// <summary>FORMDIALOG1: the hint under the boxes, shown while none is checked and one must be.</summary>
    internal IUiCellRow? ChecksHintRow { get; private set; }

    internal IUiCellRow? Buttons { get; private set; }

    internal List<IUiCellRow> OptionRows { get; } = new();

    /// <summary>LISTROWS1: the <see cref="DialogContent.OptionHeadings"/> rows, in order.</summary>
    internal List<IUiCellRow> OptionHeadingRows { get; } = new();

    /// <summary>LIMITS1: stacked extra answer buttons (<see cref="DialogContent.ExtrasStacked"/>), by cell key.</summary>
    internal List<(string Key, IUiCellRow Row)> ExtraRows { get; } = new();

    /// <summary>
    /// Give <paramref name="panel"/> the dialog chrome and footer and add the rows. Nothing here
    /// shows the panel.
    /// </summary>
    internal static UiDialogView Build(IUiPanel panel, DialogContent content, bool chrome = true)
    {
        var view = new UiDialogView();
        if (panel == null || content == null) return view;
        try { panel.Footer = content.Footer ?? ""; }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog footer: " + e.Message); }
        if (chrome)
        {
            // DialogChrome, not SelfPlacePanel: the runtime keeps sizing the dialog to its content
            // and only hands over the placement (centered, in the layout pass and the re-flow).
            Ui.HidePaneTitle(panel);
            Ui.DialogChrome(panel);
            // A modal box takes gameplay input wherever the pointer is: the backdrop already takes
            // every click, and Enter must not also reach the game.
            try { panel.CaptureMode = UiCaptureMode.WhileShown; }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog capture mode: " + e.Message); }
        }

        var host = panel as UiPanelHandle;
        // WIDEDIALOG1: a dialog that asks for a wider frame gets it before any row is measured.
        float frameWidth = content.FrameWidth > DialogLayout.FrameWidth ? content.FrameWidth : DialogLayout.FrameWidth;
        if (content.FrameWidth > DialogLayout.FrameWidth) Ui.SetDialogWidth(panel, content.FrameWidth);
        if (!string.IsNullOrWhiteSpace(content.Title))
            view.Heading = host != null ? host.AddDialogText(content.Title, title: true) : panel.Heading(content.Title);
        if (!string.IsNullOrWhiteSpace(content.Body))
            view.Body = host != null ? host.AddDialogText(content.Body, title: false) : panel.Note(content.Body);
        for (int i = 0; i < content.Lines.Count; i++)
        {
            try { view.LineRows.Add(panel.Label(content.Lines[i] ?? "")); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog line threw: " + e.Message); }
        }

        var facts = DialogFacts.Clean(content.Facts);
        if (facts.Count > 0)
        {
            float rowWidth = DialogLayout.ContentRowWidth(frameWidth);
            float labelWidth = DialogFacts.LabelWidth(facts, rowWidth);
            for (int i = 0; i < facts.Count; i++)
            {
                try { view.FactRows.Add(panel.CellRow(DialogFacts.RowSpec(i, facts[i], labelWidth, rowWidth))); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog fact threw: " + e.Message); }
            }
        }

        if (!string.IsNullOrWhiteSpace(content.Note))
            view.NoteRow = host != null ? host.AddDialogNote(content.Note) : panel.Note(content.Note);

        if (!string.IsNullOrWhiteSpace(content.DateCaption))
        {
            try { view.DateField = panel.DateTimeField(content.DateCaption, content.DateValue ?? "", content.DateZone, DateTimePickerPrecision.Minute); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog date field threw: " + e.Message); }
        }

        if (content.Reader.Count > 0)
        {
            // INSTALLSIDES2 item 4: one block, one scroll bar (the kit's scroll text with the game's
            // ScrollBar sprites), as wide as the dialog's content.
            var block = new CellSpec(CellKind.ScrollText, UiDialogs.CellReader);
            block.Lines = content.Reader;
            float height = content.ReaderHeight > 0f ? content.ReaderHeight : DialogLayout.ReaderHeight;
            try
            {
                view.ReaderRow = panel.CellRow(DialogLayout.CentredRowSpec(
                    UiDialogs.ReaderRow, block, DialogLayout.ContentRowWidth(frameWidth), height));
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog reader threw: " + e.Message); }
        }

        if (content.Shape == UiDialogShape.Prompt)
        {
            var field = new CellSpec(CellKind.Input, UiDialogs.CellField)
                .With(text: content.InputInitial ?? "", placeholder: content.InputPlaceholder ?? "");
            field.MaxLength = content.MaxLength > 0 ? content.MaxLength : 0;
            if (content.EditorHeight > 0f)
            {
                // TEXTEXPAND1: the expanded editor — as wide as the dialog's content, its height asked
                // of the cell (the row is that tall), multi-line, the settings value box's look.
                field.EditorLines = UiTextExpandLayout.LinesFor(content.EditorHeight);
                field.ReadOnly = content.InputReadOnly;
                field.HeightOverride = content.EditorHeight;
                field.Skin = CellSkin.ValueBox;
                view.Input = panel.CellRow(DialogLayout.CentredRowSpec(
                    UiDialogs.InputRow, field, DialogLayout.ContentRowWidth(frameWidth), content.EditorHeight));
                if (content.Counter)
                {
                    var counter = new CellSpec(CellKind.Text, UiDialogs.CellCounter).With(
                        text: UiTextExpandLayout.CounterText((content.InputInitial ?? "").Length, content.ShownLimit, content.InputReadOnly),
                        align: CellAlign.Right, tone: CellTone.Muted, fontSize: UiTextExpandLayout.CounterFontSize);
                    view.CounterRow = panel.CellRow(DialogLayout.CentredRowSpec(
                        UiDialogs.CounterRow, counter, DialogLayout.ContentRowWidth(frameWidth), UiTextExpandLayout.CounterRowHeight));
                }

                if (!string.IsNullOrWhiteSpace(content.InputNote))
                    view.InputNoteRow = panel.CellRow(DialogLayout.InputNoteRowSpec(UiDialogs.InputNoteRow, content.InputNote, DialogLayout.ContentRowWidth(frameWidth)));
            }
            else if (!string.IsNullOrWhiteSpace(content.InputCaption))
            {
                // FORMDIALOG1: the caption on the form's left edge, the field filling the rest of the row.
                view.Input = panel.CellRow(DialogForm.CaptionedFieldRowSpec(
                    UiDialogs.InputRow, content.InputCaption, field, DialogLayout.ContentRowWidth(frameWidth)));
            }
            else
            {
                view.Input = panel.CellRow(DialogLayout.CentredRowSpec(
                    UiDialogs.InputRow, field, DialogLayout.InputWidth, UnityUiRuntimeMetrics.RowHeight));
            }
            var error = new CellSpec(CellKind.Text, UiDialogs.CellError).With(
                text: "", align: CellAlign.Center, tone: CellTone.Orange, wrap: CellWrap.Wrap,
                fontSize: DialogLayout.ErrorFontSize, vAlign: CellVAlign.Top);
            view.Error = panel.CellRow(!string.IsNullOrWhiteSpace(content.InputCaption) && content.EditorHeight <= 0f
                ? DialogForm.FieldErrorRowSpec(UiDialogs.ErrorRow, content.InputCaption, error, DialogLayout.ContentRowWidth(frameWidth))
                : DialogLayout.CentredRowSpec(UiDialogs.ErrorRow, error, DialogLayout.BodyWidth(), UnityUiRuntimeMetrics.RowHeight));
            try { view.Error.Visible = false; }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog error row: " + e.Message); }
        }

        if (content.Checks.Count > 0)
        {
            // FORMDIALOG1: the heading, one line per box (the game's checkbox and its label), and the hint.
            if (!string.IsNullOrWhiteSpace(content.ChecksHeading))
            {
                try { view.ChecksHeadingRow = panel.CellRow(DialogForm.HeadingRowSpec(content.ChecksHeading)); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog checks heading threw: " + e.Message); }
            }

            for (int i = 0; i < content.Checks.Count; i++)
                view.CheckRows.Add(panel.CellRow(DialogForm.CheckRowSpec(i, content.Checks[i])));
            if (content.ChecksRequireOne)
            {
                try
                {
                    view.ChecksHintRow = panel.CellRow(DialogForm.HintRowSpec(content.ChecksHint));
                    view.ChecksHintRow.Visible = !content.ChecksOk;
                }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog checks hint threw: " + e.Message); }
            }
        }

        if (content.OptionsAsRows)
        {
            // LISTROWS1: full-width two-line rows (the option over its muted note), the whole row the click target, under
            // their small headings; nothing is cut at the dialog's width (DialogLayout.OptionRowFits).
            float rowWidth = DialogLayout.ContentRowWidth(frameWidth);
            for (int i = 0; i < content.Options.Count; i++)
            {
                foreach (var heading in content.OptionHeadings)
                {
                    if (heading.Before != i || string.IsNullOrWhiteSpace(heading.Text)) continue;
                    try
                    {
                        view.OptionHeadingRows.Add(panel.CellRow(DialogLayout.OptionHeadingRowSpec(
                            UiDialogs.OptionHeadingRow(view.OptionHeadingRows.Count), heading.Text, i == 0)));
                    }
                    catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog option heading threw: " + e.Message); }
                }

                view.OptionRows.Add(panel.CellRow(DialogLayout.OptionRowSpec(
                    UiDialogs.OptionRow(i), UiDialogs.OptionKey(i), content.Options[i] ?? "", content.OptionNote(i), rowWidth)));
            }
        }
        else if (content.Shape == UiDialogShape.Choose && !content.OptionsAsButtons)
        {
            for (int i = 0; i < content.Options.Count; i++)
            {
                // INSTALLSIDES1: a long Choose's options are the game's popup buttons too, like its button row.
                var option = new CellSpec(CellKind.Button, UiDialogs.OptionKey(i))
                    .With(text: content.Options[i] ?? "", fontSize: DialogLayout.ButtonFontSize, skin: CellSkin.PopupButton);
                view.OptionRows.Add(panel.CellRow(DialogLayout.CentredRowSpec(
                    UiDialogs.OptionRow(i), option, DialogLayout.ListButtonWidth, DialogLayout.ListRowHeight)));
            }
        }

        if (content.ExtrasStacked)
        {
            foreach (var extra in content.ExtraButtons)
            {
                var cell = new CellSpec(CellKind.Button, extra.Key)
                    .With(text: extra.Caption, fontSize: DialogLayout.ButtonFontSize, skin: CellSkin.PopupButton,
                        tone: content.ExtrasNeedWord && !content.Unlocks(content.InputInitial) ? CellTone.Disabled : extra.Tone);
                view.ExtraRows.Add((extra.Key, panel.CellRow(DialogLayout.CentredRowSpec(
                    UiDialogs.ExtraRow(extra.Key), cell, DialogLayout.ListButtonWidth, DialogLayout.ListRowHeight))));
            }
        }

        var buttons = content.RowButtons();
        if (buttons.Count > 0)
            view.Buttons = panel.CellRow(DialogLayout.ButtonRowSpec(content.RowKey, buttons, DialogLayout.ContentRowWidth(frameWidth)));
        return view;
    }

    /// <summary>Every row this view added, in order (for a pane's readback).</summary>
    internal IReadOnlyList<IUiHandle> Rows
    {
        get
        {
            var list = new List<IUiHandle>(6 + OptionRows.Count);
            if (Heading != null) list.Add(Heading);
            if (Body != null) list.Add(Body);
            list.AddRange(LineRows);
            list.AddRange(FactRows);
            if (NoteRow != null) list.Add(NoteRow);
            if (ReaderRow != null) list.Add(ReaderRow);
            if (DateField != null) list.Add(DateField);
            if (Input != null) list.Add(Input);
            if (CounterRow != null) list.Add(CounterRow);
            if (InputNoteRow != null) list.Add(InputNoteRow);
            if (Error != null) list.Add(Error);
            if (ChecksHeadingRow != null) list.Add(ChecksHeadingRow);
            list.AddRange(CheckRows);
            if (ChecksHintRow != null) list.Add(ChecksHintRow);
            list.AddRange(OptionHeadingRows);
            list.AddRange(OptionRows);
            foreach (var (_, row) in ExtraRows) list.Add(row);
            if (Buttons != null) list.Add(Buttons);
            return list;
        }
    }
}

/// <summary>
/// INSTALLUX2 item 1, DIALOGS1: one modal dialog. Every confirmation the framework asks for (the
/// Mods workspace's install / uninstall / reactivate / cancel-install / refusal boxes, the mod
/// page's y/n prompts, Reset all, a <c>ConfirmButton</c>'s question) and every dialog a mod opens
/// through <c>ctx.Dialogs</c> is one of these: its own panel in the middle of the screen with the
/// dialog chrome (<see cref="IUiRuntime.DialogChrome"/>: opaque frame, raycast-blocking backdrop,
/// content-sized, revealed after the layout pass), laid out by <see cref="DialogLayout"/>.
///
/// <para><b>One at a time.</b> <see cref="Current"/> is the dialog on screen. A mod's dialog queues
/// behind it (FIFO) and is shown when it closes. A framework dialog takes the screen at once: it
/// replaces a framework dialog without answering it (the INSTALLUX2 rule), and puts a mod's
/// dialog back at the head of the queue (rebuilt, with its typed text, when the screen is free).</para>
///
/// <para><b>Keys.</b> Escape answers with the dismissal (<see cref="NotifyEscape"/>, called from
/// <c>Ui.OnBootstrapKey</c> before the sweep that hides shown panels, so the owner is told — the
/// FUI29 failure). Enter answers with the main action (<see cref="NotifyEnter"/>, from the runtime's
/// typed-key pump, never while a multi-line field has focus), and is ignored for
/// <see cref="EnterGraceMs"/> after the dialog appears so the Enter that opened it cannot answer it.</para>
///
/// <para><b>Hot reload.</b> A mod's callbacks are held here, in Core, and nowhere else: the click
/// slots are bound to this object's own methods. Every unload reason drops the plugin's dialogs,
/// shown and queued, without running a callback (<see cref="DropOwner"/>, through the tracked
/// <c>ctx.Dialogs</c> and the <c>Ui.RemoveAll</c> sweep).</para>
/// </summary>
internal sealed class UiDialog : IDisposable
{
    private static UiDialog? _current;
    private static readonly List<UiDialog> Queue = new();
    private static int _answering;
    private static int _holding;
    private static int _escaping;
    private static bool _unloading;

    /// <summary>How long after a dialog appears Enter is ignored.</summary>
    internal const long EnterGraceMs = 250;

    /// <summary>Millisecond clock for the Enter grace; tests replace it.</summary>
    internal static Func<long> Clock { get; set; } = () => Environment.TickCount64;

    private readonly Func<string, IUiPanel> _factory;
    private Action? _yes;
    private Action? _no;
    private Action<int>? _pick;
    private Action<string>? _submit;
    private Func<string, string?>? _validate;
    private Action<DialogFormResult>? _save;
    private Func<DialogFormResult, string?>? _validateForm;
    private IUiPanel? _panel;
    private UiDialogView? _view;
    private string _text;
    private string _error = "";
    private bool _closed;
    private long _shownAt;

    private UiDialog(
        Func<string, IUiPanel> factory, DialogContent content, string tag, string kind, string key, object? owner)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        Content = content ?? throw new ArgumentNullException(nameof(content));
        Tag = tag ?? "";
        Kind = kind ?? "";
        Key = key ?? "";
        Owner = owner;
        _text = content.InputInitial ?? "";
    }

    /// <summary>The dialog on screen, or null.</summary>
    internal static UiDialog? Current => _current;

    /// <summary>Dialogs waiting behind <see cref="Current"/>, in the order they will be shown.</summary>
    internal static int QueuedCount => Queue.Count;

    /// <summary>What the dialog draws.</summary>
    internal DialogContent Content { get; }

    internal string Tag { get; }

    /// <summary>What is being confirmed (<c>install</c>, <c>uninstall</c>, <c>confirm</c>, …) — the caller's word.</summary>
    internal string Kind { get; }

    /// <summary>The row/mod the dialog is about; the readback's <c>key</c>.</summary>
    internal string Key { get; }

    /// <summary>The plugin owner of a mod's dialog; null for a framework dialog.</summary>
    internal object? Owner { get; }

    /// <summary>The panel of the dialog while it is (or was last) on screen; null while it has only queued.</summary>
    internal IUiPanel? Panel => _panel;

    /// <summary>Heading, body and buttons under the caller's keys (the INSTALLUX2 shape).</summary>
    internal UiPromptBlock Prompt { get; private set; } = UiPromptBlock.ForDialog("", null, null, null, "", "");

    /// <summary>The rows as built (the Prompt field, the error line, the option list too).</summary>
    internal UiDialogView? View => _view;

    /// <summary>A Prompt's current text.</summary>
    internal string Text => _text;

    /// <summary>A Prompt's error on show, or empty.</summary>
    internal string Error => _error;

    /// <summary>True while on screen: current, and its panel not disposed.</summary>
    internal bool IsOpen => !_closed && ReferenceEquals(_current, this) && _panel is not { IsDisposed: true };

    /// <summary>True while waiting in the queue.</summary>
    internal bool IsQueued => !_closed && Queue.Contains(this);

    /// <summary>
    /// Readback <c>dialog {open, key, kind, footer, title, queued}</c> for <c>mods_ui status</c>.
    /// <c>footer</c> is the version line the open dialog draws under its buttons, or empty.
    /// </summary>
    internal static Dictionary<string, object?> Describe()
    {
        var open = _current;
        string footer = "";
        try { footer = open?.Panel?.Footer ?? ""; }
        catch { /* a disposed panel reads as no footer */ }
        return new Dictionary<string, object?>
        {
            ["open"] = open != null,
            ["key"] = open?.Key ?? "",
            ["kind"] = open?.Kind ?? "none",
            ["footer"] = footer,
            ["title"] = open?.Content.Title ?? "",
            // BACKUPSDLG1: the sentence under the title (a notice's text), as drawn.
            ["body"] = open?.Content.Body ?? "",
            // DIALOGS2: the facts block as {label, value, tone} pairs, and the sentence under it.
            ["facts"] = DialogFacts.Describe(open?.Content.Facts),
            // DIALOGS2: where each part of the game's popup look came from (owned-copy / kit / ...).
            ["art"] = GameArt.SettingsArtLadder.DescribeDialog(),
            ["note"] = open?.Content.Note ?? "",
            // WIPECONFIRM1: a typed confirmation: the word, what is typed, and whether the main button is enabled.
            ["confirmWord"] = open?.Content.ConfirmWord ?? "",
            ["typed"] = open?._text ?? "",
            // The text the field draws (equal to typed after a keystroke or dialog_type; empty shows the placeholder).
            ["field"] = open?.FieldText ?? "",
            ["mainEnabled"] = open == null || open.Content.MainEnabled(open._text),
            // FORMDIALOG1: a form's boxes ({index, row, label, on}), the heading, and whether Save may be pressed for them.
            ["checks"] = ChecksReadback(open),
            ["checksHeading"] = open?.Content.ChecksHeading ?? "",
            ["checksOk"] = open == null || open.Content.ChecksOk,
            ["checksHint"] = open?.View?.ChecksHintRow is { } hintRow && hintRow.Visible ? open.Content.ChecksHint : "",
            // WORLDRESTORE1: the extra answers (their keys and captions), whether they wait for the word, and the date field.
            ["extras"] = ExtrasReadback(open),
            // DIALOGS3: what dialog_choose takes here: each answer's index (-1: none), cell key, caption and keywords.
            ["choices"] = ChoicesReadback(open),
            ["date"] = open != null && open.Content.DateCaption.Length > 0 ? open.DateValue : null,
            ["error"] = open?._error ?? "",
            // TEXTEXPAND1: the expanded editor's state (null for every other dialog).
            ["editor"] = open != null && open.Content.EditorHeight > 0f
                ? new Dictionary<string, object?>
                {
                    ["readOnly"] = open.Content.InputReadOnly,
                    ["maxLength"] = open.Content.MaxLength,
                    ["limit"] = open.Content.ShownLimit,
                    ["length"] = (open._text ?? "").Length,
                    ["counter"] = open.CounterText,
                    ["done"] = open.Content.MainCaption,
                    ["cancel"] = open.Content.CancelCaption,
                }
                : null,
            ["queued"] = Queue.Count,
            // KEYS4: the key-conflict dialog (open, which mod, its choices, how many wait behind it).
            ["keyConflict"] = KeybindConflictPrompt.Summary(),
        };
    }

    private static List<object?> ChoicesReadback(UiDialog? open)
    {
        var list = new List<object?>();
        if (open == null) return list;
        foreach (var (index, key, caption, aliases) in open.BridgeChoices())
            list.Add(new Dictionary<string, object?> { ["index"] = index, ["key"] = key, ["caption"] = caption, ["aliases"] = aliases });
        return list;
    }

    private static List<object?> ChecksReadback(UiDialog? open)
    {
        var list = new List<object?>();
        if (open == null) return list;
        var checks = open.Content.Checks;
        for (int i = 0; i < checks.Count; i++)
            list.Add(new Dictionary<string, object?>
            {
                ["index"] = i, ["row"] = DialogForm.CheckRow(i), ["label"] = checks[i].Label, ["on"] = checks[i].On,
                ["color"] = checks[i].Color,
            });
        return list;
    }

    private static List<object?> ExtrasReadback(UiDialog? open)
    {
        var list = new List<object?>();
        if (open == null) return list;
        bool on = !open.Content.ExtrasNeedWord || open.Content.Unlocks(open._text);
        foreach (var b in open.Content.ExtraButtons)
            list.Add(new Dictionary<string, object?> { ["key"] = b.Key, ["caption"] = b.Caption, ["enabled"] = on });
        return list;
    }

    private void GateExtras(bool on)
    {
        foreach (var b in Content.ExtraButtons)
        {
            try
            {
                if (!Content.ExtrasStacked) _view?.Buttons?.SetCell(b.Key, tone: on ? b.Tone : CellTone.Disabled);
                else if (_view != null)
                    foreach (var (rowKey, row) in _view.ExtraRows)
                        if (rowKey == b.Key) row.SetCell(b.Key, tone: on ? b.Tone : CellTone.Disabled);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog extra gate: " + e.Message); }
        }
    }

    /// <summary>
    /// A framework dialog (the INSTALLUX2 signature): build and show it now. <paramref name="rowKey"/>,
    /// <paramref name="yesKey"/> and <paramref name="noKey"/> keep a caller's existing
    /// <c>widget_click</c> handles. <paramref name="factory"/> is the panel source: production passes
    /// <c>Ui.Panel</c>'s wrapper, so nothing here references the runtime directly. The dialog on
    /// screen is replaced (a framework one) or put back in the queue (a mod's) — never answered.
    /// <paramref name="danger"/> marks a destructive confirmation (its Yes uninstalls, deletes or
    /// wipes): Enter does not answer it, and its main button is drawn in the danger tone whatever
    /// <paramref name="yesTone"/> says, as a mod's <c>danger: true</c> confirmation is.
    /// </summary>
    internal static UiDialog Open(
        Func<string, IUiPanel> factory,
        string tag,
        string kind,
        string key,
        string heading,
        string body,
        string yesCaption,
        LabelTone yesTone,
        string noCaption,
        string? rowKey = null,
        string? yesKey = null,
        string? noKey = null,
        string? footer = null,
        IReadOnlyList<DialogFact>? facts = null,
        string? note = null,
        IReadOnlyList<(DialogButton Button, Action Handler)>? extras = null,
        bool danger = false)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        var content = new DialogContent
        {
            Shape = UiDialogShape.Confirm,
            Title = heading ?? "",
            Body = body ?? "",
            Facts = DialogFacts.Clean(facts),
            Note = note ?? "",
            MainCaption = yesCaption ?? "",
            MainTone = danger ? CellTone.Danger : LabelTones.ToCellTone(yesTone),
            Danger = danger,
            CancelCaption = noCaption ?? "",
            RowKey = string.IsNullOrEmpty(rowKey) ? (string.IsNullOrEmpty(tag) ? "prompt" : tag) + "-buttons" : rowKey!,
            MainKey = string.IsNullOrEmpty(yesKey) ? UiPromptBlock.CellYes : yesKey!,
            CancelKey = string.IsNullOrEmpty(noKey) ? UiPromptBlock.CellNo : noKey!,
            // Every dialog is a Progenitor prompt, whichever plugin id its factory created the panel
            // under, so it carries the framework version line below its buttons.
            Footer = footer ?? Ui.FrameworkFooter,
        };
        if (extras != null)
            foreach (var (button, _) in extras)
                if (button.Caption.Length > 0) content.ExtraButtons.Add(button);
        var dialog = OpenContent(factory, content, tag, kind, key);
        if (extras != null)
            foreach (var (button, handler) in extras)
                dialog.OnExtra(button.Key, handler);
        return dialog;
    }

    private readonly Dictionary<string, Action> _extras = new(StringComparer.Ordinal);

    /// <summary>
    /// LIMITS1: what an extra answer button (<see cref="DialogContent.ExtraButtons"/>) does — it answers
    /// the dialog like Yes and No do (the box closes, then the handler runs). Bound when the dialog's
    /// buttons are built, so register it right after <see cref="Open"/>.
    /// </summary>
    internal UiDialog OnExtra(string key, Action? handler)
    {
        if (string.IsNullOrEmpty(key)) return this;
        if (handler == null) _extras.Remove(key);
        else _extras[key] = handler;
        if (_view?.Buttons is { } buttons && handler != null)
        {
            try { buttons.OnClick(key, () => AnswerExtra(handler)); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog extra button threw: " + e.Message); }
        }
        if (_view != null && handler != null)
        {
            foreach (var (rowKey, row) in _view.ExtraRows)
            {
                if (rowKey != key) continue;
                try { row.OnClick(key, () => AnswerExtra(handler)); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog extra row threw: " + e.Message); }
            }
        }
        return this;
    }

    /// <summary>
    /// SETTINGS1 stage 3: a framework dialog of any shape (the Mods page's <c>Show all</c> notice with
    /// its lines): built from <paramref name="content"/> and shown now, like <see cref="Open"/>.
    /// </summary>
    internal static UiDialog OpenContent(
        Func<string, IUiPanel> factory, DialogContent content, string tag, string kind, string key)
    {
        if (factory == null) throw new ArgumentNullException(nameof(factory));
        if (content == null) throw new ArgumentNullException(nameof(content));
        if (string.IsNullOrEmpty(content.Footer)) content.Footer = Ui.FrameworkFooter;
        var dialog = new UiDialog(factory, content, tag, kind, key, owner: null);
        if (_unloading) return dialog.Refused();
        if (_escaping > 0)
        {
            // Opened from a handler Escape just ran: the sweep that follows would hide it. It goes to
            // the head of the queue and is shown when the sweep is over (EndEscape).
            Queue.Insert(0, dialog);
            return dialog;
        }

        _holding++;
        try { TakeScreen(); }
        finally { _holding--; }
        dialog.Show();
        // A dialog that could not be shown must not leave a mod's dialog it displaced waiting.
        if (!dialog.IsOpen) Advance();
        return dialog;
    }

    /// <summary>
    /// A mod's dialog: queue it (FIFO) and show it at once when nothing else is on screen. Returns the
    /// dialog, which is the handle the plugin disposes to close (or unqueue) it.
    /// </summary>
    internal static UiDialog Enqueue(
        Func<string, IUiPanel> factory, DialogContent content, string kind, string key, object owner)
    {
        var dialog = new UiDialog(factory, content, UiDialogs.Tag, kind, key, owner);
        if (_unloading) return dialog.Refused();
        Queue.Add(dialog);
        Advance();
        return dialog;
    }

    /// <summary>What Yes / the main action does. Called after the dialog has closed itself.</summary>
    internal UiDialog OnYes(Action? handler)
    {
        _yes = handler;
        return this;
    }

    /// <summary>
    /// AUTOUPDATE1 review: what happens when the dialog goes away <b>without</b> an answer: another framework dialog took
    /// the screen, its panel left the screen, <see cref="Close"/>, or the Ui module unloading. Runs once, after the
    /// dialog is closed; never after an answer. A caller that holds something for the answer releases it here.
    /// </summary>
    internal UiDialog OnDropped(Action? handler)
    {
        _dropped = handler;
        return this;
    }

    private Action? _dropped;
    private bool _answered;

    /// <summary>What No (and Escape, and a refusal box's Close) does.</summary>
    internal UiDialog OnNo(Action? handler)
    {
        _no = handler;
        return this;
    }

    /// <summary>A Choose dialog's pick: the index into <see cref="DialogContent.Options"/>.</summary>
    internal UiDialog OnPick(Action<int>? handler)
    {
        _pick = handler;
        return this;
    }

    /// <summary>A Prompt's submit, after <paramref name="validate"/> (error text, or null to accept).</summary>
    internal UiDialog OnSubmit(Action<string>? handler, Func<string, string?>? validate)
    {
        _submit = handler;
        _validate = validate;
        return this;
    }

    /// <summary>
    /// FORMDIALOG1: a form's Save: <paramref name="handler"/> gets the text and every box's state, after
    /// <paramref name="validate"/> (error text under the field, or null to accept). Replaces <see cref="OnSubmit"/>.
    /// </summary>
    internal UiDialog OnSave(Action<DialogFormResult>? handler, Func<DialogFormResult, string?>? validate)
    {
        _save = handler;
        _validateForm = validate;
        return this;
    }

    /// <summary>FORMDIALOG1: the boxes' states now, in order.</summary>
    internal IReadOnlyList<bool> CheckStates => DialogForm.States(Content.Checks);

    /// <summary>
    /// FORMDIALOG1: flip box <paramref name="index"/> (a click on its line, the controller's A on it, the bridge): its
    /// mark, then the Save button and the hint follow whether one is checked. False when the dialog is not open or there
    /// is no such box.
    /// </summary>
    internal bool ToggleCheck(int index)
    {
        if (!IsOpen || index < 0 || index >= Content.Checks.Count) return false;
        Content.Checks = DialogForm.Flip(Content.Checks, index);
        bool on = Content.Checks[index].On;
        if (_view != null && index < _view.CheckRows.Count)
        {
            try { _view.CheckRows[index].SetCell(DialogForm.CellCheck, CellLayout.CheckText(on)); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog check mark: " + e.Message); }
        }

        GateMain();
        var hint = _view?.ChecksHintRow;
        if (hint != null)
        {
            try { hint.Visible = !Content.ChecksOk; }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog checks hint: " + e.Message); }
        }

        return true;
    }

    /// <summary>The main button lit or grayed for the word and the boxes as they stand.</summary>
    private void GateMain()
    {
        if (Content.MainCaption.Length == 0 || _view?.Buttons is not { } buttons) return;
        try { buttons.SetCell(Content.MainKey, tone: Content.MainEnabled(_text) ? Content.MainTone : CellTone.Disabled); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog main gate: " + e.Message); }
    }

    /// <summary>
    /// Escape answers the open dialog with its dismissal (a Notice's is its OK). Called from the key
    /// hook before the sweep that hides every shown panel.
    /// </summary>
    internal static bool NotifyEscape()
    {
        var open = _current;
        if (open == null) return false;
        open.Dismiss();
        return true;
    }

    /// <summary>
    /// Enter answers the open dialog with its main action: Yes, OK, a Prompt's submit (validated), or
    /// a single-button box's only button. A Choose has no main action and ignores it. False when
    /// nothing was answered.
    /// </summary>
    internal static bool NotifyEnter(bool typingElsewhere = false)
    {
        var open = _current;
        if (open == null || !open.IsOpen)
        {
            // No kit dialog: a panel drawn with the dialog layout under its own lifecycle (the
            // join-refusal panel) may take Enter as its main action.
            if (typingElsewhere) return false;
            try { return EnterFallback?.Invoke() ?? false; }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: dialog enter fallback threw: " + e.Message);
                return false;
            }
        }

        // Enter typed into another field (the game's chat, a search box) is that field's.
        if (typingElsewhere) return false;
        long now;
        try { now = Clock(); }
        catch { now = open._shownAt + EnterGraceMs; }
        if (now - open._shownAt < EnterGraceMs) return false;
        // FORMDIALOG1: the keyboard focus the player moved onto a box or a button takes Enter (Cancel cancels, a box
        // flips); a destructive main answer still needs a click, so Enter on it presses nothing.
        if (open._view != null && UiNav.PressFocusAmong(open._view.Rows, open.Content.Danger ? open.Content.MainKey : null)) return true;
        // A destructive confirmation needs a click on its button.
        if (open.Content.Danger) return false;
        var c = open.Content;
        switch (c.Shape)
        {
            case UiDialogShape.Choose:
                return false;
            case UiDialogShape.Prompt:
                open.Submit();
                return true;
            default:
                if (c.MainCaption.Length > 0) open.Answer(open._yes);
                else if (c.CancelCaption.Length > 0) open.Answer(open._no);
                else return false;
                return true;
        }
    }

    /// <summary>
    /// Hold the queue while the Escape sweep runs, so the dialog queued behind the one Escape just
    /// answered is not shown and then hidden by the same sweep. <see cref="EndEscape"/> releases it.
    /// </summary>
    internal static void BeginEscape()
    {
        _holding++;
        _escaping++;
    }

    /// <inheritdoc cref="BeginEscape"/>
    internal static void EndEscape()
    {
        if (_holding > 0) _holding--;
        if (_escaping > 0) _escaping--;
        Advance();
    }

    /// <summary>
    /// Enter's answer when no kit dialog is open: a panel with its own lifecycle that is drawn with
    /// the dialog layout registers its main action here (the join-refusal panel's Close) and clears
    /// it when it closes. Returns true when it answered.
    /// </summary>
    internal static Func<bool>? EnterFallback { get; set; }

    /// <summary>Close the dialog on screen, telling nobody. Used when one dialog replaces another.</summary>
    internal static void CloseCurrent() => _current?.Close();

    /// <summary>
    /// A dialog's owner is <paramref name="owner"/>, or a standard window that belongs to it (WINDOWS1 review: the Rename
    /// prompt of an ownerless <see cref="Ui.Window"/> is owned by the window, which matches its plugin's unload sweep).
    /// </summary>
    internal static bool OwnedBy(object? dialogOwner, object owner) =>
        ReferenceEquals(dialogOwner, owner) || (dialogOwner is UiWindowHandle window && window.BelongsTo(owner));

    /// <summary>
    /// Drop every dialog of <paramref name="owner"/> — on screen and queued — without running a
    /// callback. Every unload reason reaches this. Returns how many were dropped.
    /// </summary>
    internal static int DropOwner(object? owner)
    {
        if (owner == null) return 0;
        int dropped = 0;
        _holding++;
        try
        {
            for (int i = Queue.Count - 1; i >= 0; i--)
            {
                var queued = Queue[i];
                if (!OwnedBy(queued.Owner, owner)) continue;
                Queue.RemoveAt(i);
                queued.Release();
                dropped++;
            }

            var open = _current;
            if (open != null && OwnedBy(open.Owner, owner))
            {
                open.Release();
                dropped++;
            }
        }
        finally { _holding--; }

        if (dropped > 0) Plugin.Logger?.LogInfo("Ui: dropped " + dropped + " dialog(s) of an unloading plugin.");
        Advance();
        return dropped;
    }

    /// <summary>
    /// BACKUPSDLG1: drop every framework dialog carrying <paramref name="tag"/> — on screen and queued — without running a
    /// callback (a server-bound dialog when the client leaves that server). A mod's dialog is never touched. Returns how many
    /// were dropped.
    /// </summary>
    internal static int CloseTag(string tag)
    {
        if (string.IsNullOrEmpty(tag)) return 0;
        int dropped = 0;
        _holding++;
        try
        {
            for (int i = Queue.Count - 1; i >= 0; i--)
            {
                var queued = Queue[i];
                if (queued.Owner != null || !string.Equals(queued.Tag, tag, StringComparison.Ordinal)) continue;
                Queue.RemoveAt(i);
                queued.Release();
                dropped++;
            }

            var open = _current;
            if (open != null && open.Owner == null && string.Equals(open.Tag, tag, StringComparison.Ordinal))
            {
                open.Release();
                dropped++;
            }
        }
        finally { _holding--; }

        Advance();
        return dropped;
    }

    /// <summary>
    /// Core unload, whatever the reason: close and destroy every dialog this generation holds, on
    /// screen and queued, framework and mod alike, with no callback, and refuse to show another.
    /// <para>Why the latch: closing one dialog normally shows the next (<see cref="Advance"/>), and
    /// a panel the unload sweep disposes reports <see cref="OnLeftScreen"/>, which advances too. A
    /// queued dialog shown that way during the sweep (or by a plugin's <c>RemoveAll</c> after it)
    /// builds a fresh overlay under <c>progenitor-ui-unowned</c> that no later sweep of this
    /// generation reaches, so it outlived the swap next to the new generation's dialog.</para>
    /// Returns how many dialogs were closed.
    /// </summary>
    internal static int CloseAllForUnload()
    {
        _unloading = true;
        int closed = 0;
        _holding++;
        try
        {
            var queued = Queue.ToArray();
            Queue.Clear();
            var open = _current;
            _current = null;
            if (open != null && !open._closed)
            {
                try { open.Release(); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: unload dialog close threw: " + e.Message); }
                closed++;
            }

            foreach (var d in queued)
            {
                if (d._closed) continue;
                try { d.Release(); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: unload dialog drop threw: " + e.Message); }
                closed++;
            }
        }
        finally { _holding--; }

        EnterFallback = null;
        return closed;
    }

    /// <summary>True once <see cref="CloseAllForUnload"/> ran: this generation shows no more dialogs.</summary>
    internal static bool IsUnloading => _unloading;

    /// <summary>Dialogs this generation still holds (on screen plus queued); zero after unload.</summary>
    internal static int HeldCount => (_current != null ? 1 : 0) + Queue.Count;

    /// <summary>Opened after unload: closed at birth, never shown, no callback.</summary>
    private UiDialog Refused()
    {
        _closed = true;
        ReleaseHandlers();
        Plugin.Logger?.LogInfo("Ui: dialog '" + Content.Title + "' refused: the Ui module is unloading.");
        return this;
    }

    /// <summary>Drop the dialog: off screen or out of the queue, no callback. Idempotent.</summary>
    internal void Close()
    {
        if (_closed) return;
        Release();
        Advance();
    }

    public void Dispose() => Close();

    /// <summary>
    /// Test/reset seam: <b>close</b> the open dialog and empty the queue, not merely forget them.
    /// Forgetting left the dialog's cell rows in the process-wide row registry, so the next test's
    /// <c>widget_click prompt:&lt;key&gt;</c> resolved to a row whose panel nobody owns any more.
    /// </summary>
    internal static void ForgetForTests()
    {
        _holding++;
        try
        {
            var queued = Queue.ToArray();
            Queue.Clear();
            foreach (var d in queued)
            {
                try { d.Release(); }
                catch { /* a reset must not fail for the dialog it is dropping */ }
            }

            var open = _current;
            _current = null;
            try { open?.Release(); }
            catch { /* a reset must not fail for the dialog it is dropping */ }
        }
        finally
        {
            _holding = 0;
            _answering = 0;
            _escaping = 0;
            _unloading = false;
            EnterFallback = null;
            Clock = () => Environment.TickCount64;
        }
    }

    // ---- showing ------------------------------------------------------------------------------

    /// <summary>Clear the screen for a framework dialog: replace a framework one, requeue a mod's.</summary>
    private static void TakeScreen()
    {
        var open = _current;
        if (open == null) return;
        if (open.Owner == null)
        {
            open.Close();
            return;
        }

        // A mod's dialog steps back to the head of the queue, unanswered, and comes back (rebuilt,
        // with whatever the player had typed) once the framework's dialog is done.
        open.DropPanel();
        _current = null;
        Queue.Insert(0, open);
    }

    private static void Advance()
    {
        if (_unloading || _current != null || _answering > 0 || _holding > 0) return;
        while (Queue.Count > 0 && _current == null)
        {
            var next = Queue[0];
            Queue.RemoveAt(0);
            if (next._closed) continue;
            next.Show();
        }
    }

    private void Show()
    {
        if (_closed) return;
        if (_unloading)
        {
            Refused();
            return;
        }

        IUiPanel panel;
        try { panel = _factory(string.IsNullOrWhiteSpace(Content.Title) ? "Dialog" : Content.Title); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: dialog '" + Content.Title + "' could not be built: " + e.Message);
            _closed = true;
            return;
        }

        // A mod's dialog that cannot be drawn (the server, the Ui module skipped) would hold the
        // queue forever with nothing on screen to answer: it is dropped instead, with no callback.
        if (Owner != null && !panel.Enabled)
        {
            Plugin.Logger?.LogInfo("Ui: dialog '" + Content.Title + "' not shown: no client UI here.");
            try { panel.Dispose(); } catch { /* */ }
            _closed = true;
            ReleaseHandlers();
            return;
        }

        _panel = panel;
        Content.InputInitial = _text;
        UiDialogView view;
        try { view = UiDialogView.Build(panel, Content); }
        catch (Exception e)
        {
            // The chrome (and its backdrop) may already be on the panel: it goes with the panel.
            Plugin.Logger?.LogWarning("Ui: dialog '" + Content.Title + "' build threw: " + e.Message);
            try { panel.Dispose(); } catch { /* */ }
            _closed = true;
            ReleaseHandlers();
            return;
        }

        _view = view;
        // Anything that hides or disposes the panel behind the dialog's back (another owner's sweep,
        // an Escape sweep it was not answered by) drops the dialog and lets the queue move on.
        if (panel is UiPanelHandle host) host.LeftScreen = OnLeftScreen;
        Prompt = UiPromptBlock.ForDialog(Tag, view.Heading, view.Body, view.Buttons, Content.MainKey, Content.CancelKey);
        Bind(view);
        if (_error.Length > 0) ShowError(_error);
        _current = this;
        try { _shownAt = Clock(); }
        catch { _shownAt = 0; }
        try { Ui.Show(panel); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog show threw: " + e.Message); }
    }

    private void Bind(UiDialogView view)
    {
        var buttons = view.Buttons;
        if (buttons != null)
        {
            // Bound to this object's own methods: the plugin's delegates never reach a slot.
            if (Content.CancelCaption.Length > 0) buttons.OnClick(Content.CancelKey, Dismiss);
            if (Content.Shape == UiDialogShape.Prompt) buttons.OnClick(Content.MainKey, Submit);
            else if (Content.Shape != UiDialogShape.Choose && Content.MainCaption.Length > 0)
                buttons.OnClick(Content.MainKey, () => Answer(_yes));
            if (Content.OptionsAsButtons)
            {
                for (int i = 0; i < Content.Options.Count; i++)
                {
                    int index = i;
                    buttons.OnClick(UiDialogs.OptionKey(i), () => Pick(index));
                }
            }

            foreach (var pair in _extras)
            {
                var handler = pair.Value;
                buttons.OnClick(pair.Key, () => AnswerExtra(handler));
            }
        }

        foreach (var (key, row) in view.ExtraRows)
        {
            if (!_extras.TryGetValue(key, out var handler)) continue;
            row.OnClick(key, () => AnswerExtra(handler));
        }

        for (int i = 0; i < view.OptionRows.Count; i++)
        {
            int index = i;
            // LISTROWS1: a two-line option row is clicked anywhere on the row (pointer and click_at alike).
            if (Content.OptionsAsRows) view.OptionRows[i].OnRowClick(() => Pick(index));
            else view.OptionRows[i].OnClick(UiDialogs.OptionKey(i), () => Pick(index));
        }

        // FORMDIALOG1: a click anywhere on a check line (and the controller's A on it) flips its box.
        for (int i = 0; i < view.CheckRows.Count; i++)
        {
            int index = i;
            view.CheckRows[i].OnRowClick(() => ToggleCheck(index));
        }

        view.Input?.OnChanged(UiDialogs.CellField, OnTyped);
    }

    // ---- answering ----------------------------------------------------------------------------

    /// <summary>The dismissal: No / Cancel / Close; a Notice (OK only) answers with its OK.</summary>
    private void Dismiss()
    {
        if (!IsOpen) return;
        if (Content.CancelCaption.Length == 0 && Content.Shape == UiDialogShape.Notice)
        {
            Answer(_yes);
            return;
        }

        Answer(_no);
    }

    /// <summary>
    /// DIALOGS3: <c>widget_click</c> on the dialog: one of its button cells (or an option row) by
    /// cell key or caption, fired through the cell's own click slot. False when nothing of this
    /// dialog carries <paramref name="label"/>.
    /// </summary>
    internal bool TryClickForBridge(string? label, out Dictionary<string, object?> detail)
    {
        detail = new Dictionary<string, object?>();
        if (!IsOpen || _view == null || string.IsNullOrWhiteSpace(label)) return false;
        // A grayed answer (a Disabled-tone button) is refused, not pressed (UiClickGate); the first
        // refusal is what the caller reports when nothing clickable carries the label.
        Dictionary<string, object?>? refused = null;
        if (_view.Buttons is UiCellRowHandle buttons)
        {
            if (buttons.TryInvokeCell(label, out detail)) return true;
            if (UiClickGate.RefusalOf(detail) != null) refused = detail;
        }

        bool done = TryClickRowsForBridge(label!, ref detail, ref refused);
        if (!done && refused != null) detail = refused;
        return done;
    }

    private bool TryClickRowsForBridge(string label, ref Dictionary<string, object?> detail, ref Dictionary<string, object?>? refused)
    {
        for (int i = 0; i < _view!.OptionRows.Count; i++)
        {
            if (_view.OptionRows[i] is not UiCellRowHandle row) continue;
            // LISTROWS1: a row option answers to its option key, its row key or its first line, through the row's own
            // click slot (the one a pointer click reaches).
            if (Content.OptionsAsRows)
            {
                string key = label.Trim();
                bool match = string.Equals(key, UiDialogs.OptionKey(i), StringComparison.Ordinal)
                    || string.Equals(key, row.Key, StringComparison.Ordinal)
                    || (i < Content.Options.Count && string.Equals(key, Content.Options[i], StringComparison.OrdinalIgnoreCase));
                if (!match) continue;
                if (row.TryInvokeRow(out detail)) return true;
                if (refused == null && UiClickGate.RefusalOf(detail) != null) refused = detail;
                continue;
            }

            if (row.TryInvokeCell(label, out detail)) return true;
            if (refused == null && UiClickGate.RefusalOf(detail) != null) refused = detail;
        }

        // FORMDIALOG1: a check line answers to its row key or its label, through the row's own click slot.
        for (int i = 0; i < _view.CheckRows.Count; i++)
        {
            if (_view.CheckRows[i] is not UiCellRowHandle row) continue;
            string key = label.Trim();
            bool match = string.Equals(key, row.Key, StringComparison.Ordinal)
                || (i < Content.Checks.Count && Content.Checks[i].Label.Length > 0
                    && string.Equals(key, Content.Checks[i].Label, StringComparison.OrdinalIgnoreCase));
            if (!match) continue;
            if (row.TryInvokeRow(out detail)) return true;
            if (refused == null && UiClickGate.RefusalOf(detail) != null) refused = detail;
        }

        // WORLDRESTORE1: stacked extra answers (LIMITS1) are rows of their own.
        foreach (var (_, extraRow) in _view.ExtraRows)
        {
            if (extraRow is not UiCellRowHandle row) continue;
            if (row.TryInvokeCell(label, out detail)) return true;
            if (refused == null && UiClickGate.RefusalOf(detail) != null) refused = detail;
        }

        return false;
    }

    /// <summary>
    /// DIALOGS3: the answers <c>mods_ui dialog_choose</c> can press, as <c>(index, key, caption, aliases)</c>. A Choose
    /// lists its options (index = option index) and then its Cancel (no index); any other dialog lists its buttons in
    /// screen order (stacked extra answers top to bottom, then the button row left to right), index = that position, so
    /// a one-button dialog's button is <c>0</c>. <c>aliases</c> are the keywords that also press it (<c>yes</c>,
    /// <c>no</c>, <c>cancel</c>).
    /// </summary>
    internal List<(int Index, string Key, string Caption, string[] Aliases)> BridgeChoices()
    {
        var list = new List<(int Index, string Key, string Caption, string[] Aliases)>();
        string[] cancelAliases = { "no", "cancel" };
        if (Content.Shape == UiDialogShape.Choose)
        {
            for (int i = 0; i < Content.Options.Count; i++)
                list.Add((i, UiDialogs.OptionKey(i), Content.Options[i] ?? "", Array.Empty<string>()));
            if (Content.CancelCaption.Length > 0) list.Add((-1, Content.CancelKey, Content.CancelCaption, cancelAliases));
            return list;
        }

        var buttons = new List<DialogButton>();
        if (Content.ExtrasStacked) buttons.AddRange(Content.ExtraButtons);
        buttons.AddRange(DialogLayout.Order(Content.RowButtons()));
        // A Notice has only OK: yes, no and cancel all press it, as Enter and Escape do.
        bool noticeOk = Content.Shape == UiDialogShape.Notice && Content.CancelCaption.Length == 0;
        foreach (var b in buttons)
        {
            if (b.Caption.Length == 0) continue;
            string[] aliases = Array.Empty<string>();
            if (b.Role == DialogButtonRole.Main && b.Key == Content.MainKey)
                aliases = noticeOk ? new[] { "yes", "no", "cancel" } : new[] { "yes" };
            else if (b.Role == DialogButtonRole.Cancel && b.Key == Content.CancelKey)
                aliases = cancelAliases;
            list.Add((list.Count, b.Key, b.Caption, aliases));
        }

        return list;
    }

    /// <summary>DIALOGS3: what this dialog offers, for a <c>dialog_choose</c> error (<c>choices: 'OK' (0, yes, no, cancel)</c>).</summary>
    internal string ChoicesText()
    {
        var parts = new List<string>();
        foreach (var (index, _, caption, aliases) in BridgeChoices())
        {
            var names = new List<string>();
            if (index >= 0) names.Add(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            names.AddRange(aliases);
            parts.Add("'" + caption + "'" + (names.Count > 0 ? " (" + string.Join(", ", names) + ")" : ""));
        }

        return parts.Count == 0 ? "the open dialog has no buttons" : "choices: " + string.Join(", ", parts);
    }

    /// <summary>
    /// DIALOGS3: the cell key <c>mods_ui dialog_choose</c> clicks for <paramref name="choice"/>, or null with
    /// <paramref name="error"/> (listing the dialog's choices) when this dialog has no such button. A choice is an index
    /// (<see cref="BridgeChoices"/>), a keyword (<c>yes</c>: the main button; <c>no</c> / <c>cancel</c>: No / Cancel /
    /// Close, and a Notice's OK), or a button's caption or cell key, ignoring case. Live 2026-10-07: the restore
    /// notice's single OK did not answer to <c>OK</c>.
    /// </summary>
    internal string? ChoiceKey(string? choice, out string error)
    {
        error = "";
        string raw = (choice ?? "").Trim();
        if (raw.Length == 0)
        {
            error = "args.choice is required (a button's caption, yes, no, cancel or an index); " + ChoicesText();
            return null;
        }

        var choices = BridgeChoices();
        if (int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index))
        {
            foreach (var c in choices)
            {
                if (c.Index == index) return c.Key;
            }

            error = "the open dialog has no choice " + index + "; " + ChoicesText();
            return null;
        }

        string lower = raw.ToLowerInvariant();
        foreach (var c in choices)
        {
            if (Array.IndexOf(c.Aliases, lower) >= 0) return c.Key;
        }

        foreach (var c in choices)
        {
            if (string.Equals(c.Caption.Trim(), raw, StringComparison.OrdinalIgnoreCase)) return c.Key;
        }

        foreach (var c in choices)
        {
            if (string.Equals(c.Key, raw, StringComparison.OrdinalIgnoreCase)) return c.Key;
        }

        error = "the open dialog has no choice '" + raw + "'; " + ChoicesText();
        return null;
    }

    /// <summary>
    /// DIALOGS3 <c>mods_ui dialog_choose</c>: press the topmost open dialog's button for
    /// <paramref name="choice"/> through the same click path as <c>widget_click</c>. Throws
    /// <see cref="ArgumentException"/> (the bridge's ok:false) with nothing open or no such button.
    /// The readback says whether the dialog left the screen and what is open now.
    /// </summary>
    internal static Dictionary<string, object?> ChooseForBridge(string? choice)
    {
        var open = _current;
        if (open == null || !open.IsOpen) throw new ArgumentException("no dialog is open");
        string? key = open.ChoiceKey(choice, out string error);
        if (key == null) throw new ArgumentException(error);
        if (!open.TryClickForBridge(key, out var detail))
            throw new ArgumentException(UiClickGate.RefusalOf(detail) ?? "the open dialog's '" + key + "' button could not be clicked; " + open.ChoicesText());
        detail["choice"] = (choice ?? "").Trim().ToLowerInvariant();
        detail["title"] = open.Content.Title;
        detail["answered"] = !open.IsOpen;
        detail["dialog"] = Describe();
        return detail;
    }

    /// <summary>KEYS4: the bridge's answer to a Choose, as a click on option <paramref name="index"/> does.</summary>
    internal void PickForBridge(int index) => Pick(index);

    /// <summary>KEYS4: the bridge's dismissal, as the Cancel button or Escape does.</summary>
    internal void DismissForBridge() => Dismiss();

    private void Pick(int index)
    {
        if (!IsOpen) return;
        var pick = _pick;
        Answer(pick == null ? null : () => pick(index));
    }

    /// <summary>WORLDRESTORE1: an extra answer; one that waits for the confirmation word does nothing until it is typed.</summary>
    private void AnswerExtra(Action handler)
    {
        if (Content.ExtrasNeedWord && !Content.Unlocks(_text)) return;
        Answer(handler);
    }

    /// <summary>WORLDRESTORE1: the date field's value (ISO UTC), or the content's first value.</summary>
    internal string DateValue => _view?.DateField?.Value ?? Content.DateValue;

    /// <summary>WORLDRESTORE1 bridge: set the date field as the picker's Done would (no picker opened).</summary>
    internal bool SetDateForBridge(string isoUtc)
    {
        var field = _view?.DateField;
        if (field == null || !IsOpen) return false;
        field.Value = isoUtc ?? "";
        return true;
    }

    private void OnTyped(string text)
    {
        _text = text ?? "";
        if (_error.Length > 0) ClearError();
        // WORLDRESTORE1: extra answers that wait for the word light with it.
        if (Content.ConfirmWord.Length > 0 && Content.ExtrasNeedWord) GateExtras(Content.Unlocks(_text));
        // WIPECONFIRM1: the main action lights only once the confirmation word is typed.
        if (Content.ConfirmWord.Length > 0) GateMain();

        // WIPEPOLISH1: a short hint under the field while something is typed that is not the word.
        if (Content.ConfirmWord.Length > 0 && _text.Trim().Length > 0 && !Content.Unlocks(_text))
            ShowError(UiDialogs.ConfirmWordHint(Content.ConfirmWord));
        // TEXTEXPAND1: the counter follows each edit (no per-frame work while nothing changes).
        var counter = _view?.CounterRow;
        if (counter != null)
        {
            try { counter.SetCell(UiDialogs.CellCounter, UiTextExpandLayout.CounterText(_text.Length, Content.ShownLimit, Content.InputReadOnly)); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog counter: " + e.Message); }
        }
    }

    /// <summary>TEXTEXPAND1: the counter's text as drawn now, or empty without one.</summary>
    internal string CounterText
    {
        get
        {
            if (_view?.CounterRow is not { } row) return "";
            try
            {
                foreach (var cell in row.Cells)
                    if (cell != null && cell.Key == UiDialogs.CellCounter) return cell.Text ?? "";
            }
            catch { /* a disposed row reads as no counter */ }
            return "";
        }
    }

    /// <summary>A Prompt's main action: validate, show the error and stay open, or answer.</summary>
    internal void Submit()
    {
        if (!IsOpen) return;
        // TEXTEXPAND1: a read-only editor has nothing to commit; its main action (Enter) closes it.
        if (Content.InputReadOnly)
        {
            Dismiss();
            return;
        }

        string text = _text ?? "";
        // WIPECONFIRM1: a disabled main action does nothing (no error: the field's hint says what to type).
        if (!Content.Unlocks(text)) return;
        // FORMDIALOG1: no box checked where one must be: the hint under the boxes says so, and Save waits.
        if (!Content.ChecksOk)
        {
            try { if (_view?.ChecksHintRow is { } hint) hint.Visible = true; }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog checks hint: " + e.Message); }
            return;
        }

        var result = new DialogFormResult(text, Content.Checks);
        string? error = null;
        if (Content.MaxLength > 0 && text.Length > Content.MaxLength)
            error = UiDialogs.TooLong(Content.MaxLength);
        if (error == null && (_validate != null || _validateForm != null))
        {
            try { error = _validateForm != null ? _validateForm(result) : _validate!(text); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: dialog '" + Content.Title + "' validation threw: " + e.Message);
                error = UiDialogs.ValidationFailed;
            }
        }

        // Only null accepts: an empty or blank error is still a refusal, shown as a generic line.
        if (error != null)
        {
            ShowError(string.IsNullOrWhiteSpace(error) ? UiDialogs.NotAccepted : error);
            return;
        }

        var save = _save;
        if (save != null)
        {
            Answer(() => save(result));
            return;
        }

        var submit = _submit;
        Answer(submit == null ? null : () => submit(text));
    }

    /// <summary>
    /// Set the Prompt's text as the player's typing would (the bridge and tests use it). Typing writes the drawn field and
    /// then reports the change; this does the same: the visible field takes the text (its placeholder hides, the next key
    /// lands at the end), then <see cref="OnTyped"/> gates the buttons. Live 2026-10-07: the old path only reported the
    /// change, so <c>dialog_type RESTORE</c> lit Restore while the field still drew "Type RESTORE". No length cut here
    /// (tests reach the over-long guard through it); the bridge's <see cref="ModsMenu.TypeDialog"/> cuts at the field's
    /// limit as typing does.
    /// </summary>
    internal void SetText(string text)
    {
        text ??= "";
        if (_view?.Input is { } input)
        {
            // A live field echoes the write through its text slot (OnTyped with the same text): harmless, and the
            // explicit call below covers a field that does not echo.
            try { input.SetCell(UiDialogs.CellField, text: text); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog field write: " + e.Message); }
        }

        OnTyped(text);
    }

    /// <summary>The text the Prompt's field draws now (its cell's value), or empty without a field.</summary>
    internal string FieldText
    {
        get
        {
            if (_view?.Input is not { } input) return "";
            foreach (var cell in input.Cells)
            {
                if (string.Equals(cell.Key, UiDialogs.CellField, StringComparison.Ordinal)) return cell.Text ?? "";
            }

            return "";
        }
    }

    private void ShowError(string error)
    {
        _error = error;
        var row = _view?.Error;
        if (row == null) return;
        try
        {
            row.SetCell(UiDialogs.CellError, error);
            row.Visible = true;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog error line: " + e.Message); }
    }

    private void ClearError()
    {
        _error = "";
        var row = _view?.Error;
        if (row == null) return;
        try
        {
            row.SetCell(UiDialogs.CellError, "");
            row.Visible = false;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog error line: " + e.Message); }
    }

    /// <summary>
    /// Close first (so a handler that opens another dialog is not replaced by this one closing),
    /// then run the handler, then show whatever queued — after the handler, so a dialog the handler
    /// opens goes behind the ones already waiting.
    /// </summary>
    private void Answer(Action? handler)
    {
        if (!IsOpen) return;
        _answering++;
        try
        {
            _answered = true;
            Release();
            try { handler?.Invoke(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog '" + Content.Title + "' answer threw: " + e); }
        }
        finally { _answering--; }

        Advance();
    }

    // ---- teardown -----------------------------------------------------------------------------

    /// <summary>Closed for good: panel disposed, out of the queue, callbacks dropped. No Advance.</summary>
    private void Release()
    {
        if (_closed) return;
        _closed = true;
        Queue.Remove(this);
        if (ReferenceEquals(_current, this)) _current = null;
        DropPanel();
        ReleaseHandlers();
        var dropped = _answered ? null : _dropped;
        _dropped = null;
        if (dropped == null) return;
        try { dropped(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog '" + Content.Title + "' drop handler threw: " + e.Message); }
    }

    private void ReleaseHandlers()
    {
        _yes = null;
        _no = null;
        _pick = null;
        _submit = null;
        _validate = null;
        _save = null;
        _validateForm = null;
    }

    /// <summary>The panel left the screen without the dialog closing it: drop the dialog, no callback.</summary>
    private void OnLeftScreen()
    {
        if (_closed || !ReferenceEquals(_current, this)) return;
        Plugin.Logger?.LogInfo("Ui: dialog '" + Content.Title + "' left the screen without an answer; dropped.");
        Release();
        Advance();
    }

    private void DropPanel()
    {
        var panel = _panel;
        if (panel == null) return;
        if (panel is UiPanelHandle host) host.LeftScreen = null;
        try { panel.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: dialog close threw: " + e.Message); }
    }
}

/// <summary>
/// DIALOGS1: the mod-facing dialogs behind <c>ctx.Dialogs</c> (<see cref="PluginDialogs"/>), game-free
/// so the tests drive exactly what a plugin gets. Every dialog is queued (<see cref="UiDialog.Enqueue"/>)
/// under the plugin's owner and GUID; its panel is created under the plugin's own UI root.
/// </summary>
internal static class UiDialogs
{
    /// <summary>The tag every mod dialog carries (framework dialogs carry their caller's).</summary>
    internal const string Tag = "modDialog";

    /// <summary>Row and cell keys a bridge client clicks: <c>dialog-buttons/main</c>, <c>dialog-buttons/cancel</c>, <c>dialog-option-2/option-2</c>.</summary>
    internal const string ButtonsRow = "dialog-buttons";

    internal const string CellMain = "main";
    internal const string CellCancel = "cancel";
    internal const string InputRow = "dialog-input";
    internal const string CellField = "field";
    internal const string ErrorRow = "dialog-error";

    /// <summary>INSTALLSIDES2 item 4: a reader dialog's text block row and its cell.</summary>
    internal const string ReaderRow = "dialog-reader";

    /// <inheritdoc cref="ReaderRow"/>
    internal const string CellReader = "reader";
    internal const string CellError = "error";

    /// <summary>TEXTEXPAND1: the expanded editor's counter row and its cell.</summary>
    internal const string CounterRow = "dialog-counter";

    /// <summary>WIPEPOLISH1: the hint under a typed confirmation's field while the text is not the word.</summary>
    internal static string ConfirmWordHint(string word) => "Type " + word + " exactly to enable the button.";

    /// <summary>TOKENTIPS2: the note under a prompt's editor.</summary>
    internal const string InputNoteRow = "dialog-input-note";

    /// <inheritdoc cref="CounterRow"/>
    internal const string CellCounter = "counter";

    /// <summary>A validator that throws blocks the submit with this line (and the log says why).</summary>
    internal const string ValidationFailed = "This value could not be checked.";

    /// <summary>A validator that returns an empty or blank error still refuses; this is what is shown.</summary>
    internal const string NotAccepted = "Not accepted.";

    internal static string OptionKey(int index) => "option-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal static string OptionRow(int index) => "dialog-" + OptionKey(index);

    /// <summary>LISTROWS1: the <paramref name="index"/>th option heading row of a row-option Choose.</summary>
    internal static string OptionHeadingRow(int index) => "dialog-option-heading-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>LIMITS1: the row of a stacked extra answer button (its cell key is the button's own).</summary>
    internal static string ExtraRow(string key) => "dialog-extra-" + key;

    internal static string TooLong(int max) =>
        "At most " + max.ToString(System.Globalization.CultureInfo.InvariantCulture) + " characters.";

    /// <summary>The footer a mod's dialog carries: the mod's name and version, then the framework's, or the plain framework line.</summary>
    internal static string Footer(string? modName, string? modVersion, bool showModVersion) =>
        showModVersion
            ? DialogLayout.ModFooter(modName, modVersion, Ui.FrameworkFooter)
            : Ui.FrameworkFooter;

    internal static UiDialog Confirm(
        object owner, string pluginId, string footer,
        string title, string body, string yes, Action onYes, string no, Action? onNo, bool danger,
        IReadOnlyList<DialogFact>? facts = null, string? note = null)
    {
        if (onYes == null) throw new ArgumentNullException(nameof(onYes));
        var content = new DialogContent
        {
            Shape = UiDialogShape.Confirm,
            Title = title ?? "",
            Body = body ?? "",
            Facts = DialogFacts.Clean(facts),
            Note = note ?? "",
            MainCaption = string.IsNullOrWhiteSpace(yes) ? "OK" : yes,
            MainTone = danger ? CellTone.Danger : CellTone.Gold,
            Danger = danger,
            CancelCaption = string.IsNullOrWhiteSpace(no) ? "Cancel" : no,
            Footer = footer,
        };
        return Queue(owner, pluginId, content, "confirm").OnYes(onYes).OnNo(onNo);
    }

    internal static UiDialog Notice(
        object owner, string pluginId, string footer, string title, string body, string ok, Action? onClose,
        IReadOnlyList<DialogFact>? facts = null, string? note = null)
    {
        var content = new DialogContent
        {
            Shape = UiDialogShape.Notice,
            Title = title ?? "",
            Body = body ?? "",
            Facts = DialogFacts.Clean(facts),
            Note = note ?? "",
            MainCaption = string.IsNullOrWhiteSpace(ok) ? "OK" : ok,
            MainTone = CellTone.Gold,
            Footer = footer,
        };
        return Queue(owner, pluginId, content, "notice").OnYes(onClose);
    }

    internal static UiDialog Choose(
        object owner, string pluginId, string footer,
        string title, string body, IReadOnlyList<string> options, Action<int> onPick, string cancel, Action? onCancel)
    {
        if (onPick == null) throw new ArgumentNullException(nameof(onPick));
        if (options == null || options.Count == 0) throw new ArgumentException("At least one option is required.", nameof(options));
        if (options.Count > DialogLayout.MaxListOptions)
            throw new ArgumentException(
                "At most " + DialogLayout.MaxListOptions + " options; a longer list belongs on a page.", nameof(options));
        var copy = new List<string>(options.Count);
        for (int i = 0; i < options.Count; i++) copy.Add(options[i] ?? "");
        var content = new DialogContent
        {
            Shape = UiDialogShape.Choose,
            Title = title ?? "",
            Body = body ?? "",
            Options = copy,
            CancelCaption = string.IsNullOrWhiteSpace(cancel) ? "Cancel" : cancel,
            Footer = footer,
        };
        return Queue(owner, pluginId, content, "choose").OnPick(onPick).OnNo(onCancel);
    }

    internal static UiDialog Prompt(
        object owner, string pluginId, string footer,
        string title, string body, string initial, Action<string> onSubmit, string placeholder,
        Func<string, string?>? validate, string submit, int maxLength, string cancel, Action? onCancel)
    {
        if (onSubmit == null) throw new ArgumentNullException(nameof(onSubmit));
        var content = new DialogContent
        {
            Shape = UiDialogShape.Prompt,
            Title = title ?? "",
            Body = body ?? "",
            MainCaption = string.IsNullOrWhiteSpace(submit) ? "OK" : submit,
            MainTone = CellTone.Gold,
            CancelCaption = string.IsNullOrWhiteSpace(cancel) ? "Cancel" : cancel,
            // The field stops at maxLength, so the value it starts with does too.
            InputInitial = maxLength > 0 && (initial ?? "").Length > maxLength ? initial!.Substring(0, maxLength) : initial ?? "",
            InputPlaceholder = placeholder ?? "",
            MaxLength = maxLength > 0 ? maxLength : 0,
            Footer = footer,
        };
        return Queue(owner, pluginId, content, "prompt").OnSubmit(onSubmit, validate).OnNo(onCancel);
    }

    /// <summary>
    /// FORMDIALOG1: a form (<see cref="DialogFormOptions"/>): a one-line field with its caption on the left, the check
    /// boxes under their heading, and Save / Cancel. Save and Enter validate (the field's error line, as a Prompt's) and,
    /// with <see cref="DialogFormOptions.RequireOne"/>, wait for one box to be checked (Save is grayed and a hint says so);
    /// <paramref name="onSave"/> gets the text and every box's state. Throws for no box or more than ten.
    /// </summary>
    internal static UiDialog Form(
        object owner, string pluginId, string footer, string title, DialogFormOptions options, Action<DialogFormResult> onSave)
    {
        if (onSave == null) throw new ArgumentNullException(nameof(onSave));
        if (options == null) throw new ArgumentNullException(nameof(options));
        string? countProblem = DialogForm.CountProblem(options.Checks);
        if (countProblem != null) throw new ArgumentException(countProblem, nameof(options));
        string initial = options.Initial ?? "";
        int maxLength = options.MaxLength > 0 ? options.MaxLength : 0;
        var content = new DialogContent
        {
            Shape = UiDialogShape.Prompt,
            Title = title ?? "",
            InputCaption = options.Caption ?? "",
            MainCaption = string.IsNullOrWhiteSpace(options.Save) ? "Save" : options.Save,
            MainTone = CellTone.Gold,
            CancelCaption = string.IsNullOrWhiteSpace(options.Cancel) ? "Cancel" : options.Cancel,
            InputInitial = maxLength > 0 && initial.Length > maxLength ? initial.Substring(0, maxLength) : initial,
            InputPlaceholder = options.Placeholder ?? "",
            MaxLength = maxLength,
            Checks = DialogForm.Clean(options.Checks),
            ChecksHeading = options.Heading ?? "",
            ChecksRequireOne = options.RequireOne,
            ChecksHint = string.IsNullOrWhiteSpace(options.NoneHint) ? DialogForm.DefaultNoneHint : options.NoneHint,
            Footer = footer,
        };
        return Queue(owner, pluginId, content, "form").OnSave(onSave, options.Validate).OnNo(options.OnCancel);
    }

    private static UiDialog Queue(object owner, string pluginId, DialogContent content, string kind)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));
        string id = pluginId ?? "";
        return UiDialog.Enqueue(title => Ui.Panel(title, owner, id), content, kind, id, owner);
    }
}
