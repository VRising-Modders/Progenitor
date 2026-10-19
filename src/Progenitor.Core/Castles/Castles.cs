using System;
using System.Collections.Generic;
using Progenitor.Events;

namespace Progenitor.Castles;

/// <summary>
/// Server-side castle reads as handles: <see cref="All"/>, <see cref="ForUser"/>,
/// <see cref="ForTerritory"/>, <see cref="At"/>. Each handle carries the heart, owner and
/// territory, enumerates the placed buildings, answers <c>IsInside</c>, and runs the read-only
/// half of the game's placement check (<see cref="CastlePlacement"/>).
///
/// <para>This is the mod-facing layer over <see cref="CastleReader"/>: the reader stays the raw
/// per-entity snapshot (<see cref="CastlePlot"/> / <see cref="CastleTile"/> /
/// <see cref="CastleBuilding"/> with Unity types), and this exposes the same world through
/// game-free types so plugin code, the sample command and the tests share one shape.</para>
///
/// <para>Every call is safe on the client: it returns an empty list or a disabled handle plus
/// one Debug line, the same contract as Effects and Units, so a shared plugin does not need a
/// side check. Read-only, so nothing is tracked on a <c>PluginContext</c> and unload has
/// nothing to reverse — the only process-lifetime state is the change detector's previous
/// snapshot, which <see cref="Uninstall"/> drops.</para>
///
/// <para><b>Main thread.</b> Reads touch <c>EntityManager</c>, so every entry point throws
/// <see cref="InvalidOperationException"/> off the game main thread, exactly as Units does.
/// Call from <c>Plugin.Frame</c>, a command handler, an EventBus handler, or
/// <c>MainThread.Post</c>.</para>
/// </summary>
public static class Castles
{
    /// <summary>SkipCoreModules token. Same string as <see cref="HarmonySlotHandoff.CastlesModule"/>.</summary>
    public const string ModuleName = HarmonySlotHandoff.CastlesModule;

    private static readonly object Gate = new();
    private static readonly List<CastleSnapshot> PlacedBuffer = new();
    private static readonly List<CastleSnapshot> DestroyedBuffer = new();
    private static ICastleRuntime? _runtime;
    private static IReadOnlyList<CastleSnapshot>? _previous;
    private static long _lastPollMs = long.MinValue;
    private static bool _installed;
    private static bool _skipped;
    private static bool _frameHooked;
    private static bool _clientLogged;
    private static int _pollCount;
    private static int _publishedPlaced;
    private static int _publishedDestroyed;

    /// <summary>
    /// Every castle on this server, ordered by territory index. Empty on the client, while the
    /// module is skipped, or before the world exists.
    /// </summary>
    public static IReadOnlyList<ICastleHandle> All
    {
        get
        {
            EnsureMainThread(nameof(All));
            var runtime = CurrentRuntime();
            if (!IsUsable(runtime, nameof(All)))
                return CastleEmpty.Handles;

            var rows = SnapshotSafe(runtime);
            if (rows.Count == 0) return CastleEmpty.Handles;

            var handles = new List<ICastleHandle>(rows.Count);
            for (int i = 0; i < rows.Count; i++)
                handles.Add(new CastleHandle(rows[i], runtime, enabled: true));
            return handles;
        }
    }

    /// <summary>
    /// The castle owned by <paramref name="user"/>, or by their clan when nobody in the pair
    /// owns one directly. Never null: a player with no castle gets a disabled handle, so
    /// <c>if (castle.Enabled)</c> is the only check a caller needs.
    /// </summary>
    public static ICastleHandle ForUser(CastleId user)
    {
        var all = AllForUser(user);
        return all.Count > 0 ? all[0] : CastleHandle.Disabled(_runtime);
    }

    /// <summary>
    /// Every castle owned by <paramref name="user"/> or by their clan, ordered by territory
    /// index — a clan can hold several plots. Empty when they hold none.
    /// </summary>
    public static IReadOnlyList<ICastleHandle> AllForUser(CastleId user)
    {
        EnsureMainThread(nameof(AllForUser));
        if (user.IsNone) return CastleEmpty.Handles;

        var runtime = CurrentRuntime();
        if (!IsUsable(runtime, nameof(AllForUser)))
            return CastleEmpty.Handles;

        var rows = SnapshotSafe(runtime);
        if (rows.Count == 0) return CastleEmpty.Handles;

        CastleId clan = CastleId.None;
        try
        {
            if (!runtime.TryGetClan(user, out clan))
                clan = CastleId.None;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Castles: clan lookup for " + user + " threw: " + e.Message);
            clan = CastleId.None;
        }

        var owned = new List<ICastleHandle>();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            bool mine = row.Owner.User == user
                        || (!clan.IsNone && row.Owner.Clan == clan);
            if (mine)
                owned.Add(new CastleHandle(row, runtime, enabled: true));
        }

        return owned.Count == 0 ? CastleEmpty.Handles : owned;
    }

    /// <summary>The castle holding <paramref name="territoryIndex"/>, or a disabled handle.</summary>
    public static ICastleHandle ForTerritory(int territoryIndex)
    {
        EnsureMainThread(nameof(ForTerritory));
        var runtime = CurrentRuntime();
        if (!IsUsable(runtime, nameof(ForTerritory)))
            return CastleHandle.Disabled(runtime);

        var rows = SnapshotSafe(runtime);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Territory.Index == territoryIndex)
                return new CastleHandle(rows[i], runtime, enabled: true);
        }

        return CastleHandle.Disabled(runtime);
    }

    /// <summary>The castle whose heart is <paramref name="heart"/>, or a disabled handle.</summary>
    public static ICastleHandle ForHeart(CastleId heart)
    {
        EnsureMainThread(nameof(ForHeart));
        var runtime = CurrentRuntime();
        if (heart.IsNone || !IsUsable(runtime, nameof(ForHeart)))
            return CastleHandle.Disabled(runtime);

        // One plot read for this heart: listing every castle per call made per-heart callers (the
        // servant hooks, once per heart with a hunt and update) scale with the castle count.
        try
        {
            if (runtime.TrySnapshotOfHeart(heart, out var one))
                return one != null ? new CastleHandle(one, runtime, enabled: true) : CastleHandle.Disabled(runtime);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Castles: single heart read for " + heart + " threw: " + e.Message + "; listing every castle instead");
        }

        var rows = SnapshotSafe(runtime);
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Heart == heart)
                return new CastleHandle(rows[i], runtime, enabled: true);
        }

        return CastleHandle.Disabled(runtime);
    }

    /// <summary>
    /// The castle whose territory contains <paramref name="position"/>, or a disabled handle.
    /// X/Z only (see <see cref="CastleGeometry"/>). Territories do not overlap, so the first
    /// match is the answer.
    /// </summary>
    public static ICastleHandle At(CastleVec3 position)
    {
        EnsureMainThread(nameof(At));
        var runtime = CurrentRuntime();
        if (!IsUsable(runtime, nameof(At)))
            return CastleHandle.Disabled(runtime);

        var rows = SnapshotSafe(runtime);
        for (int i = 0; i < rows.Count; i++)
        {
            var handle = new CastleHandle(rows[i], runtime, enabled: true);
            if (handle.IsInside(position))
                return handle;
        }

        return CastleHandle.Disabled(runtime);
    }

    /// <summary>
    /// The castle whose territory contains <paramref name="entity"/>, or a disabled handle. For a
    /// character this is the castle of <see cref="StandingTerritory(CastleId)"/> (see
    /// <see cref="ICastleHandle.IsInside(CastleId)"/>).
    /// </summary>
    public static ICastleHandle At(CastleId entity)
    {
        EnsureMainThread(nameof(At));
        var runtime = CurrentRuntime();
        if (entity.IsNone || !IsUsable(runtime, nameof(At)))
            return CastleHandle.Disabled(runtime);

        var rows = SnapshotSafe(runtime);
        for (int i = 0; i < rows.Count; i++)
        {
            var handle = new CastleHandle(rows[i], runtime, enabled: true);
            if (handle.IsInside(entity))
                return handle;
        }

        return CastleHandle.Disabled(runtime);
    }

    /// <summary>
    /// The territory index of the castle plot <paramref name="character"/> stands on, or -1 when it
    /// stands on none (open world, a stale or unknown entity, the client, the module skipped). The
    /// plot whose territory contains the character's <c>Translation</c>, X/Z only: every floor of the
    /// castle counts, and a teleport is reflected on the next read. This is the same value
    /// <c>MaterialContext.StandingTerritory</c> carries outside a game call that names its heart, so a
    /// mod's actions and its material views agree on where a player stands.
    /// </summary>
    public static int StandingTerritory(CastleId character)
    {
        EnsureMainThread(nameof(StandingTerritory));
        var runtime = CurrentRuntime();
        if (character.IsNone || !IsUsable(runtime, nameof(StandingTerritory))) return -1;

        CastleVec3 position;
        try
        {
            if (!runtime.TryGetPosition(character, out position)) return -1;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Castles: position of " + character + " threw: " + e.Message);
            return -1;
        }

        var rows = SnapshotSafe(runtime);
        for (int i = 0; i < rows.Count; i++)
        {
            var handle = new CastleHandle(rows[i], runtime, enabled: true);
            if (handle.IsInside(position)) return handle.Territory.Index;
        }

        return -1;
    }

    /// <summary>
    /// The standing-plot rule over an already fetched castle list: the first castle whose territory
    /// contains <paramref name="position"/> (X/Z only), else -1. Shared by
    /// <see cref="StandingTerritory(CastleId)"/> and the material context (<c>UnityMaterialWorld</c>).
    /// </summary>
    internal static int StandingTerritory(CastleVec3 position, IReadOnlyList<ICastleHandle> castles)
    {
        for (int i = 0; i < castles.Count; i++)
            if (castles[i].IsInside(position)) return castles[i].Territory.Index;
        return -1;
    }

    /// <summary>True when the module is loaded on a server world and not skipped.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_skipped) return false;
            var runtime = CurrentRuntime();
            return runtime.IsServer;
        }
    }

    internal static void Install()
    {
        lock (Gate)
        {
            if (IsModuleSkipped())
            {
                _skipped = true;
                _installed = true;
                Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(ModuleName));
                return;
            }

            _skipped = false;
            _installed = true;
            _previous = null;
            _lastPollMs = long.MinValue;
            _runtime ??= CreateDefaultRuntime();
        }

        HookFrame();
        Plugin.Logger?.LogInfo(_runtime is { IsServer: true }
            ? "Castles: installed (server)"
            : "Castles: installed (client no-op)");
    }

    internal static void Uninstall()
    {
        UnhookFrame();
        lock (Gate)
        {
            _previous = null;
            PlacedBuffer.Clear();
            DestroyedBuffer.Clear();
            _lastPollMs = long.MinValue;
            _installed = false;
            _skipped = false;
            _clientLogged = false;
            _runtime = null;
        }

        Plugin.Logger?.LogInfo("Castles: uninstalled");
    }

    internal static string DescribePins()
    {
        if (_skipped)
            return HarmonySlotHandoff.FormatModulePinsSkipped(ModuleName);

        var holds = new List<string>();
        lock (Gate)
        {
            if (_installed) holds.Add("installed");
            if (_frameHooked) holds.Add("frame");
            if (_previous != null) holds.Add("watched=" + _previous.Count);
            if (_pollCount > 0) holds.Add("polls=" + _pollCount);
            if (_publishedPlaced > 0) holds.Add("placed=" + _publishedPlaced);
            if (_publishedDestroyed > 0) holds.Add("destroyed=" + _publishedDestroyed);
        }

        return HarmonySlotHandoff.FormatModulePins(ModuleName, holds);
    }

    /// <summary>
    /// One tick of the change detector. Hooked to <c>Plugin.Frame</c> and rate-limited to
    /// <see cref="CastleWatch.DefaultIntervalSeconds"/>. Does nothing at all while nobody is
    /// subscribed to either event, so the module costs one integer compare per frame in the
    /// common case.
    /// </summary>
    internal static void Poll()
    {
        // Interval gate first: this runs every frame, so the common path is one lock on our own
        // object and an integer compare, not two EventBus lock acquisitions per frame.
        long now = Environment.TickCount64;
        lock (Gate)
        {
            if (!CastleWatch.ShouldPoll(now, _lastPollMs))
                return;
            _lastPollMs = now;
        }

        bool wanted;
        try
        {
            wanted = EventBus.SubscriberCount<CastleHeartPlaced>() > 0
                     || EventBus.SubscriberCount<CastleHeartDestroyed>() > 0;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Castles: subscriber count threw: " + e.Message);
            return;
        }

        if (!wanted)
        {
            // Drop the retained snapshot so the next subscriber gets a clean initial scan
            // rather than a diff against a world that may since have been reloaded.
            lock (Gate)
            {
                _previous = null;
                _lastPollMs = long.MinValue;
            }

            return;
        }

        var runtime = CurrentRuntime();
        if (!runtime.IsServer || _skipped)
            return;

        IReadOnlyList<CastleSnapshot> current = SnapshotSafe(runtime);
        bool initial;
        CastleSnapshot[] placed, destroyed;
        lock (Gate)
        {
            var previous = _previous;
            initial = previous == null;
            _previous = current;
            _pollCount++;
            CastleWatch.Diff(previous, current, PlacedBuffer, DestroyedBuffer);
            // Copy under the same lock: the shared buffers are reused every poll.
            placed = PlacedBuffer.ToArray();
            destroyed = DestroyedBuffer.ToArray();
        }

        // Publish outside the lock: a handler can call back into All / ForUser.

        for (int i = 0; i < placed.Length; i++)
        {
            try
            {
                EventBus.Publish(new CastleHeartPlaced(new CastleHandle(placed[i], runtime, enabled: true), initial));
                lock (Gate) _publishedPlaced++;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Castles: CastleHeartPlaced publish threw: " + e.Message);
            }
        }

        for (int i = 0; i < destroyed.Length; i++)
        {
            try
            {
                EventBus.Publish(new CastleHeartDestroyed(destroyed[i]));
                lock (Gate) _publishedDestroyed++;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Castles: CastleHeartDestroyed publish threw: " + e.Message);
            }
        }
    }

#if PROGENITOR_TESTS
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _runtime = null;
            _previous = null;
            PlacedBuffer.Clear();
            DestroyedBuffer.Clear();
            _lastPollMs = long.MinValue;
            _installed = false;
            _skipped = false;
            _frameHooked = false;
            _clientLogged = false;
            _pollCount = 0;
            _publishedPlaced = 0;
            _publishedDestroyed = 0;
        }
    }

    internal static void SetRuntimeForTests(ICastleRuntime runtime)
    {
        lock (Gate)
            _runtime = runtime ?? NullCastleRuntime.Instance;
    }

    internal static void SetSkippedForTests(bool skipped)
    {
        lock (Gate)
            _skipped = skipped;
    }

    /// <summary>Run one poll ignoring the interval gate.</summary>
    internal static void TickForTests()
    {
        lock (Gate)
            _lastPollMs = long.MinValue;
        Poll();
    }

    internal static int PollCountForTests
    {
        get { lock (Gate) return _pollCount; }
    }
#endif

    private static bool IsUsable(ICastleRuntime runtime, string op)
    {
        if (_skipped)
        {
            LogDebug("Castles." + op + " skipped (SkipCoreModules); returning nothing.");
            return false;
        }

        if (!runtime.IsServer)
        {
            if (!_clientLogged)
            {
                _clientLogged = true;
                LogDebug("Castles." + op + " is server-only; the client returns disabled handles. This is logged once.");
            }

            return false;
        }

        return true;
    }

    private static IReadOnlyList<CastleSnapshot> SnapshotSafe(ICastleRuntime runtime)
    {
        try
        {
            return runtime.Snapshot() ?? CastleEmpty.Snapshots;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Castles: snapshot failed: " + e);
            return CastleEmpty.Snapshots;
        }
    }

    private static ICastleRuntime CurrentRuntime()
    {
        lock (Gate)
        {
            if (_runtime != null) return _runtime;
            _runtime = CreateDefaultRuntime();
            return _runtime;
        }
    }

    private static ICastleRuntime CreateDefaultRuntime()
    {
#if PROGENITOR_TESTS
        return NullCastleRuntime.Instance;
#else
        if (!Plugin.IsServerProcess)
            return NullCastleRuntime.Instance;
        return new UnityCastleRuntime();
#endif
    }

    private static bool IsModuleSkipped()
    {
        if (HarmonySlotHandoff.SkipModulesRefusedLiveWorld) return false;
        if (HarmonySlotHandoff.ShouldSkipModule(ModuleName)) return true;
#if !PROGENITOR_TESTS
        string csv = BootstrapLink.SkipCoreModules;
        if (string.IsNullOrWhiteSpace(csv)) return false;
        var parts = csv.Split(',');
        for (int i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i].Trim(), ModuleName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
#endif
        return false;
    }

    private static void HookFrame()
    {
#if !PROGENITOR_TESTS
        if (_frameHooked) return;
        Plugin.Frame += Poll;
        _frameHooked = true;
#else
        _frameHooked = true;
#endif
    }

    private static void UnhookFrame()
    {
        if (!_frameHooked) return;
#if !PROGENITOR_TESTS
        Plugin.Frame -= Poll;
#endif
        _frameHooked = false;
    }

    /// <summary>
    /// Castle reads walk <c>EntityManager</c>, which is not thread-safe. Same rule and the same
    /// message shape as <c>Units.EnsureMainThread</c>. Tests skip the check (no captured
    /// main-thread id).
    /// </summary>
    internal static void EnsureMainThread(string op)
    {
#if PROGENITOR_TESTS
        _ = op;
#else
        if (Dev.MainThread.IsMainThread) return;
        throw new InvalidOperationException(
            "Castles." + op + " must run on the game main thread (Plugin.Frame, a command, or EventBus). EntityManager reads are not thread-safe; use MainThread.Post / Run to get there.");
#endif
    }

    internal static void LogDebug(string line)
    {
#if PROGENITOR_TESTS
        _ = line;
#else
        Plugin.Logger?.LogDebug(line);
#endif
    }
}
