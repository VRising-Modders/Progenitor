using System;
using System.IO;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The shared helpers for the source pins in this suite — the tests that read a production file
/// as text because the code in it cannot be compiled here (it is <c>unsafe</c>, or it binds
/// ProjectM / Harmony / Unity, or it needs an <c>Entity</c> nothing in the suite can make).
///
/// <para><b>Why this file exists rather than a helper per test class.</b> A source pin is only
/// worth having if it survives being edited around, and this suite has now hit the two ways it
/// does not, on three separate jobs. Job FUI26 found the first — <c>Assert.Contains</c> over
/// source text is satisfied by the pinned line <b>commented out</b> — and wrote
/// <see cref="AssertStatement"/> for it. Job FDOC5 hit the second: a pinned call
/// <b>wrapped in a ternary</b> that never takes the branch containing it still contains the text,
/// so <see cref="AssertStatement"/> passes over a call that no longer runs. Both belong in one
/// place, because the next job to need one must find them rather than write a third.</para>
///
/// <para><b>Nothing in this suite reads production source with <c>Assert.Contains</c> any more.</b>
/// Job FUI27 audited every source pin and found FUI26's weakness was not two pins but the idiom:
/// with every line of every production file commented out, 3,979 of 4,001 tests still passed.
/// A pin now goes through <see cref="AssertStatement"/>, <see cref="AssertStatementExactly"/> or
/// <see cref="AssertUncommented"/>, all three of which read code and not comments. Use
/// <c>Assert.Contains</c> over source text only where the pinned text <em>is</em> a comment.</para>
///
/// <para><b>The one comment form none of the three sees: a <c>/* … */</c> block</b> (R48 finding
/// 5). The shared walker strips a <c>//</c> that is not inside a literal, line by line, and tracks
/// no state across lines — so a pinned statement wrapped in a block comment opened on an earlier
/// line still reads as live code and every pin over it still passes. This is stated here, beside
/// the guarantee, rather than only on the private helper, because it is the neighbor of the
/// mutation the file was written for: "comment every line out" is caught, "comment the region out"
/// is not. It is a known residual and not a claim: <see cref="SourcePinContractTests"/> pins the
/// residual itself on a string literal, and — since R49 finding 7 pointed out that the sentence
/// that used to sit here ("no pin in this suite is inside a block comment") was an assertion about
/// four production files that nothing measured —
/// <see cref="SourcePinContractTests.No_cs_file_under_src_samples_or_tools_opens_a_block_comment_across_lines"/>
/// now measures the reachability instead: no <c>.cs</c> file under <c>src/</c>, <c>samples/</c> or
/// <c>tools/</c> — the three roots a pin in this suite reads C# from (R50 finding 9; the pins over
/// <c>samples/Progenitor.Sample</c> and <c>tools/enc/EncWatch.cs</c> were outside FDOC7's original
/// <c>src/</c>-only scan) — opens a <c>/* … */</c> that
/// runs past the end of its line, so there is nowhere for a pin to be hiding today, and the day
/// someone wraps a region while debugging that test goes red rather than the pins going quiet.
/// If a future job needs the hole itself closed, the place is <c>CodeOf</c>'s caller (the walker
/// would have to carry an in-block flag between lines), not the pins.</para>
/// </summary>
internal static class SourcePin
{
    /// <summary>
    /// Assert that <paramref name="statement"/> begins a real statement line and is not inside a
    /// comment (job FUI26). A plain <c>Assert.Contains</c> is satisfied by
    /// <c>// ResetSceneCaches();</c>, which is exactly the edit these pins exist to catch — two of
    /// them survived that mutation until this helper existed.
    ///
    /// <para>This is a <c>StartsWith</c> test, so it still passes over a line that continues into
    /// something else. Where the <em>whole</em> statement is the assertion — a call that must not
    /// be made conditional — use <see cref="AssertStatementExactly"/>.</para>
    /// </summary>
    internal static void AssertStatement(string body, string statement)
    {
        if (AnyLive(body, (code, trimmed) => trimmed.StartsWith(statement, StringComparison.Ordinal))) return;

        Assert.Fail("no uncommented statement line `" + statement + "` in:\n" + body);
    }

    /// <summary>
    /// The strict form: a line whose <b>code part</b>, trimmed, <b>is</b>
    /// <paramref name="statement"/>. Use it when the pin is that a call happens
    /// <em>unconditionally</em> — <c>AssertStatement</c> is satisfied by the same call sitting in
    /// the untaken half of a ternary, or behind an <c>if</c> that was added on the same line, and
    /// a pin that a mutation walks around proves nothing (job FDOC5).
    ///
    /// <para><b>"The code part", not "the whole line"</b> (R48 finding 5). A trailing
    /// <c>// note</c> after the statement leaves the pin standing; anything else on the line —
    /// a condition, a second statement, a ternary — breaks it, which is the whole point. The
    /// looser reading is deliberate: a comment cannot make the pinned call conditional or stop it
    /// running, so refusing it would only tax explaining the line. This helper's contract was
    /// stated as "the whole trimmed line" while the code compared the code part, and the doc is
    /// what moved: 132 call sites already depend on the code-part reading, and nothing this pin
    /// exists to catch can hide in a trailing comment.</para>
    /// </summary>
    internal static void AssertStatementExactly(string body, string statement)
    {
        if (AnyLive(body, (code, trimmed) => string.Equals(trimmed, statement, StringComparison.Ordinal))) return;

        Assert.Fail("no line whose code part, trimmed, is exactly `" + statement + "` in:\n" + body);
    }

    /// <summary>
    /// The general form, for a pin whose text is not the start of a statement — a call in the
    /// middle of an expression, an argument, an attribute argument, a symbol that only has to
    /// appear. <paramref name="fragment"/> must occur in the <em>code</em> part of some line:
    /// not on a commented-out line, and not after a <c>//</c> on a live one.
    ///
    /// <para>Job FUI27 converted the suite to this and to <see cref="AssertStatement"/>. The
    /// measurement that forced it: with every line of every production file commented out, 3,979
    /// of the suite's 4,001 tests still passed, because <c>Assert.Contains</c> over source text
    /// reads a comment exactly as it reads code.</para>
    /// </summary>
    internal static void AssertUncommented(string body, string fragment)
    {
        if (AnyLive(body, (code, trimmed) => code.Contains(fragment, StringComparison.Ordinal))) return;

        Assert.Fail("no uncommented line containing `" + fragment + "` in:\n" + body);
    }

    /// <summary>
    /// The one line walk all three assertions share: each line's code part and that part
    /// trimmed, with blank and fully commented lines skipped.
    /// </summary>
    private static bool AnyLive(string body, Func<string, string, bool> predicate)
    {
        foreach (string line in body.Split('\n'))
        {
            string code = CodeOf(line);
            string trimmed = code.Trim();
            if (trimmed.Length == 0) continue;
            if (predicate(code, trimmed)) return true;
        }

        return false;
    }

    /// <summary>
    /// The part of <paramref name="line"/> before a <c>//</c> that is not inside a string or
    /// character literal. Verbatim (<c>@"..."</c>) and raw (<c>"""..."""</c>) literals are
    /// understood, so a pinned <c>"http://..."</c> is code and not a comment. A block comment
    /// opened on an earlier line is not tracked; no pin in this suite needs that, and the residual
    /// is declared in this class's own summary where a <em>user</em> of the pins reads it.
    /// </summary>
    private static string CodeOf(string line)
    {
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') return line.Substring(0, i);
            if (c == '@' && i + 1 < line.Length && line[i + 1] == '"')
            {
                i = SkipVerbatim(line, i + 2);
                continue;
            }

            if (c == '"' && i + 2 < line.Length && line[i + 1] == '"' && line[i + 2] == '"')
            {
                int close = line.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                if (close < 0) return line;
                i = close + 2;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                i = SkipQuoted(line, i + 1, c);
                continue;
            }
        }

        return line;
    }

    /// <summary>Index of the closing quote of an ordinary literal, or the last index.</summary>
    private static int SkipQuoted(string line, int from, char quote)
    {
        for (int i = from; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == quote) return i;
        }

        return line.Length - 1;
    }

    /// <summary>Index of the closing quote of a verbatim literal, where <c>""</c> is an escape.</summary>
    private static int SkipVerbatim(string line, int from)
    {
        for (int i = from; i < line.Length; i++)
        {
            if (line[i] != '"') continue;
            if (i + 1 < line.Length && line[i + 1] == '"') { i++; continue; }
            return i;
        }

        return line.Length - 1;
    }

    /// <summary>
    /// The negative pin: no uncommented line of <paramref name="body"/> contains
    /// <paramref name="fragment"/> (job FUI29). Use it when the assertion is that a construct has
    /// <em>gone</em> — a shadowed field that has to stay derived, a call that must never come
    /// back — where a positive pin can only say what is there.
    ///
    /// <para>It skips comment lines for the same reason the positive pins do: the prose beside a
    /// removal usually has to name the thing removed, and a pin that forbade the words would
    /// forbid explaining them.</para>
    /// </summary>
    internal static void AssertNoStatementContaining(string body, string fragment)
    {
        foreach (string line in body.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
            if (trimmed.StartsWith("*", StringComparison.Ordinal)) continue;
            if (trimmed.Contains(fragment, StringComparison.Ordinal))
                Assert.Fail("uncommented line contains `" + fragment + "`: " + trimmed);
        }
    }

    /// <summary>Read one repository file as text, for a pin over a whole file rather than a body.</summary>
    internal static string ReadFile(params string[] relativeParts) =>
        File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(relativeParts)));

    /// <summary>The braced body of the first declaration matching <paramref name="signature"/>.</summary>
    internal static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "not found: " + signature);
        int open = source.IndexOf('{', start);
        Assert.True(open > start, "no body for " + signature);

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source.Substring(open, i - open + 1);
        }

        Assert.Fail("unbalanced braces after " + signature);
        return "";
    }

    /// <summary>The repository root, found by walking up to <c>Progenitor.sln</c>.</summary>
    internal static string RepoRoot()
    {
        string dir = Path.GetFullPath(AppContext.BaseDirectory);
        for (int i = 0; i < 8; i++)
        {
            if (File.Exists(Path.Combine(dir, "Progenitor.sln"))) return dir;
            var parent = Directory.GetParent(dir);
            if (parent == null) break;
            dir = parent.FullName;
        }

        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }
}

/// <summary>
/// The pins on the pin helpers themselves (job FDOC6, R48 finding 5). Two contracts that were
/// stated one way and enforced another, or stated nowhere at all, and are now written down in
/// <see cref="SourcePin"/>'s own documentation — so they are asserted here rather than left to be
/// rediscovered by the next job that trusts the summary.
/// </summary>
public class SourcePinContractTests
{
    /// <summary>
    /// <see cref="SourcePin.AssertStatementExactly"/> compares the <b>code part</b> of a line, so a
    /// trailing <c>//</c> comment leaves the pin standing. This is the documented reading; before
    /// FUI27's refactor the whole trimmed line had to match, and the summary said so for one
    /// commit longer than the code did.
    /// </summary>
    [Fact]
    public void Exact_pin_survives_a_trailing_comment_and_nothing_else()
    {
        SourcePin.AssertStatementExactly("    Plugin.Frame += SettlePump; // armed here\n", "Plugin.Frame += SettlePump;");
        SourcePin.AssertStatementExactly("    Plugin.Frame += SettlePump;\n", "Plugin.Frame += SettlePump;");

        // Anything that is code, however small, still breaks it: that is what the helper is for.
        Assert.Throws<Xunit.Sdk.FailException>(() => SourcePin.AssertStatementExactly(
            "    if (x) Plugin.Frame += SettlePump;\n", "Plugin.Frame += SettlePump;"));
        Assert.Throws<Xunit.Sdk.FailException>(() => SourcePin.AssertStatementExactly(
            "    Plugin.Frame += SettlePump; Drop();\n", "Plugin.Frame += SettlePump;"));
        Assert.Throws<Xunit.Sdk.FailException>(() => SourcePin.AssertStatementExactly(
            "    // Plugin.Frame += SettlePump;\n", "Plugin.Frame += SettlePump;"));
    }

    /// <summary>
    /// The residual, pinned as a residual: a statement inside a <c>/* … */</c> block opened on an
    /// earlier line satisfies all three helpers, because the walker strips <c>//</c> per line and
    /// carries no state between lines. If someone closes that hole this test fails and is the
    /// place to record that the class header's paragraph must go with it — which is exactly the
    /// notice a residual documented in prose alone would not give.
    /// </summary>
    [Fact]
    public void Block_comments_are_the_documented_residual_of_all_three_helpers()
    {
        const string blocked = "/*\nPlugin.Frame += SettlePump;\n*/\n";

        SourcePin.AssertStatement(blocked, "Plugin.Frame += SettlePump;");
        SourcePin.AssertStatementExactly(blocked, "Plugin.Frame += SettlePump;");
        SourcePin.AssertUncommented(blocked, "SettlePump");
    }

    /// <summary>
    /// FDOC7 (R49 finding 7), widened by FCFG4 (R50 finding 9). The residual above is only harmless
    /// while nothing a pin reads is inside a block comment, and that half was prose. It is
    /// measurable and cheap, so here it is measured: no <c>.cs</c> file under any directory a pin
    /// reads opens a <c>/* … */</c> that is still open at the end of its line. Single-line
    /// <c>catch { /* best-effort */ }</c> is everywhere and hides nothing — the walker sees the
    /// whole line as code either way — so only the multi-line form is refused, which is exactly
    /// the form that can swallow a pinned statement.
    ///
    /// <para>Scanning trees rather than the files individual pins name is deliberate: the pins
    /// are spread over dozens of test classes and nothing enumerates their paths, so a per-pin
    /// scan would only cover the pins that happened to run. The trees have to be all of them,
    /// though: FDOC7 scanned <c>src/</c> alone while <c>OwnedPanelTests</c>, <c>PrefabIconTests</c>,
    /// <c>SampleBuffsTests</c>, <c>SampleCommandsTests</c> and <c>SampleModsTests</c> pin
    /// <c>samples/Progenitor.Sample/*.cs</c> and <c>EncHotReloadTests</c> pins
    /// <c>tools/enc/EncWatch.cs</c>, so a region commented out there disarmed those pins with the
    /// scan looking elsewhere. The other roots pins read (<c>docs/</c>, the PowerShell under
    /// <c>tools/</c>) hold no C#. The answer over all three is zero.</para>
    /// </summary>
    [Fact]
    public void No_cs_file_under_src_samples_or_tools_opens_a_block_comment_across_lines()
    {
        var offenders = new System.Collections.Generic.List<string>();
        foreach (string name in new[] { "src", "samples", "tools" })
        {
            string root = Path.Combine(SourcePin.RepoRoot(), name);
            Assert.True(Directory.Exists(root), name + "/ not found under " + SourcePin.RepoRoot());

            foreach (string path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                // Build output is not source a pin can read, and `tools/` carries two csproj trees.
                string relative = path.Substring(root.Length + 1);
                if (relative.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || relative.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    continue;

                string[] lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue; // /// docs quote paths like reload/*.dll

                    int at = lines[i].IndexOf("/*", StringComparison.Ordinal);
                    while (at >= 0)
                    {
                        int close = lines[i].IndexOf("*/", at + 2, StringComparison.Ordinal);
                        if (close < 0)
                        {
                            offenders.Add(name + "/" + relative + ":" + (i + 1) + " " + trimmed);
                            break;
                        }

                        at = lines[i].IndexOf("/*", close + 2, StringComparison.Ordinal);
                    }
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "a block comment spans lines in production source, so every SourcePin over that region "
            + "is silently disarmed (the walker strips // per line and carries no state between "
            + "lines). Close it, or close the walker's hole:\n" + string.Join("\n", offenders));
    }
}
