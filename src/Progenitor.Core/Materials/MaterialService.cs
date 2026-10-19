using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// The craft's own station inventory (main), whatever kind it is, or None. A provider source equal to
    /// it is dropped: the game pays from a plain station itself, and a shared one pays first.
    /// </summary>
    InventoryRef StationInventory(in MaterialContext context);

    /// <summary>
    /// The inventories of the game's own shared storage at the plot the player stands on (discovery
    /// only: the view reads which item types they hold and asks the provider about each of them, so a
    /// governed item the provider's containers lack is sent as 0 and hides the game's shared amount).
    /// The list may be cached for a few seconds; the contents are read by the caller.
    /// </summary>
    void VanillaSharedInventories(in MaterialContext context, ICollection<InventoryRef> into);

    /// <summary>Item types the pool may spend in this inventory (slots without an item entity).</summary>
    void Items(InventoryRef inventory, ICollection<int> into);

    /// <summary>
    /// One read of an inventory: the amount the pool may spend of every item in it (slots without an
    /// item entity, summed per item) into <paramref name="into"/>, which the caller cleared. False when
    /// the inventory cannot be read (gone, stale, unbound). The same rule as <see cref="Items"/> and
    /// the ledger's <c>Count</c>.
    /// </summary>
    bool ReadStock(InventoryRef inventory, Dictionary<int, long> into);

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
/// The game-free core of the material seams.
///
/// <para><b>One operation, one answer.</b> A governed game operation (one craft start, one placement, one repair, one heart upgrade, one character craft)
/// runs inside one synchronous main-thread system update: the game first checks affordability with its
/// merged count, then removes each cost line. The first count inside a scope opens an
/// <i>operation session</i> for that character: the provider's candidates and, per item, its
/// <see cref="ItemSources"/> are asked once and frozen, and the pool amount shown to the game's check is
/// recorded as the item's <i>quote</i>. Every cost line of that operation is paid from the frozen
/// answer: the provider is never asked again, so it cannot change what the operation pays from.</para>
///
/// <para><b>Validate, then take.</b> Each line is checked against what is left of its quote and planned
/// against the containers' current counts before anything is taken. A covered line is taken whole or
/// not at all (the planner's commit). The game proceeds after its own check passed whatever its removal
/// returns, so the binding refusal point is the check, and a line that cannot be paid in full at payment
/// (a container emptied, removed or refusing writes between the check and the payment) follows the
/// game's own shortfall behavior: earlier lines keep their debits, the line takes what its quote and the
/// stock allow, the rest stays owed (the game logs it), and the shortfall is logged once per operation.
/// Already-paid lines are never given back; only a line whose own commit rolled back is restored, and
/// then taken again as far as the stock allows.</para>
///
/// <para><b>Events after the operation.</b> <see cref="MaterialPaid"/> events are queued and published
/// by <see cref="EndScope"/>, after the game system finished the operation (same frame), so no event
/// handler can move items between the check and the payment.</para>
/// </summary>
internal sealed class MaterialService
{
    private readonly MaterialSourceRegistry _registry;
    private readonly IMaterialWorld _world;
    private readonly Action<string> _log;
    private readonly Action<MaterialPaid>? _publish;
    private readonly Dictionary<(CastleId, MaterialOperation), Session> _sessions = new();
    private readonly List<MaterialPaid> _pending = new();
    private long _scope;
    // Seeded from the clock, so ids keep increasing across Core swaps in one process.
    private long _transactions = DateTime.UtcNow.Ticks;
    private int _faultLines;

    internal MaterialService(MaterialSourceRegistry registry, IMaterialWorld world, Action<string> log, Action<MaterialPaid>? publish = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _log = log ?? (_ => { });
        _publish = publish;
    }

    /// <summary>Open sessions (tests and diagnostics).</summary>
    internal int OpenSessions => _sessions.Count;

    // ---- scope ------------------------------------------------------------------------------------

    /// <summary>
    /// The seam calls that follow belong to scope <paramref name="token"/>. A different token than the
    /// current one closes the previous scope's sessions first; their events stay queued for the next
    /// <see cref="EndScope"/>, so nothing is published from inside a seam call.
    /// </summary>
    internal void BeginScope(long token)
    {
        if (token == _scope) return;
        foreach (var s in _sessions.Values) Close(s);
        _sessions.Clear();
        _scope = token;
    }

    /// <summary>Events queued and not yet published (tests and diagnostics).</summary>
    internal int PendingEvents => _pending.Count;

    /// <summary>
    /// The game system finished: close every operation session and publish the queued
    /// <see cref="MaterialPaid"/> events (one per line the pool paid any part of).
    /// </summary>
    internal void EndScope()
    {
        foreach (var s in _sessions.Values) Close(s);
        _sessions.Clear();
        _scope = 0;
        if (_pending.Count == 0) return;
        var events = _pending.ToArray();
        _pending.Clear();
        foreach (var e in events)
        {
            try { _publish?.Invoke(e); }
            catch (Exception ex) { _log("materials: MaterialPaid handler threw: " + ex.Message); }
        }
    }

    private void Close(Session s) => _pending.AddRange(s.Paid);

    // ---- availability (the game's check) ----------------------------------------------------------

    /// <summary>
    /// The game's merged count was called for this character inside a governed scope: a new check, so
    /// a new operation session. True when the provider governs this operation here.
    /// </summary>
    internal bool CountGate(CastleId character, MaterialOperation operation) => Open(character, operation, fresh: true).Governed;

    /// <summary>
    /// Availability: given the game's own per-item counts with shared storage
    /// (<paramref name="full"/>) and without it (<paramref name="bagOnly"/>), the rows to append after
    /// the bag-only rows. A governed item gets one row with the pool's spendable amount (its quote); an
    /// item the provider returns null for keeps the game's own shared amount. Returns false when this
    /// context is not governed: the caller keeps the game's full result untouched.
    /// </summary>
    internal bool ComposeExtraRows(CastleId character, MaterialOperation operation,
        IReadOnlyDictionary<int, long> full, IReadOnlyDictionary<int, long> bagOnly, List<MaterialLine> extra)
    {
        extra.Clear();
        var s = Open(character, operation, fresh: false);
        if (!s.Governed) return false;
        var items = DiscoverItems(s, full.Keys, null);
        // Every provider call first, then every count: the quotes are read after the provider's last
        // call, so nothing a provider does during its calls can make a quote stale.
        foreach (int item in items) SpecOf(s, item);
        foreach (int item in items)
        {
            var spec = s.Specs[item];
            if (spec == null)
            {
                full.TryGetValue(item, out long all);
                bagOnly.TryGetValue(item, out long bag);
                long shared = all - bag;
                if (shared > 0) extra.Add(new MaterialLine(item, shared > int.MaxValue ? int.MaxValue : (int)shared));
                continue;
            }
            int available = MaterialPlanner.Pool(item, spec, _world.Ledger, s.Exclusions).Available;
            s.Quote[item] = available;
            if (available > 0) extra.Add(new MaterialLine(item, available));
        }
        s.Composed = true;
        extra.Sort((a, b) => a.Item.CompareTo(b.Item));
        return true;
    }

    /// <summary>
    /// The composed rows were handed to the game's check (they fit the seam's buffer): from now on this
    /// operation's payment follows the quotes. Not called when the seam kept the game's own count, so
    /// that operation is then paid by the game's own rules. Runs the development fault injection, if
    /// armed; the game's check has already seen the pool rows, so a throwing injection still leaves the
    /// operation checked (paid from the pool, never free). Logs <c>materials: check tx=…</c> once per
    /// check, so a log shows whether the game counts more than once per operation.
    /// </summary>
    internal void MarkChecked(CastleId character, MaterialOperation operation)
    {
        var s = Open(character, operation, fresh: false);
        if (!s.Governed || !s.Composed) return;
        s.Checked = true;
        _log("materials: check " + s.Context + " tx=" + s.Transaction + " quoted=" + s.Quote.Count);
        try { RunInjectedMove(s); }
        catch (Exception e) { _log("materials: fault injection threw: " + e.Message); }
    }

    /// <summary>
    /// The per-item spendable totals for a player's displayed view: only governed items (zero included,
    /// which hides the game's shared amount for that item). Items the provider returns null for are not
    /// listed; the client keeps the game's own shared amount for them. False when not governed.
    /// Outside any operation: a throwaway session, nothing is paid from it. Always recomputes (tests and
    /// diagnostics); the per-player refresh uses <see cref="RefreshView"/>.
    /// </summary>
    internal bool TryView(CastleId character, MaterialOperation operation, Dictionary<int, int> totals, out MaterialContext context, out long revision)
    {
        totals.Clear();
        var entry = new MaterialViewEntry();
        bool governed = ComputeView(character, operation, new MaterialStockReads(_world), entry);
        context = entry.Context;
        revision = entry.Revision;
        if (!governed) return false;
        foreach (var line in entry.Rows) totals[line.Item] = line.Amount;
        return true;
    }

    private readonly Dictionary<(CastleId, MaterialOperation), MaterialViewEntry> _views = new();
    private readonly HashSet<InventoryRef> _checkExclusions = new();
    private readonly List<InventoryRef> _checkShared = new();
    private readonly HashSet<InventoryRef> _trackStock = new();
    private readonly Dictionary<ContainerId, InventoryRef> _trackIds = new();
    private readonly Dictionary<int, int> _viewTotals = new();
    private readonly HashSet<InventoryRef> _poolSeen = new();
    private readonly Dictionary<IReadOnlyList<InventoryRef>, InventoryRef[]> _uniqueOrders = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// <see cref="MaterialPlanner.Available"/> for a view: the deduplicated, exclusion-filtered order is
    /// built once per shared spend order (one view's exclusions are fixed), then each item only sums
    /// its counts. Same rule: each inventory once, excluded ones dropped, the reserve subtracted once.
    /// </summary>
    private int Available(int item, MaterialPoolSpec<InventoryRef> spec, MaterialStockReads reads, HashSet<InventoryRef> exclude)
    {
        if (!_uniqueOrders.TryGetValue(spec.Order, out var unique))
        {
            _poolSeen.Clear();
            var list = new List<InventoryRef>(spec.Order.Count);
            foreach (var source in spec.Order)
                if (_poolSeen.Add(source) && !exclude.Contains(source)) list.Add(source);
            unique = list.ToArray();
            _uniqueOrders[spec.Order] = unique;
        }
        long total = 0;
        foreach (var source in unique)
        {
            int n = reads.Count(source, item);
            if (n > 0) total += n;
        }
        long a = total - spec.Reserve;
        return a <= 0 ? 0 : a > int.MaxValue ? int.MaxValue : (int)a;
    }
    private long _viewStamp;
    private int _faultCount;

    /// <summary>Cached views (tests and diagnostics).</summary>
    internal int CachedViews => _views.Count;

    /// <summary>
    /// A player's displayed view for one operation, for the per-player refresh. Reuses the last computed
    /// view when nothing it depended on changed: the provider (the same instance) and its
    /// <see cref="IMaterialSourceProvider.Revision"/>, the context (user, plot, station, epoch), the
    /// inventories left out (bag, station) and put first, the plot's shared-storage inventories, every
    /// container id it resolved and the stock of every inventory it read. Otherwise, and always when
    /// <paramref name="recompute"/> (an invalidation or a governed payment for this player), it is
    /// recomputed. <paramref name="reads"/> is the refresh's read cache: each inventory is read at most
    /// once per refresh, however many items, operations and players need it. Display only: payment
    /// never reads a cached view or <paramref name="reads"/>.
    /// </summary>
    internal MaterialViewOutcome RefreshView(CastleId character, MaterialOperation operation, MaterialStockReads reads, bool recompute,
        out MaterialViewResult view)
    {
        long start = Stopwatch.GetTimestamp();
        _providerTicks = _sharedTicks = 0;
        long readTicks = reads.ReadTicks, readCount = reads.WorldReads;
        var outcome = RefreshViewCore(character, operation, reads, recompute, out view);
        long total = Stopwatch.GetTimestamp() - start;
        if (total * 1000 >= SlowViewMs * Stopwatch.Frequency)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - _lastSlowLog >= 5 * Stopwatch.Frequency || _lastSlowLog == 0)
            {
                _lastSlowLog = now;
                long r = reads.ReadTicks - readTicks;
                _log("materials: slow view refresh op=" + operation + " outcome=" + outcome + " ms=" + Ms(total)
                    + " provider=" + Ms(_providerTicks) + " reads=" + Ms(r) + "(" + (reads.WorldReads - readCount) + ")"
                    + " sharedList=" + Ms(_sharedTicks) + " other=" + Ms(total - _providerTicks - r - _sharedTicks));
            }
        }
        return outcome;
    }

    /// <summary>A single refresh slower than this logs one <c>materials: slow view refresh</c> line (at most one per 5 s) with where the time went.</summary>
    internal const int SlowViewMs = 20;

    private long _providerTicks, _sharedTicks, _lastSlowLog;

    private static string Ms(long ticks) => (ticks * 1000.0 / Stopwatch.Frequency).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);

    private MaterialViewOutcome RefreshViewCore(CastleId character, MaterialOperation operation, MaterialStockReads reads, bool recompute,
        out MaterialViewResult view)
    {
        view = default;
        var key = (character, operation);
        var provider = _registry.Provider;
        if (provider == null || !_registry.Governs(operation))
        {
            _views.Remove(key);
            return MaterialViewOutcome.NotGoverned;
        }
        _views.TryGetValue(key, out var entry);
        if (entry != null && !recompute && CanReuse(entry, provider, operation, reads))
        {
            entry.Stamp = _viewStamp;
            _registry.CountSkip();
            if (!entry.Governed) return MaterialViewOutcome.NotGoverned;
            view = new MaterialViewResult(entry.Context, entry.Revision, entry.Rows);
            return MaterialViewOutcome.Reused;
        }

        entry ??= new MaterialViewEntry();
        bool governed = ComputeView(character, operation, reads, entry);
        _registry.CountRecompute();
        // No context (not a connected player's character): nothing to reuse next time.
        if (entry.Provider == null) _views.Remove(key);
        else
        {
            entry.Stamp = _viewStamp;
            _views[key] = entry;
        }
        if (!governed) return MaterialViewOutcome.NotGoverned;
        view = new MaterialViewResult(entry.Context, entry.Revision, entry.Rows);
        return MaterialViewOutcome.Computed;
    }

    /// <summary>
    /// What the cached view of this character and operation resolved and read when it was computed (its
    /// container ids and inventories, the plot's shared storage, bag and station included), so a budgeted
    /// refresh can resolve and read them a few per frame before <see cref="RefreshView"/> checks reuse.
    /// Nothing when no view is cached.
    /// </summary>
    internal void CachedDependencies(CastleId character, MaterialOperation operation, List<ContainerId> ids, List<InventoryRef> stocks)
    {
        if (!_views.TryGetValue((character, operation), out var entry)) return;
        foreach (var (id, _) in entry.Ids) ids.Add(id);
        foreach (var (inventory, _) in entry.Stocks) stocks.Add(inventory);
    }

    /// <summary>Drop every cached view: the next refresh recomputes everyone (an invalidation of all views, a provider change).</summary>
    internal void ForgetViews() => _views.Clear();

    /// <summary>
    /// Drop every cached view of <paramref name="character"/>, whatever the operation: the player was
    /// invalidated or paid, so each of its views is recomputed the next time it is refreshed, also a
    /// view this refresh does not cover (a Craft view while no workstation is open, an Upgrade view
    /// while no heart window is).
    /// </summary>
    internal void ForgetViews(CastleId character)
    {
        if (_views.Count == 0) return;
        List<(CastleId, MaterialOperation)>? drop = null;
        foreach (var key in _views.Keys)
            if (key.Item1.Equals(character)) (drop ??= new()).Add(key);
        if (drop != null) foreach (var k in drop) _views.Remove(k);
    }

    /// <summary>
    /// A refresh that covers every online player starts: cached views nobody asked for since the
    /// previous such refresh (a player gone offline, a station left) are dropped.
    /// </summary>
    internal void BeginFullRefresh()
    {
        if (_views.Count > 0)
        {
            List<(CastleId, MaterialOperation)>? stale = null;
            foreach (var kv in _views)
                if (kv.Value.Stamp != _viewStamp) (stale ??= new()).Add(kv.Key);
            if (stale != null) foreach (var k in stale) _views.Remove(k);
        }
        _viewStamp++;
    }

    private bool CanReuse(MaterialViewEntry entry, IMaterialSourceProvider provider, MaterialOperation operation, MaterialStockReads reads)
    {
        if (entry.NoReuse || !ReferenceEquals(entry.Provider, provider)) return false;
        if (!_world.TryContext(entry.Context.Character, operation, out var context) || !context.Equals(entry.Context)) return false;
        // Still off a plot for an operation that needs one: nothing to ask the provider (the context is unchanged).
        if (entry.PlotRefused) return true;
        long revision;
        // A throwing Revision means a recompute; the recompute asks again and counts the fault once.
        long t0 = Stopwatch.GetTimestamp();
        try { revision = provider.Revision(in context); }
        catch { return false; }
        finally { _providerTicks += Stopwatch.GetTimestamp() - t0; }
        if (revision != entry.Revision) return false;
        if (!entry.Governed) return true;

        _checkExclusions.Clear();
        _world.VanillaCounted(in context, _checkExclusions);
        if (!_checkExclusions.SetEquals(entry.Exclusions)) return false;
        if (!_world.PoolFront(in context).Equals(entry.Front)) return false;
        if (!_world.StationInventory(in context).Equals(entry.Station)) return false;
        _checkShared.Clear();
        long t1 = Stopwatch.GetTimestamp();
        _world.VanillaSharedInventories(in context, _checkShared);
        _sharedTicks += Stopwatch.GetTimestamp() - t1;
        if (_checkShared.Count != entry.Shared.Count) return false;
        for (int i = 0; i < _checkShared.Count; i++)
            if (!_checkShared[i].Equals(entry.Shared[i])) return false;
        foreach (var (id, was) in entry.Ids)
        {
            reads.TryResolve(id, out var now);
            if (!now.Equals(was)) return false;
        }
        foreach (var (inventory, was) in entry.Stocks)
            if (!reads.Stock(inventory).SameAs(was)) return false;
        return true;
    }

    /// <summary>
    /// Compute a view into <paramref name="entry"/> from a fresh provider answer and the refresh's reads,
    /// recording what it depended on. True when governed. <see cref="MaterialViewEntry.Provider"/> stays
    /// null when there was no context to compute in.
    /// </summary>
    private bool ComputeView(CastleId character, MaterialOperation operation, MaterialStockReads reads, MaterialViewEntry entry)
    {
        int faults = _faultCount;
        entry.Provider = null;
        entry.Context = default;
        entry.Revision = 0;
        entry.Governed = false;
        entry.NoReuse = false;
        entry.PlotRefused = false;
        entry.Front = InventoryRef.None;
        entry.Station = InventoryRef.None;
        entry.Exclusions.Clear();
        entry.Shared.Clear();
        entry.Ids.Clear();
        entry.Stocks.Clear();
        entry.Rows = Array.Empty<MaterialLine>();

        var provider = _registry.Provider;
        _uniqueOrders.Clear();
        reads.BeginTrack(_trackStock, _trackIds);
        try
        {
            var s = Build(character, operation, reads);
            entry.Context = s.Context;
            if (!s.HasContext) return false;
            entry.Provider = provider;
            entry.PlotRefused = s.PlotRefused;
            // Off a plot (an operation that needs one) the provider is never asked, not even for its revision.
            if (s.PlotRefused) return false;
            long t0 = Stopwatch.GetTimestamp();
            // Also for a context the provider does not govern (no candidates): the reuse check compares it, so a
            // provider that starts governing bumps it, and an unchanged one is not asked for candidates every refresh.
            try { entry.Revision = provider!.Revision(in s.Context); }
            catch (Exception e) { Fault("Revision", e); }
            finally { _providerTicks += Stopwatch.GetTimestamp() - t0; }
            if (!s.Governed) return false;
            entry.Governed = true;
            entry.Front = s.Front;
            entry.Station = s.Station;
            foreach (var x in s.Exclusions) entry.Exclusions.Add(x);
            long t1 = Stopwatch.GetTimestamp();
            _world.VanillaSharedInventories(in s.Context, entry.Shared);
            _sharedTicks += Stopwatch.GetTimestamp() - t1;
            var shared = new List<int>();
            foreach (var inv in entry.Shared) reads.Stock(inv).AddItems(shared);
            _viewTotals.Clear();
            foreach (int item in DiscoverItems(s, null, shared))
            {
                var spec = SpecOf(s, item);
                if (spec == null) continue;
                _viewTotals[item] = Available(item, spec, reads, s.Exclusions);
            }
            var rows = new List<MaterialLine>(_viewTotals.Count);
            foreach (var kv in _viewTotals) rows.Add(new MaterialLine(kv.Key, kv.Value));
            rows.Sort((a, b) => a.Item.CompareTo(b.Item));
            entry.Rows = rows;
            // The bag and the station are left out of the pool; a change in them still means a recompute.
            foreach (var x in s.Exclusions) reads.Stock(x);
            return true;
        }
        finally
        {
            reads.EndTrack();
            // A provider call that threw (or a faulted context) is never reused: recomputed every refresh.
            entry.NoReuse = faults != _faultCount;
            foreach (var kv in _trackIds) entry.Ids.Add((kv.Key, kv.Value));
            foreach (var inv in _trackStock) entry.Stocks.Add((inv, reads.Stock(inv)));
            _trackIds.Clear();
            _trackStock.Clear();
            _uniqueOrders.Clear();
        }
    }

    // ---- payment ---------------------------------------------------------------------------------

    /// <summary>
    /// Payment, before the game pays: does the provider govern this line? Only when a governed check of
    /// this operation was composed in this scope (the payment follows what the check saw; with no such
    /// check, for example when the seam kept the game's own count, the game pays by its own rules), and
    /// only for items the frozen answer does not leave to the game.
    /// </summary>
    internal bool GovernsLine(CastleId character, MaterialOperation operation, int item)
    {
        var s = Open(character, operation, fresh: false);
        return s.Governed && s.Checked && SpecOf(s, item) != null;
    }

    /// <summary>
    /// Payment, after the game paid what the bag and the station held: pay <paramref name="amount"/>
    /// from this operation's pool. A line its quote and the stock cover is taken whole or not at all.
    /// A line they do not cover takes what they allow and leaves the rest owed (the game's own shortfall
    /// behavior); earlier lines keep their debits.
    /// </summary>
    internal MaterialPayResult Pay(CastleId character, MaterialOperation operation, int item, int amount)
    {
        if (amount <= 0) return new MaterialPayResult(0, 0, "");
        // A display-only operation (RefineView) never pays, whatever scope routed the call here.
        if (MaterialOperations.IsDisplayOnly(operation)) return new MaterialPayResult(amount, 0, "display-only operation");
        var s = Open(character, operation, fresh: false);
        if (!s.Governed) return new MaterialPayResult(amount, 0, "not governed");
        if (!s.Checked) return new MaterialPayResult(amount, 0, "no governed check in this operation");
        var spec = SpecOf(s, item);
        if (spec == null) return new MaterialPayResult(amount, 0, "item not governed");

        // Validate before taking anything: the check saw the quote, so the line may not draw more than
        // what is left of it (an item the check never quoted had no pool stock: quote 0); then plan
        // against what the containers hold now.
        s.Spent.TryGetValue(item, out int spent);
        int quote = s.Quote.TryGetValue(item, out int q) ? q : 0;
        int allowed = Math.Max(0, Math.Min(amount, quote - spent));
        string why = allowed < amount ? "the check saw " + quote + " (already paid " + spent + ")" : "";
        var steps = new List<MaterialStep<InventoryRef>>();
        int lost = 0;
        if (allowed == amount)
        {
            var plan = MaterialPlanner.Plan(new[] { new MaterialLine(item, amount) }, _ => spec, _world.Ledger, s.Exclusions);
            if (plan.Ok)
            {
                var commit = MaterialPlanner.Commit(plan, _world.Ledger);
                if (commit.Ok) steps.AddRange(commit.Taken);
                else
                {
                    if (commit.Outcome == MaterialCommitOutcome.Inconsistent)
                    {
                        // Units the give-back could not put back already left the pool: they count as paid,
                        // so the retry below never takes them a second time.
                        lost = Math.Min(commit.Unrestored, allowed);
                        _log("ERROR materials: pay Inconsistent " + s.Context + " tx=" + s.Transaction + " item=" + item + " amount=" + amount
                            + ": " + commit.Reason + "; " + lost + " unit(s) counted as paid");
                    }
                    why = commit.Outcome + ": " + commit.Reason;
                }
            }
            else why = "short: " + MaterialPlanner.DescribeShort(plan);
        }
        if (steps.Count == 0 && allowed - lost > 0)
            TakeWhatIsThere(item, allowed - lost, spec, s.Exclusions, steps);

        int paid = lost;
        var debits = new List<MaterialDebit>(steps.Count + 1);
        if (lost > 0) debits.Add(new MaterialDebit(ContainerId.None, lost));
        foreach (var step in steps)
        {
            paid += step.Amount;
            debits.Add(new MaterialDebit(_world.IdOf(step.Source), step.Amount));
        }
        if (paid > 0)
        {
            s.Spent[item] = spent + paid;
            s.Paid.Add(new MaterialPaid(s.Context, s.Transaction, item, amount, paid, debits));
            _registry.CountPayment();
            _log("materials: paid " + s.Context + " tx=" + s.Transaction + " item=" + item + " amount=" + paid
                + (paid < amount ? " of " + amount : "") + " from=" + string.Join(",", debits));
        }
        if (paid < amount)
        {
            if (!s.ShortLogged)
            {
                s.ShortLogged = true;
                _registry.CountShortfall();
                _log("materials: shortfall at payment " + s.Context + " tx=" + s.Transaction + " item=" + item
                    + " owed=" + (amount - paid) + " of " + amount + ": " + why
                    + "; the game completes the action as it does when its own removal comes back short");
            }
            return new MaterialPayResult(amount - paid, paid, why);
        }
        return new MaterialPayResult(0, paid, "");
    }

    /// <summary>
    /// Best effort, never given back: take up to <paramref name="amount"/> in spend order, keeping what
    /// each container actually gave; a container that gives less than asked is passed over and the next
    /// one is asked for the rest.
    /// </summary>
    private void TakeWhatIsThere(int item, int amount, MaterialPoolSpec<InventoryRef> spec, HashSet<InventoryRef> exclude,
        List<MaterialStep<InventoryRef>> steps)
    {
        var pool = MaterialPlanner.Pool(item, spec, _world.Ledger, exclude);
        int left = Math.Min(amount, pool.Available);
        foreach (var (source, count) in pool.Rows)
        {
            if (left <= 0) break;
            int want = Math.Min(left, count);
            int got;
            try { got = _world.Ledger.Remove(source, item, want); }
            catch { got = 0; }
            if (got > 0)
            {
                steps.Add(new MaterialStep<InventoryRef>(source, item, got));
                left -= got;
            }
        }
    }

    // ---- whole request (diagnostics, tests) ---------------------------------------------------------

    /// <summary>Plan a whole request without taking anything, from a fresh provider answer.</summary>
    internal MaterialPlan<InventoryRef>? PlanRequest(CastleId character, MaterialOperation operation, IEnumerable<MaterialLine> lines)
    {
        if (MaterialOperations.IsDisplayOnly(operation)) return null;
        var s = Build(character, operation);
        if (!s.Governed) return null;
        return MaterialPlanner.Plan(lines, item => SpecOf(s, item), _world.Ledger, s.Exclusions);
    }

    /// <summary>Validate every line, then commit: every line from the pool, or nothing.</summary>
    internal MaterialCommit<InventoryRef>? PayRequest(CastleId character, MaterialOperation operation, IEnumerable<MaterialLine> lines)
    {
        var plan = PlanRequest(character, operation, lines);
        if (plan == null) return null;
        var commit = MaterialPlanner.Commit(plan, _world.Ledger);
        if (commit.Ok) _registry.CountPayment();
        return commit;
    }

    // ---- sessions -----------------------------------------------------------------------------------

    private Session Open(CastleId character, MaterialOperation operation, bool fresh)
    {
        // Seam sessions (check, compose, payment) are never opened for a display-only operation: such a
        // session is not governed, so the game keeps its own count and pays by its own rules. Its views
        // are computed by the refresh, which does not go through here.
        if (MaterialOperations.IsDisplayOnly(operation)) return new Session();
        var key = (character, operation);
        if (_sessions.TryGetValue(key, out var existing))
        {
            if (!fresh) return existing;
            Close(existing);
        }
        var s = Build(character, operation);
        _sessions[key] = s;
        return s;
    }

    /// <summary>Ask the provider once for this context: governed or not, and its candidates.</summary>
    private Session Build(CastleId character, MaterialOperation operation, MaterialStockReads? reads = null)
    {
        var s = new Session { Transaction = ++_transactions, Reads = reads };
        if (!_registry.Governs(operation)) return s;
        var provider = _registry.Provider;
        if (provider == null) return s;
        if (!_world.TryContext(character, operation, out s.Context)) return s;
        s.HasContext = true;
        // Character crafting off a plot: the game reaches no shared storage there, so the provider is
        // not asked and the craft keeps the game's own rules.
        if (MaterialOperations.RequiresStandingPlot(operation) && s.Context.StandingTerritory < 0)
        {
            s.PlotRefused = true;
            return s;
        }
        IReadOnlyList<ContainerId>? candidates;
        long t0 = Stopwatch.GetTimestamp();
        try { candidates = provider.Candidates(in s.Context); }
        catch (Exception e)
        {
            // A throwing provider governs with no remote sources; it never falls back to the
            // game's shared storage, which could be less restrictive than the provider's rules.
            _providerTicks += Stopwatch.GetTimestamp() - t0;
            Fault("Candidates", e);
            s.Provider = provider;
            s.Governed = true;
            s.ProviderFaulted = true;
            Prepare(s);
            return s;
        }
        _providerTicks += Stopwatch.GetTimestamp() - t0;
        if (candidates == null || candidates.Count == 0) return s;
        s.Provider = provider;
        s.Governed = true;
        s.Candidates = candidates;
        Prepare(s);
        return s;
    }

    private void Prepare(Session s)
    {
        _world.VanillaCounted(in s.Context, s.Exclusions);
        s.Front = _world.PoolFront(in s.Context);
        s.Station = _world.StationInventory(in s.Context);
    }

    /// <summary>
    /// The frozen pool spec for one item: the provider is asked at most once per session and item, and
    /// never after the check (an item the check did not see exists nowhere the game counts, so it is
    /// governed with an empty pool; no provider call can run between the check and the payment).
    /// </summary>
    private MaterialPoolSpec<InventoryRef>? SpecOf(Session s, int item)
    {
        if (s.Specs.TryGetValue(item, out var known)) return known;
        ItemSources? sources;
        if (s.ProviderFaulted || s.Checked) sources = EmptySources;
        else
        {
            long t0 = Stopwatch.GetTimestamp();
            try { sources = s.Provider!.Sources(in s.Context, item); }
            catch (Exception e)
            {
                Fault("Sources", e);
                // A throwing provider means "no remote sources", never "vanilla storage rules".
                sources = EmptySources;
            }
            finally { _providerTicks += Stopwatch.GetTimestamp() - t0; }
        }
        MaterialPoolSpec<InventoryRef>? spec = null;
        if (sources != null && s.Reads != null && TryMemoOrder(s, sources.SpendOrder, out var memo))
            spec = new MaterialPoolSpec<InventoryRef>(memo, sources.PoolReserve);
        else if (sources != null)
        {
            var order = new List<InventoryRef>(sources.SpendOrder.Count + 1);
            if (!s.Front.IsNone) order.Add(s.Front);
            foreach (var id in sources.SpendOrder)
            {
                if (id.IsNone || !Resolve(s, id, out var inv) || inv.IsNone) continue;
                // The craft's own station is never a provider source: the game already paid from it
                // (a plain station) or it pays first ahead of the order (shared storage).
                if (!s.Station.IsNone && inv.OwnerIndex == s.Station.OwnerIndex && inv.OwnerVersion == s.Station.OwnerVersion) continue;
                order.Add(inv);
            }
            spec = new MaterialPoolSpec<InventoryRef>(order, sources.PoolReserve);
            if (s.Reads != null && s.OrderMemo.Count < 16) s.OrderMemo.Add((ToArray(sources.SpendOrder), order));
        }
        s.Specs[item] = spec;
        return spec;
    }

    /// <summary>
    /// A view's items often share one spend order (the provider hands back the same list): resolve an
    /// order once per view and reuse it for every item that names the same containers in the same order
    /// (compared element by element, so a list the provider changed between calls is resolved again).
    /// </summary>
    private static bool TryMemoOrder(Session s, IReadOnlyList<ContainerId> ids, out List<InventoryRef> order)
    {
        foreach (var (known, resolved) in s.OrderMemo)
        {
            if (known.Length != ids.Count) continue;
            bool same = true;
            for (int i = 0; i < known.Length && same; i++) same = known[i].Equals(ids[i]);
            if (!same) continue;
            order = resolved;
            return true;
        }
        order = null!;
        return false;
    }

    private static ContainerId[] ToArray(IReadOnlyList<ContainerId> ids)
    {
        var copy = new ContainerId[ids.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = ids[i];
        return copy;
    }

    private static readonly ItemSources EmptySources = new(Array.Empty<ContainerId>());

    /// <summary>Items worth asking about: the candidates' and the pool front's stock, plus the given extras.</summary>
    private HashSet<int> DiscoverItems(Session s, IEnumerable<int>? extra, IEnumerable<int>? more)
    {
        var items = new HashSet<int>();
        var seen = new HashSet<InventoryRef>();
        foreach (var id in s.Candidates)
            if (!id.IsNone && Resolve(s, id, out var inv) && seen.Add(inv)) ItemsOf(s, inv, items);
        if (!s.Front.IsNone && seen.Add(s.Front)) ItemsOf(s, s.Front, items);
        if (extra != null) foreach (int i in extra) items.Add(i);
        if (more != null) foreach (int i in more) items.Add(i);
        items.Remove(0);
        return items;
    }

    /// <summary>A view resolves through its refresh's read cache; an operation resolves live.</summary>
    private bool Resolve(Session s, ContainerId id, out InventoryRef inventory) =>
        s.Reads != null ? s.Reads.TryResolve(id, out inventory) : _world.TryResolve(id, out inventory);

    private void ItemsOf(Session s, InventoryRef inventory, ICollection<int> into)
    {
        if (s.Reads != null) s.Reads.Stock(inventory).AddItems(into);
        else _world.Items(inventory, into);
    }

    // ---- fault injection (development bridge only) -----------------------------------------------------

    private (ContainerId From, ContainerId To, int Item)? _injectedMove;

    /// <summary>
    /// Development fault injection: right after the next governed check, move every unit of
    /// <paramref name="item"/> from <paramref name="from"/> to <paramref name="to"/> through the ledger
    /// (total-conserving), so that operation's payment meets a container that changed between its check
    /// and its payment. One shot. Reached only through the development bridge's <c>materials</c> op.
    /// </summary>
    internal void InjectMoveAfterNextCheck(ContainerId from, ContainerId to, int item) => _injectedMove = (from, to, item);

    /// <summary>Drop an armed injection that has not run.</summary>
    internal void ClearInjection() => _injectedMove = null;

    /// <summary>The armed injection, for status.</summary>
    internal string DescribeInjection() =>
        _injectedMove is { } m ? "move " + m.Item + " from " + m.From + " to " + m.To + " after the next check" : "none";

    private void RunInjectedMove(Session s)
    {
        if (_injectedMove is not { } m) return;
        _injectedMove = null;
        if (!_world.TryResolve(m.From, out var from) || !_world.TryResolve(m.To, out var to))
        {
            _log("materials: fault injection skipped: a container did not resolve");
            return;
        }
        int n = _world.Ledger.Count(from, m.Item);
        int taken = n > 0 ? _world.Ledger.Remove(from, m.Item, n) : 0;
        int added = taken > 0 ? _world.Ledger.Add(to, m.Item, taken) : 0;
        if (added < taken) _world.Ledger.Add(from, m.Item, taken - added);
        _log("materials: fault injection moved " + added + " of item " + m.Item + " after the check of tx=" + s.Transaction);
    }

    private void Fault(string member, Exception e)
    {
        _registry.CountFault();
        _faultCount++;
        if (_faultLines++ < 20)
            _log("materials: provider." + member + " threw (owner=" + _registry.Owner + "): " + e.Message);
    }

    private sealed class Session
    {
        internal long Transaction;
        internal MaterialContext Context;
        internal bool HasContext;
        /// <summary>The operation needs a standing plot and the character stands on none: the provider was not asked.</summary>
        internal bool PlotRefused;
        internal bool Governed;
        internal bool ProviderFaulted;
        internal IMaterialSourceProvider? Provider;
        internal IReadOnlyList<ContainerId> Candidates = Array.Empty<ContainerId>();
        /// <summary>A displayed view's per-refresh read cache; null for an operation (its check and payment read live).</summary>
        internal MaterialStockReads? Reads;
        /// <summary>A view's resolved spend orders (<see cref="TryMemoOrder"/>).</summary>
        internal readonly List<(ContainerId[] Ids, List<InventoryRef> Order)> OrderMemo = new();
        internal InventoryRef Front = InventoryRef.None;
        internal readonly HashSet<InventoryRef> Exclusions = new();
        internal readonly Dictionary<int, MaterialPoolSpec<InventoryRef>?> Specs = new();
        internal readonly Dictionary<int, int> Quote = new();
        internal readonly Dictionary<int, int> Spent = new();
        internal readonly List<MaterialPaid> Paid = new();
        internal InventoryRef Station = InventoryRef.None;
        internal bool Composed;
        internal bool Checked;
        internal bool ShortLogged;
    }
}
