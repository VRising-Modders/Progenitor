using System;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FEX1 (RD6 J2). Game-free coverage of the <c>ctx.Chat</c> example: the send guard, the
/// replies that carry the recipient count, and the observer's line format. The sends and the
/// <c>ChatMessage</c> subscription need a live server world and are behind
/// <c>!PROGENITOR_TESTS</c>.
/// </summary>
public class SampleChatTests
{
    [Fact]
    // Below the commands module's 500, or the observer reports handled=False for every command
    // line - which is the defect this constant exists to prevent.
    public void The_observer_runs_after_the_commands_module()
    {
        Assert.True(SampleChat.ObserverPriority < 500);
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("  hello  ", "hello")]
    [InlineData("a\tb", "a b")]
    [InlineData("a   b", "a b")]
    // A newline in chat-sourced text would otherwise forge a second log line.
    [InlineData("first\nSample chat: forged", "first Sample chat: forged")]
    public void Clean_folds_control_characters_and_whitespace(string? input, string expected)
    {
        Assert.Equal(expected, SampleChat.Clean(input));
    }

    [Fact]
    public void Elide_cuts_at_the_limit_and_marks_the_cut()
    {
        Assert.Equal("abc", SampleChat.Elide("abc", 5));
        Assert.Equal("abc…", SampleChat.Elide("abcdef", 3));
        Assert.Equal("", SampleChat.Elide("abc", 0));
    }

    [Fact]
    public void An_empty_send_is_refused()
    {
        Assert.False(SampleChat.TryPrepareSend("   ", out string prepared, out string? refusal));
        Assert.Equal("", prepared);
        Assert.Equal("Nothing to send: the text was empty.", refusal);
    }

    [Fact]
    // Refused rather than truncated: the caller has to learn their line did not go out whole.
    public void An_over_long_send_is_refused_not_truncated()
    {
        string text = new string('x', SampleChat.MaxSendChars + 1);
        Assert.False(SampleChat.TryPrepareSend(text, out string prepared, out string? refusal));
        Assert.Equal("", prepared);
        Assert.Contains("caps a send at 200", refusal);
    }

    [Fact]
    public void A_normal_line_is_prepared_clean()
    {
        Assert.True(SampleChat.TryPrepareSend("  hello   world ", out string prepared, out string? refusal));
        Assert.Equal("hello world", prepared);
        Assert.Null(refusal);
    }

    [Fact]
    public void The_shout_reply_carries_the_recipient_count()
    {
        Assert.Equal("Broadcast to 2 connected user(s), you included.", SampleChat.ShoutReply(2));
        Assert.Contains("reached nobody", SampleChat.ShoutReply(0));
    }

    [Fact]
    public void The_whisper_replies_name_the_target()
    {
        Assert.Equal("Sent to Rowan.", SampleChat.WhisperReply("Rowan", true));
        Assert.Contains("Could not send to Rowan", SampleChat.WhisperReply("Rowan", false));
        Assert.Contains("No connected player called 'Ghost'", SampleChat.NoSuchUserReply("Ghost"));
    }

    [Theory]
    [InlineData(0, "Global")]
    [InlineData(3, "Whisper")]
    [InlineData(4, "Local")]
    [InlineData(5, "System")]
    [InlineData(42, "42")]
    // An unknown value prints as its number rather than being guessed at.
    public void Chat_types_print_as_the_game_names_them(int type, string expected)
    {
        Assert.Equal(expected, SampleChat.TypeName(type));
    }

    [Fact]
    public void The_observed_line_reports_the_sender_and_the_cancel_verdict()
    {
        Assert.Equal(
            "saw type=Global from=Rowan handled=True text=\".ember 5\"",
            SampleChat.ObservedLine(0, "Rowan", handled: true, ".ember 5"));
        Assert.Equal(
            "saw type=Local from=<unknown> handled=False text=\"hi\"",
            SampleChat.ObservedLine(4, null, handled: false, "hi"));
    }

    [Fact]
    public void An_observed_line_elides_a_long_chat_line()
    {
        string text = new string('y', SampleChat.LogTextChars + 20);
        string line = SampleChat.ObservedLine(0, "Rowan", handled: false, text);
        Assert.Contains(new string('y', SampleChat.LogTextChars) + "…\"", line);
        Assert.DoesNotContain(new string('y', SampleChat.LogTextChars + 1), line);
    }

    [Fact]
    public void The_formatted_log_line_is_the_greppable_one()
    {
        Assert.Equal(
            "Sample chat: saw type=System from=Rowan handled=True text=\"x\"",
            SampleLog.Format(SampleChat.Area, SampleChat.ObservedLine(5, "Rowan", true, "x")));
    }
}
