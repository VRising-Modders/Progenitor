using System;
using System.Collections.Generic;
using ProjectM.Network;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.WorldState;
using Unity.Entities;

namespace Progenitor.Players;

/// <summary>
/// Server-side player directory (basic-authoring A3): online enumeration and lookup by
/// account id (<c>User.PlatformId</c>) or display name. Stable identity is the account id —
/// never a persisted raw entity handle. Cached results carry <see cref="PlayerInfo.WorldEpoch"/>
/// and <see cref="PlayerInfo.DirectoryRevision"/>; either moving means the session refs are
/// stale. The cache invalidates on <see cref="PlayerConnected"/>, <see cref="PlayerDisconnected"/>,
/// <see cref="WorldLoaded"/>, rename (detected on refresh), and Core reload.
///
/// <para>Duplicate / ambiguous names return <see cref="PlayerLookupStatus.Ambiguous"/> with
/// candidates — never an arbitrary single match. Offline rows may appear in account lookup when
/// <c>onlineOnly: false</c>; that discovery does not imply an operable character.</para>
///
/// <para>Main thread: reads touch <c>EntityManager</c>, so entry points throw
/// <see cref="InvalidOperationException"/> off the game main thread (same contract as Castles /
/// Units). Call from a command handler, EventBus handler, <c>Plugin.Frame</c>, or
/// <c>MainThread.Post</c>.</para>
/// </summary>
public static class PlayerDirectory
{
    private static readonly object Gate = new();
    private static readonly PlayerDirectoryCache Cache = new();
    private static readonly List<PlayerDirectoryRow> ScanBuffer = new();
    private static readonly List<PlayerDirectoryRow> OnlineBuffer = new();
    private static readonly List<int> HitBuffer = new();
    private static IDisposable? _connected;
    private static IDisposable? _disconnected;
    private static IDisposable? _worldLoaded;
    private static bool _installed;
    private static bool _skipped;

    /// <summary>Current directory revision (bumps on invalidate).</summary>
    public static int Revision
    {
        get { lock (Gate) return Cache.Revision; }
    }

    /// <summary>True when the module is installed on a server and not skipped.</summary>
    public static bool IsAvailable
    {
        get
        {
            lock (Gate)
            {
                return _installed && !_skipped && Plugin.IsServerProcess;
            }
        }
    }

    /// <summary>
    /// Hook connect / disconnect / world-load invalidation. Called from
    /// <see cref="PlayerControl.Install"/> so SkipCoreModules covers both.
    /// </summary>
    internal static void Install()
    {
        lock (Gate)
        {
            if (_installed) return;
            if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.PlayersModule))
            {
                _skipped = true;
                _installed = true;
                return;
            }

            _skipped = false;
            _installed = true;
            Cache.ResetForTests();
        }

        try
        {
            _connected = EventBus.On<PlayerConnected>(OnConnected, priority: 500);
            _disconnected = EventBus.On<PlayerDisconnected>(OnDisconnected, priority: 500);
            _worldLoaded = EventBus.On<WorldLoaded>(OnWorldLoaded, priority: 500);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("PlayerDirectory: EventBus subscribe failed: " + e);
        }

        Plugin.Logger?.LogInfo(Plugin.IsServerProcess
            ? "PlayerDirectory: installed (server; invalidate on connect/disconnect/world)"
            : "PlayerDirectory: installed (client no-op reads)");
    }

    /// <summary>Drop bus subscriptions and clear the cache (Core unload / swap).</summary>
    internal static void Uninstall()
    {
        try { _connected?.Dispose(); } catch { /* best effort */ }
        try { _disconnected?.Dispose(); } catch { /* best effort */ }
        try { _worldLoaded?.Dispose(); } catch { /* best effort */ }
        _connected = null;
        _disconnected = null;
        _worldLoaded = null;

        lock (Gate)
        {
            Cache.Invalidate(PlayerDirectoryLogic.ReasonReload);
            Cache.ResetForTests();
            _installed = false;
            _skipped = false;
        }

        Plugin.Logger?.LogInfo("PlayerDirectory: uninstalled");
    }

    /// <summary>Holds for the Players module pin line.</summary>
    internal static void AppendPinHolds(List<string> holds)
    {
        lock (Gate)
        {
            if (!_installed) return;
            if (_skipped) { holds.Add("directory=skipped"); return; }
            holds.Add("directory");
            if (_connected != null) holds.Add("dir-connect");
            if (_disconnected != null) holds.Add("dir-disconnect");
            if (_worldLoaded != null) holds.Add("dir-world");
            holds.Add("dir-rev=" + Cache.Revision);
            if (Cache.LastInvalidateReason.Length > 0)
                holds.Add("dir-last=" + Cache.LastInvalidateReason);
        }
    }

    /// <summary>Bump revision and drop rows. Public so Dev / tests can force a clear.</summary>
    public static void Invalidate(string reason)
    {
        lock (Gate)
        {
            Cache.Invalidate(string.IsNullOrEmpty(reason) ? PlayerDirectoryLogic.ReasonReload : reason);
        }
    }

    /// <summary>
    /// Every currently connected player with a non-zero <c>PlatformId</c>, ordered as scanned.
    /// Empty on the client, when skipped, or when the world is missing.
    /// </summary>
    public static IReadOnlyList<PlayerInfo> Online(World? world = null)
    {
        EnsureMainThread(nameof(Online));
        var snap = Refresh(world);
        var list = new List<PlayerInfo>();
        PlayerDirectoryLogic.CollectOnline(snap.Rows, OnlineBuffer);
        for (int i = 0; i < OnlineBuffer.Count; i++)
            list.Add(OnlineBuffer[i].ToInfo(snap.WorldEpoch, snap.Revision));
        return list;
    }

    /// <summary>
    /// Lookup by display name. Whole-name, case-insensitive. Ambiguous names return candidates
    /// and no single <see cref="PlayerNameLookup.Player"/>.
    /// </summary>
    public static PlayerNameLookup FindByName(string name, bool onlineOnly = true, World? world = null)
    {
        EnsureMainThread(nameof(FindByName));
        var snap = Refresh(world);
        var status = PlayerDirectoryLogic.MatchByName(snap.Rows, name, onlineOnly, HitBuffer);
        bool fresh = snap.IsFresh(WorldEpoch.Current, CacheRevisionUnlocked());
        return PlayerDirectoryLogic.ToNameLookup(status, snap.Rows, HitBuffer, snap.WorldEpoch, snap.Revision, fresh);
    }

    /// <summary>
    /// True when exactly one name match exists. False on miss or ambiguous — use
    /// <see cref="FindByName"/> for candidates.
    /// </summary>
    public static bool TryGetByName(string name, out PlayerInfo player, bool onlineOnly = true, World? world = null)
    {
        var found = FindByName(name, onlineOnly, world);
        if (found.Status == PlayerLookupStatus.Exact && found.Player != null)
        {
            player = found.Player;
            return true;
        }

        player = null!;
        return false;
    }

    /// <summary>
    /// Lookup by stable account id (<c>User.PlatformId</c>). When <paramref name="onlineOnly"/>
    /// is false, an offline User row may be returned; that is identity discovery only.
    /// </summary>
    public static bool TryGetByAccount(ulong platformId, out PlayerInfo player, bool onlineOnly = false, World? world = null)
    {
        EnsureMainThread(nameof(TryGetByAccount));
        player = null!;
        var snap = Refresh(world);
        int index = PlayerDirectoryLogic.FindByAccount(snap.Rows, platformId, onlineOnly);
        if (index < 0) return false;
        player = snap.Rows[index].ToInfo(snap.WorldEpoch, snap.Revision);
        return true;
    }

    /// <summary>Same as <see cref="TryGetByAccount(ulong, out PlayerInfo, bool, World?)"/>.</summary>
    public static bool TryGetByAccount(PlayerAccountId account, out PlayerInfo player, bool onlineOnly = false, World? world = null)
        => TryGetByAccount(account.Value, out player, onlineOnly, world);

    /// <summary>
    /// True when <paramref name="info"/> was stamped in the current world epoch and directory
    /// revision. A false result means session entity refs must not be used for mutation.
    /// </summary>
    public static bool IsFresh(PlayerInfo? info)
    {
        if (info == null) return false;
        int epoch;
        int revision;
        lock (Gate)
        {
            epoch = WorldEpoch.Current;
            revision = Cache.Revision;
        }
        return PlayerDirectoryLogic.IsFresh(info.WorldEpoch, info.DirectoryRevision, epoch, revision);
    }

    static int CacheRevisionUnlocked()
    {
        lock (Gate) return Cache.Revision;
    }

    static PlayerDirectorySnapshot Refresh(World? world)
    {
        lock (Gate)
        {
            if (_skipped || !Plugin.IsServerProcess)
                return new PlayerDirectorySnapshot(WorldEpoch.Current, Cache.Revision, Array.Empty<PlayerDirectoryRow>());
        }

        world ??= Worlds.Server;
        if (world == null || !world.IsCreated)
        {
            lock (Gate)
            {
                return Cache.Capture();
            }
        }

        ScanBuffer.Clear();
        try
        {
            ScanUsers(world.EntityManager, ScanBuffer);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PlayerDirectory: scan failed: " + e.Message);
            lock (Gate) return Cache.Capture();
        }

        int epoch = WorldEpoch.Current;
        lock (Gate)
        {
            Cache.Replace(epoch, ScanBuffer);
            return Cache.Capture();
        }
    }

    static void ScanUsers(EntityManager em, List<PlayerDirectoryRow> destination)
    {
        // includeDisabled: true — offline characters may be disabled; User itself is not, but
        // the option keeps the walk consistent with CharacterLookup / .ecs users.
        var users = Query.Entities(
            em,
            includeDisabled: true,
            includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));

        for (int i = 0; i < users.Count; i++)
        {
            var entity = users[i];
            User user;
            try { user = em.GetComponentData<User>(entity); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(
                    "PlayerDirectory: User read on " + entity.Index + ":" + entity.Version + ": " + e.Message);
                continue;
            }

            if (!PlayerDirectoryLogic.IsValidAccount(user.PlatformId))
                continue;

            string name;
            try { name = user.CharacterName.ToString() ?? ""; }
            catch { name = ""; }

            int charIndex = 0;
            int charVersion = 0;
            try
            {
                var character = user.LocalCharacter._Entity;
                if (character != Entity.Null)
                {
                    charIndex = character.Index;
                    charVersion = character.Version;
                }
            }
            catch { /* LocalCharacter unreadable */ }

            int slot = -1;
            if (user.IsConnected)
            {
                try
                {
                    int index = user.Index;
                    if (index >= 0 && index <= 127) slot = index;
                }
                catch { slot = -1; }
            }

            destination.Add(new PlayerDirectoryRow(
                user.PlatformId,
                name,
                user.IsConnected,
                slot,
                entity.Index,
                entity.Version,
                charIndex,
                charVersion));
        }
    }

    static void OnConnected(PlayerConnected _)
        => Invalidate(PlayerDirectoryLogic.ReasonConnect);

    static void OnDisconnected(PlayerDisconnected _)
        => Invalidate(PlayerDirectoryLogic.ReasonDisconnect);

    static void OnWorldLoaded(WorldLoaded _)
        => Invalidate(PlayerDirectoryLogic.ReasonWorld);

    static void EnsureMainThread(string op)
    {
        if (Dev.MainThread.IsMainThread) return;
        throw new InvalidOperationException(
            "PlayerDirectory." + op + " must run on the game main thread (Plugin.Frame, a command handler, EventBus, or MainThread.Post).");
    }
}
