using System;
using System.Linq;
using Progenitor.Chat;
using Xunit;

namespace Progenitor.Tests;

/// <summary>CHAT5: the chat log, tabs, the line formatter and mentions, game-free.</summary>
public sealed class ChatLogTests
{
    private static readonly DateTime T0 = new(2026, 10, 7, 20, 0, 0, DateTimeKind.Utc);

    private static ChatRecord Line(ChatKind kind, string user, string text, int seconds = 0, bool self = false) =>
        new() { Kind = kind, User = user, Text = text, TimeUtc = T0.AddSeconds(seconds), FromSelf = self };

    [Fact]
    public void Repeats_collapse_into_one_line_with_a_count()
    {
        var log = new ChatLog();
        log.Add(Line(ChatKind.System, "", "Server restarts in 10 minutes"), collapse: true);
        log.Add(Line(ChatKind.System, "", "Server restarts in 10 minutes", 5), collapse: true);
        var third = log.Add(Line(ChatKind.System, "", "Server restarts in 10 minutes", 9), collapse: true);
        Assert.Single(log.Records);
        Assert.Equal(3, third.Repeat);
        // A different line, or the same one after the window, is new.
        log.Add(Line(ChatKind.System, "", "Server restarts in 10 minutes", 200), collapse: true);
        Assert.Equal(2, log.Records.Count);
        // Collapse off keeps every line.
        log.Add(Line(ChatKind.System, "", "Server restarts in 10 minutes", 201), collapse: false);
        Assert.Equal(3, log.Records.Count);
    }

    [Fact]
    public void Capacity_drops_the_oldest()
    {
        var log = new ChatLog(ChatLog.MinCapacity);
        for (int i = 0; i < ChatLog.MinCapacity + 10; i++) log.Add(Line(ChatKind.Global, "A", "m" + i, i), collapse: true);
        Assert.Equal(ChatLog.MinCapacity, log.Records.Count);
        Assert.Equal("m10", log.Records[0].Text);
    }

    [Fact]
    public void Tabs_filter_by_channel_and_mentions_and_dividers_show_everywhere()
    {
        var log = new ChatLog();
        log.Add(Line(ChatKind.Global, "A", "hi", 1), true);
        log.Add(Line(ChatKind.Team, "B", "raid", 2), true);
        log.Add(Line(ChatKind.WhisperFrom, "C", "psst", 3), true);
        log.Add(new ChatRecord { Kind = ChatKind.Local, User = "D", Text = "hey TestPlayer", Mention = true, TimeUtc = T0.AddSeconds(4) }, true);
        log.AddMarker("Joined X · 20:00", T0.AddSeconds(5));

        Assert.Equal(5, log.Visible(ChatTab.All(), 100).Count);
        Assert.Equal(new[] { "raid", "Joined X · 20:00" }, log.Visible(ChatTab.Clan(), 100).Select(r => r.Text));
        var mentions = new ChatTab { Name = "Mentions", Scope = ChatScope.Mentions };
        Assert.Equal(new[] { "hey TestPlayer", "Joined X · 20:00" }, log.Visible(mentions, 100).Select(r => r.Text));
        // The newest lines, oldest first.
        Assert.Equal(new[] { "hey TestPlayer", "Joined X · 20:00" }, log.Visible(ChatTab.All(), 2).Select(r => r.Text));
    }

    [Fact]
    public void Tabs_save_and_load_with_All_first_and_bad_entries_skipped()
    {
        var tabs = ChatTab.Load(ChatTab.DefaultSaved);
        Assert.Equal(new[] { "All", "Clan" }, tabs.Select(t => t.Name));
        Assert.True(tabs[0].Fixed);
        var loaded = ChatTab.Load("Trade=1;All=31;=4;Bad=x;Empty=0;W<h>=8");
        Assert.Equal(new[] { "All", "Trade", "Wh" }, loaded.Select(t => t.Name));
        Assert.Equal("Trade=1;Wh=8", ChatTab.Save(loaded));
        Assert.Equal(2, ChatTab.Clan().SendChannel());
        Assert.Equal(-1, ChatTab.All().SendChannel());
    }

    [Fact]
    public void History_round_trips_and_restored_lines_never_mention()
    {
        var log = new ChatLog();
        log.Add(new ChatRecord { Kind = ChatKind.Local, User = "D", Text = "hi \"there\"\nnext", Mention = true, TimeUtc = T0 }, true);
        log.AddMarker("Left the server · 21:00", T0.AddMinutes(1));
        var back = ChatLog.Deserialize(log.Serialize(100) + "not json\n", 100);
        Assert.Equal(2, back.Count);
        Assert.Equal("hi \"there\"\nnext", back[0].Text);
        Assert.True(back[0].Restored);
        Assert.False(back[0].Mention);
        Assert.Equal(ChatKind.Session, back[1].Kind);

        var fresh = new ChatLog();
        fresh.Add(Line(ChatKind.Global, "A", "new"), true);
        fresh.Restore(back);
        Assert.Equal(new[] { "hi \"there\"\nnext", "Left the server · 21:00", "new" }, fresh.Records.Select(r => r.Text));
    }

    [Fact]
    public void File_keys_are_safe_and_distinct()
    {
        string a = ChatLog.FileKey("127.0.0.1:9976");
        string b = ChatLog.FileKey("127.0.0.1:9977");
        Assert.NotEqual(a, b);
        Assert.DoesNotContain(':', a);
        Assert.StartsWith("unknown-", ChatLog.FileKey(null));
    }

    [Fact]
    public void Render_keeps_the_games_prefix_and_escapes_the_message()
    {
        var r = new ChatRecord
        {
            Kind = ChatKind.Local, User = "A", Text = "see <b>https://example.com/x</b>",
            Full = "[Local] <color=#fff>A</color>: <noparse>see <b>https://example.com/x</b></noparse>",
        };
        string line = ChatFormat.Render(r, new ChatFormat.Options(), out var spans);
        Assert.StartsWith("[Local] <color=#fff>A</color>: ", line);
        Assert.Single(spans);
        Assert.Equal(ChatSpanKind.Url, spans[0].Kind);
        Assert.Contains("<link=\"u:0\">", line);
        Assert.DoesNotContain("<b>", line); // the typed tag is escaped, not applied
        Assert.Equal(spans[0], ChatFormat.SpanForLink("u:0", spans));
        Assert.Null(ChatFormat.SpanForLink("m:0", spans));
        Assert.Null(ChatFormat.SpanForLink("u:9", spans));
    }

    [Fact]
    public void Render_adds_timestamps_counts_mentions_and_dims_restored_lines()
    {
        var r = new ChatRecord { Kind = ChatKind.Global, User = "A", Text = "hi", Repeat = 3, Mention = true, TimeUtc = T0 };
        string line = ChatFormat.Render(r, new ChatFormat.Options { Timestamps = true }, out _, new DateTime(2026, 10, 7, 16, 5, 0));
        Assert.StartsWith("<mark=", line);
        Assert.Contains("[16:05]", line);
        Assert.Contains("[Global] A: hi", line);
        Assert.Contains("×3", line);

        // The game's own timestamp is not doubled.
        var g = new ChatRecord { Kind = ChatKind.Global, User = "A", Text = "hi", Full = "[12:50] [Global] A: <noparse>hi</noparse>", TimeUtc = T0 };
        string gl = ChatFormat.Render(g, new ChatFormat.Options { Timestamps = true }, out _);
        Assert.Equal(1, CountOf(gl, "12:50") + CountOf(gl, ":0"));

        r.Restored = true;
        Assert.StartsWith("<alpha=", ChatFormat.Render(r, new ChatFormat.Options(), out _));
        Assert.Equal("— Joined X —", ChatFormat.PlainText(new ChatRecord { Kind = ChatKind.Session, Text = "Joined X" }));
    }

    private static int CountOf(string s, string part)
    {
        int n = 0, at = 0;
        while ((at = s.IndexOf(part, at, StringComparison.Ordinal)) >= 0) { n++; at += part.Length; }
        return n;
    }

    [Fact]
    public void Mod_tokens_become_mod_links_when_a_finder_is_installed()
    {
        var old = ChatFormat.ModTokenFinder;
        try
        {
            ChatFormat.ModTokenFinder = t =>
            {
                int i = t.IndexOf("[mod:", StringComparison.Ordinal);
                return i < 0 ? Array.Empty<(int, int, string, string?)>() : new[] { (i, t.IndexOf(']', i) - i + 1, "Author-Mod", (string?)"1.2.0") };
            };
            var spans = ChatFormat.Spans("try [mod:Author-Mod@1.2.0] now", new ChatFormat.Options());
            Assert.Single(spans);
            Assert.Equal(ChatSpanKind.Mod, spans[0].Kind);
            Assert.Equal("Author-Mod@1.2.0", spans[0].Target);
            Assert.Empty(ChatFormat.Spans("try [mod:Author-Mod] now", new ChatFormat.Options { ModLinks = false }));
        }
        finally { ChatFormat.ModTokenFinder = old; }
    }

    [Theory]
    [InlineData("hey TestPlayer!", "TestPlayer", false, false, true)]
    [InlineData("testplayer, look", "TestPlayer", false, false, true)]
    [InlineData("TestPlayers unite", "TestPlayer", false, false, false)]
    [InlineData("@all raid at 9", "", true, true, true)]
    [InlineData("@all raid at 9", "", false, true, false)]
    [InlineData("@all raid at 9", "", true, false, false)]
    [InlineData("@allies only", "", true, true, false)]
    public void Mentions_are_whole_words_and_all_needs_an_admin(string text, string own, bool admin, bool allowAll, bool expected) =>
        Assert.Equal(expected, ChatMentions.IsMention(text, own, null, senderIsAdmin: admin, allowAtAll: allowAll));

    [Fact]
    public void Your_own_lines_can_mention_you_and_watch_words_count()
    {
        Assert.True(ChatMentions.IsMention("TestPlayer here", "TestPlayer", null, false, true));
        var words = ChatMentions.ParseWords("raid, boss  x");
        Assert.Equal(new[] { "raid", "boss" }, words);
        Assert.True(ChatMentions.IsMention("Boss is up", "", words, false, true));
    }
}

/// <summary>CHAT7: sent-line recall and the client mute list, game-free.</summary>
public sealed class ChatRecallTests
{
    [Fact]
    public void Up_goes_back_and_down_returns_to_the_draft()
    {
        var r = new ChatRecall();
        r.Push("first");
        r.Push(".castle info");
        r.Push(".castle info"); // a repeat is stored once
        Assert.Equal(2, r.Lines.Count);
        Assert.Equal(".castle info", r.Previous("half typ"));
        Assert.Equal("first", r.Previous("ignored"));
        Assert.Null(r.Previous("x")); // nothing older
        Assert.Equal(".castle info", r.Next());
        Assert.Equal("half typ", r.Next()); // back to what was being typed
        Assert.Null(r.Next());
    }

    [Fact]
    public void Recall_keeps_at_most_fifty_and_skips_blank_lines()
    {
        var r = new ChatRecall();
        for (int i = 0; i < 60; i++) r.Push("line " + i);
        r.Push("   ");
        Assert.Equal(ChatRecall.MaxLines, r.Lines.Count);
        Assert.Equal("line 10", r.Lines[0]);
        var loaded = new ChatRecall();
        loaded.Load(r.Lines);
        Assert.Equal("line 59", loaded.Previous(""));
    }

    [Fact]
    public void Mute_list_toggles_ignoring_case()
    {
        string s = ChatMute.Toggle("", "Mira");
        Assert.Equal("Mira", s);
        s = ChatMute.Toggle(s, "Tov");
        Assert.True(ChatMute.Contains(ChatMute.Parse(s), "tov"));
        s = ChatMute.Toggle(s, "MIRA");
        Assert.Equal("Tov", s);
        Assert.False(ChatMute.Contains(ChatMute.Parse(s), ""));
    }
}

/// <summary>CHAT8/CHAT9: location and item tokens, their input expansion, and long messages, game-free.</summary>
public sealed class ChatTokenTests
{
    [Fact]
    public void Location_and_item_tokens_are_found_strictly()
    {
        var found = ChatTokens.Find("meet [loc:-1804,-1697] with [item:-2128818978] not [loc:1,x] [item:abc] [other:1]");
        Assert.Equal(2, found.Count);
        Assert.Equal(ChatSpanKind.Location, found[0].Kind);
        Assert.Equal("-1804,-1697", found[0].Target);
        Assert.Equal(ChatSpanKind.Item, found[1].Kind);
        Assert.Equal("-2128818978", found[1].Target);
        Assert.Equal("[loc:-1805,12]", ChatTokens.Location(-1804.6f, 12.2f));
    }

    [Fact]
    public void Tokens_render_as_links_with_their_display()
    {
        var old = ChatFormat.ItemNameResolver;
        try
        {
            ChatFormat.ItemNameResolver = id => id == 42 ? "Bone Sword" : null;
            var r = new ChatRecord { Kind = ChatKind.Global, User = "A", Text = "at [loc:10,-20] got [item:42] and [item:7]" };
            string line = ChatFormat.Render(r, new ChatFormat.Options(), out var spans);
            Assert.Equal(3, spans.Count);
            Assert.Contains("<link=\"l:0\">", line);
            Assert.Contains("[10, -20]", line);
            Assert.Contains("[Bone Sword]", line);
            Assert.Contains("[item:7]", line); // an unknown item shows as written
            Assert.Equal(ChatSpanKind.Item, ChatFormat.SpanForLink("i:1", spans)!.Value.Kind);
        }
        finally { ChatFormat.ItemNameResolver = old; }
    }

    [Theory]
    [InlineData(0f, 10f, "north")]
    [InlineData(10f, 0f, "east")]
    [InlineData(-10f, -10f, "south-west")]
    [InlineData(0.1f, 0.1f, "here")]
    public void Directions_follow_the_compass(float dx, float dz, string expected) =>
        Assert.Equal(expected, ChatTokens.Direction(dx, dz));

    [Fact]
    public void Loc_and_item_commands_expand_at_the_start_of_the_line()
    {
        var oldPos = ChatInputTokens.Position;
        var oldItem = ChatInputTokens.ItemIdByPrefabName;
        try
        {
            ChatInputTokens.Position = () => (true, 100.4f, -50f);
            ChatInputTokens.ItemIdByPrefabName = n => n == "Item_Weapon_Sword_T01_Bone" ? 42 : 0;
            Assert.True(ChatInputTokens.TryExpand("/loc meet here", out var a));
            Assert.Equal("[loc:100,-50] meet here", a);
            Assert.True(ChatInputTokens.TryExpand("/item Item_Weapon_Sword_T01_Bone for sale", out var b));
            Assert.Equal("[item:42] for sale", b);
            Assert.False(ChatInputTokens.TryExpand("/item Unknown_Thing x", out _));
            Assert.False(ChatInputTokens.TryExpand("/item Item_Weapon_Sword_T01_Bone", out _)); // name not finished yet
            Assert.False(ChatInputTokens.TryExpand("say /loc", out _));
            ChatInputTokens.Position = () => (false, 0f, 0f);
            Assert.False(ChatInputTokens.TryExpand("/loc ", out _));
        }
        finally
        {
            ChatInputTokens.Position = oldPos;
            ChatInputTokens.ItemIdByPrefabName = oldItem;
        }
    }

    [Fact]
    public void Long_messages_split_under_the_byte_limit_and_join_back()
    {
        string text = string.Join(" ", System.Linq.Enumerable.Repeat("wordy", 300)); // about 1800 characters
        Assert.True(ChatLongMessage.IsLong(text));
        var parts = ChatLongMessage.Split(text, 4000);
        Assert.True(parts.Count >= 4);
        Assert.All(parts, p => Assert.True(ChatLongMessage.Bytes(p) <= ChatLongMessage.PartBytes));
        Assert.EndsWith(ChatLongMessage.MoreMark, parts[0]);
        Assert.StartsWith(ChatLongMessage.ContinueMark, parts[1]);
        Assert.False(ChatLongMessage.ExpectsMore(parts[parts.Count - 1]));

        var log = new ChatLog();
        var t = new System.DateTime(2026, 10, 7, 20, 0, 0, System.DateTimeKind.Utc);
        log.Add(new ChatRecord { Kind = ChatKind.Global, User = "A", Text = parts[0], TimeUtc = t }, true);
        for (int i = 1; i < parts.Count; i++)
        {
            var part = new ChatRecord { Kind = ChatKind.Global, User = "A", Text = parts[i], TimeUtc = t.AddSeconds(i) };
            Assert.NotNull(log.TryJoin(part));
        }

        Assert.Single(log.Records);
        Assert.Equal(text, log.Records[0].Text);
        // Another speaker's continuation, or one after the window, is a line of its own.
        log.Add(new ChatRecord { Kind = ChatKind.Global, User = "A", Text = "first" + ChatLongMessage.MoreMark, TimeUtc = t.AddSeconds(100) }, true);
        Assert.Null(log.TryJoin(new ChatRecord { Kind = ChatKind.Global, User = "B", Text = "↪ x", TimeUtc = t.AddSeconds(101) }));
        Assert.Null(log.TryJoin(new ChatRecord { Kind = ChatKind.Global, User = "A", Text = "↪ x", TimeUtc = t.AddSeconds(200) }));
    }

    [Fact]
    public void Multibyte_text_splits_on_character_boundaries()
    {
        string text = new string('é', 600) + " " + new string('字', 300);
        var parts = ChatLongMessage.Split(text, 4000);
        Assert.All(parts, p => Assert.True(ChatLongMessage.Bytes(p) <= ChatLongMessage.PartBytes));
        Assert.Equal(text.Replace(" ", ""), string.Concat(parts).Replace(ChatLongMessage.MoreMark, "").Replace(ChatLongMessage.ContinueMark, "").Replace(" ", ""));
    }
}

/// <summary>Review fix: server and mod System lines keep their own rich text, as the game draws them.</summary>
public sealed class ChatRichLineTests
{
    [Fact]
    public void A_system_line_keeps_its_color_and_still_gets_links()
    {
        var r = new ChatRecord
        {
            Kind = ChatKind.System,
            Text = "<color=#ff0>Teleported</color> see https://example.com/x",
            Full = "[SYSTEM] <color=#ff0>Teleported</color> see https://example.com/x",
        };
        string line = ChatFormat.Render(r, new ChatFormat.Options(), out var spans);
        Assert.Contains("<color=#ff0>Teleported</color>", line);
        Assert.DoesNotContain("&lt;", line);
        Assert.Single(spans);
        Assert.Contains("<link=\"u:0\">", line);
        Assert.Equal("[SYSTEM] Teleported see https://example.com/x", ChatFormat.PlainText(r));
    }

    [Fact]
    public void A_link_inside_a_tag_is_not_wrapped()
    {
        var r = new ChatRecord { Kind = ChatKind.System, Text = "x", Full = "<link=\"https://example.com/a\">click</link> here" };
        ChatFormat.Render(r, new ChatFormat.Options(), out var spans);
        Assert.Empty(spans);
    }

    [Fact]
    public void A_player_line_is_still_escaped()
    {
        var r = new ChatRecord { Kind = ChatKind.Global, User = "A", Text = "<b>hi</b>", Full = "[Global] A: <noparse><b>hi</b></noparse>" };
        string line = ChatFormat.Render(r, new ChatFormat.Options(), out _);
        Assert.DoesNotContain("<b>hi", line);
    }
}
