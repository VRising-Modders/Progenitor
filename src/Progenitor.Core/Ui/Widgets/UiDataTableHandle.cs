using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7b: what the data table needs from the runtime about its value boxes beyond <see cref="IWidgetRuntime"/>: whether a
/// cell's field holds the keyboard now (an edit commits when the player leaves the field: Enter, a click elsewhere), and
/// the field's gray hint (an inherited value). Null on a runtime that does not implement it; the table then commits only
/// through <see cref="IUiDataTable.Edit"/>.
/// </summary>
internal interface IWidgetCellField
{
    /// <summary>The input cell <paramref name="cellKey"/> of <paramref name="row"/> has the keyboard focus now.</summary>
    bool CellFieldFocused(IRuntimeCellRow row, string cellKey);

    /// <summary>Rewrite the gray hint the input cell draws while it is empty.</summary>
    void SetCellHint(IRuntimeCellRow row, string cellKey, string hint);

    /// <summary>Take the keyboard away from the input cell (Escape left it): it stops being focused, nothing is committed.</summary>
    void ReleaseCellField(IRuntimeCellRow row, string cellKey);
}

/// <summary>
/// P7b: the data table drawn as kit cell rows (<see cref="UiWidgets.DataTable"/>): the header (the Install list's sortable
/// small-caps header), <see cref="UiDataTableOptions.VisibleRows"/> slot rows built once, the footer with the status line
/// and the Columns button, the refusal line, and the column chooser's chip rows. Scrolling, sorting, grouping and a data
/// refresh rewrite the slots' cells in place (only cells whose text, tone or visibility changed are written), so the panel
/// is never laid out again for them. See <see cref="UiDataTableModel"/> for the rules and <see cref="UiDataTableLayout"/>
/// for the geometry.
/// </summary>
internal sealed class UiDataTableHandle : UiComposedHandle, IUiDataTable
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

    private sealed class Draft
    {
        internal string RowId = "";
        internal string Column = "";
        internal string Text = "";
        internal int Slot;
    }

    private readonly UiDataTableModel _model;
    private readonly UiDataTableOptions _options;
    private readonly UiTableViewport _viewport;
    private readonly bool _multi;
    private readonly bool _chooser;
    private readonly IUiCellHeader _header;
    private readonly List<IUiCellRow> _slots = new();
    private readonly IUiCellRow _footer;
    private readonly IUiCellRow _reason;
    private readonly List<IUiCellRow> _chooserRows = new();
    private readonly List<string> _chooserKeys = new();
    private readonly Dictionary<string, CellState>[] _cache;
    private readonly Dictionary<string, CellState> _headerCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string>[] _written;
    private readonly Dictionary<string, string>[] _hints;
    private readonly object?[] _images;
    private readonly bool[] _imageSet;
    private readonly bool?[] _selectedNow;

    /// <summary>The row id each slot drew last (null: not a data row), to see a focused value box's row leave its slot.</summary>
    private readonly string?[] _slotRow;
    private readonly List<Draft> _leave = new();
    private IReadOnlyList<UiTableLine>? _renderedLines;
    private int _renderedTop = -1;
    private readonly bool?[] _expandedNow;
    private readonly Dictionary<string, Draft> _drafts = new(StringComparer.Ordinal);
    private readonly List<Action<IReadOnlyList<UiTableSortKey>>> _sortChanged = new();
    private readonly List<Action<IReadOnlyList<string>>> _columnsChanged = new();
    private readonly List<Action<IReadOnlyList<string>>> _selectionChanged = new();
    private readonly List<Action<KeyValuePair<string, bool>>> _expandHandlers = new();
    private readonly List<Action<UiTableEdit>> _editHandlers = new();
    private readonly IWidgetScrollBar? _bar;
    private readonly Dictionary<string, UiTermBinding> _terms = new(StringComparer.Ordinal);
    private IDisposable? _tick;
    private bool _chooserOpen;
    private bool _rendering;
    private bool _barDragging;
    private int _spinnerFrame = -1;
    private bool _footerWritten;
    private string _columnsCaption = "";
    private string _reasonText = "";
    private string _check = UiWidgetLayout.CheckGlyph;
    private readonly Dictionary<string, IUiMarkView?> _headerMarks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UiDrawnMark> _headerMarkNow = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IUiMarkView?>[] _spinners;
    private readonly Dictionary<string, UiDrawnMark>[] _spinnerNow;

    internal UiDataTableHandle(
        IUiPanel panel, string key, IEnumerable<UiTableColumn> columns, IEnumerable<UiTableRow>? rows, UiDataTableOptions? options)
        : base(panel, key, key)
    {
        _options = options ?? new UiDataTableOptions();
        _model = new UiDataTableModel(columns, _options);
        _model.SetRows(rows); // a duplicate id throws here, before a row is built
        _viewport = new UiTableViewport(_options.VisibleRows);
        _multi = _options.MultiSelect && _options.Selectable;
        _chooser = _options.ColumnChooser && _model.ChooserColumns().Count > 0;
        ResolveGlyphs();

        var cols = _model.Columns;
        _header = Part(panel.CellHeader(UiDataTableLayout.HeaderCells(cols, _multi), RowKey("header"), UiDataTableLayout.ScrollInset));
        Lease(_header.OnSort(k => { if (Live) HeaderClick(k); }));
        foreach (var col in cols)
        {
            if (string.IsNullOrWhiteSpace(col.Term)) continue;
            // The header's cells take no cell hover of their own (OnCellHover is a no-op on a header), so the binding
            // watches nothing and the header's hover path below forwards to it.
            try { _terms[col.Key] = UiTerms.MarkCell(_header, UiDataTableLayout.ViewKey(col.Key), col.Term, watchHover: false); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: data table '" + Key + "' term '" + col.Term + "': " + e.Message); }
        }

        WatchHeaderTooltips();

        int slots = _viewport.Slots;
        _cache = new Dictionary<string, CellState>[slots];
        _written = new Dictionary<string, string>[slots];
        _hints = new Dictionary<string, string>[slots];
        _images = new object?[slots];
        _imageSet = new bool[slots];
        _spinners = new Dictionary<string, IUiMarkView?>[slots];
        _spinnerNow = new Dictionary<string, UiDrawnMark>[slots];
        _selectedNow = new bool?[slots];
        _slotRow = new string?[slots];
        _expandedNow = new bool?[slots];
        for (int i = 0; i < slots; i++)
        {
            int slot = i;
            _cache[i] = new Dictionary<string, CellState>(StringComparer.Ordinal);
            _written[i] = new Dictionary<string, string>(StringComparer.Ordinal);
            _hints[i] = new Dictionary<string, string>(StringComparer.Ordinal);
            _spinners[i] = new Dictionary<string, IUiMarkView?>(StringComparer.Ordinal);
            _spinnerNow[i] = new Dictionary<string, UiDrawnMark>(StringComparer.Ordinal);
            // The Mods list's entry rows: a light wash under the pointer, the gold entry band with its 3-unit stripe when
            // selected (ModsListLayout.RowFill), and NAV1's own frame for the controller focus — three distinct states.
            var row = Part(panel.CellRow(new CellRowSpec(
                RowKey("row" + i), UiDataTableLayout.RowCells(cols, _multi), hover: true, soft: true,
                separator: true, rightInset: UiDataTableLayout.ScrollInset)));
            _slots.Add(row);
            Lease(row.OnRowClick(() => ClickSlot(slot)));
            Lease(row.OnHover(on => HoverSlot(slot, on)));
            if (_multi) Lease(row.OnClick(UiDataTableLayout.CellCheck, () => CheckSlot(slot)));
            Lease(row.OnClick(UiDataTableLayout.CellExpand, () => ExpandSlot(slot)));
            foreach (var col in cols)
            {
                if (!IsEditable(col)) continue;
                var c = col;
                Lease(row.OnChanged(UiDataTableLayout.EditKey(c.Key), text => Typed(slot, c, text)));
            }

            UiNav.Steer(row, cmd => SteerSlot(slot, cmd));
            UiNav.StickScroll(row, StickSlot);
        }

        _footer = Part(panel.CellRow(new CellRowSpec(RowKey("footer"), UiDataTableLayout.FooterCells(_chooser), plain: true,
            rightInset: UiDataTableLayout.ScrollInset)));
        if (_chooser) Lease(_footer.OnClick(UiDataTableLayout.CellColumns, () => { if (Live) ChooserOpen = !ChooserOpen; }));
        _reason = Part(panel.CellRow(new CellRowSpec(RowKey("reason"), UiDataTableLayout.ReasonCells(), plain: true,
            rightInset: UiDataTableLayout.ScrollInset)));

        if (_chooser)
        {
            var chips = UiDataTableLayout.ChooserRows(_model.ChooserColumns());
            for (int n = 0; n < chips.Count; n++)
            {
                var chipRow = Part(panel.CellRow(new CellRowSpec(RowKey("columns" + n), chips[n], plain: true)));
                _chooserRows.Add(chipRow);
                foreach (var chip in chips[n])
                {
                    string column = chip.Key.Substring("col:".Length);
                    _chooserKeys.Add(column);
                    Lease(chipRow.OnClick(chip.Key, () => { if (Live) SetColumnVisible(column, _model.IsHidden(column)); }));
                }
            }
        }

        var runtime = WidgetRuntime.Current;
        if (runtime != null)
        {
            try { Lease(runtime.ClaimWheel(Panel, BodyRect, Wheel)); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: data table '" + Key + "' wheel claim: " + e.Message); }
            var top = RuntimeRowOf(_slots[0]);
            var bottom = RuntimeRowOf(_slots[_slots.Count - 1]);
            if (top != null && bottom != null)
            {
                try { _bar = runtime.AttachScrollBar(top, bottom, panel); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: data table '" + Key + "' scroll bar: " + e.Message); }
                if (_bar != null)
                {
                    _bar.OnDragged = DragBar;
                    Lease(_bar);
                }
            }
        }

        // Escape in one of the table's value boxes reverts the typing and leaves the field; the panel stays.
        Lease(Ui.PushEscape(EscapeField));
        Render();
    }

    private static bool IsEditable(UiTableColumn c) => c.Editable && c.Kind == UiTableColumnKind.Number;

    private void ResolveGlyphs()
    {
        var runtime = WidgetRuntime.Current;
        if (runtime == null) return;
        try
        {
            _check = runtime.DrawableGlyph(UiWidgetLayout.CheckGlyph, UiWidgetLayout.CheckFallback);
        }
        catch { /* the preferred glyphs stay */ }
    }

    // ---- IUiDataTable ------------------------------------------------------------------------

    public IReadOnlyList<UiTableColumn> Columns => _model.Columns;

    public IReadOnlyList<UiTableRow> Rows => _model.Rows;

    public void SetRows(IEnumerable<UiTableRow> rows)
    {
        if (IsDisposed) return;
        _model.SetRows(rows);
        Render();
    }

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

    public IReadOnlyList<string> HiddenColumns
    {
        get => _model.HiddenInOrder();
        set
        {
            if (IsDisposed || !_model.SetHidden(value)) return;
            Render();
            RaiseColumns();
        }
    }

    public bool SetColumnVisible(string column, bool visible)
    {
        if (IsDisposed || !_model.SetVisible(column, visible)) return false;
        Render();
        RaiseColumns();
        return true;
    }

    public IDisposable OnColumnsChanged(Action<IReadOnlyList<string>> handler) => Subscribe(_columnsChanged, handler);

    public bool ChooserOpen
    {
        get => _chooserOpen;
        set
        {
            if (IsDisposed || !_chooser || _chooserOpen == value) return;
            _chooserOpen = value;
            Render();
        }
    }

    public IReadOnlyList<string> Selected => new List<string>(_model.Selected);

    public void SetSelected(IEnumerable<string> ids)
    {
        if (IsDisposed || !_model.Replace(ids)) return;
        Render();
        RaiseSelection();
    }

    public IDisposable OnSelectionChanged(Action<IReadOnlyList<string>> handler) => Subscribe(_selectionChanged, handler);

    public bool SetExpanded(string id, bool expanded)
    {
        if (IsDisposed || !_model.SetExpanded(id, expanded)) return false;
        Render();
        Raise(_expandHandlers, new KeyValuePair<string, bool>(id, expanded), "expand");
        return true;
    }

    public bool IsExpanded(string id) => _model.IsExpanded(id);

    public IDisposable OnExpand(Action<string, bool> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        return Subscribe(_expandHandlers, (Action<KeyValuePair<string, bool>>)(p => handler(p.Key, p.Value)));
    }

    public bool Grouped
    {
        get => _model.Grouped;
        set
        {
            if (IsDisposed || _model.Grouped == value) return;
            _model.Grouped = value;
            Render();
        }
    }

    public IReadOnlyList<string> Groups => _model.Grouped ? new List<string>(_model.Groups) : new List<string>();

    public bool SetGroupCollapsed(string group, bool collapsed)
    {
        if (IsDisposed || !_model.SetCollapsed(group, collapsed)) return false;
        Render();
        return true;
    }

    public bool IsGroupCollapsed(string group) => _model.IsCollapsed(group);

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

    public IDisposable OnEdit(Action<UiTableEdit> handler) => Subscribe(_editHandlers, handler);

    public UiTableEdit? Edit(string rowId, string column, string text)
    {
        if (IsDisposed) return null;
        _drafts.Remove(UiDataTableModel.EditKey(rowId ?? "", column ?? ""));
        return StartEdit(rowId ?? "", column ?? "", text ?? "");
    }

    public IReadOnlyList<UiTableEdit> PendingEdits => new List<UiTableEdit>(_model.Pending);

    public string StatusText { get; private set; } = "";

    public string StatusReason => _model.Reason;

    // ---- Events ------------------------------------------------------------------------------

    private void RaiseSort() => Raise(_sortChanged, (IReadOnlyList<UiTableSortKey>)new List<UiTableSortKey>(_model.Sort), "sort");

    private void RaiseColumns() => Raise(_columnsChanged, (IReadOnlyList<string>)_model.HiddenInOrder(), "columns");

    private void RaiseSelection() => Raise(_selectionChanged, (IReadOnlyList<string>)new List<string>(_model.Selected), "selection");

    // ---- Input -------------------------------------------------------------------------------

    private void Sync() => _viewport.SetCount(_model.Lines().Count);

    private UiTableLine? LineAtSlot(int slot)
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

    /// <summary>A click on slot <paramref name="slot"/> (also A on a controller): select a row (Ctrl, Shift), fold a group.</summary>
    internal void ClickSlot(int slot) => ClickSlot(slot, Modifiers());

    internal void ClickSlot(int slot, UiClickModifiers mods)
    {
        if (!Live) return;
        var line = LineAtSlot(slot);
        if (line == null) return;
        if (line.Value.Kind == UiTableLineKind.GroupHeader)
        {
            SetGroupCollapsed(line.Value.Group, !_model.IsCollapsed(line.Value.Group));
            return;
        }

        if (line.Value.Kind != UiTableLineKind.Row) return;
        if (!_model.Click(_viewport.Top + slot, mods)) return;
        Render();
        RaiseSelection();
    }

    /// <summary>The check cell: add or remove the row (Ctrl-click without the key, for a controller).</summary>
    internal void CheckSlot(int slot)
    {
        if (!Live) return;
        var line = LineAtSlot(slot);
        if (line is not { Kind: UiTableLineKind.Row } l || !_model.Toggle(l.Row!.Id)) return;
        Render();
        RaiseSelection();
    }

    /// <summary>The expander: open or close a row's drawer, fold or unfold a group.</summary>
    internal void ExpandSlot(int slot)
    {
        if (!Live) return;
        var line = LineAtSlot(slot);
        if (line == null) return;
        if (line.Value.Kind == UiTableLineKind.GroupHeader)
            SetGroupCollapsed(line.Value.Group, !_model.IsCollapsed(line.Value.Group));
        else if (line.Value.Kind == UiTableLineKind.Row)
            SetExpanded(line.Value.Row!.Id, !_model.IsExpanded(line.Value.Row.Id));
    }

    /// <summary>A keystroke in slot <paramref name="slot"/>'s value box of <paramref name="column"/>: kept as a draft until the field is left.</summary>
    internal void Typed(int slot, UiTableColumn column, string? text)
    {
        if (IsDisposed || slot < 0 || slot >= _slots.Count) return;
        text ??= "";
        string cellKey = UiDataTableLayout.EditKey(column.Key);
        // Our own rewrite of the field echoes back through the same event: not the player's typing.
        if (_written[slot].TryGetValue(cellKey, out var written) && string.Equals(written, text, StringComparison.Ordinal)) return;
        if (!Live) return;
        var line = LineAtSlot(slot);
        if (line is not { Kind: UiTableLineKind.Row } l || l.Row == null || !_model.CanEdit(l.Row, column)) return;
        // Only the box that holds the keyboard types (a box released when its row left the slot takes no more keys).
        if (WidgetRuntime.Current is IWidgetCellField field && RuntimeRowOf(_slots[slot]) is { } runtimeRow)
        {
            bool focused;
            try { focused = field.CellFieldFocused(runtimeRow, cellKey); }
            catch { focused = true; }
            if (!focused) return;
        }

        string id = l.Row.Id;
        _drafts[UiDataTableModel.EditKey(id, column.Key)] = new Draft { RowId = id, Column = column.Key, Text = text, Slot = slot };
        _written[slot][cellKey] = text;
        if (_cache[slot].TryGetValue(cellKey, out var st)) _cache[slot][cellKey] = new CellState(text, st.Tone, st.Visible);
        EnsureTick();
    }

    /// <summary>Commit the draft of <paramref name="rowId"/>'s <paramref name="column"/> now (the player left the field).</summary>
    internal UiTableEdit? CommitDraft(string rowId, string column)
    {
        string key = UiDataTableModel.EditKey(rowId, column);
        if (!_drafts.TryGetValue(key, out var d)) return null;
        _drafts.Remove(key);
        return StartEdit(d.RowId, d.Column, d.Text);
    }

    internal int DraftCount => _drafts.Count;

    /// <summary>
    /// Escape (B on a controller) while one of the table's value boxes has the keyboard: the typing is dropped, the box
    /// shows its value again and gives up the keyboard, nothing is committed, and the press goes no further (the panel
    /// stays). The TMP input field's own Escape (<c>restoreOriginalTextOnEscape</c>) does the same. False when no box of
    /// this table is focused: the press goes on (a dialog, the panel sweep).
    /// </summary>
    internal bool EscapeField()
    {
        if (IsDisposed || !Visible || WidgetRuntime.Current is not IWidgetCellField field) return false;
        Sync();
        for (int slot = 0; slot < _viewport.Shown; slot++)
        {
            var runtimeRow = RuntimeRowOf(_slots[slot]);
            if (runtimeRow == null) continue;
            foreach (var col in _model.Columns)
            {
                if (!IsEditable(col) || _model.IsHidden(col.Key)) continue;
                string cell = UiDataTableLayout.EditKey(col.Key);
                bool focused;
                try { focused = field.CellFieldFocused(runtimeRow, cell); }
                catch { focused = false; }
                if (!focused) continue;
                var line = LineAtSlot(slot);
                if (line is { Kind: UiTableLineKind.Row } l && l.Row != null)
                    _drafts.Remove(UiDataTableModel.EditKey(l.Row.Id, col.Key));
                // Forget what the box was last written, so the render writes the value back over the typing.
                _cache[slot].Remove(cell);
                _written[slot].Remove(cell);
                try { field.ReleaseCellField(runtimeRow, cell); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: data table '" + Key + "' escape: " + e.Message); }
                Render();
                return true;
            }
        }

        return false;
    }

    private UiTableEdit? StartEdit(string rowId, string column, string text)
    {
        var edit = _model.Begin(rowId, column, text, CompleteEdit, out string refusal);
        if (edit == null)
        {
            if (refusal.Length > 0) _model.Reason = refusal;
            Render();
            return null;
        }

        _model.Reason = "";
        Render();
        EnsureTick();
        Raise(_editHandlers, edit, "edit");
        return edit;
    }

    private bool CompleteEdit(UiTableEdit edit, bool resolved, string? text)
    {
        bool done = _model.Complete(edit, resolved, text);
        if (done && !IsDisposed) Render();
        return done;
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

    /// <summary>The body's screen rect: the first slot's top to the last drawn slot's bottom.</summary>
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
        if (!on)
        {
            HideTooltip();
            return;
        }

        var line = LineAtSlot(slot);
        if (line is not { Kind: UiTableLineKind.Row } l)
        {
            HideTooltip();
            return;
        }

        var row = l.Row!;
        var body = new List<string>();
        if (!string.IsNullOrWhiteSpace(row.Tooltip)) body.Add(row.Tooltip);
        foreach (var col in _model.Columns)
        {
            if (_model.IsHidden(col.Key)) continue;
            string? reason = _model.RejectedAt(row.Id, col.Key);
            if (!string.IsNullOrEmpty(reason)) body.Add(col.Caption + ": " + reason);
            // Inherited and own values differ by words as well as by tone (the style guide: never color alone).
            var cell = row[col.Key];
            if (cell is { Inherited: true }) body.Add(UiDataTableLayout.InheritedLine(col, cell));
        }

        if (body.Count == 0)
        {
            HideTooltip();
            return;
        }

        var slotRow = _slots[slot];
        ShowTooltip(RectOf(slotRow), _model.CaptionOf(row), string.Join("\n", body), () => slotRow.Visible && RectOf(slotRow).IsKnown);
    }

    /// <summary>Tests: the row hover the kit's pointer slot delivers.</summary>
    internal void HoverSlotForTests(int slot, bool on) => HoverSlot(slot, on);

    private void WatchHeaderTooltips()
    {
        var runtime = WidgetRuntime.Current;
        var header = RuntimeRowOf(_header);
        if (runtime == null || header == null) return;
        foreach (var col in _model.Columns)
        {
            // Only a sortable caption has a widget (a click target) to watch.
            if (!col.Sortable || (string.IsNullOrWhiteSpace(col.Tooltip) && string.IsNullOrWhiteSpace(col.Term))) continue;
            var c = col;
            try
            {
                var widget = header.CellWidget(UiDataTableLayout.ViewKey(c.Key));
                if (widget == null) continue;
                Lease(runtime.WatchHover(widget, on => HoverHeader(c, on), Panel));
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: data table '" + Key + "' header tooltip: " + e.Message); }
        }
    }

    /// <summary>Tests: the header caption hover the runtime's hover watch delivers.</summary>
    internal void HoverHeaderForTests(string column, bool on)
    {
        var col = _model.Column(column);
        if (col != null) HoverHeader(col, on);
    }

    /// <summary>Tests and readback: the term binding of a column's caption, or null.</summary>
    internal UiTermBinding? TermOf(string column) => _terms.TryGetValue(column, out var b) ? b : null;

    private void HoverHeader(UiTableColumn column, bool on)
    {
        _terms.TryGetValue(column.Key, out var term);
        if (!on || !Visible)
        {
            term?.Hover(false);
            HideTooltip();
            return;
        }

        // A defined term explains the caption (the term wins over a plain Tooltip, as in the tabs and the list).
        if (term != null && UiTerms.IsDefined(PluginId, term.Term))
        {
            term.Hover(true);
            return;
        }

        string cell = UiDataTableLayout.ViewKey(column.Key);
        ShowTooltip(RectOf(_header, cell), column.Caption, column.Tooltip, () => _header.Visible && RectOf(_header, cell).IsKnown);
    }

    // ---- Frame tick: drafts, timeouts, the spinner --------------------------------------------------

    /// <summary>Run the frame tick while a draft or an edit is open, and only then.</summary>
    private void EnsureTick()
    {
        if (IsDisposed) return;
        if (_drafts.Count == 0 && _model.Pending.Count == 0)
        {
            StopTick();
            return;
        }

        if (_tick != null) return;
        var runtime = WidgetRuntime.Current;
        if (runtime == null) return;
        try { _tick = runtime.EveryFrame(Tick); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: data table '" + Key + "' frame tick: " + e.Message); }
    }

    private void StopTick()
    {
        var t = _tick;
        _tick = null;
        try { t?.Dispose(); }
        catch { /* */ }
    }

    internal bool Ticking => _tick != null;

    /// <summary>
    /// One frame while a draft or an edit is open: a draft whose field lost the keyboard (Enter, a click elsewhere) commits;
    /// an edit past the timeout is rejected; the spinner turns.
    /// </summary>
    internal void Tick()
    {
        if (IsDisposed)
        {
            StopTick();
            return;
        }

        var field = WidgetRuntime.Current as IWidgetCellField;
        if (field != null && _drafts.Count > 0)
        {
            var leave = _leave;
            leave.Clear();
            foreach (var d in _drafts.Values)
            {
                var line = LineAtSlot(d.Slot);
                bool there = line is { Kind: UiTableLineKind.Row } l && string.Equals(l.Row!.Id, d.RowId, StringComparison.Ordinal);
                var row = RuntimeRowOf(_slots[d.Slot]);
                bool focused = false;
                if (there && row != null)
                {
                    try { focused = field.CellFieldFocused(row, UiDataTableLayout.EditKey(d.Column)); }
                    catch { focused = false; }
                }

                if (!focused) leave.Add(d);
            }

            for (int i = 0; i < leave.Count; i++) CommitDraft(leave[i].RowId, leave[i].Column);
            leave.Clear();
        }

        // No timeout (EditTimeout <= 0): nothing to expire, so the check never runs; the tick only turns the spinner.
        bool dirty = _model.Expires && _model.Expire() > 0;
        if (_model.Pending.Count > 0)
        {
            int frame = UiDataTableLayout.SpinnerFrame(_model.Now);
            if (frame != _spinnerFrame)
            {
                _spinnerFrame = frame;
                dirty = true;
            }
        }

        if (dirty) Render();
        if (_drafts.Count == 0 && _model.Pending.Count == 0) StopTick();
    }

    protected override void OnHidden()
    {
        base.OnHidden();
        foreach (var d in new List<Draft>(_drafts.Values)) CommitDraft(d.RowId, d.Column);
    }

    protected override void OnDisposing()
    {
        StopTick();
        _drafts.Clear();
        foreach (var t in _terms.Values)
        {
            try { t.Dispose(); }
            catch { /* */ }
        }

        _terms.Clear();
        foreach (var row in _slots)
        {
            UiNav.Steer(row, null);
            UiNav.StickScroll(row, null);
        }
    }

    // ---- Readback ----------------------------------------------------------------------------------

    public Dictionary<string, object?> Describe()
    {
        Sync();
        var lines = _model.Lines();
        var columns = new List<Dictionary<string, object?>>(_model.Columns.Count);
        foreach (var c in _model.Columns)
            columns.Add(new Dictionary<string, object?>
            {
                ["key"] = c.Key,
                ["caption"] = c.Caption,
                ["kind"] = c.Kind == UiTableColumnKind.Number ? "number" : "text",
                ["hidden"] = _model.IsHidden(c.Key),
                ["hideable"] = c.Hideable,
                ["sortable"] = c.Sortable,
                ["editable"] = IsEditable(c),
                ["subtotal"] = c.Subtotal.ToString().ToLowerInvariant(),
            });
        var sort = new List<Dictionary<string, object?>>();
        foreach (var k in _model.Sort)
            sort.Add(new Dictionary<string, object?> { ["column"] = k.Column, ["ascending"] = k.Ascending });
        var slots = new List<Dictionary<string, object?>>(_viewport.Shown);
        for (int i = 0; i < _viewport.Shown; i++)
        {
            var line = lines[_viewport.Top + i];
            slots.Add(new Dictionary<string, object?>
            {
                ["slot"] = i,
                ["row"] = RowKey("row" + i),
                ["kind"] = line.Kind switch
                {
                    UiTableLineKind.Detail => "detail",
                    UiTableLineKind.GroupHeader => "group",
                    UiTableLineKind.Subtotal => "subtotal",
                    UiTableLineKind.Total => "total",
                    _ => "row",
                },
                ["id"] = line.Row?.Id,
                ["group"] = line.Group,
            });
        }

        var pending = new List<Dictionary<string, object?>>();
        foreach (var e in _model.Pending)
            pending.Add(new Dictionary<string, object?>
            {
                ["row"] = e.RowId,
                ["column"] = e.Column,
                ["old"] = e.OldValue,
                ["new"] = e.NewValue,
                ["clear"] = e.Clear,
                ["state"] = e.State.ToString().ToLowerInvariant(),
            });
        var collapsed = new List<string>();
        foreach (var g in _model.Groups) if (_model.IsCollapsed(g)) collapsed.Add(g);
        return new Dictionary<string, object?>
        {
            ["kind"] = "datatable",
            ["key"] = Key,
            ["columns"] = columns,
            ["sort"] = sort,
            ["sortMarks"] = HeaderMarks(),
            ["hidden"] = _model.HiddenInOrder(),
            ["grouped"] = _model.Grouped,
            ["groups"] = _model.Grouped ? new List<string>(_model.Groups) : new List<string>(),
            ["collapsed"] = collapsed,
            ["top"] = _viewport.Top,
            ["lines"] = lines.Count,
            ["rows"] = _model.Rows.Count,
            ["visibleRows"] = _viewport.Slots,
            ["slots"] = slots,
            ["selected"] = new List<string>(_model.Selected),
            ["expanded"] = new List<string>(_model.Expanded),
            ["pending"] = pending,
            ["drafts"] = _drafts.Count,
            ["status"] = StatusText,
            ["reason"] = _model.Reason,
            ["chooserOpen"] = _chooserOpen,
            ["scrollBar"] = _bar != null,
            ["rowKeys"] = RowKey("row") + "<n>",
        };
    }

    // ---- Drawing -------------------------------------------------------------------------------------

    protected override void Render()
    {
        if (IsDisposed || !Visible || _rendering) return;
        _rendering = true;
        List<Draft>? moved = null;
        try
        {
            var lines = _model.Lines();
            _viewport.SetCount(lines.Count);
            // A draft whose row left its slot (a scroll, a re-sort, a refresh that moved it) commits: the field is about to
            // show another row.
            foreach (var d in _drafts.Values)
            {
                int index = _viewport.LineAt(d.Slot);
                bool there = index >= 0 && lines[index].Kind == UiTableLineKind.Row
                    && string.Equals(lines[index].Row!.Id, d.RowId, StringComparison.Ordinal);
                if (!there) (moved ??= new List<Draft>()).Add(d);
            }

            if (moved != null)
                foreach (var d in moved) _drafts.Remove(UiDataTableModel.EditKey(d.RowId, d.Column));

            // A value box that has the keyboard while its row leaves the slot (a scroll, a sort, a refresh, grouping,
            // folding) gives the keyboard up before the slot is rewritten, so the next key can never edit the row that
            // moves in. Its draft (above) commits, as leaving the box does.
            ReleaseMovedFields(lines);

            // The row tooltip describes what was under the pointer; any change of the window or the body hides it.
            if (!ReferenceEquals(lines, _renderedLines) || _viewport.Top != _renderedTop) HideTooltip();
            _renderedLines = lines;
            _renderedTop = _viewport.Top;

            bool icons = AnyIcon();
            bool expander = _model.Grouped || AnyDetail();
            RenderHeader(icons, expander);
            for (int i = 0; i < _slots.Count; i++)
            {
                int index = _viewport.LineAt(i);
                if (index < 0)
                {
                    TrySetVisible(_slots[i], false);
                    continue;
                }

                RenderSlot(i, lines[index], icons, expander);
                TrySetVisible(_slots[i], true);
            }

            RenderFooter();
            if (_bar != null && !_barDragging)
            {
                try { _bar.Set(_viewport.BarSize, _viewport.BarPosition); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: data table '" + Key + "' scroll bar: " + e.Message); }
            }
        }
        finally { _rendering = false; }

        if (moved != null)
            foreach (var d in moved) StartEdit(d.RowId, d.Column, d.Text);
        EnsureTick();
    }

    /// <summary>Release the keyboard from a focused value box in a slot whose row is about to change.</summary>
    private void ReleaseMovedFields(IReadOnlyList<UiTableLine> lines)
    {
        IWidgetCellField? field = null;
        for (int i = 0; i < _slots.Count; i++)
        {
            string? was = _slotRow[i];
            int index = _viewport.LineAt(i);
            string? now = index >= 0 && lines[index].Kind == UiTableLineKind.Row ? lines[index].Row!.Id : null;
            _slotRow[i] = now;
            if (was == null || string.Equals(was, now, StringComparison.Ordinal)) continue;
            field ??= WidgetRuntime.Current as IWidgetCellField;
            if (field == null) continue;
            var runtimeRow = RuntimeRowOf(_slots[i]);
            if (runtimeRow == null) continue;
            foreach (var col in _model.Columns)
            {
                if (!IsEditable(col)) continue;
                string cell = UiDataTableLayout.EditKey(col.Key);
                bool focused;
                try { focused = field.CellFieldFocused(runtimeRow, cell); }
                catch { focused = false; }
                if (!focused) continue;
                try { field.ReleaseCellField(runtimeRow, cell); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: data table '" + Key + "' release: " + e.Message); }
                _cache[i].Remove(cell);
                _written[i].Remove(cell);
                FieldsReleased++;
            }
        }
    }

    /// <summary>Tests: how many focused value boxes were released because their row left the slot.</summary>
    internal int FieldsReleased { get; private set; }

    private bool AnyIcon()
    {
        foreach (var r in _model.Rows) if (r.Icon != null || r.ItemPrefab != 0) return true;
        return false;
    }

    private bool AnyDetail()
    {
        foreach (var r in _model.Rows) if (UiDataTableModel.HasDetail(r)) return true;
        return false;
    }

    private void RenderHeader(bool icons, bool expander)
    {
        TrySetVisible(_header, true);
        var sort = _model.Sort;
        foreach (var col in _model.Columns)
        {
            WriteHeader(UiDataTableLayout.ViewKey(col.Key), col.Caption, !_model.IsHidden(col.Key));
            if (col.Sortable) HeaderMark(col.Key, UiDataTableLayout.SortMark(sort, col.Key));
        }

        WriteHeader(UiDataTableLayout.CellIcon, "", icons);
        WriteHeader(UiDataTableLayout.CellExpand, "", expander);
    }

    /// <summary>
    /// The drawn sort mark after a sortable caption (one key: the arrow; two or more: the arrow and the key's order digit),
    /// in the header's sorted gold. The header's own font indicator is never used, so one key and several look alike.
    /// </summary>
    private void HeaderMark(string column, UiDrawnMark mark)
    {
        if (_headerMarkNow.TryGetValue(column, out var was) && was.Equals(mark)) return;
        _headerMarkNow[column] = mark;
        if (!_headerMarks.TryGetValue(column, out var view))
        {
            view = AttachMark(_header, UiDataTableLayout.ViewKey(column), UiMarkSide.After);
            _headerMarks[column] = view;
        }

        try { view?.Set(mark, MenuPalette.HeaderSorted); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: data table '" + Key + "' sort mark: " + e.Message); }
    }

    /// <summary>The waiting spinner before a pending value (slot <paramref name="slot"/>, column view cell <paramref name="cell"/>).</summary>
    private void SpinnerMark(int slot, string cell, UiDrawnMark mark)
    {
        var now = _spinnerNow[slot];
        if (now.TryGetValue(cell, out var was) && was.Equals(mark)) return;
        if (!now.ContainsKey(cell) && mark.IsNone) return;
        now[cell] = mark;
        var views = _spinners[slot];
        if (!views.TryGetValue(cell, out var view))
        {
            view = AttachMark(_slots[slot], cell, UiMarkSide.Before);
            views[cell] = view;
        }

        try { view?.Set(mark, MenuPalette.TextColor(CellTone.Pending)); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: data table '" + Key + "' spinner: " + e.Message); }
    }

    private IUiMarkView? AttachMark(IUiCellRow row, string cell, UiMarkSide side)
    {
        var runtimeRow = RuntimeRowOf(row);
        if (runtimeRow == null || WidgetRuntime.Current is not IWidgetMarks marks) return null;
        try
        {
            var view = marks.AttachMark(runtimeRow, cell, side);
            if (view != null) Lease(view);
            return view;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: data table '" + Key + "' mark: " + e.Message);
            return null;
        }
    }

    /// <summary>Readback and tests: the sort mark drawn after each sortable caption (its name; empty for none).</summary>
    internal Dictionary<string, string> HeaderMarks()
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in _headerMarkNow) d[p.Key] = p.Value.Name;
        return d;
    }

    /// <summary>Tests: the spinner mark a slot's view cell shows (its name; empty for none).</summary>
    internal string SpinnerAt(int slot, string column) =>
        _spinnerNow[slot].TryGetValue(UiDataTableLayout.ViewKey(column), out var m) ? m.Name : "";

    private void WriteHeader(string key, string text, bool visible)
    {
        if (_headerCache.TryGetValue(key, out var was) && was.Text == text && was.Visible == visible) return;
        _header.SetCell(key, text, visible: visible);
        _headerCache[key] = new CellState(text, CellTone.Muted, visible);
    }

    /// <summary>Rewrite one cell of a slot when its text, tone or visibility changed; a hidden cell keeps its text.</summary>
    private void Write(int slot, string key, string? text, CellTone tone, bool visible)
    {
        var cache = _cache[slot];
        bool known = cache.TryGetValue(key, out var was);
        if (!visible)
        {
            if (known && !was.Visible) return;
            _slots[slot].SetCell(key, visible: false);
            cache[key] = new CellState(known ? was.Text : "", known ? was.Tone : tone, false);
            return;
        }

        string t = text ?? "";
        if (known && was.Visible && was.Text == t && was.Tone == tone) return;
        if (key.StartsWith("e:", StringComparison.Ordinal)) _written[slot][key] = t; // the field's echo is ours
        _slots[slot].SetCell(key, t, tone, true);
        cache[key] = new CellState(t, tone, true);
    }

    private void Hint(int slot, string key, string hint)
    {
        if (_hints[slot].TryGetValue(key, out var was) && was == hint) return;
        _hints[slot][key] = hint;
        var row = _slots[slot];
        foreach (var spec in row.Cells)
            if (string.Equals(spec.Key, key, StringComparison.Ordinal)) spec.Placeholder = hint;
        var runtimeRow = RuntimeRowOf(row);
        if (runtimeRow != null && WidgetRuntime.Current is IWidgetCellField field)
        {
            try { field.SetCellHint(runtimeRow, key, hint); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: data table '" + Key + "' hint: " + e.Message); }
        }
    }

    private void RenderSlot(int slot, UiTableLine line, bool icons, bool expander)
    {
        var row = _slots[slot];
        var kind = line.Kind;
        var data = line.Row;
        bool isRow = kind == UiTableLineKind.Row;
        bool selected = isRow && _model.IsSelected(data!.Id);

        if (_multi) Write(slot, UiDataTableLayout.CellCheck, isRow && selected ? _check : "", CellTone.Gold, true);

        Write(slot, UiDataTableLayout.CellIcon, "", CellTone.Normal, icons && isRow);
        Write(slot, UiDataTableLayout.CellIconPad, "", CellTone.Muted, icons && !isRow);
        if (icons && isRow)
        {
            object? icon = data!.Icon;
            if (icon == null && data.ItemPrefab != 0)
            {
                try { icon = WidgetRuntime.Current?.ItemIcon(data.ItemPrefab); }
                catch { icon = null; }
            }

            if (!_imageSet[slot] || !ReferenceEquals(icon, _images[slot]))
            {
                row.SetImage(UiDataTableLayout.CellIcon, icon);
                _images[slot] = icon;
                _imageSet[slot] = true;
            }
        }

        if (kind == UiTableLineKind.Detail)
        {
            foreach (var col in _model.Columns)
            {
                Write(slot, UiDataTableLayout.ViewKey(col.Key), null, CellTone.Normal, false);
                if (!IsEditable(col)) continue;
                Write(slot, UiDataTableLayout.EditKey(col.Key), null, CellTone.Normal, false);
                SpinnerMark(slot, UiDataTableLayout.ViewKey(col.Key), UiDrawnMark.None);
            }

            Write(slot, UiDataTableLayout.CellDetail, line.Text, CellTone.Muted, true);
        }
        else
        {
            Write(slot, UiDataTableLayout.CellDetail, null, CellTone.Muted, false);
            string? first = FirstVisibleColumn();
            foreach (var col in _model.Columns)
                RenderColumn(slot, line, col, first);
        }

        bool hasDetail = isRow && UiDataTableModel.HasDetail(data!);
        bool arrow = expander && ((isRow && hasDetail) || kind == UiTableLineKind.GroupHeader);
        Write(slot, UiDataTableLayout.CellExpand, UiWidgetLayout.ExpandGlyph, CellTone.Normal, arrow);
        Write(slot, UiDataTableLayout.CellExpandPad, "", CellTone.Muted, expander && !arrow);

        bool open = isRow ? _model.IsExpanded(data!.Id) : kind == UiTableLineKind.GroupHeader && !_model.IsCollapsed(line.Group);
        if (_expandedNow[slot] != open)
        {
            row.SetExpanded(open);
            _expandedNow[slot] = open;
        }

        if (_selectedNow[slot] != selected)
        {
            row.SetSelected(selected);
            _selectedNow[slot] = selected;
        }
    }

    private string? FirstVisibleColumn()
    {
        foreach (var c in _model.Columns) if (!_model.IsHidden(c.Key)) return c.Key;
        return null;
    }

    private void RenderColumn(int slot, UiTableLine line, UiTableColumn col, string? first)
    {
        string view = UiDataTableLayout.ViewKey(col.Key);
        string edit = UiDataTableLayout.EditKey(col.Key);
        bool editable = IsEditable(col);
        bool spinning = editable && line.Kind == UiTableLineKind.Row && !_model.IsHidden(col.Key)
            && _model.PendingAt(line.Row!.Id, col.Key) != null;
        if (editable && !spinning) SpinnerMark(slot, view, UiDrawnMark.None);
        if (_model.IsHidden(col.Key))
        {
            Write(slot, view, null, CellTone.Normal, false);
            if (editable) Write(slot, edit, null, CellTone.Normal, false);
            return;
        }

        bool isFirst = string.Equals(col.Key, first, StringComparison.Ordinal);
        switch (line.Kind)
        {
            case UiTableLineKind.Row:
            {
                var row = line.Row!;
                var cell = row[col.Key];
                if (editable)
                {
                    var pending = _model.PendingAt(row.Id, col.Key);
                    if (pending != null)
                    {
                        // The spinner is drawn before the value (a texture, UiDrawnMark); the words stay the value.
                        // A clear waits as the value box's own gray hint word; a number waits in the pending yellow.
                        string shown = pending.Clear ? UiDataTableLayout.InheritedHint : UiDataTableModel.FormatValue(pending.NewValue, col);
                        Write(slot, view, shown, pending.Clear ? CellTone.Muted : CellTone.Pending, true);
                        Write(slot, edit, null, CellTone.Normal, false);
                        SpinnerMark(slot, view, UiDrawnMark.Spinner(_spinnerFrame < 0 ? 0 : _spinnerFrame));
                        return;
                    }

                    if (_model.CanEdit(row, col))
                    {
                        string key = UiDataTableModel.EditKey(row.Id, col.Key);
                        string value = _drafts.TryGetValue(key, out var draft) ? draft.Text
                            : cell == null || cell.Inherited ? "" : UiDataTableModel.Display(cell, col);
                        Hint(slot, edit, cell != null && cell.Inherited ? UiDataTableModel.Display(cell, col) : " ");
                        Write(slot, view, null, CellTone.Normal, false);
                        Write(slot, edit, value, CellTone.Normal, true);
                        return;
                    }

                    Write(slot, edit, null, CellTone.Normal, false);
                }

                Write(slot, view, UiDataTableModel.Display(cell, col), ToneOf(cell), true);
                return;
            }

            case UiTableLineKind.GroupHeader:
            {
                string caption = isFirst ? line.Group + " (" + _model.RowsIn(line.Group).Count + ")" : "";
                if (editable) Write(slot, edit, null, CellTone.Normal, false);
                Write(slot, view, caption, CellTone.Gold, true);
                return;
            }

            default: // subtotal, total
            {
                bool total = line.Kind == UiTableLineKind.Total;
                string text;
                CellTone tone;
                if (isFirst)
                {
                    text = total ? "Total" : "Subtotal";
                    tone = total ? CellTone.Gold : CellTone.Muted;
                }
                else if (col.Subtotal != UiTableSubtotal.None)
                {
                    var value = _model.SubtotalOf(total ? null : line.Group, col);
                    text = UiDataTableModel.FormatValue(value, col);
                    tone = total ? CellTone.Gold : CellTone.Normal;
                }
                else
                {
                    text = "";
                    tone = CellTone.Normal;
                }

                if (editable) Write(slot, edit, null, CellTone.Normal, false);
                Write(slot, view, text, tone, true);
                return;
            }
        }
    }

    private static CellTone ToneOf(UiTableCell? cell) =>
        cell == null ? CellTone.Normal : cell.Inherited ? CellTone.Muted : LabelTones.ToCellTone(cell.Tone);

    private void RenderFooter()
    {
        int rows = _model.Rows.Count;
        string status;
        if (rows == 0) status = _options.EmptyText ?? "";
        else
        {
            int n = _model.RowLines(_viewport.Top, _viewport.Top + _viewport.Shown, out int first, out int last);
            if (first == 0)
                status = rows + (rows == 1 ? " item" : " items") + (_model.Grouped ? " in " + _model.Groups.Count + " groups" : "");
            else
            {
                status = "Showing " + first + "–" + last + " of " + n;
                if (n != rows) status += " · " + rows + " items";
            }

            int sel = _model.Selected.Count;
            if (sel > 0) status += " · " + sel + " selected";
            int waiting = _model.Pending.Count;
            if (waiting > 0) status += " · " + waiting + " waiting";
        }

        TrySetVisible(_footer, true);
        if (!string.Equals(status, StatusText, StringComparison.Ordinal) || !_footerWritten)
            _footer.SetCell(UiDataTableLayout.CellStatus, status);
        StatusText = status;
        string columns = UiDataTableLayout.ColumnsCaption(_model.Hidden.Count);
        if (_chooser && (!string.Equals(columns, _columnsCaption, StringComparison.Ordinal) || !_footerWritten))
            _footer.SetCell(UiDataTableLayout.CellColumns, columns);
        _columnsCaption = columns;
        string reason = _model.Reason;
        if (!string.Equals(reason, _reasonText, StringComparison.Ordinal) || !_footerWritten)
            _reason.SetCell(UiDataTableLayout.CellReason, reason);
        _reasonText = reason;
        _footerWritten = true;
        TrySetVisible(_reason, reason.Length > 0);
        foreach (var chipRow in _chooserRows)
        {
            TrySetVisible(chipRow, _chooserOpen);
            if (!_chooserOpen) continue;
            foreach (var chip in chipRow.Cells)
            {
                string column = chip.Key.Substring("col:".Length);
                chipRow.SetCell(chip.Key, tone: _model.IsHidden(column) ? CellTone.ChipOff : CellTone.ChipOn);
            }
        }
    }
}
