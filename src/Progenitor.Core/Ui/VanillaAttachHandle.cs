using System;
using System.Collections.Generic;
using System.Threading;

namespace Progenitor.Ui;

/// <summary>
/// What a destroy actually did. Three answers, not two, because the two that are not failures
/// need different treatment (R61 finding 7): a <see cref="Destroyed"/> object is gone now and the
/// reference can go with it, a <see cref="Deferred"/> one has been queued by Unity and will be
/// gone at the end of the frame, and only <see cref="Failed"/> is worth destroying again.
///
/// <para>It lives beside the retry loop rather than beside the Unity destroy that produces it
/// because <see cref="VanillaAttachHandle"/> is the one consumer whose behavior changes on the
/// answer — everything else in the module only wants to know "did the object go", which
/// <c>UnityUiRuntime.TryDestroy</c> still answers as a bool.</para>
/// </summary>
internal enum UiDestroyVerdict
{
    /// <summary>The object is gone. Nothing to follow.</summary>
    Destroyed,

    /// <summary>Unity has queued the destroy; the object dies at the end of this frame.</summary>
    Deferred,

    /// <summary>The object is still here and nothing is going to remove it but another try.</summary>
    Failed,
}

/// <summary>
/// A widget that can say which of <see cref="UiDestroyVerdict"/> its last <c>Release</c> was.
/// Optional: a widget that does not implement it and is still <c>Alive</c> after a release is
/// treated as <see cref="UiDestroyVerdict.Failed"/>, which is the conservative answer (it gets
/// retried) and the behavior every widget had before.
/// </summary>
internal interface IReleaseVerdict
{
    /// <summary>The verdict of the most recent <c>Release</c>.</summary>
    UiDestroyVerdict LastRelease { get; }
}

/// <summary>
/// Core's <see cref="IVanillaAttachHandle"/>. Game-free: every Unity touch goes through
/// <see cref="IUiRuntime"/>, so the whole of the close/reopen, rebuild, degrade and teardown
/// behavior is testable against a fake.
///
/// <para><b>Row texts live here, not in Unity.</b> The container is destroyed and rebuilt every
/// time the site instance changes, which for the map card is every close and reopen. If the rows'
/// strings lived only on the cloned TMPs, a reopen would come back blank until the plugin next
/// wrote to them — and a plugin that writes on change rather than every frame would come back
/// blank and stay blank. The strings are the handle's; the GameObjects are disposable.</para>
///
/// <para><b>A destroy that did not take is not a destroy.</b> Every object this handle releases is
/// parented into the <i>vanilla</i> tree, so the reference held here is the only one in the
/// process and no sweep can find what this forgets. A <c>Release</c> that leaves the widget
/// reporting <c>Alive</c> therefore moves it to <see cref="_doomed"/> and is retried from the
/// site's frame watch — which is also why <see cref="Dispose"/> leaves the handle in the registry
/// while anything is pending, since the watch is what drives the retry (R60 finding 1). A destroy
/// Unity merely <see cref="UiDestroyVerdict.Deferred">deferred</see> goes on the same list but is
/// only <i>watched</i> from then on: it is going, and destroying it again each frame would throw
/// again and queue a second destroy (R61 finding 7).</para>
///
/// <para><b>Disposal and the handoff are one step.</b> The retry has exactly one driver — the
/// site's frame watch — and the registry decides whether to keep that watch by asking whether
/// this handle is disposed and whether it still owns a doomed object. Those two facts are
/// therefore written together, under <see cref="_gate"/>, with the disposed flag published
/// <b>last</b>: a frame that sees "disposed" has, by construction, already been able to see
/// everything <see cref="Dispose"/> filed. Publishing the flag first let a frame land in between,
/// find an empty doomed list, drop the handle and dispose the watch — and the container it was
/// about to be handed then stayed in the vanilla tree with no retry, no give-up and not even the
/// leak line (R61 finding 3).</para>
/// </summary>
internal sealed class VanillaAttachHandle : IVanillaAttachHandle
{
    /// <summary>
    /// How many frames a refused destroy is retried before the object is written off. Bounded
    /// because the alternative is a handle that is disposed, invisible to its owner, and keeping
    /// a site watch — a Core-owned per-frame delegate that pins the collectible generation —
    /// alive forever over an object Unity may simply be refusing to destroy.
    /// </summary>
    private const int MaxDestroyRetries = 30;

    private readonly object _gate = new();
    private readonly List<string> _texts = new();

    /// <summary>Each row's <see cref="LabelTone"/>, parallel to <see cref="_texts"/>.</summary>
    private readonly List<LabelTone> _tones = new();
    private readonly List<IRuntimeWidget?> _rows = new();

    /// <summary>
    /// Objects that outlived their <c>Release</c>, waiting for another frame. Only the widget is
    /// held: what to do with it is asked of the widget on every sweep and never latched here
    /// (R62 finding 5).
    /// </summary>
    private readonly List<IRuntimeWidget> _doomed = new();

    private readonly IUiRuntime _runtime;

    private IRuntimeWidget? _container;
    private int _instanceId;
    private bool _visible = true;
    private int _disposed;
    private bool _warnedContainer;
    private int _doomedTries;

    // P6: panel state, buttons and placement. Buttons are held like rows: the caption, the
    // visibility and the Bootstrap click slot id live here, the clone is disposable.
    private readonly VanillaPanelTracker _tracker = new();
    private readonly List<Action<VanillaPanelEvent>> _panelHandlers = new();
    private readonly List<string> _captions = new();
    private readonly List<VanillaButtonIcon> _icons = new();
    private readonly List<bool> _buttonShown = new();
    private readonly List<ushort> _buttonIds = new();
    private readonly List<IRuntimeWidget?> _buttons = new();
    private VanillaFit _fit = VanillaFit.Unknown;
    private string _fitReason = "";
    private bool _fitRetry;

    // Why nothing is drawn right now, for ui_read: "" while a container is attached, else the
    // plan's reason ("site" while the window is closed, "donor") or "container" when the runtime
    // could not build one (an anchor group missing on this build).
    private string _withheld = "site";

    // Refit scheduling (review of P6, finding 5): the fit reads rects and walks the vanilla group,
    // so it runs when something that can move the container changed - a build, a row or button
    // count change, a target change, a Visible write - and otherwise once every RefitEvery kept
    // frames, to follow a resolution change. An inline or tooltip verdict of Blocked is final for
    // the container it was made on (the game laid it out and it did not fit); the next build asks
    // again.
    private const int RefitEvery = 30;
    private const int MaxPendingFits = 10;
    private bool _fitDirty = true;
    private int _keptFrames;
    private int _pendingFits;

    internal VanillaAttachHandle(
        VanillaUiSite site,
        string pluginId,
        string containerName,
        object? owner,
        IUiRuntime runtime,
        bool enabled,
        VanillaPanelAnchor anchor = VanillaPanelAnchor.Default,
        int requirementLine = -1)
    {
        RequirementLine = anchor == VanillaPanelAnchor.TooltipRequirementLine ? Math.Max(0, requirementLine) : -1;
        Site = site;
        PluginId = pluginId ?? UiOwnership.UnownedId;
        ContainerName = containerName;
        Owner = owner;
        _runtime = runtime ?? NullUiRuntime.Instance;
        Enabled = enabled;
        Anchor = anchor;
        IsPanel = VanillaUiSites.IsPanel(site);
    }

    public VanillaUiSite Site { get; }

    public VanillaPanelAnchor Anchor { get; }

    public int RequirementLine { get; }

    /// <summary>A P6 panel site: open/close tracking, buttons and placement apply.</summary>
    internal bool IsPanel { get; }

    public bool IsOpen
    {
        get { lock (_gate) return _tracker.IsOpen; }
    }

    public VanillaPanelTarget Target
    {
        get { lock (_gate) return _tracker.Target; }
    }

    public int ButtonCount
    {
        get { lock (_gate) return _captions.Count; }
    }

    /// <summary>The last placement verdict (diagnostics and tests).</summary>
    internal VanillaFit Fit
    {
        get { lock (_gate) return _fit; }
    }

    public IDisposable OnPanel(Action<VanillaPanelEvent> handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (!Enabled || !IsPanel || IsDisposed) return NoLease.Instance;
        bool open;
        VanillaPanelTarget target;
        lock (_gate)
        {
            if (Owner == null) Source ??= PluginDelegateSource.Name(handler);
            _panelHandlers.Add(handler);
            open = _tracker.IsOpen;
            target = _tracker.Target;
        }

        // A plugin that subscribes while the panel is already open (a hot reload with a chest open)
        // must not have to wait for a close and reopen to learn what is shown.
        if (open) Raise(new[] { handler }, new VanillaPanelEvent(Site, VanillaPanelChange.Opened, target, VanillaPanelTarget.None));

        return new PanelLease(this, handler);
    }

    /// <summary>
    /// HOLDMOD1: one <see cref="VanillaPanelChange.Refreshed"/> to this handle's <see cref="OnPanel"/> handlers on the
    /// site's next frame, while the panel stays open on the same target. False (nothing raised) while it is closed, on a
    /// disabled or disposed handle, and on the map card.
    /// </summary>
    public bool Refresh()
    {
        if (!Enabled || !IsPanel || IsDisposed) return false;
        lock (_gate)
        {
            if (!_tracker.RequestRefresh()) return false;
            _fitDirty = true;
            return true;
        }
    }

    private void Unsubscribe(Action<VanillaPanelEvent> handler)
    {
        lock (_gate) _panelHandlers.Remove(handler);
    }

    public IUiButton Button(string caption) => Button(caption, VanillaButtonIcon.Default);

    public IUiButton Button(string caption, VanillaButtonIcon icon)
    {
        if (!Enabled || IsDisposed || !VanillaUiSites.Info(Site).AcceptsButtons) return DisabledAttachButton.Instance;
        int index;
        ushort id = UiSlotTable.Alloc(Owner);
        lock (_gate)
        {
            index = _captions.Count;
            _captions.Add(caption ?? "");
            _icons.Add(icon);
            _buttonShown.Add(true);
            _buttonIds.Add(id);
            _buttons.Add(null);
            _fitDirty = true;
            if (ContainerAliveLocked()) SyncButtonsLocked();
        }

        return new VanillaAttachButton(this, index, id);
    }

    public string PluginId { get; }

    public string ContainerName { get; }

    public bool Enabled { get; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public bool IsAttached
    {
        get { lock (_gate) return ContainerAliveLocked(); }
    }

    public int RowCount
    {
        get { lock (_gate) return _texts.Count; }
    }

    /// <summary>
    /// The plugin that attached. Cleared by <see cref="Dispose"/> (PINS1): a disposed handle can stay in the registry
    /// for a few frames to retry a refused destroy, and it must not keep the plugin, and with it its load context,
    /// alive while it does.
    /// </summary>
    internal object? Owner { get; private set; }

    /// <summary>
    /// PINS1: for a handle attached without an owner, the assembly name of the first panel handler's declaring
    /// assembly, so the owner sweep of the plugin that subscribed still finds it.
    /// </summary>
    internal string? Source { get; private set; }

    public bool Visible
    {
        get { lock (_gate) return _visible; }
        set
        {
            lock (_gate)
            {
                _visible = value;
                _fitDirty = true;
                if (ContainerAliveLocked()) _container!.Visible = value && _fit != VanillaFit.Blocked;
            }
        }
    }

    public IUiLabel Row(string text)
    {
        if (IsDisposed) return new VanillaAttachRow(this, -1);
        int index;
        lock (_gate)
        {
            index = _texts.Count;
            _texts.Add(text ?? "");
            _tones.Add(LabelTone.Normal);
            _rows.Add(null);
            _fitDirty = true;
            if (Enabled && ContainerAliveLocked()) SyncRowsLocked();
        }

        return new VanillaAttachRow(this, index);
    }

    public void SetRows(IReadOnlyList<string> rows)
    {
        if (IsDisposed) return;
        lock (_gate)
        {
            int n = rows?.Count ?? 0;
            ResizeRowsLocked(n);
            for (int i = 0; i < n; i++)
            {
                _texts[i] = rows![i] ?? "";
                _tones[i] = LabelTone.Normal;
            }

            if (Enabled && ContainerAliveLocked()) SyncRowsLocked();
        }
    }

    public void SetRows(IReadOnlyList<VanillaRow> rows)
    {
        if (IsDisposed) return;
        lock (_gate)
        {
            int n = rows?.Count ?? 0;
            ResizeRowsLocked(n);
            for (int i = 0; i < n; i++)
            {
                var r = rows![i];
                _texts[i] = r.Text;
                _tones[i] = r.Tone;
            }

            if (Enabled && ContainerAliveLocked()) SyncRowsLocked();
        }
    }

    /// <summary>Grow or shrink the row lists to <paramref name="n"/>; a dropped row's clone is released.</summary>
    private void ResizeRowsLocked(int n)
    {
        if (n != _texts.Count) _fitDirty = true;
        while (_texts.Count > n)
        {
            int last = _texts.Count - 1;
            ReleaseRowLocked(last);
            _texts.RemoveAt(last);
            _tones.RemoveAt(last);
            _rows.RemoveAt(last);
        }

        while (_texts.Count < n)
        {
            _texts.Add("");
            _tones.Add(LabelTone.Normal);
            _rows.Add(null);
        }
    }

    /// <summary>
    /// Give up the attachment. The handle leaves the registry <b>only once every object it owns
    /// is confirmed destroyed</b>: while a destroy is pending the entry — and with it the site's
    /// frame watch — is what gets the retry another frame (R60 finding 1).
    ///
    /// <para>The destroy, the handoff to <see cref="_doomed"/> and the publication of the
    /// disposed flag are one step under <see cref="_gate"/>, in that order, and the registry is
    /// only told afterwards — where it re-reads both facts under its own lock rather than
    /// trusting a snapshot this method took (R61 finding 3).</para>
    /// </summary>
    public void Dispose()
    {
        ushort[] ids;
        lock (_gate)
        {
            // Read under the gate every write to it is also under, so this is the whole of the
            // idempotence: a second Dispose sees the flag and a concurrent Apply cannot see it
            // until the work below is done.
            if (Volatile.Read(ref _disposed) != 0) return;
            DestroyContainerLocked();
            _texts.Clear();
            _tones.Clear();
            _rows.Clear();
            ids = _buttonIds.ToArray();
            _captions.Clear();
            _icons.Clear();
            _buttonShown.Clear();
            _buttonIds.Clear();
            _buttons.Clear();
            _panelHandlers.Clear();
            _tracker.Reset();
            Owner = null;

            // Last, and inside the gate. This is the ordering the whole finding is about.
            Volatile.Write(ref _disposed, 1);
        }

        // The Bootstrap click slots go with the handle: the listener comes off whatever event it is
        // on and the Core-side handler entry is dropped, so a click on a clone Unity has not
        // destroyed yet reaches nothing.
        ReleaseButtonSlots(ids);
        VanillaAttachRegistry.Settle(this);
    }

    private void ReleaseButtonSlots(ushort[] ids)
    {
        for (int i = 0; i < ids.Length; i++)
        {
            try { _runtime.Unwire(ids[i]); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(Site) + " button unwire threw: " + e.Message);
            }

            UiSlotTable.Release(ids[i]);
        }
    }

    /// <summary>Objects whose destroy was refused and is still being retried. Diagnostics and tests.</summary>
    internal int PendingDestroys
    {
        get { lock (_gate) return _doomed.Count; }
    }

    /// <summary>
    /// One refresh tick. Reads this frame's sample, asks <see cref="VanillaAttachPlan"/> what to
    /// do, and does it. Called from the site's frame watch, on the game's own thread.
    /// </summary>
    internal void Apply(in VanillaSiteSample sample)
    {
        if (!Enabled) return;

        bool disposed;
        lock (_gate)
        {
            // Read under the same gate Dispose publishes under. Outside it, this frame could see
            // a handle that is already disposed but whose doomed list has not been filled yet,
            // and then hand the registry a "nothing pending" it had no right to (R61 finding 3).
            disposed = IsDisposed;

            // The retry runs first and runs even for a disposed handle: this frame is the only
            // reason a disposed-but-pending handle is still in the table.
            if (_doomed.Count > 0) SweepDoomedLocked();
        }

        if (disposed)
        {
            // Outside the gate, and that is the rule rather than the convenience it reads as.
            // Settle takes the registry's lock and then, still holding it, reads IsDisposed and
            // PendingDestroys — which take this handle's _gate. So the only order this pair is
            // ever taken in is registry-then-handle, and the invariant that keeps it acyclic is
            // "never hold _gate across a call into the registry". A registry call added inside
            // lock (_gate) would invert the pair and deadlock. The dispatch that called us walks
            // a snapshot, so leaving the table mid-pass is safe.
            VanillaAttachRegistry.Settle(this);
            return;
        }

        // P6: the open/close/target edge, raised before this frame's plan so a handler's SetRows
        // or Button lands in the same frame's build. Raised outside the gate: a handler is plugin
        // code and may call straight back into this handle.
        if (IsPanel) ObservePanel(sample);

        bool disposedSince = false;
        lock (_gate)
        {
            // A Dispose can land between the two gate sections. Without this re-check the plan
            // below sees "no container" (Dispose moved it to _doomed) and builds a new one for a
            // disposed handle, which nothing would ever destroy.
            if (IsDisposed)
            {
                disposedSince = true;
            }
            else
            {
                ApplyLocked(sample);
            }
        }

        // Same rule as above: the registry is only called with the gate released.
        if (disposedSince) VanillaAttachRegistry.Settle(this);
    }

    private void ApplyLocked(in VanillaSiteSample sample)
    {
        {
            var state = new VanillaAttachState(ContainerAliveLocked(), _instanceId);
            var decision = VanillaAttachPlan.Decide(Site, sample, state);

            switch (decision.Action)
            {
                case VanillaAttachAction.Keep:
                    if (IsPanel && ShouldRefitLocked())
                    {
                        // A button whose clone failed on the build (a donor not ready that frame) is
                        // asked for again on the refit cadence, not only on the next close and reopen.
                        if (MissingButtonsLocked()) SyncButtonsLocked();
                        FitLocked(first: false);
                    }

                    return;

                case VanillaAttachAction.Degrade:
                    _withheld = decision.Reason;
                    if (decision.DestroyOld)
                    {
                        DestroyContainerLocked();
                        Plugin.Logger?.LogInfo(
                            "Ui: attach " + VanillaUiSites.Key(Site) + " detached source=" + decision.Reason
                            + " plugin=" + PluginId);
                    }

                    return;

                case VanillaAttachAction.Rebuild:
                case VanillaAttachAction.Build:
                    if (decision.DestroyOld) DestroyContainerLocked();
                    BuildLocked(sample, decision);
                    return;
            }
        }
    }

    internal string TextAt(int index)
    {
        if (index < 0) return "";
        lock (_gate) return index < _texts.Count ? _texts[index] : "";
    }

    internal void SetTextAt(int index, string text)
    {
        if (index < 0 || IsDisposed) return;
        lock (_gate)
        {
            if (index >= _texts.Count) return;
            _texts[index] = text ?? "";
            if (index < _rows.Count && _rows[index] is { Alive: true } w) WriteRow(w, _texts[index]);
        }
    }

    internal bool RowVisibleAt(int index)
    {
        lock (_gate)
            return index >= 0 && index < _rows.Count && _rows[index] is { Alive: true } w && w.Visible;
    }

    internal void SetRowVisibleAt(int index, bool visible)
    {
        lock (_gate)
        {
            if (index >= 0 && index < _rows.Count && _rows[index] is { Alive: true } w) w.Visible = visible;
        }
    }

    private void BuildLocked(in VanillaSiteSample sample, in VanillaAttachDecision decision)
    {
        IRuntimeWidget? container = null;
        try
        {
            container = IsPanel
                ? (RequirementLine >= 0
                    ? _runtime.BuildPanelContainer(Site, Anchor, ContainerName, RequirementLine)
                    : _runtime.BuildPanelContainer(Site, Anchor, ContainerName))
                : _runtime.BuildAttachContainer(Site, ContainerName, decision.Parent, decision.IgnoreLayout);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "Ui: attach " + VanillaUiSites.Key(Site) + " container build threw: " + e.Message);
        }

        if (container == null || !container.Alive)
        {
            _withheld = "container";

            // Said once per handle, not once per frame: the watch fires every frame the map is
            // open and a per-frame warning would bury the log it is meant to explain.
            if (!_warnedContainer)
            {
                _warnedContainer = true;
                Plugin.Logger?.LogWarning(
                    "Ui: attach " + VanillaUiSites.Key(Site) + " could not build its container; "
                    + "no rows will appear (plugin=" + PluginId + ").");
            }

            return;
        }

        _container = container;
        _instanceId = sample.InstanceId;
        _warnedContainer = false;
        _withheld = "";
        // _fitReason is deliberately kept across builds: the tooltip rebuilds on every hover, and a
        // reason already said for this handle is not said again until the container fits once.
        _fit = VanillaFit.Unknown;
        _fitDirty = true;
        _keptFrames = 0;
        _pendingFits = 0;
        container.Visible = _visible;
        SyncRowsLocked();
        if (IsPanel)
        {
            SyncButtonsLocked();
            FitLocked(first: true);
        }

        // The tooltip site rebuilds on every hover, so its attach line is said once per handle
        // rather than once per build: a line per hovered item would bury the log it explains.
        if (VanillaUiSites.IsTooltip(Site) && _saidTooltipAttach) return;
        if (VanillaUiSites.IsTooltip(Site)) _saidTooltipAttach = true;

        Plugin.Logger?.LogInfo(
            "Ui: attached " + VanillaUiSites.Key(Site)
            + (IsPanel ? " anchor=" + VanillaUiSites.AnchorKey(Anchor) + " fit=" + FitWord(_fit) + " buttons=" + _buttons.Count : "")
            + " parent=" + (string.IsNullOrEmpty(sample.ParentName) ? "?" : sample.ParentName)
            + " layout=" + (IsPanel ? LayoutWordFor(Anchor) : decision.LayoutWord)
            + " rows=" + _rows.Count
            + " source=" + decision.Reason
            + " instance=" + sample.InstanceId
            + " plugin=" + PluginId);
    }

    private bool _saidTooltipAttach;

    /// <summary>
    /// The <c>layout=</c> word for a panel anchor: <c>column</c> (placed by Progenitor beside the
    /// frame), <c>group</c> (laid out by the game's own button group) or <c>tooltip</c>.
    /// </summary>
    internal static string LayoutWordFor(VanillaPanelAnchor anchor) =>
        VanillaUiSites.IsEdge(anchor) ? "column"
        : VanillaUiSites.IsTooltipAnchor(anchor) ? "tooltip"
        : "group";

    private static string FitWord(VanillaFit fit) =>
        fit == VanillaFit.Fits ? "ok" : fit == VanillaFit.Blocked ? "blocked" : "unknown";

    /// <summary>
    /// Ask the runtime to place or verify the container and show it only when it fits. A change to
    /// <see cref="VanillaFit.Blocked"/> is said once per container (one line, no spam), and so is
    /// the recovery, because "hidden because it would overlap" is otherwise indistinguishable from
    /// "never attached".
    /// </summary>
    private bool ShouldRefitLocked()
    {
        if (_fit == VanillaFit.Blocked && !VanillaUiSites.IsEdge(Anchor) && !_fitRetry) return false;
        if (_fitDirty) return true;
        if (++_keptFrames < RefitEvery) return false;
        _keptFrames = 0;
        return true;
    }

    private void FitLocked(bool first)
    {
        _fitDirty = false;
        _keptFrames = 0;
        if (!ContainerAliveLocked()) return;
        VanillaFitResult result;
        try { result = _runtime.FitAttachContainer(Site, Anchor, _container!, _texts.Count, _captions.Count); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(Site) + " fit threw: " + e.Message);
            result = new VanillaFitResult(VanillaFit.Blocked, "threw");
        }

        if (result.Settling)
        {
            // The window is still sliding in: judge nothing, show nothing, measure again next frame.
            // A placement is never shown and then hidden because it was judged on moving rects.
            _fit = VanillaFit.Unknown;
            _fitRetry = true;
            _fitDirty = true;
            try { if (_container!.Visible) _container.Visible = false; }
            catch { /* a container Unity just destroyed is the next frame's rebuild */ }
            return;
        }

        _fit = result.Fit;
        _fitRetry = result.Retry;

        // Unknown is "not measurable yet" (a group not laid out on its build frame): shown, and
        // asked again next frame, a bounded number of times. Never a hide.
        if (result.Fit == VanillaFit.Unknown && IsPanel && _pendingFits < MaxPendingFits)
        {
            _pendingFits++;
            _fitDirty = true;
        }
        bool show = _visible && result.Shown;
        try { if (_container!.Visible != show) _container.Visible = show; }
        catch { /* a container Unity just destroyed is the next frame's rebuild */ }

        if (result.Fit == VanillaFit.Blocked && _fitReason != result.Reason)
        {
            _fitReason = result.Reason;
            Plugin.Logger?.LogInfo(
                "Ui: attach " + VanillaUiSites.Key(Site) + " anchor=" + VanillaUiSites.AnchorKey(Anchor)
                + " hidden reason=" + result.Reason + " rect=" + result.Rect + " plugin=" + PluginId);
        }
        else if (result.Fit == VanillaFit.Fits && _fitReason.Length > 0)
        {
            _fitReason = "";
            Plugin.Logger?.LogInfo(
                "Ui: attach " + VanillaUiSites.Key(Site) + " anchor=" + VanillaUiSites.AnchorKey(Anchor)
                + " shown again rect=" + result.Rect + " plugin=" + PluginId);
        }
    }

    /// <summary>
    /// Feed this frame's sample to the tracker and raise the edge it produced, if any. The handler
    /// list is copied under the gate and invoked outside it.
    /// </summary>
    private void ObservePanel(in VanillaSiteSample sample)
    {
        VanillaPanelChange? edge;
        VanillaPanelTarget previous;
        VanillaPanelTarget current;
        Action<VanillaPanelEvent>[]? handlers = null;
        lock (_gate)
        {
            if (IsDisposed) return;
            edge = _tracker.Observe(sample.Resolved, sample.Target, out previous);
            current = _tracker.Target;
            if (edge != null) _fitDirty = true;
            if (edge != null && _panelHandlers.Count > 0) handlers = _panelHandlers.ToArray();
        }

        if (edge == null) return;
        var evt = new VanillaPanelEvent(Site, edge.Value, current, previous);

        // Open and close of a window are said once each; the tooltip's per-hover edges and a
        // station's target changes are the plugin's to log if it wants them.
        if (!VanillaUiSites.IsTooltip(Site) && edge.Value is VanillaPanelChange.Opened or VanillaPanelChange.Closed)
        {
            Plugin.Logger?.LogInfo(
                "Ui: panel " + VanillaUiSites.Key(Site) + " " + (edge.Value == VanillaPanelChange.Opened ? "opened " + current : "closed " + previous)
                + " plugin=" + PluginId);
        }

        if (handlers != null) Raise(handlers, evt);
    }

    private void Raise(Action<VanillaPanelEvent>[] handlers, VanillaPanelEvent evt)
    {
        for (int i = 0; i < handlers.Length; i++)
        {
            try { handlers[i](evt); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(
                    "Ui: panel " + VanillaUiSites.Key(Site) + " handler threw (plugin=" + PluginId + "): " + e.Message);
            }
        }
    }

    /// <summary>
    /// Make the cloned buttons match <see cref="_captions"/>, and (re)wire each new clone to its
    /// button's Bootstrap click slot. <c>TryWire</c> replaces the listener on the slot, so the old
    /// clone's event (destroyed with the old container) is let go in the same call.
    /// </summary>
    private void SyncButtonsLocked()
    {
        if (!IsPanel || !ContainerAliveLocked()) return;
        while (_buttons.Count < _captions.Count) _buttons.Add(null);
        for (int i = 0; i < _captions.Count; i++)
        {
            if (_buttons[i] is { Alive: true } live)
            {
                WriteRow(live, _captions[i]);
                if (live.Visible != _buttonShown[i]) live.Visible = _buttonShown[i];
                continue;
            }

            IRuntimeWidget? button = null;
            try { button = _runtime.ClonePanelButton(Site, _container!, _captions[i], i < _icons.Count ? _icons[i] : VanillaButtonIcon.Default); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(Site) + " button clone threw: " + e.Message);
            }

            if (button is not { Alive: true })
            {
                _buttons[i] = null;
                continue;
            }

            bool wired = false;
            try { wired = _runtime.TryWire(_buttonIds[i], UiSlotKind.Click, button); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(Site) + " button wire threw: " + e.Message);
            }

            if (!wired)
            {
                // A button that cannot be clicked is worse than no button: it looks like the game's
                // and does nothing. Destroy it and say so once per build.
                Plugin.Logger?.LogWarning(
                    "Ui: attach " + VanillaUiSites.Key(Site) + " button '" + _captions[i] + "' could not be wired; dropped (plugin=" + PluginId + ").");
                ReleaseLocked(button, "button");
                _buttons[i] = null;
                continue;
            }

            _buttons[i] = button;
            if (!_buttonShown[i]) button.Visible = false;
        }
    }

    /// <summary>
    /// Everything <c>ui_read panels</c> reports about this attachment, copied under the gate. The
    /// widgets are the live clones (or null), for the Unity half to read rects from.
    /// </summary>
    internal VanillaAttachReadback Readback()
    {
        lock (_gate)
        {
            bool attached = ContainerAliveLocked();
            return new VanillaAttachReadback
            {
                Site = Site,
                Anchor = Anchor,
                RequirementLine = RequirementLine,
                PluginId = PluginId,
                ContainerName = ContainerName,
                Enabled = Enabled,
                Open = _tracker.IsOpen,
                Target = _tracker.Target,
                Visible = _visible,
                Attached = attached,
                Fit = FitWord(_fit),
                FitReason = _fitReason,
                Withheld = attached ? "" : (Enabled ? _withheld : "disabled"),
                Captions = _captions.ToArray(),
                ButtonsShown = _buttonShown.ToArray(),
                Buttons = _buttons.ToArray(),
                Rows = _texts.ToArray(),
                RowTones = _tones.ToArray(),
                Container = attached ? _container : null,
            };
        }
    }

    private bool MissingButtonsLocked()
    {
        for (int i = 0; i < _captions.Count; i++)
            if (i >= _buttons.Count || _buttons[i] is not { Alive: true }) return true;
        return false;
    }

    internal string CaptionAt(int index)
    {
        lock (_gate) return index >= 0 && index < _captions.Count ? _captions[index] : "";
    }

    internal void SetCaptionAt(int index, string caption)
    {
        if (IsDisposed) return;
        lock (_gate)
        {
            if (index < 0 || index >= _captions.Count) return;
            _captions[index] = caption ?? "";
            if (index < _buttons.Count && _buttons[index] is { Alive: true } w) WriteRow(w, _captions[index]);
        }
    }

    internal bool ButtonShownAt(int index)
    {
        lock (_gate)
            return index >= 0 && index < _buttons.Count && _buttons[index] is { Alive: true } w && w.Visible;
    }

    internal void SetButtonShownAt(int index, bool shown)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _buttonShown.Count) return;
            _buttonShown[index] = shown;
            if (index < _buttons.Count && _buttons[index] is { Alive: true } w) w.Visible = shown;
        }
    }

    private sealed class PanelLease : IDisposable
    {
        private VanillaAttachHandle? _owner;
        private readonly Action<VanillaPanelEvent> _handler;

        internal PanelLease(VanillaAttachHandle owner, Action<VanillaPanelEvent> handler)
        {
            _owner = owner;
            _handler = handler;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Unsubscribe(_handler);
        }
    }

    /// <summary>
    /// Make the cloned row objects match <see cref="_texts"/>. Called on build (where every row
    /// is missing) and on every row-count change.
    /// </summary>
    private void SyncRowsLocked()
    {
        while (_rows.Count < _texts.Count) _rows.Add(null);
        while (_rows.Count > _texts.Count)
        {
            int last = _rows.Count - 1;
            ReleaseRowLocked(last);
            _rows.RemoveAt(last);
        }

        for (int i = 0; i < _texts.Count; i++)
        {
            if (_rows[i] is { Alive: true } live)
            {
                WriteRow(live, _texts[i]);
                ToneRow(live, ToneAt(i));
                continue;
            }

            IRuntimeWidget? row = null;
            try { row = _runtime.CloneDonorRow(Site, _container!, _texts[i]); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(
                    "Ui: attach " + VanillaUiSites.Key(Site) + " row clone threw: " + e.Message);
            }

            // A clone that came out without the donor's text component is destroyed by the
            // runtime and returns null. The row then simply is not there, which is the same
            // discipline the settings panel applies to a control that fails its operability
            // check: an instrument that reports success on a component rather than on a working
            // control is worse than no instrument.
            _rows[i] = row is { Alive: true } ? row : null;
            if (_rows[i] != null)
            {
                WriteRow(_rows[i]!, _texts[i]);
                ToneRow(_rows[i]!, ToneAt(i));
            }
        }
    }

    private LabelTone ToneAt(int index) => index >= 0 && index < _tones.Count ? _tones[index] : LabelTone.Normal;

    /// <summary>
    /// Give a row its color. The runtime keeps the donor's own color for <see cref="LabelTone.Normal"/>
    /// and writes only when the tone changed, so a sync that repeats the same tones costs nothing.
    /// </summary>
    private void ToneRow(IRuntimeWidget row, LabelTone tone)
    {
        try { _runtime.StyleAttachRow(row, tone); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Ui: attach " + VanillaUiSites.Key(Site) + " row tone threw: " + e.Message);
        }
    }

    /// <summary>
    /// Write <paramref name="text"/> into a row <b>only when it differs</b> from what the row
    /// already shows (R60 finding 6).
    ///
    /// <para>A plugin that pushes on a timer rather than on a change — which the Sample's rift
    /// rows do, four times a second against a string that moves once a second — otherwise assigns
    /// an identical string into TMP three frames out of four. Whether TMP's own setter early-outs
    /// on an equal string cannot be read from the interop stubs (the bodies are native), and that
    /// is precisely the argument for not depending on it: a text assignment that does reach TMP
    /// dirties the mesh and buys a re-layout inside a vanilla card. The guard belongs here rather
    /// than in each sink, so every caller gets it — the fallback log sink already had it and the
    /// real one, the one that talks to Unity, did not.</para>
    /// </summary>
    private static void WriteRow(IRuntimeWidget row, string text)
    {
        string current;
        try { current = row.Text ?? ""; }
        catch { current = ""; }

        if (string.Equals(current, text, StringComparison.Ordinal)) return;
        row.Text = text;
    }

    private void ReleaseRowLocked(int index)
    {
        if (index < 0 || index >= _rows.Count) return;
        var row = _rows[index];
        _rows[index] = null;
        if (row == null) return;
        ReleaseLocked(row, "row");
    }

    private void DestroyContainerLocked()
    {
        for (int i = 0; i < _rows.Count; i++) _rows[i] = null;
        for (int i = 0; i < _buttons.Count; i++) _buttons[i] = null;
        var container = _container;
        _container = null;
        _instanceId = 0;
        if (container == null) return;

        // Only our container is destroyed, and Unity takes the cloned rows with it. Nothing
        // vanilla is touched: no sibling order to restore, no component to re-enable, no field to
        // put back. That is the payoff for the sibling-container rule.
        ReleaseLocked(container, "container");
    }

    /// <summary>
    /// Destroy one object we own and check that it went. A widget still reporting <c>Alive</c>
    /// afterwards is kept — the reference held here is the only one in the process, because the
    /// object is parented into the vanilla tree — and followed from the next frame.
    ///
    /// <para><b>Followed, not always retried.</b> A widget whose destroy was
    /// <see cref="UiDestroyVerdict.Deferred"/> is alive only until the end of this frame, so it
    /// goes on the list to be <i>verified</i> and nothing is logged: warning that the destroy
    /// "did not take" over the fallback that is the most likely one to work was the false alarm
    /// R61 finding 7 named. Only <see cref="UiDestroyVerdict.Failed"/> — which is also what a
    /// widget that cannot say gets — earns the warning and the per-frame retry.</para>
    ///
    /// <para><b>The same guard the sweep makes, made here too (R63 finding 6).</b> This is the
    /// handle's <i>other</i> <c>Release()</c> call site, and when the widgets' private
    /// early-returns were removed in favor of one implementation, this one was left without any:
    /// a widget that already reports <see cref="UiDestroyVerdict.Deferred"/> is one Unity has
    /// queued, and asking it to destroy itself again from inside <c>MapMenuMapper.Update</c> is
    /// R61 finding 7 verbatim. No live widget can reach here twice today — a row is nulled out of
    /// <c>_rows</c> before it is released and the container out of <c>_container</c> — so the
    /// guard is what keeps the docstrings' "the rule lives in the handle" true of the handle
    /// rather than of one of its two methods.</para>
    /// </summary>
    private void ReleaseLocked(IRuntimeWidget widget, string what)
    {
        if (VerdictOf(widget) != UiDestroyVerdict.Deferred)
        {
            try { widget.Release(); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(
                    "Ui: attach " + VanillaUiSites.Key(Site) + " " + what + " release threw: " + e.Message);
            }
        }

        if (!AliveOrFalse(widget)) return;

        if (_doomed.Count == 0) _doomedTries = 0;
        _doomed.Add(widget);
        if (VerdictOf(widget) == UiDestroyVerdict.Deferred) return;

        Plugin.Logger?.LogWarning(
            "Ui: attach " + VanillaUiSites.Key(Site) + " " + what + " destroy did not take; retrying (container="
            + ContainerName + " plugin=" + PluginId + ").");
    }

    /// <summary>
    /// One frame's worth of follow-up. A failed destroy is tried again; a deferred one is only
    /// checked, because Unity already has it and a second <c>Release</c> would throw from the same
    /// call site and queue a second destroy on the same object (R61 finding 7).
    ///
    /// <para><b>Asked, not remembered.</b> The verdict is re-read from the widget on every sweep.
    /// Latching it at file time missed the transition the loop sees most often — a first release
    /// that reported <see cref="UiDestroyVerdict.Failed"/> and a retry that Unity then deferred —
    /// and kept releasing an object Unity already owned, which is R61 finding 7 one frame
    /// downstream of its own fix (R62 finding 5).</para>
    ///
    /// <para>Bounded either way: past <see cref="MaxDestroyRetries"/> the objects are written off
    /// with a line naming the container, because an operator who has to find a frozen GameObject
    /// inside the vanilla hierarchy needs its name, and because a disposed handle must not hold a
    /// site watch forever. A "deferred" object still here after that many frames was not deferred
    /// at all, and it is worth the same line.</para>
    /// </summary>
    private void SweepDoomedLocked()
    {
        _doomedTries++;
        for (int i = _doomed.Count - 1; i >= 0; i--)
        {
            var widget = _doomed[i];
            if (VerdictOf(widget) != UiDestroyVerdict.Deferred)
            {
                try { widget.Release(); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning(
                        "Ui: attach " + VanillaUiSites.Key(Site) + " retried release threw: " + e.Message);
                }
            }

            if (!AliveOrFalse(widget)) _doomed.RemoveAt(i);
        }

        if (_doomed.Count == 0)
        {
            _doomedTries = 0;
            return;
        }

        if (_doomedTries < MaxDestroyRetries) return;

        Plugin.Logger?.LogWarning(
            "Ui: attach " + VanillaUiSites.Key(Site) + " leaked " + _doomed.Count + " object(s) after "
            + MaxDestroyRetries + " destroy attempts; container=" + ContainerName + " plugin=" + PluginId
            + " (it is parented into the vanilla hierarchy, so no name sweep can reach it).");
        _doomed.Clear();
        _doomedTries = 0;
    }

    private static bool AliveOrFalse(IRuntimeWidget widget)
    {
        try { return widget.Alive; }
        catch { return false; }
    }

    /// <summary>
    /// What the widget says its last release did. A widget that cannot say — every widget before
    /// this, and every widget in a test that does not care — is <see cref="UiDestroyVerdict.Failed"/>,
    /// the answer that keeps retrying.
    /// </summary>
    private static UiDestroyVerdict VerdictOf(IRuntimeWidget widget)
    {
        try { return widget is IReleaseVerdict v ? v.LastRelease : UiDestroyVerdict.Failed; }
        catch { return UiDestroyVerdict.Failed; }
    }

    private bool ContainerAliveLocked() => _container is { Alive: true };
}

/// <summary>One attachment's state for <c>ui_read panels</c>. Game-free; the Unity half adds the rects.</summary>
internal sealed class VanillaAttachReadback
{
    internal VanillaUiSite Site { get; init; }

    internal VanillaPanelAnchor Anchor { get; init; }

    internal string PluginId { get; init; } = "";

    internal string ContainerName { get; init; } = "";

    internal bool Enabled { get; init; }

    internal bool Open { get; init; }

    internal VanillaPanelTarget Target { get; init; }

    /// <summary>The plugin's <c>Visible</c> wish.</summary>
    internal bool Visible { get; init; }

    /// <summary>A container exists under the vanilla window.</summary>
    internal bool Attached { get; init; }

    /// <summary><c>ok</c>, <c>blocked</c> or <c>unknown</c>.</summary>
    internal string Fit { get; init; } = "unknown";

    /// <summary>The last <c>hidden reason=</c> word, or empty once it fits again.</summary>
    internal string FitReason { get; init; } = "";

    /// <summary>
    /// Empty when attached; else <c>site</c> (the window is closed), <c>donor</c> (no button donor),
    /// <c>container</c> (the anchor's group is missing; the site line names it) or <c>disabled</c>.
    /// </summary>
    internal string Withheld { get; init; } = "";

    internal string[] Captions { get; init; } = Array.Empty<string>();

    internal bool[] ButtonsShown { get; init; } = Array.Empty<bool>();

    internal IRuntimeWidget?[] Buttons { get; init; } = Array.Empty<IRuntimeWidget?>();

    internal string[] Rows { get; init; } = Array.Empty<string>();

    internal LabelTone[] RowTones { get; init; } = Array.Empty<LabelTone>();

    internal IRuntimeWidget? Container { get; init; }

    /// <summary>The requirement line of a per-line attachment, else -1.</summary>
    internal int RequirementLine { get; init; } = -1;

    /// <summary>The game-free part of the readback.</summary>
    internal Dictionary<string, object?> Describe()
    {
        var buttons = new List<object?>();
        for (int i = 0; i < Captions.Length; i++)
        {
            var w = i < Buttons.Length ? Buttons[i] : null;
            bool built = w != null && w.Alive;
            buttons.Add(new Dictionary<string, object?>
            {
                ["index"] = i,
                ["caption"] = Captions[i],
                ["wanted"] = i < ButtonsShown.Length && ButtonsShown[i],
                ["built"] = built,
                ["shown"] = built && w!.Visible,
            });
        }

        var line = Container as IRequirementLineGate;
        bool containerShown = Container != null && Container.Alive && Container.Visible && (line == null || line.GameLineShown);
        var result = new Dictionary<string, object?>
        {
            ["site"] = VanillaUiSites.Key(Site),
            ["anchor"] = Anchor.ToString(),
            ["plugin"] = PluginId,
            ["container"] = ContainerName,
            ["enabled"] = Enabled,
            ["open"] = Open,
            ["target"] = Target.ToString(),
            ["visible"] = Visible,
            ["attached"] = Attached,
            ["shown"] = containerShown,
            ["fit"] = Fit,
            ["fitReason"] = FitReason,
            ["withheld"] = Withheld,
            ["buttons"] = buttons,
            ["rows"] = new List<string>(Rows),
            ["rowTones"] = Array.ConvertAll(RowTones, t => LabelTones.Name(t)),
        };
        if (RequirementLine >= 0)
        {
            result["requirementLine"] = RequirementLine;
            result["gameLineShown"] = line?.GameLineShown;
        }
        return result;
    }
}

/// <summary>
/// A per-line container (<see cref="VanillaPanelAnchor.TooltipRequirementLine"/>): drawn only while the
/// game shows its requirement line. <see cref="IRuntimeWidget.Visible"/> is the attachment's own wish;
/// <see cref="GameLineShown"/> is the game's line, read on the last sync.
/// </summary>
internal interface IRequirementLineGate
{
    /// <summary>The game showed this container's requirement line at the last sync.</summary>
    bool GameLineShown { get; }
}

/// <summary>
/// One button of a P6 attachment, addressed by position like a row. It holds the Bootstrap click
/// slot id rather than the clone: the clone is rebuilt on every close and reopen of the panel and
/// rewired to this same id, so a handler subscribed once keeps working.
/// </summary>
internal sealed class VanillaAttachButton : IUiButton
{
    private readonly VanillaAttachHandle _owner;
    private readonly int _index;
    private readonly ushort _id;
    private Action? _handler;

    internal VanillaAttachButton(VanillaAttachHandle owner, int index, ushort id)
    {
        _owner = owner;
        _index = index;
        _id = id;
    }

    /// <summary>The Bootstrap click slot (tests).</summary>
    internal ushort WidgetId => _id;

    public string Text
    {
        get => _owner.CaptionAt(_index);
        set => _owner.SetCaptionAt(_index, value ?? "");
    }

    /// <summary>Whether the button is drawn. Kept across rebuilds; false hides only this button.</summary>
    public bool Visible
    {
        get => _owner.ButtonShownAt(_index);
        set => _owner.SetButtonShownAt(_index, value);
    }

    public bool Enabled => _owner.Enabled && !_owner.IsDisposed;

    public bool IsDisposed => _owner.IsDisposed;

    public UiHandleState State =>
        IsDisposed ? UiHandleState.Disposed
        : !Enabled ? UiHandleState.Disabled
        : _owner.IsAttached ? UiHandleState.Shown
        : UiHandleState.Built;

    public string PluginId => _owner.PluginId;

    public string RootName => _owner.ContainerName;

    public IDisposable OnClick(Action handler)
    {
        if (handler == null) throw new ArgumentNullException(nameof(handler));
        if (_id == 0 || IsDisposed) return NoLease.Instance;
        _handler = handler;
        UiSlotTable.BindClick(_id, Pressed);
        return new ClickLease(this, handler);
    }

    private void Pressed()
    {
        if (IsDisposed) return;
        _handler?.Invoke();
    }

    private void Unbind(Action handler)
    {
        if (!ReferenceEquals(_handler, handler)) return;
        _handler = null;
        UiSlotTable.BindClick(_id, null);
    }

    /// <summary>
    /// A button goes with its attachment. Disposing one on its own would leave the caption list and
    /// the clones disagreeing about how many there are; hide it with <see cref="Visible"/> instead.
    /// </summary>
    public void Dispose() { }

    private sealed class ClickLease : IDisposable
    {
        private VanillaAttachButton? _button;
        private readonly Action _handler;

        internal ClickLease(VanillaAttachButton button, Action handler)
        {
            _button = button;
            _handler = handler;
        }

        public void Dispose() => Interlocked.Exchange(ref _button, null)?.Unbind(_handler);
    }
}

/// <summary>
/// One row of an attachment, addressed by position. It deliberately does <b>not</b> hold the
/// cloned GameObject: the container is rebuilt on every close/reopen of the map, so a handle over
/// an object would go dead on the first reopen while still reporting success.
/// </summary>
internal sealed class VanillaAttachRow : IUiLabel
{
    private readonly VanillaAttachHandle _owner;
    private readonly int _index;

    internal VanillaAttachRow(VanillaAttachHandle owner, int index)
    {
        _owner = owner;
        _index = index;
    }

    public string Text
    {
        get => _index < 0 ? "" : _owner.TextAt(_index);
        set { if (_index >= 0) _owner.SetTextAt(_index, value ?? ""); }
    }

    public bool Visible
    {
        get => _index >= 0 && _owner.RowVisibleAt(_index);
        set { if (_index >= 0) _owner.SetRowVisibleAt(_index, value); }
    }

    public bool Enabled => _index >= 0 && _owner.Enabled && !_owner.IsDisposed;

    public bool IsDisposed => _index < 0 || _owner.IsDisposed;

    public UiHandleState State =>
        IsDisposed ? UiHandleState.Disposed
        : !Enabled ? UiHandleState.Disabled
        : _owner.IsAttached ? UiHandleState.Shown
        : UiHandleState.Built;

    public string PluginId => _owner.PluginId;

    /// <summary>
    /// The container, not a DontDestroyOnLoad root — an attachment has none, which is exactly
    /// why the registry exists.
    /// </summary>
    public string RootName => _owner.ContainerName;

    /// <summary>
    /// A row is disposed with its attachment. Disposing one row on its own would leave the
    /// handle's text list and the cloned objects disagreeing about how many rows there are, and
    /// the next rebuild would put the row back.
    /// </summary>
    public void Dispose() { }
}
