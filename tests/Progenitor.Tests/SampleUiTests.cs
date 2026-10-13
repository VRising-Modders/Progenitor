using System;
using Progenitor.Sample;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of Sample <c>.ui demo</c>: token parse, fake-runtime panel,
/// click-through the slot table, and close disposing the handle.
/// </summary>
[Collection("PluginLogger")]
public class SampleUiTests : IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly UiTests.FakeUiRuntime _client = new() { IsClient = true };
    private readonly object _owner = new();

    public SampleUiTests()
    {
        Plugin.Logger = _log;
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_client);
        SampleUi.AttachForTests(_owner, "progenitor.sample");
        SampleUi.EmberStackForTests = 99;
    }

    public void Dispose()
    {
        SampleUi.Detach();
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("demo", 1)]
    [InlineData("DEMO", 1)]
    [InlineData(" close ", 2)]
    [InlineData("off", 2)]
    public void TryParse_accepts_the_sample_subcommands(string? token, int expected)
    {
        Assert.True(SampleUi.TryParse(token, out var kind));
        Assert.Equal(expected, (int)kind);
    }

    [Theory]
    [InlineData("ember")]
    [InlineData("ui")]
    [InlineData("images")]
    public void TryParse_rejects_unknown(string token)
    {
        Assert.False(SampleUi.TryParse(token, out _));
    }

    [Fact]
    public void Demo_opens_a_panel_with_two_buttons_a_toggle_and_the_ember_label()
    {
        SampleUi.Run(SampleUi.Kind.Demo);
        Assert.Equal(1, SampleUi.LiveCount);
        Assert.Equal(1, _client.PanelCount);
        Assert.Equal(1, _client.ShowCount);
        Assert.Contains(_log.Infos, l => l == "Sample: ui demo open");
        Assert.Contains(_log.Infos, l => l == "Sample: ui ember-stack 99");
        Assert.Contains(_log.Infos, l => l == "Sample: ui demo shown");
        Assert.Contains(_log.Infos, l => l.Contains("Ui: panel 'Sample'", StringComparison.Ordinal));
    }

    [Fact]
    public void Log_button_click_goes_through_the_slot_and_close_disposes()
    {
        SampleUi.Run(SampleUi.Kind.Demo);
        Assert.True(_client.Wired.Count >= 2);
        UiSlotTable.BootstrapDispatch(_client.Wired[0], (byte)UiSlotKind.Click, 0f, false, null);
        Assert.Contains(_log.Infos, l => l == "Sample: ui click log");

        SampleUi.Run(SampleUi.Kind.Close);
        Assert.Equal(0, SampleUi.LiveCount);
        Assert.Contains(_log.Infos, l => l == "Sample: ui demo closed");
    }

    [Fact]
    public void Toggle_change_logs_the_new_value()
    {
        SampleUi.Run(SampleUi.Kind.Demo);
        Assert.True(SampleUi.ToggleValue);
        ushort toggleId = 0;
        for (int i = 0; i < _client.Wired.Count; i++)
            toggleId = _client.Wired[i];
        UiSlotTable.BootstrapDispatch(toggleId, (byte)UiSlotKind.Bool, 0f, false, null);
        Assert.False(SampleUi.ToggleValue);
        Assert.Contains(_log.Infos, l => l == "Sample: ui toggle value=False");
    }
}
