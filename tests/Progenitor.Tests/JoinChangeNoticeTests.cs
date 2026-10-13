using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// JOINNOTICE: a re-signed catalog that arrives mid-session (an admin's change resent to every joined client) shows a
/// small notice when the server's lists or message changed since this session saw them, and no join screens are open.
/// Game-free: the client is driven through <c>ModManifest.AcceptEnvelope</c> with catalogs signed by one test key.
/// </summary>
[Collection("ModManifest")]
public sealed class JoinChangeNoticeTests : IDisposable
{
    private readonly string _dir;
    private readonly ModManifestKey _key = ModManifestCrypto.Generate();
    private readonly List<JoinChangeNoticeState> _shown = new();
    private int _dismissed;
    private double _now = 100;

    public JoinChangeNoticeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-joinnotice-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Plugin.Logger = new TestLogSource();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = "127.0.0.1:9976";
        UiApi.ResetForTests();
        JoinFlowClient.Clock = () => _now;
        JoinFlowClient.ChangeNoticeRequested += _shown.Add;
        JoinFlowClient.ChangeNoticeDismissed += () => _dismissed++;
        JoinFlowClient.OnJoinAttempt();
    }

    public void Dispose()
    {
        JoinFlowClient.Uninstall();
        JoinFlowClient.Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        JoinFlowClient.InWorldReady = () => false;
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestConnectHost = null;
        UiApi.ResetForTests();
        Plugin.Logger = null;
        _key.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private void Catalog(string message, params RecommendedMod[] recommended)
    {
        _now += 1;
        var envelope = ModManifest.Build(Array.Empty<LoadedModInfo>(), new ManifestOverrides { ServerName = "Night Garden" }, _key, DateTime.UtcNow,
            new JoinSection(message, null, recommended, new JoinServerZone("UTC", 0)));
        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope)));
    }

    private static RecommendedMod Maps(string reason = "Better maps") => new("maps.mod", "Owner-Maps", "1.0.0", reason);

    private static RecommendedMod Sounds() => new("sounds.mod", "Owner-Sounds", "2.0", "Sounds");

    [Fact]
    public void A_mods_change_mid_session_shows_one_notice_and_view_opens_the_mods_page_with_its_tag()
    {
        // The join: a first visit with a recommendation opens the mods page 2 s later; the player closes it.
        Catalog("", Sounds());
        JoinFlowClient.InWorldReady = () => true;
        _now += 3;
        JoinFlowClient.Tick();
        Assert.Equal(JoinPage.Mods, JoinFlowClient.Session!.Page);
        JoinFlowClient.Session!.Close();
        Assert.Empty(_shown);
        Catalog("", Sounds(), Maps());     // an admin recommended a mod
        var n = Assert.Single(_shown);
        Assert.Equal("mods", n.Kind);
        Assert.Equal("This server's mods changed.", n.Text);
        Assert.Equal("View mods", n.ViewCaption);
        var read = JoinFlowTestBridge.Read();
        var notice = (Dictionary<string, object?>)read["changeNotice"]!;
        Assert.Equal(true, notice["shown"]);
        Assert.Equal("mods", notice["kind"]);

        // The same change again (a resend): no second notice.
        Catalog("", Sounds(), Maps());
        Assert.Single(_shown);

        JoinFlowClient.InWorldReady = () => true;
        var view = JoinFlowTestBridge.ChangeNoticeView();
        Assert.Equal("mods", view["result"]);
        Assert.Equal("mods", view["page"]);
        Assert.Equal(false, ((Dictionary<string, object?>)view["changeNotice"]!)["shown"]);
        Assert.True(_dismissed > 0);
        var rows = ((List<object?>)view["recommended"]!).Cast<Dictionary<string, object?>>().ToList();
        Assert.Equal("new", rows.Single(r => (string)r["id"]! == "maps.mod")["change"]);
        Assert.Equal("", rows.Single(r => (string)r["id"]! == "sounds.mod")["change"]);
    }

    /// <summary>
    /// CLIENTPOLICY3, live 2026-10-05: kicked while the welcome page was open, the page covered the game's own "Communication
    /// with server was lost … [Leave Game]" banner. When the connection ends the in-world screens close (a pending open
    /// is canceled); a refused join's screens are not in-world and are not touched.
    /// </summary>
    [Fact]
    public void The_in_world_screens_close_when_the_connection_ends()
    {
        Catalog("Welcome to Night Garden.");
        JoinFlowClient.InWorldReady = () => true;
        _now += 3;
        JoinFlowClient.Tick();
        Assert.NotNull(JoinFlowClient.Session);
        Assert.Equal(JoinMode.InWorld, JoinFlowClient.Session!.Mode);

        Assert.True(JoinFlowClient.OnConnectionLost());
        Assert.Null(JoinFlowClient.Session);
        Assert.False(JoinFlowClient.InWorldPending);
        Assert.False(JoinFlowClient.OnConnectionLost()); // raised twice for one leave: nothing more to close
    }

    [Fact]
    public void A_message_change_and_both_have_their_own_words()
    {
        Catalog("");
        Catalog("Event tonight!");
        var m = Assert.Single(_shown);
        Assert.Equal("message", m.Kind);
        Assert.Equal("The server message changed.", m.Text);
        Assert.Equal("View message", m.ViewCaption);
        Catalog("Event moved to tomorrow.", Maps());
        Assert.Equal(2, _shown.Count);
        Assert.Equal("both", _shown[1].Kind);
        Assert.Equal("This server's mods and message changed.", _shown[1].Text);
        Assert.Equal("View", _shown[1].ViewCaption);
        // A removed message is nothing to view; the mods did not change: no notice.
        Catalog("", Maps());
        Assert.Equal(2, _shown.Count);
    }

    [Fact]
    public void Open_screens_refresh_in_place_and_show_no_notice()
    {
        Catalog("");
        JoinFlowClient.InWorldReady = () => true;
        Assert.True(JoinFlowClient.ShowModsNow());
        Catalog("", Maps());
        Assert.Empty(_shown);
        Assert.Single(JoinFlowClient.Session!.Model.Recommended);
        // Closed again, the change the page showed is not news.
        JoinFlowClient.Session!.Close();
        Catalog("", Maps());
        Assert.Empty(_shown);
    }

    [Fact]
    public void The_admins_own_edit_shows_them_no_notice()
    {
        Catalog("");
        JoinFlowClient.NoteLocalAdminEdit();
        Catalog("", Maps());
        Assert.Empty(_shown);
        // Later changes still do.
        _now += 30;
        Catalog("", Maps("Maps for events"));
        Assert.Single(_shown);
    }

    [Fact]
    public void Ok_closes_it_and_a_new_join_forgets_it()
    {
        Catalog("");
        Catalog("", Maps());
        Assert.Equal("closed", JoinFlowTestBridge.ChangeNoticeClose()["result"]);
        Assert.Null(JoinFlowClient.ChangeNotice);
        Assert.Equal("no notice", JoinFlowClient.ViewChangeNotice());
        Catalog("", Maps(), new RecommendedMod("sounds.mod", "Owner-Sounds", "2.0", "Sounds"));
        Assert.Equal(2, _shown.Count);
        JoinFlowClient.OnJoinAttempt();
        Assert.Null(JoinFlowClient.ChangeNotice);
        // The next join decides afresh: its first catalog is the baseline, not a change.
        Catalog("", Maps());
        Assert.Equal(2, _shown.Count);
    }
}
