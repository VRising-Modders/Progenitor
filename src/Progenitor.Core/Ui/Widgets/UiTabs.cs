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

    /// <summary>HUB1: go to the previous tab of the row's history (<see cref="UiTabsOptions.History"/>); false at its start or with history off.</summary>
    bool Back() => false;

    /// <summary>HUB1: go to the next tab of the row's history after a <see cref="Back"/>; false at its end or with history off.</summary>
    bool Forward() => false;

    /// <summary>Readback: <c>{kind: tabs, key, selected, tabs [{caption, badge, tone}]}</c>.</summary>
    Dictionary<string, object?> Describe();
}

/// <summary>HUB1: how a tab row is drawn (<see cref="UiTabsOptions.Look"/>).</summary>
public enum UiTabsLook
{
    /// <summary>The game's window tabs on a panel made a game window with <see cref="UiLook.GameWindow(IUiPanel)"/>; chips elsewhere (the standard windows keep their chips).</summary>
    Auto = 0,

    /// <summary>The game's own window tabs (the full-screen menu's inner tab, the art the Character menu's tabs use), wherever the row is.</summary>
    Game = 1,

    /// <summary>The game's popup buttons as filter chips, the selected one on the gold plate (the look before HUB1).</summary>
    Chips = 2,
}

/// <summary>HUB1: what differs from a default tab row (<see cref="UiWidgets.Tabs(IUiPanel, string, IReadOnlyList{string}, UiTabsOptions)"/>).</summary>
public sealed class UiTabsOptions
{
    /// <summary>The tab selected first.</summary>
    public int Selected { get; set; }

    /// <summary>How the tabs are drawn; <see cref="UiTabsLook.Auto"/> by default.</summary>
    public UiTabsLook Look { get; set; } = UiTabsLook.Auto;

    /// <summary>
    /// True (the default): while the window stays open, switching to a shorter tab never makes it shorter, as the
    /// standard windows keep their height. It opens next time at the selected tab's height. Applies to a floating window
    /// (a game window or a plain panel); a Mods page is laid out by the workspace.
    /// </summary>
    public bool KeepHeight { get; set; } = true;

    /// <summary>
    /// The least height of the window's rows (without the title band and the version stamp), in kit units; 0 for none.
    /// Give the tallest tab's height to open at it on every tab. Capped to the screen like every window.
    /// </summary>
    public float MinContentHeight { get; set; }

    /// <summary>
    /// True (the default): the row keeps a browser-style history of the tabs the player selected. While its window is
    /// under the pointer or holds the input, the mouse's back and forward buttons and Alt+Left, Alt+Right (Core keybinds,
    /// remappable in the Keybinds tab; in play too) go to the previous and next tab of it, ahead of the Mods workspace's own
    /// history. It lasts one opening of the window. <see cref="IUiTabs.Back"/> and <see cref="IUiTabs.Forward"/> do the same.
    /// </summary>
    public bool History { get; set; } = true;

    /// <summary>
    /// HUB1: what <see cref="UiWidgets.Tabs(IUiPanel, string, IReadOnlyList{string}, int)"/> (the overload without options)
    /// has always drawn, kept for its callers: the popup-button chips and no history; the window keeps its height across
    /// tabs, as the standard windows do.
    /// </summary>
    internal static UiTabsOptions Legacy(int selected) =>
        new() { Selected = selected, Look = UiTabsLook.Chips, History = false, KeepHeight = true };
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

    /// <summary>WINDOWS1: the tab row's handle, the stop a controller focuses (a window routes Down from it).</summary>
    internal UiHandle? NavRow => _row as UiHandle;
    private readonly List<Action<int>> _changed = new();
    private int _selected;

    /// <summary>HUB1: true when the row draws the game's window tabs.</summary>
    internal bool GameTabs { get; }

    /// <summary>HUB1: the window keeps its tallest height while open (<see cref="UiTabsOptions.KeepHeight"/>).</summary>
    internal bool KeepsHeight { get; }

    /// <summary>HUB1: the window's least content height (<see cref="UiTabsOptions.MinContentHeight"/>), 0 for none.</summary>
    internal float MinContentHeight { get; }

    internal UiTabsHandle(IUiPanel panel, string key, IReadOnlyList<string> captions, int selected)
        : this(panel, key, captions, UiTabsOptions.Legacy(selected))
    {
    }

    internal UiTabsHandle(IUiPanel panel, string key, IReadOnlyList<string> captions, UiTabsOptions? options)
        : base(panel, key, key)
    {
        var opts = options ?? new UiTabsOptions();
        int selected = opts.Selected;
        GameTabs = UiTabsLayout.UsesGameTabs(opts.Look, panel is UiPanelHandle gameHost && gameHost.IsGameWindow);
        KeepsHeight = opts.KeepHeight;
        MinContentHeight = UiTabsLayout.MinHeight(opts.MinContentHeight);
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
        // Game tabs stand on the strip's baseline, as the game's window tabs stand on theirs (the row draws it).
        _row = Part(panel.CellRow(new CellRowSpec(RowKey("tabs"), UiTabsLayout.Cells(_captions, _selected, GameTabs), plain: true,
            skin: GameTabs ? CellRowSkin.GameTabStrip : CellRowSkin.Kit)));
        for (int i = 0; i < _captions.Length; i++)
        {
            int tab = i;
            Lease(_row.OnClick(UiTabsLayout.TabKey(i), () => { if (Live) Selected = tab; }));
            Lease(_row.OnCellHover(UiTabsLayout.TabKey(i), on => HoverTab(tab, on)));
        }

        if (opts.History) _history = new UiTabHistory(_selected);
        UiTabHistoryRoute.Register(this);

        // HUB1: a window with tabs keeps its height across them (the standard windows' rule), and may open at a least height.
        if ((KeepsHeight || MinContentHeight > 0f) && panel is UiPanelHandle owner && owner.Enabled && owner.Runtime is IRuntimePanel runtimePanel)
        {
            try { Ui.CurrentRuntime().SetHeightRule(runtimePanel, KeepsHeight, MinContentHeight); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: tabs '" + key + "' height rule: " + e.Message); }
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
            // HUB1: a selection the player made (a click, a controller press, Selected) goes on the history and drops its
            // forward part; a step back or forward through the history moves along it instead. A history from before the
            // window was last closed starts again at the tab it shows.
            if (!_walking) FreshHistory();
            _selected = next;
            if (!_walking) _history?.Push(next);
            // The handlers run inside the switch's layout batch too: a handler that rewrites a label or shows a control
            // (the sample's status line) would otherwise lay the page out a second time right after the switch's own pass.
            UiLayoutBatch.Begin();
            try
            {
                Render();
                Raise(_changed, next, "tab change");
            }
            finally { UiLayoutBatch.End(); }
        }
    }

    // ---- HUB1: browser-style history ---------------------------------------------------------------------------------

    private readonly UiTabHistory? _history;
    private bool _walking;

    /// <summary>The panel's show the history belongs to (<see cref="UiPanelHandle.ShownSequence"/>); 0 before the first.</summary>
    private long _historyShow;

    /// <summary>
    /// HUB1: the history lives as long as one opening of its window: when the window was closed and shown again since the
    /// history last moved, it starts again at the tab shown now (a browser's new tab, not the last session's).
    /// </summary>
    private void FreshHistory()
    {
        if (_history == null || HostPanel is not UiPanelHandle panel) return;
        long show = panel.ShownSequence;
        if (show == _historyShow) return;
        if (_historyShow != 0) _history.Reset(_selected);
        _historyShow = show;
    }

    /// <summary>HUB1: the window closed: the next opening starts a new history (<see cref="IUiWindow.Hide"/>).</summary>
    internal void ResetHistory()
    {
        _history?.Reset(_selected);
        _historyShow = 0;
    }

    /// <summary>HUB1: the row keeps a history (<see cref="UiTabsOptions.History"/>).</summary>
    internal bool HistoryOn => _history != null && !IsDisposed;

    /// <summary>HUB1: the history (tests and readback); null when off.</summary>
    internal UiTabHistory? History => _history;

    /// <summary>HUB1: the panel the row is on (the back and forward route asks it).</summary>
    internal IUiPanel HostPanel => Panel;

    /// <summary>HUB1: the previous tab of the history; OnChanged fires as for a click. False at the start.</summary>
    public bool Back() => Walk(back: true);

    /// <summary>HUB1: the next tab of the history after a <see cref="Back"/>. False at the end.</summary>
    public bool Forward() => Walk(back: false);

    internal bool HistoryBack() => Walk(back: true);

    internal bool HistoryForward() => Walk(back: false);

    private bool Walk(bool back)
    {
        if (_history == null || IsDisposed) return false;
        FreshHistory();
        int tab = back ? _history.Back() : _history.Forward();
        if (tab < 0) return false;
        _walking = true;
        try { Selected = tab; }
        finally { _walking = false; }
        return true;
    }

    /// <summary>HUB1: the controller's tab triggers on a window: the previous (-1) or next (+1) tab, as a click; false at either end.</summary>
    internal bool Step(int delta)
    {
        if (IsDisposed || delta == 0) return false;
        int next = Clamp(_selected + Math.Sign(delta));
        if (next == _selected) return false;
        Selected = next;
        return true;
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
        return new Dictionary<string, object?>
        {
            ["kind"] = "tabs",
            ["key"] = Key,
            ["selected"] = _selected,
            ["tabs"] = tabs,
            ["look"] = GameTabs ? "game" : "chips",
            ["keepHeight"] = KeepsHeight,
            ["minContentHeight"] = MinContentHeight,
        };
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

    /// <summary>HUB1: a game window tab's caption size (the Mods workspace tab strip's, drawn on the same tab art).</summary>
    internal const float GameTabFontSize = 22f;

    /// <summary>HUB1: the narrowest game tab, so a one-word caption still reads as a tab.</summary>
    internal const float GameTabMinWidth = 140f;

    /// <summary>HUB1: the pad either side of a game tab's caption, together.</summary>
    internal const float GameTabPad = 48f;

    /// <summary>
    /// The strip's baseline: the line the game's window tabs stand on (the Mods workspace's tabs on the pane's top trim,
    /// the inventory's on its tab divider). Measured on the workspace strip at 3840x1080: a 2-unit line whose bottom row is
    /// the tabs' bottom row, so the tabs cover it and the selected tab's fill runs into it.
    /// </summary>
    internal const float BaselineHeight = 2f;

    /// <summary>The baseline object's name under the tab row (readback and captures).</summary>
    internal const string BaselineName = "tab-baseline";

    /// <summary>
    /// A game tab's top in a row <paramref name="rowHeight"/> tall, for a tab <paramref name="tabHeight"/> tall: its bottom
    /// edge on the row's bottom edge (the baseline), never centered, so a taller row never lifts it off the line.
    /// </summary>
    internal static float GameTabTop(float rowHeight, float tabHeight) => Math.Max(0f, rowHeight - Math.Min(tabHeight, rowHeight));

    /// <summary>The baseline's top and height in a row <paramref name="rowHeight"/> tall: flush with the row's bottom edge, full width.</summary>
    internal static (float Top, float Height) Baseline(float rowHeight)
    {
        float h = Math.Min(BaselineHeight, Math.Max(0f, rowHeight));
        return (rowHeight - h, h);
    }

    /// <summary>One panel row as the single-line rule sees it (<see cref="TopLinesHidden"/>).</summary>
    internal readonly struct StripRow
    {
        internal StripRow(bool active, bool tabStrip, MenuRowPin pin, bool topLine)
        {
            Active = active;
            TabStrip = tabStrip;
            Pin = pin;
            TopLine = topLine;
        }

        /// <summary>The row is shown (a hidden tab's content is not).</summary>
        internal bool Active { get; }

        /// <summary>A game window tab row (<see cref="CellRowSkin.GameTabStrip"/>), which draws its own baseline.</summary>
        internal bool TabStrip { get; }

        internal MenuRowPin Pin { get; }

        /// <summary>
        /// The row draws a line along its top edge: a settings-grid row's resting top gradient (the game's settings
        /// entry), or a cell row's separator rule.
        /// </summary>
        internal bool TopLine { get; }
    }

    /// <summary>
    /// One line under a game tab row: the tabs stand on the strip's baseline, so the first shown row under the strip does
    /// not draw its own top line (live 2026-10-17: a settings-grid row's top gradient, a row gap under the baseline, read as
    /// a second line with the tabs floating over it). The row under a strip is the next shown row in the panel's order;
    /// under a top-pinned strip (a standard window's tab row) that is the next shown top-pinned or content row (the
    /// content's first row), under a content strip the next shown content row. Rows pinned elsewhere are skipped. True
    /// at index <c>i</c>: row <c>i</c> hides its top line. Recomputed every layout pass, so switching tabs moves it.
    /// </summary>
    internal static bool[] TopLinesHidden(IReadOnlyList<StripRow> rows)
    {
        var hidden = new bool[rows.Count];
        for (int i = 0; i < rows.Count; i++)
        {
            var strip = rows[i];
            if (!strip.Active || !strip.TabStrip || (strip.Pin != MenuRowPin.Content && strip.Pin != MenuRowPin.Top)) continue;
            for (int j = i + 1; j < rows.Count; j++)
            {
                var next = rows[j];
                bool below = next.Pin == MenuRowPin.Content || (strip.Pin == MenuRowPin.Top && next.Pin == MenuRowPin.Top);
                if (!below || !next.Active) continue;
                if (next.TopLine) hidden[j] = true;
                break;
            }
        }

        return hidden;
    }

    /// <summary>HUB1: the most a least content height may ask for.</summary>
    internal const float MaxMinHeight = 2000f;

    /// <summary>HUB1: whether a row of <paramref name="look"/> on a panel that is (<paramref name="gameWindow"/>) a game window draws game tabs.</summary>
    internal static bool UsesGameTabs(UiTabsLook look, bool gameWindow) =>
        look == UiTabsLook.Game || (look == UiTabsLook.Auto && gameWindow);

    /// <summary>HUB1: a least content height clamped to 0..<see cref="MaxMinHeight"/> (NaN and negatives are 0).</summary>
    internal static float MinHeight(float asked) =>
        float.IsNaN(asked) || asked <= 0f ? 0f : Math.Min(MaxMinHeight, asked);

    /// <summary>HUB1: a game tab's width: its caption at <see cref="GameTabFontSize"/> and the pads, the badge, the term marker's room.</summary>
    internal static float GameTabWidth(string caption, string? badge, bool term) =>
        Math.Max(GameTabMinWidth, (float)Math.Ceiling(UiChipsText.Width(caption ?? "", GameTabFontSize) + GameTabPad
            + (string.IsNullOrEmpty(badge) ? 0f : BadgeWidth(badge) + BadgeGap) + (term ? UiTermMarkerArt.Room(GameTabFontSize) : 0f)));

    internal static List<CellSpec> Cells(IReadOnlyList<string> captions, int selected) => Cells(captions, selected, false);

    internal static List<CellSpec> Cells(IReadOnlyList<string> captions, int selected, bool gameTabs)
    {
        var cells = new List<CellSpec>(captions.Count * 2);
        for (int i = 0; i < captions.Count; i++)
        {
            var chip = InstallModsPane.Chip(TabKey(i), captions[i], i == selected);
            chip.Hover = true;
            float width = ChipWidth(captions[i], "", false);
            if (gameTabs)
            {
                // HUB1: the game's window tab (the full-screen menu's inner tab), its caption centered at the strip's size.
                chip.Skin = CellSkin.GameTab;
                chip.FontSizeOverride = GameTabFontSize;
                chip.Align = CellAlign.Center;
                // The tab art spans the whole row, as the game's tabs fill their strip.
                chip.HeightOverride = UnityUiRuntimeMetrics.RowHeight;
                width = GameTabWidth(captions[i], "", false);
            }

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
                float width = chip.Skin == CellSkin.GameTab
                    ? GameTabWidth(captions[i] ?? "", badge, term)
                    : ChipWidth(captions[i] ?? "", badge, term, chip.FontSize);
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
