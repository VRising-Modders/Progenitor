using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Abilities;

/// <summary>
/// Runtime half of <see cref="PluginAbilities.SetSlot"/>. The game's own seam: a buff whose prefab
/// carries a <c>ReplaceAbilityOnSlotBuff</c> element makes <c>ReplaceAbilityOnSlotSystem</c> put that
/// group on that slot while the buff is on the character, and take it off again when the buff goes
/// (research ability-cast-and-triggers.md section 2). The buff is a Progenitor-owned clone of
/// <c>Buff_VBlood_Ability_Replace</c> with the element written on the clone, never on the vanilla
/// prefab. Server only, main thread.
/// </summary>
internal static unsafe class AbilitySlots
{
    // Holders per (character, clone): two handles for the same assignment share one buff instance,
    // which is removed when the last is released.
    private static readonly Dictionary<(int Epoch, int Index, int Version, int Clone), int> Holds = new();

    /// <summary>
    /// Make sure the clone prefab carries exactly the one element for this assignment. The registry
    /// builds the clone from the vanilla prefab, whose buffer is empty, and rebuilds it on a world
    /// change, so this runs before every apply rather than once.
    /// </summary>
    internal static bool EnsureElement(World world, PrefabGUID clone, int slot, int group, out string why)
    {
        if (!PrefabLookup.TryGetEntity(world, clone, out var prefab))
        {
            why = "slot buff " + clone.GuidHash + " has no prefab yet";
            return false;
        }
        var em = world.EntityManager;
        var type = ComponentIndex.Of<ReplaceAbilityOnSlotBuff>(ComponentType.AccessMode.ReadWrite);
        if (!em.HasComponent(prefab, type))
        {
            why = "slot buff " + clone.GuidHash + " has no ReplaceAbilityOnSlotBuff (the source changed?)";
            return false;
        }
        int typeIndex = type.TypeIndex.Value;
        int length = em.GetBufferLength(prefab, typeIndex);
        if (length == 1)
        {
            byte* existing = (byte*)em.GetBufferRawRO(prefab, typeIndex);
            if (existing != null
                && *(int*)(existing + Layout.ReplaceAbilityOnSlotSlotOffset) == slot
                && *(int*)(existing + Layout.ReplaceAbilityOnSlotNewGroupOffset) == group)
            {
                why = "";
                return true;
            }
        }
        if (length != 0)
        {
            why = "slot buff " + clone.GuidHash + " already carries " + length + " other element(s)";
            return false;
        }
        byte* element = stackalloc byte[AbilitySlotPolicy.ElementSize];
        AbilitySlotPolicy.WriteElement(new Span<byte>(element, AbilitySlotPolicy.ElementSize), slot, group);
        UnsafeEntityManagerUtility.AddBufferElement(em, prefab, typeIndex, element);
        why = "";
        return true;
    }

    /// <summary>
    /// Put the clone on a player character (one instance however many holders). The buff is applied
    /// whenever no live instance is there, whatever the holder count says: a buff removed by
    /// something else (a cleanse, another mod, an admin) or destroyed by a release earlier in the
    /// same frame must not leave a handle that owns nothing. <c>ApplyBuff</c> creates the instance
    /// synchronously (see <c>AiVariants.PutMarker</c>).
    /// </summary>
    internal static bool Apply(World world, Entity character, PrefabGUID clone, out string why)
    {
        var em = world.EntityManager;
        if (character == Entity.Null || !em.Exists(character))
        {
            why = AbilityCooldownPolicy.MissingCharacterRefusal(character.Index, character.Version);
            return false;
        }
        // Players only: the apply goes through FromCharacter { User, Character }, verified with a
        // player's user entity; a creature has no user to name.
        if (!em.HasComponent<PlayerCharacter>(character)
            || !em.HasComponent(character, ComponentIndex.Of<AbilityGroupSlotBuffer>(ComponentType.AccessMode.ReadOnly)))
        {
            why = AbilitySlotPolicy.NoSlotsRefusal(character.Index, character.Version);
            return false;
        }
        if (!TryFindInstance(em, character, clone, out _))
        {
            var debug = world.GetExistingSystemManaged<DebugEventsSystem>();
            if (debug == null)
            {
                why = AbilityCooldownPolicy.NotReadyRefusal();
                return false;
            }
            var user = em.GetComponentData<PlayerCharacter>(character).UserEntity;
            debug.ApplyBuff(
                new FromCharacter { User = user, Character = character },
                new ApplyBuffDebugEvent { BuffPrefabGUID = clone, Stacks = 1 });
        }
        var key = Key(character, clone);
        Holds.TryGetValue(key, out int holders);
        Holds[key] = holders + 1;
        why = "";
        return true;
    }

    /// <summary>Drop one holder; the last one removes the buff, which gives the slot back.</summary>
    internal static void Release(World? world, Entity character, PrefabGUID clone)
    {
        var key = Key(character, clone);
        if (!Holds.TryGetValue(key, out int holders)) return;
        if (holders > 1)
        {
            Holds[key] = holders - 1;
            return;
        }
        Holds.Remove(key);
        if (world == null || !world.IsCreated) return;
        var em = world.EntityManager;
        if (!em.Exists(character) || !TryFindInstance(em, character, clone, out var instance)) return;
        DestroyUtility.Destroy(em, instance, DestroyDebugReason.TryRemoveBuff);
        // The instance lingers until the game's destroy pass; a SetSlot in the meantime must not
        // take it for a live one.
        Dying.Add(instance);
    }

    // Holders per (world epoch, character, clone): the table outlives a world, and a character's
    // index and version can come back in the next one.
    private static (int Epoch, int Index, int Version, int Clone) Key(Entity character, PrefabGUID clone) =>
        (Progenitor.WorldState.WorldEpoch.Current, character.Index, character.Version, clone.GuidHash);

    // Instances this class destroyed that may still exist this frame.
    private static readonly HashSet<Entity> Dying = new();

    private static bool TryFindInstance(EntityManager em, Entity character, PrefabGUID clone, out Entity instance)
    {
        instance = Entity.Null;
        Dying.RemoveWhere(e => !em.Exists(e));
        if (!em.HasComponent(character, ComponentIndex.Of<BuffBuffer>(ComponentType.AccessMode.ReadOnly))) return false;
        var buffer = em.GetBuffer<BuffBuffer>(character);
        for (int i = 0; i < buffer.Length; i++)
        {
            if (buffer[i].PrefabGuid._Value != clone.GuidHash) continue;
            var candidate = buffer[i].Entity;
            if (candidate == Entity.Null || !em.Exists(candidate) || Dying.Contains(candidate)) continue;
            instance = candidate;
            return true;
        }
        return false;
    }
}
