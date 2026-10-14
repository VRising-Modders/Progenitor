using System;
using System.Collections.Generic;

namespace Progenitor.Castles;

/// <summary>
/// <see cref="ICastleHandle"/> over an <see cref="ICastleRuntime"/>. Identity comes from the
/// snapshot it was built with; buildings, tiles and entity positions are read live. The
/// territory's buildable-tile mask is fetched once per handle and cached: it is a byte per
/// tile over the whole <c>WorldBounds</c> AABB (21 504 tiles on TestPlayer's live plot), so a
/// per-call read would be wasteful and a per-handle one is not.
///
/// <para><b>Staleness (R30 finding 10, R31 finding 2, R32 finding 1).</b> The snapshot holds raw
/// <c>Entity.Index</c> / <c>Version</c> pairs, and a save reload re-assigns those on the same
/// durable castle (research <c>castle-module.md</c>: heart <c>327390:1</c> came back as
/// <c>325974:1</c>). Two independent checks say so, in this order:</para>
/// <list type="number">
/// <item><description><b>The world-load epoch</b> (<see cref="Progenitor.WorldState.WorldEpoch"/>,
/// job FEPOCH). The handle stamps <c>WorldEpoch.Current</c> at construction; when the counter has
/// moved, the handle is stale <i>whatever the world says about its ids</i>. This is the check that
/// actually covers a reload. R32 finding 1: every id in the research reload table is
/// <b>version 1</b> on both sides — heart <c>327390:1 → 325974:1</c>, owner <c>326690:1 →
/// 326205:1</c>, character <c>324767:1 → 327864:1</c> — which is the signature of a load that
/// creates entities fresh from index 0 at version 1, not of a recycled index whose version was
/// bumped. Under that reading index <c>327390</c> is simply re-issued at version 1 and
/// <c>EntityManager.Exists(327390:1)</c> answers <b>true</b> after the reload, so a liveness
/// check alone would hand a caller somebody else's plot with <c>Enabled == true</c>.</description></item>
/// <item><description><b>The per-read liveness check</b>, kept as an additional guard: the handle
/// asks the runtime, on every live read, whether its two recorded entities — the heart and the
/// territory entity — are still live at that exact index and version. That is what catches a
/// castle demolished or a heart destroyed <i>within</i> one epoch, which the counter cannot
/// see.</description></item>
/// </list>
/// <para>Neither is <c>World.SequenceNumber</c>: that number is set once in the <c>World</c> ctor
/// and the V Rising load runs inside the existing world, so it cannot move across the event being
/// detected (<see cref="ICastleRuntime.TryIsEntityLive"/> has the dump lines).</para>
///
/// <para>When either entity is gone — or the runtime cannot answer at all, which fails
/// <b>closed</b> (R31 finding 15, the rule Effects settled in R28 finding 26) — the handle is
/// <b>stale</b>: <see cref="Enabled"/> goes false with a <see cref="DisabledReason"/> that says
/// which id died, and no read re-enters the world with an id that may now belong to somebody
/// else's plot. <see cref="Refresh"/> re-resolves through
/// <c>Castles.ForTerritory(Territory.Index)</c> — the territory index is the stable key the
/// research identified, and it survives the reload the entity ids do not.</para>
/// </summary>
internal sealed class CastleHandle : ICastleHandle
{
    private readonly ICastleRuntime? _runtime;
    private readonly object _gate = new();
    private readonly bool _enabled;
    private bool[]? _buildableTiles;
    private bool _maskFetched;

    /// <summary>
    /// The world-load epoch this handle's ids were read in (job FEPOCH). Compared on every live
    /// read; a move means a save was reloaded and the recorded <c>index:version</c> pairs name
    /// whatever the new load happened to put at those indices.
    /// </summary>
    private readonly int _epoch;

    /// <summary>Set once per process when the liveness signal could not be read at all.</summary>
    private static int _loggedUnreadable;

    internal CastleHandle(CastleSnapshot snapshot, ICastleRuntime? runtime, bool enabled)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _runtime = runtime;
        _enabled = enabled;
        _epoch = Progenitor.WorldState.WorldEpoch.Current;
    }

    /// <summary>The world-load epoch this handle's entity ids were read in.</summary>
    internal int Epoch => _epoch;

    /// <summary>A handle that stands for "no castle here".</summary>
    internal static CastleHandle Disabled(ICastleRuntime? runtime = null) =>
        new(new CastleSnapshot(), runtime, enabled: false);

    public bool Enabled => _enabled && !IsStale;

    public bool IsStale
    {
        get
        {
            if (!_enabled || _runtime == null) return false;
            return StaleDetail() != null;
        }
    }

    public string DisabledReason
    {
        get
        {
            if (!_enabled) return NoCastleReason;
            string? detail = StaleDetail();
            return detail == null ? "" : StaleReason(Territory.Index, detail);
        }
    }

    /// <summary>What a handle that never stood for a castle reports.</summary>
    internal const string NoCastleReason =
        "no castle (client side, module skipped, or the handle is disabled)";

    /// <summary>What the reason says when the runtime could not answer the liveness question.</summary>
    internal const string UnreadableDetail =
        "the world could not be asked whether these entities are still live";

    /// <summary>
    /// What a handle retained across a world reload reports. Names the territory index —
    /// the stable key — so the caller knows what to pass to <c>Castles.ForTerritory</c>.
    /// </summary>
    internal static string StaleReason(int territoryIndex, string detail) =>
        "stale handle: " + detail
        + ". Entity ids are re-assigned across a save reload, so this handle is not read again. "
        + "Call Refresh(), or Castles.ForTerritory(" + territoryIndex + ") — the territory index "
        + "is the stable key.";

    /// <summary>
    /// Why this handle is stale, or null when it is not. The two ids the handle actually reads
    /// with — the heart (every building / tile read goes through it) and the territory entity
    /// (the buildable-tile mask) — are checked at their exact <c>index:version</c>.
    ///
    /// <para>An unanswerable question is stale, not "fine" (R31 finding 15). A runtime that
    /// cannot report liveness cannot be read from safely either, and the recovery — take a
    /// fresh handle, or <see cref="Refresh"/> — is one call.</para>
    /// </summary>
    private string? StaleDetail()
    {
        var runtime = _runtime;
        if (runtime == null) return null;

        // The epoch first: it is a field compare, it needs no world, and it is the only check
        // that sees a save reload (R32 finding 1 — the reload re-issues the same index at the
        // same version 1, so the liveness check below answers "live" for somebody else's
        // entity). Both epochs are named so the reason line says how far behind the handle is.
        int current = Progenitor.WorldState.WorldEpoch.Current;
        if (current != _epoch)
            return "the world was loaded since this handle was taken ("
                + Progenitor.WorldState.WorldEpoch.FormatStale(_epoch, current) + ")";

        if (Snapshot.Heart.IsNone)
            return "the handle carries no heart entity to check";

        if (!TryIsLive(runtime, Snapshot.Heart, out bool heartLive))
            return UnreadableDetail;
        if (!heartLive)
            return "the heart entity " + Snapshot.Heart + " is no longer live in this world";

        var territory = Snapshot.Territory.Entity;
        if (!territory.IsNone)
        {
            if (!TryIsLive(runtime, territory, out bool territoryLive))
                return UnreadableDetail;
            if (!territoryLive)
                return "the territory entity " + territory + " is no longer live in this world";
        }

        return null;
    }

    private static bool TryIsLive(ICastleRuntime runtime, CastleId entity, out bool live)
    {
        try
        {
            if (runtime.TryIsEntityLive(entity, out live)) return true;
        }
        catch (Exception e)
        {
            live = false;
            LogUnreadableOnce("threw: " + e.Message);
            return false;
        }

        live = false;
        LogUnreadableOnce("returned no answer (no world, or a runtime without the signal)");
        return false;
    }

    private static void LogUnreadableOnce(string why)
    {
        if (System.Threading.Interlocked.Exchange(ref _loggedUnreadable, 1) != 0) return;
        Plugin.Logger?.LogWarning(
            "Castles: the entity-liveness check " + why + ". Retained handles report stale while "
            + "it is unreadable — an unreadable signal is treated as changed, the same rule "
            + "Effects uses for its world signal. Castles.ForTerritory still works. Logged once.");
    }

    /// <summary>Test hook: forget that the unreadable-signal warning was already emitted.</summary>
    internal static void ResetUnreadableLogForTests() =>
        System.Threading.Interlocked.Exchange(ref _loggedUnreadable, 0);

    public CastleSnapshot Snapshot { get; }

    public CastleId Heart => Snapshot.Heart;

    public CastleTerritoryInfo Territory => Snapshot.Territory;

    public CastleOwnerInfo Owner => Snapshot.Owner;

    public byte Level => Snapshot.Level;

    public int FloorCount => Snapshot.FloorCount;

    public bool Raided => Snapshot.Raided;

    public bool Decaying => Snapshot.Decaying;

    public bool IsTilePrecise
    {
        get
        {
            if (!Enabled) return false;
            return BuildableTiles() != null;
        }
    }

    public IReadOnlyList<CastleBuildingInfo> Buildings()
    {
        if (!Enabled || _runtime == null) return CastleEmpty.Buildings;
        try
        {
            return _runtime.Buildings(Heart) ?? CastleEmpty.Buildings;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Castles: Buildings(" + Heart + ") threw: " + e.Message);
            return CastleEmpty.Buildings;
        }
    }

    public IReadOnlyList<KeyValuePair<string, int>> BuildingsByPrefab() =>
        CastleCounts.ByPrefab(Buildings());

    public string Name
    {
        get
        {
            if (!Enabled || _runtime == null) return "";
            try { return _runtime.HeartName(Heart) ?? ""; }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Castles: HeartName(" + Heart + ") threw: " + e.Message);
                return "";
            }
        }
    }

    public Progenitor.Inventory.ContainerId HeartId
    {
        get
        {
            var resolver = CastleContainerRows.HeartIdResolver;
            if (!Enabled || resolver == null) return Progenitor.Inventory.ContainerId.None;
            try { return resolver(Heart); }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Castles: heart id for " + Heart + " threw: " + e.Message);
                return Progenitor.Inventory.ContainerId.None;
            }
        }
    }

    public CastleHeartFuel HeartFuel()
    {
        if (!Enabled || _runtime == null) return CastleHeartFuel.Fail(DisabledReason.Length > 0 ? DisabledReason : NoCastleReason);
        try { return _runtime.HeartFuel(Heart) ?? CastleHeartFuel.Fail("no answer"); }
        catch (Exception e) { return CastleHeartFuel.Fail("heart read threw: " + e.Message); }
    }

    public IReadOnlyList<CastleContainerInfo> Containers() => InventoryRows(stations: false);

    public IReadOnlyList<CastleContainerInfo> Stations() => InventoryRows(stations: true);

    private IReadOnlyList<CastleContainerInfo> InventoryRows(bool stations)
    {
        if (!Enabled || _runtime == null) return Array.Empty<CastleContainerInfo>();
        try
        {
            return CastleContainerRows.Build(_runtime.Inventories(Heart), Territory.Index, stations,
                Progenitor.WorldState.WorldEpoch.Current);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Castles: Inventories(" + Heart + ") threw: " + e.Message);
            return Array.Empty<CastleContainerInfo>();
        }
    }

    public IReadOnlyList<CastleTileInfo> Tiles()
    {
        if (!Enabled || _runtime == null) return Array.Empty<CastleTileInfo>();
        try
        {
            return _runtime.Tiles(Heart) ?? Array.Empty<CastleTileInfo>();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Castles: Tiles(" + Heart + ") threw: " + e.Message);
            return Array.Empty<CastleTileInfo>();
        }
    }

    public bool IsInside(CastleVec3 position)
    {
        if (!Enabled) return false;
        if (!CastleGeometry.TryWorldToTile(position, out int x, out int y)) return false;
        return IsTileInside(x, y, heightLevel: -1);
    }

    public bool IsInside(CastleId entity)
    {
        if (!Enabled || _runtime == null || entity.IsNone) return false;

        // A tile-model knows its own tile and height level; that is the exact answer.
        if (_runtime.IsTileModel(entity) && _runtime.TryGetTilePosition(entity, out int tx, out int ty, out int height))
            return IsTileInside(tx, ty, height);

        // Everything else (player characters, units, dropped items) by its Translation, the position
        // the game's own standing-plot rule reads. A character's TilePosition is written in a deferred
        // pass and can name the old plot for seconds after a teleport.
        if (_runtime.TryGetPosition(entity, out var position))
            return IsInside(position);

        return false;
    }

    public CastlePlacementCheck CanPlace(int prefabGuid, CastleVec3 position, byte rotation = 0)
    {
        if (!Enabled || _runtime == null)
        {
            // A stale handle says so rather than reporting the generic "no castle": the caller
            // retained it across a reload and the fix is a different one (R30 finding 10).
            string reason = DisabledReason;
            return reason.Length == 0 || reason == NoCastleReason
                ? CastlePlacementCheck.Disabled
                : CastlePlacementCheck.Disabled with { TerritoryIndex = Territory.Index, Detail = reason };
        }

        if (!CastleGeometry.TryWorldToTile(position, out int tileX, out int tileY))
        {
            return CastlePlacementCheck.Disabled with
            {
                PrefabGuid = prefabGuid,
                TerritoryIndex = Territory.Index,
                Reason = CastleBlockReason.BadPosition,
                Detail = "position " + position + " is not a representable tile",
            };
        }

        bool resolved = false;
        int lMinX = 0, lMinY = 0, lMaxX = 1, lMaxY = 1;
        try
        {
            resolved = _runtime.TryGetPrefabFootprint(prefabGuid, out lMinX, out lMinY, out lMaxX, out lMaxY);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Castles: prefab footprint for " + prefabGuid + " threw: " + e.Message);
        }

        if (!resolved || lMaxX <= lMinX || lMaxY <= lMinY)
        {
            resolved = false;
            lMinX = 0;
            lMinY = 0;
            lMaxX = 1;
            lMaxY = 1;
        }

        CastleGeometry.LocalToWorldBounds(tileX, tileY, rotation, lMinX, lMinY, lMaxX, lMaxY,
            out int minX, out int minY, out int maxX, out int maxY);

        // Height level. There is no verified world-Y to height-level mapping (CastleGeometry),
        // so the level the caller means cannot be derived from `position`.
        //
        // On a single-level territory that is not a problem: there is one level and the answer
        // is exact. On a multi-level one (the live plot is 10-12) the old code passed -1, which
        // skips the band test *and* the occupancy loop's level filter, so a floor on level 11
        // refused a chest on level 12 and nearly every tile near existing construction came back
        // TileOccupied (R30 finding 16). Occupancy is keyed by (tile, height level), so the band
        // is evaluated one level at a time and the first level that allows the placement is the
        // answer; HeightAmbiguous marks that the caller was not the one who chose the level.
        var mask = BuildableTiles();
        var buildings = Buildings();
        var t = Territory;

        if (t.MinHeightLevel == t.MaxHeightLevel)
        {
            return CastlePlacement.Evaluate(
                t, mask, buildings, minX, minY, maxX, maxY,
                t.MinHeightLevel, prefabGuid, resolved);
        }

        CastlePlacementCheck? first = null;
        for (int level = t.MinHeightLevel; level <= t.MaxHeightLevel; level++)
        {
            var candidate = CastlePlacement.Evaluate(
                t, mask, buildings, minX, minY, maxX, maxY,
                level, prefabGuid, resolved) with
            { HeightAmbiguous = true };

            if (candidate.Allowed)
                return candidate;

            first ??= candidate;
        }

        // Nothing in the band is free. Every refusal that is not level-dependent
        // (OutsideTerritory, OutsideBuildableTiles) is the same on every level, so the lowest
        // level's answer is the representative one.
        return first ?? CastlePlacement.Evaluate(
            t, mask, buildings, minX, minY, maxX, maxY,
            -1, prefabGuid, resolved) with
        { HeightAmbiguous = true };
    }

    /// <summary>
    /// Re-resolve through <c>Castles.ForTerritory</c>, never <c>ForHeart</c>: the territory
    /// index is the key that survives a save reload, the heart entity id is not (R30 finding
    /// 10). A stale handle refreshes into the live castle on its own territory rather than
    /// into whatever entity now holds its old index.
    /// </summary>
    public ICastleHandle Refresh()
    {
        if (!_enabled || _runtime == null) return this;
        return Castles.ForTerritory(Territory.Index);
    }

    /// <inheritdoc />
    public override string ToString() =>
        Enabled ? Snapshot.ToString() : "castle (disabled: " + DisabledReason + ")";

    private bool IsTileInside(int x, int y, int heightLevel)
    {
        var t = Territory;
        if (!CastleGeometry.BoundsContains(x, y, t.MinX, t.MinY, t.MaxX, t.MaxY))
            return false;

        if (!CastleGeometry.HeightInRange(heightLevel, t.MinHeightLevel, t.MaxHeightLevel))
            return false;

        var mask = BuildableTiles();
        if (mask == null)
            return true;

        int index = CastleGeometry.LocalTileIndex(x, y, t.MinX, t.MinY, t.MaxX, t.MaxY);
        return index >= 0 && index < mask.Length && mask[index];
    }

    private bool[]? BuildableTiles()
    {
        lock (_gate)
        {
            if (_maskFetched) return _buildableTiles;
            _maskFetched = true;
            if (!Enabled || _runtime == null) return null;

            int expected = Territory.TileCount;
            if (expected <= 0) return null;

            try
            {
                if (_runtime.TryGetBuildableTiles(Territory.Entity, expected, out var tiles)
                    && tiles != null
                    && tiles.Length == expected)
                {
                    _buildableTiles = tiles;
                }
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning(
                    "Castles: CastleTerritoryTiles read for territory " + Territory.Index + " threw: " + e.Message);
            }

            return _buildableTiles;
        }
    }
}

/// <summary>Grouping helpers shared by the handle, the sample command and the tests.</summary>
public static class CastleCounts
{
    /// <summary>
    /// Count placed models by prefab name, highest count first, then name (ordinal) so two
    /// reads of the same plot print in the same order. Unnamed prefabs group under their GUID.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, int>> ByPrefab(IReadOnlyList<CastleBuildingInfo>? buildings)
    {
        if (buildings == null || buildings.Count == 0)
            return Array.Empty<KeyValuePair<string, int>>();

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < buildings.Count; i++)
        {
            var b = buildings[i];
            if (b == null) continue;
            string key = b.Label;
            counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        var rows = new List<KeyValuePair<string, int>>(counts.Count);
        foreach (var pair in counts)
            rows.Add(pair);

        rows.Sort(static (a, b) =>
        {
            int c = b.Value.CompareTo(a.Value);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });

        return rows;
    }

    /// <summary>
    /// <c>name×count</c> joined with <c>", "</c>, capped at <paramref name="max"/> entries with a
    /// <c>+N more</c> tail. The form the sample <c>.castle</c> reply uses; chat replies are
    /// split at 500 UTF-8 bytes, so the cap is not cosmetic.
    /// </summary>
    public static string Format(IReadOnlyList<KeyValuePair<string, int>>? rows, int max = 8)
    {
        if (rows == null || rows.Count == 0) return "none";
        if (max < 1) max = 1;

        var sb = new System.Text.StringBuilder();
        int shown = Math.Min(rows.Count, max);
        for (int i = 0; i < shown; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(rows[i].Key).Append('×').Append(rows[i].Value);
        }

        if (rows.Count > shown)
            sb.Append(", +").Append(rows.Count - shown).Append(" more");

        return sb.ToString();
    }
}
