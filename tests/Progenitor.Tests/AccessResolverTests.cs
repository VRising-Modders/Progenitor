using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Progenitor.Access;
using Progenitor.AccessGen;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FACC2 — the two decisions <c>AccessResolver</c> used to make by guessing, driven directly
/// against a fake reflection surface (R39 findings 2 and 3, and the review's note that nothing
/// below the text layer was tested at all).
///
/// <see cref="AccessNaming"/> is the game-free half of the resolver, so it links into this project
/// the way the parser and the emitter do; what stays in <c>AccessResolver</c> is the Il2CppInterop
/// calls that fetch the numbers this file decides on.
/// </summary>
public class AccessNamingTests
{
    // A stand-in for one interop stub type. The base class carries a same-named method, and the
    // method-info statics are declared in the opposite order from the overloads, so a resolver
    // that counted positions in GetMethods()/GetFields() would hand back the wrong pointer here —
    // which is exactly what R39 finding 2 describes.
    public class FakeBase
    {
        public virtual void Run(string text) { }
    }

    public sealed class FakeInterop : FakeBase
    {
        public void Run(int count) { }

        public void Run(float scale, int count) { }

        internal static IntPtr NativeMethodInfoPtr_Run_Public_Void_Single_Int32_0 = IntPtr.Zero;
        internal static IntPtr NativeMethodInfoPtr_Run_Public_Void_Int32_0 = IntPtr.Zero;
        internal static IntPtr NativeMethodInfoPtr_Run_Public_Virtual_New_Void_String_0 = IntPtr.Zero;
        internal static IntPtr NativeFieldInfoPtr_somethingElse = IntPtr.Zero;
    }

    private static List<string> FakeFieldNames() =>
        typeof(FakeInterop)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.Name)
            .ToList();

    private static MethodBase Overload(params Type[] parameters) =>
        typeof(FakeInterop).GetMethod("Run", parameters)!;

    [Fact]
    public void Each_overload_resolves_to_the_static_named_for_its_own_signature()
    {
        Assert.Equal(
            "NativeMethodInfoPtr_Run_Public_Void_Int32_0",
            AccessNaming.FieldNameFor(Overload(typeof(int)), FakeFieldNames(), "Fake.Type"));

        Assert.Equal(
            "NativeMethodInfoPtr_Run_Public_Void_Single_Int32_0",
            AccessNaming.FieldNameFor(Overload(typeof(float), typeof(int)), FakeFieldNames(), "Fake.Type"));

        // The inherited overload is the one an ordinal mapping shifted everything by: the base
        // declares it, and the derived type's private statics are not inherited for reflection.
        Assert.Equal(
            "NativeMethodInfoPtr_Run_Public_Virtual_New_Void_String_0",
            AccessNaming.FieldNameFor(Overload(typeof(string)), FakeFieldNames(), "Fake.Type"));
    }

    [Fact]
    public void The_answer_does_not_depend_on_the_order_the_statics_are_offered_in()
    {
        var forward = FakeFieldNames();
        var reverse = Enumerable.Reverse(forward).ToList();
        Assert.Equal(
            AccessNaming.FieldNameFor(Overload(typeof(int)), forward, "Fake.Type"),
            AccessNaming.FieldNameFor(Overload(typeof(int)), reverse, "Fake.Type"));
    }

    [Fact]
    public void A_signature_with_no_matching_static_is_a_refusal_not_another_overloads_pointer()
    {
        var thrown = Assert.Throws<AccessException>(() => AccessNaming.FieldNameFor(
            Overload(typeof(int)),
            new[] { "NativeMethodInfoPtr_Run_Public_Void_Single_Int32_0" },
            "ProjectM.DeathUtilities"));

        Assert.StartsWith("Access: ProjectM.DeathUtilities.Run ", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("none is named for the declared overload (Void_Int32)", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_statics_carrying_one_signature_are_refused_rather_than_picked_between()
    {
        var thrown = Assert.Throws<AccessException>(() => AccessNaming.FieldNameFor(
            Overload(typeof(int)),
            new[]
            {
                "NativeMethodInfoPtr_Run_Public_Void_Int32_0",
                "NativeMethodInfoPtr_Run_Private_Void_Int32_1",
            },
            "ProjectM.DeathUtilities"));

        Assert.Contains("matches 2 method-info pointer fields", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("will not pick one", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_unmangled_spelling_is_the_interops_own()
    {
        // Il2CppInterop.Generator, TypeReferenceEx.GetUnmangledName: by-ref and pointer take a
        // prefix, a generic instance appends each argument, and the backtick becomes '_'.
        Assert.Equal("Int32", AccessNaming.Unmangled(typeof(int)));
        Assert.Equal("byref_Int32", AccessNaming.Unmangled(typeof(int).MakeByRefType()));
        Assert.Equal("ptr_Byte", AccessNaming.Unmangled(typeof(byte).MakePointerType()));
        Assert.Equal("List_1_Int32", AccessNaming.Unmangled(typeof(List<int>)));
        Assert.Equal("Dictionary_2_String_Int32", AccessNaming.Unmangled(typeof(Dictionary<string, int>)));

        // Which is what produces the names on a real interop assembly. Read off
        // BepInEx/interop/ProjectM.Shared.dll (build 248598344), the two same-named statics that
        // an ordinal mapping cannot tell apart and this one can:
        //   NativeMethodInfoPtr_CalculateArea_Public_Static_Single_byref_NativeArray_1_float2_0
        //   NativeMethodInfoPtr_CalculateArea_Public_Static_Single_byref_NativeArray_1_float2_byref_NativeArray_1_int3_0
        Assert.Equal("Void_Single_Int32", AccessNaming.SignatureTail(Overload(typeof(float), typeof(int))));

        // The interop's trailing dedup index and its _PDM marker are not part of the signature.
        Assert.Equal(
            "NativeMethodInfoPtr_Run_Public_Void_Int32_PDM_3",
            AccessNaming.FieldNameFor(
                Overload(typeof(int)),
                new[] { "NativeMethodInfoPtr_Run_Public_Void_Int32_PDM_3" },
                "Fake.Type"));
    }

    [Fact]
    public void A_width_that_disagrees_with_the_field_is_refused_with_both_numbers()
    {
        var thrown = Assert.Throws<AccessException>(
            () => AccessNaming.RequireWidth(8, 4, "ProjectM.SomeType", "someFlags"));
        Assert.Contains("is 8 bytes on this build", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("accessor's type is 4 bytes", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("correct the access.txt type", thrown.Message, StringComparison.Ordinal);

        // The mirror direction is the worse one: a 4-byte T written into an 8-byte field makes
        // the runtime read four bytes of adjacent stack into the game's object.
        Assert.Throws<AccessException>(() => AccessNaming.RequireWidth(4, 8, "ProjectM.SomeType", "someFlags"));

        // Agreement passes, and so does "the runtime would not say" — the check never invents a
        // refusal out of a size it could not read.
        AccessNaming.RequireWidth(8, 8, "ProjectM.SomeType", "someFlags");
        AccessNaming.RequireWidth(0, 4, "ProjectM.SomeType", "someFlags");
    }

    [Fact]
    public void A_reference_typed_field_gets_the_pointer_pair_and_no_unmanaged_accessor()
    {
        Assert.True(AccessDeclarationParser.TryParse(
            new[]
            {
                "access v1", "assembly ProjectM.Shared",
                "field ProjectM.GameDataSystem RandomLocalizationKeys : ProjectM.RandomLocalizationKeys ref",
            },
            out var declarations, out var errors), string.Join("; ", errors));

        var declared = Assert.Single(declarations);
        Assert.True(declared.IsReference);
        Assert.Equal("ProjectM.RandomLocalizationKeys", declared.FieldType);
        Assert.Contains(" ref", declared.Canonical(), StringComparison.Ordinal);

        var text = AccessGenerator.Emit(declarations, new AccessGenOptions
        {
            DeclarationsPath = "samples/Progenitor.Sample/Access/access.txt",
            BuildId = "test-build",
        });

        Assert.Contains("public static global::System.IntPtr RandomLocalizationKeysObject(", text, StringComparison.Ordinal);
        Assert.Contains("public static uint RandomLocalizationKeysOffset()", text, StringComparison.Ordinal);
        // A field holding an object pointer has no blittable reading, so the generic pair — and
        // the non-generic typed pair, which could not compile against `unmanaged` anyway — is not
        // emitted at all.
        Assert.DoesNotContain("where T : unmanaged", text, StringComparison.Ordinal);
        Assert.DoesNotContain("global::ProjectM.RandomLocalizationKeys ", text, StringComparison.Ordinal);
    }

    // R40 finding 6. One overload's tail is a suffix of the other's: `Single_List_1_Single` ends
    // `Single_Int32_Single_List_1_Single`, exactly the shape the review found on the real pair
    //   NativeMethodInfoPtr_CalculateArea_..._Single_byref_NativeArray_1_float2_0
    //   NativeMethodInfoPtr_CalculateArea_..._Single_Int32_Single_byref_NativeArray_1_float2_0
    // A suffix match counts two candidates for the shorter overload, or — when the shorter one's
    // own static is absent on this build — silently hands back the longer one's pointer.
    public sealed class FakeSuffixInterop
    {
        public float Calc(List<float> points) => 0f;

        public float Calc(int steps, float scale, List<float> points) => 0f;

        internal static IntPtr NativeMethodInfoPtr_Calc_Public_Single_List_1_Single_0 = IntPtr.Zero;
        internal static IntPtr NativeMethodInfoPtr_Calc_Public_Single_Int32_Single_List_1_Single_0 = IntPtr.Zero;
    }

    private static List<string> SuffixFieldNames() =>
        typeof(FakeSuffixInterop)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Select(f => f.Name)
            .ToList();

    private static MethodBase SuffixOverload(params Type[] parameters) =>
        typeof(FakeSuffixInterop).GetMethod("Calc", parameters)!;

    [Fact]
    public void An_overload_whose_tail_is_a_suffix_of_another_still_resolves_to_its_own_static()
    {
        Assert.Equal("Single_List_1_Single",
            AccessNaming.SignatureTail(SuffixOverload(typeof(List<float>))));
        Assert.Equal("Single_Int32_Single_List_1_Single",
            AccessNaming.SignatureTail(SuffixOverload(typeof(int), typeof(float), typeof(List<float>))));

        // The short tail IS a segment-suffix of the long one, so the anchor cannot be "the last N
        // segments match" alone — the head before them has to be visibility/modifier words.
        Assert.Equal(
            "NativeMethodInfoPtr_Calc_Public_Single_List_1_Single_0",
            AccessNaming.FieldNameFor(SuffixOverload(typeof(List<float>)), SuffixFieldNames(), "Fake.Type"));
        Assert.Equal(
            "NativeMethodInfoPtr_Calc_Public_Single_Int32_Single_List_1_Single_0",
            AccessNaming.FieldNameFor(
                SuffixOverload(typeof(int), typeof(float), typeof(List<float>)),
                SuffixFieldNames(),
                "Fake.Type"));
    }

    [Fact]
    public void The_longer_overloads_static_is_never_handed_back_for_the_shorter_declaration()
    {
        // The silent arm of R40 finding 6: the shorter overload's own static is missing, so an
        // EndsWith matcher finds exactly one candidate — the wrong function — and says nothing.
        var thrown = Assert.Throws<AccessException>(() => AccessNaming.FieldNameFor(
            SuffixOverload(typeof(List<float>)),
            new[] { "NativeMethodInfoPtr_Calc_Public_Single_Int32_Single_List_1_Single_0" },
            "ProjectM.AreaMath"));

        Assert.Contains("none is named for the declared overload (Single_List_1_Single)",
            thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_return_type_that_merely_starts_with_a_modifier_word_is_still_one_segment()
    {
        // NativeMethodInfoPtr_ReadStaticTransformIndex_Public_Static_StaticTransformIndex_byref_NetBufferIn_0
        // on ProjectM.Shared.dll (build 248598344): "StaticTransformIndex" is the return type and
        // "Static" is the modifier, and splitting on '_' keeps them apart.
        Assert.True(AccessNaming.TailMatches("Public_Static_StaticTransformIndex_byref_NetBufferIn",
            "StaticTransformIndex_byref_NetBufferIn"));
        Assert.False(AccessNaming.TailMatches("Public_Static_StaticTransformIndex_byref_NetBufferIn",
            "TransformIndex_byref_NetBufferIn"));

        // A head word the interop's grammar does not have means the name is not what this rule
        // describes, so it does not match — a refusal, never a guess.
        Assert.False(AccessNaming.TailMatches("Public_Mystery_Void_Int32", "Void_Int32"));
        Assert.True(AccessNaming.TailMatches("Public_Abstract_Virtual_New_Void_Int32", "Void_Int32"));
    }

    [Fact]
    public void A_reference_typed_field_refuses_every_unmanaged_accessor_whatever_its_width_says()
    {
        // R40 finding 3. The width is a true number about the wrong question: a pointer slot is 8
        // bytes, so RequireWidth passes sizeof(long) and would let SetValue<long> write an
        // arbitrary integer into a slot the IL2CPP GC dereferences as an object.
        AccessNaming.RequireWidth(IntPtr.Size, sizeof(long), "ProjectM.X", "handle");

        var thrown = Assert.Throws<AccessException>(() =>
            AccessNaming.RequireValueTyped(classified: true, isReference: true, "ProjectM.X", "handle"));
        Assert.Contains("ProjectM.X.handle is reference-typed on this build", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("'ref'", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("handleObject", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("handleOffset", thrown.Message, StringComparison.Ordinal);

        // The mirror: the raw object-pointer pair on a value-typed field would write a pointer
        // over the value.
        var mirror = Assert.Throws<AccessException>(() =>
            AccessNaming.RequireReferenceTyped(classified: true, isReference: false, "ProjectM.X", "position"));
        Assert.Contains("is value-typed on this build", mirror.Message, StringComparison.Ordinal);
        Assert.Contains("Drop the trailing 'ref'", mirror.Message, StringComparison.Ordinal);

        // Each passes on its own side.
        AccessNaming.RequireValueTyped(classified: true, isReference: false, "ProjectM.X", "position");
        AccessNaming.RequireReferenceTyped(classified: true, isReference: true, "ProjectM.X", "handle");
    }

    [Fact]
    public void An_unclassified_field_refuses_every_accessor_and_names_the_step_that_failed()
    {
        // R41 finding 1. Both gates used to return silently on !classified, so the single answer
        // "the runtime would not say" opened the classification AND the width check together —
        // fail-open on exactly the condition the classification gate was added for.
        var blittable = Assert.Throws<AccessException>(() => AccessNaming.RequireValueTyped(
            classified: false, isReference: false, "ProjectM.X", "handle",
            "il2cpp_class_from_type returned 0"));
        Assert.Contains("ProjectM.X.handle could not be classified", blittable.Message, StringComparison.Ordinal);
        Assert.Contains("il2cpp_class_from_type returned 0", blittable.Message, StringComparison.Ordinal);
        Assert.Contains("refused rather than guessed", blittable.Message, StringComparison.Ordinal);

        // The pointer pair too: an unclassified field is not "probably a reference" either.
        var pointerPair = Assert.Throws<AccessException>(() => AccessNaming.RequireReferenceTyped(
            classified: false, isReference: true, "ProjectM.X", "handle",
            "il2cpp_field_get_type returned 0"));
        Assert.Contains("could not be classified", pointerPair.Message, StringComparison.Ordinal);
        Assert.Contains("il2cpp_field_get_type returned 0", pointerPair.Message, StringComparison.Ordinal);

        // isReference is meaningless when nothing classified it, so neither value of it is a way
        // through: all four combinations refuse.
        Assert.Throws<AccessException>(() =>
            AccessNaming.RequireValueTyped(classified: false, isReference: true, "ProjectM.X", "handle"));
        Assert.Throws<AccessException>(() =>
            AccessNaming.RequireReferenceTyped(classified: false, isReference: false, "ProjectM.X", "handle"));

        // The width's own unknown is still permissive — that asymmetry is the point, not an
        // oversight: an unreadable size is a missing number, an unreadable classification is a
        // GC-visible write.
        AccessNaming.RequireWidth(0, 4, "ProjectM.X", "handle");
    }

    [Fact]
    public void A_field_without_ref_gets_no_object_pointer_pair_at_all()
    {
        Assert.True(AccessDeclarationParser.TryParse(
            new[] { "access v1", "assembly ProjectM", "field ProjectM.Foo tick : System.Single" },
            out var declarations, out var errors), string.Join("; ", errors));

        var text = AccessGenerator.Emit(declarations, new AccessGenOptions { BuildId = "test-build" });

        Assert.Contains("public static T tick<T>(", text, StringComparison.Ordinal);
        Assert.Contains("public static uint tickOffset()", text, StringComparison.Ordinal);
        // R40 finding 3: the …Object pair used to be emitted for every instance field, which put
        // il2cpp_field_set_value_object on a float3 one keystroke away.
        Assert.DoesNotContain("tickObject(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SetObject(", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GetObject(", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_line_without_its_type_is_a_named_refusal()
    {
        Assert.False(AccessDeclarationParser.TryParse(
            new[] { "access v1", "assembly ProjectM", "field ProjectM.SomeType someFlags" },
            out _, out var errors));
        Assert.Contains("line 3", errors[0], StringComparison.Ordinal);
        Assert.Contains("needs a ': <Type>'", errors[0], StringComparison.Ordinal);
        Assert.Contains("width", errors[0], StringComparison.Ordinal);
    }
}

/// <summary>
/// Job FACC4 — R41 finding 1: the two paths through <c>AccessResolver.Shape</c> where the runtime
/// does not answer. Both used to leave the shape at its permissive initial value, and both gates
/// then returned silently, so a <c>SetValue&lt;long&gt;</c> on a reference-typed field reached
/// <c>il2cpp_field_set_value</c> — the exact write R40 finding 3 was about. Neither had a test,
/// because the branches lived beside the Il2CppInterop calls; <see cref="AccessFieldShape.Classify"/>
/// takes those four answers as delegates so they are drivable here without a game.
/// </summary>
public class AccessFieldShapeTests
{
    private static readonly IntPtr SomeType = new(0x1000);
    private static readonly IntPtr SomeClass = new(0x2000);

    private static AccessFieldShape Classify(
        Func<IntPtr>? fieldType = null,
        Func<IntPtr, IntPtr>? classFromType = null,
        Func<IntPtr, bool>? isValueType = null,
        Func<IntPtr, int>? valueSize = null) =>
        AccessFieldShape.Classify(
            fieldType ?? (() => SomeType),
            classFromType ?? (_ => SomeClass),
            isValueType ?? (_ => true),
            valueSize ?? (_ => 4),
            pointerSize: 8);

    [Fact]
    public void A_zero_from_either_pointer_call_is_an_unclassified_shape_naming_that_call()
    {
        var noType = Classify(fieldType: () => IntPtr.Zero);
        Assert.False(noType.Classified);
        Assert.Equal(0, noType.Width);
        Assert.False(noType.IsReference);
        Assert.Equal("il2cpp_field_get_type returned 0", noType.UnclassifiedStep);

        // The R41 scenario proper: a generic parameter or a pointer type, for which
        // il2cpp_class_from_type returns zero on a build where the declaration still resolves.
        var noClass = Classify(classFromType: _ => IntPtr.Zero);
        Assert.False(noClass.Classified);
        Assert.Equal("il2cpp_class_from_type returned 0", noClass.UnclassifiedStep);

        // And the refusal follows from the shape, so nothing between here and the accessor can
        // re-open it.
        var thrown = Assert.Throws<AccessException>(() => AccessNaming.RequireValueTyped(
            noClass.Classified, noClass.IsReference, "ProjectM.X", "handle", noClass.UnclassifiedStep));
        Assert.Contains("il2cpp_class_from_type returned 0", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_throw_from_any_step_is_unclassified_and_the_message_names_the_step_and_the_exception()
    {
        var thrower = Classify(isValueType: _ => throw new InvalidOperationException("boom"));
        Assert.False(thrower.Classified);
        Assert.Equal(0, thrower.Width);
        Assert.Equal("il2cpp_class_is_valuetype threw InvalidOperationException", thrower.UnclassifiedStep);

        // The step is the one that threw, not the last one that succeeded.
        Assert.Equal(
            "il2cpp_class_value_size threw AccessViolationException",
            Classify(valueSize: _ => throw new AccessViolationException()).UnclassifiedStep);
        Assert.Equal(
            "il2cpp_field_get_type threw NullReferenceException",
            Classify(fieldType: () => throw new NullReferenceException()).UnclassifiedStep);

        // An AccessException is the member's own "not on this build" sentence and is not turned
        // into an unclassified shape — a different failure with a different fix.
        Assert.Throws<AccessException>(() => Classify(fieldType: () => throw new AccessException("Access: gone")));
    }

    [Fact]
    public void A_classified_field_still_answers_both_questions()
    {
        var value = Classify(isValueType: _ => true, valueSize: _ => 12);
        Assert.True(value.Classified);
        Assert.False(value.IsReference);
        Assert.Equal(12, value.Width);
        Assert.Equal("", value.UnclassifiedStep);

        // A reference field is pointerSize wide — the honest number that makes the width check
        // alone useless here (R40 finding 3).
        var reference = Classify(isValueType: _ => false);
        Assert.True(reference.Classified);
        Assert.True(reference.IsReference);
        Assert.Equal(8, reference.Width);

        // A negative size never becomes a negative width.
        Assert.Equal(0, Classify(valueSize: _ => -1).Width);
    }
}

/// <summary>
/// A stand-in for one interop stub type, shaped the way Il2CppInterop shapes them: an IL2CPP
/// instance field becomes a C# property. <see cref="AccessRefVerificationTests"/> reads THIS
/// assembly's own metadata with the generator's <see cref="InteropMetadataShapes"/>, so the
/// value/reference decode is exercised against a real PE file rather than a fake.
/// </summary>
public sealed class InteropShapeFixture
{
    /// <summary>Reference: a class.</summary>
    public string Handle { get; set; } = "";

    /// <summary>Value: a primitive.</summary>
    public float Tick { get; set; }

    /// <summary>Reference: a generic instance whose definition is a class.</summary>
    public List<int> Points { get; set; } = new();

    /// <summary>Value: a generic instance whose definition is a struct.</summary>
    public KeyValuePair<int, int> Pair { get; set; }

    /// <summary>Value: a struct, reached as a plain field rather than a property.</summary>
    public DateTime Stamp;
}

/// <summary>
/// Job FACC3 — R40 finding 3's other half: <c>ref</c> was an unchecked claim by the declaration's
/// author, and the generator has the interop assemblies in front of it, so it checks.
/// </summary>
public class AccessRefVerificationTests
{
    private sealed class FakeShapes : IInteropFieldShapes
    {
        private readonly Dictionary<string, InteropFieldShape> _shapes = new(StringComparer.Ordinal);

        public FakeShapes Says(string member, InteropFieldShape shape)
        {
            _shapes[member] = shape;
            return this;
        }

        public InteropFieldShape ShapeOf(AccessDeclaration declaration, out string detail)
        {
            detail = "the fake says so";
            return _shapes.TryGetValue(declaration.Name, out var shape) ? shape : InteropFieldShape.Unknown;
        }
    }

    private static List<AccessDeclaration> Parse(params string[] lines)
    {
        Assert.True(AccessDeclarationParser.TryParse(lines, out var declarations, out var errors),
            string.Join("; ", errors));
        return declarations;
    }

    [Fact]
    public void A_reference_field_declared_without_ref_is_refused_at_generation_time()
    {
        var declarations = Parse(
            "access v1", "assembly ProjectM", "field ProjectM.X handle : ProjectM.SomeClass");

        Assert.False(AccessGenerator.VerifyReferenceModifiers(
            declarations,
            new FakeShapes().Says("handle", InteropFieldShape.Reference),
            out var errors, out _));

        var error = Assert.Single(errors);
        Assert.Contains("ProjectM.X.handle is reference-typed", error, StringComparison.Ordinal);
        Assert.Contains("field ProjectM.X handle : ProjectM.SomeClass ref", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_field_declared_with_ref_is_refused_the_same_way()
    {
        Assert.False(AccessGenerator.VerifyReferenceModifiers(
            Parse("access v1", "assembly ProjectM", "field ProjectM.X spin : Unity.Mathematics.float3 ref"),
            new FakeShapes().Says("spin", InteropFieldShape.Value),
            out var errors, out _));

        Assert.Contains("is value-typed", Assert.Single(errors), StringComparison.Ordinal);
    }

    [Fact]
    public void Agreement_passes_and_an_unanswerable_field_is_reported_but_not_fatal()
    {
        var declarations = Parse(
            "access v1", "assembly ProjectM",
            "field ProjectM.X handle : ProjectM.SomeClass ref",
            "field ProjectM.X tick : System.Single",
            "field ProjectM.X mystery : ProjectM.Whatever");

        Assert.True(AccessGenerator.VerifyReferenceModifiers(
            declarations,
            new FakeShapes().Says("handle", InteropFieldShape.Reference).Says("tick", InteropFieldShape.Value),
            out var errors, out var unverified));

        Assert.Empty(errors);
        // A field the interop emitted no member for is what the runtime resolves through
        // il2cpp_class_get_field_from_name and classifies there; refusing to generate for it on a
        // machine with no game installed would be a worse rule than the one being enforced.
        Assert.Contains("ProjectM.X.mystery", Assert.Single(unverified), StringComparison.Ordinal);
        Assert.True(AccessGenerator.VerifyReferenceModifiers(declarations, null, out errors, out _));
        Assert.Empty(errors);
    }

    [Fact]
    public void The_shapes_are_read_from_real_metadata_with_no_assembly_load()
    {
        using var shapes = new InteropMetadataShapes(AppContext.BaseDirectory);

        Assert.Equal(InteropFieldShape.Reference, ShapeOf(shapes, "Handle"));
        Assert.Equal(InteropFieldShape.Reference, ShapeOf(shapes, "Points"));
        Assert.Equal(InteropFieldShape.Value, ShapeOf(shapes, "Tick"));
        Assert.Equal(InteropFieldShape.Value, ShapeOf(shapes, "Pair"));
        Assert.Equal(InteropFieldShape.Value, ShapeOf(shapes, "Stamp"));
        Assert.Equal(InteropFieldShape.Unknown, ShapeOf(shapes, "NoSuchMember"));

        // And the whole rule over the same real metadata: the one declaration that lies is the
        // one that is refused.
        Assert.False(AccessGenerator.VerifyReferenceModifiers(
            Parse(
                "access v1", "assembly Progenitor.Tests",
                "field Progenitor.Tests.InteropShapeFixture Handle : System.String ref",
                "field Progenitor.Tests.InteropShapeFixture Tick : System.Single",
                "field Progenitor.Tests.InteropShapeFixture Points : System.Object"),
            shapes, out var errors, out _));

        Assert.Contains("Points is reference-typed", Assert.Single(errors), StringComparison.Ordinal);
    }

    private static InteropFieldShape ShapeOf(IInteropFieldShapes shapes, string member) =>
        shapes.ShapeOf(
            new AccessDeclaration(
                AccessDeclarationKind.Field,
                "Progenitor.Tests",
                "Progenitor.Tests.InteropShapeFixture",
                member,
                fieldType: "System.Object"),
            out _);
}

/// <summary>
/// Job FACC4 — <see cref="InteropMetadataShapes"/> against a <em>real</em> PE file that this test
/// compiles, named and shaped the way BepInEx's generated <c>ProjectM.Shared.dll</c> is.
///
/// R41's test note: the only thing exercising the 237 lines of <c>PEReader</c> metadata walking
/// was <c>InteropShapeFixture</c>, which reads the <em>test assembly's</em> own metadata — C# the
/// interop generator never produced, in an assembly whose name no declaration can name. Nothing
/// covered the folder lookup by assembly simple name, a nested type, a missing assembly, or a
/// generic parameter reached through a real signature blob. Roslyn is already a test-and-tool-only
/// dependency here, so the fixture is compiled and written to a temp folder and read back off
/// disk exactly as the generator reads the real one.
/// </summary>
public class InteropMetadataShapesTests : IDisposable
{
    private const string InteropSource = @"
namespace ProjectM
{
    public class RandomLocalizationKeys { }

    public struct Float3 { public float X; public float Y; public float Z; }

    // Shaped like an interop stub: the IL2CPP instance field is a C# property with the
    // NativeFieldInfoPtr_<name> static beside it.
    public class GameDataSystem
    {
        internal static System.IntPtr NativeFieldInfoPtr_RandomLocalizationKeys;
        public RandomLocalizationKeys RandomLocalizationKeys { get; set; }
        public Float3 Spin { get; set; }
        public string Name { get; set; }
        public int Count { get; set; }
        public System.Collections.Generic.List<int> Points { get; set; }
        public System.Collections.Generic.KeyValuePair<int, int> Pair { get; set; }
        public int[] Levels { get; set; }
        public System.DateTime Stamp;

        public class Nested { public Float3 Spin { get; set; } }
    }

    public class Box<T> { public T Item { get; set; } }
}";

    private readonly string _directory;
    private readonly InteropMetadataShapes _shapes;

    public InteropMetadataShapesTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "progenitor-interop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        var references = new List<Microsoft.CodeAnalysis.MetadataReference>();
        foreach (var path in ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                     .Split(Path.PathSeparator))
        {
            if (path.Length > 0 && File.Exists(path))
                references.Add(Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));
        }

        // The simple name IS the lookup key: the generator opens "<assembly>.dll" in the interop
        // folder, so the file has to be named for the assembly a declaration line names.
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "ProjectM.Shared",
            new[] { Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(InteropSource) },
            references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        using (var file = File.Create(Path.Combine(_directory, "ProjectM.Shared.dll")))
        {
            var emit = compilation.Emit(file);
            Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        }

        _shapes = new InteropMetadataShapes(_directory);
    }

    public void Dispose()
    {
        _shapes.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private InteropFieldShape ShapeOf(string type, string member, out string detail) =>
        _shapes.ShapeOf(
            new AccessDeclaration(AccessDeclarationKind.Field, "ProjectM.Shared", type, member,
                fieldType: "System.Object"),
            out detail);

    private InteropFieldShape ShapeOf(string member) => ShapeOf("ProjectM.GameDataSystem", member, out _);

    [Fact]
    public void A_real_interop_assembly_is_found_by_simple_name_and_every_property_shape_decoded()
    {
        Assert.Equal(InteropFieldShape.Reference, ShapeOf("RandomLocalizationKeys"));
        Assert.Equal(InteropFieldShape.Reference, ShapeOf("Name"));
        Assert.Equal(InteropFieldShape.Reference, ShapeOf("Points"));
        Assert.Equal(InteropFieldShape.Reference, ShapeOf("Levels"));

        Assert.Equal(InteropFieldShape.Value, ShapeOf("Spin"));
        Assert.Equal(InteropFieldShape.Value, ShapeOf("Count"));
        Assert.Equal(InteropFieldShape.Value, ShapeOf("Pair"));

        // A plain field, not a property — the second lookup, and the one an interop emits for a
        // member its generator modeled differently.
        ShapeOf("ProjectM.GameDataSystem", "Stamp", out var stampDetail);
        Assert.Equal(InteropFieldShape.Value, ShapeOf("Stamp"));
        Assert.Contains("as a field", stampDetail, StringComparison.Ordinal);

        ShapeOf("ProjectM.GameDataSystem", "RandomLocalizationKeys", out var propertyDetail);
        Assert.Contains("as a property", propertyDetail, StringComparison.Ordinal);
        Assert.Contains("reference-typed", propertyDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nested_type_answers_under_the_metadata_spelling_and_only_that_one()
    {
        Assert.Equal(InteropFieldShape.Value, ShapeOf("ProjectM.GameDataSystem/Nested", "Spin", out _));

        // The C# spelling is not the metadata spelling, and a lookup that silently fell back to it
        // would answer for the wrong type.
        Assert.Equal(InteropFieldShape.Unknown,
            ShapeOf("ProjectM.GameDataSystem.Nested", "Spin", out var detail));
        Assert.Contains("no type ProjectM.GameDataSystem.Nested", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_metadata_cannot_answer_is_Unknown_with_the_reason_and_never_a_guess()
    {
        // A generic parameter: the signature blob says VAR, which is neither a class nor a struct
        // until it is instantiated. This is the R41 finding 1 scenario's generation-time twin.
        Assert.Equal(InteropFieldShape.Unknown, ShapeOf("ProjectM.Box`1", "Item", out var generic));
        Assert.Contains("generic parameter", generic, StringComparison.Ordinal);

        Assert.Equal(InteropFieldShape.Unknown, ShapeOf("ProjectM.GameDataSystem", "NoSuchMember", out var missing));
        Assert.Contains("has no member named NoSuchMember", missing, StringComparison.Ordinal);

        Assert.Equal(InteropFieldShape.Unknown, ShapeOf("ProjectM.NoSuchType", "Spin", out var noType));
        Assert.Contains("no type ProjectM.NoSuchType", noType, StringComparison.Ordinal);

        // A declaration naming an assembly this folder does not have.
        var elsewhere = _shapes.ShapeOf(
            new AccessDeclaration(AccessDeclarationKind.Field, "ProjectM.Missing",
                "ProjectM.GameDataSystem", "Spin", fieldType: "System.Object"),
            out var noAssembly);
        Assert.Equal(InteropFieldShape.Unknown, elsewhere);
        Assert.Contains("no ProjectM.Missing.dll in ", noAssembly, StringComparison.Ordinal);

        // And a method declaration is not a field question at all.
        Assert.Equal(InteropFieldShape.Unknown, _shapes.ShapeOf(
            new AccessDeclaration(AccessDeclarationKind.Method, "ProjectM.Shared",
                "ProjectM.GameDataSystem", "Spin"),
            out var notAField));
        Assert.Contains("not a field declaration", notAField, StringComparison.Ordinal);
    }

    [Fact]
    public void The_whole_rule_runs_against_the_real_file_and_refuses_the_line_that_lies()
    {
        Assert.True(AccessDeclarationParser.TryParse(
            new[]
            {
                "access v1", "assembly ProjectM.Shared",
                "field ProjectM.GameDataSystem RandomLocalizationKeys : ProjectM.RandomLocalizationKeys",
                "field ProjectM.GameDataSystem Spin : ProjectM.Float3",
                "field ProjectM.GameDataSystem NoSuchMember : System.Single",
            },
            out var declarations, out var parseErrors), string.Join("; ", parseErrors));

        Assert.False(AccessGenerator.VerifyReferenceModifiers(
            declarations, _shapes, out var errors, out var unverified));

        var error = Assert.Single(errors);
        Assert.Contains("RandomLocalizationKeys is reference-typed", error, StringComparison.Ordinal);
        // Spin agrees and is silent; the member that is not there is reported, not fatal — and it
        // is what flips the generated header to verified: false.
        Assert.Contains("NoSuchMember", Assert.Single(unverified), StringComparison.Ordinal);
    }
}

/// <summary>
/// Job FACC2 — <c>accessgen --check</c>: what it compares, and that CI compares it. R39 finding 5
/// is that the gate compared the build header, so it failed on a game build bump rather than on a
/// declaration change, and no CI ran it at all.
/// </summary>
public class AccessCheckTests
{
    private static readonly string[] OneField =
    {
        "access v1", "assembly ProjectM", "field ProjectM.Foo bar : System.Single",
    };

    private static string EmitWith(string buildId, params string[] lines) =>
        EmitWith(buildId, verified: true, lines);

    private static string EmitWith(string buildId, bool verified, params string[] lines)
    {
        Assert.True(AccessDeclarationParser.TryParse(lines, out var declarations, out var errors),
            string.Join("; ", errors));
        return AccessGenerator.Emit(declarations, new AccessGenOptions
        {
            DeclarationsPath = "src/Progenitor.Core/Access/access.txt",
            BuildId = buildId,
            InteropVerified = verified,
        });
    }

    [Fact]
    public void An_unverified_generation_is_not_byte_identical_to_a_verified_one()
    {
        // R41 finding 3: the two used to be the same bytes, so --check's regeneration-equality
        // gate reported green on a file whose 'ref' modifiers nothing had ever checked. The state
        // is now in the artifact — in the header and in a const the runtime can print.
        var verified = EmitWith("A", verified: true, OneField);
        var unverified = EmitWith("A", verified: false, OneField);

        Assert.NotEqual(verified, unverified);
        Assert.Contains("//     verified:     true", verified, StringComparison.Ordinal);
        Assert.Contains("public const bool InteropVerified = true;", verified, StringComparison.Ordinal);
        Assert.Contains("//     verified:     false", unverified, StringComparison.Ordinal);
        Assert.Contains("--unverified", unverified, StringComparison.Ordinal);
        Assert.Contains("public const bool InteropVerified = false;", unverified, StringComparison.Ordinal);

        Assert.True(AccessGenerator.VerifiedOf(verified));
        Assert.False(AccessGenerator.VerifiedOf(unverified));
        // A file from before the header line existed claims nothing, and "no record" is not
        // "verified".
        Assert.False(AccessGenerator.VerifiedOf("// <auto-generated>\n// nothing here\n"));
        Assert.False(AccessGenerator.VerifiedOf(null));
    }

    [Fact]
    public void Check_reports_a_verification_state_that_moved_and_says_which_way()
    {
        var verdict = AccessGenerator.Check(
            EmitWith("A", verified: false, OneField),
            EmitWith("A", verified: true, OneField),
            out var detail);

        Assert.Equal(AccessGenerator.CheckVerdict.Stale, verdict);
        Assert.Contains("the declarations match", detail, StringComparison.Ordinal);
        Assert.Contains("checked-in file says verified: false", detail, StringComparison.Ordinal);
        Assert.Contains("checked the 'ref' modifiers", detail, StringComparison.Ordinal);

        // And the other direction, which is the one worth hearing about: the checked-in file was
        // verified and this run was not.
        AccessGenerator.Check(
            EmitWith("A", verified: true, OneField),
            EmitWith("A", verified: false, OneField),
            out var downgrade);
        Assert.Contains("checked-in file says verified: true", downgrade, StringComparison.Ordinal);
        Assert.Contains("--unverified", downgrade, StringComparison.Ordinal);

        // A real declaration drift is still reported as one, not as a verification story.
        AccessGenerator.Check(
            EmitWith("A", verified: false, OneField),
            EmitWith("A", verified: true, "access v1", "assembly ProjectM", "field ProjectM.Foo bar : System.Int64"),
            out var drift);
        Assert.Contains("the declarations or the emitted body differ", drift, StringComparison.Ordinal);
    }

    [Fact]
    public void A_build_stamp_that_moved_is_not_a_stale_file()
    {
        var verdict = AccessGenerator.Check(EmitWith("A", OneField), EmitWith("B", OneField), out var detail);
        Assert.Equal(AccessGenerator.CheckVerdict.BuildIdDiffers, verdict);
        Assert.Contains("only the build stamp differs", detail, StringComparison.Ordinal);
        Assert.Contains("checked-in A", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_declaration_that_moved_is_a_stale_file_even_on_one_build()
    {
        var verdict = AccessGenerator.Check(
            EmitWith("A", OneField),
            EmitWith("A", "access v1", "assembly ProjectM", "field ProjectM.Foo bar : System.Int64"),
            out var detail);
        Assert.Equal(AccessGenerator.CheckVerdict.Stale, verdict);
        Assert.Contains("differ", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Identical_input_matches_and_the_build_id_reads_back_on_its_own()
    {
        Assert.Equal(
            AccessGenerator.CheckVerdict.Matches,
            AccessGenerator.Check(EmitWith("A", OneField), EmitWith("A", OneField), out _));
        Assert.Equal("A", AccessGenerator.BuildIdOf(EmitWith("A", OneField)));
        Assert.Equal("", AccessGenerator.BuildIdOf(null));
    }

    /// <summary>
    /// The gate itself, run rather than described: regenerate from the checked-in declarations and
    /// require the checked-in file back, byte for byte. This is what makes <c>dotnet test</c> —
    /// which is all CI runs — cover <c>accessgen --check</c>.
    /// </summary>
    private static string RepoRoot()
    {
        string? dir = Path.GetFullPath(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null && !File.Exists(Path.Combine(dir, "Progenitor.sln")); i++)
            dir = Directory.GetParent(dir)?.FullName;
        Assert.True(dir != null && File.Exists(Path.Combine(dir, "Progenitor.sln")), "Progenitor.sln not found");
        return dir!;
    }

    private static void AssertRegeneratesByteIdentically(
        string declarationsRelative, string generatedRelative, string? ns = null)
    {
        var root = RepoRoot();
        var declarationsPath = Path.Combine(root, declarationsRelative.Replace('/', Path.DirectorySeparatorChar));
        var generatedPath = Path.Combine(root, generatedRelative.Replace('/', Path.DirectorySeparatorChar));
        var checkedIn = File.ReadAllText(generatedPath).Replace("\r\n", "\n");

        Assert.True(
            AccessDeclarationParser.TryParse(File.ReadAllLines(declarationsPath), out var declarations, out var errors),
            string.Join("; ", errors));

        var options = new AccessGenOptions
        {
            DeclarationsPath = declarationsRelative,
            BuildId = AccessGenerator.BuildIdOf(checkedIn),
            // Carried, not asserted: which machine generated the checked-in file is not this
            // gate's question, and CI has no interop folder to re-verify against. What IS pinned
            // is that the flag is part of the compared body — a file regenerated with the other
            // value fails this equality, which is the whole of R41 finding 3.
            InteropVerified = AccessGenerator.VerifiedOf(checkedIn),
        };
        if (ns != null) options.Namespace = ns;

        var regenerated = AccessGenerator.Emit(declarations, options);
        Assert.Equal(AccessGenerator.CheckVerdict.Matches,
            AccessGenerator.Check(checkedIn, regenerated, out _));
        Assert.Equal(checkedIn, regenerated);
    }

    [Fact]
    public void CI_runs_the_generator_on_the_checked_in_declarations_and_gets_the_checked_in_file() =>
        AssertRegeneratesByteIdentically(
            "src/Progenitor.Core/Access/access.txt",
            "src/Progenitor.Core/Access/Generated/Access.Generated.cs");

    /// <summary>
    /// The Sample ships its own declaration file and its own generated C#, which is the whole
    /// point of "a plugin does the same thing" — so the same gate covers it.
    /// </summary>
    [Fact]
    public void CI_covers_the_samples_own_declarations_the_same_way() =>
        AssertRegeneratesByteIdentically(
            "samples/Progenitor.Sample/Access/access.txt",
            "samples/Progenitor.Sample/Access/Access.Generated.cs",
            "Progenitor.Sample.Access");

    /// <summary>
    /// The interop folder, resolved the way <c>accessgen</c> and <c>Directory.Build.props</c>
    /// resolve it — the environment variables first, then the default Steam install.
    /// </summary>
    private static string InteropDirectory()
    {
        var interop = Environment.GetEnvironmentVariable("VRISING_INTEROP_DIR");
        if (!string.IsNullOrEmpty(interop)) return interop!;
        var game = Environment.GetEnvironmentVariable("VRISING_GAME_DIR");
        if (string.IsNullOrEmpty(game))
            game = @"C:\Program Files (x86)\Steam\steamapps\common\VRising";
        return Path.Combine(game!, "BepInEx", "interop");
    }

    /// <summary>
    /// R42 finding 3: the Sample's artifact was checked in as <c>verified: false</c> while its one
    /// field line carries <c>ref</c>, so the drift check <c>docs/bridge-ops.md</c> documents was red
    /// on every machine — <c>Stale</c> where the interop folder exists, exit 1 where it does not.
    /// The artifact is now the verified one, and this pins it: the checked-in bytes say
    /// <c>verified: true</c>, and on a machine that has the folder the modifiers really do agree
    /// with it, so <c>--check</c> is green rather than green-because-nobody-looked.
    /// </summary>
    [Fact]
    public void The_samples_artefact_is_the_verified_one_and_the_interop_agrees_when_it_is_here()
    {
        var root = RepoRoot();
        const string declarationsRelative = "samples/Progenitor.Sample/Access/access.txt";
        const string generatedRelative = "samples/Progenitor.Sample/Access/Access.Generated.cs";
        var generated = File.ReadAllText(
            Path.Combine(root, generatedRelative.Replace('/', Path.DirectorySeparatorChar)));

        Assert.True(AccessGenerator.VerifiedOf(generated),
            "the Sample's checked-in artifact must say verified: true — it declares a 'ref' field line");
        Assert.Contains("public const bool InteropVerified = true;", generated, StringComparison.Ordinal);

        Assert.True(AccessDeclarationParser.TryParse(
            File.ReadAllLines(Path.Combine(root, declarationsRelative.Replace('/', Path.DirectorySeparatorChar))),
            out var declarations, out var parseErrors), string.Join("; ", parseErrors));

        var interop = InteropDirectory();
        var verdict = AccessGenerator.Verify(
            declarations, interop, unverifiedOptIn: false, out _, out var errors);

        if (Directory.Exists(interop))
        {
            // This machine has the game: run the verification the documented command runs, against
            // the real interop metadata, and require the checked-in header's claim back.
            Assert.Equal(AccessGenerator.VerificationVerdict.Verified, verdict);
            Assert.Empty(errors);
        }
        else
        {
            // CI has no install. What is pinned there is the verdict the tool reaches: it REFUSES
            // (exit 1, nothing written, nothing compared) rather than emitting an unverified file
            // and then calling the checked-in verified one Stale. "No interop" is "cannot check",
            // and the tool says so instead of guessing.
            Assert.Equal(AccessGenerator.VerificationVerdict.Refused, verdict);
            Assert.Contains(errors, e => e.Contains("--unverified", StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The same refusal driven on every machine rather than only on one without a game: a field
    /// line plus a folder that is not there is exit 1, and <c>--unverified</c> is the only way past
    /// it. Nothing here can reach <c>Stale</c>, which is the verdict R42 finding 3 predicted for
    /// the old checked-in bytes.
    /// </summary>
    [Fact]
    public void Without_the_interop_folder_a_field_line_is_refused_not_generated_unverified()
    {
        Assert.True(AccessDeclarationParser.TryParse(OneField, out var declarations, out _));
        var missing = Path.Combine(Path.GetTempPath(), "progenitor-no-such-interop-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(
            AccessGenerator.VerificationVerdict.Refused,
            AccessGenerator.Verify(declarations, missing, unverifiedOptIn: false, out _, out var errors));
        Assert.Contains(errors, e => e.Contains(missing, StringComparison.Ordinal));

        Assert.Equal(
            AccessGenerator.VerificationVerdict.Refused,
            AccessGenerator.Verify(declarations, "", unverifiedOptIn: false, out _, out _));

        // The opt-out still works, and lands in the artifact as verified: false.
        Assert.Equal(
            AccessGenerator.VerificationVerdict.Unverified,
            AccessGenerator.Verify(declarations, missing, unverifiedOptIn: true, out var notices, out _));
        Assert.Contains(notices, n => n.Contains("verified: false", StringComparison.Ordinal));

        // A declaration file with no field line has no 'ref' modifier to contradict, so it needs no
        // folder at all — the rule is about fields, not about having a game installed.
        Assert.True(AccessDeclarationParser.TryParse(
            new[] { "access v1", "assembly ProjectM", "method ProjectM.Foo Bar" }, out var methodsOnly, out _));
        Assert.Equal(
            AccessGenerator.VerificationVerdict.Verified,
            AccessGenerator.Verify(methodsOnly, "", unverifiedOptIn: false, out _, out _));
    }

    /// <summary>
    /// R42 finding 3, second half: <c>Describe()</c> is emitted into every facade, so a plugin can
    /// print its own verification state. It was hand-written in Core only, which left the generated
    /// <c>InteropVerified</c> const with no reader anywhere outside Core.
    /// </summary>
    [Fact]
    public void Every_generated_facade_can_describe_its_own_verification_state()
    {
        foreach (var text in new[] { EmitWith("A", verified: true, OneField), EmitWith("A", verified: false, OneField) })
        {
            Assert.Contains("public static string Describe() =>", text, StringComparison.Ordinal);
            Assert.Contains("interop-verified=", text, StringComparison.Ordinal);
            Assert.Contains("(InteropVerified ? \"true\" : \"false\")", text, StringComparison.Ordinal);
            Assert.Contains("global::Progenitor.Access.AccessResolver.CachedCount", text, StringComparison.Ordinal);
        }

        // --no-facade is a plugin that already has its own facade: accessors and nothing else.
        Assert.True(AccessDeclarationParser.TryParse(OneField, out var declarations, out _));
        var bare = AccessGenerator.Emit(declarations, new AccessGenOptions { EmitFacade = false });
        Assert.DoesNotContain("Describe()", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("InteropVerified", bare, StringComparison.Ordinal);
    }
}

/// <summary>
/// Job FACC1 — R65 finding 7: the resolver's miss policy, at every entry.
///
/// <para>R41 finding 1 settled the policy for <c>Shape</c> and <c>AccessResolver.cs</c> stated it in
/// prose: a failure is <em>cached as that failure</em>, because the refusal must be identical on the
/// thousandth call and re-asking a native call that already threw is a per-call risk of throwing
/// again on the game's own thread. Twelve lines above that sentence, <c>FieldInfo</c> — the entry
/// where the failures actually happen, and the one <c>.access missing</c> drives — cached nothing:
/// every call re-ran <c>RunClassConstructor</c>, a reflection lookup, a native class-pointer lookup
/// and <c>il2cpp_class_get_field_from_name</c>. So did <c>MethodPointer</c> and <c>Method</c>.</para>
///
/// <para><b>Why this reads the source instead of calling the code.</b> <c>AccessResolver.cs</c> is
/// <c>unsafe</c> and binds Il2CppInterop, so it is one of the files this project deliberately cannot
/// compile (the csproj fails the build if Il2CppInterop is ever referenced here). The game-free half
/// of the layer — <see cref="AccessNaming"/>, <see cref="AccessFieldShape"/> — is linked and driven
/// directly; the memoization is not, so it is pinned against the real file's <em>syntax tree</em>
/// rather than its text: which invocation each entry routes through, and where a cache is written.
/// Roslyn is already a test-only dependency here (<see cref="InteropMetadataShapesTests"/>), and a
/// syntax pin is not satisfied by the pinned words appearing in a comment or a string.</para>
/// </summary>
public class AccessResolverMissPolicyTests
{
    /// <summary>The three positive caches plus the shape cache: every table an entry answers from.</summary>
    private static readonly string[] CacheTables = { "Fields", "FieldShapes", "MethodPointers", "MethodInfos" };

    private static readonly ClassDeclarationSyntax Resolver = LoadResolver();

    private static ClassDeclarationSyntax LoadResolver()
    {
        string? dir = Path.GetFullPath(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null && !File.Exists(Path.Combine(dir, "Progenitor.sln")); i++)
            dir = Directory.GetParent(dir)?.FullName;
        Assert.True(dir != null && File.Exists(Path.Combine(dir, "Progenitor.sln")), "Progenitor.sln not found");

        var path = Path.Combine(dir!, "src", "Progenitor.Core", "Access", "AccessResolver.cs");
        Assert.True(File.Exists(path), path + " not found");

        return Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(File.ReadAllText(path))
            .GetRoot()
            .DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == "AccessResolver");
    }

    private static MethodDeclarationSyntax Entry(string name) =>
        Assert.Single(Resolver.Members
            .OfType<MethodDeclarationSyntax>()
            .Where(m => m.Identifier.ValueText == name));

    private static string PropertyBody(string name) =>
        Assert.Single(Resolver.Members
            .OfType<PropertyDeclarationSyntax>()
            .Where(p => p.Identifier.ValueText == name))
            .ToString();

    /// <summary>Does this member call <paramref name="callee"/> (by simple name, generic or not)?</summary>
    private static bool CallsInto(Microsoft.CodeAnalysis.SyntaxNode node, string callee) =>
        node.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i => i.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText == callee,
            GenericNameSyntax g => g.Identifier.ValueText == callee,
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText == callee,
            _ => false,
        });

    /// <summary>
    /// The four entries R65 finding 7 names, each routed through the one memoization. Before the
    /// fix, <c>Shape</c> was the only one of the four that cached anything about a failure —
    /// <c>FieldInfo</c>, <c>MethodPointer</c> and <c>Method</c> hand-rolled a read-then-write of
    /// their own table with nothing on the throwing path, so this fails on three of the four.
    /// </summary>
    [Theory]
    [InlineData("FieldInfo")]
    [InlineData("Shape")]
    [InlineData("MethodPointer")]
    [InlineData("Method")]
    public void Every_resolver_entry_answers_through_the_one_memoisation(string entry)
    {
        Assert.True(
            CallsInto(Entry(entry), "Memoised"),
            entry + " must resolve through Memoized, which is what makes a refusal cost the same as a hit");
    }

    /// <summary>
    /// <c>FieldOffset</c> has no cache of its own by design: it is <c>FieldInfo</c> plus one native
    /// call, so its miss policy is <c>FieldInfo</c>'s. This is the entry <c>.access missing</c>
    /// actually calls (<c>SampleAccessDemo.HandleMissing</c>), so the two halves are pinned
    /// together — it must delegate, and the thing it delegates to must memoize.
    /// </summary>
    [Fact]
    public void FieldOffset_is_FieldInfo_plus_one_native_call_and_therefore_inherits_its_miss_policy()
    {
        var offset = Entry("FieldOffset");
        Assert.True(CallsInto(offset, "FieldInfo"), "FieldOffset must go through FieldInfo");
        Assert.True(CallsInto(offset, "il2cpp_field_get_offset"), "...and add exactly the one native call");
        Assert.DoesNotContain(CacheTables, table => offset.ToString().Contains(table + "[", StringComparison.Ordinal));

        Assert.True(CallsInto(Entry("FieldInfo"), "Memoised"));
    }

    /// <summary>
    /// The negative half, and the one that keeps the policy uniform as the file grows: no entry
    /// writes a cache table itself. A future entry that hand-rolls the old read-then-write —
    /// which is exactly how <c>FieldInfo</c>, <c>MethodPointer</c> and <c>Method</c> drifted from
    /// the rule the file states — puts its assignment somewhere other than <c>Memoised</c>, and
    /// this goes red.
    /// </summary>
    [Fact]
    public void Only_Memoised_writes_a_cache_table_or_the_miss_table()
    {
        int writes = 0;
        foreach (var assignment in Resolver.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            if (assignment.Left is not ElementAccessExpressionSyntax element) continue;
            if (element.Expression is not IdentifierNameSyntax table) continue;
            var name = table.Identifier.ValueText;
            if (name != "Misses" && !CacheTables.Contains(name, StringComparer.Ordinal)) continue;

            writes++;
            var owner = assignment.Ancestors().OfType<MethodDeclarationSyntax>().First();
            Assert.Equal("Memoised", owner.Identifier.ValueText);
        }

        // Exactly one write names a table directly, and it is the refusal — the positive write goes
        // through Memoized's `cache` parameter, which is how one helper serves four tables. A
        // Memoized that recorded only the answer would satisfy every routing pin above and leave the
        // policy exactly where R65 found it, so both writes are named below.
        Assert.Equal(1, writes);
        var memoised = Entry("Memoised").ToString();
        Assert.Contains("Misses[key] = e.Message", memoised, StringComparison.Ordinal);
        Assert.Contains("cache[key] = resolved", memoised, StringComparison.Ordinal);
    }

    /// <summary>
    /// The read side, which is what makes the refusal cheap <em>and</em> identical: a key already
    /// refused throws again from the cached sentence, out of the same lock the positive read uses,
    /// before anything native is asked. <c>.access missing</c> depends on it still throwing on
    /// every call — <see cref="SampleAccessDemoTests"/> pins the command's own side.
    /// </summary>
    [Fact]
    public void A_key_already_refused_throws_the_cached_sentence_before_any_native_work()
    {
        var memoised = Entry("Memoised");
        var body = memoised.ToString();

        Assert.Contains("Misses.TryGetValue(key, out var refused)", body, StringComparison.Ordinal);
        Assert.Contains("throw new AccessException(refused)", body, StringComparison.Ordinal);

        // The refusal is read before the resolve delegate is ever invoked.
        int read = body.IndexOf("Misses.TryGetValue", StringComparison.Ordinal);
        int resolve = body.IndexOf("resolve()", StringComparison.Ordinal);
        Assert.InRange(read, 0, resolve);

        // Only the layer's own verdict about this build is cached. A NullReferenceException out of
        // the interop is a fault of the call, not a fact about the build, and asking again is the
        // right thing — so the catch is typed, and it rethrows rather than inventing a sentence.
        var caught = Assert.Single(memoised.DescendantNodes().OfType<CatchClauseSyntax>());
        Assert.Equal("AccessException", caught.Declaration!.Type.ToString());
        Assert.Contains("throw;", caught.Block.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The cost of caching a negative is that a member which appears later in the process stays
    /// missing until something empties the table — so <c>Reset</c>, the one thing that empties the
    /// others, must empty this one too. <c>Plugin.Uninstall</c> calls it once per Core generation.
    /// </summary>
    [Fact]
    public void Reset_empties_the_miss_table_along_with_every_cache()
    {
        var reset = Entry("Reset").ToString();
        foreach (var table in CacheTables)
            Assert.Contains(table + ".Clear();", reset, StringComparison.Ordinal);
        Assert.Contains("Misses.Clear();", reset, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Describe()</c> prints this number as <c>resolved=</c>, so it counts what resolved.
    /// Folding the refusals into it would have made the facade line report a member that is on no
    /// build as resolved; they are counted separately instead.
    /// </summary>
    [Fact]
    public void The_facade_count_stays_resolved_only_and_the_refusals_are_counted_apart()
    {
        var cached = PropertyBody("CachedCount");
        Assert.DoesNotContain("Misses.Count", cached, StringComparison.Ordinal);
        Assert.Contains("Fields.Count + MethodPointers.Count + MethodInfos.Count", cached, StringComparison.Ordinal);

        Assert.Contains("Misses.Count", PropertyBody("MissCount"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Job FACC2 — R66 finding 8. <c>Shape</c> sits on top of <c>FieldInfo</c>, and it used to ask
    /// it from <em>inside</em> its own resolve lambda: the refusal for a member that is on no build
    /// then escaped through two <c>Memoised</c> frames and was written under both keys, so one
    /// missing field asked through <c>FieldWidth</c> counted as two entries in <c>MissCount</c> —
    /// the property whose documentation calls itself a refused <em>member</em> count.
    ///
    /// The pin is structural rather than textual: <c>FieldInfo</c> must be called by <c>Shape</c>
    /// and must not be reachable from the <c>Memoised</c> invocation's arguments, which is exactly
    /// the difference between "the refusal is FieldInfo's" and "the refusal is recorded twice".
    /// </summary>
    [Fact]
    public void Shape_resolves_the_field_before_it_memoises_so_one_missing_member_is_one_refusal()
    {
        var shape = Entry("Shape");
        Assert.True(CallsInto(shape, "FieldInfo"), "Shape must resolve the member through FieldInfo");

        var memoised = Assert.Single(
            shape.DescendantNodes().OfType<InvocationExpressionSyntax>(),
            i => i.Expression is IdentifierNameSyntax { Identifier.ValueText: "Memoised" }
                 || i.Expression is GenericNameSyntax { Identifier.ValueText: "Memoised" });

        Assert.False(
            CallsInto(memoised, "FieldInfo"),
            "Shape's resolve lambda must not call FieldInfo: a refusal thrown through it is written "
            + "under the |w| key as well as FieldInfo's own |f| key, and MissCount then counts one "
            + "missing member twice (R66 finding 8)");

        // And the FieldInfo call really is ahead of the memoization in the body, not merely outside
        // its argument list.
        var body = shape.ToString();
        Assert.InRange(
            body.IndexOf("FieldInfo(", StringComparison.Ordinal),
            0,
            body.IndexOf("Memoised(", StringComparison.Ordinal));
    }

    /// <summary>
    /// The shape key used to be built from <c>interopType?.FullName ?? "?"</c>, so every declared
    /// type whose interop stub was missing shared one key space — two different members named the
    /// same field on two different null types were the same cache entry. Resolving through
    /// <c>FieldInfo</c> first removes the null case entirely (it refuses before the key exists), so
    /// the fallback is gone rather than merely unused.
    /// </summary>
    [Fact]
    public void The_shape_key_has_no_shared_fallback_for_a_missing_interop_type()
    {
        var shape = Entry("Shape").ToString();
        Assert.DoesNotContain("?? \"?\"", shape, StringComparison.Ordinal);
        Assert.Contains("interopType.FullName + \"|w|\"", shape, StringComparison.Ordinal);
    }

    /// <summary>
    /// The doc comment's own claim, corrected. <c>Memoised</c> filters on <c>AccessException</c>
    /// and said that this told a fact about the build apart from a fault of the call — but
    /// <c>ResolveField</c> wraps <em>every</em> exception into an <c>AccessException</c> before it
    /// can escape (the class constructor and the native class lookup both do), so the filter sits
    /// above the wrapping and cannot make that distinction. The comment now says what is true, and
    /// this pins that it keeps saying it: a reader who trusts the old sentence would conclude that
    /// a transient fault is retried, and it is not.
    /// </summary>
    [Fact]
    public void The_memoisation_documents_that_a_wrapped_failure_is_cached_too()
    {
        var doc = Entry("Memoised").GetLeadingTrivia().ToFullString();
        Assert.Contains("ResolveField", doc, StringComparison.Ordinal);
        Assert.Contains("transient", doc, StringComparison.Ordinal);
        Assert.Contains("Reset", doc, StringComparison.Ordinal);
        Assert.DoesNotContain("left uncached", doc, StringComparison.Ordinal);

        // The two wrapping sites the correction is about are still there, so the claim being made
        // about them is a claim about this file rather than about a remembered one.
        int wraps = 0;
        foreach (var catchClause in Resolver.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            if (catchClause.Declaration?.Type.ToString() != "Exception") continue;
            if (catchClause.Block.ToString().Contains("new AccessException", StringComparison.Ordinal)) wraps++;
        }
        Assert.Equal(2, wraps);
    }
}
