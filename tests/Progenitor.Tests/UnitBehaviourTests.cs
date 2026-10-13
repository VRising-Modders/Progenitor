using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Events;
using Progenitor.Units;
using Xunit;
using UnitsApi = Progenitor.Units.Units;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the behavior-step sequencer (job FUNIT1): step ordering, the
/// completion rule per kind, what a failure does to the rest of the sequence, and Stop.
/// <para>
/// A fake driver and a fake clock stand in for the unit, so every decision this module
/// makes is checked on a machine with no V Rising on it — the live half
/// (<c>UnityUnitBehaviour.cs</c>) is only the writes.
/// </para>
/// <para>
/// This is a separate file from <c>UnitsTests.cs</c> on purpose: that file carries an
/// earlier session's uncommitted edits, and its <c>FakeUnitRuntime</c> implements
/// <see cref="IUnitRuntime"/> only. Behavior support is a second interface
/// (<c>IUnitBehaviourRuntime</c>) precisely so the older fake keeps compiling.
/// </para>
/// <para>
/// R56 finding 1's two cases run against the real <see cref="UnitHandle"/> and the real
/// <c>EventBus</c>, which is process-wide state the other Units test classes also publish on,
/// so this class joins their collection rather than racing them.
/// </para>
/// </summary>
[Collection("PluginLogger")]
public class UnitBehaviourTests
{
    // ---- ordering and completion -------------------------------------------------------------

    [Fact]
    public void Steps_that_finish_on_the_tick_that_started_them_run_straight_through()
    {
        var driver = new FakeDriver();
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);

        seq.Play(
            UnitStep.Idle(),
            UnitStep.Face(new UnitId(9, 1)),
            UnitStep.Face(new UnitVec3(1f, 0f, 2f)),
            UnitStep.Follow(new UnitId(9, 1)));

        // Nothing waits, so Play alone drains the whole sequence.
        Assert.False(seq.IsPlaying);
        Assert.Equal(new[] { "idle", "face-entity 9:1", "face-position (1, 0, 2)", "follow 9:1" }, driver.Calls);
        Assert.Equal(4, driver.Steps.Count);
        Assert.All(driver.Steps, s => Assert.Equal(UnitStepOutcome.Completed, s.Outcome));
        Assert.Equal(new[] { 0, 1, 2, 3 }, driver.Steps.ConvertAll(s => s.Index));
        Assert.Equal(UnitSequenceOutcome.Completed, driver.Sequence!.Value.Outcome);
        Assert.Equal(4, driver.Sequence!.Value.Completed);
    }

    [Fact]
    public void A_goto_holds_the_sequence_until_the_handle_says_it_arrived()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);

        seq.Play(UnitStep.GoTo(new UnitVec3(6f, 0f, 0f)), UnitStep.Idle());

        Assert.True(seq.IsPlaying);
        Assert.Equal(0, seq.StepIndex);
        // Play forces Idle once before step 1 (R55 finding 2), then the GoTo write.
        Assert.Equal(new[] { "idle", "goto (6, 0, 0)" }, driver.Calls);
        Assert.Empty(driver.Steps);

        seq.NotifyArrived(driver.Armed);

        Assert.False(seq.IsPlaying);
        Assert.Equal(new[] { "idle", "goto (6, 0, 0)", "idle" }, driver.Calls);
        Assert.Equal(UnitSequenceOutcome.Completed, driver.Sequence!.Value.Outcome);
    }

    [Fact]
    public void An_unreachable_goal_fails_the_step_and_stops_the_sequence()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);

        seq.Play(UnitStep.GoTo(new UnitVec3(6f, 0f, 0f)), UnitStep.Idle());
        seq.NotifyUnreachable(driver.Armed, "entity gone");

        Assert.False(seq.IsPlaying);
        Assert.Single(driver.Steps);
        Assert.Equal(UnitStepOutcome.Failed, driver.Steps[0].Outcome);
        Assert.Equal("entity gone", driver.Steps[0].Reason);
        Assert.Equal(UnitSequenceOutcome.Failed, driver.Sequence!.Value.Outcome);
        Assert.Equal("entity gone", driver.Sequence!.Value.Reason);
        // The Idle after it never ran; the one before the GoTo is the forced state.
        Assert.Equal(new[] { "idle", "goto (6, 0, 0)" }, driver.Calls);
        Assert.Equal(0, driver.Sequence!.Value.Completed);
    }

    [Fact]
    public void A_refused_write_inside_the_step_start_is_a_failure_not_a_reentrant_advance()
    {
        // The live handle raises Unreachable synchronously from inside GoTo when the write is
        // refused. Without the sequencer's park that notify would re-enter Advance underneath
        // itself and start step 2 while step 1 was still being started.
        var driver = new FakeDriver { RefuseGoTo = true };
        var seq = new UnitSequencer(driver, new FakeClock().Now);

        seq.Play(UnitStep.GoTo(new UnitVec3(6f, 0f, 0f)), UnitStep.Idle());

        Assert.False(seq.IsPlaying);
        Assert.Equal(new[] { "idle", "goto (6, 0, 0)" }, driver.Calls);
        Assert.Single(driver.Steps);
        Assert.Equal(UnitStepOutcome.Failed, driver.Steps[0].Outcome);
        Assert.Equal("goto refused", driver.Steps[0].Reason);
        Assert.Equal(UnitSequenceOutcome.Failed, driver.Sequence!.Value.Outcome);
    }

    [Fact]
    public void A_wait_is_a_module_timer_and_advances_only_when_the_clock_passes_it()
    {
        var driver = new FakeDriver();
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);

        seq.Play(UnitStep.Wait(2f), UnitStep.Idle());
        Assert.True(seq.IsPlaying);

        clock.Seconds = 1.9;
        seq.Tick();
        Assert.True(seq.IsPlaying);
        // Only the forced Idle so far: the Wait has not expired.
        Assert.Equal(new[] { "idle" }, driver.Calls);

        clock.Seconds = 2.0;
        seq.Tick();
        Assert.False(seq.IsPlaying);
        Assert.Equal(new[] { "idle", "idle" }, driver.Calls);
        Assert.Equal(UnitStepOutcome.Completed, driver.Steps[0].Outcome);
        Assert.Equal(UnitStepKind.Wait, driver.Steps[0].Kind);
    }

    [Fact]
    public void A_zero_second_wait_completes_without_a_tick()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);
        seq.Play(UnitStep.Wait(0f), UnitStep.Idle());
        Assert.False(seq.IsPlaying);
        Assert.Equal(2, driver.Steps.Count);
    }

    [Fact]
    public void A_negative_or_nonfinite_wait_is_clamped_rather_than_hanging_the_sequence()
    {
        Assert.Equal(0f, UnitStep.Wait(-5f).Seconds);
        Assert.Equal(0f, UnitStep.Wait(float.NaN).Seconds);
        Assert.Equal(0f, UnitStep.Wait(float.PositiveInfinity).Seconds);
    }

    [Fact]
    public void Then_runs_the_callback_between_two_steps_and_a_throwing_one_fails_the_sequence()
    {
        var driver = new FakeDriver();
        int ran = 0;
        var seq = new UnitSequencer(driver, new FakeClock().Now);
        seq.Play(UnitStep.Then(() => ran++), UnitStep.Idle());
        Assert.Equal(1, ran);
        Assert.Equal(UnitSequenceOutcome.Completed, driver.Sequence!.Value.Outcome);

        var driver2 = new FakeDriver();
        var seq2 = new UnitSequencer(driver2, new FakeClock().Now);
        seq2.Play(UnitStep.Then(() => throw new InvalidOperationException("boom")), UnitStep.Idle());
        Assert.Equal(UnitSequenceOutcome.Failed, driver2.Sequence!.Value.Outcome);
        Assert.Contains("boom", driver2.Sequence!.Value.Reason, StringComparison.Ordinal);
        // Only the forced Idle: the Idle step after the throwing Then never ran.
        Assert.Equal(new[] { "idle" }, driver2.Calls);
    }

    // ---- Animate: the one refusal that does not stop the sequence -----------------------------

    [Fact]
    public void A_refused_animation_is_skipped_holds_its_duration_and_the_sequence_carries_on()
    {
        // Animate is cosmetic and, on this build, has no server-side route at all
        // (AnimationStateBuffer is a Client-world buffer). If a refusal stopped the sequence,
        // one decorative step would strand every patrol that used it.
        var driver = new FakeDriver { RefuseAnimate = true };
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);

        seq.Play(UnitStep.Animate("Idle", 1.5f), UnitStep.Idle());

        Assert.True(seq.IsPlaying);
        clock.Seconds = 1.4;
        seq.Tick();
        Assert.True(seq.IsPlaying);

        clock.Seconds = 1.5;
        seq.Tick();
        Assert.False(seq.IsPlaying);
        Assert.Equal(UnitStepOutcome.Skipped, driver.Steps[0].Outcome);
        Assert.Equal("animation refused", driver.Steps[0].Reason);
        Assert.Equal(UnitSequenceOutcome.Completed, driver.Sequence!.Value.Outcome);
        // A skipped step still counts as run: the sequence did everything it was asked to.
        Assert.Equal(2, driver.Sequence!.Value.Completed);
    }

    [Fact]
    public void An_accepted_animation_completes_after_its_duration()
    {
        var driver = new FakeDriver();
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);
        seq.Play(UnitStep.Animate("Idle", 1f));
        Assert.Equal(new[] { "idle", "animate Idle 1" }, driver.Calls);
        clock.Seconds = 1.0;
        seq.Tick();
        Assert.Equal(UnitStepOutcome.Completed, driver.Steps[0].Outcome);
    }

    [Fact]
    public void An_animation_without_a_clip_name_is_refused_at_construction()
    {
        Assert.Throws<ArgumentException>(() => UnitStep.Animate("  "));
        Assert.Equal(UnitStep.DefaultAnimationSeconds, UnitStep.Animate("Idle").Seconds);
    }

    // ---- Stop, replace, teardown --------------------------------------------------------------

    [Fact]
    public void Stop_reports_the_sequence_stopped_and_a_second_Stop_is_a_no_op()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);
        seq.Play(UnitStep.GoTo(new UnitVec3(6f, 0f, 0f)), UnitStep.Idle());

        seq.Stop();
        Assert.False(seq.IsPlaying);
        Assert.Equal(-1, seq.StepIndex);
        Assert.Equal(UnitSequenceOutcome.Stopped, driver.Sequence!.Value.Outcome);

        driver.Sequence = null;
        seq.Stop();
        Assert.Null(driver.Sequence);
    }

    [Fact]
    public void Teardown_stops_silently_because_the_handle_is_already_going_away()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);
        seq.Play(UnitStep.GoTo(new UnitVec3(6f, 0f, 0f)));

        seq.StopSilently();

        Assert.False(seq.IsPlaying);
        Assert.Null(driver.Sequence);
        Assert.Contains(driver.Lines, l => l.Contains("sequence end Stopped", StringComparison.Ordinal));
    }

    [Fact]
    public void A_second_Play_reports_the_first_stopped_before_anything_of_the_second_runs()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);
        seq.Play(UnitStep.GoTo(new UnitVec3(6f, 0f, 0f)));
        Assert.True(seq.IsPlaying);

        seq.Play(UnitStep.Idle());

        Assert.Equal(2, driver.SequenceHistory.Count);
        Assert.Equal(UnitSequenceOutcome.Stopped, driver.SequenceHistory[0].Outcome);
        Assert.Equal("replaced by a new sequence", driver.SequenceHistory[0].Reason);
        Assert.Equal(UnitSequenceOutcome.Completed, driver.SequenceHistory[1].Outcome);
    }

    [Fact]
    public void Play_with_nothing_only_stops_what_was_running()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);
        seq.Play(UnitStep.GoTo(new UnitVec3(6f, 0f, 0f)));
        seq.Play();
        Assert.False(seq.IsPlaying);
        Assert.Equal(UnitSequenceOutcome.Stopped, driver.Sequence!.Value.Outcome);

        driver.Sequence = null;
        seq.Play(Array.Empty<UnitStep>());
        Assert.Null(driver.Sequence);
    }

    [Fact]
    public void A_null_step_in_the_array_is_rejected_before_the_old_sequence_is_replaced_by_a_broken_one()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);
        Assert.Throws<ArgumentException>(() => seq.Play(UnitStep.Idle(), null!));
        Assert.False(seq.IsPlaying);
        Assert.Empty(driver.Calls);
    }

    [Fact]
    public void A_dead_unit_refuses_a_new_sequence_and_ends_a_running_one()
    {
        var driver = new FakeDriver { Alive = false };
        var seq = new UnitSequencer(driver, new FakeClock().Now);
        seq.Play(UnitStep.Idle());
        Assert.False(seq.IsPlaying);
        Assert.Empty(driver.Calls);
        Assert.Contains(driver.Lines, l => l.Contains("sequence ignored", StringComparison.Ordinal));

        var live = new FakeDriver();
        var seq2 = new UnitSequencer(live, new FakeClock().Now);
        seq2.Play(UnitStep.GoTo(new UnitVec3(1f, 0f, 0f)));
        live.Alive = false;
        seq2.Tick();
        Assert.False(seq2.IsPlaying);
        Assert.Equal(UnitSequenceOutcome.Failed, live.Sequence!.Value.Outcome);
        Assert.Equal("unit gone", live.Sequence!.Value.Reason);
    }

    [Fact]
    public void An_arrival_nobody_is_waiting_for_is_ignored()
    {
        // Follow completes as soon as the write sticks, so the handle's later 3 m Arrived is a
        // plain event and must not be read as the boundary of some other step.
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);
        seq.Play(UnitStep.Follow(new UnitId(9, 1)));
        Assert.False(seq.IsPlaying);

        // Follow arms no wait, so the handle's ticket for this walk is None.
        seq.NotifyArrived(driver.Armed);
        seq.NotifyUnreachable(driver.Armed, "follow target gone");
        Assert.Single(driver.SequenceHistory);
    }

    [Fact]
    public void A_refused_follow_or_face_or_idle_fails_the_sequence()
    {
        foreach (var (refuse, step, expected) in new (Action<FakeDriver>, UnitStep, string)[]
                 {
                     (d => d.RefuseFollow = true, UnitStep.Follow(new UnitId(9, 1)), "follow refused"),
                     (d => d.RefuseFace = true, UnitStep.Face(new UnitId(9, 1)), "face refused"),
                     (d => d.RefuseIdle = true, UnitStep.Idle(), "idle refused"),
                 })
        {
            var driver = new FakeDriver();
            refuse(driver);
            var seq = new UnitSequencer(driver, new FakeClock().Now);
            seq.Play(step, UnitStep.Wait(5f));
            Assert.False(seq.IsPlaying);
            Assert.Equal(UnitSequenceOutcome.Failed, driver.Sequence!.Value.Outcome);
            Assert.Equal(expected, driver.Sequence!.Value.Reason);
        }
    }

    [Fact]
    public void A_driver_that_throws_fails_the_step_rather_than_escaping_into_the_frame_tick()
    {
        var driver = new FakeDriver { ThrowOnIdle = true };
        var seq = new UnitSequencer(driver, new FakeClock().Now);
        seq.Play(UnitStep.Idle());
        Assert.False(seq.IsPlaying);
        Assert.Equal(UnitSequenceOutcome.Failed, driver.Sequence!.Value.Outcome);
        Assert.Contains("step threw", driver.Sequence!.Value.Reason, StringComparison.Ordinal);
    }

    // ---- logging and surface ------------------------------------------------------------------

    [Fact]
    public void One_line_per_step_start_and_one_per_sequence_end()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);
        seq.Play(UnitStep.Idle(), UnitStep.Face(new UnitVec3(1f, 0f, 0f)));

        Assert.Equal(
            new[]
            {
                "Units: sequence start steps=2",
                "Units: step 1/2 Idle",
                "Units: step 2/2 Face (1, 0, 0)",
                "Units: sequence end Completed 2/2",
            },
            driver.Lines);
    }

    [Fact]
    public void StepIndex_and_StepCount_describe_the_running_sequence()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);
        Assert.Equal(-1, seq.StepIndex);
        Assert.Equal(0, seq.StepCount);

        seq.Play(UnitStep.GoTo(new UnitVec3(1f, 0f, 0f)), UnitStep.Idle());
        Assert.Equal(0, seq.StepIndex);
        Assert.Equal(2, seq.StepCount);
    }

    [Fact]
    public void Every_step_kind_prints_something_a_log_reader_can_use()
    {
        Assert.Equal("Idle", UnitStep.Idle().ToString());
        Assert.Equal("GoTo (1, 2, 3)", UnitStep.GoTo(new UnitVec3(1f, 2f, 3f)).ToString());
        Assert.Equal("Follow 4:1", UnitStep.Follow(new UnitId(4, 1)).ToString());
        Assert.Equal("Face 4:1", UnitStep.Face(new UnitId(4, 1)).ToString());
        Assert.Equal("Face (1, 0, 0)", UnitStep.Face(new UnitVec3(1f, 0f, 0f)).ToString());
        Assert.Equal("Wait 2s", UnitStep.Wait(2f).ToString());
        Assert.Equal("Animate 'Idle' 1.5s", UnitStep.Animate("Idle", 1.5f).ToString());
        Assert.Equal("Then", UnitStep.Then(() => { }).ToString());
    }

    // ---- the rules this job is a defense against ----------------------------------------------

    [Fact]
    public void The_module_never_compiles_or_edits_a_behaviour_tree_blob()
    {
        // The v1 rule from the research: select a vanilla tree, force Idle, drive the steps.
        // A BehaviourTreeBlob / BehaviourTreeNode write appearing in the Units module means
        // somebody started authoring trees at runtime, which the stubs do not support.
        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core", "Units"), "*.cs"))
        {
            string text = File.ReadAllText(file);
            foreach (string line in text.Split('\n'))
            {
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    || line.TrimStart().StartsWith("///", StringComparison.Ordinal)
                    || line.TrimStart().StartsWith("*", StringComparison.Ordinal))
                    continue;
                Assert.DoesNotContain("BehaviourTreeBlob", line, StringComparison.Ordinal);
                Assert.DoesNotContain("BehaviourTreeNode", line, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_live_runtime_still_refuses_Animate_with_the_reason_the_metadata_gives()
    {
        // A source pin, not a behavior test: UnityUnitBehaviour.cs is not compiled here.
        // If somebody makes Animate return true they have to delete this citation, which is
        // the moment to check whether a server-side route actually appeared.
        string text = File.ReadAllText(Path.Combine(
            SourcePin.RepoRoot(), "src", "Progenitor.Core", "Units", "UnityUnitBehaviour.cs"));
        Assert.Contains("AnimationStateBuffer is a Client-world buffer", text, StringComparison.Ordinal);
        Assert.Contains("ComponentInWorld{Client}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Tree_selection_is_refused_once_the_spawn_frame_is_over()
    {
        // Same reason: BehaviourTreeBindingSystem_Spawn is in ReactToSpawnGroup, so re-binding
        // a live unit would mean re-adding SpawnTag and re-running every ReactToSpawn system.
        string text = File.ReadAllText(Path.Combine(
            SourcePin.RepoRoot(), "src", "Progenitor.Core", "Units", "UnityUnitBehaviour.cs"));
        Assert.Contains("ReactToSpawnGroup", text, StringComparison.Ordinal);
        Assert.Contains("spawnTag=False", text, StringComparison.Ordinal);
        Assert.Contains("has no BehaviourTree component; refusing to bind it as a tree.", text, StringComparison.Ordinal);
    }

    // ---- R55 finding 1: a Play or Stop from inside the sequencer is deferred -------------------

    [Fact]
    public void Play_from_a_Then_callback_is_deferred_until_the_tick_unwinds()
    {
        // Measured in R55: the old inline path published the new sequence's still-running GoTo
        // as Completed and walked on to Follow while the unit was mid-walk, because _playing
        // was read as sequence identity. Chaining a patrol leg from a Then is the obvious use
        // of Then, so the fix is a deferral, not a refusal.
        var driver = new FakeDriver();
        UnitSequencer? seq = null;
        seq = new UnitSequencer(driver, new FakeClock().Now);

        seq.Play(
            UnitStep.Idle(),
            UnitStep.Then(() => seq!.Play(UnitStep.GoTo(new UnitVec3(1f, 0f, 0f)), UnitStep.Follow(new UnitId(7, 1)))),
            UnitStep.Idle());

        // The outer sequence stopped where the callback asked it to: its third step never ran.
        Assert.Equal(UnitSequenceOutcome.Stopped, driver.SequenceHistory[0].Outcome);
        Assert.Equal("replaced by a new sequence", driver.SequenceHistory[0].Reason);
        Assert.Equal(2, driver.SequenceHistory[0].Completed);

        // The new sequence's first step ran exactly once, is still running, and was not
        // reported: nothing has arrived yet.
        Assert.True(seq.IsPlaying);
        Assert.Equal(0, seq.StepIndex);
        Assert.Equal(2, seq.StepCount);
        Assert.Equal(new[] { "idle", "idle", "goto (1, 0, 0)" }, driver.Calls);
        Assert.DoesNotContain(driver.Steps, s => s.Kind == UnitStepKind.GoTo);
        Assert.DoesNotContain(driver.Calls, c => c.StartsWith("follow", StringComparison.Ordinal));

        // And it completes once, on the arrival, the way any first step would.
        seq.NotifyArrived(driver.Armed);
        Assert.Single(driver.Steps, s => s.Kind == UnitStepKind.GoTo && s.Outcome == UnitStepOutcome.Completed);
        Assert.Equal(1, driver.Steps.FindAll(s => s.Kind == UnitStepKind.GoTo).Count);
    }

    [Fact]
    public void Play_from_a_StepCompleted_subscriber_during_Tick_does_not_skip_the_new_sequences_first_step()
    {
        // The other measured route: Tick re-read _playing after publishing the step event, saw
        // a different sequence's true, and called Advance, so the new sequence started at index
        // 1 and its step 0 was never run or reported.
        var driver = new FakeDriver();
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);
        seq.Play(UnitStep.Wait(1f), UnitStep.Idle());

        driver.OnStepCompleted = () =>
        {
            driver.OnStepCompleted = null;
            seq.Play(UnitStep.GoTo(new UnitVec3(5f, 0f, 0f)), UnitStep.Follow(new UnitId(3, 1)));
        };

        clock.Seconds = 1.0;
        seq.Tick();

        Assert.True(seq.IsPlaying);
        Assert.Equal(0, seq.StepIndex);
        Assert.Equal(new[] { "idle", "idle", "goto (5, 0, 0)" }, driver.Calls);
        Assert.DoesNotContain(driver.Calls, c => c.StartsWith("follow", StringComparison.Ordinal));
        Assert.Equal(UnitSequenceOutcome.Stopped, driver.SequenceHistory[0].Outcome);

        seq.NotifyArrived(driver.Armed);
        Assert.Equal(1, driver.Steps.FindAll(s => s.Kind == UnitStepKind.GoTo).Count);
        Assert.Equal(UnitStepOutcome.Completed, driver.Steps.Find(s => s.Kind == UnitStepKind.GoTo).Outcome);
    }

    [Fact]
    public void Play_from_a_SequenceCompleted_subscriber_runs_the_new_sequence_after_the_old_one_has_ended()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);
        driver.OnSequenceCompleted = () =>
        {
            driver.OnSequenceCompleted = null;
            seq.Play(UnitStep.GoTo(new UnitVec3(2f, 0f, 0f)));
        };

        seq.Play(UnitStep.Idle());

        Assert.True(seq.IsPlaying);
        Assert.Equal(UnitSequenceOutcome.Completed, driver.SequenceHistory[0].Outcome);
        Assert.Equal(new[] { "idle", "idle", "goto (2, 0, 0)" }, driver.Calls);
    }

    [Fact]
    public void Stop_from_inside_a_callback_is_deferred_and_applied_once()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);

        seq.Play(
            UnitStep.Then(() => { seq.Stop(); seq.Stop(); }),
            UnitStep.Follow(new UnitId(2, 1)));

        Assert.False(seq.IsPlaying);
        // Two Stops, one end: the second finds nothing playing.
        Assert.Single(driver.SequenceHistory);
        Assert.Equal(UnitSequenceOutcome.Stopped, driver.SequenceHistory[0].Outcome);
        Assert.DoesNotContain(driver.Calls, c => c.StartsWith("follow", StringComparison.Ordinal));
        Assert.Equal(0, seq.PendingCount);
    }

    [Fact]
    public void Two_deferred_plays_are_applied_in_order_and_the_last_one_wins()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);

        seq.Play(UnitStep.Then(() =>
        {
            seq.Play(UnitStep.Face(new UnitVec3(1f, 0f, 0f)), UnitStep.Wait(9f));
            seq.Play(UnitStep.Follow(new UnitId(4, 1)));
        }));

        Assert.Equal(0, seq.PendingCount);
        Assert.False(seq.IsPlaying);
        // Both ran, in order: the Face sequence started and was then replaced by the Follow.
        Assert.Equal(new[] { "idle", "idle", "face-position (1, 0, 0)", "idle", "follow 4:1" }, driver.Calls);
        // The outer sequence's only step was also its last, so it ends Completed even though
        // that step queued two Plays (R56 finding 2). Before the reorder it read Stopped.
        Assert.Equal(
            new[] { UnitSequenceOutcome.Completed, UnitSequenceOutcome.Stopped, UnitSequenceOutcome.Completed },
            driver.SequenceHistory.ConvertAll(s => s.Outcome));
    }

    [Fact]
    public void A_null_step_in_a_deferred_Play_throws_at_the_callers_call_site()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);

        seq.Play(UnitStep.Then(() =>
            Assert.Throws<ArgumentException>(() => seq.Play(UnitStep.Idle(), null!))));

        Assert.Equal(0, seq.PendingCount);
        Assert.False(seq.IsPlaying);
    }

    // ---- R55 finding 2: the sequencer really does force Idle ----------------------------------

    [Fact]
    public void Play_forces_Idle_exactly_once_before_the_first_step()
    {
        // "The sequence forces GenericEnemyState.Idle so that tree neither wanders nor fights"
        // was stated in six places and implemented in none; the sample's first step was Face.
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);

        seq.Play(UnitStep.Face(new UnitId(9, 1)), UnitStep.Wait(1f));

        Assert.Equal("idle", driver.Calls[0]);
        Assert.Equal(new[] { "idle", "face-entity 9:1" }, driver.Calls);
        // Not a step: the caller's indices and counts are unchanged.
        Assert.Equal(2, seq.StepCount);
        Assert.Equal(new[] { 0 }, driver.Steps.ConvertAll(s => s.Index));
        Assert.Contains(driver.Lines, l => l == "Units: sequence forced Idle before step 1");
    }

    [Fact]
    public void A_sequence_that_starts_with_an_Idle_step_does_not_post_it_twice()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);
        seq.Play(UnitStep.Idle(), UnitStep.Wait(1f));
        Assert.Equal(new[] { "idle" }, driver.Calls);
        Assert.DoesNotContain(driver.Lines, l => l == "Units: sequence forced Idle before step 1");
    }

    [Fact]
    public void The_force_is_not_repeated_between_steps_because_Idle_off_leash_becomes_Return()
    {
        // unit-movement.md §20, verified live: an Idle request on a unit away from its
        // SpawnTransform puts the tree in Return (64) and walks it home. Re-posting Idle before
        // every Face or Wait would move the unit the module just placed.
        var driver = new FakeDriver();
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);

        seq.Play(UnitStep.Wait(1f), UnitStep.Face(new UnitVec3(1f, 0f, 0f)), UnitStep.Wait(1f));
        clock.Seconds = 1.0;
        seq.Tick();
        clock.Seconds = 2.0;
        seq.Tick();

        Assert.Equal(1, driver.Calls.FindAll(c => c == "idle").Count);
    }

    [Fact]
    public void A_refused_Idle_force_says_so_and_still_runs_the_sequence()
    {
        var driver = new FakeDriver { RefuseIdle = true };
        var seq = new UnitSequencer(driver, new FakeClock().Now);

        seq.Play(UnitStep.Follow(new UnitId(9, 1)));

        Assert.Equal(UnitSequenceOutcome.Completed, driver.Sequence!.Value.Outcome);
        Assert.Contains(driver.Lines, l => l.Contains("could not force Idle", StringComparison.Ordinal));
        Assert.Contains(driver.Lines, l => l.Contains("may wander or fight", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dead_unit_is_not_asked_to_force_Idle()
    {
        var driver = new FakeDriver { Alive = false };
        var seq = new UnitSequencer(driver, new FakeClock().Now);
        seq.Play(UnitStep.Face(new UnitVec3(1f, 0f, 0f)));
        Assert.Empty(driver.Calls);
    }

    // ---- R55 finding 3: a GoTo that never arrives ----------------------------------------------

    [Fact]
    public void A_goto_whose_unit_never_moves_times_out_and_fails_the_sequence()
    {
        // The documented gate: a unit does not path with no connected player in range
        // (evidence §22), and that produces neither Arrived nor Unreachable — so before this
        // the sequence waited for the rest of the session with no event and no log line.
        var driver = new FakeDriver();
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);

        seq.Play(UnitStep.GoTo(new UnitVec3(6f, 0f, 0f)), UnitStep.Follow(new UnitId(2, 1)));

        clock.Seconds = UnitStep.DefaultGoToTimeoutSeconds - 0.1;
        seq.Tick();
        Assert.True(seq.IsPlaying);

        clock.Seconds = UnitStep.DefaultGoToTimeoutSeconds;
        seq.Tick();

        Assert.False(seq.IsPlaying);
        Assert.Equal(UnitStepOutcome.TimedOut, driver.Steps[0].Outcome);
        Assert.Equal("did not arrive within 30s", driver.Steps[0].Reason);
        Assert.Equal(UnitSequenceOutcome.Failed, driver.Sequence!.Value.Outcome);
        Assert.Equal("did not arrive within 30s", driver.Sequence!.Value.Reason);
        // A timed-out step is not a step that ran.
        Assert.Equal(0, driver.Sequence!.Value.Completed);
        Assert.DoesNotContain(driver.Calls, c => c.StartsWith("follow", StringComparison.Ordinal));
        Assert.Contains(driver.Lines, l =>
            l.Contains("GoTo timed out after 30s", StringComparison.Ordinal)
            && l.Contains("no connected player in range", StringComparison.Ordinal)
            && l.Contains("evidence §22", StringComparison.Ordinal));
    }

    [Fact]
    public void An_arrival_before_the_deadline_wins_and_the_timer_does_not_fire_later()
    {
        var driver = new FakeDriver();
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);

        seq.Play(UnitStep.GoTo(new UnitVec3(6f, 0f, 0f), 5f), UnitStep.Wait(100f));
        clock.Seconds = 1.0;
        seq.NotifyArrived(driver.Armed);

        clock.Seconds = 99.0;
        seq.Tick();
        Assert.True(seq.IsPlaying);
        Assert.Single(driver.Steps);
        Assert.Equal(UnitStepOutcome.Completed, driver.Steps[0].Outcome);
    }

    [Fact]
    public void A_zero_timeout_goto_waits_the_way_it_did_before_and_a_custom_one_is_honoured()
    {
        var driver = new FakeDriver();
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);
        seq.Play(UnitStep.GoTo(new UnitVec3(6f, 0f, 0f), 0f));
        clock.Seconds = 10_000.0;
        seq.Tick();
        Assert.True(seq.IsPlaying);

        var driver2 = new FakeDriver();
        var clock2 = new FakeClock();
        var seq2 = new UnitSequencer(driver2, clock2.Now);
        seq2.Play(UnitStep.GoTo(new UnitVec3(6f, 0f, 0f), 2f));
        clock2.Seconds = 2.0;
        seq2.Tick();
        Assert.False(seq2.IsPlaying);
        Assert.Equal("did not arrive within 2s", driver2.Sequence!.Value.Reason);
    }

    // ---- R57 finding 2: the wait says which way it went nowhere ---------------------------------

    [Fact]
    public void A_timeout_after_a_discarded_notification_names_the_discard_and_not_the_player_gate()
    {
        // FUNIT3 added a second route to this timeout - a notification stamped for another step
        // is discarded, so the wait can only end on the deadline - and it is the likelier route
        // wherever a mod calls GoTo on the handle behind a running sequence. The line printed
        // the other route's diagnosis, thirty seconds and any number of log lines away from the
        // discard that explained it.
        var driver = new FakeDriver();
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);

        seq.Play(UnitStep.GoTo(new UnitVec3(6f, 0f, 0f), 30f), UnitStep.Idle());
        var live = driver.Armed;
        seq.NotifyArrived(new UnitMoveTicket(live.Epoch - 1, live.Step));

        clock.Seconds = 30.0;
        seq.Tick();

        Assert.Contains(driver.Lines, l =>
            l.Contains("GoTo timed out after 30s", StringComparison.Ordinal)
            && l.Contains("was discarded while this one waited", StringComparison.Ordinal)
            && l.Contains("something moved this unit outside the sequencer", StringComparison.Ordinal));
        Assert.DoesNotContain(driver.Lines, l =>
            l.Contains("GoTo timed out", StringComparison.Ordinal)
            && l.Contains("no connected player in range", StringComparison.Ordinal));
    }

    [Fact]
    public void A_wait_with_no_discard_still_names_the_player_in_range_gate()
    {
        var driver = new FakeDriver();
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);

        seq.Play(UnitStep.GoTo(new UnitVec3(6f, 0f, 0f), 30f));
        clock.Seconds = 30.0;
        seq.Tick();

        Assert.Contains(driver.Lines, l =>
            l.Contains("GoTo timed out after 30s", StringComparison.Ordinal)
            && l.Contains("no connected player in range", StringComparison.Ordinal)
            && l.Contains("evidence \u00a722", StringComparison.Ordinal));
    }

    [Fact]
    public void A_zero_timeout_GoTo_says_once_a_minute_that_it_is_still_waiting()
    {
        // "Wait forever" is kept rather than refused or capped - it is a real contract for a
        // caller whose way out is its own event - so the wedge has to be visible some other
        // way: one line a minute, carrying the same cause the timeout would have named.
        var driver = new FakeDriver();
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);

        seq.Play(UnitStep.GoTo(new UnitVec3(6f, 0f, 0f), 0f));

        clock.Seconds = 59.0;
        seq.Tick();
        Assert.Empty(driver.Lines.FindAll(l => l.Contains("timeoutSeconds: 0", StringComparison.Ordinal)));

        clock.Seconds = 60.0;
        seq.Tick();
        Assert.Contains(driver.Lines, l =>
            l.Contains("GoTo step 1 of sequence 1 has waited 60s with timeoutSeconds: 0 (wait forever)", StringComparison.Ordinal)
            && l.Contains("nothing but its own arrival, an unreachable or a Stop will end it", StringComparison.Ordinal));
        // A reminder is a progress report; a cause is a diagnosis (R58 finding 8). This driver
        // cannot report a position, so the line says so and guesses nothing - the old wording
        // told every healthy long walk that its non-existent stall was the no-players gate,
        // which is the string an operator greps for after a real incident.
        Assert.Contains(driver.Lines, l =>
            l.Contains("timeoutSeconds: 0", StringComparison.Ordinal)
            && l.Contains("the cause is unknown from here", StringComparison.Ordinal));
        Assert.DoesNotContain(driver.Lines, l =>
            l.Contains("timeoutSeconds: 0", StringComparison.Ordinal)
            && l.Contains("no connected player in range", StringComparison.Ordinal));

        clock.Seconds = 119.0;
        seq.Tick();
        Assert.Single(driver.Lines.FindAll(l => l.Contains("timeoutSeconds: 0", StringComparison.Ordinal)));

        clock.Seconds = 120.0;
        seq.Tick();
        Assert.Equal(2, driver.Lines.FindAll(l => l.Contains("timeoutSeconds: 0", StringComparison.Ordinal)).Count);

        // Still waiting: the reminder reports, it does not end the step.
        Assert.True(seq.IsPlaying);
        Assert.DoesNotContain(driver.Steps, s => s.Outcome == UnitStepOutcome.TimedOut);
    }

    [Fact]
    public void The_wait_forever_reminder_diagnoses_a_unit_that_stopped_moving_and_not_one_that_is_walking()
    {
        // The only evidence the sequencer has for "wedged" rather than "long walk" is whether
        // the unit moved between two reminders, so that is the only thing that earns a cause
        // (R58 finding 8).
        var driver = new FakeDriver { Position = new UnitVec3(0f, 0f, 0f) };
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);

        seq.Play(UnitStep.GoTo(new UnitVec3(600f, 0f, 0f), 0f));

        // Minute one: it walked 40 m. Progress report only.
        driver.Position = new UnitVec3(40f, 0f, 0f);
        clock.Seconds = 60.0;
        seq.Tick();
        var first = driver.Lines.FindAll(l => l.Contains("timeoutSeconds: 0", StringComparison.Ordinal));
        Assert.Single(first);
        Assert.DoesNotContain("no connected player in range", first[0], StringComparison.Ordinal);
        Assert.DoesNotContain("unknown from here", first[0], StringComparison.Ordinal);
        Assert.EndsWith("will end it", first[0], StringComparison.Ordinal);

        // Minute two: it has not moved. Now the gate is worth naming.
        clock.Seconds = 120.0;
        seq.Tick();
        var second = driver.Lines.FindAll(l => l.Contains("timeoutSeconds: 0", StringComparison.Ordinal));
        Assert.Equal(2, second.Count);
        Assert.Contains("it has not moved since the last reminder", second[1], StringComparison.Ordinal);
        Assert.Contains("no connected player in range", second[1], StringComparison.Ordinal);

        // Minute three: walking again, and the line goes back to being a progress report.
        driver.Position = new UnitVec3(90f, 0f, 0f);
        clock.Seconds = 180.0;
        seq.Tick();
        var third = driver.Lines.FindAll(l => l.Contains("timeoutSeconds: 0", StringComparison.Ordinal));
        Assert.Equal(3, third.Count);
        Assert.EndsWith("will end it", third[2], StringComparison.Ordinal);
    }

    [Fact]
    public void A_discarded_notification_is_named_by_the_reminder_without_waiting_for_a_stall()
    {
        // A discard is evidence the sequencer collected itself, so it does not have to wait for
        // the position check - and it is the one cause a walking unit can still have.
        var driver = new FakeDriver { Position = new UnitVec3(0f, 0f, 0f) };
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);

        seq.Play(UnitStep.GoTo(new UnitVec3(600f, 0f, 0f), 0f));
        var live = driver.Armed;
        seq.NotifyArrived(new UnitMoveTicket(live.Epoch - 1, live.Step));

        driver.Position = new UnitVec3(40f, 0f, 0f);
        clock.Seconds = 60.0;
        seq.Tick();

        Assert.Contains(driver.Lines, l =>
            l.Contains("timeoutSeconds: 0", StringComparison.Ordinal)
            && l.Contains("something moved this unit outside the sequencer", StringComparison.Ordinal));
    }

    [Fact]
    public void The_default_and_the_clamp_are_what_the_step_records()
    {
        Assert.Equal(UnitStep.DefaultGoToTimeoutSeconds, UnitStep.GoTo(new UnitVec3(1f, 0f, 0f)).TimeoutSeconds);
        Assert.Equal(0f, UnitStep.GoTo(new UnitVec3(1f, 0f, 0f), -3f).TimeoutSeconds);
        Assert.Equal(0f, UnitStep.GoTo(new UnitVec3(1f, 0f, 0f), float.NaN).TimeoutSeconds);
        // Follow is a standing state with nothing to arrive at, so it has no deadline.
        Assert.Equal(0f, UnitStep.Follow(new UnitId(1, 1)).TimeoutSeconds);
    }

    [Fact]
    public void A_follow_step_is_never_timed_out()
    {
        var driver = new FakeDriver();
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);
        seq.Play(UnitStep.Follow(new UnitId(2, 1)), UnitStep.Wait(1000f));

        clock.Seconds = 500.0;
        seq.Tick();
        Assert.True(seq.IsPlaying);
        Assert.DoesNotContain(driver.Steps, s => s.Outcome == UnitStepOutcome.TimedOut);
    }

    // ---- R55 finding 7: the tree-selection read-back -------------------------------------------

    [Fact]
    public void Tree_selection_reads_the_binding_back_and_says_when_it_was_written_but_not_bound()
    {
        // A source pin, like the two above: UnityUnitBehaviour.cs is not compiled here. The
        // claim "bound" used to be made by the write itself, with nothing ever read.
        string text = File.ReadAllText(Path.Combine(
            SourcePin.RepoRoot(), "src", "Progenitor.Core", "Units", "UnityUnitBehaviour.cs"));
        Assert.Contains("did not read back on", text, StringComparison.Ordinal);
        Assert.Contains("BehaviourTreeInstance", text, StringComparison.Ordinal);
        Assert.Contains("was written but not bound on", text, StringComparison.Ordinal);
    }

    // ---- R56 findings 1, 2 and 3: the arrival that finished the next sequence ------------------

    [Fact]
    public void A_sequence_whose_last_step_chained_from_its_Then_ends_Completed_not_Stopped()
    {
        // R56 finding 2, measured: [Idle, Then(-> Play(Follow))] published "Stopped 2/2
        // (replaced by a new sequence)" — every step ran, the count said so, and the outcome
        // word said the opposite, so a subscriber keyed on Completed never saw the leg finish
        // and could not tell a chain from a cancellation.
        var driver = new FakeDriver();
        UnitSequencer? seq = null;
        seq = new UnitSequencer(driver, new FakeClock().Now);

        seq.Play(UnitStep.Idle(), UnitStep.Then(() => seq!.Play(UnitStep.Follow(new UnitId(7, 1)))));

        Assert.Equal(
            new[] { UnitSequenceOutcome.Completed, UnitSequenceOutcome.Completed },
            driver.SequenceHistory.ConvertAll(s => s.Outcome));
        // The exhausted sequence reports its own length, and nothing is published as "replaced".
        Assert.Equal(2, driver.SequenceHistory[0].Completed);
        Assert.Equal(2, driver.SequenceHistory[0].Count);
        Assert.Equal("", driver.SequenceHistory[0].Reason);
        Assert.DoesNotContain(driver.SequenceHistory, s => s.Reason == "replaced by a new sequence");
        // Step 1 is Idle so Play skips its own force; the second idle is the chained
        // sequence's force before its Follow.
        Assert.Equal(new[] { "idle", "idle", "follow 7:1" }, driver.Calls);
    }

    [Fact]
    public void An_interrupted_sequence_with_steps_left_still_stops_rather_than_completing()
    {
        // The other half of the reorder: the interrupt check still does its job everywhere the
        // sequence has somewhere left to go, so this must not become a Completed too.
        var driver = new FakeDriver();
        UnitSequencer? seq = null;
        seq = new UnitSequencer(driver, new FakeClock().Now);

        seq.Play(
            UnitStep.Then(() => seq!.Play(UnitStep.Follow(new UnitId(7, 1)))),
            UnitStep.Face(new UnitVec3(3f, 0f, 0f)));

        Assert.Equal(UnitSequenceOutcome.Stopped, driver.SequenceHistory[0].Outcome);
        Assert.Equal("replaced by a new sequence", driver.SequenceHistory[0].Reason);
        // The Face it was never going to reach was not written.
        Assert.DoesNotContain(driver.Calls, c => c.StartsWith("face", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Stop_queued_behind_a_Play_cancels_it_before_anything_is_written()
    {
        // R56 finding 3, measured: the queue applied the Play first, so the new sequence's
        // forced Idle and its GoTo reached the unit with no step event and — because End
        // discards the wait — no deadline behind them. Play(); Stop(); in one callback means
        // "never mind".
        var driver = new FakeDriver();
        UnitSequencer? seq = null;
        seq = new UnitSequencer(driver, new FakeClock().Now);

        seq.Play(
            UnitStep.Then(() =>
            {
                seq!.Play(UnitStep.GoTo(new UnitVec3(5f, 0f, 0f)), UnitStep.Follow(new UnitId(2, 1)));
                seq!.Stop();
            }),
            UnitStep.Face(new UnitVec3(1f, 0f, 0f)));

        Assert.False(seq.IsPlaying);
        Assert.Equal(0, seq.PendingCount);
        // Nothing of the canceled Play was written: no second forced Idle, no GoTo.
        Assert.Equal(new[] { "idle" }, driver.Calls);
        // And nothing was published for a sequence that never started: one Stopped, for the
        // sequence that really was playing.
        Assert.Single(driver.SequenceHistory);
        Assert.Equal(UnitSequenceOutcome.Stopped, driver.SequenceHistory[0].Outcome);
        Assert.Contains(driver.Lines, l =>
            l.Contains("Stop canceled 1 deferred action(s)", StringComparison.Ordinal)
            && l.Contains("none of them was written to the unit", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Play_queued_behind_a_Stop_still_starts_normally()
    {
        // The rule is directional: Stop cancels what is queued ahead of it, and a Play queued
        // after it is untouched, so "stop this, then start that" from one callback works.
        var driver = new FakeDriver();
        UnitSequencer? seq = null;
        seq = new UnitSequencer(driver, new FakeClock().Now);

        seq.Play(
            UnitStep.Then(() =>
            {
                seq!.Stop();
                seq!.Play(UnitStep.Follow(new UnitId(3, 1)));
            }),
            UnitStep.Face(new UnitVec3(1f, 0f, 0f)));

        // The Stop ends the first sequence and the Play behind it runs in full; Follow is an
        // immediate step, so the chained sequence completes on the same drain.
        Assert.Equal(new[] { "idle", "idle", "follow 3:1" }, driver.Calls);
        Assert.Equal(
            new[] { UnitSequenceOutcome.Stopped, UnitSequenceOutcome.Completed },
            driver.SequenceHistory.ConvertAll(s => s.Outcome));
        Assert.Equal("stopped", driver.SequenceHistory[0].Reason);
    }

    [Fact]
    public void A_GoTo_wait_that_is_discarded_says_so_and_its_deadline_goes_with_it()
    {
        // The deadline belongs to the step that armed it. A Stop mid-walk drops both, and the
        // log has to show it: the unit keeps the goal that was written, and no later tick will
        // time it out on the vanished sequence's behalf.
        var driver = new FakeDriver();
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);

        seq.Play(UnitStep.GoTo(new UnitVec3(9f, 0f, 0f), 30f));
        Assert.Equal(new UnitMoveTicket(1, 0), driver.Armed);

        seq.Stop();
        Assert.Contains(driver.Lines, l =>
            l.Contains("GoTo wait step 1 of sequence 1 disarmed (sequence Stopped)", StringComparison.Ordinal)
            && l.Contains("its deadline is gone with it", StringComparison.Ordinal));

        clock.Seconds = 10_000.0;
        seq.Tick();
        Assert.DoesNotContain(driver.Steps, s => s.Outcome == UnitStepOutcome.TimedOut);
        Assert.Single(driver.SequenceHistory);
    }

    [Fact]
    public void An_arrival_stamped_for_a_step_that_is_no_longer_running_is_discarded()
    {
        // The ticket, independent of the depth fix: a notification carrying anything but the
        // wait the sequencer is holding cannot complete the live step.
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);

        seq.Play(UnitStep.GoTo(new UnitVec3(4f, 0f, 0f)), UnitStep.Follow(new UnitId(5, 1)));
        var live = driver.Armed;

        seq.NotifyArrived(new UnitMoveTicket(live.Epoch - 1, live.Step));
        Assert.Empty(driver.Steps);
        Assert.DoesNotContain(driver.Calls, c => c.StartsWith("follow", StringComparison.Ordinal));
        Assert.Contains(driver.Lines, l =>
            l.Contains("arrival for step 1 of sequence 0 discarded", StringComparison.Ordinal)
            && l.Contains("the step it was armed for is no longer running", StringComparison.Ordinal));

        // The real one still lands.
        seq.NotifyArrived(live);
        Assert.Equal(UnitStepOutcome.Completed, driver.Steps[0].Outcome);
        Assert.Contains(driver.Calls, c => c.StartsWith("follow", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unreachable_stamped_for_a_step_that_is_no_longer_running_is_discarded()
    {
        var driver = new FakeDriver();
        var seq = new UnitSequencer(driver, new FakeClock().Now);

        seq.Play(UnitStep.GoTo(new UnitVec3(4f, 0f, 0f)));
        var live = driver.Armed;

        seq.NotifyUnreachable(new UnitMoveTicket(live.Epoch, live.Step + 1), "not this step's problem");
        Assert.True(seq.IsPlaying);
        Assert.Empty(driver.Steps);

        seq.NotifyUnreachable(live, "no path");
        Assert.False(seq.IsPlaying);
        Assert.Equal("no path", driver.Sequence!.Value.Reason);
    }

    // ---- R56 finding 1, on the real handle -----------------------------------------------------

    [Fact]
    public void A_UnitArrived_subscriber_that_starts_a_new_sequence_does_not_have_its_first_GoTo_completed()
    {
        // The measured case: the rat is at (1, 0, 0), a UnitArrived subscriber chains a GoTo to
        // (50, 0, 0), and the old arrival landed on the new sequence's first GoTo — published
        // Completed, walked on to Follow, and declared the whole thing "Completed 2/2" with
        // forty-nine meters never walked. Publish and notify are one sequencer entry now, so
        // the subscriber's Play is deferred like every other callback's.
        var runtime = new HandleRuntime();
        var unit = new UnitId(11, 1);
        runtime.Place(unit, new UnitVec3(1f, 0f, 0f));
        var handle = new UnitHandle("CHAR_Critter_Rat", "example.test", null, unit, new UnitVec3(0f, 0f, 0f), runtime, enabled: true);

        var steps = new List<string>();
        var sequences = new List<string>();
        using var onStep = EventBus.On<UnitStepCompleted>(
            e => { if (ReferenceEquals(e.Handle, handle)) steps.Add(e.Step.Kind + " " + e.Index + "/" + e.Count + " " + e.Outcome); },
            owner: this);
        using var onSequence = EventBus.On<UnitSequenceCompleted>(
            e => { if (ReferenceEquals(e.Handle, handle)) sequences.Add(e.Outcome + " " + e.StepsCompleted + "/" + e.Count); },
            owner: this);
        using var onArrived = EventBus.On<UnitArrived>(
            e =>
            {
                if (!ReferenceEquals(e.Handle, handle)) return;
                if (sequences.Count > 0) return; // chain once
                handle.Behaviour.Play(UnitStep.GoTo(new UnitVec3(50f, 0f, 0f)), UnitStep.Follow(new UnitId(12, 1)));
            },
            owner: this);

        handle.Behaviour.Play(UnitStep.GoTo(new UnitVec3(1f, 0f, 0f)));
        handle.Poll(); // the unit is already at the goal, so this arrives.

        // The chained sequence is on its first step and waiting, not finished.
        Assert.True(handle.Behaviour.IsPlaying);
        Assert.Equal(0, handle.Behaviour.StepIndex);
        Assert.Equal(2, handle.Behaviour.StepCount);
        Assert.Equal(new UnitVec3(50f, 0f, 0f), runtime.LastGoal);
        // No Follow was written and no "Completed 2/2" was published for a walk not taken.
        Assert.Equal(0, runtime.FollowCalls);
        Assert.Equal(new[] { "GoTo 0/1 Completed" }, steps);
        Assert.Equal(new[] { "Completed 1/1" }, sequences);

        EventBus.RemoveAll(this);
    }

    [Fact]
    public void A_UnitUnreachable_subscriber_that_starts_a_new_sequence_does_not_have_its_first_GoTo_failed()
    {
        // The twin: there the stale notify failed the brand-new sequence's first step with the
        // old sequence's reason.
        var runtime = new HandleRuntime();
        var unit = new UnitId(21, 1);
        runtime.Place(unit, new UnitVec3(1f, 0f, 0f));
        var handle = new UnitHandle("CHAR_Critter_Rat", "example.test", null, unit, new UnitVec3(0f, 0f, 0f), runtime, enabled: true);

        var steps = new List<string>();
        var sequences = new List<string>();
        using var onStep = EventBus.On<UnitStepCompleted>(
            e => { if (ReferenceEquals(e.Handle, handle)) steps.Add(e.Step.Kind + " " + e.Index + "/" + e.Count + " " + e.Outcome + " " + e.Reason); },
            owner: this);
        using var onSequence = EventBus.On<UnitSequenceCompleted>(
            e => { if (ReferenceEquals(e.Handle, handle)) sequences.Add(e.Outcome + " " + e.StepsCompleted + "/" + e.Count); },
            owner: this);
        using var onUnreachable = EventBus.On<UnitUnreachable>(
            e =>
            {
                if (!ReferenceEquals(e.Handle, handle)) return;
                if (sequences.Count > 0) return;
                handle.Behaviour.Play(UnitStep.GoTo(new UnitVec3(50f, 0f, 0f)), UnitStep.Follow(new UnitId(22, 1)));
            },
            owner: this);

        handle.Behaviour.Play(UnitStep.GoTo(new UnitVec3(80f, 0f, 0f)));
        runtime.Hidden = true;
        handle.Poll(); // the entity reads as gone, so this is unreachable.

        Assert.True(handle.Behaviour.IsPlaying);
        Assert.Equal(0, handle.Behaviour.StepIndex);
        Assert.Equal(new[] { "GoTo 0/1 Failed entity gone" }, steps);
        Assert.Equal(new[] { "Failed 0/1" }, sequences);
        Assert.Equal(2, handle.Behaviour.StepCount);
        Assert.Equal(new UnitVec3(50f, 0f, 0f), runtime.LastGoal);
        Assert.Equal(0, runtime.FollowCalls);

        EventBus.RemoveAll(this);
    }

    // ---- R57 finding 1: a plugin unload removes delegates, not other people's sequences -------

    [Fact]
    public void SweepForUnload_stops_a_sequence_the_unloading_plugin_owns_and_reports_it_on_the_bus()
    {
        // A handle owned by a second object from the unloading assembly (a manager, a helper)
        // is not caught by RemoveAll's reference match, so the sweep is the only thing that
        // reaches it - and the plugin that owns the unit is going away, so its sequence really
        // does end here. It ends properly: published, with the move wait cleared and logged.
        var log = new TestLogSource();
        var prior = Plugin.Logger;
        Plugin.Logger = log;
        var runtime = new HandleRuntime();
        UnitsApi.ResetForTests();
        UnitsApi.SetRuntimeForTests(runtime);
        try
        {
            var unloading = new UnloadingPlugin();
            var helper = new UnloadingPlugin();
            var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), helper, "plugin.a");

            var sequences = new List<string>();
            using var onSequence = EventBus.On<UnitSequenceCompleted>(
                e => { if (ReferenceEquals(e.Handle, handle)) sequences.Add(e.Outcome + " " + e.StepsCompleted + "/" + e.Count + " " + e.Reason); },
                owner: this);

            bool ran = false;
            handle.Behaviour.Play(UnitStep.GoTo(new UnitVec3(40f, 0f, 0f)), UnitStep.Then(() => ran = true));
            Assert.True(handle.Behaviour.IsPlaying);

            UnitsApi.SweepForUnload(unloading, "plugin.a");

            Assert.False(handle.Behaviour.IsPlaying);
            Assert.False(ran);
            Assert.Equal(new[] { "Stopped 0/2 plugin " + Here + " unloaded" }, sequences);
            // The unit is not the unloading plugin's to despawn, so the handle stays live.
            Assert.False(handle.IsDisposed);
            Assert.Equal(1, UnitsApi.LiveCountForTests);
            Assert.Contains(log.Infos, l =>
                l.Contains("stopped the sequence on entity=", StringComparison.Ordinal)
                && l.Contains("the plugin that owns it, " + Here + ", is unloading", StringComparison.Ordinal));
            // The deadline goes with the sequence, and says so: the alternative is a written
            // AiMove_Server goal with nobody watching it (R56 finding 3).
            Assert.Contains(log.Infos, l =>
                l.Contains("GoTo wait step 1 of sequence 1 disarmed (sequence Stopped)", StringComparison.Ordinal));
        }
        finally
        {
            EventBus.RemoveAll(this);
            UnitsApi.ResetForTests();
            Plugin.Logger = prior;
        }
    }

    [Fact]
    public void SweepForUnload_leaves_another_plugins_sequence_running_with_only_the_unloading_callbacks_dropped()
    {
        // The measured defect (R57 finding 1): plugin B's rat, mid-GoTo, stopped by plugin A's
        // unload with no event, the goal still written and the deadline gone - because one of
        // B's steps named a method A published, which is how one mod extends another.
        //
        // This is also the pin R56 finding 10's change never had. B's handle has a non-null
        // Owner, so restoring the old "if (handle.Owner != null) continue;" skip makes the
        // sweep miss it entirely and HoldsCallbackFrom below still answers true - A's ALC
        // stays pinned, and this test fails.
        var log = new TestLogSource();
        var prior = Plugin.Logger;
        Plugin.Logger = log;
        var runtime = new HandleRuntime();
        UnitsApi.ResetForTests();
        UnitsApi.SetRuntimeForTests(runtime);
        try
        {
            var unloading = new UnloadingPlugin();
            // Plugin B, in another assembly: a bare object is declared in the BCL, which is the
            // only cheap way to have two owners from two assemblies inside one test run.
            object otherPlugin = new();
            var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), otherPlugin, "plugin.b");
            var live = (UnitHandle)handle;

            var sequences = new List<string>();
            var steps = new List<string>();
            var reasons = new List<string>();
            using var onSequence = EventBus.On<UnitSequenceCompleted>(
                e => { if (ReferenceEquals(e.Handle, handle)) sequences.Add(e.Outcome + " " + e.StepsCompleted + "/" + e.Count); },
                owner: this);
            using var onStep = EventBus.On<UnitStepCompleted>(
                e =>
                {
                    if (!ReferenceEquals(e.Handle, handle)) return;
                    steps.Add(e.Step.Kind + " " + e.Index + " " + e.Outcome);
                    reasons.Add(e.Reason);
                },
                owner: this);

            bool unloadingCallbackRan = false;
            var ownCallbackTarget = new List<int> { 1, 2, 3 };
            handle.Behaviour.Play(
                UnitStep.GoTo(new UnitVec3(40f, 0f, 0f)),
                UnitStep.Then(() => unloadingCallbackRan = true),
                UnitStep.Then(ownCallbackTarget.Clear));

            UnitsApi.SweepForUnload(unloading, "plugin.a");

            // Still walking, on the same step, with nothing published.
            Assert.True(handle.Behaviour.IsPlaying);
            Assert.Equal(0, handle.Behaviour.StepIndex);
            Assert.Equal(3, handle.Behaviour.StepCount);
            Assert.Empty(sequences);
            Assert.False(handle.IsDisposed);
            Assert.Equal(UnitHandleState.GoingTo, handle.State);

            // A's delegate is gone - the whole point of the sweep - and B's own is not.
            Assert.False(live.HoldsCallbackFrom(typeof(UnitBehaviourTests).Assembly));
            Assert.True(live.HoldsCallbackFrom(typeof(List<int>).Assembly));
            Assert.Contains(log.Infos, l =>
                l.Contains("dropped 1 Then callback(s) written in " + Here, StringComparison.Ordinal)
                && l.Contains("the rest of the sequence keeps running", StringComparison.Ordinal));

            // And the rest of it runs: the neutralized step completes as a no-op in its own
            // slot, so the indices B subscribed to still mean what they meant.
            runtime.Place(live.Entity, new UnitVec3(40f, 0f, 0f));
            UnitsApi.TickForTests();

            Assert.False(unloadingCallbackRan);
            Assert.Empty(ownCallbackTarget);
            // The neutralized step keeps its slot - so index 2 still means what B wrote at
            // index 2 - and it is Skipped with a reason, not Completed with an empty one, so a
            // subscriber that confirms its own side effect from this event can tell the no-op
            // from the callback that ran (R58 finding 6).
            Assert.Equal(new[] { "GoTo 0 Completed", "Invoke 1 Skipped", "Invoke 2 Completed" }, steps);
            Assert.Equal("callback dropped: " + Here + " unloaded", reasons[1]);
            Assert.Equal("", reasons[2]);
            Assert.Equal(new[] { "Completed 3/3" }, sequences);
        }
        finally
        {
            EventBus.RemoveAll(this);
            UnitsApi.ResetForTests();
            Plugin.Logger = prior;
        }
    }

    [Fact]
    public void SweepForUnload_neutralises_an_ownerless_sequence_rather_than_dropping_it()
    {
        // The R55 finding 9 case - a static-API spawn with no owner - reached the same way and
        // now treated the same way: the delegate goes, the patrol does not.
        var log = new TestLogSource();
        var prior = Plugin.Logger;
        Plugin.Logger = log;
        var runtime = new HandleRuntime();
        UnitsApi.ResetForTests();
        UnitsApi.SetRuntimeForTests(runtime);
        try
        {
            var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f));
            handle.Behaviour.Play(UnitStep.GoTo(new UnitVec3(40f, 0f, 0f)), UnitStep.Then(() => { }));

            UnitsApi.SweepForUnload(new UnloadingPlugin(), "plugin.a");

            Assert.True(handle.Behaviour.IsPlaying);
            Assert.False(((UnitHandle)handle).HoldsCallbackFrom(typeof(UnitBehaviourTests).Assembly));
            Assert.Contains(log.Infos, l => l.Contains("(owner: none)", StringComparison.Ordinal));
            Assert.Contains(log.Infos, l => l.Contains("pass owner: to Units.Spawn", StringComparison.Ordinal));
        }
        finally
        {
            UnitsApi.ResetForTests();
            Plugin.Logger = prior;
        }
    }

    [Fact]
    public void SweepForUnload_does_not_visit_a_sequence_that_holds_none_of_the_unloading_plugins_callbacks()
    {
        var log = new TestLogSource();
        var prior = Plugin.Logger;
        Plugin.Logger = log;
        var runtime = new HandleRuntime();
        UnitsApi.ResetForTests();
        UnitsApi.SetRuntimeForTests(runtime);
        try
        {
            object otherPlugin = new();
            var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), otherPlugin, "plugin.b");
            var keep = new List<int> { 1 };
            handle.Behaviour.Play(UnitStep.GoTo(new UnitVec3(40f, 0f, 0f)), UnitStep.Then(keep.Clear));

            UnitsApi.SweepForUnload(new UnloadingPlugin(), "plugin.a");

            Assert.True(handle.Behaviour.IsPlaying);
            Assert.True(((UnitHandle)handle).HoldsCallbackFrom(typeof(List<int>).Assembly));
            Assert.DoesNotContain(log.Infos, l => l.Contains("Then callback(s) written in", StringComparison.Ordinal));
            Assert.DoesNotContain(log.Infos, l => l.Contains("stopped the sequence on entity=", StringComparison.Ordinal));
        }
        finally
        {
            UnitsApi.ResetForTests();
            Plugin.Logger = prior;
        }
    }

    [Fact]
    public void SweepForUnload_stops_a_patrol_the_unloading_plugin_owns_that_holds_no_callback_at_all()
    {
        // R58 finding 3, gate one: the sweep used to build its list from handles that answered
        // HoldsCallbackFrom, so a patrol of GoTo / Wait / Face steps - the shape `.rat patrol`
        // itself uses - was never visited. Owned by a helper, so RemoveAll's reference match
        // misses it too, and the measured state was `still playing=True state=GoingTo`, no
        // event, and no log line at all.
        var log = new TestLogSource();
        var prior = Plugin.Logger;
        Plugin.Logger = log;
        var runtime = new HandleRuntime();
        UnitsApi.ResetForTests();
        UnitsApi.SetRuntimeForTests(runtime);
        try
        {
            var helper = new UnloadingPlugin();
            var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), helper, "plugin.a");

            var sequences = new List<string>();
            using var onSequence = EventBus.On<UnitSequenceCompleted>(
                e => { if (ReferenceEquals(e.Handle, handle)) sequences.Add(e.Outcome + " " + e.StepsCompleted + "/" + e.Count); },
                owner: this);

            handle.Behaviour.Play(UnitStep.GoTo(new UnitVec3(40f, 0f, 0f)), UnitStep.Wait(600f));
            Assert.False(((UnitHandle)handle).HoldsCallbackFrom(typeof(UnitBehaviourTests).Assembly));

            UnitsApi.SweepForUnload(new UnloadingPlugin(), "plugin.a");

            Assert.False(handle.Behaviour.IsPlaying);
            Assert.Equal(new[] { "Stopped 0/2" }, sequences);
            Assert.Contains(log.Infos, l => l.Contains("stopped the sequence on entity=", StringComparison.Ordinal));
        }
        finally
        {
            EventBus.RemoveAll(this);
            UnitsApi.ResetForTests();
            Plugin.Logger = prior;
        }
    }

    [Fact]
    public void SweepForUnload_claims_a_handle_by_its_pluginId_when_the_owner_is_a_bare_token()
    {
        // R58 finding 3, gate two: the public API takes `object owner` and invites a bare
        // token, and a token's type is declared in the BCL, so the assembly test filed a
        // plugin's own unit as a stranger's - measured as `still playing=True state=GoingTo`
        // with a log line saying the sequence "keeps running", for a plugin that is going away.
        // The pluginId is recorded on the handle for exactly this, so either half is enough.
        //
        // The sweep argument here is the production shape (R59 finding 1): an ordinary object
        // from the unloading assembly with no GUID property to reflect over. The id comes in as
        // an argument, which is the only route that exists in the game.
        var log = new TestLogSource();
        var prior = Plugin.Logger;
        Plugin.Logger = log;
        var runtime = new HandleRuntime();
        UnitsApi.ResetForTests();
        UnitsApi.SetRuntimeForTests(runtime);
        try
        {
            object token = new();
            var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), token, "plugin.a");

            var sequences = new List<string>();
            using var onSequence = EventBus.On<UnitSequenceCompleted>(
                e => { if (ReferenceEquals(e.Handle, handle)) sequences.Add(e.Outcome + " " + e.StepsCompleted + "/" + e.Count); },
                owner: this);

            bool ran = false;
            handle.Behaviour.Play(UnitStep.GoTo(new UnitVec3(40f, 0f, 0f)), UnitStep.Then(() => ran = true));

            UnitsApi.SweepForUnload(new UnloadingPlugin(), "plugin.a");

            Assert.False(handle.Behaviour.IsPlaying);
            Assert.False(ran);
            Assert.Equal(new[] { "Stopped 0/2" }, sequences);
            Assert.Contains(log.Infos, l => l.Contains("stopped the sequence on entity=", StringComparison.Ordinal));
            Assert.DoesNotContain(log.Infos, l =>
                l.Contains("the rest of the sequence keeps running", StringComparison.Ordinal));
        }
        finally
        {
            EventBus.RemoveAll(this);
            UnitsApi.ResetForTests();
            Plugin.Logger = prior;
        }
    }

    [Fact]
    public void The_sweep_reads_no_plugin_id_off_the_owner_object()
    {
        // The pin for R59 finding 1. Same handle, same bare token, same owner type - only the
        // id argument withheld. FUNIT5 reflected a public GUID / Guid property off the sweep
        // argument, which no BepInEx plugin type declares, so in the game the id half never
        // fired; with the fixture's property gone the reflective route has nothing to find and
        // this test would only pass if something still guessed an id, which nothing may.
        var runtime = new HandleRuntime();
        UnitsApi.ResetForTests();
        UnitsApi.SetRuntimeForTests(runtime);
        try
        {
            Assert.Null(typeof(UnloadingPlugin).GetProperty("GUID"));
            Assert.Null(typeof(UnloadingPlugin).GetProperty("Guid"));

            object token = new();
            var handle = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), token, "plugin.a");
            handle.Behaviour.Play(UnitStep.GoTo(new UnitVec3(40f, 0f, 0f)), UnitStep.Wait(600f));

            UnitsApi.SweepForUnload(new UnloadingPlugin(), pluginId: null);

            // Not claimed: a bare token is not this assembly's, and no id was offered.
            Assert.True(handle.Behaviour.IsPlaying);
        }
        finally
        {
            UnitsApi.ResetForTests();
        }
    }

    [Fact]
    public void RemoveAll_despawns_this_owners_units_and_leaves_every_other_sequence_alone()
    {
        // R59 finding 4. RemoveAll(owner) is the per-owner API a plugin calls to retire one
        // subsystem; FUNIT5 had put the assembly-wide unload sweep behind it, so retiring
        // subsystem A published Stopped with reason "plugin ... unloaded" on subsystems B and C
        // of a plugin that was not unloading. The wide sweep is SweepForUnload, and only the
        // reloader calls it.
        var log = new TestLogSource();
        var prior = Plugin.Logger;
        Plugin.Logger = log;
        var runtime = new HandleRuntime();
        UnitsApi.ResetForTests();
        UnitsApi.SetRuntimeForTests(runtime);
        try
        {
            var poolA = new UnloadingPlugin();
            var poolB = new UnloadingPlugin();   // same assembly, same plugin, another subsystem
            var mine = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), poolA, "plugin.a");
            var sibling = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), poolB, "plugin.a");

            var sequences = new List<string>();
            using var onSequence = EventBus.On<UnitSequenceCompleted>(
                e => sequences.Add(e.Outcome + " " + e.Reason),
                owner: this);

            mine.Behaviour.Play(UnitStep.GoTo(new UnitVec3(40f, 0f, 0f)), UnitStep.Wait(600f));
            sibling.Behaviour.Play(UnitStep.GoTo(new UnitVec3(40f, 0f, 0f)), UnitStep.Then(() => { }));

            UnitsApi.RemoveAll(poolA);

            // A's unit is gone, and its sequence went silently with the handle.
            Assert.True(mine.IsDisposed);
            Assert.False(mine.Behaviour.IsPlaying);
            // B's is untouched: still playing, still holding its callback, no event, no line.
            Assert.False(sibling.IsDisposed);
            Assert.True(sibling.Behaviour.IsPlaying);
            Assert.True(((UnitHandle)sibling).HoldsCallbackFrom(typeof(UnitBehaviourTests).Assembly));
            Assert.Empty(sequences);
            Assert.DoesNotContain(log.Infos, l =>
                l.Contains("stopped the sequence on entity=", StringComparison.Ordinal));
            Assert.DoesNotContain(log.Infos, l =>
                l.Contains("Then callback(s) written in", StringComparison.Ordinal));
        }
        finally
        {
            EventBus.RemoveAll(this);
            UnitsApi.ResetForTests();
            Plugin.Logger = prior;
        }
    }

    [Fact]
    public void A_handle_whose_entity_the_runtime_cannot_read_reports_no_position_rather_than_its_last_one()
    {
        // R59 finding 5. IUnitStepDriver.Alive consults no runtime, so a unit killed mid-walk
        // stays alive to the sequencer; going through the cached Position made the driver answer
        // true with the last number it saw, and the wait-forever reminder then read "did not
        // move" and printed the no-connected-player diagnosis for a unit that no longer existed.
        var runtime = new HandleRuntime();
        var unit = new UnitId(77, 1);
        runtime.Place(unit, new UnitVec3(5f, 0f, 0f));
        var handle = new UnitHandle("CHAR_Critter_Rat", "plugin.a", null, unit, new UnitVec3(0f, 0f, 0f), runtime, enabled: true);
        var driver = (IUnitStepDriver)handle;

        Assert.True(driver.TryGetPosition(out var read));
        Assert.Equal(new UnitVec3(5f, 0f, 0f), read);
        Assert.Equal(new UnitVec3(5f, 0f, 0f), handle.Position);   // primes the public cache

        runtime.Despawn(unit);   // killed: the entity is gone, the handle is not

        Assert.True(driver.Alive);
        Assert.Equal(new UnitVec3(5f, 0f, 0f), handle.Position);   // the public cache still answers
        Assert.False(driver.TryGetPosition(out var gone));         // the driver does not
        Assert.Equal(default(UnitVec3), gone);
    }

    [Fact]
    public void The_reminder_stops_diagnosing_a_stall_once_the_position_cannot_be_read()
    {
        // The other half of R59 finding 5, on the sequencer: a unit that walked and then went
        // unreadable gets the "could not be read" line, not "it has not moved ... no connected
        // player in range".
        var driver = new FakeDriver { Position = new UnitVec3(0f, 0f, 0f) };
        var clock = new FakeClock();
        var seq = new UnitSequencer(driver, clock.Now);

        seq.Play(UnitStep.GoTo(new UnitVec3(600f, 0f, 0f), 0f));

        driver.Position = new UnitVec3(40f, 0f, 0f);
        clock.Seconds = 60.0;
        seq.Tick();

        driver.Position = null;   // killed mid-route: the runtime cannot read the entity
        clock.Seconds = 120.0;
        seq.Tick();

        var lines = driver.Lines.FindAll(l => l.Contains("timeoutSeconds: 0", StringComparison.Ordinal));
        Assert.Equal(2, lines.Count);
        Assert.Contains("its position could not be read", lines[1], StringComparison.Ordinal);
        Assert.Contains("killed or despawned", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("it has not moved since the last reminder", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("no connected player in range", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_bystanders_reaction_to_the_unload_stop_runs_on_its_own_unit_and_on_the_swept_one()
    {
        // R58 finding 4, settled by job FUNIT6 (R59 finding 6). The published Stopped exists so
        // a bystander can park or restart the abandoned unit, and parking it is a Play aimed at
        // exactly the unit that was swept - which FUNIT4 dropped in StopForUnload's finally, so
        // the documented purpose of the event and the one reaction it invites disagreed. The
        // deferral was all that was needed: the Play is queued because the stack is still inside
        // End, and Leave drains it once the unload stop has finished. Both land.
        var log = new TestLogSource();
        var prior = Plugin.Logger;
        Plugin.Logger = log;
        var runtime = new HandleRuntime();
        UnitsApi.ResetForTests();
        UnitsApi.SetRuntimeForTests(runtime);
        try
        {
            var helper = new UnloadingPlugin();
            var leaving = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), helper, "plugin.a");
            var mine = UnitsApi.Spawn("CHAR_Critter_Rat", new UnitVec3(0f, 0f, 0f), new object(), "plugin.b");

            int reactions = 0;
            using var onSequence = EventBus.On<UnitSequenceCompleted>(
                e =>
                {
                    if (!ReferenceEquals(e.Handle, leaving)) return;
                    reactions++;
                    mine.Behaviour.Play(UnitStep.Wait(600f));       // the bystander's own unit
                    leaving.Behaviour.Play(UnitStep.Wait(600f));    // the unit that was just swept
                },
                owner: this);

            leaving.Behaviour.Play(UnitStep.GoTo(new UnitVec3(40f, 0f, 0f)));

            UnitsApi.SweepForUnload(new UnloadingPlugin(), "plugin.a");

            // One publication, one reaction: parking the swept unit does not re-publish.
            Assert.Equal(1, reactions);
            // The bystander's own unit took the new sequence.
            Assert.True(mine.Behaviour.IsPlaying);
            Assert.Equal(1, mine.Behaviour.StepCount);
            // And so did the swept one: the sweep ended its sequence, it did not dispose the
            // handle, so the unit is still there to be parked and the parking takes effect.
            Assert.True(leaving.Behaviour.IsPlaying);
            Assert.Equal(1, leaving.Behaviour.StepCount);
            Assert.False(leaving.IsDisposed);
            // The old drop said so in the log; there is nothing to say now.
            Assert.DoesNotContain(log.Infos, l =>
                l.Contains("queued on this unit from inside the unload stop were dropped", StringComparison.Ordinal));
            // It arrives the way every other deferred action does.
            Assert.Contains(log.Infos, l =>
                l.Contains("sequence Play deferred to the end of this tick", StringComparison.Ordinal));
        }
        finally
        {
            EventBus.RemoveAll(this);
            UnitsApi.ResetForTests();
            Plugin.Logger = prior;
        }
    }

    /// <summary>The assembly the sweep in these tests is unloading: this one.</summary>
    private static readonly string Here = typeof(UnitBehaviourTests).Assembly.GetName().Name!;

    /// <summary>
    /// The shape the sweep actually gets in the game: an object declared in the unloading
    /// assembly that exposes <b>no</b> plugin id of any kind. BepInEx's <c>BasePlugin</c>
    /// declares none — its public surface is <c>Log</c>, <c>Config</c>, <c>Load</c>,
    /// <c>Unload</c>, <c>AddComponent</c> — and neither does <c>ProgenitorPlugin</c> or the
    /// Sample's plugin type; the id lives on the <c>[BepInPlugin]</c> attribute and is read
    /// through <c>MetadataHelper</c>. FUNIT5's fixture declared a public <c>GUID</c> property
    /// so that its reflective lookup would find one, which is the only reason the id half of
    /// the sweep ever passed a test (job FUNIT6, R59 finding 1). The id is now an argument,
    /// passed the way <c>PluginReloader.SweepRegistrations</c> passes it.
    /// </summary>
    private sealed class UnloadingPlugin
    {
    }

    /// <summary>
    /// The smallest runtime the real <see cref="UnitHandle"/> needs: a position table and a
    /// record of what was written. Its own rather than <c>UnitsTests.FakeUnitRuntime</c>, which
    /// carries an earlier session's uncommitted edits.
    /// </summary>
    private sealed class HandleRuntime : IUnitRuntime
    {
        private readonly Dictionary<UnitId, UnitVec3> _pos = new();

        public bool IsServer => true;

        public string? LastMissReason => null;

        public int FollowCalls { get; private set; }

        public UnitVec3 LastGoal { get; private set; }

        public void Place(UnitId unit, UnitVec3 position) => _pos[unit] = position;

        /// <summary>Report the unit as gone to <c>Exists</c> without removing it: the poll then
        /// raises Unreachable while the position table still answers, which is the depth-0
        /// route R56 finding 1 is about.</summary>
        public bool Hidden { get; set; }

        private int _nextEntity = 500;

        /// <summary>Mint a real id so <c>Units.Spawn</c> hands back an enabled handle.</summary>
        public UnitId Spawn(string prefabName, int prefabGuid, UnitVec3 position)
        {
            var id = new UnitId(_nextEntity++, 1);
            _pos[id] = position;
            return id;
        }

        public bool Follow(UnitId unit, UnitId target)
        {
            FollowCalls++;
            return _pos.ContainsKey(unit);
        }

        public UnitEngageResult Engage(UnitId unit, UnitId target) => default;

        public string PetTick(UnitId pet, UnitId owner) => "gone";

        public bool TryGetHealth(UnitId unit, out float maxHealth, out float value, out bool isDead)
        {
            maxHealth = 0f; value = 0f; isDead = false; return false;
        }

        public bool HasDeadComponent(UnitId unit) => false;

        public bool TrySetHealthPools(UnitId unit, float maxHealth, float value, float maxRecovery) => false;

        public bool TryGetUnitPower(UnitId unit, out float physicalPower, out float spellPower)
        {
            physicalPower = 0f; spellPower = 0f; return false;
        }

        public bool TrySetUnitPower(UnitId unit, float? physicalPower, float? spellPower) => false;

        public bool TryGetLevel(UnitId unit, out int level) { level = 0; return false; }

        public bool TrySetLevel(UnitId unit, int level) => false;

        public bool TrySetLifetime(UnitId unit, float seconds) => false;

        public bool TrySetTeam(UnitId unit, int? teamValue, int? factionIndex) => false;

        public UnitStripVBloodResult StripVBlood(UnitId unit) => default;

        public bool AddedFollower(UnitId unit) => false;

        public bool GoTo(UnitId unit, UnitVec3 position)
        {
            if (!_pos.ContainsKey(unit)) return false;
            LastGoal = position;
            return true;
        }

        public bool Despawn(UnitId unit) => _pos.Remove(unit);

        public bool Exists(UnitId unit) => !Hidden && _pos.ContainsKey(unit);

        public bool TryGetPosition(UnitId unit, out UnitVec3 position) => _pos.TryGetValue(unit, out position);
    }

    // ---- fakes ---------------------------------------------------------------------------------

    private sealed class FakeClock
    {
        public double Seconds;
        public double Now() => Seconds;
    }

    private readonly record struct StepRecord(UnitStepKind Kind, int Index, int Count, UnitStepOutcome Outcome, string Reason);

    private readonly record struct SequenceRecord(UnitSequenceOutcome Outcome, int Completed, int Count, string Reason);

    private sealed class FakeDriver : IUnitStepDriver
    {
        public bool Alive { get; set; } = true;
        public bool RefuseGoTo { get; set; }
        public bool RefuseFollow { get; set; }
        public bool RefuseFace { get; set; }
        public bool RefuseIdle { get; set; }
        public bool RefuseAnimate { get; set; }
        public bool ThrowOnIdle { get; set; }

        /// <summary>Stands in for a <see cref="UnitStepCompleted"/> subscriber (R55 finding 1).</summary>
        public Action? OnStepCompleted { get; set; }

        /// <summary>Stands in for a <see cref="UnitSequenceCompleted"/> subscriber.</summary>
        public Action? OnSequenceCompleted { get; set; }

        public List<string> Calls { get; } = new();
        public List<string> Lines { get; } = new();
        public List<StepRecord> Steps { get; } = new();
        public List<SequenceRecord> SequenceHistory { get; } = new();
        public SequenceRecord? Sequence { get; set; }

        public UnitStepResult Idle()
        {
            if (ThrowOnIdle) throw new InvalidOperationException("idle blew up");
            Calls.Add("idle");
            return RefuseIdle ? UnitStepResult.Failed : UnitStepResult.Done;
        }

        public UnitStepResult GoTo(UnitVec3 position)
        {
            Calls.Add("goto " + position);
            return RefuseGoTo ? UnitStepResult.Failed : UnitStepResult.Running;
        }

        public UnitStepResult Follow(UnitId target)
        {
            Calls.Add("follow " + target);
            return RefuseFollow ? UnitStepResult.Failed : UnitStepResult.Done;
        }

        /// <summary>The stamp the sequencer armed the last GoTo wait with (R56 finding 1).</summary>
        public UnitMoveTicket Armed { get; private set; } = UnitMoveTicket.None;

        public void MoveArmed(UnitMoveTicket ticket) => Armed = ticket;

        public UnitStepResult Face(UnitId target)
        {
            Calls.Add("face-entity " + target);
            return RefuseFace ? UnitStepResult.Failed : UnitStepResult.Done;
        }

        public UnitStepResult Face(UnitVec3 position)
        {
            Calls.Add("face-position " + position);
            return RefuseFace ? UnitStepResult.Failed : UnitStepResult.Done;
        }

        public UnitStepResult Animate(string animationName, float seconds)
        {
            Calls.Add("animate " + animationName + " " + seconds.ToString("0.##"));
            return RefuseAnimate ? UnitStepResult.Skipped : UnitStepResult.Done;
        }

        public void Log(string line) => Lines.Add(line);

        /// <summary>
        /// Where the fake unit stands, when a test wants the wait-forever reminder's progress
        /// check to have an answer. Null is a driver that cannot say, which is what every
        /// other test here uses and what the reminder must cope with (R58 finding 8).
        /// </summary>
        public UnitVec3? Position { get; set; }

        public bool TryGetPosition(out UnitVec3 position)
        {
            position = Position ?? default;
            return Position.HasValue;
        }

        public void StepCompleted(UnitStep step, int index, int count, UnitStepOutcome outcome, string reason)
        {
            Steps.Add(new StepRecord(step.Kind, index, count, outcome, reason));
            OnStepCompleted?.Invoke();
        }

        public void SequenceCompleted(UnitSequenceOutcome outcome, int stepsCompleted, int count, string reason)
        {
            var record = new SequenceRecord(outcome, stepsCompleted, count, reason);
            Sequence = record;
            SequenceHistory.Add(record);
            OnSequenceCompleted?.Invoke();
        }
    }
}
