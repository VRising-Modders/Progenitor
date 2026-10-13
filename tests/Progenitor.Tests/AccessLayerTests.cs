using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Access;
using Progenitor.AccessGen;
using Progenitor.Resilience;
using Xunit;

// Job FACC. Two fake declarations stamped onto THIS assembly, exactly the way tools/accessgen
// stamps them onto Progenitor.dll. The assembly is "xunit.assert" rather than a ProjectM one on
// purpose: ResilienceTests asserts that the default game-assembly filter finds nothing here, and
// a fake ProjectM row would break that. Widening the filter to "xunit" is how the resilience
// tests already reach real metadata (ResilienceTests.TheScannerReadsRealMetadataAndFilters...).
[assembly: AccessMember("xunit.assert", "Xunit.Assert", "True", "Method", AccessMemberAttribute.AnySignature)]
[assembly: AccessMember("xunit.assert", "Xunit.Assert", "NoSuchMemberOnThisBuild", "Field", AccessMemberAttribute.AnySignature)]

namespace Progenitor.Tests;

/// <summary>
/// The generated access layer (roadmap M3 item 5): declaration parsing, deterministic generation,
/// the failure sentence, the resilience rows, and that the two Core call sites the job replaced
/// still ask for exactly the members they used to ask for.
///
/// Everything here is game-free. <c>Access/AccessResolver.cs</c> — the half that touches
/// Il2CppInterop — is deliberately not linked into this project and is not tested here; what is
/// tested is every decision made before and after it.
/// </summary>
public class AccessLayerTests
{
    // ---------------------------------------------------------------- declaration parsing

    private static List<AccessDeclaration> ParseOk(params string[] lines)
    {
        Assert.True(
            AccessDeclarationParser.TryParse(lines, out var declarations, out var errors),
            string.Join("; ", errors));
        return declarations;
    }

    private static List<string> ParseErrors(params string[] lines)
    {
        Assert.False(AccessDeclarationParser.TryParse(lines, out _, out var errors));
        Assert.NotEmpty(errors);
        return errors;
    }

    [Fact]
    public void The_header_is_required_and_versioned()
    {
        Assert.Contains("access v1", ParseErrors("assembly ProjectM")[0], StringComparison.Ordinal);
        Assert.Contains("unsupported", ParseErrors("access v2")[0], StringComparison.Ordinal);
        Assert.Contains("empty declaration file", ParseErrors("# nothing but a comment")[0], StringComparison.Ordinal);
        Assert.Empty(ParseOk("access v1"));
    }

    [Fact]
    public void A_member_line_needs_an_assembly_directive_in_effect()
    {
        var errors = ParseErrors("access v1", "field ProjectM.Foo bar : System.Int32");
        Assert.Contains("line 2", errors[0], StringComparison.Ordinal);
        Assert.Contains("no 'assembly' directive", errors[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Fields_methods_and_types_parse_with_their_modifiers()
    {
        var declarations = ParseOk(
            "access v1",
            "assembly ProjectM",
            "type   ProjectM.Internal.Thing",
            "field  ProjectM.Foo bar : System.Int32",
            "field  ProjectM.Foo tick : System.Single",
            "field  ProjectM.Foo counter : System.Int64 static",
            "method ProjectM.Foo Run",
            "method ProjectM.Foo Run(System.Int32,System.Single)");

        Assert.Equal(6, declarations.Count);
        Assert.All(declarations, d => Assert.Equal("ProjectM", d.Assembly));

        var thing = Assert.Single(declarations, d => d.Kind == AccessDeclarationKind.Type);
        Assert.Equal("ProjectM.Internal.Thing", thing.Type);
        Assert.Equal("", thing.Name);

        var bar = Assert.Single(declarations, d => d.Name == "bar");
        Assert.Equal(AccessDeclarationKind.Field, bar.Kind);
        Assert.False(bar.IsStatic);
        // R39 finding 3: a field line carries its type, always — it is what the runtime width
        // check compares sizeof(T) against.
        Assert.Equal("System.Int32", bar.FieldType);

        Assert.Equal("System.Single", Assert.Single(declarations, d => d.Name == "tick").FieldType);
        Assert.True(Assert.Single(declarations, d => d.Name == "counter").IsStatic);

        var runs = declarations.Where(d => d.Name == "Run").ToList();
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, d => d.Signature == "");
        // Whitespace inside the parameter list is normalized, so two spellings of one overload
        // are one declaration and hash the same.
        Assert.Contains(runs, d => d.Signature == "System.Int32, System.Single");
    }

    [Fact]
    public void Comments_blank_lines_and_whitespace_do_not_reach_the_model()
    {
        var tight = ParseOk("access v1", "assembly ProjectM", "field ProjectM.Foo bar : System.Int32");
        var loose = ParseOk(
            "# leading comment",
            "access v1",
            "",
            "   assembly    ProjectM   # which dll",
            "",
            "\tfield   ProjectM.Foo   bar   :   System.Int32",
            "# trailing comment");

        Assert.Equal(
            tight.Select(d => d.Canonical()).ToArray(),
            loose.Select(d => d.Canonical()).ToArray());
        Assert.Equal(AccessDeclarationParser.Hash(tight), AccessDeclarationParser.Hash(loose));
    }

    [Fact]
    public void Bad_lines_are_named_by_number_and_do_not_half_succeed()
    {
        Assert.Contains("line 3", ParseErrors("access v1", "assembly ProjectM", "widen ProjectM.Foo")[0], StringComparison.Ordinal);
        Assert.Contains("line 3", ParseErrors("access v1", "assembly ProjectM", "field ProjectM.Foo 9bar")[0], StringComparison.Ordinal);
        Assert.Contains("unbalanced", ParseErrors("access v1", "assembly ProjectM", "method ProjectM.Foo Run(System.Int32")[0], StringComparison.Ordinal);
        Assert.Contains("duplicate", ParseErrors(
            "access v1", "assembly ProjectM", "field ProjectM.Foo bar : System.Int32", "field ProjectM.Foo bar : System.Int32")[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Declaration_order_never_changes_the_model_or_the_hash()
    {
        var forward = ParseOk("access v1", "assembly ProjectM", "field ProjectM.A x : System.Int32", "field ProjectM.B y : System.Int32");
        var reverse = ParseOk("access v1", "assembly ProjectM", "field ProjectM.B y : System.Int32", "field ProjectM.A x : System.Int32");

        Assert.Equal(
            forward.Select(d => d.Canonical()).ToArray(),
            reverse.Select(d => d.Canonical()).ToArray());
        Assert.Equal(AccessDeclarationParser.Hash(forward), AccessDeclarationParser.Hash(reverse));
    }

    // ---------------------------------------------------------------- generation

    private static string Emit(IReadOnlyList<AccessDeclaration> declarations) =>
        AccessGenerator.Emit(declarations, new AccessGenOptions
        {
            DeclarationsPath = "src/Progenitor.Core/Access/access.txt",
            BuildId = "test-build",
        });

    [Fact]
    public void Generation_is_deterministic_and_order_independent()
    {
        var forward = ParseOk("access v1", "assembly ProjectM", "field ProjectM.A x : System.Int32", "method ProjectM.B Run");
        var reverse = ParseOk("access v1", "assembly ProjectM", "method ProjectM.B Run", "field ProjectM.A x : System.Int32");

        var first = Emit(forward);
        Assert.Equal(first, Emit(forward));
        Assert.Equal(first, Emit(reverse));
        Assert.DoesNotContain("\r", first, StringComparison.Ordinal);
    }

    [Fact]
    public void The_header_carries_the_build_id_and_the_declaration_hash()
    {
        var declarations = ParseOk("access v1", "assembly ProjectM", "field ProjectM.A x : System.Int32");
        var text = Emit(declarations);

        Assert.Contains("build:        test-build", text, StringComparison.Ordinal);
        Assert.Contains("declarations: sha256:" + AccessDeclarationParser.Hash(declarations), text, StringComparison.Ordinal);
        Assert.Contains("DO NOT EDIT BY HAND", text, StringComparison.Ordinal);
        Assert.Contains("public const int DeclaredCount = 1;", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_declaration_becomes_an_assembly_attribute_row()
    {
        var text = Emit(ParseOk(
            "access v1",
            "assembly ProjectM",
            "field  ProjectM.Foo bar : System.Int32",
            "method ProjectM.Foo Run(System.Int32)",
            "type   ProjectM.Thing"));

        Assert.Contains(
            "[assembly: global::Progenitor.Access.AccessMember(\"ProjectM\", \"ProjectM.Foo\", \"bar\", \"Field\", \"*\")]",
            text, StringComparison.Ordinal);
        // A declared overload keeps its parameter list; a bare name gets the wildcard.
        Assert.Contains("\"Run\", \"Method\", \"System.Int32\")]", text, StringComparison.Ordinal);
        Assert.Contains("\"ProjectM.Thing\", \"\", \"Type\", \"*\")]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_gets_typed_get_and_set_accessors_plus_the_offset_diagnostic()
    {
        var text = Emit(ParseOk(
            "access v1", "assembly ProjectM",
            "field ProjectM.Foo bar : System.Single",
            "field ProjectM.Foo counter : System.Int64 static"));

        Assert.Contains("public static T bar<T>(global::Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase instance) where T : unmanaged", text, StringComparison.Ordinal);
        Assert.Contains("public static global::System.Single bar(global::Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase instance)", text, StringComparison.Ordinal);
        Assert.Contains("public static uint barOffset()", text, StringComparison.Ordinal);
        // A static field takes no instance.
        Assert.Contains("public static T counter<T>() where T : unmanaged", text, StringComparison.Ordinal);
        // Nothing anywhere is a literal offset: the runtime asks il2cpp_field_get_offset.
        Assert.Contains("AccessResolver.FieldOffset(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_types_with_one_simple_name_are_both_flattened_never_one_silently()
    {
        var text = Emit(ParseOk(
            "access v1", "assembly ProjectM",
            "field ProjectM.Foo a : System.Int32",
            "field ProjectM.Ui.Foo b : System.Int32"));

        Assert.Contains("public static partial class ProjectM_Foo", text, StringComparison.Ordinal);
        Assert.Contains("public static partial class ProjectM_Ui_Foo", text, StringComparison.Ordinal);
        Assert.DoesNotContain("public static partial class Foo\n", text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- failure sentence

    [Fact]
    public void The_not_found_sentence_names_the_member_and_where_to_look()
    {
        Assert.Equal(
            "Access: ProjectM.SomeType.someField not found on this build; "
            + "the address library/resilience report knows which build",
            AccessMessages.NotFound("ProjectM.SomeType", "someField"));

        // A nested declared type reads as C#, not as metadata.
        Assert.StartsWith("Access: ProjectM.Outer.Nested.f ", AccessMessages.NotFound("ProjectM.Outer/Nested", "f"), StringComparison.Ordinal);
        Assert.Contains("2 overloads", AccessMessages.Ambiguous("ProjectM.Foo", "Run", 2), StringComparison.Ordinal);
        Assert.Contains("add a parameter list", AccessMessages.Ambiguous("ProjectM.Foo", "Run", 2), StringComparison.Ordinal);
        Assert.Contains("the instance is null", AccessMessages.Unresolved("ProjectM.Foo", "bar", "the instance is null"), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- resilience rows

    [Fact]
    public void The_metadata_scan_reports_declared_access_members()
    {
        var path = typeof(AccessLayerTests).Assembly.Location;
        var rows = MetadataScan.ScanReferences(path, out var error, new[] { "xunit" });
        Assert.Null(error);

        // Both fakes at the top of this file are rows, alongside the ordinary MemberRef rows.
        Assert.Contains(rows, r => r.Type == "Xunit.Assert" && r.Name == "True"
                                   && r.Signature == AccessMemberAttribute.AnySignature);
        var missing = Assert.Single(rows, r => r.Name == "NoSuchMemberOnThisBuild");
        Assert.Equal("xunit.assert", missing.Assembly);
        Assert.Equal(MemberKind.Field, missing.Kind);
        Assert.Equal(BindingStatus.NotChecked, missing.Status);
    }

    [Fact]
    public void A_declaration_naming_a_missing_member_resolves_to_MemberMissing()
    {
        var path = typeof(AccessLayerTests).Assembly.Location;
        var directory = Path.GetDirectoryName(path)!;
        Assert.True(File.Exists(Path.Combine(directory, "xunit.assert.dll")),
            "the resolution half of this test needs xunit.assert.dll beside the test assembly");

        var resolved = MetadataScan.Resolve(
            MetadataScan.ScanReferences(path, out _, new[] { "xunit" }), directory);

        // The wildcard signature means "any overload of this name", so a real member binds even
        // though the declaration named no parameter list...
        var bound = Assert.Single(resolved, r => r.Type == "Xunit.Assert" && r.Name == "True"
                                                 && r.Signature == AccessMemberAttribute.AnySignature);
        Assert.Equal(BindingStatus.Bound, bound.Status);

        // ...and a member that is not on this build is named, which is the whole point: a
        // generated accessor resolves by name at first use, so nothing else in the scan would
        // have seen it.
        var missing = Assert.Single(resolved, r => r.Name == "NoSuchMemberOnThisBuild");
        Assert.Equal(BindingStatus.MemberMissing, missing.Status);
    }

    // ---------------------------------------------------------------- the checked-in artifacts

    private static string FindRepoRoot()
    {
        string? dir = Path.GetFullPath(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir, "Progenitor.sln"))) return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException("Progenitor.sln not found from " + AppContext.BaseDirectory);
    }

    private static (List<AccessDeclaration> Declarations, string Generated) CoreArtefacts()
    {
        var root = FindRepoRoot();
        var declarationsPath = Path.Combine(root, "src", "Progenitor.Core", "Access", "access.txt");
        var generatedPath = Path.Combine(root, "src", "Progenitor.Core", "Access", "Generated", "Access.Generated.cs");
        Assert.True(AccessDeclarationParser.TryParse(File.ReadAllLines(declarationsPath), out var declarations, out var errors),
            string.Join("; ", errors));
        return (declarations, File.ReadAllText(generatedPath).Replace("\r\n", "\n"));
    }

    [Fact]
    public void The_checked_in_generated_file_matches_the_checked_in_declarations()
    {
        var (declarations, generated) = CoreArtefacts();

        // The whole file is not compared: the header carries a build id the generator was told,
        // which is not in the declaration file. The hash and the count are what must agree, and
        // they are what a stale regeneration would get wrong.
        Assert.Contains("declarations: sha256:" + AccessDeclarationParser.Hash(declarations), generated, StringComparison.Ordinal);
        Assert.Contains("public const int DeclaredCount = " + declarations.Count + ";", generated, StringComparison.Ordinal);
        Assert.Contains("public const string DeclarationsHash = \"" + AccessDeclarationParser.Hash(declarations) + "\";", generated, StringComparison.Ordinal);

        // And regenerating with the header the checked-in file already states reproduces it byte
        // for byte, which is the property that makes `accessgen --check` a usable CI gate.
        var buildId = generated
            .Split('\n')
            .First(l => l.Contains("//     build:", StringComparison.Ordinal))
            .Split(':')[1].Trim();
        var regenerated = AccessGenerator.Emit(declarations, new AccessGenOptions
        {
            DeclarationsPath = "src/Progenitor.Core/Access/access.txt",
            BuildId = buildId,
        });
        Assert.Equal(generated, regenerated);
    }

    [Fact]
    public void The_two_replaced_core_reads_ask_for_exactly_what_they_used_to_ask_for()
    {
        var (declarations, generated) = CoreArtefacts();

        // Ui/GameInputBlocker.cs used to call
        //   Il2CppNative.MethodPointerByPrefix(typeof(RotateCameraBlocker), "GetConsumedInputs")
        // GodMode/GodMode.cs used to call
        //   typeof(DeathUtilities).GetMethod(nameof(DeathUtilities.Kill), Public | Static)
        // and, for the detour fallback,
        //   Il2CppNative.MethodPointerByPrefix(typeof(DeathUtilities), nameof(DeathUtilities.Kill))
        // Same two members, same two mechanisms — now declared rather than spelled inline.
        Assert.Single(declarations, d =>
            d.Kind == AccessDeclarationKind.Method
            && d.Assembly == "ProjectM.Camera"
            && d.Type == "ProjectM.RotateCameraBlocker"
            && d.Name == "GetConsumedInputs"
            && d.Signature == "");
        Assert.Single(declarations, d =>
            d.Kind == AccessDeclarationKind.Method
            && d.Assembly == "ProjectM.Shared"
            && d.Type == "ProjectM.DeathUtilities"
            && d.Name == "Kill"
            && d.Signature == "");

        // The pointer accessor is the MethodPointerByPrefix replacement; the MethodBase accessor
        // is the GetMethod replacement. Both must be on the generated surface.
        Assert.Contains("public static global::System.IntPtr GetConsumedInputsPointer()", generated, StringComparison.Ordinal);
        Assert.Contains("public static global::System.Reflection.MethodBase Kill()", generated, StringComparison.Ordinal);
        Assert.Contains("public static global::System.IntPtr KillPointer()", generated, StringComparison.Ordinal);

        // The replaced call sites name the accessors and no longer name the old mechanism.
        var root = FindRepoRoot();
        var blocker = File.ReadAllText(Path.Combine(root, "src", "Progenitor.Core", "Ui", "GameInputBlocker.cs"));
        SourcePin.AssertUncommented(blocker, "Access.RotateCameraBlocker.GetConsumedInputsPointer()");
        Assert.DoesNotContain("MethodPointerByPrefix", blocker, StringComparison.Ordinal);

        var godMode = File.ReadAllText(Path.Combine(root, "src", "Progenitor.Core", "GodMode", "GodMode.cs"));
        SourcePin.AssertUncommented(godMode, "Access.DeathUtilities.Kill()");
        SourcePin.AssertUncommented(godMode, "Access.DeathUtilities.KillPointer()");
        // The remaining mention is the comment explaining why KillEntity has NO prefix fallback;
        // no call site is left.
        Assert.DoesNotContain("Il2CppNative.MethodPointerByPrefix(", godMode, StringComparison.Ordinal);
        Assert.DoesNotContain("typeof(DeathUtilities).GetMethod", godMode, StringComparison.Ordinal);
    }
}
