using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>
/// A one-of choice with every option on screen at once: a radio list, one option per line with a
/// radio circle in front of it (filled for the chosen option), the chosen line bright and the others
/// dimmed, each with an optional one-line explanation under it (RADIO1, decided 2026-10-06; it
/// replaced the segmented picker). A click on a line chooses it: one write, never a toggle
/// (<see cref="OnChanged"/> fires once per change). Create with <see cref="IUiPanel.Radio"/>.
/// </summary>
public interface IUiRadio : IUiHandle
{
    /// <summary>The chosen value (one of <see cref="Values"/>, or whatever was set).</summary>
    string Value { get; set; }

    /// <summary>The stable values, in display order.</summary>
    IReadOnlyList<string> Values { get; }

    /// <summary>What each value draws as, index-aligned with <see cref="Values"/>.</summary>
    IReadOnlyList<string> Labels { get; }

    /// <summary>Called with the new value after each pick. Not called for a programmatic <see cref="Value"/> set.</summary>
    IDisposable OnChanged(Action<string> handler);
}

/// <summary>
/// A set of choices, each switched on or off by a click; drawn as a vertical list with a check
/// marker in front of each option. Every click is a write. Create with
/// <see cref="IUiPanel.MultiSelect"/>.
/// </summary>
public interface IUiMultiSelect : IUiHandle
{
    /// <summary>The selected values, in <see cref="Values"/> order. Setting it does not raise <see cref="OnChanged"/>.</summary>
    IReadOnlyList<string> Selected { get; set; }

    /// <summary>The stable values, in display order.</summary>
    IReadOnlyList<string> Values { get; }

    /// <summary>What each value draws as, index-aligned with <see cref="Values"/>.</summary>
    IReadOnlyList<string> Labels { get; }

    /// <summary>True when <paramref name="value"/> is selected.</summary>
    bool IsSelected(string value);

    /// <summary>Called with the whole new selection after each click.</summary>
    IDisposable OnChanged(Action<IReadOnlyList<string>> handler);
}

/// <summary>
/// A read-only line whose value is read from a function: the caption on the left, the value on the
/// right, re-read every <see cref="RefreshSeconds"/> while the line is visible. A function that
/// throws is logged once and the last value stays; nothing is thrown into the frame loop. Create with
/// <see cref="IUiPanel.Info"/>. Disposing it (or its panel) stops the refresh.
/// </summary>
public interface IUiInfo : IUiLabel
{
    /// <summary>The value last read.</summary>
    string Value { get; }

    /// <summary>Seconds between reads; 0 reads once.</summary>
    double RefreshSeconds { get; }

    /// <summary>Read the value now.</summary>
    void Refresh();
}

/// <summary>How an options control draws: every option in one row, or one option per line.</summary>
internal enum UiOptionsLayout
{
    /// <summary>One row, the options side by side, the chosen one highlighted.</summary>
    Segmented = 0,

    /// <summary>One option per line with a marker in front of it.</summary>
    List = 1,
}

/// <summary>
/// SETTINGS1 stage 2: the drawing rules of the options controls, game-free so the thresholds and
/// captions have tests. Markers are ASCII so every game font draws them.
/// </summary>
internal static class UiOptionsRules
{
    /// <summary>RADIO1: the radio circle in front of a radio list line: filled for the chosen option.</summary>
    internal const string RadioMarkOn = "\u25CF";

    /// <inheritdoc cref="RadioMarkOn"/>
    internal const string RadioMarkOff = "\u25CB";

    /// <summary>The radio circle for a line.</summary>
    internal static string RadioMark(bool selected) => selected ? RadioMarkOn : RadioMarkOff;

    /// <summary>
    /// RADIOCLICK2: what the runtime draws for <paramref name="mark"/>, best first, when the font (and its fallbacks)
    /// may not have the circle: the circle, then a bullet / small ring, then ASCII. The readback keeps the circle.
    /// Any other text is its own only candidate.
    /// </summary>
    internal static IReadOnlyList<string> GlyphCandidates(string? mark) => mark switch
    {
        RadioMarkOn => new[] { RadioMarkOn, "•", "*" },
        RadioMarkOff => new[] { RadioMarkOff, "◦", "o" },
        // A list bullet (the Mods tab's landing points): U+2022 is in WGL4; the middle dot is Latin-1.
        "\u2022" => new[] { "\u2022", "\u00B7", "*" },
        _ => new[] { mark ?? "" },
    };

    /// <summary>
    /// RADIO1: a radio list line's pitch on the settings grid: the label alone, or the label and its one-line
    /// explanation. RADIOCLICK1: built from the cell-row radio list's metrics (<see cref="UiRadioList"/>) so both hosts
    /// space a list the same: the label line, the option gap, and for a described line the description gap and one
    /// explanation line.
    /// </summary>
    internal const float RadioLineHeight = UiRadioList.LineHeight + RadioPlainGap;

    /// <inheritdoc cref="RadioLineHeight"/>
    internal const float RadioLineWithDescriptionHeight =
        UiRadioList.LineHeight + UiRadioList.DescriptionGap + UiRadioList.AboutLineHeight + UiRadioList.OptionGap;

    /// <summary>RADIOCLICK1: the gap between two lines of a list without explanations (the old 30 px pitch).</summary>
    internal const float RadioPlainGap = 8f;

    /// <summary>True when any option carries an explanation (every line then has room for one).</summary>
    internal static bool AnyDescription(IReadOnlyList<UiChoiceOption>? options)
    {
        if (options == null) return false;
        for (int i = 0; i < options.Count; i++) if (options[i].Description.Length > 0) return true;
        return false;
    }

    /// <summary>
    /// RADIODESC1 (live 2026-10-06, Server tab: "The first join, when the text changes, and the first jo…"): how many
    /// lines a settings-grid radio option's explanation is given, from its text height as TMP measures it at the line's
    /// width: at least one, at most <see cref="UiRadioList.MaxAboutLines"/> (the cell-row list's limit). An unreadable
    /// measurement is one line.
    /// </summary>
    internal static int RadioDescriptionLines(float measuredHeight)
    {
        if (float.IsNaN(measuredHeight) || float.IsInfinity(measuredHeight) || measuredHeight <= 0.5f) return 1;
        int lines = (int)Math.Ceiling((measuredHeight - 1f) / UiRadioList.AboutLineHeight);
        return Math.Max(1, Math.Min(UiRadioList.MaxAboutLines, lines));
    }

    /// <summary>
    /// RADIODESC1: one settings-grid radio line's pitch in a described list: the label line, then (with an explanation)
    /// the description gap and its <paramref name="descriptionLines"/> lines, then the option gap. One explanation line
    /// is <see cref="RadioLineWithDescriptionHeight"/>.
    /// </summary>
    internal static float RadioLinePitch(bool described, int descriptionLines) =>
        UiRadioList.LineHeight + UiRadioList.OptionGap
        + (described ? UiRadioList.DescriptionGap + Math.Max(1, Math.Min(UiRadioList.MaxAboutLines, descriptionLines)) * UiRadioList.AboutLineHeight : 0f);

    /// <summary>RADIODESC1: where each line starts (from the container's top, after a 2 px pad) for these pitches.</summary>
    internal static float[] RadioLineTops(IReadOnlyList<float> pitches)
    {
        var tops = new float[pitches?.Count ?? 0];
        float y = RadioListPad;
        for (int i = 0; i < tops.Length; i++)
        {
            tops[i] = y;
            y += pitches![i];
        }

        return tops;
    }

    /// <summary>RADIODESC1: the list's height for these pitches (the pads above and below included).</summary>
    internal static float RadioListHeight(IReadOnlyList<float> pitches)
    {
        float h = 2f * RadioListPad;
        if (pitches != null) foreach (float p in pitches) h += p;
        return h;
    }

    /// <summary>RADIODESC1: the pad above the first and below the last line (the old <c>n × line + 4</c>).</summary>
    internal const float RadioListPad = 2f;

    /// <summary>The height of one line of an options list.</summary>
    internal static float LineHeight(IReadOnlyList<UiChoiceOption>? options, bool multi) =>
        !multi && AnyDescription(options) ? RadioLineWithDescriptionHeight : RadioLineHeight;

    /// <summary>Most options a segmented picker draws; more is a list (or a dropdown).</summary>
    internal const int SegmentedMaxOptions = 4;

    /// <summary>Longest option label, in characters, a segmented picker draws; longer is a list (or a dropdown).</summary>
    internal const int SegmentedMaxLabel = 14;

    internal const string RadioOn = "(o) ";
    internal const string RadioOff = "( ) ";
    internal const string CheckOn = "[x] ";
    internal const string CheckOff = "[ ] ";

    /// <summary>Part key of option <paramref name="index"/> (<see cref="IRuntimePartsWidget.Part"/>).</summary>
    internal static string PartKey(int index) => "option-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>True when two to four options, each short, fit one segmented row.</summary>
    internal static bool FitsSegmented(IReadOnlyList<string>? labels)
    {
        if (labels == null || labels.Count < 2 || labels.Count > SegmentedMaxOptions) return false;
        for (int i = 0; i < labels.Count; i++)
            if ((labels[i] ?? "").Length > SegmentedMaxLabel) return false;
        return true;
    }

    /// <summary>
    /// RADIO1 (decided 2026-10-06, "these controls are not good for what they do aka this or that"): every
    /// pick-one is a radio list, one option per line; the segmented picker is no longer chosen. A multi-select is
    /// a list too (check boxes).
    /// </summary>
    internal static UiOptionsLayout LayoutFor(IReadOnlyList<UiChoiceOption> options, bool multi) => UiOptionsLayout.List;

    /// <summary>
    /// The caption one option draws: its label alone. A segmented option's fill carries the state;
    /// a list option has a drawn check box in front of the label (live 2026-09-29 the text markers
    /// read as <c>[x] Combat</c> / <c>[] Travel</c>). The markers are kept for text readers.
    /// </summary>
    internal static string Caption(string label, bool selected, bool multi, UiOptionsLayout layout) => label ?? "";

    /// <summary>The text form of an option's state, for a reader with no pixels (<c>[x]</c>, <c>( )</c>, …).</summary>
    internal static string Marker(bool selected, bool multi) =>
        multi ? (selected ? CheckOn : CheckOff) : (selected ? RadioOn : RadioOff);
}

/// <summary>SETTINGS1 stage 2: the state of a radio group or a multi-select, game-free.</summary>
internal sealed class UiOptionsState
{
    private readonly List<UiChoiceOption> _options;
    private readonly bool[] _selected;
    private string _value = "";

    internal UiOptionsState(IReadOnlyList<UiChoiceOption>? options, IEnumerable<string>? selected, bool multi)
    {
        _options = new List<UiChoiceOption>(options ?? Array.Empty<UiChoiceOption>());
        _selected = new bool[_options.Count];
        Multi = multi;
        if (multi) SetSelected(selected);
        else
        {
            string? first = null;
            if (selected != null)
                foreach (var v in selected) { first = v; break; }
            SetValue(first ?? "");
        }
    }

    internal bool Multi { get; }

    internal IReadOnlyList<UiChoiceOption> Options => _options;

    internal int IndexOf(string? value)
    {
        for (int i = 0; i < _options.Count; i++)
            if (string.Equals(_options[i].Value, value ?? "", StringComparison.Ordinal)) return i;
        return -1;
    }

    internal bool IsSelected(int index) => index >= 0 && index < _selected.Length && _selected[index];

    /// <summary>Radio: the chosen value, kept verbatim even when it is not in the list.</summary>
    internal string Value => _value;

    internal bool ValueInList => IndexOf(_value) >= 0;

    /// <summary>The selected values in option order.</summary>
    internal IReadOnlyList<string> Selected
    {
        get
        {
            var list = new List<string>();
            for (int i = 0; i < _options.Count; i++) if (_selected[i]) list.Add(_options[i].Value);
            return list;
        }
    }

    internal void SetValue(string? value)
    {
        _value = value ?? "";
        int index = IndexOf(_value);
        for (int i = 0; i < _selected.Length; i++) _selected[i] = i == index;
    }

    internal void SetSelected(IEnumerable<string>? values)
    {
        for (int i = 0; i < _selected.Length; i++) _selected[i] = false;
        if (values == null) return;
        foreach (var v in values)
        {
            int index = IndexOf((v ?? "").Trim());
            if (index >= 0) _selected[index] = true;
        }
    }

    /// <summary>
    /// A click on option <paramref name="index"/>: a radio chooses it (no change when it already
    /// was), a multi-select flips it. True when the state changed.
    /// </summary>
    internal bool Pick(int index)
    {
        if (index < 0 || index >= _options.Count) return false;
        if (Multi)
        {
            _selected[index] = !_selected[index];
            return true;
        }

        if (_selected[index] && string.Equals(_value, _options[index].Value, StringComparison.Ordinal)) return false;
        SetValue(_options[index].Value);
        return true;
    }

    /// <summary>What the control reads as: the chosen label, or the selected labels joined; <c>none</c> for an empty set.</summary>
    internal string DisplayText
    {
        get
        {
            if (!Multi)
            {
                int index = IndexOf(_value);
                return index >= 0 ? _options[index].Label : (_value.Length == 0 ? "" : _value + " (not in list)");
            }

            var labels = new List<string>();
            for (int i = 0; i < _options.Count; i++) if (_selected[i]) labels.Add(_options[i].Label);
            return labels.Count == 0 ? "none" : string.Join(", ", labels);
        }
    }

    internal bool[] SelectionFlags()
    {
        var copy = new bool[_selected.Length];
        Array.Copy(_selected, copy, copy.Length);
        return copy;
    }
}

/// <summary>SETTINGS1 stage 2: the handle behind <see cref="IUiRadio"/> and <see cref="IUiMultiSelect"/>.</summary>
internal sealed class UiOptionsHandle : UiHandle, IUiRadio, IUiMultiSelect, IUiOptionHover
{
    private readonly UiOptionsState _state;
    private readonly UiOptionHoverState _hover = new();
    private readonly IUiRuntime? _uiRuntime;
    private readonly UiChangeHandlers<string> _changedValue = new();
    private readonly UiChangeHandlers<IReadOnlyList<string>> _changedSet = new();
    private readonly UiPartSlots _parts = new();

    internal UiOptionsHandle(
        string pluginId, string rootName, object? owner, IRuntimeWidget? runtime, bool enabled, ushort id,
        IReadOnlyList<UiChoiceOption> options, IEnumerable<string>? selected, bool multi, UiOptionsLayout layout,
        IUiRuntime? uiRuntime)
        : base(pluginId, rootName, owner, runtime, enabled)
    {
        WidgetId = id;
        _uiRuntime = uiRuntime;
        _state = new UiOptionsState(options, selected, multi);
        Layout = layout;
        WriteWidget();
    }

    internal bool Multi => _state.Multi;

    internal UiOptionsLayout Layout { get; }

    public IReadOnlyList<UiChoiceOption> Options => _state.Options;

    /// <summary>Slot ids the option parts hold. Tests.</summary>
    internal int PartSlotCount => _parts.Count;

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
        get => _state.Multi ? string.Join(", ", _state.Selected) : _state.Value;
        set
        {
            if (_state.Multi) _state.SetSelected(SplitList(value));
            else _state.SetValue(value);
            WriteWidget();
        }
    }

    public IReadOnlyList<string> Selected
    {
        get => _state.Multi ? _state.Selected : (_state.ValueInList ? new[] { _state.Value } : Array.Empty<string>());
        set
        {
            if (_state.Multi) _state.SetSelected(value);
            else
            {
                string first = "";
                if (value != null && value.Count > 0) first = value[0];
                _state.SetValue(first);
            }

            WriteWidget();
        }
    }

    /// <summary>What the control reads as (the readback's <c>value</c>).</summary>
    internal string DisplayText => _state.DisplayText;

    public bool IsSelected(string value) => _state.IsSelected(_state.IndexOf(value));

    /// <summary>A click on option <paramref name="index"/> (its part slot). Ignored while not interactable.</summary>
    internal bool Click(int index)
    {
        if (IsDisposed || !Interactable) return false;
        bool changed = _state.Pick(index);
        WriteWidget();
        if (changed) Raise();
        return changed;
    }

    // ---- OPTIONHOVER1: the line under the pointer feeds the details pane ------------------------------------------

    public int HoveredOption => _hover.Index;

    /// <summary>The pointer is over option <paramref name="index"/> (-1: over none). True when the hovered option changed.</summary>
    public bool HoverOption(int index)
    {
        if (index >= _state.Options.Count) return false;
        if (index >= 0 && IsDisposed) return false;
        return _hover.Set(index);
    }

    public IDisposable OnOptionHover(Action<int> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        return _hover.Add(handler);
    }

    /// <summary>The bridge's click with a value: pick that option.</summary>
    internal bool Pick(string? value) => Click(_state.IndexOf(value));

    /// <summary>The index of an option by its value, else by its label (case-insensitive); -1 when none.</summary>
    internal int Find(string? valueOrLabel)
    {
        int index = _state.IndexOf(valueOrLabel);
        if (index >= 0) return index;
        string want = (valueOrLabel ?? "").Trim();
        for (int i = 0; i < _state.Options.Count; i++)
            if (string.Equals(_state.Options[i].Value, want, StringComparison.OrdinalIgnoreCase)
                || string.Equals(_state.Options[i].Label, want, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>
    /// RADIO1: the readback (<c>mods_ui widget_read</c>): <c>radio {current, currentLabel, options [{value, label,
    /// description, selected, part}], hovered}</c> (a multi-select reports <c>selected</c> as a list; <c>hovered</c> is the
    /// index under the pointer, or -1).
    /// </summary>
    internal Dictionary<string, object?> Describe()
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
                ["selected"] = _state.IsSelected(i),
                ["mark"] = _state.Multi ? UiOptionsRules.Marker(_state.IsSelected(i), true).Trim() : UiOptionsRules.RadioMark(_state.IsSelected(i)),
                ["part"] = UiOptionsRules.PartKey(i),
            });
        }

        int current = _state.IndexOf(_state.Value);
        var d = new Dictionary<string, object?>
        {
            ["layout"] = Layout == UiOptionsLayout.List ? "list" : "segmented",
            ["options"] = options,
            ["hovered"] = _hover.Index,
        };
        if (_state.Multi) d["selected"] = _state.Selected;
        else
        {
            d["current"] = _state.Value;
            d["currentLabel"] = current >= 0 ? _state.Options[current].Label : "";
        }

        return d;
    }

    public IDisposable OnChanged(Action<string> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (WidgetId == 0) return Noop.Instance;
        return _changedValue.Add(handler);
    }

    public IDisposable OnChanged(Action<IReadOnlyList<string>> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (WidgetId == 0) return Noop.Instance;
        return _changedSet.Add(handler);
    }

    /// <summary>
    /// Wire the raw widget events: each option is a part with its own slot, and a click on a line chooses that
    /// option. RADIO1: a click on the row outside the lines does nothing (it used to move a radio to its next
    /// option, a toggle the user did not want).
    /// </summary>
    internal void Attach(IUiRuntime runtime)
    {
        if (WidgetId == 0 || runtime == null) return;
        for (int i = 0; i < _state.Options.Count; i++)
        {
            int index = i;
            _parts.Wire(Owner, runtime, Runtime, UiOptionsRules.PartKey(i), () => Click(index));
            // OPTIONHOVER1: the pointer over a line (or a segment, or a check line).
            _parts.WireHover(Owner, runtime, Runtime, UiOptionsRules.PartKey(i), entered =>
            {
                if (entered) HoverOption(index);
                else _hover.Leave(index);
            });
        }
    }

    protected override void OnDispose()
    {
        _hover.Clear();
        _changedValue.Clear();
        _changedSet.Clear();
        _parts.Release();
        base.OnDispose();
    }

    private void WriteWidget()
    {
        if (Runtime is not { Alive: true } r) return;
        r.StringValue = _state.DisplayText;
        try { _uiRuntime?.SetOptionStates(r, _state.SelectionFlags()); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: option states threw: " + e.Message); }
    }

    private void Raise()
    {
        if (_state.Multi) _changedSet.Raise(_state.Selected);
        else _changedValue.Raise(_state.Value);
    }

    internal static List<string> SplitList(string? text)
    {
        var list = new List<string>();
        foreach (var part in (text ?? "").Split(','))
        {
            string t = part.Trim();
            if (t.Length > 0) list.Add(t);
        }

        return list;
    }
}

/// <summary>SETTINGS1 stage 2: the handle behind <see cref="IUiInfo"/>.</summary>
internal sealed class UiInfoHandle : UiHandle, IUiInfo
{
    private Func<string>? _source;
    private string _value = "";
    private bool _faulted;
    private int _generation;
    private readonly string _caption;

    /// <summary>Shortest refresh interval; a smaller positive value is raised to this.</summary>
    internal const double MinRefreshSeconds = 0.25;

    /// <summary>Longest refresh interval; a larger value (or infinity) is lowered to this.</summary>
    internal const double MaxRefreshSeconds = 60;

    /// <summary>What an info line shows before its first successful read.</summary>
    internal const string Unavailable = "(unavailable)";

    /// <summary>
    /// Schedules the next read (seconds, action, owner). Null in the game-free build, where nothing
    /// repeats unless a test installs one; the client posts through <c>MainThread.PostDelayed</c>
    /// with the handle's owner, so a plugin's unload drops the pending read.
    /// </summary>
    internal static Action<double, Action, object?>? Scheduler { get; set; }
#if !PROGENITOR_TESTS
        = static (seconds, action, owner) => Progenitor.Dev.MainThread.PostDelayed(seconds, action, owner);
#endif

    internal UiInfoHandle(
        string pluginId, string rootName, object? owner, IRuntimeWidget? runtime, bool enabled, ushort id,
        string caption, Func<string>? source, double refreshSeconds)
        : base(pluginId, rootName, owner, runtime, enabled)
    {
        WidgetId = id;
        _caption = caption ?? "";
        _source = source;
        RefreshSeconds = double.IsNaN(refreshSeconds) || refreshSeconds <= 0
            ? 0
            : Math.Min(MaxRefreshSeconds, Math.Max(MinRefreshSeconds, refreshSeconds));
    }

    public string Value => _value;

    public double RefreshSeconds { get; }

    /// <summary>How many reads ran. Tests.</summary>
    internal int ReadCount { get; private set; }

    /// <summary>True while a scheduled read is owed.</summary>
    internal bool Scheduled { get; private set; }

    /// <summary>First read and, when the line refreshes, the first scheduled one.</summary>
    internal void Start()
    {
        Refresh();
        ScheduleNext();
    }

    public void Refresh()
    {
        if (IsDisposed) return;
        var source = _source;
        if (source == null) return;
        string next;
        ReadCount++;
        try
        {
            next = source() ?? "";
            _faulted = false;
        }
        catch (Exception e)
        {
            if (!_faulted)
                Plugin.Logger?.LogWarning("Ui: info line '" + _caption + "' threw: " + e.Message + " (logged once; the last value stays).");
            _faulted = true;
            next = _value.Length == 0 ? Unavailable : _value;
        }

        if (string.Equals(next, _value, StringComparison.Ordinal) && ReadCount > 1) return;
        _value = next;
        try { if (Runtime is { Alive: true } r) r.StringValue = _value; }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: info line write threw: " + e.Message); }
    }

    private void ScheduleNext()
    {
        if (IsDisposed || !Enabled || RefreshSeconds <= 0 || _source == null) return;
        var scheduler = Scheduler;
        if (scheduler == null) return;
        int generation = _generation;
        Scheduled = true;
        try
        {
            scheduler(RefreshSeconds, () =>
            {
                // Never throws into the frame loop: a closed page stops here, a hidden line keeps
                // its clock but skips the read, and a read that throws was already caught.
                try
                {
                    Scheduled = false;
                    if (IsDisposed || generation != _generation) return;
                    if (Visible) Refresh();
                    ScheduleNext();
                }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: info line tick threw: " + e.Message); }
            }, Owner);
        }
        catch (Exception e)
        {
            Scheduled = false;
            Plugin.Logger?.LogWarning("Ui: info line schedule threw: " + e.Message);
        }
    }

    protected override void OnDispose()
    {
        _generation++;
        _source = null;
        Scheduled = false;
        base.OnDispose();
    }
}

/// <summary>SETTINGS1 stage 2: the published <see cref="IUiPanel"/> factories for the stage 2 controls.</summary>
internal static class UiSettingsControls
{
    /// <summary>The kit dialog tag a confirm button's question opens under.</summary>
    internal const string ConfirmDialogTag = "uiConfirm";

    private static IUiControlHost? Host(IUiPanel panel) => panel as IUiControlHost;

    internal static IUiInput TextArea(IUiPanel panel, string text, string initial, int lines)
    {
        int n = lines < 2 ? 2 : lines > 12 ? 12 : lines;
        if (Host(panel) is { } host) return host.TextArea(text ?? "", initial ?? "", n);
        string plugin = panel?.PluginId ?? UiOwnership.UnownedId;
        return new UiInputHandle(plugin, panel?.RootName ?? UiOwnership.RootName(plugin), null, null, false, 0, multiline: true, lines: n);
    }

    internal static List<UiChoiceOption> Options(
        IReadOnlyList<string>? values, IReadOnlyList<string>? labels, IReadOnlyList<string>? descriptions = null)
    {
        var options = new List<UiChoiceOption>(values?.Count ?? 0);
        if (values == null) return options;
        for (int i = 0; i < values.Count; i++)
        {
            string? label = labels != null && i < labels.Count ? labels[i] : null;
            string? description = descriptions != null && i < descriptions.Count ? descriptions[i] : null;
            options.Add(new UiChoiceOption(values[i] ?? "", label, description));
        }

        return options;
    }

    internal static IUiRadio Radio(
        IUiPanel panel, string text, IReadOnlyList<string>? values, string value, IReadOnlyList<string>? labels,
        IReadOnlyList<string>? descriptions = null)
    {
        var options = Options(values, labels, descriptions);
        var layout = UiOptionsRules.LayoutFor(options, multi: false);
        return OptionsControl(panel, text, options, new[] { value ?? "" }, multi: false, layout);
    }

    internal static IUiMultiSelect MultiSelect(
        IUiPanel panel, string text, IReadOnlyList<string>? values, IEnumerable<string>? selected, IReadOnlyList<string>? labels)
    {
        var options = Options(values, labels);
        return OptionsControl(panel, text, options, selected, multi: true, UiOptionsLayout.List);
    }

    internal static UiOptionsHandle OptionsControl(
        IUiPanel panel, string text, IReadOnlyList<UiChoiceOption> options, IEnumerable<string>? selected, bool multi,
        UiOptionsLayout layout)
    {
        if (Host(panel) is { } host) return host.Options(text ?? "", options, selected, multi, layout);
        string plugin = panel?.PluginId ?? UiOwnership.UnownedId;
        return new UiOptionsHandle(
            plugin, panel?.RootName ?? UiOwnership.RootName(plugin), null, null, false, 0, options, selected, multi, layout, null);
    }

    internal static IUiInfo Info(IUiPanel panel, string text, Func<string> value, double refreshSeconds)
    {
        if (value == null) throw new ArgumentNullException(nameof(value));
        if (Host(panel) is { } host) return host.Info(text ?? "", value, refreshSeconds);
        string plugin = panel?.PluginId ?? UiOwnership.UnownedId;
        var handle = new UiInfoHandle(
            plugin, panel?.RootName ?? UiOwnership.RootName(plugin), null, null, false, 0, text ?? "", value, refreshSeconds);
        handle.Refresh();
        return handle;
    }

    internal static IUiLabel Divider(IUiPanel panel)
    {
        if (Host(panel) is { } host) return host.Divider();
        return panel.Label("");
    }

    /// <summary>
    /// A button whose click first asks <paramref name="question"/> in the kit dialog; only
    /// <b>Yes</b> runs the button's handler. The dialog belongs to the button: it closes when the
    /// button (or its panel) is disposed, and a Yes that arrives after that runs nothing.
    /// </summary>
    internal static IUiButton ConfirmButton(
        IUiPanel panel, string text, string question, Func<string, IUiPanel>? factory = null, string? body = null)
    {
        if (panel == null) throw new ArgumentNullException(nameof(panel));
        var button = panel.Button(text ?? "");
        return Confirm(panel, button, text ?? "", question, factory, body);
    }

    /// <summary>Make <paramref name="button"/> ask <paramref name="question"/> before its handler runs (<see cref="ConfirmButton"/>).</summary>
    internal static IUiButton Confirm(
        IUiPanel panel, IUiButton button, string caption, string question, Func<string, IUiPanel>? factory = null,
        string? body = null)
    {
        if (button is not UiButtonHandle handle)
        {
            button?.Dispose();
            throw new NotSupportedException("ConfirmButton needs a Progenitor panel (Ui.Panel); this panel cannot open the confirmation dialog.");
        }

        var host = panel as UiPanelHandle ?? (panel as ModHeaderPanel)?.Inner as UiPanelHandle;
        factory ??= host == null ? null : title => Ui.Panel(title, host.Owner, host.PluginId);
        handle.ConfigureConfirm(string.IsNullOrWhiteSpace(question) ? (caption ?? "") + "?" : question, caption ?? "", factory, body);
        return handle;
    }
}

/// <summary>
/// SETTINGS1 stage 2: the mod page's settings grid as kit seams. Internal: the grid is the mod page's
/// layout (<see cref="ModSettingsGrid"/>), not yet a published panel mode.
/// </summary>
internal static class UiSettingsGrid
{
    /// <summary>Rows added while on use the settings grid's columns (label, fixed control column, reset gutter).</summary>
    internal static void SettingsGrid(this IUiPanel panel, bool on)
    {
        if (panel is not UiPanelHandle host || host.RuntimePanel is not { } runtimePanel) return;
        try { Ui.CurrentRuntime().SetSettingsGrid(runtimePanel, on); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings grid threw: " + e.Message); }
    }

    /// <summary>
    /// Draw <paramref name="help"/> under <paramref name="row"/>'s caption, inside its label column,
    /// muted and smaller, wrapping there. The row grows to hold it; nothing is drawn over the
    /// control column or the gutter. Empty removes it.
    /// </summary>
    internal static void SetHelp(this IUiHandle row, string help)
    {
        if (row is not UiHandle handle) return;
        handle.Help = help ?? "";
        if (handle.IsDisposed || handle.Runtime is not { Alive: true } widget) return;
        try { Ui.CurrentRuntime().SetRowHelp(widget, handle.Help); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: row help threw: " + e.Message); }
    }

    /// <summary>
    /// TEXTAREA1: cap a text field or text area at <paramref name="characters"/> (0 = no limit).
    /// Typing and paste stop at the cap; a runtime without fields ignores it.
    /// </summary>
    internal static void SetMaxLength(this IUiHandle input, int characters)
    {
        if (input is not UiHandle handle) return;
        if (handle.IsDisposed || handle.Runtime is not { Alive: true } widget) return;
        try { Ui.CurrentRuntime().SetInputLimit(widget, characters < 0 ? 0 : characters); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: input limit threw: " + e.Message); }
    }

    /// <summary>
    /// TOKENINSERT1: insert <paramref name="token"/> at <paramref name="input"/>'s caret (the end when it never had focus),
    /// whole or not at all, and keep the keyboard in the field (<see cref="IUiRuntime.InsertText"/>). A runtime that cannot
    /// (a test fake) gets the same rule applied to the value with the caret at the end. Returns the field's value after.
    /// </summary>
    internal static string InsertAtCaret(this IUiInput input, string token, int maxLength = int.MaxValue)
    {
        if (input is not UiHandle handle || handle.IsDisposed) return "";
        bool done = false;
        if (handle.Runtime is { Alive: true } widget)
        {
            try { done = Ui.CurrentRuntime().InsertText(widget, token); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: insert threw: " + e.Message); }
        }

        if (!done)
        {
            var next = UiFieldInsert.At(input.Value, null, token, maxLength);
            if (!string.Equals(next.Text, input.Value, StringComparison.Ordinal)) input.Value = next.Text;
        }

        return input.Value;
    }

    /// <summary>Settings organization live 2026-10-06: the caption the runtime draws for this row, or null when unknown.</summary>
    internal static string? RowCaption(this IUiHandle row)
    {
        if (row is not UiHandle handle) return null;
        if (handle.IsDisposed || handle.Runtime is not { Alive: true } widget) return null;
        try { return Ui.CurrentRuntime().RowCaption(widget); }
        catch { return null; }
    }

    /// <summary>Settings organization review: mask a text field's text (the server password).</summary>
    internal static void SetMasked(this IUiHandle input, bool masked)
    {
        if (input is not UiHandle handle) return;
        if (handle.IsDisposed || handle.Runtime is not { Alive: true } widget) return;
        try { Ui.CurrentRuntime().SetInputMasked(widget, masked); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: input mask threw: " + e.Message); }
    }

    /// <summary>TEXTFILTER1: which characters a text field accepts while typing; a runtime without fields ignores it.</summary>
    internal static void SetCharFilter(this IUiHandle input, UiCharFilter filter)
    {
        if (input is not UiHandle handle) return;
        if (handle.IsDisposed || handle.Runtime is not { Alive: true } widget) return;
        try { Ui.CurrentRuntime().SetInputFilter(widget, filter); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: input filter threw: " + e.Message); }
    }

    /// <summary>
    /// A settings-grid button row: <paramref name="name"/> in the caption column and a normal-size
    /// button captioned <paramref name="caption"/> in the control column. Off the grid (or on a runtime
    /// that does not draw one) it is an ordinary button captioned <paramref name="caption"/>.
    /// </summary>
    internal static IUiButton NamedButton(this IUiPanel panel, string name, string caption)
    {
        if (panel is UiPanelHandle host) return host.AddNamedButton(name ?? "", caption ?? "");
        return panel.Button(caption ?? "");
    }

    /// <summary>
    /// A small square link in <paramref name="row"/>'s reset gutter (right of the control column).
    /// A runtime without a gutter puts it on the next row's right edge, as the stage 1 link was.
    /// </summary>
    internal static IUiIconLink GutterLink(this IUiPanel panel, IUiHandle row, string glyph, string tooltip)
    {
        if (panel is UiPanelHandle host && row is UiHandle target && target.Runtime is { } widget)
            return host.AddGutterLink(widget, glyph ?? "", tooltip ?? "");
        return panel.IconLink(glyph, tooltip);
    }

    /// <summary>
    /// TEXTEXPAND1: a small square button in the bottom-right corner of <paramref name="row"/>'s text
    /// field (the text keeps clear of it). A row without a field, or a runtime that draws none, gets
    /// a gutter link instead (<see cref="GutterLink"/>).
    /// </summary>
    internal static IUiIconLink CornerLink(this IUiPanel panel, IUiHandle row, string glyph, string tooltip)
    {
        if (panel is UiPanelHandle host && row is UiHandle target && target.Runtime is { } widget)
            return host.AddCornerLink(widget, glyph ?? "", tooltip ?? "");
        return panel.IconLink(glyph, tooltip);
    }

    // ---- MENUKIT2: the game-look settings page -----------------------------------------------------

    /// <summary>The description column on (the settings grid's rows then draw no help) or off.</summary>
    internal static void SettingsDescriptionMode(this IUiPanel panel, bool on)
    {
        if (panel is not UiPanelHandle host || host.RuntimePanel is not { } runtimePanel) return;
        try { Ui.CurrentRuntime().SetSettingsDescriptionMode(runtimePanel, on); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings description mode threw: " + e.Message); }
    }

    /// <summary>Write <paramref name="description"/> into the panel's description column.</summary>
    internal static void ShowDescription(this IUiPanel panel, ModSettingsDescription description)
    {
        if (description == null || panel is not UiPanelHandle host || host.RuntimePanel is not { } runtimePanel) return;
        try
        {
            Ui.CurrentRuntime().SetSettingsDescription(
                runtimePanel, description.Heading, description.Body, description.NotesText, description.Tone);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: settings description threw: " + e.Message); }
    }

    /// <summary>CONTROLS1 round 2: what the runtime actually drew on this panel's settings rows; null when it draws nothing.</summary>
    internal static Dictionary<string, object?>? SettingsDrawn(this IUiPanel panel)
    {
        if (panel is not UiPanelHandle host || host.RuntimePanel is not { } runtimePanel) return null;
        try { return Ui.CurrentRuntime().DescribeSettingsDrawn(runtimePanel); }
        catch (Exception e)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal) { ["error"] = e.Message };
        }
    }

    /// <summary>Light a grid row (hovered or selected) or put it back at rest.</summary>
    internal static void SetRowLit(this IUiHandle row, bool lit)
    {
        if (row is not UiHandle handle || handle.IsDisposed || handle.Runtime is not { Alive: true } widget) return;
        try { Ui.CurrentRuntime().SetGridRowLit(widget, lit); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: row lit threw: " + e.Message); }
    }

    /// <summary>The reset arrow's state: dim at the default, steel blue when changed.</summary>
    internal static void SetResetMark(this IUiHandle link, SettingsResetState state)
    {
        if (link is not UiHandle handle || handle.IsDisposed || handle.Runtime is not { Alive: true } widget) return;
        try { Ui.CurrentRuntime().SetResetMark(widget, state); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: reset mark threw: " + e.Message); }
    }

    /// <summary>A grid row's small chip (<see cref="ModSettingsRowMarks.ChipText"/>); empty hides it.</summary>
    internal static void SetRowChip(this IUiHandle row, string text)
    {
        if (row is not UiHandle handle || handle.IsDisposed || handle.Runtime is not { Alive: true } widget) return;
        try { Ui.CurrentRuntime().SetRowChip(widget, text ?? ""); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: row chip threw: " + e.Message); }
    }

    /// <summary>NOPROGMSG1: a text field's muted hint while it is empty (the value empty stands for); empty clears it.</summary>
    internal static void SetPlaceholder(this IUiHandle input, string? text)
    {
        if (input is not UiHandle handle || handle.IsDisposed) return;
        handle.Placeholder = text ?? "";
        if (handle.Runtime is not { Alive: true } widget) return;
        try { Ui.CurrentRuntime().SetInputPlaceholder(widget, handle.Placeholder); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: input placeholder threw: " + e.Message); }
    }

    /// <summary>INLINEAPPLY1: the readback word of a button drawn with the game's steel button art.</summary>
    internal const string GameButtonStyle = "game-button";

    /// <summary>INLINEAPPLY1: a game button whose art is not ready yet (the flat steel face; the art follows).</summary>
    internal const string GameButtonPendingStyle = "game-button-pending";

    /// <summary>INLINEAPPLY1: a button drawn in the kit's own look.</summary>
    internal const string KitButtonStyle = "kit";

    /// <summary>
    /// Round E: a plain button drawn as the game's standard button (the owned key cap), its caption centered. Through the
    /// runtime's shared game-button path (BUTTONART1: the borrowed steel art, retried until it arrives and after a Core swap).
    /// </summary>
    internal static void GameButton(this IUiHandle button)
    {
        if (button is not UiHandle handle || handle.IsDisposed) return;
        handle.ButtonStyle = GameButtonStyle;
        if (handle.Runtime is not { Alive: true } widget) return;
        try { Ui.CurrentRuntime().SetGameButton(widget); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: game button threw: " + e.Message); }
    }

    /// <summary>
    /// INLINEAPPLY1: the art <paramref name="button"/> is drawn with, for the readback: <c>game-button</c>,
    /// <c>game-button-pending</c> or <c>kit</c>. The runtime's answer when it can tell; else the style the button asked for.
    /// </summary>
    internal static string ButtonArt(this IUiHandle button)
    {
        if (button is not UiHandle handle) return KitButtonStyle;
        string requested = handle.ButtonStyle.Length > 0 ? handle.ButtonStyle : KitButtonStyle;
        if (handle.IsDisposed || handle.Runtime is not { Alive: true } widget) return requested;
        try { return Ui.CurrentRuntime().ButtonArt(widget) ?? requested; }
        catch { return requested; }
    }

    /// <summary>Round D: mark a button as destructive (Uninstall): the danger tone.</summary>
    internal static void Destructive(this IUiHandle button)
    {
        if (button is not UiHandle handle || handle.IsDisposed || handle.Runtime is not { Alive: true } widget) return;
        try { Ui.CurrentRuntime().SetDestructive(widget); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: destructive button threw: " + e.Message); }
    }

    /// <summary>The game's red section rule under a section heading row.</summary>
    internal static void SectionRule(this IUiHandle heading)
    {
        if (heading is not UiHandle handle || handle.IsDisposed || handle.Runtime is not { Alive: true } widget) return;
        try { Ui.CurrentRuntime().AddSectionRule(widget); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: section rule threw: " + e.Message); }
    }

    /// <summary>
    /// Call <paramref name="handler"/> with true when the pointer enters <paramref name="row"/> (any part
    /// of it, control included) and false when it leaves. Wired through a Bootstrap slot like every other
    /// pointer event; dispose to unwire. A runtime with no row pointer returns a no-op lease.
    /// </summary>
    internal static IDisposable OnRowHover(this IUiPanel panel, IUiHandle row, Action<bool> handler)
    {
        if (handler == null || panel is not UiPanelHandle host || row is not UiHandle target
            || target.Runtime is not { Alive: true } widget)
            return EmptyLease.Instance;
        IUiRuntime runtime;
        IRuntimeWidget? source;
        try
        {
            runtime = Ui.CurrentRuntime();
            source = runtime.GridRowPointer(widget);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: row hover threw: " + e.Message);
            return EmptyLease.Instance;
        }

        if (source == null) return EmptyLease.Instance;
        ushort id = UiSlotTable.Alloc(host.Owner);
        bool wired = false;
        try
        {
            wired = runtime.TryWire(id, UiSlotKind.PointerEnter, source);
            wired |= runtime.TryWire(id, UiSlotKind.PointerExit, source);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: row hover wire threw: " + e.Message); }
        if (!wired)
        {
            UiSlotTable.Release(id);
            return EmptyLease.Instance;
        }

        UiSlotTable.BindPointerEnter(id, () => handler(true));
        UiSlotTable.BindPointerExit(id, () => handler(false));
        return new RowHoverLease(runtime, id);
    }

    private sealed class RowHoverLease : IDisposable
    {
        private readonly IUiRuntime _runtime;
        private ushort _id;

        internal RowHoverLease(IUiRuntime runtime, ushort id)
        {
            _runtime = runtime;
            _id = id;
        }

        public void Dispose()
        {
            if (_id == 0) return;
            ushort id = _id;
            _id = 0;
            try { _runtime.Unwire(id); } catch { /* the panel's teardown unwires it too */ }
            UiSlotTable.Release(id);
        }
    }

    private sealed class EmptyLease : IDisposable
    {
        internal static readonly EmptyLease Instance = new();

        public void Dispose() { }
    }
}
