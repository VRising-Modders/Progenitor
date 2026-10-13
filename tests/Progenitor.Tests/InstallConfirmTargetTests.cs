using System;
using System.IO;
using Progenitor.Install;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// CONFIRMTARGET1 (user, 2026-10-01: "Since this is a server install it should say what server it
/// is installing it to."): the words that name the server a confirmation, a remote step and the
/// pending list touch. Pure text; the pane-level cases are in <see cref="InstallListViewTests"/>.
/// </summary>
[Collection("HarmonySlotHandoff")] // shares ServerProcessControl.RunningProbe with ServerProcessControlTests
public sealed class InstallConfirmTargetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "progenitor-confirmtarget-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        ServerProcessControl.RunningProbe = null;
        try { Directory.Delete(_root, true); }
        catch { /* temp */ }
    }

    private const string Folder = @"C:\GameModding\_testserver\server";

    [Fact]
    public void A_named_local_server_is_named_with_its_folder()
    {
        var s = ServerIdentity.Local("Progenitor Forms Test", Folder);
        Assert.Equal("Progenitor Forms Test", s.TitleName);
        Assert.Equal(@"Server: Progenitor Forms Test — local folder C:\GameModding\_testserver\server", s.ServerLine);
        Assert.Equal(@"your local server Progenitor Forms Test (C:\GameModding\_testserver\server)", s.Phrase);
        Assert.Equal(@"On this game and on your local server Progenitor Forms Test (C:\GameModding\_testserver\server).", s.BothLine);
        Assert.Equal("Pending restart of Progenitor Forms Test", s.PendingRestartOf);
    }

    [Fact]
    public void An_unnamed_local_server_falls_back_to_its_folder()
    {
        var s = ServerIdentity.Local("", Folder);
        Assert.Equal(@"Server: local folder C:\GameModding\_testserver\server", s.ServerLine);
        Assert.Equal(@"your local server (C:\GameModding\_testserver\server)", s.Phrase);
        Assert.Equal(ServerIdentity.LocalFallback, s.TitleName);
    }

    [Fact]
    public void A_connected_server_is_named_by_its_name_else_its_address()
    {
        var named = ServerIdentity.Connected("Blood Moon PvE", "203.0.113.5", 9876);
        Assert.Equal("Server: Blood Moon PvE — connected at 203.0.113.5:9876", named.ServerLine);
        Assert.Equal("the connected server Blood Moon PvE (203.0.113.5:9876)", named.Phrase);

        var address = ServerIdentity.Connected("", "203.0.113.5", 9876);
        Assert.Equal("203.0.113.5:9876", address.TitleName);
        Assert.Equal("Server: connected at 203.0.113.5:9876", address.ServerLine);
        Assert.Equal("the connected server 203.0.113.5:9876", address.Phrase);

        var unknown = ServerIdentity.Connected("", "");
        Assert.Equal("Server: the connected server", unknown.ServerLine);
        Assert.Equal(ServerIdentity.RemoteFallback, unknown.TitleName);
    }

    [Fact]
    public void Titles_are_one_short_line_and_long_names_are_cut_in_the_middle()
    {
        // CONFIRMTARGET2 (the user: "Make it more readable."): the server moved to the facts.
        Assert.Equal("Install RustyRepairs 1.2.2?", InstallModsCopy.PromptInstallHeading("RustyRepairs", "1.2.2"));
        Assert.Equal("Update RustyRepairs to 1.2.3?", InstallModsCopy.PromptUpdateHeading("RustyRepairs", "1.2.3"));
        Assert.Equal("Uninstall RustyRepairs?", InstallModsCopy.PromptUninstallShortHeading("RustyRepairs"));

        var longName = ServerIdentity.Local("The Very Long Name Of A Community Server For Vampires", Folder);
        Assert.Equal(ServerIdentity.TitleNameMax, longName.TitleName.Length);
        Assert.Contains("…", longName.TitleName);
        Assert.StartsWith("The Very Long", longName.TitleName);
    }

    [Fact]
    public void The_body_starts_with_the_server_line_and_names_the_local_server_in_fixed_words()
    {
        var s = ServerIdentity.Local("Progenitor Forms Test", Folder);
        Assert.Equal(s.ServerLine + "\n" + "Body.", InstallModsCopy.WithServerLine("Body.", s, false));
        Assert.Equal(s.BothLine + "\n" + "Body.", InstallModsCopy.WithServerLine("Body.", s, true));
        Assert.Equal("Body.", InstallModsCopy.WithServerLine("Body.", null, false));
        Assert.Equal(
            "Removed at the next restart of Progenitor Forms Test; it stays loaded until then.",
            InstallModsCopy.NameLocalServer(InstallModsCopy.UninstallRestartServerBody, s));
        // An unnamed server keeps "the local server": the server line already says which folder.
        Assert.Equal(InstallModsCopy.UninstallRestartServerBody,
            InstallModsCopy.NameLocalServer(InstallModsCopy.UninstallRestartServerBody, ServerIdentity.Local("", Folder)));
    }

    private static CatalogEntry Entry() => InstallListViewTests.Listing("iTestTeam-RustyRepairs", "RustyRepairs", version: "1.2.2");

    private static InstallPlan ServerPlan(params OrdinaryDependency[] dependencies) =>
        new("h", Entry(), InstallTarget.LocalServer, InstallDecision.RestartServerRequired, InstallReasons.RestartServer,
            Array.Empty<ArtifactHash>(), Array.Empty<PlanDependency>(), "RustyRepairs", "RustyRepairs.dll",
            ordinaryDependencies: dependencies);

    [Fact]
    public void A_server_install_says_where_its_dependency_was_found()
    {
        var server = ServerIdentity.Local("Progenitor Forms Test", Folder);
        var present = new OrdinaryDependency("dyno-ExampleCommandFramework", true, OrdinaryDependencies.BySatisfiedLedger, null);
        string body = InstallSummary.Describe(ServerPlan(present), "Server", server);
        Assert.Equal(
            "Server mod by iTestTeam, version 1.2.2. Loads at the next restart of Progenitor Forms Test. "
            + "Needs ExampleCommandFramework, which is installed on that server.",
            body);

        var missing = new OrdinaryDependency("dyno-ExampleCommandFramework", true, OrdinaryDependencies.BySatisfiedClosure, null);
        Assert.EndsWith(
            "Needs ExampleCommandFramework, which is not on that server yet, so it will be installed alongside.",
            InstallSummary.Describe(ServerPlan(missing), "Server", server));

        Assert.Equal(
            "Needs ExampleCommandFramework 0.10.4, which is installed on that server.",
            InstallSummary.Needs("dyno-ExampleCommandFramework", "0.10.4", false, InstallSummary.WhereFound(InstallTarget.LocalServer, server)));
        Assert.Equal(InstallSummary.OnThisGameOrThatServer, InstallSummary.WhereFound(InstallTarget.Both, server));
        // A client install, or a caller that names no server, keeps today's words.
        Assert.Equal("", InstallSummary.WhereFound(InstallTarget.Client, server));
        Assert.Equal("", InstallSummary.WhereFound(InstallTarget.LocalServer, null));
        Assert.Equal("Needs ExampleCommandFramework 0.10.4, which is installed.",
            InstallSummary.Needs("dyno-ExampleCommandFramework", "0.10.4", false));
        Assert.Contains("Loads at the next restart of the local server.", InstallSummary.Describe(ServerPlan(present), "Server"));
    }

    [Fact]
    public void Remote_progress_and_pending_words_name_the_server()
    {
        var server = ServerIdentity.Connected("", "203.0.113.5", 9876);
        RemoteInstallProgress Step(RemoteInstallStage stage, RemoteInstallOp op = RemoteInstallOp.Install) =>
            new(1, op, stage, RemoteInstallReason.None, "iTestTeam-RustyRepairs", "1.2.2", "");
        Assert.Equal("Downloading on 203.0.113.5:9876…", ServerRouteCopy.Stage(Step(RemoteInstallStage.Downloading), server));
        Assert.Equal("Pending restart of 203.0.113.5:9876", ServerRouteCopy.Stage(Step(RemoteInstallStage.PendingRestart), server));
        Assert.Equal("Removed from 203.0.113.5:9876 at its next restart",
            ServerRouteCopy.Stage(Step(RemoteInstallStage.PendingRestart, RemoteInstallOp.Uninstall), server));
        Assert.Equal("Downloading on server…", ServerRouteCopy.Stage(Step(RemoteInstallStage.Downloading)));
        Assert.Equal("Downloading on server…", ServerRouteCopy.Stage(Step(RemoteInstallStage.Downloading), ServerIdentity.Connected("", "")));

        Assert.Equal("Pending restart of 203.0.113.5:9876. Installs at its next restart (written by the server itself).",
            ServerRouteCopy.PendingSentence(true, server));
        Assert.Equal(ServerRouteCopy.PendingRemoteSentence, ServerRouteCopy.PendingSentence(true, ServerIdentity.Connected("", "")));
        Assert.Equal("Removed from 203.0.113.5:9876 at its next restart.", ServerRouteCopy.RemovedAtRestartSentenceFor(server));

        var local = ServerIdentity.Local("Progenitor Forms Test", Folder);
        Assert.Equal("Pending restart of Progenitor Forms Test · to load", InstallModsCopy.PendingItemCaption(true, local));
        Assert.Equal("Pending restart of Progenitor Forms Test · to uninstall", InstallModsCopy.PendingItemCaption(false, local));
        Assert.Equal(InstallModsCopy.PendingItemToLoad, InstallModsCopy.PendingItemCaption(true, null));
    }

    /// <summary>
    /// Live (dda963fb): the launch record ran the server with <c>-serverName "Progenitor Forms
    /// Test"</c>, which overrides the settings file's <c>"Name": "Progenitor Test"</c>; the server
    /// advertises the command-line name. Precedence: running command line, launch record, settings
    /// file, then the folder alone.
    /// </summary>
    [Fact]
    public void The_command_line_server_name_wins_over_the_settings_file()
    {
        ServerProcessControl.RunningProbe = _ => null;
        string server = Path.Combine(_root, "server");
        Directory.CreateDirectory(Path.Combine(server, "Settings"));
        Directory.CreateDirectory(Path.Combine(server, LocalServerFolder.BepInExFolderName));
        File.WriteAllText(Path.Combine(server, "Settings", "ServerHostSettings.json"), "{ \"Name\": \"Progenitor Test\" }");
        string exe = Path.Combine(server, LocalServerFolder.ServerExecutableName);
        Assert.Equal("Progenitor Test", ServerIdentity.HostName(server));

        // The launch record's -serverName beats the settings file.
        new ServerLaunchRecord(exe, new[] { "-persistentDataPath", Path.Combine(server, "data"), "-serverName", "Progenitor Forms Test" },
            server, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)).Write(server);
        Assert.Equal("Progenitor Forms Test", ServerIdentity.HostName(server));
        Assert.Equal("Progenitor Forms Test", ServerIdentity.CommandLineServerName(server));

        // The running process's own command line beats the record.
        ServerProcessControl.RunningProbe = _ => new ServerProcessInfo(4242, exe,
            "\"" + exe + "\" -serverName \"Live Name\" -saveName world1");
        Assert.Equal("Live Name", ServerIdentity.HostName(server));

        // A running process with no -serverName falls through to the record.
        ServerProcessControl.RunningProbe = _ => new ServerProcessInfo(4242, exe, "\"" + exe + "\" -saveName world1");
        Assert.Equal("Progenitor Forms Test", ServerIdentity.HostName(server));

        // A flag with no value is not a name.
        Assert.Equal("", ServerIdentity.ArgValue(new[] { "-serverName", "-saveName", "x" }, "-serverName"));
        Assert.Equal("", ServerIdentity.ArgValue(new[] { "-serverName" }, "-serverName"));
        Assert.Equal("A B", ServerIdentity.ArgValue(new[] { "-SERVERNAME", " A B " }, "-serverName"));
        // Revert: dropping CommandLineServerName from HostName reads "Progenitor Test" after the record is written.
    }

    [Fact]
    public void The_local_server_name_is_read_from_its_host_settings()
    {
        ServerProcessControl.RunningProbe = _ => null;
        string server = Path.Combine(_root, "server");
        Directory.CreateDirectory(Path.Combine(server, "Settings"));
        Assert.Equal("", ServerIdentity.HostName(server));
        File.WriteAllText(Path.Combine(server, "Settings", "ServerHostSettings.json"),
            "{ \"Name\": \"Progenitor Forms Test\", \"Port\": 9976 }");
        Assert.Equal("Progenitor Forms Test", ServerIdentity.HostName(server));
        File.WriteAllText(Path.Combine(server, "Settings", "ServerHostSettings.json"), "{ \"Name\": \"  \" }");
        Assert.Equal("", ServerIdentity.HostName(server));
        File.WriteAllText(Path.Combine(server, "Settings", "ServerHostSettings.json"), "not json");
        Assert.Equal("", ServerIdentity.HostName(server));
        Assert.Equal("", ServerIdentity.HostName(null));
    }
}
