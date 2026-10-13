using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// SETTINGS1 review finding 3: the panel a mod's <see cref="ModMenuRegistration.Header"/> builder
/// gets. It can add rows (every row-adding member of <see cref="IUiPanel"/>) and nothing else:
/// <c>Show</c> / <c>Hide</c> / <c>Dispose</c> are no-ops, the <c>Text</c> / <c>Visible</c> /
/// <c>Footer</c> / <c>CaptureMode</c> setters are ignored, and the kit's internal blocks (cell rows,
/// head blocks) are not reachable through it. Every row it adds is recorded, so a builder that
/// throws is rolled back before the default header is drawn, and an unclosed
/// <see cref="BeginInline"/> is closed by <see cref="Finish"/>.
/// </summary>
internal sealed class ModHeaderPanel : IUiPanel
{
    private readonly IUiPanel _inner;
    private readonly List<IUiHandle> _added = new();
    private readonly string _alignment;
    private int _inlineDepth;
    private bool _finished;

    internal ModHeaderPanel(IUiPanel inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _alignment = inner.ContentAlignment;
    }

    /// <summary>The rows the builder added, in order.</summary>
    internal IReadOnlyList<IUiHandle> Added => _added;

    private T Track<T>(T handle) where T : IUiHandle
    {
        if (handle != null) _added.Add(handle);
        return handle;
    }

    /// <summary>
    /// Rows are added only while the builder runs: a kept reference used later (a timer, a callback)
    /// would append below the Settings section, outside the header and outside rollback.
    /// </summary>
    private IUiPanel Open =>
        _finished
            ? throw new InvalidOperationException("Header rows can only be added while the Header builder runs; keep the returned handles to update them later.")
            : _inner;

    public IUiButton Button(string text) => Track(Open.Button(text));
    public IUiToggle Toggle(string text, bool initial = false) => Track(Open.Toggle(text, initial));
    public IUiLabel Label(string text) => Track(Open.Label(text));
    public IUiSlider Slider(string text, float min, float max, float initial) => Track(Open.Slider(text, min, max, initial));
    public IUiInput Input(string text, string initial = "") => Track(Open.Input(text, initial));
    public IUiLabel Heading(string text) => Track(Open.Heading(text));
    public IUiLabel Note(string text) => Track(Open.Note(text));
    public IUiLabel Fact(string name, string value) => Track(Open.Fact(name, value));

    public IUiDropdown Dropdown(string text, IReadOnlyList<string> values, string value, IReadOnlyList<string>? labels = null) =>
        Track(Open.Dropdown(text, values, value, labels));

    public IUiDropdown Dropdown(
        string text, IReadOnlyList<string> values, string value, IReadOnlyList<string>? labels, IReadOnlyList<string>? descriptions) =>
        Track(Open.Dropdown(text, values, value, labels, descriptions));

    public IUiNumber Number(string text, double value, double min, double max, double step = 1, int decimals = 0, string units = "") =>
        Track(Open.Number(text, value, min, max, step, decimals, units));

    public IUiSliderField SliderField(string text, float min, float max, float step, float value) =>
        Track(Open.SliderField(text, min, max, step, value));

    public IUiColorField ColorField(string text, string hex, bool alpha = false) => Track(Open.ColorField(text, hex, alpha));

    public IUiKeyField KeyField(string text, string pluginId, string bindId) => Track(Open.KeyField(text, pluginId, bindId));

    // SETTINGS1 stage 2: the row-adding controls added to IUiPanel. Forwarded explicitly — the
    // interface's default bodies would build disabled handles, because this wrapper is not a host.
    public IUiInput TextArea(string text, string initial = "", int lines = 3) => Track(Open.TextArea(text, initial, lines));

    public IUiRadio Radio(string text, IReadOnlyList<string> values, string value, IReadOnlyList<string>? labels = null) =>
        Track(Open.Radio(text, values, value, labels));

    public IUiRadio Radio(
        string text, IReadOnlyList<string> values, string value, IReadOnlyList<string>? labels, IReadOnlyList<string>? descriptions) =>
        Track(Open.Radio(text, values, value, labels, descriptions));

    public IUiMultiSelect MultiSelect(
        string text, IReadOnlyList<string> values, IEnumerable<string>? selected, IReadOnlyList<string>? labels = null) =>
        Track(Open.MultiSelect(text, values, selected, labels));

    public IUiInfo Info(string text, Func<string> value, double refreshSeconds = 1) => Track(Open.Info(text, value, refreshSeconds));

    public IUiLabel Divider() => Track(Open.Divider());

    public IUiButton ConfirmButton(string text, string question) => Track(Open.ConfirmButton(text, question));

    public IUiButton ConfirmButton(string text, string question, string body) => Track(Open.ConfirmButton(text, question, body));

    /// <summary>The page's own panel, for seams that need the host (a confirm button's dialog).</summary>
    internal IUiPanel Inner => _inner;

    public void BeginInline()
    {
        Open.BeginInline();
        _inlineDepth++;
    }

    public void EndInline()
    {
        if (_inlineDepth == 0) return;
        _inner.EndInline();
        _inlineDepth--;
    }

    /// <summary>Alignment of the header's own rows. Restored for the page by <see cref="Finish"/>.</summary>
    public string ContentAlignment
    {
        get => _inner.ContentAlignment;
        set => _inner.ContentAlignment = value;
    }

    // The page's own state: readable, never writable from a header.
    public string Text { get => _inner.Text; set { } }
    public bool Visible { get => _inner.Visible; set { } }
    public bool Enabled => _inner.Enabled;
    public bool IsDisposed => _inner.IsDisposed;
    public UiHandleState State => _inner.State;
    public string PluginId => _inner.PluginId;
    public string RootName => _inner.RootName;
    public string Align => _inner.Align;
    public UiCaptureMode CaptureMode { get => _inner.CaptureMode; set { } }
    public string Footer { get => _inner.Footer; set { } }
    public void Show() { }
    public void Hide() { }
    public void Dispose() { }

    /// <summary>
    /// After the builder: close any inline cluster it left open and put the page's alignment back.
    /// Returns how many clusters had to be closed.
    /// </summary>
    internal int Finish()
    {
        _finished = true;
        int closed = 0;
        while (_inlineDepth > 0)
        {
            try { _inner.EndInline(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: header inline close threw: " + e.Message); }
            _inlineDepth--;
            closed++;
        }

        try { _inner.ContentAlignment = _alignment; }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: header alignment restore threw: " + e.Message); }
        return closed;
    }

    /// <summary>A builder threw: dispose every row it added, newest first, then <see cref="Finish"/>.</summary>
    internal int RollBack()
    {
        int removed = 0;
        for (int i = _added.Count - 1; i >= 0; i--)
        {
            try
            {
                _added[i].Dispose();
                removed++;
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: header rollback dispose threw: " + e.Message); }
        }

        _added.Clear();
        Finish();
        return removed;
    }
}
