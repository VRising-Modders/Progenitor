using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Tiles;
using Progenitor.Castles;
using Progenitor.Ecs;
using Progenitor.Persistence;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Inventory;

/// <summary>
/// Process-lifetime state of the Inventory module on the server: the engine, the watch hub and
/// the container-id book, plus the one <c>Plugin.Frame</c> handler that runs the watches every
/// frame and the id book's upkeep every two seconds. Installed by Core; nothing here outlives
/// <see cref="Uninstall"/>.
/// </summary>
internal static class InventoryRuntime
{
    /// <summary>The mod-save store that holds the id book (<c>progenitor/progenitor.inventory.json</c>).</summary>
    internal const string StoreId = "progenitor.inventory";

    /// <summary>Key of the id book inside the store.</summary>
    internal const string BookKey = "containers";

    private const long UpkeepMs = 2_000;

    private static UnityInventoryBackend? _backend;
    private static InventoryEngine? _engine;
    private static InventoryWatchHub? _hub;
    private static ContainerIdRegistry? _book;
    private static NetworkIdResolver? _netIds;
    private static IModStore? _store;
    private static IDisposable? _ready;
    private static bool _loaded;
    private static bool _hooked;
    private static long _nextUpkeep;
    private static bool _deferLogged;
    private static int _emptyScans;
    private const int EmptyScansBeforeRetire = 30;

    internal static bool Installed => _engine != null;

    internal static InventoryEngine Engine =>
        _engine ?? throw new InvalidOperationException("ctx.Inventory runs on the server side only; this process has no inventory engine.");

    internal static InventoryWatchHub Hub =>
        _hub ?? throw new InvalidOperationException("ctx.Inventory runs on the server side only.");

    internal static InventoryStamps Stamps =>
        _stamps ?? throw new InvalidOperationException("ctx.Inventory runs on the server side only.");

    /// <summary>
    /// Process slot that carries the stamp clock across Core reloads, so a <c>since</c> a plugin took
    /// from the previous generation's <c>Clock</c> is never ahead of this one's (a plain string; nothing
    /// collectible is stored).
    /// </summary>
    private const string ClockProcessKey = "progenitor.inventory.stamp-clock";

    private static InventoryStamps? _stamps;

    internal static void Install()
    {
        if (!Plugin.IsServerProcess) return;
        _backend = new UnityInventoryBackend();
        _engine = new InventoryEngine(_backend, line => Plugin.Logger?.LogError(line));
        _stamps = new InventoryStamps(new UnityInventoryStampSource(), line => Plugin.Logger?.LogError(line));
        try { _stamps.TrySeedClock(AppDomain.CurrentDomain.GetData(ClockProcessKey) as string); }
        catch { /* no slot: the clock starts from the store's own value */ }
        _hub = new InventoryWatchHub(_backend, line => Plugin.Logger?.LogWarning(line), c => _stamps?.Stamp(c) ?? 0);
        _engine.Touched = c => _hub?.Poke(c);
        _book = new ContainerIdRegistry();
        _netIds = new NetworkIdResolver(new ServerNetworkIdSource());
        _loaded = false;
        _store = ModSave.Store(StoreId);
        _ready = _store.WhenReady(store =>
        {
            int dropped = _book!.Load(ForThisWorld(store.Get<ContainerIdDocument>(BookKey)));
            _loaded = true;
            Plugin.Logger?.LogInfo("Inventory: container ids loaded (" + _book.Count + " live, "
                + _book.RetiredCount + " retired" + (dropped > 0 ? ", " + dropped + " unreadable rows dropped" : "") + ")");
        });
        CastleContainerRows.IdResolver = ResolveForCastle;
        CastleContainerRows.HeartIdResolver = heart => IdOf(heart.ToEntity(), out _);
        Plugin.Frame += Tick;
        ModSave.BeforeSave += Flush;
        _hooked = true;
        Plugin.Logger?.LogInfo("Inventory: installed (server)");
    }

    internal static void Uninstall()
    {
        if (_hooked) Plugin.Frame -= Tick;
        ModSave.BeforeSave -= Flush;
        _hooked = false;
        CastleContainerRows.IdResolver = null;
        CastleContainerRows.HeartIdResolver = null;
        try { Flush(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Inventory: saving container ids on unload threw: " + e.Message); }
        _hub?.Clear();
        if (_stamps != null)
        {
            try { AppDomain.CurrentDomain.SetData(ClockProcessKey, _stamps.EncodeClock()); }
            catch { /* best effort: the next generation then starts from the store's value and old `since`s read as changed */ }
            _stamps.Clear();
            _stamps = null;
        }
        ChunkVersions.Reset();
        _ready?.Dispose();
        _ready = null;
        _store = null;
        _book = null;
        _netIds?.Clear();
        _netIds = null;
        _hub = null;
        _engine = null;
        _backend = null;
        _loaded = false;
        _deferLogged = false;
        _emptyScans = 0;
        _worldKey = _worldKeyFolder = null;
    }

    internal static string DescribePins() =>
        _engine == null ? "" : "inventory watches=" + (_hub?.Count ?? 0) + " ids=" + (_book?.Count ?? 0) + " stamps=" + (_stamps?.Entries ?? 0);

    private static void Tick()
    {
        try
        {
            long now = Environment.TickCount64;
            _stamps?.Tick();
            _hub?.Tick(now);
            if (_book is { Dirty: true }) Flush();
            if (now < _nextUpkeep) return;
            _nextUpkeep = now + UpkeepMs;
            Upkeep();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Inventory: frame tick threw: " + e.Message);
        }
    }

    /// <summary>Prime after a load, retire destroyed containers, write the book when it changed.</summary>
    private static void Upkeep()
    {
        var book = _book;
        if (book == null || !_loaded || book.Count == 0 && !book.Dirty) return;
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return;
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        if (!EnsurePrimed(world, epoch)) return;
        var em = world.EntityManager;
        int retired = book.Sweep(epoch, (i, v) => new Entity { Index = i, Version = v }.Exists(em),
            (recorded, i, v) => CurrentAnchor(em, new Entity { Index = i, Version = v }, recorded));
        if (retired > 0) Plugin.Logger?.LogInfo("Inventory: retired " + retired + " container id(s) whose container is gone");
        Flush();
    }

    private static string? _worldKeyFolder, _worldKey;

    /// <summary>
    /// This world's identity: a random id kept in <c>progenitor/inventory-world-id.txt</c> beside
    /// the autosaves, created on first use. A copied save folder copies it (and keeps its ids); a
    /// book written by another world carries another id. That matters because a store handed out
    /// before the save folder was known can merge a pending write from the previous world over the
    /// next world's file.
    /// </summary>
    private static string WorldKey()
    {
        string? dir = ModSave.CurrentSaveDirectory;
        if (string.IsNullOrEmpty(dir)) return "";
        if (_worldKey != null && _worldKeyFolder == dir) return _worldKey;
        try
        {
            string folder = System.IO.Path.Combine(dir, "progenitor");
            System.IO.Directory.CreateDirectory(folder);
            string path = System.IO.Path.Combine(folder, "inventory-world-id.txt");
            if (!System.IO.File.Exists(path))
            {
                try
                {
                    using var stream = new System.IO.FileStream(path, System.IO.FileMode.CreateNew, System.IO.FileAccess.Write, System.IO.FileShare.None);
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"));
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                catch (System.IO.IOException) { /* created concurrently: read it below */ }
            }

            string id = System.IO.File.ReadAllText(path).Trim();
            if (!Guid.TryParseExact(id, "N", out _)) return "";
            _worldKeyFolder = dir;
            _worldKey = id;
            return id;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Inventory: world identity file unreadable: " + e.Message);
            return "";
        }
    }

    /// <summary>The document, or null when it names another world (logged once per load).</summary>
    private static ContainerIdDocument? ForThisWorld(ContainerIdDocument? doc)
    {
        if (doc == null || string.IsNullOrEmpty(doc.World)) return doc;
        string key = WorldKey();
        if (key.Length == 0 || string.Equals(doc.World, key, StringComparison.OrdinalIgnoreCase)) return doc;
        Plugin.Logger?.LogWarning("Inventory: the container-id book in this save names another world; starting an empty book.");
        return null;
    }

    private static void Flush()
    {
        var book = _book;
        if (book == null || _store == null || !_loaded) return;
        if (!book.TakeDirty()) return;
        var doc = book.Save();
        doc.World = WorldKey();
        _store.Set(BookKey, doc);
    }

    /// <summary>
    /// True when the book is bound for <paramref name="epoch"/>. An empty book is marked bound
    /// without a scan; a non-empty one is primed (or deferred, see <see cref="TryPrime"/>).
    /// </summary>
    private static bool EnsurePrimed(World world, int epoch)
    {
        var book = _book!;
        if (book.PrimedEpoch == epoch) return true;
        // A new world load: re-read the book from this world's mod save, so a different save
        // loaded in the same process never sees another world's ids. The book is flushed on
        // every change, so nothing of the previous load is lost by this.
        if (book.PrimedEpoch != int.MinValue && _store != null)
            book.Load(ForThisWorld(_store.Get<ContainerIdDocument>(BookKey)));
        if (book.Count == 0)
        {
            book.Prime(epoch, Array.Empty<ContainerSighting>());
            return true;
        }

        return TryPrime(world, epoch);
    }

    /// <summary>
    /// Bind the saved ids to the containers standing on their anchors after a load. Deferred
    /// while the load has not finished or no castle model is visible yet, so a half-loaded world
    /// never retires the whole book.
    /// </summary>
    private static bool TryPrime(World world, int epoch)
    {
        var book = _book!;
        if (world.GetExistingSystemManaged<LoadPersistenceSystemV2>()?.Initialized != true) return false;
        var sightings = Scan(world);
        // After the load has finished, an empty world is believed once it stays empty for about a
        // minute of upkeep (every castle may really be gone); until then the book waits.
        if (sightings.Count == 0 && book.Count > 0 && ++_emptyScans < EmptyScansBeforeRetire)
        {
            if (!_deferLogged)
            {
                _deferLogged = true;
                Plugin.Logger?.LogInfo("Inventory: no placed containers visible yet; container ids wait for the castles to load.");
            }

            return false;
        }

        var (bound, retired) = book.Prime(epoch, sightings);
        _deferLogged = false;
        _emptyScans = 0;
        Plugin.Logger?.LogInfo("Inventory: container ids bound after load: " + bound + " bound, " + retired
            + " retired (no container on their tile), " + sightings.Count + " containers seen");
        return true;
    }

    /// <summary>Every placed castle model with an inventory, in one pass over the world.</summary>
    internal static List<ContainerSighting> Scan(World world)
    {
        var rows = new List<ContainerSighting>();
        var em = world.EntityManager;
        var territories = new Dictionary<Entity, int>();
        var seen = new HashSet<Entity>();
        foreach (var plot in CastleReader.Plots(world))
        {
            territories[plot.HeartEntity] = plot.TerritoryIndex;
            // The heart itself, whether or not it carries a connection to itself.
            if (seen.Add(plot.HeartEntity) && TryAnchor(em, plot.HeartEntity, plot.TerritoryIndex, out var heartAnchor))
                rows.Add(new ContainerSighting(heartAnchor, plot.HeartEntity.Index, plot.HeartEntity.Version));
        }

        var models = Query.Entities(em, includeDisabled: true, includeSystems: false,
            ComponentIndex.Of<TileModel>(ComponentType.AccessMode.ReadOnly),
            ComponentIndex.Of<CastleHeartConnection>(ComponentType.AccessMode.ReadOnly));
        for (int i = 0; i < models.Count; i++)
        {
            var e = models[i];
            try
            {
                // Hearts get ids too (their fuel inventory, and as the castle's own key).
                if (seen.Contains(e) || !UnityInventoryBackend.HasInventory(em, e) || e.Has<CastleHeart>(em)) continue;
                var conn = em.GetComponentData<CastleHeartConnection>(e);
                var heart = conn.CastleHeartEntity._Entity;
                if (!territories.TryGetValue(heart, out int territory)) continue;
                if (seen.Add(e) && TryAnchor(em, e, territory, out var anchor))
                    rows.Add(new ContainerSighting(anchor, e.Index, e.Version));
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogDebug("Inventory: container scan skipped " + e.Index + ":" + e.Version + ": " + ex.Message);
            }
        }

        return rows;
    }

    private static bool TryAnchor(EntityManager em, Entity e, int territory, out ContainerAnchor anchor)
    {
        anchor = default;
        if (!e.TryRead(em, out TilePosition pos) || !e.TryRead(em, out PrefabGUID guid)) return false;
        int height;
        try { height = pos.HeightLevel; }
        catch { height = pos.CompressedHeight; }
        anchor = new ContainerAnchor(territory, guid.GuidHash, pos.Tile.x, pos.Tile.y, height, (int)pos.TileRotation);
        return true;
    }

    // ---- ids --------------------------------------------------------------------------------

    private static ContainerId ResolveForCastle(CastleBuildingInfo b, int territory)
    {
        if (!Ready(out var world, out _)) return ContainerId.None;
        var anchor = new ContainerAnchor(territory, b.PrefabGuid, b.TileX, b.TileY, b.HeightLevel, b.Rotation);
        return Issue(world, new ContainerSighting(anchor, b.Entity.Index, b.Entity.Version));
    }

    internal static ContainerId IdOf(Entity entity, out string reason)
    {
        if (!Ready(out var world, out reason)) return ContainerId.None;
        var em = world.EntityManager;
        if (!entity.Exists(em))
        {
            reason = "the entity no longer exists";
            return ContainerId.None;
        }

        bool heart = entity.Has<CastleHeart>(em);
        if (!heart && !UnityInventoryBackend.HasInventory(em, entity))
        {
            reason = "the entity has no inventory (only placed containers, stations and hearts have ids)";
            return ContainerId.None;
        }

        // The anchor is read exactly as Scan reads it, so a restart rebinds the same key.
        var plot = CastleReader.PlotOf(world, entity);
        if (plot == null || (!heart && !entity.Has<TileModel>(em)))
        {
            reason = "not a placed castle model (player bags and servants are addressed through their owner)";
            return ContainerId.None;
        }

        if (!TryAnchor(em, entity, plot.TerritoryIndex, out var anchor))
        {
            reason = "the model's tile position or prefab could not be read";
            return ContainerId.None;
        }

        var id = Issue(world, new ContainerSighting(anchor, entity.Index, entity.Version));
        reason = id.IsNone ? "container ids are not bound yet after the load, or another live model holds this tile's id" : "";
        return id;
    }

    private static ContainerId Issue(World world, ContainerSighting s)
    {
        var book = _book!;
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        if (!EnsurePrimed(world, epoch)) return ContainerId.None;
        var em = world.EntityManager;
        // Written to the store at the end of this frame (one write however many ids were issued)
        // and before any world save (ModSave.BeforeSave), so a handed-out id is never Unknown
        // after the next restart.
        return book.IdFor(s, epoch, (i, v) => new Entity { Index = i, Version = v }.Exists(em));
    }

    /// <summary>Where a bound model stands now (territory and prefab kept from the record).</summary>
    private static ContainerAnchor? CurrentAnchor(EntityManager em, Entity e, ContainerAnchor recorded)
    {
        try
        {
            if (!e.TryRead(em, out TilePosition pos)) return null;
            int height;
            try { height = pos.HeightLevel; }
            catch { height = pos.CompressedHeight; }
            return new ContainerAnchor(recorded.TerritoryIndex, recorded.PrefabGuid, pos.Tile.x, pos.Tile.y, height, (int)pos.TileRotation);
        }
        catch
        {
            return null;
        }
    }

    internal static ContainerIdStatus Status(ContainerId id)
    {
        if (_book == null || !_loaded) return ContainerIdStatus.Unknown;
        return _book.Status(id, Progenitor.WorldState.WorldEpoch.Current);
    }

    internal static bool TryFind(ContainerId id, out Entity owner, out string reason)
    {
        owner = Entity.Null;
        if (!Ready(out var world, out reason)) return false;
        var book = _book!;
        int epoch = Progenitor.WorldState.WorldEpoch.Current;
        if (!EnsurePrimed(world, epoch))
        {
            reason = "container ids are not bound yet after the world load; try again in a moment";
            return false;
        }

        var em = world.EntityManager;
        book.Validate(id, epoch, (i, v) => new Entity { Index = i, Version = v }.Exists(em));
        switch (book.Status(id, epoch))
        {
            case ContainerIdStatus.Live:
                book.TryEntity(id, epoch, out int index, out int version, out _);
                owner = new Entity { Index = index, Version = version };
                reason = "";
                return true;
            case ContainerIdStatus.Gone:
                reason = "the container with id " + id + " was destroyed";
                return false;
            case ContainerIdStatus.Unbound:
                reason = "id " + id + " has not been matched to a container since the world loaded";
                return false;
            default:
                reason = "id " + id + " was never issued in this world";
                return false;
        }
    }

    // ---- network ids --------------------------------------------------------------------------

    /// <summary>
    /// A client-reported <c>NetworkId</c> (index, generation, type) to the server entity and its
    /// stable id. Every word is untrusted: malformed, unknown, stale and non-container ids give
    /// false with a reason. Never throws.
    /// </summary>
    internal static bool TryResolveNetworkId(int index, int generation, int type, out ContainerId id, out Entity owner, out string reason)
    {
        id = ContainerId.None;
        owner = Entity.Null;
        try
        {
            if (!NetworkIdWords.Validate(index, generation, type, out reason)) return false;
            if (!Ready(out _, out reason) || _netIds == null) return false;
            if (!_netIds.TryResolve(index, generation, type, Progenitor.WorldState.WorldEpoch.Current, Environment.TickCount64,
                    out int ei, out int ev, out reason))
                return false;
            var entity = new Entity { Index = ei, Version = ev };
            var found = IdOf(entity, out string why);
            if (found.IsNone)
            {
                // No server entity id in the reason: a mod may relay it to the player.
                reason = "network id " + NetworkIdWords.Format(index, generation) + " names something with no container id: " + why;
                return false;
            }

            id = found;
            owner = entity;
            reason = "";
            return true;
        }
        catch (Exception e)
        {
            id = ContainerId.None;
            owner = Entity.Null;
            reason = "the network id lookup failed: " + e.Message;
            return false;
        }
    }

    /// <summary>The live container's <c>NetworkId</c> as the three words a client looks it up by.</summary>
    internal static bool TryGetNetworkId(ContainerId id, out int index, out int generation, out int type, out string reason)
    {
        index = generation = type = 0;
        if (!TryFind(id, out var owner, out reason)) return false;
        var em = Worlds.Server!.EntityManager;
        if (!ServerNetworkIdSource.Read(em, owner, out int i, out int g, out int t))
        {
            reason = "container " + id + " has no network id yet";
            return false;
        }

        if (t != NetworkIdWords.TypeNormal)
        {
            reason = "container " + id + " has a network id of type " + t + ", not a normal one a client can look up";
            return false;
        }

        index = i;
        generation = g;
        type = t;
        reason = "";
        return true;
    }

    private static bool Ready(out World world, out string reason)
    {
        world = Worlds.Server!;
        if (_book == null)
        {
            reason = "container ids run on the server side only";
            return false;
        }

        if (!_loaded)
        {
            reason = "the world's mod save is not loaded yet; container ids are not available";
            return false;
        }

        if (world == null || !world.IsCreated)
        {
            reason = "no server world";
            return false;
        }

        reason = "";
        return true;
    }
}
