using System;
using System.Threading;

namespace Progenitor.Effects;

/// <summary>
/// One or more server-authored networked beam entities. <see cref="Track"/> follows
/// two entities each tick by rewriting synced <c>Start</c>/<c>End</c> (no allocation).
/// <see cref="Dispose"/> destroys the entities. Double-dispose is a no-op.
/// </summary>
public interface IServerBeamHandle : IDisposable
{
    /// <summary>Vanilla prefab name or GUID hash resolved at spawn.</summary>
    string Prefab { get; }

    /// <summary>BepInEx plugin GUID recorded for unload.</summary>
    string PluginId { get; }

    /// <summary>Look requested at spawn (coil sequences vs client tint).</summary>
    BeamPreset Preset { get; }

    /// <summary>How many networked entities this handle owns (1 for <see cref="ServerBeam.Spawn"/>).</summary>
    int Count { get; }

    /// <summary>
    /// False on the client, when the module is skipped, or when the coil prefab was missing.
    /// Track / tick become no-ops; the caller still Disposes it.
    /// </summary>
    bool Enabled { get; }

    /// <summary>True after <see cref="IDisposable.Dispose"/>, including a second call.</summary>
    bool IsDisposed { get; }

    /// <summary>First beam entity as <c>index:version</c>, or <c>0:0</c> when disabled.</summary>
    string Entity { get; }

    /// <summary>
    /// Follow <paramref name="startIndex"/>:<paramref name="startVersion"/> and
    /// <paramref name="endIndex"/>:<paramref name="endVersion"/> each server tick,
    /// plus the offsets. Zero index on an end freezes that end at the last written
    /// point. Returns this for chaining.
    /// </summary>
    IServerBeamHandle Track(
        int startIndex,
        int startVersion,
        int endIndex,
        int endVersion,
        EffectVec3 startOffset,
        EffectVec3 endOffset);

    /// <summary>
    /// Which coil sequence the clients show. Steady by default.
    /// </summary>
    BeamPhase Phase { get; }

    /// <summary>
    /// True for a <see cref="ServerBeam.Link"/> beam: its endpoints are <c>SequenceInstance</c>
    /// entity references rather than two synced float3s, so <see cref="Track"/>,
    /// <see cref="Steady"/> and <see cref="Blast"/> are no-ops and nothing is written per tick.
    ///
    /// <para><b>This is not the same as "the drawn beam follows those entities".</b> Observed
    /// live 2026-09-04: a <c>Link</c> beam stays at its spawn pose while the endpoints move.
    /// The sequencer's per-frame pulls (<c>Pull_EntitySystem</c>, <c>Pull_PositionSystem</c>,
    /// <c>Pull_RotationSystem</c>; <c>BeforeSequencerUpdateGroup</c>, <c>OrderFirst</c>, client
    /// metadata) refresh blackboard registers every frame, but
    /// <c>Pull_Position</c> pulls the <em>sequence instance entity's own</em>
    /// <c>Translation</c>, which nothing moves after spawn. Treat a
    /// <c>Link</c> beam as fixed at its spawn pose on this build; see
    /// <c>docs/effects.md</c> and the internal research notes (§A34).</para>
    /// </summary>
    bool TracksEntitiesNatively { get; }

    /// <summary>
    /// Show the steady (Preview) sequence for the whole lifetime. This is the default and
    /// what a tracking beam wants: the synced <c>PreviewTime</c> is pinned above the beam's
    /// life so no client's local Age ever passes it. Returns this for chaining.
    /// </summary>
    IServerBeamHandle Steady();

    /// <summary>
    /// Show the coil's discharge (Blast) sequence: the synced <c>PreviewTime</c> drops to
    /// zero, so every client swaps on its next frame. One-shot look; a beam that has blasted
    /// never returns to Preview on a client that already saw it
    /// (<c>ProfessorCoilSystem_Client</c> returns early once <c>BeamState == Blast</c>), so
    /// call <see cref="Steady"/> before the client sees the beam, not after.
    /// Returns this for chaining.
    /// </summary>
    IServerBeamHandle Blast();

    /// <summary>
    /// Run <paramref name="callback"/> exactly once when this handle is disposed — by the
    /// caller, by its own lifetime deadline in <c>ServerBeam.Poll</c>, by
    /// <see cref="ServerBeam.RemoveAll"/>, or by Core uninstall. Registering on an
    /// already-disposed handle runs it immediately. Use it to release anything spawned only to
    /// serve this beam (R27 finding 20b: the Sample's <c>.beam link</c> marker rat used to
    /// outlive its beam and stand in the world until the next <c>.beam off</c>). Exceptions are
    /// swallowed and logged. Returns this for chaining.
    ///
    /// <para>This holds for a <b>disabled</b> handle too (client, skipped module, missing
    /// prefab): registering a callback records the handle so <see cref="ServerBeam.RemoveAll"/>
    /// and Core uninstall can still run it (R28 finding 28.5).</para>
    ///
    /// <para><b>Thread.</b> The callback runs on the game main thread, so it may touch
    /// <c>EntityManager</c>. That holds even when the plugin disposes from a worker: the
    /// destroy and the callbacks are marshaled to the main thread together (R29 finding 15).
    /// The one exception is registering on a handle whose dispose has <em>already completed</em>,
    /// which runs the callback inline on the registering thread.</para>
    /// </summary>
    IServerBeamHandle OnDisposed(Action callback);
}

/// <summary>
/// Core-owned handle. Endpoint writes go through <see cref="IServerBeamRuntime"/> so
/// tests can drive tracking without Unity. Tick runs from <see cref="ServerBeam.Poll"/>
/// on the game main thread.
/// </summary>
internal sealed class ServerBeamHandle : IServerBeamHandle
{
    private readonly IServerBeamRuntime? _runtime;
    private readonly ServerBeamId[] _ids;
    private readonly EffectVec3[] _laterals;
    private readonly System.Collections.Generic.List<Action> _disposeCallbacks = new();
    private int _disposed;

    /// <summary>
    /// Latched by <see cref="CompleteDispose"/>, which is the only place the entities are
    /// destroyed and the only place the handle is forgotten. Separate from
    /// <see cref="_disposed"/> because an off-thread <see cref="Dispose"/> latches that one
    /// immediately and finishes a frame later (R29 finding 15).
    /// </summary>
    private int _completed;
    private bool _trackingLogged;
    private int _startIndex;
    private int _startVersion;
    private int _endIndex;
    private int _endVersion;
    private EffectVec3 _startOffset;
    private EffectVec3 _endOffset;
    private EffectVec3 _start;
    private EffectVec3 _end;
    private EffectVec3 _writtenStart;
    private EffectVec3 _writtenEnd;
    private readonly float _deadline;
    private bool _timedOut;
    private BeamPhase _phase = BeamPhase.Steady;
    private bool _keepAliveFlip;
    private float _lastKeepAlive;
    private bool _keepAliveLogged;

    internal ServerBeamHandle(
        string prefab,
        string pluginId,
        object? owner,
        BeamPreset preset,
        ServerBeamId[] ids,
        EffectVec3[] laterals,
        EffectVec3 start,
        EffectVec3 end,
        float deadline,
        float spawnedAt,
        IServerBeamRuntime? runtime,
        bool enabled,
        bool tracksEntitiesNatively = false)
    {
        TracksEntitiesNatively = tracksEntitiesNatively;
        Prefab = prefab ?? "";
        PluginId = pluginId ?? "";
        Owner = owner;
        Preset = preset;
        _ids = ids ?? Array.Empty<ServerBeamId>();
        _laterals = laterals ?? Array.Empty<EffectVec3>();
        _start = start;
        _end = end;
        _writtenStart = start;
        _writtenEnd = end;
        _deadline = deadline;
        _lastKeepAlive = spawnedAt;
        _runtime = runtime;
        Enabled = enabled;
    }

    public string Prefab { get; }

    public string PluginId { get; }

    internal object? Owner { get; }

    public BeamPreset Preset { get; }

    public int Count => _ids.Length;

    public bool Enabled { get; }

    public bool TracksEntitiesNatively { get; }

    public bool IsDisposed => _disposed != 0;

    public string Entity => _ids.Length == 0 ? "0:0" : _ids[0].ToString();

    internal bool NeedsTick =>
        _disposed == 0 && Enabled && (_startIndex != 0 || _endIndex != 0 || _deadline > 0f);

    public IServerBeamHandle Track(
        int startIndex,
        int startVersion,
        int endIndex,
        int endVersion,
        EffectVec3 startOffset,
        EffectVec3 endOffset)
    {
        ServerBeam.EnsureMainThread(nameof(Track));
        if (_disposed != 0 || !Enabled) return this;
        if (TracksEntitiesNatively)
        {
            if (!_trackingLogged)
            {
                _trackingLogged = true;
                Plugin.Logger?.LogInfo(
                    "Effects: server beam link tracks " + Fmt(startIndex, startVersion)
                    + " -> " + Fmt(endIndex, endVersion) + " natively; Track is a no-op");
            }

            return this;
        }

        _startIndex = startIndex;
        _startVersion = startVersion;
        _endIndex = endIndex;
        _endVersion = endVersion;
        _startOffset = startOffset;
        _endOffset = endOffset;
        // Force the next poll to write, even if the tracked entities happen to sit exactly
        // where the beam was spawned: the first write is what proves the link is live.
        _lastKeepAlive = float.NegativeInfinity;

        if (!_trackingLogged)
        {
            _trackingLogged = true;
            Plugin.Logger?.LogInfo(
                "Effects: server beam tracking " + Fmt(startIndex, startVersion)
                + " -> " + Fmt(endIndex, endVersion));
        }

        return this;
    }

    public IServerBeamHandle OnDisposed(Action callback)
    {
        if (callback == null) throw new ArgumentNullException(nameof(callback));
        bool runNow;
        lock (_disposeCallbacks)
        {
            // R29 finding 15: the gate is _completed, not _disposed. An off-thread Dispose
            // latches _disposed immediately and finishes on the next frame, and a callback
            // registered in that window must be drained by CompleteDispose — not run inline
            // ahead of the destroy it is supposed to follow.
            runNow = _completed != 0;
            if (!runNow) _disposeCallbacks.Add(callback);
        }

        // CompleteDispose flips _completed outside this lock before draining, so a registration
        // that raced it would otherwise be added to a list nothing will read again. Re-check
        // and take it back.
        if (!runNow && _completed != 0)
        {
            lock (_disposeCallbacks)
                runNow = _disposeCallbacks.Remove(callback);
        }

        if (runNow)
        {
            Invoke(callback);
            return this;
        }

        // R28 finding 28.5: a disabled handle is not recorded by ServerBeam.Track (R27 finding
        // 4), so RemoveAll and Core uninstall — both promised by this method's own contract —
        // could never reach it, and whatever the plugin tied to a client-side handle leaked.
        // Registering a callback is what makes the handle worth sweeping, so record it now.
        if (!Enabled)
            ServerBeam.TrackForCallbacks(this);
        return this;
    }

    private static void Invoke(Action callback)
    {
        try { callback(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: server beam dispose callback threw: " + e.Message);
        }
    }

    private void RunDisposeCallbacks()
    {
        Action[] callbacks;
        lock (_disposeCallbacks)
        {
            if (_disposeCallbacks.Count == 0) return;
            callbacks = _disposeCallbacks.ToArray();
            _disposeCallbacks.Clear();
        }

        for (int i = 0; i < callbacks.Length; i++)
            Invoke(callbacks[i]);
    }

    /// <summary>
    /// Written once when a plugin disposes a handle from a worker thread. The dispose is not
    /// lost — the ECS destroy is posted to the main thread — but the plugin is doing something
    /// its own contract does not permit, and the entities live one extra frame.
    /// </summary>
    internal const string OffThreadDisposeLog =
        "Effects: server beam Dispose ran off the game main thread; the entity destroy was "
        + "posted to the main thread and the handle stays tracked until it runs. Dispose from "
        + "Plugin.Frame, a command, or EventBus.";

    /// <summary>
    /// R28 finding 28.6. The main-thread guard used to run <b>before</b> the
    /// <see cref="Interlocked.Exchange(ref int, int)"/>, so an off-main-thread dispose threw
    /// with the handle still un-latched: <c>ServerBeam.Uninstall</c> had already cleared
    /// <c>Live</c> and swallowed the throw, so the entities, the handle and every
    /// <see cref="OnDisposed"/> callback were lost silently, and
    /// <c>PluginStaticApiSweep.Run</c> reported <c>beams = 0</c> — which reads as a clean
    /// sweep. The latch is still first, so a second <see cref="Dispose"/> is a no-op and the
    /// guard still throws: an off-thread caller is told.
    ///
    /// <para><b>R29 finding 15 (EV17).</b> That fix ran the bookkeeping in a <c>finally</c>, so
    /// an off-thread dispose latched, ran <see cref="ServerBeam.Forget"/> and fired the
    /// <see cref="OnDisposed"/> callbacks while <see cref="ServerBeam.EnsureMainThread"/> had
    /// already thrown past the destroy loop — leaving replicated <c>NetworkedSequence</c> /
    /// coil entities in the world that nothing owned, that <c>RemoveAll</c> and
    /// <c>Uninstall</c> could no longer see, and that the plugin had just been told were gone.
    /// The ECS work is now <b>marshaled</b> to the main thread through
    /// <see cref="Dev.MainThread.Post"/> and the handle is only forgotten and untracked once
    /// that has actually run, so it stays reachable from <c>Live</c> in the meantime.
    /// <see cref="OnDisposed"/> callbacks therefore always run on the game main thread.</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            // A prior off-thread Dispose latched and posted the destroy. RemoveAll and
            // Uninstall reach this handle through Live and both run on the main thread, so
            // finish the job here rather than returning a misleading no-op.
            if (ServerBeam.OnMainThread()) CompleteDispose();
            return;
        }

        if (!ServerBeam.OnMainThread())
        {
            Plugin.Logger?.LogWarning(OffThreadDisposeLog);
            Dev.MainThread.Post(CompleteDispose);
            // The caller is still told, exactly as R28 28.6 asked. The bookkeeping is not in a
            // finally any more: it belongs to CompleteDispose, on the main thread.
            ServerBeam.EnsureMainThread(nameof(Dispose));
            return;
        }

        CompleteDispose();
    }

    /// <summary>
    /// Destroy the entities, then forget and untrack. Main thread only, and idempotent: the
    /// posted copy and a later <c>RemoveAll</c> / <c>Uninstall</c> can both reach it.
    /// </summary>
    private void CompleteDispose()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        try
        {
            if (Enabled && _runtime != null)
            {
                for (int i = 0; i < _ids.Length; i++)
                {
                    try { _runtime.Destroy(_ids[i]); }
                    catch (Exception e)
                    {
                        Plugin.Logger?.LogWarning("Effects: server beam destroy threw: " + e.Message);
                    }
                }
            }

            if (Enabled)
                Plugin.Logger?.LogInfo("Effects: server beam disposed");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: server beam dispose threw: " + e.Message);
        }
        finally
        {
            ServerBeam.Forget(this);
            RunDisposeCallbacks();
        }
    }

    public BeamPhase Phase => _phase;

    public IServerBeamHandle Steady() => SetPhase(BeamPhase.Steady);

    public IServerBeamHandle Blast() => SetPhase(BeamPhase.Blast);

    /// <summary>
    /// Main-thread poll: rewrite Start/End from tracked Translations, expire at the deadline,
    /// and force a synced change at least every
    /// <see cref="BeamPresets.KeepAliveSeconds"/> seconds. The keep-alive is not optional:
    /// the coil snapshot carries only <c>ProfessorCoilBeam_State_Shared</c>, the server
    /// serializes an entity to a user only when that snapshot changed, and each client
    /// disables the entity <see cref="BeamPresets.NetworkedTimeoutSeconds"/> seconds after
    /// its last snapshot. A motionless beam therefore has to lie about <c>PreviewTime</c>,
    /// alternating between two values that keep the same phase.
    /// </summary>
    internal void Poll(float now)
    {
        if (_disposed != 0 || !Enabled || _runtime == null) return;

        try
        {
            if (_deadline > 0f && now >= _deadline && !_timedOut)
            {
                _timedOut = true;
                Dispose();
                return;
            }

            // A link beam's endpoints are synced entities: nothing to write, no snapshot to
            // keep alive (the NetworkedSequence prefab carries no NetworkedTimeout).
            if (TracksEntitiesNatively) return;

            TryTracked(_startIndex, _startVersion, _startOffset, ref _start);
            TryTracked(_endIndex, _endVersion, _endOffset, ref _end);

            bool moved = !_start.Equals(_writtenStart) || !_end.Equals(_writtenEnd);
            bool keepAlive = now - _lastKeepAlive >= BeamPresets.KeepAliveSeconds;
            if (!moved && !keepAlive) return;

            float preview = -1f;
            if (keepAlive)
            {
                _keepAliveFlip = !_keepAliveFlip;
                preview = BeamPresets.PreviewTimeFor(_phase, _keepAliveFlip);
                _lastKeepAlive = now;
                LogKeepAliveOnce(preview);
            }

            Write(preview);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: server beam poll threw: " + e.Message);
        }
    }

    private IServerBeamHandle SetPhase(BeamPhase phase)
    {
        ServerBeam.EnsureMainThread(nameof(Steady));
        if (_disposed != 0 || !Enabled || _runtime == null) return this;
        if (TracksEntitiesNatively)
        {
            ServerBeam.LogDebug("Effects: server beam link has no Preview/Blast phase; " + phase + " ignored.");
            return this;
        }

        _phase = phase;
        _keepAliveFlip = false;
        float preview = BeamPresets.PreviewTimeFor(phase, alternate: false);
        Write(preview);
        _lastKeepAlive = ServerBeam.Now();
        Plugin.Logger?.LogInfo(
            "Effects: server beam phase=" + phase
            + " previewTime=" + preview.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        return this;
    }

    private void Write(float previewTime)
    {
        if (_runtime == null) return;
        for (int i = 0; i < _ids.Length; i++)
        {
            EffectVec3 lateral = i < _laterals.Length ? _laterals[i] : default;
            var s = Add(_start, lateral);
            var e = Add(_end, lateral);
            try { _runtime.WriteEndpoints(_ids[i], s, e, previewTime); }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning("Effects: server beam tick write threw: " + ex.Message);
            }
        }

        _writtenStart = _start;
        _writtenEnd = _end;
    }

    private void LogKeepAliveOnce(float preview)
    {
        if (_keepAliveLogged) return;
        _keepAliveLogged = true;
        Plugin.Logger?.LogInfo(
            "Effects: server beam keep-alive phase=" + _phase
            + " previewTime=" + preview.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
            + " every " + BeamPresets.KeepAliveSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)
            + "s (client NetworkedTimeout "
            + BeamPresets.NetworkedTimeoutSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s)");
    }

    private bool TryTracked(int index, int version, EffectVec3 offset, ref EffectVec3 stored)
    {
        if (index == 0 || _runtime == null) return false;
        if (!_runtime.TryReadTranslation(index, version, out var pos))
            return false;
        stored = Add(pos, offset);
        return true;
    }

    private static EffectVec3 Add(EffectVec3 a, EffectVec3 b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    private static string Fmt(int index, int version) => index + ":" + version;
}
