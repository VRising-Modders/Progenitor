using System;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Minimal-GPU, minimal-CPU settings for a windowless extra client (<c>-batchmode</c> started by
/// <c>tools/extra-client.ps1 -Headless</c>, which sets <c>PROGENITOR_HEADLESS=1</c>). Nothing looks
/// at its screen, so it disables every camera (HDRP then renders nothing), caps the frame rate,
/// drops texture mip levels, turns shadows and LOD detail down and mutes all audio (FMOD events and
/// Unity's listener; the user heard headless swings through the shared audio device). It re-applies every two seconds,
/// because the game re-applies its own graphics settings and scene loads create cameras.
///
/// <para>Measured before this (2026-09-24, three clients): about 2.7 GB VRAM, 1.5 CPU cores and 1-2%
/// GPU each. Game logic does not need a camera for a bot: bots aim and move through
/// <c>EntityInput</c>, not the mouse.</para>
///
/// <para>Env overrides: <c>PROGENITOR_HEADLESS_FPS</c> (default 20, 5..60),
/// <c>PROGENITOR_HEADLESS_TEXTURE_LIMIT</c> (mip levels dropped, default 4, 0..6),
/// <c>PROGENITOR_HEADLESS_CAMERAS=1</c> keeps cameras on (for a debug capture).</para>
/// </summary>
internal static class HeadlessProfile
{
    private static bool _installed;
    private static int _frames;
    private static int _fps = 20;
    private static int _textureLimit = 4;
    private static bool _keepCameras;
    private static int _camerasDisabled;

    public static bool Active => _installed;

    /// <summary>Pure: parse an int env override within bounds, else the default.</summary>
    internal static int ParseBounded(string? text, int fallback, int min, int max) =>
        int.TryParse(text?.Trim(), out var v) && v >= min && v <= max ? v : fallback;

    public static void Install()
    {
        if (_installed || Progenitor.Plugin.IsServerProcess) return;
        if (!string.Equals(Environment.GetEnvironmentVariable("PROGENITOR_HEADLESS")?.Trim(), "1", StringComparison.Ordinal)) return;
        if (!Application.isBatchMode && !HasBatchModeArg()) return;
        _fps = ParseBounded(Environment.GetEnvironmentVariable("PROGENITOR_HEADLESS_FPS"), 20, 5, 60);
        _textureLimit = ParseBounded(Environment.GetEnvironmentVariable("PROGENITOR_HEADLESS_TEXTURE_LIMIT"), 4, 0, 6);
        _keepCameras = Environment.GetEnvironmentVariable("PROGENITOR_HEADLESS_CAMERAS")?.Trim() == "1";
        _installed = true;
        Progenitor.Plugin.Frame += OnFrame;
        Apply(log: true);
    }

    public static void Shutdown()
    {
        if (!_installed) return;
        Progenitor.Plugin.Frame -= OnFrame;
        _installed = false;
    }

    public static object Status() => new
    {
        active = _installed,
        frameCount = Time.frameCount,
        realtime = Time.realtimeSinceStartupAsDouble,
        fps = _fps,
        targetFrameRate = Application.targetFrameRate,
        textureLimit = QualitySettings.masterTextureLimit,
        shadows = QualitySettings.shadows.ToString(),
        keepCameras = _keepCameras,
        camerasDisabledTotal = _camerasDisabled,
        camerasEnabledNow = CountEnabledCameras(),
    };

    // HeadlessClient hides -batchmode from managed code, so Application.isBatchMode reads false.
    private static bool HasBatchModeArg()
    {
        foreach (var a in Environment.GetCommandLineArgs())
            if (string.Equals(a, "-batchmode", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void OnFrame()
    {
        if (++_frames % Math.Max(1, _fps * 2) != 0) return;
        try { Apply(log: false); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"headless profile: apply failed: {e.Message}"); }
    }

    private static void Apply(bool log)
    {
        if (QualitySettings.vSyncCount != 0) QualitySettings.vSyncCount = 0;
        if (Application.targetFrameRate != _fps) Application.targetFrameRate = _fps;
        if (QualitySettings.masterTextureLimit != _textureLimit) QualitySettings.masterTextureLimit = _textureLimit;
        if (QualitySettings.shadows != ShadowQuality.Disable) QualitySettings.shadows = ShadowQuality.Disable;
        if (QualitySettings.lodBias > 0.3f) QualitySettings.lodBias = 0.3f;
        if (QualitySettings.maximumLODLevel < 2) QualitySettings.maximumLODLevel = 2;
        int muted = Mute();
        int disabled = _keepCameras ? 0 : DisableCameras();
        _camerasDisabled += disabled;
        if (log || disabled > 0)
            Plugin.Logger?.LogInfo(
                $"headless profile: fps={_fps} textureLimit={_textureLimit} shadows=off lodBias=0.3 audio muted ({muted} FMOD studio manager(s)) " +
                $"cameras {(_keepCameras ? "kept (PROGENITOR_HEADLESS_CAMERAS=1)" : $"disabled {disabled} now")}");
    }

    // Nobody listens to a headless client: mute the game's FMOD events and Unity's own audio.
    // Re-applied every pass in case the game unmutes on a settings or scene change.
    private static int Mute()
    {
        int n = 0;
        try
        {
            var managers = Stunlock.Fmod.StudioManager.Instances;
            if (managers != null)
                for (int i = 0; i < managers.Count; i++)
                {
                    var m = managers[i];
                    if (m == null) continue;
                    m.MuteAllEvents(true);
                    n++;
                }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning($"headless profile: FMOD mute failed: {e.Message}"); }
        try
        {
            AudioListener.volume = 0f;
            AudioListener.pause = true;
        }
        catch { /* no Unity audio */ }
        return n;
    }

    private static int DisableCameras()
    {
        int count = Camera.allCamerasCount;
        if (count <= 0) return 0;
        // GetAllCameras fills the array natively: it must be an IL2CPP array (CLAUDE.md).
        var cams = new Il2CppReferenceArray<Camera>(count);
        int n = Camera.GetAllCameras(cams);
        int disabled = 0;
        for (int i = 0; i < n; i++)
        {
            var c = cams[i];
            if (c != null && c.enabled)
            {
                c.enabled = false;
                disabled++;
            }
        }
        return disabled;
    }

    private static int CountEnabledCameras()
    {
        try { return Camera.allCamerasCount; }
        catch { return -1; }
    }
}
