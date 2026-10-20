using System;
using System.Collections.Generic;

namespace Progenitor.Perf;

/// <summary>
/// A key-to-values index rebuilt a few sources per frame instead of all at once. A rebuild takes a list
/// of sources, classifies them under <see cref="FrameBudget"/>s across as many frames as it needs, and
/// swaps the finished index in at the end; until then the previous index keeps answering (stale by at
/// most one rebuild). Game-free; main thread only.
/// </summary>
internal sealed class IncrementalGrouping<TSource, TKey, TValue> where TKey : notnull
{
    private readonly Func<TSource, (bool Keep, TKey Key, TValue Value)> _classify;
    private readonly Comparison<TValue>? _order;
    private Dictionary<TKey, List<TValue>>? _current;
    private Dictionary<TKey, List<TValue>>? _building;
    private IReadOnlyList<TSource>? _pending;
    private int _cursor;
    private int _currentEpoch = int.MinValue;
    private int _buildingEpoch = int.MinValue;
    private long _currentBuiltMs;
    private long _buildingStartedMs;

    /// <param name="classify">Whether a source belongs in the index, and under which key with which value. A throw drops that source.</param>
    /// <param name="order">The order of each key's values once a rebuild finishes (a stable listing); none when null.</param>
    internal IncrementalGrouping(Func<TSource, (bool Keep, TKey Key, TValue Value)> classify, Comparison<TValue>? order = null)
    {
        _classify = classify ?? throw new ArgumentNullException(nameof(classify));
        _order = order;
    }

    /// <summary>A rebuild is in progress.</summary>
    internal bool Building => _pending != null;

    /// <summary>Sources the rebuild in progress has not classified yet (0 when none is).</summary>
    internal int Remaining => _pending == null ? 0 : _pending.Count - _cursor;

    /// <summary>Rebuilds finished (diagnostics and tests).</summary>
    internal int Builds { get; private set; }

    /// <summary>An index of <paramref name="epoch"/> is available.</summary>
    internal bool Ready(int epoch) => _current != null && _currentEpoch == epoch;

    /// <summary>
    /// A rebuild should start: none is running and there is no index of <paramref name="epoch"/> yet, or
    /// the current one is <paramref name="refreshMs"/> old.
    /// </summary>
    internal bool Due(int epoch, long nowMs, long refreshMs) =>
        !Building && (_current == null || _currentEpoch != epoch || nowMs - _currentBuiltMs >= refreshMs);

    /// <summary>Start a rebuild over <paramref name="sources"/> (replaces one in progress).</summary>
    internal void Begin(IReadOnlyList<TSource> sources, int epoch, long nowMs)
    {
        _pending = sources ?? Array.Empty<TSource>();
        _cursor = 0;
        _building = new Dictionary<TKey, List<TValue>>();
        _buildingEpoch = epoch;
        _buildingStartedMs = nowMs;
    }

    /// <summary>
    /// Classify sources until <paramref name="budget"/> is used up (at least one when any is left).
    /// True when the rebuild finished in this call and its index replaced the current one.
    /// </summary>
    internal bool Step(FrameBudget budget)
    {
        if (_pending == null || _building == null) return false;
        int done = 0;
        while (_cursor < _pending.Count)
        {
            if (done > 0 && budget.Exhausted) return false;
            var source = _pending[_cursor++];
            done++;
            (bool Keep, TKey Key, TValue Value) row;
            try { row = _classify(source); }
            catch { continue; }
            if (!row.Keep) continue;
            if (!_building.TryGetValue(row.Key, out var list)) _building[row.Key] = list = new List<TValue>();
            list.Add(row.Value);
        }
        if (_order != null) foreach (var list in _building.Values) list.Sort(_order);
        _current = _building;
        _currentEpoch = _buildingEpoch;
        // The age counts from the start of the rebuild: its first sources are that old.
        _currentBuiltMs = _buildingStartedMs;
        _building = null;
        _pending = null;
        _cursor = 0;
        Builds++;
        return true;
    }

    /// <summary>The values of <paramref name="key"/> in the current index of <paramref name="epoch"/>; false when there is none.</summary>
    internal bool TryGet(int epoch, TKey key, out IReadOnlyList<TValue> values)
    {
        values = Array.Empty<TValue>();
        if (!Ready(epoch)) return false;
        if (_current!.TryGetValue(key, out var list)) values = list;
        return true;
    }

    /// <summary>Drop the index and any rebuild in progress.</summary>
    internal void Clear()
    {
        _current = null;
        _building = null;
        _pending = null;
        _cursor = 0;
        _currentEpoch = int.MinValue;
    }
}
