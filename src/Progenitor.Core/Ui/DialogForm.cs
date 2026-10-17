using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// FORMDIALOG1: one check box of a form dialog (<see cref="DialogFormOptions.Checks"/>): its label, whether it starts
/// checked, and an optional key the result is read by (<see cref="DialogFormResult.IsOn(string)"/>). The dialog draws it as
/// the game's Options checkbox with the label after it; a click anywhere on the line, or Space, Enter or the
/// controller's A on it, flips it.
/// </summary>
public sealed class DialogCheck
{
    private readonly string _label = "";
    private readonly string _key = "";

    /// <summary>The words after the box (trimmed).</summary>
    public string Label { get => _label; init => _label = (value ?? "").Trim(); }

    /// <summary>Checked when the dialog opens.</summary>
    public bool On { get; init; }

    /// <summary>A name for the box in <see cref="DialogFormResult.IsOn(string)"/> (trimmed); empty: its label is used.</summary>
    public string Key { get => _key; init => _key = (value ?? "").Trim(); }

    /// <summary>The same box, checked or not.</summary>
    public DialogCheck With(bool on) => new() { Label = Label, On = on, Key = Key };

    public override string ToString() => (On ? "[x] " : "[ ] ") + Label;
}

/// <summary>
/// FORMDIALOG1: what <see cref="PluginDialogs.Form"/> shows besides its title: the field, the check boxes, the buttons
/// and the rules Save follows. Every member has a default; give at least one box in <see cref="Checks"/>.
/// </summary>
public sealed class DialogFormOptions
{
    /// <summary>The words left of the text field (<c>Name</c>); empty: the field alone, centered.</summary>
    public string Caption { get; set; } = "";

    /// <summary>The field's text when the dialog opens (cut to <see cref="MaxLength"/>).</summary>
    public string Initial { get; set; } = "";

    /// <summary>The field's hint while it is empty.</summary>
    public string Placeholder { get; set; } = "";

    /// <summary>The field's length cap; 0: none.</summary>
    public int MaxLength { get; set; }

    /// <summary>The small heading over the boxes (<c>Show messages from</c>); empty: none.</summary>
    public string Heading { get; set; } = "";

    /// <summary>One to ten boxes, in order.</summary>
    public IReadOnlyList<DialogCheck> Checks { get; set; } = Array.Empty<DialogCheck>();

    /// <summary>Save waits until at least one box is checked: grayed, with <see cref="NoneHint"/> under the boxes.</summary>
    public bool RequireOne { get; set; }

    /// <summary>The line under the boxes while none is checked and one must be; empty: <c>Turn on at least one.</c></summary>
    public string NoneHint { get; set; } = "";

    /// <summary>Run on Save and Enter: an error to show under the field (the dialog stays open), or null to accept.</summary>
    public Func<DialogFormResult, string?>? Validate { get; set; }

    /// <summary>The main button's caption.</summary>
    public string Save { get; set; } = "Save";

    /// <summary>The dismissal's caption.</summary>
    public string Cancel { get; set; } = "Cancel";

    /// <summary>Run on Cancel and Escape.</summary>
    public Action? OnCancel { get; set; }

    /// <summary>Whether the footer names this mod; null: <see cref="PluginDialogs.ShowModVersion"/>.</summary>
    public bool? ShowModVersion { get; set; }
}

/// <summary>FORMDIALOG1: a form's answer: the text and each box's state, read by index or by key.</summary>
public sealed class DialogFormResult
{
    private readonly DialogCheck[] _checks;

    internal DialogFormResult(string text, IReadOnlyList<DialogCheck>? checks)
    {
        Text = text ?? "";
        _checks = new DialogCheck[checks?.Count ?? 0];
        for (int i = 0; i < _checks.Length; i++) _checks[i] = checks![i];
    }

    /// <summary>The field's text, as typed (not trimmed).</summary>
    public string Text { get; }

    /// <summary>How many boxes the form has.</summary>
    public int Count => _checks.Length;

    /// <summary>Box <paramref name="index"/> is checked; false out of range.</summary>
    public bool IsOn(int index) => index >= 0 && index < _checks.Length && _checks[index].On;

    /// <summary>The box whose <see cref="DialogCheck.Key"/> (or, without one, label) is <paramref name="key"/>, ignoring case, is checked; false when none is.</summary>
    public bool IsOn(string key)
    {
        string want = (key ?? "").Trim();
        foreach (var c in _checks)
            if (string.Equals(c.Key.Length > 0 ? c.Key : c.Label, want, StringComparison.OrdinalIgnoreCase)) return c.On;
        return false;
    }

    /// <summary>Every box's state, in order.</summary>
    public IReadOnlyList<bool> States => DialogForm.States(_checks);
}

/// <summary>
/// FORMDIALOG1: the form dialog's rules and rows, game-free. A form is a Prompt (a name field and its error line)
/// with a group of check boxes under a small heading, and Save / Cancel. Every part is a plain cell row at the
/// dialog's content width, left-aligned on one edge (<see cref="DialogLayout.FactInset"/> from the frame's content
/// edge): the field's caption, the heading, the boxes and their labels all start there, so the form reads as one
/// neat column; the buttons stay the dialog's centered popup pair.
/// </summary>
internal static class DialogForm
{
    /// <summary>The hint under the boxes when the dialog needs one checked and none is.</summary>
    internal const string DefaultNoneHint = "Turn on at least one.";

    /// <summary>A check line's height: the game's checkbox (24) and air above and below.</summary>
    internal const float CheckRowHeight = 30f;

    /// <summary>Between two check lines.</summary>
    internal const float CheckRowGap = 2f;

    /// <summary>The heading's row (one line of <see cref="DialogLayout.BodyFontSize"/> text).</summary>
    internal const float HeadingRowHeight = 26f;

    /// <summary>Space above the heading, under the field and its error line.</summary>
    internal const float HeadingGap = 6f;

    /// <summary>The hint's row under the boxes.</summary>
    internal const float HintRowHeight = 24f;

    /// <summary>The labels' size: the facts block's (17), so a form and a facts dialog read alike.</summary>
    internal const float LabelFontSize = DialogLayout.FactFontSize;

    /// <summary>Row and cell keys: <c>dialog-check-&lt;i&gt;</c> with cells <c>check</c> and <c>label</c>.</summary>
    internal static string CheckRow(int index) => "dialog-check-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal const string CellInset = "inset";
    internal const string CellCheck = "check";
    internal const string CellLabel = "label";
    internal const string CellCaption = "caption";
    internal const string HeadingRow = "dialog-checks-heading";
    internal const string CellHeading = "heading";
    internal const string HintRow = "dialog-checks-hint";
    internal const string CellHint = "hint";

    /// <summary>Whether <paramref name="checks"/> may be saved: anything goes unless one must be on.</summary>
    internal static bool Satisfied(IReadOnlyList<DialogCheck>? checks, bool requireOne)
    {
        if (!requireOne || checks == null || checks.Count == 0) return true;
        foreach (var c in checks)
            if (c.On) return true;
        return false;
    }

    /// <summary>The checks' states, in order (what <c>onSave</c> gets).</summary>
    internal static bool[] States(IReadOnlyList<DialogCheck>? checks)
    {
        if (checks == null) return Array.Empty<bool>();
        var states = new bool[checks.Count];
        for (int i = 0; i < checks.Count; i++) states[i] = checks[i].On;
        return states;
    }

    /// <summary><paramref name="checks"/> with box <paramref name="index"/> flipped (the same list when out of range).</summary>
    internal static IReadOnlyList<DialogCheck> Flip(IReadOnlyList<DialogCheck> checks, int index)
    {
        if (checks == null || index < 0 || index >= checks.Count) return checks ?? Array.Empty<DialogCheck>();
        var copy = new DialogCheck[checks.Count];
        for (int i = 0; i < checks.Count; i++) copy[i] = i == index ? checks[i].With(!checks[i].On) : checks[i];
        return copy;
    }

    /// <summary>
    /// A copy the caller cannot change under the dialog; a null entry is an unchecked box with no label (its index still
    /// counts) and an unlabeled box is drawn blank.
    /// </summary>
    internal static IReadOnlyList<DialogCheck> Clean(IReadOnlyList<DialogCheck>? checks)
    {
        if (checks == null || checks.Count == 0) return Array.Empty<DialogCheck>();
        var copy = new DialogCheck[checks.Count];
        for (int i = 0; i < checks.Count; i++) copy[i] = checks[i] ?? new DialogCheck();
        return copy;
    }

    /// <summary>Why a form cannot be opened with <paramref name="checks"/> (none, or more than ten), or null.</summary>
    internal static string? CountProblem(IReadOnlyList<DialogCheck>? checks) =>
        checks == null || checks.Count == 0 ? "At least one check box is required."
        : checks.Count > DialogLayout.MaxListOptions ? "At most " + DialogLayout.MaxListOptions + " check boxes; a longer list belongs on a page."
        : null;

    /// <summary>The field caption's column: its estimated width with the facts' slack and pad, at least the facts' floor.</summary>
    internal static float CaptionWidth(string? caption, float rowWidth = DialogLayout.RowWidth)
    {
        if (string.IsNullOrWhiteSpace(caption)) return 0f;
        float w = CellLayout.EstimateTextWidth(caption, LabelFontSize) * DialogLayout.CaptionSlack + DialogLayout.FactLabelPad;
        w = Math.Max(w, DialogLayout.FactLabelMinWidth);
        float cap = DialogFacts.BlockWidth(rowWidth) * DialogLayout.FactLabelMaxFraction;
        return (float)Math.Ceiling(Math.Min(w, cap));
    }

    /// <summary>An empty text cell at the inset (a spacer asks no width: the option rows' pad, LISTROWS1).</summary>
    private static CellSpec Inset(string key) =>
        new CellSpec(CellKind.Text, key).With(text: "", width: CellWidth.Px(DialogLayout.FactInset), minWidth: DialogLayout.FactInset);

    /// <summary>
    /// The field with its caption on the left (<c>Name [ field ]</c>): the inset, the muted caption in its column, the
    /// field filling the rest, the inset again. The field cell is the Prompt's own (<see cref="UiDialogs.CellField"/>).
    /// </summary>
    internal static CellRowSpec CaptionedFieldRowSpec(string rowKey, string caption, CellSpec field, float rowWidth = DialogLayout.RowWidth)
    {
        float captionWidth = CaptionWidth(caption, rowWidth);
        field.Width = CellWidth.Fill(CellLayout.MinInputWidth);
        field.MinWidth = CellLayout.MinInputWidth;
        var cells = new List<CellSpec>(4)
        {
            Inset(CellInset),
            new CellSpec(CellKind.Text, CellCaption).With(text: caption ?? "", align: CellAlign.Left, tone: CellTone.Muted,
                fontSize: LabelFontSize, width: CellWidth.Px(captionWidth), minWidth: captionWidth),
            field,
            Inset(CellInset + "-right"),
        };
        return new CellRowSpec(rowKey, cells, height: UnityUiRuntimeMetrics.RowHeight, plain: true);
    }

    /// <summary>
    /// The field's error line under a captioned field: it starts where the field starts (the inset and the caption's
    /// column before it), left-aligned, in the error cell the Prompt shows and hides (<paramref name="error"/>).
    /// </summary>
    internal static CellRowSpec FieldErrorRowSpec(string rowKey, string caption, CellSpec error, float rowWidth = DialogLayout.RowWidth)
    {
        float captionWidth = CaptionWidth(caption, rowWidth);
        error.Align = CellAlign.Left;
        error.Width = CellWidth.Fill(CellLayout.MinTextWidth);
        error.MinWidth = CellLayout.MinTextWidth;
        var cells = new List<CellSpec>(4)
        {
            Inset(CellInset),
            new CellSpec(CellKind.Text, CellCaption).With(text: "", width: CellWidth.Px(captionWidth), minWidth: captionWidth),
            error,
            Inset(CellInset + "-right"),
        };
        return new CellRowSpec(rowKey, cells, height: HintRowHeight, plain: true, compact: true);
    }

    /// <summary>The checks' heading (<c>Show messages from</c>): muted, left on the form's edge, a little air above.</summary>
    internal static CellRowSpec HeadingRowSpec(string text)
    {
        var cells = new List<CellSpec>(3)
        {
            Inset(CellInset),
            new CellSpec(CellKind.Text, CellHeading).With(text: text ?? "", align: CellAlign.Left, tone: CellTone.Muted,
                fontSize: LabelFontSize, width: CellWidth.Fill(CellLayout.MinTextWidth), minWidth: CellLayout.MinTextWidth),
            Inset(CellInset + "-right"),
        };
        return new CellRowSpec(HeadingRow, cells, height: HeadingRowHeight, plain: true, compact: true, gapAbove: HeadingGap);
    }

    /// <summary>
    /// One check line: the inset, the game's checkbox, the label (bright, left) filling the rest. A plain row whose click
    /// target the dialog adds (<see cref="IUiCellRow.OnRowClick"/>), so a click anywhere on the line flips the box.
    /// </summary>
    internal static CellRowSpec CheckRowSpec(int index, DialogCheck check, bool enabled = true)
    {
        var cells = new List<CellSpec>(4)
        {
            Inset(CellInset),
            new CellSpec(CellKind.Check, CellCheck).With(text: CellLayout.CheckText(check.On),
                tone: enabled ? CellTone.Normal : CellTone.Disabled,
                width: CellWidth.Px(CellLayout.CheckWidth), minWidth: CellLayout.CheckWidth),
            new CellSpec(CellKind.Text, CellLabel).With(text: check.Label, align: CellAlign.Left,
                tone: enabled ? CellTone.Normal : CellTone.Disabled, fontSize: LabelFontSize,
                width: CellWidth.Fill(CellLayout.MinTextWidth), minWidth: CellLayout.MinTextWidth),
            Inset(CellInset + "-right"),
        };
        return new CellRowSpec(CheckRow(index), cells, height: CheckRowHeight, plain: true, compact: true, gapAbove: CheckRowGap);
    }

    /// <summary>The hint under the boxes (hidden until none is checked and one must be): the error line's tone and size, left.</summary>
    internal static CellRowSpec HintRowSpec(string text)
    {
        var cells = new List<CellSpec>(3)
        {
            Inset(CellInset),
            new CellSpec(CellKind.Text, CellHint).With(text: text ?? "", align: CellAlign.Left, tone: CellTone.Orange,
                fontSize: DialogLayout.ErrorFontSize, width: CellWidth.Fill(CellLayout.MinTextWidth), minWidth: CellLayout.MinTextWidth),
            Inset(CellInset + "-right"),
        };
        return new CellRowSpec(HintRow, cells, height: HintRowHeight, plain: true, compact: true, gapAbove: CheckRowGap);
    }
}
