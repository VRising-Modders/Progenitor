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
    /// The craft's own station inventory (main), whatever kind it is, or None. A provider source equal to
    /// it is dropped: the game pays from a plain station itself, and a shared one pays first.
    /// </summary>
    InventoryRef StationInventory(in MaterialContext context);

    /// <summary>
    /// Item types the game's own shared storage holds at the plot the player stands on (read only,
    /// discovery only: amounts are not needed). The view asks the provider about each of them, so a
    /// governed item the provider's containers lack is sent as 0 and hides the game's shared amount.
    /// </summary>
    void VanillaSharedItems(in MaterialContext context, ICollection<int> into);

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
/// The game-free core of the material seams.
///
/// <para><b>One operation, one answer.</b> A governed game operation (one craft start, one placement)
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
        _registry.CountView();
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
    /// Outside any operation: a throwaway session, nothing is paid from it.
    /// </summary>
    internal bool TryView(CastleId character, MaterialOperation operation, Dictionary<int, int> totals, out MaterialContext context, out long revision)
    {
        totals.Clear();
        revision = 0;
        var s = Build(character, operation);
        context = s.Context;
        if (!s.Governed) return false;
        try { revision = s.Provider!.Revision(in s.Context); }
        catch (Exception e) { Fault("Revision", e); }
        var shared = new List<int>();
        if (s.HasContext) _world.VanillaSharedItems(in s.Context, shared);
        foreach (int item in DiscoverItems(s, null, shared))
        {
            var spec = SpecOf(s, item);
            if (spec == null) continue;
            totals[item] = MaterialPlanner.Pool(item, spec, _world.Ledger, s.Exclusions).Available;
        }
        _registry.CountView();
        return true;
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
    private Session Build(CastleId character, MaterialOperation operation)
    {
        var s = new Session { Transaction = ++_transactions };
        if (!_registry.Governs(operation)) return s;
        var provider = _registry.Provider;
        if (provider == null) return s;
        if (!_world.TryContext(character, operation, out s.Context)) return s;
        s.HasContext = true;
        IReadOnlyList<ContainerId>? candidates;
        try { candidates = provider.Candidates(in s.Context); }
        catch (Exception e)
        {
            // A throwing provider governs with no remote sources; it never falls back to the
            // game's shared storage, which could be less restrictive than the provider's rules.
            Fault("Candidates", e);
            s.Provider = provider;
            s.Governed = true;
            s.ProviderFaulted = true;
            Prepare(s);
            return s;
        }
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
            try { sources = s.Provider!.Sources(in s.Context, item); }
            catch (Exception e)
            {
                Fault("Sources", e);
                // A throwing provider means "no remote sources", never "vanilla storage rules".
                sources = EmptySources;
            }
        }
        MaterialPoolSpec<InventoryRef>? spec = null;
        if (sources != null)
        {
            var order = new List<InventoryRef>(sources.SpendOrder.Count + 1);
            if (!s.Front.IsNone) order.Add(s.Front);
            foreach (var id in sources.SpendOrder)
            {
                if (id.IsNone || !_world.TryResolve(id, out var inv) || inv.IsNone) continue;
                // The craft's own station is never a provider source: the game already paid from it
                // (a plain station) or it pays first ahead of the order (shared storage).
                if (!s.Station.IsNone && inv.OwnerIndex == s.Station.OwnerIndex && inv.OwnerVersion == s.Station.OwnerVersion) continue;
                order.Add(inv);
            }
            spec = new MaterialPoolSpec<InventoryRef>(order, sources.PoolReserve);
        }
        s.Specs[item] = spec;
        return spec;
    }

    private static readonly ItemSources EmptySources = new(Array.Empty<ContainerId>());

    /// <summary>Items worth asking about: the candidates' and the pool front's stock, plus the given extras.</summary>
    private HashSet<int> DiscoverItems(Session s, IEnumerable<int>? extra, IEnumerable<int>? more)
    {
        var items = new HashSet<int>();
        var seen = new HashSet<InventoryRef>();
        foreach (var id in s.Candidates)
            if (!id.IsNone && _world.TryResolve(id, out var inv) && seen.Add(inv)) _world.Items(inv, items);
        if (!s.Front.IsNone && seen.Add(s.Front)) _world.Items(s.Front, items);
        if (extra != null) foreach (int i in extra) items.Add(i);
        if (more != null) foreach (int i in more) items.Add(i);
        items.Remove(0);
        return items;
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
        if (_faultLines++ < 20)
            _log("materials: provider." + member + " threw (owner=" + _registry.Owner + "): " + e.Message);
    }

    private sealed class Session
    {
        internal long Transaction;
        internal MaterialContext Context;
        internal bool HasContext;
        internal bool Governed;
        internal bool ProviderFaulted;
        internal IMaterialSourceProvider? Provider;
        internal IReadOnlyList<ContainerId> Candidates = Array.Empty<ContainerId>();
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
