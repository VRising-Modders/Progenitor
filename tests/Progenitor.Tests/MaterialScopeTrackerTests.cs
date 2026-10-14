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
}
