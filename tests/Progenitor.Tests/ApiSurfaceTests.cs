using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The fence between contract and implementation (job J17).
///
/// <para><b>Why this test exists.</b> <c>public</c> is not the API boundary in this repository and
/// never has been: the game-free half of nearly every module is public so the plain xUnit suite can
/// compile it, Bootstrap and Core meet through public shapes because neither may reference the
/// other's assembly across a Core swap, and <c>Progenitor.Dev</c> is a whole public assembly that
/// is never shipped. Before J17, 328 of the 486 public types in <c>src/</c> appeared nowhere in
/// <c>docs/api.md</c>, so a reader of the source could not tell a contract from an implementation
/// detail. <c>docs/api.md</c> now names all of them — the module tables for contract, the appendix
/// "Framework-internal public types" for the rest, with the reason each one is public — and this
/// test is what stops that from rotting: add a public type and forget the doc, and the suite says
/// which one.</para>
///
/// <para><b>What it does not check.</b> It checks that a name is <em>mentioned</em>, not that it is
/// mentioned in the right half. Moving a type between the tables and the appendix is a judgment
/// call and stays one; the appendix says so about its own borderline rows.</para>
/// </summary>
public class ApiSurfaceTests
{
    static readonly string[] Projects = ["Progenitor.Core", "Progenitor.Bootstrap", "Progenitor.Dev", "Progenitor.Testing"];

    /// <summary>
    /// The declaration form <c>docs/api.md</c> is audited against. Deliberately the same shape as
    /// the J17 sweep that produced the appendix, so the file and the test count the same set.
    /// </summary>
    static readonly Regex Declaration = new(
        @"^\s*public\s+(?:(?:static|sealed|abstract|readonly|unsafe|partial|ref)\s+)*"
        + @"(?:(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)"
        + @"|delegate\s+[^(]*?(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^(]*>)?\s*\()",
        RegexOptions.Compiled);

    [Fact]
    public void Every_public_type_in_src_is_named_somewhere_in_api_md()
    {
        string api = SourcePin.ReadFile("docs", "api.md");
        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach ((string name, string where) in PublicTypes())
        {
            if (Regex.IsMatch(api, @"(?<![A-Za-z0-9_])" + Regex.Escape(name) + @"(?![A-Za-z0-9_])"))
                continue;
            missing.Add(name + "  (" + where + ")");
        }

        Assert.True(
            missing.Count == 0,
            missing.Count + " public type(s) are named nowhere in docs/api.md. A type that is "
            + "contract gets a row in the module table it belongs to; everything else gets a row in "
            + "the appendix \"Framework-internal public types\" with the reason it is public:"
            + Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// The appendix is only worth having if it is the whole remainder, so pin the two counts it
    /// states about itself. This is the sentence a reader takes on trust when they decide not to
    /// read <c>src/</c>.
    /// </summary>
    [Fact]
    public void The_appendix_states_the_public_type_count_it_was_built_from()
    {
        string api = SourcePin.ReadFile("docs", "api.md");
        int total = PublicTypes().Select(t => t.Name).Distinct(StringComparer.Ordinal).Count();

        Assert.Contains("declare **" + total + "** public types", api);
        Assert.Contains("declare " + total + " public types", api);
    }

    static List<(string Name, string Where)> PublicTypes()
    {
        string root = SourcePin.RepoRoot();
        var found = new List<(string, string)>();

        foreach (string project in Projects)
        {
            string dir = Path.Combine(root, "src", project);
            Assert.True(Directory.Exists(dir), dir + " not found");

            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/obj/", StringComparison.Ordinal)) continue;
                if (relative.Contains("/bin/", StringComparison.Ordinal)) continue;

                string[] lines = File.ReadAllLines(file);
                for (int i = 0; i < lines.Length; i++)
                {
                    var match = Declaration.Match(lines[i]);
                    if (match.Success)
                        found.Add((match.Groups["name"].Value, relative + ":" + (i + 1)));
                }
            }
        }

        Assert.True(found.Count > 400, "the sweep found only " + found.Count + " public types; the regex is wrong");
        return found;
    }
}
