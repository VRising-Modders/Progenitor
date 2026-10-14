using System;
using System.Collections.Generic;
using Progenitor.Castles;
using Progenitor.Inventory;

namespace Progenitor.Materials;

/// <summary>
/// What the material service needs from the running game. Production: <c>UnityMaterialWorld</c>;
/// tests: an in-memory world. Every call is on the server main thread.
/// </summary>
internal interface IMaterialWorld
{
    /// <summary>World-load epoch now.</summary>
    int Epoch { get; }

    /// <summary>
    /// The context for a character in an operation: its user, standing plot and (craft) the station
    /// it is working at. False when the character is not a connected player's.
    /// </summary>
    bool TryContext(CastleId character, MaterialOperation operation, out MaterialContext context);

    /// <summary>The physical inventory behind a container id (main inventory). False when gone, unknown or unbound.</summary>
    bool TryResolve(ContainerId id, out InventoryRef inventory);

    /// <summary>The stable id of the inventory's owner, for reports. <see cref="ContainerId.None"/> when it has none.</summary>
    ContainerId IdOf(InventoryRef inventory);

    /// <summary>
    /// Inventories the game itself already counts and pays from when the shared-storage branch is
    /// skipped: the character's bag and a station that is not shared storage. Left out of the pool so
    /// nothing is counted twice.
    /// </summary>
    void VanillaCounted(in MaterialContext context, ICollection<InventoryRef> into);

    /// <summary>
    /// A station inventory the game would only reach through shared storage (the workstation being
    /// used is itself shared storage). It pays first, before the provider's order. None when n/a.
    /// </summary>
    InventoryRef PoolFront(in MaterialContext context);

    /// <summary>
    /// The game's own shared-storage totals at the plot the player stands on (the castle heart's
    /// aggregate rows), read only. Used for items the provider leaves to the game in the client view.
    /// </summary>
    void VanillaShared(in MaterialContext context, IDictionary<int, long> into);

    /// <summary>Item types the pool may spend in this inventory (slots without an item entity).</summary>
    void Items(InventoryRef inventory, ICollection<int> into);

    /// <summary>Count, take and give back.</summary>
    IMaterialLedger<InventoryRef> Ledger { get; }
}

/// <summary>Result of one governed payment request from the game.</summary>
internal readonly struct MaterialPayResult
{
    /// <summary>Units still owed after the pool paid (0 when fully paid; the full amount when the pool paid nothing).</summary>
    internal int Remainder { get; }

    /// <summary>Units the pool paid.</summary>
    internal int Paid { get; }

    internal string Reason { get; }

    internal MaterialPayResult(int remainder, int paid, string reason)
    {
        Remainder = remainder;
        Paid = paid;
        Reason = reason;
    }
}

/// <summary>
/// The game-free core of the material seams. The availability seam asks
/// <see cref="ComposeExtraRows"/> what to show in place of the game's shared-storage rows; the
/// payment seam asks <see cref="GovernsLine"/> before the game pays and <see cref="Pay"/> for what the
/// bag and the station did not cover. Every answer comes from the same provider calls and the same
/// physical counts, so what is shown is exactly what can be paid.
/// </summary>
internal sealed class MaterialService
{
    private readonly MaterialSourceRegistry _registry;
    private readonly IMaterialWorld _world;
    private readonly Action<string> _log;
    private readonly Action<MaterialPaid>? _publish;
    private int _providerFaults;

    internal MaterialService(MaterialSourceRegistry registry, IMaterialWorld world, Action<string> log, Action<MaterialPaid>? publish = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _log = log ?? (_ => { });
        _publish = publish;
    }

    /// <summary>
    /// True when <paramref name="operation"/> for this character is routed through the provider:
    /// registered, requested, supported, a resolvable player, and a non-empty candidate list.
    /// </summary>
    internal bool TryGoverned(CastleId character, MaterialOperation operation, out MaterialContext context,
        out IMaterialSourceProvider provider, out IReadOnlyList<ContainerId> candidates)
    {
        context = default;
        candidates = Array.Empty<ContainerId>();
        provider = null!;
        if (!_registry.Governs(operation)) return false;
        var p = _registry.Provider;
        if (p == null) return false;
        if (!_world.TryContext(character, operation, out context)) return false;
        IReadOnlyList<ContainerId>? c;
        try { c = p.Candidates(in context); }
        catch (Exception e)
        {
            // A throwing provider governs with no remote sources; it never falls back to the
            // game's shared storage, which could be less restrictive than the provider's rules.
            Fault("Candidates", e);
            provider = p;
            return true;
        }
        if (c == null || c.Count == 0) return false;
        provider = p;
        candidates = c;
        return true;
    }

    /// <summary>
    /// Availability: given the game's own per-item counts with shared storage
    /// (<paramref name="full"/>) and without it (<paramref name="bagOnly"/>), the rows to append after
    /// the bag-only rows. A governed item gets one row with the pool's spendable amount; an item the
    /// provider returns null for keeps the game's own shared amount. Returns false when this context is
    /// not governed: the caller keeps the game's full result untouched.
    /// </summary>
    internal bool ComposeExtraRows(CastleId character, MaterialOperation operation,
        IReadOnlyDictionary<int, long> full, IReadOnlyDictionary<int, long> bagOnly, List<MaterialLine> extra)
    {
        extra.Clear();
        if (!TryGoverned(character, operation, out var ctx, out var provider, out var candidates)) return false;
        var totals = new Dictionary<int, int>();
        var vanillaItems = new HashSet<int>();
        Totals(ctx, provider, candidates, totals, vanillaItems, full.Keys);
        foreach (var kv in full)
        {
            if (!vanillaItems.Contains(kv.Key)) continue;
            bagOnly.TryGetValue(kv.Key, out long bag);
            long shared = kv.Value - bag;
            if (shared > 0) extra.Add(new MaterialLine(kv.Key, shared > int.MaxValue ? int.MaxValue : (int)shared));
        }
        foreach (var kv in totals)
            if (kv.Value > 0) extra.Add(new MaterialLine(kv.Key, kv.Value));
        extra.Sort((a, b) => a.Item.CompareTo(b.Item));
        _registry.CountView();
        return true;
    }

    /// <summary>
    /// The per-item spendable totals for a player's view (what the client is sent). False when not governed.
    /// </summary>
    internal bool TryView(CastleId character, MaterialOperation operation, Dictionary<int, int> totals, out MaterialContext context, out long revision)
    {
        totals.Clear();
        revision = 0;
        if (!TryGoverned(character, operation, out context, out var provider, out var candidates)) return false;
        try { revision = provider.Revision(in context); }
        catch (Exception e) { Fault("Revision", e); }
        // The client shows its bag rows plus exactly these rows, so the view is complete: governed
        // items carry the pool (zero included, which hides the game's shared amount), and items the
        // provider leaves to the game carry the game's own shared amount.
        var vanillaShared = new Dictionary<int, long>();
        _world.VanillaShared(in context, vanillaShared);
        var vanillaItems = new HashSet<int>();
        Totals(context, provider, candidates, totals, vanillaItems, vanillaShared.Keys);
        foreach (int item in vanillaItems)
        {
            vanillaShared.TryGetValue(item, out long shared);
            if (shared > 0) totals[item] = shared > int.MaxValue ? int.MaxValue : (int)shared;
        }
        _registry.CountView();
        return true;
    }

    /// <summary>
    /// Fill <paramref name="totals"/> with the pool's spendable amount for every governed item found in
    /// the candidates, the pool front, or <paramref name="extraItems"/>. Items the provider returns null
    /// for go to <paramref name="vanillaItems"/>.
    /// </summary>
    private void Totals(in MaterialContext ctx, IMaterialSourceProvider provider, IReadOnlyList<ContainerId> candidates,
        Dictionary<int, int> totals, HashSet<int>? vanillaItems, IEnumerable<int>? extraItems)
    {
        var items = new HashSet<int>();
        var seen = new HashSet<InventoryRef>();
        foreach (var id in candidates)
            if (_world.TryResolve(id, out var inv) && seen.Add(inv)) _world.Items(inv, items);
        var front = _world.PoolFront(in ctx);
        if (!front.IsNone && seen.Add(front)) _world.Items(front, items);
        if (extraItems != null) foreach (int i in extraItems) items.Add(i);

        var exclude = Exclusions(ctx);
        foreach (int item in items)
        {
            var spec = Spec(ctx, provider, item, front);
            if (spec == null) { vanillaItems?.Add(item); continue; }
            var pool = MaterialPlanner.Pool(item, spec, _world.Ledger, exclude);
            totals[item] = pool.Available;
        }
    }

    /// <summary>Payment, before the game pays: does the provider govern this line?</summary>
    internal bool GovernsLine(CastleId character, MaterialOperation operation, int item)
    {
        if (!TryGoverned(character, operation, out var ctx, out var provider, out _)) return false;
        return SafeSources(ctx, provider, item) != null;
    }

    /// <summary>
    /// Payment, after the game paid what the bag and the station held: pay <paramref name="amount"/>
    /// from the pool, all or nothing. A short pool pays nothing and the whole amount stays owed.
    /// </summary>
    internal MaterialPayResult Pay(CastleId character, MaterialOperation operation, int item, int amount)
    {
        if (amount <= 0) return new MaterialPayResult(0, 0, "");
        if (!TryGoverned(character, operation, out var ctx, out var provider, out _))
            return new MaterialPayResult(amount, 0, "not governed");
        var front = _world.PoolFront(in ctx);
        var spec = Spec(ctx, provider, item, front);
        if (spec == null) return new MaterialPayResult(amount, 0, "item not governed");

        var plan = MaterialPlanner.Plan(new[] { new MaterialLine(item, amount) }, _ => spec, _world.Ledger, Exclusions(ctx));
        var commit = MaterialPlanner.Commit(plan, _world.Ledger);
        if (!commit.Ok)
        {
            string line = "materials: pay " + commit.Outcome + " " + ctx + " item=" + item + " amount=" + amount + ": " + commit.Reason;
            _log(commit.Outcome == MaterialCommitOutcome.Inconsistent ? "ERROR " + line : line);
            return new MaterialPayResult(amount, 0, commit.Reason);
        }

        var debits = new List<MaterialDebit>(commit.Taken.Count);
        foreach (var step in commit.Taken) debits.Add(new MaterialDebit(_world.IdOf(step.Source), step.Amount));
        _registry.CountPayment();
        _log("materials: paid " + ctx + " item=" + item + " amount=" + amount + " from=" + string.Join(",", debits));
        try { _publish?.Invoke(new MaterialPaid(ctx, item, amount, amount, debits)); }
        catch (Exception e) { _log("materials: MaterialPaid handler threw: " + e.Message); }
        return new MaterialPayResult(0, amount, "");
    }

    /// <summary>Plan a whole request without taking anything (pre-validation, diagnostics, tests).</summary>
    internal MaterialPlan<InventoryRef>? PlanRequest(CastleId character, MaterialOperation operation, IEnumerable<MaterialLine> lines)
    {
        if (!TryGoverned(character, operation, out var ctx, out var provider, out _)) return null;
        var front = _world.PoolFront(in ctx);
        return MaterialPlanner.Plan(lines, item => Spec(ctx, provider, item, front), _world.Ledger, Exclusions(ctx));
    }

    /// <summary>Plan and commit a whole request in one step: every line from the pool, or nothing.</summary>
    internal MaterialCommit<InventoryRef>? PayRequest(CastleId character, MaterialOperation operation, IEnumerable<MaterialLine> lines)
    {
        var plan = PlanRequest(character, operation, lines);
        if (plan == null) return null;
        var commit = MaterialPlanner.Commit(plan, _world.Ledger);
        if (commit.Ok) _registry.CountPayment();
        return commit;
    }

    private HashSet<InventoryRef> Exclusions(in MaterialContext ctx)
    {
        var set = new HashSet<InventoryRef>();
        _world.VanillaCounted(in ctx, set);
        return set;
    }

    private MaterialPoolSpec<InventoryRef>? Spec(in MaterialContext ctx, IMaterialSourceProvider provider, int item, InventoryRef front)
    {
        var sources = SafeSources(ctx, provider, item);
        if (sources == null) return null;
        var order = new List<InventoryRef>(sources.SpendOrder.Count + 1);
        if (!front.IsNone) order.Add(front);
        foreach (var id in sources.SpendOrder)
            if (!id.IsNone && _world.TryResolve(id, out var inv)) order.Add(inv);
        return new MaterialPoolSpec<InventoryRef>(order, sources.PoolReserve);
    }

    private ItemSources? SafeSources(in MaterialContext ctx, IMaterialSourceProvider provider, int item)
    {
        try { return provider.Sources(in ctx, item); }
        catch (Exception e)
        {
            Fault("Sources", e);
            // A throwing provider means "no remote sources", never "vanilla storage rules".
            return new ItemSources(Array.Empty<ContainerId>());
        }
    }

    private void Fault(string member, Exception e)
    {
        if (_providerFaults++ < 20)
            _log("materials: provider." + member + " threw (owner=" + _registry.Owner + "): " + e.Message);
    }
}
