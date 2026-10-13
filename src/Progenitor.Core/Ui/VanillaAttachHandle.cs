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

    internal VanillaAttachHandle(
        VanillaUiSite site,
        string pluginId,
        string containerName,
        object? owner,
        IUiRuntime runtime,
        bool enabled)
    {
        Site = site;
        PluginId = pluginId ?? UiOwnership.UnownedId;
        ContainerName = containerName;
        Owner = owner;
        _runtime = runtime ?? NullUiRuntime.Instance;
        Enabled = enabled;
    }

    public VanillaUiSite Site { get; }

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

    internal object? Owner { get; }

    public bool Visible
    {
        get { lock (_gate) return _visible; }
        set
        {
            lock (_gate)
            {
                _visible = value;
                if (ContainerAliveLocked()) _container!.Visible = value;
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
            _rows.Add(null);
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
            while (_texts.Count > n)
            {
                int last = _texts.Count - 1;
                ReleaseRowLocked(last);
                _texts.RemoveAt(last);
                _rows.RemoveAt(last);
            }

            for (int i = 0; i < n; i++)
            {
                string t = rows![i] ?? "";
                if (i < _texts.Count) _texts[i] = t;
                else { _texts.Add(t); _rows.Add(null); }
            }

            if (Enabled && ContainerAliveLocked()) SyncRowsLocked();
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
        lock (_gate)
        {
            // Read under the gate every write to it is also under, so this is the whole of the
            // idempotence: a second Dispose sees the flag and a concurrent Apply cannot see it
            // until the work below is done.
            if (Volatile.Read(ref _disposed) != 0) return;
            DestroyContainerLocked();
            _texts.Clear();
            _rows.Clear();

            // Last, and inside the gate. This is the ordering the whole finding is about.
            Volatile.Write(ref _disposed, 1);
        }

        VanillaAttachRegistry.Settle(this);
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
                    return;

                case VanillaAttachAction.Degrade:
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
            container = _runtime.BuildAttachContainer(Site, ContainerName, decision.Parent, decision.IgnoreLayout);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning(
                "Ui: attach " + VanillaUiSites.Key(Site) + " container build threw: " + e.Message);
        }

        if (container == null || !container.Alive)
        {
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
        container.Visible = _visible;
        SyncRowsLocked();

        Plugin.Logger?.LogInfo(
            "Ui: attached " + VanillaUiSites.Key(Site)
            + " parent=" + (string.IsNullOrEmpty(sample.ParentName) ? "?" : sample.ParentName)
            + " layout=" + decision.LayoutWord
            + " rows=" + _rows.Count
            + " source=" + decision.Reason
            + " instance=" + sample.InstanceId
            + " plugin=" + PluginId);
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
            if (_rows[i] != null) WriteRow(_rows[i]!, _texts[i]);
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
