using System;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Process-exit debug-bridge op. <see cref="UnityEngine.Application.Quit()"/> is the graceful
/// path: Unity fires <c>Application.quitting</c> at the end of the current frame, and Core's
/// handler there promotes a staged <c>Progenitor.dll.next</c>. Killing the process skips that.
/// </summary>
public static class QuitOps
{
    /// <summary>
    /// Request process exit on this side (client or dedicated/bundled server). Does not refuse:
    /// a running world, a launch in flight, and the main menu are all valid. Logs a warning so a
    /// surprise exit is visible in BepInEx. TITLEVIDEO1: Core retires the video players first and
    /// quits after at most two frames (0.5 s), logging <c>TitleVideo: quit (bridge): retired N video
    /// player(s); quitting after F frame(s), T s</c>; a server has no players and quits at once.
    /// Returns <c>{ok:true, titleVideo}</c> immediately. Must run on the main thread (the bridge
    /// already marshals it).
    /// </summary>
    public static object Quit()
    {
        try
        {
            Plugin.Logger?.LogWarning("quit requested via bridge");
            var step = Progenitor.Ui.TitleVideoQuitBridge.Quit("bridge");
            return new { ok = true, titleVideo = step };
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"quit op threw: {e}");
            throw;
        }
    }
}
