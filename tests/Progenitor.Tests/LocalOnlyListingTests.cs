using System;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// LOCALROUTE1 / LOCALONLY1 (live 2026-10-07: joined through the router's public address, the configured folder's own server
/// read as a remote one, and a both-sides install of a local test package sent only its server half): the route
/// recognizes the folder's server by the loopback rejoin's rule, and a listing only this PC's local test source carries is
/// never sent to a remote server. Game-free; not live-verified.
/// </summary>
public sealed class LocalOnlyListingTests
{
    private static ServerRouteFacts Facts(bool thisMachine, bool verified, bool treatAsRemote = false) => new()
    {
        ConfiguredPath = @"C:\srv", PathValid = true, CanonicalPath = @"C:\srv", FolderPort = 9976, Connected = true,
        ConnectedAddress = "203.0.113.7", ConnectedPort = 9976, IsLocalMachine = thisMachine, VerifiedThisPcServer = verified,
        HandshakeOk = true, TreatLocalAsRemote = treatAsRemote,
        RemoteStatus = new RemoteInstallStatus(0, true, true, true, RemoteReachability.Reachable, "", true, Array.Empty<RemoteServerRow>()),
    };

    [Fact]
    public void A_public_address_verified_as_the_folder_s_server_takes_the_local_route()
    {
        var plain = ServerRouteSelector.Decide(Facts(thisMachine: false, verified: false));
        Assert.Equal(ServerRouteKind.Remote, plain.Route.Kind);
        Assert.Contains("not verified as the folder's server", plain.Why);

        var verified = ServerRouteSelector.Decide(Facts(thisMachine: false, verified: true));
        Assert.Equal(ServerRouteKind.Local, verified.Route.Kind);
        Assert.True(verified.ConnectedIsLocal);

        // TreatLocalServerAsRemote keeps its meaning: the verified folder server is still forced remote.
        var forced = ServerRouteSelector.Decide(Facts(thisMachine: false, verified: true, treatAsRemote: true));
        Assert.Equal(ServerRouteKind.Remote, forced.Route.Kind);
        Assert.True(forced.Route.ForcedRemote);
    }

    /// <summary>
    /// F23 (live 2026-10-07): while this client hosts a private game, the route is local to that game's own server folder,
    /// whatever the configured dedicated folder and its port say. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void A_hosted_private_game_is_the_local_route_ahead_of_the_configured_folder()
    {
        var facts = new ServerRouteFacts
        {
            ConfiguredPath = @"C:srv", PathValid = true, CanonicalPath = @"C:gameVRising_Server", FolderPort = 9976, Connected = true,
            ConnectedAddress = "127.0.0.1", ConnectedPort = 9876, IsLocalMachine = true, HandshakeOk = true,
            HostedFolder = @"C:gameVRising_Server",
            RemoteStatus = new RemoteInstallStatus(0, true, true, true, RemoteReachability.Reachable, "", true, Array.Empty<RemoteServerRow>()),
        };
        var hosted = ServerRouteSelector.Decide(facts);
        Assert.Equal(ServerRouteKind.Local, hosted.Route.Kind);
        Assert.True(hosted.ConnectedIsLocal);
        Assert.Contains(HostedServerFolder.Name, hosted.Why);

        // Review (M5): a stale hosting flag while connected elsewhere, or without a completed handshake, is not the shortcut;
        // TreatLocalServerAsRemote still holds.
        var elsewhere = ServerRouteSelector.Decide(new ServerRouteFacts
        {
            ConfiguredPath = facts.ConfiguredPath, PathValid = true, CanonicalPath = facts.CanonicalPath, FolderPort = 9976, Connected = true,
            ConnectedAddress = "203.0.113.7", ConnectedPort = 9876, IsLocalMachine = false, HandshakeOk = true, HostedFolder = facts.HostedFolder,
            RemoteStatus = facts.RemoteStatus,
        });
        Assert.DoesNotContain(HostedServerFolder.Name, elsewhere.Why);
        Assert.Equal(ServerRouteKind.Remote, elsewhere.Route.Kind);
        var noHandshake = ServerRouteSelector.Decide(new ServerRouteFacts
        {
            PathValid = true, CanonicalPath = facts.CanonicalPath, Connected = true, ConnectedAddress = "127.0.0.1", ConnectedPort = 9876,
            IsLocalMachine = true, HandshakeOk = false, HostedFolder = facts.HostedFolder, RemoteStatus = facts.RemoteStatus,
        });
        Assert.DoesNotContain(HostedServerFolder.Name, noHandshake.Why);
        var forced = ServerRouteSelector.Decide(new ServerRouteFacts
        {
            PathValid = true, CanonicalPath = facts.CanonicalPath, Connected = true, ConnectedAddress = "127.0.0.1", ConnectedPort = 9876,
            IsLocalMachine = true, HandshakeOk = true, HostedFolder = facts.HostedFolder, RemoteStatus = facts.RemoteStatus, TreatLocalAsRemote = true,
        });
        Assert.DoesNotContain(HostedServerFolder.Name, forced.Why);

        // Not hosting: the configured folder's port decides, as before (the private game's port is not the folder's).
        var plain = ServerRouteSelector.Decide(new ServerRouteFacts
        {
            ConfiguredPath = @"C:srv", PathValid = true, CanonicalPath = @"C:srv", FolderPort = 9976, Connected = true,
            ConnectedAddress = "127.0.0.1", ConnectedPort = 9876, IsLocalMachine = true, HandshakeOk = true, RemoteStatus = facts.RemoteStatus,
        });
        Assert.Equal(ServerRouteKind.Remote, plain.Route.Kind);
    }
    /// <summary>
    /// F34 (live 2026-10-08): while this client hosts a private game, every server-side read (ledger, pending, reload folder)
    /// comes from the private game's own folder, never the configured dedicated server's; a forced-remote route reads none.
    /// F35: the Server column and the picker name it "Your private game", and the removal log says "your private game".
    /// Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void A_hosted_private_game_is_read_from_its_own_folder_and_named_as_such()
    {
        Assert.Equal(@"C:\game\VRising_Server\BepInEx", Progenitor.Ui.ModsMenu.ServerReadRoot(@"C:\game\VRising_Server\BepInEx", @"C:\test\BepInEx", false));
        Assert.Equal(@"C:\test\BepInEx", Progenitor.Ui.ModsMenu.ServerReadRoot(null, @"C:\test\BepInEx", false));
        Assert.Null(Progenitor.Ui.ModsMenu.ServerReadRoot(@"C:\game\VRising_Server\BepInEx", @"C:\test\BepInEx", true));
        Assert.Null(Progenitor.Ui.ModsMenu.ServerReadRoot("", "", false));

        var inputs = new ServerPickerInputs
        {
            Connected = true,
            ConnectedServer = ServerIdentity.Connected("", "127.0.0.1", 9876),
            ConnectedIsThisMachine = true,
            ConnectedPort = 9876,
            HostedFolder = @"C:\game\VRising_Server",
            Discovered = new[] { new LocalServerCandidate(@"C:\game\VRising_Server", "", "steam", port: 9876) },
        };
        Assert.Equal(ServerPicker.PrivateGameName, ServerPicker.ConnectedName(inputs));
        var entries = ServerPicker.Build(inputs);
        Assert.Equal("Server · Your private game", ServerPicker.HeaderCaption(entries[0]));
        Assert.DoesNotContain(entries, e => e.Kind == ServerPickKind.LocalFolder && e.Path == @"C:\game\VRising_Server");

        string svc = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "InstallCatalogService.cs");
        Assert.Contains("where.Replace(\"the local server\", HostedServerFolder.Name)", svc, StringComparison.Ordinal);
    }
    /// <summary>
    /// Review M-e (2026-10-08): the private game's folder is read and written only while the route decision makes it the
    /// local route (the same rule <see cref="ServerRouteSelector.Decide"/> uses); a stale hosting flag on another
    /// connection, or TreatLocalServerAsRemote, keeps it out. Game-free; not live-verified.
    /// </summary>
    [Fact]
    public void The_private_games_folder_is_used_only_when_the_route_decision_says_so()
    {
        ServerRouteFacts Facts(bool connected, bool local, bool handshake, bool forced = false) => new()
        {
            HostedFolder = @"C:\game\VRising_Server", Connected = connected, IsLocalMachine = local, HandshakeOk = handshake, TreatLocalAsRemote = forced,
        };
        Assert.True(ServerRouteSelector.HostedApplies(Facts(connected: false, local: false, handshake: false)));
        Assert.True(ServerRouteSelector.HostedApplies(Facts(connected: true, local: true, handshake: true)));
        Assert.False(ServerRouteSelector.HostedApplies(Facts(connected: true, local: false, handshake: true)));
        Assert.False(ServerRouteSelector.HostedApplies(Facts(connected: true, local: true, handshake: false)));
        Assert.False(ServerRouteSelector.HostedApplies(Facts(connected: false, local: true, handshake: true, forced: true)));
        Assert.False(ServerRouteSelector.HostedApplies(null));

        string menu = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ModsMenu.cs");
        // LOCALDETECT1: the override is the private game's folder, else the auto-detected local server's; both come from
        // the route decision (RouteOverrideBepInExRoot = HostedRouteBepInExRoot ?? DetectedRouteBepInExRoot).
        Assert.Contains("RouteOverrideBepInExRoot() => HostedRouteBepInExRoot() ?? DetectedRouteBepInExRoot();", menu, StringComparison.Ordinal);
        Assert.Contains("service.ServerRootOverride = RouteOverrideBepInExRoot;", menu, StringComparison.Ordinal);
        Assert.Contains("string? hosted = RouteOverrideBepInExRoot();", menu, StringComparison.Ordinal);
        Assert.Contains("ServerReadRoot(RouteOverrideBepInExRoot(), ServerBepInExRootOrNull(), ForcedRemoteForReads)", menu, StringComparison.Ordinal);
        Assert.DoesNotContain("HostedServerFolder.BepInExRoot;", menu, StringComparison.Ordinal);
    }
}
