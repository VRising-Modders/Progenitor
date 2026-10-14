#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Network;
using Progenitor.Castles;
using Progenitor.Ecs;
using Progenitor.Plugins;
using Unity.Entities;

namespace Progenitor.Clans;

/// <summary>
/// <see cref="PluginContext.Clans"/>: the clan of a user, its members and its castles. Server
/// only, main thread; the client throws. Read-only, so nothing is tracked.
///
/// <para>Membership is read from each <c>User.ClanEntity</c> (offline users included), the same
/// field the castle owner info uses; the clan's name and persistent id come from
/// <c>ClanTeam</c>.</para>
/// </summary>
public sealed class PluginClans
{
    private readonly PluginContext _ctx;

    internal PluginClans(PluginContext ctx) => _ctx = ctx ?? throw new ArgumentNullException(nameof(ctx));

    /// <summary>The clan <paramref name="user"/> belongs to; <see cref="ClanRef.None"/> when clanless.</summary>
    public ClanRef Of(Entity user)
    {
        var em = Require(nameof(Of));
        if (!user.Exists(em) || !user.Has<User>(em)) return ClanRef.None;
        var row = em.GetComponentData<User>(user);
        var clan = row.ClanEntity._Entity;
        return clan == Entity.Null ? ClanRef.None : Describe(em, clan);
    }

    /// <summary>The clan entity as a <see cref="ClanRef"/> (name and persistent id read now).</summary>
    public ClanRef FromEntity(Entity clan)
    {
        var em = Require(nameof(FromEntity));
        return clan == Entity.Null || !clan.Exists(em) ? ClanRef.None : Describe(em, clan);
    }

    /// <summary>Every user in <paramref name="clan"/>, online or not, ordered by name.</summary>
    public IReadOnlyList<ClanMember> Members(ClanRef clan)
    {
        var em = Require(nameof(Members));
        if (clan.IsNone) return Array.Empty<ClanMember>();
        return ClanLogic.Members(Users(em), clan.Entity);
    }

    /// <summary>Every castle whose owner's clan is <paramref name="clan"/>, ordered by territory index.</summary>
    public IReadOnlyList<ICastleHandle> Castles(ClanRef clan)
    {
        Require(nameof(Castles));
        if (clan.IsNone) return Array.Empty<ICastleHandle>();
        var rows = new List<ICastleHandle>();
        foreach (var castle in Progenitor.Castles.Castles.All)
            if (castle.Owner.Clan == clan.Entity) rows.Add(castle);
        return rows;
    }

    internal static List<ClanUserRow> Users(EntityManager em)
    {
        var rows = new List<ClanUserRow>();
        var users = Query.Entities(em, includeDisabled: true, includeSystems: false, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < users.Count; i++)
        {
            try
            {
                var u = em.GetComponentData<User>(users[i]);
                string name;
                try { name = u.CharacterName.ToString() ?? ""; } catch { name = ""; }
                rows.Add(new ClanUserRow(CastleId.From(users[i]), CastleId.From(u.ClanEntity._Entity), u.PlatformId, name, u.IsConnected));
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogDebug("Clans: user read on " + users[i].Index + ":" + users[i].Version + " threw: " + e.Message);
            }
        }

        return rows;
    }

    private static ClanRef Describe(EntityManager em, Entity clan)
    {
        string name = "";
        Guid guid = Guid.Empty;
        try
        {
            if (clan.Has<ClanTeam>(em))
            {
                var team = em.GetComponentData<ClanTeam>(clan);
                try { name = team.Name.ToString() ?? ""; } catch { name = ""; }
                try { Guid.TryParse(team.ClanGuid.ToString(), out guid); } catch { guid = Guid.Empty; }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogDebug("Clans: ClanTeam read on " + clan.Index + ":" + clan.Version + " threw: " + e.Message);
        }

        return new ClanRef(CastleId.From(clan), guid, name);
    }

    private EntityManager Require(string op)
    {
        _ctx.EnsureAttached();
        var world = Worlds.Server;
        if (!Plugin.IsServerProcess || world == null || !world.IsCreated)
            throw new InvalidOperationException("ctx.Clans." + op + " runs on the server side only.");
        if (!Dev.MainThread.IsMainThread)
            throw new InvalidOperationException("ctx.Clans." + op + " must run on the game main thread.");
        return world.EntityManager;
    }
}
#endif
