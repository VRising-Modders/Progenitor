using System;
using System.Collections.Generic;
using Progenitor.Buffs;
using Xunit;
using BuffsApi = Progenitor.Buffs.Buffs;

namespace Progenitor.Tests;

/// <summary>
/// The timed-buff lifecycle, driven through the production <see cref="Buffs"/> module and the
/// production <see cref="BuffApplication"/> state machine with a fake world behind
/// <c>IBuffRuntime</c>. Nothing here re-implements a decision: every assertion is about what
/// <c>Apply</c> / <c>Poll</c> / <c>Dispose</c> actually did.
/// </summary>
[Collection("PluginLogger")]
public sealed class BuffLifecycleTests : IDisposable
{
    private const string BuffId = "test.buff";
    private const int Guid = 1234;

    private static readonly BuffEntityId Target = new(100, 1);
    private static readonly BuffEntityId Instance = new(200, 3);

    private readonly FakeBuffRuntime _runtime = new();
    private readonly object _owner = new PluginOwner();

    public BuffLifecycleTests()
    {
        BuffsApi.ResetForTests();
        _runtime.Register(BuffId, Guid);
        _runtime.AddEntity(Target, buffable: true);
        BuffsApi.SetRuntimeForTests(_runtime);
    }

    public void Dispose() => BuffsApi.ResetForTests();

    // -----------------------------------------------------------------
    // Application versus confirmed active
    // -----------------------------------------------------------------

    [Fact]
    public void Apply_returns_requested_not_active()
    {
        var application = Apply();

        Assert.Equal(BuffApplicationState.Requested, application.State);
        Assert.False(application.IsActive);
        Assert.True(application.Instance.IsNone);
        Assert.Equal(1, _runtime.ApplyCalls);
    }

    [Fact]
    public void Requested_becomes_active_only_once_the_instance_is_observed()
    {
        var application = Apply();

        BuffsApi.TickForTests();
        Assert.Equal(BuffApplicationState.Requested, application.State);

        _runtime.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();

        Assert.Equal(BuffApplicationState.Active, application.State);
        Assert.True(application.IsActive);
        Assert.Equal(Instance, application.Instance);
    }

    [Fact]
    public void Confirm_writes_the_requested_duration_onto_the_instance()
    {
        var application = Apply(seconds: 30f);
        _runtime.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();

        Assert.Equal(BuffApplicationState.Active, application.State);
        Assert.Equal(30f, _runtime.DurationOf(Instance));
    }

    [Fact]
    public void An_untimed_clone_keeps_its_own_duration_and_the_handle_stops_claiming_one()
    {
        _runtime.Timed = false;
        var application = Apply(seconds: 30f);
        _runtime.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();

        // The buff is on; only the timer is the clone's. The handle must not keep reporting a
        // duration nothing established.
        Assert.Equal(BuffApplicationState.Active, application.State);
        Assert.Equal(BuffRequest.KeepPrefabDuration, application.RequestedDurationSeconds);
    }

    [Fact]
    public void An_apply_that_never_produces_an_instance_ends_as_not_confirmed()
    {
        var application = Apply();

        for (int i = 0; i <= BuffApplicationPolicy.ConfirmFrames; i++) BuffsApi.TickForTests();

        Assert.Equal(BuffApplicationState.NotConfirmed, application.State);
        Assert.True(application.Instance.IsNone);
        Assert.Equal(0, BuffsApi.LiveCountForTests);
    }

    // -----------------------------------------------------------------
    // Expiry and removal
    // -----------------------------------------------------------------

    [Fact]
    public void An_instance_that_disappears_by_itself_ends_and_is_not_called_removed()
    {
        var application = Active();

        _runtime.DestroyEntity(Instance);
        BuffsApi.TickForTests();

        Assert.Equal(BuffApplicationState.Ended, application.State);
        Assert.Equal(0, _runtime.DestroyCalls);
    }

    /// <summary>
    /// This test used to assert <c>Assert.False(_runtime.Exists(Instance))</c> immediately after
    /// <c>Remove()</c> — i.e. "the destroy makes the entity gone now". That is the one belief the
    /// real game never satisfies: <c>DestroyUtility.Destroy</c> raises an event and the entity
    /// survives at least until a later system runs.
    ///
    /// <para>The fake modeled the wrong thing to match, and that mismatch is exactly why the
    /// out-of-band <c>DestroyTag</c> defect was unreachable offline. With deferral now the fake's
    /// default, the assertion moves to what is actually true and provable: the destroy was issued
    /// once, and the entity goes on a later frame.</para>
    /// </summary>
    [Fact]
    public void Remove_destroys_the_instance_this_application_owns()
    {
        var application = Active();

        application.Remove();

        Assert.Equal(BuffApplicationState.Removed, application.State);
        Assert.Equal(new[] { Instance }, _runtime.Destroyed);

        // Issued, not yet gone.
        Assert.True(_runtime.Exists(Instance));

        TickFrame();
        Assert.False(_runtime.Exists(Instance));
    }

    [Fact]
    public void Repeated_removal_is_safe_and_destroys_nothing_twice()
    {
        var application = Active();

        application.Remove();
        application.Remove();
        application.Dispose();
        BuffsApi.TickForTests();

        Assert.Equal(BuffApplicationState.Removed, application.State);
        Assert.Single(_runtime.Destroyed);
    }

    [Fact]
    public void Removal_after_the_buff_already_ended_changes_nothing()
    {
        var application = Active();
        _runtime.DestroyEntity(Instance);
        BuffsApi.TickForTests();
        Assert.Equal(BuffApplicationState.Ended, application.State);

        application.Remove();

        Assert.Equal(BuffApplicationState.Ended, application.State);
        Assert.Empty(_runtime.Destroyed);
    }

    /// <summary>
    /// Rewritten for the remaining-duration contract (BUFFREFRESH). It used to assert
    /// <c>DurationOf == 20</c> after <c>Refresh(20)</c> — i.e. that the argument lands in
    /// <c>LifeTime.Duration</c> verbatim. That is the old total-lifetime meaning, and it is what
    /// made a refresh shorter than the elapsed time end the buff on the spot.
    ///
    /// <para><c>Refresh(n)</c> now means "leave n seconds remaining", so the value written is
    /// <c>elapsed + n</c>. <c>RequestedDurationSeconds</c> is unchanged: it has always meant what the
    /// caller asked for, which is still 20.</para>
    /// </summary>
    [Fact]
    public void Refresh_rewrites_the_live_duration_only_while_active()
    {
        var application = Apply(seconds: 10f);
        Assert.False(application.Refresh(20f));

        _runtime.CreateInstance(Target, Guid, Instance);
        TickFrame();                      // one frame elapses, so the instance's Age is 1
        Assert.Equal(1f, _runtime.AgeOf(Instance));

        Assert.True(application.Refresh(20f));
        Assert.Equal(21f, _runtime.DurationOf(Instance));   // 1 elapsed + 20 remaining
        Assert.Equal(20f, application.RequestedDurationSeconds);
        Assert.Equal(BuffRefusal.None, application.LastRefreshRefusal);
    }

    // -----------------------------------------------------------------
    // Cancellation during initialization
    // -----------------------------------------------------------------

    [Fact]
    public void Cancelling_before_confirmation_destroys_an_instance_that_arrives_late()
    {
        var application = Apply();
        application.Remove();

        // Still Requested: the apply was queued and the module must keep watching.
        Assert.Equal(BuffApplicationState.Requested, application.State);

        _runtime.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();

        Assert.Equal(BuffApplicationState.Removed, application.State);
        Assert.Equal(new[] { Instance }, _runtime.Destroyed);
        Assert.Equal(0, BuffsApi.LiveCountForTests);
    }

    [Fact]
    public void A_cancelled_application_is_not_handed_back_by_a_query()
    {
        var application = Apply();
        application.Remove();

        // It still holds the slot (the queued apply is in flight), but it is nobody's to act on.
        Assert.Null(BuffsApi.Find(BuffId, Target));
        Assert.Empty(BuffsApi.Active(_owner));
    }

    [Fact]
    public void Applying_while_a_cancel_is_in_flight_is_refused_rather_than_racing_it()
    {
        var first = Apply();
        first.Remove();

        var second = Apply();

        Assert.Equal(BuffApplicationState.Refused, second.State);
        Assert.Equal(BuffRefusal.CancelInFlight, second.Refusal);
        Assert.Equal(1, _runtime.ApplyCalls);
    }

    [Fact]
    public void Refreshing_a_cancelled_application_is_refused_and_never_returns_that_handle()
    {
        var first = Apply(seconds: 10f);
        first.Remove();

        var second = BuffsApi.Apply(
            Request(seconds: 60f).WithRefresh(BuffRefreshPolicy.Refresh), _owner, "test.plugin");

        Assert.NotSame(first, second);
        Assert.Equal(BuffRefusal.CancelInFlight, second.Refusal);

        // And the cancel still does its job: the late instance is destroyed, not adopted.
        _runtime.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();
        Assert.Equal(BuffApplicationState.Removed, first.State);
        Assert.Equal(new[] { Instance }, _runtime.Destroyed);
    }

    [Fact]
    public void The_slot_is_free_again_once_the_cancel_resolves()
    {
        var first = Apply();
        first.Remove();
        for (int i = 0; i <= BuffApplicationPolicy.ConfirmFrames; i++) BuffsApi.TickForTests();
        Assert.Equal(BuffApplicationState.Removed, first.State);

        var second = Apply();

        Assert.Equal(BuffApplicationState.Requested, second.State);
        Assert.Equal(2, _runtime.ApplyCalls);
    }

    [Fact]
    public void Cancelling_before_confirmation_ends_cleanly_when_nothing_ever_arrives()
    {
        var application = Apply();
        application.Remove();

        for (int i = 0; i <= BuffApplicationPolicy.ConfirmFrames; i++) BuffsApi.TickForTests();

        Assert.Equal(BuffApplicationState.Removed, application.State);
        Assert.Empty(_runtime.Destroyed);
    }

    // -----------------------------------------------------------------
    // Target invalidation: death, disconnect, world replacement
    // -----------------------------------------------------------------

    [Fact]
    public void A_destroyed_target_ends_the_application_without_a_write()
    {
        var application = Active();

        // Character death / despawn: the entity goes and its buffs go with it.
        _runtime.DestroyEntity(Target);
        _runtime.DestroyEntity(Instance);
        BuffsApi.TickForTests();

        Assert.Equal(BuffApplicationState.TargetLost, application.State);
        Assert.Empty(_runtime.Destroyed);
    }

    [Fact]
    public void A_disconnect_that_keeps_the_character_entity_does_not_end_the_application()
    {
        var application = Active();

        // A disconnect does not destroy the character entity, so nothing about this application
        // changes. The buff is the game's to keep or clear.
        BuffsApi.TickForTests();
        BuffsApi.TickForTests();

        Assert.Equal(BuffApplicationState.Active, application.State);
    }

    [Fact]
    public void A_world_reload_stops_the_application_touching_anything()
    {
        var application = Active();

        _runtime.Epoch++;
        BuffsApi.TickForTests();

        Assert.Equal(BuffApplicationState.TargetLost, application.State);
        Assert.Empty(_runtime.Destroyed);

        // And a removal after the world moved still writes nothing: the stamped index now names
        // a different entity.
        application.Remove();
        Assert.Empty(_runtime.Destroyed);
    }

    [Fact]
    public void Removing_after_a_world_reload_destroys_nothing()
    {
        var application = Active();

        _runtime.Epoch++;
        application.Remove();

        Assert.Equal(BuffApplicationState.Removed, application.State);
        Assert.Empty(_runtime.Destroyed);
        Assert.True(_runtime.Exists(Instance));
    }

    // -----------------------------------------------------------------
    // Two owners
    // -----------------------------------------------------------------

    [Fact]
    public void A_second_owner_is_refused_the_same_buff_on_the_same_target()
    {
        var first = Apply();
        var second = BuffsApi.Apply(Request(), new OtherPluginOwner(), "other.plugin");

        Assert.Equal(BuffApplicationState.Refused, second.State);
        Assert.Equal(BuffRefusal.AlreadyOwned, second.Refusal);
        Assert.Contains("one application owns one instance", second.RefusalReason);
        Assert.Equal(BuffApplicationState.Requested, first.State);
        Assert.Equal(1, _runtime.ApplyCalls);
    }

    [Fact]
    public void Two_owners_with_different_buffs_on_one_target_both_apply_and_remove_independently()
    {
        const string otherId = "test.buff.other";
        const int otherGuid = 5678;
        _runtime.Register(otherId, otherGuid);
        var otherOwner = new OtherPluginOwner();

        var mine = Active();
        var theirs = BuffsApi.Apply(
            BuffRequest.For(otherId, Target), otherOwner, "other.plugin");
        var otherInstance = new BuffEntityId(201, 1);
        _runtime.CreateInstance(Target, otherGuid, otherInstance);
        BuffsApi.TickForTests();

        Assert.Equal(BuffApplicationState.Active, theirs.State);

        mine.Remove();

        // Only this application's instance went. The other owner's contribution is untouched,
        // which is the whole point: each instance is reversed by the game on its own id.
        Assert.Equal(new[] { Instance }, _runtime.Destroyed);
        Assert.Equal(BuffApplicationState.Active, theirs.State);
        Assert.True(_runtime.Exists(otherInstance));
    }

    [Fact]
    public void The_same_owner_applying_twice_is_refused_by_default()
    {
        var first = Active();
        var second = BuffsApi.Apply(Request(seconds: 5f), _owner, "test.plugin");

        Assert.Equal(BuffApplicationState.Refused, second.State);
        Assert.Equal(BuffRefusal.AlreadyActive, second.Refusal);
        Assert.Equal(BuffApplicationState.Active, first.State);
        Assert.Equal(1, _runtime.ApplyCalls);
    }

    [Fact]
    public void Refresh_policy_returns_the_live_application_rather_than_a_second_owner()
    {
        var first = Active(seconds: 10f);
        var second = BuffsApi.Apply(
            Request(seconds: 45f).WithRefresh(BuffRefreshPolicy.Refresh), _owner, "test.plugin");

        Assert.Same(first, second);
        // BUFFREFRESH: elapsed + remaining. Active() burns one frame, so Age is 1 and 45 remaining
        // means a written total of 46. The review named the other stale assertion; this is the
        // second one, found by re-reading every DurationOf assertion in the file.
        Assert.Equal(1f, _runtime.AgeOf(Instance));
        Assert.Equal(46f, _runtime.DurationOf(Instance));
        Assert.Equal(1, _runtime.ApplyCalls);
    }

    [Fact]
    public void Refreshing_before_confirmation_carries_the_new_duration_into_the_confirm()
    {
        var first = Apply(seconds: 30f);
        var second = BuffsApi.Apply(
            Request(seconds: 120f).WithRefresh(BuffRefreshPolicy.Refresh), _owner, "test.plugin");

        Assert.Same(first, second);
        Assert.Equal(120f, first.RequestedDurationSeconds);

        _runtime.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();

        // The value that lands is the one the caller last asked for, not the first call's.
        Assert.Equal(120f, _runtime.DurationOf(Instance));
    }

    // -----------------------------------------------------------------
    // Refusal
    // -----------------------------------------------------------------

    [Fact]
    public void The_client_is_refused_before_any_write()
    {
        _runtime.IsServer = false;
        var application = Apply();

        Assert.Equal(BuffRefusal.NotServer, application.Refusal);
        Assert.Equal(BuffApplicationState.Refused, application.State);
        Assert.Equal(0, _runtime.ApplyCalls);
        Assert.Equal(0, BuffsApi.LiveCountForTests);
    }

    [Fact]
    public void No_world_is_refused_before_the_buff_is_even_looked_up()
    {
        _runtime.HasWorld = false;
        var application = Apply();

        Assert.Equal(BuffRefusal.NoWorld, application.Refusal);
        Assert.Equal(0, _runtime.ResolveCalls);
    }

    [Fact]
    public void An_unregistered_buff_id_is_refused_with_the_call_that_fixes_it()
    {
        var application = BuffsApi.Apply(BuffRequest.For("nobody.knows", Target), _owner, "test.plugin");

        Assert.Equal(BuffRefusal.BuffNotRegistered, application.Refusal);
        Assert.Contains("DefineBuff", application.RefusalReason);
        Assert.Equal(0, _runtime.ApplyCalls);
    }

    [Fact]
    public void A_registration_is_not_an_application()
    {
        _runtime.Applied = false; // registered, clone not in the world yet

        var application = Apply();

        Assert.Equal(BuffRefusal.BuffNotApplied, application.Refusal);
        Assert.Contains("a registration is not an application", application.RefusalReason);
        Assert.Equal(0, _runtime.ApplyCalls);
    }

    [Fact]
    public void A_missing_target_is_refused()
    {
        var application = BuffsApi.Apply(BuffRequest.For(BuffId, new BuffEntityId(999, 1)), _owner, "test.plugin");

        Assert.Equal(BuffRefusal.TargetMissing, application.Refusal);
        Assert.Equal(0, _runtime.ApplyCalls);
    }

    [Fact]
    public void A_target_without_a_buff_buffer_is_refused_and_told_why()
    {
        var user = new BuffEntityId(300, 1);
        _runtime.AddEntity(user, buffable: false);

        var application = BuffsApi.Apply(BuffRequest.For(BuffId, user), _owner, "test.plugin");

        Assert.Equal(BuffRefusal.TargetNotBuffable, application.Refusal);
        Assert.Contains("BuffBuffer", application.RefusalReason);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(BuffApplicationPolicy.MaxDurationSeconds + 1f)]
    public void A_duration_outside_the_supported_domain_is_refused(float seconds)
    {
        var application = BuffsApi.Apply(
            BuffRequest.For(BuffId, Target).WithDuration(seconds), _owner, "test.plugin");

        Assert.Equal(BuffRefusal.BadDuration, application.Refusal);
        Assert.Equal(0, _runtime.ApplyCalls);
    }

    [Fact]
    public void An_instance_no_application_created_is_never_adopted()
    {
        // A save restores live buff instances (evidence 109), so a clone's instance can be on the
        // target with nobody holding it. Adopting it would give this application a buff it did not
        // create to retime and, on removal, destroy.
        _runtime.CreateInstance(Target, Guid, Instance);

        var application = Apply(seconds: 30f);

        Assert.Equal(BuffRefusal.AlreadyPresent, application.Refusal);
        Assert.Contains("never adopts an instance it did not create", application.RefusalReason);
        Assert.Equal(0, _runtime.ApplyCalls);

        // And it is left exactly alone.
        BuffsApi.TickForTests();
        Assert.True(_runtime.Exists(Instance));
        Assert.Empty(_runtime.Destroyed);
    }

    [Fact]
    public void A_module_that_is_not_installed_refuses_before_it_reads_the_world()
    {
        BuffsApi.SetInstalledForTests(false);

        var application = Apply();

        Assert.Equal(BuffRefusal.ModuleUnavailable, application.Refusal);
        Assert.Equal(0, _runtime.ResolveCalls);
        Assert.Equal(0, _runtime.ApplyCalls);
    }

    [Fact]
    public void An_apply_path_the_world_does_not_have_is_refused_rather_than_reported_as_applied()
    {
        _runtime.ApplyPathAvailable = false;

        var application = Apply();

        Assert.Equal(BuffRefusal.ApplyPathUnavailable, application.Refusal);
        Assert.Equal(0, BuffsApi.LiveCountForTests);
    }

    [Fact]
    public void Every_refusal_has_an_actionable_sentence()
    {
        foreach (BuffRefusal refusal in Enum.GetValues(typeof(BuffRefusal)))
        {
            if (refusal == BuffRefusal.None) continue;
            string text = BuffRefusalText.For(refusal);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual("refused", text);
            Assert.True(text.Length > 40, refusal + " has no actionable sentence: " + text);
        }
    }

    // -----------------------------------------------------------------
    // Unload
    // -----------------------------------------------------------------

    [Fact]
    public void Removing_this_owner_removes_only_this_owner_s_buffs()
    {
        const string otherId = "test.buff.other";
        const int otherGuid = 5678;
        _runtime.Register(otherId, otherGuid);
        var otherOwner = new OtherPluginOwner();

        var mine = Active();
        var theirs = BuffsApi.Apply(BuffRequest.For(otherId, Target), otherOwner, "other.plugin");
        _runtime.CreateInstance(Target, otherGuid, new BuffEntityId(201, 1));
        BuffsApi.TickForTests();

        BuffsApi.RemoveAll(_owner);

        Assert.Equal(BuffApplicationState.Removed, mine.State);
        Assert.Equal(BuffApplicationState.Active, theirs.State);
    }

    [Fact]
    public void RemoveAll_does_not_touch_another_subsystem_of_the_same_plugin()
    {
        var subsystemA = new PluginOwner();
        var subsystemB = new PluginOwner();
        var a = BuffsApi.Apply(Request(), subsystemA, "test.plugin");
        _runtime.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();

        const string otherId = "test.buff.other";
        _runtime.Register(otherId, 5678);
        var b = BuffsApi.Apply(BuffRequest.For(otherId, Target), subsystemB, "test.plugin");
        _runtime.CreateInstance(Target, 5678, new BuffEntityId(202, 1));
        BuffsApi.TickForTests();

        BuffsApi.RemoveAll(subsystemA);

        Assert.Equal(BuffApplicationState.Removed, a.State);
        Assert.Equal(BuffApplicationState.Active, b.State);
    }

    [Fact]
    public void Plugin_unload_sweeps_by_plugin_id_even_with_a_bare_owner_token()
    {
        var token = new object();
        var application = BuffsApi.Apply(Request(), token, "test.plugin");
        _runtime.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();
        Assert.Equal(BuffApplicationState.Active, application.State);

        BuffsApi.SweepForUnload(new PluginOwner(), "test.plugin");

        Assert.Equal(BuffApplicationState.Removed, application.State);
        Assert.Equal(new[] { Instance }, _runtime.Destroyed);
    }

    /// <summary>
    /// The third-party half of the unload sweep (integration review F1). <c>Buffs.Find</c> is
    /// public and documented as finding an application <i>whoever owns it</i>, so one plugin
    /// subscribing to another plugin's application is an invited use. Before the fix, the sweep
    /// only visited applications the unloading plugin owned, so such a handler survived the unload,
    /// pinned the subscriber's collectible ALC for as long as the stranger's buff stayed live, and
    /// was later invoked in a torn-down generation.
    ///
    /// <para>The stranger's owner is a BCL object on purpose: <c>OwnedByUnloading</c>'s middle leg
    /// compares owner assemblies, and every owner type declared in this test assembly would match
    /// the unloading plugin's, which is exactly the collision that cannot happen between two real
    /// plugins. A <see cref="object"/> owner is declared in System.Private.CoreLib, so this
    /// application is genuinely not the sweeper's — which is the case under test.</para>
    ///
    /// <para>The assertions are both halves: the handler is gone, <b>and</b> the stranger's
    /// application is still live and still transitions. Dropping a third party's application
    /// outright would also make the first assertion pass, and would be wrong.</para>
    /// </summary>
    [Fact]
    public void Plugin_unload_drops_its_handlers_from_another_owners_application_and_leaves_it_running()
    {
        object stranger = new();
        var application = BuffsApi.Apply(Request(), stranger, "other.plugin");
        _runtime.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();
        Assert.Equal(BuffApplicationState.Active, application.State);

        int calls = 0;
        application.OnStateChanged += _ => calls++;

        BuffsApi.SweepForUnload(new PluginOwner(), "test.plugin");

        // Not the sweeper's application: it keeps running, and nothing was destroyed for it.
        Assert.Equal(BuffApplicationState.Active, application.State);
        Assert.Empty(_runtime.Destroyed);

        // ...but the handler this assembly wrote on it is gone, so the ALC is not pinned.
        _runtime.DestroyEntity(Instance);
        BuffsApi.TickForTests();
        Assert.Equal(BuffApplicationState.Ended, application.State);
        Assert.Equal(0, calls);
    }

    /// <summary>
    /// The negative control for the test above: the new sweep drops handlers written in the
    /// <i>unloading</i> assembly and nothing else. Without this, a sweep that simply cleared every
    /// handler on every application would pass the test above while silently breaking every other
    /// plugin still running.
    ///
    /// <para>The roles of the two owners are swapped deliberately. The unloading plugin is the BCL
    /// object, so the assembly being swept is System.Private.CoreLib; the application's owner and
    /// the handler are both declared here, in the test assembly. So the application is not the
    /// sweeper's by any of the three ownership routes, and its handler was not written in the
    /// assembly going away — it must survive untouched.</para>
    /// </summary>
    [Fact]
    public void Plugin_unload_does_not_drop_handlers_written_in_some_other_assembly()
    {
        var application = BuffsApi.Apply(Request(), new OtherPluginOwner(), "other.plugin");
        _runtime.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();
        Assert.Equal(BuffApplicationState.Active, application.State);

        int calls = 0;
        application.OnStateChanged += _ => calls++;

        BuffsApi.SweepForUnload(new object(), "unrelated.plugin");

        Assert.Equal(BuffApplicationState.Active, application.State);
        _runtime.DestroyEntity(Instance);
        BuffsApi.TickForTests();
        Assert.Equal(BuffApplicationState.Ended, application.State);
        Assert.Equal(1, calls);
    }

    /// <summary>
    /// The tracked-release contract `ctx.Buffs.Apply` relies on (integration review F4): the buff is
    /// still removed, and the owner's own handler is not run. `PluginContextBuffs` is
    /// `#if !PROGENITOR_TESTS`, so the `Track` wiring itself is compile-checked through the sample
    /// build; this covers the object it tracks, which is where the behavior lives — the same split
    /// the creature lane documents as its residual 5.
    /// </summary>
    [Fact]
    public void The_tracked_unload_release_removes_the_buff_silently_and_only_once()
    {
        var application = Active();
        int calls = 0;
        application.OnStateChanged += _ => calls++;

        var release = BuffApplication.UnloadReleaseFor(application);
        release.Dispose();

        Assert.Equal(BuffApplicationState.Removed, application.State);
        Assert.Equal(new[] { Instance }, _runtime.Destroyed);
        Assert.Equal(0, calls);

        release.Dispose();
        Assert.Equal(new[] { Instance }, _runtime.Destroyed);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Plugin_unload_drops_state_handlers_before_it_removes()
    {
        var application = Active();
        int calls = 0;
        application.OnStateChanged += _ => calls++;

        BuffsApi.SweepForUnload(_owner, "test.plugin");

        Assert.Equal(BuffApplicationState.Removed, application.State);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Plugin_unload_of_an_unconfirmed_apply_still_destroys_a_late_instance()
    {
        var application = Apply();

        BuffsApi.SweepForUnload(_owner, "test.plugin");
        Assert.Equal(BuffApplicationState.Requested, application.State);

        _runtime.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();

        Assert.Equal(BuffApplicationState.Removed, application.State);
        Assert.Equal(new[] { Instance }, _runtime.Destroyed);
    }

    [Fact]
    public void Core_uninstall_finishes_every_live_application_and_leaves_no_instance()
    {
        var active = Active();
        var second = new BuffEntityId(101, 1);
        _runtime.AddEntity(second, buffable: true);
        var pending = BuffsApi.Apply(BuffRequest.For(BuffId, second), _owner, "test.plugin");
        Assert.Equal(BuffApplicationState.Requested, pending.State);

        // The game builds the instance for the queued apply, but Core goes before the next poll.
        var lateInstance = new BuffEntityId(203, 1);
        _runtime.CreateInstance(second, Guid, lateInstance);

        BuffsApi.Uninstall();

        Assert.Equal(BuffApplicationState.Removed, active.State);
        Assert.Equal(BuffApplicationState.Removed, pending.State);
        Assert.Contains(Instance, _runtime.Destroyed);
        Assert.Contains(lateInstance, _runtime.Destroyed);
        Assert.Equal(0, BuffsApi.LiveCountForTests);
    }

    [Fact]
    public void Core_uninstall_says_so_when_an_unconfirmed_apply_cannot_be_watched_any_longer()
    {
        var log = new TestLogSource();
        Progenitor.Plugin.Logger = log;
        try
        {
            Apply();
            BuffsApi.Uninstall();

            Assert.Contains(log.Warnings, w => w.Contains("no Progenitor generation will be watching"));
        }
        finally
        {
            Progenitor.Plugin.Logger = null;
        }
    }

    // -----------------------------------------------------------------
    // Query
    // -----------------------------------------------------------------

    [Fact]
    public void Find_returns_the_live_application_and_nothing_once_it_ends()
    {
        var application = Active();

        Assert.Same(application, BuffsApi.Find(BuffId, Target));

        application.Remove();

        Assert.Null(BuffsApi.Find(BuffId, Target));
    }

    [Fact]
    public void Active_lists_only_this_owner_s_live_applications()
    {
        var application = Active();

        Assert.Equal(new IBuffApplication[] { application }, BuffsApi.Active(_owner));
        Assert.Empty(BuffsApi.Active(new OtherPluginOwner()));

        application.Remove();
        Assert.Empty(BuffsApi.Active(_owner));
    }

    [Fact]
    public void State_changes_are_published_once_each_and_stop_at_the_terminal_state()
    {
        var application = Apply();
        var seen = new List<BuffApplicationState>();
        application.OnStateChanged += a => seen.Add(a.State);

        _runtime.CreateInstance(Target, Guid, Instance);
        BuffsApi.TickForTests();
        _runtime.DestroyEntity(Instance);
        BuffsApi.TickForTests();
        BuffsApi.TickForTests();

        Assert.Equal(
            new[] { BuffApplicationState.Active, BuffApplicationState.Ended },
            seen);
    }

    [Fact]
    public void A_throwing_state_handler_does_not_stop_the_lifecycle()
    {
        var log = new TestLogSource();
        Progenitor.Plugin.Logger = log;
        try
        {
            var application = Apply();
            application.OnStateChanged += _ => throw new InvalidOperationException("boom");

            _runtime.CreateInstance(Target, Guid, Instance);
            BuffsApi.TickForTests();

            Assert.Equal(BuffApplicationState.Active, application.State);
            Assert.Contains(log.Warnings, w => w.Contains("OnStateChanged"));
        }
        finally
        {
            Progenitor.Plugin.Logger = null;
        }
    }

    [Fact]
    public void A_throwing_state_handler_does_not_swallow_the_handlers_after_it()
    {
        var log = new TestLogSource();
        Progenitor.Plugin.Logger = log;
        try
        {
            var application = Apply();
            int later = 0;
            application.OnStateChanged += _ => throw new InvalidOperationException("boom");
            application.OnStateChanged += _ => later++;

            _runtime.CreateInstance(Target, Guid, Instance);
            BuffsApi.TickForTests();

            // One plugin's broken handler must not stop another plugin learning the buff went on.
            Assert.Equal(1, later);
            Assert.Contains(log.Warnings, w => w.Contains("OnStateChanged"));
        }
        finally
        {
            Progenitor.Plugin.Logger = null;
        }
    }

    // -----------------------------------------------------------------
    // BUFFREFRESH: Refresh(n) means "leave n seconds REMAINING"
    //
    // LifeTime.Duration is the TOTAL lifetime and the game counts elapsed time in a separate Age
    // component, so the value to write is elapsed + n. Refresh used to write n straight into
    // Duration, which meant a refresh shorter than the time already elapsed ended the buff on the
    // spot - observed live 2026-09-13 (`.rested 90`, then `.rested 30` at about +31 s).
    //
    // The offline suite could not see it because the fake had no notion of elapsed time at all. The
    // fake now ages instances and expires them at age >= duration, so these tests fail on the old
    // implementation rather than passing vacuously.
    // -----------------------------------------------------------------

    [Fact]
    public void Refresh_sets_the_remaining_time_not_the_total()
    {
        var application = Active(seconds: 90f);
        _runtime.SetAge(Instance, 31f);

        Assert.True(application.Refresh(30f));

        Assert.Equal(61f, _runtime.DurationOf(Instance));            // 31 elapsed + 30 remaining
        Assert.Equal(30f, application.RequestedDurationSeconds);     // what the caller asked for
        Assert.Equal(BuffRefusal.None, application.LastRefreshRefusal);
    }

    /// <summary>
    /// The live regression, phrased as behavior rather than as a number: after asking for 30 more
    /// seconds the buff must still be there 30 seconds later. On the old implementation Duration
    /// becomes 30 while Age is already 31, and the very next frame ends it.
    /// </summary>
    [Fact]
    public void Refresh_shorter_than_the_elapsed_time_does_not_end_the_buff()
    {
        var application = Active(seconds: 90f);
        _runtime.SetAge(Instance, 31f);

        Assert.True(application.Refresh(30f));

        TickFrame();
        Assert.Equal(BuffApplicationState.Active, application.State);
        Assert.True(_runtime.Exists(Instance));

        // ...and it lasts the 30 seconds that were asked for, not some other number.
        for (int i = 0; i < 28; i++) TickFrame();
        Assert.Equal(BuffApplicationState.Active, application.State);
        TickFrame();
        Assert.Equal(BuffApplicationState.Ended, application.State);
    }

    [Fact]
    public void Refresh_reads_elapsed_from_the_instance_not_from_a_module_clock()
    {
        var application = Active(seconds: 90f);
        _runtime.AgePerFrame = 0f;              // module frames pass; the instance's clock does not
        TickFrame();
        TickFrame();
        TickFrame();
        _runtime.SetAge(Instance, 50f);         // the instance's own clock says 50

        Assert.True(application.Refresh(10f));

        // 50 + 10. A module that counted its own polls would write 4 + 10 or 1 + 10.
        Assert.Equal(60f, _runtime.DurationOf(Instance));
    }

    /// <summary>
    /// Refreshing repeatedly must converge on "n seconds from now", not accumulate. This is the
    /// property a naive `Duration += n` would break, and it is worth pinning because the correct
    /// formula and the wrong one agree on the single-refresh case.
    /// </summary>
    [Fact]
    public void Repeated_refreshes_do_not_ratchet_the_total_upward()
    {
        var application = Active(seconds: 600f);

        for (int i = 0; i < 5; i++)
        {
            Assert.True(application.Refresh(30f));
            Assert.Equal(_runtime.AgeOf(Instance) + 30f, _runtime.DurationOf(Instance));
            TickFrame();
        }

        // Five refreshes of 30 s each, one frame apart: still ~30 s of life left, never 150.
        Assert.Equal(_runtime.AgeOf(Instance) + 29f, _runtime.DurationOf(Instance));
    }

    // ---- refusals, each of which must write NOTHING ------------------------------------------

    [Fact]
    public void Refresh_is_refused_when_the_total_would_exceed_the_networked_range()
    {
        var application = Active(seconds: 9000f);
        _runtime.SetAge(Instance, 9990f);
        float before = _runtime.DurationOf(Instance);
        int writesBefore = _runtime.SetDurationCalls;

        Assert.False(application.Refresh(30f));

        // Refused, not clamped and not "wrote the same value": SetDuration was never reached.
        Assert.Equal(writesBefore, _runtime.SetDurationCalls);
        Assert.Equal(before, _runtime.DurationOf(Instance));
        Assert.Equal(BuffRefusal.RefreshTotalOutOfRange, application.LastRefreshRefusal);
    }

    /// <summary>
    /// The boundary itself is legal. LifeTime.Duration is SyncRangedFloat(0, 10000), so a total of
    /// exactly 10000 is in range - an off-by-one that refused it would be a real, silent shortening
    /// of the longest buffs the API allows.
    /// </summary>
    [Fact]
    public void A_total_of_exactly_the_range_ceiling_is_allowed()
    {
        var application = Active(seconds: 9000f);
        _runtime.SetAge(Instance, 10f);

        Assert.True(application.Refresh(BuffApplicationPolicy.MaxDurationSeconds - 10f));

        Assert.Equal(BuffApplicationPolicy.MaxDurationSeconds, _runtime.DurationOf(Instance));
    }

    [Fact]
    public void Refresh_is_refused_when_the_instance_timing_cannot_be_read()
    {
        var application = Active(seconds: 90f);
        _runtime.NoTiming.Add(Instance);
        int writesBefore = _runtime.SetDurationCalls;

        Assert.False(application.Refresh(30f));

        // Fails closed: the buff keeps the duration it had rather than getting a guessed one.
        Assert.Equal(writesBefore, _runtime.SetDurationCalls);
        Assert.Equal(90f, _runtime.DurationOf(Instance));
        Assert.Equal(BuffRefusal.RefreshTimingUnavailable, application.LastRefreshRefusal);
    }

    /// <summary>
    /// An untimed clone has no LifeTime to read, so it now refuses on the READ rather than on the
    /// write. The outcome a caller cares about is unchanged - nothing is written and the clone keeps
    /// its own timer - but the reason value moves from <c>NotTimed</c> to
    /// <c>RefreshTimingUnavailable</c>, which is recorded here so the change is deliberate and not a
    /// silent drift.
    /// </summary>
    [Fact]
    public void Refresh_on_an_untimed_clone_is_refused_and_writes_nothing()
    {
        var application = Active();
        _runtime.Timed = false;
        int writesBefore = _runtime.SetDurationCalls;

        Assert.False(application.Refresh(30f));

        Assert.Equal(writesBefore, _runtime.SetDurationCalls);
        Assert.Equal(BuffRefusal.RefreshTimingUnavailable, application.LastRefreshRefusal);
    }

    /// <summary>
    /// Under <c>RemoveBuffStack</c>, <c>Duration</c> is the length of one stack PERIOD rather than a
    /// lifetime: the game removes a stack and restarts the counter. Writing elapsed + remaining into
    /// it would lengthen every later period, so repeated refreshes would ratchet upward with no
    /// ceiling below the range guard. This API refuses that case instead of guessing at it.
    /// </summary>
    [Fact]
    public void Refresh_is_refused_when_the_duration_is_a_stack_period_rather_than_a_lifetime()
    {
        var application = Active(seconds: 90f);
        _runtime.EndAction = BuffLifetimeEnd.RemoveBuffStack;
        int writesBefore = _runtime.SetDurationCalls;

        Assert.False(application.Refresh(30f));

        Assert.Equal(writesBefore, _runtime.SetDurationCalls);
        Assert.Equal(90f, _runtime.DurationOf(Instance));
        Assert.Equal(BuffRefusal.RefreshEndActionUnsupported, application.LastRefreshRefusal);
    }

    [Fact]
    public void Refresh_is_refused_when_the_end_action_is_one_this_build_does_not_recognize()
    {
        var application = Active(seconds: 90f);
        _runtime.EndAction = BuffLifetimeEnd.Unknown;

        Assert.False(application.Refresh(30f));

        Assert.Equal(BuffRefusal.RefreshEndActionUnsupported, application.LastRefreshRefusal);
    }

    // The three end actions where Duration IS the instance's lifetime all refresh normally. These
    // keep the refusal above narrow: without them, "refuse RemoveBuffStack" could be satisfied by an
    // implementation that refuses everything.
    //
    // Three facts rather than one [Theory], because BuffLifetimeEnd is internal and an xUnit theory
    // method must be public - a public method cannot take an internal parameter type (CS0051).
    // Making the enum public to suit the test would widen the shipped surface for a test's
    // convenience; the seam is deliberately game-free and internal.

    [Fact]
    public void An_end_action_of_None_is_refreshed() => AssertLifetimeEndRefreshes(BuffLifetimeEnd.None);

    [Fact]
    public void An_end_action_of_Kill_is_refreshed() => AssertLifetimeEndRefreshes(BuffLifetimeEnd.Kill);

    [Fact]
    public void An_end_action_of_Destroy_is_refreshed() => AssertLifetimeEndRefreshes(BuffLifetimeEnd.Destroy);

    private void AssertLifetimeEndRefreshes(BuffLifetimeEnd end)
    {
        var application = Active(seconds: 90f);
        _runtime.EndAction = end;
        _runtime.SetAge(Instance, 5f);

        Assert.True(application.Refresh(30f));
        Assert.Equal(35f, _runtime.DurationOf(Instance));
        Assert.Equal(BuffRefusal.None, application.LastRefreshRefusal);
    }

    /// <summary>
    /// A non-finite elapsed value must be rejected ABOVE the seam. <c>float.NaN &gt; 10000f</c> is
    /// <b>false</b>, so a NaN total sails through a range check written as an upper bound and lands
    /// in LifeTime.Duration - where it never compares true against elapsed time, and the buff simply
    /// never ends. UnityBuffRuntime refuses a non-finite age too, but that file is excluded from the
    /// test assembly, and an invariant the suite cannot see is not an invariant.
    /// </summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void A_non_finite_elapsed_is_refused_above_the_seam(float elapsed)
    {
        var application = Active(seconds: 90f);
        _runtime.ElapsedOverride = elapsed;
        int writesBefore = _runtime.SetDurationCalls;

        Assert.False(application.Refresh(30f));

        Assert.Equal(writesBefore, _runtime.SetDurationCalls);
        Assert.Equal(90f, _runtime.DurationOf(Instance));
    }

    [Fact]
    public void A_negative_elapsed_never_produces_a_shorter_buff_than_was_asked_for()
    {
        var application = Active(seconds: 90f);
        _runtime.ElapsedOverride = -100f;

        // -100 + 30 is -70: a total that would end the buff instantly. Refused, not written.
        Assert.False(application.Refresh(30f));
        Assert.Equal(90f, _runtime.DurationOf(Instance));
        Assert.Equal(BuffRefusal.BadDuration, application.LastRefreshRefusal);
    }

    /// <summary>
    /// An argument the policy rejects must be rejected before the world is touched at all - not read
    /// and then discarded. The elapsed read is a world touch like any other.
    /// </summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(0f)]
    [InlineData(-5f)]
    [InlineData(20000f)]
    public void A_rejected_argument_never_touches_the_world(float seconds)
    {
        var application = Active(seconds: 90f);
        int readsBefore = _runtime.TimingReads;
        int writesBefore = _runtime.SetDurationCalls;

        Assert.False(application.Refresh(seconds));

        Assert.Equal(readsBefore, _runtime.TimingReads);
        Assert.Equal(writesBefore, _runtime.SetDurationCalls);
        Assert.Equal(BuffRefusal.BadDuration, application.LastRefreshRefusal);
    }

    [Fact]
    public void Refresh_after_a_world_reload_reads_nothing_and_writes_nothing()
    {
        var application = Active(seconds: 90f);
        int readsBefore = _runtime.TimingReads;
        int writesBefore = _runtime.SetDurationCalls;

        _runtime.Epoch++;

        Assert.False(application.Refresh(30f));

        // An epoch move means the recorded index may name a DIFFERENT entity, so reading through it
        // is exactly as wrong as writing through it.
        Assert.Equal(readsBefore, _runtime.TimingReads);
        Assert.Equal(writesBefore, _runtime.SetDurationCalls);
    }

    /// <summary>
    /// A refused refresh must not move <c>RequestedDurationSeconds</c>. The handle would otherwise
    /// report a duration nothing ever established - the same class of lie the untimed-clone case
    /// already guards against on the apply path.
    /// </summary>
    [Fact]
    public void A_refused_refresh_leaves_the_requested_duration_alone()
    {
        var application = Active(seconds: 90f);
        _runtime.NoTiming.Add(Instance);

        Assert.False(application.Refresh(30f));

        Assert.Equal(90f, application.RequestedDurationSeconds);
    }

    /// <summary>
    /// A write that did not take is a refusal, not a success. This is the one leg reachable after
    /// <c>TryReadTiming</c> has already succeeded — the instance is genuinely timed, and the
    /// component write itself throws — and it used to record <c>None</c>, which is the value that
    /// means "the refresh took". The player was told the buff had been extended while nothing had
    /// been written.
    /// </summary>
    [Fact]
    public void A_write_that_does_not_take_is_reported_as_a_refusal_not_as_success()
    {
        var application = Active(seconds: 90f);
        _runtime.SetAge(Instance, 31f);
        _runtime.WriteFails = true;

        Assert.False(application.Refresh(30f));

        Assert.Equal(BuffRefusal.RefreshWriteFailed, application.LastRefreshRefusal);
        Assert.NotEqual("", application.LastRefreshRefusalReason);
        // Nothing written, and the handle does not start claiming the duration it failed to write.
        Assert.Equal(90f, _runtime.DurationOf(Instance));
        Assert.Equal(90f, application.RequestedDurationSeconds);
    }

    /// <summary>
    /// The same failure seen through the route callers actually use. `State` stays `Active` and
    /// `Refusal` stays `None` — both describe the original apply — so `LastRefreshRefusal` is the
    /// only thing standing between a failed write and a success message.
    /// </summary>
    [Fact]
    public void The_ApplyOrRefresh_route_reports_a_failed_write_rather_than_success()
    {
        var first = Active(seconds: 90f);
        _runtime.WriteFails = true;

        var second = BuffsApi.Apply(
            Request(seconds: 30f).WithRefresh(BuffRefreshPolicy.Refresh), _owner, "test.plugin");

        Assert.Same(first, second);
        Assert.Equal(BuffApplicationState.Active, second.State);
        Assert.Equal(BuffRefusal.None, second.Refusal);
        Assert.Equal(BuffRefusal.RefreshWriteFailed, second.LastRefreshRefusal);
        Assert.Equal(90f, _runtime.DurationOf(Instance));
    }

    /// <summary>
    /// A refresh on a handle that is not live records why. It cannot be reached through
    /// `ApplyOrRefresh`, but a caller holding the handle can reach it, and the documented
    /// "read `LastRefreshRefusal` after a false" snippet would otherwise report `None` — no refusal —
    /// for a refresh that plainly did not happen.
    /// </summary>
    [Fact]
    public void Refreshing_a_handle_that_is_not_active_records_why()
    {
        var application = Active(seconds: 90f);
        application.Remove();
        Assert.Equal(BuffApplicationState.Removed, application.State);

        int readsBefore = _runtime.TimingReads;
        Assert.False(application.Refresh(30f));

        Assert.Equal(BuffRefusal.RefreshNotActive, application.LastRefreshRefusal);
        Assert.NotEqual("", application.LastRefreshRefusalReason);
        Assert.Equal(readsBefore, _runtime.TimingReads);   // and it touched no world
    }

    [Fact]
    public void A_successful_refresh_clears_an_earlier_refusal()
    {
        var application = Active(seconds: 90f);
        _runtime.NoTiming.Add(Instance);
        Assert.False(application.Refresh(30f));
        Assert.NotEqual(BuffRefusal.None, application.LastRefreshRefusal);

        _runtime.NoTiming.Remove(Instance);
        Assert.True(application.Refresh(30f));
        Assert.Equal(BuffRefusal.None, application.LastRefreshRefusal);
        Assert.Equal("", application.LastRefreshRefusalReason);
    }

    // ---- what the PUBLIC routes report -------------------------------------------------------

    /// <summary>
    /// The route callers actually use. <c>Buffs.Apply</c> with <c>BuffRefreshPolicy.Refresh</c>
    /// discards the bool <c>Refresh</c> returns and hands back the live application, whose
    /// <c>State</c> is <c>Active</c> and whose <c>Refusal</c> is <c>None</c> because both describe
    /// the ORIGINAL apply. A caller that reads only those two - which is what the sample command did
    /// - reports success for a refresh that wrote nothing. <c>LastRefreshRefusal</c> is what makes
    /// the difference visible without changing what State and Refusal mean.
    /// </summary>
    [Fact]
    public void The_ApplyOrRefresh_route_reports_a_refused_refresh_instead_of_claiming_success()
    {
        var first = Active(seconds: 90f);
        _runtime.NoTiming.Add(Instance);

        var second = BuffsApi.Apply(
            Request(seconds: 30f).WithRefresh(BuffRefreshPolicy.Refresh), _owner, "test.plugin");

        Assert.Same(first, second);
        Assert.Equal(BuffApplicationState.Active, second.State);   // still describes the apply
        Assert.Equal(BuffRefusal.None, second.Refusal);            // ...and so does this

        // The refresh, however, did not take, and the handle says so in an actionable sentence.
        Assert.Equal(BuffRefusal.RefreshTimingUnavailable, second.LastRefreshRefusal);
        Assert.NotEqual("", second.LastRefreshRefusalReason);
        Assert.Equal(90f, second.RequestedDurationSeconds);
        Assert.Equal(90f, _runtime.DurationOf(Instance));
    }

    [Fact]
    public void The_ApplyOrRefresh_route_reports_no_refusal_when_the_refresh_took()
    {
        var first = Active(seconds: 90f);

        var second = BuffsApi.Apply(
            Request(seconds: 30f).WithRefresh(BuffRefreshPolicy.Refresh), _owner, "test.plugin");

        Assert.Same(first, second);
        Assert.Equal(BuffRefusal.None, second.LastRefreshRefusal);
        Assert.Equal("", second.LastRefreshRefusalReason);
        Assert.Equal(31f, _runtime.DurationOf(Instance));   // 1 elapsed + 30 remaining
    }

    /// <summary>
    /// A refresh in the pending window has no instance to read, so it changes the requested duration
    /// and nothing else. It must not be recorded as a refusal either - there is nothing wrong with
    /// it, and the confirm that follows carries the new value.
    /// </summary>
    [Fact]
    public void A_refresh_before_confirmation_touches_no_instance_and_records_no_refusal()
    {
        var first = Apply(seconds: 30f);
        int readsBefore = _runtime.TimingReads;

        var second = BuffsApi.Apply(
            Request(seconds: 120f).WithRefresh(BuffRefreshPolicy.Refresh), _owner, "test.plugin");

        Assert.Same(first, second);
        Assert.Equal(readsBefore, _runtime.TimingReads);
        Assert.Equal(BuffRefusal.None, second.LastRefreshRefusal);
        Assert.Equal(120f, second.RequestedDurationSeconds);
    }

    /// <summary>
    /// Two buffs on one wearer with different elapsed times is the realistic overlap, and each must
    /// be computed from ITS OWN Age. A shared or module-level clock would make these agree.
    /// </summary>
    [Fact]
    public void Two_buffs_on_one_wearer_refresh_from_their_own_elapsed_times()
    {
        const string OtherId = "test.buff.other";
        const int OtherGuid = 5678;
        var otherInstance = new BuffEntityId(201, 3);
        _runtime.Register(OtherId, OtherGuid);

        var a = Active(seconds: 90f);

        var b = BuffsApi.Apply(
            BuffRequest.For(OtherId, Target).WithDuration(90f), _owner, "test.plugin");
        _runtime.CreateInstance(Target, OtherGuid, otherInstance);
        TickFrame();
        Assert.Equal(BuffApplicationState.Active, b.State);

        _runtime.SetAge(Instance, 10f);
        _runtime.SetAge(otherInstance, 40f);

        Assert.True(a.Refresh(30f));

        Assert.Equal(40f, _runtime.DurationOf(Instance));         // 10 + 30
        Assert.Equal(90f, _runtime.DurationOf(otherInstance));    // untouched
        Assert.Equal(40f, _runtime.AgeOf(otherInstance));         // and its clock was not reset

        Assert.True(b.Refresh(30f));
        Assert.Equal(70f, _runtime.DurationOf(otherInstance));    // 40 + 30, from its OWN age
        Assert.Equal(40f, _runtime.DurationOf(Instance));         // A unchanged by B's refresh
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private BuffRequest Request(float seconds = BuffRequest.KeepPrefabDuration) =>
        seconds > 0f
            ? BuffRequest.For(BuffId, Target).WithDuration(seconds)
            : BuffRequest.For(BuffId, Target);

    private IBuffApplication Apply(float seconds = BuffRequest.KeepPrefabDuration) =>
        BuffsApi.Apply(Request(seconds), _owner, "test.plugin");

    /// <summary>Apply, produce the instance, and poll once: a confirmed active application.</summary>
    private IBuffApplication Active(float seconds = BuffRequest.KeepPrefabDuration)
    {
        var application = Apply(seconds);
        _runtime.CreateInstance(Target, Guid, Instance);
        // Through TickFrame, not TickForTests: the instance must age on the SAME route the module
        // polls, or the fake's clock is present but inert and every refresh test would have to
        // SetAge by hand - which is exactly how a bug about elapsed time stays invisible.
        TickFrame();
        _runtime.Destroyed.Clear();
        Assert.Equal(BuffApplicationState.Active, application.State);
        return application;
    }

    /// <summary>
    /// One simulated frame with a realistically deferred world: pending destroys advance, then the
    /// module polls. Existing tests call <c>TickForTests</c> directly, which is the same thing when
    /// <see cref="FakeBuffRuntime.DestroyDelayFrames"/> is 0.
    /// </summary>
    private void TickFrame()
    {
        _runtime.AdvanceDestroys();
        _runtime.AdvanceAges();
        BuffsApi.TickForTests();
    }

    // -----------------------------------------------------------------
    // BUFFP1/BUFFP2: the destroy contract
    //
    // These exist because the live crash was unreachable offline: the fake removed the entity
    // synchronously inside DestroyInstance, so "destroy issued" and "entity gone" were the same
    // instant and no amount of testing could notice that the module stopped watching. With the fake
    // modeling the real deferral, the contract is assertable.
    // -----------------------------------------------------------------

    [Fact]
    public void Destroy_is_issued_once_and_the_module_keeps_watching_until_the_entity_is_gone()
    {
        _runtime.DestroyDelayFrames = 3;
        var application = Active();

        application.Remove();

        // Terminal immediately: the handle's contract does not wait for the world.
        Assert.Equal(BuffApplicationState.Removed, application.State);
        Assert.Equal(new[] { Instance }, _runtime.Destroyed);
        // ...but the entity is still there, and the module knows it still owes a confirmation.
        Assert.True(_runtime.Exists(Instance));
        Assert.Equal(1, BuffsApi.RetiringCount);

        TickFrame();
        Assert.Equal(1, BuffsApi.RetiringCount);

        TickFrame();
        TickFrame();

        Assert.False(_runtime.Exists(Instance));
        Assert.Equal(0, BuffsApi.RetiringCount);
        // One destroy for the removal, one for the retry the 2-frame wait allows. Exactly two, not
        // "at most two" - a never-retrying implementation would also satisfy <= 2.
        Assert.Equal(2, _runtime.Destroyed.Count);
    }

    [Fact]
    public void A_destroy_that_does_not_take_is_reissued_once_and_then_reported()
    {
        var log = new TestLogSource();
        Progenitor.Plugin.Logger = log;
        try
        {
            _runtime.DestroyDelayFrames = FakeBuffRuntime.NeverFrees;
            var application = Active();

            application.Remove();
            Assert.Equal(BuffApplicationState.Removed, application.State);

            for (int i = 0; i < BuffsApi.RetireBudgetFrames + 2; i++) TickFrame();

            // Exactly two: the original destroy and one re-issue. Not one per frame.
            Assert.Equal(2, _runtime.Destroyed.Count);
            Assert.Equal(0, BuffsApi.RetiringCount);
            Assert.True(_runtime.Exists(Instance), "the fake never frees it; that is the point");

            // ...and it gave up OUT LOUD. Without this the whole "and then reported" half of the
            // contract is unasserted: deleting the warning would leave the test green.
            Assert.Contains(log.Warnings, w =>
                w.Contains("was issued (and re-issued once)") && w.Contains("giving up watching it"));
        }
        finally
        {
            Progenitor.Plugin.Logger = null;
        }
    }

    /// <summary>
    /// A destroy that <b>threw</b> is not the same as one that found nothing to do, and it is the
    /// case that used to disappear: the handle went terminal, one warning was logged, and nothing
    /// watched or retried. The buff stayed on the wearer for good.
    /// </summary>
    [Fact]
    public void A_destroy_that_fails_is_still_watched_and_retried()
    {
        _runtime.DestroyDelayFrames = 2;
        _runtime.FailDestroysRemaining = 1;   // the first destroy throws; the retry succeeds
        var application = Active();

        application.Remove();

        Assert.Equal(BuffApplicationState.Removed, application.State);
        Assert.True(_runtime.Exists(Instance), "the failed destroy left it on the wearer");
        Assert.Equal(1, BuffsApi.RetiringCount);

        TickFrame();
        TickFrame();
        TickFrame();
        TickFrame();

        Assert.Equal(2, _runtime.Destroyed.Count);
        Assert.False(_runtime.Exists(Instance));
        Assert.Equal(0, BuffsApi.RetiringCount);
    }

    /// <summary>
    /// Removing a buff and re-applying it before the game has collected the destroy must work.
    ///
    /// <para>The destroy is an event, so for a frame or two the destroyed instance is still a
    /// `BuffBuffer` row and `TryFindInstance` keeps finding it. Removing the out-of-band
    /// `DestroyTag` made that window real, and without a carve-out the plugin gets
    /// `AlreadyPresent` — "no live application created it" — about an instance it removed itself a
    /// frame earlier. `AlreadyPresent` is for a save-restored or foreign instance, not for our own
    /// pending destroy.</para>
    /// </summary>
    [Fact]
    public void Re_applying_before_our_own_destroy_has_been_collected_is_not_refused_AlreadyPresent()
    {
        _runtime.DestroyDelayFrames = 3;
        var application = Active();

        application.Remove();
        Assert.True(_runtime.Exists(Instance), "still a BuffBuffer row for now");

        var again = BuffsApi.Apply(Request(), _owner, "test.plugin");

        Assert.NotEqual(BuffApplicationState.Refused, again.State);
        Assert.Equal(BuffApplicationState.Requested, again.State);
    }

    /// <summary>
    /// The negative control: an instance this module did <b>not</b> destroy — a save restore, or a
    /// foreign source — must still be refused. The carve-out above must not become a blanket
    /// "ignore anything already there".
    /// </summary>
    [Fact]
    public void A_foreign_instance_is_still_refused_AlreadyPresent()
    {
        _runtime.CreateInstance(Target, Guid, Instance);

        var application = BuffsApi.Apply(Request(), _owner, "test.plugin");

        Assert.Equal(BuffApplicationState.Refused, application.State);
        Assert.Equal(BuffRefusal.AlreadyPresent, application.Refusal);
    }

    /// <summary>A destroy that found nothing is not watched: there is nothing to confirm.</summary>
    [Fact]
    public void A_destroy_that_found_nothing_present_is_not_watched()
    {
        var application = Active();
        _runtime.DestroyEntity(Instance);   // gone by some other route first

        application.Remove();

        Assert.Equal(BuffApplicationState.Removed, application.State);
        Assert.Equal(0, BuffsApi.RetiringCount);
    }

    /// <summary>
    /// Negative control for the test above: when the world behaves, there is exactly one destroy and
    /// nothing is left pending. Without this, a bug that re-issued unconditionally would still pass
    /// the "at most two" assertion.
    /// </summary>
    [Fact]
    public void A_destroy_that_takes_immediately_is_issued_once_and_leaves_nothing_pending()
    {
        _runtime.DestroyDelayFrames = 1;
        var application = Active();

        application.Remove();
        TickFrame();

        Assert.Single(_runtime.Destroyed);
        Assert.False(_runtime.Exists(Instance));
        Assert.Equal(0, BuffsApi.RetiringCount);
    }

    [Fact]
    public void An_application_that_ended_on_its_own_is_not_retired()
    {
        _runtime.DestroyDelayFrames = 3;
        var application = Active();

        // The instance vanished by itself - expiry, wearer death, a foreign destroy. This module
        // destroyed nothing, so it has nothing to confirm.
        _runtime.DestroyEntity(Instance);
        TickFrame();

        Assert.Equal(BuffApplicationState.Ended, application.State);
        Assert.Empty(_runtime.Destroyed);
        Assert.Equal(0, BuffsApi.RetiringCount);
    }

    [Fact]
    public void A_retirement_is_dropped_without_touching_the_world_when_the_epoch_moves()
    {
        _runtime.DestroyDelayFrames = FakeBuffRuntime.NeverFrees;
        var application = Active();
        application.Remove();
        Assert.Equal(1, BuffsApi.RetiringCount);

        _runtime.Epoch++;
        TickFrame();

        // The recorded index names a different entity after a world load; re-destroying through it
        // would act on a stranger.
        Assert.Equal(0, BuffsApi.RetiringCount);
        Assert.Single(_runtime.Destroyed);
    }

    /// <summary>
    /// Asserts the <b>report</b>, not just that the list was emptied. `Uninstall` clears `Retiring`
    /// unconditionally, so a `RetiringCount == 0` assertion alone stays green even if the whole
    /// warning loop is deleted — which is what this test used to check.
    /// </summary>
    [Fact]
    public void Core_uninstall_reports_a_destroy_it_watched_and_never_saw_complete()
    {
        var log = new TestLogSource();
        Progenitor.Plugin.Logger = log;
        try
        {
            _runtime.DestroyDelayFrames = FakeBuffRuntime.NeverFrees;
            var application = Active();
            application.Remove();

            // Watch it for a frame so this is a genuinely-watched retirement rather than one that
            // was merely issued during shutdown.
            TickFrame();
            Assert.Equal(1, BuffsApi.RetiringCount);

            BuffsApi.Uninstall();

            Assert.Equal(0, BuffsApi.RetiringCount);
            Assert.Contains(log.Warnings, w =>
                w.Contains("was issued and watched") && w.Contains("no generation is watching it any more"));
        }
        finally
        {
            Progenitor.Plugin.Logger = null;
        }
    }

    /// <summary>
    /// The counterpart to the test above, and the reason the two are worded differently: a destroy
    /// issued by `FinishForShutdown` microseconds before Poll is unhooked <b>cannot</b> have been
    /// observed gone. Reporting that in the same alarming words would make every ordinary Core
    /// unload with a live buff look broken.
    /// </summary>
    [Fact]
    public void A_destroy_issued_during_shutdown_is_reported_calmly_not_as_a_failure()
    {
        var log = new TestLogSource();
        Progenitor.Plugin.Logger = log;
        try
        {
            _runtime.DestroyDelayFrames = FakeBuffRuntime.NeverFrees;
            Active();

            BuffsApi.Uninstall();

            Assert.DoesNotContain(log.Warnings, w => w.Contains("was issued and watched"));
            Assert.Contains(log.Infos, m => m.Contains("was issued during Core uninstall"));
        }
        finally
        {
            Progenitor.Plugin.Logger = null;
        }
    }

    /// <summary>
    /// The offline analogue of the case that crashed the server on 2026-09-13: a plugin is unloaded
    /// while it holds a live buff, in a world where destroying is deferred.
    ///
    /// <para>This cannot prove the crash is fixed — the abort was inside the game's own
    /// <c>DetachSystem</c>, which has no offline stand-in. What it does prove is the contract that
    /// was broken underneath it: the sweep issues exactly one destroy, the module keeps watching
    /// across the unload, and it confirms the entity actually went away instead of assuming it.</para>
    /// </summary>
    [Fact]
    public void Plugin_unload_with_a_live_buff_destroys_once_and_keeps_watching_until_it_is_gone()
    {
        _runtime.DestroyDelayFrames = 3;
        var application = Active();

        BuffsApi.SweepForUnload(_owner, "test.plugin");

        Assert.Equal(BuffApplicationState.Removed, application.State);
        Assert.Single(_runtime.Destroyed);
        // The entity outlives the sweep, which is the whole point: the old code assumed otherwise
        // and forced the issue with an out-of-band DestroyTag.
        Assert.True(_runtime.Exists(Instance));
        Assert.Equal(1, BuffsApi.RetiringCount);

        TickFrame();
        TickFrame();
        TickFrame();

        Assert.False(_runtime.Exists(Instance));
        Assert.Equal(0, BuffsApi.RetiringCount);
    }

    /// <summary>
    /// The only offline guard possible for <c>UnityBuffRuntime</c>, which is behind
    /// <c>#if !PROGENITOR_TESTS</c>. Idiomatic in this suite (<c>HookOrderTests</c>,
    /// <c>CoreUnloadSlotClearTests</c> read source the same way).
    /// </summary>
    [Fact]
    public void The_unity_buff_runtime_never_adds_DestroyTag()
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Buffs", "UnityBuffRuntime.cs");
        foreach (string line in source.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("///", StringComparison.Ordinal)) continue;
            Assert.False(
                trimmed.Contains("DestroyTag", StringComparison.Ordinal),
                "UnityBuffRuntime must never add DestroyTag. The game's own DestroyGroup diagnostic "
                + "says it may only be added inside ProcessDestroyEventsSystem; adding it out of band "
                + "routed a destroyed buff around the detach pipeline and crashed a live server on "
                + "2026-09-13. DestroyUtility.Destroy alone, then Buffs' retirement watch. Line: "
                + trimmed);
        }
    }

    private sealed class PluginOwner { }

    private sealed class OtherPluginOwner { }

    /// <summary>
    /// A world of dictionaries. It records what was asked of it so a test can assert that a
    /// refusal really did write nothing.
    /// </summary>
    private sealed class FakeBuffRuntime : IBuffRuntime
    {
        private readonly Dictionary<string, int> _definitions = new(StringComparer.Ordinal);
        private readonly HashSet<BuffEntityId> _entities = new();
        private readonly HashSet<BuffEntityId> _buffable = new();
        private readonly Dictionary<(BuffEntityId Target, int Guid), BuffEntityId> _instances = new();
        private readonly Dictionary<BuffEntityId, float> _durations = new();

        public bool IsServer { get; set; } = true;

        public bool HasWorld { get; set; } = true;

        public int Epoch { get; set; } = 7;

        /// <summary>False makes every definition resolve as registered-but-not-applied.</summary>
        public bool Applied { get; set; } = true;

        /// <summary>False makes the instance carry no LifeTime.</summary>
        public bool Timed { get; set; } = true;

        /// <summary>False makes RequestApply fail the way a world without DebugEventsSystem does.</summary>
        public bool ApplyPathAvailable { get; set; } = true;

        public int ApplyCalls { get; private set; }

        public int ResolveCalls { get; private set; }

        public List<BuffEntityId> Destroyed { get; } = new();

        public int DestroyCalls => Destroyed.Count;

        public void Register(string id, int guid) => _definitions[id] = guid;

        public void AddEntity(BuffEntityId entity, bool buffable)
        {
            _entities.Add(entity);
            if (buffable) _buffable.Add(entity);
        }

        public void DestroyEntity(BuffEntityId entity)
        {
            _entities.Remove(entity);
            _buffable.Remove(entity);
            var drop = new List<(BuffEntityId, int)>();
            foreach (var pair in _instances)
                if (pair.Value == entity || pair.Key.Target == entity) drop.Add(pair.Key);
            foreach (var key in drop) _instances.Remove(key);
        }

        public void CreateInstance(BuffEntityId target, int guid, BuffEntityId instance)
        {
            _entities.Add(instance);
            _instances[(target, guid)] = instance;
            _ages[instance] = 0f;
        }

        public float DurationOf(BuffEntityId instance) =>
            _durations.TryGetValue(instance, out float value) ? value : float.NaN;

        public BuffResolve TryResolveBuff(string buffId, object? owner, string? pluginId, out int prefabGuid)
        {
            ResolveCalls++;
            if (!_definitions.TryGetValue(buffId ?? "", out prefabGuid)) return BuffResolve.NotRegistered;
            return Applied ? BuffResolve.Ok : BuffResolve.NotApplied;
        }

        public BuffTargetCheck CheckTarget(BuffEntityId target)
        {
            if (!HasWorld) return BuffTargetCheck.NoWorld;
            if (!_entities.Contains(target)) return BuffTargetCheck.Missing;
            return _buffable.Contains(target) ? BuffTargetCheck.Ok : BuffTargetCheck.NotBuffable;
        }

        public bool RequestApply(BuffEntityId target, int prefabGuid, out string reason)
        {
            if (!ApplyPathAvailable)
            {
                reason = "DebugEventsSystem is not in this world yet";
                return false;
            }

            reason = "";
            ApplyCalls++;
            return true;
        }

        public bool TryFindInstance(BuffEntityId target, int prefabGuid, out BuffEntityId instance)
        {
            if (_instances.TryGetValue((target, prefabGuid), out instance) && _entities.Contains(instance))
                return true;
            instance = BuffEntityId.None;
            return false;
        }

        public bool SetDuration(BuffEntityId instance, float seconds, out bool timed)
        {
            SetDurationCalls++;
            timed = Timed;
            if (!Timed) return false;
            if (WriteFails) return false;           // timed, but the write did not take
            if (!_entities.Contains(instance)) return false;
            _durations[instance] = seconds;
            return true;
        }

        /// <summary>
        /// Make a write fail while still reporting the instance as timed — the real
        /// <c>UnityBuffRuntime</c> shape when the component write throws
        /// (<c>catch</c> around <c>entity.Write</c>), which is the only reachable way
        /// <c>SetDuration</c> returns false after <c>TryReadTiming</c> has already succeeded.
        ///
        /// <para>Without this knob that branch could not be reached from a test at all, and it is
        /// exactly where a failed write was once recorded as "no refusal".</para>
        /// </summary>
        public bool WriteFails { get; set; }

        /// <summary>How many times the module asked to write a duration. A refusal must not.</summary>
        public int SetDurationCalls { get; private set; }

        /// <summary>How many times the module read an instance's timing. A refusal path must not.</summary>
        public int TimingReads { get; private set; }

        // ---- the age model -----------------------------------------------------------------
        //
        // The game keeps elapsed time in a separate Age component and measures LifeTime.Duration
        // against it. A fake in which elapsed time does not exist cannot catch a bug about elapsed
        // time - the same shape of hole the deferred-destroy model closed for the destroy contract.
        private readonly Dictionary<BuffEntityId, float> _ages = new();

        /// <summary>Seconds every live instance ages per simulated frame. 0 freezes the clock.</summary>
        public float AgePerFrame { get; set; } = 1f;

        /// <summary>The end action the fake reports for every instance.</summary>
        public BuffLifetimeEnd EndAction { get; set; } = BuffLifetimeEnd.Destroy;

        /// <summary>Instances whose timing cannot be read - models a base carrying no Age.</summary>
        public HashSet<BuffEntityId> NoTiming { get; } = new();

        /// <summary>Force the elapsed value the fake reports, including a non-finite one.</summary>
        public float? ElapsedOverride { get; set; }

        public float AgeOf(BuffEntityId instance) =>
            _ages.TryGetValue(instance, out float v) ? v : 0f;

        public void SetAge(BuffEntityId instance, float seconds) => _ages[instance] = seconds;

        /// <summary>
        /// Advance every live instance's Age, then expire the ones whose Age has reached their
        /// <c>LifeTime.Duration</c>. Called once per simulated frame.
        ///
        /// <para>The expiry half is what makes a wrong Duration <b>observable</b>. Without it the
        /// fake would happily hold an instance whose age is past its duration, and the live
        /// regression - a refresh shorter than the elapsed time ending the buff on the spot - would
        /// still be untestable offline, which is the whole reason it reached a player.</para>
        ///
        /// <para>Only instances with a duration actually written expire: a clone left on its
        /// prefab's own timer has no entry here and is not this model's to end.</para>
        /// </summary>
        internal void AdvanceAges()
        {
            var keys = new List<BuffEntityId>(_entities);
            if (AgePerFrame != 0f)
                foreach (var key in keys)
                    _ages[key] = (_ages.TryGetValue(key, out float v) ? v : 0f) + AgePerFrame;

            foreach (var key in keys)
            {
                if (!_durations.TryGetValue(key, out float duration) || duration <= 0f) continue;
                if (!_ages.TryGetValue(key, out float age)) continue;
                if (age >= duration) DestroyEntity(key);
            }
        }

        public bool TryReadTiming(BuffEntityId instance, out BuffTiming timing)
        {
            TimingReads++;
            timing = default;
            if (NoTiming.Contains(instance)) return false;
            if (!_entities.Contains(instance)) return false;
            if (!Timed) return false;

            float elapsed = ElapsedOverride ?? (_ages.TryGetValue(instance, out float v) ? v : 0f);
            float duration = _durations.TryGetValue(instance, out float d) ? d : 0f;
            timing = new BuffTiming(elapsed, duration, EndAction);
            return true;
        }

        /// <summary>
        /// How many <see cref="Progenitor.Buffs.Buffs.TickForTests"/> calls pass between a destroy
        /// being issued and the entity actually going away (job BUFFP2 tests).
        ///
        /// <para><b>0 (the default) is the old synchronous behavior</b> and is what every
        /// pre-existing test in this file assumes. It is also the one thing the real game never
        /// does: <c>DestroyUtility.Destroy</c> raises an event, so the entity survives at least
        /// until a later system runs, and it can fail to go away entirely. That mismatch is why the
        /// out-of-band <c>DestroyTag</c> defect was unreachable offline. Set this to 1 or more to
        /// model the real thing, or to <see cref="NeverFrees"/> to model a destroy that does not
        /// take at all.</para>
        /// </summary>
        public int DestroyDelayFrames { get; set; } = 1;

        /// <summary>A destroy that is issued and never completes.</summary>
        public const int NeverFrees = int.MaxValue;

        /// <summary>
        /// Make the next <see cref="DestroyInstance"/> call fail the way a throwing
        /// <c>DestroyUtility.Destroy</c> does: the entity stays, and the runtime reports
        /// <see cref="BuffDestroy.Failed"/> rather than "already gone".
        /// </summary>
        public int FailDestroysRemaining { get; set; }

        private readonly Dictionary<BuffEntityId, int> _pendingDestroy = new();

        public BuffDestroy DestroyInstance(BuffEntityId instance)
        {
            Destroyed.Add(instance);
            if (!_entities.Contains(instance)) return BuffDestroy.NotPresent;

            if (FailDestroysRemaining > 0)
            {
                FailDestroysRemaining--;
                return BuffDestroy.Failed;
            }

            if (DestroyDelayFrames <= 0)
            {
                _entities.Remove(instance);
                return BuffDestroy.Issued;
            }

            // Issued, not yet gone. Exists() keeps answering true until the countdown expires,
            // which is what makes the retirement watch observable.
            //
            // A re-issued destroy does NOT restart the countdown: the entity is already on its way
            // out and asking twice does not make the game slower. Resetting it here would be a
            // harness artefact that makes a correct single retry look like an entity that never
            // frees.
            if (!_pendingDestroy.ContainsKey(instance)) _pendingDestroy[instance] = DestroyDelayFrames;
            return BuffDestroy.Issued;
        }

        /// <summary>Advance pending destroys. Called by the harness once per simulated frame.</summary>
        internal void AdvanceDestroys()
        {
            if (_pendingDestroy.Count == 0) return;
            var keys = new List<BuffEntityId>(_pendingDestroy.Keys);
            foreach (var key in keys)
            {
                int left = _pendingDestroy[key];
                if (left == NeverFrees) continue;
                if (--left <= 0)
                {
                    _pendingDestroy.Remove(key);
                    _entities.Remove(key);
                }
                else
                {
                    _pendingDestroy[key] = left;
                }
            }
        }

        public bool Exists(BuffEntityId entity) => _entities.Contains(entity);
    }
}
