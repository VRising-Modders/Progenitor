using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the phase-1 mod catalog: canonical JSON, ECDSA P-256
/// sign/verify, tamper detection, missing/mismatch classification, and the
/// unverified-operator path. Does not touch BepInEx/config or the game.
/// </summary>
[Collection("ModManifest")]
public sealed class ModManifestTests : IDisposable
{
    private readonly string _dir;
    private readonly ModManifestKey _key;

    public ModManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-modmanifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = true;
        _key = ModManifestCrypto.Generate();
        Plugin.Logger = new TestLogSource();
    }

    public void Dispose()
    {
        _key.Dispose();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestIsServer = false;
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void Canonical_JSON_is_stable_across_input_order()
    {
        var issued = new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc);
        var op = _key.ToOperator("Ada");
        var a = new LoadedModInfo("z.mod", "2.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, hotLoadable: false);
        var b = new LoadedModInfo("a.mod", "1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, hotLoadable: true);
        var body1 = ModManifestJson.BuildBody(new[] { a, b }, null, op, issued);
        var body2 = ModManifestJson.BuildBody(new[] { b, a }, null, op, issued);
        string c1 = ModManifestJson.CanonicalBody(body1);
        string c2 = ModManifestJson.CanonicalBody(body2);
        Assert.Equal(c1, c2);
        Assert.DoesNotContain("\n", c1);
        Assert.DoesNotContain(": ", c1);
        Assert.DoesNotContain(", ", c1);
        Assert.StartsWith("{\"issued\":\"2026-09-03T18:00:00Z\"", c1);
        Assert.Contains("\"id\":\"a.mod\"", c1);
        int ia = c1.IndexOf("\"id\":\"a.mod\"", StringComparison.Ordinal);
        int iz = c1.IndexOf("\"id\":\"z.mod\"", StringComparison.Ordinal);
        Assert.True(ia >= 0 && iz > ia, "mods must be sorted by id");
        Assert.Contains("\"schema\":1", c1);
        Assert.Contains("\"hotLoadable\":true", c1);
        Assert.Contains("\"level\":\"ServerMustHave\"", c1);
        Assert.Contains("\"versionRule\":\"Exact\"", c1);
        Assert.Contains("\"fp\":\"" + _key.Fingerprint + "\"", c1);
    }

    [Fact]
    public void Canonical_round_trips_through_parse()
    {
        var envelope = SignedSample();
        string json = ModManifestJson.CanonicalEnvelope(envelope);
        Assert.True(ModManifestJson.TryParseEnvelope(json, out var parsed, out var error), error);
        Assert.Equal(ModManifestJson.CanonicalBody(envelope.Body), ModManifestJson.CanonicalBody(parsed.Body));
        Assert.Equal(envelope.Signature, parsed.Signature);
        Assert.Equal(envelope.Body.Operator.Fp, parsed.Body.Operator.Fp);
        Assert.Equal("progenitor.sample", parsed.Body.Mods[0].Id);
    }

    [Fact]
    public void Sign_verify_round_trip()
    {
        var envelope = SignedSample();
        byte[] utf8 = ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(envelope.Body));
        Assert.True(ModManifestCrypto.Verify(_key.SpkiBase64, utf8, envelope.Signature));
        Assert.Equal(ModManifestCrypto.SignatureHexLength, envelope.Signature.Length);
        Assert.Equal(ModManifestCrypto.FingerprintHexLength, _key.Fingerprint.Length);
        Assert.Equal(_key.Fingerprint, ModManifestCrypto.Fingerprint(_key.Spki));
    }

    [Fact]
    public void Tamper_with_body_fails_verify()
    {
        var envelope = SignedSample();
        var tamperedMod = new ManifestMod(
            envelope.Body.Mods[0].Id,
            "9.9.9",
            envelope.Body.Mods[0].Level,
            envelope.Body.Mods[0].VersionRule,
            envelope.Body.Mods[0].HotLoadable);
        var tampered = new ManifestBody(
            envelope.Body.Schema,
            envelope.Body.ServerName,
            envelope.Body.Issued,
            envelope.Body.Operator,
            envelope.Body.ProfileCode,
            new[] { tamperedMod });
        byte[] utf8 = ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(tampered));
        Assert.False(ModManifestCrypto.Verify(_key.SpkiBase64, utf8, envelope.Signature));
    }

    [Fact]
    public void Tamper_with_signature_fails_verify()
    {
        var envelope = SignedSample();
        byte[] utf8 = ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(envelope.Body));
        char flip = envelope.Signature[0] == 'a' ? 'b' : 'a';
        string bad = flip + envelope.Signature.Substring(1);
        Assert.False(ModManifestCrypto.Verify(_key.SpkiBase64, utf8, bad));
        Assert.False(ModManifestCrypto.Verify(_key.SpkiBase64, utf8, ""));
        Assert.False(ModManifestCrypto.Verify(_key.SpkiBase64, utf8, "00"));
    }

    [Fact]
    public void Missing_and_mismatch_classification()
    {
        var envelope = SignedCatalog(
            new LoadedModInfo("keep.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
            new LoadedModInfo("gone.mod", "2.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, false),
            new LoadedModInfo("ver.mod", "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
            new LoadedModInfo("minor.mod", "1.2.9", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor, true),
            new LoadedModInfo("ui.mod", "3.0.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.None, true));

        var client = new[]
        {
            new LoadedModInfo("keep.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
            new LoadedModInfo("ver.mod", "1.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
            new LoadedModInfo("minor.mod", "1.2.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor, true),
        };
        var diff = ModManifestCompare.Classify(envelope, client, trust: null);

        Assert.True(diff.SignatureValid);
        Assert.False(diff.OperatorTrusted);
        Assert.True(diff.UnverifiedOperator);
        Assert.Contains(diff.Present, m => m.Id == "keep.mod");
        Assert.Contains(diff.Present, m => m.Id == "minor.mod");
        Assert.Contains(diff.Missing, m => m.Id == "gone.mod");
        Assert.Contains(diff.Missing, m => m.Id == "ui.mod");
        Assert.Contains(diff.Mismatches, m => m.Wanted.Id == "ver.mod" && m.Have == "1.1.0");
        Assert.Contains(diff.RestartRequired, m => m.Id == "gone.mod");
        Assert.DoesNotContain(diff.RestartRequired, m => m.Id == "ver.mod");
        Assert.DoesNotContain(diff.RestartRequired, m => m.Id == "ui.mod");
    }

    [Fact]
    public void Unverified_operator_path_logs_first_contact_and_does_not_reject()
    {
        var envelope = SignedSample();
        var client = new[]
        {
            new LoadedModInfo("progenitor.sample", "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
        };
        var diff = ModManifestCompare.Classify(envelope, client, new TrustedOperatorsFile());
        Assert.True(diff.SignatureValid);
        Assert.True(diff.UnverifiedOperator);
        Assert.False(diff.OperatorTrusted);
        Assert.Null(diff.SignatureError);
        string block = ModManifestCompare.FormatLogBlock(envelope, diff);
        Assert.Contains("unverified operator Ada fp=" + _key.Fingerprint, block);
        Assert.Contains("first contact; not in progenitor-trusted-operators.json", block);
        Assert.Contains("present (1): progenitor.sample 0.1.0", block);
        Assert.Contains("missing (0):", block);
        Assert.Contains("mismatch (0):", block);
        Assert.Contains("restart-required (0):", block);
    }

    [Fact]
    public void Trusted_operator_is_not_unverified()
    {
        var envelope = SignedSample();
        var trust = new TrustedOperatorsFile
        {
            Operators = new[]
            {
                new TrustedOperator { Fp = _key.Fingerprint, Spki = _key.SpkiBase64, Always = true },
            },
        };
        var diff = ModManifestCompare.Classify(
            envelope,
            new[] { new LoadedModInfo("progenitor.sample", "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true) },
            trust);
        Assert.True(diff.OperatorTrusted);
        Assert.False(diff.UnverifiedOperator);
        string block = ModManifestCompare.FormatLogBlock(envelope, diff);
        Assert.Contains("operator Ada fp=" + _key.Fingerprint + " (trusted)", block);
        Assert.DoesNotContain("unverified", block);
    }

    [Fact]
    public void Missing_row_includes_Thunderstore_package_name()
    {
        var loaded = new[]
        {
            new LoadedModInfo("pack.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, false),
        };
        var overrides = new ManifestOverrides
        {
            Mods = new[]
            {
                new ManifestModOverride
                {
                    Id = "pack.mod",
                    Package = "ExampleTeam-Pack",
                    Namespace = "ExampleTeam",
                    Sha256 = "ab" + new string('0', 62),
                    Size = 12,
                },
            },
        };
        var body = ModManifestJson.BuildBody(loaded, overrides, _key.ToOperator("Ada"), new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc));
        string sig = _key.Sign(ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(body)));
        var envelope = new ManifestEnvelope(body, sig);
        var diff = ModManifestCompare.Classify(envelope, Array.Empty<LoadedModInfo>(), trust: null);
        string block = ModManifestCompare.FormatLogBlock(envelope, diff);
        Assert.Contains("missing (1): pack.mod 1.0.0 (Thunderstore ExampleTeam-Pack)", block);
        Assert.Contains("restart-required (1): pack.mod", block);
        Assert.Equal("ab" + new string('0', 62), body.Mods[0].Sha256);
        Assert.Equal(12, body.Mods[0].Size);
    }

    [Fact]
    public void Framework_guid_is_excluded_from_the_catalog()
    {
        var body = ModManifestJson.BuildBody(
            new[]
            {
                new LoadedModInfo("progenitor", "0.1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false),
                new LoadedModInfo("progenitor.sample", "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
            },
            null,
            _key.ToOperator("Ada"),
            new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc));
        Assert.Single(body.Mods);
        Assert.Equal("progenitor.sample", body.Mods[0].Id);
    }

    [Theory]
    [InlineData("progenitor.scratch", false)]
    [InlineData("Progenitor.Scratch", false)]
    [InlineData("progenitor.scratch", true)]
    [InlineData("Progenitor.Scratch", true)]
    public void Ephemeral_exec_host_is_excluded_from_loaded_and_override_catalogs(string id, bool overrideOnly)
    {
        var loaded = overrideOnly ? Array.Empty<LoadedModInfo>() : new[]
        {
            new LoadedModInfo(id, "0.1.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, true),
        };
        var overrides = overrideOnly ? new ManifestOverrides
        {
            Mods = new[] { new ManifestModOverride { Id = id, Version = "0.1.0", Package = "Dev-Scratch" } },
        } : null;

        var body = ModManifestJson.BuildBody(loaded, overrides, _key.ToOperator("Ada"), DateTime.UtcNow);

        Assert.Empty(body.Mods);
    }

    [Fact]
    public void Exec_host_filter_preserves_prefix_neighbors_and_other_optional_mods()
    {
        var body = ModManifestJson.BuildBody(
            new[]
            {
                new LoadedModInfo("progenitor.scratch.extra", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, true),
                new LoadedModInfo("ordinary.optional", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, true),
            },
            new ManifestOverrides
            {
                Mods = new[] { new ManifestModOverride { Id = "progenitor.scratchpad", Version = "1.0.0" } },
            },
            _key.ToOperator("Ada"),
            DateTime.UtcNow);

        Assert.Collection(body.Mods,
            m => Assert.Equal("ordinary.optional", m.Id),
            m => Assert.Equal("progenitor.scratch.extra", m.Id),
            m => Assert.Equal("progenitor.scratchpad", m.Id));
    }

    [Fact]
    public void Override_only_id_is_kept_so_a_client_mod_can_be_listed()
    {
        var overrides = new ManifestOverrides
        {
            ServerName = "Night Garden",
            OperatorName = "Ada",
            Mods = new[]
            {
                new ManifestModOverride
                {
                    Id = "client.only",
                    Version = "1.2.3",
                    Level = NetworkCompatibilityLevel.ClientOnly,
                    VersionRule = NetworkVersionRule.None,
                    HotLoadable = true,
                    Package = "ExampleTeam-Ui",
                },
            },
        };
        var body = ModManifestJson.BuildBody(Array.Empty<LoadedModInfo>(), overrides, _key.ToOperator("x"), DateTime.UtcNow);
        Assert.Equal("Night Garden", body.ServerName);
        Assert.Equal("Ada", body.Operator.Name);
        Assert.Single(body.Mods);
        Assert.Equal("client.only", body.Mods[0].Id);
        Assert.Equal(NetworkCompatibilityLevel.ClientOnly, body.Mods[0].Level);
        Assert.True(body.Mods[0].HotLoadable);
    }

    [Fact]
    public void Operator_override_file_parses_array_and_map_shapes()
    {
        const string arrayJson = """
            {
              "serverName": "Night Garden",
              "operatorName": "Ada",
              "mods": [
                { "id": "a.mod", "package": "Ns-A", "sha256": "AA", "size": 4 }
              ]
            }
            """;
        Assert.True(ModManifestJson.TryParseOverrides(arrayJson, out var a, out var err), err);
        Assert.Equal("Night Garden", a.ServerName);
        Assert.Equal("a.mod", a.Mods[0].Id);
        Assert.Null(a.Mods[0].Sha256);

        const string mapJson = """
            { "mods": { "b.mod": { "url": "https://example.test/b.zip", "hotLoadable": false } } }
            """;
        Assert.True(ModManifestJson.TryParseOverrides(mapJson, out var b, out err), err);
        Assert.Equal("b.mod", b.Mods[0].Id);
        Assert.Equal("https://example.test/b.zip", b.Mods[0].Url);
        Assert.False(b.Mods[0].HotLoadable);
    }

    [Fact]
    public void NormalizeSha_rejects_non_64_hex()
    {
        Assert.Null(ModManifestJson.NormalizeSha("AA"));
        Assert.Null(ModManifestJson.NormalizeSha("not-hex"));
        Assert.Null(ModManifestJson.NormalizeSha(new string('g', 64)));
        Assert.Null(ModManifestJson.NormalizeSha(""));
        string ok = new string('a', 64);
        Assert.Equal(ok, ModManifestJson.NormalizeSha("0x" + ok.ToUpperInvariant()));
    }

    [Fact]
    public void Trust_file_parses_and_matches_fingerprint()
    {
        string json = "{ \"operators\": [ { \"fp\": \"" + _key.Fingerprint + "\", \"spki\": \"" + _key.SpkiBase64 + "\", \"always\": true } ] }";
        Assert.True(ModManifestJson.TryParseTrust(json, out var file, out var error), error);
        Assert.True(ModManifestJson.IsTrusted(file, _key.Fingerprint, _key.SpkiBase64));
        Assert.False(ModManifestJson.IsTrusted(file, "ffffffffffffffff", _key.SpkiBase64));
    }

    [Fact]
    public void Wire_chunks_round_trip_a_large_envelope()
    {
        var payload = Encoding.UTF8.GetBytes(new string('x', (15 * 1024) + 50));
        var packets = ModManifestWire.Pack(payload);
        Assert.Equal(2, packets.Count);
        Assert.Equal(ModManifestWire.FlagMore, packets[0][1] & ModManifestWire.FlagMore);
        Assert.Equal(0, packets[1][1] & ModManifestWire.FlagMore);
        var re = new ModManifestWire.Reassembly();
        Assert.True(ModManifestWire.TryRead(packets[1], out int seq1, out int count, out _, out var s1, out var error), error);
        Assert.True(re.TryAdd(seq1, count, s1, out var done, out error), error);
        Assert.Null(done);
        Assert.True(ModManifestWire.TryRead(packets[0], out int seq0, out count, out _, out var s0, out error), error);
        Assert.True(re.TryAdd(seq0, count, s0, out done, out error), error);
        Assert.NotNull(done);
        Assert.Equal(payload, done);
    }

    [Fact]
    public void Describe_on_the_server_prints_the_signed_catalog()
    {
        ModManifest.TestLoadedMods = new[]
        {
            new LoadedModInfo("progenitor.sample", "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
        };
        File.WriteAllText(Path.Combine(_dir, ModManifest.ManifestFileName), "{ \"serverName\": \"Night Garden\", \"operatorName\": \"Ada\" }");
        string text = ModManifest.Describe();
        Assert.Contains("ModManifest server", text);
        Assert.Contains("progenitor.sample 0.1.0 ServerMustHave/Exact hotLoadable", text);
        Assert.Contains("signed=true", text);
        Assert.Contains("fp=", text);
        Assert.NotNull(ModManifest.LastEnvelope);
        Assert.Equal("Night Garden", ModManifest.LastEnvelope!.Body.ServerName);
        Assert.Equal("Ada", ModManifest.LastEnvelope.Body.Operator.Name);
    }

    [Fact]
    public void Key_files_round_trip_through_comment_stripping()
    {
        string priv = Path.Combine(_dir, ModManifest.PrivateKeyFileName);
        var sb = new StringBuilder();
        sb.AppendLine("# comment");
        sb.AppendLine(Convert.ToBase64String(_key.Pkcs8));
        File.WriteAllText(priv, sb.ToString());
        string b64 = ModManifest.StripKeyComments(File.ReadAllText(priv));
        Assert.True(ModManifestCrypto.TryImportPkcs8(Convert.FromBase64String(b64), out var loaded, out var error), error);
        using (loaded)
        {
            Assert.Equal(_key.Fingerprint, loaded.Fingerprint);
            byte[] msg = ModManifestCrypto.Utf8("hello");
            string sig = loaded.Sign(msg);
            Assert.True(ModManifestCrypto.Verify(_key.Spki, msg, sig));
        }
    }

    [Fact]
    public void AcceptEnvelope_logs_the_unverified_block()
    {
        var envelope = SignedSample();
        ModManifest.TestIsServer = false;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        byte[] utf8 = ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope));
        ModManifest.AcceptEnvelope(utf8);
        var diff = ModManifest.LastDiff;
        Assert.NotNull(diff);
        Assert.True(diff!.UnverifiedOperator);
        Assert.Contains(diff.Missing, m => m.Id == "progenitor.sample");
        var log = (TestLogSource)Plugin.Logger!;
        Assert.Contains(log.Infos, line => line.Contains("unverified operator Ada"));
    }

    private ManifestEnvelope SignedSample()
    {
        return SignedCatalog(
            new LoadedModInfo(
                "progenitor.sample",
                "0.1.0",
                NetworkCompatibilityLevel.ServerMustHave,
                NetworkVersionRule.Exact,
                hotLoadable: true));
    }

    private ManifestEnvelope SignedCatalog(params LoadedModInfo[] loaded)
    {
        var overrides = new ManifestOverrides
        {
            ServerName = "Night Garden",
            OperatorName = "Ada",
        };
        var body = ModManifestJson.BuildBody(
            loaded,
            overrides,
            _key.ToOperator("Ada"),
            new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc));
        string sig = _key.Sign(ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(body)));
        return new ManifestEnvelope(body, sig);
    }
}
