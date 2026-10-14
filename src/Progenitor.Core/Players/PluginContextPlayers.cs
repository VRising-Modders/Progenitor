#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Plugins;
using Progenitor.Ecs;
using Unity.Entities;
using Unity.Transforms;

namespace Progenitor.Players;

/// <summary>
/// <see cref="PluginContext.Players"/> — server player directory bound to this plugin's world.
/// Read-only; unload has nothing to reverse. Offline identity discovery does not imply an
/// operable character. Session entity refs on a returned <see cref="PlayerInfo"/> are only
/// valid while <see cref="PlayerDirectory.IsFresh"/> is true.
/// </summary>
public sealed class PluginPlayers
{
    private readonly PluginContext _ctx;

    internal PluginPlayers(PluginContext ctx) =>
        _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    /// <summary>Connected players with a non-zero <c>PlatformId</c>.</summary>
    public IReadOnlyList<PlayerInfo> Online()
    {
        _ctx.EnsureAttached();
        return PlayerDirectory.Online(_ctx.World);
    }

    /// <summary>
    /// Lookup by display name. Ambiguous names return candidates, not an arbitrary match.
    /// </summary>
    public PlayerNameLookup FindByName(string name, bool onlineOnly = true)
    {
        _ctx.EnsureAttached();
        return PlayerDirectory.FindByName(name, onlineOnly, _ctx.World);
    }

    /// <summary>True when exactly one name match exists.</summary>
    public bool TryGetByName(string name, out PlayerInfo player, bool onlineOnly = true)
    {
        _ctx.EnsureAttached();
        return PlayerDirectory.TryGetByName(name, out player, onlineOnly, _ctx.World);
    }

    /// <summary>Lookup by stable account id (<c>User.PlatformId</c>).</summary>
    public bool TryGetByAccount(ulong platformId, out PlayerInfo player, bool onlineOnly = false)
    {
        _ctx.EnsureAttached();
        return PlayerDirectory.TryGetByAccount(platformId, out player, onlineOnly, _ctx.World);
    }

    /// <summary>Lookup by <see cref="PlayerAccountId"/>.</summary>
    public bool TryGetByAccount(PlayerAccountId account, out PlayerInfo player, bool onlineOnly = false)
    {
        _ctx.EnsureAttached();
        return PlayerDirectory.TryGetByAccount(account, out player, onlineOnly, _ctx.World);
    }

    /// <summary>True when <paramref name="info"/> is still current for mutation.</summary>
    public bool IsFresh(PlayerInfo? info)
    {
        _ctx.EnsureAttached();
        return PlayerDirectory.IsFresh(info);
    }

    /// <summary>
    /// Where every online player's character stands now: the player directory is re-read and one
    /// <c>Translation</c> is read per connected player with a live character, on every call; nothing
    /// runs between calls. Meant for on-demand use or a slow tick (seconds), not per-frame polling.
    /// Players still loading in (no character yet) are left out. Server only; throws on the client.
    /// Main thread.
    /// </summary>
    public IReadOnlyList<PlayerPosition> OnlinePositions()
    {
        var world = RequireServer(nameof(OnlinePositions));
        var em = world.EntityManager;
        var online = PlayerDirectory.Online(world);
        var list = new List<PlayerPosition>(online.Count);
        for (int i = 0; i < online.Count; i++)
            if (TryRead(em, online[i], out var position)) list.Add(position);
        return list;
    }

    /// <summary>
    /// Where <paramref name="player"/>'s character stands now. The account is looked up again in
    /// the directory, so a row kept from earlier never answers for a player who has since
    /// disconnected or changed character: false when the account is not online now, has no
    /// character, the row is from an earlier world load, or the position cannot be read. The
    /// returned position carries the current row. Re-reads the directory; on-demand use. Server only.
    /// </summary>
    public bool TryGetPosition(PlayerInfo player, out PlayerPosition position)
    {
        position = default;
        var world = RequireServer(nameof(TryGetPosition));
        if (player == null) return false;
        PlayerDirectory.TryGetByAccount(player.Account, out PlayerInfo now, onlineOnly: true, world);
        var current = PlayerPositionMath.CurrentRow(player, now, Progenitor.WorldState.WorldEpoch.Current);
        return current != null && TryRead(world.EntityManager, current, out position);
    }

    private static bool TryRead(EntityManager em, PlayerInfo player, out PlayerPosition position)
    {
        position = default;
        var s = player.Session;
        if (!s.HasCharacter) return false;
        var character = new Entity { Index = s.CharacterIndex, Version = s.CharacterVersion };
        try
        {
            if (!em.Exists(character) || !character.TryRead(em, out Translation t)) return false;
            position = new PlayerPosition(player, t.Value.x, t.Value.y, t.Value.z);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private World RequireServer(string op)
    {
        _ctx.EnsureAttached();
        if (!_ctx.IsServer)
            throw new InvalidOperationException("ctx.Players." + op + " is server-only.");
        var world = _ctx.World;
        if (world == null || !world.IsCreated)
            throw new InvalidOperationException("ctx.Players." + op + ": the server world does not exist yet.");
        return world;
    }

    /// <summary>
    /// The player's combat and form state now: <see cref="PlayerStateInfo.Dead"/>,
    /// <see cref="PlayerStateInfo.Downed"/>, <see cref="PlayerStateInfo.InPvpCombat"/>,
    /// <see cref="PlayerStateInfo.ShapeshiftKind"/> and more, read from the live character. The
    /// <paramref name="player"/> row is checked against the world first: a row from an earlier
    /// world load, or a user entity that now belongs to another account, gives
    /// <see cref="PlayerStateStatus.Stale"/>. Offline players are read too (their character is
    /// disabled). Server only, main thread; a client call returns
    /// <see cref="PlayerStateStatus.Unavailable"/>. Never throws for a bad or stale player.
    /// <b>Experimental</b>: game-free tested, not live-verified.
    /// </summary>
    public PlayerStateInfo State(PlayerInfo? player)
    {
        _ctx.EnsureAttached();
        EnsureMainThread();
        return PlayerStateReader.Read(_ctx.World, player);
    }

    /// <summary>
    /// <see cref="State(PlayerInfo)"/> for a <c>User</c> entity (not the character). An entity
    /// that no longer exists is <see cref="PlayerStateStatus.Stale"/>; one without a <c>User</c>
    /// component is <see cref="PlayerStateStatus.NotAPlayer"/>.
    /// </summary>
    public PlayerStateInfo State(Entity user)
    {
        _ctx.EnsureAttached();
        EnsureMainThread();
        return PlayerStateReader.Read(_ctx.World, user);
    }

    private static void EnsureMainThread()
    {
        if (!Dev.MainThread.IsMainThread)
            throw new InvalidOperationException(
                "ctx.Players.State must run on the game main thread (Plugin.Frame, a command handler, EventBus, or MainThread.Post).");
    }

    /// <summary>Current directory cache revision.</summary>
    public int Revision
    {
        get
        {
            _ctx.EnsureAttached();
            return PlayerDirectory.Revision;
        }
    }
}
#endif
