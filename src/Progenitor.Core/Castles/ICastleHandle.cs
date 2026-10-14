using System.Collections.Generic;

namespace Progenitor.Castles;

/// <summary>
/// One castle, as a mod sees it. Read-only, so there is nothing to dispose and unload has
/// nothing to reverse — unlike <c>IUnitHandle</c> / <c>IEffectHandle</c>, a castle handle is
/// not <c>IDisposable</c> and is never tracked on a <c>PluginContext</c>.
///
/// <para>Identity fields (<see cref="Heart"/>, <see cref="Territory"/>, <see cref="Owner"/>,
/// <see cref="Level"/>, …) are the snapshot taken when the handle was created. The reads that
/// walk the world (<see cref="Buildings"/>, <see cref="Tiles"/>, <see cref="IsInside(CastleId)"/>)
/// go to the live world each call; <see cref="Refresh"/> re-takes the snapshot.</para>
///
/// <para><b>Do not retain a handle across a save reload.</b> The snapshot holds raw entity ids
/// and a reload re-assigns them, so a handle is scoped to the world generation it was taken in.
/// It notices: after a reload <see cref="IsStale"/> is true, <see cref="Enabled"/> is false and
/// <see cref="DisabledReason"/> explains. What to persist instead is
/// <c>Territory.Index</c> (and <c>NetworkId</c> for individual models) — see
/// <see cref="IsStale"/>.</para>
///
/// <para>On the client every handle is disabled: <see cref="Enabled"/> is false, the lists are
/// empty, <see cref="IsInside(CastleVec3)"/> is false and <see cref="CanPlace"/> returns
/// <see cref="CastlePlacementCheck.Disabled"/>. One Debug line is logged the first time, the
/// same shape Effects and Units use, so shared plugin code needs no side check.</para>
/// </summary>
public interface ICastleHandle
{
    /// <summary>
    /// False on the client, when the Castles module is skipped, when this handle stands for
    /// "no castle" (for example <c>Castles.ForUser</c> on a player who has not placed a heart),
    /// and when the handle has gone <see cref="IsStale"/>. <see cref="DisabledReason"/> says
    /// which.
    /// </summary>
    bool Enabled { get; }

    /// <summary>
    /// True when this handle was taken in an earlier world generation — in practice, before a
    /// save reload. The snapshot holds raw entity ids and a reload re-assigns them, so a
    /// retained handle would otherwise answer from whatever entity now holds its old index.
    /// A stale handle reads as disabled instead: the lists are empty,
    /// <see cref="IsInside(CastleVec3)"/> is false and <see cref="CanPlace"/> returns
    /// <see cref="CastlePlacementCheck.Disabled"/>.
    ///
    /// <para><b>Handles are session-scoped.</b> Keep the <c>Territory.Index</c> if you need to
    /// find this castle again — that is the key the game keeps stable across a reload — and
    /// call <see cref="Refresh"/> or <c>Castles.ForTerritory</c> to get a live handle.</para>
    /// </summary>
    bool IsStale { get; }

    /// <summary>
    /// Why <see cref="Enabled"/> is false — "no castle" or the stale-handle line, which names
    /// both world stamps and the territory index to re-resolve with. Empty while enabled.
    /// </summary>
    string DisabledReason { get; }

    /// <summary>Castle heart entity. <see cref="CastleId.None"/> on a disabled handle.</summary>
    CastleId Heart { get; }

    /// <summary>Territory index, bounds, height band and block count.</summary>
    CastleTerritoryInfo Territory { get; }

    /// <summary>Owning user and clan.</summary>
    CastleOwnerInfo Owner { get; }

    /// <summary><c>CastleHeart.Level</c>.</summary>
    byte Level { get; }

    /// <summary><c>CastleHeart.FloorCount</c>.</summary>
    int FloorCount { get; }

    /// <summary>True while the heart's <c>ActiveEvent</c> is <c>Raided</c>.</summary>
    bool Raided { get; }

    /// <summary>True while <c>CastleTerritoryDecay.Decaying</c> is set on the territory.</summary>
    bool Decaying { get; }

    /// <summary>The snapshot behind the identity fields.</summary>
    CastleSnapshot Snapshot { get; }

    /// <summary>
    /// Every placed tile-model attached to this heart, sorted by <c>NetworkId.Normal_Index</c>
    /// then <c>Normal_Generation</c>. Live read; empty on a disabled handle.
    /// </summary>
    IReadOnlyList<CastleBuildingInfo> Buildings();

    /// <summary>
    /// Count of placed models by prefab name, highest count first then name. Built from
    /// <see cref="Buildings"/>; the shape the sample <c>.castle</c> command prints.
    /// </summary>
    IReadOnlyList<KeyValuePair<string, int>> BuildingsByPrefab();

    /// <summary>
    /// Floor cells on this plot, row-major. Pass-through to <c>CastleReader.Tiles</c>;
    /// empty on a disabled handle or in the test runtime.
    /// </summary>
    IReadOnlyList<CastleTileInfo> Tiles();

    /// <summary>
    /// True when a world position falls inside this castle's territory. X/Z only: there is no
    /// verified world-Y to height-level mapping (<see cref="CastleGeometry"/>). Uses the
    /// territory's <c>CastleTerritoryTiles</c> mask when it is readable, otherwise the
    /// <c>WorldBounds</c> AABB — <see cref="IsTilePrecise"/> says which.
    /// </summary>
    bool IsInside(CastleVec3 position);

    /// <summary>
    /// True when an entity is inside this castle's territory. Uses the entity's own
    /// <c>TilePosition</c> when it has one (which also tests the height band), otherwise its
    /// <c>Translation</c> through <see cref="IsInside(CastleVec3)"/>.
    /// </summary>
    bool IsInside(CastleId entity);

    /// <summary>
    /// True when the territory's per-tile buildable mask was readable, so
    /// <see cref="IsInside(CastleVec3)"/> and <see cref="CanPlace"/> test the claimed shape
    /// rather than its bounding box.
    /// </summary>
    bool IsTilePrecise { get; }

    /// <summary>
    /// The read-only subset of the game's placement validation for one prefab at one position.
    /// Not the game's answer: see <see cref="CastlePlacement"/> and
    /// <see cref="CastlePlacementCheck.NotChecked"/>.
    /// </summary>
    CastlePlacementCheck CanPlace(int prefabGuid, CastleVec3 position, byte rotation = 0);

    /// <summary>
    /// The heart's player-set name (<c>NameableInteractable.Name</c> on the heart); empty when
    /// unnamed, unreadable, or on a disabled handle. Live read.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The heart's stable id (the same scheme as container ids: survives restarts, never
    /// reused). Pass it to <c>ctx.Inventory.TryFind</c> to get the heart back after a restart.
    /// <see cref="Progenitor.Inventory.ContainerId.None"/> on a disabled handle or while the
    /// Inventory module is not running.
    /// </summary>
    Progenitor.Inventory.ContainerId HeartId { get; }

    /// <summary>
    /// The heart's level, the number of item slots (fuel pads) the game's level table unlocks at
    /// that level, and its fuel quantity. Live read; a failed read says why.
    /// </summary>
    CastleHeartFuel HeartFuel();

    /// <summary>
    /// Every placed model with an inventory that is not a station or the heart: chests and
    /// other storage. Each row carries the stable <c>ContainerId</c>, the nameplate and the
    /// <c>InventoryRef</c> to pass to <c>ctx.Inventory</c>. Sorted like <see cref="Buildings"/>.
    /// Live read; empty on a disabled handle.
    /// </summary>
    IReadOnlyList<CastleContainerInfo> Containers();

    /// <summary>
    /// Every crafting and refinement station (a recipe list, <c>WorkstationRecipesBuffer</c>, or
    /// <c>Refinementstation</c>), with stable ids. Storage that carries <c>CastleWorkstation</c> only
    /// for room and servant bonuses is in <see cref="Containers"/>. A refinement station's row carries both
    /// <see cref="CastleContainerInfo.Main"/> (input) and <see cref="CastleContainerInfo.Output"/>.
    /// Live read; empty on a disabled handle.
    /// </summary>
    IReadOnlyList<CastleContainerInfo> Stations();

    /// <summary>
    /// Re-read this castle from the world through its <c>Territory.Index</c> — the key that
    /// survives a save reload. Returns a new handle, or a disabled one when the territory no
    /// longer holds a castle. The original handle keeps its old snapshot.
    /// </summary>
    ICastleHandle Refresh();
}

/// <summary>
/// Game-free view of one floor cell, the shape of <see cref="CastleTile"/> without Unity types.
/// </summary>
public sealed class CastleTileInfo
{
    /// <summary>Floor entity.</summary>
    public CastleId Entity { get; init; }

    /// <summary>Tile X.</summary>
    public int X { get; init; }

    /// <summary>Tile Y (world Z).</summary>
    public int Y { get; init; }

    /// <summary><c>TilePosition.HeightLevel</c>.</summary>
    public int HeightLevel { get; init; }

    /// <summary>Raw <c>CastleFloor.FloorType</c>.</summary>
    public byte FloorType { get; init; }

    /// <summary><see cref="CastleLogic.FloorTypeName"/> of <see cref="FloorType"/>.</summary>
    public string FloorTypeName { get; init; } = "";

    /// <summary><c>CastleFloor.Impassable</c>.</summary>
    public bool Impassable { get; init; }

    /// <summary>Room entity index, 0 when the floor has no room.</summary>
    public int RoomId { get; init; }

    /// <summary>NESW wall bits (<see cref="CastleLogic.PackCardinals"/>).</summary>
    public int WallFlags { get; init; }

    /// <summary>NESW door bits.</summary>
    public int DoorFlags { get; init; }

    /// <inheritdoc />
    public override string ToString() => X + "," + Y + " h" + HeightLevel + " " + FloorTypeName;
}
