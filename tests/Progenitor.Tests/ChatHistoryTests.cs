using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Progenitor.Chat;
using Xunit;
using Xunit.Abstractions;

namespace Progenitor.Tests;

/// <summary>
/// 5,000-line chat history (2026-10-17): the log's ring and incremental tab filters, the paged window, the line-object
/// reuse plan, the history file, the save schedule and the default upgrade, game-free. The timing tests check counts
/// strictly and times with generous bounds, so a slow CI machine fails only on a real regression.
/// </summary>
public sealed class ChatHistoryTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 20, 0, 0, DateTimeKind.Utc);
    private readonly ITestOutputHelper _out;

    public ChatHistoryTests(ITestOutputHelper output) => _out = output;

    private static readonly ChatKind[] Kinds = { ChatKind.Global, ChatKind.Local, ChatKind.Team, ChatKind.WhisperFrom, ChatKind.System };

    private static ChatRecord Line(int i, ChatKind? kind = null, bool mention = false) => new()
    {
        Kind = kind ?? Kinds[i % Kinds.Length],
        User = "P" + (i % 7),
        Text = "line " + i + " see https://example.com/" + i + " [item:" + (1000 + i) + "]",
        TimeUtc = T0.AddSeconds(i * 61),
        Mention = mention,
    };

    private static ChatLog Full(int lines = ChatLog.MaxCapacity)
    {
        var log = new ChatLog(ChatLog.MaxCapacity);
        for (int i = 0; i < lines; i++) log.Add(Line(i, mention: i % 50 == 0), collapse: true);
        return log;
    }

    private static List<ChatRecord> BruteForce(ChatLog log, ChatTab tab) => log.Records.Where(tab.Shows).ToList();

    // ---- defaults and the upgrade ------------------------------------------------------------------------

    [Fact]
    public void The_default_is_5000_and_an_untouched_old_default_moves_up_once()
    {
        Assert.Equal(5000, ChatLog.DefaultCapacity);
        Assert.Equal(ChatLog.MaxCapacity, ChatLog.DefaultCapacity);
        Assert.Equal(5000, ChatHistorySetting.Upgrade(ChatHistorySetting.OldDefault, alreadyRaised: false));
        // A player's own number stays, and after the upgrade a chosen 500 stays too.
        Assert.Equal(1200, ChatHistorySetting.Upgrade(1200, alreadyRaised: false));
        Assert.Equal(50, ChatHistorySetting.Upgrade(50, alreadyRaised: false));
        Assert.Equal(500, ChatHistorySetting.Upgrade(500, alreadyRaised: true));
    }

    // ---- the ring and capacity ---------------------------------------------------------------------------

    [Fact]
    public void Ring_keeps_order_across_wraps_and_growth()
    {
        var ring = new ChatRing<string>(4);
        for (int i = 0; i < 3; i++) ring.PushBack("a" + i);
        Assert.Equal("a0", ring.PopFront());
        for (int i = 3; i < 20; i++) ring.PushBack("a" + i);
        Assert.Equal(19, ring.Count);
        Assert.Equal(Enumerable.Range(1, 19).Select(i => "a" + i), ring);
        Assert.Equal("a1", ring.First);
        Assert.Equal("a19", ring.Last);
        Assert.Throws<ArgumentOutOfRangeException>(() => ring[19]);
    }

    [Fact]
    public void Capacity_applies_at_once_lower_drops_the_oldest_higher_keeps_more()
    {
        var log = Full(1000);
        var all = log.Filtered(ChatTab.All());
        Assert.Equal(1000, all.Count);
        int v = log.Version;
        log.Capacity = 200;
        Assert.Equal(200, log.Records.Count);
        Assert.Equal("line 800", log.Records[0].Text.Substring(0, 8));
        Assert.True(log.Version > v);
        Assert.Equal(BruteForce(log, ChatTab.All()), log.Filtered(ChatTab.All()));
        log.Capacity = 5000;
        for (int i = 1000; i < 1500; i++) log.Add(Line(i), true);
        Assert.Equal(700, log.Records.Count);
        log.Capacity = 1; // clamped
        Assert.Equal(ChatLog.MinCapacity, log.Capacity);
    }

    // ---- incremental tab filters -------------------------------------------------------------------------

    [Fact]
    public void Tab_filters_stay_equal_to_a_full_scan_through_adds_repeats_joins_drops_and_edits()
    {
        var rnd = new Random(17);
        var log = new ChatLog(ChatLog.MinCapacity);
        var tabs = new List<ChatTab>
        {
            ChatTab.All(), ChatTab.Clan(), new() { Name = "W", Scope = ChatScope.Whispers },
            new() { Name = "M", Scope = ChatScope.Mentions }, new() { Name = "S", Scope = ChatScope.SystemAndMods | ChatScope.Local },
        };
        foreach (var t in tabs) log.Filtered(t);
        for (int i = 0; i < 2000; i++)
        {
            int roll = rnd.Next(10);
            if (roll < 2 && log.Records.Count > 0)
            {
                // The same line again: a repeat, which may bring a mention with it.
                var last = log.Records[log.Records.Count - 1];
                log.Add(new ChatRecord { Kind = last.Kind, User = last.User, Text = last.Text, TimeUtc = last.TimeUtc.AddSeconds(1), Mention = rnd.Next(3) == 0 }, true);
            }
            else if (roll == 2) log.AddMarker("Joined X", T0.AddSeconds(i));
            else log.Add(Line(rnd.Next(100000), Kinds[rnd.Next(Kinds.Length)], mention: rnd.Next(20) == 0), true);
            if (i == 900) tabs[1].Scope = ChatScope.Clan | ChatScope.Global; // an edited tab rescans once
            if (i == 1500) log.Capacity = 120;
            if (i % 97 == 0)
                foreach (var t in tabs) Assert.Equal(BruteForce(log, t), log.Filtered(t));
        }

        foreach (var t in tabs) Assert.Equal(BruteForce(log, t), log.Filtered(t));
    }

    [Fact]
    public void A_repeat_that_brings_a_mention_joins_the_mentions_tab()
    {
        var log = new ChatLog();
        var mentions = new ChatTab { Name = "M", Scope = ChatScope.Mentions };
        log.Add(new ChatRecord { Kind = ChatKind.Global, User = "A", Text = "hey", TimeUtc = T0 }, true);
        Assert.Empty(log.Filtered(mentions));
        log.Add(new ChatRecord { Kind = ChatKind.Global, User = "A", Text = "hey", TimeUtc = T0.AddSeconds(2), Mention = true }, true);
        Assert.Single(log.Filtered(mentions));
        log.Add(new ChatRecord { Kind = ChatKind.Global, User = "A", Text = "hey", TimeUtc = T0.AddSeconds(3), Mention = true }, true);
        Assert.Single(log.Filtered(mentions)); // never twice
        Assert.Equal(3, log.Records[0].Repeat);
        Assert.Equal(2, log.Records[0].Rev);
    }

    [Fact]
    public void Restore_numbers_every_line_again_oldest_first()
    {
        var log = new ChatLog();
        log.Add(Line(1), true);
        log.Add(Line(2), true);
        var saved = ChatLog.Deserialize(Full(300).Serialize(300), 300);
        log.Restore(saved);
        Assert.Equal(302, log.Records.Count);
        for (int i = 1; i < log.Records.Count; i++) Assert.True(log.Records[i].Seq > log.Records[i - 1].Seq);
        var all = log.Filtered(ChatTab.All());
        Assert.Equal(150, ChatLog.IndexAtOrBefore(all, all[150].Seq));
        Assert.Equal(-1, ChatLog.IndexAtOrBefore(all, 0));
    }

    // ---- the paged window --------------------------------------------------------------------------------

    [Fact]
    public void Window_follows_pins_pages_back_and_forward_and_follows_again()
    {
        var log = Full(1000);
        var lines = log.Filtered(ChatTab.All());
        var w = new ChatScrollWindow(120, 40);
        Assert.Equal((880, 120), w.Resolve(lines));

        // Reading up: pinned at the newest line drawn; new lines do not move it.
        w.Pin(lines[999].Seq);
        for (int i = 1000; i < 1010; i++) log.Add(Line(i), true);
        Assert.Equal((880, 120), w.Resolve(lines));

        Assert.True(w.PageBack(lines));
        Assert.Equal((840, 120), w.Resolve(lines));
        for (int i = 0; i < 30; i++) w.PageBack(lines);
        Assert.Equal((0, 120), w.Resolve(lines));
        Assert.False(w.PageBack(lines));

        // Forward a page at a time, then following again within a page of the newest.
        Assert.True(w.PageForward(lines));
        Assert.Equal((40, 120), w.Resolve(lines));
        while (!w.Following) Assert.True(w.PageForward(lines));
        Assert.Equal((890, 120), w.Resolve(lines));
        Assert.False(w.PageForward(lines));
    }

    [Fact]
    public void Window_whose_pinned_line_was_dropped_shows_the_oldest_lines_left()
    {
        var log = new ChatLog(ChatLog.MinCapacity);
        for (int i = 0; i < 50; i++) log.Add(Line(i), true);
        var lines = log.Filtered(ChatTab.All());
        var w = new ChatScrollWindow(20, 10);
        w.Pin(lines[5].Seq);
        for (int i = 50; i < 80; i++) log.Add(Line(i), true);
        Assert.Equal((0, 20), w.Resolve(lines));
        Assert.Equal((0, 0), w.Resolve(new ChatLog().Filtered(ChatTab.All())));
    }

    [Fact]
    public void Every_line_of_a_5000_line_history_is_reachable_by_paging_back()
    {
        var log = Full();
        var lines = log.Filtered(ChatTab.All());
        var w = new ChatScrollWindow(ChatViewWindow.Size, ChatViewWindow.Page);
        var seen = new HashSet<long>();
        w.Pin(lines[lines.Count - 1].Seq);
        do
        {
            var (start, count) = w.Resolve(lines);
            Assert.True(count <= ChatViewWindow.Size);
            for (int i = start; i < start + count; i++) seen.Add(lines[i].Seq);
        }
        while (w.PageBack(lines));

        Assert.Equal(lines.Count, seen.Count);
    }

    /// <summary>The view's window, mirrored here (ChatView is game code): 120 line objects, pages of 40.</summary>
    private static class ChatViewWindow
    {
        internal const int Size = 120;
        internal const int Page = 40;
    }

    // ---- line-object reuse -------------------------------------------------------------------------------

    private sealed class Slot
    {
        internal Slot(int id) => Id = id;
        internal int Id { get; }
        public override string ToString() => "s" + Id;
    }

    [Fact]
    public void A_new_line_moves_one_object_not_every_line()
    {
        var slots = Enumerable.Range(0, 120).Select(i => new Slot(i)).ToList();
        var current = new List<Slot>(slots);
        // The oldest line's object takes the new line at the end.
        var wanted = slots.Skip(1).Concat(new[] { slots[0] }).ToList();
        var moves = ChatSlotOrder.Plan(current, wanted);
        Assert.Single(moves);
        Assert.Same(slots[0], moves[0].Item);
        Assert.Equal(119, moves[0].Index);
        Assert.Equal(wanted, current);

        // Paging back: forty objects move to the front, the rest stay.
        var back = slots.Skip(80).Concat(slots.Take(80)).ToList();
        current = new List<Slot>(slots);
        Assert.Equal(40, ChatSlotOrder.Plan(current, back).Count);
        Assert.Equal(back, current);

        current = new List<Slot>(slots);
        Assert.Empty(ChatSlotOrder.Plan(current, slots));
    }

    [Fact]
    public void Planned_moves_replayed_with_final_index_meaning_give_the_wanted_order()
    {
        var rnd = new Random(5);
        for (int round = 0; round < 200; round++)
        {
            int n = rnd.Next(1, 40);
            var slots = Enumerable.Range(0, n).Select(i => new Slot(i)).ToList();
            var wanted = slots.OrderBy(_ => rnd.Next()).ToList();
            var plan = ChatSlotOrder.Plan(new List<Slot>(slots), wanted);
            // Replay as the engine applies them: remove, then insert so the item ends at the index.
            var children = new List<Slot>(slots);
            foreach (var (item, index) in plan)
            {
                children.Remove(item);
                children.Insert(index, item);
            }

            Assert.Equal(wanted, children);
            Assert.True(plan.Count < n || n == 1 || plan.Count <= n - 1);
        }
    }

    // ---- the history file and the save schedule ----------------------------------------------------------

    [Fact]
    public void History_file_round_trips_every_field_and_replaces_the_file_whole()
    {
        var log = new ChatLog();
        log.Add(new ChatRecord { Kind = ChatKind.WhisperTo, FromSelf = true, User = "Ä \"q\"", Text = "über <b>x</b>\n2 ✓", Full = "<color=#fff>A</color>", TimeUtc = T0, Source = ChatLinkSource.Server }, true);
        log.Add(new ChatRecord { Kind = ChatKind.System, Text = "restart", TimeUtc = T0.AddSeconds(1) }, true);
        log.Add(new ChatRecord { Kind = ChatKind.System, Text = "restart", TimeUtc = T0.AddSeconds(2) }, true);
        log.Add(new ChatRecord { Kind = ChatKind.Session, Text = "note", Transient = true, TimeUtc = T0.AddSeconds(3) }, false);

        string dir = Path.Combine(Path.GetTempPath(), "progenitor-chat-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(dir, "sub", "server.jsonl");
            File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(dir, "sub")).FullName, "server.jsonl"), "old\n");
            ChatHistoryFile.WriteAtomic(path, log.Snapshot(100));
            Assert.False(File.Exists(path + ".tmp"));
            var back = ChatLog.Deserialize(File.ReadAllText(path), 100);
            Assert.Equal(2, back.Count); // the transient note is never saved
            Assert.Equal(ChatKind.WhisperTo, back[0].Kind);
            Assert.True(back[0].FromSelf);
            Assert.Equal("Ä \"q\"", back[0].User);
            Assert.Equal("über <b>x</b>\n2 ✓", back[0].Text);
            Assert.Equal("<color=#fff>A</color>", back[0].Full);
            Assert.Equal(T0, back[0].TimeUtc);
            Assert.Equal(ChatLinkSource.Server, back[0].Source);
            Assert.Equal(2, back[1].Repeat);
            Assert.All(back, r => Assert.True(r.Restored));
            Assert.Equal(log.Serialize(100), File.ReadAllText(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* temp */ }
        }
    }

    [Fact]
    public void Saves_wait_for_quiet_but_never_longer_than_the_cap()
    {
        var s = new ChatSaveSchedule();
        Assert.False(s.Due(100));
        s.MarkDirty(0);
        Assert.False(s.Due(ChatSaveSchedule.QuietSeconds - 0.1));
        Assert.True(s.Due(ChatSaveSchedule.QuietSeconds));
        s.Saved();
        Assert.False(s.Dirty);

        // A line every second: due at the cap, not before.
        double t = 100;
        for (; t < 100 + ChatSaveSchedule.MaxDelaySeconds - 1; t += 1)
        {
            s.MarkDirty(t);
            Assert.False(s.Due(t));
        }

        s.MarkDirty(100 + ChatSaveSchedule.MaxDelaySeconds);
        Assert.True(s.Due(100 + ChatSaveSchedule.MaxDelaySeconds));
    }

    // ---- the budget: 5,000 lines under active chat -------------------------------------------------------

    private static double Ms(Action a, int runs = 3)
    {
        a(); // warm up
        double best = double.MaxValue;
        for (int i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            a();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
        }

        return best;
    }

    [Fact]
    public void A_new_line_checks_each_kept_tab_once_whatever_the_history_size()
    {
        var log = Full();
        var tabs = new[] { ChatTab.All(), ChatTab.Clan(), new ChatTab { Name = "W", Scope = ChatScope.Whispers }, new ChatTab { Name = "M", Scope = ChatScope.Mentions } };
        foreach (var t in tabs) log.Filtered(t);
        long before = log.FilterChecks;
        for (int i = 0; i < 100; i++) log.Add(Line(10000 + i), true);
        Assert.Equal(100 * tabs.Length, log.FilterChecks - before);

        // Switching between tabs already seen checks nothing; a tab seen for the first time scans once.
        before = log.FilterChecks;
        for (int i = 0; i < 50; i++) log.Filtered(tabs[i % tabs.Length]);
        Assert.Equal(0, log.FilterChecks - before);
        log.Filtered(new ChatTab { Name = "L", Scope = ChatScope.Local });
        Assert.Equal(log.Records.Count, log.FilterChecks - before);
    }

    [Fact]
    public void Ingest_tab_switch_and_save_stay_within_budget_at_5000_lines()
    {
        var log = Full();
        var tabs = new[] { ChatTab.All(), ChatTab.Clan(), new ChatTab { Name = "W", Scope = ChatScope.Whispers }, new ChatTab { Name = "M", Scope = ChatScope.Mentions } };
        foreach (var t in tabs) log.Filtered(t);
        var window = new ChatScrollWindow(120, 40);
        int next = 20000;

        // 100 new lines on a full log (each drops the oldest), with the drawn window resolved after each, as the view does.
        double ingest = Ms(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                log.Add(Line(next++), true);
                window.Resolve(log.Filtered(tabs[0]));
            }
        });
        // Switching tabs (each already seen), resolving the window.
        double tabSwitch = Ms(() =>
        {
            foreach (var t in tabs) window.Resolve(log.Filtered(t));
        });
        // The first look at a new tab: one scan of 5,000 lines.
        double firstScan = Ms(() => log.Filtered(new ChatTab { Name = "x" + next++, Scope = ChatScope.Local | ChatScope.Clan }));
        // Taking the save's copy on the main thread, and writing it (a background thread in the game).
        double snapshot = Ms(() => log.Snapshot(log.Capacity));
        var lines = log.Snapshot(log.Capacity);
        string text = "";
        double write = Ms(() => text = ChatHistoryFile.ToText(lines));
        double read = Ms(() => ChatLog.Deserialize(text, ChatLog.MaxCapacity));

        _out.WriteLine($"5000 lines: 100 ingests {ingest:0.000} ms, tab switch {tabSwitch:0.000} ms, first scan {firstScan:0.000} ms, "
            + $"snapshot {snapshot:0.000} ms, write {write:0.0} ms ({text.Length / 1024} KB), read {read:0.0} ms");
        Assert.Equal(5000, log.Records.Count);
        // Generous bounds: measured locally at a small fraction of each.
        Assert.True(ingest < 20, "100 ingests took " + ingest + " ms");
        Assert.True(tabSwitch < 5, "a tab switch took " + tabSwitch + " ms");
        Assert.True(firstScan < 25, "a first tab scan took " + firstScan + " ms");
        Assert.True(snapshot < 25, "the save snapshot took " + snapshot + " ms");
    }
}
