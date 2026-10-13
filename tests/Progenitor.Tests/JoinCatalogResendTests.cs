using System;
using System.IO;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Live 2026-10-04: an admin's accepted <c>recommend-add</c> re-signed the catalog directly, so the server's 1 s scan
/// (which compared the stamp before and after its own rebuild) saw no change and resent nothing; a joined client's open
/// mods page never showed the row. Every re-sign now leaves a resend pending for the next scan, which goes to every
/// handshake-ok client that does not have the current catalog yet.
/// </summary>
[Collection("ModManifest")]
public sealed class JoinCatalogResendTests : IDisposable
{
    private static readonly RemoteInstallActor Admin = new("Admin", 76561198000000001UL, isAdmin: true);
    private readonly string _dir;
    private double _now = 1000;

    public JoinCatalogResendTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-resend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = new[] { new LoadedModInfo("shared.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true) };
        ModManifest.TestIsServer = true;
        Plugin.Logger = new TestLogSource();
        JoinAdminServer.WindowGet = () => 60;
        JoinAdminServer.WindowSet = _ => true;
        JoinAdminServer.Audit = _ => { };
        JoinAdminServer.Now = () => _now;
        JoinServerSettings.TestContent = new JoinSection("Hi", null, null, new JoinServerZone("UTC", 0));
    }

    public void Dispose()
    {
        ModManifest.ResetForTests();
        JoinServerSettings.TestContent = null;
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestIsServer = false;
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    /// <summary>Two players joined and have the current catalog; nothing is pending.</summary>
    private static void TwoJoinedPlayers()
    {
        ModManifest.OnHandshakeOk(3);
        ModManifest.OnHandshakeOk(5);
        ModManifest.CatalogScan(out _, out _);
        Assert.False(ModManifest.CatalogScan(out _, out _));
    }

    [Fact]
    public void An_admins_accepted_change_reaches_every_joined_client_on_the_next_scan()
    {
        TwoJoinedPlayers();
        string before = ModManifest.TestCatalogStamp;
        _now += 7;
        Assert.True(JoinAdminServer.Apply(new JoinAdminRequest { Op = JoinAdminOps.RecommendAdd, Id = "maps.mod", Version = "1.2.0", Reason = "Maps." }, true, Admin).Ok);
        // The change re-signed the catalog itself, so the scan's own rebuild changes nothing (the old miss)...
        Assert.NotEqual(before, ModManifest.TestCatalogStamp);
        // ...and the resend still goes out, to both players, naming why.
        Assert.True(ModManifest.CatalogScan(out string reason, out var users));
        Assert.Equal("join-admin recommend-add", reason);
        Assert.Equal(new[] { 3, 5 }, users);
        // Once.
        Assert.False(ModManifest.CatalogScan(out _, out _));
    }

    [Fact]
    public void A_change_the_scan_itself_signs_is_resent_too()
    {
        TwoJoinedPlayers();
        JoinServerSettings.TestContent = new JoinSection("A new message", null, null, new JoinServerZone("UTC", 0));
        Assert.True(ModManifest.CatalogScan(out string reason, out var users));
        Assert.Equal("scan", reason);
        Assert.Equal(new[] { 3, 5 }, users);
    }

    [Fact]
    public void A_client_that_got_the_new_catalog_on_its_own_join_is_not_sent_it_twice()
    {
        TwoJoinedPlayers();
        ModManifest.TestLoadedMods = new[] { new LoadedModInfo("shared.mod", "1.0.1", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true) };
        // Player 7 joins: the join re-signs and sends it the new catalog directly.
        ModManifest.OnHandshakeOk(7);
        Assert.True(ModManifest.CatalogScan(out _, out var users));
        Assert.Equal(new[] { 3, 5 }, users);
    }
}
