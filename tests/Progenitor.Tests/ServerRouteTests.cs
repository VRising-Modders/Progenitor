using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>REMOTEINST1: the one Server target's route, the client channel state and the config migration.</summary>
public sealed class ServerRouteAndClientTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-route-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    private static RemoteInstallStatus Status(bool enabled = true, bool admin = true, bool allowed = true,
        RemoteReachability reach = RemoteReachability.Reachable, string detail = "") =>
        new(0, enabled, admin, enabled && admin && allowed, reach, detail, enabled && admin && allowed, null);

    // local, connected, connectedIsLocal, handshake, status kind, expected route, expected block
    public static IEnumerable<object[]> Matrix() => new[]
    {
        // Not connected: the local folder when there is one, else nothing.
        new object[] { true, false, false, false, "none", (int)ServerRouteKind.Local, (int)ServerRouteBlock.None },
        new object[] { false, false, false, false, "none", (int)ServerRouteKind.None, (int)ServerRouteBlock.NoServer },
        // Connected to the local server: the folder route, admin or not, capability or not.
        new object[] { true, true, true, true, "admin", (int)ServerRouteKind.Local, (int)ServerRouteBlock.None },
        new object[] { true, true, true, true, "player", (int)ServerRouteKind.Local, (int)ServerRouteBlock.None },
        new object[] { true, true, true, true, "none", (int)ServerRouteKind.Local, (int)ServerRouteBlock.None },
        // Connected elsewhere, with or without a local folder: the remote route decides.
        new object[] { true, true, false, true, "admin", (int)ServerRouteKind.Remote, (int)ServerRouteBlock.None },
        new object[] { false, true, false, true, "admin", (int)ServerRouteKind.Remote, (int)ServerRouteBlock.None },
        new object[] { false, true, false, true, "player", (int)ServerRouteKind.None, (int)ServerRouteBlock.NotAdmin },
        new object[] { true, true, false, true, "player", (int)ServerRouteKind.None, (int)ServerRouteBlock.NotAdmin },
        new object[] { false, true, false, true, "off", (int)ServerRouteKind.None, (int)ServerRouteBlock.RemoteOff },
        new object[] { false, true, false, true, "unlisted", (int)ServerRouteKind.None, (int)ServerRouteBlock.NotAllowed },
        new object[] { false, true, false, true, "unreachable", (int)ServerRouteKind.Remote, (int)ServerRouteBlock.Unreachable },
        new object[] { false, true, false, true, "none", (int)ServerRouteKind.None, (int)ServerRouteBlock.Pending },
        // Connected to a server without Progenitor.
        new object[] { false, true, false, false, "none", (int)ServerRouteKind.None, (int)ServerRouteBlock.NotProgenitor },
        new object[] { true, true, false, false, "none", (int)ServerRouteKind.None, (int)ServerRouteBlock.NotProgenitor },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void The_route_matrix(bool local, bool connected, bool connectedIsLocal, bool handshake, string status,
        int kindValue, int blockValue)
    {
        var kind = (ServerRouteKind)kindValue;
        var block = (ServerRouteBlock)blockValue;
        RemoteInstallStatus? s = status switch
        {
            "admin" => Status(),
            "player" => Status(admin: false),
            "off" => Status(enabled: false),
            "unlisted" => Status(allowed: false),
            "unreachable" => Status(reach: RemoteReachability.Unreachable, detail: "dns"),
            _ => null,
        };
        var route = ServerRouteSelector.Select(new ServerRouteInputs(local, connected, connectedIsLocal, handshake, s));
        Assert.Equal(kind, route.Kind);
        Assert.Equal(block, route.Block);
        Assert.Equal(kind == ServerRouteKind.Local || (kind == ServerRouteKind.Remote && block == ServerRouteBlock.None), route.CanInstall);
    }

    [Fact]
    public void Unreachable_reads_in_plain_words_and_still_manages()
    {
        var route = ServerRouteSelector.Select(new ServerRouteInputs(false, true, false, true,
            Status(reach: RemoteReachability.Unreachable, detail: "timeout")));
        Assert.False(route.CanInstall);
        Assert.True(route.CanManage);
        Assert.Equal("Remote installs unavailable: the server can't reach Thunderstore (timed out).", route.Reason);
        var resolved = DefaultInstallTarget.Resolve(DefaultInstallTargetSetting.FollowSide, PackageSide.Server, route);
        Assert.True(resolved.FellBackToClient);
        Assert.False(resolved.ToConnectedServer);
    }

    [Fact]
    public void Target_resolution_per_route()
    {
        var remote = new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None);
        var server = DefaultInstallTarget.Resolve(DefaultInstallTargetSetting.FollowSide, PackageSide.Server, remote);
        Assert.True(server.ToConnectedServer);
        Assert.False(server.LocalInstall);
        var both = DefaultInstallTarget.Resolve(DefaultInstallTargetSetting.FollowSide, PackageSide.Both, remote);
        Assert.True(both.ToConnectedServer);
        Assert.True(both.LocalInstall);
        Assert.Equal(InstallTarget.Client, both.Target);
        var client = DefaultInstallTarget.Resolve(DefaultInstallTargetSetting.Client, PackageSide.Server, remote);
        Assert.False(client.ToConnectedServer);
        Assert.Equal(InstallTarget.Client, client.Target);

        var local = new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None);
        Assert.Equal(InstallTarget.Both, DefaultInstallTarget.Resolve(DefaultInstallTargetSetting.Both, PackageSide.Client, local).Target);
        Assert.Equal(InstallTarget.LocalServer, DefaultInstallTarget.Resolve(DefaultInstallTargetSetting.Server, PackageSide.Client, local).Target);
        Assert.False(DefaultInstallTarget.Resolve(DefaultInstallTargetSetting.Server, PackageSide.Client, local).ToConnectedServer);
    }

    [Theory]
    [InlineData("localServer", true, "server")]
    [InlineData("LocalServer", true, "server")]
    [InlineData("Local server", true, "server")]
    [InlineData("server", false, "server")]
    [InlineData("both", false, "both")]
    [InlineData("followSide", false, "followSide")]
    [InlineData("nonsense", false, "nonsense")]
    [InlineData("", false, "")]
    public void The_config_migration_rewrites_only_the_legacy_value(string stored, bool rewrites, string after)
    {
        Assert.Equal(rewrites, DefaultInstallTarget.TryMigrate(stored, out string migrated));
        Assert.Equal(after, migrated);
        if (rewrites) Assert.Equal(DefaultInstallTargetSetting.Server, DefaultInstallTarget.Parse(stored));
    }

    // ---- the client channel ----------------------------------------------------------------

    private static (RemoteInstallClient Client, List<RemoteInstallRequest> Sent) Client()
    {
        var sent = new List<RemoteInstallRequest>();
        var client = new RemoteInstallClient
        {
            Send = bytes =>
            {
                Assert.True(RemoteInstallWire.TryDecodeRequest(bytes, out var r));
                sent.Add(r);
                return true;
            },
        };
        return (client, sent);
    }

    [Fact]
    public void Nothing_is_sent_to_a_server_that_never_spoke_on_the_channel()
    {
        var (client, sent) = Client();
        Assert.False(client.RequestStatus());
        Assert.Equal(0, client.Begin(RemoteInstallOp.Install, "team-Sample", "1.0.0", false, out var refusal));
        Assert.NotNull(refusal);
        Assert.Empty(sent);

        client.Accept(Status());
        Assert.True(client.RequestStatus());
        Assert.Equal(RemoteInstallOp.List, Assert.Single(sent).Op);
    }

    [Fact]
    public void A_handshake_without_a_status_push_reads_as_remote_off_after_the_grace()
    {
        var (client, _) = Client();
        double now = 0;
        client.Now = () => now;
        Assert.Null(client.Inputs(false, true, false, true).RemoteStatus);
        now = RemoteInstallClient.StatusGraceSeconds + 1;
        var inputs = client.Inputs(false, true, false, true);
        Assert.Equal(ServerRouteBlock.RemoteOff, ServerRouteSelector.Select(inputs).Block);
    }

    [Fact]
    public void Only_replies_to_this_clients_own_requests_are_believed_and_reset_forgets_the_server()
    {
        var (client, sent) = Client();
        client.Accept(Status());
        int nonce = client.Begin(RemoteInstallOp.Install, "team-Sample", "1.0.0", false, out _);
        Assert.NotEqual(0, nonce);
        Assert.Equal(RemoteInstallStage.Accepted, client.InFlightFor("team-Sample")!.Stage);
        // A second operation on the same package waits for the first.
        Assert.Equal(0, client.Begin(RemoteInstallOp.Uninstall, "team-Sample", "", false, out _));

        client.Accept(new RemoteInstallProgress(nonce + 100, RemoteInstallOp.Install, RemoteInstallStage.Failed, RemoteInstallReason.Busy, "team-Sample", "1.0.0", ""));
        Assert.Equal(RemoteInstallStage.Accepted, client.LatestFor("team-Sample")!.Stage);

        client.Accept(new RemoteInstallProgress(nonce, RemoteInstallOp.Install, RemoteInstallStage.Downloading, RemoteInstallReason.None, "team-Sample", "1.0.0", ""));
        Assert.Equal(RemoteInstallStage.Downloading, client.InFlightFor("team-Sample")!.Stage);
        Assert.True(client.Cancel("team-Sample"));
        Assert.Equal(RemoteInstallOp.Cancel, sent.Last().Op);

        client.Accept(new RemoteInstallProgress(nonce, RemoteInstallOp.Install, RemoteInstallStage.PendingRestart, RemoteInstallReason.None, "team-Sample", "1.0.0", ""));
        Assert.Null(client.InFlightFor("team-Sample"));
        // A finished step asks for the rows again.
        Assert.Equal(RemoteInstallOp.List, sent.Last().Op);

        client.Reset();
        Assert.Null(client.Status);
        // The finished result stays on the row until the next operation on this package.
        Assert.Equal(RemoteInstallStage.PendingRestart, client.LatestFor("team-Sample")!.Stage);
        Assert.False(client.ServerSupportsRemote);
    }

    [Fact]
    public void A_step_the_server_never_answers_goes_stale()
    {
        var (client, _) = Client();
        double now = 0;
        client.Now = () => now;
        client.Accept(Status());
        client.Begin(RemoteInstallOp.Install, "team-Sample", "1.0.0", false, out _);
        Assert.NotNull(client.InFlightFor("team-Sample"));
        now = RemoteInstallClient.StaleSeconds + 1;
        Assert.Null(client.InFlightFor("team-Sample"));
        var latest = client.LatestFor("team-Sample")!;
        Assert.Equal(RemoteInstallStage.Failed, latest.Stage);
        Assert.Equal(RemoteInstallReason.Unavailable, latest.Reason);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("localhost", true)]
    [InlineData("::1", true)]
    [InlineData("[::1]", true)]
    [InlineData("192.168.1.50", true)]
    [InlineData("203.0.113.9", false)]
    [InlineData("example.com", false)]
    [InlineData("", false)]
    public void Only_an_address_on_this_machine_can_be_the_local_folders_server(string address, bool local)
    {
        var mine = new[] { System.Net.IPAddress.Parse("192.168.1.50") };
        Assert.Equal(local, LocalAddress.IsThisMachine(address, () => mine));
    }

    /// <summary>
    /// Live 2026-10-01: the client joined 127.0.0.1:9976, the configured local folder's server
    /// (ServerHostSettings.json Port 9976, handshake ok), as a non-admin. The route must be local:
    /// the local folder needs no admin and is decided before anything remote.
    /// </summary>
    [Fact]
    public void The_live_inputs_choose_the_local_route()
    {
        var facts = new ServerRouteFacts
        {
            ConfiguredPath = @"C:\GameModding\_testserver\server",
            PathValid = true,
            CanonicalPath = @"C:\GameModding\_testserver\server",
            FolderPort = 9976,
            Connected = true,
            ConnectedAddress = "127.0.0.1",
            ConnectedPort = 9976,
            PortRecorded = true,
            IsLocalMachine = LocalAddress.IsThisMachine("127.0.0.1"),
            HandshakeOk = true,
            RemoteStatus = Status(admin: false),
        };
        var decision = ServerRouteSelector.Decide(facts);
        Assert.True(facts.IsLocalMachine);
        Assert.True(decision.ConnectedIsLocal);
        Assert.Equal(ServerRouteKind.Local, decision.Route.Kind);
        Assert.StartsWith("local: connected", decision.Why);

        // The same join seen before Bootstrap's connect record or the handshake reads as not local,
        // and names which input was missing.
        var early = ServerRouteSelector.Decide(new ServerRouteFacts
        {
            ConfiguredPath = facts.ConfiguredPath, PathValid = true, FolderPort = 9976, Connected = true,
            ConnectedAddress = "", ConnectedPort = 0, IsLocalMachine = false, HandshakeOk = true, RemoteStatus = Status(admin: false),
        });
        Assert.Equal(ServerRouteBlock.NotAdmin, early.Route.Block);
        Assert.Contains("no joined port recorded", early.Why);

        var otherPort = ServerRouteSelector.Decide(new ServerRouteFacts
        {
            ConfiguredPath = facts.ConfiguredPath, PathValid = true, FolderPort = 9976, Connected = true,
            ConnectedAddress = "127.0.0.1", ConnectedPort = 9977, IsLocalMachine = true, HandshakeOk = true, RemoteStatus = Status(),
        });
        Assert.Equal(ServerRouteKind.Remote, otherPort.Route.Kind);
        Assert.Contains("joined port 9977 is not the folder's port 9976", otherPort.Why);
    }

    [Theory]
    [InlineData("SteamIPv4://127.0.0.1:9976")]
    [InlineData("127.0.0.1:9976")]
    [InlineData("[::1]:9976")]
    public void Connect_descriptions_still_read_as_this_machine(string address) =>
        Assert.True(LocalAddress.IsThisMachine(address, () => Array.Empty<System.Net.IPAddress>()));

    [Fact]
    public void The_bridge_follows_the_tabs_route_unless_remote_is_forced()
    {
        var client = RemoteInstallClient.Shared;
        client.ResetForTests();
        var sent = new List<RemoteInstallRequest>();
        try
        {
            client.Send = bytes => { Assert.True(RemoteInstallWire.TryDecodeRequest(bytes, out var r)); sent.Add(r); return true; };
            client.Accept(Status());
            RemoteInstallBridgeOps.IsServer = () => false;
            RemoteInstallBridgeOps.ClientRoute = () => new ServerRouteDecision(new ServerRoute(ServerRouteKind.Local, ServerRouteBlock.None), true, "local", null);

            var refused = RemoteInstallBridgeOps.Run("install", "team-Sample", "1.0.0", false, "");
            Assert.Equal(false, refused["ok"]);
            Assert.Contains("'local'", (string)refused["error"]!);
            Assert.Empty(sent);

            var forced = RemoteInstallBridgeOps.Run("install", "team-Sample", "1.0.0", false, "remote");
            Assert.Equal(true, forced["ok"]);
            Assert.Equal(RemoteInstallOp.Install, Assert.Single(sent).Op);
        }
        finally
        {
            client.ResetForTests();
            RemoteInstallBridgeOps.IsServer = null;
            RemoteInstallBridgeOps.ClientRoute = null;
        }
    }

    [Fact]
    public void A_refusal_stays_until_the_next_operation_even_across_a_reset()
    {
        var (client, _) = Client();
        client.Accept(Status(admin: false));
        int nonce = client.Begin(RemoteInstallOp.Install, "team-Sample", "1.0.0", false, out _);
        client.Accept(new RemoteInstallProgress(nonce, RemoteInstallOp.Install, RemoteInstallStage.Refused, RemoteInstallReason.NotAdmin, "team-Sample", "1.0.0", "only an in-game admin"));
        client.Reset();
        var latest = client.LatestFor("team-Sample")!;
        Assert.Equal(RemoteInstallStage.Refused, latest.Stage);
        Assert.Equal(RemoteInstallReason.NotAdmin, latest.Reason);
        Assert.Single(client.AllLatest());

        // A step still running when the connection ended reads as lost, not as running.
        client.Accept(Status());
        client.Begin(RemoteInstallOp.Install, "team-Other", "1.0.0", false, out _);
        client.Reset();
        Assert.Equal(RemoteInstallStage.Failed, client.LatestFor("team-Other")!.Stage);
    }

    [Fact]
    public void The_server_reported_steam_id_is_kept_between_sessions()
    {
        string file = Path.Combine(_dir, "progenitor-cache", "steamid.txt");
        var (client, _) = Client();
        client.SteamIdFile = file;
        Assert.Equal(0UL, client.LastKnownSteamId);
        client.Accept(new RemoteInstallStatus(0, true, false, false, RemoteReachability.Unknown, "", false, null, steamId: 76561198000000001UL));
        Assert.Equal(76561198000000001UL, client.LastKnownSteamId);
        Assert.True(File.Exists(file));

        var (later, _) = Client();
        later.SteamIdFile = file;
        Assert.Equal(76561198000000001UL, later.LastKnownSteamId);
    }

    [Fact]
    public void The_bridge_op_reads_the_client_route_and_refuses_unknown_actions()
    {
        try
        {
            RemoteInstallBridgeOps.IsServer = () => false;
            RemoteInstallBridgeOps.ClientRoute = () => new ServerRouteDecision(new ServerRoute(ServerRouteKind.None, ServerRouteBlock.NotAdmin), false, "test", null);
            var status = RemoteInstallBridgeOps.Run("status", "team-Sample", "", false);
            Assert.Equal("client", status["side"]);
            var route = (Dictionary<string, object?>)status["route"]!;
            Assert.Equal("none", route["kind"]);
            Assert.Equal("not-admin", route["block"]);
            Assert.Equal(ServerRouteCopy.NotAdmin, route["reason"]);
            Assert.Equal(false, RemoteInstallBridgeOps.Run("bogus", "", "", false)["ok"]);

            RemoteInstallBridgeOps.IsServer = () => true;
            RemoteInstallBridgeOps.ServerService = () => null;
            Assert.Equal(false, RemoteInstallBridgeOps.Run("status", "", "", false)["ok"]);
        }
        finally
        {
            RemoteInstallBridgeOps.IsServer = null;
            RemoteInstallBridgeOps.ClientRoute = null;
            RemoteInstallBridgeOps.ServerService = null;
        }
    }

    [Fact]
    public void Rows_match_listings_by_the_ledger_spelling_rule()
    {
        var (client, _) = Client();
        client.Accept(new RemoteInstallStatus(0, true, true, true, RemoteReachability.Reachable, "", true,
            new[] { new RemoteServerRow("tovan-BossRoster", "2.1.5", RemoteRowState.Hot, ServerInstallLedger.RouteRemote) }));
        var entry = InstallListViewTests.Listing("tovan-BossRoster", "BossRoster");
        Assert.NotNull(client.RowFor(entry));
        Assert.Null(client.RowFor(InstallListViewTests.Listing("tovan-Other", "Other")));
    }
}
