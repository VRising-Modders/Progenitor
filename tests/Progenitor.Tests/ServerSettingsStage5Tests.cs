using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// The lock list (<c>ServerSettingsLock</c>) is process-wide and every server-wide edit path and every settings page reads it, so
/// the tests that set it run alone, after the parallel collections.
/// </summary>
[CollectionDefinition("ServerSettingsLock", DisableParallelization = true)]
public sealed class ServerSettingsLockCollection
{
}

/// <summary>
/// Settings organization stage 5, server half (docs/settings-organization.md, decisions 4, 5 and 8): the server values an
/// admin edits from Progenitor's page over the admin channel — the answer deadline, photo mode and ally-safe hits on
/// this server, remote installs and their admin list (with the operator's <c>AllowedSteamIdsInGame</c> lock, refused
/// on the server whatever a client sends) and the no-Progenitor notice's timing. Authorization, rate limit and audit are
/// the channel's. Game-free.
/// </summary>
[Collection("ServerSettingsLock")]
public sealed class ServerOptionsTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private const ulong AdminId = 76561198000000001UL;
    private static readonly RemoteInstallActor Admin = new("Admin", AdminId, isAdmin: true);
    private static readonly RemoteInstallActor Player = new("Player", 76561198000000002UL, isAdmin: false);

    private readonly string _dir;
    private readonly List<string> _audit = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly List<(string Op, string Value)> _stored = new();
    private readonly List<string> _warnings = new();
    private string _lockText = "";
    private bool _legacy;
    private double _now = 1000;

    public ServerOptionsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-serveroptions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = true;
        Plugin.Logger = new TestLogSource();
        JoinAdminServer.Reset();
        JoinAdminServer.WindowGet = () => 60;
        JoinAdminServer.WindowSet = _ => true;
        JoinAdminServer.Audit = _audit.Add;
        JoinAdminServer.AuditClock = () => T0;
        JoinAdminServer.Now = () => _now;
        RefusedJoins.Clock = () => T0;
        _values[ServerOptionOps.AnswerTimeout] = "20";
        _values[ServerOptionOps.PhotoMode] = "true";
        _values[ServerOptionOps.AllySafeHits] = "true";
        _values[ServerOptionOps.RemoteInstall] = "true";
        _values[ServerOptionOps.AllowedSteamIds] = "";
        _values[ServerOptionOps.NoticeSeconds] = "5";
        _values[ServerOptionOps.NoticeRepeatSeconds] = "3";
        _values[ServerOptionOps.NoticeCreationLimitSeconds] = "600";
        _values[ServerOptionOps.DisconnectGrace] = "60";
        ServerOptions.Get = op => _values.TryGetValue(op, out var v) ? v : null;
        ServerOptions.Set = (op, v) =>
        {
            _stored.Add((op, v));
            _values[op] = v;
            return true;
        };
        ServerSettingsLock.Reset();
        ServerSettingsLock.Source = () => _lockText;
        ServerSettingsLock.LegacyAllowedIdsLocked = () => _legacy;
        ServerSettingsLock.Warn = _warnings.Add;
    }

    public void Dispose()
    {
        JoinAdminServer.Uninstall();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestIsServer = false;
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private JoinAdminResponse Apply(JoinAdminRequest r, RemoteInstallActor? actor = null)
    {
        _now += 7;
        return JoinAdminServer.Apply(r, handshakeOk: true, actor ?? Admin);
    }

    private static JoinAdminRequest Seconds(string op, int seconds) => new() { Op = op, Seconds = seconds };

    private static JoinAdminRequest Value(string op, string value) => new() { Op = op, Value = value };

    [Fact]
    public void Every_option_is_an_admin_channel_operation_and_a_placed_setting()
    {
        foreach (string op in ServerOptionOps.All)
        {
            Assert.Contains(op, JoinAdminOps.All);
            Assert.True(JoinAdminOps.IsEdit(op));
            var place = SettingPlaces.Find(SettingPlaces.JoinAdminId(op));
            Assert.NotNull(place);
            Assert.Equal(SettingPlaces.StoreJoinAdmin, place!.Store);
            var spec = ServerOptions.Find(op)!;
            // AUTOUPDATE3: [AutoUpdate] Mods and MajorVersions are also a client's own keys (Preferences, the player's own
            // file); on the server the same keys are its own values, reached only through the admin channel.
            if (op is ServerOptionOps.UpdateMods or ServerOptionOps.UpdateMajors)
            {
                Assert.Equal(SettingPlaces.Preferences, SettingPlaces.Find(SettingPlaces.Id(spec.Section, spec.Key))!.Home);
                continue;
            }

            // The option's cfg entry itself stays in the file (never drawn as the client's own row).
            Assert.Equal(SettingPlaces.CfgOnly, SettingPlaces.Find(SettingPlaces.Id(spec.Section, spec.Key))!.Home);
        }

        Assert.Equal(SettingPlaces.PlayersWithoutProgenitor, SettingPlaces.Find(SettingPlaces.JoinAdminId(ServerOptionOps.AnswerTimeout))!.Home);
        Assert.Equal(SettingPlaces.OnThisServer, SettingPlaces.Find(SettingPlaces.JoinAdminId(ServerOptionOps.PhotoMode))!.Home);
        Assert.Equal(SettingPlaces.Operator, SettingPlaces.Find(SettingPlaces.JoinAdminId(ServerOptionOps.AllowedSteamIds))!.Home);
        // The lock itself is never drawn and never an operation.
        Assert.Equal(SettingPlaces.CfgOnly, SettingPlaces.Find(SettingPlaces.Id("RemoteInstall", RemoteInstallSettings.AllowedSteamIdsInGameKey))!.Home);
        Assert.DoesNotContain(JoinAdminOps.All, op => op.Contains("in-game", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Seconds_are_clamped_like_the_window_and_stored_with_an_audit_line()
    {
        var r = Apply(Seconds(ServerOptionOps.AnswerTimeout, 999));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("Clamped to 300 seconds (0 to 300).", r.Note);
        Assert.Equal((ServerOptionOps.AnswerTimeout, "300"), _stored.Single());
        Assert.Equal("300", r.State!.Options[ServerOptionOps.AnswerTimeout]);
        var line = _audit.Last();
        Assert.Contains("progenitor/JoinAdmin/answer-timeout", line);
        Assert.Contains("20", line);
        Assert.Contains("300", line);

        Assert.Equal("2", Apply(Seconds(ServerOptionOps.NoticeRepeatSeconds, 0)).State!.Options[ServerOptionOps.NoticeRepeatSeconds]);
        Assert.Equal("3600", Apply(Seconds(ServerOptionOps.NoticeCreationLimitSeconds, 99999)).State!.Options[ServerOptionOps.NoticeCreationLimitSeconds]);

        // A request without seconds is refused, never read as 0.
        var noSeconds = Apply(new JoinAdminRequest { Op = ServerOptionOps.NoticeSeconds, HasSeconds = false });
        Assert.False(noSeconds.Ok);
        Assert.Equal("5", _values[ServerOptionOps.NoticeSeconds]);
    }

    [Fact]
    public void The_disconnect_grace_is_clamped_audited_and_told_to_players_when_it_changes()
    {
        int told = 0;
        JoinAdminServer.GraceChanged = () => told++;
        var r = Apply(Seconds(ServerOptionOps.DisconnectGrace, 5));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("Clamped to 10 seconds (10 to 300).", r.Note);
        Assert.Equal("10", r.State!.Options[ServerOptionOps.DisconnectGrace]);
        Assert.Contains("progenitor/JoinAdmin/disconnect-grace", _audit.Last());
        Assert.Equal(1, told);

        Assert.Equal("300", Apply(Seconds(ServerOptionOps.DisconnectGrace, 999)).State!.Options[ServerOptionOps.DisconnectGrace]);
        Assert.Equal(2, told);
        // Unchanged: nothing stored, nobody told.
        Assert.Equal("Unchanged.", Apply(Seconds(ServerOptionOps.DisconnectGrace, 300)).Note);
        Assert.Equal(2, told);
        // Another option never tells the rule.
        Assert.True(Apply(Seconds(ServerOptionOps.NoticeSeconds, 7)).Ok);
        Assert.Equal(2, told);
    }

    [Fact]
    public void Switches_take_on_or_off_and_an_unchanged_value_stores_nothing()
    {
        Assert.True(Apply(Value(ServerOptionOps.PhotoMode, "false")).Ok);
        Assert.Equal("false", _values[ServerOptionOps.PhotoMode]);
        var same = Apply(Value(ServerOptionOps.PhotoMode, "off"));
        Assert.True(same.Ok);
        Assert.Equal("Unchanged.", same.Note);
        Assert.Single(_stored);
        var bad = Apply(Value(ServerOptionOps.AllySafeHits, "maybe"));
        Assert.False(bad.Ok);
        Assert.Equal("Choose on or off.", bad.Error);
        Assert.Contains("ally-safe-hits", _audit.Last());
    }

    [Fact]
    public void The_install_list_keeps_SteamID64s_in_order_once_and_refuses_anything_else()
    {
        string a = "76561198000000011", b = "76561198000000012";
        var r = Apply(Value(ServerOptionOps.AllowedSteamIds, b + "; " + a + " " + b));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(b + ", " + a, _values[ServerOptionOps.AllowedSteamIds]);
        // The audit line carries the list before and after.
        Assert.Contains(b + ", " + a, _audit.Last());

        var listed = new RemoteInstallActor("Listed", ulong.Parse(a), isAdmin: true);
        var bad = Apply(Value(ServerOptionOps.AllowedSteamIds, a + ", bob"), listed);
        Assert.False(bad.Ok);
        Assert.Equal("Not a SteamID64: bob.", bad.Error);
        Assert.Equal(b + ", " + a, _values[ServerOptionOps.AllowedSteamIds]);

        // Empty is a value (any admin), carried on the wire.
        var bytes = JoinAdminWire.EncodeRequest(Value(ServerOptionOps.AllowedSteamIds, ""));
        Assert.True(JoinAdminWire.TryDecodeRequest(bytes, out var decoded));
        Assert.True(decoded.HasValue);
        var cleared = Apply(decoded, listed);
        Assert.True(cleared.Ok, cleared.Error);
        Assert.Equal("", _values[ServerOptionOps.AllowedSteamIds]);
        Assert.Equal("Any admin of this server may install mods remotely.", cleared.Note);
    }

    [Fact]
    public void Before_the_server_binds_its_lock_list_the_admin_channel_refuses_every_edit()
    {
        // Review fix 5: bound on its own (not through the join admin install); fail closed until it is.
        ServerSettingsLock.RequireBound = true;
        ServerSettingsLock.Source = null;
        var r = Apply(Value(ServerOptionOps.PhotoMode, "false"));
        Assert.Equal(ServerSettingsLock.NotBoundError, r.Error);
        Assert.Empty(_stored);
        Assert.True(Apply(new JoinAdminRequest { Op = JoinAdminOps.Get }).Ok);
        ServerSettingsLock.Source = () => "";
        Assert.True(Apply(Value(ServerOptionOps.PhotoMode, "false")).Ok);
    }

    [Fact]
    public void Every_admin_may_change_remote_installs_and_the_list_with_before_and_after_audited()
    {
        // Decided 2026-10-06: every authorized admin edits the Operator rows (no install-list gate until RBAC);
        // an operator freezes them with [ServerSettings] LockedInGame.
        string listedId = "76561198000000011";
        _values[ServerOptionOps.AllowedSteamIds] = listedId;

        // Admin (not on the list) adds themselves; the audit line has the list before and after.
        var add = Apply(Value(ServerOptionOps.AllowedSteamIds, listedId + ", " + AdminId));
        Assert.True(add.Ok, add.Error);
        Assert.Equal(listedId + ", " + AdminId, _values[ServerOptionOps.AllowedSteamIds]);
        Assert.Contains(listedId, _audit.Last(), StringComparison.Ordinal);
        Assert.Contains(listedId + ", " + AdminId, _audit.Last(), StringComparison.Ordinal);

        _values[ServerOptionOps.AllowedSteamIds] = listedId;
        _values[ServerOptionOps.RemoteInstall] = "false";
        Assert.True(Apply(Value(ServerOptionOps.RemoteInstall, "true")).Ok);
        Assert.Equal("true", _values[ServerOptionOps.RemoteInstall]);

        // A player who is not an admin still changes nothing.
        Assert.Equal(JoinAdminPolicy.NotAdminError, Apply(Value(ServerOptionOps.RemoteInstall, "false"), Player).Error);
        Assert.Equal("true", _values[ServerOptionOps.RemoteInstall]);

        // The lock list is the operator's control.
        _lockText = "operator";
        Assert.Equal(ServerSettingsLock.NoteFor("operator"), Apply(Value(ServerOptionOps.RemoteInstall, "false")).Error);
        Assert.Equal("true", _values[ServerOptionOps.RemoteInstall]);
    }

    [Fact]
    public void A_locked_setting_is_refused_on_the_server_whatever_a_client_sends_and_audited()
    {
        string ids = SettingPlaces.JoinAdminId(ServerOptionOps.AllowedSteamIds);
        _lockText = ids;
        var r = Apply(Value(ServerOptionOps.AllowedSteamIds, "76561198000000011"));
        Assert.False(r.Ok);
        Assert.Equal(ServerSettingsLock.NoteFor(ids), r.Error);
        Assert.Equal("Locked in the server's config file (ServerSettings LockedInGame: " + ids + ").", r.Error);
        Assert.Empty(_stored);
        Assert.Equal("", _values[ServerOptionOps.AllowedSteamIds]);
        var line = _audit.Last();
        Assert.Contains("refused", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allowed-steam-ids", line);
        Assert.Contains("LockedInGame: " + ids, line);
        // The state carries the lock list, so the page draws the row read-only.
        Assert.Equal(new[] { ids }, r.State!.Lock);

        // Only that setting: remote installs themselves stay editable.
        Assert.True(Apply(Value(ServerOptionOps.RemoteInstall, "false")).Ok);
        // A cfg reload (the entry's new text) applies at once.
        _lockText = "";
        Assert.True(Apply(Value(ServerOptionOps.AllowedSteamIds, "76561198000000011")).Ok);
        Assert.Empty(Apply(new JoinAdminRequest { Op = JoinAdminOps.Get }).State!.Lock);
    }

    [Fact]
    public void The_lock_list_parses_ids_groups_and_star_and_warns_each_unknown_entry_once()
    {
        var warnings = new List<string>();
        var set = ServerSettingsLock.Parse(
            " Operator ; progenitor/ServerMessage/Message, example.mod/General/Count  bogus  operator *", warnings.Add);
        Assert.Equal(new[] { "operator", "progenitor/ServerMessage/Message", "example.mod/General/Count", "*" }, set.Entries);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("'bogus'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("example.mod/General/Count", StringComparison.Ordinal));

        // The id first, then a group, then *.
        Assert.Equal("progenitor/ServerMessage/Message", set.Match("progenitor/ServerMessage/Message"));
        Assert.Equal("operator", set.Match(SettingPlaces.JoinAdminId(ServerOptionOps.NoticeSeconds)));
        Assert.Equal("*", set.Match(SettingPlaces.JoinAdminId(ServerOptionOps.PhotoMode)));
        Assert.Null(ServerSettingsLock.Parse("").Match(SettingPlaces.JoinAdminId(ServerOptionOps.PhotoMode)));

        // Every group names settings, and the vanilla rules are a group of their own.
        Assert.Contains(ServerSettingsLock.GroupServerRules, ServerSettingsLock.GroupsOf("progenitor/ServerRules/ClanSize"));
        Assert.Contains(ServerSettingsLock.GroupJoin, ServerSettingsLock.GroupsOf(SettingPlaces.Id("ServerMessage", "NextReset")));
        Assert.Contains(ServerSettingsLock.GroupServerList, ServerSettingsLock.GroupsOf(SettingPlaces.Id("ServerList", "Description")));
        Assert.Contains(ServerSettingsLock.GroupNoProgenitor, ServerSettingsLock.GroupsOf(SettingPlaces.Id("ServerMessage", "NoProgenitorMessage")));
        Assert.Contains(ServerSettingsLock.GroupGameplay, ServerSettingsLock.GroupsOf(SettingPlaces.JoinAdminId(ServerOptionOps.AllySafeHits)));
        Assert.Contains(ServerSettingsLock.GroupModsPlayersNeed, ServerSettingsLock.GroupsOf(SettingPlaces.JoinAdminId(SettingPlaces.OpRecommended)));
        Assert.Empty(ServerSettingsLock.GroupsOf("example.mod/General/Count"));

        // The server's copy warns once per entry however often it is read, and follows the entry's text.
        _lockText = "bogus";
        ServerSettingsLock.Current();
        ServerSettingsLock.Current();
        _lockText = "bogus, operator";
        ServerSettingsLock.Current();
        Assert.Single(_warnings, w => w.Contains("'bogus'", StringComparison.Ordinal));
        Assert.Equal(new[] { "operator" }, ServerSettingsLock.Current().Entries);
        // The key itself is never a lockable or drawn setting.
        Assert.Equal(SettingPlaces.CfgOnly, SettingPlaces.Find(SettingPlaces.Id(ServerSettingsLock.Section, ServerSettingsLock.Key))!.Home);
    }

    [Fact]
    public void A_group_locks_its_subheading_and_star_locks_every_operation_reads_stay_open()
    {
        _lockText = "operator";
        string operatorNote = ServerSettingsLock.NoteFor("operator");
        Assert.Equal(operatorNote, Apply(Value(ServerOptionOps.RemoteInstall, "false")).Error);
        Assert.Equal(operatorNote, Apply(Seconds(ServerOptionOps.NoticeSeconds, 9)).Error);
        // Review fix 7: the refusal's audit line logs the value before (remote installs were on), not an empty one.
        Assert.Contains("operator", _audit[_audit.Count - 2], StringComparison.Ordinal);
        Assert.Contains("true", _audit[_audit.Count - 2], StringComparison.Ordinal);
        Assert.True(Apply(Value(ServerOptionOps.PhotoMode, "false")).Ok);

        _lockText = "mods-players-need";
        string needNote = ServerSettingsLock.NoteFor("mods-players-need");
        // The popup's lists, its choices and the window are under Mods players need.
        Assert.Equal(needNote, Apply(new JoinAdminRequest { Op = JoinAdminOps.RecommendAdd, Id = "some.mod", Reason = "Nice." }).Error);
        Assert.Equal(needNote, Apply(new JoinAdminRequest { Op = JoinAdminOps.Window, Seconds = 30 }).Error);
        Assert.Equal(needNote, Apply(new JoinAdminRequest { Op = JoinAdminOps.MidSession, Value = "disconnect" }).Error);
        // can't-be-checked is at home in the popup and mirrored under Players without Progenitor: both groups lock it.
        Assert.Equal(needNote, Apply(new JoinAdminRequest { Op = JoinAdminOps.Unverified, Value = "refuse" }).Error);
        _lockText = "no-progenitor";
        Assert.Equal(ServerSettingsLock.NoteFor("no-progenitor"), Apply(new JoinAdminRequest { Op = JoinAdminOps.Unverified, Value = "refuse" }).Error);
        Assert.Equal(ServerSettingsLock.NoteFor("no-progenitor"), Apply(Seconds(ServerOptionOps.AnswerTimeout, 30)).Error);
        Assert.True(Apply(new JoinAdminRequest { Op = JoinAdminOps.Window, Seconds = 30 }).Ok);

        _lockText = "*";
        int before = _stored.Count;
        Assert.Equal(ServerSettingsLock.NoteFor("*"), Apply(Value(ServerOptionOps.AllySafeHits, "false")).Error);
        Assert.Equal(before, _stored.Count);
        // Reading the state is never locked.
        var get = Apply(new JoinAdminRequest { Op = JoinAdminOps.Get });
        Assert.True(get.Ok);
        Assert.Equal(new[] { "*" }, get.State!.Lock);
    }

    [Fact]
    public void The_deprecated_switch_still_locks_the_list_and_logs_once()
    {
        _legacy = true;
        var r = Apply(Value(ServerOptionOps.AllowedSteamIds, "76561198000000011"));
        Assert.Equal("Locked in the server's config file ([RemoteInstall] AllowedSteamIdsInGame = false).", r.Error);
        Assert.Contains(ServerSettingsLock.LegacyEntry, _audit.Last());
        Assert.Equal(new[] { ServerSettingsLock.LegacyId }, r.State!.Lock);
        Apply(Value(ServerOptionOps.AllowedSteamIds, "76561198000000012"));
        Assert.Single(_warnings, w => w.Contains("deprecated", StringComparison.Ordinal));
        Assert.True(Apply(Value(ServerOptionOps.RemoteInstall, "false")).Ok);
    }

    [Fact]
    public void A_locked_server_wide_ConfigSync_row_is_refused_with_an_audit_line()
    {
        var audit = new List<string>();
        var file = new ConfigFile(Path.Combine(_dir, "progenitor.servermessage.cfg"), saveOnInit: false);
        var message = file.Bind("ServerMessage", "Message", "Hello", "Message.");
        ConfigSync.ResetForTests();
        ServerSettings.Uninstall();
        ServerSettings.Install();
        try
        {
            ServerSettings.Audit = audit.Add;
            ConfigSync.Register(SettingPlaces.PluginId, message);
            using var editable = ServerSettings.Register(SettingPlaces.PluginId, message);
            string rowId = SettingPlaces.Id("ServerMessage", "Message");
            _lockText = "join";
            var request = new ServerSettingsMessage(Guid.NewGuid(), rowId, typeof(string).FullName!, "Hello", "Bye", false, "");
            var refused = ServerSettings.Apply(request, handshakeOk: true, Admin);
            Assert.False(refused.Success);
            Assert.Equal(ServerSettingsLock.NoteFor("join"), refused.Error);
            Assert.Equal("Hello", message.Value);
            Assert.Contains(ServerSettingsLock.NoteFor("join"), audit.Last());

            _lockText = "server-list";
            Assert.True(ServerSettings.Apply(request, handshakeOk: true, Admin).Success);
            Assert.Equal("Bye", message.Value);
        }
        finally
        {
            ServerSettings.Uninstall();
            ConfigSync.ResetForTests();
        }
    }

    [Fact]
    public void A_player_who_is_not_an_admin_changes_nothing_and_learns_nothing()
    {
        var r = Apply(Value(ServerOptionOps.RemoteInstall, "false"), Player);
        Assert.False(r.Ok);
        Assert.Equal(JoinAdminPolicy.NotAdminError, r.Error);
        Assert.Null(r.State);
        Assert.Empty(_stored);
        Assert.Contains("remote-install", _audit.Last());
    }

    [Fact]
    public void A_value_the_server_cannot_store_is_reported_and_an_unbound_one_refused()
    {
        ServerOptions.Set = (_, _) => false;
        var r = Apply(Value(ServerOptionOps.PhotoMode, "false"));
        Assert.False(r.Ok);
        Assert.Equal(ServerOptions.StoreFailedError, r.Error);
        _values.Remove(ServerOptionOps.AllySafeHits);
        var unbound = Apply(Value(ServerOptionOps.AllySafeHits, "false"));
        Assert.False(unbound.Ok);
        Assert.Equal(ServerOptions.NotLoadedError, unbound.Error);
        Assert.False(Apply(new JoinAdminRequest { Op = JoinAdminOps.Get }).State!.Options.ContainsKey(ServerOptionOps.AllySafeHits));
    }

    [Fact]
    public void Option_edits_spend_the_edit_rate_limit()
    {
        var flood = new RemoteInstallActor("Flood", 76561198000000009UL, isAdmin: true);
        int refused = 0;
        for (int i = 0; i < 15; i++)
            if (JoinAdminServer.Apply(Seconds(ServerOptionOps.NoticeSeconds, i), true, flood).Error == JoinAdminPolicy.RateLimitedError) refused++;
        Assert.True(refused >= 5);
    }

    [Fact]
    public void The_state_round_trips_the_options_and_the_lock_and_an_older_server_sends_none()
    {
        _lockText = "operator, progenitor/ServerMessage/Message";
        var state = JoinAdminServer.BuildState();
        Assert.True(state.OptionsSupported);
        var bytes = JoinAdminWire.EncodeResponse(new JoinAdminResponse { Nonce = 4, Op = "get", Ok = true, State = state });
        Assert.True(JoinAdminWire.TryDecodeResponse(bytes, out var back));
        Assert.True(back.State!.OptionsSupported);
        Assert.Equal("20", back.State.Options[ServerOptionOps.AnswerTimeout]);
        Assert.Equal("", back.State.Options[ServerOptionOps.AllowedSteamIds]);
        Assert.Equal(new[] { "operator", "progenitor/ServerMessage/Message" }, back.State.Lock);

        var older = new JoinAdminState();
        Assert.True(JoinAdminWire.TryDecodeResponse(JoinAdminWire.EncodeResponse(new JoinAdminResponse { Ok = true, State = older }), out var old));
        Assert.False(old.State!.OptionsSupported);
        Assert.Empty(old.State.Options);

        // Seconds travel as a number for every seconds option.
        Assert.True(JoinAdminWire.TryDecodeRequest(JoinAdminWire.EncodeRequest(Seconds(ServerOptionOps.NoticeSeconds, 7)), out var sec));
        Assert.True(sec.HasSeconds);
        Assert.Equal(7, sec.Seconds);
    }

    [Fact]
    public void Removing_yourself_from_the_list_is_what_asks_for_a_confirmation()
    {
        const ulong me = 76561198000000011UL;
        string mine = me.ToString(), other = "76561198000000012";
        Assert.True(ServerOptions.CutsOffSelf("", other, me));
        Assert.True(ServerOptions.CutsOffSelf(mine + ", " + other, other, me));
        Assert.False(ServerOptions.CutsOffSelf(other, other + ", " + mine, me));
        Assert.False(ServerOptions.CutsOffSelf(other, "", me));
        Assert.False(ServerOptions.CutsOffSelf(mine, "", me));
        Assert.False(ServerOptions.CutsOffSelf("", other, 0));
    }
}

/// <summary>
/// Settings organization stage 5, client half: Server settings on Progenitor's page under its six subheadings, the
/// admin-channel rows hidden from a player who is not an admin (decision 5), one stored value for a choice the Players
/// joining popup also draws, the read-only locked list and the confirmations. Game-free: the fake UI runtime.
/// </summary>
[Collection("ServerSettingsLock")]
public sealed class ServerSettingsPageTests : IDisposable
{
    private readonly UiTests.FakeUiRuntime _runtime = new();
    private readonly TestLogSource _log = new();
    private readonly string _dir;
    private readonly object _owner = new();
    private readonly ConfigFile _shared;
    private readonly List<IDisposable> _handles = new();
    private readonly List<JoinAdminRequest> _sent = new();
    private readonly List<Action<JoinAdminResponse?>> _answers = new();
    private int _gets;
    private JsonDocument? _rows;
    private SettingsScopeContext _context;
    private bool _admin = true;
    private JoinAdminState? _state;

    public ServerSettingsPageTests()
    {
        UiLayoutBatch.ResetForTests();
        UiApi.ResetForTests();
        UiApi.SetRuntimeForTests(_runtime);
        Plugin.Logger = _log;
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        Handshake.ResetTables();
        ModMenuInventory.ResetForTests();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        ModSettingsPages.ResetForTests();
        ConfigSync.ResetForTests();
        Keybinds.ResetForTests();
        CoreUiSettings.Reset();
        ModsMenu.ResetNavigationForTests();
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.KeybindsPostForTests = step => step();
        _context = Joined(admin: true);
        ModsMenu.ScopeContextForTests = _ => _context;
        ConfigurationPane.ResetServerInstallStateForTests();
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-server-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _shared = new ConfigFile(Path.Combine(_dir, ConfigHelper.SharedFileName), saveOnInit: true);
        ModsMenu.SetConfigFileForTests(_shared);
        ModsMenu.BindModsConfig(_shared);
        ModPagePane.DialogFactory = title => UiApi.Panel(title, _owner, SettingsPanel.PluginId);
        _state = State();
        JoinAdminRows.Reset();
        JoinAdminRows.AdminProbe = () => _admin;
        JoinAdminRows.StateProbe = () => _state;
        JoinAdminRows.SelfSteamId = () => 76561198000000011UL;
        JoinAdminRows.Requester = (request, answer) =>
        {
            // The page's reads of the server's state are counted apart from its edits.
            if (request.Op == JoinAdminOps.Get)
            {
                _gets++;
                return null;
            }

            _sent.Add(request);
            _answers.Add(answer);
            return null;
        };
        ModsMenu.JoinAdminValueReader = JoinAdminRows.ReadPlace;
        ServerSettingsLock.Reset();
        ServerSettingsLock.ClientProbe = JoinAdminRows.LockedBy;
        ServerSettingsLock.ClientFingerprint = JoinAdminRows.LockFingerprint;
        // Three homes: one registration, two homes — On this server on Progenitor's page, the rest on the Server tab.
        _handles.Add(ModSettingsPages.Register(_owner, SettingsPanel.PluginId, page => BuildBoth(page, new ProgenitorPage.ServerParts
        {
            WhenPlayersJoin = p => p.Button("Show", () => { }, label: "Server message").InScope(SettingScope.ServerWide),
            PlayersJoiningRow = p => p.Button(JoinServerSettings.PlayersJoiningButton, () => { }, label: JoinServerSettings.PlayersJoiningLabel,
                showIf: () => _admin).InScope(SettingScope.ServerWide),
            ServerList = p => p.Section(ProgenitorPage.ServerListTitle).Info("Room left", () => "128", refreshSeconds: 0).InScope(SettingScope.ServerWide),
            NoProgenitorMessage = p => p.Info("Message without Progenitor", () => "(default)", refreshSeconds: 0).InScope(SettingScope.ServerWide),
        })));
    }

    public void Dispose()
    {
        _rows?.Dispose();
        foreach (var h in _handles) h.Dispose();
        ModsMenu.ScopeContextForTests = null;
        ModsMenu.JoinAdminValueReader = null;
        ModPagePane.DialogFactory = null;
        JoinAdminRows.Reset();
        ServerRulesRows.Reset();
        ServerSettingsLock.Reset();
        ServerSettings.Uninstall();
        UiLayoutBatch.ResetForTests();
        ModsMenu.Close();
        ModSettingsPages.ResetForTests();
        ModsMenu.LedgerForTests = Array.Empty<InstalledLedgerRow>();
        ModsMenu.SetInstallCatalogForTests(null);
        ModsMenu.ResetTabBindingsForTests();
        ModsMenu.ResetNavigationForTests();
        ModsWorkspaceFrame.ResetForTests();
        ModsMenu.UninstallLifecycleWatch();
        Handshake.UninstallClientServerContextWatch();
        ModMenuInventory.ResetForTests();
        Handshake.ResetTables();
        WorldEpoch.ResetForTests();
        SettingsCatalog.ResetForTests();
        ConfigSync.ResetForTests();
        Keybinds.ResetForTests();
        CoreUiSettings.Reset();
        ConfigurationPane.ResetServerInstallStateForTests();
        ConfigHelper.UninstallAll();
        Plugin.Logger = null;
        UiApi.ResetForTests();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static SettingsScopeContext Joined(bool admin) => new()
    {
        Connection = SettingConnection.Progenitor,
        ServerPart = ServerPartState.Running,
        CanEditServerWide = admin,
        ServerName = "Night Garden",
        ServerAddress = "127.0.0.1:9976",
    };

    private static JoinAdminState State(params string[] lockList)
    {
        var s = new JoinAdminState { PolicySupported = true, Unverified = "allow", MidSession = "warn", OptionsSupported = true };
        s.Options[ServerOptionOps.AnswerTimeout] = "20";
        s.Options[ServerOptionOps.PhotoMode] = "true";
        s.Options[ServerOptionOps.AllySafeHits] = "true";
        s.Options[ServerOptionOps.RemoteInstall] = "true";
        s.Options[ServerOptionOps.AllowedSteamIds] = "";
        s.Options[ServerOptionOps.NoticeSeconds] = "5";
        s.Options[ServerOptionOps.NoticeRepeatSeconds] = "3";
        s.Options[ServerOptionOps.NoticeCreationLimitSeconds] = "600";
        foreach (string op in ServerChatOptions.AdminOps) s.Options[op] = op == ServerOptionOps.ChatRelayUrl ? "set" : ServerChatOptions.DefaultOf(op);
        s.Lock.AddRange(lockList);
        return s;
    }

    private static void BuildBoth(SettingsPageBuilder page, ProgenitorPage.ServerParts parts)
    {
        ProgenitorPage.BuildOnThisServer(page);
        ProgenitorPage.BuildServerTab(page, parts);
    }

    /// <summary>Three homes: the Server tab, where the server's setup is drawn.</summary>
    private void OpenPage()
    {
        if (!ModsMenu.IsOpen) ModsMenu.Open();
        ModsMenu.SelectTab("server");
    }

    /// <summary>Three homes: Progenitor's page, where its own two server toggles are (On this server).</summary>
    private void OpenProgenitor()
    {
        if (!ModsMenu.IsOpen) ModsMenu.Open();
        ModsMenu.SelectTab("mods");
        ModsMenu.OpenMod(SettingsPanel.PluginId);
    }

    private JsonElement Content()
    {
        _rows?.Dispose();
        _rows = JsonDocument.Parse(JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")));
        return _rows.RootElement;
    }

    private static List<JsonElement> All(JsonElement rows, string key)
    {
        var list = new List<JsonElement>();
        foreach (var row in rows.EnumerateArray())
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value)) list.Add(value);
        return list;
    }

    private static List<JsonElement> AdminRows(JsonElement rows) =>
        All(rows, ModSettingsView.RowSetting).Where(s => s.GetProperty("kind").GetString() == "admin-setting").ToList();

    private static JsonElement Row(JsonElement rows, string op) =>
        AdminRows(rows).Single(s => s.GetProperty("key").GetString() == op);

    private ModSettingsView View => ModsMenu.SelectedTabForTests == ModsTab.Server
        ? ModsMenu.ServerPaneForTests!.SettingsView!
        : ModsMenu.ModPage!.SettingsView!;

    /// <summary>The server answers the last request with <paramref name="state"/>.</summary>
    private void Answer(bool ok = true, string note = "", string error = "")
    {
        var answer = _answers.Last();
        answer(new JoinAdminResponse { Ok = ok, Note = note, Error = error, Op = _sent.Last().Op, State = _state });
    }

    [Fact]
    public void The_Server_tab_has_its_groups_in_order_and_Progenitors_page_only_its_two_toggles()
    {
        OpenPage();
        var rows = Content();
        var titles = All(rows, ModSettingsView.RowSection).Select(s => s.GetProperty("title").GetString()).ToArray();
        // A server without the Server rules channel draws no Server rules group.
        Assert.Equal(ProgenitorPage.ServerTabTitles.Where(t => t != ProgenitorPage.ServerRulesTitle), titles);

        string SectionOf(string op) => Row(rows, op).GetProperty("section").GetString()!;
        Assert.Equal(ProgenitorPage.ModsPlayersNeedTitle, SectionOf(JoinAdminOps.MidSession));
        Assert.Equal(ProgenitorPage.PlayersWithoutProgenitorTitle, SectionOf(ServerOptionOps.AnswerTimeout));
        // Three homes: If mods can't be checked is with the other choices about the mods players need.
        Assert.Equal(ProgenitorPage.ModsPlayersNeedTitle, SectionOf(JoinAdminOps.Unverified));
        foreach (string op in new[] { ServerOptionOps.RemoteInstall, ServerOptionOps.AllowedSteamIds, ServerOptionOps.NoticeSeconds,
                     ServerOptionOps.NoticeRepeatSeconds, ServerOptionOps.NoticeCreationLimitSeconds })
            Assert.Equal(ProgenitorPage.OperatorTitle, SectionOf(op));
        // CHAT4: the filter and relay rows are the Chat group's; the webhook address is write-only and reads only "set".
        foreach (string op in ServerChatOptions.AdminOps)
        {
            Assert.Equal(ProgenitorPage.ChatServerTitle, SectionOf(op));
            Assert.Equal(SettingPlaces.ChatServer, Row(rows, op).GetProperty("home").GetString());
        }

        var url = Row(rows, ServerOptionOps.ChatRelayUrl);
        Assert.True(url.GetProperty("writeOnly").GetBoolean());
        Assert.Equal("set", url.GetProperty("value").GetString());
        Assert.False(url.TryGetProperty("field", out _));
        Assert.DoesNotContain(AdminRows(rows), s => s.GetProperty("key").GetString() is ServerOptionOps.PhotoMode or ServerOptionOps.AllySafeHits);
        // The mid-session choice is also in the Players joining popup.
        Assert.Equal(SettingPlaces.PlayersJoiningPanel, Row(rows, JoinAdminOps.MidSession).GetProperty("home").GetString());
        Assert.Equal(SettingPlaces.ModsPlayersNeed, Row(rows, JoinAdminOps.MidSession).GetProperty("alsoAt")[0].GetString());

        // Progenitor's page: On this server holds the two toggles and the link, nothing else of the server's.
        OpenProgenitor();
        rows = Content();
        var adminOnPage = AdminRows(rows).Select(s => s.GetProperty("key").GetString()).ToArray();
        Assert.Equal(new[] { ServerOptionOps.PhotoMode, ServerOptionOps.AllySafeHits }, adminOnPage);
        var photo = Row(rows, ServerOptionOps.PhotoMode);
        Assert.Equal(JoinAdminRows.PhotoModeLabel, photo.GetProperty("label").GetString());
        Assert.Equal("Allow photo mode on this server", photo.GetProperty("label").GetString());
        Assert.Equal("toggle", photo.GetProperty("control").GetString());
        Assert.Equal("true", photo.GetProperty("value").GetString());
        Assert.Equal(SettingPlaces.OnThisServer, photo.GetProperty("home").GetString());
        Assert.Equal(SettingPlaces.StoreJoinAdmin, photo.GetProperty("store").GetString());
        Assert.Equal(SettingScopes.Word(SettingScope.ServerWide), photo.GetProperty("scopeSection").GetString());
    }

    private static ServerRulesState RulesState()
    {
        var s = new ServerRulesState { PasswordSet = true, PasswordNextSet = true, RestartPending = 1, MercilessNow = false, MercilessNext = true,
            Territory = "Moderate", DaysRunning = 33, StartDate = "2026-09-02", StandardNow = true, StandardMayChange = true, AdminReserve = 4, MaxPlayersMax = 124 };
        foreach (var spec in ServerRules.Specs)
            s.Rows.Add(new ServerRuleRow { Key = spec.Key, Next = spec.Key switch
            {
                ServerRules.Name => "Night Garden", ServerRules.ClanSize => "2", ServerRules.Password => "set", ServerRules.GameModeType => "PvP",
                ServerRules.GameDifficulty => "Normal", ServerRules.BloodBoundEquipment => "false", ServerRules.CastleHeartDamageMode => ServerRules.Decaying,
                ServerRules.MaxConnectedUsers => "40", ServerRules.ResetDaysInterval => "0", _ => "Any",
            }, Loaded = spec.Key == ServerRules.ClanSize ? "4" : null, Pending = spec.Key == ServerRules.ClanSize, Editable = true, Source = "local" });
        return s;
    }

    [Fact]
    public void Server_rules_are_drawn_for_every_player_of_a_server_that_has_them_and_admins_edit_them()
    {
        var rules = RulesState();
        int gets = 0;
        ServerRulesRows.SupportedProbe = () => true;
        ServerRulesRows.StateProbe = () => rules;
        ServerRulesRows.CanEditProbe = () => _admin;
        ServerRulesRows.Requester = (request, answer) => { gets++; return null; };
        ModsMenu.ServerRulesValueReader = key => ServerRulesRows.Row(key) is { Next: { } next } ? (next, next) : null;
        OpenPage();
        var rows = Content();
        var titles = All(rows, ModSettingsView.RowSection).Select(s => s.GetProperty("title").GetString()).ToArray();
        Assert.Equal(ProgenitorPage.ServerTabTitles, titles);
        Assert.True(gets > 0);

        var clan = Row(rows, ServerRules.ClanSize);
        Assert.Equal(ServerRules.Title, clan.GetProperty("section").GetString());
        Assert.Equal("2", clan.GetProperty("value").GetString());
        Assert.True(clan.GetProperty("pending").GetBoolean());
        Assert.Equal(SettingPlaces.ServerRulesPlace, clan.GetProperty("home").GetString());
        Assert.Equal("4", clan.GetProperty("serverRule").GetProperty("loaded").GetString());
        // Stage 7: an admin edits it; the pending change is on the help line.
        Assert.True(clan.GetProperty("enabled").GetBoolean());
        Assert.Equal("", clan.GetProperty("readOnlyReason").GetString());
        Assert.Contains(ModSettingsView.PendingRestartNote, clan.GetProperty("helpLine").GetString(), StringComparison.Ordinal);

        // Server name is under Server list; the password reads only Set.
        Assert.Equal(ProgenitorPage.ServerListTitle, Row(rows, ServerRules.Name).GetProperty("section").GetString());
        var password = Row(rows, ServerRules.Password);
        Assert.Equal("Set", password.GetProperty("display").GetString());
        Assert.True(password.GetProperty("writeOnly").GetBoolean());
        Assert.True(password.GetProperty("serverRule").GetProperty("set").GetBoolean());
        Assert.False(password.TryGetProperty("field", out _));

        // The read-only lines.
        string Info(string label) => All(rows, ModSettingsView.RowSetting).Single(r => r.GetProperty("key").GetString() == "info/" + label).GetProperty("value").GetString()!;
        // PENDING1: a status row (not a setting) naming what waits, in gold.
        Assert.Equal("1 change applies at the next restart\n" + ServerRules.Find(ServerRules.ClanSize)!.Label + ": 4 → 2",
            Info(ServerRulesRows.PendingLabel));
        var pendingRow = All(rows, ModSettingsView.RowSetting).Single(r => r.GetProperty("key").GetString() == "info/" + ServerRulesRows.PendingLabel);
        Assert.Equal("status", pendingRow.GetProperty("style").GetString());
        Assert.Equal("amber", pendingRow.GetProperty("tone").GetString()); // PENDING2: muted amber lines
        Assert.Equal("No \u2192 Yes after the restart", Info(ServerRulesRows.MercilessLabel));
        Assert.Equal("Moderate", Info(ServerRulesRows.TerritoryLabel));
        Assert.Equal("33", Info(ServerRulesRows.DaysRunningLabel));
        Assert.Equal("Yes \u00B7 may change after the restart", Info(ServerRulesRows.StandardLabel));
        Assert.Equal("Off", Info(ServerRulesRows.NextWipeLabel));

        // settings_places reads the same values.
        var place = (Dictionary<string, object?>)ModsMenu.SettingsPlacesReadback()[ServerRules.Id(ServerRules.ClanSize)]!;
        Assert.Equal("2", place["value"]);
        Assert.Equal(SettingPlaces.StoreServerRules, place["store"]);

        // A player who is not an admin reads them, read-only for that reason.
        _admin = false;
        _context = Joined(admin: false);
        OpenPage();
        var playerRows = Content();
        var asPlayer = Row(playerRows, ServerRules.ClanSize);
        Assert.Equal(ServerRulesRows.AdminOnlyReason, asPlayer.GetProperty("readOnlyReason").GetString());
        // Live 2026-10-06: drawn as every other server row a non-admin sees, "Admin only · value" with the Server chip, never
        // a grayed control with a grayed Apply; the password only Set / Not set; Server name under Server list the same.
        Assert.Equal("readonly", asPlayer.GetProperty("control").GetString());
        Assert.True(asPlayer.GetProperty("viewOnly").GetBoolean());
        // Live 2026-10-06: normal brightness, like the other "Admin only · value" rows (nothing on it is a control).
        Assert.True(asPlayer.GetProperty("enabled").GetBoolean());
        Assert.Equal(true, _runtime.InteractableOf("info", ServerRules.Find(ServerRules.ClanSize)!.Label));
        Assert.Contains(ServerRulesRows.AdminOnlyReason, asPlayer.GetProperty("helpLine").GetString(), StringComparison.Ordinal);
        string clanLabel = ServerRules.Find(ServerRules.ClanSize)!.Label;
        Assert.Equal("Admin only · 2", _runtime.ValueOf("info", clanLabel));
        Assert.False(_runtime.HasWidget("input", clanLabel));
        // A pending row keeps its Restart chip (as a ConfigSync row does). Decided 2026-10-06: on the Server tab every row is a
        // server row, so the others carry no Server chip.
        Assert.Equal(ModSettingsRowMarks.RestartChip, _runtime.Chips[clanLabel]);
        Assert.Equal("", _runtime.Chips[ServerRules.Find(ServerRules.GameModeType)!.Label]);
        Assert.DoesNotContain(ModSettingsRowMarks.ServerChip, _runtime.Chips.Values);
        string passwordLabel = ServerRules.Find(ServerRules.Password)!.Label;
        Assert.Equal("Admin only · Set", _runtime.ValueOf("info", passwordLabel));
        Assert.False(_runtime.HasWidget("input", passwordLabel));
        string nameLabel = ServerRules.Find(ServerRules.Name)!.Label;
        Assert.Equal("Admin only · Night Garden", _runtime.ValueOf("info", nameLabel));
        foreach (var spec in ServerRules.Specs)
        {
            Assert.Equal("readonly", Row(playerRows, spec.Key).GetProperty("control").GetString());
            Assert.False(_runtime.HasWidget("input", spec.Label));
        }
        Assert.Equal("Admin only · PvP", _runtime.ValueOf("info", ServerRules.Find(ServerRules.GameModeType)!.Label));
    }

    [Fact]
    public void A_rule_is_sent_with_the_value_shown_and_wipes_and_a_removed_password_ask_first()
    {
        var rules = RulesState();
        var sent = new List<ServerRulesRequest>();
        var answers = new List<Action<ServerRulesResponse?>>();
        ServerRulesRows.SupportedProbe = () => true;
        ServerRulesRows.StateProbe = () => rules;
        ServerRulesRows.CanEditProbe = () => true;
        ServerRulesRows.Requester = (request, answer) =>
        {
            if (request.Op == ServerRulesRequest.OpGet) return null;
            sent.Add(request);
            answers.Add(answer);
            return null;
        };
        OpenPage();
        var clanLabel = ServerRules.Find(ServerRules.ClanSize)!.Label;
        Assert.True(_runtime.FireText(clanLabel, "3"));
        View.ApplyForBridge(clanLabel);
        var write = sent.Single();
        Assert.Equal((ServerRulesRequest.OpWrite, ServerRules.ClanSize, "3", "2", true), (write.Op, write.Key, write.Value, write.Expected, write.HasExpected));
        rules.Row(ServerRules.ClanSize)!.Next = "3";
        answers.Last()(new ServerRulesResponse { Ok = true, Note = "Saved: applies at the next restart.", State = rules });
        Assert.Equal("Saved: applies at the next restart.", Row(Content(), ServerRules.ClanSize).GetProperty("status").GetString());

        // Turning scheduled wipes on asks first, naming the backup folder; No sends nothing.
        var wipeLabel = ServerRules.Find(ServerRules.ResetDaysInterval)!.Label;
        Assert.True(_runtime.FireText(wipeLabel, "7"));
        View.ApplyForBridge(wipeLabel);
        Assert.Single(sent);
        // WIPECONFIRM1: turning wipes on asks for WIPE typed; the main button does nothing until it matches.
        Assert.Equal(WipePlan.Heading, UiDialog.Current!.Content.Title);
        Assert.Equal(WipePlan.WordWipe, UiDialog.Current.Content.ConfirmWord);
        Assert.Contains("_<ticks at wipe>", UiDialog.Current.Content.AllText, StringComparison.Ordinal);
        Assert.Contains("Wed, Sep 2, 2026", UiDialog.Current.Content.AllText, StringComparison.Ordinal);
        Assert.Contains("Wed, Sep 9, 2026", UiDialog.Current.Content.AllText, StringComparison.Ordinal);
        UiDialog.ChooseForBridge("no");
        Assert.Single(sent);
        // No puts the server's value back in the field.
        Assert.Equal("0", Row(Content(), ServerRules.ResetDaysInterval).GetProperty("field").GetString());
        Assert.True(_runtime.FireText(wipeLabel, "7"));
        View.ApplyForBridge(wipeLabel);
        UiDialog.ChooseForBridge("yes");
        Assert.Single(sent); // not typed yet
        Assert.False((bool)UiDialog.Describe()["mainEnabled"]!);
        ModsMenu.TypeDialog("wipe");
        Assert.True((bool)UiDialog.Describe()["mainEnabled"]!);
        UiDialog.ChooseForBridge("yes");
        Assert.Equal("7", sent.Last().Value);
        // The admin's yes travels with the request: the server refuses the change without it.
        Assert.True(sent.Last().Confirm);

        // The password: sent as typed, never read back; an empty Apply asks before removing it.
        var passwordLabel = ServerRules.Find(ServerRules.Password)!.Label;
        Assert.True(_runtime.FireText(passwordLabel, "hunter2"));
        View.ApplyForBridge(passwordLabel);
        Assert.Equal("hunter2", sent.Last().Value);
        Assert.False(sent.Last().HasExpected);
        answers.Last()(new ServerRulesResponse { Ok = true, Note = "Applied now, to the next player who joins.", State = rules });
        Assert.DoesNotContain("hunter2", JsonSerializer.Serialize(ModsMenu.ReadVisibleWidgets("content")), StringComparison.Ordinal);
        int before = sent.Count;
        Assert.True(_runtime.FireText(passwordLabel, ""));
        View.ApplyForBridge(passwordLabel);
        Assert.Equal(ServerRulesRows.PasswordClearHeading, UiDialog.Current!.Content.Title);
        UiDialog.ChooseForBridge("yes");
        Assert.Equal(before + 1, sent.Count);
        Assert.Equal("", sent.Last().Value);
    }

    private (ServerRulesState Rules, List<ServerRulesRequest> Sent) ServeRules()
    {
        var rules = RulesState();
        var sent = new List<ServerRulesRequest>();
        ServerRulesRows.SupportedProbe = () => true;
        ServerRulesRows.StateProbe = () => rules;
        ServerRulesRows.CanEditProbe = () => true;
        ServerRulesRows.Requester = (request, answer) =>
        {
            if (request.Op == ServerRulesRequest.OpGet) return null;
            sent.Add(request);
            // The server answers at once (its state unchanged), so the row takes the next change.
            answer(new ServerRulesResponse { Ok = true, Op = request.Op, Key = request.Key, Note = "Saved.", State = rules });
            return null;
        };
        return (rules, sent);
    }

    /// <summary>
    /// FIELDAPPLY1 (live 2026-10-06: field_apply {label, text "3"} on the wipe row returned ok and did nothing): the bridge
    /// puts the text in the field and presses Apply's own handler, so the wipe confirmation opens; typing WIPE then No
    /// writes nothing. The Next wipe date takes the same confirmation, and its Set button is addressable for click_at.
    /// </summary>
    [Fact]
    public void Field_apply_with_text_opens_the_wipe_confirmation_and_cancel_writes_nothing()
    {
        var (rules, sent) = ServeRules();
        ServerRulesRows.Today = () => new DateTime(2026, 10, 6, 12, 0, 0); // the world (Sep 2) is 34 days old
        OpenPage();
        var wipeLabel = ServerRules.Find(ServerRules.ResetDaysInterval)!.Label;

        var d = View.ApplyForBridge(wipeLabel, "3");
        Assert.Equal(true, d["confirmation"]);
        Assert.Empty(sent);
        var dialog = UiDialog.Current!;
        Assert.Equal(WipePlan.WordWipe, dialog.Content.ConfirmWord);
        Assert.Contains("the NEXT restart wipes it", dialog.Content.AllText, StringComparison.Ordinal);
        // WIPEPOLISH1: a mismatch shows the hint under the field; the word clears it. The facts are left-aligned lines.
        ModsMenu.TypeDialog("WIP");
        Assert.Equal(UiDialogs.ConfirmWordHint("WIPE"), UiDialog.Describe()["error"]);
        Assert.Equal("Warning", dialog.Content.Facts.Last().Label);
        Assert.Equal(DialogFactTone.Danger, dialog.Content.Facts.Last().Tone);
        ModsMenu.TypeDialog("WIPE");
        Assert.Equal("", UiDialog.Describe()["error"]);
        Assert.True((bool)UiDialog.Describe()["mainEnabled"]!);
        UiDialog.ChooseForBridge("no");
        Assert.Empty(sent);
        Assert.Equal("0", rules.Row(ServerRules.ResetDaysInterval)!.Next);
        Assert.Null(UiDialog.Current is { IsOpen: true } ? UiDialog.Current : null);

        // The Next wipe date: Done goes through the same confirmation; Cancel there writes nothing either.
        var wipeRow = AdminRowsByLabel(Content(), ServerRulesRows.NextWipeDateLabel);
        Assert.Equal("open", wipeRow.GetProperty("openCell").GetString());
        Assert.StartsWith("datetime:" + ServerRulesRows.NextWipeDateLabel, wipeRow.GetProperty("dateRow").GetString(), StringComparison.Ordinal);
        var field = View.DateTimeFieldForBridge(ServerRulesRows.NextWipeDateLabel)!;
        field.Open();
        Assert.True(UiDateTimeBridge.Day(9)["ok"] is true);
        UiDateTimeBridge.Done();
        Assert.Empty(sent);
        Assert.Equal(WipePlan.Heading, UiDialog.Current!.Content.Title);
        UiDialog.ChooseForBridge("no");
        Assert.Empty(sent);

        // The bridge passes the text through.
        using var doc = JsonDocument.Parse("{\"action\":\"field_apply\",\"label\":\"Scheduled wipes every (days)\",\"text\":\"3\"}");
        var request = Progenitor.Dev.Ops.ModsUiArgs.Parse(doc.RootElement.Clone());
        Assert.Equal("3", request.Value);
        Assert.Contains("TextTestDict(\"ApplyText\", request.Label, request.Value)", File.ReadAllText(Path.Combine(
            RepoRootForSource(), "src", "Progenitor.Dev", "Ops", "ModsUiOps.TextTest.cs")));
    }

    private static JsonElement AdminRowsByLabel(JsonElement rows, string label) =>
        AdminRows(rows).Single(s => s.GetProperty("label").GetString() == label);

    private static string RepoRootForSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Progenitor.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void A_wipe_change_carries_the_confirmation_and_a_past_reset_date_says_the_next_start_resets()
    {
        var (rules, sent) = ServeRules();
        // Started Sep 2; today Oct 5.
        ServerRulesRows.Today = () => new DateTime(2026, 10, 5, 12, 0, 0);
        OpenPage();
        var wipeLabel = ServerRules.Find(ServerRules.ResetDaysInterval)!.Label;

        // 60 days: the reset date (Nov 1) is ahead, but turning wipes on always asks; yes sends confirm.
        Assert.True(_runtime.FireText(wipeLabel, "60"));
        View.ApplyForBridge(wipeLabel);
        Assert.Equal(WipePlan.Heading, UiDialog.Current!.Content.Title);
        Assert.StartsWith("Change: off → every 60 days on Any", UiDialog.Current.Content.AllText, StringComparison.Ordinal);
        ModsMenu.TypeDialog("WIPE");
        UiDialog.ChooseForBridge("yes");
        Assert.True(sent.Last().Confirm);
        Assert.Equal("60", sent.Last().Value);

        // WIPECONFIRM1: every wipe change asks now. A weekday that moves the date later needs only a click.
        rules.Row(ServerRules.ResetDaysInterval)!.Next = "60";
        View.Reevaluate();
        var later = ServerRulesRows.ConfirmFor(ServerRules.DayOfReset, "Any", "Friday")!;
        Assert.Equal("", later.Word);

        // Lowering the interval to 7 puts the wipe date (Sep 9) in the past: WIPE, in red, says the next restart wipes.
        rules.Row(ServerRules.DayOfReset)!.Next = "Friday";
        View.Reevaluate();
        Assert.True(_runtime.FireText(wipeLabel, "7"));
        View.ApplyForBridge(wipeLabel);
        var dialog = UiDialog.Current!;
        Assert.Equal(WipePlan.Heading, dialog.Content.Title);
        Assert.True(dialog.Content.Danger);
        Assert.Contains("the NEXT restart wipes it", dialog.Content.AllText, StringComparison.Ordinal);
        ModsMenu.TypeDialog("wipe");
        UiDialog.ChooseForBridge("yes");
        Assert.True(sent.Last().Confirm);

        // Any other rule never carries the confirmation.
        var clanLabel = ServerRules.Find(ServerRules.ClanSize)!.Label;
        Assert.True(_runtime.FireText(clanLabel, "3"));
        View.ApplyForBridge(clanLabel);
        Assert.False(sent.Last().Confirm);
    }

    [Fact]
    public void Max_players_is_checked_against_the_states_bound_and_the_password_field_is_masked()
    {
        var (rules, sent) = ServeRules();
        rules.MaxPlayersMax = 60;
        OpenPage();
        var maxLabel = ServerRules.Find(ServerRules.MaxConnectedUsers)!.Label;
        Assert.True(_runtime.FireText(maxLabel, "61"));
        View.ApplyForBridge(maxLabel);
        Assert.Empty(sent);
        var row = Row(Content(), ServerRules.MaxConnectedUsers);
        Assert.Contains("1 to 60", row.GetProperty("status").GetString(), StringComparison.Ordinal);
        Assert.Equal(60, row.GetProperty("max").GetInt32());
        Assert.True(_runtime.FireText(maxLabel, "60"));
        View.ApplyForBridge(maxLabel);
        Assert.Equal("60", sent.Single().Value);

        var passwordLabel = ServerRules.Find(ServerRules.Password)!.Label;
        Assert.True(_runtime.MaskedOf(passwordLabel));
        Assert.True(Row(Content(), ServerRules.Password).GetProperty("masked").GetBoolean());
        Assert.False(_runtime.MaskedOf(ServerRules.Find(ServerRules.Name)!.Label));
    }

    [Fact]
    public void Apply_sits_beside_every_one_line_field_and_labels_fit_the_name_column()
    {
        ServeRules();
        OpenPage();
        // Live 2026-10-05: Apply was drawn on a full-width row of its own under the field.
        foreach (string label in new[]
                 {
                     JoinAdminRows.AnswerTimeoutLabel, JoinAdminRows.AllowedIdsLabel, JoinAdminRows.NoticeSecondsLabel,
                     JoinAdminRows.NoticeRepeatLabel, JoinAdminRows.CreationLimitLabel,
                     ServerRules.Find(ServerRules.ClanSize)!.Label, ServerRules.Find(ServerRules.Password)!.Label,
                 })
            Assert.Contains(label, _runtime.InlineFields);

        // Live 2026-10-06 (blocker): every row with a field shows its name; the readback says so (captionShown), so a row
        // drawn with a field and an empty name column fails here and in a live widget_read check.
        foreach (var row in AdminRows(Content()))
        {
            Assert.True(row.GetProperty("captionShown").GetBoolean(), row.GetProperty("label").GetString());
            Assert.Equal(row.GetProperty("label").GetString(), row.GetProperty("caption").GetString());
        }

        // Live: "Message for players without Progen…" and "Longest wait in character creation (secon…" were cut.
        foreach (var row in AdminRows(Content()))
        {
            string label = row.GetProperty("label").GetString()!;
            Assert.True(label.Length <= MaxLabel, label);
        }

        foreach (var spec in ServerRules.Specs) Assert.True(spec.Label.Length <= MaxLabel, spec.Label);
        Assert.True(JoinServerSettings.PlayersJoiningLabel.Length <= MaxLabel);
    }

    [Fact]
    public void A_rule_drawn_as_a_dropdown_opens_on_click_and_writes_once_on_a_pick()
    {
        // DROPDOWN1 (user review 2026-10-06): each click on Game mode cycled to the next value and wrote it (four writes in
        // 7 s). A click opens the list; a pick writes once; Escape, a second click, a click outside or the current value
        // writes nothing.
        var (_, sent) = ServeRules();
        OpenPage();
        string label = ServerRules.Find(ServerRules.GameModeType)!.Label;
        int writes() => sent.Count(r => r.Key == ServerRules.GameModeType);

        Assert.True(_runtime.FireControlClick("choice", label));
        var list = UiChoiceHandle.OpenNow;
        Assert.NotNull(list);
        Assert.True(list!.IsOpen);
        Assert.Equal(0, writes());
        string current = list.Value;
        string other = list.Options.First(o => o.Value != current).Value;

        // A second click on the field closes it.
        Assert.True(_runtime.FireControlClick("choice", label));
        Assert.False(list.IsOpen);
        Assert.Null(UiChoiceHandle.OpenNow);

        // Escape closes it.
        Assert.True(_runtime.FireControlClick("choice", label));
        Assert.True(UiApi.TryEscapeHandlers());
        Assert.False(list.IsOpen);

        // A click outside (the list's click-catcher) closes it.
        Assert.True(_runtime.FireControlClick("choice", label));
        list.Dismiss();
        Assert.False(list.IsOpen);

        // The current value closes it.
        Assert.True(_runtime.FireControlClick("choice", label));
        Assert.False(list.Choose(current));
        Assert.False(list.IsOpen);
        Assert.Equal(0, writes());

        // A pick: one write, the list closed.
        Assert.True(_runtime.FireControlClick("choice", label));
        Assert.True(list.Choose(other));
        Assert.False(list.IsOpen);
        Assert.Equal(1, writes());
        Assert.Equal(other, sent.Last().Value);

        // Readback: dropdown {open, options [{value, label, current}]}.
        var described = list.DescribeList();
        Assert.Equal(false, described["open"]);
        Assert.NotEmpty((System.Collections.IEnumerable)described["options"]!);
    }

    // ---- PENDING1: the Pending changes status row ----------------------------------------------------------------

    private static ServerRulesState PendingState(params (string Key, string Loaded, string Next)[] changes)
    {
        var s = RulesState();
        foreach (var row in s.Rows) { row.Pending = false; row.Loaded = row.Next; }
        foreach (var (key, loaded, next) in changes)
        {
            var row = s.Row(key)!;
            row.Loaded = loaded;
            row.Next = next;
            row.Pending = true;
        }

        s.RestartPending = changes.Length;
        return s;
    }

    [Fact]
    public void Pending_changes_reads_none_in_the_muted_tone()
    {
        var s = PendingState();
        Assert.Equal(ServerRulesRows.NothingPendingLine, ServerRulesRows.PendingLine(s));
        Assert.Equal("None. Changes here apply when the server restarts.", ServerRulesRows.PendingLine(s));
        Assert.False(ServerRulesRows.PendingAttention(s));
        var back = ServerRulesRows.PendingReadback(s);
        Assert.Equal(0, back["count"]);
        Assert.Empty((List<object?>)back["items"]!);
    }

    [Fact]
    public void One_pending_change_is_named_with_its_old_and_new_value()
    {
        var s = PendingState((ServerRules.GameModeType, "PvP", "PvE"));
        string mode = ServerRules.Find(ServerRules.GameModeType)!.Label;
        Assert.Equal("1 change applies at the next restart\n" + mode + ": PvP → PvE", ServerRulesRows.PendingLine(s));
        Assert.True(ServerRulesRows.PendingAttention(s));
        Assert.Contains("• " + mode + " PvP → PvE", ServerRulesRows.PendingHelp(s), StringComparison.Ordinal);
        var item = (Dictionary<string, object?>)((List<object?>)ServerRulesRows.PendingReadback(s)["items"]!).Single()!;
        Assert.Equal(ServerRules.GameModeType, item["key"]);
        Assert.Equal("PvP", item["loaded"]);
        Assert.Equal("PvE", item["next"]);
    }

    [Fact]
    public void Many_pending_changes_are_cut_to_and_N_more_and_the_help_lists_them_all()
    {
        var s = PendingState(
            (ServerRules.ClanSize, "4", "3"),
            (ServerRules.GameModeType, "PvP", "PvE"),
            (ServerRules.GameDifficulty, "Normal", "Brutal"),
            (ServerRules.MaxConnectedUsers, "40", "60"),
            (ServerRules.BloodBoundEquipment, "false", "true"));
        string line = ServerRulesRows.PendingLine(s);
        Assert.StartsWith("5 changes apply at the next restart\n", line, StringComparison.Ordinal);
        Assert.Equal(6, line.Split('\n').Length); // PENDING2: every change on its own line (at most 8, then +N more)
        string help = ServerRulesRows.PendingHelp(s);
        foreach (var c in ServerRulesRows.PendingChanges(s)) Assert.Contains(c.Text, help, StringComparison.Ordinal);
        Assert.Equal(5, ServerRulesRows.PendingReadback(s)["count"]);
    }

    [Fact]
    public void A_pending_password_never_shows_its_value()
    {
        var s = PendingState((ServerRules.Password, "hunter2", "swordfish"));
        Assert.Equal("1 change applies at the next restart\nPassword changed", ServerRulesRows.PendingLine(s));
        var item = (Dictionary<string, object?>)((List<object?>)ServerRulesRows.PendingReadback(s)["items"]!).Single()!;
        Assert.Null(item["loaded"]);
        Assert.Null(item["next"]);
        Assert.Equal(ServerRulesRows.PasswordChangedText, item["text"]);
        string all = ServerRulesRows.PendingLine(s) + ServerRulesRows.PendingHelp(s)
            + System.Text.Json.JsonSerializer.Serialize(ServerRulesRows.PendingReadback(s));
        Assert.DoesNotContain("hunter2", all, StringComparison.Ordinal);
        Assert.DoesNotContain("swordfish", all, StringComparison.Ordinal);
    }

    [Fact]
    public void The_pending_row_is_a_status_row_with_its_readback()
    {
        var (rules, _) = ServeRules();
        OpenPage();
        var row = All(Content(), ModSettingsView.RowSetting).Single(r => r.GetProperty("key").GetString() == "info/" + ServerRulesRows.PendingLabel);
        Assert.Equal("status", row.GetProperty("style").GetString());
        var back = All(Content(), "pendingRestart").Single();
        Assert.Equal(1, back.GetProperty("count").GetInt32());
        Assert.Equal(ServerRules.ClanSize, back.GetProperty("items")[0].GetProperty("key").GetString());

        // Nothing pending: muted.
        foreach (var r in rules.Rows) r.Pending = false;
        View.Reevaluate();
        row = All(Content(), ModSettingsView.RowSetting).Single(r => r.GetProperty("key").GetString() == "info/" + ServerRulesRows.PendingLabel);
        Assert.Equal("muted", row.GetProperty("tone").GetString());
        Assert.Equal(ServerRulesRows.NothingPendingLine, row.GetProperty("value").GetString());
    }

    [Fact]
    public void Every_inline_Apply_asks_for_the_game_button_art()
    {
        ServeRules();
        OpenPage();
        // INLINEAPPLY1 (user review 2026-10-06): the inline Apply was a flat gray block beside the steel buttons. Each
        // one-line field's Apply is a game button (readback inlineButtons [{caption, art}]), the same as Show or Close Mods.
        var inline = new HashSet<string>(_runtime.InlineFields, StringComparer.Ordinal);
        int seen = 0;
        foreach (var row in AdminRows(Content()))
        {
            string label = row.GetProperty("label").GetString()!;
            if (!inline.Contains(label)) continue;
            Assert.True(row.TryGetProperty("inlineButtons", out var buttons), label);
            var list = buttons.EnumerateArray().ToList();
            Assert.NotEmpty(list);
            foreach (var b in list)
            {
                Assert.Equal("Apply", b.GetProperty("caption").GetString());
                Assert.Equal(UiSettingsGrid.GameButtonStyle, b.GetProperty("art").GetString());
            }

            seen++;
        }

        Assert.True(seen >= 7, "inline rows seen: " + seen);
    }

    /// <summary>The longest label the settings grid's name column shows whole at the page's narrowest width (live: 34 cut).</summary>
    private const int MaxLabel = 30;

    [Fact]
    public void A_locked_row_says_which_entry_locks_it_and_carries_the_lock_marker()
    {
        var (rules, _) = ServeRules();
        foreach (var r in rules.Rows)
        {
            r.Editable = false;
            r.LockedBy = ServerSettingsLock.GroupServerRules;
            r.Reason = "This setting can't be changed here.";
        }

        _state = State(ServerSettingsLock.GroupOperator);
        OpenPage();
        var clan = Row(Content(), ServerRules.ClanSize);
        Assert.False(clan.GetProperty("enabled").GetBoolean());
        string note = ServerSettingsLock.NoteFor(ServerSettingsLock.GroupServerRules);
        Assert.Equal("Locked in the server's config file (ServerSettings LockedInGame: server-rules).", note);
        Assert.Contains(note, clan.GetProperty("helpLine").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("can't be changed here", clan.GetProperty("helpLine").GetString(), StringComparison.Ordinal);
        Assert.Equal(ModSettingsRowMarks.LockedChip, _runtime.Chips[ServerRules.Find(ServerRules.ClanSize)!.Label]);
        // An operator row is locked by its group; an open row carries no marker.
        Assert.Equal(ModSettingsRowMarks.LockedChip, _runtime.Chips[JoinAdminRows.NoticeSecondsLabel]);
        Assert.Equal("", _runtime.Chips[JoinAdminRows.MidSessionLabel]);
    }

    [Fact]
    public void An_admin_the_install_list_does_not_name_still_edits_remote_installs_and_the_list()
    {
        // Decided 2026-10-06: every admin edits the Operator rows (no install-list gate until RBAC).
        // This player is 76561198000000011; the list names only ...12.
        _state!.Options[ServerOptionOps.AllowedSteamIds] = "76561198000000012";
        OpenPage();
        var rows = Content();
        foreach (string op in new[] { ServerOptionOps.RemoteInstall, ServerOptionOps.AllowedSteamIds })
        {
            var row = Row(rows, op);
            Assert.True(row.GetProperty("enabled").GetBoolean());
            Assert.Equal("", row.GetProperty("readOnlyReason").GetString());
        }

        // Turning installs off still asks first.
        Assert.True(_runtime.FireToggle(JoinAdminRows.RemoteInstallLabel, false));
        Assert.Empty(_sent);
        Assert.Equal(JoinAdminRows.RemoteOffHeading, UiDialog.Current!.Content.Title);
        UiDialog.ChooseForBridge("yes");
        Assert.Equal("false", _sent.Single().Value);
    }

    [Fact]
    public void A_player_who_is_not_an_admin_sees_none_of_the_admin_rows()
    {
        _admin = false;
        _context = Joined(admin: false);
        OpenPage();
        var rows = Content();
        Assert.Empty(AdminRows(rows));
        // The rows every player has stay: the server message's Show button.
        Assert.Contains(All(rows, ModSettingsView.RowSetting), s => s.GetProperty("key").GetString() == "button/Show");
        Assert.Empty(_sent);
    }

    [Fact]
    public void A_choice_the_popup_also_draws_is_one_stored_value()
    {
        OpenPage();
        Assert.Equal("warn", Row(Content(), JoinAdminOps.MidSession).GetProperty("value").GetString());
        // The popup (or another admin) changes it: the server's next state is what both places show.
        _state!.MidSession = "disconnect";
        View.Reevaluate();
        var row = Row(Content(), JoinAdminOps.MidSession);
        Assert.Equal("disconnect", row.GetProperty("value").GetString());
        Assert.Equal(ClientPolicyText.MidSessionDisconnect, row.GetProperty("display").GetString());
        var places = ModsMenu.SettingsPlacesReadback();
        var place = (Dictionary<string, object?>)places[SettingPlaces.JoinAdminId(JoinAdminOps.MidSession)]!;
        Assert.Equal("disconnect", place["value"]);
        Assert.Equal(new List<string> { SettingPlaces.PlayersJoiningPanel, SettingPlaces.ModsPlayersNeed }, place["places"]);
    }

    [Fact]
    public void A_switch_sends_its_request_and_shows_the_servers_answer()
    {
        OpenProgenitor();
        Assert.True(_runtime.FireToggle(JoinAdminRows.AllySafeHitsLabel, false));
        var sent = _sent.Single();
        Assert.Equal(ServerOptionOps.AllySafeHits, sent.Op);
        Assert.Equal("false", sent.Value);
        Assert.True(Row(Content(), ServerOptionOps.AllySafeHits).GetProperty("requestPending").GetBoolean());
        _state!.Options[ServerOptionOps.AllySafeHits] = "false";
        Answer();
        var row = Row(Content(), ServerOptionOps.AllySafeHits);
        Assert.False(row.GetProperty("requestPending").GetBoolean());
        Assert.Equal("false", row.GetProperty("value").GetString());
        Assert.Equal(ModSettingsView.ServerSavedNote, row.GetProperty("status").GetString());
    }

    [Fact]
    public void A_number_is_sent_on_Apply_and_the_servers_clamp_note_shown()
    {
        OpenPage();
        Assert.True(_runtime.FireText(JoinAdminRows.AnswerTimeoutLabel, "999"));
        View.ApplyForBridge(JoinAdminRows.AnswerTimeoutLabel);
        Assert.Equal(999, _sent.Single().Seconds);
        Assert.Equal(ServerOptionOps.AnswerTimeout, _sent.Single().Op);
        _state!.Options[ServerOptionOps.AnswerTimeout] = "300";
        Answer(note: "Clamped to 300 seconds (0 to 300).");
        var row = Row(Content(), ServerOptionOps.AnswerTimeout);
        Assert.Equal("300", row.GetProperty("value").GetString());
        Assert.Equal("300", row.GetProperty("field").GetString());
        Assert.Equal("Clamped to 300 seconds (0 to 300).", row.GetProperty("status").GetString());

        // Not a number: nothing is sent.
        Assert.True(_runtime.FireText(JoinAdminRows.AnswerTimeoutLabel, "soon"));
        View.ApplyForBridge(JoinAdminRows.AnswerTimeoutLabel);
        Assert.Single(_sent);
    }

    [Fact]
    public void Turning_remote_installs_off_asks_first_and_No_sends_nothing()
    {
        OpenPage();
        Assert.True(_runtime.FireToggle(JoinAdminRows.RemoteInstallLabel, false));
        Assert.Empty(_sent);
        var dialog = UiDialog.Current!;
        Assert.Equal(JoinAdminRows.RemoteOffHeading, dialog.Content.Title);
        Assert.True(Row(Content(), ServerOptionOps.RemoteInstall).GetProperty("confirmOpen").GetBoolean());
        UiDialog.ChooseForBridge("no");
        Assert.Empty(_sent);
        Assert.Equal(ModSettingsView.AdminNotChangedNote, Row(Content(), ServerOptionOps.RemoteInstall).GetProperty("status").GetString());

        Assert.True(_runtime.FireToggle(JoinAdminRows.RemoteInstallLabel, false));
        UiDialog.ChooseForBridge("yes");
        Assert.Equal("false", _sent.Single().Value);
        // Turning it back on asks nothing.
        _state!.Options[ServerOptionOps.RemoteInstall] = "false";
        Answer();
        Assert.True(_runtime.FireToggle(JoinAdminRows.RemoteInstallLabel, true));
        Assert.Equal(2, _sent.Count);
        Assert.Null(UiDialog.Current);
    }

    [Fact]
    public void Removing_yourself_from_the_install_list_asks_first()
    {
        OpenPage();
        Assert.True(_runtime.FireText(JoinAdminRows.AllowedIdsLabel, "76561198000000012"));
        View.ApplyForBridge(JoinAdminRows.AllowedIdsLabel);
        Assert.Empty(_sent);
        Assert.Equal(JoinAdminRows.SelfOffHeading, UiDialog.Current!.Content.Title);
        UiDialog.ChooseForBridge("yes");
        Assert.Equal("76561198000000012", _sent.Single().Value);

        // Adding someone while you stay allowed asks nothing.
        _state!.Options[ServerOptionOps.AllowedSteamIds] = "76561198000000011";
        Answer();
        Assert.True(_runtime.FireText(JoinAdminRows.AllowedIdsLabel, "76561198000000011, 76561198000000012"));
        View.ApplyForBridge(JoinAdminRows.AllowedIdsLabel);
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public void A_list_the_operator_locked_is_read_only_with_the_note_and_never_sent()
    {
        string id = SettingPlaces.JoinAdminId(ServerOptionOps.AllowedSteamIds);
        _state = State(id);
        OpenPage();
        var row = Row(Content(), ServerOptionOps.AllowedSteamIds);
        Assert.True(row.GetProperty("locked").GetBoolean());
        Assert.Equal(id, row.GetProperty("lockedBy").GetString());
        Assert.False(row.GetProperty("enabled").GetBoolean());
        Assert.Equal(ServerSettingsLock.NoteFor(id), row.GetProperty("lockNote").GetString());
        Assert.Contains(ServerSettingsLock.NoteFor(id), row.GetProperty("helpLine").GetString());
        Assert.Equal(ModSettingsRowMarks.LockedChip, _runtime.Chips[JoinAdminRows.AllowedIdsLabel]);
        // The value stays visible.
        Assert.Equal("Any admin", row.GetProperty("display").GetString());
        Assert.True(_runtime.FireText(JoinAdminRows.AllowedIdsLabel, "76561198000000012"));
        View.ApplyForBridge(JoinAdminRows.AllowedIdsLabel);
        Assert.Empty(_sent);
        // The rest of Operator stays editable.
        Assert.True(Row(Content(), ServerOptionOps.RemoteInstall).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void A_group_locks_its_subheading_and_star_locks_every_row()
    {
        _state = State(ServerSettingsLock.GroupOperator);
        OpenPage();
        var rows = Content();
        foreach (var row in AdminRows(rows))
        {
            bool operatorRow = row.GetProperty("section").GetString() == ProgenitorPage.OperatorTitle;
            Assert.Equal(operatorRow, row.GetProperty("locked").GetBoolean());
            if (operatorRow) Assert.Equal("operator", row.GetProperty("lockedBy").GetString());
        }

        Assert.True(_runtime.FireToggle(JoinAdminRows.RemoteInstallLabel, false));
        Assert.Empty(_sent);
        Assert.Null(UiDialog.Current);
        // The lock list applies wherever a row is drawn: Progenitor's page's toggle is not in the operator group.
        OpenProgenitor();
        Assert.True(_runtime.FireToggle(JoinAdminRows.PhotoModeLabel, false));
        Assert.Single(_sent);

        // A lock that arrives with the server's next answer re-draws the page: now every row.
        _state = State("*");
        Assert.True(View.NeedsRebuildForScope());
        OpenPage();
        Assert.All(AdminRows(Content()), row => Assert.Equal("*", row.GetProperty("lockedBy").GetString()));
    }

    [Fact]
    public void A_locked_server_wide_ConfigSync_row_is_read_only_for_an_admin_with_its_value()
    {
        const string mod = "example.lockmod";
        var file = new ConfigFile(Path.Combine(_dir, "lockmod.cfg"), saveOnInit: true);
        var count = file.Bind("General", "Count", 3, "A count.");
        var name = file.Bind("General", "Name", "Night", "A name.");
        SettingsCatalog.UseSyncedProbe(e => ConfigSync.IsRegistered(mod, e));
        ServerSettings.Uninstall();
        ServerSettings.Install();
        _handles.Add(ConfigSync.Register(mod, count));
        _handles.Add(ConfigSync.Register(mod, name));
        _handles.Add(ModSettingsPages.Register(_owner, mod, page => page
            .Section("Server")
            .Number(count, scope: SettingScope.Server)
            .Text(name, scope: SettingScope.Server)));
        ConfigSync.ApplyRecords(new[]
        {
            new ConfigSyncRecord(mod, "General", "Count", "7"),
            new ConfigSyncRecord(mod, "General", "Name", "Night"),
        }, snapshot: true);
        _state = State(mod + "/General/Count");
        if (!ModsMenu.IsOpen) ModsMenu.Open();
        ModsMenu.OpenMod(mod);
        var rows = All(Content(), ModSettingsView.RowSetting);
        var locked = rows.Single(r => r.GetProperty("id").GetString() == mod + "/General/Count");
        Assert.True(locked.GetProperty("locked").GetBoolean());
        Assert.Equal(mod + "/General/Count", locked.GetProperty("lockedBy").GetString());
        Assert.Equal("readonly", locked.GetProperty("control").GetString());
        Assert.Equal("7", locked.GetProperty("value").GetString());
        Assert.Contains(ServerSettingsLock.NoteFor(mod + "/General/Count"), locked.GetProperty("helpLine").GetString());
        var open = rows.Single(r => r.GetProperty("id").GetString() == mod + "/General/Name");
        Assert.False(open.GetProperty("locked").GetBoolean());
        Assert.NotEqual("readonly", open.GetProperty("control").GetString());
    }

    [Fact]
    public void Before_the_servers_state_arrives_the_rows_wait_and_ask_once()
    {
        _state = null;
        OpenProgenitor();
        var row = Row(Content(), ServerOptionOps.PhotoMode);
        Assert.False(row.GetProperty("known").GetBoolean());
        Assert.Equal(ModSettingsView.AdminUnknownValue, row.GetProperty("display").GetString());
        Assert.True(_gets > 0);
        int asks = _gets;
        View.Reevaluate();
        Assert.Equal(asks, _gets);

        // An older server (no options in its state): the option rows go; the policy rows stay.
        _state = new JoinAdminState { PolicySupported = true, Unverified = "allow", MidSession = "warn" };
        OpenProgenitor();
        Assert.DoesNotContain(AdminRows(Content()), s => s.GetProperty("key").GetString() == ServerOptionOps.PhotoMode);
        OpenPage();
        Assert.Contains(AdminRows(Content()), s => s.GetProperty("key").GetString() == JoinAdminOps.Unverified);
    }

    [Fact]
    public void A_request_that_gets_no_answer_puts_the_switch_back()
    {
        OpenProgenitor();
        Assert.True(_runtime.FireToggle(JoinAdminRows.PhotoModeLabel, false));
        _answers.Last()(null);
        var row = Row(Content(), ServerOptionOps.PhotoMode);
        Assert.Equal(JoinAdminRows.NoAnswerError, row.GetProperty("status").GetString());
        Assert.Equal("true", row.GetProperty("value").GetString());
        Assert.False(row.GetProperty("requestPending").GetBoolean());
    }
}
