using System;
using System.Globalization;
using System.Text.Json;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>buff</c> op: apply or remove a buff prefab on a live entity through the
/// game's debug apply path:
/// <see cref="DebugEventsSystem.ApplyBuff"/> with an <see cref="ApplyBuffDebugEvent"/> and a
/// <see cref="FromCharacter"/>, then <see cref="DestroyUtility.Destroy"/> with
/// <see cref="DestroyDebugReason.TryRemoveBuff"/>. The instance is found by walking
/// <see cref="BuffBuffer"/> (<c>GetBuffer&lt;BuffBuffer&gt;</c> exists in this IL2CPP build).
/// Server, main thread.
/// </summary>
public static class BuffOps
{
    /// <summary>
    /// <paramref name="args"/>: <c>entity</c> required (<c>index:version</c>); <c>guid</c>
    /// required (int PrefabGUID hash or spawnable name); <c>apply</c> defaults true.
    /// <c>world</c> is selected by the caller (the Dev <c>Handlers</c> world resolver).
    /// </summary>
    public static object Run(World world, JsonElement args)
    {
        try
        {
            if (!Progenitor.Plugin.IsServerProcess)
                throw new InvalidOperationException("buff runs on the server side only");
            if (world == null || !world.IsCreated)
                throw new InvalidOperationException("the game world does not exist yet (not in a game)");

            var em = world.EntityManager;
            var entity = ReadEntity(em, args, "entity");
            var guid = ResolveGuid(world, args);
            bool apply = GetBool(args, "apply", true);

            return apply
                ? Apply(world, entity, guid)
                : Remove(world, entity, guid);
        }
        catch (Exception e) when (e is not ArgumentException and not InvalidOperationException)
        {
            Plugin.Logger?.LogError($"buff op threw: {e}");
            throw new InvalidOperationException($"buff op failed: {e.Message}", e);
        }
    }

    /// <summary>
    /// Apply <paramref name="guid"/> onto <paramref name="target"/> via
    /// <see cref="DebugEventsSystem.ApplyBuff"/>. Returns the buff instance if it is already
    /// present or if the debug apply created one this call.
    /// </summary>
    public static object Apply(World world, Entity target, PrefabGUID guid)
    {
        var em = world.EntityManager;
        if (!em.Exists(target))
            throw new InvalidOperationException($"entity {FormatEntity(target)} does not exist");

        // BuffBuffer lives on the character. A User entity would miss an already-applied row
        // and ApplyBuff a second copy; resolve to the character first.
        ResolveFromCharacter(em, target, out var userEntity, out var characterEntity);

        if (TryFindBuff(em, characterEntity, guid, out var existing) && em.Exists(existing))
        {
            Plugin.Logger?.LogInfo(
                $"buff: already present guid={guid._Value} on {FormatEntity(characterEntity)} instance={FormatEntity(existing)}");
            return Result(apply: true, already: true, characterEntity, guid, existing, world, "already applied");
        }

        var des = world.GetExistingSystemManaged<DebugEventsSystem>();
        if (des == null)
            throw new InvalidOperationException($"DebugEventsSystem is not in world '{world.Name}' yet");

        var from = new FromCharacter { User = userEntity, Character = characterEntity };
        var evt = new ApplyBuffDebugEvent { BuffPrefabGUID = guid, Stacks = 1 };
        Plugin.Logger?.LogInfo(
            $"buff: ApplyBuff guid={guid._Value} character={FormatEntity(characterEntity)} user={FormatEntity(userEntity)}");
        des.ApplyBuff(from, evt);

        if (!TryFindBuff(em, characterEntity, guid, out var instance) || !em.Exists(instance))
        {
            Plugin.Logger?.LogWarning(
                $"buff: ApplyBuff returned and BuffBuffer has no guid={guid._Value} on {FormatEntity(characterEntity)}");
            return Result(apply: true, already: false, characterEntity, guid, Entity.Null, world,
                "ApplyBuff ran but no BuffBuffer row yet (may appear next tick)");
        }

        Plugin.Logger?.LogInfo($"buff: applied guid={guid._Value} instance={FormatEntity(instance)}");
        return Result(apply: true, already: false, characterEntity, guid, instance, world, "applied");
    }

    /// <summary>
    /// Destroy the buff instance of <paramref name="guid"/> on <paramref name="target"/>, if any.
    /// </summary>
    public static object Remove(World world, Entity target, PrefabGUID guid)
    {
        var em = world.EntityManager;
        if (!em.Exists(target))
            throw new InvalidOperationException($"entity {FormatEntity(target)} does not exist");

        ResolveFromCharacter(em, target, out _, out var characterEntity);

        if (!TryFindBuff(em, characterEntity, guid, out var instance) || !em.Exists(instance))
        {
            Plugin.Logger?.LogInfo($"buff: nothing to remove guid={guid._Value} on {FormatEntity(characterEntity)}");
            return Result(apply: false, already: false, characterEntity, guid, Entity.Null, world, "not present");
        }

        Plugin.Logger?.LogInfo(
            $"buff: Destroy guid={guid._Value} instance={FormatEntity(instance)} reason=TryRemoveBuff");
        DestroyUtility.Destroy(em, instance, DestroyDebugReason.TryRemoveBuff);

        bool gone = !em.Exists(instance) || !TryFindBuff(em, characterEntity, guid, out _);
        return Result(apply: false, already: false, characterEntity, guid, instance, world,
            gone ? "removed" : "Destroy issued (instance may last one frame)");
    }

    /// <summary>
    /// True when <paramref name="target"/>'s <see cref="BuffBuffer"/> contains
    /// <paramref name="guid"/>. <c>GetBuffer&lt;BuffBuffer&gt;</c> is an IL2CPP instantiation
    /// this build ships (per the game's metadata); a missing buffer is a miss, not a crash.
    /// </summary>
    public static bool TryFindBuff(EntityManager em, Entity target, PrefabGUID guid, out Entity buffEntity)
    {
        buffEntity = Entity.Null;
        try
        {
            var bufferType = ComponentIndex.Of<BuffBuffer>(ComponentType.AccessMode.ReadOnly);
            if (!em.Exists(target) || !em.HasComponent(target, bufferType))
                return false;
            var buffer = em.GetBuffer<BuffBuffer>(target, true);
            int hash = guid._Value;
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].PrefabGuid._Value != hash) continue;
                buffEntity = buffer[i].Entity;
                return buffEntity != Entity.Null;
            }
            return false;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"buff: BuffBuffer walk on {FormatEntity(target)} threw: {e.Message}");
            buffEntity = Entity.Null;
            return false;
        }
    }

    private static object Result(
        bool apply,
        bool already,
        Entity target,
        PrefabGUID guid,
        Entity instance,
        World world,
        string note)
    {
        string? name = null;
        try
        {
            if (PrefabLookup.TryGetName(world, guid, out var found) && !string.IsNullOrEmpty(found))
                name = found;
        }
        catch { }

        return new
        {
            apply,
            already,
            entity = FormatEntity(target),
            buff = instance == Entity.Null ? null : FormatEntity(instance),
            guid = guid._Value,
            name,
            note,
        };
    }

    /// <summary>
    /// Speaker for <see cref="FromCharacter"/>: a <c>PlayerCharacter</c> uses its
    /// <c>UserEntity</c>; a <c>User</c> uses <c>LocalCharacter</c>; anything else is used as
    /// both sides (the debug apply still wants a Character).
    /// </summary>
    private static void ResolveFromCharacter(EntityManager em, Entity entity, out Entity userEntity, out Entity characterEntity)
    {
        userEntity = entity;
        characterEntity = entity;
        try
        {
            var pcType = ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly);
            if (em.HasComponent(entity, pcType))
            {
                var pc = em.GetComponentData<PlayerCharacter>(entity);
                characterEntity = entity;
                userEntity = pc.UserEntity;
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"buff: PlayerCharacter read on {FormatEntity(entity)}: {e.Message}");
        }

        try
        {
            var userType = ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly);
            if (em.HasComponent(entity, userType))
            {
                var user = em.GetComponentData<User>(entity);
                userEntity = entity;
                characterEntity = user.LocalCharacter._Entity;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"buff: User read on {FormatEntity(entity)}: {e.Message}");
        }
    }

    private static PrefabGUID ResolveGuid(World world, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("guid", out var el))
            throw new ArgumentException("args.guid is required (int PrefabGUID hash or spawnable name)");

        if (el.ValueKind == JsonValueKind.Number)
            return new PrefabGUID(el.GetInt32());

        if (el.ValueKind == JsonValueKind.String)
        {
            var text = (el.GetString() ?? "").Trim();
            if (text.Length == 0)
                throw new ArgumentException("args.guid is empty");
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hash))
                return new PrefabGUID(hash);

            if (PrefabLookup.TryGetGuidByName(world, text, out var byName) && byName._Value != 0)
                return byName;

            try
            {
                var prefabs = world.GetExistingSystemManaged<PrefabCollectionSystem>();
                if (prefabs != null
                    && prefabs._PrefabLookupMap.TryGetPrefabGuidWithName(text, out byName, true)
                    && byName._Value != 0)
                    return byName;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"buff: TryGetPrefabGuidWithName('{text}') failed: {e.Message}");
            }

            throw new ArgumentException($"no prefab named '{text}'. Pass the integer PrefabGUID hash, or a spawnable name.");
        }

        throw new ArgumentException("args.guid must be an int hash or a string name");
    }

    private static Entity ReadEntity(EntityManager em, JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"args.{name} is required ('index:version')");
        var text = el.GetString() ?? "";
        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1
            || !int.TryParse(text.AsSpan(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
            || !int.TryParse(text.AsSpan(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
        {
            throw new ArgumentException($"args.{name} must be 'index:version' (got '{text}')");
        }

        var entity = new Entity { Index = index, Version = version };
        if (!em.Exists(entity))
            throw new InvalidOperationException($"entity {text} does not exist in this world");
        return entity;
    }

    private static bool GetBool(JsonElement args, string name, bool fallback)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return fallback;
        try
        {
            if (el.ValueKind is JsonValueKind.True or JsonValueKind.False) return el.GetBoolean();
            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var s)) return s;
        }
        catch { }
        return fallback;
    }

    private static string FormatEntity(Entity entity) => $"{entity.Index}:{entity.Version}";
}
