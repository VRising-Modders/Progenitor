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
/// keeps the chest itself relevant and enabled, but cannot reach the inventory). The preview takes one copy of the user
/// on each inventory entity of the chest and on each slot item entity that has the list, takes one on items that enter
/// (adopting the copy the game's item sync already put there), releases its own copy from items that leave, and
/// releases exactly its own copies at the end: never a clear, because the list is a multiset that the game's
/// interactions share. Rules and bookkeeping: <see cref="ContainerPreviewSyncLedger"/>.
///
/// <para>Buffer access is the raw path Core already uses for the respawn list and the unlock prune: the length and a
/// read-only pointer, one <c>UnsafeEntityManagerUtility.AddBufferElement</c> to append, <c>SetBufferDataRaw</c> to write the
/// list back with one copy removed. Element size and offset are layout facts. <b>Every write is read back</b> (length
/// and contents) and a mismatch is a failure; an entity is marked released only after its removal read back, and a
/// failed release is retried on later ticks. Removing the only entry writes zero rows: that write is checked by its
/// read-back like any other (the unlock prune avoids zero-row writes because they were unverified; here the read-back
/// is the verification, and a failure leaves the entry for the retry and the startup sweep).</para>
/// </summary>
internal sealed unsafe class ContainerPreviewSync
{
    private const int MaxListEntries = 512;

    /// <summary>A release that keeps failing is given up after this many tries (the persisted record stays for the sweep).</summary>
    internal const int MaxReleaseAttempts = 10;

    private readonly World _world;
    private readonly Entity _user;
    private readonly Entity _container;
    private readonly long _userKey;
    private readonly ContainerPreviewSyncLedger _ledger = new();
    private readonly SyncSaveSuspension _suspension = new();
    private readonly Dictionary<long, int> _releaseFailures = new();
    private bool _firstRefresh = true;
    private bool _ending;

    internal ContainerPreviewSync(World world, Entity user, Entity container)
    {
        _world = world; _user = user; _container = container;
        _userKey = ContainerPreviewSyncLedger.Key(user.Index, user.Version);
    }

    internal bool Suspended => _suspension.Active;

    internal int HeldCount => _ledger.Count;

    /// <summary>True once the lease ended and every copy is released (or given up).</summary>
    internal bool Done => _ending && _ledger.Count == 0;

    /// <summary>Bring the entries in line with the chest's current inventories and items. Throws on a failed take.</summary>
    internal void Refresh()
    {
        if (_ending || Suspended || !_world.IsCreated) return;
        var em = _world.EntityManager;
        var (take, release) = _ledger.Plan(Wanted(em));
        foreach (long key in release) TryRelease(em, key);
        foreach (long key in take)
        {
            var e = ToEntity(key);
            var before = ReadUsers(em, e);
            var action = ContainerPreviewSyncLedger.ActionFor(_firstRefresh, SyncToUserBufferEdit.CountOf(before, _userKey));
            if (action == SyncEntryAction.Append) AppendOne(em, e, before);
            _ledger.MarkHeld(key);
        }
        _firstRefresh = false;
    }

    /// <summary>The 1 s server tick: retry releases after the end, put entries back after a save, else refresh.</summary>
    internal void Tick(double now)
    {
        if (_ending) { ReleaseAll(); return; }
        if (Suspended)
        {
            if (!_suspension.ShouldResume(now, SaveStamp())) return;
            Plugin.Logger?.LogInfo("ContainerPreview: inventory sync entries go back "
                + (_suspension.TimedOut ? "(the save file never changed in " + SyncSaveSuspension.TimeoutSeconds + " s)." : "after the save was written."));
            _firstRefresh = true;
        }
        Refresh();
    }

    /// <summary>A world save was triggered: take every entry out; a second save during the suspension restarts the wait.</summary>
    internal void Suspend(double now)
    {
        if (_ending) return;
        _suspension.Begin(now, SaveStamp());
        ReleaseAll();
    }

    /// <summary>The lease ended (any reason): release exactly this preview's copies; failures are retried by <see cref="Tick"/>.</summary>
    internal void End()
    {
        _ending = true;
        _suspension.End();
        ReleaseAll();
    }

    private static DateTime SaveStamp()
    {
        try { return Persistence.WorldSaveTrigger.NewestSaveWriteUtc(Persistence.WorldSaveTrigger.SaveFolder()); }
        catch { return DateTime.MinValue; }
    }

    private void ReleaseAll()
    {
        if (_ledger.Count == 0) return;
        bool alive;
        try { alive = _world.IsCreated; } catch { alive = false; }
        if (!alive) { foreach (long key in _ledger.Snapshot()) _ledger.MarkReleased(key); return; }
        var em = _world.EntityManager;
        int held = _ledger.Count;
        foreach (long key in _ledger.Snapshot()) TryRelease(em, key);
        Plugin.Logger?.LogInfo("ContainerPreview: inventory sync entries released (" + (held - _ledger.Count) + " of " + held
            + (_ledger.Count > 0 ? "; " + _ledger.Count + " retried later" : "") + ").");
    }

    /// <summary>Release this preview's copy on one entity; marked released only when the removal read back (or nothing is left to remove).</summary>
    private void TryRelease(EntityManager em, long key)
    {
        try
        {
            var e = ToEntity(key);
            if (e.Exists(em) && HasList(em, e)) RemoveOne(em, e);
            _ledger.MarkReleased(key);
            _releaseFailures.Remove(key);
        }
        catch (Exception ex)
        {
            int tries = _releaseFailures.TryGetValue(key, out int n) ? n + 1 : 1;
            _releaseFailures[key] = tries;
            if (tries < MaxReleaseAttempts)
            {
                if (tries == 1) Plugin.Logger?.LogWarning("ContainerPreview: releasing a sync entry failed (retried): " + ex.Message);
                return;
            }
            Plugin.Logger?.LogError("ContainerPreview: releasing a sync entry failed " + tries + " times; given up (the startup sweep may remove it): " + ex.Message);
            _ledger.MarkReleased(key);
            _releaseFailures.Remove(key);
        }
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
            foreach (var item in SlotItems(em, inv)) wanted.Add(ContainerPreviewSyncLedger.Key(item.Index, item.Version));
            if (inv == _container) break;   // a single-inventory owner holds its rows itself
        }
        return wanted;
    }

    /// <summary>The slot item entities of <paramref name="inv"/> that carry the sync list.</summary>
    internal static List<Entity> SlotItems(EntityManager em, Entity inv)
    {
        var items = new List<Entity>();
        var rows = em.GetBuffer<InventoryBuffer>(inv, true);
        for (int i = 0; i < rows.Length; i++)
        {
            Entity item;
            try { item = rows[i].ItemEntity.GetEntityOnServer(); } catch { continue; }
            if (item != Entity.Null && item.Exists(em) && HasList(em, item)) items.Add(item);
        }
        return items;
    }

    // ---- the raw list ---------------------------------------------------------------------------

    private static TypeIndex ListType => ComponentIndex.IndexOf<SyncToUserBuffer>();

    internal static bool HasList(EntityManager em, Entity e) => e.Has<SyncToUserBuffer>(em);

    internal static Entity ToEntity(long key)
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

    /// <summary>Append one copy of the user and read it back; throws when the list is not exactly the old one plus the user.</summary>
    private void AppendOne(EntityManager em, Entity e, long[] before)
    {
        byte* element = stackalloc byte[Layout.SyncToUserBufferStride];
        for (int i = 0; i < Layout.SyncToUserBufferStride; i++) element[i] = 0;
        *(int*)(element + Layout.SyncToUserBufferUserEntityOffset) = _user.Index;
        *(int*)(element + Layout.SyncToUserBufferUserEntityOffset + 4) = _user.Version;
        var type = ListType;
        try { UnsafeEntityManagerUtility.AddBufferElement(em, e, type.Value, element); }
        catch (Exception ex)
        {
            // The flagged index may have thrown after the element landed: retrying then would add a second copy.
            if (SyncToUserBufferEdit.AppendLanded(before, ReadUsers(em, e), _userKey)) return;
            Plugin.Logger?.LogWarning("ContainerPreview: sync list append with flagged index failed: " + ex.Message);
            UnsafeEntityManagerUtility.AddBufferElement(em, e, type.Value & SingletonsZeroSize.ClearFlagsMask, element);
        }
        if (!SyncToUserBufferEdit.AppendLanded(before, ReadUsers(em, e), _userKey))
            throw new InvalidOperationException("the sync list append on " + e.Index + ":" + e.Version + " did not read back");
    }

    /// <summary>Remove the last copy of this user and read it back; no write when the list holds none. Throws on a mismatch.</summary>
    private void RemoveOne(EntityManager em, Entity e)
    {
        var next = SyncToUserBufferEdit.RemoveOne(ReadUsers(em, e), _userKey);
        if (next == null) return;
        // A zero-row write still gets a valid pointer; its read-back (length 0) is its verification.
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
        if (!SyncToUserBufferEdit.Same(next, ReadUsers(em, e)))
            throw new InvalidOperationException("the sync list removal on " + e.Index + ":" + e.Version + " did not read back");
    }

    /// <summary>Startup sweep: remove one copy of <paramref name="user"/> from each inventory and item list of the chest that holds one. Returns how many.</summary>
    internal static int SweepOnce(World world, Entity user, Entity container)
    {
        var sweep = new ContainerPreviewSync(world, user, container);
        var em = world.EntityManager;
        int removed = 0;
        foreach (long key in sweep.Wanted(em))
        {
            var e = ToEntity(key);
            if (SyncToUserBufferEdit.CountOf(ReadUsers(em, e), sweep._userKey) == 0) continue;
            sweep.RemoveOne(em, e);
            removed++;
        }
        return removed;
    }

    /// <summary>Dev bridge readback: each entity this preview holds a copy on, the list's length and this user's copies.</summary>
    internal List<Dictionary<string, object?>> Describe()
    {
        var rows = new List<Dictionary<string, object?>>();
        if (!_world.IsCreated) return rows;
        var em = _world.EntityManager;
        foreach (long key in _ledger.Snapshot())
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
                if (_releaseFailures.TryGetValue(key, out int n)) row["releaseFailures"] = n;
            }
            catch (Exception ex) { row["error"] = ex.Message; }
            rows.Add(row);
        }
        return rows;
    }
}
#endif
