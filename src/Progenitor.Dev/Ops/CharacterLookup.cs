using System;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Unity.Entities;

namespace Progenitor.Dev.Ops;

/// <summary>
/// "Which entity does this argument mean" for the character-targeted debug-bridge ops
/// (<c>teleport</c>, <c>give</c>). A <c>PlayerCharacter</c> is itself; a <c>User</c> resolves
/// through <c>LocalCharacter</c> (ghosts have none and are refused); anything else is used
/// as-is. A character name prefers the connected user, then falls back to an offline one, so
/// the same name works whether the player is logged in or not.
///
/// <c>HealOps</c> keeps its own live-verified copy of this walk; it is deliberately not
/// refactored onto this class, because <c>heal</c>'s behavior is pinned by a live run and
/// this file exists to stop the two new ops each growing a third variant.
/// </summary>
internal static class CharacterLookup
{
    /// <summary>The character entity for an <c>index:version</c> argument.</summary>
    public static Entity FromEntityId(EntityManager em, string text, string argName)
    {
        if (!InspectArgs.TryParseEntityId(text, out var index, out var version))
            throw new ArgumentException($"args.{argName} must be 'index:version' (got '{text}')");
        var entity = new Entity { Index = index, Version = version };
        if (!em.Exists(entity))
            throw new InvalidOperationException($"entity {text} does not exist in this world (destroyed, or the index:version is from a different world)");
        return CharacterFrom(em, entity, text);
    }

    /// <summary>
    /// A <c>PlayerCharacter</c> is the target. A <c>User</c> uses <c>LocalCharacter</c>.
    /// Anything else is used as-is — a chest or a unit is a legitimate <c>give</c> target.
    /// </summary>
    public static Entity CharacterFrom(EntityManager em, Entity entity, string label)
    {
        try
        {
            if (em.HasComponent(entity, ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly)))
                return entity;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"PlayerCharacter check on {label}: {e.Message}");
        }

        try
        {
            if (em.HasComponent(entity, ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly)))
            {
                var character = em.GetComponentData<User>(entity).LocalCharacter._Entity;
                if (character == Entity.Null || !em.Exists(character))
                    throw new InvalidOperationException($"user {label} has no character entity (ghosts have none)");
                return character;
            }
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            Plugin.Logger?.LogWarning($"User read on {label}: {e.Message}");
        }

        return entity;
    }

    /// <summary>
    /// The character behind a <c>User.CharacterName</c>. A connected user wins over an
    /// offline one with the same name; a user with no character is a named refusal, not a
    /// silent miss.
    /// </summary>
    public static Entity FromUserName(EntityManager em, string name)
    {
        var users = Query.Entities(
            em,
            includeDisabled: true,
            includeSystems: false,
            ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));

        Entity fallback = Entity.Null;
        bool sawNameWithoutCharacter = false;
        for (int i = 0; i < users.Count; i++)
        {
            var entity = users[i];
            User user;
            try { user = em.GetComponentData<User>(entity); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"User read on {entity.Index}:{entity.Version}: {e.Message}");
                continue;
            }

            if (!string.Equals(user.CharacterName.ToString(), name, StringComparison.OrdinalIgnoreCase))
                continue;

            var character = user.LocalCharacter._Entity;
            if (character == Entity.Null || !em.Exists(character))
            {
                sawNameWithoutCharacter = true;
                continue;
            }

            if (user.IsConnected) return character;
            if (fallback == Entity.Null) fallback = character;
        }

        if (fallback != Entity.Null) return fallback;
        if (sawNameWithoutCharacter)
            throw new InvalidOperationException($"user '{name}' has no character entity (ghosts have none)");
        throw new InvalidOperationException($"no user named '{name}'");
    }

    /// <summary>The display name for a character, or null. Same User-first order <c>players</c> uses.</summary>
    public static string? TryName(EntityManager em, Entity character) => InspectOps.TryName(em, character);
}
