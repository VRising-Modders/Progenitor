using System;
using System.Collections.Generic;
using Progenitor.Bootstrap;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job HOSTPROBE: the hosted-child liveness tracker that replaced the once-a-second
/// all-process scan on the main thread. A fake OS stands in for <c>HostedChildProbe</c>.
/// </summary>
public class HostedChildTrackerTests
{
    private const int Parent = 4242;
    private const string ChildPath = @"C:\Games\VRising\VRising_Server\VRisingServer.exe";
    private const string ChildArgv = "-parentPID 4242 -saveName progenitor-hosted";

    private sealed class FakeProcess
    {
        public string? Path;
        public string? Cmd;
        public string Name = HostedChildTracker.DefaultServerProcessName;
        public bool Exited;
        public bool Unreadable;
    }

    private sealed class FakeOs : IHostedChildOs
    {
        public readonly Dictionary<int, FakeProcess> ByPid = new();
        public readonly Dictionary<IntPtr, FakeProcess> ByHandle = new();
        public readonly HashSet<IntPtr> Open = new();
        public bool EnumerationFails;
        public bool PollUnknown;
        public int Enumerations;
        public int Opens;
        public int Polls;
        public bool OnMainThread;
        public int MainThreadScanCalls;
        private long _next = 100;

        public int[]? CandidatePids(IReadOnlyList<string> names)
        {
            if (OnMainThread) MainThreadScanCalls++;
            Enumerations++;
            if (EnumerationFails) return null;
            var list = new List<int>();
            foreach (var kv in ByPid)
            {
                if (kv.Value.Exited) continue;
                foreach (string n in names)
                    if (string.Equals(n, kv.Value.Name, StringComparison.OrdinalIgnoreCase)) { list.Add(kv.Key); break; }
            }
            return list.ToArray();
        }

        public bool TryOpen(int pid, out IntPtr handle, out string? path, out string? commandLine)
        {
            if (OnMainThread) MainThreadScanCalls++;
            Opens++;
            handle = IntPtr.Zero;
            path = null;
            commandLine = null;
            if (!ByPid.TryGetValue(pid, out var p) || p.Exited || p.Unreadable) return false;
            handle = new IntPtr(_next++);
            ByHandle[handle] = p;
            Open.Add(handle);
            path = p.Path;
            commandLine = p.Cmd;
            return true;
        }

        public HostedChildLiveness Poll(IntPtr handle)
        {
            Polls++;
            if (PollUnknown) return HostedChildLiveness.Unknown;
            // The handle names the process object, never the PID.
            return ByHandle[handle].Exited ? HostedChildLiveness.Exited : HostedChildLiveness.Running;
        }

        public void Close(IntPtr handle) => Open.Remove(handle);
    }

    /// <summary>Background work is queued here and run only when the test says so.</summary>
    private sealed class ManualScheduler
    {
        public readonly Queue<Action> Pending = new();
        public void Run(Action work) => Pending.Enqueue(work);
        public void RunAll(FakeOs os)
        {
            os.OnMainThread = false;
            while (Pending.Count > 0) Pending.Dequeue()();
            os.OnMainThread = true;
        }
    }

    private static (HostedChildTracker tracker, FakeOs os, ManualScheduler bg, List<string> warnings) Make()
    {
        var os = new FakeOs { OnMainThread = true };
        var bg = new ManualScheduler();
        var warnings = new List<string>();
        var tracker = new HostedChildTracker(os, bg.Run, Parent) { Warn = warnings.Add };
        return (tracker, os, bg, warnings);
    }

    private static FakeProcess Child() => new() { Path = ChildPath, Cmd = ChildArgv };

    [Fact]
    public void Main_thread_tick_never_scans_synchronously()
    {
        var (tracker, os, bg, _) = Make();
        os.ByPid[900] = Child();
        tracker.NoteSpawn(ChildPath);

        Assert.False(tracker.Tick());
        Assert.Equal(0, os.MainThreadScanCalls);
        Assert.Equal(0, os.Enumerations);
        Assert.Single(bg.Pending);
        Assert.True(tracker.ScanInFlight);

        // A second tick while the first resolve is in flight queues nothing more.
        Assert.False(tracker.Tick());
        Assert.Single(bg.Pending);

        bg.RunAll(os);
        Assert.Equal(1, os.Enumerations);
        Assert.False(tracker.ScanInFlight);

        // The main thread adopts the published child; from now on a tick is one poll.
        Assert.False(tracker.Tick());
        Assert.Equal(900, tracker.KnownPid);
        int enumerations = os.Enumerations, opens = os.Opens;
        for (int i = 0; i < 100; i++) Assert.False(tracker.Tick());
        Assert.Equal(enumerations, os.Enumerations);
        Assert.Equal(opens, os.Opens);
        Assert.Empty(bg.Pending);
        Assert.Equal(0, os.MainThreadScanCalls);
        Assert.True(tracker.Hosting);
    }

    [Fact]
    public void Known_child_exit_clears_the_flag_and_closes_the_handle()
    {
        var (tracker, os, bg, _) = Make();
        var child = Child();
        os.ByPid[900] = child;
        tracker.NoteSpawn(ChildPath);
        tracker.Tick();
        bg.RunAll(os);
        tracker.Tick();
        Assert.Equal(900, tracker.KnownPid);

        child.Exited = true;
        Assert.True(tracker.Tick());
        Assert.False(tracker.Hosting);
        Assert.Empty(os.Open);
        Assert.False(tracker.Tick());
    }

    [Fact]
    public void Failed_scan_keeps_the_flag_and_logs_once()
    {
        var (tracker, os, bg, warnings) = Make();
        os.EnumerationFails = true;
        tracker.NoteSpawn(ChildPath);
        for (int i = 0; i < 3; i++)
        {
            Assert.False(tracker.Tick());
            bg.RunAll(os);
        }
        Assert.False(tracker.Tick());
        Assert.True(tracker.Hosting);
        Assert.Single(warnings);
        Assert.Equal(CoreWatchPolicy.FormatHostedScanFailed(), warnings[0]);
    }

    [Fact]
    public void Unreadable_candidates_keep_the_flag()
    {
        var (tracker, os, bg, warnings) = Make();
        os.ByPid[900] = new FakeProcess { Unreadable = true };
        tracker.NoteSpawn(ChildPath);
        tracker.Tick();
        bg.RunAll(os);
        Assert.False(tracker.Tick());
        Assert.True(tracker.Hosting);
        Assert.Contains(CoreWatchPolicy.FormatHostedQueryFailed(), warnings);
    }

    [Fact]
    public void Completed_scan_without_a_match_clears_the_flag()
    {
        var (tracker, os, bg, _) = Make();
        // A stranger's dedicated server: same name, no hosted argv.
        os.ByPid[700] = new FakeProcess { Path = @"D:\Other\VRisingServer.exe", Cmd = "-saveName world1" };
        tracker.NoteSpawn(ChildPath);
        tracker.Tick();
        bg.RunAll(os);
        Assert.True(tracker.Tick());
        Assert.False(tracker.Hosting);
        Assert.Empty(os.Open);
    }

    [Fact]
    public void Completed_scan_with_no_candidate_clears_the_flag()
    {
        var (tracker, os, bg, _) = Make();
        tracker.NoteSpawn(ChildPath);
        tracker.Tick();
        bg.RunAll(os);
        Assert.True(tracker.Tick());
        Assert.False(tracker.Hosting);
    }

    [Fact]
    public void Live_dedicated_root_is_never_adopted()
    {
        var (tracker, os, bg, _) = Make();
        os.ByPid[700] = new FakeProcess { Path = @"C:\VRisingServer\VRisingServer.exe", Cmd = ChildArgv };
        tracker.NoteSpawn(ChildPath);
        tracker.Tick();
        bg.RunAll(os);
        Assert.True(tracker.Tick());
        Assert.Equal(0, tracker.KnownPid);
    }

    [Fact]
    public void Spawn_pid_hint_is_adopted_without_a_scan()
    {
        var (tracker, os, bg, _) = Make();
        os.ByPid[900] = Child();
        tracker.NoteSpawn(ChildPath);
        tracker.NoteSpawnPid(900);
        tracker.Tick();
        bg.RunAll(os);
        tracker.Tick();
        Assert.Equal(900, tracker.KnownPid);
        Assert.Equal(0, os.Enumerations);
        Assert.Equal(1, os.Opens);
    }

    [Fact]
    public void Pid_reuse_guard_hint_is_verified_and_handle_outlives_the_pid()
    {
        var (tracker, os, bg, _) = Make();
        // The hinted PID now belongs to an unrelated process: not adopted; the scan finds the child.
        os.ByPid[900] = new FakeProcess { Path = @"C:\Windows\notepad.exe", Cmd = "notepad.exe", Name = "notepad" };
        var child = Child();
        os.ByPid[901] = child;
        tracker.NoteSpawn(ChildPath);
        tracker.NoteSpawnPid(900);
        tracker.Tick();
        bg.RunAll(os);
        tracker.Tick();
        Assert.Equal(901, tracker.KnownPid);
        Assert.Equal(1, os.Enumerations);
        Assert.Single(os.Open);

        // The child exits and its PID is handed to a new process that even looks like a hosted
        // child. Liveness asks the held handle (the old process object), so it reports the exit.
        child.Exited = true;
        os.ByPid[901] = Child();
        Assert.True(tracker.Tick());
        Assert.False(tracker.Hosting);
    }

    [Fact]
    public void Unknown_poll_keeps_the_flag_and_reidentifies_off_thread()
    {
        var (tracker, os, bg, _) = Make();
        os.ByPid[900] = Child();
        tracker.NoteSpawn(ChildPath);
        tracker.Tick();
        bg.RunAll(os);
        tracker.Tick();
        os.PollUnknown = true;
        Assert.False(tracker.Tick());
        Assert.True(tracker.Hosting);
        Assert.Equal(0, tracker.KnownPid);
        Assert.Single(bg.Pending);
        Assert.Equal(0, os.MainThreadScanCalls);
    }

    [Fact]
    public void Dedicated_join_clear_discards_an_in_flight_result()
    {
        var (tracker, os, bg, _) = Make();
        os.ByPid[900] = Child();
        tracker.NoteSpawn(ChildPath);
        tracker.Tick();
        Assert.True(tracker.Clear());
        bg.RunAll(os);
        Assert.False(tracker.Tick());
        Assert.False(tracker.Hosting);
        Assert.Empty(os.Open);
        Assert.False(tracker.Clear());
    }

    [Fact]
    public void A_result_published_after_clear_is_flagged_and_released_by_a_not_hosting_tick()
    {
        // Review item (a): ProcessHost ticks a tracker that is not hosting only while HasPublished is true.
        var (tracker, os, bg, _) = Make();
        os.ByPid[900] = Child();
        tracker.NoteSpawn(ChildPath);
        tracker.Tick();
        tracker.Clear();
        bg.RunAll(os);
        Assert.True(tracker.HasPublished);
        Assert.Single(os.Open);
        Assert.False(tracker.Tick());
        Assert.False(tracker.HasPublished);
        Assert.Empty(os.Open);
    }

    [Fact]
    public void An_unreadable_candidate_keeps_the_flag_even_when_another_was_read()
    {
        // Review item (c): a scan that read one candidate (no match) but could not read another must not clear.
        var (tracker, os, bg, warnings) = Make();
        os.ByPid[800] = new FakeProcess { Path = @"D:\Other\VRisingServer.exe", Cmd = "-persistentDataPath other" };
        os.ByPid[801] = new FakeProcess { Unreadable = true };
        tracker.NoteSpawn(ChildPath);
        tracker.Tick();
        bg.RunAll(os);
        Assert.False(tracker.Tick());
        Assert.True(tracker.Hosting);
        Assert.Contains(CoreWatchPolicy.FormatHostedQueryFailed(), warnings);
    }

    [Fact]
    public void The_reported_child_gone_clears_even_with_unreadable_candidates()
    {
        // Review item (b): unreadable candidates alone would keep the flag forever; the spawn site's PID no longer listed
        // means the reported child is gone.
        var (tracker, os, bg, _) = Make();
        var child = Child();
        os.ByPid[900] = child;
        os.ByPid[801] = new FakeProcess { Unreadable = true };
        tracker.NoteSpawn(ChildPath);
        tracker.NoteSpawnPid(900);
        tracker.Tick();
        bg.RunAll(os);
        tracker.Tick();
        Assert.Equal(900, tracker.KnownPid);

        // The handle cannot be asked any more: re-identify. The child is gone; 801 still cannot be read.
        os.PollUnknown = true;
        Assert.False(tracker.Tick());
        os.PollUnknown = false;
        child.Exited = true;
        bg.RunAll(os);
        Assert.True(tracker.Tick());
        Assert.False(tracker.Hosting);
    }

    [Fact]
    public void Without_a_reported_pid_unreadable_candidates_still_keep_the_flag()
    {
        var (tracker, os, bg, _) = Make();
        os.ByPid[801] = new FakeProcess { Unreadable = true };
        tracker.NoteSpawn(ChildPath);
        tracker.Tick();
        bg.RunAll(os);
        Assert.False(tracker.Tick());
        Assert.True(tracker.Hosting);
    }

    [Fact]
    public void A_new_spawn_drops_the_previous_generation()
    {
        var (tracker, os, bg, _) = Make();
        os.ByPid[900] = Child();
        tracker.NoteSpawn(ChildPath);
        tracker.Tick();
        bg.RunAll(os);
        // Stale result from generation 1 arrives after a new spawn: it is closed, not adopted.
        tracker.NoteSpawn(ChildPath);
        Assert.False(tracker.Tick());
        Assert.Equal(0, tracker.KnownPid);
        Assert.Single(bg.Pending);
        bg.RunAll(os);
        tracker.Tick();
        Assert.Equal(900, tracker.KnownPid);
        Assert.Single(os.Open);
    }

    [Fact]
    public void Candidate_names_add_the_spawned_image_name()
    {
        Assert.Equal(new[] { "VRisingServer" }, HostedChildTracker.CandidateNames(ChildPath));
        Assert.Equal(new[] { "VRisingServer" }, HostedChildTracker.CandidateNames(null));
        Assert.Equal(new[] { "VRisingServer", "CustomServer" },
            HostedChildTracker.CandidateNames(@"D:\x\CustomServer.exe"));
    }

    [Fact]
    public void After_scan_policy_is_fail_closed()
    {
        Assert.True(CoreWatchPolicy.ChildStillRunningAfterScan(scanFailed: true, candidates: 0, processesQueried: 0, foundLive: false));
        Assert.False(CoreWatchPolicy.ChildStillRunningAfterScan(scanFailed: false, candidates: 0, processesQueried: 0, foundLive: false));
        Assert.True(CoreWatchPolicy.ChildStillRunningAfterScan(scanFailed: false, candidates: 2, processesQueried: 0, foundLive: false));
        Assert.False(CoreWatchPolicy.ChildStillRunningAfterScan(scanFailed: false, candidates: 2, processesQueried: 1, foundLive: false));
        Assert.True(CoreWatchPolicy.ChildStillRunningAfterScan(scanFailed: false, candidates: 2, processesQueried: 1, foundLive: true));

        // The review follow-ups: an unreadable candidate keeps the flag; the reported child no longer listed clears it.
        Assert.True(CoreWatchPolicy.ChildStillRunningAfterScan(false, 2, 1, false, queryFailed: true, spawnPid: 0, spawnPidListed: false));
        Assert.False(CoreWatchPolicy.ChildStillRunningAfterScan(false, 2, 2, false, queryFailed: false, spawnPid: 0, spawnPidListed: false));
        Assert.False(CoreWatchPolicy.ChildStillRunningAfterScan(false, 2, 0, false, queryFailed: true, spawnPid: 900, spawnPidListed: false));
        Assert.True(CoreWatchPolicy.ChildStillRunningAfterScan(false, 2, 1, false, queryFailed: true, spawnPid: 900, spawnPidListed: true));
        Assert.True(CoreWatchPolicy.ChildStillRunningAfterScan(true, 0, 0, false, false, 900, false));
        Assert.False(CoreWatchPolicy.ChildStillRunningAfterScan(false, 0, 0, false, false, 900, false));
    }
}
