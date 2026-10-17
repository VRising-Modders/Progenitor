using System;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>The Dev bridge <c>command</c> op's arguments (a line typed as a connected player), game-free.</summary>
public sealed class ChatInjectArgsTests
{
    private static ChatInjectRequest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ChatInjectArgs.Parse(doc.RootElement.Clone());
    }

    [Fact]
    public void A_plain_line_defaults_to_local_with_the_echo_and_the_first_connected_user()
    {
        var r = Parse("{\"text\":\"hello\"}");
        Assert.Equal("hello", r.Text);
        Assert.Equal(ChatInjectArgs.Local, r.MessageType);
        Assert.Equal("local", r.Channel);
        Assert.True(r.Echo);
        Assert.Null(r.UserIndex);
        Assert.Null(r.CharacterName);
    }

    [Theory]
    [InlineData("global", ChatInjectArgs.Global, "global")]
    [InlineData("Global", ChatInjectArgs.Global, "global")]
    [InlineData("clan", ChatInjectArgs.Team, "clan")]
    [InlineData("team", ChatInjectArgs.Team, "clan")]
    [InlineData("local", ChatInjectArgs.Local, "local")]
    public void Channels_map_to_the_game_types(string channel, int type, string reported)
    {
        var r = Parse("{\"text\":\"hi\",\"channel\":\"" + channel + "\"}");
        Assert.Equal(type, r.MessageType);
        Assert.Equal(reported, r.Channel);
    }

    [Theory]
    [InlineData("{\"text\":\"hi\",\"channel\":\"whisper\"}")]
    [InlineData("{\"text\":\"hi\",\"channel\":\"system\"}")]
    [InlineData("{\"text\":\"hi\",\"echo\":\"no\"}")]
    [InlineData("{\"text\":\"\"}")]
    [InlineData("{}")]
    public void Bad_arguments_are_refused(string json) =>
        Assert.Throws<ArgumentException>(() => Parse(json));

    [Fact]
    public void The_echo_can_be_turned_off()
    {
        Assert.False(Parse("{\"text\":\".joins\",\"echo\":false}").Echo);
        Assert.True(Parse("{\"text\":\".joins\",\"echo\":true}").Echo);
    }

    [Theory]
    [InlineData("{\"text\":\"x\",\"user\":\"Ghost1\"}", null, "Ghost1")]
    [InlineData("{\"text\":\"x\",\"user\":10001}", 10001, null)]
    [InlineData("{\"text\":\"x\",\"user\":\"10001\"}", 10001, null)]
    [InlineData("{\"text\":\"x\",\"userIndex\":3,\"user\":\"Ghost1\"}", 3, null)]
    [InlineData("{\"text\":\"x\",\"userIndex\":\"4\",\"user\":7}", 4, null)]
    [InlineData("{\"text\":\"x\",\"user\":\"  \"}", null, null)]
    public void The_speaker_is_an_index_or_a_name(string json, int? index, string? name)
    {
        var r = Parse(json);
        Assert.Equal(index, r.UserIndex);
        Assert.Equal(name, r.CharacterName);
    }
}
