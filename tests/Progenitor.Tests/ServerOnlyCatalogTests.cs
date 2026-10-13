using System;
using System.IO;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The server-only catalog path (docs/mod-manifest.md "Server-only plugins"). Its tests went with the consent panel
/// (ServerOnlyConsentTests, 2026-10-05); what remains is pinned here: the signed flag, the client comparison that never
/// lists such a row as something to install, the log line, and the byte-identical catalog without one.
/// </summary>
[Collection("ModManifest")]
public sealed class ServerOnlyCatalogTests : IDisposable
{
    private const string Shared = "progenitor.sample";
    private const string ServerOnly = "progenitor.reloadexample";
    private const string Host = "127.0.0.1:9976";

    private readonly string _dir;

    public ServerOnlyCatalogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-serveronly-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = Host;
        Plugin.Logger = new TestLogSource();
    }

    public void Dispose()
    {
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestConnectHost = null;
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private static ManifestEnvelope Catalog(ModManifestKey key, bool withServerOnly)
    {
        var loaded = withServerOnly
            ? new[]
            {
                new LoadedModInfo(Shared, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
                new LoadedModInfo(ServerOnly, "0.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true, serverOnly: true),
            }
            : new[]
            {
                new LoadedModInfo(Shared, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
            };
        return ModManifest.Build(loaded, new ManifestOverrides { ServerName = "Night Garden" }, key,
            new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc), null);
    }

    [Fact]
    public void The_flag_is_signed_into_the_catalog_only_when_true()
    {
        using var key = ModManifestCrypto.Generate();
        var with = Catalog(key, withServerOnly: true);
        var without = Catalog(key, withServerOnly: false);

        string json = ModManifestJson.CanonicalEnvelope(with);
        Assert.Contains("\"serverOnly\":true", json, StringComparison.Ordinal);
        Assert.DoesNotContain("serverOnly", ModManifestJson.CanonicalEnvelope(without), StringComparison.Ordinal);
        Assert.True(System.Linq.Enumerable.Single(with.Body.Mods, m => m.Id == ServerOnly).ServerOnly);
        Assert.False(System.Linq.Enumerable.Single(with.Body.Mods, m => m.Id == Shared).ServerOnly);
    }

    [Fact]
    public void The_client_lists_a_server_only_row_as_a_server_mod_and_never_as_something_to_install()
    {
        using var key = ModManifestCrypto.Generate();
        var envelope = Catalog(key, withServerOnly: true);

        var diff = ModManifestCompare.Classify(envelope, Array.Empty<LoadedModInfo>(), new TrustedOperatorsFile(), Host);

        Assert.True(diff.SignatureValid);
        var row = Assert.Single(diff.ServerOnly);
        Assert.Equal(ServerOnly, row.Id);
        Assert.DoesNotContain(diff.Missing, m => m.Id == ServerOnly);
        Assert.DoesNotContain(diff.Present, m => m.Id == ServerOnly);
        Assert.DoesNotContain(diff.RestartRequired, m => m.Id == ServerOnly);
        Assert.Contains(diff.Missing, m => m.Id == Shared);

        string block = ModManifestCompare.FormatLogBlock(envelope, diff, Host);
        Assert.Contains("server-only (1): " + ServerOnly + " 0.2.0", block, StringComparison.Ordinal);
    }

    [Fact]
    public void A_catalog_with_a_server_only_row_verifies_and_is_accepted()
    {
        using var key = ModManifestCrypto.Generate();
        var envelope = Catalog(key, withServerOnly: true);

        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope)));

        var pending = ModManifest.Pending;
        Assert.NotNull(pending);
        Assert.True(pending!.SignatureValid);
        Assert.Contains(pending.Diff.ServerOnly, m => m.Id == ServerOnly);
        Assert.DoesNotContain(pending.Missing, m => m.Id == ServerOnly);
    }
}
