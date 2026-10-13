using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// REMOTEUNREACH1 (live 2026-10-03): <c>[Install] TreatLocalServerAsRemote</c> edited in the running
/// game's cfg did not take effect until a Core hot swap (BepInEx reads the file once). It is now read
/// off the file when the file changes.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class RemoteUnreachableCopyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-remoteunreach-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();

    public RemoteUnreachableCopyTests()
    {
        Directory.CreateDirectory(_dir);
        TreatLocalServerAsRemote.ResetForTests();
        InstallLog.TestSink = (line, _) => { lock (_log) _log.Add(line); };
    }

    public void Dispose()
    {
        TreatLocalServerAsRemote.ResetForTests();
        InstallLog.TestSink = null;
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    private static string Cfg(string value) =>
        "## Settings file was created by plugin Progenitor\n\n[Install]\n\n## Test / advanced.\n# Setting type: Boolean\n# Default value: false\n"
        + "TreatLocalServerAsRemote = " + value + "\n\n[Mods]\n\nTreatLocalServerAsRemote = nonsense\n";

    [Fact]
    public void The_key_is_read_from_the_install_section_only()
    {
        Assert.True(TreatLocalServerAsRemote.ParseKey(Cfg("true")));
        Assert.False(TreatLocalServerAsRemote.ParseKey(Cfg("false")));
        Assert.Null(TreatLocalServerAsRemote.ParseKey("[Install]\nMaxDownloadMB = 128\n"));
        Assert.Null(TreatLocalServerAsRemote.ParseKey("[Mods]\nTreatLocalServerAsRemote = true\n"));
        Assert.Null(TreatLocalServerAsRemote.ParseKey(Cfg("maybe")));
        Assert.True(TreatLocalServerAsRemote.ParseKey("[Install]\r\nTreatLocalServerAsRemote = True\r\n"));
    }

    [Fact]
    public void An_edit_to_the_running_games_cfg_is_seen_without_a_restart()
    {
        TreatLocalServerAsRemote.RecheckInterval = TimeSpan.Zero;
        string path = Path.Combine(_dir, "progenitor.cfg");
        File.WriteAllText(path, Cfg("false"));
        var t0 = new DateTime(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, t0);
        Assert.False(TreatLocalServerAsRemote.FromFile(path, t0));

        File.WriteAllText(path, Cfg("true"));
        File.SetLastWriteTimeUtc(path, t0.AddSeconds(5));
        Assert.True(TreatLocalServerAsRemote.FromFile(path, t0.AddSeconds(6)));
        Assert.Contains(_log, l => l.Contains("TreatLocalServerAsRemote is now true"));

        File.WriteAllText(path, Cfg("false"));
        File.SetLastWriteTimeUtc(path, t0.AddSeconds(10));
        Assert.False(TreatLocalServerAsRemote.FromFile(path, t0.AddSeconds(11)));
        // Revert: reading the bound entry only keeps the value the game started with (live: 30+ s, then a Core swap).
    }

    [Fact]
    public void The_file_is_looked_at_no_more_than_once_a_second()
    {
        string path = Path.Combine(_dir, "progenitor.cfg");
        File.WriteAllText(path, Cfg("false"));
        var t0 = new DateTime(2026, 10, 3, 18, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, t0);
        Assert.False(TreatLocalServerAsRemote.FromFile(path, t0));

        File.WriteAllText(path, Cfg("true"));
        File.SetLastWriteTimeUtc(path, t0.AddSeconds(1));
        Assert.False(TreatLocalServerAsRemote.FromFile(path, t0.AddMilliseconds(200)));
        Assert.True(TreatLocalServerAsRemote.FromFile(path, t0.AddSeconds(2)));
    }
}
