using System;
using System.Collections.Generic;

namespace Progenitor.Materials;

/// <summary>
/// The physical side the planner drives: count, take and give back one item in one inventory.
/// Production uses the server's inventory code (<c>UnityMaterialLedger</c>); tests use a dictionary.
/// Every call happens inside one synchronous main-thread step.
/// </summary>
internal interface IMaterialLedger<TSource> where TSource : IEquatable<TSource>
{
    /// <summary>Units of <paramref name="item"/> in <paramref name="source"/> that the pool may spend (0 when unreadable).</summary>
    int Count(TSource source, int item);

    /// <summary>Take up to <paramref name="amount"/>; returns what was actually taken (read back).</summary>
    int Remove(TSource source, int item, int amount);

    /// <summary>Give back up to <paramref name="amount"/>; returns what was actually added (read back).</summary>
    int Add(TSource source, int item, int amount);
}

/// <summary>One cost line: an item (PrefabGUID hash) and an amount.</summary>
internal readonly struct MaterialLine : IEquatable<MaterialLine>
{
    internal int Item { get; }
    internal int Amount { get; }

    internal MaterialLine(int item, int amount)
    {
        Item = item;
        Amount = amount;
    }

    public bool Equals(MaterialLine other) => Item == other.Item && Amount == other.Amount;
    public override bool Equals(object? obj) => obj is MaterialLine o && Equals(o);
    public override int GetHashCode() => HashCode.Combine(Item, Amount);
    public override string ToString() => Item + "x" + Amount;
}

/// <summary>Where one item may come from: inventories in spend order, the pool reserve, and inventories to leave out.</summary>
internal sealed class MaterialPoolSpec<TSource> where TSource : IEquatable<TSource>
{
    internal IReadOnlyList<TSource> Order { get; }
    internal int Reserve { get; }

    internal MaterialPoolSpec(IReadOnlyList<TSource> order, int reserve)
    {
        Order = order ?? throw new ArgumentNullException(nameof(order));
        Reserve = reserve < 0 ? 0 : reserve;
    }
}

/// <summary>The pool for one item, after dedup and exclusion, with what each inventory holds now.</summary>
internal sealed class MaterialPool<TSource> where TSource : IEquatable<TSource>
{
    internal int Item { get; }
    internal IReadOnlyList<(TSource Source, int Count)> Rows { get; }
    internal long Total { get; }
    internal int Reserve { get; }

    /// <summary><see cref="Total"/> minus the reserve, never negative, capped at <see cref="int.MaxValue"/>.</summary>
    internal int Available { get; }

    internal MaterialPool(int item, IReadOnlyList<(TSource, int)> rows, long total, int reserve)
    {
        Item = item;
        Rows = rows;
        Total = total;
        Reserve = reserve;
        long a = total - reserve;
        Available = a <= 0 ? 0 : a > int.MaxValue ? int.MaxValue : (int)a;
    }
}

/// <summary>One planned (or executed) take from one inventory.</summary>
internal readonly struct MaterialStep<TSource> where TSource : IEquatable<TSource>
{
    internal TSource Source { get; }
    internal int Item { get; }
    internal int Amount { get; }

    internal MaterialStep(TSource source, int item, int amount)
    {
        Source = source;
        Item = item;
        Amount = amount;
    }

    public override string ToString() => Source + ":" + Item + "x" + Amount;
}

/// <summary>A whole request's plan. <see cref="Ok"/> false lists the short items and plans nothing.</summary>
internal sealed class MaterialPlan<TSource> where TSource : IEquatable<TSource>
{
    internal bool Ok => Short.Count == 0;
    internal IReadOnlyList<MaterialLine> Lines { get; }
    internal IReadOnlyList<MaterialStep<TSource>> Steps { get; }

    /// <summary>Items the pool cannot cover: item, needed, available.</summary>
    internal IReadOnlyList<(int Item, int Needed, int Available)> Short { get; }

    /// <summary>Lines the provider does not govern (no pool spec): the game pays them itself.</summary>
    internal IReadOnlyList<MaterialLine> Ungoverned { get; }

    internal MaterialPlan(IReadOnlyList<MaterialLine> lines, IReadOnlyList<MaterialStep<TSource>> steps,
        IReadOnlyList<(int, int, int)> shortItems, IReadOnlyList<MaterialLine> ungoverned)
    {
        Lines = lines;
        Steps = steps;
        Short = shortItems;
        Ungoverned = ungoverned;
    }
}

/// <summary>How a commit ended.</summary>
internal enum MaterialCommitOutcome
{
    /// <summary>Every planned take happened exactly.</summary>
    Paid,

    /// <summary>The plan was not OK; nothing was touched.</summary>
    Refused,

    /// <summary>A take came back short; every take already made was given back exactly. Nothing was paid.</summary>
    RolledBack,

    /// <summary>A take came back short and giving back failed too: items are missing. Logged as an error.</summary>
    Inconsistent,
}

internal sealed class MaterialCommit<TSource> where TSource : IEquatable<TSource>
{
    internal MaterialCommitOutcome Outcome { get; }
    internal IReadOnlyList<MaterialStep<TSource>> Taken { get; }
    internal string Reason { get; }

    /// <summary>Units that could not be given back during a rollback (0 unless <see cref="MaterialCommitOutcome.Inconsistent"/>).</summary>
    internal int Unrestored { get; }

    internal bool Ok => Outcome == MaterialCommitOutcome.Paid;

    internal MaterialCommit(MaterialCommitOutcome outcome, IReadOnlyList<MaterialStep<TSource>> taken, string reason, int unrestored)
    {
        Outcome = outcome;
        Taken = taken;
        Reason = reason;
        Unrestored = unrestored;
    }
}

/// <summary>
/// The transaction planner. Pure apart from the ledger reads and writes it is handed:
/// <list type="number">
/// <item><description><see cref="Normalize"/>: merge repeated items, drop non-positive lines.</description></item>
/// <item><description><see cref="Pool"/>: one item's inventories in spend order, each physical
/// inventory once, excluded ones dropped, the reserve subtracted once from the pool total.</description></item>
/// <item><description><see cref="Plan"/>: every line planned against its pool, or nothing at all.</description></item>
/// <item><description><see cref="Commit"/>: execute the plan; any short take rolls back every take
/// already made, exactly.</description></item>
/// </list>
/// Nothing here spans frames or holds a reservation: the caller plans and commits in one
/// synchronous main-thread step, so no other consumer can spend the same stock in between.
/// </summary>
internal static class MaterialPlanner
{
    /// <summary>Merge repeated items (first appearance keeps its position) and drop lines of zero or less.</summary>
    internal static IReadOnlyList<MaterialLine> Normalize(IEnumerable<MaterialLine> lines)
    {
        if (lines == null) throw new ArgumentNullException(nameof(lines));
        var order = new List<int>();
        var sums = new Dictionary<int, long>();
        foreach (var line in lines)
        {
            if (line.Amount <= 0 || line.Item == 0) continue;
            if (!sums.TryGetValue(line.Item, out long s)) { order.Add(line.Item); s = 0; }
            sums[line.Item] = s + line.Amount;
        }
        var result = new List<MaterialLine>(order.Count);
        foreach (int item in order)
        {
            long s = sums[item];
            if (s > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(lines), "cost for item " + item + " overflows: " + s);
            result.Add(new MaterialLine(item, (int)s));
        }
        return result;
    }

    /// <summary>
    /// One item's pool: <paramref name="spec"/>'s inventories in order, each once, minus
    /// <paramref name="exclude"/>, with their current counts.
    /// </summary>
    internal static MaterialPool<TSource> Pool<TSource>(int item, MaterialPoolSpec<TSource> spec,
        IMaterialLedger<TSource> ledger, ICollection<TSource>? exclude = null)
        where TSource : IEquatable<TSource>
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        if (ledger == null) throw new ArgumentNullException(nameof(ledger));
        var seen = new HashSet<TSource>();
        var rows = new List<(TSource, int)>();
        long total = 0;
        foreach (var source in spec.Order)
        {
            if (!seen.Add(source)) continue;
            if (exclude != null && exclude.Contains(source)) continue;
            int n = ledger.Count(source, item);
            if (n <= 0) continue;
            rows.Add((source, n));
            total += n;
        }
        return new MaterialPool<TSource>(item, rows, total, spec.Reserve);
    }

    /// <summary>
    /// <see cref="Pool"/>'s <see cref="MaterialPool{TSource}.Available"/> alone, without building the
    /// rows: the same dedup, exclusion and reserve, with a caller-owned scratch set (cleared here), so a
    /// view of many items allocates nothing per item.
    /// </summary>
    internal static int Available<TSource>(int item, MaterialPoolSpec<TSource> spec, IMaterialLedger<TSource> ledger,
        ICollection<TSource>? exclude, HashSet<TSource> seen)
        where TSource : IEquatable<TSource>
    {
        if (spec == null) throw new ArgumentNullException(nameof(spec));
        if (ledger == null) throw new ArgumentNullException(nameof(ledger));
        seen.Clear();
        long total = 0;
        foreach (var source in spec.Order)
        {
            if (!seen.Add(source)) continue;
            if (exclude != null && exclude.Contains(source)) continue;
            int n = ledger.Count(source, item);
            if (n > 0) total += n;
        }
        long a = total - spec.Reserve;
        return a <= 0 ? 0 : a > int.MaxValue ? int.MaxValue : (int)a;
    }

    /// <summary>
    /// Plan every line. <paramref name="specFor"/> returns null for an item the provider does not
    /// govern; that line is listed in <see cref="MaterialPlan{TSource}.Ungoverned"/> and not planned.
    /// When any governed line is short, the plan has no steps.
    /// </summary>
    internal static MaterialPlan<TSource> Plan<TSource>(IEnumerable<MaterialLine> lines,
        Func<int, MaterialPoolSpec<TSource>?> specFor, IMaterialLedger<TSource> ledger, ICollection<TSource>? exclude = null)
        where TSource : IEquatable<TSource>
    {
        if (specFor == null) throw new ArgumentNullException(nameof(specFor));
        var normalized = Normalize(lines);
        var steps = new List<MaterialStep<TSource>>();
        var shortItems = new List<(int, int, int)>();
        var ungoverned = new List<MaterialLine>();
        foreach (var line in normalized)
        {
            var spec = specFor(line.Item);
            if (spec == null) { ungoverned.Add(line); continue; }
            var pool = Pool(line.Item, spec, ledger, exclude);
            if (pool.Available < line.Amount)
            {
                shortItems.Add((line.Item, line.Amount, pool.Available));
                continue;
            }
            int left = line.Amount;
            foreach (var (source, count) in pool.Rows)
            {
                if (left == 0) break;
                int take = Math.Min(left, count);
                steps.Add(new MaterialStep<TSource>(source, line.Item, take));
                left -= take;
            }
        }
        if (shortItems.Count > 0) steps.Clear();
        return new MaterialPlan<TSource>(normalized, steps, shortItems, ungoverned);
    }

    /// <summary>
    /// Execute <paramref name="plan"/>. All or nothing: a take that comes back short gives back
    /// every take already made (in reverse), then reports <see cref="MaterialCommitOutcome.RolledBack"/>,
    /// or <see cref="MaterialCommitOutcome.Inconsistent"/> when even that failed.
    /// </summary>
    internal static MaterialCommit<TSource> Commit<TSource>(MaterialPlan<TSource> plan, IMaterialLedger<TSource> ledger)
        where TSource : IEquatable<TSource>
    {
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (ledger == null) throw new ArgumentNullException(nameof(ledger));
        if (!plan.Ok)
            return new MaterialCommit<TSource>(MaterialCommitOutcome.Refused, Array.Empty<MaterialStep<TSource>>(),
                "short: " + DescribeShort(plan), 0);

        var taken = new List<MaterialStep<TSource>>(plan.Steps.Count);
        foreach (var step in plan.Steps)
        {
            int got;
            string? error = null;
            try { got = ledger.Remove(step.Source, step.Item, step.Amount); }
            catch (Exception e) { got = 0; error = e.Message; }
            if (got > 0) taken.Add(new MaterialStep<TSource>(step.Source, step.Item, got));
            if (got == step.Amount && error == null) continue;

            string why = "take " + step + " returned " + got + (error != null ? " (" + error + ")" : "");
            int unrestored = Restore(taken, ledger);
            return unrestored == 0
                ? new MaterialCommit<TSource>(MaterialCommitOutcome.RolledBack, Array.Empty<MaterialStep<TSource>>(), why, 0)
                : new MaterialCommit<TSource>(MaterialCommitOutcome.Inconsistent, Array.Empty<MaterialStep<TSource>>(),
                    why + "; " + unrestored + " unit(s) could not be given back", unrestored);
        }
        return new MaterialCommit<TSource>(MaterialCommitOutcome.Paid, taken, "", 0);
    }

    private static int Restore<TSource>(List<MaterialStep<TSource>> taken, IMaterialLedger<TSource> ledger)
        where TSource : IEquatable<TSource>
    {
        int missing = 0;
        for (int i = taken.Count - 1; i >= 0; i--)
        {
            var step = taken[i];
            int back;
            try { back = ledger.Add(step.Source, step.Item, step.Amount); }
            catch { back = 0; }
            if (back < step.Amount) missing += step.Amount - back;
        }
        return missing;
    }

    internal static string DescribeShort<TSource>(MaterialPlan<TSource> plan) where TSource : IEquatable<TSource>
    {
        var parts = new List<string>(plan.Short.Count);
        foreach (var (item, needed, available) in plan.Short) parts.Add(item + " need=" + needed + " have=" + available);
        return string.Join(", ", parts);
    }
}
