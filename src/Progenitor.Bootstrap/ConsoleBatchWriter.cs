using System;
using System.Diagnostics;
using System.Threading;

namespace Progenitor.Bootstrap;

/// <summary>
/// The queued console's single writer thread, game-free: it takes batches from a <see cref="ConsoleLogQueue"/> and
/// hands each same-color run to <c>write</c> (the console in production).
///
/// <para>Only this thread ever writes. Process exit (<see cref="Stop"/>) never writes itself and never waits on a lock:
/// it wakes the thread and waits, at most for its budget, until the queue is empty. A console write that blocks (a
/// QuickEdit selection pauses console output until it is cleared) therefore holds up only this background thread; exit
/// and the handlers after it go on once the budget is spent. Lines logged while stopping are still queued and written
/// for as long as the process lives.</para>
/// </summary>
internal sealed class ConsoleBatchWriter
{
    /// <summary>After waking, the writer waits this long so a burst goes out as one batch (not while stopping).</summary>
    internal const int DefaultCoalesceMs = 15;

    private readonly ConsoleLogQueue _queue;
    private readonly Action<ConsoleColor, string> _write;
    private readonly Action? _afterBatch;
    private readonly int _coalesceMs;
    private readonly Thread _thread;
    private volatile bool _stopping;
    private volatile bool _disposed;
    private volatile bool _inBatch;

    internal ConsoleBatchWriter(ConsoleLogQueue queue, Action<ConsoleColor, string> write, Action? afterBatch = null,
        int coalesceMs = DefaultCoalesceMs, string threadName = "Progenitor console writer")
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _write = write ?? throw new ArgumentNullException(nameof(write));
        _afterBatch = afterBatch;
        _coalesceMs = Math.Max(0, coalesceMs);
        _thread = new Thread(Loop) { IsBackground = true, Name = threadName };
    }

    internal bool Stopping => _stopping;

    internal void Start() => _thread.Start();

    private void Loop()
    {
        while (!_disposed)
        {
            try
            {
                if (!_queue.WaitForWork(250)) continue;
                if (!_stopping && _coalesceMs > 0) Thread.Sleep(_coalesceMs);
                WriteOneBatch();
            }
            catch
            {
                // A console that went away: keep going so the queue does not sit full; try again shortly.
                try { Thread.Sleep(100); } catch { return; }
            }
        }
    }

    /// <summary>Write one batch on this thread. False when nothing was queued.</summary>
    private bool WriteOneBatch()
    {
        _inBatch = true;
        try
        {
            var runs = _queue.TakeBatch();
            if (runs.Count == 0) return false;
            long start = Stopwatch.GetTimestamp();
            int lines = 0;
            foreach (var run in runs)
            {
                lines += run.Lines;
                try { _write(run.Color, run.Text); }
                catch { /* the console closed */ }
            }
            try { _afterBatch?.Invoke(); }
            catch { /* the console closed */ }
            _queue.NoteWritten(lines, Stopwatch.GetTimestamp() - start);
            return true;
        }
        finally
        {
            _inBatch = false;
        }
    }

    /// <summary>
    /// Process exit: let the writer finish what is queued, waiting at most <paramref name="maxMs"/>. Returns true when the
    /// queue was empty and the writer idle in time. Never blocks longer than the budget, whatever the console does.
    /// </summary>
    internal bool Stop(int maxMs)
    {
        _stopping = true;
        _queue.Wake();
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if (_queue.Pending == 0 && !_inBatch) return true;
            if (clock.ElapsedMilliseconds >= maxMs) return false;
            _queue.Wake();
            Thread.Sleep(5);
        }
    }

    /// <summary>End the thread after its current batch (tests; production keeps it for the life of the process).</summary>
    internal void Dispose()
    {
        _disposed = true;
        _queue.Wake();
    }
}
