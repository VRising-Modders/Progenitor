using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using ProjectM.Tiles;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;
using Unity.Transforms;

namespace Progenitor.Castles;

/// <summary>
/// The live server implementation of <see cref="ICastleRuntime"/>: the only file in this module
/// that touches Unity or ProjectM types, so everything else compiles into the test assembly.
/// Plot / tile / building reads delegate to <see cref="CastleReader"/> (which already isolates
/// every entity in try/catch and never throws for a missing component) and are mapped into the
/// game-free info types.
///
/// <para>The one read that is new here is <c>CastleTerritoryTiles</c>, and it deliberately does
/// not use <c>GetBuffer&lt;T&gt;</c>: that generic instantiation may not exist in this IL2CPP
/// build and hard-crashes when it does not (interop trap 2). It goes through the non-generic
/// <c>GetBufferRawRO</c> + <c>GetBufferLength</c> pair that <c>Effects</c> already uses live for
/// <c>BuffSequence</c> and <c>BlackboardElement</c>, and the result is only believed when its
/// element count equals the territory's own <c>WorldBounds</c> tile count — a length the caller
/// computes independently, so a garbage read falls back to the AABB instead of answering with
/// noise.</para>
/// </summary>
internal sealed unsafe class UnityCastleRuntime : ICastleRuntime
{
    private readonly object _gate = new();

    /// <summary>
    /// Footprints learned from placed instances: prefab GUID hash to local bounds around the
    /// pivot tile. Prefab entities carry an all-zero <c>TileBounds</c> (verified live on
    /// <c>TM_Castle_Floor_Outdoor_Plain01</c>, entity 45571:2) because the local bounds live in
    /// the <c>TileData.Data</c> blob, which this module does not read. So the footprint of a
    /// prefab is learned the only honest way available to a read: from a model of that prefab
    /// already standing in the world.
    /// </summary>
    private readonly Dictionary<int, (int MinX, int MinY, int MaxX, int MaxY)> _footprints = new();

    /// <summary>(territory, reason) keys already logged; see <see cref="LogTerritoryOnce"/>.</summary>
    private readonly HashSet<string> _loggedTerritories = new(StringComparer.Ordinal);

    public bool IsServer
    {
        get
        {
            try
            {
                var world = Worlds.Server;
                return world != null && world.IsCreated;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Castles: server world probe threw: " + e.Message);
                return false;
            }
        }
    }

    /// <summary>
    /// <c>EntityManager.Exists</c> on the exact <c>index:version</c> pair, through the same
    /// <c>Entity.Exists(em)</c> extension every <see cref="CastleReader"/> read already uses.
    /// False (the return, not <paramref name="live"/>) when there is no server world or the
    /// read threw — the caller fails closed on that.
    ///
    /// <para>This replaces the <c>World.SequenceNumber</c> stamp (R31 finding 2): a V Rising
    /// save reload runs <i>inside</i> the live server world, so the sequence number cannot
    /// move across it, while the entity versions it re-assigns can be observed one entity at a
    /// time. See <see cref="ICastleRuntime.TryIsEntityLive"/> for the dump lines.</para>
    /// </summary>
    public bool TryIsEntityLive(CastleId entity, out bool live)
    {
        live = false;
        try
        {
            var world = Worlds.Server;
            if (world == null || !world.IsCreated) return false;
            live = entity.ToEntity().Exists(world.EntityManager);
            return true;
        }
        catch (Exception e)
        {
            if (!_loggedLivenessFailure)
            {
                _loggedLivenessFailure = true;
                Plugin.Logger?.LogWarning(
                    "Castles: Entity.Exists(" + entity + ") threw: " + e.Message
                    + ". Retained handles fail closed (they report stale) while this signal is "
                    + "unreadable; Castles.ForTerritory still works. Logged once.");
            }

            return false;
        }
    }

    /// <summary>One-shot guard for the <see cref="TryIsEntityLive"/> warning.</summary>
    private bool _loggedLivenessFailure;

    public IReadOnlyList<CastleSnapshot> Snapshot()
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return CastleEmpty.Snapshots;

        var plots = CastleReader.Plots(world);
        if (plots.Count == 0) return CastleEmpty.Snapshots;

        var rows = new List<CastleSnapshot>(plots.Count);
        for (int i = 0; i < plots.Count; i++)
            rows.Add(ToSnapshot(plots[i]));
        return rows;
    }

    public IReadOnlyList<CastleBuildingInfo> Buildings(CastleId heart)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated || heart.IsNone) return CastleEmpty.Buildings;

        var plot = CastleReader.PlotOf(world, heart.ToEntity());
        if (plot == null) return CastleEmpty.Buildings;

        var buildings = CastleReader.Buildings(world, plot);
        var rows = new List<CastleBuildingInfo>(buildings.Count);
        for (int i = 0; i < buildings.Count; i++)
        {
            var info = ToInfo(buildings[i]);
            rows.Add(info);
            RememberFootprint(info);
        }

        return rows;
    }

    public IReadOnlyList<CastleTileInfo> Tiles(CastleId heart)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated || heart.IsNone) return Array.Empty<CastleTileInfo>();

        var plot = CastleReader.PlotOf(world, heart.ToEntity());
        if (plot == null) return Array.Empty<CastleTileInfo>();

        var tiles = CastleReader.Tiles(world, plot);
        var rows = new List<CastleTileInfo>(tiles.Count);
        for (int i = 0; i < tiles.Count; i++)
        {
            var t = tiles[i];
            rows.Add(new CastleTileInfo
            {
                Entity = CastleId.From(t.FloorEntity),
                X = t.X,
                Y = t.Y,
                HeightLevel = t.Height,
                FloorType = t.FloorType,
                FloorTypeName = t.FloorTypeName,
                Impassable = t.Impassable,
                RoomId = t.RoomId,
                WallFlags = t.WallFlags,
                DoorFlags = t.DoorFlags,
            });
        }

        return rows;
    }

    public bool TryGetBuildableTiles(CastleId territory, int expectedTileCount, out bool[] tiles)
    {
        tiles = Array.Empty<bool>();
        if (territory.IsNone || expectedTileCount <= 0) return false;

        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return false;

        try
        {
            var em = world.EntityManager;
            var entity = territory.ToEntity();
            if (!entity.Has<CastleTerritoryTiles>(em))
                return false;

            var typeIndex = ComponentIndex.IndexOf<CastleTerritoryTiles>();
            int length = em.GetBufferLength(entity, typeIndex);
            if (!CastleTerritoryBuffers.TileMaskLengthOk(length, expectedTileCount))
            {
                // Info, not a warning: a territory whose buffer never passes this would otherwise
                // log on every `.castle` call, and the degraded answer (the AABB) is a documented
                // mode. Once per territory.
                LogTerritoryOnce(territory, "tiles-length",
                    CastleTerritoryBuffers.TileMaskFallbackLog(territory.ToString(), length, expectedTileCount));
                return false;
            }

            void* raw = em.GetBufferRawRO(entity, typeIndex);
            if (raw == null)
            {
                LogTerritoryOnce(territory, "tiles-null",
                    "Castles: CastleTerritoryTiles on " + territory + " reported length " + length +
                    " but GetBufferRawRO returned null; IsInside / CanPlace answer from the territory AABB here.");
                return false;
            }

            // One bool per tile, stride 1, row-major over WorldBounds — the layout is the server
            // metadata's, never sizeof and never TypeManager (the EV17 rule), and
            // the decode itself lives in the game-free CastleTerritoryBuffers so it is tested.
            var span = new ReadOnlySpan<byte>(raw, length * CastleTerritoryBuffers.TileElementStride);
            var mask = CastleTerritoryBuffers.DecodeTileMask(span, length, expectedTileCount);
            if (mask == null) return false;

            int buildable = 0;
            for (int i = 0; i < mask.Length; i++)
                if (mask[i]) buildable++;

            LogTerritoryOnce(territory, "tiles-read",
                CastleTerritoryBuffers.TileMaskReadLog(territory.ToString(), length, buildable));

            tiles = mask;
            return true;
        }
        catch (Exception e)
        {
            LogTerritoryOnce(territory, "tiles-threw",
                "Castles: CastleTerritoryTiles read on " + territory + " threw: " + e.Message +
                "; IsInside / CanPlace answer from the territory AABB here.");
            return false;
        }
    }

    /// <summary>
    /// One line per (territory, reason). The tile mask is fetched once per handle and a handle is
    /// built per <c>.castle</c> call, so an unguarded log here is a line per call.
    /// </summary>
    private void LogTerritoryOnce(CastleId territory, string reason, string message)
    {
        string key = territory.ToString() + "/" + reason;
        lock (_gate)
        {
            // Past the cap the set stops growing and the module stops logging, rather than
            // holding a key per territory of a world that has thousands.
            if (_loggedTerritories.Count >= 256) return;
            if (!_loggedTerritories.Add(key)) return;
        }

        Plugin.Logger?.LogInfo(message);
    }

    public bool TryGetTilePosition(CastleId entity, out int tileX, out int tileY, out int heightLevel)
    {
        tileX = 0;
        tileY = 0;
        heightLevel = -1;

        var world = Worlds.Server;
        if (world == null || !world.IsCreated || entity.IsNone) return false;

        try
        {
            var em = world.EntityManager;
            if (!entity.ToEntity().TryRead(em, out TilePosition pos))
                return false;

            tileX = pos.Tile.x;
            tileY = pos.Tile.y;
            try { heightLevel = pos.HeightLevel; }
            catch { heightLevel = -1; }
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Castles: TilePosition read on " + entity + " threw: " + e.Message);
            return false;
        }
    }

    public bool IsTileModel(CastleId entity)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated || entity.IsNone) return false;
        try { return entity.ToEntity().Has<ProjectM.Tiles.TileModel>(world.EntityManager); }
        catch { return false; }
    }

    public bool TryGetPosition(CastleId entity, out CastleVec3 position)
    {
        position = default;

        var world = Worlds.Server;
        if (world == null || !world.IsCreated || entity.IsNone) return false;

        try
        {
            var em = world.EntityManager;
            var e = entity.ToEntity();
            if (e.TryRead(em, out Translation translation))
            {
                position = translation.Value;
                return true;
            }

            if (e.TryRead(em, out LocalToWorld ltw))
            {
                position = ltw.Position;
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Plugin.Logger?.LogWarning("Castles: position read on " + entity + " threw: " + ex.Message);
            return false;
        }
    }

    public bool TryGetPrefabFootprint(int prefabGuid, out int localMinX, out int localMinY, out int localMaxX, out int localMaxY)
    {
        lock (_gate)
        {
            if (_footprints.TryGetValue(prefabGuid, out var box))
            {
                localMinX = box.MinX;
                localMinY = box.MinY;
                localMaxX = box.MaxX;
                localMaxY = box.MaxY;
                return true;
            }
        }

        localMinX = 0;
        localMinY = 0;
        localMaxX = 1;
        localMaxY = 1;
        return false;
    }

    public bool TryGetClan(CastleId user, out CastleId clan)
    {
        clan = CastleId.None;

        var world = Worlds.Server;
        if (world == null || !world.IsCreated || user.IsNone) return false;

        try
        {
            var em = world.EntityManager;
            var entity = user.ToEntity();
            if (!entity.Has<User>(em)) return false;

            // GetComponentData<User>: bools + NetworkedEntity, so not C# unmanaged (trap 9).
            var row = em.GetComponentData<User>(entity);
            var clanEntity = row.ClanEntity._Entity;
            if (clanEntity == Entity.Null) return false;

            clan = CastleId.From(clanEntity);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Castles: clan read on " + user + " threw: " + e.Message);
            return false;
        }
    }

    // ---- inventory membership ------------------------------------------------------------------
    // Containers()/Stations() used to list every castle building on the server for each call. The
    // index keeps which inventory-bearing models belong to which heart and rescans only when the
    // structure stamp below moves (see CastleInventoryIndex). Rows are still read live.

    private CastleInventoryIndex? _inventoryIndex;

    private CastleInventoryIndex InventoryIndex =>
        _inventoryIndex ??= CastleInventoryIndex.Live = new CastleInventoryIndex(InventoryStamp, ScanInventoryModels);

    private static ComponentType[] InventoryModelTypes(bool instances) => new[]
    {
        ComponentIndex.Of<TileModel>(ComponentType.AccessMode.ReadOnly),
        ComponentIndex.Of<CastleHeartConnection>(ComponentType.AccessMode.ReadOnly),
        instances
            ? ComponentIndex.Of<InventoryInstanceElement>(ComponentType.AccessMode.ReadOnly)
            : ComponentIndex.Of<InventoryBuffer>(ComponentType.AccessMode.ReadOnly),
    };

    /// <summary>
    /// The stamp of the inventory-bearing castle models: the entity count of each query, which moves
    /// only when such a model is created or destroyed (a sum of chunk counts, no entity list). Not the
    /// queries' component order versions: those move on any structural change to any entity carrying
    /// one of the types, characters and inventories included, which is nearly every frame. Null when
    /// unreadable, which makes the index rescan.
    /// </summary>
    private static long? InventoryStamp()
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return null;
        var em = world.EntityManager;
        long stamp = 0;
        foreach (bool instances in new[] { false, true })
        {
            var query = Query.Build(em, includeDisabled: true, includeSystems: false, InventoryModelTypes(instances));
            try { stamp = (stamp << 32) | (uint)query.CalculateEntityCount(); }
            finally { query.Dispose(); }
        }
        return stamp;
    }

    /// <summary>
    /// Every placed model with an inventory and a heart connection (a few hundred server-wide, not
    /// every castle building), with its heart through the guarded raw connection reader. Hearts are
    /// left out, as the inventory rows leave them out.
    /// </summary>
    private static IEnumerable<(CastleId Model, CastleId Heart)> ScanInventoryModels()
    {
        var result = new List<(CastleId, CastleId)>();
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return result;
        var em = world.EntityManager;
        var seen = new HashSet<CastleId>();
        foreach (bool instances in new[] { false, true })
        {
            foreach (var e in Query.Entities(em, includeDisabled: true, includeSystems: false, InventoryModelTypes(instances)))
            {
                try
                {
                    var id = CastleId.From(e);
                    if (!seen.Add(id) || e.Has<CastleHeart>(em)) continue;
                    var heart = Servants.ServantRuntime.HeartOf(em, e);
                    if (heart != Entity.Null) result.Add((id, CastleId.From(heart)));
                }
                catch { /* unreadable entity: not listed */ }
            }
        }
        return result;
    }

    public long StructureRevision(CastleId heart)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated || heart.IsNone) return 0;
        return InventoryIndex.RevisionOf(heart, Progenitor.WorldState.WorldEpoch.Current);
    }

    public IReadOnlyList<CastleInventoryRow> Inventories(CastleId heart)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated || heart.IsNone) return Array.Empty<CastleInventoryRow>();
        var em = world.EntityManager;
        var heartEntity = heart.ToEntity();
        if (!heartEntity.Exists(em) || !heartEntity.Has<CastleHeart>(em)) return Array.Empty<CastleInventoryRow>();

        // The index only narrows the candidates; each is still read and its heart connection
        // checked exactly as CastleReader.Buildings does, then sorted the same way.
        var models = InventoryIndex.Of(heart, Progenitor.WorldState.WorldEpoch.Current);
        var buildings = new List<CastleBuilding>(models.Count);
        for (int i = 0; i < models.Count; i++)
        {
            var building = CastleReader.ConnectedBuilding(world, models[i].ToEntity(), heartEntity);
            if (building != null) buildings.Add(building);
        }
        buildings.Sort((a, b) => CastleLogic.CompareNetworkIds(
            a.NetworkIndex, a.NetworkGeneration, b.NetworkIndex, b.NetworkGeneration));
        var rows = new List<CastleInventoryRow>();
        for (int i = 0; i < buildings.Count; i++)
        {
            var b = buildings[i];
            var e = b.Entity;
            try
            {
                if (e.Has<CastleHeart>(em) || !Inventory.UnityInventoryBackend.HasInventory(em, e)) continue;
                bool refinement = e.Has<Refinementstation>(em);
                // Ordinary storage also carries CastleWorkstation for room/servant
                // bonuses. The recipe buffer distinguishes a crafting workstation.
                bool station = refinement || e.Has<WorkstationRecipesBuffer>(em);
                CastleNameplates.TryRead(em, e, out bool nameable, out string name);
                rows.Add(new CastleInventoryRow
                {
                    Building = ToInfo(b),
                    IsStation = station,
                    IsRefinement = refinement,
                    SlotCount = Inventory.UnityInventoryBackend.SlotCount(em, e, 0),
                    Nameable = nameable,
                    Nameplate = name,
                });
            }
            catch (Exception ex)
            {
                Plugin.Logger?.LogWarning("Castles: inventory row for " + e.Index + ":" + e.Version + " threw: " + ex.Message);
            }
        }

        return rows;
    }

    public string HeartName(CastleId heart)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated || heart.IsNone) return "";
        return CastleNameplates.TryRead(world.EntityManager, heart.ToEntity(), out _, out string name) ? name : "";
    }

    public CastleHeartFuel HeartFuel(CastleId heart)
    {
        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return CastleHeartFuel.Fail("no server world");
        try
        {
            var em = world.EntityManager;
            byte* p = Ai.AiRuntime.RO<CastleHeart>(em, heart.ToEntity(), Layout.CastleHeartDataOffset + sizeof(long));
            if (p == null) return CastleHeartFuel.Fail("the entity is not a live castle heart");
            int level = p[Layout.CastleHeartLevelOffset];
            int fuel = *(int*)(p + Layout.CastleHeartFuelQuantityOffset);
            byte* blob = Ai.AiRuntime.BlobData(p + Layout.CastleHeartDataOffset);
            int length = Ai.AiRuntime.BlobLength(blob);
            if (blob == null || length <= 0)
                return new CastleHeartFuel { Level = level, FuelQuantity = fuel, Reason = "the heart's level table is not readable" };
            if (!CastleHeartLevels.TryItemSlots(new ReadOnlySpan<byte>(blob, length), level, out int slots, out int rows))
                return new CastleHeartFuel { Level = level, FuelQuantity = fuel, LevelRows = rows, Reason = "no row for level " + level + " in the heart's level table" };
            return new CastleHeartFuel { Level = level, FuelQuantity = fuel, UnlockedSlots = slots, LevelRows = rows };
        }
        catch (Exception e)
        {
            return CastleHeartFuel.Fail("heart read threw: " + e.Message);
        }
    }

    private static CastleSnapshot ToSnapshot(CastlePlot plot) => new()
    {
        Heart = CastleId.From(plot.HeartEntity),
        Territory = new CastleTerritoryInfo
        {
            Index = plot.TerritoryIndex,
            Entity = CastleId.From(plot.TerritoryEntity),
            MinX = plot.BoundsMinX,
            MinY = plot.BoundsMinY,
            MaxX = plot.BoundsMaxX,
            MaxY = plot.BoundsMaxY,
            MinHeightLevel = plot.MinHeightLevel,
            MaxHeightLevel = plot.MaxHeightLevel,
            BlockCount = plot.BlockCount,
            BlocksRead = plot.BlockCountRead,
        },
        Owner = new CastleOwnerInfo
        {
            User = CastleId.From(plot.OwnerUserEntity),
            PlatformId = plot.OwnerPlatformId,
            Name = plot.OwnerName,
            Clan = CastleId.From(plot.ClanEntity),
            ClanName = plot.ClanName,
        },
        Level = plot.Level,
        FloorCount = plot.FloorCount,
        Raided = plot.Raided,
        Decaying = plot.Decaying,
    };

    private static CastleBuildingInfo ToInfo(CastleBuilding b) => new()
    {
        Entity = CastleId.From(b.Entity),
        PrefabGuid = b.PrefabGuid._Value,
        PrefabName = b.PrefabName,
        TileX = b.OriginX,
        TileY = b.OriginY,
        HeightLevel = b.OriginHeight,
        Rotation = b.Rotation,
        RotationName = b.RotationName,
        FootprintMinX = b.FootprintMinX,
        FootprintMinY = b.FootprintMinY,
        FootprintMaxX = b.FootprintMaxX,
        FootprintMaxY = b.FootprintMaxY,
        Interactable = b.Interactable,
        Kind = CastleLogic.KindName(b.Kind),
        NetworkIndex = b.NetworkIndex,
        NetworkGeneration = b.NetworkGeneration,
    };

    /// <summary>
    /// Learn a prefab's local footprint from a placed instance: world bounds minus the pivot
    /// tile, undoing the model's rotation. Only <c>TileRotation.None</c> instances are recorded,
    /// because the inverse rotation is inferred rather than live-verified
    /// (<see cref="CastleGeometry.LocalToWorldBounds"/>) and a wrong sample would be cached.
    /// </summary>
    private void RememberFootprint(CastleBuildingInfo info)
    {
        if (info.PrefabGuid == 0 || info.Rotation != 0) return;
        if (info.FootprintMaxX <= info.FootprintMinX || info.FootprintMaxY <= info.FootprintMinY) return;

        int minX = info.FootprintMinX - info.TileX;
        int minY = info.FootprintMinY - info.TileY;
        int maxX = info.FootprintMaxX - info.TileX;
        int maxY = info.FootprintMaxY - info.TileY;

        lock (_gate)
        {
            if (_footprints.Count >= 4096) return;
            _footprints[info.PrefabGuid] = (minX, minY, maxX, maxY);
        }
    }
}
