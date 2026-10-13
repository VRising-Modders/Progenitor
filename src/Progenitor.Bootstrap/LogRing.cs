using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace Progenitor.Bootstrap;

/// <summary>
/// Process-lifetime log ring. Lives here so a later Core swap does not drop <c>LatestSeq</c> or
/// in-flight <c>logs</c> reads. Core's <c>Progenitor.Dev.LogBuffer</c> forwards to this instance
/// when Bootstrap is installed.
/// </summary>
internal sealed class LogRing : ILogListener
{
    internal readonly struct Entry
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

    internal static LogRing Instance { get; } = new();

    private const int Capacity = 4000;
    private readonly Entry[] _ring = new Entry[Capacity];
    private readonly object _lock = new();
    private long _next;

    public LogLevel LogLevelFilter => LogLevel.All;

    internal static void Install()
    {
        if (!Logger.Listeners.Contains(Instance)) Logger.Listeners.Add(Instance);
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

    internal List<Entry> Read(long since, int max, string? filter, LogLevel? minLevel)
    {
        var result = new List<Entry>();
        lock (_lock)
        {
            long start = Math.Max(since + 1, _next - Capacity);
            for (long s = start; s < _next; s++)
            {
                var e = _ring[s % Capacity];
                if (minLevel.HasValue && e.Level > minLevel.Value) continue;
                if (!string.IsNullOrEmpty(filter) && e.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && e.Source.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                result.Add(e);
            }
        }
        if (result.Count > max) result.RemoveRange(0, result.Count - max);
        return result;
    }

    internal long LatestSeq { get { lock (_lock) return _next - 1; } }
}
