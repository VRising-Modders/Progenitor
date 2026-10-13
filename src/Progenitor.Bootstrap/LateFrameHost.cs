using System;
using UnityEngine;

namespace Progenitor.Bootstrap;

/// <summary>
/// Process-lifetime late-frame pump for Core's LateFrame slot. Unity never holds a Core
/// delegate: this type's methods are the ConvertDelegate targets, and they invoke
/// <see cref="ProcessHost.InvokeLateFrame"/> with the same null-runtime guard as
/// <c>CoreTick</c>.
/// </summary>
/// <remarks>
/// Last game writer of <c>HybridLineRendererComponent.Positions</c> is
/// <c>ParticleLifetimeSystem.UpdateLineRendererPositionElement_Job</c>
/// (<c>[UpdateInGroup(typeof(PresentationSystemGroup))]</c>). PresentationSystemGroup is in
/// PreLateUpdate, so FrameTicker LateUpdate already runs after it. <c>HybridLineRendererComponent</c>
/// has no LateUpdate / OnWillRenderObject (its methods are Awake, SetPosition,
/// Refresh, CalculateStandard only).
/// HDRP (<c>HDRenderPipeline</c>) drives cameras through
/// <c>RenderPipeline.BeginCameraRendering</c> and does not call
/// <c>Camera.FireOnPreCull</c>. Subscribing <c>Camera.onPreCull</c>
/// therefore succeeds and then never invokes. <c>add_beginCameraRendering</c> is
/// stripped; <c>beginContextRendering</c> is an <c>Il2CppSystem.Action</c> (not a
/// C# delegate) over a struct parameter, so we do not ConvertDelegate it.
/// After <see cref="SilentFallbackFrames"/> of a silent camera hook, this type
/// falls back to FrameTicker LateUpdate and says so. <c>Application.onBeforeRender</c>
/// is stripped (evidence 101).
/// </remarks>
internal static class LateFrameHost
{
    internal const string PreCullLog = "Bootstrap: LateFrame hooked Camera.onPreCull";
    internal const string PreRenderLog = "Bootstrap: LateFrame hooked Camera.onPreRender";
    internal const string LateUpdateLog = "Bootstrap: LateFrame falling back to FrameTicker.LateUpdate";
    internal const string PreCullSource = "Camera.onPreCull";
    internal const string PreRenderSource = "Camera.onPreRender";
    internal const string LateUpdateSource = "FrameTicker.LateUpdate";
    internal const int SilentFallbackFrames = 2;

    private static Camera.CameraCallback? _preCull;
    private static Camera.CameraCallback? _preRender;
    private static bool _installed;
    private static int _lastFrame = -1;
    private static bool _loggedFirstInvoke;
    private static bool _loggedFirstFilled;
    private static bool _laterHookFired;
    private static bool _fallbackLateUpdate;
    private static int _silentFrames;

    internal static bool CameraHooked => _preCull != null || _preRender != null;

    internal static string Source { get; private set; } = "none";

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        if (TryHook("Camera.onPreCull", PreCullLog, OnPreCull, ref _preCull, GetPreCull, SetPreCull))
        {
            Source = PreCullSource;
            return;
        }

        if (TryHook("Camera.onPreRender", PreRenderLog, OnPreCull, ref _preRender, GetPreRender, SetPreRender))
        {
            Source = PreRenderSource;
            return;
        }

        Source = LateUpdateSource;
        _fallbackLateUpdate = true;
        ProcessHost.Log?.LogInfo(LateUpdateLog);
    }

    /// <summary>
    /// FrameTicker LateUpdate. Skipped once a camera callback has been observed
    /// firing, so the slot runs after every LateUpdate. If that hook never fires
    /// (HDRP skips FireOnPreCull), fall back here after
    /// <see cref="SilentFallbackFrames"/> silent frames. PresentationSystemGroup
    /// already ran in PreLateUpdate, so this write still lands after the sequencer.
    /// </summary>
    internal static void TickFromLateUpdate()
    {
        int frame = Time.frameCount;
        if (frame == _lastFrame) return;
        if (_laterHookFired && !_fallbackLateUpdate) return;
        if (CameraHooked && !_laterHookFired && !_fallbackLateUpdate)
        {
            _silentFrames++;
            if (_silentFrames <= SilentFallbackFrames) return;
            _fallbackLateUpdate = true;
            Source = LateUpdateSource;
            ProcessHost.Log?.LogInfo(
                "LateFrame: Camera/SRP callback never fired; falling back to FrameTicker.LateUpdate");
        }

        InvokeOnce(LateUpdateSource);
    }

    internal static void InvokeOnce() => InvokeOnce(Source);

    internal static void InvokeOnce(string source)
    {
        int frame = Time.frameCount;
        if (frame == _lastFrame) return;
        _lastFrame = frame;
        bool filled = ProcessHost.LateFrame != null;
        LogFirstInvoke(source, frame, filled);
        ProcessHost.InvokeLateFrame();
    }

    private static void LogFirstInvoke(string source, int frame, bool filled)
    {
        string slot = filled ? "filled" : "empty";
        if (!_loggedFirstInvoke)
        {
            _loggedFirstInvoke = true;
            if (filled) _loggedFirstFilled = true;
            ProcessHost.Log?.LogInfo(
                "LateFrame: first invoke source=" + source + " frame=" + frame + " slot=" + slot);
            return;
        }

        if (filled && !_loggedFirstFilled)
        {
            _loggedFirstFilled = true;
            ProcessHost.Log?.LogInfo(
                "LateFrame: first invoke source=" + source + " frame=" + frame + " slot=filled");
        }
    }

    private static void OnPreCull(Camera cam)
    {
        _ = cam;
        _laterHookFired = true;
        try { InvokeOnce(Source); }
        catch (Exception e) { ProcessHost.Log?.LogError("LateFrame camera callback threw: " + e); }
    }

    private static Camera.CameraCallback? GetPreCull() => Camera.onPreCull;

    private static void SetPreCull(Camera.CameraCallback? value) => Camera.onPreCull = value;

    private static Camera.CameraCallback? GetPreRender() => Camera.onPreRender;

    private static void SetPreRender(Camera.CameraCallback? value) => Camera.onPreRender = value;

    private static bool TryHook(
        string name,
        string successLog,
        Action<Camera> handler,
        ref Camera.CameraCallback? stored,
        Func<Camera.CameraCallback?> get,
        Action<Camera.CameraCallback?> set)
    {
        try
        {
            // Implicit conversion ConvertDelegate's the Bootstrap Action. Never pass a
            // Core method here (the cache pins that ALC for process lifetime).
            Camera.CameraCallback ours = handler;
            var current = get();
            set(current == null ? ours : current + ours);
            stored = ours;
            ProcessHost.Log?.LogInfo(successLog);
            return true;
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("Bootstrap: LateFrame " + name + " threw: " + e.Message);
            stored = null;
            return false;
        }
    }
}
