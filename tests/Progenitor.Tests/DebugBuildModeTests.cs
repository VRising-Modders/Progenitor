using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Commands;
using Progenitor.Debugging;
using Progenitor.Install;
using Progenitor.Persistence;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// <c>.debug build on|off</c>: the parse, the server's admin rule through the real dispatcher, the
/// switched settings, and the controller end to end (record, restore, adopt, not-ready and
/// unreadable paths) against an in-memory store and a fake settings singleton.
/// </summary>
[Collection("CommandRegistry")]
public sealed class DebugBuildModeTests : IDisposable
{
    private const ulong Member = 76561198000000042;
    private const ulong NotListed = 76561198000000077;
    private const string ThisProcess = "4242-638640000000000000";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-debug-build-" + Guid.NewGuid().ToString("N"));

    public DebugBuildModeTests()
    {
        CommandRegistry.ResetForTests();
        DebugCommands.LastRequest = null;
        ServerAdminList.Shared = null;
    }

    public void Dispose()
    {
        CommandRegistry.ResetForTests();
        DebugCommands.LastRequest = null;
        ServerAdminList.Shared = null;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ---- parse ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "BuildStatus")]
    [InlineData("status", "BuildStatus")]
    [InlineData("on", "BuildOn")]
    [InlineData(" OFF ", "BuildOff")]
    public void Build_forms_parse(string tail, string actionName)
    {
        Assert.True(DebugCommandParse.TryParse("build", tail, out var request, out var error));
        Assert.Equal("", error);
        Assert.Equal(Enum.Parse<DebugAction>(actionName), request.Action);
        Assert.True(request.IsBuild);
        Assert.False(request.TargetsOther);
    }

    [Theory]
    [InlineData("toggle")]
    [InlineData("on Alaric")]
    [InlineData("1")]
    public void A_build_argument_other_than_on_off_status_is_refused(string tail)
    {
        Assert.False(DebugCommandParse.TryParse("build", tail, out _, out var error));
        Assert.Equal(DebugCommandParse.BuildUsageReply, error);
    }

    [Fact]
    public void The_usage_names_the_build_form_and_the_buff_forms_are_not_build()
    {
        Assert.Contains(".debug build [on|off|status]", DebugCommandParse.Usage, StringComparison.Ordinal);
        Assert.True(DebugCommandParse.TryParse("on", "", out var on, out _));
        Assert.False(on.IsBuild);
        Assert.True(DebugCommandParse.TryParse("", "", out var status, out _));
        Assert.False(status.IsBuild);
    }

    // ---- the admin rule, through the real dispatcher --------------------------------------------

    [Fact]
    public void A_player_who_is_not_an_admin_is_refused_before_build_parses()
    {
        using var root = RegisterRoot();
        using var build = RegisterBuild();
        AdminListHolds(Member);
        foreach (var text in new[] { ".debug build", ".debug build on", ".debug build off", ".debug build sideways" })
        {
            var player = new Ctx { IsAdmin = false, PlatformId = NotListed };
            CommandDispatcher.TryDispatch(player, text);
            Assert.Equal(new[] { DebugCommands.AdminOnlyReply }, player.Replies);
            Assert.Null(DebugCommands.LastRequest);
        }
        // The rest of .debug is behind the same rule.
        var other = new Ctx { IsAdmin = false, PlatformId = NotListed };
        Assert.Equal(CommandResult.Denied, CommandDispatcher.TryDispatch(other, ".debug on"));
        Assert.Equal(new[] { DebugCommands.AdminOnlyReply }, other.Replies);
    }

    /// <summary>
    /// On the admin list, never ran <c>adminauth</c> since the restart (the game's admin flag is
    /// off). The dispatcher's <c>adminOnly</c> would refuse; the server's admin rule lets them in,
    /// for build mode and every other <c>.debug</c> form (decided 2026-10-09).
    /// </summary>
    [Fact]
    public void A_listed_admin_without_adminauth_reaches_the_build_handler_and_the_root()
    {
        using var root = RegisterRoot();
        using var build = RegisterBuild();
        AdminListHolds(Member);
        var listed = new Ctx { IsAdmin = false, PlatformId = Member };

        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(listed, ".debug build on"));
        Assert.Equal(DebugAction.BuildOn, DebugCommands.LastRequest!.Value.Action);
        Assert.DoesNotContain(DebugCommands.AdminOnlyReply, listed.Replies);

        DebugCommands.LastRequest = null;
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(listed, ".debug on"));
        Assert.Equal(DebugAction.On, DebugCommands.LastRequest!.Value.Action);
        Assert.DoesNotContain(DebugCommands.AdminOnlyReply, listed.Replies);
    }

    [Theory]
    [InlineData(".debug build", "BuildStatus")]
    [InlineData(".debug build on", "BuildOn")]
    [InlineData(".debug build OFF", "BuildOff")]
    [InlineData(".debug build status", "BuildStatus")]
    public void An_adminauth_admin_reaches_the_build_handler_through_the_literal_subcommand(string text, string actionName)
    {
        using var root = RegisterRoot();
        using var build = RegisterBuild();
        var admin = new Ctx { IsAdmin = true };
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(admin, text));
        Assert.Equal(Enum.Parse<DebugAction>(actionName), DebugCommands.LastRequest!.Value.Action);

        // The other .debug forms still resolve to the root command.
        DebugCommands.LastRequest = null;
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(admin, ".debug on"));
        Assert.Equal(DebugAction.On, DebugCommands.LastRequest!.Value.Action);
    }

    /// <summary>
    /// A listed admin without <c>adminauth</c> can name another player and use the map teleport's
    /// command path; a player off the list is refused every form, including the bad ones, before
    /// any parse, with the dispatcher's own words.
    /// </summary>
    [Theory]
    [InlineData(".debug", "Status")]
    [InlineData(".debug off", "Off")]
    [InlineData(".debug status Neris", "Status")]
    [InlineData(".debug on Alaric", "On")]
    [InlineData(".debug tp 12.5 -40", "Teleport")]
    [InlineData(".debug build detached", "BuildDetached")]
    public void Every_debug_form_follows_the_admin_list_rule(string text, string actionName)
    {
        using var root = RegisterRoot();
        using var build = RegisterBuild();
        AdminListHolds(Member);

        var listed = new Ctx { IsAdmin = false, PlatformId = Member };
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(listed, text));
        Assert.Equal(Enum.Parse<DebugAction>(actionName), DebugCommands.LastRequest!.Value.Action);

        DebugCommands.LastRequest = null;
        var player = new Ctx { IsAdmin = false, PlatformId = NotListed };
        Assert.Equal(CommandResult.Denied, CommandDispatcher.TryDispatch(player, text));
        Assert.Equal(new[] { DebugCommands.AdminOnlyReply }, player.Replies);
        Assert.Null(DebugCommands.LastRequest);

        var stray = new Ctx { IsAdmin = false, PlatformId = NotListed };
        Assert.Equal(CommandResult.Denied, CommandDispatcher.TryDispatch(stray, ".debug sideways"));
        Assert.Equal(new[] { DebugCommands.AdminOnlyReply }, stray.Replies);
    }

    /// <summary>
    /// <c>.help</c>, group listings and the chat hints use the same test as the dispatcher, so a
    /// player off the list never sees <c>.debug</c> and a listed admin without <c>adminauth</c> does.
    /// </summary>
    [Fact]
    public void The_chat_hints_and_help_hide_debug_from_players_and_show_it_to_listed_admins()
    {
        using var root = RegisterRoot();
        using var build = RegisterBuild();
        Assert.True(CommandRegistry.TryGetRoot(DebugCommands.Name, out var bundle));
        Assert.False(bundle.VisibleTo(isAdmin: false, listedAdmin: false));
        Assert.True(bundle.VisibleTo(isAdmin: false, listedAdmin: true));
        Assert.True(bundle.VisibleTo(isAdmin: true, listedAdmin: false));

        var player = CommandHintCatalog.Build(isAdmin: false, listedAdmin: false);
        Assert.DoesNotContain(player, e => e.Command.StartsWith(".debug", StringComparison.Ordinal));
        var listed = CommandHintCatalog.Build(isAdmin: false, listedAdmin: true);
        Assert.Contains(listed, e => e.Command == ".debug");
        Assert.Contains(listed, e => e.Command == ".debug build");

        // .help and the server's hint request compute the rule the same way.
        string help = SourcePin.ReadFile("src", "Progenitor.Core", "Commands", "BuiltInCommands.cs");
        SourcePin.AssertUncommented(help, "bundle.VisibleTo(ctx.IsAdmin, CommandAccess.ListedAdminFor(ctx, bundle));");
        SourcePin.AssertUncommented(help, "if (!overload.UsableBy(ctx.IsAdmin, listedAdmin)) continue;");
        Assert.DoesNotContain("VisibleTo(ctx.IsAdmin)", help, StringComparison.Ordinal);
        string hints = SourcePin.ReadFile("src", "Progenitor.Core", "Chat", "ChatCommandHintsServer.cs");
        SourcePin.AssertUncommented(hints, "bool listedAdmin = ServerAdminList.AuthorizeShared(current.PlatformId, current.IsAdmin) != RemoteAuthorizedBy.None;");
        SourcePin.AssertUncommented(hints, "CommandHintCatalog.Build(admin, listedAdmin);");
    }

    /// <summary>The admin-list gate never widens a plain <c>adminOnly</c> command to a listed admin without <c>adminauth</c>.</summary>
    [Fact]
    public void The_admin_list_gate_does_not_widen_adminOnly_commands()
    {
        using var handle = CommandRegistry.RegisterType(typeof(GateProbeCommands), new object(), CommandRoutingSettings.BuiltInOwnerId);
        Assert.True(CommandRegistry.TryGetRoot("hx_gate_adminonly", out var adminOnly));
        Assert.False(adminOnly.VisibleTo(isAdmin: false, listedAdmin: true));
        AdminListHolds(Member);
        var listed = new Ctx { IsAdmin = false, PlatformId = Member };
        Assert.Equal(CommandResult.Denied, CommandDispatcher.TryDispatch(listed, ".hx_gate_adminonly"));
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(listed, ".hx_gate_listed"));
    }

    private static class GateProbeCommands
    {
        [Command("hx_gate_adminonly", adminOnly: true)]
        public static void AdminOnly(ICommandContext ctx) { }

        [AdminListGate]
        [Command("hx_gate_listed")]
        public static void Listed(ICommandContext ctx) { }
    }

    [Fact]
    public void An_admin_with_a_bad_build_argument_gets_the_build_usage()
    {
        using var root = RegisterRoot();
        using var build = RegisterBuild();
        var admin = new Ctx { IsAdmin = true };
        CommandDispatcher.TryDispatch(admin, ".debug build sideways");
        Assert.Contains(DebugCommandParse.BuildUsageReply, admin.Replies);
        Assert.Null(DebugCommands.LastRequest);
    }

    [Fact]
    public void The_build_command_is_registered_with_the_root_and_the_root_path_rechecks_the_rule()
    {
        string tools = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugTools.cs");
        SourcePin.AssertUncommented(tools,
            "CommandRegistry.RegisterType(typeof(DebugBuildCommands), Owner, CommandRoutingSettings.BuiltInOwnerId)");
        string commands = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugCommands.cs");
        string execute = SourcePin.MethodBody(commands, "internal static void ExecuteBuild(ICommandContext ctx, DebugAction action, string piece = \"\", int confirm = -1)");
        int rule = execute.IndexOf("ServerAdminList.AuthorizeShared(ctx.User.PlatformId, ctx.User.IsAdmin)", StringComparison.Ordinal);
        int run = execute.IndexOf("DebugBuildRuntime.Run(", StringComparison.Ordinal);
        Assert.True(rule > 0 && run > rule, "the admin rule must be checked before build mode runs");
    }

    // ---- the settings ----------------------------------------------------------------------------

    [Fact]
    public void Build_mode_switches_the_seven_build_settings_at_their_dump_offsets()
    {
        var expected = new Dictionary<string, int>
        {
            ["BuildCostsDisabled"] = 0x5,
            ["AllProgressionUnlocked"] = 0x7,
            ["FreeBuildingPlacementEnabled"] = 0xE,
            ["FloorPlacementRestrictionsDisabled"] = 0x10,
            ["CastleLimitsDisabled"] = 0x1B,
            ["CastleDragBuildingEnabled"] = 0x1C,
            ["BuildingProximityRequirementDisabled"] = 0x27,
        };
        Assert.Equal(expected.Count, DebugBuildMode.Settings.Count);
        foreach (var s in DebugBuildMode.Settings)
        {
            Assert.Equal(expected[s.Name], s.Offset);
            Assert.Equal(s.Offset, DebugBuildMode.OffsetOf(s.Name));
        }
        Assert.Equal(0x28, DebugBuildMode.Extent);
        Assert.Equal(-1, DebugBuildMode.OffsetOf("CastleHeartBloodEssenceDisabled"));
        Assert.Equal(DebugBuildMode.Settings.Count, DebugBuildMode.Settings.Select(s => s.Offset).Distinct().Count());
    }

    /// <summary>
    /// The 2026-10-09 bug: with BuildingPlacementRestrictionsDisabled on, the game skips the
    /// territory check that also picks the heart a new piece joins, so build mode must never turn
    /// it on again; it stays known (offset 0xD) so off and the restore can still clear it.
    /// </summary>
    [Fact]
    public void Placement_restrictions_are_retired_never_written_but_still_cleared()
    {
        Assert.DoesNotContain("BuildingPlacementRestrictionsDisabled", DebugBuildMode.AllNames());
        var retired = Assert.Single(DebugBuildMode.Retired);
        Assert.Equal("BuildingPlacementRestrictionsDisabled", retired.Name);
        Assert.Equal(0xD, retired.Offset);
        Assert.Equal(0xD, DebugBuildMode.OffsetOf("BuildingPlacementRestrictionsDisabled"));
        Assert.True(DebugBuildMode.IsRetired("BuildingPlacementRestrictionsDisabled"));

        var plan = DebugBuildMode.PlanOn(_ => false, null);
        Assert.DoesNotContain("BuildingPlacementRestrictionsDisabled", plan.Write);
        Assert.DoesNotContain("BuildingPlacementRestrictionsDisabled", plan.Record);
        Assert.Empty(plan.ClearRetired);
    }

    [Fact]
    public void On_over_an_old_record_turns_the_retired_setting_back_and_drops_it()
    {
        var h = new Harness();
        foreach (var n in h.Live.Keys.ToList()) h.Live[n] = true;
        h.Store.Set(DebugBuildMode.MarkerKey, "on;p=" + ThisProcess + ";" + string.Join(",", DebugBuildMode.AllNames()) + ",BuildingPlacementRestrictionsDisabled");

        h.Run(DebugAction.BuildOn);
        Assert.False(h.Live["BuildingPlacementRestrictionsDisabled"]);
        Assert.All(DebugBuildMode.AllNames(), n => Assert.True(h.Live[n]));
        Assert.DoesNotContain("BuildingPlacementRestrictionsDisabled", Record(h.Store)!.Names);
        Assert.Contains(h.Replies, l => l.Contains("turned off BuildingPlacementRestrictionsDisabled", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Replies, l => l.StartsWith("Warning:", StringComparison.Ordinal));
    }

    [Fact]
    public void Off_and_the_restore_clear_a_recorded_retired_setting()
    {
        var h = new Harness();
        h.Live["BuildCostsDisabled"] = true;
        h.Live["BuildingPlacementRestrictionsDisabled"] = true;
        h.Store.Set(DebugBuildMode.MarkerKey, "on;p=" + ThisProcess + ";BuildCostsDisabled,BuildingPlacementRestrictionsDisabled");
        h.Run(DebugAction.BuildOff);
        Assert.All(h.Live.Values, Assert.False);

        h = new Harness();
        h.Live["BuildingPlacementRestrictionsDisabled"] = true;
        h.Store.Set(DebugBuildMode.MarkerKey, "on;p=other;BuildingPlacementRestrictionsDisabled");
        var c = h.NewGeneration();
        c.OnWorldLoad();
        Assert.True(c.Tick());
        Assert.False(h.Live["BuildingPlacementRestrictionsDisabled"]);
    }

    [Fact]
    public void A_Core_swap_adopts_but_turns_the_retired_setting_back()
    {
        var h = new Harness();
        foreach (var n in h.Live.Keys.ToList()) h.Live[n] = true;
        h.Store.Set(DebugBuildMode.MarkerKey, "on;p=" + ThisProcess + ";" + string.Join(",", DebugBuildMode.AllNames()) + ",BuildingPlacementRestrictionsDisabled");
        var c = h.NewGeneration(ThisProcess);
        c.Install(worldRunning: true);
        Assert.True(c.Tick());
        Assert.False(h.Live["BuildingPlacementRestrictionsDisabled"]);
        Assert.All(DebugBuildMode.AllNames(), n => Assert.True(h.Live[n]));
        var record = Record(h.Store)!;
        Assert.Equal(DebugBuildMode.AllNames(), record.Names);
        Assert.Equal(ThisProcess, record.ProcessStamp);
        Assert.Contains(h.Log, l => l.Contains("turned off retired BuildingPlacementRestrictionsDisabled", StringComparison.Ordinal));
    }

    [Fact]
    public void A_retired_setting_that_was_on_before_is_left_on_by_on_and_warned_about()
    {
        var h = new Harness();
        h.Live["BuildingPlacementRestrictionsDisabled"] = true; // an operator's own choice: no record names it
        h.Run(DebugAction.BuildOn);
        Assert.True(h.Live["BuildingPlacementRestrictionsDisabled"]);
        Assert.Contains(h.Replies, l => l.StartsWith("Warning: BuildingPlacementRestrictionsDisabled is on", StringComparison.Ordinal));

        h.Replies.Clear();
        h.Run(DebugAction.BuildStatus);
        Assert.Contains(h.Replies, l => l.StartsWith("Warning: BuildingPlacementRestrictionsDisabled is on", StringComparison.Ordinal));

        h.Run(DebugAction.BuildOff);                             // the record does not name it
        Assert.True(h.Live["BuildingPlacementRestrictionsDisabled"]);
    }

    [Fact]
    public void Off_without_a_record_never_clears_an_operators_retired_setting_and_warns()
    {
        var h = new Harness();
        h.Live["BuildingPlacementRestrictionsDisabled"] = true;
        h.Live["BuildCostsDisabled"] = true;
        h.Run(DebugAction.BuildOff);
        Assert.False(h.Live["BuildCostsDisabled"]);
        Assert.True(h.Live["BuildingPlacementRestrictionsDisabled"]);
        Assert.DoesNotContain("BuildingPlacementRestrictionsDisabled", DebugBuildMode.PlanOffWithoutRecord(_ => true));
        Assert.Contains(h.Replies, l => l.StartsWith("Warning: BuildingPlacementRestrictionsDisabled is on and build mode did not set it", StringComparison.Ordinal));

        h = new Harness();
        h.Live["BuildingPlacementRestrictionsDisabled"] = true;
        h.Store.Set(DebugBuildMode.MarkerKey, 5);               // unreadable record: still not ours to clear
        h.Run(DebugAction.BuildOff);
        Assert.True(h.Live["BuildingPlacementRestrictionsDisabled"]);
    }

    [Fact]
    public void The_retired_warning_says_who_set_it()
    {
        Func<string, bool> on = n => n == "BuildingPlacementRestrictionsDisabled";
        Assert.Equal("", DebugBuildMode.RetiredWarning(_ => false, null));
        Assert.Contains("an earlier build mode set it", DebugBuildMode.RetiredWarning(on, new[] { "BuildingPlacementRestrictionsDisabled" }), StringComparison.Ordinal);
        Assert.Contains(".debug build off turns it off", DebugBuildMode.RetiredWarning(on, new[] { "BuildingPlacementRestrictionsDisabled" }), StringComparison.Ordinal);
        string theirs = DebugBuildMode.RetiredWarning(on, new[] { "BuildCostsDisabled" });
        Assert.Contains("build mode did not set it", theirs, StringComparison.Ordinal);
        Assert.DoesNotContain(".debug build off turns it off", theirs, StringComparison.Ordinal);

        var h = new Harness();
        h.Live["BuildingPlacementRestrictionsDisabled"] = true;
        h.Store.Set(DebugBuildMode.MarkerKey, "on;p=" + ThisProcess + ";BuildingPlacementRestrictionsDisabled");
        h.Run(DebugAction.BuildStatus);
        Assert.Contains(h.Replies, l => l.Contains("an earlier build mode set it", StringComparison.Ordinal));
    }

    // ---- the record ------------------------------------------------------------------------------

    [Fact]
    public void The_record_round_trips_with_its_process_stamp_and_reads_the_first_format()
    {
        var r = DebugBuildMode.DecodeMarker(DebugBuildMode.EncodeMarker(new[] { "CastleLimitsDisabled", "BuildCostsDisabled" }, ThisProcess))!;
        Assert.Equal(new[] { "BuildCostsDisabled", "CastleLimitsDisabled" }, r.Names);
        Assert.Equal(ThisProcess, r.ProcessStamp);

        var empty = DebugBuildMode.DecodeMarker(DebugBuildMode.EncodeMarker(Array.Empty<string>(), ThisProcess))!;
        Assert.Empty(empty.Names);

        var legacy = DebugBuildMode.DecodeMarker("on;BuildCostsDisabled")!;
        Assert.Equal(new[] { "BuildCostsDisabled" }, legacy.Names);
        Assert.Equal("", legacy.ProcessStamp);

        Assert.Null(DebugBuildMode.DecodeMarker(null));
        Assert.Null(DebugBuildMode.DecodeMarker(""));
        Assert.Null(DebugBuildMode.DecodeMarker("BuildCostsDisabled"));
        Assert.Null(DebugBuildMode.DecodeMarker("on;p=unterminated"));
        Assert.Equal("debug.build", DebugBuildMode.MarkerKey);
    }

    [Fact]
    public void Names_this_build_does_not_know_are_kept_in_the_record_and_reported_by_off()
    {
        var r = DebugBuildMode.DecodeMarker("on;p=1;FutureSetting, BuildCostsDisabled ,FutureSetting")!;
        Assert.Equal(new[] { "BuildCostsDisabled", "FutureSetting" }, r.Names);

        var plan = DebugBuildMode.PlanOn(_ => true, r.Names);
        Assert.Contains("FutureSetting", plan.Record);

        var off = DebugBuildMode.PlanOff(_ => true, r.Names);
        Assert.Equal(new[] { "BuildCostsDisabled" }, off.Clear);
        Assert.Equal(new[] { "FutureSetting" }, off.Unknown);
    }

    // ---- set and restore -------------------------------------------------------------------------

    [Fact]
    public void On_then_off_restores_exactly_and_drops_the_record()
    {
        var h = new Harness();
        h.Run(DebugAction.BuildOn);
        Assert.All(DebugBuildMode.AllNames(), n => Assert.True(h.Live[n]));
        var record = Record(h.Store)!;
        Assert.Equal(DebugBuildMode.AllNames(), record.Names);
        Assert.Equal(ThisProcess, record.ProcessStamp);
        Assert.Contains(h.Replies, l => l.Contains("every player", StringComparison.Ordinal));

        h.Run(DebugAction.BuildOff);
        Assert.All(h.Live.Values, Assert.False);
        Assert.DoesNotContain(DebugBuildMode.MarkerKey, h.Store.Keys);
    }

    [Fact]
    public void A_setting_that_was_already_on_stays_on_after_off()
    {
        var h = new Harness();
        h.Live["BuildCostsDisabled"] = true;
        h.Live["CastleLimitsDisabled"] = true;
        var before = new Dictionary<string, bool>(h.Live);

        h.Run(DebugAction.BuildOn);
        Assert.DoesNotContain("BuildCostsDisabled", Record(h.Store)!.Names);
        h.Run(DebugAction.BuildOff);
        Assert.Equal(before, h.Live);
    }

    [Fact]
    public void A_second_on_keeps_the_first_record()
    {
        var h = new Harness();
        h.Live["AllProgressionUnlocked"] = true;
        h.Run(DebugAction.BuildOn);
        h.Live["FreeBuildingPlacementEnabled"] = false; // someone turned one of ours off
        h.Run(DebugAction.BuildOn);
        var names = Record(h.Store)!.Names;
        Assert.Contains("FreeBuildingPlacementEnabled", names);
        Assert.DoesNotContain("AllProgressionUnlocked", names);
        Assert.True(h.Live["FreeBuildingPlacementEnabled"]);
    }

    [Fact]
    public void Off_with_no_record_turns_every_build_setting_off_and_says_so()
    {
        var h = new Harness();
        h.Live["BuildCostsDisabled"] = true;   // a save made with the mode on whose record was lost
        h.Live["CastleLimitsDisabled"] = true;

        h.Run(DebugAction.BuildOff);
        Assert.All(h.Live.Values, Assert.False);
        Assert.Contains(h.Replies, l => l.StartsWith("There is no record of build mode, so every build setting", StringComparison.Ordinal));
    }

    [Fact]
    public void Off_with_an_unreadable_record_turns_everything_off_and_drops_it()
    {
        var h = new Harness();
        foreach (var n in DebugBuildMode.AllNames()) h.Live[n] = true;
        h.Store.Set(DebugBuildMode.MarkerKey, 42); // not a string record

        h.Run(DebugAction.BuildOff);
        Assert.All(h.Live.Values, Assert.False);
        Assert.DoesNotContain(DebugBuildMode.MarkerKey, h.Store.Keys);
        Assert.Contains(h.Replies, l => l.StartsWith("Build mode's record in this save cannot be read", StringComparison.Ordinal));
    }

    [Fact]
    public void On_with_an_unreadable_record_is_refused_and_writes_nothing()
    {
        var h = new Harness();
        h.Store.Set(DebugBuildMode.MarkerKey, "garbage");

        h.Run(DebugAction.BuildOn);
        Assert.Equal(new[] { DebugBuildMode.UnreadableOnReply }, h.Replies);
        Assert.All(h.Live.Values, Assert.False);
        Assert.Equal("garbage", h.Store.Get<string>(DebugBuildMode.MarkerKey));
        Assert.Equal(0, h.Writes);
    }

    [Fact]
    public void Status_warns_when_the_record_and_the_live_settings_disagree()
    {
        var h = new Harness();
        h.Live["BuildCostsDisabled"] = true;
        h.Run(DebugAction.BuildStatus);
        Assert.StartsWith("Build mode: off, but warning: build settings are on with no record", h.Replies[0], StringComparison.Ordinal);

        h = new Harness();
        h.Run(DebugAction.BuildOn);
        h.Live["CastleLimitsDisabled"] = false;
        h.Replies.Clear();
        h.Run(DebugAction.BuildStatus);
        Assert.StartsWith("Build mode: on.", h.Replies[0], StringComparison.Ordinal);
        Assert.Contains(h.Replies, l => l == "Warning: recorded as turned on but now off: CastleLimitsDisabled.");

        h = new Harness();
        h.Store.Set(DebugBuildMode.MarkerKey, 7);
        h.Live["BuildCostsDisabled"] = true;
        h.Run(DebugAction.BuildStatus);
        Assert.StartsWith("Build mode: unknown. Warning: its record in this save cannot be read; build settings are on: BuildCostsDisabled.",
            h.Replies[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_pending_store_refuses_on_and_off_and_writes_nothing()
    {
        var h = new Harness { Ready = false };
        h.Run(DebugAction.BuildOn);
        h.Run(DebugAction.BuildOff);
        Assert.Equal(new[] { DebugBuildMode.NotReadyReply, DebugBuildMode.NotReadyReply }, h.Replies);
        Assert.Equal(0, h.Writes);
        Assert.Empty(h.Store.Keys);

        h.Replies.Clear();
        h.Run(DebugAction.BuildStatus);
        Assert.StartsWith("Build mode: unknown (the world save folder is not known yet", h.Replies[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_singleton_changes_nothing()
    {
        var h = new Harness { Singleton = false };
        h.Run(DebugAction.BuildOn);
        Assert.Equal(new[] { DebugBuildMode.NoSingletonReply }, h.Replies);
        Assert.Empty(h.Store.Keys);
    }

    // ---- lifetime: restore after a load, adopt after a swap --------------------------------------

    [Fact]
    public void After_a_world_load_the_tick_waits_for_the_store_then_restores()
    {
        var h = new Harness();
        h.Run(DebugAction.BuildOn);           // the save carries the mode and its record
        var c = h.NewGeneration();            // a fresh process: Core installs before the world exists
        c.Install(worldRunning: false);
        Assert.False(c.ResolvePending);

        c.OnWorldLoad();
        h.Ready = false;
        Assert.False(c.Tick());               // store pending: try again next frame
        Assert.True(c.ResolvePending);
        Assert.All(DebugBuildMode.AllNames(), n => Assert.True(h.Live[n]));

        h.Ready = true;
        h.Singleton = false;
        Assert.False(c.Tick());               // singleton not there yet
        h.Singleton = true;
        Assert.True(c.Tick());
        Assert.False(c.ResolvePending);
        Assert.All(h.Live.Values, Assert.False);
        Assert.DoesNotContain(DebugBuildMode.MarkerKey, h.Store.Keys);
        Assert.Contains(h.Log, l => l.Contains("world load: build mode does not survive", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Core_swap_in_the_same_process_adopts_build_mode()
    {
        var h = new Harness();
        h.Run(DebugAction.BuildOn);
        var c = h.NewGeneration(ThisProcess);
        c.Install(worldRunning: true);
        Assert.True(c.ResolvePending);
        Assert.True(c.Tick());
        Assert.All(DebugBuildMode.AllNames(), n => Assert.True(h.Live[n]));
        Assert.NotNull(Record(h.Store));
        Assert.Contains(h.Log, l => l.Contains("adopted", StringComparison.Ordinal));
    }

    /// <summary>
    /// The gap the process stamp closes: the server restarted, the world loaded, and a Core swap
    /// landed before the old generation's restore ran. The new generation never saw the load, but
    /// the record was written by another process, so it restores.
    /// </summary>
    [Fact]
    public void A_swap_after_an_unrestored_restart_restores()
    {
        var h = new Harness();
        h.Run(DebugAction.BuildOn);
        var c = h.NewGeneration("9999-638640000000000001");
        c.Install(worldRunning: true);
        Assert.True(c.Tick());
        Assert.All(h.Live.Values, Assert.False);
        Assert.DoesNotContain(DebugBuildMode.MarkerKey, h.Store.Keys);
        Assert.Contains(h.Log, l => l.Contains("Core install after a server restart", StringComparison.Ordinal));
    }

    [Fact]
    public void A_record_without_a_process_stamp_is_restored_on_install_and_unknown_names_are_logged()
    {
        var h = new Harness();
        h.Live["BuildCostsDisabled"] = true;
        h.Store.Set(DebugBuildMode.MarkerKey, "on;BuildCostsDisabled,FutureSetting");
        var c = h.NewGeneration(ThisProcess);
        c.Install(worldRunning: true);
        Assert.True(c.Tick());
        Assert.False(h.Live["BuildCostsDisabled"]);
        Assert.Contains(h.Log, l => l.Contains("FutureSetting", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unreadable_record_after_a_load_is_left_alone_and_logged()
    {
        var h = new Harness();
        h.Live["BuildCostsDisabled"] = true;
        h.Store.Set(DebugBuildMode.MarkerKey, 3);
        var c = h.NewGeneration();
        c.OnWorldLoad();
        Assert.True(c.Tick());
        Assert.True(h.Live["BuildCostsDisabled"]);
        Assert.Contains(h.Log, l => l.Contains("cannot be read", StringComparison.Ordinal));
    }

    [Fact]
    public void Nothing_pending_without_a_record()
    {
        var h = new Harness();
        var c = h.NewGeneration();
        c.OnWorldLoad();
        Assert.True(c.Tick());
        Assert.Equal(0, h.Writes);
        Assert.Equal(DebugBuildMode.Recovery.None, DebugBuildMode.Decide(null, afterWorldLoad: true, ThisProcess));
    }

    [Fact]
    public void The_runtime_uninstall_writes_nothing_and_uses_the_guarded_access()
    {
        string runtime = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugBuildRuntime.cs");
        Assert.DoesNotContain(".Write(", SourcePin.MethodBody(runtime, "private static void Uninstall()"), StringComparison.Ordinal);
        SourcePin.AssertUncommented(runtime, "byte* rw = AiRuntime.RW<SyncedServerDebugSettings>(em, entity, DebugBuildMode.Extent);");
        Assert.DoesNotContain("SetDebugSettingEvent(", runtime, StringComparison.Ordinal);
    }

    // ---- helpers ---------------------------------------------------------------------------------

    private static DebugBuildMode.Record? Record(IModStore store) =>
        DebugBuildMode.DecodeMarker(store.Get<string>(DebugBuildMode.MarkerKey));

    private static IDisposable RegisterRoot() =>
        CommandRegistry.RegisterType(typeof(DebugCommands), new object(), CommandRoutingSettings.BuiltInOwnerId);

    private static IDisposable RegisterBuild() =>
        CommandRegistry.RegisterType(typeof(DebugBuildCommands), new object(), CommandRoutingSettings.BuiltInOwnerId);

    private void AdminListHolds(params ulong[] ids)
    {
        string pdp = Path.Combine(_dir, "save");
        Directory.CreateDirectory(Path.Combine(pdp, "Settings"));
        File.WriteAllLines(Path.Combine(pdp, "Settings", "adminlist.txt"), Array.ConvertAll(ids, id => id.ToString()));
        ServerAdminList.Shared = new ServerAdminList(() => pdp, Path.Combine(_dir, "server"));
    }

    private sealed class Harness : IBuildSettingsAccess
    {
        internal readonly Dictionary<string, bool> Live = DebugBuildMode.Tracked().ToDictionary(s => s.Name, _ => false);
        internal readonly ModStore Store = new("progenitor.debug.test", filePath: "");
        internal readonly List<string> Replies = new();
        internal readonly List<string> Log = new();
        internal bool Ready = true;
        internal bool Singleton = true;
        internal int Writes;
        private readonly DebugBuildController _controller;

        internal Harness() => _controller = NewGeneration(ThisProcess);

        internal DebugBuildController NewGeneration(string process = "1-1") =>
            new(this, () => Ready ? Store : null, process, Log.Add);

        internal void Run(DebugAction action) => _controller.Run(action, "Tester (adminlist)", Replies.Add);

        public bool TryRead(out Func<string, bool> current)
        {
            var snapshot = new Dictionary<string, bool>(Live);
            current = n => snapshot.TryGetValue(n, out bool v) && v;
            return Singleton;
        }

        public void Write(IReadOnlyList<string> names, bool value)
        {
            if (names.Count > 0) Writes++;
            foreach (var n in names)
                if (Live.ContainsKey(n)) Live[n] = value;
        }
    }

    private sealed class Ctx : ICommandContext
    {
        public bool IsAdmin { get; set; }

        public ulong PlatformId { get; set; }

        public List<string> Replies { get; } = new();

        public void Reply(string text) => Replies.Add(text ?? "");
    }
}
