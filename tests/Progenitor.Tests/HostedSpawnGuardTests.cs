using System;
using System.IO;
using Progenitor.Hosted;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of hosted spawn guards (review R5 findings 5 and 9): Doorstop nest
/// accounting around nested <c>StunProcess.Start</c>, and the child-log probe that must not
/// treat a leftover <c>LogOutput.log</c> as this spawn. Also the deferred-Uninstall
/// completion-hook route (R11 finding 4): Bootstrap lifetime slots vs ConvertDelegate
/// fallback. Linked through <c>HostedServerLauncher.cs</c>. Live-server refusals stay in
/// <see cref="HostedPathRulesTests"/>.
/// </summary>
public class HostedSpawnGuardTests
{
    private const string GameRoot = @"C:\FakeSteam\steamapps\common\VRising";
    private const string BundledServer = GameRoot + @"\VRising_Server";
    private const string BundledLog = BundledServer + @"\BepInEx\LogOutput.log";

    // ---- DoorstopStripNest (R5 finding 5) -----------------------------------------------------

    [Fact]
    public void Single_Start_enters_then_leave_restores()
    {
        var nest = new HostedServerLauncher.DoorstopStripNest();
        Assert.Equal(0, nest.Depth);
        Assert.True(nest.Enter());
        Assert.Equal(1, nest.Depth);
        Assert.True(nest.Leave());
        Assert.Equal(0, nest.Depth);
    }

    [Fact]
    public void Nested_Starts_share_the_outer_snapshot_until_outer_leave()
    {
        var nest = new HostedServerLauncher.DoorstopStripNest();
        Assert.True(nest.Enter());
        Assert.False(nest.Enter());
        Assert.Equal(2, nest.Depth);
        // Inner finalizer: decrement, do not restore (outer CreateProcessW has not run).
        Assert.False(nest.Leave());
        Assert.Equal(1, nest.Depth);
        Assert.True(nest.Leave());
        Assert.Equal(0, nest.Depth);
    }

    [Fact]
    public void Postfix_plus_finalizer_cannot_restore_twice()
    {
        // The R5 bug: both postfix and finalizer decremented. After one Enter, the first Leave
        // restores; the second must be a no-op so a nested outer Start cannot see Depth 0
        // and put DOORSTOP_* back before CreateProcessW.
        var nest = new HostedServerLauncher.DoorstopStripNest();
        Assert.True(nest.Enter());
        Assert.True(nest.Leave());
        Assert.False(nest.Leave());
        Assert.Equal(0, nest.Depth);
    }

    [Fact]
    public void Two_leaves_on_the_inner_start_steal_the_outer_restore()
    {
        // The helper decrements once per Leave. Harmony postfix+finalizer on the inner
        // Start used to Leave twice, hitting Depth 0 before the outer CreateProcessW
        // (R5 finding 5). The patch must Leave only from the finalizer (__state).
        var nest = new HostedServerLauncher.DoorstopStripNest();
        Assert.True(nest.Enter());
        Assert.False(nest.Enter());
        Assert.False(nest.Leave()); // inner postfix
        Assert.True(nest.Leave());  // inner finalizer restores — too early
        Assert.Equal(0, nest.Depth);
        Assert.False(nest.Leave()); // outer finalizer is now a no-op
    }

    [Fact]
    public void Non_hosted_Start_does_not_leave_because_it_did_not_enter()
    {
        var nest = new HostedServerLauncher.DoorstopStripNest();
        Assert.True(nest.Enter());
        // A StunProcess.Start of some other exe: prefix did not Enter, __state is false,
        // finalizer must not Leave.
        Assert.Equal(1, nest.Depth);
        Assert.True(nest.Leave());
        Assert.Equal(0, nest.Depth);
    }

    [Fact]
    public void Leave_without_enter_is_a_noop()
    {
        var nest = new HostedServerLauncher.DoorstopStripNest();
        Assert.False(nest.Leave());
        Assert.Equal(0, nest.Depth);
    }

    [Fact]
    public void Reset_forgets_in_flight_starts()
    {
        var nest = new HostedServerLauncher.DoorstopStripNest();
        nest.Enter();
        nest.Enter();
        nest.Reset();
        Assert.Equal(0, nest.Depth);
        Assert.False(nest.Leave());
        Assert.True(nest.Enter());
    }

    [Fact]
    public void Uninstall_must_defer_while_a_Start_is_between_snapshot_and_restore()
    {
        var nest = new HostedServerLauncher.DoorstopStripNest();
        Assert.False(nest.HasInFlightStart);
        nest.Enter();
        Assert.True(nest.HasInFlightStart);
        nest.Leave();
        Assert.False(nest.HasInFlightStart);
    }

    [Fact]
    public void Three_deep_only_outermost_leave_restores()
    {
        var nest = new HostedServerLauncher.DoorstopStripNest();
        Assert.True(nest.Enter());
        Assert.False(nest.Enter());
        Assert.False(nest.Enter());
        Assert.False(nest.Leave());
        Assert.False(nest.Leave());
        Assert.True(nest.Leave());
        Assert.Equal(0, nest.Depth);
    }

    // ---- ChildLogProbe (R5 finding 9) ---------------------------------------------------------

    [Fact]
    public void Missing_log_is_not_this_spawn()
    {
        var spawn = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(HostedServerLauncher.ChildLogProbe.IsFromThisSpawn(
            exists: false, lastWriteUtc: spawn.AddSeconds(30), spawnUtc: spawn));
    }

    [Fact]
    public void Leftover_log_written_before_spawn_is_not_this_spawn()
    {
        var spawn = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(HostedServerLauncher.ChildLogProbe.IsFromThisSpawn(
            exists: true, lastWriteUtc: spawn.AddSeconds(-1), spawnUtc: spawn));
    }

    [Fact]
    public void Log_written_at_spawn_counts_as_this_spawn()
    {
        var spawn = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(HostedServerLauncher.ChildLogProbe.IsFromThisSpawn(
            exists: true, lastWriteUtc: spawn, spawnUtc: spawn));
    }

    [Fact]
    public void Log_written_after_spawn_counts_as_this_spawn()
    {
        var spawn = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(HostedServerLauncher.ChildLogProbe.IsFromThisSpawn(
            exists: true, lastWriteUtc: spawn.AddSeconds(30), spawnUtc: spawn));
    }

    [Fact]
    public void Bundled_server_log_may_be_retired_to_prev_in_the_same_folder()
    {
        Assert.True(HostedServerLauncher.ChildLogProbe.MayRetireLeftover(
            BundledLog, BundledServer, out var retired, out var reason), reason);
        Assert.Equal(
            HostedPathRules.Normalize(BundledLog) + HostedServerLauncher.ChildLogProbe.RetiredSuffix,
            retired);
        Assert.Equal(".prev", HostedServerLauncher.ChildLogProbe.RetiredSuffix);
        Assert.Equal("LogOutput.log", HostedServerLauncher.ChildLogProbe.FileName);
    }

    [Fact]
    public void Log_name_is_matched_case_insensitively()
    {
        var log = BundledServer + @"\BepInEx\logoutput.LOG";
        Assert.True(HostedServerLauncher.ChildLogProbe.MayRetireLeftover(
            log, BundledServer, out var retired, out var reason), reason);
        Assert.EndsWith(".prev", retired, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, BundledServer)]
    [InlineData("", BundledServer)]
    [InlineData(BundledLog, null)]
    [InlineData(BundledLog, "")]
    public void Empty_retire_paths_are_refused(string? logPath, string? serverDir)
    {
        Assert.False(HostedServerLauncher.ChildLogProbe.MayRetireLeftover(
            logPath, serverDir, out var retired, out var reason));
        Assert.Equal("", retired);
        Assert.Contains("empty", reason);
    }

    [Fact]
    public void Live_dedicated_server_log_must_not_be_retired()
    {
        var live = HostedPathRules.ForbiddenLiveServerRoot + @"\BepInEx\LogOutput.log";
        Assert.False(HostedServerLauncher.ChildLogProbe.MayRetireLeftover(
            live, HostedPathRules.ForbiddenLiveServerRoot, out var retired, out var reason));
        Assert.Equal("", retired);
        Assert.Contains("live dedicated server", reason);
        Assert.Contains(HostedPathRules.ForbiddenLiveServerRoot, reason);
    }

    [Theory]
    [InlineData(@"C:\VRisingServer\BepInEx\LogOutput.log", @"C:\VRisingServer")]
    [InlineData(@"C:\VRisingServer\BepInEx\LogOutput.log", BundledServer)]
    [InlineData(BundledLog, @"C:\VRisingServer")]
    public void Any_live_server_leg_of_a_retire_is_refused(string logPath, string serverDir)
    {
        Assert.False(HostedServerLauncher.ChildLogProbe.MayRetireLeftover(
            logPath, serverDir, out var retired, out var reason));
        Assert.Equal("", retired);
        Assert.Contains("live dedicated server", reason);
    }

    [Fact]
    public void Log_outside_serverDir_is_refused()
    {
        Assert.False(HostedServerLauncher.ChildLogProbe.MayRetireLeftover(
            GameRoot + @"\BepInEx\LogOutput.log", BundledServer, out var retired, out var reason));
        Assert.Equal("", retired);
        Assert.Contains("not inside serverDir", reason);
    }

    [Fact]
    public void Log_not_named_LogOutput_is_refused()
    {
        Assert.False(HostedServerLauncher.ChildLogProbe.MayRetireLeftover(
            BundledServer + @"\BepInEx\Player.log", BundledServer, out var retired, out var reason));
        Assert.Equal("", retired);
        Assert.Contains("unexpected path", reason);
    }

    [Fact]
    public void Log_not_in_BepInEx_folder_is_refused()
    {
        Assert.False(HostedServerLauncher.ChildLogProbe.MayRetireLeftover(
            BundledServer + @"\LogOutput.log", BundledServer, out var retired, out var reason));
        Assert.Equal("", retired);
        Assert.Contains("unexpected path", reason);
    }

    // ---- Leftover bundled child (R6 finding 4) ------------------------------------------------

    [Fact]
    public void Bundled_VRisingServer_under_VRising_Server_is_a_leftover_child()
    {
        var exe = BundledServer + @"\VRisingServer.exe";
        Assert.True(HostedServerLauncher.ChildLogProbe.IsBundledChildProcess(exe, BundledServer));
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\VRising\VRising_Server\VRisingServer.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\VRising\VRising_Server\VRisingServer.exe\")]
    [InlineData(@"c:\program files (x86)\steam\steamapps\common\vrising\vrising_server\vrisingserver.exe")]
    [InlineData(@"\\?\C:\Program Files (x86)\Steam\steamapps\common\VRising\VRising_Server\VRisingServer.exe")]
    public void Steam_x86_bundled_path_is_a_leftover_child_and_not_the_live_server(string exe)
    {
        const string serverDir =
            @"C:\Program Files (x86)\Steam\steamapps\common\VRising\VRising_Server";
        const string serverDirSlash = serverDir + @"\";
        Assert.True(
            HostedServerLauncher.ChildLogProbe.IsBundledChildProcess(exe, serverDir),
            "normalized (x86) process path should match the bundled server dir");
        Assert.True(
            HostedServerLauncher.ChildLogProbe.IsBundledChildProcess(exe, serverDirSlash),
            "trailing separator on serverDir must not miss");
        Assert.False(HostedServerLauncher.ChildLogProbe.IsBundledChildProcess(exe, @"C:\VRisingServer"));
        Assert.False(HostedServerLauncher.ChildLogProbe.IsBundledChildProcess(
            exe, HostedPathRules.ForbiddenLiveServerRoot));
    }

    [Fact]
    public void Live_dedicated_VRisingServer_is_never_a_bundled_child()
    {
        var liveExe = HostedPathRules.ForbiddenLiveServerRoot + @"\VRisingServer.exe";
        Assert.False(HostedServerLauncher.ChildLogProbe.IsBundledChildProcess(
            liveExe, HostedPathRules.ForbiddenLiveServerRoot));
        Assert.False(HostedServerLauncher.ChildLogProbe.IsBundledChildProcess(liveExe, BundledServer));
        Assert.False(HostedServerLauncher.ChildLogProbe.IsBundledChildProcess(
            BundledServer + @"\VRisingServer.exe", HostedPathRules.ForbiddenLiveServerRoot));
    }

    [Fact]
    public void Client_exe_and_empty_paths_are_not_bundled_children()
    {
        Assert.False(HostedServerLauncher.ChildLogProbe.IsBundledChildProcess(
            GameRoot + @"\VRising.exe", BundledServer));
        Assert.False(HostedServerLauncher.ChildLogProbe.IsBundledChildProcess(null, BundledServer));
        Assert.False(HostedServerLauncher.ChildLogProbe.IsBundledChildProcess(
            BundledServer + @"\VRisingServer.exe", null));
        Assert.False(HostedServerLauncher.ChildLogProbe.IsBundledChildProcess("", BundledServer));
    }

    [Fact]
    public void Leftover_child_alive_makes_the_probe_inconclusive_not_present()
    {
        var spawn = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        var line = HostedServerLauncher.ChildLogProbe.FormatProbeLine(
            leftoverChildAlive: true,
            exists: true,
            lastWriteUtc: spawn.AddSeconds(30),
            spawnUtc: spawn,
            probeSeconds: 30,
            length: 4096,
            logPath: BundledLog,
            dryRun: false);
        Assert.StartsWith(
            HostedServerLauncher.ChildLogProbe.InconclusiveLeftoverChildAlive,
            line,
            StringComparison.Ordinal);
        Assert.Contains(BundledLog, line, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("present after", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_without_a_leftover_child_still_reports_present()
    {
        var spawn = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        var line = HostedServerLauncher.ChildLogProbe.FormatProbeLine(
            leftoverChildAlive: false,
            exists: true,
            lastWriteUtc: spawn.AddSeconds(30),
            spawnUtc: spawn,
            probeSeconds: 30,
            length: 128,
            logPath: BundledLog,
            dryRun: false);
        Assert.StartsWith("present after", line, StringComparison.Ordinal);
        Assert.DoesNotContain(
            HostedServerLauncher.ChildLogProbe.InconclusiveLeftoverChildAlive,
            line,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Open_file_rename_failure_is_sharing_or_lock_or_in_use()
    {
        Assert.False(HostedServerLauncher.ChildLogProbe.IsOpenFileRenameFailure(null));
        Assert.True(HostedServerLauncher.ChildLogProbe.IsOpenFileRenameFailure(
            new UnauthorizedAccessException("denied")));
        Assert.True(HostedServerLauncher.ChildLogProbe.IsOpenFileRenameFailure(
            new IOException("The process cannot access the file because it is being used by another process.")));
        Assert.False(HostedServerLauncher.ChildLogProbe.IsOpenFileRenameFailure(
            new ArgumentException("not IO")));
        Assert.False(HostedServerLauncher.ChildLogProbe.IsOpenFileRenameFailure(
            new IOException("disk full")));
    }

    [Fact]
    public void Sharing_violation_HRESULT_is_an_open_file_rename_failure()
    {
        var e = new SharingViolationIoException();
        Assert.True(HostedServerLauncher.ChildLogProbe.IsOpenFileRenameFailure(e));
    }

    [Fact]
    public void Rename_failure_treats_leftover_as_alive_and_probe_is_inconclusive()
    {
        Assert.True(HostedServerLauncher.ChildLogProbe.TreatRenameFailureAsLeftoverAlive(
            new IOException("disk full")));
        Assert.True(HostedServerLauncher.ChildLogProbe.TreatRenameFailureAsLeftoverAlive(
            new UnauthorizedAccessException("denied")));
        Assert.True(HostedServerLauncher.ChildLogProbe.TreatRenameFailureAsLeftoverAlive(
            new SharingViolationIoException()));
        Assert.False(HostedServerLauncher.ChildLogProbe.TreatRenameFailureAsLeftoverAlive(null));

        var spawn = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        var line = HostedServerLauncher.ChildLogProbe.FormatProbeLine(
            leftoverChildAlive: true,
            exists: true,
            lastWriteUtc: spawn.AddSeconds(30),
            spawnUtc: spawn,
            probeSeconds: 30,
            length: 4096,
            logPath: BundledLog,
            dryRun: false);
        Assert.StartsWith(
            HostedServerLauncher.ChildLogProbe.InconclusiveLeftoverChildAlive,
            line,
            StringComparison.Ordinal);
        Assert.DoesNotContain("present after", line, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class SharingViolationIoException : IOException
    {
        public SharingViolationIoException()
            : base("sharing violation")
        {
            HResult = unchecked((int)0x80070020);
        }
    }

    // ---- UninstallDeferral (R8 regression B, row 6) -------------------------------------------

    [Fact]
    public void Install_does_not_cancel_a_deferred_Uninstall()
    {
        Assert.False(HostedServerLauncher.UninstallDeferral.CancelDeferredUninstallOnInstall(
            installed: true, uninstallRequested: true));
        Assert.False(HostedServerLauncher.UninstallDeferral.CancelDeferredUninstallOnInstall(
            installed: true, uninstallRequested: false));
        Assert.False(HostedServerLauncher.UninstallDeferral.CancelDeferredUninstallOnInstall(
            installed: false, uninstallRequested: true));
    }

    [Fact]
    public void Install_queues_after_deferred_Uninstall_instead_of_no_op()
    {
        Assert.True(HostedServerLauncher.UninstallDeferral.QueueInstallAfterDeferredUninstall(
            installed: true, uninstallRequested: true));
        Assert.False(HostedServerLauncher.UninstallDeferral.QueueInstallAfterDeferredUninstall(
            installed: true, uninstallRequested: false));
        Assert.False(HostedServerLauncher.UninstallDeferral.QueueInstallAfterDeferredUninstall(
            installed: false, uninstallRequested: true));
        Assert.True(HostedServerLauncher.UninstallDeferral.ShouldRunQueuedInstall(
            queuedInstall: true, nowInstalled: false));
        Assert.False(HostedServerLauncher.UninstallDeferral.ShouldRunQueuedInstall(
            queuedInstall: true, nowInstalled: true));
        Assert.False(HostedServerLauncher.UninstallDeferral.ShouldRunQueuedInstall(
            queuedInstall: false, nowInstalled: false));
    }

    [Fact]
    public void Finalizer_must_not_unpatch_Harmony()
    {
        Assert.False(HostedServerLauncher.UninstallDeferral.RunDeferredUninstallFromFinalizer(
            uninstallRequested: true, hasInFlightStart: false));
        Assert.False(HostedServerLauncher.UninstallDeferral.RunDeferredUninstallFromFinalizer(
            uninstallRequested: true, hasInFlightStart: true));
        Assert.False(HostedServerLauncher.UninstallDeferral.RunDeferredUninstallFromFinalizer(
            uninstallRequested: false, hasInFlightStart: false));
    }

    [Fact]
    public void Deferred_Uninstall_completes_from_idle_tick_not_the_Start_finalizer()
    {
        Assert.True(HostedServerLauncher.UninstallDeferral.RequestIdleCompletionFromFinalizer(
            uninstallRequested: true, hasInFlightStart: false));
        Assert.False(HostedServerLauncher.UninstallDeferral.RequestIdleCompletionFromFinalizer(
            uninstallRequested: true, hasInFlightStart: true));
        Assert.False(HostedServerLauncher.UninstallDeferral.RequestIdleCompletionFromFinalizer(
            uninstallRequested: false, hasInFlightStart: false));

        Assert.True(HostedServerLauncher.UninstallDeferral.ShouldCompleteDeferredUninstall(
            uninstallRequested: true, hasInFlightStart: false));
        Assert.False(HostedServerLauncher.UninstallDeferral.ShouldCompleteDeferredUninstall(
            uninstallRequested: true, hasInFlightStart: true));
        Assert.False(HostedServerLauncher.UninstallDeferral.ShouldCompleteDeferredUninstall(
            uninstallRequested: false, hasInFlightStart: false));

        Assert.Equal(0.5, HostedServerLauncher.UninstallDeferral.RetrySeconds);
    }

    [Fact]
    public void Leftover_scan_at_probe_time_excludes_the_child_spawned_by_this_start()
    {
        var spawn = new DateTime(2026, 9, 3, 5, 30, 0, DateTimeKind.Utc);
        Assert.False(HostedServerLauncher.ChildLogProbe.StartedBefore(spawn.AddSeconds(1), spawn));
        Assert.False(HostedServerLauncher.ChildLogProbe.StartedBefore(spawn.AddSeconds(-1), spawn));
        Assert.True(HostedServerLauncher.ChildLogProbe.StartedBefore(spawn.AddMinutes(-5), spawn));
        Assert.True(HostedServerLauncher.ChildLogProbe.StartedBefore(null, spawn));
    }

    // ---- StartDecision (R11 finding 8) --------------------------------------------------------

    [Fact]
    public void Leftover_undeletable_Core_refuses_the_spawn()
    {
        Assert.Equal(
            HostedStartAction.RefuseSpawn,
            HostedServerLauncher.StartDecision.Decide(
                isBundledServerExe: true,
                enabled: true,
                serverDirValid: true,
                stagingOk: false,
                leftoverPluginsCoreDllCouldNotBeRemoved: true));
    }

    [Fact]
    public void Refuse_spawn_log_names_leftover_child_not_plugins_Core()
    {
        var child = HostedServerLauncher.RefuseSpawnLog.Describe(leftoverChildMappedCore: true);
        Assert.Contains("leftover hosted child", child, StringComparison.Ordinal);
        Assert.Contains("progenitor-core/Progenitor.dll", child, StringComparison.Ordinal);
        Assert.DoesNotContain("plugins/Progenitor.dll", child, StringComparison.Ordinal);
        Assert.Contains("leftover hosted child", HostedServerLauncher.RefuseSpawnLog.HookError(true), StringComparison.Ordinal);

        var plugins = HostedServerLauncher.RefuseSpawnLog.Describe(leftoverChildMappedCore: false);
        Assert.Contains("plugins/Progenitor.dll", plugins, StringComparison.Ordinal);
        Assert.DoesNotContain("leftover hosted child", plugins, StringComparison.Ordinal);
    }

    [Fact]
    public void Successful_staging_injects_and_strips_Doorstop()
    {
        Assert.Equal(
            HostedStartAction.InjectAndStripDoorstop,
            HostedServerLauncher.StartDecision.Decide(
                isBundledServerExe: true,
                enabled: true,
                serverDirValid: true,
                stagingOk: true,
                leftoverPluginsCoreDllCouldNotBeRemoved: false));
    }

    [Fact]
    public void Staging_failure_without_leftover_Core_does_not_inject()
    {
        Assert.Equal(
            HostedStartAction.PassThrough,
            HostedServerLauncher.StartDecision.Decide(
                isBundledServerExe: true,
                enabled: true,
                serverDirValid: true,
                stagingOk: false,
                leftoverPluginsCoreDllCouldNotBeRemoved: false));
    }

    [Fact]
    public void Disabled_or_non_server_exe_is_passthrough()
    {
        Assert.Equal(
            HostedStartAction.PassThrough,
            HostedServerLauncher.StartDecision.Decide(
                isBundledServerExe: false,
                enabled: true,
                serverDirValid: true,
                stagingOk: true,
                leftoverPluginsCoreDllCouldNotBeRemoved: false));
        Assert.Equal(
            HostedStartAction.PassThrough,
            HostedServerLauncher.StartDecision.Decide(
                isBundledServerExe: true,
                enabled: false,
                serverDirValid: true,
                stagingOk: true,
                leftoverPluginsCoreDllCouldNotBeRemoved: false));
        Assert.Equal(
            HostedStartAction.PassThrough,
            HostedServerLauncher.StartDecision.Decide(
                isBundledServerExe: true,
                enabled: true,
                serverDirValid: false,
                stagingOk: true,
                leftoverPluginsCoreDllCouldNotBeRemoved: false));
    }

    // ---- CompletionHookRouting (R11 finding 4 / CC open question 2) ---------------------------

    [Fact]
    public void Bootstrap_present_routes_deferred_Uninstall_through_lifetime_slots_not_ConvertDelegate()
    {
        var route = HostedServerLauncher.CompletionHookRouting.Decide(bootstrapOwnsLifetime: true);
        Assert.Equal(HostedCompletionHookRoute.BootstrapSlot, route);
        Assert.True(HostedServerLauncher.CompletionHookRouting.UseBootstrapLifetimeSlots(route));
        Assert.False(HostedServerLauncher.CompletionHookRouting.ConvertDelegateFromCore(route));
        Assert.False(HostedServerLauncher.CompletionHookRouting.SubscribePluginFrame(route));
    }

    [Fact]
    public void Bootstrap_absent_falls_back_to_ConvertDelegate_and_is_logged()
    {
        var route = HostedServerLauncher.CompletionHookRouting.Decide(bootstrapOwnsLifetime: false);
        Assert.Equal(HostedCompletionHookRoute.CoreConvertDelegateFallback, route);
        Assert.False(HostedServerLauncher.CompletionHookRouting.UseBootstrapLifetimeSlots(route));
        Assert.True(HostedServerLauncher.CompletionHookRouting.ConvertDelegateFromCore(route));
        Assert.True(HostedServerLauncher.CompletionHookRouting.SubscribePluginFrame(route));
        Assert.Contains("Bootstrap absent", HostedServerLauncher.CompletionHookRouting.FallbackLog, StringComparison.Ordinal);
        Assert.Contains("ConvertDelegate", HostedServerLauncher.CompletionHookRouting.FallbackLog, StringComparison.Ordinal);
        Assert.Contains("pins this Core ALC", HostedServerLauncher.CompletionHookRouting.FallbackLog, StringComparison.Ordinal);
    }

    [Fact]
    public void Routing_does_not_weaken_live_server_refusals()
    {
        // Completion-hook routing is independent of path guards: a live dedicated
        // server path is still refused whether Bootstrap owns lifetime or not.
        _ = HostedServerLauncher.CompletionHookRouting.Decide(bootstrapOwnsLifetime: true);
        _ = HostedServerLauncher.CompletionHookRouting.Decide(bootstrapOwnsLifetime: false);
        Assert.True(HostedPathRules.IsForbiddenLiveServerPath(@"C:\VRisingServer\VRisingServer.exe"));
        Assert.False(HostedServerLauncher.ChildLogProbe.MayRetireLeftover(
            @"C:\VRisingServer\BepInEx\LogOutput.log",
            @"C:\VRisingServer",
            out var retired,
            out var reason));
        Assert.Equal("", retired);
        Assert.Contains("live dedicated server", reason);
        Assert.Equal(
            HostedStartAction.PassThrough,
            HostedServerLauncher.StartDecision.Decide(
                isBundledServerExe: true,
                enabled: true,
                serverDirValid: false,
                stagingOk: true,
                leftoverPluginsCoreDllCouldNotBeRemoved: false));
    }
}
