using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7b: icon buttons drawn as marks, not text. The ordered list's grip, up, down and remove, the master-detail member
/// rows' same buttons, and the picker's and the data table's check cells are box-less button cells with no caption (so
/// nothing is underlined and no glyph can be missing from the game's font); their face is a drawn mark
/// (<see cref="UiDrawnMark.Icon"/>, a coverage texture centered in the cell, <see cref="UiMarkSide.Center"/>). The cell
/// stays a button: its click and its NAV1 stop are unchanged.
///
/// <para><b>Look.</b> The muted gold of the game's box-less glyph buttons (<see cref="Normal"/>), brighter under the
/// pointer (<see cref="Bright"/>), faint when the button does nothing on that row (<see cref="Faint"/>: the top row's up,
/// a row that cannot be removed). The colors are game-free and tested; the drawing is the runtime's
/// (<see cref="IWidgetMarks"/>), and a runtime without it draws nothing while the state is kept.</para>
/// </summary>
internal sealed class UiIconMarks : IDisposable
{
    /// <summary>The glyph buttons' muted gold.</summary>
    internal static readonly PaletteColor Normal = PaletteColor.Hex(0xb89a5a);

    /// <summary>Under the pointer.</summary>
    internal static readonly PaletteColor Bright = PaletteColor.Hex(0xe6c98a);

    /// <summary>A button that does nothing on its row.</summary>
    internal static readonly PaletteColor Faint = PaletteColor.Hex(0x5a4e3c);

    /// <summary>
    /// P7b chips: on a selected chip's gold plate, the light cream of the plate's caption (<see cref="MenuPalette.ChipOnText"/>),
    /// at rest and under the pointer alike (the plate's own hover sprite shows the hover).
    /// </summary>
    internal static readonly PaletteColor OnPlate = MenuPalette.ChipOnText;

    /// <summary>The mark's color for its state.</summary>
    internal static PaletteColor ColorFor(bool enabled, bool hovered) => !enabled ? Faint : hovered ? Bright : Normal;

    /// <summary>As <see cref="ColorFor(bool, bool)"/>; an enabled mark on a selected chip's gold plate is <see cref="OnPlate"/>.</summary>
    internal static PaletteColor ColorFor(bool enabled, bool hovered, bool onPlate) =>
        enabled && onPlate ? OnPlate : ColorFor(enabled, hovered);

    private sealed class Slot
    {
        internal IUiMarkView? View;
        internal IDisposable? Hover;
        internal UiDrawnMark Mark = UiDrawnMark.None;
        internal bool Enabled = true;
        internal bool Hovered;
        internal bool OnPlate;
        internal bool Drawn;
        internal PaletteColor DrawnColor;
        internal UiDrawnMark DrawnMark = UiDrawnMark.None;
    }

    private readonly Dictionary<(IUiCellRow Row, string Cell), Slot> _slots = new();
    private bool _disposed;

    /// <summary>
    /// Give cell <paramref name="cellKey"/> of <paramref name="row"/> a mark slot (once). <paramref name="watchHover"/>
    /// brightens the mark under the pointer through the cell's own hover (the cell must be built with
    /// <see cref="CellSpec.Hover"/>); leave it off for a cell whose pointer events another watch owns (the grip's drag).
    /// </summary>
    internal void Attach(IUiCellRow row, string cellKey, bool watchHover)
    {
        if (_disposed || row == null || cellKey == null || _slots.ContainsKey((row, cellKey))) return;
        var slot = new Slot();
        _slots[(row, cellKey)] = slot;
        try
        {
            if (row is UiCellRowHandle handle && handle.NavWidget(null) is IRuntimeCellRow runtimeRow && WidgetRuntime.Current is IWidgetMarks marks)
                slot.View = marks.AttachMark(runtimeRow, cellKey, UiMarkSide.Center);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: icon mark on '" + cellKey + "' could not be attached: " + e.Message); }

        if (!watchHover) return;
        try
        {
            slot.Hover = row.OnCellHover(cellKey, on =>
            {
                slot.Hovered = on;
                Apply(slot);
            });
        }
        catch { /* the mark keeps its resting color */ }
    }

    /// <summary>Show <paramref name="mark"/> (<see cref="UiDrawnMark.None"/> hides it) on the cell, faint when not <paramref name="enabled"/>.</summary>
    internal void Set(IUiCellRow row, string cellKey, UiDrawnMark mark, bool enabled = true) => Set(row, cellKey, mark, enabled, onPlate: false);

    /// <summary>As above; <paramref name="onPlate"/> draws it in the gold plate's cream (a selected chip's remove mark).</summary>
    internal void Set(IUiCellRow row, string cellKey, UiDrawnMark mark, bool enabled, bool onPlate)
    {
        if (_disposed || !_slots.TryGetValue((row, cellKey), out var slot)) return;
        slot.Mark = mark;
        slot.Enabled = enabled;
        slot.OnPlate = onPlate;
        Apply(slot);
    }

    /// <summary>The mark the cell shows now (tests and readback).</summary>
    internal UiDrawnMark MarkOf(IUiCellRow row, string cellKey) =>
        _slots.TryGetValue((row, cellKey), out var slot) ? slot.Mark : UiDrawnMark.None;

    /// <summary>Whether the cell's mark is drawn enabled (tests and readback).</summary>
    internal bool EnabledOf(IUiCellRow row, string cellKey) => _slots.TryGetValue((row, cellKey), out var slot) && slot.Enabled;

    /// <summary>The color the cell's mark is drawn in now (tests).</summary>
    internal PaletteColor ColorOf(IUiCellRow row, string cellKey) =>
        _slots.TryGetValue((row, cellKey), out var slot) ? ColorFor(slot.Enabled, slot.Hovered, slot.OnPlate) : Normal;

    private static void Apply(Slot slot)
    {
        var color = ColorFor(slot.Enabled, slot.Hovered, slot.OnPlate);
        if (slot.Drawn && slot.DrawnMark.Equals(slot.Mark) && slot.DrawnColor.Equals(color)) return;
        slot.Drawn = true;
        slot.DrawnMark = slot.Mark;
        slot.DrawnColor = color;
        try { slot.View?.Set(slot.Mark, color); }
        catch { /* the row is gone */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var slot in _slots.Values)
        {
            try { slot.Hover?.Dispose(); }
            catch { /* */ }
            try { slot.View?.Dispose(); }
            catch { /* */ }
        }

        _slots.Clear();
    }
}
