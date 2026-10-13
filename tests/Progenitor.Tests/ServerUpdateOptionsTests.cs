using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// AUTOUPDATE3, the Server tab's Updates group on the admin channel (<c>'PrJa'</c>): the <c>[AutoUpdate]</c> options with
/// their authorization, lock group (<c>updates</c>) and audit line; Restart when empty refused while nothing restarts the
/// server; Check now, Restart now and Cancel restart as audited actions; the update status in the admins' state and the
/// notice packet. Game-free.
/// </summary>
[Collection("ServerSettingsLock")]
public sealed class ServerUpdateOptionsTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RemoteInstallActor Admin = new("Admin", 76561198000000001UL, isAdmin: true);
    private static readonly RemoteInstallActor Player = new("Player", 76561198000000002UL, isAdmin: false);

    private readonly string _dir;
    private readonly List<string> _audit = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly List<(string Op, string Value)> _stored = new();
    private string _lockText = "";
    private string _host = "";
    private double _now = 1000;

    public ServerUpdateOptionsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-updateoptions-" + Guid.NewGuid().ToString("N"));
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
        _values[ServerOptionOps.UpdateMods] = "Notify";
        _values[ServerOptionOps.UpdateMajors] = "Notify";
        _values[ServerOptionOps.UpdateRestartWhenEmpty] = "false";
        _values[ServerOptionOps.UpdateEmptyMinutes] = "5";
        _values[ServerOptionOps.UpdateBackup] = "false";
        _values[ServerOptionOps.UpdateBackupKeep] = "2";
        _values[ServerOptionOps.UpdateBackupSkipHours] = "6";
        ServerOptions.Get = op => _values.TryGetValue(op, out var v) ? v : null;
        ServerOptions.Set = (op, v) =>
        {
            _stored.Add((op, v));
            _values[op] = v;
            return true;
        };
        ServerAutoUpdate.Reset();
        ServerAutoUpdate.SettingsProbe = () => new ServerUpdateSettings { Bound = true, HostRestarts = _host };
        ServerSettingsLock.Reset();
        ServerSettingsLock.Source = () => _lockText;
        ServerSettingsLock.LegacyAllowedIdsLocked = () => false;
    }

    public void Dispose()
    {
        JoinAdminServer.Uninstall();
        ServerAutoUpdate.Reset();
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

    [Fact]
    public void Every_update_option_is_an_admin_channel_edit_placed_in_the_updates_group()
    {
        foreach (string op in ServerOptionOps.UpdateOps)
        {
            Assert.Contains(op, ServerOptionOps.All);
            Assert.Contains(op, JoinAdminOps.All);
            var place = SettingPlaces.Find(SettingPlaces.JoinAdminId(op))!;
            Assert.Equal(SettingPlaces.UpdatesServer, place.Home);
            Assert.Contains(ServerSettingsLock.GroupUpdates, ServerSettingsLock.GroupsOf(SettingPlaces.JoinAdminId(op)));
            Assert.Equal(ServerUpdateSettings.Section, ServerOptions.Find(op)!.Section);
        }

        Assert.Equal(SettingPlaces.UpdatesServer, SettingPlaces.Find(SettingPlaces.JoinAdminId(JoinAdminOps.UpdateRestart))!.Home);
        Assert.Equal(SettingPlaces.CfgOnly, SettingPlaces.Find(SettingPlaces.Id("AutoUpdate", "HostRestarts"))!.Home);
        Assert.Equal(SettingPlaces.CfgOnly, SettingPlaces.Find(SettingPlaces.Id("AutoUpdate", "CheckHours"))!.Home);
        Assert.Contains("updates", ServerSettingsLock.Description);
        Assert.Equal(ServerOptionOps.UpdateOps, ServerUpdatesRows.Settings().Select(s => s.Key));
    }

    [Fact]
    public void The_mode_and_majors_are_choices_and_the_numbers_clamp_in_their_own_unit()
    {
        var r = Apply(new JoinAdminRequest { Op = ServerOptionOps.UpdateMods, Value = "automatic" });
        Assert.True(r.Ok, r.Error);
        Assert.Equal((ServerOptionOps.UpdateMods, "Automatic"), _stored.Last());
        Assert.Contains(_audit, l => l.Contains("progenitor/JoinAdmin/auto-update-mods \"Notify\" -> \"Automatic\""));
        Assert.False(Apply(new JoinAdminRequest { Op = ServerOptionOps.UpdateMods, Value = "sometimes" }).Ok);
        Assert.False(Apply(new JoinAdminRequest { Op = ServerOptionOps.UpdateMajors, Value = "Off" }).Ok);

        r = Apply(new JoinAdminRequest { Op = ServerOptionOps.UpdateEmptyMinutes, Seconds = 500 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal("Clamped to 120 minutes (1 to 120).", r.Note);
        r = Apply(new JoinAdminRequest { Op = ServerOptionOps.UpdateBackupKeep, HasSeconds = false });
        Assert.Equal("Give whole backups, 1 to 20.", r.Error);
        r = Apply(new JoinAdminRequest { Op = ServerOptionOps.UpdateBackupSkipHours, Seconds = 0 });
        Assert.True(r.Ok, r.Error);
        Assert.Equal("0", _values[ServerOptionOps.UpdateBackupSkipHours]);
        // The state carries every bound option for the page.
        Assert.Equal("120", r.State!.Options[ServerOptionOps.UpdateEmptyMinutes]);
    }

    [Fact]
    public void Restart_when_empty_is_refused_while_nothing_restarts_the_server()
    {
        var r = Apply(new JoinAdminRequest { Op = ServerOptionOps.UpdateRestartWhenEmpty, Value = "true" });
        Assert.False(r.Ok);
        Assert.Equal(ServerUpdateSettings.NoHostRestartsText, r.Error);
        Assert.Empty(_stored);
        Assert.Contains(_audit, l => l.Contains("refused") && l.Contains("auto-update-restart-when-empty"));
        // Turning it off is always allowed.
        Assert.True(Apply(new JoinAdminRequest { Op = ServerOptionOps.UpdateRestartWhenEmpty, Value = "false" }).Ok);
        _host = "NSSM service";
        r = Apply(new JoinAdminRequest { Op = ServerOptionOps.UpdateRestartWhenEmpty, Value = "on" });
        Assert.True(r.Ok, r.Error);
        Assert.Equal("true", _values[ServerOptionOps.UpdateRestartWhenEmpty]);
    }

    [Fact]
    public void Check_now_restart_now_and_cancel_are_audited_admin_actions_the_lock_list_can_stop()
    {
        var seen = new List<(string, ulong)>();
        JoinAdminServer.UpdateAction = (op, actor) =>
        {
            seen.Add((op, actor.SteamId));
            return op == JoinAdminOps.UpdateRestart ? (false, "No update waits for a restart.", "") : (true, "", "Checking for updates now.");
        };
        JoinAdminServer.UpdatesState = () =>
        {
            var u = new JoinAdminUpdates { Line = "Checked 12:00 · 1 waiting for a restart", Countdown = -1, ComesBack = true, HostRestarts = "panel" };
            u.RestartPending.Add("Team-Maps 2.0.0");
            return u;
        };

        var r = Apply(new JoinAdminRequest { Op = JoinAdminOps.UpdateCheck });
        Assert.True(r.Ok, r.Error);
        Assert.Equal("Checking for updates now.", r.Note);
        Assert.Equal("Checked 12:00 · 1 waiting for a restart", r.State!.Updates!.Line);
        Assert.Contains(_audit, l => l.Contains("accepted") && l.Contains("progenitor/JoinAdmin/auto-update-check"));
        r = Apply(new JoinAdminRequest { Op = JoinAdminOps.UpdateRestart });
        Assert.False(r.Ok);
        Assert.Equal("No update waits for a restart.", r.Error);
        Assert.Contains(_audit, l => l.Contains("refused") && l.Contains("auto-update-restart"));

        // A player who is not an admin is refused before the auto-updater hears of it.
        Assert.Equal(JoinAdminPolicy.NotAdminError, Apply(new JoinAdminRequest { Op = JoinAdminOps.UpdateRestart }, Player).Error);
        // The operator's lock list: the updates group locks Restart now and the settings.
        _lockText = "updates";
        r = Apply(new JoinAdminRequest { Op = JoinAdminOps.UpdateRestart });
        Assert.False(r.Ok);
        Assert.StartsWith(ServerSettingsLock.Note.TrimEnd('.'), r.Error);
        Assert.False(Apply(new JoinAdminRequest { Op = ServerOptionOps.UpdateBackup, Value = "true" }).Ok);
        // Check now is in the group too (review 2026-10-09: it was not placed, so the lock did not cover it).
        Assert.Contains(ServerSettingsLock.GroupUpdates, ServerSettingsLock.GroupsOf(SettingPlaces.JoinAdminId(JoinAdminOps.UpdateCheck)));
        r = Apply(new JoinAdminRequest { Op = JoinAdminOps.UpdateCheck });
        Assert.False(r.Ok);
        Assert.StartsWith(ServerSettingsLock.Note.TrimEnd('.'), r.Error);
        Assert.Equal(2, seen.Count);
        Assert.All(seen, s => Assert.Equal(Admin.SteamId, s.Item2));
    }

    [Fact]
    public void The_update_status_and_the_notice_travel_on_the_wire()
    {
        var state = new JoinAdminState { OptionsSupported = true };
        state.Options[ServerOptionOps.UpdateMods] = "Automatic";
        state.Updates = new JoinAdminUpdates { Line = "Checked 05:00 · 1 applied live", Countdown = 240, ComesBack = true, HostRestarts = "panel" };
        state.Updates.RestartPending.Add("Team-Maps 2.0.0");
        state.Updates.Waiting.Add("Chat 1.0.0 → 2.0.0 · a major version (Major versions is Notify)");
        state.Updates.Recent.Add("Bags 1.0.0 → 1.1.0: applied");
        var bytes = JoinAdminWire.EncodeResponse(new JoinAdminResponse { Nonce = 9, Op = JoinAdminOps.Get, Ok = true, State = state });
        Assert.True(JoinAdminWire.TryDecodeResponse(bytes, out var back));
        var u = back.State!.Updates!;
        Assert.Equal("Checked 05:00 · 1 applied live", u.Line);
        Assert.Equal(240, u.Countdown);
        Assert.True(u.ComesBack);
        Assert.Equal("panel", u.HostRestarts);
        Assert.Equal(new[] { "Team-Maps 2.0.0" }, u.RestartPending);
        Assert.Single(u.Waiting);
        Assert.Single(u.Recent);
        Assert.Equal("Automatic", back.State.Options[ServerOptionOps.UpdateMods]);
        // A server older than phase 3 sends no status: the page shows why the group is empty.
        var old = JoinAdminWire.EncodeResponse(new JoinAdminResponse { Nonce = 1, Op = "get", Ok = true, State = new JoinAdminState() });
        Assert.True(JoinAdminWire.TryDecodeResponse(old, out var oldBack));
        Assert.Null(oldBack.State!.Updates);

        var notice = JoinAdminWire.EncodeNotice("restart", "1 update waiting for a restart", offerRestart: true);
        Assert.Equal(JoinAdminWire.KindNotice, JoinAdminWire.KindOf(notice));
        // A client older than phase 3 accepted kinds up to the hello only, so it drops the notice.
        Assert.True(notice[1] > JoinAdminWire.KindHello);
        Assert.True(JoinAdminWire.TryDecodeNotice(notice, out string kind, out string text, out bool offer));
        Assert.Equal(("restart", "1 update waiting for a restart", true), (kind, text, offer));
        Assert.False(JoinAdminWire.TryDecodeResponse(notice, out _));
    }

    [Fact]
    public void A_long_status_trims_its_history_lines_before_anything_else()
    {
        var state = new JoinAdminState { OptionsSupported = true, Updates = new JoinAdminUpdates { Line = "x" } };
        for (int i = 0; i < 8; i++) state.Updates.Recent.Add(new string('r', 200));
        for (int i = 0; i < 8; i++) state.Updates.Waiting.Add(new string('w', 200));
        for (int i = 0; i < 64; i++) state.Alert.Add(new JoinAdminAlertRow { Mod = "m" + i, Text = new string('a', 300) });
        var bytes = JoinAdminWire.EncodeResponse(new JoinAdminResponse { Nonce = 1, Op = "get", Ok = true, State = state });
        Assert.True(bytes.Length <= JoinAdminLimits.MaxPacketBytes);
        Assert.True(JoinAdminWire.TryDecodeResponse(bytes, out var back));
        Assert.Equal("x", back.State!.Updates!.Line);
    }
}
