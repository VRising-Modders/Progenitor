using System.Linq;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>INSTALLSIDES1: the Install tab's server picker — its entries, their order, the default and the route.</summary>
public sealed class ServerPickerTests
{
    private const string Configured = @"C:\GameModding\_testserver\server";
    private const string Steam = @"D:\Steam\steamapps\common\VRisingDedicatedServer";

    private static ServerPickerInputs Inputs(bool connected = false, string configured = Configured, params LocalServerCandidate[] found) =>
        new()
        {
            Connected = connected,
            ConnectedServer = ServerIdentity.Connected("Progenitor Forms Test", "127.0.0.1", 9976),
            ConfiguredPath = configured,
            ConfiguredName = configured.Length > 0 ? "Forms Test" : "",
            Discovered = found,
        };

    [Fact]
    public void Entries_list_the_connected_server_the_local_folders_private_games_and_none_in_order()
    {
        var entries = ServerPicker.Build(Inputs(connected: true, Configured, new LocalServerCandidate(Steam, "", "Steam", running: true)));
        Assert.Equal(
            new[] { ServerPickKind.Connected, ServerPickKind.LocalFolder, ServerPickKind.LocalFolder, ServerPickKind.PrivateGames, ServerPickKind.None },
            entries.Select(e => e.Kind).ToArray());
        Assert.Equal("Connected: Progenitor Forms Test", entries[0].Caption);
        Assert.Equal("Forms Test (this PC)", entries[1].Caption);
        Assert.Equal(ServerPicker.SourceConfigured, entries[1].Detail);
        Assert.Equal("VRisingDedicatedServer (this PC)", entries[2].Caption);
        Assert.Equal("Steam · running", entries[2].Detail);
        Assert.Equal(ServerPicker.PrivateGamesCaption, entries[3].Caption);
        Assert.Equal(ServerPicker.NoneCaption, entries[4].Caption);
        Assert.Equal(ServerPicker.LocalId(Configured), entries[1].Id);
    }

    [Fact]
    public void Not_connected_has_no_connected_entry()
    {
        var entries = ServerPicker.Build(Inputs(connected: false));
        Assert.DoesNotContain(entries, e => e.Kind == ServerPickKind.Connected);
    }

    [Fact]
    public void A_connected_server_with_no_name_is_named_by_its_address()
    {
        var entries = ServerPicker.Build(new ServerPickerInputs { Connected = true, ConnectedServer = ServerIdentity.Connected("", "10.0.0.5", 9876) });
        Assert.Equal("Connected: 10.0.0.5:9876", entries[0].Caption);
        Assert.Equal("Server · 10.0.0.5:9876", ServerPicker.HeaderCaption(entries[0]));
    }

    [Fact]
    public void The_same_folder_found_twice_is_listed_once()
    {
        var entries = ServerPicker.Build(Inputs(false, Configured,
            new LocalServerCandidate(Configured + "\\", "", "Steam"),
            new LocalServerCandidate(Configured.ToUpperInvariant(), "", "running now"),
            new LocalServerCandidate("", "", "nothing")));
        Assert.Single(entries, e => e.Kind == ServerPickKind.LocalFolder);
    }

    [Fact]
    public void The_default_is_connected_then_the_configured_folder_then_private_games()
    {
        Assert.Equal(ServerPickKind.Connected, ServerPicker.Resolve(null, ServerPicker.Build(Inputs(connected: true))).Kind);
        var local = ServerPicker.Resolve(null, ServerPicker.Build(Inputs(connected: false)));
        Assert.Equal(ServerPickKind.LocalFolder, local.Kind);
        Assert.Equal(Configured, local.Path);
        Assert.Equal(ServerPickKind.PrivateGames, ServerPicker.Resolve(null, ServerPicker.Build(Inputs(false, ""))).Kind);
        // A discovered-only folder is never picked for the player.
        var discovered = ServerPicker.Build(Inputs(false, "", new LocalServerCandidate(Steam, "", "Steam")));
        Assert.Equal(ServerPickKind.PrivateGames, ServerPicker.Resolve(null, discovered).Kind);
    }

    [Fact]
    public void A_pick_stands_while_offered_and_falls_back_when_not()
    {
        var connected = ServerPicker.Build(Inputs(connected: true));
        Assert.Equal(ServerPickKind.None, ServerPicker.Resolve(ServerPicker.IdNone, connected).Kind);
        Assert.Equal(ServerPickKind.LocalFolder, ServerPicker.Resolve(ServerPicker.LocalId(Configured.ToLowerInvariant()), connected).Kind);
        // Picked the connected server, then disconnected: the default takes over.
        var after = ServerPicker.Build(Inputs(connected: false));
        Assert.Equal(ServerPickKind.LocalFolder, ServerPicker.Resolve(ServerPicker.IdConnected, after).Kind);
        Assert.Equal(ServerPickKind.PrivateGames, ServerPicker.Resolve("local:Z:\\gone", ServerPicker.Build(Inputs(false, ""))).Kind);
    }

    [Fact]
    public void Each_kind_maps_to_its_target_and_header()
    {
        var entries = ServerPicker.Build(Inputs(connected: true));
        Assert.Equal(ServerTargetKind.Server, entries.Single(e => e.Kind == ServerPickKind.Connected).TargetKind);
        Assert.Equal(ServerTargetKind.Server, entries.Single(e => e.Kind == ServerPickKind.LocalFolder).TargetKind);
        var priv = entries.Single(e => e.Kind == ServerPickKind.PrivateGames);
        Assert.Equal(ServerTargetKind.PrivateGames, priv.TargetKind);
        Assert.Equal("Server · Private games", ServerPicker.HeaderCaption(priv));
        var none = entries.Single(e => e.Kind == ServerPickKind.None);
        Assert.Equal(ServerTargetKind.None, none.TargetKind);
        Assert.Equal("Server", ServerPicker.HeaderCaption(none));
        Assert.Equal("Server · Forms Test", ServerPicker.HeaderCaption(entries.Single(e => e.Kind == ServerPickKind.LocalFolder)));
    }

    [Fact]
    public void The_route_follows_the_pick()
    {
        var remote = new ServerRoute(ServerRouteKind.Remote, ServerRouteBlock.None);
        var entries = ServerPicker.Build(Inputs(connected: true, Configured, new LocalServerCandidate(Steam, "", "Steam")));
        Assert.Equal(ServerRouteKind.Remote, ServerPicker.RouteFor(entries[0], remote, Configured).Kind);
        Assert.Equal(ServerRouteKind.Local, ServerPicker.RouteFor(entries[1], remote, Configured).Kind);
        // A found folder that is not (yet) the configured one has no route until it is applied.
        var steam = ServerPicker.RouteFor(entries[2], remote, Configured);
        Assert.Equal(ServerRouteKind.None, steam.Kind);
        Assert.Equal(ServerRouteKind.None, ServerPicker.RouteFor(entries.Single(e => e.Kind == ServerPickKind.PrivateGames), remote, Configured).Kind);
        Assert.Equal(ServerRouteKind.None, ServerPicker.RouteFor(entries.Single(e => e.Kind == ServerPickKind.None), remote, Configured).Kind);
    }

    [Fact]
    public void Readback_names_the_entry()
    {
        var w = ServerPicker.ToWidget(ServerPicker.Build(Inputs(connected: true))[0]);
        Assert.Equal("connected", w["kind"]);
        Assert.Equal("Progenitor Forms Test", w["name"]);
    }

    // ---- Live fix (2026-10-03): name the connected server, and list it once -------------------

    private const string Headless = @"C:\GameModding\_testserver\headless";

    /// <summary>The live rig: joined 127.0.0.1:9976, the configured Forms Test folder on 9976, Headless Test on 9986.</summary>
    private static ServerPickerInputs LiveRig(bool selectorProved = true, bool thisMachine = true, string catalogName = "V Rising") =>
        new()
        {
            Connected = true,
            ConnectedServer = ServerIdentity.Connected("", "127.0.0.1", 9976),
            ConfiguredPath = Configured,
            ConfiguredName = "Progenitor Forms Test",
            ConfiguredPort = 9976,
            ConnectedIsConfigured = selectorProved,
            ConnectedPort = 9976,
            ConnectedIsThisMachine = thisMachine,
            ConnectedCatalogName = catalogName,
            Discovered = new[]
            {
                new LocalServerCandidate(Configured, "Progenitor Forms Test", "running now", running: true, port: 9976),
                new LocalServerCandidate(Headless, "Progenitor Headless Test", "running now", running: true, port: 9986),
            },
        };

    [Fact]
    public void The_connected_server_that_is_a_folder_on_this_PC_is_named_by_that_folder_and_listed_once()
    {
        var entries = ServerPicker.Build(LiveRig());
        Assert.Equal(
            new[] { "Connected: Progenitor Forms Test", "Progenitor Headless Test (this PC)", ServerPicker.PrivateGamesCaption, ServerPicker.NoneCaption },
            entries.Select(e => e.Caption).ToArray());
        var connected = entries[0];
        Assert.Equal("Progenitor Forms Test", connected.ShortName);
        Assert.Equal("127.0.0.1:9976", connected.Address);
        Assert.Equal(Configured, connected.Path);
        Assert.Contains(ServerPicker.SameServerDetail, connected.Detail);
        Assert.Equal("Server · Progenitor Forms Test", ServerPicker.HeaderCaption(connected));
        // The address stays visible as the secondary part: in the list when it fits one button,
        // and always in the dialog's line about the connection.
        Assert.Equal(connected.Caption, connected.ChoiceCaption);
        Assert.Equal("Connected to Progenitor Forms Test at 127.0.0.1:9976, a server on this PC.", ServerPicker.ConnectedLine(entries));
        var w = ServerPicker.ToWidget(connected);
        Assert.Equal("127.0.0.1:9976", w["address"]);
    }

    [Fact]
    public void A_short_name_keeps_the_address_beside_it_in_the_list()
    {
        var inputs = LiveRig();
        var entries = ServerPicker.Build(new ServerPickerInputs
        {
            Connected = true, ConnectedServer = inputs.ConnectedServer, ConfiguredPath = Configured, ConfiguredName = "Forms",
            ConfiguredPort = 9976, ConnectedIsConfigured = true, ConnectedPort = 9976, ConnectedIsThisMachine = true,
        });
        Assert.Equal("Connected: Forms", entries[0].Caption);
        Assert.Equal("Connected: Forms · 127.0.0.1:9976", entries[0].ChoiceCaption);
    }

    [Fact]
    public void Without_the_selector_s_proof_a_folder_on_this_machine_with_the_joined_port_is_the_connected_server()
    {
        var entries = ServerPicker.Build(LiveRig(selectorProved: false));
        Assert.Equal("Connected: Progenitor Forms Test", entries[0].Caption);
        Assert.DoesNotContain(entries, e => e.Kind == ServerPickKind.LocalFolder && ServerPicker.SameId(e.Path, Configured));
        Assert.Contains(entries, e => e.Caption == "Progenitor Headless Test (this PC)");
    }

    [Fact]
    public void A_server_on_another_machine_is_never_matched_to_a_local_folder()
    {
        var entries = ServerPicker.Build(LiveRig(selectorProved: false, thisMachine: false));
        Assert.Equal("Connected: 127.0.0.1:9976", entries[0].Caption);
        Assert.Contains(entries, e => e.Kind == ServerPickKind.LocalFolder && ServerPicker.SameId(e.Path, Configured));
        Assert.Equal("", entries[0].Path);
    }

    [Fact]
    public void Two_folders_on_the_joined_port_are_not_guessed_between()
    {
        var inputs = new ServerPickerInputs
        {
            Connected = true,
            ConnectedServer = ServerIdentity.Connected("", "127.0.0.1", 9976),
            ConnectedPort = 9976,
            ConnectedIsThisMachine = true,
            Discovered = new[]
            {
                new LocalServerCandidate(Configured, "A", "Steam", port: 9976),
                new LocalServerCandidate(Headless, "B", "Steam", port: 9976),
            },
        };
        Assert.Null(ServerPicker.ConnectedFolder(inputs));
        Assert.Equal("Connected: 127.0.0.1:9976", ServerPicker.Build(inputs)[0].Caption);
    }

    [Fact]
    public void A_remote_server_takes_its_catalog_name_but_not_the_catalog_default()
    {
        var named = LiveRig(selectorProved: false, thisMachine: false, catalogName: "Sunset PvE");
        Assert.Equal("Sunset PvE", ServerPicker.ConnectedName(named));
        Assert.Equal("Connected: Sunset PvE", ServerPicker.Build(named)[0].Caption);
        Assert.Equal("", ServerPicker.ConnectedName(LiveRig(selectorProved: false, thisMachine: false, catalogName: ServerPicker.ManifestDefaultName)));
        Assert.Equal("", ServerPicker.ConnectedLine(ServerPicker.Build(Inputs(connected: false))));
    }

    [Fact]
    public void A_folder_picked_before_it_was_known_to_be_the_connected_server_resolves_to_the_connected_entry()
    {
        var entries = ServerPicker.Build(LiveRig());
        var picked = ServerPicker.Resolve(ServerPicker.LocalId(Configured), entries);
        Assert.Equal(ServerPickKind.Connected, picked.Kind);
        Assert.Equal(ServerPickKind.LocalFolder, ServerPicker.Resolve(ServerPicker.LocalId(Headless), entries).Kind);
    }
}
