using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Progenitor.Ui;

/// <summary>Local build history contains prefab identities, never world entity IDs.</summary>
internal sealed class BuildRecentHistory
{
    internal const int DefaultCount = 10;
    internal const int MaximumCount = 50;
    private readonly List<int> _items = new();
    internal IReadOnlyList<int> Items => _items;
    internal int Revision { get; private set; }

    internal void Load(string value, int limit)
    {
        _items.Clear();
        limit = Math.Clamp(limit, 1, MaximumCount);
        foreach (string word in (value ?? "").Split(','))
        {
            if (int.TryParse(word, NumberStyles.Integer, CultureInfo.InvariantCulture, out int guid)
                && guid != 0 && !_items.Contains(guid)) _items.Add(guid);
            if (_items.Count == limit) break;
        }
        Revision++;
    }

    internal bool Record(int guid, int limit)
    {
        if (guid == 0) return false;
        if (_items.Count > 0 && _items[0] == guid) return Trim(limit);
        _items.Remove(guid);
        _items.Insert(0, guid);
        Trim(limit);
        Revision++;
        return true;
    }

    internal bool Trim(int limit)
    {
        limit = Math.Clamp(limit, 1, MaximumCount);
        if (_items.Count <= limit) return false;
        _items.RemoveRange(limit, _items.Count - limit);
        Revision++;
        return true;
    }

    internal string Save() => string.Join(",", _items.Select(g => g.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// The Recent row's visual order: the indexes of <paramref name="cellGuids"/> (the native cells' prefabs, in cell
    /// order) newest first, as <paramref name="recent"/> lists them. Cells with no recent prefab are left out.
    /// </summary>
    internal static List<int> SiblingOrder(IReadOnlyList<int> recent, IReadOnlyList<int> cellGuids)
    {
        var order = new List<int>();
        foreach (int guid in recent)
            for (int i = 0; i < cellGuids.Count; i++)
                if (guid != 0 && cellGuids[i] == guid) order.Add(i);
        return order;
    }

    internal List<BuildMenuBlob.ResultCell> Cells(BuildMenuBlob blob, Func<int, bool> matches)
    {
        var cells = new List<BuildMenuBlob.ResultCell>();
        foreach (int guid in _items)
        {
            if (!matches(guid)) continue;
            foreach (var structure in blob.Structures)
            {
                int i = Array.IndexOf(structure.Blueprints, guid);
                if (i < 0) continue;
                cells.Add(new BuildMenuBlob.ResultCell(0, new[] { structure.BlueprintAt[i] }));
                break;
            }
        }
        return cells;
    }
}

/// <summary>Writes the history file at most once per <see cref="DelayMs"/>: the first change starts the wait.</summary>
internal sealed class BuildRecentSaveDebounce
{
    internal const long DelayMs = 3000;
    private long _due = -1;

    internal bool Pending => _due >= 0;
    internal void Mark(long now) { if (_due < 0) _due = now + DelayMs; }
    internal bool Due(long now) => _due >= 0 && now >= _due;
    internal void Saved() => _due = -1;
}

/// <summary>Search and its count occupy one measured band clear of the variant selector.</summary>
internal static class BuildSearchPlacement
{
    internal static (float X, float Y) Place(float left, float right, float rowTop,
        float fieldWidth, float countWidth, bool selectorShown, float selectorLeft, float selectorTop)
    {
        float width = fieldWidth + 10 + countWidth;
        float x = left + fieldWidth / 2;
        float y = rowTop + 14;
        if (selectorShown && left + width + 16 > selectorLeft) y = Math.Max(y, selectorTop + 14);
        // Tiny layouts still anchor to their left edge; the field width is reduced by the caller.
        return (x, y);
    }
}
