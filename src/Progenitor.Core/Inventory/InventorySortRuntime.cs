using System;
using System.Runtime.InteropServices;
using ProjectM.Network;
using Progenitor.Ai;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Hooks;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Inventory;

/// <summary>
/// Client publisher of <see cref="InventorySortUsed"/>.
///
/// <para><b>Seam.</b> The game's sort (the key or a sort button) makes the client create one
/// client-to-server event entity, <c>SortSingleInventoryEvent</c> or <c>SortAllInventoriesEvent</c>
/// (each carries the sorted inventory's <c>NetworkId</c>), which the client's
/// <c>SerializeAndSendClientEventsSystem</c> sends. A <c>SystemHooks.Before</c> on that system reads
/// those entities just before they go out. Nothing reads input and nothing is patched.</para>
///
/// <para><b>Cost.</b> The hook runs only on frames where the client has an event to send (the
/// system's own query gate), and returns at once while nothing subscribes to the event. With a
/// subscriber: two cached queries' entity arrays per such frame, and on a sort one
/// <c>NetworkId</c> read plus a lookup of the local character.</para>
/// </summary>
internal static unsafe class InventorySortRuntime
{
    private const int NetworkIdBytes = 12;

    private static readonly InventorySortFilter Filter = new();
    private static IDisposable? _hook;
    private static EntityQuery _single, _all;
    private static World? _queryWorld;
    private static ulong _queryWorldSeq;
    private static bool _hasQueries;
    private static int _failures;

    internal static void Install()
    {
        if (Plugin.IsServerProcess || _hook != null) return;
        try
        {
            _hook = SystemHooks.Before<SerializeAndSendClientEventsSystem>(BeforeSend);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Inventory: sort event hook install threw: " + e.Message);
            _hook = null;
        }
    }

    internal static void Uninstall()
    {
        try { _hook?.Dispose(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Inventory: sort event hook dispose threw: " + e.Message); }
        _hook = null;
        ResetQueries();
        Filter.Clear();
    }

    private static void BeforeSend(SystemHookContext hook)
    {
        if (EventBus.SubscriberCount<InventorySortUsed>() == 0) return;
        try
        {
            var world = hook.World;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;
            EnsureQueries(world, em);
            Drain(em, _single, InventorySortScope.Single);
            Drain(em, _all, InventorySortScope.All);
        }
        catch (Exception e)
        {
            if (_failures++ < 4) Plugin.Logger?.LogWarning("Inventory: sort event read threw: " + e.Message);
        }
    }

    private static void Drain(EntityManager em, EntityQuery query, InventorySortScope scope)
    {
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (!ReadEventId(em, e, scope, out int n0, out int n1, out int n2)) continue;
                var target = Classify(em, n0, n1, n2);
                long key = ((long)e.Index << 32) | (uint)e.Version;
                var ev = Filter.Admit(key, scope, target, n0, n1, n2, Environment.TickCount64, DateTime.UtcNow);
                if (ev == null) continue;
                Plugin.Logger?.LogDebug("Inventory: " + ev);
                EventBus.Publish(ev);
            }
        }
        finally
        {
            entities.Dispose();
        }
    }

    /// <summary>The event's <c>NetworkId</c> as three normalized words; false when unreadable or the layout disagrees.</summary>
    private static bool ReadEventId(EntityManager em, Entity e, InventorySortScope scope, out int n0, out int n1, out int n2)
    {
        n0 = n1 = n2 = 0;
        if (Marshal.SizeOf<NetworkId>() != NetworkIdBytes) return false;
        byte* p = scope == InventorySortScope.Single
            ? AiRuntime.RO<SortSingleInventoryEvent>(em, e, Layout.SortSingleInventoryEventInventoryOffset + NetworkIdBytes)
            : AiRuntime.RO<SortAllInventoriesEvent>(em, e, Layout.SortAllInventoriesEventInventoryOffset + NetworkIdBytes);
        if (p == null) return false;
        int* net = (int*)(p + (scope == InventorySortScope.Single
            ? Layout.SortSingleInventoryEventInventoryOffset
            : Layout.SortAllInventoriesEventInventoryOffset));
        (n0, n1, n2) = InventorySortFilter.Normalize(net[0], net[1], net[2]);
        return true;
    }

    /// <summary>Own when the id is the local character's or its bag's; Other otherwise; Unknown without a character.</summary>
    private static InventorySortTarget Classify(EntityManager em, int n0, int n1, int n2)
    {
        if (n0 == 0 && n1 == 0 && n2 == 0) return InventorySortTarget.Unknown;
        if (!Progenitor.Dye.DyeSender.TryLocalCharacter(em, out var character)) return InventorySortTarget.Unknown;
        if (SameId(em, character, n0, n1, n2)) return InventorySortTarget.Own;
        for (int instance = 0; instance < Progenitor.Items.Items.MaxInventoryInstances; instance++)
        {
            if (!UnityInventoryBackend.TryInventoryEntity(em, character, instance, out var bag, out _)) break;
            if (bag != character && SameId(em, bag, n0, n1, n2)) return InventorySortTarget.Own;
        }
        return InventorySortTarget.Other;
    }

    private static bool SameId(EntityManager em, Entity entity, int n0, int n1, int n2)
    {
        var type = ComponentIndex.Of<NetworkId>(ComponentType.AccessMode.ReadOnly);
        if (entity == Entity.Null || !em.Exists(entity) || !em.HasComponent(entity, type)) return false;
        int* net = (int*)em.GetComponentDataRawRO(entity, type.TypeIndex);
        if (net == null) return false;
        var (a, b, c) = InventorySortFilter.Normalize(net[0], net[1], net[2]);
        return a == n0 && b == n1 && c == n2;
    }

    private static void EnsureQueries(World world, EntityManager em)
    {
        ulong seq = world.SequenceNumber;
        if (_hasQueries && _queryWorldSeq == seq) return;
        ResetQueries();
        Filter.Clear();
        _single = Query.Build(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<SortSingleInventoryEvent>(ComponentType.AccessMode.ReadOnly));
        _all = Query.Build(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<SortAllInventoriesEvent>(ComponentType.AccessMode.ReadOnly));
        _queryWorld = world;
        _queryWorldSeq = seq;
        _hasQueries = true;
    }

    /// <summary>Dispose the cached queries while their world is alive; a destroyed world already freed them.</summary>
    private static void ResetQueries()
    {
        if (!_hasQueries) return;
        _hasQueries = false;
        bool alive = false;
        try { alive = _queryWorld != null && _queryWorld.IsCreated; }
        catch { alive = false; }
        if (alive)
        {
            try { _single.Dispose(); } catch { /* */ }
            try { _all.Dispose(); } catch { /* */ }
        }
        _single = default;
        _all = default;
        _queryWorld = null;
        _queryWorldSeq = 0;
    }
}
