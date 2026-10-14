using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7b: the master-detail widget drawn as kit cell rows (<see cref="UiWidgets.MasterDetail"/>): an optional tab row (the
/// merged <see cref="UiTabsHandle"/>), then <see cref="UiMasterDetailModel.Rows"/> rows made once and rewritten in place.
/// Each row carries a master line on its left, the divider, and a detail line on its right
/// (<see cref="UiMasterDetailLayout"/>). The member list's order, drawers and drag are the ordered list's own model
/// (<see cref="UiOrderedListModel"/>) and drag step (<see cref="UiOrderedDrag"/>).
///
/// <para><b>Input.</b> A master entry is its row's own click (the controller's A on the row, NAV1); a click that lands on
/// the detail half of a row is not a master click (the pointer is checked against the divider). Every detail action is a
/// button cell (a link, a line's buttons, the member arrows, expander and remove), so the controller's left and right move
/// between the master entry and the detail buttons on a row. The wheel over either half scrolls that half
/// (<see cref="IWidgetRuntime.ClaimWheel"/>), and up or down on the first or last row scrolls the half the focus is in
/// (<see cref="UiNav.Steer"/>). No key is read.</para>
/// </summary>
internal sealed class UiMasterDetailHandle : UiComposedHandle, IUiMasterDetail
{
    private readonly UiMasterDetailOptions _options;
    private readonly UiMasterDetailModel _model;
    private readonly float _fraction;
    private readonly UiTabsHandle? _tabs;
    private readonly List<IUiCellRow> _rows = new();
    private readonly UiMasterLine?[] _masterShown;
    private readonly UiDetailSlot?[] _detailShown;
    private readonly List<Action<string>> _selected = new();
    private readonly List<Action<UiDetailAction>> _action = new();
    private readonly List<Action<UiListReorder>> _reorder = new();
    private readonly List<Action<UiListItem>> _remove = new();
    private readonly List<Action<string>> _search = new();
    private readonly List<int> _centerMembers = new();
    private IWidgetScrollBar? _scrollBar;
    private IDisposable? _dragTick;
    private float _stick;
    private int _hoverRow = -1;
    /// <summary>Where the pointer is on the hovered row: 0 nowhere, 1 the master half, 2 the detail half.</summary>
    private int _hoverSide;

    /// <summary>P7b terms: per row, the binding on its master caption and on its detail heading (rows are reused slots).</summary>
    private readonly UiTermBinding?[] _masterTerms;
    private readonly UiTermBinding?[] _detailTerms;
    private IDisposable? _hoverTick;
    private string _grip = UiWidgetLayout.GripGlyph;
    private string _up = UiWidgetLayout.UpGlyph;
    private string _down = UiWidgetLayout.DownGlyph;
    private string _removeGlyph = UiWidgetLayout.RemoveGlyph;

    internal UiMasterDetailHandle(IUiPanel panel, string key, UiMasterDetailOptions? options)
        : base(panel, key, key)
    {
        _options = options ?? new UiMasterDetailOptions();
        var captions = _options.Tabs != null && _options.Tabs.Count > 0 ? _options.Tabs : null;
        _model = new UiMasterDetailModel(captions?.Count ?? 1, _options.Rows, _options.Search, _options);
        _fraction = UiMasterDetailLayout.FractionFor(_options.MasterFraction);
        _masterShown = new UiMasterLine?[_model.Rows];
        _detailShown = new UiDetailSlot?[_model.Rows];
        _masterTerms = new UiTermBinding?[_model.Rows];
        _detailTerms = new UiTermBinding?[_model.Rows];
        ResolveGlyphs();

        if (captions != null)
        {
            _tabs = Part(new UiTabsHandle(panel, RowKey("tabs"), captions, 0));
            Lease(_tabs.OnChanged(OnTab));
        }

        var runtime = WidgetRuntime.Current;
        for (int i = 0; i < _model.Rows; i++)
        {
            int r = i;
            bool searchRow = _model.Search && r == 0;
            var cells = UiMasterDetailLayout.RowCells(_fraction, searchRow, _options.SearchPlaceholder);
            foreach (var c in cells) c.Text = GlyphFor(c);
            // Plain rows (no whole-row tint): the master half's band and the divider's rule are tiles the widget sets
            // (CellRowSpec.TileStates), so only the left half lights. The first row starts the rule (no reach above it).
            var spec = new CellRowSpec(RowKey("row" + r), cells, plain: true, hover: true, separator: true)
            {
                TileStates = true,
                TileEdge = r == 0 ? CellSectionEdge.First : CellSectionEdge.Middle,
            };
            var row = Part(panel.CellRow(spec));
            _rows.Add(row);
            Tile(row, UiMasterDetailLayout.DividerTile, UiTileState.Rule);
            if (searchRow) Lease(row.OnChanged(UiMasterDetailLayout.CellMSearch, Typed));
            else Lease(row.OnRowClick(() => ClickMaster(r)));
            Lease(row.OnHover(on => HoverRow(r, on))); // the search row too: its detail heading can explain a term

            Lease(row.OnClick(UiMasterDetailLayout.CellLink, () => ClickLink(r)));
            for (int b = 0; b < UiDetailLine.MaxButtons; b++)
            {
                int button = b;
                Lease(row.OnClick(UiMasterDetailLayout.CellButton(b), () => ClickButton(r, button)));
            }

            Lease(row.OnClick(UiMasterDetailLayout.CellUp, () => StepMember(r, -1)));
            Lease(row.OnClick(UiMasterDetailLayout.CellDown, () => StepMember(r, +1)));
            Lease(row.OnClick(UiMasterDetailLayout.CellExpand, () => ToggleMember(r)));
            Lease(row.OnClick(UiMasterDetailLayout.CellRemove, () => RemoveSlot(r)));
            var runtimeRow = RuntimeRowOf(row);
            if (runtimeRow != null && runtime != null)
            {
                try { Lease(runtime.WatchCell(runtimeRow, UiMasterDetailLayout.CellGrip, e => Grip(r, e), panel)); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: master-detail '" + Key + "' grip watch: " + e.Message); }
            }

            UiNav.Steer(row, command => Steer(r, command));
            UiNav.StickScroll(row, amount => Stick(r, amount));
        }

        if (runtime != null)
        {
            try
            {
                Lease(runtime.ClaimWheel(MasterArea, n => Wheel(master: true, n)));
                Lease(runtime.ClaimWheel(DetailArea, n => Wheel(master: false, n)));
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: master-detail '" + Key + "' wheel claim: " + e.Message); }

            var top = RuntimeRowOf(_rows[0]);
            var bottom = RuntimeRowOf(_rows[_rows.Count - 1]);
            if (top != null && bottom != null)
            {
                try
                {
                    _scrollBar = runtime.AttachScrollBar(top, bottom, panel);
                    if (_scrollBar != null)
                    {
                        Lease(_scrollBar);
                        _scrollBar.OnDragged = p => { if (Live && _model.ScrollDetailTo(p)) Render(); };
                    }
                }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: master-detail '" + Key + "' scroll bar: " + e.Message); }
            }
        }

        Render();
    }

    private void ResolveGlyphs()
    {
        var runtime = WidgetRuntime.Current;
        if (runtime == null) return;
        try
        {
            _grip = runtime.DrawableGlyph(UiWidgetLayout.GripGlyph, UiWidgetLayout.GripFallback);
            _up = runtime.DrawableGlyph(UiWidgetLayout.UpGlyph, UiWidgetLayout.UpFallback);
            _down = runtime.DrawableGlyph(UiWidgetLayout.DownGlyph, UiWidgetLayout.DownFallback);
            _removeGlyph = runtime.DrawableGlyph(UiWidgetLayout.RemoveGlyph, UiWidgetLayout.RemoveFallback);
        }
        catch { /* the preferred glyphs stay */ }
    }

    private string GlyphFor(CellSpec cell) => cell.Key switch
    {
        UiMasterDetailLayout.CellGrip => _grip,
        UiMasterDetailLayout.CellUp => _up,
        UiMasterDetailLayout.CellDown => _down,
        UiMasterDetailLayout.CellRemove => _removeGlyph,
        _ => cell.Text,
    };

    // ---- IUiMasterDetail -----------------------------------------------------------------------

    public IUiTabs? Tabs => _tabs;

    public int Tab => _model.Tab;

    public IReadOnlyList<UiMasterEntry> Entries => _model.Page.Entries;

    public IReadOnlyList<UiMasterEntry> Matches => _model.Matches(_model.Page);

    public void SetEntries(IEnumerable<UiMasterEntry> entries) => SetEntries(_model.Tab, entries);

    public void SetEntries(int tab, IEnumerable<UiMasterEntry> entries)
    {
        if (IsDisposed) return;
        _model.SetEntries(tab, entries);
        if (_model.ClampTab(tab) == _model.Tab) Render();
    }

    public string? SelectedId => _model.Page.SelectedId;

    public bool Select(string? id)
    {
        if (IsDisposed) return false;
        HideTooltip();
        if (!_model.Select(id)) return false;
        Render();
        Raise(_selected, id ?? "", "selected");
        return true;
    }

    public string Query
    {
        get => _model.Page.Query;
        set
        {
            if (IsDisposed || !_model.SetQuery(value)) return;
            Render();
            Raise(_search, _model.Page.Query, "search");
        }
    }

    public IReadOnlyList<UiDetailLine> Detail => _model.Page.Detail;

    public void SetDetail(IEnumerable<UiDetailLine> lines) => SetDetail(_model.Tab, lines);

    public void SetDetail(int tab, IEnumerable<UiDetailLine> lines)
    {
        if (IsDisposed) return;
        bool current = _model.ClampTab(tab) == _model.Tab;
        if (current) StopDrag();
        _model.SetDetail(tab, lines);
        if (current) Render();
    }

    public void SetTexts(int tab, string? newCaption = null, string? emptyText = null, string? detailEmptyText = null)
    {
        var page = _model.PageAt(tab);
        if (newCaption != null) page.NewCaption = newCaption;
        if (emptyText != null) page.EmptyText = emptyText;
        if (detailEmptyText != null) page.DetailEmptyText = detailEmptyText;
        if (_model.ClampTab(tab) == _model.Tab) Render();
    }

    public IReadOnlyList<UiListItem> Members => _model.Page.Members.Items;

    public bool MoveMember(string id, int index)
    {
        if (IsDisposed) return false;
        StopDrag();
        var members = _model.Page.Members;
        if (!members.Move(members.IndexOf(id), index, out var change)) return false;
        Render();
        Raise(_reorder, change, "reorder");
        return true;
    }

    public bool RemoveMember(string id)
    {
        if (IsDisposed) return false;
        StopDrag();
        if (!_model.Page.Members.Remove(id, out var removed, out _)) return false;
        Render();
        Raise(_remove, removed!, "remove");
        return true;
    }

    public bool SetMemberExpanded(string id, bool expanded)
    {
        if (IsDisposed || _model.Page.Members.Dragging || !_model.Page.Members.SetExpanded(id, expanded)) return false;
        Render();
        return true;
    }

    public int Rows => _model.Rows;

    public int MasterTop => _model.Page.MasterTop;

    public int DetailTop => _model.Page.DetailTop;

    public bool ScrollMaster(int lines)
    {
        if (IsDisposed || !_model.ScrollMaster(lines)) return false;
        HideTooltip();
        Render();
        return true;
    }

    public bool ScrollDetail(int lines)
    {
        if (IsDisposed || _model.Page.Members.Dragging || !_model.ScrollDetail(lines)) return false;
        Render();
        return true;
    }

    public IDisposable OnSelected(Action<string> handler) => Subscribe(_selected, handler);

    public IDisposable OnAction(Action<UiDetailAction> handler) => Subscribe(_action, handler);

    public IDisposable OnReorder(Action<UiListReorder> handler) => Subscribe(_reorder, handler);

    public IDisposable OnRemove(Action<UiListItem> handler) => Subscribe(_remove, handler);

    public IDisposable OnSearch(Action<string> handler) => Subscribe(_search, handler);

    public Dictionary<string, object?> Describe()
    {
        var page = _model.Page;
        var master = new List<Dictionary<string, object?>>();
        foreach (var l in _model.MasterLines(page))
            master.Add(new Dictionary<string, object?>
            {
                ["kind"] = l.Kind.ToString().ToLowerInvariant(),
                ["id"] = l.Entry?.Id,
                ["text"] = l.Text,
                ["selected"] = l.Entry != null && string.Equals(l.Entry.Id, page.SelectedId, StringComparison.Ordinal),
            });
        var detail = new List<Dictionary<string, object?>>();
        foreach (var s in _model.DetailLines(page))
            detail.Add(new Dictionary<string, object?>
            {
                ["kind"] = s.Kind == UiDetailSlotKind.Line ? s.Line!.Kind.ToString().ToLowerInvariant() : s.Kind.ToString().ToLowerInvariant(),
                ["id"] = s.Member?.Id ?? s.Line?.Id,
                ["text"] = s.Text,
            });
        var members = new List<string>(page.Members.Order());
        return new Dictionary<string, object?>
        {
            ["kind"] = "masterdetail",
            ["key"] = Key,
            ["tab"] = _model.Tab,
            ["selected"] = page.SelectedId,
            ["query"] = page.Query,
            ["rows"] = _model.Rows,
            ["masterTop"] = page.MasterTop,
            ["detailTop"] = page.DetailTop,
            ["master"] = master,
            ["detail"] = detail,
            ["members"] = members,
            ["drag"] = page.Members.Dragging
                ? new Dictionary<string, object?> { ["from"] = page.Members.DragFrom, ["target"] = page.Members.DragTarget }
                : null,
            ["rowKeys"] = RowKey("row") + "<n>",
        };
    }

    // ---- Input ---------------------------------------------------------------------------------

    /// <summary>Tests and the bridge: the row handle <paramref name="r"/>.</summary>
    internal IUiCellRow RowAt(int r) => _rows[r];

    private void OnTab(int tab)
    {
        StopDrag();
        HideTooltip();
        if (_model.SetTab(tab)) Render();
    }

    private void Typed(string? text)
    {
        if (IsDisposed || !_model.SetQuery(text)) return;
        HideTooltip();
        Render();
        Raise(_search, _model.Page.Query, "search");
    }

    /// <summary>A click on row <paramref name="r"/> outside its buttons: the master line there.</summary>
    internal void ClickMaster(int r)
    {
        if (!Live || _model.Page.Members.Dragging || r < 0 || r >= _masterShown.Length) return;
        if (_masterShown[r] is not { } line || PointerOnDetail(r)) return;
        switch (line.Kind)
        {
            case UiMasterLineKind.Entry:
                Select(line.Entry!.Id);
                break;
            case UiMasterLineKind.New:
                RaiseAction(UiDetailAction.MasterLine, UiDetailAction.New);
                break;
        }
    }

    /// <summary>
    /// The row click is the whole row's; a pointer click on the detail half (right of the divider, on this row) is not a
    /// master click. A controller press on the row (the focus marker on the row itself) always is.
    /// </summary>
    private bool PointerOnDetail(int r)
    {
        try
        {
            var focus = UiNav.Focus;
            if (UiNav.Marked && !focus.IsEmpty && ReferenceEquals(focus.Handle, _rows[r]) && focus.Part.Length == 0) return false;
        }
        catch { /* no navigation state: judge by the pointer */ }

        var runtime = WidgetRuntime.Current;
        if (runtime == null) return false;
        PointerSample p;
        try { p = runtime.ReadPointer(); }
        catch { return false; }
        if (p.Kind != PointerKind.Inside) return false;
        var row = RectOf(_rows[r]);
        var divider = RectOf(_rows[r], UiMasterDetailLayout.CellDivider);
        if (!row.IsKnown || !divider.IsKnown) return false;
        return p.Y >= row.MinY && p.Y <= row.MaxY && p.X >= divider.MinX;
    }

    /// <summary>
    /// The pointer entered (<paramref name="on"/>) or left row <paramref name="r"/>. While it is over the row a frame tick
    /// follows which half it is on: over the master half the entry gets the hover band and its tooltip; over the detail
    /// half neither.
    /// </summary>
    private void HoverRow(int r, bool on)
    {
        if (on)
        {
            int was = _hoverRow;
            _hoverRow = r;
            _hoverSide = 0;
            if (was >= 0 && was != r)
            {
                LeaveTerms(was);
                DrawMasterState(was); // an exit the old row did not report
            }
            if (_hoverTick == null)
            {
                try { _hoverTick = WidgetRuntime.Current?.EveryFrame(HoverTick); }
                catch { _hoverTick = null; }
            }

            HoverTick();
            return;
        }

        if (_hoverRow != r) return;
        StopHover();
    }

    /// <summary>Tests: the row hover the kit row's pointer slot delivers.</summary>
    internal void HoverRowForTests(int r, bool on) => HoverRow(r, on);

    /// <summary>One frame while a row is hovered: the hover band and the tooltip follow the half the pointer is on.</summary>
    internal void HoverTick()
    {
        int r = _hoverRow;
        if (r < 0 || r >= _rows.Count || !Live)
        {
            StopHover();
            return;
        }

        int side = PointerOverMaster(r) ? 1 : PointerOnRow(r) ? 2 : 0;
        if (side == _hoverSide) return;
        _hoverSide = side;
        DrawMasterState(r);
        HideTooltip();
        LeaveTerms(r);
        if (_model.Page.Members.Dragging) return;

        // A caption that explains a term shows the glossary line (the binding draws the box, anchored on the caption).
        var term = side == 1 ? _masterTerms[r] : side == 2 ? _detailTerms[r] : null;
        if (term != null && UiTerms.IsDefined(PluginId, term.Term))
        {
            term.Hover(true);
            return;
        }

        if (side != 1 || _masterShown[r] is not { Kind: UiMasterLineKind.Entry } line || string.IsNullOrWhiteSpace(line.Entry!.Tooltip)) return;

        var row = _rows[r];
        var rect = RectOf(row);
        var divider = RectOf(row, UiMasterDetailLayout.CellDivider);
        var anchor = rect.IsKnown && divider.IsKnown ? ScreenRect.FromCorners(rect.MinX, rect.MinY, divider.MinX, rect.MaxY) : rect;
        ShowTooltip(anchor, line.Entry.Title, line.Entry.Tooltip, () => row.Visible && RectOf(row).IsKnown);
    }

    private void StopHover()
    {
        int r = _hoverRow;
        _hoverRow = -1;
        _hoverSide = 0;
        if (r >= 0 && r < _rows.Count) LeaveTerms(r);
        var tick = _hoverTick;
        _hoverTick = null;
        try { tick?.Dispose(); }
        catch { /* */ }
        HideTooltip();
        if (r >= 0 && r < _rows.Count) DrawMasterState(r);
    }

    private void LeaveTerms(int r)
    {
        if (r < 0 || r >= _rows.Count) return;
        _masterTerms[r]?.Hover(false);
        _detailTerms[r]?.Hover(false);
    }

    /// <summary>The pointer is on row <paramref name="r"/>.</summary>
    private bool PointerOnRow(int r)
    {
        var runtime = WidgetRuntime.Current;
        if (runtime == null) return false;
        PointerSample p;
        try { p = runtime.ReadPointer(); }
        catch { return false; }
        var row = RectOf(_rows[r]);
        return p.Kind == PointerKind.Inside && row.IsKnown && row.Contains(p.X, p.Y);
    }

    /// <summary>The pointer is on row <paramref name="r"/>, left of its divider.</summary>
    private bool PointerOverMaster(int r)
    {
        var runtime = WidgetRuntime.Current;
        if (runtime == null) return false;
        PointerSample p;
        try { p = runtime.ReadPointer(); }
        catch { return false; }
        if (p.Kind != PointerKind.Inside) return false;
        var row = RectOf(_rows[r]);
        var divider = RectOf(_rows[r], UiMasterDetailLayout.CellDivider);
        if (!row.IsKnown || !divider.IsKnown) return false;
        return p.Y >= row.MinY && p.Y <= row.MaxY && p.X >= row.MinX && p.X < divider.MinX;
    }

    private UiDetailSlot? DetailAt(int r) => r >= 0 && r < _detailShown.Length ? _detailShown[r] : null;

    internal void ClickLink(int r)
    {
        if (!Live || DetailAt(r) is not { Kind: UiDetailSlotKind.Line } slot || slot.Line!.Kind != UiDetailLineKind.Link) return;
        RaiseAction(slot.Line.Id, UiDetailAction.Open);
    }

    internal void ClickButton(int r, int b)
    {
        if (!Live || _model.Page.Members.Dragging || DetailAt(r) is not { Kind: UiDetailSlotKind.Line } slot) return;
        var buttons = slot.Line!.Buttons;
        if (b < 0 || b >= buttons.Count || !buttons[b].Enabled) return;
        RaiseAction(slot.Line.Id, buttons[b].Id);
    }

    private void RaiseAction(string lineId, string actionId) =>
        Raise(_action, new UiDetailAction(_model.Tab, _model.Page.SelectedId ?? "", lineId, actionId), "action");

    internal void StepMember(int r, int delta)
    {
        if (!Live || _model.Page.Members.Dragging || DetailAt(r) is not { Kind: UiDetailSlotKind.Member } slot || !slot.Line!.Reorder) return;
        MoveMember(slot.Member!.Id, slot.MemberIndex + delta);
    }

    internal void ToggleMember(int r)
    {
        if (!Live || _model.Page.Members.Dragging || DetailAt(r) is not { Kind: UiDetailSlotKind.Member } slot) return;
        var item = slot.Member!;
        if (!UiOrderedListModel.HasDetail(item)) return;
        SetMemberExpanded(item.Id, !_model.Page.Members.IsExpanded(item.Id));
    }

    internal void RemoveSlot(int r)
    {
        if (!Live || _model.Page.Members.Dragging || DetailAt(r) is not { Kind: UiDetailSlotKind.Member } slot || !slot.Line!.Remove) return;
        if (!slot.Member!.Removable) return;
        RemoveMember(slot.Member.Id);
    }

    /// <summary>A pointer event on row <paramref name="r"/>'s grip: a primary press starts a drag, the release drops it.</summary>
    internal void Grip(int r, UiPointerEvent e)
    {
        var members = _model.Page.Members;
        if (e.Kind == UiPointerEventKind.Down)
        {
            if (!Live || !e.PrimaryHeld || DetailAt(r) is not { Kind: UiDetailSlotKind.Member } slot || !slot.Line!.Reorder) return;
            if (!members.BeginDrag(slot.MemberIndex)) return;
            HideTooltip();
            try { _dragTick = WidgetRuntime.Current?.EveryFrame(DragTick); }
            catch (Exception ex) { Plugin.Logger?.LogWarning("Ui: master-detail '" + Key + "' drag tick: " + ex.Message); }
            Render();
        }
        else if (e.Kind == UiPointerEventKind.Up)
        {
            Drop();
        }
    }

    /// <summary>One frame of a member drag (the ordered list's step): the member slot under the pointer is the target.</summary>
    internal void DragTick()
    {
        var members = _model.Page.Members;
        if (!members.Dragging) return;
        switch (UiOrderedDrag.Step(WidgetRuntime.Current, Live, MemberCenters, out int slot))
        {
            case UiDragStep.Cancel:
                StopDrag();
                Render();
                return;
            case UiDragStep.Drop:
                Drop();
                return;
            case UiDragStep.Target:
                if (slot >= 0 && slot < _centerMembers.Count && members.DragTo(_centerMembers[slot])) Render();
                return;
        }
    }

    /// <summary>The vertical centers of the rows drawing members now, top first; <see cref="_centerMembers"/> holds their member indices.</summary>
    private IReadOnlyList<float> MemberCenters()
    {
        _centerMembers.Clear();
        var centers = new List<float>();
        for (int r = 0; r < _rows.Count; r++)
        {
            if (_detailShown[r] is not { Kind: UiDetailSlotKind.Member } slot) continue;
            var rect = RectOf(_rows[r]);
            centers.Add(rect.IsKnown ? (rect.MinY + rect.MaxY) / 2f : float.NaN);
            _centerMembers.Add(slot.MemberIndex);
        }

        return centers;
    }

    private void Drop()
    {
        var members = _model.Page.Members;
        if (!members.Dragging) return;
        DisposeTick();
        bool moved = members.EndDrag(out var change);
        if (IsDisposed) return;
        Render();
        if (moved) Raise(_reorder, change, "reorder");
    }

    private void StopDrag()
    {
        DisposeTick();
        foreach (int tab in AllTabs()) _model.PageAt(tab).Members.CancelDrag();
    }

    private IEnumerable<int> AllTabs()
    {
        for (int i = 0; i < _model.TabCount; i++) yield return i;
    }

    private void DisposeTick()
    {
        var tick = _dragTick;
        _dragTick = null;
        try { tick?.Dispose(); }
        catch { /* */ }
    }

    // ---- Scrolling -------------------------------------------------------------------------------

    private bool AreaLive => !IsDisposed && Visible && Panel.State == UiHandleState.Shown;

    private ScreenRect MasterArea()
    {
        if (!AreaLive) return ScreenRect.Unknown;
        var top = RectOf(_rows[0]);
        var bottom = RectOf(_rows[_rows.Count - 1]);
        var divider = RectOf(_rows[0], UiMasterDetailLayout.CellDivider);
        if (!top.IsKnown || !bottom.IsKnown || !divider.IsKnown) return ScreenRect.Unknown;
        return ScreenRect.FromCorners(top.MinX, bottom.MinY, divider.MinX, top.MaxY);
    }

    private ScreenRect DetailArea()
    {
        if (!AreaLive) return ScreenRect.Unknown;
        var top = RectOf(_rows[0]);
        var bottom = RectOf(_rows[_rows.Count - 1]);
        var divider = RectOf(_rows[0], UiMasterDetailLayout.CellDivider);
        if (!top.IsKnown || !bottom.IsKnown || !divider.IsKnown) return ScreenRect.Unknown;
        return ScreenRect.FromCorners(divider.MinX, bottom.MinY, top.MaxX, top.MaxY);
    }

    /// <summary>One wheel notch over a half (+1 toward the end). False hands the notch to the panel.</summary>
    internal bool Wheel(bool master, int notch)
    {
        if (!Live) return false;
        int lines = notch * UiMasterDetailLayout.LinesPerNotch;
        return master ? ScrollMaster(lines) : ScrollDetail(lines);
    }

    /// <summary>Up on the first row or down on the last scrolls the half the focus is in, before the focus leaves.</summary>
    internal bool Steer(int r, NavCommand command)
    {
        if (!Live || (command != NavCommand.Up && command != NavCommand.Down)) return false;
        bool detail = FocusOnDetail(r);
        int firstMaster = _model.Search ? 1 : 0;
        if (command == NavCommand.Up)
        {
            if (detail && r == 0) return ScrollDetail(-1);
            if (!detail && r == firstMaster) return ScrollMaster(-1);
            return false;
        }

        if (r != _rows.Count - 1) return false;
        return detail ? ScrollDetail(+1) : ScrollMaster(+1);
    }

    private bool FocusOnDetail(int r)
    {
        try
        {
            var focus = UiNav.Focus;
            if (focus.IsEmpty || !ReferenceEquals(focus.Handle, _rows[r])) return false;
            return Array.IndexOf(UiMasterDetailLayout.DetailKeys, focus.Part) >= 0;
        }
        catch { return false; }
    }

    /// <summary>The right stick (positive up) scrolls the focused half about nine lines a second at full tilt.</summary>
    private bool Stick(int r, float amount)
    {
        if (!Live) return false;
        _stick += -amount * 0.15f;
        if (Math.Abs(_stick) < 1f) return true;
        int lines = _stick > 0 ? 1 : -1;
        _stick -= lines;
        if (FocusOnDetail(r)) ScrollDetail(lines);
        else ScrollMaster(lines);
        return true;
    }

    protected override void OnHidden()
    {
        base.OnHidden();
        StopDrag();
        StopHover();
    }

    protected override void OnDisposing()
    {
        StopDrag();
        StopHover();
        for (int r = 0; r < _rows.Count; r++)
        {
            SyncTerm(_masterTerms, r, UiMasterDetailLayout.CellMTitle, "");
            SyncTerm(_detailTerms, r, UiMasterDetailLayout.CellText, "");
        }

        foreach (var row in _rows)
        {
            UiNav.Steer(row, null);
            UiNav.StickScroll(row, null);
        }
    }

    // ---- Drawing ---------------------------------------------------------------------------------

    protected override void Render()
    {
        if (IsDisposed || !Visible) return;
        if (_tabs != null) TrySetVisible(_tabs, true);
        var page = _model.Page;
        bool icons = false, counts = false;
        foreach (var e in page.Entries)
        {
            if (e.Icon != null || e.ItemPrefab != 0) icons = true;
            if (!string.IsNullOrEmpty(e.Badge)) counts = true;
        }

        var masterWindow = _model.MasterWindow(page);
        var detailWindow = _model.DetailWindow(page);
        var runtime = WidgetRuntime.Current;
        int firstMaster = _model.Search ? 1 : 0;
        for (int r = 0; r < _rows.Count; r++)
        {
            var row = _rows[r];
            Set(row, UiMasterDetailLayout.CellMIcon, visible: icons);
            Set(row, UiMasterDetailLayout.CellMCount, visible: counts);
            if (r < firstMaster)
            {
                _masterShown[r] = null;
                var cell = Find(row, UiMasterDetailLayout.CellMSearch);
                if (cell != null && !string.Equals(cell.Text, page.Query, StringComparison.Ordinal)) row.SetCell(UiMasterDetailLayout.CellMSearch, page.Query);
                Set(row, UiMasterDetailLayout.CellMNote, _model.MasterStatus(page), CellTone.Muted);
            }
            else
            {
                int m = r - firstMaster;
                UiMasterLine? line = m < masterWindow.Count ? masterWindow[m] : null;
                _masterShown[r] = line;
                DrawMaster(row, line, page, icons, runtime);
                DrawMasterState(r);
                SyncTerm(_masterTerms, r, UiMasterDetailLayout.CellMTitle, line is { Kind: UiMasterLineKind.Entry } e ? e.Entry!.Term : "");
            }

            UiDetailSlot? slot = r < detailWindow.Count ? detailWindow[r] : null;
            _detailShown[r] = slot;
            DrawDetail(row, slot, page, runtime);
            SyncTerm(_detailTerms, r, UiMasterDetailLayout.CellText,
                slot is { Kind: UiDetailSlotKind.Line } s && s.Line!.Kind == UiDetailLineKind.Heading ? s.Line.Term : "");
            TrySetVisible(row, true);
        }

        if (_scrollBar != null)
        {
            var (size, position) = _model.DetailBar(page);
            try { _scrollBar.Set(size, position); }
            catch { /* the wheel and the controller still scroll */ }
        }
    }

    private void DrawMaster(IUiCellRow row, UiMasterLine? line, UiMasterDetailPage page, bool icons, IWidgetRuntime? runtime)
    {
        string title = "", note = "", count = "";
        var tone = CellTone.Normal;
        object? icon = null;
        if (line is { } l)
        {
            switch (l.Kind)
            {
                case UiMasterLineKind.Entry:
                    var e = l.Entry!;
                    title = e.Title;
                    note = e.Note;
                    count = UiMasterDetailLayout.CountText(e.Badge);
                    icon = e.Icon;
                    if (icon == null && e.ItemPrefab != 0 && runtime != null && icons)
                    {
                        try { icon = runtime.ItemIcon(e.ItemPrefab); }
                        catch { icon = null; }
                    }

                    break;
                case UiMasterLineKind.New:
                    title = l.Text;
                    tone = CellTone.Info;
                    break;
                default:
                    title = l.Text;
                    tone = CellTone.Muted;
                    break;
            }
        }

        Set(row, UiMasterDetailLayout.CellMTitle, title, tone);
        Set(row, UiMasterDetailLayout.CellMNote, note, CellTone.Muted);
        Set(row, UiMasterDetailLayout.CellMCount, count, CellTone.Client);
        if (icons) row.SetImage(UiMasterDetailLayout.CellMIcon, icon);
    }

    /// <summary>
    /// The master half's band on row <paramref name="r"/>: the Mods list entry's selected band (with its stripe) on the
    /// selected entry, its hover wash under the pointer (an entry or the new-entry line), both together brighter; nothing
    /// otherwise. The controller's focus is NAV1's own marker on the row, so the three never look alike.
    /// </summary>
    private void DrawMasterState(int r)
    {
        if (r < 0 || r >= _rows.Count) return;
        var line = _masterShown[r];
        bool selected = line is { Kind: UiMasterLineKind.Entry } l
                        && string.Equals(l.Entry!.Id, _model.Page.SelectedId, StringComparison.Ordinal);
        bool hovered = r == _hoverRow && _hoverSide == 1 && line is { Kind: UiMasterLineKind.Entry or UiMasterLineKind.New }
                       && !_model.Page.Members.Dragging;
        Tile(_rows[r], UiMasterDetailLayout.MasterTile, UiTileLook.Entry(selected, hovered));
    }

    /// <summary>
    /// Mark row <paramref name="r"/>'s cell <paramref name="cellKey"/> with <paramref name="term"/> (empty: unmark), keeping
    /// the binding in <paramref name="bindings"/>. The row's own hover forwards to it (<see cref="HoverTick"/>), so the
    /// binding watches no hover of its own.
    /// </summary>
    private void SyncTerm(UiTermBinding?[] bindings, int r, string cellKey, string term)
    {
        if (r < 0 || r >= bindings.Length) return;
        var now = bindings[r];
        term = (term ?? "").Trim();
        if (now != null && UiHelpStore.Normalize(now.Term) == UiHelpStore.Normalize(term)) return;
        bindings[r] = null;
        try { now?.Dispose(); }
        catch { /* */ }
        if (term.Length == 0 || IsDisposed) return;
        try { bindings[r] = UiTerms.MarkCell(_rows[r], cellKey, term, watchHover: false); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: master-detail '" + Key + "' term '" + term + "': " + e.Message); }
    }

    /// <summary>The term marked on row <paramref name="r"/>'s master caption (tests and readback).</summary>
    internal string MasterTermAt(int r) => r >= 0 && r < _masterTerms.Length ? _masterTerms[r]?.Term ?? "" : "";

    /// <summary>The term marked on row <paramref name="r"/>'s detail heading (tests and readback).</summary>
    internal string DetailTermAt(int r) => r >= 0 && r < _detailTerms.Length ? _detailTerms[r]?.Term ?? "" : "";

    private static void Tile(IUiCellRow row, int tile, UiTileState state)
    {
        if (row is UiCellRowHandle h) h.SetTileState(tile, state);
    }

    private void DrawDetail(IUiCellRow row, UiDetailSlot? slot, UiMasterDetailPage page, IWidgetRuntime? runtime)
    {
        // What each detail cell shows on this line; everything else is hidden.
        var show = new Dictionary<string, (string Text, CellTone Tone)>(StringComparer.Ordinal);
        bool open = false;
        IReadOnlyList<UiDetailButton> buttons = Array.Empty<UiDetailButton>();
        object? icon = null;
        bool iconCell = false;
        float indent = 0f;
        var members = page.Members;
        if (slot is { } s)
        {
            switch (s.Kind)
            {
                case UiDetailSlotKind.Empty:
                    show[UiMasterDetailLayout.CellText] = (s.Text, CellTone.Muted);
                    break;
                case UiDetailSlotKind.Drawer:
                    indent = MemberIndent(page);
                    show[UiMasterDetailLayout.CellText] = (s.Text, CellTone.Muted);
                    break;
                case UiDetailSlotKind.Member:
                {
                    var item = s.Member!;
                    var line = s.Line!;
                    bool dragging = members.Dragging;
                    bool held = dragging && s.MemberIndex == members.DragTarget;
                    int real = dragging ? -1 : s.MemberIndex;
                    if (line.Reorder) show[UiMasterDetailLayout.CellGrip] = (_grip, held ? CellTone.Gold : CellTone.Normal);
                    if (AnyIcon(members.Items))
                    {
                        iconCell = true;
                        icon = item.Icon;
                        if (icon == null && item.ItemPrefab != 0 && runtime != null)
                        {
                            try { icon = runtime.ItemIcon(item.ItemPrefab); }
                            catch { icon = null; }
                        }
                    }

                    show[UiMasterDetailLayout.CellText] = (item.Title, held ? CellTone.Gold : CellTone.Normal);
                    if (item.Note.Length > 0) show[UiMasterDetailLayout.CellDNote] = (item.Note, CellTone.Muted);
                    if (item.Badge.Length > 0) show[UiMasterDetailLayout.CellDCount] = (item.Badge, CellTone.Client);
                    if (line.Reorder)
                    {
                        show[UiMasterDetailLayout.CellUp] = (_up, !dragging && members.CanMoveUp(real) ? CellTone.Normal : CellTone.Faint);
                        show[UiMasterDetailLayout.CellDown] = (_down, !dragging && members.CanMoveDown(real) ? CellTone.Normal : CellTone.Faint);
                    }

                    if (AnyDetail(members.Items))
                    {
                        bool has = UiOrderedListModel.HasDetail(item);
                        open = has && members.IsExpanded(item.Id) && !dragging;
                        show[UiMasterDetailLayout.CellExpand] = (UiWidgetLayout.ExpandGlyph, has ? CellTone.Normal : CellTone.Faint);
                    }

                    if (line.Remove)
                        show[UiMasterDetailLayout.CellRemove] = (_removeGlyph, item.Removable && !dragging ? CellTone.Normal : CellTone.Faint);
                    break;
                }
                default:
                {
                    var line = s.Line!;
                    switch (line.Kind)
                    {
                        case UiDetailLineKind.Title:
                            show[UiMasterDetailLayout.CellText] = (line.Text, CellTone.Normal);
                            buttons = line.Buttons;
                            break;
                        case UiDetailLineKind.Text:
                            show[UiMasterDetailLayout.CellText] = (line.Text, LabelTones.ToCellTone(line.Tone));
                            break;
                        case UiDetailLineKind.Heading:
                            show[UiMasterDetailLayout.CellText] = (line.Text, CellTone.Gold);
                            break;
                        case UiDetailLineKind.Link:
                            show[UiMasterDetailLayout.CellLink] = (line.Text, CellTone.Normal);
                            if (line.Note.Length > 0) show[UiMasterDetailLayout.CellDNote] = (line.Note, CellTone.Muted);
                            break;
                        case UiDetailLineKind.Actions:
                            buttons = line.Buttons;
                            break;
                        default:
                            show[UiMasterDetailLayout.CellText] = ("", CellTone.Muted);
                            break;
                    }

                    break;
                }
            }
        }

        for (int b = 0; b < buttons.Count; b++)
        {
            var key = UiMasterDetailLayout.CellButton(b);
            var cell = Find(row, key);
            if (cell != null)
            {
                float w = UiMasterDetailLayout.ButtonWidth(buttons[b].Caption);
                cell.Width = CellWidth.Px(w);
                cell.MinWidth = w;
            }

            show[key] = (buttons[b].Caption, buttons[b].Enabled ? CellTone.Normal : CellTone.Disabled);
        }

        if (show.TryGetValue(UiMasterDetailLayout.CellDNote, out var noteText) && Find(row, UiMasterDetailLayout.CellDNote) is { } noteCell)
            noteCell.Width = CellWidth.Px(UiMasterDetailLayout.DetailNoteWidth(noteText.Text));
        if (indent > 0f && Find(row, UiMasterDetailLayout.CellIndent) is { } indentCell)
        {
            float w = Math.Max(1f, indent - CellLayout.Gap);
            indentCell.Width = CellWidth.Px(w);
            indentCell.MinWidth = w;
            show[UiMasterDetailLayout.CellIndent] = ("", CellTone.Muted);
        }

        if (iconCell) show[UiMasterDetailLayout.CellDIcon] = ("", CellTone.Normal);
        foreach (var key in UiMasterDetailLayout.DetailKeys)
        {
            if (show.TryGetValue(key, out var v)) Set(row, key, v.Text, v.Tone, visible: true, force: key == UiMasterDetailLayout.CellIndent || key == UiMasterDetailLayout.CellDNote || key.StartsWith("d-btn", StringComparison.Ordinal));
            else Set(row, key, visible: false);
        }

        if (iconCell) row.SetImage(UiMasterDetailLayout.CellDIcon, icon);
        if (row.Expanded != open) row.SetExpanded(open);
    }

    /// <summary>Where a member's caption starts in the detail half: past the grip and the icon (the drawer's indent).</summary>
    private static float MemberIndent(UiMasterDetailPage page)
    {
        var line = page.MembersLine;
        return UiWidgetLayout.DrawerIndent(line?.Reorder == true, AnyIcon(page.Members.Items));
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

    private static CellSpec? Find(IUiCellRow row, string key)
    {
        foreach (var c in row.Cells)
            if (string.Equals(c.Key, key, StringComparison.Ordinal)) return c;
        return null;
    }

    /// <summary>Rewrite a cell only when a facet changed (each rewrite re-places the row's cells).</summary>
    private static void Set(IUiCellRow row, string key, string? text = null, CellTone? tone = null, bool? visible = null, bool force = false)
    {
        var cell = Find(row, key);
        if (cell == null) return;
        bool changed = force
                       || (text != null && !string.Equals(cell.Text, text, StringComparison.Ordinal))
                       || (tone != null && cell.Tone != tone.Value)
                       || (visible != null && cell.Visible != visible.Value);
        if (changed) row.SetCell(key, text, tone, visible);
    }
}
