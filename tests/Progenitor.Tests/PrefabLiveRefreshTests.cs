using System;
using System.IO;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of registry-driven live buff refresh (evidence 109): opt-in only,
/// never first process start, only after a changed definition / retarget / edit, one
/// instance per frame, and the exact log line.
/// </summary>
public class PrefabLiveRefreshTests
{
    [Fact]
    public void Refresh_is_off_when_not_opted_in()
    {
        Assert.False(PrefabLiveRefresh.ShouldRefresh(
            optIn: false, isBuff: true, alreadyClonedThisProcess: true,
            definitionChanged: true, retargeted: true, editPassChangedClone: true,
            previousFingerprintMissing: true));
    }

    [Fact]
    public void Refresh_is_off_for_items()
    {
        Assert.False(PrefabLiveRefresh.ShouldRefresh(
            optIn: true, isBuff: false, alreadyClonedThisProcess: true,
            definitionChanged: true, retargeted: false, editPassChangedClone: false,
            previousFingerprintMissing: false));
    }

    [Fact]
    public void Refresh_never_runs_on_first_process_start()
    {
        Assert.False(PrefabLiveRefresh.ShouldRefresh(
            optIn: true, isBuff: true, alreadyClonedThisProcess: false,
            definitionChanged: true, retargeted: true, editPassChangedClone: true,
            previousFingerprintMissing: true));
    }

    [Fact]
    public void Refresh_is_off_when_the_definition_did_not_change()
    {
        Assert.False(PrefabLiveRefresh.ShouldRefresh(
            optIn: true, isBuff: true, alreadyClonedThisProcess: true,
            definitionChanged: false, retargeted: false, editPassChangedClone: false,
            previousFingerprintMissing: false));
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    public void Refresh_runs_when_opted_in_after_a_changed_definition(
        bool definitionChanged, bool retargeted, bool editChanged, bool previousMissing)
    {
        Assert.True(PrefabLiveRefresh.ShouldRefresh(
            optIn: true, isBuff: true, alreadyClonedThisProcess: true,
            definitionChanged, retargeted, editChanged, previousMissing));
    }

    [Fact]
    public void Refresh_does_not_run_on_core_swap_when_the_definition_is_unchanged()
    {
        Assert.False(PrefabLiveRefresh.ShouldRefresh(
            optIn: true, isBuff: true, alreadyClonedThisProcess: true,
            definitionChanged: false, retargeted: false, editPassChangedClone: false,
            previousFingerprintMissing: true));
        Assert.True(PrefabLiveRefresh.ShouldLogSkip(
            optIn: true, isBuff: true, alreadyClonedThisProcess: true, willRefresh: false));
        Assert.Equal(
            "PrefabRegistry: refresh skipped: definition unchanged ('sample.nodrain')",
            PrefabLiveRefresh.FormatSkippedUnchanged("sample.nodrain"));
        Assert.Contains(
            PrefabLiveRefresh.SkippedUnchangedToken,
            PrefabLiveRefresh.FormatSkippedUnchanged("sample.nodrain"),
            StringComparison.Ordinal);
        Assert.False(PrefabLiveRefresh.ShouldLogSkip(
            optIn: true, isBuff: true, alreadyClonedThisProcess: false, willRefresh: false));
    }

    [Fact]
    public void RefreshOne_skips_a_lost_instance_and_preserves_stacks_and_duration()
    {
        Assert.True(PrefabLiveRefresh.ShouldSkipLostInstance(instanceExists: false, destroyedByUs: false));
        Assert.False(PrefabLiveRefresh.ShouldSkipLostInstance(instanceExists: true, destroyedByUs: false));
        Assert.False(PrefabLiveRefresh.ShouldSkipLostInstance(instanceExists: false, destroyedByUs: true));

        Assert.True(PrefabLiveRefresh.ShouldDestroyNow(instanceExists: true, destroyedByUs: false));
        Assert.True(PrefabLiveRefresh.ShouldReapply(instanceExists: false, destroyedByUs: true));
        // R29 finding 22.2: `Assert.Equal(2, DestroyWaitFrames)` was a constant asserting itself
        // and could not fail for a behavioral reason. What matters is that the state machine and
        // the constant agree, so drive the machine and count: a destroy whose entity never goes
        // away must produce exactly DestroyWaitFrames Waits and then Apply, whatever the number
        // is. This fails if ShouldWaitForDestroy's boundary drifts off the constant in either
        // direction, and it still fails if the machine stops waiting at all.
        Assert.True(PrefabLiveRefresh.DestroyWaitFrames > 0);
        var stubborn = new PrefabLiveRefresh.RefreshState();
        var step = PrefabLiveRefresh.RefreshStep.Skip;
        Assert.True(PrefabLiveRefresh.Advance(ref stubborn, instanceExists: true, out step));
        Assert.Equal(PrefabLiveRefresh.RefreshStep.Destroy, step);
        int waits = 0;
        while (PrefabLiveRefresh.Advance(ref stubborn, instanceExists: true, out step))
        {
            Assert.Equal(PrefabLiveRefresh.RefreshStep.Wait, step);
            waits++;
            Assert.True(waits <= PrefabLiveRefresh.DestroyWaitFrames, "the destroy wait is unbounded");
        }
        Assert.Equal(PrefabLiveRefresh.RefreshStep.Apply, step);
        Assert.Equal(PrefabLiveRefresh.DestroyWaitFrames, waits);

        Assert.Equal((byte)3, PrefabLiveRefresh.PreserveStacks(3));
        Assert.Equal((byte)1, PrefabLiveRefresh.PreserveStacks(0));
        Assert.True(PrefabLiveRefresh.ShouldRestoreDuration(hadLifeTime: true, duration: 12.5f));
        Assert.False(PrefabLiveRefresh.ShouldRestoreDuration(hadLifeTime: false, duration: 12.5f));
        Assert.False(PrefabLiveRefresh.ShouldRestoreDuration(hadLifeTime: true, duration: -1f));
        Assert.Contains("BuffSequenceSpawned", PrefabLiveRefresh.CannotPreserve, StringComparison.Ordinal);
        Assert.Contains("StartTimeUnmodified", PrefabLiveRefresh.CannotPreserve, StringComparison.Ordinal);
    }

    /// <summary>
    /// FT regression (evidence 131): the queue item is a struct. The destroy frame writes
    /// <c>DestroyedByUs</c>; if that write does not survive the re-queue, the next frame sees
    /// a never-destroyed item whose entity is gone, skips it as "lost", and the live buff is
    /// destroyed without ever being re-applied (<c>.nodrain off</c> answered "not present").
    /// </summary>
    [Fact]
    public void Destroy_state_survives_the_requeue_and_the_buff_is_reapplied()
    {
        var state = new PrefabLiveRefresh.RefreshState();

        // Frame 1: the instance is alive, we destroy it and ask to come back.
        Assert.True(PrefabLiveRefresh.Advance(ref state, instanceExists: true, out var first));
        Assert.Equal(PrefabLiveRefresh.RefreshStep.Destroy, first);
        Assert.True(state.DestroyedByUs);
        Assert.Equal(0, state.WaitFrames);

        // Frame 2: the old entity is gone and, because the state survived, we re-apply.
        Assert.False(PrefabLiveRefresh.Advance(ref state, instanceExists: false, out var second));
        Assert.Equal(PrefabLiveRefresh.RefreshStep.Apply, second);

        // The FS behavior: the destroy frame's state was lost, so frame 2 read a fresh
        // state and dropped the item — the buff stayed destroyed.
        var lost = new PrefabLiveRefresh.RefreshState();
        Assert.False(PrefabLiveRefresh.Advance(ref lost, instanceExists: false, out var dropped));
        Assert.Equal(PrefabLiveRefresh.RefreshStep.Skip, dropped);
    }

    [Fact]
    public void Deferred_destroy_waits_then_applies_and_a_lost_instance_is_dropped()
    {
        var state = new PrefabLiveRefresh.RefreshState();
        Assert.True(PrefabLiveRefresh.Advance(ref state, instanceExists: true, out var destroy));
        Assert.Equal(PrefabLiveRefresh.RefreshStep.Destroy, destroy);

        // Destroy is deferred: the old entity is still there. Wait, counting frames.
        for (int i = 0; i < PrefabLiveRefresh.DestroyWaitFrames; i++)
        {
            Assert.True(PrefabLiveRefresh.Advance(ref state, instanceExists: true, out var wait));
            Assert.Equal(PrefabLiveRefresh.RefreshStep.Wait, wait);
            Assert.Equal(i + 1, state.WaitFrames);
        }

        // After DestroyWaitFrames we apply anyway rather than looping forever.
        Assert.False(PrefabLiveRefresh.Advance(ref state, instanceExists: true, out var apply));
        Assert.Equal(PrefabLiveRefresh.RefreshStep.Apply, apply);

        // A buff that expired or was cleansed between enqueue and dequeue is never re-applied.
        var untouched = new PrefabLiveRefresh.RefreshState();
        Assert.False(PrefabLiveRefresh.Advance(ref untouched, instanceExists: false, out var skip));
        Assert.Equal(PrefabLiveRefresh.RefreshStep.Skip, skip);
        Assert.False(untouched.DestroyedByUs);
    }

    [Fact]
    public void Refresh_loop_passes_the_queue_item_by_reference_and_never_drops_a_requeue()
    {
        var apply = ReadContentFile("PrefabLiveRefreshApply.cs");
        SourcePin.AssertStatement(apply, "private static bool RefreshOne(World world, ref Pending item)");
        SourcePin.AssertStatement(apply, "if (RefreshOne(world, ref item))");
        SourcePin.AssertUncommented(apply, "PrefabLiveRefresh.Advance(ref state, exists, out var step)");
        Assert.DoesNotContain("RefreshOne(World world, Pending item)", apply, StringComparison.Ordinal);

        int requeue = apply.IndexOf("private static void Requeue(Pending item)", StringComparison.Ordinal);
        Assert.True(requeue > 0);
        int nextMethod = apply.IndexOf("private static void ReadPreserved(", requeue, StringComparison.Ordinal);
        Assert.True(nextMethod > requeue);
        string body = apply.Substring(requeue, nextMethod - requeue);
        // The dedupe set must not swallow a half-done item: we owe it an ApplyBuff.
        Assert.DoesNotContain("if (!QueuedKeys.Add(key)) return;", body, StringComparison.Ordinal);
        SourcePin.AssertStatement(body, "QueuedKeys.Add(key);");
    }

    [Fact]
    public void Fingerprint_includes_the_edit_stamp_so_an_unchanged_reload_does_not_refresh()
    {
        string same = PrefabLiveRefresh.Fingerprint(
            PrefabKind.Buff, 900030866, "Buff_General_Haste", "No Drain", "desc", "icons/nodrain.png",
            editStamp: "mod.a\0-1000\01");
        string sameAgain = PrefabLiveRefresh.Fingerprint(
            PrefabKind.Buff, 900030866, "Buff_General_Haste", "No Drain", "desc", "icons/nodrain.png",
            editStamp: "mod.a\0-1000\01");
        string edited = PrefabLiveRefresh.Fingerprint(
            PrefabKind.Buff, 900030866, "Buff_General_Haste", "No Drain", "desc", "icons/nodrain.png",
            editStamp: "mod.a\0-1000\01\0v2");
        Assert.False(PrefabLiveRefresh.DefinitionChanged(same, sameAgain));
        Assert.True(PrefabLiveRefresh.DefinitionChanged(same, edited));
        Assert.False(PrefabLiveRefresh.ShouldRefresh(
            optIn: true, isBuff: true, alreadyClonedThisProcess: true,
            definitionChanged: PrefabLiveRefresh.DefinitionChanged(same, sameAgain),
            retargeted: false, editPassChangedClone: false, previousFingerprintMissing: true));
    }

    [Fact]
    public void DefinitionChanged_is_false_when_there_is_no_previous_fingerprint()
    {
        Assert.False(PrefabLiveRefresh.DefinitionChanged(null, "1\n0\n\n\n\n"));
        Assert.False(PrefabLiveRefresh.DefinitionChanged("", "1\n0\n\n\n\n"));
    }

    [Fact]
    public void DefinitionChanged_detects_builder_field_edits()
    {
        string haste = PrefabLiveRefresh.Fingerprint(
            PrefabKind.Buff, 900030866, "Buff_General_Haste", "No Drain", "old", "icons/nodrain.png");
        string renamed = PrefabLiveRefresh.Fingerprint(
            PrefabKind.Buff, 900030866, "Buff_General_Haste", "No Drain", "new desc", "icons/nodrain.png");
        Assert.True(PrefabLiveRefresh.DefinitionChanged(haste, renamed));
        Assert.False(PrefabLiveRefresh.DefinitionChanged(haste, haste));
    }

    [Fact]
    public void Log_line_matches_the_server_contract()
    {
        Assert.Equal(
            "PrefabRegistry: refreshed 2 live instance(s) of 'sample.nodrain' (definition changed)",
            PrefabLiveRefresh.FormatRefreshed("sample.nodrain", 2, PrefabLiveRefresh.ReasonDefinitionChanged));
        Assert.Equal(
            "PrefabRegistry: refreshed 1 live instance(s) of 'sample.nodrain' (edit)",
            PrefabLiveRefresh.FormatRefreshed("sample.nodrain", 1, PrefabLiveRefresh.ReasonEdit));
        Assert.Equal(
            "PrefabRegistry: refreshed 3 live instance(s) of 'sample.nodrain' (retarget)",
            PrefabLiveRefresh.FormatRefreshed("sample.nodrain", 3, PrefabLiveRefresh.ReasonRetarget));
        Assert.Equal(
            "PrefabRegistry: refreshed 1 live instance(s) of 'sample.nodrain' (reload)",
            PrefabLiveRefresh.FormatRefreshed("sample.nodrain", 1, PrefabLiveRefresh.ReasonReload));
        Assert.Equal(
            "PrefabRegistry: refresh skipped: definition unchanged ('sample.nodrain')",
            PrefabLiveRefresh.FormatSkippedUnchanged("sample.nodrain"));
        Assert.Equal(
            "PrefabRegistry: nudged 1 live instance(s) of 'sample.nodrain' (icon)",
            PrefabLiveRefresh.FormatNudged("sample.nodrain", 1, PrefabLiveRefresh.ReasonIcon));
        Assert.Equal("icon", PrefabLiveRefresh.ReasonIcon);
    }

    [Fact]
    public void Nudge_counts_a_fake_instance_list()
    {
        var fake = new[] { "a", "dead", "c", "gone" };
        int n = PrefabLiveRefresh.CountNudgeTargets(fake, s => s != "dead" && s != "gone");
        Assert.Equal(2, n);
        Assert.True(PrefabLiveRefresh.ShouldLogNudge(n));
        Assert.False(PrefabLiveRefresh.ShouldLogNudge(0));
        Assert.Equal(0, PrefabLiveRefresh.CountNudgeTargets<string>(null, _ => true));
        Assert.Equal(3, PrefabLiveRefresh.CountNudgeTargets(new[] { "x", "y", "z" }, isLive: null));
        Assert.Equal(
            "PrefabRegistry: nudged 2 live instance(s) of 'sample.nodrain' (icon)",
            PrefabLiveRefresh.FormatNudged("sample.nodrain", n, PrefabLiveRefresh.ReasonIcon));
    }

    [Fact]
    public void PickReason_prefers_retarget_then_definition_then_edit()
    {
        Assert.Equal(
            PrefabLiveRefresh.ReasonRetarget,
            PrefabLiveRefresh.PickReason(true, true, true, true));
        Assert.Equal(
            PrefabLiveRefresh.ReasonDefinitionChanged,
            PrefabLiveRefresh.PickReason(true, false, true, true));
        Assert.Equal(
            PrefabLiveRefresh.ReasonEdit,
            PrefabLiveRefresh.PickReason(false, false, true, true));
        Assert.Equal(
            PrefabLiveRefresh.ReasonReload,
            PrefabLiveRefresh.PickReason(false, false, false, true));
    }

    [Fact]
    public void One_instance_per_frame()
    {
        // R29 finding 22.2: `Assert.Equal(1, InstancesPerFrame)` echoed the constant back at
        // itself. The behavior that matters is that a queue of three takes three frames — that
        // fails if the cap moves, if TakeThisFrame stops honoring it, or if it ever hands out
        // more than it was asked for.
        Assert.Equal(0, PrefabLiveRefresh.TakeThisFrame(0));
        int frames = 0;
        for (int remaining = 3; remaining > 0; frames++)
        {
            int take = PrefabLiveRefresh.TakeThisFrame(remaining);
            Assert.InRange(take, 1, PrefabLiveRefresh.InstancesPerFrame);
            Assert.True(take <= remaining);
            remaining -= take;
        }
        Assert.Equal(3, frames);

        var schedule = new LiveRefreshSchedule();
        schedule.Enqueue(3);
        Assert.True(schedule.TryTakeOne());
        Assert.Equal(2, schedule.Remaining);
        Assert.True(schedule.TryTakeOne());
        Assert.Equal(1, schedule.Remaining);
        Assert.True(schedule.TryTakeOne());
        Assert.Equal(0, schedule.Remaining);
        Assert.False(schedule.TryTakeOne());
    }

    [Fact]
    public void Fingerprints_are_remembered_until_Reset()
    {
        const int guid = 650563057;
        PrefabLiveRefresh.Reset();
        Assert.Null(PrefabLiveRefresh.LastFingerprint(guid));
        string fp = PrefabLiveRefresh.Fingerprint(
            PrefabKind.Buff, 900030866, "Buff_General_Haste", "No Drain", "desc", "icons/nodrain.png");
        PrefabLiveRefresh.RememberFingerprint(guid, fp);
        Assert.Equal(fp, PrefabLiveRefresh.LastFingerprint(guid));
        PrefabLiveRefresh.Reset();
        Assert.Null(PrefabLiveRefresh.LastFingerprint(guid));
    }

    [Fact]
    public void Registry_queues_refresh_after_apply_and_destroys_through_TryRemoveBuff()
    {
        var src = ReadContentFile("PrefabRegistry.cs");
        SourcePin.AssertStatement(src, "PrefabLiveRefreshApply.AfterCloneChanged");
        SourcePin.AssertStatement(src, "public BuffDefinitionBuilder RefreshLiveInstances()");
        SourcePin.AssertStatement(src, "_refreshLiveInstances");

        var apply = ReadContentFile("PrefabLiveRefreshApply.cs");
        SourcePin.AssertStatement(apply, "DestroyUtility.Destroy(em, item.Instance, DestroyDebugReason.TryRemoveBuff)");
        SourcePin.AssertStatement(apply, "des.ApplyBuff(from, evt)");
        SourcePin.AssertUncommented(apply, "PrefabLiveRefresh.Advance");
        SourcePin.AssertUncommented(ReadContentFile("PrefabLiveRefresh.cs"), "ShouldSkipLostInstance");
        SourcePin.AssertUncommented(apply, "PreserveStacks");
        SourcePin.AssertUncommented(apply, "ShouldRestoreDuration");
        SourcePin.AssertUncommented(apply, "FormatSkippedUnchanged");
        SourcePin.AssertUncommented(apply, "logNamedOnly: false");
        SourcePin.AssertUncommented(apply, "ComponentIndex.Of<Buff>");
        SourcePin.AssertUncommented(apply, "ComponentIndex.Of<PrefabGUID>");
        SourcePin.AssertStatement(apply, "BuffBarTextApply.TryApply(em, instance, def, logNamedOnly: false)");
        SourcePin.AssertStatement(apply, "BuffIconApply.TryNudge(em, instance, def)");
        SourcePin.AssertStatement(apply, "BuffBarDataAccess.TryCopyHud");
        Assert.DoesNotContain("entity.Remove", apply, StringComparison.Ordinal);
        SourcePin.AssertUncommented(apply, "PrefabLiveRefresh.FormatRefreshed");
        SourcePin.AssertUncommented(apply, "PrefabLiveRefresh.FormatNudged");
        SourcePin.AssertStatement(apply, "public static void AfterIconApplied");
        SourcePin.AssertUncommented(apply, "PrefabLiveRefresh.ReasonIcon");
        SourcePin.AssertUncommented(ReadContentFile("PrefabLiveRefresh.cs"), "InstancesPerFrame");
        var icon = ReadContentFile("BuffIconApply.cs");
        SourcePin.AssertStatement(icon, "PrefabLiveRefreshApply.AfterIconApplied(em, clone, def)");
    }

    [Fact]
    public void RefreshLiveInstances_is_not_a_wire_field()
    {
        var packet = ReadContentFile("PrefabDefinitionPacket.cs");
        Assert.DoesNotContain("RefreshLiveInstances", packet, StringComparison.Ordinal);
        var wire = ReadContentFile("PrefabWire.cs");
        Assert.DoesNotContain("RefreshLiveInstances", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void Icon_nudge_reuses_the_lease_sprite_and_does_not_redecode()
    {
        var src = ReadContentFile("BuffIconApply.cs");
        int nudge = src.IndexOf("public static void TryNudge", StringComparison.Ordinal);
        int apply = src.IndexOf("public static void TryApply", StringComparison.Ordinal);
        Assert.True(nudge >= 0 && apply > nudge);
        string body = src.Substring(nudge, apply - nudge);
        SourcePin.AssertUncommented(body, "lease.CurrentSprite");
        SourcePin.AssertUncommented(body, "TryGetCachedSprite");
        Assert.DoesNotContain("TryDecodeSprite", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TryReadPng", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// FU (R26 finding 4 remainder): the edit stamp used to carry the monotonic registration
    /// index and the invalidation counter, so the same plugin build re-registering after a
    /// plugin reload or a Core swap produced a different stamp — and every Core swap logged
    /// <c>refreshed 1 live instance(s) ... (definition changed)</c>, destroying the opted-in
    /// buff and re-firing its on-apply effects.
    /// </summary>
    [Fact]
    public void Edit_stamp_is_the_same_when_the_same_plugin_build_registers_again()
    {
        var table = new PrefabEditTable();
        AddEditor(table, "mod.sample", PrefabEditPriority.Default, out var handle);
        string first = table.Stamp(RefreshGuid);
        Assert.NotEqual("", first);

        // Plugin hot reload inside one Core generation: the row goes away and comes back with
        // the next registration index.
        handle.Dispose();
        Assert.Equal("", table.Stamp(RefreshGuid));
        AddEditor(table, "mod.sample", PrefabEditPriority.Default, out _);
        Assert.Equal(first, table.Stamp(RefreshGuid));

        // Core swap: a brand-new table (the old one died with the ALC) and the same plugin
        // build registering into it.
        var nextGeneration = new PrefabEditTable();
        AddEditor(nextGeneration, "mod.sample", PrefabEditPriority.Default, out _);
        Assert.Equal(first, nextGeneration.Stamp(RefreshGuid));

        // The invalidation counter is a per-generation counter, so it stays out of the stamp;
        // the apply fingerprint still moves so the editors themselves re-run.
        Assert.True(table.TrySnapshotOrdered(RefreshGuid, out var ordered, out _));
        string applyBefore = table.CurrentFingerprint(RefreshGuid, ordered);
        table.Invalidate(RefreshGuid);
        Assert.Equal(first, table.Stamp(RefreshGuid));
        Assert.NotEqual(applyBefore, table.CurrentFingerprint(RefreshGuid, ordered));
    }

    [Fact]
    public void Edit_stamp_moves_when_an_edit_is_added_removed_or_reprioritised()
    {
        var table = new PrefabEditTable();
        AddEditor(table, "mod.sample", PrefabEditPriority.Default, out var first);
        string one = table.Stamp(RefreshGuid);

        AddEditor(table, "mod.sample", PrefabEditPriority.Default, out var second);
        string two = table.Stamp(RefreshGuid);
        Assert.NotEqual(one, two);

        second.Dispose();
        Assert.Equal(one, table.Stamp(RefreshGuid));

        first.Dispose();
        AddEditor(table, "mod.sample", PrefabEditPriority.Late, out _);
        Assert.NotEqual(one, table.Stamp(RefreshGuid));

        // A different plugin, and a different version token, are both real changes.
        Assert.NotEqual(
            PrefabLiveRefresh.EditIdentity(new[]
            {
                new PrefabLiveRefresh.EditIdentityRow("mod.sample", 0, null),
            }),
            PrefabLiveRefresh.EditIdentity(new[]
            {
                new PrefabLiveRefresh.EditIdentityRow("mod.other", 0, null),
            }));
        Assert.NotEqual(
            PrefabLiveRefresh.EditIdentity(new[]
            {
                new PrefabLiveRefresh.EditIdentityRow("mod.sample", 0, "v1"),
            }),
            PrefabLiveRefresh.EditIdentity(new[]
            {
                new PrefabLiveRefresh.EditIdentityRow("mod.sample", 0, "v2"),
            }));
        Assert.Equal("", PrefabLiveRefresh.EditIdentity(null));
        Assert.Equal("", PrefabLiveRefresh.EditIdentity(Array.Empty<PrefabLiveRefresh.EditIdentityRow>()));
    }

    /// <summary>
    /// The whole definition hash of an unchanged plugin build must survive a Core swap: same
    /// source, same icon/name/description keys, same editor set registered again.
    ///
    /// R27 finding 24.4: this used to compare two locally computed strings, so it could pass
    /// while production compared against <c>null</c>. <paramref name="previous"/> now comes out
    /// of the real store after a real swap (<c>Reset</c> + the Bootstrap handoff), which is the
    /// only way the value production actually reads is under test.
    /// </summary>
    [Fact]
    public void Definition_fingerprint_survives_a_swap_and_moves_on_a_real_change()
    {
        var before = new PrefabEditTable();
        AddEditor(before, "mod.sample", PrefabEditPriority.Default, out _);
        string previous = SwapWithHandoff(Fingerprint(before.Stamp(RefreshGuid)));

        var after = new PrefabEditTable();
        AddEditor(after, "mod.sample", PrefabEditPriority.Default, out _);
        Assert.False(PrefabLiveRefresh.DefinitionChanged(previous, Fingerprint(after.Stamp(RefreshGuid))));

        var withExtraEdit = new PrefabEditTable();
        AddEditor(withExtraEdit, "mod.sample", PrefabEditPriority.Default, out _);
        AddEditor(withExtraEdit, "mod.sample", PrefabEditPriority.Late, out _);
        Assert.True(PrefabLiveRefresh.DefinitionChanged(previous, Fingerprint(withExtraEdit.Stamp(RefreshGuid))));

        Assert.True(PrefabLiveRefresh.DefinitionChanged(
            previous, Fingerprint(after.Stamp(RefreshGuid), iconPath: "icons/other.png")));
        Assert.True(PrefabLiveRefresh.DefinitionChanged(
            previous, Fingerprint(after.Stamp(RefreshGuid), localizedName: "Renamed")));
        Assert.True(PrefabLiveRefresh.DefinitionChanged(
            previous, Fingerprint(after.Stamp(RefreshGuid), sourceHash: 12345)));
    }

    /// <summary>
    /// The swap-time double evaluation: the clone apply runs before the plugin's Content.Edit
    /// passes are registered, the edit pass runs after. Both phases note; one settle decides.
    /// Before FU the first phase logged "refresh skipped: definition unchanged" and the second
    /// logged "refreshed 1 live instance(s) ... (definition changed)" for the same swap.
    ///
    /// R27 finding 24.4: both notes used to be handed a literal non-null previous fingerprint,
    /// a state production never reached after a swap because the store had just been cleared.
    /// The previous value now comes from <c>PrefabLiveRefresh.LastFingerprint</c> after a real
    /// swap, exactly as <c>PrefabRegistry.NoteLiveRefresh</c> reads it.
    /// </summary>
    [Fact]
    public void Two_apply_phases_of_one_swap_produce_one_decision()
    {
        var decisions = new LiveRefreshDecisions();
        var table = new PrefabEditTable();
        AddEditor(table, "mod.sample", PrefabEditPriority.Default, out _);
        string settled = Fingerprint(table.Stamp(RefreshGuid));
        Assert.Equal(settled, SwapWithHandoff(settled));

        // Phase 1: the clone was re-applied; no editors are registered yet, so the fingerprint
        // this phase could compute would be missing the edit identity. It records nothing.
        decisions.Note(
            RefreshGuid, alreadyClonedThisProcess: true, retargeted: false,
            editPassWrote: false, inputsChanged: false,
            previousFingerprint: PrefabLiveRefresh.LastFingerprint(RefreshGuid));
        // Phase 2: the plugin re-registered its editors and they wrote the clone.
        decisions.Note(
            RefreshGuid, alreadyClonedThisProcess: true, retargeted: false,
            editPassWrote: true, inputsChanged: false,
            previousFingerprint: PrefabLiveRefresh.LastFingerprint(RefreshGuid));

        Assert.Equal(1, decisions.Count);
        Assert.Single(decisions.PendingGuids());

        Assert.True(decisions.TrySettle(RefreshGuid, settled, out var decision));
        Assert.False(decision.DefinitionChanged);
        Assert.False(decision.EditPassChangedClone);
        Assert.True(decision.AlreadyClonedThisProcess);
        Assert.False(decision.PreviousFingerprintMissing);

        bool willRefresh = PrefabLiveRefresh.ShouldRefresh(
            optIn: true, isBuff: true, decision.AlreadyClonedThisProcess,
            decision.DefinitionChanged, decision.Retargeted,
            decision.EditPassChangedClone, decision.PreviousFingerprintMissing);
        Assert.False(willRefresh);
        Assert.True(PrefabLiveRefresh.ShouldLogSkip(
            optIn: true, isBuff: true, decision.AlreadyClonedThisProcess, willRefresh));

        // One decision per apply: a second settle has nothing to log, so the swap can never
        // produce both lines.
        Assert.False(decisions.TrySettle(RefreshGuid, settled, out _));
        Assert.Equal(0, decisions.Count);
    }

    [Fact]
    public void A_changed_plugin_build_still_refreshes_and_a_config_invalidate_counts_as_an_edit()
    {
        var decisions = new LiveRefreshDecisions();
        var before = new PrefabEditTable();
        AddEditor(before, "mod.sample", PrefabEditPriority.Default, out _);
        string previous = Fingerprint(before.Stamp(RefreshGuid));

        var withExtraEdit = new PrefabEditTable();
        AddEditor(withExtraEdit, "mod.sample", PrefabEditPriority.Default, out _);
        AddEditor(withExtraEdit, "mod.sample", PrefabEditPriority.Late, out _);

        decisions.Note(
            RefreshGuid, alreadyClonedThisProcess: true, retargeted: false,
            editPassWrote: false, inputsChanged: false, previousFingerprint: previous);
        decisions.Note(
            RefreshGuid, alreadyClonedThisProcess: true, retargeted: false,
            editPassWrote: true, inputsChanged: false, previousFingerprint: previous);
        Assert.True(decisions.TrySettle(RefreshGuid, Fingerprint(withExtraEdit.Stamp(RefreshGuid)), out var changed));
        Assert.True(changed.DefinitionChanged);
        Assert.True(PrefabLiveRefresh.ShouldRefresh(
            optIn: true, isBuff: true, changed.AlreadyClonedThisProcess,
            changed.DefinitionChanged, changed.Retargeted,
            changed.EditPassChangedClone, changed.PreviousFingerprintMissing));
        Assert.Equal(
            PrefabLiveRefresh.ReasonDefinitionChanged,
            PrefabLiveRefresh.PickReason(
                changed.DefinitionChanged, changed.Retargeted,
                changed.EditPassChangedClone, changed.PreviousFingerprintMissing));

        // Captured editor values moved (a cfg reload calls Invalidate). No build-stable
        // fingerprint can see that, so the InputsChanged re-run carries it.
        decisions.Note(
            RefreshGuid, alreadyClonedThisProcess: true, retargeted: false,
            editPassWrote: true, inputsChanged: true, previousFingerprint: previous);
        Assert.True(decisions.TrySettle(RefreshGuid, previous, out var invalidated));
        Assert.False(invalidated.DefinitionChanged);
        Assert.True(invalidated.EditPassChangedClone);
        Assert.True(PrefabLiveRefresh.ShouldRefresh(
            optIn: true, isBuff: true, invalidated.AlreadyClonedThisProcess,
            invalidated.DefinitionChanged, invalidated.Retargeted,
            invalidated.EditPassChangedClone, invalidated.PreviousFingerprintMissing));

        // A retarget is still a refresh, and Clear drops everything at Core Uninstall.
        decisions.Note(
            RefreshGuid, alreadyClonedThisProcess: true, retargeted: true,
            editPassWrote: false, inputsChanged: false, previousFingerprint: previous);
        Assert.Equal(1, decisions.Count);
        decisions.Clear();
        Assert.Equal(0, decisions.Count);
        Assert.False(decisions.TrySettle(RefreshGuid, previous, out _));
    }

    /// <summary>
    /// The registry must not record a fingerprint mid-apply: the settle is the only writer,
    /// and both apply phases only note.
    /// </summary>
    [Fact]
    public void Registry_records_the_fingerprint_only_once_the_editors_are_registered()
    {
        var src = ReadContentFile("PrefabRegistry.cs");
        SourcePin.AssertStatement(src, "private static void NoteLiveRefresh(");
        SourcePin.AssertStatement(src, "private static void SettleLiveRefresh()");
        SourcePin.AssertStatement(src, "RefreshDecisions.Note(");
        SourcePin.AssertUncommented(src, "RefreshDecisions.TrySettle(guidHash, currentFp, out var decision)");

        // Exactly one RememberFingerprint call, and it is inside the settle.
        int remember = src.IndexOf("PrefabLiveRefresh.RememberFingerprint(", StringComparison.Ordinal);
        Assert.True(remember > 0);
        Assert.Equal(
            remember,
            src.LastIndexOf("PrefabLiveRefresh.RememberFingerprint(", StringComparison.Ordinal));
        int settle = src.IndexOf("private static void SettleLiveRefresh()", StringComparison.Ordinal);
        Assert.True(settle > 0 && remember > settle);

        // AfterCloneChanged on the server runs from the settle only; the client nudge stays
        // immediate because it never destroys an instance.
        SourcePin.AssertStatement(src, "if (!Plugin.IsServerProcess)");

        var stamp = ReadContentFile("PrefabEditTable.cs");
        SourcePin.AssertStatement(stamp, "public static string StableStamp(");
        int stableStamp = stamp.IndexOf("public string Stamp(int guidHash)", StringComparison.Ordinal);
        int nextMember = stamp.IndexOf("public static string StableStamp(", stableStamp, StringComparison.Ordinal);
        string body = stamp.Substring(stableStamp, nextMember - stableStamp);
        SourcePin.AssertUncommented(body, "StableStamp(ordered)");
        Assert.DoesNotContain("CurrentFingerprint(", body, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // R27 finding 1 — the previous fingerprint must survive a Core swap
    // ------------------------------------------------------------------

    /// <summary>
    /// The exact transition the opt-in refresh exists for. Generation 1 settles a fingerprint;
    /// the swap exports it into Bootstrap <c>ContentMemory</c>, <c>Uninstall</c> clears the Core
    /// static, generation 2 imports it, and the first apply of generation 2 sees a real previous
    /// fingerprint — so a plugin whose icon and name changed actually refreshes.
    ///
    /// Fails on the pre-FZ code: there was no export/import at all, so the store was empty after
    /// <c>Reset</c>, <c>LastFingerprint</c> returned null, <c>DefinitionChanged(null, x)</c> was
    /// false by construction and <c>ShouldRefresh</c> returned false. The sibling test below
    /// asserts that exact old outcome so the pair brackets the fix.
    /// </summary>
    [Fact]
    public void Live_refresh_fires_on_the_first_apply_after_a_core_swap()
    {
        var gen1Editors = new PrefabEditTable();
        AddEditor(gen1Editors, "mod.sample", PrefabEditPriority.Default, out _);
        string gen1 = Fingerprint(gen1Editors.Stamp(RefreshGuid));

        string carried = SwapWithHandoff(gen1);
        Assert.Equal(gen1, carried);
        Assert.Equal(gen1, PrefabLiveRefresh.LastFingerprint(RefreshGuid));

        // Generation 2 loaded a rebuilt plugin: same editor set, new icon and name.
        var gen2Editors = new PrefabEditTable();
        AddEditor(gen2Editors, "mod.sample", PrefabEditPriority.Default, out _);
        string gen2 = Fingerprint(
            gen2Editors.Stamp(RefreshGuid), localizedName: "No Drain II", iconPath: "icons/nodrain2.png");

        var decisions = new LiveRefreshDecisions();
        decisions.Note(
            RefreshGuid, alreadyClonedThisProcess: true, retargeted: false,
            editPassWrote: false, inputsChanged: false,
            previousFingerprint: PrefabLiveRefresh.LastFingerprint(RefreshGuid));
        decisions.Note(
            RefreshGuid, alreadyClonedThisProcess: true, retargeted: false,
            editPassWrote: true, inputsChanged: false,
            previousFingerprint: PrefabLiveRefresh.LastFingerprint(RefreshGuid));

        Assert.True(decisions.TrySettle(RefreshGuid, gen2, out var decision));
        Assert.False(decision.PreviousFingerprintMissing);
        Assert.True(decision.DefinitionChanged);
        Assert.True(PrefabLiveRefresh.ShouldRefresh(
            optIn: true, isBuff: true, decision.AlreadyClonedThisProcess,
            decision.DefinitionChanged, decision.Retargeted,
            decision.EditPassChangedClone, decision.PreviousFingerprintMissing));
        Assert.Equal(
            PrefabLiveRefresh.ReasonDefinitionChanged,
            PrefabLiveRefresh.PickReason(
                decision.DefinitionChanged, decision.Retargeted,
                decision.EditPassChangedClone, decision.PreviousFingerprintMissing));
    }

    /// <summary>
    /// The pre-FZ behavior, pinned so it cannot come back silently: without the handoff the
    /// new generation compares against null, which is not a change, and a genuinely rebuilt
    /// plugin never refreshes its live buffs.
    /// </summary>
    [Fact]
    public void Without_the_handoff_a_swap_can_never_see_a_definition_change()
    {
        PrefabLiveRefresh.Reset();
        PrefabLiveRefresh.RememberFingerprint(RefreshGuid, Fingerprint("gen1"));
        PrefabLiveRefresh.Reset(); // Uninstall, and nothing carried the map over.

        Assert.Null(PrefabLiveRefresh.LastFingerprint(RefreshGuid));
        var decisions = new LiveRefreshDecisions();
        decisions.Note(
            RefreshGuid, alreadyClonedThisProcess: true, retargeted: false,
            editPassWrote: true, inputsChanged: false,
            previousFingerprint: PrefabLiveRefresh.LastFingerprint(RefreshGuid));
        Assert.True(decisions.TrySettle(RefreshGuid, Fingerprint("gen2-different"), out var decision));
        Assert.True(decision.PreviousFingerprintMissing);
        Assert.False(decision.DefinitionChanged);
        Assert.False(PrefabLiveRefresh.ShouldRefresh(
            optIn: true, isBuff: true, decision.AlreadyClonedThisProcess,
            decision.DefinitionChanged, decision.Retargeted,
            decision.EditPassChangedClone, decision.PreviousFingerprintMissing));
    }

    /// <summary>
    /// The handoff store itself: round-trip, copies (not shared arrays), and a length mismatch
    /// storing nothing rather than pairing a fingerprint with the wrong GUID.
    /// </summary>
    [Fact]
    public void ContentMemory_carries_fingerprints_and_refuses_a_mismatched_pair()
    {
        var mem = new Progenitor.Bootstrap.ContentMemory();
        Assert.Equal(0, mem.FingerprintCount);

        var guids = new[] { RefreshGuid, 42 };
        var prints = new[] { "a", "b" };
        Assert.Equal(Progenitor.Bootstrap.ContentMemoryStore.Replaced, mem.ReplaceFingerprints(guids, prints));
        Assert.Equal(2, mem.FingerprintCount);

        guids[0] = -1;
        prints[0] = "tampered";
        mem.TakeFingerprints(out var outGuids, out var outPrints);
        Assert.Equal(new[] { RefreshGuid, 42 }, outGuids);
        Assert.Equal(new[] { "a", "b" }, outPrints);

        Assert.Equal(
            Progenitor.Bootstrap.ContentMemoryStore.KeptLengthMismatch,
            mem.ReplaceFingerprints(new[] { 1, 2 }, new[] { "only-one" }));
    }

    /// <summary>
    /// R28 finding 16. The store is the only thing that crosses a collectible Core ALC unload,
    /// and it used to accept an empty or mismatched write as "the set is now empty". One
    /// swallowed rehydrate (a throw in <c>ImportClonesAndKeys</c> before the fingerprint read)
    /// therefore gave generation N+1 an empty map, and <em>its</em> export wiped the store for
    /// the rest of the process — every later swap compared against no previous fingerprint at
    /// all, which is R27 finding 1's behavior restored permanently and in silence. Since the
    /// import side merges, an empty export carries no information.
    ///
    /// Fails on the pre-FZ2 code: <c>ReplaceFingerprints</c> / <c>ReplaceCloneSources</c>
    /// returned <c>void</c> and both of the <c>FingerprintCount</c> / <c>CloneSourceCount</c>
    /// assertions after the empty and mismatched writes read 0 instead of 2.
    /// </summary>
    [Fact]
    public void An_empty_or_mismatched_snapshot_keeps_the_previous_generations_rows()
    {
        var mem = new Progenitor.Bootstrap.ContentMemory();
        mem.ReplaceFingerprints(new[] { RefreshGuid, 42 }, new[] { "a", "b" });
        Assert.Equal(2, mem.FingerprintCount);

        // Generation N+1 settled nothing (its rehydrate was swallowed): it must not wipe the
        // rows generation N left behind.
        Assert.Equal(
            Progenitor.Bootstrap.ContentMemoryStore.KeptNothingOffered,
            mem.ReplaceFingerprints(Array.Empty<int>(), Array.Empty<string>()));
        Assert.Equal(2, mem.FingerprintCount);
        Assert.Equal(
            Progenitor.Bootstrap.ContentMemoryStore.KeptNothingOffered,
            mem.ReplaceFingerprints(null, null));
        Assert.Equal(2, mem.FingerprintCount);
        Assert.Equal(
            Progenitor.Bootstrap.ContentMemoryStore.KeptLengthMismatch,
            mem.ReplaceFingerprints(new[] { 1, 2, 3 }, new[] { "x" }));
        Assert.Equal(2, mem.FingerprintCount);
        mem.TakeFingerprints(out var stillThere, out _);
        Assert.Equal(new[] { RefreshGuid, 42 }, stillThere);

        // The CloneOf store — the FA reclaim half of the same defect — behaves identically.
        mem.ReplaceCloneSources(new[] { RefreshGuid }, new[] { 900030866 });
        Assert.Equal(1, mem.CloneSourceCount);
        Assert.Equal(
            Progenitor.Bootstrap.ContentMemoryStore.KeptNothingOffered,
            mem.ReplaceCloneSources(Array.Empty<int>(), Array.Empty<int>()));
        Assert.Equal(1, mem.CloneSourceCount);
        Assert.Equal(
            Progenitor.Bootstrap.ContentMemoryStore.KeptLengthMismatch,
            mem.ReplaceCloneSources(new[] { 1, 2 }, new[] { 3 }));
        Assert.Equal(1, mem.CloneSourceCount);

        // A real snapshot still replaces.
        Assert.Equal(
            Progenitor.Bootstrap.ContentMemoryStore.Replaced,
            mem.ReplaceFingerprints(new[] { 7 }, new[] { "later" }));
        Assert.Equal(1, mem.FingerprintCount);
    }

    /// <summary>The store outcome is reported in words the log can carry.</summary>
    [Fact]
    public void ContentMemory_store_outcomes_are_logged_with_counts()
    {
        Assert.Equal(
            "ContentMemory: stored 3 live-refresh fingerprint(s) for the next Core generation.",
            Progenitor.Bootstrap.ContentMemoryLog.FormatStore(
                Progenitor.Bootstrap.ContentMemoryStore.Replaced, "live-refresh fingerprint", 3, 3, 3));
        Assert.Equal(
            "ContentMemory: refused a live-refresh fingerprint snapshot whose arrays disagree"
            + " (keys=3 values=1); kept the 2 row(s) already stored.",
            Progenitor.Bootstrap.ContentMemoryLog.FormatStore(
                Progenitor.Bootstrap.ContentMemoryStore.KeptLengthMismatch, "live-refresh fingerprint", 3, 1, 2));
        Assert.Equal(
            "ContentMemory: CloneOf hash snapshot offered nothing; kept the 4 row(s) already"
            + " stored (an empty export carries no information).",
            Progenitor.Bootstrap.ContentMemoryLog.FormatStore(
                Progenitor.Bootstrap.ContentMemoryStore.KeptNothingOffered, "CloneOf hash", 0, 0, 4));
    }

    /// <summary>Import merges and never clobbers a fingerprint this generation already recorded.</summary>
    [Fact]
    public void Importing_fingerprints_does_not_overwrite_this_generation()
    {
        PrefabLiveRefresh.Reset();
        PrefabLiveRefresh.RememberFingerprint(RefreshGuid, "fresh");
        Assert.Equal(2, PrefabLiveRefresh.ImportFingerprints(new[] { RefreshGuid, 7 }, new[] { "stale", "carried" }));
        Assert.Equal("fresh", PrefabLiveRefresh.LastFingerprint(RefreshGuid));
        Assert.Equal("carried", PrefabLiveRefresh.LastFingerprint(7));

        Assert.Equal(0, PrefabLiveRefresh.ImportFingerprints(null, null));
        Assert.Equal(0, PrefabLiveRefresh.ImportFingerprints(new[] { 1, 2 }, new[] { "x" }));
        PrefabLiveRefresh.Reset();
    }

    /// <summary>
    /// R28 finding 16, import half: a rehydrate that takes nothing must say so with counts.
    /// The old <c>ImportFingerprints</c> returned the offered length whether or not anything
    /// merged and had no failure channel at all, so the caller logged "rehydrated 2" for a
    /// no-op and logged nothing whatever for a mismatch or an empty store — the one signal that
    /// the handoff had broken.
    ///
    /// Fails on the pre-FZ2 code: the four-argument overload, the
    /// <c>FingerprintImport</c> enum and <c>FormatFingerprintImport</c> did not exist.
    /// </summary>
    [Fact]
    public void A_rehydrate_that_takes_nothing_reports_why()
    {
        PrefabLiveRefresh.Reset();

        int offered = PrefabLiveRefresh.ImportFingerprints(
            new[] { RefreshGuid, 7 }, new[] { "a", "b" }, out int added, out var status);
        Assert.Equal(2, offered);
        Assert.Equal(2, added);
        Assert.Equal(PrefabLiveRefresh.FingerprintImport.Taken, status);

        // Same rows again: benign, but "merged 0" must not read as a successful rehydrate.
        offered = PrefabLiveRefresh.ImportFingerprints(
            new[] { RefreshGuid, 7 }, new[] { "a", "b" }, out added, out status);
        Assert.Equal(2, offered);
        Assert.Equal(0, added);
        Assert.Equal(PrefabLiveRefresh.FingerprintImport.AllAlreadyPresent, status);

        Assert.Equal(0, PrefabLiveRefresh.ImportFingerprints(null, null, out added, out status));
        Assert.Equal(0, added);
        Assert.Equal(PrefabLiveRefresh.FingerprintImport.NothingOffered, status);

        Assert.Equal(0, PrefabLiveRefresh.ImportFingerprints(
            Array.Empty<int>(), Array.Empty<string>(), out added, out status));
        Assert.Equal(PrefabLiveRefresh.FingerprintImport.NothingOffered, status);

        Assert.Equal(0, PrefabLiveRefresh.ImportFingerprints(
            new[] { 1, 2, 3 }, new[] { "x" }, out added, out status));
        Assert.Equal(0, added);
        Assert.Equal(PrefabLiveRefresh.FingerprintImport.LengthMismatch, status);
        Assert.Null(PrefabLiveRefresh.LastFingerprint(1));   // a mismatch pairs nothing

        Assert.Equal(
            "PrefabRegistry: live-refresh fingerprint rehydrate merged 2 of 3 offered row(s).",
            PrefabLiveRefresh.FormatFingerprintImport(PrefabLiveRefresh.FingerprintImport.Taken, 3, 3, 2));
        Assert.Contains(
            "took nothing: the handoff arrays disagree (guids=3 fingerprints=1)",
            PrefabLiveRefresh.FormatFingerprintImport(PrefabLiveRefresh.FingerprintImport.LengthMismatch, 3, 1, 0),
            StringComparison.Ordinal);
        Assert.Contains(
            "took nothing: Bootstrap ContentMemory held no rows",
            PrefabLiveRefresh.FormatFingerprintImport(PrefabLiveRefresh.FingerprintImport.NothingOffered, 0, 0, 0),
            StringComparison.Ordinal);
        Assert.Contains(
            "this generation had already settled all of them",
            PrefabLiveRefresh.FormatFingerprintImport(PrefabLiveRefresh.FingerprintImport.AllAlreadyPresent, 2, 2, 0),
            StringComparison.Ordinal);

        PrefabLiveRefresh.Reset();
    }

    /// <summary>
    /// R28 finding 16, export half: one unusable row must not cost the handoff. An empty
    /// fingerprint is not a settled value — <c>Fingerprint()</c> never produces one — and it
    /// would read on the far side as a real previous value that differs from everything, i.e. a
    /// spurious "definition changed" that destroys and re-applies an opted-in buff. It is
    /// skipped and counted; every other row still crosses.
    ///
    /// Fails on the pre-FZ2 code: <c>ExportFingerprints</c> had no <c>skipped</c> overload and
    /// copied the empty row straight into the array, so both the length and the
    /// <c>LastFingerprint</c> assertion after the round-trip break.
    /// </summary>
    [Fact]
    public void Export_skips_an_unusable_row_and_keeps_the_rest()
    {
        PrefabLiveRefresh.Reset();
        PrefabLiveRefresh.RememberFingerprint(RefreshGuid, "good");
        PrefabLiveRefresh.RememberFingerprint(7, "");            // never a settled fingerprint
        PrefabLiveRefresh.RememberFingerprint(8, null!);         // coerced to "" by Remember

        PrefabLiveRefresh.ExportFingerprints(out var guids, out var prints, out int skipped);
        Assert.Equal(2, skipped);
        Assert.Equal(new[] { RefreshGuid }, guids);
        Assert.Equal(new[] { "good" }, prints);

        PrefabLiveRefresh.Reset();
        PrefabLiveRefresh.ImportFingerprints(guids, prints);
        Assert.Equal("good", PrefabLiveRefresh.LastFingerprint(RefreshGuid));
        Assert.Null(PrefabLiveRefresh.LastFingerprint(7));

        // Nothing settled at all is an empty export, not a throw.
        PrefabLiveRefresh.Reset();
        PrefabLiveRefresh.ExportFingerprints(out guids, out prints, out skipped);
        Assert.Empty(guids);
        Assert.Empty(prints);
        Assert.Equal(0, skipped);
    }

    // ------------------------------------------------------------------
    // R29 finding 11 — an empty export must be able to clear the store,
    // and a stale fingerprint must not survive to mask a real change
    // ------------------------------------------------------------------

    /// <summary>
    /// R28 finding 16 made an empty export mean nothing, which was right for the failure it
    /// named and left no way to say "there genuinely is nothing left". The exporter now says
    /// which kind of empty this is, and only the authoritative kind reaches the store's clear.
    ///
    /// Fails on the pre-FZ3 code: <c>FingerprintExport</c>, the four-out
    /// <c>ExportFingerprints</c> overload and <c>FormatFingerprintExport</c> did not exist, so
    /// the two empties were the same value and nothing could tell them apart.
    /// </summary>
    [Fact]
    public void An_empty_export_says_whether_the_emptiness_is_real()
    {
        PrefabLiveRefresh.Reset();

        // Generation swapped out before its first apply pass settled: emptiness proves nothing.
        PrefabLiveRefresh.ExportFingerprints(out var guids, out _, out _, out var status);
        Assert.Empty(guids);
        Assert.Equal(PrefabLiveRefresh.FingerprintExport.Unsettled, status);
        Assert.False(PrefabLiveRefresh.ApplyPassSettled);

        // The generation completed an apply pass and settled nothing: the emptiness is real.
        Assert.True(PrefabLiveRefresh.SettleApplyPass(out int dropped));
        Assert.Equal(0, dropped);
        Assert.True(PrefabLiveRefresh.ApplyPassSettled);
        PrefabLiveRefresh.ExportFingerprints(out guids, out _, out _, out status);
        Assert.Empty(guids);
        Assert.Equal(PrefabLiveRefresh.FingerprintExport.AuthoritativelyEmpty, status);

        // A real row makes it an ordinary export again.
        PrefabLiveRefresh.RememberFingerprint(RefreshGuid, "settled");
        PrefabLiveRefresh.ExportFingerprints(out guids, out _, out _, out status);
        Assert.Equal(new[] { RefreshGuid }, guids);
        Assert.Equal(PrefabLiveRefresh.FingerprintExport.Rows, status);

        // A map of nothing but unusable rows is empty again and cannot pass itself off as an
        // export: the empty-kind is whatever the pass state says, never Rows.
        PrefabLiveRefresh.Reset();
        PrefabLiveRefresh.RememberFingerprint(RefreshGuid, "");
        PrefabLiveRefresh.ExportFingerprints(out guids, out _, out int skipped, out status);
        Assert.Empty(guids);
        Assert.Equal(1, skipped);
        Assert.Equal(PrefabLiveRefresh.FingerprintExport.Unsettled, status);

        Assert.Contains(
            "the emptiness is real; clearing Bootstrap ContentMemory",
            PrefabLiveRefresh.FormatFingerprintExport(PrefabLiveRefresh.FingerprintExport.AuthoritativelyEmpty, 0),
            StringComparison.Ordinal);
        Assert.Contains(
            "never settled, so the emptiness proves nothing",
            PrefabLiveRefresh.FormatFingerprintExport(PrefabLiveRefresh.FingerprintExport.Unsettled, 0),
            StringComparison.Ordinal);

        PrefabLiveRefresh.Reset();
    }

    /// <summary>
    /// R29 finding 11, import half. A rehydrated fingerprint is provisional until this
    /// generation registers the definition it belongs to. If the first apply pass finishes
    /// without it — the developer removed the definition, the plugin failed to load, the content
    /// module is off — the row is a fossil from a build nobody is running, and leaving it in the
    /// map hands it on to every later generation to be compared against. It is dropped, and the
    /// count is logged so a definition that quietly stopped registering is visible.
    ///
    /// Fails on the pre-FZ3 code: <c>NoteRegistered</c> / the unclaimed sweep /
    /// <c>ProvisionalImportCount</c> did not exist, and every imported row survived to the
    /// export unconditionally — <c>LastFingerprint(GoneGuid)</c> here still read "gone-v1".
    /// </summary>
    [Fact]
    public void A_rehydrated_fingerprint_nobody_re_registers_is_dropped_at_the_first_apply_pass()
    {
        const int goneGuid = 424242;
        PrefabLiveRefresh.Reset();

        // The previous generation handed over two rows.
        Assert.Equal(2, PrefabLiveRefresh.ImportFingerprints(
            new[] { RefreshGuid, goneGuid }, new[] { "kept-v1", "gone-v1" }));
        Assert.Equal(2, PrefabLiveRefresh.ProvisionalImportCount);

        // This generation registers one of the two definitions.
        PrefabLiveRefresh.NoteRegistered(RefreshGuid);
        Assert.Equal(1, PrefabLiveRefresh.ProvisionalImportCount);

        // End of the first apply pass: the unclaimed row goes and is reported.
        Assert.True(PrefabLiveRefresh.SettleApplyPass(out int dropped));
        Assert.Equal(1, dropped);
        Assert.Equal("kept-v1", PrefabLiveRefresh.LastFingerprint(RefreshGuid));
        Assert.Null(PrefabLiveRefresh.LastFingerprint(goneGuid));
        Assert.Equal(0, PrefabLiveRefresh.ProvisionalImportCount);
        Assert.Contains(
            "dropped 1 rehydrated live-refresh fingerprint(s) whose definition was not"
            + " re-registered",
            PrefabLiveRefresh.FormatFingerprintsDropped(dropped),
            StringComparison.Ordinal);

        // Idempotent: only the first call is the end of the first apply pass. A row this
        // generation settled itself is never provisional and is never swept.
        Assert.False(PrefabLiveRefresh.SettleApplyPass(out int second));
        Assert.Equal(0, second);
        PrefabLiveRefresh.RememberFingerprint(7, "settled-here");
        Assert.Equal(0, PrefabLiveRefresh.ProvisionalImportCount);
        Assert.Equal("settled-here", PrefabLiveRefresh.LastFingerprint(7));

        // And the fossil cannot come back through the export.
        PrefabLiveRefresh.ExportFingerprints(out var guids, out _, out _, out var status);
        Assert.Equal(PrefabLiveRefresh.FingerprintExport.Rows, status);
        Assert.DoesNotContain(goneGuid, guids);

        PrefabLiveRefresh.Reset();
        Assert.False(PrefabLiveRefresh.ApplyPassSettled);
    }

    /// <summary>
    /// R29 finding 11, store half. <c>ClearFingerprints</c> is the only way to empty the store,
    /// and it is a separate method rather than an overload of <c>ReplaceFingerprints</c> — an
    /// empty array must stay meaningless there (R28 finding 16), and Core resolves these by
    /// simple name, so an overload would be an <c>AmbiguousMatchException</c> at the reflection
    /// call rather than a compile error.
    ///
    /// Fails on the pre-FZ3 code: <c>ClearFingerprints</c> and
    /// <c>ContentMemoryStore.Cleared</c> did not exist, so a store that had rows could never be
    /// emptied by any code path at all.
    /// </summary>
    [Fact]
    public void Only_an_authoritatively_empty_export_clears_the_store()
    {
        var mem = new Progenitor.Bootstrap.ContentMemory();
        mem.ReplaceFingerprints(new[] { RefreshGuid, 42 }, new[] { "a", "b" });
        Assert.Equal(2, mem.FingerprintCount);

        // The R28 rule is untouched: an empty write still cannot wipe anything.
        Assert.Equal(
            Progenitor.Bootstrap.ContentMemoryStore.KeptNothingOffered,
            mem.ReplaceFingerprints(Array.Empty<int>(), Array.Empty<string>()));
        Assert.Equal(2, mem.FingerprintCount);

        // The explicit clear does, and says so.
        Assert.Equal(Progenitor.Bootstrap.ContentMemoryStore.Cleared, mem.ClearFingerprints());
        Assert.Equal(0, mem.FingerprintCount);
        mem.TakeFingerprints(out var guids, out var prints);
        Assert.Empty(guids);
        Assert.Empty(prints);

        // Clearing an empty store is not an error, and the CloneOf half is untouched by it.
        mem.ReplaceCloneSources(new[] { RefreshGuid }, new[] { 900030866 });
        Assert.Equal(Progenitor.Bootstrap.ContentMemoryStore.Cleared, mem.ClearFingerprints());
        Assert.Equal(1, mem.CloneSourceCount);

        Assert.Equal(
            "ContentMemory: cleared 2 stored live-refresh fingerprint row(s); the generation that"
            + " wrote them completed an apply pass and settled none (an authoritatively empty"
            + " export, not a failed one).",
            Progenitor.Bootstrap.ContentMemoryLog.FormatStore(
                Progenitor.Bootstrap.ContentMemoryStore.Cleared, "live-refresh fingerprint", 0, 0, 2));

        // One method name, so Core's GetMethod(name) cannot become ambiguous.
        var overloads = typeof(Progenitor.Bootstrap.ContentMemory)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        int replaceFingerprints = 0;
        int clearFingerprints = 0;
        foreach (var m in overloads)
        {
            if (m.Name == "ReplaceFingerprints") replaceFingerprints++;
            if (m.Name == "ClearFingerprints") clearFingerprints++;
        }
        Assert.Equal(1, replaceFingerprints);
        Assert.Equal(1, clearFingerprints);
    }

    /// <summary>
    /// The Core side is wired to all three: the registry sweeps unclaimed imports at the settle
    /// and again before the export, claims a GUID when it registers it, and routes an empty
    /// export through <c>ClearFingerprints</c> only when the exporter called it authoritative.
    /// <c>PrefabRegistry.cs</c> is game-typed and not compiled here, so the wiring itself can
    /// only be asserted against the source; the rules it calls are tested above.
    /// </summary>
    [Fact]
    public void PrefabRegistry_wires_the_fingerprint_sweep_and_clear()
    {
        var src = ReadContentFile("PrefabRegistry.cs");
        SourcePin.AssertStatement(src, "PrefabLiveRefresh.NoteRegistered(def.Guid.GuidHash);");
        SourcePin.AssertUncommented(src, "PrefabLiveRefresh.SettleApplyPass(out int dropped)");
        SourcePin.AssertUncommented(src, "PrefabLiveRefresh.FormatFingerprintsDropped(dropped)");
        SourcePin.AssertStatement(src, "out var fpGuids, out var fingerprints, out var fpSkipped, out var fpStatus");
        SourcePin.AssertStatement(src, "if (status != PrefabLiveRefresh.FingerprintExport.AuthoritativelyEmpty)");
        SourcePin.AssertUncommented(src, "GetMethod(\"ClearFingerprints\"");
        // The sweep runs at both settle exits (nothing pending, and after the decision loop) and
        // again on the way out through the export, so no path can leave a fossil row behind.
        Assert.Equal(2, CountOccurrences(src, "SettleApplyPassAndDropUnclaimed();"));
        Assert.Equal(1, CountOccurrences(src, "SweepUnclaimedFingerprints();"));
        // Cleared is a success, not a refusal, or every clear would log as an error.
        SourcePin.AssertUncommented(src, "string.Equals(name, \"Cleared\", StringComparison.Ordinal)");
    }

    // ------------------------------------------------------------------
    // R43 job FPRF1 — the teardown export must not be able to authorize
    // its own emptiness
    // ------------------------------------------------------------------

    /// <summary>
    /// The bug: <c>ExportProcessMemory</c> ran the unclaimed sweep through the same method the
    /// settle uses, that method set <c>_applyPassSettled</c> itself, and the very next line read
    /// that flag to choose between <c>AuthoritativelyEmpty</c> and <c>Unsettled</c>. So
    /// <c>Unsettled</c> could not be produced on the export path at all, and a generation that
    /// never applied anything — a Core swap that landed before the world existed, a content plugin
    /// caught mid-write, the Content module skipped for one generation — declared its own empty map
    /// authoritative and cleared <c>ContentMemory</c>, the only store that crosses a Core swap.
    /// Two swaps later the developer edits a buff and gets <c>refresh skipped: definition
    /// unchanged</c>, i.e. R27 finding 1 back in silence.
    ///
    /// This is the teardown sequence verbatim: sweep, then export. It fails on the pre-FPRF1 code,
    /// where the sweep left <c>ApplyPassSettled</c> true and the status read
    /// <c>AuthoritativelyEmpty</c>.
    /// </summary>
    [Fact]
    public void The_teardown_sweep_cannot_make_an_empty_export_authoritative()
    {
        const int goneGuid = 424243;
        PrefabLiveRefresh.Reset();

        // Generation N+1 rehydrates the previous generation's rows and then never applies
        // anything: no Register, no settle, straight to the swap.
        Assert.Equal(2, PrefabLiveRefresh.ImportFingerprints(
            new[] { RefreshGuid, goneGuid }, new[] { "carried-v1", "carried-v2" }));
        Assert.Equal(2, PrefabLiveRefresh.ProvisionalImportCount);
        Assert.False(PrefabLiveRefresh.ApplyPassSettled);

        // PrefabRegistry.ExportProcessMemory: sweep the unclaimed rows, then export.
        Assert.True(PrefabLiveRefresh.SweepUnclaimedImports(out int dropped));
        Assert.Equal(2, dropped);

        // The sweep emptied the map but must not have claimed any authority for doing so.
        Assert.False(PrefabLiveRefresh.ApplyPassSettled);

        PrefabLiveRefresh.ExportFingerprints(out var guids, out _, out _, out var status);
        Assert.Empty(guids);
        Assert.Equal(PrefabLiveRefresh.FingerprintExport.Unsettled, status);

        // And that verdict is the one ContentMemory refuses to clear on.
        Assert.NotEqual(PrefabLiveRefresh.FingerprintExport.AuthoritativelyEmpty, status);
        Assert.Contains(
            "never settled, so the emptiness proves nothing",
            PrefabLiveRefresh.FormatFingerprintExport(status, 0),
            StringComparison.Ordinal);

        // A later real settle still works, and only then is the emptiness authoritative.
        Assert.True(PrefabLiveRefresh.SettleApplyPass(out int settleDropped));
        Assert.Equal(0, settleDropped);
        Assert.True(PrefabLiveRefresh.ApplyPassSettled);
        PrefabLiveRefresh.ExportFingerprints(out guids, out _, out _, out status);
        Assert.Empty(guids);
        Assert.Equal(PrefabLiveRefresh.FingerprintExport.AuthoritativelyEmpty, status);

        PrefabLiveRefresh.Reset();
    }

    /// <summary>
    /// All three export verdicts have to be reachable through the real sequences, or the store
    /// side is deciding on a value that only one branch can ever produce. Pins one sequence per
    /// verdict and, for each, whether it may reach <c>ContentMemory.ClearFingerprints</c>.
    /// </summary>
    [Fact]
    public void Every_export_verdict_is_reachable_and_only_one_of_them_clears_the_store()
    {
        // Rows — this generation settled a fingerprint. The store is replaced, never cleared.
        PrefabLiveRefresh.Reset();
        PrefabLiveRefresh.RememberFingerprint(RefreshGuid, "v1");
        PrefabLiveRefresh.SettleApplyPass(out _);
        PrefabLiveRefresh.ExportFingerprints(out var guids, out _, out _, out var rows);
        Assert.Equal(new[] { RefreshGuid }, guids);
        Assert.Equal(PrefabLiveRefresh.FingerprintExport.Rows, rows);

        // AuthoritativelyEmpty — the apply pass ran and settled nothing. Only this may clear.
        PrefabLiveRefresh.Reset();
        Assert.Equal(1, PrefabLiveRefresh.ImportFingerprints(new[] { RefreshGuid }, new[] { "gone" }));
        PrefabLiveRefresh.SettleApplyPass(out int settled);
        Assert.Equal(1, settled);
        PrefabLiveRefresh.ExportFingerprints(out guids, out _, out _, out var authoritative);
        Assert.Empty(guids);
        Assert.Equal(PrefabLiveRefresh.FingerprintExport.AuthoritativelyEmpty, authoritative);

        // Unsettled — the generation reached teardown without an apply pass. Keep and log.
        PrefabLiveRefresh.Reset();
        Assert.Equal(1, PrefabLiveRefresh.ImportFingerprints(new[] { RefreshGuid }, new[] { "carried" }));
        PrefabLiveRefresh.SweepUnclaimedImports(out _);
        PrefabLiveRefresh.ExportFingerprints(out guids, out _, out _, out var unsettled);
        Assert.Empty(guids);
        Assert.Equal(PrefabLiveRefresh.FingerprintExport.Unsettled, unsettled);

        // Three sequences, three distinct verdicts: none of them is unreachable.
        Assert.NotEqual(rows, authoritative);
        Assert.NotEqual(rows, unsettled);
        Assert.NotEqual(authoritative, unsettled);

        PrefabLiveRefresh.Reset();
    }

    /// <summary>
    /// The structural half of R43 job FPRF1, since <c>PrefabRegistry.cs</c> is game-typed and not
    /// compiled here. <c>_applyPassSettled</c> has exactly one assignment, it is reachable only
    /// with <c>markSettled: true</c>, exactly one entry point passes that, and the teardown export
    /// does not call it. "Less likely" would be a call-ordering rule; this is a single writer with
    /// a single caller.
    /// </summary>
    [Fact]
    public void Only_the_settle_may_claim_the_apply_pass_settled()
    {
        var refresh = ReadContentFile("PrefabLiveRefresh.cs");
        Assert.Equal(1, CountOccurrences(refresh, "_applyPassSettled = true;"));
        Assert.Equal(1, CountOccurrences(refresh, "if (markSettled) _applyPassSettled = true;"));
        Assert.Equal(1, CountOccurrences(refresh, "DropUnclaimed(markSettled: true, out dropped)"));
        Assert.Equal(1, CountOccurrences(refresh, "DropUnclaimed(markSettled: false, out dropped)"));

        var src = ReadContentFile("PrefabRegistry.cs");
        Assert.Equal(1, CountOccurrences(src, "PrefabLiveRefresh.SettleApplyPass("));
        Assert.Equal(1, CountOccurrences(src, "PrefabLiveRefresh.SweepUnclaimedImports("));

        // The teardown export sweeps, and between the sweep and the status read there is nothing
        // that could settle the pass.
        int export = src.IndexOf("internal static void ExportProcessMemory(", StringComparison.Ordinal);
        Assert.True(export >= 0);
        int read = src.IndexOf(
            "out var fpGuids, out var fingerprints, out var fpSkipped, out var fpStatus",
            export, StringComparison.Ordinal);
        Assert.True(read > export);
        string teardown = src.Substring(export, read - export);
        SourcePin.AssertStatement(teardown, "SweepUnclaimedFingerprints();");
        Assert.DoesNotContain("SettleApplyPass", teardown, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------
    // R27 finding 12 — the duration restore has to wait for the instance
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>DebugEventsSystem.ApplyBuff</c> only queues an <c>ApplyBuffDebugEvent</c>; the game
    /// builds the buff entity on a later update. The restore therefore has to come back over
    /// several frames and write the duration when the instance appears.
    ///
    /// Fails on the pre-FZ code: there was no state machine at all — the restore ran inside the
    /// same call as <c>ApplyBuff</c>, found nothing, and dropped the remaining duration with no
    /// log. <c>AdvanceRestore</c> / <c>RestoreState</c> / <c>FormatDurationNotRestored</c> did
    /// not exist.
    /// </summary>
    [Fact]
    public void Duration_restore_waits_for_the_instance_ApplyBuff_creates()
    {
        var state = new PrefabLiveRefresh.RestoreState();

        // Frames 1 and 2: the game has not built the instance yet.
        Assert.Equal(PrefabLiveRefresh.RestoreStep.Wait, PrefabLiveRefresh.AdvanceRestore(ref state, instanceFound: false));
        Assert.Equal(1, state.Frames);
        Assert.Equal(PrefabLiveRefresh.RestoreStep.Wait, PrefabLiveRefresh.AdvanceRestore(ref state, instanceFound: false));
        Assert.Equal(2, state.Frames);

        // Frame 3: it exists. Write the duration and finish.
        Assert.Equal(PrefabLiveRefresh.RestoreStep.Restore, PrefabLiveRefresh.AdvanceRestore(ref state, instanceFound: true));
        Assert.Equal(2, state.Frames);
    }

    /// <summary>The wait is bounded, and giving up is logged rather than silent.</summary>
    [Fact]
    public void Duration_restore_gives_up_loudly_after_its_budget()
    {
        // R29 finding 22.2: the budget is pinned by walking it, not by asserting the constant
        // against its own literal. The machine must Wait RestoreDurationFrames times and then
        // GiveUp — that fails if the loop and the constant ever stop agreeing, and it fails if
        // the wait becomes unbounded.
        Assert.True(PrefabLiveRefresh.RestoreDurationFrames > 0);
        var state = new PrefabLiveRefresh.RestoreState();
        int waited = 0;
        while (PrefabLiveRefresh.AdvanceRestore(ref state, instanceFound: false)
               == PrefabLiveRefresh.RestoreStep.Wait)
        {
            waited++;
            Assert.True(waited <= PrefabLiveRefresh.RestoreDurationFrames, "the restore wait is unbounded");
        }
        Assert.Equal(PrefabLiveRefresh.RestoreDurationFrames, waited);
        Assert.Equal(PrefabLiveRefresh.RestoreDurationFrames, state.Frames);
        Assert.Equal(PrefabLiveRefresh.RestoreStep.GiveUp, PrefabLiveRefresh.AdvanceRestore(ref state, instanceFound: false));
        Assert.Equal(PrefabLiveRefresh.RestoreStep.GiveUp, PrefabLiveRefresh.AdvanceRestore(ref state, instanceFound: false));

        string line = PrefabLiveRefresh.FormatDurationNotRestored("sample.nodrain", 12.5f);
        Assert.Contains("duration not restored", line, StringComparison.Ordinal);
        Assert.Contains("sample.nodrain", line, StringComparison.Ordinal);
        Assert.Contains("12.5", line, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // R27 finding 13 — an item that destroyed its instance is never dropped
    // ------------------------------------------------------------------

    /// <summary>
    /// Between the destroy frame and the re-apply frame the item owes the holder an
    /// <c>ApplyBuff</c>. A dead world, a throw, or an Uninstall landing in that window must
    /// re-queue it, not drop it.
    ///
    /// Fails on the pre-FZ code: <c>OwesApply</c> / <c>ShouldRetryOwedApply</c> and the two log
    /// formats did not exist, and all three call sites simply returned, leaving the holder
    /// without the buff and nothing in the log saying so.
    /// </summary>
    [Fact]
    public void An_item_that_destroyed_its_instance_owes_an_apply()
    {
        // Before the destroy there is nothing owed: dropping such an item is harmless.
        Assert.False(PrefabLiveRefresh.OwesApply(destroyedByUs: false, applied: false));
        // Destroyed and not yet re-applied: this is the state that must never be dropped.
        Assert.True(PrefabLiveRefresh.OwesApply(destroyedByUs: true, applied: false));
        // ApplyBuff has been queued: the debt is settled even though the restore may continue.
        Assert.False(PrefabLiveRefresh.OwesApply(destroyedByUs: true, applied: true));
    }

    /// <summary>The retry is budgeted, and both outcomes are loud.</summary>
    [Fact]
    public void An_owed_apply_retries_to_a_budget_and_then_says_the_buff_was_lost()
    {
        // R28 finding 29.6: these used to be written as MaxOwedApplyRetries ± 1, which asserts
        // X-1 < X — true for any budget, including 0 (never retry) and int.MaxValue (retry for
        // ever). The boundary is spelled out in literals, and the budget is pinned to the value
        // the docs and the log line promise.
        Assert.Equal(30, PrefabLiveRefresh.MaxOwedApplyRetries);
        Assert.True(PrefabLiveRefresh.ShouldRetryOwedApply(0));
        Assert.True(PrefabLiveRefresh.ShouldRetryOwedApply(1));
        Assert.True(PrefabLiveRefresh.ShouldRetryOwedApply(29));
        Assert.False(PrefabLiveRefresh.ShouldRetryOwedApply(30));
        Assert.False(PrefabLiveRefresh.ShouldRetryOwedApply(31));

        string retry = PrefabLiveRefresh.FormatOwedApplyRetry("sample.nodrain", "server world is gone", 3);
        Assert.Contains("re-apply owed", retry, StringComparison.Ordinal);
        Assert.Contains("server world is gone", retry, StringComparison.Ordinal);
        Assert.Contains("3/" + PrefabLiveRefresh.MaxOwedApplyRetries, retry, StringComparison.Ordinal);

        string lost = PrefabLiveRefresh.FormatOwedApplyLost("sample.nodrain", "apply threw: boom");
        Assert.Contains("destroyed but never re-applied", lost, StringComparison.Ordinal);
        Assert.Contains("the holder lost this buff", lost, StringComparison.Ordinal);

        Assert.Contains("drained 2 owed re-apply(s)", PrefabLiveRefresh.FormatOwedApplyDrained(2), StringComparison.Ordinal);
    }

    /// <summary>
    /// The three drop paths and the Uninstall drain are wired to the policy above. Source
    /// assertions because the surrounding code is behind <c>!PROGENITOR_TESTS</c>; the
    /// behavior they gate is covered by the two tests above.
    /// </summary>
    [Fact]
    public void The_three_mid_flight_drop_paths_requeue_instead()
    {
        var apply = ReadContentFile("PrefabLiveRefreshApply.cs");
        // Dead world between the destroy and the re-apply.
        SourcePin.AssertStatement(apply, "RequeueOwedNow(ref item, \"server world is gone\")");
        // Any throw inside RefreshOne.
        SourcePin.AssertUncommented(apply, "RequeueOwed(ref item, \"apply threw: \" + e.Message)");
        // Uninstall (a second Core swap inside the one-frame window).
        SourcePin.AssertUncommented(apply, "TakeOwedNoLock()");
        SourcePin.AssertUncommented(apply, "TryApplyOwedNow");
        SourcePin.AssertUncommented(apply, "FormatOwedApplyDrained");
        // The restore is a later frame, not the apply frame.
        SourcePin.AssertStatement(apply, "if (item.Applied)");
        SourcePin.AssertUncommented(apply, "PrefabLiveRefresh.AdvanceRestore(ref state, found)");
        // R27 finding 12's `return` that aborted the whole restore on the first candidate
        // without LifeTime is a `continue` now.
        SourcePin.AssertStatement(apply, "if (!em.HasComponent(e, lifeType)) continue;");
        Assert.DoesNotContain("if (!em.HasComponent(e, lifeType)) return;", apply, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // R27 finding 21 — the settle countdown
    // ------------------------------------------------------------------

    /// <summary>
    /// Every note re-arms the full window, so a plugin that registers its <c>Content.Edit</c>
    /// passes late still lands inside it and the settle records a fingerprint that already has
    /// the editors in it. Arming only when the countdown had run out left a late registration
    /// outside the window: the settle recorded an editor-less fingerprint and the late edit pass
    /// then read as "definition changed" and destroyed the opted-in buff for nothing.
    ///
    /// Fails on the pre-FZ code: <c>ScheduleRefreshSettle</c> did
    /// <c>if (_refreshSettleCountdown &lt;= 0) _refreshSettleCountdown = 2;</c>, so the second
    /// note below would have left the countdown at 1 and the settle would have fired on the
    /// very next frame — the assertion on <c>Remaining</c> after the re-arm is the one that
    /// breaks. <c>RefreshSettleCountdown</c> did not exist either.
    /// </summary>
    [Fact]
    public void Settle_window_is_extended_by_every_note_so_a_late_editor_is_inside_it()
    {
        var countdown = new RefreshSettleCountdown(2);
        Assert.True(countdown.Arm());          // first note subscribes
        Assert.True(countdown.Hooked);
        Assert.Equal(2, countdown.Remaining);

        Assert.False(countdown.Tick());        // frame 1: 2 -> 1, not yet
        Assert.Equal(1, countdown.Remaining);

        // A late Content.Edit registration notes on the frame where the old code would have
        // refused to re-arm, giving that prefab a zero-frame window.
        Assert.False(countdown.Arm());         // already hooked: must not subscribe twice
        Assert.Equal(2, countdown.Remaining);

        Assert.False(countdown.Tick());
        Assert.True(countdown.Tick());         // fires two frames after the LAST note
        Assert.True(countdown.Stop());
        Assert.False(countdown.Hooked);
    }

    /// <summary>
    /// The hook flag is only ever flipped by the caller that owns the transition, so
    /// <c>Plugin.Frame</c> can neither be double-subscribed nor lose its subscription and leave
    /// <c>refreshSettle=N</c> stuck in <c>DescribePins</c>.
    /// </summary>
    [Fact]
    public void Settle_countdown_subscribes_and_unsubscribes_exactly_once()
    {
        var countdown = new RefreshSettleCountdown(2);
        Assert.True(countdown.Arm());
        Assert.False(countdown.Arm());
        Assert.False(countdown.Arm());
        Assert.True(countdown.Stop());
        Assert.False(countdown.Stop());
        Assert.Equal(0, countdown.Remaining);
        Assert.False(countdown.Hooked);

        // A re-arm after a stop subscribes again; the earlier stop cannot cancel it.
        Assert.True(countdown.Arm());
        Assert.True(countdown.Hooked);
        Assert.Equal(2, countdown.Remaining);

        // A window of one frame still fires on that frame rather than never.
        var single = new RefreshSettleCountdown(1);
        single.Arm();
        Assert.True(single.Tick());
    }

    /// <summary>
    /// R28 finding 28.1: <c>Tick</c> was <c>if (_remaining &gt; 0 &amp;&amp; --_remaining &gt; 0)
    /// return false; return true;</c>, which short-circuits on a disarmed countdown without
    /// decrementing and then returns <b>true</b>. That matters because <c>Plugin.Frame</c>
    /// snapshots its invocation list per frame: a handler <c>Stop()</c> removed earlier in the
    /// same frame still runs that frame, and it ran with <c>_remaining == 0</c> — so it settled
    /// immediately, taking a refresh decision on a window nobody had armed.
    ///
    /// Fails on the pre-FZ2 code: every <c>Assert.False(countdown.Tick())</c> below returns true.
    /// </summary>
    [Fact]
    public void A_disarmed_countdown_never_settles()
    {
        var countdown = new RefreshSettleCountdown(2);

        // Never armed at all.
        Assert.False(countdown.Tick());
        Assert.Equal(0, countdown.Remaining);

        // Armed, then stopped: the handler that is still in this frame's snapshot must no-op.
        Assert.True(countdown.Arm());
        Assert.True(countdown.Stop());
        Assert.False(countdown.Tick());
        Assert.False(countdown.Tick());

        // Fired normally, then ticked again before the unsubscribe lands.
        Assert.True(countdown.Arm());
        Assert.False(countdown.Tick());
        Assert.True(countdown.Tick());          // the real settle
        Assert.False(countdown.Tick());         // the same frame's leftover tick settles nothing
        Assert.Equal(0, countdown.Remaining);
    }

    /// <summary>The registry uses the counter and no longer keeps the two fields unlocked.</summary>
    [Fact]
    public void Registry_settle_state_lives_behind_the_counter()
    {
        var src = ReadContentFile("PrefabRegistry.cs");
        SourcePin.AssertUncommented(src, "RefreshSettleCountdown RefreshSettle = new(RefreshSettleFrames)");
        SourcePin.AssertStatement(src, "if (RefreshSettle.Arm())");
        SourcePin.AssertStatement(src, "if (RefreshSettle.Stop())");
        SourcePin.AssertStatement(src, "if (!RefreshSettle.Tick()) return;");
        Assert.DoesNotContain("_refreshSettleCountdown", src, StringComparison.Ordinal);
        Assert.DoesNotContain("_refreshSettleHooked", src, StringComparison.Ordinal);
        // The swap handoff is wired into the snapshot / rehydrate pair FA already uses.
        SourcePin.AssertStatement(src, "out var fpGuids, out var fingerprints, out var fpSkipped, out var fpStatus");
        SourcePin.AssertUncommented(src, "PrefabLiveRefresh.ImportFingerprints(");
        SourcePin.AssertUncommented(src, "\"ReplaceFingerprints\"");
        SourcePin.AssertUncommented(src, "\"TakeFingerprints\"");

        // R28 finding 16: an empty export is never written as an empty store (the write used to
        // wipe it), and the rehydrate reports itself instead of returning false in silence.
        // R29 finding 11: the empty branch now asks the exporter which kind of empty it is, and
        // only the authoritative kind clears. PrefabRegistry.cs is game-typed and cannot compile
        // into this project; the rules the call sites use are tested directly above.
        SourcePin.AssertStatement(src, "if (guids.Length == 0)");
        SourcePin.AssertUncommented(src, "PrefabLiveRefresh.FormatFingerprintExport(status, 0)");
        SourcePin.AssertUncommented(src, "ContentMemory keeps what it holds.");
        SourcePin.AssertStatement(src, "LogContentMemoryStore(outcome, \"live-refresh fingerprint\"");
        SourcePin.AssertStatement(src, "LogContentMemoryStore(outcome, \"CloneOf hash\"");
        SourcePin.AssertUncommented(src, "PrefabLiveRefresh.FormatFingerprintImport(");
    }

    /// <summary>
    /// One Core swap, modeled end to end through the same two stores production uses:
    /// export -> ContentMemory -> Uninstall clears the Core static -> import. Returns the value
    /// the new generation's first apply will read.
    /// </summary>
    private static string SwapWithHandoff(string settledFingerprint)
    {
        PrefabLiveRefresh.Reset();
        PrefabLiveRefresh.RememberFingerprint(RefreshGuid, settledFingerprint);

        // Runtime.SnapshotContent -> PrefabRegistry.ExportProcessMemory, before Plugin.Uninstall.
        var memory = new Progenitor.Bootstrap.ContentMemory();
        PrefabLiveRefresh.ExportFingerprints(out var guids, out var prints);
        memory.ReplaceFingerprints(guids, prints);

        // PrefabRegistry.Uninstall -> PrefabLiveRefresh.Reset: the Core static dies with the ALC.
        PrefabLiveRefresh.Reset();
        Assert.Null(PrefabLiveRefresh.LastFingerprint(RefreshGuid));

        // Runtime.Install -> TryRehydrate -> PrefabRegistry.ImportProcessMemory.
        memory.TakeFingerprints(out var carriedGuids, out var carriedPrints);
        PrefabLiveRefresh.ImportFingerprints(carriedGuids, carriedPrints);
        return PrefabLiveRefresh.LastFingerprint(RefreshGuid) ?? "";
    }

    private const int RefreshGuid = 650563057;

    private static string Fingerprint(
        string editStamp,
        int sourceHash = 900030866,
        string? localizedName = "No Drain",
        string? iconPath = "icons/nodrain.png") =>
        PrefabLiveRefresh.Fingerprint(
            PrefabKind.Buff, sourceHash, "Buff_General_Haste", localizedName, "desc", iconPath, editStamp);

    private static void AddEditor(
        PrefabEditTable table,
        string pluginId,
        PrefabEditPriority priority,
        out IDisposable handle,
        string? version = null)
    {
        Assert.True(table.TryAdd(
            RefreshGuid, pluginId, pluginId, (int)priority,
            before: null, after: null, owner: new object(),
            editor: new Action<object>(_ => { }),
            loadOrder: -1, out handle, out var refuse, version: version));
        Assert.Null(refuse);
    }

    private static string ReadContentFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Progenitor.Core", "Content", fileName);
            if (File.Exists(candidate))
                return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("src/Progenitor.Core/Content/" + fileName);
    }
}
