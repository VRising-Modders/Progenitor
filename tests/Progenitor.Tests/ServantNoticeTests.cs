using System;
using System.Collections.Generic;
using Progenitor.Sample;
using Progenitor.Servants;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// ctx.Servants.SuppressGameNotices, game-free: the per-recipient decision (OR over every plugin's
/// filter, a throwing filter keeps the line), the kind of a finished-hunt line, and the handles.
/// The user type is an int here; the server passes the user entity.
/// </summary>
public class ServantNoticeTests
{
    private const int Alice = 1, Bob = 2, Carol = 3;

    [Fact]
    public void No_filter_keeps_every_line()
    {
        var f = new ServantNoticeFilters<int>();
        Assert.False(f.Any);
        foreach (var kind in new[] { ServantNoticeKind.Started, ServantNoticeKind.Returned, ServantNoticeKind.Killed })
            Assert.False(f.ShouldSuppress(Alice, kind));
        Assert.Equal(0, f.Suppressed);
    }

    [Fact]
    public void A_filter_drops_the_line_for_its_user_only()
    {
        var f = new ServantNoticeFilters<int>();
        var seen = new List<(int, ServantNoticeKind)>();
        f.Register((u, k) => { seen.Add((u, k)); return u == Alice && k == ServantNoticeKind.Returned; }, "mod.one");

        Assert.True(f.ShouldSuppress(Alice, ServantNoticeKind.Returned));
        Assert.False(f.ShouldSuppress(Bob, ServantNoticeKind.Returned));      // the clan mate still gets it
        Assert.False(f.ShouldSuppress(Alice, ServantNoticeKind.Killed));
        Assert.False(f.ShouldSuppress(Alice, ServantNoticeKind.Started));
        Assert.Equal(1, f.Suppressed);
        Assert.Equal(new[] { (Alice, ServantNoticeKind.Returned), (Bob, ServantNoticeKind.Returned), (Alice, ServantNoticeKind.Killed), (Alice, ServantNoticeKind.Started) }, seen);
    }

    [Fact]
    public void Filters_of_several_plugins_combine_with_or()
    {
        var f = new ServantNoticeFilters<int>();
        f.Register((u, _) => u == Alice, "mod.one");
        f.Register((u, k) => u == Bob && k == ServantNoticeKind.Started, "mod.two");

        Assert.True(f.ShouldSuppress(Alice, ServantNoticeKind.Killed));
        Assert.True(f.ShouldSuppress(Bob, ServantNoticeKind.Started));
        Assert.False(f.ShouldSuppress(Bob, ServantNoticeKind.Returned));
        Assert.False(f.ShouldSuppress(Carol, ServantNoticeKind.Started));
        Assert.Equal(2, f.Count);
    }

    [Fact]
    public void The_first_true_wins_and_later_filters_are_not_asked()
    {
        var f = new ServantNoticeFilters<int>();
        int secondCalls = 0;
        f.Register((_, _) => true, "mod.one");
        f.Register((_, _) => { secondCalls++; return false; }, "mod.two");
        Assert.True(f.ShouldSuppress(Alice, ServantNoticeKind.Returned));
        Assert.Equal(0, secondCalls);
    }

    [Fact]
    public void A_throwing_filter_keeps_the_line_logs_once_and_the_next_filter_still_decides()
    {
        var f = new ServantNoticeFilters<int>();
        var log = new List<string>();
        f.Log = log.Add;
        f.Register((_, _) => throw new InvalidOperationException("boom"), "mod.bad");
        Assert.False(f.ShouldSuppress(Alice, ServantNoticeKind.Returned));
        Assert.False(f.ShouldSuppress(Alice, ServantNoticeKind.Returned));
        Assert.Equal(2, f.Failures);
        Assert.Single(log, l => l.Contains("mod.bad") && l.Contains("boom"));

        f.Register((u, _) => u == Alice, "mod.good");
        Assert.True(f.ShouldSuppress(Alice, ServantNoticeKind.Returned));
        Assert.False(f.ShouldSuppress(Bob, ServantNoticeKind.Returned));
    }

    [Fact]
    public void Disposing_a_handle_removes_only_its_registration_and_twice_is_harmless()
    {
        var f = new ServantNoticeFilters<int>();
        var one = f.Register((u, _) => u == Alice, "mod.one");
        var same = f.Register((u, _) => u == Bob, "mod.one");   // one plugin may register several
        one.Dispose();
        one.Dispose();
        Assert.Equal(1, f.Count);
        Assert.False(f.ShouldSuppress(Alice, ServantNoticeKind.Started));
        Assert.True(f.ShouldSuppress(Bob, ServantNoticeKind.Started));
        same.Dispose();
        Assert.False(f.Any);
    }

    [Fact]
    public void Clear_drops_every_filter_and_old_handles_stay_harmless()
    {
        var f = new ServantNoticeFilters<int>();
        var h = f.Register((_, _) => true, "mod.one");
        f.Clear();
        Assert.False(f.Any);
        h.Dispose();
        var again = f.Register((_, _) => true, "mod.two");
        h.Dispose();
        Assert.True(f.ShouldSuppress(Alice, ServantNoticeKind.Killed));
        again.Dispose();
    }

    [Fact]
    public void Registration_arguments_are_checked()
    {
        var f = new ServantNoticeFilters<int>();
        Assert.Throws<ArgumentNullException>(() => f.Register(null!, "mod.one"));
        Assert.Throws<ArgumentException>(() => f.Register((_, _) => true, " "));
    }

    [Fact]
    public void A_finished_line_is_returned_on_success_and_killed_otherwise()
    {
        Assert.Equal(ServantNoticeKind.Returned, ServantNoticeFilters<int>.FinishedKind(true));
        Assert.Equal(ServantNoticeKind.Killed, ServantNoticeFilters<int>.FinishedKind(false));
    }

    [Fact]
    public void The_finished_events_success_flag_is_read_where_the_metadata_puts_it()
    {
        Assert.Equal(0x44, Layout.ServantMissionFinishedEventSuccessOffset);
        Assert.Equal(0x0, Layout.SendEventToUserUserIndexOffset);
    }

    [Fact]
    public void The_sample_parses_quiet_on_and_off()
    {
        Assert.True(SampleServants.TryParse("quiet", "on", null, null, out var on));
        Assert.Equal(SampleServants.Kind.Quiet, on.Kind);
        Assert.True(on.On);
        Assert.True(SampleServants.TryParse("quiet", "OFF", null, null, out var off));
        Assert.False(off.On);
        Assert.False(SampleServants.TryParse("quiet", "", null, null, out _));
        Assert.Contains("quiet on|off", SampleServants.UsageReply);
    }

    [Fact]
    public void Every_send_path_of_a_hunt_line_asks_the_filters()
    {
        string notices = SourcePin.ReadFile("src", "Progenitor.Core", "Servants", "ServantRuntime.Notices.cs");
        Assert.Contains("SystemHooks.Before<SerializeAndSendServerEventsSystem>(BeforeServerSend)", notices);
        Assert.Contains("ComponentIndex.Of<ServantMissionFinishedEvent>", notices);
        Assert.Contains("ComponentIndex.Of<ServantMissionStartedEvent>", notices);
        Assert.Contains("Notices.ShouldSuppress(user, kind)", notices);

        string chance = SourcePin.ReadFile("src", "Progenitor.Core", "Servants", "ServantRuntime.Chance.cs");
        Assert.Contains("Notices.ShouldSuppress(u, ServantNoticeKind.Started)", chance);

        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Servants", "ServantRuntime.cs");
        Assert.Contains("InstallNotices();", runtime);
        Assert.Contains("UninstallNotices();", runtime);

        string api = SourcePin.ReadFile("src", "Progenitor.Core", "Servants", "PluginServants.cs");
        Assert.Contains("_ctx.Track(handle);", api);
    }
}
