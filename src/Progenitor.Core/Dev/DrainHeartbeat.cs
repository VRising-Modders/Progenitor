using System;
using System.Collections;
using System.Reflection;

namespace Progenitor.Dev;

/// <summary>
/// Bounded drain heartbeat: one file-only Info line every <see cref="MainThread.HeartbeatSeconds"/>
/// (0 disables), checked inside the once-per-frame drain rather than every frame.
/// </summary>
internal static class DrainHeartbeat
{
    internal const int DefaultSeconds = 5;

    internal static Func<long> NowMs = () => Environment.TickCount64;
    internal static Action<string>? LogSink;

    private static long _lastEmitMs = long.MinValue;
    private static int _lastEmitFrame = int.MinValue;
    private static ICollection? _hostQueue;
    private static ICollection? _hostDelayed;
    private static Func<long>? _hostDrainMs;
    private static bool _hostBound;

    internal static bool Due(long nowMs, long lastEmitMs, int periodSeconds)
    {
        if (periodSeconds <= 0) return false;
        if (lastEmitMs == long.MinValue) return true;
        return nowMs - lastEmitMs >= periodSeconds * 1000L;
    }

    internal static string Format(int frame, long drainMs, int queued, int delayed, string gate) =>
        "tick hb frame=" + frame
        + " drainMs=" + drainMs
        + " queued=" + queued
        + " delayed=" + delayed
        + " gate=" + (string.IsNullOrEmpty(gate) ? "free" : gate);

    internal static string FormatGate(bool entered, string? op, long id) =>
        entered ? "busy:" + (op ?? "") + "#" + id : "free";

    internal static void NoteDrain(long drainMs)
    {
        MainThread.LastDrainMs = drainMs;
        MainThread.LastDrainAtUtc = DateTime.UtcNow;
        MaybeEmit();
    }

    internal static void NoteFrame(int frame)
    {
        MainThread.LastDrainFrame = frame;
        CopyHostDrainStamp();
        MaybeEmit();
    }

    internal static void Counts(out int queued, out int delayed)
    {
#if !PROGENITOR_TESTS
        if (BootstrapLink.MainThread != null)
        {
            HostCounts(out queued, out delayed);
            return;
        }
#endif
        MainThread.LeftoverCounts(out queued, out delayed);
        if (queued != 0 || delayed != 0) return;
        HostCounts(out queued, out delayed);
    }

    internal static void ResetForTests()
    {
        _lastEmitMs = long.MinValue;
        _lastEmitFrame = int.MinValue;
        NowMs = () => Environment.TickCount64;
        LogSink = null;
        _hostQueue = null;
        _hostDelayed = null;
        _hostDrainMs = null;
        _hostBound = false;
    }

    private static void MaybeEmit()
    {
        int period = MainThread.HeartbeatSeconds;
        if (period <= 0) return;
        int frame = MainThread.LastDrainFrame;
        if (frame == _lastEmitFrame) return;
        long now = NowMs();
        if (!Due(now, _lastEmitMs, period)) return;
        _lastEmitMs = now;
        _lastEmitFrame = frame;
        Counts(out int queued, out int delayed);
        Write(Format(
            frame,
            MainThread.LastDrainMs,
            queued,
            delayed,
            MainThread.HeartbeatGateLabel));
    }

    private static void Write(string line)
    {
        try { LogSink?.Invoke(line); }
        catch { }
        // File only (docs/bridge.md, "Logging policy"): read it in LogOutput.log or with the bridge's logs op.
        try { Plugin.Logger?.LogDiag(line); }
        catch { }
    }

    private static void HostCounts(out int queued, out int delayed)
    {
        BindHost();
        queued = Count(_hostQueue);
        delayed = Count(_hostDelayed);
    }

    private static int Count(ICollection? collection)
    {
        if (collection == null) return 0;
        try { return collection.Count; }
        catch { return 0; }
    }

    private static void BindHost()
    {
        if (_hostBound) return;
        _hostBound = true;
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? type;
                try { type = asm.GetType("Progenitor.Bootstrap.WorkQueue", throwOnError: false); }
                catch { continue; }
                if (type == null) continue;
                _hostQueue = ReadStaticCollection(type, "Queue");
                _hostDelayed = ReadStaticCollection(type, "Delayed");
                _hostDrainMs = BindLongReader(type, "LastDrainMs");
                return;
            }
        }
        catch
        {
            // Heartbeat still logs queued=0 delayed=0.
        }
    }

    /// <summary>
    /// Production Drain lives in Bootstrap and runs before Core's tick, so
    /// <see cref="NoteDrain"/> never sees it. Copy <c>WorkQueue.LastDrainMs</c> when
    /// that property exists. Absent until Bootstrap stamps it — do not add that stamp
    /// from Core; a Bootstrap edit forces a process restart.
    /// </summary>
    private static void CopyHostDrainStamp()
    {
        BindHost();
        if (_hostDrainMs == null) return;
        try { MainThread.LastDrainMs = _hostDrainMs(); }
        catch { }
    }

    private static ICollection? ReadStaticCollection(Type type, string name)
    {
        try
        {
            var field = type.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return field?.GetValue(null) as ICollection;
        }
        catch
        {
            return null;
        }
    }

    private static Func<long>? BindLongReader(Type type, string name)
    {
        try
        {
            var prop = type.GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            var getter = prop?.GetGetMethod(nonPublic: true);
            if (getter == null || getter.ReturnType != typeof(long)) return null;
            return (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), getter);
        }
        catch
        {
            return null;
        }
    }
}
