using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// RADIO1 (decided 2026-10-06): a pick-one choice drawn as a radio list in the cell-row kit (dialogs and popups
/// such as the Players joining panel), the same look and readback as the settings grid's <see cref="IUiRadio"/>. One
/// option per line: the radio circle (<see cref="UiOptionsRules.RadioMark"/>, gold when chosen), the label (bright when
/// chosen, dimmed otherwise) and, when given, a muted explanation under the label, wrapped to the row. A click on either
/// line chooses that option: one write when it is not already chosen, never a toggle. Game-free: the rows are
/// <see cref="CellRowSpec"/>s.
///
/// <para>
/// RADIOCLICK1 (live report 2026-10-06): the lines are plain rows, which the runtime built without a click target, so a
/// real click did nothing while the bridge (which fires the slot directly) chose fine. <see cref="IUiCellRow.OnRowClick"/>
/// now gives such a row its target (<see cref="IRuntimeCellRow.EnsureRowClick"/>). The rows are also compact
/// (<see cref="CellRowSpec.Compact"/>): each used to take the panel's 40 px row floor plus an 8 px gap, so an option
/// and its explanation sat far apart; and the explanation's indent was a spacer cell (spacers ask no width) while the
/// circle's cell took the 72 px text floor, so the explanation started at the row's left edge, left of the label.
/// The metrics below are shared with the settings grid's radio lines (<see cref="UiOptionsRules"/>).
/// </para>
/// </summary>
internal sealed class UiRadioList
{
    internal const string CellMark = "mark";
    internal const string CellLabel = "label";
    internal const string CellIndent = "indent";
    internal const string CellAbout = "about";

    /// <summary>
    /// The circle column's width (its cell's floor too, or it would take the 72 px text floor). RADIOCLICK2: at least
    /// <see cref="MarkGlyphNeed"/>; live at 20 px the circle did not fit and was not drawn.
    /// </summary>
    internal const float MarkWidth = 24f;

    /// <summary>
    /// RADIOCLICK2: the width the circle glyph needs at <see cref="MarkFontSize"/>: a full em (the filled and open circles
    /// are about an em wide in the game's fonts and their fallbacks) plus 2 px of side bearing.
    /// </summary>
    internal const float MarkGlyphNeed = MarkFontSize + 2f;

    /// <summary>Where the label and the explanation start, from the line's left edge: the circle and the cell gap.</summary>
    internal const float LabelInset = MarkWidth + CellLayout.Gap;

    internal const float MarkFontSize = 18f;
    internal const float LabelFontSize = 16f;
    internal const float AboutFontSize = 13f;

    /// <summary>An option line's height (the label's one line).</summary>
    internal const float LineHeight = 22f;

    /// <summary>One wrapped line of an explanation.</summary>
    internal const float AboutLineHeight = 17f;

    /// <summary>The explanation row's padding (above and below its lines, together).</summary>
    internal const float AboutPad = 2f;

    /// <summary>Most lines an explanation wraps to; longer copy belongs in the docs.</summary>
    internal const int MaxAboutLines = 4;

    /// <summary>Between an option line and its explanation.</summary>
    internal const float DescriptionGap = 2f;

    /// <summary>Between one option (its explanation, if any) and the next option's line.</summary>
    internal const float OptionGap = 10f;

    /// <summary>The explanation row's height for <paramref name="lines"/> wrapped lines.</summary>
    internal static float AboutHeight(int lines) => Math.Max(1, lines) * AboutLineHeight + AboutPad;

    /// <summary>The explanation's text width in a list <paramref name="width"/> wide.</summary>
    internal static float AboutWidth(float width) => Math.Max(40f, width - LabelInset - 4f);

    /// <summary>How many lines <paramref name="description"/> wraps to in a list <paramref name="width"/> wide.</summary>
    internal static int AboutLines(string? description, float width) =>
        Math.Max(1, MenuPanelLayout.WrappedLines(description, AboutWidth(width), AboutFontSize, MaxAboutLines));

    private readonly List<UiChoiceOption> _options;

    internal UiRadioList(string key, IReadOnlyList<UiChoiceOption>? options, string? current, bool locked = false, string? lockedWhy = null)
    {
        Key = key ?? "";
        _options = new List<UiChoiceOption>(options ?? Array.Empty<UiChoiceOption>());
        Current = current ?? "";
        Locked = locked;
        LockedWhy = locked ? (lockedWhy ?? "").Trim() : "";
    }

    /// <summary>The line the host shows above a locked list saying why (the operator's lock note); empty when editable.</summary>
    internal string LockedWhy { get; }

    internal string Key { get; }

    internal IReadOnlyList<UiChoiceOption> Options => _options;

    /// <summary>The chosen value (kept verbatim even when it is not an option).</summary>
    internal string Current { get; private set; }

    /// <summary>The operator's lock (or another reason) keeps it read-only: lines draw disabled and clicks do nothing.</summary>
    internal bool Locked { get; }

    /// <summary>The option's index by value, else by label (case-insensitive); -1 when none.</summary>
    internal int IndexOf(string? valueOrLabel)
    {
        string want = (valueOrLabel ?? "").Trim();
        for (int i = 0; i < _options.Count; i++)
            if (string.Equals(_options[i].Value, want, StringComparison.Ordinal)) return i;
        for (int i = 0; i < _options.Count; i++)
            if (string.Equals(_options[i].Value, want, StringComparison.OrdinalIgnoreCase)
                || string.Equals(_options[i].Label, want, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    internal bool IsChosen(int index) =>
        index >= 0 && index < _options.Count && string.Equals(_options[index].Value, Current, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A click on option <paramref name="index"/>: true (and <see cref="Current"/> moves) only when it is a real option,
    /// not already chosen and not locked, so the caller writes exactly once. A click on the chosen line does nothing.
    /// </summary>
    internal bool Choose(int index)
    {
        if (Locked || index < 0 || index >= _options.Count || IsChosen(index)) return false;
        Current = _options[index].Value;
        return true;
    }

    /// <summary>The option line's row key: <c>&lt;key&gt;-&lt;index&gt;</c>; its explanation row adds <c>-about</c>.</summary>
    internal string LineKey(int index) => Key + "-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal string AboutKey(int index) => LineKey(index) + "-" + CellAbout;

    internal static CellTone MarkTone(bool chosen, bool locked) => locked ? CellTone.Disabled : chosen ? CellTone.Gold : CellTone.Muted;

    internal static CellTone LabelTone(bool chosen, bool locked) => locked ? CellTone.Disabled : chosen ? CellTone.Normal : CellTone.Muted;

    internal static CellTone AboutTone(bool chosen, bool locked) => locked || !chosen ? CellTone.Disabled : CellTone.Muted;

    /// <summary>The rows, in order: each option's line, then its explanation row when it has one.</summary>
    internal IReadOnlyList<CellRowSpec> Rows(float width)
    {
        var rows = new List<CellRowSpec>(_options.Count * 2);
        for (int i = 0; i < _options.Count; i++)
        {
            bool chosen = IsChosen(i);
            var o = _options[i];
            // The first line keeps the ordinary gap under whatever is above the list (its heading).
            rows.Add(new CellRowSpec(LineKey(i), new List<CellSpec>
            {
                new CellSpec(CellKind.Text, CellMark).With(text: UiOptionsRules.RadioMark(chosen), width: CellWidth.Px(MarkWidth),
                    minWidth: MarkWidth, align: CellAlign.Center, tone: MarkTone(chosen, Locked), fontSize: MarkFontSize,
                    wrap: CellWrap.Glyph),
                new CellSpec(CellKind.Text, CellLabel).With(text: o.Label, width: CellWidth.Fill(80f), tone: LabelTone(chosen, Locked),
                    fontSize: LabelFontSize),
            }, height: LineHeight, plain: true, hover: !Locked, compact: true, gapAbove: i == 0 ? -1f : OptionGap));
            if (o.Description.Length == 0) continue;
            // An empty text cell, not a spacer: a spacer asks no width, and the explanation would start at the left edge.
            rows.Add(new CellRowSpec(AboutKey(i), new List<CellSpec>
            {
                new CellSpec(CellKind.Text, CellIndent).With(text: "", width: CellWidth.Px(MarkWidth), minWidth: MarkWidth),
                new CellSpec(CellKind.Text, CellAbout).With(text: o.Description, width: CellWidth.Fill(40f), tone: AboutTone(chosen, Locked),
                    wrap: CellWrap.Wrap, fontSize: AboutFontSize),
            // RADIOCLICK2: the estimate is only the fallback; the runtime fits the row to the lines TMP draws.
            }, height: AboutHeight(AboutLines(o.Description, width)), plain: true, hover: !Locked, compact: true, gapAbove: DescriptionGap,
                fitWrap: true));
        }

        return rows;
    }

    /// <summary>The readback: the same shape as the settings grid's radio (<c>kind radio</c>, <c>current</c>, <c>options</c>).</summary>
    internal Dictionary<string, object?> Describe()
    {
        var options = new List<object?>(_options.Count);
        int current = -1;
        for (int i = 0; i < _options.Count; i++)
        {
            bool chosen = IsChosen(i);
            if (chosen) current = i;
            options.Add(new Dictionary<string, object?>
            {
                ["value"] = _options[i].Value,
                ["label"] = _options[i].Label,
                ["description"] = _options[i].Description,
                ["selected"] = chosen,
                ["mark"] = UiOptionsRules.RadioMark(chosen),
                ["row"] = LineKey(i),
                // RADIOCLICK1: whether a pointer click on the drawn line reaches its choose (null: not drawn).
                ["clickable"] = _drawn.TryGetValue(LineKey(i), out var line) ? (object?)(line is UiCellRowHandle h && h.RowClickReachable) : null,
            });
        }

        return new Dictionary<string, object?>
        {
            ["kind"] = "radio",
            ["key"] = Key,
            ["current"] = Current,
            ["currentLabel"] = current >= 0 ? _options[current].Label : "",
            ["locked"] = Locked,
            ["lockedWhy"] = LockedWhy,
            ["options"] = options,
        };
    }

    /// <summary>The rows <see cref="Draw"/> put on the panel, by row key.</summary>
    private readonly Dictionary<string, IUiCellRow> _drawn = new(StringComparer.Ordinal);

    /// <summary>
    /// Draw the list on <paramref name="panel"/> (a Progenitor panel with cell rows). <paramref name="choose"/> runs with
    /// the chosen value, once per real change; it is the same write a caller's bridge op makes. A locked list draws its
    /// lines disabled and binds no click; <paramref name="lockedWhy"/> is the reason the host shows above it.
    /// </summary>
    internal static UiRadioList Draw(
        IUiPanel panel, string key, IReadOnlyList<UiChoiceOption> options, string current, float width, bool locked, Action<string> choose,
        string? lockedWhy = null)
    {
        var list = new UiRadioList(key, options, current, locked, lockedWhy);
        var specs = list.Rows(width);
        for (int r = 0; r < specs.Count; r++)
        {
            var row = panel.CellRow(specs[r]);
            list._drawn[specs[r].Key] = row;
            if (locked || choose == null) continue;
            int index = OptionIndexOf(list, specs[r].Key);
            if (index < 0) continue;
            row.OnRowClick(() =>
            {
                if (list.Choose(index)) choose(list.Current);
            });
        }

        return list;
    }

    private static int OptionIndexOf(UiRadioList list, string rowKey)
    {
        for (int i = 0; i < list._options.Count; i++)
            if (string.Equals(rowKey, list.LineKey(i), StringComparison.Ordinal) || string.Equals(rowKey, list.AboutKey(i), StringComparison.Ordinal))
                return i;
        return -1;
    }
}

/// <summary>
/// RADIOART1 (user, 2026-10-10: "the radio controls look bad"): a radio option's circle drawn as images, not a font glyph
/// (the game's fonts have no circle glyphs, so the list fell back to "o" and a bullet). A ring the size of the game's
/// checkbox frame, and a filled dot inside it for the chosen option; both white, tinted by the Image. Game-free.
/// </summary>
internal static class RadioGlyph
{
    internal const int TextureSize = 64;

    /// <summary>The ring's outer radius and thickness, as shares of the texture.</summary>
    internal const float RingRadiusShare = 0.44f;

    internal const float RingWidthShare = 0.085f;

    /// <summary>The chosen option's dot radius, as a share of the texture.</summary>
    internal const float DotRadiusShare = 0.22f;

    /// <summary>Coverage (0..1) of the ring at pixel (<paramref name="x"/>, <paramref name="y"/>) of a <paramref name="size"/>² texture.</summary>
    internal static float RingAlpha(int x, int y, int size)
    {
        float d = Distance(x, y, size);
        float outer = size * RingRadiusShare;
        float inner = outer - size * RingWidthShare;
        return Clamp(Math.Min(outer - d + 0.5f, d - inner + 0.5f));
    }

    /// <summary>Coverage (0..1) of the dot at pixel (<paramref name="x"/>, <paramref name="y"/>).</summary>
    internal static float DotAlpha(int x, int y, int size) => Clamp(size * DotRadiusShare - Distance(x, y, size) + 0.5f);

    private static float Distance(int x, int y, int size)
    {
        float c = size * 0.5f;
        float dx = x + 0.5f - c, dy = y + 0.5f - c;
        return (float)Math.Sqrt(dx * dx + dy * dy);
    }

    private static float Clamp(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    /// <summary>The ring's gray (the checkbox frame's light steel), brighter on the chosen option.</summary>
    internal static float RingGrey(bool chosen) => chosen ? 0.86f : 0.58f;

    /// <summary>The dot: the checkmark's near-white.</summary>
    internal const float DotGrey = 0.92f;
}
