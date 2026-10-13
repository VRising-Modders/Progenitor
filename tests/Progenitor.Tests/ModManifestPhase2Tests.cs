using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Phase 2: chunker/reassembler, trust-file atomic write, id validation, and the
/// fake handshake-ok hook order. Game-free; does not touch BepInEx/config.
/// </summary>
[Collection("ModManifest")]
public sealed class ModManifestPhase2Tests : IDisposable
{
    private readonly string _dir;
    private readonly ModManifestKey _key;

    public ModManifestPhase2Tests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-modmanifest-p2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = true;
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
    public void Id_validation_rejects_path_characters()
    {
        Assert.False(ModManifestIds.TryValidateId("", out _));
        Assert.False(ModManifestIds.TryValidateId("a/../b", out _));
        Assert.False(ModManifestIds.TryValidateId("a\\b", out _));
        Assert.False(ModManifestIds.TryValidateId("a:b", out _));
        Assert.False(ModManifestIds.TryValidateId("a*b", out _));
        Assert.False(ModManifestIds.TryValidateId(new string('a', 64), out _));
        Assert.True(ModManifestIds.TryValidateId("progenitor.sample", out _));
        Assert.False(ModManifestIds.TryValidateVersion("1/0", out _));
        Assert.True(ModManifestIds.TryValidateVersion("1.0.0-beta.1", out _));
        Assert.Equal("package.bin", ModManifestIds.SafeFileName(".."));
        Assert.Equal("package.bin", ModManifestIds.SafeFileName("evil*.dll"));
        Assert.Equal("ok.dll", ModManifestIds.SafeFileName(@"C:\mods\ok.dll"));
    }

    [Fact]
    public void Staging_path_stays_under_the_staging_root()
    {
        string root = Path.Combine(_dir, "staging");
        Assert.True(ModManifestIds.TryStagingPath(root, "fp0011", "a.mod", "1.0.0", "a.dll", out var dir, out var file, out var error), error);
        Assert.True(ModManifestIds.IsUnder(root, dir));
        Assert.True(ModManifestIds.IsUnder(root, file));
        Assert.False(ModManifestIds.TryStagingPath(root, "fp0011", "..", "1.0.0", "a.dll", out _, out _, out _));
        Assert.False(ModManifestIds.TryStagingPath(root, "fp0011", "a.mod", "..", "a.dll", out _, out _, out _));
    }

    /// <summary>
    /// R28 finding 11. Two servers that declare the same id, version and file name must not resolve
    /// to the same staged file: without the host segment the second one's
    /// <c>File.Move(overwrite: true)</c> replaced the first one's package. Fails on the old
    /// four-token path build, which had no host segment to differ in.
    /// </summary>
    [Fact]
    public void Staging_is_keyed_by_host_so_two_servers_never_share_a_file()
    {
        string root = Path.Combine(_dir, "staging");
        Assert.True(ModManifestIds.TryStagingPath(root, "aaaa1111", "a.mod", "1.0.0", "a.dll", out var dirA, out var fileA, out _));
        Assert.True(ModManifestIds.TryStagingPath(root, "bbbb2222", "a.mod", "1.0.0", "a.dll", out var dirB, out var fileB, out _));
        Assert.NotEqual(fileA, fileB);
        Assert.NotEqual(dirA, dirB);
        Assert.True(ModManifestIds.IsUnder(root, fileA));
        Assert.True(ModManifestIds.IsUnder(root, fileB));
        Assert.Contains("aaaa1111", fileA, StringComparison.Ordinal);
        Assert.Contains("bbbb2222", fileB, StringComparison.Ordinal);

        // A host key is never a wire token: anything unusable collapses to a bounded safe name,
        // and it can never introduce a path segment of its own.
        Assert.Equal("unknown", ModManifestIds.NormalizeHostKey(""));
        Assert.Equal("unknown", ModManifestIds.NormalizeHostKey("   "));
        Assert.Equal("unknown", ModManifestIds.NormalizeHostKey("NUL"));
        // R29 finding 21.10: the ':' has to go, but the folder still has to be this address and no
        // other one, so a key the sanitizer changed carries a digest of the original.
        var address = ModManifestIds.NormalizeHostKey("10.0.0.1:9876");
        Assert.StartsWith("10.0.0.1-9876-", address, StringComparison.Ordinal);
        Assert.NotEqual(address, ModManifestIds.NormalizeHostKey("10.0.0.1/9876"));
        Assert.DoesNotContain("/", ModManifestIds.NormalizeHostKey("a/b"), StringComparison.Ordinal);
        Assert.DoesNotContain("\\", ModManifestIds.NormalizeHostKey("a\\b"), StringComparison.Ordinal);
        Assert.True(ModManifestIds.NormalizeHostKey(new string('h', 500)).Length <= ModManifestIds.MaxHostKeyChars);
        Assert.True(ModManifestIds.TryStagingPath(root, "../../evil", "a.mod", "1.0.0", "a.dll", out _, out var escaped, out _));
        Assert.True(ModManifestIds.IsUnder(root, escaped));
    }

    /// <summary>
    /// R28 finding 28.12. A reserved DOS device name resolves to a device, not a file, and a token
    /// that only differs by trailing whitespace or a dot collapses onto another one on Windows —
    /// the old validator checked a trimmed copy and then combined the original.
    /// </summary>
    [Fact]
    public void Reserved_device_names_and_trailing_dot_or_space_tokens_are_refused()
    {
        Assert.Equal("package.bin", ModManifestIds.SafeFileName("NUL"));
        Assert.Equal("package.bin", ModManifestIds.SafeFileName("nul.dll"));
        Assert.Equal("package.bin", ModManifestIds.SafeFileName("COM1"));
        Assert.Equal("package.bin", ModManifestIds.SafeFileName("LPT9.txt"));
        Assert.Equal("ok.dll", ModManifestIds.SafeFileName("ok.dll"));

        Assert.False(ModManifestIds.TryValidateId("mod.", out _));
        Assert.False(ModManifestIds.TryValidateId("mod ", out _));
        Assert.False(ModManifestIds.TryValidateId(" mod", out _));
        Assert.False(ModManifestIds.TryValidateId("con", out _));
        Assert.True(ModManifestIds.TryValidateId("mod", out _));
    }

    [Fact]
    public void Trust_file_read_write_atomic_replace()
    {
        string path = Path.Combine(_dir, ModManifest.TrustedFileName);
        var first = new TrustedOperatorsFile
        {
            Operators = new[]
            {
                new TrustedOperator { Host = "127.0.0.1:9976", Fp = "aaaaaaaaaaaaaaaa", Spki = "s", Always = true, Seen = "2026-09-03T18:00:00Z" },
            },
        };
        ModManifestTrust.WriteAtomic(path, first);
        Assert.False(File.Exists(path + ".tmp"));
        var read = ModManifestTrust.ReadFile(path);
        Assert.Equal("aaaaaaaaaaaaaaaa", read.Operators[0].Fp);

        var second = ModManifestTrust.Upsert(read, "127.0.0.1:9976", "bbbbbbbbbbbbbbbb", "s2", always: true, new DateTime(2026, 9, 3, 19, 0, 0, DateTimeKind.Utc));
        ModManifestTrust.WriteAtomic(path, second);
        Assert.False(File.Exists(path + ".tmp"));
        var again = ModManifestTrust.ReadFile(path);
        Assert.Single(again.Operators);
        Assert.Equal("bbbbbbbbbbbbbbbb", again.Operators[0].Fp);
        Assert.True(again.Operators[0].Always);
        Assert.Contains("\"fp\":\"bbbbbbbbbbbbbbbb\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// Trust on first use (2026-10-05): with the consent panel gone, the first verified catalog from an address with no
    /// pin pins that key (always) and reclassifies the join as trusted.
    /// </summary>
    [Fact]
    public void First_contact_pins_the_operator_on_first_use()
    {
        var envelope = SignedSample();
        ModManifest.TestIsServer = false;
        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope)));
        var pending = ModManifest.Pending;
        Assert.NotNull(pending);
        Assert.False(pending!.FirstContact);
        Assert.True(pending.OperatorTrusted);
        Assert.False(pending.FingerprintChanged);
        Assert.Equal("127.0.0.1:9976", pending.Host);
        Assert.Contains(pending.Missing, m => m.Id == "progenitor.sample");

        string path = Path.Combine(_dir, ModManifest.TrustedFileName);
        Assert.True(File.Exists(path));
        var file = ModManifestTrust.ReadFile(path);
        var row = Assert.Single(file.Operators);
        Assert.Equal(pending.Fingerprint, row.Fp);
        Assert.Equal("127.0.0.1:9976", row.Host);
        Assert.True(row.Always);
        Assert.Contains(((TestLogSource)Plugin.Logger!).Infos, l => l.Contains("on first use", StringComparison.Ordinal));
    }

    /// <summary>
    /// A key change for an address that is already pinned is flagged and never re-pinned by first use.
    /// </summary>
    [Fact]
    public void A_key_change_is_not_pinned_on_first_use()
    {
        string path = Path.Combine(_dir, ModManifest.TrustedFileName);
        ModManifestTrust.WriteAtomic(path, new TrustedOperatorsFile
        {
            Operators = new[]
            {
                new TrustedOperator { Host = "127.0.0.1:9976", Fp = "deadbeefdeadbeef", Spki = "old", Always = true },
            },
        });

        ModManifest.TestIsServer = false;
        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(SignedSample())));

        var pending = ModManifest.Pending;
        Assert.NotNull(pending);
        Assert.True(pending!.FingerprintChanged);
        Assert.False(pending.OperatorTrusted);
        var row = Assert.Single(ModManifestTrust.ReadFile(path).Operators);
        Assert.Equal("deadbeefdeadbeef", row.Fp);
    }

    /// <summary>A trust file that exists but cannot be read is never rewritten by first use.</summary>
    [Fact]
    public void An_unreadable_trust_file_is_not_overwritten_by_first_use()
    {
        string path = Path.Combine(_dir, ModManifest.TrustedFileName);
        File.WriteAllText(path, "{ not json");

        ModManifest.TestIsServer = false;
        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(SignedSample())));

        Assert.Equal("{ not json", File.ReadAllText(path));
        Assert.False(ModManifest.Pending?.OperatorTrusted ?? false);
    }

    /// <summary>
    /// A trust file written before 2026-10-05 still carries <c>riskAck</c> rows from the retired consent panel. It
    /// still loads, and the operator stays trusted.
    /// </summary>
    [Fact]
    public void An_old_trust_file_with_risk_acknowledgements_still_loads()
    {
        const string old = "{\"version\":1,\"operators\":[{\"host\":\"127.0.0.1:9976\",\"fp\":\"aaaaaaaaaaaaaaaa\",\"spki\":\"s\",\"always\":true,\"seen\":\"2026-09-03T18:00:00Z\",\"riskAck\":\"2026-09-04T10:00:00Z\"}]}";
        Assert.True(ModManifestJson.TryParseTrust(old, out var file, out var error), error);
        var row = Assert.Single(file.Operators);
        Assert.Equal("aaaaaaaaaaaaaaaa", row.Fp);
        Assert.True(row.Always);
        Assert.Equal("2026-09-04T10:00:00Z", row.RiskAck);
    }

    [Fact]
    public void TrustOperator_still_writes_the_file()
    {
        var envelope = SignedSample();
        ModManifest.TestIsServer = false;
        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope)));
        var pending = ModManifest.Pending!;

        Assert.True(ModManifest.TrustOperator(pending.Fingerprint, always: true));
        var file = ModManifestTrust.ReadFile(Path.Combine(_dir, ModManifest.TrustedFileName));
        Assert.Equal(pending.Fingerprint, file.Operators[0].Fp);
        Assert.True(file.Operators[0].Always);
    }

    [Fact]
    public void Fingerprint_change_for_a_known_host_is_never_auto_accepted()
    {
        var first = SignedSample();
        var trust = new TrustedOperatorsFile
        {
            Operators = new[]
            {
                new TrustedOperator { Host = "127.0.0.1:9976", Fp = "deadbeefdeadbeef", Spki = "old", Always = true },
            },
        };
        var diff = ModManifestCompare.Classify(first, Array.Empty<LoadedModInfo>(), trust, "127.0.0.1:9976");
        Assert.True(diff.FingerprintChanged);
        Assert.False(diff.OperatorTrusted);
        Assert.False(diff.UnverifiedOperator);
        Assert.Equal("deadbeefdeadbeef", diff.PreviousFingerprint);
        string block = ModManifestCompare.FormatLogBlock(first, diff, "127.0.0.1:9976");
        Assert.Contains("operator key changed for host 127.0.0.1:9976", block, StringComparison.Ordinal);
        Assert.Contains("not accepted", block, StringComparison.Ordinal);
        Assert.DoesNotContain("unverified operator", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_OnResponse_source_calls_config_sync_then_manifest()
    {
        string src = ReadNetworkingSource("Handshake.cs");
        int config = src.IndexOf("ConfigSync.OnHandshakeOk(world, userEntity, answered, versions);", StringComparison.Ordinal);
        int manifest = src.IndexOf("ModManifest.OnHandshakeOk(user.Index);", StringComparison.Ordinal);
        Assert.True(config >= 0, "Handshake.OnResponse must call ConfigSync.OnHandshakeOk");
        Assert.True(manifest > config, "ModManifest.OnHandshakeOk must follow ConfigSync.OnHandshakeOk");
        string catalog = ReadNetworkingSource("ModManifest.cs");
        Assert.DoesNotContain("UserAnswerStatus", catalog);
    }

    private ManifestEnvelope SignedSample()
    {
        var body = ModManifestJson.BuildBody(
            new[]
            {
                new LoadedModInfo("progenitor.sample", "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true),
            },
            new ManifestOverrides { ServerName = "Night Garden", OperatorName = "Ada" },
            _key.ToOperator("Ada"),
            new DateTime(2026, 9, 3, 18, 0, 0, DateTimeKind.Utc));
        string sig = _key.Sign(ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(body)));
        return new ManifestEnvelope(body, sig);
    }

    private static string ReadNetworkingSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Progenitor.Core", "Networking", fileName);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new InvalidOperationException(fileName + " not found");
    }
}

[Collection("Handshake")]
public sealed class ModManifestHandshakeOrderTests : IDisposable
{
    public ModManifestHandshakeOrderTests()
    {
        Handshake.ResetTables();
        ConfigSync.TestHandshakeOkCalls = 0;
        Plugin.Logger = new TestLogSource();
    }

    public void Dispose()
    {
        Handshake.ResetTables();
        ConfigSync.TestHandshakeOkCalls = 0;
        Plugin.Logger = null;
    }

    [Fact]
    public void Fake_handshake_ok_path_is_handshake_then_config_then_manifest()
    {
        Handshake.NotifyHandshakeOk(4);
        Assert.Equal(new[] { "handshake", "config", "manifest" }, Handshake.TestOkHookOrder);
        Assert.Equal(1, ConfigSync.TestHandshakeOkCalls);
    }
}
