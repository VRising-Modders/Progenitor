using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// INSTPLUG1 (live 2026-10-03): Install on a third-party plain BepInEx plugin package read <c>Cannot install — the
/// package layout targets BepInEx/plugins/, patchers/ or core/; this menu will not write there</c>.
/// The package is the standard Thunderstore layout — the plugin at
/// <c>BepInEx/plugins/QStack/QStack.dll</c> — plus review paperwork: <c>SOURCE_REVIEW.md</c> at the
/// root and the plugin's source under <c>Source/</c>. The fixture is a generated package of that shape
/// (<c>tools/make-dummy-package.ps1 -Kind Plain -Paperwork</c>), with the same layout and file names.
/// </summary>
public sealed class InstallBepInExPluginsLayoutTests : IDisposable
{
    private const string Key = "QATeam-QStacks";
    private readonly string _base = Path.Combine(Path.GetTempPath(), "progenitor-instplug-" + Guid.NewGuid().ToString("N"));
    private readonly List<(string Line, bool Warn)> _log = new();

    public InstallBepInExPluginsLayoutTests()
    {
        Directory.CreateDirectory(ClientBep);
        InstallLog.TestSink = (line, warn) => { lock (_log) _log.Add((line, warn)); };
    }

    public void Dispose()
    {
        InstallLog.TestSink = null;
        try { Directory.Delete(_base, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string FixtureZip => Path.Combine(AppContext.BaseDirectory, "Fixtures", "QATeam-QStacks-1.0.1.zip");
    private string ClientGame => Path.Combine(_base, "VRising");
    private string ClientBep => Path.Combine(ClientGame, "BepInEx");

    private sealed class NoTransport : ICatalogTransport
    {
        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network in this test");
    }

    /// <summary>The live listing's index row (categories Client + Server, BepInExPack dependency).</summary>
    private static string IndexJson() =>
        "[{\"name\":\"QStacks\",\"full_name\":\"QATeam-QStacks\",\"owner\":\"QATeam\","
        + "\"package_url\":\"https://thunderstore.io/c/v-rising/p/QATeam/QStacks/\",\"is_deprecated\":false,"
        + "\"categories\":[\"Client\",\"Server\",\"Mods\"],"
        + "\"versions\":[{\"name\":\"QStacks\",\"full_name\":\"QATeam-QStacks-1.0.1\",\"version_number\":\"1.0.1\","
        + "\"description\":\"A generated plain BepInEx plugin.\","
        + "\"dependencies\":[\"BepInEx-BepInExPack_V_Rising-1.733.2\"],"
        + "\"download_url\":\"https://thunderstore.io/package/download/QATeam/QStacks/1.0.1/\","
        + "\"file_size\":91029,\"is_active\":true}]}]";

    private InstallCatalogService Catalog()
    {
        var catalog = new InstallCatalogService(ClientBep, new NoTransport(), null, null, null, false, null, null, Path.Combine(_base, "nolocal"), false);
        Assert.True(catalog.LoadIndexFromJson(IndexJson(), "stamp").Ok);
        return catalog;
    }

    [Fact]
    public void QStacks_installs_through_the_restart_route_into_the_menus_own_folder()
    {
        var catalog = Catalog();
        var inspected = catalog.InspectLocalZip(Key, FixtureZip);
        Assert.True(inspected.Ok, inspected.Error);
        Assert.True(inspected.Inspection!.OrdinaryApproved, inspected.Inspection.OrdinaryLayout?.Refusal);
        var plugin = inspected.Inspection.OrdinaryLayout!.Plugin!;
        Assert.Equal("BepInEx/plugins/QStack/QStack.dll", plugin.Destination);
        Assert.Equal("QStack.dll", plugin.FileName);

        var planned = catalog.Plan(Key, InstallTarget.Client);
        Assert.True(planned.Ok, planned.Error);
        Assert.Equal(InstallDecision.RestartClientRequired, planned.Plan!.Decision);
        Assert.Contains(InstallReasons.OrdinaryRestartRoute, planned.Plan.Reason);
        Assert.True(catalog.Approve(planned.Plan.PlanHash).Ok);

        var written = catalog.ExecuteToPending(planned.Plan.PlanHash);
        Assert.True(written.Ok, written.Error);
        Assert.False(written.HotLoaded);
        Assert.StartsWith(Path.Combine(ClientBep, ModManifestPending.PendingFolderName), written.PendingPath!);
        Assert.False(Directory.Exists(Path.Combine(ClientBep, "plugins")), "the menu wrote into BepInEx/plugins/");
        // Only the plugin is placed: no Source/, no paperwork.
        Assert.Single(Directory.GetFiles(Path.Combine(ClientBep, ModManifestPending.PendingFolderName), "*", SearchOption.AllDirectories));

        var row = Assert.Single(ServerInstallLedger.TryRead(ClientGame)!.Files);
        Assert.Equal(ServerInstallLedger.CategoryPending, row.Category);
        Assert.Equal("package:" + Key + "@1.0.1", row.Source);
        Assert.Equal(plugin.PluginGuid, row.PluginId);
        // Revert: treating SOURCE_REVIEW.md or Source/* as files to place refuses the whole package.
    }

    private string Repack(params (string Name, string Text)[] extra)
    {
        string path = Path.Combine(_base, "pkg-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var src = ZipFile.OpenRead(FixtureZip))
        using (var dst = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var e in src.Entries)
            {
                if (e.FullName.EndsWith("/", StringComparison.Ordinal)) continue;
                var copy = dst.CreateEntry(e.FullName);
                using var o = copy.Open();
                using var i = e.Open();
                i.CopyTo(o);
            }

            foreach (var (name, text) in extra)
            {
                var entry = dst.CreateEntry(name);
                using var o = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(text);
                o.Write(bytes, 0, bytes.Length);
            }
        }

        return path;
    }

    [Theory]
    [InlineData("BepInEx/patchers/QStack/Pre.txt")]
    [InlineData("patchers/Pre.txt")]
    [InlineData("BepInEx/core/Evil.txt")]
    [InlineData("core/Evil.txt")]
    [InlineData("BepInEx/config/QStack.cfg")]
    public void A_write_into_patchers_core_or_config_is_refused_and_named(string offending)
    {
        var catalog = Catalog();
        var inspected = catalog.InspectLocalZip(Key, Repack((offending, "x")));
        Assert.True(inspected.Ok, inspected.Error);
        Assert.False(inspected.Inspection!.OrdinaryApproved);
        var planned = catalog.Plan(Key, InstallTarget.Client);
        Assert.Equal(InstallDecision.UnknownBlocked, planned.Plan!.Decision);
        Assert.Contains(offending, planned.Plan.Reason);
        Assert.False(Directory.Exists(Path.Combine(ClientBep, ModManifestPending.PendingFolderName)));
    }

    [Fact]
    public void A_data_file_the_plugin_might_need_is_still_refused_by_name()
    {
        var catalog = Catalog();
        Assert.True(catalog.InspectLocalZip(Key, Repack(("BepInEx/plugins/QStack/stacks.json", "{}"))).Ok);
        var planned = catalog.Plan(Key, InstallTarget.Client);
        Assert.Equal(InstallDecision.UnknownBlocked, planned.Plan!.Decision);
        Assert.Contains("BepInEx/plugins/QStack/stacks.json", planned.Plan.Reason);
    }

    [Theory]
    [InlineData("bepinex/Plugins/QStack/QStack.pdb")]
    [InlineData("docs/usage.md")]
    [InlineData("Source/Extra.cs")]
    [InlineData("LICENSE.txt")]
    public void Paperwork_symbols_and_source_beside_the_plugin_are_ignored(string extra)
    {
        var catalog = Catalog();
        var inspected = catalog.InspectLocalZip(Key, Repack((extra, "x")));
        Assert.True(inspected.Inspection!.OrdinaryApproved, inspected.Inspection.OrdinaryLayout?.Refusal);
    }
}
