using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Progenitor.SelfUpdate;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// AUTOUPDATE5, game-free: Bootstrap's start-loop guard (<see cref="BootHealth"/>). Each start that runs exactly the
/// journal's new images is counted; after <see cref="BootHealthPolicy.FailedStartsBeforeRestore"/> starts without Core's
/// healthy mark the next start restores the previous Core (now) and Bootstrap (staged as <c>.next</c>), marks the version
/// failed and writes the record back. A staged release's Core is put in place by its new Bootstrap. Another copy deployed
/// over the release is never rolled back.
/// </summary>
[Collection("SelfUpdates")]
public sealed class BootHealthTests : IDisposable
{
    private readonly string _root;
    private readonly string _boot;
    private readonly List<string> _info = new();
    private readonly List<string> _errors = new();

    public BootHealthTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "pboot-" + Guid.NewGuid().ToString("N").Substring(0, 10), "BepInEx");
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
        Directory.CreateDirectory(Path.Combine(_root, "progenitor-core"));
        _boot = Path.Combine(_root, "plugins", SelfPaths.BootstrapFile);
        BootHealth.BootstrapPathHint = _boot;
    }

    public void Dispose()
    {
        BootHealth.BootstrapPathHint = null;
        try { Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true); }
        catch { /* temp */ }
    }

    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>A kept release folder with its Core, Bootstrap, a table and release.json.</summary>
    private string Keep(string version, string core, string boot, string source = "thunderstore")
    {
        string dir = Path.Combine(SelfPaths.Store(_root), SelfPaths.KeptDirName(version, SelfFiles.Sha256(B(core))));
        Directory.CreateDirectory(Path.Combine(dir, SelfPaths.TablesFolder));
        File.WriteAllBytes(Path.Combine(dir, SelfPaths.CoreFile), B(core));
        File.WriteAllBytes(Path.Combine(dir, SelfPaths.BootstrapFile), B(boot));
        File.WriteAllText(Path.Combine(dir, SelfPaths.TablesFolder, "client-1.json"), version);
        File.WriteAllText(Path.Combine(dir, SelfPaths.KeptInfoName), new SelfKept
        {
            Version = version, Package = "Progenitor-Progenitor", CoreSha = SelfFiles.Sha256(B(core)), BootstrapSha = SelfFiles.Sha256(B(boot)),
            Protocol = 5, Source = source, KeptUtc = DateTime.UtcNow,
        }.ToJson());
        return SelfPaths.Relative(_root, dir);
    }

    private void Journal(string state, int starts, bool newBootstrap)
    {
        var j = new SelfJournal
        {
            State = state,
            Version = "2026.10.14",
            Previous = "2026.10.12",
            NewDir = Keep("2026.10.14", "core-new", "boot-new"),
            PrevDir = Keep("2026.10.12", "core-old", "boot-old"),
            NewCore = SelfFiles.Sha256(B("core-new")),
            NewBootstrap = newBootstrap ? SelfFiles.Sha256(B("boot-new")) : "",
            PrevCore = SelfFiles.Sha256(B("core-old")),
            PrevBootstrap = SelfFiles.Sha256(B("boot-old")),
            Route = newBootstrap ? "restart" : "hot",
            Starts = starts,
            Utc = DateTime.UtcNow,
        };
        SelfFiles.EditState(_root, s => { s.Journal = j; return true; }, out _);
    }

    private string Run() => BootHealth.RunAtStart(_root, _boot, _info.Add, _errors.Add);

    private SelfJournal J() => SelfFiles.ReadState(_root).Journal!;

    private string Disk(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    [Fact]
    public void The_policy_counts_only_the_journal_s_images_and_restores_after_two_failed_starts()
    {
        var j = new SelfJournal { State = SelfJournal.Trial, NewCore = "c", NewBootstrap = "b", Starts = 0 };
        Assert.Equal(BootAction.Count, BootHealthPolicy.Decide(j, "b", "c"));
        j.Starts = 1;
        Assert.Equal(BootAction.Count, BootHealthPolicy.Decide(j, "b", "c"));
        j.Starts = BootHealthPolicy.FailedStartsBeforeRestore;
        Assert.Equal(BootAction.Restore, BootHealthPolicy.Decide(j, "B", "C"));
        Assert.Equal(BootAction.Superseded, BootHealthPolicy.Decide(j, "b", "other"));
        Assert.Equal(BootAction.Superseded, BootHealthPolicy.Decide(j, "old", "c"));
        Assert.Equal(BootAction.Superseded, BootHealthPolicy.Decide(j, "b", ""));

        var staged = new SelfJournal { State = SelfJournal.Staged, NewCore = "c", NewBootstrap = "b" };
        Assert.Equal(BootAction.None, BootHealthPolicy.Decide(staged, "old", "x"));
        Assert.Equal(BootAction.Place, BootHealthPolicy.Decide(staged, "b", "x"));

        var hot = new SelfJournal { State = SelfJournal.Swapping, NewCore = "c", PrevBootstrap = "b0", Starts = 1 };
        Assert.Equal(BootAction.Count, BootHealthPolicy.Decide(hot, "b0", "c"));
        Assert.Equal(BootAction.Superseded, BootHealthPolicy.Decide(hot, "other", "c"));
        Assert.Equal(BootAction.Count, BootHealthPolicy.Decide(hot, "b0", "old", "c", ""));
        Assert.Equal(BootAction.Superseded, BootHealthPolicy.Decide(hot, "b0", "c", "deploy", ""));
        Assert.Equal(BootAction.None, BootHealthPolicy.Decide(new SelfJournal { State = SelfJournal.Ok }, "b", "c"));
        Assert.Equal(BootAction.None, BootHealthPolicy.Decide(null, "b", "c"));
    }

    [Fact]
    public void No_journal_is_a_no_op()
    {
        Assert.Equal("no self-update journal", Run());
        Assert.False(File.Exists(SelfPaths.State(_root)));
        Assert.Empty(_errors);
    }

    [Fact]
    public void A_staged_release_waits_for_its_Bootstrap_then_places_its_Core_and_starts_the_trial()
    {
        File.WriteAllText(_boot, "boot-old");
        File.WriteAllText(SelfPaths.CoreDll(_root), "core-old");
        Journal(SelfJournal.Staged, 0, newBootstrap: true);

        // The previous Bootstrap still runs this launch.
        Assert.StartsWith("waiting", Run());
        Assert.Equal(SelfJournal.Staged, J().State);
        Assert.Equal("core-old", Disk(SelfPaths.CoreDll(_root)));

        // Next launch: the new Bootstrap runs; it puts the release's Core (and tables) in place before Core loads.
        File.WriteAllText(_boot, "boot-new");
        Assert.Equal("placed", Run());
        Assert.Equal("core-new", Disk(SelfPaths.CoreDll(_root)));
        Assert.Equal("2026.10.14", Disk(Path.Combine(SelfPaths.Tables(_root), "client-1.json")));
        Assert.Equal(SelfJournal.Trial, J().State);
        Assert.Equal(1, J().Starts);
    }

    [Fact]
    public void Two_starts_without_the_healthy_mark_restore_the_previous_copy_and_mark_the_version_failed()
    {
        File.WriteAllText(_boot, "boot-new");
        File.WriteAllText(SelfPaths.CoreDll(_root), "core-new");
        Journal(SelfJournal.Trial, 1, newBootstrap: true);
        SelfFiles.WriteRecord(_root, new SelfInstallRecord { Version = "2026.10.12", CoreSha = SelfFiles.Sha256(B("core-old")), BootstrapSha = SelfFiles.Sha256(B("boot-old")) }, out _);

        Assert.Equal("counted 2", Run());
        Assert.Equal(2, J().Starts);
        Assert.Equal("core-new", Disk(SelfPaths.CoreDll(_root)));

        Assert.Equal("restored", Run());
        Assert.Equal("core-old", Disk(SelfPaths.CoreDll(_root)));
        Assert.Equal("boot-old", Disk(_boot + ".next"));
        Assert.Equal("2026.10.12", Disk(Path.Combine(SelfPaths.Tables(_root), "client-1.json")));
        var state = SelfFiles.ReadState(_root);
        Assert.Equal(SelfJournal.RolledBack, state.Journal!.State);
        Assert.True(state.IsFailed("2026.10.14"));
        Assert.Equal("2026.10.12", SelfFiles.ReadRecord(_root)!.Version);
        Assert.Contains(_errors, e => e.Contains("rolled back") && e.Contains("not applied automatically again"));

        // Rolled back: later starts do nothing.
        Assert.Equal("no update in progress", Run());
    }

    [Fact]
    public void A_core_only_release_restores_Core_and_stages_no_Bootstrap()
    {
        File.WriteAllText(_boot, "boot-old");
        File.WriteAllText(SelfPaths.CoreDll(_root), "core-new");
        Journal(SelfJournal.Swapping, BootHealthPolicy.FailedStartsBeforeRestore, newBootstrap: false);
        Assert.Equal("restored", Run());
        Assert.Equal("core-old", Disk(SelfPaths.CoreDll(_root)));
        Assert.False(File.Exists(_boot + ".next"));
    }

    [Fact]
    public void Another_copy_deployed_over_the_release_is_never_rolled_back()
    {
        File.WriteAllText(_boot, "boot-new");
        File.WriteAllText(SelfPaths.CoreDll(_root), "core-from-a-developer-deploy");
        Journal(SelfJournal.Trial, BootHealthPolicy.FailedStartsBeforeRestore, newBootstrap: true);
        Assert.Equal("superseded", Run());
        Assert.Equal("core-from-a-developer-deploy", Disk(SelfPaths.CoreDll(_root)));
        Assert.Equal(SelfJournal.Superseded, J().State);
        Assert.False(SelfFiles.ReadState(_root).IsFailed("2026.10.14"));
    }

    [Fact]
    public void A_pending_deploy_supersedes_the_trial_and_is_never_touched()
    {
        File.WriteAllText(_boot, "boot-new");
        File.WriteAllText(SelfPaths.CoreDll(_root), "core-new");
        Journal(SelfJournal.Trial, BootHealthPolicy.FailedStartsBeforeRestore, newBootstrap: true);
        File.WriteAllText(SelfPaths.CoreNext(_root), "core-from-deploy");
        File.WriteAllText(_boot + ".next", "boot-from-deploy");
        Assert.Equal("superseded", Run());
        Assert.Equal("core-from-deploy", Disk(SelfPaths.CoreNext(_root)));
        Assert.Equal("boot-from-deploy", Disk(_boot + ".next"));
        Assert.Equal("core-new", Disk(SelfPaths.CoreDll(_root)));
    }

    [Fact]
    public void A_swap_that_crashed_before_it_ran_is_judged_on_the_pending_Core()
    {
        File.WriteAllText(_boot, "boot-old");
        File.WriteAllText(SelfPaths.CoreDll(_root), "core-old");
        Journal(SelfJournal.Swapping, 1, newBootstrap: false);
        File.WriteAllText(SelfPaths.CoreNext(_root), "core-new");
        Assert.Equal("counted 2", Run());

        // A Bootstrap-only deploy under a hot release is another copy, not the release.
        File.WriteAllText(_boot, "boot-from-deploy");
        Assert.Equal("superseded", Run());
    }

    [Fact]
    public void A_clean_exit_of_a_process_that_ran_the_release_is_not_a_failed_start()
    {
        Journal(SelfJournal.Trial, 1, newBootstrap: true);
        string bootNew = SelfFiles.Sha256(B("boot-new")), coreNew = SelfFiles.Sha256(B("core-new"));
        Assert.Equal("not judged", BootHealth.NoteCleanExit(_root, bootNew, SelfFiles.Sha256(B("core-old"))));
        Assert.StartsWith("clean exit", BootHealth.NoteCleanExit(_root, bootNew, coreNew));
        Assert.Equal(0, J().Starts);
        Assert.Equal("not judged", BootHealth.NoteCleanExit(_root, bootNew, coreNew));
    }

    [Fact]
    public void A_staged_release_whose_Core_cannot_be_placed_restores_the_previous_Bootstrap()
    {
        File.WriteAllText(_boot, "boot-new");
        File.WriteAllText(SelfPaths.CoreDll(_root), "core-old");
        Journal(SelfJournal.Staged, 0, newBootstrap: true);
        File.Delete(Path.Combine(SelfPaths.Absolute(_root, J().NewDir), SelfPaths.CoreFile));
        Assert.StartsWith("failed: kept Core unreadable", Run());
        Assert.Equal("boot-old", Disk(_boot + ".next"));
        Assert.Equal(SelfJournal.RolledBack, J().State);
        Assert.True(SelfFiles.ReadState(_root).IsFailed("2026.10.14"));
    }

    [Fact]
    public void A_kept_Core_that_changed_on_disk_is_never_placed()
    {
        File.WriteAllText(_boot, "boot-new");
        File.WriteAllText(SelfPaths.CoreDll(_root), "core-old");
        Journal(SelfJournal.Staged, 0, newBootstrap: true);
        File.WriteAllText(Path.Combine(SelfPaths.Absolute(_root, J().NewDir), SelfPaths.CoreFile), "tampered");
        Assert.StartsWith("failed", Run());
        Assert.Equal("core-old", Disk(SelfPaths.CoreDll(_root)));
        Assert.Equal(SelfJournal.RolledBack, J().State);
    }
}
