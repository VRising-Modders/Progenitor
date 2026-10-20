using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;
using PrefabTag = Unity.Entities.Prefab;

namespace Progenitor.Content;

/// <summary>
/// Server: destroy live buff instances through <c>DestroyUtility.Destroy(..., TryRemoveBuff)</c>
/// and re-apply through <c>DebugEventsSystem.ApplyBuff</c> (the same path Sample <c>.nodrain</c>
/// uses) so a changed clone actually reaches characters. Client: never destroy; copy the clone's
/// <c>ShowOnBuffBarData</c> onto existing instances (icon is a managed Sprite the instance copied
/// at spawn — evidence 112 — while Header/Description keys already follow the table). Never
/// remove runtime buffers (<c>BuffSequenceSpawned</c>) from a live instance: the game's destroy
/// path is what stops the sequence (evidence 109).
/// </summary>
internal static unsafe class PrefabLiveRefreshApply
{
    /// <summary><c>Buff.Target</c> is an <c>Entity</c> at field offset 8 (the IL viewer, inferred from stubs).</summary>
    private const int BuffTargetOffset = Layout.BuffTargetOffset;

    private static readonly object Gate = new();
    private static readonly Queue<Pending> Queue = new();
    private static readonly HashSet<long> QueuedKeys = new();
    private static bool _hooked;
    [ThreadStatic] private static int _lastNudgedHash;
    [ThreadStatic] private static int _lastNudgedCount;

    private struct Pending
    {
        public PrefabGUID Guid;
        public string Id;
        public Entity Instance;
        public Entity Character;
        public Entity User;
        public bool DestroyedByUs;
        public byte Stacks;
        public float LifeTimeDuration;
        public bool HasLifeTime;
        public int WaitFrames;

        /// <summary>Set once <c>ApplyBuff</c> has been queued for this item. From here the item
        /// is no longer owed an apply; it is only waiting to write the duration back.</summary>
        public bool Applied;

        /// <summary>Frames spent looking for the instance <c>ApplyBuff</c> was asked to create.</summary>
        public int RestoreFrames;

        /// <summary>How many times an owed apply has been deferred instead of dropped.</summary>
        public int OwedRetries;
    }

    /// <summary>
    /// After a successful clone apply or an edit pass: server may queue destroy+reapply
    /// (opt-in); client always nudges HUD fields on existing instances of this GUID.
    /// </summary>
    public static void AfterCloneChanged(
        World world,
        PrefabDefinition def,
        bool alreadyClonedThisProcess,
        bool definitionChanged,
        bool retargeted,
        bool editPassChangedClone,
        bool previousFingerprintMissing)
    {
        try
        {
            if (world == null || !world.IsCreated || def == null) return;
            if (def.Kind != PrefabKind.Buff) return;

            var em = world.EntityManager;
            Entity clone = Entity.Null;
            try { PrefabLookup.TryGetEntity(world, def.Guid, out clone); }
            catch { /* collect still runs */ }

            var instances = CollectLiveBuffInstances(em, def.Guid, clone);
            if (instances.Count == 0) return;

            if (!Plugin.IsServerProcess)
            {
                NudgeClientHud(em, clone, def, instances, PrefabLiveRefresh.ReasonIcon);
                return;
            }

            bool willRefresh = PrefabLiveRefresh.ShouldRefresh(
                    def.RefreshLiveInstances,
                    isBuff: true,
                    alreadyClonedThisProcess,
                    definitionChanged,
                    retargeted,
                    editPassChangedClone,
                    previousFingerprintMissing);
            if (PrefabLiveRefresh.ShouldLogSkip(
                    def.RefreshLiveInstances,
                    isBuff: true,
                    alreadyClonedThisProcess,
                    willRefresh))
            {
                Plugin.Logger?.LogInfo(PrefabLiveRefresh.FormatSkippedUnchanged(def.Id));
                return;
            }

            if (!willRefresh)
                return;

            string reason = PrefabLiveRefresh.PickReason(
                definitionChanged, retargeted, editPassChangedClone, previousFingerprintMissing);
            int queued = Enqueue(em, def, instances, reason);
            if (queued > 0)
                Plugin.Logger?.LogInfo(PrefabLiveRefresh.FormatRefreshed(def.Id, queued, reason));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: live refresh of '{def?.Id}' threw: {e}");
        }
    }

    /// <summary>
    /// After a clone's icon (or name/description) was written: copy HUD fields onto live
    /// instances of that GUID and log <c>PrefabRegistry: nudged N live instance(s) of 'id' (icon)</c>.
    /// Runs after the swap-time apply and after the late plugin-reload apply (evidence 112:
    /// the late icon write used to skip this).
    /// </summary>
    public static void AfterIconApplied(EntityManager em, Entity clone, PrefabDefinition def)
    {
        try
        {
            if (Plugin.IsServerProcess) return;
            if (def == null || def.Kind != PrefabKind.Buff) return;
            var instances = CollectLiveBuffInstances(em, def.Guid, clone);
            NudgeClientHud(em, clone, def, instances, PrefabLiveRefresh.ReasonIcon);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: after-icon nudge of '" + (def?.Id ?? "") + "' threw: " + e.Message);
        }
    }

    /// <summary>
    /// Drop the frame hook and the pending queue (Core Uninstall). Items that already
    /// destroyed their instance and have not re-applied it yet are drained synchronously
    /// first: clearing the queue under them is one of the three paths that left the holder
    /// permanently without the buff (R27 finding 13). A second Core swap landing in the
    /// one-frame window between destroy and re-apply is exactly this case.
    /// </summary>
    public static void Uninstall()
    {
        try
        {
            Pending[] owed;
            lock (Gate)
            {
                owed = TakeOwedNoLock();
                Queue.Clear();
                QueuedKeys.Clear();
                if (_hooked)
                {
                    Plugin.Frame -= OnFrame;
                    _hooked = false;
                }
            }

            int drained = 0;
            for (int i = 0; i < owed.Length; i++)
            {
                if (TryApplyOwedNow(owed[i])) drained++;
            }
            if (drained > 0)
                Plugin.Logger?.LogInfo(PrefabLiveRefresh.FormatOwedApplyDrained(drained));

            _lastNudgedHash = 0;
            _lastNudgedCount = 0;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: live-refresh uninstall threw: " + e.Message);
        }
    }

    /// <summary>Queued items that destroyed their instance and never re-applied it.</summary>
    private static Pending[] TakeOwedNoLock()
    {
        if (Queue.Count == 0) return Array.Empty<Pending>();
        var owed = new List<Pending>();
        foreach (var q in Queue)
        {
            if (PrefabLiveRefresh.OwesApply(q.DestroyedByUs, q.Applied))
                owed.Add(q);
        }
        return owed.Count == 0 ? Array.Empty<Pending>() : owed.ToArray();
    }

    /// <summary>
    /// Last-chance synchronous <c>ApplyBuff</c> for an owed item. No duration restore: there
    /// is no next frame to find the new instance on, and a re-applied buff with the clone's
    /// authored duration beats a holder who silently lost it.
    /// </summary>
    private static bool TryApplyOwedNow(Pending item)
    {
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated)
            {
                Plugin.Logger?.LogError(PrefabLiveRefresh.FormatOwedApplyLost(item.Id, "uninstall with no server world"));
                return false;
            }
            var em = world.EntityManager;
            if (item.Character == Entity.Null || !em.Exists(item.Character)) return false;
            var des = world.GetExistingSystemManaged<DebugEventsSystem>();
            if (des == null)
            {
                Plugin.Logger?.LogError(PrefabLiveRefresh.FormatOwedApplyLost(item.Id, "uninstall with no DebugEventsSystem"));
                return false;
            }
            des.ApplyBuff(
                new FromCharacter
                {
                    User = item.User != Entity.Null && em.Exists(item.User) ? item.User : item.Character,
                    Character = item.Character,
                },
                new ApplyBuffDebugEvent
                {
                    BuffPrefabGUID = item.Guid,
                    Stacks = PrefabLiveRefresh.PreserveStacks(item.Stacks),
                });
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError(PrefabLiveRefresh.FormatOwedApplyLost(item.Id, "uninstall drain threw: " + e.Message));
            return false;
        }
    }

    /// <summary>Pin text for <c>PrefabRegistry.DescribePins</c>.</summary>
    public static string? DescribePin()
    {
        lock (Gate)
        {
            if (!_hooked && Queue.Count == 0) return null;
            return "liveRefresh=" + Queue.Count;
        }
    }

    /// <summary>
    /// Live <c>Buff</c> + <c>PrefabGUID</c> entities that are not the prefab clone.
    /// Same skip rules as FJ's count (no <c>Prefab</c> tag; include disabled). Empty on throw.
    /// </summary>
    internal static List<Entity> CollectLiveBuffInstances(EntityManager em, PrefabGUID target, Entity prefabClone)
    {
        var result = new List<Entity>();
        try
        {
            var buffType = ComponentIndex.Of<Buff>(ComponentType.AccessMode.ReadOnly);
            var guidType = ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly);
            var prefabType = ComponentIndex.Of<PrefabTag>(ComponentType.AccessMode.ReadOnly);
            var entities = Query.Entities(em, includeDisabled: true, includeSystems: false, buffType, guidType);
            int hash = target.GuidHash;
            // Every buff in the world (disabled ones too) passes through here once per settled buff definition, on the
            // main thread: read the GUID first (one call) and pay for the other checks only on a match. The query's
            // entities were all alive when it ran, this same frame.
            for (int i = 0; i < entities.Count; i++)
            {
                var e = entities[i];
                if (e == prefabClone) continue;
                PrefabGUID pg;
                try { pg = em.GetComponentData<PrefabGUID>(e); }
                catch { continue; }
                if (pg.GuidHash != hash) continue;
                if (!em.Exists(e)) continue;
                if (em.HasComponent(e, prefabType)) continue;
                result.Add(e);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"PrefabRegistry: live buff query for guid {target.GuidHash} threw: {e.Message}");
        }
        return result;
    }

    private static int Enqueue(EntityManager em, PrefabDefinition def, List<Entity> instances, string reason)
    {
        int added = 0;
        lock (Gate)
        {
            for (int i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
                if (!TryResolveCharacter(em, instance, out var character, out var user))
                {
                    Plugin.Logger?.LogWarning(
                        $"PrefabRegistry: live refresh of '{def.Id}' skipped instance {instance.Index}:{instance.Version} (no character)");
                    continue;
                }
                long key = Key(def.Guid.GuidHash, character);
                if (!QueuedKeys.Add(key)) continue;
                Queue.Enqueue(new Pending
                {
                    Guid = def.Guid,
                    Id = def.Id,
                    Instance = instance,
                    Character = character,
                    User = user,
                    DestroyedByUs = false,
                    Stacks = 1,
                    LifeTimeDuration = 0f,
                    HasLifeTime = false,
                    WaitFrames = 0,
                    Applied = false,
                    RestoreFrames = 0,
                    OwedRetries = 0,
                });
                added++;
            }
            if (added > 0) EnsureHookNoLock();
        }
        _ = reason;
        return added;
    }

    private static void EnsureHookNoLock()
    {
        if (_hooked) return;
        Plugin.Frame += OnFrame;
        _hooked = true;
    }

    private static void OnFrame()
    {
        try
        {
            Pending item;
            lock (Gate)
            {
                if (Queue.Count == 0)
                {
                    if (_hooked)
                    {
                        Plugin.Frame -= OnFrame;
                        _hooked = false;
                    }
                    return;
                }
                // One item per frame, deliberately: each costs at least two frames (destroy,
                // then apply) plus up to RestoreDurationFrames for the duration write, so a
                // definition held by 40 players is several seconds of staggered work rather
                // than one hitch. That is three orders of magnitude away from the settle window
                // (PrefabRegistry.RefreshSettleFrames = 2 frames after the last *decision*), and
                // the two are independent on purpose: the fingerprint is a property of the
                // definition, not of how long its instances take to come back, so a drain still
                // in flight when the settle fires cannot change what the settle records
                // (R28 finding 28.2).
                item = Queue.Dequeue();
                QueuedKeys.Remove(Key(item.Guid.GuidHash, item.Character));
            }

            var world = Worlds.Server;
            if (world == null || !world.IsCreated)
            {
                // R27 finding 13: dropping the item here after the destroy frame is what left
                // the holder without the buff when a world teardown landed between the two
                // frames. Keep it queued until a world is back (bounded by the retry budget).
                RequeueOwedNow(ref item, "server world is gone");
                return;
            }
            // `item` is a struct: RefreshOne must take it by ref or the destroy frame's
            // state is lost and the destroyed buff is never re-applied (FT / evidence 131).
            if (RefreshOne(world, ref item))
                Requeue(item);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: live-refresh frame threw: {e}");
        }
    }

    /// <summary>
    /// One queued instance. Returns true when the item must be re-queued (destroy this
    /// frame, re-apply next frame once the old entity is gone). Never re-applies a buff
    /// that expired or was cleansed between enqueue and dequeue. Stacks and remaining
    /// <c>LifeTime.Duration</c> are read before destroy and written after apply;
    /// <see cref="PrefabLiveRefresh.CannotPreserve"/> lists what this cannot restore.
    ///
    /// <paramref name="item"/> is a struct and MUST stay <c>ref</c>: the destroy frame writes
    /// <c>DestroyedByUs</c> / stacks / duration onto it and the caller re-queues that same
    /// value. FT: a by-value parameter dropped those writes, so the next frame saw a
    /// never-destroyed item whose entity was gone, skipped it as lost, and the live buff was
    /// destroyed without ever being re-applied (evidence 131).
    /// </summary>
    private static bool RefreshOne(World world, ref Pending item)
    {
        try
        {
            var em = world.EntityManager;

            // Phase 3 (R27 finding 12): ApplyBuff only queued an ApplyBuffDebugEvent, so the
            // new instance appears on a later frame. Keep looking for it, then write the
            // duration onto it. The old code re-queried on the apply frame itself, never found
            // anything, and dropped the remaining duration with no log at all.
            if (item.Applied)
                return AdvanceRestore(em, ref item);

            if (item.Character == Entity.Null || !em.Exists(item.Character))
            {
                if (PrefabLiveRefresh.OwesApply(item.DestroyedByUs, item.Applied))
                {
                    Plugin.Logger?.LogError(PrefabLiveRefresh.FormatOwedApplyLost(
                        item.Id, "the holder is gone; nothing to re-apply to"));
                }
                return false;
            }

            bool exists = item.Instance != Entity.Null && em.Exists(item.Instance);
            // Read stacks / duration off the old entity before Advance flips DestroyedByUs.
            if (PrefabLiveRefresh.ShouldDestroyNow(exists, item.DestroyedByUs))
                ReadPreserved(em, item.Instance, out item.Stacks, out item.HasLifeTime, out item.LifeTimeDuration);

            var state = new PrefabLiveRefresh.RefreshState
            {
                DestroyedByUs = item.DestroyedByUs,
                WaitFrames = item.WaitFrames,
            };
            bool requeue = PrefabLiveRefresh.Advance(ref state, exists, out var step);
            item.DestroyedByUs = state.DestroyedByUs;
            item.WaitFrames = state.WaitFrames;

            if (step == PrefabLiveRefresh.RefreshStep.Skip)
                return false;

            if (step == PrefabLiveRefresh.RefreshStep.Destroy)
            {
                DestroyUtility.Destroy(em, item.Instance, DestroyDebugReason.TryRemoveBuff);
                return requeue;
            }

            if (step == PrefabLiveRefresh.RefreshStep.Wait)
                return requeue;

            var des = world.GetExistingSystemManaged<DebugEventsSystem>();
            if (des == null)
            {
                // Owed apply: never drop it here either. DebugEventsSystem is missing during a
                // world teardown / rebuild, which is exactly when the item must survive.
                return RequeueOwed(ref item, "DebugEventsSystem missing");
            }

            var from = new FromCharacter
            {
                User = item.User != Entity.Null && em.Exists(item.User) ? item.User : item.Character,
                Character = item.Character,
            };
            var evt = new ApplyBuffDebugEvent
            {
                BuffPrefabGUID = item.Guid,
                Stacks = PrefabLiveRefresh.PreserveStacks(item.Stacks),
            };
            des.ApplyBuff(from, evt);
            item.Applied = true;
            item.RestoreFrames = 0;
            // The apply is no longer owed. Stay queued only when there is a duration to
            // restore onto the instance the game has not built yet.
            return PrefabLiveRefresh.ShouldRestoreDuration(item.HasLifeTime, item.LifeTimeDuration);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"PrefabRegistry: live refresh apply of '{item.Id}' threw: {e}");
            // R27 finding 13: a throw between the destroy and the re-apply used to drop the
            // item silently, leaving the holder without the buff for good.
            return RequeueOwed(ref item, "apply threw: " + e.Message);
        }
    }

    /// <summary>
    /// Keep an item that already destroyed its instance and has not re-applied it, and say so
    /// loudly. Returns true when the caller must re-queue. An item that owes nothing (the
    /// destroy never happened, or the apply already ran) is dropped as before.
    /// </summary>
    private static bool RequeueOwed(ref Pending item, string why)
    {
        if (!PrefabLiveRefresh.OwesApply(item.DestroyedByUs, item.Applied))
            return false;
        item.OwedRetries++;
        if (!PrefabLiveRefresh.ShouldRetryOwedApply(item.OwedRetries))
        {
            Plugin.Logger?.LogError(PrefabLiveRefresh.FormatOwedApplyLost(item.Id, why));
            return false;
        }
        Plugin.Logger?.LogWarning(PrefabLiveRefresh.FormatOwedApplyRetry(item.Id, why, item.OwedRetries));
        return true;
    }

    /// <summary>
    /// For <see cref="OnFrame"/>, which owns the dequeued item and must re-queue it itself
    /// (the world is gone, so <see cref="RefreshOne"/> was never entered).
    /// </summary>
    private static void RequeueOwedNow(ref Pending item, string why)
    {
        if (RequeueOwed(ref item, why))
            Requeue(item);
    }

    /// <summary>
    /// Post-apply frame: find the instance the game built for our <c>ApplyBuff</c> and write
    /// the remaining duration onto it. Returns true while the item must stay queued.
    /// </summary>
    private static bool AdvanceRestore(EntityManager em, ref Pending item)
    {
        bool found = TryRestoreDuration(em, item);
        var state = new PrefabLiveRefresh.RestoreState { Frames = item.RestoreFrames };
        var step = PrefabLiveRefresh.AdvanceRestore(ref state, found);
        item.RestoreFrames = state.Frames;
        if (step == PrefabLiveRefresh.RestoreStep.Wait) return true;
        if (step == PrefabLiveRefresh.RestoreStep.GiveUp)
        {
            Plugin.Logger?.LogWarning(
                PrefabLiveRefresh.FormatDurationNotRestored(item.Id, item.LifeTimeDuration));
        }
        return false;
    }

    /// <summary>
    /// Put a half-done item back on the queue. Unlike <see cref="Enqueue"/> this never
    /// de-duplicates it away: once we destroyed the instance we owe it an ApplyBuff, and
    /// dropping the item here is what leaves the character without the buff (FT).
    /// </summary>
    private static void Requeue(Pending item)
    {
        try
        {
            lock (Gate)
            {
                long key = Key(item.Guid.GuidHash, item.Character);
                QueuedKeys.Add(key);
                Queue.Enqueue(item);
                EnsureHookNoLock();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: live-refresh requeue threw: " + e.Message);
        }
    }

    private static void ReadPreserved(
        EntityManager em,
        Entity instance,
        out byte stacks,
        out bool hasLifeTime,
        out float duration)
    {
        stacks = 1;
        hasLifeTime = false;
        duration = 0f;
        try
        {
            var buffType = ComponentIndex.Of<Buff>(ComponentType.AccessMode.ReadOnly);
            if (em.HasComponent(instance, buffType))
            {
                var buff = em.GetComponentData<Buff>(instance);
                stacks = PrefabLiveRefresh.PreserveStacks(buff.Stacks);
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: Buff.Stacks read on live refresh threw: " + e.Message);
        }

        try
        {
            var lifeType = ComponentIndex.Of<LifeTime>(ComponentType.AccessMode.ReadOnly);
            if (em.HasComponent(instance, lifeType))
            {
                var life = em.GetComponentData<LifeTime>(instance);
                hasLifeTime = true;
                duration = life.Duration;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: LifeTime read on live refresh threw: " + e.Message);
        }
    }

    /// <summary>
    /// Look for the instance the game built for our <c>ApplyBuff</c> on this character and
    /// write the remaining duration onto it. True when it was found and written; false means
    /// the instance does not exist yet and the caller should come back next frame.
    /// A candidate without <c>LifeTime</c> is skipped, not treated as the answer: the old
    /// <c>return</c> there aborted the whole restore on the first such instance.
    /// </summary>
    private static bool TryRestoreDuration(EntityManager em, Pending item)
    {
        try
        {
            Entity clone = Entity.Null;
            try
            {
                var server = Worlds.Server;
                if (server != null)
                    PrefabLookup.TryGetEntity(server, item.Guid, out clone);
            }
            catch { /* collect still runs */ }
            var instances = CollectLiveBuffInstances(em, item.Guid, clone);
            for (int i = 0; i < instances.Count; i++)
            {
                var e = instances[i];
                if (e == item.Instance) continue;
                if (!TryResolveCharacter(em, e, out var character, out _) || character != item.Character)
                    continue;
                var lifeType = ComponentIndex.Of<LifeTime>(ComponentType.AccessMode.ReadWrite);
                if (!em.HasComponent(e, lifeType)) continue;
                var life = em.GetComponentData<LifeTime>(e);
                life.Duration = item.LifeTimeDuration;
                em.SetComponentData(e, life);
                return true;
            }
            return false;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: LifeTime restore on live refresh threw: " + e.Message);
            return false;
        }
    }

    private static void NudgeClientHud(
        EntityManager em,
        Entity clone,
        PrefabDefinition def,
        List<Entity> instances,
        string reason)
    {
        try
        {
            int nudged = 0;
            for (int i = 0; i < instances.Count; i++)
            {
                var instance = instances[i];
                if (instance == Entity.Null || !em.Exists(instance)) continue;
                // Same helpers that just wrote the clone: copy HUD fields onto the instance's
                // own ShowOnBuffBarData. Never Remove a buffer here.
                BuffBarTextApply.TryApply(em, instance, def, logNamedOnly: false);
                BuffIconApply.TryNudge(em, instance, def);
                if (clone != Entity.Null && em.Exists(clone))
                    BuffBarDataAccess.TryCopyHud(em, clone, instance);
                nudged++;
            }

            if (!PrefabLiveRefresh.ShouldLogNudge(nudged)) return;
            int hash = def.Guid.GuidHash;
            if (_lastNudgedHash == hash && _lastNudgedCount == nudged) return;
            _lastNudgedHash = hash;
            _lastNudgedCount = nudged;
            Plugin.Logger?.LogInfo(PrefabLiveRefresh.FormatNudged(def.Id, nudged, reason));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"PrefabRegistry: client HUD nudge of '{def.Id}' threw: {e.Message}");
        }
    }

    private static bool TryResolveCharacter(EntityManager em, Entity instance, out Entity character, out Entity user)
    {
        character = Entity.Null;
        user = Entity.Null;
        try
        {
            if (instance.TryRead<EntityOwner>(em, out var owner) && owner.Owner != Entity.Null && em.Exists(owner.Owner))
                character = owner.Owner;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: EntityOwner read on live buff threw: " + e.Message);
        }

        if (character == Entity.Null)
        {
            try
            {
                var buffType = ComponentIndex.Of<Buff>(ComponentType.AccessMode.ReadOnly);
                if (em.HasComponent(instance, buffType))
                {
                    var typeIndex = ComponentIndex.IndexOf<Buff>();
                    byte* ptr = (byte*)em.GetComponentDataRawRO(instance, typeIndex);
                    if (ptr != null)
                    {
                        var target = *(Entity*)(ptr + BuffTargetOffset);
                        if (target != Entity.Null && em.Exists(target))
                            character = target;
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("PrefabRegistry: Buff.Target read on live buff threw: " + e.Message);
            }
        }

        if (character == Entity.Null) return false;

        try
        {
            var pcType = ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly);
            if (em.HasComponent(character, pcType))
            {
                var pc = em.GetComponentData<PlayerCharacter>(character);
                user = pc.UserEntity;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("PrefabRegistry: PlayerCharacter read on live-buff target threw: " + e.Message);
        }

        if (user == Entity.Null) user = character;
        return true;
    }

    private static long Key(int guidHash, Entity character) =>
        ((long)guidHash << 32) ^ (uint)character.Index ^ ((long)character.Version << 16);
}
