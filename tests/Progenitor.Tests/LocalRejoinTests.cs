using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// LOCALREJOIN1 (live 2026-10-07: Join now reconnected to the server list's public address of a server on this PC, through the
/// router's NAT loopback, and timed out): this PC's own server is reconnected through 127.0.0.1 on the same port, with the
/// original address as the fallback. Game-free; addresses are documentation placeholders. Not live-verified.
/// </summary>
public sealed class LocalRejoinTests
{
    // A documentation address (RFC 5737), standing in for the server list's public address.
    private const string Public = "203.0.113.7";

    private static JoinTarget Target(string address = Public, int port = 9976, string name = "Forms Test") =>
        new() { Address = address, Port = port, Protocol = JoinTarget.ProtocolSteamIPv4, ServerName = name, Fingerprint = "ab12cd34" };

    private static JoinLocalFacts Facts(bool thisMachine = false, bool loopback = false, bool valid = true, int port = 9976,
        bool running = true, string name = "Forms Test", string key = "ab12cd34") => new()
    {
        FolderOperatorFingerprint = key,
        AddressIsThisMachine = thisMachine,
        AddressIsLoopback = loopback,
        FolderValid = valid,
        FolderPort = port,
        FolderServerRunning = running,
        FolderServerName = name,
    };

    [Fact]
    public void This_pc_s_own_server_is_reconnected_through_loopback_with_the_original_as_fallback()
    {
        var (primary, fallback) = JoinLocalRoute.Decide(Target(), Facts(), out string log);
        Assert.Equal("127.0.0.1", primary.Address);
        Assert.Equal(9976, primary.Port);
        Assert.Equal(JoinTarget.ProtocolSteamIPv4, primary.Protocol);
        Assert.Equal("Forms Test", primary.ServerName);
        Assert.Equal(Public, fallback!.Address);
        Assert.StartsWith("JoinFlow: reconnect target 127.0.0.1:9976 (local server:", log);
        Assert.Contains("was " + Public + ":9976", log);

        // An address of this machine (a LAN address) is local by the Install tab's own rule, running or not.
        Assert.True(JoinLocalRoute.IsLocalServer(Target("192.0.2.10"), Facts(thisMachine: true, running: false, name: ""), out _));
    }

    [Theory]
    [InlineData("loopback")]
    [InlineData("no-folder")]
    [InlineData("other-port")]
    [InlineData("not-running")]
    [InlineData("other-name")]
    [InlineData("no-facts")]
    public void Anything_less_keeps_the_original_address(string kind)
    {
        var facts = kind switch
        {
            "loopback" => Facts(loopback: true),
            "no-folder" => Facts(valid: false),
            "other-port" => Facts(port: 9877),
            "not-running" => Facts(running: false),
            "other-name" => Facts(name: "Another Server"),
            _ => null,
        };
        var (primary, fallback) = JoinLocalRoute.Decide(Target(), facts, out string log);
        Assert.Equal(Public, primary.Address);
        Assert.Null(fallback);
        Assert.StartsWith("JoinFlow: reconnect target " + Public + ":9976 (", log);
    }

    private sealed class Host : IJoinConnectHost
    {
        public bool World;
        public bool Menu = true;
        public bool Joined;
        public bool Failing;
        public bool Kicked;
        public string Reason = "Timeout";
        public readonly List<string> Calls = new();

        public bool WorldExists => World;
        public bool MainMenuReady => Menu && !World;
        public bool JoinedOk => Joined && World;
        public void RequestLeave() { Calls.Add("leave"); World = false; }
        public void RequestMainMenu() => Calls.Add("menu");

        public bool Launch(JoinTarget target, out string error)
        {
            error = "";
            Calls.Add("launch:" + target.Endpoint);
            World = true;
            return true;
        }

        public bool TryConnectFailure(out string reasonName, out bool kicked)
        {
            reasonName = Failing ? Reason : "";
            kicked = Kicked;
            return Failing;
        }
    }

    /// <summary>The loopback connect timing out falls back to the original address once, and that one can still join.</summary>
    [Fact]
    public void A_failed_loopback_connect_falls_back_to_the_original_address()
    {
        var (primary, fallback) = JoinLocalRoute.Decide(Target(), Facts(), out _);
        var host = new Host();
        var r = new JoinReconnect(primary, host, 0, fallback);
        r.Tick(0.1);
        Assert.Equal("launch:127.0.0.1:9976", host.Calls[0]);
        r.Tick(0.2); // under way
        host.Failing = true;
        r.Tick(5);
        Assert.True(r.UsedFallback);
        Assert.Equal(Public, r.Target.Address);
        Assert.Equal("leaving", r.Phase);
        r.Tick(5.1); // leaves the failed connect
        host.Failing = false;
        r.Tick(5.2); // the menu is back: connect again
        Assert.Equal("launch:" + Public + ":9976", host.Calls[host.Calls.Count - 1]);
        r.Tick(5.3);
        host.Joined = true;
        r.Tick(6);
        Assert.Equal("joined", r.Phase);
    }

    /// <summary>A kick is the server's answer: no other address is tried. Without a fallback a failure ends the reconnect.</summary>
    [Fact]
    public void A_kick_or_a_failure_without_fallback_ends_the_reconnect()
    {
        var (primary, fallback) = JoinLocalRoute.Decide(Target(), Facts(), out _);
        var host = new Host();
        var r = new JoinReconnect(primary, host, 0, fallback);
        r.Tick(0.1);
        r.Tick(0.2);
        host.Failing = true;
        host.Kicked = true;
        r.Tick(1);
        r.Tick(1 + JoinReconnect.KickGraceSeconds + 0.1);
        Assert.Equal("failed", r.Phase);
        Assert.False(r.UsedFallback);

        var plain = new Host();
        var only = new JoinReconnect(Target(), plain, 0);
        only.Tick(0.1);
        only.Tick(0.2);
        plain.Failing = true;
        only.Tick(1);
        Assert.Equal("failed", only.Phase);
    }

    // ---- review fixes ---------------------------------------------------------------------------

    /// <summary>A known operator key decides: a different key is another server; a public address needs the key.</summary>
    [Fact]
    public void The_operator_key_decides_for_a_public_address()
    {
        Assert.False(JoinLocalRoute.IsLocalServer(Target(), Facts(key: "ffff0000"), out string why));
        Assert.Contains("operator key", why);
        var unknown = Target();
        unknown.Fingerprint = "";
        Assert.False(JoinLocalRoute.IsLocalServer(unknown, Facts(), out why));
        Assert.Contains("operator key is unknown", why);
        Assert.False(JoinLocalRoute.IsLocalServer(Target(), Facts(key: ""), out _));
        // An address of this machine still needs no key, but a known different one still refuses.
        Assert.True(JoinLocalRoute.IsLocalServer(unknown, Facts(thisMachine: true, key: ""), out _));
        Assert.False(JoinLocalRoute.IsLocalServer(Target(), Facts(thisMachine: true, key: "ffff0000"), out _));
    }

    [Theory]
    [InlineData("Timeout", true)]
    [InlineData("FailedToEstablishConnection", true)]
    [InlineData("Unknown", true)]
    [InlineData("ClientError", true)]
    [InlineData("", true)]
    [InlineData("Banned", false)]
    [InlineData("ServerFull", false)]
    [InlineData("NoFreeSlots", false)]
    [InlineData("IncorrectVersion", false)]
    [InlineData("ServerStartingUp", false)]
    [InlineData("IncorrectPassword", false)]
    [InlineData("BlockedUserOnServer", false)]
    public void Only_a_connect_that_reached_no_server_falls_back(string reason, bool fallsBack)
    {
        Assert.Equal(fallsBack, JoinReconnect.NoAnswer(reason));
        var (primary, fallback) = JoinLocalRoute.Decide(Target(), Facts(), out _);
        var host = new Host { Reason = reason };
        var r = new JoinReconnect(primary, host, 0, fallback);
        r.Tick(0.1);
        r.Tick(0.2);
        host.Failing = true;
        r.Tick(1);
        Assert.Equal(fallsBack, r.UsedFallback);
        if (!fallsBack) Assert.Equal("failed", r.Phase);
    }

    /// <summary>The loopback join keeps the original endpoint as the record key, so profiles, pins and acks carry over.</summary>
    [Fact]
    public void A_loopback_rejoin_keeps_the_original_record_key()
    {
        try
        {
            ConnectHostAlias.Set("127.0.0.1:9976", Public + ":9976");
            Assert.Equal(Public + ":9976", ConnectHostAlias.Resolve("127.0.0.1:9976"));
            Assert.Equal("127.0.0.1:9877", ConnectHostAlias.Resolve("127.0.0.1:9877"));
            ConnectHostAlias.Clear();
            Assert.Equal("127.0.0.1:9976", ConnectHostAlias.Resolve("127.0.0.1:9976"));
        }
        finally { ConnectHostAlias.Clear(); }
    }

    [Fact]
    public void The_folder_s_operator_key_is_read_from_its_public_key_file()
    {
        string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "progenitor-opkey-" + Guid.NewGuid().ToString("N"));
        try
        {
            string config = System.IO.Path.Combine(dir, "BepInEx", "config");
            System.IO.Directory.CreateDirectory(config);
            Assert.Equal("", JoinLocalRoute.ReadOperatorFingerprint(dir));
            System.IO.File.WriteAllText(System.IO.Path.Combine(config, ModManifest.PublicKeyFileName),
                "# progenitor-operator.pub — ECDSA P-256 SPKI (base64). fp=ab12cd34\nAAAA\n");
            Assert.Equal("ab12cd34", JoinLocalRoute.ReadOperatorFingerprint(dir));
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>Review 2026-10-07 (LOW): the loopback alias never outlives its reconnect.</summary>
    [Fact]
    public void The_alias_lives_for_its_reconnects_join_and_connection_only()
    {
        try
        {
            ConnectHostAlias.Set("127.0.0.1:9976", Public + ":9976");
            // The reconnect leaves the old world first: the armed alias stays.
            ConnectHostAlias.OnJoinBoundary(newJoin: false);
            Assert.Equal(Public + ":9976", ConnectHostAlias.Resolve("127.0.0.1:9976"));
            // Its own join takes it, and it serves that connection.
            ConnectHostAlias.OnJoinBoundary(newJoin: true);
            Assert.Equal(Public + ":9976", ConnectHostAlias.Resolve("127.0.0.1:9976"));
            // That connection ends: gone.
            ConnectHostAlias.OnJoinBoundary(newJoin: false);
            Assert.Equal("127.0.0.1:9976", ConnectHostAlias.Resolve("127.0.0.1:9976"));

            // Another join after its own: gone as well.
            ConnectHostAlias.Set("127.0.0.1:9976", Public + ":9976");
            ConnectHostAlias.OnJoinBoundary(newJoin: true);
            ConnectHostAlias.OnJoinBoundary(newJoin: true);
            Assert.Equal("", ConnectHostAlias.Describe());
        }
        finally { ConnectHostAlias.Clear(); }
    }
}
