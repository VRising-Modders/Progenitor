using System;
using System.IO;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FSAMP. Game-free coverage of <c>.access</c>'s reply lines. The read itself goes through
/// the generated <c>samples/Progenitor.Sample/Access/Access.Generated.cs</c>, which names game
/// types and is therefore not on this project's compile list; what is pinned here is the shape of
/// the two lines <c>docs/bridge-ops.md</c> tells an operator to compare against the dump.
/// </summary>
public class SampleAccessDemoTests
{
    [Fact]
    public void The_first_line_reports_the_runtime_offset_next_to_the_dump_offset()
    {
        var lines = SampleAccessDemo.Lines(
            "access: resolved=3 declared=2",
            declaredCount: 1,
            declarationsHash: "7e2c85802e78f986d5f61c2a1c4a7a5abf376a4d9f057c4bc7af37ba0f51610c",
            offset: 0x100,
            valuePresent: true);

        Assert.Equal(2, lines.Count);
        Assert.Contains(SampleAccessDemo.DeclaredMember, lines[0]);
        Assert.Contains("value present", lines[0]);
        Assert.Contains("runtime offset 0x100", lines[0]);
        Assert.Contains("the server metadata records 0x100", lines[0]);
        Assert.Contains("nothing in Progenitor writes an offset down", lines[0]);
    }

    [Fact]
    public void A_null_reference_field_is_reported_not_treated_as_a_failure()
    {
        var lines = SampleAccessDemo.Lines("access: resolved=1 declared=2", 1, "abcdef0123456789", 0x100, valuePresent: false);

        Assert.Contains("value null", lines[0]);
    }

    [Fact]
    public void The_second_line_carries_the_declaration_count_the_hash_and_the_samples_own_facade_line()
    {
        var lines = SampleAccessDemo.Lines(
            "access: resolved=3 declared=1 interop-verified=true",
            declaredCount: 1,
            declarationsHash: "7e2c85802e78f986d5f61c2a1c4a7a5abf376a4d9f057c4bc7af37ba0f51610c",
            offset: 0x100,
            valuePresent: true);

        Assert.Contains("Sample access.txt: declared=1", lines[1]);
        Assert.Contains("sha256=7e2c85802e78", lines[1]);
        // The Sample's OWN facade line, verification state included — not Core's (R42 finding 3).
        Assert.Contains("Sample access facade: access: resolved=3 declared=1 interop-verified=true", lines[1]);
    }

    [Fact]
    public void A_missing_hash_or_resolution_line_degrades_readably()
    {
        var lines = SampleAccessDemo.Lines("", 0, "", 0, false);

        Assert.Contains("sha256=(none)", lines[1]);
        Assert.Contains("Sample access facade: (unavailable)", lines[1]);
    }

    [Fact]
    public void Short_truncates_to_twelve_characters_and_never_throws_on_a_shorter_hash()
    {
        Assert.Equal("7e2c85802e78", SampleAccessDemo.Short("7e2c85802e78f986d5f61c2a1c4a7a5a"));
        Assert.Equal("abc", SampleAccessDemo.Short("abc"));
        Assert.Equal("", SampleAccessDemo.Short(""));
    }

    [Fact]
    public void A_member_missing_on_this_build_is_reported_as_one_sentence()
    {
        string reply = SampleAccessDemo.FailureReply(
            "Access: ProjectM.GameDataSystem.RandomLocalizationKeys not found on this build; "
            + "the address library/resilience report knows which build");

        Assert.StartsWith("The declared member did not resolve on this build: Access:", reply);
        Assert.Equal("The declared member did not resolve on this build: (no message)", SampleAccessDemo.FailureReply("  "));
    }

    [Fact]
    public void The_dump_offset_the_reply_cites_is_the_one_the_declaration_file_documents()
    {
        Assert.Equal(0x100u, SampleAccessDemo.DumpOffset);
        Assert.Equal("ProjectM.GameDataSystem.RandomLocalizationKeys", SampleAccessDemo.DeclaredMember);
    }

    // ---- Job J21: the generated forms the field read does not cover ----

    /// <summary>
    /// <c>Access.&lt;Type&gt;.Type</c> is a plain <c>typeof()</c> over the interop stub, and the
    /// reply has to say that: a reader who took it for the game's native class would go looking
    /// for a pointer that is not there.
    /// </summary>
    [Fact]
    public void The_type_lines_name_both_facades_and_say_it_is_the_interop_stub()
    {
        var lines = SampleAccessDemo.TypeLines("ProjectM.GameDataSystem", "ProjectM.DeathUtilities");

        Assert.Equal(2, lines.Count);
        Assert.Contains("interop stub type", lines[0], StringComparison.Ordinal);
        Assert.Contains("not the game's native class", lines[0], StringComparison.Ordinal);
        Assert.Contains("GameDataSystem.Type = ProjectM.GameDataSystem", lines[0], StringComparison.Ordinal);
        Assert.Contains("DeathUtilities.Type = ProjectM.DeathUtilities", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_facade_that_did_not_answer_is_reported_rather_than_printed_as_an_empty_name()
    {
        var lines = SampleAccessDemo.TypeLines("", "");

        Assert.Contains("(unavailable)", lines[0], StringComparison.Ordinal);
        Assert.Contains("(unavailable)", lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The two method forms answer different questions about one member, and the dangerous
    /// misreading is that either of them <em>calls</em> it. Both lines say so.
    /// </summary>
    [Fact]
    public void The_method_lines_give_the_Harmony_target_and_the_detour_target_and_say_nothing_was_invoked()
    {
        var lines = SampleAccessDemo.MethodLines("ProjectM.DeathUtilities::Kill/3 params", 0x7C4CB80);

        Assert.Equal(2, lines.Count);
        Assert.Contains("Access.DeathUtilities.Kill() (declared ProjectM.DeathUtilities.Kill)", lines[0], StringComparison.Ordinal);
        Assert.Contains("MethodBase: ProjectM.DeathUtilities::Kill/3 params", lines[0], StringComparison.Ordinal);
        Assert.Contains("nothing was invoked", lines[0], StringComparison.Ordinal);
        Assert.Contains("Access.DeathUtilities.KillPointer() resolved the compiled function pointer: 0x7C4CB80", lines[1], StringComparison.Ordinal);
        Assert.Contains("native-detour target", lines[1], StringComparison.Ordinal);
        Assert.Contains("moves with every game build", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void An_unresolved_method_is_reported_rather_than_printed_as_an_empty_signature()
    {
        var lines = SampleAccessDemo.MethodLines("", 0);

        Assert.Contains("(unavailable)", lines[0], StringComparison.Ordinal);
        Assert.Contains("0x0", lines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// The deliberate miss exists so the <c>AccessException</c> sentence is visible somewhere
    /// other than an incident. The reply carries it verbatim, and names the member asked for.
    /// </summary>
    [Fact]
    public void The_missing_member_reply_names_the_member_and_carries_the_AccessException_message()
    {
        string reply = SampleAccessDemo.MissingReply(
            "Access: ProjectM.GameDataSystem.NoSuchFieldOnAnyBuild not found on this build; "
            + "the address library/resilience report knows which build");

        Assert.Contains("ProjectM.GameDataSystem." + SampleAccessDemo.MissingMember, reply, StringComparison.Ordinal);
        Assert.Contains("exists on no build", reply, StringComparison.Ordinal);
        Assert.Contains("AccessException: Access: ProjectM.GameDataSystem.NoSuchFieldOnAnyBuild not found", reply, StringComparison.Ordinal);
        Assert.EndsWith("knows which build", reply);
    }

    [Fact]
    public void An_AccessException_with_no_message_still_reads_as_a_sentence()
    {
        Assert.EndsWith("AccessException: (no message)", SampleAccessDemo.MissingReply("   "));
    }

    /// <summary>
    /// The one outcome that must not read as a pass: the resolver answering for a member that
    /// cannot exist. It would mean the layer resolved something else under that name.
    /// </summary>
    [Fact]
    public void A_resolver_that_answers_for_a_missing_member_is_reported_as_a_bug_not_a_pass()
    {
        string reply = SampleAccessDemo.MissingDidNotThrowReply(0x100);

        Assert.Contains("ANSWERED (offset 0x100)", reply, StringComparison.Ordinal);
        Assert.Contains("instead of throwing AccessException", reply, StringComparison.Ordinal);
        Assert.Contains("bug in the access layer, not as a pass", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The four forms are behind <c>!PROGENITOR_TESTS</c> (they bind ProjectM and the generated
    /// facades), so the calls themselves are pinned in the source — one per api.md row.
    /// </summary>
    [Fact]
    public void Each_generated_form_is_actually_called_by_a_subcommand()
    {
        string src = Source();

        SourcePin.AssertStatement(src, "var method = CoreAccess.DeathUtilities.Kill();");
        SourcePin.AssertStatement(src, "var pointer = CoreAccess.DeathUtilities.KillPointer();");
        SourcePin.AssertStatement(src, "uint offset = AccessResolver.FieldOffset(");
        SourcePin.AssertUncommented(src, "SampleAccess.GameDataSystem.Type");
        SourcePin.AssertUncommented(src, "CoreAccess.DeathUtilities.Type");
    }

    /// <summary>
    /// The subcommands are a <c>[CommandGroup]</c> named after the existing root command on
    /// purpose — the dispatcher matches a two-token group before a root name, so bare
    /// <c>.access</c> is untouched. Pinned because renaming the group would silently move four
    /// commands.
    /// </summary>
    [Fact]
    public void The_forms_are_subcommands_of_the_existing_access_command()
    {
        string src = Source();

        SourcePin.AssertStatement(src, "[CommandGroup(\"access\")]");
        SourcePin.AssertStatement(src, "[Command(\"type\"");
        SourcePin.AssertStatement(src, "[Command(\"method\"");
        SourcePin.AssertStatement(src, "[Command(\"missing\"");
    }

    /// <summary>
    /// Every one of them prints a runtime offset, a function pointer or a resolver failure, which
    /// is server diagnostics — the same reason the root <c>.access</c> is gated.
    /// </summary>
    [Fact]
    public void Every_access_subcommand_is_admin_gated_like_the_root_command()
    {
        string src = Source();
        int commands = CountOccurrences(src, "[Command(\"");
        int gated = CountOccurrences(src, "adminOnly: true");

        Assert.Equal(4, commands);
        Assert.Equal(4, gated);
    }

    private static int CountOccurrences(string body, string needle)
    {
        int n = 0;
        for (int i = body.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = body.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "Progenitor.Sample", "SampleAccessDemo.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            dir = dir.Parent;
        }

        throw new FileNotFoundException("samples/Progenitor.Sample/SampleAccessDemo.cs not found from " + AppContext.BaseDirectory);
    }
}
