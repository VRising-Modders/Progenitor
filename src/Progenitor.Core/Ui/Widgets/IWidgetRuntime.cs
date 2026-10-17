using System;

namespace Progenitor.Ui;

/// <summary>
/// P7: what the composed widgets (ordered list, picker, tabs, code area) need from the runtime beyond
/// <see cref="IUiRuntime"/>. A separate interface so the widget work does not widen the core runtime
/// contract: <see cref="WidgetRuntime.Current"/> answers null on a runtime that does not implement it
/// (the server's null runtime, an older fake), and every widget then degrades to state only, exactly
/// as a disabled handle does.
/// </summary>
internal interface IWidgetRuntime
{
    /// <summary>
    /// Show the kit's tooltip box (the dropdown description box's look: the game's dark framed box, a
    /// gold title line, a body) beside <paramref name="anchor"/> (screen pixels), above every mod panel.
    /// One tooltip at a time: a second call moves and rewrites it. Empty title and body hide it.
    /// <paramref name="stillShown"/>, when given, is asked once a frame while the box is up; the box hides itself the
    /// first time it answers false (the hovered control left the screen without a pointer exit: Escape closed its
    /// panel, a page was rebuilt, its plugin unloaded).
    /// </summary>
    void ShowTooltip(ScreenRect anchor, string title, string body, Func<bool>? stillShown = null);

    /// <summary>Hide the tooltip. No-op when none is shown.</summary>
    void HideTooltip();

    /// <summary>
    /// Watch Down / Up / Exit on one cell of a cell row (its own object), through the same Bootstrap
    /// trampoline <see cref="Ui.WatchPointer"/> uses. Null when the cell has no object.
    /// </summary>
    IDisposable? WatchCell(IRuntimeCellRow row, string cellKey, Action<UiPointerEvent> handler, object? owner);

    /// <summary>
    /// CHECKLIST1: watch Down / Up / Exit on a whole widget's own object (a cell row: a press anywhere on it that no cell
    /// button takes), as <see cref="WatchCell"/> does for one cell. Null when the widget has no object or the runtime
    /// cannot watch it.
    /// </summary>
    IDisposable? WatchWidget(IRuntimeWidget widget, Action<UiPointerEvent> handler, object? owner) => null;

    /// <summary>
    /// Report the pointer entering (<c>true</c>) and leaving any kit widget's object, through owned
    /// <c>EventTrigger</c> rows and the Bootstrap trampoline (the tooltip on an arbitrary handle). Null when the widget
    /// has no object.
    /// </summary>
    IDisposable? WatchHover(IRuntimeWidget widget, Action<bool> onHover, object? owner);

    /// <summary>
    /// Run <paramref name="tick"/> once per rendered frame on the main thread until the returned handle is
    /// disposed (a drag in flight, a tooltip's hover delay). A tick that throws is logged once and removed.
    /// </summary>
    IDisposable EveryFrame(Action tick);

    /// <summary>
    /// <paramref name="preferred"/> when the kit's body font draws every character of it, else
    /// <paramref name="fallback"/>, so a widget never shows a missing-glyph box.
    /// </summary>
    string DrawableGlyph(string preferred, string fallback);

    /// <summary>The pointer in screen pixels (the same reading the hover gate uses).</summary>
    PointerSample ReadPointer();

    /// <summary>Whether the primary (left) mouse button is held now, from the framework's input layer.</summary>
    bool PrimaryHeld();

    /// <summary>
    /// A game item's own inventory icon (<c>ManagedItemData.Icon</c>, a <c>UnityEngine.Sprite</c>) for its prefab id, or
    /// null when there is no client world or the item has none. Cached per world.
    /// </summary>
    object? ItemIcon(int prefab);

    /// <summary>A game item's display name in the player's language for its prefab id, or null.</summary>
    string? ItemName(int prefab);

    /// <summary>
    /// P7b: the Shift and Ctrl keys held now, from the framework's input layer (read at the moment a click arrives, for
    /// multi-select). Never a keybind: the modifiers only change what a click means.
    /// </summary>
    UiClickModifiers ClickModifiers() => UiClickModifiers.None;

    /// <summary>
    /// P7b: take wheel notches over <paramref name="area"/> (screen pixels) of <paramref name="panel"/> before that panel
    /// scrolls (<see cref="UiWheelClaims"/>): only while <paramref name="panel"/> is the topmost shown panel under the
    /// pointer and the pointer is inside its visible viewport. <paramref name="onNotch"/> gets +1 toward the end, -1
    /// toward the start, and answers whether it moved. Disposed with the widget.
    /// </summary>
    IDisposable ClaimWheel(IUiPanel panel, Func<ScreenRect> area, Func<int, bool> onNotch) =>
        UiWheelClaims.Add(area, onNotch, (panel as UiPanelHandle)?.RuntimePanel);

    /// <summary>
    /// P7b: a vertical scroll bar in the game's art (<c>ScrollBar_Bar</c> / <c>ScrollBar_Handle</c>, the kit's track and
    /// handle when not borrowed) down the right edge of the rows from <paramref name="top"/> to <paramref name="bottom"/>
    /// (both cell rows of one panel, top first), inside the rows' right inset. Null when the runtime cannot draw one
    /// (the wheel and the controller still scroll).
    /// </summary>
    IWidgetScrollBar? AttachScrollBar(IRuntimeCellRow top, IRuntimeCellRow bottom, object? owner) => null;

    /// <summary>
    /// HUB1: <paramref name="panel"/>'s room from its last layout, in kit units: the tallest its scrolling viewport may
    /// be (at the frame's height cap: the screen share, a standard window's place, a Mods pane's fixed geometry) and the
    /// height its content takes now. False before a layout or on a runtime that does not lay panels out.
    /// </summary>
    bool PanelRoom(IUiPanel panel, out float maxViewport, out float content)
    {
        maxViewport = 0f;
        content = 0f;
        return false;
    }

    /// <summary>
    /// HUB1: a castle building's icon in the game's build menu (a <c>UnityEngine.Sprite</c>) for its prefab id, or null
    /// (<see cref="UiBuildingIcons"/>).
    /// </summary>
    object? BuildingIcon(int prefab) => null;
}

/// <summary>
/// P7b: a widget's own scroll bar (<see cref="IWidgetRuntime.AttachScrollBar"/>). The widget owns the position; the bar
/// shows it and reports drags. Disposed with the widget.
/// </summary>
internal interface IWidgetScrollBar : IDisposable
{
    /// <summary>
    /// Show the bar for a view of <paramref name="size"/> (the visible share, 0..1) at <paramref name="position"/> (0 the
    /// top, 1 the bottom). A size of 1 or more hides it. Never raises <see cref="OnDragged"/>.
    /// </summary>
    void Set(float size, float position);

    /// <summary>The bar is drawn now.</summary>
    bool Visible { get; }

    /// <summary>The player dragged the handle or clicked the track: the new position, 0 the top, 1 the bottom.</summary>
    Action<float>? OnDragged { get; set; }
}

/// <summary>Resolves the current runtime's widget half.</summary>
internal static class WidgetRuntime
{
    /// <summary>The widget half of the current runtime, or null when it has none.</summary>
    internal static IWidgetRuntime? Current
    {
        get
        {
            try { return Ui.CurrentRuntime() as IWidgetRuntime; }
            catch { return null; }
        }
    }
}
