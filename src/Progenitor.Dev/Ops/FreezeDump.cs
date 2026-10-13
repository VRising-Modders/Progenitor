using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using BepInEx;

namespace Progenitor.Dev.Ops;

/// <summary>
/// In-process freeze watchdog and the socket-thread <c>dump</c> op. Polls
/// <see cref="Progenitor.Dev.MainThread.LastDrainFrame"/> from a Dev background
/// thread (never the Unity main thread). A stuck frame counter starts one
/// dedicated short-lived writer thread that minidumps this process so a later
/// native unwind can name the hitch. The socket thread never calls
/// <c>MiniDumpWriteDump</c>.
/// </summary>
internal static class FreezeDump
{
    internal const int DefaultFreezeSeconds = 10;
    internal const int DumpCap = 3;
    internal const int DefaultPollMs = 2000;
    internal const string FolderName = "progenitor-dumps";
    internal const string ProcessCapKey = "Progenitor.FreezeDump.DumpsWritten";
    internal const string PhaseIdle = "idle";
    internal const string PhaseTriggered = "triggered";
    internal const string PhaseWriting = "writing";
    internal const string PhaseCompleted = "completed";
    internal const string PhaseFailed = "failed";
    internal const string TriggerWatchdog = "watchdog";
    internal const string TriggerDump = "dump";

    /// <summary>
    /// <c>MiniDumpNormal | MiniDumpWithHandleData | MiniDumpWithThreadInfo</c>.
    /// Thread stacks and handle data, no full memory.
    /// </summary>
    internal const uint DumpType =
        MiniDumpNormal | MiniDumpWithHandleData | MiniDumpWithThreadInfo;

    internal const uint MiniDumpNormal = 0x00000000;
    internal const uint MiniDumpWithHandleData = 0x00000004;
    internal const uint MiniDumpWithThreadInfo = 0x00001000;

    private static readonly object Gate = new();

    private static FreezeWatchState _state;
    private static int _freezeSeconds = DefaultFreezeSeconds;
    private static string? _lastPath;
    private static DateTime _lastAtUtc;
    private static long _lastFrozenMs;
    private static bool _running;
    private static CancellationTokenSource? _cts;
    private static bool _alcHooked;
    private static bool _dumpInProgress;
    private static string? _inProgressPath;
    private static string _phase = PhaseIdle;
    private static int _lastErrorHr;
    private static string? _lastErrorPath;
    private static DateTime _lastErrorAtUtc;
    private static string? _lastTrigger;
    private static bool _lastSynthetic;
    private static bool _syntheticArmed;
    private static bool _capLogged;
    /// <summary>Poll period. Production stays at <see cref="DefaultPollMs"/>; tests shorten it.</summary>
    internal static int PollMs = DefaultPollMs;

#if PROGENITOR_TESTS
    internal static Action<string>? LogSink;
    /// <summary>Test hook: return 0 to succeed, a HRESULT to fail. Never calls dbghelp.</summary>
    internal static Func<string, int> WriteDump = static _ => 0;
#endif
    internal static Func<long> NowMs = () => Environment.TickCount64;

    internal static int FreezeSeconds
    {
        get { lock (Gate) return _freezeSeconds; }
        set { lock (Gate) _freezeSeconds = value < 0 ? 0 : value; }
    }

    /// <summary>
    /// Starts the poller. Called from <c>Handlers</c> construction (the Dev
    /// socket's lifetime). Idempotent: cancels any previous poller first. Dump
    /// count is per process (survives a collectible Dev reload) and is not reset.
    /// </summary>
    internal static void Start()
    {
        Stop();
        var cts = new CancellationTokenSource();
        lock (Gate)
        {
            _cts = cts;
            _running = true;
            _state.DumpsWritten = Math.Max(_state.DumpsWritten, ReadProcessDumpsWritten());
            WriteProcessDumpsWritten(_state.DumpsWritten);
            _state.Initialized = false;
            _capLogged = _state.DumpsWritten >= DumpCap;
        }
        HookAlcUnload();
        try
        {
            var thread = new Thread(() => Loop(cts.Token))
            {
                IsBackground = true,
                Name = "progenitor-freeze-watch",
            };
            thread.Start();
        }
        catch (Exception e)
        {
            lock (Gate) _running = false;
            Log(WatchdogFailedLine(e.HResult));
        }
    }

    /// <summary>
    /// Signals the poller to exit. Does not join: Unload / the game main thread
    /// must not wait on a dump write. A writer already in flight is left running.
    /// </summary>
    internal static void Stop()
    {
        CancellationTokenSource? cts;
        lock (Gate)
        {
            cts = _cts;
            _cts = null;
            _running = false;
        }
        try { cts?.Cancel(); } catch { }
    }

    /// <summary>
    /// Socket-thread / watchdog poll. Reads the frame counter (a Core static int;
    /// this method never marshals to the Unity main thread) and starts at most
    /// one dump writer when the freeze threshold is crossed. Returns immediately;
    /// never calls <c>MiniDumpWriteDump</c> on this thread. After <see cref="Stop"/>
    /// this is a no-op so a dying generation cannot arm a dump.
    /// </summary>
    internal static void Poll()
    {
        int frame;
        try { frame = Progenitor.Dev.MainThread.LastDrainFrame; }
        catch { return; }

        long now = NowMs();
        long frozenMs = 0;
        string? path = null;
        bool logCap = false;
        StallEdgeKind edge;
        long stalledMs;
        lock (Gate)
        {
            if (!_running) return;
            if (_dumpInProgress) return;
            SyncProcessCountLocked();
            edge = StallEdge(ref _state, frame, now, _freezeSeconds, out stalledMs);
            if (!ShouldDump(ref _state, frame, now, _freezeSeconds, out frozenMs))
            {
                if (_state.DumpsWritten >= DumpCap)
                    logCap = NoteCapLocked();
            }
            else
            {
                WriteProcessDumpsWritten(_state.DumpsWritten);
                path = NextPath(frame);
                _dumpInProgress = true;
                _inProgressPath = path;
                _phase = PhaseTriggered;
                _lastTrigger = TriggerWatchdog;
                _lastSynthetic = _syntheticArmed;
            }
        }
        // Outside Gate: the window read is a few user32 calls, never a message to the game window.
        if (edge == StallEdgeKind.Began)
        {
            var window = FreezeWindowState.Read();
            Log(StallBeganLine(stalledMs, frame, window));
        }
        else if (edge == StallEdgeKind.Ended)
        {
            Log(StallEndedLine(stalledMs, frame, FreezeWindowState.Read()));
        }
        if (path == null)
        {
            if (logCap) Log(CapReachedLine(DumpCap));
            return;
        }
        StartWriter(path, frame, frozenMs, TriggerWatchdog);
    }

    /// <summary>
    /// Manual <c>dump</c> op. Socket thread. Args: <c>reason</c>. Starts a
    /// dedicated writer thread and returns immediately with <c>started</c> and
    /// <c>path</c>. Never calls <c>MiniDumpWriteDump</c> here.
    /// </summary>
    internal static object Run(JsonElement args)
    {
        string reason = ReadReason(args);
        int frame;
        try { frame = Progenitor.Dev.MainThread.LastDrainFrame; }
        catch { frame = 0; }
        long now = NowMs();
        long frozenMs;
        string path;
        int written;
        lock (Gate)
        {
            SyncProcessCountLocked();
            if (_dumpInProgress)
            {
                return new
                {
                    started = false,
                    error = "dump in progress",
                    path = _inProgressPath,
                    dumpsWritten = _state.DumpsWritten,
                    dumpCapReached = _state.DumpsWritten >= DumpCap,
                    phase = _phase,
                    reason,
                };
            }
            if (_state.DumpsWritten >= DumpCap)
            {
                bool logCap = NoteCapLocked();
                var capped = new
                {
                    started = false,
                    error = "dump cap reached (3 per process)",
                    dumpsWritten = _state.DumpsWritten,
                    dumpCapReached = true,
                    phase = _phase,
                    reason,
                };
                if (logCap)
                {
                    // Log after constructing the result but still under Gate is fine: Log never
                    // re-enters FreezeDump. The operator-visible cap must not be a silent no-op.
                    Log(CapReachedLine(DumpCap));
                }
                return capped;
            }
            if (!_state.Initialized)
            {
                _state.Frame = frame;
                _state.LastAdvanceMs = now;
                _state.Armed = true;
                _state.Initialized = true;
            }
            frozenMs = now - _state.LastAdvanceMs;
            if (frozenMs < 0) frozenMs = 0;
            _state.DumpsWritten++;
            WriteProcessDumpsWritten(_state.DumpsWritten);
            path = NextPath(frame);
            _dumpInProgress = true;
            _inProgressPath = path;
            _phase = PhaseTriggered;
            _lastTrigger = TriggerDump;
            _lastSynthetic = _syntheticArmed;
            written = _state.DumpsWritten;
        }

        if (!StartWriter(path, frame, frozenMs, TriggerDump))
        {
            lock (Gate) written = _state.DumpsWritten;
            return new
            {
                started = false,
                error = "dump writer failed to start",
                dumpsWritten = written,
                dumpCapReached = written >= DumpCap,
                phase = PhaseFailed,
                reason,
            };
        }

        return new
        {
            started = true,
            path,
            reason,
            frame,
            frozenMs,
            dumpsWritten = written,
            dumpCapReached = written >= DumpCap,
            phase = PhaseWriting,
            synthetic = _syntheticArmed,
        };
    }

    internal static void Snapshot(
        out bool running,
        out int freezeSeconds,
        out int dumpCount,
        out object? lastFreezeDump,
        out bool dumpInProgress) =>
        Snapshot(
            out running,
            out freezeSeconds,
            out dumpCount,
            out lastFreezeDump,
            out dumpInProgress,
            out _,
            out _,
            out _,
            out _,
            out _);

    internal static void Snapshot(
        out bool running,
        out int freezeSeconds,
        out int dumpCount,
        out object? lastFreezeDump,
        out bool dumpInProgress,
        out string dumpPhase,
        out bool dumpCapReached,
        out object? lastDumpError,
        out bool syntheticArmed,
        out string? lastTrigger)
    {
        lock (Gate)
        {
            SyncProcessCountLocked();
            running = _running;
            freezeSeconds = _freezeSeconds;
            dumpCount = _state.DumpsWritten;
            dumpInProgress = _dumpInProgress;
            dumpPhase = _phase;
            dumpCapReached = _state.DumpsWritten >= DumpCap;
            syntheticArmed = _syntheticArmed;
            lastTrigger = _lastTrigger;
            lastFreezeDump = _lastPath == null ? null : SuccessDumpLocked();
            lastDumpError = _phase == PhaseFailed || _lastErrorHr != 0 || _lastErrorPath != null
                ? FailedDumpLocked()
                : null;
        }
    }

    /// <summary>
    /// Pure freeze decision. First sample only records the frame. An advancing
    /// frame re-arms and never dumps. A stuck frame for <paramref name="freezeSeconds"/>
    /// dumps once; the next dump waits for an advance. Cap is <see cref="DumpCap"/>.
    /// Frame 0 (no drain yet) never auto-dumps, so process start is not a freeze.
    /// </summary>
    internal static bool ShouldDump(
        ref FreezeWatchState state,
        int frame,
        long nowMs,
        int freezeSeconds,
        out long frozenMs)
    {
        frozenMs = 0;
        if (!state.Initialized)
        {
            state.Frame = frame;
            state.LastAdvanceMs = nowMs;
            state.Armed = true;
            state.Initialized = true;
            return false;
        }

        if (frame != state.Frame)
        {
            state.Frame = frame;
            state.LastAdvanceMs = nowMs;
            state.Armed = true;
            return false;
        }

        frozenMs = nowMs - state.LastAdvanceMs;
        if (frozenMs < 0) frozenMs = 0;
        if (frame == 0) return false;
        if (freezeSeconds <= 0) return false;
        if (!state.Armed) return false;
        if (state.DumpsWritten >= DumpCap) return false;
        if (frozenMs < freezeSeconds * 1000L) return false;

        state.Armed = false;
        state.DumpsWritten++;
        return true;
    }

    internal enum StallEdgeKind
    {
        None,
        Began,
        Ended,
    }

    /// <summary>
    /// Pure stall-edge decision, evaluated before <see cref="ShouldDump"/> on the same sample (it
    /// reads <c>Frame</c>/<c>LastAdvanceMs</c> before <see cref="ShouldDump"/> moves them and owns only
    /// <c>Stalled</c>). <see cref="StallEdgeKind.Began"/> fires once when the frame has been stuck for
    /// <paramref name="freezeSeconds"/>, whether or not a dump is written (the cap or a dump in flight
    /// must not hide a freeze). <see cref="StallEdgeKind.Ended"/> fires on the first advance after that,
    /// with <paramref name="stalledMs"/> the whole stall (to poll precision), so the log shows when the
    /// frame loop came back: the per-frame heartbeat cannot say it was stopped.
    /// </summary>
    internal static StallEdgeKind StallEdge(
        ref FreezeWatchState state,
        int frame,
        long nowMs,
        int freezeSeconds,
        out long stalledMs)
    {
        stalledMs = 0;
        if (!state.Initialized) return StallEdgeKind.None;
        long since = nowMs - state.LastAdvanceMs;
        if (since < 0) since = 0;
        if (frame != state.Frame)
        {
            if (!state.Stalled) return StallEdgeKind.None;
            state.Stalled = false;
            stalledMs = since;
            return StallEdgeKind.Ended;
        }
        if (state.Stalled || frame == 0 || freezeSeconds <= 0) return StallEdgeKind.None;
        if (since < freezeSeconds * 1000L) return StallEdgeKind.None;
        state.Stalled = true;
        stalledMs = since;
        return StallEdgeKind.Began;
    }

    internal static string StallBeganLine(long frozenMs, int frame, FreezeWindowState window) =>
        "freeze began frozenForMs=" + frozenMs.ToString(CultureInfo.InvariantCulture)
        + " frame=" + frame.ToString(CultureInfo.InvariantCulture)
        + " " + window.Describe()
        + " hint=" + window.Hint();

    internal static string StallEndedLine(long frozenMs, int frame, FreezeWindowState window) =>
        "freeze ended frozenForMs=" + frozenMs.ToString(CultureInfo.InvariantCulture)
        + " frame=" + frame.ToString(CultureInfo.InvariantCulture)
        + " " + window.Describe();

    internal static string FileName(DateTime utc, int frame) =>
        FileName(utc, frame, synthetic: false);

    internal static string FileName(DateTime utc, int frame, bool synthetic) =>
        (synthetic ? "synthetic-" : "")
        + "freeze-"
        + utc.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture)
        + "-frame"
        + frame.ToString(CultureInfo.InvariantCulture)
        + ".dmp";

    internal static string Folder()
    {
        string root = "";
        try { root = Paths.BepInExRootPath ?? ""; }
        catch { root = ""; }
        return string.IsNullOrEmpty(root) ? FolderName : Path.Combine(root, FolderName);
    }

    internal static string WrittenLine(string path, long frozenMs, int frame) =>
        "freeze dump written path=" + path
        + " frozenForMs=" + frozenMs.ToString(CultureInfo.InvariantCulture)
        + " frame=" + frame.ToString(CultureInfo.InvariantCulture);

    internal static string FailedLine(int hr) =>
        "freeze dump failed hr=" + hr.ToString(CultureInfo.InvariantCulture);

    internal static string WatchdogFailedLine(int hr) =>
        "freeze watchdog failed to start hr=" + hr.ToString(CultureInfo.InvariantCulture);

    internal static string CapReachedLine(int cap) =>
        "freeze dump cap reached dumpsWritten=" + cap.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Marks subsequent dumps (watchdog or <c>dump</c> op) as synthetic so every
    /// artifact is labeled. The hold fixture calls this; a later reader must not
    /// treat those files as the historical freeze.
    /// </summary>
    internal static void BeginSyntheticCapture()
    {
        lock (Gate) _syntheticArmed = true;
    }

    internal static void EndSyntheticCapture()
    {
        lock (Gate) _syntheticArmed = false;
    }

#if PROGENITOR_TESTS
    internal static void ResetForTests()
    {
        Stop();
        lock (Gate)
        {
            _state = default;
            _freezeSeconds = DefaultFreezeSeconds;
            _lastPath = null;
            _lastAtUtc = default;
            _lastFrozenMs = 0;
            _running = false;
            _dumpInProgress = false;
            _inProgressPath = null;
            _phase = PhaseIdle;
            _lastErrorHr = 0;
            _lastErrorPath = null;
            _lastErrorAtUtc = default;
            _lastTrigger = null;
            _lastSynthetic = false;
            _syntheticArmed = false;
            _capLogged = false;
            PollMs = DefaultPollMs;
        }
        ClearProcessDumpsWritten();
        FreezeWindowState.ReadOverride = null;
        LogSink = null;
        WriteDump = static _ => 0;
        NowMs = () => Environment.TickCount64;
    }

    /// <summary>Mark the poller armed without starting a thread, so tests can drive <see cref="Poll"/>.</summary>
    internal static void ArmForTests()
    {
        lock (Gate) _running = true;
    }

    /// <summary>
    /// New collectible Dev generation: statics reset, per-process cap kept.
    /// </summary>
    internal static void SimulateNewGenerationForTests()
    {
        Stop();
        lock (Gate)
        {
            int written = Math.Max(_state.DumpsWritten, ReadProcessDumpsWritten());
            _state = default;
            _state.DumpsWritten = written;
            WriteProcessDumpsWritten(written);
            _freezeSeconds = DefaultFreezeSeconds;
            _lastPath = null;
            _lastAtUtc = default;
            _lastFrozenMs = 0;
            _running = false;
            _dumpInProgress = false;
            _inProgressPath = null;
            _phase = PhaseIdle;
            _lastErrorHr = 0;
            _lastErrorPath = null;
            _lastErrorAtUtc = default;
            _lastTrigger = null;
            _lastSynthetic = false;
            _syntheticArmed = false;
            _capLogged = written >= DumpCap;
            _alcHooked = false;
            PollMs = DefaultPollMs;
        }
        LogSink = null;
        WriteDump = static _ => 0;
        NowMs = () => Environment.TickCount64;
    }
#endif

    private static string NextPath(int frame) =>
        Path.Combine(Folder(), FileName(DateTime.UtcNow, frame, _syntheticArmed));

    /// <summary>
    /// Starts the short-lived writer. Returns false if the thread could not be
    /// created; then the in-progress slot is released. Does not join.
    /// </summary>
    private static bool StartWriter(string path, int frame, long frozenMs, string trigger)
    {
        lock (Gate) _phase = PhaseWriting;
        try
        {
            var thread = new Thread(() => WriteOnBackground(path, frame, frozenMs, trigger))
            {
                IsBackground = true,
                Name = "progenitor-freeze-dump",
            };
            thread.Start();
            return true;
        }
        catch (Exception e)
        {
            FinishWrite(ok: false, path, frozenMs, e.HResult, trigger);
            return false;
        }
    }

    private static void Loop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (ct.WaitHandle.WaitOne(PollMs)) return;
                    Poll();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch
                {
                    // Keep polling. A dump failure is logged inside WriteOnBackground.
                }
            }
        }
        catch
        {
            // Background thread must not throw out of ThreadStart.
        }
    }

    /// <summary>
    /// Dedicated writer. Holds no framework lock around <c>MiniDumpWriteDump</c>;
    /// that call suspends other threads itself. Never the socket thread.
    /// </summary>
    private static void WriteOnBackground(string path, int frame, long frozenMs, string trigger)
    {
        bool ok = false;
        int hr = 0;
        try
        {
            ok = TryWrite(path, out hr);
        }
        catch (Exception e)
        {
            hr = e.HResult;
            ok = false;
        }
        FinishWrite(ok, path, frozenMs, hr, trigger);
        if (ok)
            Log(WrittenLine(path, frozenMs, frame));
        else
            Log(FailedLine(hr));
    }

    private static void FinishWrite(bool ok, string path, long frozenMs, int hr, string trigger)
    {
        lock (Gate)
        {
            _dumpInProgress = false;
            _inProgressPath = null;
            _lastTrigger = trigger;
            if (ok)
            {
                _lastPath = path;
                _lastAtUtc = DateTime.UtcNow;
                _lastFrozenMs = frozenMs;
                _lastSynthetic = path.IndexOf("synthetic-", StringComparison.OrdinalIgnoreCase) >= 0;
                _phase = PhaseCompleted;
            }
            else
            {
                if (_state.DumpsWritten > 0)
                    _state.DumpsWritten--;
                WriteProcessDumpsWritten(_state.DumpsWritten);
                if (_state.DumpsWritten < DumpCap)
                    _capLogged = false;
                _lastErrorHr = hr;
                _lastErrorPath = path;
                _lastErrorAtUtc = DateTime.UtcNow;
                _phase = PhaseFailed;
            }
        }

        if (ok) return;
        try
        {
            if (path.Length > 0 && File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static bool TryWrite(string path, out int hr)
    {
#if PROGENITOR_TESTS
        var hook = WriteDump;
        if (hook != null)
        {
            hr = hook(path);
            return hr == 0;
        }
#endif
        try { Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Folder()); }
        catch (Exception e)
        {
            hr = e.HResult;
            return false;
        }
        return TryMiniDump(path, out hr);
    }

    /// <summary>
    /// dbghelp MiniDumpWriteDump of the current process. Missing dbghelp is a
    /// logged failure, never a throw. Caller is the dedicated writer thread;
    /// never the Unity main thread or the socket thread. No framework lock is
    /// held here: MiniDumpWriteDump suspends other threads itself.
    /// </summary>
    private static bool TryMiniDump(string path, out int hr)
    {
        hr = 0;
        FileStream? fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            IntPtr process = GetCurrentProcess();
            uint pid = GetCurrentProcessId();
            bool ok = MiniDumpWriteDump(
                process,
                pid,
                fs.SafeFileHandle.DangerousGetHandle(),
                DumpType,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            if (!ok)
                hr = Marshal.GetHRForLastWin32Error();
            return ok;
        }
        catch (DllNotFoundException e)
        {
            hr = e.HResult;
            return false;
        }
        catch (EntryPointNotFoundException e)
        {
            hr = e.HResult;
            return false;
        }
        catch (Exception e)
        {
            hr = e.HResult;
            return false;
        }
        finally
        {
            try { fs?.Dispose(); } catch { }
        }
    }

    private static void HookAlcUnload()
    {
        lock (Gate)
        {
            if (_alcHooked) return;
            _alcHooked = true;
        }
        try
        {
            var alc = AssemblyLoadContext.GetLoadContext(typeof(FreezeDump).Assembly);
            if (alc == null || !alc.IsCollectible) return;
            alc.Unloading += static _ =>
            {
                try { Stop(); } catch { }
            };
        }
        catch
        {
            lock (Gate) _alcHooked = false;
        }
    }

    private static void SyncProcessCountLocked()
    {
        int process = ReadProcessDumpsWritten();
        if (process > _state.DumpsWritten)
            _state.DumpsWritten = process;
    }

    private static int ReadProcessDumpsWritten()
    {
        try
        {
            return AppDomain.CurrentDomain.GetData(ProcessCapKey) is int n && n > 0 ? n : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void WriteProcessDumpsWritten(int n)
    {
        try { AppDomain.CurrentDomain.SetData(ProcessCapKey, n < 0 ? 0 : n); }
        catch { }
    }

    private static void ClearProcessDumpsWritten()
    {
        try { AppDomain.CurrentDomain.SetData(ProcessCapKey, 0); }
        catch { }
    }

    /// <summary>Returns true when this is the first cap observation since the last release.</summary>
    private static bool NoteCapLocked()
    {
        if (_capLogged) return false;
        _capLogged = true;
        return true;
    }

    private static object SuccessDumpLocked() => new
    {
        path = _lastPath,
        at = _lastAtUtc.ToString("o", CultureInfo.InvariantCulture),
        frozenMs = _lastFrozenMs,
        completed = true,
        synthetic = _lastSynthetic,
        trigger = _lastTrigger,
    };

    private static object FailedDumpLocked() => new
    {
        path = _lastErrorPath,
        at = _lastErrorAtUtc == default
            ? null
            : _lastErrorAtUtc.ToString("o", CultureInfo.InvariantCulture),
        hr = _lastErrorHr,
        completed = false,
        synthetic = _lastSynthetic,
        trigger = _lastTrigger,
    };

    private static string ReadReason(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return "";
        if (!args.TryGetProperty("reason", out var el) || el.ValueKind != JsonValueKind.String)
            return "";
        return el.GetString() ?? "";
    }

    private static void Log(string line)
    {
#if PROGENITOR_TESTS
        try { LogSink?.Invoke(line); } catch { }
#endif
        try { Plugin.Logger?.LogWarning(line); } catch { }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    [DllImport("dbghelp.dll", CallingConvention = CallingConvention.StdCall, SetLastError = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        IntPtr hFile,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);
}

/// <summary>Mutable freeze-watch bookkeeping for <see cref="FreezeDump.ShouldDump"/>.</summary>
internal struct FreezeWatchState
{
    public int Frame;
    public long LastAdvanceMs;
    public bool Armed;
    public int DumpsWritten;
    public bool Initialized;
    /// <summary>A stall reached the freeze threshold and has not ended yet (<see cref="FreezeDump.StallEdge"/>).</summary>
    public bool Stalled;
}
