using System;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Decided 2026-10-06: the player hosting a private game is a Progenitor admin. The host is the
/// game's own <c>-owner</c> argument on a <c>-hostServer</c> process; the game's admin flag is untouched.
/// </summary>
[Collection(ServerAdminListHostTests.Collection)]
public sealed class ServerAdminListHostTests : IDisposable
{
    internal const string Collection = "ServerAdminList host (process-wide command line)";

    // Distinct from every SteamID other test classes use, so a host set here cannot authorize them.
    private const ulong Host = 76561198000000099UL;
    private const ulong Guest = 76561198000000098UL;

    private const string HostedLine =
        "\"C:\\Game\\VRising_Server\\VRisingServer.exe\" -batchMode -nographics -timestamps -logFile \"x.log\" -saveName \"save one\" "
        + "-persistentDataPath \"C:\\Data\\VRising\" -gamePort 9876 -queryPort 9877 -owner 76561198000000099 -hostServer "
        + "-secure False -lanMode False -parentPID 4242 -keepAliveOnError -job-worker-count 3";

    public ServerAdminListHostTests() => UseLine(HostedLine);

    public void Dispose()
    {
        ServerAdminList.CommandLineProbe = null;
        ServerAdminList.ResetHostForTests();
    }

    private static void UseLine(string? line)
    {
        ServerAdminList.CommandLineProbe = () => line;
        ServerAdminList.ResetHostForTests();
    }

    [Fact]
    public void Hosted_command_line_names_the_owner()
    {
        Assert.Equal(Host, ServerAdminList.HostFromCommandLine(HostedLine));
        Assert.Equal(Host, ServerAdminList.HostSteamId);
        Assert.True(ServerAdminList.IsHost(Host));
        Assert.False(ServerAdminList.IsHost(Guest));
        Assert.False(ServerAdminList.IsHost(0));
    }

    [Theory]
    [InlineData("VRisingServer.exe -owner 76561198000000099")] // no -hostServer: a dedicated server
    [InlineData("VRisingServer.exe -hostServer")] // no owner
    [InlineData("VRisingServer.exe -hostServer -owner")] // owner without a value
    [InlineData("VRisingServer.exe -hostServer -owner nope")]
    [InlineData("VRisingServer.exe -hostServer -ownerX 76561198000000099")] // not the whole token
    [InlineData("VRisingServer.exe -saveName \"-owner 76561198000000099 -hostServer\"")] // inside a quoted value
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_has_no_host(string? line) =>
        Assert.Equal(0UL, ServerAdminList.HostFromCommandLine(line));

    [Fact]
    public void Names_match_case_insensitively_and_quoted_values_parse() =>
        Assert.Equal(Host, ServerAdminList.HostFromCommandLine("x.exe -HOSTSERVER -Owner \"76561198000000099\""));

    [Fact]
    public void Host_authorizes_after_the_list_and_adminauth()
    {
        Assert.Equal(RemoteAuthorizedBy.Host, ServerAdminList.Authorize(AdminListRead.NotMember, false, Host));
        Assert.Equal(RemoteAuthorizedBy.Host, ServerAdminList.Authorize(AdminListRead.Unreadable, false, Host));
        Assert.Equal(RemoteAuthorizedBy.AdminList, ServerAdminList.Authorize(AdminListRead.Member, false, Host));
        Assert.Equal(RemoteAuthorizedBy.AdminAuth, ServerAdminList.Authorize(AdminListRead.NotMember, true, Host));
        Assert.Equal(RemoteAuthorizedBy.None, ServerAdminList.Authorize(AdminListRead.NotMember, false, Guest));
        Assert.Equal("host", ServerAdminList.Word(RemoteAuthorizedBy.Host));
    }

    [Fact]
    public void AuthorizeShared_counts_the_host_without_a_list()
    {
        var saved = ServerAdminList.Shared;
        ServerAdminList.Shared = null;
        try
        {
            Assert.Equal(RemoteAuthorizedBy.Host, ServerAdminList.AuthorizeShared(Host, false));
            Assert.Equal(RemoteAuthorizedBy.None, ServerAdminList.AuthorizeShared(Guest, false));
            Assert.Equal(RemoteAuthorizedBy.None, ServerAdminList.AuthorizeShared(0, false));
        }
        finally { ServerAdminList.Shared = saved; }
    }

    [Fact]
    public void Progenitor_admin_gate_is_IsAdmin_or_host()
    {
        Assert.True(ServerAdminList.IsProgenitorAdmin(Host, false));
        Assert.True(ServerAdminList.IsProgenitorAdmin(Guest, true));
        Assert.False(ServerAdminList.IsProgenitorAdmin(Guest, false));
        UseLine("VRisingServer.exe -owner 76561198000000099"); // dedicated: no host
        Assert.False(ServerAdminList.IsProgenitorAdmin(Host, false));
    }

    [Fact]
    public void Every_live_admin_gate_counts_the_host()
    {
        string ctx = SourcePin.ReadFile("src", "Progenitor.Core", "Commands", "ICommandContext.cs");
        SourcePin.AssertUncommented(ctx, "IsAdmin = Progenitor.Install.ServerAdminList.IsProgenitorAdmin(user.PlatformId, user.IsAdmin);");
        string debug = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugMap.cs");
        // 2026-10-08: the map teleport authorizes like remote installs (admin list, adminauth, host).
        SourcePin.AssertUncommented(debug, "Progenitor.Install.ServerAdminList.AuthorizeShared(user.PlatformId, user.IsAdmin) != Progenitor.Install.RemoteAuthorizedBy.None;");
        SourcePin.AssertUncommented(debug, "isAdmin = IsAdmin(u);");
        SourcePin.AssertUncommented(debug, "Gate(IsAdmin(user), hasCharacter");
        // 2026-10-09: every .debug form authorizes with the same rule (decided: only players on the admin list).
        string commands = SourcePin.ReadFile("src", "Progenitor.Core", "Debugging", "DebugCommands.cs");
        SourcePin.AssertUncommented(commands, "internal static bool Authorized(ICommandContext ctx) => CommandAccess.ListedAdmin(ctx);");
        foreach (string signature in new[]
        {
            "public static void Debug(ICommandContext ctx, string what = \"\", [Remainder] string rest = \"\")",
            "public static void Build(ICommandContext ctx, [Remainder] string what = \"\")",
        })
        {
            string body = SourcePin.MethodBody(commands, signature);
            int gate = body.IndexOf("Authorized(ctx)", StringComparison.Ordinal);
            int parse = body.IndexOf("DebugCommandParse.TryParse(", StringComparison.Ordinal);
            Assert.True(gate > 0 && parse > gate, signature + ": the admin rule must be checked before the parse");
        }
        Assert.Equal(2, CountOf(commands, "[AdminListGate]"));
        Assert.DoesNotContain("adminOnly: true", commands, StringComparison.Ordinal);
        string registry = SourcePin.ReadFile("src", "Progenitor.Core", "Commands", "CommandRegistry.cs");
        SourcePin.AssertUncommented(registry,
            "Progenitor.Install.ServerAdminList.AuthorizeShared(SteamIdOf(ctx), GameAdminOf(ctx)) != Progenitor.Install.RemoteAuthorizedBy.None;");
        string stop = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "StopServerEvents.cs");
        // F21: the stop channel authorizes like remote installs (admin list, adminauth, host).
        SourcePin.AssertUncommented(stop, "var by = ServerAdminList.AuthorizeShared(current.PlatformId, current.IsAdmin);");
        string photo = SourcePin.ReadFile("src", "Progenitor.Core", "Cameras", "PhotoMode.cs");
        SourcePin.AssertUncommented(photo, "ledger.Handle(index, Progenitor.Install.ServerAdminList.IsProgenitorAdmin(current.PlatformId, current.IsAdmin)");
        string install = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "RemoteInstallEvents.cs");
        SourcePin.AssertUncommented(install, "ServerAdminList.Authorize(membership, current.IsAdmin, current.PlatformId)");
    }

    private static int CountOf(string s, string part)
    {
        int n = 0;
        for (int at = s.IndexOf(part, StringComparison.Ordinal); at >= 0; at = s.IndexOf(part, at + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Fact]
    public void Host_travels_as_AdminAuth_so_older_clients_accept_the_status()
    {
        var status = new RemoteInstallStatus(1, true, true, true, RemoteReachability.Reachable, "", false, null,
            steamId: Host, authorizedBy: RemoteAuthorizedBy.Host);
        byte[] bytes = RemoteInstallWire.EncodeStatus(status);
        Assert.True(RemoteInstallWire.TryDecodeStatus(bytes, out var back));
        Assert.True(back.Authorized);
        Assert.Equal(RemoteAuthorizedBy.AdminAuth, back.AuthorizedBy);
    }
}

[CollectionDefinition(ServerAdminListHostTests.Collection, DisableParallelization = true)]
public sealed class ServerAdminListHostCollection { }
