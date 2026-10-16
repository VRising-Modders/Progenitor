using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7: composed widgets for a mod's panel or Mods page — an ordered list, an entity and item picker, tabs with count
/// badges, and a code text area. Each one is made of the kit's own rows and buttons, so it draws in the game's look,
/// lines up with the settings grid, is reached by a controller like every other control, and is released with its panel.
/// On a panel this runtime cannot draw (the server, a skipped module) a widget keeps its state and draws nothing, like
/// every other handle. Layout rules: docs/ui.md "Widgets".
/// </summary>
public static partial class UiWidgets
{
    /// <summary>
    /// An ordered list: drag a row by its grip or use its up and down buttons to reorder, expand a row to read its
    /// drawer, remove a row. <paramref name="key"/> names the widget (and prefixes its rows' bridge keys); keep it unique
    /// on the panel. The list changes itself, then raises <see cref="IUiOrderedList.OnReorder"/> /
    /// <see cref="IUiOrderedList.OnRemove"/>.
    /// </summary>
    public static IUiOrderedList OrderedList(
        this IUiPanel panel, string key, IEnumerable<UiListItem> items, UiOrderedListOptions? options = null)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        return new UiOrderedListHandle(panel, key, items, options);
    }

    /// <summary>
    /// An entity and item picker: a search field with autofill, results under section headings with icons, entries
    /// grayed with the reason they cannot be picked, single or multiple selection. <paramref name="key"/> names the
    /// widget and prefixes its rows' bridge keys.
    /// </summary>
    public static IUiPicker Picker(this IUiPanel panel, string key, IEnumerable<UiPickerEntry> entries, UiPickerOptions? options = null)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        return new UiPickerHandle(panel, key, entries, options);
    }

    /// <summary>
    /// A row of tabs, the selected one lit, each with an optional count badge. On a game window (<see cref="UiLook.GameWindow(IUiPanel)"/>,
    /// every standard window) they are the game's own window tabs; elsewhere the game's popup buttons as chips. A window
    /// with tabs keeps its height while it stays open (<see cref="UiTabsOptions.KeepHeight"/>). Add a tab's controls with
    /// <see cref="IUiTabs.Add"/> after this call; they show only while their tab is selected.
    /// </summary>
    public static IUiTabs Tabs(this IUiPanel panel, string key, IReadOnlyList<string> captions, int selected = 0)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        return new UiTabsHandle(panel, key, captions, new UiTabsOptions { Selected = selected });
    }

    /// <summary>
    /// HUB1: <see cref="Tabs(IUiPanel, string, IReadOnlyList{string}, int)"/> with options: the look
    /// (<see cref="UiTabsLook"/>), whether the window keeps its height across tabs, and a least content height.
    /// </summary>
    public static IUiTabs Tabs(this IUiPanel panel, string key, IReadOnlyList<string> captions, UiTabsOptions options)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        return new UiTabsHandle(panel, key, captions, options);
    }

    /// <summary>
    /// Show <paramref name="text"/> (under <paramref name="title"/>, when given) in the game-styled tooltip box while the
    /// pointer is over <paramref name="handle"/>. Works on any kit control; returns a lease that removes the tooltip (it
    /// also goes with the control). On a runtime that cannot draw, an inert lease.
    /// </summary>
    public static IDisposable Tooltip(this IUiHandle handle, string text, string title = "")
    {
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        return UiTooltipLease.Attach(handle, title ?? "", text ?? "");
    }
}
