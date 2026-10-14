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
    /// </summary>
    void ShowTooltip(ScreenRect anchor, string title, string body);

    /// <summary>Hide the tooltip. No-op when none is shown.</summary>
    void HideTooltip();

    /// <summary>
    /// Watch Down / Up / Exit on one cell of a cell row (its own object), through the same Bootstrap
    /// trampoline <see cref="Ui.WatchPointer"/> uses. Null when the cell has no object.
    /// </summary>
    IDisposable? WatchCell(IRuntimeCellRow row, string cellKey, Action<UiPointerEvent> handler, object? owner);

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
