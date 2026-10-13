#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Plugins;
using Unity.Entities;

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
