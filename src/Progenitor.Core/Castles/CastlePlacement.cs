using System;
using System.Collections.Generic;

namespace Progenitor.Castles;

/// <summary>Why <see cref="CastlePlacement.Evaluate"/> refused, in the order it tests.</summary>
public enum CastleBlockReason
{
    /// <summary>Nothing this check knows about refuses the placement.</summary>
    None = 0,

    /// <summary>The handle is disabled (client side, module skipped, or no castle).</summary>
    NoCastle = 1,

    /// <summary>The position is not a representable tile (NaN / infinity / off the grid).</summary>
    BadPosition = 2,

    /// <summary>The footprint leaves the territory's tile AABB.</summary>
    OutsideTerritory = 3,

    /// <summary>Inside the AABB but on a tile the territory marks not buildable.</summary>
    OutsideBuildableTiles = 4,

    /// <summary>The height level is outside the territory's Min/Max band.</summary>
    HeightOutOfRange = 5,

    /// <summary>A placed tile-model's footprint covers one of the requested tiles.</summary>
    TileOccupied = 6,
}

/// <summary>
/// The read-only half of the game's placement validation.
///
/// <para><b>The game's own check is not callable read-only on this build.</b> Vanilla runs
/// <c>GetPlacementResult.GetResult(ref Operation, ref JobData)</c> (server metadata).
/// <c>JobData</c> is a 0x10E8-byte Burst struct holding
/// <c>TileModelSpatialLookup</c>, <c>TileWorld</c>, <c>MapZoneCollection</c>,
/// <c>ContestCollection</c>, <c>CastleRebuildRegistry</c>, <c>PrefabLookupMap</c> and roughly
/// thirty <c>ComponentLookup&lt;T&gt;</c> fields, and it is only ever built by
/// <c>GetPlacementResult.FullSystemData.Create(ref SystemState)</c> / <c>SystemData</c>
/// from inside the placement system. Every route to it is an interop
/// trap at once: by-ref struct parameters on an IL2CPP method (trap 1), generic
/// instantiations over game value types that may not exist (trap 2), and native calls with a
/// guessed convention (trap 5). So this module does <b>not</b> call it.</para>
///
/// <para>What is reproduced here is the part a read can be honest about: the territory
/// containment and height band that <c>CheckCastleTerritoryZone</c> tests and the tile
/// occupancy that <c>CheckPlacementDatasPerWorldTile</c> tests — the latter against the
/// plot's own placed tile-models, not against
/// the game's spatial lookup. <see cref="CastlePlacementCheck.Complete"/> is therefore always
/// false and <see cref="CastlePlacementCheck.NotChecked"/> names what is missing.</para>
/// </summary>
public static class CastlePlacement
{
    /// <summary>
    /// Vanilla placement rules this module does not reproduce. Listed on every result so a
    /// consumer cannot mistake <see cref="CastlePlacementCheck.Allowed"/> for the game's answer.
    /// Each entry names the <c>GetPlacementResult</c> step it stands for.
    /// </summary>
    public static IReadOnlyList<string> NotChecked { get; } = new[]
    {
        "placement type requirements and restrictions (CheckPlacementRequirements, floor/wall/roof kinds a model needs under it)",
        "map zone restrictions (CheckMapZoneRestrictions)",
        "restriction areas and roads (CheckRestrictionAreas, CastleAreaRequirement.BlockPlacementOnRoads)",
        "contest and arena areas (CheckContestAreaRestrictions, IsBlockedByContestAreas)",
        "castle rebuild restrictions and placement history (CheckCastleRebuildRestrictions, CheckPlacementHistory)",
        "castle teleporter rules (CheckCastleTeleporters)",
        "siege state (CheckSiege)",
        "terrain and static collision from TileWorld (CheckStaticPlacement)",
        "attach / replace / detach resolution and the dependency tree (CancelPlacementsBasedOnDependencyTree)",
        "team and territory-claim rules for other players' plots (CanBuildWithTerritoryRequirement_*)",
        "build cost and inventory",
    };

    /// <summary>
    /// Evaluate a footprint against one territory. Pure: the caller supplies the tile mask
    /// (<c>CastleTerritoryTiles</c>, row-major over the territory AABB, or null when it was
    /// not readable) and the placed buildings to test occupancy against.
    /// </summary>
    /// <param name="territory">Territory to place in.</param>
    /// <param name="buildableTiles">
    /// Per-tile <c>InsideBuildableTerritory</c> flags in <see cref="CastleGeometry.LocalTileIndex"/>
    /// order, or null when the buffer was not read. Null means "AABB only".
    /// </param>
    /// <param name="buildings">Placed models whose footprints occupy tiles. Null is treated as empty.</param>
    /// <param name="minX">Footprint min X, inclusive.</param>
    /// <param name="minY">Footprint min Y, inclusive.</param>
    /// <param name="maxX">Footprint max X, exclusive.</param>
    /// <param name="maxY">Footprint max Y, exclusive.</param>
    /// <param name="heightLevel">Height level to test, or -1 for "unknown, do not test".</param>
    /// <param name="prefabGuid">Prefab the caller asked about; echoed on the result.</param>
    /// <param name="footprintResolved">False when the footprint fell back to a single tile.</param>
    public static CastlePlacementCheck Evaluate(
        CastleTerritoryInfo territory,
        IReadOnlyList<bool>? buildableTiles,
        IReadOnlyList<CastleBuildingInfo>? buildings,
        int minX, int minY, int maxX, int maxY,
        int heightLevel,
        int prefabGuid,
        bool footprintResolved)
    {
        if (territory == null) throw new ArgumentNullException(nameof(territory));

        var result = new CastlePlacementCheck
        {
            PrefabGuid = prefabGuid,
            TerritoryIndex = territory.Index,
            FootprintMinX = minX,
            FootprintMinY = minY,
            FootprintMaxX = maxX,
            FootprintMaxY = maxY,
            HeightLevel = heightLevel,
            FootprintResolved = footprintResolved,
            BuildableTilesRead = buildableTiles != null,
        };

        if (maxX <= minX || maxY <= minY)
            return result.With(CastleBlockReason.BadPosition, "footprint is empty");

        // 1. Territory AABB. CheckCastleTerritoryZone refuses a model whose world bounds leave
        //    the territory unless CastleAreaRequirement.AllowTilesStickingOutOfTerritory is set;
        //    that per-prefab flag is one of the things NotChecked names.
        bool insideBounds = true;
        for (int y = minY; y < maxY && insideBounds; y++)
        {
            for (int x = minX; x < maxX; x++)
            {
                if (CastleGeometry.BoundsContains(x, y, territory.MinX, territory.MinY, territory.MaxX, territory.MaxY))
                    continue;
                insideBounds = false;
                break;
            }
        }

        result = result with { InsideTerritory = insideBounds };
        if (!insideBounds)
            return result.With(CastleBlockReason.OutsideTerritory, "footprint leaves territory " + territory.Index);

        // 2. Buildable tiles, when the CastleTerritoryTiles buffer was readable. A territory
        //    AABB is a box; the claimable shape inside it is not.
        if (buildableTiles != null)
        {
            for (int y = minY; y < maxY; y++)
            {
                for (int x = minX; x < maxX; x++)
                {
                    int index = CastleGeometry.LocalTileIndex(x, y, territory.MinX, territory.MinY, territory.MaxX, territory.MaxY);
                    if (index >= 0 && index < buildableTiles.Count && buildableTiles[index])
                        continue;
                    return (result with { InsideBuildableTiles = false })
                        .With(CastleBlockReason.OutsideBuildableTiles, "tile " + x + "," + y + " is not buildable territory");
                }
            }

            result = result with { InsideBuildableTiles = true };
        }

        // 3. Height band.
        if (!CastleGeometry.HeightInRange(heightLevel, territory.MinHeightLevel, territory.MaxHeightLevel))
        {
            return (result with { HeightInRange = false }).With(
                CastleBlockReason.HeightOutOfRange,
                "height level " + heightLevel + " is outside " + territory.MinHeightLevel + "-" + territory.MaxHeightLevel);
        }

        result = result with { HeightInRange = true };

        // 4. Occupancy against the plot's own placed models, keyed by (tile, height level).
        //    Same height level only: floors and the models standing on them share tiles across
        //    levels by design. `heightLevel` -1 means the level is unknown and the filter is
        //    skipped, which collapses every level onto one grid — a caller that does not know
        //    the level should walk the band rather than pass -1 (CastleHandle.CanPlace does,
        //    R30 finding 16); -1 remains available for a caller that genuinely wants
        //    "occupied at any level".
        if (buildings != null)
        {
            for (int i = 0; i < buildings.Count; i++)
            {
                var b = buildings[i];
                if (b == null) continue;
                if (heightLevel >= 0 && b.HeightLevel >= 0 && b.HeightLevel != heightLevel) continue;
                if (!CastleGeometry.BoundsOverlap(minX, minY, maxX, maxY,
                        b.FootprintMinX, b.FootprintMinY, b.FootprintMaxX, b.FootprintMaxY))
                    continue;

                return (result with { TileFree = false, BlockingEntity = b.Entity, BlockingPrefabName = b.Label })
                    .With(CastleBlockReason.TileOccupied, "occupied by " + b.Label + " " + b.Entity);
            }
        }

        return (result with { TileFree = true }).With(CastleBlockReason.None, "no refusal from the checks this module performs");
    }
}

/// <summary>
/// What <see cref="CastlePlacement.Evaluate"/> could determine. <see cref="Allowed"/> is a
/// partial verdict: true means "none of the checks this module performs refuses it", never
/// "the game would place it". <see cref="Complete"/> is always false — see
/// <see cref="CastlePlacement"/> for why the game's validator is not callable read-only.
/// </summary>
public sealed record CastlePlacementCheck
{
    /// <summary>Prefab GUID hash the caller asked about.</summary>
    public int PrefabGuid { get; init; }

    /// <summary>Territory the check ran against.</summary>
    public int TerritoryIndex { get; init; }

    /// <summary>Footprint min X, inclusive.</summary>
    public int FootprintMinX { get; init; }

    /// <summary>Footprint min Y, inclusive.</summary>
    public int FootprintMinY { get; init; }

    /// <summary>Footprint max X, exclusive.</summary>
    public int FootprintMaxX { get; init; }

    /// <summary>Footprint max Y, exclusive.</summary>
    public int FootprintMaxY { get; init; }

    /// <summary>Height level tested, or -1 when it was unknown and skipped.</summary>
    public int HeightLevel { get; init; } = -1;

    /// <summary>
    /// True when the caller did not name a height level and the territory spans several, so
    /// <see cref="CastleHandle"/> evaluated the band level by level and this result is one
    /// level's answer: <see cref="Allowed"/> means "allowed on <see cref="HeightLevel"/>", and
    /// a refusal means no level in the band was free (R30 finding 16). False on a single-level
    /// territory, where <see cref="HeightLevel"/> is the only level there is.
    /// </summary>
    public bool HeightAmbiguous { get; init; }

    /// <summary>
    /// False when the prefab's own <c>TileBounds</c> could not be read and the check fell back
    /// to a single tile. A multi-tile model then gets an optimistic answer.
    /// </summary>
    public bool FootprintResolved { get; init; }

    /// <summary>True when the <c>CastleTerritoryTiles</c> mask was available for this check.</summary>
    public bool BuildableTilesRead { get; init; }

    /// <summary>Every footprint tile is inside the territory AABB.</summary>
    public bool InsideTerritory { get; init; }

    /// <summary>
    /// Every footprint tile is marked buildable. Only meaningful when
    /// <see cref="BuildableTilesRead"/>; false-with-no-mask means "not tested".
    /// </summary>
    public bool InsideBuildableTiles { get; init; }

    /// <summary>Height level is inside the territory band (or was not tested).</summary>
    public bool HeightInRange { get; init; }

    /// <summary>No placed model on the plot overlaps the footprint at this height level.</summary>
    public bool TileFree { get; init; }

    /// <summary>The model that occupies the footprint, when <see cref="TileFree"/> is false.</summary>
    public CastleId BlockingEntity { get; init; }

    /// <summary>Prefab name (or GUID) of <see cref="BlockingEntity"/>.</summary>
    public string BlockingPrefabName { get; init; } = "";

    /// <summary>First refusal, in <see cref="CastlePlacement"/>'s test order.</summary>
    public CastleBlockReason Reason { get; init; } = CastleBlockReason.NoCastle;

    /// <summary>One-line explanation of <see cref="Reason"/>.</summary>
    public string Detail { get; init; } = "";

    /// <summary>
    /// Always false: this is the read-only subset, not the game's validator. Kept as a
    /// property so a consumer that starts trusting the answer has to read the name.
    /// </summary>
    public bool Complete => false;

    /// <summary>Vanilla rules that were not applied. Same list as <see cref="CastlePlacement.NotChecked"/>.</summary>
    public IReadOnlyList<string> NotChecked => CastlePlacement.NotChecked;

    /// <summary>
    /// True when no check this module performs refused the placement. A partial verdict:
    /// the game can still refuse for any reason in <see cref="NotChecked"/>.
    /// </summary>
    public bool Allowed => Reason == CastleBlockReason.None;

    /// <summary>Result for a disabled handle.</summary>
    public static CastlePlacementCheck Disabled { get; } = new()
    {
        Reason = CastleBlockReason.NoCastle,
        Detail = CastleHandle.NoCastleReason,
    };

    internal CastlePlacementCheck With(CastleBlockReason reason, string detail) =>
        this with { Reason = reason, Detail = detail ?? "" };

    /// <inheritdoc />
    public override string ToString() =>
        (Allowed ? "allowed (partial)" : "blocked: " + Reason) + " — " + Detail;
}
