using System;
using BepInEx.Logging;

namespace Progenitor;

/// <summary>
/// File-only diagnostics (docs/bridge.md, "Logging policy"). <see cref="LogDiag"/> writes an Info line to
/// <c>BepInEx/LogOutput.log</c> and to the Dev bridge's log ring, but not to the BepInEx console window: a console
/// that draws every line stalls the game, and per-frame or per-layout diagnostics are read from the file or with the
/// bridge's <c>logs</c> op anyway. Nothing needs editing in <c>BepInEx.cfg</c>.
///
/// <para>The line goes to every registered <see cref="ILogListener"/> whose filter takes Info, except the console
/// listeners (<c>ConsoleLogListener</c>, and the preloader's while it is still registered). The file line reads
/// exactly as a <c>LogInfo</c> line from the same source would. Warnings and errors keep using
/// <c>LogWarning</c>/<c>LogError</c> and still reach the console.</para>
/// </summary>
public static class DiagnosticLog
{
    /// <summary>Write <paramref name="data"/> as an Info line to the log file and the bridge ring, not the console.</summary>
    public static void LogDiag(this ManualLogSource? source, object? data)
    {
        if (source == null) return;
        LogEventArgs args;
        try { args = new LogEventArgs(data, LogLevel.Info, source); }
        catch { return; }
        try
        {
            foreach (ILogListener listener in Logger.Listeners)
            {
                if (listener == null || IsConsole(listener)) continue;
                try
                {
                    if ((listener.LogLevelFilter & LogLevel.Info) == LogLevel.None) continue;
                    listener.LogEvent(source, args);
                }
                catch
                {
                    // One listener failing must not stop the others, or the caller.
                }
            }
        }
        catch
        {
            // Logger.Listeners can throw while BepInEx tears down; a diagnostic is not worth more than that.
        }
    }

    /// <summary>The console listeners: BepInEx's <c>ConsoleLogListener</c> and the preloader's console listener.</summary>
    private static bool IsConsole(ILogListener listener)
    {
        if (listener is ConsoleLogListener) return true;
        string name = listener.GetType().Name;
        return name.IndexOf("Console", StringComparison.Ordinal) >= 0;
    }
}
