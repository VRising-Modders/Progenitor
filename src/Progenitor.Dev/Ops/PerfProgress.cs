using System;
using System.Threading;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Scalar-only publication for socket readers; never walks the live recording.
/// Record, Read and Complete share one short private lock. <see cref="LiveStatus"/>
/// takes that lock only — never <c>PerfOps.Gate</c> — so a main-thread start/stop/report
/// cannot stall the socket read.
/// </summary>
internal sealed class PerfProgress
{
    internal const string LiveNote =
        "Silence is time since the last completed sample (or capture start). It is not an attributed frame duration or proof of a freeze. When running=false and historical=true these are retained values from captureId, not a current liveness signal.";

    private readonly object _gate = new();
    private readonly long _captureId;
    private readonly long _started;
    private readonly double _msPerTick;
    private bool _running = true;
    private bool _historical;
    private long _frames, _lastSample;
    private double _frameMs, _coreMs, _maxFrameMs;

    internal long CaptureId => _captureId;

    internal PerfProgress(long captureId, long started, long frequency)
    {
        _captureId = captureId;
        _started = started;
        _msPerTick = 1000.0 / frequency;
    }

    internal void Record(long captureId, long now, double frameMs, double coreMs)
    {
        lock (_gate)
        {
            if (!_running || captureId != _captureId) return;
            _frames++;
            _lastSample = now;
            _frameMs = frameMs;
            _coreMs = coreMs;
            _maxFrameMs = Math.Max(_maxFrameMs, frameMs);
        }
    }

    /// <summary>
    /// Marks this capture finished. Later <see cref="Record"/> calls — including a late
    /// frame from the old sink — are ignored so historical values cannot move.
    /// </summary>
    internal void Complete()
    {
        lock (_gate)
        {
            _running = false;
            _historical = true;
        }
    }

    internal Snapshot Read(long now)
    {
        lock (_gate) return SnapshotLocked(now);
    }

    /// <summary>
    /// Socket-thread live view. <paramref name="progress"/> is a volatile snapshot of the
    /// published capture; this method never takes an external gate.
    /// </summary>
    internal static PerfLiveStatus LiveStatus(PerfProgress? progress, long now)
    {
        if (progress == null)
            return new PerfLiveStatus(false, false, 0, null, LiveNote);
        return progress.LiveView(now);
    }

    private PerfLiveStatus LiveView(long now)
    {
        lock (_gate)
            return new PerfLiveStatus(_running, _historical, _captureId, SnapshotLocked(now), LiveNote);
    }

    private Snapshot SnapshotLocked(long now) =>
        new(_frames,
            Math.Max(0, now - (_frames == 0 ? _started : _lastSample)) * _msPerTick,
            _frames == 0 ? null : _frameMs,
            _frames == 0 ? null : _coreMs,
            _frames == 0 ? null : _maxFrameMs);

    internal sealed record Snapshot(long frames, double sampleSilenceMs,
        double? lastFrameMs, double? lastCoreTickMs, double? maxFrameMs);

    internal sealed record PerfLiveStatus(
        bool running, bool historical, long captureId, Snapshot? progress, string note);
}

/// <summary>
/// Timer and sink cleanup for one capture generation. Dispose is idempotent and drops the
/// callback so no static keeps a delegate into the Core generation that just unloaded.
/// </summary>
internal sealed class PerfCaptureLifetime : IDisposable
{
    private Action? _release;

    internal PerfCaptureLifetime(Action release) =>
        _release = release ?? throw new ArgumentNullException(nameof(release));

    internal bool Disposed => Volatile.Read(ref _release) == null;

    public void Dispose()
    {
        var release = Interlocked.Exchange(ref _release, null);
        if (release == null) return;
        try { release(); }
        catch { }
    }
}
