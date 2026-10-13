using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of Step D: generation naming, refuse-while-previous-ALC-alive,
/// bounded collect wait, Core-ALC Resolving (never a default-ALC <c>Progenitor</c>), and
/// the cascade leaving the old generation running when Uninstall fails.
/// </summary>
public class CoreReloadPolicyTests
{
    [Fact]
    public void AlcName_is_prefix_plus_1_based_generation()
    {
        Assert.Equal("progenitor-core", CoreReloadPolicy.AlcNamePrefix);
        Assert.Equal("progenitor-core:1", CoreReloadPolicy.AlcName(1));
        Assert.Equal("progenitor-core:2", CoreReloadPolicy.AlcName(2));
        Assert.True(CoreReloadPolicy.IsCoreAlcName("progenitor-core:1"));
        Assert.False(CoreReloadPolicy.IsCoreAlcName("progenitor-reload:sample:1"));
        Assert.False(CoreReloadPolicy.IsCoreAlcName("progenitor-core"));
        Assert.False(CoreReloadPolicy.IsCoreAlcName(""));
        Assert.False(CoreReloadPolicy.IsCoreAlcName(null));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoreReloadPolicy.AlcName(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CoreReloadPolicy.AlcName(-1));
    }

    [Fact]
    public void NextGeneration_starts_at_1()
    {
        Assert.Equal(1, CoreReloadPolicy.FirstGeneration);
        Assert.Equal(1, CoreReloadPolicy.NextGeneration(0));
        Assert.Equal(2, CoreReloadPolicy.NextGeneration(1));
        Assert.Equal(3, CoreReloadPolicy.NextGeneration(2));
    }

    [Fact]
    public void MayStart_refuses_a_live_previous_generation_and_an_in_flight_swap()
    {
        Assert.True(CoreReloadPolicy.MayStart(previousGenerationAlive: false, alreadyReloading: false));
        Assert.False(CoreReloadPolicy.MayStart(previousGenerationAlive: true, alreadyReloading: false));
        Assert.False(CoreReloadPolicy.MayStart(previousGenerationAlive: false, alreadyReloading: true));
        Assert.False(CoreReloadPolicy.MayStart(previousGenerationAlive: true, alreadyReloading: true));
    }

    [Fact]
    public void Refuse_messages_are_the_grep_lines()
    {
        Assert.Equal("Core reload refused: a swap is already in progress.", CoreReloadPolicy.RefuseAlreadyReloading);
        Assert.Equal("Core reload scheduled: cannot swap from inside Core tick.", CoreReloadPolicy.ScheduledInsideTick);
        Assert.Equal("scheduled gen=1", CoreReloadPolicy.FormatScheduled(1));
        Assert.Equal("scheduled gen=1 dryRun", CoreReloadPolicy.FormatScheduled(1, dryRun: true));
        Assert.Equal(1, CoreReloadPolicy.ScheduledGeneration(0));
        Assert.Equal("promoted Progenitor.dll.next", CoreReloadPolicy.FormatPromotedNext());
        Assert.Equal(
            "Core install gen=1 from watch (no previous generation)",
            CoreReloadPolicy.FormatCoreLessInstall(1, "watch"));
        Assert.Equal(
            "Core not loaded: protocol mismatch; deploy a Core built for Bootstrap protocol 2",
            CoreReloadPolicy.FormatProtocolMismatchStayCoreLess(2));
        Assert.Contains("UiSlots", CoreReloadPolicy.FailedInstallSlots);
        Assert.Contains("lifetime.lateframe", CoreReloadPolicy.FailedInstallSlots);
        Assert.Equal("collect-wait frames=7 collected=False", CoreReloadPolicy.FormatCollectWait(7, collected: false));
        Assert.Equal("plugin-alc progenitor-reload:Sample:1 collected=True frames=11", CoreReloadPolicy.FormatPluginAlcCollect("progenitor-reload:Sample:1", collected: true, 11));
        Assert.Equal("plugin-alc progenitor-reload:Dev:2 collected=False frames=60", CoreReloadPolicy.FormatPluginAlcCollect("progenitor-reload:Dev:2", collected: false, 60));
        Assert.Equal(
            "refused: plugin ALC progenitor-reload:Sample:1 still alive; restored plugins=0",
            CoreReloadPolicy.FormatPluginAlcRefused(new[] { "progenitor-reload:Sample:1" }));
        Assert.Equal(
            "refused: plugin ALC progenitor-reload:Sample:1 still alive; restored plugins=2",
            CoreReloadPolicy.FormatPluginAlcRefused(new[] { "progenitor-reload:Sample:1" }, 2));
        Assert.Equal(
            "refused: plugin ALC progenitor-reload:Sample:1, progenitor-reload:Dev:2 still alive; restored plugins=0",
            CoreReloadPolicy.FormatPluginAlcRefused(new[] { "progenitor-reload:Sample:1", "progenitor-reload:Dev:2" }));
        Assert.Equal("refused: plugin ALC (unknown) still alive; restored plugins=0", CoreReloadPolicy.FormatPluginAlcRefused(null));
        Assert.Equal(
            "Core reload refused: plugin ALC progenitor-reload:Sample:1 still alive; restart the process.",
            CoreReloadPolicy.RefusePluginAlcAlive(new[] { "progenitor-reload:Sample:1" }));
        Assert.Equal("(enumeration-failed)", CoreReloadPolicy.EnumerationFailedAlcName);
        Assert.Equal("(unnamed)", CoreReloadPolicy.UnnamedAlcName);
        Assert.Equal(
            new[] { "a", CoreReloadPolicy.EnumerationFailedAlcName },
            CoreReloadPolicy.ClosePluginAlcListing(new List<string> { "a" }, enumerationFailed: true));
        Assert.Equal(
            new[] { CoreReloadPolicy.EnumerationFailedAlcName },
            CoreReloadPolicy.ClosePluginAlcListing(new List<string>(), enumerationFailed: true));
        Assert.Empty(CoreReloadPolicy.ClosePluginAlcListing(new List<string>(), enumerationFailed: false));
        Assert.Equal("pin-diag alcs=[]", CoreReloadPolicy.FormatCollectibleAlcs(null));
        Assert.Equal("pin-diag alcs=[]", CoreReloadPolicy.FormatCollectibleAlcs(Array.Empty<string>()));
        Assert.Equal("pin-diag alcs=[(unnamed)]", CoreReloadPolicy.FormatCollectibleAlcs(new[] { "" }));
        Assert.Equal(
            "pin-diag alcs=[progenitor-core:2, progenitor-reload:Sample:1]",
            CoreReloadPolicy.FormatCollectibleAlcs(new[] { "progenitor-reload:Sample:1", "progenitor-core:2" }));
        Assert.Equal(
            "pin-diag alcs=[(enumeration-failed)]",
            CoreReloadPolicy.FormatCollectibleAlcs(
                CoreReloadPolicy.ClosePluginAlcListing(new List<string>(), enumerationFailed: true)));
        Assert.Equal(60, CoreReloadPolicy.PluginAlcWaitFrames);
        Assert.Equal("pin-diag stack frames=12 plugin=0 core=0 dirty=False", CoreReloadPolicy.FormatStackCheck(12, 0, 0));
        Assert.Equal("pin-diag stack frames=8 plugin=1 core=2 dirty=True", CoreReloadPolicy.FormatStackCheck(8, 1, 2));
        Assert.Equal(1, CoreReloadPolicy.UnityFlushFrames);
        Assert.True(CoreReloadPolicy.MayDestroyImmediateOnUninstall);
        Assert.False(CoreReloadPolicy.MayMutateDelegateCaches);
        Assert.Equal("unity-teardown frame=12", CoreReloadPolicy.FormatUnityTeardown(12));
        Assert.Equal("unload-alc frame=13", CoreReloadPolicy.FormatUnloadAlcFrame(13));
        Assert.Equal("collect frame=14", CoreReloadPolicy.FormatCollectFrame(14));
        Assert.Equal(
            "pin-diag DelegateSupport NativeToManagedTrampolines core=3 (append-only; not removed)",
            CoreReloadPolicy.FormatDelegateSupportAppendOnly(3));
        Assert.Equal(
            "pin-diag HarmonyIL2CPP DelegateCache core=1 (append-only; not removed)",
            CoreReloadPolicy.FormatHarmonyIl2CppAppendOnly(1));
        Assert.Equal(60, CoreReloadPolicy.CollectWaitFrames);
        Assert.Equal(10, CoreReloadPolicy.CollectPulseEveryFrames);
        Assert.Equal(10, CoreReloadPolicy.CollectPulseEveryFrameUntil);
        Assert.Equal(5, CoreReloadPolicy.CollectPulseStrideAfter);
        Assert.Equal(8, CoreReloadPolicy.HitchBackoffMs);
        Assert.Equal(2, CoreReloadPolicy.HitchBackoffAfter);
        Assert.Equal(10, CoreReloadPolicy.HitchBackoffStride);
        Assert.Equal(30, CoreReloadPolicy.PluginLatchPollEveryFrames);
        Assert.True(CoreReloadPolicy.ShouldPulseCollect(0, 10));
        Assert.False(CoreReloadPolicy.ShouldPulseCollect(1, 10));
        Assert.True(CoreReloadPolicy.ShouldPulseCollect(10, 10));
        Assert.False(CoreReloadPolicy.ShouldPulseCollect(-1, 10));
        Assert.True(CoreReloadPolicy.ShouldPulseCoreCollect(0));
        Assert.True(CoreReloadPolicy.ShouldPulseCoreCollect(9));
        Assert.True(CoreReloadPolicy.ShouldPulseCoreCollect(10));
        Assert.False(CoreReloadPolicy.ShouldPulseCoreCollect(11));
        Assert.False(CoreReloadPolicy.ShouldPulseCoreCollect(14));
        Assert.True(CoreReloadPolicy.ShouldPulseCoreCollect(15));
        Assert.False(CoreReloadPolicy.ShouldPulseCoreCollect(-1));
        Assert.Equal("collect-wait hitch backoff: pulses >8ms twice; stepping to every 10th frame after frame 10", CoreReloadPolicy.FormatCollectHitchBackoff());
        Assert.True(CoreReloadPolicy.ShouldPulseCoreCollect(1, consecutiveHitches: 2));
        Assert.True(CoreReloadPolicy.ShouldPulseCoreCollect(9, consecutiveHitches: 2));
        Assert.True(CoreReloadPolicy.ShouldPulseCoreCollect(0, consecutiveHitches: 2));
        Assert.True(CoreReloadPolicy.ShouldPulseCoreCollect(10, consecutiveHitches: 2));
        Assert.False(CoreReloadPolicy.ShouldPulseCoreCollect(11, consecutiveHitches: 2));
        Assert.True(CoreReloadPolicy.ShouldPulseCoreCollect(59, consecutiveHitches: 2));
        Assert.True(CoreReloadPolicy.ShouldPollPluginLatch(0, 30));
        Assert.False(CoreReloadPolicy.ShouldPollPluginLatch(1, 30));
        Assert.True(CoreReloadPolicy.ShouldPollPluginLatch(30, 30));
        Assert.False(CoreReloadPolicy.ShouldPollPluginLatch(-1, 30));
        Assert.Equal("collect-wait hitch ms=12", CoreReloadPolicy.FormatCollectHitch(12));
        Assert.True(CoreReloadPolicy.IsPluginAlcName("progenitor-reload:Sample:1"));
        Assert.False(CoreReloadPolicy.IsPluginAlcName("progenitor-core:1"));
        Assert.False(CoreReloadPolicy.IsPluginAlcName(""));
        Assert.Equal(
            "Core reload refused: generation 1 (progenitor-core:1) is still alive; restart the process.",
            CoreReloadPolicy.RefusePreviousAlive(1, "progenitor-core:1"));
        Assert.Equal(
            "Core reload refused: generation 2 (progenitor-core:2) is still alive; restart the process.",
            CoreReloadPolicy.RefusePreviousAlive(2, null));
        Assert.StartsWith("Core reload gen 2: ", CoreReloadPolicy.StepLog(2, "freeze"));
        Assert.Equal(
            "ok gen=2 collected=True pluginsUnloaded=1 pluginsReloaded=1 reason=bridge",
            CoreReloadPolicy.FormatOk(2, collected: true, pluginsUnloaded: 1, pluginsReloaded: 1, reason: "bridge"));
        Assert.Equal(
            "failed gen=2 step=load oldRunning=False error=boom",
            CoreReloadPolicy.FormatFailed(2, "load", "boom", oldRunning: false));
        Assert.True(CoreReloadPolicy.MayLoadNextGeneration(collected: true));
        Assert.False(CoreReloadPolicy.MayLoadNextGeneration(collected: false));
        Assert.Equal(
            "Core reload: generation 1 (progenitor-core:1) did not collect; restoring via fresh ALC instead of stacking N+1.",
            CoreReloadPolicy.DidNotCollect(1, "progenitor-core:1"));
        Assert.Equal(
            "Core reload: generation 2 (progenitor-core:2) did not collect; restoring via fresh ALC instead of stacking N+1.",
            CoreReloadPolicy.DidNotCollect(2, null));
        Assert.Equal(
            "refused: generation 1 still alive; restored; restart the process",
            CoreReloadPolicy.FormatRestored(1));
        Assert.Equal(
            "CORE-LESS last resort: generation 1 did not collect and fresh LoadAndInstall failed: boom; process has no Core until restart",
            CoreReloadPolicy.FormatCoreLess(1, "boom"));
        Assert.Equal(
            "abandon failed install gen=2 alc=progenitor-core:2; slots cleared before Unload",
            CoreReloadPolicy.FormatAbandonFailedInstall(2, "progenitor-core:2"));
        Assert.Equal(
            "abandon failed install gen=2 alc=progenitor-core:2; slots cleared before Unload",
            CoreReloadPolicy.FormatAbandonFailedInstall(2, null));
        Assert.Equal(
            "abandon failed install gen=0 alc=progenitor-core; slots cleared before Unload",
            CoreReloadPolicy.FormatAbandonFailedInstall(0, null));
        Assert.Equal(
            "N+1 install failed gen=2; restoring gen=1 via fresh LoadAndInstall",
            CoreReloadPolicy.FormatRestoreAfterFailedNext(1, 2));
        Assert.Equal(
            "install of generation 3 failed (protocol mismatch); generation 2 restored; no restart needed",
            CoreReloadPolicy.FormatFailedInstallRestored(3, "protocol mismatch", 2));
        Assert.Equal("restored", CoreReloadStatus.OutcomeRestored);
        Assert.True(CoreReloadPolicy.MayClearIl2CppCaches(0));
        Assert.False(CoreReloadPolicy.MayClearIl2CppCaches(1));
        Assert.False(CoreReloadPolicy.MayMutateDelegateCaches);
        Assert.True(CoreReloadPolicy.MayUnloadCollectibleAlc(0, 0, 0));
        Assert.False(CoreReloadPolicy.MayUnloadCollectibleAlc(1, 0, 0));
        Assert.False(CoreReloadPolicy.MayUnloadCollectibleAlc(0, 1, 0));
        Assert.False(CoreReloadPolicy.MayUnloadCollectibleAlc(0, 0, 1));
        Assert.Equal(
            "pin-diag ClassInjector progenitor-named=2 will-not-collect",
            CoreReloadPolicy.FormatClassInjectorWillNotCollect(2));
        Assert.Equal("Progenitor.Ticks.FrameTicker", CoreReloadPolicy.BootstrapFrameTickerFullName);
        Assert.True(CoreReloadPolicy.IsBootstrapInjectedFullName("Progenitor.Ticks.FrameTicker"));
        Assert.False(CoreReloadPolicy.IsClassInjectorCorePin("Progenitor.Ticks.FrameTicker"));
        Assert.True(CoreReloadPolicy.IsClassInjectorCorePin("Progenitor.Effects.CanvasGraphic"));
        Assert.False(CoreReloadPolicy.IsClassInjectorCorePin("Progenitor.Bootstrap.Ticks.FrameTicker"));
        Assert.False(CoreReloadPolicy.IsClassInjectorCorePin(null));
        Assert.Equal(
            "pin-diag injected-type name=Progenitor.Ticks.FrameTicker bootstrap=True",
            CoreReloadPolicy.FormatInjectedTypeLine("Progenitor.Ticks.FrameTicker"));
        Assert.Equal(
            "pin-diag native-pin kind=Delegate name=lifetime.quitting owner=progenitor-core:1 gen=progenitor-core:1",
            CoreReloadPolicy.FormatNativePinLine("Delegate", "lifetime.quitting", "progenitor-core:1", "progenitor-core:1"));
        Assert.Equal(
            "pin-diag holder=HarmonySlots._managedPrefix type=Progenitor.Runtime",
            CoreReloadPolicy.FormatHolder("HarmonySlots._managedPrefix", "Progenitor.Runtime"));
        Assert.Equal(
            "pin-diag holder=0Harmony:HarmonySharedState._state type=System.Object",
            CoreReloadPolicy.FormatHolder("0Harmony", "HarmonySharedState._state", "System.Object"));
        Assert.Equal("pin-diag holder-scan ms=12", CoreReloadPolicy.FormatHolderScanElapsed(12, wide: false));
        Assert.Equal("pin-diag holder-scan wide ms=240", CoreReloadPolicy.FormatHolderScanElapsed(240, wide: true));
        Assert.Equal("pin-diag holder-scan truncated ms=400", CoreReloadPolicy.FormatHolderScanTruncated(400));
        Assert.Equal(400, CoreReloadHolderScan.HolderScanBudgetMilliseconds);
        Assert.True(CoreReloadPolicy.IsWaitedCollectibleAlc("progenitor-reload:Sample:1"));
        Assert.True(CoreReloadPolicy.IsWaitedCollectibleAlc(CoreReloadPolicy.EnumerationFailedAlcName));
        Assert.False(CoreReloadPolicy.IsWaitedCollectibleAlc("ThirdParty.Mod"));
        Assert.False(CoreReloadPolicy.IsWaitedCollectibleAlc(""));
        Assert.False(CoreReloadPolicy.IsWaitedCollectibleAlc(null));
        Assert.False(CoreReloadPolicy.IsWaitedCollectibleAlc("progenitor-core:1"));
        Assert.True(CoreReloadPolicy.IsProgenitorAlcName("progenitor-core:1"));
        Assert.True(CoreReloadPolicy.IsProgenitorAlcName("progenitor-reload:Sample:1"));
        Assert.False(CoreReloadPolicy.IsProgenitorAlcName("ThirdParty.Mod"));
        Assert.Equal(1, CoreReloadPolicy.CountWaitedAlcNames(new[] { "progenitor-reload:Sample:1", "Someone.Collectible", "progenitor-core:2" }));
        Assert.Equal(
            new[] { "progenitor-reload:Sample:1" },
            CoreReloadPolicy.FilterWaitedAlcNames(new[] { "Someone.Collectible", "progenitor-reload:Sample:1", "" }));
        Assert.True(CoreReloadPolicy.PluginAlcNameMatches("progenitor-reload:Sample:123", "Sample"));
        Assert.False(CoreReloadPolicy.PluginAlcNameMatches("progenitor-reload:Dev:123", "Sample"));
        Assert.True(CoreReloadPolicy.ShouldRefusePluginAlcName("Sample", new[] { "progenitor-reload:Sample:1" }));
        Assert.False(CoreReloadPolicy.ShouldRefusePluginAlcName("Dev", new[] { "progenitor-reload:Sample:1" }));
        Assert.True(CoreReloadPolicy.MayCreatePluginAlc(latchedAlive: false, evenIfLatched: false));
        Assert.False(CoreReloadPolicy.MayCreatePluginAlc(latchedAlive: true, evenIfLatched: false));
        Assert.True(CoreReloadPolicy.MayCreatePluginAlc(latchedAlive: true, evenIfLatched: true));
        Assert.True(CoreReloadPolicy.ShouldClearForcePluginLatch(enumerationSucceeded: true, waitedCount: 0));
        Assert.False(CoreReloadPolicy.ShouldClearForcePluginLatch(enumerationSucceeded: false, waitedCount: 0));
        Assert.True(CoreReloadPolicy.ShouldClearForcePluginLatch(enumerationSucceeded: true, waitedCount: 1));
        Assert.False(CoreReloadPolicy.ShouldLatchFailedInstallContext(installInvoked: false));
        Assert.True(CoreReloadPolicy.ShouldLatchFailedInstallContext(installInvoked: true));
        Assert.False(CoreReloadPolicy.ShouldLatchFailedInstallContext(
            installInvoked: true,
            error: CoreReloadPolicy.FormatProtocolMismatch(1, 2)));
        Assert.False(CoreReloadPolicy.ShouldLatchFailedInstallContext(
            installInvoked: true,
            error: "InvalidOperationException: " + CoreReloadPolicy.FormatProtocolMismatch(1, 2)));
        Assert.True(CoreReloadPolicy.ShouldLatchFailedInstallContext(
            installInvoked: true,
            error: "NullReferenceException: slot"));
        Assert.True(CoreReloadPolicy.IsProtocolMismatchMessage(CoreReloadPolicy.FormatProtocolMismatch(1, 2)));
        Assert.False(CoreReloadPolicy.IsProtocolMismatchMessage("Install threw"));
        Assert.False(CoreReloadPolicy.LogListenerInstalledAfterUninstall(removeSucceeded: true));
        Assert.True(CoreReloadPolicy.LogListenerInstalledAfterUninstall(removeSucceeded: false));
        Assert.True(CoreReloadHolderScan.IsWideScanAssemblyName("0Harmony", location: null));
        Assert.True(CoreReloadHolderScan.IsWideScanAssemblyName("BepInEx.Core", location: null));
        Assert.True(CoreReloadHolderScan.IsWideScanAssemblyName("MonoMod.Utils", location: null));
        Assert.True(CoreReloadHolderScan.IsWideScanAssemblyName("Il2CppInterop.Runtime", location: null));
        Assert.True(CoreReloadHolderScan.IsWideScanAssemblyName("Progenitor.Bootstrap", location: null));
        Assert.False(CoreReloadHolderScan.IsWideScanAssemblyName(
            "SomePlugin",
            @"C:\game\BepInEx\plugins\SomePlugin.dll"));
        Assert.True(CoreReloadHolderScan.IsBepInExPluginLocation(@"C:\game\BepInEx\plugins\SomePlugin.dll"));
        Assert.True(CoreReloadHolderScan.IsKnownInitializedStaticTypeName("HarmonyLib.HarmonySharedState"));
        Assert.True(CoreReloadHolderScan.IsKnownInitializedStaticTypeName("HarmonyLib.PatchFunctions"));
        Assert.True(CoreReloadHolderScan.IsKnownInitializedStaticTypeName("HarmonyLib.Internal.Patching.PatchFunctions"));
        Assert.True(CoreReloadHolderScan.IsKnownInitializedStaticTypeName("BepInEx.Configuration.ConfigFile"));
        Assert.True(CoreReloadHolderScan.IsKnownInitializedStaticTypeName("BepInEx.Logging.Logger"));
        Assert.False(CoreReloadHolderScan.IsKnownInitializedStaticTypeName("HarmonyLib.Harmony"));
        Assert.False(CoreReloadHolderScan.IsWideScanAssemblyName("ProjectM", @"C:\game\BepInEx\interop\ProjectM.dll"));
        Assert.False(CoreReloadHolderScan.IsWideScanAssemblyName("UnityEngine.CoreModule", location: null));
        Assert.False(CoreReloadHolderScan.IsWideScanAssemblyName("Il2CppSystem", location: null));
        Assert.False(CoreReloadHolderScan.IsWideScanAssemblyName("Progenitor.Tests", location: null));
        Assert.False(CoreReloadHolderScan.IsWideScanAssembly(typeof(object).Assembly));
        Assert.Equal("last-good bytes=2048", CoreReloadPolicy.FormatLastGoodBytes(2048));
        Assert.Equal(
            "pin-diag native-pin-gen gen=progenitor-core:2 count=9",
            CoreReloadPolicy.FormatNativePinGeneration("progenitor-core:2", 9));
        Assert.Equal("pin-diag native-pin-gen gen= count=0", CoreReloadPolicy.FormatNativePinGeneration(null, 0));
        Assert.Equal(
            "pin-release NativePinRegistry gen=progenitor-core:1 before=9 after=0 dropped=9",
            CoreReloadPolicy.FormatPinRelease("progenitor-core:1", 9, 0, 9));
        Assert.Equal("ReaskConnectedHandshake", CoreLayout.ReaskHandshakeMethod);
        Assert.Equal("DiagnosePins", CoreLayout.DiagnosePinsMethod);
        Assert.Equal(
            "dryRun gen=1 collected=False restored=True",
            CoreReloadPolicy.FormatDryRun(1, collected: false, restored: true));
        Assert.True(CoreReloadPolicy.ParseDryRun("dryRun"));
        Assert.True(CoreReloadPolicy.ParseDryRun("{\"dryRun\":true}"));
        Assert.True(CoreReloadPolicy.ParseDryRun("bridge core_reload dryRun"));
        Assert.False(CoreReloadPolicy.ParseDryRun("bridge core_reload"));
        Assert.False(CoreReloadPolicy.ParseDryRun("{\"dryRun\":false}"));
        Assert.False(CoreReloadPolicy.ParseDryRun(null));
        Assert.False(CoreReloadPolicy.ParseDryRun(""));
        Assert.True(CoreReloadPolicy.ParseDryRunWide("dryRunWide"));
        Assert.True(CoreReloadPolicy.ParseDryRunWide("bridge dryRunWide"));
        Assert.True(CoreReloadPolicy.ParseDryRun("dryRunWide"));
        Assert.False(CoreReloadPolicy.ParseDryRunWide("dryRun"));
        Assert.False(CoreReloadPolicy.ParseDryRunWide(null));
        Assert.Equal("HolderScanWide", HarmonySlotPolicy.HolderScanWideKey);
        // R31 finding 13 (job FRLD3) made it 3 for the plugin unload hand-off slot; job FHOOK3
        // made it 4 by widening the four system-update slot delegates to carry Core's call token;
        // the 2026-10 id rename made it 5 (Bootstrap GUID, Harmony id and cfg names).
        Assert.Equal(5, CoreLayout.ProtocolVersion);
        Assert.Equal("Progenitor.BootstrapLink", CoreLayout.ProtocolTypeName);
        Assert.Equal("ProtocolVersion", CoreLayout.ProtocolFieldName);
        Assert.Equal(
            "Core reload refused: Bootstrap protocol 1 does not accept Core protocol 0.",
            CoreReloadPolicy.FormatProtocolMismatch(1, 0));
        Assert.Equal(
            "Core reload failed: Install of generation 2 failed (boom); quarantined Progenitor.dll.failed-20260903120000; restored last-good",
            CoreReloadPolicy.FormatFailedInstall(2, "boom", "Progenitor.dll.failed-20260903120000", restoredLastGood: true));
        Assert.Equal(
            "Core reload failed: Install of generation 2 failed (boom); quarantined Progenitor.dll.failed-1; no last-good to restore",
            CoreReloadPolicy.FormatFailedInstall(2, "boom", "Progenitor.dll.failed-1", restoredLastGood: false));
        Assert.Equal(
            "lastReload gen=2 outcome=ok reason=watch timestamp=",
            CoreReloadPolicy.FormatLastReload(2, "ok", "watch", default));
        Assert.Equal("gc-collect", CoreReloadPolicy.PulseCollectStep);
        Assert.Equal("wait-pending-finalizers", CoreReloadPolicy.PulseWaitStep);
        Assert.Equal("check", CoreReloadPolicy.PulseCheckStep);
        Assert.Equal("quarantine", CoreReloadOrchestrator.StepQuarantine);
    }

    [Fact]
    public void WaitUntilCollected_returns_true_once_stillAlive_is_false()
    {
        int pulses = 0;
        int remaining = 3;
        bool collected = CoreReloadPolicy.WaitUntilCollected(
            () => remaining-- > 0,
            () => pulses++,
            attempts: 10);
        Assert.True(collected);
        Assert.Equal(3, pulses);
    }

    [Fact]
    public void WaitUntilCollected_returns_false_when_the_ALC_never_dies()
    {
        int pulses = 0;
        bool collected = CoreReloadPolicy.WaitUntilCollected(
            () => true,
            () => pulses++,
            attempts: 4);
        Assert.False(collected);
        Assert.Equal(4, pulses);
    }

    [Fact]
    public void WaitUntilCollected_is_true_immediately_when_already_dead()
    {
        int pulses = 0;
        bool collected = CoreReloadPolicy.WaitUntilCollected(
            () => false,
            () => pulses++,
            attempts: 4);
        Assert.True(collected);
        Assert.Equal(0, pulses);
    }

    [Fact]
    public void Core_ALC_Resolving_returns_default_assemblies_except_Progenitor()
    {
        var self = typeof(CoreReloadPolicyTests).Assembly;
        string selfName = self.GetName().Name!;
        var list = new Assembly[] { self };
        Assert.Same(self, CoreAssemblyResolver.Resolve(selfName, list));
        Assert.Null(CoreAssemblyResolver.Resolve(CoreAssemblyResolver.CoreSimpleName, list));
        Assert.Null(CoreAssemblyResolver.Resolve("Progenitor", list));
        Assert.Null(CoreAssemblyResolver.Resolve("BepInEx.Core", list));
        Assert.Null(CoreAssemblyResolver.Resolve("", list));
        Assert.Null(CoreAssemblyResolver.Resolve(null, list));
    }

    [Fact]
    public void Layout_names_the_snapshot_and_live_world_methods()
    {
        Assert.Equal("SnapshotContent", CoreLayout.SnapshotMethod);
        Assert.Equal("NotifyLiveWorldAfterCoreSwap", CoreLayout.NotifyLiveWorldMethod);
        Assert.Equal("ApplyKeyPlaceholdersAfterPlugins", CoreLayout.ApplyPlaceholdersMethod);
        Assert.Equal("DiagnosePins", CoreLayout.DiagnosePinsMethod);
        Assert.Equal("ReaskConnectedHandshake", CoreLayout.ReaskHandshakeMethod);
    }

    [Fact]
    public void CreateGenerationWeak_tracks_resurrection()
    {
        var weak = CoreReloadPolicy.CreateGenerationWeak(new object());
        Assert.True(weak.TrackResurrection);
        Assert.Throws<ArgumentNullException>(() => CoreReloadPolicy.CreateGenerationWeak(null!));
    }

    [Fact]
    public void PulseCollectThenCheck_records_collect_then_wait_then_check()
    {
        var notes = new List<string>();
        bool collected = CoreReloadPolicy.PulseCollectThenCheck(() => false, notes.Add);
        Assert.True(collected);
        Assert.Equal(
            new[]
            {
                CoreReloadPolicy.PulseCollectStep,
                CoreReloadPolicy.PulseWaitStep,
                CoreReloadPolicy.PulseCheckStep,
            },
            notes);
    }

    [Fact]
    public void PulseCollectThenCheck_is_not_collected_while_the_weak_is_alive()
    {
        var notes = new List<string>();
        bool collected = CoreReloadPolicy.PulseCollectThenCheck(() => true, notes.Add);
        Assert.False(collected);
        Assert.Equal(CoreReloadPolicy.PulseCollectStep, notes[0]);
        Assert.Equal(CoreReloadPolicy.PulseWaitStep, notes[1]);
        Assert.Equal(CoreReloadPolicy.PulseCheckStep, notes[2]);
    }

    [Fact]
    public void AcceptProtocol_requires_matching_positive_versions()
    {
        Assert.True(CoreReloadPolicy.AcceptProtocol(1, 1, out var ok));
        Assert.Equal("", ok);
        Assert.False(CoreReloadPolicy.AcceptProtocol(1, 0, out var missing));
        Assert.Equal(CoreReloadPolicy.FormatProtocolMismatch(1, 0), missing);
        Assert.False(CoreReloadPolicy.AcceptProtocol(1, 2, out var mismatch));
        Assert.Contains("protocol 1", mismatch);
        Assert.Contains("protocol 2", mismatch);
        Assert.False(CoreReloadPolicy.AcceptProtocol(0, 0, out _));
        Assert.Equal(0, CoreReloadPolicy.ReadProtocolVersion(null));
        Assert.Equal(0, CoreReloadPolicy.ReadProtocolVersion(typeof(CoreReloadPolicyTests).Assembly));
    }

    [Fact]
    public void Collect_helpers_are_NoInlining_and_the_ALC_nulls_its_fields()
    {
        var prepare = typeof(CoreReloadCollect).GetMethod(nameof(CoreReloadCollect.PrepareAndUnload));
        var request = typeof(CoreReloadCollect).GetMethod(nameof(CoreReloadCollect.RequestUnload));
        var neverInstalled = typeof(CoreReloadCollect).GetMethod(nameof(CoreReloadCollect.UnloadNeverInstalled));
        Assert.NotNull(prepare);
        Assert.NotNull(request);
        Assert.NotNull(neverInstalled);
        Assert.True(HasNoInlining(prepare!));
        Assert.True(HasNoInlining(request!));
        Assert.True(HasNoInlining(neverInstalled!));

        var alc = new CoreLoadContext("progenitor-core:test-prepare");
        alc.AttachResolving((_, _) => null);
        Assert.True(alc.HasResolvingHandler);
        alc.PrepareUnload();
        Assert.False(alc.HasResolvingHandler);
        alc.Unload();
    }

    private static bool HasNoInlining(MethodInfo method) =>
        (method.MethodImplementationFlags & MethodImplAttributes.NoInlining) != 0;
}

/// <summary>Fake cascade host used by <see cref="CoreReloadOrchestratorTests"/>.</summary>
internal sealed class FakeReloadOps : ICoreReloadOps
{
    public readonly List<string> Steps = new();
    /// <summary>Job FINJ1: core-bound process-lifetime plugins the live ops would report.</summary>
    public readonly List<string> LifetimeBound = new();
    public IReadOnlyList<string> CoreBoundProcessLifetimePlugins() => LifetimeBound;
    public bool PreviousGenerationAlive { get; set; }
    public bool AlreadyReloading { get; set; }
    public int CurrentGeneration { get; set; }
    public string? CurrentAlcName { get; set; }
    public bool ReadOk { get; set; } = true;
    public string ReadError { get; set; } = "missing";
    public byte[] Bytes { get; set; } = new byte[] { 1, 2, 3 };
    public bool UninstallOk { get; set; } = true;
    public string UninstallError { get; set; } = "uninstall boom";
    public bool UnloadAlcOk { get; set; } = true;
    public bool Collects { get; set; } = true;
    public bool LoadOk { get; set; } = true;
    public string LoadError { get; set; } = "load boom";
    public bool ReinstallOk { get; set; } = true;
    public string ReinstallError { get; set; } = "restore boom";
    public int UnloadPluginCount { get; set; } = 1;
    public int ReloadPluginCount { get; set; } = 1;
    public string? ThrowOn { get; set; }
    public bool YieldPluginAlcWait { get; set; }
    public bool YieldUnityFlush { get; set; }
    public int CurrentFrame { get; set; }
    public bool PluginAlcsNeverCollectable { get; set; }
    public byte[]? LastGoodBytes { get; set; } = new byte[] { 1, 2, 3 };
    public byte[]? LastLoadedBytes { get; private set; }
    public List<string> AlivePluginAlcs { get; } = new();
    public List<string> WatchedPluginAlcs { get; } = new();
    public List<string> LatchedPluginAlcNameList { get; } = new();
    public bool LatchedPluginAlcsAlive { get; set; }
    public IReadOnlyList<string> AlivePluginAlcNames() => AlivePluginAlcs;
    public IReadOnlyList<string> WatchedPluginAlcNames() => WatchedPluginAlcs;
    public IReadOnlyList<string> LatchedPluginAlcNames() => LatchedPluginAlcNameList;
    public void LatchUncollectedPluginAlcs()
    {
        LatchedPluginAlcNameList.Clear();
        LatchedPluginAlcNameList.AddRange(AlivePluginAlcs);
        LatchedPluginAlcsAlive = LatchedPluginAlcNameList.Count > 0;
    }
    public int LoadedGeneration { get; private set; }
    public int LoadAttempts { get; private set; }
    /// <summary>When set, <see cref="LoadAndInstall"/> returns false only for this generation.</summary>
    public int? FailLoadGeneration { get; set; }
    public bool DryRun { get; set; }
    public bool Reinstalled { get; private set; }
    public bool Unretired { get; private set; }
    public bool ClearedSlots { get; private set; }
    public readonly List<string> ReleasedGenerations = new();
    /// <summary>
    /// Live UnloadAlc returns ok=true, collected=false so the frame driver can poll.
    /// Tests of <see cref="CoreReloadOrchestrator.Run"/> leave this false so a failed
    /// collect still restores in the same call.
    /// </summary>
    public bool YieldCollect { get; set; }
    /// <summary>True until <see cref="DropOldGenerationRefs"/> / UnloadAlc unpublishes.</summary>
    public bool HasPublishedAssembly { get; set; } = true;
    /// <summary>Stand-in for the old Core runtime object. Must be nulled before collect.</summary>
    public object? OldRuntime { get; set; }
    /// <summary>Stand-in collectible ALC. Unloaded and nulled in <see cref="DropOldGenerationRefs"/>.</summary>
    public AssemblyLoadContext? OldAlc { get; set; }
    /// <summary>
    /// Generation published by the last successful <see cref="LoadAndInstall"/>.
    /// The reload path's leftover pin: must be dropped in
    /// <see cref="DropOldGenerationRefs"/> the same way boot-path Uninstall drops slots.
    /// </summary>
    public object? PublishedRuntime { get; set; }
    /// <summary>Collectible ALC of <see cref="PublishedRuntime"/>.</summary>
    public AssemblyLoadContext? PublishedAlc { get; set; }
    /// <summary>
    /// Slot forward bound at Install (Harmony / receive / lifetime). Reload-path
    /// Install fills this; UnloadAlc must clear it or generation 2 stays rooted.
    /// </summary>
    public object? SlotForward { get; set; }
    /// <summary>Keep-time generation id, advanced before Install like live PinGeneration.</summary>
    public string? PinGeneration { get; set; }

    public bool TryReadCoreBytes(out byte[] bytes, out string error)
    {
        Note(CoreReloadOrchestrator.StepRead);
        bytes = Bytes;
        error = ReadError;
        return ReadOk;
    }

    public void Freeze() => Note(CoreReloadOrchestrator.StepFreeze);
    public void Unfreeze() => Note(CoreReloadOrchestrator.StepUnfreeze);
    public readonly List<string> Quarantined = new();
    public readonly List<int> QuarantinedGenerations = new();
    public int Pulses;
    public int PulseMilliseconds { get; set; }
    public Action? OnPulse { get; set; }
    public int PulseCollect()
    {
        Pulses++;
        Note(CoreReloadPolicy.PulseCollectStep);
        Note(CoreReloadPolicy.PulseWaitStep);
        OnPulse?.Invoke();
        return PulseMilliseconds;
    }
    public void QuarantineFailedCore(int generation, string error)
    {
        QuarantinedGenerations.Add(generation);
        Quarantined.Add(error);
        Note(CoreReloadOrchestrator.StepQuarantine);
    }
    public int UnloadPlugins()
    {
        Note(CoreReloadOrchestrator.StepUnloadPlugins);
        WatchedPluginAlcs.Clear();
        WatchedPluginAlcs.AddRange(AlivePluginAlcs);
        return UnloadPluginCount;
    }
    public bool SnapshotOk { get; set; } = true;
    public string SnapshotError { get; set; } = "snapshot boom";
    public bool SnapshotContent(out string error)
    {
        Note(CoreReloadOrchestrator.StepSnapshot);
        error = SnapshotError;
        return SnapshotOk;
    }
    public bool UninstallCore(out string error)
    {
        Note(CoreReloadOrchestrator.StepUninstall);
        error = UninstallError;
        return UninstallOk;
    }
    public void DiagnosePins() => Note(CoreReloadOrchestrator.StepDiagnose);
    public void DropCoreWork() => Note(CoreReloadOrchestrator.StepDropWork);
    public void NullRuntime()
    {
        Note(CoreReloadOrchestrator.StepNullRuntime);
        OldRuntime = null;
        SlotForward = null;
    }
    public void DropOldGenerationRefs()
    {
        OldRuntime = null;
        SlotForward = null;
        PinGeneration = null;
        HasPublishedAssembly = false;
        CurrentGeneration = 0;
        CurrentAlcName = null;
        var oldAlc = OldAlc;
        OldAlc = null;
        DiscardAlc(oldAlc);
        DiscardPublished();
    }

    private void DiscardPublished()
    {
        PublishedRuntime = null;
        var published = PublishedAlc;
        PublishedAlc = null;
        DiscardAlc(published);
    }

    private static void DiscardAlc(AssemblyLoadContext? alc)
    {
        if (alc == null) return;
        try { alc.Unload(); }
        catch (InvalidOperationException) { /* already unloading */ }
    }
    public bool UnloadAlc(out bool collected, out string error)
    {
        Note(CoreReloadOrchestrator.StepUnloadAlc);
        DropOldGenerationRefs();
        if (YieldCollect)
        {
            collected = false;
            error = "";
            return true;
        }
        collected = Collects;
        error = Collects ? "" : "alc boom";
        return UnloadAlcOk && Collects;
    }
    public bool LoadAndInstall(byte[] bytes, int generation, out string error)
    {
        Note(CoreReloadOrchestrator.StepLoad);
        LoadAttempts++;
        LoadedGeneration = generation;
        LastLoadedBytes = bytes;
        error = LoadError;
        if (FailLoadGeneration is int fail && generation == fail)
            return false;
        if (!LoadOk)
            return false;
        // Advance PinGeneration before the published object exists — the live
        // Install KeepDelegate path. Then bind a slot forward to the new runtime.
        PinGeneration = CoreReloadPolicy.AlcName(generation);
        DiscardPublished();
        CurrentGeneration = generation;
        CurrentAlcName = PinGeneration;
        PublishedRuntime = new object();
        PublishedAlc = new AssemblyLoadContext(PinGeneration, isCollectible: true);
        SlotForward = PublishedRuntime;
        HasPublishedAssembly = true;
        return true;
    }
    public void ClearSlotsAfterFailedInstall()
    {
        Note(CoreReloadOrchestrator.StepClearSlots);
        ClearedSlots = true;
    }
    public void ReleaseFailedGeneration(string? alcName)
    {
        Note(CoreReloadOrchestrator.StepReleaseFailed);
        if (!string.IsNullOrEmpty(alcName))
            ReleasedGenerations.Add(alcName);
    }
    public bool ReinstallCurrent(out string error)
    {
        Note(CoreReloadOrchestrator.StepRestore);
        Reinstalled = true;
        error = ReinstallError;
        return ReinstallOk;
    }
    public void UnretireCoreWork() => Unretired = true;
    public void OpenLiveGate() => Note(CoreReloadOrchestrator.StepOpenGate);
    public bool Reasked { get; private set; }
    public void ReaskConnectedHandshake() => Reasked = true;
    public int ReloadPlugins() { Note(CoreReloadOrchestrator.StepReloadPlugins); return ReloadPluginCount; }
    public readonly List<string> Logs = new();
    public void Log(string message) => Logs.Add(message);

    private void Note(string step)
    {
        Steps.Add(step);
        if (string.Equals(ThrowOn, step, StringComparison.Ordinal))
            throw new InvalidOperationException("injected " + step);
    }
}

public class CoreReloadOrchestratorTests
{
    [Fact]
    public void Refuses_when_a_previous_generation_is_still_alive()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, CurrentAlcName = "progenitor-core:1", PreviousGenerationAlive = true };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.True(result.Refused);
        Assert.False(result.Ok);
        Assert.True(result.OldGenerationRunning);
        Assert.Contains("still alive", result.Summary);
        Assert.Empty(ops.Steps);
    }

    [Fact]
    public void Refuses_when_a_swap_is_already_in_progress()
    {
        var ops = new FakeReloadOps { AlreadyReloading = true, CurrentGeneration = 1 };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.True(result.Refused);
        Assert.Contains("already in progress", result.Summary);
        Assert.Empty(ops.Steps);
    }

    [Fact]
    public void Missing_file_leaves_the_old_generation_running()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, ReadOk = false, ReadError = "Core missing" };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.False(result.Ok);
        Assert.True(result.OldGenerationRunning);
        Assert.Equal(CoreReloadOrchestrator.StepRead, result.Step);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepFreeze, ops.Steps);
    }

    [Fact]
    public void Uninstall_failure_unfreezes_and_leaves_the_old_generation_running()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, UninstallOk = false };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.False(result.Ok);
        Assert.True(result.OldGenerationRunning);
        Assert.Equal(CoreReloadOrchestrator.StepUninstall, result.Step);
        Assert.Contains(CoreReloadOrchestrator.StepFreeze, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepUnfreeze, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepReloadPlugins, ops.Steps);
        Assert.True(
            ops.Steps.IndexOf(CoreReloadOrchestrator.StepUnfreeze)
            < ops.Steps.IndexOf(CoreReloadOrchestrator.StepReloadPlugins));
        Assert.DoesNotContain(CoreReloadOrchestrator.StepUnloadAlc, ops.Steps);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepLoad, ops.Steps);
    }

    [Fact]
    public void Throw_during_plugin_unload_recovers_plugins_without_reinstall()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, ThrowOn = CoreReloadOrchestrator.StepUnloadPlugins };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.False(result.Ok);
        Assert.True(result.OldGenerationRunning);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepRestore, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepReloadPlugins, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepUnfreeze, ops.Steps);
        Assert.True(
            ops.Steps.IndexOf(CoreReloadOrchestrator.StepUnfreeze)
            < ops.Steps.IndexOf(CoreReloadOrchestrator.StepReloadPlugins));
        Assert.DoesNotContain(CoreReloadOrchestrator.StepUnloadAlc, ops.Steps);
        Assert.False(ops.Reinstalled);
        Assert.False(ops.Reasked);
    }

    [Fact]
    public void DryRun_throw_during_plugin_unload_is_not_ok()
    {
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            DryRun = true,
            ThrowOn = CoreReloadOrchestrator.StepUnloadPlugins,
        };
        var result = CoreReloadOrchestrator.Run(ops, "dryRun");
        Assert.False(result.Ok);
        Assert.False(ops.Reinstalled);
        Assert.DoesNotContain("dryRun gen=", result.Summary);
    }

    [Fact]
    public void Throw_after_uninstall_restores_without_claiming_dryRun_ok()
    {
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            DryRun = true,
            ThrowOn = CoreReloadOrchestrator.StepDropWork,
        };
        var result = CoreReloadOrchestrator.Run(ops, "dryRun");
        Assert.False(result.Ok);
        Assert.True(ops.Reinstalled);
        Assert.True(ops.Unretired);
        Assert.Contains(CoreReloadOrchestrator.StepRestore, ops.Steps);
        Assert.False(ops.Reasked);
    }

    [Fact]
    public void Load_failure_after_ALC_unload_does_not_claim_the_old_generation_is_running()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, LoadOk = false };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.False(result.Ok);
        Assert.False(result.OldGenerationRunning);
        Assert.Equal(CoreReloadOrchestrator.StepLoad, result.Step);
        Assert.False(result.Collected);
        Assert.Contains(CoreReloadOrchestrator.StepUnfreeze, ops.Steps);
        Assert.True(ops.ClearedSlots);
        Assert.Equal(new[] { "progenitor-core:2", "progenitor-core:1" }, ops.ReleasedGenerations);
        Assert.Contains("oldRunning=False", result.Summary);
        Assert.Equal(2, ops.LoadAttempts);
        Assert.Equal(1, ops.LoadedGeneration);
    }

    [Fact]
    public void Failing_NPlus1_install_clears_slots_releases_generation_then_restores_N()
    {
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            CurrentAlcName = "progenitor-core:1",
            FailLoadGeneration = 2,
        };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.False(result.Ok);
        Assert.False(result.Refused);
        Assert.True(result.OldGenerationRunning);
        Assert.Equal(1, result.Generation);
        Assert.Equal(1, ops.LoadedGeneration);
        Assert.Equal(2, ops.LoadAttempts);
        Assert.True(ops.ClearedSlots);
        Assert.Equal(new[] { "progenitor-core:2" }, ops.ReleasedGenerations);
        Assert.Equal(new[] { 2 }, ops.QuarantinedGenerations);
        Assert.Equal(new[] { "load boom" }, ops.Quarantined);
        Assert.True(ops.Unretired);
        Assert.True(ops.Reasked);
        Assert.Contains(CoreReloadOrchestrator.StepQuarantine, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepClearSlots, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepReleaseFailed, ops.Steps);
        int firstLoad = ops.Steps.IndexOf(CoreReloadOrchestrator.StepLoad);
        int quarantine = ops.Steps.IndexOf(CoreReloadOrchestrator.StepQuarantine);
        int clear = ops.Steps.IndexOf(CoreReloadOrchestrator.StepClearSlots);
        int release = ops.Steps.IndexOf(CoreReloadOrchestrator.StepReleaseFailed);
        int restoreLoad = ops.Steps.LastIndexOf(CoreReloadOrchestrator.StepLoad);
        Assert.True(firstLoad >= 0);
        Assert.True(quarantine > firstLoad);
        Assert.True(clear > quarantine);
        Assert.True(release > clear);
        Assert.True(restoreLoad > release);
        Assert.Equal(
            "install of generation 2 failed (load boom); generation 1 restored; no restart needed",
            result.Summary);
        Assert.Equal(CoreReloadStatus.OutcomeRestored, result.Outcome);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepRestore, ops.Steps);
    }

    [Fact]
    public void Success_increments_generation_and_logs_collect_and_plugin_counts()
    {
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            UnloadPluginCount = 2,
            ReloadPluginCount = 2,
            Collects = true,
        };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.True(result.Ok);
        Assert.False(result.Refused);
        Assert.False(result.OldGenerationRunning);
        Assert.Equal(2, result.Generation);
        Assert.Equal(2, ops.LoadedGeneration);
        Assert.True(result.Collected);
        Assert.Equal(
            "ok gen=2 collected=True pluginsUnloaded=2 pluginsReloaded=2 reason=bridge",
            result.Summary);
        Assert.Equal(
            new[]
            {
                CoreReloadOrchestrator.StepRead,
                CoreReloadOrchestrator.StepFreeze,
                CoreReloadOrchestrator.StepUnloadPlugins,
                CoreReloadOrchestrator.StepDiagnose,
                CoreReloadOrchestrator.StepSnapshot,
                CoreReloadOrchestrator.StepUninstall,
                CoreReloadOrchestrator.StepDropWork,
                CoreReloadOrchestrator.StepNullRuntime,
                CoreReloadOrchestrator.StepUnloadAlc,
                CoreReloadOrchestrator.StepLoad,
                CoreReloadOrchestrator.StepOpenGate,
                CoreReloadOrchestrator.StepReloadPlugins,
                CoreReloadOrchestrator.StepUnfreeze,
            },
            ops.Steps);
    }

    [Fact]
    public void Success_with_empty_reload_folder_still_collects()
    {
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 0,
            UnloadPluginCount = 0,
            ReloadPluginCount = 0,
            Collects = true,
        };
        var result = CoreReloadOrchestrator.Run(ops, "empty");
        Assert.True(result.Ok);
        Assert.Equal(1, result.Generation);
        Assert.Equal("ok gen=1 collected=True pluginsUnloaded=0 pluginsReloaded=0 reason=empty", result.Summary);
    }

    [Fact]
    public void Did_not_collect_restores_generation_N_via_fresh_LoadAndInstall()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, CurrentAlcName = "progenitor-core:1", Collects = false };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.False(result.Ok);
        Assert.True(result.Refused);
        Assert.False(result.Collected);
        Assert.True(result.OldGenerationRunning);
        Assert.Equal(1, result.Generation);
        Assert.Equal(1, ops.LoadedGeneration);
        Assert.False(ops.Reinstalled);
        Assert.True(ops.Unretired);
        Assert.Contains(CoreReloadOrchestrator.StepLoad, ops.Steps);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepRestore, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepReloadPlugins, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepUnfreeze, ops.Steps);
        Assert.Equal("refused: generation 1 still alive; restored; restart the process", result.Summary);
        Assert.True(ops.Reasked);
        Assert.Null(ops.OldRuntime);
        Assert.True(ops.HasPublishedAssembly);
        Assert.NotNull(ops.PublishedRuntime);
        Assert.Equal(1, ops.CurrentGeneration);
    }

    [Fact]
    public void UnloadAlc_threw_but_collected_loads_next_generation_instead_of_Core_less()
    {
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            UnloadAlcOk = false,
            Collects = true,
        };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.True(result.Ok);
        Assert.False(result.OldGenerationRunning);
        Assert.True(result.Collected);
        Assert.Equal(2, result.Generation);
        Assert.Equal(2, ops.LoadedGeneration);
        Assert.False(ops.Reinstalled);
        Assert.Contains(CoreReloadOrchestrator.StepLoad, ops.Steps);
        Assert.True(ops.Reasked);
        Assert.True(ops.Unretired);
    }

    [Fact]
    public void Second_start_while_restored_generation_is_still_unloading_is_refused()
    {
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            CurrentAlcName = "progenitor-core:1",
            Collects = false,
        };
        var first = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.True(first.Refused);
        Assert.False(ops.Reinstalled);
        Assert.Equal(1, ops.LoadedGeneration);

        var again = new FakeReloadOps
        {
            CurrentGeneration = 1,
            CurrentAlcName = "progenitor-core:1",
            PreviousGenerationAlive = true,
        };
        var second = CoreReloadOrchestrator.Run(again, "retry");
        Assert.True(second.Refused);
        Assert.True(second.OldGenerationRunning);
        Assert.Contains("restart the process", second.Summary);
        Assert.Empty(again.Steps);
        Assert.Equal(0, again.LoadedGeneration);
    }

    [Fact]
    public void DryRun_that_does_not_collect_restores_without_claiming_ok_swap()
    {
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            CurrentAlcName = "progenitor-core:1",
            Collects = false,
            DryRun = true,
        };
        var result = CoreReloadOrchestrator.Run(ops, "bridge core_reload");
        Assert.True(result.Ok);
        Assert.False(result.Refused);
        Assert.True(result.OldGenerationRunning);
        Assert.False(ops.Reinstalled);
        Assert.Equal(1, ops.LoadedGeneration);
        Assert.Contains(CoreReloadOrchestrator.StepLoad, ops.Steps);
        Assert.True(ops.Reasked);
        Assert.Equal("dryRun gen=1 collected=False restored=True", result.Summary);
    }

    [Fact]
    public void DryRun_that_collects_reinstalls_the_same_generation()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, Collects = true, DryRun = true };
        var result = CoreReloadOrchestrator.Run(ops, "{\"dryRun\":true}");
        Assert.True(result.Ok);
        Assert.False(result.Refused);
        Assert.False(result.OldGenerationRunning);
        Assert.Equal(1, result.Generation);
        Assert.Equal(1, ops.LoadedGeneration);
        Assert.False(ops.Reinstalled);
        Assert.Contains(CoreReloadOrchestrator.StepLoad, ops.Steps);
        Assert.Equal("dryRun gen=1 collected=True restored=True", result.Summary);
        Assert.True(ops.Unretired);
        Assert.True(ops.Reasked);
    }

    [Fact]
    public void ParseDryRun_reason_token_selects_dry_run_without_the_flag()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, Collects = false };
        var result = CoreReloadOrchestrator.Run(ops, "dryRun");
        Assert.True(result.Ok);
        Assert.Equal("dryRun gen=1 collected=False restored=True", result.Summary);
    }

    [Fact]
    public void Snapshot_failure_unfreezes_and_leaves_the_old_generation_running()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, SnapshotOk = false };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.False(result.Ok);
        Assert.True(result.OldGenerationRunning);
        Assert.Equal(CoreReloadOrchestrator.StepSnapshot, result.Step);
        Assert.Contains(CoreReloadOrchestrator.StepUnfreeze, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepReloadPlugins, ops.Steps);
        Assert.True(
            ops.Steps.IndexOf(CoreReloadOrchestrator.StepUnfreeze)
            < ops.Steps.IndexOf(CoreReloadOrchestrator.StepReloadPlugins));
        Assert.DoesNotContain(CoreReloadOrchestrator.StepUninstall, ops.Steps);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepLoad, ops.Steps);
    }

    [Fact]
    public void Plugin_ALC_that_does_not_collect_restores_without_uninstalling_Core()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1 };
        ops.AlivePluginAlcs.Add("progenitor-reload:Sample:1");
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.True(result.Refused);
        Assert.False(result.Ok);
        Assert.True(result.OldGenerationRunning);
        Assert.Equal(1, result.Generation);
        Assert.Equal(
            "refused: plugin ALC progenitor-reload:Sample:1 still alive; restored plugins=1",
            result.Summary);
        Assert.Contains(CoreReloadOrchestrator.StepUnloadPlugins, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepUnfreeze, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepReloadPlugins, ops.Steps);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepUninstall, ops.Steps);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepUnloadAlc, ops.Steps);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepLoad, ops.Steps);
        Assert.Equal(0, ops.LoadedGeneration);
        Assert.True(ops.LatchedPluginAlcsAlive);
        Assert.Equal(new[] { "progenitor-reload:Sample:1" }, ops.LatchedPluginAlcNameList);

        int stepsAfterFirst = ops.Steps.Count;
        var again = CoreReloadOrchestrator.Run(ops, "retry");
        Assert.True(again.Refused);
        Assert.Contains("restart the process", again.Summary);
        Assert.Equal(
            "Core reload refused: plugin ALC progenitor-reload:Sample:1 still alive; restart the process.",
            again.Summary);
        Assert.Equal(stepsAfterFirst, ops.Steps.Count);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepUninstall, ops.Steps);
    }

    [Fact]
    public void Plugin_ALC_wait_miss_reports_truthful_plugin_reload_count()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, ReloadPluginCount = 0 };
        ops.AlivePluginAlcs.Add("progenitor-reload:Sample:1");
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.True(result.Refused);
        Assert.Contains(CoreReloadOrchestrator.StepReloadPlugins, ops.Steps);
        Assert.Equal(
            "refused: plugin ALC progenitor-reload:Sample:1 still alive; restored plugins=0",
            result.Summary);
    }

    [Fact]
    public void Third_party_collectible_does_not_block_a_Progenitor_reload()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, ReloadPluginCount = 2, Collects = true };
        ops.AlivePluginAlcs.Add("Someone.Collectible");
        ops.AlivePluginAlcs.Add("");
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.True(result.Ok);
        Assert.Equal(2, result.Generation);
        Assert.Contains(CoreReloadOrchestrator.StepUninstall, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepLoad, ops.Steps);
        Assert.False(ops.LatchedPluginAlcsAlive);
        Assert.False(CoreReloadPolicy.IsWaitedCollectibleAlc("Someone.Collectible"));
        Assert.False(CoreReloadPolicy.IsWaitedCollectibleAlc(""));
    }

    [Fact]
    public void Plugin_ALC_wait_miss_reloads_plugins_exactly_once()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, ReloadPluginCount = 1 };
        ops.AlivePluginAlcs.Add("progenitor-reload:Sample:1");
        ops.AlivePluginAlcs.Add("Someone.Collectible");
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.True(result.Refused);
        Assert.Equal(
            "refused: plugin ALC progenitor-reload:Sample:1 still alive; restored plugins=1",
            result.Summary);
        int reloads = 0;
        for (int i = 0; i < ops.Steps.Count; i++)
        {
            if (ops.Steps[i] == CoreReloadOrchestrator.StepReloadPlugins) reloads++;
        }
        Assert.Equal(1, reloads);
        Assert.Contains(CoreReloadOrchestrator.StepUnfreeze, ops.Steps);
        Assert.True(
            ops.Steps.IndexOf(CoreReloadOrchestrator.StepUnfreeze)
            < ops.Steps.IndexOf(CoreReloadOrchestrator.StepReloadPlugins));
        Assert.DoesNotContain(CoreReloadOrchestrator.StepUninstall, ops.Steps);
    }

    [Fact]
    public void Empty_last_good_refuses_rather_than_replaying_failed_NPlus1_bytes()
    {
        var bad = new byte[] { 9, 9, 9 };
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            CurrentAlcName = "progenitor-core:1",
            Bytes = bad,
            LastGoodBytes = Array.Empty<byte>(),
            FailLoadGeneration = 2,
        };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.False(result.Ok);
        Assert.False(result.OldGenerationRunning);
        Assert.Contains("CORE-LESS", result.Summary);
        Assert.Contains("last-good bytes empty", result.Summary);
        Assert.Equal(1, ops.LoadAttempts);
        Assert.Equal(bad, ops.LastLoadedBytes);
        Assert.Equal(2, ops.LoadedGeneration);
    }

    [Fact]
    public void Empty_last_good_collect_fail_is_core_less_not_disk_fallback()
    {
        var next = new byte[] { 7, 8 };
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            Bytes = next,
            LastGoodBytes = null,
            Collects = false,
        };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.False(result.Ok);
        Assert.Contains("CORE-LESS", result.Summary);
        Assert.Contains("last-good bytes empty", result.Summary);
        Assert.Equal(0, ops.LoadAttempts);
        Assert.Null(ops.LastLoadedBytes);
    }

    [Fact]
    public void Failed_NPlus1_install_restores_last_good_bytes_not_disk()
    {
        var good = new byte[] { 1, 2, 3 };
        var bad = new byte[] { 9, 9, 9 };
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            CurrentAlcName = "progenitor-core:1",
            Bytes = bad,
            LastGoodBytes = good,
            FailLoadGeneration = 2,
        };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.False(result.Refused);
        Assert.Equal(1, result.Generation);
        Assert.Equal(1, ops.LoadedGeneration);
        Assert.Equal(2, ops.LoadAttempts);
        Assert.Equal(good, ops.LastLoadedBytes);
        Assert.Equal(
            "install of generation 2 failed (load boom); generation 1 restored; no restart needed",
            result.Summary);
        Assert.Equal(CoreReloadStatus.OutcomeRestored, result.Outcome);
    }

    [Fact]
    public void Collect_fail_restores_last_good_bytes()
    {
        var good = new byte[] { 4, 5 };
        var next = new byte[] { 7, 8 };
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            Bytes = next,
            LastGoodBytes = good,
            Collects = false,
        };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.True(result.Refused);
        Assert.Equal(1, ops.LoadedGeneration);
        Assert.Equal(good, ops.LastLoadedBytes);
        Assert.NotEqual(next, ops.LastLoadedBytes);
    }

    [Fact]
    public void Three_consecutive_swaps_increment_generation()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, UnloadPluginCount = 2, ReloadPluginCount = 2 };
        var r2 = CoreReloadOrchestrator.Run(ops, "a");
        Assert.True(r2.Ok);
        Assert.Equal(2, r2.Generation);
        Assert.Equal("ok gen=2 collected=True pluginsUnloaded=2 pluginsReloaded=2 reason=a", r2.Summary);

        ops.CurrentGeneration = 2;
        var r3 = CoreReloadOrchestrator.Run(ops, "b");
        Assert.True(r3.Ok);
        Assert.Equal(3, r3.Generation);
        Assert.Equal("ok gen=3 collected=True pluginsUnloaded=2 pluginsReloaded=2 reason=b", r3.Summary);

        ops.CurrentGeneration = 3;
        var r4 = CoreReloadOrchestrator.Run(ops, "c");
        Assert.True(r4.Ok);
        Assert.Equal(4, r4.Generation);
        Assert.Equal("ok gen=4 collected=True pluginsUnloaded=2 pluginsReloaded=2 reason=c", r4.Summary);
    }
}

public class CoreReloadFrameDriverTests
{
    [Fact]
    public void Request_returns_scheduled_and_does_not_run_the_cascade()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1 };
        var driver = new CoreReloadFrameDriver();
        Assert.Equal("scheduled gen=1", driver.Request(1, "progenitor-core:1", previousGenerationAlive: false, "bridge"));
        Assert.True(driver.IsBusy);
        Assert.True(driver.IsScheduled);
        Assert.False(driver.IsCollecting);
        Assert.Empty(ops.Steps);
    }

    [Fact]
    public void Request_dryRun_appends_the_token()
    {
        var driver = new CoreReloadFrameDriver();
        Assert.Equal("scheduled gen=1 dryRun", driver.Request(1, "progenitor-core:1", false, "dryRun"));
    }

    [Fact]
    public void Request_refuses_when_busy_or_previous_generation_alive()
    {
        var driver = new CoreReloadFrameDriver();
        driver.Request(1, "progenitor-core:1", false, "bridge");
        Assert.Equal(CoreReloadPolicy.RefuseAlreadyReloading, driver.Request(1, "progenitor-core:1", false, "again"));
        var other = new CoreReloadFrameDriver();
        Assert.Contains("still alive", other.Request(1, "progenitor-core:1", previousGenerationAlive: true, "bridge"));
        Assert.False(other.IsBusy);
    }

    [Fact]
    public void Tick_yields_unity_teardown_before_UnloadAlc_then_collect()
    {
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            YieldUnityFlush = true,
            YieldCollect = true,
            CurrentFrame = 10,
        };
        var driver = new CoreReloadFrameDriver();
        Assert.Equal("scheduled gen=1", driver.Request(1, "progenitor-core:1", false, "bridge"));
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Empty(ops.Steps);

        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.True(driver.IsWaitingUnityFlush);
        Assert.Contains(CoreReloadOrchestrator.StepUninstall, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepDropWork, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepNullRuntime, ops.Steps);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepUnloadAlc, ops.Steps);
        Assert.Contains(CoreReloadPolicy.StepLog(2, CoreReloadPolicy.FormatUnityTeardown(10)), ops.Logs);
        int teardown = ops.Steps.IndexOf(CoreReloadOrchestrator.StepNullRuntime);
        Assert.True(teardown > ops.Steps.IndexOf(CoreReloadOrchestrator.StepUninstall));
        Assert.True(teardown > ops.Steps.IndexOf(CoreReloadOrchestrator.StepDropWork));

        ops.CurrentFrame = 11;
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.False(driver.IsWaitingUnityFlush);
        Assert.True(driver.IsCollecting);
        Assert.Contains(CoreReloadOrchestrator.StepUnloadAlc, ops.Steps);
        Assert.Contains(CoreReloadPolicy.StepLog(2, CoreReloadPolicy.FormatUnloadAlcFrame(11)), ops.Logs);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepLoad, ops.Steps);

        ops.CurrentFrame = 12;
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Contains(CoreReloadPolicy.StepLog(2, CoreReloadPolicy.FormatCollectFrame(12)), ops.Logs);
        Assert.True(ops.Pulses >= 1);

        var result = driver.Tick(ops, alcAlive: false);
        Assert.True(result.HasValue);
        Assert.True(result.Value.Ok);
        Assert.Equal(2, result.Value.Generation);
        Assert.Contains(CoreReloadOrchestrator.StepLoad, ops.Steps);
    }

    [Fact]
    public void Tick_yields_after_UnloadAlc_then_loads_when_the_ALC_dies()
    {
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            YieldCollect = true,
            DryRun = true,
        };
        var driver = new CoreReloadFrameDriver();
        Assert.Equal("scheduled gen=1 dryRun", driver.Request(1, "progenitor-core:1", false, "dryRun"));
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Empty(ops.Steps);
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Contains(CoreReloadOrchestrator.StepUnloadAlc, ops.Steps);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepRestore, ops.Steps);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepLoad, ops.Steps);
        Assert.True(driver.IsCollecting);
        Assert.Null(driver.Tick(ops, alcAlive: true));
        var result = driver.Tick(ops, alcAlive: false);
        Assert.True(result.HasValue);
        Assert.True(result.Value.Ok);
        Assert.Equal("dryRun gen=1 collected=True restored=True", result.Value.Summary);
        Assert.Contains(CoreReloadOrchestrator.StepLoad, ops.Steps);
        Assert.Equal(1, ops.LoadedGeneration);
        Assert.False(driver.IsBusy);
    }

    [Fact]
    public void Tick_pulses_collect_then_wait_on_each_Core_ALC_poll_frame()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, YieldCollect = true };
        var driver = new CoreReloadFrameDriver();
        driver.Request(1, "progenitor-core:1", false, "bridge");
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Equal(0, ops.Pulses);
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Equal(1, ops.Pulses);
        int collect = ops.Steps.IndexOf(CoreReloadPolicy.PulseCollectStep);
        int wait = ops.Steps.IndexOf(CoreReloadPolicy.PulseWaitStep);
        Assert.True(collect >= 0);
        Assert.Equal(collect + 1, wait);
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Equal(2, ops.Pulses);
        var result = driver.Tick(ops, alcAlive: false);
        Assert.True(result.HasValue);
        Assert.Equal(3, ops.Pulses);
    }

    [Fact]
    public void Tick_caps_core_collect_pulses_after_the_first_10_frames()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, YieldCollect = true };
        var driver = new CoreReloadFrameDriver();
        driver.Request(1, "progenitor-core:1", false, "bridge");
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Equal(0, ops.Pulses);
        for (int i = 0; i < 20; i++)
            Assert.Null(driver.Tick(ops, alcAlive: true));
        // First 10 collect frames pulse every tick; then 10 and 15 (stride 5).
        Assert.Equal(12, ops.Pulses);
        int expected = 0;
        for (int f = 0; f < 20; f++)
        {
            if (CoreReloadPolicy.ShouldPulseCoreCollect(f)) expected++;
        }
        Assert.Equal(expected, ops.Pulses);
    }

    [Fact]
    public void Tick_samples_the_weak_after_the_pulse_including_frame_60()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, YieldCollect = true };
        var driver = new CoreReloadFrameDriver();
        driver.Request(1, "progenitor-core:1", false, "bridge");
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Null(driver.Tick(ops, alcAlive: true));

        bool alive = true;
        ops.OnPulse = () => alive = false;
        // Frozen bool is the R18 bug: pulse can collect but the argument stays true.
        Assert.Null(driver.Tick(ops, alcAlive: true));
        alive = true;
        var afterPulse = driver.Tick(ops, () => alive);
        Assert.True(afterPulse.HasValue);
        Assert.True(afterPulse.Value.Ok);
        Assert.Equal(true, afterPulse.Value.Collected);
        Assert.Contains("ok gen=", afterPulse.Value.Summary);
    }

    [Fact]
    public void Tick_backs_off_pulses_after_two_slow_hitches()
    {
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            YieldCollect = true,
            PulseMilliseconds = CoreReloadPolicy.HitchBackoffMs + 1,
        };
        var driver = new CoreReloadFrameDriver();
        driver.Request(1, "progenitor-core:1", false, "bridge");
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Null(driver.Tick(ops, alcAlive: true));

        for (int i = 0; i < 12; i++)
            Assert.Null(driver.Tick(ops, alcAlive: true));

        // Hitch backoff must not skip the first 10 every-frame pulses.
        // Frames 0..9 pulse regardless; frame 10 is the first backoff stride.
        Assert.Equal(11, ops.Pulses);
        Assert.Contains(CoreReloadPolicy.FormatCollectHitchBackoff(), ops.Logs);
    }

    [Fact]
    public void Tick_restores_when_the_frame_budget_expires()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, YieldCollect = true };
        var driver = new CoreReloadFrameDriver();
        driver.Request(1, "progenitor-core:1", false, "bridge");
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Null(driver.Tick(ops, alcAlive: true));
        CoreReloadResult? result = null;
        for (int i = 0; i < CoreReloadPolicy.CollectWaitFrames; i++)
        {
            result = driver.Tick(ops, alcAlive: true);
            if (result != null) break;
        }
        Assert.True(result.HasValue);
        Assert.True(result.Value.Refused);
        Assert.Equal("refused: generation 1 still alive; restored; restart the process", result.Value.Summary);
        Assert.False(ops.Reinstalled);
        Assert.Equal(1, ops.LoadedGeneration);
        Assert.True(ops.Unretired);
        Assert.Contains(CoreReloadOrchestrator.StepLoad, ops.Steps);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepRestore, ops.Steps);
        Assert.True(ops.Reasked);
    }

    [Fact]
    public void Tick_real_swap_increments_generation_after_collect()
    {
        var ops = new FakeReloadOps { CurrentGeneration = 1, YieldCollect = true, UnloadPluginCount = 2, ReloadPluginCount = 2 };
        var driver = new CoreReloadFrameDriver();
        Assert.Equal("scheduled gen=1", driver.Request(1, "progenitor-core:1", false, "bridge"));
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Null(driver.Tick(ops, alcAlive: true));
        var result = driver.Tick(ops, alcAlive: false);
        Assert.True(result.HasValue);
        Assert.True(result.Value.Ok);
        Assert.Equal(2, result.Value.Generation);
        Assert.Equal(
            "ok gen=2 collected=True pluginsUnloaded=2 pluginsReloaded=2 reason=bridge",
            result.Value.Summary);
    }

    [Fact]
    public void Tick_waits_for_plugin_ALCs_before_unloading_Core()
    {
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            YieldPluginAlcWait = true,
            YieldCollect = true,
            UnloadPluginCount = 2,
            ReloadPluginCount = 2,
        };
        ops.AlivePluginAlcs.Add("progenitor-reload:Sample:1");
        var driver = new CoreReloadFrameDriver();
        Assert.Equal("scheduled gen=1", driver.Request(1, "progenitor-core:1", false, "bridge"));
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.True(driver.IsWaitingPluginAlcs);
        Assert.Contains(CoreReloadOrchestrator.StepUnloadPlugins, ops.Steps);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepUninstall, ops.Steps);
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.True(driver.IsWaitingPluginAlcs);
        ops.AlivePluginAlcs.Clear();
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.False(driver.IsWaitingPluginAlcs);
        Assert.Contains(CoreReloadOrchestrator.StepUninstall, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepUnloadAlc, ops.Steps);
        var result = driver.Tick(ops, alcAlive: false);
        Assert.True(result.HasValue);
        Assert.True(result.Value.Ok);
        Assert.Equal(2, result.Value.Generation);
        Assert.Equal(
            "ok gen=2 collected=True pluginsUnloaded=2 pluginsReloaded=2 reason=bridge",
            result.Value.Summary);
    }

    [Fact]
    public void Tick_refuses_when_plugin_ALC_wait_expires()
    {
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            YieldPluginAlcWait = true,
        };
        ops.AlivePluginAlcs.Add("progenitor-reload:Dev:1");
        var driver = new CoreReloadFrameDriver();
        driver.Request(1, "progenitor-core:1", false, "bridge");
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Null(driver.Tick(ops, alcAlive: true));
        CoreReloadResult? result = null;
        for (int i = 0; i < CoreReloadPolicy.PluginAlcWaitFrames; i++)
        {
            result = driver.Tick(ops, alcAlive: true);
            if (result != null) break;
        }
        Assert.True(result.HasValue);
        Assert.True(result.Value.Refused);
        Assert.Equal(
            "refused: plugin ALC progenitor-reload:Dev:1 still alive; restored plugins=1",
            result.Value.Summary);
        Assert.DoesNotContain(CoreReloadOrchestrator.StepUninstall, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepReloadPlugins, ops.Steps);
        Assert.False(driver.IsBusy);
    }

    [Fact]
    public void Collect_step_holds_no_strong_reference_to_the_old_runtime()
    {
        var driver = new CoreReloadFrameDriver();
        var ops = new FakeReloadOps { CurrentGeneration = 1, YieldCollect = true };
        BeginCollect(driver, ops, out var weakRuntime, out var weakAlc);

        Assert.True(driver.IsCollecting);
        Assert.Null(ops.OldRuntime);
        Assert.Null(ops.OldAlc);
        Assert.False(ops.HasPublishedAssembly);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weakRuntime.IsAlive);
        Assert.False(weakAlc.IsAlive);
        Assert.True(driver.IsCollecting);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BeginCollect(
        CoreReloadFrameDriver driver,
        FakeReloadOps ops,
        out WeakReference weakRuntime,
        out WeakReference weakAlc)
    {
        var runtime = new object();
        var alc = new AssemblyLoadContext("progenitor-core:test-collect", isCollectible: true);
        weakRuntime = new WeakReference(runtime);
        weakAlc = new WeakReference(alc);
        ops.OldRuntime = runtime;
        ops.OldAlc = alc;
        driver.Request(1, "progenitor-core:1", previousGenerationAlive: false, "bridge");
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Null(driver.Tick(ops, alcAlive: true));
    }

    [Fact]
    public void Second_swap_collect_holds_no_strong_reference_to_generation_2()
    {
        var driver = new CoreReloadFrameDriver();
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            YieldCollect = true,
            UnloadPluginCount = 2,
            ReloadPluginCount = 2,
        };
        BeginFirstSwap(driver, ops);
        BeginSecondCollect(driver, ops, out var weakRuntime, out var weakAlc, out var weakSlot);

        Assert.True(driver.IsCollecting);
        Assert.False(ops.HasPublishedAssembly);
        Assert.Equal(0, ops.CurrentGeneration);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(weakRuntime.IsAlive);
        Assert.False(weakAlc.IsAlive);
        Assert.False(weakSlot.IsAlive);
        Assert.True(driver.IsCollecting);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BeginFirstSwap(CoreReloadFrameDriver driver, FakeReloadOps ops)
    {
        driver.Request(1, "progenitor-core:1", previousGenerationAlive: false, "first");
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Null(driver.Tick(ops, alcAlive: true));
        var result = driver.Tick(ops, alcAlive: false);
        Assert.True(result.HasValue);
        Assert.True(result.Value.Ok);
        Assert.Equal(2, result.Value.Generation);
        Assert.Equal(2, ops.CurrentGeneration);
        Assert.NotNull(ops.PublishedRuntime);
        Assert.NotNull(ops.SlotForward);
        Assert.Equal("progenitor-core:2", ops.PinGeneration);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void BeginSecondCollect(
        CoreReloadFrameDriver driver,
        FakeReloadOps ops,
        out WeakReference weakRuntime,
        out WeakReference weakAlc,
        out WeakReference weakSlot)
    {
        object runtime = ops.PublishedRuntime ?? throw new InvalidOperationException("generation 2 was not published");
        var alc = ops.PublishedAlc ?? throw new InvalidOperationException("generation 2 ALC missing");
        object slot = ops.SlotForward ?? runtime;
        weakRuntime = new WeakReference(runtime);
        weakAlc = new WeakReference(alc);
        weakSlot = new WeakReference(slot);
        runtime = null!;
        slot = null!;
        alc = null;
        driver.Request(2, "progenitor-core:2", previousGenerationAlive: false, "second");
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Null(driver.Tick(ops, alcAlive: true));
        Assert.Null(ops.PublishedRuntime);
        Assert.Null(ops.SlotForward);
        Assert.Null(ops.PublishedAlc);
        Assert.Null(ops.PinGeneration);
    }

    [Fact]
    public void Collect_fail_then_fresh_load_fail_leaves_Core_less()
    {
        var ops = new FakeReloadOps
        {
            CurrentGeneration = 1,
            Collects = false,
            LoadOk = false,
            LoadError = "install boom",
        };
        var result = CoreReloadOrchestrator.Run(ops, "bridge");
        Assert.False(result.Ok);
        Assert.False(result.Refused);
        Assert.False(result.OldGenerationRunning);
        Assert.False(result.Collected);
        Assert.Equal(CoreReloadOrchestrator.StepLoad, result.Step);
        Assert.False(ops.Reinstalled);
        Assert.Contains("oldRunning=False", result.Summary);
        Assert.Contains(CoreReloadOrchestrator.StepUnfreeze, ops.Steps);
        Assert.True(ops.ClearedSlots);
        Assert.Equal(new[] { "progenitor-core:1" }, ops.ReleasedGenerations);
        Assert.Contains(CoreReloadOrchestrator.StepClearSlots, ops.Steps);
        Assert.Contains(CoreReloadOrchestrator.StepReleaseFailed, ops.Steps);
        Assert.True(
            ops.Steps.IndexOf(CoreReloadOrchestrator.StepLoad)
            < ops.Steps.IndexOf(CoreReloadOrchestrator.StepClearSlots));
        Assert.True(
            ops.Steps.IndexOf(CoreReloadOrchestrator.StepClearSlots)
            < ops.Steps.IndexOf(CoreReloadOrchestrator.StepReleaseFailed));
    }
}

public class CoreReloadStackCheckTests
{
    [Fact]
    public void Log_marks_this_default_ALC_stack_clean()
    {
        var lines = new List<string>();
        CoreReloadStackCheck.Log(coreAssembly: null, coreAlcName: null, log: lines.Add);
        Assert.Contains(lines, l => l.StartsWith("pin-diag stack ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("dirty=False", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("  stack ", StringComparison.Ordinal));
    }

    [Fact]
    public void IsPluginAlc_matches_reloader_context_names()
    {
        Assert.True(CoreReloadStackCheck.IsPluginAlc("progenitor-reload:Sample:1"));
        Assert.False(CoreReloadStackCheck.IsPluginAlc("progenitor-core:1"));
        Assert.False(CoreReloadStackCheck.IsPluginAlc(null));
    }
}

public class CoreReloadHolderScanTests
{
    private sealed class SlotBox
    {
        public AssemblyLoadContext? Context;
        public object? Inner;
    }

    private sealed class InnerBox
    {
        public AssemblyLoadContext? Context;
    }

    [Fact]
    public void Walk_reports_a_field_holding_the_unloading_ALC()
    {
        var alc = new AssemblyLoadContext("progenitor-core:scan-test", isCollectible: true);
        try
        {
            var box = new SlotBox { Context = alc };
            var lines = new List<string>();
            CoreReloadHolderScan.Walk(
                staticTypes: null,
                roots: new object[] { box },
                unloadingAssembly: null,
                unloadingAlc: alc,
                log: lines.Add);
            Assert.Contains(lines, l =>
                l.StartsWith("pin-diag holder=", StringComparison.Ordinal)
                && l.Contains("Context", StringComparison.Ordinal)
                && l.Contains("type=", StringComparison.Ordinal));
        }
        finally
        {
            try { alc.Unload(); }
            catch (InvalidOperationException) { /* already unloading */ }
        }
    }

    [Fact]
    public void Walk_reports_a_nested_field_and_skips_an_empty_graph()
    {
        var alc = new AssemblyLoadContext("progenitor-core:scan-nested", isCollectible: true);
        try
        {
            var box = new SlotBox { Inner = new InnerBox { Context = alc } };
            var hits = new List<string>();
            CoreReloadHolderScan.Walk(
                staticTypes: null,
                roots: new object[] { box },
                unloadingAssembly: null,
                unloadingAlc: alc,
                log: hits.Add);
            Assert.Contains(hits, l =>
                l.Contains("Inner", StringComparison.Ordinal)
                && l.Contains("Context", StringComparison.Ordinal));

            var empty = new List<string>();
            CoreReloadHolderScan.Walk(
                staticTypes: null,
                roots: new object[] { new SlotBox() },
                unloadingAssembly: null,
                unloadingAlc: alc,
                log: empty.Add);
            Assert.Contains("pin-diag holders=0", empty);
            Assert.Contains(empty, l => l.StartsWith("pin-diag holder-scan ms=", StringComparison.Ordinal));
        }
        finally
        {
            try { alc.Unload(); }
            catch (InvalidOperationException) { /* already unloading */ }
        }
    }

    [Fact]
    public void Wide_scan_reports_a_planted_static_holder_in_this_assembly()
    {
        var alc = new AssemblyLoadContext("progenitor-core:wide-plant", isCollectible: true);
        HolderScanPlant.Pin = alc;
        try
        {
            var lines = new List<string>();
            CoreReloadHolderScan.WalkWide(
                assemblies: new[] { typeof(HolderScanPlant).Assembly },
                extraRoots: null,
                unloadingAssembly: null,
                unloadingAlc: alc,
                log: lines.Add);
            Assert.Contains(lines, l =>
                l.StartsWith("pin-diag holder=", StringComparison.Ordinal)
                && l.Contains("HolderScanPlant", StringComparison.Ordinal)
                && l.Contains("Pin", StringComparison.Ordinal)
                && l.Contains("type=", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("pin-diag holder-scan wide ms=", StringComparison.Ordinal));
            Assert.False(CoreReloadHolderScan.IsWideScanAssembly(typeof(HolderScanPlant).Assembly));
            Assert.False(CoreReloadHolderScan.IsWideScanAssembly(typeof(object).Assembly));
        }
        finally
        {
            HolderScanPlant.Pin = null;
            try { alc.Unload(); }
            catch (InvalidOperationException) { /* already unloading */ }
        }
    }

    [Fact]
    public void Wide_scan_skips_a_type_with_a_static_constructor()
    {
        var alc = new AssemblyLoadContext("progenitor-core:cctor-skip", isCollectible: true);
        UninitCctorFlag.Hits = 0;
        try
        {
            Assert.False(CoreReloadHolderScan.ShouldReadStaticFields(typeof(UninitCctorPlant)));
            Assert.True(CoreReloadHolderScan.ShouldReadStaticFields(typeof(HolderScanPlant)));
            Assert.False(CoreReloadHolderScan.IsKnownInitializedStaticType(typeof(HolderScanPlant)));
            var lines = new List<string>();
            CoreReloadHolderScan.WalkWide(
                assemblies: new[] { typeof(UninitCctorPlant).Assembly },
                extraRoots: null,
                unloadingAssembly: null,
                unloadingAlc: alc,
                log: lines.Add);
            Assert.Equal(0, UninitCctorFlag.Hits);
            Assert.DoesNotContain(lines, l =>
                l.Contains("UninitCctorPlant", StringComparison.Ordinal)
                && l.StartsWith("pin-diag holder=", StringComparison.Ordinal));
        }
        finally
        {
            try { alc.Unload(); }
            catch (InvalidOperationException) { /* already unloading */ }
        }
    }

    [Fact]
    public void Wide_scan_does_not_report_the_unloading_assembly_from_AppDomain()
    {
        var alc = new AssemblyLoadContext("progenitor-core:listing", isCollectible: true);
        try
        {
            var listing = new List<string>();
            CoreReloadHolderScan.Walk(
                staticTypes: null,
                roots: new object[] { AppDomain.CurrentDomain },
                unloadingAssembly: null,
                unloadingAlc: alc,
                log: listing.Add);
            Assert.DoesNotContain(listing, l => l.StartsWith("pin-diag holder=", StringComparison.Ordinal));
            Assert.True(CoreReloadHolderScan.IsLoadedContextListing(AppDomain.CurrentDomain.GetAssemblies()));
            Assert.True(CoreReloadHolderScan.IsLoadedContextListing(new[] { alc }));

            var box = new SlotBox { Context = alc };
            var holders = new List<string>();
            CoreReloadHolderScan.Walk(
                staticTypes: null,
                roots: new object[] { box },
                unloadingAssembly: null,
                unloadingAlc: alc,
                log: holders.Add);
            Assert.Contains(holders, l =>
                l.StartsWith("pin-diag holder=", StringComparison.Ordinal)
                && l.Contains("Context", StringComparison.Ordinal));
        }
        finally
        {
            try { alc.Unload(); }
            catch (InvalidOperationException) { /* already unloading */ }
        }
    }

    [Fact]
    public void Walk_records_Il2CppObjectBase_type_and_pointer_without_dereferencing()
    {
        var wrapper = new Il2CppObjectBase
        {
            Pointer = new IntPtr(0xabc),
            Child = new SlotBox { Context = null },
        };
        Assert.True(CoreReloadHolderScan.IsIl2CppObjectBaseType(typeof(Il2CppObjectBase)));
        Assert.False(CoreReloadHolderScan.ShouldEnumerate(wrapper));
        var lines = new List<string>();
        CoreReloadHolderScan.Walk(
            staticTypes: null,
            roots: new object[] { wrapper },
            unloadingAssembly: typeof(Il2CppObjectBase).Assembly,
            unloadingAlc: null,
            log: lines.Add);
        Assert.Contains(lines, l =>
            l.StartsWith("pin-diag holder=", StringComparison.Ordinal)
            && l.Contains("ptr=0xabc", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("Child", StringComparison.Ordinal));
    }

    [Fact]
    public void Walk_stops_at_zero_budget_and_logs_truncated()
    {
        var alc = new AssemblyLoadContext("progenitor-core:budget", isCollectible: true);
        HolderScanPlant.Pin = alc;
        try
        {
            var lines = new List<string>();
            CoreReloadHolderScan.Walk(
                staticTypes: null,
                roots: null,
                unloadingAssembly: null,
                unloadingAlc: alc,
                log: lines.Add,
                options: new CoreReloadHolderScan.Options
                {
                    Wide = true,
                    MaxDepth = CoreReloadHolderScan.WideMaxDepth,
                    MaxHits = CoreReloadHolderScan.WideMaxHits,
                    BudgetMilliseconds = 0,
                    Assemblies = new[] { typeof(HolderScanPlant).Assembly },
                });
            Assert.Contains(lines, l => l.StartsWith("pin-diag holder-scan truncated ms=", StringComparison.Ordinal));
        }
        finally
        {
            HolderScanPlant.Pin = null;
            try { alc.Unload(); }
            catch (InvalidOperationException) { /* already unloading */ }
        }
    }
}

/// <summary>Flag the UninitCctorPlant cctor increments. Touched from the test without running that cctor.</summary>
public static class UninitCctorFlag
{
    public static int Hits;
}

/// <summary>Type with a cctor so the wide scan must not GetValue its statics.</summary>
public static class UninitCctorPlant
{
    static UninitCctorPlant() => UninitCctorFlag.Hits++;
    public static object? Pin;
}

/// <summary>Name-matches Il2CppObjectBase so the walker records pointer and does not walk Child.</summary>
public sealed class Il2CppObjectBase
{
    public IntPtr Pointer { get; set; }
    public object? Child;
}

/// <summary>Planted static for <see cref="CoreReloadHolderScanTests.Wide_scan_reports_a_planted_static_holder_in_this_assembly"/>.</summary>
public static class HolderScanPlant
{
    public static object? Pin;
}

public class ContentMemoryTests
{
    [Fact]
    public void Replace_and_Take_round_trip_cloned_appended_and_tombs()
    {
        var mem = new ContentMemory();
        Assert.Equal(0, mem.ClonedCount);
        mem.Replace(
            new[] { 802060347, 1 },
            new[] { 802060347 },
            new[] { 802060347 },
            new[] { "sample.ember" });
        Assert.Equal(2, mem.ClonedCount);
        Assert.Equal(1, mem.AppendedCount);
        Assert.Equal(1, mem.TombstoneCount);

        var snap = mem.Take();
        Assert.Equal(new[] { 802060347, 1 }, snap.ClonedGuids);
        Assert.Equal(new[] { 802060347 }, snap.AppendedGuids);
        Assert.Equal(new[] { 802060347 }, snap.TombstoneHashes);
        Assert.Equal(new[] { "sample.ember" }, snap.TombstoneIds);

        snap.ClonedGuids[0] = 0;
        Assert.Equal(802060347, mem.Take().ClonedGuids[0]);
    }

    [Fact]
    public void Replace_rejects_mismatched_tombstone_arrays()
    {
        var mem = new ContentMemory();
        Assert.Throws<ArgumentException>(() =>
            mem.Replace(Array.Empty<int>(), Array.Empty<int>(), new[] { 1 }, Array.Empty<string>()));
    }

    [Fact]
    public void Null_arrays_become_empty()
    {
        var mem = new ContentMemory();
        mem.Replace(null, null, null, null);
        var snap = mem.Take();
        Assert.Empty(snap.ClonedGuids);
        Assert.Empty(snap.AppendedGuids);
        Assert.Empty(snap.TombstoneHashes);
        Assert.Empty(snap.TombstoneIds);
        Assert.Empty(snap.AppendedIndices);
        Assert.Empty(snap.TombstoneIndices);
    }

    [Fact]
    public void Replace_stores_appended_and_tombstone_indices()
    {
        var mem = new ContentMemory();
        mem.Replace(
            clonedGuids: new[] { 802060347 },
            appendedGuids: new[] { 802060347 },
            appendedIndices: new[] { 1083 },
            tombstoneHashes: new[] { 1 },
            tombstoneIds: new[] { "gone" },
            tombstoneIndices: new[] { 12 });
        var snap = mem.Take();
        Assert.Equal(new[] { 1083 }, snap.AppendedIndices);
        Assert.Equal(new[] { 12 }, snap.TombstoneIndices);
        snap.AppendedIndices[0] = 0;
        Assert.Equal(1083, mem.Take().AppendedIndices[0]);
    }

    [Fact]
    public void Missing_indices_become_unknown_minus_one()
    {
        var mem = new ContentMemory();
        mem.Replace(new[] { 1 }, new[] { 2, 3 }, new[] { 4 }, new[] { "t" });
        var snap = mem.Take();
        Assert.Equal(new[] { -1, -1 }, snap.AppendedIndices);
        Assert.Equal(new[] { -1 }, snap.TombstoneIndices);
    }

    [Fact]
    public void Replace_rejects_mismatched_index_arrays()
    {
        var mem = new ContentMemory();
        Assert.Throws<ArgumentException>(() =>
            mem.Replace(
                clonedGuids: Array.Empty<int>(),
                appendedGuids: new[] { 1 },
                appendedIndices: new[] { 1, 2 },
                tombstoneHashes: Array.Empty<int>(),
                tombstoneIds: Array.Empty<string>(),
                tombstoneIndices: Array.Empty<int>()));
    }

    [Fact]
    public void KeyRowStillHolds_requires_the_saved_index()
    {
        Assert.True(ContentMemory.KeyRowStillHolds(savedIndex: 1083, currentIndex: 1083));
        Assert.False(ContentMemory.KeyRowStillHolds(savedIndex: 1083, currentIndex: 659));
        Assert.False(ContentMemory.KeyRowStillHolds(savedIndex: 1083, currentIndex: -1));
        Assert.False(ContentMemory.KeyRowStillHolds(savedIndex: -1, currentIndex: 0));
    }

    [Fact]
    public void Snapshot_fields_are_primitive_arrays_not_Core_types()
    {
        foreach (var p in typeof(ContentSnapshot).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var t = p.PropertyType;
            Assert.True(
                t == typeof(int[]) || t == typeof(string[]),
                p.Name + " is " + t.FullName + " and would pin a collectible Core generation");
        }
    }
}
