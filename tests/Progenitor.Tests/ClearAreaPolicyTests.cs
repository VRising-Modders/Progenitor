using System;
using System.Collections.Generic;
using Progenitor.Units;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of <c>ctx.Units.ClearArea</c>'s decisions: which surveyed entity is removed
/// in which scope, how the <c>EntityOwner</c> chain is followed, and when the pass loop stops. The
/// live survey (UnityClearArea.cs) only reads these facts off entities.
/// </summary>
public class ClearAreaPolicyTests
{
    private static ClearCandidate Npc() => new() { InArea = true, IsUnit = true };

    private static ClearCandidate OurSummon() => new() { InArea = true, IsUnit = true, IsMinion = true, OwnedByOurs = true };

    private static ClearCandidate Drop() => new() { InArea = true, IsDrop = true };

    private static ClearAreaOptions Owned() => new();

    private static ClearAreaOptions All() => new() { Scope = ClearAreaScope.All };

    [Fact]
    public void Defaults_are_owned_scope_everything_included_four_passes_no_unattributed_drops()
    {
        var o = new ClearAreaOptions();
        Assert.Equal(ClearAreaScope.Owned, o.Scope);
        Assert.True(o.IncludeUnits);
        Assert.True(o.IncludeSummons);
        Assert.True(o.IncludeDrops);
        Assert.False(o.IncludeUnattributedDrops);
        Assert.Equal(4, o.MaxPasses);
        Assert.Equal(ClearAreaScope.Owned, default(ClearAreaScope));
    }

    [Theory]
    [InlineData(ClearAreaScope.Owned)]
    [InlineData(ClearAreaScope.All)]
    public void Player_character_user_and_player_owned_entities_are_never_targets(ClearAreaScope scope)
    {
        var options = new ClearAreaOptions { Scope = scope, IncludeUnattributedDrops = true };

        var player = Npc(); player.IsPlayerCharacter = true;
        var user = Npc(); user.IsUser = true;
        var servantOfPlayer = Npc(); servantOfPlayer.PlayerOwned = true;
        var playerSummonOfOurs = OurSummon(); playerSummonOfOurs.PlayerOwned = true;
        var playerDrop = Drop(); playerDrop.PlayerOwned = true;
        var ourUnitPlayerOwned = Npc(); ourUnitPlayerOwned.IsOurUnit = true; ourUnitPlayerOwned.PlayerOwned = true;

        foreach (var c in new[] { player, user, servantOfPlayer, playerSummonOfOurs, playerDrop, ourUnitPlayerOwned })
            Assert.Equal(ClearTargetKind.None, ClearAreaPolicy.Classify(c, options));
    }

    [Theory]
    [InlineData(ClearAreaScope.Owned)]
    [InlineData(ClearAreaScope.All)]
    public void Structures_servants_and_unknown_owner_chains_are_never_targets(ClearAreaScope scope)
    {
        var options = new ClearAreaOptions { Scope = scope, IncludeUnattributedDrops = true };

        var structure = Npc(); structure.IsCastleStructure = true;
        var servant = Npc(); servant.IsServant = true;
        var looped = Npc(); looped.OwnerUnknown = true;
        var ourStructure = Npc(); ourStructure.IsOurUnit = true; ourStructure.IsCastleStructure = true;

        foreach (var c in new[] { structure, servant, looped, ourStructure })
            Assert.Equal(ClearTargetKind.None, ClearAreaPolicy.Classify(c, options));
    }

    [Fact]
    public void Owned_scope_takes_our_units_and_their_summons_but_not_world_npcs()
    {
        var ours = Npc(); ours.IsOurUnit = true;
        var worldNpc = Npc();
        var worldSummon = Npc(); worldSummon.IsMinion = true;

        Assert.Equal(ClearTargetKind.Unit, ClearAreaPolicy.Classify(ours, Owned()));
        Assert.Equal(ClearTargetKind.Summon, ClearAreaPolicy.Classify(OurSummon(), Owned()));
        Assert.Equal(ClearTargetKind.None, ClearAreaPolicy.Classify(worldNpc, Owned()));
        Assert.Equal(ClearTargetKind.None, ClearAreaPolicy.Classify(worldSummon, Owned()));
    }

    [Fact]
    public void Owned_by_ours_without_a_minion_marker_is_not_a_summon()
    {
        // A projectile-like unit that merely names our boss as EntityOwner is not a summon.
        var owned = Npc(); owned.OwnedByOurs = true;
        Assert.Equal(ClearTargetKind.None, ClearAreaPolicy.Classify(owned, Owned()));
    }

    [Fact]
    public void All_scope_takes_every_non_player_npc_and_summon()
    {
        var worldNpc = Npc();
        var worldSummon = Npc(); worldSummon.IsMinion = true;
        var ours = Npc(); ours.IsOurUnit = true;

        Assert.Equal(ClearTargetKind.Unit, ClearAreaPolicy.Classify(worldNpc, All()));
        Assert.Equal(ClearTargetKind.Summon, ClearAreaPolicy.Classify(worldSummon, All()));
        Assert.Equal(ClearTargetKind.Unit, ClearAreaPolicy.Classify(ours, All()));
        Assert.Equal(ClearTargetKind.Summon, ClearAreaPolicy.Classify(OurSummon(), All()));
    }

    [Fact]
    public void Mounts_are_left_unless_this_plugin_spawned_them()
    {
        var horse = Npc(); horse.IsMountable = true;
        var ourHorse = Npc(); ourHorse.IsMountable = true; ourHorse.IsOurUnit = true;

        Assert.Equal(ClearTargetKind.None, ClearAreaPolicy.Classify(horse, All()));
        Assert.Equal(ClearTargetKind.Unit, ClearAreaPolicy.Classify(ourHorse, Owned()));
    }

    [Fact]
    public void Include_flags_filter_each_kind()
    {
        var ours = Npc(); ours.IsOurUnit = true;
        var noUnits = new ClearAreaOptions { IncludeUnits = false };
        var noSummons = new ClearAreaOptions { IncludeSummons = false };
        var noDrops = new ClearAreaOptions { Scope = ClearAreaScope.All, IncludeDrops = false };

        Assert.Equal(ClearTargetKind.None, ClearAreaPolicy.Classify(ours, noUnits));
        Assert.Equal(ClearTargetKind.Summon, ClearAreaPolicy.Classify(OurSummon(), noUnits));
        Assert.Equal(ClearTargetKind.None, ClearAreaPolicy.Classify(OurSummon(), noSummons));
        Assert.Equal(ClearTargetKind.Unit, ClearAreaPolicy.Classify(ours, noSummons));
        Assert.Equal(ClearTargetKind.None, ClearAreaPolicy.Classify(Drop(), noDrops));
    }

    [Fact]
    public void Owned_scope_removes_unattributed_drops_only_when_opted_in()
    {
        var attributed = Drop(); attributed.OwnedByOurs = true;

        Assert.Equal(ClearTargetKind.None, ClearAreaPolicy.Classify(Drop(), Owned()));
        Assert.Equal(ClearTargetKind.Drop, ClearAreaPolicy.Classify(attributed, Owned()));
        Assert.Equal(ClearTargetKind.Drop, ClearAreaPolicy.Classify(Drop(), new ClearAreaOptions { IncludeUnattributedDrops = true }));
        Assert.Equal(ClearTargetKind.Drop, ClearAreaPolicy.Classify(Drop(), All()));
    }

    [Fact]
    public void Outside_area_already_scheduled_and_dying_entities_are_skipped()
    {
        var outside = Npc(); outside.InArea = false;
        var scheduled = Npc(); scheduled.AlreadyScheduled = true;
        var dying = Npc(); dying.Dying = true;
        foreach (var c in new[] { outside, scheduled, dying })
            Assert.Equal(ClearTargetKind.None, ClearAreaPolicy.Classify(c, All()));
    }

    [Fact]
    public void Entity_that_is_neither_unit_nor_drop_is_never_a_target()
    {
        var other = new ClearCandidate { InArea = true, IsMinion = true, OwnedByOurs = true };
        Assert.Equal(ClearTargetKind.None, ClearAreaPolicy.Classify(other, All()));
    }

    [Fact]
    public void InArea_is_horizontal_distance_at_any_height()
    {
        Assert.True(ClearAreaPolicy.InArea(0f, 0f, 10f, 6f, 8f));
        Assert.True(ClearAreaPolicy.InArea(100f, 100f, 5f, 100f, 105f));
        Assert.False(ClearAreaPolicy.InArea(0f, 0f, 10f, 6f, 8.1f));
        Assert.False(ClearAreaPolicy.InArea(0f, 0f, 10f, float.NaN, 0f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(200.5f)]
    public void Validate_refuses_bad_radius(float radius)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClearAreaPolicy.Validate(radius, new ClearAreaOptions()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void Validate_refuses_bad_pass_count(int passes)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ClearAreaPolicy.Validate(10f, new ClearAreaOptions { MaxPasses = passes }));
    }

    [Fact]
    public void Validate_returns_pass_count()
    {
        Assert.Equal(4, ClearAreaPolicy.Validate(200f, new ClearAreaOptions()));
        Assert.Equal(1, ClearAreaPolicy.Validate(1f, new ClearAreaOptions { MaxPasses = 1 }));
        Assert.Throws<ArgumentNullException>(() => ClearAreaPolicy.Validate(1f, null!));
    }

    // Owner chain: ids are (index, 1). 1 = player, 10 = our boss, 20..29 = world.

    private static UnitId Id(int i) => new(i, 1);

    private static OwnerChain Walk(Dictionary<int, int> owners, int start, params int[] ours) =>
        ClearAreaPolicy.WalkOwners(
            Id(start),
            id => owners.TryGetValue(id.Index, out int o) ? Id(o) : UnitId.None,
            id => id.Index == 1,
            id => Array.IndexOf(ours, id.Index) >= 0);

    [Fact]
    public void Summon_of_our_boss_is_ours_and_not_player_owned()
    {
        var chain = Walk(new() { [30] = 10 }, 30, 10);
        Assert.True(chain.OursOwned);
        Assert.False(chain.PlayerOwned);
        Assert.False(chain.Unknown);
    }

    [Fact]
    public void Summon_of_a_summon_of_our_boss_is_ours()
    {
        var chain = Walk(new() { [31] = 30, [30] = 10 }, 31, 10);
        Assert.True(chain.OursOwned);
    }

    [Fact]
    public void Player_anywhere_in_the_chain_wins_over_ours()
    {
        // A summon owned by our unit that a player has taken over (owner chain reaches the player).
        var chain = Walk(new() { [30] = 10, [10] = 1 }, 30, 10);
        Assert.True(chain.PlayerOwned);
        Assert.True(ClearAreaPolicy.IsProtected(new ClearCandidate { PlayerOwned = chain.PlayerOwned }));
    }

    [Fact]
    public void Player_servant_is_player_owned()
    {
        var chain = Walk(new() { [25] = 1 }, 25);
        Assert.True(chain.PlayerOwned);
        Assert.False(chain.OursOwned);
    }

    [Fact]
    public void No_owner_and_self_owner_end_the_chain_cleanly()
    {
        var none = Walk(new(), 20);
        Assert.False(none.PlayerOwned || none.OursOwned || none.Unknown);

        var self = Walk(new() { [20] = 20 }, 20);
        Assert.False(self.PlayerOwned || self.OursOwned || self.Unknown);

        var selfUp = Walk(new() { [21] = 20, [20] = 20 }, 21);
        Assert.False(selfUp.Unknown);
    }

    [Fact]
    public void Loop_or_overlong_chain_is_unknown_and_protected()
    {
        var loop = Walk(new() { [20] = 21, [21] = 20 }, 20);
        Assert.True(loop.Unknown);

        var longChain = Walk(new() { [20] = 21, [21] = 22, [22] = 23, [23] = 24, [24] = 25 }, 20);
        Assert.True(longChain.Unknown);

        var four = Walk(new() { [20] = 21, [21] = 22, [22] = 23, [23] = 24 }, 20);
        Assert.False(four.Unknown);

        Assert.True(ClearAreaPolicy.IsProtected(new ClearCandidate { OwnerUnknown = true }));
    }

    [Fact]
    public void Pass_policy_stops_when_a_survey_is_empty_and_nothing_is_alive()
    {
        Assert.Equal(ClearAreaStep.Done, ClearAreaPolicy.After(1, 4, 0, 0));
        Assert.Equal(ClearAreaStep.Done, ClearAreaPolicy.After(3, 4, 0, 0));
    }

    [Fact]
    public void Pass_policy_repeats_while_new_targets_or_removals_in_flight()
    {
        Assert.Equal(ClearAreaStep.AnotherPass, ClearAreaPolicy.After(1, 4, 6, 0));
        // Four pillars summoned after the first list was taken.
        Assert.Equal(ClearAreaStep.AnotherPass, ClearAreaPolicy.After(2, 4, 4, 0));
        // Nothing new, but a boss is still being removed and may summon again.
        Assert.Equal(ClearAreaStep.AnotherPass, ClearAreaPolicy.After(2, 4, 0, 1));
    }

    [Fact]
    public void Pass_policy_is_bounded_and_verifies_after_the_last_pass()
    {
        Assert.Equal(ClearAreaStep.Verify, ClearAreaPolicy.After(4, 4, 2, 0));
        Assert.Equal(ClearAreaStep.Verify, ClearAreaPolicy.After(4, 4, 0, 3));
        Assert.Equal(ClearAreaStep.Verify, ClearAreaPolicy.After(1, 1, 1, 0));
        Assert.Equal(ClearAreaStep.Done, ClearAreaPolicy.After(4, 4, 0, 0));
    }

    [Fact]
    public void Simulated_clear_catches_late_summons_within_the_pass_budget()
    {
        // Pass 1 removes 6 bosses; they summon 4 pillars before dying; pass 2 removes those; pass 3 is empty.
        var survey = new Queue<(int found, int alive)>(new[] { (6, 0), (4, 6), (0, 0) });
        int passes = 0;
        ClearAreaStep step;
        do
        {
            var (found, alive) = survey.Dequeue();
            passes++;
            step = ClearAreaPolicy.After(passes, ClearAreaOptions.DefaultMaxPasses, found, alive);
        }
        while (step == ClearAreaStep.AnotherPass);

        Assert.Equal(ClearAreaStep.Done, step);
        Assert.Equal(3, passes);
    }

    [Fact]
    public void Result_counts_by_kind_and_formats()
    {
        var r = new ClearAreaResult { Enabled = true };
        r.Count(ClearTargetKind.Unit);
        r.Count(ClearTargetKind.Summon);
        r.Count(ClearTargetKind.Summon);
        r.Count(ClearTargetKind.Drop);
        r.Count(ClearTargetKind.None);
        Assert.Equal(1, r.UnitsRemoved);
        Assert.Equal(2, r.SummonsRemoved);
        Assert.Equal(1, r.DropsRemoved);
        Assert.Equal(4, r.Total);
        Assert.Contains("units=1 summons=2 drops=1", r.ToString());
        Assert.Contains("running", r.ToString());
    }
}
