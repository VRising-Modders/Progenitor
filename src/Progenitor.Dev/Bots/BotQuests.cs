using System;
using System.Collections.Generic;
using System.Text.Json;
using ProjectM;
using ProjectM.Network;
using Progenitor.Dev.Ops;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Dev.Bots;

/// <summary>
/// Server op <c>bot_quests</c>: a bot's journal (the game's Achievement system) so a scripted
/// brain can follow progression, e.g. "Collecting the Remains: Leave the Crypt; Collect Bones
/// [0/30]; unlocks Bone Sword, Bone Ring". Read-only; nothing is written.
///
/// <para>Data path (server metadata):
/// <c>User</c> entity -> <c>AchievementOwner</c> (<c>NetworkedEntity Entity @0x4</c>,
/// whose <c>Entity _Entity</c> is at +0x0 of <c>NetworkedEntity</c>) -> the per-player
/// achievement-data entity, holding <c>AchievementInProgressElement</c> rows
/// (<c>PrefabGUID @0x0; int CompletedAmount @0x4</c>, stride 8) and <c>AchievementClaimedElement</c>
/// rows (<c>PrefabGUID @0x0</c>, stride 4). Achievement prefabs carry
/// <c>AchievementData</c> (<c>int Level @0x0; PrefabGUID Reward @0x4; PrefabGUID
/// Dependency @0x8</c>) and an <c>AchievementSubTaskEntry</c> buffer (<c>PrefabGUID @0x0</c>,
/// stride 4). Subtask prefabs carry <c>AchievementSubTaskData</c>
/// (<c>PrefabIdentifier PrefabReference @0x24; PrefabIdentifier TechReference @0x28; int
/// RequiredCompletedCount @0x2C; AchievementProgressType ProgressType @0x38 (byte);
/// AchievementProgressLinkType ProgressLinkType @0x39 (byte)</c>; <c>PrefabIdentifier</c> is one int GUID
/// hash). The reward prefab's <c>TechUnlockRecipeBuffer</c> /
/// <c>TechUnlockBlueprintBuffer</c> (<c>PrefabGUID @0x0</c>, stride 4) name what a
/// claim unlocks. All reads are raw (<c>GetComponentDataRawRO</c> / <c>GetBufferRawRO</c> at metadata offsets
/// and strides): <c>AchievementSubTaskData</c> holds a blob reference and <c>AchievementOwner</c> a bool, and
/// no new generic instantiation over a game value type is created.</para>
///
/// <para>State per achievement: <c>claimed</c> when its GUID is in the claimed buffer; <c>locked</c>
/// when its <c>Dependency</c> is set and not claimed; otherwise <c>complete</c> when every subtask's
/// progress reaches its required count (the brain should then send a claim) or <c>in progress</c>.
/// Subtask progress is the <c>CompletedAmount</c> of the in-progress row keyed by the subtask GUID
/// (inferred from <c>ClaimAchievementSystem.IsSubTaskCompleted(PrefabGUID, int, DynamicBuffer&lt;
/// AchievementInProgressElement&gt;)</c>; native body not read). A subtask without a
/// row reports <c>row=false</c> and progress 0. The raw rows are also returned so this keying can be
/// checked live. The dependency rule for "current" is inferred from the field name. <b>Not verified
/// live.</b></para>
///
/// <para>Args: <c>user</c> (character name) or <c>platformId</c>; <c>scope</c> <c>current</c> (default:
/// unclaimed achievements whose dependency is claimed or empty), <c>open</c> (every unclaimed one) or
/// <c>all</c> (claimed and locked too); optional <c>achievement</c> (GUID hash) to return just that one
/// whatever its state.</para>
/// </summary>
internal static class BotQuests
{
    // Strides and offsets from the server metadata (see the summary); local because they are read only here.
    private const int InProgressStride = 8;          // AchievementInProgressElement
    private const int InProgressCountOffset = 0x4;   // AchievementInProgressElement.CompletedAmount
    private const int GuidStride = 4;                // AchievementClaimedElement / AchievementSubTaskEntry / TechUnlock*Buffer
    private const int OwnerEntityOffset = 0x4;       // AchievementOwner.Entity (NetworkedEntity._Entity @0x0)
    private const int DataLevelOffset = 0x0;         // AchievementData.Level
    private const int DataRewardOffset = 0x4;        // AchievementData.Reward
    private const int DataDependencyOffset = 0x8;    // AchievementData.Dependency
    private const int SubPrefabReferenceOffset = 0x24;
    private const int SubTechReferenceOffset = 0x28;
    private const int SubRequiredOffset = 0x2C;
    private const int SubProgressTypeOffset = 0x38;
    private const int SubLinkTypeOffset = 0x39;

    // AchievementProgressType (byte) and AchievementProgressLinkType (byte).
    private static readonly string[] ProgressTypes =
    {
        "PossessItem", "Kill", "Craft", "Hit", "Spawn", "ConsumeVBlood", "ObtainItem", "UnlockResearch",
        "EnterCompleteRoom", "EquipmentLevel", "InfuseVBlood", "StoneCoffinBound", "InteractWithTier2BloodFountain",
        "UseVBloodMenu", "InteractWithTier4BloodFountain", "InteractWithTier5BloodFountain", "LeftFarbane",
        "OpenedBuildMenu", "UnlockNewSpell", "InteractWithTier3BloodFountain", "OpenedMap", "EnteredTerritory",
        "OpenedCharacterMenu", "EnterCompleteRoomTreasury",
    };
    private static readonly string[] LinkTypes = { "Prefab", "EntityCategory", "MaterialCategory", "Custom", "Tech" };

    public static unsafe object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("bot_quests runs on the server");
        var em = world.EntityManager;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>()
            ?? throw new InvalidOperationException("no PrefabCollectionSystem");
        var userEntity = ResolveUser(em, args, out string name);
        string scope = Str(args, "scope")?.Trim().ToLowerInvariant() ?? "current";
        if (scope != "current" && scope != "open" && scope != "all")
            throw new ArgumentException("args.scope must be current, open or all");
        int? only = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("achievement", out var a) && a.ValueKind == JsonValueKind.Number
            ? a.GetInt32() : null;

        // User -> AchievementOwner -> achievement-data entity.
        var ownerType = ComponentIndex.Of<AchievementOwner>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(userEntity, ownerType))
            throw new InvalidOperationException($"'{name}' has no AchievementOwner");
        byte* owner = (byte*)em.GetComponentDataRawRO(userEntity, ownerType.TypeIndex);
        if (owner == null) throw new InvalidOperationException("AchievementOwner read failed");
        var data = *(Entity*)(owner + OwnerEntityOffset);
        if (data == Entity.Null || !em.Exists(data))
            throw new InvalidOperationException($"'{name}' achievement-data entity {data.Index}:{data.Version} does not exist");

        var progress = new Dictionary<int, int>();
        var rowList = new List<object>();
        var inProgressType = ComponentIndex.Of<AchievementInProgressElement>(ComponentType.AccessMode.ReadOnly);
        if (em.HasComponent(data, inProgressType))
        {
            int len = em.GetBufferLength(data, inProgressType.TypeIndex);
            byte* raw = (byte*)em.GetBufferRawRO(data, inProgressType.TypeIndex);
            for (int i = 0; raw != null && i < len; i++)
            {
                int guid = *(int*)(raw + i * InProgressStride);
                int count = *(int*)(raw + i * InProgressStride + InProgressCountOffset);
                progress[guid] = count;
                rowList.Add(new { guid, name = PrefabName(prefabs, guid), count });
            }
        }
        var claimed = new HashSet<int>(ReadGuids(em, data, ComponentIndex.Of<AchievementClaimedElement>(ComponentType.AccessMode.ReadOnly)));

        // Every achievement prefab (prefab entities need IncludePrefab).
        var dataType = ComponentIndex.Of<AchievementData>(ComponentType.AccessMode.ReadOnly);
        var guidType = ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly);
        var achievementEntities = Query.EntitiesWithOptions(em,
            EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities, dataType, guidType);
        var subEntryType = ComponentIndex.Of<AchievementSubTaskEntry>(ComponentType.AccessMode.ReadOnly);
        var subDataType = ComponentIndex.Of<AchievementSubTaskData>(ComponentType.AccessMode.ReadOnly);

        var list = new List<(int level, object row)>();
        var seen = new HashSet<int>();
        int total = 0;
        foreach (var e in achievementEntities)
        {
            byte* g = (byte*)em.GetComponentDataRawRO(e, guidType.TypeIndex);
            byte* d = (byte*)em.GetComponentDataRawRO(e, dataType.TypeIndex);
            if (g == null || d == null) continue;
            int guid = *(int*)g;
            if (!seen.Add(guid)) continue; // one prefab per GUID
            total++;
            if (only != null && guid != only.Value) continue;
            int level = *(int*)(d + DataLevelOffset);
            int reward = *(int*)(d + DataRewardOffset);
            int dependency = *(int*)(d + DataDependencyOffset);
            bool isClaimed = claimed.Contains(guid);
            bool locked = !isClaimed && dependency != 0 && !claimed.Contains(dependency);
            if (only == null)
            {
                if (scope != "all" && isClaimed) continue;
                if (scope == "current" && locked) continue;
            }

            var subtasks = new List<object>();
            bool allDone = true;
            foreach (int sub in ReadGuids(em, e, subEntryType))
            {
                bool hasRow = progress.TryGetValue(sub, out int done);
                int required = 0, reference = 0, tech = 0; byte ptype = 255, ltype = 255;
                if (prefabs._PrefabGuidToEntityMap.TryGetValue(new PrefabGUID(sub), out var subEntity)
                    && subEntity != Entity.Null && em.HasComponent(subEntity, subDataType))
                {
                    byte* s = (byte*)em.GetComponentDataRawRO(subEntity, subDataType.TypeIndex);
                    if (s != null)
                    {
                        reference = *(int*)(s + SubPrefabReferenceOffset);
                        tech = *(int*)(s + SubTechReferenceOffset);
                        required = *(int*)(s + SubRequiredOffset);
                        ptype = s[SubProgressTypeOffset];
                        ltype = s[SubLinkTypeOffset];
                    }
                }
                if (!hasRow || done < required) allDone = false;
                subtasks.Add(new
                {
                    subtask = sub,
                    name = PrefabName(prefabs, sub),
                    progress = hasRow ? done : 0,
                    required,
                    row = hasRow,
                    trackedItem = reference != 0 ? new { guid = reference, name = PrefabName(prefabs, reference) } : null,
                    tech = tech != 0 ? new { guid = tech, name = PrefabName(prefabs, tech) } : null,
                    type = ptype < ProgressTypes.Length ? ProgressTypes[ptype] : ptype.ToString(),
                    link = ltype < LinkTypes.Length ? LinkTypes[ltype] : ltype.ToString(),
                });
            }

            int? ownCount = progress.TryGetValue(guid, out int own) ? own : null;
            string state = isClaimed ? "claimed" : locked ? "locked" : allDone && subtasks.Count > 0 ? "complete" : "in progress";
            list.Add((level, new
            {
                achievement = guid,
                name = PrefabName(prefabs, guid),
                state,
                claimable = state == "complete",
                level,
                ownCount,
                dependency = dependency != 0 ? new { guid = dependency, name = PrefabName(prefabs, dependency) } : null,
                reward = reward != 0 ? RewardInfo(em, prefabs, reward) : null,
                subtasks,
            }));
        }
        list.Sort((x, y) => x.level.CompareTo(y.level));
        var achievements = new List<object>(list.Count);
        foreach (var item in list) achievements.Add(item.row);

        return new
        {
            name,
            user = $"{userEntity.Index}:{userEntity.Version}",
            achievementData = $"{data.Index}:{data.Version}",
            scope,
            totalAchievements = total,
            claimedCount = claimed.Count,
            achievements,
            rows = rowList,
            note = "not verified live: subtask progress assumes rows keyed by subtask GUID; state 'complete' means claimable (send ClaimAchievementEvent)",
        };
    }

    // Reward prefab: its name plus recipes/blueprints its tech buffers unlock, when present.
    private static object RewardInfo(EntityManager em, PrefabCollectionSystem prefabs, int reward)
    {
        var recipes = new List<object>();
        var blueprints = new List<object>();
        if (prefabs._PrefabGuidToEntityMap.TryGetValue(new PrefabGUID(reward), out var r) && r != Entity.Null)
        {
            foreach (int g in ReadGuids(em, r, ComponentIndex.Of<TechUnlockRecipeBuffer>(ComponentType.AccessMode.ReadOnly)))
                recipes.Add(new { guid = g, name = PrefabName(prefabs, g) });
            foreach (int g in ReadGuids(em, r, ComponentIndex.Of<TechUnlockBlueprintBuffer>(ComponentType.AccessMode.ReadOnly)))
                blueprints.Add(new { guid = g, name = PrefabName(prefabs, g) });
        }
        return new { guid = reward, name = PrefabName(prefabs, reward), unlocksRecipes = recipes, unlocksBlueprints = blueprints };
    }

    // A buffer whose element is one PrefabGUID (int @0x0, stride 4), through the non-generic raw path.
    private static unsafe List<int> ReadGuids(EntityManager em, Entity entity, ComponentType type)
    {
        var result = new List<int>();
        if (!em.HasComponent(entity, type)) return result;
        int length = em.GetBufferLength(entity, type.TypeIndex);
        byte* raw = (byte*)em.GetBufferRawRO(entity, type.TypeIndex);
        if (raw == null) return result;
        for (int i = 0; i < length; i++)
            result.Add(*(int*)(raw + i * GuidStride));
        return result;
    }

    private static string? PrefabName(PrefabCollectionSystem prefabs, int guid) =>
        guid == 0 ? null : EntityOps.TryPrefabName(prefabs, new PrefabGUID(guid));

    // Same matching as BotCast.ResolveCharacter, returning the User entity instead of the character.
    private static Entity ResolveUser(EntityManager em, JsonElement args, out string name)
    {
        ulong? platformId = null;
        string? wanted = Str(args, "user");
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("platformId", out var p))
        {
            if (p.ValueKind == JsonValueKind.String && ulong.TryParse(p.GetString(), out var ps)) platformId = ps;
            else if (p.ValueKind == JsonValueKind.Number && p.TryGetUInt64(out var pn)) platformId = pn;
        }
        if (platformId == null && string.IsNullOrWhiteSpace(wanted))
            throw new ArgumentException("args.user (character name) or args.platformId is required");
        var users = Query.Entities(em, includeDisabled: true, includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < users.Count; i++)
        {
            var u = em.GetComponentData<User>(users[i]); // bool-bearing: existing typed accessor
            if ((platformId != null && u.PlatformId == platformId.Value)
                || (platformId == null && string.Equals(u.CharacterName.ToString(), wanted, StringComparison.OrdinalIgnoreCase)))
            {
                name = u.CharacterName.ToString();
                return users[i];
            }
        }
        throw new InvalidOperationException("no such user");
    }

    private static string? Str(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
