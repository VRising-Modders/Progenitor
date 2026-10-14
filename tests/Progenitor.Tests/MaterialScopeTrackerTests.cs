using System;
using System.Collections.Generic;
using Progenitor.Materials;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The governed systems' Before and After hooks as the runtime drives them (both registered to run
/// even when the system is skipped): pairing, the opening system, and a system that throws.
/// </summary>
public class MaterialScopeTrackerTests
{
    private const string Craft = "StartCraftingSystem", Build = "PlaceTileModelSystem";
    private const string Repair = "RepairItemSystem", Heart = "CastleHeartEventSystem";

    private static (MaterialScopeTracker tracker, List<string> closed) Make()
    {
        var closed = new List<string>();
        var t = new MaterialScopeTracker();
        t.Closed = () => closed.Add("closed");
        return (t, closed);
    }

    [Fact]
    public void Before_and_after_pair_and_seam_calls_inside_see_the_operation()
    {
        var (t, closed) = Make();
        Assert.False(t.TryCurrent(10, out _, out _));
        t.Before(MaterialOperation.Craft, Craft, 10);
        Assert.True(t.TryCurrent(10, out var op, out long token));
        Assert.Equal(MaterialOperation.Craft, op);
        Assert.NotEqual(0, token);
        Assert.Equal(Craft, t.OpenSystem);
        t.After(Craft);
        Assert.False(t.TryCurrent(10, out _, out _));
        Assert.Single(closed);
    }

    [Fact]
    public void A_skipped_system_still_pairs_because_both_hooks_run()
    {
        // Another plugin skips the system: with OnlyWhenSystemRuns = false the After still runs.
        var (t, closed) = Make();
        t.Before(MaterialOperation.Build, Build, 10);
        t.After(Build);
        Assert.Null(t.OpenSystem);
        Assert.False(t.TryCurrent(10, out _, out _));
        Assert.Single(closed);
    }

    [Fact]
    public void Another_systems_after_never_closes_the_scope()
    {
        var (t, closed) = Make();
        t.Before(MaterialOperation.Craft, Craft, 10);
        t.After(Build);
        Assert.True(t.TryCurrent(10, out var op, out _));
        Assert.Equal(MaterialOperation.Craft, op);
        Assert.Empty(closed);
    }

    [Fact]
    public void A_throwing_system_is_closed_by_the_next_governed_before_and_never_nests()
    {
        var (t, closed) = Make();
        t.Before(MaterialOperation.Craft, Craft, 10); // throws: no After
        t.Before(MaterialOperation.Build, Build, 10);
        Assert.Single(closed);
        Assert.True(t.TryCurrent(10, out var op, out _));
        Assert.Equal(MaterialOperation.Build, op);
        t.After(Build);
        Assert.False(t.TryCurrent(10, out _, out _)); // no stranded Craft scope underneath
    }

    [Fact]
    public void A_throwing_system_governs_nothing_in_a_later_frame_and_is_closed_by_the_tick()
    {
        var (t, closed) = Make();
        t.Before(MaterialOperation.Craft, Craft, 10); // throws: no After
        Assert.False(t.TryCurrent(11, out _, out _)); // a seam call never closes (or publishes) anything
        Assert.Empty(closed);
        t.Tick(10);
        Assert.Empty(closed);
        t.Tick(11);
        Assert.Single(closed);
        Assert.Null(t.OpenSystem);
    }

    [Fact]
    public void Repair_and_upgrade_systems_pair_like_build_and_craft()
    {
        var (t, closed) = Make();
        // One frame running every governed system in turn, each one paired by its own After.
        var systems = new[]
        {
            (MaterialOperation.Repair, Repair), (MaterialOperation.Upgrade, Heart),
            (MaterialOperation.Craft, Craft), (MaterialOperation.Build, Build),
        };
        foreach (var (op, system) in systems)
        {
            t.Before(op, system, 20);
            Assert.True(t.TryCurrent(20, out var current, out long token));
            Assert.Equal(op, current);
            Assert.NotEqual(0, token);
            Assert.Equal(system, t.OpenSystem);
            t.After(system);
            Assert.False(t.TryCurrent(20, out _, out _));
        }
        Assert.Equal(4, closed.Count);
    }

    [Fact]
    public void A_throwing_repair_is_closed_by_the_heart_systems_before_and_each_scope_has_its_own_token()
    {
        var (t, closed) = Make();
        t.Before(MaterialOperation.Repair, Repair, 30); // throws: no After
        Assert.True(t.TryCurrent(30, out _, out long repairToken));
        t.After(Heart); // not the opening system: ignored
        Assert.Empty(closed);
        t.Before(MaterialOperation.Upgrade, Heart, 30);
        Assert.Single(closed);
        Assert.True(t.TryCurrent(30, out var op, out long heartToken));
        Assert.Equal(MaterialOperation.Upgrade, op);
        Assert.NotEqual(repairToken, heartToken);
        t.After(Heart);
        Assert.Equal(2, closed.Count);
    }
}

/// <summary>
/// Inside the castle heart system's scope only the calls of an upgrade request are an Upgrade: claim,
/// raid and destroy costs reach the same count and removal in the same update and stay vanilla.
/// </summary>
public class MaterialUpgradeFilterTests
{
    private const ulong Alice = 0x0000000100000010UL, Bob = 0x0000000100000020UL;
    private const ulong Home = 0x0000000A00000001UL, Enemy = 0x0000000B00000001UL;

    private static Action<List<MaterialHeartRequest>> Requests(params MaterialHeartRequest[] rows) => into => into.AddRange(rows);

    [Fact]
    public void Only_the_character_and_heart_of_an_upgrade_request_are_an_upgrade()
    {
        var f = new MaterialUpgradeFilter();
        var load = Requests(new MaterialHeartRequest(Alice, Home, true), new MaterialHeartRequest(Bob, Enemy, false));
        Assert.True(f.IsUpgrade(1, Alice, Home, load));
        Assert.False(f.IsUpgrade(1, Alice, Enemy, load)); // another heart
        Assert.False(f.IsUpgrade(1, Bob, Enemy, load));   // a claim, raid or destroy request
        Assert.False(f.IsUpgrade(1, Bob, Home, load));    // another character
    }

    [Fact]
    public void A_pair_with_an_upgrade_and_another_request_in_one_update_is_not_governed()
    {
        var f = new MaterialUpgradeFilter();
        var load = Requests(new MaterialHeartRequest(Alice, Home, true), new MaterialHeartRequest(Alice, Home, false));
        Assert.False(f.IsUpgrade(1, Alice, Home, load));
    }

    [Fact]
    public void Requests_are_read_once_per_scope_and_again_for_a_new_scope()
    {
        var f = new MaterialUpgradeFilter();
        int reads = 0;
        var rows = new List<MaterialHeartRequest> { new(Alice, Home, true) };
        Action<List<MaterialHeartRequest>> load = into => { reads++; into.AddRange(rows); };
        Assert.True(f.IsUpgrade(5, Alice, Home, load));
        Assert.True(f.IsUpgrade(5, Alice, Home, load));
        Assert.Equal(1, reads);
        rows.Clear(); // the next update has no upgrade request
        Assert.False(f.IsUpgrade(6, Alice, Home, load));
        Assert.Equal(2, reads);
    }

    [Fact]
    public void Unreadable_requests_and_missing_ids_are_never_an_upgrade()
    {
        var f = new MaterialUpgradeFilter();
        Assert.False(f.IsUpgrade(1, Alice, Home, _ => throw new InvalidOperationException("layout")));
        var load = Requests(new MaterialHeartRequest(Alice, Home, true));
        Assert.False(f.IsUpgrade(0, Alice, Home, load)); // no scope
        Assert.False(f.IsUpgrade(2, 0, Home, load));
        Assert.False(f.IsUpgrade(2, Alice, 0, load));
        Assert.True(f.IsUpgrade(3, Alice, Home, load));
        f.Clear();
        Assert.True(f.IsUpgrade(3, Alice, Home, load)); // cleared: read again
    }
}
