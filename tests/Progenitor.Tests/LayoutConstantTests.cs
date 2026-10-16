using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Progenitor.LayoutGen;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The layout-constant generator (job FLAY1, roadmap M4.1) and the artifact it produces.
///
/// <para><b>What the tool is for.</b> Sixteen-odd numeric memory-layout constants — buffer element
/// strides, field offsets, enum values — sat as literals in <c>src/</c> with a metadata line
/// number in a comment and nothing checking them. Each one is a wrong-memory write if the game
/// moves the field on an update, and a comment is not a check. They are now declared once in
/// <c>src/Progenitor.Core/Layout/layout.txt</c>, emitted into <c>Layout</c>, and resolved against
/// the game's own metadata by <c>layoutgen --check</c>.</para>
///
/// <para><b>Two halves, and only one of them needs the game.</b> Resolving needs the 127 MB
/// metadata type listing that exists only on a machine with the exported metadata, so that half is a local
/// command. Everything below is game-free and runs in CI: the parser and the resolver over a small
/// hand-written fake dump, the checked-in generated file against the checked-in declarations, and
/// the compiled <c>Layout</c> constants against both.</para>
/// </summary>
public class LayoutConstantTests
{
    // ---- the fake dump: the four shapes the resolver has to read -------------------------------
    //
    // Written by hand rather than sliced out of the real dump, so a test failure names a parser
    // bug rather than a game update. The shapes are copied faithfully from
    // the server metadata's type listing: a `// Namespace:` line, an attribute line,
    // a declaration ending in `// TypeDefIndex: n`, a `// Fields` block whose every field line
    // carries `// 0xHEX`, then `// Properties` / `// Methods` sections that must NOT be read as
    // layout, and a closing brace in column 0.
    private const string FakeDump = @"
// Namespace: Fake.Space
[ComponentInWorld(new[] { 4, 2 })]
public struct SmallElement : IBufferElementData // TypeDefIndex: 100
{
	// Fields
	public int First; // 0x0
	public int Second; // 0x4
	public float3 Third; // 0x8

	// Properties
	public int NotAField { get; }

	// Methods

	// Offset: 0x1 VA: 0x1
	public static int Something() { }
}

// Namespace: Fake.Space
public struct TailPadded // TypeDefIndex: 101
{
	// Fields
	public FixedBytes4 Name; // 0x0
	public int Index; // 0x4
	public bool Flag; // 0x8
}

// Namespace: Fake.Space
public struct FixedBytes4 // TypeDefIndex: 102
{
	// Fields
	public byte b0; // 0x0
	public byte b1; // 0x1
	public byte b2; // 0x2
	public byte b3; // 0x3
}

// Namespace: Unity.Mathematics
public struct float3 : IEquatable<float3> // TypeDefIndex: 103
{
	// Fields
	public float x; // 0x0
	public float y; // 0x4
	public float z; // 0x8
	public static readonly float3 zero; // 0x0
}

// Namespace: Fake.Space
public enum FakeMode // TypeDefIndex: 104
{
	// Fields
	public byte value__; // 0x0
	public const FakeMode None = 0;
	public const FakeMode Position = 1;
	public const FakeMode Entity = 3;
}

// Namespace: Fake.Space
public struct CappedString // TypeDefIndex: 105
{
	// Fields
	internal const ushort utf8MaxLengthInBytes = 61;
	internal ushort utf8LengthInBytes; // 0x0
	internal FixedBytes4 bytes; // 0x2
}

// Namespace: Fake.Space
public struct BiggerCappedString // TypeDefIndex: 106
{
	// Fields
	internal const ushort utf8MaxLengthInBytes = 125;
	internal ushort utf8LengthInBytes; // 0x0
}

// Namespace: Unity.Collections
public struct FakeNativeArray<T> : IDisposable // TypeDefIndex: 107
{
	// Fields
	internal void* m_Buffer; // 0x0
	internal int m_Length; // 0x0
	internal Allocator m_AllocatorLabel; // 0x0
}

// Namespace: Fake.Space
internal struct AutoProperties // TypeDefIndex: 110
{
	// Fields
	private void* m_Data; // 0x0
	private int <Capacity>k__BackingField; // 0x8
	private int <Count>k__BackingField; // 0xC
	private readonly int ComponentCount; // 0x10
}

// Namespace: Fake.Space
internal static class Holder // TypeDefIndex: 111
{
	// Methods
}

// Namespace: Fake.Space
[IsReadOnly]
public struct Holder<T> // TypeDefIndex: 112
{
	// Fields
	private readonly void* _buffer; // 0x0
}

// Namespace: Fake.One
public struct Twice // TypeDefIndex: 108
{
	// Fields
	public int A; // 0x0
}

// Namespace: Fake.Two
public struct Twice // TypeDefIndex: 109
{
	// Fields
	public long A; // 0x0
}
";

    private static DumpIndex Fake() => DumpIndex.FromText(FakeDump.Replace("\r\n", "\n"));

    private static LayoutDeclaration Parse(string line)
    {
        Assert.True(
            LayoutDeclarationParser.TryParse(new[] { "layout v1", "dump server", line }, out var declarations, out var errors),
            string.Join("; ", errors));
        return Assert.Single(declarations);
    }

    private static long Resolve(string line)
    {
        Assert.True(Fake().TryResolve(Parse(line), out long value, out string error), error);
        return value;
    }

    private static string Refuse(string line)
    {
        Assert.False(Fake().TryResolve(Parse(line), out _, out string error), "expected a refusal from: " + line);
        Assert.NotEqual("", error);
        return error;
    }

    // ---- resolver: offsets --------------------------------------------------------------------

    [Fact]
    public void An_offset_query_reads_the_hex_the_dump_states()
    {
        Assert.Equal(0, Resolve("offset A = 0 : SmallElement.First"));
        Assert.Equal(4, Resolve("offset A = 4 : SmallElement.Second"));
        Assert.Equal(8, Resolve("offset A = 8 : SmallElement.Third"));
        Assert.Equal(2, Resolve("offset A = 2 : CappedString.bytes"));
    }

    [Fact]
    public void An_auto_property_backing_field_is_a_field_queried_by_its_compiler_name()
    {
        Assert.Equal(8, Resolve("offset A = 8 : AutoProperties.<Capacity>k__BackingField"));
        Assert.Equal(0xC, Resolve("offset A = 0xC : AutoProperties.<Count>k__BackingField"));
        Assert.Equal(0x10, Resolve("offset A = 0x10 : AutoProperties.ComponentCount"));
        Assert.Equal(0x18, Resolve("size A = 0x18 : AutoProperties"));
        Assert.Contains("Capacity", Refuse("offset A = 8 : AutoProperties.Capacity"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_generic_struct_is_told_from_a_static_class_of_the_same_name_by_its_parameter_list()
    {
        Assert.Contains("2 times", Refuse("offset A = 0 : Fake.Space.Holder._buffer"), StringComparison.Ordinal);
        Assert.Equal(0, Resolve("offset A = 0 : Fake.Space.Holder<T>._buffer"));
        Assert.Equal(0, Resolve("offset A = 0 : Holder<T>._buffer"));
    }

    [Fact]
    public void A_property_is_not_a_field_and_a_method_body_is_not_layout()
    {
        // The `// Properties` and `// Methods` sections come after the fields and contain lines
        // that look enough like fields to fool a looser parser. If either leaked in, SmallElement
        // would size wrong and `NotAField` would resolve.
        Assert.Contains("NotAField", Refuse("offset A = 0 : SmallElement.NotAField"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_static_field_is_not_part_of_the_instance_layout()
    {
        // `public static readonly float3 zero; // 0x0` sits in float3's field list and carries an
        // offset comment. Counting it would not change float3's size here, but it is exactly the
        // line that makes a naive parser report a bogus field, so the refusal is pinned.
        Assert.Contains("zero", Refuse("offset A = 0 : float3.zero"), StringComparison.Ordinal);
    }

    // ---- resolver: derived sizes ---------------------------------------------------------------

    [Fact]
    public void A_size_is_the_widest_field_extent_rounded_up_to_the_widest_alignment()
    {
        // int@0 + int@4 + float3(12)@8 = extent 20, alignment 4 -> 20, no tail padding.
        Assert.Equal(20, Resolve("size A = 20 : SmallElement"));
        // float3 itself: three floats, extent 12, alignment 4.
        Assert.Equal(12, Resolve("size A = 12 : float3"));
        // FixedBytes4: four bytes, alignment 1.
        Assert.Equal(4, Resolve("size A = 4 : FixedBytes4"));
    }

    [Fact]
    public void Tail_padding_is_added_because_that_is_what_a_native_array_strides_by()
    {
        // bool@0x8 gives extent 9; the widest field is a 4-byte int, so the element rounds up to
        // 12. This is the SequenceInputElement shape that produced the live EV17 regression: the
        // managed sizeof reads the unpadded extent and the native array strides by the padded one
        // (layout.txt: a native array stride comes from the metadata, never from sizeof). The
        // metadata-derived number is the padded one.
        Assert.Equal(12, Resolve("size A = 12 : TailPadded"));
    }

    [Fact]
    public void A_size_the_dump_cannot_support_is_refused_rather_than_guessed()
    {
        Assert.Contains("no type named", Refuse("size A = 4 : NoSuchType"), StringComparison.Ordinal);
        Assert.Contains("generic instantiation", Refuse("size A = 8 : BlobAssetReference<Thing>"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_open_generic_whose_fields_all_read_zero_is_refused_not_resolved()
    {
        // The real trap this guard exists for: Unity's NativeArray<T> is dumped with m_Buffer,
        // m_Length and m_AllocatorLabel all at `// 0x0`, because an open generic has no laid-out
        // instance. Resolving that would "discover" m_Length at 0 and then report the code's
        // correct 8 as a drift — a false alarm that would train the next reader to ignore the
        // tool. Both the size and the offset must refuse.
        Assert.Contains("all at offset 0", Refuse("size A = 16 : FakeNativeArray"), StringComparison.Ordinal);
        Assert.Equal(0, Resolve("offset A = 0 : FakeNativeArray.m_Length"));
    }

    [Fact]
    public void A_type_name_two_namespaces_declare_is_ambiguous_until_it_is_qualified()
    {
        Assert.Contains("declared 2 times", Refuse("size A = 4 : Twice"), StringComparison.Ordinal);
        Assert.Equal(4, Resolve("size A = 4 : Fake.One.Twice"));
        Assert.Equal(8, Resolve("size A = 8 : Fake.Two.Twice"));
    }

    // ---- resolver: enums and patterns ----------------------------------------------------------

    [Fact]
    public void An_enum_query_reads_the_member_value_and_the_size_reads_its_backing_field()
    {
        Assert.Equal(0, Resolve("enum A = 0 : FakeMode.None"));
        Assert.Equal(3, Resolve("enum A = 3 : FakeMode.Entity"));
        Assert.Equal(1, Resolve("size A = 1 : FakeMode"));   // value__ is a byte
        Assert.Contains("no enum member", Refuse("enum A = 9 : FakeMode.Nope"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_match_query_captures_a_number_the_dump_states_but_not_as_an_offset()
    {
        // Anchored to one type's block, because the same const line appears in every
        // FixedString size with a different number.
        Assert.Equal(125, Resolve(@"match A = 125 : /utf8MaxLengthInBytes = (\d+);/ in BiggerCappedString"));
        Assert.Equal(61, Resolve(@"match A = 61 : /utf8MaxLengthInBytes = (\d+);/ in CappedString"));
    }

    [Fact]
    public void An_unanchored_pattern_that_matches_two_different_numbers_is_ambiguous()
    {
        var error = Refuse(@"match A = 125 : /utf8MaxLengthInBytes = (\d+);/");
        Assert.Contains("different values", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_match_query_that_hits_nothing_or_has_the_wrong_group_count_is_refused()
    {
        Assert.Contains("matches no line", Refuse(@"match A = 1 : /never_in_the_dump = (\d+)/"), StringComparison.Ordinal);
        Assert.Contains("one capture group", Refuse(@"match A = 1 : /(u)tf8MaxLengthInBytes = (\d+);/"), StringComparison.Ordinal);
    }

    // ---- parser --------------------------------------------------------------------------------

    [Fact]
    public void The_first_directive_must_be_the_version()
    {
        Assert.False(LayoutDeclarationParser.TryParse(new[] { "offset A = 0 : X.Y" }, out _, out var errors));
        Assert.Contains(errors, e => e.Contains("layout v1", StringComparison.Ordinal));
    }

    [Fact]
    public void A_value_keeps_the_spelling_it_was_written_with_and_still_compares_as_a_number()
    {
        var hex = Parse("offset A = 0x40 : SmallElement.First");
        Assert.Equal("0x40", hex.ValueText);
        Assert.Equal(64, hex.Value);
        var dec = Parse("offset A = 64 : SmallElement.First");
        Assert.Equal("64", dec.ValueText);
        Assert.Equal(64, dec.Value);
    }

    [Fact]
    public void The_parser_names_every_way_a_line_can_be_wrong()
    {
        var lines = new[]
        {
            "layout v1",
            "dump sideways",
            "wobble A = 1 : X.Y",
            "offset  = 1 : X.Y",
            "offset A 1 : X.Y",
            "offset A = 1 X.Y",
            "offset B = zzz : X.Y",
            "offset C = 1 : NoDot",
            "offset Dup = 1 : X.Y",
            "offset Dup = 2 : X.Z",
            "match D = 1 : notslashed",
        };
        Assert.False(LayoutDeclarationParser.TryParse(lines, out _, out var errors));
        Assert.Contains(errors, e => e.Contains("'sideways'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("unknown declaration 'wobble'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("not a C# identifier", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("missing '='", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("missing ':'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("'zzz'", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Type.Member", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("declared twice", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("/slashes/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_citation_names_the_member_and_never_a_file_line()
    {
        // Generated doc comments carry the citation verbatim, and a line number in a generated
        // metadata file changes on every game update. The parser refuses that shape.
        foreach (var bad in new[] { "# types.cs:540068", "# types.cs 540068", "# server line 20557", "# lines 12-14" })
        {
            Assert.False(LayoutDeclarationParser.TryParse(
                new[] { "layout v1", "dump server", "offset A = 0 : X.Y " + bad }, out _, out var errors));
            Assert.Contains(errors, e => e.Contains("points at a file line", StringComparison.Ordinal));
        }
        Assert.True(LayoutDeclarationParser.TryParse(
            new[] { "layout v1", "dump server", "offset A = 0 : X.Y # an Entity (Index, Version); EV17" }, out _, out _));
    }

    // ---- emitter -------------------------------------------------------------------------------

    [Fact]
    public void The_emitter_is_deterministic_and_the_hash_covers_the_query_not_only_the_value()
    {
        var a = Parse("offset A = 8 : SmallElement.Third");
        var b = Parse("offset A = 8 : SmallElement.Second");
        Assert.NotEqual(
            LayoutDeclarationParser.Hash(new List<LayoutDeclaration> { a }),
            LayoutDeclarationParser.Hash(new List<LayoutDeclaration> { b }));

        var options = new LayoutGenOptions { BuildId = "1" };
        Assert.Equal(
            LayoutEmitter.Emit(new List<LayoutDeclaration> { a }, options),
            LayoutEmitter.Emit(new List<LayoutDeclaration> { a }, options));
    }

    [Fact]
    public void Check_ignores_a_moved_build_stamp_and_nothing_else()
    {
        var declarations = new List<LayoutDeclaration> { Parse("offset A = 8 : SmallElement.Third") };
        var first = LayoutEmitter.Emit(declarations, new LayoutGenOptions { BuildId = "1" });
        var second = LayoutEmitter.Emit(declarations, new LayoutGenOptions { BuildId = "2" });
        Assert.Equal(LayoutEmitter.CheckVerdict.UpToDate, LayoutEmitter.Check(first, second, out _));

        var edited = first.Replace("= 8;", "= 9;", StringComparison.Ordinal);
        Assert.Equal(LayoutEmitter.CheckVerdict.Stale, LayoutEmitter.Check(edited, first, out var detail));
        Assert.Contains("hand-edited", detail, StringComparison.Ordinal);

        var other = LayoutEmitter.Emit(
            new List<LayoutDeclaration> { Parse("offset A = 4 : SmallElement.Second") },
            new LayoutGenOptions { BuildId = "1" });
        Assert.Equal(LayoutEmitter.CheckVerdict.Stale, LayoutEmitter.Check(other, first, out detail));
        Assert.Contains("declarations hash", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_reports_a_disagreement_and_never_rewrites_the_declared_value()
    {
        // The whole reason the tool exists: the dump says 4, the declaration says 5, and the
        // answer is a loud "these disagree", not a quietly re-emitted 4. An offset that moved on
        // a game update needs the code fixed, and a generator that absorbed the new number would
        // ship an unverified write under a green build.
        var wrong = Parse("offset A = 5 : SmallElement.Second");
        var resolution = Assert.Single(LayoutEmitter.Resolve(new List<LayoutDeclaration> { wrong }, Fake()));
        Assert.True(resolution.Resolved);
        Assert.False(resolution.Agrees);
        Assert.Equal(4, resolution.Value);
        Assert.Equal(5, resolution.Declaration.Value);
        Assert.Contains("= 5;", LayoutEmitter.Emit(new List<LayoutDeclaration> { wrong }, new LayoutGenOptions()), StringComparison.Ordinal);
    }

    // ---- the checked-in artifacts (game-free: no dump needed) -----------------------------------

    private static string DeclarationsPath =>
        Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core", "Layout", "layout.txt");

    private static string GeneratedPath =>
        Path.Combine(SourcePin.RepoRoot(), "src", "Progenitor.Core", "Layout", "Generated", "LayoutConstants.cs");

    private static List<LayoutDeclaration> CheckedInDeclarations()
    {
        Assert.True(
            LayoutDeclarationParser.TryParse(File.ReadAllLines(DeclarationsPath), out var declarations, out var errors),
            string.Join("; ", errors));
        return declarations;
    }

    [Fact]
    public void The_checked_in_generated_file_matches_the_checked_in_declarations()
    {
        var declarations = CheckedInDeclarations();
        var generated = File.ReadAllText(GeneratedPath).Replace("\r\n", "\n");
        var hash = LayoutDeclarationParser.Hash(declarations);

        Assert.Contains("declarations: sha256:" + hash, generated, StringComparison.Ordinal);
        Assert.Contains("public const int DeclaredCount = " + declarations.Count + ";", generated, StringComparison.Ordinal);

        // Regenerating with the header the file already carries reproduces it byte for byte.
        // That property is what makes `layoutgen --check` a usable gate, and it is asserted here
        // so CI covers the gate on a machine that has neither the game nor the 127 MB dump.
        var regenerated = LayoutEmitter.Emit(declarations, new LayoutGenOptions
        {
            DeclarationsPath = "src/Progenitor.Core/Layout/layout.txt",
            BuildId = LayoutEmitter.BuildIdOf(generated),
            DumpResolved = generated.Contains("resolved:     true", StringComparison.Ordinal),
            // Carried the same way `build:` is, and for the same reason: this machine may have
            // neither game binary, and the round trip has to reproduce the file it was handed
            // rather than the file this machine could have measured (job FLAY4).
            GameIdentity = LayoutEmitter.GameIdentityOf(generated),
            // And its provenance line, carried for the same reason (job FLAY6): a machine that
            // measured nothing has no business rewriting the record of who measured what.
            GameMeasured = LayoutEmitter.GameMeasuredOf(generated),
        });
        Assert.Equal(generated, regenerated);
    }

    [Fact]
    public void Every_declared_value_is_the_value_the_compiled_constant_carries()
    {
        // The file comparison above proves the text agrees. This proves the *compiled* surface
        // agrees, which is the thing the migrated call sites actually read.
        var fields = typeof(global::Progenitor.Layout)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .ToDictionary(f => f.Name, f => (int)f.GetRawConstantValue()!, StringComparer.Ordinal);

        foreach (var declaration in CheckedInDeclarations())
        {
            Assert.True(fields.ContainsKey(declaration.Name), "Layout has no constant " + declaration.Name);
            Assert.Equal(declaration.Value, fields[declaration.Name]);
        }
        Assert.Equal(global::Progenitor.Layout.DeclaredCount, CheckedInDeclarations().Count);
    }

    [Fact]
    public void Every_declaration_names_a_dump_that_exists_and_carries_a_citation_or_a_neighbour()
    {
        foreach (var declaration in CheckedInDeclarations())
        {
            Assert.True(declaration.Side is "server" or "client", declaration.Name + " has side '" + declaration.Side + "'");
            Assert.NotEqual("", declaration.Query);
        }
    }

    // ---- the migrated sites ---------------------------------------------------------------------

    /// <summary>
    /// Every constant site that used to carry a dump-derived literal, and the <c>Layout.</c>
    /// reference it now carries instead.
    ///
    /// <para>These are the pins the job exists for. A future edit that "simplifies" one of these
    /// back to a literal puts the number outside the one file <c>--check</c> reads, which is
    /// precisely the state this job found and removed — so the pin is on the reference, and it
    /// goes through <see cref="SourcePin.AssertStatement"/> rather than
    /// <c>Assert.Contains</c>, because a commented-out constant satisfies the latter.</para>
    /// </summary>
    public static TheoryData<string, string, string> MigratedSites() => new()
    {
        { "src/Progenitor.Core/Castles/CastleTerritoryBuffers.cs", "BlockElementStride", "Layout.CastleTerritoryBlocksStride" },
        { "src/Progenitor.Core/Castles/CastleTerritoryBuffers.cs", "BlockCoordinateXOffset", "Layout.CastleTerritoryBlocksBlockCoordinateOffset" },
        { "src/Progenitor.Core/Castles/CastleTerritoryBuffers.cs", "TileElementStride", "Layout.CastleTerritoryTilesStride" },
        { "src/Progenitor.Core/Castles/CastleTerritoryBuffers.cs", "TileFlagOffset", "Layout.CastleTerritoryTilesInsideBuildableOffset" },
        { "src/Progenitor.Core/Effects/SequenceBlackboardLayout.cs", "InputIndexOffset", "Layout.SequenceInputElementIndexOffset" },
        { "src/Progenitor.Core/Effects/SequenceBlackboardLayout.cs", "InputTypeOffset", "Layout.SequenceInputElementTypeOffset" },
        { "src/Progenitor.Core/Effects/SequenceBlackboardLayout.cs", "InputIsInputParameterOffset", "Layout.SequenceInputElementIsInputParameterOffset" },
        { "src/Progenitor.Core/Effects/SequenceBlackboardLayout.cs", "TypeFloat3", "Layout.BlackboardRegisterTypeFloat3" },
        { "src/Progenitor.Core/Effects/SequenceBlackboardLayout.cs", "TypeQuaternion", "Layout.BlackboardRegisterTypeQuaternion" },
        { "src/Progenitor.Core/Effects/SequenceBlackboardLayout.cs", "BlackboardElementSize", "Layout.BlackboardElementStride" },
        { "src/Progenitor.Core/Effects/SequenceBlackboardLayout.cs", "BlackboardElementValueOffset", "Layout.BlackboardElementValueOffset" },
        { "src/Progenitor.Core/Effects/SequenceBlackboardLayout.cs", "LineRendererPositionElementSize", "Layout.LineRendererPositionElementStride" },
        { "src/Progenitor.Core/Effects/SequenceBlackboardLayout.cs", "SequenceGuidOffset", "Layout.BuffSequenceSequenceGuidOffset" },
        { "src/Progenitor.Core/Effects/SequenceBlackboardLayout.cs", "ConditionOffset", "Layout.BuffSequenceConditionOffset" },
        { "src/Progenitor.Core/Effects/SequenceBlackboardLayout.cs", "PositionIndexOffset", "Layout.LineRendererPositionIndexOffset" },
        { "src/Progenitor.Core/Effects/SequenceBlackboardLayout.cs", "BlobAssetHeaderBytes", "Layout.BlobAssetHeaderBytes" },
        { "src/Progenitor.Core/Effects/SequenceBlackboardLayout.cs", "BlobAssetHeaderLengthOffset", "Layout.BlobAssetHeaderLengthOffset" },
        { "src/Progenitor.Core/Effects/SequenceBlackboardLayout.cs", "NameMaxBytes", "Layout.FixedString64MaxUtf8Bytes" },
        { "src/Progenitor.Core/Units/UnitSteering.cs", "FollowerFollowedOffset", "Layout.FollowerFollowedOffset" },
        { "src/Progenitor.Core/Units/UnitSteering.cs", "FollowerModeOffset", "Layout.FollowerModeOffset" },
        { "src/Progenitor.Core/Units/UnitSteering.cs", "FollowerStationaryOffset", "Layout.FollowerStationaryOffset" },
        { "src/Progenitor.Core/Units/UnitSteering.cs", "FollowModeUnit", "Layout.FollowModeUnit" },
        { "src/Progenitor.Core/Units/UnitSteering.cs", "AiMoveTargetPositionOffset", "Layout.AiMoveTargetPositionOffset" },
        { "src/Progenitor.Core/Units/UnitSteering.cs", "AiMoveTargetEntityMovementOffset", "Layout.AiMoveTargetEntityMovementOffset" },
        { "src/Progenitor.Core/Units/UnitSteering.cs", "AiMoveTargettingModeOffset", "Layout.AiMoveTargettingModeOffset" },
        { "src/Progenitor.Core/Units/UnitSteering.cs", "TargettingNone", "Layout.AiTargettingModeNone" },
        { "src/Progenitor.Core/Units/UnitSteering.cs", "TargettingPosition", "Layout.AiTargettingModePosition" },
        { "src/Progenitor.Core/Units/UnitSteering.cs", "TargettingEntity", "Layout.AiTargettingModeEntity" },
        { "src/Progenitor.Core/Units/UnitSteering.cs", "BtIdle", "Layout.GenericEnemyStateIdle" },
        { "src/Progenitor.Core/Units/UnitSteering.cs", "BtFollow", "Layout.GenericEnemyStateFollow" },
        { "src/Progenitor.Core/Castles/CastleLogic.cs", "FloorTypeTreasury", "Layout.CastleFloorTypesTreasury" },
        { "src/Progenitor.Core/Content/PrefabLiveRefreshApply.cs", "BuffTargetOffset", "Layout.BuffTargetOffset" },
        { "src/Progenitor.Dev/Ops/HealLogic.cs", "HealthMaxOffset", "Layout.HealthMaxOffset" },
        { "src/Progenitor.Dev/Ops/HealLogic.cs", "HealthValueOffset", "Layout.HealthValueOffset" },
        { "src/Progenitor.Dev/Ops/HealLogic.cs", "HealthIsDeadOffset", "Layout.HealthIsDeadOffset" },
        { "src/Progenitor.Dev/Ops/HealLogic.cs", "BloodMaxOffset", "Layout.BloodMaxOffset" },
        { "src/Progenitor.Dev/Ops/HealLogic.cs", "BloodValueOffset", "Layout.BloodValueOffset" },
        { "src/Progenitor.Dev/Ops/WorldSaveOps.cs", "MaxFileNameUtf8Bytes", "Layout.FixedString128MaxUtf8Bytes" },
        { "src/Progenitor.Dev/Ops/WorldSaveOps.cs", "CompressSaveFilesOffset", "Layout.SavePersistentDataCompressSaveFilesOffset" },
        { "src/Progenitor.Dev/Ops/WorldSaveOps.cs", "SaveFileNameOffset", "Layout.SavePersistentDataSaveFileNameOffset" },
        { "src/Progenitor.Dev/Ops/WorldSaveOps.cs", "SaveFileNameBytes", "Layout.FixedString512PayloadBytes" },
    };

    [Theory]
    [MemberData(nameof(MigratedSites))]
    public void A_migrated_constant_reads_the_generated_layout_and_not_a_literal(
        string relativePath, string constant, string reference)
    {
        var text = File.ReadAllText(Path.Combine(SourcePin.RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));
        SourcePin.AssertUncommented(text, constant + " = " + reference + ";");
    }

    [Fact]
    public void Every_layout_constant_the_declarations_emit_is_actually_referenced_somewhere()
    {
        // A generated constant nothing reads is a constant nothing keeps honest: the migration is
        // only worth having if the call sites moved with it. Counting references from src/ is the
        // cheapest way to notice a half-finished one. Samples count too: Layout is public, and a sample
        // that pokes a game struct (DYEALL's ChangeTransmogColorEvent) must read the same checked facts.
        var root = SourcePin.RepoRoot();
        var sources = new[] { "src", "samples" }
            .SelectMany(dir => Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            .Where(p => !p.Contains(Path.Combine("Layout", "Generated"), StringComparison.Ordinal)
                        && !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();

        var unreferenced = CheckedInDeclarations()
            .Where(d => !sources.Any(s => s.Contains("Layout." + d.Name, StringComparison.Ordinal)))
            .Select(d => d.Name)
            .ToList();
        Assert.Empty(unreferenced);
    }

    [Fact]
    public void No_dump_derived_literal_is_left_in_the_files_this_job_migrated()
    {
        // The mutation this whole job is a defense against: someone edits one of these files and
        // types the number back in. Every `const int`/`const byte` in the migrated files that is
        // a bare numeric literal is listed here with a reason, so a new one has to be argued for
        // rather than added quietly.
        //
        // What stays a literal, and why, is in docs/modules.md "Layout constants": these are
        // arithmetic over other constants, Progenitor's own wire format, or a field width the
        // dump states as a type rather than a number.
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // Derived from the migrated offsets, not typed: the dump states offsets and no sizes.
            // (`InputElementStride` used to sit here as arithmetic over the next two; R53 finding 7
            // moved it into layout.txt as a `size`, because layoutgen derives that stride from the
            // same three offsets and it is the one number in this file a live regression has
            // already got wrong. These two stay: they are the inputs to the local
            // `InputElementPackedBytes` cross-check that the declared stride is that arithmetic.)
            "InputIsInputParameterBytes", "InputElementAlignment",
            "BlackboardElementAlignment", "BlackboardElementValueBytes",
            "LineRendererPositionElementAlignment",
            "GuidHashBytes", "ConditionBytes", "Alignment",
            "Float3Bytes", "QuaternionBytes", "IndexFieldBytes", "PositionFieldBytes",
            // Refusal bounds and sanity caps: Progenitor's own policy, nothing in the dump.
            "MaxInputs", "CacheCap", "MaxBlobOffset", "MaxElements", "MinElements",
            // Native out of a function BODY (the block loop divides tile coordinates by 10),
            // not stated anywhere in the metadata — it carries types and fields, not
            // the arithmetic inside a method. See the citation on the constant.
            "BlockTileSize",
            // CastleLogic's own wall bitmask: the game's CastleFloor.WallNorth/East/South/West are
            // Entity fields, not flag values, so these four numbers are
            // Progenitor's encoding and there is nothing in the metadata to check them against.
            "WallNorth", "WallEast", "WallSouth", "WallWest",
            // Debug-bridge row caps, ours.
            "DefaultLimit", "MaxLimit",
            // Ours, not the game's: a magnitude cap on a buffer length, and the second int of
            // an int2 the dump states as one field rather than two.
            "BlockCoordinateYOffset", "MaxTerritoryBlocks",
        };

        var offenders = new List<string>();
        foreach (var path in MigratedSites().Select(row => (string)row[0]!).Distinct())
        {
            var lines = File.ReadAllLines(Path.Combine(SourcePin.RepoRoot(), path.Replace('/', Path.DirectorySeparatorChar)));
            foreach (var offender in BareNumericConstants(lines))
            {
                if (!allowed.Contains(offender.Name)) offenders.Add(path + ": " + offender.Name + " = " + offender.Value);
            }
        }

        Assert.True(offenders.Count == 0,
            "bare numeric layout literal(s) outside layout.txt: " + string.Join("; ", offenders)
            + ". Declare each in src/Progenitor.Core/Layout/layout.txt and reference Layout.<Name>, or"
            + " add it to this test's allow-list with the reason the dump cannot resolve it.");
    }

    // ---- the classifier behind the sweep (R53 finding 6) ----------------------------------------

    /// <summary>One bare numeric constant declaration the sweep found.</summary>
    private readonly record struct BareConstant(string Name, string Value);

    /// <summary>
    /// Every line of <paramref name="lines"/> that declares an integer constant whose value is a
    /// bare number.
    ///
    /// <para><b>Why this is comment-tolerant (R53 finding 6).</b> The first version of this
    /// classifier took everything after <c>=</c>, trimmed a trailing <c>;</c>, and then decided
    /// "not a bare number" for anything containing a space — so a trailing <c>// cited 2730760</c>
    /// made the number invisible to it. That is not a contrived mutation: <b>it is the exact shape
    /// these constants had before they were migrated</b> (layout.txt's own header: "literals
    /// scattered through src/ with a metadata line number in a comment and nothing checking
    /// them"). The one way a reviewer would naturally write the regression was the one way the
    /// guard could not see it. Comments are now cut before the value is read, the way
    /// <c>SourcePin</c> cuts them, and the remaining token has to parse as an integer rather than
    /// merely start with a digit.</para>
    ///
    /// <para>The line filter is wide for the same reason: <c>internal const byte</c>,
    /// <c>const long</c>, <c>static readonly int</c> and a <c>protected</c> modifier all used to
    /// slip past a four-spelling <c>StartsWith</c> chain, and the old <c>parts[3]</c> mis-named
    /// anything with a different number of leading tokens. The name is now the token immediately
    /// before the <c>=</c>.</para>
    /// </summary>
    private static List<BareConstant> BareNumericConstants(IEnumerable<string> lines)
    {
        var found = new List<BareConstant>();
        foreach (var raw in lines)
        {
            var trimmed = CodeOnly(raw).Trim();
            int eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;

            var head = trimmed.Substring(0, eq).Trim();
            var parts = head.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var name = parts[^1];
            var modifiers = parts[..^1];
            if (!modifiers.Contains("const", StringComparer.Ordinal)
                && !modifiers.Contains("readonly", StringComparer.Ordinal)) continue;
            if (!modifiers.Any(IntegerTypes.Contains)) continue;

            var value = trimmed.Substring(eq + 1).Trim().TrimEnd(';').Trim();
            if (!IsBareInteger(value)) continue;
            found.Add(new BareConstant(name, value));
        }

        return found;
    }

    private static readonly HashSet<string> IntegerTypes = new(StringComparer.Ordinal)
    {
        "int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte", "nint", "nuint",
    };

    /// <summary>
    /// <paramref name="value"/> is a decimal or <c>0x</c> integer and nothing else — not an
    /// expression, not a reference, not a number with a comment glued to it.
    /// </summary>
    private static bool IsBareInteger(string value)
    {
        if (value.Length == 0) return false;
        var body = value.TrimEnd('u', 'U', 'l', 'L');
        if (body.Length == 0) return false;
        if (body.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(body.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
        }

        return ulong.TryParse(
            body.Replace("_", "", StringComparison.Ordinal), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// The part of <paramref name="line"/> before a <c>//</c> or a <c>/*</c> that is not inside a
    /// string or character literal, with a <c>/* … */</c> closed on the same line cut out. The
    /// same rule <c>SourcePin.CodeOf</c> applies, and the same declared residual: a block comment
    /// opened on an earlier line is not tracked, which
    /// <c>SourcePinContractTests.No_cs_file_under_src_samples_or_tools_opens_a_block_comment_across_lines</c>
    /// already forbids across every file this sweep reads.
    /// </summary>
    private static string CodeOnly(string line)
    {
        var sb = new System.Text.StringBuilder(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                int close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (close < 0) break;
                sb.Append(' ');
                i = close + 1;
                continue;
            }

            if (c == '@' && i + 1 < line.Length && line[i + 1] == '"')
            {
                int end = SkipVerbatimLiteral(line, i + 2);
                sb.Append(line, i, end - i + 1);
                i = end;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                int end = SkipQuotedLiteral(line, i + 1, c);
                sb.Append(line, i, end - i + 1);
                i = end;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static int SkipQuotedLiteral(string line, int from, char quote)
    {
        for (int i = from; i < line.Length; i++)
        {
            if (line[i] == '\\') { i++; continue; }
            if (line[i] == quote) return i;
        }

        return line.Length - 1;
    }

    private static int SkipVerbatimLiteral(string line, int from)
    {
        for (int i = from; i < line.Length; i++)
        {
            if (line[i] != '"') continue;
            if (i + 1 < line.Length && line[i + 1] == '"') { i++; continue; }
            return i;
        }

        return line.Length - 1;
    }

    [Fact]
    public void The_literal_sweep_sees_a_number_that_kept_its_citation_comment()
    {
        // R53 finding 6, and the exact mutation the review used. The second line is the shape
        // every one of these constants had before FLAY1 migrated it, so a guard that cannot see
        // it is a guard against nothing.
        var caught = BareNumericConstants(new[]
        {
            "    internal const int InputIndexOffset = 0x40;",
            "    internal const int InputIndexOffset = 0x40; // cited 2730760",
            "    internal const int InputIndexOffset = 0x40; /* cited 2730760 */",
            "    internal const int InputIndexOffset = 0x40;   //cited 2730760, EV17",
        });
        Assert.Equal(4, caught.Count);
        Assert.All(caught, c => Assert.Equal("InputIndexOffset", c.Name));
        Assert.All(caught, c => Assert.Equal("0x40", c.Value));
    }

    [Fact]
    public void The_literal_sweep_reads_the_spellings_the_four_way_StartsWith_chain_missed()
    {
        // The three smaller holes finding 6 lists beside the comment one: a byte const behind an
        // accessibility the chain did not spell, a wider integer type, and `static readonly`.
        // `parts[3]` also mis-named anything whose modifier count differed, so the names are
        // asserted, not just the count.
        var caught = BareNumericConstants(new[]
        {
            "    internal const byte Flags = 3;",
            "    private const byte Mask = 0x0F;",
            "    protected const int Legacy = 12;",
            "    public const long Wide = 76;",
            "    private static readonly int Late = 24;",
            "    public const uint Unsigned = 8u;",
        });
        Assert.Equal(
            new[] { "Flags", "Mask", "Legacy", "Wide", "Late", "Unsigned" },
            caught.Select(c => c.Name).ToArray());
    }

    [Fact]
    public void The_literal_sweep_leaves_alone_what_is_not_a_bare_number()
    {
        // A migrated site, arithmetic over other constants, a string, and a commented-out
        // declaration must all read as clean — otherwise the sweep is noise and gets suppressed.
        Assert.Empty(BareNumericConstants(new[]
        {
            "    internal const int InputIndexOffset = Layout.SequenceInputElementIndexOffset;",
            "    internal const int Packed = InputIsInputParameterOffset + 1;",
            "    internal const string Cite = \"cited 2730760\";",
            "    // internal const int InputIndexOffset = 0x40;",
            "    /// <summary>0x40 is the offset.</summary>",
            "    internal int NotAConstant = 40;",
            "    internal const int FromCall = Round(73, 4);",
        }));
    }

    // ---- the exit code (R53 finding 1) ----------------------------------------------------------

    private static LayoutCheckTally TallyOf(params string[] declarationLines)
    {
        var lines = new List<string> { "layout v1", "dump server" };
        lines.AddRange(declarationLines);
        Assert.True(
            LayoutDeclarationParser.TryParse(lines, out var declarations, out var errors),
            string.Join("; ", errors));
        return LayoutCheckTally.Of(LayoutEmitter.Resolve(declarations, Fake()), new[] { "server" });
    }

    [Fact]
    public void A_declaration_the_dump_cannot_resolve_fails_the_check_like_a_disagreement()
    {
        // R53 finding 1, and both variants the review measured. A game update that RENAMES a type
        // or a field does not move a number, it removes the thing the number was measured
        // against — so nothing disagrees, and before this the tool exited 0 and said so.
        var vanishedType = TallyOf("offset A = 0 : TypeThatVanishedInAGameUpdate.First");
        Assert.Single(vanishedType.Unresolved);
        Assert.Contains("no type named", vanishedType.Unresolved[0].Error, StringComparison.Ordinal);
        Assert.True(vanishedType.DumpFailed);
        Assert.Equal(0, vanishedType.Agreements);

        var renamedField = TallyOf("offset A = 0 : SmallElement.FirstRenamedByAGameUpdate");
        Assert.Single(renamedField.Unresolved);
        Assert.Contains("FirstRenamedByAGameUpdate", renamedField.Unresolved[0].Error, StringComparison.Ordinal);
        Assert.True(renamedField.DumpFailed);

        // And the case that already worked, so the new rule is an addition and not a replacement.
        Assert.True(TallyOf("offset A = 5 : SmallElement.Second").DumpFailed);
        Assert.False(TallyOf("offset A = 4 : SmallElement.Second").DumpFailed);
    }

    [Fact]
    public void An_unresolved_declaration_is_named_with_the_layout_txt_line_it_came_from()
    {
        // The report has to say where to go. The parser carries the 1-based line, and the line
        // number is deliberately not part of the declarations hash, so adding a comment above a
        // declaration does not make every generated file in the tree read as stale.
        Assert.True(LayoutDeclarationParser.TryParse(
            new[] { "layout v1", "dump server", "# a comment", "offset A = 0 : Gone.First" },
            out var declarations, out _));
        var declaration = Assert.Single(declarations);
        Assert.Equal(4, declaration.LineNumber);
        Assert.Equal("layout.txt:4", LayoutCheckTally.Where("layout.txt", declaration));

        var withComment = LayoutDeclarationParser.Hash(declarations);
        Assert.True(LayoutDeclarationParser.TryParse(
            new[] { "layout v1", "dump server", "offset A = 0 : Gone.First" }, out var moved, out _));
        Assert.Equal(3, moved[0].LineNumber);
        Assert.Equal(withComment, LayoutDeclarationParser.Hash(moved));
    }

    [Fact]
    public void The_summary_line_never_claims_agreement_about_a_value_it_did_not_look_at()
    {
        // R53 finding 1's second half. The old line was a two-way ternary on "was a dump given",
        // so the words "the dump agrees with every value" were printed by a branch that had not
        // measured whether every value was checked.
        Assert.Equal(
            "the dump agrees with all 2 values",
            TallyOf("offset A = 0 : SmallElement.First", "offset B = 4 : SmallElement.Second").Verdict());

        var withUnresolved = TallyOf("offset A = 0 : SmallElement.First", "offset B = 0 : Gone.First");
        Assert.Equal("1 value agree, 1 value could not be resolved in the dump", withUnresolved.Verdict());
        Assert.DoesNotContain("every value", withUnresolved.Verdict(), StringComparison.Ordinal);

        Assert.Contains(
            "disagree with the dump",
            TallyOf("offset A = 9 : SmallElement.First").Verdict(), StringComparison.Ordinal);

        // No dump at all is the CI shape and must stay a pass with an honest sentence.
        Assert.True(LayoutDeclarationParser.TryParse(
            new[] { "layout v1", "dump server", "offset A = 0 : SmallElement.First" }, out var none, out _));
        var noDump = LayoutCheckTally.Of(
            new List<LayoutEmitter.Resolution> { new(none[0], false, 0, "no server dump given") },
            Array.Empty<string>());
        Assert.False(noDump.DumpFailed);
        Assert.False(noDump.FullyResolved);
        Assert.Equal("no dump given, values unconfirmed", noDump.Verdict());
    }

    [Fact]
    public void A_side_with_no_dump_on_this_run_is_skipped_and_never_fails_the_check()
    {
        // The benign third case, kept apart from unresolved on purpose: `--check` with only a
        // server dump must not fail because a client declaration went unasked, or the deploy gate
        // finding 1 adds would fail every run on a machine with one dump.
        Assert.True(LayoutDeclarationParser.TryParse(
            new[]
            {
                "layout v1", "dump server", "offset A = 0 : SmallElement.First",
                "dump client", "offset B = 4 : SmallElement.Second",
            },
            out var declarations, out _));
        var resolutions = new List<LayoutEmitter.Resolution>
        {
            LayoutEmitter.Resolve(new List<LayoutDeclaration> { declarations[0] }, Fake())[0],
            new(declarations[1], false, 0, "no client dump given"),
        };
        var tally = LayoutCheckTally.Of(resolutions, new[] { "server" });
        Assert.False(tally.DumpFailed);
        Assert.Equal(1, tally.Skipped);
        Assert.False(tally.FullyResolved);
        Assert.Contains("1 value unchecked (no dump for their side)", tally.Verdict(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_stride_with_the_live_regression_behind_it_is_declared_and_not_only_derived()
    {
        // R53 finding 7. SequenceInputElement's 76-byte stride is the number a client regression
        // already got wrong ("stride is 73, not 76", every beam path=none), and it was the one
        // FLAY1 left as arithmetic. Declaring it is safe because layoutgen derives it from the
        // same three offsets by the same round-up rule — which this asserts over the fake dump's
        // copy of that shape, so a change to either rule shows up here.
        var declaration = Assert.Single(
            CheckedInDeclarations().Where(d => d.Name == "SequenceInputElementStride").ToList());
        Assert.Equal(LayoutQueryKind.Size, declaration.Kind);
        Assert.Equal(76, declaration.Value);
        Assert.Equal(76, global::Progenitor.Layout.SequenceInputElementStride);
        Assert.Equal(
            global::Progenitor.Layout.SequenceInputElementStride,
            global::Progenitor.Effects.SequenceBlackboardLayout.RoundUpToAlignment(
                global::Progenitor.Layout.SequenceInputElementIsInputParameterOffset + 1, 4));

        // TailPadded in the fake dump is that shape: a byte blob, then 4-byte fields, then a
        // bool — extent 9, alignment 4, stride 12. The rule that turns 73 into 76 is the rule
        // that turns 9 into 12.
        Assert.Equal(12, Resolve("size A = 12 : TailPadded"));
    }

    // ---- is the oracle a dump of THIS game? (R54 finding 1, job FLAY3) ---------------------------
    //
    // Over fabricated files with fabricated timestamps, never the real dump: the rule under test is
    // "older than the binary it claims to describe", and the only way to test it honestly is to own
    // both timestamps. The real 127 MB dump is not touched by anything below.

    private sealed class FakeGameFiles : IDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "progenitor-flay3-" + Guid.NewGuid().ToString("N"));

        public FakeGameFiles() => Directory.CreateDirectory(_root);

        /// <summary>Write a file of <paramref name="bytes"/> bytes stamped <paramref name="writtenUtc"/>.</summary>
        public string Write(string name, DateTime writtenUtc, int bytes = 8)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllBytes(path, new byte[bytes]);
            File.SetLastWriteTimeUtc(path, writtenUtc);
            return path;
        }

        public string Missing(string name) => Path.Combine(_root, name);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }

    private static readonly DateTime Patched = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static DumpFreshness Judge(
        string dumpPath,
        string binaryPath,
        bool check = true,
        IReadOnlyDictionary<string, DumpFreshness.Identity>? recorded = null) =>
        DumpFreshness.Of(
            new Dictionary<string, string> { ["server"] = dumpPath },
            binaryPath.Length == 0
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { ["server"] = binaryPath },
            recorded ?? new Dictionary<string, DumpFreshness.Identity>(),
            recordedIsBinding: check);

    [Fact]
    public void A_dump_written_after_the_game_binary_passes_and_records_the_binarys_identity()
    {
        using var files = new FakeGameFiles();
        var binary = files.Write("GameAssembly.dll", Patched, bytes: 4096);
        var dump = files.Write("metadata.cs", Patched.AddHours(3));

        // The whole green path of `--check`: a current dump, and a header that records exactly
        // the binary on this machine. Since FLAY4 the record is not optional here — a check with
        // no recorded identity is a refusal, not a pass — so the matching identity IS the case
        // that passes, and this test is where that is pinned.
        var recorded = new Dictionary<string, DumpFreshness.Identity>
        {
            ["server"] = new DumpFreshness.Identity(Patched, 4096),
        };
        var verdict = Judge(dump, binary, check: true, recorded);

        Assert.False(verdict.Failed);
        Assert.Empty(verdict.Failures);
        Assert.Empty(verdict.Notes);
        Assert.Empty(verdict.Remedies);
        Assert.True(verdict.Measured.TryGetValue("server", out var identity));
        Assert.Equal(4096, identity.Length);
        Assert.Equal(Patched, identity.WrittenUtc);
        Assert.Equal("server=2026-09-01T12:00:00Z/4096", verdict.HeaderValue());

        // A dump written in the same second as the binary is not older, and must not fail: the
        // rule is a refusal to check against a PREVIOUS build, not a demand for a delay.
        Assert.False(Judge(files.Write("same.cs", Patched), binary, check: true, recorded).Failed);

        // And generating needs no record at all, which is what makes the first regenerate after a
        // re-dump possible.
        Assert.False(Judge(dump, binary, check: false).Failed);
    }

    [Fact]
    public void A_dump_older_than_the_binary_it_claims_to_describe_fails_the_check()
    {
        // The whole finding: Stunlock ships a patch, BepInEx regenerates the interop, the build
        // succeeds, and the gate resolves all 43 declarations against a dump of the previous
        // build and prints agreement. Nothing else in the tree notices.
        using var files = new FakeGameFiles();
        var dump = files.Write("metadata.cs", Patched.AddDays(-8));
        var binary = files.Write("GameAssembly.dll", Patched, bytes: 4096);

        var verdict = Judge(dump, binary);

        Assert.True(verdict.Failed);
        Assert.Equal(new[] { "server" }, verdict.FailedSides);
        var sentence = Assert.Single(verdict.Failures);
        Assert.Contains("older than the game binary", sentence, StringComparison.Ordinal);
        Assert.Contains("2026-08-24T12:00:00Z", sentence, StringComparison.Ordinal);
        Assert.Contains("2026-09-01T12:00:00Z", sentence, StringComparison.Ordinal);
        Assert.Contains(dump, sentence, StringComparison.Ordinal);
        Assert.Contains(binary, sentence, StringComparison.Ordinal);
        Assert.Contains("Re-export the game's metadata", DumpFreshness.HowToRefresh("server"), StringComparison.Ordinal);

        // Generating rather than checking is the same refusal: writing a header from a stale dump
        // is how the stale answer becomes the recorded one.
        Assert.True(Judge(dump, binary, check: false).Failed);
    }

    [Fact]
    public void A_binary_that_is_not_there_is_reported_and_never_silently_skipped()
    {
        using var files = new FakeGameFiles();
        var dump = files.Write("metadata.cs", Patched.AddHours(3));

        // Named and absent: the path this run believes in is wrong, so nothing established the
        // dump's freshness. Fail-closed.
        var absent = Judge(dump, files.Missing("GameAssembly.dll"));
        Assert.True(absent.Failed);
        Assert.Contains("GameAssembly.dll is not at", Assert.Single(absent.Failures), StringComparison.Ordinal);
        Assert.Empty(absent.Measured);

        // Not named at all: a gate that an omitted argument turns off must say so out loud. It is
        // not a failure (that would break every game-free invocation), but it is never silent.
        var unnamed = Judge(dump, "");
        Assert.False(unnamed.Failed);
        var note = Assert.Single(unnamed.Notes);
        Assert.Contains("not checked for freshness", note, StringComparison.Ordinal);
        Assert.Contains("--game-assembly", note, StringComparison.Ordinal);
        Assert.Contains(dump, note, StringComparison.Ordinal);
        Assert.Empty(unnamed.Measured);
    }

    [Fact]
    public void A_binary_that_is_not_the_one_the_header_recorded_fails_the_check_and_not_the_generate()
    {
        using var files = new FakeGameFiles();
        var binary = files.Write("GameAssembly.dll", Patched, bytes: 4096);
        var dump = files.Write("metadata.cs", Patched.AddHours(3));
        var recorded = new Dictionary<string, DumpFreshness.Identity>
        {
            ["server"] = new DumpFreshness.Identity(Patched.AddDays(-30), 4000),
        };

        var checking = Judge(dump, binary, check: true, recorded);
        Assert.True(checking.Failed);
        Assert.Contains(
            "is not the one the generated layout header records",
            Assert.Single(checking.Failures), StringComparison.Ordinal);

        // Generating is how a new identity is recorded after a re-dump, so it must not refuse the
        // very act of updating the record.
        Assert.False(Judge(dump, binary, check: false, recorded).Failed);

        // And the same identity, round-tripped through the header text, agrees with itself.
        var line = Judge(dump, binary).HeaderValue();
        var parsed = DumpFreshness.ParseHeaderValue(line);
        Assert.Equal(new DumpFreshness.Identity(Patched, 4096), parsed["server"]);
        Assert.False(Judge(dump, binary, check: true, parsed).Failed);
    }

    [Fact]
    public void A_header_that_records_no_identity_for_a_named_side_fails_the_check_it_cannot_run()
    {
        // R55 finding 4, job FLAY4. This is the case that made the rule above dead code for a
        // commit: the checked-in header had no `game:` line, so `recorded` was empty, `was.Known`
        // was false, the comparison never ran, and --check printed "the dump agrees with all 43
        // values" over a gate that was switched off. The absence is indistinguishable from "this
        // header predates the line", which is exactly why it may not be a fall-through.
        using var files = new FakeGameFiles();
        var binary = files.Write("GameAssembly.dll", Patched, bytes: 4096);
        var dump = files.Write("metadata.cs", Patched.AddHours(3));

        var verdict = Judge(dump, binary, check: true, new Dictionary<string, DumpFreshness.Identity>());

        Assert.True(verdict.Failed);
        Assert.Equal(new[] { "server" }, verdict.FailedSides);
        var sentence = Assert.Single(verdict.Failures);
        Assert.Contains("records no game identity for the server side", sentence, StringComparison.Ordinal);
        Assert.Contains(binary, sentence, StringComparison.Ordinal);
        Assert.Contains("2026-09-01T12:00:00Z/4096", sentence, StringComparison.Ordinal);
        Assert.Contains("a check that cannot fire", sentence, StringComparison.Ordinal);

        // The remedy is a REGENERATE, not a re-dump: the dump here is newer than the binary and
        // perfectly current. Sending the reader to re-export it would cost an hour and fix
        // nothing, which is why the two failures carry two procedures.
        var remedy = Assert.Single(verdict.Remedies);
        Assert.Contains("--game-assembly", remedy, StringComparison.Ordinal);
        Assert.DoesNotContain("Re-export", remedy, StringComparison.Ordinal);
        Assert.Equal(DumpFreshness.HowToRecord(), remedy);
        // The stale-dump remedy is the other one, and the two must not be the same sentence.
        Assert.NotEqual(DumpFreshness.HowToRefresh("server"), remedy);

        // A `default` Identity parsed out of a malformed header is "recorded nothing" too, and
        // must fail the same way rather than comparing equal to whatever was measured.
        var unknown = new Dictionary<string, DumpFreshness.Identity> { ["server"] = default };
        Assert.True(Judge(dump, binary, check: true, unknown).Failed);

        // Two sides missing a record print ONE instruction: the fix is one generate run naming
        // both, and a per-side copy invites doing half of it.
        var both = DumpFreshness.Of(
            new Dictionary<string, string> { ["server"] = dump, ["client"] = dump },
            new Dictionary<string, string> { ["server"] = binary, ["client"] = binary },
            new Dictionary<string, DumpFreshness.Identity>(),
            recordedIsBinding: true);
        Assert.Equal(2, both.Failures.Count);
        Assert.Single(both.Remedies);

        // Generating is not held to it: a generate with --game-assembly is precisely the act that
        // writes the missing line, so refusing there would make the failure unfixable.
        Assert.False(Judge(dump, binary, check: false, new Dictionary<string, DumpFreshness.Identity>()).Failed);

        // And a side whose binary was NOT named is still only a note. The rule is "named a
        // binary, recorded nothing", not "recorded nothing" — otherwise the game-free half of
        // --check, the half CI runs, would fail on every machine without the game.
        var unnamed = Judge(dump, "", check: true, new Dictionary<string, DumpFreshness.Identity>());
        Assert.False(unnamed.Failed);
        Assert.Single(unnamed.Notes);
    }

    /// <summary>
    /// Judge both sides off one dump, naming only the binaries in <paramref name="binaries"/>.
    /// The two-side shape is the whole subject of the merge tests: a one-side helper cannot
    /// express "this run measured the server and said nothing about the client".
    /// </summary>
    private static DumpFreshness JudgeBothSides(
        string dumpPath,
        Dictionary<string, string> binaries,
        bool check = false) =>
        DumpFreshness.Of(
            new Dictionary<string, string> { ["client"] = dumpPath, ["server"] = dumpPath },
            binaries,
            DumpFreshness.ParseHeaderValue(BothRecorded),
            recordedIsBinding: check);

    /// <summary>A checked-in <c>game:</c> line that records both sides, from an older build.</summary>
    private const string BothRecorded =
        "client=2026-01-01T00:00:00Z/4000 server=2026-01-01T00:00:00Z/4001";

    [Fact]
    public void A_generate_that_names_only_the_server_re_records_it_and_keeps_the_recorded_client()
    {
        // R56 finding 5, job FLAY5. The half-generate is the easy mistake: deploy.ps1 skips the
        // server dump on a machine with no test-server tree, and the mirror of that is a machine
        // with no game client. Before this the emitted `game:` line was built from the sides
        // MEASURED, so naming one side wrote one side and erased the other — and since FLAY4 an
        // erased side is a hard refusal, so the mistake broke the deploy of every machine that
        // had the binary the committing machine lacked.
        using var files = new FakeGameFiles();
        var binary = files.Write("GameAssembly.dll", Patched, bytes: 4096);
        var dump = files.Write("metadata.cs", Patched.AddHours(3));

        var freshness = JudgeBothSides(
            dump, new Dictionary<string, string> { ["server"] = binary });
        Assert.False(freshness.Failed);

        // What this run saw: the server only. That is the console line, and it is NOT the header.
        Assert.Equal("server=2026-09-01T12:00:00Z/4096", freshness.HeaderValue());

        var merged = freshness.MergeInto(BothRecorded);
        Assert.Equal(
            "client=2026-01-01T00:00:00Z/4000 server=2026-09-01T12:00:00Z/4096",
            merged.HeaderValue);
        Assert.Empty(merged.Dropped);
        Assert.Empty(merged.Unrecorded);

        // The carried-forward client identity is the one --check will compare against on a
        // machine that does have the client binary, so it has to survive the round trip through
        // the header text and not merely through the dictionary.
        var carried = DumpFreshness.ParseHeaderValue(merged.HeaderValue);
        Assert.Equal(new DumpFreshness.Identity(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), 4000),
            carried["client"]);
        Assert.Equal(new DumpFreshness.Identity(Patched, 4096), carried["server"]);

        // And the mirror image: naming only the client keeps the recorded server.
        var mirrored = JudgeBothSides(dump, new Dictionary<string, string> { ["client"] = binary })
            .MergeInto(BothRecorded);
        Assert.Equal(
            "client=2026-09-01T12:00:00Z/4096 server=2026-01-01T00:00:00Z/4001",
            mirrored.HeaderValue);
        Assert.Empty(mirrored.Dropped);
    }

    [Fact]
    public void A_generate_that_names_no_binary_keeps_both_recorded_identities_verbatim()
    {
        // The game-free generate — CI, and any contributor without the game installed. It measures
        // nothing, so it must reproduce the header it was handed character for character; a
        // re-formatting round trip here would be a diff on a line nobody edited, and a blanking
        // would be the finding-5 outage twice over.
        using var files = new FakeGameFiles();
        var dump = files.Write("metadata.cs", Patched.AddHours(3));

        var freshness = JudgeBothSides(dump, new Dictionary<string, string>());
        Assert.False(freshness.Failed);
        Assert.Equal(2, freshness.Notes.Count);   // one "not checked for freshness" per side
        Assert.Empty(freshness.Measured);
        Assert.Equal("", freshness.HeaderValue());

        var merged = freshness.MergeInto(BothRecorded);
        Assert.Equal(BothRecorded, merged.HeaderValue);
        Assert.Empty(merged.Dropped);
        Assert.Empty(merged.Unrecorded);

        // Including the pre-FLAY3 file that records nothing: it stays recording nothing rather
        // than gaining a line, which is what lets such a file regenerate byte for byte.
        Assert.Equal("", freshness.MergeInto("").HeaderValue);
    }

    [Fact]
    public void A_first_generate_records_the_side_it_names_and_says_the_other_is_unrecorded()
    {
        // The bootstrap case, and the one place the merge cannot help: there is nothing to carry
        // forward. Recording one side has to be allowed — otherwise the header could never be
        // started on a machine with one game tree — but the run says out loud which side it is
        // leaving uncovered, at the moment of the damage rather than a machine away, which is the
        // half of R56 finding 5 the carry-forward alone does not answer.
        using var files = new FakeGameFiles();
        var binary = files.Write("GameAssembly.dll", Patched, bytes: 4096);
        var dump = files.Write("metadata.cs", Patched.AddHours(3));

        var freshness = DumpFreshness.Of(
            new Dictionary<string, string> { ["client"] = dump, ["server"] = dump },
            new Dictionary<string, string> { ["server"] = binary },
            new Dictionary<string, DumpFreshness.Identity>(),
            recordedIsBinding: false);

        Assert.False(freshness.Failed);
        Assert.Equal(new[] { "client", "server" }, freshness.Sides);

        var merged = freshness.MergeInto("");
        Assert.Equal("server=2026-09-01T12:00:00Z/4096", merged.HeaderValue);
        Assert.Empty(merged.Dropped);
        Assert.Equal(new[] { "client" }, merged.Unrecorded);

        // And the remedy names the argument to add, not a re-dump: the client dump here is fine.
        var how = DumpFreshness.HowToName("client");
        Assert.Contains("--game-assembly client=", how, StringComparison.Ordinal);
        Assert.DoesNotContain("Re-export", how, StringComparison.Ordinal);

        // The second run, on a machine that has the client, completes the header without being
        // handed the server binary again — which is the whole point of the carry-forward.
        var completed = DumpFreshness.Of(
            new Dictionary<string, string> { ["client"] = dump, ["server"] = dump },
            new Dictionary<string, string> { ["client"] = binary },
            DumpFreshness.ParseHeaderValue(merged.HeaderValue),
            recordedIsBinding: false).MergeInto(merged.HeaderValue);
        Assert.Equal(
            "client=2026-09-01T12:00:00Z/4096 server=2026-09-01T12:00:00Z/4096",
            completed.HeaderValue);
        Assert.Empty(completed.Unrecorded);
        Assert.Empty(completed.Dropped);
    }

    [Fact]
    public void The_checked_in_generated_header_records_a_game_identity_for_both_sides()
    {
        // The regenerate FLAY4 exists to perform, asserted as a property of the artifact rather
        // than as a thing someone remembers to do. Without this line in the checked-in file the
        // strongest rule in the gate is unreachable no matter how correct the code above is, and
        // nothing else in the suite would notice it going missing again: the staleness diff
        // ignores `game:` by design, so a regenerate on a machine with no dump would drop the
        // line and every other test would stay green.
        var text = File.ReadAllText(GeneratedPath).Replace("\r\n", "\n");
        var identity = LayoutEmitter.GameIdentityOf(text);
        Assert.NotEqual("", identity);

        var recorded = DumpFreshness.ParseHeaderValue(identity);
        foreach (var side in new[] { "client", "server" })
        {
            Assert.True(recorded.TryGetValue(side, out var id), side + " has no recorded identity");
            Assert.True(id.Known);
            Assert.True(id.Length > 0, side + " records a zero-length GameAssembly.dll");
            Assert.True(id.WrittenUtc > new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        }
    }

    [Fact]
    public void The_generated_header_carries_the_game_identity_and_a_game_update_is_not_a_stale_file()
    {
        Assert.True(LayoutDeclarationParser.TryParse(
            new[] { "layout v1", "dump server", "offset A = 0 : SmallElement.First" },
            out var declarations, out _));

        var withIdentity = LayoutEmitter.Emit(declarations, new LayoutGenOptions
        {
            GameIdentity = "client=2026-09-01T12:00:00Z/4096 server=2026-09-01T12:00:00Z/4096",
        });
        Assert.Contains(
            "//     game:         client=2026-09-01T12:00:00Z/4096 server=2026-09-01T12:00:00Z/4096",
            withIdentity, StringComparison.Ordinal);
        Assert.Equal(
            "client=2026-09-01T12:00:00Z/4096 server=2026-09-01T12:00:00Z/4096",
            LayoutEmitter.GameIdentityOf(withIdentity));

        // A run that was told no binary emits no line at all and reads back as nothing, which is
        // what lets the caller carry the checked-in value forward instead of blanking it — and
        // what lets a file generated before this line existed regenerate byte for byte.
        var without = LayoutEmitter.Emit(declarations, new LayoutGenOptions());
        Assert.DoesNotContain("//     game:", without, StringComparison.Ordinal);
        Assert.Equal("", LayoutEmitter.GameIdentityOf(without));

        // The staleness diff must ignore the line: a game update is a freshness verdict with its
        // own message, not a "the file was hand-edited" report. Otherwise every machine without
        // the binary would read the checked-in file as stale and the gate would get ignored.
        Assert.Equal(
            LayoutEmitter.CheckVerdict.UpToDate,
            LayoutEmitter.Check(without, withIdentity, out _));

        // Files predating the header line read as recording nothing rather than throwing.
        Assert.Equal("", LayoutEmitter.GameIdentityOf("// <auto-generated>\n// </auto-generated>\n"));
    }

    // ------------------------------------ provenance of the game: line (job FLAY6, R57 finding 6)

    [Fact]
    public void The_header_says_which_sides_the_run_that_wrote_the_game_line_measured()
    {
        // R57 finding 6. After FLAY5 a `game:` entry may have been measured by this run or
        // carried forward from the checked-in header by a run that never had that binary —
        // possibly several game updates ago — and the line reads as one statement of fact either
        // way. The `measured:` line is the half that says which is which.
        Assert.True(LayoutDeclarationParser.TryParse(
            new[] { "layout v1", "dump server", "offset A = 0 : SmallElement.First" },
            out var declarations, out _));

        var text = LayoutEmitter.Emit(declarations, new LayoutGenOptions
        {
            GameIdentity = "client=2026-01-01T00:00:00Z/4000 server=2026-09-01T12:00:00Z/4096",
            GameMeasured = "server",
        });

        Assert.Contains("//     measured:     server  (sides the run that last wrote game:",
            text, StringComparison.Ordinal);
        Assert.Equal("server", LayoutEmitter.GameMeasuredOf(text));
        // The value column lines up with every other header line, so the block stays readable.
        Assert.Contains("//     measured:     server", text, StringComparison.Ordinal);

        // Two sides fit on the one line, ordinally sorted by the caller.
        var both = LayoutEmitter.Emit(declarations, new LayoutGenOptions
        {
            GameIdentity = "client=2026-01-01T00:00:00Z/4000 server=2026-09-01T12:00:00Z/4096",
            GameMeasured = "client server",
        });
        Assert.Equal("client server", LayoutEmitter.GameMeasuredOf(both));

        // No identity, no provenance: a line saying "nobody measured anything" about a `game:`
        // line that is not there would be a header line every pre-FLAY3 file has to gain.
        var none = LayoutEmitter.Emit(declarations, new LayoutGenOptions { GameMeasured = "server" });
        Assert.DoesNotContain("//     measured:", none, StringComparison.Ordinal);
        Assert.Equal("", LayoutEmitter.GameMeasuredOf(none));
        Assert.Equal("", LayoutEmitter.GameMeasuredOf("// <auto-generated>\n// </auto-generated>\n"));

        // And the staleness diff ignores it, exactly as it ignores `game:`. A one-sided generate
        // legitimately moves this line, and a moved provenance line must not make every other
        // machine read the checked-in file as hand-edited — which is the failure mode that made
        // the gate ignorable once already.
        Assert.Equal(LayoutEmitter.CheckVerdict.UpToDate, LayoutEmitter.Check(text, both, out _));
    }

    [Fact]
    public void The_checked_in_generated_header_records_which_sides_were_measured()
    {
        // Asserted as a property of the artifact, like the `game:` line above it: the provenance
        // is only worth anything if the committed file carries it, and nothing else in the suite
        // would notice a regenerate dropping the line (the staleness diff ignores it by design).
        var text = File.ReadAllText(GeneratedPath).Replace("\r\n", "\n");
        var measured = LayoutEmitter.GameMeasuredOf(text);
        Assert.Equal("client server", measured);

        // Every side it names is a side `game:` actually records.
        var recorded = DumpFreshness.ParseHeaderValue(LayoutEmitter.GameIdentityOf(text));
        foreach (var side in measured.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            Assert.True(recorded.ContainsKey(side), "measured: names " + side + ", game: does not");
    }

    [Fact]
    public void A_one_sided_generate_reports_the_value_it_will_write_and_names_the_carried_side()
    {
        // The console half of R57 finding 6. `Program` used to print
        // `layoutgen: game identity <what this run measured>` and then write
        // `<merged>` — on a one-sided generate two different strings, with nothing in the line
        // saying which one it was. Describe() annotates the value that will actually be written.
        using var files = new FakeGameFiles();
        var binary = files.Write("GameAssembly.dll", Patched, bytes: 4096);
        var dump = files.Write("metadata.cs", Patched.AddHours(3));

        var freshness = JudgeBothSides(
            dump, new Dictionary<string, string> { ["server"] = binary });
        var merged = freshness.MergeInto(BothRecorded);

        Assert.Equal("server", freshness.MeasuredSidesValue());
        Assert.Equal(
            "client=2026-01-01T00:00:00Z/4000 (carried) server=2026-09-01T12:00:00Z/4096 (measured)",
            freshness.Describe(merged.HeaderValue));
        Assert.Equal(
            "measured this run: server; carried from the header: client",
            freshness.ProvenanceSummary(merged.HeaderValue));

        // The old line — this run's measurements — is still available and still says something
        // different, which is exactly why the console must not use it for a generate.
        Assert.Equal("server=2026-09-01T12:00:00Z/4096", freshness.HeaderValue());
    }

    [Fact]
    public void A_generate_that_measured_nothing_says_so_and_carries_the_provenance_forward()
    {
        // CI and any contributor without the game: it measures nothing, writes the recorded
        // identity back verbatim, and must therefore leave the provenance line alone too —
        // rewriting it to "nobody" would erase the record of the run that did the measuring and
        // would break the byte-for-byte regenerate the whole gate rests on.
        using var files = new FakeGameFiles();
        var dump = files.Write("metadata.cs", Patched.AddHours(3));

        var freshness = JudgeBothSides(dump, new Dictionary<string, string>());
        Assert.Equal("", freshness.MeasuredSidesValue());
        Assert.Equal(
            "measured this run: none; carried from the header: client, server",
            freshness.ProvenanceSummary(BothRecorded));
        Assert.Equal("(nothing recorded)", freshness.Describe(""));
    }

    [Fact]
    public void A_check_of_a_side_whose_recorded_identity_was_carried_still_compares_it()
    {
        // The identity is the identity: how it got into the header does not change what it
        // asserts. A `measured:` line naming only the server must not make the client's recorded
        // identity advisory — that would turn the provenance marker into a way of disarming the
        // strongest rule in the gate on exactly the sides FLAY5 made carryable.
        using var files = new FakeGameFiles();
        var dump = files.Write("metadata.cs", Patched.AddHours(3));
        var clientBinary = files.Write("client-GameAssembly.dll", Patched, bytes: 4096);

        Assert.True(LayoutDeclarationParser.TryParse(
            new[] { "layout v1", "dump server", "offset A = 0 : SmallElement.First" },
            out var declarations, out _));
        var header = LayoutEmitter.Emit(declarations, new LayoutGenOptions
        {
            // The client half was carried forward by a run three updates ago; only the server was
            // measured when this header was written.
            GameIdentity = "client=2026-01-01T00:00:00Z/4000 server=2026-09-01T12:00:00Z/4096",
            GameMeasured = "server",
        });
        Assert.Equal("server", LayoutEmitter.GameMeasuredOf(header));

        var recorded = DumpFreshness.ParseHeaderValue(LayoutEmitter.GameIdentityOf(header));
        var freshness = DumpFreshness.Of(
            new Dictionary<string, string> { ["client"] = dump },
            new Dictionary<string, string> { ["client"] = clientBinary },
            recorded,
            recordedIsBinding: true);

        // The carried client record does not match the binary on this machine, and the check says
        // so rather than shrugging because nobody here measured it.
        Assert.True(freshness.Failed);
        Assert.Equal(new[] { "client" }, freshness.FailedSides);

        // ... and the provenance the run reports is about THIS run, not about the header's line.
        Assert.Equal(
            "measured this run: client; carried from the header: server",
            freshness.ProvenanceSummary(LayoutEmitter.GameIdentityOf(header)));
    }

    // ---- a check says "compared" only about a side it was handed a binary for (FLAY7, R58/10) ---

    /// <summary>The recorded server identity, as a file this test can actually create.</summary>
    private static readonly DateTime RecordedServerWrite = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_check_that_names_a_binary_calls_that_side_compared_and_the_other_side_recorded()
    {
        // Case 1 of 3: named and matching. The server binary on this machine is the one the
        // header records, so that side really was compared; the client was not looked at, and the
        // line must not pretend otherwise just because the header carries a client identity.
        using var files = new FakeGameFiles();
        var dump = files.Write("metadata.cs", Patched.AddHours(3));
        var server = files.Write("GameAssembly.dll", RecordedServerWrite, bytes: 4001);

        var freshness = JudgeBothSides(
            dump, new Dictionary<string, string> { ["server"] = server }, check: true);

        Assert.False(freshness.Failed);
        Assert.Equal("game identity compared", freshness.CheckVerb());
        Assert.Equal(
            "client=2026-01-01T00:00:00Z/4000 "
            + "(recorded, not compared: no --game-assembly for this side) "
            + "server=2026-01-01T00:00:00Z/4001 (compared)",
            freshness.DescribeChecked(BothRecorded));

        // The summary counts and the per-side marks are two renderings of one fact, so they are
        // asserted against each other rather than only against a literal.
        Assert.Equal(
            "compared this run: server; recorded but not compared: client",
            freshness.CheckProvenanceSummary(BothRecorded));
        AssertMarksAndSummaryAgree(freshness, BothRecorded);
    }

    [Fact]
    public void A_check_whose_named_binary_disagrees_still_calls_that_side_compared()
    {
        // Case 2 of 3: named and mismatched. "Compared" is about whether the comparison happened,
        // not about how it came out — a failing side is the one side nobody can doubt was looked
        // at, and demoting it to "not compared" would be the same lie in the other direction.
        using var files = new FakeGameFiles();
        var dump = files.Write("metadata.cs", Patched.AddHours(3));
        var server = files.Write("GameAssembly.dll", Patched, bytes: 4096);

        var freshness = JudgeBothSides(
            dump, new Dictionary<string, string> { ["server"] = server }, check: true);

        Assert.True(freshness.Failed);
        Assert.Equal(new[] { "server" }, freshness.FailedSides);
        Assert.Equal("game identity compared", freshness.CheckVerb());
        Assert.Contains("server=2026-01-01T00:00:00Z/4001 (compared)",
            freshness.DescribeChecked(BothRecorded));
        Assert.Equal(
            "compared this run: server; recorded but not compared: client",
            freshness.CheckProvenanceSummary(BothRecorded));
        AssertMarksAndSummaryAgree(freshness, BothRecorded);
    }

    [Fact]
    public void A_check_given_no_game_assembly_at_all_says_recorded_rather_than_compared()
    {
        // Case 3 of 3, and R58 finding 10 itself: this is the invocation the repository's own
        // recipe runs and the one every other test here drives, and it used to print
        // "game identity compared: client=… (carried) server=… (carried)" one line above
        // "measured this run: none". Two identities announced as compared, nothing stat'd. The
        // per-side mark is dropped too: every side would carry the same negative, and the verb
        // has already said it once for the whole line.
        using var files = new FakeGameFiles();
        var dump = files.Write("metadata.cs", Patched.AddHours(3));

        var freshness = JudgeBothSides(dump, new Dictionary<string, string>(), check: true);

        Assert.Empty(freshness.Measured);
        Assert.Equal(
            "game identity recorded (not compared: no --game-assembly given)",
            freshness.CheckVerb());
        Assert.Equal(BothRecorded, freshness.DescribeChecked(BothRecorded));
        Assert.DoesNotContain("compared)", freshness.DescribeChecked(BothRecorded));
        Assert.Equal(
            "compared this run: none; recorded but not compared: client, server",
            freshness.CheckProvenanceSummary(BothRecorded));
        AssertMarksAndSummaryAgree(freshness, BothRecorded);

        // The generate wording is untouched: a run that WRITES an identity it did not measure is
        // carrying it forward, which is a different and still-honest sentence.
        Assert.Equal(
            "measured this run: none; carried from the header: client, server",
            freshness.ProvenanceSummary(BothRecorded));
    }

    [Fact]
    public void A_check_told_about_a_binary_it_never_used_does_not_tell_the_operator_to_pass_one()
    {
        // The fourth case, and the trap in the wording: a run CAN be handed --game-assembly and
        // still compare nothing — here the client is named but no client dump was loaded, so the
        // side is never judged. Saying "no --game-assembly for this side" there sends an operator
        // to add the argument they already gave. The reason clause distinguishes the two.
        using var files = new FakeGameFiles();
        var dump = files.Write("metadata.cs", Patched.AddHours(3));
        var client = files.Write("client-GameAssembly.dll", RecordedServerWrite, bytes: 4000);

        var freshness = DumpFreshness.Of(
            new Dictionary<string, string> { ["server"] = dump },
            new Dictionary<string, string> { ["client"] = client },
            DumpFreshness.ParseHeaderValue(BothRecorded),
            recordedIsBinding: true);

        Assert.Empty(freshness.Measured);
        Assert.Equal(new[] { "client" }, freshness.NamedBinaries);
        Assert.Equal(
            "game identity recorded (not compared: no side's binary was checked)",
            freshness.CheckVerb());
        Assert.Equal(DumpFreshness.NotCheckedMark, freshness.MarkFor("client"));
        Assert.Equal(DumpFreshness.NotComparedMark, freshness.MarkFor("server"));
        Assert.Equal(
            "client=2026-01-01T00:00:00Z/4000 "
            + "(recorded, not compared: this side's binary was not checked) "
            + "server=2026-01-01T00:00:00Z/4001 "
            + "(recorded, not compared: no --game-assembly for this side)",
            freshness.DescribeChecked(BothRecorded));
        AssertMarksAndSummaryAgree(freshness, BothRecorded);
    }

    /// <summary>
    /// The two check lines are one fact rendered twice, so a wording change that moves one and not
    /// the other is a defect even when both literals above still pass. Counts the
    /// <c>(compared)</c> marks in the identity line and compares them with the sides the summary
    /// names as compared.
    /// </summary>
    private static void AssertMarksAndSummaryAgree(DumpFreshness freshness, string headerValue)
    {
        var summary = freshness.CheckProvenanceSummary(headerValue);
        int semicolon = summary.IndexOf(';');
        var comparedClause = summary.Substring(0, semicolon)
            .Replace("compared this run: ", "");
        int named = comparedClause == "none"
            ? 0
            : comparedClause.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;

        var described = freshness.DescribeChecked(headerValue);
        int marks = 0;
        for (int i = described.IndexOf(DumpFreshness.ComparedMark, StringComparison.Ordinal);
             i >= 0;
             i = described.IndexOf(DumpFreshness.ComparedMark, i + 1, StringComparison.Ordinal))
        {
            // NotComparedMark ends with ComparedMark's text only if the two are ever made to
            // overlap; they do not, and this asserts it stays that way.
            marks++;
        }

        Assert.Equal(named, marks);
        Assert.Equal(freshness.Measured.Count, named);
    }
}
