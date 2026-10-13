using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Preloader.Core.Logging;
using Progenitor.SessionLogs;

namespace Progenitor.Preloader;

/// <summary>
/// SUPPORT1: the second disk listener, writing this session's copy of the BepInEx log to
/// <c>BepInEx/progenitor-logs/session.log</c> (<see cref="SessionLogFiles"/>). It is added to
/// <see cref="Logger.Listeners"/> from the patcher's <c>Initialize</c> and stays there for the life of the process:
/// BepInEx's IL2CPP preloader removes only its own buffering listener before the chainloader starts, and nothing
/// clears the listener list.
///
/// <para>Lines the preloader logs are buffered by BepInEx and replayed to every listener when the chainloader
/// initializes its loggers (which is how LogOutput.log gets them). While that buffering listener is still attached
/// this listener drops what it receives, so the replay writes each preloader line once, in order, the same as
/// LogOutput.log. The levels are BepInEx's default disk levels; the <c>Unity</c> source follows BepInEx's own
/// blacklist (<c>[Logging.Disk] WriteUnityLog</c>).</para>
/// </summary>
internal sealed class SessionLogListener : ILogListener
{
    private readonly SessionLogWriter _writer;
    private volatile bool _preloaderDone;

    private SessionLogListener(SessionLogWriter writer) => _writer = writer;

    /// <summary>BepInEx's default <c>[Logging.Disk] LogLevels</c>. A fixed set: binding the entry here would race the chainloader's own bind.</summary>
    public LogLevel LogLevelFilter => LogLevel.Fatal | LogLevel.Error | LogLevel.Warning | LogLevel.Message | LogLevel.Info;

    /// <summary>
    /// Open this session's log and attach the listener. Returns a line to log (where it writes, or why it does not).
    /// Never throws.
    /// </summary>
    internal static string Attach()
    {
        try
        {
            string root = Paths.BepInExRootPath;
            int pid = Environment.ProcessId;
            var stream = SessionLogFiles.Open(root, pid, out string path, out string error);
            if (stream == null) return "Progenitor preloader: no session log (" + error + ").";

            var writer = new SessionLogWriter(stream, SessionLogFiles.DefaultMaxBytes);
            string process = "";
            try { process = Process.GetCurrentProcess().ProcessName; }
            catch { /* the name is only for the header */ }
            writer.WriteLine(SessionLogFiles.Header(DateTime.UtcNow, process, pid), flushNow: true);

            var listener = new SessionLogListener(writer);
            Logger.Listeners.Add(listener);
            try { AppDomain.CurrentDomain.ProcessExit += (_, _) => writer.Flush(); }
            catch { /* the timer still flushes */ }
            return "Progenitor preloader: this session's log is BepInEx/" + SessionLogFiles.FolderName + "/" + System.IO.Path.GetFileName(path) + ".";
        }
        catch (Exception e)
        {
            return "Progenitor preloader: no session log (" + e.GetType().Name + ": " + e.Message + ").";
        }
    }

    public void LogEvent(object sender, LogEventArgs eventArgs)
    {
        try
        {
            if (!_preloaderDone)
            {
                bool buffering;
                try { buffering = PreloaderBuffering(); }
                catch { buffering = false; } // a BepInEx without that type: write from now on
                if (buffering) return;
                _preloaderDone = true;
            }

            if (eventArgs?.Source != null && IsBlacklisted(eventArgs.Source.SourceName)) return;
            bool urgent = (eventArgs!.Level & (LogLevel.Fatal | LogLevel.Error)) != LogLevel.None;
            _writer.WriteLine(eventArgs.ToString(), urgent);
        }
        catch { /* a log line must never throw into the logger */ }
    }

    private static bool _blacklistBroken;

    /// <summary>
    /// BepInEx's disk-log source blacklist (the <c>Unity</c> source unless <c>[Logging.Disk] WriteUnityLog</c>). Kept out of
    /// <see cref="LogEvent"/> and never inlined, so a BepInEx without that member fails here once (JIT-time
    /// <see cref="MissingMemberException"/>) instead of making every log line throw; after that nothing is blacklisted.
    /// </summary>
    private static bool IsBlacklisted(string? source)
    {
        if (_blacklistBroken || source == null) return false;
        try { return BlacklistContains(source); }
        catch
        {
            _blacklistBroken = true;
            return false;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool BlacklistContains(string source) => DiskLogListener.BlacklistedSources.Contains(source);

    /// <summary>True while BepInEx's preloader still buffers lines for the chainloader to replay.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool PreloaderBuffering()
    {
        foreach (var listener in Logger.Listeners)
            if (listener is PreloaderConsoleListener) return true;
        return false;
    }

    public void Dispose() => _writer.Dispose();
}
