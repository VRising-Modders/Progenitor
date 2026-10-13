using System;
using System.IO;
using System.Linq;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// INSTALLSIDES2 item 7: abandoned <c>*.zip.&lt;guid&gt;.part</c> downloads in the staging <c>tmp</c>
/// folder are removed — old and unheld only; a running download, a zip the catalog still holds and
/// anything that is not a <c>.part</c> file stay.
/// </summary>
public sealed class StagingTempSweepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-sweep-" + Guid.NewGuid().ToString("N"));

    public StagingTempSweepTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Part(string dir, string name, DateTime writtenUtc, int bytes = 1024)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[bytes]);
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    [Fact]
    public void Old_unheld_parts_go_and_everything_else_stays()
    {
        var now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        string tmp = CatalogFetcher.TempDirectory(_root);
        Assert.EndsWith(Path.Combine("progenitor-staging", "index", "tmp"), tmp);
        string old1 = Part(tmp, "HotDemo-0.1.0.zip.01be22df25b147ab97d886e7f118dc3d.part", now.AddDays(-4), 8192);
        string old2 = Part(tmp, "AutoLatchGates-3.1.0.zip.03ea43e06f8b4c2d9c2f35a7675e47dc.part", now.AddHours(-3), 4096);
        string running = Part(tmp, "Almanacs-2.42.94.zip.0c9abbc8d3134f3ab24deab49b00f178.part", now.AddMinutes(-1));
        string held = Part(tmp, "Rowland-1.0.0.zip.10f118faa58848098cb9a622e743b11e.part", now.AddDays(-1));
        string notPart = Part(tmp, "notes.txt", now.AddDays(-30));
        string nested = Part(Path.Combine(tmp, "sub"), "Old-1.0.0.zip.110477d6cb174cc793fa396fd140e83c.part", now.AddDays(-30));

        var result = StagingTempSweep.Sweep(tmp, now, StagingTempSweep.DefaultMaxAge, new[] { held });

        Assert.False(File.Exists(old1));
        Assert.False(File.Exists(old2));
        Assert.True(File.Exists(running));
        Assert.True(File.Exists(held));
        Assert.True(File.Exists(notPart));
        Assert.True(File.Exists(nested));
        Assert.Equal(2, result.Removed.Count);
        Assert.Equal(8192 + 4096, result.RemovedBytes);
        Assert.Equal(2, result.Kept);
        Assert.Equal(0, result.Failed);
        string line = result.Describe(tmp);
        Assert.Contains("removed 2 abandoned download(s), 12 KB", line);
        Assert.Contains(Path.GetFileName(old1), line);
        Assert.Contains("kept 2", line);
    }

    [Fact]
    public void A_missing_or_empty_folder_is_an_empty_result()
    {
        var now = DateTime.UtcNow;
        Assert.Empty(StagingTempSweep.Sweep(Path.Combine(_root, "absent"), now, StagingTempSweep.DefaultMaxAge).Removed);
        Assert.Empty(StagingTempSweep.Sweep(null, now, StagingTempSweep.DefaultMaxAge).Removed);
        Assert.Empty(StagingTempSweep.Sweep("", now, StagingTempSweep.DefaultMaxAge).Removed);
    }

    [Fact]
    public void A_long_list_is_capped_in_the_log_line()
    {
        var now = new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        string tmp = CatalogFetcher.TempDirectory(_root);
        for (int i = 0; i < 20; i++) Part(tmp, "HotDemo-0.1.0.zip." + i.ToString("x32") + ".part", now.AddDays(-2), 100);
        var result = StagingTempSweep.Sweep(tmp, now, StagingTempSweep.DefaultMaxAge);
        Assert.Equal(20, result.Removed.Count);
        Assert.Empty(Directory.GetFiles(tmp));
        Assert.Contains("… 8 more", result.Describe(tmp));
    }

    [Fact]
    public void The_menu_runs_the_sweep_off_the_main_thread_once_per_catalog()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        string ensure = SourcePin.MethodBody(src, "private static void EnsureCatalog()");
        SourcePin.AssertUncommented(ensure, "System.Threading.Tasks.Task.Run(");
        SourcePin.AssertUncommented(ensure, "service.SweepAbandonedDownloads(DateTime.UtcNow)");
        SourcePin.AssertUncommented(ensure, "swept.Describe(service.StagingTempDirectory)");
    }
}
