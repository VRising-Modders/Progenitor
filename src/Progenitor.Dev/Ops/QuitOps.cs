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
    /// surprise exit is visible in BepInEx. Returns <c>{ok:true}</c> immediately; Unity quits
    /// after the current frame. Must run on the main thread (the bridge already marshals it).
    /// </summary>
    public static object Quit()
    {
        try
        {
            Plugin.Logger?.LogWarning("quit requested via bridge");
            UnityEngine.Application.Quit();
            return new { ok = true };
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"quit op threw: {e}");
            throw;
        }
    }
}
