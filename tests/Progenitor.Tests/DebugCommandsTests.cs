using System;
using System.Collections.Generic;
using Progenitor.Commands;
using Progenitor.Debugging;
using Progenitor.Persistence;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Core's <c>.debug</c> command (moved from the Sample's <c>.nodrain</c> / <c>.maptp</c>,
/// 2026-10-06): the parse, the admin gate through the real dispatcher, the wiring that keeps it
/// Core-owned and server-only, and the one rule the grant ledger exists for — an
/// <c>Immortal</c> the debug buff did not grant is never touched.
/// </summary>
[Collection("CommandRegistry")]
public sealed class DebugCommandsTests : IDisposable
{
    public DebugCommandsTests()
    {
        CommandRegistry.ResetForTests();
        DebugCommands.LastRequest = null;
    }

    public void Dispose()
    {
        CommandRegistry.ResetForTests();
        DebugCommands.LastRequest = null;
    }

    // ---- parse --------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "", "Status", "")]
    [InlineData("status", "", "Status", "")]
    [InlineData("on", "", "On", "")]
    [InlineData("off", "", "Off", "")]
    [InlineData("ON", "  ", "On", "")]
    [InlineData("on", "Alaric", "On", "Alaric")]
    [InlineData("off", " Neris ", "Off", "Neris")]
    [InlineData("status", "Vasska", "Status", "Vasska")]
    public void On_off_and_status_parse_for_yourself_or_a_named_player(string what, string rest, string actionName, string player)
    {
        Assert.True(DebugCommandParse.TryParse(what, rest, out var request, out var error));
        Assert.Equal("", error);
        Assert.Equal(Enum.Parse<DebugAction>(actionName), request.Action);
        Assert.Equal(player, request.Player);
        Assert.Equal(player.Length > 0, request.TargetsOther);
    }

    [Theory]
    [InlineData("\"Alaric\"", "Alaric")]
    [InlineData("'Neris'", "Neris")]
    [InlineData("  \" Vasska \"  ", "Vasska")]
    [InlineData("\"Alaric", "\"Alaric")]      // unmatched: left as typed, so it simply does not match
    [InlineData("\"Alaric'", "\"Alaric'")]
    [InlineData("\"\"", "")]
    public void A_quoted_player_name_loses_one_pair_of_surrounding_quotes(string rest, string expected)
    {
        Assert.True(DebugCommandParse.TryParse("on", rest, out var request, out _));
        Assert.Equal(expected, request.Player);
        Assert.Equal(expected, DebugCommandParse.PlayerName(rest));
    }

    [Fact]
    public void Tp_takes_two_invariant_numbers_and_targets_the_caller()
    {
        Assert.True(DebugCommandParse.TryParse("tp", "-1200.5 850", out var request, out _));
        Assert.Equal(DebugAction.Teleport, request.Action);
        Assert.Equal(-1200.5f, request.X);
        Assert.Equal(850f, request.Z);
        Assert.False(request.TargetsOther);
    }

    [Theory]
    [InlineData("tp", "")]
    [InlineData("tp", "10")]
    [InlineData("tp", "10 20 30")]
    [InlineData("tp", "east north")]
    [InlineData("tp", "NaN 4")]
    [InlineData("tp", "1,5 4")]
    public void Tp_without_exactly_two_finite_numbers_is_refused(string what, string rest)
    {
        Assert.False(DebugCommandParse.TryParse(what, rest, out _, out var error));
        Assert.Equal(DebugCommandParse.TeleportUsageReply, error);
    }

    [Theory]
    [InlineData("bogus", "")]
    [InlineData("nodrain", "on")]
    [InlineData("", "stray")]
    public void Anything_else_replies_with_the_usage(string what, string rest)
    {
        Assert.False(DebugCommandParse.TryParse(what, rest, out _, out var error));
        Assert.Equal(DebugCommandParse.UsageReply, error);
        Assert.Contains(".debug [on|off|status] [player]", error, StringComparison.Ordinal);
        Assert.Contains(".debug tp <x> <z>", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Requests_map_to_the_buff_kinds_and_target_lines()
    {
        Assert.Equal(DebugBuff.Kind.On, DebugCommandParse.KindOf(DebugAction.On));
        Assert.Equal(DebugBuff.Kind.Off, DebugCommandParse.KindOf(DebugAction.Off));
        Assert.Equal(DebugBuff.Kind.Status, DebugCommandParse.KindOf(DebugAction.Status));
        Assert.Equal("Debug on for Alaric:", DebugCommandParse.FormatTargetHeader("Alaric", DebugAction.On));
        Assert.Equal("An admin turned the debug buff off for you.", DebugCommandParse.FormatTargetNotice(DebugAction.Off));
        Assert.Equal("", DebugCommandParse.FormatTargetNotice(DebugAction.Status));
        Assert.Contains("'Neris'", DebugCommandParse.FormatNoSuchPlayer("Neris"), StringComparison.Ordinal);
        Assert.Contains("matches 2 online players", DebugCommandParse.FormatAmbiguousPlayer("Ash", 2), StringComparison.Ordinal);
    }

    // ---- the admin gate, through the real dispatcher -------------------------------------------

    [Fact]
    public void A_non_admin_is_refused_before_anything_parses_or_runs()
    {
        using var handle = CommandRegistry.RegisterType(typeof(DebugCommands), new object(), CommandRoutingSettings.BuiltInOwnerId);
        Assert.True(CommandRegistry.TryGetRoot(DebugCommands.Name, out var bundle));
        Assert.All(bundle.Overloads, o => Assert.False(o.AdminOnly));
        Assert.All(bundle.Overloads, o => Assert.True(o.AdminListGate));
        Assert.False(bundle.VisibleTo(isAdmin: false));
        Assert.False(bundle.VisibleTo(isAdmin: false, listedAdmin: false));

        foreach (var text in new[] { ".debug", ".debug on", ".debug off Alaric", ".debug tp 1 2", ".debug status Neris" })
        {
            var player = new Ctx { IsAdmin = false };
            Assert.Equal(CommandResult.Denied, CommandDispatcher.TryDispatch(player, text));
            Assert.Contains("This command is admin-only.", player.Replies);
            Assert.Null(DebugCommands.LastRequest);
        }
    }

    [Theory]
    [InlineData(".debug", "Status", "")]
    [InlineData(".debug on", "On", "")]
    [InlineData(".debug off", "Off", "")]
    [InlineData(".debug on Alaric", "On", "Alaric")]
    [InlineData(".debug status Neris", "Status", "Neris")]
    [InlineData(".debug tp 12.5 -40", "Teleport", "")]
    public void An_admin_reaches_the_handler_with_the_parsed_request(string text, string actionName, string player)
    {
        using var handle = CommandRegistry.RegisterType(typeof(DebugCommands), new object(), CommandRoutingSettings.BuiltInOwnerId);
        var admin = new Ctx { IsAdmin = true };
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(admin, text));
        Assert.NotNull(DebugCommands.LastRequest);
        Assert.Equal(Enum.Parse<DebugAction>(actionName), DebugCommands.LastRequest!.Value.Action);
        Assert.Equal(player, DebugCommands.LastRequest!.Value.Player);
    }

    [Fact]
    public void An_admin_with_a_bad_argument_gets_the_usage_and_nothing_runs()
    {
        using var handle = CommandRegistry.RegisterType(typeof(DebugCommands), new object(), CommandRoutingSettings.BuiltInOwnerId);
        var admin = new Ctx { IsAdmin = true };
        CommandDispatcher.TryDispatch(admin, ".debug sideways");
        Assert.Contains(DebugCommandParse.UsageReply, admin.Replies);
        Assert.Null(DebugCommands.LastRequest);
    }

    // ---- wiring ---------------------------------------------------------------------------------

    /// <summary>
    /// Core registers the command on the server only, under Core's own owner and the built-in
    /// owner id (no new hard-coded framework id), and the old Sample commands are gone so nothing
    /// duplicates it.
    /// </summary>
    [Fact]
    public void The_command_is_Core_owned_server_only_and_the_Sample_no_longer_has_one()
    {
        string tools = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugTools.cs");
        string install = SourcePin.MethodBody(tools, "internal static void Install()");
        SourcePin.AssertStatement(install, "if (Plugin.IsServerProcess)");
        SourcePin.AssertUncommented(install,
            "CommandRegistry.RegisterType(typeof(DebugCommands), Owner, CommandRoutingSettings.BuiltInOwnerId)");

        string commands = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugCommands.cs");
        SourcePin.AssertUncommented(commands, "[Command(Name, adminOnly: false, usage: DebugCommandParse.Usage, description: Description)]");
        SourcePin.AssertUncommented(commands, "[Command(Member, adminOnly: false, usage: DebugCommandParse.BuildUsage, description: Description)]");

        string sample = SourcePin.ReadFile("samples", "Progenitor.Sample", "SampleCommands.cs");
        Assert.DoesNotContain("[Command(\"nodrain\"", sample, StringComparison.Ordinal);
        Assert.DoesNotContain("[Command(\"maptp\"", sample, StringComparison.Ordinal);
    }

    /// <summary>
    /// The map teleport key goes through the keybind registry (a map-scope bind, so it never
    /// touches gameplay) and the module reads no input itself.
    /// </summary>
    [Fact]
    public void The_map_teleport_key_is_a_registered_map_scope_keybind()
    {
        string map = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugMap.cs");
        SourcePin.AssertUncommented(map, "Keybinds.RegisterScoped(DebugTools.Owner, SettingsPanel.PluginId, KeybindId, file,");
        SourcePin.AssertUncommented(map, "allowWhileCaptured: false, allowWhenChatUnknown: true, KeybindScope.Map)");
        foreach (var direct in new[] { "UnityEngine.Input.", "Input.GetKey", "Keyboard.current", "Mouse.current" })
            Assert.DoesNotContain(direct, map, StringComparison.Ordinal);
    }

    // ---- the ledger: foreign Immortal is left alone, ours is re-armed ------------------------

    [Fact]
    public void An_Immortal_with_no_grant_is_foreign_and_never_removed()
    {
        var store = new ModStore("progenitor.debug.test", filePath: "");
        // Nothing granted: vanilla's, an admin's or another mod's Immortal.
        var plan = DebugBuff.DecideForCharacter(store, "76561198000000001", "Alaric", wearsBuff: false, out bool pre, out bool drop);
        Assert.Equal(DebugBuff.ImmortalPlan.Foreign, plan);
        Assert.False(pre);
        Assert.False(drop);
        Assert.False(DebugBuff.ImmortalClearRemoves(granted: false, preExisting: false));
        Assert.False(DebugBuff.ImmortalClearRemoves(granted: false, preExisting: true));
    }

    [Fact]
    public void A_grant_made_over_a_pre_existing_Immortal_only_ever_drops_the_guard()
    {
        var store = new ModStore("progenitor.debug.test", filePath: "");
        Assert.True(DebugBuff.RecordGrant(store, "76561198000000002", "Neris", preExisting: true));
        var plan = DebugBuff.DecideForCharacter(store, "76561198000000002", "Neris", wearsBuff: false, out bool pre, out _);
        Assert.Equal(DebugBuff.ImmortalPlan.DropGuardOnly, plan);
        Assert.True(pre);
        Assert.False(DebugBuff.ImmortalClearRemoves(granted: true, preExisting: true));
    }

    [Fact]
    public void Our_grant_is_kept_while_the_buff_is_on_and_removed_when_it_is_gone()
    {
        var store = new ModStore("progenitor.debug.test", filePath: "");
        Assert.True(DebugBuff.RecordGrant(store, "76561198000000003", "Vasska", preExisting: false));
        Assert.Equal(DebugBuff.ImmortalPlan.Keep,
            DebugBuff.DecideForCharacter(store, "76561198000000003", "Vasska", wearsBuff: true, out _, out _));
        Assert.Equal(DebugBuff.ImmortalPlan.Remove,
            DebugBuff.DecideForCharacter(store, "76561198000000003", "Vasska", wearsBuff: false, out _, out _));
        Assert.True(DebugBuff.ImmortalClearRemoves(granted: true, preExisting: false));

        // A re-arm (the world-ready adopt after a Core swap) never re-reads its own write as foreign.
        Assert.False(DebugBuff.RecordGrant(store, "76561198000000003", "Vasska", preExisting: true));
        Assert.True(DebugBuff.TryGetGrant(store, "76561198000000003", "Vasska", out bool preExisting));
        Assert.False(preExisting);
    }

    /// <summary>
    /// The ledger survives a Core swap (Core uninstalls with <see cref="Progenitor.Plugins.PluginUnloadReason.Swap"/>)
    /// and is keyed by account plus character name, never an entity id, in a neutral store.
    /// </summary>
    [Fact]
    public void The_ledger_is_kept_across_a_Core_swap_and_keyed_by_a_stable_identity()
    {
        Assert.True(DebugBuff.ShouldKeepGrant(Progenitor.Plugins.PluginUnloadReason.Swap));
        Assert.False(DebugBuff.ShouldKeepGrant(Progenitor.Plugins.PluginUnloadReason.Removal));
        Assert.False(DebugBuff.ShouldKeepGrant(null)); // .debug off and the watchdog forget
        Assert.False(DebugBuff.ShouldSweep(Progenitor.Plugins.PluginUnloadReason.Swap));
        Assert.False(DebugBuff.CanRecordGrant("76561198000000001", ""));
        Assert.Equal("debug.immortal", DebugBuff.GrantStoreKey);

        string tools = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugTools.cs");
        SourcePin.AssertUncommented(tools, "internal const string StoreId = \"progenitor.debug\";");
        string buff = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugBuff.cs");
        SourcePin.AssertUncommented(buff, "new(Progenitor.GodMode.GodModeGuard.CorePlugin, GuardOwnerLabel)");
    }

    /// <summary>
    /// A Core generation installed into a running world gets no <c>WorldReady</c>, so its ledger
    /// store can still be pending (reads empty). The reconcile must defer rather than latch on
    /// "0 own grants", and <c>.debug on</c> must refuse rather than read-modify-write an empty
    /// ledger over the real one. Both ask <c>DebugTools.StoreReady</c>, which resolves the save
    /// folder first.
    /// </summary>
    [Fact]
    public void A_pending_ledger_defers_the_reconcile_and_refuses_on()
    {
        string buff = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugBuff.cs");
        string reconcile = SourcePin.MethodBody(buff, "private static void ReconcileImmortalOnWorldReady()");
        int ready = reconcile.IndexOf("if (!DebugTools.StoreReady)", StringComparison.Ordinal);
        int ids = reconcile.IndexOf("var ids = GrantIds(store);", StringComparison.Ordinal);
        Assert.True(ready > 0 && ids > ready, "the readiness check must come before the ledger read");
        SourcePin.AssertUncommented(reconcile, "report.Add(ReconcileLedgerPendingLog);");

        string on = SourcePin.MethodBody(buff, "private static void On(Target t)");
        int onReady = on.IndexOf("if (!DebugTools.StoreReady)", StringComparison.Ordinal);
        int record = on.IndexOf("TryCharacterId(em, t.Character", StringComparison.Ordinal);
        Assert.True(onReady > 0 && record > onReady, ".debug on must check the ledger before anything is applied");
        SourcePin.AssertUncommented(on, "t.Reply(LedgerNotReadyReply);");

        string tools = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugTools.cs");
        SourcePin.AssertUncommented(tools, "ModSave.TryResolveDirectoryNow();");
        SourcePin.AssertUncommented(tools, "return Store is ModStore store && store.IsReady;");
    }

    /// <summary>
    /// The prefab pass runs from the registry's after-apply hook on both sides, which is now a
    /// multicast Core seam: the inert-clone stripper and the debug buff each add and remove their
    /// own delegate, and one subscriber's throw cannot skip the other.
    /// </summary>
    [Fact]
    public void The_after_apply_seam_is_shared_and_isolated()
    {
        string inert = SourcePin.ReadFile("src", "Progenitor.Core", "Ai", "InertClones.cs");
        SourcePin.AssertStatement(inert, "PrefabRegistry.AfterApply += _hook;");
        SourcePin.AssertStatement(inert, "if (_hook != null) PrefabRegistry.AfterApply -= _hook;");
        Assert.DoesNotContain("PrefabRegistry.AfterApply = _hook;", inert, StringComparison.Ordinal);

        string buff = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugBuff.cs");
        SourcePin.AssertStatement(buff, "PrefabRegistry.AfterApply += afterApply;");
        SourcePin.AssertUncommented(buff, "new ActionDisposable(() => PrefabRegistry.AfterApply -= afterApply)");

        string registry = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabRegistry.cs");
        SourcePin.AssertUncommented(registry, "foreach (var hook in afterApply.GetInvocationList())");
    }

    /// <summary>
    /// The map teleport's four settings are cfg-only (settings-organization.md inventory): homed
    /// in the settings-places table, so neither Progenitor's page nor the Preferences tab draws them.
    /// </summary>
    [Theory]
    [InlineData("HoldSeconds")]
    [InlineData("SearchRadius")]
    [InlineData("SearchStep")]
    [InlineData("WalkableCheck")]
    public void The_map_settings_are_cfg_only(string key)
    {
        var place = Progenitor.Ui.SettingPlaces.Find(Progenitor.Ui.SettingPlaces.Id("DebugMap", key));
        Assert.NotNull(place);
        Assert.Equal(Progenitor.Ui.SettingPlaces.CfgOnly, place!.Home);
    }

    // ---- migration of the Sample's ledger ------------------------------------------------------

    [Fact]
    public void The_Samples_ledger_is_migrated_with_its_pre_existing_flags()
    {
        var legacy = new ModStore("sample.test", filePath: "");
        var store = new ModStore("progenitor.debug.test", filePath: "");
        DebugBuff.RecordGrant(store, "76561198000000003", "Vasska", preExisting: false); // already ours
        legacy.Set(DebugBuff.LegacyGrantStoreKey, new DebugBuff.ImmortalGrantBook
        {
            Grants =
            {
                new DebugBuff.ImmortalGrant { CharacterId = "76561198000000001", Identity = "Alaric", PreExisting = false },
                new DebugBuff.ImmortalGrant { CharacterId = "76561198000000002", Identity = "Neris", PreExisting = true },
                new DebugBuff.ImmortalGrant { CharacterId = "76561198000000003", Identity = "Other", PreExisting = true },
                new DebugBuff.ImmortalGrant { CharacterId = "76561198000000004", Identity = "", PreExisting = false },
            },
        });

        Assert.Equal(2, DebugBuff.MigrateLegacyGrants(legacy, store));

        Assert.True(DebugBuff.TryGetGrant(store, "76561198000000001", "Alaric", out bool alaricPre));
        Assert.False(alaricPre);
        Assert.True(DebugBuff.TryGetGrant(store, "76561198000000002", "Neris", out bool nerisPre));
        Assert.True(nerisPre); // a pre-existing Immortal stays foreign: only the guard is ever dropped
        Assert.Equal(DebugBuff.ImmortalPlan.DropGuardOnly,
            DebugBuff.DecideForCharacter(store, "76561198000000002", "Neris", wearsBuff: false, out _, out _));
        // This ledger's own row wins over the Sample's for the same account.
        Assert.True(DebugBuff.TryGetGrant(store, "76561198000000003", "Vasska", out bool vasskaPre));
        Assert.False(vasskaPre);
        // An identity-less row is not copied.
        Assert.False(DebugBuff.TryGetGrant(store, "76561198000000004", "", out _));
        // The legacy key is gone, so a second pass copies nothing.
        Assert.DoesNotContain(DebugBuff.LegacyGrantStoreKey, legacy.Keys);
        Assert.Equal(0, DebugBuff.MigrateLegacyGrants(legacy, store));
        Assert.Equal(0, DebugBuff.MigrateLegacyGrants(null, store));
    }

    [Fact]
    public void The_migration_runs_once_per_world_from_the_reconcile_and_never_reads_its_own_store()
    {
        string buff = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugBuff.cs");
        string reconcile = SourcePin.MethodBody(buff, "private static void ReconcileImmortalOnWorldReady()");
        int ready = reconcile.IndexOf("if (!DebugTools.StoreReady)", StringComparison.Ordinal);
        int migrate = reconcile.IndexOf("ModSave.FindStoresHolding(LegacyGrantStoreKey, DebugTools.StoreId)", StringComparison.Ordinal);
        int ids = reconcile.IndexOf("var ids = GrantIds(store);", StringComparison.Ordinal);
        Assert.True(ready > 0 && migrate > ready && ids > migrate, "migrate after the ledger is ready and before it is read");
        Assert.Equal("nodrain.immortal", DebugBuff.LegacyGrantStoreKey);
    }

    // ---- live-refresh fingerprint ---------------------------------------------------------------

    [Fact]
    public void The_prefab_pass_version_is_part_of_the_refresh_fingerprint()
    {
        string buff = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugBuff.cs");
        SourcePin.AssertStatement(buff, ".PassVersion(PrefabPassVersion)");
        Assert.False(string.IsNullOrEmpty(DebugBuff.PrefabPassVersion));
        string registry = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabRegistry.cs");
        SourcePin.AssertUncommented(registry, "string.IsNullOrEmpty(def.PassVersion) ? editStamp : editStamp + \"|pass=\" + def.PassVersion");
        SourcePin.AssertUncommented(registry, "def.PassVersion = _passVersion;");
    }

    // ---- the map teleport key -----------------------------------------------------------------

    /// <summary>
    /// Decided 2026-10-06: authenticated admins use the game's own Ctrl+Shift+click map
    /// teleport, so Progenitor's key is registered unbound by default and clashes with nothing.
    /// </summary>
    [Fact]
    public void The_map_teleport_key_is_unbound_by_default()
    {
        string map = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugMap.cs");
        SourcePin.AssertUncommented(map, "internal static readonly KeyChord DefaultTeleportKey = KeyChord.None;");
        Assert.DoesNotContain("UiKey.MouseMiddle", map, StringComparison.Ordinal);
        var lines = DebugMapPolicy.StatusLines(new DebugMapPolicy.DebugMapState(true, true), pushed: true, radius: 100f, walkCheck: false);
        Assert.Contains("Ctrl+Shift+click", lines[1], StringComparison.Ordinal);
    }

    private sealed class Ctx : ICommandContext
    {
        public bool IsAdmin { get; set; }

        public List<string> Replies { get; } = new();

        public void Reply(string text) => Replies.Add(text ?? "");
    }
}
