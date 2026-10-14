using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7b: the chip set drawn as kit cell rows (<see cref="UiWidgets.Chips"/>). One line row per possible line is made once,
/// each with room for <see cref="UiChipsOptions.MaxPerLine"/> chips, and rewritten in place: a render packs the chips for
/// the row's width (<see cref="UiChipsLayout.Pack"/>), writes each line's widths and captions into its cells and hides the
/// lines it does not need. See <see cref="UiChipsLayout"/> for the geometry.
/// </summary>
internal sealed class UiChipsHandle : UiComposedHandle, IUiChips
{
    /// <summary>How often (in frames) a measured set checks whether its row changed width.</summary>
    internal const int MeasureEveryFrames = 20;

    private readonly UiChipsModel _model;
    private readonly UiChipsOptions _options;
    private readonly int _maxLines;
    private readonly int _perLine;
    private readonly List<IUiCellRow> _lines = new();
    private readonly List<Action<UiChip>> _click = new();
    private readonly List<Action<UiChip>> _remove = new();
    private readonly List<Action<int>> _add = new();
    private readonly List<Action<IReadOnlyList<UiChip>>> _more = new();
    private ChipPack _pack = ChipPack.None;
    private float _measured;
    private int _frames;
    private bool _repacking;

    internal UiChipsHandle(IUiPanel panel, string key, IEnumerable<UiChip>? chips, UiChipsOptions? options)
        : base(panel, key, key)
    {
        _options = options ?? new UiChipsOptions();
        _model = new UiChipsModel(chips);
        _maxLines = UiChipsLayout.ClampLines(_options.MaxLines);
        _perLine = UiChipsLayout.ClampPerLine(_options.MaxPerLine);
        string removeGlyph = UiWidgetLayout.RemoveGlyph;
        var runtime = WidgetRuntime.Current;
        try { if (runtime != null) removeGlyph = runtime.DrawableGlyph(UiWidgetLayout.RemoveGlyph, UiWidgetLayout.RemoveFallback); }
        catch { /* the preferred glyph stays */ }

        for (int i = 0; i < _maxLines; i++)
        {
            int line = i;
            var cells = UiChipsLayout.LineCells(_perLine, removeGlyph);
            var row = Part(panel.CellRow(new CellRowSpec(RowKey("line" + i), cells, height: UiChipsLayout.LineHeight, plain: true, gapAbove: i == 0 ? -1f : UiChipsLayout.LineGap)));
            _lines.Add(row);
            for (int j = 0; j < _perLine; j++)
            {
                int slot = j;
                Lease(row.OnClick(UiChipsLayout.ChipKey(j), () => Press(line, slot)));
                Lease(row.OnClick(UiChipsLayout.RemoveKey(j), () => RemoveAt(line, slot)));
                Lease(row.OnCellHover(UiChipsLayout.ChipKey(j), on => HoverSlot(line, slot, on)));
            }
        }

        // A set packed for a fixed width never needs to look at its row again.
        if (!(_options.RowWidth > 0f) && runtime != null)
        {
            try { Lease(runtime.EveryFrame(MeasureTick)); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: chips '" + Key + "' measure tick: " + e.Message); }
        }

        Render();
    }

    // ---- IUiChips ------------------------------------------------------------------------------

    public IReadOnlyList<UiChip> Chips => _model.Chips;

    public int Shown => _pack.Shown;

    public int Overflow => _pack.Overflow;

    public int Capacity => _maxLines * _perLine;

    public IReadOnlyList<string> SelectedIds => _model.SelectedIds();

    /// <summary>The width the chips were last packed for, in panel units.</summary>
    internal float PackedWidth => _pack.RowWidth;

    internal ChipPack Pack => _pack;

    /// <summary>The line rows, in order (tests and the readback).</summary>
    internal IReadOnlyList<IUiCellRow> LineRows => _lines;

    public void SetChips(IEnumerable<UiChip> chips)
    {
        if (IsDisposed) return;
        HideTooltip();
        _model.SetChips(chips);
        Render();
    }

    public bool Remove(string id)
    {
        if (IsDisposed || !_model.Remove(id, out var removed)) return false;
        HideTooltip();
        Render();
        Raise(_remove, removed!, "remove");
        return true;
    }

    public bool SetSelected(string id, bool selected)
    {
        if (IsDisposed || !_model.SetSelected(id, selected)) return false;
        Render();
        return true;
    }

    public bool IsSelected(string id) => _model.IsSelected(id);

    public IDisposable OnClick(Action<UiChip> handler) => Subscribe(_click, handler);

    public IDisposable OnRemove(Action<UiChip> handler) => Subscribe(_remove, handler);

    public IDisposable OnAdd(Action handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        return Subscribe(_add, (Action<int>)(_ => handler()));
    }

    public IDisposable OnMore(Action<IReadOnlyList<UiChip>> handler) => Subscribe(_more, handler);

    public Dictionary<string, object?> Describe()
    {
        var chips = new List<Dictionary<string, object?>>(_model.Count);
        for (int i = 0; i < _model.Count; i++)
        {
            var chip = _model.Chips[i];
            var placed = _pack.Of(i);
            chips.Add(new Dictionary<string, object?>
            {
                ["id"] = chip.Id,
                ["label"] = chip.Label,
                ["shown"] = placed?.Shown,
                ["selected"] = _model.IsSelected(chip.Id),
                ["removable"] = RemoveShown(chip),
                ["item"] = chip.IsItem,
                ["term"] = chip.Term ?? "",
                ["line"] = placed?.Line ?? -1,
                ["slot"] = placed?.Slot ?? -1,
            });
        }

        var add = _pack.Of(UiChipsLayout.AddIndex);
        var more = _pack.Of(UiChipsLayout.MoreIndex);
        return new Dictionary<string, object?>
        {
            ["kind"] = "chips",
            ["key"] = Key,
            ["chips"] = chips,
            ["shown"] = _pack.Shown,
            ["overflow"] = _pack.Overflow,
            ["capacity"] = Capacity,
            ["rowWidth"] = _pack.RowWidth,
            ["lines"] = _pack.Lines.Count,
            ["toggle"] = _options.Toggle,
            ["add"] = add == null ? null : new Dictionary<string, object?> { ["line"] = add.Line, ["slot"] = add.Slot },
            ["more"] = more == null ? null : new Dictionary<string, object?> { ["line"] = more.Line, ["slot"] = more.Slot, ["text"] = more.Shown },
            ["rowKeys"] = RowKey("line") + "<n>",
            ["cellKeys"] = "chip<slot> / x<slot> / icon<slot>",
        };
    }

    // ---- Input -------------------------------------------------------------------------------

    private bool RemoveShown(UiChip chip) => _options.Remove && chip.Removable;

    /// <summary>The caption of chip slot (<paramref name="line"/>, <paramref name="slot"/>) was pressed.</summary>
    internal void Press(int line, int slot)
    {
        if (!Live) return;
        var placed = _pack.At(line, slot);
        if (placed == null) return;
        switch (placed.Index)
        {
            case UiChipsLayout.AddIndex:
                Raise(_add, 0, "add");
                return;
            case UiChipsLayout.MoreIndex:
                var hidden = new List<UiChip>(_pack.Overflow);
                for (int i = _pack.Shown; i < _model.Count; i++) hidden.Add(_model.Chips[i]);
                Raise(_more, (IReadOnlyList<UiChip>)hidden, "more");
                return;
        }

        if (placed.Index < 0 || placed.Index >= _model.Count) return;
        var chip = _model.Chips[placed.Index];
        if (_options.Toggle)
        {
            _model.SetSelected(chip.Id, !_model.IsSelected(chip.Id));
            Render();
        }

        Raise(_click, chip, "click");
    }

    /// <summary>The remove glyph of chip slot (<paramref name="line"/>, <paramref name="slot"/>) was pressed.</summary>
    internal void RemoveAt(int line, int slot)
    {
        if (!Live) return;
        var placed = _pack.At(line, slot);
        if (placed == null || placed.Index < 0 || placed.Index >= _model.Count || !placed.Remove) return;
        Remove(_model.Chips[placed.Index].Id);
    }

    private void HoverSlot(int line, int slot, bool on)
    {
        var term = TermBinding(line, slot);
        if (!on)
        {
            HideTooltip();
            term?.Hover(false);
            return;
        }

        var placed = _pack.At(line, slot);
        if (placed == null) return;
        // P7b help: a chip whose caption explains a term shows the glossary line (the term binding draws the box).
        if (term != null && placed.Index >= 0 && UiTerms.IsDefined(PluginId, term.Term))
        {
            term.Hover(true);
            return;
        }
        string title, body;
        if (placed.Index == UiChipsLayout.AddIndex)
        {
            title = "";
            body = _options.AddTooltip ?? "";
        }
        else if (placed.Index == UiChipsLayout.MoreIndex)
        {
            title = "";
            body = HiddenSummary();
        }
        else if (placed.Index >= 0 && placed.Index < _model.Count)
        {
            var chip = _model.Chips[placed.Index];
            // A shortened caption is always worth a tooltip: the full caption is its title.
            title = placed.Truncated || chip.Tooltip.Length > 0 ? placed.Caption : "";
            body = chip.Tooltip;
        }
        else return;

        var row = _lines[line];
        string cell = UiChipsLayout.ChipKey(slot);
        ShowTooltip(RectOf(row, cell), title, body, () => row.Visible && RectOf(row, cell).IsKnown);
    }

    /// <summary>The more chip's hover text: the hidden chips' labels, the first few.</summary>
    internal string HiddenSummary()
    {
        const int listed = 8;
        var names = new List<string>();
        for (int i = _pack.Shown; i < _model.Count && names.Count < listed; i++) names.Add(_model.Chips[i].Label);
        int rest = _pack.Overflow - names.Count;
        return string.Join(", ", names) + (rest > 0 ? ", and " + rest + " more" : "");
    }

    // ---- Width -----------------------------------------------------------------------------------

    /// <summary>The row width to pack for: the caller's, else the measured row, else the narrowest checked width.</summary>
    private float RowWidth()
    {
        if (_options.RowWidth > 0f) return _options.RowWidth;
        float measured = MeasureRow();
        if (measured > 0f) _measured = measured;
        return _measured > 0f ? _measured : UiChipsLayout.DefaultRowWidth;
    }

    /// <summary>
    /// The first line row's width in panel units: its screen width over the canvas scale, read from a drawn caption cell
    /// whose asked width is known. 0 while nothing is drawn.
    /// </summary>
    private float MeasureRow()
    {
        if (_lines.Count == 0) return 0f;
        var row = _lines[0];
        var rowRect = RectOf(row);
        if (!rowRect.IsKnown) return 0f;
        var cells = row.Cells;
        for (int j = 0; j < _perLine; j++)
        {
            var spec = cells[j * UiChipsLayout.CellsPerSlot];
            if (!spec.Visible || spec.Width.Mode != CellWidthMode.Px) continue;
            var cellRect = RectOf(row, spec.Key);
            if (!cellRect.IsKnown) continue;
            float width = UiChipsLayout.LayoutWidth(rowRect.MaxX - rowRect.MinX, cellRect.MaxX - cellRect.MinX, spec.Width.Value);
            // A row read before its first layout pass can report a placeholder size: never pack for less than a panel row.
            return width >= UiChipsLayout.MinMeasuredWidth ? width : 0f;
        }

        return 0f;
    }

    /// <summary>Every few frames while shown: pack again when the row's width changed (a resized window, a first layout).</summary>
    internal void MeasureTick()
    {
        if (++_frames < MeasureEveryFrames) return;
        _frames = 0;
        if (IsDisposed || !Visible || Panel.State != UiHandleState.Shown) return;
        float measured = MeasureRow();
        if (measured > 0f && Math.Abs(measured - _pack.RowWidth) > 1f)
        {
            _measured = measured;
            Render();
        }
    }

    // ---- Terms (P7b help) ------------------------------------------------------------------------

    /// <summary>One term binding per chip slot, following the chip drawn in it (slots are re-packed on every render).</summary>
    private UiTermBinding?[,]? _slotTerms;

    private UiTermBinding? TermBinding(int line, int slot) =>
        _slotTerms != null && line >= 0 && line < _slotTerms.GetLength(0) && slot >= 0 && slot < _slotTerms.GetLength(1)
            ? _slotTerms[line, slot]
            : null;

    /// <summary>Mark slot (<paramref name="line"/>, <paramref name="slot"/>)'s chip caption with <paramref name="term"/> (empty: unmark).</summary>
    private void SyncTerm(int line, int slot, string term)
    {
        _slotTerms ??= new UiTermBinding?[_maxLines, _perLine];
        if (line < 0 || line >= _maxLines || slot < 0 || slot >= _perLine) return;
        var now = _slotTerms[line, slot];
        term = (term ?? "").Trim();
        if (now != null && UiHelpStore.Normalize(now.Term) == UiHelpStore.Normalize(term)) return;
        _slotTerms[line, slot] = null;
        try { now?.Dispose(); }
        catch { /* */ }
        if (term.Length == 0 || IsDisposed) return;
        // The chip cell's own hover is bound to HoverSlot, which forwards to the binding: no second hover watch.
        try { _slotTerms[line, slot] = UiTerms.MarkCell(_lines[line], UiChipsLayout.ChipKey(slot), term, watchHover: false, reserveRoom: false); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: chips '" + Key + "' term '" + term + "': " + e.Message); }
    }

    /// <summary>The term marked on slot (<paramref name="line"/>, <paramref name="slot"/>)'s chip, or empty (tests and readback).</summary>
    internal string TermAt(int line, int slot) => TermBinding(line, slot)?.Term ?? "";

    protected override void OnDisposing()
    {
        if (_slotTerms == null) return;
        for (int i = 0; i < _maxLines; i++)
            for (int j = 0; j < _perLine; j++)
                SyncTerm(i, j, "");
    }

    // ---- Drawing -------------------------------------------------------------------------------

    protected override void Render()
    {
        if (IsDisposed || !Visible) return;
        var pieces = new List<UiChipsLayout.ChipPiece>(_model.Count);
        for (int i = 0; i < _model.Count; i++)
        {
            var c = _model.Chips[i];
            pieces.Add(new UiChipsLayout.ChipPiece(i, UiChipsLayout.Caption(c), c.IsItem, RemoveShown(c), (c.Term ?? "").Trim().Length > 0));
        }

        _pack = UiChipsLayout.Pack(pieces, RowWidth(), _maxLines, _perLine, _options.Add ? (_options.AddText ?? "+") : null);
        var runtime = WidgetRuntime.Current;
        for (int i = 0; i < _lines.Count; i++)
        {
            var row = _lines[i];
            bool used = i < _pack.Lines.Count || (i == 0 && _pack.Empty);
            if (!used)
            {
                TrySetVisible(row, false);
                for (int j = 0; j < _perLine; j++) SyncTerm(i, j, "");
                continue;
            }

            // The cell specs are the row's live specs: widths and captions are written into them, then each cell is
            // repainted (the runtime places the row's cells again from the specs on every repaint).
            var cells = row.Cells;
            UiChipsLayout.Apply(cells, _pack, i);
            var placed = i < _pack.Lines.Count ? _pack.Lines[i] : (IReadOnlyList<ChipPlacement>)Array.Empty<ChipPlacement>();
            for (int j = 0; j < _perLine; j++)
            {
                var p = j < placed.Count ? placed[j] : null;
                string chipKey = UiChipsLayout.ChipKey(j);
                var tone = p == null ? CellTone.ChipOff : ChipTone(p);
                row.SetCell(chipKey, tone: tone, visible: p != null);
                // The remove glyph takes the chip's tone: light cream on a selected chip's gold plate.
                row.SetCell(UiChipsLayout.RemoveKey(j), tone: tone, visible: p != null && p.Remove);
                string iconKey = UiChipsLayout.IconKey(j);
                row.SetCell(iconKey, visible: p != null && p.Icon);
                if (p != null && p.Icon) row.SetImage(iconKey, IconOf(_model.Chips[p.Index], runtime));
                SyncTerm(i, j, p != null && p.Index >= 0 ? _model.Chips[p.Index].Term : "");
            }

            row.SetCell(UiChipsLayout.EmptyKey, _options.EmptyText ?? "", visible: i == 0 && _pack.Empty);
            TrySetVisible(row, true);
        }

        // The first render packs before any caption is drawn to measure by; once one is, a row that reads a different
        // width is packed again at once rather than a few frames later.
        if (_repacking || _options.RowWidth > 0f) return;
        float measured = MeasureRow();
        if (measured <= 0f || Math.Abs(measured - _pack.RowWidth) <= 1f) return;
        _measured = measured;
        _repacking = true;
        try { Render(); }
        finally { _repacking = false; }
    }

    private CellTone ChipTone(ChipPlacement p) =>
        p.Index >= 0 && p.Index < _model.Count && _model.IsSelected(_model.Chips[p.Index].Id) ? CellTone.ChipOn : CellTone.ChipOff;

    private static object? IconOf(UiChip chip, IWidgetRuntime? runtime)
    {
        if (chip.Icon != null) return chip.Icon;
        if (chip.ItemPrefab == 0 || runtime == null) return null;
        try { return runtime.ItemIcon(chip.ItemPrefab); }
        catch { return null; }
    }
}
