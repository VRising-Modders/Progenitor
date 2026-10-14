using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// P7: the handle half every composed widget shares (ordered list, picker, tabs). A composed widget is a set of kit
/// rows on one panel; this class gives it the <see cref="IUiHandle"/> contract: identity from the panel, one
/// <see cref="Visible"/> switch over its rows, <see cref="Interactable"/> as an input gate, and a dispose that releases
/// every row, the tooltip it showed and any frame tick it holds. Handlers a plugin registers are called guarded: a throw
/// is logged with the widget's key and never escapes into a Unity callback.
/// </summary>
internal abstract class UiComposedHandle : IUiHandle
{
    private readonly List<IUiHandle> _parts = new();
    private readonly List<IDisposable> _leases = new();
    private bool _visible = true;
    private bool _interactable = true;
    private bool _disposed;
    private string _text;

    protected UiComposedHandle(IUiPanel panel, string key, string text)
    {
        Panel = panel ?? throw new ArgumentNullException(nameof(panel));
        Key = string.IsNullOrWhiteSpace(key) ? "widget" : key.Trim();
        _text = text ?? "";
    }

    protected IUiPanel Panel { get; }

    /// <summary>The caller's key: the prefix of every row key this widget makes (the bridge's handle).</summary>
    internal string Key { get; }

    /// <summary>A row key under this widget: <c>key:part</c>.</summary>
    protected string RowKey(string part) => Key + ":" + part;

    /// <summary>Keep a row (or any handle) as part of this widget; disposed with it.</summary>
    protected T Part<T>(T handle) where T : IUiHandle
    {
        if (handle != null) _parts.Add(handle);
        return handle!;
    }

    /// <summary>Keep a lease (a click subscription, a pointer watch, a frame tick); disposed with the widget.</summary>
    protected void Lease(IDisposable? lease)
    {
        if (lease != null) _leases.Add(lease);
    }

    protected IReadOnlyList<IUiHandle> Parts => _parts;

    public virtual string Text
    {
        get => _text;
        set => _text = value ?? "";
    }

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_disposed || _visible == value) return;
            _visible = value;
            if (!value)
            {
                foreach (var part in _parts) TrySetVisible(part, false);
                OnHidden();
            }
            else
            {
                Render();
            }
        }
    }

    public bool Enabled => !_disposed && Panel.Enabled;

    public bool IsDisposed => _disposed;

    public UiHandleState State =>
        _disposed ? UiHandleState.Disposed
        : !Panel.Enabled ? UiHandleState.Disabled
        : Panel.State == UiHandleState.Shown ? UiHandleState.Shown
        : UiHandleState.Built;

    public string PluginId => Panel.PluginId;

    public string RootName => Panel.RootName;

    public string Align => "left";

    public bool Interactable
    {
        get => _interactable;
        set
        {
            if (_interactable == value) return;
            _interactable = value;
            foreach (var part in _parts)
            {
                try { part.Interactable = value; }
                catch { /* a part that cannot gray keeps its look; input is still gated here */ }
            }
        }
    }

    /// <summary>Input is acted on: not disposed, interactable and shown.</summary>
    protected bool Live => !_disposed && _interactable && _visible;

    /// <summary>Repaint every row from the model. Called on show and after every change.</summary>
    protected abstract void Render();

    /// <summary>The widget was hidden: drop transient state (a drag, a tooltip).</summary>
    protected virtual void OnHidden() => HideTooltip();

    protected virtual void OnDisposing() { }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { OnDisposing(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: widget '" + Key + "' dispose threw: " + e.Message); }
        HideTooltip();
        for (int i = _leases.Count - 1; i >= 0; i--)
        {
            try { _leases[i].Dispose(); }
            catch { /* */ }
        }

        _leases.Clear();
        for (int i = _parts.Count - 1; i >= 0; i--)
        {
            try { _parts[i].Dispose(); }
            catch { /* */ }
        }

        _parts.Clear();
    }

    protected static void TrySetVisible(IUiHandle part, bool visible)
    {
        try
        {
            if (part.Visible != visible) part.Visible = visible;
        }
        catch { /* */ }
    }

    // ---- Tooltip ---------------------------------------------------------------------------

    private bool _tooltipShown;

    /// <summary>Show the kit tooltip beside <paramref name="anchor"/>; nothing when either text is empty.</summary>
    protected void ShowTooltip(ScreenRect anchor, string title, string body)
    {
        if (_disposed || (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body)) || !anchor.IsKnown) return;
        var runtime = WidgetRuntime.Current;
        if (runtime == null) return;
        try
        {
            runtime.ShowTooltip(anchor, title ?? "", body ?? "");
            _tooltipShown = true;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: widget '" + Key + "' tooltip threw: " + e.Message); }
    }

    protected void HideTooltip()
    {
        if (!_tooltipShown) return;
        _tooltipShown = false;
        try { WidgetRuntime.Current?.HideTooltip(); }
        catch { /* */ }
    }

    // ---- Guarded plugin handlers -------------------------------------------------------------

    /// <summary>Call every handler in <paramref name="handlers"/>, each guarded.</summary>
    protected void Raise<T>(List<Action<T>> handlers, T value, string what)
    {
        foreach (var h in handlers.ToArray())
        {
            try { h(value); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: widget '" + Key + "' " + what + " handler threw: " + e.Message); }
        }
    }

    protected static IDisposable Subscribe<T>(List<T> handlers, T handler) where T : class
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        handlers.Add(handler);
        return new ActionDisposable(() => handlers.Remove(handler));
    }

    /// <summary>The visible readback rect of a cell row (or one of its cells), screen pixels.</summary>
    protected static ScreenRect RectOf(IUiCellRow? row, string? cellKey = null) =>
        row is UiCellRowHandle h ? h.NavRect(cellKey) : ScreenRect.Unknown;

    /// <summary>The runtime row behind a cell row handle (for a pointer watch), or null.</summary>
    protected static IRuntimeCellRow? RuntimeRowOf(IUiCellRow? row) =>
        row is UiCellRowHandle h ? h.NavWidget(null) as IRuntimeCellRow : null;
}
