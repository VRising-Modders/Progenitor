using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C3: the dual-signed catalog (docs/mod-manifest.md, Catalog body). The current form is today's
/// bytes; the extended form adds <c>peers</c> and <c>match</c> per row and goes only to clients that declared
/// <c>Handshake.Capability.VersionRanges</c>, because an older client re-canonicalizes without keys it does not know
/// and would reject the extended form's signature.
/// </summary>
[Collection("ModManifest")]
public sealed class CatalogDualSignTests : IDisposable
{
    private const string Trade = "example.trade";
    private const string Chat = "example.chat";
    private static readonly DateTime Issued = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;

    public CatalogDualSignTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-dualsign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = "127.0.0.1:9976";
        Plugin.Logger = new TestLogSource();
    }

    public void Dispose()
    {
        VersionMatching.SettingsSource = null;
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = null;
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private static LoadedModInfo[] Loaded() => new[]
    {
        new LoadedModInfo(Trade, "1.4.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
        new LoadedModInfo(Chat, "2.0.1", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor, false),
    };

    private static ManifestOverrides Overrides() => new() { ServerName = "Night Garden" };

    /// <summary>Trade declares a range; Chat gets the default (whatever the declaration source does not cover).</summary>
    private static string? Peers(ManifestMod m) => m.Id == Trade ? ">=1.2.0 <2.0.0" : null;

    private static (ManifestEnvelope Current, ManifestEnvelope Extended) Forms(ModManifestKey key, VersionMatchSettings? settings = null) =>
        ModManifest.BuildForms(Loaded(), Overrides(), key, Issued, null, Peers, settings ?? VersionMatchSettings.Default);

    private static bool Verifies(ManifestBody body, string signature) =>
        ModManifestCrypto.Verify(body.Operator.Spki, ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(body)), signature);

    [Fact]
    public void Both_forms_verify_with_the_same_key_and_share_issued_and_operator()
    {
        using var key = ModManifestCrypto.Generate();
        var (current, extended) = Forms(key);

        Assert.True(Verifies(current.Body, current.Signature));
        Assert.True(Verifies(extended.Body, extended.Signature));
        Assert.NotEqual(current.Signature, extended.Signature);
        Assert.Equal(current.Body.Issued, extended.Body.Issued);
        Assert.Equal(current.Body.Operator.Fp, extended.Body.Operator.Fp);
        Assert.Equal(key.Fingerprint, extended.Body.Operator.Fp);
    }

    [Fact]
    public void The_current_form_is_byte_identical_to_the_pre_C3_catalog()
    {
        using var key = ModManifestCrypto.Generate();
        var (current, _) = Forms(key, new VersionMatchSettings(VersionMatchingMode.Exact));
        string body = ModManifestJson.CanonicalBody(current.Body);

        // The canonical body exactly as the pre-C3 writer produced it for these inputs: no new keys anywhere.
        string expected =
            "{\"issued\":\"2026-10-09T12:00:00Z\",\"mods\":["
            + "{\"hotLoadable\":false,\"id\":\"example.chat\",\"level\":\"NoNeedForSync\",\"namespace\":null,\"package\":null,"
            + "\"sha256\":null,\"size\":null,\"url\":null,\"version\":\"2.0.1\",\"versionRule\":\"Minor\"},"
            + "{\"hotLoadable\":true,\"id\":\"example.trade\",\"level\":\"ServerMustHave\",\"namespace\":null,\"package\":null,"
            + "\"sha256\":null,\"size\":null,\"url\":null,\"version\":\"1.4.0\",\"versionRule\":\"Exact\"}],"
            + "\"operator\":{\"fp\":\"" + key.Fingerprint + "\",\"name\":\"operator\",\"spki\":\"" + key.SpkiBase64 + "\"},"
            + "\"profileCode\":null,\"schema\":1,\"serverName\":\"Night Garden\"}";
        Assert.Equal(expected, body);
        Assert.DoesNotContain("peers", body, StringComparison.Ordinal);
        Assert.DoesNotContain("match", body, StringComparison.Ordinal);

        // And it is what the unchanged single-form entry point signs.
        var plain = ModManifest.Build(Loaded(), Overrides(), key, Issued, null);
        Assert.Equal(ModManifestJson.CanonicalBody(plain.Body), body);
    }

    [Fact]
    public void The_extended_form_writes_peers_and_match_in_canonical_key_order_only_when_set()
    {
        using var key = ModManifestCrypto.Generate();
        var (_, ranges) = Forms(key);
        string body = ModManifestJson.CanonicalBody(ranges.Body);
        Assert.Contains("\"level\":\"ServerMustHave\",\"namespace\":null,\"package\":null,\"peers\":\">=1.2.0 <2.0.0\",\"sha256\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"match\"", body, StringComparison.Ordinal);
        // Chat had no range from the source: no key at all.
        Assert.Null(ranges.Body.Mods.Single(m => m.Id == Chat).Peers);

        var settings = new VersionMatchSettings(VersionMatchingMode.Minor, new Dictionary<string, ClientModRule> { [Chat] = ClientModRule.Any });
        var (_, strict) = Forms(key, settings);
        string strictBody = ModManifestJson.CanonicalBody(strict.Body);
        Assert.Contains("\"level\":\"ServerMustHave\",\"match\":\"minor\",\"namespace\":null", strictBody, StringComparison.Ordinal);
        Assert.Contains("\"level\":\"NoNeedForSync\",\"match\":\"any\",\"namespace\":null", strictBody, StringComparison.Ordinal);
        Assert.Equal("exact", ModCatalogForms.MatchOf(Trade, new VersionMatchSettings(VersionMatchingMode.Exact)));
        Assert.Equal("at-least", ModCatalogForms.MatchOf(Trade,
            new VersionMatchSettings(VersionMatchingMode.Exact, new Dictionary<string, ClientModRule> { [Trade] = ClientModRule.AtLeast })));
        Assert.Null(ModCatalogForms.MatchOf(Trade, VersionMatchSettings.Default));
    }

    [Fact]
    public void An_older_client_that_drops_unknown_keys_rejects_the_extended_form_and_accepts_the_current_one()
    {
        using var key = ModManifestCrypto.Generate();
        var (current, extended) = Forms(key, new VersionMatchSettings(VersionMatchingMode.Exact));

        // What a pre-C3 client does with the envelope: parse the body, keep the keys it knows, re-canonicalize, verify.
        static ManifestBody OldClientView(ManifestEnvelope e)
        {
            Assert.True(ModManifestJson.TryParseEnvelope(ModManifestJson.CanonicalEnvelope(e), out var parsed, out var error), error);
            return ModCatalogForms.Current(parsed.Body);
        }

        Assert.False(Verifies(OldClientView(extended), extended.Signature));
        Assert.True(Verifies(OldClientView(current), current.Signature));
    }

    [Fact]
    public void The_extended_form_round_trips_peers_and_match_and_a_C3_client_verifies_it()
    {
        using var key = ModManifestCrypto.Generate();
        var settings = new VersionMatchSettings(VersionMatchingMode.Range, new Dictionary<string, ClientModRule> { [Trade] = ClientModRule.Exact });
        var (current, extended) = Forms(key, settings);
        string json = ModManifestJson.CanonicalEnvelope(extended);

        Assert.True(ModManifestJson.TryParseEnvelope(json, out var parsed, out var error), error);
        var trade = parsed.Body.Mods.Single(m => m.Id == Trade);
        Assert.Equal(">=1.2.0 <2.0.0", trade.Peers);
        Assert.Equal("exact", trade.Match);
        var chat = parsed.Body.Mods.Single(m => m.Id == Chat);
        Assert.Null(chat.Peers);
        Assert.Null(chat.Match);
        Assert.Equal(json, ModManifestJson.CanonicalEnvelope(parsed));
        Assert.True(Verifies(parsed.Body, parsed.Signature));

        // The client's real accept path: the extended form verifies, and an old-form catalog (an older server) still does.
        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(json));
        Assert.True(ModManifest.Pending!.SignatureValid);
        Assert.Equal(">=1.2.0 <2.0.0", ModManifest.LastEnvelope!.Body.Mods.Single(m => m.Id == Trade).Peers);

        ModManifest.ClearJoinState();
        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(current)));
        Assert.True(ModManifest.Pending!.SignatureValid);
        Assert.Null(ModManifest.LastEnvelope!.Body.Mods.Single(m => m.Id == Trade).Peers);
    }

    [Fact]
    public void A_tampered_range_fails_the_extended_signature()
    {
        using var key = ModManifestCrypto.Generate();
        var (_, extended) = Forms(key);
        string json = ModManifestJson.CanonicalEnvelope(extended).Replace(">=1.2.0 <2.0.0", ">=0.1.0 <9.0.0", StringComparison.Ordinal);
        Assert.True(ModManifestJson.TryParseEnvelope(json, out var parsed, out _));
        Assert.False(Verifies(parsed.Body, parsed.Signature));
    }

    [Fact]
    public void The_server_sends_each_user_the_form_its_capability_reads()
    {
        ModManifest.TestLoadedMods = Loaded();
        ModManifest.TestIsServer = true;
        ModManifest.TestRangeUsers.Add(5);

        ModManifest.OnHandshakeOk(5);
        ModManifest.OnHandshakeOk(6);

        var toFive = ModManifest.TestSent.Single(s => s.User == 5).Envelope;
        var toSix = ModManifest.TestSent.Single(s => s.User == 6).Envelope;
        Assert.Same(ModManifest.ExtendedEnvelope, toFive);
        Assert.Same(ModManifest.LastEnvelope, toSix);
        // The server's own effective range (no declaration loaded here: the same minor version).
        Assert.Equal(CompatRange.SameMinorAs("1.4.0")!.Text, toFive.Body.Mods.Single(m => m.Id == Trade).Peers);
        Assert.All(toSix.Body.Mods, m => Assert.Null(m.Peers));
        Assert.True(Verifies(toFive.Body, toFive.Signature));
        Assert.True(Verifies(toSix.Body, toSix.Signature));
        Assert.Equal(toSix.Body.Issued, toFive.Body.Issued);

        // The deny descriptor is narrowed from the same per-user form.
        ModManifest.OnHandshakeDenied(5, new List<string> { Trade });
        Assert.NotNull(ModManifest.TestDenyEnvelope!.Body.Mods.Single().Peers);
        Assert.True(Verifies(ModManifest.TestDenyEnvelope.Body, ModManifest.TestDenyEnvelope.Signature));
        ModManifest.OnHandshakeDenied(6, new List<string> { Trade });
        Assert.Null(ModManifest.TestDenyEnvelope!.Body.Mods.Single().Peers);

        Assert.True(ModManifest.TryBuildInstallDescriptor(5, new List<string> { Trade }, joinFlowClient: false, out var descriptor));
        Assert.NotNull(descriptor!.Body.Mods.Single().Peers);
    }

    [Fact]
    public void A_version_matching_change_re_signs_both_forms_and_asks_for_a_resend()
    {
        ModManifest.TestLoadedMods = Loaded();
        ModManifest.TestIsServer = true;
        _ = ModManifest.Describe();
        Assert.NotNull(ModManifest.ExtendedEnvelope);
        Assert.Null(ModManifest.ExtendedEnvelope!.Body.Mods.Single(m => m.Id == Trade).Match);
        ModManifest.TakeCatalogResend(out _, out _);

        // Unchanged settings: nothing to re-sign.
        var before = ModManifest.ExtendedEnvelope;
        ModManifest.ResignForVersionMatching();
        Assert.Same(before, ModManifest.ExtendedEnvelope);
        Assert.False(ModManifest.TakeCatalogResend(out _, out _));

        VersionMatching.SettingsSource = () => new VersionMatchSettings(VersionMatchingMode.Exact);
        ModManifest.ResignForVersionMatching();
        Assert.NotSame(before, ModManifest.ExtendedEnvelope);
        Assert.All(ModManifest.ExtendedEnvelope!.Body.Mods, m => Assert.Equal("exact", m.Match));
        Assert.All(ModManifest.LastEnvelope!.Body.Mods, m => Assert.Null(m.Match));
        Assert.True(ModManifest.TakeCatalogResend(out string reason, out _));
        Assert.Equal("version matching", reason);
    }

    [Fact]
    public void The_extended_form_signs_the_servers_version_matching_and_the_current_form_never_carries_it()
    {
        using var key = ModManifestCrypto.Generate();
        var (current, extended) = Forms(key, new VersionMatchSettings(VersionMatchingMode.Exact));
        Assert.Null(current.Body.Matching);
        Assert.Equal("exact", extended.Body.Matching);
        Assert.Equal("range", Forms(key).Extended.Body.Matching);

        string body = ModManifestJson.CanonicalBody(extended.Body);
        Assert.Contains("{\"issued\":\"2026-10-09T12:00:00Z\",\"matching\":\"exact\",\"mods\":[", body, StringComparison.Ordinal);
        Assert.DoesNotContain("matching", ModManifestJson.CanonicalBody(current.Body), StringComparison.Ordinal);

        // A C3 client reads it back exactly and verifies; a changed mode fails the signature.
        string json = ModManifestJson.CanonicalEnvelope(extended);
        Assert.True(ModManifestJson.TryParseEnvelope(json, out var parsed, out var error), error);
        Assert.Equal("exact", parsed.Body.Matching);
        Assert.True(Verifies(parsed.Body, parsed.Signature));
        Assert.True(ModManifestJson.TryParseEnvelope(json.Replace("\"matching\":\"exact\"", "\"matching\":\"range\"", StringComparison.Ordinal),
            out var tampered, out _));
        Assert.False(Verifies(tampered.Body, tampered.Signature));

        // A narrowed (deny) form keeps the mode it was signed with and still verifies.
        var narrowed = ModManifestDenyPolicy.Narrow(extended, new[] { Trade }, key);
        Assert.NotNull(narrowed);
        Assert.Equal("exact", narrowed!.Body.Matching);
        Assert.True(Verifies(narrowed.Body, narrowed.Signature));
    }

    [Fact]
    public void Every_version_matching_change_re_signs_even_a_catalog_without_rows_and_a_rule_re_signs_its_row()
    {
        // No catalog rows: before the mode was part of the extended form nothing in the stamp moved, so a change of
        // Version matching was never re-signed and connected clients kept the mode their join's request bits announced.
        ModManifest.TestIsServer = true;
        _ = ModManifest.Describe();
        var first = ModManifest.ExtendedEnvelope!;
        Assert.Equal("range", first.Body.Matching);
        ModManifest.TakeCatalogResend(out _, out _);

        VersionMatching.SettingsSource = () => new VersionMatchSettings(VersionMatchingMode.Minor);
        ModManifest.ResignForVersionMatching();
        Assert.NotSame(first, ModManifest.ExtendedEnvelope);
        Assert.Equal("minor", ModManifest.ExtendedEnvelope!.Body.Matching);
        Assert.Null(ModManifest.LastEnvelope!.Body.Matching);
        Assert.True(ModManifest.TakeCatalogResend(out string reason, out _));
        Assert.Equal("version matching", reason);

        // With rows: a per-mod rule for one of them re-signs, under an unchanged mode, with only that row's match.
        ModManifest.TestLoadedMods = Loaded();
        VersionMatching.SettingsSource = () => VersionMatchSettings.Default;
        ModManifest.ResignForVersionMatching();
        var ranges = ModManifest.ExtendedEnvelope!;
        ModManifest.TakeCatalogResend(out _, out _);
        VersionMatching.SettingsSource = () => new VersionMatchSettings(VersionMatchingMode.Range,
            new Dictionary<string, ClientModRule> { [Chat] = ClientModRule.Any });
        ModManifest.ResignForVersionMatching();
        Assert.NotSame(ranges, ModManifest.ExtendedEnvelope);
        Assert.Equal("range", ModManifest.ExtendedEnvelope!.Body.Matching);
        Assert.Equal("any", ModManifest.ExtendedEnvelope.Body.Mods.Single(m => m.Id == Chat).Match);
        Assert.Null(ModManifest.ExtendedEnvelope.Body.Mods.Single(m => m.Id == Trade).Match);
        Assert.True(ModManifest.TakeCatalogResend(out _, out _));

        // The same settings again: nothing to re-sign.
        var same = ModManifest.ExtendedEnvelope;
        ModManifest.ResignForVersionMatching();
        Assert.Same(same, ModManifest.ExtendedEnvelope);
        Assert.False(ModManifest.TakeCatalogResend(out _, out _));
    }
}
