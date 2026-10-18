using System.Collections.Generic;
using System.Linq;
using Progenitor.Plugins;
using Progenitor.Reload;
using Xunit;

namespace Progenitor.Tests;

/// <summary>Hot-load chat lines (docs/hot-reload.md, "Hot-load messages"): coalescing, text and startup quiet. Game-free.</summary>
public sealed class HotLoadNoticeTests
{
    private const string Core = "2026.10.85";

    private static readonly System.DateTime Now = new(2026, 10, 17, 10, 0, 30, System.DateTimeKind.Utc);

    private static string Line(string outcome, string reason = "watch", string stamp = "2026-10-17T10:00:00.0000000Z") =>
        "lastReload gen=2 outcome=" + outcome + " reason=" + reason + " timestamp=" + stamp;

    private static HotLoadCoreStatus Status(string outcome, string timestamp, string reason = "watch", bool inFlight = false) =>
        new() { Outcome = outcome, Timestamp = timestamp, Reason = reason, InFlight = inFlight };

    private static readonly HotLoadCoreStatus Idle = Status("none", "");

    /// <summary>A process past its start: Satisvampory 2.0.0 and HuntsAndRifts 1.1.0 loaded at start, quiet since.</summary>
    private static HotLoadNotices Running(out double now)
    {
        var m = new HotLoadNotices(0, Core, carried: null);
        m.Loaded("satis", "Satisvampory", "2.0.0", 1, cascade: false, silent: false);
        m.Loaded("hunts", "HuntsAndRifts", "1.1.0", 1.5, cascade: false, silent: false);
        Assert.Null(m.Poll(10, Idle));
        Assert.Null(m.Poll(HotLoadNotices.StartupMinSeconds + 1, Idle));
        Assert.False(m.InStartup);
        now = 30;
        return m;
    }

    private static void Swap(HotLoadNotices m, string id, string name, string version, double now)
    {
        m.Unloaded(id, name, "", PluginUnloadReason.Swap, now, cascade: false, silent: false);
        m.Loaded(id, name, version, now, cascade: false, silent: false);
    }

    [Fact]
    public void The_loads_at_process_start_are_not_announced_and_teach_the_versions()
    {
        var m = new HotLoadNotices(0, Core, carried: null);
        Assert.True(m.InStartup);
        m.Loaded("satis", "Satisvampory", "2.0.0", 0.5, cascade: false, silent: false);
        // A dependency that waited loads late, still at start.
        m.Loaded("dep", "Dependency", "1.0.0", 12, cascade: false, silent: false);
        for (double t = 1; t < 40; t += 0.25) Assert.Null(m.Poll(t, Idle));
        Assert.False(m.InStartup);
        Assert.Equal("2.0.0", m.KnownVersion("satis"));

        Swap(m, "satis", "Satisvampory", "2.0.0", 41);
        Assert.Null(m.Poll(41.5, Idle));
        Assert.Equal("Satisvampory reloaded (2.0.0).", m.Poll(42.1, Idle));
        Assert.Null(m.Poll(43, Idle));
    }

    [Fact]
    public void Startup_lasts_until_the_loads_were_quiet_for_a_while()
    {
        var m = new HotLoadNotices(0, Core, carried: null);
        m.Loaded("a", "A", "1.0.0", 18, cascade: false, silent: false);
        Assert.Null(m.Poll(HotLoadNotices.StartupMinSeconds + 1, Idle));
        Assert.True(m.InStartup);
        Assert.Null(m.Poll(18 + HotLoadNotices.StartupQuietSeconds + 0.1, Idle));
        Assert.False(m.InStartup);
    }

    [Fact]
    public void Each_change_reads_as_one_short_sentence()
    {
        var m = Running(out double t);
        Swap(m, "satis", "Satisvampory", "2.0.1", t);
        Assert.Equal("Satisvampory updated to 2.0.1.", m.Poll(t + 1.1, Idle));

        m.Unloaded("hunts", "HuntsAndRifts", "1.1.0", PluginUnloadReason.Removal, t + 5, cascade: false, silent: false);
        Assert.Equal("HuntsAndRifts removed.", m.Poll(t + 6.1, Idle));
        Assert.Null(m.KnownVersion("hunts"));

        m.Loaded("new", "NewMod", "0.3.0", t + 10, cascade: false, silent: false);
        Assert.Equal("NewMod added (0.3.0).", m.Poll(t + 11.1, Idle));
    }

    [Fact]
    public void A_burst_becomes_one_line_after_it_settles()
    {
        var m = Running(out double t);
        Swap(m, "satis", "Satisvampory", "2.0.0", t);
        Assert.Null(m.Poll(t + 0.6, Idle));
        Swap(m, "hunts", "HuntsAndRifts", "1.2.0", t + 0.8);
        Assert.Null(m.Poll(t + 1.5, Idle));
        Assert.Equal("Satisvampory reloaded (2.0.0). HuntsAndRifts updated to 1.2.0.", m.Poll(t + 1.9, Idle));
        Assert.False(m.Pending);
    }

    [Fact]
    public void A_failed_reload_and_a_rollback_say_so()
    {
        var m = Running(out double t);
        m.Unloaded("satis", "Satisvampory", "2.0.0", PluginUnloadReason.Swap, t, cascade: false, silent: false);
        m.Failed("satis", "Satisvampory", "2.0.1", t, cascade: false, silent: false);
        Assert.Equal("Satisvampory failed to reload.", m.Poll(t + 1.1, Idle));

        // The automatic update's rollback: the new build throws, the previous one is put back.
        var r = Running(out t);
        r.Unloaded("satis", "Satisvampory", "2.0.0", PluginUnloadReason.Swap, t, cascade: false, silent: false);
        r.Failed("satis", "Satisvampory", "2.0.1", t, cascade: false, silent: false);
        r.Unloaded("satis", "Satisvampory", "2.0.1", PluginUnloadReason.Swap, t + 0.5, cascade: false, silent: false);
        r.Loaded("satis", "Satisvampory", "2.0.0", t + 0.5, cascade: false, silent: false);
        Assert.Equal("Satisvampory failed to update; the previous version keeps running.", r.Poll(t + 1.6, Idle));

        var n = Running(out t);
        n.Failed("brand", "BrandNew", "1.0.0", t, cascade: false, silent: false);
        Assert.Equal("BrandNew failed to load.", n.Poll(t + 1.1, Idle));
    }

    [Fact]
    public void A_per_server_pause_a_join_and_process_exit_are_not_hot_loads()
    {
        var m = Running(out double t);
        m.Unloaded("satis", "Satisvampory", "2.0.0", PluginUnloadReason.Suspend, t, cascade: false, silent: false);
        m.Loaded("satis", "Satisvampory", "2.0.0", t + 3, cascade: false, silent: false);
        // The join's own version swap.
        m.Unloaded("hunts", "HuntsAndRifts", "1.1.0", PluginUnloadReason.Swap, t + 4, cascade: false, silent: true);
        m.Loaded("hunts", "HuntsAndRifts", "1.0.0", t + 4, cascade: false, silent: true);
        Assert.Null(m.Poll(t + 10, Idle));
        Assert.False(m.Pending);
        Assert.Equal("1.0.0", m.KnownVersion("hunts"));

        m.Unloaded("satis", "Satisvampory", "2.0.0", PluginUnloadReason.Shutdown, t + 20, cascade: false, silent: false);
        m.Unloaded("hunts", "HuntsAndRifts", "1.0.0", PluginUnloadReason.Removal, t + 20, cascade: false, silent: false);
        Assert.Null(m.Poll(t + 30, Idle));
    }

    [Fact]
    public void A_Core_swap_is_one_line_with_the_plugins_whose_version_changed()
    {
        var old = Running(out double t);
        // The swap's cascade in the outgoing generation: unloads say nothing.
        old.Unloaded("satis", "Satisvampory", "2.0.0", PluginUnloadReason.Swap, t, cascade: true, silent: false);
        old.Unloaded("hunts", "HuntsAndRifts", "1.1.0", PluginUnloadReason.Swap, t, cascade: true, silent: false);
        string carried = old.Save(t);
        Assert.Null(old.Poll(t + 10, Status("scheduled", "a", inFlight: true)));

        var m = new HotLoadNotices(100, Core, carried);
        Assert.False(m.InStartup);
        m.Loaded("satis", "Satisvampory", "2.0.0", 100.2, cascade: true, silent: false);
        m.Loaded("hunts", "HuntsAndRifts", "1.1.0", 100.2, cascade: true, silent: false);
        Assert.Null(m.Poll(101.5, Status("scheduled", "a", inFlight: true)));
        // Settled, but a Core swap waits so plugins a deploy copies after it join the line.
        Assert.Null(m.Poll(102, Status("ok", "b")));
        Swap(m, "dev", "Progenitor Dev", Core, 103);
        Assert.Null(m.Poll(104.5, Status("ok", "b")));
        Assert.Equal("Progenitor reloaded (" + Core + "). Progenitor Dev added (" + Core + ").", m.Poll(105.1, Status("ok", "b")));
    }

    [Fact]
    public void A_Core_update_names_the_new_version_and_a_restore_says_it_failed()
    {
        var old = Running(out double t);
        string carried = old.Save(t).Replace("core\t" + Core, "core\t2026.10.84");

        var m = new HotLoadNotices(100, Core, carried);
        m.Loaded("satis", "Satisvampory", "2.0.1", 100.3, cascade: true, silent: false);
        m.Loaded("hunts", "HuntsAndRifts", "1.1.0", 100.3, cascade: true, silent: false);
        Assert.Equal("Progenitor updated to " + Core + ". Satisvampory updated to 2.0.1.", m.Poll(106, Status("ok", "b")));

        var restored = new HotLoadNotices(100, "2026.10.84", carried);
        Assert.Equal("Progenitor failed to update; the previous version keeps running.", restored.Poll(106, Status("restored", "b")));
    }

    [Fact]
    public void A_refused_swap_in_the_running_generation_is_said_once()
    {
        var m = Running(out double t);
        Assert.Null(m.Poll(t, Status("none", "")));
        // The swap unloads and puts back the same builds before it refuses.
        m.Unloaded("satis", "Satisvampory", "2.0.0", PluginUnloadReason.Swap, t + 1, cascade: true, silent: false);
        m.Loaded("satis", "Satisvampory", "2.0.0", t + 1.5, cascade: true, silent: false);
        Assert.Null(m.Poll(t + 1.6, Status("refused", "t1")));
        Assert.Equal("Progenitor failed to update; the previous version keeps running.", m.Poll(t + 7, Status("refused", "t1")));
        // The watcher's retry of the same staged image: nothing new to say.
        Assert.Null(m.Poll(t + 20, Status("refused", "t2")));
        Assert.Null(m.Poll(t + 30, Status("refused", "t2")));
        // Scheduled and ok are not failures; a dry run is a developer's check.
        Assert.Null(m.Poll(t + 40, Status("failed", "t3", reason: "dryRun")));
        Assert.Null(m.Poll(t + 50, Status("scheduled", "t4")));
        Assert.Null(m.Poll(t + 60, Status("none", "t4")));
    }

    [Fact]
    public void A_batch_cut_short_by_a_Core_swap_is_said_by_the_next_generation()
    {
        var old = Running(out double t);
        Swap(old, "satis", "Satisvampory", "2.0.1", t);
        string carried = old.Save(t + 0.2);

        var m = new HotLoadNotices(100, Core, carried);
        Assert.Equal("Satisvampory updated to 2.0.1. Progenitor reloaded (" + Core + ").", m.Poll(106, Status("ok", "b")));
        Assert.Equal("2.0.1", m.KnownVersion("satis"));
    }

    [Fact]
    public void The_status_line_parses_with_spaces_in_the_reason()
    {
        var s = HotLoadCoreStatus.Parse("lastReload gen=3 outcome=ok reason=self-update 2026.10.85 now timestamp=2026-10-17T10:00:00.0000000Z", false, Now)!;
        Assert.Equal("ok", s.Outcome);
        Assert.Equal("self-update 2026.10.85 now", s.Reason);
        Assert.Equal("2026-10-17T10:00:00.0000000Z", s.Timestamp);
        Assert.False(s.InFlight);
        Assert.True(HotLoadCoreStatus.Parse("lastReload gen=3 outcome=scheduled reason= timestamp=", false, Now)!.InFlight);
        Assert.Null(HotLoadCoreStatus.Parse("nonsense", false, Now));
    }

    [Fact]
    public void A_long_list_is_cut_with_a_count()
    {
        var sentences = Enumerable.Range(1, 30).Select(i => "SomeLongModName" + i + " reloaded (1.0." + i + ").").ToList();
        string line = HotLoadNoticeText.Join(sentences)!;
        Assert.True(line.Length <= HotLoadNoticeText.MaxChars, line.Length.ToString());
        Assert.EndsWith(" more mods changed.", line);
        Assert.Null(HotLoadNoticeText.Join(new List<string>()));
        Assert.Equal("A removed.", HotLoadNoticeText.Join(new[] { "A removed." }));
    }

    [Theory]
    [InlineData("Server hot load: Satisvampory reloaded (2.0.0).", true)]
    [InlineData("Server hot load: Progenitor updated to 2026.10.85. Progenitor Dev reloaded (2026.10.85).", true)]
    [InlineData("Server hot load: HuntsAndRifts removed.", true)]
    [InlineData("Server hot load: Satisvampory failed to update; the previous version keeps running.", true)]
    [InlineData("Server hot load: A reloaded (1.0.0). 3 more mods changed.", true)]
    [InlineData("Satisvampory reloaded (2.0.0).", false)]
    // Another mod's server notice that happens to read like one is left alone.
    [InlineData("Server: HuntsAndRifts removed.", false)]
    [InlineData("Castle removed.", false)]
    [InlineData("Server hot load: restarting in 5 minutes.", false)]
    [InlineData("Server hot load: Satisvampory reloaded (2.0.0)", false)]
    [InlineData("", false)]
    public void The_client_recognizes_a_servers_hot_load_line(string text, bool expected) =>
        Assert.Equal(expected, HotLoadNoticeText.IsServerNotice(text));

    [Fact]
    public void Every_line_the_model_makes_is_recognized_as_a_server_line()
    {
        var m = Running(out double t);
        Swap(m, "satis", "Satisvampory", "2.0.1", t);
        m.Unloaded("hunts", "HuntsAndRifts", "1.1.0", PluginUnloadReason.Removal, t, cascade: false, silent: false);
        m.Failed("x", "X Mod", "1.0.0", t, cascade: false, silent: false);
        string line = m.Poll(t + 2, Idle)!;
        Assert.True(HotLoadNoticeText.IsServerNotice(HotLoadNoticeText.ServerLine(line)), line);
    }

    [Fact]
    public void A_scheduled_status_is_the_cascade_even_when_the_probe_reads_idle()
    {
        // CoreLoader.IsReloading reads false while the swap unloads and reloads plugins; the status line still says scheduled.
        Assert.True(HotLoadCoreStatus.IsCascade(false, Line("scheduled"), Now));
        Assert.True(HotLoadCoreStatus.IsCascade(true, null, Now));
        Assert.False(HotLoadCoreStatus.IsCascade(false, Line("ok"), Now));
        Assert.False(HotLoadCoreStatus.IsCascade(false, null, Now));
        // A scheduled status that never got its result does not silence plugin events for good.
        Assert.False(HotLoadCoreStatus.IsCascade(false, Line("scheduled", stamp: "2026-10-17T09:50:00.0000000Z"), Now));

        // The decision fed into a whole Core deploy: no "failed to reload", no "reloaded" per plugin.
        var old = Running(out double t);
        bool cascade = HotLoadCoreStatus.IsCascade(false, Line("scheduled"), Now);
        old.Unloaded("satis", "Satisvampory", "2.0.0", PluginUnloadReason.Swap, t, cascade, silent: false);
        old.Unloaded("hunts", "HuntsAndRifts", "1.1.0", PluginUnloadReason.Swap, t, cascade, silent: false);
        var m = new HotLoadNotices(100, Core, old.Save(t));
        cascade = HotLoadCoreStatus.IsCascade(false, Line("scheduled"), Now);
        m.Loaded("satis", "Satisvampory", "2.0.0", 100.2, cascade, silent: false);
        m.Loaded("hunts", "HuntsAndRifts", "1.1.0", 100.2, cascade, silent: false);
        Assert.Null(m.Poll(101, HotLoadCoreStatus.Parse(Line("scheduled"), false, Now)));
        var done = HotLoadCoreStatus.Parse(Line("ok", stamp: "2026-10-17T10:00:20.0000000Z"), false, Now);
        Assert.Equal("Progenitor reloaded (" + Core + ").", m.Poll(106, done));
    }

    [Fact]
    public void A_line_waits_while_a_join_runs_but_not_on_a_server_without_Progenitor()
    {
        var m = Running(out double t);
        Swap(m, "satis", "Satisvampory", "2.0.1", t);
        Assert.Null(m.Poll(t + 2, Idle, holdForJoin: true));
        Assert.Null(m.Poll(t + 20, Idle, holdForJoin: true));
        Assert.Equal("Satisvampory updated to 2.0.1.", m.Poll(t + 21, Idle, holdForJoin: false));

        Assert.True(HotLoadNoticeText.JoinInProgress(inWorld: true, connectionHeld: true, handshakeResult: null, serverWithoutProgenitor: false));
        Assert.False(HotLoadNoticeText.JoinInProgress(true, true, "ok", false));
        // A vanilla server never answers the handshake: once that is known, the client is not joining any more.
        Assert.False(HotLoadNoticeText.JoinInProgress(true, true, null, serverWithoutProgenitor: true));
        Assert.False(HotLoadNoticeText.JoinInProgress(false, true, null, false));
    }

    [Fact]
    public void Server_broadcasts_wait_for_their_gap_merge_by_mod_and_skip_recent_repeats()
    {
        var limit = new HotLoadBroadcastLimiter();
        limit.Offer(new[] { new HotLoadSentence("A", "A reloaded (1.0.0).") });
        Assert.Equal("A reloaded (1.0.0).", limit.Take(0));

        limit.Offer(new[] { new HotLoadSentence("A", "A reloaded (1.0.0).") });
        Assert.Null(limit.Take(10));
        // The same news inside three minutes: dropped.
        Assert.Null(limit.Take(31));
        Assert.False(limit.Pending);

        limit.Offer(new[] { new HotLoadSentence("A", "A updated to 2.0.0."), new HotLoadSentence("B", "B removed.") });
        limit.Offer(new[] { new HotLoadSentence("A", "A updated to 3.0.0.") });
        Assert.Equal("B removed. A updated to 3.0.0.", limit.Take(45));
        Assert.Null(limit.Take(60));

        limit.Offer(new[] { new HotLoadSentence("A", "A reloaded (1.0.0).") });
        Assert.Equal("A reloaded (1.0.0).", limit.Take(200));
    }

    [Fact]
    public void A_dry_run_swap_says_nothing()
    {
        var old = Running(out double t);
        var m = new HotLoadNotices(100, Core, old.Save(t));
        m.Loaded("satis", "Satisvampory", "2.0.0", 100.2, cascade: true, silent: false);
        var dry = HotLoadCoreStatus.Parse(Line("ok", reason: "core_reload dryRun", stamp: "2026-10-17T10:00:20.0000000Z"), false, Now);
        Assert.Null(m.Poll(106, dry));
        Assert.Null(m.Poll(110, dry));
        Assert.False(m.Pending);
    }

    [Fact]
    public void State_of_another_format_starts_as_at_process_start()
    {
        var m = new HotLoadNotices(0, Core, "v0\ncore\t2026.10.80\nknown\tsatis\tSatisvampory\t2.0.0\n");
        Assert.True(m.InStartup);
        Assert.Null(m.KnownVersion("satis"));
        Assert.Null(m.Poll(10, Idle));
    }
}
