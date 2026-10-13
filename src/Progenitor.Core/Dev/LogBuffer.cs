using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;

namespace Progenitor.Dev;

/// <summary>
/// In-memory ring of recent log entries from every BepInEx log source, with a monotonically
/// increasing sequence number so a reader can ask for "everything since N". The debug bridge serves
/// from here rather than tailing the log file, so nothing depends on disk flushing or file locks.
/// </summary>
public sealed class LogBuffer : ILogListener
{
    public readonly struct Entry
    {
        public Entry(long seq, DateTime time, LogLevel level, string source, string text)
        {
            Seq = seq; Time = time; Level = level; Source = source; Text = text;
        }
        public long Seq { get; }
        public DateTime Time { get; }
        public LogLevel Level { get; }
        public string Source { get; }
        public string Text { get; }
    }

    public static LogBuffer Instance { get; } = new();

    /// <summary>
    /// Bootstrap's ring when that plugin is installed. Dev still reads <see cref="Instance"/>;
    /// forwarding keeps a single sequence number across the process.
    /// </summary>
    internal static BootstrapLink.ILogBackend? Backend { get; set; }

    /// <summary>
    /// <c>typeof(Progenitor.Bootstrap.LogRing).FullName</c>. Core must not add a second
    /// <see cref="ILogListener"/> when Bootstrap already installed that ring.
    /// </summary>
    internal const string BootstrapRingTypeName = "Progenitor.Bootstrap.LogRing";

    internal const string SecondRingSkipLog = "Bootstrap log ring already installed; Core will not install a second ring";

    private const int Capacity = 4000;
    private readonly Entry[] _ring = new Entry[Capacity];
    private readonly object _lock = new();
    private long _next;
    private static int _loggedRingSkip;
    private static bool _listenerInstalled;

    public LogLevel LogLevelFilter => LogLevel.All;

    internal static bool BootstrapRingIsListening()
    {
        try
        {
            foreach (ILogListener listener in Logger.Listeners)
            {
                if (listener == null) continue;
                if (string.Equals(listener.GetType().FullName, BootstrapRingTypeName, StringComparison.Ordinal))
                    return true;
            }
        }
        catch
        {
            // Logger.Listeners can throw if BepInEx is tearing down; treat as "not listening".
        }
        return false;
    }

    internal static void Install()
    {
        bool ringListening = BootstrapRingIsListening();
        if (ringListening)
            BootstrapLink.EnsureLogBackend();
        if (!Ticks.TickOwnership.CoreMayInstallLogRing(Backend != null, ringListening))
        {
            if (Backend == null && ringListening && Interlocked.Exchange(ref _loggedRingSkip, 1) == 0)
                Plugin.Logger?.LogWarning(SecondRingSkipLog);
            return;
        }
        if (!Logger.Listeners.Contains(Instance))
        {
            Logger.Listeners.Add(Instance);
            _listenerInstalled = true;
        }
    }

    /// <summary>
    /// Remove this ring from <see cref="Logger.Listeners"/> when Core installed it
    /// (Bootstrap-absent path). No-op when Bootstrap's ring is the listener.
    /// </summary>
    internal static void Uninstall()
    {
        if (!_listenerInstalled) return;
        bool removed = false;
        try
        {
            Logger.Listeners.Remove(Instance);
            removed = true;
        }
        catch
        {
            // tearing down; leave the flag set so a later Uninstall retries
        }
        // Clear the flag only after Remove succeeded. A throw leaves it set so a
        // later Uninstall retries (R16 finding 6). Policy: CoreReloadPolicy.LogListenerInstalledAfterUninstall.
        _listenerInstalled = !removed;
    }

    public void LogEvent(object sender, LogEventArgs eventArgs)
    {
        var text = eventArgs.Data?.ToString() ?? "";
        var source = eventArgs.Source?.SourceName ?? "?";
        lock (_lock)
        {
            _ring[_next % Capacity] = new Entry(_next, DateTime.UtcNow, eventArgs.Level, source, text);
            _next++;
        }
    }

    public void Dispose() { }

    /// <summary>Entries with Seq > since, newest last, at most max, optionally filtered by substring and minimum level.</summary>
    public List<Entry> Read(long since, int max, string? filter, LogLevel? minLevel)
    {
        if (Backend == null) BootstrapLink.EnsureLogBackend();
        if (Backend != null) return Backend.Read(since, max, filter, minLevel);
        if (!Networking.ReceiveSlotHandoff.ServeCoreLogRing(backendSet: false, BootstrapRingIsListening()))
            return new List<Entry>();
        var result = new List<Entry>();
        lock (_lock)
        {
            long start = Math.Max(since + 1, _next - Capacity);
            for (long s = start; s < _next; s++)
            {
                var e = _ring[s % Capacity];
                if (minLevel.HasValue && e.Level > minLevel.Value) continue; // BepInEx levels: lower value = more severe
                if (!string.IsNullOrEmpty(filter) && e.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && e.Source.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                result.Add(e);
            }
        }
        if (result.Count > max) result.RemoveRange(0, result.Count - max);
        return result;
    }

    public long LatestSeq
    {
        get
        {
            if (Backend == null) BootstrapLink.EnsureLogBackend();
            if (Backend != null) return Backend.LatestSeq;
            if (!Networking.ReceiveSlotHandoff.ServeCoreLogRing(backendSet: false, BootstrapRingIsListening()))
                return -1;
            lock (_lock) return _next - 1;
        }
    }
}
