using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Inventory;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// STATIONPREVIEW1, game-free: <c>ctx.Inventory.Preview</c> opening a crafting, refining or salvage station's own window
/// (the window the server picks, the refusals, the panel target, the wire, and what a station preview's window is).
/// </summary>
public sealed class StationPreviewTests
{
    [Fact]
    public void A_plain_container_opens_the_container_window()
    {
        Assert.Equal(ContainerPreviewMenus.ContainerMenu, ContainerPreviewMenus.WindowFor(false, Array.Empty<int>(), false, clientOpensStations: true));
        Assert.Equal(ContainerPreviewMenus.ContainerMenu, ContainerPreviewMenus.WindowFor(false, Array.Empty<int>(), false, clientOpensStations: false));
    }

    [Theory]
    [InlineData(ContainerPreviewMenus.WorkstationMenu)]
    [InlineData(ContainerPreviewMenus.RefinementstationMenu)]
    [InlineData(ContainerPreviewMenus.SalvagestationMenu)]
    public void A_lone_station_opens_its_own_window_for_a_client_that_can(int menu)
    {
        Assert.Equal(menu, ContainerPreviewMenus.WindowFor(true, new[] { menu }, false, clientOpensStations: true));
        Assert.Equal(0, ContainerPreviewMenus.WindowFor(true, new[] { menu }, false, clientOpensStations: false)); // an older client
        Assert.Equal(0, ContainerPreviewMenus.WindowFor(true, new[] { menu }, otherOwnMenu: true, clientOpensStations: true));
    }

    [Fact]
    public void Another_menu_is_refused()
    {
        Assert.Equal(0, ContainerPreviewMenus.WindowFor(true, Array.Empty<int>(), true, true)); // a castle heart, a prison, a coffin...
        Assert.Equal(0, ContainerPreviewMenus.WindowFor(true, Array.Empty<int>(), false, true)); // an own menu, no station
        Assert.Contains("LightningRodStation", ContainerPreviewMenus.OwnMenuComponents);
        Assert.Contains("LightningRodStation", ContainerPreviewMenus.OtherOwnMenuComponents);
    }

    [Fact]
    public void A_devourer_that_also_lists_recipes_opens_the_salvage_window()
    {
        // The server lists the station windows in PreviewableStations order: salvage, refining, then the recipe list.
        var devourer = new[] { ContainerPreviewMenus.SalvagestationMenu, ContainerPreviewMenus.WorkstationMenu };
        Assert.Equal(ContainerPreviewMenus.SalvagestationMenu, ContainerPreviewMenus.WindowFor(true, devourer, false, true));
        var furnace = new[] { ContainerPreviewMenus.RefinementstationMenu, ContainerPreviewMenus.WorkstationMenu };
        Assert.Equal(ContainerPreviewMenus.RefinementstationMenu, ContainerPreviewMenus.WindowFor(true, furnace, false, true));
        var bench = new[] { ContainerPreviewMenus.WorkstationMenu };
        Assert.Equal(ContainerPreviewMenus.WorkstationMenu, ContainerPreviewMenus.WindowFor(true, bench, false, true));
        // Never past another own menu.
        Assert.Equal(0, ContainerPreviewMenus.WindowFor(true, devourer, otherOwnMenu: true, clientOpensStations: true));
    }

    [Fact]
    public void The_window_opens_only_with_every_inventory_instance_present()
    {
        Assert.True(ContainerPreviewInventories.Ready(1, new[] { true }));
        Assert.True(ContainerPreviewInventories.Ready(2, new[] { true, true }));
        Assert.False(ContainerPreviewInventories.Ready(2, new[] { true, false })); // a furnace's output not here yet
        Assert.False(ContainerPreviewInventories.Ready(2, new[] { false, true })); // the first missing, the second here
        Assert.False(ContainerPreviewInventories.Ready(0, Array.Empty<bool>()));
        Assert.False(ContainerPreviewInventories.Ready(2, new[] { true }));
    }

    [Fact]
    public void The_previewable_stations_and_the_rest_make_the_whole_refusal_list()
    {
        var stations = ContainerPreviewMenus.PreviewableStations.Select(s => s.Component).ToList();
        Assert.Equal(new[] { "Salvagestation", "Refinementstation", "WorkstationRecipesBuffer" }, stations);
        var all = ContainerPreviewMenus.OtherOwnMenuComponents.Concat(stations).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(ContainerPreviewMenus.OwnMenuComponents.OrderBy(x => x, StringComparer.Ordinal), all);
        foreach (var s in stations) Assert.DoesNotContain(s, ContainerPreviewMenus.OtherOwnMenuComponents);
    }

    private static List<string> HasChecks(string method)
    {
        string source = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewServer.cs");
        int start = source.IndexOf(method, StringComparison.Ordinal);
        Assert.True(start > 0, method);
        int end = source.IndexOf(method.Contains("List<int>") ? "return menus;" : ";", start, StringComparison.Ordinal);
        string body = source.Substring(start, end - start);
        return System.Text.RegularExpressions.Regex.Matches(body, @"Has<(?:[A-Za-z0-9_]+\.)*([A-Za-z0-9_]+)>\(em\)")
            .Select(m => m.Groups[1].Value).ToList();
    }

    [Fact]
    public void The_server_checks_the_other_menus_and_the_stations_by_the_same_names()
    {
        Assert.Equal(ContainerPreviewMenus.OtherOwnMenuComponents.ToList(), HasChecks("private static bool HasOtherOwnMenu("));
        Assert.Equal(ContainerPreviewMenus.PreviewableStations.Select(s => s.Component).ToList(), HasChecks("private static List<int> StationMenusOf("));
    }

    [Fact]
    public void The_refusal_names_stations_only_to_a_client_that_opens_them()
    {
        var f = new ContainerPreviewFacts
        {
            UserConnected = true, Mode = ContainerPreviewMode.On, RateOk = true, IdParsed = true, Found = true, HasInventory = true,
            HasOwnMenu = true, InCastle = true, Relation = Progenitor.Castles.CastleRelation.Owner, HasNetworkId = true,
        };
        Assert.Equal("Only plain containers (chests, braziers) can be previewed.", ContainerPreviewAuthorization.Refusal(f));
        f.ClientOpensStations = true;
        Assert.Equal(ContainerPreviewAuthorization.StationRefusal, ContainerPreviewAuthorization.Refusal(f));
        f.HasOwnMenu = false; // a lone station for a client that can
        Assert.Null(ContainerPreviewAuthorization.Refusal(f));
    }

    [Fact]
    public void The_window_kinds_and_menus_map_both_ways()
    {
        foreach (int menu in new[] { 3, 4, 6, 22 })
        {
            Assert.True(ContainerPreviewMenus.IsPreviewWindow(menu));
            Assert.Equal(menu, ContainerPreviewMenus.MenuOf(ContainerPreviewMenus.KindOf(menu)));
        }
        Assert.False(ContainerPreviewMenus.IsPreviewWindow(18)); // the castle heart
        Assert.Equal(VanillaPanelKind.None, ContainerPreviewMenus.KindOf(18));
        Assert.Equal(0, ContainerPreviewMenus.MenuOf(VanillaPanelKind.CastleHeart));
        Assert.Equal(VanillaPanelKind.SalvageStation, ContainerPreviewMenus.KindOf(ContainerPreviewMenus.SalvagestationMenu));
    }

    [Fact]
    public void A_station_preview_names_its_station_on_the_station_panel_target_only()
    {
        var id = new ContainerId(Guid.NewGuid());
        var preview = new VanillaPreviewWindow(true, 50, 2, 50, 2, 900, 3, 1, id, VanillaPanelKind.SalvageStation);
        var station = new VanillaPanelTarget(VanillaPanelKind.SalvageStation, 50, 2, 0, 0, 0, 0, 0);
        var applied = VanillaPanelPreview.Apply(station, preview, emptyIsWindow: true);
        Assert.Equal(id, applied.PreviewContainer);
        Assert.Equal(900, applied.NetworkId0); // the server's words when the entity has none to read

        // A container window, or another station window, is not this preview's.
        var chest = new VanillaPanelTarget(VanillaPanelKind.Container, 50, 2, 0, 0, 0, 0, 0);
        Assert.True(VanillaPanelPreview.Apply(chest, preview, true).PreviewContainer.IsNone);
        var bench = new VanillaPanelTarget(VanillaPanelKind.Workstation, 50, 2, 0, 0, 0, 0, 0);
        Assert.True(VanillaPanelPreview.Apply(bench, preview, true).PreviewContainer.IsNone);
        // The default kind stays the container window, as before.
        Assert.Equal(VanillaPanelKind.Container, new VanillaPreviewWindow(true, 1, 1, 1, 1, 1, 1, 1, id).Kind);
    }

    [Fact]
    public void The_wire_carries_the_station_wish_and_the_window()
    {
        var open = new ContainerPreviewPacket { Kind = ContainerPreviewWire.KindOpen, Sequence = 2, Container = "c-" + new string('b', 32), Seconds = 30, Stations = true };
        var req = ContainerPreviewWire.Decode(ContainerPreviewWire.Encode(open), expectRequest: true);
        Assert.True(req!.Stations);
        var reply = new ContainerPreviewPacket { Kind = ContainerPreviewWire.KindReply, Sequence = 2, Ok = true, MenuType = ContainerPreviewMenus.SalvagestationMenu };
        Assert.Equal(22, ContainerPreviewWire.Decode(ContainerPreviewWire.Encode(reply), expectRequest: false)!.MenuType);
        // An older client's packet has no Stations field; an older server's reply no MenuType.
        var old = System.Text.Encoding.UTF8.GetBytes("{\"Version\":" + ContainerPreviewWire.Version + ",\"Kind\":\"open\",\"Sequence\":3,\"Container\":\"c-" + new string('c', 32) + "\",\"Seconds\":30}");
        Assert.False(ContainerPreviewWire.Decode(old, expectRequest: true)!.Stations);
    }

    [Theory]
    [InlineData(VanillaPanelKind.Container, true)]
    [InlineData(VanillaPanelKind.Workstation, true)]
    [InlineData(VanillaPanelKind.RefinementStation, true)]
    [InlineData(VanillaPanelKind.SalvageStation, true)] // The Devourer: the user's case
    [InlineData(VanillaPanelKind.CastleHeart, false)]
    [InlineData(VanillaPanelKind.PrisonCell, false)]
    [InlineData(VanillaPanelKind.UnitStation, false)]
    public void The_reopen_fallback_opens_stations_from_afar_too(VanillaPanelKind kind, bool fromAfar)
    {
        Assert.Equal(fromAfar, ContainerReturnRules.OpensFromAfar(kind));
        Assert.Contains("salvage stations", ContainerReturnRules.RemoteNotAContainer);
    }

    [Fact]
    public void A_station_window_with_no_interaction_is_a_previews_and_never_remembered()
    {
        Assert.False(ContainerReturnRules.Remembers(VanillaPanelKind.SalvageStation, hasInteraction: false, previewOwnsWindow: false));
        Assert.True(ContainerReturnRules.Remembers(VanillaPanelKind.SalvageStation, hasInteraction: true, previewOwnsWindow: false));
        Assert.False(ContainerReturnRules.Remembers(VanillaPanelKind.Workstation, hasInteraction: true, previewOwnsWindow: true));
    }
}
