using System;
using System.Collections.Generic;
using Xunit;
using Boot = Progenitor.Bootstrap;
using Core = Progenitor.Reload;

namespace Progenitor.Tests;

/// <summary>
/// Job FRLD. The reload folder and the Core generation slot are the two automatic
/// code-loading paths Progenitor adds on top of BepInEx, and until this job they logged a
/// filename and loaded anything settled. These tests pin the two properties that fix that:
/// every load line carries the file's SHA-256 and size, and an optional
/// <c>BepInEx/config/progenitor-allow.txt</c> turns the folder into "these exact bytes only",
/// failing closed on anything it cannot parse.
///
/// The policy exists twice (Bootstrap owns the reloader in the shipped layout; Core keeps its
/// own copy for the Bootstrap-less path, exactly as <c>ReloaderHost</c> mirrors
/// <c>PluginReloader</c>). Every table below runs through <b>both</b> copies.
/// </summary>
public class ReloadAllowPolicyTests
{
    // Known vectors, so a "clever" hashing change cannot quietly redefine the identity line.
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string AbcHash = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    private static byte[] Abc() => new byte[] { 0x61, 0x62, 0x63 };

    [Fact]
    public void Sha256Hex_is_lowercase_full_length_and_matches_the_known_vectors()
    {
        Assert.Equal(EmptyHash, Boot.ReloadAllowPolicy.Sha256Hex(Array.Empty<byte>()));
        Assert.Equal(AbcHash, Boot.ReloadAllowPolicy.Sha256Hex(Abc()));
        Assert.Equal(EmptyHash, Core.ReloadAllowPolicy.Sha256Hex(Array.Empty<byte>()));
        Assert.Equal(AbcHash, Core.ReloadAllowPolicy.Sha256Hex(Abc()));
        Assert.Equal(64, Boot.ReloadAllowPolicy.HashLength);
        Assert.Equal(AbcHash, Boot.ReloadAllowPolicy.Sha256Hex(Abc()).ToLowerInvariant());
    }

    [Fact]
    public void Short_is_the_first_sixteen_hex_characters()
    {
        Assert.Equal(16, Boot.ReloadAllowPolicy.ShortHashLength);
        Assert.Equal("ba7816bf8f01cfea", Boot.ReloadAllowPolicy.Short(AbcHash));
        Assert.Equal("ba7816bf8f01cfea", Core.ReloadAllowPolicy.Short(AbcHash));
        // Never throws on a short or empty input.
        Assert.Equal("abc", Boot.ReloadAllowPolicy.Short("abc"));
        Assert.Equal("", Boot.ReloadAllowPolicy.Short(null));
    }

    [Fact]
    public void EffectiveName_strips_the_staging_suffix_so_a_next_matches_the_live_row()
    {
        Assert.Equal("Progenitor.dll", Boot.ReloadAllowPolicy.EffectiveName("Progenitor.dll.next"));
        Assert.Equal("Progenitor.dll", Boot.ReloadAllowPolicy.EffectiveName("Progenitor.dll"));
        Assert.Equal("Progenitor.dll", Core.ReloadAllowPolicy.EffectiveName("Progenitor.dll.NEXT"));
        Assert.Equal("", Boot.ReloadAllowPolicy.EffectiveName(null));
    }

    /// <summary>
    /// R30 finding 11: SHA256SUMS and the allowlist share one row grammar, so a row from a
    /// release file is a valid allowlist row. <c>TryRowName</c> is that grammar's only reader;
    /// both copies must agree on it, including on what is still malformed.
    /// </summary>
    [Fact]
    public void TryRowName_takes_the_leaf_of_a_qualified_row_and_still_refuses_a_bare_path()
    {
        foreach (var read in new Func<string, (bool ok, string name, string error)>[]
        {
            s => { bool ok = Boot.ReloadAllowPolicy.TryRowName(s, out string n, out string e); return (ok, n, e); },
            s => { bool ok = Core.ReloadAllowPolicy.TryRowName(s, out string n, out string e); return (ok, n, e); },
        })
        {
            Assert.Equal((true, "Progenitor.dll", ""), read("Progenitor.dll"));
            Assert.Equal((true, "Progenitor.dll", ""), read("  Progenitor.dll  "));
            // The per-entry rows package.ps1 writes, in both slash flavors.
            Assert.Equal((true, "Progenitor.dll", ""), read("Progenitor-0.4.0.zip!BepInEx/progenitor-core/Progenitor.dll"));
            Assert.Equal((true, "Progenitor.Sample.dll", ""), read(@"Progenitor-0.4.0.zip!BepInEx\progenitor-reload\Progenitor.Sample.dll"));
            // A zip row is a leaf name already.
            Assert.Equal((true, "Progenitor-0.4.0.zip", ""), read("Progenitor-0.4.0.zip"));

            // Still malformed: a bare path is a typo, not a pasted release row.
            Assert.False(read("BepInEx/plugins/X.dll").ok);
            Assert.False(read(@"BepInEx\plugins\X.dll").ok);
            Assert.False(read("").ok);
            Assert.False(read("Progenitor-0.4.0.zip!").ok);
            Assert.False(read("Progenitor-0.4.0.zip!dir/").ok);
        }

        // One writer: the C# formatter and package.ps1's Format-SumsRow emit this shape.
        Assert.Equal(
            AbcHash + "  Progenitor-0.4.0.zip!BepInEx/progenitor-core/Progenitor.dll",
            Boot.ReloadAllowPolicy.FormatSumsEntryLine(AbcHash, "Progenitor-0.4.0.zip", "BepInEx/progenitor-core/Progenitor.dll"));
        Assert.Equal(
            Boot.ReloadAllowPolicy.FormatSumsEntryLine(AbcHash, "z.zip", "a/b.dll"),
            Core.ReloadAllowPolicy.FormatSumsEntryLine(AbcHash, "z.zip", "a/b.dll"));

        // ...and it round-trips through the reader on both copies.
        string row = Boot.ReloadAllowPolicy.FormatSumsEntryLine(AbcHash, "Progenitor-0.4.0.zip", "BepInEx/progenitor-core/Progenitor.dll");
        var b = Boot.ReloadAllowPolicy.Parse(row);
        Assert.Equal(Boot.AllowListStatus.Active, b.Status);
        Assert.Equal(Boot.AllowDecision.Listed, b.Decide("Progenitor.dll", AbcHash));
        var c = Core.ReloadAllowPolicy.Parse(row);
        Assert.Equal(Core.AllowListStatus.Active, c.Status);
        Assert.Equal(Core.AllowDecision.Listed, c.Decide("Progenitor.dll", AbcHash));
    }

    /// <summary>R30 finding 13: an allowlist path that cannot be resolved is Malformed, not Absent.</summary>
    [Fact]
    public void UnresolvedPath_fails_closed_on_both_copies()
    {
        var boot = Boot.ReloadAllowPolicy.UnresolvedPath("Paths threw");
        Assert.Equal(Boot.AllowListStatus.Malformed, boot.Status);
        Assert.Equal(Boot.AllowDecision.Malformed, boot.Decide("Progenitor.dll", AbcHash));
        Assert.False(Boot.ReloadAllowPolicy.MayLoad(boot.Decide("Progenitor.dll", AbcHash)));
        Assert.Contains(Boot.ReloadAllowPolicy.DisplayPath, boot.Error, StringComparison.Ordinal);
        Assert.Contains("Paths threw", boot.Error, StringComparison.Ordinal);

        var core = Core.ReloadAllowPolicy.UnresolvedPath("Paths threw");
        Assert.Equal(Core.AllowListStatus.Malformed, core.Status);
        Assert.Equal(Core.AllowDecision.Malformed, core.Decide("Progenitor.dll", AbcHash));
        Assert.Equal(boot.Error, core.Error);

        // A reason we do not have is still a refusal, not a silent Absent.
        Assert.Equal(Boot.AllowListStatus.Malformed, Boot.ReloadAllowPolicy.UnresolvedPath("").Status);
        Assert.Equal(Boot.AllowListStatus.Malformed, Boot.ReloadAllowPolicy.UnresolvedPath(null).Status);
    }

    [Fact]
    public void No_allowlist_file_leaves_behaviour_unchanged_and_says_allowlist_none()
    {
        var boot = Boot.ReloadAllowPolicy.Parse(null);
        Assert.Equal(Boot.AllowListStatus.Absent, boot.Status);
        var d = boot.Decide("Anything.dll", AbcHash);
        Assert.Equal(Boot.AllowDecision.NoList, d);
        Assert.True(Boot.ReloadAllowPolicy.MayLoad(d));
        Assert.Equal("none", Boot.ReloadAllowPolicy.Token(d));

        var core = Core.ReloadAllowPolicy.Parse(null);
        Assert.Equal(Core.AllowListStatus.Absent, core.Status);
        Assert.Equal(Core.AllowDecision.NoList, core.Decide("Anything.dll", AbcHash));
    }

    [Fact]
    public void A_listed_pair_loads_and_everything_else_is_refused()
    {
        string text =
            "# operator allowlist\n" +
            "\n" +
            AbcHash + "  Progenitor.Sample.dll\n" +
            EmptyHash + "\tProgenitor.dll\n";

        var list = Boot.ReloadAllowPolicy.Parse(text);
        Assert.Equal(Boot.AllowListStatus.Active, list.Status);
        Assert.Equal(2, list.Count);

        Assert.Equal(Boot.AllowDecision.Listed, list.Decide("Progenitor.Sample.dll", AbcHash));
        // Tab-separated row, and a .next resolves to the live name.
        Assert.Equal(Boot.AllowDecision.Listed, list.Decide("Progenitor.dll.next", EmptyHash));
        // Right name, wrong bytes.
        Assert.Equal(Boot.AllowDecision.Unlisted, list.Decide("Progenitor.Sample.dll", EmptyHash));
        // Right bytes, wrong slot: an allowlisted DLL dropped in under another name is refused.
        Assert.Equal(Boot.AllowDecision.Unlisted, list.Decide("Evil.dll", AbcHash));
        // Not mentioned at all.
        Assert.Equal(Boot.AllowDecision.Unlisted, list.Decide("Other.dll", new string('f', 64)));

        Assert.False(Boot.ReloadAllowPolicy.MayLoad(Boot.AllowDecision.Unlisted));
        Assert.Equal("unlisted", Boot.ReloadAllowPolicy.Token(Boot.AllowDecision.Unlisted));
        Assert.Equal("ok", Boot.ReloadAllowPolicy.Token(Boot.AllowDecision.Listed));
    }

    [Fact]
    public void Hash_and_name_matching_are_case_insensitive()
    {
        var list = Boot.ReloadAllowPolicy.Parse(AbcHash.ToUpperInvariant() + "  Progenitor.Sample.DLL");
        Assert.Equal(Boot.AllowListStatus.Active, list.Status);
        Assert.Equal(Boot.AllowDecision.Listed, list.Decide("progenitor.sample.dll", AbcHash));
        Assert.Equal(Boot.AllowDecision.Listed, list.Decide("Progenitor.Sample.dll", AbcHash.ToUpperInvariant()));
    }

    [Fact]
    public void A_file_of_only_comments_is_an_empty_allowlist_that_refuses_everything()
    {
        // Not "absent". The operator created the file and listed nothing, so nothing loads.
        var list = Boot.ReloadAllowPolicy.Parse("# nothing yet\n\n   \n");
        Assert.Equal(Boot.AllowListStatus.Active, list.Status);
        Assert.Equal(0, list.Count);
        Assert.Equal(Boot.AllowDecision.Unlisted, list.Decide("Progenitor.Sample.dll", AbcHash));
    }

    public static IEnumerable<object[]> MalformedRows() => new List<object[]>
    {
        new object[] { AbcHash + "\n", 1 },                       // hash with no filename
        new object[] { "deadbeef  Progenitor.dll\n", 1 },          // hash too short
        new object[] { "# ok\n" + new string('z', 64) + "  x.dll\n", 2 }, // not hex
        new object[] { AbcHash + "  \n", 1 },                     // trailing whitespace only
        new object[] { AbcHash + "  sub/dir/x.dll\n", 1 },        // a path, not a leaf name
        new object[] { AbcHash + "  ok.dll\n" + "oops\n", 2 },     // good row then a bad one
    };

    [Theory]
    [MemberData(nameof(MalformedRows))]
    public void A_malformed_allowlist_fails_closed_and_names_the_line(string text, int line)
    {
        foreach (var status in new[] { "boot", "core" })
        {
            if (status == "boot")
            {
                var list = Boot.ReloadAllowPolicy.Parse(text);
                Assert.Equal(Boot.AllowListStatus.Malformed, list.Status);
                Assert.Contains("line " + line, list.Error, StringComparison.Ordinal);
                var d = list.Decide("ok.dll", AbcHash);
                Assert.Equal(Boot.AllowDecision.Malformed, d);
                // Fail closed: even a row that parsed before the bad line does not load.
                Assert.False(Boot.ReloadAllowPolicy.MayLoad(d));
                Assert.Equal("malformed", Boot.ReloadAllowPolicy.Token(d));
            }
            else
            {
                var list = Core.ReloadAllowPolicy.Parse(text);
                Assert.Equal(Core.AllowListStatus.Malformed, list.Status);
                Assert.Contains("line " + line, list.Error, StringComparison.Ordinal);
                Assert.False(Core.ReloadAllowPolicy.MayLoad(list.Decide("ok.dll", AbcHash)));
            }
        }
    }

    [Fact]
    public void MalformedFor_is_the_unreadable_file_case_and_also_fails_closed()
    {
        var list = Boot.ReloadAllowPolicy.MalformedFor("cannot read the file: denied");
        Assert.Equal(Boot.AllowListStatus.Malformed, list.Status);
        Assert.Equal(Boot.AllowDecision.Malformed, list.Decide("x.dll", AbcHash));
    }

    [Fact]
    public void Plugin_load_lines_carry_the_short_hash_the_size_and_the_allowlist_state()
    {
        Assert.Equal(
            "Reload: loading Progenitor.Sample.dll sha256=ba7816bf8f01cfea size=45056 allowlist=none",
            Boot.ReloadAllowPolicy.FormatPluginLoad("Progenitor.Sample.dll", AbcHash, 45056, Boot.AllowDecision.NoList));
        Assert.Equal(
            "Reload: loading Progenitor.Sample.dll sha256=ba7816bf8f01cfea size=45056 allowlist=ok",
            Boot.ReloadAllowPolicy.FormatPluginLoad("Progenitor.Sample.dll", AbcHash, 45056, Boot.AllowDecision.Listed));
        Assert.Equal(
            "Reload: image Progenitor.Sample.dll sha256=" + AbcHash + " size=45056 asm=Progenitor.Sample",
            Boot.ReloadAllowPolicy.FormatPluginLoadVerbose("Progenitor.Sample.dll", AbcHash, 45056, "Progenitor.Sample"));
    }

    [Fact]
    public void The_refusal_lines_say_what_was_refused_and_why()
    {
        Assert.Equal(
            "Reload: refused Evil.dll: sha256=ba7816bf8f01cfea size=1024 allowlist=unlisted; "
            + "not listed in BepInEx/config/progenitor-allow.txt; quarantined, not loaded.",
            Boot.ReloadAllowPolicy.FormatRefusedPlugin("Evil.dll", AbcHash, 1024, Boot.AllowDecision.Unlisted));
        Assert.Equal(
            "Core reload refused: Progenitor.dll.next sha256=ba7816bf8f01cfea size=1024 allowlist=unlisted; "
            + "not listed in BepInEx/config/progenitor-allow.txt; quarantined, not loaded.",
            Boot.ReloadAllowPolicy.FormatRefusedCore("Progenitor.dll.next", AbcHash, 1024, Boot.AllowDecision.Unlisted));
        Assert.Equal(
            "Reload: refused Evil.dll: sha256=ba7816bf8f01cfea size=1024 allowlist=malformed; "
            + "BepInEx/config/progenitor-allow.txt did not parse; quarantined, not loaded.",
            Boot.ReloadAllowPolicy.FormatRefusedPlugin("Evil.dll", AbcHash, 1024, Boot.AllowDecision.Malformed));
        Assert.Equal(
            "Reload: allowlist BepInEx/config/progenitor-allow.txt is malformed (line 3: expected 64 hex sha256 characters); "
            + "loading nothing from the reload folder and no Core generation. Fix or delete the file.",
            Boot.ReloadAllowPolicy.FormatMalformed("line 3: expected 64 hex sha256 characters"));
    }

    [Fact]
    public void Core_load_lines_keep_the_old_prefix_and_add_the_identity()
    {
        Assert.Equal(
            "Loaded Core from Progenitor.dll (progenitor-core:2, collectible) sha256=ba7816bf8f01cfea "
            + "size=1114112 allowlist=none.",
            Boot.ReloadAllowPolicy.FormatCoreLoad("Progenitor.dll", "progenitor-core:2", AbcHash, 1114112, Boot.AllowDecision.NoList));
        Assert.Equal(
            "Core image Progenitor.dll sha256=" + AbcHash + " size=1114112 asm=Progenitor",
            Boot.ReloadAllowPolicy.FormatCoreLoadVerbose("Progenitor.dll", AbcHash, 1114112, "Progenitor"));
        Assert.Equal(
            "Core watch: staged Progenitor.dll.next sha256=ba7816bf8f01cfea size=1114112 allowlist=ok",
            Boot.ReloadAllowPolicy.FormatStagedCore("Progenitor.dll.next", AbcHash, 1114112, Boot.AllowDecision.Listed));
    }

    [Fact]
    public void Sums_rows_and_allowlist_rows_are_the_same_shape()
    {
        // tools/package.ps1 emits this; an operator pastes it into progenitor-allow.txt.
        string row = Boot.ReloadAllowPolicy.FormatSumsLine(AbcHash, "Progenitor.dll");
        Assert.Equal(AbcHash + "  Progenitor.dll", row);
        Assert.Equal("SHA256SUMS", Boot.ReloadAllowPolicy.SumsFileName);

        var parsed = Boot.ReloadAllowPolicy.Parse(row);
        Assert.Equal(Boot.AllowListStatus.Active, parsed.Status);
        Assert.Equal(Boot.AllowDecision.Listed, parsed.Decide("Progenitor.dll", AbcHash));
    }

    [Fact]
    public void The_two_copies_agree_on_every_decision_in_the_table()
    {
        string text = AbcHash + "  Progenitor.Sample.dll\n" + EmptyHash + "  Progenitor.dll\n";
        var boot = Boot.ReloadAllowPolicy.Parse(text);
        var core = Core.ReloadAllowPolicy.Parse(text);
        Assert.Equal(boot.Count, core.Count);

        var cases = new (string Name, string Hash)[]
        {
            ("Progenitor.Sample.dll", AbcHash),
            ("Progenitor.dll.next", EmptyHash),
            ("Progenitor.Sample.dll", EmptyHash),
            ("Evil.dll", AbcHash),
            ("", AbcHash),
            ("Progenitor.dll", "short"),
        };
        foreach (var (name, hash) in cases)
        {
            Assert.Equal(boot.Decide(name, hash).ToString(), core.Decide(name, hash).ToString());
            Assert.Equal(
                Boot.ReloadAllowPolicy.FormatPluginLoad(name, hash, 7, boot.Decide(name, hash)),
                Core.ReloadAllowPolicy.FormatPluginLoad(name, hash, 7, core.Decide(name, hash)));
        }
        Assert.Equal(Boot.ReloadAllowPolicy.DisplayPath, Core.ReloadAllowPolicy.DisplayPath);
        Assert.Equal("BepInEx/config/progenitor-allow.txt", Boot.ReloadAllowPolicy.DisplayPath);
        Assert.Equal("progenitor-allow.txt", Boot.ReloadAllowPolicy.FileName);
    }
}
