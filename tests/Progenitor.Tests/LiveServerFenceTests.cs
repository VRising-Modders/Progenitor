using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Progenitor.Install;
using Progenitor.WorldRestore;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Review 2026-10-08 (M3, L1, and the fence matrix asked for with M1/M2): the live-server fence, exercised against a
/// temporary stand-in for the live root (<see cref="WorldRestorePaths.LiveRootForTests"/>, per thread). Junctions,
/// symbolic links, 8.3 names, <c>\\?\</c>, administrative shares by name and address, quoted and <c>=</c> arguments,
/// relative arguments, and a live path in a launch record or a running process. Nothing is started or signaled.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class LiveServerFenceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _live;
    private readonly string _test;

    public LiveServerFenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-fence-" + Guid.NewGuid().ToString("N"));
        _live = Path.Combine(_dir, "LiveServerRootWithALongName");
        _test = Path.Combine(_dir, "TestServer");
        Directory.CreateDirectory(Path.Combine(_live, "save-data"));
        Directory.CreateDirectory(_test);
        WorldRestorePaths.LiveRootForTests = _live;
        WorldRestorePaths.ForgetLiveRootForms();
        ServerProcessControl.ResetForTests();
    }

    public void Dispose()
    {
        WorldRestorePaths.LiveRootForTests = null;
        WorldRestorePaths.ForgetLiveRootForms();
        ServerProcessControl.ResetForTests();
        try
        {
            foreach (string link in Directory.GetDirectories(_dir))
            {
                var info = new DirectoryInfo(link);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) info.Delete();
            }
            Directory.Delete(_dir, recursive: true);
        }
        catch { /* temp */ }
    }

    private static bool Junction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10000);
            return Directory.Exists(link);
        }
        catch { return false; }
    }

    [Fact]
    public void A_junction_into_the_live_root_is_fenced()
    {
        string link = Path.Combine(_dir, "LooksHarmless");
        Assert.True(Junction(link, _live));
        Assert.True(WorldRestorePaths.IsLiveServerPath(Path.Combine(link, "save-data")));
        Assert.True(WorldRestorePaths.IsLiveServerPath(link));
        Assert.False(WorldRestorePaths.IsLiveServerPath(_test));
    }

    [Fact]
    public void When_the_live_root_itself_is_a_junction_its_target_is_fenced()
    {
        string target = Path.Combine(_dir, "RealServerFolder");
        Directory.CreateDirectory(Path.Combine(target, "save-data"));
        string root = Path.Combine(_dir, "LiveAsJunction");
        Assert.True(Junction(root, target));
        WorldRestorePaths.LiveRootForTests = root;
        WorldRestorePaths.ForgetLiveRootForms();
        var forms = WorldRestorePaths.LiveRootForms();
        Assert.NotNull(forms);
        Assert.Contains(forms!, f => string.Equals(f, target, StringComparison.OrdinalIgnoreCase));
        Assert.True(WorldRestorePaths.IsLiveServerPath(Path.Combine(target, "save-data")));
        Assert.True(WorldRestorePaths.IsLiveServerPath(Path.Combine(root, "save-data")));
        Assert.False(WorldRestorePaths.IsLiveServerPath(_test));
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "WorldRestore", "WorldRestorePaths.cs");
        Assert.Contains("if (roots == null) return true;", src, StringComparison.Ordinal);
    }

    [SymlinkFact]
    public void A_symbolic_link_into_the_live_root_is_fenced_when_this_machine_can_make_one()
    {
        string link = Path.Combine(_dir, "SymLinked");
        Directory.CreateSymbolicLink(link, _live);
        Assert.True(WorldRestorePaths.IsLiveServerPath(Path.Combine(link, "save-data")));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);

    [ShortNameFact]
    public void An_8_3_spelling_of_the_live_root_is_fenced_when_the_volume_has_short_names()
    {
        var sb = new StringBuilder(1024);
        uint n = GetShortPathNameW(_live, sb, (uint)sb.Capacity);
        string shortPath = n > 0 ? sb.ToString() : _live;
        Assert.False(string.Equals(shortPath, _live, StringComparison.OrdinalIgnoreCase));
        Assert.True(WorldRestorePaths.IsLiveServerPath(Path.Combine(shortPath, "save-data")));
    }

    [Fact]
    public void Device_prefixes_and_this_machines_admin_shares_by_name_and_address_are_fenced()
    {
        string rest = _live.Substring(3);
        char drive = char.ToUpperInvariant(_live[0]);
        Assert.True(WorldRestorePaths.IsLiveServerPath(@"\\?\" + _live));
        Assert.True(WorldRestorePaths.IsLiveServerPath(@"\\.\" + _live));
        foreach (string host in new[] { "localhost", "127.0.0.1", Environment.MachineName, System.Net.Dns.GetHostName() })
            Assert.True(WorldRestorePaths.IsLiveServerPath(@"\\" + host + @"\" + drive + @"$\" + rest), host);
        Assert.False(WorldRestorePaths.IsLiveServerPath(@"\\some-other-host\" + drive + @"$\" + rest));
    }

    /// <summary>Review L1: an admin share by one of this machine's own LAN addresses.</summary>
    [LanAddressFact]
    public void This_machines_admin_share_by_its_own_address_is_fenced()
    {
        string rest = _live.Substring(3);
        char drive = char.ToUpperInvariant(_live[0]);
        string ip = LanAddressFactAttribute.FirstAddress()!;
        Assert.True(WorldRestorePaths.IsLiveServerPath(@"\\" + ip + @"\" + drive + @"$\" + rest), ip);
    }

    /// <summary>
    /// Final review (2026-10-08): a path that resolves onto this machine's admin share (a mapped drive to
    /// \\localhost\C$, or a link to \\&lt;own address&gt;\C$\...) is fenced, and so is a live root that resolves to one.
    /// </summary>
    [Fact]
    public void A_path_or_live_root_that_resolves_to_an_admin_share_is_fenced()
    {
        string rest = _live.Substring(3);
        char drive = char.ToUpperInvariant(_live[0]);
        WorldRestorePaths.FinalPathOfForTests = p =>
            string.Equals(p, _test, StringComparison.OrdinalIgnoreCase) ? @"\\?\UNC\127.0.0.1\" + drive + @"$\" + rest : p;
        try
        {
            Assert.True(WorldRestorePaths.IsLiveServerPath(Path.Combine(_test, "save-data")));

            string target = Path.Combine(_dir, "RealServerFolder");
            Directory.CreateDirectory(target);
            WorldRestorePaths.FinalPathOfForTests = p =>
                string.Equals(p, _live, StringComparison.OrdinalIgnoreCase) ? @"\\?\UNC\localhost\" + drive + @"$\" + target.Substring(3) : p;
            WorldRestorePaths.ForgetLiveRootForms();
            Assert.Contains(WorldRestorePaths.LiveRootForms()!, f => string.Equals(f, target, StringComparison.OrdinalIgnoreCase));
            Assert.True(WorldRestorePaths.IsLiveServerPath(Path.Combine(target, "x")));
            string src = SourcePin.ReadFile("src", "Progenitor.Core", "WorldRestore", "WorldRestorePaths.cs");
            Assert.Contains("return PlainFullPath(resolved);", src, StringComparison.Ordinal);
        }
        finally
        {
            WorldRestorePaths.FinalPathOfForTests = null;
            WorldRestorePaths.ForgetLiveRootForms();
        }
    }

    /// <summary>Final review: -persistentDataPath=value is read for the host settings too; a following flag is not a value.</summary>
    [Fact]
    public void Persistent_data_path_reads_both_spellings_and_never_a_following_flag()
    {
        Assert.Equal(@"C:\x\data", ServerProcessControl.PersistentDataPathOf(new[] { @"-persistentDataPath=C:\x\data" }, null));
        Assert.Equal(@"C:\x\data", ServerProcessControl.PersistentDataPathOf(new[] { "-persistentDataPath", "\"" + @"C:\x\data" + "\"" }, null));
        Assert.Null(ServerProcessControl.PersistentDataPathOf(new[] { "-persistentDataPath", "-saveName", "w" }, null));
        var candidates = ServerProcessControl.HostSettingsCandidatesFromArgs(new[] { "-persistentDataPath=" + Path.Combine(_test, "data") });
        Assert.Contains(Path.Combine(_test, "data", "Settings", "ServerHostSettings.json"), candidates);
    }

    [Fact]
    public void Quoted_equals_and_relative_arguments_are_fenced()
    {
        string save = Path.Combine(_live, "save-data");
        Assert.True(WorldRestorePaths.IsLiveServerCommandLine(new[] { "-persistentDataPath", "\"" + save + "\"" }));
        Assert.True(WorldRestorePaths.IsLiveServerCommandLine(new[] { "-persistentDataPath=" + save }));
        Assert.True(WorldRestorePaths.IsLiveServerCommandLine(new[] { "-persistentDataPath=\"" + save + "\"" }));
        // Relative to the working directory (review L1): from a folder beside the live root.
        Assert.True(WorldRestorePaths.IsLiveServerCommandLine(new[] { "-persistentDataPath", @"..\LiveServerRootWithALongName\save-data" }, _test));
        Assert.True(WorldRestorePaths.IsLiveServerCommandLine(new[] { "-persistentDataPath", "save-data" }, _live));
        Assert.False(WorldRestorePaths.IsLiveServerCommandLine(new[] { "-persistentDataPath", "save-data", "-saveName", "world1" }, _test));
    }

    [Fact]
    public void A_live_exe_working_directory_or_running_process_is_fenced()
    {
        string exe = Path.Combine(_test, "VRisingServer.exe");
        string liveExe = Path.Combine(_live, "VRisingServer.exe");
        Assert.True(ServerProcessControl.IsLiveServer(_test, new ServerLaunchRecord(liveExe, new[] { "-saveName", "x" }, _test, DateTime.UtcNow)));
        Assert.True(ServerProcessControl.IsLiveServer(_test, new ServerLaunchRecord(exe, new[] { "-saveName", "x" }, _live, DateTime.UtcNow)));
        Assert.True(ServerProcessControl.IsLiveServer(_test, process: new ServerProcessInfo(1, liveExe, "x")));
        Assert.True(ServerProcessControl.IsLiveServer(_test, process: new ServerProcessInfo(1, exe,
            "\"" + exe + "\" -persistentDataPath \"" + Path.Combine(_live, "save-data") + "\"")));
        Assert.False(ServerProcessControl.IsLiveServer(_test, process: new ServerProcessInfo(1, exe,
            "\"" + exe + "\" -persistentDataPath \"" + Path.Combine(_test, "save-data") + "\"")));
        // The cached answer (review L3) agrees.
        Assert.True(ServerProcessControl.IsLiveServerCached(_test, new ServerProcessInfo(1, liveExe, "x")));
        Assert.False(ServerProcessControl.IsLiveServerCached(_test, new ServerProcessInfo(1, exe, "x")));
    }

    /// <summary>Review M2: a start names its own save folder, and never the default one; a bare record is never written.</summary>
    [Fact]
    public void A_start_needs_its_own_persistent_data_path_and_never_the_default_one()
    {
        string exe = Path.Combine(_test, "VRisingServer.exe");
        File.WriteAllText(exe, "exe");
        Directory.CreateDirectory(Path.Combine(_test, "BepInEx"));
        string fallback = Path.Combine(_dir, "default-save");
        ServerProcessControl.DefaultSaveRootForTests = fallback;
        Assert.Equal(ServerProcessControl.ReasonNoPersistentDataPath,
            ServerProcessControl.StartRefusal(_test, new ServerLaunchRecord(exe, new[] { "-saveName", "x" }, _test, DateTime.UtcNow)));
        Assert.Equal(ServerProcessControl.ReasonDefaultSavePath,
            ServerProcessControl.StartRefusal(_test, new ServerLaunchRecord(exe, new[] { "-persistentDataPath", Path.Combine(fallback, "Saves") }, _test, DateTime.UtcNow)));
        Assert.Equal("", ServerProcessControl.StartRefusal(_test, new ServerLaunchRecord(exe, new[] { "-persistentDataPath=" + Path.Combine(_test, "data") }, _test, DateTime.UtcNow)));

        // No record over nothing from a bare process (no arguments read).
        Assert.Null(ServerProcessControl.WriteLaunchRecord(_test, new ServerProcessInfo(5, exe, "\"" + exe + "\"")));
        Assert.Null(ServerLaunchRecord.TryRead(_test));
    }

    /// <summary>Review M1: the local stop goes only to the folder's running server, bound to the joined port.</summary>
    [Fact]
    public void The_local_stop_needs_the_folders_server_bound_to_the_joined_port()
    {
        string exe = Path.Combine(_test, "VRisingServer.exe");
        Directory.CreateDirectory(Path.Combine(_test, "VRisingServer_Data", "StreamingAssets", "Settings"));
        File.WriteAllText(Path.Combine(_test, "VRisingServer_Data", "StreamingAssets", "Settings", "ServerHostSettings.json"), "{ \"Port\": 9976 }");
        ServerProcessControl.SubmitCommand = _ => true;
        ServerProcessControl.ConnectedServerPort = () => 9976;
        ServerProcessControl.ProgenitorHandshakeOk = () => true;
        Assert.Equal(9976, ServerProcessControl.HostPort(_test));

        ServerProcessInfo? running = null;
        ServerProcessControl.RunningProbe = _ => running;
        ServerProcessControl.UdpOwnerProbe = _ => new[] { 777 };
        Assert.False(ServerProcessControl.GracefulAvailable(_test, out string why));
        Assert.Equal(ServerProcessControl.ReasonNotRunning, why);

        running = new ServerProcessInfo(4242, exe, "x");
        Assert.False(ServerProcessControl.GracefulAvailable(_test, out why));
        Assert.Equal(ServerProcessControl.ReasonPortNotThisProcess(9976), why);

        ServerProcessControl.UdpOwnerProbe = _ => null;
        Assert.False(ServerProcessControl.GracefulAvailable(_test, out why));
        Assert.Equal(ServerProcessControl.ReasonPortOwnerUnknown, why);

        ServerProcessControl.UdpOwnerProbe = _ => new[] { 4242 };
        Assert.True(ServerProcessControl.GracefulAvailable(_test, out why), why);
    }

    /// <summary>Review L2: the server's own Steam app id, none of the client's; out of the client's job when allowed.</summary>
    [Fact]
    public void The_server_starts_with_its_own_steam_app_id_and_breaks_away_from_the_clients_job()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SteamAppId"] = "1604030x", ["SteamGameId"] = "1604030x", ["SteamOverlayGameId"] = "1", ["PATH"] = "p",
        };
        ServerProcessControl.CleanSteamEnvironment(env);
        Assert.Equal("1604030", env["SteamAppId"]);
        Assert.False(env.ContainsKey("SteamGameId"));
        Assert.False(env.ContainsKey("SteamOverlayGameId"));
        Assert.Equal("p", env["PATH"]);
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "ServerProcessControl.cs");
        Assert.Contains("CreateNewConsole | CreateUnicodeEnvironment | CreateBreakawayFromJob", src, StringComparison.Ordinal);
        Assert.Contains("CleanSteamEnvironment(env);", src, StringComparison.Ordinal);
    }

    /// <summary>Review L4: the start lock is a named mutex too, so another Core generation or process holding it refuses a start.</summary>
    [Fact]
    public void Another_holder_of_the_named_start_mutex_refuses_a_start()
    {
        ServerProcessControl.UseStartMutex = true;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var m = new Mutex(false, ServerProcessControl.StartMutexName);
            m.WaitOne();
            held.Set();
            release.Wait(10000);
            m.ReleaseMutex();
        });
        holder.Start();
        try
        {
            Assert.True(held.Wait(10000));
            ServerProcessControl.StartOverride = (_, _, _) => throw new InvalidOperationException("must not start");
            Assert.Equal(ServerProcessControl.RefusedLine(ServerProcessControl.ReasonStartInFlight), ServerProcessControl.Start(_test).Line);
            Assert.False(ServerProcessControl.StartInFlight);
        }
        finally
        {
            release.Set();
            holder.Join(10000);
            ServerProcessControl.UseStartMutex = false;
        }
    }
}

/// <summary>Skipped, with the reason, where this machine cannot create a symbolic link (Developer Mode or elevation).</summary>
internal sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "progenitor-symprobe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try { Directory.CreateSymbolicLink(Path.Combine(dir, "l"), dir); }
            finally { try { Directory.Delete(dir, true); } catch { /* temp */ } }
        }
        catch { Skip = "This machine cannot create symbolic links (needs Developer Mode or elevation); the junction tests cover the rule."; }
    }
}

/// <summary>Skipped, with the reason, where the temp volume makes no 8.3 short names.</summary>
internal sealed class ShortNameFactAttribute : FactAttribute
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);

    public ShortNameFactAttribute()
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "progenitor-shortnameprobe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var sb = new StringBuilder(1024);
                uint n = GetShortPathNameW(dir, sb, (uint)sb.Capacity);
                if (n == 0 || string.Equals(sb.ToString(), dir, StringComparison.OrdinalIgnoreCase))
                    Skip = "The temp volume makes no 8.3 short names; the final-path rule is covered by the junction tests.";
            }
            finally { try { Directory.Delete(dir); } catch { /* temp */ } }
        }
        catch { Skip = "8.3 short names could not be probed on this machine."; }
    }
}

/// <summary>Skipped, with the reason, where this machine has no IPv4 address besides loopback.</summary>
internal sealed class LanAddressFactAttribute : FactAttribute
{
    public LanAddressFactAttribute()
    {
        if (FirstAddress() == null) Skip = "This machine has no non-loopback IPv4 address to name an admin share by.";
    }

    internal static string? FirstAddress()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                foreach (var a in nic.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(a.Address))
                        return a.Address.ToString();
        }
        catch { /* none */ }

        return null;
    }
}
