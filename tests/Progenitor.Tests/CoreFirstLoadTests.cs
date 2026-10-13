using System;
using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FH: first-load promote, quarantine-by-loaded-image, Core-less Install,
/// and failed-Install slot names (R24 finding 6). Game-free; live CoreLoader
/// disk moves are not run here.
/// </summary>
public class CoreFirstLoadTests
{
    [Fact]
    public void Promote_before_first_read_is_unconditional()
    {
        Assert.True(CoreReloadPolicy.PromoteBeforeFirstRead);
        Assert.True(CoreReloadPolicy.ShouldPromoteOnFirstLoad(watchEnabled: false));
        Assert.True(CoreReloadPolicy.ShouldPromoteOnFirstLoad(watchEnabled: true));
        Assert.Equal("promoted Progenitor.dll.next", CoreReloadPolicy.FormatPromotedNext());
    }

    [Fact]
    public void Quarantine_only_the_loaded_image()
    {
        var loaded = new byte[] { 1, 2, 3 };
        var same = new byte[] { 1, 2, 3 };
        var other = new byte[] { 9, 9, 9 };
        Assert.True(CoreReloadPolicy.MayQuarantineOnDisk(loaded, same));
        Assert.False(CoreReloadPolicy.MayQuarantineOnDisk(loaded, other));
        Assert.False(CoreReloadPolicy.MayQuarantineOnDisk(loaded, null));
        Assert.False(CoreReloadPolicy.MayQuarantineOnDisk(null, same));
        Assert.Equal(
            "Core quarantine skipped: on-disk file is not the loaded image",
            CoreReloadPolicy.FormatQuarantineSkipped());
        Assert.Equal(CoreReloadPolicy.ImageHash(loaded), CoreReloadPolicy.ImageHash(same));
        Assert.NotEqual(CoreReloadPolicy.ImageHash(loaded), CoreReloadPolicy.ImageHash(other));
    }

    [Fact]
    public void Restore_last_good_never_overwrites_a_newer_file_or_an_old_protocol()
    {
        var loaded = new byte[] { 1, 1, 1 };
        var lastGood = new byte[] { 2, 2, 2 };
        var newerOnDisk = new byte[] { 3, 3, 3 };
        Assert.False(CoreReloadPolicy.MayRestoreLastGood(lastGood, 2, 2, loaded, newerOnDisk));
        Assert.True(CoreReloadPolicy.MayRestoreLastGood(lastGood, 2, 2, loaded, loaded));
        Assert.False(CoreReloadPolicy.MayRestoreLastGood(lastGood, 1, 2, loaded, loaded));
        Assert.False(CoreReloadPolicy.MayRestoreLastGood(null, 2, 2, loaded, loaded));
        Assert.False(CoreReloadPolicy.ShouldRestoreLastGoodOnProtocolMismatch(1, 2));
        Assert.True(CoreReloadPolicy.ShouldRestoreLastGoodOnProtocolMismatch(2, 2));
        Assert.Equal(
            "Core not loaded: protocol mismatch; deploy a Core built for Bootstrap protocol 2",
            CoreReloadPolicy.FormatProtocolMismatchStayCoreLess(2));
    }

    [Fact]
    public void Core_less_watch_installs_instead_of_snapshotting()
    {
        Assert.True(CoreReloadPolicy.ShouldInstallDirect(0, hasPublishedAssembly: false));
        Assert.True(CoreReloadPolicy.ShouldInstallDirect(0, hasPublishedAssembly: true));
        Assert.False(CoreReloadPolicy.ShouldInstallDirect(1, hasPublishedAssembly: true));
        Assert.True(CoreReloadPolicy.ShouldInstallDirect(1, hasPublishedAssembly: false));
        Assert.Equal(
            "Core install gen=1 from watch (no previous generation)",
            CoreReloadPolicy.FormatCoreLessInstall(1, "watch"));
        Assert.Equal(
            "Core install gen=1 from bridge (no previous generation)",
            CoreReloadPolicy.FormatCoreLessInstall(1, "bridge"));
        Assert.Equal(1, CoreReloadPolicy.ScheduledGeneration(0));
        Assert.Equal(1, CoreReloadPolicy.ScheduledGeneration(1));
        Assert.False(CoreReloadPolicy.MayScanPluginsWithoutCore);

        var ops = new FakeReloadOps { CurrentGeneration = 0, HasPublishedAssembly = false };
        var result = CoreReloadOrchestrator.Run(ops, "watch");
        Assert.True(result.Ok);
        Assert.Equal(1, result.Generation);
        Assert.Equal(1, ops.LoadedGeneration);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepSnapshot, ops.Steps);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepFreeze, ops.Steps);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepUninstall, ops.Steps);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepUnloadAlc, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepRead, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepLoad, ops.Steps);
        Assert.Contains(ops.Logs, l => l.IndexOf("Core install gen=1 from watch (no previous generation)", StringComparison.Ordinal) >= 0);
        Assert.Equal(
            "ok gen=1 collected=True pluginsUnloaded=0 pluginsReloaded=1 reason=watch",
            result.Summary);
    }

    [Fact]
    public void Core_less_failed_install_does_not_snapshot()
    {
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 0,
            HasPublishedAssembly = false,
            LoadOk = false,
            LoadError = "Core is not loaded.",
        };
        var result = CoreReloadOrchestrator.Run(ops, "watch");
        Assert.False(result.Ok);
        Assert.Equal(1, result.Generation);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepSnapshot, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepLoad, ops.Steps);
        Assert.DoesNotContain("step=snapshot", result.Summary);
        Assert.Contains("step=load", result.Summary);
    }

    [Fact]
    public void Failed_install_slot_list_includes_Ui_and_LateFrame()
    {
        Assert.Contains("UiSlots", CoreReloadPolicy.FailedInstallSlots);
        Assert.Contains("lifetime.lateframe", CoreReloadPolicy.FailedInstallSlots);
        Assert.Contains("UiInputContext.keys", CoreReloadPolicy.FailedInstallSlots);
        Assert.Contains("UiInputContext.capture", CoreReloadPolicy.FailedInstallSlots);
        Assert.Contains("HarmonySlots", CoreReloadPolicy.FailedInstallSlots);
        Assert.Contains("CoreTick", CoreReloadPolicy.FailedInstallSlots);
        // Job FLATCH4 (R47 finding 3): the input mask's apply slot, which FLATCH3 added to
        // NullRuntime and MarkUninstalled and not to the shared pre-unload clear.
        Assert.Contains("UiMaskDetour", CoreReloadPolicy.FailedInstallSlots);
        // Job NATIVE1: the Action-field trampoline's Core dispatcher (UiActionSlot), cleared on
        // the same three paths as UiSlots (failed install, NullRuntime, MarkUninstalled).
        Assert.Contains("UiActionSlot", CoreReloadPolicy.FailedInstallSlots);
        // The length lock stays, and its limits are now written down where it is: it fails on
        // an addition nobody named and cannot fail on an omission. CoreUnloadSlotClearTests is
        // what covers the other direction.
        Assert.Equal(13, CoreReloadPolicy.FailedInstallSlots.Length);
    }

    [Fact]
    public void Core_less_live_change_requests_only_a_new_settled_stamp()
    {
        Assert.True(CoreReloadPolicy.ShouldRequestCoreLessLiveChange(
            coreLoaded: false, liveExists: true, liveSettled: true, sameStampAsLastTry: false));
        Assert.False(CoreReloadPolicy.ShouldRequestCoreLessLiveChange(
            coreLoaded: true, liveExists: true, liveSettled: true, sameStampAsLastTry: false));
        Assert.False(CoreReloadPolicy.ShouldRequestCoreLessLiveChange(
            coreLoaded: false, liveExists: true, liveSettled: true, sameStampAsLastTry: true));
        Assert.False(CoreReloadPolicy.ShouldRequestCoreLessLiveChange(
            coreLoaded: false, liveExists: false, liveSettled: true, sameStampAsLastTry: false));
    }

    [Fact]
    public void Core_less_does_not_cancel_a_scheduled_reload_when_promote_fails()
    {
        Assert.False(CoreWatchPolicy.CancelScheduledReload(
            hadStagedNext: true, promoteSucceeded: false, coreLoaded: false));
        Assert.True(CoreWatchPolicy.CancelScheduledReload(
            hadStagedNext: true, promoteSucceeded: false, coreLoaded: true));
    }

    [Fact]
    public void Frame_driver_schedules_generation_1_when_none_is_loaded()
    {
        var driver = new CoreReloadFrameDriver();
        Assert.Equal("scheduled gen=1", driver.Request(0, null, previousGenerationAlive: false, "watch"));
    }
}
