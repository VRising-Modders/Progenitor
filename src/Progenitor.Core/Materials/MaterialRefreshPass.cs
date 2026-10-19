using System;
using System.Collections.Generic;
using Progenitor.Castles;
using Progenitor.Inventory;
using Progenitor.Perf;

namespace Progenitor.Materials;

/// <summary>One player's view of one operation, queued in a <see cref="MaterialRefreshPass"/>.</summary>
internal sealed class MaterialPushItem
{
    internal int UserIndex;
    internal CastleId User;
    internal CastleId Character;
    internal MaterialOperation Operation;

    /// <summary>Send even when the view did not change.</summary>
    internal bool Force;

    /// <summary>Recompute even when the cached view could be reused (an invalidation or a governed payment).</summary>
    internal bool Recompute;

    /// <summary>Container ids the cached view resolved: resolved a step at a time before the view is refreshed.</summary>
    internal readonly List<ContainerId> WarmIds = new();

    /// <summary>Inventories the cached view read: read a step at a time before the view is refreshed.</summary>
    internal readonly List<InventoryRef> WarmStocks = new();

    internal int Cursor;
}

/// <summary>
/// The per-player view refresh, spread across frames under a time budget (game-free). A pass holds one
/// item per player and operation. Each step does one small piece: it resolves one container id or reads
/// one inventory into the pass's read cache (the ids and inventories the cached view depended on), and
/// once an item's reads are cached it refreshes and sends that view (<c>push</c>), whose reuse check then
/// finds every read already cached. Reads are kept for the whole pass, so one inventory is read once per
/// pass however many players and operations depend on it; a view is therefore stale by at most one pass.
/// Display only: payment never reads them.
/// </summary>
internal sealed class MaterialRefreshPass
{
    private readonly BudgetedQueue<MaterialPushItem> _queue;
    private readonly Action<MaterialPushItem> _push;
    private MaterialStockReads? _reads;

    internal MaterialRefreshPass(Action<MaterialPushItem> push)
    {
        _push = push ?? throw new ArgumentNullException(nameof(push));
        _queue = new BudgetedQueue<MaterialPushItem>(Step);
    }

    /// <summary>A pass has items left.</summary>
    internal bool Active => _queue.Count > 0;

    /// <summary>Items left in the pass.</summary>
    internal int Remaining => _queue.Count;

    /// <summary>The pass covers every online player (else only the dirty ones).</summary>
    internal bool Everyone { get; private set; }

    /// <summary>Views refreshed (pushed) since construction (tests and diagnostics).</summary>
    internal long Pushed { get; private set; }

    /// <summary>Warm-up reads and resolutions since construction (tests and diagnostics).</summary>
    internal long WarmSteps { get; private set; }

    /// <summary>Start a pass over <paramref name="items"/> with an empty read cache (replaces one in progress).</summary>
    internal void Start(IEnumerable<MaterialPushItem> items, bool everyone, MaterialStockReads reads)
    {
        _queue.Clear();
        _reads = reads ?? throw new ArgumentNullException(nameof(reads));
        _reads.Clear();
        Everyone = everyone;
        foreach (var item in items) _queue.Enqueue(item);
    }

    /// <summary>Steps under <paramref name="budget"/> (at least one); what is left carries over to the next call.</summary>
    internal BudgetRun Run(FrameBudget budget) => _queue.Run(budget);

    /// <summary>Drop the pass and its reads.</summary>
    internal void Clear()
    {
        _queue.Clear();
        _reads?.Clear();
        _reads = null;
        Everyone = false;
    }

    private bool Step(MaterialPushItem item)
    {
        var reads = _reads!;
        int ids = item.WarmIds.Count;
        if (item.Cursor < ids)
        {
            reads.TryResolve(item.WarmIds[item.Cursor++], out _);
            WarmSteps++;
            return false;
        }
        if (item.Cursor < ids + item.WarmStocks.Count)
        {
            reads.Stock(item.WarmStocks[item.Cursor++ - ids]);
            WarmSteps++;
            return false;
        }
        Pushed++;
        _push(item);
        return true;
    }
}
