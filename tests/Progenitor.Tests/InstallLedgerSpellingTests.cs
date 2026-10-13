using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// POLISH4: the server half of a Both install was invisible to the Install tab in a later session.
///
/// <para>Live proof (2026-09-13, deploy #76, client + local test server). HotDemo — listing
/// <c>Progenitor-HotDemo</c>, plugin <c>progenitor.hotdemo</c> — was installed to Both. The
/// local server's ledger row read, verbatim,
/// <c>{"path": "BepInEx/progenitor-pending/index/progenitor.hotdemo.dll", …, "source":
/// "package:progenitor.hotdemo@0.1.0"}</c>, and the file was there. Yet the Install row read
/// <c>halves {client: loaded, server: none}</c>: the source carried the <em>plugin id</em> and the
/// index-derived catalog entry of a fresh session carries no inspected manifest, so
/// <c>Manifest.Id</c> was empty and none of the four name spellings matched. POLISH3's per-half
/// Uninstall then reported <c>server: none</c> and did nothing; the pending file and the row stayed
/// and the Mods tab went on listing the mod as <c>pendload</c> under its GUID.</para>
///
/// <para>Each fact names what its removal breaks.</para>
/// </summary>
public sealed class InstallLedgerSpellingTests : IDisposable
{
    private const string Key = "Progenitor-HotDemo";
    private const string PluginId = "progenitor.hotdemo";
    private const string Version = "0.1.0";

    /// <summary>The live row's source, verbatim: the old plugin-id spelling.</summary>
    private const string OldSource = "package:" + PluginId + "@" + Version;

    private readonly string _temp;
    private readonly string _clientGame;
    private readonly string _serverGame;

    public InstallLedgerSpellingTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "progenitor-polish4-" + Guid.NewGuid().ToString("N"));
        _clientGame = Path.Combine(_temp, "client");
        _serverGame = Path.Combine(_temp, "server");
        Directory.CreateDirectory(ClientBep);
        Directory.CreateDirectory(ServerBep);
        ServerInstall.ResetHotSweepForTests();
    }

    public void Dispose()
    {
        UiDialog.Current?.Close();
        try { Directory.Delete(_temp, true); } catch { }
    }

    private string ClientBep => Path.Combine(_clientGame, "BepInEx");
    private string ServerBep => Path.Combine(_serverGame, "BepInEx");

    // ---- item 2: read both spellings ----------------------------------------------------------

    /// <summary>
    /// The live shape, end to end: the exact server row above, a catalog entry with no inspected
    /// manifest, and no identity cache at all. The half has to read <c>pending</c> — which is the
    /// fact that makes POLISH3's per-half Uninstall reach it.
    /// What this would catch: dropping the identity-cache route from
    /// <see cref="LedgerSpelling.Matches(CatalogEntry, InstalledLedgerRow, LedgerIdentityProbe)"/>,
    /// or going back to comparing a row's package half against the entry's four name spellings only.
    /// </summary>
    [Fact]
    public void The_live_plugin_id_spelled_server_row_reads_halves_server_pending()
    {
        string pending = WriteServerPending();
        WriteServerLedger(OldSource);
        var entry = IndexEntry();

        // The pre-POLISH4 comparison: four name spellings, no identity. This is the live failure.
        Assert.False(LedgerSpelling.Matches(entry, PluginId));
        Assert.Equal(
            (InstalledState.HalfNone, InstalledState.HalfNone),
            InstalledState.HalvesOf(entry, InstalledState.Scan(null, ServerBep), loadedHere: false));

        // With the identity cache — a prior inspection of Progenitor-HotDemo read that GUID out of its
        // bytes — the same row is this listing's, with no re-inspection and no download.
        var probe = Probe(PluginId);
        Assert.True(LedgerSpelling.Matches(entry, PluginId, probe));
        var scanned = InstalledState.Scan(null, ServerBep, null, null, probe);
        Assert.Equal(
            (InstalledState.HalfNone, InstalledState.HalfPending),
            InstalledState.HalvesOf(entry, scanned, loadedHere: false));

        // The row itself is found, with its path, so a removal has something to name.
        var rows = InstalledState.LedgerRowsFor(entry, scanned);
        Assert.Single(rows);
        Assert.Equal(OldSource, rows[0].Source);
        Assert.Equal(InstalledState.ServerRoot, rows[0].Root);
        Assert.Equal(pending, rows[0].FilePath);
        Assert.True(File.Exists(pending));
        // Revert: dropping InstalledStateQuery.IdentityProbe (or not passing it from LedgerRowsFor)
        // fails this with `server: none` over a file that is on disk.
    }

    /// <summary>
    /// The identity route reads <c>progenitor-cache/identity</c> from disk, and the Install tab's
    /// build asks it for every listing against every ledger row several times over — live that was
    /// about 1.3 s of a 2.3 s tab switch over 300 listings. The pane asks it once per listing per
    /// scan, and a new scan asks again, so an inspection that writes an identity is still seen.
    /// What this would catch: dropping the memo (the switch is slow again), or keeping answers
    /// across scans (a fresh identity would never join its ledger row).
    /// </summary>
    [Fact]
    public void The_pane_asks_the_identity_route_once_per_listing_per_scan()
    {
        WriteServerPending();
        WriteServerLedger(OldSource);
        var entry = IndexEntry();
        int asked = 0;
        LedgerIdentityProbe counting = _ => { asked++; return PluginId; };

        var pane = new InstallModsPane(Catalog());
        pane.SetInstalledQuery(InstalledState.Scan(ClientBep, ServerBep, null, null, counting));
        for (int i = 0; i < 3; i++)
            Assert.Equal((InstalledState.HalfNone, InstalledState.HalfPending), pane.HalvesOf(entry));
        Assert.Equal(1, asked);

        pane.SetInstalledQuery(InstalledState.Scan(ClientBep, ServerBep, null, null, counting));
        Assert.Equal((InstalledState.HalfNone, InstalledState.HalfPending), pane.HalvesOf(entry));
        Assert.Equal(2, asked);
    }

    /// <summary>
    /// The other route to the same row, and the one that needs no cache: a row this build wrote
    /// carries the listing key in <c>source</c> and the plugin id in its own <c>pluginId</c> field,
    /// so it resolves from its own bytes whether it is asked by key or by GUID.
    /// What this would catch: dropping <c>pluginId</c> from the ledger's JSON round-trip, or reading
    /// a row's identity from <c>source</c> alone.
    /// </summary>
    [Fact]
    public void The_new_spelling_round_trips_and_resolves_without_any_identity_cache()
    {
        WriteServerPending();
        WriteServerLedger("package:" + Key + "@" + Version, PluginId);

        var read = ServerInstallLedger.TryRead(_serverGame);
        Assert.NotNull(read);
        Assert.Equal("package:" + Key + "@" + Version, read!.Files[0].Source);
        Assert.Equal(PluginId, read.Files[0].PluginId);

        var entry = IndexEntry();
        var scanned = InstalledState.Scan(null, ServerBep);
        Assert.Equal(
            (InstalledState.HalfNone, InstalledState.HalfPending),
            InstalledState.HalvesOf(entry, scanned, loadedHere: false));

        // The plugin id is still readable off the row — the Mods sidebar joins on it.
        var rows = InstalledState.LedgerRowsFor(entry, scanned);
        Assert.Equal(PluginId, Assert.Single(rows).PluginId);
        Assert.Single(ModMenuInventory.LedgerRowsForPlugin(rows, PluginId));
        // A promote keeps the field: the reconciled `mod` row is still joinable by GUID.
        Assert.Empty(ServerInstall.ReconcilePromoted(ServerBep).Rewritten); // the file is still there
        Assert.Equal(PluginId, ServerInstallLedger.TryRead(_serverGame)!.Files[0].PluginId);
        // Revert: not writing/reading `pluginId`, or dropping it in ReconcilePromoted, fails this.
    }

    /// <summary>
    /// A row whose <c>pluginId</c> the writer knew needs no cache even when the <c>source</c> spells
    /// a different package version, and a probe that throws or answers nothing is a cache miss rather
    /// than a match: nothing here guesses a GUID from a name.
    /// What this would catch: treating an empty or failed identity answer as "matches".
    /// </summary>
    [Fact]
    public void An_unknown_identity_is_a_miss_and_never_a_guess()
    {
        var entry = IndexEntry();
        Assert.False(LedgerSpelling.Matches(entry, PluginId, _ => null));
        Assert.False(LedgerSpelling.Matches(entry, PluginId, _ => ""));
        Assert.False(LedgerSpelling.Matches(entry, PluginId, _ => throw new InvalidOperationException("cache")));
        Assert.False(LedgerSpelling.Matches(entry, "HotDemo", Probe(PluginId)));
        Assert.Equal("", LedgerSpelling.IdentityOf(entry, _ => throw new InvalidOperationException("cache")));

        // The key spelling has never needed a probe, and still does not.
        Assert.True(LedgerSpelling.Matches(entry, Key));
        Assert.Equal("package:" + Key + "@" + Version, LedgerSpelling.SourceFor(entry, Version));
        // Revert: falling back to the package name as a plugin GUID fails the `HotDemo` line.
    }

    // ---- item 1 + 4: the Uninstall the spelling unblocks ---------------------------------------

    /// <summary>
    /// The live sequence, through the pane: the old-spelled server row, Uninstall, Yes. The half is
    /// canceled — the downloaded file and its ledger row go — and <c>removed.halves.server</c> says
    /// so. Live this Yes reported <c>server: none</c> and left both behind.
    /// What this would catch: a catalog whose Remove asks for fewer spellings than the pane's read
    /// accepts, which is the exact asymmetry POLISH3 closed on one axis and POLISH4 on the other.
    /// </summary>
    [Fact]
    public void Uninstall_cancels_the_old_spelled_server_half_and_reports_cancelled()
    {
        string pending = WriteServerPending();
        WriteServerLedger(OldSource);
        string clientReload = WriteClientHotHalf();
        CacheIdentity();

        var catalog = Catalog();
        Assert.True(catalog.TryGetEntry(Key, out var entry) && entry != null);
        Assert.Equal("", entry!.Manifest.Id); // the live shape: nothing inspected this session

        var pane = new InstallModsPane(catalog);
        pane.LedgerProbe = _ => true;
        pane.HotLedgerProbe = _ => true; // the client half hot loaded, as it did live
        pane.LoadedPluginsProbe = () => new[] { PluginId };
        pane.SetInstalledQuery(InstalledState.Scan(ClientBep, ServerBep));

        // Seen: the client half is loaded here, the server's is a downloaded copy. Live the second
        // half of this pair read `none`.
        Assert.Equal(
            (InstalledState.HalfLoaded, InstalledState.HalfPending),
            pane.HalvesOf(entry!));

        Assert.True(pane.OnInstallClick(Key));
        Assert.Equal(InstallPromptKind.Uninstall, pane.Prompt);
        Assert.True(pane.PromptYes());

        // Reached: the file and the row are gone.
        Assert.False(File.Exists(pending), "the local server would still load the canceled half");
        var after = ServerInstallLedger.TryRead(_serverGame);
        Assert.True(after == null || after.Files.Count == 0);

        var removed = (Dictionary<string, object?>)pane.ActionToWidget(entry!)["removed"]!;
        var halves = (Dictionary<string, object?>)removed["halves"]!;
        Assert.Equal(InstallModsPane.UninstallHalfCancelled, halves["server"]);
        Assert.Equal(InstallModsPane.UninstallHalfUnloaded, halves["client"]);
        Assert.Equal(true, removed["ok"]);
        Assert.False(File.Exists(clientReload));

        pane.SetInstalledQuery(InstalledState.Scan(ClientBep, ServerBep));
        Assert.Equal(
            (InstalledState.HalfNone, InstalledState.HalfNone),
            pane.HalvesOf(entry!));
        // Revert: taking the identity route out of InstallCatalogService.LedgerSpellingsOf leaves the
        // file on disk and reports `server: none` (live deploy #76).
    }

    /// <summary>
    /// INSTORD1's dependency check reads the same ledgers: a package installed by an older build
    /// under its plugin id still satisfies a dependency on it, and only through an identity a prior
    /// inspection of <em>that package</em> recorded.
    /// What this would catch: refusing an install whose dependency is really on the target, or
    /// satisfying one from a ledger row that names an unrelated GUID.
    /// </summary>
    [Fact]
    public void An_old_spelled_row_satisfies_a_dependency_on_its_package()
    {
        var cache = new OrdinaryIdentityCache(ClientBep);
        Assert.True(cache.Write(new OrdinaryIdentityRecord("Progenitor", "HotDemo", Version, PluginId, Version, "sha")));
        var view = new OrdinaryDependencyView(new[] { OldSource }, Array.Empty<string>(), cache);

        Assert.True(OrdinaryDependencies.SatisfiedOn(view, "Progenitor", "HotDemo", Version, out string by));
        Assert.Equal(OrdinaryDependencies.BySatisfiedLedger, by);

        // Nothing inspected this package: the row is not evidence for it.
        var blind = new OrdinaryDependencyView(new[] { OldSource }, Array.Empty<string>(), cache);
        Assert.False(OrdinaryDependencies.SatisfiedOn(blind, "Progenitor", "Other", Version, out string missing));
        Assert.Equal(OrdinaryDependencies.BySatisfiedMissing, missing);
        // Revert: removing LedgerHasByIdentity fails the first assertion; reading a package name as a
        // GUID fails the second.
    }

    // ---- item 3: the sidebar caption ----------------------------------------------------------

    /// <summary>
    /// POLISH4 item 3. A ledgered mod nothing has loaded registered no name, so the sidebar drew its
    /// plugin GUID (live: <c>progenitor.hotdemo</c>). The listing's name is used instead —
    /// from the ledger's key, else the key the identity cache resolves — and the GUID stays the row's
    /// id everywhere it is an identity.
    /// What this would catch: captioning an unloaded ledgered row with its GUID again, renaming a row
    /// that has a name of its own, or reading a plugin GUID as a package key.
    /// </summary>
    [Fact]
    public void An_unloaded_ledgered_row_is_captioned_with_the_listings_name()
    {
        // The new spelling: the key is on the row, so no probe is needed.
        var byKey = Row(PluginId, PluginId, ModProvenance.Progenitor, LedgerRow("package:" + Key + "@" + Version));
        Assert.Equal("HotDemo", ModsMenu.SidebarNameOf(byKey, null));
        Assert.Equal(PluginId, byKey.Id);

        // The old spelling: the row names only the GUID, and the identity cache resolves the key.
        var byIdentity = Row(PluginId, PluginId, ModProvenance.Progenitor, LedgerRow(OldSource));
        Assert.Equal(PluginId, ModsMenu.SidebarNameOf(byIdentity, null));
        Assert.Equal("HotDemo", ModsMenu.SidebarNameOf(byIdentity, _ => Key));

        // A row with a name of its own keeps it, and a probe answering a GUID changes nothing.
        var named = Row(PluginId, "Hot Demo", ModProvenance.Progenitor, LedgerRow("package:" + Key + "@" + Version));
        Assert.Equal("Hot Demo", ModsMenu.SidebarNameOf(named, _ => Key));
        Assert.Equal(PluginId, ModsMenu.SidebarNameOf(byIdentity, _ => PluginId));

        // A mod another tool installed is not renamed from a ledger it is not in.
        var foreign = Row("some.other.mod", "some.other.mod", ModProvenance.Other);
        Assert.Equal("some.other.mod", ModsMenu.SidebarNameOf(foreign, _ => Key));
        // Revert: captioning from row.Name alone fails the first two; dropping the provenance or
        // same-as-id guard fails the last two.
    }

    // ---- fixture ------------------------------------------------------------------------------

    private static ModsSidebarRow Row(
        string id, string name, ModProvenance provenance, params InstalledLedgerRow[] ledger) =>
        new()
        {
            Id = id,
            Name = name,
            Provenance = provenance,
            Ledger = ledger,
        };

    private static InstalledLedgerRow LedgerRow(string source)
    {
        Assert.True(InstalledState.TrySplitPackageSource(source, out string package, out string version));
        return new InstalledLedgerRow(
            package, "sha", version, PluginId + ".dll", source,
            ServerInstallLedger.CategoryPending, InstalledState.ServerRoot);
    }

    /// <summary>The live pending file: the plugin's DLL under the local server's index folder.</summary>
    private string WriteServerPending()
    {
        string path = Path.Combine(ServerBep, InstalledState.PendingFolder, "index", PluginId + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 8, 1, 9, 2 });
        return path;
    }

    /// <summary>
    /// The client half the live install had: a <c>progenitor-reload/</c> copy this process hot loaded,
    /// ledgered under the listing key (the hot route has always written that spelling).
    /// </summary>
    private string WriteClientHotHalf()
    {
        string path = Path.Combine(ClientBep, InstalledState.ReloadFolder, PluginId + ".dll");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = { 4, 4, 4, 4 };
        File.WriteAllBytes(path, bytes);
        new ServerInstallLedger("1.0", "b", "c", new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc), new[]
        {
            new ServerInstallLedgerEntry(
                "BepInEx/" + InstalledState.ReloadFolder + "/" + PluginId + ".dll",
                InstalledState.HashBytes(bytes),
                bytes.Length,
                ServerInstallLedger.CategoryMod,
                "package:" + Key + "@" + Version,
                new DateTime(2026, 9, 13, 14, 22, 40, DateTimeKind.Utc),
                uninstallPending: false,
                hot: true,
                pluginId: PluginId),
        }).Write(_clientGame);
        return path;
    }

    private void WriteServerLedger(string source, string? pluginId = null)
    {
        string rel = "BepInEx/" + InstalledState.PendingFolder + "/index/" + PluginId + ".dll";
        string abs = ServerInstallLedger.Absolute(_serverGame, rel);
        new ServerInstallLedger("1.0", "b", "c", new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc), new[]
        {
            new ServerInstallLedgerEntry(
                rel,
                InstalledState.HashBytes(File.ReadAllBytes(abs)),
                new FileInfo(abs).Length,
                ServerInstallLedger.CategoryPending,
                source,
                new DateTime(2026, 9, 13, 14, 22, 40, DateTimeKind.Utc),
                uninstallPending: false,
                hot: false,
                pluginId: pluginId),
        }).Write(_serverGame);
    }

    /// <summary>
    /// The inspected identity a prior session recorded for this listing, in the client root's
    /// <c>progenitor-cache/identity</c> — the cache the catalog service's probe reads.
    /// </summary>
    private void CacheIdentity() =>
        Assert.True(new OrdinaryIdentityCache(ClientBep)
            .Write(new OrdinaryIdentityRecord("Progenitor", "HotDemo", Version, PluginId, Version, "sha")));

    private static LedgerIdentityProbe Probe(string guid) => _ => guid;

    /// <summary>The catalog entry a fresh session has: adapted from the index, nothing inspected.</summary>
    private CatalogEntry IndexEntry()
    {
        Assert.True(Catalog().TryGetEntry(Key, out var entry) && entry != null);
        return entry!;
    }

    private InstallCatalogService Catalog()
    {
        var catalog = new InstallCatalogService(
            ClientBep,
            new NoNetwork(),
            null,
            null,
            null,
            localServerConfigured: true,
            null,
            ServerBep);
        Assert.True(catalog.LoadIndexFromJson(IndexJson, "stamp").Ok);
        return catalog;
    }

    private const string IndexJson =
        "[{\"name\":\"HotDemo\",\"full_name\":\"" + Key + "\",\"owner\":\"Progenitor\","
        + "\"package_url\":\"https://thunderstore.io/c/v-rising/p/Progenitor/HotDemo/\",\"is_deprecated\":false,"
        + "\"versions\":[{\"name\":\"HotDemo\",\"full_name\":\"" + Key + "-" + Version + "\",\"description\":\"d\","
        + "\"version_number\":\"" + Version + "\",\"dependencies\":[],"
        + "\"download_url\":\"https://thunderstore.io/package/download/Progenitor/HotDemo/" + Version + "/\","
        + "\"file_size\":10,\"is_active\":true}]}]";

    private sealed class NoNetwork : ICatalogTransport
    {
        public Task<CatalogTransportResponse> SendAsync(
            CatalogTransportRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("no network in this test");
    }
}
