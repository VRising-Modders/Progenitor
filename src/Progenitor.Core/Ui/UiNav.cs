using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Progenitor.Ui;

/// <summary>NAV1: one navigation step, from a controller or the keyboard (docs/ui.md "Controller and keyboard navigation").</summary>
internal enum NavCommand
{
    None = 0,
    Up,
    Down,
    Left,
    Right,

    /// <summary>A on a controller, Space or Enter on the keyboard: press, flip, open, pick.</summary>
    Submit,

    /// <summary>B on a controller: close an open list, go back a pane, else the Escape path.</summary>
    Cancel,

    /// <summary>The left and right triggers: the previous or next Mods workspace tab.</summary>
    TabPrev,
    TabNext,

    /// <summary>X on a controller: reset the focused setting to its default.</summary>
    Reset,
}

/// <summary>NAV1: what the navigation inputs were this frame, read by the runtime from the devices. Game-free.</summary>
internal struct NavSample
{
    internal bool Up;
    internal bool Down;
    internal bool Left;
    internal bool Right;
    internal bool Submit;
    internal bool Cancel;
    internal bool TabPrev;
    internal bool TabNext;
    internal bool Reset;

    /// <summary>The right stick's vertical axis (-1..1, positive up): scrolls the focused control's list.</summary>
    internal float Scroll;

    /// <summary>The mouse moved or was clicked: the pointer is in use, and the focus marker steps aside.</summary>
    internal bool PointerActive;

    /// <summary>A text field has the keyboard: only Cancel navigates (the field takes its keys).</summary>
    internal bool TextFocused;

    /// <summary>A key capture is waiting for a key: only Cancel navigates.</summary>
    internal bool Capturing;
}

/// <summary>
/// NAV1: turns held navigation inputs into commands: a press is one step, and a direction held past
/// <see cref="InitialDelay"/> repeats every <see cref="Interval"/>. Buttons step once per press. Game-free.
/// </summary>
internal sealed class NavRepeater
{
    internal const double InitialDelay = 0.40;
    internal const double Interval = 0.09;

    private readonly bool[] _held = new bool[4];
    private readonly double[] _next = new double[4];
    private bool _submit;
    private bool _cancel;
    private bool _tabPrev;
    private bool _tabNext;
    private bool _reset;

    internal void Reset()
    {
        for (int i = 0; i < 4; i++)
        {
            _held[i] = false;
            _next[i] = 0;
        }

        _submit = _cancel = _tabPrev = _tabNext = _reset = false;
    }

    /// <summary>The commands this frame's sample makes at time <paramref name="now"/> (seconds), in a fixed order.</summary>
    internal void Step(in NavSample s, double now, List<NavCommand> output)
    {
        Direction(0, s.Up, now, NavCommand.Up, output);
        Direction(1, s.Down, now, NavCommand.Down, output);
        Direction(2, s.Left, now, NavCommand.Left, output);
        Direction(3, s.Right, now, NavCommand.Right, output);
        Edge(ref _submit, s.Submit, NavCommand.Submit, output);
        Edge(ref _cancel, s.Cancel, NavCommand.Cancel, output);
        Edge(ref _tabPrev, s.TabPrev, NavCommand.TabPrev, output);
        Edge(ref _tabNext, s.TabNext, NavCommand.TabNext, output);
        Edge(ref _reset, s.Reset, NavCommand.Reset, output);
    }

    private void Direction(int i, bool down, double now, NavCommand command, List<NavCommand> output)
    {
        if (!down)
        {
            _held[i] = false;
            return;
        }

        if (!_held[i])
        {
            _held[i] = true;
            _next[i] = now + InitialDelay;
            output.Add(command);
            return;
        }

        if (now < _next[i]) return;
        _next[i] = now + Interval;
        output.Add(command);
    }

    private static void Edge(ref bool was, bool down, NavCommand command, List<NavCommand> output)
    {
        if (down && !was) output.Add(command);
        was = down;
    }
}

/// <summary>NAV1: how a control takes navigation. Every concrete handle type has one (the coverage test checks).</summary>
internal enum NavRole
{
    /// <summary>Not a focus stop: a label, a readout, a container, or a control drawn by another stop.</summary>
    None = 0,
    Button,
    Toggle,
    Slider,
    SliderEntry,
    Number,
    Dropdown,

    /// <summary>A radio list or a multi-select (the instance's <c>Multi</c> decides).</summary>
    Options,

    /// <summary>A text field, a text area, a color's hex box: A gives it the keyboard.</summary>
    Text,
    Keybind,
    CellRow,
}

/// <summary>NAV1: the focused place: a handle, and for a cell row its cell, for a multi-select its option.</summary>
internal readonly struct NavTarget : IEquatable<NavTarget>
{
    private readonly string? _part;

    internal NavTarget(UiHandle handle, string part = "")
    {
        Handle = handle;
        _part = part ?? "";
    }

    internal UiHandle Handle { get; }

    /// <summary>A cell row: the cell key (empty: the whole-row click). A multi-select: the option index. Else empty.</summary>
    internal string Part => _part ?? "";

    internal bool IsEmpty => Handle == null;

    public bool Equals(NavTarget other) => ReferenceEquals(Handle, other.Handle) && string.Equals(Part, other.Part, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is NavTarget other && Equals(other);

    public override int GetHashCode() => (Handle == null ? 0 : RuntimeHelpers.GetHashCode(Handle)) ^ Part.GetHashCode();
}

/// <summary>
/// NAV1: one screen the navigation moves on: a dialog, a floating window that takes input while shown, or the Mods
/// workspace. Built fresh by the host on every step, so it never holds a handle across frames.
/// </summary>
internal sealed class NavSurface
{
    internal NavSurface(string key, IReadOnlyList<IReadOnlyList<UiHandle>> panes)
    {
        Key = key ?? "";
        Panes = panes ?? Array.Empty<IReadOnlyList<UiHandle>>();
    }

    /// <summary>Identity: a new key is a new screen, and the focus starts again.</summary>
    internal string Key { get; }

    /// <summary>The panes (the workspace: the left list, then the page), each its handles in build order.</summary>
    internal IReadOnlyList<IReadOnlyList<UiHandle>> Panes { get; }

    /// <summary>The pane the focus starts in (the workspace: the list when it has a stop).</summary>
    internal int StartPane { get; set; }

    /// <summary>Where the focus starts, when the screen has a better first stop than its first control (a dialog's answer).</summary>
    internal NavTarget? Start { get; set; }

    /// <summary>The workspace: B in a later pane goes back to the list, and A in the list goes on to the page.</summary>
    internal bool MasterDetail { get; set; }

    /// <summary>The workspace's previous (-1) or next (+1) tab, true when it moved; null when the screen has no tabs.</summary>
    internal Func<int, bool>? Tab { get; set; }
}

/// <summary>NAV1: what the model needs from the game and the menus. Production: <see cref="UiNavHost"/>; tests substitute a fake.</summary>
internal interface INavHost
{
    /// <summary>The topmost screen the navigation acts on, or null when none takes it.</summary>
    NavSurface? Active();

    /// <summary>The target's rect in screen pixels (y up), or <see cref="ScreenRect.Unknown"/>.</summary>
    ScreenRect Rect(NavTarget target);

    /// <summary>Draw or remove the focus marker on <paramref name="target"/>.</summary>
    void Mark(NavTarget target, bool on);

    /// <summary>Scroll the target's list so it is in view.</summary>
    void Reveal(NavTarget target);

    /// <summary>Give a text field (or the field inside a control) the keyboard. False when it has none.</summary>
    bool FocusText(UiHandle handle);

    /// <summary>A button's click, through the same slot a pointer click takes.</summary>
    void Click(UiHandle handle);

    /// <summary>The Escape key's path: answer a dialog, a screen's Back, else hide the shown panels.</summary>
    void Escape();

    /// <summary>Scroll the list under the focus by <paramref name="amount"/> (-1..1 per frame, positive up).</summary>
    void Scroll(NavTarget target, float amount);
}

/// <summary>
/// NAV1: what a settings view (or another owner) does for one of its controls: the focus marker is its row's own
/// selection (the game's band, the description column), and X resets it.
/// </summary>
internal sealed class NavRowHook
{
    internal NavRowHook(Action<bool> focus, Func<bool>? reset)
    {
        Focus = focus;
        Reset = reset;
    }

    internal Action<bool> Focus { get; }

    internal Func<bool>? Reset { get; }
}

/// <summary>
/// NAV1: controller and keyboard navigation for Progenitor's screens. One focus at a time, on the topmost screen
/// that takes input; directions move it by the controls' places on screen, and the focused control takes the
/// directions it uses itself (a slider, a dropdown, a toggle). Game-free: the devices are read by the runtime
/// (<see cref="Tick"/>'s sample) and everything Unity is behind <see cref="INavHost"/>. docs/ui.md
/// "Controller and keyboard navigation".
/// </summary>
internal static class UiNav
{
    /// <summary>Frames the focus waits for the page after a list row opened it (the page builds on a later frame).</summary>
    internal const int PendingPaneFrames = 30;

    /// <summary>Frames the focus waits for new page controls before the page as it is counts (the same mod's row again).</summary>
    internal const int PendingFreshFrames = 6;

    /// <summary>Right-stick dead zone.</summary>
    internal const float ScrollDeadZone = 0.25f;

    private static readonly NavRepeater Repeater = new();
    private static readonly List<NavCommand> Commands = new();
    private static readonly ConditionalWeakTable<UiHandle, NavRowHook> Hooks = new();
    private static readonly ConditionalWeakTable<UiHandle, object> Skipped = new();
    private static readonly object SkipMark = new();

    private static NavTarget _focus;
    private static string _surface = "";
    private static int _pane;
    private static bool _marked;
    private static bool _active;
    private static float _lastX = float.NaN;
    private static float _lastY = float.NaN;
    private static int _pendingPane = -1;
    private static int _pendingFrames;
    private static readonly HashSet<UiHandle> _pendingStale = new(ReferenceEqualityComparer.Instance);
    private static string _returnSurface = "";
    private static NavTarget _returnFocus;
    private static int _returnPane;
    private static float _returnX = float.NaN;
    private static float _returnY = float.NaN;

    internal static INavHost Host { get; set; } = UiNavHost.Instance;

    /// <summary>The focused place now (empty when none).</summary>
    internal static NavTarget Focus => _focus;

    /// <summary>Whether the focus marker is drawn (the last input was a navigation input, not the pointer).</summary>
    internal static bool Marked => _marked;

    /// <summary>The surface key the focus belongs to, or empty.</summary>
    internal static string Surface => _surface;

    /// <summary>The pane index of the focus on its surface.</summary>
    internal static int Pane => _pane;

    /// <summary>
    /// A settings view's row: its focus marker is <paramref name="hook"/>'s, and X resets it. Weak: the hook goes
    /// with the handle.
    /// </summary>
    internal static void Hook(IUiHandle? handle, NavRowHook hook)
    {
        if (handle is not UiHandle h || hook == null) return;
        Hooks.Remove(h);
        Hooks.Add(h, hook);
    }

    /// <summary>A control the navigation passes over (a settings row's reset arrow: X resets instead).</summary>
    internal static void Skip(IUiHandle? handle)
    {
        if (handle is not UiHandle h) return;
        Hooks.Remove(h);
        Skipped.Remove(h);
        Skipped.Add(h, SkipMark);
    }

    private static readonly ConditionalWeakTable<UiHandle, Func<NavCommand, bool>> Steers = new();
    private static readonly ConditionalWeakTable<UiHandle, Func<float, bool>> StickScrolls = new();

    /// <summary>
    /// P7b: a control that takes a direction itself before the focus moves (a virtualized list's first or last drawn row
    /// scrolls the list instead of leaving it). <paramref name="steer"/> answers true when it took the command; null
    /// removes the hook. Weak: the hook goes with the handle.
    /// </summary>
    internal static void Steer(IUiHandle? handle, Func<NavCommand, bool>? steer)
    {
        if (handle is not UiHandle h) return;
        Steers.Remove(h);
        if (steer != null) Steers.Add(h, steer);
    }

    /// <summary>
    /// P7b: the right stick over this control scrolls something of its own (a virtualized list) rather than the panel:
    /// <paramref name="scroll"/> gets the stick (-1..1 per frame, positive up) and answers true when it moved. Null
    /// removes the hook.
    /// </summary>
    internal static void StickScroll(IUiHandle? handle, Func<float, bool>? scroll)
    {
        if (handle is not UiHandle h) return;
        StickScrolls.Remove(h);
        if (scroll != null) StickScrolls.Add(h, scroll);
    }

    private static bool TrySteer(NavCommand command)
    {
        if (_focus.IsEmpty || !Steers.TryGetValue(_focus.Handle, out var steer)) return false;
        try { return steer(command); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: nav steer threw: " + e.Message);
            return false;
        }
    }

    private static bool TryStickScroll(float amount)
    {
        if (_focus.IsEmpty || !StickScrolls.TryGetValue(_focus.Handle, out var scroll)) return false;
        try { return scroll(amount); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: nav stick scroll threw: " + e.Message);
            return false;
        }
    }

    /// <summary>The role of a handle type: <see cref="NavRole.None"/> for a type that is never a stop.</summary>
    internal static NavRole RoleOf(Type type)
    {
        if (type == typeof(UiButtonHandle) || type == typeof(UiIconLinkHandle) || type == typeof(UiMarkedButtonHandle)) return NavRole.Button;
        if (type == typeof(UiToggleHandle)) return NavRole.Toggle;
        if (type == typeof(UiSliderHandle)) return NavRole.Slider;
        if (type == typeof(UiSliderEntryHandle)) return NavRole.SliderEntry;
        if (type == typeof(UiNumericHandle)) return NavRole.Number;
        if (type == typeof(UiChoiceHandle)) return NavRole.Dropdown;
        if (type == typeof(UiOptionsHandle)) return NavRole.Options;
        if (type == typeof(UiInputHandle) || type == typeof(UiColorPickerHandle)) return NavRole.Text;
        if (type == typeof(UiKeybindPickerHandle)) return NavRole.Keybind;
        if (type == typeof(UiCellRowHandle)) return NavRole.CellRow;
        return NavRole.None;
    }

    /// <summary>
    /// Types that are never a stop, and why. A concrete handle type in neither this list nor <see cref="RoleOf"/>'s
    /// fails the coverage test, so a new control cannot ship without a decision.
    /// </summary>
    internal static readonly IReadOnlyDictionary<Type, string> NotStops = new Dictionary<Type, string>
    {
        [typeof(UiLabelHandle)] = "text to read",
        [typeof(UiToneLabelHandle)] = "text to read",
        [typeof(UiInfoHandle)] = "a live readout",
        [typeof(UiPanelHandle)] = "a container: its children are the stops",
        [typeof(UiDateTimeField)] = "its cell row is the stop (the button cells open the picker)",
    };

    /// <summary>The stops a target offers on <paramref name="handle"/>: one, or one per cell or option.</summary>
    internal static List<NavTarget> Stops(UiHandle handle)
    {
        var list = new List<NavTarget>();
        if (!IsStop(handle)) return list;
        switch (handle)
        {
            case UiCellRowHandle row:
                if (row.RowClickReachable) list.Add(new NavTarget(row));
                foreach (string key in row.NavCellKeys()) list.Add(new NavTarget(row, key));
                break;
            default:
                list.Add(new NavTarget(handle));
                break;
        }

        return list;
    }

    /// <summary>Whether <paramref name="handle"/> can take the focus now.</summary>
    internal static bool IsStop(UiHandle? handle)
    {
        if (handle == null || handle.IsDisposed || !handle.Enabled) return false;
        if (Skipped.TryGetValue(handle, out _)) return false;
        if (RoleOf(handle.GetType()) == NavRole.None) return false;
        try
        {
            if (!handle.Visible || !handle.Interactable) return false;
        }
        catch { return false; }

        if (handle is UiCellRowHandle row) return !row.IsHeader && (row.RowClickReachable || row.NavCellKeys().Count > 0);
        return true;
    }

    /// <summary>
    /// One frame: the sample becomes commands, and each runs. Also drops a focus whose control is gone and puts the
    /// marker away when the pointer is used.
    /// </summary>
    internal static void Tick(in NavSample sample, double now)
    {
        Commands.Clear();
        Repeater.Step(sample, now, Commands);
        var surface = SafeActive();
        if (surface == null)
        {
            Leave();
            return;
        }

        _active = true;
        if (!string.Equals(surface.Key, _surface, StringComparison.Ordinal)) Enter(surface);
        else Revalidate(surface);

        if (sample.PointerActive && Commands.Count == 0 && _marked) Unmark();

        if (_pendingPane >= 0) StepPending(surface);

        for (int i = 0; i < Commands.Count; i++)
        {
            var command = Commands[i];
            if ((sample.TextFocused || sample.Capturing) && command != NavCommand.Cancel) continue;
            // B while a text field has the keyboard, a key capture waits, or a list the focus is not on is open:
            // that is Escape's first (it blurs the field, cancels the capture, closes the list), never a pane move.
            if (command == NavCommand.Cancel && (sample.TextFocused || sample.Capturing || OtherListOpen()))
            {
                try { Host.Escape(); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: nav escape threw: " + e.Message); }
                surface = SafeActive();
                if (surface == null) break;
                continue;
            }

            Execute(command, surface);
            surface = SafeActive();
            if (surface == null) break;
            if (!string.Equals(surface.Key, _surface, StringComparison.Ordinal)) Enter(surface);
        }

        if (surface != null && _marked && !_focus.IsEmpty && !sample.TextFocused
            && (sample.Scroll > ScrollDeadZone || sample.Scroll < -ScrollDeadZone))
        {
            try
            {
                if (!TryStickScroll(sample.Scroll)) Host.Scroll(_focus, sample.Scroll);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Ui: nav scroll threw: " + e.Message); }
        }
    }

    /// <summary>Run one command on the active screen (the bridge's <c>nav press</c> too). False when nothing took it.</summary>
    internal static bool Execute(NavCommand command)
    {
        var surface = SafeActive();
        if (surface == null) return false;
        _active = true;
        if (!string.Equals(surface.Key, _surface, StringComparison.Ordinal)) Enter(surface);
        else Revalidate(surface);
        return Execute(command, surface);
    }

    /// <summary>
    /// BRIDGEUI1 (the bridge's <c>nav focus</c>): put the controller focus on a stop of the active screen, as a run of
    /// direction presses that ended there would, and draw its marker. <paramref name="part"/> is a cell key of a cell row
    /// (empty: the row). False when no screen takes navigation or the handle is not one of its stops.
    /// </summary>
    internal static bool FocusOn(UiHandle handle, string part)
    {
        var surface = SafeActive();
        if (surface == null || handle == null || !IsStop(handle)) return false;
        if (PaneOf(surface, handle) < 0) return false;
        var target = new NavTarget(handle, part ?? "");
        if (!Stops(handle).Contains(target)) return false;
        _active = true;
        if (!string.Equals(surface.Key, _surface, StringComparison.Ordinal)) Enter(surface);
        SetFocus(target);
        Mark();
        return true;
    }

    private static bool Execute(NavCommand command, NavSurface surface)
    {
        if (command == NavCommand.None) return false;

        // The first press after the pointer was in use only shows where the focus is.
        if (!_marked && command != NavCommand.Cancel && command != NavCommand.TabPrev && command != NavCommand.TabNext)
        {
            if (_focus.IsEmpty) Enter(surface);
            if (_focus.IsEmpty) return false;
            Mark();
            return true;
        }

        if (!_focus.IsEmpty && Within(command)) return true;
        if (command is NavCommand.Up or NavCommand.Down or NavCommand.Left or NavCommand.Right && TrySteer(command)) return true;

        switch (command)
        {
            case NavCommand.Up:
            case NavCommand.Down:
            case NavCommand.Left:
            case NavCommand.Right:
                return Move(surface, command);
            case NavCommand.Submit:
                return Submit(surface);
            case NavCommand.Cancel:
                return Cancel(surface);
            case NavCommand.TabPrev:
            case NavCommand.TabNext:
                if (surface.Tab == null) return false;
                bool moved = false;
                try { moved = surface.Tab(command == NavCommand.TabNext ? 1 : -1); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Ui: nav tab threw: " + e.Message); }
                // A new tab is a new screen: the focus starts again. At either end nothing moved, and the focus stays.
                if (moved) _surface = "";
                return moved;
            case NavCommand.Reset:
                if (_focus.IsEmpty || !Hooks.TryGetValue(_focus.Handle, out var hook) || hook.Reset == null) return false;
                try { return hook.Reset(); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Ui: nav reset threw: " + e.Message);
                    return false;
                }
            default:
                return false;
        }
    }

    /// <summary>The focused control takes the command itself: a value change, an open list, a cell or option move.</summary>
    private static bool Within(NavCommand command)
    {
        var target = _focus;
        var handle = target.Handle;
        int delta = command == NavCommand.Left ? -1 : command == NavCommand.Right ? 1 : 0;
        switch (handle)
        {
            case UiChoiceHandle choice when choice.IsOpen:
                switch (command)
                {
                    case NavCommand.Up: choice.MoveHighlight(-1); return true;
                    case NavCommand.Down: choice.MoveHighlight(1); return true;
                    case NavCommand.Submit: choice.ConfirmHighlight(); return true;
                    case NavCommand.Cancel: choice.Dismiss(); return true;
                    case NavCommand.Left:
                    case NavCommand.Right: return true;
                    default: return false;
                }
            case UiChoiceHandle choice when delta != 0:
                choice.Cycle(delta);
                return true;
            case UiToggleHandle toggle when delta != 0 && toggle.Interactable:
                toggle.Value = !toggle.Value;
                return true;
            case UiSliderHandle slider when delta != 0:
                slider.Value = UiNavMath.SliderStep(slider.Value, slider.Min, slider.Max, delta);
                return true;
            case UiSliderEntryHandle entry when delta != 0:
                entry.Nudge(delta);
                return true;
            case UiNumericHandle number when delta != 0:
                number.StepBy(delta);
                return true;
            case UiOptionsHandle options when delta != 0:
                return OptionsMove(options, target, delta);
            case UiCellRowHandle row when delta != 0:
                return CellMove(row, target, delta);
            default:
                return false;
        }
    }

    private static bool OptionsMove(UiOptionsHandle options, NavTarget target, int delta)
    {
        int count = options.Options.Count;
        if (count == 0) return true;
        if (options.Multi)
        {
            int at = ParseIndex(target.Part);
            int next = UiNavMath.Clamp(at + delta, 0, count - 1);
            if (next != at) SetFocus(new NavTarget(options, next.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            return true;
        }

        int current = IndexOfValue(options);
        int pick = UiNavMath.Clamp((current < 0 ? (delta > 0 ? -1 : count) : current) + delta, 0, count - 1);
        if (pick != current) options.Click(pick);
        return true;
    }

    private static bool CellMove(UiCellRowHandle row, NavTarget target, int delta)
    {
        var stops = Stops(row);
        if (stops.Count <= 1) return false;
        int at = stops.IndexOf(target);
        int next = at + delta;
        if (at < 0 || next < 0 || next >= stops.Count) return false;
        SetFocus(stops[next]);
        return true;
    }

    private static bool Submit(NavSurface surface)
    {
        var target = _focus;
        if (target.IsEmpty) return false;
        var handle = target.Handle;
        bool openedFromList = surface.MasterDetail && _pane == 0;
        bool acted;
        switch (handle)
        {
            case UiButtonHandle or UiIconLinkHandle or UiMarkedButtonHandle:
                Host.Click(handle);
                acted = true;
                break;
            case UiToggleHandle toggle:
                toggle.Value = !toggle.Value;
                acted = true;
                break;
            case UiChoiceHandle choice:
                choice.OpenList();
                acted = true;
                break;
            case UiOptionsHandle options:
                if (options.Multi) options.Click(ParseIndex(target.Part));
                else RadioNext(options);
                acted = true;
                break;
            case UiKeybindPickerHandle key:
                acted = key.BeginCapture();
                break;
            case UiCellRowHandle row:
                // This row's own slots: a row elsewhere with the same key (a dialog over the page) is never clicked.
                acted = target.Part.Length == 0 ? row.TryInvokeRow(out _) : row.TryInvokeCell(target.Part, out _);
                break;
            case UiInputHandle or UiColorPickerHandle or UiNumericHandle or UiSliderEntryHandle:
                acted = Host.FocusText(handle);
                break;
            default:
                acted = false;
                break;
        }

        // The workspace: a list row opens its page, and the focus follows to it once it is built.
        if (acted && openedFromList && surface.Panes.Count > 1)
        {
            _pendingPane = 1;
            _pendingFrames = 0;
            // The page still shows the previous mod's controls until the new one is built: wait for controls that are new.
            _pendingStale.Clear();
            foreach (var h in surface.Panes[1]) _pendingStale.Add(h);
        }

        return acted;
    }

    private static bool RadioNext(UiOptionsHandle options)
    {
        int count = options.Options.Count;
        if (count == 0) return false;
        int current = IndexOfValue(options);
        return options.Click((current + 1) % count);
    }

    private static bool Cancel(NavSurface surface)
    {
        // The workspace's page: back to the list, at the row that opened it.
        if (surface.MasterDetail && _pane > 0 && surface.Panes.Count > 0 && FirstStop(surface.Panes[0], preferSelected: true) is { } back)
        {
            _pane = 0;
            SetFocus(back);
            return true;
        }

        try { Host.Escape(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: nav escape threw: " + e.Message); }
        return true;
    }

    private static bool Move(NavSurface surface, NavCommand direction)
    {
        if (_focus.IsEmpty)
        {
            Enter(surface);
            return !_focus.IsEmpty;
        }

        if (_pane < 0 || _pane >= surface.Panes.Count) return false;
        var from = Host.Rect(_focus);
        var candidates = new List<(NavTarget Target, ScreenRect Rect)>();
        foreach (var handle in surface.Panes[_pane])
        {
            foreach (var stop in Stops(handle))
            {
                if (stop.Equals(_focus)) continue;
                candidates.Add((stop, Host.Rect(stop)));
            }
        }

        int pick = UiNavMath.Pick(from, candidates, direction);
        if (pick < 0)
        {
            // No geometry (a runtime that does not draw): the build order.
            pick = OrderedNeighbor(surface.Panes[_pane], direction);
            if (pick < 0) return false;
            var ordered = OrderedStops(surface.Panes[_pane]);
            SetFocus(ordered[pick]);
            return true;
        }

        SetFocus(candidates[pick].Target);
        return true;
    }

    private static int OrderedNeighbor(IReadOnlyList<UiHandle> pane, NavCommand direction)
    {
        if (!Host.Rect(_focus).IsKnown)
        {
            var ordered = OrderedStops(pane);
            int at = ordered.IndexOf(_focus);
            int step = direction is NavCommand.Down or NavCommand.Right ? 1 : -1;
            int next = at + step;
            return at >= 0 && next >= 0 && next < ordered.Count ? next : -1;
        }

        return -1;
    }

    private static List<NavTarget> OrderedStops(IReadOnlyList<UiHandle> pane)
    {
        var list = new List<NavTarget>();
        foreach (var handle in pane)
        {
            // A cell row's cells are reached with left and right; up and down stop on the row once.
            var stops = Stops(handle);
            if (stops.Count > 0) list.Add(stops[0]);
        }

        if (!_focus.IsEmpty && !list.Contains(_focus))
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (!ReferenceEquals(list[i].Handle, _focus.Handle)) continue;
                list[i] = _focus;
                break;
            }
        }

        return list;
    }

    private static void Enter(NavSurface surface)
    {
        // A screen opening over this one (a dialog): remember where the focus was, to come back to it when that screen
        // closes (live 2026-10-10: after B answered a dialog the focus went to the Mods list, not to the button that
        // asked). Coming back to the remembered screen restores it.
        if (_surface.Length > 0 && !_focus.IsEmpty && !string.Equals(_surface, surface.Key, StringComparison.Ordinal))
        {
            if (string.Equals(_returnSurface, surface.Key, StringComparison.Ordinal)) { /* returning: handled below */ }
            else
            {
                _returnSurface = _surface;
                _returnFocus = _focus;
                _returnPane = _pane;
                _returnX = _lastX;
                _returnY = _lastY;
            }
        }

        if (string.Equals(_returnSurface, surface.Key, StringComparison.Ordinal))
        {
            var back = _returnFocus;
            int backPane = _returnPane;
            float backX = _returnX, backY = _returnY;
            ForgetReturn();
            if (!back.IsEmpty && IsStop(back.Handle) && PaneOf(surface, back.Handle) == backPane)
            {
                ClearFocus();
                _surface = surface.Key;
                _pendingPane = -1;
                _pendingStale.Clear();
                _pane = backPane;
                SetFocus(back);
                return;
            }

            // The page was rebuilt while the dialog was up (the Mods page redraws when its prompt closes): the stop
            // nearest where the focus was, on the same pane.
            if (backPane >= 0 && backPane < surface.Panes.Count && !float.IsNaN(backX))
            {
                var candidates = new List<(NavTarget Target, ScreenRect Rect)>();
                foreach (var handle in surface.Panes[backPane])
                    foreach (var stop in Stops(handle))
                        candidates.Add((stop, Host.Rect(stop)));
                int pick = UiNavMath.Nearest(backX, backY, candidates);
                if (pick >= 0)
                {
                    ClearFocus();
                    _surface = surface.Key;
                    _pendingPane = -1;
                    _pendingStale.Clear();
                    _pane = backPane;
                    SetFocus(candidates[pick].Target);
                    return;
                }
            }
        }

        ClearFocus();
        _surface = surface.Key;
        _pendingPane = -1;
        _pendingStale.Clear();
        _lastX = _lastY = float.NaN;
        NavTarget? start = surface.Start is { IsEmpty: false } preferred && IsStop(preferred.Handle) ? preferred : null;
        int pane = UiNavMath.Clamp(surface.StartPane, 0, Math.Max(0, surface.Panes.Count - 1));
        if (start != null)
        {
            _pane = PaneOf(surface, start.Value.Handle);
            if (_pane < 0) _pane = pane;
            SetFocus(start.Value);
            return;
        }

        for (int i = 0; i < surface.Panes.Count; i++)
        {
            int p = (pane + i) % surface.Panes.Count;
            if (FirstStop(surface.Panes[p], preferSelected: true) is not { } first) continue;
            _pane = p;
            SetFocus(first);
            return;
        }

        _pane = pane;
    }

    /// <summary>The focused control was rebuilt or hidden: the stop nearest to where it was, on the same pane.</summary>
    private static void Revalidate(NavSurface surface)
    {
        if (!_focus.IsEmpty && IsStop(_focus.Handle) && PaneOf(surface, _focus.Handle) >= 0
            && (_focus.Part.Length == 0 || Stops(_focus.Handle).Contains(_focus) || _focus.Handle is UiOptionsHandle)) return;
        bool had = !_focus.IsEmpty;
        if (had) ClearFocus();
        if (_pane < 0 || _pane >= surface.Panes.Count) _pane = 0;
        if (surface.Panes.Count == 0) return;
        NavTarget? near = null;
        if (!float.IsNaN(_lastX))
        {
            var candidates = new List<(NavTarget Target, ScreenRect Rect)>();
            foreach (var handle in surface.Panes[_pane])
                foreach (var stop in Stops(handle))
                    candidates.Add((stop, Host.Rect(stop)));
            int pick = UiNavMath.Nearest(_lastX, _lastY, candidates);
            if (pick >= 0) near = candidates[pick].Target;
        }

        near ??= FirstStop(surface.Panes[_pane], preferSelected: true);
        if (near is { } found) SetFocus(found);
    }

    private static void StepPending(NavSurface surface)
    {
        if (++_pendingFrames > PendingPaneFrames || _pendingPane >= surface.Panes.Count)
        {
            _pendingPane = -1;
            _pendingStale.Clear();
            return;
        }

        // A on the row of the page already shown builds nothing new: after a few frames the page as it is counts.
        var fresh = new List<UiHandle>();
        foreach (var h in surface.Panes[_pendingPane])
            if (_pendingFrames >= PendingFreshFrames || !_pendingStale.Contains(h)) fresh.Add(h);
        if (FirstStop(fresh, preferSelected: false) is not { } first) return;
        _pane = _pendingPane;
        _pendingPane = -1;
        _pendingStale.Clear();
        SetFocus(first);
    }

    private static NavTarget? FirstStop(IReadOnlyList<UiHandle> pane, bool preferSelected)
    {
        NavTarget? first = null;
        float bestY = float.NegativeInfinity;
        float bestX = float.PositiveInfinity;
        foreach (var handle in pane)
        {
            var stops = Stops(handle);
            if (stops.Count == 0) continue;
            if (preferSelected && handle is UiCellRowHandle { Selected: true }) return stops[0];
            var rect = Host.Rect(stops[0]);
            if (!rect.IsKnown)
            {
                first ??= stops[0];
                continue;
            }

            // Reading order: the highest row, then the leftmost (screen y grows upward).
            float y = rect.MaxY;
            float x = rect.MinX;
            if (first == null || y > bestY + 0.5f || (Math.Abs(y - bestY) <= 0.5f && x < bestX))
            {
                first = stops[0];
                bestY = y;
                bestX = x;
            }
        }

        return first;
    }

    private static int PaneOf(NavSurface surface, UiHandle handle)
    {
        for (int p = 0; p < surface.Panes.Count; p++)
            for (int i = 0; i < surface.Panes[p].Count; i++)
                if (ReferenceEquals(surface.Panes[p][i], handle)) return p;
        return -1;
    }

    private static void SetFocus(NavTarget target)
    {
        if (target.Equals(_focus)) return;
        if (_marked) MarkOff();
        if (_focus.Handle is UiOptionsHandle oldOptions && !ReferenceEquals(oldOptions, target.Handle))
        {
            try { oldOptions.HoverOption(-1); } catch { /* */ }
        }

        _focus = target;
        try
        {
            var rect = Host.Rect(target);
            if (rect.IsKnown)
            {
                _lastX = (rect.MinX + rect.MaxX) * 0.5f;
                _lastY = (rect.MinY + rect.MaxY) * 0.5f;
            }
        }
        catch { /* */ }

        if (target.Handle is UiOptionsHandle options && options.Multi)
        {
            if (target.Part.Length == 0)
            {
                _focus = new NavTarget(options, "0");
                target = _focus;
            }

            try { options.HoverOption(ParseIndex(target.Part)); } catch { /* */ }
        }

        if (_marked) Mark();
    }

    private static void ClearFocus()
    {
        if (_marked) MarkOff();
        if (_focus.Handle is UiOptionsHandle options)
        {
            try { options.HoverOption(-1); } catch { /* */ }
        }

        _focus = default;
    }

    private static void Mark()
    {
        if (_focus.IsEmpty) return;
        _marked = true;
        Draw(_focus, true);
        try { Host.Reveal(_focus); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: nav reveal threw: " + e.Message); }
    }

    private static void MarkOff()
    {
        if (_focus.IsEmpty) return;
        Draw(_focus, false);
    }

    private static void Unmark()
    {
        MarkOff();
        _marked = false;
    }

    private static void Draw(NavTarget target, bool on)
    {
        if (target.Handle == null || target.Handle.IsDisposed) return;
        try
        {
            if (Hooks.TryGetValue(target.Handle, out var hook)) hook.Focus(on);
            else Host.Mark(target, on);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: nav marker threw: " + e.Message); }
    }

    /// <summary>No screen takes navigation: drop the focus (its marker too) but remember that the last input was ours.</summary>
    private static void Leave()
    {
        if (!_active) return;
        _active = false;
        bool marked = _marked;
        ClearFocus();
        _marked = marked;
        _surface = "";
        _pendingPane = -1;
        _pendingStale.Clear();
    }

    private static void ForgetReturn()
    {
        _returnSurface = "";
        _returnFocus = default;
        _returnPane = 0;
        _returnX = _returnY = float.NaN;
    }

    private static NavSurface? SafeActive()
    {
        try { return Host.Active(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: nav surface threw: " + e.Message);
            return null;
        }
    }

    private static bool OtherListOpen() =>
        UiChoiceHandle.AnyOpen && !(_focus.Handle is UiChoiceHandle { IsOpen: true });

    /// <summary>
    /// The game's own Close (its Back and Menu_Close actions) must be held this frame: B on a Mods workspace page goes
    /// back to the list, and the game's menu must not close under it. Read by the shown-input pump's close hold.
    /// </summary>
    internal static bool HoldsGameClose => _active && _marked && _pane > 0 && _surface.StartsWith("workspace:", StringComparison.Ordinal);

    private static int IndexOfValue(UiOptionsHandle options)
    {
        var values = options.Values;
        for (int i = 0; i < values.Count; i++)
            if (string.Equals(values[i], options.Value, StringComparison.Ordinal)) return i;
        return -1;
    }

    private static int ParseIndex(string part) =>
        int.TryParse(part, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int i) && i >= 0 ? i : 0;

    /// <summary>The readback (bridge <c>nav</c>): the screen, the pane and the focused control.</summary>
    internal static Dictionary<string, object?> Describe()
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["surface"] = _surface,
            ["pane"] = _pane,
            ["marked"] = _marked,
            ["pendingPane"] = _pendingPane,
        };
        if (_focus.IsEmpty)
        {
            d["focus"] = null;
            return d;
        }

        var handle = _focus.Handle;
        string text = "";
        try { text = handle is UiCellRowHandle row ? row.Key : handle.Text ?? ""; }
        catch { text = ""; }
        d["focus"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["kind"] = RoleOf(handle.GetType()).ToString(),
            ["type"] = handle.GetType().Name,
            ["text"] = text,
            ["part"] = _focus.Part,
            ["hooked"] = Hooks.TryGetValue(handle, out _),
        };
        return d;
    }

    /// <summary>
    /// No panel is shown (the runtime's per-frame pump stops): drop the focus, so no handle (which holds its plugin's
    /// owner) is kept across a plugin unload. Whether the last input was a navigation input is kept.
    /// </summary>
    internal static void Release()
    {
        bool marked = _marked;
        try { ClearFocus(); } catch { _focus = default; }
        _focus = default;
        _marked = marked;
        _active = false;
        _surface = "";
        _pendingPane = -1;
        _pendingStale.Clear();
        ForgetReturn();
        Repeater.Reset();
    }

    /// <summary>Forget everything (tests, a Core unload). The marker is not redrawn.</summary>
    internal static void Reset()
    {
        try { if (_marked) MarkOff(); } catch { /* */ }
        _focus = default;
        _surface = "";
        _pane = 0;
        _marked = false;
        _active = false;
        _lastX = _lastY = float.NaN;
        _pendingPane = -1;
        _pendingFrames = 0;
        _pendingStale.Clear();
        ForgetReturn();
        Repeater.Reset();
        Commands.Clear();
    }

    private sealed class NullHost : INavHost
    {
        internal static readonly NullHost Instance = new();

        public NavSurface? Active() => null;

        public ScreenRect Rect(NavTarget target) => ScreenRect.Unknown;

        public void Mark(NavTarget target, bool on) { }

        public void Reveal(NavTarget target) { }

        public bool FocusText(UiHandle handle) => false;

        public void Click(UiHandle handle) { }

        public void Escape() { }

        public void Scroll(NavTarget target, float amount) { }
    }
}

/// <summary>NAV1: the arithmetic of navigation: which stop a direction reaches, slider steps. Game-free.</summary>
internal static class UiNavMath
{
    /// <summary>A plain slider moves by a twentieth of its range per step.</summary>
    internal const float SliderSteps = 20f;

    internal static int Clamp(int value, int min, int max) => value < min ? min : value > max ? max : value;

    /// <summary>
    /// A keyboard Space or Enter counts for navigation only when it went down while nothing else owned it (a text field,
    /// a key capture, a dialog's own Enter). Once owned, it stays out until released, so the press that answered a
    /// dialog or left a field does not act again on the screen behind it.
    /// </summary>
    internal static bool KeyCounts(bool down, bool gated, ref bool latched)
    {
        if (!down)
        {
            latched = false;
            return false;
        }

        if (gated) latched = true;
        return !latched;
    }

    internal static float SliderStep(float value, float min, float max, int delta)
    {
        if (!(max > min)) return value;
        float next = value + (max - min) / SliderSteps * delta;
        return next < min ? min : next > max ? max : next;
    }

    /// <summary>
    /// The candidate a direction reaches from <paramref name="from"/>, or -1. Up and down take the nearest row
    /// beyond this one, preferring the candidate most in line with it; left and right take the nearest candidate
    /// that overlaps this row. Screen pixels, y up.
    /// </summary>
    internal static int Pick(ScreenRect from, IReadOnlyList<(NavTarget Target, ScreenRect Rect)> candidates, NavCommand direction)
    {
        if (!from.IsKnown || candidates == null) return -1;
        float fx = (from.MinX + from.MaxX) * 0.5f;
        float fy = (from.MinY + from.MaxY) * 0.5f;
        int best = -1;
        float bestScore = float.PositiveInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            var r = candidates[i].Rect;
            if (!r.IsKnown) continue;
            float cx = (r.MinX + r.MaxX) * 0.5f;
            float cy = (r.MinY + r.MaxY) * 0.5f;
            float score;
            switch (direction)
            {
                case NavCommand.Down:
                case NavCommand.Up:
                {
                    // Beyond this row: the candidate's center past this rect's edge.
                    float gap = direction == NavCommand.Down ? from.MinY - cy : cy - from.MaxY;
                    if (gap <= 0f) continue;
                    // In line: no horizontal cost while the candidate overlaps this column.
                    float dx = r.MaxX < from.MinX ? from.MinX - r.MaxX : r.MinX > from.MaxX ? r.MinX - from.MaxX : 0f;
                    score = gap + dx * 2f;
                    break;
                }
                case NavCommand.Left:
                case NavCommand.Right:
                {
                    // Same row only: the vertical spans overlap.
                    if (r.MaxY < from.MinY || r.MinY > from.MaxY) continue;
                    float gap = direction == NavCommand.Right ? cx - fx : fx - cx;
                    if (gap <= 0.5f) continue;
                    score = gap + Math.Abs(cy - fy);
                    break;
                }
                default:
                    return -1;
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        return best;
    }

    /// <summary>The candidate whose center is nearest the point, or -1.</summary>
    internal static int Nearest(float x, float y, IReadOnlyList<(NavTarget Target, ScreenRect Rect)> candidates)
    {
        int best = -1;
        float bestD = float.PositiveInfinity;
        for (int i = 0; i < candidates.Count; i++)
        {
            var r = candidates[i].Rect;
            if (!r.IsKnown) continue;
            float dx = (r.MinX + r.MaxX) * 0.5f - x;
            float dy = (r.MinY + r.MaxY) * 0.5f - y;
            float d = dx * dx + dy * dy;
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }

        return best;
    }
}
