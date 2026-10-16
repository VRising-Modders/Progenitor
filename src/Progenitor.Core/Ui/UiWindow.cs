using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// WINDOWS1: a standard window (<see cref="UiWindowPreset"/>) made by <c>ctx.Windows.Create</c> or <see cref="Ui.Window"/>.
/// Add rows to <see cref="Panel"/> (and, in a Tabbed window, to <see cref="Info"/>) with the ordinary kit calls, put
/// each row on a tab with <see cref="Add{T}"/>, then <see cref="Show"/>. Dispose closes it and releases every row; the
/// plugin's unload does the same.
/// </summary>
public interface IUiWindow : IDisposable
{
    /// <summary>The preset this window was made with.</summary>
    UiWindowPreset Preset { get; }

    /// <summary>The title in the title band. Setting it redraws the title (Rename does this too).</summary>
    string Title { get; set; }

    /// <summary>The content: add rows here with the ordinary <see cref="IUiPanel"/> calls and the widgets.</summary>
    IUiPanel Panel { get; }

    /// <summary>The tab row, or null when the window has none.</summary>
    IUiTabs? Tabs { get; }

    /// <summary>Tabbed: the info column on the left; null in the other presets.</summary>
    IUiWindowColumn? Info { get; }

    /// <summary>The toolbar's buttons, in the order of <see cref="UiWindowOptions.Tools"/> (Rename not included).</summary>
    IReadOnlyList<IUiButton> Tools { get; }

    /// <summary>The version stamp at the bottom; empty for none. Settable at any time.</summary>
    string VersionStamp { get; set; }

    /// <summary>True while the window is on screen (derived from the panel's state, so an Escape that closed it reads false).</summary>
    bool IsOpen { get; }

    /// <summary>True after <see cref="IDisposable.Dispose"/>.</summary>
    bool IsDisposed { get; }

    /// <summary>Make <paramref name="content"/> part of tab <paramref name="tab"/> (shown only while it is selected); no tab row: returned unchanged.</summary>
    T Add<T>(int tab, T content) where T : IUiHandle;

    /// <summary>After the player renamed the window with its Rename button; the handler gets the new title (already applied).</summary>
    IDisposable OnRename(Action<string> handler);

    /// <summary>Put the window on screen.</summary>
    void Show();

    /// <summary>Take the window off the screen; it can be shown again.</summary>
    void Hide();

    /// <summary>Show when closed, hide when open.</summary>
    void Toggle();

    /// <summary>Readback: preset, title, open, stamp, the spec's geometry, the tabs and the tools.</summary>
    Dictionary<string, object?> Describe();
}

/// <summary>
/// WINDOWS1: a Tabbed window's info column (<see cref="IUiWindow.Info"/>): a narrow column of text and buttons on the left
/// that scrolls on its own when it is longer than its clip (the wheel, the right stick). Put a row on one tab with <see cref="IUiWindow.Add{T}"/>, as for content.
/// </summary>
public interface IUiWindowColumn
{
    /// <summary>A section heading.</summary>
    IUiLabel Heading(string text);

    /// <summary>A muted line of explanation.</summary>
    IUiLabel Note(string text);

    /// <summary>A line in a status tone.</summary>
    IUiToneLabel Label(string text, LabelTone tone = LabelTone.Normal);

    /// <summary>
    /// A name and its value on one line: the name at the left (shortened with an ellipsis when the column is too narrow),
    /// the value right-aligned (a figure in <see cref="UiTextStyle.Number"/>, other words in <see cref="UiTextStyle.Body"/>).
    /// Set the value later with <see cref="IUiHandle.Text"/>.
    /// </summary>
    IUiLabel Fact(string name, string value);

    /// <summary>A button (the game's compact button).</summary>
    IUiButton Button(string text);
}

/// <summary>WINDOWS1: the window composed over one Progenitor panel. Game-free: the runtime draws the chrome it is asked for.</summary>
internal sealed class UiWindowHandle : IUiWindow
{
    internal const string TabsKey = "window-tabs";

    private readonly object _owner;

    /// <summary>The owner the window was made with; null for an ownerless <see cref="Ui.Window"/>.</summary>
    private readonly object? _ownerGiven;

    /// <summary>
    /// Ownerless windows (<see cref="Ui.Window"/> without an owner), weakly: a plugin's unload sweep drops the OnRename
    /// handlers it left on them (<see cref="DropHandlersOf"/>).
    /// </summary>
    private static readonly List<WeakReference<UiWindowHandle>> Ownerless = new();
    private readonly string _pluginId;
    private readonly string _stampFooter;
    private readonly IUiPanel _panel;
    private IUiTabs? _tabs;
    private WindowColumn? _info;
    private readonly List<IUiButton> _tools = new();
    private IUiButton? _rename;
    /// <summary>
    /// The OnRename handlers, each with the name (a string, never the assembly) of the plugin assembly that declared it,
    /// so an ownerless window's Rename prompt and handlers still go with that plugin's unload sweep (<see cref="BelongsTo"/>).
    /// </summary>
    private readonly List<(Action<string> Handler, string? Source)> _renamed = new();
    private readonly UiWindowSpec _spec;
    private IDisposable? _renameDialog;
    private bool _disposed;

    internal UiWindowHandle(
        UiWindowPreset preset, string title, object? owner, string? pluginId, string modName, string modVersion,
        UiWindowOptions? options)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Title is required.", nameof(title));
        _spec = UiWindowLayout.Spec(preset);
        var opts = options ?? new UiWindowOptions();
        var tabs = UiWindowLayout.TabsFor(preset, title, opts);
        string? refused = UiWindowLayout.RefuseTabs(tabs) ?? (_spec.ToolbarAllowed
            ? UiWindowLayout.RefuseTools(opts.Tools, opts.Renamable, UiWindowLayout.ToolbarRowWidth(_spec))
            : null);
        if (refused != null) throw new ArgumentException(refused, nameof(options));

        Preset = preset;
        _ownerGiven = owner;
        _owner = owner ?? this;
        if (owner == null)
            lock (Ownerless)
            {
                Ownerless.RemoveAll(w => !w.TryGetTarget(out _));
                Ownerless.Add(new WeakReference<UiWindowHandle>(this));
            }
        _panel = Ui.Panel(title.Trim(), owner, pluginId);
        _pluginId = _panel.PluginId;
        _stampFooter = UiWindowLayout.StampText(modName, modVersion, Ui.FrameworkFooter, frameWidth: _spec.FrameWidth);

        try { Compose(opts, tabs); }
        catch
        {
            // A window that failed half-built gives its panel back instead of leaving it to the owner sweep.
            try { _panel.Dispose(); } catch { /* already gone */ }
            throw;
        }
    }

    private void Compose(UiWindowOptions opts, IReadOnlyList<string> tabs)
    {
        _panel.GameWindow();
        // Text is left-aligned in every standard window (titles center themselves; numbers align right).
        _panel.ContentAlignment = SettingsAlign.Left;
        Ui.WindowChrome(_panel, _spec);
        _captureFixed = opts.CaptureMode.HasValue;
        _panel.CaptureMode = opts.CaptureMode ?? _spec.CaptureMode;
        _panel.Footer = !opts.VersionStamp ? "" : opts.VersionText.Trim().Length > 0 ? opts.VersionText.Trim() : _stampFooter;

        if (UiWindowLayout.HasToolbar(Preset, opts))
        {
            Ui.PinMenuRows(_panel, MenuRowPin.Top);
            try
            {
                _panel.BeginInline();
                foreach (var tool in opts.Tools ?? Array.Empty<UiWindowTool>())
                {
                    var button = _panel.Button(tool.Caption.Trim());
                    if (tool.Tooltip.Length > 0) button.Tooltip(tool.Tooltip);
                    _tools.Add(button);
                }

                if (opts.Renamable)
                {
                    _rename = _panel.Button(UiWindowLayout.RenameCaption);
                    _rename.Tooltip("Give this window a new name.");
                    _rename.OnClick(AskRename);
                }

                _panel.EndInline();
            }
            finally { Ui.PinMenuRows(_panel, MenuRowPin.Content); }
        }

        if (tabs.Count > 0)
        {
            Ui.PinMenuRows(_panel, MenuRowPin.Top);
            try { _tabs = _panel.Tabs(TabsKey, tabs, opts.SelectedTab); }
            finally { Ui.PinMenuRows(_panel, MenuRowPin.Content); }
        }

        if (_spec.InfoColumnWidth > 0f) _info = new WindowColumn(_panel);
    }

    public UiWindowPreset Preset { get; }

    public string Title
    {
        get => _panel.Text;
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            _panel.Text = value.Trim();
            // A panel's title write does not lay the panel out again; the ornaments follow the title's new width.
            Ui.WindowChrome(_panel, _spec);
        }
    }

    public IUiPanel Panel => _panel;

    public IUiTabs? Tabs => _tabs;

    public IUiWindowColumn? Info => _info;

    public IReadOnlyList<IUiButton> Tools => _tools;

    public string VersionStamp
    {
        get => _panel.Footer;
        set => _panel.Footer = value ?? "";
    }

    public bool IsOpen => !_disposed && !_panel.IsDisposed && _panel.State == UiHandleState.Shown;

    public bool IsDisposed => _disposed;

    public T Add<T>(int tab, T content) where T : IUiHandle => _tabs == null ? content : _tabs.Add(tab, content);

    public IDisposable OnRename(Action<string> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var entry = (handler, PluginDelegateSource.Name(handler));
        _renamed.Add(entry);
        return new ActionDisposable(() => _renamed.Remove(entry));
    }

    public void Show()
    {
        if (_disposed) return;
        // A Basic window leaves the input to the game, except for a player who is navigating with a controller or the
        // keyboard when it opens (they pressed A on the button that opened it): it then takes the input while shown, so the
        // controller moves into it (NAV1 navigates only WhileShown windows) and B closes it.
        if (_spec.ControllerHandoff && !_captureFixed && !IsOpen)
            _panel.CaptureMode = UiWindowLayout.CaptureOnShow(_spec, NavigatingNow());
        _panel.Show();
    }

    /// <summary>Whether the player is navigating with a controller or keys now (<see cref="UiNav.Marked"/>); tests replace it.</summary>
    internal static Func<bool> NavigatingNow { get; set; } = () => UiNav.Marked;

    private bool _captureFixed;

    public void Hide()
    {
        if (_disposed) return;
        _panel.Hide();
    }

    public void Toggle()
    {
        if (IsOpen) Hide();
        else Show();
    }

    /// <summary>
    /// Whether this window (the owner of its Rename prompt) belongs to <paramref name="owner"/>, which is unloading: the
    /// owner it was made with, or for an ownerless window a plugin whose OnRename handler it holds, matched by assembly
    /// inside that plugin's unload sweep only (<see cref="PluginDelegateSource"/>, as PINS1 matches unowned watches).
    /// <see cref="UiDialog.DropOwner"/> asks this for a dialog a window owns.
    /// </summary>
    internal bool BelongsTo(object? owner)
    {
        if (owner == null) return false;
        if (_ownerGiven != null) return ReferenceEquals(_ownerGiven, owner);
        string? name = PluginDelegateSource.OwnerName(owner);
        foreach (var entry in _renamed)
            if (PluginDelegateSource.Matches(entry.Source, name)) return true;
        return false;
    }

    /// <summary>The plugins whose handlers this window holds, each once (readback for tests).</summary>
    internal IReadOnlyList<string> HandlerSources
    {
        get
        {
            var list = new List<string>();
            foreach (var entry in _renamed)
                if (entry.Source != null && !list.Contains(entry.Source)) list.Add(entry.Source);
            return list;
        }
    }

    /// <summary>Drop this window's OnRename handlers declared in the assembly named <paramref name="assemblyName"/>; how many.</summary>
    internal int DropHandlersFrom(string? assemblyName)
    {
        if (assemblyName == null) return 0;
        return _renamed.RemoveAll(e => PluginDelegateSource.Matches(e.Source, assemblyName));
    }

    /// <summary>
    /// WINDOWS1 review: inside <paramref name="owner"/>'s unload sweep (<c>Ui.RemoveAll</c>), drop the OnRename handlers
    /// that plugin left on ownerless windows, so they do not keep its delegates (and its load context) alive. Matches by
    /// the handler's assembly only inside the sweep (<see cref="PluginDelegateSource.OwnerName"/>); otherwise nothing.
    /// </summary>
    internal static int DropHandlersOf(object owner)
    {
        string? name = PluginDelegateSource.OwnerName(owner);
        if (name == null) return 0;
        int dropped = 0;
        lock (Ownerless)
        {
            Ownerless.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var weak in Ownerless)
                if (weak.TryGetTarget(out var window)) dropped += window.DropHandlersFrom(name);
        }

        return dropped;
    }

    /// <summary>The Rename button's click (tests press it here).</summary>
    internal void AskRename()
    {
        if (_disposed) return;
        _renameDialog?.Dispose();
        _renameDialog = UiDialogs.Prompt(
            _owner, _pluginId, _stampFooter.Length > 0 ? _stampFooter : Ui.FrameworkFooter,
            "Rename", "", Title, Rename, "", s => string.IsNullOrWhiteSpace(s) ? "Type a name." : null,
            UiWindowLayout.RenameCaption, 40, "Cancel", null);
    }

    /// <summary>Apply a new title and tell the OnRename handlers (the prompt's answer).</summary>
    internal void Rename(string title)
    {
        if (_disposed || string.IsNullOrWhiteSpace(title)) return;
        Title = title;
        foreach (var entry in _renamed.ToArray())
        {
            try { entry.Handler(Title); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: window rename handler threw: " + e.Message); }
        }
    }

    public Dictionary<string, object?> Describe()
    {
        var tabs = new List<string>();
        if (_tabs != null) tabs.AddRange(_tabs.Captions);
        var tools = new List<string>();
        foreach (var t in _tools) tools.Add(t.Text);
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = "window",
            ["preset"] = Preset.ToString(),
            ["title"] = Title,
            ["open"] = IsOpen,
            ["stamp"] = VersionStamp,
            ["frameWidth"] = _spec.FrameWidth,
            ["headerBand"] = _spec.HeaderBand,
            ["infoColumn"] = _spec.InfoColumnWidth,
            ["capture"] = _panel.CaptureMode.ToString(),
            ["tabs"] = tabs,
            ["selectedTab"] = _tabs?.Selected ?? -1,
            ["tools"] = tools,
            ["rename"] = _rename != null,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renamed.Clear();
        if (_ownerGiven == null)
            lock (Ownerless) Ownerless.RemoveAll(w => !w.TryGetTarget(out var t) || ReferenceEquals(t, this));
        try { _renameDialog?.Dispose(); } catch { /* the dialog goes with the plugin anyway */ }
        _renameDialog = null;
        try { _panel.Hide(); } catch { /* already gone */ }
        _panel.Dispose();
    }

    /// <summary>The info column: each row pinned to the side band, then the pin given back to the content.</summary>
    private sealed class WindowColumn : IUiWindowColumn
    {
        private readonly IUiPanel _panel;

        internal WindowColumn(IUiPanel panel) => _panel = panel;

        private T Side<T>(Func<T> add)
        {
            Ui.PinMenuRows(_panel, MenuRowPin.Side);
            try { return add(); }
            finally { Ui.PinMenuRows(_panel, MenuRowPin.Content); }
        }

        public IUiLabel Heading(string text) => Side(() => _panel.Heading(text ?? ""));

        public IUiLabel Note(string text) => Side(() => _panel.Note(text ?? ""));

        public IUiToneLabel Label(string text, LabelTone tone = LabelTone.Normal) => Side(() => _panel.Label(text ?? "", tone));

        // WINDOWS1 live fix: a fact in the narrow column is a name-and-value cell row (the name fills and is shortened with an
        // ellipsis, the value is sized to its text and right-aligned on one line), never the settings grid's caption and
        // control columns, whose 560-wide ruler broke "Chests" into "Chest / s" here.
        public IUiLabel Fact(string name, string value) =>
            Side(() => (IUiLabel)new UiStyledCells(_panel, name ?? "", value ?? "", UiWindowLayout.InfoValueStyle(value)));

        public IUiButton Button(string text) => Side(() => _panel.Button(text ?? ""));
    }
}
