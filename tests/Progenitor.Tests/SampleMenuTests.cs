using System;
using System.Text.Json;
using Progenitor.Sample;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

[Collection("PluginLogger")]
public sealed class SampleMenuTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly object _owner = new();

    public SampleMenuTests()
    {
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        SampleMenu.ClearFixtures();
    }

    public void Dispose()
    {
        SampleMenu.ClearFixtures();
        ModsMenu.Close();
        ModsMenu.UninstallLifecycleWatch();
        UiApi.ResetForTests();
    }

    [Fact]
    public void Fixtures_parse_accepts_a_count_or_off_and_rejects_the_rest()
    {
        Assert.True(SampleMenu.TryParse("fixtures", "24", out int n, out bool off));
        Assert.Equal(24, n);
        Assert.False(off);

        Assert.True(SampleMenu.TryParse("FIXTURES", "off", out n, out off));
        Assert.Equal(0, n);
        Assert.True(off);

        Assert.False(SampleMenu.TryParse("fixtures", "0", out _, out _));
        Assert.False(SampleMenu.TryParse("fixtures", "49", out _, out _));
        Assert.False(SampleMenu.TryParse("show", "24", out _, out _));
        Assert.False(SampleMenu.TryParse("fixtures", "", out _, out _));
        Assert.Equal(SampleMenu.UsageReply, SampleMenu.Handle(_owner, "nope", ""));
    }

    [Fact]
    public void Fixture_labels_are_long_and_numbered()
    {
        Assert.Equal("progenitor.sample.fixture.01", SampleMenu.FixtureId(1));
        Assert.Equal("Fixture 01", SampleMenu.FixtureName(1));
        Assert.StartsWith("Fixture 01 — ", SampleMenu.FixtureLabel(1));
        Assert.Contains(SampleMenu.FixtureId(1), SampleMenu.FixtureLabel(1));
        Assert.True(SampleMenu.FixtureLabel(1).Length > SampleMenu.FixtureName(1).Length + 20);
        var pane = MenuPanelLayout.ForCanvas(1280f, 720f, true);
        Assert.NotEqual(SampleMenu.FixtureLabel(1), MenuPanelLayout.ClipLabel(SampleMenu.FixtureLabel(1), pane.RowWidth));
    }

    [Fact]
    public void Fixtures_register_through_the_menu_and_off_unregisters()
    {
        Assert.Equal(SampleMenu.AppliedReply(3), SampleMenu.Handle(_owner, "fixtures", "3"));
        Assert.Equal(3, SampleMenu.FixtureCount);

        ModsMenu.Open();
        string json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.Contains("progenitor.sample.fixture.01", json);
        Assert.Contains("progenitor.sample.fixture.03", json);
        Assert.Contains("Fixture 01", json);

        Assert.Equal(SampleMenu.OffReply, SampleMenu.Handle(_owner, "fixtures", "off"));
        Assert.Equal(0, SampleMenu.FixtureCount);
        json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.DoesNotContain("progenitor.sample.fixture.", json);
    }

    [Fact]
    public void Enough_fixtures_overflow_the_list_pane()
    {
        SampleMenu.ApplyFixtures(_owner, 24);
        // The fixtures sit under this framework's namespace, so they are in the Required by
        // Progenitor group; it is expanded here rather than left to whatever an earlier test did.
        ModsMenu.SetRequiredGroupExpandedForTests(true);
        ModsMenu.Open();
        string json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.GetProperty("totalRows").GetInt32() >= 24);
        Assert.True(doc.RootElement.GetProperty("listScroll").GetProperty("overflow").GetBoolean());
        Assert.Equal(1f, doc.RootElement.GetProperty("listScroll").GetProperty("position").GetSingle());
        SampleMenu.ClearFixtures();
        ModsMenu.SetRequiredGroupExpandedForTests(false);
        json = JsonSerializer.Serialize(ModsMenu.DescribeWorkspace());
        Assert.DoesNotContain("progenitor.sample.fixture.", json);
    }

    [Fact]
    public void Sample_registers_the_menudemo_command_class()
    {
        string plugin = SourcePin.ReadFile("samples", "Progenitor.Sample", "Plugin.cs");
        SourcePin.AssertStatementExactly(plugin, "ctx.Commands.Register(typeof(SampleMenuCommands));");
        string menu = SourcePin.ReadFile("samples", "Progenitor.Sample", "SampleMenu.cs");
        SourcePin.AssertUncommented(menu, "[Command(\"menudemo\"");
    }
}
