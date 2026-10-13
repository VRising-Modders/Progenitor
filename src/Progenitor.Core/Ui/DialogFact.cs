using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>How a dialog fact's value is drawn: the ordinary bright text, or a state color.</summary>
public enum DialogFactTone
{
    /// <summary>The ordinary bright value text.</summary>
    Normal = 0,

    /// <summary>A good state (installed, ready).</summary>
    Ok = 1,

    /// <summary>Needs attention (will be changed, waits on a restart).</summary>
    Warn = 2,

    /// <summary>Blocks the action.</summary>
    Danger = 3,
}

/// <summary>
/// DIALOGS2: one label/value row of a dialog's facts block (<c>Server: Progenitor Forms Test</c>).
/// The label is drawn muted in a column as wide as the widest label of the block; the value is
/// drawn bright, left-aligned, wrapping to at most <see cref="DialogLayout.MaxFactLines"/> lines.
/// An empty label continues the previous fact (a second <c>Requires</c> line).
/// </summary>
public readonly struct DialogFact
{
    public DialogFact(string label, string value, DialogFactTone tone = DialogFactTone.Normal)
    {
        Label = (label ?? "").Trim();
        Value = (value ?? "").Trim();
        Tone = tone;
    }

    /// <summary>The muted caption on the left; empty continues the fact above.</summary>
    public string Label { get; }

    /// <summary>The bright value on the right.</summary>
    public string Value { get; }

    public DialogFactTone Tone { get; }

    public override string ToString() => Label.Length == 0 ? Value : Label + ": " + Value;
}

/// <summary>DIALOGS2: the facts block's geometry and rows. Pure arithmetic, like <see cref="DialogLayout"/>.</summary>
internal static class DialogFacts
{
    /// <summary>Row keys a bridge client reads: <c>dialog-fact-0</c>, … ; cells <c>label</c> and <c>value</c>.</summary>
    internal static string RowKey(int index) => "dialog-fact-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal const string CellInset = "inset";
    internal const string CellLabel = "label";
    internal const string CellValue = "value";

    /// <summary>Facts with an empty value are left out; labels and values are trimmed.</summary>
    internal static List<DialogFact> Clean(IEnumerable<DialogFact>? facts)
    {
        var list = new List<DialogFact>();
        if (facts == null) return list;
        foreach (var f in facts)
            if (f.Value.Length > 0) list.Add(new DialogFact(f.Label, f.Value, f.Tone));
        return list;
    }

    /// <summary>
    /// The label column: the widest label's estimated width (with the caption slack the buttons use),
    /// never wider than <see cref="DialogLayout.FactLabelMaxFraction"/> of the block. Zero when no
    /// fact has a label.
    /// </summary>
    internal static float LabelWidth(IReadOnlyList<DialogFact> facts, float rowWidth = DialogLayout.RowWidth)
    {
        float widest = 0f;
        if (facts != null)
        {
            for (int i = 0; i < facts.Count; i++)
            {
                if (facts[i].Label.Length == 0) continue;
                widest = Math.Max(widest, CellLayout.EstimateTextWidth(facts[i].Label, DialogLayout.FactFontSize) * DialogLayout.CaptionSlack);
            }
        }

        if (widest <= 0f) return 0f;
        // FIX-NONMODS (live: the install confirmation's "Mod" label drew "M…"): the estimate is the
        // glyphs alone, but a text cell draws inside its own padding, so a short label's column was
        // narrower than the label. The column takes the cell's padding on top and never goes under
        // a floor that holds any short word.
        widest = Math.Max(widest + DialogLayout.FactLabelPad, DialogLayout.FactLabelMinWidth);
        float cap = BlockWidth(rowWidth) * DialogLayout.FactLabelMaxFraction;
        return (float)Math.Ceiling(Math.Min(widest, cap));
    }

    /// <summary>The block's width: the row less the inset on both sides.</summary>
    internal static float BlockWidth(float rowWidth = DialogLayout.RowWidth) =>
        Math.Max(1f, MenuPanelLayout.Positive(rowWidth, DialogLayout.RowWidth) - DialogLayout.FactInset * 2f);

    /// <summary>The value column: the block less the label column and the gap between them.</summary>
    internal static float ValueWidth(float labelWidth, float rowWidth = DialogLayout.RowWidth) =>
        Math.Max(1f, BlockWidth(rowWidth) - Math.Max(0f, labelWidth) - (labelWidth > 0f ? CellLayout.Gap : 0f));

    /// <summary>Estimated lines of one value, at most <see cref="DialogLayout.MaxFactLines"/>.</summary>
    internal static int ValueLines(string? value, float valueWidth) =>
        MenuPanelLayout.WrappedLines(value, valueWidth, DialogLayout.FactFontSize, DialogLayout.MaxFactLines);

    /// <summary>One fact row's height: its value's lines, plus <see cref="DialogLayout.FactRowPad"/>.</summary>
    internal static float RowHeight(int lines) =>
        Math.Max(1, lines) * DialogLayout.FactLineHeight + DialogLayout.FactRowPad;

    internal static CellTone ToCellTone(DialogFactTone tone) => tone switch
    {
        DialogFactTone.Ok => CellTone.Ok,
        DialogFactTone.Warn => CellTone.Warn,
        DialogFactTone.Danger => CellTone.Danger,
        _ => CellTone.Normal,
    };

    /// <summary>
    /// One fact as a plain cell row: a fixed inset, the muted label at <paramref name="labelWidth"/>
    /// (left-aligned), and the value filling the rest (left-aligned, wrapping, top-aligned).
    /// </summary>
    internal static CellRowSpec RowSpec(int index, DialogFact fact, float labelWidth, float rowWidth = DialogLayout.RowWidth)
    {
        float valueWidth = ValueWidth(labelWidth, rowWidth);
        int lines = ValueLines(fact.Value, valueWidth);
        var cells = new List<CellSpec>(3)
        {
            new CellSpec(CellKind.Text, CellInset).With(text: "", width: CellWidth.Px(DialogLayout.FactInset), minWidth: DialogLayout.FactInset),
        };
        if (labelWidth > 0f)
        {
            cells.Add(new CellSpec(CellKind.Text, CellLabel).With(
                text: fact.Label, width: CellWidth.Px(labelWidth), minWidth: labelWidth, align: CellAlign.Left,
                tone: CellTone.Muted, fontSize: DialogLayout.FactFontSize, vAlign: CellVAlign.Top));
        }

        cells.Add(new CellSpec(CellKind.Text, CellValue).With(
            text: fact.Value, width: CellWidth.Fill(1f), align: CellAlign.Left, tone: ToCellTone(fact.Tone),
            wrap: CellWrap.Wrap, fontSize: DialogLayout.FactFontSize, vAlign: CellVAlign.Top));
        // The right inset: a Fill cell takes every remaining unit, so the block's right edge is
        // pinned by the row width less this.
        cells.Add(new CellSpec(CellKind.Text, "inset-right").With(text: "", width: CellWidth.Px(DialogLayout.FactInset), minWidth: DialogLayout.FactInset));
        // FACTWRAP1 (live 2026-10-07: the wipe dialog drew "every 304 days on Any → every 3 days o…"): the value always
        // wraps and the row is as tall as the text the runtime measures at the placed width (as the radio
        // explanations), one line at least and MaxFactLines at most; the estimate is only the fallback.
        return new CellRowSpec(RowKey(index), cells, height: RowHeight(lines), plain: true, fitWrap: true)
        {
            FitWrapFloor = RowHeight(1),
            FitWrapCeiling = RowHeight(DialogLayout.MaxFactLines),
        };
    }

    /// <summary>Readback <c>facts: [{label, value, tone}]</c>.</summary>
    internal static List<Dictionary<string, object?>> Describe(IReadOnlyList<DialogFact>? facts)
    {
        var list = new List<Dictionary<string, object?>>();
        if (facts == null) return list;
        for (int i = 0; i < facts.Count; i++)
        {
            list.Add(new Dictionary<string, object?>
            {
                ["label"] = facts[i].Label,
                ["value"] = facts[i].Value,
                ["tone"] = facts[i].Tone.ToString().ToLowerInvariant(),
            });
        }

        return list;
    }

    /// <summary>The facts as plain lines (<c>Label: value</c>), for a log or a text readback.</summary>
    internal static string ToText(IReadOnlyList<DialogFact>? facts)
    {
        if (facts == null || facts.Count == 0) return "";
        var lines = new List<string>(facts.Count);
        for (int i = 0; i < facts.Count; i++) lines.Add(facts[i].ToString());
        return string.Join("\n", lines);
    }
}
