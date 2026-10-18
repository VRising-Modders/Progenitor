using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// MASKED1: the masked text input. The rules every password field follows (<see cref="UiMaskedText"/>: asterisks in a
/// readback, no copy or cut, no undo history) and the panel API (<see cref="IUiInput.Masked"/>). The dialog half (a
/// secret prompt and a masked form) is in <see cref="DialogTests"/>.
/// </summary>
[Collection("PluginLogger")]
public sealed class UiMaskedInputTests : System.IDisposable
{
    private readonly TestLogSource _log = new();
    private readonly UiTests.FakeUiRuntime _client = new() { IsClient = true };
    private readonly object _owner = new();

    public UiMaskedInputTests()
    {
        Plugin.Logger = _log;
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_client);
    }

    public void Dispose()
    {
        UiApi.ResetForTests();
        Plugin.Logger = null;
    }

    [Fact]
    public void A_masked_readback_is_one_asterisk_per_character_and_never_the_text()
    {
        Assert.Equal("*******", UiMaskedText.Shown("hunter2", masked: true));
        Assert.Equal("", UiMaskedText.Shown(null, masked: true));
        Assert.Equal("hunter2", UiMaskedText.Shown("hunter2", masked: false));
        Assert.Equal("", UiMaskedText.Shown(null, masked: false));
        Assert.DoesNotContain("h", UiMaskedText.Shown("hhh", masked: true));
    }

    [Theory]
    [InlineData('c', false)]
    [InlineData('C', false)]
    [InlineData('x', false)]
    [InlineData('X', false)]
    [InlineData('v', true)]
    [InlineData('a', true)]
    [InlineData('z', true)]
    public void A_masked_field_never_puts_its_text_on_the_clipboard(char letter, bool allowed)
    {
        Assert.Equal(allowed, UiMaskedText.ClipboardAllowed(letter, masked: true));
        Assert.True(UiMaskedText.ClipboardAllowed(letter, masked: false));
    }

    [Fact]
    public void A_masked_field_keeps_no_undo_history()
    {
        Assert.False(UiMaskedText.KeepsHistory(masked: true));
        Assert.True(UiMaskedText.KeepsHistory(masked: false));
    }

    [Fact]
    public void Masked_on_a_panel_input_switches_the_field_and_Value_still_returns_the_text()
    {
        var panel = UiApi.Panel("Lock", _owner, "example.masked.test");
        var password = panel.Input("Password");
        var name = panel.Input("Name", "ember");
        Assert.False(password.Masked);
        password.Masked = true;
        Assert.True(password.Masked);
        Assert.True(_client.MaskedOf("Password"));
        Assert.False(_client.MaskedOf("Name"));
        password.Value = "hunter2";
        Assert.Equal("hunter2", password.Value);
        password.Masked = false;
        Assert.False(_client.MaskedOf("Password"));
        Assert.False(name.Masked);
        panel.Dispose();
    }

    [Fact]
    public void Masked_after_dispose_does_not_throw()
    {
        var panel = UiApi.Panel("Lock", _owner, "example.masked.test");
        var password = panel.Input("Password");
        panel.Dispose();
        password.Masked = true;
        Assert.True(password.Masked);
    }
}
