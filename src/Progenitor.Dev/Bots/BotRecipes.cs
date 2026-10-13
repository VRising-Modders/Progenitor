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
/// Server op <c>bot_recipes</c>: recipe lookup by output item, so a bot can find the recipe for
/// <c>Item_Gloves_T01_Bone</c> without guessing the recipe's prefab name. Read-only.
///
/// <para><b>Args:</b> optional <c>user</c> or <c>platformId</c> (adds <c>unlocked</c> per recipe);
/// optional <c>output</c> (item GUID number, or a name substring matched against output item
/// names); optional <c>name</c> (substring of the recipe prefab name); optional <c>onlyUnlocked</c>
/// (requires a user); <c>limit</c> (default 50, 1..1000).</para>
///
/// <para><b>Reads (server metadata), all raw through non-generic <c>GetComponentDataRawRO</c> /
/// <c>GetBufferLength</c> / <c>GetBufferRawRO</c>; no new IL2CPP generic instantiation:</b></para>
/// <list type="bullet">
/// <item><description><c>RecipeData</c>: <c>[0xC] float CraftDuration; [0x14] bool AlwaysUnlocked;
/// [0x15] bool HideInStation</c>. Bool-bearing, read raw. Recipe prefabs are found by a prefab-inclusive
/// query for <c>RecipeData</c> + <c>PrefabGUID</c>.</description></item>
/// <item><description><c>RecipeRequirementBuffer</c> and <c>RecipeOutputBuffer</c>:
/// <c>PrefabGUID @0x0; int Amount @0x4</c>, stride 8.</description></item>
/// <item><description>Station link: <c>WorkstationRecipesBuffer</c> (<c>PrefabGUID RecipeGuid @0x0</c>,
/// stride 4) and <c>RefinementstationRecipesBuffer</c> (<c>PrefabGUID RecipeGuid @0x0; bool Unlocked @0x4;
/// bool Disabled @0x5</c>; stride 8 = 6 bytes padded to the 4-byte alignment of <c>PrefabGUID</c>) on station
/// prefabs (those also carrying <c>Prefab</c>). A station prefab that is a player character
/// (<c>PlayerCharacter</c>) or the <c>User</c> prefab is reported with <c>kind = "character"</c>:
/// character ("in hand") crafting. <b>Live 2026-09-24:</b> the in-hand recipes (e.g.
/// Recipe_Armor_Gloves_T01_Bone, and the Bone Sword crafted in hand earlier) are listed by the prefab
/// named <c>User</c>. If no prefab lists a recipe, <c>stations</c> is empty and
/// <c>characterCraftable</c> is false: the data does not say where it is crafted.</description></item>
/// <item><description>Unlock state: <c>User</c> entity -> <c>ProgressionMapper</c>
/// (<c>NetworkedEntity ProgressionEntity @0x0</c>, whose <c>Entity _Entity</c> is at +0x0) ->
/// <c>UnlockedRecipeElement</c> (<c>PrefabGUID UnlockedRecipe @0x0; bool UserHasRequiredContentFlags
/// @0x4</c>, stride 8). <c>unlocked = AlwaysUnlocked || listed</c>. Server settings that skip the
/// progression check are not consulted.</description></item>
/// </list>
///
/// <para>The recipe/station index holds only GUIDs, names and numbers; it is built once per Dev
/// generation (prefabs do not change at runtime) and not cached when it comes back empty.</para>
///
/// <para><b>Status:</b> source-checked and built; <b>not verified live</b>.</para>
/// </summary>
internal static class BotRecipes
{
    private const int PairStride = 8;                  // RecipeRequirementBuffer / RecipeOutputBuffer / BlueprintRequirementBuffer
    private const int PairAmountOffset = 0x4;
    private const int WorkstationRowStride = 4;        // WorkstationRecipesBuffer
    private const int RefinementRowStride = 8;         // RefinementstationRecipesBuffer
    private const int UnlockedRowStride = 8;           // UnlockedRecipeElement / UnlockedBlueprintElement
    private const int RecipeCraftDurationOffset = 0xC; // RecipeData.CraftDuration
    private const int RecipeAlwaysUnlockedOffset = 0x14;
    private const int RecipeHideInStationOffset = 0x15;
    private const int DefaultLimit = 50;
    private const int MaxLimit = 1000;

    private sealed class Recipe
    {
        public int Guid;
        public string? Name;
        public float CraftSeconds;
        public bool AlwaysUnlocked;
        public bool HideInStation;
        public (int guid, int amount)[] Outputs = Array.Empty<(int, int)>();
        public (int guid, int amount)[] Requirements = Array.Empty<(int, int)>();
        public List<(int guid, string kind)> Stations = new();
    }

    private static List<Recipe>? s_index;
    private static Dictionary<int, string?>? s_names;

    public static object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("bot_recipes runs on the server");
        if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("args must be an object");
        var em = world.EntityManager;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>()
            ?? throw new InvalidOperationException("no PrefabCollectionSystem");

        string? nameFilter = Str(args, "name");
        int? outputGuid = null;
        string? outputName = null;
        if (args.TryGetProperty("output", out var o))
        {
            if (o.ValueKind == JsonValueKind.Number) outputGuid = o.GetInt32();
            else if (o.ValueKind == JsonValueKind.String && int.TryParse(o.GetString(), out int og)) outputGuid = og;
            else if (o.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(o.GetString())) outputName = o.GetString();
            else throw new ArgumentException("args.output must be an item GUID or a non-empty name substring");
        }
        bool onlyUnlocked = args.TryGetProperty("onlyUnlocked", out var ou) && ou.ValueKind == JsonValueKind.True;
        int limit = Limit(args);

        bool hasUser = Str(args, "user") != null || args.TryGetProperty("platformId", out _);
        if (onlyUnlocked && !hasUser) throw new ArgumentException("args.onlyUnlocked needs args.user or args.platformId");
        string? userName = null;
        string? progressionId = null;
        HashSet<int>? unlockedSet = null;
        if (hasUser)
        {
            var user = ResolveUser(em, args, out string n);
            userName = n;
            var progression = ProgressionEntity(em, user, n);
            progressionId = $"{progression.Index}:{progression.Version}";
            unlockedSet = new HashSet<int>(ReadUnlocked(em, progression, ComponentIndex.Of<UnlockedRecipeElement>(ComponentType.AccessMode.ReadOnly)));
        }

        var index = Index(em, prefabs);
        var names = s_names!;
        var rows = new List<object>();
        int matched = 0;
        foreach (var r in index)
        {
            if (nameFilter != null && (r.Name == null || r.Name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)) continue;
            if (outputGuid != null || outputName != null)
            {
                bool hit = false;
                foreach (var (g, _) in r.Outputs)
                {
                    if (outputGuid != null ? g == outputGuid.Value
                        : Name(prefabs, names, g) is string on && on.IndexOf(outputName!, StringComparison.OrdinalIgnoreCase) >= 0)
                    { hit = true; break; }
                }
                if (!hit) continue;
            }
            bool? unlocked = unlockedSet == null ? null : r.AlwaysUnlocked || unlockedSet.Contains(r.Guid);
            if (onlyUnlocked && unlocked != true) continue;
            matched++;
            if (rows.Count >= limit) continue;

            var stations = new List<object>();
            bool character = false;
            foreach (var (g, kind) in r.Stations)
            {
                if (kind == "character") character = true;
                stations.Add(new { name = Name(prefabs, names, g), guid = g, kind });
            }
            rows.Add(new
            {
                recipe = r.Name,
                guid = r.Guid,
                craftSeconds = r.CraftSeconds,
                alwaysUnlocked = r.AlwaysUnlocked,
                hideInStation = r.HideInStation,
                unlocked,
                outputs = Pairs(prefabs, names, r.Outputs),
                requirements = Pairs(prefabs, names, r.Requirements),
                stations,
                characterCraftable = character,
            });
        }
        return new
        {
            user = userName,
            progression = progressionId,
            unlockedRows = unlockedSet?.Count,
            totalRecipes = index.Count,
            matched,
            returned = rows.Count,
            recipes = rows,
            note = "stations come from WorkstationRecipesBuffer/RefinementstationRecipesBuffer on prefabs; kind 'character' = a player-character prefab lists it (in-hand craft, inferred); empty stations = no prefab lists it",
        };
    }

    private static List<Recipe> Index(EntityManager em, PrefabCollectionSystem prefabs)
    {
        if (s_index != null) return s_index;
        var names = new Dictionary<int, string?>();
        var recipes = new List<Recipe>();
        var byGuid = new Dictionary<int, Recipe>();
        var guidType = ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly);
        var dataType = ComponentIndex.Of<RecipeData>(ComponentType.AccessMode.ReadOnly);
        var reqType = ComponentIndex.Of<RecipeRequirementBuffer>(ComponentType.AccessMode.ReadOnly);
        var outType = ComponentIndex.Of<RecipeOutputBuffer>(ComponentType.AccessMode.ReadOnly);
        const EntityQueryOptions options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities;

        foreach (var e in Query.EntitiesWithOptions(em, options, dataType, guidType))
        {
            int guid = ReadGuid(em, e, guidType);
            if (guid == 0 || byGuid.ContainsKey(guid)) continue;
            var r = new Recipe { Guid = guid, Name = Name(prefabs, names, guid) };
            unsafe
            {
                byte* d = (byte*)em.GetComponentDataRawRO(e, dataType.TypeIndex);
                if (d != null)
                {
                    r.CraftSeconds = *(float*)(d + RecipeCraftDurationOffset);
                    r.AlwaysUnlocked = d[RecipeAlwaysUnlockedOffset] != 0;
                    r.HideInStation = d[RecipeHideInStationOffset] != 0;
                }
            }
            r.Outputs = ReadPairs(em, e, outType).ToArray();
            r.Requirements = ReadPairs(em, e, reqType).ToArray();
            byGuid[guid] = r;
            recipes.Add(r);
        }

        var prefabTag = ComponentIndex.Of<Prefab>(ComponentType.AccessMode.ReadOnly);
        var playerType = ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly);
        LinkStations(em, options, guidType, prefabTag, playerType, byGuid,
            ComponentIndex.Of<WorkstationRecipesBuffer>(ComponentType.AccessMode.ReadOnly), WorkstationRowStride, "workstation");
        LinkStations(em, options, guidType, prefabTag, playerType, byGuid,
            ComponentIndex.Of<RefinementstationRecipesBuffer>(ComponentType.AccessMode.ReadOnly), RefinementRowStride, "refinement");

        recipes.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        s_names = names;
        if (recipes.Count > 0) s_index = recipes;
        return recipes;
    }

    // One station prefab per GUID (placed instances, which lack the Prefab tag, are skipped).
    private static unsafe void LinkStations(EntityManager em, EntityQueryOptions options, ComponentType guidType, ComponentType prefabTag,
        ComponentType playerType, Dictionary<int, Recipe> byGuid, ComponentType bufferType, int stride, string kind)
    {
        var userType = ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly);
        var seen = new HashSet<int>();
        foreach (var e in Query.EntitiesWithOptions(em, options, bufferType, guidType))
        {
            if (!em.HasComponent(e, prefabTag)) continue;
            int station = ReadGuid(em, e, guidType);
            if (station == 0 || !seen.Add(station)) continue;
            // Live 2026-09-24: in-hand recipes (e.g. Recipe_Armor_Gloves_T01_Bone) are listed by the
            // `User` prefab's WorkstationRecipesBuffer, not the character's.
            string rowKind = em.HasComponent(e, playerType) || em.HasComponent(e, userType) ? "character" : kind;
            int length = em.GetBufferLength(e, bufferType.TypeIndex);
            byte* raw = (byte*)em.GetBufferRawRO(e, bufferType.TypeIndex);
            for (int i = 0; raw != null && i < length; i++)
            {
                int recipe = *(int*)(raw + i * stride);
                if (!byGuid.TryGetValue(recipe, out var r)) continue;
                bool dup = false;
                foreach (var s in r.Stations) if (s.guid == station) { dup = true; break; }
                if (!dup) r.Stations.Add((station, rowKind));
            }
        }
    }

    // ---- shared with BotBlueprints ----

    internal static int Limit(JsonElement args)
    {
        if (!args.TryGetProperty("limit", out var l)) return DefaultLimit;
        if (l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out int n) || n < 1 || n > MaxLimit)
            throw new ArgumentException($"args.limit must be an integer 1..{MaxLimit}");
        return n;
    }

    /// <summary>
    /// The user's progression entity: <c>ProgressionMapper</c> on the User entity,
    /// <c>NetworkedEntity ProgressionEntity @0x0</c>, <c>Entity _Entity @0x0</c> inside it.
    /// Raw read: <c>NetworkedEntity</c> carries a bool.
    /// </summary>
    internal static unsafe Entity ProgressionEntity(EntityManager em, Entity user, string name)
    {
        var type = ComponentIndex.Of<ProgressionMapper>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(user, type)) throw new InvalidOperationException($"'{name}' has no ProgressionMapper");
        byte* raw = (byte*)em.GetComponentDataRawRO(user, type.TypeIndex);
        if (raw == null) throw new InvalidOperationException("ProgressionMapper read failed");
        var e = *(Entity*)raw;
        if (e == Entity.Null || !em.Exists(e))
            throw new InvalidOperationException($"'{name}' progression entity {e.Index}:{e.Version} does not exist");
        return e;
    }

    /// <summary>GUIDs of an <c>Unlocked*Element</c> buffer: <c>PrefabGUID @0x0; bool @0x4</c>, stride 8.</summary>
    internal static unsafe List<int> ReadUnlocked(EntityManager em, Entity progression, ComponentType type)
    {
        var result = new List<int>();
        if (!em.HasComponent(progression, type)) return result;
        int length = em.GetBufferLength(progression, type.TypeIndex);
        byte* raw = (byte*)em.GetBufferRawRO(progression, type.TypeIndex);
        for (int i = 0; raw != null && i < length; i++)
            result.Add(*(int*)(raw + i * UnlockedRowStride));
        return result;
    }

    /// <summary>A <c>{ PrefabGUID @0x0; int Amount @0x4 }</c> buffer, stride 8.</summary>
    internal static unsafe List<(int guid, int amount)> ReadPairs(EntityManager em, Entity entity, ComponentType type)
    {
        var result = new List<(int, int)>();
        if (!em.HasComponent(entity, type)) return result;
        int length = em.GetBufferLength(entity, type.TypeIndex);
        byte* raw = (byte*)em.GetBufferRawRO(entity, type.TypeIndex);
        for (int i = 0; raw != null && i < length; i++)
            result.Add((*(int*)(raw + i * PairStride), *(int*)(raw + i * PairStride + PairAmountOffset)));
        return result;
    }

    internal static List<object> Pairs(PrefabCollectionSystem prefabs, Dictionary<int, string?>? names, IEnumerable<(int guid, int amount)> pairs)
    {
        var list = new List<object>();
        foreach (var (g, a) in pairs) list.Add(new { item = Name(prefabs, names, g), guid = g, amount = a });
        return list;
    }

    internal static string? Name(PrefabCollectionSystem prefabs, Dictionary<int, string?>? names, int guid)
    {
        if (guid == 0) return null;
        if (names != null && names.TryGetValue(guid, out var cached)) return cached;
        var n = EntityOps.TryPrefabName(prefabs, new PrefabGUID(guid));
        if (names != null) names[guid] = n;
        return n;
    }

    private static unsafe int ReadGuid(EntityManager em, Entity e, ComponentType guidType)
    {
        byte* g = (byte*)em.GetComponentDataRawRO(e, guidType.TypeIndex);
        return g == null ? 0 : *(int*)g;
    }

    /// <summary>Same matching as <see cref="BotCast.ResolveCharacter"/>, returning the User entity.</summary>
    internal static Entity ResolveUser(EntityManager em, JsonElement args, out string name)
    {
        ulong? platformId = null;
        string? wanted = Str(args, "user");
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("platformId", out var p))
        {
            if (p.ValueKind == JsonValueKind.String && ulong.TryParse(p.GetString(), out var ps)) platformId = ps;
            else if (p.ValueKind == JsonValueKind.Number && p.TryGetUInt64(out var pn)) platformId = pn;
            else throw new ArgumentException("args.platformId must be an unsigned integer");
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

    internal static string? Str(JsonElement a, string k) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
