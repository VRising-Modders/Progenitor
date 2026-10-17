#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using Progenitor.Ecs;
using ProjectM;
using ProjectM.Network;
using Unity.Entities;

namespace Progenitor.Inventory;

/// <summary>
/// Server, main thread: one preview's entries in the game's per-user inventory sync lists (<c>SyncToUserBuffer</c>)
/// for the length of its lease. A chest's inventory entity reaches only the users in that list (the streaming lease
/// keeps the chest itself relevant and enabled, but cannot reach the inventory). The preview adds one copy of the user
/// to each inventory entity of the chest and to each slot item entity that has the list, adds on items that enter,
/// removes its own copy from items that leave, and removes exactly its own copies at the end: never a clear, because
/// the list is a multiset that the game's interactions share. Rules and bookkeeping: <see cref="ContainerPreviewSyncLedger"/>.
///
/// <para>Buffer access is the raw path Core already uses for the respawn list: the length and a read-only pointer, one
/// <c>UnsafeEntityManagerUtility.AddBufferElement</c> to append, <c>SetBufferDataRaw</c> to write the list back with
/// one copy removed. Element size and offset are layout facts (<c>SyncToUserBufferStride</c>,
/// <c>SyncToUserBufferUserEntityOffset</c>). The slot rows are read with the typed <c>GetBuffer&lt;InventoryBuffer&gt;</c>
/// Core already uses.</para>
///
/// <para><b>Saves.</b> Whether the game saves <c>SyncToUserBuffer</c> is unknown. Before a world save
/// (<c>ModSave.BeforeSave</c>) the entries are taken out and put back <see cref="ContainerPreviewSyncSave.ResumeAfterSeconds"/>
/// later.</para>
/// </summary>
internal sealed unsafe class ContainerPreviewSync
{
    private const int MaxListEntries = 512;

    private readonly World _world;
    private readonly Entity _user;
    private readonly Entity _container;
    private readonly long _userKey;
    private readonly ContainerPreviewSyncLedger _ledger = new();
    private double _suspendedAt = double.NaN;
    private bool _ended;

    internal ContainerPreviewSync(World world, Entity user, Entity container)
    {
        _world = world; _user = user; _container = container;
        _userKey = ContainerPreviewSyncLedger.Key(user.Index, user.Version);
    }

    internal bool Suspended => !double.IsNaN(_suspendedAt);

    internal int HeldCount => _ledger.Count;

    /// <summary>Bring the entries in line with the chest's current inventories and items. Throws on a failed write.</summary>
    internal void Refresh()
    {
        if (_ended || Suspended || !_world.IsCreated) return;
        var em = _world.EntityManager;
        var (add, remove) = _ledger.Plan(Wanted(em));
        foreach (long key in remove)
        {
            var e = ToEntity(key);
            if (e.Exists(em) && HasList(em, e)) RemoveOne(em, e);
            _ledger.MarkRemoved(key);
        }
        foreach (long key in add)
        {
            AddOne(em, ToEntity(key));
            _ledger.MarkAdded(key);
        }
    }

    /// <summary>The 1 s server tick: put suspended entries back after a save, else refresh.</summary>
    internal void Tick(double now)
    {
        if (_ended) return;
        if (Suspended)
        {
            if (!ContainerPreviewSyncSave.ShouldResume(_suspendedAt, now)) return;
            _suspendedAt = double.NaN;
            Plugin.Logger?.LogInfo("ContainerPreview: inventory sync entries put back after the save.");
        }
        Refresh();
    }

    /// <summary>A world save is about to be written: take every entry out until <see cref="Tick"/> puts them back.</summary>
    internal void Suspend(double now)
    {
        if (_ended || Suspended) return;
        RemoveAll();
        _suspendedAt = now;
    }

    /// <summary>The lease ended (any reason): remove exactly this preview's copies. Never throws.</summary>
    internal void End()
    {
        if (_ended) return;
        _ended = true;
        RemoveAll();
    }

    private void RemoveAll()
    {
        var held = _ledger.TakeAll();
        if (held.Count == 0) return;
        bool alive;
        try { alive = _world.IsCreated; } catch { alive = false; }
        if (!alive) return;
        var em = _world.EntityManager;
        int removed = 0;
        foreach (long key in held)
        {
            try
            {
                var e = ToEntity(key);
                if (e.Exists(em) && HasList(em, e) && RemoveOne(em, e)) removed++;
            }
            catch (Exception ex) { Plugin.Logger?.LogWarning("ContainerPreview: removing a sync entry threw: " + ex.Message); }
        }
        Plugin.Logger?.LogInfo("ContainerPreview: inventory sync entries removed (" + removed + " of " + held.Count + ").");
    }

    /// <summary>The chest's inventory entities and their slot items that carry the sync list.</summary>
    private List<long> Wanted(EntityManager em)
    {
        var wanted = new List<long>();
        if (!_container.Exists(em)) return wanted;
        for (int instance = 0; instance < Progenitor.Items.Items.MaxInventoryInstances; instance++)
        {
            if (!UnityInventoryBackend.TryInventoryEntity(em, _container, instance, out var inv, out _)) break;
            if (HasList(em, inv)) wanted.Add(ContainerPreviewSyncLedger.Key(inv.Index, inv.Version));
            var rows = em.GetBuffer<InventoryBuffer>(inv, true);
            for (int i = 0; i < rows.Length; i++)
            {
                Entity item;
                try { item = rows[i].ItemEntity.GetEntityOnServer(); } catch { continue; }
                if (item == Entity.Null || !item.Exists(em) || !HasList(em, item)) continue;
                wanted.Add(ContainerPreviewSyncLedger.Key(item.Index, item.Version));
            }
            if (inv == _container) break;   // a single-inventory owner holds its rows itself
        }
        return wanted;
    }

    // ---- the raw list ---------------------------------------------------------------------------

    private static TypeIndex ListType => ComponentIndex.IndexOf<SyncToUserBuffer>();

    private static bool HasList(EntityManager em, Entity e) => e.Has<SyncToUserBuffer>(em);

    private static Entity ToEntity(long key)
    {
        var (index, version) = ContainerPreviewSyncLedger.Unkey(key);
        return new Entity { Index = index, Version = version };
    }

    /// <summary>The list's user entities, as keys.</summary>
    internal static long[] ReadUsers(EntityManager em, Entity e)
    {
        var type = ListType;
        int length = em.GetBufferLength(e, type);
        if (length < 0 || length > MaxListEntries) throw new InvalidOperationException("sync list has " + length + " entries");
        if (length == 0) return Array.Empty<long>();
        byte* raw = (byte*)em.GetBufferRawRO(e, type);
        if (raw == null) throw new InvalidOperationException("sync list raw buffer was null");
        var users = new long[length];
        for (int i = 0; i < length; i++)
        {
            byte* row = raw + i * Layout.SyncToUserBufferStride + Layout.SyncToUserBufferUserEntityOffset;
            users[i] = ContainerPreviewSyncLedger.Key(*(int*)row, *(int*)(row + 4));
        }
        return users;
    }

    private void AddOne(EntityManager em, Entity e)
    {
        byte* element = stackalloc byte[Layout.SyncToUserBufferStride];
        for (int i = 0; i < Layout.SyncToUserBufferStride; i++) element[i] = 0;
        *(int*)(element + Layout.SyncToUserBufferUserEntityOffset) = _user.Index;
        *(int*)(element + Layout.SyncToUserBufferUserEntityOffset + 4) = _user.Version;
        var type = ListType;
        try { UnsafeEntityManagerUtility.AddBufferElement(em, e, type.Value, element); }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("ContainerPreview: sync list append with flagged index failed: " + ex.Message);
            UnsafeEntityManagerUtility.AddBufferElement(em, e, type.Value & SingletonsZeroSize.ClearFlagsMask, element);
        }
    }

    /// <summary>Remove the last copy of this user; false when the list holds none.</summary>
    private bool RemoveOne(EntityManager em, Entity e)
    {
        var next = SyncToUserBufferEdit.RemoveOne(ReadUsers(em, e), _userKey);
        if (next == null) return false;
        int bytes = Math.Max(1, next.Length) * Layout.SyncToUserBufferStride;
        byte* data = stackalloc byte[bytes];
        for (int i = 0; i < bytes; i++) data[i] = 0;
        for (int i = 0; i < next.Length; i++)
        {
            var (index, version) = ContainerPreviewSyncLedger.Unkey(next[i]);
            byte* row = data + i * Layout.SyncToUserBufferStride + Layout.SyncToUserBufferUserEntityOffset;
            *(int*)row = index;
            *(int*)(row + 4) = version;
        }
        UnsafeEntityManagerUtility.SetBufferDataRaw(em, e, ListType.Value, Layout.SyncToUserBufferStride, 4, next.Length, data);
        return true;
    }

    /// <summary>Dev bridge readback: each entity this preview holds a copy on, the list's length and this user's copies.</summary>
    internal List<Dictionary<string, object?>> Describe()
    {
        var rows = new List<Dictionary<string, object?>>();
        if (!_world.IsCreated) return rows;
        var em = _world.EntityManager;
        foreach (long key in _ledger.Added)
        {
            var e = ToEntity(key);
            var row = new Dictionary<string, object?> { ["entity"] = e.Index + ":" + e.Version };
            try
            {
                if (!e.Exists(em) || !HasList(em, e)) row["users"] = null;
                else
                {
                    var users = ReadUsers(em, e);
                    row["users"] = users.Length;
                    row["thisUser"] = SyncToUserBufferEdit.CountOf(users, _userKey);
                    row["inventory"] = e.Has<InventoryBuffer>(em);
                }
            }
            catch (Exception ex) { row["error"] = ex.Message; }
            rows.Add(row);
        }
        return rows;
    }
}
#endif
