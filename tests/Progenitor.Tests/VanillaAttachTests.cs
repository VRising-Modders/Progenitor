using System;
using System.Collections.Generic;
using System.Threading;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Job FUI36: hosting mod rows beside a vanilla widget. Everything here is game-free — the plan
/// decides from an instance id and three booleans, the registry from an owner reference, and the
/// handle drives a fake <see cref="IUiRuntime"/>. The Unity half (<c>MapWarEventSiteResolver</c>)
/// is what reads a <c>MapMenuMapper</c> and is deliberately not compiled into this project.
/// </summary>
[Collection("PluginLogger")]
public class VanillaAttachTests : IDisposable
{
    private const VanillaUiSite Site = VanillaUiSite.MapWarEventInfo;

    private readonly TestLogSource _log = new();
    private readonly FakeAttachRuntime _client = new() { IsClient = true };
    private readonly object _owner = new();

    public VanillaAttachTests()
    {
        Plugin.Logger = _log;
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_client);
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    // ---------------------------------------------------------------- the plan

    [Fact]
    public void First_sight_of_a_site_builds_a_container_beside_the_widget()
    {
        var d = VanillaAttachPlan.Decide(Site, Open(instanceId: 11), VanillaAttachState.Empty);

        Assert.Equal(VanillaAttachAction.Build, d.Action);
        Assert.Equal(VanillaAttachParent.WidgetSibling, d.Parent);
        Assert.False(d.IgnoreLayout);
        Assert.False(d.DestroyOld);
        Assert.Equal("first", d.Reason);
        Assert.Equal("none", d.LayoutWord);
    }

    [Fact]
    public void The_same_instance_next_frame_is_kept_and_nothing_is_rebuilt()
    {
        var d = VanillaAttachPlan.Decide(Site, Open(11), new VanillaAttachState(true, 11));

        Assert.Equal(VanillaAttachAction.Keep, d.Action);
        Assert.False(d.DestroyOld);
    }

    [Fact]
    public void A_changed_instance_rebuilds_and_destroys_the_old_container_first()
    {
        // Close/reopen of the map. Skipping the destroy is exactly how a frozen duplicate appears
        // beside the card on the deactivate-and-reuse branch.
        var d = VanillaAttachPlan.Decide(Site, Open(12), new VanillaAttachState(true, 11));

        Assert.Equal(VanillaAttachAction.Rebuild, d.Action);
        Assert.True(d.DestroyOld);
        Assert.Equal("instance", d.Reason);
    }

    [Fact]
    public void A_container_the_game_destroyed_takes_the_build_branch_not_the_keep_branch()
    {
        var d = VanillaAttachPlan.Decide(Site, Open(11), new VanillaAttachState(hasContainer: false, 11));

        Assert.Equal(VanillaAttachAction.Build, d.Action);
        Assert.False(d.DestroyOld);
    }

    [Fact]
    public void An_absent_site_degrades_and_takes_the_container_with_it()
    {
        var d = VanillaAttachPlan.Decide(Site, VanillaSiteSample.Absent, new VanillaAttachState(true, 11));

        Assert.Equal(VanillaAttachAction.Degrade, d.Action);
        Assert.True(d.DestroyOld);
        Assert.Equal("site", d.Reason);
    }

    [Fact]
    public void An_absent_site_with_no_container_asks_for_no_work_at_all()
    {
        var d = VanillaAttachPlan.Decide(Site, VanillaSiteSample.Absent, VanillaAttachState.Empty);

        Assert.Equal(VanillaAttachAction.Degrade, d.Action);
        Assert.False(d.DestroyOld);
    }

    [Fact]
    public void A_site_whose_donor_is_gone_degrades_rather_than_drawing_a_row_in_our_own_font()
    {
        var sample = new VanillaSiteSample(true, 11, donorPresent: false, parentLayoutGroup: false, "MapCard");
        var d = VanillaAttachPlan.Decide(Site, sample, VanillaAttachState.Empty);

        Assert.Equal(VanillaAttachAction.Degrade, d.Action);
        Assert.Equal("donor", d.Reason);
    }

    [Fact]
    public void A_parent_governed_by_a_layout_group_gets_ignoreLayout_so_the_vanilla_card_does_not_move()
    {
        var sample = new VanillaSiteSample(true, 11, donorPresent: true, parentLayoutGroup: true, "MapCard");
        var d = VanillaAttachPlan.Decide(Site, sample, VanillaAttachState.Empty);

        Assert.Equal(VanillaAttachAction.Build, d.Action);
        Assert.Equal(VanillaAttachParent.WidgetSibling, d.Parent);
        Assert.True(d.IgnoreLayout);
        Assert.Equal("ignored", d.LayoutWord);
    }

    [Fact]
    public void A_fitter_on_the_parent_is_not_a_layout_group_and_takes_no_layout_branch()
    {
        // R60 finding 2. A ContentSizeFitter lays no child out — it sizes the parent from the
        // parent's own ILayoutElements — so it can never move the vanilla card and an
        // ignoreLayout on our child means nothing to it. Claiming layout=ignored here would be
        // the log line asserting a neutralization that never happened.
        var sample = new VanillaSiteSample(
            true, 11, donorPresent: true, parentLayoutGroup: false, "MapCard", parentSizeFitter: true);

        Assert.True(sample.ParentSizeFitter);
        Assert.False(sample.ParentLayoutGroup);

        var d = VanillaAttachPlan.Decide(Site, sample, VanillaAttachState.Empty);

        Assert.Equal(VanillaAttachAction.Build, d.Action);
        Assert.Equal(VanillaAttachParent.WidgetSibling, d.Parent);
        Assert.False(d.IgnoreLayout);
        Assert.Equal("none", d.LayoutWord);
    }

    [Fact]
    public void A_group_and_a_fitter_together_take_the_group_branch_and_still_report_both()
    {
        var sample = new VanillaSiteSample(
            true, 11, donorPresent: true, parentLayoutGroup: true, "MapCard", parentSizeFitter: true);
        var d = VanillaAttachPlan.Decide(Site, sample, VanillaAttachState.Empty);

        Assert.True(d.IgnoreLayout);
        Assert.Equal("ignored", d.LayoutWord);
        Assert.True(sample.ParentSizeFitter);
    }

    [Fact]
    public void A_site_that_forbids_ignoreLayout_reparents_to_the_canvas_instead()
    {
        // No site takes this branch today; the branch exists because P3 of the client spike may
        // find a layout the ignore flag does not survive, and a rule with no code is not a rule.
        var info = new VanillaUiSiteInfo(Site, "X", "TimerText", "MapMenuMapper", allowIgnoreLayout: false);
        Assert.False(info.AllowIgnoreLayout);

        var sample = new VanillaSiteSample(true, 11, true, parentLayoutGroup: true, "MapCard");
        var d = VanillaAttachPlan.Decide(Site, sample, VanillaAttachState.Empty);

        // The one shipped site allows it, so this asserts the two halves are wired to the same
        // flag rather than that the flag is false.
        Assert.Equal(VanillaUiSites.Info(Site).AllowIgnoreLayout, d.Parent == VanillaAttachParent.WidgetSibling);
    }

    [Fact]
    public void An_undefined_cast_site_is_never_treated_like_the_one_site_that_exists()
    {
        var bogus = (VanillaUiSite)77;
        Assert.False(VanillaUiSites.IsDefined(bogus));
        Assert.Equal("site-77", VanillaUiSites.Key(bogus));

        var d = VanillaAttachPlan.Decide(bogus, Open(11), VanillaAttachState.Empty);
        Assert.Equal(VanillaAttachAction.Degrade, d.Action);
        Assert.Equal("undefined", d.Reason);
    }

    [Fact]
    public void The_only_site_names_its_donor_its_system_and_its_container_suffix()
    {
        var info = VanillaUiSites.Info(Site);
        Assert.Equal("MapWarEventInfo", info.Key);
        Assert.Equal("TimerText", info.Donor);
        Assert.Equal("MapMenuMapper", info.System);
        Assert.Equal(
            "progenitor-ui-example.sample-MapWarEventInfo",
            UiOwnership.AttachName("example.sample", info.Key));
    }

    // ---------------------------------------------------------------- the handle

    [Fact]
    public void A_server_handle_is_disabled_but_valid_and_allocates_nothing()
    {
        UiApi.SetRuntimeForTests(new FakeAttachRuntime { IsClient = false });
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");

        Assert.NotNull(a);
        Assert.False(a.Enabled);
        Assert.False(a.IsAttached);
        var row = a.Row("T1 next in 1h 9m");
        Assert.False(row.Enabled);
        a.Visible = false;
        a.SetRows(new[] { "a", "b" });
        Assert.False(a.IsAttached);
        a.Dispose();
        Assert.True(a.IsDisposed);
        a.Dispose();
        Assert.True(a.IsDisposed);
    }

    [Fact]
    public void A_skipped_module_returns_a_disabled_handle_and_installs_no_watch()
    {
        UiApi.SetSkippedForTests(true);
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");

        Assert.False(a.Enabled);
        Assert.Equal(0, _client.WatchCount);
        Assert.False(VanillaAttachRegistry.HasWatch(Site));
        a.Dispose();
    }

    [Fact]
    public void An_enabled_attach_installs_exactly_one_watch_for_the_site_however_many_handles()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        var b = UiApi.AttachToVanilla(Site, new object(), "example.other");

        Assert.True(a.Enabled);
        Assert.Equal(1, _client.WatchCount);
        Assert.Equal(1, VanillaAttachRegistry.WatchCount);

        a.Dispose();
        Assert.Equal(1, VanillaAttachRegistry.WatchCount);   // b still needs it
        b.Dispose();
        Assert.Equal(0, VanillaAttachRegistry.WatchCount);
        Assert.Equal(1, _client.WatchDisposals);
    }

    [Fact]
    public void Rows_appear_when_the_site_first_resolves_and_carry_their_text()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        var t1 = a.Row("T1 (57+) next in 1h 9m");
        var t2 = a.Row("T2 (80+) remaining 19m 49s");

        Assert.False(a.IsAttached);
        Assert.Equal("T1 (57+) next in 1h 9m", t1.Text);   // held by the handle, not by Unity

        _client.Fire(Site, Open(11));

        Assert.True(a.IsAttached);
        Assert.Equal(2, _client.RowTexts.Count);
        Assert.Equal("T1 (57+) next in 1h 9m", _client.RowTexts[0]);
        Assert.Equal("T2 (80+) remaining 19m 49s", _client.RowTexts[1]);
        Assert.Equal(UiHandleState.Shown, t2.State);
        a.Dispose();
    }

    [Fact]
    public void A_steady_frame_rebuilds_nothing()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("one");
        _client.Fire(Site, Open(11));
        int builds = _client.ContainerBuilds;

        for (int i = 0; i < 60; i++) _client.Fire(Site, Open(11));

        Assert.Equal(builds, _client.ContainerBuilds);
        Assert.Equal(1, _client.LiveContainers);
        a.Dispose();
    }

    [Fact]
    public void Closing_and_reopening_the_map_leaves_exactly_one_container_and_the_rows_come_back()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        a.Row("T2");
        _client.Fire(Site, Open(11));
        Assert.Equal(1, _client.LiveContainers);

        _client.Fire(Site, VanillaSiteSample.Absent);          // map closed
        Assert.False(a.IsAttached);
        Assert.Equal(0, _client.LiveContainers);

        _client.Fire(Site, Open(12));                          // reopened, new instance
        Assert.True(a.IsAttached);
        Assert.Equal(1, _client.LiveContainers);
        Assert.Equal(2, _client.RowTexts.Count);
        Assert.Equal("T1", _client.RowTexts[0]);
        a.Dispose();
    }

    [Fact]
    public void A_container_the_game_destroyed_is_replaced_without_leaving_a_second_one()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        _client.KillContainers();                              // Unity tore the map view down
        Assert.False(a.IsAttached);

        _client.Fire(Site, Open(11));                          // same instance id, no container
        Assert.True(a.IsAttached);
        Assert.Equal(1, _client.LiveContainers);
        a.Dispose();
    }

    [Fact]
    public void SetRows_grows_shrinks_and_rewrites_and_never_leaves_a_stale_row()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        _client.Fire(Site, Open(11));

        a.SetRows(new[] { "one", "two", "three" });
        Assert.Equal(3, a.RowCount);
        Assert.Equal(new[] { "one", "two", "three" }, _client.RowTexts);

        a.SetRows(new[] { "only" });
        Assert.Equal(1, a.RowCount);
        Assert.Equal(new[] { "only" }, _client.RowTexts);

        a.SetRows(Array.Empty<string>());
        Assert.Equal(0, a.RowCount);
        Assert.Empty(_client.RowTexts);
        a.Dispose();
    }

    [Fact]
    public void A_row_handle_survives_a_rebuild_and_keeps_writing_to_the_current_clone()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        var row = a.Row("before");
        _client.Fire(Site, Open(11));
        _client.Fire(Site, VanillaSiteSample.Absent);
        _client.Fire(Site, Open(12));

        row.Text = "after";

        Assert.Equal("after", row.Text);
        Assert.Equal(new[] { "after" }, _client.RowTexts);
        a.Dispose();
    }

    [Fact]
    public void A_row_written_while_the_map_is_closed_lands_on_the_next_open()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        var row = a.Row("stale");
        _client.Fire(Site, Open(11));
        _client.Fire(Site, VanillaSiteSample.Absent);

        row.Text = "written while closed";
        _client.Fire(Site, Open(12));

        Assert.Equal(new[] { "written while closed" }, _client.RowTexts);
        a.Dispose();
    }

    [Fact]
    public void Visible_is_remembered_across_a_rebuild()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));
        a.Visible = false;
        Assert.False(_client.ContainerVisible);

        _client.Fire(Site, Open(12));
        Assert.False(_client.ContainerVisible);

        a.Visible = true;
        Assert.True(_client.ContainerVisible);
        a.Dispose();
    }

    [Fact]
    public void A_runtime_that_cannot_build_the_container_says_so_once_and_not_every_frame()
    {
        _client.RefuseContainers = true;
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");

        for (int i = 0; i < 10; i++) _client.Fire(Site, Open(11));

        Assert.False(a.IsAttached);
        Assert.Equal(1, CountLines("could not build its container"));
        a.Dispose();
    }

    [Fact]
    public void A_row_whose_clone_has_no_text_component_is_simply_not_there()
    {
        _client.RefuseRows = true;
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        Assert.True(a.IsAttached);          // the container is fine
        Assert.Empty(_client.RowTexts);     // the row is not
        a.Dispose();
    }

    [Fact]
    public void Dispose_destroys_our_container_and_nothing_else()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        a.Dispose();

        Assert.True(a.IsDisposed);
        Assert.Equal(0, _client.LiveContainers);
    }

    [Fact]
    public void A_disposed_handle_ignores_a_refresh_and_a_second_dispose()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));
        a.Dispose();

        _client.Fire(Site, Open(12));
        a.Dispose();

        Assert.Equal(0, _client.LiveContainers);
    }

    // ------------------------------------------------- a destroy that did not take (R60 #1)

    [Fact]
    public void A_refused_destroy_keeps_the_registry_entry_and_the_watch_that_retries_it()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));
        Assert.Equal(1, _client.LiveContainers);

        // Unity refuses the destroy. The container is parented into the vanilla tree, so the
        // reference the handle holds is the only one in the process: forgetting it now welds a
        // frozen container with dead rows into the game's hierarchy.
        _client.RefuseDestroys = true;
        a.Dispose();

        Assert.True(a.IsDisposed);
        Assert.Equal(1, _client.LiveContainers);
        Assert.Equal(1, VanillaAttachRegistry.Count);
        Assert.True(VanillaAttachRegistry.HasWatch(Site));
        Assert.Contains("destroy did not take; retrying", Log());

        _client.RefuseDestroys = false;
        _client.Fire(Site, Open(11));

        Assert.Equal(0, _client.LiveContainers);
        Assert.Equal(0, VanillaAttachRegistry.Count);
        Assert.False(VanillaAttachRegistry.HasWatch(Site));
    }

    [Fact]
    public void A_destroy_that_throws_is_logged_and_retried_rather_than_swallowed()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        _client.DestroysThrow = true;
        a.Dispose();

        Assert.Contains("container release threw", Log());
        Assert.Equal(1, VanillaAttachRegistry.Count);

        _client.DestroysThrow = false;
        _client.Fire(Site, Open(11));

        Assert.Equal(0, _client.LiveContainers);
        Assert.Equal(0, VanillaAttachRegistry.Count);
    }

    [Fact]
    public void A_rebuild_whose_old_container_refuses_to_die_retries_it_and_still_builds_the_new_one()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        _client.RefuseDestroys = true;
        _client.Fire(Site, Open(12));           // the map was closed and reopened

        Assert.Equal(2, _client.ContainerBuilds);
        Assert.Equal(2, _client.LiveContainers);   // the old one is still there, and still ours
        Assert.True(a.IsAttached);

        _client.RefuseDestroys = false;
        _client.Fire(Site, Open(12));

        Assert.Equal(1, _client.LiveContainers);   // the retry took; one container beside the card
        a.Dispose();
    }

    [Fact]
    public void A_destroy_refused_forever_is_written_off_with_the_container_name_and_frees_the_watch()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        _client.RefuseDestroys = true;
        a.Dispose();

        // The retry is bounded: a disposed handle must not hold a per-frame Core-owned delegate
        // for the life of the generation over an object Unity will never destroy.
        for (int i = 0; i < 40; i++) _client.Fire(Site, Open(11));

        Assert.Contains("leaked 1 object(s)", Log());
        Assert.Contains("container=progenitor-ui-example.sample-MapWarEventInfo", Log());
        Assert.Equal(0, VanillaAttachRegistry.Count);
        Assert.False(VanillaAttachRegistry.HasWatch(Site));
    }

    [Fact]
    public void Core_Uninstall_says_how_many_refused_destroys_it_had_to_leave_behind()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        _client.RefuseDestroys = true;
        VanillaAttachRegistry.Clear();

        Assert.Contains("Uninstall left 1 object(s) whose destroy was refused", Log());
        Assert.Equal(0, VanillaAttachRegistry.Count);
    }

    // ------------------------------- the three things a destroy can be (R61 #7) and the race (R61 #3)

    [Fact]
    public void A_destroy_that_took_leaves_nothing_to_follow_and_says_nothing()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        _client.DestroyVerdict = UiDestroyVerdict.Destroyed;
        a.Dispose();

        Assert.Equal(0, _client.LiveContainers);
        Assert.Equal(1, _client.ContainerReleases);
        Assert.Equal(0, VanillaAttachRegistry.Count);
        Assert.False(VanillaAttachRegistry.HasWatch(Site));
        Assert.DoesNotContain("destroy did not take", Log());
    }

    [Fact]
    public void A_deferred_destroy_is_verified_rather_than_repeated_and_is_never_called_a_failure()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        // DestroyImmediate threw and the fallback Destroy took it. The object is Unity's now: it
        // is alive for the rest of this frame and gone at the end of it. Calling that "destroy
        // did not take" and re-throwing into it every frame was R61 finding 7.
        _client.DestroyVerdict = UiDestroyVerdict.Deferred;
        a.Dispose();

        Assert.True(a.IsDisposed);
        Assert.Equal(1, _client.LiveContainers);              // still here, this frame only
        Assert.Equal(1, VanillaAttachRegistry.Count);         // so the handle keeps its driver
        Assert.True(VanillaAttachRegistry.HasWatch(Site));
        Assert.DoesNotContain("destroy did not take", Log());
        Assert.DoesNotContain("release threw", Log());

        // Frames while Unity has not got round to it: the sweep only looks, it does not destroy.
        _client.Fire(Site, Open(11));
        _client.Fire(Site, Open(11));
        Assert.Equal(1, _client.ContainerReleases);

        _client.EndFrame();
        _client.Fire(Site, Open(11));

        Assert.Equal(0, _client.LiveContainers);
        Assert.Equal(1, _client.ContainerReleases);           // one destroy, never a second
        Assert.Equal(0, VanillaAttachRegistry.Count);
        Assert.False(VanillaAttachRegistry.HasWatch(Site));
        Assert.DoesNotContain("leaked", Log());
    }

    [Fact]
    public void A_failed_destroy_still_warns_once_and_is_actually_retried_every_frame()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        _client.RefuseDestroys = true;
        a.Dispose();

        Assert.Equal(1, _client.ContainerReleases);
        Assert.Equal(1, CountLines("destroy did not take; retrying"));

        _client.Fire(Site, Open(11));
        _client.Fire(Site, Open(11));

        // The difference from the deferred case: this one really is retried, because nothing has
        // taken the object and nothing else is going to.
        Assert.Equal(3, _client.ContainerReleases);
        Assert.Equal(1, VanillaAttachRegistry.Count);

        _client.RefuseDestroys = false;
        _client.Fire(Site, Open(11));
        Assert.Equal(0, VanillaAttachRegistry.Count);
    }

    [Fact]
    public void The_disposed_flag_is_published_after_the_handoff_so_no_frame_can_read_it_early()
    {
        // The invariant R61 finding 3 is about, pinned directly rather than through a race the
        // window of which is three instructions wide. Dispose used to set IsDisposed and *then*
        // take the gate, so a frame reading the flag in between saw a disposed handle whose
        // doomed list had not been filled yet, called it finished, dropped it from the table and
        // disposed the site watch - leaving the container with no retry, no give-up and no leak
        // line. The release below runs inside the disposal's critical section: at that moment the
        // flag must still be unpublished to every other thread, because the handoff is not done.
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        _client.RefuseDestroys = true;
        bool? seenFromAnotherThread = null;
        _client.OnRelease = () =>
        {
            // Another thread, reading exactly the field Apply reads. It takes no lock, so this
            // cannot deadlock against the gate the disposal is holding.
            var reader = new Thread(() => seenFromAnotherThread = a.IsDisposed);
            reader.IsBackground = true;
            reader.Start();
            reader.Join(TimeSpan.FromSeconds(10));
        };

        a.Dispose();

        Assert.False(seenFromAnotherThread);
        Assert.True(a.IsDisposed);

        // And the handoff itself happened: the refused container is tracked, with its driver.
        Assert.Equal(1, VanillaAttachRegistry.Count);
        Assert.True(VanillaAttachRegistry.HasWatch(Site));

        _client.RefuseDestroys = false;
        _client.Fire(Site, Open(11));
        Assert.Equal(0, VanillaAttachRegistry.Count);
    }

    [Fact]
    public void A_dispose_racing_a_frame_never_leaves_a_doomed_container_without_the_watch_that_retries_it()
    {
        // R61 finding 3. Dispose used to publish IsDisposed *before* it took its own gate, so a
        // frame landing in those few instructions saw a disposed handle whose doomed list had not
        // been filled yet, concluded it was finished, dropped it from the table and disposed the
        // site watch. The disposal then filed its refused container into a handle that was no
        // longer tracked, on a site with nothing left to drive the retry: no retry, no 30-frame
        // give-up, and not even the leak line. Both facts are now written under the gate with the
        // flag last, and the table re-reads both under its own lock.
        //
        // A three-instruction window cannot be hit on purpose, so this is the outcome guard and
        // the test above is the one that pins the ordering deterministically. What is asserted
        // here is the outcome, never the timing, so it cannot fail for being slow.
        const int Rounds = 200;
        for (int round = 0; round < Rounds; round++)
        {
            var runtime = new FakeAttachRuntime { IsClient = true };
            UiApi.ResetForTests();
            UiApi.SetRuntimeForTests(runtime);

            var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
            a.Row("T1");
            runtime.Fire(Site, Open(11));
            Assert.Equal(1, runtime.LiveContainers);

            // Unity refuses this destroy, so the container is still ours when Dispose returns and
            // something must be left to try again.
            runtime.RefuseDestroys = true;

            Exception? onFrame = null;
            var start = new Barrier(2);

            // Bounded well under the 30-frame give-up: a write-off is a correct outcome the
            // assertion would then not be able to tell from the defect.
            var frame = new Thread(() =>
            {
                try
                {
                    start.SignalAndWait();
                    for (int i = 0; i < 8; i++) runtime.Fire(Site, Open(11));
                }
                catch (Exception e) { onFrame = e; }
            });

            frame.IsBackground = true;
            frame.Start();
            start.SignalAndWait();
            a.Dispose();

            Assert.True(frame.Join(TimeSpan.FromSeconds(10)));
            Assert.Null(onFrame);

            // The container is alive (every destroy was refused), so the handle must still be in
            // the table and the site must still have the watch that retries it.
            Assert.Equal(1, runtime.LiveContainers);
            Assert.True(
                VanillaAttachRegistry.Count == 1 && VanillaAttachRegistry.HasWatch(Site),
                "round " + round + ": tracked=" + VanillaAttachRegistry.Count
                + " watch=" + VanillaAttachRegistry.HasWatch(Site));

            // And it is still a retry that works, not an entry nothing will ever clear.
            runtime.RefuseDestroys = false;
            runtime.Fire(Site, Open(11));

            Assert.Equal(0, runtime.LiveContainers);
            Assert.Equal(0, VanillaAttachRegistry.Count);
            Assert.False(VanillaAttachRegistry.HasWatch(Site));
        }
    }

    // -------------------------------------------------------- rows are written on change (R60 #6)

    [Fact]
    public void An_unchanged_row_is_not_written_into_the_widget_again()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        var row = a.Row("T1 next in 5m");
        _client.Fire(Site, Open(11));

        int writes = _client.RowWrites;

        // The Sample's rift rows push four times a second against a string that moves once a
        // second, so three pushes in four are identical. Only the changed one reaches the widget.
        a.SetRows(new[] { "T1 next in 5m" });
        _client.Fire(Site, Open(11));
        row.Text = "T1 next in 5m";
        Assert.Equal(writes, _client.RowWrites);

        row.Text = "T1 next in 4m";
        Assert.Equal(writes + 1, _client.RowWrites);
        Assert.Equal(new List<string> { "T1 next in 4m" }, _client.RowTexts);

        a.SetRows(new[] { "T1 next in 4m" });
        Assert.Equal(writes + 1, _client.RowWrites);
        a.Dispose();
    }

    // ---------------------------------------------------------------- the registry

    [Fact]
    public void RemoveAll_disposes_this_owners_attachments_and_counts_them()
    {
        var mine = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        var theirs = UiApi.AttachToVanilla(Site, new object(), "example.other");
        mine.Row("T1");
        theirs.Row("T1");
        _client.Fire(Site, Open(11));
        Assert.Equal(2, _client.LiveContainers);

        int n = UiApi.RemoveAll(_owner);

        Assert.Equal(1, n);
        Assert.True(mine.IsDisposed);
        Assert.False(theirs.IsDisposed);
        Assert.Equal(1, _client.LiveContainers);
        theirs.Dispose();
    }

    [Fact]
    public void RemoveAll_counts_attachments_alongside_panels_because_the_reload_log_is_one_number()
    {
        var panel = UiApi.Panel("Sample", _owner, "example.sample");
        var attach = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        Assert.True(panel.Enabled);
        Assert.True(attach.Enabled);

        Assert.Equal(2, UiApi.RemoveAll(_owner));
    }

    [Fact]
    public void RemoveAll_for_an_owner_with_nothing_attached_returns_zero()
    {
        Assert.Equal(0, UiApi.RemoveAll(new object()));
    }

    [Fact]
    public void An_owner_is_matched_by_reference_and_never_by_plugin_id_prefix()
    {
        // The IsOwnedBy rule stated where it actually bites: two plugins whose GUIDs extend one
        // another share nothing, and neither does a second instance of the same plugin type.
        var extra = new object();
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        var b = UiApi.AttachToVanilla(Site, extra, "example.sample.extra");

        Assert.Equal(1, UiApi.RemoveAll(extra));
        Assert.False(a.IsDisposed);
        Assert.True(b.IsDisposed);
        Assert.False(UiOwnership.IsOwnedBy(b.ContainerName, "example.sample"));
        a.Dispose();
    }

    [Fact]
    public void Core_Uninstall_sweeps_every_attachment_and_every_watch()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        var b = UiApi.AttachToVanilla(Site, new object(), "example.other");
        a.Row("T1");
        b.Row("T1");
        _client.Fire(Site, Open(11));
        Assert.Equal(2, _client.LiveContainers);

        UiApi.Uninstall();

        Assert.True(a.IsDisposed);
        Assert.True(b.IsDisposed);
        Assert.Equal(0, _client.LiveContainers);
        Assert.Equal(0, VanillaAttachRegistry.Count);
        Assert.Equal(0, VanillaAttachRegistry.WatchCount);
        Assert.Contains("attachments=2", Log());
    }

    [Fact]
    public void A_watch_installed_after_its_last_handle_is_gone_is_disposed_rather_than_kept()
    {
        // R60 finding 7. needWatch is read under the gate and the install runs outside it, which
        // is right — installing under a lock Dispatch also takes would be worse. The window it
        // opens is this: the handle is disposed while the watch is being built, its Remove finds
        // no watch to drop because none is registered yet, and the store then files a per-frame
        // Core-owned delegate for a site with nothing on it, which nothing but Core Uninstall
        // ever takes back. The disposal happens inside the installer here because that is exactly
        // the interleaving, without a second thread to make the test flaky.
        var handle = new VanillaAttachHandle(
            Site, "example.sample", "progenitor-ui-example.sample-MapWarEventInfo", _owner, _client, enabled: true);

        VanillaAttachRegistry.Add(handle, site =>
        {
            handle.Dispose();
            return _client.WatchSite(site, _ => { });
        });

        Assert.Equal(0, VanillaAttachRegistry.Count);
        Assert.False(VanillaAttachRegistry.HasWatch(Site));
        Assert.Equal(0, VanillaAttachRegistry.WatchCount);
        Assert.Equal(0, _client.WatchCount);        // the lease was given back, not leaked
        Assert.Equal(1, _client.WatchDisposals);
    }

    [Fact]
    public void The_pin_diagnostic_reports_attachments_their_hook_and_the_repair_tick()
    {
        VanillaAttachRegistry.UseHeartbeat(_ => new TestLease(() => { }));

        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        string pins = UiApi.DescribePins();

        Assert.Contains("attach=1", pins);
        Assert.Contains("attachSites=1", pins);
        Assert.Contains("attachHooks=1", pins);
        Assert.Contains("attachTick=True", pins);

        // R63 finding 1. The line goes when the table does — and it goes because the tick was
        // *stopped* with the last attachment, not because a still-running tick is unreportable.
        a.Dispose();
        Assert.False(VanillaAttachRegistry.HasRepairTickForTests);
        Assert.DoesNotContain("attachHooks", UiApi.DescribePins());
        Assert.DoesNotContain("attachTick", UiApi.DescribePins());
    }

    // ------------------------------- the tick lasts as long as the table (R63 #1)

    [Fact]
    public void The_repair_tick_stops_with_the_last_attachment_and_starts_again_with_the_next()
    {
        int installs = 0;
        int disposals = 0;
        VanillaAttachRegistry.UseHeartbeat(_ =>
        {
            installs++;
            return new TestLease(() => disposals++);
        });

        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        Assert.Equal(1, installs);
        Assert.True(VanillaAttachRegistry.HasRepairTickForTests);

        // The last attachment goes and takes its watch with it, so there is nothing left to
        // repair. A tick still running here is a per-frame Core-owned delegate walking an empty
        // list until Core Uninstall — the object InstallWatch's own comment refuses to file.
        a.Dispose();

        Assert.Equal(0, VanillaAttachRegistry.Count);
        Assert.Equal(0, VanillaAttachRegistry.WatchCount);
        Assert.False(VanillaAttachRegistry.HasRepairTickForTests);
        Assert.Equal(1, disposals);

        // ...and the next attachment gets one again, which EnsureHeartbeat was already written for.
        var b = UiApi.AttachToVanilla(Site, _owner, "example.sample");

        Assert.Equal(2, installs);
        Assert.True(VanillaAttachRegistry.HasRepairTickForTests);

        b.Dispose();
        Assert.False(VanillaAttachRegistry.HasRepairTickForTests);
        Assert.Equal(2, disposals);
    }

    [Fact]
    public void A_tick_whose_table_emptied_while_it_was_being_built_is_given_straight_back()
    {
        // R64 finding 2, and the interleave this test used to pin as correct behavior. The tick
        // is built outside the registry's gate — for the same reason a watch is — so the last
        // handle can go while it is being built, and Settle, the only path that stops a tick,
        // then finds none to stop. Preventing that needs no lock held across a call into the
        // Unity half: the factory has already returned by the time the filing lock is taken, so
        // the live table is re-asked there and the tick is disposed rather than filed — the same
        // second-lock guard InstallWatch makes for a watch (R60 finding 7).
        var handle = new VanillaAttachHandle(
            Site, "example.sample", "progenitor-ui-example.sample-MapWarEventInfo", _owner, _client, enabled: true);

        int disposals = 0;
        VanillaAttachRegistry.UseHeartbeat(_ =>
        {
            // The last attachment goes while the tick is under construction. Its Settle runs to
            // completion before the factory returns, so the emptiness is real and permanent.
            handle.Dispose();
            return new TestLease(() => disposals++);
        });

        VanillaAttachRegistry.Add(handle, site => _client.WatchSite(site, _ => { }));

        Assert.Equal(0, VanillaAttachRegistry.Count);
        Assert.Equal(0, VanillaAttachRegistry.WatchCount);

        // Stopped, not reported: nothing per-frame and Core-owned is left walking an empty table.
        Assert.False(VanillaAttachRegistry.HasRepairTickForTests);
        Assert.Equal(1, disposals);

        // And the pin line has nothing to say, because there is nothing held.
        string pins = UiApi.DescribePins();
        Assert.DoesNotContain("attachTick", pins);
        Assert.DoesNotContain("attach=", pins);

        // The next attachment still gets one: the give-back is a decline, not a latch.
        var b = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        Assert.True(VanillaAttachRegistry.HasRepairTickForTests);
        b.Dispose();
        Assert.False(VanillaAttachRegistry.HasRepairTickForTests);
        Assert.Equal(2, disposals);
    }

    [Fact]
    public void A_permanently_disabled_handle_is_not_something_the_repair_tick_stays_alive_for()
    {
        // R65 finding 2. Live holds every handle, disabled ones included, so "the table is empty"
        // and "nothing can need a repair" are different questions — and the tick's whole reason to
        // exist is the second one. Ui.AttachToVanilla files a permanently disabled handle for a
        // site Core does not know (and for a skipped module, and off the client), Enabled is
        // get-only, NeedsWatchLocked skips it and RepairLostWatches can never act for it. Asking
        // the first question let one such handle hold a per-frame Core-owned delegate over a list
        // it could not repair for the rest of the generation: R63 finding 1's object, by a path
        // R64 finding 2 did not consider.
        int installs = 0;
        int disposals = 0;
        VanillaAttachRegistry.UseHeartbeat(_ =>
        {
            installs++;
            return new TestLease(() => disposals++);
        });

        var real = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        var dead = UiApi.AttachToVanilla((VanillaUiSite)9999, _owner, "example.sample");

        Assert.False(dead.Enabled);
        Assert.Equal(1, installs);
        Assert.True(VanillaAttachRegistry.HasRepairTickForTests);

        // The only attachment that could ever lose a watch goes. What is left cannot be repaired,
        // so the driver of the repair goes with it rather than waiting for Uninstall.
        real.Dispose();

        Assert.Equal(1, VanillaAttachRegistry.Count);        // the disabled handle is still tracked
        Assert.Equal(0, VanillaAttachRegistry.WatchCount);
        Assert.False(VanillaAttachRegistry.HasRepairTickForTests);
        Assert.Equal(1, disposals);
        Assert.Equal(0, VanillaAttachRegistry.RepairLostWatches());

        // Still on the pin line, and now saying the true thing: something is held, nothing ticks.
        string pins = UiApi.DescribePins();
        Assert.Contains("attach=1", pins);
        Assert.Contains("attachTick=False", pins);

        // And a disabled handle on its own never starts one, so the next enabled attachment is
        // what does — the give-back is a decline, not a latch.
        var b = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        Assert.Equal(2, installs);
        Assert.True(VanillaAttachRegistry.HasRepairTickForTests);

        b.Dispose();
        dead.Dispose();
        Assert.False(VanillaAttachRegistry.HasRepairTickForTests);
        Assert.Equal(2, disposals);
    }

    [Fact]
    public void A_disabled_handle_alone_never_starts_the_repair_tick()
    {
        // The other half of R65 finding 2, at the start rather than the stop: EnsureHeartbeat runs
        // on every Add, including the Add of a handle that can never install a watch.
        int installs = 0;
        VanillaAttachRegistry.UseHeartbeat(_ =>
        {
            installs++;
            return new TestLease(() => { });
        });

        var dead = UiApi.AttachToVanilla((VanillaUiSite)9999, _owner, "example.sample");

        Assert.False(dead.Enabled);
        Assert.Equal(1, VanillaAttachRegistry.Count);
        Assert.Equal(0, installs);
        Assert.False(VanillaAttachRegistry.HasRepairTickForTests);

        dead.Dispose();
    }

    [Fact]
    public void The_attach_line_names_the_parent_the_layout_branch_and_the_instance()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, new VanillaSiteSample(true, 11, true, parentLayoutGroup: true, "MapCardHolder"));

        Assert.Contains(
            "Ui: attached MapWarEventInfo parent=MapCardHolder layout=ignored rows=1 source=first instance=11",
            Log());
        a.Dispose();
    }

    [Fact]
    public void A_watch_the_runtime_refuses_is_reported_rather_than_left_silently_dead()
    {
        _client.RefuseWatch = true;
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");

        Assert.True(a.Enabled);
        Assert.False(a.IsAttached);
        Assert.Contains("has no frame watch", Log());
        a.Dispose();
    }

    // ------------------------------------- a watch lost under a kept handle (R62 #2)

    [Fact]
    public void A_kept_handle_whose_site_lost_its_watch_gets_it_back_and_the_retry_then_proceeds()
    {
        // R62 finding 2, the exact state. The repair used to be reachable only from Apply, and
        // Apply only runs from the site watch — so the one state it existed for was the one state
        // it could never observe, because losing the watch also loses the caller.
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        _client.RefuseDestroys = true;
        a.Dispose();
        Assert.Equal(1, VanillaAttachRegistry.Count);
        Assert.True(VanillaAttachRegistry.HasWatch(Site));

        // The watch goes by a path that is not Settle's remove arm. Nothing drives the retry now:
        // the frame the game still fires reaches no subscriber at all.
        Assert.True(VanillaAttachRegistry.DropWatchForTests(Site));
        _client.Fire(Site, Open(11));
        Assert.Equal(1, _client.ContainerReleases);
        Assert.Equal(1, VanillaAttachRegistry.Count);

        Assert.Equal(1, VanillaAttachRegistry.RepairLostWatches());

        Assert.True(VanillaAttachRegistry.HasWatch(Site));
        Assert.Equal(1, CountLines("lost its frame watch while an attachment still needs it"));

        // ...and the point of the watch: the retry runs again and the container finally goes.
        _client.RefuseDestroys = false;
        _client.Fire(Site, Open(11));

        Assert.Equal(2, _client.ContainerReleases);
        Assert.Equal(0, _client.LiveContainers);
        Assert.Equal(0, VanillaAttachRegistry.Count);
        Assert.False(VanillaAttachRegistry.HasWatch(Site));
    }

    [Fact]
    public void The_repair_is_driven_by_a_frame_that_is_not_the_site_watch()
    {
        Action? heartbeat = null;
        int installs = 0;
        VanillaAttachRegistry.UseHeartbeat(tick =>
        {
            installs++;
            heartbeat = tick;
            return new TestLease(() => heartbeat = null);
        });

        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        Assert.Equal(1, installs);
        Assert.True(VanillaAttachRegistry.HasRepairTickForTests);
        Assert.NotNull(heartbeat);

        _client.RefuseDestroys = true;
        a.Dispose();
        Assert.True(VanillaAttachRegistry.DropWatchForTests(Site));

        // One frame of the driver, with no site watch anywhere in the chain that produced it.
        heartbeat!();

        Assert.True(VanillaAttachRegistry.HasWatch(Site));

        _client.RefuseDestroys = false;
        _client.Fire(Site, Open(11));
        Assert.Equal(0, VanillaAttachRegistry.Count);

        // And the driver is given back with the table, not left ticking into the next generation.
        UiApi.Uninstall();
        Assert.False(VanillaAttachRegistry.HasRepairTickForTests);
    }

    [Fact]
    public void A_watch_the_repair_cannot_rebuild_is_given_up_on_rather_than_retried_forever()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        _client.RefuseDestroys = true;
        a.Dispose();
        Assert.True(VanillaAttachRegistry.DropWatchForTests(Site));

        // The runtime now refuses to build one at all. An unbounded repair would try, log and
        // fail once a frame for the life of the generation.
        _client.RefuseWatch = true;
        int repaired = 0;
        for (int i = 0; i < 20; i++) repaired += VanillaAttachRegistry.RepairLostWatches();

        Assert.Equal(5, repaired);
        Assert.False(VanillaAttachRegistry.HasWatch(Site));
        Assert.Equal(1, CountLines("lost its frame watch while an attachment still needs it"));
        Assert.Equal(1, CountLines("gave up repairing its frame watch after 5 attempts"));
        Assert.Contains("watch=absent", Log());
    }

    // ------------------------- the bound counts rebuilds, not failures (R63 #2)

    [Fact]
    public void A_watch_that_is_rebuilt_and_lost_again_every_frame_reaches_the_same_give_up_line()
    {
        // R63 finding 2. The count used to be handed back the moment a watch was filed, so this
        // shape — every rebuild succeeds and every rebuild is lost again — spent one repair, one
        // allocation and one warning per frame for the life of the generation and never reached
        // the give-up line, because every loss after a success was "attempt 1" all over again.
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        _client.RefuseDestroys = true;
        a.Dispose();
        Assert.Equal(1, VanillaAttachRegistry.Count);

        int repaired = 0;
        for (int i = 0; i < 20; i++)
        {
            VanillaAttachRegistry.DropWatchForTests(Site);
            repaired += VanillaAttachRegistry.RepairLostWatches();
        }

        Assert.Equal(5, repaired);
        Assert.Equal(1, CountLines("lost its frame watch while an attachment still needs it"));
        Assert.Equal(1, CountLines("gave up repairing its frame watch after 5 attempts"));
        Assert.Contains("watch=rebuilt and lost again", Log());

        // Past the bound the repair does nothing at all: no install, no line, no allocation.
        Assert.Equal(0, VanillaAttachRegistry.RepairLostWatches());
        Assert.False(VanillaAttachRegistry.HasWatch(Site));
    }

    [Fact]
    public void The_repair_count_starts_again_once_the_site_stops_needing_a_watch()
    {
        // The one place the count is given back, and the reason it has to be given back: a site
        // whose five tries were spent by a handle that has since gone must not deny the repair to
        // the next attachment on it.
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        _client.RefuseDestroys = true;
        a.Dispose();
        for (int i = 0; i < 20; i++)
        {
            VanillaAttachRegistry.DropWatchForTests(Site);
            VanillaAttachRegistry.RepairLostWatches();
        }

        Assert.Equal(0, VanillaAttachRegistry.RepairLostWatches());

        // A second attachment brings a watch back, the first handle's destroy finally takes, and
        // the site empties — which is what clears the count.
        var b = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        _client.RefuseDestroys = false;
        _client.Fire(Site, Open(11));
        b.Dispose();
        Assert.Equal(0, VanillaAttachRegistry.Count);

        var c = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        c.Row("T1");
        _client.Fire(Site, Open(11));
        _client.RefuseDestroys = true;
        c.Dispose();
        Assert.True(VanillaAttachRegistry.DropWatchForTests(Site));

        Assert.Equal(1, VanillaAttachRegistry.RepairLostWatches());
        Assert.True(VanillaAttachRegistry.HasWatch(Site));
    }

    // ------------------------------- the verdict is asked for, never remembered (R62 #5)

    [Fact]
    public void A_failed_destroy_that_Unity_then_defers_is_released_exactly_twice()
    {
        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));

        // First release: both destroys refused, so the object is filed as a failure and retried.
        _client.RefuseDestroys = true;
        a.Dispose();
        Assert.Equal(1, _client.ContainerReleases);

        // The retry Unity queues instead of refusing. Latching the verdict at file time — R62
        // finding 5 — left this entry marked "failed" forever, so the handle kept calling Release
        // on an object Unity already owned, once a frame, for thirty frames.
        _client.RefuseDestroys = false;
        _client.DestroyVerdict = UiDestroyVerdict.Deferred;
        _client.Fire(Site, Open(11));

        Assert.Equal(2, _client.ContainerReleases);
        Assert.Equal(1, _client.LiveContainers);        // Unity has it; alive until end of frame

        _client.Fire(Site, Open(11));
        _client.Fire(Site, Open(11));
        Assert.Equal(2, _client.ContainerReleases);     // verified, never released again

        _client.EndFrame();
        _client.Fire(Site, Open(11));

        Assert.Equal(2, _client.ContainerReleases);
        Assert.Equal(0, _client.LiveContainers);
        Assert.Equal(0, VanillaAttachRegistry.Count);
        Assert.DoesNotContain("leaked", Log());
    }

    [Fact]
    public void A_widget_Unity_has_already_queued_is_filed_rather_than_released_again()
    {
        // R63 finding 6. ReleaseLocked is the handle's *other* Release() call site, and when the
        // widgets' private early-returns were dropped in favor of one implementation it was left
        // without any guard at all: the sweep asked the verdict, the first release did not. A
        // widget that already reports Deferred is one Unity has queued, and asking it to destroy
        // itself again from inside MapMenuMapper.Update is R61 finding 7 verbatim.
        _client.ContainersStartDeferred = true;

        var a = UiApi.AttachToVanilla(Site, _owner, "example.sample");
        a.Row("T1");
        _client.Fire(Site, Open(11));
        Assert.Equal(1, _client.LiveContainers);

        a.Dispose();

        // Not released — and still followed, because it is alive until the end of the frame.
        Assert.Equal(0, _client.ContainerReleases);
        Assert.Equal(1, VanillaAttachRegistry.Count);
        Assert.DoesNotContain("destroy did not take", Log());

        // Unity runs it at end of frame and the handle then settles out, as it does for any
        // deferred object.
        _client.EndFrame();
        _client.Fire(Site, Open(11));

        Assert.Equal(0, _client.LiveContainers);
        Assert.Equal(0, VanillaAttachRegistry.Count);
        Assert.DoesNotContain("leaked", Log());
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A disposable whose only job is to say when it was given back.</summary>
    private sealed class TestLease : IDisposable
    {
        private readonly Action _onDispose;

        internal TestLease(Action onDispose) => _onDispose = onDispose;

        public void Dispose() => _onDispose();
    }

    /// <summary>Everything the module logged this test, as one string.</summary>
    private string Log()
    {
        var all = new List<string>();
        all.AddRange(_log.Infos);
        all.AddRange(_log.Warnings);
        all.AddRange(_log.Errors);
        return string.Join(Environment.NewLine, all);
    }

    private static VanillaSiteSample Open(int instanceId) =>
        new(resolved: true, instanceId, donorPresent: true, parentLayoutGroup: false, "MapCard");

    private int CountLines(string needle)
    {
        int n = 0;
        string text = Log();
        int i = text.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            n++;
            i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
        }

        return n;
    }

    /// <summary>
    /// A runtime that records what the attachment asked it to do. Nothing here is Unity; the only
    /// thing it models is object lifetime, which is the whole of what the plan reasons about.
    /// </summary>
    private sealed class FakeAttachRuntime : IUiRuntime
    {
        private readonly List<FakeContainer> _containers = new();
        private readonly List<FakeContainer> _deferred = new();
        private readonly Dictionary<VanillaUiSite, Action<VanillaSiteSample>> _watches = new();

        public bool IsClient { get; set; } = true;

        public bool RefuseWatch { get; set; }

        public bool RefuseContainers { get; set; }

        public bool RefuseRows { get; set; }

        /// <summary>
        /// <c>Release</c> leaves the object alive — Unity's <c>DestroyImmediate</c> refusing from
        /// inside an engine callback, as the real widget models it by keeping its GameObject
        /// reference when the destroy did not take (R60 finding 1).
        /// </summary>
        public bool RefuseDestroys { get; set; }

        /// <summary>...and throws on the way, which is the shape the real one takes.</summary>
        public bool DestroysThrow { get; set; }

        /// <summary>
        /// Which of <see cref="UiDestroyVerdict"/> a release that is neither refused nor throwing
        /// reports. <c>Destroyed</c> is the ordinary case; <c>Deferred</c> is Unity having taken
        /// the destroy and not run it yet, which is the one this fake could not express before
        /// (R61 finding 7) and the one the module used to call a failure.
        /// </summary>
        public UiDestroyVerdict DestroyVerdict { get; set; } = UiDestroyVerdict.Destroyed;

        /// <summary>
        /// A container that reports <see cref="UiDestroyVerdict.Deferred"/> before anyone has
        /// released it: an object Unity already has queued, which is the state R63 finding 6 is
        /// about and the one shape this fake could not express. It is queued for end-of-frame
        /// death at build time, exactly as Unity would have it.
        /// </summary>
        public bool ContainersStartDeferred { get; set; }

        /// <summary>Every <c>Release</c> that reached a container, refused or not.</summary>
        public int ContainerReleases { get; private set; }

        /// <summary>
        /// Run from inside a container's <c>Release</c>, which is inside the handle's own gate.
        /// The only way to put another thread in the disposal's critical section without hoping
        /// two threads collide on their own (R61 finding 3).
        /// </summary>
        public Action? OnRelease { get; set; }

        /// <summary>Unity's end of frame: everything a deferred destroy queued actually dies.</summary>
        public void EndFrame()
        {
            var queued = _deferred.ToArray();
            _deferred.Clear();
            for (int i = 0; i < queued.Length; i++) queued[i].Kill();
        }

        internal void CountContainerRelease() => ContainerReleases++;

        internal void FireReleaseHook()
        {
            var hook = OnRelease;
            OnRelease = null;      // one shot: a retry must not spawn a second racer
            hook?.Invoke();
        }

        private void QueueDeferredCore(FakeContainer container) => _deferred.Add(container);

        /// <summary>Every text assignment that actually reached a row widget.</summary>
        public int RowWrites { get; private set; }

        internal void CountRowWrite() => RowWrites++;

        public int WatchCount => _watches.Count;

        public int WatchDisposals { get; private set; }

        public int ContainerBuilds { get; private set; }

        public int LiveContainers
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _containers.Count; i++)
                    if (_containers[i].Alive) n++;
                return n;
            }
        }

        public bool ContainerVisible
        {
            get
            {
                for (int i = 0; i < _containers.Count; i++)
                    if (_containers[i].Alive) return _containers[i].Visible;
                return false;
            }
        }

        /// <summary>Every live row's text, in order, across every live container.</summary>
        public List<string> RowTexts
        {
            get
            {
                var list = new List<string>();
                for (int i = 0; i < _containers.Count; i++)
                {
                    if (!_containers[i].Alive) continue;
                    var rows = _containers[i].Rows;
                    for (int j = 0; j < rows.Count; j++)
                        if (rows[j].Alive) list.Add(rows[j].Text);
                }

                return list;
            }
        }

        /// <summary>Drive one frame of the site's watch, as the game's system update would.</summary>
        public void Fire(VanillaUiSite site, VanillaSiteSample sample)
        {
            if (_watches.TryGetValue(site, out var cb)) cb(sample);
        }

        /// <summary>Unity destroyed the map view under us.</summary>
        public void KillContainers()
        {
            for (int i = 0; i < _containers.Count; i++) _containers[i].Kill();
        }

        public IDisposable? WatchSite(VanillaUiSite site, Action<VanillaSiteSample> onFrame)
        {
            if (RefuseWatch) return null;
            _watches[site] = onFrame;
            return new Lease(() => { _watches.Remove(site); WatchDisposals++; });
        }

        public IRuntimeWidget? BuildAttachContainer(
            VanillaUiSite site, string containerName, VanillaAttachParent parent, bool ignoreLayout)
        {
            if (RefuseContainers) return null;
            ContainerBuilds++;
            var c = new FakeContainer(containerName, this);
            _containers.Add(c);
            return c;
        }

        public IRuntimeWidget? CloneDonorRow(VanillaUiSite site, IRuntimeWidget container, string text)
        {
            if (RefuseRows) return null;
            if (container is not FakeContainer c || !c.Alive) return null;
            var row = new FakeRow(text, this);
            c.Rows.Add(row);
            return row;
        }

        // Nothing below is exercised by this file; the attach capability shares no state with the
        // panel surface, which is the point of it being a separate table.
        public IRuntimePanel? CreatePanel(string title, string rootName) => new FakePanel(title);

        public IRuntimeWidget? AddButton(IRuntimePanel panel, string text) => null;

        public IRuntimeWidget? AddToggle(IRuntimePanel panel, string text, bool initial) => null;

        public IRuntimeWidget? AddLabel(IRuntimePanel panel, string text) => null;

        public IRuntimeWidget? AddSlider(IRuntimePanel panel, string text, float min, float max, float initial) => null;

        public IRuntimeWidget? AddInput(IRuntimePanel panel, string text, string initial) => null;

        public void PlacePanel(IRuntimePanel panel, PanelSpot spot) { }

        public void Show(IRuntimePanel panel) { }

        public void Hide(IRuntimePanel panel) { }

        public bool TryWire(ushort widgetId, UiSlotKind kind, IRuntimeWidget widget) => false;

        public void Unwire(ushort widgetId) { }

        public void UnwireMany(IReadOnlyList<ushort> ids) { }

        public int DestroyRoot(string rootName) => 0;

        public int DestroyByPrefix(string prefix) => 0;

        public void SetCapture(bool capture) { }

        public ScreenRect PanelRect(IRuntimePanel panel) => ScreenRect.Unknown;

        public PointerSample ReadPointer() => PointerSample.Unreadable;

        private sealed class Lease : IDisposable
        {
            private Action? _off;

            internal Lease(Action off) => _off = off;

            public void Dispose()
            {
                var off = _off;
                _off = null;
                off?.Invoke();
            }
        }

        private sealed class FakeContainer : IRuntimeWidget, IReleaseVerdict
        {
            private readonly FakeAttachRuntime _owner;
            private bool _alive = true;
            private UiDestroyVerdict _verdict = UiDestroyVerdict.Failed;

            internal FakeContainer(string name, FakeAttachRuntime owner)
            {
                Name = name;
                _owner = owner;
                if (!owner.ContainersStartDeferred) return;
                _verdict = UiDestroyVerdict.Deferred;
                owner.QueueDeferredCore(this);
            }

            internal string Name { get; }

            internal List<FakeRow> Rows { get; } = new();

            public string Text { get => ""; set { } }

            public bool Visible { get; set; } = true;

            public bool BoolValue { get; set; }

            public float FloatValue { get; set; }

            public string StringValue { get; set; } = "";

            public bool Alive => _alive;

            public object? ClickEvent => null;

            public object? FloatEvent => null;

            public object? BoolEvent => null;

            public object? TextEvent => null;

            public object? PointerEnterEvent => null;

            public object? PointerExitEvent => null;

            public UiDestroyVerdict LastRelease => _verdict;

            internal void Kill()
            {
                _alive = false;
                for (int i = 0; i < Rows.Count; i++) Rows[i].Release();
            }

            public void Release()
            {
                _owner.CountContainerRelease();
                _owner.FireReleaseHook();
                if (_owner.DestroysThrow)
                {
                    _verdict = UiDestroyVerdict.Failed;
                    throw new InvalidOperationException("DestroyImmediate refused");
                }

                if (_owner.RefuseDestroys)
                {
                    _verdict = UiDestroyVerdict.Failed;
                    return;
                }

                if (_owner.DestroyVerdict == UiDestroyVerdict.Deferred)
                {
                    // Unity has it. Still alive this frame, gone at EndFrame, and it must not be
                    // asked to destroy itself again in between.
                    _verdict = UiDestroyVerdict.Deferred;
                    _owner.QueueDeferredCore(this);
                    return;
                }

                _verdict = UiDestroyVerdict.Destroyed;
                _alive = false;
                for (int i = 0; i < Rows.Count; i++) Rows[i].Release();
            }
        }

        private sealed class FakeRow : IRuntimeWidget
        {
            private readonly FakeAttachRuntime _owner;
            private bool _alive = true;
            private string _text;

            internal FakeRow(string text, FakeAttachRuntime owner)
            {
                _text = text ?? "";
                _owner = owner;
            }

            public string Text
            {
                get => _text;
                set
                {
                    _text = value ?? "";
                    _owner.CountRowWrite();
                }
            }

            public bool Visible { get; set; } = true;

            public bool BoolValue { get; set; }

            public float FloatValue { get; set; }

            public string StringValue { get; set; } = "";

            public bool Alive => _alive;

            public object? ClickEvent => null;

            public object? FloatEvent => null;

            public object? BoolEvent => null;

            public object? TextEvent => null;

            public object? PointerEnterEvent => null;

            public object? PointerExitEvent => null;

            public void Release() => _alive = false;
        }

        private sealed class FakePanel : IRuntimePanel
        {
            internal FakePanel(string title) => Text = title;

            public string Text { get; set; }

            public bool Visible { get; set; }

            public bool BoolValue { get; set; }

            public float FloatValue { get; set; }

            public string StringValue { get; set; } = "";

            public bool Alive { get; private set; } = true;

            public object? ClickEvent => null;

            public object? FloatEvent => null;

            public object? BoolEvent => null;

            public object? TextEvent => null;

            public object? PointerEnterEvent => null;

            public object? PointerExitEvent => null;

            public void Release() => Alive = false;
        }
    }
}
