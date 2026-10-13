using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// LOCALJOIN1 (debug, requested 2026-10-07) and the SRVUNINST1 follow-ups: the join window's local test source
/// for a required mod the server named no source for, the server notice's advice by what this client has, and the
/// Mods page's bare version. Game-free; not live-verified.
/// </summary>
public sealed class LocalJoinSourceTests
{
    private const string Hot = "progenitor.hotdemo";

    private static Handshake.ModRecord Must(string id, string version) =>
        Handshake.ModRecord.CreateDeclared(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact);

    private static ManifestMod Row(string id, string version, string? package = null) =>
        new(id, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, false, null, package, null, null);

    private static ManifestBody Catalog(params ManifestMod[] rows) =>
        new(1, "Forms Test", "2026-10-07T12:00:00Z", new ManifestOperator("Ada", "", "a1b2c3d4e5f67890"), null, rows);

    private static JoinPageModel Model(ManifestBody? catalog, Func<string, LocalTestMatch?>? local) =>
        JoinPageModel.Build(new JoinFacts
        {
            ServerName = "Forms Test",
            Asked = new[] { Must(Hot, "0.1.0") },
            Provided = Array.Empty<Handshake.ModRecord>(),
            Catalog = catalog,
            LocalTestOf = local,
        });

    private static readonly LocalTestMatch Local = new("Example-HotDemo", "0.1.0", "HotDemo");

    /// <summary>With no local index (everyone's default) nothing changes: the row asks the admins.</summary>
    [Fact]
    public void Without_a_local_index_a_sourceless_row_still_asks_the_admin()
    {
        var row = Model(null, null).Required.Single();
        Assert.Equal(JoinRowAction.AskAdmin, row.Action);
        Assert.False(row.LocalTest);
        Assert.Equal("No download source", JoinText.SourceLine(row));
    }

    /// <summary>
    /// The live case: the server requires Progenitor Hot Demo and names no source; the local test index provides it.
    /// The row installs from it, says so on its button and its source line, and must install that very plugin.
    /// </summary>
    [Fact]
    public void A_sourceless_required_mod_installs_from_the_local_test_source_and_says_so()
    {
        var model = Model(null, id => id == Hot ? Local : null);
        var row = model.Required.Single();
        Assert.Equal(JoinRowAction.Install, row.Action);
        Assert.True(row.LocalTest);
        Assert.Equal(LocalTestSource.InstallCaption, JoinText.ActionCaption(row));
        Assert.StartsWith("Local test source · Example-HotDemo", JoinText.SourceLine(row));
        var request = JoinFlowSession.RequestFor(row);
        Assert.Equal("Example-HotDemo", request.Package);
        Assert.Equal(Hot, request.ModId);
        Assert.DoesNotContain("didn't say where", JoinText.CannotFixFooter(model));
    }

    /// <summary>A source the server named always wins over the local test source.</summary>
    [Fact]
    public void A_server_named_package_is_never_replaced_by_the_local_test_source()
    {
        var row = Model(Catalog(Row(Hot, "0.1.0", "Owner-HotDemo")), id => Local).Required.Single();
        Assert.False(row.LocalTest);
        Assert.Equal("Owner-HotDemo", row.Package);
        Assert.Equal("Thunderstore · Owner", JoinText.SourceLine(row));
    }

    /// <summary>The local index's listings map to the plugin GUIDs their own packages declare (here progenitor.json).</summary>
    [Fact]
    public void The_local_index_maps_listings_to_the_plugin_ids_their_packages_declare()
    {
        string dir = Path.Combine(Path.GetTempPath(), "progenitor-localjoin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        LocalTestSource.ResetForTests();
        try
        {
            string zip = Path.Combine(dir, "Example-HotDemo-0.1.0.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("progenitor.json");
                using var w = new StreamWriter(entry.Open());
                w.Write("{\"id\":\"" + Hot + "\",\"version\":\"0.1.0\"}");
            }

            string url = new Uri(zip).AbsoluteUri;
            File.WriteAllText(Path.Combine(dir, LocalIndexSource.IndexFileName),
                "[{\"name\":\"HotDemo\",\"full_name\":\"Example-HotDemo\",\"owner\":\"Example\",\"package_url\":\"\",\"is_deprecated\":false,"
                + "\"categories\":[\"Mods\"],\"versions\":[{\"name\":\"HotDemo\",\"full_name\":\"Example-HotDemo-0.1.0\",\"description\":\"d\","
                + "\"version_number\":\"0.1.0\",\"dependencies\":[],\"download_url\":\"" + url + "\",\"file_size\":100,\"is_active\":true}]}]");

            var map = LocalTestSource.Map(dir);
            Assert.True(map.TryGetValue(Hot, out var match));
            Assert.Equal("Example-HotDemo", match!.Key);
            Assert.Equal("0.1.0", match.Version);
            Assert.Empty(LocalTestSource.Map(null));
            Assert.Empty(LocalTestSource.Map(Path.Combine(dir, "missing")));
            // A download outside the folder is never read.
            Assert.Null(LocalTestSource.ZipPathOf("file:///C:/elsewhere/x.zip", dir));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // ---- review fixes ---------------------------------------------------------------------------

    /// <summary>A local listing that lacks the version the server needs is not offered; the row says what it has.</summary>
    [Fact]
    public void A_local_listing_without_the_needed_version_is_not_offered()
    {
        var older = new LocalTestMatch("Example-HotDemo", "0.0.9", "HotDemo");
        var row = Model(null, id => older).Required.Single();
        Assert.Equal(JoinRowAction.AskAdmin, row.Action);
        Assert.False(row.LocalTest);
        Assert.Equal("No download source · " + LocalTestSource.WrongVersion("0.0.9", "0.1.0"), JoinText.SourceLine(row));
        Assert.Contains("has 0.0.9, not 0.1.0", row.LocalTestNote);
    }

    /// <summary>The row shows the local package's facts, and its request is marked local (no Thunderstore trust).</summary>
    [Fact]
    public void A_local_row_shows_the_local_package_s_facts_and_its_request_is_local()
    {
        var local = new LocalTestMatch("Example-HotDemo", "0.1.0", "HotDemo", new[] { "0.1.0" }, size: 4607, hotLoadable: true);
        var row = Model(Catalog(Row(Hot, "0.1.0")), id => local).Required.Single();
        Assert.True(row.LocalTest);
        Assert.Equal(4607, row.Size);
        Assert.Null(row.Sha256);
        Assert.True(row.HotLoadable);
        Assert.True((row.Tags & JoinRowTags.AppliesNow) != 0);
        Assert.True(JoinFlowSession.RequestFor(row).LocalTest);
        Assert.Equal("Refused: the local test source's listing of X is gone.",
            JoinCatalogInstaller.LocalWords("Refused: Thunderstore's listing of X is gone."));
    }

    /// <summary>Only the plugin the installer will accept is offered: the manifest id, else a single plugin.</summary>
    [Fact]
    public void Only_the_plugin_an_install_is_accepted_as_is_offered()
    {
        Assert.Equal("a.one", new LocalZipFacts("a.one", false, new[] { "a.one", "a.helper" }).AcceptedPlugin);
        Assert.Equal("a.solo", new LocalZipFacts("", false, new[] { "a.solo" }).AcceptedPlugin);
        Assert.Equal("", new LocalZipFacts("", false, new[] { "a.one", "a.two" }).AcceptedPlugin);
    }

    // ---- B: the server notice's advice ---------------------------------------------------------

    /// <summary>
    /// Live 2026-10-07: an uninstalled HotDemo read "turn it back on in the Mods list". The advice follows what this
    /// client has: turned off → turn it on; not installed → install (from the local test source when it has it).
    /// </summary>
    [Fact]
    public void A_missing_required_mod_gets_advice_for_what_this_client_has()
    {
        RecheckItem Item(RecheckLocalState local, bool localSource = false) =>
            new() { Key = Hot, Name = "Progenitor Hot Demo", Kind = ClientModProblem.KindMissing, Local = local, LocalTestSource = localSource };

        Assert.Equal("Required here; turn it back on in the Mods list",
            ClientPolicyNoticeText.Reason(Item(RecheckLocalState.TurnedOff), RecheckPauseOutcome.NotTried));
        Assert.Equal("Required here; turn it back on in the Mods list",
            ClientPolicyNoticeText.Reason(Item(RecheckLocalState.Unknown), RecheckPauseOutcome.NotTried));
        Assert.Equal("Required here and not installed; look for it in Install Mods, or ask the admins where to get it",
            ClientPolicyNoticeText.Reason(Item(RecheckLocalState.NotInstalled), RecheckPauseOutcome.NotTried));
        Assert.Equal("Required here and not installed; install it from Install Mods (local test source), or from the join window when you next join",
            ClientPolicyNoticeText.Reason(Item(RecheckLocalState.NotInstalled, localSource: true), RecheckPauseOutcome.NotTried));
        var listed = Item(RecheckLocalState.NotInstalled);
        listed.InstallListed = true;
        Assert.Equal("Required here and not installed; install it from Install Mods, or from the join window when you next join",
            ClientPolicyNoticeText.Reason(listed, RecheckPauseOutcome.NotTried));
        // The main menu's disconnect notice: the fix is made there, before the rejoin.
        Assert.Equal("Required here and it is not installed; look for it in Install Mods, or ask the admins where to get it",
            ClientPolicyNoticeText.Reason(Item(RecheckLocalState.NotInstalled), RecheckPauseOutcome.NotTried, disconnected: true));
        Assert.Equal("Required here and it is not installed; install it from Install Mods before you rejoin",
            ClientPolicyNoticeText.Reason(listed, RecheckPauseOutcome.NotTried, disconnected: true));
        Assert.Equal("Required here and it was turned off; turn it back on in the Mods list before you rejoin",
            ClientPolicyNoticeText.Reason(Item(RecheckLocalState.TurnedOff), RecheckPauseOutcome.NotTried, disconnected: true));
    }

    /// <summary>
    /// NOTICEOPEN1 (standing rule 2026-10-07): the in-game notice offers the fix where the player is: Open Install Mods for
    /// a required mod with no copy here, Open Mods for one turned off (the main menu's disconnect notice too), nothing for a paused
    /// mod. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void The_notice_offers_open_install_mods_or_open_mods_for_the_fix_here()
    {
        RecheckItem Item(RecheckLocalState local, string key = Hot) =>
            new() { Key = key, Name = "Progenitor Hot Demo", Kind = ClientModProblem.KindMissing, Local = local };

        var install = ClientPolicyNoticeText.Compose(RecheckNoticeKind.Warn, new[] { Item(RecheckLocalState.TurnedOff, "x.off"), Item(RecheckLocalState.NotInstalled) }, 0, "Night Garden");
        Assert.Equal(("install", Hot, "Open Install Mods"), (install.OpenKind, install.OpenKey, install.OpenCaption));
        var mods = ClientPolicyNoticeText.Compose(RecheckNoticeKind.Disconnect, new[] { Item(RecheckLocalState.TurnedOff, "x.off") }, 30, "Night Garden");
        Assert.Equal(("mods", "x.off", "Open Mods"), (mods.OpenKind, mods.OpenKey, mods.OpenCaption));
        // The disconnect notice (shown again on the main menu) offers the same fix there.
        var menu = ClientPolicyNoticeText.Compose(RecheckNoticeKind.Disconnected, new[] { Item(RecheckLocalState.NotInstalled) }, 0, "Night Garden");
        Assert.Equal(("install", "Open Install Mods"), (menu.OpenKind, menu.OpenCaption));
        Assert.Equal("mods", ClientPolicyNoticeText.Compose(RecheckNoticeKind.Disconnected, new[] { Item(RecheckLocalState.TurnedOff, "x.off") }, 0, "Night Garden").OpenKind);
        Assert.Equal("", ClientPolicyNoticeText.Compose(RecheckNoticeKind.Resolved, new[] { Item(RecheckLocalState.NotInstalled) }, 0, "Night Garden").OpenKind);
        Assert.Equal("", ClientPolicyNoticeText.Compose(RecheckNoticeKind.Warn, new[] { Item(RecheckLocalState.Unknown) }, 0, "Night Garden").OpenKind);
        var paused = new Dictionary<string, RecheckPauseOutcome>(StringComparer.OrdinalIgnoreCase) { [Hot] = RecheckPauseOutcome.Paused };
        Assert.Equal("", ClientPolicyNoticeText.Compose(RecheckNoticeKind.Warn, new[] { Item(RecheckLocalState.NotInstalled) }, 0, "Night Garden", paused).OpenKind);
    }

    // ---- D: the bare version --------------------------------------------------------------------

    [Fact]
    public void The_page_uses_the_bare_version_never_the_inventory_s_display_words()
    {
        Assert.Equal("0.1.0", ModMenuInventory.BareVersion("Server 0.1.0"));
        Assert.Equal("1.0", ModMenuInventory.BareVersion("Local 1.0; server 1.1"));
        Assert.Equal("", ModMenuInventory.BareVersion(ModMenuInventory.VersionUnknown));
        Assert.Equal("2.4.0", ModMenuInventory.BareVersion(" 2.4.0 "));
        Assert.Equal("Uninstall progenitor.reloadexample 0.1.0?",
            ModPageCopy.UninstallHeading("progenitor.reloadexample", ModMenuInventory.BareVersion("Server 0.1.0")));
    }
}
