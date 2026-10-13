using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Bootstrap;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;
using ModRecord = Progenitor.Networking.Handshake.ModRecord;

namespace Progenitor.Tests;

/// <summary>
/// Per-server mod pause: the planner, the coordinator driven through a fake Bootstrap backend
/// (which uses Bootstrap's real closure policy), the no-Progenitor watch, the profile file,
/// Bootstrap's suspend table, and the deny / Mods-tab text. Game-free.
/// </summary>
[Collection("Handshake")]
public sealed class ServerModPauseTests : IDisposable
{
    private readonly string _dir;

    public ServerModPauseTests()
    {
        ServerModPause.ResetForTests();
        ServerProfiles.ResetForTests();
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-pause-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ServerProfiles.TestDirectory = _dir;
    }

    public void Dispose()
    {
        ServerModPause.ResetForTests();
        ServerProfiles.TestDirectory = null;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ------------------------------------------------------------------ fakes

    private static ModRecord Smh(string guid, string version = "1.0.0") =>
        ModRecord.CreateDeclared(guid, version, NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor);

    private static ModRecord Optional(string guid, string version = "1.0.0") =>
        ModRecord.CreateDeclared(guid, version, NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor);

    internal sealed class FakePlugin
    {
        internal string Guid = "";
        internal string Dll = "";
        internal List<ModRecord> Rows = new();
        internal string[] Deps = Array.Empty<string>();
        internal bool Reloadable = true;
        internal bool Unsafe;
        internal bool Loaded = true;
        /// <summary>Its previous plugin ALC is still alive: a resume stays pending until cleared.</summary>
        internal bool AlcAlive;
    }

    /// <summary>
    /// Stands in for Bootstrap: loaded reload DLLs and their rows, with Bootstrap's real
    /// <see cref="PluginSuspendTable"/> holding the paused and resuming units.
    /// </summary>
    internal sealed class FakeBackend : IServerModPauseBackend
    {
        internal readonly List<FakePlugin> Plugins = new();
        internal readonly List<ModRecord> Unowned = new();
        internal readonly List<string> Calls = new();
        internal readonly PluginSuspendTable Table = new();
        internal bool IsSupported = true;
        internal bool? Hosting = false;
        public string PendingRequest { get; set; } = "";
        public string ResumeReports { get; set; } = "";
        internal string Connection = "203.0.113.5:9876@1";
        public string ConnectionIdentity() => Connection;

        public IReadOnlyList<string> KeepPaused(IReadOnlyList<string> guids)
        {
            var kept = Table.CancelResume(guids);
            Calls.Add("keep:" + string.Join(",", kept));
            return kept;
        }
        /// <summary>After a resume, how many Provided reads still hide the resumed rows.</summary>
        internal int RowsLateReads;
        internal int NeverBack;
        private readonly HashSet<string> _late = new(StringComparer.OrdinalIgnoreCase);

        internal FakePlugin Add(string guid, string dll, params ModRecord[] rows)
        {
            var p = new FakePlugin { Guid = guid, Dll = dll, Rows = rows.ToList() };
            Plugins.Add(p);
            return p;
        }

        internal FakePlugin Get(string guid) => Plugins.Single(p => p.Guid == guid);

        public bool Supported => IsSupported;
        public string UnsupportedReason => IsSupported ? "" : "fake too old";
        public bool? HostsPrivateGame() => Hosting;

        private List<SuspendUnitNode> Nodes()
        {
            var dlls = Plugins.Where(p => p.Loaded && p.Reloadable).Select(p => p.Dll).Distinct().ToList();
            var nodes = new List<SuspendUnitNode>();
            foreach (var dll in dlls)
            {
                var ps = Plugins.Where(p => p.Loaded && p.Reloadable && p.Dll == dll).ToList();
                nodes.Add(new SuspendUnitNode(dll, ps.Select(p => p.Guid).ToArray(),
                    ps.SelectMany(p => p.Deps).ToArray(), ps.Any(p => p.Unsafe)));
            }
            return nodes;
        }

        public IReadOnlyList<string> Suspendable() =>
            Plugins.Where(p => p.Loaded && p.Reloadable && !p.Unsafe).Select(p => p.Guid).ToList();

        public IReadOnlyList<PausedPlugin> Suspended()
        {
            var list = new List<PausedPlugin>();
            foreach (var row in Table.Rows())
                if (PausedPlugin.TryParse(row, out var parsed)) list.Add(parsed);
            return list;
        }

        public IReadOnlyList<string> Closure(IReadOnlyList<string> guids, out string refusal)
        {
            var nodes = Nodes();
            if (!PluginSuspendPolicy.TryClosure(nodes, guids, out var selected, out refusal)) return Array.Empty<string>();
            return PluginSuspendPolicy.GuidsOf(nodes, selected);
        }

        public IReadOnlyList<string> Suspend(IReadOnlyList<string> guids, IReadOnlyList<string> rowOwners, out string report)
        {
            var stayed = Table.CancelResume(guids);
            var rest = guids.Where(g => !stayed.Contains(g, StringComparer.OrdinalIgnoreCase)).ToList();
            var set = rest.Count > 0 ? Closure(rest, out report) : (IReadOnlyList<string>)Array.Empty<string>();
            report = "";
            foreach (var dll in Plugins.Where(p => set.Contains(p.Guid, StringComparer.OrdinalIgnoreCase)).Select(p => p.Dll).Distinct().ToList())
            {
                var ps = Plugins.Where(p => p.Dll == dll && p.Loaded).ToList();
                var plugins = ps.Select(p =>
                {
                    var mine = rowOwners.Select(r => r.Split('\t')).Where(f => f.Length >= 2 && f[0] == p.Guid).ToList();
                    return new SuspendedPlugin(p.Guid, p.Guid.ToUpperInvariant(), "1.0.0",
                        mine.Select(f => f[1]).ToList(), mine.Where(f => f.Length > 2 && f[2] == "1").Select(f => f[1]).ToList());
                }).ToList();
                Table.Add(new SuspendedUnit(dll, dll, "", DateTime.UtcNow, plugins, ps.SelectMany(p => p.Deps).ToArray()));
                foreach (var p in ps) p.Loaded = false;
            }
            var done = stayed.Concat(set).ToList();
            Calls.Add("suspend:" + string.Join(",", done));
            return done;
        }

        public IReadOnlyList<string> Resume(IReadOnlyList<string>? guids, out string report)
        {
            report = "";
            var loaded = new List<string>();
            foreach (var unit in Table.BeginResume(guids)) loaded.AddRange(Step(unit));
            Calls.Add("resume:" + string.Join(",", loaded));
            return loaded;
        }

        /// <summary>What a Bootstrap scan does to resuming units.</summary>
        internal List<string> Scan()
        {
            var loaded = new List<string>();
            foreach (var unit in Table.Units().Where(u => u.Resuming).ToList()) loaded.AddRange(Step(unit));
            return loaded;
        }

        private List<string> Step(SuspendedUnit unit)
        {
            var ps = unit.Plugins.Select(sp => Get(sp.Guid)).ToList();
            if (ps.Any(p => p.AlcAlive) || Table.WaitsOnDependency(unit)) return new List<string>();
            Table.Remove(unit.Path);
            foreach (var p in ps) { p.Loaded = true; if (RowsLateReads > 0 || NeverBack > 0) _late.Add(p.Guid); }
            return ps.Select(p => p.Guid).ToList();
        }

        internal ModRecord[] Provided()
        {
            if (_late.Count > 0 && NeverBack == 0 && RowsLateReads-- <= 0) _late.Clear();
            var rows = new List<ModRecord>(Unowned);
            foreach (var p in Plugins) if (p.Loaded && !_late.Contains(p.Guid)) rows.AddRange(p.Rows);
            return rows.ToArray();
        }

        internal Dictionary<string, string> Owners()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Plugins) if (p.Loaded && !_late.Contains(p.Guid)) foreach (var r in p.Rows) d[r.Guid] = p.Guid;
            return d;
        }
    }

    internal sealed class Harness
    {
        internal readonly FakeBackend Backend = new();
        internal readonly List<(double Due, Action Work)> Delayed = new();
        internal readonly List<string> Notices = new();
        internal readonly List<ModRecord[]> Replies = new();
        internal readonly NoProgenitorWatch Watch = new();
        internal double Now;
        internal string Host = "203.0.113.5:9876";
        internal readonly List<ModRecord> AskedRecord = new();

        internal Harness()
        {
            ServerModPause.Test = new ServerModPause.TestSeams
            {
                Backend = Backend,
                Post = a => a(),
                PostDelayed = (s, a) => Delayed.Add((Now + s, a)),
                Host = () => Host,
                Clock = () => Now,
                Provided = Backend.Provided,
                Owners = Backend.Owners,
                Asked = () => AskedRecord.ToArray(),
                ReAnswer = () => { Backend.Calls.Add("reanswer"); return true; },
                Watch = Watch,
            };
            ServerModPause.NoticeRequested += (title, text) => Notices.Add(text);
        }

        /// <summary>One reply: what the server would receive, read at send time.</summary>
        internal bool Reply()
        {
            Backend.Calls.Add("reply");
            Replies.Add(Backend.Provided());
            return true;
        }

        internal void Request(params ModRecord[] asked) => Request(false, asked);

        internal void Request(bool truncated, params ModRecord[] asked)
        {
            AskedRecord.Clear();
            AskedRecord.AddRange(asked);
            ServerModPause.OnRequest(asked.ToList(), truncated, Reply);
        }

        /// <summary>A Bootstrap scan, then Core's frame tick after the poll interval.</summary>
        internal List<string> ScanAndTick()
        {
            var loaded = Backend.Scan();
            Now += ServerModPause.AwaitPollSeconds;
            ServerModPause.Tick();
            return loaded;
        }

        /// <summary>Run by <see cref="Pump"/> each step; a Bootstrap scan when a test wants one.</summary>
        internal Action? EachStep;

        internal void Pump(double step = ServerModPause.ResumeRowsPollSeconds, int max = 1000)
        {
            for (int i = 0; i < max && Delayed.Count > 0; i++)
            {
                Now += step;
                EachStep?.Invoke();
                var due = Delayed.Where(d => d.Due <= Now).ToList();
                foreach (var d in due) { Delayed.Remove(d); d.Work(); }
            }
        }

        internal bool Paused(string guid) => Backend.Plugins.Any(p => p.Guid == guid && !p.Loaded);
    }

    private static (IReadOnlyList<string>, string) NoClosure(IReadOnlyList<string> g) => (Array.Empty<string>(), "refused");

    private static Func<IReadOnlyList<string>, (IReadOnlyList<string>, string)> ClosureOf(FakeBackend backend) =>
        g => { var set = backend.Closure(g, out var r); return (set, r); };

    // ------------------------------------------------------------------ planner

    [Fact]
    public void A_client_ServerMustHave_mod_the_server_lacks_is_planned_for_pause()
    {
        var b = new FakeBackend();
        b.Add("a", "a.dll", Smh("a"));
        var plan = ServerModPausePolicy.PlanPause(Array.Empty<ModRecord>(), false, b.Provided(), b.Owners(),
            b.Suspendable().ToList(), ClosureOf(b), autoPause: true);
        Assert.Equal(new[] { "a" }, plan.Pause);
        Assert.Empty(plan.RestartNeeded);
        Assert.Equal("", plan.Why);
    }

    [Fact]
    public void A_join_that_already_succeeds_plans_nothing()
    {
        var b = new FakeBackend();
        b.Add("a", "a.dll", Smh("a"));
        var plan = ServerModPausePolicy.PlanPause(new[] { Smh("a") }, false, b.Provided(), b.Owners(),
            b.Suspendable().ToList(), ClosureOf(b), autoPause: true);
        Assert.Empty(plan.Pause);
        Assert.Equal("", plan.Why);
    }

    [Fact]
    public void A_full_sixteen_row_request_may_be_truncated_so_nothing_is_paused()
    {
        var b = new FakeBackend();
        b.Add("a", "a.dll", Smh("a"));
        var asked = Enumerable.Range(0, Handshake.MaxMods).Select(i => Optional("srv." + i)).ToArray();
        var plan = ServerModPausePolicy.PlanPause(asked, truncated: true, b.Provided(), b.Owners(),
            b.Suspendable().ToList(), ClosureOf(b), autoPause: true);
        Assert.Empty(plan.Pause);
        Assert.Equal(ServerModPausePolicy.WhyTruncated, plan.Why);
    }

    [Fact]
    public void Dependents_and_dll_siblings_are_paused_with_the_failing_plugin()
    {
        var b = new FakeBackend();
        b.Add("a", "a.dll", Smh("a"));
        b.Add("a.helper", "a.dll", ModRecord.Undeclared("a.helper", "1.0.0"));   // a library: declares nothing
        b.Add("b", "b.dll", ModRecord.Undeclared("b", "1.0.0")).Deps = new[] { "a" };
        b.Add("c", "c.dll", Optional("c"));
        var plan = ServerModPausePolicy.PlanPause(Array.Empty<ModRecord>(), false, b.Provided(), b.Owners(),
            b.Suspendable().ToList(), ClosureOf(b), autoPause: true);
        Assert.Equal(new[] { "a", "a.helper", "b" }, plan.Pause.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void A_NoNeedForSync_mod_the_server_lacks_is_never_paused()
    {
        // A "Both" plugin that declares it works without its server half.
        var b = new FakeBackend();
        b.Add("both", "both.dll", Optional("both"));
        var plan = ServerModPausePolicy.PlanPause(Array.Empty<ModRecord>(), false, b.Provided(), b.Owners(),
            b.Suspendable().ToList(), ClosureOf(b), autoPause: true);
        Assert.Empty(plan.Pause);
        var none = ServerModPausePolicy.PlanNoProgenitor(b.Provided(), b.Owners(), b.Suspendable().ToList(), ClosureOf(b), true);
        Assert.Empty(none.Pause);
        Assert.Empty(none.RestartNeeded);
    }

    [Fact]
    public void A_NoNeedForSync_row_that_fails_its_version_rule_is_not_paused()
    {
        var b = new FakeBackend();
        b.Add("both", "both.dll", Optional("both", "2.0.0"));
        // The server has it (a row it did not send, e.g. ClientOnly there) at another minor.
        var plan = ServerModPausePolicy.PlanPause(new[] { Optional("both", "1.0.0") }, false, b.Provided(), b.Owners(),
            b.Suspendable().ToList(), ClosureOf(b), autoPause: true);
        Assert.Empty(plan.Pause);
    }

    [Fact]
    public void A_closure_that_would_take_a_NoNeedForSync_or_ClientOnly_mod_pauses_nothing()
    {
        var b = new FakeBackend();
        b.Add("a", "a.dll", Smh("a"));
        b.Add("soft", "soft.dll", Optional("soft")).Deps = new[] { "a" };
        var plan = ServerModPausePolicy.PlanPause(Array.Empty<ModRecord>(), false, b.Provided(), b.Owners(),
            b.Suspendable().ToList(), ClosureOf(b), autoPause: true);
        Assert.Empty(plan.Pause);
        Assert.Equal(ServerModPausePolicy.WhyProtected, plan.Why);

        var ui = new FakeBackend();
        ui.Add("a", "a.dll", Smh("a"));
        ui.Add("ui", "a.dll", ModRecord.CreateDeclared("ui", "1.0.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.None));
        var none = ServerModPausePolicy.PlanNoProgenitor(ui.Provided(), ui.Owners(), ui.Suspendable().ToList(), ClosureOf(ui), true);
        Assert.Empty(none.Pause);
        Assert.Equal(ServerModPausePolicy.WhyProtected, none.Why);
    }

    [Fact]
    public void A_failing_restart_only_mod_blocks_the_auto_pause_and_is_named()
    {
        var b = new FakeBackend();
        b.Add("a", "a.dll", Smh("a"));
        b.Unowned.Add(Smh("plain.mod"));
        var plan = ServerModPausePolicy.PlanPause(Array.Empty<ModRecord>(), false, b.Provided(), b.Owners(),
            b.Suspendable().ToList(), ClosureOf(b), autoPause: true);
        Assert.Empty(plan.Pause);
        Assert.Equal(new[] { "plain.mod" }, plan.RestartNeeded);
        Assert.Equal(ServerModPausePolicy.WhyRestartOnly, plan.Why);
    }

    [Fact]
    public void A_plugin_whose_row_the_server_asked_for_is_never_paused()
    {
        var b = new FakeBackend();
        // Plugin "a" provides its own row (which the server asks about and has) and a second
        // ServerMustHave row the server does not run.
        b.Add("a", "a.dll", Smh("a"), Smh("a.core"));
        var plan = ServerModPausePolicy.PlanPause(new[] { Smh("a") }, false, b.Provided(), b.Owners(),
            b.Suspendable().ToList(), ClosureOf(b), autoPause: true);
        Assert.Empty(plan.Pause);
        Assert.Equal(ServerModPausePolicy.WhyAsked, plan.Why);
    }

    [Fact]
    public void An_unsafe_harmony_plugin_refuses_the_closure_and_nothing_is_paused()
    {
        var b = new FakeBackend();
        b.Add("a", "a.dll", Smh("a")).Unsafe = true;
        var plan = ServerModPausePolicy.PlanPause(Array.Empty<ModRecord>(), false, b.Provided(), b.Owners(),
            new[] { "a" }, ClosureOf(b), autoPause: true);
        Assert.Empty(plan.Pause);
        Assert.Contains("UnsafeHarmony", plan.Why);
    }

    [Fact]
    public void Auto_pause_off_plans_nothing()
    {
        var b = new FakeBackend();
        b.Add("a", "a.dll", Smh("a"));
        var plan = ServerModPausePolicy.PlanPause(Array.Empty<ModRecord>(), false, b.Provided(), b.Owners(),
            b.Suspendable().ToList(), ClosureOf(b), autoPause: false);
        Assert.Empty(plan.Pause);
        Assert.Equal(ServerModPausePolicy.WhyAutoPauseOff, plan.Why);
    }

    [Fact]
    public void Combined_plan_resumes_what_the_server_asks_about_by_guid_or_remembered_row()
    {
        var rows = new Dictionary<string, IReadOnlyList<string>> { ["x"] = new[] { "x.row" } };
        var plan = ServerModPausePolicy.Plan(new[] { Smh("a"), Smh("x.row") }, false, Array.Empty<ModRecord>(),
            null, Array.Empty<string>(), new[] { "a", "x", "y" }, rows, NoClosure, autoPause: true);
        Assert.Equal(new[] { "a", "x" }, plan.Resume);
    }

    [Theory]
    [InlineData("127.0.0.1:9876", true)]
    [InlineData("Lidgren://127.0.0.1:9886", true)]
    [InlineData("localhost:9876", true)]
    [InlineData("[::1]:9876", true)]
    [InlineData("203.0.113.5:9876", false)]
    [InlineData("Aqf27THwTQgIoENZsJCBAvJNEAA", false)]
    [InlineData("", false)]
    public void Local_hosts_are_recognised(string host, bool local) =>
        Assert.Equal(local, ServerModPausePolicy.IsLocalHost(host));

    // ------------------------------------------------------------------ coordinator (handshake path)

    [Fact]
    public void A_mod_the_server_does_not_run_is_paused_before_the_reply_and_the_notice_names_it()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Backend.Add("b", "b.dll", Optional("b"));

        h.Request();

        Assert.Equal(new[] { "suspend:a", "reply" }, h.Backend.Calls);
        Assert.DoesNotContain(h.Replies.Single(), r => r.Guid == "a");
        Assert.True(ServerModPause.Current.Any);
        Assert.Equal(new[] { "a" }, ServerModPause.Current.PausedIds);
        Assert.Equal(h.Host, ServerModPause.Current.Host);
        Assert.Equal("Paused 1 mod for this server: A (This server doesn't run it). They come back when you leave.", h.Notices.Single());
        Assert.Equal(new[] { "a" }, ServerProfiles.Get(h.Host).LastPaused);
    }

    [Fact]
    public void Nothing_to_do_replies_immediately_without_touching_the_backend()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request(Smh("a"));
        Assert.Equal(new[] { "reply" }, h.Backend.Calls);
        Assert.Empty(h.Notices);
    }

    [Fact]
    public void A_truncated_request_is_answered_without_pausing()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request(true, Enumerable.Range(0, Handshake.MaxMods).Select(i => Optional("srv." + i)).ToArray());
        Assert.Equal(new[] { "reply" }, h.Backend.Calls);
        Assert.False(h.Paused("a"));
    }

    [Fact]
    public void A_paused_mod_the_server_asks_for_is_resumed_before_the_reply()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();                     // server 1 does not run "a": paused
        Assert.True(h.Paused("a"));
        h.Backend.Calls.Clear();

        h.Request(Smh("a"));             // same or another server now asks for it

        Assert.Equal(new[] { "resume:a", "reply" }, h.Backend.Calls);
        Assert.Contains(h.Replies.Last(), r => r.Guid == "a");
        Assert.False(ServerModPause.Current.Any);
    }

    [Fact]
    public void An_abnormal_leave_with_no_connection_end_is_repaired_by_the_next_request()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"), Smh("a.core"));
        h.Request();                     // server 1 runs neither: "a" paused
        Assert.True(h.Paused("a"));
        h.Backend.Calls.Clear();

        // No OnConnectionEnded: the client crashed out of server 1. Server 2 runs the mod and asks
        // for the row the plugin provided (not its own GUID), which the pause remembered.
        h.Host = "198.51.100.7:9876";
        h.Request(Smh("a.core"));

        Assert.Equal("resume:a", h.Backend.Calls[0]);
        Assert.Equal("reply", h.Backend.Calls.Last());
        Assert.Contains(h.Replies.Last(), r => r.Guid == "a.core");
    }

    [Fact]
    public void Pause_then_leave_then_join_a_server_that_requires_it_pauses_nothing()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();
        Assert.True(h.Paused("a"));

        Handshake.NotifyClientConnectionEnded();   // the real leave
        Assert.False(h.Paused("a"));
        h.Backend.Calls.Clear();

        h.Request(Smh("a"));
        Assert.Equal(new[] { "reply" }, h.Backend.Calls);
        Assert.Contains(h.Replies.Last(), r => r.Guid == "a");
    }

    [Fact]
    public void A_new_join_epoch_does_not_resume_but_a_connection_end_does()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();
        Assert.True(h.Paused("a"));

        Handshake.NotifyHandshakeEpochChanged();
        Assert.True(h.Paused("a"));

        Handshake.NotifyClientConnectionEnded();
        Assert.False(h.Paused("a"));
        Assert.Contains("resume:a", h.Backend.Calls);
    }

    [Fact]
    public void The_reply_waits_for_resumed_rows_to_come_back()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();
        h.Backend.Calls.Clear();
        h.Backend.RowsLateReads = 3;

        h.Request(Smh("a"));
        Assert.DoesNotContain("reply", h.Backend.Calls);   // still waiting
        h.Pump();

        Assert.Equal("reply", h.Backend.Calls.Last());
        Assert.Contains(h.Replies.Last(), r => r.Guid == "a");
        Assert.True(h.Now < ServerModPause.ResumeRowsWaitSeconds);
    }

    [Fact]
    public void A_resumed_dependent_that_provides_no_row_does_not_delay_the_reply()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Backend.Add("lib", "lib.dll").Deps = new[] { "a" };   // declares nothing, provides nothing
        h.Request();
        Assert.True(h.Paused("a") && h.Paused("lib"));
        h.Backend.Calls.Clear();

        h.Request(Smh("a"));

        Assert.Equal(new[] { "resume:a,lib", "reply" }, h.Backend.Calls);
        Assert.Empty(h.Delayed);
        Assert.Equal(0, h.Now);
    }

    [Fact]
    public void The_reply_goes_out_at_the_deadline_when_rows_never_come_back()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();
        h.Backend.Calls.Clear();
        h.Backend.NeverBack = 1;

        h.Request(Smh("a"));
        h.Pump();

        Assert.Equal("reply", h.Backend.Calls.Last());
        Assert.True(h.Now >= ServerModPause.ResumeRowsWaitSeconds);
        Assert.True(h.Now < 20, "well inside the server's answer deadline");
    }

    [Fact]
    public void A_profile_opt_out_is_respected()
    {
        var h = new Harness();
        ServerProfiles.SetAutoPause(h.Host, false);
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();
        Assert.Equal(new[] { "reply" }, h.Backend.Calls);
        Assert.False(h.Paused("a"));
    }

    [Fact]
    public void An_older_bootstrap_degrades_to_the_plain_refusal_and_says_why()
    {
        var h = new Harness();
        h.Backend.IsSupported = false;
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();
        Assert.Equal(new[] { "reply" }, h.Backend.Calls);
        Assert.False(h.Paused("a"));
        Assert.Contains("Update Progenitor and restart the game", h.Notices.Single());
    }

    // ------------------------------------------------------------------ capability check

    public static class HostWithoutPause
    {
        public static string[] SuspendPlugins(string[] guids, out string report) { report = ""; return guids; }
    }

    public static class HostWithPause
    {
        internal static readonly List<string> Seen = new();
        public static string[] SuspendPlugins(string[] guids, string[] rowOwners, out string report) { Seen.Add("s:" + string.Join(",", rowOwners)); report = "ok"; return guids; }
        public static string[]? ResumePlugins(string[]? guids, out string report) { Seen.Add("r"); report = ""; return guids ?? new[] { "all" }; }
        public static string[] SuspendedPlugins() => new[] { "a\tAlpha\t1.2.3\tresuming\ta,a.core", "" };
        public static string[] SuspendablePluginGuids() => new[] { "a" };
        public static string[] PluginSuspendClosure(string[] guids, out string refusal) { refusal = ""; return guids; }
        public static string PendingHandshakeRequest { get; set; } = "";
        public static string PendingResumeReports { get; set; } = "";
        public static string[] KeepPluginsPaused(string[] guids) => guids;
        public static bool IsHostingPrivateGame { get; set; }
    }

    /// <summary>Everything but the carry-over slot: a Bootstrap from before this revision.</summary>
    public static class HostWithoutCarry
    {
        public static string[] SuspendPlugins(string[] guids, string[] rowOwners, out string report) { report = ""; return guids; }
        public static string[]? ResumePlugins(string[]? guids, out string report) { report = ""; return guids; }
        public static string[] SuspendedPlugins() => Array.Empty<string>();
        public static string[] SuspendablePluginGuids() => Array.Empty<string>();
        public static string[] PluginSuspendClosure(string[] guids, out string refusal) { refusal = ""; return guids; }
    }

    [Fact]
    public void The_capability_check_refuses_a_bootstrap_without_the_entry_points()
    {
        var old = ReflectedPauseBackend.Create(typeof(HostWithoutPause));
        Assert.False(old.Supported);
        Assert.Contains("SuspendPlugins", old.UnsupportedReason);   // the pre-row-owner shape is refused too
        Assert.Empty(old.Suspended());

        var none = ReflectedPauseBackend.Create(null);
        Assert.False(none.Supported);
        Assert.Equal(ServerModPauseText.NoBootstrap, none.UnsupportedReason);

        Assert.False(ReflectedPauseBackend.Create(typeof(HostWithoutCarry)).Supported);

        var current = ReflectedPauseBackend.Create(typeof(HostWithPause));
        Assert.True(current.Supported);
        Assert.Equal(new[] { "x" }, current.Suspend(new[] { "x" }, new[] { "x\trow" }, out var report));
        Assert.Equal("ok", report);
        Assert.Contains("s:x\trow", HostWithPause.Seen);
        Assert.Equal(new[] { "all" }, current.Resume(null, out _));
        var paused = current.Suspended().Single();
        Assert.Equal(("a", "Alpha", "1.2.3", true), (paused.Guid, paused.Name, paused.Version, paused.Resuming));
        Assert.Equal(new[] { "a", "a.core" }, paused.Rows);
        HostWithPause.IsHostingPrivateGame = true;
        Assert.True(current.HostsPrivateGame());
        HostWithPause.IsHostingPrivateGame = false;
        Assert.False(current.HostsPrivateGame());
        current.PendingRequest = "v1|1|0";
        Assert.Equal("v1|1|0", HostWithPause.PendingHandshakeRequest);
        current.PendingRequest = "";
    }

    // ------------------------------------------------------------------ no-Progenitor server

    [Fact]
    public void The_watch_fires_once_when_no_request_arrives_within_the_grace_period()
    {
        var w = new NoProgenitorWatch();
        Assert.False(w.Observe(false, 0, 10));      // loading
        Assert.False(w.Observe(true, 1, 10));       // Running: armed
        Assert.True(w.Armed);
        Assert.False(w.Observe(true, 10.9, 10));
        Assert.True(w.Observe(true, 11, 10));
        Assert.False(w.Observe(true, 30, 10));      // once
    }

    [Fact]
    public void A_request_within_the_grace_period_or_before_running_keeps_the_watch_quiet()
    {
        var w = new NoProgenitorWatch();
        w.Observe(false, 0, 10);
        w.Observe(true, 1, 10);
        w.NoteRequest();
        Assert.False(w.Observe(true, 20, 10));

        var early = new NoProgenitorWatch();
        early.Observe(false, 0, 10);
        early.NoteRequest();                        // the usual order: asked while loading
        Assert.False(early.Observe(true, 1, 10));
        Assert.False(early.Armed);
    }

    [Fact]
    public void A_core_installed_mid_game_or_a_zero_grace_never_arms()
    {
        var w = new NoProgenitorWatch();
        Assert.False(w.Observe(true, 0, 10));       // first sight is Running: a Core swap in game
        Assert.False(w.Armed);
        Assert.False(w.Observe(true, 100, 10));

        var off = new NoProgenitorWatch();
        off.Observe(false, 0, 0);
        off.Observe(true, 1, 0);
        Assert.False(off.Armed);
    }

    [Fact]
    public void A_running_flicker_does_not_rearm_but_a_real_connection_end_does()
    {
        var w = new NoProgenitorWatch();
        w.Observe(false, 0, 10);
        w.NoteRequest();
        w.Observe(true, 1, 10);
        w.Observe(false, 5, 10);                    // Running -> Loading flicker, same connection
        w.Observe(true, 6, 10);
        Assert.False(w.Armed);
        Assert.False(w.Observe(true, 100, 10));

        w.Reset();                                  // the connection really ended
        w.Observe(false, 200, 10);
        w.Observe(true, 201, 10);                   // next join, no request yet
        Assert.True(w.Armed);
    }

    [Fact]
    public void No_request_within_grace_pauses_every_pausable_ServerMustHave_mod()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Backend.Add("ui", "ui.dll", ModRecord.CreateDeclared("ui", "1.0.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.None));
        h.Backend.Unowned.Add(Smh("plain.mod"));

        ServerModPause.OnClientFrame(false);
        h.Now = 1; ServerModPause.OnClientFrame(true);
        h.Now = 1 + ServerModPause.DefaultNoProgenitorGraceSeconds; ServerModPause.OnClientFrame(true);

        Assert.Equal(new[] { "suspend:a" }, h.Backend.Calls);
        Assert.False(h.Paused("ui"));
        Assert.True(ServerModPause.Current.ServerWithoutProgenitor);
        Assert.Equal(
            "Paused 1 mod for this server: A (This server doesn't run Progenitor). They come back when you leave. "
            + "Still running because they were loaded at game start: plain.mod; restart V Rising without them to turn them off.",
            h.Notices.Single());
        Assert.False(ServerProfiles.Get(h.Host).RunsProgenitor);
    }

    [Fact]
    public void A_request_within_grace_takes_the_normal_path()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        ServerModPause.OnClientFrame(false);
        h.Now = 1; ServerModPause.OnClientFrame(true);
        h.Request(Smh("a"));
        h.Now = 50; ServerModPause.OnClientFrame(true);
        Assert.Equal(new[] { "reply" }, h.Backend.Calls);
        Assert.False(h.Paused("a"));
    }

    [Fact]
    public void A_late_request_after_the_grace_pause_resumes_what_it_asks_for_before_replying()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Backend.Add("b", "b.dll", Smh("b"));
        ServerModPause.OnNoProgenitorDetected(10);
        Assert.True(h.Paused("a") && h.Paused("b"));
        h.Backend.Calls.Clear();

        h.Request(Smh("a"));             // a slow Progenitor server after all; it runs "a" only

        Assert.Equal(new[] { "resume:a", "reply" }, h.Backend.Calls);
        Assert.True(h.Paused("b"));
        Assert.False(ServerModPause.Current.ServerWithoutProgenitor);
    }

    [Fact]
    public void The_no_progenitor_pause_respects_the_opt_out_and_skips_a_local_server()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        ServerProfiles.SetAutoPause(h.Host, false);
        ServerModPause.OnNoProgenitorDetected(10);
        Assert.False(h.Paused("a"));

        h.Host = "127.0.0.1:9886";   // a private game's hosted child
        ServerModPause.OnNoProgenitorDetected(10);
        Assert.False(h.Paused("a"));
        Assert.Empty(h.Backend.Calls);
    }

    // ------------------------------------------------------------------ profiles

    [Fact]
    public void Profiles_round_trip_and_default_to_auto_pause_on()
    {
        var p = new ServerProfile
        {
            Host = "203.0.113.5:9876",
            AutoPause = false,
            LastPaused = new List<string> { "a", "b" },
            LastSeenUtc = "2026-09-27T00:00:00.0000000Z",
            ServerName = "Test",
            OperatorFingerprint = "abcd",
            RunsProgenitor = false,
        };
        string json = ServerProfiles.Serialize(new[] { p, new ServerProfile { Host = "other" } });
        Assert.True(ServerProfiles.TryParse(json, out var back, out var error), error);
        Assert.Equal(2, back.Count);
        Assert.False(back[0].AutoPause);
        Assert.Equal(new[] { "a", "b" }, back[0].LastPaused);
        Assert.Equal("Test", back[0].ServerName);
        Assert.Equal("abcd", back[0].OperatorFingerprint);
        Assert.False(back[0].RunsProgenitor);
        Assert.True(back[1].AutoPause);
        Assert.Null(back[1].RunsProgenitor);

        Assert.True(ServerProfiles.AutoPause("never-seen:1"));
        Assert.False(File.Exists(ServerProfiles.FilePath));
        ServerProfiles.SetAutoPause("Host.Example:1", false);
        Assert.False(ServerProfiles.AutoPause("host.example:1"));
        Assert.False(ServerProfiles.TryParse("[1,2]", out _, out _));
    }

    [Fact]
    public void Identity_is_recorded_only_for_a_server_that_already_has_a_profile()
    {
        ServerProfiles.NoteIdentityIfKnown("new:1", "Name", "fp");
        Assert.False(File.Exists(ServerProfiles.FilePath));
        ServerProfiles.NotePaused("known:1", new[] { "a" }, DateTime.UtcNow);
        ServerProfiles.NoteIdentityIfKnown("known:1", "Name", "fp");
        var p = ServerProfiles.Get("known:1");
        Assert.Equal(("Name", "fp", (bool?)true), (p.ServerName, p.OperatorFingerprint, p.RunsProgenitor));
    }

    // ------------------------------------------------------------------ Bootstrap suspend table

    [Fact]
    public void The_suspend_table_skips_paused_files_in_the_scan_and_forgets_deleted_ones()
    {
        var t = new PluginSuspendTable();
        var write = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
        t.Add(new SuspendedUnit(@"C:\r\A.dll", "A", "sha", write, new[] { new SuspendedPlugin("a", "A", "1") }));

        Assert.False(t.SkipInScan(@"C:\r\B.dll", write, out _));
        Assert.True(t.SkipInScan(@"c:\r\a.dll", write, out var changed));
        Assert.False(changed);
        Assert.True(t.SkipInScan(@"C:\r\A.dll", write.AddSeconds(5), out changed));   // redeployed while paused
        Assert.True(changed);
        Assert.True(t.SkipInScan(@"C:\r\A.dll", write.AddSeconds(5), out changed));
        Assert.False(changed);
        Assert.True(t.Units().Single().ChangedWhileSuspended);

        var gone = t.DropMissing(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\r\B.dll" });
        Assert.Single(gone);
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void Resuming_brings_back_paused_dependents_too()
    {
        var t = new PluginSuspendTable();
        var w = DateTime.UtcNow;
        t.Add(new SuspendedUnit("A.dll", "A", "", w, new[] { new SuspendedPlugin("a", "A", "1") }));
        t.Add(new SuspendedUnit("B.dll", "B", "", w, new[] { new SuspendedPlugin("b", "B", "1") }, new[] { "a" }));
        t.Add(new SuspendedUnit("C.dll", "C", "", w, new[] { new SuspendedPlugin("c", "C", "1") }));

        var back = t.BeginResume(new[] { "a" });
        Assert.Equal(new[] { "A.dll", "B.dll" }, back.Select(u => u.Path).ToArray());
        Assert.True(back.All(u => u.Resuming));
        Assert.Equal(3, t.Count);                        // still held until they actually load
        Assert.False(t.SkipInScan("A.dll", w, out _));   // the scan steps a resuming unit, it does not skip it
        Assert.Contains("c\tC\t1\tpaused\t\t", t.Rows());
        Assert.Contains("a\tA\t1\tresuming\t\t", t.Rows());
    }

    [Fact]
    public void Resuming_a_dependent_brings_back_what_it_depends_on_first()
    {
        var t = new PluginSuspendTable();
        var w = DateTime.UtcNow;
        // Suspend inserted dependents first (as ReloaderHost does): B depends on A.
        t.Add(new SuspendedUnit("B.dll", "B", "", w, new[] { new SuspendedPlugin("b", "B", "1") }, new[] { "a" }));
        t.Add(new SuspendedUnit("A.dll", "A", "", w, new[] { new SuspendedPlugin("a", "A", "1") }));

        var order = t.BeginResume(new[] { "b" });
        Assert.Equal(new[] { "A.dll", "B.dll" }, order.Select(u => u.Path).ToArray());

        var all = new PluginSuspendTable();
        all.Add(new SuspendedUnit("B.dll", "B", "", w, new[] { new SuspendedPlugin("b", "B", "1") }, new[] { "a" }));
        all.Add(new SuspendedUnit("A.dll", "A", "", w, new[] { new SuspendedPlugin("a", "A", "1") }));
        Assert.Equal(new[] { "A.dll", "B.dll" }, all.BeginResume(null).Select(u => u.Path).ToArray());
    }

    [Theory]
    [InlineData(false, true, false, 0, 0)]
    [InlineData(true, false, false, 0, 1)]      // inside the settle window
    [InlineData(true, true, false, 0, 3)]
    [InlineData(true, true, true, 0, 2)]
    [InlineData(true, true, true, PluginSuspendPolicy.MaxCollectPulses, 1)]
    public void A_pending_resume_waits_pulses_boundedly_and_never_fails(bool exists, bool settled, bool blocked, int pulses, int step) =>
        Assert.Equal((PluginSuspendPolicy.ResumeStep)step, PluginSuspendPolicy.DecideResume(exists, settled, blocked, pulses));

    [Fact]
    public void Keeping_a_unit_paused_also_keeps_its_resuming_dependents_even_without_hard_rows()
    {
        var t = new PluginSuspendTable();
        var w = DateTime.UtcNow;
        t.Add(new SuspendedUnit("B.dll", "B", "", w, new[] { new SuspendedPlugin("b", "B", "1", new[] { "b" }, new[] { "b" }) }));
        t.Add(new SuspendedUnit("D.dll", "D", "", w, new[] { new SuspendedPlugin("d", "D", "1") }, new[] { "b" }));
        t.Add(new SuspendedUnit("E.dll", "E", "", w, new[] { new SuspendedPlugin("e", "E", "1") }, new[] { "d" }));
        t.BeginResume(null);

        var kept = t.CancelResume(new[] { "b" });

        Assert.Equal(new[] { "b", "d", "e" }, kept.OrderBy(x => x).ToArray());
        Assert.All(t.Units(), u => Assert.False(u.Resuming));
    }

    [Fact]
    public void A_resuming_dependent_waits_until_its_dependency_has_actually_loaded()
    {
        var t = new PluginSuspendTable();
        var w = DateTime.UtcNow;
        var b = new SuspendedUnit("B.dll", "B", "", w, new[] { new SuspendedPlugin("b", "B", "1") });
        var d = new SuspendedUnit("D.dll", "D", "", w, new[] { new SuspendedPlugin("d", "D", "1") }, new[] { "b" });
        t.Add(b);
        t.Add(d);
        t.BeginResume(null);

        Assert.True(t.WaitsOnDependency(d));        // B is still blocked on its old context
        Assert.False(t.WaitsOnDependency(b));
        Assert.Equal(PluginSuspendPolicy.ResumeStep.Wait,
            PluginSuspendPolicy.DecideResume(true, true, false, 0, dependencyPending: true));
        t.Remove(b.Path);                            // B loaded
        Assert.False(t.WaitsOnDependency(d));

        string src = SourcePin.ReadFile("src", "Progenitor.Bootstrap", "ReloaderHost.cs");
        Assert.Contains("bool dependencyPending = Paused.WaitsOnDependency(unit);", src);
    }

    [Fact]
    public void A_dependent_of_a_kept_mod_does_not_load_on_the_next_join()
    {
        var h = new Harness();
        var b = h.Backend.Add("b", "b.dll", Smh("b"));
        h.Backend.Add("d", "d.dll", ModRecord.Undeclared("d", "1.0.0")).Deps = new[] { "b" };
        h.Request();                                // server 1 runs neither: b and d paused
        Assert.True(h.Paused("b") && h.Paused("d"));
        b.AlcAlive = true;
        ServerModPause.OnConnectionEnded();         // both released; b blocked, so d waits on b
        Assert.True(h.Paused("d"));
        h.Backend.Calls.Clear();

        h.Request(Optional("other"));               // server 2 does not run b

        Assert.Equal("keep:b,d", h.Backend.Calls[0]);
        b.AlcAlive = false;
        h.ScanAndTick();
        Assert.True(h.Paused("b") && h.Paused("d"));
    }

    [Fact]
    public void A_new_pause_of_a_unit_still_resuming_keeps_it_paused()
    {
        var t = new PluginSuspendTable();
        t.Add(new SuspendedUnit("A.dll", "A", "", DateTime.UtcNow, new[] { new SuspendedPlugin("a", "A", "1") }));
        t.BeginResume(new[] { "a" });
        Assert.Equal(new[] { "a" }, t.CancelResume(new[] { "a" }));
        Assert.True(t.SkipInScan("A.dll", DateTime.UtcNow, out _));
    }

    [Fact]
    public void The_closure_is_all_or_nothing()
    {
        var units = new[]
        {
            new SuspendUnitNode("a.dll", new[] { "a", "a2" }, null, false),
            new SuspendUnitNode("b.dll", new[] { "b" }, new[] { "a2" }, false),
            new SuspendUnitNode("u.dll", new[] { "u" }, new[] { "b" }, true),
        };
        Assert.False(PluginSuspendPolicy.TryClosure(units, new[] { "a" }, out _, out var refusal));
        Assert.Contains("UnsafeHarmony", refusal);   // u depends on b depends on a2
        Assert.False(PluginSuspendPolicy.TryClosure(units, new[] { "plain" }, out _, out refusal));
        Assert.Contains("loaded at game start", refusal);
        Assert.True(PluginSuspendPolicy.TryClosure(units.Take(2).ToArray(), new[] { "a" }, out var sel, out _));
        Assert.Equal(new[] { "a", "a2", "b" }, PluginSuspendPolicy.GuidsOf(units, sel));
    }

    [Fact]
    public void The_reload_scan_asks_the_suspend_table_before_it_loads_and_forgets_deleted_pauses()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Bootstrap", "ReloaderHost.cs");
        int scan = src.IndexOf("private static void Scan()", StringComparison.Ordinal);
        int skip = src.IndexOf("Paused.SkipInScan(file, write", scan, StringComparison.Ordinal);
        int load = src.IndexOf("ByPath.TryGetValue(file, out var loaded)", scan, StringComparison.Ordinal);
        int drop = src.IndexOf("Paused.DropMissing(seen)", scan, StringComparison.Ordinal);
        Assert.True(scan > 0 && skip > scan && load > skip && drop > load, "Scan must skip paused files before any load");
        Assert.Contains("Unload(item, PluginUnloadSlot.Suspend, releaseIcons: false);", src);
    }

    // ------------------------------------------------------------------ review round 1

    [Fact]
    public void The_scan_steps_a_pending_resume_and_marks_Failed_only_after_a_real_load_attempt()
    {
        string src = SourcePin.ReadFile("src", "Progenitor.Bootstrap", "ReloaderHost.cs");
        int scan = src.IndexOf("private static void Scan()", StringComparison.Ordinal);
        int step = src.IndexOf("StepResume(resumingUnit, resumeLines);", scan, StringComparison.Ordinal);
        int order = src.IndexOf("Paused.ResumingInOrder()", scan, StringComparison.Ordinal);
        int load = src.IndexOf("foreach (var file in Directory.GetFiles(Folder, \"*.dll\"))", scan, StringComparison.Ordinal);
        Assert.True(order > scan && step > order);
        Assert.True(step > scan && load > step);

        int body = src.IndexOf("private static bool StepResume(", StringComparison.Ordinal);
        int end = src.IndexOf("\n    }\n", body, StringComparison.Ordinal);
        string fn = src.Substring(body, end - body);
        int decide = fn.IndexOf("PluginSuspendPolicy.DecideResume(", StringComparison.Ordinal);
        int loadCall = fn.IndexOf("var fresh = Load(unit.Path, write);", StringComparison.Ordinal);
        int failed = fn.IndexOf("Failed[unit.Path] = write;", StringComparison.Ordinal);
        Assert.True(decide > 0 && loadCall > decide && failed > loadCall, "a pending resume must never be marked Failed before it tried to load");
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(fn, @"Failed\[").Count);
    }

    [Fact]
    public void The_reply_waits_for_an_asked_mod_still_resuming_and_carries_its_rows()
    {
        var h = new Harness();
        var a = h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();
        a.AlcAlive = true;
        h.Backend.Calls.Clear();
        var resumed = new List<ModsResumed>();
        using var sub = Progenitor.Events.EventBus.On<ModsResumed>(resumed.Add);

        h.Request(Smh("a"));                        // the server runs it, but it cannot load yet
        Assert.Equal(new[] { "resume:" }, h.Backend.Calls);   // no reply yet: it waits for the scan
        Assert.Empty(resumed);
        Assert.Equal(new[] { "a" }, ServerModPause.Current.ResumingIds);
        Assert.Empty(ServerModPause.Current.PausedIds);

        int steps = 0;
        h.EachStep = () => { if (++steps == 5) a.AlcAlive = false; h.Backend.Scan(); };
        h.Pump();

        Assert.Equal("reply", h.Backend.Calls.Last());
        Assert.Contains(h.Replies.Last(), r => r.Guid == "a");
        Assert.True(h.Now < ServerModPause.ResumeRowsWaitSeconds);
        h.Now += 1; ServerModPause.Tick();
        Assert.Equal(new[] { "a" }, resumed.Single().Ids);
        Assert.DoesNotContain("reanswer", h.Backend.Calls);   // the reply already carried it
    }

    [Fact]
    public void A_mod_that_loads_only_after_the_wait_is_reported_then_re_answered()
    {
        var h = new Harness();
        var a = h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();
        a.AlcAlive = true;
        h.Backend.Calls.Clear();
        var resumed = new List<ModsResumed>();
        using var sub = Progenitor.Events.EventBus.On<ModsResumed>(resumed.Add);

        h.Request(Smh("a"));
        h.Pump();                                   // never loads within the wait
        Assert.Equal("reply", h.Backend.Calls.Last());
        Assert.True(h.Now >= ServerModPause.ResumeRowsWaitSeconds);
        Assert.Empty(resumed);

        a.AlcAlive = false;
        h.ScanAndTick();
        Assert.Equal(ServerModPause.ReasonServerRunsIt, resumed.Single().Reason);
        Assert.Equal("reanswer", h.Backend.Calls.Last());
    }

    [Fact]
    public void A_mod_still_resuming_after_a_leave_stays_paused_on_a_server_that_does_not_run_it()
    {
        var h = new Harness();
        var a = h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();                                // server 1: paused
        a.AlcAlive = true;
        ServerModPause.OnConnectionEnded();         // leave: released, cannot load yet
        Assert.Equal(new[] { "a" }, ServerModPause.Current.ResumingIds);
        h.Backend.Calls.Clear();
        h.Notices.Clear();

        h.Request(Optional("other"));               // server 2 does not run "a"

        Assert.Equal(new[] { "keep:a", "reply" }, h.Backend.Calls);
        Assert.Equal(new[] { "a" }, ServerModPause.Current.PausedIds);
        a.AlcAlive = false;
        h.ScanAndTick();
        Assert.True(h.Paused("a"));                 // a scan does not load a paused unit
        Assert.Single(h.Notices);
    }

    [Fact]
    public void A_mod_still_resuming_stays_paused_on_a_server_without_Progenitor()
    {
        var h = new Harness();
        var a = h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();
        a.AlcAlive = true;
        ServerModPause.OnConnectionEnded();
        h.Backend.Calls.Clear();
        h.AskedRecord.Clear();

        ServerModPause.OnNoProgenitorDetected(10);
        Assert.Equal("keep:a", h.Backend.Calls[0]);
        Assert.Empty(ServerModPause.Current.ResumingIds);
        Assert.True(ServerModPause.Current.ServerWithoutProgenitor);
    }

    [Fact]
    public void Resumed_mods_not_yet_reported_survive_a_Core_swap()
    {
        var h = new Harness();
        var a = h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();
        a.AlcAlive = true;
        ServerModPause.OnConnectionEnded();
        Assert.Contains("a\t" + ServerModPause.ReasonDisconnect, h.Backend.ResumeReports);

        ServerModPause.Uninstall();                 // Core swap
        var seams = ServerModPause.Test;
        ServerModPause.ResetForTests();
        ServerModPause.Test = seams;
        var resumed = new List<ModsResumed>();
        using var sub = Progenitor.Events.EventBus.On<ModsResumed>(resumed.Add);

        a.AlcAlive = false;
        h.ScanAndTick();
        Assert.Equal(ServerModPause.ReasonDisconnect, resumed.Single().Reason);
        Assert.Equal("", h.Backend.ResumeReports);
    }

    [Fact]
    public void A_resuming_mod_nobody_recorded_is_rediscovered_and_reported()
    {
        var h = new Harness();
        var a = h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();
        a.AlcAlive = true;
        ServerModPause.OnConnectionEnded();
        h.Backend.ResumeReports = "";               // lost (a Bootstrap from before the slot existed)
        var resumed = new List<ModsResumed>();
        using var sub = Progenitor.Events.EventBus.On<ModsResumed>(resumed.Add);

        h.ScanAndTick();                            // still blocked: rediscovered, not reported
        Assert.Empty(resumed);
        a.AlcAlive = false;
        h.ScanAndTick();
        Assert.Single(resumed);
    }

    [Fact]
    public void A_waiting_resume_logs_once_per_state_change()
    {
        string a1 = PluginSuspendPolicy.FormatResumePending("A.dll", settled: true, pulses: 1);
        Assert.Equal(a1, PluginSuspendPolicy.FormatResumePending("A.dll", settled: true, pulses: 3));
        Assert.NotEqual(a1, PluginSuspendPolicy.FormatResumePending("A.dll", settled: false, pulses: 0));
        Assert.NotEqual(a1, PluginSuspendPolicy.FormatResumePending("A.dll", settled: true, pulses: PluginSuspendPolicy.MaxCollectPulses));
        string src = SourcePin.ReadFile("src", "Progenitor.Bootstrap", "ReloaderHost.cs");
        Assert.Contains("if (!string.Equals(note, unit.LastNote, StringComparison.Ordinal))", src);
    }

    [Fact]
    public void The_Mods_tab_shows_a_resuming_mod_as_resuming_not_paused()
    {
        var snap = ModMenuInventory.Build(Array.Empty<ModMenuLocalObservation>(), Array.Empty<ModRecord>(),
            Array.Empty<ModRecord>(), null, "3", resuming: new[] { new ModMenuLocalObservation("a", "a", "", false) });
        Assert.Equal(ModMenuInventory.StatusResuming, snap.Rows.Single(r => r.Id == "a").Status);
    }

    [Fact]
    public void A_carried_request_is_answered_only_on_the_same_live_connection()
    {
        Assert.True(CarriedRequest.MayAnswer("h:1", "c", "h:1", "c", connectionLive: true));
        Assert.False(CarriedRequest.MayAnswer("h:1", "c", "h:1", "c", connectionLive: false));   // disconnect screen
        Assert.False(CarriedRequest.MayAnswer("h:1", "c", "h:2", "c", true));
        Assert.False(CarriedRequest.MayAnswer("h:1", "c", "h:1", "c2", true));                 // a reconnect
        Assert.False(CarriedRequest.MayAnswer("", "", "", "", true));

        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        var queued = new List<Action>();
        ServerModPause.Test!.Post = queued.Add;
        h.Request();
        ServerModPause.Uninstall();
        h.Backend.Connection = "203.0.113.5:9876@2";   // the client reconnected meanwhile
        Assert.False(ServerModPause.TakeCarriedRequest(true, out _, out _));
        Assert.Equal("", h.Backend.PendingRequest);    // discarded
    }

    [Fact]
    public void A_resume_on_leave_that_loads_late_is_reported_without_a_re_answer()
    {
        var h = new Harness();
        var a = h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();
        a.AlcAlive = true;
        var resumed = new List<ModsResumed>();
        using var sub = Progenitor.Events.EventBus.On<ModsResumed>(resumed.Add);

        ServerModPause.OnConnectionEnded();
        Assert.Empty(resumed);
        a.AlcAlive = false;
        h.ScanAndTick();

        Assert.Equal(ServerModPause.ReasonDisconnect, resumed.Single().Reason);
        Assert.DoesNotContain("reanswer", h.Backend.Calls);
    }

    [Fact]
    public void Resuming_a_dependent_loads_its_paused_dependency_first()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Backend.Add("b", "b.dll", Smh("b")).Deps = new[] { "a" };
        h.Request();
        Assert.True(h.Paused("a") && h.Paused("b"));
        h.Backend.Calls.Clear();

        h.Request(Smh("b"));                        // asks only for the dependent

        Assert.Equal("resume:a,b", h.Backend.Calls[0]);
    }

    [Fact]
    public void A_paused_mod_is_resumed_by_any_of_its_rows_after_a_Core_swap()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"), Smh("a.core"));
        h.Request();
        Assert.Contains(h.Backend.Suspended().Single().Rows, r => r == "a.core");   // kept by Bootstrap

        ServerModPause.Uninstall();                 // Core swap: Core memory is gone
        var seams = ServerModPause.Test;
        ServerModPause.ResetForTests();
        ServerModPause.Test = seams;
        h.Backend.Calls.Clear();

        h.Request(Smh("a.core"));                   // not the plugin's own GUID
        Assert.Equal("resume:a", h.Backend.Calls[0]);
    }

    [Fact]
    public void A_server_that_asks_about_a_paused_mod_as_optional_resumes_it()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();
        h.Backend.Calls.Clear();
        h.Request(Optional("a"));
        Assert.Equal("resume:a", h.Backend.Calls[0]);
    }

    [Fact]
    public void A_disconnect_during_the_deferred_reply_abandons_it_without_pause_reply_or_profile()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        var queued = new List<Action>();
        ServerModPause.Test!.Post = queued.Add;     // the reconcile is one frame later

        h.Request();
        Assert.NotEqual("", h.Backend.PendingRequest);
        ServerModPause.OnConnectionEnded();         // left before that frame
        foreach (var work in queued.ToList()) work();

        Assert.DoesNotContain("reply", h.Backend.Calls);
        Assert.False(h.Paused("a"));
        Assert.False(File.Exists(ServerProfiles.FilePath));
        Assert.Equal("", h.Backend.PendingRequest);
    }

    [Fact]
    public void A_disconnect_while_waiting_for_rows_abandons_the_reply()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();
        h.Backend.Calls.Clear();
        h.Backend.NeverBack = 1;
        h.Request(Smh("a"));
        ServerModPause.OnConnectionEnded();
        h.Pump();
        Assert.DoesNotContain("reply", h.Backend.Calls);
    }

    [Fact]
    public void A_deferred_request_survives_a_Core_swap_and_is_answered_by_the_next_generation()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        var queued = new List<Action>();
        ServerModPause.Test!.Post = queued.Add;
        h.Request(true, Optional("srv"));           // deferred; the queued work dies with this Core
        ServerModPause.Uninstall();

        ServerModPause.Test!.Post = a => a();
        Assert.True(ServerModPause.TakeCarriedRequest(true, out var asked, out bool truncated));
        Assert.True(truncated);
        Assert.Equal("srv", asked.Single().Guid);
        Assert.Equal(NetworkCompatibilityLevel.NoNeedForSync, asked.Single().Level);
        Assert.False(ServerModPause.TakeCarriedRequest(true, out _, out _));   // taken once

        ServerModPause.OnRequest(asked, truncated, h.Reply);
        Assert.Equal("reply", h.Backend.Calls.Last());
        Assert.Equal("", h.Backend.PendingRequest);
    }

    [Fact]
    public void The_carried_request_round_trips()
    {
        var rows = new List<ModRecord> { Smh("a", "1.2.3"), Optional("b"), ModRecord.Undeclared("c", "2") };
        string text = CarriedRequest.Serialize(7, true, rows, "h|x:1", "c@9");
        Assert.True(CarriedRequest.TryParse(text, out long seq, out bool truncated, out var back, out var host, out var conn));
        Assert.Equal((7L, true, "h x:1", "c@9"), (seq, truncated, host, conn));
        Assert.Equal(rows.Select(r => (r.Guid, r.Version, r.Level, r.VersionRule, r.Declared)),
            back.Select(r => (r.Guid, r.Version, r.Level, r.VersionRule, r.Declared)));
        Assert.False(CarriedRequest.TryParse("garbage", out _, out _, out _));
    }

    [Fact]
    public void The_no_progenitor_pause_never_fires_while_this_client_hosts_or_cannot_tell()
    {
        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));

        h.Backend.Hosting = true;                   // a private game: its server never asks the host
        ServerModPause.OnNoProgenitorDetected(10);
        h.Backend.Hosting = null;                   // Bootstrap could not say
        ServerModPause.OnNoProgenitorDetected(10);
        h.Backend.Hosting = false;
        foreach (var host in new[] { "", "Aqf27THwTQgIoENZsJCBAvJNEAA", "127.0.0.1:9876", "10.0.0.5", "not a host:1" })
        {
            h.Host = host;
            ServerModPause.OnNoProgenitorDetected(10);
        }
        Assert.False(h.Paused("a"));
        Assert.Empty(h.Backend.Calls);

        h.Host = "play.example.com:9876";
        ServerModPause.OnNoProgenitorDetected(10);
        Assert.True(h.Paused("a"));
    }

    [Theory]
    [InlineData("203.0.113.5:9876", true)]
    [InlineData("[2001:db8::1]:9876", true)]
    [InlineData("play.example.com:9876", true)]
    [InlineData("203.0.113.5", false)]
    [InlineData("203.0.113.5:0", false)]
    [InlineData("1.2.3:9876", false)]
    [InlineData("server:9876", false)]
    [InlineData("Aqf27THwTQgIoENZsJCBAvJNEAA", false)]
    [InlineData("127.0.0.1:9876", false)]
    [InlineData("", false)]
    public void Only_recognised_remote_address_forms_allow_the_detection(string host, bool ok)
    {
        Assert.Equal(ok, ServerModPausePolicy.IsRecognisedRemoteHost(host));
        Assert.Equal(ok, ServerModPausePolicy.MayDetectNoProgenitor(false, host));
        Assert.False(ServerModPausePolicy.MayDetectNoProgenitor(null, host));
        Assert.False(ServerModPausePolicy.MayDetectNoProgenitor(true, host));
    }

    [Fact]
    public void The_no_progenitor_plan_never_pauses_a_plugin_the_recorded_request_asked_about()
    {
        var b = new FakeBackend();
        b.Add("a", "a.dll", Smh("a"));
        b.Add("b", "b.dll", Smh("b"), Smh("b.core"));
        var plan = ServerModPausePolicy.PlanNoProgenitor(b.Provided(), b.Owners(), b.Suspendable().ToList(), ClosureOf(b), true,
            new[] { Smh("a") });
        Assert.Equal(new[] { "b" }, plan.Pause);
        var byRow = ServerModPausePolicy.PlanNoProgenitor(b.Provided(), b.Owners(), b.Suspendable().ToList(), ClosureOf(b), true,
            new[] { Smh("b.core") });
        Assert.Equal(new[] { "a" }, byRow.Pause);
    }

    [Fact]
    public void An_unreadable_profiles_file_is_never_overwritten_and_turns_auto_pause_off()
    {
        ServerProfiles.ResetForTests();
        ServerProfiles.SetAutoPause("other:1", false);
        File.WriteAllText(ServerProfiles.FilePath, "{ not json");
        Assert.False(ServerProfiles.AutoPause("203.0.113.5:9876"));
        ServerProfiles.NotePaused("203.0.113.5:9876", new[] { "a" }, DateTime.UtcNow);
        Assert.Equal("{ not json", File.ReadAllText(ServerProfiles.FilePath));

        var h = new Harness();
        h.Backend.Add("a", "a.dll", Smh("a"));
        h.Request();
        Assert.False(h.Paused("a"));                // fail-safe: an opt-out may be in there
    }

    [Fact]
    public void A_readable_profiles_file_is_replaced_atomically_and_keeps_other_servers()
    {
        ServerProfiles.SetAutoPause("other:1", false);
        ServerProfiles.NotePaused("mine:1", new[] { "a" }, DateTime.UtcNow);
        Assert.False(ServerProfiles.AutoPause("other:1"));
        Assert.Equal(new[] { "a" }, ServerProfiles.Get("mine:1").LastPaused);
        Assert.False(File.Exists(ServerProfiles.FilePath + ".tmp"));
    }

    // ------------------------------------------------------------------ owners, deny text, Mods tab

    [Fact]
    public void Provided_rows_remember_their_owning_plugin_until_unprovided()
    {
        Handshake.ResetTables();
        try
        {
            Handshake.ProvideOwned("owner.plugin", "row.one", "1.0.0");
            Handshake.Provide("row.static", "1.0.0");
            var owners = Handshake.CopyProvidedOwners();
            Assert.Equal("owner.plugin", owners["row.one"]);
            Assert.False(owners.ContainsKey("row.static"));
            Handshake.Unprovide("row.one");
            Assert.Empty(Handshake.CopyProvidedOwners());
        }
        finally { Handshake.ResetTables(); }
    }

    [Fact]
    public void The_deny_panel_says_a_restart_is_needed_for_a_restart_only_mod_and_keeps_JOINWORD1()
    {
        var rows = ModManifestDeny.BuildLocalRows(Array.Empty<ModRecord>(), new[] { Smh("plain.mod"), Smh("hot.mod") },
            id => id == "plain.mod");
        Assert.True(rows.Single(r => r.Id == "plain.mod").RestartOnly);
        Assert.False(rows.Single(r => r.Id == "hot.mod").RestartOnly);
        var report = new ModDenyReport("kick", "host", "", "", "", false, rows);
        var lines = ModDenyText.Lines(report);
        Assert.Contains(ModDenyText.YourModsFix, lines);
        Assert.Single(lines, l => l == ModDenyText.RestartOnlyNote);
    }

    [Fact]
    public void A_paused_plugin_is_listed_on_the_Mods_tab_as_paused_for_this_server()
    {
        var snap = ModMenuInventory.Build(Array.Empty<ModMenuLocalObservation>(), Array.Empty<ModRecord>(),
            Array.Empty<ModRecord>(), null, "3", paused: new[] { new ModMenuLocalObservation("a", "Alpha", "1.0.0", false) });
        var row = snap.Rows.Single(r => r.Id == "a");
        Assert.Equal(ModMenuInventory.StatusPaused, row.Status);
        Assert.Equal("Local", row.Location);
        Assert.Equal("Alpha", row.DisplayName);
        var sidebar = new ModsSidebarRow { Id = "a", Name = "Alpha", Version = "1.0.0", Inventory = row };
        Assert.Equal("1.0.0 · " + ModMenuInventory.StatusPaused, sidebar.Subtitle);
    }
}
