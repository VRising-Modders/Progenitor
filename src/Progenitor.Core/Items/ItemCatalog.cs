using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Progenitor.Items;

/// <summary>
/// One item prefab in <c>ctx.Items.Catalog()</c>. Immutable; the catalog hands out the same
/// instances until it is rebuilt.
/// </summary>
public sealed class ItemCatalogEntry
{
    /// <summary>Creates an entry. Plugins read entries; Core builds them.</summary>
    public ItemCatalogEntry(int prefabGuid, string prefabName, string? displayName, int maxAmount, long itemCategory, int itemType, bool isCustom)
    {
        PrefabGuid = prefabGuid;
        PrefabName = prefabName ?? "";
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName!.Trim();
        MaxAmount = maxAmount;
        ItemCategory = itemCategory;
        ItemType = itemType;
        IsCustom = isCustom;
    }

    /// <summary>The prefab GUID hash, the same int <c>ctx.Inventory</c> and <c>ctx.Items.Grant</c> take.</summary>
    public int PrefabGuid { get; }

    /// <summary>The prefab name (<c>Item_Ingredient_Mineral_IronOre</c>); for a custom item, its definition id. Never null; empty when the game knows no name.</summary>
    public string PrefabName { get; }

    /// <summary>
    /// The name a player sees, from the game's localization in the current language. Client only
    /// for vanilla items: the dedicated server ships no string tables, so there it is null. A custom
    /// item defined through the content API with <c>Named(...)</c> carries that name on both sides.
    /// </summary>
    public string? DisplayName { get; }

    /// <summary>Stack size, from the same item lookup table the game's inventory code (and <c>ctx.Inventory</c>) reads.</summary>
    public int MaxAmount { get; }

    /// <summary>The game's <c>ItemCategory</c> flags as a long.</summary>
    public long ItemCategory { get; }

    /// <summary>The game's <c>ItemType</c> enum value as an int.</summary>
    public int ItemType { get; }

    /// <summary>True for an item cloned by the content API (<c>DefineItem</c> or a data pack) in this process.</summary>
    public bool IsCustom { get; }

    /// <summary>
    /// True when <paramref name="filter"/> is blank, equals <see cref="PrefabGuid"/> (as an integer),
    /// or is contained in <see cref="PrefabName"/> or <see cref="DisplayName"/> (case-insensitive).
    /// </summary>
    public bool Matches(string? filter)
    {
        var f = (filter ?? "").Trim();
        if (f.Length == 0) return true;
        if (int.TryParse(f, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int guid)
            && guid == PrefabGuid)
            return true;
        return PrefabName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
            || (DisplayName != null && DisplayName.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary><c>Item_Ingredient_Mineral_IronOre (1332980397) "Iron Ore" xN</c> (N is the stack size).</summary>
    public override string ToString() =>
        PrefabName + " (" + PrefabGuid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"
        + (DisplayName == null ? "" : " \"" + DisplayName + "\"")
        + " x" + MaxAmount.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>What the last catalog build cost and where its names came from.</summary>
public readonly struct ItemCatalogInfo
{
    /// <summary>Creates a summary.</summary>
    public ItemCatalogInfo(bool built, string side, int count, int withDisplayName, int custom, double buildMilliseconds, int epoch, int builds)
    {
        Built = built;
        Side = side ?? "";
        Count = count;
        WithDisplayName = withDisplayName;
        Custom = custom;
        BuildMilliseconds = buildMilliseconds;
        Epoch = epoch;
        Builds = builds;
    }

    /// <summary>False until a build with a world produced at least one entry.</summary>
    public bool Built { get; }

    /// <summary><c>server</c> or <c>client</c>: the world the catalog was read from.</summary>
    public string Side { get; }

    /// <summary>Entries in the catalog.</summary>
    public int Count { get; }

    /// <summary>Entries whose <see cref="ItemCatalogEntry.DisplayName"/> is set.</summary>
    public int WithDisplayName { get; }

    /// <summary>Entries with <see cref="ItemCatalogEntry.IsCustom"/>.</summary>
    public int Custom { get; }

    /// <summary>Wall time of the last build.</summary>
    public double BuildMilliseconds { get; }

    /// <summary>The world-load epoch the catalog belongs to.</summary>
    public int Epoch { get; }

    /// <summary>Builds since Core loaded (a rebuild per world load or content change is expected; one per call is not).</summary>
    public int Builds { get; }
}

/// <summary>One raw row from the game, before the catalog normalizes it.</summary>
internal readonly struct ItemCatalogRow
{
    internal ItemCatalogRow(int prefabGuid, string? prefabName, string? displayName, int maxAmount, long itemCategory, int itemType, bool isCustom)
    {
        PrefabGuid = prefabGuid;
        PrefabName = prefabName;
        DisplayName = displayName;
        MaxAmount = maxAmount;
        ItemCategory = itemCategory;
        ItemType = itemType;
        IsCustom = isCustom;
    }

    internal int PrefabGuid { get; }
    internal string? PrefabName { get; }
    internal string? DisplayName { get; }
    internal int MaxAmount { get; }
    internal long ItemCategory { get; }
    internal int ItemType { get; }
    internal bool IsCustom { get; }
}

/// <summary>Where the catalog reads item prefabs from. The game implementation lives in <c>UnityItemCatalogSource</c>.</summary>
internal interface IItemCatalogSource
{
    /// <summary>The current world object, or null when there is none (main menu, before load).</summary>
    object? World { get; }

    /// <summary><c>server</c> or <c>client</c>.</summary>
    string Side { get; }

    /// <summary>The world-load epoch; a change rebuilds.</summary>
    int Epoch { get; }

    /// <summary>Bumped when a custom item is applied or removed; a change rebuilds.</summary>
    int ContentVersion { get; }

    /// <summary>Append every item prefab of the current world.</summary>
    void Collect(List<ItemCatalogRow> into);
}

/// <summary>
/// Builds the item catalog once per (world, epoch, content version) and serves it until one of
/// them changes. Game-free: the source is the only game-touching part. Main thread.
/// </summary>
internal sealed class ItemCatalogCache
{
    private static readonly IReadOnlyList<ItemCatalogEntry> Empty = Array.AsReadOnly(Array.Empty<ItemCatalogEntry>());

    private readonly IItemCatalogSource _source;
    private readonly object _gate = new();
    private readonly List<ItemCatalogRow> _scratch = new();
    private IReadOnlyList<ItemCatalogEntry> _entries = Empty;
    private Dictionary<int, ItemCatalogEntry> _byGuid = new();
    private object? _world;
    private int _epoch;
    private int _contentVersion;
    private bool _valid;
    private ItemCatalogInfo _info;
    private int _builds;

    internal ItemCatalogCache(IItemCatalogSource source) =>
        _source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>The catalog for the current world; empty when there is no world (nothing is cached then).</summary>
    internal IReadOnlyList<ItemCatalogEntry> Entries()
    {
        lock (_gate)
        {
            Refresh();
            return _entries;
        }
    }

    /// <summary>The entry for <paramref name="prefabGuid"/>, or null when it is not an item in the current world.</summary>
    internal ItemCatalogEntry? Find(int prefabGuid)
    {
        lock (_gate)
        {
            Refresh();
            return _byGuid.TryGetValue(prefabGuid, out var entry) ? entry : null;
        }
    }

    /// <summary>Summary of the last build. Does not build.</summary>
    internal ItemCatalogInfo Info
    {
        get { lock (_gate) return _info; }
    }

    /// <summary>Drop the cached catalog; the next read rebuilds.</summary>
    internal void Invalidate()
    {
        lock (_gate) _valid = false;
    }

    private void Refresh()
    {
        var world = _source.World;
        if (world == null)
        {
            Clear();
            return;
        }

        int epoch = _source.Epoch;
        int content = _source.ContentVersion;
        if (_valid && ReferenceEquals(world, _world) && epoch == _epoch && content == _contentVersion)
            return;

        var watch = Stopwatch.StartNew();
        _scratch.Clear();
        try
        {
            _source.Collect(_scratch);
        }
        catch
        {
            _scratch.Clear();
        }

        var entries = Normalize(_scratch, out var byGuid, out int named, out int custom);
        _scratch.Clear();
        watch.Stop();
        _builds++;

        if (entries.Count == 0)
        {
            // The world exists but its prefabs are not loaded yet: serve empty, cache nothing,
            // and build again on the next call.
            Clear();
            _info = new ItemCatalogInfo(false, _source.Side, 0, 0, 0, watch.Elapsed.TotalMilliseconds, epoch, _builds);
            return;
        }

        _entries = entries;
        _byGuid = byGuid;
        _world = world;
        _epoch = epoch;
        _contentVersion = content;
        _valid = true;
        _info = new ItemCatalogInfo(true, _source.Side, entries.Count, named, custom, watch.Elapsed.TotalMilliseconds, epoch, _builds);
    }

    private void Clear()
    {
        _entries = Empty;
        _byGuid = new Dictionary<int, ItemCatalogEntry>();
        _world = null;
        _valid = false;
    }

    /// <summary>
    /// Drop GUID 0, keep the first row of each GUID, and sort by prefab name (ordinal) then GUID so
    /// two builds of the same world list the same order.
    /// </summary>
    internal static IReadOnlyList<ItemCatalogEntry> Normalize(List<ItemCatalogRow> rows, out Dictionary<int, ItemCatalogEntry> byGuid, out int named, out int custom)
    {
        byGuid = new Dictionary<int, ItemCatalogEntry>(rows.Count);
        var list = new List<ItemCatalogEntry>(rows.Count);
        named = 0;
        custom = 0;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            if (row.PrefabGuid == 0 || byGuid.ContainsKey(row.PrefabGuid)) continue;
            var entry = new ItemCatalogEntry(row.PrefabGuid, row.PrefabName ?? "", row.DisplayName, row.MaxAmount, row.ItemCategory, row.ItemType, row.IsCustom);
            byGuid.Add(entry.PrefabGuid, entry);
            list.Add(entry);
            if (entry.DisplayName != null) named++;
            if (entry.IsCustom) custom++;
        }

        list.Sort(static (a, b) =>
        {
            int c = string.CompareOrdinal(a.PrefabName, b.PrefabName);
            return c != 0 ? c : a.PrefabGuid.CompareTo(b.PrefabGuid);
        });
        return list.AsReadOnly();
    }

    /// <summary>The entries that <see cref="ItemCatalogEntry.Matches"/> <paramref name="filter"/>, in catalog order.</summary>
    internal static List<ItemCatalogEntry> Filter(IReadOnlyList<ItemCatalogEntry> entries, string? filter)
    {
        var result = new List<ItemCatalogEntry>();
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Matches(filter))
                result.Add(entries[i]);
        }
        return result;
    }
}
