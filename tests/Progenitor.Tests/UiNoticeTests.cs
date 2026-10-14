using System;
using System.Collections.Generic;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// P7 <c>ctx.Hud.Notify</c>: the game-free notice queue (<see cref="UiNoticeQueue"/>) and the framework band's look
/// (<see cref="UiNoticeBand"/>). FIFO across owners, one on screen at a time, dedupe window, per-owner rate limit,
/// bounded queue, drop on unload, waiting for a busy game banner, and the band's width, fades and kind accents.
/// </summary>
public sealed class UiNoticeTests
{
    private readonly UiNoticeQueue _queue = new();
    private readonly object _a = new();
    private readonly object _b = new();
    private readonly List<string> _presented = new();

    private double Present(UiNoticeHandle n)
    {
        _presented.Add(n.Header);
        return 3.0;
    }

    [Fact]
    public void Notices_show_one_at_a_time_in_posting_order_across_owners()
    {
        var first = _queue.Enqueue(_a, "First", "", UiNoticeKind.Info, 0, 0);
        var second = _queue.Enqueue(_b, "Second", "", UiNoticeKind.Info, 0, 0.1);

        Assert.Same(first, _queue.Next(0.2, false, Present));
        Assert.Equal(UiNoticeState.Showing, first.State);
        Assert.Equal(UiNoticeState.Queued, second.State);

        Assert.Null(_queue.Next(3.0, false, Present)); // first still holds the screen (ends at 3.2)
        Assert.Null(_queue.Next(3.3, false, Present)); // first ended; the gap runs
        Assert.Equal(UiNoticeState.Done, first.State);
        Assert.Same(second, _queue.Next(3.3 + UiNoticeRules.GapSeconds, false, Present));
        Assert.Equal(new[] { "First", "Second" }, _presented);
    }

    [Fact]
    public void The_same_text_within_the_window_is_dropped_as_a_duplicate()
    {
        var one = _queue.Enqueue(_a, "Saved", "3 containers", UiNoticeKind.Info, 0, 0);
        var dup = _queue.Enqueue(_b, "Saved", "3 containers", UiNoticeKind.Success, 0, 1);
        var otherDetail = _queue.Enqueue(_a, "Saved", "4 containers", UiNoticeKind.Info, 0, 1);
        var later = _queue.Enqueue(_a, "Saved", "3 containers", UiNoticeKind.Info, 0, UiNoticeRules.DedupeWindowSeconds + 0.1);

        Assert.Equal(UiNoticeState.Queued, one.State);
        Assert.Equal(UiNoticeState.Dropped, dup.State);
        Assert.Equal("duplicate", ((UiNoticeHandle)dup).DropReason);
        Assert.Equal(UiNoticeState.Queued, otherDetail.State);
        Assert.Equal(UiNoticeState.Queued, later.State);
    }

    [Fact]
    public void An_owner_over_its_rate_limit_is_dropped_and_others_are_not()
    {
        for (int i = 0; i < UiNoticeRules.RateLimitCount; i++)
            Assert.Equal(UiNoticeState.Queued, _queue.Enqueue(_a, "n" + i, "", UiNoticeKind.Info, 0, i * 0.1).State);

        var over = _queue.Enqueue(_a, "too many", "", UiNoticeKind.Info, 0, 1.0);
        Assert.Equal(UiNoticeState.Dropped, over.State);
        Assert.Equal("rate-limited", over.DropReason);
        Assert.Equal(UiNoticeState.Queued, _queue.Enqueue(_b, "other owner", "", UiNoticeKind.Info, 0, 1.0).State);

        // The window slides: once the first posts are old enough, the owner may post again.
        Assert.Equal(UiNoticeState.Queued,
            _queue.Enqueue(_a, "again", "", UiNoticeKind.Info, 0, UiNoticeRules.RateLimitWindowSeconds + 0.05).State);
    }

    [Fact]
    public void A_full_queue_drops_its_oldest_waiting_notice()
    {
        var owners = new List<object>();
        var posted = new List<UiNoticeHandle>();
        for (int i = 0; i < UiNoticeRules.MaxQueued; i++)
        {
            var owner = new object();
            owners.Add(owner);
            posted.Add(_queue.Enqueue(owner, "n" + i, "", UiNoticeKind.Info, 0, 0));
        }

        var extra = _queue.Enqueue(new object(), "newest", "", UiNoticeKind.Info, 0, 0);
        Assert.Equal(UiNoticeState.Queued, extra.State);
        Assert.Equal(UiNoticeState.Dropped, posted[0].State);
        Assert.Equal("queue-full", posted[0].DropReason);
        Assert.Equal(UiNoticeRules.MaxQueued, _queue.QueuedCount);
    }

    [Fact]
    public void Unloading_an_owner_drops_its_queued_notices_and_ends_its_showing_one()
    {
        var showing = _queue.Enqueue(_a, "A1", "", UiNoticeKind.Info, 0, 0);
        var queuedA = _queue.Enqueue(_a, "A2", "", UiNoticeKind.Info, 0, 0);
        var queuedB = _queue.Enqueue(_b, "B1", "", UiNoticeKind.Info, 0, 0);
        _queue.Next(0, false, Present);
        var withdrawn = new List<UiNoticeHandle>();
        _queue.Withdrawn += withdrawn.Add;

        Assert.Equal(2, _queue.DropOwner(_a));

        Assert.Equal(UiNoticeState.Done, showing.State);
        Assert.Equal(UiNoticeState.Dropped, queuedA.State);
        Assert.Equal("owner-unloaded", queuedA.DropReason);
        Assert.Equal(UiNoticeState.Queued, queuedB.State);
        Assert.Equal(new[] { (UiNoticeHandle)showing }, withdrawn);
        Assert.Same(queuedB, _queue.Next(0.1, false, Present)); // screen freed at once; no gap after a withdraw
    }

    [Fact]
    public void Dispose_drops_a_queued_notice_and_ends_a_showing_one()
    {
        var showing = _queue.Enqueue(_a, "one", "", UiNoticeKind.Info, 0, 0);
        var queued = _queue.Enqueue(_a, "two", "", UiNoticeKind.Info, 0, 0);
        _queue.Next(0, false, Present);

        queued.Dispose();
        Assert.Equal(UiNoticeState.Dropped, queued.State);
        showing.Dispose();
        Assert.Equal(UiNoticeState.Done, showing.State);
        Assert.False(_queue.HasWork);
        showing.Dispose(); // idempotent
        Assert.Equal(UiNoticeState.Done, showing.State);
    }

    [Fact]
    public void A_busy_game_banner_or_hidden_HUD_is_never_overwritten_the_notice_waits()
    {
        var n = _queue.Enqueue(_a, "waits", "", UiNoticeKind.Info, 0, 0);
        Assert.Null(_queue.Next(0, true, Present));
        Assert.Null(_queue.Next(UiNoticeRules.MaxWaitSeconds - 0.1, true, Present));
        Assert.Equal(UiNoticeState.Queued, n.State);
        Assert.Empty(_presented);
        Assert.Same(n, _queue.Next(UiNoticeRules.MaxWaitSeconds - 0.05, false, Present)); // the banner freed up
    }

    [Fact]
    public void A_notice_posted_while_the_games_full_screen_menu_hides_the_HUD_shows_when_the_menu_closes()
    {
        var expired = new List<UiNoticeHandle>();
        _queue.Expired += expired.Add;
        var n = _queue.Enqueue(_a, "while the menu is open", "", UiNoticeKind.Info, 0, 0);
        // The menu stays open three times the ordinary wait: the notice waits and does not expire.
        for (double t = 0; t <= UiNoticeRules.MaxWaitSeconds * 3; t += 1.0)
            Assert.Null(_queue.Next(t, true, Present, 0, menuHold: true));
        Assert.Equal(UiNoticeState.Queued, n.State);
        Assert.Empty(expired);
        // The menu closes: it shows.
        Assert.Same(n, _queue.Next(UiNoticeRules.MaxWaitSeconds * 3 + 1, false, Present));
        Assert.Equal(new[] { "while the menu is open" }, _presented);
    }

    [Fact]
    public void The_menu_or_the_workspace_in_a_world_holds_the_banner_and_a_short_settle_follows()
    {
        Assert.True(UiNoticeRules.MenuHolds(gameMenuOpen: true, workspaceOpen: false, inWorld: true));
        Assert.True(UiNoticeRules.MenuHolds(gameMenuOpen: false, workspaceOpen: true, inWorld: true));
        Assert.False(UiNoticeRules.MenuHolds(gameMenuOpen: false, workspaceOpen: true, inWorld: false)); // title: the band
        Assert.False(UiNoticeRules.MenuHolds(false, false, true));

        var n = _queue.Enqueue(_a, "after the menu", "", UiNoticeKind.Info, 0, 0);
        // The HUD reads visible (the live case): only the menu hold keeps the banner from playing unseen.
        Assert.Null(_queue.Next(1.0, false, Present, 0, menuHold: true));
        Assert.Null(_queue.Next(5.0, false, Present, 0, menuHold: true));
        Assert.Empty(_presented);
        // The menu closes at 5: the queue settles before it shows.
        Assert.Null(_queue.Next(5.0 + UiNoticeRules.MenuSettleSeconds - 0.1, false, Present, 0, menuHold: false));
        Assert.Equal(UiNoticeState.Queued, n.State);
        Assert.Same(n, _queue.Next(5.0 + UiNoticeRules.MenuSettleSeconds + 0.05, false, Present, 0, menuHold: false));
        Assert.Equal(new[] { "after the menu" }, _presented);
    }

    [Fact]
    public void A_notice_held_by_the_menu_still_expires_after_the_hold_limit_and_a_hidden_HUD_after_the_wait()
    {
        var expired = new List<UiNoticeHandle>();
        _queue.Expired += expired.Add;
        var held = _queue.Enqueue(_a, "held", "", UiNoticeKind.Info, 0, 0);
        for (double t = 0; t <= UiNoticeRules.MaxHeldSeconds + 1; t += 5.0)
            _queue.Next(t, true, Present, 0, menuHold: true);
        Assert.Equal(UiNoticeState.Dropped, held.State);
        // The player's own hidden HUD (no menu): the ordinary 30 s wait.
        var hidden = _queue.Enqueue(_b, "hidden", "", UiNoticeKind.Info, 0, 1000);
        _queue.Next(1000 + UiNoticeRules.MaxWaitSeconds + 0.5, true, Present, 0, menuHold: false);
        Assert.Equal(UiNoticeState.Dropped, hidden.State);
        Assert.Equal(2, expired.Count);
    }

    [Fact]
    public void A_notice_that_waits_too_long_expires_unshown_and_is_reported()
    {
        var expired = new List<UiNoticeHandle>();
        _queue.Expired += expired.Add;
        var old = _queue.Enqueue(_a, "old", "", UiNoticeKind.Info, 0, 0);
        var young = _queue.Enqueue(_b, "young", "", UiNoticeKind.Info, 0, UiNoticeRules.MaxWaitSeconds - 1);

        Assert.Null(_queue.Next(UiNoticeRules.MaxWaitSeconds, true, Present));
        Assert.Equal(UiNoticeState.Dropped, old.State);
        Assert.Equal("expired", old.DropReason);
        Assert.Equal(new[] { old }, expired);
        Assert.Equal(UiNoticeState.Queued, young.State);
        Assert.Empty(_presented);
    }

    [Fact]
    public void Notices_still_waiting_when_the_world_changes_are_dropped()
    {
        var expired = new List<UiNoticeHandle>();
        _queue.Expired += expired.Add;
        var showing = _queue.Enqueue(_a, "showing", "", UiNoticeKind.Info, 0, 0, epoch: 5);
        var waiting = _queue.Enqueue(_a, "waiting", "", UiNoticeKind.Info, 0, 0, epoch: 5);
        Assert.Same(showing, _queue.Next(0, false, Present, epoch: 5));

        var posted = _queue.Enqueue(_b, "new world", "", UiNoticeKind.Info, 0, 1, epoch: 6);
        Assert.Null(_queue.Next(1, false, Present, epoch: 6));
        Assert.Equal("world-changed", waiting.DropReason);
        Assert.Equal(new[] { waiting }, expired);
        Assert.Equal(UiNoticeState.Showing, showing.State); // what is on screen finishes its slot
        Assert.Null(_queue.Next(3.0, false, Present, epoch: 6)); // the slot ends; the gap runs
        Assert.Same(posted, _queue.Next(3.0 + UiNoticeRules.GapSeconds, false, Present, epoch: 6));
    }

    [Fact]
    public void The_band_ground_comes_from_the_kit_palette()
    {
        Assert.Equal(MenuPalette.PaneFill.R, UiNoticeBand.Ground.R);
        Assert.Equal(MenuPalette.PaneFill.G, UiNoticeBand.Ground.G);
        Assert.Equal(MenuPalette.PaneFill.B, UiNoticeBand.Ground.B);
        Assert.InRange(UiNoticeBand.Ground.A, 0.5f, 1f);
    }

    [Fact]
    public void A_notice_nothing_could_present_is_dropped_and_the_next_one_goes()
    {
        var bad = _queue.Enqueue(_a, "bad", "", UiNoticeKind.Info, 0, 0);
        var good = _queue.Enqueue(_b, "good", "", UiNoticeKind.Info, 0, 0);
        Assert.Null(_queue.Next(0, false, _ => 0));
        Assert.Equal(UiNoticeState.Dropped, bad.State);
        Assert.Equal("not-presented", bad.DropReason);
        Assert.Same(good, _queue.Next(0, false, Present));
        Assert.Null(_queue.Next(0, false, _ => throw new InvalidOperationException())); // nothing queued; a throw would be caught too
    }

    [Fact]
    public void Empty_text_is_dropped_and_a_detail_only_notice_becomes_its_header()
    {
        Assert.Equal("empty", _queue.Enqueue(_a, "  ", "", UiNoticeKind.Info, 0, 0).DropReason);
        var detailOnly = _queue.Enqueue(_a, "", "just a detail", UiNoticeKind.Info, 0, 0);
        Assert.Equal("just a detail", detailOnly.Header);
        Assert.Equal("", detailOnly.Detail);
    }

    [Fact]
    public void Text_is_one_line_and_cut_with_an_ellipsis()
    {
        Assert.Equal("a b c", UiNoticeRules.Clean(" a\nb\r\nc ", 50));
        string cut = UiNoticeRules.Clean(new string('x', 300), UiNoticeRules.MaxHeaderChars);
        Assert.Equal(UiNoticeRules.MaxHeaderChars, cut.Length);
        Assert.EndsWith("…", cut);
    }

    [Fact]
    public void Clear_empties_everything()
    {
        var a = _queue.Enqueue(_a, "a", "", UiNoticeKind.Info, 0, 0);
        var b = _queue.Enqueue(_a, "b", "", UiNoticeKind.Info, 0, 0);
        _queue.Next(0, false, Present);
        _queue.Clear();
        Assert.Equal(UiNoticeState.Done, a.State);
        Assert.Equal(UiNoticeState.Dropped, b.State);
        Assert.False(_queue.HasWork);
        // The dedupe memory is cleared too.
        Assert.Equal(UiNoticeState.Queued, _queue.Enqueue(_a, "a", "", UiNoticeKind.Info, 0, 0.1).State);
    }

    [Fact]
    public void Inert_notices_are_already_dropped()
    {
        var inert = UiNoticeHandle.Inert("server");
        Assert.Equal(UiNoticeState.Dropped, inert.State);
        inert.Dispose();
        Assert.Equal(UiNoticeState.Dropped, inert.State);
    }

    [Theory]
    [InlineData(0, 4.0, 4.0)]
    [InlineData(-3, 5.5, 5.5)]
    [InlineData(0, 0, UiNoticeRules.DefaultSeconds)]
    [InlineData(0.2, 4.0, UiNoticeRules.MinSeconds)]
    [InlineData(999, 4.0, UiNoticeRules.MaxSeconds)]
    [InlineData(double.NaN, 4.0, 4.0)]
    public void Seconds_default_to_the_presenter_and_are_clamped(double requested, double presenterDefault, double expected)
    {
        Assert.Equal(expected, UiNoticeRules.Seconds(requested, presenterDefault), 6);
    }

    [Fact]
    public void Band_width_fits_the_text_between_its_limits()
    {
        Assert.Equal(UiNoticeBand.MinWidth, UiNoticeBand.Width(50, 0));
        Assert.Equal(600f + 2 * UiNoticeBand.PadX, UiNoticeBand.Width(600, 400));
        Assert.Equal(600f + 2 * UiNoticeBand.PadX, UiNoticeBand.Width(200, 600));
        Assert.Equal(UiNoticeBand.MaxWidth, UiNoticeBand.Width(5000, 0));
        Assert.Equal(UiNoticeBand.MinWidth, UiNoticeBand.Width(float.NaN, -3));
        Assert.True(UiNoticeBand.Height(true) > UiNoticeBand.Height(false));
    }

    [Fact]
    public void Band_fades_in_and_out_with_its_notice()
    {
        Assert.Equal(0f, UiNoticeBand.Alpha(0, 0, 4));
        Assert.Equal(1f, UiNoticeBand.Alpha(2, 0, 4));
        Assert.InRange(UiNoticeBand.Alpha(0.1, 0, 4), 0.4f, 0.6f);
        Assert.InRange(UiNoticeBand.Alpha(4 - UiNoticeBand.FadeOutSeconds / 2, 0, 4), 0.4f, 0.6f);
        Assert.Equal(0f, UiNoticeBand.Alpha(4.1, 0, 4));
        Assert.Equal(0f, UiNoticeBand.Alpha(1, double.NaN, 4));
    }

    [Fact]
    public void Band_ground_is_clear_at_both_ends_and_solid_in_the_middle()
    {
        Assert.Equal(0f, UiNoticeBand.GroundAlpha(0f));
        Assert.Equal(0f, UiNoticeBand.GroundAlpha(1f));
        Assert.Equal(1f, UiNoticeBand.GroundAlpha(0.5f));
        Assert.Equal(1f, UiNoticeBand.GroundAlpha(UiNoticeBand.FadeFraction));
        Assert.True(UiNoticeBand.GroundAlpha(0.05f) < UiNoticeBand.GroundAlpha(0.1f));
        Assert.Equal(UiNoticeBand.GroundAlpha(0.07f), UiNoticeBand.GroundAlpha(0.93f), 4);
        Assert.Equal(0f, UiNoticeBandGlyph.Alpha(-1));
        Assert.Equal(1f, UiNoticeBandGlyph.Alpha(UiNoticeBandGlyph.Width / 2));
    }

    [Fact]
    public void Kinds_tint_the_band_rules_and_info_keeps_the_quiet_gold()
    {
        var info = UiNoticeBand.RuleColor(UiNoticeKind.Info);
        Assert.Equal(MenuPalette.Gold.R, info.R);
        Assert.Equal(UiNoticeBand.RuleAlpha, info.A);
        var colors = new HashSet<(float, float, float)>();
        foreach (UiNoticeKind kind in Enum.GetValues(typeof(UiNoticeKind)))
        {
            var c = UiNoticeBand.Accent(kind);
            colors.Add((c.R, c.G, c.B));
        }

        Assert.Equal(4, colors.Count);
        Assert.Equal(UiNoticeBand.KindRuleAlpha, UiNoticeBand.RuleColor(UiNoticeKind.Error).A);
    }
}
