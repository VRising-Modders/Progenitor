using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Install;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Settings-scopes step 3 (docs/settings-scopes.md, decided 2026-10-03): administrator edits of
/// server settings authorize like remote installs — admin-list membership (no adminauth), else
/// <c>User.IsAdmin</c> — are rate limited per SteamID, and every decided request is audited to
/// <c>BepInEx/progenitor-settings.log</c>.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class ServerSettingsAdminParityTests : IDisposable
{
    private const ulong Member = 76561198000000001UL;
    private const ulong Stranger = 76561198000000002UL;
    private const string Row = "example.mod/General/Count";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-settings-parity-" + Guid.NewGuid().ToString("N"));
    private readonly string _pdp;
    private readonly ConfigFile _file;
    private readonly List<string> _audit = new();
    private double _now;

    public ServerSettingsAdminParityTests()
    {
        _pdp = Path.Combine(_dir, "save");
        Directory.CreateDirectory(Path.Combine(_pdp, "Settings"));
        _file = new ConfigFile(Path.Combine(_dir, "example.cfg"), saveOnInit: true);
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        ConfigSync.ResetForTests();
        ServerSettings.Uninstall();
        ServerSettings.Install();
        ServerSettings.TestClock = () => _now;
        ServerSettings.Audit = _audit.Add;
        ServerSettings.AuditClock = () => new DateTime(2026, 10, 3, 12, 0, 0, DateTimeKind.Utc);
        ServerAdminList.Shared = null;
    }

    public void Dispose()
    {
        ServerAdminList.Shared = null;
        ServerSettings.Uninstall();
        ConfigHelper.UninstallAll();
        ConfigSync.ResetForTests();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private ConfigEntry<int> Entry()
    {
        var entry = _file.Bind("General", "Count", 3, "");
        ConfigSync.Register("example.mod", entry);
        ServerSettings.Register("example.mod", entry);
        return entry;
    }

    private void AdminListHolds(params ulong[] ids)
    {
        File.WriteAllLines(Path.Combine(_pdp, "Settings", "adminlist.txt"), Array.ConvertAll(ids, id => id.ToString()));
        ServerAdminList.Shared = new ServerAdminList(() => _pdp, Path.Combine(_dir, "server"));
    }

    private static ServerSettingsMessage Change(string expected = "3", string value = "5") =>
        new(Guid.NewGuid(), Row, "System.Int32", expected, value, false, "");

    // What OnRequest builds off the fresh User read.
    private static RemoteInstallActor Actor(ulong steamId, bool isAdmin, string name = "Vlad") =>
        new(name, steamId, isAdmin, ServerAdminList.AuthorizeShared(steamId, isAdmin));

    [Fact]
    public void An_admin_list_member_edits_without_adminauth()
    {
        var entry = Entry();
        AdminListHolds(Member);

        var result = ServerSettings.Apply(Change(), true, Actor(Member, isAdmin: false));

        Assert.True(result.Success, result.Error);
        Assert.Equal(5, entry.Value);
        Assert.Equal(
            "2026-10-03T12:00:00Z settings accepted by Vlad (" + Member + ", adminlist) example.mod/General/Count \"3\" -> \"5\"",
            Assert.Single(_audit));
    }

    [Fact]
    public void A_player_on_neither_the_list_nor_adminauth_is_refused_and_audited()
    {
        var entry = Entry();
        AdminListHolds(Member);

        var result = ServerSettings.Apply(Change(), true, Actor(Stranger, isAdmin: false, name: "Guest"));

        Assert.False(result.Success);
        Assert.Equal(ServerSettings.NotAdminError, result.Error);
        Assert.Equal(3, entry.Value);
        Assert.Equal(
            "2026-10-03T12:00:00Z settings refused by Guest (" + Stranger + ", none) example.mod/General/Count \"3\" -> \"5\": "
            + ServerSettings.NotAdminError,
            Assert.Single(_audit));
    }

    [Fact]
    public void Adminauth_still_authorizes_a_player_not_on_the_list()
    {
        var entry = Entry();
        AdminListHolds(Member);

        var result = ServerSettings.Apply(Change(), true, Actor(Stranger, isAdmin: true));

        Assert.True(result.Success, result.Error);
        Assert.Equal(5, entry.Value);
        Assert.Contains(", adminauth)", Assert.Single(_audit));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Without_a_readable_list_only_adminauth_authorizes(bool isAdmin, bool expected)
    {
        Entry();
        // No shared reader (a client, or the install module did not run) …
        Assert.Equal(expected, ServerSettings.Apply(Change(), true, Actor(Member, isAdmin)).Success);
        // … and a reader whose files do not exist (unreadable) fall back the same way.
        ServerAdminList.Shared = new ServerAdminList(() => Path.Combine(_dir, "missing"), Path.Combine(_dir, "missing-install"));
        Assert.Equal(RemoteAuthorizedBy.None, ServerAdminList.AuthorizeShared(Member, isAdmin: false));
        Assert.Equal(RemoteAuthorizedBy.AdminAuth, ServerAdminList.AuthorizeShared(Member, isAdmin: true));
    }

    [Fact]
    public void SteamId_zero_is_never_a_member()
    {
        AdminListHolds(Member);
        Assert.Equal(RemoteAuthorizedBy.None, ServerAdminList.AuthorizeShared(0, isAdmin: false));
        Assert.Equal(RemoteAuthorizedBy.AdminList, ServerAdminList.AuthorizeShared(Member, isAdmin: false));
    }

    [Fact]
    public void Edits_are_rate_limited_per_sender_and_the_limit_is_audited_once_per_window()
    {
        Entry();
        AdminListHolds(Member, Stranger);
        string current = "3";
        for (int i = 0; i < ServerSettings.RateMaxEdits; i++)
        {
            string next = current == "3" ? "4" : "3";
            var ok = ServerSettings.Apply(Change(current, next), true, Actor(Member, false));
            Assert.True(ok.Success, ok.Error);
            current = next;
        }

        var limited = ServerSettings.Apply(Change(current, "9"), true, Actor(Member, false));
        var again = ServerSettings.Apply(Change(current, "9"), true, Actor(Member, false));
        Assert.Equal(ServerSettings.RateLimitedError, limited.Error);
        Assert.Equal(ServerSettings.RateLimitedError, again.Error);
        Assert.Equal(ServerSettings.RateMaxEdits + 1, _audit.Count);
        Assert.EndsWith(": " + ServerSettings.RateLimitedError, _audit[_audit.Count - 1]);

        // Another admin has their own window.
        Assert.True(ServerSettings.Apply(Change(current, "9"), true, Actor(Stranger, false)).Success);

        // A full window later the first admin may edit again.
        _now += ServerSettings.RateWindowSeconds;
        Assert.True(ServerSettings.Apply(Change("9", "2"), true, Actor(Member, false)).Success);
    }

    [Fact]
    public void Refused_requests_spend_the_window_too()
    {
        Entry();
        AdminListHolds(Member);
        for (int i = 0; i < ServerSettings.RateMaxEdits; i++)
            Assert.Equal(ServerSettings.NotAdminError, ServerSettings.Apply(Change(), true, Actor(Stranger, false)).Error);
        Assert.Equal(ServerSettings.RateLimitedError, ServerSettings.Apply(Change(), true, Actor(Stranger, false)).Error);
    }

    [Fact]
    public void The_bool_overload_still_means_adminauth()
    {
        var entry = Entry();
        Assert.False(ServerSettings.Apply(Change(), true, false).Success);
        Assert.True(ServerSettings.Apply(Change(), true, true).Success);
        Assert.Equal(5, entry.Value);
        Assert.Contains("(0, adminauth)", _audit[1]);
    }

    [Fact]
    public void Audit_lines_stay_one_line_and_bounded()
    {
        string line = SettingsAuditLog.Line(new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc), false, "A\nB", 7,
            "none", "m/S/K", "x\"y", new string('z', 500), "bad\r\nthing");
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("A\\nB", line);
        Assert.Contains("\"x\\\"y\"", line);
        Assert.Contains(new string('z', SettingsAuditLog.MaxValueChars) + "…", line);
        Assert.EndsWith(": bad\\r\\nthing", line);
    }

    [Fact]
    public void Audit_files_append_and_roll_past_the_limit()
    {
        string root = Path.Combine(_dir, "BepInEx");
        Directory.CreateDirectory(root);
        SettingsAuditLog.Append(root, "first");
        RemoteInstallAuditLog.Append(root, "install");
        Assert.Equal(new[] { "first" }, File.ReadAllLines(SettingsAuditLog.PathFor(root)));
        Assert.Equal(new[] { "install" }, File.ReadAllLines(RemoteInstallAuditLog.PathFor(root)));

        File.WriteAllText(SettingsAuditLog.PathFor(root), new string('x', (int)AuditLogFile.MaxBytes + 1));
        SettingsAuditLog.Append(root, "after roll");
        Assert.Equal(new[] { "after roll" }, File.ReadAllLines(SettingsAuditLog.PathFor(root)));
        Assert.True(File.Exists(SettingsAuditLog.PathFor(root) + ".1"));
    }

    [Fact]
    public void Uninstall_drops_the_audit_sink_and_the_rate_windows()
    {
        Entry();
        AdminListHolds(Member);
        for (int i = 0; i < ServerSettings.RateMaxEdits; i++) ServerSettings.Apply(Change(), true, Actor(Stranger, false));
        ServerSettings.Uninstall();
        ServerSettings.Install();
        Assert.Null(ServerSettings.Audit);
        Assert.Equal(ServerSettings.NotAdminError, ServerSettings.Apply(Change(), true, Actor(Stranger, false)).Error);
    }

    [Fact]
    public void The_live_request_handler_authorizes_through_the_shared_admin_list()
    {
        string body = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "ServerSettings.cs");
        SourcePin.AssertUncommented(body, "Progenitor.Install.ServerAdminList.AuthorizeShared(currentUser.PlatformId, currentUser.IsAdmin)");
        SourcePin.AssertUncommented(body, "PeerAllows(currentUser.Index, request.RowId), actor);");
        string events = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "RemoteInstallEvents.cs");
        SourcePin.AssertUncommented(events, "ServerAdminList.Shared = _adminList;");
    }
}
