using System.Collections.Generic;
using Progenitor.Castles;
using Progenitor.Servants;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The success cap's runtime selection rules, game-free: which row of a heart the game finishes
/// (the first due one), when a capped row is held instead of vetoed (not due by Progenitor's
/// clock, after an uncertain row, while a save is in flight), the world-load gate, the save
/// tracker, and keeping a pending-restore record until a later save completed.
/// </summary>
public class ServantCapSelectionTests
{
    private static CapRowState Row(bool dueSoon, bool dueNow, bool capped = false, bool mustHold = false) => new(dueSoon, dueNow, capped, mustHold);

    private static CapRowState NotDue => Row(false, false);
    private static CapRowState Due(bool capped = false) => Row(true, true, capped);
    private static CapRowState Soon(bool capped = false) => Row(true, false, capped);

    private static CapRowAction[] Select(bool saving, params CapRowState[] rows) =>
        CapSelection.Select(rows, timersDisabled: false, saveInFlight: saving, out _);

    [Fact]
    public void Only_the_first_due_row_of_a_heart_is_vetoed()
    {
        var a = Select(false, NotDue, Due(capped: true), Due(capped: true));
        Assert.Equal(new[] { CapRowAction.None, CapRowAction.Veto, CapRowAction.None }, a);
    }

    [Fact]
    public void An_uncapped_first_due_row_shields_the_capped_rows_after_it()
    {
        Assert.Equal(new[] { CapRowAction.None, CapRowAction.None }, Select(false, Due(), Due(capped: true)));
    }

    [Fact]
    public void A_capped_row_due_only_within_the_slack_is_held_and_the_walk_goes_on()
    {
        // The held row reads as not due to the game, so the next due row is the one finished.
        var a = Select(false, Soon(capped: true), Due(capped: true));
        Assert.Equal(new[] { CapRowAction.Hold, CapRowAction.Veto }, a);
    }

    [Fact]
    public void After_an_uncertain_uncapped_row_a_capped_row_is_held_not_vetoed()
    {
        // Row 0 may or may not be due by the game's later clock: the game finishes it or row 1.
        var a = Select(false, Soon(), Due(capped: true), Due(capped: true));
        Assert.Equal(new[] { CapRowAction.None, CapRowAction.Hold, CapRowAction.Hold }, a);
        // An uncapped due row after the uncertain one ends the walk: nothing later is touched.
        Assert.Equal(new[] { CapRowAction.None, CapRowAction.None, CapRowAction.None }, Select(false, Soon(), Due(), Due(capped: true)));
    }

    [Fact]
    public void While_a_save_is_in_flight_capped_rows_are_held_never_vetoed()
    {
        var a = Select(true, Due(capped: true), Due());
        Assert.Equal(new[] { CapRowAction.Hold, CapRowAction.None }, a);
        Assert.Equal(new[] { CapRowAction.None }, Select(true, Due())); // uncapped rows are untouched
    }

    [Fact]
    public void While_the_book_cannot_be_read_every_due_row_is_held()
    {
        var a = Select(false, Row(true, true, mustHold: true), NotDue, Row(true, false, mustHold: true));
        Assert.Equal(new[] { CapRowAction.Hold, CapRowAction.None, CapRowAction.Hold }, a);
    }

    [Fact]
    public void With_timers_off_only_row_zero_finishes_and_nothing_can_be_held()
    {
        var rows = new[] { Due(capped: true), Due(capped: true) };
        Assert.Equal(new[] { CapRowAction.Veto, CapRowAction.None }, CapSelection.Select(rows, true, false, out bool uncapped));
        Assert.False(uncapped);
        Assert.Equal(new[] { CapRowAction.None, CapRowAction.None }, CapSelection.Select(rows, true, true, out uncapped));
        Assert.True(uncapped); // during a save it finishes uncapped (logged)
        Assert.Equal(new[] { CapRowAction.None, CapRowAction.None }, CapSelection.Select(new[] { Due(), Due(capped: true) }, true, false, out uncapped));
        Assert.False(uncapped);
    }

    [Fact]
    public void The_world_load_gate()
    {
        Assert.False(CapGate.WorldLoaded(0, false)); // the hooks fire while the world is still being built
        Assert.True(CapGate.WorldLoaded(1, false));
        Assert.True(CapGate.WorldLoaded(0, true));  // a Core swap into a running world
        Assert.False(CapGate.CanSweep(false, true, "w1"));
        Assert.False(CapGate.CanSweep(true, false, "w1"));
        Assert.False(CapGate.CanSweep(true, true, null));
        Assert.True(CapGate.CanSweep(true, true, "w1"));
    }

    [Fact]
    public void A_record_is_kept_until_a_save_that_started_after_the_restore_completed()
    {
        var saves = new SaveTracker();
        var kept = new RetainedRestores();

        // A save is in flight when the restore runs (a late restore): that save does not count.
        saves.Observe(true);
        kept.Keep("restore:w:1:1:5", saves.Mark());
        saves.Observe(false);
        Assert.Empty(kept.Release(saves));

        // The next save starts and is still running: kept.
        saves.Observe(true);
        Assert.Empty(kept.Release(saves));
        saves.Observe(true);
        Assert.Empty(kept.Release(saves));

        // It completes: released.
        saves.Observe(false);
        Assert.Equal(new[] { "restore:w:1:1:5" }, kept.Release(saves));
        Assert.Equal(0, kept.Count);
    }

    [Fact]
    public void A_restore_with_no_save_in_flight_waits_for_the_next_save()
    {
        var saves = new SaveTracker();
        saves.Observe(true);
        saves.Observe(false); // an earlier, finished save
        var kept = new RetainedRestores();
        kept.Keep("k", saves.Mark());
        Assert.False(saves.SavedSince(saves.Mark()));
        Assert.Empty(kept.Release(saves));
        saves.Observe(true);
        saves.Observe(false);
        Assert.Single(kept.Release(saves));
        Assert.Equal(2, saves.Started);
        Assert.Equal(2, saves.Completed);
    }

    [Fact]
    public void Keeping_a_key_twice_keeps_the_later_mark()
    {
        var saves = new SaveTracker();
        var kept = new RetainedRestores();
        kept.Keep("k", saves.Mark());
        saves.Observe(true);
        kept.Keep("k", saves.Mark()); // re-kept by a sweep during the save
        saves.Observe(false);
        Assert.Empty(kept.Release(saves));
        Assert.Equal(1, kept.Count);
    }

    [Theory]
    // The game's rounding helper is IL2CPP's Math.Round(double): ties go to the even integer.
    [InlineData(2.25f, 0.375f, 22 + 100 + 38)]  // 22.5 -> 22, 37.5 -> 38
    [InlineData(2.75f, 0.125f, 28 + 100 + 12)]  // 27.5 -> 28, 12.5 -> 12
    [InlineData(-0.25f, 0f, -2 + 100 + 0)]      // -2.5 -> -2
    public void Proficiency_rounds_ties_to_even_like_the_game(float gear, float expertise, int expected)
    {
        Assert.Equal(expected, ServantLogic.HuntProficiency(gear, expertise));
    }
}
