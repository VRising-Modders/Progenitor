using System;
using System.Collections.Generic;
using Progenitor.Chat;
using Xunit;

namespace Progenitor.Tests;

/// <summary>CHAT14: the "sent elsewhere" note and tab pulse for a line sent from a tab that does not show it, game-free.</summary>
public sealed class ChatSentElsewhereTests
{
    private static ChatRecord Sent(ChatKind kind, string text = "hi") =>
        new() { Kind = kind, FromSelf = true, User = "Me", Text = text, TimeUtc = DateTime.UtcNow };

    private static List<ChatTab> Row(params ChatTab[] tabs) => new(tabs);

    private static ChatTab Whispers() => new() { Name = "Whispers", Scope = ChatScope.Whispers };

    // ---- which tab shows it -----------------------------------------------------------------------------------

    [Fact]
    public void The_most_specific_tab_is_named_before_All()
    {
        var tabs = Row(ChatTab.All(), ChatTab.Clan(), Whispers());
        Assert.Equal(2, ChatSentElsewhere.TargetTab(tabs, 1, Sent(ChatKind.WhisperTo)));
    }

    [Fact]
    public void All_is_named_when_no_other_tab_shows_the_channel()
    {
        var tabs = Row(ChatTab.All(), ChatTab.Clan(), Whispers());
        Assert.Equal(0, ChatSentElsewhere.TargetTab(tabs, 1, Sent(ChatKind.Global)));
    }

    [Fact]
    public void A_tie_names_the_first_tab_in_the_row_and_the_shown_tab_is_never_named()
    {
        var mixed = new ChatTab { Name = "Talk", Scope = ChatScope.Global | ChatScope.Whispers };
        var trade = new ChatTab { Name = "Trade", Scope = ChatScope.Global | ChatScope.Local };
        var tabs = Row(ChatTab.All(), ChatTab.Clan(), mixed, trade);
        Assert.Equal(2, ChatSentElsewhere.TargetTab(tabs, 1, Sent(ChatKind.Global)));
        Assert.Equal(3, ChatSentElsewhere.TargetTab(tabs, 2, Sent(ChatKind.Global)));
    }

    [Fact]
    public void No_tab_shows_it_gives_minus_one()
    {
        var tabs = Row(ChatTab.Clan(), Whispers());
        Assert.Equal(-1, ChatSentElsewhere.TargetTab(tabs, 0, Sent(ChatKind.Global)));
    }

    // ---- the note's words -------------------------------------------------------------------------------------

    [Fact]
    public void Hint_text_names_the_whisper_target_or_the_channel_and_the_tab()
    {
        Assert.Equal("Whisper to Bob sent · see Whispers", ChatSentElsewhere.Hint(Sent(ChatKind.WhisperTo), "Bob", "Whispers"));
        Assert.Equal("Whisper sent · see Whispers", ChatSentElsewhere.Hint(Sent(ChatKind.WhisperTo), "", "Whispers"));
        Assert.Equal("Sent to Global · see All", ChatSentElsewhere.Hint(Sent(ChatKind.Global), null, "All"));
        Assert.Equal("Sent to Clan · see Clan", ChatSentElsewhere.Hint(Sent(ChatKind.Team), null, "Clan"));
        Assert.Equal("Sent to Local · see All", ChatSentElsewhere.Hint(Sent(ChatKind.Local), null, "All"));
        Assert.Equal("no tab shows Global", ChatSentElsewhere.Hint(Sent(ChatKind.Global), null, null));
        Assert.Equal("no tab shows whispers", ChatSentElsewhere.Hint(Sent(ChatKind.WhisperTo), "Bob", ""));
    }

    // ---- when a note starts -----------------------------------------------------------------------------------

    [Fact]
    public void A_whisper_sent_from_Clan_notes_the_Whispers_tab_and_pulses_it()
    {
        var tabs = Row(ChatTab.All(), ChatTab.Clan(), Whispers());
        var sent = new ChatSentElsewhere();
        var note = sent.NoteSent(Sent(ChatKind.WhisperTo), tabs, 1, "Bob", now: 10);
        Assert.NotNull(note);
        Assert.Same(tabs[2], note!.Target);
        Assert.Same(tabs[1], note.From);
        Assert.False(note.Echo);
        Assert.Equal("Whisper to Bob sent · see Whispers", note.Text);

        Assert.True(sent.PulseLit(tabs[2], 10.1));
        Assert.False(sent.PulseLit(tabs[2], 10.45));
        Assert.True(sent.PulseLit(tabs[2], 10.7));
        Assert.False(sent.PulseLit(tabs[2], 10 + ChatSentElsewhere.PulseSeconds + 0.01));
        Assert.False(sent.PulseLit(tabs[0], 10.1)); // only the named tab pulses
    }

    [Fact]
    public void Nothing_is_noted_when_the_shown_tab_shows_the_line_or_it_is_not_yours()
    {
        var tabs = Row(ChatTab.All(), ChatTab.Clan(), Whispers());
        var sent = new ChatSentElsewhere();
        Assert.Null(sent.NoteSent(Sent(ChatKind.Team), tabs, 1, null, 0));
        Assert.Null(sent.NoteSent(Sent(ChatKind.WhisperTo), tabs, 0, "Bob", 0));
        var incoming = Sent(ChatKind.WhisperFrom);
        incoming.FromSelf = false;
        Assert.Null(sent.NoteSent(incoming, tabs, 1, null, 0));
        var restored = Sent(ChatKind.Global);
        restored.Restored = true;
        Assert.Null(sent.NoteSent(restored, tabs, 1, null, 0));
        Assert.Null(sent.Current);
    }

    [Fact]
    public void With_no_tab_showing_the_channel_the_note_echoes_the_line()
    {
        var tabs = Row(ChatTab.Clan(), Whispers());
        var sent = new ChatSentElsewhere();
        var line = Sent(ChatKind.Global, "anyone selling iron?");
        var note = sent.NoteSent(line, tabs, 0, null, 0);
        Assert.NotNull(note);
        Assert.True(note!.Echo);
        Assert.Null(note.Target);
        Assert.Same(line, note.Line);
        string rich = ChatSentElsewhere.Rich(note, new ChatFormat.Options());
        Assert.Contains("anyone selling iron?", rich);
        Assert.Contains("no tab shows Global", rich);
    }

    // ---- showing, fading, never saved --------------------------------------------------------------------------

    [Fact]
    public void The_note_shows_only_in_the_tab_it_was_sent_from_and_fades_out()
    {
        var tabs = Row(ChatTab.All(), ChatTab.Clan(), Whispers());
        var sent = new ChatSentElsewhere();
        var note = sent.NoteSent(Sent(ChatKind.WhisperTo), tabs, 1, "Bob", 100)!;
        Assert.Same(note, sent.Visible(tabs[1], 101));
        Assert.Null(sent.Visible(tabs[2], 101));
        Assert.Equal(1f, ChatSentElsewhere.Alpha(note, 101));
        float fading = ChatSentElsewhere.Alpha(note, 100 + ChatSentElsewhere.NoteSeconds - ChatSentElsewhere.FadeSeconds / 2);
        Assert.InRange(fading, 0.4f, 0.6f);
        Assert.Null(sent.Visible(tabs[1], 100 + ChatSentElsewhere.NoteSeconds));
        Assert.Null(sent.Current);
    }

    [Fact]
    public void Clear_forgets_the_note_and_the_pulse()
    {
        var tabs = Row(ChatTab.All(), ChatTab.Clan(), Whispers());
        var sent = new ChatSentElsewhere();
        sent.NoteSent(Sent(ChatKind.WhisperTo), tabs, 1, "Bob", 0);
        int v = sent.Version;
        sent.Clear();
        Assert.Null(sent.Current);
        Assert.False(sent.PulseLit(tabs[2], 0.1));
        Assert.True(sent.Version > v);
    }

    [Fact]
    public void The_note_is_never_saved_and_the_line_stays_in_its_own_channel()
    {
        var log = new ChatLog();
        var tabs = Row(ChatTab.All(), ChatTab.Clan(), Whispers());
        var line = log.Add(Sent(ChatKind.WhisperTo, "meet at the castle"), collapse: true);
        var sent = new ChatSentElsewhere();
        var note = sent.NoteSent(line, tabs, 1, "Bob", 0);
        Assert.NotNull(note);

        Assert.Single(log.Records);
        string saved = log.Serialize(100);
        Assert.DoesNotContain("see Whispers", saved);
        Assert.Contains("meet at the castle", saved);
        Assert.Empty(log.Visible(tabs[1], 50));   // the Clan tab never gets the whisper
        Assert.Single(log.Visible(tabs[2], 50));  // the Whispers tab has it
    }

    [Fact]
    public void Player_text_in_the_note_stays_literal()
    {
        var tabs = Row(ChatTab.All(), ChatTab.Clan(), Whispers());
        var sent = new ChatSentElsewhere();
        var note = sent.NoteSent(Sent(ChatKind.WhisperTo), tabs, 1, "<b>Bob</b>", 0)!;
        string rich = ChatSentElsewhere.Rich(note, new ChatFormat.Options());
        Assert.DoesNotContain("<b>Bob</b>", rich);
    }
}
