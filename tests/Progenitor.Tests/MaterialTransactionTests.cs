using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Inventory;
using Progenitor.Materials;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// One governed game operation (one craft start, one placement) is one transaction: the provider is
/// asked once at the game's check, every cost line is paid from that answer, a line that cannot be paid
/// in full takes what its quote and the stock allow (the game's own shortfall behavior, earlier lines
/// kept), and events are published after the game system finished.
/// </summary>
public partial class MaterialServiceTests
{
    /// <summary>The game's check: the merged count inside a governed scope (gate, then compose).</summary>
    private static List<MaterialLine> Check(MaterialService service, CastleId character, MaterialOperation op,
        Dictionary<int, long>? full = null, Dictionary<int, long>? bag = null)
    {
        var extra = new List<MaterialLine>();
        Assert.True(service.CountGate(character, op));
        Assert.True(service.ComposeExtraRows(character, op, full ?? new Dictionary<int, long>(), bag ?? new Dictionary<int, long>(), extra));
        service.MarkChecked(character, op); // the composed rows fit the seam's buffer
        return extra;
    }

    /// <summary>The game's payment of one line: the gate (-1 = the game pays it by its own rules), then the pool's part.</summary>
    private static int PayLine(MaterialService service, CastleId character, MaterialOperation op, int item, int amount)
    {
        if (!service.GovernsLine(character, op, item)) return -1;
        return service.Pay(character, op, item, amount).Remainder;
    }

    [Fact]
    public void Every_line_of_an_operation_is_paid_from_the_answer_the_check_saw()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        var a = world.Chest(1, (Iron, 10));
        var b = world.Chest(2, (Iron, 10), (Wood, 6));
        provider.All.AddRange(new[] { a, b });
        provider.Orders[Iron] = new List<ContainerId> { a, b };
        registry.Register(provider, "test");
        service.BeginScope(1);
        Check(service, world.Player, MaterialOperation.Craft);

        // The provider changes its mind (and then breaks) after the check: the operation keeps its answer.
        provider.Orders[Iron] = new List<ContainerId> { b, a };
        provider.ThrowOnSources = true;
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Craft, Iron, 12));
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Craft, Wood, 6));
        Assert.Equal(0, world.Get(a, Iron));
        Assert.Equal(8, world.Get(b, Iron));
        Assert.Equal(1, provider.SourcesCalls[Iron]);
        Assert.Equal(1, provider.SourcesCalls[Wood]);
        Assert.Equal(0, registry.Status().Faults);

        service.EndScope();
        Assert.Equal(2, paid.Count);
        Assert.Single(paid.Select(p => p.Transaction).Distinct());
    }

    [Fact]
    public void A_provider_that_throws_at_the_check_shows_an_empty_pool_and_nothing_is_taken()
    {
        var (world, provider, registry, service, paid, log) = Setup();
        var a = world.Chest(1, (Cloth, 10));
        provider.All.Add(a);
        provider.ThrowOnCandidates = true;
        registry.Register(provider, "test");
        service.BeginScope(1);
        // The game's own shared storage holds cloth; the check must not see it.
        var rows = Check(service, world.Player, MaterialOperation.Build, new Dictionary<int, long> { [Cloth] = 40 });
        Assert.DoesNotContain(rows, r => r.Item == Cloth && r.Amount > 0);
        Assert.Equal(3, PayLine(service, world.Player, MaterialOperation.Build, Cloth, 3));
        service.EndScope();
        Assert.Equal(10, world.Get(a, Cloth));
        Assert.Empty(paid);
        Assert.Equal(1, registry.Status().Faults);
        Assert.Contains(log, l => l.Contains("provider.Candidates threw"));
    }

    [Fact]
    public void A_provider_that_throws_for_one_item_shows_an_empty_pool_for_that_item_only()
    {
        var (world, provider, registry, service, _, _) = Setup();
        provider.All.Add(world.Chest(1, (Cloth, 10), (Iron, 4)));
        provider.ThrowFor.Add(Cloth);
        registry.Register(provider, "test");
        service.BeginScope(1);
        var rows = Check(service, world.Player, MaterialOperation.Build, new Dictionary<int, long> { [Cloth] = 40 });
        Assert.DoesNotContain(rows, r => r.Item == Cloth);
        Assert.Equal(4, rows.Single(r => r.Item == Iron).Amount);
        Assert.Equal(1, registry.Status().Faults);
    }

    [Fact]
    public void A_container_removed_between_check_and_pay_leaves_that_line_short_and_keeps_earlier_lines()
    {
        var (world, provider, registry, service, paid, log) = Setup();
        var a = world.Chest(1, (Iron, 5));
        var b = world.Chest(2, (Wood, 4));
        provider.All.AddRange(new[] { a, b });
        registry.Register(provider, "test");
        service.BeginScope(1);
        Check(service, world.Player, MaterialOperation.Craft);

        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Craft, Iron, 5));
        // The wood chest is destroyed between the check and its line.
        world.Stock.Remove((world.Ids[b], Wood));
        world.Ids.Remove(b);

        Assert.Equal(4, PayLine(service, world.Player, MaterialOperation.Craft, Wood, 4));
        Assert.Equal(0, world.Get(a, Iron)); // the earlier line keeps its debit, as the game's own removal would
        service.EndScope();
        Assert.Equal(Iron, paid.Single().Item);
        Assert.Equal(1, registry.Status().Shortfalls);
        Assert.Single(log, l => l.Contains("shortfall at payment"));
    }

    [Fact]
    public void A_container_emptied_between_check_and_pay_takes_what_is_left()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        var a = world.Chest(1, (Iron, 8));
        var b = world.Chest(2, (Iron, 8));
        provider.All.AddRange(new[] { a, b });
        registry.Register(provider, "test");
        service.BeginScope(1);
        var rows = Check(service, world.Player, MaterialOperation.Build);
        Assert.Equal(16, rows.Single(r => r.Item == Iron).Amount);
        world.Stock[(world.Ids[b], Iron)] = 1; // something else took from b in between

        Assert.Equal(3, PayLine(service, world.Player, MaterialOperation.Build, Iron, 12));
        Assert.Equal(0, world.Get(a, Iron));
        Assert.Equal(0, world.Get(b, Iron));
        service.EndScope();
        Assert.Equal(9, paid.Single().Paid);
        Assert.Equal(12, paid.Single().Requested);
    }

    [Fact]
    public void A_write_fault_restores_that_lines_commit_then_keeps_what_the_containers_gave()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        var a = world.Chest(1, (Iron, 4), (Wood, 2));
        var b = world.Chest(2, (Wood, 5));
        provider.All.AddRange(new[] { a, b });
        provider.Orders[Wood] = new List<ContainerId> { a, b };
        registry.Register(provider, "test");
        service.BeginScope(1);
        Check(service, world.Player, MaterialOperation.Craft);
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Craft, Iron, 4));
        // The second container gives one unit less than asked.
        var bRef = world.Ids[b];
        world.RemoveOverride = (inv, item, amount) => inv.Equals(bRef) ? amount - 1 : amount;
        Assert.Equal(1, PayLine(service, world.Player, MaterialOperation.Craft, Wood, 6));
        world.RemoveOverride = null;
        Assert.Equal(0, world.Get(a, Iron)); // the earlier line is never given back
        Assert.Equal(0, world.Get(a, Wood));
        Assert.Equal(2, world.Get(b, Wood)); // 5 - 3 actually given; nothing double-taken by the retry
        service.EndScope();
        Assert.Equal(new[] { 4, 5 }, paid.Select(p => p.Paid).ToArray());
    }

    [Fact]
    public void A_commit_that_cannot_give_back_is_reported_as_an_error()
    {
        var (world, provider, registry, service, paid, log) = Setup();
        var a = world.Chest(1, (Wood, 2));
        var b = world.Chest(2, (Wood, 5));
        provider.All.AddRange(new[] { a, b });
        provider.Orders[Wood] = new List<ContainerId> { a, b };
        registry.Register(provider, "test");
        service.BeginScope(1);
        Check(service, world.Player, MaterialOperation.Craft);
        var bRef = world.Ids[b];
        world.RemoveOverride = (inv, item, amount) => inv.Equals(bRef) ? 0 : amount;
        world.AddOverride = (_, _, amount) => amount - 1;
        // Commit takes 2 from a, b refuses, the give-back returns only 1 to a: 1 unit is lost and counts
        // as paid; the retry takes the 1 left in a, b still refuses.
        Assert.Equal(4, PayLine(service, world.Player, MaterialOperation.Craft, Wood, 6));
        Assert.Equal(0, world.Get(a, Wood));
        Assert.Equal(5, world.Get(b, Wood));
        Assert.Contains(log, l => l.StartsWith("ERROR materials: pay Inconsistent") && l.Contains("1 unit(s) counted as paid"));
        service.EndScope();
        var e = Assert.Single(paid);
        Assert.Equal(2, e.Paid);
        Assert.Equal(new[] { new MaterialDebit(ContainerId.None, 1).ToString(), new MaterialDebit(a, 1).ToString() }, e.Debits.Select(d => d.ToString()));
    }

    [Fact]
    public void A_container_that_refuses_is_passed_over_and_the_next_one_pays()
    {
        var (world, provider, registry, service, paid, log) = Setup();
        var a = world.Chest(1, (Wood, 3));
        var b = world.Chest(2, (Wood, 10));
        provider.All.AddRange(new[] { a, b });
        provider.Orders[Wood] = new List<ContainerId> { a, b };
        registry.Register(provider, "test");
        service.BeginScope(1);
        Check(service, world.Player, MaterialOperation.Craft);
        var aRef = world.Ids[a];
        world.RemoveOverride = (inv, item, amount) => inv.Equals(aRef) ? 0 : amount;
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Craft, Wood, 6));
        Assert.Equal(3, world.Get(a, Wood));
        Assert.Equal(4, world.Get(b, Wood));
        Assert.DoesNotContain(log, l => l.Contains("shortfall"));
        service.EndScope();
        Assert.Equal(6, Assert.Single(paid).Paid);
    }

    [Fact]
    public void Repeated_lines_for_one_item_cannot_spend_more_than_the_check_saw()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        var a = world.Chest(1, (Cloth, 8));
        world.Stock[(world.Ids[a], Cloth)] = 8;
        provider.All.Add(a);
        registry.Register(provider, "test");
        service.BeginScope(1);
        Check(service, world.Player, MaterialOperation.Craft);
        world.Stock[(world.Ids[a], Cloth)] = 20; // more arrives after the check: still only the quote is spent
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Craft, Cloth, 5));
        Assert.Equal(2, PayLine(service, world.Player, MaterialOperation.Craft, Cloth, 5));
        Assert.Equal(12, world.Get(a, Cloth));
        service.EndScope();
        Assert.Equal(new[] { 5, 3 }, paid.Select(p => p.Paid).ToArray());
    }

    [Fact]
    public void An_item_the_provider_leaves_to_the_game_is_paid_and_shown_exactly_as_vanilla()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        var a = world.Chest(1, (Cloth, 8), (Wood, 50));
        provider.All.Add(a);
        provider.LeaveToGame.Add(Wood);
        registry.Register(provider, "test");
        service.BeginScope(1);
        var rows = Check(service, world.Player, MaterialOperation.Craft,
            new Dictionary<int, long> { [Wood] = 13, [Cloth] = 2 }, new Dictionary<int, long> { [Wood] = 3, [Cloth] = 2 });
        Assert.Equal(10, rows.Single(r => r.Item == Wood).Amount); // the game's own shared amount, not the pool's 50
        Assert.Equal(8, rows.Single(r => r.Item == Cloth).Amount);
        // The payment gate says "not governed": the game's own removal (with its shared storage) pays it.
        Assert.Equal(-1, PayLine(service, world.Player, MaterialOperation.Craft, Wood, 10));
        Assert.Equal(50, world.Get(a, Wood));
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Craft, Cloth, 8));
        service.EndScope();
        Assert.Equal(Cloth, paid.Single().Item);
    }

    [Fact]
    public void A_new_check_starts_a_new_transaction_and_a_new_scope_publishes_the_last()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        var a = world.Chest(1, (Cloth, 10));
        provider.All.Add(a);
        registry.Register(provider, "test");
        service.BeginScope(1);
        Check(service, world.Player, MaterialOperation.Craft);
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Craft, Cloth, 4));
        var rows = Check(service, world.Player, MaterialOperation.Craft); // a second craft event in the same update
        Assert.Equal(6, rows.Single(r => r.Item == Cloth).Amount);
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Craft, Cloth, 6));
        Assert.Empty(paid);
        service.BeginScope(2); // the previous scope's system threw before its closing hook
        Assert.Empty(paid);    // never published from inside a seam call
        Assert.Equal(0, service.OpenSessions);
        Assert.Equal(2, service.PendingEvents);
        service.EndScope();
        Assert.Equal(2, paid.Count);
        Assert.NotEqual(paid[0].Transaction, paid[1].Transaction);
    }

    [Fact]
    public void A_shortfall_is_logged_once_per_operation_and_the_next_operation_starts_clean()
    {
        var (world, provider, registry, service, paid, log) = Setup();
        var a = world.Chest(1, (Cloth, 3), (Iron, 1));
        provider.All.Add(a);
        registry.Register(provider, "test");
        service.BeginScope(1);
        Check(service, world.Player, MaterialOperation.Craft);
        Assert.Equal(1, PayLine(service, world.Player, MaterialOperation.Craft, Cloth, 4));
        Assert.Equal(1, PayLine(service, world.Player, MaterialOperation.Craft, Iron, 2));
        Assert.Single(log, l => l.Contains("shortfall at payment"));
        world.Stock[(world.Ids[a], Cloth)] = 5;
        var rows = Check(service, world.Player, MaterialOperation.Craft);
        Assert.Equal(5, rows.Single(r => r.Item == Cloth).Amount);
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Craft, Cloth, 5));
        service.EndScope();
        Assert.Equal(1, registry.Status().Shortfalls);
        Assert.Equal(3, paid.Count);
    }

    [Fact]
    public void A_character_that_is_not_a_player_is_never_governed_and_never_shares_a_session()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        var a = world.Chest(1, (Cloth, 3));
        provider.All.Add(a);
        registry.Register(provider, "test");
        service.BeginScope(1);
        Check(service, world.Player, MaterialOperation.Craft);
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Craft, Cloth, 3));
        Assert.False(service.CountGate(new CastleId(77, 1), MaterialOperation.Craft));
        Assert.Equal(-1, PayLine(service, new CastleId(77, 1), MaterialOperation.Craft, Cloth, 1));
        service.EndScope();
        Assert.Single(paid);
    }

    [Fact]
    public void Without_a_governed_check_the_game_pays_by_its_own_rules()
    {
        // The seam kept the game's own count (an unexpected array shape, or composed rows that did not
        // fit the buffer): the payment must follow what that check saw, so the line is the game's.
        var (world, provider, registry, service, paid, _) = Setup();
        var a = world.Chest(1, (Cloth, 10));
        provider.All.Add(a);
        registry.Register(provider, "test");
        service.BeginScope(1);
        Assert.True(service.CountGate(world.Player, MaterialOperation.Craft));
        Assert.Equal(-1, PayLine(service, world.Player, MaterialOperation.Craft, Cloth, 4));
        Assert.True(service.ComposeExtraRows(world.Player, MaterialOperation.Craft, new Dictionary<int, long>(), new Dictionary<int, long>(), new List<MaterialLine>()));
        Assert.Equal(-1, PayLine(service, world.Player, MaterialOperation.Craft, Cloth, 4)); // composed, never marked
        Assert.Equal(4, service.Pay(world.Player, MaterialOperation.Craft, Cloth, 4).Remainder);
        service.EndScope();
        Assert.Equal(10, world.Get(a, Cloth));
        Assert.Empty(paid);
    }

    [Fact]
    public void The_provider_is_never_called_between_the_check_and_the_payment()
    {
        var (world, provider, registry, service, _, _) = Setup();
        provider.All.Add(world.Chest(1, (Cloth, 10)));
        registry.Register(provider, "test");
        service.BeginScope(1);
        Check(service, world.Player, MaterialOperation.Craft);
        int calls = provider.SourcesCalls.Values.Sum();
        int candidates = provider.Seen.Count;
        // An item the check never saw exists nowhere the game counts: governed with an empty pool.
        Assert.Equal(2, PayLine(service, world.Player, MaterialOperation.Craft, 4242, 2));
        Assert.Equal(calls, provider.SourcesCalls.Values.Sum());
        Assert.Equal(candidates, provider.Seen.Count);
    }

    [Fact]
    public void Injected_change_between_check_and_pay_leaves_the_line_short_and_conserves_items()
    {
        var (world, provider, registry, service, paid, log) = Setup();
        var a = world.Chest(1, (Iron, 6), (Wood, 3));
        var sink = world.Chest(2);
        provider.All.Add(a);
        registry.Register(provider, "test");
        service.InjectMoveAfterNextCheck(a, sink, Wood);
        Assert.Contains("move", service.DescribeInjection());
        service.BeginScope(1);
        var rows = Check(service, world.Player, MaterialOperation.Craft);
        Assert.Equal(3, rows.Single(r => r.Item == Wood).Amount); // the quote was read before the move
        Assert.Equal(3, world.Get(sink, Wood));
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Craft, Iron, 6));
        Assert.Equal(3, PayLine(service, world.Player, MaterialOperation.Craft, Wood, 3));
        service.EndScope();
        Assert.Equal(0, world.Get(a, Iron));
        Assert.Equal(0, world.Get(a, Wood));
        Assert.Equal(3, world.Get(sink, Wood));
        Assert.Equal(Iron, paid.Single().Item);
        Assert.Equal("none", service.DescribeInjection());
        Assert.Contains(log, l => l.Contains("fault injection moved 3"));
    }

    [Fact]
    public void An_injection_that_throws_still_pays_from_the_pool_the_check_saw()
    {
        var (world, provider, registry, service, paid, log) = Setup();
        var a = world.Chest(1, (Wood, 3));
        var sink = world.Chest(2);
        provider.All.Add(a);
        registry.Register(provider, "test");
        var aRef = world.Ids[a];
        world.RemoveOverride = (inv, item, amount) => inv.Equals(aRef) ? throw new System.InvalidOperationException("write fault") : amount;
        service.InjectMoveAfterNextCheck(a, sink, Wood);
        service.BeginScope(1);
        Assert.True(service.CountGate(world.Player, MaterialOperation.Craft));
        Assert.True(service.ComposeExtraRows(world.Player, MaterialOperation.Craft, new Dictionary<int, long>(), new Dictionary<int, long>(), new List<MaterialLine>()));
        service.MarkChecked(world.Player, MaterialOperation.Craft);
        Assert.Contains(log, l => l.Contains("fault injection threw"));
        Assert.Contains(log, l => l.StartsWith("materials: check ") && l.Contains("tx="));
        world.RemoveOverride = null;
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Craft, Wood, 3)); // never free
        Assert.Equal(0, world.Get(a, Wood));
    }

    [Fact]
    public void The_crafts_own_station_is_never_a_provider_source()
    {
        // The world's bag-and-station answer misses the station (another identification of it) and the
        // provider lists it: it must still never pay as part of the pool, so it is not counted twice.
        var (world, provider, registry, service, _, _) = Setup();
        world.Station = new InventoryRef(50, 1, 0, Epoch);
        world.VanillaCountedMissesStation = true;
        var stationId = new ContainerId(System.Guid.NewGuid());
        world.Ids[stationId] = world.Station;
        world.Stock[(world.Station, Cloth)] = 40;
        var chest = world.Chest(1, (Cloth, 5));
        provider.All.AddRange(new[] { stationId, chest });
        registry.Register(provider, "test");
        service.BeginScope(1);
        var rows = Check(service, world.Player, MaterialOperation.Craft);
        Assert.Equal(5, rows.Single(r => r.Item == Cloth).Amount);
        Assert.Equal(1, PayLine(service, world.Player, MaterialOperation.Craft, Cloth, 6));
        Assert.Equal(40, world.Get(world.Station, Cloth));
        Assert.Equal(0, world.Get(chest, Cloth));
    }

    [Fact]
    public void A_throwing_event_handler_cannot_break_the_operation()
    {
        var world = new FakeWorld();
        var provider = new OrderedProvider();
        var registry = new MaterialSourceRegistry { HookState = () => (true, "") };
        var log = new List<string>();
        var service = new MaterialService(registry, world, log.Add, _ => throw new System.InvalidOperationException("handler bug"));
        var a = world.Chest(1, (Cloth, 3));
        provider.All.Add(a);
        registry.Register(provider, "test");
        service.BeginScope(1);
        Check(service, world.Player, MaterialOperation.Craft);
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Craft, Cloth, 3));
        service.EndScope();
        Assert.Equal(0, world.Get(a, Cloth));
        Assert.Contains(log, l => l.Contains("MaterialPaid handler threw"));
    }

    // ---- Repair and Upgrade: the same transaction ---------------------------------------------------

    private static readonly MaterialOperation[] RepairAndUpgrade = { MaterialOperation.Repair, MaterialOperation.Upgrade };

    [Fact]
    public void Repair_and_upgrade_stay_vanilla_for_a_provider_that_does_not_name_them()
    {
        foreach (var op in RepairAndUpgrade)
        {
            var (world, provider, registry, service, paid, _) = Setup(); // the provider governs Build and Craft
            provider.All.Add(world.Chest(1, (Iron, 10)));
            registry.Register(provider, "test");
            service.BeginScope(1);
            Assert.False(service.CountGate(world.Player, op));
            Assert.False(service.GovernsLine(world.Player, op, Iron));
            Assert.Equal(4, service.Pay(world.Player, op, Iron, 4).Remainder);
            service.EndScope();
            Assert.Empty(paid);
        }
    }

    [Fact]
    public void Repair_and_upgrade_pay_every_line_from_the_checks_answer_with_one_transaction()
    {
        foreach (var op in RepairAndUpgrade)
        {
            var (world, provider, registry, service, paid, _) = Setup();
            provider.Operations = new[] { MaterialOperation.Repair, MaterialOperation.Upgrade };
            var a = world.Chest(1, (Iron, 10));
            var b = world.Chest(2, (Iron, 10), (Wood, 6));
            provider.All.AddRange(new[] { a, b });
            provider.Orders[Iron] = new List<ContainerId> { b, a };
            registry.Register(provider, "test");
            service.BeginScope(1);
            var rows = Check(service, world.Player, op);
            Assert.Equal(20, rows.Single(r => r.Item == Iron).Amount);
            Assert.Equal(op, provider.Seen.Last().Operation);

            provider.Orders[Iron] = new List<ContainerId> { a, b }; // too late: the operation keeps its answer
            Assert.Equal(0, PayLine(service, world.Player, op, Iron, 12));
            Assert.Equal(0, PayLine(service, world.Player, op, Wood, 6));
            Assert.Equal(0, world.Get(b, Iron));
            Assert.Equal(8, world.Get(a, Iron));
            Assert.Empty(paid); // published only when the system's update ends
            service.EndScope();

            Assert.Equal(2, paid.Count);
            Assert.All(paid, p => Assert.Equal(op, p.Context.Operation));
            Assert.Single(paid.Select(p => p.Transaction).Distinct());
            Assert.Equal(new[] { new MaterialDebit(b, 10).ToString(), new MaterialDebit(a, 2).ToString() },
                paid.Single(p => p.Item == Iron).Debits.Select(d => d.ToString()));
        }
    }

    [Fact]
    public void Repair_and_upgrade_leave_items_the_provider_returns_null_for_to_the_game()
    {
        foreach (var op in RepairAndUpgrade)
        {
            var (world, provider, registry, service, paid, _) = Setup();
            provider.Operations = new[] { op };
            var a = world.Chest(1, (Iron, 10), (Wood, 10));
            provider.All.Add(a);
            provider.LeaveToGame.Add(Wood);
            registry.Register(provider, "test");
            service.BeginScope(1);
            // The game's own shared storage holds 7 wood: that item keeps the game's amount.
            var rows = Check(service, world.Player, op, new Dictionary<int, long> { [Wood] = 7 });
            Assert.Equal(7, rows.Single(r => r.Item == Wood).Amount);
            Assert.Equal(10, rows.Single(r => r.Item == Iron).Amount);
            Assert.Equal(-1, PayLine(service, world.Player, op, Wood, 3)); // the game's own removal pays it
            Assert.Equal(0, PayLine(service, world.Player, op, Iron, 3));
            service.EndScope();
            Assert.Equal(10, world.Get(a, Wood));
            Assert.Equal(Iron, paid.Single().Item);
        }
    }

    [Fact]
    public void Repair_and_upgrade_shortfalls_keep_earlier_lines_and_log_once()
    {
        foreach (var op in RepairAndUpgrade)
        {
            var (world, provider, registry, service, paid, log) = Setup();
            provider.Operations = new[] { op };
            var a = world.Chest(1, (Iron, 5));
            var b = world.Chest(2, (Wood, 4), (Cloth, 2));
            provider.All.AddRange(new[] { a, b });
            registry.Register(provider, "test");
            service.BeginScope(1);
            Check(service, world.Player, op);
            Assert.Equal(0, PayLine(service, world.Player, op, Iron, 5));
            world.Stock[(world.Ids[b], Wood)] = 1; // something took from b between the check and the line
            world.Stock[(world.Ids[b], Cloth)] = 0;
            Assert.Equal(3, PayLine(service, world.Player, op, Wood, 4));
            Assert.Equal(2, PayLine(service, world.Player, op, Cloth, 2));
            Assert.Equal(0, world.Get(a, Iron)); // the earlier line keeps its debit
            service.EndScope();
            Assert.Equal(new[] { Iron, Wood }, paid.Select(p => p.Item));
            Assert.Equal(1, paid.Single(p => p.Item == Wood).Paid);
            Assert.Equal(1, registry.Status().Shortfalls);
            Assert.Single(log, l => l.Contains("shortfall at payment") && l.Contains(op.ToString()));
        }
    }

    [Fact]
    public void Repair_at_a_station_leaves_the_station_out_and_a_shared_station_pays_first()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        provider.Operations = new[] { MaterialOperation.Repair };
        world.Station = new InventoryRef(50, 1, 0, Epoch);
        var stationId = new ContainerId(Guid.NewGuid());
        world.Ids[stationId] = world.Station;
        world.Stock[(world.Station, Iron)] = 3;
        var chest = world.Chest(1, (Iron, 10));
        provider.All.AddRange(new[] { stationId, chest }); // a careless provider lists the station
        registry.Register(provider, "test");
        service.BeginScope(1);
        var rows = Check(service, world.Player, MaterialOperation.Repair);
        Assert.Equal(10, rows.Single(r => r.Item == Iron).Amount); // the game pays from the plain station itself
        Assert.Equal(stationId, provider.Seen.Last().Station);
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Repair, Iron, 4));
        Assert.Equal(3, world.Get(world.Station, Iron));
        Assert.Equal(6, world.Get(chest, Iron));
        service.EndScope();

        // The same station as shared storage: the game reaches it only through its shared branch, so it pays first.
        world.StationIsSharedStorage = true;
        service.BeginScope(2);
        Check(service, world.Player, MaterialOperation.Repair);
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Repair, Iron, 4));
        Assert.Equal(0, world.Get(world.Station, Iron));
        Assert.Equal(5, world.Get(chest, Iron));
        service.EndScope();
        Assert.Equal(2, paid.Count);
    }

    [Fact]
    public void Upgrade_names_the_heart_as_its_station_and_never_pays_from_it()
    {
        var (world, provider, registry, service, paid, _) = Setup();
        provider.Operations = new[] { MaterialOperation.Upgrade };
        world.Heart = new InventoryRef(60, 1, 0, Epoch);
        var heartId = new ContainerId(Guid.NewGuid());
        world.Ids[heartId] = world.Heart;
        world.Stock[(world.Heart, Wood)] = 50;
        var chest = world.Chest(1, (Wood, 20));
        provider.All.AddRange(new[] { heartId, chest });
        registry.Register(provider, "test");
        service.BeginScope(1);
        var rows = Check(service, world.Player, MaterialOperation.Upgrade);
        Assert.Equal(heartId, provider.Seen.Last().Station);
        Assert.Equal(20, rows.Single(r => r.Item == Wood).Amount);
        Assert.Equal(0, PayLine(service, world.Player, MaterialOperation.Upgrade, Wood, 15));
        service.EndScope();
        Assert.Equal(50, world.Get(world.Heart, Wood));
        Assert.Equal(5, world.Get(chest, Wood));
        Assert.Equal(MaterialOperation.Upgrade, paid.Single().Context.Operation);
        Assert.Equal(heartId, paid.Single().Context.Station);
    }

    [Fact]
    public void Repair_and_upgrade_views_list_only_governed_items()
    {
        var (world, provider, registry, service, _, _) = Setup();
        provider.Operations = new[] { MaterialOperation.Repair, MaterialOperation.Upgrade };
        provider.All.Add(world.Chest(1, (Iron, 9), (Wood, 4)));
        provider.LeaveToGame.Add(Wood);
        registry.Register(provider, "test");
        foreach (var op in RepairAndUpgrade)
        {
            var totals = new Dictionary<int, int>();
            Assert.True(service.TryView(world.Player, op, totals, out var ctx, out _));
            Assert.Equal(op, ctx.Operation);
            Assert.Equal(9, totals[Iron]);
            Assert.False(totals.ContainsKey(Wood));
        }
        Assert.False(service.TryView(world.Player, MaterialOperation.Craft, new Dictionary<int, int>(), out _, out _));
    }
}
