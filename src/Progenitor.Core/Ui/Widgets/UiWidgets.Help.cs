using System;

namespace Progenitor.Ui;

public static partial class UiWidgets
{
    /// <summary>
    /// P7b (experimental): mark <paramref name="term"/> on this control. Hovering it shows the term's line from the
    /// plugin's glossary (<see cref="UiTerms.Define(string, string, string)"/>) in the kit's tooltip, the term as the
    /// title; a small muted gold circle-i after the control's text shows until the player has hovered the term
    /// <see cref="UiTerms.AutoHideAfter"/> times or its marker is hidden (<see cref="UiTerms.HideMarker"/>); the
    /// explanation keeps working without it. The glossary is read on every hover, so a term defined (or reworded) later
    /// still explains itself. Works on any kit control (a label, a button, a toggle, a heading); the plugin is the
    /// control's own (<see cref="IUiHandle.PluginId"/>). Returns a lease that removes the hover and the marker; both also
    /// go with the control. On a runtime that cannot draw, the lease keeps the state and draws nothing.
    /// </summary>
    public static IDisposable Term(this IUiHandle handle, string term)
    {
        if (handle == null) throw new ArgumentNullException(nameof(handle));
        if (string.IsNullOrWhiteSpace(term)) throw new ArgumentException("A term is required.", nameof(term));
        return UiTerms.MarkHandle(handle, term);
    }

    /// <summary>
    /// P7b (experimental): a toggle bound to this plugin's term markers, for a plugin's own settings or Help page: on
    /// shows every marker again (<see cref="UiTerms.ShowMarkers"/>: hidden markers back, hover counts restarted), off hides
    /// them all. It follows changes made elsewhere (an auto-hide, another toggle). The plugin is the panel's.
    /// </summary>
    public static IUiToggle TermMarkersToggle(this IUiPanel panel, string text = "Show help markers")
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        string pluginId = panel.PluginId;
        var toggle = panel.Toggle(string.IsNullOrWhiteSpace(text) ? "Show help markers" : text, UiTerms.MarkersOn(pluginId));
        bool writing = false;
        toggle.OnChanged(on =>
        {
            if (writing) return;
            writing = true;
            try { UiTerms.SetMarkers(pluginId, on); }
            finally { writing = false; }
        });
        // Released with the toggle on any panel kind: the handler is pruned as soon as the toggle is disposed (a page
        // redraw, a header rebuilt, the panel closed), and with its owner on unload (Ui.RemoveAll); a panel that offers
        // a disposal hook also takes the lease. A ModHeaderPanel's own Dispose is a no-op, so the toggle is the anchor.
        var follow = UiTerms.OnMarkersChanged(pluginId, () =>
        {
            if (writing || toggle.IsDisposed) return;
            bool now = UiTerms.MarkersOn(pluginId);
            if (toggle.Value == now) return;
            writing = true;
            try { toggle.Value = now; }
            finally { writing = false; }
        }, (toggle as UiHandle)?.Owner, () => toggle.IsDisposed);
        if (panel is UiPanelHandle host) host.AttachDisposable(follow);
        return toggle;
    }
}
