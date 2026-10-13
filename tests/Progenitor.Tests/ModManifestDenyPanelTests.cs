using System;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Join-session lifecycle of the deny panel. A panel opened for join N must be
/// disposed — not hidden — when the client reaches the main menu, when join N+1
/// starts, and when a second result replaces it. Tests fail on a build that only
/// clears <c>ModManifestDeny.Current</c> and leaves the overlay standing.
/// </summary>
[Collection("PluginLogger")]
public sealed class ModManifestDenyPanelTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly UiTests.FakeUiRuntime _client = new() { IsClient = true };

    public ModManifestDenyPanelTests()
    {
        Plugin.Logger = _log;
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_client);
        WorldEpoch.ResetForTests();
        UiFontLookup.Invalidate();
        ModManifestDeny.ResetForTests();
    }

    public void Dispose()
    {
        ModManifestDenyPanel.Close();
        ModManifestDeny.ResetForTests();
        UiApi.ResetForTests();
        UiFontLookup.Invalidate();
        WorldEpoch.ResetForTests();
        Plugin.Logger = null;
    }

    [Fact]
    public void A_deny_panel_opened_for_join_N_is_disposed_when_join_N_plus_1_starts()
    {
        var panel = ModManifestDenyPanel.Open(Report());
        Assert.True(ModManifestDenyPanel.IsOpen);
        Assert.False(panel.IsDisposed);

        ModManifestDeny.OnJoinAttempt();

        Assert.True(panel.IsDisposed);
        Assert.Equal(UiHandleState.Disposed, panel.State);
        Assert.False(ModManifestDenyPanel.IsOpen);
        Assert.False(ModManifestDenyPanel.HasHeldPanel);
        Assert.Contains(_log.Infos, l => l.Contains("deny panel closed.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_deny_panel_opened_for_join_N_is_disposed_when_the_client_reaches_the_main_menu()
    {
        var panel = ModManifestDenyPanel.Open(Report());
        Assert.True(ModManifestDenyPanel.IsOpen);

        Handshake.NotifyClientReturnedToMenu();

        Assert.True(panel.IsDisposed);
        Assert.Equal(UiHandleState.Disposed, panel.State);
        Assert.False(ModManifestDenyPanel.IsOpen);
        Assert.False(ModManifestDenyPanel.HasHeldPanel);
    }

    [Fact]
    public void A_second_result_replaces_the_panel_the_first_result_opened()
    {
        var first = ModManifestDenyPanel.Open(Report("Night Garden"));
        Assert.True(ModManifestDenyPanel.IsOpen);

        var second = ModManifestDenyPanel.Open(Report("Other Garden"));

        Assert.True(first.IsDisposed);
        Assert.Equal(UiHandleState.Disposed, first.State);
        Assert.NotSame(first, second);
        Assert.True(ModManifestDenyPanel.IsOpen);
        Assert.Equal(UiHandleState.Shown, second.State);
    }

    [Fact]
    public void Close_disposes_the_handle_it_does_not_hide_it()
    {
        var panel = ModManifestDenyPanel.Open(Report());
        ModManifestDenyPanel.Close();

        Assert.True(panel.IsDisposed);
        Assert.Equal(UiHandleState.Disposed, panel.State);
        Assert.False(ModManifestDenyPanel.HasHeldPanel);
        Assert.False(_client.OverlayVisibleFor(ModDenyText.PanelTitle("Night Garden")));
    }

    [Fact]
    public void Escape_then_DismissHidden_disposes_the_handle_escape_only_hid()
    {
        var panel = ModManifestDenyPanel.Open(Report());
        UiApi.PressKeyForTests(27, down: true);
        Assert.Equal(UiHandleState.Built, panel.State);
        Assert.False(panel.IsDisposed);

        ModManifestDenyPanel.DismissHidden();

        Assert.True(panel.IsDisposed);
        Assert.Equal(UiHandleState.Disposed, panel.State);
        Assert.False(ModManifestDenyPanel.HasHeldPanel);
    }

    [Fact]
    public void A_descriptor_rebuild_does_not_log_a_close_open_pair()
    {
        ModManifestDenyPanel.Open(Report());
        _log.Clear();

        var rebuilt = ModManifestDenyPanel.Open(Report(sourced: true));

        Assert.True(ModManifestDenyPanel.IsOpen);
        Assert.Equal(UiHandleState.Shown, rebuilt.State);
        Assert.DoesNotContain(_log.Infos, l => l.Contains("deny panel closed.", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Infos, l => l.Contains("deny panel opened", StringComparison.Ordinal));
    }

    [Fact]
    public void Opening_after_an_epoch_change_reuses_the_cached_font()
    {
        ModManifestDenyPanel.Open(Report());
        int afterWarm = _client.SceneSweepCount;
        Assert.True(afterWarm >= 1);
        ModManifestDenyPanel.Close();

        WorldEpoch.Advance("deny-panel-font");
        ModManifestDenyPanel.Open(Report());

        Assert.Equal(afterWarm, _client.SceneSweepCount);
    }

    [Fact]
    public void Core_unload_disposes_the_deny_panel()
    {
        var panel = ModManifestDenyPanel.Open(Report());
        ModManifestDenyPanel.Uninstall();

        Assert.True(panel.IsDisposed);
        Assert.False(ModManifestDenyPanel.IsOpen);
        Assert.False(ModManifestDenyPanel.HasHeldPanel);
    }

    private static ModDenyReport Report(string server = "Night Garden", bool sourced = false) =>
        new ModDenyReport(
            "Cannot join — server mods",
            "127.0.0.1:9976",
            server,
            "Ada",
            "fp",
            descriptorReceived: sourced,
            new[]
            {
                new ModDenyRow(
                    "progenitor.sample",
                    "1.0.0",
                    null,
                    ModDenyOrigin.ServerRequires,
                    "the server requires it",
                    package: sourced ? "ExampleTeam-Sample" : null),
            });
}
