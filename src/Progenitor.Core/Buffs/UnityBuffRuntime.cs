#if !PROGENITOR_TESTS
using System;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Progenitor.Content;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Buffs;

/// <summary>
/// The live side of <see cref="Buffs"/>: the only file in the module that names a <c>ProjectM</c>
/// or <c>Unity.Entities</c> type. Every call runs on the server main thread and every one of them
/// is one of the paths this repository already proves:
///
/// <list type="bullet">
/// <item><b>Apply</b> — <c>DebugEventsSystem.ApplyBuff(FromCharacter, ApplyBuffDebugEvent)</c>,
/// the same call the Dev <c>buff</c> op and <c>PrefabLiveRefreshApply</c> make. It queues an event;
/// the buff entity appears on a later update.</item>
/// <item><b>Confirm</b> — a walk of the character's <c>BuffBuffer</c>, the instantiation this
/// IL2CPP build ships and the registry's refresh already uses.</item>
/// <item><b>Duration</b> — a typed <c>LifeTime</c> write through <see cref="EntityExtensions"/>,
/// which gates the write on the ECS type size. <c>EndAction</c> is read and written back unchanged:
/// what happens at zero is the cloned vanilla buff's decision, not this module's.</item>
/// <item><b>Remove</b> — <c>DestroyUtility.Destroy(..., DestroyDebugReason.TryRemoveBuff)</c>.</item>
/// </list>
///
/// <para>No component is written on the <em>character</em>. Nothing here adds immortality, immunity
/// flags or aggro suppression: those live in the Sample's debug buff, they are protections rather
/// than a lifecycle, and they do not belong in a general authoring API.</para>
/// </summary>
internal sealed class UnityBuffRuntime : IBuffRuntime
{
    public bool IsServer => Plugin.IsServerProcess;

    public bool HasWorld
    {
        get
        {
            var world = World;
            return world != null && world.IsCreated;
        }
    }

    public int Epoch => Progenitor.WorldState.WorldEpoch.Current;

    private static World? World => Worlds.Server;

    public BuffResolve TryResolveBuff(string buffId, object? owner, string? pluginId, out int prefabGuid)
    {
        prefabGuid = 0;
        if (string.IsNullOrWhiteSpace(buffId)) return BuffResolve.NotRegistered;

        var world = World;
        if (world == null || !world.IsCreated) return BuffResolve.NotApplied;

        PrefabGUID guid = default;
        bool found = false;
        try
        {
            var definitions = PrefabRegistry.Definitions;
            for (int i = 0; i < definitions.Count; i++)
            {
                var definition = definitions[i];
                if (!string.Equals(definition.Id, buffId, StringComparison.Ordinal)) continue;
                if (definition.Kind != PrefabKind.Buff) continue;
                if (!OwnedBy(definition.Owner, owner, pluginId)) continue;
                guid = definition.Guid;
                found = true;
                break;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Buffs: definition lookup for '" + buffId + "' threw: " + e.Message);
            return BuffResolve.NotRegistered;
        }

        if (!found) return BuffResolve.NotRegistered;

        // Registered is not applied: the registry may not have put the clone into this world's
        // collection yet (no world at register time, a reload in flight, a refused apply).
        try
        {
            if (!PrefabLookup.TryGetEntity(world, guid, out var prefab) || !prefab.Exists(world.EntityManager))
                return BuffResolve.NotApplied;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Buffs: prefab lookup for '" + buffId + "' threw: " + e.Message);
            return BuffResolve.NotApplied;
        }

        prefabGuid = guid.GuidHash;
        return BuffResolve.Ok;
    }

    /// <summary>
    /// A definition is the caller's when the registry recorded the same owner object, or an owner
    /// whose type the caller's assembly declares. The second route is what lets a plugin register
    /// the definition from one class and apply it from another.
    ///
    /// <para>There is deliberately no BepInEx-id route here, unlike the unload sweep. The registry
    /// stores an owner <em>object</em> and no id, and an assembly's simple name is not a BepInEx
    /// GUID — matching one against the other would be a comparison that never succeeds, dressed as
    /// a third chance. <paramref name="pluginId"/> is kept on the signature because it is what the
    /// refusal line names.</para>
    /// </summary>
    private static bool OwnedBy(object? definitionOwner, object? owner, string? pluginId)
    {
        _ = pluginId;
        if (definitionOwner == null || owner == null) return false;
        if (ReferenceEquals(definitionOwner, owner)) return true;
        try
        {
            return ReferenceEquals(definitionOwner.GetType().Assembly, owner.GetType().Assembly);
        }
        catch
        {
            // An owner that cannot answer is not a match.
            return false;
        }
    }

    public BuffTargetCheck CheckTarget(BuffEntityId target)
    {
        var world = World;
        if (world == null || !world.IsCreated) return BuffTargetCheck.NoWorld;

        var em = world.EntityManager;
        var entity = target.ToEntity();
        try
        {
            if (!em.Exists(entity)) return BuffTargetCheck.Missing;
            // BuffBuffer is where the game records a wearer's buffs. No buffer, no buff - and
            // that includes a User entity, which is the commonest thing to pass by mistake.
            if (!em.HasComponent(entity, ComponentIndex.Of<BuffBuffer>(ComponentType.AccessMode.ReadOnly)))
                return BuffTargetCheck.NotBuffable;
            return BuffTargetCheck.Ok;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Buffs: target check on " + target + " threw: " + e.Message);
            return BuffTargetCheck.Missing;
        }
    }

    public bool RequestApply(BuffEntityId target, int prefabGuid, out string reason)
    {
        reason = "";
        var world = World;
        if (world == null || !world.IsCreated)
        {
            reason = "no server world";
            return false;
        }

        DebugEventsSystem? events;
        try { events = world.GetExistingSystemManaged<DebugEventsSystem>(); }
        catch (Exception e)
        {
            reason = "DebugEventsSystem lookup threw: " + e.Message;
            return false;
        }

        if (events == null)
        {
            reason = BuffRefusalText.For(BuffRefusal.ApplyPathUnavailable);
            return false;
        }

        var em = world.EntityManager;
        var character = target.ToEntity();
        Entity user = ResolveUser(em, character);

        try
        {
            var from = new FromCharacter { User = user, Character = character };
            // Stacks is always 1: see BuffRefreshPolicy for why this API does not expose stacking.
            var evt = new ApplyBuffDebugEvent { BuffPrefabGUID = new PrefabGUID(prefabGuid), Stacks = 1 };
            events.ApplyBuff(from, evt);
            return true;
        }
        catch (Exception e)
        {
            reason = "ApplyBuff threw: " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// The <c>User</c> half of <c>FromCharacter</c>. A player character points at its user; anything
    /// else (a spawned unit, a boss) is its own speaker, which is what the Dev op does too.
    /// </summary>
    private static Entity ResolveUser(EntityManager em, Entity character)
    {
        try
        {
            if (em.HasComponent(character, ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly)))
                return em.GetComponentData<PlayerCharacter>(character).UserEntity;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Buffs: PlayerCharacter read on " + character.Index + " threw: " + e.Message);
        }

        return character;
    }

    public bool TryFindInstance(BuffEntityId target, int prefabGuid, out BuffEntityId instance)
    {
        instance = BuffEntityId.None;
        var world = World;
        if (world == null || !world.IsCreated) return false;

        var em = world.EntityManager;
        var entity = target.ToEntity();
        try
        {
            if (!em.Exists(entity)) return false;
            if (!em.HasComponent(entity, ComponentIndex.Of<BuffBuffer>(ComponentType.AccessMode.ReadOnly)))
                return false;

            var buffer = em.GetBuffer<BuffBuffer>(entity, true);
            for (int i = 0; i < buffer.Length; i++)
            {
                if (buffer[i].PrefabGuid.GuidHash != prefabGuid) continue;
                var found = buffer[i].Entity;
                if (found == Entity.Null) continue;
                instance = BuffEntityId.From(found);
                return true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Buffs: BuffBuffer walk on " + target + " threw: " + e.Message);
        }

        return false;
    }

    public bool SetDuration(BuffEntityId instance, float seconds, out bool timed)
    {
        timed = false;
        var world = World;
        if (world == null || !world.IsCreated) return false;

        var em = world.EntityManager;
        var entity = instance.ToEntity();
        try
        {
            if (!em.Exists(entity)) return false;
            if (!entity.TryRead(em, out LifeTime life)) return false;

            timed = true;
            // Only Duration. EndAction stays whatever the cloned vanilla buff authored: what
            // happens when the timer reaches zero is the base buff's contract, and guessing a
            // value for it is exactly the kind of guess this project does not make.
            life.Duration = seconds;
            entity.Write(em, in life);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Buffs: LifeTime write on " + instance + " threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Read <c>ProjectM.Age.Value</c> and the instance's <c>LifeTime</c> in one pass. Never writes.
    ///
    /// <para>Both are read here rather than in two calls so the elapsed time and the duration it is
    /// measured against come from the same moment. A non-finite or negative age is reported as
    /// unreadable - but the decisive finiteness guard is ABOVE the seam, in
    /// <c>BuffApplication.Refresh</c>, because this file is excluded from the test assembly by
    /// <c>#if !PROGENITOR_TESTS</c> and an invariant the suite cannot see is not an invariant.</para>
    /// </summary>
    public bool TryReadTiming(BuffEntityId instance, out BuffTiming timing)
    {
        timing = default;
        var world = World;
        if (world == null || !world.IsCreated) return false;

        var em = world.EntityManager;
        var entity = instance.ToEntity();
        try
        {
            if (!em.Exists(entity)) return false;
            if (!entity.TryRead(em, out LifeTime life)) return false;
            if (!entity.TryRead(em, out Age age)) return false;

            float elapsed = age.Value;
            if (float.IsNaN(elapsed) || float.IsInfinity(elapsed) || elapsed < 0f) return false;

            timing = new BuffTiming(elapsed, life.Duration, EndOf(life.EndAction));
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Buffs: timing read on " + instance + " threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Map the game's <c>LifeTimeEndAction</c> onto the game-free enum. An unrecognized value
    /// becomes <see cref="BuffLifetimeEnd.Unknown"/> so the caller refuses rather than assuming the
    /// ordinary timed-buff meaning.
    /// </summary>
    private static BuffLifetimeEnd EndOf(LifeTimeEndAction action) => action switch
    {
        LifeTimeEndAction.None => BuffLifetimeEnd.None,
        LifeTimeEndAction.Kill => BuffLifetimeEnd.Kill,
        LifeTimeEndAction.Destroy => BuffLifetimeEnd.Destroy,
        LifeTimeEndAction.RemoveBuffStack => BuffLifetimeEnd.RemoveBuffStack,
        _ => BuffLifetimeEnd.Unknown,
    };

    public BuffDestroy DestroyInstance(BuffEntityId instance)
    {
        var world = World;
        if (world == null || !world.IsCreated) return BuffDestroy.NotPresent;

        var em = world.EntityManager;
        var entity = instance.ToEntity();

        // Start from "we do not know", not from "not present". If em.Exists ITSELF throws we have no
        // evidence the entity is gone, and defaulting to NotPresent would skip the watch for exactly
        // the case we are least sure about - the same shape as the bug this tri-state replaced.
        bool present = true;
        try
        {
            if (!em.Exists(entity))
            {
                present = false;
                return BuffDestroy.NotPresent;
            }

            // DestroyUtility.Destroy ONLY. Never add DestroyTag here (job BUFFP1, 2026-09-13).
            //
            // This used to do the "DIAG5 pair" - Destroy, then Add<DestroyTag> if the entity
            // survived. That was a misapplied precedent and it crashed a live server. The game's own
            // DestroyGroup diagnostic says so in plain words, naming our entity:
            //
            //   "We have 1 entities that have DestroyTag on them where we either added DestroyTag
            //    outside of the ProcessDestroyEventsSystem ... We should ONLY add DestroyTag inside
            //    of that system"
            //
            // DestroyUtility.Destroy already raises the event that carries the entity into that
            // system, which is what performs the detach. The extra tag routes the entity around that
            // pipeline, so it reaches DetachSystem destroyed-but-still-Attached and the detach walk
            // faults inside a Burst job.
            //
            // The scope distinction the old comment missed: BuffSweep's tag is for a buff on a unit
            // *Core is itself destroying*, and that path waits DestroyTagDeferFrames = 120 for buff
            // absence first. A buff on a live player character is the opposite case - the wearer
            // stays and the game must keep its AttachedBuffer correct. The two in-repo paths that
            // destroy a buff on a live wearer both issue Destroy only and then re-poll for absence:
            // Content/PrefabLiveRefreshApply.cs:434 and Bootstrap/ControlFormService.cs:505.
            //
            // "There is no later frame to notice a destroy that did not take" was the old comment's
            // reason for the tag. That is now fixed rather than worked around: Buffs owns a
            // retirement watch (Buffs.NoteRetiring / Buffs.Poll) which re-issues once and then
            // reports. Attach/Attached are NOT stripped and DetachEntity is NOT called - DIAG5's
            // standing prohibitions are unchanged.
            DestroyUtility.Destroy(em, entity, DestroyDebugReason.TryRemoveBuff);
            return BuffDestroy.Issued;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Buffs: destroy of " + instance + " threw: " + e.Message);
            // Present but not destroyed is NOT the same as "already gone": the buff is still on the
            // wearer with nothing scheduled to take it off. Report it as Failed so the retirement
            // watch picks it up and retries, instead of the caller treating a throw as "nothing to
            // do" and walking away.
            return present ? BuffDestroy.Failed : BuffDestroy.NotPresent;
        }
    }

    public bool Exists(BuffEntityId entity)
    {
        var world = World;
        if (world == null || !world.IsCreated) return false;
        try { return world.EntityManager.Exists(entity.ToEntity()); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Buffs: Exists(" + entity + ") threw: " + e.Message);
            return false;
        }
    }
}
#endif
