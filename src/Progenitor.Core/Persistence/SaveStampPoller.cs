using System;
using System.Threading;

namespace Progenitor.Persistence;

/// <summary>
/// Samples the save folder's newest write stamp off the main thread, one sample at a time (game-free).
/// The world-save guard polls the stamp while a save is being written; listing the folder and stating its
/// files on the main thread, right as the game wrote a large save, stalled a frame for 254 ms. The main
/// thread asks for a sample (<see cref="Request"/>), keeps running, and takes the result on a later frame
/// (<see cref="TryTake"/>) with the clock read by the worker just before and just after it listed the
/// folder, so the guard's settle rule is judged on when the folder was actually read
/// (<c>SyncSaveSuspension.ShouldResume(readStart, readEnd, stamp)</c>), not when the sample was asked for.
/// <see cref="Reset"/> drops a sample that was asked for before it.
/// </summary>
internal sealed class SaveStampPoller
{
    private readonly Func<string?, DateTime> _sample;
    private readonly Action<Action> _run;
    private readonly Func<double> _clock;
    private readonly object _gate = new();
    private int _generation;
    private bool _inFlight;
    private bool _hasResult;
    private DateTime _result;
    private double _readStart;
    private double _readEnd;

    /// <param name="sample">Reads the folder's newest stamp (<see cref="DateTime.MinValue"/> for none); runs on <paramref name="run"/>'s thread.</param>
    /// <param name="clock">Seconds on the caller's clock, read by the worker around the listing (thread-safe; a Stopwatch in production).</param>
    /// <param name="run">Starts work off the main thread (the thread pool in production; tests may run it inline or later).</param>
    internal SaveStampPoller(Func<string?, DateTime> sample, Func<double> clock, Action<Action>? run = null)
    {
        _sample = sample ?? throw new ArgumentNullException(nameof(sample));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _run = run ?? (work => ThreadPool.QueueUserWorkItem(_ => work()));
    }

    /// <summary>A sample was asked for and has not been taken yet (finished or not).</summary>
    internal bool Busy
    {
        get { lock (_gate) return _inFlight || _hasResult; }
    }

    /// <summary>Samples taken (tests and diagnostics).</summary>
    internal int Samples { get; private set; }

    /// <summary>Ask for a sample of <paramref name="folder"/>; ignored while one is pending.</summary>
    internal void Request(string? folder)
    {
        int generation;
        lock (_gate)
        {
            if (_inFlight || _hasResult) return;
            _inFlight = true;
            generation = _generation;
        }
        Samples++;
        try
        {
            _run(() =>
            {
                DateTime stamp;
                double start = _clock();
                try { stamp = _sample(folder); }
                catch { stamp = DateTime.MinValue; }
                double end = _clock();
                lock (_gate)
                {
                    if (generation != _generation) return;
                    _inFlight = false;
                    _hasResult = true;
                    _result = stamp;
                    _readStart = start;
                    _readEnd = end;
                }
            });
        }
        catch
        {
            lock (_gate) _inFlight = false;
        }
    }

    /// <summary>The finished sample and when the worker read the folder (just before and just after), once; false while none is ready.</summary>
    internal bool TryTake(out DateTime stamp, out double readStart, out double readEnd)
    {
        lock (_gate)
        {
            stamp = _result;
            readStart = _readStart;
            readEnd = _readEnd;
            if (!_hasResult) return false;
            _hasResult = false;
            return true;
        }
    }

    /// <summary>Forget any sample asked for so far (a new suspension starts, or the guard unloads).</summary>
    internal void Reset()
    {
        lock (_gate)
        {
            _generation++;
            _inFlight = false;
            _hasResult = false;
        }
    }
}
