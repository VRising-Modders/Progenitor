using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7: a row of tabs on a panel (<see cref="UiWidgets.Tabs"/>): one is selected, each can carry a count badge, and each
/// owns the handles added to it with <see cref="Add"/>, which are shown only while that tab is selected.
/// </summary>
public interface IUiTabs : IUiHandle
{
    /// <summary>The tab captions, in order.</summary>
    IReadOnlyList<string> Captions { get; }

    /// <summary>The selected tab. Setting it switches the content and raises <see cref="OnChanged"/> when it changed.</summary>
    int Selected { get; set; }

    /// <summary>A count badge after tab <paramref name="tab"/>'s caption; 0 or less hides it, more than 99 reads 99+.</summary>
    void SetBadge(int tab, int count);

    /// <summary>A badge in a status tone (<see cref="LabelTone.Danger"/> for errors); empty text hides it.</summary>
    void SetBadge(int tab, string text, LabelTone tone);

    /// <summary>Hover text for tab <paramref name="tab"/>; empty for none.</summary>
    void SetTooltip(int tab, string text);

    /// <summary>
    /// P7b (experimental): tab <paramref name="tab"/>'s caption explains <paramref name="term"/> of the plugin's glossary
    /// (<see cref="UiTerms.Define(string, string, string)"/>): hovering it shows the term's line (in place of the tab's
    /// <see cref="SetTooltip"/> text) and a circle-i marker follows the caption until the term's marker is hidden. Empty
    /// removes it.
    /// </summary>
    void SetTerm(int tab, string term);

    /// <summary>Make <paramref name="content"/> part of tab <paramref name="tab"/>: shown only while that tab is selected.</summary>
    T Add<T>(int tab, T content) where T : IUiHandle;

    /// <summary>After the selected tab changed (a click, a controller press, or <see cref="Selected"/>).</summary>
    IDisposable OnChanged(Action<int> handler);

    /// <summary>Readback: <c>{kind: tabs, key, selected, tabs [{caption, badge, tone}]}</c>.</summary>
    Dictionary<string, object?> Describe();
}

/// <summary>P7: the count-badge rule the tabs, the ordered list and the picker share.</summary>
public static class UiBadge
{
    /// <summary>The most a badge spells out; more reads <c>99+</c>.</summary>
    public const int MaxShown = 99;

    /// <summary>The badge text for <paramref name="count"/>: empty for 0 or less, the number, or <c>99+</c>.</summary>
    public static string Text(int count) => count <= 0 ? "" : count > MaxShown ? MaxShown + "+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// P7: the tabs drawn as one kit cell row of the game's popup buttons (the Install tab's filter chips:
/// <see cref="CellSkin.Chip"/>, the selected one on the gold plate). A tab's count badge sits inside its chip, after the
/// caption (<see cref="UiTabsLayout"/>), and each chip is as wide as its caption, badge and term marker need.
/// </summary>
internal sealed class UiTabsHandle : UiComposedHandle, IUiTabs
{
    private readonly string[] _captions;
    private readonly string[] _badges;
    private readonly LabelTone[] _tones;
    private readonly string[] _tips;
    private readonly UiTermBinding?[] _terms;
    private readonly bool[] _termOn;
    private readonly List<IUiHandle>[] _content;
    private readonly IUiCellRow _row;
    private readonly List<Action<int>> _changed = new();
    private int _selected;

    internal UiTabsHandle(IUiPanel panel, string key, IReadOnlyList<string> captions, int selected)
        : base(panel, key, key)
    {
        if (captions == null || captions.Count == 0) throw new ArgumentException("Tabs need at least one caption.", nameof(captions));
        _captions = new string[captions.Count];
        for (int i = 0; i < _captions.Length; i++) _captions[i] = captions[i] ?? "";
        _badges = new string[_captions.Length];
        _tones = new LabelTone[_captions.Length];
        _tips = new string[_captions.Length];
        _terms = new UiTermBinding?[_captions.Length];
        _content = new List<IUiHandle>[_captions.Length];
        for (int i = 0; i < _captions.Length; i++)
        {
            _badges[i] = "";
            _tips[i] = "";
            _content[i] = new List<IUiHandle>();
        }

        _selected = Clamp(selected);
        _termOn = new bool[_captions.Length];
        _row = Part(panel.CellRow(new CellRowSpec(RowKey("tabs"), UiTabsLayout.Cells(_captions, _selected), plain: true)));
        for (int i = 0; i < _captions.Length; i++)
        {
            int tab = i;
            Lease(_row.OnClick(UiTabsLayout.TabKey(i), () => { if (Live) Selected = tab; }));
            Lease(_row.OnCellHover(UiTabsLayout.TabKey(i), on => HoverTab(tab, on)));
        }

        Render();
    }

    private int Clamp(int tab) => tab < 0 ? 0 : tab >= _captions.Length ? _captions.Length - 1 : tab;

    public IReadOnlyList<string> Captions => _captions;

    public int Selected
    {
        get => _selected;
        set
        {
            int next = Clamp(value);
            if (IsDisposed || next == _selected) return;
            _selected = next;
            Render();
            Raise(_changed, next, "tab change");
        }
    }

    public void SetBadge(int tab, int count) => SetBadge(tab, UiBadge.Text(count), LabelTone.Normal);

    public void SetBadge(int tab, string text, LabelTone tone)
    {
        if (tab < 0 || tab >= _captions.Length || IsDisposed) return;
        _badges[tab] = text ?? "";
        _tones[tab] = tone;
        Render();
    }

    public void SetTooltip(int tab, string text)
    {
        if (tab < 0 || tab >= _captions.Length) return;
        _tips[tab] = text ?? "";
    }

    public T Add<T>(int tab, T content) where T : IUiHandle
    {
        if (content == null) throw new ArgumentNullException(nameof(content));
        if (tab < 0 || tab >= _captions.Length) throw new ArgumentOutOfRangeException(nameof(tab));
        _content[tab].Add(content);
        if (Visible) TrySetVisible(content, tab == _selected);
        return content;
    }

    public IDisposable OnChanged(Action<int> handler) => Subscribe(_changed, handler);

    public Dictionary<string, object?> Describe()
    {
        var tabs = new List<Dictionary<string, object?>>(_captions.Length);
        for (int i = 0; i < _captions.Length; i++)
            tabs.Add(new Dictionary<string, object?>
            {
                ["caption"] = _captions[i],
                ["badge"] = _badges[i],
                ["tone"] = LabelTones.Name(_tones[i]),
                ["content"] = _content[i].Count,
            });
        return new Dictionary<string, object?> { ["kind"] = "tabs", ["key"] = Key, ["selected"] = _selected, ["tabs"] = tabs };
    }

    public void SetTerm(int tab, string term)
    {
        if (tab < 0 || tab >= _captions.Length) return;
        var now = _terms[tab];
        _terms[tab] = null;
        try { now?.Dispose(); }
        catch { /* */ }
        // The chip grows by the marker's room while it explains a term (the marker keeps that room in the caption's right
        // margin), so the caption is never shortened or covered.
        _termOn[tab] = !string.IsNullOrWhiteSpace(term) && !IsDisposed;
        Render();
        if (string.IsNullOrWhiteSpace(term) || IsDisposed) return;
        // The tab's hover slot already runs HoverTab; the binding takes its hovers from there (one handler per cell).
        try { _terms[tab] = UiTerms.MarkCell(_row, UiTabsLayout.TabKey(tab), term, watchHover: false); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: tabs '" + Key + "' term '" + term + "': " + e.Message); }
    }

    /// <summary>The term marked on tab <paramref name="tab"/>, or empty (tests and readback).</summary>
    internal string TermOf(int tab) => tab >= 0 && tab < _terms.Length ? _terms[tab]?.Term ?? "" : "";

    protected override void OnDisposing()
    {
        for (int i = 0; i < _terms.Length; i++)
        {
            try { _terms[i]?.Dispose(); }
            catch { /* */ }
            _terms[i] = null;
        }
    }

    private void HoverTab(int tab, bool on)
    {
        var term = _terms[tab];
        if (term != null && term.Explains)
        {
            term.Hover(on);
            return;
        }

        if (!on || string.IsNullOrWhiteSpace(_tips[tab]))
        {
            HideTooltip();
            return;
        }

        ShowTooltip(RectOf(_row, UiTabsLayout.TabKey(tab)), _captions[tab], _tips[tab], () => RectOf(_row).IsKnown);
    }

    protected override void OnHidden()
    {
        base.OnHidden();
        foreach (var list in _content)
            foreach (var h in list) TrySetVisible(h, false);
    }

    protected override void Render()
    {
        if (IsDisposed || !Visible) return;
        // One layout pass for the whole switch: every content handle that shows or hides asks its panel for a layout, and a
        // tab with a list or a table holds dozens of rows (live 2026-10-14: ~40 single-row layouts per switch and scroll bar
        // verdicts taken on half-switched pages). The batch defers those asks and lays the panel out once at the end.
        UiLayoutBatch.Begin();
        try
        {
            TrySetVisible(_row, true);
            UiTabsLayout.Apply(_row.Cells, _captions, _badges, _termOn);
            for (int i = 0; i < _captions.Length; i++)
            {
                bool on = i == _selected;
                _row.SetCell(UiTabsLayout.TabKey(i), _captions[i], on ? CellTone.ChipOn : CellTone.ChipOff);
                _row.SetCell(UiTabsLayout.BadgeKey(i), _badges[i], BadgeTone(_tones[i]), visible: _badges[i].Length > 0);
            }

            // Hide the old tab's content before showing the new one's, so the page never holds both at once.
            for (int i = 0; i < _captions.Length; i++)
                if (i != _selected)
                    foreach (var h in _content[i]) TrySetVisible(h, false);
            foreach (var h in _content[_selected]) TrySetVisible(h, true);
        }
        finally { UiLayoutBatch.End(); }
    }

    /// <summary>A badge's palette: gold for a plain count, the server red for danger and warnings, green for ok.</summary>
    internal static CellTone BadgeTone(LabelTone tone) => tone switch
    {
        LabelTone.Danger or LabelTone.Warn or LabelTone.Orange => CellTone.Server,
        LabelTone.Ok => CellTone.Ok,
        LabelTone.Muted => CellTone.Muted,
        _ => CellTone.Client,
    };
}

/// <summary>
/// P7: the tab row's cells: one chip per tab (the Install filter chip) with its count badge inside it, after the caption.
/// Each chip is as wide as its caption (measured per character, <see cref="UiChipsText"/>, so a capitalized word such as
/// <c>HUD</c> is never cut), its badge and the term marker's room; the caption's right margin keeps the badge clear.
/// Game-free: <c>UiChipsTests</c> resolves the row at the checked widths.
/// </summary>
internal static class UiTabsLayout
{
    internal static string TabKey(int i) => "tab" + i;

    internal static string BadgeKey(int i) => "badge" + i;

    /// <summary>A tab caption's size: the Install chip's (the kit button's).</summary>
    internal const float CaptionFontSize = MenuPanelLayout.HeaderButtonFontSize;

    /// <summary>One glyph of slack on the caption (the Install chips' rule).</summary>
    internal const float CaptionSlack = CaptionFontSize * MenuPanelLayout.AverageGlyphEm;

    /// <summary>The badge inside a chip: its text size, height, side padding, narrowest width and its inset from the chip's right edge.</summary>
    internal const float BadgeFontSize = CellLayout.BadgeFontSize;
    internal const float BadgeHeight = 20f;
    internal const float BadgePad = 10f;
    internal const float BadgeMinWidth = 22f;
    internal const float BadgeInset = 6f;

    /// <summary>Between the caption (or its term marker) and the badge.</summary>
    internal const float BadgeGap = 4f;

    /// <summary>A badge's width for <paramref name="text"/> (0 for none).</summary>
    internal static float BadgeWidth(string? text) =>
        string.IsNullOrEmpty(text) ? 0f : Math.Max(BadgeMinWidth, (float)Math.Ceiling(UiChipsText.Width(text, BadgeFontSize) + BadgePad));

    /// <summary>The caption's right margin: half the button pad, or past the badge (its inset, width and gap) when there is one.</summary>
    internal static float CaptionRight(string? badge) =>
        string.IsNullOrEmpty(badge) ? CellLayout.ButtonPad * .5f : BadgeInset + BadgeWidth(badge) + BadgeGap;

    /// <summary>A tab chip's width: caption, slack and pads, the badge inside it, and the term marker's room when it explains a term.</summary>
    internal static float ChipWidth(string caption, bool term, float fontSize) => ChipWidth(caption, "", term, fontSize);

    /// <inheritdoc cref="ChipWidth(string, bool, float)"/>
    internal static float ChipWidth(string caption, string? badge, bool term, float fontSize = CaptionFontSize) =>
        (float)Math.Ceiling(UiChipsText.Width(caption ?? "", fontSize) + fontSize * MenuPanelLayout.AverageGlyphEm
            + CellLayout.ButtonPad * .5f + CaptionRight(badge) + (term ? UiTermMarkerArt.Room(fontSize) : 0f));

    internal static List<CellSpec> Cells(IReadOnlyList<string> captions, int selected)
    {
        var cells = new List<CellSpec>(captions.Count * 2);
        for (int i = 0; i < captions.Count; i++)
        {
            var chip = InstallModsPane.Chip(TabKey(i), captions[i], i == selected);
            chip.Hover = true;
            float width = ChipWidth(captions[i], "", false);
            chip.With(width: CellWidth.Px(width), minWidth: width);
            cells.Add(chip);
            // Inside the chip at its right end, over the chip art; it takes no pointer, so a press on it presses the tab.
            cells.Add(new CellSpec(CellKind.Badge, BadgeKey(i))
            {
                Visible = false,
                OverlayOf = TabKey(i),
                OverlayInset = BadgeInset,
                HeightOverride = BadgeHeight,
                MinWidth = BadgeMinWidth,
            }.With(tone: CellTone.Client, width: CellWidth.Px(BadgeMinWidth)));
        }

        return cells;
    }

    /// <summary>
    /// Write every tab's chip width, caption margin and badge width into the row's live cell specs (the handle repaints
    /// them). <paramref name="terms"/> marks the tabs whose caption explains a term (null: none).
    /// </summary>
    internal static void Apply(IReadOnlyList<CellSpec> cells, IReadOnlyList<string> captions, IReadOnlyList<string> badges, IReadOnlyList<bool>? terms)
    {
        for (int i = 0; i < captions.Count; i++)
        {
            string badge = i < badges.Count ? badges[i] ?? "" : "";
            bool term = terms != null && i < terms.Count && terms[i];
            CellSpec? chip = null, mark = null;
            foreach (var c in cells)
            {
                if (string.Equals(c.Key, TabKey(i), StringComparison.Ordinal)) chip = c;
                else if (string.Equals(c.Key, BadgeKey(i), StringComparison.Ordinal)) mark = c;
            }

            if (chip != null)
            {
                float width = ChipWidth(captions[i] ?? "", badge, term, chip.FontSize);
                chip.With(width: CellWidth.Px(width), minWidth: width);
                chip.CaptionInsetRight = badge.Length > 0 ? CaptionRight(badge) : 0f;
            }

            if (mark != null)
            {
                float w = Math.Max(BadgeMinWidth, BadgeWidth(badge));
                mark.Width = CellWidth.Px(w);
                mark.MinWidth = w;
                mark.Text = badge;
                mark.Visible = badge.Length > 0;
            }
        }
    }
}
