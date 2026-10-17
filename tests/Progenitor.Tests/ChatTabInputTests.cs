using System.Linq;
using Progenitor.Chat;
using Xunit;

namespace Progenitor.Tests;

/// <summary>CHAT13: the input line per tab (send channel, whisper line, drafts) and its saved form.</summary>
public sealed class ChatTabInputTests
{
    private static ChatTab Tab(string name, ChatScope scope) => new() { Name = name, Scope = scope };

    [Fact]
    public void Default_channel_is_the_narrowest_the_tab_shows()
    {
        Assert.Equal(ChatSend.Clan, ChatTabInput.DefaultFor(Tab("Clan", ChatScope.Clan)));
        Assert.Equal(ChatSend.Local, ChatTabInput.DefaultFor(Tab("Local", ChatScope.Local)));
        Assert.Equal(ChatSend.Global, ChatTabInput.DefaultFor(Tab("Trade", ChatScope.Global)));
        // Mixed: never the whole server when a smaller audience is shown.
        Assert.Equal(ChatSend.Clan, ChatTabInput.DefaultFor(Tab("Mix", ChatScope.Global | ChatScope.Clan)));
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

    [Fact]
    public void Whisper_tab_prefills_the_partner_and_falls_back_without_one()
    {
        var w = Tab("Whispers", ChatScope.Whispers);
        Assert.Equal("/w Mira ", ChatTabInput.PrefillFor(w, "Mira"));
        Assert.Equal(-1, ChatTabInput.ModeFor(w, "Mira"));
        // No partner yet: no prefill, and nothing to switch to on a whispers-only tab.
        Assert.Null(ChatTabInput.PrefillFor(w, ""));
        Assert.Null(ChatTabInput.EffectiveFor(w, ""));
        var wm = Tab("W+M", ChatScope.Whispers | ChatScope.Mentions);
        Assert.Null(ChatTabInput.EffectiveFor(wm, null));
        // A clan tab that shows whispers types to clan; it never prefills.
        Assert.Null(ChatTabInput.PrefillFor(Tab("CW", ChatScope.Clan | ChatScope.Whispers), "Mira"));
        Assert.True(ChatTabInput.IsOnlyPrefill(" /w Mira  ", "/w Mira "));
        Assert.False(ChatTabInput.IsOnlyPrefill("/w Mira hi", "/w Mira "));
        Assert.False(ChatTabInput.IsOnlyPrefill("", null));
    }

    [Fact]
    public void Explicit_channel_wins_and_is_remembered_per_tab()
    {
        var clan = Tab("Clan", ChatScope.Clan);
        Assert.True(ChatTabInput.Remember(clan, (int)ChatSend.Global));
        Assert.Equal(ChatSend.Global, clan.SendTo);
        Assert.Equal(0, ChatTabInput.ModeFor(clan, ""));
        // Back to the default: stored as none.
        Assert.True(ChatTabInput.Remember(clan, (int)ChatSend.Clan));
        Assert.Null(clan.SendTo);
        Assert.False(ChatTabInput.Remember(clan, (int)ChatSend.Clan));
        // Region (1) and unknown modes are not remembered; whispers are not sticky.
        Assert.False(ChatTabInput.Remember(clan, 1));
        Assert.False(ChatTabInput.Remember(clan, 42));
        Assert.False(ChatTabInput.Remember(clan, (int)ChatSend.Whisper));
        var all = ChatTab.All();
        Assert.False(ChatTabInput.Remember(all, (int)ChatSend.Whisper));
        Assert.True(ChatTabInput.Remember(all, (int)ChatSend.Local));
        Assert.Equal(4, ChatTabInput.ModeFor(all, ""));
        // On a whisper tab, /g is remembered and a whisper brings the tab's default back.
        var w = Tab("Whispers", ChatScope.Whispers);
        Assert.True(ChatTabInput.Remember(w, (int)ChatSend.Global));
        Assert.Null(ChatTabInput.PrefillFor(w, "Mira"));
        Assert.True(ChatTabInput.Remember(w, (int)ChatSend.Whisper));
        Assert.Equal("/w Mira ", ChatTabInput.PrefillFor(w, "Mira"));
        // Editing a tab's channels forgets its remembered channel.
        clan.SendTo = ChatSend.Global;
        clan.Scope = ChatScope.Local;
        Assert.Null(clan.SendTo);
        Assert.Equal(4, ChatTabInput.ModeFor(clan, ""));
        clan.SendTo = ChatSend.Global;
        clan.Scope = ChatScope.Local;
        Assert.Equal(ChatSend.Global, clan.SendTo);
    }

    [Fact]
    public void Tracker_remembers_only_changes_made_while_typing()
    {
        var tab = Tab("Clan", ChatScope.Clan);
        var t = new ChatTabInputTracker();
        // A world load sets the channel while the input is closed: noted, not remembered.
        Assert.False(t.Observe(tab, 4, focused: false));
        Assert.False(t.Observe(tab, 4, focused: true));
        // The player types /g.
        Assert.True(t.Observe(tab, 0, focused: true));
        Assert.Equal(ChatSend.Global, tab.SendTo);
        // Progenitor's own switch is not a choice.
        t.Applied(2);
        Assert.False(t.Observe(tab, 2, focused: true));
        Assert.Equal(ChatSend.Global, tab.SendTo);
        // After a reset (new world) the first seen channel is only noted.
        t.Reset();
        var fresh = Tab("Local", ChatScope.Local);
        Assert.False(t.Observe(fresh, 0, focused: true));
        Assert.Null(fresh.SendTo);
    }

    [Fact]
    public void Tracker_prefills_once_per_opening_and_after_a_tab_switch()
    {
        var w = Tab("Whispers", ChatScope.Whispers);
        var t = new ChatTabInputTracker();
        Assert.Null(t.Prefill(w, "Mira", focused: false, ""));
        Assert.Equal("/w Mira ", t.Prefill(w, "Mira", focused: true, ""));
        // The player cleared the line: no second prefill in this opening.
        Assert.Null(t.Prefill(w, "Mira", focused: true, ""));
        t.Observe(w, 0, focused: false);
        Assert.Equal("/w Mira ", t.Prefill(w, "Mira", focused: true, ""));
        // Something typed already: left alone.
        t.Observe(w, 0, focused: false);
        Assert.Null(t.Prefill(w, "Mira", focused: true, "hey"));
        Assert.Null(t.Prefill(w, "Mira", focused: true, ""));
        t.TabChanged();
        Assert.Equal("/w Mira ", t.Prefill(w, "Mira", focused: true, ""));
        // No partner: nothing.
        t.TabChanged();
        Assert.Null(t.Prefill(w, "", focused: true, ""));
    }

    [Fact]
    public void Drafts_swap_with_the_tab()
    {
        var a = Tab("Clan", ChatScope.Clan);
        var b = Tab("Trade", ChatScope.Global);
        // First tab in a world: the line stays.
        Assert.Equal("typed", ChatTabInput.SwapDraft(null, a, "typed", null));
        Assert.Equal("", ChatTabInput.SwapDraft(a, b, "half a line", null));
        Assert.Equal("half a line", a.Draft);
        Assert.Equal("wts sword", ChatTabInput.SwapDraft(b, b, "wts sword", null));
        Assert.Equal("half a line", ChatTabInput.SwapDraft(b, a, "wts sword", null));
        Assert.Equal("wts sword", b.Draft);
        // A bare whisper prefill is not kept as a draft.
        var w = Tab("Whispers", ChatScope.Whispers);
        ChatTabInput.SwapDraft(a, w, "", null);
        Assert.Equal("wts sword", ChatTabInput.SwapDraft(w, b, "/w Mira ", "/w Mira "));
        Assert.Equal("", w.Draft);
        ChatTabInput.SwapDraft(b, w, "", null);
        ChatTabInput.SwapDraft(w, b, "/w Mira see you", "/w Mira ");
        Assert.Equal("/w Mira see you", w.Draft);
    }

    [Fact]
    public void Saved_send_channels_round_trip_and_older_strings_still_load()
    {
        var tabs = ChatTab.Load("Clan=4;Trade=1;All;W=8");
        Assert.All(tabs, t => Assert.Null(t.SendTo));
        Assert.Equal("Clan=4;Trade=1;All;W=8", ChatTab.Save(tabs));
        tabs[1].SendTo = ChatSend.Local;   // Trade
        tabs[2].SendTo = ChatSend.Global;  // All
        string saved = ChatTab.Save(tabs);
        Assert.Equal("Clan=4;Trade=1;>local;All;>global;W=8", saved);
        var back = ChatTab.Load(saved);
        Assert.Equal(new[] { "Clan", "Trade", "All", "W" }, back.Select(t => t.Name));
        Assert.Equal(new ChatSend?[] { null, ChatSend.Local, ChatSend.Global, null }, back.Select(t => t.SendTo));
        // All first with a remembered channel is written, so its marker has an owner.
        var first = ChatTab.Load(ChatTab.DefaultSaved);
        first[0].SendTo = ChatSend.Clan;
        Assert.Equal("All;>clan;Clan=4", ChatTab.Save(first));
        Assert.Equal(ChatSend.Clan, ChatTab.Load("All;>clan;Clan=4")[0].SendTo);
        // Stray and unknown markers are skipped; a marker never names a tab.
        var odd = ChatTab.Load(">clan;Clan=4;>nope;>local;Trade=1;>");
        Assert.Equal(new[] { "All", "Clan", "Trade" }, odd.Select(t => t.Name));
        Assert.All(odd, t => Assert.Null(t.SendTo));
        // The fifth tab's marker is read although the tab list is full.
        var full = ChatTab.Load("A=1;B=1;C=1;D=1;E=1;>local;F=1");
        Assert.Equal(6, full.Count);
        Assert.Equal(ChatSend.Local, full[5].SendTo);
    }

    /// <summary>The loader as it was before CHAT13 (CHAT12): a newer string must still give the same tabs there.</summary>
    [Fact]
    public void A_build_before_send_channels_loads_the_new_string()
    {
        string saved = "All;>clan;Clan=4;Trade=1;>local;W=8";
        Assert.Equal(new[] { "All", "Clan", "Trade", "W" }, OldLoad(saved));
        Assert.Equal(new[] { "All", "Clan", "Trade", "W" }, ChatTab.Load(saved).Select(t => t.Name));
    }

    private static string[] OldLoad(string saved)
    {
        var names = new System.Collections.Generic.List<string>();
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
