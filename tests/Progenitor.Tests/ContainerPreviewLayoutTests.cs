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
        var t = VanillaPanelPreview.Apply(read, Owning());
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
        var t = VanillaPanelPreview.Apply(read, Owning());
        Assert.True(t.HasNetworkId);
        Assert.Equal(40, t.NetworkIdIndex);
        Assert.Equal(1, t.NetworkId2);
    }

    [Fact]
    public void An_empty_target_during_a_preview_becomes_the_previewed_chest()
    {
        var t = VanillaPanelPreview.Apply(new VanillaPanelTarget(VanillaPanelKind.Container, 0, 0), Owning(targetIndex: 900, targetVersion: 1));
        Assert.Equal(900, t.EntityIndex);
        Assert.Equal(1, t.EntityVersion);
        Assert.Equal(Chest, t.PreviewContainer);
        // The chest off the client for a moment: the entity the window was opened on.
        var gone = VanillaPanelPreview.Apply(new VanillaPanelTarget(VanillaPanelKind.Container, 0, 0), Owning(targetIndex: 0, targetVersion: 0));
        Assert.Equal(812, gone.EntityIndex);
    }

    [Fact]
    public void A_chest_opened_for_real_is_left_alone()
    {
        var other = new VanillaPanelTarget(VanillaPanelKind.Container, 500, 2, 77, 4, 1);
        Assert.Equal(other, VanillaPanelPreview.Apply(other, Owning()));
        Assert.False(VanillaPanelPreview.Apply(other, Owning()).IsPreview);
        // No preview owns the window, or another kind of window: unchanged.
        var mine = new VanillaPanelTarget(VanillaPanelKind.Container, 812, 3, 40, 1, 1);
        Assert.False(VanillaPanelPreview.Apply(mine, default).IsPreview);
        var station = new VanillaPanelTarget(VanillaPanelKind.Workstation, 812, 3, 40, 1, 1);
        Assert.False(VanillaPanelPreview.Apply(station, Owning()).IsPreview);
    }

    [Fact]
    public void Two_targets_differ_by_their_preview_container()
    {
        var plain = new VanillaPanelTarget(VanillaPanelKind.Container, 812, 3, 40, 1, 1);
        var previewed = VanillaPanelPreview.Apply(plain, Owning());
        Assert.NotEqual(plain, previewed);
        Assert.Equal(previewed, VanillaPanelPreview.Apply(plain, Owning()));
    }

    [Fact]
    public void The_resolver_applies_the_preview_to_every_container_kind_target_and_keeps_it_for_the_readback()
    {
        string resolver = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "VanillaPanelSiteResolver.cs");
        Assert.Contains("if (kind == VanillaPanelKind.Container) panelTarget = VanillaPanelPreview.Apply(panelTarget, Progenitor.Inventory.ContainerPreviewClient.PreviewWindow(world));", resolver);
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
        Assert.Contains("mapper._InventorySubMenu", parts);
        Assert.Contains("mapper._ContainerSubMenu", parts);
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
        var shot = CameraPreviewFramingRules.FromCamera(0, 10, -10, 0, -s, s, 0, 0, 0);
        Assert.True(shot.Ok);
        Assert.Equal(45, shot.Pitch, 2);
        Assert.Equal(MathF.Sqrt(200), shot.Distance, 2);
        // Level or upward, far away or degenerate: not usable.
        Assert.False(CameraPreviewFramingRules.FromCamera(0, 1, -10, 0, 0, 1, 0, 0, 0).Ok);
        Assert.False(CameraPreviewFramingRules.FromCamera(0, 100, -100, 0, -s, s, 0, 0, 0).Ok);
        Assert.False(CameraPreviewFramingRules.FromCamera(0, 10, -10, 0, 0, 0, 0, 0, 0).Ok);
    }
}
