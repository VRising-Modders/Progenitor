using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Nothing in the repository may identify a maintainer or their machine (project rule, 2026-10-06). This scans every
/// tracked file (and every entry of a tracked zip) for:
/// <list type="bullet">
/// <item><b>Personal identifiers</b>: names, handles, the user's SteamID, IP and e-mail. They are checked by a rolling
/// hash of their lower-cased text, so the list itself does not put them back into the tree. To add one, append its
/// length and <see cref="Hash"/> value to <see cref="Personal"/>.</item>
/// <item><b>SteamIDs</b>: any SteamID64 outside the placeholder range the tests use
/// (<c>76561198000000000</c>–<c>76561198000000999</c>, plus <c>76561190000000000</c>).</item>
/// <item><b>Machine paths</b>: this machine's workspace folder name, a user-profile path (other than a placeholder or
/// a CI runner's), and the local evidence folder. Docs use the placeholders in docs/dev-loop.md ("Path placeholders");
/// tools resolve the workspace at run time (tools/common/Workspace.ps1, workspace.mjs).</item>
/// </list>
/// <para><b>Exception</b>: this file is exempt from the machine-path rule because it spells the patterns.</para>
/// </summary>
public class IdentifierGuardTests
{
    /// <summary>(length, hash) of each forbidden personal token, lower-cased. See <see cref="Hash"/>.</summary>
    private static readonly (int Length, ulong Hash, string What)[] Personal =
    {
        (8, 0x783863647240546AUL, "a personal handle (1)"),
        (6, 0x65DB0218B4DA6159UL, "a personal handle (2)"),
        (5, 0x07392DD999EAC909UL, "the Windows user name"),
        (7, 0x3CB53CC9CB85CFB7UL, "a player character name"),
        (10, 0x16E125887111EB95UL, "an e-mail name"),
        (10, 0x6550B93596BA1A2BUL, "a personal name"),
        (17, 0xF9B4DDDE9A87C0CCUL, "the user's SteamID"),
        (17, 0xF9B4DDDE9A87C0C6UL, "the user's SteamID (as a JSON double)"),
        (17, 0xEAA8AA75E152BAB2UL, "the user's Steam P2P id"),
        (13, 0xD4BFC7D297D19D95UL, "the user's public IP"),
    };

    private const string SelfPath = "tests/Progenitor.Tests/IdentifierGuardTests.cs";

    private static readonly Regex SteamId = new(@"(?<![0-9])7656119[0-9]{10}(?![0-9])", RegexOptions.Compiled);

    private static readonly (Regex Pattern, string What)[] MachinePaths =
    {
        // One narrow allowance: the V Rising Mod Community's public Discord invite, https://vrisingmods.com/discord
        // (SupportBundle.DiscordInviteUrl, linked as "Modding Discord" from Thunderstore's V Rising page), whose domain
        // happens to spell the workspace folder. Exactly that host and path; anything else with the name is still caught.
        (new Regex(@"VRisingMods(?!\.com/discord(?![A-Za-z0-9]))", RegexOptions.IgnoreCase | RegexOptions.Compiled), "this machine's workspace folder"),
        (new Regex(@"LocalClaude", RegexOptions.IgnoreCase | RegexOptions.Compiled), "a local evidence folder"),
        (new Regex(@"(?<![A-Za-z])[A-Za-z]:[\\/]+Users[\\/]+(?!(?:<[^>]*>|RUNNE~1|runneradmin|Public|Default)(?:[\\/""'\s]|$))[^\\/\s""'<>]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled), "a user-profile path"),
    };

    /// <summary>Polynomial rolling hash of lower-cased UTF-16 code units, base 1099511628211, mod 2^64.</summary>
    internal static ulong Hash(string text)
    {
        ulong h = 0;
        foreach (char c in text.ToLowerInvariant()) h = unchecked(h * Base + c);
        return h;
    }

    private const ulong Base = 1099511628211UL;

    [Fact]
    public void Only_the_communitys_Discord_invite_may_spell_the_workspace_folder_name()
    {
        var found = new List<string>();
        Scan("probe.txt", Encoding.ASCII.GetBytes("https://vrisingmods.com/discord"), found);
        Assert.Empty(found);
        foreach (string probe in new[] { "VRisingMods/Progenitor", "https://vrisingmods.com/discords", "https://vrisingmods.com/other", "vrisingmods.org/discord" })
        {
            var hit = new List<string>();
            Scan("probe.txt", Encoding.ASCII.GetBytes(probe), hit);
            Assert.Single(hit);
        }
    }

    [Fact]
    public void The_hash_list_is_self_consistent()
    {
        // Guards the scanner itself: a known placeholder hashes to the value an independent implementation gives
        // (the list was computed the same way), and the rolling scan sees it in the middle of a line.
        Assert.Equal(0x48F19E2D60050E7EUL, Hash("Example"));
        Assert.Equal(Hash("example"), HashWindow("an example here".ToLowerInvariant(), 3, 7));
        var found = new List<string>();
        Scan("probe.txt", Encoding.ASCII.GetBytes("x " + "7656119" + "7993547076 y"), found);
        Assert.Equal(2, found.Count); // the hashed SteamID and the range rule
        Assert.All(Personal, p => Assert.InRange(p.Length, 4, 64));
    }

    /// <summary>
    /// The two old handles (the retired plugin id prefix among them) are caught in any letter case and inside a
    /// dotted id or a URL. They are built from character codes so this file does not spell them. There is no
    /// allowlist: no tracked file needs either handle (the repository URL is not written in the tree, and NOTICE's
    /// third-party records do not carry them). If one is ever needed, it is an explicit (path, line) exception here.
    /// </summary>
    [Fact]
    public void The_old_handles_are_caught_in_any_case()
    {
        string first = new(new[] { (char)103, (char)114, (char)101, (char)101, (char)110, (char)101, (char)121, (char)101 });
        string second = new(new[] { (char)102, (char)97, (char)110, (char)103, (char)108, (char)121 });
        string mixed = char.ToUpperInvariant(first[0]) + first.Substring(1, 4) + char.ToUpperInvariant(first[5]) + first.Substring(6);
        foreach (string probe in new[]
                 {
                     first + ".progenitor", first.ToUpperInvariant(), "Team_" + mixed + "-PhialStore", "https://example.com/" + mixed + "0/repo",
                     second, "by " + char.ToUpperInvariant(second[0]) + second.Substring(1) + ".",
                 })
        {
            var found = new List<string>();
            Scan("probe.md", Encoding.UTF8.GetBytes("x " + probe + " y"), found);
            Assert.True(found.Count == 1 && found[0].Contains("a personal handle", StringComparison.Ordinal),
                "not caught exactly once: " + probe.Length + " chars, findings: " + string.Join("; ", found));
        }
    }

    [Fact]
    public void No_tracked_file_carries_a_personal_identifier_or_a_machine_path()
    {
        string root = RepoRoot();
        var findings = new List<string>();
        foreach (string rel in TrackedFiles(root))
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(full)) continue; // deleted in the working tree, not yet committed
            byte[] bytes = File.ReadAllBytes(full);
            Scan(rel, bytes, findings);
            if (rel.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
                    foreach (var entry in zip.Entries)
                    {
                        using var s = entry.Open();
                        using var m = new MemoryStream();
                        s.CopyTo(m);
                        Scan(rel + "!" + entry.FullName, m.ToArray(), findings);
                    }
                }
                catch (InvalidDataException) { findings.Add(rel + ": not a readable zip"); }
            }
        }

        Assert.True(findings.Count == 0,
            "Identifying text in tracked files (see IdentifierGuardTests for the rules and placeholders):\n" + string.Join("\n", findings.Take(200)));
    }

    private static void Scan(string rel, byte[] bytes, List<string> findings)
    {
        // Latin-1 keeps one char per byte, so UTF-8 text and ASCII strings inside binaries both match.
        string text = Encoding.Latin1.GetString(bytes);
        string lower = text.ToLowerInvariant();
        string path = rel.Split('!')[0];

        foreach (var group in Personal.GroupBy(p => p.Length))
        {
            int len = group.Key;
            if (lower.Length < len) continue;
            var wanted = group.ToDictionary(p => p.Hash, p => p.What);
            ulong pow = 1;
            for (int i = 1; i < len; i++) pow = unchecked(pow * Base);
            ulong h = HashWindow(lower, 0, len);
            for (int i = 0; ; i++)
            {
                if (wanted.TryGetValue(h, out string? what))
                    findings.Add(rel + ":" + LineOf(text, i) + ": " + what);
                if (i + len >= lower.Length) break;
                h = unchecked((h - lower[i] * pow) * Base + lower[i + len]);
            }
        }

        foreach (Match m in SteamId.Matches(text))
        {
            ulong id = ulong.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture);
            bool placeholder = (id >= 76561198000000000UL && id <= 76561198000000999UL) || id == 76561190000000000UL;
            if (!placeholder) findings.Add(rel + ":" + LineOf(text, m.Index) + ": a SteamID outside the placeholder range");
        }

        if (string.Equals(path, SelfPath, StringComparison.Ordinal)) return;
        foreach (var (pattern, what) in MachinePaths)
            foreach (Match m in pattern.Matches(text))
                findings.Add(rel + ":" + LineOf(text, m.Index) + ": " + what);
    }

    private static ulong HashWindow(string lower, int start, int length)
    {
        ulong h = 0;
        for (int i = start; i < start + length; i++) h = unchecked(h * Base + lower[i]);
        return h;
    }

    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++) if (text[i] == '\n') line++;
        return line;
    }

    private static IEnumerable<string> TrackedFiles(string root)
    {
        var psi = new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var git = Process.Start(psi) ?? throw new InvalidOperationException("git could not be started");
        string output = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, "git ls-files failed: " + git.StandardError.ReadToEnd());
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Progenitor.sln"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }
}
