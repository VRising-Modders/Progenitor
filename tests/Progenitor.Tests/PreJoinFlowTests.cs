using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Install;
using Progenitor.Plugins;
using Xunit;

namespace Progenitor.Tests;

/// <summary>AUTOUPDATE1 review fixes: the pre-join state machine, versions, server keys and the connected-server block.</summary>
public sealed class PreJoinFlowTests
{
    private sealed class FakeHost : IPreJoinHost
    {
        public readonly List<string> Calls = new();
        public PreJoinQuestion? Shown;
        public Action<string>? Answer;
        public Action? Drop;
        public bool AskWorks = true;
        public bool CanRestart = true;
        public bool RestartWorks = true;
        public Action<IReadOnlyList<PreparedUpdate>, UpdateRunEnd>? Done;
        public Action<bool, string>? Loaded;
        public bool BeginWorks = true;

        public bool Ask(PreJoinQuestion question, Action<string> answered, Action dropped)
        {
            Calls.Add("ask " + question.Kind);
            if (!AskWorks) return false;
            Shown = question;
            Answer = answered;
            Drop = dropped;
            return true;
        }

        public void Launch(object connect, string why) => Calls.Add("launch " + connect + " (" + why + ")");

        public bool RestartAndJoin(object connect, out string error)
        {
            Calls.Add("restart-and-join " + connect);
            error = RestartWorks ? "" : "no relauncher";
            return RestartWorks;
        }

        public bool CanRestartAndJoin(object connect) => CanRestart;

        public bool BeginUpdate(IReadOnlyList<string> packages, Action<IReadOnlyList<PreparedUpdate>, UpdateRunEnd> done, out string message)
        {
            Calls.Add("update " + string.Join(",", packages));
            Done = done;
            message = BeginWorks ? "" : "an update is already running";
            return BeginWorks;
        }

        public void WaitLoaded(IReadOnlyList<HotWrite> writes, Action<bool, string> then)
        {
            Calls.Add("wait " + string.Join(",", writes.Select(w => w.PluginId)));
            Loaded = then;
        }

        public void Log(string line) => Calls.Add("log " + line);

        public bool Writing;
        public readonly List<Action> Later = new();
        public bool UpdateWriting => Writing;
        public void After(double seconds, Action action) => Later.Add(action);

        /// <summary>Run what was scheduled (one round).</summary>
        public void Tick()
        {
            var now = Later.ToList();
            Later.Clear();
            foreach (var a in now) a();
        }
    }

    [Fact]
    public void A_join_waits_while_an_update_is_being_written_then_is_decided_as_usual()
    {
        var host = new FakeHost { Writing = true };
        var flow = new PreJoinFlow(host);
        int decided = 0;
        Assert.False(flow.OnLaunch("A", "s", () => { decided++; return new PreJoinDecision { Why = "nothing" }; }));
        Assert.Equal("waiting", flow.State);
        Assert.Equal(0, decided);
        // A second launch while waiting is ignored, the first stays held.
        Assert.False(flow.OnLaunch("B", "s", () => Offer()));
        host.Tick();
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("launch", StringComparison.Ordinal));
        host.Writing = false;
        host.Tick();
        Assert.Equal(1, decided);
        Assert.Contains("launch A (the update finished)", host.Calls);
        Assert.False(flow.Holding);

        // With something to offer after the wait, the prompt opens for the held join.
        host.Writing = true;
        Assert.False(flow.OnLaunch("C", "s2", () => Offer("s2")));
        host.Writing = false;
        host.Tick();
        Assert.Equal(PreJoinFlow.KindPrompt, host.Shown!.Kind);
        Assert.True(flow.Holding);
    }

    [Fact]
    public void Update_first_waits_for_a_running_update_instead_of_failing()
    {
        var host = new FakeHost();
        var flow = new PreJoinFlow(host);
        Assert.False(flow.OnLaunch("A", "s", () => Offer()));
        host.Writing = true;
        host.Answer!(PreJoinFlow.KeyMain);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("update ", StringComparison.Ordinal));
        Assert.Equal("updating", flow.State);
        host.Writing = false;
        host.Tick();
        Assert.Contains("update Team-Chat", host.Calls);
    }

    private static PreJoinDecision Offer(string server = "steamipv4://127.0.0.1:9976")
    {
        var item = new UpdateItem { Package = "Team-Chat", Name = "Chat", Installed = "1.0.0", Latest = "1.1.0", PluginIds = new[] { "team.chat" } };
        var report = new UpdateReport(new[] { item }, Array.Empty<UpdateSkip>(), 1, DateTime.UtcNow, "");
        return PreJoinOffers.Decide(report, null, UpdateMode.Automatic, true, server, null);
    }

    private static PreparedUpdate Prepared(string outcome, bool hot = false)
    {
        var p = new PreparedUpdate { Package = "Team-Chat", Name = "Chat", From = "1.0.0", To = "1.1.0", Outcome = outcome };
        if (hot) p.HotWrites.Add(new HotWrite("team.chat", "x.dll", "Chat"));
        return p;
    }

    [Fact]
    public void A_join_with_an_offer_is_held_and_join_anyway_launches_it_once()
    {
        var host = new FakeHost();
        var flow = new PreJoinFlow(host);
        Assert.False(flow.OnLaunch("A", "s", () => Offer()));
        Assert.True(flow.Holding);
        Assert.Equal(PreJoinFlow.KindPrompt, host.Shown!.Kind);
        Assert.Equal("Join anyway", host.Shown.ExtraCaption);
        host.Answer!(PreJoinFlow.KeyExtra);
        Assert.Contains("launch A (join anyway)", host.Calls);
        Assert.False(flow.Holding);
        // The same offer to the same server is not asked again this session.
        Assert.True(flow.OnLaunch("A", "s", () => PreJoinOffers.Decide(
            new UpdateReport(Offer().Offers.Select(o => o.Item).ToList(), Array.Empty<UpdateSkip>(), 1, DateTime.UtcNow, ""),
            null, UpdateMode.Automatic, true, "steamipv4://127.0.0.1:9976", flow.Dismissed)));
    }

    [Fact]
    public void Nothing_to_offer_lets_the_join_through()
    {
        var host = new FakeHost();
        var flow = new PreJoinFlow(host);
        Assert.True(flow.OnLaunch("A", "s", () => new PreJoinDecision { Why = "nothing" }));
        Assert.False(flow.Holding);
    }

    [Fact]
    public void Cancel_and_a_dropped_prompt_release_the_hold_and_launch_nothing()
    {
        var host = new FakeHost();
        var flow = new PreJoinFlow(host);
        flow.OnLaunch("A", "s", () => Offer());
        host.Answer!(PreJoinFlow.KeyCancel);
        Assert.False(flow.Holding);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("launch", StringComparison.Ordinal));

        flow.OnLaunch("B", "s", () => Offer());
        host.Drop!();
        Assert.False(flow.Holding);
        Assert.Equal("idle", flow.State);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("launch", StringComparison.Ordinal));
        // The next join asks again: nothing is stuck.
        Assert.False(flow.OnLaunch("C", "s", () => Offer()));
        Assert.True(flow.Holding);
    }

    [Fact]
    public void A_prompt_that_could_not_open_lets_the_join_through()
    {
        var host = new FakeHost { AskWorks = false };
        var flow = new PreJoinFlow(host);
        Assert.True(flow.OnLaunch("A", "s", () => Offer()));
        Assert.False(flow.Holding);
    }

    [Fact]
    public void A_second_launch_while_asking_is_ignored_and_a_stale_hold_is_replaced()
    {
        var host = new FakeHost();
        var flow = new PreJoinFlow(host);
        flow.OnLaunch("A", "s", () => Offer());
        Assert.False(flow.OnLaunch("B", "s", () => Offer()));
        Assert.Single(host.Calls, c => c == "ask " + PreJoinFlow.KindPrompt);

        // The failure dialog was replaced by another dialog with no callback at all (not even the drop): the hold would
        // be stale. A new launch replaces it instead of being swallowed.
        host.Answer!(PreJoinFlow.KeyMain);
        host.Done!(new[] { new PreparedUpdate { Package = "Team-Chat", Name = "Chat", Error = "offline" } }, UpdateRunEnd.NothingPrepared);
        Assert.Equal(PreJoinFlow.KindFailed, host.Shown!.Kind);
        // Simulate a dialog lost without a drop callback.
        typeof(PreJoinFlow).GetProperty("State")!.SetValue(flow, "idle");
        Assert.False(flow.OnLaunch("C", "s", () => Offer()));
        Assert.Contains(host.Calls, c => c.Contains("stale held join was replaced"));
        Assert.Equal(PreJoinFlow.KindPrompt, host.Shown!.Kind);
    }

    [Fact]
    public void Update_first_offline_asks_join_anyway_or_cancel_and_never_connects_by_itself()
    {
        var host = new FakeHost();
        var flow = new PreJoinFlow(host);
        flow.OnLaunch("A", "s", () => Offer());
        host.Answer!(PreJoinFlow.KeyMain);
        Assert.Equal("updating", flow.State);
        Assert.False(flow.OnLaunch("B", "s", () => Offer()));
        host.Done!(new[] { new PreparedUpdate { Package = "Team-Chat", Name = "Chat", Error = "the index could not be read" } }, UpdateRunEnd.NothingPrepared);
        Assert.Equal(PreJoinFlow.KindFailed, host.Shown!.Kind);
        Assert.Contains("could not update", host.Shown.Body);
        host.Answer!(PreJoinFlow.KeyCancel);
        Assert.False(flow.Holding);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("launch", StringComparison.Ordinal));
    }

    [Fact]
    public void Cancelling_the_new_dependency_question_returns_to_the_prompt()
    {
        var host = new FakeHost();
        var flow = new PreJoinFlow(host);
        flow.OnLaunch("A", "s", () => Offer());
        host.Answer!(PreJoinFlow.KeyMain);
        host.Done!(new[] { Prepared("") }, UpdateRunEnd.Cancelled);
        Assert.Equal(PreJoinFlow.KindPrompt, host.Shown!.Kind);
        Assert.True(flow.Holding);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("launch", StringComparison.Ordinal));
    }

    [Fact]
    public void A_hot_update_is_launched_only_after_the_new_image_loaded()
    {
        var host = new FakeHost();
        var flow = new PreJoinFlow(host);
        flow.OnLaunch("A", "s", () => Offer());
        host.Answer!(PreJoinFlow.KeyMain);
        host.Done!(new[] { Prepared("loaded", hot: true) }, UpdateRunEnd.Applied);
        Assert.Contains("wait team.chat", host.Calls);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("launch", StringComparison.Ordinal));
        host.Loaded!(true, "");
        Assert.Contains("launch A (updated and loaded)", host.Calls);
    }

    [Fact]
    public void A_hot_update_that_did_not_load_asks_before_joining()
    {
        var host = new FakeHost();
        var flow = new PreJoinFlow(host);
        flow.OnLaunch("A", "s", () => Offer());
        host.Answer!(PreJoinFlow.KeyMain);
        host.Done!(new[] { Prepared("loaded", hot: true) }, UpdateRunEnd.Applied);
        host.Loaded!(false, "Not loaded after 15 seconds: Chat.");
        Assert.Equal(PreJoinFlow.KindNotLoaded, host.Shown!.Kind);
        host.Answer!(PreJoinFlow.KeyMain);
        Assert.Contains("launch A (join before the update loaded)", host.Calls);
    }

    [Fact]
    public void A_restart_update_offers_restart_and_join_join_with_the_current_version_or_cancel()
    {
        var host = new FakeHost();
        var flow = new PreJoinFlow(host);
        flow.OnLaunch("A", "s", () => Offer());
        host.Answer!(PreJoinFlow.KeyMain);
        host.Done!(new[] { Prepared("restart") }, UpdateRunEnd.Applied);
        Assert.Equal(PreJoinFlow.KindRestart, host.Shown!.Kind);
        Assert.Equal("Restart and join", host.Shown.MainCaption);
        Assert.Equal("Join with the current version", host.Shown.ExtraCaption);
        Assert.Contains("loads at the next start", host.Shown.Body);
        host.Answer!(PreJoinFlow.KeyMain);
        Assert.Contains("restart-and-join A", host.Calls);
        Assert.False(flow.Holding);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("launch", StringComparison.Ordinal));

        var host2 = new FakeHost { CanRestart = false };
        var flow2 = new PreJoinFlow(host2);
        flow2.OnLaunch("B", "s", () => Offer());
        host2.Answer!(PreJoinFlow.KeyMain);
        host2.Done!(new[] { Prepared("restart") }, UpdateRunEnd.Applied);
        Assert.Equal("Join with the current version", host2.Shown!.MainCaption);
        Assert.Equal("", host2.Shown.ExtraCaption);
        host2.Answer!(PreJoinFlow.KeyMain);
        Assert.Contains("launch B (join with the current version)", host2.Calls);
    }

    [Fact]
    public void Unload_drops_a_held_join_and_says_so()
    {
        var host = new FakeHost();
        var flow = new PreJoinFlow(host);
        flow.OnLaunch("A", "s", () => Offer());
        flow.Unload();
        Assert.False(flow.Holding);
        Assert.Contains(host.Calls, c => c.Contains("dropped because Core is reloading"));
        // A late answer from the dead dialog does nothing.
        host.Answer!(PreJoinFlow.KeyExtra);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("launch", StringComparison.Ordinal));
    }

    // ---- versions, keys, connected-server block ----

    [Theory]
    [InlineData("1.2.0", "1.2.0-beta", true)]
    [InlineData("1.2.0-beta", "1.2.0", false)]
    [InlineData("1.2.0", "1.2", false)]
    [InlineData("1.2.1", "1.2", true)]
    [InlineData("1.10.0", "1.9.9", true)]
    [InlineData("v2.0", "1.9", true)]
    [InlineData("1.2.0+build5", "1.2.0", false)]
    [InlineData("1.2.0-rc2", "1.2.0-rc1", true)]
    public void Update_versions_order_numbers_then_releases_over_prereleases(string candidate, string installed, bool newer) =>
        Assert.Equal(newer, UpdateVersions.IsNewer(candidate, installed));

    [Fact]
    public void Server_keys_follow_the_transport()
    {
        Assert.Equal("steamipv4://127.0.0.1:9976", ServerRequirementMemory.KeyFor("SteamIPv4", "127.0.0.1", 9976, 0, null));
        Assert.Equal("lidgren://127.0.0.1:9976", ServerRequirementMemory.KeyFor("Lidgren://", "127.0.0.1", 9976, 0, null));
        Assert.Equal("steamp2p://76561198000000001", ServerRequirementMemory.KeyFor("SteamP2P", "", 0, 76561198000000001UL, null));
        Assert.Equal("eosp2p://abc", ServerRequirementMemory.KeyFor("EosP2P", "", 0, 0, "abc"));
        Assert.Equal("", ServerRequirementMemory.KeyFor("SteamP2P", "", 0, 0, ""));
    }

    [Fact]
    public void Every_plugin_of_a_package_caps_it()
    {
        var item = new UpdateItem { Package = "Team-Pack", Name = "Pack", Installed = "1.0.0", Latest = "1.1.0", PluginIds = new[] { "team.a", "team.b" } };
        var reqs = new[] { new ServerRequirement { ModId = "TEAM.B", Version = "1.0.0", Rule = NetworkVersionRule.Exact } };
        Assert.Equal("the server you are on needs exactly 1.0.0", PreJoinOffers.BlockedBy(item, reqs));
        Assert.Equal("", PreJoinOffers.BlockedBy(item, null));
        var minor = new[] { new ServerRequirement { ModId = "team.a", Version = "1.1.5", Rule = NetworkVersionRule.Minor } };
        Assert.Equal("", PreJoinOffers.BlockedBy(item, minor));
    }
}
