using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.CastleBuilding;
using ProjectM.Network;
using ProjectM.Tiles;
using Progenitor.Ecs;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Castles;

/// <summary>
/// Server-side castle reads from the live ECS world. Queries go through
/// <see cref="Query.Entities"/> (disabled entities included). Components with a bool or
/// <c>NetworkedEntity</c> are read with <c>GetComponentData&lt;T&gt;</c> (the <c>User</c>
/// exemption); blittable ones use <see cref="EntityExtensions.TryRead{T}"/>.
/// <c>CastleTerritoryBlocks</c> is walked raw through the non-generic
/// <c>GetBufferLength</c> / <c>GetBufferRawRO</c> pair Effects already uses live — never
/// <c>GetBuffer&lt;T&gt;</c> (trap 2) — with the element layout taken from the dump
/// (<see cref="CastleTerritoryBuffers"/>). Each entity is isolated in try/catch; a missing
/// component never throws out of this type (one warning per component name).
/// </summary>
public static class CastleReader
{
    private static readonly object LogLock = new();
    private static readonly HashSet<string> LoggedOnce = new(StringComparer.Ordinal);

    /// <summary>
    /// Called from Core Install so SkipCoreModules can log a skip. Reads are stateless;
    /// skip is a documented no-op for the bisect list.
    /// </summary>
    internal static void Install()
    {
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.CastlesModule))
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(HarmonySlotHandoff.CastlesModule));
    }

    /// <summary>No process-lifetime holds that pin a collectible Core generation.</summary>
    internal static string DescribePins() =>
        HarmonySlotHandoff.FormatModulePins(HarmonySlotHandoff.CastlesModule);

    /// <summary>
    /// Every <c>CastleHeart</c> plus the territory it points at. Empty when
    /// <paramref name="world"/> is missing or the query fails. Never throws for a
    /// missing component.
    /// </summary>
    public static IReadOnlyList<CastlePlot> Plots(World world)
    {
        var empty = (IReadOnlyList<CastlePlot>)Array.Empty<CastlePlot>();
        if (world == null || !world.IsCreated)
            return empty;

        try
        {
            var em = world.EntityManager;
            var hearts = Query.Entities(em, includeDisabled: true, includeSystems: false,
                ComponentIndex.Of<CastleHeart>(ComponentType.AccessMode.ReadOnly));
            var plots = new List<CastlePlot>(hearts.Count);
            for (int i = 0; i < hearts.Count; i++)
            {
                try
                {
                    var plot = ReadPlot(em, hearts[i]);
                    if (plot != null)
                        plots.Add(plot);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning($"CastleReader.Plots: heart {Fmt(hearts[i])} threw: {e.Message}");
                }
            }

            plots.Sort((a, b) => a.TerritoryIndex.CompareTo(b.TerritoryIndex));
            return plots;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"CastleReader.Plots failed: {e}");
            return empty;
        }
    }

    /// <summary>
    /// Floor tiles on <paramref name="plot"/>, row-major (<c>Tile.y</c>, then <c>Tile.x</c>,
    /// then height). Floors whose <c>CastleHeartConnection</c> matches the plot heart are
    /// kept; floors without that component are kept only when they sit inside
    /// <paramref name="plot"/>'s tile AABB. Empty cells are omitted.
    /// </summary>
    public static IReadOnlyList<CastleTile> Tiles(World world, CastlePlot plot)
    {
        var empty = (IReadOnlyList<CastleTile>)Array.Empty<CastleTile>();
        if (world == null || !world.IsCreated || plot == null)
            return empty;

        try
        {
            var em = world.EntityManager;
            var floors = Query.Entities(em, includeDisabled: true, includeSystems: false,
                ComponentIndex.Of<CastleFloor>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<TilePosition>(ComponentType.AccessMode.ReadOnly));
            var tiles = new List<CastleTile>(Math.Min(floors.Count, 512));
            for (int i = 0; i < floors.Count; i++)
            {
                try
                {
                    var tile = ReadTile(em, floors[i], plot);
                    if (tile != null)
                        tiles.Add(tile);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning($"CastleReader.Tiles: floor {Fmt(floors[i])} threw: {e.Message}");
                }
            }

            tiles.Sort((a, b) => CastleLogic.CompareTiles(a.Y, a.X, a.Height, b.Y, b.X, b.Height));
            return tiles;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"CastleReader.Tiles failed: {e}");
            return empty;
        }
    }

    /// <summary>
    /// Placed tile-models whose <c>CastleHeartConnection</c> matches <paramref name="plot"/>'s
    /// heart, sorted by <c>NetworkId.Normal_Index</c> then <c>Normal_Generation</c>.
    /// <c>CastleHeartHelpers.GetConnectedTileModels</c> is not used: it returns
    /// <c>NativeList&lt;Entity&gt;</c>, which is not live-verified on this interop.
    /// </summary>
    public static IReadOnlyList<CastleBuilding> Buildings(World world, CastlePlot plot)
    {
        var empty = (IReadOnlyList<CastleBuilding>)Array.Empty<CastleBuilding>();
        if (world == null || !world.IsCreated || plot == null)
            return empty;

        try
        {
            var em = world.EntityManager;
            var models = Query.Entities(em, includeDisabled: true, includeSystems: false,
                ComponentIndex.Of<TileModel>(ComponentType.AccessMode.ReadOnly),
                ComponentIndex.Of<CastleHeartConnection>(ComponentType.AccessMode.ReadOnly));
            var buildings = new List<CastleBuilding>(Math.Min(models.Count, 512));
            for (int i = 0; i < models.Count; i++)
            {
                try
                {
                    var building = ReadBuilding(world, em, models[i], plot.HeartEntity);
                    if (building != null)
                        buildings.Add(building);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning($"CastleReader.Buildings: {Fmt(models[i])} threw: {e.Message}");
                }
            }

            buildings.Sort((a, b) => CastleLogic.CompareNetworkIds(
                a.NetworkIndex, a.NetworkGeneration, b.NetworkIndex, b.NetworkGeneration));
            return buildings;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"CastleReader.Buildings failed: {e}");
            return empty;
        }
    }

    /// <summary>
    /// Plot that owns <paramref name="heartOrTerritory"/>: a heart entity, a territory entity,
    /// or any tile-model with <c>CastleHeartConnection</c>. Null when the entity is none of
    /// those or the matching heart was not readable.
    /// </summary>
    public static CastlePlot? PlotOf(World world, Entity heartOrTerritory)
    {
        if (world == null || !world.IsCreated)
            return null;

        try
        {
            var em = world.EntityManager;
            if (!heartOrTerritory.Exists(em))
                return null;

            Entity heart = Entity.Null;
            if (heartOrTerritory.Has<CastleHeart>(em))
            {
                heart = heartOrTerritory;
            }
            else if (heartOrTerritory.Has<CastleTerritory>(em))
            {
                // GetComponentData<CastleTerritory>: bool IsGlobalDebugTerritory → not C# unmanaged.
                if (TryGetManaged(em, heartOrTerritory, "CastleTerritory", out CastleTerritory territory))
                    heart = territory.CastleHeart;
            }
            else if (heartOrTerritory.Has<CastleHeartConnection>(em))
            {
                // GetComponentData<CastleHeartConnection>: NetworkedEntity (has a bool).
                if (TryGetManaged(em, heartOrTerritory, "CastleHeartConnection", out CastleHeartConnection conn))
                    heart = ServerEntity(conn.CastleHeartEntity);
            }

            if (heart == Entity.Null || !heart.Exists(em) || !heart.Has<CastleHeart>(em))
                return null;

            return ReadPlot(em, heart);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"CastleReader.PlotOf({Fmt(heartOrTerritory)}) failed: {e}");
            return null;
        }
    }

    // ---- plot --------------------------------------------------------------------------------

    private static CastlePlot? ReadPlot(EntityManager em, Entity heartEntity)
    {
        if (!heartEntity.Exists(em) || !heartEntity.Has<CastleHeart>(em))
        {
            LogMissingOnce("CastleHeart");
            return null;
        }

        // GetComponentData<CastleHeart>: NetworkedEntity LastUserOwner → not C# unmanaged.
        if (!TryGetManaged(em, heartEntity, "CastleHeart", out CastleHeart heart))
            return null;

        var territoryEntity = heart.CastleTerritoryEntity;
        int territoryIndex = 0;
        int minX = 0, minY = 0, maxX = 0, maxY = 0;
        byte minH = 0, maxH = 0;
        bool decaying = false;
        int blockCount = 0;
        bool blockCountRead = false;
        IReadOnlyList<CastleBlockCoord> blocks = Array.Empty<CastleBlockCoord>();

        if (territoryEntity != Entity.Null && territoryEntity.Exists(em) && territoryEntity.Has<CastleTerritory>(em))
        {
            // GetComponentData<CastleTerritory>: bool IsGlobalDebugTerritory → not C# unmanaged.
            if (TryGetManaged(em, territoryEntity, "CastleTerritory", out CastleTerritory territory))
            {
                territoryIndex = territory.CastleTerritoryIndex;
                var bounds = territory.WorldBounds;
                minX = bounds.Min.x;
                minY = bounds.Min.y;
                maxX = bounds.Max.x;
                maxY = bounds.Max.y;
                minH = territory.MinHeightLevel;
                maxH = territory.MaxHeightLevel;
            }

            if (territoryEntity.Has<CastleTerritoryDecay>(em))
            {
                // GetComponentData<CastleTerritoryDecay>: bool Decaying → not C# unmanaged.
                if (TryGetManaged(em, territoryEntity, "CastleTerritoryDecay", out CastleTerritoryDecay decay))
                    decaying = decay.Decaying;
            }

            if (territoryEntity.Has<CastleTerritoryBlocks>(em))
            {
                // The bound is the territory's own WorldBounds, read above — never the buffer
                // length being checked (R30 finding 7). Zero bounds means no bound, so no walk.
                int maxBlocks = CastleTerritoryBuffers.MaxBlocksForBounds(minX, minY, maxX, maxY);
                blockCountRead = TryReadTerritoryBlocks(em, territoryEntity, maxBlocks, out blockCount, out blocks);
            }
        }
        else
        {
            LogMissingOnce("CastleTerritory");
        }

        if (!decaying)
            decaying = TryHeartIsDecaying(heart);

        var ownerEntity = Entity.Null;
        if (heartEntity.Has<UserOwner>(em))
        {
            // GetComponentData<UserOwner>: NetworkedEntity Owner → not C# unmanaged.
            if (TryGetManaged(em, heartEntity, "UserOwner", out UserOwner owner))
                ownerEntity = ServerEntity(owner.Owner);
        }

        if ((ownerEntity == Entity.Null || !ownerEntity.Exists(em)) && heart.LastUserOwner._Entity != Entity.Null)
            ownerEntity = ServerEntity(heart.LastUserOwner);

        ulong platformId = 0;
        string ownerName = "";
        var clanEntity = Entity.Null;
        string clanName = "";
        if (ownerEntity != Entity.Null && ownerEntity.Exists(em) && ownerEntity.Has<User>(em))
        {
            // GetComponentData<User>: bools + NetworkedEntity → not C# unmanaged.
            if (TryGetManaged(em, ownerEntity, "User", out User user))
            {
                platformId = user.PlatformId;
                ownerName = user.CharacterName.ToString();
                clanEntity = ServerEntity(user.ClanEntity);
            }
        }

        if (clanEntity != Entity.Null && clanEntity.Exists(em) && clanEntity.Has<ClanTeam>(em))
        {
            // GetComponentData<ClanTeam>: Il2CppSystem.Guid at offset 128; not a bool/NetworkedEntity
            // struct, but also not a licensed unmanaged Read. Isolated try/catch.
            if (TryGetManaged(em, clanEntity, "ClanTeam", out ClanTeam clan))
                clanName = clan.Name.ToString();
        }

        return new CastlePlot
        {
            TerritoryIndex = territoryIndex,
            TerritoryEntity = territoryEntity,
            HeartEntity = heartEntity,
            BoundsMinX = minX,
            BoundsMinY = minY,
            BoundsMaxX = maxX,
            BoundsMaxY = maxY,
            MinHeightLevel = minH,
            MaxHeightLevel = maxH,
            BlockCoordinates = blocks,
            BlocksSkipped = !blockCountRead,
            BlockCount = blockCount,
            BlockCountRead = blockCountRead,
            FloorCount = heart.FloorCount,
            OwnerUserEntity = ownerEntity,
            OwnerPlatformId = platformId,
            OwnerName = ownerName ?? "",
            ClanEntity = clanEntity,
            ClanName = clanName ?? "",
            Level = heart.Level,
            Raided = heart.ActiveEvent == CastleHeartEvent.Raided || TryHeartIsRaided(heart),
            Decaying = decaying,
        };
    }

    // ---- tiles -------------------------------------------------------------------------------

    private static CastleTile? ReadTile(EntityManager em, Entity floorEntity, CastlePlot plot)
    {
        if (!floorEntity.TryRead(em, out TilePosition pos))
        {
            LogMissingOnce("TilePosition");
            return null;
        }

        int x = pos.Tile.x;
        int y = pos.Tile.y;
        int height = ReadHeight(in pos);

        bool connectedToHeart = false;
        if (floorEntity.Has<CastleHeartConnection>(em))
        {
            // GetComponentData<CastleHeartConnection>: NetworkedEntity.
            if (TryGetManaged(em, floorEntity, "CastleHeartConnection", out CastleHeartConnection conn))
                connectedToHeart = ServerEntity(conn.CastleHeartEntity) == plot.HeartEntity;
        }

        if (!connectedToHeart)
        {
            if (floorEntity.Has<CastleHeartConnection>(em))
                return null;
            if (!CastleLogic.TileInBounds(x, y, plot.BoundsMinX, plot.BoundsMinY, plot.BoundsMaxX, plot.BoundsMaxY))
                return null;
        }

        byte floorType = 0;
        bool impassable = false;
        Entity wallN = Entity.Null, wallE = Entity.Null, wallS = Entity.Null, wallW = Entity.Null;
        if (floorEntity.Has<CastleFloor>(em))
        {
            // GetComponentData<CastleFloor>: bool Impassable → not C# unmanaged.
            if (TryGetManaged(em, floorEntity, "CastleFloor", out CastleFloor floor))
            {
                floorType = (byte)floor.FloorType;
                impassable = floor.Impassable;
                wallN = floor.WallNorth;
                wallE = floor.WallEast;
                wallS = floor.WallSouth;
                wallW = floor.WallWest;
            }
        }
        else
        {
            LogMissingOnce("CastleFloor");
        }

        var roomEntity = Entity.Null;
        if (floorEntity.Has<CastleRoomConnection>(em))
        {
            // GetComponentData<CastleRoomConnection>: NetworkedEntity.
            if (TryGetManaged(em, floorEntity, "CastleRoomConnection", out CastleRoomConnection room))
                roomEntity = ServerEntity(room.RoomEntity);
        }

        bool wallNorth = IsLiveEntity(em, wallN);
        bool wallEast = IsLiveEntity(em, wallE);
        bool wallSouth = IsLiveEntity(em, wallS);
        bool wallWest = IsLiveEntity(em, wallW);

        return new CastleTile
        {
            FloorEntity = floorEntity,
            X = x,
            Y = y,
            Height = height,
            FloorPresent = true,
            FloorType = floorType,
            FloorTypeName = CastleLogic.FloorTypeName(floorType),
            Impassable = impassable,
            RoomEntity = roomEntity,
            RoomId = roomEntity == Entity.Null ? 0 : roomEntity.Index,
            WallFlags = CastleLogic.PackCardinals(wallNorth, wallEast, wallSouth, wallWest),
            DoorFlags = CastleLogic.PackCardinals(
                IsDoor(em, wallN),
                IsDoor(em, wallE),
                IsDoor(em, wallS),
                IsDoor(em, wallW)),
        };
    }

    // ---- buildings ---------------------------------------------------------------------------

    private static CastleBuilding? ReadBuilding(World world, EntityManager em, Entity entity, Entity heartEntity)
    {
        if (!entity.Has<CastleHeartConnection>(em))
        {
            LogMissingOnce("CastleHeartConnection");
            return null;
        }

        // GetComponentData<CastleHeartConnection>: NetworkedEntity.
        if (!TryGetManaged(em, entity, "CastleHeartConnection", out CastleHeartConnection conn))
            return null;
        if (ServerEntity(conn.CastleHeartEntity) != heartEntity)
            return null;

        var guid = default(PrefabGUID);
        if (!entity.TryRead(em, out guid))
            LogMissingOnce("PrefabGUID");

        string prefabName = "";
        if (guid._Value != 0)
            PrefabLookup.TryGetName(world, guid, out prefabName);

        int ox = 0, oy = 0, oh = 0;
        byte rotation = 0;
        if (entity.TryRead(em, out TilePosition pos))
        {
            ox = pos.Tile.x;
            oy = pos.Tile.y;
            oh = ReadHeight(in pos);
            rotation = (byte)pos.TileRotation;
        }
        else
        {
            LogMissingOnce("TilePosition");
        }

        int fMinX = ox, fMinY = oy, fMaxX = ox + 1, fMaxY = oy + 1;
        if (entity.TryRead(em, out TileBounds bounds))
        {
            fMinX = bounds.Value.Min.x;
            fMinY = bounds.Value.Min.y;
            fMaxX = bounds.Value.Max.x;
            fMaxY = bounds.Value.Max.y;
        }
        else
        {
            LogMissingOnce("TileBounds");
        }

        int netIndex = int.MaxValue;
        int netGen = 0;
        if (entity.TryRead(em, out NetworkId net))
        {
            netIndex = net.Normal_Index;
            netGen = net.Normal_Generation;
        }
        else
        {
            LogMissingOnce("NetworkId");
        }

        bool isHeart = entity.Has<CastleHeart>(em);
        bool isContainer = entity.Has<InventoryOwner>(em);
        bool isStation = entity.Has<CastleWorkstation>(em) || entity.Has<Refinementstation>(em);
        var kind = CastleLogic.ClassifyKind(isHeart, isContainer, isStation);

        return new CastleBuilding
        {
            Entity = entity,
            PrefabGuid = guid,
            PrefabName = prefabName ?? "",
            OriginX = ox,
            OriginY = oy,
            OriginHeight = oh,
            Rotation = rotation,
            RotationName = CastleLogic.RotationName(rotation),
            FootprintMinX = fMinX,
            FootprintMinY = fMinY,
            FootprintMaxX = fMaxX,
            FootprintMaxY = fMaxY,
            Interactable = entity.Has<Interactable>(em),
            Kind = kind,
            NetworkIndex = netIndex,
            NetworkGeneration = netGen,
        };
    }

    // ---- access ------------------------------------------------------------------------------

    /// <summary>
    /// <c>GetComponentData&lt;T&gt;</c> for structs the research marks as not C# unmanaged
    /// (bool / NetworkedEntity) plus <c>ClanTeam</c>. Missing component or throw → false and
    /// one log per name; never bubbles.
    /// </summary>
    private static bool TryGetManaged<T>(EntityManager em, Entity entity, string name, out T value)
    {
        value = default!;
        try
        {
            if (!entity.Has<T>(em))
            {
                LogMissingOnce(name);
                return false;
            }

            value = em.GetComponentData<T>(entity);
            return true;
        }
        catch (Exception e)
        {
            LogMissingOnce(name, e);
            value = default!;
            return false;
        }
    }

    private static Entity ServerEntity(NetworkedEntity networked)
    {
        try
        {
            return networked._Entity;
        }
        catch (Exception e)
        {
            LogMissingOnce("NetworkedEntity._Entity", e);
            return Entity.Null;
        }
    }

    private static bool IsLiveEntity(EntityManager em, Entity entity)
    {
        try
        {
            return entity != Entity.Null && entity.Index != 0 && entity.Exists(em);
        }
        catch (Exception e)
        {
            LogMissingOnce("Entity.Exists", e);
            return entity != Entity.Null && entity.Index != 0;
        }
    }

    private static bool IsDoor(EntityManager em, Entity wall)
    {
        try
        {
            return IsLiveEntity(em, wall) && wall.Has<Door>(em);
        }
        catch (Exception e)
        {
            LogMissingOnce("Door", e);
            return false;
        }
    }

    private static int ReadHeight(in TilePosition pos)
    {
        try
        {
            return pos.HeightLevel;
        }
        catch (Exception e)
        {
            LogMissingOnce("TilePosition.HeightLevel", e);
            return pos.CompressedHeight;
        }
    }

    private static bool TryHeartIsRaided(in CastleHeart heart)
    {
        try
        {
            return heart.IsRaided();
        }
        catch (Exception e)
        {
            LogMissingOnce("CastleHeart.IsRaided", e);
            return false;
        }
    }

    private static bool TryHeartIsDecaying(in CastleHeart heart)
    {
        try
        {
            return heart.IsDecaying();
        }
        catch (Exception e)
        {
            LogMissingOnce("CastleHeart.IsDecaying", e);
            return false;
        }
    }

    /// <summary>
    /// Read <c>CastleTerritoryBlocks</c> through the non-generic
    /// <c>EntityManager.GetBufferLength(entity, typeIndex)</c> / <c>GetBufferRawRO(entity, typeIndex)</c>
    /// pair — the path <c>Effects</c> already uses live for <c>BuffSequence</c> /
    /// <c>BlackboardElement</c>. <c>GetBuffer&lt;T&gt;</c> is never called: that generic
    /// instantiation may not exist in this IL2CPP build and hard-crashes when it does not
    /// (interop trap 2).
    ///
    /// <para>Element layout is the game metadata's, never <c>sizeof</c> and never the interop's
    /// <c>TypeManager</c> (the EV17 rule): <c>struct CastleTerritoryBlocks { int2 BlockCoordinate; }</c>,
    /// from the server metadata — 8 bytes, x at 0x0 and y at 0x4.</para>
    ///
    /// <para><b>The length is bounded by something other than itself.</b>
    /// <paramref name="maxBlocks"/> is <see cref="CastleTerritoryBuffers.MaxBlocksForBounds"/>
    /// of the territory's own <c>WorldBounds</c> — 273 on the live plot, not the 65 536
    /// magnitude ceiling and not a number derived from <c>GetBufferLength</c>. The span below is
    /// sized from a length that already passed that bound, so a wrong <c>GetBufferLength</c>
    /// (the premise of R27 finding 17) cannot walk past the allocation; the old check compared
    /// the length against a span built from the same length and could only fire in a unit test
    /// (R30 finding 7). A territory whose bounds did not read gives <paramref name="maxBlocks"/>
    /// 0 and the buffer is not walked at all.</para>
    /// </summary>
    private static unsafe bool TryReadTerritoryBlocks(
        EntityManager em, Entity entity, int maxBlocks, out int count, out IReadOnlyList<CastleBlockCoord> blocks)
    {
        count = 0;
        blocks = Array.Empty<CastleBlockCoord>();

        try
        {
            var typeIndex = ComponentIndex.IndexOf<CastleTerritoryBlocks>();
            int length = em.GetBufferLength(entity, typeIndex);
            if (!CastleTerritoryBuffers.BlockLengthOk(length, maxBlocks))
            {
                LogMissingOnce("CastleTerritoryBlocks.Length",
                    message: CastleTerritoryBuffers.BlockLengthRefusedLog(Fmt(entity), length, maxBlocks));
                return false;
            }

            count = length;
            if (length == 0)
                return true;

            void* raw = em.GetBufferRawRO(entity, typeIndex);
            if (raw == null)
            {
                LogMissingOnce("CastleTerritoryBlocks.Raw",
                    message: $"CastleReader: CastleTerritoryBlocks on {Fmt(entity)} reported length {length} but " +
                             "GetBufferRawRO returned null; the count stands, the coordinates do not.");
                return true;
            }

            var span = new ReadOnlySpan<byte>(raw, length * CastleTerritoryBuffers.BlockElementStride);
            var coords = CastleTerritoryBuffers.DecodeBlockCoordinates(span, length, maxBlocks);
            if (coords == null)
                return true;

            var rows = new CastleBlockCoord[length];
            for (int i = 0; i < length; i++)
                rows[i] = new CastleBlockCoord(coords[i * 2], coords[(i * 2) + 1]);

            blocks = rows;
            return true;
        }
        catch (Exception e)
        {
            LogMissingOnce("CastleTerritoryBlocks", e);
            count = 0;
            blocks = Array.Empty<CastleBlockCoord>();
            return false;
        }
    }

    private static void LogMissingOnce(string componentName, Exception? e = null, string? message = null)
    {
        lock (LogLock)
        {
            if (!LoggedOnce.Add(componentName))
                return;
        }

        string text = message
            ?? (e == null
                ? $"CastleReader: {componentName} missing or unreadable; further misses of this component are silent."
                : $"CastleReader: {componentName} failed ({e.GetType().Name}: {e.Message}); further misses of this component are silent.");
        Plugin.Logger?.LogWarning(text);
    }

    private static string Fmt(Entity entity) => $"{entity.Index}:{entity.Version}";
}
