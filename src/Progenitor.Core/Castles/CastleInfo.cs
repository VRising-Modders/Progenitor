using System;
using System.Collections.Generic;

namespace Progenitor.Castles;

/// <summary>
/// Who a castle belongs to. <see cref="User"/> is the owning <c>User</c> entity
/// (<c>UserOwner.Owner</c>, falling back to <c>CastleHeart.LastUserOwner</c>);
/// <see cref="Clan"/> is that user's <c>User.ClanEntity</c> when they are in one.
/// </summary>
public sealed class CastleOwnerInfo
{
    /// <summary>Owning <c>User</c> entity, or <see cref="CastleId.None"/>.</summary>
    public CastleId User { get; init; }

    /// <summary><c>User.PlatformId</c> (Steam id), or 0 when the user row was unreadable.</summary>
    public ulong PlatformId { get; init; }

    /// <summary><c>User.CharacterName</c>, empty when unreadable.</summary>
    public string Name { get; init; } = "";

    /// <summary><c>User.ClanEntity</c>, or <see cref="CastleId.None"/> when the owner has no clan.</summary>
    public CastleId Clan { get; init; }

    /// <summary><c>ClanTeam.Name</c>, empty when there is no clan or it was unreadable.</summary>
    public string ClanName { get; init; } = "";

    /// <summary>True when <see cref="Clan"/> is set.</summary>
    public bool HasClan => !Clan.IsNone;

    /// <summary>Owner for a disabled handle.</summary>
    public static CastleOwnerInfo None { get; } = new();

    /// <inheritdoc />
    public override string ToString() =>
        HasClan
            ? (Name.Length == 0 ? User.ToString() : Name) + " [" + ClanName + "]"
            : (Name.Length == 0 ? User.ToString() : Name);
}

/// <summary>
/// A castle's claimed territory: the plot index mods key on, its tile AABB
/// (<c>CastleTerritory.WorldBounds</c>), its height band, and how much of it the reader
/// actually managed to read.
/// </summary>
public sealed class CastleTerritoryInfo
{
    /// <summary><c>CastleTerritory.CastleTerritoryIndex</c>.</summary>
    public int Index { get; init; }

    /// <summary>Territory entity (carries the bounds and the two buffers).</summary>
    public CastleId Entity { get; init; }

    /// <summary><c>WorldBounds.Min.x</c>, inclusive.</summary>
    public int MinX { get; init; }

    /// <summary><c>WorldBounds.Min.y</c> (world Z), inclusive.</summary>
    public int MinY { get; init; }

    /// <summary><c>WorldBounds.Max.x</c>, exclusive.</summary>
    public int MaxX { get; init; }

    /// <summary><c>WorldBounds.Max.y</c> (world Z), exclusive.</summary>
    public int MaxY { get; init; }

    /// <summary><c>CastleTerritory.MinHeightLevel</c>, inclusive.</summary>
    public byte MinHeightLevel { get; init; }

    /// <summary><c>CastleTerritory.MaxHeightLevel</c>, inclusive.</summary>
    public byte MaxHeightLevel { get; init; }

    /// <summary>
    /// <c>CastleTerritoryBlocks</c> element count, or 0 when the length was not readable or
    /// failed the sanity check. See <see cref="BlocksRead"/>.
    /// </summary>
    public int BlockCount { get; init; }

    /// <summary>True when <see cref="BlockCount"/> came from a buffer length that passed its check.</summary>
    public bool BlocksRead { get; init; }

    /// <summary>Tiles in the AABB: <c>(MaxX − MinX) × (MaxY − MinY)</c>.</summary>
    public int TileCount => CastleGeometry.BoundsTileCount(MinX, MinY, MaxX, MaxY);

    /// <summary>Width in tiles.</summary>
    public int Width => Math.Max(0, MaxX - MinX);

    /// <summary>Height in tiles (world Z axis).</summary>
    public int Depth => Math.Max(0, MaxY - MinY);

    /// <summary>Empty territory for a disabled handle.</summary>
    public static CastleTerritoryInfo None { get; } = new();

    /// <inheritdoc />
    public override string ToString() =>
        "#" + Index + " [" + MinX + "," + MinY + ".." + MaxX + "," + MaxY + "] h" +
        MinHeightLevel + "-" + MaxHeightLevel;
}

/// <summary>
/// One placed tile-model, game-free. The live shape of
/// <see cref="CastleBuilding"/> without Unity or Stunlock types, so plugin code, the sample
/// command and the tests all see the same rows.
/// </summary>
public sealed class CastleBuildingInfo
{
    /// <summary>Tile-model entity.</summary>
    public CastleId Entity { get; init; }

    /// <summary><c>PrefabGUID._Value</c> (the signed hash).</summary>
    public int PrefabGuid { get; init; }

    /// <summary>Spawnable name from <c>PrefabLookup</c>, empty when unresolved.</summary>
    public string PrefabName { get; init; } = "";

    /// <summary><c>TilePosition.Tile.x</c> (the model's pivot tile).</summary>
    public int TileX { get; init; }

    /// <summary><c>TilePosition.Tile.y</c> (world Z).</summary>
    public int TileY { get; init; }

    /// <summary><c>TilePosition.HeightLevel</c>.</summary>
    public int HeightLevel { get; init; }

    /// <summary>Raw <c>TilePosition.TileRotation</c>.</summary>
    public byte Rotation { get; init; }

    /// <summary><see cref="CastleLogic.RotationName"/> of <see cref="Rotation"/>.</summary>
    public string RotationName { get; init; } = "";

    /// <summary><c>TileBounds.Value.Min.x</c>, inclusive.</summary>
    public int FootprintMinX { get; init; }

    /// <summary><c>TileBounds.Value.Min.y</c>, inclusive.</summary>
    public int FootprintMinY { get; init; }

    /// <summary><c>TileBounds.Value.Max.x</c>, exclusive.</summary>
    public int FootprintMaxX { get; init; }

    /// <summary><c>TileBounds.Value.Max.y</c>, exclusive.</summary>
    public int FootprintMaxY { get; init; }

    /// <summary>True when the entity carries <c>Interactable</c>.</summary>
    public bool Interactable { get; init; }

    /// <summary><c>heart</c> / <c>container</c> / <c>station</c> / <c>decor</c>.</summary>
    public string Kind { get; init; } = "decor";

    /// <summary><c>NetworkId.Normal_Index</c>, <see cref="int.MaxValue"/> when missing.</summary>
    public int NetworkIndex { get; init; } = int.MaxValue;

    /// <summary><c>NetworkId.Normal_Generation</c>.</summary>
    public int NetworkGeneration { get; init; }

    /// <summary>Label for logs and command replies.</summary>
    public string Label => PrefabName.Length > 0
        ? PrefabName
        : PrefabGuid.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override string ToString() =>
        Label + " @" + TileX + "," + TileY + " h" + HeightLevel + " (" + Kind + ")";
}

/// <summary>
/// One castle as the poller and the handle factory see it: identity plus everything that is
/// cheap to read once. Buildings and the buildable-tile mask are fetched on demand by
/// <see cref="ICastleHandle"/>, not carried here, so a once-per-second poll stays cheap.
/// </summary>
public sealed class CastleSnapshot
{
    /// <summary>Heart entity. The identity the change detector keys on.</summary>
    public CastleId Heart { get; init; }

    /// <summary>Territory of this castle.</summary>
    public CastleTerritoryInfo Territory { get; init; } = CastleTerritoryInfo.None;

    /// <summary>Owner of this castle.</summary>
    public CastleOwnerInfo Owner { get; init; } = CastleOwnerInfo.None;

    /// <summary><c>CastleHeart.Level</c>.</summary>
    public byte Level { get; init; }

    /// <summary><c>CastleHeart.FloorCount</c>, or 0 when unreadable.</summary>
    public int FloorCount { get; init; }

    /// <summary>True when <c>CastleHeart.ActiveEvent</c> is <c>Raided</c>.</summary>
    public bool Raided { get; init; }

    /// <summary>True when <c>CastleTerritoryDecay.Decaying</c> is set.</summary>
    public bool Decaying { get; init; }

    /// <summary>Convenience: <c>Territory.Index</c>.</summary>
    public int TerritoryIndex => Territory.Index;

    /// <inheritdoc />
    public override string ToString() =>
        "castle " + Territory + " heart " + Heart + " of " + Owner + " lv" + Level;
}

/// <summary>Empty read results, allocated once.</summary>
internal static class CastleEmpty
{
    internal static readonly IReadOnlyList<CastleSnapshot> Snapshots = Array.Empty<CastleSnapshot>();
    internal static readonly IReadOnlyList<CastleBuildingInfo> Buildings = Array.Empty<CastleBuildingInfo>();
    internal static readonly IReadOnlyList<ICastleHandle> Handles = Array.Empty<ICastleHandle>();
    internal static readonly IReadOnlyList<string> Strings = Array.Empty<string>();
}
