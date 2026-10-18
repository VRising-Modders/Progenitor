using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// CHECKLIST1: the checklist drawn as kit cell rows (<see cref="UiWidgets.Checklist"/>): the search row with the filter
/// dropdown, the filter's chips while it is open, the data table's sortable header, <see cref="UiChecklistOptions.VisibleRows"/>
/// slot rows built once and rewritten in place (only cells whose text, tone or visibility changed are written), the status
/// line with Select shown and Clear, and the Cancel and Confirm buttons. The body scrolls like the data table's (the wheel
/// claim, the game-art scroll bar, a controller at the first and last slot, the right stick) and fills its window with
/// <see cref="UiChecklistOptions.FillHeight"/>. See <see cref="UiChecklistModel"/> for the rules and
/// <see cref="UiChecklistLayout"/> for the geometry.
/// </summary>
internal sealed class UiChecklistHandle : UiComposedHandle, IUiChecklist
{
    private readonly struct CellState
    {
        internal CellState(string text, CellTone tone, bool visible)
        {
            Text = text;
            Tone = tone;
            Visible = visible;
        }

        internal string Text { get; }
        internal CellTone Tone { get; }
        internal bool Visible { get; }
    }

    private readonly UiChecklistModel _model;
    private readonly UiChecklistOptions _options;
    private readonly UiTableViewport _viewport;
    private readonly bool _preview;
    private readonly string _previewCaption;
    private readonly UiRowActionIcon? _previewIcon;
    private readonly UiIconMarks _viewMarks = new();
    private readonly IUiCellRow _search;
    private readonly List<IUiCellRow> _chipRows = new();
    private readonly List<int> _chipFilters = new();
    private readonly IUiCellHeader _header;
    private readonly List<IUiCellRow> _slots = new();
    private readonly IUiCellRow _status;
    private readonly IUiCellRow? _buttons;
    private readonly Dictionary<string, CellState>[] _cache;
    private readonly Dictionary<string, CellState> _rowCache = new(StringComparer.Ordinal);
    private readonly object?[] _images;
    private readonly bool[] _imageSet;
    private readonly bool?[] _selectedNow;
    private readonly bool?[] _expandedNow;
    private readonly List<Action<IReadOnlyList<string>>> _selectionChanged = new();
    private readonly List<Action<IReadOnlyList<UiTableSortKey>>> _sortChanged = new();
    private readonly List<Action<string>> _filterChanged = new();
    private readonly List<Action<IReadOnlyList<string>>> _confirmed = new();
    private readonly List<Action<bool>> _cancelled = new();
    private readonly List<Action<string>> _previewHandlers = new();
    private readonly List<Action<string>> _hovered = new();
    private readonly Dictionary<string, IUiMarkView?> _headerMarks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UiDrawnMark> _headerMarkNow = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _headerGoldNow = new(StringComparer.Ordinal);
    private IWidgetScrollBar? _bar;
    private IReadOnlyList<UiChecklistLine>? _renderedLines;
    private int _renderedTop = -1;
    private bool _rendering;
    private bool _barDragging;
    private bool _filterOpen;
    private bool _suspended;
    private bool _visibleBeforeSuspend = true;
    private string _hoveredId = "";
    private string _previewedId = "";
    private bool _focusBeforeSuspend;

    internal UiChecklistHandle(IUiPanel panel, string key, IEnumerable<UiChecklistEntry>? entries, UiChecklistOptions? options)
        : base(panel, key, key)
    {
        _options = options ?? new UiChecklistOptions();
        _model = new UiChecklistModel(_options);
        _model.SetEntries(entries); // a duplicate id throws here, before a row is built
        if (_options.Selected != null) _model.Replace(_options.Selected);
        _viewport = _options.FillHeight
            ? new UiTableViewport(_options.VisibleRows, UiTableViewport.MaxSlots)
            : new UiTableViewport(_options.VisibleRows);
        _preview = _options.Preview;
        _previewCaption = string.IsNullOrWhiteSpace(_options.PreviewCaption) ? "View" : _options.PreviewCaption.Trim();
        _previewIcon = _preview ? _options.PreviewIcon : null;
        bool viewIcon = _previewIcon != null;
        var cols = _model.Columns;
        float inset = UiDataTableLayout.ScrollInset;

        var labels = new List<string>();
        foreach (var f in _model.Filters) labels.Add(f.Label);
        _search = Part(panel.CellRow(new CellRowSpec(RowKey("search"),
            UiChecklistLayout.SearchCells(_options.Placeholder, _model.HasFilters, _options.FilterCaption ?? "", labels), plain: true, rightInset: inset)));
        Lease(_search.OnChanged(UiChecklistLayout.CellSearch, text =>
        {
            if (IsDisposed || !_model.SetQuery(text)) return;
            _viewport.SetTop(0);
            Render();
        }));
        if (_model.HasFilters)
        {
            Lease(_search.OnClick(UiChecklistLayout.CellFilter, () => { if (Live) FilterOpen = !FilterOpen; }));
            var chips = UiChecklistLayout.ChipRows(_model.Filters);
            int index = 0;
            for (int n = 0; n < chips.Count; n++)
            {
                var chipRow = Part(panel.CellRow(new CellRowSpec(RowKey("filters" + n), chips[n], plain: true)));
                _chipRows.Add(chipRow);
                foreach (var chip in chips[n])
                {
                    int at = index++;
                    _chipFilters.Add(at);
                    Lease(chipRow.OnClick(chip.Key, () => { if (Live) PickFilter(at); }));
                }
            }
        }

        _header = Part(panel.CellHeader(UiChecklistLayout.HeaderCells(cols, _preview, _previewCaption, viewIcon), RowKey("header"), inset));
        Lease(_header.OnSort(k => { if (Live) HeaderClick(k); }));

        int slots = _viewport.Built;
        _cache = new Dictionary<string, CellState>[slots];
        _images = new object?[slots];
        _imageSet = new bool[slots];
        _selectedNow = new bool?[slots];
        _expandedNow = new bool?[slots];
        var widgets = WidgetRuntime.Current;
        for (int i = 0; i < slots; i++)
        {
            int slot = i;
            _cache[i] = new Dictionary<string, CellState>(StringComparer.Ordinal);
            var row = Part(panel.CellRow(new CellRowSpec(
                RowKey("row" + i), UiChecklistLayout.RowCells(cols, _preview, _previewCaption, viewIcon), hover: true, soft: true,
                separator: true, rightInset: inset)));
            _slots.Add(row);
            Lease(row.OnRowClick(() => ClickSlot(slot)));
            Lease(row.OnHover(on => HoverSlot(slot, on)));
            Lease(row.OnClick(UiChecklistLayout.CellFold, () => FoldSlot(slot)));
            if (_preview)
            {
                Lease(row.OnClick(UiChecklistLayout.CellView, () => PreviewSlot(slot)));
                if (viewIcon)
                {
                    // The data table's icon button: a drawn face (the checklist's own hover brightens it) and a tooltip.
                    _viewMarks.Attach(row, UiChecklistLayout.CellView, watchHover: false);
                    Lease(row.OnCellHover(UiChecklistLayout.CellView, on => HoverView(slot, on)));
                }

                // A right press anywhere on the row (the row's own object; a cell button keeps its own press).
                if (widgets != null && RuntimeRowOf(row) is { } runtimeRow)
                {
                    try { Lease(widgets.WatchWidget(runtimeRow, e => RowPointer(slot, e), panel)); }
                    catch (Exception e) { Plugin.Logger?.LogWarning("Ui: checklist '" + Key + "' right-click watch: " + e.Message); }
                }
            }

            UiNav.Steer(row, cmd => SteerSlot(slot, cmd));
            UiNav.StickScroll(row, StickSlot);
            if (_options.Buttons) UiNav.EnterKey(row, EnterOnRow);
        }

        _status = Part(panel.CellRow(new CellRowSpec(RowKey("status"), UiChecklistLayout.StatusCells(), plain: true, rightInset: inset)));
        Lease(_status.OnClick(UiChecklistLayout.CellSelectShown, () => { if (Live) SelectShown(); }));
        Lease(_status.OnClick(UiChecklistLayout.CellClear, () => { if (Live) ClearSelection(); }));

        if (_options.Buttons)
        {
            _buttons = Part(panel.CellRow(UiChecklistLayout.ButtonRow(RowKey("buttons"), _options.CancelCaption, _options.ConfirmFormat)));
            Lease(_buttons.OnClick(UiChecklistLayout.CellCancel, () => { if (Live) Cancel(); }));
            Lease(_buttons.OnClick(UiChecklistLayout.CellConfirm, () => { if (Live) Confirm(); }));
        }

        if (widgets != null)
        {
            try { Lease(widgets.ClaimWheel(Panel, BodyRect, Wheel)); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: checklist '" + Key + "' wheel claim: " + e.Message); }
            AttachBar(widgets);
            if (_options.FillHeight)
            {
                try { _fillTick = widgets.EveryFrame(FillTick); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: checklist '" + Key + "' fill height: " + e.Message); }
            }
        }

        // Escape (B) closes the open filter first, then cancels while the mod listens for it.
        Lease(Ui.PushEscape(EscapeKey));
        SetupJustifyAndFocus(cols);
        Render();
    }

    private void SetupJustifyAndFocus(IReadOnlyList<UiTableColumn> cols)
    {
        if (WidgetRuntime.Current is not IWidgetCellField field) return;
        try
        {
            var header = RuntimeRowOf(_header);
            foreach (var col in cols)
            {
                if (!UiDataTableLayout.NeedsLeftJustify(cols, col)) continue;
                string cell = UiDataTableLayout.ViewKey(col.Key);
                if (header != null) field.SetCellJustify(header, cell, CellAlign.Left, UiDataTableLayout.LeftTextLead);
                foreach (var slot in _slots)
                    if (RuntimeRowOf(slot) is { } row) field.SetCellJustify(row, cell, CellAlign.Left, UiDataTableLayout.LeftTextLead);
            }

            foreach (var slot in _slots)
                if (RuntimeRowOf(slot) is { } row) field.FrameNavFocus(row);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: checklist '" + Key + "' text alignment: " + e.Message); }
    }

    // ---- IUiChecklist ------------------------------------------------------------------------------

    public IReadOnlyList<UiTableColumn> Columns => _model.Columns;

    public IReadOnlyList<UiChecklistEntry> Entries => _model.Entries;

    public void SetEntries(IEnumerable<UiChecklistEntry> entries)
    {
        if (IsDisposed) return;
        // Keep the scroll on the line that was at the top, by its entry id (or its group heading), not its number.
        Sync();
        var lines = _model.Lines();
        var top = _viewport.Top < lines.Count ? lines[_viewport.Top] : (UiChecklistLine?)null;
        _model.SetEntries(entries);
        if (top is { } t)
        {
            int line = t.Kind == UiChecklistLineKind.Entry ? _model.LineOf(t.Entry!.Id) : _model.LineOfGroup(t.Section);
            Sync();
            if (line >= 0) _viewport.SetTop(line);
        }

        Render();
    }

    public string Query
    {
        get => _model.Query;
        set
        {
            if (IsDisposed) return;
            bool changed = _model.SetQuery(value);
            _search.SetCell(UiChecklistLayout.CellSearch, _model.Query);
            if (!changed) return;
            _viewport.SetTop(0);
            Render();
        }
    }

    public IReadOnlyList<UiChecklistFilter> Filters => new List<UiChecklistFilter>(_model.Filters);

    public string Filter
    {
        get => _model.Filter;
        set
        {
            if (IsDisposed || !_model.SetFilter(value)) return;
            _viewport.SetTop(0);
            Render();
            Raise(_filterChanged, _model.Filter, "filter");
        }
    }

    public IDisposable OnFilterChanged(Action<string> handler) => Subscribe(_filterChanged, handler);

    public bool FilterOpen
    {
        get => _filterOpen;
        set
        {
            if (IsDisposed || !_model.HasFilters || _filterOpen == value) return;
            SetFilterOpen(value);
            Render();
        }
    }

    private void PickFilter(int index)
    {
        if (index < 0 || index >= _model.Filters.Count) return;
        SetFilterOpen(false);
        string id = UiChecklistModel.IdOf(_model.Filters[index]);
        if (_model.SetFilter(id))
        {
            _viewport.SetTop(0);
            Render();
            Raise(_filterChanged, _model.Filter, "filter");
        }
        else Render();
    }

    /// <summary>The body rows the open filter's chips took (<see cref="UiChecklistOptions.KeepHeight"/>), given back when it closes.</summary>
    private int _chipTaken;

    /// <summary>
    /// Open or close the filter's chips. With <see cref="UiChecklistOptions.KeepHeight"/> the chips' rows come out of the
    /// body while they show, so the window keeps its height (a body at its fewest rows keeps them, and the window grows).
    /// </summary>
    private void SetFilterOpen(bool open)
    {
        if (_filterOpen == open) return;
        _filterOpen = open;
        if (!_options.KeepHeight) return;
        int before = _viewport.Slots;
        bool changed;
        if (open)
        {
            changed = _viewport.SetSlots(before - _chipRows.Count);
            _chipTaken = before - _viewport.Slots;
        }
        else
        {
            changed = _viewport.SetSlots(before + _chipTaken);
            _chipTaken = 0;
        }

        if (changed && WidgetRuntime.Current is { } runtime) AttachBar(runtime);
    }

    public IReadOnlyList<UiChecklistEntry> Shown => new List<UiChecklistEntry>(_model.Shown);

    public IReadOnlyList<UiTableSortKey> Sort
    {
        get => new List<UiTableSortKey>(_model.Sort);
        set
        {
            if (IsDisposed || !_model.SetSort(value)) return;
            Render();
            RaiseSort();
        }
    }

    public bool SortBy(string column, bool add = false)
    {
        if (IsDisposed || !_model.ClickSort(column, add)) return false;
        Render();
        RaiseSort();
        return true;
    }

    public IDisposable OnSortChanged(Action<IReadOnlyList<UiTableSortKey>> handler) => Subscribe(_sortChanged, handler);

    public IReadOnlyList<string> Selected => new List<string>(_model.Selected);

    public bool IsSelected(string id) => _model.IsSelected(id);

    public bool Select(string id, bool selected = true)
    {
        if (IsDisposed || !_model.Set(id, selected)) return false;
        Render();
        RaiseSelection();
        return true;
    }

    public void SetSelected(IEnumerable<string> ids)
    {
        if (IsDisposed || !_model.Replace(ids)) return;
        Render();
        RaiseSelection();
    }

    public int SelectShown()
    {
        if (IsDisposed) return 0;
        int added = _model.SelectShown();
        if (added == 0) return 0;
        Render();
        RaiseSelection();
        return added;
    }

    public void ClearSelection()
    {
        if (IsDisposed || _model.Clear() == 0) return;
        Render();
        RaiseSelection();
    }

    public IDisposable OnSelectionChanged(Action<IReadOnlyList<string>> handler) => Subscribe(_selectionChanged, handler);

    public IReadOnlyList<string> Groups => new List<string>(_model.Groups);

    public bool SetGroupCollapsed(string section, bool collapsed)
    {
        if (IsDisposed || !_model.SetCollapsed(section, collapsed)) return false;
        Render();
        return true;
    }

    public bool IsGroupCollapsed(string section) => _model.IsCollapsed(section);

    public int TopIndex
    {
        get
        {
            Sync();
            return _viewport.Top;
        }
        set
        {
            if (IsDisposed) return;
            Sync();
            if (_viewport.SetTop(value)) Render();
        }
    }

    public int LineCount => _model.Lines().Count;

    public int VisibleRows => _viewport.Slots;

    public bool ScrollTo(string id)
    {
        if (IsDisposed) return false;
        int line = _model.LineOf(id);
        if (line < 0) return false;
        Sync();
        if (_viewport.EnsureShown(line)) Render();
        return true;
    }

    public string StatusText => _model.Status();

    public string ConfirmText => _model.ConfirmCaption(_model.Selected.Count);

    public bool CanConfirm => _model.Selected.Count > 0;

    public bool Confirm()
    {
        if (IsDisposed || !CanConfirm) return false;
        Raise(_confirmed, (IReadOnlyList<string>)new List<string>(_model.Selected), "confirm");
        return true;
    }

    public IDisposable OnConfirmed(Action<IReadOnlyList<string>> handler) => Subscribe(_confirmed, handler);

    public void Cancel()
    {
        if (IsDisposed) return;
        if (_filterOpen)
        {
            SetFilterOpen(false);
            Render(); // the chips hide
        }

        Raise(_cancelled, true, "cancel");
    }

    public IDisposable OnCancelled(Action handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        return Subscribe(_cancelled, (Action<bool>)(_ => handler()));
    }

    public bool Preview(string id)
    {
        if (IsDisposed || _model.Find(id) == null) return false;
        _previewedId = id;
        HideTooltip();
        Raise(_previewHandlers, id, "preview");
        return true;
    }

    public IDisposable OnPreview(Action<string> handler) => Subscribe(_previewHandlers, handler);

    public IDisposable OnRowHovered(Action<string> handler) => Subscribe(_hovered, handler);

    public void Suspend()
    {
        if (IsDisposed || _suspended) return;
        _visibleBeforeSuspend = Visible;
        _focusBeforeSuspend = FocusIsOnSlot();
        SetHovered("");
        HideTooltip();
        Visible = false;
        _suspended = true; // after the hide: from here a Visible change is held for Resume (DeferVisible)
    }

    public void Resume()
    {
        if (IsDisposed || !_suspended) return;
        _suspended = false;
        Visible = _visibleBeforeSuspend;
        if (!_focusBeforeSuspend || !Visible) return;
        _focusBeforeSuspend = false;
        // The controller was on the list: put it back on the previewed row (scrolled into view).
        if (_previewedId.Length == 0 || !ScrollTo(_previewedId)) return;
        Sync();
        int slot = _viewport.SlotOf(_model.LineOf(_previewedId));
        if (slot >= 0 && _slots[slot] is UiHandle row)
        {
            try { UiNav.FocusOn(row, ""); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: checklist '" + Key + "' focus: " + e.Message); }
        }
    }

    public bool Suspended => _suspended;

    /// <summary>While suspended, a Visible change (a tab switch) is remembered for <see cref="Resume"/> and shows nothing.</summary>
    protected override bool DeferVisible(bool value)
    {
        if (!_suspended) return false;
        _visibleBeforeSuspend = value;
        return true;
    }

    /// <summary>Whether NAV1's focus is on one of the body's rows now (the row or one of its cells).</summary>
    private bool FocusIsOnSlot()
    {
        var focus = UiNav.Focus;
        if (focus.IsEmpty) return false;
        foreach (var slot in _slots)
            if (ReferenceEquals(slot, focus.Handle)) return true;
        return false;
    }

    // ---- Events ------------------------------------------------------------------------------------

    private void RaiseSelection() => Raise(_selectionChanged, (IReadOnlyList<string>)new List<string>(_model.Selected), "selection");

    private void RaiseSort() => Raise(_sortChanged, (IReadOnlyList<UiTableSortKey>)new List<UiTableSortKey>(_model.Sort), "sort");

    private void SetHovered(string id)
    {
        if (string.Equals(id, _hoveredId, StringComparison.Ordinal)) return;
        _hoveredId = id;
        Raise(_hovered, id, "row hover");
    }

    // ---- Input -------------------------------------------------------------------------------------

    private void Sync() => _viewport.SetCount(_model.Lines().Count);

    private UiChecklistLine? LineAtSlot(int slot)
    {
        var lines = _model.Lines();
        Sync();
        int index = _viewport.LineAt(slot);
        return index >= 0 && index < lines.Count ? lines[index] : null;
    }

    private static UiClickModifiers Modifiers()
    {
        try { return WidgetRuntime.Current?.ClickModifiers() ?? UiClickModifiers.None; }
        catch { return UiClickModifiers.None; }
    }

    /// <summary>A header caption click: Shift adds the column as the next sort key.</summary>
    internal void HeaderClick(string column) => SortBy(column, (Modifiers() & UiClickModifiers.Shift) != 0);

    /// <summary>A click on slot <paramref name="slot"/> (also A / Space on a controller or keyboard).</summary>
    internal void ClickSlot(int slot) => ClickSlot(slot, Modifiers());

    /// <summary>
    /// A click on slot <paramref name="slot"/>: an entry flips (Shift: the range from the anchor takes its new state), a
    /// heading folds. Every click is one flip: a double-click is two (a preview is the View button or a right-click).
    /// </summary>
    internal void ClickSlot(int slot, UiClickModifiers mods)
    {
        if (!Live || _suspended) return;
        var line = LineAtSlot(slot);
        if (line == null) return;
        if (line.Value.Kind == UiChecklistLineKind.Section)
        {
            if (_options.CollapsibleGroups) SetGroupCollapsed(line.Value.Section, !_model.IsCollapsed(line.Value.Section));
            return;
        }

        if (!_model.Click(_viewport.Top + slot, (mods & UiClickModifiers.Shift) != 0)) return;
        Render();
        RaiseSelection();
    }

    /// <summary>The fold arrow on a section line.</summary>
    internal void FoldSlot(int slot)
    {
        if (!Live || _suspended) return;
        var line = LineAtSlot(slot);
        if (line is { Kind: UiChecklistLineKind.Section } l && _options.CollapsibleGroups)
            SetGroupCollapsed(l.Section, !_model.IsCollapsed(l.Section));
    }

    /// <summary>The row's preview button (and a right-click on the row).</summary>
    internal void PreviewSlot(int slot)
    {
        if (!Live || _suspended || !_preview) return;
        var line = LineAtSlot(slot);
        if (line is { Kind: UiChecklistLineKind.Entry } l) Preview(l.Entry!.Id);
    }

    private void RowPointer(int slot, UiPointerEvent e)
    {
        // A press that is not the primary button: the right (or middle) button.
        if (e.Kind == UiPointerEventKind.Down && !e.PrimaryHeld) PreviewSlot(slot);
    }

    /// <summary>Tests: the right press the row's pointer watch delivers.</summary>
    internal void RightClickForTests(int slot) => RowPointer(slot, new UiPointerEvent(UiPointerEventKind.Down, false));

    /// <summary>Enter on a focused row: Confirm (nothing is flipped). Taken even with nothing checked, so Enter never flips a row.</summary>
    internal bool EnterOnRow()
    {
        if (!Live || _suspended) return false;
        Confirm();
        return true;
    }

    /// <summary>Escape (B): close the open filter, else cancel while the mod listens; otherwise the press goes on.</summary>
    internal bool EscapeKey()
    {
        if (IsDisposed || !Visible || _suspended || Panel.State != UiHandleState.Shown) return false;
        // Only the topmost Escape target's press: a window stacked over this one (or an open dropdown list) closes first.
        if (!Ui.IsEscapeTarget(Panel)) return false;
        if (_filterOpen)
        {
            FilterOpen = false;
            return true;
        }

        if (_cancelled.Count == 0) return false;
        Cancel();
        return true;
    }

    internal bool SteerSlot(int slot, NavCommand command)
    {
        if (!Live) return false;
        Sync();
        bool moved = false;
        if (command == NavCommand.Up && slot == 0 && _viewport.Top > 0) moved = _viewport.ScrollBy(-1);
        else if (command == NavCommand.Down && slot == _viewport.Shown - 1 && _viewport.Top < _viewport.MaxTop) moved = _viewport.ScrollBy(1);
        if (moved) Render();
        return moved;
    }

    internal bool StickSlot(float amount)
    {
        if (!Live) return false;
        Sync();
        int before = _viewport.Top;
        bool took = _viewport.Stick(amount);
        if (_viewport.Top != before) Render();
        return took;
    }

    /// <summary>One wheel notch over the body (+1 toward the end). False at the end: the panel scrolls instead.</summary>
    internal bool Wheel(int notch)
    {
        if (!Live) return false;
        Sync();
        if (!_viewport.Wheel(notch)) return false;
        HideTooltip();
        Render();
        return true;
    }

    private void DragBar(float position)
    {
        if (!Live) return;
        Sync();
        if (!_viewport.SetTop(_viewport.TopForBar(position))) return;
        _barDragging = true;
        try { Render(); }
        finally { _barDragging = false; }
    }

    private ScreenRect BodyRect()
    {
        if (IsDisposed || !Visible || Panel.State != UiHandleState.Shown) return ScreenRect.Unknown;
        int shown = Math.Min(_slots.Count, _viewport.Shown + _fillers); // the filler rows too: the body keeps its height
        if (shown <= 0) return ScreenRect.Unknown;
        var a = RectOf(_slots[0]);
        var b = RectOf(_slots[shown - 1]);
        if (!a.IsKnown || !b.IsKnown) return ScreenRect.Unknown;
        return ScreenRect.FromCorners(Math.Min(a.MinX, b.MinX), Math.Min(a.MinY, b.MinY), Math.Max(a.MaxX, b.MaxX), Math.Max(a.MaxY, b.MaxY));
    }

    /// <summary>The slot row the pointer is on (its row hover), or -1.</summary>
    private int _hoveredSlot = -1;

    private void HoverSlot(int slot, bool on)
    {
        if (on) _hoveredSlot = slot;
        else if (_hoveredSlot == slot) _hoveredSlot = -1;
        var line = LineAtSlot(slot);
        if (!on)
        {
            // The pointer may enter the next row before it leaves this one: only this row's own exit clears the id.
            if (line is { Kind: UiChecklistLineKind.Entry } left && string.Equals(left.Entry!.Id, _hoveredId, StringComparison.Ordinal))
            {
                SetHovered("");
                HideTooltip();
            }

            return;
        }

        if (line is not { Kind: UiChecklistLineKind.Entry } l)
        {
            SetHovered("");
            HideTooltip();
            return;
        }

        var entry = l.Entry!;
        SetHovered(entry.Id);
        var body = new List<string>();
        if (entry.Disabled) body.Add(entry.DisabledReason.Trim());
        else if (!string.IsNullOrWhiteSpace(entry.Tooltip)) body.Add(entry.Tooltip);
        foreach (var col in _model.Columns)
        {
            string text = UiDataTableModel.Display(_model.CellOf(entry, col), col);
            if (UiDataTableLayout.IsCut(_model.Columns, col, text)) body.Add(col.Caption + ": " + text);
        }

        if (body.Count == 0)
        {
            HideTooltip();
            return;
        }

        var slotRow = _slots[slot];
        string title = entry.Label.Length > 0 ? entry.Label : entry.Id;
        ShowTooltip(RectOf(slotRow), title, string.Join("\n", body), () => slotRow.Visible && RectOf(slotRow).IsKnown);
    }

    /// <summary>Tests: the row hover the kit's pointer slot delivers.</summary>
    internal void HoverSlotForTests(int slot, bool on) => HoverSlot(slot, on);

    /// <summary>
    /// The pointer on an icon preview button (<see cref="UiChecklistOptions.PreviewIcon"/>): its mark brightens and the
    /// caption shows as its tooltip; leaving it gives the row its own tooltip back.
    /// </summary>
    private void HoverView(int slot, bool on)
    {
        if (_previewIcon == null || slot < 0 || slot >= _slots.Count) return;
        var slotRow = _slots[slot];
        _viewMarks.SetLit(slotRow, UiChecklistLayout.CellView, on);
        if (!on)
        {
            if (_hoveredSlot == slot) HoverSlot(slot, true);
            else HideTooltip();
            return;
        }

        if (!Visible || LineAtSlot(slot) is not { Kind: UiChecklistLineKind.Entry })
        {
            HideTooltip();
            return;
        }

        ShowTooltip(RectOf(slotRow, UiChecklistLayout.CellView), _previewCaption, "",
            () => slotRow.Visible && RectOf(slotRow, UiChecklistLayout.CellView).IsKnown);
    }

    /// <summary>Tests: the icon preview button's hover.</summary>
    internal void HoverViewForTests(int slot, bool on) => HoverView(slot, on);

    /// <summary>Tests and readback: the mark the preview button of slot <paramref name="slot"/> draws (none for a text button).</summary>
    internal UiDrawnMark ViewMarkAt(int slot) => _viewMarks.MarkOf(_slots[slot], UiChecklistLayout.CellView);

    /// <summary>Tests: the color that mark is drawn in now.</summary>
    internal PaletteColor ViewMarkColorAt(int slot) => _viewMarks.ColorOf(_slots[slot], UiChecklistLayout.CellView);

    private void AttachBar(IWidgetRuntime runtime)
    {
        var old = _bar;
        _bar = null;
        try { old?.Dispose(); }
        catch { /* */ }
        var top = RuntimeRowOf(_slots[0]);
        var bottom = RuntimeRowOf(_slots[Math.Max(0, Math.Min(_slots.Count, _viewport.Slots) - 1)]);
        if (top == null || bottom == null) return;
        try { _bar = runtime.AttachScrollBar(top, bottom, Panel); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: checklist '" + Key + "' scroll bar: " + e.Message); }
        if (_bar != null) _bar.OnDragged = DragBar;
    }

    // ---- A drawn row waiting for a building's icon (the data table's HUB1 rule) ------------------------------------

    private int _waitingBuilding;
    private int _iconVersion;
    private IDisposable? _iconTick;
    private UiBuildingIconWatch? _iconWatch;

    /// <summary>HUB1: the building icons drawn rows show, checked now and then for a sprite the game unloaded.</summary>
    private UiBuildingIconWatch IconWatch => _iconWatch ??= new UiBuildingIconWatch(_images.Length);

    internal int WaitingBuilding => _waitingBuilding;

    private void SyncIconTick()
    {
        if ((_waitingBuilding == 0 && _iconWatch is not { Any: true }) || IsDisposed)
        {
            var t = _iconTick;
            _iconTick = null;
            try { t?.Dispose(); }
            catch { /* */ }
            return;
        }

        if (_iconTick != null || WidgetRuntime.Current is not { } runtime) return;
        try { _iconTick = runtime.EveryFrame(IconTick); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: checklist '" + Key + "' icon wait: " + e.Message); }
    }

    internal void IconTick()
    {
        if (IsDisposed) return;
        // A drawn building icon whose sprite the game unloaded (or that changed) draws again: dropped or resolved anew.
        if (_iconWatch != null && _iconWatch.Due() && Visible && Panel.State == UiHandleState.Shown
            && WidgetRuntime.Current is { } watchRuntime && _iconWatch.Stale(watchRuntime.BuildingIcon))
        {
            Render();
            return;
        }

        if (_waitingBuilding == 0) return;
        if (UiBuildingIcons.Version != _iconVersion)
        {
            Render();
            return;
        }

        if (!Visible || Panel.State != UiHandleState.Shown) return;
        try { WidgetRuntime.Current?.BuildingIcon(_waitingBuilding); }
        catch { /* the next frame asks again */ }
        if (UiBuildingIcons.Version != _iconVersion) Render();
    }

    // ---- Fill the panel's height (the data table's HUB1 rule) ------------------------------------------------------

    private IDisposable? _fillTick;
    private int _fillFrames;

    internal void FillTick()
    {
        if (IsDisposed || !Visible || ++_fillFrames < UiDataTableHandle.FillEveryFrames) return;
        _fillFrames = 0;
        if (Panel.State != UiHandleState.Shown || WidgetRuntime.Current is not { } runtime) return;
        float maxViewport, content;
        try
        {
            if (!runtime.PanelRoom(Panel, out maxViewport, out content)) return;
        }
        catch { return; }

        ApplyFill(UiTableViewport.FillRows(maxViewport, content, ShownSlots(), UiTableViewport.SlotPitch, _viewport.Built));
    }

    internal bool ApplyFill(int rows)
    {
        if (rows < 0 || IsDisposed || !_viewport.SetSlots(rows)) return false;
        if (WidgetRuntime.Current is { } runtime) AttachBar(runtime);
        Render();
        return true;
    }

    private int ShownSlots()
    {
        int shown = 0;
        foreach (var slot in _slots)
            if (slot.Visible) shown++;
        return shown;
    }

    protected override void OnHidden()
    {
        base.OnHidden();
        SetHovered("");
    }

    protected override void OnDisposing()
    {
        _viewMarks.Dispose();
        try { _fillTick?.Dispose(); }
        catch { /* */ }
        _fillTick = null;
        try { _iconTick?.Dispose(); }
        catch { /* */ }
        _iconTick = null;
        try { _bar?.Dispose(); }
        catch { /* */ }
        _bar = null;
        foreach (var row in _slots)
        {
            UiNav.Steer(row, null);
            UiNav.StickScroll(row, null);
            UiNav.EnterKey(row, null);
        }
    }

    // ---- Readback --------------------------------------------------------------------------------------

    public Dictionary<string, object?> Describe()
    {
        Sync();
        var lines = _model.Lines();
        var slots = new List<Dictionary<string, object?>>(_viewport.Shown);
        for (int i = 0; i < _viewport.Shown; i++)
        {
            var line = lines[_viewport.Top + i];
            var e = line.Entry;
            slots.Add(new Dictionary<string, object?>
            {
                ["slot"] = i,
                ["row"] = RowKey("row" + i),
                ["kind"] = line.Kind == UiChecklistLineKind.Section ? "section" : "entry",
                ["id"] = e?.Id,
                ["section"] = line.Section,
                ["checked"] = e != null && _model.IsSelected(e.Id),
                ["disabled"] = e?.Disabled == true,
                ["reason"] = e?.DisabledReason ?? "",
            });
        }

        var filters = new List<Dictionary<string, object?>>();
        foreach (var f in _model.Filters) filters.Add(new Dictionary<string, object?> { ["id"] = f.Id, ["label"] = f.Label });
        var sort = new List<Dictionary<string, object?>>();
        foreach (var k in _model.Sort) sort.Add(new Dictionary<string, object?> { ["column"] = k.Column, ["ascending"] = k.Ascending });
        var columns = new List<string>();
        foreach (var c in _model.Columns) columns.Add(c.Key);
        var collapsed = new List<string>();
        foreach (var s in _model.Groups) if (_model.IsCollapsed(s)) collapsed.Add(s);
        return new Dictionary<string, object?>
        {
            ["kind"] = "checklist",
            ["key"] = Key,
            ["query"] = _model.Query,
            ["filter"] = _model.Filter,
            ["filters"] = filters,
            ["filterOpen"] = _filterOpen,
            ["sort"] = sort,
            ["sortMarks"] = HeaderMarks(),
            ["columns"] = columns,
            ["top"] = _viewport.Top,
            ["lines"] = lines.Count,
            ["entries"] = _model.Entries.Count,
            ["shown"] = _model.Shown.Count,
            ["visibleRows"] = _viewport.Slots,
            ["fillHeight"] = _options.FillHeight,
            ["slots"] = slots,
            ["selected"] = new List<string>(_model.Selected),
            ["groups"] = new List<string>(_model.Groups),
            ["collapsed"] = collapsed,
            ["status"] = StatusText,
            ["confirm"] = ConfirmText,
            ["canConfirm"] = CanConfirm,
            ["preview"] = _preview,
            ["previewIcon"] = _previewIcon is { } pi ? UiDataTableLayout.MarkOf(pi).Name : "",
            ["keepHeight"] = _options.KeepHeight,
            ["fillers"] = _fillers,
            ["bodyRows"] = ShownSlots(),
            ["chipRows"] = _filterOpen ? _chipRows.Count : 0,
            ["room"] = Room(),
            ["suspended"] = _suspended,
            ["scrollBar"] = _bar != null,
            ["rowKeys"] = RowKey("row") + "<n>",
        };
    }

    /// <summary>
    /// The readback's <c>room</c>: the panel's last layout (<see cref="IWidgetRuntime.PanelRoom"/>) as <c>{viewport,
    /// content}</c> in kit units, so a live check can see that a fold, a filter or a search left the content's height as it
    /// was; null before a layout or on a runtime that does not lay panels out.
    /// </summary>
    private Dictionary<string, object?>? Room()
    {
        try
        {
            if (WidgetRuntime.Current is not { } runtime || !runtime.PanelRoom(Panel, out float viewport, out float content)) return null;
            return new Dictionary<string, object?> { ["viewport"] = Math.Round(viewport, 1), ["content"] = Math.Round(content, 1) };
        }
        catch { return null; }
    }

    internal Dictionary<string, string> HeaderMarks()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in _headerMarkNow) d[p.Key] = p.Value.Name;
        return d;
    }

    /// <summary>Tests: slot <paramref name="slot"/>'s row.</summary>
    internal IUiCellRow SlotAt(int slot) => _slots[slot];

    // ---- Drawing -----------------------------------------------------------------------------------------

    protected override void Render()
    {
        if (IsDisposed || !Visible || _rendering) return;
        _rendering = true;
        try
        {
            var lines = _model.Lines();
            _viewport.SetCount(lines.Count);
            if (!ReferenceEquals(lines, _renderedLines) || _viewport.Top != _renderedTop) HideTooltip();
            _renderedLines = lines;
            _renderedTop = _viewport.Top;
            _waitingBuilding = 0;
            IconWatch.BeginRender();
            _iconVersion = UiBuildingIcons.Version;

            RenderSearch();
            bool icons = AnyIcon();
            RenderHeader(icons);
            // KeepHeight: blank filler rows under the lines keep the body at all its rows (a fold, a filter, a search).
            _fillers = _options.KeepHeight ? _viewport.Fillers(_viewport.Slots) : 0;
            int fillTo = _viewport.Shown + _fillers;
            for (int i = 0; i < _slots.Count; i++)
            {
                int index = _viewport.LineAt(i);
                if (index < 0)
                {
                    if (i < fillTo)
                    {
                        RenderFiller(i, icons);
                        SetFiller(i, true);
                        TrySetVisible(_slots[i], true);
                    }
                    else TrySetVisible(_slots[i], false);
                    continue;
                }

                SetFiller(i, false);
                RenderSlot(i, lines[index], icons);
                TrySetVisible(_slots[i], true);
            }

            RenderStatus();
            SyncIconTick();
            if (_bar != null && !_barDragging)
            {
                try { _bar.Set(_viewport.BarSize, _viewport.BarPosition); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: checklist '" + Key + "' scroll bar: " + e.Message); }
            }
        }
        finally { _rendering = false; }
    }

    private bool AnyIcon()
    {
        foreach (var e in _model.Entries) if (e.Icon != null || e.ItemPrefab != 0 || e.BuildingPrefab != 0) return true;
        return false;
    }

    /// <summary>Rewrite one cell of a part row when its text, tone or visibility changed (a hidden cell keeps its text).</summary>
    private static void WriteCell(IUiCellRow row, Dictionary<string, CellState> cache, string prefix, string key, string? text, CellTone tone, bool visible)
    {
        string ck = prefix + key;
        bool known = cache.TryGetValue(ck, out var was);
        if (!visible)
        {
            if (known && !was.Visible) return;
            row.SetCell(key, visible: false);
            cache[ck] = new CellState(known ? was.Text : "", known ? was.Tone : tone, false);
            return;
        }

        string t = text ?? "";
        if (known && was.Visible && was.Text == t && was.Tone == tone) return;
        row.SetCell(key, t, tone, true);
        cache[ck] = new CellState(t, tone, true);
    }

    private void Write(int slot, string key, string? text, CellTone tone, bool visible) =>
        WriteCell(_slots[slot], _cache[slot], "", key, text, tone, visible);

    private void RenderSearch()
    {
        TrySetVisible(_search, true);
        if (_model.HasFilters)
            WriteCell(_search, _rowCache, "search/", UiChecklistLayout.CellFilter, _model.FilterOption.Label, CellTone.Normal, true);

        int at = 0;
        foreach (var chipRow in _chipRows)
        {
            TrySetVisible(chipRow, _filterOpen);
            foreach (var chip in chipRow.Cells)
            {
                int index = _chipFilters[at++];
                if (!_filterOpen) continue;
                bool on = string.Equals(UiChecklistModel.IdOf(_model.Filters[index]), _model.Filter, StringComparison.Ordinal);
                WriteCell(chipRow, _rowCache, "chips/", chip.Key, _model.Filters[index].Label, on ? CellTone.ChipOn : CellTone.ChipOff, true);
            }
        }
    }

    private void RenderHeader(bool icons)
    {
        TrySetVisible(_header, true);
        WriteCell(_header, _rowCache, "header/", UiChecklistLayout.CellIcon, "", CellTone.Muted, icons);
        var sort = _model.Sort;
        foreach (var col in _model.Columns)
        {
            if (!col.Sortable) continue;
            var mark = UiDataTableLayout.SortMark(sort, col.Key);
            HeaderMark(col.Key, mark);
            HeaderGold(col.Key, !mark.IsNone);
        }
    }

    private void HeaderMark(string column, UiDrawnMark mark)
    {
        if (_headerMarkNow.TryGetValue(column, out var was) && was.Equals(mark)) return;
        _headerMarkNow[column] = mark;
        if (!_headerMarks.TryGetValue(column, out var view))
        {
            view = AttachMark(_header, UiDataTableLayout.ViewKey(column));
            _headerMarks[column] = view;
        }

        try { view?.Set(mark, MenuPalette.HeaderSorted); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: checklist '" + Key + "' sort mark: " + e.Message); }
    }

    private IUiMarkView? AttachMark(IUiCellRow row, string cell)
    {
        var runtimeRow = RuntimeRowOf(row);
        if (runtimeRow == null || WidgetRuntime.Current is not IWidgetMarks marks) return null;
        try
        {
            var view = marks.AttachMark(runtimeRow, cell, UiMarkSide.After);
            if (view != null) Lease(view);
            return view;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: checklist '" + Key + "' mark: " + e.Message);
            return null;
        }
    }

    private void HeaderGold(string column, bool gold)
    {
        string cell = UiDataTableLayout.ViewKey(column);
        if (_headerGoldNow.TryGetValue(cell, out bool was) && was == gold) return;
        _headerGoldNow[cell] = gold;
        if (WidgetRuntime.Current is not IWidgetCellField field || RuntimeRowOf(_header) is not { } header) return;
        try { field.SetCellTextColor(header, cell, gold ? MenuPalette.HeaderSorted : (PaletteColor?)null); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: checklist '" + Key + "' sorted caption: " + e.Message); }
    }

    /// <summary>Tests and readback: whether a column's caption is drawn gold (sorted).</summary>
    internal bool CaptionGold(string column) =>
        _headerGoldNow.TryGetValue(UiDataTableLayout.ViewKey(column), out bool gold) && gold;

    private void RenderSlot(int slot, UiChecklistLine line, bool icons)
    {
        var row = _slots[slot];
        bool section = line.Kind == UiChecklistLineKind.Section;
        var entry = line.Entry;
        bool on = !section && _model.IsSelected(entry!.Id);
        bool disabled = !section && entry!.Disabled;

        Write(slot, UiChecklistLayout.CellCheck, CellLayout.CheckText(on), disabled ? CellTone.Disabled : CellTone.Normal, !section);
        Write(slot, UiChecklistLayout.CellFold, UiWidgetLayout.ExpandGlyph, CellTone.Normal, section && _options.CollapsibleGroups);
        Write(slot, UiChecklistLayout.CellSection, section ? _model.SectionCaption(line.Section) : "", CellTone.Gold, section);

        object? icon = null;
        if (icons && !section)
        {
            icon = entry!.Icon;
            if (icon == null && entry.ItemPrefab != 0)
            {
                try { icon = WidgetRuntime.Current?.ItemIcon(entry.ItemPrefab); }
                catch { icon = null; }
            }

            if (icon == null && entry.BuildingPrefab != 0)
            {
                try { icon = WidgetRuntime.Current?.BuildingIcon(entry.BuildingPrefab); }
                catch { icon = null; }
                if (icon == null) _waitingBuilding = entry.BuildingPrefab;
                else IconWatch.Drawn(slot, entry.BuildingPrefab, icon);
            }
        }

        bool well = icon != null;
        Write(slot, UiChecklistLayout.CellIcon, "", CellTone.Normal, !section && icons && well);
        Write(slot, UiChecklistLayout.CellIconPad, "", CellTone.Muted, !section && icons && !well);
        if (well && (!_imageSet[slot] || !ReferenceEquals(icon, _images[slot])))
        {
            row.SetImage(UiChecklistLayout.CellIcon, icon);
            _images[slot] = icon;
            _imageSet[slot] = true;
        }

        foreach (var col in _model.Columns)
        {
            string view = UiDataTableLayout.ViewKey(col.Key);
            if (section)
            {
                Write(slot, view, null, CellTone.Normal, false);
                continue;
            }

            var cell = _model.CellOf(entry!, col);
            var tone = disabled ? CellTone.Faint : cell == null ? CellTone.Normal : LabelTones.ToCellTone(cell.Tone);
            Write(slot, view, UiDataTableModel.Display(cell, col), tone, true);
        }

        if (_preview)
        {
            Write(slot, UiChecklistLayout.CellView, _previewIcon != null ? "" : _previewCaption, CellTone.Normal, !section);
            if (_previewIcon is { } pi)
                _viewMarks.Set(row, UiChecklistLayout.CellView, section ? UiDrawnMark.None : UiDataTableLayout.MarkOf(pi));
        }

        bool open = section && !_model.IsCollapsed(line.Section);
        if (_expandedNow[slot] != open)
        {
            row.SetExpanded(open);
            _expandedNow[slot] = open;
        }

        if (_selectedNow[slot] != on)
        {
            row.SetSelected(on);
            _selectedNow[slot] = on;
        }
    }

    /// <summary>The filler rows drawn under the lines now (<see cref="UiChecklistOptions.KeepHeight"/>).</summary>
    private int _fillers;

    /// <summary>Which slots are fillers now (null: never one yet).</summary>
    private bool?[]? _fillerNow;

    /// <summary>Tests and readback: the filler rows drawn under the lines now.</summary>
    internal int Fillers => _fillers;

    /// <summary>Tests: whether slot <paramref name="slot"/> is a filler now.</summary>
    internal bool IsFillerSlot(int slot) => _fillerNow is { } f && f[slot] == true;

    /// <summary>
    /// Make slot <paramref name="slot"/> a filler or a row again (the data table's MinRows rule): a filler is passed over
    /// by the navigation and its row button takes no click and draws no hover wash. Written only when it changes.
    /// </summary>
    private void SetFiller(int slot, bool filler)
    {
        _fillerNow ??= new bool?[_slots.Count];
        bool was = _fillerNow[slot] == true;
        _fillerNow[slot] = filler;
        if (was == filler) return;
        UiNav.SetSkipped(_slots[slot], filler);
        if (WidgetRuntime.Current is IWidgetCellField field && RuntimeRowOf(_slots[slot]) is { } row)
        {
            try { field.SetRowInert(row, filler); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: checklist '" + Key + "' filler row: " + e.Message); }
        }
    }

    /// <summary>
    /// A filler slot: a blank line as tall as any, with the icon's and every column's place kept, and nothing to check,
    /// fold, preview or hover.
    /// </summary>
    private void RenderFiller(int slot, bool icons)
    {
        var row = _slots[slot];
        Write(slot, UiChecklistLayout.CellCheck, CellLayout.CheckText(false), CellTone.Normal, false);
        Write(slot, UiChecklistLayout.CellFold, UiWidgetLayout.ExpandGlyph, CellTone.Normal, false);
        Write(slot, UiChecklistLayout.CellSection, "", CellTone.Gold, false);
        Write(slot, UiChecklistLayout.CellIcon, "", CellTone.Normal, false);
        Write(slot, UiChecklistLayout.CellIconPad, "", CellTone.Muted, icons);
        foreach (var col in _model.Columns) Write(slot, UiDataTableLayout.ViewKey(col.Key), "", CellTone.Normal, true);
        if (_preview)
        {
            Write(slot, UiChecklistLayout.CellView, _previewIcon != null ? "" : _previewCaption, CellTone.Normal, false);
            if (_previewIcon != null) _viewMarks.Set(row, UiChecklistLayout.CellView, UiDrawnMark.None);
        }

        if (_expandedNow[slot] != false)
        {
            row.SetExpanded(false);
            _expandedNow[slot] = false;
        }

        if (_selectedNow[slot] != false)
        {
            row.SetSelected(false);
            _selectedNow[slot] = false;
        }
    }

    private void RenderStatus()
    {
        string status = _model.Status();
        TrySetVisible(_status, true);
        WriteCell(_status, _rowCache, "status/", UiChecklistLayout.CellStatus, status, CellTone.Muted, true);
        WriteCell(_status, _rowCache, "status/", UiChecklistLayout.CellSelectShown, UiChecklistLayout.SelectShownCaption, CellTone.Normal,
            _model.CanSelectShown());
        WriteCell(_status, _rowCache, "status/", UiChecklistLayout.CellClear, UiChecklistLayout.ClearCaption, CellTone.Normal,
            _model.Selected.Count > 0);
        if (_buttons == null) return;
        TrySetVisible(_buttons, true);
        WriteCell(_buttons, _rowCache, "buttons/", UiChecklistLayout.CellCancel,
            string.IsNullOrWhiteSpace(_options.CancelCaption) ? "Cancel" : _options.CancelCaption, CellTone.Normal, true);
        WriteCell(_buttons, _rowCache, "buttons/", UiChecklistLayout.CellConfirm, ConfirmText, CanConfirm ? CellTone.Normal : CellTone.Disabled, true);
    }
}
