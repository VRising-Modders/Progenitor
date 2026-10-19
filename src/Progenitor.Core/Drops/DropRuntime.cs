using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ai;
using Progenitor.Ecs;
using Progenitor.Hooks;
using Progenitor.Inventory;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;

namespace Progenitor.Drops;

/// <summary>
/// Server observer behind <c>ctx.Inventory.OnGroundDrop</c> and <c>ctx.Inventory.OnDropAll</c>:
/// what players drop from their own bag, with the amounts read back from the bag.
///
/// <para><b>Seam.</b> The client's drop requests arrive as event entities carrying
/// <c>FromCharacter</c>: <c>DropItemAtSlotEvent</c> (the inventory menu's drop key) and
/// <c>DropEntireInventoryEvent</c> are handled by <c>DropItemSystem</c>; <c>DropInventoryItemEvent</c>
/// (a slot dragged onto the ground, naming an inventory by its <c>NetworkId</c>) by
/// <c>DropInventoryItemSystem</c>. Both systems handle their events on the main thread inside their own
/// update. A <c>SystemHooks.Before</c> on each reads the requests and the sender's bag rows; the
/// matching <c>SystemHooks.After</c> reads the rows again and the character's position, and the
/// difference is the drop. Nothing is written, patched or skipped. A drag-out that names a container
/// rather than the player's own inventory is not this observer's: <c>OnPlayerMove</c> reports it
/// (<c>Kind = Drop</c>, <c>Counterpart = Ground</c>).</para>
///
/// <para><b>Whole-inventory drops.</b> When an update carries a <c>DropEntireInventoryEvent</c>, the
/// Before also notes which containers of the dropped-inventory prefab
/// (<c>RootPrefabCollection.PlayerInventoryDropContainerPrefabGUID</c>) already exist. From the next
/// frame tick a new container of that prefab within <see cref="DropContainerMatcher.MaxDistance"/> of
/// where the player stood is that drop's, checked every few frames for up to
/// <see cref="ContainerSearchFrames"/> frames.</para>
///
/// <para><b>Cost.</b> The hooks exist only while something listens. They fire only on frames where
/// their system runs; a run with no event is one or two cached-query emptiness checks. Per handled
/// update: one entity array per event kind, a typed read and one raw read per request (64 at most),
/// two raw bag reads and one position read per dropping character (32 at most). A whole-inventory drop
/// also lists every inventory-bearing entity once in the Before, and again every few frames until its
/// container is found, reading each one's prefab.</para>
/// </summary>
internal static unsafe class DropRuntime
{
    private const int NetworkIdBytes = 12;
    private const long InstallRetryMs = 5_000;

    /// <summary>How long a whole-inventory drop waits for its container, frames.</summary>
    internal const int ContainerSearchFrames = 60;

    /// <summary>Frames between two container searches while a drop waits.</summary>
    private const int ContainerSearchEvery = 5;

    private static DropEventHub<PlayerGroundDrop>? _ground;
    private static DropEventHub<PlayerDropAll>? _all;
    private static readonly List<IDisposable> Hooks = new();
    private static readonly BagDropBatch DragBatch = new(), ItemBatch = new();
    private static readonly object DragOwner = new(), ItemOwner = new();
    private static readonly WorldBoundQuery DragQuery = new("Drops.DropInventoryItemEvent");
    private static readonly WorldBoundQuery SlotQuery = new("Drops.DropItemAtSlotEvent");
    private static readonly WorldBoundQuery AllQuery = new("Drops.DropEntireInventoryEvent");
    private static readonly WorldBoundQuery BufferOwners = new("Drops.InventoryBufferOwners");
    private static readonly WorldBoundQuery InstanceOwners = new("Drops.InventoryInstanceOwners");
    private static readonly List<MoveSlotRow> Rows = new();
    private static readonly List<PendingGroundDrop> FoundGround = new();
    private static readonly List<PendingDropAll> FoundAll = new();
    private static readonly List<PendingDropAll> Waiting = new();
    private static readonly List<DropContainerCandidate> Candidates = new();
    private static HashSet<long>? _containersBefore;
    private static long _frames;
    private static long _nextInstallTry;
    private static int _failures;

    // Counters for logs and a later bridge op.
    internal static long Updates, Requests, Truncated, Unchanged, Unread, LostAfter, DropAllsFound, DropAllsTimedOut;
    internal static string LastError = "";

    internal static bool Installed => _ground != null;

    internal static DropEventHub<PlayerGroundDrop> Ground =>
        _ground ?? throw new InvalidOperationException("ctx.Inventory.OnGroundDrop runs on the server side only.");

    internal static DropEventHub<PlayerDropAll> All =>
        _all ?? throw new InvalidOperationException("ctx.Inventory.OnDropAll runs on the server side only.");

    internal static void Install()
    {
        if (!Plugin.IsServerProcess || _ground != null) return;
        _ground = new DropEventHub<PlayerGroundDrop>("ground-drop", line => Plugin.Logger?.LogWarning(line)) { ActiveChanged = _ => SetHooks(AnyActive) };
        _all = new DropEventHub<PlayerDropAll>("drop-all", line => Plugin.Logger?.LogWarning(line)) { ActiveChanged = _ => SetHooks(AnyActive) };
    }

    internal static void Uninstall()
    {
        try { _ground?.Reset(); } catch (Exception e) { Plugin.Logger?.LogWarning("Drops: ground-drop reset threw: " + e.Message); }
        try { _all?.Reset(); } catch (Exception e) { Plugin.Logger?.LogWarning("Drops: drop-all reset threw: " + e.Message); }
        SetHooks(false);
        DragQuery.Release();
        SlotQuery.Release();
        AllQuery.Release();
        BufferOwners.Release();
        InstanceOwners.Release();
        GroundPiles.Release();
        DropPrefabsReader.Clear();
        Waiting.Clear();
        Candidates.Clear();
        _containersBefore = null;
        _ground = null;
        _all = null;
        Updates = Requests = Truncated = Unchanged = Unread = LostAfter = DropAllsFound = DropAllsTimedOut = 0;
        LastError = "";
        _failures = 0;
        _frames = 0;
        _nextInstallTry = 0;
    }

    private static bool AnyActive => (_ground?.Active ?? false) || (_all?.Active ?? false);
    private static bool GroundActive => _ground?.Active ?? false;
    private static bool AllActive => _all?.Active ?? false;

    private static void SetHooks(bool on)
    {
        if (!on)
        {
            foreach (var h in Hooks)
            {
                try { h.Dispose(); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Drops: hook dispose threw: " + e.Message); }
            }

            Hooks.Clear();
            DragBatch.Clear();
            ItemBatch.Clear();
            Waiting.Clear();
            _containersBefore = null;
            return;
        }

        if (Hooks.Count > 0) return;
        try
        {
            // Same bracket as the player-move observer: the Before runs after every other plugin's
            // Before, the After before any other plugin's After. Defaults: only when the system runs.
            Hooks.Add(SystemHooks.Before<DropInventoryItemSystem>(c => BeforeDrag(c), new HookOptions { Priority = -10_000 }));
            Hooks.Add(SystemHooks.After<DropInventoryItemSystem>(c => After(DragBatch, DragOwner, c), new HookOptions { Priority = 10_000 }));
            Hooks.Add(SystemHooks.Before<DropItemSystem>(c => BeforeItem(c), new HookOptions { Priority = -10_000 }));
            Hooks.Add(SystemHooks.After<DropItemSystem>(c => After(ItemBatch, ItemOwner, c), new HookOptions { Priority = 10_000 }));
            Plugin.Logger?.LogInfo("Drops: bag-drop observer on (DropInventoryItemSystem, DropItemSystem).");
        }
        catch (Exception e)
        {
            LastError = "hook install threw: " + e.Message;
            Plugin.Logger?.LogWarning("Drops: " + LastError);
            SetHooks(false);
        }
    }

    /// <summary>Main thread, from the inventory frame tick: deliver what the hooks found and search for drop-all containers.</summary>
    internal static void Tick()
    {
        if (_ground == null || _all == null) return;
        _frames++;
        // A Before without its After (the system threw, or another hook skipped it): nothing to compare.
        if (DragBatch.Open) { LostAfter++; DragBatch.Clear(); }
        if (ItemBatch.Open) { LostAfter++; ItemBatch.Clear(); }

        if (AnyActive && Hooks.Count == 0)
        {
            long now = Environment.TickCount64;
            if (now >= _nextInstallTry)
            {
                _nextInstallTry = now + InstallRetryMs;
                SetHooks(true);
            }
        }

        _ground.Drain();
        if (Waiting.Count > 0) Search();
        _all.Drain();
    }

    // ---- the hooks ------------------------------------------------------------------------------

    private static void BeforeDrag(SystemHookContext hook)
    {
        if (DragBatch.Open) { LostAfter++; DragBatch.Clear(); }
        if (!GroundActive) return;
        try
        {
            var world = hook.World;
            if (world == null || !world.IsCreated || world != Worlds.Server) return;
            var em = world.EntityManager;
            var query = Cached<DropInventoryItemEvent>(DragQuery, world, em);
            if (query.IsEmptyIgnoreFilter) return;
            DragBatch.Begin(DragOwner);
            Updates++;
            var events = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < events.Length; i++)
                {
                    var e = events[i];
                    if (!e.TryRead(em, out FromCharacter from)) continue;
                    byte* p = AiRuntime.RO<DropInventoryItemEvent>(em, e, Layout.ServerDropInventoryItemEventInventoryOffset + NetworkIdBytes);
                    if (p == null) continue;
                    Requests++;
                    int* raw = (int*)(p + Layout.ServerDropInventoryItemEventInventoryOffset);
                    var (n0, n1, n2) = InventorySortFilter.Normalize(raw[0], raw[1], raw[2]);
                    // Only the sender's own inventory; a container window's drop is OnPlayerMove's.
                    if (n2 != NetworkIdWords.TypeNormal || !PlayerMoveRuntime.IsOwnPlayer(em, from.Character, n0, n1)) continue;
                    DragBatch.AddRequest(Key(from.User), Key(from.Character), BagDropRequestKind.DragOut);
                }
            }
            finally
            {
                events.Dispose();
            }

            Truncated += DragBatch.Truncated;
            ReadBags(em, DragBatch, after: false);
            if (DragBatch.BagCount == 0) DragBatch.Clear();
        }
        catch (Exception e)
        {
            DragBatch.Clear();
            Fail("DropInventoryItemSystem before", e);
        }
    }

    private static void BeforeItem(SystemHookContext hook)
    {
        if (ItemBatch.Open) { LostAfter++; ItemBatch.Clear(); }
        bool ground = GroundActive, all = AllActive;
        if (!ground && !all) return;
        try
        {
            var world = hook.World;
            if (world == null || !world.IsCreated || world != Worlds.Server) return;
            var em = world.EntityManager;
            // Drop-key requests also matter to a drop-all listener: one in the same update makes that drop ambiguous.
            var slots = Cached<DropItemAtSlotEvent>(SlotQuery, world, em);
            var alls = all ? Cached<DropEntireInventoryEvent>(AllQuery, world, em) : default;
            bool anySlot = !slots.IsEmptyIgnoreFilter;
            bool anyAll = all && !alls.IsEmptyIgnoreFilter;
            if (!anySlot && !anyAll) return;
            ItemBatch.Begin(ItemOwner);
            Updates++;
            if (anySlot) Collect(em, slots, BagDropRequestKind.DropKey);
            if (anyAll) Collect(em, alls, BagDropRequestKind.DropAll);
            Truncated += ItemBatch.Truncated;
            if (ItemBatch.BagCount == 0)
            {
                ItemBatch.Clear();
                return;
            }

            ReadBags(em, ItemBatch, after: false);
            // The containers that exist now are never this update's drop.
            _containersBefore = ItemBatch.HasDropAll ? ExistingContainers(world, em) : null;
        }
        catch (Exception e)
        {
            ItemBatch.Clear();
            Fail("DropItemSystem before", e);
        }
    }

    private static void Collect(EntityManager em, EntityQuery query, BagDropRequestKind kind)
    {
        var events = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < events.Length; i++)
            {
                var e = events[i];
                if (!e.TryRead(em, out FromCharacter from)) continue;
                int setting = 0;
                if (kind == BagDropRequestKind.DropAll)
                {
                    byte* p = AiRuntime.RO<DropEntireInventoryEvent>(em, e, Layout.ServerDropEntireInventoryEventSettingOffset + sizeof(int));
                    setting = p == null ? -1 : *(int*)(p + Layout.ServerDropEntireInventoryEventSettingOffset);
                }

                Requests++;
                ItemBatch.AddRequest(Key(from.User), Key(from.Character), kind, setting);
            }
        }
        finally
        {
            events.Dispose();
        }
    }

    private static void After(BagDropBatch batch, object owner, SystemHookContext hook)
    {
        if (!batch.Open || !ReferenceEquals(batch.Owner, owner)) return;
        try
        {
            var world = hook.World;
            if (world == null || !world.IsCreated || world != Worlds.Server)
            {
                batch.Clear();
                return;
            }

            var em = world.EntityManager;
            ReadBags(em, batch, after: true);
            FoundGround.Clear();
            FoundAll.Clear();
            var before = ReferenceEquals(batch, ItemBatch) ? _containersBefore : null;
            if (ReferenceEquals(batch, ItemBatch)) _containersBefore = null;
            batch.Emit(DateTime.UtcNow, FoundGround, FoundAll);
            Unchanged += batch.Unchanged;
            Unread += batch.Unread;
            if (GroundActive)
                foreach (var g in FoundGround)
                {
                    var copy = g;
                    _ground!.Enqueue(seq => copy.ToDrop(seq));
                }

            if (AllActive)
                foreach (var a in FoundAll)
                {
                    a.Before = before;
                    a.Deadline = _frames + ContainerSearchFrames;
                    Waiting.Add(a);
                }

            FoundGround.Clear();
            FoundAll.Clear();
        }
        catch (Exception e)
        {
            batch.Clear();
            Fail("after", e);
        }
    }

    /// <summary>Each requesting character's bag rows (instance 0, the bag) and, after the update, its position.</summary>
    private static void ReadBags(EntityManager em, BagDropBatch batch, bool after)
    {
        for (int i = 0; i < batch.BagCount; i++)
        {
            long c = batch.CharacterOf(i);
            var character = new Entity { Index = (int)(c >> 32), Version = (int)c };
            if (!after)
            {
                long key = 0;
                if (character.Exists(em) && UnityInventoryBackend.TryInventoryEntity(em, character, 0, out var bag, out _))
                    key = Key(bag);
                batch.SetKey(i, key);
            }

            long k = batch.KeyOf(i);
            batch.SetRows(i, after, k != 0 && PlayerMoveRuntime.ReadRowsInto(em, k, Rows) ? Rows : null);
            if (after && character.TryRead(em, out Translation t))
                batch.SetPosition(i, t.Value.x, t.Value.y, t.Value.z);
        }
    }

    // ---- whole-inventory drop containers ---------------------------------------------------------

    /// <summary>The containers of the dropped-inventory prefab that exist now; null when the prefab is unknown.</summary>
    private static HashSet<long>? ExistingContainers(World world, EntityManager em)
    {
        if (!DropPrefabsReader.TryRead(world, out var prefabs, out _) || prefabs.PlayerInventoryDropContainer == 0) return null;
        var set = new HashSet<long>();
        Candidates.Clear();
        ListContainers(world, em, prefabs.PlayerInventoryDropContainer, Candidates);
        foreach (var c in Candidates) set.Add(c.Key);
        Candidates.Clear();
        return set;
    }

    /// <summary>Every live entity with an inventory (its own slots, or instance rows) whose prefab is <paramref name="prefab"/>.</summary>
    private static void ListContainers(World world, EntityManager em, int prefab, List<DropContainerCandidate> into)
    {
        Scan(em, Owners<InventoryBuffer>(BufferOwners, world, em), prefab, into);
        Scan(em, Owners<InventoryInstanceElement>(InstanceOwners, world, em), prefab, into);
    }

    private static void Scan(EntityManager em, EntityQuery query, int prefab, List<DropContainerCandidate> into)
    {
        var array = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < array.Length; i++)
            {
                var e = array[i];
                if (!e.TryRead(em, out PrefabGUID guid) || guid.GuidHash != prefab) continue;
                long key = Key(e);
                bool dup = false;
                foreach (var c in into) if (c.Key == key) { dup = true; break; }
                if (dup) continue;
                if (GroundPiles.TryPosition(em, e, out float x, out float y, out float z))
                    into.Add(new DropContainerCandidate(key, x, y, z));
            }
        }
        finally
        {
            array.Dispose();
        }
    }

    /// <summary>Find containers for the drops waiting; deliver the found ones and the ones out of time.</summary>
    private static void Search()
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated)
        {
            Waiting.Clear();
            return;
        }

        bool due = _frames % ContainerSearchEvery == 0;
        bool anyExpired = false;
        foreach (var w in Waiting) if (_frames >= w.Deadline) { anyExpired = true; break; }
        if (!due && !anyExpired) return;

        var em = world.EntityManager;
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        int[] match = Array.Empty<int>();
        bool[] ambiguous = Array.Empty<bool>();
        string unknown = "";
        try
        {
            Candidates.Clear();
            if (DropPrefabsReader.TryRead(world, out var prefabs, out string why) && prefabs.PlayerInventoryDropContainer != 0)
            {
                ListContainers(world, em, prefabs.PlayerInventoryDropContainer, Candidates);
                match = DropContainerMatcher.Match(Waiting, Candidates, out ambiguous);
            }
            else unknown = "the dropped-inventory prefab could not be read (" + why + ")";
        }
        catch (Exception e)
        {
            Fail("drop-all search", e);
            unknown = "the container search failed: " + e.Message;
        }

        for (int i = Waiting.Count - 1; i >= 0; i--)
        {
            var w = Waiting[i];
            int at = i < match.Length ? match[i] : -1;
            if (at >= 0)
            {
                var c = Candidates[at];
                var container = new Entity { Index = (int)(c.Key >> 32), Version = (int)c.Key };
                bool covers = false;
                try
                {
                    var snapshot = InventoryRuntime.Engine.Read(InventoryRef.For(container));
                    covers = snapshot.Ok && DropContainerMatcher.ContentsCover(w.Items, snapshot.Slots);
                }
                catch { covers = false; }

                DropAllsFound++;
                string note = covers ? "" : "the container does not hold everything that left the bag";
                bool amb = ambiguous[i];
                _all!.Enqueue(seq => w.ToDrop(seq, container.Index, container.Version, epoch, covers, amb, note));
                Waiting.RemoveAt(i);
            }
            else if (_frames >= w.Deadline || w.Before == null || unknown.Length > 0)
            {
                DropAllsTimedOut++;
                string reason = unknown.Length > 0 ? unknown
                    : w.Before == null ? "the dropped-inventory prefab was not readable when the drop happened"
                    : !w.HasPosition ? "the player's position was not readable after the drop"
                    : "no new dropped-inventory container appeared within " + DropContainerMatcher.MaxDistance + " m in " + ContainerSearchFrames + " frames";
                _all!.Enqueue(seq => w.ToDrop(seq, 0, 0, epoch, false, false, reason));
                Waiting.RemoveAt(i);
            }
        }

        Candidates.Clear();
    }

    // ---- helpers ----------------------------------------------------------------------------------

    private static EntityQuery Cached<T>(WorldBoundQuery cache, World world, EntityManager em)
    {
        if (cache.TryGet(world, out var query)) return query;
        query = Query.Build(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<FromCharacter>(ComponentType.AccessMode.ReadOnly));
        cache.Store(world, query);
        return query;
    }

    private static EntityQuery Owners<T>(WorldBoundQuery cache, World world, EntityManager em)
    {
        if (cache.TryGet(world, out var query)) return query;
        query = Query.Build(em, includeDisabled: false, includeSystems: false,
            ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<PrefabGUID>(ComponentType.AccessMode.ReadOnly));
        cache.Store(world, query);
        return query;
    }

    private static void Fail(string phase, Exception e)
    {
        LastError = phase + ": " + e.Message;
        if (_failures++ < 4) Plugin.Logger?.LogWarning("Drops: " + LastError);
    }

    private static long Key(Entity e) => ((long)e.Index << 32) | (uint)e.Version;
}
