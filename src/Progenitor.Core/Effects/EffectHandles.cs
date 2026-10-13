using System;
using System.Threading;
using Progenitor.Dev;
#if !PROGENITOR_TESTS
using Unity.Entities;
using Unity.Mathematics;
#endif

namespace Progenitor.Effects;

/// <summary>Shared handle bookkeeping. Typed subclasses only add family-specific knobs.</summary>
public abstract class EffectHandle : IEffectHandle
{
    private int _disposed;
    private IRuntimeHost? _host;
    private IHostHandle? _hostHandle;

    internal EffectHandle(
        AssetKind kind,
        string name,
        string pluginId,
        string rootName,
        object? owner,
        IRuntimeObject? runtime,
        bool enabled)
    {
        Kind = kind;
        Name = name ?? "";
        PluginId = pluginId ?? EffectOwnership.UnownedId;
        RootName = rootName ?? EffectOwnership.RootName(pluginId);
        Owner = owner;
        RuntimeObject = runtime;
        Enabled = enabled && runtime != null;
        State = Enabled ? EffectHandleState.Borrowed : EffectHandleState.Disabled;
    }

    public AssetKind Kind { get; }

    public string Name { get; }

    public string PluginId { get; }

    public string RootName { get; }

    public bool Enabled { get; private set; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public EffectHandleState State { get; private set; }

    internal object? Owner { get; }

    internal IRuntimeObject? RuntimeObject { get; }

    internal IHostHandle? AttachedHost => _hostHandle;

    /// <summary>
    /// Vanilla sprite color. UI images read <c>Image.color</c>; other families map this onto
    /// their tint path so a caller holding only <see cref="IEffectHandle"/> can still recolor.
    /// </summary>
    public EffectColor Color
    {
        get => RuntimeObject is { Alive: true } obj ? obj.Color : default;
        set
        {
            if (RuntimeObject is { Alive: true } obj)
                obj.Color = value;
        }
    }

    internal IHostHandle AttachHost(IRuntimeHost? runtimeHost, HostKind kind, bool enabled)
    {
        if (IsDisposed)
            return HostHandle.Disabled(kind);

        // Dispose the previous IHostHandle so its NotifyHostDisposed cannot later
        // forget a second live host (R22 #9). Clear the slot first: Dispose of the
        // old handle would otherwise null the new one.
        var previous = _hostHandle;
        _hostHandle = null;
        _host = null;
        if (previous != null)
        {
            try { previous.Dispose(); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Effects: previous host dispose threw: " + e.Message);
            }
        }

        _host = runtimeHost;
        var handle = new HostHandle(kind, runtimeHost, enabled && runtimeHost != null, this);
        _hostHandle = handle;
        if (Enabled && runtimeHost != null)
            State = EffectHandleState.Hosted;
        return handle;
    }

    internal void NotifyHostDisposed(IHostHandle host)
    {
        if (!ReferenceEquals(_hostHandle, host)) return;
        _host = null;
        _hostHandle = null;
        if (!IsDisposed && Enabled)
            State = EffectHandleState.Borrowed;
    }

    internal IRuntimeHost? RuntimeHost => _host;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _hostHandle?.Dispose();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: host dispose threw: " + e.Message);
        }

        _hostHandle = null;
        _host = null;
        try
        {
            RuntimeObject?.Release();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: borrow dispose threw: " + e.Message);
        }

        Enabled = false;
        State = EffectHandleState.Disposed;
        Assets.Forget(this);
        Plugin.Logger?.LogInfo("Effects: disposed " + Kind + " '" + Name + "'");
    }
}

/// <summary>
/// Vanilla sprite on a mod-owned overlay (evidence 78). Color, size, anchored position and
/// canvas sorting order are the knobs that spike wrote.
/// </summary>
public sealed class UiImageHandle : EffectHandle
{
    internal UiImageHandle(string name, string pluginId, string rootName, object? owner, IRuntimeObject? runtime, bool enabled)
        : base(AssetKind.Image, name, pluginId, rootName, owner, runtime, enabled)
    {
    }

    public EffectVec2 Size
    {
        get => RuntimeObject is { Alive: true } obj ? obj.Size : default;
        set
        {
            if (RuntimeObject is { Alive: true } obj)
                obj.Size = value;
        }
    }

    public EffectVec2 Position
    {
        get => RuntimeObject is { Alive: true } obj ? obj.ScreenPosition : default;
        set
        {
            if (RuntimeObject is { Alive: true } obj)
                obj.ScreenPosition = value;
        }
    }

    public int SortingOrder
    {
        get => RuntimeObject is { Alive: true } obj ? obj.SortingOrder : 0;
        set
        {
            if (RuntimeObject is { Alive: true } obj)
                obj.SortingOrder = value;
        }
    }
}

/// <summary>
/// Clone of a named <c>ParticleSystem</c> (evidence 79). Tint writes <c>main.startColor</c> and
/// the renderer material <c>_UnlitColor</c> / <c>_Color</c> / <c>_EmissionColor</c> because HDRP
/// particles often ignore startColor.
/// </summary>
public sealed class ParticleHandle : EffectHandle
{
    internal ParticleHandle(string name, string pluginId, string rootName, object? owner, IRuntimeObject? runtime, bool enabled)
        : base(AssetKind.Particle, name, pluginId, rootName, owner, runtime, enabled)
    {
    }

    public EffectColor Tint
    {
        get => Color;
        set => Color = value;
    }

    public EffectVec3 Position
    {
        get => RuntimeObject is { Alive: true } obj ? obj.WorldPosition : default;
        set
        {
            if (RuntimeObject is { Alive: true } obj)
                obj.WorldPosition = value;
        }
    }

    /// <summary>
    /// Last uniform scale written by <see cref="Scale"/>, or 1 when none has been applied
    /// (or the runtime object is gone). Tests read this; live callers do not need it.
    /// </summary>
    public float UniformScale => RuntimeObject is { Alive: true } obj ? obj.UniformScale : 0f;

    /// <summary>
    /// Uniform scale of the borrowed clone. Always writes the root
    /// <c>transform.localScale</c>. When <c>ParticleSystem.main.scalingMode</c> is not
    /// <c>Hierarchy</c> (Local / Shape), also multiplies <c>main.startSizeMultiplier</c>
    /// because those modes ignore the transform. Chainable before and after
    /// <see cref="Play"/> so a caller can size the clone then start it, or start it then
    /// size it. Disposed handles log once and refuse (evidence 95: unscaled Embers are
    /// faint; the live showcase used Flame 2x + Embers 4x).
    /// </summary>
    public ParticleHandle Scale(float uniform)
    {
        if (IsDisposed)
        {
            Plugin.Logger?.LogInfo("Effects: Scale refused; Particle '" + Name + "' is disposed");
            return this;
        }

        if (!Enabled) return this;
        if (float.IsNaN(uniform) || float.IsInfinity(uniform))
        {
            Plugin.Logger?.LogInfo("Effects: Scale refused; Particle '" + Name + "' scale is not finite");
            return this;
        }

        try
        {
            if (RuntimeObject is { Alive: true } obj)
                obj.UniformScale = uniform;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: Scale threw: " + e.Message);
        }

        return this;
    }

    public void Play() => RuntimeObject?.Play();

    public void Stop() => RuntimeObject?.Stop();
}

/// <summary>
/// Mod-owned HDRP Volume with Vignette / ColorAdjustments overrides (evidence 79). This family
/// is camera-wide: it is not a per-panel overlay. Host still exists so the caller can raise
/// priority; it does not create a canvas. Intensity and smoothness are the Vignette knobs the
/// volume-menu spike wrote; the game's own low-health vignette can still dominate a faint gold.
/// </summary>
public sealed class VolumeHandle : EffectHandle
{
    internal VolumeHandle(string name, string pluginId, string rootName, object? owner, IRuntimeObject? runtime, bool enabled)
        : base(AssetKind.Volume, name, pluginId, rootName, owner, runtime, enabled)
    {
    }

    /// <summary>Vignette color, or ColorAdjustments.colorFilter when Vignette could not be added.</summary>
    public EffectColor Tint
    {
        get => Color;
        set => Color = value;
    }

    /// <summary>Vignette intensity (0–1). No-op on a disabled handle.</summary>
    public float Intensity
    {
        get => RuntimeObject is { Alive: true } obj ? obj.Intensity : 0f;
        set
        {
            if (RuntimeObject is { Alive: true } obj)
                obj.Intensity = value;
        }
    }

    /// <summary>Vignette smoothness (0–1). No-op on a disabled handle.</summary>
    public float Smoothness
    {
        get => RuntimeObject is { Alive: true } obj ? obj.Smoothness : 0f;
        set
        {
            if (RuntimeObject is { Alive: true } obj)
                obj.Smoothness = value;
        }
    }
}

/// <summary>
/// Buff sequence played detached at <see cref="Start"/> via the spike's pose-only
/// <c>PlaySequence(em, prefab, start, identity)</c> (evidence 80, 91: that call produces the
/// hybrid clone and the beam sound). Play waits until both <see cref="Start"/> and
/// <see cref="End"/> have been assigned (evidence 96: the first setter used to play with the
/// other end at origin). After the driver clone exists, start/end markers are written onto
/// <c>SequenceInstance.Target</c> / <c>SecondaryTarget</c> when
/// <see cref="Assets.BindSequencerMarkers"/> is on. Evidence 101: the Cardinal graph
/// ignores those markers (Positions stay origin + driver), so the flag defaults off
/// and the Bootstrap LateFrame slot writes <c>SetPosition(0/1)</c> + <c>Refresh</c>
/// after Presentation / LateUpdate (inspect only: Cardinal pixels come from an owned
/// <c>LineRenderer</c> under the plugin root). Default owned-line look is a thin (0.12 m,
/// <c>widthMultiplier = 1</c>) view-aligned additive unlit line, not the clone's
/// trail-sprite material (evidence 119). Materials created for that line are never
/// assigned to the pooled clone (evidence 125).
/// <see cref="Track(int,int,EffectVec3,EffectVec3)"/> follows entities each late frame.
/// Marker Translation / Rotation / LocalToWorld use
/// generic <c>EntityManager.SetComponentData&lt;T&gt;</c> — <c>Entity.Write&lt;T&gt;</c>
/// is refused for LocalToWorld (TypeSize 4 vs sizeof 64, evidence 99).
/// <see cref="SetEndpoints"/> plays at once. Later Start/End writes re-pose the
/// sequence entity and move the markers (when the flag is on). Tint, Host and Play
/// before the GO exists record the intent; a bounded <c>MainThread.PostDelayed(0)</c>
/// poll (default 30 frames) binds the clone from the sequence entity's
/// <c>HybridLineRenderers</c> managed component (pooled reuse rebinds). The clone is SetActive once after
/// parenting (evidence 99 left it <c>activeSelf=False</c>).
/// </summary>
public sealed class SequenceEffectHandle : EffectHandle
{
    /// <summary>Frames the handle will poll for a late hybrid driver. Tests may lower this via the runtime.</summary>
    public const int DefaultDriverPollFrames = 30;

    private int _polls;
    private int _polling;
    private bool _landedLogged;
    private bool _startSet;
    private bool _endSet;

    internal SequenceEffectHandle(string name, string pluginId, string rootName, object? owner, IRuntimeObject? runtime, bool enabled)
        : base(AssetKind.Sequence, name, pluginId, rootName, owner, runtime, enabled)
    {
    }

    public EffectColor Tint
    {
        get => Color;
        set
        {
            Color = value;
            // PlaySequence has not run yet (Sample sets tint then Host). Arming here would
            // burn the poll budget before the clone exists. After bind, Color writes the
            // driver directly; do not re-arm the poll (R24 #7).
            if (!HasDriver && (RuntimeObject?.SequenceEntityId ?? 0) != 0)
                ArmDriverPoll();
        }
    }

    internal int SequenceEntityId => RuntimeObject?.SequenceEntityId ?? 0;

    internal int EndMarkerId => RuntimeObject?.EndMarkerId ?? 0;

    internal bool HasDriver => RuntimeObject?.HasDriver ?? false;

    internal bool HasOwnedLine => RuntimeObject?.HasOwnedLine ?? false;

    /// <summary>Meters on the owned line after the last <see cref="LineWidth"/> write (tests).</summary>
    internal float OwnedLineWidth => RuntimeObject?.LineWidth ?? 0f;

    /// <summary>True after <see cref="UsePrefabLineMaterial"/> (tests).</summary>
    internal bool UsesPrefabLineMaterial => RuntimeObject?.UsePrefabLineMaterial ?? false;

    /// <summary>Shader name last assigned to the owned line (tests).</summary>
    internal string OwnedLineMaterialName => RuntimeObject?.OwnedLineMaterialName ?? "";

    /// <summary>
    /// Preset last applied by <see cref="ApplyPreset"/> / <see cref="Assets.Beam"/>.
    /// GameRed falls back to Thin when no castle sequence GUID resolved.
    /// </summary>
    public BeamStyle Style => RuntimeObject?.BeamStyle ?? BeamStyle.Thin;

    /// <summary>True while the halo <c>LineRenderer</c> exists (tests).</summary>
    internal bool HasGlowLine => RuntimeObject?.HasGlowLine ?? false;

    /// <summary>Halo width in meters after the last <see cref="Glow"/> / preset write (tests).</summary>
    internal float OwnedGlowWidth => RuntimeObject?.GlowWidth ?? 0f;

    /// <summary>Draw-path verdict string (tests).</summary>
    internal string DrawPath => RuntimeObject?.DrawPath ?? "";

    /// <summary>
    /// Vanilla beam prefab whose copy draws this beam (evidence 130), or empty when the
    /// owned unlit line does. The copy belongs to this handle; the sequencer's pooled clone
    /// is never written (R26 finding 3).
    /// </summary>
    public string GameLinePrefab => RuntimeObject?.GameLinePrefabName ?? "";

    /// <summary>
    /// True once the sequencer recycled the pooled clone. The body is hidden at that moment
    /// so a tracked beam cannot freeze in the world (R26 finding 8); Dispose still cleans up.
    /// </summary>
    public bool Recycled => RuntimeObject?.Recycled ?? false;

    /// <summary>False after a recycle hid the body, or after dispose (tests).</summary>
    internal bool LineVisible => RuntimeObject?.LineVisible ?? false;

    /// <summary>Copied game-material color before tint (tests).</summary>
    internal EffectColor CopiedMaterialColor => RuntimeObject?.CopiedMaterialColor ?? default;

    /// <summary>Last color written onto the owned line after tint math (tests).</summary>
    internal EffectColor LastTintedColor => RuntimeObject?.LastTintedColor ?? default;

    /// <summary>Late-frame <c>LineRendererPositionElement</c> writes (tests).</summary>
    internal int LineRendererBufferWrites => RuntimeObject?.LineRendererBufferWrites ?? 0;

    internal string DriverName => RuntimeObject?.DriverName ?? "";

    internal bool TintAppliedToDriver => RuntimeObject?.TintAppliedToDriver ?? false;

    internal bool DriverActive => RuntimeObject?.DriverActive ?? false;

    internal string LastTransformWritePath => RuntimeObject?.LastTransformWritePath ?? "";

    internal int TransformWriteWarnings => RuntimeObject?.TransformWriteWarnings ?? 0;

    internal bool HybridLastWrite => RuntimeObject?.HybridLastWrite ?? false;

    internal int LateWriteCount => RuntimeObject?.LateWriteCount ?? 0;

    internal int LastLateWriteFrame => RuntimeObject?.LastLateWriteFrame ?? 0;

    internal int SequencerOverwriteCount => RuntimeObject?.SequencerOverwriteCount ?? 0;

    internal string LateWriteVerdict => RuntimeObject?.LateWriteVerdict ?? "";

    /// <summary>True while the bounded driver poll is scheduled. Tests assert it drops after bind.</summary>
    internal bool IsPolling => Volatile.Read(ref _polling) != 0;

    /// <summary>
    /// Play the resolved sequence at <see cref="Start"/> (identity rotation). Idempotent: a
    /// second call does not spawn another sequence. Start/End wait for both ends before
    /// calling this; <see cref="SetEndpoints"/> and an explicit caller call it immediately.
    /// </summary>
    public void Play()
    {
        if (IsDisposed || !Enabled) return;
        try { RuntimeObject?.Play(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: sequence Play threw: " + e.Message);
        }

        DriveIfPlayed();
        ArmDriverPoll();
    }

    /// <summary>
    /// Move both world-space ends and play immediately (both ends are known). PlaySequence
    /// stays the spike pose at <paramref name="start"/>; later writes re-pose that entity
    /// and move the markers so the Sample can walk the far end without another play call.
    /// Fixed pose: clears any <see cref="Track(int,int,EffectVec3,EffectVec3)"/> follow.
    /// </summary>
    public void SetEndpoints(EffectVec3 start, EffectVec3 end)
    {
        if (IsDisposed || !Enabled) return;
        _startSet = true;
        _endSet = true;
        if (RuntimeObject is { Alive: true } obj)
        {
            obj.ClearEndpointTracking();
            obj.BeamStart = start;
            obj.BeamEnd = end;
        }

        Play();
    }

    /// <summary>
    /// Constant owned-line width in meters (no width curve). Default
    /// <see cref="SequenceLateDriver.OwnedLineDefaultWidth"/>. Chainable before or after
    /// <see cref="Play"/>. Disposed handles refuse.
    /// </summary>
    public SequenceEffectHandle LineWidth(float metres)
    {
        if (IsDisposed)
        {
            Plugin.Logger?.LogInfo("Effects: LineWidth refused; Sequence '" + Name + "' is disposed");
            return this;
        }

        if (!Enabled) return this;
        if (float.IsNaN(metres) || float.IsInfinity(metres) || metres < 0f)
        {
            Plugin.Logger?.LogInfo("Effects: LineWidth refused; Sequence '" + Name + "' width is not a finite >= 0");
            return this;
        }

        try
        {
            if (RuntimeObject is { Alive: true } obj)
                obj.LineWidth = metres;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: LineWidth threw: " + e.Message);
        }

        return this;
    }

    /// <summary>
    /// Tint the owned line (and the borrowed clone, when it lands). Same write as
    /// <see cref="Tint"/>; this name is the chainable form. Default when unset is
    /// <see cref="SequenceLateDriver.OwnedLineDefaultTint"/>.
    /// </summary>
    public SequenceEffectHandle LineColor(EffectColor color)
    {
        Tint = color;
        return this;
    }

    /// <summary>
    /// Copy the pooled clone's material, width and gradient onto the owned line once
    /// the driver lands. Opt-in: the default is a thin additive unlit line because
    /// Cardinal's trail-sprite shader draws a folded sheet on a two-point
    /// <c>LineRenderer</c> (evidence 119).
    /// </summary>
    public SequenceEffectHandle UsePrefabLineMaterial()
    {
        if (IsDisposed || !Enabled) return this;
        try
        {
            if (RuntimeObject is { Alive: true } obj)
                obj.UsePrefabLineMaterial = true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: UsePrefabLineMaterial threw: " + e.Message);
        }

        return this;
    }

    /// <summary>
    /// Apply a <see cref="BeamStyle"/>: Thin is the unlit core. GameRed / GameFeed copy a
    /// vanilla LineRenderer-bodied beam prefab (evidence 130) and drive that copy, so the
    /// game's own material draws the beam; when none is loaded they fall back to copying the
    /// clone's material onto the owned line (skipping trail sprites) plus a glow halo.
    /// GameBlue stays on the owned line — Cardinal's body is a particle graph. Called from
    /// <see cref="Assets.Beam"/>. Chainable.
    /// </summary>
    public SequenceEffectHandle ApplyPreset(BeamStyle style, string? source = null)
    {
        if (IsDisposed)
        {
            Plugin.Logger?.LogInfo("Effects: ApplyPreset refused; Sequence '" + Name + "' is disposed");
            return this;
        }

        if (!Enabled) return this;
        try
        {
            if (RuntimeObject is { Alive: true } obj)
                obj.ApplyBeamPreset(style);
            if (!string.IsNullOrEmpty(source))
                Plugin.Logger?.LogInfo("Effects: Beam(" + style + ") borrow " + Name + " source=" + source);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: ApplyPreset threw: " + e.Message);
        }

        return this;
    }

    /// <summary>
    /// Second owned line, wider and more transparent than the core, so a thin beam
    /// reads as core + soft halo without a game glow material. Width 0 removes it.
    /// Chainable. Disposed handles refuse.
    /// </summary>
    public SequenceEffectHandle Glow(float width, float alpha = BeamCatalog.DefaultGlowAlpha)
    {
        if (IsDisposed)
        {
            Plugin.Logger?.LogInfo("Effects: Glow refused; Sequence '" + Name + "' is disposed");
            return this;
        }

        if (!Enabled) return this;
        if (float.IsNaN(width) || float.IsInfinity(width) || width < 0f
            || float.IsNaN(alpha) || float.IsInfinity(alpha))
        {
            Plugin.Logger?.LogInfo("Effects: Glow refused; Sequence '" + Name + "' glow is not finite >= 0");
            return this;
        }

        try
        {
            if (RuntimeObject is { Alive: true } obj)
            {
                obj.GlowAlpha = alpha;
                obj.GlowWidth = width;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: Glow threw: " + e.Message);
        }

        return this;
    }

    /// <summary>
    /// Follow two entities by index each late frame (game-free for tests). A missing
    /// entity freezes that end at its last pose. Chainable; plays once both ids are set.
    /// </summary>
    internal SequenceEffectHandle Track(int startId, int endId, EffectVec3 startOffset = default, EffectVec3 endOffset = default)
    {
        if (IsDisposed || !Enabled) return this;
        try
        {
            if (RuntimeObject is { Alive: true } obj)
            {
                obj.TrackStartEntity(startId, 1, startOffset);
                obj.TrackEndEntity(endId, 1, endOffset);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: Track threw: " + e.Message);
        }

        _startSet = true;
        _endSet = true;
        Play();
        return this;
    }

    /// <summary>
    /// Follow <paramref name="startId"/>; the far end is a fixed world point.
    /// Game-free for tests. Chainable.
    /// </summary>
    internal SequenceEffectHandle Track(int startId, EffectVec3 end, EffectVec3 startOffset = default)
    {
        if (IsDisposed || !Enabled) return this;
        try
        {
            if (RuntimeObject is { Alive: true } obj)
            {
                obj.TrackStartEntity(startId, 1, startOffset);
                obj.TrackEndWorld(end);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: Track threw: " + e.Message);
        }

        _startSet = true;
        _endSet = true;
        Play();
        return this;
    }

#if !PROGENITOR_TESTS
    /// <summary>
    /// Follow <paramref name="start"/> and <paramref name="end"/> each late frame
    /// (client <c>Translation</c> or <c>LocalToWorld.Position</c>, cached
    /// <c>ComponentType</c>, no per-frame allocation). Offsets are world-space.
    /// A destroyed entity freezes that end and logs once. Chainable before or after
    /// <see cref="Play"/>.
    /// </summary>
    public SequenceEffectHandle Track(Entity start, Entity end, float3 startOffset = default, float3 endOffset = default)
    {
        if (IsDisposed || !Enabled) return this;
        try
        {
            if (RuntimeObject is { Alive: true } obj)
            {
                obj.TrackStartEntity(start.Index, start.Version, new EffectVec3(startOffset.x, startOffset.y, startOffset.z));
                obj.TrackEndEntity(end.Index, end.Version, new EffectVec3(endOffset.x, endOffset.y, endOffset.z));
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: Track threw: " + e.Message);
        }

        _startSet = true;
        _endSet = true;
        Play();
        return this;
    }

    /// <summary>
    /// Follow <paramref name="start"/>; the far end is a fixed world point.
    /// <paramref name="startOffset"/> is applied to the entity pose each frame.
    /// </summary>
    public SequenceEffectHandle Track(Entity start, float3 end, float3 startOffset = default)
    {
        if (IsDisposed || !Enabled) return this;
        try
        {
            if (RuntimeObject is { Alive: true } obj)
            {
                obj.TrackStartEntity(start.Index, start.Version, new EffectVec3(startOffset.x, startOffset.y, startOffset.z));
                obj.TrackEndWorld(new EffectVec3(end.x, end.y, end.z));
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: Track threw: " + e.Message);
        }

        _startSet = true;
        _endSet = true;
        Play();
        return this;
    }
#endif

    private void TryPlayWhenReady()
    {
        if (_startSet && _endSet)
            Play();
    }

    private void DriveIfPlayed()
    {
        if (RuntimeObject is not { Alive: true } obj) return;
        if ((obj.SequenceEntityId == 0) && !obj.HasDriver) return;
        try { obj.DriveEndpoints(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: sequence endpoint drive threw: " + e.Message);
        }
    }

    /// <summary>
    /// Start the bounded late-driver poll if one is not already running. Host and Play call
    /// this after <c>PlaySequence</c>; Tint calls it so a color set before the GO exists is
    /// applied when it lands. After the GO binds, the same tick keeps driving endpoints.
    /// </summary>
    internal void ArmDriverPoll()
    {
        if (IsDisposed || !Enabled) return;
        if (Interlocked.CompareExchange(ref _polling, 1, 0) != 0) return;
        _polls = 0;
        try
        {
            MainThread.PostDelayed(0, PollOnce, Owner);
        }
        catch (Exception e)
        {
            _polling = 0;
            Plugin.Logger?.LogWarning("Effects: sequence driver poll PostDelayed threw: " + e.Message);
        }
    }

    private void PollOnce()
    {
        if (IsDisposed || !Enabled)
        {
            Volatile.Write(ref _polling, 0);
            return;
        }

        try
        {
            RuntimeHost?.SyncScreenSize();
            var obj = RuntimeObject;
            bool had = obj != null && obj.HasDriver;
            if (obj != null && (had || obj.TryBindDriver()))
            {
                RuntimeHost?.AttachDriver(obj);
                try { obj.DriveEndpoints(); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Effects: sequence endpoint drive threw: " + e.Message);
                }

                if (!_landedLogged && !had)
                {
                    _landedLogged = true;
                    Plugin.Logger?.LogInfo(
                        "Effects: sequence driver '" + obj.DriverName + "' landed after " + (_polls + 1) + " frame(s)");
                }

                // R24 #7: first successful bind stops the poll. Per-frame pose is the
                // late driver only. Do not PostDelayed reschedule.
                Volatile.Write(ref _polling, 0);
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: sequence driver poll threw: " + e.Message);
        }

        _polls++;
        int max = DriverPollLimit();
        if (_polls >= max)
        {
            Plugin.Logger?.LogInfo(
                "Effects: sequence driver GO not found after " + max + " frames (sequence entity has no HybridLineRenderers).");
            Volatile.Write(ref _polling, 0);
            return;
        }

        try
        {
            MainThread.PostDelayed(0, PollOnce, Owner);
        }
        catch (Exception e)
        {
            Volatile.Write(ref _polling, 0);
            Plugin.Logger?.LogWarning("Effects: sequence driver poll reschedule threw: " + e.Message);
        }
    }

    private int DriverPollLimit()
    {
        try
        {
            int n = Assets.DriverPollFrames;
            return n > 0 ? n : DefaultDriverPollFrames;
        }
        catch
        {
            return DefaultDriverPollFrames;
        }
    }

    /// <summary>
    /// Near end (PlaySequence pose). Alias of <see cref="Start"/> so older callers that
    /// set a single pose still compile; a zero <see cref="End"/> is a zero-length beam until
    /// <see cref="SetEndpoints"/> runs.
    /// </summary>
    public EffectVec3 Position
    {
        get => Start;
        set => Start = value;
    }

    /// <summary>Near world-space end of the beam and the PlaySequence pose.</summary>
    public EffectVec3 Start
    {
        get => RuntimeObject is { Alive: true } obj ? obj.BeamStart : default;
        set
        {
            _startSet = true;
            if (RuntimeObject is { Alive: true } obj)
            {
                obj.ClearEndpointTracking();
                obj.BeamStart = value;
            }

            TryPlayWhenReady();
        }
    }

    /// <summary>
    /// Far world-space end. A client-local marker entity at this pose is
    /// <c>SequenceInstance.SecondaryTarget</c> once the driver clone exists when
    /// <see cref="Assets.BindSequencerMarkers"/> is on. Hybrid <c>SetPosition(1, …)</c>
    /// in the LateFrame slot is the drawing path (evidence 101). Assignment after play
    /// moves the stored end (and the marker, when the flag is on) without another
    /// <c>PlaySequence</c>.
    /// </summary>
    public EffectVec3 End
    {
        get => RuntimeObject is { Alive: true } obj ? obj.BeamEnd : default;
        set
        {
            _endSet = true;
            if (RuntimeObject is { Alive: true } obj)
            {
                obj.ClearEndpointTracking();
                obj.BeamEnd = value;
            }

            TryPlayWhenReady();
        }
    }
}

/// <summary>Catalog-only or unknown kind: still an <see cref="IEffectHandle"/> so Dispose is uniform.</summary>
internal sealed class DisabledEffectHandle : EffectHandle
{
    internal DisabledEffectHandle(AssetKind kind, string name, string pluginId, string rootName, object? owner)
        : base(kind, name, pluginId, rootName, owner, runtime: null, enabled: false)
    {
    }
}

internal sealed class HostHandle : IHostHandle
{
    private int _disposed;
    private readonly IRuntimeHost? _runtime;
    private readonly EffectHandle? _owner;

    internal HostHandle(HostKind kind, IRuntimeHost? runtime, bool enabled, EffectHandle? owner)
    {
        Kind = kind;
        _runtime = runtime;
        Enabled = enabled;
        _owner = owner;
    }

    internal static HostHandle Disabled(HostKind kind) => new(kind, null, enabled: false, owner: null);

    public HostKind Kind { get; }

    public bool Enabled { get; private set; }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Unused layer the RT host parked the driver on, or -1 when layers were not moved.</summary>
    public int HostedLayer => _runtime?.HostedLayer ?? -1;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _runtime?.Release();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: host surface dispose threw: " + e.Message);
        }

        Enabled = false;
        _owner?.NotifyHostDisposed(this);
    }
}
