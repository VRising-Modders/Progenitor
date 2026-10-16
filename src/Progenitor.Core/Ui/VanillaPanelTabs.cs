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
    public event Action<VanillaPanelEvent>? PanelChanged;
    public event Action<int>? SelectionChanged;

    internal VanillaPanelTabs(IUiRuntime runtime, VanillaUiSite site, string[] captions, object owner)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _site = site;
        _count = captions.Length;
        _host = runtime.CreateVanillaTabs(site, captions, i => Selected = i);
        Live.Add(this);
        if (runtime.IsClient) _watch = runtime.WatchSite(site, OnFrame);
    }

    public int Selected
    {
        get => _selected;
        set
        {
            if (_disposed || value < 0 || value >= _count) return;
            _host?.Select(value);
            if (value == _selected) return;
            _selected = value;
            SelectionChanged?.Invoke(value);
        }
    }

    public bool ShowRedistribution
    {
        get => _host?.ShowRedistribution ?? false;
        set { if (!_disposed && _host != null && _host.ShowRedistribution != value) { Selected = 0; _host.ShowRedistribution = value; } }
    }

    public void Mount(IUiPanel panel)
    {
        if (!_disposed && panel is UiPanelHandle h && h.RuntimePanel != null) { h.GridControls = true; _host?.Mount(h.RuntimePanel); }
    }

    private void OnFrame(VanillaSiteSample sample)
    {
        if (_disposed) return;
        var change = _tracker.Observe(sample.Resolved, sample.Target, out var previous);
        if (change != null) { _selected = 0; _host?.Select(0); }
        _host?.Update(sample);
        if (change != null) PanelChanged?.Invoke(new VanillaPanelEvent(_site, change.Value, _tracker.Target, previous));
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
