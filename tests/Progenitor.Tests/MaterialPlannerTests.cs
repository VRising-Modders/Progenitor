using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Materials;
using Xunit;

namespace Progenitor.Tests;

public class MaterialPlannerTests
{
    /// <summary>In-memory ledger: (source, item) → count. Hooks let a test break a take or a give-back.</summary>
    internal sealed class FakeLedger : IMaterialLedger<string>
    {
        public readonly Dictionary<(string, int), int> Stock = new();
        public Func<string, int, int, int>? RemoveOverride;
        public Func<string, int, int, int>? AddOverride;
        public int Removes, Adds;

        public FakeLedger Put(string source, int item, int amount)
        {
            Stock[(source, item)] = Get(source, item) + amount;
            return this;
        }

        public int Get(string source, int item) => Stock.TryGetValue((source, item), out int n) ? n : 0;

        public int Count(string source, int item) => Get(source, item);

        public int Remove(string source, int item, int amount)
        {
            Removes++;
            if (RemoveOverride != null) amount = RemoveOverride(source, item, amount);
            int take = Math.Min(amount, Get(source, item));
            Stock[(source, item)] = Get(source, item) - take;
            return take;
        }

        public int Add(string source, int item, int amount)
        {
            Adds++;
            if (AddOverride != null) amount = AddOverride(source, item, amount);
            Stock[(source, item)] = Get(source, item) + amount;
            return amount;
        }

        public long Total(int item) => Stock.Where(kv => kv.Key.Item2 == item).Sum(kv => (long)kv.Value);
    }

    private const int Cloth = 100, Iron = 200, Wood = 300;

    private static MaterialPoolSpec<string> Spec(int reserve, params string[] order) => new(order, reserve);

    [Fact]
    public void Normalize_merges_repeated_items_and_drops_empty_lines()
    {
        var lines = MaterialPlanner.Normalize(new[]
        {
            new MaterialLine(Cloth, 4), new MaterialLine(Iron, 0), new MaterialLine(Cloth, 6),
            new MaterialLine(Wood, -3), new MaterialLine(0, 5), new MaterialLine(Iron, 2),
        });
        Assert.Equal(new[] { new MaterialLine(Cloth, 10), new MaterialLine(Iron, 2) }, lines);
    }

    [Fact]
    public void Normalize_refuses_an_overflowing_cost()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MaterialPlanner.Normalize(new[] { new MaterialLine(Cloth, int.MaxValue), new MaterialLine(Cloth, 1) }));
    }

    [Fact]
    public void Pool_counts_each_physical_inventory_once_and_leaves_exclusions_out()
    {
        var ledger = new FakeLedger().Put("a", Cloth, 5).Put("b", Cloth, 7).Put("bag", Cloth, 100);
        var pool = MaterialPlanner.Pool(Cloth, Spec(0, "a", "b", "a", "bag", "empty"), ledger, new HashSet<string> { "bag" });
        Assert.Equal(12, pool.Total);
        Assert.Equal(new[] { ("a", 5), ("b", 7) }, pool.Rows);
    }

    [Fact]
    public void Reserve_is_subtracted_once_from_the_pool_not_once_per_chest()
    {
        var ledger = new FakeLedger().Put("a", Cloth, 10).Put("b", Cloth, 10).Put("c", Cloth, 10);
        var pool = MaterialPlanner.Pool(Cloth, Spec(8, "a", "b", "c"), ledger);
        Assert.Equal(30, pool.Total);
        Assert.Equal(22, pool.Available);
    }

    [Fact]
    public void Reserve_boundary_one_more_unit_is_short()
    {
        var ledger = new FakeLedger().Put("a", Cloth, 10).Put("b", Cloth, 10);
        var ok = MaterialPlanner.Plan(new[] { new MaterialLine(Cloth, 15) }, _ => Spec(5, "a", "b"), ledger);
        Assert.True(ok.Ok);
        var tooMuch = MaterialPlanner.Plan(new[] { new MaterialLine(Cloth, 16) }, _ => Spec(5, "a", "b"), ledger);
        Assert.False(tooMuch.Ok);
        Assert.Empty(tooMuch.Steps);
        Assert.Equal((Cloth, 16, 15), tooMuch.Short.Single());
    }

    [Fact]
    public void Each_item_follows_its_own_spend_order()
    {
        var ledger = new FakeLedger()
            .Put("chestA", Iron, 10).Put("chestB", Iron, 10)
            .Put("chestA", Wood, 10).Put("chestB", Wood, 10);
        var plan = MaterialPlanner.Plan(
            new[] { new MaterialLine(Iron, 12), new MaterialLine(Wood, 3) },
            item => item == Iron ? Spec(0, "chestA", "chestB") : Spec(0, "chestB", "chestA"),
            ledger);
        Assert.True(plan.Ok);
        Assert.Equal(new[] { "chestA:200x10", "chestB:200x2", "chestB:300x3" }, plan.Steps.Select(s => s.ToString()));
    }

    [Fact]
    public void Repeated_lines_are_paid_as_one_and_the_reserve_still_counts_once()
    {
        var ledger = new FakeLedger().Put("a", Cloth, 10);
        var plan = MaterialPlanner.Plan(new[] { new MaterialLine(Cloth, 4), new MaterialLine(Cloth, 4) }, _ => Spec(3, "a"), ledger);
        Assert.False(plan.Ok); // 8 needed, 7 spendable
        Assert.Equal((Cloth, 8, 7), plan.Short.Single());
    }

    [Fact]
    public void One_short_item_plans_nothing_for_any_item()
    {
        var ledger = new FakeLedger().Put("a", Iron, 50).Put("a", Wood, 1);
        var plan = MaterialPlanner.Plan(new[] { new MaterialLine(Iron, 5), new MaterialLine(Wood, 2) }, _ => Spec(0, "a"), ledger);
        Assert.False(plan.Ok);
        Assert.Empty(plan.Steps);
        var commit = MaterialPlanner.Commit(plan, ledger);
        Assert.Equal(MaterialCommitOutcome.Refused, commit.Outcome);
        Assert.Equal(0, ledger.Removes);
        Assert.Equal(50, ledger.Get("a", Iron));
    }

    [Fact]
    public void Ungoverned_lines_are_left_to_the_game()
    {
        var ledger = new FakeLedger().Put("a", Iron, 5);
        var plan = MaterialPlanner.Plan(new[] { new MaterialLine(Iron, 5), new MaterialLine(Wood, 2) },
            item => item == Iron ? Spec(0, "a") : null, ledger);
        Assert.True(plan.Ok);
        Assert.Equal(new MaterialLine(Wood, 2), plan.Ungoverned.Single());
        Assert.All(plan.Steps, s => Assert.Equal(Iron, s.Item));
    }

    [Fact]
    public void Commit_takes_exactly_the_plan()
    {
        var ledger = new FakeLedger().Put("a", Cloth, 3).Put("b", Cloth, 9);
        var plan = MaterialPlanner.Plan(new[] { new MaterialLine(Cloth, 7) }, _ => Spec(0, "a", "b"), ledger);
        var commit = MaterialPlanner.Commit(plan, ledger);
        Assert.True(commit.Ok);
        Assert.Equal(0, ledger.Get("a", Cloth));
        Assert.Equal(5, ledger.Get("b", Cloth));
        Assert.Equal(5, ledger.Total(Cloth));
    }

    [Fact]
    public void A_short_take_rolls_every_earlier_take_back_exactly()
    {
        var ledger = new FakeLedger().Put("a", Iron, 4).Put("b", Iron, 4).Put("a", Wood, 4);
        var plan = MaterialPlanner.Plan(new[] { new MaterialLine(Iron, 6), new MaterialLine(Wood, 4) },
            _ => Spec(0, "a", "b"), ledger);
        Assert.True(plan.Ok);
        // The wood take comes back one short (a full slot, a game refusal, anything).
        ledger.RemoveOverride = (s, item, amount) => item == Wood ? amount - 1 : amount;
        var before = new Dictionary<(string, int), int>(ledger.Stock);
        var commit = MaterialPlanner.Commit(plan, ledger);
        Assert.Equal(MaterialCommitOutcome.RolledBack, commit.Outcome);
        Assert.Empty(commit.Taken);
        Assert.Equal(before, ledger.Stock);
    }

    [Fact]
    public void A_failed_give_back_is_reported_as_inconsistent()
    {
        var ledger = new FakeLedger().Put("a", Iron, 4).Put("b", Iron, 4);
        var plan = MaterialPlanner.Plan(new[] { new MaterialLine(Iron, 6) }, _ => Spec(0, "a", "b"), ledger);
        ledger.RemoveOverride = (s, item, amount) => s == "b" ? 0 : amount;
        ledger.AddOverride = (s, item, amount) => amount - 1;
        var commit = MaterialPlanner.Commit(plan, ledger);
        Assert.Equal(MaterialCommitOutcome.Inconsistent, commit.Outcome);
        Assert.Equal(1, commit.Unrestored);
    }

    [Fact]
    public void A_take_that_throws_rolls_back()
    {
        var ledger = new FakeLedger().Put("a", Iron, 4).Put("b", Iron, 4);
        var plan = MaterialPlanner.Plan(new[] { new MaterialLine(Iron, 6) }, _ => Spec(0, "a", "b"), ledger);
        ledger.RemoveOverride = (s, item, amount) => s == "b" ? throw new InvalidOperationException("gone") : amount;
        var commit = MaterialPlanner.Commit(plan, ledger);
        Assert.Equal(MaterialCommitOutcome.RolledBack, commit.Outcome);
        Assert.Contains("gone", commit.Reason);
        Assert.Equal(4, ledger.Get("a", Iron));
    }

    [Fact]
    public void Stock_spent_by_someone_else_between_plan_and_commit_rolls_back_without_loss()
    {
        var ledger = new FakeLedger().Put("a", Cloth, 5).Put("b", Cloth, 5);
        var plan = MaterialPlanner.Plan(new[] { new MaterialLine(Cloth, 8) }, _ => Spec(0, "a", "b"), ledger);
        // A competing consumer (another player, a station feeder) empties b first.
        ledger.Remove("b", Cloth, 5);
        var commit = MaterialPlanner.Commit(plan, ledger);
        Assert.Equal(MaterialCommitOutcome.RolledBack, commit.Outcome);
        Assert.Equal(5, ledger.Get("a", Cloth));
        Assert.Equal(0, ledger.Get("b", Cloth));
    }

    [Fact]
    public void Two_requests_for_the_last_full_set_only_one_succeeds()
    {
        var ledger = new FakeLedger().Put("a", Iron, 10).Put("a", Wood, 10);
        var lines = new[] { new MaterialLine(Iron, 10), new MaterialLine(Wood, 10) };
        // Each request plans and commits in one synchronous step, as the seams do.
        var first = MaterialPlanner.Commit(MaterialPlanner.Plan(lines, _ => Spec(0, "a"), ledger), ledger);
        var second = MaterialPlanner.Commit(MaterialPlanner.Plan(lines, _ => Spec(0, "a"), ledger), ledger);
        Assert.True(first.Ok);
        Assert.Equal(MaterialCommitOutcome.Refused, second.Outcome);
        Assert.Equal(0, ledger.Total(Iron));
    }
}
