using System;
using System.Collections.Generic;
using System.Threading;

namespace Progenitor.Ui;

/// <summary>
/// Per-panel overlay canvas (Canvas + GraphicRaycaster). Destroyed with the panel on
/// Close/Dispose (R25 #8), not only on RemoveAll / Uninstall.
/// </summary>
internal interface IHasOverlay
{
    IRuntimeWidget? Overlay { get; }
}

internal abstract class UiHandle : IUiHandle
{
    private int _disposed;

    protected UiHandle(
        string pluginId,
        string rootName,
        object? owner,
        IRuntimeWidget? runtime,
        bool enabled)
    {
        PluginId = pluginId ?? UiOwnership.UnownedId;
        RootName = rootName ?? UiOwnership.RootName(pluginId);
        Owner = owner;
        Runtime = runtime;
        Enabled = enabled && runtime != null;
        State = Enabled ? UiHandleState.Built : UiHandleState.Disabled;
    }

    public string PluginId { get; }

    public string RootName { get; }

    public string Align { get; internal set; } = SettingsAlign.Centre;

    public bool Enabled { get; protected set; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public UiHandleState State { get; protected set; }

    internal object? Owner { get; }

    internal IRuntimeWidget? Runtime { get; }

    internal ushort WidgetId { get; set; }

    /// <summary>
    /// INLINEAPPLY1: the button style this handle asked for: <see cref="UiSettingsGrid.GameButtonStyle"/> once
    /// <see cref="UiSettingsControls.GameButton"/> ran, else empty (the kit's). The readback's fallback when the runtime
    /// cannot say which art it drew.
    /// </summary>
    internal string ButtonStyle { get; set; } = "";

    /// <summary>NOPROGMSG1: the muted hint an empty text field shows (<see cref="UiSettingsGrid.SetPlaceholder"/>); empty for none.</summary>
    internal string Placeholder { get; set; } = "";

    public string Text
    {
        get => Runtime is { Alive: true } r ? r.Text : "";
        set
        {
            if (Runtime is { Alive: true } r)
                r.Text = value ?? "";
        }
    }

    public bool Visible
    {
        get => Runtime is { Alive: true } r && r.Visible;
        set
        {
            if (Runtime is { Alive: true } r)
                r.Visible = value;
        }
    }

    private bool _interactable = true;

    /// <summary>
    /// SETTINGS1 stage 2 (<see cref="IUiHandle.Interactable"/>): held here so a disabled handle and the
    /// readback still answer; written through to the runtime widget, which grays it in place.
    /// </summary>
    public bool Interactable
    {
        get => _interactable;
        set
        {
            _interactable = value;
            if (Runtime is not { Alive: true } r) return;
            try { r.Interactable = value; }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: interactable write threw: " + e.Message); }
        }
    }

    /// <summary>SETTINGS1 stage 2: the help text drawn under this row's caption (<see cref="UiSettingsGrid.SetHelp"/>).</summary>
    internal string Help { get; set; } = "";

    internal void MarkShown()
    {
        if (!IsDisposed && Enabled)
            State = UiHandleState.Shown;
    }

    internal void MarkHidden()
    {
        if (!IsDisposed && Enabled)
            State = UiHandleState.Built;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { OnDispose(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: handle dispose threw: " + e.Message);
        }

        State = UiHandleState.Disposed;
        Enabled = false;
        Ui.Forget(this);
    }

    protected virtual void OnDispose()
    {
        if (WidgetId != 0)
        {
            try { Ui.CurrentRuntime().Unwire(WidgetId); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: Unwire threw: " + e.Message); }
            UiSlotTable.Release(WidgetId);
            WidgetId = 0;
        }

        try { Runtime?.Release(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: runtime Release threw: " + e.Message); }
    }
}

/// <summary>
/// Job LIST4 item 3: scroll a block of rows into view. The measurement and the write are Unity
/// (a <c>ScrollRect</c>'s content offset and two <c>RectTransform</c>s), so the runtime installs
/// itself here as a hook rather than this being a member of <see cref="IUiRuntime"/> — a pane can
/// then ask for a reveal from game-free code, and a suite with no Unity loaded simply gets false.
/// The arithmetic between the measurement and the write is
/// <see cref="MenuPanelLayout.RevealOffsetY"/>, which is where the rule is tested.
/// </summary>
internal static class UiReveal
{
    /// <summary>
    /// Scroll <paramref name="panel"/> so the band from <paramref name="first"/>'s top to
    /// <paramref name="last"/>'s bottom is visible, and report the content-pixel geometry it
    /// decided from. False when there is nothing to measure (no scroll view, no Unity).
    /// </summary>
    internal delegate bool RevealHook(
        IRuntimePanel panel,
        IRuntimeWidget first,
        IRuntimeWidget last,
        bool toTop,
        out float rowTop,
        out float viewportTop,
        out float viewportHeight);

    /// <summary>Installed by the Unity runtime; null in a game-free process.</summary>
    internal static RevealHook? Hook { get; set; }

    /// <summary>
    /// INSTALLSIDES2: measure a row's top from the list viewport's top edge (<paramref name="rowY"/>,
    /// px downward) and, when <paramref name="write"/>, move the list so that distance becomes
    /// <paramref name="wantedY"/> (<see cref="MenuPanelLayout.AnchorOffset"/>). <paramref name="offset"/>
    /// is the content offset the list holds afterwards.
    /// </summary>
    internal delegate bool AnchorHook(
        IRuntimePanel panel, IRuntimeWidget row, bool write, float wantedY, out float rowY, out float offset);

    /// <inheritdoc cref="AnchorHook"/>
    internal static AnchorHook? Anchor { get; set; }

    /// <inheritdoc cref="AnchorHook"/>
    internal static bool TryAnchor(IUiPanel? panel, IUiHandle? row, bool write, float wantedY, out float rowY, out float offset)
    {
        rowY = 0f;
        offset = 0f;
        var hook = Anchor;
        if (hook == null) return false;
        if (panel is not UiPanelHandle host || host.RuntimePanel is not { } runtimePanel) return false;
        if (row is not UiHandle a || a.IsDisposed || a.Runtime is not { Alive: true } widget) return false;
        try { return hook(runtimePanel, widget, write, wantedY, out rowY, out offset); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: row anchor threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// INSTALLUX1 item 7: read how far down its content a panel's list is scrolled, without moving
    /// it. Installed by the Unity runtime beside <see cref="Hook"/>; a game-free process gets false.
    /// </summary>
    internal delegate bool OffsetHook(IRuntimePanel panel, out float offsetY);

    /// <inheritdoc cref="OffsetHook"/>
    internal static OffsetHook? Offset { get; set; }

    /// <summary>
    /// INSTALLUX1 item 7: raised by the runtime after the wheel writes one notch onto a list, so a
    /// pane whose icon requests follow the scroll window can ask for the new window. Subscribers are
    /// per pane build and must detach on teardown; a listener that throws is swallowed here so one
    /// pane cannot break another's scrolling.
    /// </summary>
    internal static event Action? Scrolled;

    /// <summary>Raise <see cref="Scrolled"/>. Called by the runtime's wheel pump only.</summary>
    internal static void RaiseScrolled()
    {
        var handlers = Scrolled;
        if (handlers == null) return;
        foreach (Action handler in handlers.GetInvocationList())
        {
            try { handler(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: scroll listener threw: " + e.Message); }
        }
    }

    /// <inheritdoc cref="OffsetHook"/>
    internal static bool TryOffset(IUiPanel? panel, out float offsetY)
    {
        offsetY = 0f;
        var hook = Offset;
        if (hook == null) return false;
        if (panel is not UiPanelHandle host || host.RuntimePanel is not { } runtimePanel) return false;
        try { return hook(runtimePanel, out offsetY); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: scroll offset read threw: " + e.Message);
            offsetY = 0f;
            return false;
        }
    }

    internal static bool TryReveal(
        IUiPanel? panel,
        IUiHandle? first,
        IUiHandle? last,
        out float rowTop,
        out float viewportTop,
        out float viewportHeight) =>
        TryReveal(panel, first, last, toTop: false, out rowTop, out viewportTop, out viewportHeight);

    /// <summary>
    /// INSTALLSIDES1: as above; <paramref name="toTop"/> puts <paramref name="first"/>'s top on the
    /// viewport's top edge even when the block already fits (clamped to the content's end) — the
    /// Install drawer's open, which always brings its row to the top of the list.
    /// </summary>
    internal static bool TryReveal(
        IUiPanel? panel,
        IUiHandle? first,
        IUiHandle? last,
        bool toTop,
        out float rowTop,
        out float viewportTop,
        out float viewportHeight)
    {
        rowTop = 0f;
        viewportTop = 0f;
        viewportHeight = 0f;
        var hook = Hook;
        if (hook == null) return false;
        if (panel is not UiPanelHandle host || host.RuntimePanel is not { } runtimePanel) return false;
        if (first is not UiHandle a || a.IsDisposed || a.Runtime is not { Alive: true } firstWidget) return false;
        var lastHandle = last as UiHandle ?? a;
        var lastWidget = lastHandle.IsDisposed || lastHandle.Runtime is not { Alive: true } w ? firstWidget : w;
        try { return hook(runtimePanel, firstWidget, lastWidget, toTop, out rowTop, out viewportTop, out viewportHeight); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: reveal threw: " + e.Message);
            return false;
        }
    }
}

internal sealed class UiPanelHandle : UiHandle, IUiPanel, IUiControlHost
{
    private readonly List<UiHandle> _children = new();
    private readonly IUiRuntime _runtime;
    private readonly IRuntimePanel? _panel;
    private string _contentAlignment = SettingsAlign.Centre;
    private int _shown;

    /// <summary>
    /// This panel's own choice, or null to follow <see cref="Ui.CaptureMode"/> (job FUI19).
    /// </summary>
    private UiCaptureMode? _mode;

    /// <summary>
    /// The mode this panel was actually shown with. Frozen at Show and used at Hide, so a default
    /// that moves while a panel is open cannot make the Hide path detach something the Show path
    /// never attached — which would leave the gate holding a destroyed panel.
    /// </summary>
    private UiCaptureMode _shownMode = UiCaptureModes.Default;

    /// <summary>
    /// The slot this panel holds while it is shown (jobs FUI20, FUI21), or
    /// <see cref="UiPanelSlots.NoSlot"/>. Since FUI21 the slot is not a position — the position
    /// comes from this panel's rank in the shown set — it is the <i>order</i> that set is ranked
    /// in. Held for exactly as long as the panel is shown, and lowest-free, so the longest-open
    /// panel keeps the leftmost column and a panel opening beside it never makes it swap sides.
    /// </summary>
    private int _slot = UiPanelSlots.NoSlot;

    internal UiPanelHandle(
        string pluginId,
        string rootName,
        object? owner,
        IUiRuntime runtime,
        IRuntimePanel? panel,
        bool enabled)
        : base(pluginId, rootName, owner, panel, enabled)
    {
        _runtime = runtime;
        _panel = panel;
    }

    public IUiButton Button(string text) =>
        AddChild(UiSlotKind.Click,
            // CSAMPLE1: on a custom mod page (the settings grid's page) a button is the game's compact button at the
            // row's left edge, as the game's Back is; elsewhere the kit's full-row button.
            () => _panel == null ? null : GridControls ? _runtime.AddPageButton(_panel, text ?? "") : _runtime.AddButton(_panel, text ?? ""),
            (w, id) => new UiButtonHandle(PluginId, RootName, Owner, w, w != null, id));

    public IUiToggle Toggle(string text, bool initial = false) =>
        AddChild(UiSlotKind.Bool, () => _panel == null ? null : OnGrid(() => _runtime.AddToggle(_panel, text ?? "", initial)),
            (w, id) => new UiToggleHandle(PluginId, RootName, Owner, w, w != null, id));

    public IUiLabel Label(string text) =>
        AddChild(UiSlotKind.Click, () => _panel == null ? null : _runtime.AddLabel(_panel, text ?? ""),
            (w, id) => new UiLabelHandle(PluginId, RootName, Owner, w, w != null, id), wire: false);

    public IUiSlider Slider(string text, float min, float max, float initial) =>
        AddChild(UiSlotKind.Float, () => _panel == null ? null : OnGrid(() => _runtime.AddSlider(_panel, text ?? "", min, max, initial)),
            (w, id) => new UiSliderHandle(PluginId, RootName, Owner, w, w != null, id) { Min = min, Max = max });

    public IUiInput Input(string text, string initial = "") =>
        AddChild(UiSlotKind.Text, () => _panel == null ? null : OnGrid(() => _runtime.AddInput(_panel, text ?? "", initial ?? "")),
            (w, id) => new UiInputHandle(PluginId, RootName, Owner, w, w != null, id));

    public string ContentAlignment
    {
        get => _contentAlignment;
        set
        {
            _contentAlignment = SettingsAlign.Normalize(value);
            if (_panel == null) return;
            try { _runtime.SetContentAlignment(_panel, _contentAlignment); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: content alignment: " + e.Message); }
        }
    }

    public IUiLabel Heading(string text) =>
        AddChild(UiSlotKind.Click, () => _panel == null ? null : _runtime.AddHeading(_panel, text ?? ""),
            (w, id) => new UiLabelHandle(PluginId, RootName, Owner, w, w != null, id), wire: false);

    /// <summary>DIALOGS1: a dialog's centered, wrapping title (bold) or body (muted) line (<see cref="IUiRuntime.AddDialogText"/>).</summary>
    internal IUiLabel AddDialogText(string text, bool title) =>
        AddChild(UiSlotKind.Click, () => _panel == null ? null : _runtime.AddDialogText(_panel, text ?? "", title),
            (w, id) => new UiLabelHandle(PluginId, RootName, Owner, w, w != null, id), wire: false);

    /// <summary>DIALOGS3: a dialog's note under its facts (<see cref="IUiRuntime.AddDialogNote"/>).</summary>
    internal IUiLabel AddDialogNote(string text) =>
        AddChild(UiSlotKind.Click, () => _panel == null ? null : _runtime.AddDialogNote(_panel, text ?? ""),
            (w, id) => new UiLabelHandle(PluginId, RootName, Owner, w, w != null, id), wire: false);

    /// <summary>
    /// DIALOGS1: while true, the settings-kit controls added to this panel (toggle, slider, input,
    /// dropdown, number, slider field, color, key, text area, radio, multi-select, info line) are
    /// drawn on the stage 2 settings grid — name left, a fixed control column at the same x on every
    /// row, a toggle as the track-and-knob switch — exactly as the mod page's settings are. Labels,
    /// headings, notes and dividers keep the full row; a button is the game's compact button at the
    /// row's left edge (CSAMPLE1, <see cref="PageButtonLayout"/>). The Mods workspace sets it
    /// while a mod's custom page builder runs (<see cref="ModsMenu.OpenPage"/>).
    /// </summary>
    internal bool GridControls { get; set; }

    /// <summary>DIALOGS1: called once when this shown panel is hidden or disposed (the kit dialog's liveness hook).</summary>
    internal Action? LeftScreen { get; set; }

    /// <summary>Run one control factory on the settings grid when <see cref="GridControls"/> is on.</summary>
    private IRuntimeWidget? OnGrid(Func<IRuntimeWidget?> add)
    {
        if (!GridControls || _panel == null) return add();
        try { _runtime.SetSettingsGrid(_panel, true); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings grid threw: " + e.Message); }
        try { return add(); }
        finally
        {
            try { _runtime.SetSettingsGrid(_panel, false); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings grid threw: " + e.Message); }
        }
    }

    public IUiLabel Note(string text) =>
        AddChild(UiSlotKind.Click, () => _panel == null ? null : _runtime.AddMutedLabel(_panel, text ?? ""),
            (w, id) => new UiLabelHandle(PluginId, RootName, Owner, w, w != null, id), wire: false);

    public IUiLabel Fact(string name, string value) =>
        // GRIDROW1: on a custom page or a game window a fact is a settings-grid row (name on the name column, the value
        // right-aligned in the control column, as an Info value), not the kit's centered pair (live 2026-10-11).
        AddChild(UiSlotKind.Click, () => _panel == null ? null : OnGrid(() => _runtime.AddFact(_panel, name ?? "", value ?? "")),
            (w, id) => new UiLabelHandle(PluginId, RootName, Owner, w, w != null, id), wire: false);

    public void BeginInline()
    {
        if (_panel == null) return;
        try { _runtime.BeginInlineControls(_panel); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: begin inline: " + e.Message); }
    }

    public void EndInline()
    {
        if (_panel == null) return;
        try { _runtime.EndInlineControls(_panel); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: end inline: " + e.Message); }
    }

    // Each settings-kit control is wired to the raw widget event its runtime exposes (a click, the
    // field's end-of-edit, a slider sample), then Attach binds that slot to the handle's own state
    // machine and wires any extra parts (arrows, steppers) to slots of their own. The plugin's
    // OnChanged handlers are held by the handle and called on a commit, never on a raw event.

    public IUiChoice Choice(string text, IReadOnlyList<UiChoiceOption> options, string value)
    {
        var handle = AddChild(UiSlotKind.Click,
            () => _panel == null ? null : OnGrid(() => _runtime.AddChoice(_panel, text ?? "", options, value ?? "")),
            (w, id) => new UiChoiceHandle(PluginId, RootName, Owner, w, w != null, id, options, value ?? ""));
        handle.Attach(_runtime);
        return handle;
    }

    public IUiNumeric Numeric(string text, double value, double min, double max, double step, int decimals = 0, string? units = null)
    {
        var handle = AddChild(UiSlotKind.Text,
            () => _panel == null ? null : OnGrid(() => _runtime.AddNumeric(_panel, text ?? "", value, min, max, step, decimals, units ?? "")),
            (w, id) => new UiNumericHandle(PluginId, RootName, Owner, w, w != null, id, value, min, max, step, decimals, units));
        handle.Attach(_runtime);
        return handle;
    }

    public IUiSliderEntry SliderEntry(string text, float min, float max, float step, float initial)
    {
        var handle = AddChild(UiSlotKind.Float,
            () => _panel == null ? null : OnGrid(() => _runtime.AddSliderEntry(_panel, text ?? "", min, max, step, initial)),
            (w, id) => new UiSliderEntryHandle(PluginId, RootName, Owner, w, w != null, id, min, max, step, initial));
        handle.Attach(_runtime);
        return handle;
    }

    public IUiColorPicker ColorPicker(string text, UiColor initial, bool alpha = false)
    {
        var handle = AddChild(UiSlotKind.Text,
            () => _panel == null ? null : OnGrid(() => _runtime.AddColorPicker(_panel, text ?? "", initial, alpha)),
            (w, id) => new UiColorPickerHandle(PluginId, RootName, Owner, w, w != null, id, initial, alpha));
        handle.Attach(_runtime);
        return handle;
    }

    public IUiKeybindPicker KeybindPicker(string text, string pluginId, string bindId)
    {
        var handle = AddChild(UiSlotKind.Click,
            () => _panel == null ? null : OnGrid(() => _runtime.AddKeybindPicker(_panel, text ?? "", pluginId ?? "", bindId ?? "")),
            (w, id) => new UiKeybindPickerHandle(PluginId, RootName, Owner, w, w != null, id, pluginId ?? "", bindId ?? ""));
        handle.Attach(_runtime);
        return handle;
    }

    public IUiInput TextArea(string text, string initial, int lines) =>
        AddChild(UiSlotKind.Text,
            () => _panel == null ? null : OnGrid(() => _runtime.AddTextArea(_panel, text ?? "", initial ?? "", lines)),
            (w, id) => new UiInputHandle(PluginId, RootName, Owner, w, w != null, id, multiline: true, lines: lines));

    public UiOptionsHandle Options(
        string text, IReadOnlyList<UiChoiceOption> options, IEnumerable<string>? selected, bool multi, UiOptionsLayout layout)
    {
        var handle = AddChild(UiSlotKind.Click,
            () => _panel == null ? null : OnGrid(() => _runtime.AddOptions(_panel, text ?? "", options, multi, layout)),
            (w, id) => new UiOptionsHandle(PluginId, RootName, Owner, w, w != null, id, options, selected, multi, layout, _runtime));
        handle.Attach(_runtime);
        return handle;
    }

    public IUiInfo Info(string text, Func<string> value, double refreshSeconds)
    {
        string initial = "";
        try { initial = value?.Invoke() ?? ""; }
        catch { initial = UiInfoHandle.Unavailable; }
        var handle = AddChild(UiSlotKind.Click,
            () => _panel == null ? null : OnGrid(() => _runtime.AddInfo(_panel, text ?? "", initial)),
            (w, id) => new UiInfoHandle(PluginId, RootName, Owner, w, w != null, id, text ?? "", value, refreshSeconds),
            wire: false);
        handle.Start();
        return handle;
    }

    public IUiLabel Divider() =>
        AddChild(UiSlotKind.Click, () => _panel == null ? null : _runtime.AddDivider(_panel),
            (w, id) => new UiLabelHandle(PluginId, RootName, Owner, w, w != null, id), wire: false);

    /// <summary>SETTINGS1 stage 2: a grid button row, a name and a button (<see cref="UiSettingsGrid.NamedButton"/>).</summary>
    internal UiButtonHandle AddNamedButton(string name, string caption) =>
        AddChild(UiSlotKind.Click,
            () => _panel == null ? null : _runtime.AddGridButton(_panel, name, caption),
            (w, id) => new UiButtonHandle(PluginId, RootName, Owner, w, w != null, id));

    /// <summary>SETTINGS1 stage 2: a square link in a row's reset gutter (<see cref="UiSettingsGrid.GutterLink"/>).</summary>
    internal IUiIconLink AddGutterLink(IRuntimeWidget row, string glyph, string tooltip) =>
        AddChild(UiSlotKind.Click,
            () => _panel == null ? null : _runtime.AddGutterLink(_panel, row, glyph, tooltip),
            (w, id) => new UiIconLinkHandle(PluginId, RootName, Owner, w, w != null, id, glyph, tooltip, _runtime));

    /// <summary>TEXTEXPAND1: a small square button in a text field's corner (<see cref="UiSettingsGrid.CornerLink"/>).</summary>
    internal IUiIconLink AddCornerLink(IRuntimeWidget row, string glyph, string tooltip) =>
        AddChild(UiSlotKind.Click,
            () => _panel == null ? null : _runtime.AddCornerLink(_panel, row, glyph, tooltip),
            (w, id) => new UiIconLinkHandle(PluginId, RootName, Owner, w, w != null, id, glyph, tooltip, _runtime));

    /// <summary>The runtime panel behind this handle, for <see cref="UiReveal"/>. Null when disabled.</summary>
    internal IRuntimePanel? RuntimePanel => _panel;

    internal IUiCellRow AddCellRow(CellRowSpec spec, bool header) =>
        AddChild(UiSlotKind.Click,
            // CSAMPLE1: on a custom mod page a cell row (a date-time field) is a settings-grid row, as on the settings
            // page, so its name and control share the grid's columns with the page's other controls.
            () => _panel == null ? null : OnGrid(() => _runtime.AddCellRow(_panel, spec, header)),
            (w, id) => new UiCellRowHandle(PluginId, RootName, Owner, w as IRuntimeCellRow, w != null, id, spec, header, _runtime));

    /// <summary>KIT1: a label whose caption carries a <see cref="LabelTone"/> color.</summary>
    internal IUiToneLabel AddToneLabel(string text, LabelTone tone) =>
        AddChild(UiSlotKind.Click,
            () => _panel == null ? null : _runtime.AddTonedLabel(_panel, text, tone),
            (w, id) => new UiToneLabelHandle(PluginId, RootName, Owner, w, w != null, id, tone, _runtime),
            wire: false);

    /// <summary>KIT1: a small square icon-only button, optionally in the previous row's right-hand column.</summary>
    internal IUiIconLink AddIconLink(string glyph, string tooltip, bool attachToHeading) =>
        AddChild(UiSlotKind.Click,
            () => _panel == null ? null : _runtime.AddIconLink(_panel, glyph, tooltip, attachToHeading),
            (w, id) => new UiIconLinkHandle(PluginId, RootName, Owner, w, w != null, id, glyph, tooltip, _runtime));

    /// <summary>KIT1: a sidebar row with a red marker in front and a state dot at the right edge.</summary>
    internal IUiMarkedButton AddMarkedButton(string caption, string marker, LabelTone? dot) =>
        AddChild(UiSlotKind.Click,
            () => _panel == null ? null : _runtime.AddMarkedButton(_panel, caption, marker, dot),
            (w, id) => new UiMarkedButtonHandle(PluginId, RootName, Owner, w, w != null, id, caption, marker, dot, _runtime));

    /// <summary>
    /// When this panel blocks gameplay input (job FUI19). Unset means "follow the default", which
    /// is this plugin's own if it set one and otherwise the framework's
    /// (<see cref="UiCaptureMode.WhenHovered"/>). Setting it on an open panel takes effect at once
    /// — a panel switched to <see cref="UiCaptureMode.WhileShown"/> has to start blocking without
    /// waiting for the player to move the mouse.
    /// </summary>
    private string _footer = "";

    /// <summary>
    /// The version footer (<see cref="IUiPanel.Footer"/>). Held on the handle so a disabled panel
    /// and the readback still answer; the runtime draws it when there is a runtime panel.
    /// </summary>
    public string Footer
    {
        get => _footer;
        set
        {
            string next = value?.Trim() ?? "";
            if (string.Equals(next, _footer, StringComparison.Ordinal)) return;
            _footer = next;
            if (IsDisposed || _panel == null) return;
            try { _runtime.SetPanelFooter(_panel, next); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: panel footer: " + e.Message); }
        }
    }

    public UiCaptureMode CaptureMode
    {
        get => _mode ?? Ui.CaptureMode(PluginId);
        set
        {
            _mode = value;
            if (IsDisposed || _panel == null) return;
            if (Volatile.Read(ref _shown) != 1) return;

            // The frozen mode moves with it, so the Hide path still detaches what is attached.
            var previous = _shownMode;
            _shownMode = value;
            try
            {
                if (previous == UiCaptureMode.Never && value != UiCaptureMode.Never)
                {
                    // It was not being routed and was not counted; it becomes both.
                    UiHoverGate.Attach(_panel, value);
                    UiInputCapture.Acquire();
                }
                else if (previous != UiCaptureMode.Never && value == UiCaptureMode.Never)
                {
                    UiInputCapture.Release();
                    UiHoverGate.Detach(_panel);
                }
                else
                {
                    UiHoverGate.Remode(_panel, value);
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: CaptureMode change threw: " + e.Message);
            }
        }
    }

    public void Show()
    {
        if (IsDisposed || !Enabled || _panel == null) return;

        // Job FUI20. The slot has to be claimed before the runtime lays the panel out, because the
        // offset is applied by the same pass that resizes the frame to its content — the room a
        // cascade step has is a function of the height the frame is about to be given. `unowned`
        // is the slot's state between here and the transition below: if Show throws before the
        // panel actually becomes shown, nothing will ever release it, so the finally does.
        bool unowned = Volatile.Read(ref _shown) == 0 && ClaimSlot();

        try
        {
            _runtime.Show(_panel);
            if (Interlocked.Exchange(ref _shown, 1) == 0)
            {
                // The panel is shown, so the slot now belongs to it and is released at Hide.
                unowned = false;
                _runtime.SetCapture(true);
                // Cursor unlock (Bootstrap) and the game's input context (Core) are two
                // separate refcounts on purpose: the cursor is process-lifetime state that
                // Bootstrap restores, the context is a per-frame mask Core owns and must
                // remove on unload. Both are driven by the same 0→1 panel transition.
                //
                // The cursor is unlocked unconditionally and for as long as the panel is shown,
                // whatever the capture mode says: a locked cursor could not be moved to the panel
                // in the first place, so only the input block is ever routed by the pointer
                // (job FUI19, UiCaptureMode).
                _shownMode = CaptureMode;
                if (_shownMode != UiCaptureMode.Never)
                {
                    // Gate first, refcount second. The gate settles the answer before the 0→1 that
                    // registers the context, so a hover-mode panel opened with the pointer
                    // elsewhere never registers-then-removes on the following frame.
                    UiHoverGate.Attach(_panel, _shownMode);
                    UiInputCapture.Acquire();
                }

                Ui.NotifyShown(+1);
                // NAV1: the newest shown panel is the one a controller acts on.
                ShownSequence = Interlocked.Increment(ref s_shownSequence);

                // Job FUI21. This panel is already in its column; the ones that were open before
                // it are not, because a set of two is laid out differently from a set of one. Only
                // an open or a close moves a panel — a re-layout the player triggered by using a
                // panel (a rebind rewriting a row's text) recomputes the same rank out of the same
                // set and lands on the same place.
                Ui.ReflowPanels();
            }
            MarkShown();
            for (int i = 0; i < _children.Count; i++)
                _children[i].MarkShown();
            Plugin.Logger?.LogDiag("Ui: show '" + Text + "' slot=" + _slot);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: Show threw: " + e.Message);
        }
        finally
        {
            if (unowned) ReleaseSlot();
        }
    }

    /// <summary>
    /// Take a slot and tell the runtime which of the shown panels this one is. Returns whether a
    /// slot was taken, so the caller knows whether it still has to be given back. Never throws: a
    /// runtime that refuses the placement leaves the panel centered, which is where every panel was
    /// before job FUI20.
    ///
    /// <para>
    /// The spot is worked out here rather than left to the re-flow after the show, because this
    /// panel's own layout pass runs inside <c>_runtime.Show</c> — before the re-flow — and a panel
    /// that appeared centered for one frame and then jumped into its column would be a visible
    /// flicker. The count comes from the allocator, which already holds this panel's slot, so it
    /// is the count the set is about to have and matches what the re-flow computes.
    /// </para>
    /// </summary>
    private bool ClaimSlot()
    {
        int slot;
        try { slot = UiPanelSlots.Acquire(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: panel slot acquire threw: " + e.Message);
            return false;
        }

        _slot = slot;

        var spot = new PanelSpot(0, 1);
        try { spot = new PanelSpot(UiPanelSlots.RankOf(slot), UiPanelSlots.Count); }
        catch (Exception e)
        {
            // A set of one is the honest guess: the center, which is the pre-FUI20 placement.
            Plugin.Logger?.LogWarning("Ui: panel rank threw: " + e.Message);
        }

        try { _runtime.PlacePanel(_panel!, spot); }
        catch (Exception e)
        {
            // The slot is still held and still released by the caller; only the placement failed.
            Plugin.Logger?.LogWarning("Ui: PlacePanel threw: " + e.Message);
        }

        return true;
    }

    /// <summary>
    /// Whether this panel is one of the shown set, for <see cref="Ui.ReflowPanels"/> — the only
    /// caller, and the only place that can see every panel at once.
    /// </summary>
    internal bool IsShownPanel => Volatile.Read(ref _shown) == 1;

    /// <summary>The slot this panel holds, which is the order the shown set is ranked in.</summary>
    internal int PlacementSlot => _slot;

    /// <summary>
    /// Tell the runtime this panel is the <paramref name="rank"/>th of <paramref name="count"/>
    /// shown panels, and let it move the frame there. Never throws — a re-flow runs from Show and
    /// from Hide, and one panel that refused to move must not stop the rest of the set from
    /// being laid out.
    /// </summary>
    internal void PlaceAt(int rank, int count)
    {
        if (_panel == null) return;
        try { _runtime.PlacePanel(_panel, new PanelSpot(rank, count)); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: re-flow PlacePanel threw: " + e.Message); }
    }

    /// <summary>
    /// Give the cascade slot back and forget it. Idempotent, because it runs from Hide, from
    /// Dispose-while-shown and from a Show that threw.
    /// </summary>
    private void ReleaseSlot()
    {
        int slot = _slot;
        _slot = UiPanelSlots.NoSlot;
        if (slot < 0) return;
        try { UiPanelSlots.Release(slot); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: panel slot release threw: " + e.Message); }

        // Tell the runtime the panel is no longer one of the set, so a panel shown again while the
        // set has a different size is not drawn at a stale offset.
        try { if (_panel != null) _runtime.PlacePanel(_panel, PanelSpot.None); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: PlacePanel(none) threw: " + e.Message); }
    }

    public void Hide()
    {
        if (IsDisposed) return;
        // Escape sweep (Ui.OnBootstrapKey) calls Hide, not Dispose. A focused
        // non-empty Mods search box consumes that Hide so the field clears and
        // the workspace stays open. One physical Escape is InvokeKey(27) then
        // InvokeKey(17); the clear latch must survive both. Back/Menu_Close stay
        // taken so the game menu does not open. Close/suspend use ForceHide /
        // HideCore and still hide.
        if (ModsMenu.TryConsumeEscapeHide()) return;
        HideCore();
    }

    /// <summary>Hide even when the search box would consume Escape.</summary>
    internal void ForceHide()
    {
        if (IsDisposed) return;
        HideCore();
    }

    /// <summary>
    /// The body of <see cref="Hide"/> without the disposed guard. <see cref="UiHandle.Dispose"/>
    /// flips <c>IsDisposed</c> before it calls <see cref="OnDispose"/>, so a plain
    /// <c>Hide()</c> there would return without releasing the cursor capture or the input
    /// context — a panel disposed while shown would leave both held (FUI2).
    /// </summary>
    private void HideCore()
    {
        if (_panel == null) return;
        try
        {
            _runtime.Hide(_panel);
            if (Interlocked.Exchange(ref _shown, 0) == 1)
            {
                _runtime.SetCapture(false);
                if (_shownMode != UiCaptureMode.Never)
                {
                    // Refcount first: a panel hidden or disposed while the pointer is over it must
                    // drop the count in the same order it always did, because count > 0 is what
                    // makes the block wanted at all. The gate is then told, which is only how the
                    // *remaining* panels are re-decided — the leaving panel's own contribution is
                    // already gone (job FUI19).
                    UiInputCapture.Release();
                    UiHoverGate.Detach(_panel);
                }

                Ui.NotifyShown(-1);

                // Last, and after the gate: the slot is only about where the frame is drawn, and
                // the frame is already hidden. Releasing it earlier would let a panel opened on
                // the same frame land on top of one that is still on screen.
                ReleaseSlot();

                // Job FUI21, and the deliberate reversal of FUI20's "closing the panel above does
                // not slide the one below": with panels tiled rather than stacked, a survivor left
                // in its old column would sit off-center with an empty column beside it and
                // nothing to be off-center from. The layout is a function of what is on screen, so
                // what is left is laid out for how many are left.
                Ui.ReflowPanels();

                // DIALOGS1: a dialog on this panel learns that it left the screen (a sweep, another
                // owner's dispose) and drops itself, so the queue behind it moves on.
                var left = LeftScreen;
                if (left != null)
                {
                    try { left(); }
                    catch (Exception e) { Plugin.Logger?.LogWarning("Ui: left-screen hook threw: " + e.Message); }
                }
            }

            MarkHidden();
            for (int i = 0; i < _children.Count; i++)
                _children[i].MarkHidden();
            Plugin.Logger?.LogDiag("Ui: hide '" + Text + "'");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: Hide threw: " + e.Message);
        }
    }

    protected override void OnDispose()
    {
        HideCore();
        UiHandle[] snapshot;
        lock (_children)
            snapshot = _children.ToArray();
        for (int i = 0; i < snapshot.Length; i++)
        {
            try { snapshot[i].Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: child dispose threw: " + e.Message); }
        }

        lock (_children)
            _children.Clear();

        // Per-panel overlay canvas (and its GraphicRaycaster) dies with the panel.
        // RemoveAll / Uninstall used to be the only path that destroyed it (R25 #8).
        try { if (_panel is IHasOverlay has) has.Overlay?.Release(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: overlay Release threw: " + e.Message); }

        base.OnDispose();
    }

    private static long s_shownSequence;

    /// <summary>NAV1: when this panel was last shown (higher is newer); 0 before its first show.</summary>
    internal long ShownSequence { get; private set; }

    /// <summary>NAV1: whether the panel is on screen now.</summary>
    internal bool IsShownNow => Volatile.Read(ref _shown) == 1;

    /// <summary>NAV1: the capture mode the panel was shown with (a controller acts only on a panel that takes input while shown).</summary>
    internal UiCaptureMode ShownCaptureMode => _shownMode;

    /// <summary>NAV1: the panel's handles in build order.</summary>
    internal UiHandle[] ChildrenSnapshot()
    {
        lock (_children)
            return _children.ToArray();
    }

    internal void ForgetChild(UiHandle child)
    {
        lock (_children)
            _children.Remove(child);
    }

    /// <summary>
    /// JOINFLOW1 (the date-time field): take a handle composed from this panel's own rows as a child,
    /// so the panel's dispose (a page rebuild, the panel closing, the owner's unload) disposes it too.
    /// A disposed panel disposes the handle at once.
    /// </summary>
    internal void AdoptChild(UiHandle child)
    {
        if (child == null) return;
        if (IsDisposed)
        {
            child.Dispose();
            return;
        }

        lock (_children)
            if (!_children.Contains(child)) _children.Add(child);
        Ui.TrackChild(child);
    }

    private T AddChild<T>(
        UiSlotKind kind,
        Func<IRuntimeWidget?> create,
        Func<IRuntimeWidget?, ushort, T> ctor,
        bool wire = true)
        where T : UiHandle
    {
        if (IsDisposed || !Enabled)
            return ctor(null, 0);

        IRuntimeWidget? widget = null;
        try { widget = create(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: add child threw: " + e.Message);
        }

        ushort id = 0;
        if (widget != null)
        {
            id = UiSlotTable.Alloc(Owner);
            if (wire)
            {
                try
                {
                    if (!_runtime.TryWire(id, kind, widget))
                        Plugin.Logger?.LogDiag("Ui: TryWire kind=" + kind + " id=" + id + " returned false (Bootstrap absent or no UnityEvent).");
                    TryWirePointer(id, widget);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Ui: TryWire threw: " + e.Message);
                }
            }
        }

        var handle = ctor(widget, id);
        handle.Align = _contentAlignment;
        lock (_children)
            _children.Add(handle);
        Ui.TrackChild(handle);
        return handle;
    }

    private void TryWirePointer(ushort id, IRuntimeWidget widget)
    {
        try
        {
            if (widget.PointerEnterEvent != null)
                _runtime.TryWire(id, UiSlotKind.PointerEnter, widget);
            if (widget.PointerExitEvent != null)
                _runtime.TryWire(id, UiSlotKind.PointerExit, widget);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: pointer wire threw: " + e.Message);
        }
    }
}

internal sealed class UiButtonHandle : UiHandle, IUiButton
{
    internal UiButtonHandle(string pluginId, string rootName, object? owner, IRuntimeWidget? runtime, bool enabled, ushort id)
        : base(pluginId, rootName, owner, runtime, enabled)
    {
        WidgetId = id;
    }

    private Action? _handler;
    private string? _question;
    private string _body = "";
    private string _caption = "";
    private Func<string, IUiPanel>? _dialogFactory;
    private UiDialog? _dialog;

    public IDisposable OnClick(Action handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (WidgetId == 0) return Noop.Instance;
        _handler = handler;
        UiSlotTable.BindClick(WidgetId, Pressed);
        return new ActionDisposable(() =>
        {
            if (ReferenceEquals(_handler, handler)) _handler = null;
            UiSlotTable.BindClick(WidgetId, null);
        });
    }

    /// <summary>SETTINGS1 stage 2: the question a click asks first, or null for a plain button.</summary>
    internal string? ConfirmQuestion => _question;

    /// <summary>The confirmation dialog this button has open, or null.</summary>
    internal UiDialog? ConfirmDialog => _dialog is { IsOpen: true } open ? open : null;

    /// <summary>SETTINGS1 stage 2: ask <paramref name="question"/> before the handler runs (<see cref="IUiPanel.ConfirmButton"/>).</summary>
    internal void ConfigureConfirm(string question, string caption, Func<string, IUiPanel>? factory, string? body = null)
    {
        _question = question;
        _body = body ?? "";
        _caption = caption ?? "";
        _dialogFactory = factory;
    }

    /// <summary>The raw click: ignored while not interactable; a confirm button opens its question.</summary>
    internal void Pressed()
    {
        if (IsDisposed || !Interactable) return;
        var handler = _handler;
        if (handler == null) return;
        if (_question == null)
        {
            handler();
            return;
        }

        var factory = _dialogFactory;
        if (factory == null)
        {
            Plugin.Logger?.LogWarning("Ui: '" + _caption + "' asks for confirmation but no dialog can be opened; not run.");
            return;
        }

        UiDialog dialog;
        try
        {
            dialog = UiDialog.Open(
                factory, UiSettingsControls.ConfirmDialogTag, "confirm", _caption, _question, _body,
                _caption, LabelTone.Orange, ModPageCopy.PromptNo);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: confirmation dialog for '" + _caption + "' threw: " + e.Message);
            return;
        }

        _dialog = dialog;
        dialog.OnYes(() =>
        {
            _dialog = null;
            // The dialog belongs to the button: a Yes that outlived it (a rebuilt page, an unload)
            // runs nothing.
            if (IsDisposed || !Interactable)
            {
                Plugin.Logger?.LogWarning("Ui: '" + _caption + "' confirmed after its button was gone; not run.");
                return;
            }

            try { _handler?.Invoke(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: '" + _caption + "' threw: " + e.Message); }
        });
        dialog.OnNo(() => _dialog = null);
    }

    protected override void OnDispose()
    {
        var open = _dialog;
        _dialog = null;
        _handler = null;
        if (open is { IsOpen: true })
        {
            try { open.Close(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: confirmation dialog close threw: " + e.Message); }
        }

        base.OnDispose();
    }
}

internal sealed class UiToggleHandle : UiHandle, IUiToggle
{
    internal UiToggleHandle(string pluginId, string rootName, object? owner, IRuntimeWidget? runtime, bool enabled, ushort id)
        : base(pluginId, rootName, owner, runtime, enabled)
    {
        WidgetId = id;
    }

    public bool Value
    {
        get => Runtime is { Alive: true } r && r.BoolValue;
        set
        {
            if (Runtime is { Alive: true } r)
                r.BoolValue = value;
        }
    }

    public IDisposable OnChanged(Action<bool> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (WidgetId == 0) return Noop.Instance;
        UiSlotTable.BindBool(WidgetId, handler);
        return new ActionDisposable(() => UiSlotTable.BindBool(WidgetId, null));
    }
}

internal sealed class UiLabelHandle : UiHandle, IUiLabel
{
    internal UiLabelHandle(string pluginId, string rootName, object? owner, IRuntimeWidget? runtime, bool enabled, ushort id)
        : base(pluginId, rootName, owner, runtime, enabled)
    {
        WidgetId = id;
    }
}

/// <summary>
/// KIT1: a label that remembers its <see cref="LabelTone"/>. The color itself is a TMP write the
/// runtime makes; this handle is the state of record so the readback reports the tone the caption was
/// last given even on a runtime that draws nothing.
/// </summary>
internal sealed class UiToneLabelHandle : UiHandle, IUiToneLabel
{
    private readonly IUiRuntime? _uiRuntime;
    private LabelTone _tone;

    internal UiToneLabelHandle(
        string pluginId, string rootName, object? owner, IRuntimeWidget? runtime, bool enabled, ushort id,
        LabelTone tone, IUiRuntime? uiRuntime)
        : base(pluginId, rootName, owner, runtime, enabled)
    {
        WidgetId = id;
        _tone = tone;
        _uiRuntime = uiRuntime;
    }

    internal static UiToneLabelHandle Disabled(IUiPanel? panel, LabelTone tone)
    {
        string plugin = panel?.PluginId ?? UiOwnership.UnownedId;
        return new UiToneLabelHandle(
            plugin, panel?.RootName ?? UiOwnership.RootName(plugin), null, null, false, 0, tone, null);
    }

    public LabelTone Tone => _tone;

    public void SetTone(LabelTone tone)
    {
        _tone = tone;
        if (IsDisposed || _uiRuntime == null || Runtime is not { Alive: true } widget) return;
        try { _uiRuntime.StyleLabel(widget, tone); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: label tone: " + e.Message); }
    }

    public void SetText(string text, LabelTone tone)
    {
        Text = text ?? "";
        SetTone(tone);
    }

    public Dictionary<string, object?> Describe() => new()
    {
        ["kind"] = "label",
        ["text"] = Text,
        ["tone"] = LabelTones.Name(_tone),
    };
}

/// <summary>
/// KIT1: the icon-only link button. The glyph, the tooltip and the url live here because all three
/// are in the readback and the last of them is not drawn at all.
/// </summary>
internal sealed class UiIconLinkHandle : UiHandle, IUiIconLink
{
    private readonly IUiRuntime? _uiRuntime;

    internal UiIconLinkHandle(
        string pluginId, string rootName, object? owner, IRuntimeWidget? runtime, bool enabled, ushort id,
        string glyph, string tooltip, IUiRuntime? uiRuntime)
        : base(pluginId, rootName, owner, runtime, enabled)
    {
        WidgetId = id;
        Glyph = glyph ?? "";
        Tooltip = tooltip ?? "";
        _uiRuntime = uiRuntime;
    }

    internal static UiIconLinkHandle Disabled(IUiPanel? panel, string glyph, string tooltip)
    {
        string plugin = panel?.PluginId ?? UiOwnership.UnownedId;
        return new UiIconLinkHandle(
            plugin, panel?.RootName ?? UiOwnership.RootName(plugin), null, null, false, 0, glyph, tooltip, null);
    }

    public string Glyph { get; private set; }

    public string Tooltip { get; }

    public string Url { get; set; } = "";

    public void SetSprite(object? sprite)
    {
        if (IsDisposed || _uiRuntime == null || Runtime is not { Alive: true } widget) return;
        try
        {
            if (_uiRuntime.SetIconSprite(widget, sprite) && sprite != null) Glyph = "";
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: icon sprite: " + e.Message); }
    }

    public IDisposable OnClick(Action handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (WidgetId == 0) return Noop.Instance;
        UiSlotTable.BindClick(WidgetId, handler);
        return new ActionDisposable(() => UiSlotTable.BindClick(WidgetId, null));
    }

    public Dictionary<string, object?> Describe() => new()
    {
        ["kind"] = "link",
        ["glyph"] = Glyph,
        ["tooltip"] = Tooltip,
        ["url"] = Url,
    };
}

/// <summary>
/// KIT1: a sidebar row whose marker and dot are their own drawn parts rather than characters in the
/// caption. The caption stays the mod's name, which is what a click, a search match and the readback
/// all want it to be.
/// </summary>
internal sealed class UiMarkedButtonHandle : UiHandle, IUiMarkedButton
{
    private readonly IUiRuntime? _uiRuntime;
    private string _marker;
    private LabelTone? _dot;

    internal UiMarkedButtonHandle(
        string pluginId, string rootName, object? owner, IRuntimeWidget? runtime, bool enabled, ushort id,
        string caption, string marker, LabelTone? dot, IUiRuntime? uiRuntime)
        : base(pluginId, rootName, owner, runtime, enabled)
    {
        WidgetId = id;
        Caption = caption ?? "";
        _marker = marker ?? "";
        _dot = dot;
        _uiRuntime = uiRuntime;
    }

    internal static UiMarkedButtonHandle Disabled(IUiPanel? panel, string caption, string marker, LabelTone? dot)
    {
        string plugin = panel?.PluginId ?? UiOwnership.UnownedId;
        return new UiMarkedButtonHandle(
            plugin, panel?.RootName ?? UiOwnership.RootName(plugin), null, null, false, 0, caption, marker, dot, null);
    }

    public string Caption { get; }

    public string Marker => _marker;

    public LabelTone? Dot => _dot;

    public void SetMarker(string? marker)
    {
        _marker = marker ?? "";
        Restyle();
    }

    public void SetDot(LabelTone? dot)
    {
        _dot = dot;
        Restyle();
    }

    private void Restyle()
    {
        if (IsDisposed || _uiRuntime == null || Runtime is not { Alive: true } widget) return;
        try { _uiRuntime.StyleMarkedButton(widget, _marker, _dot); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: row marker: " + e.Message); }
    }

    public IDisposable OnClick(Action handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (WidgetId == 0) return Noop.Instance;
        UiSlotTable.BindClick(WidgetId, handler);
        return new ActionDisposable(() => UiSlotTable.BindClick(WidgetId, null));
    }

    public Dictionary<string, object?> Describe() => new()
    {
        ["kind"] = "button",
        ["label"] = Caption,
        ["marker"] = _marker,
        ["dot"] = _dot == null ? null : LabelTones.Name(_dot.Value),
    };
}

internal sealed class UiSliderHandle : UiHandle, IUiSlider
{
    internal UiSliderHandle(string pluginId, string rootName, object? owner, IRuntimeWidget? runtime, bool enabled, ushort id)
        : base(pluginId, rootName, owner, runtime, enabled)
    {
        WidgetId = id;
    }

    /// <summary>NAV1: the range the slider was made with (a controller step is a twentieth of it).</summary>
    internal float Min { get; init; }

    internal float Max { get; init; } = 1f;

    public float Value
    {
        get => Runtime is { Alive: true } r ? r.FloatValue : 0f;
        set
        {
            if (Runtime is { Alive: true } r)
                r.FloatValue = value;
        }
    }

    public IDisposable OnChanged(Action<float> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (WidgetId == 0) return Noop.Instance;
        UiSlotTable.BindFloat(WidgetId, handler);
        return new ActionDisposable(() => UiSlotTable.BindFloat(WidgetId, null));
    }
}

internal sealed class UiInputHandle : UiHandle, IUiInput
{
    internal UiInputHandle(
        string pluginId, string rootName, object? owner, IRuntimeWidget? runtime, bool enabled, ushort id,
        bool multiline = false, int lines = 1)
        : base(pluginId, rootName, owner, runtime, enabled)
    {
        WidgetId = id;
        Multiline = multiline;
        Lines = multiline ? Math.Max(2, lines) : 1;
    }

    /// <summary>SETTINGS1 stage 2: a <see cref="IUiPanel.TextArea"/> rather than a one-line field.</summary>
    internal bool Multiline { get; }

    /// <summary>Lines tall (1 for a one-line field).</summary>
    internal int Lines { get; }

    public string Value
    {
        get => Runtime is { Alive: true } r ? r.StringValue : "";
        set
        {
            if (Runtime is { Alive: true } r)
                r.StringValue = value ?? "";
        }
    }

    public IDisposable OnChanged(Action<string> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (WidgetId == 0) return Noop.Instance;
        UiSlotTable.BindText(WidgetId, handler);
        return new ActionDisposable(() => UiSlotTable.BindText(WidgetId, null));
    }
}

/// <summary>
/// The managed half of a cell row (LIST1). Keeps the specs, the expanded / selected state and
/// the sort mark so the row can be described (<see cref="Describe"/>) and rewritten without the
/// Unity object, forwards each change to <see cref="IRuntimeCellRow"/> when there is one, and
/// owns one Bootstrap click slot per button cell (per sortable header cell) beside the row's
/// own. A registry of live rows lets the bridge address a row by key.
/// </summary>
internal sealed class UiCellRowHandle : UiHandle, IUiCellHeader
{
    private static readonly List<UiCellRowHandle> LiveRows = new();

    private readonly CellRowSpec _spec;
    private readonly bool _header;
    private readonly IRuntimeCellRow? _row;
    private readonly IUiRuntime? _uiRuntime;
    private readonly Dictionary<string, ushort> _cellIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inputKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<string>> _changed = new(StringComparer.Ordinal);
    private Action<string>? _sort;
    private bool _expanded;
    private bool _selected;
    private string? _sortedKey;
    private bool _sortAscending;

    internal UiCellRowHandle(
        string pluginId,
        string rootName,
        object? owner,
        IRuntimeCellRow? row,
        bool enabled,
        ushort id,
        CellRowSpec spec,
        bool header,
        IUiRuntime? runtime)
        : base(pluginId, rootName, owner, row, enabled)
    {
        WidgetId = id;
        _spec = spec ?? new CellRowSpec("", Array.Empty<CellSpec>());
        _header = header;
        _row = row;
        _uiRuntime = runtime;
        WireCells();
        lock (LiveRows) LiveRows.Add(this);
    }

    /// <summary>A row on a panel this runtime cannot draw: state only.</summary>
    internal static UiCellRowHandle Disabled(IUiPanel? panel, CellRowSpec spec, bool header)
    {
        string plugin = panel?.PluginId ?? UiOwnership.UnownedId;
        string root = panel?.RootName ?? UiOwnership.RootName(plugin);
        return new UiCellRowHandle(plugin, root, null, null, false, 0, spec, header, null);
    }

    public string Key => _spec.Key;

    public IReadOnlyList<CellSpec> Cells => _spec.Cells;

    /// <inheritdoc />
    public float RightInset => _spec.RightInset;

    public bool Expanded => _expanded;

    public bool Selected => _selected;

    public string? SortedKey => _sortedKey;

    /// <inheritdoc />
    public bool SortAscending => _sortAscending;

    internal bool IsHeader => _header;

    /// <summary>
    /// NAV1: the cells a controller can press, in order: visible button (and clickable switch, linked) cells that hold a
    /// click slot and are not drawn disabled.
    /// </summary>
    internal List<string> NavCellKeys()
    {
        var keys = new List<string>();
        if (_header || IsDisposed) return keys;
        foreach (var cell in _spec.Cells)
        {
            if (cell == null || !cell.Visible || cell.Tone == CellTone.Disabled) continue;
            if (!IsClickable(cell, false) || _inputKeys.Contains(cell.Key)) continue;
            if (!_cellIds.TryGetValue(cell.Key, out ushort id) || id == 0) continue;
            keys.Add(cell.Key);
        }

        return keys;
    }

    /// <summary>NAV1: the row's rect, or a cell's (<paramref name="cellKey"/>), in screen pixels; unknown when unreadable.</summary>
    internal ScreenRect NavRect(string? cellKey)
    {
        if (_row == null || IsDisposed) return ScreenRect.Unknown;
        try
        {
            if (string.IsNullOrEmpty(cellKey)) return _row.RowRect();
            foreach (var pair in _row.CellRects())
                if (string.Equals(pair.Key, cellKey, StringComparison.Ordinal)) return pair.Value;
        }
        catch { /* unreadable */ }

        return ScreenRect.Unknown;
    }

    /// <summary>NAV1: the row's runtime widget, or a button cell's, for the focus marker.</summary>
    internal IRuntimeWidget? NavWidget(string? cellKey)
    {
        if (_row == null || IsDisposed) return null;
        if (string.IsNullOrEmpty(cellKey)) return _row;
        try { return _row.CellWidget(cellKey!); }
        catch { return null; }
    }

    /// <summary>The Bootstrap slot id wired to <paramref name="cellKey"/> (a click slot, or a text slot for an input cell); 0 when none.</summary>
    internal ushort CellSlotId(string? cellKey) =>
        cellKey != null && _cellIds.TryGetValue(cellKey, out ushort id) ? id : (ushort)0;

    /// <summary>Which cells hold a click slot: button cells, or sortable cells on a header.</summary>
    internal static bool IsClickable(CellSpec spec, bool header) =>
        header
            ? !string.IsNullOrEmpty(spec.SortKey)
            : spec.Kind == CellKind.Button || (spec.Kind == CellKind.Switch && spec.Clickable) || IsLinked(spec);

    /// <summary>JOINLINKS1: a text or scroll cell that draws links (<see cref="CellSpec.Linkify"/>) takes clicks.</summary>
    internal static bool IsLinked(CellSpec spec) =>
        spec.Linkify && ((spec.Kind == CellKind.Text && spec.Wrap != CellWrap.TwoLine) || spec.Kind == CellKind.ScrollText);

    /// <summary>
    /// JOINLINKS1: the link of the linked cell <paramref name="cellKey"/> under the pointer now (its index
    /// in <see cref="CellSpec.LinksNow"/>), or -1. Call it from the cell's click handler.
    /// </summary>
    public int LinkAt(string cellKey)
    {
        var cell = _spec.Find(cellKey);
        if (cell == null || !IsLinked(cell) || IsDisposed || _row is not { Alive: true }) return -1;
        try
        {
            int index = _row.LinkAt(cellKey);
            return index >= 0 && index < cell.LinksNow().Count ? index : -1;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: link hit test '" + cellKey + "': " + e.Message);
            return -1;
        }
    }

    /// <summary>Which cells hold a text slot (LIST3): input cells on a body row.</summary>
    internal static bool IsInput(CellSpec spec, bool header) =>
        !header && spec.Kind == CellKind.Input;

    /// <summary>
    /// C5: an image cell that only reports the pointer (<see cref="CellSpec.Hover"/>; the Mods list's Works across versions
    /// icon). It holds a slot for its pointer trigger but takes no click of its own: a press on it goes to its row.
    /// </summary>
    internal static bool IsHoverOnly(CellSpec spec, bool header) =>
        !header && spec.Hover && spec.Kind == CellKind.Image;

    /// <summary>Which cells the runtime must answer <see cref="IRuntimeCellRow.CellWidget"/> for.</summary>
    internal static bool HasWidget(CellSpec spec, bool header) =>
        IsClickable(spec, header) || IsInput(spec, header) || IsHoverOnly(spec, header);

    private void WireCells()
    {
        if (_row == null || _uiRuntime == null || !Enabled) return;
        for (int i = 0; i < _spec.Cells.Count; i++)
        {
            var cell = _spec.Cells[i];
            bool input = IsInput(cell, _header);
            bool hoverOnly = IsHoverOnly(cell, _header);
            if ((!IsClickable(cell, _header) && !input && !hoverOnly) || _cellIds.ContainsKey(cell.Key)) continue;
            IRuntimeWidget? widget = null;
            try { widget = _row.CellWidget(cell.Key); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: cell widget '" + cell.Key + "': " + e.Message); }
            if (widget == null) continue;
            ushort id = UiSlotTable.Alloc(Owner);
            if (!hoverOnly)
            {
                try
                {
                    if (!_uiRuntime.TryWire(id, input ? UiSlotKind.Text : UiSlotKind.Click, widget))
                        Plugin.Logger?.LogDiag("Ui: TryWire cell '" + cell.Key + "' id=" + id + " returned false.");
                }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: TryWire cell threw: " + e.Message); }
            }
            _cellIds[cell.Key] = id;
            // KEYS3 polish: a hover cell's pointer trigger goes to the same slot, as a row's does.
            if (!input && cell.Hover)
            {
                try
                {
                    if (widget.PointerEnterEvent != null) _uiRuntime.TryWire(id, UiSlotKind.PointerEnter, widget);
                    if (widget.PointerExitEvent != null) _uiRuntime.TryWire(id, UiSlotKind.PointerExit, widget);
                }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: cell pointer wire threw: " + e.Message); }
            }

            if (input)
            {
                // The spec is the value of record: the readback and a later SetCell read it, and
                // the consumer's handler sees the same string the field holds.
                _inputKeys.Add(cell.Key);
                var spec = cell;
                UiSlotTable.BindText(id, value =>
                {
                    spec.Text = value ?? "";
                    if (_changed.TryGetValue(spec.Key, out var handler)) handler(spec.Text);
                });
            }
            else if (_header)
            {
                string sortKey = cell.SortKey ?? cell.Key;
                UiSlotTable.BindClick(id, () => _sort?.Invoke(sortKey));
            }
        }
    }

    public IDisposable OnRowClick(Action handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (WidgetId == 0) return Noop.Instance;
        EnsureRowClickWired();
        UiSlotTable.BindClick(WidgetId, handler);
        return new ActionDisposable(() => UiSlotTable.BindClick(WidgetId, null));
    }

    private bool _rowClickWired;

    /// <summary>
    /// RADIOCLICK1: whether the pointer can reach this row's click slot: the runtime row has a click event and it is
    /// wired to <see cref="UiHandle.WidgetId"/>. The bridge's <c>widget_click</c> dispatches the slot directly, so it
    /// worked on a plain row whose click no pointer could reach; this is what the readback reports instead.
    /// </summary>
    internal bool RowClickReachable => _rowClickWired || (_row?.ClickEvent != null && CellRowSpec.RowClickAtBuild(_spec, _header));

    /// <summary>
    /// RADIOCLICK1: a row built without a click target (plain, band; <see cref="CellRowSpec.RowClickAtBuild"/>) gets
    /// one when a row click is bound, and its slot is wired to it, exactly as a list row's is when it is built.
    /// </summary>
    private void EnsureRowClickWired()
    {
        if (_rowClickWired || _header || _row == null || _uiRuntime == null || IsDisposed || !Enabled) return;
        if (CellRowSpec.RowClickAtBuild(_spec, _header) && _row.ClickEvent != null)
        {
            // Wired when the panel added the row (UiPanelHandle.AddChild).
            _rowClickWired = true;
            return;
        }

        try
        {
            if (_row.EnsureRowClick() == null)
            {
                Plugin.Logger?.LogDiag("Ui: row '" + _spec.Key + "' has no click target to wire.");
                return;
            }

            _rowClickWired = _uiRuntime.TryWire(WidgetId, UiSlotKind.Click, _row);
            if (!_rowClickWired) Plugin.Logger?.LogDiag("Ui: TryWire row click '" + _spec.Key + "' returned false.");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: row click wire threw: " + e.Message); }
    }

    public IDisposable OnClick(string cellKey, Action handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (_header || cellKey == null || _inputKeys.Contains(cellKey) || !_cellIds.TryGetValue(cellKey, out ushort id) || id == 0) return Noop.Instance;
        UiSlotTable.BindClick(id, handler);
        return new ActionDisposable(() => UiSlotTable.BindClick(id, null));
    }

    /// <inheritdoc />
    public IDisposable OnHover(Action<bool> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        ushort id = WidgetId;
        if (_header || !_spec.Hover || id == 0) return Noop.Instance;
        UiSlotTable.BindPointerEnter(id, () => handler(true));
        UiSlotTable.BindPointerExit(id, () => handler(false));
        return new ActionDisposable(() =>
        {
            UiSlotTable.BindPointerEnter(id, null);
            UiSlotTable.BindPointerExit(id, null);
        });
    }

    /// <inheritdoc />
    public IDisposable OnCellHover(string cellKey, Action<bool> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        var cell = _spec.Find(cellKey);
        if (_header || cell == null || !cell.Hover || !_cellIds.TryGetValue(cellKey, out ushort id) || id == 0) return Noop.Instance;
        UiSlotTable.BindPointerEnter(id, () => handler(true));
        UiSlotTable.BindPointerExit(id, () => handler(false));
        return new ActionDisposable(() =>
        {
            UiSlotTable.BindPointerEnter(id, null);
            UiSlotTable.BindPointerExit(id, null);
        });
    }

    /// <summary>KEYS3 polish: dispatch a pointer enter or exit to a hover cell of a visible row, as its trigger does.</summary>
    internal static bool TryHoverCell(string? rowKey, string? cellKey, bool entered)
    {
        if (string.IsNullOrWhiteSpace(rowKey) || cellKey == null) return false;
        var row = FindVisible(rowKey!);
        if (row == null || row._spec.Find(cellKey) is not { Hover: true }) return false;
        if (!row._cellIds.TryGetValue(cellKey, out ushort id) || id == 0) return false;
        UiSlotTable.BootstrapDispatch(id, (byte)(entered ? UiSlotKind.PointerEnter : UiSlotKind.PointerExit), 0f, false, null);
        return true;
    }

    /// <summary>
    /// KEYS3: dispatch a pointer enter or exit to the visible row <paramref name="rowKey"/> through its
    /// slot, as the runtime's pointer trigger does (tests, and the bridge's hover op). False when the
    /// row is not visible or has no slot.
    /// </summary>
    internal static bool TryHover(string? rowKey, bool entered)
    {
        if (string.IsNullOrWhiteSpace(rowKey)) return false;
        var row = FindVisible(rowKey!);
        if (row == null || row.WidgetId == 0 || !row._spec.Hover) return false;
        UiSlotTable.BootstrapDispatch(row.WidgetId, (byte)(entered ? UiSlotKind.PointerEnter : UiSlotKind.PointerExit), 0f, false, null);
        return true;
    }

    public IDisposable OnChanged(string cellKey, Action<string> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (_header || cellKey == null || !_inputKeys.Contains(cellKey)) return Noop.Instance;
        _changed[cellKey] = handler;
        return new ActionDisposable(() =>
        {
            if (_changed.TryGetValue(cellKey, out var bound) && ReferenceEquals(bound, handler)) _changed.Remove(cellKey);
        });
    }

    public IDisposable OnSort(Action<string> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (!_header || !Enabled) return Noop.Instance;
        _sort = handler;
        return new ActionDisposable(() => { if (ReferenceEquals(_sort, handler)) _sort = null; });
    }

    public bool SetCell(string key, string? text = null, CellTone? tone = null, bool? visible = null)
    {
        var cell = _spec.Find(key);
        if (cell == null || IsDisposed) return false;
        if (text != null) cell.Text = text;
        if (tone != null) cell.Tone = tone.Value;
        if (visible != null) cell.Visible = visible.Value;
        if (_row is { Alive: true })
        {
            try { _row.ApplyCell(cell); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: cell rewrite '" + key + "': " + e.Message); }
        }
        return true;
    }

    public bool SetImage(string key, object? sprite)
    {
        var cell = _spec.Find(key);
        if (cell == null || cell.Kind != CellKind.Image || IsDisposed) return false;
        if (_row is { Alive: true })
        {
            try { _row.SetImage(key, sprite); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: cell image '" + key + "': " + e.Message); }
        }
        return true;
    }

    public void SetExpanded(bool expanded)
    {
        _expanded = expanded;
        if (_row is { Alive: true })
        {
            try { _row.SetExpanded(expanded); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: cell row expand: " + e.Message); }
        }
    }

    public void SetSelected(bool selected)
    {
        _selected = selected;
        if (_row is { Alive: true })
        {
            try { _row.SetSelected(selected); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: cell row select: " + e.Message); }
        }
    }

    public void SetSorted(string? sortKey) => SetSorted(sortKey, ascending: false);

    /// <inheritdoc />
    public void SetSorted(string? sortKey, bool ascending)
    {
        _sortAscending = ascending;
        string? found = null;
        for (int i = 0; i < _spec.Cells.Count && sortKey != null; i++)
        {
            if (string.Equals(_spec.Cells[i].SortKey, sortKey, StringComparison.Ordinal)) { found = sortKey; break; }
        }
        _sortedKey = found;
        if (_row is { Alive: true })
        {
            try { _row.SetSorted(found, ascending); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: header sort mark: " + e.Message); }
        }
    }

    /// <summary>The caption a header cell shows: uppercase, with the indicator on the sorted column.</summary>
    internal string HeaderCaptionOf(CellSpec cell) =>
        CellLayout.HeaderCaption(
            cell.Text, _sortedKey != null && string.Equals(cell.SortKey, _sortedKey, StringComparison.Ordinal), _sortAscending, cell.SmallCaps);

    public Dictionary<string, object?> Describe()
    {
        var rects = new Dictionary<string, ScreenRect>(StringComparer.Ordinal);
        ScreenRect rowRect = ScreenRect.Unknown;
        if (_row is { Alive: true })
        {
            try
            {
                foreach (var pair in _row.CellRects()) rects[pair.Key] = pair.Value;
                rowRect = _row.RowRect();
            }
            catch { rects.Clear(); }
        }

        var cells = new List<object?>(_spec.Cells.Count);
        for (int i = 0; i < _spec.Cells.Count; i++)
        {
            var cell = _spec.Cells[i];
            var described = new Dictionary<string, object?>
            {
                ["key"] = cell.Key,
                ["kind"] = KindName(cell.Kind),
                ["text"] = _header ? HeaderCaptionOf(cell) : cell.Text,
                ["tone"] = ToneName(cell.Tone),
                ["visible"] = cell.Visible,
                ["rect"] = rects.TryGetValue(cell.Key, out var r) && r.IsKnown ? RectOf(r) : null,
            };
            // LIST3: an input cell reads back `{kind: input, text, placeholder}` — text is its value.
            if (cell.Kind == CellKind.Input) described["placeholder"] = cell.Placeholder;
            // SEARCHCLEAR1: a search box says so (it draws the clear button while it has text).
            if (cell.Kind == CellKind.Input && cell.Clearable) described["clearable"] = true;
            // INSTALLCOLS1: a two-line cell's toned lead (the Install list's update note).
            if (cell.NoteLead.Length > 0) described["noteLead"] = cell.NoteLead;
            // KEYS3: a key cap or an ornament names its skin (the runtime says which donor drew it).
            if (cell.Skin != CellSkin.Kit) described["skin"] = SkinName(cell.Skin);
            if (cell.Strike) described["strike"] = true;
            if (cell.Hover) described["hover"] = true;
            // INSTALLSIDES1: an overlay names the cell it is drawn over; a scroll block its lines and
            // what the runtime measured (content and viewport height, the offset it holds).
            if (cell.OverlayOf != null) described["overlayOf"] = cell.OverlayOf;
            // C5: an inline overlay sits on its target's second line after this many characters of the note.
            if (cell.OverlayOf != null && cell.InlineAfter >= 0) described["inlineAfter"] = cell.InlineAfter;
            // INSTALLSIDES2: the per-side column, a grayed side and the state glyph the cell asks for
            // (the runtime logs once per font whether it drew the glyph or the words alone).
            if (cell.Column != CellColumn.None) described["column"] = cell.Column == CellColumn.Client ? "client" : "server";
            // Live review 3: a column button's drawn images (the sprite, the tint plate) as the runtime holds them.
            if (cell.Column != CellColumn.None && cell.Kind == CellKind.Button && _row is { Alive: true })
            {
                try
                {
                    var images = _row.CellImages(cell.Key);
                    if (images.Count > 0) described["images"] = images;
                }
                catch { /* best-effort */ }
            }
            if (cell.Grayed) described["grayed"] = true;
            if (cell.Glyph.Length > 0) described["glyph"] = cell.Glyph;
            // TILES1: the framed tile the cell belongs to.
            if (cell.Tile > 0) described["tile"] = cell.Tile;
            // JOINLINKS1: a linked cell's links as drawn now ({index, text, url}).
            if (IsLinked(cell)) described["links"] = UiLinks.Describe(cell.LinksNow());
            if (cell.Kind == CellKind.ScrollText)
            {
                described["lines"] = cell.Lines.Count;
                if (cell.NoScroll) described["noScroll"] = true;
                if (_row is { Alive: true })
                {
                    try
                    {
                        if (_row.TryScrollState(cell.Key, out float content, out float viewport, out float offset))
                        {
                            described["scroll"] = new Dictionary<string, object?>
                            {
                                ["content"] = Math.Round(content, 1),
                                ["viewport"] = Math.Round(viewport, 1),
                                ["offset"] = Math.Round(offset, 1),
                                ["scrolls"] = content > viewport + .5f,
                            };
                        }
                    }
                    catch { /* the readback is best-effort */ }
                }
            }
            cells.Add(described);
        }

        var row = new Dictionary<string, object?>
        {
            ["kind"] = _header ? "cellheader" : "cellrow",
            ["label"] = _spec.Key,
            ["key"] = _spec.Key,
            ["enabled"] = Enabled,
            ["twoLine"] = _spec.TwoLine,
            ["height"] = _spec.Height,
            ["cells"] = cells,
            ["expanded"] = _expanded,
            ["selected"] = _selected,
            ["rect"] = rowRect.IsKnown ? RectOf(rowRect) : null,
            // RADIOCLICK1: whether a pointer click on the row reaches its row click.
            ["rowClick"] = !_header && RowClickReachable,
        };
        if (_spec.Hover) row["hover"] = true;
        if (_spec.Compact) row["compact"] = true;
        if (_spec.GapAbove >= 0f) row["gapAbove"] = _spec.GapAbove;
        if (_spec.JoinAbove) row["joinAbove"] = true;
        // JOINLINKS1: the section band the row draws ("red" / "green") and which of its edges it carries.
        if (_spec.Section != CellSection.None)
        {
            row["section"] = SectionName(_spec.Section);
            row["sectionEdge"] = SectionEdgeName(_spec.SectionEdge);
        }
        // TILES1: a tile row names the tile edges it draws (head row "first", note row "last").
        if (HasTiles(_spec)) row["tileEdge"] = SectionEdgeName(_spec.TileEdge);
        if (_spec.Skin != CellRowSkin.Kit) row["skin"] = _spec.Skin == CellRowSkin.GameEntry ? "game-entry" : "category-heading";
        if (_header)
        {
            row["sortedKey"] = _sortedKey;
            row["sortAscending"] = _sortAscending;
        }
        return row;
    }

    private static Dictionary<string, object?> RectOf(ScreenRect r) => new()
    {
        ["x"] = r.MinX,
        ["y"] = r.MinY,
        ["w"] = r.MaxX - r.MinX,
        ["h"] = r.MaxY - r.MinY,
    };

    private static bool HasTiles(CellRowSpec spec)
    {
        foreach (var c in spec.Cells) if (c.Tile > 0) return true;
        return false;
    }

    /// <summary>JOINLINKS1: readback name of a section band.</summary>
    internal static string SectionName(CellSection section) => section switch
    {
        CellSection.Red => "red",
        CellSection.Green => "green",
        _ => "none",
    };

    /// <summary>JOINLINKS1: readback name of a section row's edges.</summary>
    internal static string SectionEdgeName(CellSectionEdge edge) => edge switch
    {
        CellSectionEdge.First | CellSectionEdge.Last => "single",
        CellSectionEdge.First => "first",
        CellSectionEdge.Last => "last",
        _ => "middle",
    };

    /// <summary>Readback name of a cell skin.</summary>
    internal static string SkinName(CellSkin skin) => skin switch
    {
        CellSkin.KeyCap => "key-cap",
        CellSkin.ResetIcon => "reset-icon",
        CellSkin.Ornament => "ornament",
        CellSkin.PopupButton => "popup-button",
        CellSkin.Quiet => "quiet",
        CellSkin.Link => "link",
        CellSkin.Expander => "expander",
        CellSkin.Dropdown => "dropdown",
        CellSkin.LoopGlyph => "loop-glyph",
        _ => "kit",
    };

    internal static string KindName(CellKind kind) => kind switch
    {
        CellKind.ScrollText => "scrolltext",
        CellKind.Image => "image",
        CellKind.Text => "text",
        CellKind.Muted => "muted",
        CellKind.Mono => "mono",
        CellKind.Badge => "badge",
        CellKind.Button => "button",
        CellKind.Chevron => "chevron",
        CellKind.Input => "input",
        CellKind.Switch => "switch",
        CellKind.Diamond => "diamond",
        _ => "spacer",
    };

    internal static string ToneName(CellTone tone) => tone switch
    {
        CellTone.Muted => "muted",
        CellTone.Gold => "gold",
        CellTone.Server => "server",
        CellTone.Client => "client",
        CellTone.Both => "both",
        CellTone.Warn => "warn",
        CellTone.Ok => "ok",
        CellTone.Pending => "pending",
        CellTone.Disabled => "disabled",
        CellTone.Danger => "danger",
        CellTone.Orange => "orange",
        CellTone.Amber => "amber",
        CellTone.SectionRedHead => "section-red-head",
        CellTone.SectionRedNote => "section-red-note",
        CellTone.SectionGreenHead => "section-green-head",
        CellTone.SectionGreenNote => "section-green-note",
        CellTone.SettingName => "setting-name",
        _ => "normal",
    };

    /// <summary>
    /// The <c>widget_read</c> row for the live, visible cell row whose key is <paramref name="key"/>,
    /// or null. The bridge substitutes it for the plain <c>widget</c> row the menu describes.
    /// </summary>
    internal static Dictionary<string, object?>? DescribeVisible(string? key)
    {
        var found = FindVisible(key);
        return found?.Describe();
    }

    /// <summary>
    /// Bridge click on a cell row: <c>&lt;rowKey&gt;</c> is the row click, <c>&lt;rowKey&gt;/&lt;cellKey&gt;</c>
    /// a button (or sortable header) cell, and a bare button caption the first visible button cell
    /// showing it. Dispatches through the same slot the Bootstrap trampoline uses.
    /// </summary>
    internal static bool TryInvoke(string? label, out Dictionary<string, object?> detail)
    {
        detail = new Dictionary<string, object?>();
        if (string.IsNullOrWhiteSpace(label)) return false;
        string rowKey = label;
        string? cellKey = null;
        int slash = label.LastIndexOf('/');
        UiCellRowHandle? row = FindVisible(rowKey);
        if (row == null && slash > 0)
        {
            rowKey = label.Substring(0, slash);
            cellKey = label.Substring(slash + 1);
            row = FindVisible(rowKey);
        }
        if (row != null)
        {
            if (cellKey == null)
            {
                if (row.WidgetId == 0) return false;
                // RADIOCLICK1: only a row the pointer can click; the bridge must not succeed where a player cannot.
                if (!row.RowClickReachable)
                {
                    detail["label"] = row.Key;
                    detail["reachable"] = false;
                    return false;
                }
                UiSlotTable.BootstrapDispatch(row.WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
                detail["label"] = row.Key;
                detail["kind"] = row._header ? "cellheader" : "cellrow";
                detail["invoked"] = 1;
                return true;
            }
            if (row._inputKeys.Contains(cellKey)) return false;
            if (!row._cellIds.TryGetValue(cellKey, out ushort id) || id == 0) return false;
            UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Click, 0f, false, null);
            detail["label"] = row.Key + "/" + cellKey;
            detail["kind"] = "cell";
            detail["invoked"] = 1;
            return true;
        }

        // A bare caption: the first visible button cell showing it.
        UiCellRowHandle[] snapshot;
        lock (LiveRows) snapshot = LiveRows.ToArray();
        for (int i = 0; i < snapshot.Length; i++)
        {
            var candidate = snapshot[i];
            if (candidate.IsDisposed || !candidate.Enabled || !candidate.Visible) continue;
            foreach (var cell in candidate._spec.Cells)
            {
                if (!cell.Visible || !string.Equals(cell.Text, label, StringComparison.OrdinalIgnoreCase)) continue;
                if (candidate._inputKeys.Contains(cell.Key)) continue;
                if (!candidate._cellIds.TryGetValue(cell.Key, out ushort id) || id == 0) continue;
                UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Click, 0f, false, null);
                detail["label"] = candidate.Key + "/" + cell.Key;
                detail["kind"] = "cell";
                detail["invoked"] = 1;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// DIALOGS3: fire one of <b>this</b> row's button cells, found by its key or (case-insensitive)
    /// by its caption, through the same slot a real click dispatches. Unlike <see cref="TryInvoke"/>
    /// it never searches other rows, so a dialog's <c>No</c> cannot land on a workspace cell that
    /// happens to show the same word.
    /// </summary>
    /// <summary>
    /// LISTROWS1: fire this row's own click slot, as a pointer click on it does. False when the row has no slot or no
    /// pointer could reach it (RADIOCLICK1: the bridge must not succeed where a player cannot).
    /// </summary>
    internal bool TryInvokeRow(out Dictionary<string, object?> detail)
    {
        detail = new Dictionary<string, object?>();
        if (IsDisposed || _header || WidgetId == 0) return false;
        if (!RowClickReachable)
        {
            detail["label"] = Key;
            detail["reachable"] = false;
            return false;
        }

        UiSlotTable.BootstrapDispatch(WidgetId, (byte)UiSlotKind.Click, 0f, false, null);
        detail["label"] = Key;
        detail["kind"] = "cellrow";
        detail["invoked"] = 1;
        return true;
    }

    internal bool TryInvokeCell(string? keyOrCaption, out Dictionary<string, object?> detail)
    {
        detail = new Dictionary<string, object?>();
        if (IsDisposed || string.IsNullOrWhiteSpace(keyOrCaption)) return false;
        string? key = null;
        foreach (var cell in _spec.Cells)
        {
            if (!string.Equals(cell.Key, keyOrCaption, StringComparison.Ordinal)) continue;
            key = cell.Key;
            break;
        }

        if (key == null)
        {
            foreach (var cell in _spec.Cells)
            {
                if (!cell.Visible || !string.Equals(cell.Text, keyOrCaption, StringComparison.OrdinalIgnoreCase)) continue;
                key = cell.Key;
                break;
            }
        }

        if (key == null || _inputKeys.Contains(key)) return false;
        if (!_cellIds.TryGetValue(key, out ushort id) || id == 0) return false;
        UiSlotTable.BootstrapDispatch(id, (byte)UiSlotKind.Click, 0f, false, null);
        detail["label"] = Key + "/" + key;
        detail["kind"] = "cell";
        detail["invoked"] = 1;
        return true;
    }

    /// <summary>
    /// RADIOCLICK1 (<c>mods_ui click_at {row}</c>): the visible row <paramref name="key"/>'s rect in Unity screen pixels
    /// (the space <c>ScreenRectOf</c> reads), so the bridge can click its center through the pointer route. False when no
    /// such row is shown or its rect is unreadable.
    /// </summary>
    internal static bool TryRowRect(string? key, out ScreenRect rect)
    {
        rect = ScreenRect.Unknown;
        string rowKey = key ?? "";
        string? cellKey = null;
        var row = FindVisible(rowKey);
        // FIELDAPPLY1: "<row>/<cell>" aims at one cell (a date row's Set button), when no row has the whole key.
        int slash = rowKey.LastIndexOf('/');
        if (row == null && slash > 0)
        {
            cellKey = rowKey.Substring(slash + 1);
            row = FindVisible(rowKey.Substring(0, slash));
        }

        if (row?._row is not { Alive: true } runtime) return false;
        try
        {
            if (cellKey == null) rect = runtime.RowRect();
            else
                foreach (var pair in runtime.CellRects())
                    if (string.Equals(pair.Key, cellKey, StringComparison.Ordinal)) rect = pair.Value;
        }
        catch { return false; }
        return rect.IsKnown;
    }

    private static UiCellRowHandle? FindVisible(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        lock (LiveRows)
        {
            for (int i = 0; i < LiveRows.Count; i++)
            {
                var row = LiveRows[i];
                if (row.IsDisposed || !row.Enabled) continue;
                if (!string.Equals(row.Key, key, StringComparison.Ordinal)) continue;
                if (!row.Visible) continue;
                return row;
            }
        }
        return null;
    }

    internal static int LiveCountForTests
    {
        get { lock (LiveRows) return LiveRows.Count; }
    }

    protected override void OnDispose()
    {
        lock (LiveRows) LiveRows.Remove(this);
        foreach (var pair in _cellIds)
        {
            try { _uiRuntime?.Unwire(pair.Value); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: cell Unwire threw: " + e.Message); }
            UiSlotTable.Release(pair.Value);
        }
        _cellIds.Clear();
        _inputKeys.Clear();
        _changed.Clear();
        _sort = null;
        base.OnDispose();
    }
}

/// <summary>
/// KIT1: the two-column head block of a detail page, as built. Three or four
/// <see cref="IUiCellRow"/>s (see <see cref="HeadBlockLayout.Build"/>) plus the handles a caller
/// needs to bind a click or rewrite the status without rebuilding the page. The rows are ordinary
/// panel children, so the panel owns their lifetime and disposing the panel releases them.
/// </summary>
internal sealed class UiHeadBlock
{
    private readonly List<IUiCellRow> _rows = new(4);

    private UiHeadBlock(HeadSpec spec, float rowWidth)
    {
        Spec = spec;
        RowWidth = rowWidth;
    }

    internal HeadSpec Spec { get; }

    /// <summary>The pane row width the block was laid out for.</summary>
    internal float RowWidth { get; }

    internal IReadOnlyList<IUiCellRow> Rows => _rows;

    /// <summary>The title / status row. Never null once a block is built.</summary>
    internal IUiCellRow? TitleRow { get; private set; }

    /// <summary>The meta / Like / action row.</summary>
    internal IUiCellRow? MetaRow { get; private set; }

    /// <summary>The one-liner / badges row, or null when the page has neither.</summary>
    internal IUiCellRow? OneLinerRow { get; private set; }

    /// <summary>The warning row, or null when nothing is wrong.</summary>
    internal IUiCellRow? WarningRow { get; private set; }

    /// <summary>True when the runtime drew the block rather than only keeping its state.</summary>
    internal bool Enabled => TitleRow is { Enabled: true };

    internal static UiHeadBlock Build(IUiPanel? panel, HeadSpec? spec, float rowWidth)
    {
        var used = spec ?? new HeadSpec();
        float width = rowWidth > 1f ? rowWidth : MenuPanelLayout.DetailRowWidth();
        var block = new UiHeadBlock(used, width);
        if (panel == null) return block;
        var specs = HeadBlockLayout.Build(used, width);
        for (int i = 0; i < specs.Count; i++)
        {
            var row = panel.CellRow(specs[i]);
            block._rows.Add(row);
            if (row.Key.EndsWith("-title", StringComparison.Ordinal)) block.TitleRow = row;
            else if (row.Key.EndsWith("-meta", StringComparison.Ordinal)) block.MetaRow = row;
            else if (row.Key.EndsWith("-oneliner", StringComparison.Ordinal)) block.OneLinerRow = row;
            else if (row.Key.EndsWith("-warning", StringComparison.Ordinal)) block.WarningRow = row;
        }
        return block;
    }

    /// <summary>Bind the inline toggle cell (the Like). A block with no toggle gets a no-op lease.</summary>
    internal IDisposable OnToggle(Action handler) =>
        MetaRow?.OnClick(HeadBlockLayout.CellLike, handler) ?? Noop.Instance;

    /// <summary>SETTINGS1 stage 2: bind one of the meta row's small buttons (<see cref="HeadSpec.MetaButtons"/>).</summary>
    internal IDisposable OnMetaButton(string key, Action handler) =>
        MetaRow?.OnClick(key, handler) ?? Noop.Instance;

    /// <summary>Bind the action button. A read-only page gets a no-op lease.</summary>
    internal IDisposable OnAction(Action handler) =>
        MetaRow?.OnClick(HeadBlockLayout.CellAction, handler) ?? Noop.Instance;

    /// <summary>Rewrite the status word and its tone in place; false when there is no status cell.</summary>
    internal bool SetStatus(string word, LabelTone tone) =>
        TitleRow?.SetCell(HeadBlockLayout.CellStatusWord, word ?? "", LabelTones.ToCellTone(tone)) ?? false;

    /// <summary>Rewrite the action caption and tone in place; false on a read-only page.</summary>
    internal bool SetAction(string caption, LabelTone tone) =>
        MetaRow?.SetCell(HeadBlockLayout.CellAction, caption ?? "", LabelTones.ToCellTone(tone)) ?? false;

    /// <summary>Rewrite the toggle caption and its pressed state in place.</summary>
    internal bool SetToggle(string caption, bool on) =>
        MetaRow?.SetCell(HeadBlockLayout.CellLike, caption ?? "", on ? CellTone.Gold : CellTone.Normal) ?? false;
}

/// <summary>
/// KIT1: the in-pane y/n prompt (the SERVERINST2 shape the mock draws for uninstall, reactivate and
/// cancel-install) as one block: a heading, a muted body, and the two buttons right-aligned on one
/// cell row. <see cref="Tag"/> is what the caller keys its readback rows by, so three panes can share
/// the block and keep their own key.
/// </summary>
internal sealed class UiPromptBlock
{
    /// <summary>The yes cell of <see cref="Buttons"/>.</summary>
    internal const string CellYes = "yes";

    /// <summary>The no cell of <see cref="Buttons"/>.</summary>
    internal const string CellNo = "no";

    private UiPromptBlock(string tag) => Tag = tag ?? "";

    /// <summary>The caller's name for this prompt; also the buttons row's key suffix.</summary>
    internal string Tag { get; }

    internal IUiLabel? Heading { get; private set; }

    internal IUiLabel? Body { get; private set; }

    internal IUiCellRow? Buttons { get; private set; }

    internal bool Enabled => Buttons is { Enabled: true };

    /// <summary>
    /// INSTALLUX2: the cell keys the buttons are drawn under. Defaulted to <see cref="CellYes"/> /
    /// <see cref="CellNo"/>; a caller whose readback and <c>widget_click</c> handles are already
    /// published under other names (the Install tab's <c>promptYes</c> / <c>promptNo</c> /
    /// <c>promptClose</c>) passes its own, so moving the prompt into a dialog does not move the
    /// handles a bridge client clicks.
    /// </summary>
    internal string YesKey { get; private set; } = CellYes;

    /// <inheritdoc cref="YesKey"/>
    internal string NoKey { get; private set; } = CellNo;

    internal static UiPromptBlock Build(
        IUiPanel? panel,
        string tag,
        string heading,
        string body,
        string yesCaption,
        LabelTone yesTone,
        string noCaption,
        string? rowKey = null,
        string? yesKey = null,
        string? noKey = null)
    {
        var block = new UiPromptBlock(tag);
        block.YesKey = string.IsNullOrEmpty(yesKey) ? CellYes : yesKey!;
        block.NoKey = string.IsNullOrEmpty(noKey) ? CellNo : noKey!;
        if (panel == null) return block;
        block.Heading = panel.Heading(heading ?? "");
        block.Body = panel.Note(body ?? "");
        // The two buttons are one right-aligned pair, which is the mock's `justify-content:flex-end`.
        // No is listed first so the column model packs Yes at the right edge.
        var cells = new List<CellSpec>(3) { new CellSpec(CellKind.Spacer, HeadBlockLayout.CellGap) };
        if (!string.IsNullOrEmpty(noCaption))
            cells.Add(new CellSpec(CellKind.Button, block.NoKey).With(text: noCaption, align: CellAlign.Right));
        if (!string.IsNullOrEmpty(yesCaption))
            cells.Add(new CellSpec(CellKind.Button, block.YesKey).With(
                text: yesCaption, align: CellAlign.Right, tone: LabelTones.ToCellTone(yesTone)));
        // CONSENT2 item 5 (INSTALLUX1 flagged it): the same right inset the head block takes.
        // Without it the pair is packed flush against the row's right edge, which is the boundary
        // of the pane's clip, and Yes — the wider of the two — is drawn under the scrollbar band
        // with its right-hand side cut off, exactly as the mod page's Uninstall button was.
        block.Buttons = panel.CellRow(new CellRowSpec(
            string.IsNullOrEmpty(rowKey)
                ? (string.IsNullOrEmpty(tag) ? "prompt" : tag) + "-buttons"
                : rowKey!,
            cells,
            rightInset: HeadBlockLayout.RightColumnInset));
        return block;
    }

    /// <summary>
    /// DIALOGS1: the block a dialog built with the shared dialog layout (<see cref="UiDialogView"/>)
    /// hands its caller — the same <see cref="Heading"/> / <see cref="Body"/> / <see cref="Buttons"/>
    /// and cell keys the in-pane prompt publishes, so a pane's readback rows and a bridge client's
    /// <c>widget_click</c> handles do not move.
    /// </summary>
    internal static UiPromptBlock ForDialog(
        string tag, IUiLabel? heading, IUiLabel? body, IUiCellRow? buttons, string yesKey, string noKey) =>
        new(tag)
        {
            Heading = heading,
            Body = body,
            Buttons = buttons,
            YesKey = string.IsNullOrEmpty(yesKey) ? CellYes : yesKey,
            NoKey = string.IsNullOrEmpty(noKey) ? CellNo : noKey,
        };

    internal IDisposable OnYes(Action handler) => Buttons?.OnClick(YesKey, handler) ?? Noop.Instance;

    internal IDisposable OnNo(Action handler) => Buttons?.OnClick(NoKey, handler) ?? Noop.Instance;
}

internal sealed class ActionDisposable : IDisposable
{
    private Action? _action;

    public ActionDisposable(Action action) => _action = action;

    public void Dispose()
    {
        var a = Interlocked.Exchange(ref _action, null);
        try { a?.Invoke(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: lease dispose threw: " + e.Message); }
    }
}

internal sealed class Noop : IDisposable
{
    public static readonly Noop Instance = new();
    public void Dispose() { }
}
