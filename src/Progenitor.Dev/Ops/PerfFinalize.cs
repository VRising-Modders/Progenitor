using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Background finalization for a closed perf capture. The close thread snapshots the
/// recording and uninstalls the sink; this type then runs <c>Summary()</c>, JSON
/// serialization and the file write on a dedicated thread, the same pattern as the
/// freeze-dump writer.
///
/// <para>Snapshotting under <see cref="PerfCloseGate.RecordGate"/> protects concurrent
/// recording. It does not move this work off the close thread — that is what this
/// type is for. A diagnostic close-path fix, not an attribution of any observed
/// freeze to report writing.</para>
///
/// <para><b>Overlap.</b> One writer at a time. A later capture is refused while
/// <see cref="StateWriting"/> so two closes cannot share or overwrite one report
/// identity. Fence-by-id would still let a late publish land on the wrong
/// capture; queueing would hide that the previous write is still occupying the
/// disk. Refusal is the same contract as "already running".</para>
///
/// <para><b>Unload.</b> <see cref="Detach"/> bumps the generation so an in-flight
/// writer cannot publish into a later capture. It does not join: Unload and the
/// game main thread must not wait on the file write. The file may still land.
/// An ALC <c>Unloading</c> hook is the backstop if Dev unload did not run
/// <c>PerfOps.Shutdown</c>.</para>
/// </summary>
internal static class PerfFinalize
{
    internal const string StateNone = "none";
    internal const string StateWriting = "writing";
    internal const string StateCompleted = "completed";
    internal const string StateFailed = "failed";

    internal const string BusyRefusal =
        "a previous perf report is still being written; poll status until reportState is completed or failed";

    internal const string RunningRefusal =
        "a perf window is already running; call perf {\"action\":\"stop\"} first";

    private static readonly object Gate = new();
    private static readonly double MsPerTick = 1000.0 / Stopwatch.Frequency;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static int _generation;
    private static int _inFlightGeneration;
    private static bool _inFlight;
    private static string _state = StateNone;
    private static long _captureId;
    private static string? _path;
    private static string? _writeError;
    private static Dictionary<string, object?>? _summary;
    private static double _closeMs;
    private static double _finalizeMs;
    private static bool _endedDuringSilence;
    private static double _sampleSilenceMsAtClose;
    private static bool _alcHooked;

#if PROGENITOR_TESTS
    /// <summary>
    /// Test hook: return null to succeed, an error message to fail. When set, the
    /// production disk write is not used. The hook runs on the writer thread.
    /// </summary>
    internal static Func<string, string, string?>? WriteFile;
#endif

    /// <summary>
    /// True while a writer thread is still in flight. A new capture must not start.
    /// </summary>
    internal static bool IsBusy
    {
        get { lock (Gate) return _inFlight; }
    }

    /// <summary>
    /// The start-refusal policy <see cref="PerfOps"/> actually calls. Tests drive
    /// this function, not a copy of the rule.
    /// </summary>
    internal static string? StartRefusal(bool windowRunning, bool closeHeld, bool finalizeBusy)
    {
        if (finalizeBusy) return BusyRefusal;
        if (windowRunning || closeHeld) return RunningRefusal;
        return null;
    }

    internal static ReportWriteSnapshot Read()
    {
        lock (Gate) return SnapshotLocked();
    }

    /// <summary>
    /// Socket-thread live envelope: progress scalars plus report-write state.
    /// Takes this type's lock only — never <c>PerfOps.Gate</c>.
    /// </summary>
    internal static PerfLiveEnvelope LiveView(PerfProgress.PerfLiveStatus live, ReportWriteSnapshot report)
    {
        bool writing = report.State == StateWriting;
        bool idle = report.State == StateNone;
        return new PerfLiveEnvelope(
            live.running,
            live.historical,
            live.captureId,
            live.progress,
            live.note,
            report.State,
            report.CaptureId,
            report.Path,
            report.WriteError,
            report.CloseMs > 0 ? report.CloseMs : null,
            writing || idle ? null : report.FinalizeMs,
            report.EndedDuringSilence,
            idle ? null : report.SampleSilenceMsAtClose);
    }

    internal static Dictionary<string, object?> Result(ReportWriteSnapshot snap, string? note = null)
    {
        bool writing = snap.State == StateWriting;
        bool idle = snap.State == StateNone;
        return new Dictionary<string, object?>
        {
            ["running"] = false,
            ["reportState"] = snap.State,
            ["captureId"] = snap.CaptureId,
            ["path"] = snap.Path,
            ["writeError"] = snap.WriteError,
            ["summary"] = snap.Summary,
            ["closeMs"] = snap.CloseMs,
            ["finalizeMs"] = writing || idle ? null : snap.FinalizeMs,
            ["endedDuringSilence"] = snap.EndedDuringSilence,
            ["sampleSilenceMsAtClose"] = snap.SampleSilenceMsAtClose,
            ["note"] = note ?? NoteFor(snap.State),
        };
    }

    internal static string NoteFor(string state) => state switch
    {
        StateWriting =>
            "sampling stopped; the report is being written on a background thread. poll status or live until reportState is completed or failed",
        StateFailed =>
            "the report write failed; writeError names the failure and the summary is still here when Summary() succeeded",
        StateCompleted =>
            "the window had already closed; this is the finished report",
        _ => "no perf window is running; start one first",
    };

    /// <summary>
    /// Called on the close thread after snapshot and uninstall. Returns immediately
    /// once the writer thread is started. Does not join.
    /// </summary>
    internal static bool TryStart(long captureId, PerfReport report, string path, double closeMs)
    {
        if (report == null) throw new ArgumentNullException(nameof(report));
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("path is required", nameof(path));

        HookAlcUnload();
        int generation;
        lock (Gate)
        {
            if (_inFlight) return false;
            _inFlight = true;
            _inFlightGeneration = _generation;
            _state = StateWriting;
            _captureId = captureId;
            _path = path;
            _writeError = null;
            _summary = null;
            _closeMs = closeMs;
            _finalizeMs = 0;
            _endedDuringSilence = report.EndedDuringSilence;
            _sampleSilenceMsAtClose = report.SampleSilenceMsAtClose;
            generation = _generation;
        }

        report.CaptureId = captureId;
        report.CloseMs = closeMs;

        try
        {
            var thread = new Thread(() => WriteOnBackground(generation, captureId, report, path, closeMs))
            {
                IsBackground = true,
                Name = "progenitor-perf-write",
            };
            thread.Start();
            return true;
        }
        catch (Exception e)
        {
            Finish(generation, captureId, ok: false, path, closeMs, 0, null, e.Message,
                report.EndedDuringSilence, report.SampleSilenceMsAtClose);
            return true;
        }
    }

    /// <summary>
    /// Unload / generation hand-off. Does not join the writer. An in-flight write
    /// still occupies the slot until that thread exits, then does not publish.
    /// </summary>
    internal static void Detach()
    {
        lock (Gate)
        {
            _generation++;
            _captureId = 0;
            _path = null;
            _writeError = null;
            _summary = null;
            _closeMs = 0;
            _finalizeMs = 0;
            _endedDuringSilence = false;
            _sampleSilenceMsAtClose = 0;
            _state = _inFlight ? StateWriting : StateNone;
        }
    }

#if PROGENITOR_TESTS
    internal static void ResetForTests()
    {
        Detach();
        lock (Gate)
        {
            _inFlight = false;
            _state = StateNone;
            _generation++;
        }
        WriteFile = null;
    }
#endif

    private static void WriteOnBackground(
        int generation, long captureId, PerfReport report, string path, double closeMs)
    {
        long started = Stopwatch.GetTimestamp();
        Dictionary<string, object?>? summary = null;
        string? error = null;
        try
        {
            summary = report.Summary();
            var json = JsonSerializer.Serialize(report.ToJson(), JsonOptions);
            error = WriteContents(path, json);
        }
        catch (Exception e)
        {
            error = e.Message;
            try { Plugin.Logger?.LogError($"perf report write to '{path}' failed: {error}"); }
            catch { }
        }

        double finalizeMs = (Stopwatch.GetTimestamp() - started) * MsPerTick;
        report.FinalizeMs = finalizeMs;
        if (summary != null)
        {
            summary["closeMs"] = PerfStats.Round(closeMs, 3);
            summary["finalizeMs"] = PerfStats.Round(finalizeMs, 3);
            summary["sampleSilenceMsAtClose"] = PerfStats.Round(report.SampleSilenceMsAtClose, 3);
            summary["endedDuringSilence"] = report.EndedDuringSilence;
            summary["captureId"] = captureId;
        }

        bool ok = error == null;
        if (!ok)
        {
            try
            {
                if (path.Length > 0 && File.Exists(path)) File.Delete(path);
            }
            catch { }
        }

        Finish(generation, captureId, ok, ok ? path : null, closeMs, finalizeMs, summary, error,
            report.EndedDuringSilence, report.SampleSilenceMsAtClose);
    }

    private static string? WriteContents(string path, string json)
    {
#if PROGENITOR_TESTS
        var hook = WriteFile;
        if (hook != null) return hook(path, json);
#endif
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, json);
            return null;
        }
        catch (Exception e)
        {
            try { Plugin.Logger?.LogError($"perf report write to '{path}' failed: {e.Message}"); }
            catch { }
            return $"perf report write to '{path}' failed: {e.Message}";
        }
    }

    private static void Finish(
        int generation,
        long captureId,
        bool ok,
        string? path,
        double closeMs,
        double finalizeMs,
        Dictionary<string, object?>? summary,
        string? error,
        bool endedDuringSilence,
        double sampleSilenceMsAtClose)
    {
        lock (Gate)
        {
            if (_inFlight && _inFlightGeneration == generation)
                _inFlight = false;

            if (generation != _generation)
            {
                if (!_inFlight && _state == StateWriting)
                    _state = StateNone;
                return;
            }

            _state = ok ? StateCompleted : StateFailed;
            _captureId = captureId;
            _path = path;
            _writeError = ok ? null : error;
            _summary = summary;
            _closeMs = closeMs;
            _finalizeMs = finalizeMs;
            _endedDuringSilence = endedDuringSilence;
            _sampleSilenceMsAtClose = sampleSilenceMsAtClose;
        }
    }

    private static ReportWriteSnapshot SnapshotLocked() =>
        new(_state, _captureId, _path, _writeError, _summary, _closeMs, _finalizeMs,
            _endedDuringSilence, _sampleSilenceMsAtClose);

    private static void HookAlcUnload()
    {
        lock (Gate)
        {
            if (_alcHooked) return;
            _alcHooked = true;
        }
        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(typeof(PerfFinalize).Assembly);
            if (alc == null || !alc.IsCollectible) return;
            alc.Unloading += static _ =>
            {
                try { Detach(); } catch { }
            };
        }
        catch
        {
            lock (Gate) _alcHooked = false;
        }
    }
}

/// <summary>Published write-slot snapshot. Safe to copy off the lock.</summary>
internal readonly struct ReportWriteSnapshot
{
    internal ReportWriteSnapshot(
        string state,
        long captureId,
        string? path,
        string? writeError,
        Dictionary<string, object?>? summary,
        double closeMs,
        double finalizeMs,
        bool endedDuringSilence,
        double sampleSilenceMsAtClose)
    {
        State = state;
        CaptureId = captureId;
        Path = path;
        WriteError = writeError;
        Summary = summary;
        CloseMs = closeMs;
        FinalizeMs = finalizeMs;
        EndedDuringSilence = endedDuringSilence;
        SampleSilenceMsAtClose = sampleSilenceMsAtClose;
    }

    internal string State { get; }
    internal long CaptureId { get; }
    internal string? Path { get; }
    internal string? WriteError { get; }
    internal Dictionary<string, object?>? Summary { get; }
    internal double CloseMs { get; }
    internal double FinalizeMs { get; }
    internal bool EndedDuringSilence { get; }
    internal double SampleSilenceMsAtClose { get; }
}

/// <summary>
/// Socket-thread live view: capture progress plus report-write state. Extra fields
/// are additive; existing <c>running</c> / <c>historical</c> / <c>captureId</c> /
/// <c>progress</c> / <c>note</c> keep their meaning.
/// </summary>
internal sealed record PerfLiveEnvelope(
    bool running,
    bool historical,
    long captureId,
    PerfProgress.Snapshot? progress,
    string note,
    string reportState,
    long reportCaptureId,
    string? path,
    string? writeError,
    double? closeMs,
    double? finalizeMs,
    bool endedDuringSilence,
    double? sampleSilenceMsAtClose);

/// <summary>
/// Whether a capture ended without a completed frame interval covering the close.
/// Silence above <see cref="SilenceGapMs"/> at close is not a normal inter-tick gap;
/// the omitted interval is at least that silence. This flags the capture shape. It
/// does not attribute a freeze to report writing.
/// </summary>
internal static class PerfCloseObservation
{
    /// <summary>
    /// A completed frame covering the close was not observed when silence at close
    /// exceeds this. 100 ms is several frames at 60 Hz and is not a normal gap.
    /// </summary>
    public const double SilenceGapMs = 100;

    public static bool EndedDuringSilence(double sampleSilenceMsAtClose) =>
        sampleSilenceMsAtClose > SilenceGapMs;

    public static void Stamp(PerfReport report, double sampleSilenceMsAtClose, double? lastFrameMs)
    {
        if (report == null) throw new ArgumentNullException(nameof(report));
        report.SampleSilenceMsAtClose = sampleSilenceMsAtClose;
        report.LastFrameMs = lastFrameMs;
        report.EndedDuringSilence = EndedDuringSilence(sampleSilenceMsAtClose);
    }

    public static void Stamp(PerfReport report, PerfProgress.Snapshot? snap)
    {
        if (snap == null)
        {
            Stamp(report, 0, null);
            return;
        }
        Stamp(report, snap.sampleSilenceMs, snap.lastFrameMs);
    }
}
