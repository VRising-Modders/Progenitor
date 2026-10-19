using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// BROWSERFILTER1 (docs/server-list-description.md, "Progenitor servers in Join Game"): the server-list tag a Progenitor
/// server publishes, the exact-token match the client applies to a row's tags, the filter pass over the shown list,
/// its change detection and fail-open behavior, and the server's tag publisher over a fake game.
/// </summary>
public class ServerBrowserFilterTests
{
    private static readonly DateTime Now = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);

    // ---- the marker -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_marker_is_one_lowercase_tag()
    {
        Assert.Equal("progenitor", ServerBrowserMarker.Tag);
        Assert.Equal(ServerBrowserMarker.Tag, ServerBrowserMarker.Tag.ToLowerInvariant());
        Assert.DoesNotContain(",", ServerBrowserMarker.Tag);
    }

    [Theory]
    [InlineData("ded,pvp,cs4,progenitor", true)]
    [InlineData("progenitor", true)]
    [InlineData("ded, Progenitor ,std", true)]
    [InlineData("ded,pvp,cs4", false)]
    [InlineData("notprogenitor,progenitor2,progenitor-mod", false)]
    [InlineData("", false)]
    public void Only_the_exact_tag_marks_a_row(string joined, bool marked)
    {
        var tags = joined.Length == 0 ? new List<string>() : joined.Split(',').ToList();
        Assert.Equal(marked, ServerBrowserMarker.IsMarked(tags));
    }

    [Fact]
    public void A_row_without_tags_is_not_marked()
    {
        Assert.False(ServerBrowserMarker.IsMarked(null));
        Assert.False(ServerBrowserMarker.IsMarked(new string?[] { null, "" }));
    }

    [Fact]
    public void The_description_signature_is_not_the_marker()
    {
        // The description is not part of a Steam row, and an admin can edit or imitate it: only the tag counts.
        Assert.False(ServerBrowserMarker.IsMarked(new[] { ServerListDescription.Signature }));
        Assert.False(ServerBrowserMarker.IsMarked(new[] { "Requires the Progenitor mod" }));
    }

    [Fact]
    public void Steam_tag_room_counts_the_commas()
    {
        Assert.True(ServerBrowserMarker.FitsSteam(new[] { "ded", "pvp", "cs4", "hc", "sea", "std", "dif_no", "sal_mi", "old" }));
        // 117 characters of tags + the comma + "progenitor" passes 127.
        var full = new List<string> { new string('a', 117) };
        Assert.False(ServerBrowserMarker.FitsSteam(full));
        var fits = new List<string> { new string('a', 115) };
        Assert.True(ServerBrowserMarker.FitsSteam(fits));
        Assert.True(ServerBrowserMarker.FitsSteam(null));
    }

    // ---- the count line ----------------------------------------------------------------------------------------------

    /// <summary>A stand-in for the font measurement: a fixed advance per character.</summary>
    private static Func<string, float> PerChar(float advance) => s => s.Length * advance;

    [Fact]
    public void Count_line_suffix_is_short()
    {
        Assert.Equal(" · Progenitor", ServerBrowserCountLine.Suffix);
        Assert.DoesNotContain("only", ServerBrowserCountLine.Suffix);
    }

    [Theory]
    // Live 2026.10.143's numbers, and larger counts.
    [InlineData("Servers: 0 (1,538 total)")]
    [InlineData("Servers: 1,234 (12,345 total)")]
    [InlineData("Servers: 12,345 (123,456 total)")]
    public void Count_line_adds_the_suffix_when_the_whole_line_fits(string game)
    {
        string marked = game + " · Progenitor";
        // Exactly as wide as the marked line: it fits.
        Assert.Equal(marked, ServerBrowserCountLine.Fit(game, marked.Length * 10f, PerChar(10f)));
        // Half a pixel of slack for rounding.
        Assert.Equal(marked, ServerBrowserCountLine.Fit(game, marked.Length * 10f - 0.4f, PerChar(10f)));
        // One character short: the game's own text, never a wrapped line.
        Assert.Equal(game, ServerBrowserCountLine.Fit(game, (marked.Length - 1) * 10f, PerChar(10f)));
    }

    [Fact]
    public void Count_line_never_marks_twice_and_replaces_the_old_suffix()
    {
        const string game = "Servers: 0 (1,538 total)";
        var measure = PerChar(10f);
        string once = ServerBrowserCountLine.Fit(game, 1000f, measure);
        Assert.Equal(game + " · Progenitor", once);
        Assert.Equal(once, ServerBrowserCountLine.Fit(once, 1000f, measure));
        // A line an earlier Core marked with the long suffix.
        Assert.Equal(once, ServerBrowserCountLine.Fit(game + " · Progenitor only", 1000f, measure));
        // Too narrow now: the earlier suffix comes off too.
        Assert.Equal(game, ServerBrowserCountLine.Fit(once, game.Length * 10f, measure));
        Assert.Equal(game, ServerBrowserCountLine.Base(game + " · Progenitor only"));
        Assert.Equal(game, ServerBrowserCountLine.Base(once));
    }

    [Fact]
    public void Count_line_leaves_the_game_text_without_a_measurement_or_width()
    {
        const string game = "Servers: 1,234 (12,345 total)";
        Assert.Equal(game, ServerBrowserCountLine.Fit(game, 1000f, null));
        Assert.Equal(game, ServerBrowserCountLine.Fit(game, -1f, PerChar(1f)));
        Assert.Equal(game, ServerBrowserCountLine.Fit(game, 0f, PerChar(1f)));
        Assert.Equal(game, ServerBrowserCountLine.Fit(game, float.NaN, PerChar(1f)));
        Assert.Equal(game, ServerBrowserCountLine.Fit(game, float.PositiveInfinity, PerChar(1f)));
        Assert.Equal(game, ServerBrowserCountLine.Fit(game, 1000f, _ => -1f));
        Assert.Equal(game, ServerBrowserCountLine.Fit(game, 1000f, _ => float.NaN));
        Assert.Equal(game, ServerBrowserCountLine.Fit(game, 1000f, _ => throw new InvalidOperationException()));
        Assert.Equal("", ServerBrowserCountLine.Fit("", 1000f, PerChar(1f)));
        Assert.Equal("", ServerBrowserCountLine.Fit(null, 1000f, PerChar(1f)));
    }

    // ---- the filter pass ---------------------------------------------------------------------------------------------

    private sealed class FakeRows : IServerBrowserRows
    {
        public readonly List<(string Name, List<string>? Tags)> Items = new();
        public bool ThrowOnRead;

        public FakeRows Add(string name, params string[] tags)
        {
            Items.Add((name, tags.Length == 0 ? null : tags.ToList()));
            return this;
        }

        public int Count => Items.Count;

        public bool MarkedAt(int index)
        {
            if (ThrowOnRead) throw new InvalidOperationException("row read failed");
            return ServerBrowserMarker.IsMarked(Items[index].Tags);
        }

        public void RemoveAt(int index) => Items.RemoveAt(index);

        public string[] Names => Items.Select(i => i.Name).ToArray();
    }

    private static FakeRows Mixed() => new FakeRows()
        .Add("Vanilla PvP", "ded", "pvp", "cs4")
        .Add("Progenitor Forms Test", "ded", "pvp", "progenitor")
        .Add("No tags")
        .Add("Fake", "progenitor-ish")
        .Add("Another Progenitor", "progenitor", "std");

    [Fact]
    public void A_pass_keeps_only_marked_rows_in_their_order()
    {
        var rows = Mixed();
        var pass = ServerBrowserFilterState.Filter(rows);
        Assert.Equal(new[] { "Progenitor Forms Test", "Another Progenitor" }, rows.Names);
        Assert.Equal(new ServerBrowserPass(5, 3, 2), pass);
    }

    [Fact]
    public void The_switch_off_changes_nothing()
    {
        var state = new ServerBrowserFilterState();
        var rows = Mixed();
        Assert.Equal(ServerBrowserFilterState.Next.None, state.Tick(false, new ServerBrowserStamp(1, 5, 2), rows, null));
        Assert.Equal(5, rows.Count);
    }

    [Fact]
    public void Turning_it_on_trims_and_asks_for_a_redraw_then_waits_for_the_next_rebuild()
    {
        var state = new ServerBrowserFilterState();
        var rows = Mixed();
        Assert.Equal(ServerBrowserFilterState.Next.Redraw, state.Tick(true, new ServerBrowserStamp(1, 5, 2), rows, null));
        Assert.Equal(2, rows.Count);
        state.Settled(new ServerBrowserStamp(1, 2, 2));

        // The game hands back the same trimmed list until it rebuilds: no pass.
        rows.Add("Vanilla added behind our back", "ded");
        Assert.Equal(ServerBrowserFilterState.Next.None, state.Tick(true, new ServerBrowserStamp(1, 2, 2), rows, null));
        Assert.Equal(3, rows.Count);
        Assert.Equal(1, state.Passes);

        // A rebuild (new refresh time) is filtered again.
        Assert.Equal(ServerBrowserFilterState.Next.Redraw, state.Tick(true, new ServerBrowserStamp(1, 3, 4), rows, null));
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, state.Passes);
    }

    [Fact]
    public void A_rebuild_into_a_new_list_object_is_filtered()
    {
        var state = new ServerBrowserFilterState();
        var rows = Mixed();
        state.Tick(true, new ServerBrowserStamp(1, 5, 2), rows, null);
        state.Settled(new ServerBrowserStamp(1, 2, 2));
        var sorted = Mixed();
        Assert.Equal(ServerBrowserFilterState.Next.Redraw, state.Tick(true, new ServerBrowserStamp(9, 5, 2), sorted, null));
        Assert.Equal(2, sorted.Count);
    }

    [Fact]
    public void An_all_progenitor_list_needs_no_redraw()
    {
        var state = new ServerBrowserFilterState();
        var rows = new FakeRows().Add("A", "progenitor").Add("B", "ded", "progenitor");
        Assert.Equal(ServerBrowserFilterState.Next.None, state.Tick(true, new ServerBrowserStamp(1, 2, 2), rows, null));
        Assert.Equal(new ServerBrowserPass(2, 0, 2), state.LastPass);
    }

    [Fact]
    public void Turning_it_off_asks_for_the_full_list_once()
    {
        var state = new ServerBrowserFilterState();
        var rows = Mixed();
        state.Tick(true, new ServerBrowserStamp(1, 5, 2), rows, null);
        Assert.Equal(ServerBrowserFilterState.Next.Restore, state.Tick(false, new ServerBrowserStamp(1, 2, 2), rows, null));
        Assert.Equal(ServerBrowserFilterState.Next.None, state.Tick(false, new ServerBrowserStamp(1, 5, 4), rows, null));
        Assert.Null(state.LastPass);
    }

    [Fact]
    public void A_read_failure_fails_open_and_logs_once()
    {
        var state = new ServerBrowserFilterState();
        var rows = Mixed();
        rows.ThrowOnRead = true;
        var logs = new List<string>();
        Assert.Equal(ServerBrowserFilterState.Next.Restore, state.Tick(true, new ServerBrowserStamp(1, 5, 2), rows, logs.Add));
        Assert.True(state.Failed);
        Assert.Contains("row read failed", state.FailReason);
        Assert.Single(logs);
        Assert.Contains("every server is shown", logs[0]);
        Assert.Equal(5, rows.Count);

        // Off for the session: later frames neither filter nor log again, even with the switch still on.
        rows.ThrowOnRead = false;
        Assert.Equal(ServerBrowserFilterState.Next.None, state.Tick(true, new ServerBrowserStamp(1, 5, 4), rows, logs.Add));
        Assert.Equal(5, rows.Count);
        Assert.Single(logs);
    }

    [Fact]
    public void No_list_yet_is_not_a_failure()
    {
        var state = new ServerBrowserFilterState();
        Assert.Equal(ServerBrowserFilterState.Next.None, state.Tick(true, default, null, null));
        Assert.False(state.Failed);
        // Once the list exists it is filtered.
        Assert.Equal(ServerBrowserFilterState.Next.Redraw, state.Tick(true, new ServerBrowserStamp(1, 5, 2), Mixed(), null));
    }

    [Fact]
    public void Forget_refilters_the_next_list()
    {
        var state = new ServerBrowserFilterState();
        var rows = Mixed();
        state.Tick(true, new ServerBrowserStamp(1, 5, 2), rows, null);
        state.Settled(new ServerBrowserStamp(1, 2, 2));
        state.Forget();
        rows.Add("Vanilla", "ded");
        Assert.Equal(ServerBrowserFilterState.Next.Redraw, state.Tick(true, new ServerBrowserStamp(1, 2, 2), rows, null));
        Assert.Equal(2, rows.Count);
    }

    // ---- the server's tag publisher ----------------------------------------------------------------------------------

    private sealed class FakeTagGame : IServerTagGame
    {
        public bool IsReady = true;
        public readonly List<string> List = new() { "ded", "pvp", "cs4", "dif_no" };
        public int Adds;
        public int Removes;
        public bool Throw;

        public bool Ready => IsReady;

        public IReadOnlyList<string>? Tags => IsReady ? List.ToList() : null;

        public void Add(string tag)
        {
            if (Throw) throw new InvalidOperationException("no system");
            Adds++;
            // The game's SetServerGameTag: blank or duplicate tags are ignored.
            if (!string.IsNullOrWhiteSpace(tag) && !List.Contains(tag)) List.Add(tag);
        }

        public void Remove(string tag)
        {
            Removes++;
            List.Remove(tag);
        }
    }

    [Fact]
    public void The_server_adds_the_tag_once_the_game_is_ready()
    {
        var game = new FakeTagGame { IsReady = false };
        var p = new ServerBrowserTagPublisher();
        Assert.Null(p.Tick(Now, true, game));
        Assert.Equal("waiting", p.Status);
        Assert.Null(p.Tick(Now.AddMilliseconds(500), true, game));
        Assert.Equal(0, game.Adds);

        game.IsReady = true;
        string? line = p.Tick(Now.AddSeconds(1), true, game);
        Assert.Contains("\"progenitor\" server-list tag is published", line);
        Assert.Equal("added", p.Status);
        Assert.Equal(new[] { "ded", "pvp", "cs4", "dif_no", "progenitor" }, game.List);
        Assert.True(p.FitsSteam);
    }

    [Fact]
    public void A_present_tag_is_left_alone_and_checked_each_minute()
    {
        var game = new FakeTagGame();
        var p = new ServerBrowserTagPublisher();
        p.Tick(Now, true, game);
        Assert.Null(p.Tick(Now.AddSeconds(30), true, game));
        Assert.Equal(1, game.Adds);
        Assert.Null(p.Tick(Now.AddSeconds(61), true, game));
        Assert.Equal("present", p.Status);
        Assert.Equal(1, game.Adds);

        // Something removed it (another mod): the next minute's check puts it back.
        game.List.Remove("progenitor");
        Assert.NotNull(p.Tick(Now.AddSeconds(122), true, game));
        Assert.Contains("progenitor", game.List);
    }

    [Fact]
    public void Turning_the_cfg_switch_off_removes_the_tag_at_once()
    {
        var game = new FakeTagGame();
        var p = new ServerBrowserTagPublisher();
        p.Tick(Now, true, game);
        string? line = p.Tick(Now.AddSeconds(1), false, game);
        Assert.Contains("removed", line);
        Assert.DoesNotContain("progenitor", game.List);
        Assert.Equal("removed", p.Status);
        Assert.Null(p.Tick(Now.AddSeconds(70), false, game));
        Assert.Equal("absent", p.Status);
        Assert.Equal(1, game.Removes);
    }

    [Fact]
    public void A_failed_publish_is_reported_and_retried_a_minute_later()
    {
        var game = new FakeTagGame { Throw = true };
        var p = new ServerBrowserTagPublisher();
        string? line = p.Tick(Now, true, game);
        Assert.Contains("could not be published", line);
        Assert.StartsWith("failed:", p.Status);
        Assert.Null(p.Tick(Now.AddSeconds(30), true, game));
        game.Throw = false;
        Assert.NotNull(p.Tick(Now.AddSeconds(61), true, game));
        Assert.Equal("added", p.Status);
    }

    [Fact]
    public void A_crowded_tag_string_is_published_with_a_warning()
    {
        var game = new FakeTagGame();
        game.List.Clear();
        game.List.AddRange(new[] { "pvp", "dif_no", new string('x', 110) });
        var p = new ServerBrowserTagPublisher();
        string? line = p.Tick(Now, true, game);
        Assert.Contains("Steam may cut them", line);
        Assert.False(p.FitsSteam);
        Assert.Contains("progenitor", game.List);
    }

    [Fact]
    public void The_tag_waits_for_the_games_own_tags()
    {
        var game = new FakeTagGame();
        game.List.Clear();
        var p = new ServerBrowserTagPublisher();
        // No startup tags yet (FinalizeSetupServer has not run): wait.
        Assert.Null(p.Tick(Now, true, game));
        Assert.Equal("waiting", p.Status);

        // Startup tags, but no activity-level dif_* tag yet: wait up to ActivityTagWait.
        game.List.AddRange(new[] { "ded", "pve", "cs4" });
        Assert.Null(p.Tick(Now.AddSeconds(1), true, game));
        Assert.Null(p.Tick(Now.AddSeconds(30), true, game));
        Assert.Equal(0, game.Adds);

        // The game's dif_* arrives: ours goes after it.
        game.List.Add("dif_br");
        Assert.NotNull(p.Tick(Now.AddSeconds(31), true, game));
        Assert.Equal("progenitor", game.List[^1]);
    }

    [Fact]
    public void Without_a_difficulty_tag_the_tag_is_added_after_the_wait()
    {
        var game = new FakeTagGame();
        game.List.Clear();
        game.List.AddRange(new[] { "ded", "pvp" });
        var p = new ServerBrowserTagPublisher();
        Assert.Null(p.Tick(Now, true, game));
        Assert.Null(p.Tick(Now + ServerBrowserTagPublisher.ActivityTagWait - TimeSpan.FromSeconds(1), true, game));
        Assert.NotNull(p.Tick(Now + ServerBrowserTagPublisher.ActivityTagWait, true, game));
        Assert.Contains("progenitor", game.List);
    }

    [Fact]
    public void A_redraw_failure_fails_open_once()
    {
        var state = new ServerBrowserFilterState();
        var rows = Mixed();
        state.Tick(true, new ServerBrowserStamp(1, 5, 2), rows, null);
        var logs = new List<string>();
        Assert.True(state.Fail("redraw threw", logs.Add));
        Assert.False(state.Fail("again", logs.Add));
        Assert.True(state.Failed);
        Assert.Single(logs);
        Assert.Contains("every server is shown", logs[0]);
        // Off for the session even with the switch on.
        Assert.Equal(ServerBrowserFilterState.Next.None, state.Tick(true, new ServerBrowserStamp(1, 5, 4), Mixed(), null));
    }
}
