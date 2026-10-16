using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// A finite choice. On the settings grid it is the game's dropdown: a click opens the list of options under the
/// field (the current one marked); picking an option closes it and is one write (<c>OnChanged</c> fires once); Escape,
/// a click outside the list or picking the current value closes it with no write. Off the grid it is a selector, the
/// value between a previous and a next arrow, each arrow one write; a click on the value opens the same list. Create with
/// <see cref="IUiPanel.Dropdown"/>. A value that is not one of <see cref="Values"/> is kept and shown
/// as <c>value (not in list)</c> rather than coerced.
/// </summary>
public interface IUiDropdown : IUiHandle
{
    /// <summary>The stored value (one of <see cref="Values"/>, or whatever was set).</summary>
    string Value { get; set; }

    /// <summary>The stable values, in display order.</summary>
    IReadOnlyList<string> Values { get; }

    /// <summary>What each value draws as, index-aligned with <see cref="Values"/>.</summary>
    IReadOnlyList<string> Labels { get; }

    /// <summary>Called with the new value after each pick. Not called for a programmatic <see cref="Value"/> set.</summary>
    IDisposable OnChanged(Action<string> handler);
}

/// <summary>
/// A number field with a minus and a plus stepper. Typed text commits on Enter or focus loss and is
/// clamped to <see cref="Min"/>..<see cref="Max"/> and snapped to <see cref="Step"/>; a stepper click
/// commits one step at once. Create with <see cref="IUiPanel.Number"/>.
/// </summary>
public interface IUiNumber : IUiHandle
{
    double Value { get; set; }

    double Min { get; }

    double Max { get; }

    double Step { get; }

    /// <summary>Shown after the row caption; never parsed.</summary>
    string Units { get; }

    /// <summary>Called with the committed value. Not called for a programmatic <see cref="Value"/> set.</summary>
    IDisposable OnChanged(Action<double> handler);
}

/// <summary>
/// A slider with an exact number field beside it. Dragging previews locally and commits once the
/// slider settles; typing a number commits on Enter or focus loss. Create with
/// <see cref="IUiPanel.SliderField"/>.
/// </summary>
public interface IUiSliderField : IUiHandle
{
    float Value { get; set; }

    float Min { get; }

    float Max { get; }

    float Step { get; }

    /// <summary>Called with the committed value, once per settled drag or typed entry.</summary>
    IDisposable OnChanged(Action<float> handler);
}

/// <summary>
/// A color swatch with a hex field (<c>#RRGGBB</c>, or <c>#RRGGBBAA</c> when alpha is on). Typed hex
/// commits on Enter or focus loss. Create with <see cref="IUiPanel.ColorField"/>.
/// </summary>
public interface IUiColorField : IUiHandle
{
    /// <summary>The committed color as invariant hex.</summary>
    string Color { get; set; }

    bool AlphaEnabled { get; }

    /// <summary>Called with the committed hex.</summary>
    IDisposable OnChanged(Action<string> handler);
}

/// <summary>
/// A key-chord capture control bound to one registered keybind (<see cref="Keybinds"/>). Clicking it
/// waits for the next chord; Escape cancels. Create with <see cref="IUiPanel.KeyField"/>.
/// </summary>
public interface IUiKeyField : IUiHandle
{
    KeyChord Chord { get; }

    string ChordText { get; }

    bool Capturing { get; }

    /// <summary>The last refusal (a reserved or conflicting chord), or null.</summary>
    string? ConflictNotice { get; }

    bool BeginCapture();

    void CancelCapture();

    bool Clear();

    bool ResetToDefault();

    /// <summary>Called with the new chord once a capture lands.</summary>
    IDisposable OnChanged(Action<KeyChord> handler);
}

/// <summary>
/// A runtime widget built from more than one clickable part (the selector's arrows, a number's
/// steppers). Each part is wired to its own Bootstrap slot by the handle that owns the widget.
/// </summary>
internal interface IRuntimePartsWidget : IRuntimeWidget
{
    /// <summary>The clickable part named <paramref name="key"/>, or null when it was not built.</summary>
    IRuntimeWidget? Part(string key);
}

/// <summary>
/// A choice control that can report how its value caption fits (bridge readback <c>fit</c>): the value's own area and the
/// arrows' rects in screen pixels, the drawn size and whether the caption was cut.
/// </summary>
internal interface IRuntimeChoiceFit
{
    Dictionary<string, object?>? DescribeFit();
}

/// <summary>Part keys of <see cref="IRuntimePartsWidget"/> controls.</summary>
internal static class UiControlParts
{
    internal const string Previous = "prev";
    internal const string Next = "next";
    internal const string Minus = "minus";
    internal const string Plus = "plus";

    /// <summary>DROPDOWN1: the open list's click-catcher behind the options (a click outside the list dismisses it).</summary>
    internal const string ListBlocker = "list-blocker";

    /// <summary>DROPDOWN1: option <paramref name="index"/> of a dropdown's open list.</summary>
    internal static string Option(int index) => "option/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The extra Bootstrap slots one control holds for its parts. Released with the handle, so a
/// disposed control cannot be clicked through a stale part.
/// </summary>
internal sealed class UiPartSlots
{
    private readonly List<ushort> _ids = new();

    internal int Count => _ids.Count;

    internal void Wire(object? owner, IUiRuntime runtime, IRuntimeWidget? widget, string key, Action click)
    {
        if (widget is not IRuntimePartsWidget parts || runtime == null) return;
        IRuntimeWidget? part;
        try { part = parts.Part(key); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: control part '" + key + "': " + e.Message);
            return;
        }

        if (part == null) return;
        ushort id = UiSlotTable.Alloc(owner);
        try
        {
            if (!runtime.TryWire(id, UiSlotKind.Click, part))
                Plugin.Logger?.LogDiag("Ui: TryWire part '" + key + "' id=" + id + " returned false.");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: TryWire part threw: " + e.Message); }

        UiSlotTable.BindClick(id, click);
        _ids.Add(id);
    }

    /// <summary>
    /// OPTIONHOVER1: wire the pointer entering (<c>true</c>) and leaving (<c>false</c>) the part named <paramref name="key"/>
    /// to <paramref name="hovered"/>, through its own Bootstrap slot. A part the runtime built without pointer events (or a
    /// runtime with none) is skipped: hovering it then never changes the details pane.
    /// </summary>
    internal void WireHover(object? owner, IUiRuntime runtime, IRuntimeWidget? widget, string key, Action<bool> hovered)
    {
        if (widget is not IRuntimePartsWidget parts || runtime == null || hovered == null) return;
        IRuntimeWidget? part;
        try { part = parts.Part(key); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: control part '" + key + "' hover: " + e.Message);
            return;
        }

        if (part == null || part.PointerEnterEvent == null) return;
        ushort id = UiSlotTable.Alloc(owner);
        bool wired = false;
        try
        {
            wired = runtime.TryWire(id, UiSlotKind.PointerEnter, part);
            wired |= runtime.TryWire(id, UiSlotKind.PointerExit, part);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: TryWire part hover threw: " + e.Message); }
        if (!wired)
        {
            UiSlotTable.Release(id);
            return;
        }

        UiSlotTable.BindPointerEnter(id, () => hovered(true));
        UiSlotTable.BindPointerExit(id, () => hovered(false));
        _ids.Add(id);
    }

    internal void Release()
    {
        if (_ids.Count == 0) return;
        IUiRuntime? runtime = null;
        try { runtime = Ui.CurrentRuntime(); } catch { /* teardown */ }
        for (int i = 0; i < _ids.Count; i++)
        {
            try { runtime?.Unwire(_ids[i]); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: part Unwire threw: " + e.Message); }
            UiSlotTable.Release(_ids[i]);
        }

        _ids.Clear();
    }
}

/// <summary>
/// Plugin handlers held by a control handle and called directly on a commit. The Bootstrap slot of
/// the control carries the raw widget event instead (a click, a typed edit, a slider sample), so the
/// two never share a slot kind.
/// </summary>
internal sealed class UiChangeHandlers<T>
{
    private readonly List<Action<T>> _handlers = new();

    internal IDisposable Add(Action<T> handler)
    {
        _handlers.Add(handler);
        return new ActionDisposable(() => _handlers.Remove(handler));
    }

    internal void Raise(T value)
    {
        if (_handlers.Count == 0) return;
        var copy = _handlers.ToArray();
        for (int i = 0; i < copy.Length; i++)
        {
            try { copy[i](value); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: control change handler threw: " + e.Message); }
        }
    }

    internal void Clear() => _handlers.Clear();
}

/// <summary>
/// OPTIONHOVER1 (requested 2026-10-06: "When hovering over the different settings / difficulties the tool tip should
/// update"): a choice control whose options report the pointer over them — a dropdown's open list, a radio list's lines.
/// The settings page feeds the hovered option to its details pane (<see cref="ModSettingsDescription.ForOption"/>).
/// </summary>
internal interface IUiOptionHover : IUiHandle
{
    /// <summary>The options, with their <see cref="UiChoiceOption.Description"/>.</summary>
    IReadOnlyList<UiChoiceOption> Options { get; }

    /// <summary>The option under the pointer, or -1.</summary>
    int HoveredOption { get; }

    /// <summary>
    /// The pointer is over option <paramref name="index"/> (-1: over none). What the pointer slots call, and the bridge's
    /// <c>mods_ui hover</c>. False when nothing changed or the option cannot be hovered now (a dropdown whose list is closed).
    /// </summary>
    bool HoverOption(int index);

    /// <summary>Called with the hovered index (-1 when the pointer leaves the options or the list closes) on each change.</summary>
    IDisposable OnOptionHover(Action<int> handler);
}

/// <summary>OPTIONHOVER1: the hovered-option state and its listeners, shared by the choice handles. Game-free.</summary>
internal sealed class UiOptionHoverState
{
    private readonly UiChangeHandlers<int> _changed = new();

    internal int Index { get; private set; } = -1;

    /// <summary>Hover <paramref name="index"/> (-1: none). True, and the listeners told, when it changed.</summary>
    internal bool Set(int index)
    {
        if (index < -1) index = -1;
        if (index == Index) return false;
        Index = index;
        _changed.Raise(index);
        return true;
    }

    /// <summary>The pointer left option <paramref name="index"/>: none is hovered, unless another one already is.</summary>
    internal bool Leave(int index) => Index == index && Set(-1);

    internal IDisposable Add(Action<int> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        return _changed.Add(handler);
    }

    internal void Clear()
    {
        Index = -1;
        _changed.Clear();
    }
}

/// <summary>
/// Settings-kit control handles. The internal interfaces carry the full state machine (drafts,
/// highlight, commit policies) for Core and its tests; plugins see the published façades above
/// (<see cref="IUiDropdown"/>, <see cref="IUiNumber"/>, <see cref="IUiSliderField"/>,
/// <see cref="IUiColorField"/>, <see cref="IUiKeyField"/>), which the same handle objects implement.
/// </summary>
internal interface IUiControlHost
{
    IUiChoice Choice(string text, IReadOnlyList<UiChoiceOption> options, string value);

    IUiNumeric Numeric(string text, double value, double min, double max, double step, int decimals = 0, string? units = null);

    IUiSliderEntry SliderEntry(string text, float min, float max, float step, float initial);

    IUiColorPicker ColorPicker(string text, UiColor initial, bool alpha = false);

    IUiKeybindPicker KeybindPicker(string text, string pluginId, string bindId);

    /// <summary>SETTINGS1 stage 2: a multi-line field.</summary>
    IUiInput TextArea(string text, string initial, int lines);

    /// <summary>SETTINGS1 stage 2: a radio group (<paramref name="multi"/> false) or a multi-select.</summary>
    UiOptionsHandle Options(
        string text, IReadOnlyList<UiChoiceOption> options, IEnumerable<string>? selected, bool multi, UiOptionsLayout layout);

    /// <summary>SETTINGS1 stage 2: a live read-only line.</summary>
    IUiInfo Info(string text, Func<string> value, double refreshSeconds);

    /// <summary>SETTINGS1 stage 2: a horizontal rule.</summary>
    IUiLabel Divider();
}

/// <summary>Dropdown / finite choice. Commit: <see cref="UiCommitPolicy.Immediate"/>.</summary>
internal interface IUiChoice : IUiHandle
{
    UiCommitPolicy Commit { get; }

    string Value { get; set; }

    string DisplayLabel { get; }

    bool ValueInList { get; }

    bool IsOpen { get; }

    int HighlightIndex { get; }

    IReadOnlyList<UiChoiceOption> Options { get; }

    void Open();

    void Close();

    bool MoveHighlight(int delta);

    bool ConfirmHighlight();

    IDisposable OnChanged(Action<string> handler);
}

/// <summary>
/// Numeric field + stepper. Typed text: <see cref="UiCommitPolicy.OnSubmit"/>.
/// Stepper click: <see cref="UiCommitPolicy.Immediate"/>.
/// </summary>
internal interface IUiNumeric : IUiHandle
{
    UiCommitPolicy TypedCommit { get; }

    UiCommitPolicy StepperCommit { get; }

    double Value { get; set; }

    string Draft { get; }

    string Units { get; }

    double Min { get; }

    double Max { get; }

    double Step { get; }

    void SetDraft(string text);

    bool TryCommitDraft();

    void CancelDraft();

    bool StepBy(int steps);

    IDisposable OnChanged(Action<double> handler);
}

/// <summary>
/// Slider + exact numeric. Drag: <see cref="UiCommitPolicy.OnRelease"/> (preview
/// locally, one commit on release). Keyboard: immediate. Exact entry: on submit.
/// </summary>
internal interface IUiSliderEntry : IUiHandle
{
    UiCommitPolicy DragCommit { get; }

    UiCommitPolicy KeyboardCommit { get; }

    UiCommitPolicy ExactCommit { get; }

    float Value { get; set; }

    float Preview { get; }

    float Min { get; }

    float Max { get; }

    float Step { get; }

    bool Dragging { get; }

    void BeginDrag();

    void DragTo(float value);

    bool EndDrag();

    bool Nudge(int steps);

    bool TryCommitExact(string text);

    IDisposable OnChanged(Action<float> handler);

    IDisposable OnPreview(Action<float> handler);
}

/// <summary>Swatch + hex / components. Typed hex: <see cref="UiCommitPolicy.OnSubmit"/>.</summary>
internal interface IUiColorPicker : IUiHandle
{
    UiCommitPolicy TypedCommit { get; }

    UiColor Value { get; set; }

    bool AlphaEnabled { get; }

    string Hex { get; }

    void SetHexDraft(string hex);

    bool TryCommitHex();

    bool SetComponent(UiColorChannel channel, float amount);

    IDisposable OnChanged(Action<UiColor> handler);
}

/// <summary>
/// Keybind capture / cancel / clear / reset. Reuses <see cref="Keybinds"/>.
/// Commit: <see cref="UiCommitPolicy.Immediate"/> when capture lands.
/// </summary>
internal interface IUiKeybindPicker : IUiHandle
{
    UiCommitPolicy Commit { get; }

    string BindPluginId { get; }

    string BindId { get; }

    KeyChord Chord { get; }

    string ChordText { get; }

    bool Capturing { get; }

    string? ConflictNotice { get; }

    bool BeginCapture();

    void CancelCapture();

    bool Clear();

    bool ResetToDefault();

    IDisposable OnChanged(Action<KeyChord> handler);
}

/// <summary>
/// Factory for the settings-kit primitives. The contract owner binds through these
/// from <c>SettingsEditors</c> once descriptor names settle.
/// </summary>
internal static class UiPrimitives
{
    internal static IUiChoice Choice(IUiPanel panel, string text, IReadOnlyList<UiChoiceOption> options, string value) =>
        Host(panel)?.Choice(text, options, value) ?? DisabledChoice(panel, options, value);

    internal static IUiNumeric Numeric(
        IUiPanel panel, string text, double value, double min, double max, double step, int decimals = 0, string? units = null) =>
        Host(panel)?.Numeric(text, value, min, max, step, decimals, units)
        ?? DisabledNumeric(panel, value, min, max, step, decimals, units);

    internal static IUiSliderEntry SliderEntry(IUiPanel panel, string text, float min, float max, float step, float initial) =>
        Host(panel)?.SliderEntry(text, min, max, step, initial)
        ?? DisabledSliderEntry(panel, min, max, step, initial);

    internal static IUiColorPicker ColorPicker(IUiPanel panel, string text, UiColor initial, bool alpha = false) =>
        Host(panel)?.ColorPicker(text, initial, alpha)
        ?? DisabledColor(panel, initial, alpha);

    internal static IUiKeybindPicker KeybindPicker(IUiPanel panel, string text, string pluginId, string bindId) =>
        Host(panel)?.KeybindPicker(text, pluginId, bindId)
        ?? DisabledKeybind(panel, pluginId, bindId);

    private static IUiControlHost? Host(IUiPanel panel) => panel as IUiControlHost;

    private static IUiChoice DisabledChoice(IUiPanel panel, IReadOnlyList<UiChoiceOption> options, string value)
    {
        string plugin = panel?.PluginId ?? UiOwnership.UnownedId;
        string root = panel?.RootName ?? UiOwnership.RootName(plugin);
        return new UiChoiceHandle(plugin, root, null, null, false, 0, options, value);
    }

    private static IUiNumeric DisabledNumeric(
        IUiPanel panel, double value, double min, double max, double step, int decimals, string? units)
    {
        string plugin = panel?.PluginId ?? UiOwnership.UnownedId;
        string root = panel?.RootName ?? UiOwnership.RootName(plugin);
        return new UiNumericHandle(plugin, root, null, null, false, 0, value, min, max, step, decimals, units);
    }

    private static IUiSliderEntry DisabledSliderEntry(IUiPanel panel, float min, float max, float step, float initial)
    {
        string plugin = panel?.PluginId ?? UiOwnership.UnownedId;
        string root = panel?.RootName ?? UiOwnership.RootName(plugin);
        return new UiSliderEntryHandle(plugin, root, null, null, false, 0, min, max, step, initial);
    }

    private static IUiColorPicker DisabledColor(IUiPanel panel, UiColor initial, bool alpha)
    {
        string plugin = panel?.PluginId ?? UiOwnership.UnownedId;
        string root = panel?.RootName ?? UiOwnership.RootName(plugin);
        return new UiColorPickerHandle(plugin, root, null, null, false, 0, initial, alpha);
    }

    private static IUiKeybindPicker DisabledKeybind(IUiPanel panel, string pluginId, string bindId)
    {
        string plugin = panel?.PluginId ?? UiOwnership.UnownedId;
        string root = panel?.RootName ?? UiOwnership.RootName(plugin);
        return new UiKeybindPickerHandle(plugin, root, null, null, false, 0, pluginId, bindId);
    }
}

/// <summary>The published <see cref="IUiPanel"/> control factories, routed through <see cref="UiPrimitives"/>.</summary>
internal static class UiControls
{
    internal static IUiDropdown Dropdown(
        IUiPanel panel, string text, IReadOnlyList<string>? values, string value, IReadOnlyList<string>? labels,
        IReadOnlyList<string>? descriptions = null)
    {
        var options = new List<UiChoiceOption>(values?.Count ?? 0);
        if (values != null)
        {
            for (int i = 0; i < values.Count; i++)
            {
                string? label = labels != null && i < labels.Count ? labels[i] : null;
                string? description = descriptions != null && i < descriptions.Count ? descriptions[i] : null;
                options.Add(new UiChoiceOption(values[i] ?? "", label, description));
            }
        }

        return (IUiDropdown)UiPrimitives.Choice(panel, text ?? "", options, value ?? "");
    }

    internal static IUiNumber Number(
        IUiPanel panel, string text, double value, double min, double max, double step, int decimals, string? units) =>
        (IUiNumber)UiPrimitives.Numeric(panel, text ?? "", value, min, max, step, decimals, units);

    internal static IUiSliderField SliderField(IUiPanel panel, string text, float min, float max, float step, float value) =>
        (IUiSliderField)UiPrimitives.SliderEntry(panel, text ?? "", min, max, step, value);

    internal static IUiColorField ColorField(IUiPanel panel, string text, string? hex, bool alpha)
    {
        if (!UiColor.TryParseHex(hex, alpha, out var color) && !UiColor.TryParseHex(hex, false, out color))
            color = UiColor.Black;
        return (IUiColorField)UiPrimitives.ColorPicker(panel, text ?? "", color, alpha);
    }

    internal static IUiKeyField KeyField(IUiPanel panel, string text, string pluginId, string bindId) =>
        (IUiKeyField)UiPrimitives.KeybindPicker(panel, text ?? "", pluginId ?? "", bindId ?? "");
}

internal sealed class UiChoiceHandle : UiHandle, IUiChoice, IUiDropdown, IUiOptionHover
{
    private readonly UiChoiceState _state;
    private readonly UiOptionHoverState _hover = new();
    private readonly UiChangeHandlers<string> _changed = new();
    private readonly UiPartSlots _parts = new();

    internal UiChoiceHandle(
        string pluginId, string rootName, object? owner, IRuntimeWidget? runtime, bool enabled, ushort id,
        IReadOnlyList<UiChoiceOption> options, string value)
        : base(pluginId, rootName, owner, runtime, enabled)
    {
        WidgetId = id;
        _state = new UiChoiceState(options, value);
        WriteWidget();
    }

    public UiCommitPolicy Commit => UiChoiceState.Commit;

    /// <summary>HUB1: a compact dropdown (<see cref="UiWidgets.CompactDropdown"/>): Left and Right move between the row's dropdowns.</summary>
    internal bool Compact { get; set; }

    public IReadOnlyList<UiChoiceOption> Options => _state.Options;

    public IReadOnlyList<string> Values
    {
        get
        {
            var values = new string[_state.Options.Count];
            for (int i = 0; i < values.Length; i++) values[i] = _state.Options[i].Value;
            return values;
        }
    }

    public IReadOnlyList<string> Labels
    {
        get
        {
            var labels = new string[_state.Options.Count];
            for (int i = 0; i < labels.Length; i++) labels[i] = _state.Options[i].Label;
            return labels;
        }
    }

    public string Value
    {
        get => _state.Value;
        set
        {
            _state.Select(value);
            WriteWidget();
        }
    }

    public string DisplayLabel => _state.DisplayLabel;

    public bool ValueInList => _state.ValueInList;

    public bool IsOpen => _state.IsOpen;

    public int HighlightIndex => _state.HighlightIndex;

    /// <summary>Slot ids the arrows hold. Tests.</summary>
    internal int PartSlotCount => _parts.Count;

    public void Open() => OpenList();

    public void Close() => Dismiss();

    // ---- DROPDOWN1 (user review 2026-10-06): a control drawn as a dropdown behaves as one ------------------------

    private static UiChoiceHandle? _openNow;
    private IDisposable? _escape;

    /// <summary>The choice whose list is open now (one at a time), or null.</summary>
    internal static UiChoiceHandle? OpenNow => _openNow is { IsDisposed: false, IsOpen: true } open ? open : null;

    /// <summary>True while any choice's list is open (Escape dismisses it before anything else hides).</summary>
    internal static bool AnyOpen => OpenNow != null;

    /// <summary>
    /// A click on the field: open the list under it (the current value marked), or close it when it is open. Never
    /// writes the value.
    /// </summary>
    internal void ToggleList()
    {
        if (_state.IsOpen) Dismiss();
        else OpenList();
    }

    /// <summary>Open the list (closing any other choice's list first). No write.</summary>
    internal void OpenList()
    {
        if (IsDisposed) return;
        var other = _openNow;
        if (other != null && !ReferenceEquals(other, this)) other.Dismiss();
        _state.Open();
        _openNow = this;
        if (_escape == null)
        {
            try
            {
                _escape = Ui.PushEscape(() =>
                {
                    if (!_state.IsOpen) return false;
                    Dismiss();
                    // The press is this list's: the workspace's search clear never acts on it too.
                    WorkspaceInputPolicy.NotePressTaken(EscapeStage.AnswerDialog);
                    return true;
                });
            }
            catch { _escape = null; }
        }

        WriteWidget();
    }

    /// <summary>Close the list without a write (Escape, a click outside it, a click on the field again).</summary>
    internal void Dismiss()
    {
        _state.Close();
        EndOpen();
        WriteWidget();
    }

    // ---- OPTIONHOVER1: the open list's option under the pointer feeds the details pane ---------------------------

    public int HoveredOption => _hover.Index;

    /// <summary>
    /// The pointer is over option <paramref name="index"/> of the open list (-1: over none). A closed list hovers nothing:
    /// only -1 is taken then. True when the hovered option changed.
    /// </summary>
    public bool HoverOption(int index)
    {
        if (index >= _state.Options.Count) return false;
        if (index >= 0 && (!_state.IsOpen || IsDisposed)) return false;
        // The highlight follows the pointer (and stays on the last option it was over when it leaves the list).
        if (index >= 0 && _state.SetHighlight(index)) WriteWidget();
        return _hover.Set(index);
    }

    public IDisposable OnOptionHover(Action<int> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        return _hover.Add(handler);
    }

    /// <summary>
    /// Pick option <paramref name="value"/> (its value, or else its caption) from the open list: the list closes, and a
    /// value other than the current one is applied once (one <see cref="OnChanged"/> call: one write). Picking the current
    /// value only closes. False when nothing was written.
    /// </summary>
    internal bool Choose(string? value)
    {
        int index = IndexOfValueOrLabel(value);
        if (index < 0)
        {
            Dismiss();
            return false;
        }

        return ChooseIndex(index);
    }

    /// <summary>Option <paramref name="index"/> of the list, as <see cref="Choose"/>.</summary>
    internal bool ChooseIndex(int index)
    {
        if (index < 0 || index >= _state.Options.Count)
        {
            Dismiss();
            return false;
        }

        bool changed = _state.Select(_state.Options[index].Value);
        EndOpen();
        WriteWidget();
        if (changed) Raise();
        return changed;
    }

    /// <summary>The readback: <c>dropdown {open, options [{value, label, description, current}], highlight, hovered}</c>.</summary>
    internal Dictionary<string, object?> Describe()
    {
        var d = new Dictionary<string, object?> { ["dropdown"] = DescribeList() };
        try
        {
            if (Runtime is IRuntimeChoiceFit fit && fit.DescribeFit() is { } shown) d["fit"] = shown;
        }
        catch { /* the list readback stands alone */ }
        return d;
    }

    /// <summary><c>{open, options [{value, label, description, current}], highlight, hovered}</c>.</summary>
    internal Dictionary<string, object?> DescribeList()
    {
        var options = new List<object?>(_state.Options.Count);
        for (int i = 0; i < _state.Options.Count; i++)
        {
            var o = _state.Options[i];
            options.Add(new Dictionary<string, object?>
            {
                ["value"] = o.Value,
                ["label"] = o.Label,
                ["description"] = o.Description,
                ["current"] = string.Equals(o.Value, _state.Value, StringComparison.Ordinal),
            });
        }

        return new Dictionary<string, object?>
        {
            ["open"] = _state.IsOpen,
            ["options"] = options,
            ["highlight"] = _state.HighlightIndex,
            ["hovered"] = _hover.Index,
        };
    }

    private int IndexOfValueOrLabel(string? value)
    {
        string v = value ?? "";
        for (int i = 0; i < _state.Options.Count; i++)
            if (string.Equals(_state.Options[i].Value, v, StringComparison.Ordinal)) return i;
        for (int i = 0; i < _state.Options.Count; i++)
            if (string.Equals(_state.Options[i].Label, v, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private void EndOpen()
    {
        // OPTIONHOVER1: a closed list hovers nothing; the details pane goes back to the setting.
        _hover.Set(-1);
        if (ReferenceEquals(_openNow, this)) _openNow = null;
        var lease = _escape;
        _escape = null;
        try { lease?.Dispose(); }
        catch { /* the handler is gone either way */ }
    }

    public bool MoveHighlight(int delta)
    {
        bool moved = _state.MoveHighlight(delta);
        if (moved) WriteWidget();
        return moved;
    }

    public bool ConfirmHighlight()
    {
        bool changed = _state.ConfirmHighlight();
        EndOpen();
        WriteWidget();
        if (changed) Raise();
        return changed;
    }

    /// <summary>
    /// The selector's arrows: move to the previous or next option, wrapping at the ends, and commit
    /// it. A value that is not in the list steps to the first (next) or last (previous) option.
    /// </summary>
    internal bool Cycle(int delta)
    {
        int count = _state.Options.Count;
        if (count == 0 || delta == 0) return false;
        int index = -1;
        for (int i = 0; i < count; i++)
        {
            if (!string.Equals(_state.Options[i].Value, _state.Value, StringComparison.Ordinal)) continue;
            index = i;
            break;
        }

        int next = index < 0
            ? (delta > 0 ? 0 : count - 1)
            : ((index + delta) % count + count) % count;
        string before = _state.Value;
        _state.Select(_state.Options[next].Value);
        WriteWidget();
        if (string.Equals(before, _state.Value, StringComparison.Ordinal)) return false;
        Raise();
        return true;
    }

    public IDisposable OnChanged(Action<string> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (WidgetId == 0) return Noop.Instance;
        return _changed.Add(handler);
    }

    /// <summary>
    /// Wire the raw widget events. DROPDOWN1 (user review 2026-10-06: Game mode, drawn as a dropdown, changed and wrote
    /// its value on every click): a click on the field opens the list under it and never writes; an option of the list
    /// applies once; the click-catcher behind the list dismisses it. The off-grid selector's arrows still step back and
    /// forth (a selector look, not the dropdown art; the dropdown hides them). Called once by the panel that built the
    /// control.
    /// </summary>
    internal void Attach(IUiRuntime runtime)
    {
        if (WidgetId == 0 || runtime == null) return;
        UiSlotTable.BindClick(WidgetId, ToggleList);
        _parts.Wire(Owner, runtime, Runtime, UiControlParts.Previous, () => Cycle(-1));
        _parts.Wire(Owner, runtime, Runtime, UiControlParts.Next, () => Cycle(+1));
        _parts.Wire(Owner, runtime, Runtime, UiControlParts.ListBlocker, Dismiss);
        for (int i = 0; i < _state.Options.Count; i++)
        {
            int index = i;
            _parts.Wire(Owner, runtime, Runtime, UiControlParts.Option(i), () => ChooseIndex(index));
            // OPTIONHOVER1: the pointer over an option of the open list.
            _parts.WireHover(Owner, runtime, Runtime, UiControlParts.Option(i), entered =>
            {
                if (entered) HoverOption(index);
                else _hover.Leave(index);
            });
        }
    }

    protected override void OnDispose()
    {
        _state.Close();
        EndOpen();
        _hover.Clear();
        _changed.Clear();
        _parts.Release();
        base.OnDispose();
    }

    private void WriteWidget()
    {
        if (Runtime is not { Alive: true } r) return;
        // The label, not the stored value: an enum's value and caption are the same word, but a
        // choice list with captions (Default install target) must show the caption.
        r.StringValue = _state.DisplayLabel;
        r.BoolValue = _state.IsOpen;
        r.FloatValue = _state.HighlightIndex;
    }

    private void Raise() => _changed.Raise(_state.Value);
}

internal sealed class UiNumericHandle : UiHandle, IUiNumeric, IUiNumber
{
    private readonly UiNumericState _state;
    private readonly UiChangeHandlers<double> _changed = new();
    private readonly UiPartSlots _parts = new();

    internal UiNumericHandle(
        string pluginId, string rootName, object? owner, IRuntimeWidget? runtime, bool enabled, ushort id,
        double value, double min, double max, double step, int decimals, string? units)
        : base(pluginId, rootName, owner, runtime, enabled)
    {
        WidgetId = id;
        _state = new UiNumericState(value, min, max, step, decimals, units);
        WriteWidget();
    }

    public UiCommitPolicy TypedCommit => UiNumericState.TypedCommit;

    public UiCommitPolicy StepperCommit => UiNumericState.StepperCommit;

    public double Min => _state.Min;

    public double Max => _state.Max;

    public double Step => _state.Step;

    public string Units => _state.Units;

    public string Draft => _state.Draft;

    internal int PartSlotCount => _parts.Count;

    public double Value
    {
        get => _state.Committed;
        set
        {
            _state.SetCommitted(value);
            WriteWidget();
        }
    }

    public void SetDraft(string text)
    {
        _state.SetDraft(text);
        WriteWidget();
    }

    public bool TryCommitDraft()
    {
        bool changed = _state.TryCommitDraft(out _);
        WriteWidget();
        if (changed) Raise();
        return changed;
    }

    public void CancelDraft()
    {
        _state.CancelDraft();
        WriteWidget();
    }

    public bool StepBy(int steps)
    {
        bool changed = _state.StepBy(steps);
        WriteWidget();
        if (changed) Raise();
        return changed;
    }

    public IDisposable OnChanged(Action<double> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (WidgetId == 0) return Noop.Instance;
        return _changed.Add(handler);
    }

    /// <summary>
    /// Wire the raw widget events: the field's end-of-edit commits the typed text, the steppers
    /// commit one step each.
    /// </summary>
    internal void Attach(IUiRuntime runtime)
    {
        if (WidgetId == 0 || runtime == null) return;
        UiSlotTable.BindText(WidgetId, text =>
        {
            _state.SetDraft(text);
            TryCommitDraft();
        });
        _parts.Wire(Owner, runtime, Runtime, UiControlParts.Minus, () => StepBy(-1));
        _parts.Wire(Owner, runtime, Runtime, UiControlParts.Plus, () => StepBy(+1));
    }

    protected override void OnDispose()
    {
        _changed.Clear();
        _parts.Release();
        base.OnDispose();
    }

    private void WriteWidget()
    {
        if (Runtime is not { Alive: true } r) return;
        r.FloatValue = (float)_state.Committed;
        r.StringValue = _state.Draft;
    }

    private void Raise() => _changed.Raise(_state.Committed);
}

internal sealed class UiSliderEntryHandle : UiHandle, IUiSliderEntry, IUiSliderField
{
    private readonly UiSliderEntryState _state;
    private readonly UiChangeHandlers<float> _changed = new();
    private Action<float>? _preview;
    private int _settleGeneration;

    /// <summary>
    /// How long a dragged slider must rest before its value is committed. Unity's slider raises one
    /// event per drag sample and nothing on release, so "settled" is the release the commit policy
    /// asks for: one write per drag, never a write per frame.
    /// </summary>
    internal const double SettleSeconds = 0.35;

    /// <summary>
    /// Schedules the settle commit. Null in the game-free build, where a sample commits at once so
    /// a test sees the write; the client posts it through <c>MainThread.PostDelayed</c>.
    /// </summary>
    internal static Action<double, Action>? SettleScheduler { get; set; }
#if !PROGENITOR_TESTS
        = static (seconds, action) => Progenitor.Dev.MainThread.PostDelayed(seconds, action);
#endif

    internal UiSliderEntryHandle(
        string pluginId, string rootName, object? owner, IRuntimeWidget? runtime, bool enabled, ushort id,
        float min, float max, float step, float initial)
        : base(pluginId, rootName, owner, runtime, enabled)
    {
        WidgetId = id;
        _state = new UiSliderEntryState(initial, min, max, step);
        WriteWidget();
    }

    public UiCommitPolicy DragCommit => UiSliderEntryState.DragCommit;

    public UiCommitPolicy KeyboardCommit => UiSliderEntryState.KeyboardCommit;

    public UiCommitPolicy ExactCommit => UiSliderEntryState.ExactCommit;

    public float Min => _state.Min;

    public float Max => _state.Max;

    public float Step => _state.Step;

    public float Preview => _state.Preview;

    public bool Dragging => _state.Dragging;

    public float Value
    {
        get => _state.Committed;
        set
        {
            _state.SetCommitted(value);
            WriteWidget();
        }
    }

    public void BeginDrag()
    {
        _state.BeginDrag();
        WriteWidget();
    }

    public void DragTo(float value)
    {
        _state.DragTo(value);
        WriteWidget();
        try { _preview?.Invoke(_state.Preview); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: slider preview threw: " + e.Message); }
    }

    public bool EndDrag()
    {
        bool changed = _state.EndDrag();
        WriteWidget();
        if (changed) Raise();
        return changed;
    }

    public bool Nudge(int steps)
    {
        bool changed = _state.Nudge(steps);
        WriteWidget();
        if (changed) Raise();
        return changed;
    }

    public bool TryCommitExact(string text)
    {
        bool changed = _state.TryCommitDraft(text, out _);
        WriteWidget();
        if (changed) Raise();
        return changed;
    }

    public IDisposable OnChanged(Action<float> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (WidgetId == 0) return Noop.Instance;
        return _changed.Add(handler);
    }

    public IDisposable OnPreview(Action<float> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        _preview = handler;
        return new ActionDisposable(() => _preview = null);
    }

    /// <summary>
    /// One raw slider sample: preview it, and commit once the slider has rested
    /// <see cref="SettleSeconds"/>. A sample equal to the committed value while no drag is open is
    /// the echo of this handle's own write and is ignored. Unity's slider reports no release, so a
    /// player who holds the handle still for longer than the settle time mid-drag commits that
    /// intermediate value, and the rest of the drag commits again when it settles.
    /// </summary>
    internal void Sample(float value)
    {
        if (IsDisposed) return;
        if (!_state.Dragging && Math.Abs(_state.Snap(value) - _state.Committed) < 1e-6f) return;
        if (!_state.Dragging) _state.BeginDrag();
        DragTo(value);
        int generation = ++_settleGeneration;
        var scheduler = SettleScheduler;
        if (scheduler == null)
        {
            EndDrag();
            return;
        }

        CommitPending = true;
        try
        {
            scheduler(SettleSeconds, () =>
            {
                if (IsDisposed || generation != _settleGeneration || !_state.Dragging) return;
                CommitPending = false;
                EndDrag();
            });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: slider settle schedule threw: " + e.Message);
            CommitPending = false;
            EndDrag();
        }
    }

    /// <summary>Wire the raw widget events: slider samples, and the exact field's end-of-edit.</summary>
    internal void Attach(IUiRuntime runtime)
    {
        if (WidgetId == 0 || runtime == null) return;
        UiSlotTable.BindFloat(WidgetId, Sample);
        UiSlotTable.BindText(WidgetId, text => TryCommitExact(text));
        // The panel wired the slider (Float); the exact field is the same id's Text kind.
        if (Runtime?.TextEvent != null)
        {
            try { runtime.TryWire(WidgetId, UiSlotKind.Text, Runtime); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: slider field wire threw: " + e.Message); }
        }
    }

    /// <summary>True while a settle commit is scheduled and has not run.</summary>
    internal bool CommitPending { get; private set; }

    /// <summary>
    /// Review finding 4: a drag the page closes on (Escape, a tab switch, a Core swap) inside the
    /// settle window is committed here rather than lost. The flush runs before the handlers are
    /// cleared, so the owner still sees it.
    /// </summary>
    protected override void OnDispose()
    {
        if (CommitPending && _state.Dragging)
        {
            CommitPending = false;
            try { EndDrag(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: slider flush on dispose threw: " + e.Message); }
        }

        CommitPending = false;
        _changed.Clear();
        _preview = null;
        base.OnDispose();
    }

    private void WriteWidget()
    {
        if (Runtime is not { Alive: true } r) return;
        r.FloatValue = _state.Preview;
        r.StringValue = _state.Draft;
        r.BoolValue = _state.Dragging;
    }

    private void Raise() => _changed.Raise(_state.Committed);
}

internal sealed class UiColorPickerHandle : UiHandle, IUiColorPicker, IUiColorField
{
    private readonly UiColorState _state;
    private readonly UiChangeHandlers<UiColor> _changed = new();
    private readonly UiChangeHandlers<string> _changedHex = new();

    internal UiColorPickerHandle(
        string pluginId, string rootName, object? owner, IRuntimeWidget? runtime, bool enabled, ushort id,
        UiColor initial, bool alpha)
        : base(pluginId, rootName, owner, runtime, enabled)
    {
        WidgetId = id;
        _state = new UiColorState(initial, alpha);
        WriteWidget();
    }

    public UiCommitPolicy TypedCommit => UiColorState.TypedCommit;

    public bool AlphaEnabled => _state.AlphaEnabled;

    public string Hex => _state.HexDraft;

    public UiColor Value
    {
        get => _state.Value;
        set
        {
            _state.SetValue(value);
            WriteWidget();
        }
    }

    public string Color
    {
        get => _state.HexCommitted;
        set
        {
            if (!UiColor.TryParseHex(value, _state.AlphaEnabled, out var parsed)
                && !UiColor.TryParseHex(value, false, out parsed))
                return;
            _state.SetValue(parsed);
            WriteWidget();
        }
    }

    public void SetHexDraft(string hex)
    {
        _state.SetHexDraft(hex);
        WriteWidget();
    }

    public bool TryCommitHex()
    {
        bool changed = _state.TryCommitHex(out _);
        WriteWidget();
        if (changed) Raise();
        return changed;
    }

    public bool SetComponent(UiColorChannel channel, float amount)
    {
        bool changed = _state.SetComponent(channel, amount);
        WriteWidget();
        if (changed) Raise();
        return changed;
    }

    public IDisposable OnChanged(Action<UiColor> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (WidgetId == 0) return Noop.Instance;
        return _changed.Add(handler);
    }

    public IDisposable OnChanged(Action<string> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (WidgetId == 0) return Noop.Instance;
        return _changedHex.Add(handler);
    }

    /// <summary>Wire the raw widget event: the hex field's end-of-edit commits it.</summary>
    internal void Attach(IUiRuntime runtime)
    {
        if (WidgetId == 0 || runtime == null) return;
        UiSlotTable.BindText(WidgetId, text =>
        {
            _state.SetHexDraft(text);
            TryCommitHex();
        });
    }

    protected override void OnDispose()
    {
        _changed.Clear();
        _changedHex.Clear();
        base.OnDispose();
    }

    private void WriteWidget()
    {
        if (Runtime is not { Alive: true } r) return;
        r.StringValue = _state.HexCommitted;
        r.FloatValue = _state.Value.R;
        r.BoolValue = _state.AlphaEnabled;
    }

    private void Raise()
    {
        _changed.Raise(_state.Value);
        _changedHex.Raise(_state.HexCommitted);
    }
}

internal sealed class UiKeybindPickerHandle : UiHandle, IUiKeybindPicker, IUiKeyField
{
    private readonly UiKeybindPickerState _state;
    private readonly Action<RebindResult> _finished;
    private readonly UiChangeHandlers<KeyChord> _changed = new();

    internal UiKeybindPickerHandle(
        string pluginId, string rootName, object? owner, IRuntimeWidget? runtime, bool enabled, ushort id,
        string bindPluginId, string bindId)
        : base(pluginId, rootName, owner, runtime, enabled)
    {
        WidgetId = id;
        _state = new UiKeybindPickerState(bindPluginId, bindId);
        _finished = OnRebindFinished;
        try
        {
            Keybinds.RebindFinished += _finished;
            Keybinds.RebindRefused += _finished;
        }
        catch { /* */ }
        WriteWidget();
    }

    public UiCommitPolicy Commit => UiKeybindPickerState.Commit;

    public string BindPluginId => _state.PluginId;

    public string BindId => _state.BindId;

    public KeyChord Chord => _state.Chord;

    public string ChordText => _state.DisplayText;

    public bool Capturing => _state.Capturing;

    public string? ConflictNotice => _state.ConflictNotice;

    public bool BeginCapture()
    {
        bool started = _state.BeginCapture();
        WriteWidget();
        return started;
    }

    public void CancelCapture()
    {
        _state.CancelCapture();
        WriteWidget();
    }

    public bool Clear()
    {
        bool ok = _state.Clear();
        WriteWidget();
        return ok;
    }

    public bool ResetToDefault()
    {
        bool ok = _state.ResetToDefault();
        WriteWidget();
        return ok;
    }

    public IDisposable OnChanged(Action<KeyChord> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (WidgetId == 0) return Noop.Instance;
        return _changed.Add(handler);
    }

    /// <summary>Wire the raw widget event: a click starts the capture.</summary>
    internal void Attach(IUiRuntime runtime)
    {
        if (WidgetId == 0 || runtime == null) return;
        UiSlotTable.BindClick(WidgetId, () => { if (Interactable) BeginCapture(); });
    }

    protected override void OnDispose()
    {
        try
        {
            Keybinds.RebindFinished -= _finished;
            Keybinds.RebindRefused -= _finished;
        }
        catch { /* */ }
        if (_state.Capturing)
        {
            try { _state.CancelCapture(); }
            catch { /* */ }
        }

        _changed.Clear();
        Refused = null;
        base.OnDispose();
    }

    private void OnRebindFinished(RebindResult result)
    {
        KeyChord before = _state.Chord;
        _state.NoteFinished(result);
        WriteWidget();
        if (result.Outcome == RebindOutcome.Bound && before != _state.Chord)
            Raise();
        if (result.Outcome == RebindOutcome.Refused && string.Equals(result.Id, _state.BindId, StringComparison.Ordinal))
        {
            var refused = Refused;
            if (refused != null)
            {
                try { refused(result.Reason ?? ""); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: keybind picker refusal handler threw: " + e.Message); }
            }
        }
    }

    /// <summary>
    /// KEYS2: raised with the reason when a chord for this bind is refused (reserved by Windows or Steam, a
    /// Windows-key shortcut, used by something else). The mod page's Keys row shows it. Internal: the public
    /// <see cref="IUiKeyField"/> contract is unchanged.
    /// </summary>
    internal event Action<string>? Refused;

    private void WriteWidget()
    {
        if (Runtime is not { Alive: true } r) return;
        r.StringValue = _state.DisplayText;
        r.BoolValue = _state.Capturing;
    }

    private void Raise() => _changed.Raise(_state.Chord);
}
