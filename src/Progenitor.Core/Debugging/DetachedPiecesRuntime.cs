#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using ProjectM.Shared;
using ProjectM.Tiles;
using Progenitor.Ai;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;
using Unity.Transforms;

namespace Progenitor.Debugging;

/// <summary>
/// The interop half of <c>.debug build detached [remove [all]]</c> (<see cref="DetachedPieces"/>):
/// one query of every tile model with <c>CastleHeartConnection</c>, <c>CastleAreaRequirement</c>
/// and <c>UserOwner</c> (disabled ones included: castle pieces go disabled when no player is near),
/// raw read-only reads at <c>layout.txt</c> offsets, and removal through the game's own
/// <c>DestroyUtility.Destroy</c>, never a hand-added <c>DestroyTag</c>. Server only; main thread.
/// </summary>
internal static unsafe class DetachedPiecesRuntime
{
    // Entity {Index, Version} at the start of a NetworkedEntity (layout.txt NetworkedEntity notes).
    private const int EntityBytes = 8;

    internal static void Run(World world, Entity caller, DebugAction action, string piece, int confirm, string by, Action<string> reply)
    {
        piece ??= "";
        if (world == null || !world.IsCreated)
        {
            reply(DebugBuildMode.NoSingletonReply);
            return;
        }
        var em = world.EntityManager;
        string callerKey = Key(caller);
        var detached = DetachedPieces.Detached(Read(world, em, out var entities));

        if (action == DebugAction.BuildDetached)
        {
            Log("Debug build: detached listed by " + by + ": " + detached.Count + " piece(s).");
            foreach (var row in detached) Log("Debug build: detached " + DetachedPieces.Describe(row) + ".");
            foreach (var line in DetachedPieces.ListLines(detached, callerKey)) reply(line);
            return;
        }

        bool all = action == DebugAction.BuildDetachedRemoveAll;
        var take = DetachedPieces.ToRemove(detached, callerKey, all, piece);
        string refusal = DetachedPieces.ConfirmRefusal(take.Count, confirm, all, piece);
        if (refusal.Length > 0)
        {
            Log("Debug build: detached remove by " + by + " not confirmed: " + take.Count + " match, count given " + confirm + ".");
            reply(refusal);
            return;
        }
        var removed = new List<DetachedPieces.Row>();
        var holding = new List<DetachedPieces.Row>();
        int failed = 0;
        foreach (var row in take)
        {
            try
            {
                var e = entities[row.Id];
                // Re-check right before the destroy: still there, still detached, not already going.
                string why = !em.Exists(e) ? "it no longer exists"
                    : e.Has<DestroyTag>(em) ? "it is already being destroyed"
                    : !IsStillDetached(em, e) ? "it is no longer detached"
                    : "";
                if (why.Length > 0)
                {
                    failed++;
                    Plugin.Logger?.LogWarning("Debug build: detached " + row.Id + " not removed: " + why + ".");
                    continue;
                }
                // The destroy drops nothing and refunds nothing: a piece that may hold items stays.
                if (Progenitor.Items.Items.MayHoldItems(em, e))
                {
                    holding.Add(row with { MayHoldItems = true });
                    Log("Debug build: detached " + row.Id + " not removed: it may hold items.");
                    continue;
                }
                DestroyUtility.Destroy(em, e, DestroyDebugReason.DestroyTileModels);
                removed.Add(row);
                Log("Debug build: detached removed by " + by + ": " + DetachedPieces.Describe(row) + ".");
            }
            catch (Exception ex)
            {
                failed++;
                Plugin.Logger?.LogWarning("Debug build: removing detached " + row.Id + " threw: " + ex.Message);
            }
        }
        foreach (var line in DetachedPieces.RemoveLines(removed, holding, failed, detached.Count, all, piece)) reply(line);
    }

    private static List<DetachedPieces.Row> Read(World world, EntityManager em, out Dictionary<string, Entity> byId)
    {
        byId = new Dictionary<string, Entity>(StringComparer.Ordinal);
        var rows = new List<DetachedPieces.Row>();
        var found = Query.Entities(em, includeDisabled: true, includeSystems: false,
            ComponentIndex.Of<TileModel>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<CastleHeartConnection>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<CastleAreaRequirement>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<UserOwner>(ComponentType.AccessMode.ReadOnly));
        foreach (var e in found)
        {
            try
            {
                if (!TryReadPiece(em, e, out byte requirement, out var heart, out var owner)) continue;
                // Only a candidate is worth the name and position reads.
                var probe = new DetachedPieces.Row("", "", 0, 0, 0, requirement, heart != Entity.Null, owner != Entity.Null, "", "");
                if (!DetachedPieces.IsDetached(probe)) continue;

                string prefab = "";
                if (e.TryRead(em, out PrefabGUID guid) && guid._Value != 0) PrefabLookup.TryGetName(world, guid, out prefab);
                float x = 0, y = 0, z = 0;
                if (e.TryRead(em, out Translation t))
                {
                    x = t.Value.x;
                    y = t.Value.y;
                    z = t.Value.z;
                }
                string id = Key(e);
                byId[id] = e;
                rows.Add(new DetachedPieces.Row(id, prefab ?? "", x, y, z, requirement, false, true, Key(owner), OwnerName(em, owner),
                    Progenitor.Items.Items.MayHoldItems(em, e)));
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning("Debug build: reading piece " + Key(e) + " threw: " + ex.Message);
            }
        }
        return rows;
    }

    private static bool IsStillDetached(EntityManager em, Entity e) =>
        TryReadPiece(em, e, out byte requirement, out var heart, out var owner)
        && DetachedPieces.IsDetached(new DetachedPieces.Row("", "", 0, 0, 0, requirement, heart != Entity.Null, owner != Entity.Null, "", ""));

    /// <summary>The area requirement byte, the heart and the owner user, by guarded raw reads.</summary>
    private static bool TryReadPiece(EntityManager em, Entity e, out byte requirement, out Entity heart, out Entity owner)
    {
        requirement = 0;
        heart = Entity.Null;
        owner = Entity.Null;
        byte* req = AiRuntime.RO<CastleAreaRequirement>(em, e, Layout.CastleAreaRequirementRequirementTypeOffset + 1);
        byte* conn = AiRuntime.RO<CastleHeartConnection>(em, e, Layout.CastleHeartConnectionEntityOffset + EntityBytes);
        byte* own = AiRuntime.RO<UserOwner>(em, e, Layout.UserOwnerOwnerOffset + EntityBytes);
        if (req == null || conn == null || own == null) return false;
        requirement = req[Layout.CastleAreaRequirementRequirementTypeOffset];
        heart = *(Entity*)(conn + Layout.CastleHeartConnectionEntityOffset);
        owner = *(Entity*)(own + Layout.UserOwnerOwnerOffset);
        return true;
    }

    private static string OwnerName(EntityManager em, Entity user)
    {
        try
        {
            if (user == Entity.Null || !em.Exists(user) || !em.HasComponent<User>(user)) return "";
            return em.GetComponentData<User>(user).CharacterName.ToString();
        }
        catch
        {
            return "";
        }
    }

    private static string Key(Entity e) => e.Index + ":" + e.Version;

    private static void Log(string line) => Plugin.Logger?.LogInfo(line);
}
#endif
