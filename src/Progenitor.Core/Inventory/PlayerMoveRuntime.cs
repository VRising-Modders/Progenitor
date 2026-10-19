using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Network;
using Progenitor.Ai;
using Progenitor.Ecs;
using Progenitor.Hooks;
using Unity.Collections;
using Unity.Entities;

namespace Progenitor.Inventory;

/// <summary>
/// Server observer behind <c>ctx.Inventory.OnPlayerMove</c>: item moves a player makes by hand in
/// a container window, with the amounts read back from the inventories.
///
/// <para><b>Seam.</b> Each item request a client sends arrives as one event entity
/// (<c>MoveItemBetweenInventoriesEvent</c>, <c>MoveAllItemsBetweenInventoriesEvent</c>/<c>V2</c>,
/// <c>SmartMergeItemsBetweenInventoriesEvent</c>, <c>SplitItemEvent</c>/<c>V2</c>,
/// <c>SortSingleInventoryEvent</c>, <c>SortAllInventoriesEvent</c>, <c>DropInventoryItemEvent</c>,
/// <c>EquipItemFromInventoryEvent</c>) carrying <c>FromCharacter</c> and the inventories'
/// <c>NetworkId</c>s. One server system handles each kind on the main thread, inside its own
/// update (the game's lambda runs with structural changes, no scheduled job). A
/// <c>SystemHooks.Before</c> on that system reads the events and the rows of every non-player
/// inventory they name; the matching <c>SystemHooks.After</c> reads the same rows again and the
/// difference is the move. Nothing is written, patched or skipped.</para>
///
/// <para><b>Cost.</b> The hooks exist only while something listens (a plugin subscription or the
/// Dev bridge recorder). They fire only on frames where that system runs; a run with no event is
/// one cached-query emptiness check. Per handled update: one entity array, one typed read and one
/// or two id reads per request (64 at most), and two raw buffer reads per observed inventory (32
/// at most). Container ids are looked up at delivery, outside the game's update.</para>
/// </summary>
internal static unsafe class PlayerMoveRuntime
{
    private const int NetworkIdBytes = 12;
    private const int MaxRows = 1024;

    private static PlayerMoveHub? _hub;
    private static NetworkIdResolver? _resolver;
    private static readonly List<IDisposable> Hooks = new();
    private static readonly List<Watcher> Watchers = new();
    private static readonly PlayerMoveBatch Batch = new();
    private static readonly List<PendingPlayerMove> Found = new();
    private static readonly List<MoveSlotRow> Rows = new();
    private static readonly List<int> SideA = new(4), SideB = new(4);
    private static int _failures;
    private const long InstallRetryMs = 5_000;
    private static long _nextInstallTry;

    // Counters for the Dev bridge.
    internal static long Updates, Requests, Truncated, PlayerOnly, Unresolved, Unchanged, Unread, LostAfter;
    internal static string LastError = "";

    // Resolver rebuild timing: a rebuild on a lookup miss runs inside the game's update (the hook); the
    // prime runs from the frame tick once per world load while the hooks are in place.
    internal static double HookRebuildMaxUs, PrimeLastUs;
    private static int _primedEpoch = int.MinValue;

    internal static bool Installed => _hub != null;

    internal static PlayerMoveHub Hub =>
        _hub ?? throw new InvalidOperationException("ctx.Inventory runs on the server side only.");

    internal static bool HooksInstalled => Hooks.Count > 0;

    internal static int ResolverRebuilds => _resolver?.Rebuilds ?? 0;
    internal static int ResolverPrimes => _resolver?.Primes ?? 0;

    private sealed class Watcher
    {
        internal PlayerInventoryMoveKind Kind;
        internal string Name = "";
        internal WorldBoundQuery Cache = null!;
        internal Func<EntityManager, EntityQuery> Build = null!;
        internal Func<EntityManager, Entity, int, IntPtr> Raw = null!;
        internal int OffsetA, OffsetB = -1;
        internal MoveSideRole FixedB = MoveSideRole.None;
        internal int Need;
    }

    internal static void Install()
    {
        if (!Plugin.IsServerProcess || _hub != null) return;
        _hub = new PlayerMoveHub(line => Plugin.Logger?.LogWarning(line)) { ActiveChanged = SetHooks };
        _resolver = new NetworkIdResolver(new ServerNetworkIdSource(includeInventoryEntities: true));
        BuildWatchers();
    }

    internal static void Uninstall()
    {
        try { _hub?.Reset(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Inventory: player-move reset threw: " + e.Message); }
        SetHooks(false);
        foreach (var w in Watchers) w.Cache.Release();
        Watchers.Clear();
        Batch.Clear();
        Found.Clear();
        _resolver?.Clear();
        _resolver = null;
        _hub = null;
        Updates = Requests = Truncated = PlayerOnly = Unresolved = Unchanged = Unread = LostAfter = 0;
        LastError = "";
        _failures = 0;
        _nextInstallTry = 0;
        HookRebuildMaxUs = PrimeLastUs = 0;
        _primedEpoch = int.MinValue;
    }

    private static void BuildWatchers()
    {
        Watchers.Clear();
        Add<MoveItemBetweenInventoriesEvent>(PlayerInventoryMoveKind.Move, Layout.ServerMoveItemEventFromInventoryOffset, Layout.ServerMoveItemEventToInventoryOffset);
        Add<MoveAllItemsBetweenInventoriesEvent>(PlayerInventoryMoveKind.MoveAll, Layout.ServerMoveAllItemsEventFromInventoryOffset, Layout.ServerMoveAllItemsEventToInventoryOffset);
        Add<MoveAllItemsBetweenInventoriesEventV2>(PlayerInventoryMoveKind.MoveAll, Layout.ServerMoveAllItemsEventV2FromInventoryOffset, Layout.ServerMoveAllItemsEventV2ToInventoryOffset);
        Add<SmartMergeItemsBetweenInventoriesEvent>(PlayerInventoryMoveKind.SmartMerge, Layout.ServerSmartMergeEventFromInventoryOffset, Layout.ServerSmartMergeEventToInventoryOffset);
        Add<SplitItemEvent>(PlayerInventoryMoveKind.Split, Layout.ServerSplitItemEventInventoryOffset);
        Add<SplitItemEventV2>(PlayerInventoryMoveKind.Split, Layout.ServerSplitItemEventV2FromInventoryOffset, Layout.ServerSplitItemEventV2ToInventoryOffset);
        Add<SortSingleInventoryEvent>(PlayerInventoryMoveKind.Sort, Layout.ServerSortSingleInventoryEventInventoryOffset);
        Add<SortAllInventoriesEvent>(PlayerInventoryMoveKind.SortAll, Layout.ServerSortAllInventoriesEventInventoryOffset);
        Add<DropInventoryItemEvent>(PlayerInventoryMoveKind.Drop, Layout.ServerDropInventoryItemEventInventoryOffset, -1, MoveSideRole.Ground);
        Add<EquipItemFromInventoryEvent>(PlayerInventoryMoveKind.Equip, Layout.ServerEquipItemFromInventoryEventFromInventoryOffset, -1, MoveSideRole.Equipment);
    }

    private static void Add<T>(PlayerInventoryMoveKind kind, int offsetA, int offsetB = -1, MoveSideRole fixedB = MoveSideRole.None)
    {
        Watchers.Add(new Watcher
        {
            Kind = kind,
            Name = typeof(T).Name,
            Cache = new WorldBoundQuery("PlayerMoves." + typeof(T).Name),
            Build = em => Query.Build(em, includeDisabled: false, includeSystems: false,
                ComponentIndex.Of<T>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<FromCharacter>(ComponentType.AccessMode.ReadOnly)),
            Raw = (m, e, n) => (IntPtr)AiRuntime.RO<T>(m, e, n),
            OffsetA = offsetA,
            OffsetB = offsetB,
            FixedB = fixedB,
            Need = Math.Max(offsetA, offsetB) + NetworkIdBytes,
        });
    }

    /// <summary>Install the hook pairs while something listens; remove them when the last listener leaves.</summary>
    private static void SetHooks(bool on)
    {
        if (!on)
        {
            foreach (var h in Hooks)
            {
                try { h.Dispose(); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Inventory: player-move hook dispose threw: " + e.Message); }
            }

            Hooks.Clear();
            Batch.Clear();
            return;
        }

        if (Hooks.Count > 0 || Watchers.Count == 0) return;
        try
        {
            Pair<MoveItemBetweenInventoriesSystem>(Watchers[0]);
            Pair<MoveAllItemsBetweenInventoriesSystem>(Watchers[1]);
            Pair<MoveAllItemsBetweenInventoriesV2System>(Watchers[2]);
            Pair<SmartMergeItemsBetweenInventoriesSystem>(Watchers[3]);
            Pair<SplitItemSystem>(Watchers[4]);
            Pair<SplitItemV2System>(Watchers[5]);
            Pair<SortSingleInventorySystem>(Watchers[6]);
            Pair<SortAllInventoriesSystem>(Watchers[7]);
            Pair<DropInventoryItemSystem>(Watchers[8]);
            Pair<EquipItemFromInventorySystem>(Watchers[9]);
            Plugin.Logger?.LogInfo("Inventory: player-move observer on (" + Watchers.Count + " item request systems).");
        }
        catch (Exception e)
        {
            LastError = "hook install threw: " + e.Message;
            Plugin.Logger?.LogWarning("Inventory: player-move " + LastError);
            SetHooks(false);
        }
    }

    private static void Pair<TSystem>(Watcher w)
    {
        // Higher priority runs first in both phases. The Before runs last (other plugins' Before hooks,
        // which may destroy or rewrite an event, have run) and the After first (no other plugin's After
        // has written yet), so the pair brackets exactly the game's update. Defaults: only when the system runs.
        Hooks.Add(SystemHooks.Before<TSystem>(ctx => Before(w, ctx), new HookOptions { Priority = -10_000 }));
        Hooks.Add(SystemHooks.After<TSystem>(ctx => After(w, ctx), new HookOptions { Priority = 10_000 }));
    }

    /// <summary>Main thread, from <c>InventoryRuntime.Tick</c>: deliver what the hooks found.</summary>
    internal static void Tick()
    {
        if (_hub == null) return;
        if (Batch.Open)
        {
            // A Before without its After (the system threw, or another hook skipped it): nothing to compare.
            LostAfter++;
            Batch.Clear();
        }

        // Build the network-id map outside the game's update once per world load, so the first move
        // does not pay the scan inside the hook. A miss later still rebuilds there (throttled, 250 ms).
        if (Hooks.Count > 0 && _resolver != null)
        {
            int epoch = Progenitor.WorldState.WorldEpoch.Current;
            var world = Worlds.Server;
            if (epoch != _primedEpoch && world != null && world.IsCreated)
            {
                _primedEpoch = epoch;
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                _resolver.Prime(epoch, Environment.TickCount64);
                PrimeLastUs = Micros(t0);
            }
        }

        // A failed install is retried while something listens, at most every few seconds.
        if (_hub.Active && Hooks.Count == 0)
        {
            long now = Environment.TickCount64;
            if (now >= _nextInstallTry)
            {
                _nextInstallTry = now + InstallRetryMs;
                SetHooks(true);
            }
        }

        if (_hub.PendingCount == 0) return;
        bool recording = _hub.Recording;
        _hub.Drain(r => InventoryRuntime.IdOf(r.Owner, out _),
            recording ? m => Plugin.Logger?.LogInfo("Inventory: player move " + m) : null);
    }

    // ---- the hooks ------------------------------------------------------------------------------

    private static void Before(Watcher w, SystemHookContext hook)
    {
        if (_hub == null || !_hub.Active) return;
        // A batch still open here never got its After (the system threw, or another hook skipped it).
        // Drop it before any early return, so a later After of this system cannot diff stale rows.
        if (Batch.Open)
        {
            LostAfter++;
            Batch.Clear();
        }

        try
        {
            var world = hook.World;
            if (world == null || !world.IsCreated || world != Worlds.Server) return;
            var em = world.EntityManager;
            if (!w.Cache.TryGet(world, out var query))
            {
                query = w.Build(em);
                w.Cache.Store(world, query);
            }

            if (query.IsEmptyIgnoreFilter) return;
            Batch.Begin(w, w.Kind);
            Updates++;
            var events = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < events.Length; i++)
                {
                    if (Batch.RequestCount >= PlayerMoveBatch.MaxRequests)
                    {
                        Truncated += events.Length - i;
                        Batch.MarkIncomplete();
                        break;
                    }

                    ReadRequest(em, w, events[i]);
                }
            }
            finally
            {
                events.Dispose();
            }

            if (Batch.InventoryCount == 0)
            {
                Batch.Clear();
                return;
            }

            for (int i = 0; i < Batch.InventoryCount; i++)
                Batch.SetRows(i, after: false, ReadRows(em, Batch.KeyOf(i)) ? Rows : null);
        }
        catch (Exception e)
        {
            Batch.Clear();
            Fail(w, "before", e);
        }
    }

    private static void After(Watcher w, SystemHookContext hook)
    {
        if (!Batch.Open || !ReferenceEquals(Batch.Owner, w)) return;
        try
        {
            var world = hook.World;
            if (world == null || !world.IsCreated || world != Worlds.Server)
            {
                Batch.Clear();
                return;
            }

            var em = world.EntityManager;
            for (int i = 0; i < Batch.InventoryCount; i++)
                Batch.SetRows(i, after: true, ReadRows(em, Batch.KeyOf(i)) ? Rows : null);
            Found.Clear();
            Batch.Emit(DateTime.UtcNow, Found);
            Unchanged += Batch.Unchanged;
            Unread += Batch.Unread;
            if (Found.Count > 0) _hub?.Enqueue(Found);
            Found.Clear();
        }
        catch (Exception e)
        {
            Batch.Clear();
            Fail(w, "after", e);
        }
    }

    private static void Fail(Watcher w, string phase, Exception e)
    {
        LastError = w.Name + " " + phase + ": " + e.Message;
        if (_failures++ < 4) Plugin.Logger?.LogWarning("Inventory: player-move " + LastError);
    }

    // ---- one request ----------------------------------------------------------------------------

    private static void ReadRequest(EntityManager em, Watcher w, Entity e)
    {
        // A request that cannot be read could have touched an observed inventory: the batch is then incomplete.
        if (!e.TryRead(em, out FromCharacter from)) { Batch.MarkIncomplete(); return; }
        byte* p = (byte*)w.Raw(em, e, w.Need);
        if (p == null) { Batch.MarkIncomplete(); return; }
        Requests++;
        var character = from.Character;
        SideA.Clear();
        SideB.Clear();
        var roleA = Resolve(em, p + w.OffsetA, character, SideA);
        var roleB = w.OffsetB >= 0 ? Resolve(em, p + w.OffsetB, character, SideB) : w.FixedB;
        if (roleA == MoveSideRole.Unknown) Unresolved++;
        if (roleB == MoveSideRole.Unknown) Unresolved++;
        if (SideA.Count == 0 && SideB.Count == 0)
        {
            if (roleA != MoveSideRole.Unknown && roleB != MoveSideRole.Unknown) PlayerOnly++;
            else Batch.MarkIncomplete();
            return;
        }

        Batch.AddRequest(Key(from.User), Key(character), roleA, SideA, roleB, SideB);
    }

    /// <summary>What a <c>NetworkId</c> in the event names; non-player inventories are added to the batch.</summary>
    private static MoveSideRole Resolve(EntityManager em, byte* netPtr, Entity character, List<int> into)
    {
        int* raw = (int*)netPtr;
        var (n0, n1, n2) = InventorySortFilter.Normalize(raw[0], raw[1], raw[2]);
        if (n2 != NetworkIdWords.TypeNormal) return MoveSideRole.Unknown;
        if (IsOwnPlayer(em, character, n0, n1)) return MoveSideRole.OwnPlayer;
        if (_resolver == null) return MoveSideRole.Unknown;
        int rebuilds = _resolver.Rebuilds;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        bool hit = _resolver.TryResolve(n0, n1, n2, Progenitor.WorldState.WorldEpoch.Current, Environment.TickCount64, out int ei, out int ev, out _);
        if (_resolver.Rebuilds != rebuilds)
            HookRebuildMaxUs = Math.Max(HookRebuildMaxUs, Micros(t0));
        if (!hit) return MoveSideRole.Unknown;
        var named = new Entity { Index = ei, Version = ev };
        if (!named.Exists(em)) return MoveSideRole.Unknown;

        var owner = named;
        if (named.TryRead(em, out InventoryConnection conn) && conn.InventoryOwner != Entity.Null && conn.InventoryOwner.Exists(em))
            owner = conn.InventoryOwner;
        if (owner == character || named == character) return MoveSideRole.OwnPlayer;
        if (owner.Has<PlayerCharacter>(em)) return MoveSideRole.OtherPlayer;

        int instances = UnityInventoryBackend.InstanceCount(em, owner);
        if (named != owner && named.Has<InventoryBuffer>(em))
        {
            // The event named one of the owner's inventory entities: observe just that one.
            for (int k = 0; k < instances; k++)
            {
                if (UnityInventoryBackend.TryInventoryEntity(em, owner, k, out var inv, out _) && inv == named)
                {
                    AddInventory(named, InventoryRef.For(owner, k), into);
                    return MoveSideRole.Inventory;
                }
            }

            AddInventory(named, InventoryRef.For(named, 0), into);
            return MoveSideRole.Inventory;
        }

        for (int k = 0; k < instances; k++)
            if (UnityInventoryBackend.TryInventoryEntity(em, owner, k, out var inv, out _))
                AddInventory(inv, InventoryRef.For(owner, k), into);
        return into.Count > 0 ? MoveSideRole.Inventory : MoveSideRole.Unknown;
    }

    private static void AddInventory(Entity inv, InventoryRef r, List<int> into)
    {
        int at = Batch.AddInventory(Key(inv), r);
        if (at >= 0 && !into.Contains(at)) into.Add(at);
    }

    /// <summary>True when the words are the acting character's own id or one of its inventory entities' ids.</summary>
    internal static bool IsOwnPlayer(EntityManager em, Entity character, int index, int generation)
    {
        if (character == Entity.Null || !character.Exists(em)) return false;
        if (Same(em, character, index, generation)) return true;
        for (int k = 0; k < Progenitor.Items.Items.MaxInventoryInstances; k++)
        {
            if (!UnityInventoryBackend.TryInventoryEntity(em, character, k, out var bag, out _)) break;
            if (bag != character && Same(em, bag, index, generation)) return true;
        }

        return false;
    }

    private static bool Same(EntityManager em, Entity e, int index, int generation) =>
        ServerNetworkIdSource.Read(em, e, out int i, out int g, out int t)
        && t == NetworkIdWords.TypeNormal && i == index && g == generation;

    // ---- rows -----------------------------------------------------------------------------------

    /// <summary>The slots of the inventory entity <paramref name="key"/> into <see cref="Rows"/>, read-only and raw.</summary>
    private static bool ReadRows(EntityManager em, long key) => ReadRowsInto(em, key, Rows);

    /// <summary>
    /// The slots of the inventory entity <paramref name="key"/> into <paramref name="into"/> (cleared
    /// first), read-only and raw at the layout constants. Also used by the bag-drop observer.
    /// </summary>
    internal static bool ReadRowsInto(EntityManager em, long key, List<MoveSlotRow> into)
    {
        into.Clear();
        var e = new Entity { Index = (int)(key >> 32), Version = (int)key };
        if (!e.Exists(em) || !e.Has<InventoryBuffer>(em)) return false;
        var type = ComponentIndex.IndexOf<InventoryBuffer>();
        int length = em.GetBufferLength(e, type);
        if (length < 0 || length > MaxRows) return false;
        if (length == 0) return true;
        byte* raw = (byte*)em.GetBufferRawRO(e, type);
        if (raw == null) return false;
        for (int i = 0; i < length; i++)
        {
            byte* row = raw + (long)i * Layout.InventoryBufferStride;
            int item = *(int*)(row + Layout.InventoryBufferItemTypeOffset);
            int amount = *(int*)(row + Layout.InventoryBufferAmountOffset);
            byte* ent = row + Layout.InventoryBufferItemEntityOffset + Layout.NetworkedEntityEntityOffset;
            int index = *(int*)ent, version = *(int*)(ent + 4);
            long itemEntity = index == 0 && version == 0 ? 0 : ((long)index << 32) | (uint)version;
            into.Add(new MoveSlotRow(item, amount, itemEntity));
        }

        return true;
    }

    private static double Micros(long t0) =>
        (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;

    private static long Key(Entity e) => ((long)e.Index << 32) | (uint)e.Version;
}

/// <summary>
/// What the Dev bridge's <c>inventory_moves</c> op read: the observer's state and counters and the
/// most recent player moves.
/// </summary>
public sealed class PlayerInventoryMoveReport
{
    internal PlayerInventoryMoveReport() { }

    /// <summary>Whether the observer is installed (server, Core loaded).</summary>
    public bool Installed { get; internal set; }

    /// <summary>Whether the system hooks are in place (something listens).</summary>
    public bool Hooked { get; internal set; }

    /// <summary>Plugin subscriptions (<c>ctx.Inventory.OnPlayerMove</c>).</summary>
    public int Subscribers { get; internal set; }

    /// <summary>Whether the bridge recorder listens.</summary>
    public bool Recording { get; internal set; }

    /// <summary>System updates with at least one request observed.</summary>
    public long Updates { get; internal set; }

    /// <summary>Requests read.</summary>
    public long Requests { get; internal set; }

    /// <summary>Requests past the per-update cap (64), not observed.</summary>
    public long Truncated { get; internal set; }

    /// <summary>Requests that named only player inventories (a bag sort), not reported.</summary>
    public long PlayerOnly { get; internal set; }

    /// <summary>Request sides whose network id did not resolve.</summary>
    public long Unresolved { get; internal set; }

    /// <summary>Requests after which none of their containers had changed (refused by the game, or nothing to move).</summary>
    public long Unchanged { get; internal set; }

    /// <summary>Observed inventories whose rows could not be read before or after.</summary>
    public long Unread { get; internal set; }

    /// <summary>Before hooks whose After never came (the system threw or was skipped).</summary>
    public long LostAfter { get; internal set; }

    /// <summary>Moves raised since Core loaded.</summary>
    public long Raised { get; internal set; }

    /// <summary>Moves dropped by the delivery queue cap.</summary>
    public long Dropped { get; internal set; }

    /// <summary>Subscriber calls that threw.</summary>
    public long HandlerErrors { get; internal set; }

    /// <summary>Network-id map rebuilds since Core loaded (primes included).</summary>
    public int Rebuilds { get; internal set; }

    /// <summary>Rebuilds done from the frame tick, once per world load while the hooks are in place.</summary>
    public int Primes { get; internal set; }

    /// <summary>The slowest rebuild a lookup miss ran inside the game's update (inside the hook), microseconds.</summary>
    public double HookRebuildMaxMicroseconds { get; internal set; }

    /// <summary>How long the last prime took, microseconds.</summary>
    public double PrimeMicroseconds { get; internal set; }

    /// <summary>The last hook failure, or "".</summary>
    public string LastError { get; internal set; } = "";

    /// <summary>The most recent moves, oldest first.</summary>
    public IReadOnlyList<PlayerInventoryMove> Moves { get; internal set; } = Array.Empty<PlayerInventoryMove>();
}

/// <summary>
/// The Dev bridge's <c>inventory_moves</c> op over <c>ctx.Inventory.OnPlayerMove</c>: turn the
/// recorder on or off, clear and read back the last moves. Public only because Core grants
/// <c>Progenitor.Dev</c> no <c>InternalsVisibleTo</c>; not a plugin API. Server and main thread
/// only: throws <see cref="InvalidOperationException"/> elsewhere.
/// </summary>
public static class PlayerInventoryMoveBridge
{
    /// <summary>
    /// Apply <paramref name="record"/> (null leaves it) and <paramref name="clear"/>, then report the
    /// last <paramref name="last"/> moves (at most 256).
    /// </summary>
    public static PlayerInventoryMoveReport Read(int last, bool? record, bool clear)
    {
        if (!Plugin.IsServerProcess || !PlayerMoveRuntime.Installed)
            throw new InvalidOperationException("inventory_moves runs on the server side only; inventories are server-authoritative.");
        if (!Dev.MainThread.IsMainThread)
            throw new InvalidOperationException("inventory_moves must run on the game main thread.");
        var hub = PlayerMoveRuntime.Hub;
        if (record.HasValue) hub.SetRecording(record.Value);
        if (clear) hub.ClearRecent();
        PlayerMoveRuntime.Tick();
        return new PlayerInventoryMoveReport
        {
            Installed = true,
            Hooked = PlayerMoveRuntime.HooksInstalled,
            Subscribers = hub.Subscribers,
            Recording = hub.Recording,
            Updates = PlayerMoveRuntime.Updates,
            Requests = PlayerMoveRuntime.Requests,
            Truncated = PlayerMoveRuntime.Truncated,
            PlayerOnly = PlayerMoveRuntime.PlayerOnly,
            Unresolved = PlayerMoveRuntime.Unresolved,
            Unchanged = PlayerMoveRuntime.Unchanged,
            Unread = PlayerMoveRuntime.Unread,
            LostAfter = PlayerMoveRuntime.LostAfter,
            Raised = hub.Raised,
            Dropped = hub.Dropped,
            HandlerErrors = hub.HandlerErrors,
            LastError = PlayerMoveRuntime.LastError,
            Rebuilds = PlayerMoveRuntime.ResolverRebuilds,
            Primes = PlayerMoveRuntime.ResolverPrimes,
            HookRebuildMaxMicroseconds = PlayerMoveRuntime.HookRebuildMaxUs,
            PrimeMicroseconds = PlayerMoveRuntime.PrimeLastUs,
            Moves = hub.Recent(Math.Max(0, Math.Min(last, PlayerMoveHub.RingSize))),
        };
    }
}
