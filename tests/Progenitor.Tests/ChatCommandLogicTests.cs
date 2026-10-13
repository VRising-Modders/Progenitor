using Progenitor.Chat;
using Xunit;

namespace Progenitor.Tests;

/// <summary>CHAT2: <c>/r</c> and the whisper name completion, game-free.</summary>
public sealed class ChatCommandLogicTests
{
    private static readonly string[] Online = { "Mira", "Mordecai", "ash", "Tov", "Mirabel" };

    [Theory]
    [InlineData("/r hi there", "/reply hi there")]
    [InlineData("/R ok", "/reply ok")]
    [InlineData("/r ", "/reply ")]
    public void Slash_r_becomes_the_games_reply(string typed, string expected)
    {
        Assert.True(ChatCommandLogic.TryExpandReply(typed, out var line));
        Assert.Equal(expected, line);
    }

    [Theory]
    [InlineData("/r")]
    [InlineData("/reply hi")]
    [InlineData("/raid tonight")]
    [InlineData("hello /r x")]
    [InlineData("")]
    public void Other_lines_are_left_alone(string typed)
    {
        Assert.False(ChatCommandLogic.TryExpandReply(typed, out var line));
        Assert.Equal(typed, line);
    }

    [Theory]
    [InlineData("/w mi", "/w ", "mi")]
    [InlineData("/W ", "/W ", "")]
    [InlineData("/whisper Tov", "/whisper ", "Tov")]
    [InlineData("/w mi\t", "/w ", "mi")]
    public void A_name_being_typed_after_w_is_found(string typed, string command, string partial)
    {
        Assert.True(ChatCommandLogic.TryParseWhisperName(typed, out var c, out var p));
        Assert.Equal(command, c);
        Assert.Equal(partial, p);
    }

    [Theory]
    [InlineData("/w Mira hello")]
    [InlineData("/wave")]
    [InlineData("/w")]
    [InlineData("w mi")]
    public void Other_lines_are_not_a_name_being_typed(string typed) =>
        Assert.False(ChatCommandLogic.TryParseWhisperName(typed, out _, out _));

    [Fact]
    public void Matches_are_case_insensitive_sorted_and_skip_self()
    {
        Assert.Equal(new[] { "Mira", "Mirabel", "Mordecai" }, ChatCommandLogic.Matches("m", Online, self: null));
        Assert.Equal(new[] { "Mirabel", "Mordecai" }, ChatCommandLogic.Matches("M", Online, self: "mira"));
        Assert.Equal(new[] { "ash" }, ChatCommandLogic.Matches("AS", Online, null));
        Assert.Empty(ChatCommandLogic.Matches("z", Online, null));
        Assert.Equal(5, ChatCommandLogic.Matches("", Online, null).Count);
    }

    [Fact]
    public void Completion_fills_the_first_match_and_cycles_on_the_next_press()
    {
        var c = new ChatNameCompleter();
        string? line = c.Complete("/w mi", Online, null);
        Assert.Equal("/w Mira ", line);
        line = c.Complete(line, Online, null);
        Assert.Equal("/w Mirabel ", line);
        line = c.Complete(line, Online, null);
        Assert.Equal("/w Mira ", line);
    }

    [Fact]
    public void Typing_after_a_completion_starts_over()
    {
        var c = new ChatNameCompleter();
        Assert.Equal("/w Mira ", c.Complete("/w mi", Online, null));
        Assert.True(c.WouldAct("/w Mira "));
        // The player typed a message: no longer a name being completed.
        Assert.False(c.WouldAct("/w Mira hello"));
        Assert.Null(c.Complete("/w Mira hello", Online, null));
        // A fresh name.
        Assert.Equal("/whisper Tov ", c.Complete("/whisper t", Online, null));
    }

    [Fact]
    public void Would_complete_only_when_a_name_matches()
    {
        var c = new ChatNameCompleter();
        Assert.True(c.WouldComplete("/w mi", Online, null));
        Assert.False(c.WouldComplete("/w zz", Online, null));      // Tab keeps cycling the channel
        Assert.False(c.WouldComplete("hello", Online, null));
        Assert.Equal("/w Mira ", c.Complete("/w mi", Online, null));
        Assert.True(c.WouldComplete("/w Mira ", Online, null));     // cycling the last result
        Assert.False(c.WouldComplete("/w Mira ", Online, "Mirabel") && !c.WouldComplete("/w Mira ", Online, null));
    }

    [Fact]
    public void A_reset_forgets_the_last_result()
    {
        var c = new ChatNameCompleter();
        Assert.Equal("/w Mira ", c.Complete("/w mi", Online, null));
        c.Reset();
        // Retyping the same line later is not a cycle of the old partial.
        Assert.False(c.WouldAct("/w Mira "));
        Assert.Null(c.Complete("/w Mira ", Online, null));
    }

    [Fact]
    public void Reply_goes_to_the_game_reply_until_there_is_a_partner()
    {
        var w = new ChatWhisperMemory();
        Assert.Equal("/reply ", w.ReplyLine());
        Assert.True(ChatCommandLogic.TryExpandReply("/r hi", w.ReplyLine(), out var line));
        Assert.Equal("/reply hi", line);
    }

    [Fact]
    public void Reply_continues_a_whisper_you_sent_even_to_yourself()
    {
        var w = new ChatWhisperMemory();
        w.NoteDraft("/w TestPlayer hey");
        w.NoteDraft("");          // the game cleared the line on send
        w.NoteSent();             // the sent whisper ("Note to Self") appeared
        Assert.Equal("TestPlayer", w.Partner);
        Assert.True(ChatCommandLogic.TryExpandReply("/r again", w.ReplyLine(), out var line));
        Assert.Equal("/w TestPlayer again", line);
    }

    [Fact]
    public void Reply_follows_the_latest_conversation_in_either_direction()
    {
        var w = new ChatWhisperMemory();
        w.NoteDraft("/whisper Mira hello");
        w.NoteSent();
        Assert.Equal("Mira", w.Partner);
        w.NoteReceived("Tov");
        Assert.Equal("/w Tov ", w.ReplyLine());
        // Whisper mode (the game switched to it after "/w Ash ") names the target too.
        w.NoteWhisperMode("Ash");
        w.NoteSent();
        Assert.Equal("Ash", w.Partner);
    }

    [Theory]
    [InlineData("/w Mira hi", "Mira")]
    [InlineData("/W Mira ", "Mira")]
    [InlineData("/whisper Tov a b c", "Tov")]
    public void A_whisper_line_names_its_target(string text, string target)
    {
        Assert.True(ChatWhisperMemory.TryWhisperTarget(text, out var name));
        Assert.Equal(target, name);
    }

    [Theory]
    [InlineData("/w Mira")]
    [InlineData("/w ")]
    [InlineData("hello")]
    [InlineData("/wave hi")]
    public void An_unfinished_or_other_line_names_no_target(string text) =>
        Assert.False(ChatWhisperMemory.TryWhisperTarget(text, out _));

    [Fact]
    public void A_sent_whisper_with_no_known_target_keeps_the_partner()
    {
        var w = new ChatWhisperMemory();
        w.NoteReceived("Tov");
        w.NoteSent();
        Assert.Equal("Tov", w.Partner);
    }

    [Fact]
    public void No_match_changes_nothing()
    {
        var c = new ChatNameCompleter();
        Assert.Null(c.Complete("/w zz", Online, null));
        Assert.Null(c.Complete("hello", Online, null));
        Assert.True(c.WouldAct("/w zz")); // the key is still held back from cycling the channel while typing a name
    }
}
