using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// HOSTKEY1: the server's host key is the dialed <c>address:port</c>, not the per-connection Steam relay string, and the
/// trust pins, Thunderstore confirmations and server profiles written under relay strings move to it once a verified
/// catalog names their operator. Game-free: nothing here has been observed in the game.
/// </summary>
[Collection("ModManifest")]
public sealed class ConnectHostKeyTests : IDisposable
{
    private const string Host = "127.0.0.1:9976";

    // Three connections to one test server on 2026-10-05.
    private const string RelayA = "Aqf27THwTQgI4Hk6FpSBAvJNEAA";
    private const string RelayB = "Aqf27THwTQgIuB3hF5SBAvJNEAA";
    private const string RelayC = "Aqf27THwTQgIEH2AFZSBAvJNEAA";
    private const string Policy = "{\"mode\":\"approved\",\"required\":[],\"approved\":[]}";

    private readonly string _dir;
    private readonly ModManifestKey _key;
    private readonly ManifestOperator _op;

    public ConnectHostKeyTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-hostkey-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Plugin.Logger = new TestLogSource();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = false;
        ModManifest.TestConnectHost = Host;
        ServerProfiles.ResetForTests();
        ServerProfiles.TestDirectory = _dir;
        _key = ModManifestCrypto.Generate();
        _op = _key.ToOperator("Ada");
    }

    public void Dispose()
    {
        _key.Dispose();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestConnectHost = null;
        ServerProfiles.TestDirectory = null;
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private string TrustPath => Path.Combine(_dir, ModManifest.TrustedFileName);

    private static string At(int hour) => ModManifestJson.FormatIssued(new DateTime(2026, 10, 5, hour, 0, 0, DateTimeKind.Utc));

    private TrustedOperator Pin(string host, int seenHour, int issuedHour, string riskAck = "", string? spki = null) => new()
    {
        Host = host,
        Spki = spki ?? _op.Spki,
        Fp = _op.Fp,
        Always = true,
        Seen = At(seenHour),
        Issued = At(issuedHour),
        RiskAck = riskAck,
    };

    private ManifestEnvelope SignedAt(int hour)
    {
        var body = ModManifestJson.BuildBody(
            new[] { new LoadedModInfo("progenitor.sample", "0.1.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact, true) },
            new ManifestOverrides { ServerName = "Night Garden", OperatorName = "Ada" },
            _op,
            new DateTime(2026, 10, 5, hour, 0, 0, DateTimeKind.Utc));
        string sig = _key.Sign(ModManifestCrypto.Utf8(ModManifestJson.CanonicalBody(body)));
        return new ManifestEnvelope(body, sig);
    }

    private void Accept(ManifestEnvelope envelope) =>
        ModManifest.AcceptEnvelope(ModManifestCrypto.Utf8(ModManifestJson.CanonicalEnvelope(envelope)));

    private void WriteProfiles(params ServerProfile[] profiles) =>
        File.WriteAllText(Path.Combine(_dir, ServerProfiles.FileName), ServerProfiles.Serialize(profiles));

    private static List<ServerProfile> ReadProfiles(string dir)
    {
        Assert.True(ServerProfiles.TryParse(File.ReadAllText(Path.Combine(dir, ServerProfiles.FileName)), out var list, out var error), error);
        return list;
    }

    // ------------------------------------------------------------------ the key

    [Fact]
    public void The_address_is_the_key_and_the_relay_string_only_the_last_resort()
    {
        // The game's address for this connection first: Bootstrap's dialed record is never cleared and can be stale.
        Assert.Equal("10.0.0.5:9876", ConnectHostKey.Choose("127.0.0.1", 9976, "10.0.0.5", 9876, "SteamIPv4://" + RelayA));
        Assert.Equal(Host, ConnectHostKey.Choose("127.0.0.1", 9976, "", 0, "SteamIPv4://" + RelayA));
        Assert.Equal("play.example.org:9876", ConnectHostKey.Choose("Play.Example.org", 9876, "", 0, ""));
        // The game's address is skipped when it is a relay string, or without a port.
        Assert.Equal(Host, ConnectHostKey.Choose("127.0.0.1", 9976, RelayA, 9876, "SteamIPv4://" + RelayB));
        Assert.Equal(RelayB, ConnectHostKey.Choose("", 0, RelayA, 9876, "SteamIPv4://" + RelayB));
        Assert.Equal("1.2.3.4:9876", ConnectHostKey.Choose("", 0, "5.6.7.8", 0, "Lidgren://1.2.3.4:9876"));
        Assert.Equal("", ConnectHostKey.Choose(null, 0, null, 0, null));
    }

    [Fact]
    public void Relay_strings_are_told_from_addresses()
    {
        foreach (string relay in new[] { RelayA, RelayB, RelayC, RelayA.ToLowerInvariant() })
        {
            Assert.True(ConnectHostKey.IsRelayString(relay), relay);
            Assert.False(ConnectHostKey.IsStable(relay), relay);
        }

        // An EOS product user id (32 hex) is the whole key of an EosP2P join, not a relay string.
        foreach (string address in new[] { Host, "play.example.org:9876", "lanbox", "fe80::1", "0002a1b2c3d4e5f60718293a4b5c6d7e" })
        {
            Assert.False(ConnectHostKey.IsRelayString(address), address);
            Assert.True(ConnectHostKey.IsStable(address), address);
        }

        Assert.False(ConnectHostKey.IsStable(""));
        Assert.False(ConnectHostKey.IsStable(ServerProfiles.UnknownHostKey));
    }

    // ------------------------------------------------------------------ the trust file

    [Fact]
    public void Relay_pins_for_the_key_become_one_pin_for_the_address()
    {
        var other = Pin("10.0.0.9:9876", 1, 1, spki: "other-spki");
        var otherRelay = Pin(RelayC, 1, 1, spki: "other-spki");
        var file = new TrustedOperatorsFile
        {
            Operators = new[] { Pin(RelayA, 3, 5), Pin(RelayB, 2, 9, riskAck: At(2)), other, otherRelay },
            ThunderstoreAcks = new[]
            {
                new ThunderstoreAck { Host = RelayA, Spki = _op.Spki, Fp = _op.Fp, At = At(4), Packages = new[] { "Owner-A@1.0.0" } },
                new ThunderstoreAck { Host = RelayB, Spki = _op.Spki, Fp = _op.Fp, At = At(6), Packages = new[] { "Owner-B@1.0.0", "Owner-A@1.0.0" } },
            },
        };

        var moved = ModManifestTrust.AdoptRelayRows(file, Host, _op.Spki, _op.Fp);

        var pin = Assert.Single(moved.Operators, r => r.Spki == _op.Spki);
        Assert.Equal(Host, pin.Host);
        Assert.True(pin.Always);
        Assert.Equal(At(2), pin.Seen);
        Assert.Equal("", pin.Issued);
        Assert.Equal(At(2), pin.RiskAck);
        Assert.Contains(other, moved.Operators);
        Assert.Contains(otherRelay, moved.Operators);
        var ack = Assert.Single(moved.ThunderstoreAcks);
        Assert.Equal(Host, ack.Host);
        Assert.Equal(At(6), ack.At);
        Assert.Equal(new[] { "Owner-A@1.0.0", "Owner-B@1.0.0" }, ack.Packages);
        Assert.Equal(_op.Fp, ModManifestTrust.FindPin(moved, Host, _op.Spki, _op.Fp)?.Fp);
    }

    [Fact]
    public void An_address_already_pinned_keeps_its_pin_and_the_relay_rows_go()
    {
        var own = Pin(Host, 7, 7);
        var file = new TrustedOperatorsFile { Operators = new[] { own, Pin(RelayA, 1, 9) } };

        var moved = ModManifestTrust.AdoptRelayRows(file, Host, _op.Spki, _op.Fp);

        Assert.Same(own, Assert.Single(moved.Operators));
    }

    [Fact]
    public void Nothing_moves_without_relay_rows_or_to_a_relay_string()
    {
        var file = new TrustedOperatorsFile { Operators = new[] { Pin("10.0.0.9:9876", 1, 1) } };
        Assert.Same(file, ModManifestTrust.AdoptRelayRows(file, Host, _op.Spki, _op.Fp));

        var relay = new TrustedOperatorsFile { Operators = new[] { Pin(RelayA, 1, 1) } };
        Assert.Same(relay, ModManifestTrust.AdoptRelayRows(relay, RelayB, _op.Spki, _op.Fp));
        Assert.Same(relay, ModManifestTrust.AdoptRelayRows(relay, Host, "another-spki", _op.Fp));
    }

    [Fact]
    public void A_verified_catalog_moves_the_relay_pins_on_disk_and_is_trusted()
    {
        ModManifestTrust.WriteAtomic(TrustPath, new TrustedOperatorsFile { Operators = new[] { Pin(RelayA, 1, 2), Pin(RelayB, 3, 4) } });

        Accept(SignedAt(6));

        Assert.True(ModManifest.Pending?.OperatorTrusted);
        var pin = Assert.Single(ModManifestTrust.ReadFile(TrustPath).Operators);
        Assert.Equal(Host, pin.Host);
        Assert.Equal(At(1), pin.Seen);
        Assert.Equal(At(6), pin.Issued);
    }

    [Fact]
    public void A_newer_stamp_from_another_server_with_the_key_is_not_a_replay_here()
    {
        // One operator key signs two servers; the relay row of the one signed later must not reject this one's catalog.
        ModManifestTrust.WriteAtomic(TrustPath, new TrustedOperatorsFile { Operators = new[] { Pin(RelayA, 1, 9), Pin(RelayB, 2, 4) } });

        Accept(SignedAt(6));

        Assert.NotNull(ModManifest.LastEnvelope);
        Assert.True(ModManifest.Pending?.OperatorTrusted);
        Assert.Equal(At(6), Assert.Single(ModManifestTrust.ReadFile(TrustPath).Operators).Issued);
    }

    [Fact]
    public void A_replayed_catalog_moves_nothing()
    {
        ModManifestTrust.WriteAtomic(TrustPath, new TrustedOperatorsFile { Operators = new[] { Pin(Host, 1, 9), Pin(RelayA, 2, 3) } });

        Accept(SignedAt(6));

        Assert.Null(ModManifest.LastEnvelope);
        var rows = ModManifestTrust.ReadFile(TrustPath).Operators;
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Host == RelayA);
    }

    [Fact]
    public void A_catalog_signed_by_another_key_moves_nothing()
    {
        ModManifestTrust.WriteAtomic(TrustPath, new TrustedOperatorsFile { Operators = new[] { Pin(RelayA, 1, 2, spki: "other-spki") } });

        Accept(SignedAt(6));

        var rows = ModManifestTrust.ReadFile(TrustPath).Operators;
        Assert.Contains(rows, r => r.Host == RelayA);
        Assert.Contains(rows, r => r.Host == Host && r.Spki == _op.Spki);
    }

    // ------------------------------------------------------------------ the server profiles

    [Fact]
    public void A_verified_catalog_moves_the_relay_profiles_so_the_next_join_knows_the_policy()
    {
        WriteProfiles(
            new ServerProfile { Host = RelayA.ToLowerInvariant(), OperatorFingerprint = _op.Fp, LastSeenUtc = At(1), AutoPause = false, ClientPolicy = Policy, ClientPolicySeenUtc = At(1) },
            new ServerProfile { Host = RelayB.ToLowerInvariant(), OperatorFingerprint = _op.Fp, LastSeenUtc = At(3), ServerName = "Night Garden", LastPaused = new List<string> { "extra.hot" } },
            new ServerProfile { Host = RelayC.ToLowerInvariant(), LastSeenUtc = At(2) },
            new ServerProfile { Host = "aqf27thwtqgiaaaaaaaaavjneaa", OperatorFingerprint = "ffffffffffffffff", LastSeenUtc = At(2) });
        Assert.Equal("", ServerProfiles.PolicyOf(Host));

        Accept(SignedAt(6));

        Assert.Equal(Policy, ServerProfiles.PolicyOf(Host));
        var profile = ServerProfiles.Get(Host);
        Assert.Equal("Night Garden", profile.ServerName);
        Assert.False(profile.AutoPause); // the older profile's opt-out is kept
        Assert.Equal(new[] { "extra.hot" }, profile.LastPaused);
        var hosts = ReadProfiles(_dir).Select(p => p.Host).OrderBy(h => h, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { Host, "aqf27thwtqgiaaaaaaaaavjneaa" }, hosts);
    }

    [Fact]
    public void Relay_profiles_fill_what_the_address_profile_lacks()
    {
        WriteProfiles(
            new ServerProfile { Host = Host, OperatorFingerprint = _op.Fp, AutoPause = false, LastSeenUtc = At(8), ServerName = "Here" },
            new ServerProfile { Host = RelayA.ToLowerInvariant(), OperatorFingerprint = _op.Fp, ServerName = "Old", ClientPolicy = Policy, ClientPolicySeenUtc = At(7) });

        Assert.True(ServerProfiles.AdoptRelayProfiles(Host, _op.Fp));

        var only = Assert.Single(ReadProfiles(_dir));
        Assert.Equal(Host, only.Host);
        Assert.False(only.AutoPause);
        Assert.Equal("Here", only.ServerName);
        Assert.Equal(Policy, only.ClientPolicy);
        Assert.False(ServerProfiles.AdoptRelayProfiles(Host, _op.Fp));
    }

    [Fact]
    public void An_opt_out_survives_a_profile_the_first_pause_created_before_the_catalog()
    {
        // The first join after the update: the per-server pause writes the address's profile before any catalog arrives.
        WriteProfiles(
            new ServerProfile { Host = RelayA.ToLowerInvariant(), OperatorFingerprint = _op.Fp, AutoPause = false, LastSeenUtc = At(1) },
            new ServerProfile { Host = RelayB.ToLowerInvariant(), OperatorFingerprint = _op.Fp, LastSeenUtc = At(3), ServerName = "Night Garden" });
        ServerProfiles.NotePaused(Host, new[] { "extra.hot" }, new DateTime(2026, 10, 5, 5, 0, 0, DateTimeKind.Utc));
        Assert.True(ServerProfiles.AutoPause(Host));

        Assert.True(ServerProfiles.AdoptRelayProfiles(Host, _op.Fp));

        var profile = ServerProfiles.Get(Host);
        Assert.False(profile.AutoPause);
        Assert.Equal("Night Garden", profile.ServerName);
        Assert.Equal(new[] { "extra.hot" }, profile.LastPaused);
        Assert.Single(ReadProfiles(_dir));
    }

    [Fact]
    public void Profiles_move_only_to_an_address_and_only_with_a_fingerprint()
    {
        WriteProfiles(new ServerProfile { Host = RelayA.ToLowerInvariant(), OperatorFingerprint = _op.Fp, ClientPolicy = Policy, ClientPolicySeenUtc = At(1) });

        Assert.False(ServerProfiles.AdoptRelayProfiles(RelayB, _op.Fp));
        Assert.False(ServerProfiles.AdoptRelayProfiles(Host, ""));
        Assert.False(ServerProfiles.AdoptRelayProfiles(Host, "ffffffffffffffff"));
        Assert.Equal(Policy, ServerProfiles.PolicyOf(RelayA));
    }
}
