using System;
using System.Collections.Generic;
using ProjectM;
using Progenitor.Ai;
using Progenitor.Content;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace Progenitor.Stations;

/// <summary>A burning structure found by <see cref="Braziers.Near"/>.</summary>
public sealed class BrazierInfo
{
    /// <summary>The structure entity. Valid for this world load only; do not persist it.</summary>
    public Entity Brazier { get; init; }

    /// <summary>The structure's prefab GUID hash.</summary>
    public int PrefabGuid { get; init; }

    /// <summary>The structure's prefab name; empty when unknown.</summary>
    public string Name { get; init; } = "";

    /// <summary>World position.</summary>
    public float3 Position { get; init; }

    /// <summary>Horizontal distance from the point the search was made at.</summary>
    public float Distance { get; init; }

    /// <summary>Its switch, burn state and fuel at the time of the search.</summary>
    public BrazierState State { get; init; } = new();
}

/// <summary>
/// Server reads and the on/off switch of burning structures: every entity with the game's
/// <c>BurnContainer</c> (the mist brazier, which also carries <c>Bonfire</c>). <c>ctx.Stations</c> is the
/// plugin-bound surface; every member here throws on the client.
///
/// <para><b>The switch.</b> The vanilla window's toggle sends <c>ToggleRefiningEvent</c>; the server's
/// <c>ToggleRefiningSystem</c> checks the sender is using the structure and flips
/// <c>BurnContainer.Enabled</c>. <see cref="SetEnabled"/> writes that same byte directly on the server
/// (no player needs the window open) and only when it differs, so it is a set, not a flip.
/// <c>BurnContainer</c> is a networked component: every client's window and effect follow.</para>
///
/// <para><b>Burning and fuel.</b> <c>BonfireSystem_Server</c> burns while the switch is on, it is day
/// and the structure's <c>EditableTileModel.CanRepair</c> flag is clear (inferred to mean "not
/// damaged"); it takes one fuel item (<c>Bonfire.InputItem</c>) from the
/// structure's own inventory per <c>Bonfire.BurnTime</c> seconds and never draws on castle storage.
/// Nothing burns at night, so fuel lasts through it. Turning the switch off stops the burn at the next
/// update; the started item's progress is kept and resumes when it is turned on again.</para>
///
/// <para>Reads and the write are raw at the declared <see cref="Layout"/> offsets through the
/// non-generic component accessors and the overlay write guard.</para>
/// </summary>
public static unsafe class Braziers
{
    private const int BurnExtent = Layout.BurnContainerEnabledOffset + 1;
    private const int BonfireExtent = Layout.BonfireIsActiveOffset + 1;

    /// <summary>True when <paramref name="entity"/> has the game's <c>BurnContainer</c>.</summary>
    public static bool IsBrazier(World world, Entity entity)
    {
        var em = RequireServer(world);
        return entity != Entity.Null && em.Exists(entity)
            && em.HasComponent(entity, ComponentIndex.Of<BurnContainer>(ComponentType.AccessMode.ReadOnly));
    }

    /// <summary>The switch, burn state and fuel of <paramref name="entity"/>; null when it is not a burning structure.</summary>
    public static BrazierState? Read(World world, Entity entity)
    {
        var em = RequireServer(world);
        return ReadState(em, entity);
    }

    /// <summary>
    /// Set the switch of <paramref name="entity"/> to <paramref name="on"/>. <c>Ok</c> false with a
    /// <c>Reason</c> when the entity does not exist or has no <c>BurnContainer</c>; the byte is written
    /// and read back otherwise.
    /// </summary>
    public static BrazierToggleResult SetEnabled(World world, Entity entity, bool on)
    {
        var em = RequireServer(world);
        if (entity == Entity.Null || !em.Exists(entity))
            return new BrazierToggleResult { Ok = false, Enabled = false, Reason = "the entity does not exist" };
        byte* ro = AiRuntime.RO<BurnContainer>(em, entity, BurnExtent);
        if (ro == null)
            return new BrazierToggleResult { Ok = false, Enabled = false, Reason = "the entity has no BurnContainer (not a brazier)" };
        bool current = ro[Layout.BurnContainerEnabledOffset] != 0;
        var plan = BrazierLogic.PlanSet(current, on);
        if (plan == null)
            return new BrazierToggleResult { Ok = true, Changed = false, Enabled = current, Reason = "already " + (on ? "on" : "off") };
        byte* rw = AiRuntime.RW<BurnContainer>(em, entity, BurnExtent);
        rw[Layout.BurnContainerEnabledOffset] = plan.Value;
        byte* back = AiRuntime.RO<BurnContainer>(em, entity, BurnExtent);
        bool now = back != null && back[Layout.BurnContainerEnabledOffset] != 0;
        return new BrazierToggleResult
        {
            Ok = now == on,
            Changed = now != current,
            Enabled = now,
            Reason = now == on ? "switched " + (on ? "on" : "off") : "the switch read back " + (now ? "on" : "off") + " after the write",
        };
    }

    /// <summary>
    /// Burning structures within <paramref name="radius"/> meters (horizontal) of
    /// <paramref name="position"/>, nearest first. Scans every <c>BurnContainer</c> in the world and
    /// counts each one's fuel; call on demand or on a slow tick, not every frame. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for a radius that is not finite and positive.
    /// </summary>
    public static IReadOnlyList<BrazierInfo> Near(World world, float3 position, float radius)
    {
        BrazierLogic.RequireRadius(radius);
        var em = RequireServer(world);
        var found = new List<BrazierInfo>();
        var entities = Query.Entities(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<BurnContainer>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<Translation>(ComponentType.AccessMode.ReadOnly));
        float2 center = position.xz;
        foreach (var e in entities)
        {
            if (!e.TryRead<Translation>(em, out var tr)) continue;
            float d = math.distance(tr.Value.xz, center);
            if (d > radius) continue;
            var state = ReadState(em, e);
            if (state == null) continue;
            int guid = AiRuntime.PrefabGuidOf(em, e);
            PrefabLookup.TryGetName(world, new PrefabGUID(guid), out string name);
            found.Add(new BrazierInfo { Brazier = e, PrefabGuid = guid, Name = name ?? "", Position = tr.Value, Distance = d, State = state });
        }
        found.Sort((a, b) => a.Distance.CompareTo(b.Distance));
        return found;
    }

    private static BrazierState? ReadState(EntityManager em, Entity entity)
    {
        if (entity == Entity.Null || !em.Exists(entity)) return null;
        byte* burn = AiRuntime.RO<BurnContainer>(em, entity, BurnExtent);
        if (burn == null) return null;
        bool enabled = burn[Layout.BurnContainerEnabledOffset] != 0;
        float progress = *(float*)(burn + Layout.BurnContainerProgressOffset);
        byte* fire = AiRuntime.RO<Bonfire>(em, entity, BonfireExtent);
        if (fire == null)
            return new BrazierState { Enabled = enabled, Progress = progress, HasBonfire = false };
        int fuel = *(int*)(fire + Layout.BonfireInputItemOffset);
        int amount = 0;
        if (fuel != 0)
        {
            try { amount = Progenitor.Inventory.InventoryRuntime.Engine.Count(Progenitor.Inventory.InventoryRef.For(entity), fuel); }
            catch { amount = 0; }
        }
        return new BrazierState
        {
            Enabled = enabled,
            Progress = progress,
            HasBonfire = true,
            Burning = fire[Layout.BonfireIsActiveOffset] != 0,
            Strength = *(float*)(fire + Layout.BonfireStrengthOffset),
            FuelItemGuid = fuel,
            FuelAmount = amount,
            BurnTimeSeconds = *(float*)(fire + Layout.BonfireBurnTimeOffset),
        };
    }

    private static EntityManager RequireServer(World world)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("ctx.Stations runs on the server side only");
        if (world == null || !world.IsCreated)
            throw new InvalidOperationException("the game world does not exist yet");
        if (world.Name != Worlds.ServerName)
            throw new InvalidOperationException("ctx.Stations needs the server's default world (\"" + Worlds.ServerName + "\"), not \"" + world.Name + "\"");
        return world.EntityManager;
    }
}
