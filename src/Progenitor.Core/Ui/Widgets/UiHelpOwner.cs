using System;

namespace Progenitor.Ui;

public static partial class ModsMenu
{
    /// <summary>
    /// P7b help: the mod whose page <paramref name="handle"/> is on, when it is the Mods workspace's content pane (or one
    /// of its rows) showing a mod's page (its overview or a custom page). That pane belongs to the framework (its handles
    /// report Progenitor's plugin id), but what it shows is the mod's. Null for anything else.
    /// </summary>
    internal static string? PagePluginOf(IUiHandle? handle)
    {
        if (handle == null || string.IsNullOrEmpty(_selected)) return null;
        // Held, not Live: a page is built (and its words marked) before the pane is shown.
        if (Owned.Held is not UiPanelHandle page || page.IsDisposed) return null;
        if (ReferenceEquals(handle, page)) return _selected;
        foreach (var child in page.ChildrenSnapshot())
            if (ReferenceEquals(child, handle)) return _selected;
        return null;
    }
}

/// <summary>P7b help: whose glossary a marked control reads.</summary>
internal static class UiHelpOwner
{
    /// <summary>
    /// The plugin a control explains its words for: the mod whose Mods page it is on (<see cref="ModsMenu.PagePluginOf"/>),
    /// else the control's own plugin id (a mod's floating window, a vanilla attachment).
    /// </summary>
    internal static string PluginOf(IUiHandle handle)
    {
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        string? page = null;
        try { page = ModsMenu.PagePluginOf(handle); }
        catch { page = null; }
        return string.IsNullOrEmpty(page) ? handle.PluginId : page!;
    }
}
