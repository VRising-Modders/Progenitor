using System;
using System.IO;
using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of Step E (watch <c>Progenitor.dll.next</c>) and Step F
/// (quarantine names, lastReload dump). Live <c>CoreWatch</c> / disk moves are not run here.
/// </summary>
public class CoreWatchPolicyTests
{
    [Fact]
    public void Decide_requests_only_a_settled_new_stamp_when_enabled()
    {
        Assert.Equal(
            CoreWatchDecision.Request,
            CoreWatchPolicy.Decide(true, hostedChild: false, alreadyReloading: false, nextExists: true, settled: true, sameStampAsLastRequest: false));
        Assert.Equal(
            CoreWatchDecision.Skip,
            CoreWatchPolicy.Decide(true, false, false, nextExists: false, settled: true, sameStampAsLastRequest: false));
        Assert.Equal(
            CoreWatchDecision.Skip,
            CoreWatchPolicy.Decide(true, false, false, nextExists: true, settled: false, sameStampAsLastRequest: false));
        Assert.Equal(
            CoreWatchDecision.Skip,
            CoreWatchPolicy.Decide(true, false, false, nextExists: true, settled: true, sameStampAsLastRequest: true));
    }

    [Fact]
    public void Decide_refuses_hosted_child_and_in_flight_and_honours_the_config_flag()
    {
        Assert.Equal(
            CoreWatchDecision.RefuseHosted,
            CoreWatchPolicy.Decide(true, hostedChild: true, alreadyReloading: false, nextExists: true, settled: true, sameStampAsLastRequest: false));
        Assert.Equal(
            CoreWatchDecision.RefuseHosting,
            CoreWatchPolicy.Decide(true, hostedChild: false, alreadyReloading: false, nextExists: true, settled: true, sameStampAsLastRequest: false, hostingPrivateGame: true));
        Assert.Equal(
            CoreWatchDecision.RefuseInFlight,
            CoreWatchPolicy.Decide(true, hostedChild: false, alreadyReloading: true, nextExists: true, settled: true, sameStampAsLastRequest: false));
        Assert.Equal(
            CoreWatchDecision.Disabled,
            CoreWatchPolicy.Decide(enabled: false, hostedChild: false, alreadyReloading: false, nextExists: true, settled: true, sameStampAsLastRequest: false));
    }

    [Fact]
    public void Hosted_child_is_parentPID_or_the_hosted_save_token()
    {
        Assert.False(CoreWatchPolicy.IsHostedChild(null));
        Assert.False(CoreWatchPolicy.IsHostedChild(""));
        Assert.False(CoreWatchPolicy.IsHostedChild(@"C:\GameModding\_testserver\server\VRisingServer.exe -persistentDataPath save-data"));
        Assert.True(CoreWatchPolicy.IsHostedChild(@"VRisingServer.exe -persistentDataPath x -parentPID 1234"));
        Assert.True(CoreWatchPolicy.IsHostedChild(@"VRisingServer.exe -parentPID=99"));
        Assert.True(CoreWatchPolicy.IsHostedChild(@"VRisingServer.exe -persistentDataPath CloudSaves\1\v4\progenitor-hosted"));
        Assert.False(CoreWatchPolicy.IsHostedChild(@"VRisingServer.exe -saveName not-parentPID-really"));
    }

    [Fact]
    public void Watch_log_lines_are_the_grep_strings()
    {
        Assert.Equal("watch", CoreWatchPolicy.WatchReason);
        Assert.Equal("Reload", CoreWatchPolicy.ConfigSection);
        Assert.Equal("WatchCore", CoreWatchPolicy.ConfigKey);
        Assert.Equal("Progenitor.core.next", CoreWatchPolicy.NextFileName);
        Assert.Equal(1.0, CoreWatchPolicy.SettleSeconds);
        Assert.Equal(1.0, CoreWatchPolicy.ScanIntervalSeconds);
        Assert.Equal("Core watch: Progenitor.dll.next settled; requesting reload.", CoreWatchPolicy.FormatSettled());
        Assert.Equal("Core watch: Progenitor.dll.next is identical to the live Core; skipped", CoreWatchPolicy.FormatSkippedIdentical());
        Assert.Equal("Core watch refused: a swap is already in progress.", CoreWatchPolicy.FormatRefusedInFlight());
        Assert.Equal("Core watch refused: this process is a hosted child.", CoreWatchPolicy.FormatRefusedHosted());
        Assert.Equal(
            "Core watch refused: this client is hosting a private game; the child keeps the copied Core.",
            CoreWatchPolicy.FormatRefusedHosting());
        Assert.Equal("Core watch: Reload.WatchCore is false.", CoreWatchPolicy.FormatDisabled());
        Assert.True(CoreWatchPolicy.RequestAccepted("scheduled gen=2"));
        Assert.True(CoreWatchPolicy.RequestAccepted("scheduled gen=2 dryRun"));
        Assert.False(CoreWatchPolicy.RequestAccepted("Core reload refused: a swap is already in progress."));
        Assert.False(CoreWatchPolicy.RequestAccepted(null));
        Assert.Equal(
            "Core watch: RequestReload refused; will retry when the staged file changes; "
            + "a restart promotes it. still alive; restart the process",
            CoreWatchPolicy.FormatRefusedRequest("still alive; restart the process"));
        Assert.True(CoreWatchPolicy.LooksLikeHostedSpawn(@"VRisingServer.exe -parentPID 1234"));
        Assert.False(CoreWatchPolicy.LooksLikeHostedSpawn(@"VRisingServer.exe -saveName test1"));
        Assert.Equal(5, CoreWatchPolicy.PromoteRetryLimit);
        Assert.True(CoreWatchPolicy.StampAfterPromote(promoteSucceeded: true));
        Assert.False(CoreWatchPolicy.StampAfterPromote(promoteSucceeded: false));
        Assert.True(CoreWatchPolicy.MayRetryPromote(1));
        Assert.False(CoreWatchPolicy.MayRetryPromote(0));
        Assert.False(CoreWatchPolicy.MayRetryPromote(5));
        Assert.True(CoreWatchPolicy.ExhaustedPromoteRetries(5));
        Assert.False(CoreWatchPolicy.ExhaustedPromoteRetries(4));
        Assert.Equal(
            "Core watch: promote failed; will retry 1/5. sharing violation",
            CoreWatchPolicy.FormatPromoteRetry(1, 5, "sharing violation"));
        Assert.Equal(
            "Core watch: promote failed; giving up until the file changes. sharing violation",
            CoreWatchPolicy.FormatPromoteRetryExhausted("sharing violation"));
        Assert.Equal(
            "Core watch: WatchCore resumed (hosted child exited).",
            CoreWatchPolicy.FormatWatchResumed(CoreWatchPolicy.ResumeReasonChildExited));
        Assert.Equal(
            "Core watch: WatchCore resumed (joined a dedicated server).",
            CoreWatchPolicy.FormatWatchResumed(CoreWatchPolicy.ResumeReasonDedicatedJoin));
    }

    [Fact]
    public void Hosted_child_matches_path_plus_argv_never_the_live_server()
    {
        const int pid = 4242;
        string childPath = @"C:\Steam\VRising\VRising_Server\VRisingServer.exe";
        string livePath = CoreWatchPolicy.ForbiddenLiveServerRoot + @"\VRisingServer.exe";
        Assert.True(CoreWatchPolicy.IsForbiddenLiveServerPath(livePath));
        Assert.False(CoreWatchPolicy.IsForbiddenLiveServerPath(childPath));
        Assert.False(CoreWatchPolicy.IsForbiddenLiveServerPath(@"C:\VRisingServerBackup\VRisingServer.exe"));

        Assert.True(CoreWatchPolicy.HasParentPid(@"VRisingServer.exe -parentPID 4242 -saveName progenitor-hosted", pid));
        Assert.True(CoreWatchPolicy.HasParentPid(@"VRisingServer.exe -parentPID=4242", pid));
        Assert.False(CoreWatchPolicy.HasParentPid(@"VRisingServer.exe -parentPID 42420", pid));
        Assert.False(CoreWatchPolicy.HasParentPid(@"VRisingServer.exe -saveName test1", pid));

        Assert.True(CoreWatchPolicy.IsLiveHostedChild(
            childPath, @"-parentPID 4242 -saveName progenitor-hosted", pid, childPath));
        Assert.True(CoreWatchPolicy.IsLiveHostedChild(
            childPath, commandLine: null, pid, spawnedPath: childPath));
        Assert.False(CoreWatchPolicy.IsLiveHostedChild(
            livePath, @"-parentPID 4242 -saveName progenitor-hosted", pid, childPath));
        Assert.False(CoreWatchPolicy.IsLiveHostedChild(
            childPath, @"-saveName test1", pid, spawnedPath: null));
        Assert.False(CoreWatchPolicy.IsLiveHostedChild(
            executablePath: null, commandLine: null, pid, spawnedPath: childPath));

        Assert.False(CoreWatchPolicy.IsDedicatedJoin(shouldHost: true, "progenitor-hosted"));
        Assert.False(CoreWatchPolicy.IsDedicatedJoin(shouldHost: false, "CloudSaves\\1\\v4\\progenitor-hosted"));
        Assert.True(CoreWatchPolicy.IsDedicatedJoin(shouldHost: false, "test1"));
        Assert.True(CoreWatchPolicy.IsDedicatedJoin(shouldHost: false, serverSaveName: null));

        Assert.True(CoreWatchPolicy.ShouldClearHosting(wasHosting: true, childStillRunning: false, dedicatedJoin: false));
        Assert.True(CoreWatchPolicy.ShouldClearHosting(wasHosting: true, childStillRunning: true, dedicatedJoin: true));
        Assert.False(CoreWatchPolicy.ShouldClearHosting(wasHosting: true, childStillRunning: true, dedicatedJoin: false));
        Assert.False(CoreWatchPolicy.ShouldClearHosting(wasHosting: false, childStillRunning: false, dedicatedJoin: false));

        Assert.Equal("StunProcess_MONO$$Start", CoreWatchPolicy.HostedSpawnDumpName);
        Assert.True(CoreWatchPolicy.ChildStillRunning(scanFailed: true, processesQueried: 10, foundLive: false));
        Assert.True(CoreWatchPolicy.ChildStillRunning(scanFailed: false, processesQueried: 0, foundLive: false));
        Assert.True(CoreWatchPolicy.ChildStillRunning(scanFailed: false, processesQueried: 3, foundLive: true));
        Assert.False(CoreWatchPolicy.ChildStillRunning(scanFailed: false, processesQueried: 3, foundLive: false));
        Assert.False(CoreWatchPolicy.ShouldClearHosting(
            wasHosting: true,
            childStillRunning: CoreWatchPolicy.ChildStillRunning(scanFailed: true, processesQueried: 0, foundLive: false),
            dedicatedJoin: false));
        Assert.Equal(
            "Core watch: hosted-child probe could not query processes; keeping the hosting flag.",
            CoreWatchPolicy.FormatHostedScanFailed());
        Assert.Equal(
            "Core watch: hosted-child probe OpenProcess/NtQueryInformationProcess failed; keeping the hosting flag.",
            CoreWatchPolicy.FormatHostedQueryFailed());
    }

    [Fact]
    public void Inspect_quarantines_invalid_next_without_request_or_stamp()
    {
        var invalid = CoreWatchPolicy.InspectStaged(nextIsValidImage: false, identicalToLive: false);
        Assert.Equal(CoreWatchInspectDecision.Quarantine, invalid);
        Assert.False(CoreWatchPolicy.MayRequestAfterInspect(invalid));
        Assert.False(CoreWatchPolicy.StampAfterInspect(invalid));
        Assert.Equal(
            CoreWatchInspectDecision.Quarantine,
            CoreWatchPolicy.InspectStaged(nextIsValidImage: false, identicalToLive: true));
        // Live CoreWatch always runs image validation; identical junk must not skip.
        Assert.True(CoreWatchPolicy.CancelScheduledReload(hadStagedNext: true, promoteSucceeded: false));
        Assert.False(CoreWatchPolicy.CancelScheduledReload(hadStagedNext: true, promoteSucceeded: true));
        Assert.False(CoreWatchPolicy.CancelScheduledReload(hadStagedNext: false, promoteSucceeded: false));
        Assert.False(CoreWatchPolicy.CancelScheduledReload(hadStagedNext: true, promoteSucceeded: false, coreLoaded: false));
    }

    [Fact]
    public void Inspect_skips_identical_live_bytes_with_the_grep_line()
    {
        var identical = CoreWatchPolicy.InspectStaged(nextIsValidImage: true, identicalToLive: true);
        Assert.Equal(CoreWatchInspectDecision.SkipIdentical, identical);
        Assert.False(CoreWatchPolicy.MayRequestAfterInspect(identical));
        Assert.True(CoreWatchPolicy.StampAfterInspect(identical));
        Assert.Equal(
            "Core watch: Progenitor.dll.next is identical to the live Core; skipped",
            CoreWatchPolicy.FormatSkippedIdentical());

        byte[] live = { 1, 2, 3, 4 };
        Assert.True(CoreWatchPolicy.BytesIdentical(live, (byte[])live.Clone()));
        Assert.False(CoreWatchPolicy.BytesIdentical(live, new byte[] { 1, 2, 3, 5 }));
        Assert.False(CoreWatchPolicy.BytesIdentical(live, new byte[] { 1, 2, 3 }));
        Assert.False(CoreWatchPolicy.BytesIdentical(live, null));
        Assert.False(CoreWatchPolicy.BytesIdentical(null, live));
        Assert.False(CoreWatchPolicy.BytesIdentical(Array.Empty<byte>(), Array.Empty<byte>()));

        var request = CoreWatchPolicy.InspectStaged(nextIsValidImage: true, identicalToLive: false);
        Assert.Equal(CoreWatchInspectDecision.Request, request);
        Assert.True(CoreWatchPolicy.MayRequestAfterInspect(request));
        Assert.False(CoreWatchPolicy.StampAfterInspect(request));
    }

    [Fact]
    public void NextPath_and_quarantine_names_sit_next_to_the_live_dll()
    {
        string dll = Path.Combine("BepInEx", "progenitor-core", "Progenitor.dll");
        Assert.Equal(dll + ".next", CoreWatchPolicy.NextPath(dll));
        var utc = new DateTime(2026, 9, 3, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Progenitor.dll.failed-20260903120000000", CoreWatchPolicy.FailedFileName(utc));
        Assert.Equal(
            Path.Combine("BepInEx", "progenitor-core", "Progenitor.dll.failed-20260903120000000"),
            CoreWatchPolicy.QuarantinePath(dll, utc));
        Assert.True(CoreWatchPolicy.IsSettled(utc, utc.AddSeconds(1)));
        Assert.False(CoreWatchPolicy.IsSettled(utc, utc.AddMilliseconds(500)));
        var first = new CoreWatchPolicy.FileStamp(100, utc);
        var same = new CoreWatchPolicy.FileStamp(100, utc);
        var differentLength = new CoreWatchPolicy.FileStamp(99, utc);
        Assert.False(CoreWatchPolicy.IsSettled(previous: null, first));
        Assert.True(CoreWatchPolicy.IsSettled(first, same));
        Assert.False(CoreWatchPolicy.IsSettled(first, differentLength));
        Assert.False(CoreWatchPolicy.IsSettled(first, new CoreWatchPolicy.FileStamp(0, utc)));
    }

    [Fact]
    public void LastReload_dump_carries_gen_outcome_reason_and_protocol()
    {
        CoreReloadStatus.Record(2, CoreReloadStatus.OutcomeFailed, "watch", "progenitor-core:2", inFlight: false);
        string line = CoreReloadStatus.FormatLastReload();
        Assert.Contains("lastReload gen=2", line);
        Assert.Contains("outcome=failed", line);
        Assert.Contains("reason=watch", line);

        var dump = CoreReloadStatus.ToDump(liveGeneration: 1, liveAlcName: "progenitor-core:1", bootstrapProtocol: 1, coreProtocol: 1);
        var t = dump.GetType();
        Assert.Equal(1, t.GetProperty("generation")!.GetValue(dump));
        Assert.Equal("progenitor-core:1", t.GetProperty("alcName")!.GetValue(dump));
        Assert.Equal(1, t.GetProperty("protocol")!.GetValue(dump));
        Assert.Equal(1, t.GetProperty("bootstrapProtocol")!.GetValue(dump));
        Assert.Equal(1, t.GetProperty("coreProtocol")!.GetValue(dump));
        var watch = t.GetProperty("watch")!.GetValue(dump)!;
        Assert.NotNull(watch.GetType().GetProperty("hostedChild"));
        Assert.NotNull(watch.GetType().GetProperty("hostingPrivateGame"));
        var last = t.GetProperty("lastReload")!.GetValue(dump)!;
        var lt = last.GetType();
        Assert.Equal(2, lt.GetProperty("gen")!.GetValue(last));
        Assert.Equal("failed", lt.GetProperty("outcome")!.GetValue(last));
        Assert.Equal("watch", lt.GetProperty("reason")!.GetValue(last));
    }

    /// <summary>
    /// Job FREL8 / R67 finding 1: the same order <c>CoreWatch.TickCore</c> runs, with the
    /// two costs the review measured made countable — <see cref="Inspections"/> is the full
    /// read + SHA-256 of the staged image (<c>ShouldRequestReload</c>), and
    /// <see cref="LogLines"/> is every line one tick writes. Only the policy decisions are
    /// the real ones; the disk and the loader are stand-ins.
    /// </summary>
    private sealed class WatchSim
    {
        private CoreWatchPolicy.FileStamp _parked;
        private bool _haveParked;
        private string? _parkedReason;
        private CoreWatchPolicy.FileStamp _held;
        private bool _haveHeld;
        private double _heldRetryAt;
        private int _heldRetries;
        private string? _lastTransientReason;
        private CoreWatchPolicy.FileStamp _inspected;
        private bool _haveInspected;
        private double _now;
        private bool _inFlight;
        private CoreWatchPolicy.FileStamp _lastSeen;
        private bool _haveSeen;
        private CoreWatchPolicy.FileStamp _lastRequested;
        private bool _haveRequested;

        /// <summary>Full reads plus SHA-256 of the 2 MB staged image.</summary>
        public int Inspections { get; private set; }

        /// <summary>Every log line the tick emits (staged-sha, settled, refusal).</summary>
        public int LogLines { get; private set; }

        public int Requests { get; private set; }

        public bool Parked => _haveParked;

        public string? ParkedReason => _parkedReason;

        /// <summary>What <c>CoreLoader.RequestReload</c> answers. Default: the live refusal.</summary>
        public string Answer { get; set; } =
            "Core reload refused: generation 44 (progenitor-core:44) is still alive; restart the process.";

        /// <summary>Job FREL9: the image is held (backoff), not parked.</summary>
        public bool Held => _haveHeld;

        public void Tick(bool exists, CoreWatchPolicy.FileStamp stamp)
        {
            double now = _now;
            _now += CoreWatchPolicy.ScanIntervalSeconds;
            bool settled = false;
            bool same = false;
            if (exists)
            {
                settled = CoreWatchPolicy.IsSettled(_haveSeen ? _lastSeen : null, stamp);
                _lastSeen = stamp;
                _haveSeen = true;
                same = _haveRequested && stamp.Equals(_lastRequested);
            }
            else
            {
                _haveSeen = false;
            }

            bool parked = CoreWatchPolicy.ParkedOnStamp(_haveParked ? _parked : null, exists, stamp);
            if (!parked && _haveParked)
            {
                _haveParked = false;
                _parkedReason = null;
            }
            if (_haveHeld && !(exists && stamp.Equals(_held))) ClearHold();
            bool held = CoreWatchPolicy.HeldOnStamp(
                _haveHeld ? _held : null, exists, stamp, now, _heldRetryAt);
            if (_haveInspected && !(exists && stamp.Equals(_inspected))) _haveInspected = false;
            same |= parked || held;

            var decision = CoreWatchPolicy.Decide(
                enabled: true, hostedChild: false, alreadyReloading: _inFlight,
                nextExists: exists, settled: settled, sameStampAsLastRequest: same);
            if (decision != CoreWatchDecision.Request) return;

            // Job FREL10: a retry of a held image reuses the first attempt's read, hash,
            // allowlist verdict and identical-to-live comparison, and writes neither line.
            if (!CoreWatchPolicy.MayReuseInspection(_haveInspected ? _inspected : null, exists, stamp))
            {
                Inspections++;
                LogLines++;      // "staged Progenitor.dll.next sha256=... size=..."
                LogLines++;      // "Progenitor.dll.next settled; requesting reload."
                _inspected = stamp;
                _haveInspected = true;
            }
            Requests++;
            string result = Answer;
            if (CoreWatchPolicy.RequestAccepted(result))
            {
                _haveParked = false;
                _parkedReason = null;
                _haveInspected = false;
                ClearHold();
                _inFlight = true;      // ReloadDriver.Request holds the frame driver
                return;
            }

            if (CoreWatchPolicy.ParkOnRefusal(result))
            {
                _parked = stamp;
                _haveParked = true;
                _parkedReason = result;
                ClearHold();
                LogLines++;      // the one refusal line
                return;
            }

            if (_haveHeld && stamp.Equals(_held))
            {
                _heldRetries++;
            }
            else
            {
                _held = stamp;
                _haveHeld = true;
                _heldRetries = 0;
                _lastTransientReason = null;
            }

            // Job FREL10: the bound. The last retry parks instead of arming another backoff.
            if (CoreWatchPolicy.ExhaustedTransientRetries(_heldRetries))
            {
                GaveUpAfter = _heldRetries;
                _parked = stamp;
                _haveParked = true;
                _parkedReason = result;
                ClearHold();
                LogLines++;      // the one give-up line
                return;
            }

            _heldRetryAt = CoreWatchPolicy.NextRetryAt(now);
            if (CoreWatchPolicy.ShouldLogRefusal(_lastTransientReason, result))
            {
                _lastTransientReason = result;
                LogLines++;      // one line per distinct transient reason
            }
        }

        /// <summary>Retries the watcher gave a transient refusal before parking it; 0 if it never did.</summary>
        public int GaveUpAfter { get; private set; }

        private void ClearHold()
        {
            _haveHeld = false;
            _heldRetryAt = 0;
            _heldRetries = 0;
            _lastTransientReason = null;
        }

        /// <summary>The successful promote, which is the only thing that stamps.</summary>
        public void NotePromoteSucceeded()
        {
            if (!_haveSeen) return;
            _lastRequested = _lastSeen;
            _haveRequested = true;
            _inFlight = false;
            _haveParked = false;
            _parkedReason = null;
            _haveInspected = false;
            ClearHold();
        }
    }

    [Fact]
    public void A_refused_next_that_did_not_change_is_never_inspected_again()
    {
        var utc = new DateTime(2026, 9, 6, 2, 13, 11, DateTimeKind.Utc);
        var staged = new CoreWatchPolicy.FileStamp(2112000, utc);
        var sim = new WatchSim();

        // Ten minutes of one-second scans. Before FREL8 this was 600 reads, 600 hashes
        // and ~1,800 lines through a 4,000-entry ring.
        for (int i = 0; i < 600; i++) sim.Tick(exists: true, staged);

        Assert.Equal(1, sim.Inspections);
        Assert.Equal(1, sim.Requests);
        Assert.Equal(3, sim.LogLines);
        Assert.True(sim.Parked);
        Assert.Contains("still alive", sim.ParkedReason);
    }

    [Fact]
    public void A_changed_next_after_a_refusal_is_inspected_exactly_once_more()
    {
        var utc = new DateTime(2026, 9, 6, 2, 13, 11, DateTimeKind.Utc);
        var first = new CoreWatchPolicy.FileStamp(2112000, utc);
        var sim = new WatchSim();
        for (int i = 0; i < 30; i++) sim.Tick(exists: true, first);
        Assert.Equal(1, sim.Inspections);

        // A fresh deploy: different length and write time. The park is keyed to the stamp.
        var second = new CoreWatchPolicy.FileStamp(2113024, utc.AddMinutes(5));
        for (int i = 0; i < 30; i++) sim.Tick(exists: true, second);

        Assert.Equal(2, sim.Inspections);
        Assert.Equal(2, sim.Requests);
        Assert.Equal(6, sim.LogLines);
        Assert.True(sim.Parked);

        // Deleting the staged file unparks, and nothing is inspected while it is gone.
        for (int i = 0; i < 30; i++) sim.Tick(exists: false, default);
        Assert.Equal(2, sim.Inspections);
        Assert.False(sim.Parked);
    }

    [Fact]
    public void A_park_does_not_stamp_or_consume_the_file_so_a_restart_still_promotes_it()
    {
        var utc = new DateTime(2026, 9, 6, 2, 13, 11, DateTimeKind.Utc);
        var staged = new CoreWatchPolicy.FileStamp(2112000, utc);
        var sim = new WatchSim();
        for (int i = 0; i < 30; i++) sim.Tick(exists: true, staged);
        Assert.True(sim.Parked);

        // The restart is a new process: a fresh watcher, no park, and the same file on
        // disk. It must still be a Request — the park never quarantined or stamped it.
        var afterRestart = new WatchSim { Answer = "scheduled gen=1" };
        afterRestart.Tick(exists: true, staged);
        afterRestart.Tick(exists: true, staged);
        Assert.Equal(1, afterRestart.Requests);
        Assert.False(afterRestart.Parked);

        // A successful promote is what stamps, and that path is untouched.
        afterRestart.NotePromoteSucceeded();
        for (int i = 0; i < 10; i++) afterRestart.Tick(exists: true, staged);
        Assert.Equal(1, afterRestart.Requests);
        Assert.False(afterRestart.Parked);
    }

    /// <summary>
    /// Job FREL9 / R68 finding 1: exactly one of the four answers
    /// <c>CoreLoader.RequestReload</c> can refuse with outlives the process. The plugin-ALC
    /// one ends in the same "still alive; restart the process" words and is not one of them.
    /// </summary>
    [Fact]
    public void Only_the_previous_generation_refusal_is_permanent()
    {
        const string previousAlive =
            "Core reload refused: generation 44 (progenitor-core:44) is still alive; restart the process.";
        const string pluginAlc =
            "Core reload refused: plugin ALC progenitor-plugin:7 still alive; restart the process.";
        const string inFlight = "Core reload refused: a swap is already in progress.";
        const string threw = "failed gen=44 step=request oldRunning=True error=IOException: the file is in use";

        Assert.True(CoreWatchPolicy.ParkOnRefusal(previousAlive));
        Assert.False(CoreWatchPolicy.HoldOnRefusal(previousAlive));

        foreach (string transient in new[] { pluginAlc, inFlight, threw, "something new", "", null! })
        {
            Assert.False(CoreWatchPolicy.ParkOnRefusal(transient));
            Assert.True(CoreWatchPolicy.HoldOnRefusal(transient));
        }

        Assert.False(CoreWatchPolicy.ParkOnRefusal("scheduled gen=45"));
        Assert.False(CoreWatchPolicy.HoldOnRefusal("scheduled gen=45"));
    }

    /// <summary>
    /// R68 finding 1: the plugin-ALC latch clears itself in <c>PollPluginLatch</c> a few
    /// GCs later. Before FREL9 the first refusal parked the image and nothing ever asked
    /// again, so a good Core sat staged until somebody re-staged it or restarted.
    /// </summary>
    [Fact]
    public void A_plugin_alc_refusal_that_clears_on_the_next_poll_is_retried_and_the_swap_lands()
    {
        var utc = new DateTime(2026, 9, 6, 2, 13, 11, DateTimeKind.Utc);
        var staged = new CoreWatchPolicy.FileStamp(2112000, utc);
        var sim = new WatchSim
        {
            Answer = "Core reload refused: plugin ALC progenitor-plugin:7 still alive; restart the process.",
        };

        // Scan 1 sees the file for the first time (never settled); scan 2 asks and is refused.
        for (int i = 0; i < 4; i++) sim.Tick(exists: true, staged);
        Assert.Equal(1, sim.Requests);
        Assert.False(sim.Parked);          // held, not parked: it clears on its own
        Assert.True(sim.Held);

        // The weaks die; PollPluginLatch clears the latch and the next request is accepted.
        sim.Answer = "scheduled gen=45";
        // Three more scans are still inside the five-second backoff; the fourth is the retry.
        for (int i = 0; i < 4; i++) sim.Tick(exists: true, staged);

        Assert.Equal(2, sim.Requests);
        Assert.False(sim.Parked);
        Assert.False(sim.Held);
        sim.NotePromoteSucceeded();
        for (int i = 0; i < 10; i++) sim.Tick(exists: true, staged);
        Assert.Equal(2, sim.Requests);
    }

    /// <summary>
    /// Job FREL10 / R69 finding 6: a transient refusal that never clears must be bounded, and
    /// its retries must be free. FREL9's backoff alone left the watcher asking, re-reading two
    /// 2 MB images and re-hashing one every five seconds for the life of the process — 120
    /// requests, 120 inspections and 241 lines over the ten minutes below, and the same again
    /// for every ten minutes after, on a plugin-ALC latch that never dies (the upstream IL2CPP
    /// detour delegate cache is permanent by design, a known permanent pin).
    /// Now: one inspection for the bytes, twelve free retries over a minute, then a park.
    /// </summary>
    [Fact]
    public void A_transient_refusal_that_never_clears_is_retried_free_then_parked()
    {
        var utc = new DateTime(2026, 9, 6, 2, 13, 11, DateTimeKind.Utc);
        var staged = new CoreWatchPolicy.FileStamp(2112000, utc);
        var sim = new WatchSim
        {
            Answer = "Core reload refused: plugin ALC progenitor-plugin:7 still alive; restart the process.",
        };

        // Ten minutes of one-second scans.
        for (int i = 0; i < 600; i++) sim.Tick(exists: true, staged);

        // One first attempt plus TransientRetryLimit retries at the five-second backoff, and
        // then nothing at all: the park is what stops the tenth minute costing anything.
        Assert.Equal(1 + CoreWatchPolicy.TransientRetryLimit, sim.Requests);
        Assert.Equal(13, sim.Requests);
        // The bytes are read and hashed exactly once. Before FREL10 this was one full
        // inspection per retry.
        Assert.Equal(1, sim.Inspections);
        // Two lines for the one inspection (staged-sha, settled), one for the transient
        // refusal, one for the give-up. Every retry in between is silent.
        Assert.Equal(4, sim.LogLines);
        Assert.Equal(CoreWatchPolicy.TransientRetryLimit, sim.GaveUpAfter);
        Assert.False(sim.Held);
        Assert.True(sim.Parked);
        Assert.Contains("plugin ALC", sim.ParkedReason);

        // The sim is a mirror, so pin the two structures it mirrors in the file that is not
        // compiled into this suite: the retry counter feeding the give-up, and the reuse test
        // standing between a retry and ShouldRequestReload's reads.
        string watch = SourcePin.ReadFile("src", "Progenitor.Bootstrap", "CoreWatch.cs");
        SourcePin.AssertStatement(
            SourcePin.MethodBody(
                watch, "private static void Hold(CoreWatchPolicy.FileStamp stamp, string? reason, double now)"),
            "if (CoreWatchPolicy.ExhaustedTransientRetries(_heldRetries))");
        string tick = SourcePin.MethodBody(watch, "private static void TickCore()");
        SourcePin.AssertStatement(tick, "if (!CoreWatchPolicy.MayReuseInspection(");
    }

    /// <summary>
    /// Job FREL10: the bound is per stamp, so it never strands an image the operator has just
    /// re-staged — the park it ends in is the ordinary stamp-keyed one, and a fresh deploy
    /// buys a fresh inspection and a fresh minute of retries.
    /// </summary>
    [Fact]
    public void The_give_up_park_is_keyed_to_the_stamp_so_a_fresh_deploy_starts_over()
    {
        var utc = new DateTime(2026, 9, 6, 2, 13, 11, DateTimeKind.Utc);
        var first = new CoreWatchPolicy.FileStamp(2112000, utc);
        var sim = new WatchSim
        {
            Answer = "Core reload refused: plugin ALC progenitor-plugin:7 still alive; restart the process.",
        };
        for (int i = 0; i < 300; i++) sim.Tick(exists: true, first);
        Assert.True(sim.Parked);
        Assert.Equal(13, sim.Requests);
        Assert.Equal(1, sim.Inspections);

        // A fresh deploy. The latch has cleared by the time it lands.
        sim.Answer = "scheduled gen=45";
        var second = new CoreWatchPolicy.FileStamp(2113024, utc.AddMinutes(5));
        for (int i = 0; i < 5; i++) sim.Tick(exists: true, second);

        Assert.False(sim.Parked);
        Assert.Equal(14, sim.Requests);
        Assert.Equal(2, sim.Inspections);   // new bytes: inspected for real
        Assert.Equal(6, sim.LogLines);      // 4 from the first image, staged-sha + settled here
    }

    /// <summary>
    /// Job FREL10, the two policy decisions the tick is built from. The retry budget counts
    /// the refusals <em>after</em> the first, and an inspection is reusable only for the exact
    /// bytes it was taken from.
    /// </summary>
    [Fact]
    public void The_transient_retry_budget_is_a_minute_and_an_inspection_is_reusable_per_stamp()
    {
        Assert.Equal(12, CoreWatchPolicy.TransientRetryLimit);
        Assert.Equal(
            60.0,
            CoreWatchPolicy.TransientRetryLimit * CoreWatchPolicy.TransientRetrySeconds);
        Assert.False(CoreWatchPolicy.ExhaustedTransientRetries(0));
        Assert.False(CoreWatchPolicy.ExhaustedTransientRetries(11));
        Assert.True(CoreWatchPolicy.ExhaustedTransientRetries(12));
        Assert.True(CoreWatchPolicy.ExhaustedTransientRetries(13));

        var utc = new DateTime(2026, 9, 6, 2, 13, 11, DateTimeKind.Utc);
        var stamp = new CoreWatchPolicy.FileStamp(2112000, utc);
        Assert.True(CoreWatchPolicy.MayReuseInspection(stamp, fileExists: true, stamp));
        Assert.False(CoreWatchPolicy.MayReuseInspection(stamp, fileExists: false, stamp));
        Assert.False(CoreWatchPolicy.MayReuseInspection(inspected: null, fileExists: true, stamp));
        Assert.False(CoreWatchPolicy.MayReuseInspection(
            stamp, fileExists: true, new CoreWatchPolicy.FileStamp(2112001, utc)));
        Assert.False(CoreWatchPolicy.MayReuseInspection(
            stamp, fileExists: true, new CoreWatchPolicy.FileStamp(2112000, utc.AddSeconds(1))));

        string line = CoreWatchPolicy.FormatTransientGaveUp(
            12, "Core reload refused: plugin ALC progenitor-plugin:7 still alive; restart the process.");
        Assert.Contains("12 retries over 60 s", line);
        Assert.Contains("not clearing", line);
        Assert.Contains("a restart promotes it", line);
        Assert.Contains("plugin ALC", line);

        // Source pin: CoreWatch binds BepInEx, so it is not compiled into this suite. The
        // three properties the finding is about are structural — the retry is counted, the
        // exhausted count parks, and the reuse test guards the read.
        string watch = SourcePin.ReadFile("src", "Progenitor.Bootstrap", "CoreWatch.cs");
        string hold = SourcePin.MethodBody(
            watch, "private static void Hold(CoreWatchPolicy.FileStamp stamp, string? reason, double now)");
        SourcePin.AssertStatement(hold, "if (CoreWatchPolicy.ExhaustedTransientRetries(_heldRetries))");
        SourcePin.AssertStatement(hold, "Park(stamp, reason);");
        SourcePin.AssertStatement(hold, "_heldRetries++;");
        string tick = SourcePin.MethodBody(watch, "private static void TickCore()");
        SourcePin.AssertStatement(tick, "if (!CoreWatchPolicy.MayReuseInspection(");
    }

    /// <summary>
    /// R68 finding 1, second half: <c>TickCore</c> read <c>busy</c> once at the top and handed
    /// that stale value to the Core-less path, which runs after the staged arm may have
    /// scheduled a cascade — so one tick could schedule a swap and then hold the recovery path
    /// on a <c>RefuseAlreadyReloading</c> it caused itself. The flag is now read inside the
    /// Core-less path, which is why it takes no such parameter.
    /// </summary>
    [Fact]
    public void The_core_less_path_reads_the_reloading_flag_after_the_staged_request_of_the_same_tick()
    {
        // The value read at the top of the tick, before the staged arm scheduled anything.
        Assert.True(CoreWatchPolicy.MayTryCoreLessLiveInstall(
            enabled: true, hostedChild: false, hostingPrivateGame: false, alreadyReloadingNow: false));
        // ReloadDriver.Request set the hold the moment the staged arm scheduled the cascade.
        Assert.False(CoreWatchPolicy.MayTryCoreLessLiveInstall(
            enabled: true, hostedChild: false, hostingPrivateGame: false, alreadyReloadingNow: true));
        Assert.False(CoreWatchPolicy.MayTryCoreLessLiveInstall(
            enabled: false, hostedChild: false, hostingPrivateGame: false, alreadyReloadingNow: false));
        Assert.False(CoreWatchPolicy.MayTryCoreLessLiveInstall(
            enabled: true, hostedChild: true, hostingPrivateGame: false, alreadyReloadingNow: false));
        Assert.False(CoreWatchPolicy.MayTryCoreLessLiveInstall(
            enabled: true, hostedChild: false, hostingPrivateGame: true, alreadyReloadingNow: false));

        // Source pin: CoreWatch binds BepInEx, so it is not compiled into this suite. The
        // regression is a signature — the tick handing its own stale `busy` down — so the pin
        // is over the signature and over the fresh read inside the body.
        string watch = SourcePin.ReadFile("src", "Progenitor.Bootstrap", "CoreWatch.cs");
        SourcePin.AssertStatement(
            watch,
            "private static void TryRequestCoreLessLiveInstall(bool enabled, bool hosted, bool hosting, double now)");
        string body = SourcePin.MethodBody(
            watch,
            "private static void TryRequestCoreLessLiveInstall(bool enabled, bool hosted, bool hosting, double now)");
        SourcePin.AssertStatement(
            body,
            "if (!CoreWatchPolicy.MayTryCoreLessLiveInstall(");
        Assert.Contains("CoreLoader.IsReloading", body, StringComparison.Ordinal);

        string tick = SourcePin.MethodBody(watch, "private static void TickCore()");
        SourcePin.AssertStatement(tick, "TryRequestCoreLessLiveInstall(enabled, hosted, hosting, now);");
        SourcePin.AssertNoStatementContaining(tick, "TryRequestCoreLessLiveInstall(enabled, hosted, hosting, busy)");
    }

    /// <summary>
    /// R68 finding 6: a Core-less process with no <c>.next</c> on disk parks on the live
    /// image, and the staged path had already written <c>parked = false</c> earlier in the
    /// same tick. Both parks now feed the one dump field.
    /// </summary>
    [Fact]
    public void The_watch_dump_shows_the_core_less_park_too()
    {
        Assert.True(CoreWatchPolicy.ParkedForDump(stagedParked: false, liveParked: true));
        Assert.True(CoreWatchPolicy.ParkedForDump(stagedParked: true, liveParked: false));
        Assert.False(CoreWatchPolicy.ParkedForDump(stagedParked: false, liveParked: false));
        Assert.Equal(
            "staged",
            CoreWatchPolicy.ParkReasonForDump(stagedParked: true, "staged", "live"));
        Assert.Equal(
            "live",
            CoreWatchPolicy.ParkReasonForDump(stagedParked: false, "staged", "live"));

        const string liveReason =
            "Core reload refused: generation 44 (progenitor-core:44) is still alive; restart the process.";
        CoreReloadStatus.SetWatch(
            enabled: true, pending: false, hostedChild: false, hostingPrivateGame: false,
            inFlight: false, previousAlive: true);
        CoreReloadStatus.SetWatchParked(
            CoreWatchPolicy.ParkedForDump(stagedParked: false, liveParked: true),
            CoreWatchPolicy.ParkReasonForDump(stagedParked: false, stagedReason: null, liveReason: liveReason));

        var dump = CoreReloadStatus.ToDump(liveGeneration: 0, liveAlcName: null, protocol: 1);
        var watch = dump.GetType().GetProperty("watch")!.GetValue(dump)!;
        var wt = watch.GetType();
        Assert.Equal(false, wt.GetProperty("pendingNext")!.GetValue(watch));
        Assert.Equal(true, wt.GetProperty("parked")!.GetValue(watch));
        Assert.Contains("still alive", (string)wt.GetProperty("parkedReason")!.GetValue(watch)!);
    }

    [Fact]
    public void Held_on_stamp_expires_on_the_deadline_and_on_a_byte_change()
    {
        var utc = new DateTime(2026, 9, 6, 2, 13, 11, DateTimeKind.Utc);
        var stamp = new CoreWatchPolicy.FileStamp(2112000, utc);
        double retryAt = CoreWatchPolicy.NextRetryAt(100.0);
        Assert.Equal(100.0 + CoreWatchPolicy.TransientRetrySeconds, retryAt);
        Assert.True(CoreWatchPolicy.HeldOnStamp(stamp, true, stamp, 101.0, retryAt));
        Assert.False(CoreWatchPolicy.HeldOnStamp(stamp, true, stamp, retryAt, retryAt));
        Assert.False(CoreWatchPolicy.HeldOnStamp(stamp, false, stamp, 101.0, retryAt));
        Assert.False(CoreWatchPolicy.HeldOnStamp(
            stamp, true, new CoreWatchPolicy.FileStamp(2112001, utc), 101.0, retryAt));
        Assert.False(CoreWatchPolicy.HeldOnStamp(held: null, true, stamp, 101.0, retryAt));

        Assert.True(CoreWatchPolicy.ShouldLogRefusal(null, "a"));
        Assert.False(CoreWatchPolicy.ShouldLogRefusal("a", "a"));
        Assert.True(CoreWatchPolicy.ShouldLogRefusal("a", "b"));
        Assert.Contains("retrying every", CoreWatchPolicy.FormatTransientRefusal("plugin ALC x still alive"));
    }

    [Fact]
    public void Parked_on_stamp_needs_the_file_and_the_exact_length_and_write_time()
    {
        var utc = new DateTime(2026, 9, 6, 2, 13, 11, DateTimeKind.Utc);
        var stamp = new CoreWatchPolicy.FileStamp(2112000, utc);
        Assert.True(CoreWatchPolicy.ParkedOnStamp(stamp, fileExists: true, stamp));
        Assert.False(CoreWatchPolicy.ParkedOnStamp(stamp, fileExists: false, stamp));
        Assert.False(CoreWatchPolicy.ParkedOnStamp(parked: null, fileExists: true, stamp));
        Assert.False(CoreWatchPolicy.ParkedOnStamp(
            stamp, fileExists: true, new CoreWatchPolicy.FileStamp(2112001, utc)));
        Assert.False(CoreWatchPolicy.ParkedOnStamp(
            stamp, fileExists: true, new CoreWatchPolicy.FileStamp(2112000, utc.AddSeconds(1))));
    }

    [Fact]
    public void The_watch_dump_says_whether_the_staged_file_is_parked()
    {
        CoreReloadStatus.SetWatch(
            enabled: true, pending: true, hostedChild: false, hostingPrivateGame: false,
            inFlight: false, previousAlive: true);
        CoreReloadStatus.SetWatchParked(true, "generation 44 is still alive; restart the process");
        var dump = CoreReloadStatus.ToDump(liveGeneration: 44, liveAlcName: "progenitor-core:44", protocol: 1);
        var watch = dump.GetType().GetProperty("watch")!.GetValue(dump)!;
        var wt = watch.GetType();
        Assert.Equal(true, wt.GetProperty("pendingNext")!.GetValue(watch));
        Assert.Equal(true, wt.GetProperty("parked")!.GetValue(watch));
        Assert.Contains("still alive", (string)wt.GetProperty("parkedReason")!.GetValue(watch)!);

        CoreReloadStatus.SetWatchParked(false, "generation 44 is still alive");
        var dump2 = CoreReloadStatus.ToDump(liveGeneration: 44, liveAlcName: "progenitor-core:44", protocol: 1);
        var watch2 = dump2.GetType().GetProperty("watch")!.GetValue(dump2)!;
        Assert.Equal(false, watch2.GetType().GetProperty("parked")!.GetValue(watch2));
        Assert.Equal("", watch2.GetType().GetProperty("parkedReason")!.GetValue(watch2));
    }

    [Fact]
    public void Failed_install_restore_records_restored_outcome_and_summary()
    {
        var result = new CoreReloadResult(
            ok: false,
            refused: false,
            oldGenerationRunning: true,
            generation: 2,
            collected: true,
            step: CoreReloadOrchestrator.StepUnfreeze,
            summary: CoreReloadPolicy.FormatFailedInstallRestored(3, "protocol mismatch", 2),
            outcome: CoreReloadStatus.OutcomeRestored);
        CoreReloadStatus.Record(result, "watch");
        string line = CoreReloadStatus.FormatLastReload();
        Assert.Contains("outcome=restored", line);
        Assert.Contains("no restart needed", line);
        Assert.DoesNotContain("still alive", line);

        var dump = CoreReloadStatus.ToDump(liveGeneration: 2, liveAlcName: "progenitor-core:2", protocol: 1);
        var last = dump.GetType().GetProperty("lastReload")!.GetValue(dump)!;
        var lt = last.GetType();
        Assert.Equal(2, lt.GetProperty("gen")!.GetValue(last));
        Assert.Equal("restored", lt.GetProperty("outcome")!.GetValue(last));
        Assert.Equal(
            "install of generation 3 failed (protocol mismatch); generation 2 restored; no restart needed",
            lt.GetProperty("reason")!.GetValue(last));
        Assert.NotNull(dump.GetType().GetProperty("watch"));
        Assert.NotNull(dump.GetType().GetProperty("pinDiag"));
        Assert.Equal(2, dump.GetType().GetProperty("generation")!.GetValue(dump));
    }
}
