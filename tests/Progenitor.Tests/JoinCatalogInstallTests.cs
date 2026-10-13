using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// JOINFLOW2: the join screens' Thunderstore installer on the Install tab's own machinery, game-free, with the real
/// HotDemo package served from a local index (as <c>[Install] LocalIndexPath</c> does on the test rig): the exact
/// version, the refusals (gone version, not listed, not Thunderstore, NSFW, Thunderstore off, wrong plugin, server
/// only, managed elsewhere), the write into <c>progenitor-pending</c> with its ledger row, and the download progress.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class JoinCatalogInstallTests : IDisposable
{
    private const string Key = "Progenitor-HotDemo";
    private const string PluginId = "progenitor.hotdemo";
    private readonly string _base = Path.Combine(Path.GetTempPath(), "progenitor-joininstall-" + Guid.NewGuid().ToString("N"));

    public JoinCatalogInstallTests()
    {
        Directory.CreateDirectory(Local);
        Directory.CreateDirectory(ClientBep);
        File.Copy(FixtureZip, Path.Combine(Local, "Progenitor-HotDemo-0.1.0.zip"));
        File.WriteAllText(Path.Combine(Local, LocalIndexSource.IndexFileName), IndexJson());
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string FixtureZip => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Progenitor-HotDemo-0.1.0.zip");
    private string Local => Path.Combine(_base, "dummy");
    private string ClientGame => Path.Combine(_base, "VRising");
    private string ClientBep => Path.Combine(ClientGame, "BepInEx");

    private string IndexJson(bool nsfw = false)
    {
        string url = new Uri(Path.Combine(Local, "Progenitor-HotDemo-0.1.0.zip")).AbsoluteUri;
        return "[{\"name\":\"HotDemo\",\"full_name\":\"Progenitor-HotDemo\",\"owner\":\"Progenitor\",\"package_url\":\"\","
            + "\"has_nsfw_content\":" + (nsfw ? "true" : "false") + ","
            + "\"versions\":[{\"name\":\"HotDemo\",\"full_name\":\"Progenitor-HotDemo-0.1.0\",\"description\":\"d\",\"version_number\":\"0.1.0\","
            + "\"dependencies\":[],\"download_url\":\"" + url + "\",\"downloads\":0,\"date_created\":\"2026-09-13T02:14:35Z\","
            + "\"is_active\":true,\"file_size\":4607}]}]";
    }

    private sealed class NoTransport : ICatalogTransport
    {
        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network in this test");
    }

    private JoinPackageIndex Index(string? fetched = null) => new(
        _ => Task.FromResult(fetched == null
            ? new CatalogTextFetchResult(false, "offline", null, 0, 0)
            : new CatalogTextFetchResult(true, null, fetched, fetched.Length, 200)),
        () => null,
        () => Local);

    private JoinCatalogInstaller Installer(IReadOnlyList<JoinInstallRequest> batch, JoinInstallRules? rules = null, string? fetched = null) =>
        new(() => new InstallCatalogService(ClientBep, new NoTransport(), localIndexPath: Local, registerForBridge: false),
            Index(fetched), batch, rules);

    private static JoinInstallRequest Req(string version, JoinInstallKind kind = JoinInstallKind.Install, string modId = PluginId, string package = Key) =>
        new(modId, "Hot Demo", package, version, kind, kind != JoinInstallKind.Recommended, modId: modId);

    private static async Task<(JoinInstallOutcome Outcome, List<string> Phases)> Run(JoinCatalogInstaller installer, JoinInstallRequest request)
    {
        var phases = new List<string>();
        var outcome = await installer.InstallAsync(request, (p, pct) => { lock (phases) phases.Add(JoinText.InstallPhaseName(p) + (pct >= 0 ? ":" + pct : "")); },
            CancellationToken.None);
        return (outcome, phases);
    }

    [Fact]
    public async Task The_exact_version_installs_through_the_install_tab_and_is_ledgered_for_the_next_start()
    {
        var request = Req("0.1.0");
        var (outcome, phases) = await Run(Installer(new[] { request }), request);
        Assert.True(outcome.Phase == JoinInstallPhase.AfterRestart, outcome.Reason);
        Assert.Equal(PluginId, outcome.PluginId);
        Assert.EndsWith(Path.Combine("progenitor-pending", "index", PluginId + ".dll"), outcome.Path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(outcome.Path));
        var row = Assert.Single(ServerInstallLedger.TryRead(ClientGame)!.Files);
        Assert.Equal("package:" + Key + "@0.1.0", row.Source);
        Assert.Equal(new[] { "resolving", "downloading:0", "installing" }, phases);
    }

    [Fact]
    public async Task A_version_thunderstore_no_longer_serves_is_refused_plainly_before_any_download()
    {
        var request = Req("0.0.9");
        var (outcome, _) = await Run(Installer(new[] { request }), request);
        Assert.Equal(JoinInstallPhase.Refused, outcome.Phase);
        Assert.Equal("Progenitor-HotDemo 0.0.9 is no longer on Thunderstore (it has 0.1.0).", outcome.Reason);
        Assert.False(Directory.Exists(Path.Combine(ClientBep, "progenitor-pending")));

        // Offline and not in the local index: unknown, so it says Thunderstore could not be reached.
        var missing = Req("1.0.0", package: "Nobody-Nothing");
        var (offline, _) = await Run(Installer(new[] { missing }), missing);
        Assert.Equal(JoinInstallPhase.Failed, offline.Phase);
        Assert.Equal("Thunderstore could not be reached (offline).", offline.Reason);
        // Read, and not there: not listed.
        var (notListed, _) = await Run(Installer(new[] { missing }, fetched: "[]"), missing);
        Assert.Equal(JoinInstallPhase.Refused, notListed.Phase);
        Assert.Equal("Nobody-Nothing is not on Thunderstore's V Rising list.", notListed.Reason);
    }

    [Fact]
    public async Task A_package_that_is_not_the_mod_the_server_named_is_refused()
    {
        var request = Req("0.1.0", modId: "someone.else.mod");
        var (outcome, _) = await Run(Installer(new[] { request }), request);
        Assert.Equal(JoinInstallPhase.Refused, outcome.Phase);
        Assert.Equal("Progenitor-HotDemo holds progenitor.hotdemo, not someone.else.mod that the server named.", outcome.Reason);
        Assert.False(File.Exists(Path.Combine(ClientBep, "progenitor-pending", "index", PluginId + ".dll")));
    }

    [Fact]
    public async Task The_install_tabs_settings_and_refusals_hold()
    {
        var request = Req("0.1.0");
        var off = await Run(Installer(new[] { request }, new JoinInstallRules { ThunderstoreEnabled = () => false }), request);
        Assert.Equal(JoinInstallText.ThunderstoreOff, off.Outcome.Reason);

        File.WriteAllText(Path.Combine(Local, LocalIndexSource.IndexFileName), IndexJson(nsfw: true));
        var nsfw = await Run(Installer(new[] { request }), request);
        Assert.Equal(JoinInstallPhase.Refused, nsfw.Outcome.Phase);
        Assert.Equal(JoinInstallText.Nsfw, nsfw.Outcome.Reason);
        var allowed = await Run(Installer(new[] { request }, new JoinInstallRules { ShowNsfw = () => true }), request);
        Assert.Equal(JoinInstallPhase.AfterRestart, allowed.Outcome.Phase);

        // An update of a copy another tool installed is refused (it is updated with that tool).
        var update = Req("0.1.0", JoinInstallKind.Update);
        var managed = await Run(Installer(new[] { update }, new JoinInstallRules { ShowNsfw = () => true, ManagedElsewhere = _ => "r2modman" }), update);
        Assert.Equal(JoinInstallPhase.Refused, managed.Outcome.Phase);
        Assert.Equal("Installed by r2modman; update it there.", managed.Outcome.Reason);
    }

    /// <summary>
    /// JOINFLOW4: "Use version X" keeps the newer copy before it plans; a copy that cannot be kept is never replaced,
    /// and a moved copy is waited out (unloaded) before the plan reads the loader.
    /// </summary>
    [Fact]
    public async Task Use_version_keeps_the_newer_copy_first_and_never_replaces_one_it_could_not_keep()
    {
        var request = new JoinInstallRequest(PluginId, "Hot Demo", Key, "0.1.0", JoinInstallKind.UseVersion, true, replaces: "0.2.0", modId: PluginId);
        int kept = 0, polls = 0;
        bool loaded = true;
        var rules = new JoinInstallRules
        {
            KeepBeforeReplace = r => { kept++; return (true, "Your 0.2.0 is kept for other servers.", true); },
            PluginLoaded = _ => { polls++; return loaded && polls < 3; },
            Delay = (_, _) => Task.CompletedTask,
        };
        var ok = await Run(Installer(new[] { request }, rules), request);
        Assert.Equal(1, kept);
        Assert.True(polls >= 3);
        Assert.Equal(JoinInstallPhase.AfterRestart, ok.Outcome.Phase);

        // Kept, but still loaded after the wait: the server's version is never written beside it.
        var stuck = new JoinInstallRules
        {
            KeepBeforeReplace = _ => (true, "kept", true),
            PluginLoaded = _ => true,
            Delay = (_, _) => Task.CompletedTask,
            UnloadWait = TimeSpan.Zero,
        };
        var notUnloaded = await Run(Installer(new[] { request }, stuck), request);
        Assert.Equal(JoinInstallPhase.Refused, notUnloaded.Outcome.Phase);
        Assert.Contains("had not unloaded yet", notUnloaded.Outcome.Reason);

        var refuse = new JoinInstallRules { KeepBeforeReplace = _ => (false, "It is loaded from BepInEx/plugins (managed elsewhere).", false) };
        var refused = await Run(Installer(new[] { request }, refuse), request);
        Assert.Equal(JoinInstallPhase.Refused, refused.Outcome.Phase);
        Assert.Contains("could not be kept for other servers", refused.Outcome.Reason);
        Assert.Contains("so it was not replaced", refused.Outcome.Reason);

        // When the screens already said it cannot be kept (Keeps false), the stage 2 replacement goes ahead.
        var replacing = new JoinInstallRequest(PluginId, "Hot Demo", Key, "0.1.0", JoinInstallKind.UseVersion, true, replaces: "0.2.0", modId: PluginId)
        {
            Keeps = false,
            KeepReason = "It is loaded from BepInEx/plugins (managed elsewhere).",
        };
        var replaced = await Run(Installer(new[] { replacing }, refuse), replacing);
        Assert.Equal(JoinInstallPhase.AfterRestart, replaced.Outcome.Phase);
        Assert.Contains("is replaced by 0.1.0", JoinText.UseVersionWarning(replacing));
    }

    [Fact]
    public void Only_thunderstores_own_https_hosts_count_as_thunderstore()
    {
        Assert.True(JoinCatalogInstaller.IsThunderstoreUrl("https://thunderstore.io/package/download/A/B/1.0.0/"));
        Assert.True(JoinCatalogInstaller.IsThunderstoreUrl("https://gcdn.thunderstore.io/live/repository/packages/A-B-1.0.0.zip"));
        Assert.False(JoinCatalogInstaller.IsThunderstoreUrl("http://thunderstore.io/x"));
        Assert.False(JoinCatalogInstaller.IsThunderstoreUrl("https://thunderstore.io.evil.example/x"));
        Assert.False(JoinCatalogInstaller.IsThunderstoreUrl("https://evil.example/thunderstore.io"));
        Assert.False(JoinCatalogInstaller.IsThunderstoreUrl("file:///C:/x.zip"));
    }

    [Fact]
    public async Task A_fetched_listing_with_a_download_off_thunderstore_is_refused()
    {
        // No local index: the fetched index names a download on another host.
        File.Delete(Path.Combine(Local, LocalIndexSource.IndexFileName));
        string json = "[{\"name\":\"Bad\",\"full_name\":\"Evil-Bad\",\"owner\":\"Evil\",\"versions\":[{\"name\":\"Bad\",\"full_name\":\"Evil-Bad-1.0.0\","
            + "\"version_number\":\"1.0.0\",\"download_url\":\"https://evil.example/bad.zip\",\"is_active\":true,\"file_size\":10}]}]";
        var request = Req("1.0.0", modId: "evil.bad", package: "Evil-Bad");
        var (outcome, _) = await Run(Installer(new[] { request }, fetched: json), request);
        Assert.Equal(JoinInstallPhase.Refused, outcome.Phase);
        Assert.Equal(JoinInstallText.NotThunderstore, outcome.Reason);
    }

    [Fact]
    public async Task The_index_is_fetched_once_and_reused_and_its_failure_falls_back_to_the_cache()
    {
        int fetches = 0;
        var index = new JoinPackageIndex(_ =>
        {
            fetches++;
            return Task.FromResult(new CatalogTextFetchResult(false, "offline", null, 0, 0));
        }, () => "[]", () => null);
        var load = await index.LoadAsync(new Dictionary<string, string> { [Key] = "0.1.0" }, CancellationToken.None);
        Assert.Equal("[]", load.Json);
        Assert.Equal("offline", load.Error);
        Assert.Equal(1, fetches);

        var good = new JoinPackageIndex(_ => Task.FromResult(new CatalogTextFetchResult(true, null, IndexJson(), 1, 200)), () => null, () => null);
        var first = await good.LoadAsync(new Dictionary<string, string> { [Key] = "0.1.0" }, CancellationToken.None);
        await good.LoadAsync(new Dictionary<string, string> { [Key] = "0.1.0" }, CancellationToken.None);
        Assert.Equal(1, good.Fetches);
        Assert.Equal("0.1.0", first.Versions[Key].Matched);
    }

    [Fact]
    public async Task Download_progress_is_reported_per_row_through_the_transport()
    {
        var bytes = new byte[1000];
        var transport = new ProgressCatalogTransport(new BytesTransport(bytes));
        var seen = new List<int>();
        JoinDownloadProgress.Current = (read, total) => { lock (seen) seen.Add(JoinDownloadProgress.Percent(read, total)); };
        try
        {
            var fetcher = new CatalogFetcher(transport, new CatalogFetchOptions());
            var result = await fetcher.FetchTextAsync("https://thunderstore.io/x", 4096, CancellationToken.None);
            Assert.True(result.Ok, result.Error);
        }
        finally { JoinDownloadProgress.Current = null; }

        Assert.NotEmpty(seen);
        Assert.Equal(100, seen[seen.Count - 1]);
        Assert.Equal(-1, JoinDownloadProgress.Percent(5, null));
        Assert.Equal(62, JoinDownloadProgress.Percent(62, 100));
    }

    private sealed class BytesTransport : ICatalogTransport
    {
        private readonly byte[] _bytes;

        public BytesTransport(byte[] bytes) => _bytes = bytes;

        public Task<CatalogTransportResponse> SendAsync(CatalogTransportRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new CatalogTransportResponse(200, new MemoryStream(_bytes), null, _bytes.Length));
    }
}
