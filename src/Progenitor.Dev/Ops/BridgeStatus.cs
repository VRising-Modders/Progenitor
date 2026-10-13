using System;
using System.Globalization;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Socket-thread liveness snapshot of the one-op gate and drain heartbeat. Never takes the gate.
/// Optional args toggle the removable instruments: <c>uiTickTiming</c>, <c>heartbeatSeconds</c>,
/// <c>freezeSeconds</c>.
/// </summary>
internal static class BridgeStatus
{
    internal static object Run(MainThreadOpGate gate, JsonElement args)
    {
        if (gate == null) throw new ArgumentNullException(nameof(gate));
        ApplyToggles(args);
        var snap = gate.Describe();
        MainThread.QueueCounts(out int queued, out int delayed);
        FreezeDump.Snapshot(
            out bool freezeWatchdog,
            out int freezeSeconds,
            out int freezeDumpCount,
            out object? lastFreezeDump,
            out bool dumpInProgress,
            out string dumpPhase,
            out bool dumpCapReached,
            out object? lastDumpError,
            out bool dumpSyntheticArmed,
            out string? lastDumpTrigger);
        return Shape(
            snap.Entered,
            snap.Op,
            snap.Id,
            snap.EnteredAtUtc,
            snap.Abandoned,
            snap.ElapsedMs,
            queued,
            delayed,
            MainThread.LastDrainAtUtc == default ? null : MainThread.LastDrainAtUtc,
            MainThread.LastDrainMs,
            MainThread.LastDrainFrame,
            MainThread.HeartbeatSeconds,
            MainThread.UiTickTimingEnabled,
            freezeWatchdog,
            freezeSeconds,
            freezeDumpCount,
            lastFreezeDump,
            dumpInProgress,
            dumpPhase,
            dumpCapReached,
            lastDumpError,
            dumpSyntheticArmed,
            lastDumpTrigger);
    }

    internal static object Shape(
        bool entered,
        string? op,
        long id,
        DateTime? enteredAtUtc,
        bool abandoned,
        long elapsedMs,
        int queued,
        int delayed,
        DateTime? lastDrainAtUtc,
        long lastDrainMs,
        int frame,
        int heartbeatSeconds,
        bool uiTickTiming,
        bool freezeWatchdog = false,
        int freezeSeconds = FreezeDump.DefaultFreezeSeconds,
        int freezeDumpCount = 0,
        object? lastFreezeDump = null,
        bool dumpInProgress = false,
        string dumpPhase = FreezeDump.PhaseIdle,
        bool dumpCapReached = false,
        object? lastDumpError = null,
        bool dumpSyntheticArmed = false,
        string? lastDumpTrigger = null)
    {
        return new
        {
            entered,
            op,
            id,
            enteredAt = enteredAtUtc?.ToString("o", CultureInfo.InvariantCulture),
            abandoned,
            elapsedMs,
            queued,
            delayed,
            lastDrainAt = lastDrainAtUtc?.ToString("o", CultureInfo.InvariantCulture),
            lastDrainMs,
            frame,
            heartbeatSeconds,
            uiTickTiming,
            freezeWatchdog,
            freezeSeconds,
            freezeDumpCount,
            dumpInProgress,
            dumpPhase,
            dumpCapReached,
            lastDumpError,
            dumpSyntheticArmed,
            lastDumpTrigger,
            lastFreezeDump,
        };
    }

    internal static void ApplyToggles(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) return;
        if (args.TryGetProperty("uiTickTiming", out var timing)
            && (timing.ValueKind == JsonValueKind.True || timing.ValueKind == JsonValueKind.False))
            MainThread.UiTickTimingEnabled = timing.GetBoolean();
        if (args.TryGetProperty("heartbeatSeconds", out var hb)
            && hb.ValueKind == JsonValueKind.Number
            && hb.TryGetInt32(out int seconds)
            && seconds >= 0)
            MainThread.HeartbeatSeconds = seconds;
        if (args.TryGetProperty("freezeSeconds", out var freeze)
            && freeze.ValueKind == JsonValueKind.Number
            && freeze.TryGetInt32(out int freezeSeconds)
            && freezeSeconds >= 0)
            FreezeDump.FreezeSeconds = freezeSeconds;
    }
}
