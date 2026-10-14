using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>The <c>console</c> bridge op's game-free half (<see cref="ConsolePlan"/>): arguments, the command word, the output lines.</summary>
public class BridgeConsoleTests
{
    private static ConsolePlan.Request Parse(string json) => ConsolePlan.Parse(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void A_command_line_defaults_to_echo_and_a_one_second_wait()
    {
        var r = Parse("{\"text\":\"  adminauth  \"}");
        Assert.Equal("adminauth", r.Text);
        Assert.Equal("adminauth", r.Command);
        Assert.Equal(ConsolePlan.DefaultWaitMs, r.WaitMs);
        Assert.True(r.Echo);
    }

    [Fact]
    public void Arguments_stay_in_the_text_and_the_command_is_the_first_word()
    {
        var r = Parse("{\"text\":\"ClanInvite Some Player\",\"waitMs\":0,\"echo\":false}");
        Assert.Equal("ClanInvite Some Player", r.Text);
        Assert.Equal("ClanInvite", r.Command);
        Assert.Equal(0, r.WaitMs);
        Assert.False(r.Echo);
        Assert.Equal("Kill", ConsolePlan.CommandName("Kill\tSelf"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"text\":\"\"}")]
    [InlineData("{\"text\":\"   \"}")]
    [InlineData("{\"text\":5}")]
    [InlineData("{\"text\":\"a\\nb\"}")]
    [InlineData("{\"text\":\"a\",\"waitMs\":10001}")]
    [InlineData("{\"text\":\"a\",\"waitMs\":-1}")]
    [InlineData("{\"text\":\"a\",\"echo\":\"yes\"}")]
    [InlineData("[]")]
    public void Bad_arguments_are_refused(string json) =>
        Assert.Throws<ArgumentException>(() => Parse(json));

    [Fact]
    public void A_too_long_line_is_refused() =>
        Assert.Throws<ArgumentException>(() => Parse("{\"text\":\"" + new string('x', ConsolePlan.MaxTextLength + 1) + "\"}"));

    [Theory]
    [InlineData("<color=#FF0000>COMMAND NOT FOUND></color> foo", "COMMAND NOT FOUND> foo")]
    [InlineData("> adminauth", "> adminauth")]
    [InlineData("<b>bold</b> and 1 < 2 > 0", "bold and 1 < 2 > 0")]
    [InlineData("a <not a tag> b", "a <not a tag> b")]
    [InlineData("trailing   ", "trailing")]
    [InlineData(null, "")]
    public void Rows_lose_their_rich_text_tags(string? row, string expected) =>
        Assert.Equal(expected, ConsolePlan.Plain(row));

    [Fact]
    public void Lines_drop_empty_rows_keep_the_newest_and_spot_an_unknown_command()
    {
        var rows = new List<string?> { "", "> nope", "<color=#ff0000>COMMAND NOT FOUND></color> nope", null };
        var lines = ConsolePlan.Lines(rows, out bool truncated);
        Assert.False(truncated);
        Assert.Equal(new[] { "> nope", "COMMAND NOT FOUND> nope" }, lines);
        Assert.True(ConsolePlan.SaysNotFound(lines));
        Assert.False(ConsolePlan.SaysNotFound(new[] { "> adminauth" }));

        var many = new List<string?>();
        for (int i = 0; i < ConsolePlan.MaxLines + 5; i++) many.Add("line " + i);
        var kept = ConsolePlan.Lines(many, out truncated);
        Assert.True(truncated);
        Assert.Equal(ConsolePlan.MaxLines, kept.Count);
        Assert.Equal("line " + (ConsolePlan.MaxLines + 4), kept[kept.Count - 1]);
    }

    [Fact]
    public void New_messages_start_after_the_last_one_seen_before_the_command()
    {
        IntPtr a = new(1), b = new(2), c = new(3), d = new(4);
        Assert.Equal(2, ConsolePlan.NewStart(new[] { a, b, c, d }, b));
        Assert.Equal(4, ConsolePlan.NewStart(new[] { a, b, c, d }, d));
        Assert.Equal(0, ConsolePlan.NewStart(new[] { a, b }, IntPtr.Zero));
        // The message seen before was dropped from the front of the list: everything left is newer.
        Assert.Equal(0, ConsolePlan.NewStart(new[] { c, d }, b));
    }
}
