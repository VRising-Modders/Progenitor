using System;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>Argument contract of the <c>keybind_press</c> bridge op (<see cref="KeybindPressOps"/>).</summary>
public class BridgeKeyPressOpTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Bind_with_plugin_is_a_bind_request()
    {
        var request = KeybindPressOps.Parse(Args("{\"bind\":\" ToggleFreeCamera \",\"plugin\":\"example.film\"}"));
        Assert.Equal("ToggleFreeCamera", request.Bind);
        Assert.Equal("example.film", request.Plugin);
        Assert.Null(request.Chord);
    }

    [Fact]
    public void Bind_without_plugin_leaves_the_plugin_null()
    {
        var request = KeybindPressOps.Parse(Args("{\"bind\":\"MarkShot\"}"));
        Assert.Equal("MarkShot", request.Bind);
        Assert.Null(request.Plugin);
    }

    [Fact]
    public void Chord_is_parsed_in_the_cfg_format()
    {
        var request = KeybindPressOps.Parse(Args("{\"chord\":\"F10 + Ctrl\"}"));
        Assert.Equal(new KeyChord(UiKey.F10, KeyModifiers.Ctrl), request.Chord);
        Assert.Null(request.Bind);
    }

    /// <summary>KEYS8: a mouse chord is pressed by its cfg text, Unity's KeyCode names included.</summary>
    [Theory]
    [InlineData("Mouse0 + Ctrl + Shift", UiKey.MouseLeft, KeyModifiers.Ctrl | KeyModifiers.Shift)]
    [InlineData("MouseRight + Alt", UiKey.MouseRight, KeyModifiers.Alt)]
    [InlineData("Mouse3", UiKey.MouseBack, KeyModifiers.None)]
    public void A_mouse_chord_is_a_chord_request(string text, UiKey key, KeyModifiers mods)
    {
        var request = KeybindPressOps.Parse(Args("{\"chord\":\"" + text + "\"}"));
        Assert.Equal(new KeyChord(key, mods), request.Chord);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"plugin\":\"example.film\"}")]
    [InlineData("{\"bind\":\"   \"}")]
    [InlineData("{\"bind\":7}")]
    [InlineData("{\"bind\":\"A\",\"chord\":\"F10\"}")]
    [InlineData("{\"plugin\":\"p\",\"chord\":\"F10\"}")]
    [InlineData("{\"chord\":\"NotAKey\"}")]
    [InlineData("{\"chord\":\"None\"}")]
    [InlineData("[]")]
    public void Missing_ambiguous_or_bad_arguments_are_refused(string json)
    {
        var e = Assert.Throws<ArgumentException>(() => KeybindPressOps.Parse(Args(json)));
        Assert.Contains("keybind_press", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_op_refuses_outside_a_client()
    {
        // The test build has no client process, the same branch a dedicated server takes.
        var e = Assert.Throws<InvalidOperationException>(() => KeybindPressOps.Run(Args("{\"chord\":\"F10\"}")));
        Assert.Equal(KeybindPressOps.ClientOnlyMessage, e.Message);
    }
}
