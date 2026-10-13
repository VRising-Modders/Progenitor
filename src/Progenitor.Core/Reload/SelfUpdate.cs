using System;
using System.IO;
using System.Reflection;

namespace Progenitor.Reload;

/// <summary>
/// Fallback promoter for <c>BepInEx/progenitor-core/Progenitor.dll.next</c> when Bootstrap is
/// absent. When Bootstrap is present it promotes Core before <c>LoadFrom</c> (this launch) and
/// this type is a no-op so <c>DelegateSupport.ConvertDelegate</c> does not cache an IL2CPP
/// delegate on Core. Loop scripts still promote both DLLs before spawn.
/// </summary>
internal static class SelfUpdate
{
    private const string NextSuffix = ".next";
    private const string PrevSuffix = ".prev";

    private static EventHandler? _processExit;

    public static void Install()
    {
        if (BootstrapLink.OwnsLifetime) return;
        // Startup: a .next left behind by a previous crash. Promoting now means the *next* launch is
        // current; this launch keeps running the old copy.
        if (Promote(out var msg)) Plugin.Logger?.LogWarning($"{msg} It takes effect on the next launch; this process is still running the previous Core.");
        if (_processExit == null)
            _processExit = (_, _) => PromoteAtExit();
        AppDomain.CurrentDomain.ProcessExit += _processExit;
        // Never ConvertDelegate from Core: that caches an IL2CPP delegate and pins a collectible
        // ALC. Bootstrap owns Application.quitting. Fallback here is ProcessExit only. Keep the
        // handler in a static so Uninstall can unsubscribe (anonymous lambdas pin this ALC).
    }

    /// <summary>
    /// Drop the ProcessExit handler. Called from Core Uninstall on the Bootstrap-absent
    /// path so a collectible generation does not stay rooted by AppDomain.
    /// </summary>
    public static void Uninstall()
    {
        var handler = _processExit;
        _processExit = null;
        if (handler == null) return;
        try { AppDomain.CurrentDomain.ProcessExit -= handler; }
        catch { /* tearing down */ }
    }

    private static bool _exitHandled;

    private static void PromoteAtExit()
    {
        if (_exitHandled) return;
        _exitHandled = true;
        if (Promote(out var m)) Plugin.Logger?.LogInfo(m);
    }

    private static bool Promote(out string message)
    {
        message = "";
        try
        {
            var current = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(current)) return false;
            var next = current + NextSuffix;
            if (!File.Exists(next)) return false;

            var prev = current + PrevSuffix;
            if (File.Exists(prev)) File.Delete(prev);
            File.Move(current, prev);
            try
            {
                File.Move(next, current);
            }
            catch
            {
                // The first move already renamed Progenitor.dll away. Restore it before this
                // method returns or the plugins folder has no Core for the next launch.
                RestoreCurrent(prev, current);
                throw;
            }
            try { File.Delete(prev); } catch { /* still mapped by this process; harmless, cleaned next time */ }
            message = $"Core: promoted {Path.GetFileName(next)} to {Path.GetFileName(current)}.";
            return true;
        }
        catch (Exception e)
        {
            message = $"Core: could not promote staged update: {e.Message}";
            Plugin.Logger?.LogWarning(message);
            return false;
        }
    }

    /// <summary>
    /// Move <paramref name="prev"/> back to <paramref name="current"/> if the second promote
    /// move failed. Never deletes <paramref name="current"/> if it already exists (a partial
    /// same-volume rename is still a file BepInEx can try to load).
    /// </summary>
    private static void RestoreCurrent(string prev, string current)
    {
        try
        {
            if (File.Exists(current)) return;
            if (!File.Exists(prev))
            {
                Plugin.Logger?.LogError($"Core: promote failed and {Path.GetFileName(current)} is missing; {Path.GetFileName(prev)} is also missing.");
                return;
            }
            File.Move(prev, current);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Core: failed to restore {Path.GetFileName(current)} from {Path.GetFileName(prev)}: {e.Message}. The plugins folder may have no Progenitor.dll.");
        }
    }
}
