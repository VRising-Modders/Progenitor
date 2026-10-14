using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7: the picker drawn as kit cell rows (<see cref="UiWidgets.Picker"/>): the search field (the game's value box, with
/// the kit's clear button), a status line with the autofill suggestion as a link, and <see cref="UiPickerModel.Rows"/>
/// result slots made once and rewritten in place. A slot is a section heading (gold caption, no icon) or an entry.
/// </summary>
internal sealed class UiPickerHandle : UiComposedHandle, IUiPicker
{
    private const string CellSearch = "search";
    private const string CellStatus = "status";
    private const string CellUse = "use";

    private readonly UiPickerModel _model;
    private readonly UiPickerOptions _options;
    private readonly bool _icons;
    private readonly IUiCellRow _search;
    private readonly IUiCellRow _status;
    private readonly List<IUiCellRow> _slots = new();
    private readonly List<UiPickerLine> _lines = new();
    private readonly List<Action<IReadOnlyList<UiPickerEntry>>> _picked = new();
    private string _check = UiWidgetLayout.CheckGlyph;

    internal UiPickerHandle(IUiPanel panel, string key, IEnumerable<UiPickerEntry>? entries, UiPickerOptions? options)
        : base(panel, key, key)
    {
        _options = options ?? new UiPickerOptions();
        _model = new UiPickerModel(entries, _options.Multi, _options.Rows, _options.Selected);
        _icons = AnyIcon(_model.Entries);
        try { _check = WidgetRuntime.Current?.DrawableGlyph(UiWidgetLayout.CheckGlyph, UiWidgetLayout.CheckFallback) ?? _check; }
        catch { /* the preferred glyph stays */ }

        _search = Part(panel.CellRow(new CellRowSpec(RowKey(CellSearch), UiPickerLayout.SearchCells(_options.Placeholder), plain: true)));
        Lease(_search.OnChanged(CellSearch, text =>
        {
            if (IsDisposed) return;
            _model.Query = text ?? "";
            Render();
        }));

        _status = Part(panel.CellRow(new CellRowSpec(RowKey(CellStatus), UiPickerLayout.StatusCells(), plain: true, compact: true)));
        Lease(_status.OnClick(CellUse, () => { if (Live) AcceptSuggestion(); }));

        for (int i = 0; i < _model.Rows; i++)
        {
            int slot = i;
            var row = Part(panel.CellRow(new CellRowSpec(
                RowKey("line" + i), UiPickerLayout.LineCells(_options.Multi, _icons), hover: true,
                skin: CellRowSkin.GameEntry, separator: true, quietHover: true)));
            _slots.Add(row);
            Lease(row.OnRowClick(() => ClickSlot(slot)));
            Lease(row.OnHover(on => HoverSlot(slot, on)));
            if (_options.Multi) Lease(row.OnClick(UiWidgetLayout.CellCheck, () => ClickSlot(slot)));
        }

        Render();
    }

    private static bool AnyIcon(IReadOnlyList<UiPickerEntry> entries)
    {
        foreach (var e in entries) if (e.Icon != null || e.ItemPrefab != 0) return true;
        return false;
    }

    // ---- IUiPicker -------------------------------------------------------------------------

    public string Query
    {
        get => _model.Query;
        set
        {
            if (IsDisposed) return;
            _model.Query = value ?? "";
            _search.SetCell(CellSearch, _model.Query);
            Render();
        }
    }

    public IReadOnlyList<UiPickerEntry> Matches => _model.Matches;

    public string Suggestion => _options.Autofill ? _model.Suggestion : "";

    public IReadOnlyList<string> Selected => _model.Selected;

    public bool AcceptSuggestion()
    {
        string s = Suggestion;
        if (s.Length == 0 || IsDisposed) return false;
        Query = s;
        if (!_options.Multi)
        {
            foreach (var e in _model.Matches)
            {
                if (e.Disabled || !string.Equals(e.Label, s, StringComparison.Ordinal)) continue;
                if (_model.Select(e.Id, true))
                {
                    Render();
                    RaisePicked();
                }

                break;
            }
        }

        return true;
    }

    public void SetEntries(IEnumerable<UiPickerEntry> entries)
    {
        if (IsDisposed) return;
        _model.SetEntries(entries);
        Render();
    }

    public bool Select(string id, bool selected = true)
    {
        if (IsDisposed || !_model.Select(id, selected)) return false;
        Render();
        RaisePicked();
        return true;
    }

    public void ClearSelection()
    {
        if (IsDisposed || !_model.ClearSelection()) return;
        Render();
        RaisePicked();
    }

    public IDisposable OnPicked(Action<IReadOnlyList<UiPickerEntry>> handler) => Subscribe(_picked, handler);

    public Dictionary<string, object?> Describe()
    {
        var lines = new List<Dictionary<string, object?>>(_lines.Count);
        foreach (var l in _lines)
            lines.Add(new Dictionary<string, object?>
            {
                ["kind"] = l.Kind == UiPickerLineKind.Section ? "section" : "entry",
                ["id"] = l.Entry?.Id,
                ["label"] = l.Text,
                ["disabled"] = l.Entry?.Disabled == true,
                ["reason"] = l.Entry?.DisabledReason ?? "",
                ["selected"] = l.Entry != null && _model.IsSelected(l.Entry.Id),
            });
        return new Dictionary<string, object?>
        {
            ["kind"] = "picker",
            ["key"] = Key,
            ["multi"] = _options.Multi,
            ["query"] = _model.Query,
            ["suggestion"] = Suggestion,
            ["matches"] = _model.Matches.Count,
            ["selected"] = new List<string>(_model.Selected),
            ["lines"] = lines,
        };
    }

    private void RaisePicked() => Raise(_picked, (IReadOnlyList<UiPickerEntry>)_model.SelectedEntries(), "picked");

    // ---- Input -------------------------------------------------------------------------------

    internal void ClickSlot(int slot)
    {
        if (!Live || slot < 0 || slot >= _lines.Count) return;
        var entry = _lines[slot].Entry;
        if (entry == null || !_model.Click(entry.Id)) return;
        Render();
        RaisePicked();
    }

    /// <summary>Tests: the row hover the kit's pointer slot delivers.</summary>
    internal void HoverSlotForTests(int slot, bool on) => HoverSlot(slot, on);

    private void HoverSlot(int slot, bool on)
    {
        if (!on || slot >= _lines.Count || _lines[slot].Entry is not { } entry)
        {
            HideTooltip();
            return;
        }

        string body = entry.Disabled ? entry.DisabledReason : entry.Tooltip;
        if (string.IsNullOrWhiteSpace(body))
        {
            HideTooltip();
            return;
        }

        ShowTooltip(RectOf(_slots[slot]), entry.Label, body);
    }

    // ---- Drawing -------------------------------------------------------------------------------

    protected override void Render()
    {
        if (IsDisposed || !Visible) return;
        var lines = _model.Lines(out int hidden);
        _lines.Clear();
        _lines.AddRange(lines);
        string suggestion = Suggestion;
        _status.SetCell(CellStatus, _model.Status(hidden));
        _status.SetCell(CellUse, suggestion.Length == 0 ? "" : "Use “" + suggestion + "”", visible: suggestion.Length > 0);
        TrySetVisible(_search, true);
        TrySetVisible(_status, true);

        var runtime = WidgetRuntime.Current;
        for (int i = 0; i < _slots.Count; i++)
        {
            var row = _slots[i];
            if (i >= lines.Count)
            {
                TrySetVisible(row, false);
                continue;
            }

            var line = lines[i];
            if (line.Kind == UiPickerLineKind.Section)
            {
                row.SetCell(UiWidgetLayout.CellTitle, line.Text, CellTone.Gold);
                row.SetCell(UiWidgetLayout.CellNote, "", visible: false);
                if (_icons) row.SetCell(UiWidgetLayout.CellIcon, visible: false);
                if (_options.Multi) row.SetCell(UiWidgetLayout.CellCheck, "", visible: false);
                row.SetSelected(false);
            }
            else
            {
                var e = line.Entry!;
                bool on = _model.IsSelected(e.Id);
                var tone = e.Disabled ? CellTone.Faint : CellTone.Normal;
                row.SetCell(UiWidgetLayout.CellTitle, e.Label, tone);
                string note = e.Disabled ? e.DisabledReason : e.Note;
                row.SetCell(UiWidgetLayout.CellNote, note, e.Disabled ? CellTone.Faint : CellTone.Muted, visible: note.Length > 0);
                if (_options.Multi)
                    row.SetCell(UiWidgetLayout.CellCheck, on ? _check : "", e.Disabled ? CellTone.Faint : CellTone.Gold, visible: true);
                if (_icons)
                {
                    object? icon = e.Icon;
                    if (icon == null && e.ItemPrefab != 0 && runtime != null)
                    {
                        try { icon = runtime.ItemIcon(e.ItemPrefab); }
                        catch { icon = null; }
                    }

                    row.SetCell(UiWidgetLayout.CellIcon, visible: true);
                    row.SetImage(UiWidgetLayout.CellIcon, icon);
                }

                row.SetSelected(on);
            }

            TrySetVisible(row, true);
        }
    }
}

/// <summary>P7: the picker's rows (see <see cref="UiWidgetLayout"/> for the shared rules).</summary>
internal static class UiPickerLayout
{
    internal static List<CellSpec> SearchCells(string placeholder) => new()
    {
        new CellSpec(CellKind.Input, "search") { Clearable = true, Skin = CellSkin.ValueBox }
            .With(width: CellWidth.Fill(CellLayout.MinInputWidth), placeholder: placeholder ?? "Search"),
    };

    internal static List<CellSpec> StatusCells() => new()
    {
        new CellSpec(CellKind.Muted, "status").With(width: CellWidth.Fill(UiWidgetLayout.CaptionMinWidth)),
        new CellSpec(CellKind.Button, "use") { Skin = CellSkin.Link, Optional = true }.With(align: CellAlign.Right, minWidth: 80f),
    };

    /// <summary>One result line: [check (multi)] [icon] caption [note].</summary>
    internal static List<CellSpec> LineCells(bool multi, bool icon)
    {
        var cells = new List<CellSpec>(4);
        if (multi)
            cells.Add(new CellSpec(CellKind.Button, UiWidgetLayout.CellCheck) { MinWidth = UiWidgetLayout.IconButtonWidth, Skin = CellSkin.Quiet }
                .With(width: CellWidth.Px(UiWidgetLayout.IconButtonWidth)));
        if (icon)
            cells.Add(new CellSpec(CellKind.Image, UiWidgetLayout.CellIcon) { MinWidth = UiWidgetLayout.IconSize, Skin = CellSkin.IconWell }
                .With(width: CellWidth.Px(UiWidgetLayout.IconSize)));
        cells.Add(new CellSpec(CellKind.Text, UiWidgetLayout.CellTitle)
            .With(width: CellWidth.Fill(UiWidgetLayout.CaptionMinWidth), minWidth: UiWidgetLayout.CaptionMinWidth));
        cells.Add(new CellSpec(CellKind.Muted, UiWidgetLayout.CellNote) { DropOrder = 1 }.With(align: CellAlign.Right, optional: true));
        return cells;
    }
}
