#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using ProjectM;
using Progenitor.Ai;
using Progenitor.Ecs;
using Progenitor.Persistence;
using Progenitor.Plugins;
using Progenitor.WorldState;
using Unity.Entities;

namespace Progenitor.Debugging;

/// <summary>
/// The interop half of <c>.debug build on|off</c>: reads and writes the game's own server debug
/// settings (<see cref="DebugBuildMode.Settings"/>) on the <c>SyncedServerDebugSettings</c>
/// singleton for <see cref="DebugBuildController"/>, and drives its lifetime (Core install, world
/// loads, the frame tick). Server only; main thread.
///
/// <para><b>Why the singleton and not the game's event.</b> The game's admin path
/// (<c>SetDebugSettingEvent</c> → <c>DebugEventsSystem.SetDebugSetting</c> →
/// <c>SyncedServerDebugSettings.SetSetting</c>) writes the same singleton field, and then also
/// rewrites the server's debug settings file (<c>SaveToSettingsFile</c>), so a toggle made that
/// way comes back after every restart and overwrites an operator's file. The handler and
/// <c>SetSetting</c> both take the settings struct by reference, which this framework does not
/// call. Writing the component is enough: the game copies the singleton into its shared static
/// every frame (<c>SetupSyncedServerDebugSettingsForWorld_Base</c>) and replicates its
/// <c>[SyncBool]</c> fields to every client. Reads and writes go through
/// <see cref="AiRuntime.RO{T}"/> / <see cref="AiRuntime.RW{T}"/> at <c>layout.txt</c> offsets,
/// the same access ctx.Time uses for <c>DayNightCycleDisabled</c>; no new generic interop
/// instantiation over a game value type.</para>
///
/// <para><b>Lifetime.</b> A Core swap keeps build mode: the uninstall writes nothing and the next
/// generation adopts the record when it was written by this server process. A world load, or a
/// record written by an earlier server process, restores what the record names.</para>
/// </summary>
internal static unsafe class DebugBuildRuntime
{
    private static readonly object Gate = new();
    private static DebugBuildController? _controller;
    private static bool _hooked;
    private static int _cacheEpoch;
    private static Entity _settings = Entity.Null;
    private static int _failuresLogged;

    /// <summary>This server process: a restart changes it, a Core swap does not.</summary>
    internal static string ProcessStamp()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return p.Id + "-" + p.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return Environment.ProcessId.ToString();
        }
    }

    internal static void Install(List<IDisposable> registrations)
    {
        if (_controller != null || !Plugin.IsServerProcess) return;
        _controller = new DebugBuildController(new Access(), ReadyStore, ProcessStamp(), Log);
        WorldEpoch.Advanced += OnAdvanced;
        registrations.Add(new ActionDisposable(Uninstall));
        var world = Worlds.Server;
        _controller.Install(world != null && world.IsCreated);
        lock (Gate) UpdateHook_NoLock();
    }

    /// <summary>
    /// Core uninstall (a swap or process exit). Writes nothing on purpose: build mode survives a
    /// Core swap, and a new process restores it from the record's process stamp.
    /// </summary>
    private static void Uninstall()
    {
        var controller = _controller;
        if (controller == null) return;
        WorldEpoch.Advanced -= OnAdvanced;
        try
        {
            if (controller.State() == DebugBuildMode.RecordState.Present)
                Log("Debug build: unload: build mode stays on across a Core swap; a world load or restart restores it.");
        }
        catch { /* the store may already be gone at process exit */ }
        lock (Gate)
        {
            _controller = null;
            UpdateHook_NoLock();
        }
        _settings = Entity.Null;
    }

    private static IModStore? ReadyStore() => DebugTools.StoreReady ? DebugTools.Store : null;

    private static void OnAdvanced(int epoch)
    {
        lock (Gate)
        {
            _controller?.OnWorldLoad();
            UpdateHook_NoLock();
        }
    }

    private static void UpdateHook_NoLock()
    {
        bool want = _controller != null && _controller.ResolvePending;
        if (want && !_hooked)
        {
            Plugin.Frame += Tick;
            _hooked = true;
        }
        else if (!want && _hooked)
        {
            try { Plugin.Frame -= Tick; } catch { /* Frame may already be gone */ }
            _hooked = false;
        }
    }

    private static void Tick()
    {
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return;
            var controller = _controller;
            if (controller == null || !controller.Tick()) return;
            lock (Gate) UpdateHook_NoLock();
        }
        catch (Exception e)
        {
            LogFailure("restore threw: " + e.Message);
        }
    }

    // ---- the command ---------------------------------------------------------------------------

    /// <summary>Run <c>.debug build on|off|status</c> and reply. <paramref name="by"/> names the admin for the log.</summary>
    internal static void Run(World world, DebugAction action, string by, Action<string> reply)
    {
        var controller = _controller;
        if (controller == null || world == null || !world.IsCreated)
        {
            reply(DebugBuildMode.NoSingletonReply);
            return;
        }
        controller.Run(action, by, reply);
    }

    /// <summary>The record's state, for the readout under bare <c>.debug</c>.</summary>
    internal static DebugBuildMode.RecordState State() =>
        _controller?.State() ?? DebugBuildMode.RecordState.NotReady;

    // ---- raw access ----------------------------------------------------------------------------

    private sealed class Access : IBuildSettingsAccess
    {
        public bool TryRead(out Func<string, bool> current)
        {
            current = _ => false;
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return false;
            var em = world.EntityManager;
            var entity = FindSettings(em);
            byte* p = entity == Entity.Null ? null : AiRuntime.RO<SyncedServerDebugSettings>(em, entity, DebugBuildMode.Extent);
            if (p == null) return false;
            var values = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var s in DebugBuildMode.Tracked()) values[s.Name] = p[s.Offset] != 0;
            current = name => values.TryGetValue(name, out bool v) && v;
            return true;
        }

        public void Write(IReadOnlyList<string> names, bool value)
        {
            if (names.Count == 0) return;
            var em = Worlds.Server!.EntityManager;
            var entity = FindSettings(em);
            byte* rw = AiRuntime.RW<SyncedServerDebugSettings>(em, entity, DebugBuildMode.Extent);
            foreach (var name in names)
            {
                int offset = DebugBuildMode.OffsetOf(name);
                if (offset >= 0) rw[offset] = value ? (byte)1 : (byte)0;
            }
        }
    }

    private static Entity FindSettings(EntityManager em)
    {
        int epoch = WorldEpoch.Current;
        if (epoch != _cacheEpoch)
        {
            _cacheEpoch = epoch;
            _settings = Entity.Null;
        }
        if (_settings != Entity.Null && AiRuntime.RO<SyncedServerDebugSettings>(em, _settings, DebugBuildMode.Extent) != null)
            return _settings;
        var type = ComponentIndex.Of<SyncedServerDebugSettings>(ComponentType.AccessMode.ReadOnly);
        var found = Query.Entities(em, includeDisabled: false, includeSystems: false, type);
        if (found.Count == 0) found = Query.Entities(em, includeDisabled: true, includeSystems: true, type);
        _settings = found.Count == 0 ? Entity.Null : found[0];
        return _settings;
    }

    private static void Log(string line) => Plugin.Logger?.LogInfo(line);

    private static void LogFailure(string line)
    {
        if (_failuresLogged >= 8) return;
        _failuresLogged++;
        Plugin.Logger?.LogWarning("Debug build: " + line);
    }
}
#endif
