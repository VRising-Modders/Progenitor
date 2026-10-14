using System.Collections.Generic;

namespace Progenitor.Castles;

/// <summary>
/// The game (or a test fake) behind <see cref="Castles"/>. Tests inject a fake so they never
/// need the game; the server uses <c>UnityCastleRuntime</c>, which is the only file in this
/// module that touches Unity or ProjectM types. Every method swallows game exceptions and
/// returns an empty / false result rather than throw into a hook.
/// </summary>
internal interface ICastleRuntime
{
    /// <summary>True in a VRisingServer process with a live server world. Castle reads are server-side.</summary>
    bool IsServer { get; }

    /// <summary>
    /// Whether the <b>exact</b> entity — <c>Entity.Index</c> <i>and</i> <c>Entity.Version</c> —
    /// is still live in the server world. Returns <c>false</c> when the question could not be
    /// answered at all (no world, the read threw, a runtime that has no world), and the caller
    /// then fails closed: "cannot be checked" is treated as stale, matching the rule Effects
    /// settled in R28 finding 26 (<c>ServerBeamRuntime</c>: an unreadable signal is a changed
    /// signal, because the cost of a wrong keep is far higher than the cost of a wrong refusal).
    ///
    /// <para><b>Why a handle needs it, and why it is not <c>World.SequenceNumber</c> (R31
    /// finding 2).</b> A save reload re-assigns every <c>Entity.Index</c> (research
    /// <c>castle-module.md</c> — heart <c>327390:1</c> came back as <c>325974:1</c> on the same
    /// durable castle), so a retained handle's raw ids can name a different castle afterwards.
    /// But the reload does <b>not</b> create a new <c>World</c>: <c>World.SequenceNumber</c> is
    /// get-only and set once in the ctor (server metadata: <c>get_SequenceNumber</c> and no
    /// setter emitted), while
    /// <c>LoadPersistenceSystemV2.TriggerNewLoad(World world, …)</c> and
    /// <c>DeserializeWorldData(RawPersistentSaveData, out NativeArray&lt;int&gt;
    /// existingVersionsByEntityIndex, …)</c> (<c>:85011</c>) both take the <i>existing</i>
    /// world and its <i>existing</i> per-index versions — the load is a normal system tick of
    /// <c>PersistenceGroup</c> (<c>:85021</c>) inside the live world. The sequence number
    /// therefore cannot move across the event it was supposed to detect. The entity
    /// <b>version</b> is the thing that does move: it is exactly what
    /// <c>existingVersionsByEntityIndex</c> is about, and ECS bumps it whenever an index is
    /// freed and re-used, so an id retained across the reload stops existing.</para>
    ///
    /// <para>The stable key that survives all of this is
    /// <c>CastleTerritory.CastleTerritoryIndex</c>, which is what <c>Refresh()</c> re-resolves
    /// through.</para>
    /// </summary>
    bool TryIsEntityLive(CastleId entity, out bool live);

    /// <summary>
    /// Every castle on the world, ordered by <c>CastleTerritoryIndex</c>. Cheap enough for a
    /// once-per-second poll: identity plus the two component reads, no buffers and no buildings.
    /// </summary>
    IReadOnlyList<CastleSnapshot> Snapshot();

    /// <summary>Placed tile-models of one castle, already sorted by network id.</summary>
    IReadOnlyList<CastleBuildingInfo> Buildings(CastleId heart);

    /// <summary>Floor cells of one castle, already row-major.</summary>
    IReadOnlyList<CastleTileInfo> Tiles(CastleId heart);

    /// <summary>
    /// The territory's <c>CastleTerritoryTiles</c> flags, row-major over its <c>WorldBounds</c>.
    /// False when the buffer was missing, unreadable, or its length did not equal the AABB tile
    /// count — the caller then falls back to the AABB.
    /// </summary>
    bool TryGetBuildableTiles(CastleId territory, int expectedTileCount, out bool[] tiles);

    /// <summary>
    /// The entity's <c>TilePosition</c>. False when it has none (most entities, including
    /// player characters, do not).
    /// </summary>
    bool TryGetTilePosition(CastleId entity, out int tileX, out int tileY, out int heightLevel);

    /// <summary>The entity's world position from <c>Translation</c>. False when it has none.</summary>
    bool TryGetPosition(CastleId entity, out CastleVec3 position);

    /// <summary>
    /// The prefab's own footprint from <c>TileBounds.Value</c> (local, around the pivot).
    /// False when the prefab or the component was not found; the caller then uses one tile.
    /// </summary>
    bool TryGetPrefabFootprint(int prefabGuid, out int localMinX, out int localMinY, out int localMaxX, out int localMaxY);

    /// <summary>The user's <c>User.ClanEntity</c>. False when the user has no clan or was unreadable.</summary>
    bool TryGetClan(CastleId user, out CastleId clan);

    /// <summary>Placed models with an inventory on one castle (containers and stations), sorted like <see cref="Buildings"/>.</summary>
    IReadOnlyList<CastleInventoryRow> Inventories(CastleId heart);

    /// <summary>The heart's nameplate, empty when unnamed or unreadable.</summary>
    string HeartName(CastleId heart);

    /// <summary>The heart's level, unlocked slots from its level table, and fuel quantity.</summary>
    CastleHeartFuel HeartFuel(CastleId heart);
}

/// <summary>
/// Default when no runtime is injected: every read is empty, reported as client-side.
/// Used on the client, in the test assembly, and while the module is skipped.
/// </summary>
internal sealed class NullCastleRuntime : ICastleRuntime
{
    internal static readonly NullCastleRuntime Instance = new();

    public bool IsServer => false;

    /// <summary>
    /// No world, so the question cannot be answered. Handles over this runtime are disabled
    /// anyway (client side / module skipped), so failing closed costs nothing here.
    /// </summary>
    public bool TryIsEntityLive(CastleId entity, out bool live)
    {
        live = false;
        return false;
    }

    public IReadOnlyList<CastleSnapshot> Snapshot() => CastleEmpty.Snapshots;

    public IReadOnlyList<CastleBuildingInfo> Buildings(CastleId heart) => CastleEmpty.Buildings;

    public IReadOnlyList<CastleTileInfo> Tiles(CastleId heart) => System.Array.Empty<CastleTileInfo>();

    public bool TryGetBuildableTiles(CastleId territory, int expectedTileCount, out bool[] tiles)
    {
        tiles = System.Array.Empty<bool>();
        return false;
    }

    public bool TryGetTilePosition(CastleId entity, out int tileX, out int tileY, out int heightLevel)
    {
        tileX = 0;
        tileY = 0;
        heightLevel = -1;
        return false;
    }

    public bool TryGetPosition(CastleId entity, out CastleVec3 position)
    {
        position = default;
        return false;
    }

    public bool TryGetPrefabFootprint(int prefabGuid, out int localMinX, out int localMinY, out int localMaxX, out int localMaxY)
    {
        localMinX = 0;
        localMinY = 0;
        localMaxX = 1;
        localMaxY = 1;
        return false;
    }

    public bool TryGetClan(CastleId user, out CastleId clan)
    {
        clan = CastleId.None;
        return false;
    }

    public IReadOnlyList<CastleInventoryRow> Inventories(CastleId heart) => System.Array.Empty<CastleInventoryRow>();

    public string HeartName(CastleId heart) => "";

    public CastleHeartFuel HeartFuel(CastleId heart) => CastleHeartFuel.Fail("no server world");
}
