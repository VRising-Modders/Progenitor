using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>REMOTEINST1 (design decision): remote installs authorize by admin-list membership or adminauth.</summary>
public sealed class ServerAdminListTests : IDisposable
{
    private const ulong Member = 76561198000000001UL;
    private const ulong Other = 76561198000000002UL;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "progenitor-adminlist-" + Guid.NewGuid().ToString("N"));
    private readonly string _pdp;
    private readonly string _install;

    public ServerAdminListTests()
    {
        _pdp = Path.Combine(_dir, "save");
        _install = Path.Combine(_dir, "server");
        Directory.CreateDirectory(Path.Combine(_pdp, "Settings"));
        Directory.CreateDirectory(Path.Combine(_install, "VRisingServer_Data", "StreamingAssets", "Settings"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
    }

    private string LocalFile => Path.Combine(_pdp, "Settings", "adminlist.txt");
    private string DefaultFile => Path.Combine(_install, "VRisingServer_Data", "StreamingAssets", "Settings", "adminlist.txt");

    private ServerAdminList List(List<string>? log = null) =>
        new(() => _pdp, _install) { LogOnce = line => log?.Add(line) };

    private static RemoteInstallActor Actor(ServerAdminList list, ulong id, bool isAdmin) =>
        new("P", id, isAdmin, ServerAdminList.Authorize(list.Check(id), isAdmin));

    private static readonly RemoteInstallRequest Install = new(1, RemoteInstallOp.Install, "team-Sample", "1.0.0");

    private static RemoteInstallReason Check(RemoteInstallActor actor, RemoteInstallSettings? settings = null) =>
        RemoteInstallPolicy.Check(Install, actor, settings ?? RemoteInstallSettings.Default, new RemoteInstallRateLimiter(), false, 1);

    [Fact]
    public void A_member_without_adminauth_is_authorized()
    {
        File.WriteAllText(LocalFile, Member + "\r\n");
        var actor = Actor(List(), Member, isAdmin: false);
        Assert.Equal(RemoteAuthorizedBy.AdminList, actor.AuthorizedBy);
        Assert.Equal(RemoteInstallReason.None, Check(actor));
    }

    [Fact]
    public void Adminauth_without_membership_is_authorized_and_neither_is_refused()
    {
        File.WriteAllText(LocalFile, Member + "\n");
        var list = List();
        var authed = Actor(list, Other, isAdmin: true);
        Assert.Equal(RemoteAuthorizedBy.AdminAuth, authed.AuthorizedBy);
        Assert.Equal(RemoteInstallReason.None, Check(authed));
        var neither = Actor(list, Other, isAdmin: false);
        Assert.Equal(RemoteAuthorizedBy.None, neither.AuthorizedBy);
        Assert.Equal(RemoteInstallReason.NotAdmin, Check(neither));
    }

    [Fact]
    public void AllowedSteamIds_still_restricts_members()
    {
        File.WriteAllText(LocalFile, Member + "\n");
        var actor = Actor(List(), Member, isAdmin: false);
        var listed = new RemoteInstallSettings { AllowedSteamIds = new HashSet<ulong> { Other } };
        Assert.Equal(RemoteInstallReason.NotAllowed, Check(actor, listed));
        Assert.False(RemoteInstallPolicy.Allowed(new RemoteInstallSettings { Enabled = false }, actor));
    }

    [Fact]
    public void An_edit_to_the_file_is_picked_up_without_a_restart()
    {
        File.WriteAllText(LocalFile, Other + "\n");
        var list = List();
        Assert.Equal(AdminListRead.NotMember, list.Check(Member));
        File.WriteAllText(LocalFile, Other + "\n" + Member + "\n");
        File.SetLastWriteTimeUtc(LocalFile, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(AdminListRead.Member, list.Check(Member));
    }

    [Fact]
    public void The_persistent_data_file_wins_over_the_install_default()
    {
        File.WriteAllText(DefaultFile, Member + "\n");
        var list = List();
        Assert.Equal(AdminListRead.Member, list.Check(Member));
        Assert.Equal(DefaultFile, list.CurrentPath);
        File.WriteAllText(LocalFile, Other + "\n");
        Assert.Equal(AdminListRead.NotMember, list.Check(Member));
        Assert.Equal(LocalFile, list.CurrentPath);
    }

    [Fact]
    public void An_unreadable_list_falls_back_to_adminauth_and_logs_once()
    {
        var log = new List<string>();
        var list = List(log);
        Assert.Equal(AdminListRead.Unreadable, list.Check(Member));
        Assert.Equal(AdminListRead.Unreadable, list.Check(Member));
        Assert.Single(log);
        Assert.Equal(RemoteAuthorizedBy.None, Actor(list, Member, isAdmin: false).AuthorizedBy);
        Assert.Equal(RemoteAuthorizedBy.AdminAuth, Actor(list, Member, isAdmin: true).AuthorizedBy);
    }

    [Fact]
    public void Parsing_ignores_blanks_comments_and_junk()
    {
        var ids = ServerAdminList.Parse("﻿" + Member + "\r\n\r\n  # comment\nnot-a-number\n  " + Other + "  \n-5\n");
        Assert.Equal(new HashSet<ulong> { Member, Other }, ids);
    }

    [Fact]
    public void The_status_and_audit_carry_who_authorized()
    {
        var status = new RemoteInstallStatus(0, true, true, true, RemoteReachability.Reachable, "", true, null,
            authorizedBy: RemoteAuthorizedBy.AdminList);
        Assert.True(RemoteInstallWire.TryDecodeStatus(RemoteInstallWire.EncodeStatus(status), out var back));
        Assert.True(back.Authorized);
        Assert.True(back.Admin); // alias for one release
        Assert.Equal(RemoteAuthorizedBy.AdminList, back.AuthorizedBy);
        string line = RemoteInstallService.AuditLine(new RemoteInstallActor("Ada", Member, false, RemoteAuthorizedBy.AdminList),
            Install, RemoteInstallStage.PendingRestart, RemoteInstallReason.None, "", Array.Empty<string>(), DateTime.UtcNow);
        Assert.Contains("authorizedBy=adminlist", line);
    }
}
