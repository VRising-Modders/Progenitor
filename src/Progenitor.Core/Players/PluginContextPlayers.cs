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
    /// Where every online player's character stands now: one <c>Translation</c> read per connected
    /// player with a live character, per call, and nothing between calls (no per-frame cost). Players
    /// still loading in (no character yet) are left out. Server only; throws on the client. Main thread.
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
    /// Where <paramref name="player"/>'s character stands now. False when the row is not online, has
    /// no character, is from an earlier world load, or the position cannot be read. Server only.
    /// </summary>
    public bool TryGetPosition(PlayerInfo player, out PlayerPosition position)
    {
        position = default;
        var world = RequireServer(nameof(TryGetPosition));
        if (player == null || !player.IsOnline || player.WorldEpoch != Progenitor.WorldState.WorldEpoch.Current) return false;
        return TryRead(world.EntityManager, player, out position);
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
