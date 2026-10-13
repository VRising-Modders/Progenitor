using System;
using System.Collections.Generic;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The edit baselines (roadmap M2.4) and the pass that drives them: removing an editor puts the
/// components it wrote back to the values they had before the first edit.
///
/// <para><b>What runs here is the production ladder.</b> Job FEDIT1 wrote these tests against a
/// hand copy of <c>PrefabRegistry.TryRunEdits</c> living in this file, and R49 finding 2 found
/// that the copy had already diverged from the original in four places while every test still
/// passed. The ladder now lives in <see cref="PrefabEditPass.Run"/>, the game-typed steps behind
/// <see cref="IPrefabEditPass"/>, and <see cref="FakePass"/> below is only the component bag half
/// — the decisions (restore or drop by trigger, restore-only when no editors remain, bind before
/// drop, the retry budget for an unserviced removal) are the shipped ones.</para>
///
/// <para><see cref="FakeHandle"/> is the same thing for the write site: it calls the production
/// <see cref="PrefabEditPass.TryCapture"/>, so "capture only when needed, before the write, never
/// for a component the entity does not carry" is one rule and not two.</para>
/// </summary>
public class PrefabEditBaselineTests
{
    private const int GuidHash = 42;

    [Fact]
    public void First_write_captures_vanilla_and_a_second_write_does_not_move_it()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var baselines = new PrefabEditBaselines();
        var handle = new FakeHandle(world, baselines, GuidHash);

        handle.Write("ItemData", 5);
        handle.Write("ItemData", 9);

        Assert.Equal(new[] { "ItemData" }, baselines.Components(GuidHash));
        Assert.Equal(new[] { "ItemData" }, baselines.Restore(GuidHash, world.EntityIndex, world.EntityVersion, world));
        Assert.Equal(1, world.Values["ItemData"]);
    }

    [Fact]
    public void Reading_does_not_capture()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var baselines = new PrefabEditBaselines();
        var handle = new FakeHandle(world, baselines, GuidHash);

        handle.Read("ItemData");

        Assert.Equal(0, baselines.Count(GuidHash));
    }

    /// <summary>
    /// The write site's third rule: a component the entity does not carry has no vanilla value
    /// to put back, and the write itself is going to fail. <c>TryCapture</c> refuses it rather
    /// than parking a restore callback that would write a struct the entity never had.
    /// </summary>
    [Fact]
    public void A_component_the_entity_does_not_carry_is_not_captured()
    {
        var world = new FakeWorld();
        var baselines = new PrefabEditBaselines();

        bool captured = PrefabEditPass.TryCapture(
            baselines, GuidHash, "Absent", componentPresent: false,
            world.EntityIndex, world.EntityVersion, _ => throw new InvalidOperationException("never"));

        Assert.False(captured);
        Assert.Equal(0, baselines.Count(GuidHash));
        Assert.Empty(baselines.Restore(GuidHash, world.EntityIndex, world.EntityVersion, world));
    }

    [Fact]
    public void Capture_is_refused_a_second_time_and_a_null_store_is_not_a_crash()
    {
        var world = new FakeWorld();
        var baselines = new PrefabEditBaselines();
        Action<object> restore = ctx => ((FakeWorld)ctx).Values["ItemData"] = 1;

        Assert.False(PrefabEditPass.NeedsCapture(null, GuidHash, "ItemData"));
        Assert.False(PrefabEditPass.TryCapture(null, GuidHash, "ItemData", true, 7, 1, restore));

        Assert.True(PrefabEditPass.NeedsCapture(baselines, GuidHash, "ItemData"));
        Assert.True(PrefabEditPass.TryCapture(baselines, GuidHash, "ItemData", true, 7, 1, restore));
        Assert.False(PrefabEditPass.NeedsCapture(baselines, GuidHash, "ItemData"));
        Assert.False(PrefabEditPass.TryCapture(baselines, GuidHash, "ItemData", true, 7, 1, restore));
        Assert.Equal(1, baselines.Count(GuidHash));
    }

    [Fact]
    public void Removing_the_last_editor_restores_vanilla_and_clears_the_store()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var table = NewTable();
        Add(table, "a", h => h.Write("ItemData", 7), out var handle);

        var pass = Pass(table, world, PrefabEditTrigger.EditorsChanged);
        Assert.Equal(PrefabEditPassOutcome.RestoredThenRan, pass.Outcome);
        Assert.Equal(7, world.Values["ItemData"]);
        Assert.Empty(pass.Log);

        handle.Dispose();
        Assert.Equal(new[] { GuidHash }, table.DrainPendingRemovals());

        pass = Pass(table, world, PrefabEditTrigger.EditorsChanged);

        Assert.Equal(PrefabEditPassOutcome.Restored, pass.Outcome);
        Assert.True(PrefabEditPass.RemovalServiced(pass.Outcome));
        Assert.Equal(1, world.Values["ItemData"]);
        Assert.Equal(0, table.Baselines.Count(GuidHash));
        Assert.Equal(
            new[] { PrefabEditLog.RestoredAfterLastEditor(GuidHash, new[] { "ItemData" }) },
            pass.Log);
    }

    [Fact]
    public void Removing_one_editor_restores_then_the_remaining_editor_wins()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        world.Values["Durability"] = 2;
        var table = NewTable();
        Add(table, "a", h => h.Write("ItemData", 7), out var handleA);
        Add(table, "b", h => h.Write("Durability", 8), out _);

        Pass(table, world, PrefabEditTrigger.EditorsChanged);
        Assert.Equal(7, world.Values["ItemData"]);
        Assert.Equal(8, world.Values["Durability"]);

        handleA.Dispose();
        var pass = Pass(table, world, PrefabEditTrigger.EditorsChanged);

        Assert.Equal(PrefabEditPassOutcome.RestoredThenRan, pass.Outcome);
        Assert.Equal(1, world.Values["ItemData"]);
        Assert.Equal(8, world.Values["Durability"]);
        Assert.Equal(
            new[] { PrefabEditLog.RestoredBeforeRerun(GuidHash, 2, 1) },
            pass.Log);
    }

    [Fact]
    public void A_pass_is_idempotent_for_a_read_modify_write_editor()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 10;
        var table = NewTable();
        Add(table, "a", h => h.Write("ItemData", h.Read("ItemData") * 2), out _);

        Pass(table, world, PrefabEditTrigger.EditorsChanged);
        Assert.Equal(20, world.Values["ItemData"]);

        Pass(table, world, PrefabEditTrigger.Rehydrate);
        Assert.Equal(20, world.Values["ItemData"]);
    }

    [Fact]
    public void New_clone_drops_the_baselines_without_restoring()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var table = NewTable();
        Add(table, "a", h => h.Write("ItemData", 7), out _);
        Pass(table, world, PrefabEditTrigger.EditorsChanged);

        // The applicator just wrote definition values onto the (re)created entity.
        world.Values["ItemData"] = 100;
        var pass = Pass(table, world, PrefabEditTrigger.NewClone);

        Assert.Equal(PrefabEditPassOutcome.DroppedThenRan, pass.Outcome);
        Assert.Equal(7, world.Values["ItemData"]);
        Assert.Equal(new[] { "ItemData" }, table.Baselines.Components(GuidHash));
        Assert.Equal(new[] { "ItemData" }, table.Baselines.Restore(GuidHash, world.EntityIndex, world.EntityVersion, world));
        Assert.Equal(100, world.Values["ItemData"]);
    }

    /// <summary>
    /// The bind comes before the drop, which is the ordering the hand copy got wrong (R49
    /// finding 2). A <c>NewClone</c> pass whose bind fails — the collection does not have the
    /// entity yet — must leave the previous entity's baselines in the store; dropping them for
    /// an entity nothing was written to would forget vanilla for good.
    /// </summary>
    [Fact]
    public void A_new_clone_pass_whose_bind_fails_keeps_the_baselines()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var table = NewTable();
        Add(table, "a", h => h.Write("ItemData", 7), out _);
        Pass(table, world, PrefabEditTrigger.EditorsChanged);
        Assert.Equal(1, table.Baselines.Count(GuidHash));

        var pass = Pass(table, world, PrefabEditTrigger.NewClone, bindFails: true);

        Assert.Equal(PrefabEditPassOutcome.BindFailed, pass.Outcome);
        Assert.Equal(new[] { "ItemData" }, table.Baselines.Components(GuidHash));
        Assert.Equal(7, world.Values["ItemData"]);
    }

    /// <summary>
    /// Removal + a run already in flight. <c>TryBeginRun</c> refuses, the drained removal did
    /// nothing, and the flush has to put it back — for the last-editor case nothing else ever
    /// visits this GUID again (R49 finding 6).
    /// </summary>
    [Fact]
    public void A_removal_that_lands_on_a_run_in_flight_is_not_serviced()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var table = NewTable();
        Add(table, "a", h => h.Write("ItemData", 7), out _);
        Pass(table, world, PrefabEditTrigger.EditorsChanged);

        Assert.True(table.TryBeginRun(GuidHash));
        var pass = Pass(table, world, PrefabEditTrigger.EditorsChanged);
        table.EndRun(GuidHash);

        Assert.Equal(PrefabEditPassOutcome.RunInFlight, pass.Outcome);
        Assert.False(PrefabEditPass.RemovalServiced(pass.Outcome));
    }

    /// <summary>Removal + no world: nothing is read, nothing is written, nothing is serviced.</summary>
    [Fact]
    public void A_removal_with_no_world_is_not_serviced()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var table = NewTable();
        Add(table, "a", h => h.Write("ItemData", 7), out var handle);
        Pass(table, world, PrefabEditTrigger.EditorsChanged);
        handle.Dispose();
        table.DrainPendingRemovals();

        var pass = Pass(table, world, PrefabEditTrigger.EditorsChanged, worldAlive: false);

        Assert.Equal(PrefabEditPassOutcome.NoWorld, pass.Outcome);
        Assert.False(PrefabEditPass.RemovalServiced(pass.Outcome));
        Assert.Equal(1, table.Baselines.Count(GuidHash));
        Assert.Equal(7, world.Values["ItemData"]);
    }

    /// <summary>
    /// Removal + last editor + a bind that fails. This is the case with no other path back:
    /// <c>RestoreEditsOnly</c> is reachable only from an <c>EditorsChanged</c> trigger and only
    /// a drained removal produces one, so the baselines must stay and the removal must be
    /// re-queued.
    /// </summary>
    [Fact]
    public void A_last_editor_removal_whose_bind_fails_keeps_the_baselines_and_is_not_serviced()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var table = NewTable();
        Add(table, "a", h => h.Write("ItemData", 7), out var handle);
        Pass(table, world, PrefabEditTrigger.EditorsChanged);
        handle.Dispose();
        table.DrainPendingRemovals();

        var pass = Pass(table, world, PrefabEditTrigger.EditorsChanged, bindFails: true);

        Assert.Equal(PrefabEditPassOutcome.RestoreOnlyBindFailed, pass.Outcome);
        Assert.False(PrefabEditPass.RemovalServiced(pass.Outcome));
        Assert.Equal(1, table.Baselines.Count(GuidHash));
        Assert.Equal(7, world.Values["ItemData"]);

        // ... and the retry that follows finds the entity and finishes the job.
        var later = Pass(table, world, PrefabEditTrigger.EditorsChanged);
        Assert.Equal(PrefabEditPassOutcome.Restored, later.Outcome);
        Assert.Equal(1, world.Values["ItemData"]);
        Assert.Equal(0, table.Baselines.Count(GuidHash));
    }

    /// <summary>
    /// No editors and nothing captured is serviced, not owed: there is nothing to put back.
    /// A trigger other than <c>EditorsChanged</c> over an editor-less GUID is not the pass that
    /// restores, and does not touch the store.
    /// </summary>
    [Fact]
    public void An_editorless_guid_restores_only_on_the_editors_changed_trigger()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var table = NewTable();

        var empty = Pass(table, world, PrefabEditTrigger.EditorsChanged);
        Assert.Equal(PrefabEditPassOutcome.NothingToRestore, empty.Outcome);
        Assert.True(PrefabEditPass.RemovalServiced(empty.Outcome));

        Add(table, "a", h => h.Write("ItemData", 7), out var handle);
        Pass(table, world, PrefabEditTrigger.EditorsChanged);
        handle.Dispose();
        table.DrainPendingRemovals();

        var wrong = Pass(table, world, PrefabEditTrigger.CollectionReady);
        Assert.Equal(PrefabEditPassOutcome.NotThisTrigger, wrong.Outcome);
        Assert.Equal(7, world.Values["ItemData"]);
        Assert.Equal(1, table.Baselines.Count(GuidHash));
    }

    /// <summary>
    /// The retry budget: an unserviced removal is put back until it is spent, and the give-up
    /// line names the outcome that kept answering. Bounded, because a permanently dead world
    /// must not re-arm the frame hook forever.
    /// </summary>
    [Fact]
    public void An_unserviced_removal_is_requeued_until_the_budget_is_spent()
    {
        int retries = 0;
        for (int i = 1; i <= PrefabEditPass.MaxRemovalRetries; i++)
        {
            var action = PrefabEditPass.NextRemovalAction(PrefabEditPassOutcome.NoWorld, retries, 1, out retries);
            Assert.Equal(PrefabEditPass.RemovalRetryAction.Requeue, action);
            Assert.Equal(i, retries);
        }

        Assert.Equal(
            PrefabEditPass.RemovalRetryAction.GiveUp,
            PrefabEditPass.NextRemovalAction(PrefabEditPassOutcome.NoWorld, retries, 1, out _));

        Assert.Equal(
            PrefabEditPass.RemovalRetryAction.Serviced,
            PrefabEditPass.NextRemovalAction(PrefabEditPassOutcome.Restored, 5, 1, out int cleared));
        Assert.Equal(0, cleared);

        Assert.Equal(
            "PrefabRegistry: removal never serviced on guid 42 after 30 retry(s): the last answer was "
            + "NoWorld and 1 baseline(s) are still owed, so this prefab keeps the removed editor's"
            + " values until Core uninstall",
            PrefabEditLog.RemovalGivenUp(GuidHash, PrefabEditPassOutcome.NoWorld.ToString(), 30, 1));
    }

    /// <summary>
    /// A GUID that owes nothing is serviced by definition (R50 finding 6). Its editors only read,
    /// or never ran, so no baseline was ever captured: re-queueing it burns thirty flush frames and
    /// then prints a warning claiming the prefab keeps values that were never written.
    /// </summary>
    [Fact]
    public void A_removal_on_a_guid_with_no_baselines_is_not_retried_and_not_warned_about()
    {
        Assert.Equal(
            PrefabEditPass.RemovalRetryAction.NotOwed,
            PrefabEditPass.NextRemovalAction(PrefabEditPassOutcome.NoWorld, 0, 0, out int retries));
        Assert.Equal(0, retries);

        Assert.Equal(
            PrefabEditPass.RemovalRetryAction.NotOwed,
            PrefabEditPass.NextRemovalAction(PrefabEditPassOutcome.RestoreOnlyBindFailed, 7, 0, out retries));
        Assert.Equal(0, retries);

        // And the shape it comes from: two read-only editors, one of them removed while the
        // prefab entity cannot be bound. Nothing was ever captured, so nothing is owed.
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var table = NewTable();
        Add(table, "a", h => h.Read("ItemData"), out var handle);
        Add(table, "b", h => h.Read("ItemData"), out _);
        Pass(table, world, PrefabEditTrigger.EditorsChanged);
        handle.Dispose();
        table.DrainPendingRemovals();
        Assert.Equal(0, table.Baselines.Count(GuidHash));

        var pass = Pass(table, world, PrefabEditTrigger.EditorsChanged, bindFails: true);
        Assert.Equal(PrefabEditPassOutcome.BindFailed, pass.Outcome);
        Assert.Equal(
            PrefabEditPass.RemovalRetryAction.NotOwed,
            PrefabEditPass.NextRemovalAction(pass.Outcome, 0, table.Baselines.Count(GuidHash), out _));
    }

    /// <summary>
    /// The two outcomes a retry cannot help (R50 finding 6): a bind failure on a prefab that still
    /// has editors is owned by the next <c>CollectionReady</c> sweep, and a priority cycle is a
    /// property of the registered editors, not of this frame — before FEDW2 the cycle line was
    /// logged once per re-queue, thirty-one times, with a give-up warning on top.
    /// </summary>
    [Fact]
    public void A_removal_another_path_owns_is_not_requeued()
    {
        Assert.False(PrefabEditPass.RemovalRetryHelps(PrefabEditPassOutcome.BindFailed));
        Assert.False(PrefabEditPass.RemovalRetryHelps(PrefabEditPassOutcome.CycleRefused));
        Assert.True(PrefabEditPass.RemovalRetryHelps(PrefabEditPassOutcome.RestoreOnlyBindFailed));
        Assert.True(PrefabEditPass.RemovalRetryHelps(PrefabEditPassOutcome.NoWorld));
        Assert.True(PrefabEditPass.RemovalRetryHelps(PrefabEditPassOutcome.RunInFlight));

        Assert.Equal(
            PrefabEditPass.RemovalRetryAction.OwnedElsewhere,
            PrefabEditPass.NextRemovalAction(PrefabEditPassOutcome.BindFailed, 0, 2, out int retries));
        Assert.Equal(0, retries);
        Assert.Equal(
            PrefabEditPass.RemovalRetryAction.OwnedElsewhere,
            PrefabEditPass.NextRemovalAction(PrefabEditPassOutcome.CycleRefused, 3, 2, out retries));
        Assert.Equal(0, retries);

        // The last-editor bind failure is the case that still has nowhere else to go.
        Assert.Equal(
            PrefabEditPass.RemovalRetryAction.Requeue,
            PrefabEditPass.NextRemovalAction(PrefabEditPassOutcome.RestoreOnlyBindFailed, 0, 1, out retries));
        Assert.Equal(1, retries);

        Assert.Equal(
            "PrefabRegistry: removal not retried on guid 42: the pass answered BindFailed and the"
            + " prefab still has editors, so the next CollectionReady sweep owns it",
            PrefabEditLog.RemovalNotRetried(
                GuidHash,
                PrefabEditPassOutcome.BindFailed.ToString(),
                PrefabEditPass.RemovalNotRetriedReason(PrefabEditPassOutcome.BindFailed)));
        Assert.Contains(
            "priority cycle",
            PrefabEditPass.RemovalNotRetriedReason(PrefabEditPassOutcome.CycleRefused));
        Assert.Equal("", PrefabEditPass.RemovalNotRetriedReason(PrefabEditPassOutcome.NoWorld));
    }

    /// <summary>
    /// The registry prints each terminal line once per guid per Core generation, and it is the
    /// registry — not the decision function — that holds that set. Pinned at the source, because
    /// the flush needs a world the suite does not have.
    /// </summary>
    [Fact]
    public void The_registry_logs_one_terminal_removal_line_per_guid_per_generation()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabRegistry.cs");
        string body = SourcePin.MethodBody(
            source, "private static void NoteRemovalOutcome(int guidHash, PrefabEditPassOutcome outcome)");

        SourcePin.AssertStatement(body, "int owed = Edits.Baselines.Count(guidHash);");
        SourcePin.AssertUncommented(body, "PrefabEditPass.NextRemovalAction(outcome, soFar, owed, out int retries)");
        SourcePin.AssertStatement(body, "if (_removalNoticed.Add(guidHash))");
        SourcePin.AssertUncommented(source, "_removalNoticed.Clear();");
    }

    /// <summary>
    /// A re-queued removal survives the drain that took it, so the next flush frame runs it
    /// again. This is the half of finding 6 that lives in the table.
    /// </summary>
    [Fact]
    public void A_requeued_removal_comes_back_out_of_the_next_drain()
    {
        var table = NewTable();
        Add(table, "a", _ => { }, out var handle);
        handle.Dispose();

        Assert.Equal(new[] { GuidHash }, table.DrainPendingRemovals());
        Assert.Empty(table.DrainPendingRemovals());

        table.RequeueRemoval(GuidHash);

        Assert.Equal(new[] { GuidHash }, table.DrainPendingRemovals());
        Assert.Empty(table.DrainPendingRemovals());
    }

    /// <summary>
    /// The flush's drain order: a GUID the removal pass already visited was restored and re-run
    /// by that pass, so its pending InputsChanged run is dropped rather than run a second time.
    /// </summary>
    [Fact]
    public void A_pending_run_for_a_guid_the_removal_pass_visited_is_dropped()
    {
        Assert.Equal(new[] { 2, 4 }, PrefabEditPass.PendingRunsToRun(new[] { 1, 3 }, new[] { 1, 2, 3, 4 }));
        Assert.Equal(new[] { 1, 2 }, PrefabEditPass.PendingRunsToRun(Array.Empty<int>(), new[] { 1, 2 }));
        Assert.Empty(PrefabEditPass.PendingRunsToRun(new[] { 1 }, new[] { 1 }));
        Assert.Empty(PrefabEditPass.PendingRunsToRun(new[] { 1 }, Array.Empty<int>()));
        Assert.Empty(PrefabEditPass.PendingRunsToRun(null, null));
    }

    [Fact]
    public void A_baseline_from_another_entity_is_dropped_not_written()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var baselines = new PrefabEditBaselines();
        new FakeHandle(world, baselines, GuidHash).Write("ItemData", 7);

        world.EntityVersion++;
        world.Values["ItemData"] = 55;

        Assert.Empty(baselines.Restore(GuidHash, world.EntityIndex, world.EntityVersion, world));
        Assert.Equal(55, world.Values["ItemData"]);
        Assert.Equal(0, baselines.Count(GuidHash));
    }

    [Fact]
    public void A_throwing_restore_is_reported_and_the_others_still_run()
    {
        var world = new FakeWorld();
        world.Values["Good"] = 1;
        var baselines = new PrefabEditBaselines();
        baselines.Capture(GuidHash, "Bad", world.EntityIndex, world.EntityVersion,
            _ => throw new InvalidOperationException("boom"));
        baselines.Capture(GuidHash, "Good", world.EntityIndex, world.EntityVersion,
            ctx => ((FakeWorld)ctx).Values["Good"] = 1);
        world.Values["Good"] = 9;

        var errors = new List<string>();
        var restored = baselines.Restore(
            GuidHash, world.EntityIndex, world.EntityVersion, world,
            (type, e) => errors.Add(PrefabEditLog.RestoreThrew(type, GuidHash, e)));

        Assert.Equal(new[] { "Good" }, restored);
        Assert.Equal(1, world.Values["Good"]);
        Assert.Single(errors);
        Assert.StartsWith(
            "PrefabRegistry: restore of Bad on guid 42 threw: System.InvalidOperationException: boom",
            errors[0]);
    }

    /// <summary>
    /// The restore-only path logs a throwing callback and forgets the baselines anyway — a
    /// baseline whose restore threw is not retried, which is the branch the hand copy could not
    /// express (R49 finding 2). The prefab is left with whatever that write put there and the
    /// error line is the only trace, so it says which component it was.
    /// </summary>
    [Fact]
    public void A_restore_only_pass_forgets_a_baseline_whose_callback_threw()
    {
        var world = new FakeWorld();
        world.Values["Bad"] = 1;
        world.Values["Good"] = 2;
        world.Throwing.Add("Bad");
        var table = NewTable();
        Add(table, "a", h => { h.Write("Bad", 7); h.Write("Good", 8); }, out var handle);
        Pass(table, world, PrefabEditTrigger.EditorsChanged);
        handle.Dispose();
        table.DrainPendingRemovals();

        var pass = Pass(table, world, PrefabEditTrigger.EditorsChanged);

        Assert.Equal(PrefabEditPassOutcome.Restored, pass.Outcome);
        Assert.Equal(7, world.Values["Bad"]);
        Assert.Equal(2, world.Values["Good"]);
        Assert.Equal(0, table.Baselines.Count(GuidHash));
        Assert.Single(pass.Errors);
        Assert.StartsWith("PrefabRegistry: restore of Bad on guid 42 threw:", pass.Errors[0]);
        Assert.Equal(
            new[] { PrefabEditLog.RestoredAfterLastEditor(GuidHash, new[] { "Good" }) },
            pass.Log);
    }

    [Fact]
    public void Uninstall_restores_every_prefab_and_the_table_clear_forgets_them()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var other = new FakeWorld();
        other.Values["Durability"] = 2;
        var table = NewTable();
        new FakeHandle(world, table.Baselines, GuidHash).Write("ItemData", 7);
        new FakeHandle(other, table.Baselines, 99).Write("Durability", 8);

        // Mirrors PrefabRegistry.RestoreAllBaselines: one bind per guid, then Clear.
        var guids = table.Baselines.Guids();
        Array.Sort(guids);
        int components = 0;
        int prefabs = 0;
        var worlds = new Dictionary<int, FakeWorld> { [GuidHash] = world, [99] = other };
        for (int i = 0; i < guids.Length; i++)
        {
            var w = worlds[guids[i]];
            var restored = table.Baselines.Restore(guids[i], w.EntityIndex, w.EntityVersion, w);
            if (restored.Length == 0) continue;
            components += restored.Length;
            prefabs++;
        }
        table.Clear();

        Assert.Equal(1, world.Values["ItemData"]);
        Assert.Equal(2, other.Values["Durability"]);
        Assert.Equal("PrefabRegistry: restored 2 component(s) on 2 prefab(s) at Core uninstall",
            PrefabEditLog.RestoredAtUninstall(components, prefabs));
        Assert.Empty(table.Baselines.Guids());
    }

    [Fact]
    public void A_throwing_restore_skips_that_component_and_still_restores_the_rest()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 40;
        world.Values["LifeTime"] = 9;
        var baselines = new PrefabEditBaselines();
        Assert.True(baselines.Capture(GuidHash, "ItemData", world.EntityIndex, world.EntityVersion,
            _ => throw new InvalidOperationException("boom")));
        Assert.True(baselines.Capture(GuidHash, "LifeTime", world.EntityIndex, world.EntityVersion,
            ctx => ((FakeWorld)ctx).Values["LifeTime"] = 1));

        var errors = new List<string>();
        string[] restored = baselines.Restore(
            GuidHash, world.EntityIndex, world.EntityVersion, world,
            (type, e) => errors.Add(type + ": " + e.Message));

        Assert.Equal(new[] { "LifeTime" }, restored);
        Assert.Equal(40, world.Values["ItemData"]);
        Assert.Equal(1, world.Values["LifeTime"]);
        Assert.Equal("ItemData: boom", Assert.Single(errors));
        Assert.Equal(2, baselines.Count(GuidHash));
        Assert.Equal(
            "PrefabRegistry: restore incomplete on guid 42: restored 1 of 2 captured baseline(s); a throwing restore was skipped and the remaining values still ran. This prefab is not vanilla.",
            PrefabEditLog.RestoreIncomplete(GuidHash, owed: 2, restored: restored.Length));
    }

    [Fact]
    public void Restore_keeps_the_baseline_for_the_next_pass()
    {
        var world = new FakeWorld();
        world.Values["ItemData"] = 1;
        var baselines = new PrefabEditBaselines();
        new FakeHandle(world, baselines, GuidHash).Write("ItemData", 7);

        Assert.Single(baselines.Restore(GuidHash, world.EntityIndex, world.EntityVersion, world));
        Assert.Equal(1, world.Values["ItemData"]);
        world.Values["ItemData"] = 7;
        Assert.Single(baselines.Restore(GuidHash, world.EntityIndex, world.EntityVersion, world));
        Assert.Equal(1, world.Values["ItemData"]);
    }

    [Fact]
    public void A_removal_notifies_the_scheduling_hook_once_per_removal()
    {
        int calls = 0;
        var table = NewTable();
        table.RemovalScheduled = () => calls++;
        Add(table, "a", h => h.Write("ItemData", 7), out var handle);
        Assert.Equal(0, calls);

        handle.Dispose();
        handle.Dispose();

        Assert.Equal(1, calls);
        Assert.Equal(new[] { GuidHash }, table.DrainPendingRemovals());
        Assert.Empty(table.DrainPendingRemovals());
    }

    [Fact]
    public void Unregister_owner_queues_the_removal()
    {
        var table = NewTable();
        var owner = new object();
        Assert.True(table.TryAdd(
            GuidHash, "a.id", "a", 0, null, null, owner,
            new Action<FakeHandle>(_ => { }), -1, out _, out _));

        Assert.Equal(1, table.UnregisterOwner(owner));

        Assert.Equal(new[] { GuidHash }, table.DrainPendingRemovals());
    }

    /// <summary>
    /// The registry really drives the ladder above rather than keeping a second copy of it, and
    /// the flush really feeds each drained removal's outcome to the retry budget. Text, because
    /// <c>PrefabRegistry.cs</c> binds ProjectM and cannot be compiled here — the decisions it
    /// makes are covered by the tests above, and these three lines are the wiring between them.
    /// </summary>
    [Fact]
    public void The_registry_runs_the_pass_through_the_extracted_ladder()
    {
        string registry = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabRegistry.cs");

        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(registry, "int guidHash, World? world, PrefabEditTrigger trigger, out PrefabEditPassOutcome outcome)"),
            "outcome = PrefabEditPass.Run(trigger, pass);");
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(registry, "private static void OnEditFlushFrame()"),
            "NoteRemovalOutcome(removals[i], outcome);");
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(registry, "private static void NoteRemovalOutcome(int guidHash, PrefabEditPassOutcome outcome)"),
            "Edits.RequeueRemoval(guidHash);");
        SourcePin.AssertStatementExactly(
            SourcePin.MethodBody(registry, "private static bool RestoreEditsOnly(World world, int guidHash)"),
            "if (handle == null) return false;");
    }

    /// <summary>
    /// The write site runs the same capture rule the tests above drive, and the two capture
    /// paths stay asymmetric on purpose: the <c>GetComponentData</c> path's restore re-syncs
    /// <c>ItemHashLookupMap</c> and re-forces <c>ItemTypeGUID</c>, exactly as a write does, and
    /// the <c>unmanaged</c> path does not. R49 finding 2 named that asymmetry as the one thing
    /// the old hand-written double could not model; it still cannot be executed here (both sides
    /// are ECS generics), so it is pinned instead of being quietly untested.
    /// </summary>
    [Fact]
    public void The_write_site_captures_through_the_shared_rule_and_the_item_data_restore_re_syncs()
    {
        string handle = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabEditHandle.cs");
        string plain = SourcePin.MethodBody(handle, "private void CaptureBaseline<T>(string typeName) where T : unmanaged");
        string managed = SourcePin.MethodBody(handle, "private void CaptureManagedBaseline<T>(string typeName)");

        SourcePin.AssertUncommented(plain, "PrefabEditPass.TryCapture(");
        SourcePin.AssertUncommented(managed, "PrefabEditPass.TryCapture(");
        SourcePin.AssertStatementExactly(plain, "entity.Write(world.EntityManager, in original);");
        SourcePin.AssertStatementExactly(managed, "world.EntityManager.SetComponentData(entity, original);");
        SourcePin.AssertStatementExactly(managed, "SyncItemData(world, entity, guid, typeName, out _);");
    }

    private static PrefabEditTable NewTable() => new();

    private static void Add(
        PrefabEditTable table,
        string name,
        Action<FakeHandle> editor,
        out IDisposable handle)
    {
        Assert.True(table.TryAdd(
            GuidHash,
            name + ".id",
            name,
            (int)PrefabEditPriority.Default,
            null,
            null,
            owner: new object(),
            editor,
            loadOrder: -1,
            out handle,
            out var refuse));
        Assert.Null(refuse);
    }

    /// <summary>
    /// One edit pass through the production ladder. Returns the pass so the test can read the
    /// outcome it reported and the lines it logged.
    /// </summary>
    private static FakePass Pass(
        PrefabEditTable table,
        FakeWorld world,
        PrefabEditTrigger trigger,
        bool worldAlive = true,
        bool bindFails = false)
    {
        var pass = new FakePass(table, world, trigger)
        {
            WorldAlive = worldAlive,
            BindFails = bindFails,
        };
        pass.Outcome = PrefabEditPass.Run(trigger, pass);
        return pass;
    }

    /// <summary>Component bag standing in for one prefab entity in one world.</summary>
    private sealed class FakeWorld
    {
        public readonly Dictionary<string, int> Values = new(StringComparer.Ordinal);

        /// <summary>Components whose restore callback throws, for the error paths.</summary>
        public readonly HashSet<string> Throwing = new(StringComparer.Ordinal);

        public int EntityIndex = 7;
        public int EntityVersion = 1;
    }

    /// <summary>
    /// The game-typed half of <see cref="IPrefabEditPass"/> against a component bag: what
    /// <c>PrefabRegistry.RegistryEditPass</c> does with a <c>World</c>, an <c>EntityManager</c>
    /// and a <c>PrefabEditHandle</c>. Every decision above these methods is the shipped one.
    /// </summary>
    private sealed class FakePass : IPrefabEditPass
    {
        private readonly PrefabEditTable _table;
        private readonly FakeWorld _world;
        private readonly PrefabEditTrigger _trigger;
        private PrefabEditRegistration[] _ordered = Array.Empty<PrefabEditRegistration>();
        private FakeHandle? _handle;

        public FakePass(PrefabEditTable table, FakeWorld world, PrefabEditTrigger trigger)
        {
            _table = table;
            _world = world;
            _trigger = trigger;
        }

        public bool WorldAlive { get; set; } = true;

        /// <summary>Stands in for a collection that does not have the entity yet.</summary>
        public bool BindFails { get; set; }

        public PrefabEditPassOutcome Outcome { get; set; }

        public bool Again { get; private set; }

        public List<string> Log { get; } = new();

        public List<string> Errors { get; } = new();

        public bool HasEditors => _table.HasGuid(GuidHash);

        public int BaselineCount => _table.Baselines.Count(GuidHash);

        public bool RestoreOnly()
        {
            if (BindFails) return false;
            var restored = Restore();
            _table.Baselines.Drop(GuidHash);
            if (restored.Length > 0)
                Log.Add(PrefabEditLog.RestoredAfterLastEditor(GuidHash, restored));
            return true;
        }

        public bool TryBeginRun() => _table.TryBeginRun(GuidHash);

        public void EndRun() => Again = _table.EndRun(GuidHash);

        public bool TrySnapshot(out int orderedCount)
        {
            if (!_table.TrySnapshotOrdered(GuidHash, out _ordered, out _))
            {
                _ordered = Array.Empty<PrefabEditRegistration>();
                orderedCount = 0;
                return false;
            }
            orderedCount = _ordered.Length;
            return true;
        }

        public bool ShouldRun() => _table.ShouldRun(GuidHash, _trigger, _ordered);

        public bool TryBind()
        {
            if (BindFails) return false;
            _handle = new FakeHandle(_world, _table.Baselines, GuidHash);
            return true;
        }

        public void DropBaselines() => _table.Baselines.Drop(GuidHash);

        public void RestoreBeforeRerun()
        {
            var restored = Restore();
            if (restored.Length > 0 && _trigger == PrefabEditTrigger.EditorsChanged)
                Log.Add(PrefabEditLog.RestoredBeforeRerun(GuidHash, restored.Length, _ordered.Length));
        }

        public void RunEditors()
        {
            var handle = _handle!;
            var records = PrefabEditApply.Run(
                _ordered,
                reg =>
                {
                    handle.Begin();
                    ((Action<FakeHandle>)reg.Editor)(handle);
                    return handle.Touched;
                },
                _ => { },
                (_, _) => { });
            _table.RememberRun(GuidHash, _ordered, records);
        }

        private string[] Restore()
        {
            return _table.Baselines.Restore(
                GuidHash, _world.EntityIndex, _world.EntityVersion, _world,
                (typeName, e) => Errors.Add(PrefabEditLog.RestoreThrew(typeName, GuidHash, e)));
        }
    }

    /// <summary>
    /// What <c>PrefabEditHandle</c> does on the tracked write surface, through the production
    /// capture rule: pre-check, read the pre-write value, capture it, then write. The restore
    /// callback closes over the value and the entity identity, never over the world.
    /// </summary>
    private sealed class FakeHandle
    {
        private readonly FakeWorld _world;
        private readonly PrefabEditBaselines _baselines;
        private readonly int _guidHash;
        private readonly List<string> _touched = new();

        public FakeHandle(FakeWorld world, PrefabEditBaselines baselines, int guidHash)
        {
            _world = world;
            _baselines = baselines;
            _guidHash = guidHash;
        }

        public IReadOnlyList<string> Touched => _touched;

        public void Begin() => _touched.Clear();

        public int Read(string component) => _world.Values[component];

        public void Write(string component, int value)
        {
            if (PrefabEditPass.NeedsCapture(_baselines, _guidHash, component))
            {
                bool present = _world.Values.TryGetValue(component, out int original);
                bool throws = _world.Throwing.Contains(component);
                PrefabEditPass.TryCapture(
                    _baselines, _guidHash, component, present,
                    _world.EntityIndex, _world.EntityVersion,
                    ctx =>
                    {
                        if (throws) throw new InvalidOperationException("boom");
                        ((FakeWorld)ctx).Values[component] = original;
                    });
            }
            _world.Values[component] = value;
            if (!_touched.Contains(component)) _touched.Add(component);
        }
    }
}
