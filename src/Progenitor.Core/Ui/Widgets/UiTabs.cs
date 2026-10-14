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
/// <see cref="CellSkin.Chip"/>, the selected one on the gold plate), each followed by its badge cell.
/// </summary>
internal sealed class UiTabsHandle : UiComposedHandle, IUiTabs
{
    private readonly string[] _captions;
    private readonly string[] _badges;
    private readonly LabelTone[] _tones;
    private readonly string[] _tips;
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
        _content = new List<IUiHandle>[_captions.Length];
        for (int i = 0; i < _captions.Length; i++)
        {
            _badges[i] = "";
            _tips[i] = "";
            _content[i] = new List<IUiHandle>();
        }

        _selected = Clamp(selected);
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

    private void HoverTab(int tab, bool on)
    {
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
        TrySetVisible(_row, true);
        for (int i = 0; i < _captions.Length; i++)
        {
            bool on = i == _selected;
            _row.SetCell(UiTabsLayout.TabKey(i), _captions[i], on ? CellTone.ChipOn : CellTone.ChipOff);
            _row.SetCell(UiTabsLayout.BadgeKey(i), _badges[i], BadgeTone(_tones[i]), visible: _badges[i].Length > 0);
            foreach (var h in _content[i]) TrySetVisible(h, on);
        }
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

/// <summary>P7: the tab row's cells: one chip per tab (the Install filter chip) and a count badge after it.</summary>
internal static class UiTabsLayout
{
    internal static string TabKey(int i) => "tab" + i;

    internal static string BadgeKey(int i) => "badge" + i;

    internal static List<CellSpec> Cells(IReadOnlyList<string> captions, int selected)
    {
        var cells = new List<CellSpec>(captions.Count * 2);
        for (int i = 0; i < captions.Count; i++)
        {
            var chip = InstallModsPane.Chip(TabKey(i), captions[i], i == selected);
            chip.Hover = true;
            cells.Add(chip);
            cells.Add(new CellSpec(CellKind.Badge, BadgeKey(i)) { Visible = false }
                .With(tone: CellTone.Client, minWidth: UiWidgetLayout.CountBadgeMinWidth));
        }

        return cells;
    }
}
