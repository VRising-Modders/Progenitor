using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Ui;
using Progenitor.WorldState;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// Settings organization stage 6 (docs/settings-organization.md, "Vanilla server settings (Server rules)"; research note
/// server-browser-filter-settings.md §5): the next start's values resolved in the game's order (files, presets, clamps,
/// overrides), the loaded-versus-next state, the read-only lines, the <c>'PrSv'</c> wire and the server's read path. Game-free.
/// </summary>
[Collection("ServerSettingsLock")]
public sealed class ServerRulesTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RemoteInstallActor Admin = new("Admin", 76561198000000001UL, isAdmin: true);
    private static readonly RemoteInstallActor Player = new("Player", 76561198000000002UL, isAdmin: false);

    private readonly string _dir;
    private readonly string _install;
    private readonly string _data;
    private readonly string _save;
    private readonly List<string> _audit = new();
    private double _now = 1000;

    public ServerRulesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-serverrules-" + Guid.NewGuid().ToString("N"));
        _install = Path.Combine(_dir, "server");
        _data = Path.Combine(_dir, "data");
        _save = Path.Combine(_data, "Saves", "v4", "world1");
        Directory.CreateDirectory(Path.Combine(_install, "VRisingServer_Data", "StreamingAssets", "Settings"));
        Directory.CreateDirectory(Path.Combine(_install, "VRisingServer_Data", "StreamingAssets", "GameSettingPresets"));
        Directory.CreateDirectory(Path.Combine(_install, "VRisingServer_Data", "StreamingAssets", "GameDifficultyPresets"));
        Directory.CreateDirectory(Path.Combine(_data, "Settings"));
        Directory.CreateDirectory(_save);
        Plugin.Logger = new TestLogSource();
        ServerRulesServer.Uninstall();
        ServerSettingsLock.Reset();
        ServerRulesServer.Audit = _audit.Add;
        ServerRulesServer.AuditClock = () => T0;
        ServerRulesServer.Now = () => _now;
        Assets("Settings", "ServerGameSettings.json", "{\n  \"GameDifficulty\": \"Normal\",\n  \"GameModeType\": \"PvP\",\n  \"CastleHeartDamageMode\": \"CanBeDestroyedByPlayers\",\n  \"BloodBoundEquipment\": true,\n  \"ClanSize\": 4\n}\n");
        Assets("Settings", "ServerHostSettings.json", "{\n  \"Name\": \"V Rising Server\",\n  \"MaxConnectedUsers\": 40,\n  \"MaxConnectedAdmins\": 4,\n  \"Password\": \"\",\n  \"SaveName\": \"world1\"\n}\n");
    }

    public void Dispose()
    {
        ServerRulesServer.Uninstall();
        ServerSettingsLock.Reset();
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private void Assets(string folder, string name, string text) =>
        File.WriteAllText(Path.Combine(_install, "VRisingServer_Data", "StreamingAssets", folder, name), text);

    private void Local(string name, string text) => File.WriteAllText(Path.Combine(_data, "Settings", name), text);

    private void SaveFile(string name, string text) => File.WriteAllText(Path.Combine(_save, name), text);

    private ServerHostSettingsResolution Host(params string[] args) => ServerHostSettingsFiles.Resolve(new ServerHostSettingsInputs
    {
        Args = args,
        Env = _ => null,
        InstallRoot = _install,
        PersistentDataPath = _data,
    });

    private GameSettingsResolution Game(ServerHostSettingsResolution host) => new(new GameSettingsInputs
    {
        InstallRoot = _install,
        PersistentDataPath = _data,
        SaveFolder = host.SaveFolder,
        Preset = host.EffectiveString(ServerRules.GameSettingsPreset),
        DifficultyPreset = host.EffectiveString(ServerRules.GameDifficultyPreset),
    });

    private ServerRulesState State(Func<string, string?>? loaded = null, IReadOnlyList<string>? tags = null, string? start = null, params string[] args)
    {
        var host = Host(args);
        return ServerRulesStateBuilder.Build(new ServerRulesFacts
        {
            Host = host,
            Game = Game(host),
            Loaded = loaded,
            Tags = tags,
            StartDateText = start,
            NowLocal = new DateTime(2026, 10, 5, 12, 0, 0),
            LockedBy = ServerSettingsLock.LockedBy,
        });
    }

    [Fact]
    public void Game_keys_layer_save_over_local_over_the_install_key_by_key_with_enum_numbers_and_the_clamp()
    {
        Local("ServerGameSettings.json", "{ \"GameModeType\": \"PvE\", \"ClanSize\": 2 }");
        SaveFile("ServerGameSettings.json", "{ \"ClanSize\": 99, \"CastleHeartDamageMode\": 0 }");
        var game = Game(Host());
        Assert.Equal(("PvE", "local"), game.Next(ServerRules.GameModeType));
        // The save folder's file wins key by key; a number names the enum; the game clamps clan size to 1..50.
        Assert.Equal(("50", "save"), game.Next(ServerRules.ClanSize));
        Assert.Equal((ServerRules.Decaying, "save"), game.Next(ServerRules.CastleHeartDamageMode));
        Assert.Equal(("Normal", "default"), game.Next(ServerRules.GameDifficulty));
        Assert.Equal(Path.Combine(_save, GameSettingsResolution.FileName), game.WriteTarget!.Path);

        // Without a save-folder file, changes go to the persistent-data copy; never StreamingAssets.
        File.Delete(Path.Combine(_save, GameSettingsResolution.FileName));
        Assert.Equal(Path.Combine(_data, "Settings", GameSettingsResolution.FileName), Game(Host()).WriteTarget!.Path);
    }

    [Fact]
    public void A_preset_replaces_every_file_layer_and_a_missing_one_leaves_code_defaults_with_a_warning()
    {
        Local("ServerGameSettings.json", "{ \"GameModeType\": \"PvE\", \"ClanSize\": 2 }");
        Assets("GameSettingPresets", "SoloPvP.json", "{ \"ClanSize\": 1 }");
        Local("ServerHostSettings.json", "{ \"GameSettingsPreset\": \"SoloPvP\" }");
        var game = Game(Host());
        Assert.True(game.PresetActive);
        Assert.Equal(("1", "preset"), game.Next(ServerRules.ClanSize));
        // The preset starts from code defaults: the local PvE is gone.
        Assert.Equal(("PvP", "code"), game.Next(ServerRules.GameModeType));
        var state = State();
        var row = state.Row(ServerRules.ClanSize)!;
        Assert.False(row.Editable);
        Assert.Equal("This server uses the SoloPvP preset, which replaces these settings at start.", row.Reason);
        Assert.True(state.Row(ServerRules.MaxConnectedUsers)!.Editable);

        Local("ServerHostSettings.json", "{ \"GameSettingsPreset\": \"SoloPvPP\" }");
        state = State();
        Assert.Contains("SoloPvPP", state.PresetWarning, StringComparison.Ordinal);
        Assert.Contains("built-in defaults", state.PresetWarning, StringComparison.Ordinal);
        Assert.Equal("4", state.Row(ServerRules.ClanSize)!.Next);
        Assert.False(state.Row(ServerRules.ClanSize)!.Editable);

        foreach (string none in new[] { "", "default", "NONE" })
        {
            Local("ServerHostSettings.json", "{ \"GameSettingsPreset\": \"" + none + "\" }");
            Assert.False(Game(Host()).PresetActive);
            Assert.True(State().Row(ServerRules.ClanSize)!.Editable);
        }

        // The command line's preset wins over the file (-p is the preset, -P the password: case matters).
        Local("ServerHostSettings.json", "{ }");
        Assert.Equal("SoloPvP", Host("-p", "SoloPvP").EffectiveString(ServerRules.GameSettingsPreset));
        Assert.Null(Host("-P", "secret").OverrideOf(ServerRules.GameSettingsPreset));
        Assert.Equal("-P", Host("-P", "secret").OverrideOf(ServerRules.Password)!.Value.Source);
    }

    [Fact]
    public void A_difficulty_preset_is_laid_over_and_locks_only_the_rows_its_file_sets()
    {
        Assets("GameDifficultyPresets", "Difficulty_Brutal.json", "{ \"GameDifficulty\": 2, \"SunDamageModifier\": 1.0 }");
        Local("ServerHostSettings.json", "{ \"GameDifficultyPreset\": \"Difficulty_Brutal\" }");
        Local("ServerGameSettings.json", "{ \"GameDifficulty\": \"Relaxed\" }");
        var state = State();
        var difficulty = state.Row(ServerRules.GameDifficulty)!;
        Assert.Equal("Hard", difficulty.Next);
        Assert.Equal("difficulty-preset", difficulty.Source);
        Assert.False(difficulty.Editable);
        Assert.Equal("Set by the difficulty preset Difficulty_Brutal in ServerHostSettings.", difficulty.Reason);
        Assert.True(state.Row(ServerRules.ClanSize)!.Editable);
    }

    [Fact]
    public void Max_players_are_clamped_to_128_less_the_admins_and_an_override_is_named()
    {
        Local("ServerHostSettings.json", "{ \"MaxConnectedUsers\": 200, \"MaxConnectedAdmins\": 8 }");
        var state = State();
        Assert.Equal(8, state.AdminReserve);
        Assert.Equal(120, state.MaxPlayersMax);
        Assert.Equal("120", state.Row(ServerRules.MaxConnectedUsers)!.Next);

        var overridden = State(args: new[] { "-maxUsers", "10" }).Row(ServerRules.MaxConnectedUsers)!;
        Assert.Equal("10", overridden.Next);
        Assert.Equal("-maxUsers", overridden.Override);
        Assert.True(overridden.Editable);
        Assert.StartsWith("The start command sets -maxUsers", overridden.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_state_marks_pending_rules_and_derives_merciless_standard_territory_days_and_the_next_wipe()
    {
        Local("ServerGameSettings.json", "{ \"ClanSize\": 2, \"BloodBoundEquipment\": false, \"CastleHeartDamageMode\": \"CanBeDestroyedOnlyWhenDecaying\" }");
        Local("ServerHostSettings.json", "{ \"ResetDaysInterval\": 7, \"DayOfReset\": \"Friday\", \"Password\": \"hunter2\" }");
        var loaded = new Dictionary<string, string>
        {
            [ServerRules.ClanSize] = "4", [ServerRules.BloodBoundEquipment] = "true", [ServerRules.CastleHeartDamageMode] = ServerRules.Decaying,
            [ServerRules.GameModeType] = "PvP", [ServerRules.GameDifficulty] = "Normal",
        };
        var state = State(k => loaded.TryGetValue(k, out var v) ? v : null, new[] { "ded", "pvp", "sal_mi", "std" }, "09/02/2026");

        Assert.True(state.Row(ServerRules.ClanSize)!.Pending);
        Assert.True(state.Row(ServerRules.BloodBoundEquipment)!.Pending);
        Assert.False(state.Row(ServerRules.GameModeType)!.Pending);
        Assert.Equal(2, state.RestartPending);
        // Merciless now: blood-bound on and a decaying-only heart: no. After the restart: yes.
        Assert.False(state.MercilessNow);
        Assert.True(state.MercilessNext);
        Assert.Equal("Moderate", state.Territory);
        Assert.True(state.StandardNow);
        // A pending game row other than the clan size may change Standard settings.
        Assert.True(state.StandardMayChange);
        Assert.Equal(33, state.DaysRunning);
        Assert.Equal("2026-09-02", state.StartDate);
        // Sept 2 + 7 days = Wed Sept 9, moved forward to Friday.
        Assert.Equal("2026-09-11", state.NextReset);
        // The password is never in the state.
        Assert.Equal("set", state.Row(ServerRules.Password)!.Next);
        Assert.True(state.PasswordNextSet);

        loaded[ServerRules.BloodBoundEquipment] = "false";
        Assert.False(State(k => loaded.TryGetValue(k, out var v) ? v : null, null, null).StandardMayChange);
        Assert.Equal("Abundant", ServerRules.TerritoryWord(ServerRules.ActivityLevelOf(new[] { "pvp" })));
        Assert.Equal("Limited", ServerRules.TerritoryWord(ServerRules.ActivityLevelOf(new[] { "sal_mi", "sal_hi" })));
        Assert.Equal(new DateTime(2026, 9, 9), ServerRules.ResetDate(new DateTime(2026, 9, 2), 7, "Any"));
        Assert.Null(ServerRules.ResetDate(new DateTime(2026, 9, 2), 0, "Any"));
    }

    [Fact]
    public void A_locked_rule_is_read_only_in_the_state()
    {
        ServerSettingsLock.Source = () => "server-rules";
        var state = State();
        Assert.All(state.Rows, r =>
        {
            Assert.False(r.Editable);
            Assert.Equal("server-rules", r.LockedBy);
        });
        ServerSettingsLock.Source = () => "server-list";
        state = State();
        Assert.Equal("server-list", state.Row(ServerRules.Name)!.LockedBy);
        Assert.Equal("", state.Row(ServerRules.ClanSize)!.LockedBy);
    }

    [Fact]
    public void The_wire_round_trips_the_state_and_never_carries_the_password()
    {
        Local("ServerHostSettings.json", "{ \"Password\": \"hunter2\" }");
        var state = State(start: "09/02/2026");
        var bytes = ServerRulesWire.EncodeResponse(new ServerRulesResponse { Nonce = 3, Op = "get", Ok = true, State = state });
        Assert.DoesNotContain("hunter2", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.True(ServerRulesWire.TryDecodeResponse(bytes, out var back));
        Assert.Equal(3, back.Nonce);
        Assert.True(back.State!.PasswordNextSet);
        Assert.Equal(state.Rows.Count, back.State.Rows.Count);
        Assert.Equal(state.Row(ServerRules.MaxConnectedUsers)!.Next, back.State.Row(ServerRules.MaxConnectedUsers)!.Next);
        Assert.Equal("2026-09-02", back.State.StartDate);

        var request = new ServerRulesRequest { Op = ServerRulesRequest.OpWrite, Key = ServerRules.ClanSize, Value = "3", Expected = "4", HasExpected = true, Nonce = 9 };
        Assert.True(ServerRulesWire.TryDecodeRequest(ServerRulesWire.EncodeRequest(request), out var r));
        Assert.Equal(("write", "ClanSize", "3", "4", true), (r.Op, r.Key, r.Value, r.Expected, r.HasExpected));
        // An answer never reads as a request.
        Assert.False(ServerRulesWire.TryDecodeRequest(bytes, out _));
        Assert.True(ServerRulesWire.TryDecodeChanged(ServerRulesWire.EncodeChanged(state), out var pushed));
        Assert.NotNull(pushed);
    }

    // ---- stage 7: writes ------------------------------------------------------------------------

    private readonly List<string> _livePasswords = new();

    private void ServeFiles(Func<string, string?>? loaded = null)
    {
        ServerRulesServer.FactsProvider = () =>
        {
            var host = Host();
            return new ServerRulesFacts { Host = host, Game = Game(host), Loaded = loaded };
        };
        ServerRulesServer.SetLivePassword = _livePasswords.Add;
    }

    private ServerRulesResponse Write(string key, string value, string? expected = null, RemoteInstallActor? actor = null, bool confirm = false)
    {
        _now += 7;
        var request = new ServerRulesRequest { Op = ServerRulesRequest.OpWrite, Key = key, Value = value, Confirm = confirm };
        if (expected != null)
        {
            request.Expected = expected;
            request.HasExpected = true;
        }

        return ServerRulesServer.Apply(request, handshakeOk: true, actor ?? Admin);
    }

    [Fact]
    public void A_game_rule_is_written_into_the_highest_existing_file_byte_for_byte_and_waits_for_the_restart()
    {
        const string original = "﻿{\n  // a comment the operator wrote\n  \"ClanSize\": 4,\n  \"GameModeType\": \"PvP\"\n}\n";
        File.WriteAllBytes(Path.Combine(_save, GameSettingsResolution.FileName), Encoding.UTF8.GetBytes(original));
        ServeFiles(k => k == ServerRules.ClanSize ? "4" : null);
        var r = Write(ServerRules.ClanSize, "3", expected: "4");
        Assert.True(r.Ok, r.Error);
        Assert.Equal("Saved: applies at the next restart.", r.Note);
        // Every other byte is kept: the byte-order mark, the comment, the order and the spacing.
        byte[] after = File.ReadAllBytes(Path.Combine(_save, GameSettingsResolution.FileName));
        Assert.Equal(Encoding.UTF8.GetBytes(original.Replace("\"ClanSize\": 4", "\"ClanSize\": 3")), after);
        Assert.False(File.Exists(Path.Combine(_data, "Settings", GameSettingsResolution.FileName)));
        var row = r.State!.Row(ServerRules.ClanSize)!;
        Assert.Equal("3", row.Next);
        Assert.True(row.Pending);
        Assert.Contains("progenitor/ServerRules/ClanSize", _audit.Last(), StringComparison.Ordinal);

        // The same value again writes nothing.
        var mtime = File.GetLastWriteTimeUtc(Path.Combine(_save, GameSettingsResolution.FileName));
        Assert.Equal("Unchanged.", Write(ServerRules.ClanSize, "3", expected: "3").Note);
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(Path.Combine(_save, GameSettingsResolution.FileName)));

        // Without a save-folder file, the persistent-data copy is created with only that key.
        File.Delete(Path.Combine(_save, GameSettingsResolution.FileName));
        Assert.True(Write(ServerRules.GameModeType, "PvE").Ok);
        Assert.Contains("\"GameModeType\": \"PvE\"", File.ReadAllText(Path.Combine(_data, "Settings", GameSettingsResolution.FileName)), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_save, GameSettingsResolution.FileName)));
    }

    [Fact]
    public void Writes_are_checked_for_bounds_choices_conflicts_presets_locks_and_the_admin()
    {
        ServeFiles();
        Assert.Equal("Give a whole number from 1 to 10.", Write(ServerRules.ClanSize, "11").Error);
        // Max players: 1..(128 - 4 admins); never above the clamp.
        Assert.Equal("Give a whole number from 1 to 124.", Write(ServerRules.MaxConnectedUsers, "125").Error);
        Assert.True(Write(ServerRules.MaxConnectedUsers, "124").Ok);
        Assert.StartsWith("Choose one of:", Write(ServerRules.GameDifficulty, "Brutal").Error, StringComparison.Ordinal);
        Assert.True(Write(ServerRules.GameDifficulty, "hard").Ok);
        Assert.Equal(ServerRulesDecisions.ConflictError, Write(ServerRules.ClanSize, "2", expected: "7").Error);
        Assert.Equal(ServerRulesServer.NotAdminError, Write(ServerRules.ClanSize, "2", actor: Player).Error);

        Local("ServerHostSettings.json", "{ \"GameSettingsPreset\": \"StandardPvP\", \"MaxConnectedUsers\": 40 }");
        Assets("GameSettingPresets", "StandardPvP.json", "{ \"ClanSize\": 4 }");
        Assert.Equal("This server uses the StandardPvP preset, which replaces these settings at start.", Write(ServerRules.ClanSize, "2").Error);

        ServerSettingsLock.Source = () => "server-rules";
        var locked = Write(ServerRules.MaxConnectedUsers, "20");
        // Review fix 7: the refusal and its audit line name the lock list's entry.
        Assert.Equal("Locked in the server's config file (ServerSettings LockedInGame: server-rules).", locked.Error);
        Assert.Contains("refused", _audit.Last(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MaxConnectedUsers", _audit.Last(), StringComparison.Ordinal);
        Assert.Contains("(ServerSettings LockedInGame: server-rules)", _audit.Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_password_is_capped_applied_live_cleared_by_blank_and_never_echoed()
    {
        ServeFiles();
        var tooLong = Write(ServerRules.Password, new string('x', 33));
        Assert.Equal(ServerRulesDecisions.PasswordTooLong, tooLong.Error);
        Assert.Empty(_livePasswords);

        var set = Write(ServerRules.Password, "hunter2");
        Assert.True(set.Ok, set.Error);
        Assert.Equal(new[] { "hunter2" }, _livePasswords);
        Assert.Contains("\"Password\": \"hunter2\"", File.ReadAllText(Path.Combine(_data, "Settings", "ServerHostSettings.json")), StringComparison.Ordinal);
        Assert.True(set.State!.PasswordNextSet);
        Assert.DoesNotContain("hunter2", Encoding.UTF8.GetString(ServerRulesWire.EncodeResponse(set)), StringComparison.Ordinal);
        Assert.All(_audit, line => Assert.DoesNotContain("hunter2", line, StringComparison.Ordinal));
        Assert.Contains("<hidden>", _audit.Last(), StringComparison.Ordinal);

        // Only spaces is no password: it is cleared, live and in the file.
        var cleared = Write(ServerRules.Password, "   ");
        Assert.True(cleared.Ok, cleared.Error);
        Assert.Equal("", _livePasswords.Last());
        Assert.Contains("\"Password\": \"\"", File.ReadAllText(Path.Combine(_data, "Settings", "ServerHostSettings.json")), StringComparison.Ordinal);
        Assert.False(cleared.State!.PasswordNextSet);
    }

    [Fact]
    public void The_server_name_is_one_line_for_the_restart_and_scheduled_wipes_need_a_confirmation()
    {
        ServeFiles();
        var r = Write(ServerRules.Name, "Night\nGarden\t ");
        Assert.True(r.Ok, r.Error);
        // Review fix 4: the name is not changed in the running server (its discovery data is a native struct); it waits.
        Assert.Equal("Saved: applies at the next restart.", r.Note);
        Assert.False(ServerRules.Find(ServerRules.Name)!.Live);
        Assert.Contains("\"Name\": \"NightGarden\"", File.ReadAllText(Path.Combine(_data, "Settings", "ServerHostSettings.json")), StringComparison.Ordinal);
        Assert.Equal("At most 64 characters.", Write(ServerRules.Name, new string('n', 65)).Error);
        Assert.Equal("Give the server a name.", Write(ServerRules.Name, " \n ").Error);
        // Review fix 3: a rename that only changes capitals is written.
        var caps = Write(ServerRules.Name, "nightgarden");
        Assert.True(caps.Ok, caps.Error);
        Assert.NotEqual("Unchanged.", caps.Note);
        Assert.Contains("\"Name\": \"nightgarden\"", File.ReadAllText(Path.Combine(_data, "Settings", "ServerHostSettings.json")), StringComparison.Ordinal);

        // Review fix (wipes): turning them on needs confirm: true in the request; without it nothing is written.
        var unconfirmed = Write(ServerRules.ResetDaysInterval, "7");
        Assert.Equal(ServerRulesDecisions.WipeConfirmError, unconfirmed.Error);
        Assert.DoesNotContain("ResetDaysInterval", File.ReadAllText(Path.Combine(_data, "Settings", "ServerHostSettings.json")), StringComparison.Ordinal);
        Assert.Contains("refused", _audit.Last(), StringComparison.OrdinalIgnoreCase);
        var wipes = Write(ServerRules.ResetDaysInterval, "7", confirm: true);
        Assert.True(wipes.Ok, wipes.Error);
        Assert.Contains("may reset this world", wipes.Note, StringComparison.Ordinal);
        // The start date is unknown here, so every later change of the schedule asks again.
        Assert.Equal(ServerRulesDecisions.WipeConfirmError, Write(ServerRules.DayOfReset, "friday").Error);
        Assert.True(Write(ServerRules.DayOfReset, "friday", confirm: true).Ok);
        Assert.Contains("\"DayOfReset\": \"Friday\"", File.ReadAllText(Path.Combine(_data, "Settings", "ServerHostSettings.json")), StringComparison.Ordinal);

        // An overridden rule is written all the same, and the note says the override wins at start.
        var host = Host("-maxUsers", "10");
        ServerRulesServer.FactsProvider = () => new ServerRulesFacts { Host = host, Game = Game(host) };
        var overridden = Write(ServerRules.MaxConnectedUsers, "30");
        Assert.True(overridden.Ok, overridden.Error);
        Assert.Contains("-maxUsers", overridden.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_json_edit_keeps_every_other_byte_and_adds_a_missing_key_after_the_brace()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\n  \"A\": 1, /* keep */\n  \"B\": { \"C\": [1, 2] },\n  \"D\": \"x\"\n}");
        string replaced = Encoding.UTF8.GetString(JsonFileEdit.WithValue(json, "b", "7")!);
        Assert.Equal("{\n  \"A\": 1, /* keep */\n  \"B\": 7,\n  \"D\": \"x\"\n}", replaced);
        string added = Encoding.UTF8.GetString(JsonFileEdit.WithValue(json, "E", "true")!);
        Assert.StartsWith("{\n  \"E\": true,\n  \"A\": 1", added, StringComparison.Ordinal);
        Assert.Null(JsonFileEdit.WithValue(Encoding.UTF8.GetBytes("[1]"), "A", "1"));
    }

    [Fact]
    public void Any_player_who_passed_the_handshake_reads_the_rules_and_refusals_are_audited()
    {
        ServerRulesServer.FactsProvider = () =>
        {
            var host = Host();
            return new ServerRulesFacts { Host = host, Game = Game(host) };
        };
        var read = ServerRulesServer.Apply(new ServerRulesRequest { Op = ServerRulesRequest.OpGet }, handshakeOk: true, Player);
        Assert.True(read.Ok);
        Assert.NotNull(read.State);
        Assert.Empty(_audit);

        _now += 1;
        var early = ServerRulesServer.Apply(new ServerRulesRequest { Op = ServerRulesRequest.OpGet }, handshakeOk: false, Player);
        Assert.False(early.Ok);
        Assert.Equal(ServerRulesServer.HandshakeError, early.Error);
        Assert.Null(early.State);
        Assert.Single(_audit);
        Assert.Equal(CustomEventPolicy.ServerRulesEventId, 0x50725376);
        Assert.True(CustomEventPolicy.IsReserved(CustomEventPolicy.ServerRulesEventId));

        // Reads spend their own budget.
        int refused = 0;
        for (int i = 0; i < 40; i++)
            if (ServerRulesServer.Apply(new ServerRulesRequest { Op = ServerRulesRequest.OpGet }, true, Player).Error == ServerSettings.RateLimitedError) refused++;
        Assert.True(refused >= 9);
    }

    // ---- review fixes (2026-10-05) ---------------------------------------------------------------

    [Fact]
    public void A_wipe_change_needs_a_confirmation_when_it_turns_wipes_on_or_brings_the_reset_to_now()
    {
        var start = new DateTime(2026, 9, 2);
        var now = new DateTime(2026, 10, 5, 12, 0, 0);
        // Off stays off; turning on always asks.
        Assert.False(ServerRules.WipeNeedsConfirm(start, 0, 0, "Any", now, out _, out _));
        Assert.True(ServerRules.WipeNeedsConfirm(start, 0, 60, "Any", now, out var reset, out bool past));
        Assert.Equal(new DateTime(2026, 11, 1), reset);
        Assert.False(past);
        // Already on: a change that keeps the date ahead asks nothing; one that brings it to now or earlier asks.
        Assert.False(ServerRules.WipeNeedsConfirm(start, 60, 45, "Any", now, out _, out _));
        Assert.True(ServerRules.WipeNeedsConfirm(start, 60, 7, "Any", now, out reset, out past));
        Assert.Equal(new DateTime(2026, 9, 9), reset);
        Assert.True(past);
        Assert.False(ServerRules.WipeNeedsConfirm(start, 60, 60, "Friday", now, out _, out _));
        // An unknown start date always asks.
        Assert.True(ServerRules.WipeNeedsConfirm(null, 60, 61, "Any", now, out _, out _));

        // The server enforces it with its own clock and the world's start date.
        Local("ServerHostSettings.json", "{ \"ResetDaysInterval\": 60 }");
        ServerRulesServer.FactsProvider = () =>
        {
            var host = Host();
            return new ServerRulesFacts { Host = host, Game = Game(host), StartDateText = "09/02/2026", NowLocal = now };
        };
        Assert.True(Write(ServerRules.ResetDaysInterval, "45").Ok);
        var lowered = Write(ServerRules.ResetDaysInterval, "7");
        Assert.Equal(ServerRulesDecisions.WipeConfirmError, lowered.Error);
        Assert.Contains("\"ResetDaysInterval\": 45", File.ReadAllText(Path.Combine(_data, "Settings", "ServerHostSettings.json")), StringComparison.Ordinal);
        Assert.True(Write(ServerRules.ResetDaysInterval, "7", confirm: true).Ok);
        // Turning them off never asks.
        Assert.True(Write(ServerRules.ResetDaysInterval, "0").Ok);

        // The wire carries the confirmation.
        var request = new ServerRulesRequest { Op = ServerRulesRequest.OpWrite, Key = ServerRules.ResetDaysInterval, Value = "7", Confirm = true };
        Assert.True(ServerRulesWire.TryDecodeRequest(ServerRulesWire.EncodeRequest(request), out var back));
        Assert.True(back.Confirm);
        request.Confirm = false;
        Assert.True(ServerRulesWire.TryDecodeRequest(ServerRulesWire.EncodeRequest(request), out back));
        Assert.False(back.Confirm);
    }

    [Fact]
    public void Max_players_above_the_clamp_in_the_file_can_be_written_down_to_the_clamp()
    {
        // The file says 200; the game clamps to 124 (128 less 4 admins), which is what the row shows.
        Local("ServerHostSettings.json", "{ \"MaxConnectedUsers\": 200 }");
        ServeFiles();
        var row = State().Row(ServerRules.MaxConnectedUsers)!;
        Assert.Equal("124", row.Next);
        Assert.Equal("200", row.Raw);
        var r = Write(ServerRules.MaxConnectedUsers, "124");
        Assert.True(r.Ok, r.Error);
        Assert.NotEqual("Unchanged.", r.Note);
        Assert.Contains("\"MaxConnectedUsers\": 124", File.ReadAllText(Path.Combine(_data, "Settings", "ServerHostSettings.json")), StringComparison.Ordinal);
        Assert.Equal("Unchanged.", Write(ServerRules.MaxConnectedUsers, "124").Note);
    }

    [Fact]
    public void The_same_password_is_written_again_so_an_answer_never_confirms_a_guess()
    {
        ServeFiles();
        Assert.True(Write(ServerRules.Password, "hunter2").Ok);
        var again = Write(ServerRules.Password, "hunter2");
        Assert.True(again.Ok, again.Error);
        Assert.Equal("Applied now, to the next player who joins.", again.Note);
        Assert.Equal(new[] { "hunter2", "hunter2" }, _livePasswords);
    }

    [Fact]
    public void A_named_preset_applies_only_when_its_folder_exists_and_is_never_called_missing_without_the_install()
    {
        Local("ServerHostSettings.json", "{ \"GameSettingsPreset\": \"SoloPvP\" }");
        Directory.Delete(Path.Combine(_install, "VRisingServer_Data", "StreamingAssets", "GameSettingPresets"), recursive: true);
        // No GameSettingPresets folder: the game loads no preset, so the file layers stand and the rows are editable.
        var game = Game(Host());
        Assert.False(game.PresetActive);
        Assert.False(game.PresetMissing);
        Assert.True(State().Row(ServerRules.ClanSize)!.Editable);
        Assert.Equal("", State().PresetWarning);

        // The install is unknown: the preset may apply, so the rows stay read-only, but nothing says it is missing.
        var unknown = new GameSettingsResolution(new GameSettingsInputs
        {
            InstallRoot = "",
            PersistentDataPath = _data,
            SaveFolder = Host().SaveFolder,
            Preset = "SoloPvP",
        });
        Assert.True(unknown.PresetActive);
        Assert.False(unknown.PresetMissing);
    }

    [Fact]
    public void A_duplicate_key_is_read_and_written_where_the_game_reads_it_the_last_one()
    {
        byte[] json = Encoding.UTF8.GetBytes("{\n  \"ClanSize\": 2,\n  \"ClanSize\": 5\n}");
        Assert.Equal("5", JsonFileEdit.CurrentLiteral(json, "ClanSize"));
        Assert.Equal("{\n  \"ClanSize\": 2,\n  \"ClanSize\": 3\n}", Encoding.UTF8.GetString(JsonFileEdit.WithValue(json, "ClanSize", "3")!));
        Local("ServerGameSettings.json", "{ \"ClanSize\": 2, \"ClanSize\": 5 }");
        Assert.Equal("5", State().Row(ServerRules.ClanSize)!.Next);
    }

    [Fact]
    public void Before_the_lock_list_is_bound_every_edit_path_refuses_and_reads_stay_open()
    {
        // Review fix 5: the server binds the lock list on its own; until it has, it fails closed.
        ServerSettingsLock.RequireBound = true;
        ServerSettingsLock.Source = null;
        ServeFiles();
        var refused = Write(ServerRules.ClanSize, "3");
        Assert.Equal(ServerSettingsLock.NotBoundError, refused.Error);
        Assert.Equal(ServerSettingsLock.NotBoundEntry, ServerSettingsLock.LockedBy(SettingPlaces.JoinAdminId(ServerOptionOps.PhotoMode)));
        Assert.True(ServerRulesServer.Apply(new ServerRulesRequest { Op = ServerRulesRequest.OpGet }, handshakeOk: true, Admin).Ok);
        ServerSettingsLock.Source = () => "";
        Assert.True(Write(ServerRules.ClanSize, "3").Ok);
        ServerSettingsLock.Reset();
        Assert.False(ServerSettingsLock.RequireBound);
    }

    [Fact]
    public void An_inline_field_and_its_Apply_share_the_control_column_without_overlap()
    {
        foreach (float width in new[] { 700f, 900f, 1236f })
        {
            var grid = ModSettingsRowGrid.For(width);
            var s = grid.InlineSlots(1, 0);
            Assert.Equal(grid.ControlLeft, s.FieldLeft, 2);
            Assert.True(s.FieldWidth >= ModSettingsRowGrid.MinInlineFieldWidth);
            Assert.True(s.FieldLeft + s.FieldWidth < s.ButtonLeft);
            Assert.True(s.ButtonWidth > 0f);
            // Live 2026-10-06: the field was too narrow. The buttons reach into the gutter, short of the reset arrow, so the
            // field keeps the whole bar less at most one button.
            Assert.True(s.ButtonLeft + s.ButtonWidth <= grid.InlineRight + 0.01f);
            Assert.True(grid.InlineRight >= grid.BarRight);
            Assert.True(grid.InlineRight < grid.ResetCenterX);
            Assert.True(s.FieldWidth >= grid.ControlWidth - s.ButtonWidth - ModSettingsRowGrid.InlineGap - 0.01f);
        }
    }

    [Theory]
    [InlineData(700f)]
    [InlineData(1236f)]
    public void A_read_only_rows_chip_never_overlaps_its_value_text(float width)
    {
        // Live 2026-10-06: on the non-admin Server rules rows the Server chip covered the start of "Admin only · PvP".
        var grid = ModSettingsRowGrid.For(width);
        var slot = grid.ControlSlot(SettingsControlShape.Bar);
        float controlRight = grid.FromCenter(slot.Left + slot.Width);
        foreach (string text in new[] { "Admin only · …", "Admin only · PvP", "Admin only · Players can destroy it", "Admin only · Progenitor Forms Test", "Admin only · Not set" })
        foreach (string chipText in new[] { ModSettingsRowMarks.ServerChip, ModSettingsRowMarks.RestartChip, ModSettingsRowMarks.LockedChip })
        {
            float textW = CellLayout.EstimateTextWidth(text, 18f);
            float valueLeft = ModSettingsRowGrid.ReadOnlyValueLeft(controlRight, slot.Width, textW);
            float chipW = ModSettingsRowGrid.ChipWidth(CellLayout.EstimateTextWidth(chipText, 13f));
            float chipCenter = ModSettingsRowGrid.ChipCenterX(valueLeft, chipW);
            float chipRight = chipCenter + chipW * 0.5f;
            // The chip's rect ends a gap before the value's text starts; the text ends at the control's right edge.
            Assert.True(chipRight <= valueLeft - ModSettingsRowGrid.ChipGap + 0.01f, text + " / " + chipText);
            Assert.True(valueLeft >= controlRight - slot.Width - 0.01f);
            Assert.True(valueLeft <= controlRight);
        }
    }

    [Fact]
    public void Every_settings_grid_field_draws_its_name_inline_or_not()
    {
        // Live 2026-10-06 (blocker): fields with Apply at their right lost their names.
        Assert.True(ModSettingsRowGrid.InputDrawsCaption(searchField: false, inline: true, grid: true));
        Assert.True(ModSettingsRowGrid.InputDrawsCaption(searchField: false, inline: false, grid: true));
        Assert.True(ModSettingsRowGrid.InputDrawsCaption(searchField: false, inline: false, grid: false));
        // The Configuration folder path (inline, not a grid row) and a search field carry no caption.
        Assert.False(ModSettingsRowGrid.InputDrawsCaption(searchField: false, inline: true, grid: false));
        Assert.False(ModSettingsRowGrid.InputDrawsCaption(searchField: true, inline: false, grid: true));
    }

    [Fact]
    public void A_write_compares_with_the_files_value_not_an_override_and_unchanged_logs_nothing()
    {
        // Live 2026-10-06: Name overridden by -serverName "Progenitor Forms Test"; writing it back after a lowercase rename
        // answered "Unchanged." (compared with the override), logged an accepted line and left the lowercase name in the file.
        Local("ServerHostSettings.json", "{ \"Name\": \"Progenitor Forms Test\" }");
        ServerRulesServer.FactsProvider = () =>
        {
            var host = Host("-serverName", "Progenitor Forms Test");
            return new ServerRulesFacts { Host = host, Game = Game(host) };
        };
        string file = Path.Combine(_data, "Settings", "ServerHostSettings.json");
        var lower = Write(ServerRules.Name, "progenitor forms test");
        Assert.True(lower.Ok, lower.Error);
        Assert.Contains("\"Name\": \"progenitor forms test\"", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Contains("\"Progenitor Forms Test\" -> \"progenitor forms test\"", _audit.Last(), StringComparison.Ordinal);

        var back = Write(ServerRules.Name, "Progenitor Forms Test");
        Assert.True(back.Ok, back.Error);
        Assert.NotEqual("Unchanged.", back.Note);
        Assert.Contains("\"Name\": \"Progenitor Forms Test\"", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Contains("\"progenitor forms test\" -> \"Progenitor Forms Test\"", _audit.Last(), StringComparison.Ordinal);

        // The same value again: nothing written and no accepted audit line.
        int lines = _audit.Count;
        Assert.Equal("Unchanged.", Write(ServerRules.Name, "Progenitor Forms Test").Note);
        Assert.Equal(lines, _audit.Count);

        // Numbers and choices compare with the file too.
        Local("ServerHostSettings.json", "{ \"MaxConnectedUsers\": 30, \"DayOfReset\": \"Friday\" }");
        ServerRulesServer.FactsProvider = () =>
        {
            var host = Host("-maxUsers", "10");
            return new ServerRulesFacts { Host = host, Game = Game(host) };
        };
        var users = Write(ServerRules.MaxConnectedUsers, "10");
        Assert.True(users.Ok, users.Error);
        Assert.NotEqual("Unchanged.", users.Note);
        Assert.Contains("\"MaxConnectedUsers\": 10", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Equal("Unchanged.", Write(ServerRules.DayOfReset, "friday").Note);
        // WIPEDAY1: no scheduled wipe here, so a new day waits for one (no confirmation needed) and the note says so.
        var day = Write(ServerRules.DayOfReset, "saturday");
        Assert.True(day.Ok, day.Error);
        Assert.Equal("Saved: Wipe on Saturday · takes effect once a wipe is scheduled.", day.Note);
    }

    [Fact]
    public void A_refused_wipe_change_writes_a_refusal_audit_line()
    {
        ServeFiles();
        var refused = Write(ServerRules.ResetDaysInterval, "7");
        Assert.Equal(ServerRulesDecisions.WipeConfirmError, refused.Error);
        var line = _audit.Last();
        Assert.Contains("settings refused", line, StringComparison.Ordinal);
        Assert.Contains("progenitor/ServerRules/ResetDaysInterval", line, StringComparison.Ordinal);
        Assert.Contains("\"0\" -> \"7\"", line, StringComparison.Ordinal);
        Assert.EndsWith(": " + ServerRulesDecisions.WipeConfirmError, line, StringComparison.Ordinal);
    }

    [Fact]
    public void An_admin_rows_chip_reads_Locked_or_Restart()
    {
        var spec = new AdminSettingSpec { LockedBy = () => "operator", Pending = () => true };
        Assert.Equal(ModSettingsRowMarks.LockedChip, ModSettingsRowMarks.AdminChipText(spec));
        spec = new AdminSettingSpec { LockedBy = () => null, Pending = () => true };
        Assert.Equal(ModSettingsRowMarks.RestartChip, ModSettingsRowMarks.AdminChipText(spec));
        Assert.Equal("", ModSettingsRowMarks.AdminChipText(new AdminSettingSpec()));
    }
}
