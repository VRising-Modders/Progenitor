using System;
using System.Collections.Generic;
using System.Reflection;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Hooks;
using Progenitor.Patches;
using Progenitor.Units;
using Progenitor.WorldState;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Combat;

/// <summary>
/// Core-owned publishers for <see cref="UnitDied"/>, <see cref="PlayerDied"/>, and
/// <see cref="DamageDealt"/>. Death is a slot-routed postfix on <c>DeathUtilities.Kill</c>
/// through <see cref="PatchRegistry"/> (the same unload-safe path as <c>ctx.Patches</c>, never
/// <c>UnsafeHarmony</c>). Death also observes <c>DeathEvent</c> entities via <see cref="SystemHooks.Before"/> on
/// managed <c>DeathEventListenerSystem</c> (the ReactToDeadGroup signal Kill creates), so a
/// missed Kill-postfix <c>__args</c> unbox still publishes. Damage is a <see cref="SystemHooks.Before"/> on managed
/// <c>StatChangeSystem</c> that reads <c>StatChangeEvent</c> — not a Burst <c>DealDamageSystem</c>
/// patch. Both publish on the main thread via <see cref="EventBus"/>.
/// </summary>
public static class CombatHooks
{
    /// <summary>SkipCoreModules token.</summary>
    public const string ModuleName = "Combat";

    /// <summary>Core owner id shared with EventHooks / GodMode for the slot registry.</summary>
    internal const string OwnerId = MyPluginInfo.PLUGIN_GUID + PatchSitePolicy.CoreOwnerSuffix;

    /// <summary>Detour-site log key for the death postfix.</summary>
    public const string SiteKill = "combat.kill";

    /// <summary>Address-library id, same string GodMode uses for <c>DeathUtilities.Kill</c>.</summary>
    public const string KillAddressId = "ProjectM.DeathUtilities$$Kill";

    private static readonly object Gate = new();
    private static PatchHandle? _killHandle;
    private static IDisposable? _damageHook;
    private static IDisposable? _deathEventHook;
    // World-bound: a client-hosted server world is destroyed when the host leaves, and disposing a
    // query after that is a native crash (core-swap-crash-2026-10-02.md).
    private static readonly WorldBoundQuery DamageQuery = new("combat damage");
    private static readonly WorldBoundQuery DeathEventQuery = new("combat death-event");
    // Same-frame dedup between Kill postfix and DeathEvent observer (index:version key).
    private static readonly HashSet<ulong> PublishedDeathKeys = new();
    private static bool _installed;
    private static bool _skipped;
    private static int _deathsPublished;
    private static int _damagesPublished;
    private static int _killedArg = -1;
    private static int _deathEventArg = -1;
    private static int _deathReasonArg = -1;

    /// <summary>True once <see cref="Install"/> ran and was not skipped.</summary>
    public static bool Installed
    {
        get { lock (Gate) return _installed && !_skipped; }
    }

    /// <summary>Install death postfix and damage system hook. Idempotent. Server-meaningful; client is a no-op install.</summary>
    internal static void Install()
    {
        lock (Gate)
        {
            if (_installed) return;
            if (HarmonySlotHandoff.ShouldSkipModule(ModuleName))
            {
                _skipped = true;
                _installed = true;
                Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(ModuleName));
                return;
            }

            _skipped = false;
            _installed = true;
        }

        if (!Plugin.IsServerProcess)
        {
            Plugin.Logger?.LogInfo("combat: installed (client no-op; death/damage bus is server-authoritative)");
            return;
        }

        EnsureDeathPatched();
        EnsureDamageHooked();
        EnsureDeathEventHooked();
        Plugin.Logger?.LogInfo(DescribePins());
    }

    /// <summary>Drop the kill postfix, the StatChangeSystem hook, and the cached query.</summary>
    internal static void Uninstall()
    {
        PatchHandle? kill;
        IDisposable? damage;
        IDisposable? deathEvent;
        lock (Gate)
        {
            kill = _killHandle;
            damage = _damageHook;
            deathEvent = _deathEventHook;
            _killHandle = null;
            _damageHook = null;
            _deathEventHook = null;
            _installed = false;
            _skipped = false;
            _killedArg = _deathEventArg = _deathReasonArg = -1;
            PublishedDeathKeys.Clear();
        }

        try { kill?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("combat: kill unpatch: " + e.Message); }
        try { damage?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("combat: damage unhook: " + e.Message); }
        try { deathEvent?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("combat: death-event unhook: " + e.Message); }
        ResetDamageQuery();
        ResetDeathEventQuery();
        Plugin.Logger?.LogInfo("combat: uninstalled");
    }

    /// <summary>Pin line for Core boot.</summary>
    internal static string DescribePins()
    {
        if (_skipped)
            return HarmonySlotHandoff.FormatModulePinsSkipped(ModuleName);

        bool death;
        bool damage;
        bool deathEvent;
        int deaths;
        int damages;
        bool installed;
        lock (Gate)
        {
            installed = _installed;
            death = _killHandle != null;
            damage = _damageHook != null;
            deathEvent = _deathEventHook != null;
            deaths = _deathsPublished;
            damages = _damagesPublished;
        }

        var holds = new List<string>();
        if (installed) holds.Add("installed");
        if (death) holds.Add("death=patched");
        if (deathEvent) holds.Add("deathEvent=hooked");
        if (damage) holds.Add("damage=hooked");
        if (deaths > 0) holds.Add("deaths=" + deaths);
        if (damages > 0) holds.Add("damages=" + damages);
        return HarmonySlotHandoff.FormatModulePins(ModuleName, holds);
    }

    private static void EnsureDeathPatched()
    {
        lock (Gate)
        {
            if (_killHandle != null) return;
        }

        MethodBase? kill;
        try { kill = Access.Access.DeathUtilities.Kill(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("combat: resolve DeathUtilities.Kill: " + e.Message);
            return;
        }

        if (kill == null)
        {
            Plugin.Logger?.LogWarning("combat: DeathUtilities.Kill unresolved; UnitDied/PlayerDied stay silent");
            return;
        }

        Patches.Patches.Install();
        PatchSites.Register(kill, SiteKill, KillAddressId,
            () => Access.Access.DeathUtilities.KillPointer());

        int killed = PatchRegistry.ArgIndex(kill, "killed");
        int deathEvent = PatchRegistry.ArgIndex(kill, "deathEvent");
        int deathReason = PatchRegistry.ArgIndex(kill, "deathReason");
        if (killed < 0)
        {
            Plugin.Logger?.LogWarning(SlotArgs.FormatMissingParameter(
                kill.Name, "killed", "the combat death postfix is refused"));
            return;
        }

        var handle = PatchRegistry.PostfixSlot(
            owner: null,
            ownerId: OwnerId,
            target: kill,
            postfix: OnKillPostfix,
            priority: 400);

        lock (Gate)
        {
            if (handle == null)
            {
                Plugin.Logger?.LogWarning("combat: Kill postfix refused; UnitDied/PlayerDied stay silent");
                return;
            }

            _killHandle = handle;
            _killedArg = killed;
            _deathEventArg = deathEvent;
            _deathReasonArg = deathReason;
        }

        Plugin.Logger?.LogInfo("combat: death postfix live on DeathUtilities.Kill");
    }

    private static void EnsureDamageHooked()
    {
        lock (Gate)
        {
            if (_damageHook != null) return;
        }

        IDisposable? hook;
        try
        {
            hook = SystemHooks.Before(
                typeof(ProjectM.Gameplay.Systems.StatChangeSystem),
                OnStatChangeBefore,
                new HookOptions
                {
                    Priority = 0,
                    PluginId = OwnerId,
                    OnlyWhenSystemRuns = true,
                });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("combat: StatChangeSystem Before hook failed: " + e.Message);
            return;
        }

        lock (Gate) { _damageHook = hook; }
        Plugin.Logger?.LogInfo("combat: damage Before hook on StatChangeSystem");
    }

    private static void OnKillPostfix(object? instance, object?[] args)
    {
        _ = instance;
        if (EventBus.SubscriberCount<UnitDied>() == 0
            && EventBus.SubscriberCount<PlayerDied>() == 0)
            return;

        int killedIdx;
        int deathEventIdx;
        int deathReasonIdx;
        lock (Gate)
        {
            killedIdx = _killedArg;
            deathEventIdx = _deathEventArg;
            deathReasonIdx = _deathReasonArg;
        }

        if (!SlotArgs.TryArg(args, killedIdx, out Entity killed))
        {
            if (args != null && killedIdx >= 0 && killedIdx < args.Length)
            {
                Plugin.Logger?.LogWarning(
                    SlotArgs.FormatWrongArgumentType(
                        SiteKill, "killed", killedIdx, args[killedIdx], typeof(Entity)));
            }
            return;
        }

        var diedId = UnitId.From(killed);
        var killerId = UnitId.None;
        var sourceId = UnitId.None;
        string statReason = "";
        string deathReason = "";

        if (deathEventIdx >= 0 && SlotArgs.TryArg(args, deathEventIdx, out DeathEvent deathEvent))
        {
            if (deathEvent.Died != Entity.Null)
            {
                killed = deathEvent.Died;
                diedId = UnitId.From(killed);
            }
            if (deathEvent.Killer != Entity.Null)
                killerId = UnitId.From(deathEvent.Killer);
            if (deathEvent.Source != Entity.Null)
                sourceId = UnitId.From(deathEvent.Source);
            try { statReason = deathEvent.StatChangeReason.ToString() ?? ""; }
            catch { }
        }
        else if (deathEventIdx >= 0 && args != null && deathEventIdx < args.Length
                 && args[deathEventIdx] != null
                 && args[deathEventIdx] is not DeathEvent)
        {
            Plugin.Logger?.LogWarning(
                SlotArgs.FormatWrongArgumentType(
                    SiteKill, "deathEvent", deathEventIdx, args[deathEventIdx], typeof(DeathEvent)));
        }

        if (deathReasonIdx >= 0 && SlotArgs.TryArg(args, deathReasonIdx, out DeathReason dr))
        {
            try { deathReason = dr.ToString() ?? ""; }
            catch { }
        }

        string reason = CombatEventPolicy.CombineReasons(statReason, deathReason);
        PublishDeath(killed, diedId, killerId, sourceId, reason, "kill-postfix");
    }

    private static void EnsureDeathEventHooked()
    {
        lock (Gate)
        {
            if (_deathEventHook != null) return;
        }

        IDisposable? hook;
        try
        {
            // Before DeathEventListenerSystem so DeathEvent rows still exist (ReactToDeadGroup
            // consumes them). DeathEvent is created by DeathUtilities.Kill — this is the game's
            // authentic "a unit finished the kill path" signal, independent of __args unboxing.
            hook = SystemHooks.Before(
                typeof(ProjectM.DeathEventListenerSystem),
                OnDeathEventBefore,
                new HookOptions
                {
                    Priority = 0,
                    PluginId = OwnerId,
                    OnlyWhenSystemRuns = true,
                });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("combat: DeathEventListenerSystem Before hook failed: " + e.Message);
            return;
        }

        lock (Gate) { _deathEventHook = hook; }
        Plugin.Logger?.LogInfo("combat: death-event Before hook on DeathEventListenerSystem");
    }

    private static void OnDeathEventBefore(SystemHookContext ctx)
    {
        if (EventBus.SubscriberCount<UnitDied>() == 0
            && EventBus.SubscriberCount<PlayerDied>() == 0)
            return;
        try
        {
            var world = ctx.World;
            if (world == null || !world.IsCreated) { ResetDeathEventQuery(); return; }
            var em = world.EntityManager;
            var query = GetDeathEventQuery(world, em);
            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    if (!em.Exists(entity)) continue;
                    DeathEvent row;
                    try { row = em.GetComponentData<DeathEvent>(entity); }
                    catch { continue; }

                    var died = row.Died;
                    if (died == Entity.Null) continue;
                    var diedId = UnitId.From(died);
                    var killerId = row.Killer == Entity.Null ? UnitId.None : UnitId.From(row.Killer);
                    var sourceId = row.Source == Entity.Null ? UnitId.None : UnitId.From(row.Source);
                    string reason;
                    try { reason = row.StatChangeReason.ToString() ?? ""; }
                    catch { reason = ""; }

                    PublishDeath(died, diedId, killerId, sourceId, reason, "death-event");
                }
            }
            finally
            {
                entities.Dispose();
                // Drop same-frame Kill-postfix keys after ReactToDead has observed them, so the
                // next frame's kills are not falsely deduped. Entity version bumps on destroy, but
                // clearing keeps the set bounded.
                lock (Gate) { PublishedDeathKeys.Clear(); }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("combat: death-event observe: " + e);
        }
    }

    /// <summary>
    /// Shared publish for Kill postfix and DeathEvent observer. Dedups by died index:version so
    /// both paths in one frame only raise once.
    /// </summary>
    private static void PublishDeath(
        Entity killed,
        UnitId diedId,
        UnitId killerId,
        UnitId sourceId,
        string reason,
        string via)
    {
        ulong key = ((ulong)(uint)killed.Index << 32) | (uint)killed.Version;
        lock (Gate)
        {
            if (!PublishedDeathKeys.Add(key))
                return;
        }

        int epoch = WorldEpoch.Current;
        bool isPlayer = false;
        var userId = UnitId.None;

        try
        {
            var world = Worlds.Server;
            if (world != null && world.IsCreated)
            {
                var em = world.EntityManager;
                if (em.Exists(killed))
                {
                    var playerType = ComponentIndex.Of<PlayerCharacter>(ComponentType.AccessMode.ReadOnly);
                    isPlayer = em.HasComponent(killed, playerType);
                    if (isPlayer)
                    {
                        var pc = em.GetComponentData<PlayerCharacter>(killed);
                        if (pc.UserEntity != Entity.Null)
                            userId = UnitId.From(pc.UserEntity);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("combat: classify death: " + e.Message);
        }

        try
        {
            if (CombatEventPolicy.PublishAsPlayerDied(isPlayer))
                EventBus.Publish(new PlayerDied(diedId, userId, killerId, sourceId, reason, epoch));
            else
                EventBus.Publish(new UnitDied(diedId, killerId, sourceId, reason, epoch));
            lock (Gate) { _deathsPublished++; }
            Plugin.Logger?.LogInfo(
                "combat: published " + (isPlayer ? "PlayerDied" : "UnitDied")
                + " via=" + via + " died=" + diedId + " reason=" + reason);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("combat: publish death: " + e);
        }
    }

    private static void OnStatChangeBefore(SystemHookContext ctx)
    {
        if (EventBus.SubscriberCount<DamageDealt>() == 0) return;
        try
        {
            var world = ctx.World;
            if (world == null || !world.IsCreated) { ResetDamageQuery(); return; }
            var em = world.EntityManager;
            var query = GetDamageQuery(world, em);
            var entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                int epoch = WorldEpoch.Current;
                for (int i = 0; i < entities.Length; i++)
                {
                    var entity = entities[i];
                    if (!em.Exists(entity)) continue;
                    StatChangeEvent row;
                    try { row = em.GetComponentData<StatChangeEvent>(entity); }
                    catch { continue; }

                    int statType = (int)row.StatType;
                    if (!CombatEventPolicy.ShouldPublishDamage(statType, row.Change, row.OriginalChange))
                        continue;

                    string reason;
                    try { reason = row.Reason.ToString() ?? ""; }
                    catch { reason = ""; }

                    EventBus.Publish(new DamageDealt(
                        UnitId.From(row.Entity),
                        row.Source == Entity.Null ? UnitId.None : UnitId.From(row.Source),
                        row.Change,
                        row.OriginalChange,
                        row.StatChangeFlags,
                        reason,
                        epoch));
                    lock (Gate) { _damagesPublished++; }
                }
            }
            finally
            {
                entities.Dispose();
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("combat: damage observe: " + e);
        }
    }

    private static EntityQuery GetDamageQuery(World world, EntityManager em)
    {
        lock (Gate)
        {
            if (DamageQuery.TryGet(world, out var query)) return query;
            query = em.CreateEntityQuery(ComponentType.ReadOnly<StatChangeEvent>());
            DamageQuery.Store(world, query);
            return query;
        }
    }

    private static void ResetDamageQuery()
    {
        lock (Gate) { DamageQuery.Release(); }
    }

    private static EntityQuery GetDeathEventQuery(World world, EntityManager em)
    {
        lock (Gate)
        {
            if (DeathEventQuery.TryGet(world, out var query)) return query;
            query = em.CreateEntityQuery(ComponentType.ReadOnly<DeathEvent>());
            DeathEventQuery.Store(world, query);
            return query;
        }
    }

    private static void ResetDeathEventQuery()
    {
        lock (Gate) { DeathEventQuery.Release(); }
    }
}
