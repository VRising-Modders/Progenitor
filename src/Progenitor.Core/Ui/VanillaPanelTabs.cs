using System;
using System.Collections.Generic;

namespace Progenitor.Ui;

/// <summary>Tabs inside a container or station window. Index zero is the game's original view.</summary>
public interface IVanillaPanelTabs : IDisposable
{
    int Selected { get; set; }
    bool ShowRedistribution { get; set; }
    void Mount(IUiPanel panel);
    event Action<VanillaPanelEvent>? PanelChanged;
    event Action<int>? SelectionChanged;

    /// <summary>
    /// Enable or disable one tab (index zero is the game's own view). A disabled tab is drawn grayed
    /// out, cannot be clicked, selected or navigated to, and shows <paramref name="reason"/> as its
    /// hover tooltip. Disabling the selected tab moves the selection to tab zero, or to the first
    /// enabled tab, raising <see cref="SelectionChanged"/>. The state is kept across targets and
    /// re-layout until changed. Out-of-range indexes are ignored. Default: no-op.
    /// </summary>
    void SetTabEnabled(int index, bool enabled, string reason = "") { }

    /// <summary>False while <see cref="SetTabEnabled"/> has disabled the tab. Default: true.</summary>
    bool IsTabEnabled(int index) => true;
}

public static partial class Ui
{
    /// <summary>Add native-looking tabs to a chest or station. Track the returned handle with the plugin owner.
    /// Mount an ordinary panel when a custom tab is selected. Closing restores the original view.</summary>
    public static IVanillaPanelTabs AttachPanelTabs(VanillaUiSite site, string[] captions, object owner)
    {
        if (site != VanillaUiSite.ContainerPanel && site != VanillaUiSite.StationPanel)
            throw new ArgumentException("Tabs support containers and stations.", nameof(site));
        if (captions == null || captions.Length < 2 || captions.Length > 6)
            throw new ArgumentException("Provide the native caption followed by one to five custom tabs.", nameof(captions));
        return new VanillaPanelTabs(CurrentRuntime(), site, (string[])captions.Clone(), owner);
    }
}

internal interface IRuntimeVanillaTabs : IDisposable
{
    void Update(in VanillaSiteSample sample);
    void Select(int index);
    void Mount(IRuntimePanel panel);
    bool ShowRedistribution { get; set; }

    /// <summary>Draw tab <paramref name="index"/> enabled or disabled, with its hover reason. Default: no-op (test fakes).</summary>
    void SetTabEnabled(int index, bool enabled, string reason) { }
}

/// <summary>Which tab a selection falls back to when tabs are disabled. Game-free.</summary>
internal static class VanillaPanelTabFallback
{
    /// <summary>
    /// <paramref name="selected"/> when it is enabled; else tab zero when enabled; else the first
    /// enabled tab; else tab zero (the game's own view) when every tab is disabled.
    /// </summary>
    internal static int Resolve(int selected, IReadOnlyList<bool> disabled)
    {
        if (selected >= 0 && selected < disabled.Count && !disabled[selected]) return selected;
        for (int i = 0; i < disabled.Count; i++)
            if (!disabled[i]) return i;
        return 0;
    }

    /// <summary>
    /// Whether strip tab <paramref name="index"/> is disabled. The Redistribution tab (the index past
    /// the captions) is the game's view too, so it follows tab zero: a click on it would select the
    /// game's view, which a disabled tab zero refuses, and the strip would leave the plugin's tab.
    /// </summary>
    internal static bool IsOff(int index, IReadOnlyList<bool> disabled)
        => index >= 0 && disabled.Count > 0 && (index < disabled.Count ? disabled[index] : disabled[0]);

    /// <summary>
    /// Strip tab <paramref name="index"/> is drawn selected: it is the selection and is enabled. With
    /// every tab disabled the selection rests on tab zero, which is then not highlighted.
    /// </summary>
    internal static bool Highlights(int index, int selected, IReadOnlyList<bool> disabled)
        => index == selected && !IsOff(index, disabled);
}

/// <summary>
/// How a disabled panel tab is drawn (<see cref="IVanillaPanelTabs.SetTabEnabled"/>). Game-free.
/// The game's own disabled look when the donor tab's button draws its Disabled state differently
/// from Normal; otherwise the faint style, the whole tab at <see cref="FaintAlpha"/>.
/// </summary>
internal static class VanillaTabDisabledLook
{
    /// <summary>The faint style's opacity, as a disabled cell (<c>CellTone.Disabled</c>) is drawn.</summary>
    internal const float FaintAlpha = .45f;

    internal const string Donor = "donor";
    internal const string Faint = "faint";

    /// <summary>
    /// One state target of the tab's button draws Disabled unlike Normal: a color tint whose disabled
    /// color differs (both clear counts as no difference), a sprite swap with a disabled sprite, or an
    /// animation with a disabled trigger. <paramref name="transition"/> is Unity's numbering: 1 color
    /// tint, 2 sprite swap, 3 animation.
    /// </summary>
    internal static bool Draws(int transition, (float R, float G, float B, float A) normal,
        (float R, float G, float B, float A) disabled, bool disabledSprite, bool disabledTrigger)
    {
        switch (transition)
        {
            case 1:
                if (normal.A < .02f && disabled.A < .02f) return false;
                return Math.Abs(normal.R - disabled.R) > .02f || Math.Abs(normal.G - disabled.G) > .02f
                    || Math.Abs(normal.B - disabled.B) > .02f || Math.Abs(normal.A - disabled.A) > .02f;
            case 2: return disabledSprite;
            case 3: return disabledTrigger;
            default: return false;
        }
    }
}

internal sealed class VanillaPanelTabs : IVanillaPanelTabs
{
    private static readonly List<VanillaPanelTabs> Live = new();
    private object? _owner;
    private readonly VanillaUiSite _site;
    private readonly int _count;
    private readonly VanillaPanelTracker _tracker = new();
    private IRuntimeVanillaTabs? _host;
    private IDisposable? _watch;
    private bool _disposed;
    private int _selected;
    private readonly bool[] _disabled;
    private readonly string[] _reasons;
    public event Action<VanillaPanelEvent>? PanelChanged;
    public event Action<int>? SelectionChanged;

    internal VanillaPanelTabs(IUiRuntime runtime, VanillaUiSite site, string[] captions, object owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _site = site;
        _count = captions.Length;
        _disabled = new bool[_count];
        _reasons = new string[_count];
        _host = runtime.CreateVanillaTabs(site, captions, i => Selected = i);
        Live.Add(this);
        if (runtime.IsClient) _watch = runtime.WatchSite(site, OnFrame);
    }

    public int Selected
    {
        get => _selected;
        set
        {
            if (_disposed || value < 0 || value >= _count || _disabled[value]) return;
            _host?.Select(value);
            if (value == _selected) return;
            _selected = value;
            SelectionChanged?.Invoke(value);
        }
    }

    public bool ShowRedistribution
    {
        get => _host?.ShowRedistribution ?? false;
        set { if (!_disposed && _host != null && _host.ShowRedistribution != value) { Selected = Fallback(0); _host.ShowRedistribution = value; } }
    }

    public void SetTabEnabled(int index, bool enabled, string reason = "")
    {
        if (_disposed || index < 0 || index >= _count) return;
        string why = enabled ? "" : reason ?? "";
        if (_disabled[index] == !enabled && _reasons[index] == why) return;
        _disabled[index] = !enabled;
        _reasons[index] = why;
        _host?.SetTabEnabled(index, enabled, why);
        // While closed nothing is shown: the next open falls back after PanelChanged.
        if (!enabled && index == _selected && _tracker.IsOpen) FallBack();
    }

    public bool IsTabEnabled(int index) => index < 0 || index >= _count || !_disabled[index];

    /// <summary>The reason given when the tab was disabled; empty while it is enabled (tests).</summary>
    internal string TabReason(int index) => index >= 0 && index < _count ? _reasons[index] ?? "" : "";

    private int Fallback(int wanted) => VanillaPanelTabFallback.Resolve(wanted, _disabled);

    /// <summary>Leave a disabled selected tab for the fallback tab, raising <see cref="SelectionChanged"/>.</summary>
    private void FallBack()
    {
        int next = Fallback(_selected);
        if (next == _selected) return;
        _host?.Select(next);
        _selected = next;
        SelectionChanged?.Invoke(next);
    }

    public void Mount(IUiPanel panel)
    {
        // WINDOWS1 review: a panel mounted in a chest or station tab is placed by that tab, never in the window row (it
        // pushed a window aside, and Escape could hide it and blank the tab).
        if (panel is UiPanelHandle tagged) { tagged.Flows = false; tagged.MountedInTab = true; }
        if (!_disposed && panel is UiPanelHandle h && h.RuntimePanel != null) { h.GridControls = true; _host?.Mount(h.RuntimePanel); }
    }

    private void OnFrame(VanillaSiteSample sample)
    {
        if (_disposed) return;
        // A station window without the game's tab row (a unit station) cannot host the strip: to
        // the tabs it is closed, so the consumer never mounts content onto a window with no tabs.
        if (sample.Resolved && !VanillaStationWindows.HostsTabs(_site, sample.Target.Kind)) sample = default;
        var change = _tracker.Observe(sample.Resolved, sample.Target, out var previous);
        if (change != null) { _selected = 0; _host?.Select(0); }
        _host?.Update(sample);
        if (change != null) PanelChanged?.Invoke(new VanillaPanelEvent(_site, change.Value, _tracker.Target, previous));
        // A target opens on the game's view; when that tab is disabled, the first enabled tab is selected instead.
        if (change != null && change.Value != VanillaPanelChange.Closed && !_disposed) FallBack();
    }

    /// <summary>
    /// Leaves nothing of the plugin reachable through this handle, even when the plugin never
    /// unsubscribed: the subscribers, the owner and the runtime host (whose click slots close over
    /// this handle) are all dropped, so a Core reference that outlives the dispose cannot pin the
    /// plugin's load context. Each step is guarded so a throwing host still clears the rest.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PanelChanged = null;
        SelectionChanged = null;
        Live.Remove(this);
        var watch = _watch;
        var host = _host;
        _watch = null;
        _host = null;
        _owner = null;
        try { watch?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: panel tabs watch release threw: " + e.Message); }
        try { host?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Ui: panel tabs release threw: " + e.Message); }
    }

    /// <summary>Live handles (tests: the owner sweep leaves none behind).</summary>
    internal static int LiveCount => Live.Count;

    /// <summary>True while any subscriber is attached (tests: dispose clears plugin delegates).</summary>
    internal bool HasSubscribers => PanelChanged != null || SelectionChanged != null;

    internal static void Drop(object? owner)
    {
        foreach (var tabs in Live.ToArray())
            if (owner == null || ReferenceEquals(owner, tabs._owner)) tabs.Dispose();
    }
}
