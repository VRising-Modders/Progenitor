using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7: the ordered list drawn as kit cell rows (<see cref="UiWidgets.OrderedList"/>). One item row and one drawer row per
/// slot are made once, in page order, and rewritten in place; a slot past the item count is hidden. See
/// <see cref="UiOrderedListModel"/> for the order rules and <see cref="UiWidgetLayout"/> for the geometry.
/// </summary>
internal sealed class UiOrderedListHandle : UiComposedHandle, IUiOrderedList
{
    private readonly UiOrderedListModel _model;
    private readonly UiOrderedListOptions _options;
    private readonly bool _icons;
    private readonly bool _expand;
    private readonly List<IUiCellRow> _rows = new();
    private readonly List<IUiCellRow> _drawers = new();
    private readonly IUiCellRow _footer;
    private readonly List<Action<UiListReorder>> _reorder = new();
    private readonly List<Action<UiListItem>> _remove = new();
    private readonly List<Action<KeyValuePair<UiListItem, bool>>> _expandHandlers = new();
    private IDisposable? _dragTick;
    private string _glyphGrip = UiWidgetLayout.GripGlyph;
    private string _glyphUp = UiWidgetLayout.UpGlyph;
    private string _glyphDown = UiWidgetLayout.DownGlyph;
    private string _glyphRemove = UiWidgetLayout.RemoveGlyph;

    internal UiOrderedListHandle(IUiPanel panel, string key, IEnumerable<UiListItem>? items, UiOrderedListOptions? options)
        : base(panel, key, key)
    {
        _options = options ?? new UiOrderedListOptions();
        _model = new UiOrderedListModel(items, _options.Capacity);
        _icons = AnyIcon(_model.Items);
        _expand = AnyDetail(_model.Items);
        ResolveGlyphs();

        float indent = UiWidgetLayout.DrawerIndent(_options.Reorder, _icons);
        float drawerHeight = UiWidgetLayout.DrawerHeight(_options.DetailLines);
        for (int i = 0; i < _model.Capacity; i++)
        {
            int slot = i;
            var cells = UiWidgetLayout.ListRowCells(_options.Reorder, _options.Remove, _expand, _icons);
            foreach (var c in cells) c.Text = Glyph(c);
            var row = Part(panel.CellRow(new CellRowSpec(
                RowKey("row" + i), cells, hover: true, skin: CellRowSkin.GameEntry, separator: true, quietHover: true)));
            _rows.Add(row);
            var drawer = Part(panel.CellRow(new CellRowSpec(
                RowKey("drawer" + i), UiWidgetLayout.DrawerCells(indent), height: drawerHeight, plain: true, joinAbove: true,
                indent: indent)));
            _drawers.Add(drawer);

            Lease(row.OnRowClick(() => ToggleSlot(slot)));
            Lease(row.OnHover(on => HoverSlot(slot, on)));
            if (_options.Reorder)
            {
                Lease(row.OnClick(UiWidgetLayout.CellUp, () => Step(slot, -1)));
                Lease(row.OnClick(UiWidgetLayout.CellDown, () => Step(slot, +1)));
                var runtimeRow = RuntimeRowOf(row);
                var widgets = WidgetRuntime.Current;
                if (runtimeRow != null && widgets != null)
                {
                    try { Lease(widgets.WatchCell(runtimeRow, UiWidgetLayout.CellGrip, e => Grip(slot, e), panel)); }
                    catch (Exception e) { Plugin.Logger?.LogWarning("Ui: ordered list '" + Key + "' grip watch: " + e.Message); }
                }
            }

            if (_expand) Lease(row.OnClick(UiWidgetLayout.CellExpand, () => ToggleSlot(slot)));
            if (_options.Remove) Lease(row.OnClick(UiWidgetLayout.CellRemove, () => RemoveSlot(slot)));
        }

        _footer = Part(panel.CellRow(new CellRowSpec(RowKey("footer"),
            new List<CellSpec> { new CellSpec(CellKind.Muted, "text").With(width: CellWidth.Fill(UiWidgetLayout.CaptionMinWidth)) },
            plain: true)));
        Render();
    }

    private static bool AnyIcon(IReadOnlyList<UiListItem> items)
    {
        foreach (var i in items) if (i.Icon != null || i.ItemPrefab != 0) return true;
        return false;
    }

    private static bool AnyDetail(IReadOnlyList<UiListItem> items)
    {
        foreach (var i in items) if (UiOrderedListModel.HasDetail(i)) return true;
        return false;
    }

    private void ResolveGlyphs()
    {
        var runtime = WidgetRuntime.Current;
        if (runtime == null) return;
        try
        {
            _glyphGrip = runtime.DrawableGlyph(UiWidgetLayout.GripGlyph, UiWidgetLayout.GripFallback);
            _glyphUp = runtime.DrawableGlyph(UiWidgetLayout.UpGlyph, UiWidgetLayout.UpFallback);
            _glyphDown = runtime.DrawableGlyph(UiWidgetLayout.DownGlyph, UiWidgetLayout.DownFallback);
            _glyphRemove = runtime.DrawableGlyph(UiWidgetLayout.RemoveGlyph, UiWidgetLayout.RemoveFallback);
        }
        catch { /* the preferred glyphs stay */ }
    }

    private string Glyph(CellSpec cell) => cell.Key switch
    {
        UiWidgetLayout.CellGrip => _glyphGrip,
        UiWidgetLayout.CellUp => _glyphUp,
        UiWidgetLayout.CellDown => _glyphDown,
        UiWidgetLayout.CellRemove => _glyphRemove,
        _ => cell.Text,
    };

    // ---- IUiOrderedList --------------------------------------------------------------------

    public IReadOnlyList<UiListItem> Items => _model.Items;

    public int Capacity => _model.Capacity;

    public int Overflow => _model.Overflow;

    public void SetItems(IEnumerable<UiListItem> items)
    {
        if (IsDisposed) return;
        StopDrag();
        _model.SetItems(items);
        Render();
    }

    public bool Move(string id, int index)
    {
        if (IsDisposed) return false;
        StopDrag();
        if (!_model.Move(_model.IndexOf(id), index, out var change)) return false;
        Render();
        Raise(_reorder, change, "reorder");
        return true;
    }

    public bool Remove(string id)
    {
        if (IsDisposed) return false;
        StopDrag();
        if (!_model.Remove(id, out var removed, out _)) return false;
        Render();
        Raise(_remove, removed!, "remove");
        return true;
    }

    public bool SetExpanded(string id, bool expanded)
    {
        if (IsDisposed || !_model.SetExpanded(id, expanded)) return false;
        Render();
        var item = _model.Find(id);
        if (item != null) Raise(_expandHandlers, new KeyValuePair<UiListItem, bool>(item, expanded), "expand");
        return true;
    }

    public bool IsExpanded(string id) => _model.IsExpanded(id);

    public IDisposable OnReorder(Action<UiListReorder> handler) => Subscribe(_reorder, handler);

    public IDisposable OnRemove(Action<UiListItem> handler) => Subscribe(_remove, handler);

    public IDisposable OnExpand(Action<UiListItem, bool> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        return Subscribe(_expandHandlers, (Action<KeyValuePair<UiListItem, bool>>)(p => handler(p.Key, p.Value)));
    }

    public Dictionary<string, object?> Describe()
    {
        var items = new List<Dictionary<string, object?>>(_model.Count);
        foreach (var item in _model.Items)
            items.Add(new Dictionary<string, object?>
            {
                ["id"] = item.Id,
                ["title"] = item.Title,
                ["expanded"] = _model.IsExpanded(item.Id),
            });
        return new Dictionary<string, object?>
        {
            ["kind"] = "orderedlist",
            ["key"] = Key,
            ["items"] = items,
            ["capacity"] = _model.Capacity,
            ["overflow"] = _model.Overflow,
            ["drag"] = _model.Dragging
                ? new Dictionary<string, object?> { ["from"] = _model.DragFrom, ["target"] = _model.DragTarget }
                : null,
            ["rowKeys"] = RowKey("row") + "<n>",
        };
    }

    // ---- Input -------------------------------------------------------------------------------

    private UiListItem? ItemAt(int slot)
    {
        var display = _model.Display();
        return slot >= 0 && slot < display.Count && slot < _model.Capacity ? display[slot] : null;
    }

    /// <summary>The up (-1) or down (+1) button of slot <paramref name="slot"/>.</summary>
    internal void Step(int slot, int delta)
    {
        if (!Live || _model.Dragging) return;
        var item = ItemAt(slot);
        if (item == null) return;
        Move(item.Id, slot + delta);
    }

    internal void ToggleSlot(int slot)
    {
        if (!Live || _model.Dragging) return;
        var item = ItemAt(slot);
        if (item == null || !UiOrderedListModel.HasDetail(item)) return;
        SetExpanded(item.Id, !_model.IsExpanded(item.Id));
    }

    internal void RemoveSlot(int slot)
    {
        if (!Live || _model.Dragging) return;
        var item = ItemAt(slot);
        if (item == null || !item.Removable) return;
        Remove(item.Id);
    }

    private void HoverSlot(int slot, bool on)
    {
        if (!on || _model.Dragging)
        {
            HideTooltip();
            return;
        }

        var item = ItemAt(slot);
        if (item == null || string.IsNullOrWhiteSpace(item.Tooltip)) return;
        ShowTooltip(RectOf(_rows[slot]), item.Title, item.Tooltip);
    }

    /// <summary>A pointer event on slot <paramref name="slot"/>'s grip: a primary press starts a drag, the release drops it.</summary>
    internal void Grip(int slot, UiPointerEvent e)
    {
        if (e.Kind == UiPointerEventKind.Down)
        {
            if (!Live || !e.PrimaryHeld || !_model.BeginDrag(slot)) return;
            HideTooltip();
            var runtime = WidgetRuntime.Current;
            try { _dragTick = runtime?.EveryFrame(DragTick); }
            catch (Exception ex) { Plugin.Logger?.LogWarning("Ui: ordered list '" + Key + "' drag tick: " + ex.Message); }
            Render();
        }
        else if (e.Kind == UiPointerEventKind.Up)
        {
            Drop();
        }
    }

    /// <summary>One frame of a drag: the slot under the pointer becomes the target; a missed release drops.</summary>
    internal void DragTick()
    {
        if (!_model.Dragging) return;
        var runtime = WidgetRuntime.Current;
        if (runtime == null || !Live)
        {
            StopDrag();
            Render();
            return;
        }

        bool held;
        try { held = runtime.PrimaryHeld(); }
        catch { held = false; }
        if (!held)
        {
            Drop();
            return;
        }

        PointerSample pointer;
        try { pointer = runtime.ReadPointer(); }
        catch { return; }
        if (pointer.Kind != PointerKind.Inside) return;
        int target = UiOrderedListModel.DropIndex(pointer.Y, SlotCenters());
        if (_model.DragTo(target)) Render();
    }

    private List<float> SlotCenters()
    {
        var centers = new List<float>(_model.Drawn);
        for (int i = 0; i < _model.Drawn; i++)
        {
            var r = RectOf(_rows[i]);
            centers.Add(r.IsKnown ? (r.MinY + r.MaxY) / 2f : float.NaN);
        }

        return centers;
    }

    private void Drop()
    {
        if (!_model.Dragging) return;
        DisposeTick();
        bool moved = _model.EndDrag(out var change);
        Render();
        if (moved) Raise(_reorder, change, "reorder");
    }

    private void StopDrag()
    {
        DisposeTick();
        _model.CancelDrag();
    }

    private void DisposeTick()
    {
        var tick = _dragTick;
        _dragTick = null;
        try { tick?.Dispose(); }
        catch { /* */ }
    }

    protected override void OnHidden()
    {
        base.OnHidden();
        StopDrag();
    }

    protected override void OnDisposing() => StopDrag();

    // ---- Drawing -------------------------------------------------------------------------------

    protected override void Render()
    {
        if (IsDisposed || !Visible) return;
        var display = _model.Display();
        int drawn = _model.Drawn;
        var runtime = WidgetRuntime.Current;
        for (int i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var drawer = _drawers[i];
            if (i >= drawn)
            {
                TrySetVisible(drawer, false);
                TrySetVisible(row, false);
                continue;
            }

            var item = display[i];
            int realIndex = _model.Dragging ? -1 : i;
            row.SetCell(UiWidgetLayout.CellTitle, item.Title);
            row.SetCell(UiWidgetLayout.CellNote, item.Note, visible: item.Note.Length > 0);
            row.SetCell(UiWidgetLayout.CellBadge, item.Badge, visible: item.Badge.Length > 0);
            if (_options.Reorder)
            {
                bool still = _model.Dragging;
                row.SetCell(UiWidgetLayout.CellUp, tone: !still && _model.CanMoveUp(realIndex) ? CellTone.Normal : CellTone.Faint);
                row.SetCell(UiWidgetLayout.CellDown, tone: !still && _model.CanMoveDown(realIndex) ? CellTone.Normal : CellTone.Faint);
            }

            bool hasDetail = UiOrderedListModel.HasDetail(item);
            bool open = hasDetail && _model.IsExpanded(item.Id) && !_model.Dragging;
            if (_expand) row.SetCell(UiWidgetLayout.CellExpand, tone: hasDetail ? CellTone.Normal : CellTone.Faint);
            if (_options.Remove) row.SetCell(UiWidgetLayout.CellRemove, tone: item.Removable && !_model.Dragging ? CellTone.Normal : CellTone.Faint);
            if (_icons)
            {
                object? icon = item.Icon;
                if (icon == null && item.ItemPrefab != 0 && runtime != null)
                {
                    try { icon = runtime.ItemIcon(item.ItemPrefab); }
                    catch { icon = null; }
                }

                row.SetImage(UiWidgetLayout.CellIcon, icon);
            }

            row.SetExpanded(open);
            row.SetSelected(_model.Dragging && i == _model.DragTarget);
            TrySetVisible(row, true);
            drawer.SetCell(UiWidgetLayout.CellDetail, open ? item.Detail : "");
            TrySetVisible(drawer, open);
        }

        string footer = _model.Count == 0 ? _options.EmptyText
            : _model.Overflow > 0 ? _model.Overflow + (_model.Overflow == 1 ? " more item is not shown." : " more items are not shown.")
            : "";
        _footer.SetCell("text", footer);
        TrySetVisible(_footer, footer.Length > 0);
    }
}
