using System;
using Progenitor.Ui;

namespace Progenitor.ControlsSample;

/// <summary>
/// The gallery's docked panel: a plain floating game window docked to the left edge (<c>panel.Dock(UiDock.Left)</c>),
/// vertically centered, the way a preview's controls or a Find panel sit beside the scene. Its buttons move it round the
/// edges (left, right, top, bottom), step its alignment along the edge (center and top on a side; start, center and end
/// on the top or bottom), and widen it to 30% of the screen, so the placement can be seen live; the other windows (the
/// gallery's own Basic window, say) keep clear of a side dock. Every action writes a log line. Game-free: the panel
/// comes from the plugin.
/// </summary>
internal sealed class GalleryDocked : IDisposable
{
    internal const string Title = "Docked panel";

    private readonly Func<IUiPanel> _panel;
    private readonly Action<string> _log;
    private IUiPanel? _window;
    private readonly UiDockOptions _options = new() { Margin = 24f };
    private UiDock _side = UiDock.Left;

    internal GalleryDocked(Func<IUiPanel> panel, Action<string> log)
    {
        _panel = panel ?? throw new ArgumentNullException(nameof(panel));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    private void Say(string what) => _log(ControlsDemo.LogPrefix + "docked " + what);

    /// <summary>The window while it exists (tests).</summary>
    internal IUiPanel? Window => _window is { IsDisposed: false } w ? w : null;

    /// <summary>Show the window (building it the first time), or hide it when it is shown.</summary>
    internal string Toggle()
    {
        var w = Window;
        if (w != null && w.State == UiHandleState.Shown)
        {
            w.Hide();
            return "closed";
        }

        w ??= _window = Build();
        w.Show();
        return "opened " + Describe();
    }

    private string Describe() =>
        _side + ", " + _options.Align.ToString().ToLowerInvariant() + (_options.WidthFraction > 0f ? ", 30% wide" : "");

    private static bool Strip(UiDock side) => side is UiDock.Top or UiDock.Bottom;

    /// <summary>The next edge round the screen: left, right, top, bottom. The alignment is kept when the new edge takes it.</summary>
    internal void NextEdge()
    {
        _side = _side switch { UiDock.Left => UiDock.Right, UiDock.Right => UiDock.Top, UiDock.Top => UiDock.Bottom, _ => UiDock.Left };
        if (Strip(_side) && _options.Align == UiDockAlign.Top) _options.Align = UiDockAlign.Start;
        if (!Strip(_side) && _options.Align is UiDockAlign.Start or UiDockAlign.End) _options.Align = UiDockAlign.Center;
        Apply();
    }

    /// <summary>The next alignment along the edge: center and top on a side; start, center and end on the top or bottom.</summary>
    internal void NextAlign()
    {
        _options.Align = Strip(_side)
            ? _options.Align switch { UiDockAlign.Start => UiDockAlign.Center, UiDockAlign.Center => UiDockAlign.End, _ => UiDockAlign.Start }
            : _options.Align == UiDockAlign.Top ? UiDockAlign.Center : UiDockAlign.Top;
        Apply();
    }

    /// <summary>The edge and alignment it is docked with (tests).</summary>
    internal (UiDock Side, UiDockAlign Align) Placement => (_side, _options.Align);

    private void Apply()
    {
        Window?.Dock(_side, _options);
        Say(Describe());
    }

    private IUiPanel Build()
    {
        var p = _panel().GameWindow();
        p.ContentAlignment = "left";
        p.Dock(_side, _options);
        p.Text("Docked to a screen edge: it stays there while other windows open and close, and they keep clear of a side dock.", UiTextStyle.Muted);
        p.BeginInline();
        p.Button("Next edge").OnClick(NextEdge);
        p.Button("Align").OnClick(NextAlign);
        p.Button("Width").OnClick(() => { _options.WidthFraction = _options.WidthFraction > 0f ? 0f : 0.3f; Apply(); });
        p.EndInline();
        p.Button("Close").Centered().OnClick(() => { Window?.Hide(); Say("closed"); });
        return p;
    }

    public void Dispose()
    {
        var w = _window;
        _window = null;
        if (w == null || w.IsDisposed) return;
        try { w.Hide(); } finally { w.Dispose(); }
    }
}
