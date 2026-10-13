using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using BepInEx;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Server op <c>unlock</c> (TEST SETUP, isolated test server only): make every build-menu blueprint
/// and crafting recipe available to one player, so content mods add shows up too, and undo it.
/// Policy and planning are in <see cref="UnlockArgs"/>.
///
/// <para><b>Mechanism (native server, not live-verified).</b> <c>ProgressionUtility.UpdateUnlockedBuffers</c>
/// zeroes <c>UnlockedRecipeElement</c>, <c>UnlockedBlueprintElement</c>, <c>UnlockedShapeshiftElement</c>
/// and <c>UnlockedMusicTrackElement</c> and refills them from <c>UnlockedProgressionElement</c>: for each listed
/// GUID it expands the prefab's <c>ProgressionBook*</c> and <c>TechUnlock*Buffer</c> rows, and adds the GUID itself
/// when its prefab carries <c>RecipeData</c> (GetRecipeLookup) or <c>BlueprintData</c> (GetBlueprintLookup). So a
/// blueprint or recipe GUID listed directly is unlocked without any tech, which is what covers mod clones.
/// The game runs that rebuild from <c>DebugEventsSystem.UnlockProgression</c>, reached by a received
/// <c>UnlockProgressionDebugEvent {PrefabGUID PrefabGuid @0x0}</c> (EventId 1381311156) with
/// <c>HandleClientDebugEvent {int FromUserIndex @0x0}</c> + <c>FromCharacter</c>: it skips a GUID
/// <c>HasUnlockedProgression</c> already accepts, else adds it and rebuilds.</para>
///
/// <para><b>Writes.</b> <c>all</c>/<c>prefabs</c>: every new GUID but the last is written raw into
/// <c>UnlockedProgressionElement</c> in one <c>UnsafeEntityManagerUtility.SetBufferDataRaw</c> (the call the game's
/// own save loader uses), the last is raised as the event, and its handler rebuilds once. <c>lock</c>/<c>revert</c>:
/// there is no lock event, so the targets plus one kept trigger row are removed in one raw write and the trigger
/// is raised again (see <see cref="UnlockArgs.PlanLock"/>). The read-back runs one second later through
/// <see cref="DeferredResult"/>.</para>
/// </summary>
internal static class UnlockOps
{
    internal const string Label = "TEST SETUP";
    internal const int UnlockProgressionDebugEventId = 1381311156; // same value on server and client
    private const int ProgressionStride = 4;          // UnlockedProgressionElement {PrefabGUID @0x0}
    private const int DerivedStride = 8;              // Unlocked{Blueprint,Recipe,Shapeshift}Element {PrefabGUID @0x0; bool @0x4}
    private const int DerivedFlagOffset = 0x4;        // UserHasRequiredContentFlags
    private const int NetworkedEntityWaitingOffset = 0x8; // NetworkedEntity._WaitingForSync
    private const int MaxProgressionRows = 65_536;
    private const double ReadbackDelaySeconds = 1;
    private const int ReadbackWaitMs = 1000;

    public static object Run(World world, JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("unlock runs on the server; only the server owns progression");
        RefuseLiveServer();
        var req = UnlockArgs.Parse(args);
        var em = world.EntityManager;
        var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>()
            ?? throw new InvalidOperationException($"PrefabCollectionSystem is not in world '{world.Name}' yet");
        var who = ResolveUser(em, req.User);
        var progression = ProgressionEntity(em, who.User, who.Name);
        var state = ReadState(em, progression);

        return req.Action switch
        {
            UnlockArgs.UnlockAction.Status => Status(em, prefabs, who, progression, state, req.Limit),
            UnlockArgs.UnlockAction.All => Unlock(world, em, prefabs, who, progression, state, req,
                AllPrefabGuids(em, ComponentIndex.Of<BlueprintData>(ComponentType.AccessMode.ReadOnly))
                    .Concat(AllPrefabGuids(em, ComponentIndex.Of<RecipeData>(ComponentType.AccessMode.ReadOnly))).ToList()),
            UnlockArgs.UnlockAction.Prefabs => Unlock(world, em, prefabs, who, progression, state, req, ResolveList(prefabs, req.Prefabs, requireKnown: true)),
            UnlockArgs.UnlockAction.Lock => Lock(world, em, prefabs, who, progression, state, req, ResolveList(prefabs, req.Prefabs, requireKnown: false), record: null),
            UnlockArgs.UnlockAction.Revert => Revert(world, em, prefabs, who, progression, state, req),
            _ => throw new ArgumentException("unknown action"),
        };
    }

    private sealed class Who
    {
        public Entity User;
        public Entity Character;
        public int Index;
        public string Name = "";
    }

    // ---- status ------------------------------------------------------------------------------

    private static object Status(EntityManager em, PrefabCollectionSystem prefabs, Who who, Entity progression, UnlockArgs.State state, int limit)
    {
        var blueprints = AllPrefabGuids(em, ComponentIndex.Of<BlueprintData>(ComponentType.AccessMode.ReadOnly));
        var recipes = AllPrefabGuids(em, ComponentIndex.Of<RecipeData>(ComponentType.AccessMode.ReadOnly));
        string path = RecordPath(who.Name);
        UnlockArgs.Record? record = TryReadRecord(path, out string? recordError);
        return new
        {
            action = "status",
            user = UserInfo(who),
            progressionEntity = Format(progression),
            buffers = new
            {
                progression = state.Progression.Count,
                blueprints = new { unlocked = state.BlueprintsOn.Count, contentLocked = state.BlueprintsContentLocked.Count },
                recipes = new { unlocked = state.RecipesOn.Count, contentLocked = state.RecipesContentLocked.Count },
                shapeshifts = state.Shapeshifts.Count,
            },
            prefabTotals = new { blueprints = blueprints.Count, recipes = recipes.Count },
            locked = new
            {
                blueprints = LockedSummary(prefabs, state, blueprints, limit),
                recipes = LockedSummary(prefabs, state, recipes, limit),
            },
            record = new { path, exists = record != null, guids = record?.Guids.Count ?? 0, progenitorGuids = record?.Owners.Count ?? 0, updatedUtc = record?.UpdatedUtc, error = recordError },
            dangling = Dangling(prefabs, state, limit),
        };
    }

    /// <summary>
    /// <c>UnlockedProgressionElement</c> rows whose prefab is not in the collection: the client looks
    /// each one up about twice a second and logs "does not exist as a registered prefab". Core
    /// <c>UnlockPrune</c> removes the ones a Progenitor mod registered; the rest need <c>lock</c>.
    /// </summary>
    private static object Dangling(PrefabCollectionSystem prefabs, UnlockArgs.State state, int limit)
    {
        var missing = state.Progression.Distinct()
            .Where(g => !(prefabs._PrefabGuidToEntityMap.TryGetValue(new PrefabGUID(g), out var e) && e != Entity.Null))
            .ToList();
        return new
        {
            count = missing.Count,
            list = UnlockArgs.Cap(missing, limit).Select(g =>
            {
                var o = Owner(g);
                return new { guid = g, mod = o?.Mod, id = o?.Id, kind = o?.Kind };
            }).ToList(),
            truncated = missing.Count > limit,
        };
    }

    private static UnlockArgs.GuidOwner? Owner(int guid)
    {
        try
        {
            if (Progenitor.Content.UnlockPrune.TryDescribeGuid(guid, out var mod, out var id, out var kind))
                return new UnlockArgs.GuidOwner { Mod = mod, Id = id, Kind = kind };
        }
        catch (Exception e) { Plugin.Logger?.LogWarning($"unlock: owner lookup for {guid} failed: {e.Message}"); }
        return null;
    }

    /// <summary>Of <paramref name="guids"/>, those a Progenitor mod registered, with that mod.</summary>
    private static Dictionary<int, UnlockArgs.GuidOwner> Owners(IEnumerable<int> guids)
    {
        var map = new Dictionary<int, UnlockArgs.GuidOwner>();
        foreach (int g in guids)
            if (Owner(g) is { } o) map[g] = o;
        return map;
    }

    private static object LockedSummary(PrefabCollectionSystem prefabs, UnlockArgs.State state, List<int> guids, int limit)
    {
        var locked = guids.Where(g => !state.IsUnlocked(g) && !state.IsContentLocked(g)).ToList();
        int contentLocked = guids.Count(state.IsContentLocked);
        return new
        {
            count = locked.Count,
            contentLocked,
            list = UnlockArgs.Cap(locked, limit).Select(g => new { guid = g, name = Name(prefabs, g) }).ToList(),
            truncated = locked.Count > limit,
        };
    }

    // ---- all / prefabs -----------------------------------------------------------------------

    private static object Unlock(World world, EntityManager em, PrefabCollectionSystem prefabs, Who who, Entity progression,
        UnlockArgs.State state, UnlockArgs.Request req, List<int> candidates)
    {
        var plan = UnlockArgs.PlanUnlock(state, candidates);
        string action = UnlockArgs.ActionName(req.Action);
        string path = RecordPath(who.Name);
        var summary = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["label"] = Label,
            ["user"] = UserInfo(who),
            ["progressionEntity"] = Format(progression),
            ["dryRun"] = req.DryRun,
            ["candidates"] = candidates.Count,
            ["alreadyUnlocked"] = plan.AlreadyUnlocked,
            ["contentLocked"] = plan.ContentLocked.Count,
            ["new"] = plan.New.Count,
            ["newList"] = UnlockArgs.Cap(plan.New, req.Limit).Select(g => new { guid = g, name = Name(prefabs, g), mod = Owner(g)?.Mod }).ToList(),
            ["record"] = path,
        };
        var owners = Owners(plan.New);
        summary["progenitorGuids"] = owners.Count;
        if (plan.New.Count == 0)
        {
            summary["sent"] = false;
            summary["note"] = "nothing to unlock";
            return summary;
        }
        if (req.DryRun)
        {
            summary["sent"] = false;
            summary["bulkRows"] = plan.BulkWrite?.Count - state.Progression.Count ?? 0;
            summary["trigger"] = plan.Trigger;
            return summary;
        }

        RequireCharacter(em, who);
        CheckLayouts();
        if (plan.BulkWrite != null && plan.BulkWrite.Count > MaxProgressionRows)
            throw new InvalidOperationException($"UnlockedProgressionElement would hold {plan.BulkWrite.Count} rows (cap {MaxProgressionRows}); refusing");

        // Record first: if anything below throws, revert still knows every GUID that may have landed.
        var existing = TryReadRecord(path, out string? recordError);
        if (recordError != null) throw new InvalidOperationException($"unlock record {path} is unreadable ({recordError}); fix or delete it before unlocking more");
        WriteRecord(path, UnlockArgs.Merge(existing, who.Name, who.Index, plan.New, DateTime.UtcNow, owners));

        if (plan.BulkWrite != null) WriteProgression(em, progression, plan.BulkWrite);
        var ev = RaiseUnlockProgression(em, who, plan.Trigger!.Value);
        Plugin.Logger?.LogInfo($"unlock: {Label} {action} for {who.Name}: {plan.New.Count} new ({plan.BulkWrite?.Count - state.Progression.Count ?? 0} raw rows + trigger {plan.Trigger}), {plan.AlreadyUnlocked} already, {plan.ContentLocked.Count} content-locked; record {path}");

        summary["sent"] = true;
        summary["event"] = Format(ev);
        summary["eventId"] = UnlockProgressionDebugEventId;
        summary["trigger"] = plan.Trigger;
        var expected = plan.New;
        int limit = req.Limit;
        return Deferred(() =>
        {
            var after = ReadState(em, ProgressionEntity(em, who.User, who.Name));
            var missing = UnlockArgs.Mismatches(after, expected, wantUnlocked: true);
            summary["readback"] = new
            {
                eventConsumed = !em.Exists(ev),
                unlocked = expected.Count - missing.Count,
                notUnlocked = missing.Count,
                notUnlockedList = UnlockArgs.Cap(missing, limit).Select(g => new { guid = g, name = Name(prefabs, g) }).ToList(),
                progression = after.Progression.Count,
                blueprints = after.BlueprintsOn.Count,
                recipes = after.RecipesOn.Count,
            };
            summary["note"] = missing.Count == 0
                ? "read back one second later: every new GUID counts as unlocked; run action status for the locked lists"
                : "read back one second later: some GUIDs do not count as unlocked; if eventConsumed is false the rebuild has not run yet, run action status again";
            return summary;
        });
    }

    // ---- lock / revert -----------------------------------------------------------------------

    private static object Revert(World world, EntityManager em, PrefabCollectionSystem prefabs, Who who, Entity progression,
        UnlockArgs.State state, UnlockArgs.Request req)
    {
        string path = RecordPath(who.Name);
        var record = TryReadRecord(path, out string? error);
        if (error != null) throw new InvalidOperationException($"unlock record {path} is unreadable: {error}");
        if (record == null) throw new InvalidOperationException($"no unlock record for '{who.Name}' ({path}); nothing this op added is known");
        return Lock(world, em, prefabs, who, progression, state, req, record.Guids, record: path);
    }

    private static object Lock(World world, EntityManager em, PrefabCollectionSystem prefabs, Who who, Entity progression,
        UnlockArgs.State state, UnlockArgs.Request req, List<int> targets, string? record)
    {
        var plan = UnlockArgs.PlanLock(state, targets);
        string action = UnlockArgs.ActionName(req.Action);
        var summary = new Dictionary<string, object?>
        {
            ["action"] = action,
            ["label"] = Label,
            ["user"] = UserInfo(who),
            ["progressionEntity"] = Format(progression),
            ["dryRun"] = req.DryRun,
            ["targets"] = targets.Count,
            ["removed"] = plan.Removed.Count,
            ["removedList"] = UnlockArgs.Cap(plan.Removed, req.Limit).Select(g => new { guid = g, name = Name(prefabs, g) }).ToList(),
            ["notInProgression"] = plan.NotInProgression.Count,
            ["notInProgressionList"] = UnlockArgs.Cap(plan.NotInProgression, req.Limit).Select(g => new { guid = g, name = Name(prefabs, g) }).ToList(),
            ["trigger"] = plan.Trigger,
            ["record"] = record,
        };
        if (plan.Removed.Count == 0)
        {
            summary["sent"] = false;
            if (record != null && !req.DryRun)
            {
                File.Delete(record); // none of its GUIDs is in the list any more: the record is spent
                summary["recordDeleted"] = true;
            }
            summary["note"] = "none of the targets is in UnlockedProgressionElement";
            return summary;
        }
        if (plan.Refusal != null) throw new InvalidOperationException(plan.Refusal);
        if (req.DryRun)
        {
            summary["sent"] = false;
            return summary;
        }

        RequireCharacter(em, who);
        CheckLayouts();
        WriteProgression(em, progression, plan.Write!);
        int trigger = plan.Trigger!.Value;
        Entity ev;
        try { ev = RaiseUnlockProgression(em, who, trigger); }
        catch
        {
            AppendProgression(em, progression, trigger); // never leave the trigger row out of the list
            throw;
        }
        Plugin.Logger?.LogInfo($"unlock: {Label} {action} for {who.Name}: removed {plan.Removed.Count} from UnlockedProgressionElement, {plan.NotInProgression.Count} not present, rebuild trigger {trigger}");

        summary["sent"] = true;
        summary["event"] = Format(ev);
        summary["eventId"] = UnlockProgressionDebugEventId;
        var removed = plan.Removed;
        int limit = req.Limit;
        return Deferred(() =>
        {
            // The entity the write went to, not a fresh guarded lookup: a lookup that throws (the
            // progression entity "waiting for sync") must not skip the trigger-row repair below.
            var prog = progression;
            var after = ReadState(em, prog);
            bool consumed = !em.Exists(ev);
            bool triggerBack = after.ProgressionSet.Contains(trigger);
            if (!triggerBack)
            {
                // The handler did not re-add it: restore the row so the player keeps that unlock.
                if (!consumed) em.DestroyEntity(ev);
                AppendProgression(em, prog, trigger);
                after = ReadState(em, prog);
            }
            // The record is spent only once the list is known good again (trigger row back).
            if (record != null && after.ProgressionSet.Contains(trigger))
            {
                File.Delete(record);
                summary["recordDeleted"] = true;
            }
            var still = UnlockArgs.Mismatches(after, removed, wantUnlocked: false);
            summary["readback"] = new
            {
                eventConsumed = consumed,
                triggerRestored = triggerBack ? "by the game's handler" : "re-appended raw (the handler did not run; derived buffers were not rebuilt)",
                stillUnlocked = still.Count,
                stillUnlockedList = UnlockArgs.Cap(still, limit).Select(g => new { guid = g, name = Name(prefabs, g) }).ToList(),
                progression = after.Progression.Count,
                blueprints = after.BlueprintsOn.Count,
                recipes = after.RecipesOn.Count,
            };
            summary["note"] = still.Count == 0
                ? "read back one second later: none of the removed GUIDs counts as unlocked"
                : "read back one second later: some removed GUIDs still count as unlocked (a remaining tech or book grants them)";
            return summary;
        });
    }

    // ---- game reads and writes -----------------------------------------------------------------

    private static Who ResolveUser(EntityManager em, UnlockArgs.UserRef wanted)
    {
        var users = Query.Entities(em, includeDisabled: true, includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < users.Count; i++)
        {
            var u = em.GetComponentData<User>(users[i]); // bool-bearing: the typed accessor
            bool match = wanted.Index != null
                ? u.Index == wanted.Index.Value
                : string.Equals(u.CharacterName.ToString(), wanted.Name, StringComparison.OrdinalIgnoreCase);
            if (!match) continue;
            string name = u.CharacterName.ToString();
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException($"user {wanted} has no character name yet");
            return new Who { User = users[i], Character = u.LocalCharacter._Entity, Index = u.Index, Name = name };
        }
        throw new InvalidOperationException($"no user {wanted}");
    }

    /// <summary><c>DebugEventsSystem.UnlockProgression</c> reads the sender character's <c>BuffBuffer</c> after the rebuild.</summary>
    private static void RequireCharacter(EntityManager em, Who who)
    {
        if (who.Character == Entity.Null || !em.Exists(who.Character))
            throw new InvalidOperationException($"'{who.Name}' has no live character (the game's unlock handler reads the character's buffs)");
    }

    private static unsafe Entity ProgressionEntity(EntityManager em, Entity user, string name)
    {
        var e = Bots.BotRecipes.ProgressionEntity(em, user, name);
        byte* raw = (byte*)em.GetComponentDataRawRO(user, ComponentIndex.Of<ProgressionMapper>(ComponentType.AccessMode.ReadOnly).TypeIndex);
        if (raw == null) throw new InvalidOperationException("ProgressionMapper read failed");
        if (raw[NetworkedEntityWaitingOffset] != 0) throw new InvalidOperationException("ProgressionMapper is waiting for sync; try again");
        return e;
    }

    private static unsafe UnlockArgs.State ReadState(EntityManager em, Entity progression)
    {
        var s = new UnlockArgs.State();
        var progType = ComponentIndex.Of<UnlockedProgressionElement>(ComponentType.AccessMode.ReadOnly);
        if (!em.HasComponent(progression, progType))
            throw new InvalidOperationException($"progression entity {Format(progression)} has no UnlockedProgressionElement");
        int n = em.GetBufferLength(progression, progType.TypeIndex);
        byte* raw = (byte*)em.GetBufferRawRO(progression, progType.TypeIndex);
        for (int i = 0; raw != null && i < n; i++) s.Progression.Add(*(int*)(raw + i * ProgressionStride));
        ReadDerived(em, progression, ComponentIndex.Of<UnlockedBlueprintElement>(ComponentType.AccessMode.ReadOnly), s.BlueprintsOn, s.BlueprintsContentLocked);
        ReadDerived(em, progression, ComponentIndex.Of<UnlockedRecipeElement>(ComponentType.AccessMode.ReadOnly), s.RecipesOn, s.RecipesContentLocked);
        // HasUnlockedProgression counts any shapeshift row, flag or not.
        ReadDerived(em, progression, ComponentIndex.Of<UnlockedShapeshiftElement>(ComponentType.AccessMode.ReadOnly), s.Shapeshifts, s.Shapeshifts);
        return s;
    }

    private static unsafe void ReadDerived(EntityManager em, Entity progression, ComponentType type, HashSet<int> on, HashSet<int> contentLocked)
    {
        if (!em.HasComponent(progression, type)) return;
        int n = em.GetBufferLength(progression, type.TypeIndex);
        byte* raw = (byte*)em.GetBufferRawRO(progression, type.TypeIndex);
        for (int i = 0; raw != null && i < n; i++)
        {
            byte* row = raw + i * DerivedStride;
            (row[DerivedFlagOffset] != 0 ? on : contentLocked).Add(*(int*)row);
        }
    }

    /// <summary>Distinct <c>PrefabGUID</c>s of the prefab entities carrying <paramref name="marker"/>, prefabs and disabled included.</summary>
    private static List<int> AllPrefabGuids(EntityManager em, ComponentType marker)
    {
        const EntityQueryOptions options = EntityQueryOptions.IncludePrefab | EntityQueryOptions.IncludeDisabledEntities;
        var guidType = ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly);
        var seen = new HashSet<int>();
        var list = new List<int>();
        foreach (var e in Query.EntitiesWithOptions(em, options, marker, guidType))
        {
            int g = em.GetComponentData<PrefabGUID>(e).GuidHash;
            if (g != 0 && seen.Add(g)) list.Add(g);
        }
        return list;
    }

    private static List<int> ResolveList(PrefabCollectionSystem prefabs, List<PrefabInspectArgs.Key> keys, bool requireKnown)
    {
        var list = new List<int>(keys.Count);
        foreach (var k in keys)
        {
            int g = k.IsHash ? k.Hash : ResolveName(prefabs, k.Name);
            if (requireKnown && !(prefabs._PrefabGuidToEntityMap.TryGetValue(new PrefabGUID(g), out var e) && e != Entity.Null))
                throw new ArgumentException($"prefab {g} is not in the prefab collection; refusing to add an unknown GUID");
            list.Add(g);
        }
        return list;
    }

    private static int ResolveName(PrefabCollectionSystem prefabs, string text)
    {
        try
        {
            if (prefabs._PrefabLookupMap.TryGetPrefabGuidWithName(text, out var guid, true) && guid._Value != 0) return guid._Value;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning($"unlock: TryGetPrefabGuidWithName('{text}') failed: {e.Message}"); }
        try
        {
            var dict = prefabs._SpawnableNameToPrefabGuidDictionary;
            if (dict != null && dict.ContainsKey(text)) return dict[text]._Value;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning($"unlock: spawnable name lookup '{text}' failed: {e.Message}"); }
        throw new ArgumentException(PrefabInspectArgs.UnknownNameError(text));
    }

    /// <summary>Refuse every raw write unless the interop agrees with the metadata offsets the strides above assume.</summary>
    private static void CheckLayouts()
    {
        Expect(Marshal.SizeOf<UnlockedProgressionElement>() == ProgressionStride, "UnlockedProgressionElement size is not 4");
        Expect((int)Marshal.OffsetOf<UnlockedProgressionElement>(nameof(UnlockedProgressionElement.UnlockedPrefab)) == 0, "UnlockedProgressionElement.UnlockedPrefab is not at 0x0");
        Expect((int)Marshal.OffsetOf<UnlockedBlueprintElement>(nameof(UnlockedBlueprintElement.UnlockedBlueprint)) == 0, "UnlockedBlueprintElement.UnlockedBlueprint is not at 0x0");
        Expect((int)Marshal.OffsetOf<UnlockedBlueprintElement>(nameof(UnlockedBlueprintElement.UserHasRequiredContentFlags)) == DerivedFlagOffset, "UnlockedBlueprintElement flag is not at 0x4");
        Expect((int)Marshal.OffsetOf<UnlockedRecipeElement>(nameof(UnlockedRecipeElement.UnlockedRecipe)) == 0, "UnlockedRecipeElement.UnlockedRecipe is not at 0x0");
        Expect((int)Marshal.OffsetOf<UnlockedRecipeElement>(nameof(UnlockedRecipeElement.UserHasRequiredContentFlags)) == DerivedFlagOffset, "UnlockedRecipeElement flag is not at 0x4");
        Expect((int)Marshal.OffsetOf<UnlockProgressionDebugEvent>(nameof(UnlockProgressionDebugEvent.PrefabGuid)) == 0, "UnlockProgressionDebugEvent.PrefabGuid is not at 0x0");
        Expect((int)Marshal.OffsetOf<HandleClientDebugEvent>(nameof(HandleClientDebugEvent.FromUserIndex)) == 0, "HandleClientDebugEvent.FromUserIndex is not at 0x0");
    }

    private static void Expect(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException(what + " (dump layout); refusing the raw write");
    }

    /// <summary>Replace <c>UnlockedProgressionElement</c> with <paramref name="guids"/> in one call, then read it back.</summary>
    private static unsafe void WriteProgression(EntityManager em, Entity progression, List<int> guids)
    {
        var type = ComponentIndex.Of<UnlockedProgressionElement>(ComponentType.AccessMode.ReadWrite);
        var data = new int[Math.Max(1, guids.Count)];
        guids.CopyTo(data);
        fixed (int* p = data)
            UnsafeEntityManagerUtility.SetBufferDataRaw(em, progression, type.TypeIndex.Value, ProgressionStride, 4, guids.Count, p);
        int n = em.GetBufferLength(progression, type.TypeIndex);
        int* raw = (int*)em.GetBufferRawRO(progression, type.TypeIndex);
        bool same = n == guids.Count && (n == 0 || raw != null);
        for (int i = 0; same && i < n; i++) same = raw[i] == guids[i];
        if (!same) throw new InvalidOperationException($"UnlockedProgressionElement write did not read back ({n} rows, expected {guids.Count})");
    }

    private static unsafe void AppendProgression(EntityManager em, Entity progression, int guid)
    {
        var type = ComponentIndex.Of<UnlockedProgressionElement>(ComponentType.AccessMode.ReadWrite);
        int value = guid;
        try { UnsafeEntityManagerUtility.AddBufferElement(em, progression, type.TypeIndex.Value, &value); }
        catch (Exception e)
        {
            // As Core PlayerRespawn: retry with the flag bits cleared from the index.
            Plugin.Logger?.LogWarning($"unlock: buffer append with flagged index failed: {e.Message}");
            UnsafeEntityManagerUtility.AddBufferElement(em, progression, type.TypeIndex.Value & SingletonsZeroSize.ClearFlagsMask, &value);
        }
    }

    /// <summary>
    /// The received-event entity the network layer builds for a client debug event:
    /// <c>UnlockProgressionDebugEvent</c> (raw, offset-checked), <c>NetworkEventType</c>,
    /// <c>ReceiveNetworkEventTag</c>, <c>FromCharacter</c>, <c>HandleClientDebugEvent</c> (raw).
    /// Same recipe as Core <c>UnitBlood</c> (verified live for <c>ConsumeBloodAdminEvent</c>).
    /// </summary>
    private static unsafe Entity RaiseUnlockProgression(EntityManager em, Who who, int guid)
    {
        var evType = ComponentIndex.Of<UnlockProgressionDebugEvent>(ComponentType.AccessMode.ReadWrite);
        var debugType = ComponentIndex.Of<HandleClientDebugEvent>(ComponentType.AccessMode.ReadWrite);
        var ev = em.CreateEntity(
            evType,
            ComponentIndex.Of<NetworkEventType>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<ReceiveNetworkEventTag>(ComponentType.AccessMode.ReadWrite),
            ComponentIndex.Of<FromCharacter>(ComponentType.AccessMode.ReadWrite),
            debugType);
        try
        {
            byte* raw = (byte*)em.GetComponentDataRawRW(ev, evType.TypeIndex);
            if (raw == null) throw new InvalidOperationException("GetComponentDataRawRW returned null for UnlockProgressionDebugEvent");
            *(int*)raw = guid;
            byte* dbg = (byte*)em.GetComponentDataRawRW(ev, debugType.TypeIndex);
            if (dbg == null) throw new InvalidOperationException("GetComponentDataRawRW returned null for HandleClientDebugEvent");
            *(int*)dbg = who.Index;
            em.SetComponentData(ev, new FromCharacter { User = who.User, Character = who.Character });
            em.SetComponentData(ev, new NetworkEventType { EventId = UnlockProgressionDebugEventId, IsDebugEvent = true, IsAdminEvent = false });
        }
        catch
        {
            em.DestroyEntity(ev); // never leave a half-written received event for the handler
            throw;
        }
        return ev;
    }

    // ---- record file ---------------------------------------------------------------------------

    private static string RecordPath(string name) => Path.Combine(Paths.ConfigPath, UnlockArgs.RecordFileName(name));

    private static UnlockArgs.Record? TryReadRecord(string path, out string? error)
    {
        error = null;
        if (!File.Exists(path)) return null;
        try { return UnlockArgs.ParseRecord(File.ReadAllText(path)); }
        catch (Exception e) when (e is FormatException or JsonException or IOException)
        {
            error = e.Message;
            return null;
        }
    }

    private static void WriteRecord(string path, UnlockArgs.Record record)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, UnlockArgs.Serialize(record));
        File.Move(tmp, path, overwrite: true);
    }

    // ---- guards and formatting -----------------------------------------------------------------

    /// <summary>The isolated test servers only: never a process, game root or config under the live server's folder.</summary>
    private static void RefuseLiveServer()
    {
        string?[] paths = { SafePath(() => Environment.ProcessPath), SafePath(() => Paths.GameRootPath), SafePath(() => Paths.ConfigPath), AppContext.BaseDirectory };
        foreach (var p in paths)
            if (p != null && DevGate.IsUnderLiveServerRoot(p))
                throw new InvalidOperationException($"unlock refuses to run on the live dedicated server ({DevGate.LiveServerRoot})");
    }

    private static string? SafePath(Func<string?> get)
    {
        try { return get(); } catch { return null; }
    }

    private static object Deferred(Func<object> readBack)
    {
        var deferred = new DeferredResult(ReadbackWaitMs);
        try
        {
            MainThread.PostDelayed(ReadbackDelaySeconds, () =>
            {
                try
                {
                    if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
                    deferred.SetResult(readBack());
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogError($"unlock read-back failed: {e}");
                    deferred.SetException(e);
                }
            }, deferred);
        }
        catch (Exception e)
        {
            deferred.SetException(e);
            throw;
        }
        return deferred;
    }

    private static object UserInfo(Who who) => new { name = who.Name, index = who.Index, entity = Format(who.User), character = Format(who.Character) };

    private static string? Name(PrefabCollectionSystem prefabs, int guid) => guid == 0 ? null : EntityOps.TryPrefabName(prefabs, new PrefabGUID(guid));

    private static string Format(Entity e) => $"{e.Index}:{e.Version}";
}
