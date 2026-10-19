using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Ui;

/// <summary>One line of an <see cref="ItemLimitList"/>: an item prefab or a category, and the most of it (0 or more).</summary>
public readonly struct ItemLimit : IEquatable<ItemLimit>
{
    private ItemLimit(int item, string category, int max)
    {
        Item = item;
        Category = category ?? "";
        Max = max;
    }

    /// <summary>The item's prefab GUID, or 0 for a category line.</summary>
    public int Item { get; }

    /// <summary>The category id (a mod-defined word), or empty for an item line.</summary>
    public string Category { get; }

    /// <summary>The count this line allows (what it means is the mod's: Autoloot stops picking the item up there).</summary>
    public int Max { get; }

    public bool IsCategory => Item == 0;

    public static ItemLimit ForItem(int prefabGuid, int max)
    {
        if (prefabGuid == 0) throw new ArgumentException("An item prefab GUID is required.", nameof(prefabGuid));
        if (max < 0) throw new ArgumentOutOfRangeException(nameof(max));
        return new ItemLimit(prefabGuid, "", max);
    }

    public static ItemLimit ForCategory(string category, int max)
    {
        string id = (category ?? "").Trim();
        if (!ItemLimitList.IsCategoryId(id)) throw new ArgumentException(ItemLimitList.CategoryRule, nameof(category));
        if (max < 0) throw new ArgumentOutOfRangeException(nameof(max));
        return new ItemLimit(0, id, max);
    }

    /// <summary>The line's identity: <c>i:&lt;guid&gt;</c> or <c>c:&lt;category&gt;</c> (lowercase).</summary>
    public string Key => IsCategory ? "c:" + Category.ToLowerInvariant() : "i:" + Item.ToString(CultureInfo.InvariantCulture);

    public bool Equals(ItemLimit other) => Item == other.Item && Max == other.Max
        && string.Equals(Category, other.Category, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ItemLimit other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Item, Category, Max);

    public override string ToString() => (IsCategory ? "c:" + Category : Item.ToString(CultureInfo.InvariantCulture))
        + "=" + Max.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// An immutable list of item or category limits: the value of an item-list setting (<c>ctx.Settings.ItemList</c>,
/// <c>ctx.Settings.PerPlayerItemList</c>). Stored and sent as text: lines separated by <c>;</c>, each
/// <c>&lt;item guid&gt;=&lt;max&gt;</c> or <c>c:&lt;category&gt;=&lt;max&gt;</c>, for example
/// <c>-1531666018=50;c:gems=0</c>. At most <see cref="MaxEntries"/> lines and <see cref="MaxEncodedBytes"/> bytes of
/// text; one line per item or category.
/// </summary>
public sealed class ItemLimitList : IReadOnlyList<ItemLimit>, IEquatable<ItemLimitList>
{
    /// <summary>The most lines any item list holds.</summary>
    public const int MaxEntries = 256;

    /// <summary>
    /// The longest encoded list, in UTF-8 bytes: one settings field on the wire. 256 item lines with counts up to 999 always
    /// fit; larger counts or long category names reach this limit first, and such a list is refused.
    /// </summary>
    public const int MaxEncodedBytes = 4096;

    /// <summary>The longest category id.</summary>
    public const int MaxCategoryLength = 32;

    internal const string CategoryRule = "A category is 1 to 32 letters, digits, '_', '-' or '.'.";

    private readonly ItemLimit[] _items;

    private ItemLimitList(ItemLimit[] items) => _items = items;

    public static ItemLimitList Empty { get; } = new(Array.Empty<ItemLimit>());

    /// <summary>A list of <paramref name="lines"/> without any check (an editor's candidate, checked before it is sent).</summary>
    internal static ItemLimitList Unchecked(IEnumerable<ItemLimit> lines) => new(new List<ItemLimit>(lines).ToArray());

    /// <summary>A list of <paramref name="lines"/>; throws when it breaks the general rules (duplicates, too long).</summary>
    public static ItemLimitList Of(IEnumerable<ItemLimit> lines)
    {
        if (lines == null) throw new ArgumentNullException(nameof(lines));
        var list = new ItemLimitList(new List<ItemLimit>(lines).ToArray());
        if (list.Check(ItemListRules.Structural) is { } error) throw new ArgumentException(error, nameof(lines));
        return list;
    }

    public int Count => _items.Length;

    public ItemLimit this[int index] => _items[index];

    public IEnumerator<ItemLimit> GetEnumerator() => ((IEnumerable<ItemLimit>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    /// <summary>The item line for <paramref name="prefabGuid"/>'s limit, or null when the list does not name the item.</summary>
    public int? MaxForItem(int prefabGuid)
    {
        foreach (var line in _items) if (!line.IsCategory && line.Item == prefabGuid) return line.Max;
        return null;
    }

    /// <summary>The category line's limit, or null when the list does not name the category (compared without case).</summary>
    public int? MaxForCategory(string category)
    {
        foreach (var line in _items)
            if (line.IsCategory && string.Equals(line.Category, category, StringComparison.OrdinalIgnoreCase)) return line.Max;
        return null;
    }

    /// <summary>A copy with <paramref name="line"/> added, or replacing the line for the same item or category.</summary>
    public ItemLimitList With(ItemLimit line)
    {
        var next = new List<ItemLimit>(_items);
        int at = next.FindIndex(l => l.Key == line.Key);
        if (at >= 0) next[at] = line;
        else next.Add(line);
        return Of(next);
    }

    /// <summary>A copy without the line whose <see cref="ItemLimit.Key"/> is <paramref name="key"/>.</summary>
    public ItemLimitList Without(string key)
    {
        var next = new List<ItemLimit>(_items);
        next.RemoveAll(l => l.Key == key);
        return new ItemLimitList(next.ToArray());
    }

    /// <summary>The stored text (see the class summary).</summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _items.Length; i++)
        {
            if (i > 0) sb.Append(';');
            sb.Append(_items[i].ToString());
        }
        return sb.ToString();
    }

    /// <summary>Parse stored text. False with a reason for malformed text or a list that breaks the general rules.</summary>
    public static bool TryParse(string? text, out ItemLimitList list, out string error) =>
        TryParse(text, ItemListRules.Structural, out list, out error);

    /// <summary>Parse and check against <paramref name="rules"/> (the server's own validation of every write).</summary>
    public static bool TryParse(string? text, ItemListRules rules, out ItemLimitList list, out string error)
    {
        list = Empty;
        error = "";
        rules ??= ItemListRules.Structural;
        string value = (text ?? "").Trim();
        if (value.Length == 0) return true;
        if (Encoding.UTF8.GetByteCount(value) > MaxEncodedBytes)
        {
            error = TooLarge(Encoding.UTF8.GetByteCount(value));
            return false;
        }

        var parts = value.Split(';');
        if (parts.Length > MaxEntries)
        {
            error = "At most " + Math.Min(rules.MaxEntries, MaxEntries).ToString(CultureInfo.InvariantCulture) + " lines.";
            return false;
        }

        var items = new ItemLimit[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            int eq = part.LastIndexOf('=');
            if (eq <= 0 || !int.TryParse(part.Substring(eq + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int max))
            {
                error = "Line " + (i + 1).ToString(CultureInfo.InvariantCulture) + " is not <item>=<count> or c:<category>=<count>.";
                return false;
            }

            string what = part.Substring(0, eq).Trim();
            if (max < 0)
            {
                error = "Counts are 0 or more.";
                return false;
            }

            if (what.StartsWith("c:", StringComparison.OrdinalIgnoreCase))
            {
                string id = what.Substring(2).Trim();
                if (!IsCategoryId(id))
                {
                    error = CategoryRule;
                    return false;
                }
                items[i] = ItemLimit.ForCategory(id, max);
            }
            else if (int.TryParse(what, NumberStyles.Integer, CultureInfo.InvariantCulture, out int guid) && guid != 0)
                items[i] = ItemLimit.ForItem(guid, max);
            else
            {
                error = "Line " + (i + 1).ToString(CultureInfo.InvariantCulture) + " names no item.";
                return false;
            }
        }

        var parsed = new ItemLimitList(items);
        if (parsed.Check(rules) is { } problem)
        {
            error = problem;
            return false;
        }

        list = parsed;
        return true;
    }

    /// <summary>Null when the list keeps <paramref name="rules"/>, else the reason.</summary>
    public string? Check(ItemListRules rules)
    {
        rules ??= ItemListRules.Structural;
        if (_items.Length > Math.Min(rules.MaxEntries, MaxEntries))
            return "At most " + Math.Min(rules.MaxEntries, MaxEntries).ToString(CultureInfo.InvariantCulture) + " lines.";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in _items)
        {
            if (!seen.Add(line.Key)) return (line.IsCategory ? "The category " + line.Category : "The item " + line.Item.ToString(CultureInfo.InvariantCulture)) + " is listed twice.";
            if (line.Max < 0 || line.Max > rules.MaxCount) return "Counts are 0 to " + rules.MaxCount.ToString("#,0", CultureInfo.InvariantCulture) + ".";
            if (line.IsCategory && !rules.AllowCategories) return "This list takes items only.";
            if (line.IsCategory && !IsCategoryId(line.Category)) return CategoryRule;
        }

        int bytes = Encoding.UTF8.GetByteCount(ToString());
        return bytes > MaxEncodedBytes ? TooLarge(bytes) : null;
    }

    internal static string TooLarge(int bytes) => "The list is too long to share: " + bytes.ToString("#,0", CultureInfo.InvariantCulture)
        + " bytes, at most " + MaxEncodedBytes.ToString("#,0", CultureInfo.InvariantCulture) + ". Remove some lines.";

    internal static bool IsCategoryId(string? id)
    {
        if (string.IsNullOrEmpty(id) || id!.Length > MaxCategoryLength) return false;
        foreach (char c in id)
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.')) return false;
        return true;
    }

    public bool Equals(ItemLimitList? other)
    {
        if (other == null || other.Count != Count) return false;
        for (int i = 0; i < _items.Length; i++) if (!_items[i].Equals(other._items[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is ItemLimitList other && Equals(other);

    public override int GetHashCode() => ToString().GetHashCode();
}

/// <summary>What an item-list setting accepts. The server checks every write against its own copy.</summary>
public sealed class ItemListRules
{
    /// <summary>Only the general rules (lines, duplicates, size): any count, categories allowed.</summary>
    internal static readonly ItemListRules Structural = new() { MaxCount = int.MaxValue, AllowCategories = true };

    /// <summary>The most lines (1 to <see cref="ItemLimitList.MaxEntries"/>).</summary>
    public int MaxEntries { get; init; } = ItemLimitList.MaxEntries;

    /// <summary>The largest count a line may hold.</summary>
    public int MaxCount { get; init; } = 100_000;

    /// <summary>True when lines may name categories as well as items.</summary>
    public bool AllowCategories { get; init; }

    /// <summary>
    /// On the server, when <see cref="AllowCategories"/>: the categories the item picker offers, as (id, display name),
    /// read each time the definition is sent and once a second while players are connected. At most 64 are sent.
    /// A stored line may name a category the provider no longer lists; it is kept.
    /// </summary>
    public Func<IReadOnlyList<KeyValuePair<string, string>>>? Categories { get; init; }

    /// <summary>The word for the count column (default <c>Most</c>).</summary>
    public string CountLabel { get; init; } = "Most";

    /// <summary>
    /// True when the list takes only items that stack (a stack size above 1 in the item catalog,
    /// <see cref="Progenitor.Items.ItemCatalogEntry.MaxAmount"/>): the item picker offers only those, and the server refuses
    /// an edit that adds any other item (or one its catalog does not know). Lines already stored are kept. Categories are
    /// not affected. The server checks edits only; a default list is not checked against the catalog.
    /// </summary>
    public bool StackableOnly { get; init; }

    /// <summary>
    /// PICKFRONT1: null when every item line of <paramref name="list"/> that is not already in <paramref name="kept"/>
    /// stacks by <paramref name="stackSize"/> (a prefab's stack size, null when unknown), else the reason. Always null
    /// without <see cref="StackableOnly"/>.
    /// </summary>
    internal string? CheckStackable(ItemLimitList list, ItemLimitList? kept, Func<int, int?> stackSize)
    {
        if (!StackableOnly || list == null) return null;
        foreach (var line in list)
        {
            if (line.IsCategory || kept?.MaxForItem(line.Item) != null) continue;
            int? size = null;
            try { size = stackSize?.Invoke(line.Item); }
            catch { size = null; }
            string item = "The item " + line.Item.ToString(CultureInfo.InvariantCulture);
            if (size == null) return item + " could not be checked; this list takes stackable items only.";
            if (size.Value <= 1) return item + " does not stack; this list takes stackable items only.";
        }
        return null;
    }

    /// <summary>PICKFRONT1: whether the picker offers an item of stack size <paramref name="stackSize"/>.</summary>
    internal static bool Offers(bool stackableOnly, int stackSize) => !stackableOnly || stackSize > 1;

    internal void Validate()
    {
        if (MaxEntries < 1 || MaxEntries > ItemLimitList.MaxEntries)
            throw new ArgumentOutOfRangeException(nameof(MaxEntries), "1 to " + ItemLimitList.MaxEntries + " lines.");
        if (MaxCount < 0) throw new ArgumentOutOfRangeException(nameof(MaxCount));
        if (string.IsNullOrWhiteSpace(CountLabel) || CountLabel.Length > 32) throw new ArgumentException("A count label of 1 to 32 characters.", nameof(CountLabel));
    }
}

/// <summary>
/// The item-list settings of this process by row id (<c>plugin/section/key</c>), with their rules: the server's
/// validation of every write, and the hints it sends. Each registration is withdrawn on plugin unload.
/// </summary>
internal static class ItemListSettings
{
    private static readonly Dictionary<string, ItemListRules> Rules = new(StringComparer.Ordinal);

    internal static IDisposable Register(string rowId, ItemListRules rules)
    {
        if (string.IsNullOrEmpty(rowId)) throw new ArgumentException("A setting id is required.", nameof(rowId));
        if (rules == null) throw new ArgumentNullException(nameof(rules));
        rules.Validate();
        if (Rules.ContainsKey(rowId)) throw new ArgumentException("This item list is already registered: " + rowId);
        Rules[rowId] = rules;
        return new Lease(rowId, rules);
    }

    internal static bool TryGet(string? rowId, out ItemListRules rules)
    {
        rules = null!;
        return rowId != null && Rules.TryGetValue(rowId, out rules!);
    }

    internal static bool Has(string? rowId) => rowId != null && Rules.ContainsKey(rowId);

    /// <summary>The canonical text of a write, or the reason it is refused.</summary>
    internal static bool TryCanonical(ItemListRules rules, string? value, out string canonical, out string error) =>
        TryCanonical(rules, value, null, out canonical, out error);

    /// <summary>
    /// As above, replacing <paramref name="current"/> (the stored text): with <see cref="ItemListRules.StackableOnly"/>,
    /// an item line that is not already stored must stack in this side's item catalog (<see cref="StackSize"/>).
    /// </summary>
    internal static bool TryCanonical(ItemListRules rules, string? value, string? current, out string canonical, out string error)
    {
        canonical = "";
        if (!ItemLimitList.TryParse(value, rules, out var list, out error)) return false;
        if (rules.StackableOnly)
        {
            var kept = ItemLimitList.TryParse(current, out var stored, out _) ? stored : null;
            if (rules.CheckStackable(list, kept, StackSize) is { } problem)
            {
                error = problem;
                return false;
            }
        }
        canonical = list.ToString();
        return true;
    }

    /// <summary>
    /// PICKFRONT1: a prefab's stack size from this side's item catalog, null when the catalog does not know it. A seam:
    /// tests supply their own.
    /// </summary>
    internal static Func<int, int?> StackSize = DefaultStackSize;

#if !PROGENITOR_TESTS
    private static IReadOnlyList<Progenitor.Items.ItemCatalogEntry>? _stackSource;
    private static Dictionary<int, int>? _stackByGuid;
#endif

    private static int? DefaultStackSize(int prefabGuid)
    {
#if PROGENITOR_TESTS
        return null;
#else
        IReadOnlyList<Progenitor.Items.ItemCatalogEntry> entries;
        try { entries = Progenitor.Items.ItemCatalogBridge.Entries(); }
        catch { return null; }
        // The catalog hands out the same list until it is rebuilt, so the index is rebuilt with it.
        if (!ReferenceEquals(entries, _stackSource) || _stackByGuid == null)
        {
            var index = new Dictionary<int, int>(entries.Count);
            foreach (var e in entries) index[e.PrefabGuid] = e.MaxAmount;
            _stackByGuid = index;
            _stackSource = entries;
        }
        return _stackByGuid.TryGetValue(prefabGuid, out int size) ? size : null;
#endif
    }

    internal static void ResetForTests()
    {
        Rules.Clear();
        StackSize = DefaultStackSize;
    }

    private sealed class Lease : IDisposable
    {
        private string? _id;
        private readonly ItemListRules _rules;
        internal Lease(string id, ItemListRules rules) { _id = id; _rules = rules; }
        public void Dispose()
        {
            if (_id != null && Rules.TryGetValue(_id, out var current) && ReferenceEquals(current, _rules)) Rules.Remove(_id);
            _id = null;
        }
    }
}
