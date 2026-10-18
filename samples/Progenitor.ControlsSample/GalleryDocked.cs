using System;
using Progenitor.Ui;

namespace Progenitor.ControlsSample;

/// <summary>
/// The gallery's docked panel: a plain floating game window docked to the left edge (<c>panel.Dock(UiDock.Left)</c>),
/// vertically centered, the way a preview's controls or a Find panel sit beside the scene. Its buttons move it around the
/// edges (left, right, top, bottom), step its alignment along the edge (center and top on a side; start, center and end
/// on the top or bottom), and widen it to 30% of the screen on a side. On the top and bottom edges it is a narrow
/// strip, 420 wide (it fits the gap in the middle of the game's container window), with no title band and as tall as
/// its rows; Width switches it to the 640 frame and back. The other windows (the gallery's own Basic window, say) keep
/// clear of a side dock. Every action writes a log line. Game-free: the panel comes from the plugin.
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

    /// <summary>The narrow strip's width: it fits the 448-unit gap in the middle of the game's container window.</summary>
    internal const float StripWidth = 420f;

    private string Describe() =>
        _side + ", " + _options.Align.ToString().ToLowerInvariant()
        + (_options.WidthFraction > 0f ? ", 30% wide" : _options.Width > 0f ? ", " + _options.Width + " wide" : "");

    private static bool Strip(UiDock side) => side is UiDock.Top or UiDock.Bottom;

    /// <summary>
    /// The next edge around the screen: left, right, top, bottom. The alignment is kept when the new edge takes it. On the
    /// top and bottom edges it becomes a narrow strip (<see cref="StripWidth"/> wide, no title band, as tall as its rows).
    /// </summary>
    internal void NextEdge()
    {
        bool wasStrip = Strip(_side);
        _side = _side switch { UiDock.Left => UiDock.Right, UiDock.Right => UiDock.Top, UiDock.Top => UiDock.Bottom, _ => UiDock.Left };
        if (Strip(_side) && _options.Align == UiDockAlign.Top) _options.Align = UiDockAlign.Start;
        if (!Strip(_side) && _options.Align is UiDockAlign.Start or UiDockAlign.End) _options.Align = UiDockAlign.Center;
        if (Strip(_side) && !wasStrip) { _options.Width = StripWidth; _options.WidthFraction = 0f; }
        if (!Strip(_side) && wasStrip) _options.Width = 0f;
        Apply();
    }

    /// <summary>On a side: 30% of the screen or its own width. On the top or bottom: the narrow strip or the 640 frame.</summary>
    internal void ToggleWidth()
    {
        if (Strip(_side)) _options.Width = _options.Width > 0f ? 0f : StripWidth;
        else _options.WidthFraction = _options.WidthFraction > 0f ? 0f : 0.3f;
        Apply();
    }

    /// <summary>The frame width asked for (tests).</summary>
    internal float AskedWidth => _options.Width;

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
        p.Text("Docked to a screen edge: it stays put while other windows open and close.", UiTextStyle.Muted);
        p.BeginInline();
        p.Button("Next edge").OnClick(NextEdge);
        p.Button("Align").OnClick(NextAlign);
        p.Button("Width").OnClick(ToggleWidth);
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
