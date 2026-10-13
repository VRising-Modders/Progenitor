using System;
using System.Collections.Generic;
using Progenitor.Install;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Review 2026-10-07 (after the live freezes): a process scan, a command-line read or the folder facts never run on the
/// main thread. The frame reads cached answers, refreshed in the background; until the first arrives it reads none (the
/// safe default). Game-free; not live-verified.
/// </summary>
[Collection("ModManifest")]
public sealed class MainThreadScanTests : IDisposable
{
    private readonly List<Action> _queued = new();

    public MainThreadScanTests()
    {
        ServerProcessControl.ResetForTests();
        ServerProcessControl.RunningCache.Scheduler = a => _queued.Add(a);
        LocalFolderFactsCache.Clear();
        LocalFolderFactsCache.Scheduler = a => _queued.Add(a);
    }

    public void Dispose()
    {
        ServerProcessControl.OnMainThread = () => false;
        ServerProcessControl.RunningCache.Scheduler = null;
        ServerProcessControl.ResetForTests();
        ServerInstall.RunningServerProbe = null;
        LocalFolderFactsCache.Compute = null;
        LocalFolderFactsCache.Scheduler = null;
        LocalFolderFactsCache.Clear();
    }

    private void RunQueued()
    {
        var run = new List<Action>(_queued);
        _queued.Clear();
        bool main = ServerProcessControl.OnMainThread();
        ServerProcessControl.OnMainThread = () => false; // the thread pool
        try { foreach (var a in run) a(); }
        finally { ServerProcessControl.OnMainThread = () => main; }
    }

    [Fact]
    public void The_main_thread_reads_the_running_server_from_the_cache_and_never_scans()
    {
        int scans = 0;
        ServerInstall.RunningServerProbe = null;
        ServerProcessControl.OnMainThread = () => true;
        // FindRunningServerNow is the only scan; count it through the seam the scan honors.
        string root = @"C:\srv-" + Guid.NewGuid().ToString("N");
        Func<string, string?> probe = r => { scans++; return r + @"\VRisingServer.exe"; };

        Assert.Null(ServerProcessControl.Running(root));
        Assert.Equal(0, scans);
        Assert.Single(_queued);
        // A second read before the refresh ran starts no second refresh.
        Assert.Null(ServerProcessControl.Running(root));
        Assert.Single(_queued);

        ServerInstall.RunningServerProbe = probe;
        RunQueued();
        Assert.Equal(1, scans);
        ServerInstall.RunningServerProbe = null;
        Assert.Equal(root + @"\VRisingServer.exe", ServerProcessControl.Running(root)?.Path);
        Assert.Equal(1, scans);
        Assert.Empty(_queued);
        Assert.True(ServerProcessControl.RunningCache.Known(root));
    }

    [Fact]
    public void Folder_facts_are_computed_in_the_background_and_refreshed_on_a_join()
    {
        int computed = 0;
        LocalFolderFactsCache.Compute = r => { computed++; return new LocalFolderFactsCache.FolderFacts { Port = 9976, Running = true, Name = "Night Garden" }; };
        const string root = @"C:\srv";
        Assert.Null(LocalFolderFactsCache.Get(root));
        Assert.Equal(0, computed);
        RunQueued();
        Assert.Equal(1, computed);
        Assert.Equal(9976, LocalFolderFactsCache.Get(root)!.Port);
        Assert.Empty(_queued);

        LocalFolderFactsCache.RefreshAll();
        Assert.Equal("Night Garden", LocalFolderFactsCache.Get(root)!.Name); // the last facts serve meanwhile
        Assert.Single(_queued);
        RunQueued();
        Assert.Equal(2, computed);
    }

    [Fact]
    public void The_route_and_the_rejoin_read_the_folder_facts_from_the_cache()
    {
        string live = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "JoinFlowLive.cs");
        Assert.Contains("LocalFolderFactsCache.Get(root)", live, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerProcessControl.RunningProbe =", live, StringComparison.Ordinal);
        string control = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "ServerProcessControl.cs");
        Assert.Contains("if (OnMainThread()) return RunningCache.Get(serverRoot!);", control, StringComparison.Ordinal);
    }
}
