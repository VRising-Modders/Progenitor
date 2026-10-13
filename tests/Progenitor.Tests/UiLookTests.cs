using System;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// <see cref="UiLook"/>: the public way a mod asks for the game's button and a colored label, on a floating window or
/// a Mods page. Game-free.
/// </summary>
[Collection("PluginLogger")]
public sealed class UiLookTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();

    public UiLookTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
    }

    public void Dispose()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
    }

    [Fact]
    public void GameStyle_asks_for_the_game_button_and_chains()
    {
        using var panel = UiApi.Panel("Window", new object(), "example");
        var plain = panel.Button("Plain");
        var styled = panel.Button("Apply");

        Assert.Same(styled, styled.GameStyle());
        Assert.Equal(UiSettingsGrid.GameButtonStyle, styled.ButtonArt());
        Assert.Equal(UiSettingsGrid.KitButtonStyle, plain.ButtonArt());
    }

    [Fact]
    public void GameStyle_on_a_disposed_button_does_nothing()
    {
        using var panel = UiApi.Panel("Window", new object(), "example");
        var button = panel.Button("Apply");
        button.Dispose();
        Assert.Same(button, button.GameStyle());
    }

    [Fact]
    public void GameStyle_of_null_says_so() =>
        Assert.Throws<ArgumentNullException>(() => UiLook.GameStyle(null!));

    [Fact]
    public void A_tone_label_keeps_and_changes_its_tone()
    {
        using var panel = UiApi.Panel("Window", new object(), "example");
        var status = panel.Label("Running", LabelTone.Ok);
        Assert.Equal(LabelTone.Ok, status.Tone);

        status.SetText("Stopped", LabelTone.Danger);
        Assert.Equal(LabelTone.Danger, status.Tone);
        Assert.Equal("danger", status.Describe()["tone"]);
    }
}
