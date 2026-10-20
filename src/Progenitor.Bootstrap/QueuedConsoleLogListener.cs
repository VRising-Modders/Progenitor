using System;
using System.Diagnostics;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;

namespace Progenitor.Bootstrap;

/// <summary>
/// Process-lifetime replacement for BepInEx's <see cref="ConsoleLogListener"/>: the thread that logs formats the line
/// and queues it (<see cref="ConsoleLogQueue"/>); one background thread writes the queue to the BepInEx console.
///
/// <para>Why: BepInEx's console listener sets the color, writes the line and resets the color synchronously on the
/// logging thread, for every line from every mod. A console host that is slow to take output turns each
/// <c>LogInfo</c> into a frame stall, longer for longer lines (see the knowledge note
/// <c>console-log-line-stalls-server-frame</c>). The disk log (<c>LogOutput.log</c>), the bridge ring and the session
/// log are untouched and keep every line whole; only the console copy is queued, bounded and shortened.</para>
///
/// <para>Installed from <see cref="ProcessHost.Install"/> when the console is active and
/// <c>[Logging] QueuedConsole</c> in <c>progenitor.cfg</c> is true (the default). Its type name contains
/// "Console", so <c>LogDiag</c> (file-only diagnostics) still skips it.</para>
/// </summary>
internal sealed class QueuedConsoleLogListener : ILogListener
{
    internal const string ConfigSection = "Logging";
    internal const string ConfigKey = "QueuedConsole";

    /// <summary>
    /// The most process exit waits for the writer to finish what is queued. It never waits longer, even when a console
    /// write is blocked (<see cref="ConsoleBatchWriter.Stop"/>), so later exit handlers (the self-update promote) still run.
    /// </summary>
    private const int ExitDrainMs = 500;

    internal static QueuedConsoleLogListener? Instance { get; private set; }

    private readonly ConsoleLogQueue _queue = new();
    private readonly ILogListener _replaced;
    private readonly ConsoleBatchWriter _writer;

    private QueuedConsoleLogListener(ILogListener replaced)
    {
        _replaced = replaced;
        _writer = new ConsoleBatchWriter(_queue, WriteRun, ResetColor);
    }

    /// <summary>The level filter of the listener it replaced (BepInEx's <c>[Logging.Console] LogLevels</c>).</summary>
    public LogLevel LogLevelFilter
    {
        get
        {
            try { return _replaced.LogLevelFilter; }
            catch { return LogLevel.Fatal | LogLevel.Error | LogLevel.Warning | LogLevel.Message | LogLevel.Info; }
        }
    }

    internal ConsoleLogQueue Queue => _queue;

    /// <summary>
    /// Swap BepInEx's console listener for this one. Returns the line to log (what happened). Never throws. The new
    /// listener is added before the old one is removed, so a line logged during the swap may print twice, never not at all.
    /// </summary>
    internal static string Install(ConfigFile? config)
    {
        try
        {
            if (Instance != null) return "";
            bool enabled = true;
            try
            {
                enabled = config?.Bind(ConfigSection, ConfigKey, true,
                    "When true (default), lines for the BepInEx console window are queued and written by a background thread, "
                    + "so a slow console never stalls the game's frame. Lines longer than "
                    + ConsoleLogQueue.DefaultMaxLineChars + " characters are shortened in the console only, and the console "
                    + "drops lines (with a notice) when it falls far behind. LogOutput.log always keeps every line whole. "
                    + "Set false if another mod needs BepInEx's own ConsoleLogListener in Logger.Listeners. "
                    + "Takes effect at the next start.").Value ?? true;
            }
            catch { enabled = true; }
            if (!enabled) return "Console: queued console is off ([Logging] QueuedConsole=false); BepInEx writes the console directly.";
            if (!ConsoleManager.ConsoleActive) return "";

            ConsoleLogListener? original = null;
            foreach (var listener in Logger.Listeners)
            {
                if (listener != null && listener.GetType() == typeof(ConsoleLogListener))
                {
                    original = (ConsoleLogListener)listener;
                    break;
                }
            }
            if (original == null) return "";

            var queued = new QueuedConsoleLogListener(original);
            queued._writer.Start();
            Logger.Listeners.Add(queued);
            Logger.Listeners.Remove(original);
            Instance = queued;
            try { AppDomain.CurrentDomain.ProcessExit += (_, _) => queued._writer.Stop(ExitDrainMs); }
            catch { /* background thread; the process ends anyway */ }
            return "Console: lines for the console window are queued and written off the game thread ([Logging] QueuedConsole).";
        }
        catch (Exception e)
        {
            return "Console: queued console not installed (" + e.GetType().Name + ": " + e.Message + "); BepInEx writes the console directly.";
        }
    }

    /// <summary>Counters for Core's hitch summary (<see cref="ConsoleLogQueue.ToArray"/>); null when not installed.</summary>
    internal static long[]? Snapshot(bool resetMaxima)
    {
        var i = Instance;
        return i == null ? null : ConsoleLogQueue.ToArray(i._queue.Snapshot(resetMaxima));
    }

    /// <summary>
    /// Queue the line. Lines logged during process exit are queued too: the writer keeps writing them for as long as the
    /// process lives (the unload and self-update lines), and exit waits only <see cref="ExitDrainMs"/> for them.
    /// </summary>
    public void LogEvent(object sender, LogEventArgs eventArgs)
    {
        try
        {
            if (eventArgs == null) return;
            long start = Stopwatch.GetTimestamp();
            string data;
            try { data = eventArgs.Data?.ToString() ?? ""; }
            catch (Exception e) { data = "<" + e.GetType().Name + " formatting the log line>"; }
            string text = ConsoleLogQueue.Format(
                eventArgs.Level.ToString(), eventArgs.Source?.SourceName ?? "?", data, _queue.MaxLineChars, out bool truncated);
            ConsoleColor color = eventArgs.Level.GetConsoleColor();
            _queue.Enqueue(color, text, truncated, Stopwatch.GetTimestamp() - start);
        }
        catch { /* a log line must never throw into the logger */ }
    }

    /// <summary>Writer thread only.</summary>
    private static void WriteRun(ConsoleColor color, string text)
    {
        var stream = ConsoleManager.ConsoleStream;
        if (stream == null) return;
        ConsoleManager.SetConsoleColor(color);
        stream.Write(text);
    }

    /// <summary>Writer thread only, after each batch: the console's default color, as BepInEx leaves it.</summary>
    private static void ResetColor() => ConsoleManager.SetConsoleColor(ConsoleColor.Gray);

    /// <summary>Kept for the life of the process: the writer thread is a background thread.</summary>
    public void Dispose() { }
}
