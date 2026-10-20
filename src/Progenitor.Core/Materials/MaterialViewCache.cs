using System;
using System.Collections.Generic;
using System.Diagnostics;
using Progenitor.Inventory;

namespace Progenitor.Materials;

/// <summary>
/// What one inventory read found: the spendable amount per item (slots without an item entity),
/// sorted by item. Immutable, so a view can keep it and compare a later read against it exactly.
/// </summary>
internal sealed class MaterialStock
{
    /// <summary>An inventory that could not be read (gone, stale, unbound): nothing in it counts.</summary>
    internal static readonly MaterialStock Unreadable = new(false, Array.Empty<int>(), Array.Empty<int>());

    private readonly int[] _items;
    private readonly int[] _counts;

    internal bool Ok { get; }

    internal int ItemCount => _items.Length;

    private MaterialStock(bool ok, int[] items, int[] counts)
    {
        Ok = ok;
        _items = items;
        _counts = counts;
    }

    /// <summary>From one read's per-item totals (amounts capped at <see cref="int.MaxValue"/>; zero and item 0 dropped).</summary>
    internal static MaterialStock From(Dictionary<int, long> totals)
    {
        int n = 0;
        foreach (var kv in totals) if (kv.Key != 0 && kv.Value > 0) n++;
        var items = new int[n];
        var counts = new int[n];
        int i = 0;
        foreach (var kv in totals)
        {
            if (kv.Key == 0 || kv.Value <= 0) continue;
            items[i] = kv.Key;
            counts[i] = kv.Value > int.MaxValue ? int.MaxValue : (int)kv.Value;
            i++;
        }
        Array.Sort(items, counts);
        return new MaterialStock(true, items, counts);
    }

    internal int Count(int item)
    {
        int i = Array.BinarySearch(_items, item);
        return i >= 0 ? _counts[i] : 0;
    }

    internal void AddItems(ICollection<int> into)
    {
        foreach (int item in _items) into.Add(item);
    }

    /// <summary>Same readability and the same amount of every item.</summary>
    internal bool SameAs(MaterialStock other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (Ok != other.Ok || _items.Length != other._items.Length) return false;
        for (int i = 0; i < _items.Length; i++)
            if (_items[i] != other._items[i] || _counts[i] != other._counts[i]) return false;
        return true;
    }
}

/// <summary>
/// One refresh's reads: every inventory is read at most once and every container id resolved at most
/// once, however many items, operations and players ask. Display only: it never writes (a write
/// throws), and it is cleared at the end of each refresh, so nothing here outlives the main-thread step
/// it was read in. Payment never uses it; it reads the live ledger at the check.
/// </summary>
internal sealed class MaterialStockReads : IMaterialLedger<InventoryRef>
{
    private readonly IMaterialWorld _world;
    private readonly Dictionary<InventoryRef, MaterialStock> _stock = new();
    private readonly Dictionary<ContainerId, InventoryRef> _resolved = new();
    private readonly Dictionary<int, long> _scratch = new();
    private HashSet<InventoryRef>? _trackStock;
    private Dictionary<ContainerId, InventoryRef>? _trackIds;

    internal MaterialStockReads(IMaterialWorld world) => _world = world ?? throw new ArgumentNullException(nameof(world));

    /// <summary>Inventory reads that reached the world (since construction).</summary>
    internal long WorldReads { get; private set; }

    /// <summary>Stopwatch ticks spent in world reads and id resolutions (since construction; diagnostics).</summary>
    internal long ReadTicks { get; private set; }

    /// <summary>Forget this refresh's reads.</summary>
    internal void Clear()
    {
        _stock.Clear();
        _resolved.Clear();
        _trackStock = null;
        _trackIds = null;
    }

    /// <summary>Forget one inventory's read: the next <see cref="Stock"/> reads it from the world again.</summary>
    internal void Forget(InventoryRef inventory) => _stock.Remove(inventory);

    /// <summary>Forget one container id's resolution: the next <see cref="TryResolve"/> resolves it again.</summary>
    internal void Forget(ContainerId id) => _resolved.Remove(id);

    /// <summary>Record every inventory read and id resolved from now until <see cref="EndTrack"/>.</summary>
    internal void BeginTrack(HashSet<InventoryRef> stock, Dictionary<ContainerId, InventoryRef> ids)
    {
        stock.Clear();
        ids.Clear();
        _trackStock = stock;
        _trackIds = ids;
    }

    internal void EndTrack()
    {
        _trackStock = null;
        _trackIds = null;
    }

    /// <summary>The inventory's stock, read from the world once per refresh.</summary>
    internal MaterialStock Stock(InventoryRef inventory)
    {
        _trackStock?.Add(inventory);
        if (_stock.TryGetValue(inventory, out var known)) return known;
        MaterialStock stock;
        _scratch.Clear();
        WorldReads++;
        long t0 = Stopwatch.GetTimestamp();
        try { stock = _world.ReadStock(inventory, _scratch) ? MaterialStock.From(_scratch) : MaterialStock.Unreadable; }
        catch { stock = MaterialStock.Unreadable; }
        ReadTicks += Stopwatch.GetTimestamp() - t0;
        _stock[inventory] = stock;
        return stock;
    }

    /// <summary>A container id's inventory, resolved once per refresh (<see cref="InventoryRef.None"/> when it does not resolve).</summary>
    internal bool TryResolve(ContainerId id, out InventoryRef inventory)
    {
        if (!_resolved.TryGetValue(id, out inventory))
        {
            long t0 = Stopwatch.GetTimestamp();
            try { if (!_world.TryResolve(id, out inventory)) inventory = InventoryRef.None; }
            catch { inventory = InventoryRef.None; }
            ReadTicks += Stopwatch.GetTimestamp() - t0;
            _resolved[id] = inventory;
        }
        if (_trackIds != null) _trackIds[id] = inventory;
        return !inventory.IsNone;
    }

    public int Count(InventoryRef source, int item) => Stock(source).Count(item);

    public int Remove(InventoryRef source, int item, int amount) =>
        throw new InvalidOperationException("materials: a displayed view never takes items");

    public int Add(InventoryRef source, int item, int amount) =>
        throw new InvalidOperationException("materials: a displayed view never gives items");
}

/// <summary>How <see cref="MaterialService.RefreshView"/> produced a player's view.</summary>
internal enum MaterialViewOutcome
{
    /// <summary>Not governed for this player and operation: no view (the client shows the game's own counts).</summary>
    NotGoverned,

    /// <summary>Recomputed from the provider and the containers.</summary>
    Computed,

    /// <summary>Nothing relevant changed since the last compute: the cached view, no provider sources asked, no totals recounted.</summary>
    Reused,
}

/// <summary>One player's view for one operation, as last computed.</summary>
internal readonly struct MaterialViewResult
{
    internal MaterialContext Context { get; }
    internal long Revision { get; }

    /// <summary>Governed items and their pool totals, sorted by item; zero included.</summary>
    internal IReadOnlyList<MaterialLine> Rows { get; }

    internal MaterialViewResult(MaterialContext context, long revision, IReadOnlyList<MaterialLine> rows)
    {
        Context = context;
        Revision = revision;
        Rows = rows;
    }
}

/// <summary>
/// Everything a computed view depended on, so a later refresh can tell, without asking the provider
/// for sources or recounting any total, that the view would come out the same: the provider and its
/// revision, the context, the inventories left out and put first, the plot's shared-storage
/// inventories, every container id resolved and every inventory read while computing (the bag and
/// the station included).
/// </summary>
internal sealed class MaterialViewEntry
{
    internal IMaterialSourceProvider? Provider;
    internal MaterialContext Context;
    internal long Revision;
    internal bool Governed;

    /// <summary>Not governed because the operation needs a standing plot and the character stood on none (the provider was not asked).</summary>
    internal bool PlotRefused;

    /// <summary>A provider call threw while computing: never reused, recomputed every refresh.</summary>
    internal bool NoReuse;

    internal InventoryRef Front = InventoryRef.None;
    internal InventoryRef Station = InventoryRef.None;
    internal readonly HashSet<InventoryRef> Exclusions = new();
    internal readonly List<InventoryRef> Shared = new();
    internal readonly List<(ContainerId Id, InventoryRef Inventory)> Ids = new();
    internal readonly List<(InventoryRef Inventory, MaterialStock Stock)> Stocks = new();
    internal IReadOnlyList<MaterialLine> Rows = Array.Empty<MaterialLine>();

    /// <summary>The refresh that last asked for this entry (stale entries are swept).</summary>
    internal long Stamp;
}
