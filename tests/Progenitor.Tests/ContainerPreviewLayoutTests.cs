using System;
using Progenitor.Cameras;
using Progenitor.Inventory;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the container preview's station layout work: the panel sites' target while a preview owns
/// the container window (what attached mod tabs receive), the camera preview keeping every HUD canvas that holds part
/// of that window, and the camera preview's distance, pitch and player-camera framing. The game reads behind them are
/// client runtime code, not live-verified.
/// </summary>
public class ContainerPreviewLayoutTests
{
    private static readonly ContainerId Chest = new(new Guid("11111111-2222-3333-4444-555555555555"));

    private static VanillaPreviewWindow Owning(int windowIndex = 812, int windowVersion = 3, int targetIndex = 812, int targetVersion = 3) =>
        new(ownsWindow: true, windowIndex, windowVersion, targetIndex, targetVersion, net0: 40, net1: 1, net2: 1, container: Chest);

    // ---- the panel target ------------------------------------------------------------------------

    [Fact]
    public void The_container_panel_of_a_preview_names_the_previewed_chest_by_entity_network_id_and_container_id()
    {
        var read = new VanillaPanelTarget(VanillaPanelKind.Container, 812, 3, 40, 1, 1);
        var t = VanillaPanelPreview.Apply(read, Owning(), emptyIsWindow: true);
        Assert.True(t.IsPreview);
        Assert.Equal(Chest, t.PreviewContainer);
        Assert.Equal(812, t.EntityIndex);
        Assert.Equal(40, t.NetworkIdIndex);
        Assert.Equal(1, t.NetworkIdGeneration);
        Assert.Contains("preview=" + Chest, t.ToString());
    }

    [Fact]
    public void A_target_without_a_network_id_gets_the_servers_words()
    {
        var read = new VanillaPanelTarget(VanillaPanelKind.Container, 812, 3);   // the entity had no id to read
        var t = VanillaPanelPreview.Apply(read, Owning(), emptyIsWindow: true);
        Assert.True(t.HasNetworkId);
        Assert.Equal(40, t.NetworkIdIndex);
        Assert.Equal(1, t.NetworkId2);
    }

    [Fact]
    public void An_empty_window_target_during_a_preview_becomes_the_previewed_chest()
    {
        var t = VanillaPanelPreview.Apply(new VanillaPanelTarget(VanillaPanelKind.Container, 0, 0), Owning(targetIndex: 900, targetVersion: 1), emptyIsWindow: true);
        Assert.Equal(900, t.EntityIndex);
        Assert.Equal(1, t.EntityVersion);
        Assert.Equal(Chest, t.PreviewContainer);
        // The chest off the client for a moment: the entity the window was opened on.
        var gone = VanillaPanelPreview.Apply(new VanillaPanelTarget(VanillaPanelKind.Container, 0, 0), Owning(targetIndex: 0, targetVersion: 0), emptyIsWindow: true);
        Assert.Equal(812, gone.EntityIndex);
    }

    [Fact]
    public void A_chest_opened_for_real_is_left_alone()
    {
        var other = new VanillaPanelTarget(VanillaPanelKind.Container, 500, 2, 77, 4, 1);
        Assert.Equal(other, VanillaPanelPreview.Apply(other, Owning(), emptyIsWindow: true));
        Assert.False(VanillaPanelPreview.Apply(other, Owning(), emptyIsWindow: true).IsPreview);
        // No preview owns the window, or another kind of window: unchanged.
        var mine = new VanillaPanelTarget(VanillaPanelKind.Container, 812, 3, 40, 1, 1);
        Assert.False(VanillaPanelPreview.Apply(mine, default, emptyIsWindow: true).IsPreview);
        var station = new VanillaPanelTarget(VanillaPanelKind.Workstation, 812, 3, 40, 1, 1);
        Assert.False(VanillaPanelPreview.Apply(station, Owning(), emptyIsWindow: true).IsPreview);
    }

    [Fact]
    public void Two_targets_differ_by_their_preview_container()
    {
        var plain = new VanillaPanelTarget(VanillaPanelKind.Container, 812, 3, 40, 1, 1);
        var previewed = VanillaPanelPreview.Apply(plain, Owning(), emptyIsWindow: true);
        Assert.NotEqual(plain, previewed);
        Assert.Equal(previewed, VanillaPanelPreview.Apply(plain, Owning(), emptyIsWindow: true));
    }

    [Fact]
    public void The_resolver_applies_the_preview_to_every_container_kind_target_and_keeps_it_for_the_readback()
    {
        string resolver = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "VanillaPanelSiteResolver.cs");
        // STATIONPREVIEW1: every panel kind (Apply matches the preview window's own kind).
        Assert.Contains("if (kind != VanillaPanelKind.None) panelTarget = VanillaPanelPreview.Apply(panelTarget, Progenitor.Inventory.ContainerPreviewClient.PreviewWindow(world), emptyIsWindow: site != VanillaUiSite.InventoryTooltip);", resolver);
        Assert.Contains("LastContainerPanelTarget = panelTarget;", resolver);
        string client = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewClient.cs");
        Assert.Contains("[\"panelTarget\"] = PanelTargetReadback(),", client);
    }

    // ---- the station layout --------------------------------------------------------------------------

    [Fact]
    public void The_preview_shows_the_game_station_layout_by_default()
    {
        Assert.False(new ContainerPreviewOptions().HidePlayerInventory);
    }

    [Fact]
    public void The_camera_preview_keeps_every_canvas_that_holds_part_of_the_window()
    {
        var keep = CameraPreviewHud.CanvasesToKeep(true, new[] { false, true, false, true });
        Assert.Equal(new[] { false, true, false, true }, keep);
        Assert.True(CameraPreviewHud.Reasserts(0, keep));
        Assert.False(CameraPreviewHud.Reasserts(1, keep));
        Assert.False(CameraPreviewHud.Reasserts(3, keep));
        Assert.True(CameraPreviewHud.Reasserts(7, keep));
        Assert.All(CameraPreviewHud.CanvasesToKeep(false, new[] { true, true }), k => Assert.False(k));
        string preview = SourcePin.ReadFile("src", "Progenitor.Core", "Cameras", "CameraPreview.cs");
        Assert.Contains("parts = container!.WindowParts();", preview);
        string client = SourcePin.ReadFile("src", "Progenitor.Core", "Inventory", "ContainerPreviewClient.cs");
        string parts = SourcePin.MethodBody(client, "internal List<Transform> WindowParts(");
        Assert.Contains("var bag = window.Bag;", parts); // STATIONPREVIEW1: the halves of whichever window the preview opened
        Assert.Contains("var sub = window.Sub;", parts);
    }

    // ---- camera framing ----------------------------------------------------------------------------------

    [Fact]
    public void The_default_framing_is_unchanged_and_explicit_values_win()
    {
        Assert.Equal((6f, 35f), CameraPreviewFramingRules.Resolve(null, null, false, (true, 15, 50)));
        Assert.Equal((12f, 35f), CameraPreviewFramingRules.Resolve(12, null, false, default));
        Assert.Equal((15f, 50f), CameraPreviewFramingRules.Resolve(null, null, true, (true, 15, 50)));
        Assert.Equal((15f, 40f), CameraPreviewFramingRules.Resolve(null, 40, true, (true, 15, 50)));
        Assert.Equal((6f, 35f), CameraPreviewFramingRules.Resolve(null, null, true, (false, 0, 0)));
    }

    [Theory]
    [InlineData(1.9f, null, false)]
    [InlineData(40.1f, null, false)]
    [InlineData(null, 9.9f, false)]
    [InlineData(null, 85.1f, false)]
    [InlineData(2f, 10f, true)]
    [InlineData(40f, 85f, true)]
    [InlineData(null, null, true)]
    public void Explicit_framing_is_bounded(float? distance, float? pitch, bool ok) =>
        Assert.Equal(ok, CameraPreviewFramingRules.Problem(distance, pitch) == null);

    [Fact]
    public void The_players_camera_is_read_as_distance_and_pitch()
    {
        // 10 m up and 10 m back from the character, looking down at it: 45 degrees, about 14.1 m.
        float s = MathF.Sqrt(0.5f);
        var shot = CameraPreviewFramingRules.FromCamera(true, 0, 10, -10, 0, -s, s, 0, 0, 0);
        Assert.True(shot.Ok);
        Assert.Equal(45, shot.Pitch, 2);
        Assert.Equal(MathF.Sqrt(200), shot.Distance, 2);
        // Level or upward, far away or degenerate: not usable.
        Assert.False(CameraPreviewFramingRules.FromCamera(true, 0, 1, -10, 0, 0, 1, 0, 0, 0).Ok);
        Assert.False(CameraPreviewFramingRules.FromCamera(true, 0, 100, -100, 0, -s, s, 0, 0, 0).Ok);
        Assert.False(CameraPreviewFramingRules.FromCamera(true, 0, 10, -10, 0, 0, 0, 0, 0, 0).Ok);
    }

    [Fact]
    public void The_distance_is_to_the_look_at_point_not_the_character()
    {
        // The same camera, 2 m to the side of the character: the look-at point is 2 m from it on the ground, and the
        // distance is still the 14.1 m along the view axis (to the character it would be about 14.3 m).
        float s = MathF.Sqrt(0.5f);
        var shot = CameraPreviewFramingRules.FromCamera(true, 2, 10, -10, 0, -s, s, 0, 0, 0);
        Assert.True(shot.Ok);
        Assert.Equal(MathF.Sqrt(200), shot.Distance, 2);
        // A camera looking at ground more than 5 m from the character is not the player's camera.
        Assert.False(CameraPreviewFramingRules.FromCamera(true, 8, 10, -10, 0, -s, s, 0, 0, 0).Ok);
        // A camera below the character's height does not look down at it.
        Assert.False(CameraPreviewFramingRules.FromCamera(true, 0, -1, -10, 0, -s, s, 0, 0, 0).Ok);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]   // a preview being replaced still has its orbit on screen
    [InlineData(false, false, false)] // the game is not in its top-down mode (a switch has not landed)
    public void Only_the_players_own_camera_is_matched(bool anyShot, bool topDown, bool onScreen)
    {
        Assert.Equal(onScreen, CameraPreviewFramingRules.PlayerCameraOnScreen(anyShot, topDown));
        float s = MathF.Sqrt(0.5f);
        var shot = CameraPreviewFramingRules.FromCamera(CameraPreviewFramingRules.PlayerCameraOnScreen(anyShot, topDown), 0, 10, -10, 0, -s, s, 0, 0, 0);
        Assert.Equal(onScreen, shot.Ok);
        // Not matched: the defaults.
        var framing = CameraPreviewFramingRules.Resolve(null, null, true, shot);
        Assert.Equal(onScreen ? MathF.Sqrt(200) : CameraPreviewFramingRules.DefaultDistance, framing.Distance, 2);
    }

    // ---- the bag half and the close frame ----------------------------------------------------------

    [Fact]
    public void A_hovered_item_in_the_players_own_bag_half_does_not_name_the_previewed_chest()
    {
        // The inventory tooltip over the player's bag: kind Container (the container window), no target entity.
        var bagItem = new VanillaPanelTarget(VanillaPanelKind.Container, 0, 0, 0, 0, 0, itemGuid: 1234, itemAmount: 5);
        var t = VanillaPanelPreview.Apply(bagItem, Owning(), emptyIsWindow: false);
        Assert.False(t.IsPreview);
        Assert.False(t.HasEntity);
        Assert.Equal(bagItem, t);
        // The same empty target on the container panel is the window's own (the mapper has no target for a moment).
        var panel = VanillaPanelPreview.Apply(new VanillaPanelTarget(VanillaPanelKind.Container, 0, 0, 0, 0, 0), Owning(), emptyIsWindow: true);
        Assert.True(panel.IsPreview);
        Assert.Equal(812, panel.EntityIndex);
        // A hovered item in the container half names the chest's entity, so it is the preview's on either site.
        var chestItem = new VanillaPanelTarget(VanillaPanelKind.Container, 812, 3, 40, 1, 1, itemGuid: 1234, itemAmount: 5);
        Assert.True(VanillaPanelPreview.Apply(chestItem, Owning(), emptyIsWindow: false).IsPreview);
    }

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, false, false)] // the window is gone: the next sample is a plain miss, then Closed
    [InlineData(true, false, false, false)] // another chest is on the window now
    [InlineData(true, true, true, false)]   // the player opened that chest for real
    public void A_closed_preview_keeps_its_target_until_its_window_is_gone(bool shown, bool onWindow, bool interaction, bool held)
    {
        Assert.Equal(held, ContainerPreviewMachine.HoldsClosingView(shown, onWindow, interaction));
        // While held, the target is the same as it was before the close, so the panel sees no TargetChanged.
        var read = new VanillaPanelTarget(VanillaPanelKind.Container, 812, 3, 40, 1, 1);
        Assert.Equal(VanillaPanelPreview.Apply(read, Owning(), true), VanillaPanelPreview.Apply(read, Owning(), true));
    }
}
