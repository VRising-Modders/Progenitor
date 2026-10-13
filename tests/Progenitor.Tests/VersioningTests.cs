using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The git-derived build version (docs/versioning.md). Runs the real MSBuild target,
/// tools/version/ProgenitorVersion.targets, against a throwaway git repository whose commits
/// have known committer dates, so the rules are tested exactly as every project build runs them.
/// Needs git and the dotnet SDK on PATH; a missing one is a failure, not a pass.
/// </summary>
public sealed class VersioningTests : IDisposable
{
    private readonly string _repo;
    private int _counter;
    private int _pr;

    public VersioningTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "progenitor-version-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Version Test");
        Git("config", "user.email", "version-test@example.invalid");
        Git("config", "commit.gpgsign", "false");
    }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_repo, recursive: true);
        }
        catch { /* temp */ }
    }

    [Fact]
    public void ReleaseCountsMergedPullRequestsOnlyAndDirectCommitsTakeTheLastMergesRelease()
    {
        Commit("a/x.txt", "2026-09-01T10:00:00Z");
        Commit("a/x.txt", "2026-09-02T10:00:00Z");
        // Direct commits before any merge: release 0 of their own month.
        Assert.Equal("2026.9.0", Compute().Version);

        MergePr("a/x.txt", "2026-09-03T10:00:00Z");
        var first = Compute();
        Assert.Equal("2026.9.1", first.Version);
        Assert.Equal("2026.9.1.0", first.Numeric);
        Assert.Equal("2026.9.1", first.Thunderstore);
        Assert.Matches("^[0-9a-f]{10}$", first.Revision);

        // A direct commit on main is not a release: it belongs to the last merge at or before it.
        Commit("a/x.txt", "2026-09-04T10:00:00Z");
        Assert.Equal("2026.9.1", Compute().Version);

        // The PR's own commits are not on main's first-parent line and never count.
        MergePr("a/x.txt", "2026-09-05T10:00:00Z", commits: 3);
        Assert.Equal("2026.9.2", Compute().Version);
    }

    [Fact]
    public void MonthRolloverRestartsTheReleaseNumberInUtc()
    {
        MergePr("a/x.txt", "2026-09-29T10:00:00Z");
        MergePr("a/x.txt", "2026-09-30T10:00:00Z");
        Assert.Equal("2026.9.2", Compute().Version);

        // 23:30 at -04:00 on 30 September is 03:30 UTC on 1 October.
        MergePr("a/x.txt", "2026-09-30T23:30:00-04:00");
        Assert.Equal("2026.10.1", Compute().Version);

        MergePr("a/x.txt", "2026-10-02T10:00:00Z");
        Assert.Equal("2026.10.2", Compute().Version);
    }

    [Fact]
    public void EachComponentKeepsTheReleaseOfItsOwnLastChange()
    {
        MergePr("a/x.txt", "2026-09-01T10:00:00Z");
        MergePr("b/y.txt", "2026-09-02T10:00:00Z");
        MergePr("c/z.txt", "2026-09-03T10:00:00Z");

        Assert.Equal("2026.9.3", Compute().Version);            // whole repository
        Assert.Equal("2026.9.1", Compute(Scope("a/")).Version);
        Assert.Equal("2026.9.2", Compute(Scope("b/")).Version);

        // A shared build input changes every component.
        MergePr("Directory.Build.props", "2026-09-04T10:00:00Z");
        Assert.Equal("2026.9.4", Compute(Scope("a/")).Version);
        Assert.Equal("2026.9.4", Compute(Scope("b/")).Version);

        MergePr("b/y.txt", "2026-09-05T10:00:00Z");
        var a = Compute(Scope("a/"));
        Assert.Equal("2026.9.4", a.Version);
        Assert.Equal("2026.9.5", Compute(Scope("b/")).Version);
        Assert.Equal(ShortSha("HEAD~1"), a.Revision);
    }

    [Fact]
    public void SubReleaseCountsOnlyThisComponentsBranchCommitsAndDirtyFiles()
    {
        MergePr("a/x.txt", "2026-10-01T10:00:00Z");
        MergePr("b/y.txt", "2026-10-02T10:00:00Z");
        Git("checkout", "-q", "-b", "work");
        Commit("b/y.txt", "2026-10-05T10:00:00Z");
        Commit("b/y.txt", "2026-10-06T10:00:00Z");

        Assert.Equal("2026.10.1", Compute(Scope("a/")).Version);
        var b = Compute(Scope("b/"));
        Assert.Equal("2026.10.2.2", b.Version);
        Assert.Equal("2026.10.2.2", b.Numeric);
        Assert.Equal("2026.10.2", b.Thunderstore);
        Assert.Equal(ShortSha("HEAD"), b.Revision);
        Assert.Equal("2026.10.2.2", Compute().Version);

        // An untracked, non-ignored file under b/: one more iteration for b only.
        File.WriteAllText(Path.Combine(_repo, "b", "new.txt"), "x");
        var dirty = Compute(Scope("b/"));
        Assert.Equal("2026.10.2.3", dirty.Version);
        Assert.EndsWith(".dirty", dirty.Revision, StringComparison.Ordinal);
        Assert.Equal("2026.10.1", Compute(Scope("a/")).Version);

        // A tracked edit as well is still one dirty tree, not two.
        File.AppendAllText(Path.Combine(_repo, "b", "y.txt"), "edit\n");
        Assert.Equal("2026.10.2.3", Compute(Scope("b/")).Version);

        // A dirty file under a/ changes a only.
        File.WriteAllText(Path.Combine(_repo, "a", "new.txt"), "x");
        Assert.Equal("2026.10.1.1", Compute(Scope("a/")).Version);

        // Ignored files do not count.
        Git("stash", "-q", "--include-untracked");
        File.WriteAllText(Path.Combine(_repo, ".git", "info", "exclude"), "*.tmp\n");
        File.WriteAllText(Path.Combine(_repo, "a", "scratch.tmp"), "x");
        Assert.Equal("2026.10.1", Compute(Scope("a/")).Version);
        Assert.Equal("2026.10.2.2", Compute(Scope("b/")).Version);
    }

    [Fact]
    public void ProjectInputsIncludeLinkedFilesAndReferencedProjects()
    {
        Write("app/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
            + "  <ItemGroup>\n"
            + "    <Compile Include=\"..\\shared\\Linked.cs\" Link=\"Linked.cs\" />\n"
            + "    <Compile Include=\"..\\gen\\*.cs\" />\n"
            + "    <ProjectReference Include=\"..\\lib\\Lib.csproj\" />\n"
            + "  </ItemGroup>\n"
            + "</Project>\n");
        Write("lib/Lib.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\n");
        MergePr("app/Program.cs", "2026-10-01T10:00:00Z"); // the initial commit carries both projects
        string app = Path.Combine(_repo, "app", "App.csproj");

        Assert.Equal("2026.10.1", ComputeProject(app).Version);
        MergePr("shared/Linked.cs", "2026-10-02T10:00:00Z");
        Assert.Equal("2026.10.2", ComputeProject(app).Version);
        MergePr("gen/Generated.cs", "2026-10-03T10:00:00Z");
        Assert.Equal("2026.10.3", ComputeProject(app).Version);
        MergePr("lib/Lib.cs", "2026-10-04T10:00:00Z");
        Assert.Equal("2026.10.4", ComputeProject(app).Version);
        // Neither the project, its links nor its references.
        MergePr("shared/Other.cs", "2026-10-05T10:00:00Z");
        MergePr("unrelated/u.txt", "2026-10-06T10:00:00Z");
        var v = ComputeProject(app);
        Assert.Equal("2026.10.4", v.Version);
        Assert.Equal("Directory.Build.props;Directory.Build.targets;app/;shared/Linked.cs;gen/;lib/", v.Inputs);
    }

    [Fact]
    public void OriginMainIsPreferredOverLocalMain()
    {
        MergePr("a/x.txt", "2026-10-01T10:00:00Z");
        MergePr("a/x.txt", "2026-10-02T10:00:00Z");
        // origin/main one merge behind local main: the second PR's commit and its merge are
        // unmerged work relative to origin/main.
        Git("update-ref", "refs/remotes/origin/main", "HEAD~1");
        Assert.Equal("2026.10.1.2", Compute().Version);
    }

    [Fact]
    public void MissingGitOrMainFallsBackToZeroWithAWarningAndNeverFailsTheBuild()
    {
        MergePr("a/x.txt", "2026-10-01T10:00:00Z");

        var noGit = Compute("-p:ProgenitorGitExe=" + Path.Combine(_repo, "no-such-git.exe"));
        Assert.Equal("0.0.0.0", noGit.Version);
        Assert.Equal("0.0.0.0", noGit.Numeric);
        Assert.Contains("warning", noGit.Output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Using 0.0.0.0", noGit.Output, StringComparison.Ordinal);

        var noMain = Compute("-p:ProgenitorMainRefs=no-such-ref");
        Assert.Equal("0.0.0.0", noMain.Version);
        Assert.Contains("no merge base", noMain.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitOverrideWinsAndIsValidated()
    {
        MergePr("a/x.txt", "2026-10-01T10:00:00Z");

        var three = Compute("-p:ProgenitorVersion=2026.9.12");
        Assert.Equal("2026.9.12", three.Version);
        Assert.Equal("2026.9.12.0", three.Numeric);
        Assert.Equal("2026.9.12", three.Thunderstore);

        var four = Compute("-p:ProgenitorVersion=2026.9.12.3");
        Assert.Equal("2026.9.12.3", four.Numeric);
        Assert.Equal("2026.9.12", four.Thunderstore);

        var (exit, output) = RunMsBuild("-p:ProgenitorVersion=0.1.0-beta");
        Assert.NotEqual(0, exit);
        Assert.Contains("must be year.month.release", output, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------ helpers

    private sealed record Computed(string Version, string Numeric, string Thunderstore, string Revision, string Inputs, string Output);

    private string Scope(string relativeDir) => "-p:ProgenitorVersionExtraInputs=" + relativeDir;

    private Computed ComputeProject(string projectFile) => Compute("-p:ProgenitorVersionProjectFile=" + projectFile);

    private Computed Compute(params string[] extra)
    {
        var (exit, output) = RunMsBuild(extra);
        Assert.True(exit == 0, "msbuild exited " + exit + ":\n" + output);
        int start = output.IndexOf('{');
        int end = output.LastIndexOf('}');
        Assert.True(start >= 0 && end > start, "no -getProperty JSON in:\n" + output);
        using var doc = JsonDocument.Parse(output.Substring(start, end - start + 1));
        var p = doc.RootElement.GetProperty("Properties");
        return new Computed(
            p.GetProperty("ProgenitorVersion").GetString() ?? "",
            p.GetProperty("ProgenitorNumericVersion").GetString() ?? "",
            p.GetProperty("ProgenitorThunderstoreVersion").GetString() ?? "",
            p.GetProperty("ProgenitorSourceRevision").GetString() ?? "",
            p.GetProperty("ProgenitorVersionInputs").GetString() ?? "",
            output);
    }

    private (int Exit, string Output) RunMsBuild(params string[] extra)
    {
        string targets = Path.Combine(RepoRoot(), "tools", "version", "ProgenitorVersion.targets");
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // The test host can inherit MSBuild variables from the `dotnet test` that launched it.
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            string key = (string)e.Key;
            if (key.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)) psi.Environment.Remove(key);
        }
        foreach (string a in new[]
        {
            "msbuild", targets, "-nologo", "-nodeReuse:false", "-t:ProgenitorComputeVersion",
            "-p:ProgenitorRepoRoot=" + _repo,
            "-getProperty:ProgenitorVersion", "-getProperty:ProgenitorNumericVersion",
            "-getProperty:ProgenitorThunderstoreVersion", "-getProperty:ProgenitorSourceRevision",
            "-getProperty:ProgenitorVersionInputs",
        }) psi.ArgumentList.Add(a);
        foreach (string a in extra) psi.ArgumentList.Add(a);
        return Run(psi, 180_000);
    }

    private void Write(string relative, string content)
    {
        string path = Path.Combine(_repo, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>A direct commit on the current branch that appends to <paramref name="file"/>.</summary>
    private void Commit(string file, string isoDate)
    {
        _counter++;
        string path = Path.Combine(_repo, file.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, _counter + "\n");
        Git("add", "-A");
        GitAt(isoDate, "commit", "-q", "-m", "commit " + _counter);
    }

    /// <summary>A pull request: a branch off main with <paramref name="commits"/> commits to
    /// <paramref name="file"/>, merged into main with a merge commit dated <paramref name="isoDate"/>.</summary>
    private void MergePr(string file, string isoDate, int commits = 1)
    {
        _pr++;
        string branch = "pr-" + _pr;
        if (_counter == 0) Commit("README", "2000-01-01T00:00:00Z"); // an initial commit to branch from
        Git("checkout", "-q", "-b", branch, "main");
        for (int i = 0; i < commits; i++) Commit(file, isoDate);
        Git("checkout", "-q", "main");
        GitAt(isoDate, "merge", "-q", "--no-ff", "-m", "Merge " + branch, branch);
    }

    private string ShortSha(string rev)
    {
        var psi = GitPsi(null, "rev-parse", "--short=10", rev);
        var (exit, output) = Run(psi, 60_000);
        Assert.Equal(0, exit);
        return output.Trim();
    }

    private void Git(params string[] args) => GitAt(null, args);

    private void GitAt(string? isoDate, params string[] args)
    {
        var (exit, output) = Run(GitPsi(isoDate, args), 60_000);
        Assert.True(exit == 0, "git " + string.Join(" ", args) + " exited " + exit + ":\n" + output);
    }

    private ProcessStartInfo GitPsi(string? isoDate, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (isoDate != null)
        {
            psi.Environment["GIT_AUTHOR_DATE"] = isoDate;
            psi.Environment["GIT_COMMITTER_DATE"] = isoDate;
        }
        foreach (string a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static (int, string) Run(ProcessStartInfo psi, int timeoutMs)
    {
        using var p = Process.Start(psi);
        Assert.NotNull(p);
        var err = p!.StandardError.ReadToEndAsync();
        string stdout = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(timeoutMs), psi.FileName + " timed out");
        return (p.ExitCode, stdout + "\n" + err.Result);
    }

    private static string RepoRoot()
    {
        string? dir = Path.GetFullPath(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Progenitor.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }
}
