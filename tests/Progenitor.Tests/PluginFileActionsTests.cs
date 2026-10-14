using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.PluginActions;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// SETTINGS1 stage 3: the preloader's next-start plugin actions (PluginFileActions.cs, compiled
/// into this assembly as-is). Every test builds its own BepInEx tree under the temp folder and
/// deletes it afterwards. Game-free.
/// </summary>
public sealed class PluginFileActionsTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 9, 29, 12, 34, 56, DateTimeKind.Utc);

    private readonly string _base;
    private readonly string _root;

    public PluginFileActionsTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "progenitor-pfa-tests-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(_base, "BepInEx");
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(_root, "core"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    // ------------------------------------------------------------------ helpers

    private string Full(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private string Put(string rel, string content)
    {
        string full = Full(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>plugins/Author-Mod with Companion.dll, Mod.dll and a config file.</summary>
    private string MakeMod(string folder = "Author-Mod")
    {
        Put($"plugins/{folder}/Companion.dll", "companion bytes");
        Put($"plugins/{folder}/mod.cfg", "setting = 1");
        return Put($"plugins/{folder}/Mod.dll", "mod bytes");
    }

    private static LoadedPluginFact Fact(string id, string location, params string[] deps) =>
        new(id, id + " name", location, deps);

    private PluginIdentification IdentifyAlone(string id, string location) =>
        PluginFileActions.Identify(_root, id, location, new[] { Fact(id, location) });

    private PluginFileRequest RequestFor(string id, PluginFileAction action, PluginIdentification ident) => new()
    {
        PluginId = id,
        Name = id + " name",
        Action = action,
        Folder = ident.Folder,
        Files = new List<PluginFileEntry>(ident.Files),
        RequestedUtc = Now.ToString("o"),
        Loose = ident.Loose,
    };

    private PluginFileState ReadState()
    {
        Assert.True(PluginFileActions.TryRead(_root, out var state, out string error), error);
        return state;
    }

    private void Submit(PluginFileRequest request)
    {
        Assert.True(PluginFileActions.Request(_root, request, out string error), error);
    }

    // ------------------------------------------------------------------ Identify

    [Fact]
    public void Identify_ModAloneInItsFolder_IsCertainWithEveryFile()
    {
        string location = MakeMod();

        var ident = IdentifyAlone("author.mod", location);

        Assert.True(ident.Certain, ident.Why);
        Assert.Equal("", ident.Why);
        Assert.Equal("plugins/Author-Mod", ident.Folder);
        Assert.Equal(
            new[] { "plugins/Author-Mod/Companion.dll", "plugins/Author-Mod/Mod.dll", "plugins/Author-Mod/mod.cfg" },
            ident.Files.Select(f => f.Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        foreach (var f in ident.Files)
        {
            if (PluginFileActions.IsDll(f.Path))
                Assert.Equal(PluginFileActions.Sha256Of(Full(f.Path)), f.Sha256);
            else
                Assert.Equal("", f.Sha256);
        }

        Assert.Equal(2, ident.Dlls.Count);
        Assert.All(ident.Dlls, d => Assert.Equal(64, d.Sha256.Length));
    }

    [Fact]
    public void Identify_LooseDllWithoutPluginMetadata_IsRefused()
    {
        string location = Put("plugins/Loose.dll", "loose");
        var ident = IdentifyAlone("loose", location);
        Assert.False(ident.Certain);
        Assert.Equal(PluginFileActions.WhyNoPluginInFile(location), ident.Why);
        Assert.Empty(ident.Files);
    }

    [Fact]
    public void Identify_FolderSharedWithAnotherLoadedPlugin_NamesIt()
    {
        string location = MakeMod();
        string other = Put("plugins/Author-Mod/sub/Other.dll", "other");
        var ident = PluginFileActions.Identify(_root, "author.mod", location,
            new[] { Fact("author.mod", location), new LoadedPluginFact("other.mod", "Other Mod", other, null) });
        Assert.False(ident.Certain);
        Assert.Equal(PluginFileActions.WhyShared("Other Mod"), ident.Why);
    }

    [Fact]
    public void Identify_HardDependencyOfAnotherPlugin_NamesTheDependent()
    {
        string location = MakeMod();
        string other = Put("plugins/Other-Mod/Other.dll", "other");
        var ident = PluginFileActions.Identify(_root, "author.mod", location,
            new[] { Fact("author.mod", location), new LoadedPluginFact("other.mod", "Other Mod", other, new[] { "Author.Mod" }) });
        Assert.False(ident.Certain);
        Assert.Equal(PluginFileActions.WhyNeededBy("Other Mod"), ident.Why);
    }

    [Fact]
    public void Identify_OutsidePlugins_IsRefused()
    {
        string inCore = Put("core/Some.dll", "core");
        Assert.Equal(PluginFileActions.WhyNotInPlugins, IdentifyAlone("some", inCore).Why);

        string elsewhere = Path.Combine(_base, "Elsewhere", "Mod.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(elsewhere)!);
        File.WriteAllText(elsewhere, "x");
        var ident = IdentifyAlone("some", elsewhere);
        Assert.False(ident.Certain);
        Assert.Equal(PluginFileActions.WhyNotInPlugins, ident.Why);
    }

    [Fact]
    public void Identify_ProgenitorFolderFileOrId_IsRefused()
    {
        string inFolder = Put("plugins/ProgenitorExtras/Extra.dll", "x");
        Assert.Equal(PluginFileActions.WhyProgenitor, IdentifyAlone("extra", inFolder).Why);

        string named = Put("plugins/Author-Thing/Progenitor.Thing.dll", "x");
        Assert.Equal(PluginFileActions.WhyProgenitor, IdentifyAlone("thing", named).Why);

        string location = MakeMod();
        var byId = IdentifyAlone("progenitor.something", location);
        Assert.False(byId.Certain);
        Assert.Equal(PluginFileActions.WhyProgenitor, byId.Why);
    }

    [Fact]
    public void Identify_EmptyLocation_IsRefused()
    {
        var ident = PluginFileActions.Identify(_root, "author.mod", "", null);
        Assert.False(ident.Certain);
        Assert.Equal(PluginFileActions.WhyNoLocation, ident.Why);
    }

    // ------------------------------------------------------------------ state file

    [Fact]
    public void SerializeAndTryParse_PreserveEveryField()
    {
        var state = new PluginFileState();
        state.Requests.Add(new PluginFileRequest
        {
            PluginId = "author.mod",
            Name = "Author Mod",
            Action = PluginFileAction.Enable,
            Folder = "plugins/Author-Mod",
            RequestedUtc = "2026-09-29T12:00:00.0000000Z",
            Files = new List<PluginFileEntry> { new("plugins/Author-Mod/Mod.dll", "ABCDEF"), new("plugins/Author-Mod/mod.cfg", "") },
        });
        state.Results.Add(new PluginFileResult
        {
            PluginId = "other.mod",
            Name = "Other Mod",
            Action = PluginFileAction.Remove,
            Done = true,
            Reason = "some reason",
            CompletedUtc = "2026-09-29T12:34:56.0000000Z",
            Folder = "plugins/Other-Mod",
            RemovedTo = "progenitor-removed/20260929-123456/Other-Mod",
            Files = new List<PluginFileEntry> { new("plugins/Other-Mod/Other.dll", "0123") },
        });

        Assert.True(PluginFileActions.TryParse(PluginFileActions.Serialize(state), out var back, out string error), error);

        var rq = Assert.Single(back.Requests);
        Assert.Equal("author.mod", rq.PluginId);
        Assert.Equal("Author Mod", rq.Name);
        Assert.Equal(PluginFileAction.Enable, rq.Action);
        Assert.Equal("plugins/Author-Mod", rq.Folder);
        Assert.Equal("2026-09-29T12:00:00.0000000Z", rq.RequestedUtc);
        Assert.Equal(new[] { "plugins/Author-Mod/Mod.dll", "plugins/Author-Mod/mod.cfg" }, rq.Files.Select(f => f.Path));
        Assert.Equal(new[] { "abcdef", "" }, rq.Files.Select(f => f.Sha256));

        var rs = Assert.Single(back.Results);
        Assert.Equal("other.mod", rs.PluginId);
        Assert.Equal("Other Mod", rs.Name);
        Assert.Equal(PluginFileAction.Remove, rs.Action);
        Assert.True(rs.Done);
        Assert.Equal("some reason", rs.Reason);
        Assert.Equal("2026-09-29T12:34:56.0000000Z", rs.CompletedUtc);
        Assert.Equal("plugins/Other-Mod", rs.Folder);
        Assert.Equal("progenitor-removed/20260929-123456/Other-Mod", rs.RemovedTo);
        var file = Assert.Single(rs.Files);
        Assert.Equal("plugins/Other-Mod/Other.dll", file.Path);
        Assert.Equal("0123", file.Sha256);
    }

    [Fact]
    public void RequestCancelPendingFor_RoundTripThroughTheFile()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        Submit(RequestFor("author.mod", PluginFileAction.Disable, ident));
        Submit(RequestFor("author.mod", PluginFileAction.Remove, ident));

        var state = ReadState();
        var pending = PluginFileActions.PendingFor(state, "AUTHOR.MOD");
        Assert.NotNull(pending);
        Assert.Single(state.Requests);
        Assert.Equal(PluginFileAction.Remove, pending!.Action);
        Assert.Equal(ident.Files.Count, pending.Files.Count);
        Assert.Null(PluginFileActions.PendingFor(state, "other.mod"));

        Assert.True(PluginFileActions.Cancel(_root, "author.mod", out string error), error);
        Assert.Null(PluginFileActions.PendingFor(ReadState(), "author.mod"));
        Assert.False(PluginFileActions.Cancel(_root, "author.mod", out _));
    }

    // ------------------------------------------------------------------ RunAll

    [Fact]
    public void RunAll_DisableThenEnable_RenamesEveryFileLikeR2modmanAndReconciles()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        Submit(RequestFor("author.mod", PluginFileAction.Disable, ident));

        var results = PluginFileActions.RunAll(_root, Now, null, null);

        var r = Assert.Single(results);
        Assert.True(r.Done, r.Reason);
        Assert.False(File.Exists(Full("plugins/Author-Mod/Mod.dll")));
        Assert.False(File.Exists(Full("plugins/Author-Mod/Companion.dll")));
        Assert.True(File.Exists(Full("plugins/Author-Mod/Mod.dll.old")));
        Assert.True(File.Exists(Full("plugins/Author-Mod/Companion.dll.old")));
        Assert.False(File.Exists(Full("plugins/Author-Mod/mod.cfg")));
        Assert.Equal("setting = 1", File.ReadAllText(Full("plugins/Author-Mod/mod.cfg.old")));

        var state = ReadState();
        Assert.Empty(state.Requests);
        var saved = Assert.Single(state.Results);
        Assert.True(saved.Done);
        Assert.Equal(PluginFileAction.Disable, saved.Action);
        Assert.Equal("author.mod", Assert.Single(PluginFileActions.DisabledOnDisk(_root, state)).PluginId);

        Submit(RequestFor("author.mod", PluginFileAction.Enable, ident));
        var enabled = Assert.Single(PluginFileActions.RunAll(_root, Now.AddMinutes(1), null, null));
        Assert.True(enabled.Done, enabled.Reason);
        Assert.Equal("mod bytes", File.ReadAllText(Full("plugins/Author-Mod/Mod.dll")));
        Assert.Equal("companion bytes", File.ReadAllText(Full("plugins/Author-Mod/Companion.dll")));
        Assert.False(File.Exists(Full("plugins/Author-Mod/Mod.dll.old")));
        Assert.Equal("setting = 1", File.ReadAllText(Full("plugins/Author-Mod/mod.cfg")));

        state = ReadState();
        Assert.Empty(state.Requests);
        Assert.Single(state.Results);
        Assert.Empty(PluginFileActions.DisabledOnDisk(_root, state));
    }

    [Fact]
    public void RunAll_Remove_MovesTheFolderAndSuffixesASecondSameNamedFolder()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        Submit(RequestFor("author.mod", PluginFileAction.Remove, ident));

        var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));
        Assert.True(r.Done, r.Reason);
        Assert.Equal("progenitor-removed/20260929-123456/Author-Mod", r.RemovedTo);
        Assert.False(Directory.Exists(Full("plugins/Author-Mod")));
        Assert.Equal("mod bytes", File.ReadAllText(Full("progenitor-removed/20260929-123456/Author-Mod/Mod.dll")));
        Assert.Equal("progenitor-removed/20260929-123456/Author-Mod", Assert.Single(ReadState().Results).RemovedTo);

        var again = IdentifyAlone("author.mod2", MakeMod());
        Submit(RequestFor("author.mod2", PluginFileAction.Remove, again));
        var r2 = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));
        Assert.True(r2.Done, r2.Reason);
        Assert.Equal("progenitor-removed/20260929-123456/Author-Mod-2", r2.RemovedTo);
        Assert.True(Directory.Exists(Full("progenitor-removed/20260929-123456/Author-Mod")));
        Assert.True(File.Exists(Full("progenitor-removed/20260929-123456/Author-Mod-2/Mod.dll")));
    }

    [Fact]
    public void RunAll_DllChangedAfterRequest_IsNotRenamed()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        Submit(RequestFor("author.mod", PluginFileAction.Disable, ident));
        File.WriteAllText(Full("plugins/Author-Mod/Mod.dll"), "edited bytes");

        var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));

        Assert.False(r.Done);
        Assert.Equal(PluginFileActions.WhyChanged(Full("plugins/Author-Mod/Mod.dll")), r.Reason);
        Assert.StartsWith("Mod.dll ", r.Reason);
        Assert.True(File.Exists(Full("plugins/Author-Mod/Mod.dll")));
        Assert.True(File.Exists(Full("plugins/Author-Mod/Companion.dll")));
        Assert.False(File.Exists(Full("plugins/Author-Mod/Mod.dll.old")));
        Assert.False(File.Exists(Full("plugins/Author-Mod/Companion.dll.old")));

        var state = ReadState();
        Assert.Empty(state.Requests);
        var saved = Assert.Single(state.Results);
        Assert.False(saved.Done);
        Assert.Equal(r.Reason, saved.Reason);
    }

    [Fact]
    public void RunAll_MissingFile_IsReported()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        Submit(RequestFor("author.mod", PluginFileAction.Disable, ident));
        File.Delete(Full("plugins/Author-Mod/Companion.dll"));

        var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));

        Assert.False(r.Done);
        Assert.Equal(PluginFileActions.WhyMissing(Full("plugins/Author-Mod/Companion.dll")), r.Reason);
        Assert.True(File.Exists(Full("plugins/Author-Mod/Mod.dll")));
        Assert.False(File.Exists(Full("plugins/Author-Mod/Mod.dll.old")));
        var state = ReadState();
        Assert.Empty(state.Requests);
        Assert.False(Assert.Single(state.Results).Done);
    }

    [Fact]
    public void RunAll_OldFileInTheWay_RenamesNothingAtAll()
    {
        // Companion.dll sorts before Mod.dll, so it passes its checks first; the in-the-way check on
        // the second DLL must still leave the first one untouched (all or nothing).
        var ident = IdentifyAlone("author.mod", MakeMod());
        Assert.Equal("plugins/Author-Mod/Companion.dll", ident.Dlls[0].Path);
        Submit(RequestFor("author.mod", PluginFileAction.Disable, ident));
        Put("plugins/Author-Mod/Mod.dll.old", "someone else's");

        var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));

        Assert.False(r.Done);
        Assert.Equal(PluginFileActions.WhyInTheWay(Full("plugins/Author-Mod/Mod.dll.old")), r.Reason);
        Assert.Equal("companion bytes", File.ReadAllText(Full("plugins/Author-Mod/Companion.dll")));
        Assert.False(File.Exists(Full("plugins/Author-Mod/Companion.dll.old")));
        Assert.Equal("mod bytes", File.ReadAllText(Full("plugins/Author-Mod/Mod.dll")));
        Assert.Equal("someone else's", File.ReadAllText(Full("plugins/Author-Mod/Mod.dll.old")));
        var state = ReadState();
        Assert.Empty(state.Requests);
        Assert.False(Assert.Single(state.Results).Done);
    }

    public static IEnumerable<object[]> UnsafeRequests()
    {
        // xUnit theory arguments must be public types; the action travels as its int value.
        foreach (var action in new[] { (int)PluginFileAction.Disable, (int)PluginFileAction.Remove })
        {
            yield return new object[] { action, "plugins", "plugins/Loose.dll" };
            yield return new object[] { action, "config", "config/Thing.dll" };
            yield return new object[] { action, "plugins/../core", "core/Core.dll" };
            yield return new object[] { action, "plugins/ProgenitorX", "plugins/ProgenitorX/X.dll" };
            yield return new object[] { action, "plugins/Author-Mod", "plugins/Other-Mod/Other.dll" };
            yield return new object[] { action, "plugins/Author-Mod", "plugins/Author-Mod/../Other-Mod/Other.dll" };
        }
    }

    [Theory]
    [MemberData(nameof(UnsafeRequests))]
    public void RunAll_UnsafeRequest_TouchesNothing(int action, string folder, string file)
    {
        var watched = new[]
        {
            "plugins/Loose.dll", "config/Thing.dll", "core/Core.dll", "plugins/ProgenitorX/X.dll",
            "plugins/Other-Mod/Other.dll", "plugins/Author-Mod/Mod.dll",
        };
        foreach (var w in watched) Put(w, "bytes of " + w);

        var request = new PluginFileRequest
        {
            PluginId = "evil.mod",
            Name = "Evil",
            Action = (PluginFileAction)action,
            Folder = folder,
            Files = new List<PluginFileEntry> { new(file, PluginFileActions.Sha256Of(Full(file))) },
        };
        Submit(request);

        var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));

        Assert.False(r.Done);
        Assert.Equal(PluginFileActions.WhyUnsafeRequest, r.Reason);
        foreach (var w in watched)
        {
            Assert.Equal("bytes of " + w, File.ReadAllText(Full(w)));
            Assert.False(File.Exists(Full(w) + ".old"), w + ".old");
        }

        Assert.False(Directory.Exists(Full("progenitor-removed")));
        Assert.Empty(ReadState().Requests);
    }

    [Fact]
    public void IsProtected_CoversCoreProgenitorAndBepInExSegments()
    {
        Assert.True(PluginFileActions.IsProtected(_root, Full("core/x.dll"), out string why));
        Assert.Equal(PluginFileActions.WhyNotInPlugins, why);
        Assert.True(PluginFileActions.IsProtected(_root, Full("plugins/Progenitor.Bootstrap.dll"), out why));
        Assert.Equal(PluginFileActions.WhyProgenitor, why);
        Assert.True(PluginFileActions.IsProtected(_root, Full("plugins/BepInExPack/x.dll"), out why));
        Assert.Equal(PluginFileActions.WhyProgenitor, why);
        Assert.True(PluginFileActions.IsProtected(_root, Full("plugins"), out _));
        Assert.False(PluginFileActions.IsProtected(_root, Full("plugins/Author-Mod/Mod.dll"), out why));
        Assert.Equal("", why);
    }

    [Fact]
    public void RunAll_UnreadableStateFile_ChangesNothing()
    {
        MakeMod();
        string statePath = PluginFileActions.StatePath(_root);
        const string corrupt = "{\"requests\": [ {\"pluginId\": \"author.mod\", \"action\": \"remove\", \"folder\": \"plugins/Author-Mod\"";
        File.WriteAllText(statePath, corrupt);
        var warnings = new List<string>();

        var results = PluginFileActions.RunAll(_root, Now, null, warnings.Add);

        Assert.Empty(results);
        Assert.Single(warnings);
        Assert.Equal(corrupt, File.ReadAllText(statePath));
        Assert.True(File.Exists(Full("plugins/Author-Mod/Mod.dll")));
        Assert.False(Directory.Exists(Full("progenitor-removed")));
        Assert.False(PluginFileActions.TryRead(_root, out _, out string error));
        Assert.NotEqual("", error);
    }

    // ------------------------------------------------------------------ review finding 3

    /// <summary>A directory junction, made the way a player could (no admin needed). False when this machine cannot.</summary>
    private static bool TryJunction(string link, string target)
    {
        try
        {
            Directory.CreateDirectory(target);
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p!.WaitForExit(10000);
            return Directory.Exists(link) && (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Review finding 3: a linked folder halfway down a mod's folder is never followed. Identify refuses
    /// it, and a request whose subfolder became a junction after it was made renames and moves nothing.
    /// What this would catch: a disable that renames files in the junction's target, outside plugins.
    /// </summary>
    [Fact]
    public void A_linked_subfolder_is_refused_when_identified_and_when_carried_out()
    {
        if (!OperatingSystem.IsWindows()) return;
        string outside = Path.Combine(_base, "outside-shared");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "x.cfg"), "shared");

        string location = MakeMod();
        if (!TryJunction(Full("plugins/Author-Mod/data"), outside)) return; // no junctions here: nothing to prove
        var linked = IdentifyAlone("author.mod", location);
        Assert.False(linked.Certain);
        Assert.Equal(PluginFileActions.WhyLink, linked.Why);
        Directory.Delete(Full("plugins/Author-Mod/data"));

        // Identified clean, then the subfolder is swapped for a junction before the start.
        Put("plugins/Author-Mod/data/x.cfg", "own");
        var ident = IdentifyAlone("author.mod", location);
        Assert.True(ident.Certain, ident.Why);
        Submit(RequestFor("author.mod", PluginFileAction.Disable, ident));
        Directory.Delete(Full("plugins/Author-Mod/data"), recursive: true);
        Assert.True(TryJunction(Full("plugins/Author-Mod/data"), outside));

        var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));
        Assert.False(r.Done);
        Assert.Equal(PluginFileActions.WhyLink, r.Reason);
        Assert.True(File.Exists(Path.Combine(outside, "x.cfg")));
        Assert.False(File.Exists(Path.Combine(outside, "x.cfg.old")));
        Assert.True(File.Exists(Full("plugins/Author-Mod/Mod.dll")));

        // A removal of a folder with a link inside is refused too.
        Submit(RequestFor("author.mod", PluginFileAction.Remove, ident));
        var removed = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));
        Assert.False(removed.Done);
        Assert.True(Directory.Exists(Full("plugins/Author-Mod")));
        Directory.Delete(Full("plugins/Author-Mod/data"));
    }

    /// <summary>
    /// Review finding 3: a file the mod already ships off (<c>*.old</c>) is left exactly as it is, and
    /// a folder holding both spellings of one file is refused with the reason rather than failed at start.
    /// </summary>
    [Fact]
    public void A_file_already_off_is_left_alone_and_both_spellings_are_refused()
    {
        string location = MakeMod();
        Put("plugins/Author-Mod/Legacy.dll.old", "legacy bytes");
        var ident = IdentifyAlone("author.mod", location);
        Assert.True(ident.Certain, ident.Why);
        Assert.DoesNotContain(ident.Files, f => f.Path.Contains("Legacy"));
        Submit(RequestFor("author.mod", PluginFileAction.Disable, ident));
        var off = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));
        Assert.True(off.Done, off.Reason);
        Assert.Equal("legacy bytes", File.ReadAllText(Full("plugins/Author-Mod/Legacy.dll.old")));
        Submit(RequestFor("author.mod", PluginFileAction.Enable, ident));
        Assert.True(Assert.Single(PluginFileActions.RunAll(_root, Now, null, null)).Done);
        Assert.False(File.Exists(Full("plugins/Author-Mod/Legacy.dll")), "a file the mod shipped off is not turned on");

        Put("plugins/Author-Mod/Mod.dll.old", "stale copy");
        var both = IdentifyAlone("author.mod", location);
        Assert.False(both.Certain);
        Assert.Equal(PluginFileActions.WhyOldBeside(Full("plugins/Author-Mod/Mod.dll")), both.Why);
    }

    // ------------------------------------------------------------------ second review: finding 1 (idempotency, journal)

    /// <summary>The disable journal for Author-Mod as a start writes it before renaming.</summary>
    private List<PluginJournalEntry> DisableJournal(PluginFileRequest request) => new()
    {
        PluginJournalEntry.For(request, request.Files.Select(f => new PluginJournalMove
        {
            From = f.Path,
            To = f.Path + PluginFileActions.DisabledSuffix,
            Sha256 = f.Sha256,
        }).ToList()),
    };

    private void WriteJournal(List<PluginJournalEntry> entries) =>
        Assert.True(PluginFileActions.TryWriteJournal(_root, entries, out string error), error);

    private void AssertAllOff()
    {
        foreach (var f in new[] { "Companion.dll", "Mod.dll", "mod.cfg" })
        {
            Assert.False(File.Exists(Full("plugins/Author-Mod/" + f)), f);
            Assert.True(File.Exists(Full("plugins/Author-Mod/" + f + ".old")), f + ".old");
        }
    }

    /// <summary>What this would catch: a second run of a finished disable failing with "was not there".</summary>
    [Fact]
    public void Execute_twice_is_done_both_times()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        var request = RequestFor("author.mod", PluginFileAction.Disable, ident);

        var first = PluginFileActions.Execute(_root, request, Now);
        var second = PluginFileActions.Execute(_root, request, Now);

        Assert.True(first.Done, first.Reason);
        Assert.True(second.Done, second.Reason);
        AssertAllOff();
    }

    /// <summary>
    /// The renames happen but the state write fails: the journal keeps the outcome, and the next start
    /// records Done (so the mod stays listed as off) instead of replacing it with "was not there".
    /// </summary>
    [Fact]
    public void RunAll_after_a_failed_state_write_records_done_at_the_next_start()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        Submit(RequestFor("author.mod", PluginFileAction.Disable, ident));
        string statePath = PluginFileActions.StatePath(_root);
        var warnings = new List<string>();

        File.SetAttributes(statePath, FileAttributes.ReadOnly);
        try
        {
            var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, warnings.Add));
            Assert.True(r.Done, r.Reason);
        }
        finally
        {
            File.SetAttributes(statePath, FileAttributes.Normal);
        }

        Assert.Contains(warnings, w => w.Contains("writing " + PluginFileActions.FileName));
        Assert.True(File.Exists(PluginFileActions.JournalPath(_root)));
        Assert.Single(ReadState().Requests); // the write failed, so the request is still waiting
        AssertAllOff();

        var again = Assert.Single(PluginFileActions.RunAll(_root, Now.AddMinutes(1), null, null));

        Assert.True(again.Done, again.Reason);
        AssertAllOff();
        var state = ReadState();
        Assert.Empty(state.Requests);
        Assert.True(Assert.Single(state.Results).Done);
        Assert.Equal("author.mod", Assert.Single(PluginFileActions.DisabledOnDisk(_root, state)).PluginId);
        Assert.False(File.Exists(PluginFileActions.JournalPath(_root)));
    }

    /// <summary>The process died after one of three renames: the next start completes the other two.</summary>
    [Fact]
    public void RunAll_completes_a_start_that_stopped_after_one_of_three_renames()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        var request = RequestFor("author.mod", PluginFileAction.Disable, ident);
        Submit(request);
        WriteJournal(DisableJournal(request));
        File.Move(Full("plugins/Author-Mod/Companion.dll"), Full("plugins/Author-Mod/Companion.dll.old"));

        var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));

        Assert.True(r.Done, r.Reason);
        AssertAllOff();
        Assert.Equal("mod bytes", File.ReadAllText(Full("plugins/Author-Mod/Mod.dll.old")));
        var state = ReadState();
        Assert.Empty(state.Requests);
        Assert.Single(PluginFileActions.DisabledOnDisk(_root, state));
        Assert.False(File.Exists(PluginFileActions.JournalPath(_root)));
    }

    /// <summary>A removal that moved the folder but never wrote the state is recorded with where it went.</summary>
    [Fact]
    public void RunAll_records_a_removal_whose_state_write_never_happened()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        var request = RequestFor("author.mod", PluginFileAction.Remove, ident);
        Submit(request);
        const string dest = "progenitor-removed/20260929-000000/Author-Mod";
        WriteJournal(new List<PluginJournalEntry>
        {
            PluginJournalEntry.For(request, new List<PluginJournalMove> { new() { From = "plugins/Author-Mod", To = dest, IsFolder = true } }),
        });
        Directory.CreateDirectory(Path.GetDirectoryName(Full(dest))!);
        Directory.Move(Full("plugins/Author-Mod"), Full(dest));

        var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));

        Assert.True(r.Done, r.Reason);
        Assert.Equal(dest, r.RemovedTo);
        Assert.Empty(ReadState().Requests);
        Assert.False(Directory.Exists(Full("progenitor-removed/20260929-123456")), "the request was not run a second time");
    }

    /// <summary>One file was renamed, then another file changed: the finished rename is undone.</summary>
    [Fact]
    public void RunAll_undoes_an_interrupted_start_when_a_remaining_file_no_longer_matches()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        var request = RequestFor("author.mod", PluginFileAction.Disable, ident);
        Submit(request);
        WriteJournal(DisableJournal(request));
        File.Move(Full("plugins/Author-Mod/Companion.dll"), Full("plugins/Author-Mod/Companion.dll.old"));
        File.WriteAllText(Full("plugins/Author-Mod/Mod.dll"), "edited bytes");

        var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));

        Assert.False(r.Done);
        Assert.Equal(PluginFileActions.WhyInterrupted("plugins/Author-Mod/Mod.dll"), r.Reason);
        Assert.Equal("companion bytes", File.ReadAllText(Full("plugins/Author-Mod/Companion.dll")));
        Assert.False(File.Exists(Full("plugins/Author-Mod/Companion.dll.old")));
        Assert.Equal("edited bytes", File.ReadAllText(Full("plugins/Author-Mod/Mod.dll")));
        Assert.Empty(ReadState().Requests);
        Assert.False(File.Exists(PluginFileActions.JournalPath(_root)));
    }

    /// <summary>
    /// Undoing fails too (the renamed file is held open): the reason must say the folder is left
    /// partly renamed and name the file, never "nothing was changed".
    /// </summary>
    [Fact]
    public void RunAll_says_the_folder_is_left_partly_renamed_when_undoing_fails()
    {
        if (!OperatingSystem.IsWindows()) return; // an open file blocks a rename on Windows only
        var ident = IdentifyAlone("author.mod", MakeMod());
        var request = RequestFor("author.mod", PluginFileAction.Disable, ident);
        Submit(request);
        WriteJournal(DisableJournal(request));
        File.Move(Full("plugins/Author-Mod/Companion.dll"), Full("plugins/Author-Mod/Companion.dll.old"));
        File.WriteAllText(Full("plugins/Author-Mod/Mod.dll"), "edited bytes");

        PluginFileResult r;
        using (new FileStream(Full("plugins/Author-Mod/Companion.dll.old"), FileMode.Open, FileAccess.Read, FileShare.Read))
            r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));

        Assert.False(r.Done);
        Assert.Equal(
            PluginFileActions.WhyInterruptedPartly("plugins/Author-Mod/Mod.dll", new[] { "plugins/Author-Mod/Companion.dll.old" }),
            r.Reason);
        Assert.Contains("partly renamed", r.Reason);
        Assert.DoesNotContain("nothing was changed", r.Reason);
        Assert.True(File.Exists(Full("plugins/Author-Mod/Companion.dll.old")));
    }

    /// <summary>A journal naming a file outside the mod's folder touches nothing.</summary>
    [Fact]
    public void RunAll_refuses_a_journal_move_outside_the_mod_folder()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        var request = RequestFor("author.mod", PluginFileAction.Disable, ident);
        Put("core/Core.dll", "core");
        Put("core/Core.dll.old", "old core");
        WriteJournal(new List<PluginJournalEntry>
        {
            PluginJournalEntry.For(request, new List<PluginJournalMove> { new() { From = "core/Core.dll", To = "core/Core.dll.old" } }),
        });

        var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));

        Assert.False(r.Done);
        Assert.Equal(PluginFileActions.WhyUnsafeRequest, r.Reason);
        Assert.Equal("core", File.ReadAllText(Full("core/Core.dll")));
        Assert.Equal("old core", File.ReadAllText(Full("core/Core.dll.old")));
    }

    // ------------------------------------------------------------------ second review: finding 2 (concurrency)

    /// <summary>What this would catch: renaming DLLs another running game process from this install has open.</summary>
    [Fact]
    public void RunAll_leaves_requests_waiting_while_another_game_process_runs()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        Submit(RequestFor("author.mod", PluginFileAction.Disable, ident));
        var infos = new List<string>();

        var results = PluginFileActions.RunAll(_root, Now, infos.Add, null, () => true);

        Assert.Empty(results);
        Assert.Single(infos);
        Assert.NotNull(PluginFileActions.PendingFor(ReadState(), "author.mod"));
        Assert.True(File.Exists(Full("plugins/Author-Mod/Mod.dll")));
        Assert.False(File.Exists(Full("plugins/Author-Mod/Mod.dll.old")));

        var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null, () => false));
        Assert.True(r.Done, r.Reason);
    }

    /// <summary>A writer waits for the lock another process holds, and gives up truthfully.</summary>
    [Fact]
    public void Request_fails_while_another_holder_keeps_the_state_lock()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        using var held = new System.Threading.ManualResetEventSlim();
        using var release = new System.Threading.ManualResetEventSlim();
        var holder = new System.Threading.Thread(() =>
        {
            var gate = PluginFileActions.AcquireLock(_root, out _);
            held.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            PluginFileActions.ReleaseLock(gate);
        });
        var before = PluginFileActions.LockTimeout;
        PluginFileActions.LockTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            holder.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(PluginFileActions.Request(_root, RequestFor("author.mod", PluginFileAction.Disable, ident), out string error));
            Assert.Equal(PluginFileActions.WhyBusy, error);
        }
        finally
        {
            release.Set();
            holder.Join();
            PluginFileActions.LockTimeout = before;
        }

        Submit(RequestFor("author.mod", PluginFileAction.Disable, ident));
        Assert.NotNull(PluginFileActions.PendingFor(ReadState(), "author.mod"));
        Assert.Empty(Directory.GetFiles(Full("config"), "*.tmp"));
    }

    // ------------------------------------------------------------------ second review: findings 3 and 4 (ownership, unreadable)

    /// <summary>
    /// A real BepInEx plugin assembly (<see cref="OrdinaryPackageFixtures.Plugin"/>, compiled by Roslyn), written
    /// outside the BepInEx tree. It used to be a sample's build output found by walking up from the test
    /// binaries, which in a fresh worktree found another checkout's stale build.
    /// </summary>
    private string PluginDll()
    {
        string dll = Path.Combine(_base, "fixture", "FixturePlugin.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
        if (!File.Exists(dll)) File.WriteAllBytes(dll, Fixtures.Value.Plugin);
        return dll;
    }

    private static readonly Lazy<OrdinaryPackageFixtures> Fixtures = new(() => new OrdinaryPackageFixtures());

    private const string FixtureGuid = OrdinaryPackageFixtures.PluginGuid;

    [Fact]
    public void TryReadPluginGuids_reads_the_BepInPlugin_guid_and_ignores_non_assemblies()
    {
        string dll = PluginDll();

        Assert.True(PluginFileActions.TryReadPluginGuids(dll, out var guids));
        Assert.Equal(new[] { FixtureGuid }, guids);

        Assert.False(PluginFileActions.TryReadPluginGuids(Put("plugins/Author-Mod/Native.dll", "not a PE file"), out var none));
        Assert.Empty(none);
    }

    /// <summary>What this would catch: turning off a folder that also holds a plugin that failed to load.</summary>
    [Fact]
    public void Identify_folder_holding_a_dll_that_declares_another_plugin_is_shared()
    {
        string dll = PluginDll();
        string location = MakeMod();
        File.Copy(dll, Full("plugins/Author-Mod/Extra.dll"));

        var ident = IdentifyAlone("author.mod", location);

        Assert.False(ident.Certain);
        Assert.Equal(PluginFileActions.WhyShared(FixtureGuid), ident.Why);
        Assert.Empty(ident.Files);

        // Off (.dll.old) counts too: a removal would carry it away.
        File.Move(Full("plugins/Author-Mod/Extra.dll"), Full("plugins/Author-Mod/Extra.dll.old"));
        Assert.Equal(PluginFileActions.WhyShared(FixtureGuid), IdentifyAlone("author.mod", location).Why);
    }

    /// <summary>What this would catch: a DLL recorded with an empty hash, which the next start never checks.</summary>
    [Fact]
    public void Identify_dll_that_cannot_be_read_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return; // FileShare.None is advisory elsewhere
        string location = MakeMod();

        PluginIdentification ident;
        using (new FileStream(Full("plugins/Author-Mod/Companion.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
            ident = IdentifyAlone("author.mod", location);

        Assert.False(ident.Certain);
        Assert.Equal(PluginFileActions.WhyUnreadableFile("Companion.dll"), ident.Why);
        Assert.Empty(ident.Files);
        Assert.True(IdentifyAlone("author.mod", location).Certain);
    }

    // ------------------------------------------------------------------ second review: finding 5 (8.3 short names)

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "GetShortPathNameW")]
    private static extern uint GetShortPathName(string longPath, System.Text.StringBuilder shortPath, uint capacity);

    /// <summary>What this would catch: <c>plugins/PROGEN~1/x.dll</c> slipping past the <c>Progenitor*</c> check.</summary>
    [Fact]
    public void IsProtected_sees_through_8dot3_short_names()
    {
        if (!OperatingSystem.IsWindows()) return;
        string file = Put("plugins/ProgenitorLongNameFolder/x.dll", "x");
        var buffer = new System.Text.StringBuilder(1024);
        uint n = GetShortPathName(file, buffer, (uint)buffer.Capacity);
        if (n == 0 || n > buffer.Capacity) return;
        string shortPath = buffer.ToString();
        if (Path.GetFileName(Path.GetDirectoryName(shortPath)!).Equals("ProgenitorLongNameFolder", StringComparison.OrdinalIgnoreCase))
            return; // short names are off on this volume: nothing to prove

        Assert.True(PluginFileActions.IsProtected(_root, shortPath, out string why));
        Assert.Equal(PluginFileActions.WhyProgenitor, why);
        Assert.True(PluginFileActions.IsStrictlyUnder(Full("plugins/ProgenitorLongNameFolder"), shortPath));
        Assert.Equal(PluginFileActions.WhyProgenitor, IdentifyAlone("some.mod", shortPath).Why);
    }

    // ------------------------------------------------------------------ round 2 A: a leftover journal while another process runs

    /// <summary>
    /// What this would catch: this start loading a half-renamed mod silently, or renaming files another
    /// running game process may have open. Nothing moves; the truth is recorded; the next start finishes it.
    /// </summary>
    [Fact]
    public void RunAll_while_another_process_runs_reports_a_half_done_journal_and_touches_nothing()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        var request = RequestFor("author.mod", PluginFileAction.Disable, ident);
        Submit(request);
        WriteJournal(DisableJournal(request));
        File.Move(Full("plugins/Author-Mod/Companion.dll"), Full("plugins/Author-Mod/Companion.dll.old"));

        var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null, () => true));

        Assert.False(r.Done);
        Assert.Equal(PluginFileActions.WhyLeftWhileOthersRun(new[] { "plugins/Author-Mod/Companion.dll.old" }), r.Reason);
        Assert.True(File.Exists(Full("plugins/Author-Mod/Companion.dll.old")));
        Assert.True(File.Exists(Full("plugins/Author-Mod/Mod.dll")));
        Assert.True(File.Exists(Full("plugins/Author-Mod/mod.cfg")));
        Assert.True(File.Exists(PluginFileActions.JournalPath(_root)));
        var state = ReadState();
        Assert.NotNull(PluginFileActions.PendingFor(state, "author.mod"));
        Assert.Equal(r.Reason, PluginFileActions.LastResultFor(state, "author.mod")!.Reason);

        var finished = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null, () => false));
        Assert.True(finished.Done, finished.Reason);
        AssertAllOff();
        Assert.False(File.Exists(PluginFileActions.JournalPath(_root)));
    }

    // ------------------------------------------------------------------ round 2 B: an unreadable journal is kept

    [Fact]
    public void RunAll_keeps_an_unreadable_journal_aside_and_says_where_files_may_be()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        Submit(RequestFor("author.mod", PluginFileAction.Disable, ident));
        const string corrupt = "{\"schema\":1,\"entries\":[{\"pluginId\": \"author.mod\", \"name\": \"x\", \"action\": \"disable\", \"folder\": \"plu";
        File.WriteAllText(PluginFileActions.JournalPath(_root), corrupt);

        var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));

        const string keptAs = "config/" + PluginFileActions.JournalFileName + ".corrupt-20260929-123456";
        Assert.Equal("author.mod", r.PluginId);
        Assert.Equal(PluginFileAction.Disable, r.Action);
        Assert.False(r.Done);
        Assert.Equal(PluginFileActions.WhyCorruptJournal(keptAs), r.Reason);
        Assert.Equal(corrupt, File.ReadAllText(Full(keptAs)));
        Assert.False(File.Exists(PluginFileActions.JournalPath(_root)));
        Assert.True(File.Exists(Full("plugins/Author-Mod/Mod.dll")), "the request of a plugin the record names does not run");
        Assert.Empty(ReadState().Requests);

        // Nothing legible, and the first aside name is taken: a unique name and the preloader's own result.
        File.WriteAllText(PluginFileActions.JournalPath(_root), "\u0001garbage");
        var g = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));
        Assert.Equal(PluginFileActions.JournalResultId, g.PluginId);
        Assert.Equal("Progenitor preloader", g.Name);
        Assert.Equal(PluginFileAction.Remove, g.Action);
        Assert.Equal(PluginFileActions.WhyCorruptJournal(keptAs + "-2"), g.Reason);
        Assert.True(File.Exists(Full(keptAs + "-2")));
    }

    // ------------------------------------------------------------------ round 2 C: a short lock timeout for Core

    [Fact]
    public void Request_and_Cancel_with_a_short_lock_timeout_fail_fast_with_WhyBusy()
    {
        var ident = IdentifyAlone("author.mod", MakeMod());
        using var held = new System.Threading.ManualResetEventSlim();
        using var release = new System.Threading.ManualResetEventSlim();
        var holder = new System.Threading.Thread(() =>
        {
            var gate = PluginFileActions.AcquireLock(_root, out _);
            held.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            PluginFileActions.ReleaseLock(gate);
        });
        try
        {
            holder.Start();
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.False(PluginFileActions.Request(_root, RequestFor("author.mod", PluginFileAction.Disable, ident), out string error, TimeSpan.FromMilliseconds(250)));
            Assert.Equal(PluginFileActions.WhyBusy, error);
            Assert.False(PluginFileActions.Cancel(_root, "author.mod", out error, TimeSpan.FromMilliseconds(250)));
            Assert.Equal(PluginFileActions.WhyBusy, error);
            // Two 250 ms lock waits; waiting for the holder would take its full 30 s.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), watch.Elapsed.ToString());
        }
        finally
        {
            release.Set();
            holder.Join();
        }
    }

    // ------------------------------------------------------------------ round 2 D: which processes are this install's game

    [Fact]
    public void IsGameProcessOfInstall_counts_this_installs_client_and_servers_and_unreadable_game_processes()
    {
        string root = Path.Combine(_base, "Game");
        string other = Path.Combine(_base, "OtherGame");
        bool Is(string name, string? path, int pid = 20) => PluginFileActions.IsGameProcessOfInstall(name, path, root, 10, pid);

        Assert.True(Is("VRising", Path.Combine(root, "VRising.exe")));
        Assert.True(Is("VRising", Path.Combine(root.ToUpperInvariant(), "vrising.exe")));
        Assert.False(Is("VRising", Path.Combine(root, "VRising.exe"), pid: 10)); // this process
        Assert.False(Is("VRising", Path.Combine(other, "VRising.exe")));
        Assert.False(Is("VRising", Path.Combine(root, "sub", "VRising.exe")));
        Assert.True(Is("VRisingServer", Path.Combine(root, "VRising_Server", "VRisingServer.exe")));
        Assert.True(Is("VRisingServer", Path.Combine(root, "VRisingServer.exe")));
        Assert.False(Is("VRisingServer", Path.Combine(other, "VRising_Server", "VRisingServer.exe")));
        Assert.True(Is("VRising", null)); // cannot be inspected: may be ours
        Assert.True(Is("VRisingServer", null));
        Assert.False(Is("notepad", null)); // not the game at all
        Assert.False(Is("notepad", Path.Combine(root, "VRising.exe")));
    }

    // ------------------------------------------------------------------ loose single-DLL mods

    /// <summary>The user's real plugins listing. Which fake DLL declares which plugin comes from <see cref="LooseGuids"/>.</summary>
    private static readonly string[] LooseListing =
    {
        "BasinHoverTips.dll", "ClanResourceHUD.dll", "GroundPickupGlow.dll", "HuntTimer.dll", "RetainedPassword.dll",
        "RetainedPassword.README.md", "LowAngle.dll.disabled", "System.Security.Cryptography.ProtectedData.dll",
        "Progenitor.Bootstrap.dll", "Progenitor.Bootstrap.dll.prev", "BasinHoverTips.dll.105.live.bak", "ClanResourceHUD.dll.next",
    };

    private static readonly Dictionary<string, string[]> GuidsByFile = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BasinHoverTips.dll"] = new[] { "author.basinhovertips" },
        ["ClanResourceHUD.dll"] = new[] { "author.clanresourcehud" },
        ["GroundPickupGlow.dll"] = new[] { "author.groundpickupglow" },
        ["HuntTimer.dll"] = new[] { "author.hunttimer", "author.hunttimer.extra" },
        ["RetainedPassword.dll"] = new[] { "author.retainedpassword" },
        ["Progenitor.Bootstrap.dll"] = new[] { "progenitor.bootstrap" },
    };

    private static IReadOnlyList<string> LooseGuids(string path) =>
        GuidsByFile.TryGetValue(Path.GetFileName(path), out var g) ? g : Array.Empty<string>();

    private void MakeLooseListing()
    {
        foreach (var name in LooseListing) Put("plugins/" + name, "bytes of " + name);
    }

    private PluginIdentification IdentifyLoose(string id, string file, params LoadedPluginFact[] others)
    {
        string location = Full("plugins/" + file);
        var loaded = new List<LoadedPluginFact> { Fact(id, location) };
        loaded.AddRange(others);
        return PluginFileActions.Identify(_root, id, location, loaded, LooseGuids);
    }

    /// <summary>Every file under the BepInEx root (relative path → hash), config excluded.</summary>
    private Dictionary<string, string> Snapshot() =>
        Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
            .Select(f => PluginFileActions.RelativeTo(_root, f))
            .Where(r => !r.StartsWith("config/", StringComparison.Ordinal))
            .ToDictionary(r => r, r => PluginFileActions.Sha256Of(Full(r)), StringComparer.Ordinal);

    /// <summary>Everything in <paramref name="before"/> except <paramref name="changed"/> is exactly as it was.</summary>
    private void AssertUntouchedExcept(Dictionary<string, string> before, params string[] changed)
    {
        var after = Snapshot();
        foreach (var kv in before)
        {
            if (changed.Contains(kv.Key)) continue;
            Assert.True(after.TryGetValue(kv.Key, out var sha), kv.Key + " is gone");
            Assert.Equal(kv.Value, sha);
        }
    }

    [Fact]
    public void Loose_disable_enable_and_remove_touch_only_the_mod_dll()
    {
        MakeLooseListing();
        var ident = IdentifyLoose("author.basinhovertips", "BasinHoverTips.dll");
        Assert.True(ident.Certain, ident.Why);
        Assert.True(ident.Loose);
        Assert.Equal("plugins", ident.Folder);
        Assert.Equal(new[] { "plugins/BasinHoverTips.dll" }, ident.Files.Select(f => f.Path));
        var original = Snapshot();

        Submit(RequestFor("author.basinhovertips", PluginFileAction.Disable, ident));
        Assert.True(ReadState().Requests[0].Loose, "the flag survives the state file");
        var off = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));
        Assert.True(off.Done, off.Reason);
        AssertUntouchedExcept(original, "plugins/BasinHoverTips.dll");
        Assert.Equal(original["plugins/BasinHoverTips.dll"], PluginFileActions.Sha256Of(Full("plugins/BasinHoverTips.dll.old")));
        Assert.Equal(original.Count, Snapshot().Count);
        Assert.Equal("author.basinhovertips", Assert.Single(PluginFileActions.DisabledOnDisk(_root, ReadState())).PluginId);

        Submit(RequestFor("author.basinhovertips", PluginFileAction.Enable, ident));
        var on = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));
        Assert.True(on.Done, on.Reason);
        Assert.Equal(original, Snapshot());
        Assert.Empty(PluginFileActions.DisabledOnDisk(_root, ReadState()));

        Submit(RequestFor("author.basinhovertips", PluginFileAction.Remove, ident));
        var removed = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));
        Assert.True(removed.Done, removed.Reason);
        Assert.Equal("progenitor-removed/20260929-123456/BasinHoverTips", removed.RemovedTo);
        AssertUntouchedExcept(original, "plugins/BasinHoverTips.dll");
        Assert.False(File.Exists(Full("plugins/BasinHoverTips.dll")));
        Assert.Equal(
            new[] { "progenitor-removed/20260929-123456/BasinHoverTips/BasinHoverTips.dll" },
            Snapshot().Keys.Where(k => k.StartsWith("progenitor-removed/", StringComparison.Ordinal)));
    }

    [Fact]
    public void Loose_remove_takes_exactly_the_dll_and_its_readme()
    {
        MakeLooseListing();
        Put("plugins/RetainedPassword.cfg", "cfg");
        Put("plugins/RetainedPassword.dll.105.bak", "bak");
        Put("plugins/RetainedPasswordExtra.md", "not its doc");
        var ident = IdentifyLoose("author.retainedpassword", "RetainedPassword.dll");
        Assert.True(ident.Certain, ident.Why);
        Assert.Equal(
            new[] { "plugins/RetainedPassword.dll", "plugins/RetainedPassword.README.md" },
            ident.Files.Select(f => f.Path));
        var original = Snapshot();

        Submit(RequestFor("author.retainedpassword", PluginFileAction.Disable, ident));
        Assert.True(Assert.Single(PluginFileActions.RunAll(_root, Now, null, null)).Done);
        AssertUntouchedExcept(original, "plugins/RetainedPassword.dll");
        Assert.True(File.Exists(Full("plugins/RetainedPassword.README.md")), "a disable renames the DLL alone");

        Submit(RequestFor("author.retainedpassword", PluginFileAction.Remove, ident));
        var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));
        Assert.True(r.Done, r.Reason);
        AssertUntouchedExcept(original, "plugins/RetainedPassword.dll", "plugins/RetainedPassword.README.md");
        Assert.Equal(
            new[]
            {
                "progenitor-removed/20260929-123456/RetainedPassword/RetainedPassword.README.md",
                "progenitor-removed/20260929-123456/RetainedPassword/RetainedPassword.dll.old",
            },
            Snapshot().Keys.Where(k => k.StartsWith("progenitor-removed/", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal));
        Assert.True(File.Exists(Full("plugins/RetainedPassword.cfg")));
        Assert.True(File.Exists(Full("plugins/RetainedPassword.dll.105.bak")));
    }

    [Fact]
    public void Loose_dlls_that_are_not_exactly_one_matching_plugin_are_refused()
    {
        MakeLooseListing();
        string two = Full("plugins/HuntTimer.dll");
        Assert.Equal(PluginFileActions.WhyManyPlugins(two), IdentifyLoose("author.hunttimer", "HuntTimer.dll").Why);

        string none = Full("plugins/System.Security.Cryptography.ProtectedData.dll");
        var noPlugin = IdentifyLoose("system.protecteddata", "System.Security.Cryptography.ProtectedData.dll");
        Assert.False(noPlugin.Certain);
        Assert.Equal(PluginFileActions.WhyNoPluginInFile(none), noPlugin.Why);

        Assert.Equal(
            PluginFileActions.WhyDeclaresOther(Full("plugins/ClanResourceHUD.dll"), "author.clanresourcehud"),
            IdentifyLoose("author.somethingelse", "ClanResourceHUD.dll").Why);
        Assert.Equal(PluginFileActions.WhyProgenitor, IdentifyLoose("some.bootstrap", "Progenitor.Bootstrap.dll").Why);
        Assert.Equal(
            PluginFileActions.WhyNeededBy("Dependent"),
            IdentifyLoose("author.groundpickupglow", "GroundPickupGlow.dll",
                new LoadedPluginFact("author.dependent", "Dependent", Full("plugins/ClanResourceHUD.dll"), new[] { "author.groundpickupglow" })).Why);

        Put("plugins/GroundPickupGlow.dll.old", "someone's copy");
        Assert.Equal(PluginFileActions.WhyOldBeside(Full("plugins/GroundPickupGlow.dll")), IdentifyLoose("author.groundpickupglow", "GroundPickupGlow.dll").Why);
    }

    [Fact]
    public void Loose_request_naming_any_other_file_touches_nothing()
    {
        MakeLooseListing();
        var ident = IdentifyLoose("author.basinhovertips", "BasinHoverTips.dll");
        var original = Snapshot();
        foreach (var extra in new[] { "plugins/System.Security.Cryptography.ProtectedData.dll", "plugins/BasinHoverTips.dll.105.live.bak", "plugins/ClanResourceHUD.dll.next", "plugins/LowAngle.dll.disabled" })
        {
            foreach (var action in new[] { PluginFileAction.Disable, PluginFileAction.Remove })
            {
                var request = RequestFor("author.basinhovertips", action, ident);
                request.Files.Add(new PluginFileEntry(extra, PluginFileActions.Sha256Of(Full(extra))));
                Submit(request);
                var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));
                Assert.False(r.Done, extra);
                Assert.Equal(PluginFileActions.WhyUnsafeRequest, r.Reason);
                Assert.Equal(original, Snapshot());
            }
        }

        // Folder mode may never name plugins itself.
        var folderMode = RequestFor("author.basinhovertips", PluginFileAction.Disable, ident);
        folderMode.Loose = false;
        Submit(folderMode);
        Assert.Equal(PluginFileActions.WhyUnsafeRequest, Assert.Single(PluginFileActions.RunAll(_root, Now, null, null)).Reason);
        Assert.Equal(original, Snapshot());
    }

    /// <summary>A loose removal that stopped after the DLL moved is completed at the next start.</summary>
    [Fact]
    public void Loose_remove_interrupted_after_the_dll_moved_is_completed()
    {
        MakeLooseListing();
        var ident = IdentifyLoose("author.retainedpassword", "RetainedPassword.dll");
        var request = RequestFor("author.retainedpassword", PluginFileAction.Remove, ident);
        Submit(request);
        const string dest = "progenitor-removed/20260929-000000/RetainedPassword/";
        WriteJournal(new List<PluginJournalEntry>
        {
            PluginJournalEntry.For(request, new List<PluginJournalMove>
            {
                new() { From = "plugins/RetainedPassword.dll", To = dest + "RetainedPassword.dll", Sha256 = ident.Files[0].Sha256 },
                new() { From = "plugins/RetainedPassword.README.md", To = dest + "RetainedPassword.README.md" },
            }),
        });
        Directory.CreateDirectory(Full(dest));
        File.Move(Full("plugins/RetainedPassword.dll"), Full(dest + "RetainedPassword.dll"));

        var r = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));

        Assert.True(r.Done, r.Reason);
        Assert.Equal("progenitor-removed/20260929-000000/RetainedPassword", r.RemovedTo);
        Assert.True(File.Exists(Full(dest + "RetainedPassword.README.md")));
        Assert.False(File.Exists(Full("plugins/RetainedPassword.README.md")));
        Assert.True(File.Exists(Full("plugins/BasinHoverTips.dll")));
    }

    /// <summary>The default metadata reader in loose mode, on a real plugin assembly.</summary>
    [Fact]
    public void Loose_real_plugin_assembly_is_read_through_the_default_reader()
    {
        string dll = PluginDll();
        string copy = Full("plugins/FixtureCopy.dll");
        File.Copy(dll, copy);

        var ident = PluginFileActions.Identify(_root, "author.notthis", copy, new[] { Fact("author.notthis", copy) });

        Assert.False(ident.Certain);
        Assert.Equal(PluginFileActions.WhyDeclaresOther(copy, FixtureGuid), ident.Why);
    }

    /// <summary>
    /// The leftovers a hand-built mod's own deploy script leaves beside its DLL: numbered backups and staged
    /// next copies. None of them ends in <c>.dll</c> or <c>.dll.old</c>, so the chainloader never loads
    /// them and none of them is the mod's file.
    /// </summary>
    private static readonly string[] LooseLeftovers =
    {
        "ExampleHover.dll.100.crash.bak", "ExampleHover.dll.105.live.bak", "ExampleHover.dll.118.live.bak",
        "ExampleHover.dll.next", "ExampleHover.dll.next.105", "ExampleHover.dll.next.102.crash", "ExampleHover.dll.next.crash",
        "ExampleHover.dll.prev",
    };

    /// <summary>
    /// What this would catch (live finding, 2026-10-07): a loose single DLL with many <c>*.bak</c> and
    /// <c>*.next</c> siblings refused, or those siblings swept up with it. Real metadata through the default
    /// reader; the leftovers do not block, are never listed and are never renamed or moved.
    /// </summary>
    [Fact]
    public void Loose_dll_with_bak_and_next_leftovers_is_certain_and_touches_only_the_dll()
    {
        string dll = Full("plugins/ExampleHover.dll");
        File.Copy(PluginDll(), dll);
        foreach (var name in LooseLeftovers) Put("plugins/" + name, "an older build of " + name);
        // A leftover that is a real plugin image (a backup of an older build) declares the same GUID; it is
        // still not loaded and still not the mod's file.
        File.Copy(PluginDll(), Full("plugins/ExampleHover.dll.119.bak"));

        var ident = PluginFileActions.Identify(_root, FixtureGuid, dll, new[] { Fact(FixtureGuid, dll) });

        Assert.True(ident.Certain, ident.Why);
        Assert.True(ident.Loose);
        Assert.Equal("plugins", ident.Folder);
        Assert.Equal(new[] { "plugins/ExampleHover.dll" }, ident.Files.Select(f => f.Path));
        var original = Snapshot();

        Submit(RequestFor(FixtureGuid, PluginFileAction.Disable, ident));
        var off = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));
        Assert.True(off.Done, off.Reason);
        AssertUntouchedExcept(original, "plugins/ExampleHover.dll");
        Assert.True(File.Exists(dll + PluginFileActions.DisabledSuffix));

        Submit(RequestFor(FixtureGuid, PluginFileAction.Enable, ident));
        Assert.True(Assert.Single(PluginFileActions.RunAll(_root, Now, null, null)).Done);
        Assert.Equal(original, Snapshot());

        Submit(RequestFor(FixtureGuid, PluginFileAction.Remove, ident));
        var removed = Assert.Single(PluginFileActions.RunAll(_root, Now, null, null));
        Assert.True(removed.Done, removed.Reason);
        AssertUntouchedExcept(original, "plugins/ExampleHover.dll");
        Assert.Equal(
            new[] { "progenitor-removed/20260929-123456/ExampleHover/ExampleHover.dll" },
            Snapshot().Keys.Where(k => k.StartsWith("progenitor-removed/", StringComparison.Ordinal)));
        foreach (var name in LooseLeftovers) Assert.True(File.Exists(Full("plugins/" + name)), name + " stays");
    }
}
