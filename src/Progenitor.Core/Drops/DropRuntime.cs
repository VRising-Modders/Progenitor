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
/// <para><b>Whole-inventory drops.</b> While a drop-all listener exists, a book
/// (<see cref="DropContainerBook"/>) knows which containers of the dropped-inventory prefab
/// (<c>RootPrefabCollection.PlayerInventoryDropContainerPrefabGUID</c>) are already accounted for:
/// a baseline when the first listener arrives and after each world load, always from the frame
/// tick or the subscribing call, never inside a game update. After a drop, from the next frame
/// tick and then every <see cref="ContainerSearchEvery"/> frames for up to
/// <see cref="ContainerSearchFrames"/> frames, one shared scan per pass offers the new containers;
/// a container within <see cref="DropContainerMatcher.MaxDistance"/> of where the player stood that
/// holds what left the bag is that drop's.</para>
///
/// <para><b>Cost.</b> The hooks exist only while something listens. They fire only on frames where
/// their system runs; a run with no event is one or two cached-query emptiness checks. Per handled
/// update: one entity array per event kind, a typed read and one raw read per request (64 at most),
/// two raw bag reads and one position read per dropping character (32 at most). The container scan
/// (the baseline, and each search pass while a drop waits) visits inventory-bearing entities and reads
/// their prefab, at most <see cref="MaxScanVisits"/> per scan; it never runs inside the hooks.</para>
/// </summary>
internal static unsafe class DropRuntime
{
    private const int NetworkIdBytes = 12;
    private const long InstallRetryMs = 5_000;

    /// <summary>How long a whole-inventory drop waits for its container, frames.</summary>
    internal const int ContainerSearchFrames = 60;

    /// <summary>Frames between two container searches while a drop waits.</summary>
    internal const int ContainerSearchEvery = 10;

    /// <summary>The most entities one container scan visits; past this the scan stops and says so.</summary>
    internal const int MaxScanVisits = 50_000;

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
    private static readonly List<DropContainerCandidate> Scanned = new();
    private static readonly List<DropContainerCandidate> Candidates = new();
    private static readonly HashSet<long> ScanKeys = new();
    private static readonly DropContainerBook Book = new();
    private static long _nextSearch;
    private static bool _scanCapped;
    private static long _frames;
    private static long _nextInstallTry;
    private static int _failures;

    // Counters for logs and a later bridge op.
    internal static long Updates, Requests, Truncated, Unchanged, Unread, LostAfter, DropAllsFound, DropAllsTimedOut, ScanCapped;
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
        _all = new DropEventHub<PlayerDropAll>("drop-all", line => Plugin.Logger?.LogWarning(line))
        {
            ActiveChanged = on =>
            {
                // The book starts before the hooks can see a drop, so the first drop's container is new to it.
                if (on) EnsureBaseline();
                else Book.Clear();
                SetHooks(AnyActive);
            },
        };
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
        Scanned.Clear();
        ScanKeys.Clear();
        Book.Clear();
        _nextSearch = 0;
        _ground = null;
        _all = null;
        Updates = Requests = Truncated = Unchanged = Unread = LostAfter = DropAllsFound = DropAllsTimedOut = ScanCapped = 0;
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
        if (AllActive)
        {
            int epoch = Progenitor.WorldState.WorldEpoch.Current;
            if (!Book.HasBaselineFor(epoch))
            {
                // A world load (the book had another epoch): drops still waiting belong to the old world.
                if (Book.Epoch != int.MinValue) FlushWaiting(epoch, "the world was reloaded before the container was found");
                EnsureBaseline();
            }

            if (Waiting.Count > 0) Search();
        }
        else if (Waiting.Count > 0) Waiting.Clear();
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
            // Both kinds always: a drop-key request makes a drop-all of the same update ambiguous, and a
            // bag that had a drop-all in this update must never be reported as per-item ground drops.
            var slots = Cached<DropItemAtSlotEvent>(SlotQuery, world, em);
            var alls = Cached<DropEntireInventoryEvent>(AllQuery, world, em);
            bool anySlot = !slots.IsEmptyIgnoreFilter;
            bool anyAll = !alls.IsEmptyIgnoreFilter;
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
            batch.Emit(DateTime.UtcNow, FoundGround, FoundAll);
            Unchanged += batch.Unchanged;
            Unread += batch.Unread;
            if (GroundActive)
                foreach (var g in FoundGround)
                {
                    var copy = g;
                    _ground!.Enqueue(seq => copy.ToDrop(seq));
                }

            if (AllActive && FoundAll.Count > 0)
            {
                bool booked = Book.HasBaselineFor(Progenitor.WorldState.WorldEpoch.Current);
                foreach (var a in FoundAll)
                {
                    a.Deadline = _frames + ContainerSearchFrames;
                    // Without a book from before this drop, its container cannot be told from older ones.
                    a.Unbooked = !booked;
                    Waiting.Add(a);
                }

                // The first search runs from the next frame tick, outside this update.
                _nextSearch = _frames + 1;
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

    /// <summary>Take the book's baseline for this world load (outside any game update). Leaves the book unset when the prefab is not readable yet.</summary>
    private static void EnsureBaseline()
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        if (Book.HasBaselineFor(epoch)) return;
        try
        {
            if (!DropPrefabsReader.TryRead(world, out var prefabs, out _) || prefabs.PlayerInventoryDropContainer == 0) return;
            ScanContainers(world, prefabs.PlayerInventoryDropContainer, Scanned);
            var keys = new List<long>(Scanned.Count);
            foreach (var c in Scanned) keys.Add(c.Key);
            Book.Baseline(epoch, keys);
            Scanned.Clear();
        }
        catch (Exception e)
        {
            Fail("drop-all baseline", e);
        }
    }

    /// <summary>
    /// Every live entity of the prefab with an inventory (its own slots, or instance rows), each once,
    /// visiting at most <see cref="MaxScanVisits"/> entities.
    /// </summary>
    private static void ScanContainers(World world, int prefab, List<DropContainerCandidate> into)
    {
        into.Clear();
        ScanKeys.Clear();
        _scanCapped = false;
        var em = world.EntityManager;
        int budget = MaxScanVisits;
        budget = Scan(em, Owners<InventoryBuffer>(BufferOwners, world, em), prefab, into, budget);
        if (budget > 0) Scan(em, Owners<InventoryInstanceElement>(InstanceOwners, world, em), prefab, into, budget);
        ScanKeys.Clear();
    }

    private static int Scan(EntityManager em, EntityQuery query, int prefab, List<DropContainerCandidate> into, int budget)
    {
        var array = query.ToEntityArray(Allocator.Temp);
        try
        {
            for (int i = 0; i < array.Length; i++)
            {
                if (budget-- <= 0)
                {
                    _scanCapped = true;
                    ScanCapped++;
                    return 0;
                }

                var e = array[i];
                if (!e.TryRead(em, out PrefabGUID guid) || guid.GuidHash != prefab) continue;
                long key = Key(e);
                if (!ScanKeys.Add(key)) continue;
                if (GroundPiles.TryPosition(em, e, out float x, out float y, out float z))
                    into.Add(new DropContainerCandidate(key, x, y, z));
            }
        }
        finally
        {
            array.Dispose();
        }

        return budget;
    }

    /// <summary>One shared pass for every drop waiting: offer the new containers, deliver the matched ones and the ones out of time.</summary>
    private static void Search()
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated)
        {
            Waiting.Clear();
            return;
        }

        bool anyExpired = false;
        foreach (var w in Waiting) if (_frames >= w.Deadline) { anyExpired = true; break; }
        if (_frames < _nextSearch && !anyExpired) return;
        _nextSearch = _frames + ContainerSearchEvery;

        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        int[] match = Array.Empty<int>();
        bool[] ambiguous = Array.Empty<bool>();
        string unknown = "";
        var contents = new Dictionary<long, IReadOnlyList<InventorySlot>?>();
        try
        {
            Candidates.Clear();
            if (!Book.HasBaselineFor(epoch)) unknown = "the dropped-inventory containers could not be listed before the drop";
            else if (DropPrefabsReader.TryRead(world, out var prefabs, out string why) && prefabs.PlayerInventoryDropContainer != 0)
            {
                ScanContainers(world, prefabs.PlayerInventoryDropContainer, Scanned);
                Book.New(Scanned, Candidates);
                Scanned.Clear();
                match = DropContainerMatcher.Match(Waiting, Candidates, (r, c) =>
                {
                    if (Waiting[r].Unbooked) return false;
                    long key = Candidates[c].Key;
                    if (!contents.TryGetValue(key, out var slots))
                    {
                        var container = new Entity { Index = (int)(key >> 32), Version = (int)key };
                        var snapshot = InventoryRuntime.Engine.Read(InventoryRef.For(container));
                        slots = snapshot.Ok ? snapshot.Slots : null;
                        contents[key] = slots;
                    }

                    bool covers = slots != null && DropContainerMatcher.ContentsCover(Waiting[r].Items, slots);
                    if (!covers) Waiting[r].SawMismatch = true;
                    return covers;
                }, out ambiguous);
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
                Book.Matched(c.Key);
                DropAllsFound++;
                int index = (int)(c.Key >> 32), version = (int)c.Key;
                bool amb = ambiguous[i];
                _all!.Enqueue(seq => w.ToDrop(seq, index, version, epoch, true, amb, ""));
                Waiting.RemoveAt(i);
            }
            else if (_frames >= w.Deadline || unknown.Length > 0 || w.Unbooked)
            {
                DropAllsTimedOut++;
                string reason = unknown.Length > 0 ? unknown
                    : w.Unbooked ? "the dropped-inventory containers could not be listed before the drop (the prefab table was not readable)"
                    : !w.HasPosition ? "the player's position was not readable after the drop"
                    : w.SawMismatch ? "a new dropped-inventory container lay within " + DropContainerMatcher.MaxDistance + " m but did not hold what left the bag"
                    : "no new dropped-inventory container appeared within " + DropContainerMatcher.MaxDistance + " m in " + ContainerSearchFrames + " frames";
                if (_scanCapped) reason += " (the container scan stopped at " + MaxScanVisits + " entities)";
                _all!.Enqueue(seq => w.ToDrop(seq, 0, 0, epoch, false, false, reason));
                Waiting.RemoveAt(i);
            }
        }

        Candidates.Clear();
        if (Waiting.Count == 0) Book.Settle();
    }

    /// <summary>Deliver every waiting drop without a container.</summary>
    private static void FlushWaiting(int epoch, string reason)
    {
        foreach (var w in Waiting)
        {
            DropAllsTimedOut++;
            var drop = w;
            _all?.Enqueue(seq => drop.ToDrop(seq, 0, 0, epoch, false, false, reason));
        }

        Waiting.Clear();
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
