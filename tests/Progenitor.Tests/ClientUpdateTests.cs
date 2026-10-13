using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>AUTOUPDATE1 (M6.1): client update detection, pins, the pre-join decision and the update runner. Game-free.</summary>
[Collection("ClientUpdates")]
public sealed class ClientUpdateTests : IDisposable
{
    private readonly string _root;

    public ClientUpdateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "progenitor-autoupdate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "BepInEx", "config"));
        ClientUpdates.Reset();
    }

    public void Dispose()
    {
        ClientUpdates.Reset();
        try { Directory.Delete(_root, recursive: true); }
        catch { /* temp */ }
    }

    private static ManagedPackage Managed(string key, string version, string pluginId = "")
    {
        var m = new ManagedPackage { Key = key, Version = version };
        if (pluginId.Length > 0) m.PluginIds.Add(pluginId);
        return m;
    }

    private static IndexListing Listing(string key, string latest, params string[] deps) =>
        new() { Key = key, Name = key.Split('-').Last(), Latest = latest, Dependencies = deps };

    private static Dictionary<string, IndexListing> Index(params IndexListing[] listings) =>
        listings.ToDictionary(l => l.Key, StringComparer.OrdinalIgnoreCase);

    private static UpdateReport Detect(IEnumerable<ManagedPackage> managed, Dictionary<string, IndexListing> index,
        Dictionary<string, string>? pins = null, Func<string, bool>? present = null, bool nsfw = false) =>
        UpdateDetection.Detect(managed.ToList(), index, pins, present, nsfw, new DateTime(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc));

    // ---- Detection ----

    [Fact]
    public void A_newer_listing_is_an_update_and_an_equal_or_older_one_is_not()
    {
        var r = Detect(
            new[] { Managed("Team-Chat", "1.2.0"), Managed("Team-Maps", "2.0.0"), Managed("Team-Old", "3.0.0") },
            Index(Listing("Team-Chat", "1.3.0"), Listing("Team-Maps", "2.0.0"), Listing("Team-Old", "2.9.9")));
        Assert.Single(r.Items);
        Assert.Equal("Team-Chat", r.Items[0].Package);
        Assert.Equal("1.2.0", r.Items[0].Installed);
        Assert.Equal("1.3.0", r.Items[0].Latest);
        Assert.False(r.Items[0].Major);
        Assert.Equal(1, r.Count);
        Assert.Equal(3, r.Managed);
    }

    [Fact]
    public void A_higher_first_number_is_a_major_update()
    {
        var r = Detect(new[] { Managed("Team-Maps", "1.4.2") }, Index(Listing("Team-Maps", "2.0.0")));
        Assert.True(r.Items[0].Major);
        Assert.Contains("· major", r.Items[0].Line);
        Assert.True(UpdateDetection.IsMajor("v1.9", "2.0"));
        Assert.False(UpdateDetection.IsMajor("1.9", "1.10"));
        Assert.False(UpdateDetection.IsMajor("abc", "2.0"));
    }

    [Fact]
    public void A_pin_at_the_installed_version_keeps_the_mod_out_of_the_count_and_a_stale_pin_does_not()
    {
        var managed = new[] { Managed("Team-Chat", "1.2.0"), Managed("Team-Maps", "1.0.0") };
        var index = Index(Listing("Team-Chat", "1.3.0"), Listing("Team-Maps", "1.1.0"));
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["team-chat"] = "1.2.0", ["Team-Maps"] = "0.9.0" };
        var r = Detect(managed, index, pins);
        Assert.Equal(2, r.Items.Count);
        Assert.Equal(1, r.Count);
        var chat = r.Find("Team-Chat")!;
        Assert.True(chat.Pinned);
        Assert.Equal("1.2.0", chat.PinnedAt);
        Assert.False(r.Find("Team-Maps")!.Pinned);
        // Unpinned first.
        Assert.Equal("Team-Maps", r.Items[0].Package);
    }

    [Fact]
    public void Changed_unlisted_deprecated_nsfw_and_loader_packages_are_skipped_with_a_reason()
    {
        var changed = Managed("Team-Changed", "1.0.0");
        changed.Changed = true;
        var nsfw = Listing("Team-Spicy", "2.0.0");
        nsfw.Nsfw = true;
        var dep = Listing("Team-Gone", "2.0.0");
        dep.Deprecated = true;
        var r = Detect(
            new[] { changed, Managed("Team-Unlisted", "1.0.0"), Managed("Team-Gone", "1.0.0"), Managed("Team-Spicy", "1.0.0"),
                Managed("BepInEx-BepInExPack_V_Rising", "1.0.0") },
            Index(Listing("Team-Changed", "2.0.0"), dep, nsfw, Listing("BepInEx-BepInExPack_V_Rising", "2.0.0")));
        Assert.Empty(r.Items);
        Assert.Equal(UpdateDetection.SkipChanged, r.Skipped.Single(s => s.Package == "Team-Changed").Reason);
        Assert.Equal(UpdateDetection.SkipNotListed, r.Skipped.Single(s => s.Package == "Team-Unlisted").Reason);
        Assert.Equal(UpdateDetection.SkipDeprecated, r.Skipped.Single(s => s.Package == "Team-Gone").Reason);
        Assert.Equal(UpdateDetection.SkipNsfw, r.Skipped.Single(s => s.Package == "Team-Spicy").Reason);
        Assert.Equal(UpdateDetection.SkipLoader, r.Skipped.Single(s => s.Package == "BepInEx-BepInExPack_V_Rising").Reason);

        // With NSFW listings shown, the flagged one is an update.
        var shown = Detect(new[] { Managed("Team-Spicy", "1.0.0") }, Index(nsfw), nsfw: true);
        Assert.Single(shown.Items);
    }

    [Fact]
    public void A_dependency_that_is_not_installed_is_named_and_one_that_is_installed_is_not()
    {
        var r = Detect(
            new[] { Managed("Team-Chat", "1.0.0") },
            Index(Listing("Team-Chat", "1.1.0", "dyno-ExampleCommandFramework-0.11.0", "Team-Lib-1.0.0",
                "BepInEx-BepInExPack_V_Rising-1.733.2", "garbage")),
            present: key => key == "Team-Lib");
        var item = r.Items.Single();
        Assert.Equal(new[] { "dyno-ExampleCommandFramework" }, item.NewDependencies);
        Assert.Contains("needs dyno-ExampleCommandFramework (not installed)", item.Line);
        Assert.Equal("dyno-ExampleCommandFramework", UpdateDetection.DependencyKey("dyno-ExampleCommandFramework-0.11.0"));
        Assert.Equal("", UpdateDetection.DependencyKey("noversion"));
    }

    [Fact]
    public void The_ledger_reader_takes_mod_and_pending_rows_and_marks_changed_bytes()
    {
        string reload = Path.Combine(_root, "BepInEx", "progenitor-reload");
        Directory.CreateDirectory(reload);
        byte[] good = Encoding.UTF8.GetBytes("good image");
        File.WriteAllBytes(Path.Combine(reload, "chat.dll"), good);
        File.WriteAllBytes(Path.Combine(reload, "maps.dll"), Encoding.UTF8.GetBytes("edited by hand"));
        string sha = Hex(SHA256.HashData(good));
        var at = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        new ServerInstallLedger("1", "", "", at, new[]
        {
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/chat.dll", sha, good.Length, "mod", "package:Team-Chat@1.2.0", at, pluginId: "team.chat"),
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/maps.dll", sha, good.Length, "mod", "package:Team-Maps@1.0.0", at),
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/x.dll", sha, 1, "framework", "client-image:abc", at),
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/gone.dll", sha, 1, "mod", "package:Team-Gone@1.0.0", at, uninstallPending: true),
        }).Write(_root);

        var managed = ClientUpdateInputs.ReadManaged(_root);
        Assert.Equal(new[] { "Team-Chat", "Team-Maps" }, managed.Select(m => m.Key).ToArray());
        Assert.Equal("1.2.0", managed[0].Version);
        Assert.Equal("team.chat", managed[0].PluginId);
        Assert.False(managed[0].Changed);
        Assert.True(managed[1].Changed);
    }

    [Fact]
    public void Pins_and_server_requirements_round_trip_through_their_files()
    {
        string bep = Path.Combine(_root, "BepInEx");
        var pins = new UpdatePins();
        pins.Pins["Team-Chat"] = "1.2.0";
        Assert.True(ClientUpdateInputs.WritePins(bep, pins, out _));
        Assert.Equal("1.2.0", ClientUpdateInputs.ReadPins(bep).Pins["team-chat"]);
        Assert.Empty(UpdatePins.Parse("{ not json").Pins);

        var memory = new ServerRequirementMemory();
        string key = ServerRequirementMemory.KeyOf("127.0.0.1", 9976);
        Assert.Equal("127.0.0.1:9976", key);
        memory.Note(key, new[] { new ServerRequirement { ModId = "team.chat", Version = "1.2.0", Rule = NetworkVersionRule.Minor } }, DateTime.UtcNow);
        Assert.True(ClientUpdateInputs.WriteRequirements(bep, memory, out _));
        var back = ClientUpdateInputs.ReadRequirements(bep).Find(key)!;
        Assert.Equal(NetworkVersionRule.Minor, back.Single().Rule);
        Assert.Equal("1.2.0", back.Single().Version);
        Assert.Equal("", ServerRequirementMemory.KeyOf("", 1));
    }

    [Fact]
    public void Server_memory_keeps_the_newest_servers_only()
    {
        var memory = new ServerRequirementMemory();
        var t = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < ServerRequirementMemory.MaxServers + 3; i++)
            memory.Note("10.0.0." + i + ":9876", Array.Empty<ServerRequirement>(), t.AddMinutes(i));
        Assert.Equal(ServerRequirementMemory.MaxServers, memory.Count);
        Assert.Null(memory.Find("10.0.0.0:9876"));
        Assert.NotNull(memory.Find("10.0.0.34:9876"));
    }

    [Fact]
    public void Server_requirements_skip_client_only_rows()
    {
        var rows = new[]
        {
            new Handshake.ModRecord("a", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Exact),
            new Handshake.ModRecord("b", "1.0.0", NetworkCompatibilityLevel.ClientOnly, NetworkVersionRule.Exact),
            new Handshake.ModRecord("c", "2.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor),
        };
        var reqs = ServerRequirements.Of(rows);
        Assert.Equal(new[] { "a", "c" }, reqs.Select(r => r.ModId).ToArray());
    }

    // ---- Pre-join prompt ----

    private static UpdateReport TwoUpdates() => Detect(
        new[] { Managed("Team-Chat", "1.2.0", "team.chat"), Managed("Team-Maps", "2.0.1", "team.maps") },
        Index(Listing("Team-Chat", "1.3.0"), Listing("Team-Maps", "2.0.2")));

    [Fact]
    public void An_unknown_server_is_offered_every_unpinned_update()
    {
        var d = PreJoinOffers.Decide(TwoUpdates(), null, UpdateMode.Automatic, promptOn: true, "1.2.3.4:9876", null);
        Assert.True(d.Show);
        Assert.False(d.RequirementsKnown);
        Assert.Equal(2, d.Offers.Count);
        Assert.Equal("1.2.3.4:9876|Team-Chat@1.3.0,Team-Maps@2.0.2", d.DismissKey);
        Assert.Contains("Chat 1.2.0 → 1.3.0", PreJoinOffers.Body(d));
    }

    [Fact]
    public void The_servers_exact_and_minor_rules_cap_the_offer_and_a_version_past_them_is_never_offered()
    {
        var reqs = new[]
        {
            new ServerRequirement { ModId = "team.chat", Version = "1.2.0", Rule = NetworkVersionRule.Exact },
            new ServerRequirement { ModId = "TEAM.MAPS", Version = "2.0.0", Rule = NetworkVersionRule.Minor },
        };
        var d = PreJoinOffers.Decide(TwoUpdates(), reqs, UpdateMode.Notify, true, "s", null);
        Assert.True(d.Show);
        Assert.Equal("Team-Maps", d.Offers.Single().Item.Package);
        Assert.Equal("2.0.2", d.Offers.Single().Version);
        var held = d.Held.Single();
        Assert.Equal("Team-Chat", held.Item.Package);
        Assert.Equal("this server needs exactly 1.2.0", held.Held);
        Assert.Contains("Not offered: Chat (this server needs exactly 1.2.0).", PreJoinOffers.Body(d));

        // An Exact rule naming the newest version offers it; a Minor rule on another minor holds it.
        var exactNew = new[] { new ServerRequirement { ModId = "team.chat", Version = "1.3.0", Rule = NetworkVersionRule.Exact } };
        Assert.Contains(PreJoinOffers.Decide(TwoUpdates(), exactNew, UpdateMode.Notify, true, "s", null).Offers, o => o.Item.Package == "Team-Chat");
        var minorOld = new[] { new ServerRequirement { ModId = "team.maps", Version = "1.9.0", Rule = NetworkVersionRule.Minor } };
        var held2 = PreJoinOffers.Decide(TwoUpdates(), minorOld, UpdateMode.Notify, true, "s", null).Held.Single();
        Assert.Equal("this server needs 1.9.x", held2.Held);

        // A rule of None compares nothing: the newest is offered.
        var none = new[] { new ServerRequirement { ModId = "team.chat", Version = "0.1", Rule = NetworkVersionRule.None } };
        Assert.Equal(2, PreJoinOffers.Decide(TwoUpdates(), none, UpdateMode.Notify, true, "s", null).Offers.Count);
    }

    [Fact]
    public void Nothing_is_shown_when_off_when_the_prompt_is_off_when_everything_is_held_or_after_join_anyway()
    {
        var report = TwoUpdates();
        Assert.Equal("off", PreJoinOffers.Decide(report, null, UpdateMode.Off, true, "s", null).Why);
        Assert.Equal("prompt-off", PreJoinOffers.Decide(report, null, UpdateMode.Automatic, false, "s", null).Why);
        Assert.Equal("not-checked", PreJoinOffers.Decide(UpdateReport.Empty, null, UpdateMode.Automatic, true, "s", null).Why);
        var allHeld = new[]
        {
            new ServerRequirement { ModId = "team.chat", Version = "1.2.0", Rule = NetworkVersionRule.Exact },
            new ServerRequirement { ModId = "team.maps", Version = "2.0.1", Rule = NetworkVersionRule.Exact },
        };
        var held = PreJoinOffers.Decide(report, allHeld, UpdateMode.Automatic, true, "s", null);
        Assert.False(held.Show);
        Assert.Equal("nothing", held.Why);

        var first = PreJoinOffers.Decide(report, null, UpdateMode.Automatic, true, "s", null);
        var dismissed = new HashSet<string> { first.DismissKey };
        Assert.Equal("dismissed", PreJoinOffers.Decide(report, null, UpdateMode.Automatic, true, "s", dismissed).Why);
        // Another server, or a newer version, asks again.
        Assert.True(PreJoinOffers.Decide(report, null, UpdateMode.Automatic, true, "other", dismissed).Show);
    }

    [Fact]
    public void Pinned_mods_are_never_offered_before_a_join()
    {
        var pins = new Dictionary<string, string> { ["Team-Chat"] = "1.2.0", ["Team-Maps"] = "2.0.1" };
        var r = Detect(
            new[] { Managed("Team-Chat", "1.2.0", "team.chat"), Managed("Team-Maps", "2.0.1", "team.maps") },
            Index(Listing("Team-Chat", "1.3.0"), Listing("Team-Maps", "2.0.2")), pins);
        var d = PreJoinOffers.Decide(r, null, UpdateMode.Automatic, true, "s", null);
        Assert.False(d.Show);
        Assert.Equal("nothing", d.Why);
    }

    // ---- The update state and the runner ----

    private void WireSync(IReadOnlyList<CatalogEntry> entries)
    {
        ClientUpdates.EntriesProbe = () => entries;
        ClientUpdates.GameRootProbe = () => _root;
        ClientUpdates.BepInExRootProbe = () => Path.Combine(_root, "BepInEx");
        ClientUpdates.RunOff = work => Task.FromResult(work());
        ClientUpdates.PostMain = a => a();
    }

    private void LedgerWith(string key, string version, string pluginId)
    {
        string reload = Path.Combine(_root, "BepInEx", "progenitor-reload");
        Directory.CreateDirectory(reload);
        byte[] bytes = Encoding.UTF8.GetBytes(key + version);
        File.WriteAllBytes(Path.Combine(reload, key + ".dll"), bytes);
        var at = DateTime.UtcNow;
        new ServerInstallLedger("1", "", "", at, new[]
        {
            new ServerInstallLedgerEntry("BepInEx/progenitor-reload/" + key + ".dll", Hex(SHA256.HashData(bytes)), bytes.Length, "mod",
                "package:" + key + "@" + version, at, pluginId: pluginId),
        }).Write(_root);
    }

    private static CatalogEntry Entry(string key, string version) =>
        new(new ManifestMod("", version, NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.None, false, "Team", key),
            CatalogProvenance.AdaptedListing, InstallReasons.SourceThunderstore, InstallReasons.SourceLabelThunderstore,
            "2026-10-09T00:00:00Z", displayName: key.Split('-').Last(), owner: "Team", packageName: key);

    [Fact]
    public void A_recheck_reads_the_ledger_and_the_index_on_hand_and_the_badge_counts_it()
    {
        LedgerWith("Team-Chat", "1.0.0", "team.chat");
        WireSync(new[] { Entry("Team-Chat", "1.1.0") });
        int before = ClientUpdates.Version;
        ClientUpdates.Recheck("test");
        var rep = ClientUpdates.Report;
        Assert.True(rep.Count == 1, "managed=" + rep.Managed + " items=" + rep.Items.Count + " skipped=" + string.Join(";", rep.Skipped.Select(x => x.Package + ":" + x.Reason)) + " ran=" + rep.HasRun);
        Assert.Equal("1 update available", ClientUpdates.BadgeText);
        Assert.True(ClientUpdates.Version > before);
        Assert.Contains("1 update available", ClientUpdates.HeaderText());

        // The same list again does not move the version (the Install tab rebuilds on it).
        int same = ClientUpdates.Version;
        ClientUpdates.Recheck("again");
        Assert.Equal(same, ClientUpdates.Version);

        // Off hides the badge but keeps the report.
        ClientUpdates.ModeProbe = () => UpdateMode.Off;
        Assert.Equal(0, ClientUpdates.BadgeCount);
        Assert.Equal("", ClientUpdates.BadgeText);
        Assert.Equal("3 updates available", ClientUpdates.BadgeTextFor(3));
    }

    [Fact]
    public void Keep_this_version_writes_the_pin_file_and_unpin_removes_it()
    {
        LedgerWith("Team-Chat", "1.0.0", "team.chat");
        WireSync(new[] { Entry("Team-Chat", "1.1.0") });
        ClientUpdates.Recheck("test");
        Assert.True(ClientUpdates.SetPinned("Team-Chat", true, out string msg), msg);
        Assert.Equal("1.0.0", ClientUpdateInputs.ReadPins(Path.Combine(_root, "BepInEx")).Pins["Team-Chat"]);
        Assert.Equal(0, ClientUpdates.Report.Count);
        Assert.True(ClientUpdates.Report.Items.Single().Pinned);
        Assert.True(ClientUpdates.SetPinned("Team-Chat", false, out msg), msg);
        Assert.Equal(1, ClientUpdates.Report.Count);
        Assert.False(ClientUpdates.SetPinned("Team-Other", true, out msg));
    }

    private sealed class FakePipeline
    {
        public readonly List<string> Calls = new();
        public bool Hot = true;
        public string[] NewDependencies = Array.Empty<string>();

        public UpdatePipeline Build() => new()
        {
            Inspect = (key, _) => { Calls.Add("inspect " + key); return Task.FromResult(new InspectCallResult(true, null, null, null)); },
            Plan = (key, _) =>
            {
                Calls.Add("plan " + key);
                return Task.FromResult(PlanSetFor(key, NewDependencies));
            },
            Execute = set =>
            {
                Calls.Add("execute " + set.Members.Last(m => m.IsRoot).Package);
                var members = new List<ExecuteMemberResult>();
                foreach (var m in set.Members) members.Add(new ExecuteMemberResult(m.Package, m.Version, "p", Hot, null, m.IsRoot));
                return ExecuteSetResult.Written(members);
            },
            Release = key => Calls.Add("release " + key),
            ZipPath = _ => null,
            RunOnMain = work => Task.FromResult(work()),
            Post = a => a(),
            Installed = _ => false,
        };
    }

    private static InstallPlanSet PlanSetFor(string key, string[] deps)
    {
        var members = new List<InstallPlanMember>();
        foreach (var d in deps) members.Add(new InstallPlanMember(d, "1.0.0", PlanFor(d), false, "closure", "", ""));
        var root = PlanFor(key);
        members.Add(new InstallPlanMember(key, "1.1.0", root, true, "root", "", ""));
        return InstallPlanSet.Planned(root, members, Array.Empty<ClosureMember>(), InstallTarget.Client);
    }

    private static InstallPlan PlanFor(string key) => new(
        key + "-hash", Entry(key, "1.1.0"), InstallTarget.Client, InstallDecision.ApplyNow, "test",
        Array.Empty<ArtifactHash>(), Array.Empty<PlanDependency>(), null, null);

    [Fact]
    public void Update_all_asks_once_with_every_update_then_writes_each_through_the_pipeline()
    {
        LedgerWith("Team-Chat", "1.0.0", "team.chat");
        WireSync(new[] { Entry("Team-Chat", "1.1.0") });
        ClientUpdates.Recheck("test");
        var fake = new FakePipeline();
        ClientUpdates.Pipeline = fake.Build();
        Action? yes = null;
        IReadOnlyList<(string Label, string Value)>? shownFacts = null;
        string? title = null;
        ClientUpdates.OpenConfirm = (t, body, facts, caption, onYes, onNo) => { title = t; shownFacts = facts; yes = onYes; return true; };
        IReadOnlyList<PreparedUpdate>? result = null;
        bool applied = false;

        Assert.True(ClientUpdates.BeginUpdate(new[] { "Team-Chat" }, "test", confirm: true, (p, a) => { result = p; applied = a == UpdateRunEnd.Applied && ClientUpdates.AnyApplied(p); }, out string msg), msg);
        SpinUntil(() => yes != null);
        Assert.Equal("Update 1 mod?", title);
        Assert.Equal(("Chat", "1.0.0 → 1.1.0"), shownFacts![0]);
        Assert.Equal("confirming", ClientUpdates.RunState);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("execute", StringComparison.Ordinal));

        yes!();
        SpinUntil(() => result != null);
        Assert.True(applied);
        Assert.Equal("loaded", result!.Single().Outcome);
        Assert.Equal(new[] { "inspect Team-Chat", "plan Team-Chat", "plan Team-Chat", "execute Team-Chat" }, fake.Calls.ToArray());
        Assert.Equal("Chat 1.1.0 is installed and loads now.", ClientUpdates.ResultBody(result!));
        Assert.Equal("done", ClientUpdates.RunState);
    }

    [Fact]
    public void Cancel_writes_nothing_and_releases_the_downloads()
    {
        LedgerWith("Team-Chat", "1.0.0", "team.chat");
        WireSync(new[] { Entry("Team-Chat", "1.1.0") });
        ClientUpdates.Recheck("test");
        var fake = new FakePipeline();
        ClientUpdates.Pipeline = fake.Build();
        Action? no = null;
        ClientUpdates.OpenConfirm = (t, body, facts, caption, onYes, onNo) => { no = onNo; return true; };
        bool finished = false, applied = true;
        ClientUpdates.BeginUpdate(new[] { "Team-Chat" }, "test", true, (p, a) => { finished = true; applied = a != UpdateRunEnd.Cancelled; }, out _);
        SpinUntil(() => no != null);
        no!();
        Assert.True(finished);
        Assert.False(applied);
        Assert.Contains("release Team-Chat", fake.Calls);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("execute", StringComparison.Ordinal));
        Assert.Equal("cancelled", ClientUpdates.RunState);
    }

    [Fact]
    public void Without_a_confirmation_an_update_that_installs_a_new_mod_still_asks_first()
    {
        LedgerWith("Team-Chat", "1.0.0", "team.chat");
        WireSync(new[] { Entry("Team-Chat", "1.1.0") });
        ClientUpdates.Recheck("test");
        var fake = new FakePipeline { NewDependencies = new[] { "dyno-ExampleCommandFramework" } };
        ClientUpdates.Pipeline = fake.Build();
        bool asked = false;
        IReadOnlyList<(string Label, string Value)>? shownFacts = null;
        ClientUpdates.OpenConfirm = (t, body, facts, caption, onYes, onNo) => { asked = true; shownFacts = facts; return true; };
        ClientUpdates.BeginUpdate(new[] { "Team-Chat" }, "pre-join", confirm: false, null, out _);
        SpinUntil(() => asked);
        Assert.Contains(("", "also installs dyno-ExampleCommandFramework"), shownFacts!);
        Assert.DoesNotContain(fake.Calls, c => c.StartsWith("execute", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_new_mods_the_pre_join_path_writes_straight_away_and_a_restart_route_says_so()
    {
        LedgerWith("Team-Chat", "1.0.0", "team.chat");
        WireSync(new[] { Entry("Team-Chat", "1.1.0") });
        ClientUpdates.Recheck("test");
        var fake = new FakePipeline { Hot = false };
        ClientUpdates.Pipeline = fake.Build();
        ClientUpdates.OpenConfirm = (_, _, _, _, _, _) => throw new InvalidOperationException("must not ask");
        IReadOnlyList<PreparedUpdate>? result = null;
        ClientUpdates.BeginUpdate(new[] { "Team-Chat" }, "pre-join", confirm: false, (p, a) => result = p, out _);
        SpinUntil(() => result != null);
        Assert.Equal("restart", result!.Single().Outcome);
        Assert.Equal("Chat 1.1.0 loads at the next start.", ClientUpdates.ResultBody(result!));
    }

    [Fact]
    public void A_second_run_is_refused_while_one_is_open()
    {
        LedgerWith("Team-Chat", "1.0.0", "team.chat");
        WireSync(new[] { Entry("Team-Chat", "1.1.0") });
        ClientUpdates.Recheck("test");
        ClientUpdates.Pipeline = new FakePipeline().Build();
        ClientUpdates.OpenConfirm = (_, _, _, _, _, _) => true;
        Assert.True(ClientUpdates.BeginUpdate(new[] { "Team-Chat" }, "a", true, null, out _));
        SpinUntil(() => ClientUpdates.RunState == "confirming");
        Assert.False(ClientUpdates.BeginUpdate(new[] { "Team-Chat" }, "b", true, null, out string msg));
        Assert.Equal("an update is already running", msg);
        Assert.False(ClientUpdates.BeginUpdate(new[] { "Team-Nope" }, "c", true, null, out msg));
    }

    [Fact]
    public void While_connected_a_mod_the_server_checks_at_another_version_is_not_updated()
    {
        LedgerWith("Team-Chat", "1.0.0", "team.chat");
        WireSync(new[] { Entry("Team-Chat", "1.1.0") });
        ClientUpdates.Recheck("test");
        ClientUpdates.Pipeline = new FakePipeline().Build();
        ClientUpdates.ConnectedProbe = () => new[] { new ServerRequirement { ModId = "team.chat", Version = "1.0.0", Rule = NetworkVersionRule.Exact } };
        var item = ClientUpdates.Report.Items.Single();
        Assert.Equal("the server you are on needs exactly 1.0.0", ClientUpdates.BlockedReason(item));
        Assert.False(ClientUpdates.BeginUpdate(new[] { "Team-Chat" }, "test", true, null, out string msg));
        Assert.Equal("not updated: Chat (the server you are on needs exactly 1.0.0)", msg);
        var cells = InstallModsPane.UpdateRowCells(item, ClientUpdates.BlockedReason(item));
        Assert.DoesNotContain(cells, c => c.Key == InstallModsPane.CellUpdateOne);
        Assert.Contains("not now: the server you are on needs exactly 1.0.0", cells.Single(c => c.Key == InstallModsPane.CellUpdateText).Text);
        ClientUpdates.ConnectedProbe = null;
        Assert.Equal("", ClientUpdates.BlockedReason(item));
    }

    // ---- The Install tab's list (cells only) ----

    [Fact]
    public void The_update_rows_show_versions_and_the_keep_or_unpin_button()
    {
        var item = new UpdateItem { Package = "Team-Chat", Name = "Chat", Installed = "1.0.0", Latest = "1.1.0" };
        var cells = InstallModsPane.UpdateRowCells(item);
        Assert.Contains(cells, c => c.Key == InstallModsPane.CellUpdateOne);
        Assert.Equal("Keep 1.0.0", cells.Single(c => c.Key == InstallModsPane.CellUpdateKeep).Text);
        Assert.Equal("Chat 1.0.0 → 1.1.0", cells.Single(c => c.Key == InstallModsPane.CellUpdateText).Text);

        item.PinnedAt = "1.0.0";
        var pinned = InstallModsPane.UpdateRowCells(item);
        Assert.DoesNotContain(pinned, c => c.Key == InstallModsPane.CellUpdateOne);
        Assert.Equal("Unpin", pinned.Single(c => c.Key == InstallModsPane.CellUpdateKeep).Text);
        Assert.Equal("Chat kept at 1.0.0 · 1.1.0 available", pinned.Single(c => c.Key == InstallModsPane.CellUpdateText).Text);

        Assert.Equal("Update all", InstallModsPane.UpdateAllCaption("idle", 2));
        Assert.Equal("Update", InstallModsPane.UpdateAllCaption("done", 1));
        Assert.Equal("Updating…", InstallModsPane.UpdateAllCaption("applying", 2));
    }

    [Fact]
    public void Preferences_has_an_updates_group_with_the_mode_and_the_prompt()
    {
        Assert.Contains(ConfigurationPane.UpdatesGroup, ConfigurationPane.ConfigGroups);
        Assert.Equal(UpdateMode.Automatic, UpdateSettings.Parse(UpdateSettings.DefaultMode));
        Assert.True(UpdateSettings.DefaultPrompt);
        Assert.Equal(UpdateMode.Notify, UpdateSettings.Parse("notify"));
        Assert.Equal(UpdateMode.Off, UpdateSettings.Parse(" Off "));
        Assert.Equal(UpdateMode.Automatic, UpdateSettings.Parse("bogus"));
        Assert.Equal(new[] { "Automatic", "Notify", "Off" }, UpdateSettings.ModeWords);
    }

    private static void SpinUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("condition not met");
            System.Threading.Thread.Sleep(5);
        }
    }

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
}
