using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BepInEx.Configuration;
using Progenitor.Config;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// SERVERINST1: install Progenitor onto a local dedicated-server folder from the client's own
/// image and remove it with every ledger file. Temp folders only; no process is ever started
/// or stopped. Guard removals named in comments must fail these tests.
/// </summary>
[Collection("HarmonySlotHandoff")]
public sealed class ServerInstallTests : IDisposable
{
    private readonly string _root;
    private readonly string _server;
    private readonly string _client;
    private readonly ServerInstallSource _source;

    public ServerInstallTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "progenitor-serverinst-" + Guid.NewGuid().ToString("N"));
        _server = Path.Combine(_root, "server");
        _client = Path.Combine(_root, "client", "BepInEx");
        Directory.CreateDirectory(_server);
        File.WriteAllText(Path.Combine(_server, LocalServerFolder.ServerExecutableName), "exe");
        Directory.CreateDirectory(Path.Combine(_server, "BepInEx", "core"));
        File.WriteAllText(Path.Combine(_server, "BepInEx", "core", "BepInEx.Core.dll"), "bepinex");
        Directory.CreateDirectory(Path.Combine(_server, "BepInEx", "plugins"));
        Directory.CreateDirectory(Path.Combine(_client, "plugins"));
        Directory.CreateDirectory(Path.Combine(_client, "progenitor-core"));
        File.WriteAllBytes(Path.Combine(_client, "plugins", "Progenitor.Bootstrap.dll"), Bytes("bootstrap-v1"));
        File.WriteAllBytes(Path.Combine(_client, "progenitor-core", "Progenitor.dll"), Bytes("core-v1"));
        _source = ServerInstallSource.FromClientRoot(_client, "0.1.0");
        ServerInstall.RunningServerProbe = _ => null;
        ServerInstall.FreeSpaceOverride = null;
        ServerInstall.WriteAccessOverride = null;
        ServerInstall.ClockUtc = () => new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc);
        LocalServerFolder.WriteAccessOverride = null;
        LocalServerFolder.DosDeviceLookup = null;
        LocalServerFolder.ReparseLookup = null;
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        UiApi.ResetForTests();
    }

    public void Dispose()
    {
        ServerInstall.RunningServerProbe = null;
        ServerInstall.FreeSpaceOverride = null;
        ServerInstall.WriteAccessOverride = null;
        ServerInstall.ClockUtc = null;
        LocalServerFolder.WriteAccessOverride = null;
        LocalServerFolder.DosDeviceLookup = null;
        LocalServerFolder.ReparseLookup = null;
        ConfigurationPane.ResetServerInstallStateForTests();
        UiApi.ResetForTests();
        ConfigHelper.UninstallAll();
        HarmonySlotHandoff.SetSkipModules(null, refusedLiveWorld: false);
        try { Directory.Delete(_root, recursive: true); }
        catch { /* temp */ }
    }

    // --- preflight ---

    [Fact]
    public void Preflight_refuses_when_BepInEx_core_is_missing_and_writes_nothing()
    {
        Directory.Delete(Path.Combine(_server, "BepInEx", "core"), recursive: true);
        var status = ServerInstall.Inspect(_server, _source);
        Assert.Equal(ServerInstallState.Refused, status.State);
        Assert.Equal(ServerInstall.RefusalNoBepInEx, status.Reason);
        Assert.Equal("BepInEx is not installed on the server", status.Reason);

        var result = ServerInstall.Install(_server, _source);
        Assert.False(result.Ok);
        Assert.False(File.Exists(ServerInstallLedger.PathFor(_server)));
        Assert.False(File.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "Progenitor.Bootstrap.dll")));
        // Revert: dropping HasBepInExCore from Inspect writes Bootstrap into a server that cannot load it.
    }

    [Fact]
    public void Preflight_refuses_an_unwritable_folder_and_no_free_space()
    {
        ServerInstall.WriteAccessOverride = _ => false;
        var unwritable = ServerInstall.Install(_server, _source);
        Assert.False(unwritable.Ok);
        Assert.StartsWith(ServerInstall.RefusalUnwritable, unwritable.Message);
        Assert.False(File.Exists(ServerInstallLedger.PathFor(_server)));

        ServerInstall.WriteAccessOverride = null;
        ServerInstall.FreeSpaceOverride = _ => 1;
        var noSpace = ServerInstall.Install(_server, _source);
        Assert.False(noSpace.Ok);
        Assert.Equal(ServerInstall.RefusalNoSpace, noSpace.Message);
        Assert.False(File.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "progenitor-core", "Progenitor.core")));
        // Revert: skipping the free-space check leaves a torn copy on a full drive.
    }

    [Fact]
    public void Running_server_in_the_folder_refuses_install_and_uninstall_and_names_the_path()
    {
        Assert.True(ServerInstall.Install(_server, _source).Ok);
        string exe = Path.Combine(_server, "VRisingServer.exe");
        ServerInstall.RunningServerProbe = root => string.Equals(root, _server, StringComparison.OrdinalIgnoreCase) ? exe : null;

        var status = ServerInstall.Inspect(_server, _source);
        Assert.Equal(ServerInstallState.Refused, status.State);
        Assert.Contains(exe, status.Reason);
        // SERVERINST3: the toggle is still refused while it runs, but the copy now points at the Stop server row.
        Assert.Contains("Stop the server first (Stop server below), then install or remove.", status.Reason);

        var install = ServerInstall.Install(_server, _source);
        Assert.False(install.Ok);
        var uninstall = ServerInstall.Uninstall(_server);
        Assert.False(uninstall.Ok);
        Assert.Contains(exe, uninstall.Message);
        Assert.True(File.Exists(ServerInstallLedger.PathFor(_server)));
        Assert.True(File.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "Progenitor.Bootstrap.dll")));

        // The product never operates a process: no Start/Kill/CloseMainWindow statements in the feature.
        string src = SourcePin.ReadFile("src", "Progenitor.Core", "Install", "ServerInstall.cs");
        SourcePin.AssertNoStatementContaining(src, "Process.Start(");
        SourcePin.AssertNoStatementContaining(src, ".Kill(");
        SourcePin.AssertNoStatementContaining(src, ".CloseMainWindow(");
        string pane = SourcePin.ReadFile("src", "Progenitor.Core", "Ui", "ConfigurationPane.cs");
        SourcePin.AssertNoStatementContaining(pane, "Process.Start(");
        SourcePin.AssertNoStatementContaining(pane, ".Kill(");
        // Revert: removing the FindRunningServer check from Uninstall deletes a mapped DLL under a live server.
    }

    // --- install ---

    [Fact]
    public void Install_copies_the_exact_client_files_verifies_hashes_and_writes_the_ledger()
    {
        var result = ServerInstall.Install(_server, _source);
        Assert.True(result.Ok, result.Message);
        Assert.Equal("Progenitor 0.1.0 installed (server needs a restart)", result.Message);

        string bootstrap = Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "Progenitor.Bootstrap.dll");
        string core = Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "progenitor-core", "Progenitor.core");
        Assert.Equal(Bytes("bootstrap-v1"), File.ReadAllBytes(bootstrap));
        Assert.Equal(Bytes("core-v1"), File.ReadAllBytes(core));
        Assert.True(Directory.Exists(Path.Combine(_server, "BepInEx", "progenitor-pending")));
        Assert.True(Directory.Exists(Path.Combine(_server, "BepInEx", "progenitor-reload")));

        var ledger = ServerInstallLedger.TryRead(_server);
        Assert.NotNull(ledger);
        Assert.Equal(ServerInstallLedger.CurrentSchema, ledger!.Schema);
        Assert.Equal("0.1.0", ledger.Version);
        Assert.Equal(Sha(Bytes("bootstrap-v1")), ledger.BootstrapSha256);
        Assert.Equal(Sha(Bytes("core-v1")), ledger.CoreSha256);
        Assert.Equal(2, ledger.Files.Count);
        var b = ledger.Files.Single(f => f.Path == ServerInstall.BootstrapRelative);
        Assert.Equal(ServerInstallLedger.CategoryFramework, b.Category);
        Assert.Equal(Sha(Bytes("bootstrap-v1")), b.Sha256);
        Assert.Equal(Bytes("bootstrap-v1").Length, b.Size);
        Assert.StartsWith(ServerInstallLedger.ClientImageSourcePrefix, b.Source);
        Assert.Equal(new DateTime(2026, 9, 12, 10, 0, 0, DateTimeKind.Utc), b.WrittenUtc);
        Assert.Contains("\"schema\": 1", File.ReadAllText(ServerInstallLedger.PathFor(_server)));
        Assert.Empty(Directory.GetFiles(Path.Combine(_server, "BepInEx"), "*.part", SearchOption.AllDirectories));

        var status = ServerInstall.Inspect(_server, _source);
        Assert.Equal(ServerInstallState.Installed, status.State);
        Assert.Equal("installed", status.StateName);
        // Revert: writing the ledger without the sha256 fields makes the uninstall unable to tell a changed file.
    }

    [Fact]
    public void Reinstall_with_identical_hashes_is_a_no_op()
    {
        Assert.True(ServerInstall.Install(_server, _source).Ok);
        string ledgerPath = ServerInstallLedger.PathFor(_server);
        string before = File.ReadAllText(ledgerPath);
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "Progenitor.Bootstrap.dll"));

        var again = ServerInstall.Install(_server, _source);
        Assert.True(again.Ok);
        Assert.True(again.NoOp);
        Assert.Empty(again.Removed);
        Assert.Equal(before, File.ReadAllText(ledgerPath));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "Progenitor.Bootstrap.dll")));
        // Revert: always copying would rewrite Bootstrap under an unchanged install.
    }

    [Fact]
    public void A_different_client_image_offers_update_with_both_hashes_and_keeps_pending_entries()
    {
        Assert.True(ServerInstall.Install(_server, _source).Ok);
        string pendingRel = "BepInEx/progenitor-pending/index/Mod.dll";
        Directory.CreateDirectory(Path.GetDirectoryName(ServerInstallLedger.Absolute(_server, pendingRel))!);
        File.WriteAllBytes(ServerInstallLedger.Absolute(_server, pendingRel), Bytes("mod"));
        ServerInstallLedger.TryRead(_server)!
            .With(new ServerInstallLedgerEntry(pendingRel, Sha(Bytes("mod")), 3, ServerInstallLedger.CategoryPending, "package:mod@1", DateTime.UtcNow))
            .Write(_server);

        File.WriteAllBytes(Path.Combine(_client, "progenitor-core", "Progenitor.dll"), Bytes("core-v2"));
        var source2 = ServerInstallSource.FromClientRoot(_client, "0.2.0");
        var status = ServerInstall.Inspect(_server, source2);
        Assert.Equal(ServerInstallState.UpdateAvailable, status.State);
        Assert.Equal("update-available", status.StateName);
        Assert.Equal(Sha(Bytes("core-v1")), status.InstalledCoreSha);
        Assert.Equal(Sha(Bytes("core-v2")), status.ClientCoreSha);
        string line = ServerInstall.UpdateStatus(status);
        Assert.Contains(Sha(Bytes("core-v1")).Substring(0, 8), line);
        Assert.Contains(Sha(Bytes("core-v2")).Substring(0, 8), line);

        var update = ServerInstall.Install(_server, source2);
        Assert.True(update.Ok, update.Message);
        Assert.False(update.NoOp);
        Assert.Equal(Bytes("core-v2"), File.ReadAllBytes(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "progenitor-core", "Progenitor.core")));
        var ledger = ServerInstallLedger.TryRead(_server)!;
        Assert.Equal("0.2.0", ledger.Version);
        Assert.Equal(Sha(Bytes("core-v2")), ledger.CoreSha256);
        Assert.Contains(ledger.Files, f => f.Path == pendingRel && f.Category == ServerInstallLedger.CategoryPending);
        Assert.Equal(3, ledger.Files.Count);
        Assert.Equal(ServerInstallState.Installed, ServerInstall.Inspect(_server, source2).State);
        // Revert: rebuilding the ledger from the two framework files alone drops the pending mod from the uninstall.
    }

    // --- uninstall ---

    [Fact]
    public void Uninstall_removes_exactly_the_ledger_files_the_promoted_copy_empty_folders_and_the_ledger()
    {
        Assert.True(ServerInstall.Install(_server, _source).Ok);
        string pendingRel = "BepInEx/progenitor-pending/index/Mod.dll";
        Directory.CreateDirectory(Path.GetDirectoryName(ServerInstallLedger.Absolute(_server, pendingRel))!);
        File.WriteAllBytes(ServerInstallLedger.Absolute(_server, pendingRel), Bytes("mod"));
        string promoted = Path.Combine(_server, "BepInEx", "progenitor-reload", "index", "Mod.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(promoted)!);
        File.WriteAllBytes(promoted, Bytes("mod"));
        ServerInstallLedger.TryRead(_server)!
            .With(new ServerInstallLedgerEntry(pendingRel, Sha(Bytes("mod")), 3, ServerInstallLedger.CategoryPending, "package:mod@1", DateTime.UtcNow))
            .Write(_server);
        string foreignPlugin = Path.Combine(_server, "BepInEx", "plugins", "OtherTool.dll");
        File.WriteAllBytes(foreignPlugin, Bytes("foreign"));
        string foreignReload = Path.Combine(_server, "BepInEx", "progenitor-reload", "Other.dll");
        File.WriteAllBytes(foreignReload, Bytes("foreign-reload"));

        var result = ServerInstall.Uninstall(_server);
        Assert.True(result.Ok, result.Message);
        Assert.Empty(result.Skipped);
        Assert.Contains(ServerInstall.BootstrapRelative, result.Removed);
        Assert.Contains(ServerInstall.CoreRelative, result.Removed);
        Assert.Contains(pendingRel, result.Removed);
        Assert.Contains("BepInEx/progenitor-reload/index/Mod.dll", result.Removed);

        Assert.False(File.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "Progenitor.Bootstrap.dll")));
        Assert.False(File.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "progenitor-core", "Progenitor.core")));
        Assert.False(Directory.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "progenitor-core")));
        Assert.False(Directory.Exists(Path.Combine(_server, "BepInEx", "progenitor-pending")));
        Assert.False(File.Exists(promoted));
        Assert.False(File.Exists(ServerInstallLedger.PathFor(_server)));

        Assert.True(File.Exists(foreignPlugin));
        Assert.True(File.Exists(foreignReload));
        Assert.True(Directory.Exists(Path.Combine(_server, "BepInEx", "progenitor-reload")));
        Assert.True(Directory.Exists(Path.Combine(_server, "BepInEx", "plugins")));
        Assert.True(File.Exists(Path.Combine(_server, "BepInEx", "core", "BepInEx.Core.dll")));
        Assert.True(File.Exists(Path.Combine(_server, "VRisingServer.exe")));
        // Revert: deleting plugins/*.dll instead of ledger entries removes OtherTool.dll; deleting
        // progenitor-reload wholesale removes Other.dll.
    }

    [Fact]
    public void Uninstall_leaves_a_changed_ledger_file_in_place_and_reports_it()
    {
        Assert.True(ServerInstall.Install(_server, _source).Ok);
        string core = Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "progenitor-core", "Progenitor.core");
        File.WriteAllBytes(core, Bytes("user-edited-core"));

        var result = ServerInstall.Uninstall(_server);
        Assert.True(result.Ok);
        Assert.Equal(new[] { ServerInstall.CoreRelative }, result.Skipped);
        Assert.Contains(ServerInstall.BootstrapRelative, result.Removed);
        Assert.Contains("changed", result.Message);
        Assert.True(File.Exists(core));
        Assert.Equal(Bytes("user-edited-core"), File.ReadAllBytes(core));
        Assert.False(File.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "Progenitor.Bootstrap.dll")));

        var remaining = ServerInstallLedger.TryRead(_server);
        Assert.NotNull(remaining);
        Assert.Single(remaining!.Files);
        Assert.Equal(ServerInstall.CoreRelative, remaining.Files[0].Path);
        // Revert: deleting without the hash compare removes the user's edited Core.
    }

    [Fact]
    public void Updating_a_three_location_install_moves_it_into_the_single_folder()
    {
        // The ledger and files an older Progenitor wrote: Bootstrap at the plugins root, Core in BepInEx/progenitor-core.
        string oldBoot = Path.Combine(_server, "BepInEx", "plugins", "Progenitor.Bootstrap.dll");
        string oldCore = Path.Combine(_server, "BepInEx", "progenitor-core", "Progenitor.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(oldCore)!);
        File.WriteAllBytes(oldBoot, Bytes("bootstrap-v0"));
        File.WriteAllBytes(oldCore, Bytes("core-v0"));
        var when = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        new ServerInstallLedger("0.0.9", Sha(Bytes("bootstrap-v0")), Sha(Bytes("core-v0")), when, new[]
        {
            new ServerInstallLedgerEntry("BepInEx/plugins/Progenitor.Bootstrap.dll", Sha(Bytes("bootstrap-v0")), 12, ServerInstallLedger.CategoryFramework, "client-image:x", when),
            new ServerInstallLedgerEntry("BepInEx/progenitor-core/Progenitor.dll", Sha(Bytes("core-v0")), 7, ServerInstallLedger.CategoryFramework, "client-image:y", when),
        }).Write(_server);

        var result = ServerInstall.Install(_server, _source);
        Assert.True(result.Ok, result.Message);
        Assert.True(File.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "Progenitor.Bootstrap.dll")));
        Assert.True(File.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "progenitor-core", "Progenitor.core")));
        Assert.False(File.Exists(oldBoot));
        Assert.False(File.Exists(oldCore));
        Assert.False(Directory.Exists(Path.Combine(_server, "BepInEx", "progenitor-core")));
        var ledger = ServerInstallLedger.TryRead(_server)!;
        Assert.Equal(new[] { ServerInstall.BootstrapRelative, ServerInstall.CoreRelative },
            ledger.OfCategory(ServerInstallLedger.CategoryFramework).Select(f => f.Path).ToArray());
        // Revert: keeping the old framework files gives the server two Bootstraps and two Cores.
    }

    [Fact]
    public void Updating_keeps_an_old_framework_file_whose_bytes_changed()
    {
        string oldBoot = Path.Combine(_server, "BepInEx", "plugins", "Progenitor.Bootstrap.dll");
        File.WriteAllBytes(oldBoot, Bytes("edited-by-hand"));
        var when = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        new ServerInstallLedger("0.0.9", Sha(Bytes("bootstrap-v0")), "", when, new[]
        {
            new ServerInstallLedgerEntry("BepInEx/plugins/Progenitor.Bootstrap.dll", Sha(Bytes("bootstrap-v0")), 12, ServerInstallLedger.CategoryFramework, "client-image:x", when),
        }).Write(_server);
        Assert.True(ServerInstall.Install(_server, _source).Ok);
        Assert.True(File.Exists(oldBoot));
    }

    [Fact]
    public void Uninstall_without_a_ledger_touches_nothing()
    {
        string foreign = Path.Combine(_server, "BepInEx", "plugins", "Progenitor.Bootstrap.dll");
        File.WriteAllBytes(foreign, Bytes("placed-by-hand"));
        var result = ServerInstall.Uninstall(_server);
        Assert.True(result.Ok);
        Assert.True(result.NoOp);
        Assert.True(File.Exists(foreign));
        // Revert: falling back to "known Progenitor paths" without a ledger removes a hand-placed Bootstrap.
    }

    // --- ledger ---

    [Fact]
    public void Ledger_round_trips_and_a_torn_or_foreign_schema_reads_as_absent()
    {
        var when = new DateTime(2026, 9, 12, 9, 30, 0, DateTimeKind.Utc);
        var ledger = new ServerInstallLedger("0.1.0", "aa", "bb", when, new[]
        {
            new ServerInstallLedgerEntry("BepInEx/plugins/Progenitor.Bootstrap.dll", "aa", 10, ServerInstallLedger.CategoryFramework, "client-image:aa", when),
            new ServerInstallLedgerEntry("BepInEx/progenitor-pending/index/X.dll", "cc", 20, ServerInstallLedger.CategoryPending, "package:x@1.0.0", when),
        });
        ledger.Write(_server);
        var read = ServerInstallLedger.TryRead(_server)!;
        Assert.Equal("0.1.0", read.Version);
        Assert.Equal(2, read.Files.Count);
        Assert.Equal("package:x@1.0.0", read.Files[1].Source);
        Assert.Equal(20, read.Files[1].Size);
        Assert.Equal(when, read.Files[1].WrittenUtc);
        Assert.True(read.Contains("bepinex/plugins/progenitor.bootstrap.dll"));

        File.WriteAllText(ServerInstallLedger.PathFor(_server), "{\"schema\":1,\"files\":[");
        Assert.Null(ServerInstallLedger.TryRead(_server));
        File.WriteAllText(ServerInstallLedger.PathFor(_server), "{\"schema\":99,\"files\":[]}");
        Assert.Null(ServerInstallLedger.TryRead(_server));
        // Revert: reading a torn ledger as empty would let Install overwrite a real one silently.
    }

    [Fact]
    public void Server_target_staging_appends_the_pending_file_to_the_ledger_and_client_target_does_not()
    {
        Assert.True(ServerInstall.Install(_server, _source).Ok);
        string serverBep = Path.Combine(_server, "BepInEx");
        var (entry, inspection, zip) = VerifiedSample();

        var serverPlan = InstallPlanner.Plan(entry, inspection, InstallTarget.LocalServer, localServerConfigured: true);
        var written = InstallStaging.ExecuteToPending(serverBep, serverPlan, inspection, zip);
        Assert.True(written.Ok, written.Error);
        var ledger = ServerInstallLedger.TryRead(_server)!;
        var pending = ledger.Files.Single(f => f.Category == ServerInstallLedger.CategoryPending);
        Assert.Equal(ServerInstallLedger.Relative(_server, written.PendingPath!), pending.Path);
        Assert.StartsWith("BepInEx/progenitor-pending/", pending.Path);
        Assert.Equal(CatalogFetcher.HashFile(written.PendingPath!), pending.Sha256);
        // POLISH4 item 1: the LISTING KEY in `source`, with the plugin id in the row's own field.
        // This line read `package:example.sample@1.0.0` before — the plugin-id spelling that made
        // the local server's half of a Both install unreachable from a later session's catalog entry.
        Assert.Equal("package:team-Sample@1.0.0", pending.Source);
        Assert.Equal("example.sample", pending.PluginId);

        // Idempotent: the already-present path appends the same entry once.
        var again = InstallStaging.ExecuteToPending(serverBep, serverPlan, inspection, zip);
        Assert.True(again.Ok && again.AlreadyPresent);
        Assert.Equal(3, ServerInstallLedger.TryRead(_server)!.Files.Count);

        // Uninstall removes the staged file through the ledger.
        var removed = ServerInstall.Uninstall(_server);
        Assert.Contains(pending.Path, removed.Removed);
        Assert.False(File.Exists(written.PendingPath));

        // INSTALLFAIL1 (live 2026-10-03) supersedes "a client root has no ledger": the Client target
        // is ledgered on its own root, the ledger created when absent, exactly as the hot and
        // ordinary routes ledger it. Unledgered, HotDemo's Client cell read `Missing` after a
        // successful write and Uninstall could never take the copy back.
        string clientRoot = Path.Combine(_root, "client2", "BepInEx");
        Directory.CreateDirectory(clientRoot);
        var clientPlan = InstallPlanner.Plan(entry, inspection, InstallTarget.Client, localServerConfigured: true);
        var clientWrite = InstallStaging.ExecuteToPending(clientRoot, clientPlan, inspection, zip);
        Assert.True(clientWrite.Ok, clientWrite.Error);
        var clientRow = Assert.Single(ServerInstallLedger.TryRead(Path.Combine(_root, "client2"))!.Files);
        Assert.Equal(ServerInstallLedger.CategoryPending, clientRow.Category);
        Assert.Equal("package:team-Sample@1.0.0", clientRow.Source);
        Assert.Equal("example.sample", clientRow.PluginId);
        // Revert: removing AppendServerLedger from InstallStaging leaves a server-target mod behind on uninstall.
    }

    [Fact]
    public void Prompt_text_lists_the_count_and_the_names()
    {
        Assert.True(ServerInstall.Install(_server, _source).Ok);
        string pendingRel = "BepInEx/progenitor-pending/index/Mod.dll";
        var ledger = ServerInstallLedger.TryRead(_server)!
            .With(new ServerInstallLedgerEntry(pendingRel, "cc", 3, ServerInstallLedger.CategoryPending, "package:mod@1", DateTime.UtcNow));
        string text = ServerInstall.PromptText(ledger);
        Assert.StartsWith("This removes Progenitor from the server AND every mod Progenitor installed there (3 files: ", text);
        Assert.Contains(ServerInstall.BootstrapRelative, text);
        Assert.Contains(ServerInstall.CoreRelative, text);
        Assert.Contains(pendingRel, text);
        Assert.EndsWith("Mods installed by other tools are not touched. The server needs a restart.", text);
        // Revert: dropping the file list from the prompt fails the Contains asserts.
    }

    // --- Configuration pane ---

    [Fact]
    public void Pane_shows_no_toggle_until_the_folder_is_validated_then_installs_on_toggle_on()
    {
        LocalServerFolder.WriteAccessOverride = _ => true;
        var (pane, bindings) = Pane();
        var panel = new FakePanel();
        pane.Build(panel);
        Assert.DoesNotContain(panel.Toggles, t => t.Text == ConfigurationPane.ServerInstallToggle);

        Assert.True(pane.ApplyDraft(_server).Completed);
        panel = new FakePanel();
        pane.Build(panel);
        var toggle = panel.Toggles.Single(t => t.Text == ConfigurationPane.ServerInstallToggle);
        Assert.False(toggle.Value);
        Assert.Equal("not-installed", pane.ServerInstallReadback()["state"]);
        Assert.Contains(panel.Labels, l => l.Text == ServerInstall.NotInstalledStatus);
        Assert.True(pane.IsServerInstallHandle(toggle));

        int rebuilds = 0;
        pane.SetRebuild(() => rebuilds++);
        toggle.Fire(true);
        Assert.True(File.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "Progenitor.Bootstrap.dll")));
        Assert.Equal("Progenitor 0.1.0 installed (server needs a restart)", pane.ServerInstallLine);
        Assert.Equal(1, rebuilds);
        var readback = pane.ServerInstallReadback();
        Assert.Equal("installed", readback["state"]);
        Assert.Equal("0.1.0", readback["version"]);
        Assert.Equal(new[] { ServerInstall.BootstrapRelative, ServerInstall.CoreRelative }, (IReadOnlyList<string>)readback["ledgerFiles"]!);

        panel = new FakePanel();
        pane.Build(panel);
        Assert.True(panel.Toggles.Single(t => t.Text == ConfigurationPane.ServerInstallToggle).Value);
        Assert.Contains(panel.Labels, l => l.Text == "Progenitor 0.1.0 installed (server needs a restart)");
        _ = bindings;
        // Revert: building the toggle for an unvalidated folder would let a click copy into "Not configured".
    }

    [Fact]
    public void Pane_toggle_off_opens_the_prompt_and_Remove_uninstalls_while_Cancel_keeps_everything()
    {
        LocalServerFolder.WriteAccessOverride = _ => true;
        var (pane, _) = Pane();
        Assert.True(pane.ApplyDraft(_server).Completed);
        Assert.True(pane.InstallProgenitorOnServer().Completed);

        var panel = new FakePanel();
        pane.Build(panel);
        panel.Toggles.Single(t => t.Text == ConfigurationPane.ServerInstallToggle).Fire(false);
        Assert.True(pane.UninstallPromptOpen);
        Assert.True(File.Exists(ServerInstallLedger.PathFor(_server)));

        panel = new FakePanel();
        pane.Build(panel);
        Assert.Contains(panel.Labels, l => l.Text == ConfigurationPane.ServerRemoveHeading);
        var body = panel.Labels.Single(l => l.Text.StartsWith(ServerInstall.PromptPrefix, StringComparison.Ordinal));
        Assert.Contains("(2 files: " + ServerInstall.BootstrapRelative + ", " + ServerInstall.CoreRelative + ")", body.Text);
        Assert.True(pane.IsServerInstallHandle(body));
        Assert.DoesNotContain(panel.Toggles, t => t.Text == ConfigurationPane.ServerInstallToggle);
        Assert.Equal(true, pane.ServerInstallReadback()["promptOpen"]);

        panel.Buttons.Single(b => b.Text == ConfigurationPane.ServerCancelButton).Fire();
        Assert.False(pane.UninstallPromptOpen);
        Assert.True(File.Exists(ServerInstallLedger.PathFor(_server)));
        Assert.Equal("installed", pane.ServerInstallReadback()["state"]);

        Assert.True(pane.RequestUninstall().Completed);
        panel = new FakePanel();
        pane.Build(panel);
        panel.Buttons.Single(b => b.Text == ConfigurationPane.ServerRemoveButton).Fire();
        Assert.False(pane.UninstallPromptOpen);
        Assert.False(File.Exists(ServerInstallLedger.PathFor(_server)));
        Assert.False(File.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "Progenitor.Bootstrap.dll")));
        Assert.Equal("not-installed", pane.ServerInstallReadback()["state"]);
        Assert.Contains("removed", pane.ServerInstallLine);
        // Revert: uninstalling straight from the toggle (no prompt) fails the ledger-still-present assert after toggle off.
    }

    [Fact]
    public void Pane_hides_the_toggle_and_shows_the_reason_while_the_server_runs()
    {
        LocalServerFolder.WriteAccessOverride = _ => true;
        var (pane, _) = Pane();
        Assert.True(pane.ApplyDraft(_server).Completed);
        Assert.True(pane.InstallProgenitorOnServer().Completed);
        string exe = Path.Combine(_server, "VRisingServer.exe");
        ServerInstall.RunningServerProbe = _ => exe;

        var panel = new FakePanel();
        pane.Build(panel);
        Assert.DoesNotContain(panel.Toggles, t => t.Text == ConfigurationPane.ServerInstallToggle);
        var note = panel.Labels.Single(l => l.Text == ConfigurationPane.ServerRunningLine);
        Assert.True(pane.IsServerInstallHandle(note));
        var readback = pane.ServerInstallReadback();
        Assert.Equal("refused", readback["state"]);
        Assert.Contains(exe, (string)readback["reason"]!);
        Assert.False(pane.RequestUninstall().Completed);
        Assert.False(pane.ConfirmUninstall().Completed);
        Assert.True(File.Exists(ServerInstallLedger.PathFor(_server)));
        // Revert: building the toggle regardless of the probe lets a click delete DLLs under a running server.
    }

    [Fact]
    public void Existing_configuration_layout_keeps_its_headings_and_folder_row()
    {
        LocalServerFolder.WriteAccessOverride = _ => true;
        var (pane, _) = Pane();
        Assert.True(pane.ApplyDraft(_server).Completed);
        var panel = new FakePanel();
        pane.Build(panel);
        // Three homes: the groups are the row kit's own headings (cell rows), Local server's a subheading row; the panel's
        // only hand-built heading is the tab's title.
        Assert.Empty(panel.Headings.Where(h => ConfigurationPane.DetailSectionHeadings.Contains(h)));
        Assert.Contains(ConfigurationPane.PreferencesTitle, panel.Headings);
        // Mods tab holds only Edit other mods' settings, which this test does not bind, so the group is not drawn here.
        Assert.Equal(new[] { ConfigurationPane.InstallTabGroup, ConfigurationPane.SourcesGroup, ConfigurationPane.UpdatesGroup },
            pane.SettingsView!.Sections.Select(s => s.Title).ToArray());
        Assert.Contains(panel.Buttons, b => b.Text == "Apply");
        Assert.Contains(panel.Buttons, b => b.Text == "Clear");
        Assert.Equal(ConfigurationLayout.Alignment, panel.ContentAlignment);
        int heading = panel.Labels.FindIndex(l => l.Text == ConfigurationPane.PrivateGameNote);
        int toggleNote = panel.Labels.FindIndex(l => l.Text == ServerInstall.NotInstalledStatus);
        Assert.True(heading >= 0 && heading < toggleNote, "the install row sits inside the Local dedicated server section");
    }

    // --- helpers ---

    // --- SERVERINST2: the prompt must survive the recompose that recreates the pane ---

    /// <summary>
    /// <c>ModsMenu.BuildConfigurationPanes</c> constructs a fresh <see cref="ConfigurationPane"/>
    /// on every recompose, including the one <c>RequestUninstall</c> asks for. Live (client
    /// gen 6, 2026-09-12) the second toggle click therefore read back <c>promptOpen false</c>
    /// with the toggle re-checked. This drives the same recreation over one folder.
    /// </summary>
    [Fact]
    public void Prompt_survives_the_recompose_that_recreates_the_pane_and_Remove_leaves_reload_alone()
    {
        LocalServerFolder.WriteAccessOverride = _ => true;
        var (pane, bindings) = Pane();
        var folder = pane.Folder;
        Assert.True(pane.ApplyDraft(_server).Completed);
        var panel = new FakePanel();
        pane.Build(panel);
        panel.Toggles.Single(t => t.Text == ConfigurationPane.ServerInstallToggle).Fire(true);
        Assert.Equal("installed", pane.ServerInstallReadback()["state"]);
        string reloadDir = Path.Combine(_server, "BepInEx", "progenitor-reload");
        string otherTool = Path.Combine(reloadDir, "OtherTool.dll");
        File.WriteAllBytes(otherTool, Bytes("not-progenitors"));

        // Recompose the way the menu host does: same bindings and folder, a new pane.
        pane = Recompose(pane, bindings, folder, out panel);
        var toggle = panel.Toggles.Single(t => t.Text == ConfigurationPane.ServerInstallToggle);
        Assert.True(toggle.Value);
        Assert.False(pane.UninstallPromptOpen);
        Assert.Equal(false, pane.ServerInstallReadback()["promptOpen"]);

        toggle.Fire(false);
        Assert.True(pane.UninstallPromptOpen);

        pane = Recompose(pane, bindings, folder, out panel);
        Assert.True(pane.UninstallPromptOpen);
        Assert.Equal(true, pane.ServerInstallReadback()["promptOpen"]);
        Assert.DoesNotContain(panel.Toggles, t => t.Text == ConfigurationPane.ServerInstallToggle);
        var heading = panel.Labels.Single(l => l.Text == ConfigurationPane.ServerRemoveHeading);
        var body = panel.Labels.Single(l => l.Text.StartsWith(ServerInstall.PromptPrefix, StringComparison.Ordinal));
        Assert.Contains("(2 files: ", body.Text);
        var remove = panel.Buttons.Single(b => b.Text == ConfigurationPane.ServerRemoveButton);
        var cancel = panel.Buttons.Single(b => b.Text == ConfigurationPane.ServerCancelButton);
        Assert.True(pane.IsServerInstallHandle(heading));
        Assert.True(pane.IsServerInstallHandle(body));
        Assert.True(pane.IsServerInstallHandle(remove));
        Assert.True(pane.IsServerInstallHandle(cancel));

        cancel.Fire();
        pane = Recompose(pane, bindings, folder, out panel);
        Assert.False(pane.UninstallPromptOpen);
        Assert.True(panel.Toggles.Single(t => t.Text == ConfigurationPane.ServerInstallToggle).Value);
        Assert.True(File.Exists(ServerInstallLedger.PathFor(_server)));

        panel.Toggles.Single(t => t.Text == ConfigurationPane.ServerInstallToggle).Fire(false);
        pane = Recompose(pane, bindings, folder, out panel);
        panel.Buttons.Single(b => b.Text == ConfigurationPane.ServerRemoveButton).Fire();
        Assert.False(pane.UninstallPromptOpen);
        Assert.False(File.Exists(ServerInstallLedger.PathFor(_server)));
        Assert.False(File.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "Progenitor.Bootstrap.dll")));
        Assert.False(File.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "progenitor-core", "Progenitor.core")));
        Assert.False(Directory.Exists(Path.Combine(_server, "BepInEx", "plugins", "Progenitor", "progenitor-core")));
        Assert.False(Directory.Exists(Path.Combine(_server, "BepInEx", "progenitor-pending")));
        Assert.True(Directory.Exists(reloadDir));
        Assert.Equal(Bytes("not-progenitors"), File.ReadAllBytes(otherTool));
        Assert.True(Directory.Exists(Path.Combine(_server, "BepInEx", "plugins")));

        pane = Recompose(pane, bindings, folder, out panel);
        Assert.False(panel.Toggles.Single(t => t.Text == ConfigurationPane.ServerInstallToggle).Value);
        Assert.Equal("not-installed", pane.ServerInstallReadback()["state"]);
        Assert.Equal(false, pane.ServerInstallReadback()["promptOpen"]);
        // Revert: keeping the prompt flag on the instance fails the promptOpen assert after the
        // second Recompose; removing the reload-folder guard deletes OtherTool.dll.
    }

    [Fact]
    public void Toggle_click_is_a_flip_of_the_real_state_and_a_vanished_ledger_drops_the_prompt()
    {
        LocalServerFolder.WriteAccessOverride = _ => true;
        var (pane, bindings) = Pane();
        var folder = pane.Folder;
        Assert.True(pane.ApplyDraft(_server).Completed);
        Assert.True(pane.InstallProgenitorOnServer().Completed);
        var panel = new FakePanel();
        pane.Build(panel);

        // A widget whose bound value fell out of step re-sends "on" while installed: still a flip.
        panel.Toggles.Single(t => t.Text == ConfigurationPane.ServerInstallToggle).Fire(true);
        Assert.True(pane.UninstallPromptOpen);
        Assert.True(File.Exists(ServerInstallLedger.PathFor(_server)));
        Assert.True(pane.CancelUninstall().Completed);
        Assert.False(pane.UninstallPromptOpen);

        // Clear drops a pending prompt with the folder.
        Assert.True(pane.RequestUninstall().Completed);
        Assert.True(pane.UninstallPromptOpen);
        Assert.True(pane.ClearFolder().Completed);
        Assert.False(pane.UninstallPromptOpen);
        Assert.True(pane.ApplyDraft(_server).Completed);
        Assert.False(pane.UninstallPromptOpen);

        // A ledger removed by other means while the prompt is pending: the recreated pane shows the toggle, unchecked.
        Assert.True(pane.RequestUninstall().Completed);
        File.Delete(ServerInstallLedger.PathFor(_server));
        pane = Recompose(pane, bindings, folder, out panel);
        Assert.False(pane.UninstallPromptOpen);
        Assert.False(panel.Toggles.Single(t => t.Text == ConfigurationPane.ServerInstallToggle).Value);
        Assert.DoesNotContain(panel.Labels, l => l.Text == ConfigurationPane.ServerRemoveHeading);

        // Not installed, the widget re-sends "off": still a flip, so it installs.
        panel.Toggles.Single(t => t.Text == ConfigurationPane.ServerInstallToggle).Fire(false);
        Assert.Equal("installed", pane.ServerInstallReadback()["state"]);
        // Revert: routing on the kit value (on => install, off => prompt) fails the first promptOpen assert.
    }

    /// <summary>Dispose the pane and build a new one over the same bindings and folder, as <c>ModsMenu.BuildConfigurationPanes</c> does.</summary>
    private ConfigurationPane Recompose(ConfigurationPane previous, ModsConfigBindings bindings, LocalServerFolder folder, out FakePanel panel)
    {
        previous.Dispose();
        var pane = new ConfigurationPane(bindings, folder, new object(), scan: null, source: _source);
        panel = new FakePanel();
        pane.Build(panel);
        return pane;
    }

    private (ConfigurationPane Pane, ModsConfigBindings Bindings) Pane()
    {
        string cfgDir = Path.Combine(_root, "cfg");
        Directory.CreateDirectory(cfgDir);
        var file = new ConfigFile(Path.Combine(cfgDir, ConfigHelper.SharedFileName), saveOnInit: true);
        var bindings = ConfigurationPane.Bind(file);
        var pane = new ConfigurationPane(bindings, new LocalServerFolder(), new object(), scan: null, source: _source);
        return (pane, bindings);
    }

    private (CatalogEntry Entry, PackageInspection Inspection, string Zip) VerifiedSample()
    {
        byte[] dll = Bytes("fake-dll:sample:" + new string('X', 64));
        string dllSha = Sha(dll);
        string json = "{\"id\":\"example.sample\",\"version\":\"1.0.0\",\"sha256\":\"" + dllSha + "\",\"side\":\"server\",\"hotLoadable\":false}";
        string zip = Path.Combine(_root, "pkg-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var file = new FileStream(zip, FileMode.Create, FileAccess.Write))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            using (var s = archive.CreateEntry("progenitor.json", CompressionLevel.NoCompression).Open())
                s.Write(Encoding.UTF8.GetBytes(json));
            using (var s = archive.CreateEntry("example.sample.dll", CompressionLevel.NoCompression).Open())
                s.Write(dll);
        }

        var inspection = PackageInspector.InspectFile(zip);
        Assert.True(inspection.Ok, inspection.Refusal);
        var entry = new CatalogEntry(
            new ManifestMod("", "1.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Exact, false, "team", "team-Sample", null, 1, "https://thunderstore.io/package/download/team/Sample/1.0.0/"),
            CatalogProvenance.AdaptedListing,
            InstallReasons.SourceThunderstore,
            InstallReasons.SourceLabelThunderstore,
            "stamp",
            displayName: "Sample",
            author: "team",
            downloadUrl: "https://thunderstore.io/package/download/team/Sample/1.0.0/",
            listingFileSize: 1,
            owner: "team",
            packageName: "Sample");
        entry = InstallCatalogService.ApplyInspection(entry, inspection);
        return (entry, inspection, zip);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Sha(byte[] bytes) => CatalogFetcher.ToHex(SHA256.HashData(bytes));

    private sealed class FakePanel : IUiPanel
    {
        public List<FakeLabel> Labels { get; } = new();
        public List<FakeButton> Buttons { get; } = new();
        public List<FakeToggle> Toggles { get; } = new();
        public List<string> Headings { get; } = new();
        public string ContentAlignment { get; set; } = SettingsAlign.Centre;
        public string Align { get; set; } = SettingsAlign.Centre;
        public string Text { get; set; } = "Configuration";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public UiCaptureMode CaptureMode { get; set; }

        public IUiButton Button(string text)
        {
            var b = new FakeButton { Text = text ?? "" };
            Buttons.Add(b);
            return b;
        }

        public IUiInput Input(string text, string initial = "") => new FakeInput { Text = text ?? "", Value = initial ?? "" };

        public IUiLabel Label(string text)
        {
            var l = new FakeLabel { Text = text ?? "" };
            Labels.Add(l);
            return l;
        }

        public IUiLabel Heading(string text)
        {
            Headings.Add(text ?? "");
            return Label(text);
        }

        public IUiLabel Note(string text) => Label(text);

        public IUiToggle Toggle(string text, bool initial = false)
        {
            var t = new FakeToggle { Text = text ?? "", Value = initial };
            Toggles.Add(t);
            return t;
        }

        public IUiSlider Slider(string text, float min, float max, float initial) => throw new NotSupportedException();
        public void BeginInline() { }
        public void EndInline() { }
        public void Show() { }
        public void Hide() { }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeLabel : IUiLabel
    {
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeButton : IUiButton
    {
        private Action? _handler;
        public string Text { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public IDisposable OnClick(Action handler) { _handler = handler; return this; }
        public void Fire() => _handler?.Invoke();
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeToggle : IUiToggle
    {
        private Action<bool>? _handler;
        public string Text { get; set; } = "";
        public bool Value { get; set; }
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public IDisposable OnChanged(Action<bool> handler) { _handler = handler; return this; }
        public void Fire(bool value) { Value = value; _handler?.Invoke(value); }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeInput : IUiInput
    {
        public string Text { get; set; } = "";
        public string Value { get; set; } = "";
        public bool Visible { get; set; } = true;
        public bool Enabled => true;
        public bool IsDisposed { get; private set; }
        public UiHandleState State => UiHandleState.Built;
        public string PluginId => "test";
        public string RootName => "test";
        public IDisposable OnChanged(Action<string> handler) => this;
        public void Dispose() => IsDisposed = true;
    }
}
