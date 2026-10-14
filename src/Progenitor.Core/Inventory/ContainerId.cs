using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Inventory;

/// <summary>
/// A stable id for one placed container or station (a chest, a refinement station, a
/// workbench). Unlike an entity id it survives a server restart, and it is never given to a
/// different container: a container destroyed while the server runs has its id retired, and a
/// new one placed on the same tile gets a fresh id. See <see cref="ContainerIdRegistry"/> for
/// how the id is kept and what can break it. Text form: <c>c-</c> plus 32 hex digits.
/// </summary>
public readonly struct ContainerId : IEquatable<ContainerId>
{
    /// <summary>The random 128-bit value.</summary>
    public Guid Value { get; }

    /// <summary>Wrap a value.</summary>
    public ContainerId(Guid value) => Value = value;

    /// <summary>True for the default (no id) value.</summary>
    public bool IsNone => Value == Guid.Empty;

    /// <summary>The default (no id) value.</summary>
    public static ContainerId None => default;

    /// <summary>A fresh random id.</summary>
    public static ContainerId New() => new(Guid.NewGuid());

    /// <summary>Parse <c>c-</c> plus 32 hex digits (the <see cref="ToString"/> form); a bare 32-hex value is accepted too.</summary>
    public static bool TryParse(string? text, out ContainerId id)
    {
        id = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string s = text.Trim();
        if (s.StartsWith("c-", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
        if (!Guid.TryParseExact(s, "N", out var g) || g == Guid.Empty) return false;
        id = new ContainerId(g);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(ContainerId other) => Value == other.Value;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ContainerId other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode();

    /// <summary><c>c-</c> plus 32 lowercase hex digits; <c>none</c> for <see cref="None"/>.</summary>
    public override string ToString() => IsNone ? "none" : "c-" + Value.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>Value equality.</summary>
    public static bool operator ==(ContainerId left, ContainerId right) => left.Equals(right);

    /// <summary>Value inequality.</summary>
    public static bool operator !=(ContainerId left, ContainerId right) => !left.Equals(right);
}

/// <summary>What the registry knows about a <see cref="ContainerId"/>.</summary>
public enum ContainerIdStatus
{
    /// <summary>Never issued in this world (or retired so long ago it was forgotten).</summary>
    Unknown,

    /// <summary>Bound to a live container in the current world load.</summary>
    Live,

    /// <summary>Issued and saved, but not matched to a container since the world loaded yet.</summary>
    Unbound,

    /// <summary>Retired: the container was destroyed or was not found after a load. Never reused.</summary>
    Gone,
}

/// <summary>
/// Where a placed container stands: the key the world save preserves. A tile-model's
/// <c>TilePosition</c>, rotation and prefab are saved with it, and two placed models of the same
/// prefab cannot occupy the same origin tile and height level of one territory.
/// </summary>
internal readonly struct ContainerAnchor : IEquatable<ContainerAnchor>
{
    /// <summary>Construct an anchor.</summary>
    public ContainerAnchor(int territoryIndex, int prefabGuid, int tileX, int tileY, int heightLevel, int rotation)
    {
        TerritoryIndex = territoryIndex;
        PrefabGuid = prefabGuid;
        TileX = tileX;
        TileY = tileY;
        HeightLevel = heightLevel;
        Rotation = rotation;
    }

    /// <summary><c>CastleTerritory.CastleTerritoryIndex</c>; -1 when the model is on no territory.</summary>
    public int TerritoryIndex { get; }

    /// <summary>The model's prefab.</summary>
    public int PrefabGuid { get; }

    /// <summary><c>TilePosition.Tile.x</c>.</summary>
    public int TileX { get; }

    /// <summary><c>TilePosition.Tile.y</c>.</summary>
    public int TileY { get; }

    /// <summary><c>TilePosition.HeightLevel</c>.</summary>
    public int HeightLevel { get; }

    /// <summary><c>TileRotation</c>.</summary>
    public int Rotation { get; }

    /// <summary>Stable text key, used in the saved document.</summary>
    public string Key =>
        string.Create(CultureInfo.InvariantCulture,
            $"t{TerritoryIndex}:p{PrefabGuid}:{TileX},{TileY}:h{HeightLevel}:r{Rotation}");

    /// <summary>Parse <see cref="Key"/>.</summary>
    public static bool TryParse(string? key, out ContainerAnchor anchor)
    {
        anchor = default;
        if (string.IsNullOrEmpty(key)) return false;
        var parts = key.Split(':');
        if (parts.Length != 5 || parts[0].Length < 2 || parts[1].Length < 2 || parts[3].Length < 2 || parts[4].Length < 2)
            return false;
        var xy = parts[2].Split(',');
        if (xy.Length != 2) return false;
        var ci = CultureInfo.InvariantCulture;
        if (!int.TryParse(parts[0].AsSpan(1), NumberStyles.Integer, ci, out int t)
            || !int.TryParse(parts[1].AsSpan(1), NumberStyles.Integer, ci, out int p)
            || !int.TryParse(xy[0], NumberStyles.Integer, ci, out int x)
            || !int.TryParse(xy[1], NumberStyles.Integer, ci, out int y)
            || !int.TryParse(parts[3].AsSpan(1), NumberStyles.Integer, ci, out int h)
            || !int.TryParse(parts[4].AsSpan(1), NumberStyles.Integer, ci, out int r))
            return false;
        anchor = new ContainerAnchor(t, p, x, y, h, r);
        return true;
    }

    /// <inheritdoc />
    public bool Equals(ContainerAnchor o) =>
        TerritoryIndex == o.TerritoryIndex && PrefabGuid == o.PrefabGuid && TileX == o.TileX
        && TileY == o.TileY && HeightLevel == o.HeightLevel && Rotation == o.Rotation;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ContainerAnchor o && Equals(o);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(TerritoryIndex, PrefabGuid, TileX, TileY, HeightLevel, Rotation);

    /// <inheritdoc />
    public override string ToString() => Key;
}

/// <summary>Saved form of the registry (JSON through the world's mod save).</summary>
internal sealed class ContainerIdDocument
{
    /// <summary>Format version.</summary>
    public int Schema { get; set; } = 1;

    /// <summary>Ids still in use: <c>id</c> and anchor key.</summary>
    public List<ContainerIdRow> Live { get; set; } = new();

    /// <summary>Retired ids, newest last, capped at <see cref="ContainerIdRegistry.MaxRetired"/>.</summary>
    public List<string> Retired { get; set; } = new();
}

/// <summary>One saved id.</summary>
internal sealed class ContainerIdRow
{
    /// <summary><see cref="ContainerId.ToString"/>.</summary>
    public string Id { get; set; } = "";

    /// <summary><see cref="ContainerAnchor.Key"/>.</summary>
    public string Anchor { get; set; } = "";
}

/// <summary>A live container the registry is told about: where it stands and its entity now.</summary>
internal readonly struct ContainerSighting
{
    /// <summary>Construct a sighting.</summary>
    public ContainerSighting(ContainerAnchor anchor, int entityIndex, int entityVersion)
    {
        Anchor = anchor;
        EntityIndex = entityIndex;
        EntityVersion = entityVersion;
    }

    /// <summary>Where it stands.</summary>
    public ContainerAnchor Anchor { get; }

    /// <summary><c>Entity.Index</c> in the current world load.</summary>
    public int EntityIndex { get; }

    /// <summary><c>Entity.Version</c> in the current world load.</summary>
    public int EntityVersion { get; }
}

/// <summary>
/// The P5 container-id book. Game-free: the server feeds it sightings and an entity-liveness
/// probe; tests feed it fakes.
///
/// <para><b>Scheme.</b> An id is a random GUID the framework assigns the first time a container
/// is asked about. The book maps it to the container's <see cref="ContainerAnchor"/> (territory,
/// prefab, origin tile, height level, rotation: what the world save keeps for a placed model)
/// and, within one world load, to the container's entity. The book is saved through the world's
/// mod save (<c>progenitor/progenitor.inventory.json</c> beside the autosaves), which is written
/// together with each world save.</para>
///
/// <para><b>Guarantees.</b></para>
/// <list type="bullet">
/// <item><description>Restart: after a load the book re-binds each saved id to the container
/// standing on its anchor (<see cref="Prime"/>); ids whose anchor holds no matching container
/// are retired.</description></item>
/// <item><description>No reuse: within a load, an id stays bound to one entity; when that entity
/// is gone (found by <see cref="Sweep"/> or when another entity shows up on the anchor) the id is
/// retired and the next container on that anchor gets a new id. Ids are random, so a retired id
/// can never be issued again.</description></item>
/// </list>
/// <para><b>Failure modes.</b> (1) A container destroyed and replaced on the same anchor while
/// the framework was not running (or in the last sweep interval before a shutdown save) inherits
/// the old id. (2) A save rolled back to an older autosave can bring back a container whose id
/// the book already retired; it gets a new id. (3) Moving the castle (relocation) changes every
/// anchor, so every container gets a new id. (4) Only placed tile-models have ids: player bags
/// and servants are addressed through their owner, not by id.</para>
/// </summary>
internal sealed class ContainerIdRegistry
{
    /// <summary>Retired ids remembered for <see cref="ContainerIdStatus.Gone"/> answers.</summary>
    public const int MaxRetired = 10_000;

    private readonly Dictionary<ContainerId, Record> _records = new();
    private readonly Dictionary<ContainerAnchor, ContainerId> _byAnchor = new();
    private readonly Dictionary<long, ContainerId> _byEntity = new();
    private readonly LinkedList<ContainerId> _retiredOrder = new();
    private readonly HashSet<ContainerId> _retired = new();
    private readonly Func<ContainerId> _newId;
    private int _primedEpoch = int.MinValue;

    /// <summary>Construct an empty book. <paramref name="newId"/> is for tests; default is random.</summary>
    public ContainerIdRegistry(Func<ContainerId>? newId = null) => _newId = newId ?? ContainerId.New;

    /// <summary>True when the book changed since the last <see cref="TakeDirty"/>.</summary>
    public bool Dirty { get; private set; }

    /// <summary>Live and unbound ids.</summary>
    public int Count => _records.Count;

    /// <summary>Remembered retired ids.</summary>
    public int RetiredCount => _retired.Count;

    /// <summary>The epoch the book was last primed in.</summary>
    public int PrimedEpoch => _primedEpoch;

    /// <summary>Read and clear <see cref="Dirty"/>.</summary>
    public bool TakeDirty()
    {
        bool d = Dirty;
        Dirty = false;
        return d;
    }

    /// <summary>Replace the book with a saved document. Unparsable rows are dropped and counted.</summary>
    public int Load(ContainerIdDocument? doc)
    {
        _records.Clear();
        _byAnchor.Clear();
        _byEntity.Clear();
        _retired.Clear();
        _retiredOrder.Clear();
        _primedEpoch = int.MinValue;
        Dirty = false;
        if (doc == null) return 0;

        int dropped = 0;
        foreach (var row in doc.Live ?? new List<ContainerIdRow>())
        {
            if (row == null || !ContainerId.TryParse(row.Id, out var id) || !ContainerAnchor.TryParse(row.Anchor, out var anchor)
                || _records.ContainsKey(id) || _byAnchor.ContainsKey(anchor))
            {
                dropped++;
                continue;
            }

            _records[id] = new Record(id, anchor);
            _byAnchor[anchor] = id;
        }

        foreach (var text in doc.Retired ?? new List<string>())
        {
            if (ContainerId.TryParse(text, out var id) && !_records.ContainsKey(id) && _retired.Add(id))
                _retiredOrder.AddLast(id);
            else
                dropped++;
        }

        return dropped;
    }

    /// <summary>The book as a document to save.</summary>
    public ContainerIdDocument Save()
    {
        var doc = new ContainerIdDocument();
        foreach (var r in _records.Values)
            doc.Live.Add(new ContainerIdRow { Id = r.Id.ToString(), Anchor = r.Anchor.Key });
        doc.Live.Sort(static (a, b) => string.CompareOrdinal(a.Anchor, b.Anchor));
        foreach (var id in _retiredOrder)
            doc.Retired.Add(id.ToString());
        return doc;
    }

    /// <summary>
    /// After a world load: bind every saved id whose anchor is among <paramref name="sightings"/>
    /// (every placed container in the world now) and retire the ids whose anchor holds nothing.
    /// Issues no new ids. Returns (bound, retired).
    /// </summary>
    public (int Bound, int Retired) Prime(int epoch, IEnumerable<ContainerSighting> sightings)
    {
        _byEntity.Clear();
        int bound = 0;
        if (sightings != null)
        {
            foreach (var s in sightings)
            {
                if (!_byAnchor.TryGetValue(s.Anchor, out var id)) continue;
                var r = _records[id];
                if (r.Epoch == epoch && !(r.EntityIndex == s.EntityIndex && r.EntityVersion == s.EntityVersion))
                    continue; // already bound this load to another entity; Sweep decides
                Bind(r, s.EntityIndex, s.EntityVersion, epoch);
                bound++;
            }
        }

        var unbound = new List<Record>();
        foreach (var r in _records.Values)
            if (r.Epoch != epoch) unbound.Add(r);
        foreach (var r in unbound) Retire(r);

        _primedEpoch = epoch;
        Dirty |= unbound.Count > 0;
        return (bound, unbound.Count);
    }

    /// <summary>
    /// The id for a container seen now. Returns its existing id when the anchor's id is bound
    /// to this entity (or not yet bound in this load); retires the anchor's old id and issues a
    /// new one when the anchor's id belongs to an entity that is gone.
    /// </summary>
    public ContainerId IdFor(ContainerSighting s, int epoch, Func<int, int, bool> isLive)
    {
        if (_byEntity.TryGetValue(EntityKey(s.EntityIndex, s.EntityVersion), out var known)
            && _records.TryGetValue(known, out var kr) && kr.Epoch == epoch && kr.Anchor.Equals(s.Anchor))
            return known;

        if (_byAnchor.TryGetValue(s.Anchor, out var id))
        {
            var r = _records[id];
            if (r.Epoch != epoch)
            {
                // Saved, not seen this load (or bound in an older load): this is its container.
                Bind(r, s.EntityIndex, s.EntityVersion, epoch);
                return id;
            }

            if (r.EntityIndex == s.EntityIndex && r.EntityVersion == s.EntityVersion)
                return id;

            if (isLive != null && isLive(r.EntityIndex, r.EntityVersion))
            {
                // Two live models on one anchor should not exist. Keep the first binding and
                // give this one no id rather than share one.
                return ContainerId.None;
            }

            Retire(r);
        }

        var fresh = new Record(_newId(), s.Anchor);
        _records[fresh.Id] = fresh;
        _byAnchor[s.Anchor] = fresh.Id;
        Bind(fresh, s.EntityIndex, s.EntityVersion, epoch);
        Dirty = true;
        return fresh.Id;
    }

    /// <summary>Retire every id bound in this load whose entity is no longer live. Returns how many.</summary>
    public int Sweep(int epoch, Func<int, int, bool> isLive)
    {
        if (isLive == null) return 0;
        List<Record>? dead = null;
        foreach (var r in _records.Values)
        {
            if (r.Epoch != epoch) continue;
            if (!isLive(r.EntityIndex, r.EntityVersion))
                (dead ??= new List<Record>()).Add(r);
        }

        if (dead == null) return 0;
        foreach (var r in dead) Retire(r);
        return dead.Count;
    }

    /// <summary>Status of <paramref name="id"/> in <paramref name="epoch"/>.</summary>
    public ContainerIdStatus Status(ContainerId id, int epoch)
    {
        if (_records.TryGetValue(id, out var r))
            return r.Epoch == epoch ? ContainerIdStatus.Live : ContainerIdStatus.Unbound;
        return _retired.Contains(id) ? ContainerIdStatus.Gone : ContainerIdStatus.Unknown;
    }

    /// <summary>The entity <paramref name="id"/> is bound to in <paramref name="epoch"/>.</summary>
    public bool TryEntity(ContainerId id, int epoch, out int index, out int version, out ContainerAnchor anchor)
    {
        index = version = 0;
        anchor = default;
        if (!_records.TryGetValue(id, out var r)) return false;
        anchor = r.Anchor;
        if (r.Epoch != epoch) return false;
        index = r.EntityIndex;
        version = r.EntityVersion;
        return true;
    }

    /// <summary>The anchor an id was issued for, live or unbound.</summary>
    public bool TryAnchor(ContainerId id, out ContainerAnchor anchor)
    {
        anchor = default;
        if (!_records.TryGetValue(id, out var r)) return false;
        anchor = r.Anchor;
        return true;
    }

    private void Bind(Record r, int index, int version, int epoch)
    {
        if (r.Epoch == epoch) _byEntity.Remove(EntityKey(r.EntityIndex, r.EntityVersion));
        r.EntityIndex = index;
        r.EntityVersion = version;
        r.Epoch = epoch;
        _byEntity[EntityKey(index, version)] = r.Id;
    }

    private void Retire(Record r)
    {
        _records.Remove(r.Id);
        if (_byAnchor.TryGetValue(r.Anchor, out var a) && a == r.Id) _byAnchor.Remove(r.Anchor);
        _byEntity.Remove(EntityKey(r.EntityIndex, r.EntityVersion));
        if (_retired.Add(r.Id))
        {
            _retiredOrder.AddLast(r.Id);
            while (_retiredOrder.Count > MaxRetired)
            {
                _retired.Remove(_retiredOrder.First!.Value);
                _retiredOrder.RemoveFirst();
            }
        }

        Dirty = true;
    }

    private static long EntityKey(int index, int version) => ((long)index << 32) | (uint)version;

    private sealed class Record
    {
        internal Record(ContainerId id, ContainerAnchor anchor)
        {
            Id = id;
            Anchor = anchor;
        }

        internal ContainerId Id { get; }
        internal ContainerAnchor Anchor { get; }
        internal int EntityIndex { get; set; }
        internal int EntityVersion { get; set; }
        internal int Epoch { get; set; } = int.MinValue;
    }
}
