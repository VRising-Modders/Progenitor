using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// R24 findings 2 and 4: envelope reassembly, unsigned storage, uninstall /
/// world-change clear, fingerprint/SPKI trust, replay, and catalog hash presence.
/// </summary>
[Collection("ModManifest")]
public sealed class ModManifestEq3Tests : IDisposable
{
    private readonly string _dir;
    private readonly ModManifestKey _key;

    public ModManifestEq3Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-modmanifest-eq3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = "127.0.0.1:9976";
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
        ModManifest.TestConnectHost = null;
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort temp cleanup */ }
    }

    [Fact]
    public void Second_envelope_with_the_same_count_replaces_the_first()
    {
        var first = SignedAt(new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc), "first.mod");
        var second = SignedAt(new DateTime(2026, 9, 3, 19, 0, 0, DateTimeKind.Utc), "second.mod");
        var packets1 = ModManifestWire.Pack(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(first)));
        var packets2 = ModManifestWire.Pack(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(second)));
        Assert.Single(packets1);
        Assert.Single(packets2);

        var re = new ModManifestWire.Reassembly();
        Assert.True(ModManifestWire.TryRead(packets1[0], out int seq, out int count, out _, out var s1, out var error), error);
        Assert.True(re.TryAdd(seq, count, s1, out var done, out error), error);
        Assert.NotNull(done);
        byte[] firstBytes = done!;

        Assert.True(ModManifestWire.TryRead(packets2[0], out seq, out count, out _, out var s2, out error), error);
        Assert.True(re.TryAdd(seq, count, s2, out done, out error), error);
        Assert.NotNull(done);
        Assert.NotEqual(firstBytes, done);
        Assert.Equal(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(second)), done);

        // Duplicate of the completed second envelope does not re-emit.
        Assert.True(re.TryAdd(seq, count, s2, out done, out error), error);
        Assert.Null(done);
    }

    [Fact]
    public void Duplicate_chunk_never_re_emits_through_AcceptCatalogPacket()
    {
        var envelope = SignedSample();
        var packets = ModManifestWire.Pack(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope)));
        ModManifest.AcceptCatalogPacketForTests(packets[0]);
        Assert.NotNull(ModManifest.LastEnvelope);
        Assert.Equal("progenitor.sample", ModManifest.LastEnvelope!.Body.Mods[0].Id);
        var log = (TestLogSource)Plugin.Logger!;
        int infos = log.Infos.Count;

        ModManifest.AcceptCatalogPacketForTests(packets[0]);
        Assert.Equal("progenitor.sample", ModManifest.LastEnvelope!.Body.Mods[0].Id);
        Assert.Equal(infos, log.Infos.Count);
    }

    [Fact]
    public void Bad_signature_is_not_stored_and_Classify_does_not_walk_mods()
    {
        var good = SignedSample();
        char flip = good.Signature[0] == 'a' ? 'b' : 'a';
        var bad = new ManifestEnvelope(good.Body, flip + good.Signature.Substring(1));
        var diff = ModManifestCompare.Classify(bad, Array.Empty<LoadedModInfo>(), trust: null);
        Assert.False(diff.SignatureValid);
        Assert.Empty(diff.Present);
        Assert.Empty(diff.Missing);
        Assert.Empty(diff.Mismatches);
        Assert.False(diff.UnverifiedOperator);

        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(bad)));
        Assert.Null(ModManifest.LastEnvelope);
        Assert.Null(ModManifest.Pending);
        var log = (TestLogSource)Plugin.Logger!;
        Assert.Contains(log.Warnings, line => line.Contains("Manifest signature does not match key", StringComparison.Ordinal));
    }

    [Fact]
    public void Disconnect_then_another_server_does_not_freeze_the_first_catalog()
    {
        var serverA = SignedAt(new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc), "server.a");
        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(serverA)));
        Assert.Equal("server.a", ModManifest.LastEnvelope!.Body.Mods[0].Id);
        Assert.NotNull(ModManifest.Pending);

        ModManifest.NotifyClientWorldChanged();
        Assert.Null(ModManifest.LastEnvelope);
        Assert.Null(ModManifest.Pending);
        Assert.Null(ModManifest.LastDiff);

        var serverB = SignedAt(new DateTime(2026, 9, 3, 19, 0, 0, DateTimeKind.Utc), "server.b");
        var packets = ModManifestWire.Pack(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(serverB)));
        Assert.Single(packets);
        ModManifest.AcceptCatalogPacketForTests(packets[0]);
        Assert.NotNull(ModManifest.LastEnvelope);
        Assert.Equal("server.b", ModManifest.LastEnvelope!.Body.Mods[0].Id);
        Assert.DoesNotContain(ModManifest.LastEnvelope.Body.Mods, m => m.Id == "server.a");
    }

    [Fact]
    public void Uninstall_clears_installed_envelope_and_pending()
    {
        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(SignedSample())));
        Assert.NotNull(ModManifest.LastEnvelope);
        _ = ModManifest.Describe();
        Assert.True(ModManifest.TestIsInstalled);

        ModManifest.ResetForTests();
        Assert.False(ModManifest.TestIsInstalled);
        Assert.Null(ModManifest.LastEnvelope);
        Assert.Null(ModManifest.Pending);
        Assert.Null(ModManifest.LastDiff);
    }

    [Fact]
    public void Plugin_Uninstall_calls_ModManifest_Uninstall()
    {
        string src = ReadRepoFile("src", "Progenitor.Core", "Plugin.cs");
        SourcePin.AssertStatement(src, "Networking.ModManifest.Uninstall();");
    }

    [Fact]
    public void Fingerprint_does_not_match_embedded_key_is_rejected()
    {
        using var attacker = ModManifestCrypto.Generate();
        var fakeOp = new ManifestOperator("Ada", attacker.SpkiBase64, _key.Fingerprint);
        var body = new ManifestBody(
            ModManifestJson.SchemaVersion,
            "Night Garden",
            "2026-09-03T18:00:00Z",
            fakeOp,
            null,
            new[]
            {
                new ManifestMod("evil.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
            });
        string sig = attacker.Sign(ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(body)));
        var envelope = new ManifestEnvelope(body, sig);

        var diff = ModManifestCompare.Classify(envelope, Array.Empty<LoadedModInfo>(), trust: null);
        Assert.False(diff.SignatureValid);
        Assert.Equal(ModManifestCompare.FingerprintRejectedLog, diff.SignatureError);
        Assert.Empty(diff.Missing);

        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope)));
        Assert.Null(ModManifest.LastEnvelope);
        Assert.Null(ModManifest.Pending);
        var log = (TestLogSource)Plugin.Logger!;
        Assert.Contains(log.Warnings, line => line == ModManifestCompare.FingerprintRejectedLog);
    }

    [Fact]
    public void Fp_only_trust_row_is_refused()
    {
        var envelope = SignedSample();
        var trust = new TrustedOperatorsFile
        {
            Operators = new[]
            {
                new TrustedOperator { Fp = _key.Fingerprint, Spki = "", Always = true },
            },
        };
        var diff = ModManifestCompare.Classify(
            envelope,
            Array.Empty<LoadedModInfo>(),
            trust,
            "127.0.0.1:9976");
        Assert.True(diff.SignatureValid);
        Assert.False(diff.OperatorTrusted);
        Assert.True(diff.UnverifiedOperator);
        Assert.False(ModManifestJson.IsTrusted(trust, _key.Fingerprint, _key.SpkiBase64));
        var log = (TestLogSource)Plugin.Logger!;
        Assert.Contains(log.Warnings, line => line == ModManifestCompare.FpOnlyTrustRowLog(_key.Fingerprint));
        int warns = log.Warnings.Count;
        _ = ModManifestCompare.Classify(envelope, Array.Empty<LoadedModInfo>(), trust, "127.0.0.1:9976");
        Assert.Equal(warns, log.Warnings.Count);
    }

    [Fact]
    public void Replay_of_an_older_issued_envelope_is_rejected()
    {
        string lastSeen = "2026-09-03T19:00:00Z";
        var trust = new TrustedOperatorsFile
        {
            Operators = new[]
            {
                new TrustedOperator
                {
                    Host = "127.0.0.1:9976",
                    Spki = _key.SpkiBase64,
                    Fp = _key.Fingerprint,
                    Always = true,
                    Issued = lastSeen,
                },
            },
        };
        File.WriteAllText(
            Path.Combine(_dir, ModManifest.TrustedFileName),
            ModManifestJson.CanonicalTrust(trust));

        var older = SignedAt(new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc), "old.mod");
        var diff = ModManifestCompare.Classify(older, Array.Empty<LoadedModInfo>(), trust, "127.0.0.1:9976");
        Assert.True(diff.Replayed);
        Assert.True(diff.SignatureValid);
        Assert.Empty(diff.Missing);
        string want = ModManifestCompare.ReplayRejectedLog(older.Body.Issued, lastSeen);
        Assert.Equal(want, diff.ReplayError);

        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(older)));
        Assert.Null(ModManifest.LastEnvelope);
        Assert.Null(ModManifest.Pending);
        var log = (TestLogSource)Plugin.Logger!;
        Assert.Contains(log.Warnings, line => line == want);
    }

    [Fact]
    public void Hash_present_mismatch_and_not_checked()
    {
        string match = new string('a', 64);
        string other = new string('b', 64);
        var loaded = new[]
        {
            new LoadedModInfo("ok.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
            new LoadedModInfo("bad.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
            new LoadedModInfo("plain.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
        };
        var overrides = new ManifestOverrides
        {
            ServerName = "Night Garden",
            OperatorName = "Ada",
            Mods = new[]
            {
                new ManifestModOverride { Id = "ok.mod", Sha256 = match },
                new ManifestModOverride { Id = "bad.mod", Sha256 = match },
            },
        };
        var body = ModManifestJson.BuildBody(
            loaded,
            overrides,
            _key.ToOperator("Ada"),
            new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc));
        string sig = _key.Sign(ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(body)));
        var envelope = new ManifestEnvelope(body, sig);

        var client = new[]
        {
            new LoadedModInfo("ok.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true, match),
            new LoadedModInfo("bad.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true, other),
            new LoadedModInfo("plain.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
        };
        var diff = ModManifestCompare.Classify(envelope, client, trust: null);
        Assert.Contains(diff.Present, m => m.Id == "ok.mod");
        Assert.DoesNotContain(diff.HashNotCheckedIds, id => id == "ok.mod");
        Assert.Contains(diff.Present, m => m.Id == "plain.mod");
        Assert.Contains(diff.HashNotCheckedIds, id => id == "plain.mod");
        Assert.Contains(diff.Mismatches, m => m.Wanted.Id == "bad.mod" && m.HashMismatch);
        string block = ModManifestCompare.FormatLogBlock(envelope, diff);
        Assert.Contains("ok.mod 1.0.0", block);
        Assert.DoesNotContain("ok.mod 1.0.0 (hash not checked)", block);
        Assert.Contains("plain.mod 1.0.0 (hash not checked)", block);
        Assert.Contains("bad.mod mismatch (hash)", block);
    }

    [Fact]
    public void TrustOperator_without_a_connect_host_writes_a_host_less_row()
    {
        ModManifest.TestConnectHost = "";
        var envelope = SignedSample();
        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope)));
        Assert.True(ModManifest.TrustOperator(ModManifest.Pending!.Fingerprint, always: true));
        var file = ModManifestTrust.ReadFile(Path.Combine(_dir, ModManifest.TrustedFileName));
        Assert.Single(file.Operators);
        Assert.Equal("", file.Operators[0].Host);
        Assert.Equal(_key.SpkiBase64, file.Operators[0].Spki);
        var log = (TestLogSource)Plugin.Logger!;
        Assert.Contains(log.Infos, line => line.Contains("host-less", StringComparison.Ordinal));
    }

    private ManifestEnvelope SignedSample() =>
        SignedAt(new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc), "progenitor.sample");

    private ManifestEnvelope SignedAt(DateTime issuedUtc, string id)
    {
        var body = ModManifestJson.BuildBody(
            new[]
            {
                new LoadedModInfo(id, "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
            },
            new ManifestOverrides { ServerName = "Night Garden", OperatorName = "Ada" },
            _key.ToOperator("Ada"),
            issuedUtc);
        string sig = _key.Sign(ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(body)));
        return new ManifestEnvelope(body, sig);
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(parts));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new InvalidOperationException(string.Join("/", parts) + " not found");
    }
}
