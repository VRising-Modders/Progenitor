using System;
using System.Collections.Generic;
using System.Linq;

namespace Progenitor.SharedResourcesTest;

// This oracle never pays for an operation. It predicts debits, then compares independent reads.
public sealed class Fixture
{
    private readonly Dictionary<int, Dictionary<string, int>> _ranks = new();
    private readonly Dictionary<int, int> _reserves = new();
    private readonly Dictionary<int, int> _costs = new();
    private Dictionary<(string Source, int Item), int>? _before;
    private Dictionary<(string Source, int Item), int>? _expected;
    public long Revision { get; private set; }
    public bool Active => _before != null;
    public bool ExpectedSuccess { get; private set; }
    public IReadOnlyDictionary<int, int> Costs => _costs;

    public void Rank(int item, string source, int depositRank)
    {
        Editable();
        if (item == 0 || string.IsNullOrWhiteSpace(source) || depositRank < 0)
            throw new ArgumentException("Use a nonzero item, source alias and nonnegative deposit rank (0 is best).");
        if (!_ranks.TryGetValue(item, out var rows)) _ranks[item] = rows = new();
        rows[source] = depositRank;
        Revision++;
    }

    public void Cost(int item, int amount, int reserve)
    {
        Editable();
        if (item == 0 || amount <= 0 || reserve < 0) throw new ArgumentException("Cost must be positive and reserve nonnegative.");
        _costs[item] = amount;
        _reserves[item] = reserve;
        Revision++;
    }

    public string[] Order(int item) => _ranks.TryGetValue(item, out var rows)
        ? rows.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key).ToArray()
        : Array.Empty<string>();
    public int Reserve(int item) => _reserves.TryGetValue(item, out int n) ? n : 0;

    public void Begin(IEnumerable<string> allSources, Func<string, int, int> count, bool expectPayment = true)
    {
        Editable();
        if (_costs.Count == 0) throw new InvalidOperationException("Configure every ingredient cost first.");
        var sources = allSources.ToArray();
        if (sources.Length == 0 || sources.Distinct(StringComparer.Ordinal).Count() != sources.Length)
            throw new InvalidOperationException("Unique source aliases are required.");
        var before = new Dictionary<(string, int), int>();
        var expected = new Dictionary<(string, int), int>();
        bool enough = true;
        foreach (var cost in _costs)
        {
            var order = Order(cost.Key);
            if (order.Length == 0 || order.Any(s => !sources.Contains(s, StringComparer.Ordinal)))
                throw new InvalidOperationException("Each ingredient must name bound source aliases.");
            foreach (string source in sources)
            {
                int n = count(source, cost.Key);
                if (n < 0) throw new InvalidOperationException("Unreadable or negative inventory count.");
                before[(source, cost.Key)] = n;
                expected[(source, cost.Key)] = 0;
            }
            long total = order.Sum(s => (long)before[(s, cost.Key)]);
            if (total - Reserve(cost.Key) < cost.Value) enough = false;
            int left = cost.Value;
            foreach (string source in order)
            {
                int take = Math.Min(left, before[(source, cost.Key)]);
                expected[(source, cost.Key)] = take;
                left -= take;
            }
        }
        // An insufficient ingredient means ZERO debit for the entire request.
        if (!enough || !expectPayment) foreach (var key in expected.Keys.ToArray()) expected[key] = 0;
        _before = before;
        _expected = expected;
        ExpectedSuccess = enough && expectPayment;
    }

    public string[] Verify(Func<string, int, int> count)
    {
        if (_before == null || _expected == null) throw new InvalidOperationException("Run begin before verify.");
        var errors = new List<string>();
        foreach (var row in _before)
        {
            int after = count(row.Key.Source, row.Key.Item);
            long debit = (long)row.Value - after;
            int expected = _expected[row.Key];
            if (after < 0 || debit != expected)
                errors.Add($"{row.Key.Source} item={row.Key.Item}: before={row.Value} after={after} debit={debit} expected={expected}");
        }
        return errors.ToArray();
    }

    public string[] Baseline() => _before == null ? Array.Empty<string>() : _before.Select(x =>
        $"{x.Key.Source} item={x.Key.Item} before={x.Value} expectedDebit={_expected![x.Key]}").ToArray();

    public void End() { _before = null; _expected = null; }
    public void Clear() { End(); _ranks.Clear(); _costs.Clear(); _reserves.Clear(); Revision++; }
    private void Editable() { if (Active) throw new InvalidOperationException("End the active capture before changing its configuration."); }
}
