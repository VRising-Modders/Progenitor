using System;
using System.Collections.Generic;
using System.Text.Json;
using ProjectM;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Dev.Bots;

/// <summary>
/// Server op <c>bot_blueprints</c>: the build-menu blueprints a user has unlocked, with their costs,
/// so a bot knows what it may place (a new character had none; claiming <c>Journal_Gathering</c> added
/// the castle heart -485210554, live 2026-09-24). Read-only.
///
/// <para><b>Args:</b> <c>user</c> or <c>platformId</c> (required); optional <c>name</c> substring of
/// the blueprint prefab name; <c>limit</c> (default 50, 1..1000).</para>
///
/// <para><b>Reads (server metadata), raw through non-generic <c>GetComponentDataRawRO</c> /
/// <c>GetBufferLength</c> / <c>GetBufferRawRO</c>:</b></para>
/// <list type="bullet">
/// <item><description>User -> <c>ProgressionMapper</c> -> progression entity ->
/// <c>UnlockedBlueprintElement</c> (<c>PrefabGUID UnlockedBlueprint @0x0; bool
/// UserHasRequiredContentFlags @0x4</c>, stride 8). See <see cref="BotRecipes.ProgressionEntity"/>.</description></item>
/// <item><description>Cost: <c>BlueprintRequirementBuffer</c> (<c>PrefabGUID @0x0; int Amount @0x4</c>,
/// stride 8) on the blueprint prefab, looked up fresh through <c>_PrefabGuidToEntityMap</c>.</description></item>
/// <item><description><c>BlueprintData</c> on the same prefab: <c>[0x34] float FullDismantleTime;
/// [0x38] bool IsStartBlueprint; [0x39] bool IsInventoryItemBuilding; [0x3A] bool ExitBuildModeWhenBuilt;
/// [0x3B] bool RequiresLineOfSight; [0x3C] bool RequiresSuccessfullPathfinding</c>. Bool-bearing, read raw;
/// <c>blueprint</c> is null when the prefab lacks it.</description></item>
/// </list>
///
/// <para><b>Status:</b> source-checked and built; <b>not verified live</b>.</para>
/// </summary>
internal static class BotBlueprints
{
    private const int DismantleTimeOffset = 0x34;
    private const int IsStartBlueprintOffset = 0x38;
    private const int IsInventoryItemBuildingOffset = 0x39;
    private const int ExitBuildModeOffset = 0x3A;
    private const int RequiresLineOfSightOffset = 0x3B;
    private const int RequiresPathfindingOffset = 0x3C;

    public static unsafe object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("bot_blueprints runs on the server");
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("args must be an object");
        var em = world.EntityManager;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>()
            ?? throw new InvalidOperationException("no PrefabCollectionSystem");
        string? filter = BotRecipes.Str(args, "name");
        int limit = BotRecipes.Limit(args);

        var user = BotRecipes.ResolveUser(em, args, out string name);
        var progression = BotRecipes.ProgressionEntity(em, user, name);
        var unlocked = BotRecipes.ReadUnlocked(em, progression, ComponentIndex.Of<UnlockedBlueprintElement>(ComponentType.AccessMode.ReadOnly));

        var costType = ComponentIndex.Of<BlueprintRequirementBuffer>(ComponentType.AccessMode.ReadOnly);
        var dataType = ComponentIndex.Of<BlueprintData>(ComponentType.AccessMode.ReadOnly);
        var rows = new List<object>();
        var seen = new HashSet<int>();
        int matched = 0;
        foreach (int guid in unlocked)
        {
            if (guid == 0 || !seen.Add(guid)) continue;
            string? prefabName = BotRecipes.Name(prefabs, null, guid);
            if (filter != null && (prefabName == null || prefabName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
            matched++;
            if (rows.Count >= limit) continue;

            List<object>? cost = null;
            object? blueprint = null;
            bool found = prefabs._PrefabGuidToEntityMap.TryGetValue(new PrefabGUID(guid), out var prefab) && prefab != Entity.Null;
            if (found)
            {
                cost = BotRecipes.Pairs(prefabs, null, BotRecipes.ReadPairs(em, prefab, costType));
                if (em.HasComponent(prefab, dataType))
                {
                    byte* d = (byte*)em.GetComponentDataRawRO(prefab, dataType.TypeIndex);
                    if (d != null)
                        blueprint = new
                        {
                            isStartBlueprint = d[IsStartBlueprintOffset] != 0,
                            isInventoryItemBuilding = d[IsInventoryItemBuildingOffset] != 0,
                            exitBuildModeWhenBuilt = d[ExitBuildModeOffset] != 0,
                            requiresLineOfSight = d[RequiresLineOfSightOffset] != 0,
                            requiresPathfinding = d[RequiresPathfindingOffset] != 0,
                            fullDismantleSeconds = *(float*)(d + DismantleTimeOffset),
                        };
                }
            }
            rows.Add(new { guid, name = prefabName, prefabFound = found, cost, blueprint });
        }
        return new
        {
            name,
            progression = $"{progression.Index}:{progression.Version}",
            count = seen.Count,
            matched,
            returned = rows.Count,
            blueprints = rows,
        };
    }

    /// <summary>
    /// Server op <c>bot_canplace</c>: the read-only placement subset Core already implements
    /// (<c>ICastleHandle.CanPlace</c>: territory, buildable tiles, height band, occupancy) for the
    /// user's own castle, so a bot can learn why a silent refusal happened. Not the game's validator
    /// (<c>NotChecked</c> lists what it skips). Args <c>user</c>, <c>prefab</c> (GUID), <c>x</c>,
    /// <c>y</c>, <c>z</c>, optional <c>rotation</c> 0..3.
    /// </summary>
    public static object CanPlace(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException("bot_canplace runs on the server");
        var em = world.EntityManager;
        var user = BotRecipes.ResolveUser(em, args, out string name);
        int prefab = args.TryGetProperty("prefab", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : throw new ArgumentException("args.prefab (GUID) is required");
        float F(string k) => args.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetSingle() : throw new ArgumentException($"args.{k} is required");
        byte rotation = args.TryGetProperty("rotation", out var r) && r.ValueKind == JsonValueKind.Number ? (byte)r.GetInt32() : (byte)0;
        var castle = Progenitor.Castles.Castles.ForUser(Progenitor.Castles.CastleId.From(user));
        var check = castle.CanPlace(prefab, new Progenitor.Castles.CastleVec3(F("x"), F("y"), F("z")), rotation);
        return new
        {
            name,
            allowed = check.Allowed,
            reason = check.Reason.ToString(),
            detail = check.Detail,
            tileFree = check.TileFree,
            blocking = check.BlockingPrefabName,
            notChecked = check.NotChecked,
        };
    }

    /// <summary>
    /// Server op <c>bot_prefabs</c>: buildable prefabs (entities carrying <c>BlueprintData</c>, prefabs
    /// included) whose name contains <c>name</c>, with GUID and cost. Uses the same prefab-entity
    /// query and naming as <c>bot_recipes</c> (no dictionary enumeration). Args <c>name</c>, <c>limit</c>.
    /// </summary>
    public static object Prefabs(World world, JsonElement args)
    {
        var em = world.EntityManager;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>() ?? throw new InvalidOperationException("no PrefabCollectionSystem");
        string filter = BotRecipes.Str(args, "name") ?? throw new ArgumentException("args.name (substring) is required");
        int limit = BotRecipes.Limit(args);
        const EntityQueryOptions options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities;
        var guidType = ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly);
        var bpType = ComponentIndex.Of<BlueprintData>(ComponentType.AccessMode.ReadOnly);
        var reqType = ComponentIndex.Of<BlueprintRequirementBuffer>(ComponentType.AccessMode.ReadOnly);
        var rows = new List<object>();
        var seen = new HashSet<int>();
        foreach (var e in Query.EntitiesWithOptions(em, options, bpType, guidType))
        {
            int guid = em.GetComponentData<PrefabGUID>(e).GuidHash;
            if (!seen.Add(guid)) continue;
            string? n = Progenitor.Dev.Ops.EntityOps.TryPrefabName(prefabs, new PrefabGUID(guid));
            if (n == null || n.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            rows.Add(new { name = n, guid, cost = em.HasComponent(e, reqType) ? BotRecipes.Pairs(prefabs, null, BotRecipes.ReadPairs(em, e, reqType)) : new List<object>() });
            if (rows.Count >= limit) break;
        }
        return new { filter, count = rows.Count, prefabs = rows };
    }
}
