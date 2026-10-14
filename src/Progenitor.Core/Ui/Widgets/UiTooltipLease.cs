using System;

namespace Progenitor.Ui;

/// <summary>
/// P7: <see cref="UiWidgets.Tooltip"/> on any kit handle: a hover watch on the control's own object that shows the kit
/// tooltip beside it while the pointer is over it. The watch is owner-tagged (released by <c>Ui.RemoveAll</c> and every
/// Core teardown like any pointer watch); disposing the lease removes it sooner.
/// </summary>
internal sealed class UiTooltipLease : IDisposable
{
    private readonly UiHandle _handle;
    private readonly string _title;
    private readonly string _text;
    private IDisposable? _watch;
    private bool _shown;
    private bool _disposed;

    private UiTooltipLease(UiHandle handle, string title, string text)
    {
        _handle = handle;
        _title = title;
        _text = text;
    }

    internal static IDisposable Attach(IUiHandle handle, string title, string text)
    {
        if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(title)) return Noop.Instance;
        if (handle is not UiHandle kit || kit.IsDisposed || !kit.Enabled || kit.Runtime == null) return Noop.Instance;
        var runtime = WidgetRuntime.Current;
        if (runtime == null) return Noop.Instance;
        var lease = new UiTooltipLease(kit, title, text);
        try { lease._watch = runtime.WatchHover(kit.Runtime, lease.Hover, kit.Owner); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: tooltip on '" + kit.Text + "' failed: " + e.Message); }
        return lease._watch == null ? Noop.Instance : lease;
    }

    /// <summary>The pointer entered or left the control.</summary>
    internal void Hover(bool on)
    {
        var runtime = WidgetRuntime.Current;
        if (runtime == null) return;
        if (!on || _disposed || _handle.IsDisposed || !_handle.Visible)
        {
            Hide(runtime);
            return;
        }

        ScreenRect rect;
        try { rect = _handle.Runtime == null ? ScreenRect.Unknown : Ui.CurrentRuntime().WidgetRect(_handle.Runtime); }
        catch { rect = ScreenRect.Unknown; }
        if (!rect.IsKnown) return;
        try
        {
            runtime.ShowTooltip(rect, _title, _text);
            _shown = true;
        }
        catch { /* */ }
    }

    private void Hide(IWidgetRuntime runtime)
    {
        if (!_shown) return;
        _shown = false;
        try { runtime.HideTooltip(); }
        catch { /* */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var runtime = WidgetRuntime.Current;
        if (runtime != null) Hide(runtime);
        try { _watch?.Dispose(); }
        catch { /* */ }
        _watch = null;
    }
}
