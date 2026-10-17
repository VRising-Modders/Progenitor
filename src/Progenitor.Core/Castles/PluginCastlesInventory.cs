#if !PROGENITOR_TESTS
using System;
using ProjectM.Network;
using Progenitor.Ai;
using Progenitor.Castles;
using Progenitor.Clans;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Plugins;

/// <summary>Castle relations and nameplates (milestone P-a). Server only, main thread.</summary>
public sealed partial class PluginCastles
{
    /// <summary>
    /// How <paramref name="user"/> stands to <paramref name="castle"/>: owner, member of the
    /// owner's clan, allied by the game's team check (<c>Team.IsAllies</c> between the user's
    /// character and the heart, the check the game uses for castle access), or none. A disabled
    /// handle answers <see cref="CastleRelation.None"/>.
    /// </summary>
    public CastleRelation RelationTo(Entity user, ICastleHandle castle) => RelationOf(RequireServer(nameof(RelationTo)), user, castle);

    /// <summary>
    /// <see cref="RelationTo"/> without a plugin context, for Core-owned server checks (the container preview's
    /// authorization). Server world, main thread.
    /// </summary>
    internal static CastleRelation RelationOf(World world, Entity user, ICastleHandle castle)
    {
        if (castle == null) return CastleRelation.None;
        // Re-read the castle now: a handle's owner and clan are a snapshot, and ownership or clan
        // membership may have changed since it was taken.
        castle = castle.Refresh();
        if (!castle.Enabled) return CastleRelation.None;
        var em = world.EntityManager;
        if (!user.Exists(em) || !user.Has<User>(em)) return CastleRelation.None;
        var row = em.GetComponentData<User>(user);
        var clan = CastleId.From(row.ClanEntity._Entity);

        bool? allied = null;
        try
        {
            var character = row.LocalCharacter._Entity;
            var heart = castle.Heart.ToEntity();
            if (AiRuntime.TryTeam(em, character, out int va, out int fa) && AiRuntime.TryTeam(em, heart, out int vb, out int fb))
            {
                var table = AiRuntime.Factions(world);
                allied = FactionMath.IsAllies(va, fa, vb, fb, index =>
                    table.TryGetValue(index, out var info) ? (info.Membership, info.Friendly) : null);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogDebug("Castles: team check for " + user.Index + ":" + user.Version + " threw: " + e.Message);
        }

        return ClanLogic.Relation(CastleId.From(user), clan, castle.Owner, allied);
    }

    /// <summary>
    /// The player-set name on a chest, station or heart (<c>NameableInteractable.Name</c>); null
    /// when the entity has no nameplate, empty when it is unnamed.
    /// </summary>
    public string? Nameplate(Entity entity)
    {
        var world = RequireServer(nameof(Nameplate));
        return CastleNameplates.TryRead(world.EntityManager, entity, out bool nameable, out string name) && nameable ? name : null;
    }

    /// <summary>
    /// Rename a chest, station or heart. Server write of the networked component, so every client
    /// sees it. Refused (false with a reason) for an entity without a nameplate, a name longer than
    /// 61 UTF-8 bytes, or control characters. An empty name clears it.
    /// </summary>
    public bool TrySetNameplate(Entity entity, string name, out string reason)
    {
        var world = RequireServer(nameof(TrySetNameplate));
        return CastleNameplates.TryWrite(world.EntityManager, entity, name, out reason);
    }

    /// <summary>Read a building's actual world position, including its floor elevation.</summary>
    public bool TryGetBuildingPosition(Entity entity, out Progenitor.Units.UnitVec3 position)
    {
        var em = RequireServer(nameof(TryGetBuildingPosition)).EntityManager;
        position = default;
        if (!entity.Exists(em) || !entity.TryRead<Unity.Transforms.Translation>(em, out var value)) return false;
        position = new Progenitor.Units.UnitVec3(value.Value.x, value.Value.y, value.Value.z);
        return true;
    }

    /// <summary>Game floor-height encoding for a building, or -1 if it has no tile position.</summary>
    public int BuildingFloorHeight(Entity entity)
    {
        var em = RequireServer(nameof(BuildingFloorHeight)).EntityManager;
        return entity.Exists(em) && entity.TryRead<ProjectM.TilePosition>(em, out var tile) ? tile.CompressedHeight : -1;
    }

    private World RequireServer(string op)
    {
        _ctx.EnsureAttached();
        var world = Worlds.Server;
        if (!Progenitor.Plugin.IsServerProcess || world == null || !world.IsCreated)
            throw new InvalidOperationException("ctx.Castles." + op + " runs on the server side only.");
        if (!Dev.MainThread.IsMainThread)
            throw new InvalidOperationException("ctx.Castles." + op + " must run on the game main thread.");
        return world;
    }
}
#endif
