using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Progenitor.Castles;
using Progenitor.Inventory;
using Progenitor.Sample;
using Progenitor.Servants;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// ctx.Servants, game-free: the throne's state rule and proficiency, hunt timing and the return
/// diff, the send validation (the game's checks in the game's order), the mission row and throne
/// list byte layouts, the one-provider list registry, and the sample's parsing. Every id, GUID and
/// name here is a placeholder.
/// </summary>
public class ServantLogicTests
{
    private static readonly CastleId Heart = new(500, 1);
    private static readonly CastleId OtherHeart = new(600, 1);
    private static readonly CastleId A = new(11, 1), B = new(12, 1), C = new(13, 1), D = new(14, 1);
    private const int Mission = 777001, OtherMission = 777002;
    private const long Now = 638_000_000_000_000_000L;

    private static ServantFacts Ready(CastleId id, string name = "") => new()
    {
        Servant = id,
        Exists = true,
        IsServant = true,
        OnMission = false,
        Dead = false,
        ItemStacks = 0,
        Coffin = new CastleId(id.Index + 1000, 1),
        CoffinHeart = Heart,
        InjuryEndTicks = 0,
        Name = name,
    };

    private static SendFacts Facts(params ServantFacts[] servants) => new()
    {
        Heart = Heart,
        HeartOk = true,
        Servants = servants,
        MissionGuid = Mission,
        ZoneKnown = true,
        Zone = new MissionZone(3, -2, 4),
        MissionDataKnown = true,
        ServantSlots = 3,
        DataSlot = 1,
        SettingsReadable = true,
        SettingCount = 4,
        SettingLength = 7200f,
        CheckDiscovered = true,
        ProgressionReadable = true,
        Discovered = true,
        NowTicks = Now,
    };

    // ---- throne list rules ------------------------------------------------------------------------

    [Theory]
    [InlineData(8, 0L, false, true, ServantState.Dead)]
    [InlineData(9, 0L, true, true, ServantState.Dead)]
    [InlineData(7, Now + 1, true, true, ServantState.Injured)]
    [InlineData(7, Now, true, true, ServantState.AwayOnHunt)]
    [InlineData(7, 0L, false, false, ServantState.HasItems)]
    [InlineData(7, 0L, false, true, ServantState.Free)]
    public void State_follows_the_games_order(int coffinState, long injuryEnd, bool onHunt, bool empty, ServantState expected) =>
        Assert.Equal(expected, ServantLogic.StateOf(coffinState, injuryEnd, Now, onHunt, empty));

    [Fact]
    public void State_values_are_the_games_enum()
    {
        Assert.Equal(Layout.ServantInfoStateDead, (int)ServantState.Dead);
        Assert.Equal(Layout.ServantInfoStateInjured, (int)ServantState.Injured);
        Assert.Equal(Layout.ServantInfoStateAwayOnHunt, (int)ServantState.AwayOnHunt);
        Assert.Equal(Layout.ServantInfoStateHasItemInInventory, (int)ServantState.HasItems);
        Assert.Equal(Layout.ServantInfoStateFree, (int)ServantState.Free);
    }

    [Theory]
    [InlineData(7, true)]
    [InlineData(8, true)]
    [InlineData(9, true)]
    [InlineData(6, false)]
    [InlineData(0, false)]
    public void Throne_lists_living_revivable_and_reviving_coffins(int state, bool listed) =>
        Assert.Equal(listed, ServantLogic.ThroneLists(state));

    [Fact]
    public void Hunt_proficiency_rounds_each_term_then_adds_100()
    {
        Assert.Equal(100f, ServantLogic.HuntProficiency(0, 0));
        // 8.04 * 10 = 80.4 -> 80; 0.375 * 100 = 37.5 -> 38 (ties to even)
        Assert.Equal(218f, ServantLogic.HuntProficiency(8.04f, 0.375f));
        // 2.25 * 10 = 22.5 -> 22 (ties to even); 0.125 * 100 = 12.5 -> 12
        Assert.Equal(134f, ServantLogic.HuntProficiency(2.25f, 0.125f));
        Assert.Equal(0.3f, ServantLogic.LootFactor(0.6f), 5);
    }

    [Fact]
    public void Provider_list_is_deduplicated_and_capped_in_order()
    {
        var asked = new List<CastleId> { A, B, A, CastleId.None, C, D };
        Assert.Equal(new[] { A, B, C }, ServantLogic.CapList(asked, 3));
        Assert.Empty(ServantLogic.CapList(null, 3));
    }

    [Fact]
    public void Routed_send_needs_every_heart_still_listed_by_the_provider()
    {
        Assert.Null(ServantLogic.RouteRefusal(new[] { OtherHeart }, new[] { Heart, OtherHeart }));
        Assert.Null(ServantLogic.RouteRefusal(new[] { Heart, OtherHeart }, new[] { OtherHeart, Heart }));
        // left the clan: the provider no longer returns the clanmate's heart
        Assert.Contains(OtherHeart.ToString(), ServantLogic.RouteRefusal(new[] { OtherHeart }, new[] { Heart })!);
        // all or nothing: one heart no longer listed refuses the whole send
        Assert.NotNull(ServantLogic.RouteRefusal(new[] { Heart, OtherHeart }, new[] { Heart }));
        Assert.NotNull(ServantLogic.RouteRefusal(new[] { OtherHeart }, null));
        Assert.NotNull(ServantLogic.RouteRefusal(new[] { OtherHeart }, Array.Empty<CastleId>()));
    }

    [Fact]
    public void Shown_list_belongs_to_one_player_and_goes_on_disconnect()
    {
        var reg = new ServantListRegistry();
        using var h = reg.Register(new Fixed(), "mod.one");
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        reg.RecordShown(4, new ServantListRegistry.Shown { AtUtc = t0, User = A });
        reg.RecordShown(5, new ServantListRegistry.Shown { AtUtc = t0, User = B });
        Assert.Equal(A, reg.ShownTo(4, t0)!.User);
        reg.ForgetShown(4);
        Assert.Null(reg.ShownTo(4, t0));
        Assert.NotNull(reg.ShownTo(5, t0));
        Assert.Equal(TimeSpan.FromMinutes(2), ServantListRegistry.ShownTtl);
    }

    // ---- timing and returns -----------------------------------------------------------------------

    [Fact]
    public void Completion_is_start_plus_length_in_ticks()
    {
        Assert.Equal(Now + 7200L * ServantLogic.TicksPerSecond, ServantLogic.CompletionTicks(Now, 7200f));
        var row = new MissionRow { StartTicks = Now, LengthSeconds = 10f };
        Assert.False(ServantLogic.IsDue(row, Now, 0, false));
        Assert.True(ServantLogic.IsDue(row, Now + 10 * ServantLogic.TicksPerSecond, 0, false));
        Assert.True(ServantLogic.IsDue(row, Now + 9 * ServantLogic.TicksPerSecond, 2 * ServantLogic.TicksPerSecond, false));
        Assert.True(ServantLogic.IsDue(row, Now, 0, timersDisabled: true));
    }

    [Fact]
    public void Hunt_length_follows_the_timers_switch()
    {
        Assert.Equal(7200f, ServantLogic.HuntLength(7200f, false));
        Assert.Equal(7200f * 1e-5f, ServantLogic.HuntLength(7200f, true));
    }

    [Fact]
    public void Finished_rows_are_the_ones_gone_after_the_update()
    {
        var r1 = new MissionRow { MissionGuid = Mission, StartTicks = Now, Servant1 = A };
        var r2 = new MissionRow { MissionGuid = OtherMission, StartTicks = Now + 5, Servant1 = B };
        var r2Again = new MissionRow { MissionGuid = OtherMission, StartTicks = Now + 5, Servant1 = B };
        var gone = ServantLogic.Finished(new[] { r1, r2 }, new[] { r2Again });
        Assert.Single(gone);
        Assert.Same(r1, gone[0]);
        Assert.Empty(ServantLogic.Finished(new[] { r1 }, new[] { r1 }));
    }

    // A survivor as the return publish reads it once the game is done: alive, carrying DestroyData
    // like every live unit, off the hunt, buff gone, coffin alive.
    private static ReturnFacts Home(int coffinState = 7, long injuryEnd = 0) => new()
    {
        Exists = true,
        HasDestroyData = true,
        CoffinReadable = true,
        CoffinState = coffinState,
        InjuryEndTicks = injuryEnd,
    };

    [Theory]
    [InlineData(false, false, false, 7, 0L, ServantOutcome.Dead)]      // entity gone
    [InlineData(true, true, false, 7, 0L, ServantOutcome.Dead)]        // Dead component
    [InlineData(true, false, true, 7, 0L, ServantOutcome.Dead)]        // DestroyTag
    [InlineData(true, false, false, 8, 0L, ServantOutcome.Dead)]       // coffin revivable
    [InlineData(true, false, false, 9, 0L, ServantOutcome.Dead)]       // coffin reviving
    [InlineData(true, false, false, 7, Now + 1, ServantOutcome.Injured)]
    [InlineData(true, false, false, 7, Now, ServantOutcome.Returned)]
    public void Outcome_of_a_returned_servant(bool exists, bool dead, bool destroyTag, int coffinState, long injuryEnd, ServantOutcome expected)
    {
        var f = Home(coffinState, injuryEnd);
        f.Exists = exists;
        f.HasDead = dead;
        f.HasDestroyTag = destroyTag;
        Assert.Equal(expected, ServantLogic.OutcomeOf(f, Now));
    }

    [Fact]
    public void A_live_servant_home_with_items_is_returned_although_it_carries_DestroyData()
    {
        // The live report (2026-10-16): both servants alive at home with loot, published as Dead
        // because DestroyData (on every live unit) was read as a destroy.
        var f = Home();
        Assert.True(f.HasDestroyData);
        Assert.False(ServantLogic.Destroying(hasDead: false, hasDestroyTag: false, hasDestroyData: true));
        Assert.Equal(ServantOutcome.Returned, ServantLogic.OutcomeOf(f, Now));
        Assert.True(ServantLogic.ReturnSettled(f));
    }

    [Fact]
    public void Without_a_readable_coffin_a_live_servant_is_not_dead()
    {
        var f = new ReturnFacts { Exists = true, HasDestroyData = true };
        Assert.Equal(ServantOutcome.Returned, ServantLogic.OutcomeOf(f, Now));
    }

    [Fact]
    public void A_destroyed_entity_whose_coffin_names_another_living_servant_is_not_dead()
    {
        var gone = Home();
        gone.Exists = false;
        gone.CoffinServantAlive = true;
        Assert.Equal(ServantOutcome.Returned, ServantLogic.OutcomeOf(gone, Now));
        gone.InjuryEndTicks = Now + 10;
        Assert.Equal(ServantOutcome.Injured, ServantLogic.OutcomeOf(gone, Now));
        gone.CoffinState = 8;
        Assert.Equal(ServantOutcome.Dead, ServantLogic.OutcomeOf(gone, Now));

        var truly = Home();
        truly.Exists = false;
        Assert.Equal(ServantOutcome.Dead, ServantLogic.OutcomeOf(truly, Now));
    }

    [Fact]
    public void A_return_waits_until_the_game_has_applied_it()
    {
        var onHunt = Home();
        onHunt.OnMission = true;
        onHunt.MissionBuffOn = true;
        Assert.False(ServantLogic.ReturnSettled(onHunt));

        var buffOnly = Home();
        buffOnly.MissionBuffOn = true;
        Assert.False(ServantLogic.ReturnSettled(buffOnly));

        var flagOnly = Home();
        flagOnly.OnMission = true;
        Assert.False(ServantLogic.ReturnSettled(flagOnly));

        Assert.True(ServantLogic.ReturnSettled(Home()));
        Assert.True(ServantLogic.ReturnSettled(new ReturnFacts { Exists = false }));
        Assert.True(ServantLogic.ReturnSettled(new ReturnFacts { Exists = true, HasDestroyTag = true, OnMission = true, MissionBuffOn = true }));
        // DestroyData alone settles nothing: a survivor still on its hunt waits.
        Assert.False(ServantLogic.ReturnSettled(new ReturnFacts { Exists = true, HasDestroyData = true, OnMission = true }));
    }

    [Fact]
    public void A_returned_servant_carries_the_same_inventory_reference_as_the_throne_list()
    {
        const int epoch = 7;
        var loot = new[] { new ServantLoot(5, 272), new ServantLoot(6, 29) };
        var back = ServantLogic.Returned(A, A, new CastleId(90, 1), "Gideon", Home(), Now, epoch, loot, stacks: 2);
        var listed = ServantLogic.ServantInventory(A, epoch); // what ctx.Servants.List gives as ServantInfo.Inventory
        Assert.Equal(ServantOutcome.Returned, back.Outcome);
        Assert.Equal(A, back.Servant);
        Assert.Equal(listed, back.Inventory);
        Assert.False(back.Inventory.IsNone);
        Assert.Equal((A.Index, A.Version, 0, epoch), (back.Inventory.OwnerIndex, back.Inventory.OwnerVersion, back.Inventory.Instance, back.Inventory.Epoch));
        Assert.False(back.Inventory.IsStaleIn(epoch));
        Assert.True(back.LootIsHaul);
        Assert.Equal(loot, back.Loot);

        var injured = ServantLogic.Returned(A, A, CastleId.None, "", Home(injuryEnd: Now + 1), Now, epoch, loot, stacks: 2);
        Assert.Equal(ServantOutcome.Injured, injured.Outcome);
        Assert.Equal(listed, injured.Inventory);
    }

    [Fact]
    public void A_relinked_servant_reports_the_living_servant_and_its_inventory()
    {
        var gone = Home();
        gone.Exists = false;
        gone.CoffinServantAlive = true;
        var back = ServantLogic.Returned(A, B, new CastleId(90, 1), "", gone, Now, 3, Array.Empty<ServantLoot>(), stacks: 0);
        Assert.Equal(ServantOutcome.Returned, back.Outcome);
        Assert.Equal(B, back.Servant);
        Assert.Equal(ServantLogic.ServantInventory(B, 3), back.Inventory);
    }

    [Fact]
    public void A_dead_servant_has_no_inventory_and_an_unreadable_one_is_no_haul()
    {
        var dead = Home(coffinState: 8);
        var back = ServantLogic.Returned(A, A, CastleId.None, "", dead, Now, 3, new[] { new ServantLoot(5, 1) }, stacks: 1);
        Assert.Equal(ServantOutcome.Dead, back.Outcome);
        Assert.True(back.Inventory.IsNone);
        Assert.Empty(back.Loot);
        Assert.False(back.LootIsHaul);

        var unreadable = ServantLogic.Returned(A, A, CastleId.None, "", Home(), Now, 3, Array.Empty<ServantLoot>(), stacks: -1);
        Assert.False(unreadable.LootIsHaul);
        Assert.Equal(ServantLogic.ServantInventory(A, 3), unreadable.Inventory);

        var again = Home();
        again.OutAgain = true;
        var out2 = ServantLogic.Returned(A, A, CastleId.None, "", again, Now, 3, new[] { new ServantLoot(5, 1) }, stacks: 1);
        Assert.Empty(out2.Loot);
        Assert.False(out2.LootIsHaul);
        Assert.Equal(ServantLogic.ServantInventory(A, 3), out2.Inventory);
    }

    [Fact]
    public void A_servant_sent_out_again_settles_its_earlier_return()
    {
        var again = Home();
        again.OnMission = true;
        again.MissionBuffOn = true;
        again.OutAgain = true;
        Assert.True(ServantLogic.ReturnSettled(again));
        Assert.Equal(ServantOutcome.Returned, ServantLogic.OutcomeOf(again, Now));
    }

    [Fact]
    public void A_resend_on_the_same_mission_is_its_own_row()
    {
        // The first hunt returned and the same servants went again on the same mission: the new row
        // has its own start, so the old row's return does not match it and vice versa.
        var first = new MissionRow { MissionGuid = Mission, StartTicks = Now, LengthSeconds = 7200, Servant1 = A, Servant2 = B, Count = 2 };
        var resent = new MissionRow { MissionGuid = Mission, StartTicks = Now + 600 * ServantLogic.TicksPerSecond, LengthSeconds = 7200, Servant1 = A, Servant2 = B, Count = 2 };
        Assert.NotEqual(first.Key, resent.Key);
        Assert.Empty(ServantLogic.Finished(new[] { resent }, new[] { resent }));
        var gone = ServantLogic.Finished(new[] { resent }, Array.Empty<MissionRow>());
        Assert.Same(resent, Assert.Single(gone));

        // Each decoded row carries its own servants.
        var bytes = new byte[0x40 * 2];
        ServantLogic.EncodeRow(first, bytes.AsSpan(0, 0x40));
        ServantLogic.EncodeRow(new MissionRow { MissionGuid = OtherMission, StartTicks = Now + 1, LengthSeconds = 60, Servant1 = C, Count = 1 }, bytes.AsSpan(0x40, 0x40));
        var r0 = ServantLogic.DecodeRow(bytes.AsSpan(0, 0x40));
        var r1 = ServantLogic.DecodeRow(bytes.AsSpan(0x40, 0x40));
        Assert.Equal(new[] { A, B }, r0.Servants());
        Assert.Equal(new[] { C }, r1.Servants());
    }

    [Fact]
    public void Two_finishes_in_quick_succession_are_two_returns()
    {
        // Finish (start backdated), return, resend, finish again: each update's snapshot finds only
        // the row that was due then.
        long backdated1 = Now - 7201 * ServantLogic.TicksPerSecond;
        var first = new MissionRow { MissionGuid = Mission, StartTicks = backdated1, LengthSeconds = 7200, Servant1 = A, Servant2 = B, Count = 2 };
        Assert.True(ServantLogic.IsDue(first, Now, 0, false));
        Assert.Same(first, Assert.Single(ServantLogic.Finished(new[] { first }, Array.Empty<MissionRow>())));

        long later = Now + 30 * ServantLogic.TicksPerSecond;
        var resent = new MissionRow { MissionGuid = Mission, StartTicks = later, LengthSeconds = 7200, Servant1 = A, Servant2 = B, Count = 2 };
        Assert.False(ServantLogic.IsDue(resent, later, 2 * ServantLogic.TicksPerSecond, false));
        var backdated2 = new MissionRow { MissionGuid = Mission, StartTicks = later + 1 - 7201 * ServantLogic.TicksPerSecond, LengthSeconds = 7200, Servant1 = A, Servant2 = B, Count = 2 };
        Assert.True(ServantLogic.IsDue(backdated2, later + 1, 0, false));
        Assert.NotEqual(first.Key, backdated2.Key);
        Assert.Same(backdated2, Assert.Single(ServantLogic.Finished(new[] { backdated2 }, Array.Empty<MissionRow>())));
    }

    [Fact]
    public void Loot_adds_up_per_item_in_first_seen_order()
    {
        var loot = ServantLogic.Loot(new[] { (5, 10), (0, 3), (6, 2), (5, 7), (7, 0) });
        Assert.Equal(2, loot.Count);
        Assert.Equal((5, 17), (loot[0].Item, loot[0].Amount));
        Assert.Equal((6, 2), (loot[1].Item, loot[1].Amount));

        var ev = new ServantsReturned
        {
            Servants = new[]
            {
                new ReturnedServant { Servant = A, Loot = new[] { new ServantLoot(5, 1) } },
                new ReturnedServant { Servant = B, Loot = new[] { new ServantLoot(6, 2), new ServantLoot(5, 4) } },
            },
        };
        Assert.Equal(new[] { (5, 5), (6, 2) }, Pairs(ev.TotalLoot));
    }

    [Fact]
    public void Survivors_are_the_unhurt_servants()
    {
        var back = new[]
        {
            new ReturnedServant { Servant = A, Outcome = ServantOutcome.Returned },
            new ReturnedServant { Servant = B, Outcome = ServantOutcome.Injured },
            new ReturnedServant { Servant = C, Outcome = ServantOutcome.Dead },
            new ReturnedServant { Servant = D, Outcome = ServantOutcome.Returned },
        };
        Assert.Equal(new[] { A, D }, ServantLogic.Survivors(back));
    }

    // ---- the send validation ------------------------------------------------------------------------

    [Fact]
    public void A_valid_send_plans_the_row_the_game_writes()
    {
        var plan = ServantLogic.PlanSend(Facts(Ready(A), Ready(B)));
        Assert.True(plan.Ok, plan.Reason);
        var row = plan.Row!;
        Assert.Equal(Mission, row.MissionGuid);
        Assert.Equal(Now, row.StartTicks);
        Assert.Equal(7200f, row.LengthSeconds);
        Assert.Equal(1, row.DataSlot);
        Assert.Equal(A, row.Servant1);
        Assert.Equal(B, row.Servant2);
        Assert.True(row.Servant3.IsNone);
        Assert.Equal(2, row.Count);
    }

    [Fact]
    public void Timers_off_shortens_the_written_length()
    {
        var f = Facts(Ready(A));
        f.TimersDisabled = true;
        Assert.Equal(7200f * 1e-5f, ServantLogic.PlanSend(f).Row!.LengthSeconds);
    }

    public static IEnumerable<object[]> Refusals()
    {
        yield return Case(f => f.HeartOk = false, ServantSendRefusal.HeartGone);
        yield return Case(f => f.ProgressionReadable = false, ServantSendRefusal.NoProgression);
        yield return Case(f => f.ZoneKnown = false, ServantSendRefusal.MissionUnknown);
        yield return Case(f => f.Discovered = false, ServantSendRefusal.ZoneLocked);
        yield return Case(f => f.Servants = Array.Empty<ServantFacts>(), ServantSendRefusal.ServantCount);
        yield return Case(f => f.Servants = new[] { Ready(A), Ready(B), Ready(C), Ready(D) }, ServantSendRefusal.ServantCount);
        yield return Case(f => f.Servants = new[] { Ready(A), Ready(A) }, ServantSendRefusal.DuplicateServant);
        yield return Case(f => f.Servants = new[] { With(Ready(A), s => s.Exists = false) }, ServantSendRefusal.ServantGone);
        yield return Case(f => f.Servants = new[] { With(Ready(A), s => s.IsServant = false) }, ServantSendRefusal.ServantGone);
        yield return Case(f => f.Servants = new[] { With(Ready(A), s => s.OnMission = true) }, ServantSendRefusal.ServantBusy);
        yield return Case(f => f.Servants = new[] { With(Ready(A), s => s.Dead = true) }, ServantSendRefusal.ServantDead);
        yield return Case(f => f.Servants = new[] { With(Ready(A), s => s.ItemStacks = 2) }, ServantSendRefusal.ServantHasItems);
        yield return Case(f => f.Servants = new[] { With(Ready(A), s => s.ItemStacks = -1) }, ServantSendRefusal.ServantHasItems);
        yield return Case(f => f.Servants = new[] { With(Ready(A), s => s.Coffin = CastleId.None) }, ServantSendRefusal.ServantGone);
        yield return Case(f => f.Servants = new[] { With(Ready(A), s => s.InjuryEndTicks = Now + 1) }, ServantSendRefusal.ServantInjured);
        yield return Case(f => f.Servants = new[] { With(Ready(A), s => s.CoffinHeart = OtherHeart) }, ServantSendRefusal.ServantOfOtherCastle);
        yield return Case(f => f.Existing = new[] { new MissionRow { MissionGuid = Mission, Servant1 = D } }, ServantSendRefusal.ZoneBusy);
        yield return Case(f => f.Existing = new[] { new MissionRow { MissionGuid = OtherMission, Servant1 = D, Servant2 = A } }, ServantSendRefusal.ServantBusy);
        yield return Case(f => f.MissionDataKnown = false, ServantSendRefusal.MissionUnknown);
        yield return Case(f => { f.ServantSlots = 1; f.Servants = new[] { Ready(A), Ready(B) }; }, ServantSendRefusal.TooManyForMission);
        yield return Case(f => f.SettingsReadable = false, ServantSendRefusal.SettingsMissing);
        yield return Case(f => f.DataSlot = 4, ServantSendRefusal.DataSlotUnknown);
        yield return Case(f => f.DataSlot = -1, ServantSendRefusal.DataSlotUnknown);
    }

    private static object[] Case(Action<SendFacts> change, ServantSendRefusal refusal) => new object[] { change, refusal };

    private static ServantFacts With(ServantFacts s, Action<ServantFacts> change)
    {
        change(s);
        return s;
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Each_game_check_refuses_with_a_reason(object change, ServantSendRefusal refusal)
    {
        var f = Facts(Ready(A));
        ((Action<SendFacts>)change)(f);
        var plan = ServantLogic.PlanSend(f);
        Assert.False(plan.Ok);
        Assert.Equal(refusal, plan.Refusal);
        Assert.False(string.IsNullOrWhiteSpace(plan.Reason));
        Assert.Null(plan.Row);
    }

    [Fact]
    public void Discovery_is_not_checked_when_asked_not_to_or_when_progression_is_unlocked()
    {
        var f = Facts(Ready(A));
        f.Discovered = false;
        f.ProgressionReadable = false;
        f.CheckDiscovered = false;
        Assert.True(ServantLogic.PlanSend(f).Ok);
        f.CheckDiscovered = true;
        f.SkipProgression = true;
        Assert.True(ServantLogic.PlanSend(f).Ok);
    }

    [Fact]
    public void Refusal_names_the_servant()
    {
        var plan = ServantLogic.PlanSend(Facts(With(Ready(A, "Placeholder"), s => s.InjuryEndTicks = Now + 10)));
        Assert.Contains("'Placeholder'", plan.Reason);
        Assert.Contains("injured", plan.Reason);
    }

    // ---- byte layouts -----------------------------------------------------------------------------

    [Fact]
    public void Mission_row_layout_matches_the_metadata()
    {
        Assert.Equal(0x40, Layout.ActiveServantMissionStride);
        Assert.Equal(0x18, Layout.ActiveServantMissionServant1Offset);
        Assert.Equal(12, Layout.ActiveServantMissionServant2Offset - Layout.ActiveServantMissionServant1Offset);
        Assert.Equal(0x3C, Layout.ActiveServantMissionCountOffset);
    }

    [Fact]
    public void Mission_row_round_trips_and_writes_unsynced_server_entities()
    {
        var row = new MissionRow { MissionGuid = Mission, StartTicks = Now, LengthSeconds = 3600f, DataSlot = 2, Servant1 = A, Servant2 = B, Count = 2 };
        var bytes = new byte[Layout.ActiveServantMissionStride];
        Array.Fill(bytes, (byte)0xEE);
        ServantLogic.EncodeRow(row, bytes);
        Assert.Equal(Mission, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0)));
        Assert.Equal(Now, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8)));
        Assert.Equal(0, bytes[Layout.ActiveServantMissionServant1Offset + Layout.NetworkedEntityWaitingForSyncOffset]);
        // the empty third slot and every padding byte are zero
        for (int i = Layout.ActiveServantMissionServant3Offset; i < Layout.ActiveServantMissionCountOffset; i++) Assert.Equal(0, bytes[i]);
        for (int i = Layout.ActiveServantMissionServant1Offset + 9; i < Layout.ActiveServantMissionServant2Offset; i++) Assert.Equal(0, bytes[i]);
        var back = ServantLogic.DecodeRow(bytes);
        Assert.Equal(row.Key, back.Key);
        Assert.Equal(row.LengthSeconds, back.LengthSeconds);
        Assert.Equal(row.DataSlot, back.DataSlot);
        Assert.Equal(B, back.Servant2);
        Assert.True(back.Servant3.IsNone);
        Assert.Equal(2, back.Count);
        Assert.True(back.Names(B));
        Assert.False(back.Names(CastleId.None));
    }

    [Fact]
    public void Fixed_list_padding_follows_the_collections_rule()
    {
        Assert.Equal(6, ServantLogic.FixedListPadding(0x70));
        Assert.Equal(0, ServantLogic.FixedListPadding(1));
        Assert.Equal(0, ServantLogic.FixedListPadding(2));
        Assert.Equal(2, ServantLogic.FixedListPadding(4));
        Assert.Equal(6, ServantLogic.FixedListPadding(8));
        Assert.Equal(8, ServantLogic.ResponseFirstEntryOffset);
        Assert.Equal(36, ServantLogic.ResponseCapacity);
        Assert.Equal(0x70, Layout.ServantInfoEntryStride);
    }

    [Fact]
    public void Throne_list_writes_length_entries_and_clears_the_rest()
    {
        var list = new byte[ServantLogic.ResponseListBytes];
        Array.Fill(list, (byte)0xAB);
        var entries = new List<ServantLogic.ThroneEntry>();
        for (int i = 0; i < 40; i++)
        {
            var e = new ServantLogic.ThroneEntry { State = i % 2 == 0 ? ServantState.Free : ServantState.Injured, HuntProficiency = 100 + i, Perk0 = 9 };
            BinaryPrimitives.WriteInt32LittleEndian(e.NetworkId, 1000 + i);
            e.NameBytes[0] = 1;
            e.NameBytes[2] = (byte)'a';
            entries.Add(e);
        }
        int n = ServantLogic.WriteResponseList(list, entries);
        Assert.Equal(36, n);
        Assert.Equal(36, BinaryPrimitives.ReadUInt16LittleEndian(list));
        for (int i = 2; i < 8; i++) Assert.Equal(0, list[i]);
        var rows = ServantLogic.ReadResponseList(list);
        Assert.Equal(36, rows.Count);
        Assert.Equal(1000 + 35, BinaryPrimitives.ReadInt32LittleEndian(rows[35].NetworkId));
        Assert.Equal(ServantState.Injured, rows[1].State);
        int second = ServantLogic.ResponseFirstEntryOffset + Layout.ServantInfoEntryStride;
        Assert.Equal(101f, BinaryPrimitives.ReadSingleLittleEndian(list.AsSpan(second + Layout.ServantInfoEntryHuntProficiencyOffset)));
        Assert.Equal(9, BinaryPrimitives.ReadInt32LittleEndian(list.AsSpan(second + Layout.ServantInfoEntryPerk0Offset)));
        int end = ServantLogic.ResponseFirstEntryOffset + 36 * Layout.ServantInfoEntryStride;
        for (int i = end; i < list.Length; i++) Assert.Equal(0, list[i]);
    }

    [Fact]
    public void NetworkIds_compare_index_generation_and_type()
    {
        var a = new byte[12];
        var b = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(a, 77);
        BinaryPrimitives.WriteInt32LittleEndian(b, 77);
        a[4] = b[4] = 3;
        a[5] = 0x55; // padding is ignored
        Assert.True(ServantLogic.SameNetworkId(a, b));
        b[Layout.NetworkIdTypeOffset] = 1;
        Assert.False(ServantLogic.SameNetworkId(a, b));
        Assert.True(ServantLogic.IsEmptyNetworkId(new byte[12]));
        Assert.False(ServantLogic.IsEmptyNetworkId(a));
    }

    [Fact]
    public void Zones_decode_and_discovery_walks_three_byte_rows()
    {
        Assert.Equal(5, ServantLogic.ZoneIdOf(4));
        var zone = new MissionZone(-3, 7, 5);
        var discovered = new byte[] { 1, 2, 3, unchecked((byte)-3), 7, 5, 9, 9, 9 };
        Assert.Equal(new MissionZone(1, 2, 3), ServantLogic.ReadZone(discovered));
        Assert.True(ServantLogic.HasDiscovered(discovered, 3, zone));
        Assert.False(ServantLogic.HasDiscovered(discovered, 1, zone));
        Assert.False(ServantLogic.HasDiscovered(discovered, 3, new MissionZone(-3, 7, 4)));
        Assert.Equal("-3,7#5", zone.ToString());
    }

    // ---- the list provider registry ---------------------------------------------------------------

    private sealed class Fixed : IServantListProvider
    {
        public IReadOnlyList<CastleId>? HeartsFor(ServantListRequest request) => new[] { Heart };
    }

    [Fact]
    public void One_provider_at_a_time_and_a_stale_handle_removes_nothing()
    {
        var reg = new ServantListRegistry();
        int dropped = 0;
        reg.Unregistered = () => dropped++;
        var first = reg.Register(new Fixed(), "mod.one");
        var ex = Assert.Throws<InvalidOperationException>(() => reg.Register(new Fixed(), "mod.two"));
        Assert.Contains("mod.one", ex.Message);
        first.Dispose();
        Assert.False(reg.Registered);
        var second = reg.Register(new Fixed(), "mod.two");
        first.Dispose();
        Assert.True(reg.Registered);
        Assert.Equal("mod.two", reg.Owner);
        Assert.False(reg.RemoveOwner("mod.one"));
        Assert.True(reg.RemoveOwner("mod.two"));
        Assert.False(reg.Registered);
        second.Dispose();
        Assert.Equal(2, dropped); // once per real unregistration, never for a stale handle
    }

    [Fact]
    public void Shown_lists_expire_and_go_with_the_provider()
    {
        var reg = new ServantListRegistry();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        reg.RecordShown(4, new ServantListRegistry.Shown { AtUtc = t0 });
        Assert.Null(reg.ShownTo(4, t0)); // nothing recorded without a provider
        var h = reg.Register(new Fixed(), "mod.one");
        reg.RecordShown(4, new ServantListRegistry.Shown { AtUtc = t0 });
        Assert.True(reg.AnyShown);
        Assert.NotNull(reg.ShownTo(4, t0.AddSeconds(90)));
        Assert.Null(reg.ShownTo(4, t0.AddMinutes(3)));
        reg.RecordShown(5, new ServantListRegistry.Shown { AtUtc = t0 });
        reg.CountRouted(true);
        reg.CountRouted(false);
        var status = reg.Status();
        Assert.Equal(2, status.Swaps);
        Assert.Equal(1, status.RoutedSends);
        Assert.Equal(1, status.RoutedRefusals);
        h.Dispose();
        Assert.False(reg.AnyShown);
        Assert.Null(reg.ShownTo(5, t0));
    }

    // ---- the sample -------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "", "", "", true)]
    [InlineData("list", "", "", "", true)]
    [InlineData("hunts", "", "", "", true)]
    [InlineData("send", "#1,#2", "777001", "1", true)]
    [InlineData("send", "1", "3,-2#4", "0", true)]
    [InlineData("send", "1,1", "777001", "1", false)]
    [InlineData("send", "1,2,3,4", "777001", "1", false)]
    [InlineData("send", "1", "3,-2#0", "1", false)]
    [InlineData("send", "1", "777001", "-1", false)]
    [InlineData("watch", "resend", "", "", true)]
    [InlineData("watch", "maybe", "", "", false)]
    [InlineData("swap", "on", "", "", true)]
    [InlineData("again", "", "", "", true)]
    [InlineData("bogus", "", "", "", false)]
    public void Sample_parses_its_verbs(string verb, string a, string b, string c, bool ok) =>
        Assert.Equal(ok, SampleServants.TryParse(verb, a, b, c, out _));

    [Fact]
    public void Sample_send_by_zone_and_by_guid()
    {
        Assert.True(SampleServants.TryParse("send", "#2,#3", "3,-2#4", "2", out var byZone));
        Assert.True(byZone.ByZone);
        Assert.Equal(new MissionZone(3, -2, 4), byZone.Zone);
        Assert.Equal(new[] { 2, 3 }, byZone.Rows);
        Assert.Equal(2, byZone.Slot);
        Assert.True(SampleServants.TryParse("send", "1", "777001", "0", out var byGuid));
        Assert.False(byGuid.ByZone);
        Assert.Equal(777001, byGuid.MissionGuid);
    }

    [Fact]
    public void Sample_lines_and_hearts_order()
    {
        Assert.Equal("1h05m", SampleServants.FormatSeconds(3900));
        Assert.Equal("4m10s", SampleServants.FormatSeconds(250));
        Assert.Equal("12s", SampleServants.FormatSeconds(12));
        Assert.Equal(new[] { OtherHeart, Heart }, SampleServants.HeartsInOrder(OtherHeart, new[] { Heart, OtherHeart }));
        var line = SampleServants.ListLine(1, new ServantInfo { Name = "Placeholder", State = ServantState.HasItems, HuntProficiency = 150, ItemStacks = 3, Servant = A });
        Assert.Contains("#1 Placeholder HasItems prof 150 items 3", line);
        var ev = new ServantsReturned
        {
            Heart = Heart,
            Territory = 7,
            MissionGuid = Mission,
            Servants = new[] { new ReturnedServant { Servant = A, Name = "P1", Outcome = ServantOutcome.Returned, Loot = new[] { new ServantLoot(5, 3) } } },
        };
        Assert.Contains("servants=P1:Returned loot=3x5", SampleServants.ReturnLine(ev));
    }

    private static List<(int, int)> Pairs(IReadOnlyList<ServantLoot> loot)
    {
        var list = new List<(int, int)>();
        foreach (var l in loot) list.Add((l.Item, l.Amount));
        return list;
    }

    [Fact]
    public void Result_text_says_sent_or_why()
    {
        var ok = new ServantSendResult { Ok = true, Servants = new[] { A }, MissionGuid = Mission, DataSlot = 1, LengthSeconds = 60 };
        Assert.StartsWith("sent 1 servant(s)", ok.ToString());
        var no = ServantSendResult.Refused(ServantSendRefusal.ZoneBusy, "busy");
        Assert.Equal("refused (ZoneBusy): busy", no.ToString());
        Assert.True(InventoryRef.None.IsNone);
    }
}
