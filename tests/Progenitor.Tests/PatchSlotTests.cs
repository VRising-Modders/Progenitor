using System;
using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FHARM6, R40 finding 2: the pool's own contract, driven directly rather than through
/// <c>PatchRegistry</c>. Two properties are at stake and they are different properties.
///
/// <para><b>Generations</b> make a slot <em>token</em> unambiguous. A bare index is not: the pool
/// hands out the lowest free index first, so a released slot is the very next one bound, and a
/// stale registry entry holding "slot 0" would release, or take over, whatever now lives there.
/// A token carries the generation the caller bound, and every later address of that slot is a
/// no-op once the generation has moved.</para>
///
/// <para><b>Quarantine</b> is the answer to the case a generation cannot reach: a Harmony detour
/// that survived a failed unpatch. That detour calls <c>Prefix_I00</c>, which is compiled once
/// and carries no token, so nothing on the dispatch path can tell it from a live call. The only
/// sound answer is to never rebind the index, and to charge the loss against the 32 so it is
/// visible rather than absorbed.</para>
/// </summary>
[Collection("PluginLogger")]
public sealed class PatchSlotTests : IDisposable
{
    public PatchSlotTests()
    {
        PatchSlots.ReleaseAll();
        PatchSlots.ClearQuarantineForTests();
    }

    public void Dispose()
    {
        PatchSlots.ReleaseAll();
        PatchSlots.ClearQuarantineForTests();
    }

    private static PatchSlots.Body Body(Func<object?> value) => (_, _, _) => value();

    [Fact]
    public void A_token_carries_the_index_and_the_slots_generation()
    {
        long first = PatchSlots.Bind(PatchSlots.KindPrefix, Body(() => true), "owner.a");

        int index = PatchSlots.IndexOf(first);
        Assert.InRange(index, 0, PatchSlots.Capacity - 1);
        Assert.Equal(PatchSlots.CurrentGeneration(index), PatchSlots.GenerationOf(first));
        Assert.Equal("owner.a", PatchSlots.OwnerOf(index));

        Assert.True(PatchSlots.Release(first, "owner.a"));

        // The release bumped the generation, so the same index comes back on a new token.
        long second = PatchSlots.Bind(PatchSlots.KindPrefix, Body(() => true), "owner.b");
        Assert.Equal(index, PatchSlots.IndexOf(second));
        Assert.NotEqual(PatchSlots.GenerationOf(first), PatchSlots.GenerationOf(second));
        Assert.True(PatchSlots.GenerationOf(second) > PatchSlots.GenerationOf(first));
    }

    /// <summary>
    /// The finding's narrow arm: a stale token must not be able to reach the slot it used to
    /// own. Owner A releases; the pool hands the same index to B; A's token (or a duplicate
    /// Dispose of A's handle) must do nothing at all to B.
    /// </summary>
    [Fact]
    public void A_stale_token_cannot_release_or_quarantine_the_slot_it_used_to_hold()
    {
        long a = PatchSlots.Bind(PatchSlots.KindPrefix, Body(() => true), "owner.a");
        Assert.True(PatchSlots.Release(a, "owner.a"));

        long b = PatchSlots.Bind(PatchSlots.KindPrefix, Body(() => false), "owner.b");
        Assert.Equal(PatchSlots.IndexOf(a), PatchSlots.IndexOf(b));

        // A's token addresses a generation that is gone. Neither call may touch B's slot.
        Assert.False(PatchSlots.Release(a, "owner.a"));
        Assert.False(PatchSlots.Release(a, null));
        Assert.False(PatchSlots.Quarantine(a, "owner.a"));

        Assert.Equal("owner.b", PatchSlots.OwnerOf(PatchSlots.IndexOf(b)));
        Assert.Equal(1, PatchSlots.InUse);
        Assert.Equal(0, PatchSlots.QuarantineCount);

        // B's own token still works.
        Assert.True(PatchSlots.Release(b, "owner.b"));
        Assert.Equal(0, PatchSlots.InUse);
    }

    /// <summary>
    /// The high arm: a slot whose unpatch threw is never handed out again, for the life of the
    /// process. Without this, the surviving detour on A's target starts dispatching into B's
    /// body with A's <c>__args</c>.
    /// </summary>
    [Fact]
    public void A_quarantined_slot_is_never_rebound_and_is_counted_against_the_pool()
    {
        long a = PatchSlots.Bind(PatchSlots.KindPrefix, Body(() => true), "owner.a");
        int stranded = PatchSlots.IndexOf(a);

        Assert.True(PatchSlots.Quarantine(a, "owner.a"));

        Assert.True(PatchSlots.IsQuarantined(stranded));
        Assert.Equal(0, PatchSlots.InUse);
        Assert.Equal(1, PatchSlots.QuarantineCount);
        // Charged against the 32: the loss shrinks the pool rather than disappearing.
        Assert.Equal(PatchSlots.Capacity - 1, PatchSlots.Free);
        Assert.Contains("quarantined=1", PatchSlots.Describe(), StringComparison.Ordinal);

        // Bind prefers the lowest free index, so without the quarantine this would be `stranded`.
        long b = PatchSlots.Bind(PatchSlots.KindPrefix, Body(() => false), "owner.b");
        Assert.NotEqual(stranded, PatchSlots.IndexOf(b));

        // And no sweep of any kind brings it back.
        PatchSlots.ReleaseOwner("owner.a");
        PatchSlots.ReleaseAll();
        Assert.True(PatchSlots.IsQuarantined(stranded));
        Assert.Equal(1, PatchSlots.QuarantineCount);
        Assert.NotEqual(stranded, PatchSlots.IndexOf(
            PatchSlots.Bind(PatchSlots.KindPrefix, Body(() => true), "owner.c")));
    }

    /// <summary>Quarantine is idempotent and owner-scoped, like <c>Release</c>.</summary>
    [Fact]
    public void Quarantine_refuses_the_wrong_owner_and_does_not_double_count()
    {
        long a = PatchSlots.Bind(PatchSlots.KindPrefix, Body(() => true), "owner.a");

        Assert.False(PatchSlots.Quarantine(a, "owner.b"));
        Assert.Equal(0, PatchSlots.QuarantineCount);
        Assert.Equal(1, PatchSlots.InUse);

        Assert.True(PatchSlots.Quarantine(a, "owner.a"));
        Assert.False(PatchSlots.Quarantine(a, "owner.a"));
        Assert.Equal(1, PatchSlots.QuarantineCount);
        Assert.Equal(0, PatchSlots.InUse);
    }

    /// <summary>
    /// A released slot is a no-op that runs the original, and the sweeps bump the generation
    /// exactly as a single release does, so a token held across <c>ReleaseOwner</c> or
    /// <c>ReleaseAll</c> is just as dead as one held across <c>Release</c>.
    /// </summary>
    [Fact]
    public void The_owner_and_all_sweeps_bump_the_generation_too()
    {
        long a = PatchSlots.Bind(PatchSlots.KindPrefix, Body(() => true), "owner.a");
        long b = PatchSlots.Bind(PatchSlots.KindPostfix, Body(() => null), "owner.b");

        Assert.Equal(1, PatchSlots.ReleaseOwner("owner.a"));
        Assert.False(PatchSlots.Release(a, "owner.a"));

        Assert.Equal(1, PatchSlots.ReleaseAll());
        Assert.False(PatchSlots.Release(b, "owner.b"));
        Assert.Equal(0, PatchSlots.InUse);
    }

    /// <summary>An exhausted pool refuses, and a quarantine brings exhaustion one slot closer.</summary>
    [Fact]
    public void Quarantined_slots_bring_exhaustion_closer()
    {
        long stranded = PatchSlots.Bind(PatchSlots.KindPrefix, Body(() => true), "owner.a");
        Assert.True(PatchSlots.Quarantine(stranded, "owner.a"));

        // Job FHARM7 / R41 finding 2: the last CoreReserve slots are held for Core, so reaching
        // real exhaustion takes a fill from both budgets. Quarantine attrition is charged to the
        // plugin budget first, which is why the plugin fill is one short of NonCoreCapacity.
        for (int i = 0; i < PatchSlots.NonCoreCapacity - 1; i++)
        {
            Assert.True(PatchSlots.Bind(PatchSlots.KindPrefix, Body(() => true), "owner.fill") >= 0);
        }
        Assert.True(PatchSlots.Bind(PatchSlots.KindPrefix, Body(() => true), "owner.fill") < 0);
        for (int i = 0; i < PatchSlots.CoreReserve; i++)
        {
            Assert.True(PatchSlots.Bind(PatchSlots.KindPrefix, Body(() => true), "progenitor.core") >= 0);
        }

        Assert.Equal(0, PatchSlots.Free);
        Assert.True(PatchSlots.Bind(PatchSlots.KindPrefix, Body(() => true), "owner.last") < 0);
        Assert.True(PatchSlots.Bind(PatchSlots.KindPrefix, Body(() => true), "progenitor.core") < 0);
    }
}
