using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FCI1 (R44 finding 10): a <c>&lt;Compile&gt;</c> item may not name a file that is not there.
///
/// <para>The defect: commit <c>fb9d7fa</c> added
/// <c>&lt;Compile Include="..\..\samples\Progenitor.Sample\SampleConsentDemo.cs" /&gt;</c> to
/// Progenitor.Tests.csproj for a file that did not arrive until <c>cc84d95</c>, so <c>fb9d7fa</c>
/// cannot build at all. Nothing failed at the time — the tip of the pair built fine — and the
/// cost lands months later on whoever bisects through it and gets a build error that has nothing
/// to do with what they were bisecting for.</para>
///
/// <para>This project is the one place in the solution where that is easy to do: it compiles
/// ~230 files out of <c>src\</c>, <c>samples\</c> and <c>tools\</c> by explicit link (it cannot
/// ProjectReference Core — see the header of the csproj), several parallel jobs append to that
/// list on the same day, and MSBuild's own error for a missing linked file is a compile error in
/// a file list nobody reads. So the list is checked here instead, against the disk, as a test.</para>
///
/// <para>This is a source pin in the sense the rest of this suite uses the phrase: it reads a
/// repository file rather than driving behavior. It is the kind worth having, because the
/// property it pins — "every path in the build's own file list resolves" — is not observable from
/// any object this assembly can construct, and the failure it prevents is a build that does not
/// happen rather than a behavior that is wrong.</para>
/// </summary>
public class ProjectFileManifestTests
{
    /// <summary>
    /// The projects that name source files by hand. Every other project in the solution takes the
    /// SDK's default <c>**\*.cs</c> glob, where a path cannot go stale because no path is written
    /// down; those are deliberately not listed here and adding them would assert nothing.
    /// </summary>
    private static readonly string[] HandMaintainedProjects =
    {
        Path.Combine("tests", "Progenitor.Tests", "Progenitor.Tests.csproj"),
        // The driftmap tool links Core's DriftMap.cs the same way and breaks the same way.
        Path.Combine("tools", "driftmap", "Progenitor.DriftMap.csproj"),
        // Mod compatibility C5: the test library links Core's codec sources, and its tests link the samples' message files.
        Path.Combine("src", "Progenitor.Testing", "Progenitor.Testing.csproj"),
        Path.Combine("tests", "Progenitor.Testing.Tests", "Progenitor.Testing.Tests.csproj"),
    };

    /// <summary>
    /// The finding itself: an <c>Include</c> that names a file which does not exist. This is the
    /// same assertion in both directions — a line added ahead of its file and a line left behind
    /// by a deleted or renamed file are the same broken path — so there is no second test for the
    /// delete case.
    /// </summary>
    [Fact]
    public void Every_compile_include_names_a_file_that_exists()
    {
        var broken = new List<string>();
        string root = RepoRoot();
        int found = 0;

        foreach (string project in HandMaintainedProjects)
        {
            string projectDir = Path.GetDirectoryName(Path.Combine(root, project))!;
            foreach (string path in CompilePaths(Path.Combine(root, project), "Include"))
            {
                found++;
                if (!File.Exists(Path.Combine(projectDir, path)))
                    broken.Add(project + " -> " + path);
            }
        }

        // A source pin that stopped finding anything to pin is worse than no pin: if the XML
        // shape ever changes under this reader (an xmlns on <Project>, say) every assertion below
        // would pass over an empty set. The floor is well under the ~230 links present.
        Assert.True(
            found > 100,
            "source pin: only " + found + " <Compile Include> paths were found across " +
            HandMaintainedProjects.Length + " projects, so this test is no longer reading the " +
            "file lists it is meant to guard.");

        Assert.True(
            broken.Count == 0,
            "source pin: these <Compile Include> paths name files that do not exist, so the " +
            "project cannot build at this commit (R44 finding 10):" + Environment.NewLine +
            string.Join(Environment.NewLine, broken));
    }

    /// <summary>
    /// The same rule for <c>Remove</c>, which fails silently rather than loudly. The
    /// <c>SkipForeignTestFiles</c> lines exist so one job can build while another job's sources
    /// are mid-edit; a <c>Remove</c> whose file was renamed removes nothing and MSBuild says
    /// nothing, so the filtered build quietly stops being filtered. That has already happened once
    /// here — the comment above the <c>ServerBeamTests.cs</c> line records the list going stale
    /// until <c>-p:SkipForeignTestFiles=true</c> no longer compiled at all.
    /// </summary>
    [Fact]
    public void Every_non_wildcard_compile_remove_names_a_file_that_exists()
    {
        var broken = new List<string>();
        string root = RepoRoot();

        foreach (string project in HandMaintainedProjects)
        {
            string projectDir = Path.GetDirectoryName(Path.Combine(root, project))!;
            foreach (string path in CompilePaths(Path.Combine(root, project), "Remove"))
            {
                if (!File.Exists(Path.Combine(projectDir, path)))
                    broken.Add(project + " -> " + path);
            }
        }

        Assert.True(
            broken.Count == 0,
            "source pin: these <Compile Remove> paths name files that do not exist. A Remove that " +
            "matches nothing is a silent no-op, so the build it was filtering is no longer " +
            "filtered:" + Environment.NewLine + string.Join(Environment.NewLine, broken));
    }

    /// <summary>
    /// Every literal path in one attribute of the project's <c>Compile</c> items, project-relative.
    /// Wildcards are skipped (a glob that matches nothing is legal MSBuild and is how the
    /// <c>Effects\*.cs</c> / <c>Ui\*.cs</c> removes are written), and so is anything holding an
    /// unexpanded MSBuild property, which this reader cannot evaluate and must not guess at.
    /// </summary>
    private static IEnumerable<string> CompilePaths(string csprojPath, string attribute)
    {
        XDocument doc = XDocument.Load(csprojPath);
        foreach (XElement item in doc.Descendants().Where(e => e.Name.LocalName == "Compile"))
        {
            string? value = item.Attribute(attribute)?.Value;
            if (value == null) continue;

            foreach (string entry in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                string path = entry.Trim();
                if (path.Length == 0) continue;
                if (path.Contains('*') || path.Contains('?')) continue;
                if (path.Contains("$(", StringComparison.Ordinal)) continue;
                yield return path.Replace('\\', Path.DirectorySeparatorChar);
            }
        }
    }

    /// <summary>Same walk the other repository-reading suites use (see ReloadHardeningTests).</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Progenitor.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }
}
