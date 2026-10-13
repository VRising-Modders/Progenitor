using System;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Progenitor.Dev.Ops;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// The version footer: every prompt Progenitor shows carries <c>Progenitor &lt;version&gt;</c> in small
/// muted text centered under its last row, a plugin's panel carries nothing unless it opts in through
/// <see cref="IUiPanel.Footer"/>, and the Mods workspace carries the component versions
/// (<c>Progenitor · Bootstrap[ · Dev]</c>). Game-free: a fake runtime records what was asked of it;
/// the Unity drawing is source-checked only.
/// </summary>
[Collection("PluginLogger")]
public sealed class PanelFooterTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly object _owner = new();

    public PanelFooterTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
        FrameworkVersions.ResetForTests();
        FrameworkVersions.CoreForTests = "2026.9.58";
        UseLoaded(bootstrap: true, dev: false);
    }

    public void Dispose()
    {
        UiDialog.ForgetForTests();
        UiLayoutBatch.ResetForTests();
        try { ModsMenu.Close(); } catch { /* not every test opens it */ }
        ModsMenu.ResetTabBindingsForTests();
        ModsWorkspaceFrame.ResetForTests();
        ModMenuInventory.ResetForTests();
        FrameworkVersions.ResetForTests();
        Plugin.Logger = null;
        UiApi.ResetForTests();
    }

    /// <summary>Stand-in assemblies: any loaded assembly will do, its version is not what is asserted.</summary>
    private static void UseLoaded(bool bootstrap, bool dev)
    {
        Assembly any = typeof(PanelFooterTests).Assembly;
        FrameworkVersions.FindLoadedForTests = name =>
            name == FrameworkVersions.BootstrapAssemblyName ? (bootstrap ? any : null)
            : name == FrameworkVersions.DevAssemblyName ? (dev ? any : null)
            : null;
    }

    // ---- version strings ----------------------------------------------------------------------

    [Theory]
    [InlineData("2026.9.58+2a14494cde", "2026.9.58")]
    [InlineData("2026.9.58.1+2a14494cde.dirty", "2026.9.58.1")]
    [InlineData(" 2026.9.12 ", "2026.9.12")]
    [InlineData("2026.9.12.3", "2026.9.12.3")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_strips_build_metadata_and_keeps_three_or_four_parts(string? informational, string expected) =>
        Assert.Equal(expected, FrameworkVersions.Normalize(informational));

    [Fact]
    public void Assembly_version_fallback_drops_a_zero_sub_release_only()
    {
        Assert.Equal("2026.9.12", FrameworkVersions.FromAssemblyVersion(new Version(2026, 9, 12, 0)));
        Assert.Equal("2026.9.12.3", FrameworkVersions.FromAssemblyVersion(new Version(2026, 9, 12, 3)));
        Assert.Equal("", FrameworkVersions.FromAssemblyVersion(null));
    }

    [Fact]
    public void A_real_assembly_reads_without_metadata()
    {
        string v = FrameworkVersions.Of(typeof(FrameworkVersions).Assembly);
        Assert.DoesNotContain("+", v);
        Assert.Matches(new Regex(@"^\d+(\.\d+){2,3}$"), v);
        Assert.Equal(FrameworkVersions.Unknown, FrameworkVersions.Of(null));
    }

    [Fact]
    public void Prompt_footer_is_the_product_name_and_core_version()
    {
        Assert.Equal("Progenitor 2026.9.58", FrameworkVersions.PromptFooter("2026.9.58+abc"));
        Assert.Equal("Progenitor 2026.9.58.4", FrameworkVersions.PromptFooter("2026.9.58.4+abc.dirty"));
        Assert.Equal("Progenitor unknown", FrameworkVersions.PromptFooter(null));
        Assert.Equal("Progenitor 2026.9.58", UiApi.FrameworkFooter);
        Assert.Equal("2026.9.58", UiApi.FrameworkVersion);
    }

    [Fact]
    public void Workspace_footer_names_core_and_bootstrap_and_never_the_dev_bridge()
    {
        Assert.Equal(
            "Progenitor Core 2026.9.58 · Bootstrap 2026.9.57",
            FrameworkVersions.WorkspaceFooter("2026.9.58", "2026.9.57+abc"));
        Assert.Equal(
            "Progenitor Core 2026.9.58.2 · Bootstrap unknown",
            FrameworkVersions.WorkspaceFooter("2026.9.58.2+abc.dirty", null));

        UseLoaded(bootstrap: true, dev: true);
        Assert.DoesNotContain("Dev", FrameworkVersions.CurrentWorkspaceFooter());
        Assert.StartsWith("Progenitor Core ", FrameworkVersions.CurrentWorkspaceFooter());
        Assert.Contains("· Bootstrap ", FrameworkVersions.CurrentWorkspaceFooter());
    }

    // ---- who carries a footer -----------------------------------------------------------------

    [Fact]
    public void Core_owned_panels_carry_the_framework_footer_and_plugin_panels_do_not()
    {
        var core = UiApi.Panel("Progenitor prompt", _owner, FrameworkVersions.CorePluginId);
        Assert.Equal("Progenitor 2026.9.58", core.Footer);
        Assert.Equal("Progenitor 2026.9.58", _runtime.FooterFor("Progenitor prompt"));

        var plugin = UiApi.Panel("Plugin panel", _owner, "example.sample");
        Assert.Equal("", plugin.Footer);
        Assert.Null(_runtime.FooterFor("Plugin panel"));
    }

    [Fact]
    public void A_plugin_panel_opts_in_with_the_framework_line_or_its_own()
    {
        var panel = UiApi.Panel("Plugin panel", _owner, "example.sample");
        panel.Footer = UiApi.FrameworkFooter;
        Assert.Equal("Progenitor 2026.9.58", _runtime.FooterFor("Plugin panel"));

        panel.Footer = "  Sample 1.2.3  ";
        Assert.Equal("Sample 1.2.3", panel.Footer);
        Assert.Equal("Sample 1.2.3", _runtime.FooterFor("Plugin panel"));

        panel.Footer = null!;
        Assert.Equal("", panel.Footer);
        Assert.Equal("", _runtime.FooterFor("Plugin panel"));
    }

    [Fact]
    public void Workspace_panes_replace_the_default_the_sidebar_drops_it()
    {
        var sidebar = UiApi.Panel("Sidebar", _owner, FrameworkVersions.CorePluginId);
        UiApi.ConfigureMenuPanel(sidebar, sidebar: true);
        Assert.Equal("", sidebar.Footer);
        Assert.Equal("", _runtime.FooterFor("Sidebar"));

        var detail = UiApi.Panel("Detail", _owner, FrameworkVersions.CorePluginId);
        UiApi.ConfigureMenuPanel(detail, sidebar: false);
        Assert.Equal("Progenitor Core 2026.9.58 · Bootstrap " + FrameworkVersions.Bootstrap, detail.Footer);
        Assert.Equal(detail.Footer, _runtime.FooterFor("Detail"));
    }

    [Fact]
    public void Every_dialog_carries_the_framework_footer_and_reads_it_back()
    {
        var closed = UiDialog.Describe();
        Assert.Equal("", closed["footer"]);

        // A plugin id that is not Core's: the dialog is still a Progenitor prompt.
        var dialog = UiApi.Dialog(
            "Install HotDemo?", "Body.", "Yes", LabelTone.Ok, "No",
            tag: "prompt", kind: "install", key: "k", owner: _owner, pluginId: "example.installux2");
        Assert.Equal("Progenitor 2026.9.58", dialog.Panel.Footer);
        Assert.Equal("Progenitor 2026.9.58", _runtime.FooterFor("Install HotDemo?"));
        var open = UiDialog.Describe();
        Assert.Equal(true, open["open"]);
        Assert.Equal("Progenitor 2026.9.58", open["footer"]);

        dialog.Close();
        Assert.Equal("", UiDialog.Describe()["footer"]);
    }

    // ---- layout arithmetic --------------------------------------------------------------------

    [Fact]
    public void The_band_is_reserved_below_the_content_so_a_fitting_panel_still_fits()
    {
        Assert.Equal(0f, PanelFooterLayout.BandFor(""));
        Assert.Equal(0f, PanelFooterLayout.BandFor(null));
        Assert.Equal(PanelFooterLayout.BandHeight, PanelFooterLayout.BandFor("Progenitor 2026.9.58"));
        Assert.True(PanelFooterLayout.FontSize < SettingsAlign.NoteFontSize, "the footer must be smaller than a note");

        var layout = new PanelLayout();
        for (int i = 0; i < 3; i++) layout.NextRow(UnityUiRuntimeMetrics.RowHeight);
        float content = layout.ContentHeight;
        float plain = PanelLayout.PanelHeightFor(content, 1080f);
        float footed = PanelFooterLayout.PanelHeightFor(content, 1080f, "Progenitor 2026.9.58");
        Assert.True(footed >= plain);
        Assert.False(PanelLayout.Scrolls(content, PanelFooterLayout.ViewportHeightFor(footed, "Progenitor 2026.9.58")),
            "the last button must not be pushed below the viewport by the footer");
        Assert.Equal(plain, PanelFooterLayout.PanelHeightFor(content, 1080f, ""));
    }

    [Fact]
    public void The_footer_centres_on_the_workspace_frame_without_leaving_its_pane()
    {
        Assert.Equal(0f, PanelFooterLayout.CentredOffset(100f, 100f, 1248f));
        // A detail pane right of the sidebar: the line moves left towards the frame's center.
        Assert.Equal(-188f, PanelFooterLayout.CentredOffset(0f, 188f, 1248f), 3);
        // Never so far that the text rect leaves the pane.
        float room = (1248f - PanelFooterLayout.TextWidth(1248f)) * 0.5f - PanelLayout.PadSide;
        Assert.Equal(-room, PanelFooterLayout.CentredOffset(-5000f, 0f, 1248f), 3);
        Assert.Equal(0f, PanelFooterLayout.CentredOffset(-5000f, 0f, 400f), 3);
        Assert.Equal(PanelFooterLayout.MaxTextWidth, PanelFooterLayout.TextWidth(1248f));
        Assert.Equal(400f - 2f * PanelLayout.PadSide, PanelFooterLayout.TextWidth(400f));
    }

    // ---- readback -----------------------------------------------------------------------------

    [Fact]
    public void Status_carries_the_footer_and_the_dialog_footer_in_every_shape()
    {
        var empty = ModsUiResult.EmptyStatus();
        Assert.Equal("", empty["footer"]);
        Assert.Equal("", ModsUiResult.DialogClosed()["footer"]);

        var status = ModsUiResult.Status(
            open: true, site: "top-tab", plugin: null, page: "overview", pageId: "", search: "",
            entries: Array.Empty<object>(), curtain: true, capture: true, width: 3840, height: 1080,
            mode: "window", panes: 2, observers: 0,
            footer: "Progenitor 2026.9.58 · Bootstrap 2026.9.57");
        Assert.Equal("Progenitor 2026.9.58 · Bootstrap 2026.9.57", status["footer"]);
    }

    [Fact]
    public void The_open_workspace_reports_its_footer_and_a_closed_one_reports_none()
    {
        using (var closed = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
            Assert.Equal("", closed.RootElement.GetProperty("footer").GetString());

        ModsMenu.Open();
        string expected = FrameworkVersions.WorkspaceFooter("2026.9.58", FrameworkVersions.Bootstrap);
        using (var open = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.DescribeWorkspace())))
            Assert.Equal(expected, open.RootElement.GetProperty("footer").GetString());
        Assert.Equal(expected, ModsMenu.WorkspaceFooterText());
        Assert.Equal("", _runtime.FooterFor("Mods"));
        Assert.Equal("", _runtime.FooterFor(ModsMenu.StripPanelTitle));
    }
}
