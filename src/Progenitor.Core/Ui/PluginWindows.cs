using System;
using System.Collections.Generic;
using Progenitor.Plugins;

namespace Progenitor.Ui;

/// <summary>
/// <c>ctx.Windows</c> (WINDOWS1, docs/ui.md "Standard windows"): this plugin's standard windows. <see cref="Create"/> makes
/// a window of one of the three presets (<see cref="UiWindowPreset"/>) whose version stamp names this plugin
/// (<c>&lt;plugin name&gt; &lt;version&gt; · Progenitor &lt;version&gt;</c>, from its BepInEx metadata). Every window is
/// tracked: each unload reason closes it and releases its rows. Client only: on the server a window is a disabled
/// handle that draws nothing, so shared plugin code needs no side check.
///
/// <para><b>Status:</b> game-free tested, not live-verified.</para>
/// </summary>
public sealed class PluginWindows
{
    private readonly PluginContext _ctx;
    private readonly List<IUiWindow> _windows = new();

    internal PluginWindows(PluginContext ctx)
    {
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));
        _ctx.Track(new ActionDisposable(CloseAll));
    }

    /// <summary>
    /// A new window (not shown yet): add rows to <see cref="IUiWindow.Panel"/>, then <see cref="IUiWindow.Show"/>.
    /// Dispose it to close it for good; unload does that for every window still open.
    /// </summary>
    public IUiWindow Create(UiWindowPreset preset, string title, UiWindowOptions? options = null)
    {
        var window = Ui.Window(preset, title, _ctx.Owner, _ctx.Guid, _ctx.Name, _ctx.Version, options);
        _windows.RemoveAll(w => w.IsDisposed);
        _windows.Add(window);
        return window;
    }

    /// <summary>The stamp line this plugin's windows carry by default.</summary>
    public string VersionStamp => UiWindowLayout.StampText(_ctx.Name, _ctx.Version, Ui.FrameworkFooter);

    /// <summary>This plugin's windows not yet disposed.</summary>
    public IReadOnlyList<IUiWindow> Open
    {
        get
        {
            _windows.RemoveAll(w => w.IsDisposed);
            return _windows.ToArray();
        }
    }

    private void CloseAll()
    {
        var all = _windows.ToArray();
        _windows.Clear();
        foreach (var w in all)
        {
            try { w.Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: closing a window on unload threw: " + e.Message); }
        }
    }
}
