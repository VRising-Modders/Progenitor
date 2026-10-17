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

    /// <summary>Two clicks on one row within this many seconds are a double-click (a preview).</summary>
    internal const double DoubleClickSeconds = 0.45;

    private readonly UiChecklistModel _model;
    private readonly UiChecklistOptions _options;
    private readonly UiTableViewport _viewport;
    private readonly bool _preview;
    private readonly string _previewCaption;
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
    private readonly Func<double> _clock;
    private IWidgetScrollBar? _bar;
    private IReadOnlyList<UiChecklistLine>? _renderedLines;
    private int _renderedTop = -1;
    private bool _rendering;
    private bool _barDragging;
    private bool _filterOpen;
    private bool _suspended;
    private bool _visibleBeforeSuspend = true;
    private string _hoveredId = "";
    private string? _lastClickId;
    private double _lastClickAt = double.NegativeInfinity;

    internal UiChecklistHandle(IUiPanel panel, string key, IEnumerable<UiChecklistEntry>? entries, UiChecklistOptions? options)
        : base(panel, key, key)
    {
        _options = options ?? new UiChecklistOptions();
        _model = new UiChecklistModel(_options);
        _model.SetEntries(entries); // a duplicate id throws here, before a row is built
        if (_options.Selected != null) _model.Replace(_options.Selected);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _clock = _options.Clock ?? (() => watch.Elapsed.TotalSeconds);
        _viewport = _options.FillHeight
            ? new UiTableViewport(_options.VisibleRows, UiTableViewport.MaxSlots)
            : new UiTableViewport(_options.VisibleRows);
        _preview = _options.Preview;
        _previewCaption = string.IsNullOrWhiteSpace(_options.PreviewCaption) ? "View" : _options.PreviewCaption.Trim();
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

        _header = Part(panel.CellHeader(UiChecklistLayout.HeaderCells(cols, _preview, _previewCaption), RowKey("header"), inset));
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
                RowKey("row" + i), UiChecklistLayout.RowCells(cols, _preview, _previewCaption), hover: true, soft: true,
                separator: true, rightInset: inset)));
            _slots.Add(row);
            Lease(row.OnRowClick(() => ClickSlot(slot)));
            Lease(row.OnHover(on => HoverSlot(slot, on)));
            Lease(row.OnClick(UiChecklistLayout.CellFold, () => FoldSlot(slot)));
            if (_preview)
            {
                Lease(row.OnClick(UiChecklistLayout.CellView, () => PreviewSlot(slot)));
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
        _model.SetEntries(entries);
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
            _filterOpen = value;
            Render();
        }
    }

    private void PickFilter(int index)
    {
        if (index < 0 || index >= _model.Filters.Count) return;
        _filterOpen = false;
        string id = UiChecklistModel.IdOf(_model.Filters[index]);
        if (_model.SetFilter(id))
        {
            _viewport.SetTop(0);
            Render();
            Raise(_filterChanged, _model.Filter, "filter");
        }
        else Render();
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

    public bool IsChecked(string id) => _model.IsChecked(id);

    public bool SetChecked(string id, bool on = true)
    {
        if (IsDisposed || !_model.Set(id, on)) return false;
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

    public int ClearSelection()
    {
        if (IsDisposed) return 0;
        int n = _model.Clear();
        if (n == 0) return 0;
        Render();
        RaiseSelection();
        return n;
    }

    public IDisposable OnSelectionChanged(Action<IReadOnlyList<string>> handler) => Subscribe(_selectionChanged, handler);

    public IReadOnlyList<string> Sections => new List<string>(_model.Sections);

    public bool SetSectionCollapsed(string section, bool collapsed)
    {
        if (IsDisposed || !_model.SetCollapsed(section, collapsed)) return false;
        Render();
        return true;
    }

    public bool IsSectionCollapsed(string section) => _model.IsCollapsed(section);

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

    public string StatusText { get; private set; } = "";

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
        _filterOpen = false;
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
        _suspended = true;
        SetHovered("");
        HideTooltip();
        Visible = false;
    }

    public void Resume()
    {
        if (IsDisposed || !_suspended) return;
        _suspended = false;
        Visible = _visibleBeforeSuspend;
    }

    public bool Suspended => _suspended;

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
    /// heading folds. A second pointer click on the same entry within <see cref="DoubleClickSeconds"/> is a double-click:
    /// it flips the entry back and asks for its preview (with <see cref="UiChecklistOptions.Preview"/>).
    /// </summary>
    internal void ClickSlot(int slot, UiClickModifiers mods)
    {
        if (!Live || _suspended) return;
        var line = LineAtSlot(slot);
        if (line == null) return;
        if (line.Value.Kind == UiChecklistLineKind.Section)
        {
            _lastClickId = null;
            if (_options.CollapsibleSections) SetSectionCollapsed(line.Value.Section, !_model.IsCollapsed(line.Value.Section));
            return;
        }

        var entry = line.Value.Entry!;
        bool shift = (mods & UiClickModifiers.Shift) != 0;
        double now = _clock();
        // A double-click is the pointer's: a controller or Space pressed twice quickly is two flips.
        bool pointer = !UiNav.Marked;
        bool second = _preview && pointer && !shift && string.Equals(_lastClickId, entry.Id, StringComparison.Ordinal)
            && now - _lastClickAt <= DoubleClickSeconds;
        _lastClickId = second ? null : entry.Id;
        _lastClickAt = now;
        bool changed = _model.Click(_viewport.Top + slot, shift && !second);
        if (changed)
        {
            Render();
            RaiseSelection();
        }

        if (second) Preview(entry.Id);
    }

    /// <summary>The fold arrow on a section line.</summary>
    internal void FoldSlot(int slot)
    {
        if (!Live || _suspended) return;
        var line = LineAtSlot(slot);
        if (line is { Kind: UiChecklistLineKind.Section } l && _options.CollapsibleSections)
            SetSectionCollapsed(l.Section, !_model.IsCollapsed(l.Section));
    }

    /// <summary>The row's preview button (and a right-click, a double-click).</summary>
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
        int shown = _viewport.Shown;
        if (shown <= 0) return ScreenRect.Unknown;
        var a = RectOf(_slots[0]);
        var b = RectOf(_slots[shown - 1]);
        if (!a.IsKnown || !b.IsKnown) return ScreenRect.Unknown;
        return ScreenRect.FromCorners(Math.Min(a.MinX, b.MinX), Math.Min(a.MinY, b.MinY), Math.Max(a.MaxX, b.MaxX), Math.Max(a.MaxY, b.MaxY));
    }

    private void HoverSlot(int slot, bool on)
    {
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

    internal int WaitingBuilding => _waitingBuilding;

    private void SyncIconTick()
    {
        if (_waitingBuilding == 0 || IsDisposed)
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
        if (IsDisposed || _waitingBuilding == 0) return;
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
                ["checked"] = e != null && _model.IsChecked(e.Id),
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
        foreach (var s in _model.Sections) if (_model.IsCollapsed(s)) collapsed.Add(s);
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
            ["sections"] = new List<string>(_model.Sections),
            ["collapsed"] = collapsed,
            ["status"] = StatusText,
            ["confirm"] = ConfirmText,
            ["canConfirm"] = CanConfirm,
            ["preview"] = _preview,
            ["suspended"] = _suspended,
            ["scrollBar"] = _bar != null,
            ["rowKeys"] = RowKey("row") + "<n>",
        };
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
            _iconVersion = UiBuildingIcons.Version;

            RenderSearch();
            bool icons = AnyIcon();
            RenderHeader(icons);
            for (int i = 0; i < _slots.Count; i++)
            {
                int index = _viewport.LineAt(i);
                if (index < 0)
                {
                    TrySetVisible(_slots[i], false);
                    continue;
                }

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
        bool on = !section && _model.IsChecked(entry!.Id);
        bool disabled = !section && entry!.Disabled;

        Write(slot, UiChecklistLayout.CellCheck, CellLayout.CheckText(on), disabled ? CellTone.Disabled : CellTone.Normal, !section);
        Write(slot, UiChecklistLayout.CellFold, UiWidgetLayout.ExpandGlyph, CellTone.Normal, section && _options.CollapsibleSections);
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

        if (_preview) Write(slot, UiChecklistLayout.CellView, _previewCaption, CellTone.Normal, !section);

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

    private void RenderStatus()
    {
        string status = _model.Status();
        TrySetVisible(_status, true);
        WriteCell(_status, _rowCache, "status/", UiChecklistLayout.CellStatus, status, CellTone.Muted, true);
        StatusText = status;
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
