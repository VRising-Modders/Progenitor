#if !PROGENITOR_TESTS
using System;
using System.Diagnostics;
using Progenitor.Inventory;

namespace Progenitor.Persistence;

/// <summary>
/// Server: one world-save suspension for every Core module that keeps state in game components the world save writes and
/// that must not survive a crash (the container preview's inventory sync entries, the streaming-origin leases'
/// <c>User.ProxySyncPosition</c>). When the game accepts a world save (<see cref="ModSave.BeforeWorldSave"/>) it raises
/// <see cref="Suspend"/>; once the save file is written (<see cref="SyncSaveSuspension"/>: the write stamp moved past the
/// trigger's and settled, or a timeout) it raises <see cref="Resume"/>. A second save during it extends it.
/// <see cref="Enabled"/> false (Dev bridge, E1b persistence read only) lets one save through untouched and turns itself
/// back on after it or after <see cref="OffMaxSeconds"/>.
/// </summary>
internal static class WorldSaveGuard
{
    /// <summary>The most the guard stays off.</summary>
    internal const double OffMaxSeconds = 300;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly SyncSaveSuspension Suspension = new();
    private static bool _installed;
    private static bool _enabled = true;
    private static double _offSince = double.NaN;
    private static double _nextTick;

    /// <summary>A world save was accepted: take the state out of the game's components now.</summary>
    internal static event Action? Suspend;

    /// <summary>The save is written: put the state back.</summary>
    internal static event Action? Resume;

    internal static double Now => Clock.Elapsed.TotalSeconds;

    /// <summary>True while a world save is being written.</summary>
    internal static bool Active => Suspension.Active;

    /// <summary>E1b only: false lets the next world save keep the state in. Back on after that save or five minutes.</summary>
    internal static bool Enabled
    {
        get => _enabled;
        set { _enabled = value; _offSince = value ? double.NaN : Now; }
    }

    internal static void Install()
    {
        if (_installed || !Plugin.IsServerProcess) return;
        _installed = true;
        Enabled = true;
        ModSave.BeforeWorldSave += OnBeforeWorldSave;
        Plugin.Frame += Tick;
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        ModSave.BeforeWorldSave -= OnBeforeWorldSave;
        Plugin.Frame -= Tick;
        Suspension.End();
        StampPoller.Reset();
        Suspend = null;
        Resume = null;
    }

    private static DateTime SaveStamp()
    {
        try { return WorldSaveTrigger.NewestSaveWriteUtc(WorldSaveTrigger.SaveFolder()); }
        catch { return DateTime.MinValue; }
    }

    /// <summary>
    /// The game accepted (or the Dev bridge queued) a world save: begin, or extend, the suspension and tell the modules.
    /// With the guard off this one save goes through untouched, and the guard is on again.
    /// </summary>
    private static void OnBeforeWorldSave()
    {
        if (!_enabled)
        {
            Plugin.Logger?.LogWarning("WorldSaveGuard: the guard is off (Dev bridge); this world save keeps the guarded state. The guard is on again from now.");
            Enabled = true;
            return;
        }
        // Sampled here, on the main thread, before the game writes: the stamp the write must move past.
        StampPoller.Reset();
        Suspension.Begin(Now, SaveStamp());
        Raise(Suspend, "suspend");
    }

    private static void Tick()
    {
        double now = Now;
        if (now < _nextTick) return;
        _nextTick = now + 0.25;
        if (!_enabled && now - _offSince >= OffMaxSeconds)
        {
            Enabled = true;
            Plugin.Logger?.LogInfo("WorldSaveGuard: the guard is back on (" + OffMaxSeconds + " s passed).");
        }
        if (!Suspension.Active) return;
        // The folder is listed off the main thread (StampPoller): on the main thread, right as the game wrote a
        // large save, it stalled a frame for 254 ms. A sample is judged at the times the worker read the folder
        // (its own clock reads around the listing), so the settle is never shorter than a second of real
        // stillness; with none ready, only the timeout is checked.
        long start = Stopwatch.GetTimestamp();
        bool resume;
        if (StampPoller.TryTake(out var stamp, out double readStart, out double readEnd)) resume = Suspension.ShouldResume(readStart, readEnd, stamp);
        else resume = Suspension.ShouldResume(now, DateTime.MinValue);
        if (!resume)
        {
            if (!StampPoller.Busy) StampPoller.Request(WorldSaveTrigger.SaveFolder());
            Progenitor.Perf.FrameWork.Note(WorkName, Stopwatch.GetTimestamp() - start, 0);
            return;
        }
        StampPoller.Reset();
        // File-only (docs/bridge.md, "Logging policy"): it is written after every world save.
        Plugin.Logger.LogDiag("WorldSaveGuard: guarded state goes back "
            + (Suspension.TimedOut ? "(the save file never changed in " + SyncSaveSuspension.TimeoutSeconds + " s)." : "after the save was written."));
        Raise(Resume, "resume");
        Progenitor.Perf.FrameWork.Note(WorkName, Stopwatch.GetTimestamp() - start, 0);
    }

    private const string WorkName = "saveGuard";

    // Clock.Elapsed reads the Stopwatch's timestamp: safe from the worker thread while the clock runs.
    private static readonly SaveStampPoller StampPoller = new(WorldSaveTrigger.NewestSaveWriteUtc, () => Clock.Elapsed.TotalSeconds);

    private static void Raise(Action? handlers, string what)
    {
        if (handlers == null) return;
        foreach (Action h in handlers.GetInvocationList())
        {
            try { h(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("WorldSaveGuard: a " + what + " handler threw: " + e.Message); }
        }
    }
}
#endif
