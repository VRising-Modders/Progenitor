using System;
using System.Text.Json;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Server debug-bridge <c>heal</c>: fill <c>Health.Value</c> from
/// <c>Health.MaxHealth._Value</c> (never a hardcoded 125) and <c>Blood.Value</c> from
/// <c>Blood.MaxBlood._Value</c> (100 when that reads 0), and clear <c>Health.IsDead</c>.
/// Writes are overlay pokes through <c>GetComponentDataRawRW</c> — Health and Blood have
/// bool fields so they are not C# <c>unmanaged</c>, and a guessed
/// <c>SetComponentDataRaw</c> length at TypeSize 0 is the Follower trap. A downed player
/// (a buff carrying <c>VampireDownedBuff</c>) has that buff removed after the health fill,
/// through the same <c>DestroyUtility.Destroy(..., TryRemoveBuff)</c> path as the <c>buff</c>
/// op; healing alone leaves the player downed. Other buffs (<c>BloodQualityBuff</c> and the
/// rest) are left alone. Replaces the manual <c>set</c> recipe the live loop used on the
/// character from <c>users</c>.
/// </summary>
public static unsafe class HealOps
{
    /// <summary>
    /// <paramref name="args"/>: <c>user</c> (character name) or <c>entity</c>
    /// (<c>index:version</c> of a character, or a User whose <c>LocalCharacter</c> is
    /// used). Server, main thread.
    /// </summary>
    public static object Run(World world, JsonElement args)
    {
        try
        {
            if (!Progenitor.Plugin.IsServerProcess)
                throw new InvalidOperationException("heal runs on the server side only");
            if (world == null || !world.IsCreated)
                throw new InvalidOperationException("the game world does not exist yet (not in a game)");

            var target = HealLogic.ParseTarget(args);
            var em = world.EntityManager;
            var character = ResolveCharacter(em, target);
            return Heal(em, character);
        }
        catch (Exception e) when (e is not ArgumentException and not InvalidOperationException)
        {
            Plugin.Logger?.LogError($"heal op threw: {e}");
            throw new InvalidOperationException($"heal op failed: {e.Message}", e);
        }
    }

    private static object Heal(EntityManager em, Entity character)
    {
        var healthType = ComponentIndex.Of<Health>(ComponentType.AccessMode.ReadWrite);
        if (!em.Exists(character))
            throw new InvalidOperationException($"entity {FormatEntity(character)} does not exist");
        if (!em.HasComponent(character, healthType))
            throw new InvalidOperationException($"entity {FormatEntity(character)} has no Health component");
        if (em.HasComponent(character, ComponentIndex.Of<Dead>(ComponentType.AccessMode.ReadOnly)))
            throw new InvalidOperationException(HealLogic.DeadComponentRefusal);

        byte* healthPtr = (byte*)em.GetComponentDataRawRW(character, healthType.TypeIndex);
        if (healthPtr == null)
            throw new InvalidOperationException($"GetComponentDataRawRW returned null for Health on {FormatEntity(character)}");

        float maxHealth = *(float*)(healthPtr + HealLogic.HealthMaxOffset);
        float healthBefore = *(float*)(healthPtr + HealLogic.HealthValueOffset);
        bool isDeadBefore = *(healthPtr + HealLogic.HealthIsDeadOffset) != 0;
        *(float*)(healthPtr + HealLogic.HealthValueOffset) = maxHealth;
        *(healthPtr + HealLogic.HealthIsDeadOffset) = 0;
        float healthAfter = *(float*)(healthPtr + HealLogic.HealthValueOffset);
        bool isDeadAfter = *(healthPtr + HealLogic.HealthIsDeadOffset) != 0;

        bool hasBlood = false;
        float bloodBefore = 0f, bloodAfter = 0f, maxBlood = 0f;
        try
        {
            var bloodType = ComponentIndex.Of<Blood>(ComponentType.AccessMode.ReadWrite);
            if (em.HasComponent(character, bloodType))
            {
                byte* bloodPtr = (byte*)em.GetComponentDataRawRW(character, bloodType.TypeIndex);
                if (bloodPtr == null)
                {
                    Plugin.Logger?.LogWarning($"heal: GetComponentDataRawRW returned null for Blood on {FormatEntity(character)}");
                }
                else
                {
                    hasBlood = true;
                    maxBlood = *(float*)(bloodPtr + HealLogic.BloodMaxOffset);
                    bloodBefore = *(float*)(bloodPtr + HealLogic.BloodValueOffset);
                    float target = HealLogic.BloodTarget(maxBlood);
                    *(float*)(bloodPtr + HealLogic.BloodValueOffset) = target;
                    bloodAfter = *(float*)(bloodPtr + HealLogic.BloodValueOffset);
                    maxBlood = target;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"heal: Blood write on {FormatEntity(character)} failed: {e.Message}");
        }

        // After the health fill, never before: a downed buff removed while health is still 0
        // must not leave the character at 0.
        var downed = RemoveDownedBuff(em, character);

        string name = TryCharacterName(em, character) ?? FormatEntity(character);
        Plugin.Logger?.LogInfo(HealLogic.LogLine(name, healthBefore, healthAfter, bloodBefore, bloodAfter, downed));
        return HealLogic.Result(
            FormatEntity(character),
            name,
            healthBefore,
            healthAfter,
            maxHealth,
            bloodBefore,
            bloodAfter,
            maxBlood,
            isDeadBefore,
            isDeadAfter,
            hasBlood,
            downed);
    }

    /// <summary>
    /// Removes the character's downed buff, if any: the <see cref="BuffBuffer"/> entry whose buff
    /// entity carries <see cref="VampireDownedBuff"/>. Same destroy path as the <c>buff</c> op.
    /// </summary>
    private static HealLogic.Downed RemoveDownedBuff(EntityManager em, Entity character)
    {
        try
        {
            var buff = FindDownedBuff(em, character);
            if (buff == Entity.Null)
                return HealLogic.Downed.None;
            int guid = em.HasComponent<PrefabGUID>(buff) ? em.GetComponentData<PrefabGUID>(buff)._Value : 0;
            DestroyUtility.Destroy(em, buff, DestroyDebugReason.TryRemoveBuff);
            // Destroy can leave the instance for one more frame (the buff op reports the same).
            bool goneNow = !em.Exists(buff) || FindDownedBuff(em, character) == Entity.Null;
            return new HealLogic.Downed(true, true, FormatEntity(buff), guid, goneNow);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"heal: removing the downed buff on {FormatEntity(character)} failed: {e.Message}");
            return HealLogic.Downed.None;
        }
    }

    private static Entity FindDownedBuff(EntityManager em, Entity character)
    {
        if (!em.HasComponent<BuffBuffer>(character))
            return Entity.Null;
        var buffer = em.GetBuffer<BuffBuffer>(character);
        for (int i = 0; i < buffer.Length; i++)
        {
            var buff = buffer[i].Entity;
            if (buff != Entity.Null && em.Exists(buff) && em.HasComponent<VampireDownedBuff>(buff))
                return buff;
        }
        return Entity.Null;
    }

    private static Entity ResolveCharacter(EntityManager em, HealLogic.Target target)
    {
        if (target.Kind == HealLogic.TargetKind.Entity)
        {
            if (!HealLogic.TryParseEntityId(target.Value, out var index, out var version))
                throw new ArgumentException($"args.entity must be 'index:version' (got '{target.Value}')");
            var entity = new Entity { Index = index, Version = version };
            if (!em.Exists(entity))
                throw new InvalidOperationException($"entity {target.Value} does not exist in this world (destroyed, or the index:version is from a different world)");
            return CharacterFrom(em, entity);
        }

        return CharacterFromUserName(em, target.Value);
    }

    /// <summary>
    /// A <c>PlayerCharacter</c> is the heal target. A <c>User</c> uses
    /// <c>LocalCharacter</c> (ghosts have none). Anything else is used as-is and must
    /// carry Health.
    /// </summary>
    private static Entity CharacterFrom(EntityManager em, Entity entity)
    {
        try
        {
            var pcType = ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly);
            if (em.HasComponent(entity, pcType))
                return entity;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"heal: PlayerCharacter check on {FormatEntity(entity)}: {e.Message}");
        }

        try
        {
            var userType = ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly);
            if (em.HasComponent(entity, userType))
            {
                var user = em.GetComponentData<User>(entity);
                var character = user.LocalCharacter._Entity;
                if (character == Entity.Null || !em.Exists(character))
                    throw new InvalidOperationException($"user {FormatEntity(entity)} has no character entity (ghosts cannot be healed)");
                return character;
            }
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            Plugin.Logger?.LogWarning($"heal: User read on {FormatEntity(entity)}: {e.Message}");
        }

        return entity;
    }

    private static Entity CharacterFromUserName(EntityManager em, string name)
    {
        var users = Query.Entities(
            em,
            includeDisabled: true,
            includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));

        Entity fallback = Entity.Null;
        string? fallbackName = null;
        for (int i = 0; i < users.Count; i++)
        {
            var entity = users[i];
            User user;
            try { user = em.GetComponentData<User>(entity); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"heal: User read on {FormatEntity(entity)}: {e.Message}");
                continue;
            }

            var characterName = user.CharacterName.ToString();
            if (!string.Equals(characterName, name, StringComparison.OrdinalIgnoreCase))
                continue;

            var character = user.LocalCharacter._Entity;
            if (character == Entity.Null || !em.Exists(character))
            {
                fallbackName ??= characterName;
                continue;
            }

            if (user.IsConnected)
                return character;
            if (fallback == Entity.Null)
            {
                fallback = character;
                fallbackName = characterName;
            }
        }

        if (fallback != Entity.Null)
            return fallback;

        if (fallbackName != null)
            throw new InvalidOperationException($"user '{name}' has no character entity (ghosts cannot be healed)");
        throw new InvalidOperationException($"no user named '{name}'");
    }

    private static string? TryCharacterName(EntityManager em, Entity character)
    {
        try
        {
            var pcType = ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly);
            if (!em.HasComponent(character, pcType))
                return null;
            var pc = em.GetComponentData<PlayerCharacter>(character);
            if (em.Exists(pc.UserEntity))
            {
                var userType = ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly);
                if (em.HasComponent(pc.UserEntity, userType))
                {
                    var user = em.GetComponentData<User>(pc.UserEntity);
                    var name = user.CharacterName.ToString();
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            var pcName = pc.Name.ToString();
            return string.IsNullOrEmpty(pcName) ? null : pcName;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"heal: name of {FormatEntity(character)} failed: {e.Message}");
            return null;
        }
    }

    private static string FormatEntity(Entity entity) => $"{entity.Index}:{entity.Version}";
}
