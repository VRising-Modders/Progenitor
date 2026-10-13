using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Samples are what mod authors copy, so they store data where it behaves correctly with wipes and
/// world restores (docs/modules.md "Where should my data go?"): world state in <c>ctx.Save</c>,
/// data that survives wipes in <c>ctx.ServerSave</c>, settings and preferences in config. A sample
/// that writes its own files with <c>System.IO</c> instead keeps state that a wipe does not archive
/// and a restore does not roll back. This scan flags every <c>System.IO</c> write in
/// <c>samples/</c>; a sample that has a real reason lists it in <see cref="Allowlist"/> with that
/// reason.
///
/// <para><b>Why every write and not only writes "under the game folders".</b> Where a path points
/// is decided at run time (config, <c>Paths.BepInExRootPath</c>), so a static scan cannot tell a
/// game folder from any other; and a sample has no business writing outside the game folders
/// either. Reads are not flagged. <c>src/</c> is not scanned: the framework writes its own files
/// (the stores themselves, backups, logs) by design. Comments and string contents are blanked
/// first, with the same blanking as <see cref="DirectKeyReadScanner"/>.</para>
/// </summary>
public class SampleFileWriteSourceTests
{
    /// <summary>Sample files allowed to write files, each with its reason. Repository-relative.</summary>
    internal static readonly IReadOnlyDictionary<string, string> Allowlist = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["samples/Progenitor.FilmExample/Plugin.cs"] =
            "Client-only working folder exchanged with tools outside the game (capture requests it deletes "
            + "after reading, marked shots it appends); not world state or server data. Reason in FilmPaths "
            + "and the sample's README.",
    };

    private static readonly Regex Pattern = new(
        @"\bFile\.(?:Write\w*|Append\w*|Create\w*|Move|Copy|Delete|Replace|Open|OpenWrite|Encrypt|SetAttributes)\s*\("
        + @"|\bDirectory\.(?:Create\w*|Delete|Move)\s*\("
        + @"|\bnew\s+(?:System\.IO\.)?(?:StreamWriter|FileStream|BinaryWriter)\s*\(",
        RegexOptions.CultureInvariant);

    internal static IReadOnlyList<(int Line, string Match)> Scan(string source)
    {
        string code = DirectKeyReadScanner.CodeOnly(source);
        var hits = new List<(int, string)>();
        string[] lines = code.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            foreach (Match m in Pattern.Matches(lines[i]))
                hits.Add((i + 1, m.Value));
        return hits;
    }

    [Fact]
    public void No_sample_writes_files_with_System_IO_outside_the_allowlist()
    {
        string repo = SourcePin.RepoRoot();
        var offenders = new List<string>();
        foreach (string file in SampleFiles(repo))
        {
            string relative = Relative(repo, file);
            if (Allowlist.ContainsKey(relative)) continue;
            foreach (var hit in Scan(File.ReadAllText(file)))
                offenders.Add(relative + ":" + hit.Line + ": " + hit.Match);
        }

        Assert.True(
            offenders.Count == 0,
            "A sample writes files directly. Keep world state in ctx.Save, data that survives wipes in "
            + "ctx.ServerSave, and settings in config (docs/modules.md \"Where should my data go?\"), or "
            + "add the file to the allowlist with its reason:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Every_allowlisted_file_exists_and_still_writes()
    {
        string repo = SourcePin.RepoRoot();
        var stale = new List<string>();
        foreach (var entry in Allowlist)
        {
            string path = Path.Combine(repo, entry.Key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) stale.Add(entry.Key + " (missing)");
            else if (Scan(File.ReadAllText(path)).Count == 0) stale.Add(entry.Key + " (no write left)");
        }

        Assert.True(stale.Count == 0, "Remove stale allowlist entries:\n  " + string.Join("\n  ", stale));
    }

    [Theory]
    [InlineData("File.WriteAllText(path, json);")]
    [InlineData("System.IO.File.AppendAllText(path, line);")]
    [InlineData("File.Delete(path);")]
    [InlineData("File.Move(a, b, overwrite: true);")]
    [InlineData("using var fs = File.Create(path);")]
    [InlineData("Directory.CreateDirectory(dir);")]
    [InlineData("using var w = new StreamWriter(path);")]
    [InlineData("var fs = new System.IO.FileStream(path, FileMode.Create);")]
    public void Planted_writes_are_reported(string code)
    {
        Assert.Single(Scan("class C {\n  void M() {\n    " + code + "\n  }\n}\n"));
    }

    [Theory]
    [InlineData("var text = File.ReadAllText(path);")]
    [InlineData("if (File.Exists(path)) { }")]
    [InlineData("var info = new System.IO.FileInfo(path);")]
    [InlineData("// File.WriteAllText(path, json);")]
    [InlineData("Log(\"File.Delete(path) is not called\");")]
    [InlineData("var take = TakeFile.Load(dir);")]
    [InlineData("ctx.Save.Set(\"k\", 1); ctx.ServerSave.Set(\"k\", 1);")]
    public void Reads_comments_literals_and_stores_are_not_reported(string code)
    {
        Assert.Empty(Scan("class C {\n  void M() {\n    " + code + "\n  }\n}\n"));
    }

    private static IEnumerable<string> SampleFiles(string repo)
    {
        string dir = Path.Combine(repo, "samples");
        Assert.True(Directory.Exists(dir), "samples/ not found under " + repo);
        foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            string relative = Relative(repo, file);
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal))
                continue;
            yield return file;
        }
    }

    private static string Relative(string repo, string file) =>
        Path.GetRelativePath(repo, file).Replace(Path.DirectorySeparatorChar, '/');
}
