using System.Collections.Generic;
using System.Linq;
using Progenitor.Chat;
using Xunit;

namespace Progenitor.Tests;

/// <summary>CHAT13: the input line per tab (send channel, whisper line, drafts, sent lines) and its saved form.</summary>
public sealed class ChatTabInputTests
{
    private static ChatTab Tab(string name, ChatScope scope) => new() { Name = name, Scope = scope };

    private const ChatScope GlobalClan = ChatScope.Global | ChatScope.Clan;

    [Fact]
    public void Default_channel_is_the_narrowest_the_tab_shows()
    {
        Assert.Equal(ChatSend.Clan, ChatTabInput.DefaultFor(Tab("Clan", ChatScope.Clan)));
        Assert.Equal(ChatSend.Local, ChatTabInput.DefaultFor(Tab("Local", ChatScope.Local)));
        Assert.Equal(ChatSend.Global, ChatTabInput.DefaultFor(Tab("Trade", ChatScope.Global)));
        // Mixed: never the whole server when a smaller audience is shown.
        Assert.Equal(ChatSend.Clan, ChatTabInput.DefaultFor(Tab("Mix", GlobalClan)));
        Assert.Equal(ChatSend.Local, ChatTabInput.DefaultFor(Tab("Mix", ChatScope.Global | ChatScope.Local | ChatScope.Mentions)));
        Assert.Equal(ChatSend.Clan, ChatTabInput.DefaultFor(Tab("Mix", ChatScope.Clan | ChatScope.Whispers)));
        // Whispers with no other sendable channel: whisper the partner.
        Assert.Equal(ChatSend.Whisper, ChatTabInput.DefaultFor(Tab("Whispers", ChatScope.Whispers)));
        Assert.Equal(ChatSend.Whisper, ChatTabInput.DefaultFor(Tab("W", ChatScope.Whispers | ChatScope.Mentions | ChatScope.SystemAndMods)));
        // Nothing to type to: keep the channel in use.
        Assert.Null(ChatTabInput.DefaultFor(Tab("Mentions", ChatScope.Mentions)));
        Assert.Null(ChatTabInput.DefaultFor(Tab("System", ChatScope.SystemAndMods)));
        Assert.Null(ChatTabInput.DefaultFor(ChatTab.All()));
        Assert.Equal(-1, ChatTab.All().SendChannel());
        Assert.Equal(2, ChatTab.Clan().SendChannel());
    }

    // Review item 3: a whispers tab never sends to Global by default.
    [Fact]
    public void Whisper_tab_prefills_the_partner_over_Local_and_falls_back_to_Local()
    {
        var w = Tab("Whispers", ChatScope.Whispers);
        Assert.Equal("/w Mira ", ChatTabInput.PrefillFor(w, "Mira"));
        // Under the prefilled line (and when it is cleared) the channel is Local.
        Assert.Equal((int)ChatSend.Local, ChatTabInput.ModeFor(w, "Mira"));
        // No partner yet: no prefill, Local.
        Assert.Null(ChatTabInput.PrefillFor(w, ""));
        Assert.Equal(ChatSend.Local, ChatTabInput.EffectiveFor(w, ""));
        Assert.Equal((int)ChatSend.Local, ChatTabInput.ModeFor(w, null));
        var wm = Tab("W+M+S", ChatScope.Whispers | ChatScope.Mentions | ChatScope.SystemAndMods);
        Assert.Equal((int)ChatSend.Local, ChatTabInput.ModeFor(wm, "Mira"));
        // A clan tab that shows whispers types to clan; it never prefills.
        Assert.Null(ChatTabInput.PrefillFor(Tab("CW", ChatScope.Clan | ChatScope.Whispers), "Mira"));
        Assert.Equal((int)ChatSend.Clan, ChatTabInput.ModeFor(Tab("CW", ChatScope.Clan | ChatScope.Whispers), "Mira"));
    }

    // Review item 1: only a channel the tab shows is remembered; any other pick is a one-off.
    [Fact]
    public void Only_a_channel_the_tab_shows_is_remembered()
    {
        var clan = Tab("Clan", ChatScope.Clan);
        Assert.False(ChatTabInput.Remember(clan, (int)ChatSend.Global));
        Assert.False(ChatTabInput.Remember(clan, (int)ChatSend.Local));
        Assert.False(ChatTabInput.Remember(clan, (int)ChatSend.Whisper));
        Assert.False(ChatTabInput.Remember(clan, (int)ChatSend.System));
        Assert.Null(clan.SendTo);
        Assert.Equal((int)ChatSend.Clan, ChatTabInput.ModeFor(clan, ""));

        var mix = Tab("Mix", GlobalClan);
        Assert.True(ChatTabInput.Remember(mix, (int)ChatSend.Global));
        Assert.Equal(ChatSend.Global, mix.SendTo);
        Assert.Equal(0, ChatTabInput.ModeFor(mix, ""));
        // A stray Local pick (not shown) leaves Global remembered.
        Assert.False(ChatTabInput.Remember(mix, (int)ChatSend.Local));
        Assert.Equal(ChatSend.Global, mix.SendTo);
        // Back to the default: stored as none.
        Assert.True(ChatTabInput.Remember(mix, (int)ChatSend.Clan));
        Assert.Null(mix.SendTo);
        // Region (1) and unknown modes are never remembered.
        Assert.False(ChatTabInput.Remember(mix, 1));
        Assert.False(ChatTabInput.Remember(mix, 42));
        // System only where system lines are shown.
        var cs = Tab("CS", ChatScope.Clan | ChatScope.SystemAndMods);
        Assert.True(ChatTabInput.Remember(cs, (int)ChatSend.System));
        Assert.Equal(ChatSend.System, cs.SendTo);

        // A whispers-only tab: Global is not shown, so /g lasts one opening; the whisper default stays.
        var w = Tab("Whispers", ChatScope.Whispers);
        Assert.False(ChatTabInput.Remember(w, (int)ChatSend.Global));
        Assert.False(ChatTabInput.Remember(w, (int)ChatSend.Whisper));
        Assert.Equal("/w Mira ", ChatTabInput.PrefillFor(w, "Mira"));
        // Whispers are never sticky elsewhere.
        Assert.False(ChatTabInput.Remember(Tab("CW", ChatScope.Clan | ChatScope.Whispers), (int)ChatSend.Whisper));

        // Editing a tab's channels forgets its remembered channel.
        mix.SendTo = ChatSend.Global;
        mix.Scope = ChatScope.Local | ChatScope.Global;
        Assert.Null(mix.SendTo);
        Assert.Equal(4, ChatTabInput.ModeFor(mix, ""));
        mix.SendTo = ChatSend.Global;
        mix.Scope = ChatScope.Local | ChatScope.Global;
        Assert.Equal(ChatSend.Global, mix.SendTo);
    }

    // Review item 2: All, Mentions-only and System-only tabs keep the channel in use and never remember.
    [Fact]
    public void Tabs_without_a_default_never_remember()
    {
        foreach (var tab in new[] { ChatTab.All(), Tab("Mentions", ChatScope.Mentions), Tab("System", ChatScope.SystemAndMods) })
        {
            foreach (var send in new[] { ChatSend.Global, ChatSend.Local, ChatSend.Clan, ChatSend.Whisper, ChatSend.System })
            {
                Assert.False(ChatTabInput.CanRemember(tab, send));
                Assert.False(ChatTabInput.Remember(tab, (int)send));
            }

            Assert.Null(tab.SendTo);
            Assert.Equal(-1, ChatTabInput.ModeFor(tab, "Mira"));
            Assert.Null(ChatTabInput.PrefillFor(tab, "Mira"));
        }
    }

    [Fact]
    public void Tracker_remembers_only_picks_made_while_typing_and_reports_openings()
    {
        var tab = Tab("Mix", GlobalClan);
        var t = new ChatTabInputTracker();
        // A world load sets the channel while the input is closed: noted, not remembered.
        Assert.False(t.Observe(tab, 4, focused: false, out bool opened));
        Assert.False(opened);
        // Opening the input: reported, and a change seen on that frame is not a pick (the caller re-applies the tab's).
        Assert.False(t.Observe(tab, 0, focused: true, out opened));
        Assert.True(opened);
        Assert.Null(tab.SendTo);
        t.Applied(2);
        // The player types /g.
        Assert.True(t.Observe(tab, 0, focused: true, out opened));
        Assert.False(opened);
        Assert.Equal(ChatSend.Global, tab.SendTo);
        // Progenitor's own switch is not a choice.
        t.Applied(2);
        Assert.False(t.Observe(tab, 2, focused: true, out _));
        Assert.Equal(ChatSend.Global, tab.SendTo);
        // A stray Tab cycle to Local (not shown) is not saved.
        Assert.False(t.Observe(tab, 4, focused: true, out _));
        Assert.Equal(ChatSend.Global, tab.SendTo);
        // After a reset (new world) the first seen channel is only noted.
        t.Reset();
        var fresh = Tab("Mix", GlobalClan);
        Assert.False(t.Observe(fresh, 0, focused: true, out _));
        Assert.Null(fresh.SendTo);
    }

    [Fact]
    public void Tracker_prefills_once_per_opening_and_after_a_tab_switch()
    {
        var w = Tab("Whispers", ChatScope.Whispers);
        var t = new ChatTabInputTracker();
        Assert.Null(t.Prefill(w, "Mira", focused: false, "", insertPending: false));
        Assert.Equal("/w Mira ", t.Prefill(w, "Mira", focused: true, "", insertPending: false));
        // The player cleared the line: no second prefill in this opening.
        Assert.Null(t.Prefill(w, "Mira", focused: true, "", insertPending: false));
        t.Observe(w, 4, focused: false, out _);
        Assert.Equal("/w Mira ", t.Prefill(w, "Mira", focused: true, "", insertPending: false));
        // Something typed already: left alone.
        t.Observe(w, 4, focused: false, out _);
        Assert.Null(t.Prefill(w, "Mira", focused: true, "hey", insertPending: false));
        Assert.Null(t.Prefill(w, "Mira", focused: true, "", insertPending: false));
        t.TabChanged();
        Assert.Equal("/w Mira ", t.Prefill(w, "Mira", focused: true, "", insertPending: false));
        // No partner: nothing.
        t.TabChanged();
        Assert.Null(t.Prefill(w, "", focused: true, "", insertPending: false));
    }

    // Review item 5: a right-click Whisper (or Share) insert on the whispers tab never follows a prefill.
    [Fact]
    public void A_pending_insert_wins_over_the_whisper_prefill()
    {
        var w = Tab("Whispers", ChatScope.Whispers);
        var t = new ChatTabInputTracker();
        // The insert is waiting for the chat to open: no prefill on that frame.
        Assert.Null(t.Prefill(w, "Bob", focused: true, "", insertPending: true));
        // It landed ("/w Carol "): the opening keeps it.
        Assert.Null(t.Prefill(w, "Bob", focused: true, "/w Carol ", insertPending: false));
        // An insert into a line that already holds the prefill replaces a bare whisper line.
        Assert.Equal("/w Carol ", ChatTabInput.JoinInsert("/w Bob ", "/w Carol "));
        Assert.Equal("/whisper Carol ", ChatTabInput.JoinInsert("/w Bob", "/whisper Carol "));
        // Share in chat on a prefilled line shares with that partner; other lines get it appended.
        Assert.Equal("/w Bob [mod:A-B]", ChatTabInput.JoinInsert("/w Bob ", "[mod:A-B]"));
        Assert.Equal("/w Bob hi /w Carol ", ChatTabInput.JoinInsert("/w Bob hi", "/w Carol "));
        Assert.Equal("hello [mod:A-B]", ChatTabInput.JoinInsert("hello", "[mod:A-B]"));
        Assert.Equal("[mod:A-B]", ChatTabInput.JoinInsert("", "[mod:A-B]"));
    }

    [Fact]
    public void Bare_whisper_lines_are_recognized()
    {
        Assert.True(ChatTabInput.IsBareWhisper("/w Bob "));
        Assert.True(ChatTabInput.IsBareWhisper(" /whisper Bob"));
        Assert.True(ChatTabInput.IsBareWhisper("/W bob"));
        Assert.False(ChatTabInput.IsBareWhisper("/w Bob hi"));
        Assert.False(ChatTabInput.IsBareWhisper("/w "));
        Assert.False(ChatTabInput.IsBareWhisper("/wave"));
        Assert.False(ChatTabInput.IsBareWhisper(""));
        Assert.False(ChatTabInput.IsBareWhisper(null));
    }

    [Fact]
    public void Drafts_swap_with_the_tab()
    {
        var a = Tab("Clan", ChatScope.Clan);
        var b = Tab("Trade", ChatScope.Global);
        // First tab in a world: the line stays.
        Assert.Equal("typed", ChatTabInput.SwapDraft(null, a, "typed"));
        Assert.Equal("", ChatTabInput.SwapDraft(a, b, "half a line"));
        Assert.Equal("half a line", a.Draft);
        Assert.Equal("wts sword", ChatTabInput.SwapDraft(b, b, "wts sword"));
        Assert.Equal("half a line", ChatTabInput.SwapDraft(b, a, "wts sword"));
        Assert.Equal("wts sword", b.Draft);
        // A bare whisper line (a prefill, or a stale one after the partner changed) is not kept as a draft.
        var w = Tab("Whispers", ChatScope.Whispers);
        ChatTabInput.SwapDraft(a, w, "");
        Assert.Equal("wts sword", ChatTabInput.SwapDraft(w, b, "/w Bob "));
        Assert.Equal("", w.Draft);
        ChatTabInput.SwapDraft(b, w, "");
        ChatTabInput.SwapDraft(w, b, "/w Bob see you");
        Assert.Equal("/w Bob see you", w.Draft);
    }

    // Review item 6: a tab switch never records a draft as sent.
    [Fact]
    public void Sent_watch_counts_sends_not_tab_clicks_or_swapped_drafts()
    {
        var watch = new ChatSentWatch();
        bool NoTab(string _) => false;
        bool OnTab(string _) => true;

        // Enter: focused with text, then unfocused and empty.
        Assert.Null(watch.Frame(true, "hello", NoTab));
        Assert.True(watch.LostFocus(false));
        Assert.Equal("hello", watch.Frame(false, "", NoTab));
        Assert.False(watch.LostFocus(false));

        // Focus lost to a press on the tab row, the game emptying the line: a tab click, not a send.
        var asked = new List<string>();
        Assert.Null(watch.Frame(true, "half a line", NoTab));
        Assert.Equal("half a line", watch.Typed);
        Assert.Null(watch.Frame(false, "", s => { asked.Add(s); return true; }));
        Assert.Equal(new[] { "half a line" }, asked);
        Assert.True(watch.LastLossWasTabClick);
        Assert.Equal("", watch.Typed);

        // The line swapped for another tab's while still focused, then focus lost with the new (empty) line.
        Assert.Null(watch.Frame(true, "draft on clan", NoTab));
        watch.Forget();
        Assert.Null(watch.Frame(false, "", NoTab));

        // A bare whisper line closed (Escape) is not a sent line, stale partner or not.
        Assert.Null(watch.Frame(true, "/w Bob ", NoTab));
        Assert.Null(watch.Frame(false, "", NoTab));

        // Focus lost with the line still there (a click elsewhere that keeps it): nothing sent.
        Assert.Null(watch.Frame(true, "still typing", NoTab));
        Assert.Null(watch.Frame(false, "still typing", OnTab));
    }

    // Live 2026-10-18: the game empties the input when Enter sends, but its focus flag drops a chat update later.
    [Fact]
    public void Sent_watch_counts_a_send_whose_focus_loss_shows_a_frame_after_the_clear()
    {
        var watch = new ChatSentWatch();
        bool NoTab(string _) => false;

        Assert.Null(watch.Frame(true, "hello", NoTab));
        Assert.Null(watch.Frame(true, "", NoTab));      // emptied, focus flag not updated yet
        Assert.Equal("hello", watch.Typed);
        Assert.True(watch.LostFocus(false));
        Assert.Equal("hello", watch.Frame(false, "", NoTab));
        Assert.Equal("", watch.Typed);

        // Tolerates the lag up to ClearedFrames focused frames.
        Assert.Null(watch.Frame(true, "again", NoTab));
        for (int i = 0; i < ChatSentWatch.ClearedFrames; i++) Assert.Null(watch.Frame(true, "", NoTab));
        Assert.Equal("again", watch.Frame(false, "", NoTab));

        // A line emptied while the input stays open (select all, delete) is no longer offered once focus stays.
        Assert.Null(watch.Frame(true, "typo", NoTab));
        for (int i = 0; i <= ChatSentWatch.ClearedFrames; i++) Assert.Null(watch.Frame(true, "", NoTab));
        Assert.Null(watch.Frame(false, "", NoTab));

        // Typing again after a clear forgets the emptied line.
        Assert.Null(watch.Frame(true, "first", NoTab));
        Assert.Null(watch.Frame(true, "", NoTab));
        Assert.Null(watch.Frame(true, "s", NoTab));
        Assert.Equal("s", watch.Typed);

        // A swapped line (Forget) right after a clear is not counted.
        Assert.Null(watch.Frame(true, "draft", NoTab));
        Assert.Null(watch.Frame(true, "", NoTab));
        watch.Forget();
        Assert.Null(watch.Frame(false, "", NoTab));

        // A tab click right after a clear is asked with the emptied line and stays a tab click.
        var asked = new List<string>();
        Assert.Null(watch.Frame(true, "for clan", NoTab));
        Assert.Null(watch.Frame(true, "", NoTab));
        Assert.Null(watch.Frame(false, "", s => { asked.Add(s); return true; }));
        Assert.Equal(new[] { "for clan" }, asked);
    }

    [Fact]
    public void Saved_send_channels_round_trip_and_older_strings_still_load()
    {
        var tabs = ChatTab.Load("Clan=4;Mix=5;All;W=8");
        Assert.All(tabs, t => Assert.Null(t.SendTo));
        Assert.Equal("Clan=4;Mix=5;All;W=8", ChatTab.Save(tabs));
        Assert.True(ChatTabInput.Remember(tabs[1], (int)ChatSend.Global));
        string saved = ChatTab.Save(tabs);
        Assert.Equal("Clan=4;Mix=5;>global;All;W=8", saved);
        var back = ChatTab.Load(saved);
        Assert.Equal(new[] { "Clan", "Mix", "All", "W" }, back.Select(t => t.Name));
        Assert.Equal(new ChatSend?[] { null, ChatSend.Global, null, null }, back.Select(t => t.SendTo));
        // Markers a tab may not remember are dropped: All never, a channel it doesn't show, a whisper, the default.
        var odd = ChatTab.Load("All;>clan;Clan=4;>global;W=8;>whisper;S=16;>system;CS=20;>system;Mix=5;>clan;>nope;>");
        Assert.Equal(new[] { "All", "Clan", "W", "S", "CS", "Mix" }, odd.Select(t => t.Name));
        Assert.Equal(new ChatSend?[] { null, null, null, null, ChatSend.System, null }, odd.Select(t => t.SendTo));
        Assert.Equal("Clan=4;W=8;S=16;CS=20;>system;Mix=5", ChatTab.Save(odd));
        // Stray markers never name a tab.
        Assert.Equal(new[] { "All", "Clan" }, ChatTab.Load(">clan;Clan=4").Select(t => t.Name));
    }

    [Fact]
    public void The_fifth_tabs_marker_is_read_although_the_list_is_full()
    {
        var full = ChatTab.Load("A=1;B=1;C=1;D=1;E=3;>global;F=1");
        Assert.Equal(6, full.Count);
        Assert.Equal(ChatSend.Global, full[5].SendTo);
    }

    /// <summary>The loader as it was before CHAT13 (CHAT12): a newer string must still give the same tabs there.</summary>
    [Fact]
    public void A_build_before_send_channels_loads_the_new_string()
    {
        string saved = "Clan=4;Mix=5;>global;All;W=8;>whisper";
        Assert.Equal(new[] { "Clan", "Mix", "All", "W" }, OldLoad(saved));
        Assert.Equal(new[] { "Clan", "Mix", "All", "W" }, ChatTab.Load(saved).Select(t => t.Name));
    }

    private static string[] OldLoad(string saved)
    {
        var names = new List<string>();
        int allAt = -1;
        foreach (var raw in saved.Split(';'))
        {
            if (names.Count >= ChatTab.MaxTabs - 1) break;
            string part = raw.Trim();
            if (allAt < 0 && string.Equals(part, "All", System.StringComparison.OrdinalIgnoreCase)) { allAt = names.Count; continue; }
            int eq = part.LastIndexOf('=');
            if (eq <= 0) continue;
            string name = ChatTab.Clean(part.Substring(0, eq));
            if (name.Length == 0 || string.Equals(name, "All", System.StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(part.Substring(eq + 1), out int s) || s == 0) continue;
            names.Add(name);
        }

        names.Insert(allAt < 0 ? 0 : System.Math.Min(allAt, names.Count), "All");
        return names.ToArray();
    }
}
