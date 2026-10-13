using System;
using Progenitor.Install;
using Xunit;

namespace Progenitor.Tests;

/// <summary>UI polish (user, 2026-10-01): the Default install target captions and the live description.</summary>
public sealed class InstallTargetHelpTests
{
    private static readonly ServerRoute Local = new(ServerRouteKind.Local, ServerRouteBlock.None);
    private static readonly ServerRoute Remote = new(ServerRouteKind.Remote, ServerRouteBlock.None);
    private static readonly ServerRoute NoServer = new(ServerRouteKind.None, ServerRouteBlock.NoServer);
    private static readonly ServerRoute NotAdmin = new(ServerRouteKind.None, ServerRouteBlock.NotAdmin);
    private const string Path = @"C:\GameModding\_testserver\server";

    [Theory]
    [InlineData("followSide", "Automatic (by mod type)")]
    [InlineData("both", "Client and server")]
    [InlineData("client", "My client only")]
    [InlineData("server", "Server only")]
    public void Captions_map_to_the_unchanged_values(string value, string caption)
    {
        var setting = DefaultInstallTarget.Parse(value);
        Assert.Equal(caption, DefaultInstallTarget.CaptionOf(setting));
        Assert.Equal(value, DefaultInstallTarget.ValueOf(setting));
        Assert.Equal(setting, DefaultInstallTarget.Parse(caption));
    }

    [Theory]
    [InlineData("Follow the mod's side", "followSide")]
    [InlineData("Both", "both")]
    [InlineData("My client", "client")]
    [InlineData("Server", "server")]
    [InlineData("Local server", "server")]
    [InlineData("localServer", "server")]
    public void Old_values_and_captions_still_load(string stored, string value) =>
        Assert.Equal(value, DefaultInstallTarget.ValueOf(DefaultInstallTarget.Parse(stored)));

    private static string D(DefaultInstallTargetSetting s, ServerRoute r, RemoteAuthorizedBy by = RemoteAuthorizedBy.AdminList) =>
        DefaultInstallTargetHelp.Describe(s, r, Path, by);

    [Fact]
    public void Automatic_reads_by_route()
    {
        Assert.StartsWith("Each mod goes where it runs", D(DefaultInstallTargetSetting.FollowSide, Local));
        Assert.Contains("Right now: server mods go to your local server at " + Path + ".", D(DefaultInstallTargetSetting.FollowSide, Local));
        Assert.Contains("Right now: server mods go to the server you're connected to (you're on its admin list).", D(DefaultInstallTargetSetting.FollowSide, Remote));
        Assert.Contains("(you're an admin there)", D(DefaultInstallTargetSetting.FollowSide, Remote, RemoteAuthorizedBy.AdminAuth));
        Assert.Contains("Right now: no server is available, so server mods install to this game only. They still load in private games you host.",
            D(DefaultInstallTargetSetting.FollowSide, NoServer));
        Assert.Contains("(Server installs need you on this server's admin list)", D(DefaultInstallTargetSetting.FollowSide, NotAdmin));
    }

    [Fact]
    public void Client_and_server_reads_by_route()
    {
        Assert.StartsWith("Every mod installs to both this game and the server.", D(DefaultInstallTargetSetting.Both, Local));
        Assert.Contains("Right now: mods go to this game and your local server at " + Path + ".", D(DefaultInstallTargetSetting.Both, Local));
        Assert.Contains("Right now: mods go to this game and the server you're connected to", D(DefaultInstallTargetSetting.Both, Remote));
        Assert.Contains("Right now: no server is available, so mods install to this game only.", D(DefaultInstallTargetSetting.Both, NoServer));
    }

    [Fact]
    public void My_client_only_never_names_a_server()
    {
        foreach (var route in new[] { Local, Remote, NoServer })
        {
            string text = D(DefaultInstallTargetSetting.Client, route);
            Assert.StartsWith("Mods install to this game only, never to a server.", text);
            Assert.Contains("Right now: mods install to this game.", text);
        }
    }

    [Fact]
    public void Server_only_says_not_available_without_a_route()
    {
        Assert.Contains("Right now: mods go to your local server at " + Path + ".", D(DefaultInstallTargetSetting.Server, Local));
        Assert.Contains("Right now: mods go to the server you're connected to", D(DefaultInstallTargetSetting.Server, Remote));
        Assert.Contains("Right now: not available — no server to install to, so mods install to this game instead.", D(DefaultInstallTargetSetting.Server, NoServer));
    }

    [Fact]
    public void Every_description_lists_each_choice_and_what_server_means()
    {
        foreach (DefaultInstallTargetSetting s in DefaultInstallTarget.All)
        {
            string text = D(s, Local);
            Assert.Contains(DefaultInstallTargetHelp.AutomaticLine, text);
            Assert.Contains(DefaultInstallTargetHelp.BothLine, text);
            Assert.Contains(DefaultInstallTargetHelp.ClientLine, text);
            Assert.Contains(DefaultInstallTargetHelp.ServerLine, text);
            Assert.Contains(DefaultInstallTargetHelp.ServerMeaning, text);
        }
    }
}
