using System.Collections.Generic;

namespace Progenitor.Effects;

/// <summary>
/// Unity (or a test fake) behind <see cref="Assets"/>. Tests inject a fake so they never
/// need the game; the client uses <c>UnityEffectRuntime</c>. Every method is expected to
/// swallow Unity exceptions and return a disabled/empty result rather than throw into a hook.
/// Sequence play (pose-only; SequenceInstance marker bind is behind
/// <see cref="Assets.BindSequencerMarkers"/>, default off — evidence 101: the Cardinal
/// graph ignores the marker), generic SetComponentData for marker transforms, hybrid
/// SetPosition in the Bootstrap LateFrame slot),
/// RT layer pick, RT size, and the late-driver poll are on this interface (and on
/// <see cref="IRuntimeObject"/> / <see cref="IRuntimeHost"/>) so the test suite can fail
/// those paths without Unity.
/// </summary>
internal interface IEffectRuntime
{
    /// <summary>False in a VRisingServer process. Server Borrow/Host/Find must not touch Unity.</summary>
    bool IsClient { get; }

    IReadOnlyList<string> Find(AssetKind kind, string? nameContains);

    /// <summary>
    /// Clone or construct the named asset under <paramref name="rootName"/>. Null means the
    /// source was missing (caller still returns a disabled handle). For <see cref="AssetKind.Sequence"/>
    /// null also means the buff has no <c>SequenceGUID</c> / <c>BuffSequence</c> row — never play
    /// the buff itself as a sequence.
    /// </summary>
    IRuntimeObject? Borrow(AssetKind kind, string name, string rootName);

    IRuntimeHost? Host(IRuntimeObject borrowed, HostKind kind, string rootName);

    /// <summary>
    /// Plugin unload: destroy the GameObject whose name equals <paramref name="rootName"/>
    /// exactly (Unity destroys children with it). Not a prefix match — a GUID that extends
    /// another plugin's GUID must not lose its root.
    /// </summary>
    int DestroyRoot(string rootName);

    /// <summary>
    /// Spike step-0 / Core Uninstall: disable then destroy every object whose name starts with
    /// <paramref name="prefix"/>. Returns how many roots were released.
    /// </summary>
    int DestroyByPrefix(string prefix);

    /// <summary>
    /// Unused unnamed Unity layer in 8..31 with no renderer on it. -1 when none remain.
    /// Reserved per plugin root so two Host calls from the same plugin reuse the layer and a
    /// second plugin does not steal it.
    /// </summary>
    int ReserveLayer(string rootName);

    /// <summary>
    /// Screen size the RT host should match. Tests mutate this to force a resize; the Unity
    /// runtime reads <c>Screen.width/height</c>.
    /// </summary>
    EffectVec2 ScreenSize { get; }

    /// <summary>
    /// Frames a sequence handle will <c>MainThread.PostDelayed(0)</c> poll for a late driver.
    /// Default 30. Tests lower this so a missing-driver give-up is cheap.
    /// </summary>
    int MaxDriverPollFrames { get; }

    /// <summary>
    /// Why the last <see cref="Borrow"/> returned null, for the one log line Assets writes.
    /// Null means the generic "no Kind named X" line.
    /// </summary>
    string? LastMissReason { get; }

    /// <summary>
    /// Sequence name (hash or spawnable) to borrow for <paramref name="style"/>, or null
    /// when that preset is not on this client. <paramref name="source"/> names the dump
    /// field that supplied it (tests use "test"). Thin and GameBlue are the Cardinal
    /// buff; GameRed walks coffin / invalid-placement / arena-flag sequence GUIDs.
    /// </summary>
    string? ResolveBeamPreset(BeamStyle style, out string source);

    /// <summary>
    /// Name of the vanilla two-point beam prefab this client can copy for
    /// <paramref name="style"/>, or null when none of
    /// <see cref="BeamCatalog.GameLineBeamsFor"/> is loaded. The copy is instantiated by
    /// the handle, so writing its LineRenderer and material is legal
    /// (<see cref="CloneWritePolicy"/>); the pooled sequencer clone is never written.
    /// </summary>
    string? ResolveBeamLinePrefab(BeamStyle style, out string source);
}

/// <summary>One borrowed Unity object (or a fake). Handle property writes go through here.</summary>
internal interface IRuntimeObject
{
    AssetKind Kind { get; }

    string Name { get; }

    bool Alive { get; }

    EffectColor Color { get; set; }

    EffectVec2 Size { get; set; }

    EffectVec2 ScreenPosition { get; set; }

    EffectVec3 WorldPosition { get; set; }

    /// <summary>
    /// Uniform scale of a borrowed particle clone. Writes the root transform and, when
    /// <c>ParticleSystem.main.scalingMode</c> is not Hierarchy, the main module's
    /// <c>startSizeMultiplier</c> (Local / Shape ignore transform scale). Other families
    /// store the value and do nothing else. Default 1.
    /// </summary>
    float UniformScale { get; set; }

    /// <summary>
    /// Near end of a two-point sequence and the pose passed to
    /// <c>PlaySequence(em, prefab, start, identity)</c>. A later write re-poses the
    /// play entity. Particles ignore this.
    /// </summary>
    EffectVec3 BeamStart { get; set; }

    /// <summary>
    /// Far end of a two-point sequence. PlaySequence itself is pose-only at
    /// <see cref="BeamStart"/> (the spike call that produces the driver and the sound);
    /// this end is a marker entity on <c>SequenceInstance.SecondaryTarget</c> bound after
    /// the driver clone exists.
    /// </summary>
    EffectVec3 BeamEnd { get; set; }

    /// <summary>HDRP Vignette intensity (0–1). Other families ignore this.</summary>
    float Intensity { get; set; }

    /// <summary>HDRP Vignette smoothness (0–1). Other families ignore this.</summary>
    float Smoothness { get; set; }

    int SortingOrder { get; set; }

    /// <summary>
    /// True once the hybrid driver GO exists. Sequence clones appear one or more frames after
    /// <c>PlaySequence</c>; Tint/Host before that record intent instead of failing.
    /// </summary>
    bool HasDriver { get; }

    /// <summary>
    /// True after the handle created its own world-space <c>LineRenderer</c> (the default
    /// Cardinal draw path: the sequencer hybrid line is not the visible body).
    /// </summary>
    bool HasOwnedLine { get; }

    /// <summary>
    /// Meters written onto the owned <c>LineRenderer</c>: <c>startWidth = endWidth</c>,
    /// <c>widthMultiplier = 1</c>, constant width curve. Default
    /// <see cref="SequenceLateDriver.OwnedLineDefaultWidth"/>.
    /// </summary>
    float LineWidth { get; set; }

    /// <summary>
    /// When true, copy the pooled clone's material and width once it lands (the
    /// pre-EV8 look). Default false: a thin additive unlit line.
    /// </summary>
    bool UsePrefabLineMaterial { get; set; }

    /// <summary>Preset last applied by <see cref="Assets.Beam"/> / <see cref="SequenceEffectHandle.ApplyPreset"/>.</summary>
    BeamStyle BeamStyle { get; }

    /// <summary>True after the handle created the wider, more transparent halo line.</summary>
    bool HasGlowLine { get; }

    /// <summary>
    /// Name of the vanilla beam prefab this handle copied for its body, or empty when the
    /// owned unlit line draws it. The copy belongs to us; the pooled sequencer clone never does.
    /// </summary>
    string GameLinePrefabName { get; }

    /// <summary>
    /// True once the sequencer took the pooled clone back (R26 finding 8). The owned line
    /// is hidden at that moment so a tracked beam cannot freeze in the world; the handle
    /// still disposes normally.
    /// </summary>
    bool Recycled { get; }

    /// <summary>False once <see cref="Recycled"/> hid the body, or after release.</summary>
    bool LineVisible { get; }

    /// <summary>
    /// Halo width in meters. 0 destroys the glow line. GameRed/GameBlue default
    /// <see cref="BeamCatalog.DefaultGlowWidth"/> unless the copied material already glows.
    /// </summary>
    float GlowWidth { get; set; }

    /// <summary>Halo alpha (0–1) multiplied onto the core color.</summary>
    float GlowAlpha { get; set; }

    /// <summary>
    /// <see cref="SequenceLateDriver.VerdictOwnedLine"/>,
    /// <see cref="SequenceLateDriver.VerdictOwnedGameMaterial"/> or
    /// <see cref="SequenceLateDriver.VerdictGameLineBuffer"/> once known.
    /// </summary>
    string DrawPath { get; }

    /// <summary>Material color captured when the game line was copied; default if none.</summary>
    EffectColor CopiedMaterialColor { get; }

    /// <summary>Last color written onto the owned line after tint math.</summary>
    EffectColor LastTintedColor { get; }

    /// <summary>Late-frame writes of <c>LineRendererPositionElement</c> on the sequence entity.</summary>
    int LineRendererBufferWrites { get; }

    /// <summary>Apply a <see cref="BeamStyle"/> (copy policy, default tint, glow). Chain from <see cref="Assets.Beam"/>.</summary>
    void ApplyBeamPreset(BeamStyle style);

    /// <summary>
    /// Shader name last assigned to the owned line (<c>HDRP/Unlit</c> on the default
    /// path, the prefab shader when opted in, empty before create).
    /// </summary>
    string OwnedLineMaterialName { get; }

    /// <summary>
    /// Follow <paramref name="index"/>:<paramref name="version"/> for the near end
    /// each late frame, plus <paramref name="offset"/>. Zero index clears tracking
    /// for that end.
    /// </summary>
    void TrackStartEntity(int index, int version, EffectVec3 offset);

    /// <summary>Follow an entity for the far end. Same contract as <see cref="TrackStartEntity"/>.</summary>
    void TrackEndEntity(int index, int version, EffectVec3 offset);

    /// <summary>Far end is a fixed world point; the near end may still track.</summary>
    void TrackEndWorld(EffectVec3 world);

    /// <summary>Stop following entities; later Start/End writes are the pose.</summary>
    void ClearEndpointTracking();

    /// <summary>Scene name of the hybrid driver once found; empty while polling.</summary>
    string DriverName { get; }

    /// <summary>
    /// <c>SequenceState.Id</c> index after play (0 when none). Tests assert Dispose destroys it.
    /// </summary>
    int SequenceEntityId { get; }

    /// <summary>
    /// Far-end marker entity index (0 when markers are off or none was created).
    /// Pose-only <c>PlaySequence</c> still runs (evidence 91). Marker bind is behind
    /// <see cref="Assets.BindSequencerMarkers"/> (default off: evidence 101, the
    /// Cardinal graph ignores Target/SecondaryTarget).
    /// </summary>
    int EndMarkerId { get; }

    /// <summary>True after a tint write actually reached the driver GO (not only the pending field).</summary>
    bool TintAppliedToDriver { get; }

    /// <summary>
    /// One poll for a late sequence driver. Returns true when the GO is now bound. No-op until
    /// PlaySequence has run.
    /// </summary>
    bool TryBindDriver();

    /// <summary>
    /// True after the hybrid driver GO is <c>activeSelf</c> (we SetActive once after
    /// parenting; evidence 99 left the clone inactive). False while polling or after release.
    /// </summary>
    bool DriverActive { get; }

    /// <summary>
    /// How marker Translation / Rotation / LocalToWorld are written. Live path is
    /// <see cref="TransformWritePath.GenericSetComponentData"/>; empty before play.
    /// </summary>
    string LastTransformWritePath { get; }

    /// <summary>
    /// How many transform-write failures this handle has logged (capped at one per
    /// component: Translation, Rotation, LocalToWorld).
    /// </summary>
    int TransformWriteWarnings { get; }

    /// <summary>
    /// True after the LateFrame slot wrote hybrid <c>SetPosition</c> for a bound driver.
    /// </summary>
    bool HybridLastWrite { get; }

    /// <summary>How many LateFrame hybrid writes this object has done while bound.</summary>
    int LateWriteCount { get; }

    /// <summary>
    /// <c>Time.frameCount</c> of the most recent late write (0 before the first).
    /// Exposed so a live inspect log can prove the slot ran after bind.
    /// </summary>
    int LastLateWriteFrame { get; }

    /// <summary>
    /// Frames whose Positions at the start of the late slot were not [start,end]
    /// (the sequencer overwrote before us).
    /// </summary>
    int SequencerOverwriteCount { get; }

    /// <summary>
    /// <see cref="SequenceLateDriver.VerdictLateFrame"/> or
    /// <see cref="SequenceLateDriver.VerdictSequencer"/> once enough late writes have
    /// run (or on dispose). Empty before the verdict.
    /// </summary>
    string LateWriteVerdict { get; }

    /// <summary>
    /// Move the start/end marker transforms (generic <c>SetComponentData</c>) and re-pose
    /// the play entity. Hybrid <c>SetPosition</c> runs from <see cref="DriveLateWrite"/>
    /// in the Bootstrap LateFrame slot, after Presentation / LateUpdate.
    /// </summary>
    void DriveEndpoints();

    /// <summary>
    /// Last write of the frame: owned <c>LineRenderer.SetPosition(int, Vector3)</c> from
    /// two cached vectors, then (while bound) hybrid <c>SetPosition</c> + <c>Refresh</c>
    /// and a sequencer-overwrite count. Steady state allocates nothing — inspect strings
    /// and the Positions list wrapper run once per handle (R25 #12). No-op when disposed.
    /// </summary>
    void DriveLateWrite();

    void Play();

    void Stop();

    /// <summary>Stop, disable, DestroyImmediate this object. Idempotent. Sequence play entity + markers go too.</summary>
    void Release();
}

/// <summary>Overlay canvas or RT camera + RawImage. Independent of the world clone.</summary>
internal interface IRuntimeHost
{
    HostKind Kind { get; }

    bool Alive { get; }

    /// <summary>
    /// Unused Unity layer the host moved the borrowed GO (and children) onto, or -1 when
    /// the host did not change layers (overlay, volume, or the camera-at-pose fallback).
    /// Dispose restores the previous layer on each moved object.
    /// </summary>
    int HostedLayer { get; }

    /// <summary>RT width last created or resized. 0 for overlay / volume.</summary>
    int RtWidth { get; }

    /// <summary>RT height last created or resized. 0 for overlay / volume.</summary>
    int RtHeight { get; }

    /// <summary>
    /// Recreate the RT when <see cref="IEffectRuntime.ScreenSize"/> (or Unity <c>Screen</c>)
    /// differs from the last create. No-op for overlay.
    /// </summary>
    void SyncScreenSize();

    /// <summary>
    /// Late-driver path: move the now-found sequence GO onto <see cref="HostedLayer"/> and
    /// remember the previous layers so Dispose can restore them. No-op if already moved or
    /// the layer is -1.
    /// </summary>
    void AttachDriver(IRuntimeObject obj);

    void Release();
}

/// <summary>Default when no runtime is injected: every call is a no-op, reported as server-side.</summary>
internal sealed class NullEffectRuntime : IEffectRuntime
{
    internal static readonly NullEffectRuntime Instance = new();

    public bool IsClient => false;

    public EffectVec2 ScreenSize => default;

    public int MaxDriverPollFrames => 30;

    public string? LastMissReason => null;

    public string? ResolveBeamPreset(BeamStyle style, out string source)
    {
        source = "";
        return null;
    }

    public string? ResolveBeamLinePrefab(BeamStyle style, out string source)
    {
        source = "";
        return null;
    }

    public IReadOnlyList<string> Find(AssetKind kind, string? nameContains) => System.Array.Empty<string>();

    public IRuntimeObject? Borrow(AssetKind kind, string name, string rootName) => null;

    public IRuntimeHost? Host(IRuntimeObject borrowed, HostKind kind, string rootName) => null;

    public int DestroyRoot(string rootName) => 0;

    public int DestroyByPrefix(string prefix) => 0;

    public int ReserveLayer(string rootName) => -1;
}
