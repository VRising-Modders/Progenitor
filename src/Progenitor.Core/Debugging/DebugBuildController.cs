using System;
using System.Collections.Generic;
using Progenitor.Persistence;

namespace Progenitor.Debugging;

/// <summary>
/// Reads and writes the build settings on the game's debug settings singleton. The game half is
/// <c>DebugBuildRuntime</c> (raw guarded component access at <c>layout.txt</c> offsets); tests
/// pass a dictionary.
/// </summary>
internal interface IBuildSettingsAccess
{
    /// <summary>A snapshot of the switched settings by game field name. False when the singleton is missing.</summary>
    bool TryRead(out Func<string, bool> current);

    /// <summary>Write <paramref name="value"/> to each named setting this mode knows; other names are ignored.</summary>
    void Write(IReadOnlyList<string> names, bool value);
}

/// <summary>
/// The state machine behind <c>.debug build</c>, free of game types so the test project runs it
/// end to end: the command (status, on, off), the record kept in the save store, and what happens
/// after a Core install into a running world or a world load. One instance per Core generation;
/// main thread only.
/// </summary>
internal sealed class DebugBuildController
{
    private readonly IBuildSettingsAccess _settings;
    private readonly Func<IModStore?> _readyStore;
    private readonly string _processStamp;
    private readonly Action<string> _log;
    private bool _afterLoad;

    /// <param name="settings">The singleton access.</param>
    /// <param name="readyStore">Core's <c>progenitor.debug</c> store once the world save folder is known; null before.</param>
    /// <param name="processStamp">Identifies this server process (a restart changes it, a Core swap does not).</param>
    /// <param name="log">One log line per call.</param>
    internal DebugBuildController(IBuildSettingsAccess settings, Func<IModStore?> readyStore, string processStamp, Action<string> log)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _readyStore = readyStore ?? throw new ArgumentNullException(nameof(readyStore));
        _processStamp = processStamp ?? "";
        _log = log ?? (_ => { });
    }

    /// <summary>A leftover record still has to be adopted or restored; tick every frame while true.</summary>
    internal bool ResolvePending { get; private set; }

    /// <summary>
    /// Core installed. Into a running world (a Core swap, or a swap that landed after a load whose
    /// restore an earlier generation never ran): resolve the record by its process stamp on the
    /// next ready tick. Before any world: wait for the load.
    /// </summary>
    internal void Install(bool worldRunning)
    {
        if (worldRunning) ResolvePending = true;
    }

    /// <summary>A world finished loading in this generation: a record left in it is restored.</summary>
    internal void OnWorldLoad()
    {
        _afterLoad = true;
        ResolvePending = true;
    }

    /// <summary>
    /// Resolve a pending record once the store and the singleton are readable. Returns true when
    /// nothing is pending any more, false to be called again next frame.
    /// </summary>
    internal bool Tick()
    {
        if (!ResolvePending) return true;
        var store = _readyStore();
        if (store == null) return false;
        if (!_settings.TryRead(out var current)) return false;

        var state = ReadRecord(store, out var record);
        ResolvePending = false;
        if (state == DebugBuildMode.RecordState.Unreadable)
        {
            _log("Debug build: the build mode record in this save cannot be read; build settings left as they are "
                + "(on: " + Names(DebugBuildMode.PlanOffWithoutRecord(current)) + "). .debug build off turns them all off.");
            return true;
        }
        switch (DebugBuildMode.Decide(record, _afterLoad, _processStamp))
        {
            case DebugBuildMode.Recovery.Adopt:
            {
                // A record from a build that still switched a retired setting: turn it back now,
                // so pieces built from here on join their castle without an off/on.
                var retired = DebugBuildMode.RetiredToClear(current, record!.Names);
                var kept = DebugBuildMode.WithoutRetired(record.Names);
                _settings.Write(retired, false);
                if (kept.Count != record.Names.Count)
                    store.Set(DebugBuildMode.MarkerKey, DebugBuildMode.EncodeMarker(kept, _processStamp));
                _log("Debug build: adopted: build mode is on from the previous Core generation (" + Describe(record) + ")"
                    + (retired.Count == 0 ? "" : "; turned off retired " + Names(retired) + " (pieces built with it join no castle)")
                    + ".");
                break;
            }
            case DebugBuildMode.Recovery.RestoreAndClear:
                var plan = DebugBuildMode.PlanOff(current, record!.Names);
                _settings.Write(plan.Clear, false);
                store.Remove(DebugBuildMode.MarkerKey);
                _log("Debug build: " + (_afterLoad ? "world load" : "Core install after a server restart")
                    + ": build mode does not survive a world load; turned back " + Names(plan.Clear)
                    + UnknownNote(plan.Unknown) + " and dropped the record.");
                break;
        }
        return true;
    }

    /// <summary>Run <c>.debug build on|off|status</c>; every line goes to <paramref name="reply"/>.</summary>
    internal void Run(DebugAction action, string by, Action<string> reply)
    {
        if (!_settings.TryRead(out var current))
        {
            reply(DebugBuildMode.NoSingletonReply);
            return;
        }
        var store = _readyStore();
        var state = store == null ? DebugBuildMode.RecordState.NotReady : ReadRecord(store, out _);
        DebugBuildMode.Record? record = null;
        if (store != null && state == DebugBuildMode.RecordState.Present) ReadRecord(store, out record);

        if (action == DebugAction.BuildStatus)
        {
            foreach (var line in DebugBuildMode.StatusLines(state, record?.Names, current)) reply(line);
            return;
        }
        if (store == null)
        {
            reply(DebugBuildMode.NotReadyReply);
            return;
        }

        if (action == DebugAction.BuildOn)
        {
            if (state == DebugBuildMode.RecordState.Unreadable)
            {
                _log("Debug build: on by " + by + " refused: the record cannot be read and is left as it is.");
                reply(DebugBuildMode.UnreadableOnReply);
                return;
            }
            var plan = DebugBuildMode.PlanOn(current, record?.Names);
            // A retired setting an earlier build mode turned on goes first, while the old record
            // still names it; then the record, so a write that throws halfway still leaves every
            // changed setting named.
            _settings.Write(plan.ClearRetired, false);
            store.Set(DebugBuildMode.MarkerKey, DebugBuildMode.EncodeMarker(plan.Record, _processStamp));
            _settings.Write(plan.Write, true);
            _log("Debug build: on by " + by + ": wrote " + Names(plan.Write) + "; record " + Names(plan.Record)
                + (plan.ClearRetired.Count == 0 ? "" : "; turned off retired " + Names(plan.ClearRetired)) + ".");
            reply(DebugBuildMode.FormatOn(plan, record != null));
            if (_settings.TryRead(out var now))
            {
                string warning = DebugBuildMode.RetiredWarning(now, plan.Record);
                if (warning.Length > 0) reply(warning);
            }
        }
        else if (record != null)
        {
            var plan = DebugBuildMode.PlanOff(current, record.Names);
            _settings.Write(plan.Clear, false);
            store.Remove(DebugBuildMode.MarkerKey);
            _log("Debug build: off by " + by + ": turned back " + Names(plan.Clear) + UnknownNote(plan.Unknown) + ".");
            reply(DebugBuildMode.FormatOff(plan));
        }
        else
        {
            bool unreadable = state == DebugBuildMode.RecordState.Unreadable;
            var cleared = DebugBuildMode.PlanOffWithoutRecord(current);
            _settings.Write(cleared, false);
            if (unreadable) store.Remove(DebugBuildMode.MarkerKey);
            _log("Debug build: off by " + by + " with " + (unreadable ? "an unreadable" : "no") + " record: turned off "
                + Names(cleared) + ".");
            reply(DebugBuildMode.FormatOffWithoutRecord(cleared, unreadable));
        }
        if (_settings.TryRead(out var after))
        {
            if (action == DebugAction.BuildOff)
            {
                // Still on after off: nothing named it, so it is an operator's own; say so.
                string warning = DebugBuildMode.RetiredWarning(after, null);
                if (warning.Length > 0) reply(warning);
            }
            reply("Settings: " + DebugBuildMode.FormatSettings(after) + ".");
        }
    }

    /// <summary>What the record says now (for the one-line readout under <c>.debug</c>).</summary>
    internal DebugBuildMode.RecordState State()
    {
        var store = _readyStore();
        return store == null ? DebugBuildMode.RecordState.NotReady : ReadRecord(store, out _);
    }

    /// <summary>Absent when the key is not there; Unreadable when it is but does not decode.</summary>
    internal static DebugBuildMode.RecordState ReadRecord(IModStore store, out DebugBuildMode.Record? record)
    {
        record = null;
        try
        {
            bool has = false;
            foreach (var key in store.Keys)
                if (key == DebugBuildMode.MarkerKey) { has = true; break; }
            if (!has) return DebugBuildMode.RecordState.Absent;
            record = DebugBuildMode.DecodeMarker(store.Get<string>(DebugBuildMode.MarkerKey));
            return record == null ? DebugBuildMode.RecordState.Unreadable : DebugBuildMode.RecordState.Present;
        }
        catch
        {
            record = null;
            return DebugBuildMode.RecordState.Unreadable;
        }
    }

    private static string Names(List<string> names) => names.Count == 0 ? "nothing" : string.Join(",", names);

    private static string UnknownNote(List<string> unknown) =>
        unknown.Count == 0 ? "" : " (the record also names settings this build does not switch, left alone: " + string.Join(",", unknown) + ")";

    private static string Describe(DebugBuildMode.Record record) =>
        record.Names.Count == 0 ? "it changed nothing" : "it turned on " + string.Join(", ", record.Names);
}
