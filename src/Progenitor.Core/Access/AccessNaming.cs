using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace Progenitor.Access;

/// <summary>
/// The two decisions the access layer used to make by guessing, in one game-free file so both
/// have a test below the text layer (R39 findings 2 and 3).
///
/// **Which <c>NativeMethodInfoPtr_*</c> static belongs to which overload.** Il2CppInterop names
/// that field deterministically, from the method itself
/// (<c>Il2CppInterop.Generator</c>, <c>MethodRewriteContext.UnmangledNameWithSignature</c> and
/// <c>TypeReferenceEx.GetUnmangledName</c>):
///
/// <code>
/// NativeMethodInfoPtr_{name}_{access}[_Abstract][_Virtual][_Static][_Final][_New]_{return}[_{param}]*[_PDM]_{index}
/// </code>
///
/// with a by-ref parameter spelled <c>byref_&lt;element&gt;</c>, a pointer <c>ptr_&lt;element&gt;</c>,
/// and a generic instance <c>Name_1_Arg</c> (the backtick becomes an underscore and each argument
/// is appended). Real examples from <c>ProjectM.Shared.dll</c> on build 248598344:
///
/// <code>
/// NativeMethodInfoPtr_AddComponentDeferred_Public_Void_Entity_T_0
/// NativeMethodInfoPtr_AddComponentDeferred_Public_Static_Void_EntityCommandBuffer_Entity_T_0
/// NativeMethodInfoPtr_CalculateArea_Public_Static_Single_byref_NativeArray_1_float2_0
/// NativeMethodInfoPtr_CalculateArea_Public_Static_Single_byref_NativeArray_1_float2_byref_NativeArray_1_int3_0
/// NativeMethodInfoPtr_AddHit_Public_Virtual_Final_New_Boolean_RaycastHit_0
/// </code>
///
/// The return type and the full parameter list are in the name, so the tail
/// <c>{return}[_{param}]*</c> identifies one overload and nothing else — <em>provided the match is
/// anchored on a token boundary</em>. A plain <c>EndsWith("_" + tail)</c> is suffix containment,
/// and one overload's tail can be a suffix of another's: <c>Single_NativeArray_1_float2</c> is a
/// suffix of <c>Single_Int32_Single_NativeArray_1_float2</c>, so the shorter overload would count
/// two matches (loud) or, if its own static is absent on this build, resolve to the longer
/// overload's pointer (silent — R40 finding 6). So <see cref="TailMatches"/> compares the whole
/// <c>_</c>-separated segment list and additionally requires every segment <em>before</em> the tail
/// to be one of the interop's visibility/modifier words (<see cref="HeadWords"/>) — the grammar
/// above says the head is exactly <c>{access}[_Abstract][_Virtual][_Static][_Final][_New]</c> and
/// nothing else. Measured over the 13,922 <c>NativeMethodInfoPtr_*</c> names in
/// <c>ProjectM.Shared.dll</c> and <c>Il2Cppmscorlib.dll</c> (build 248598344), the head words that
/// occur are exactly <c>Public</c>, <c>Private</c>, <c>Internal</c>, <c>Protected</c>,
/// <c>Abstract</c>, <c>Virtual</c>, <c>Static</c>, <c>Final</c> and <c>New</c>; a return type that
/// merely <em>starts</em> with one of them is a separate segment and does not confuse the anchor
/// (<c>NativeMethodInfoPtr_ReadStaticTransformIndex_Public_Static_StaticTransformIndex_byref_NetBufferIn_0</c>).
/// A head this rule does not recognize is an <see cref="AccessException"/>, never a guess.
///
/// The same corpus answers two spellings that look like gaps in <see cref="Unmangled"/> and are
/// not: an interop stub never takes a CLR array (<c>char[]</c> is modeled as
/// <c>Il2CppStructArray&lt;char&gt;</c> and spelled <c>Il2CppStructArray_1_Char</c>, e.g.
/// <c>NativeMethodInfoPtr_ToCharArray_Public_Il2CppStructArray_1_Char_0</c>), and a nullable is
/// modeled as <c>Il2CppSystem.Nullable_Unboxed&lt;T&gt;</c> and spelled
/// <c>Nullable_Unboxed_1_Int32</c> — both of which the generic branch below already produces from
/// the interop's own <see cref="Type"/>.
///
/// The anchored tail is what <see cref="FieldNameFor"/> matches on. It never counts positions: neither
/// <see cref="Type.GetMethods()"/> nor <see cref="Type.GetFields()"/> promises an order, and the
/// two orders are not the same order (private statics are not inherited, same-named methods are).
/// Zero matches or more than one is an <see cref="AccessException"/> naming the tail it derived,
/// not a pointer to some other function.
///
/// **How wide the caller's <c>T</c> may be.** <see cref="RequireWidth"/> is the comparison
/// <see cref="AccessResolver"/> makes once per field, between the IL2CPP field's own size and
/// <c>sizeof(T)</c>; it is here so the sentence is unit-testable without a game.
/// </summary>
public static class AccessNaming
{
    /// <summary>The prefix Il2CppInterop gives every method-info pointer static.</summary>
    public const string MethodInfoPrefix = "NativeMethodInfoPtr_";

    /// <summary>
    /// The interop's unmangled spelling of one type, as
    /// <c>TypeReferenceEx.GetUnmangledName</c> writes it.
    /// </summary>
    public static string Unmangled(Type? type)
    {
        if (type == null) return "?";
        if (type.IsByRef) return "byref_" + Unmangled(type.GetElementType());
        if (type.IsPointer) return "ptr_" + Unmangled(type.GetElementType());
        if (type.IsGenericType && !type.IsGenericTypeDefinition)
        {
            var sb = new StringBuilder(Unmangled(type.GetGenericTypeDefinition()));
            foreach (var argument in type.GetGenericArguments())
                sb.Append('_').Append(Unmangled(argument));
            return sb.ToString();
        }
        return type.Name.Replace('`', '_');
    }

    /// <summary>
    /// The part of a generated field name that identifies one overload and no other: the return
    /// type followed by every parameter type, each unmangled. Two overloads of one method cannot
    /// share it, because they cannot share a parameter list.
    /// </summary>
    public static string SignatureTail(MethodBase method)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        var sb = new StringBuilder();
        sb.Append(Unmangled(method is MethodInfo mi ? mi.ReturnType : typeof(void)));
        foreach (var parameter in method.GetParameters())
            sb.Append('_').Append(Unmangled(parameter.ParameterType));
        return sb.ToString();
    }

    /// <summary>
    /// The generated <c>NativeMethodInfoPtr_*</c> field name for one resolved overload, picked
    /// out of <paramref name="candidateFieldNames"/> by the interop's own naming and never by
    /// position. <paramref name="candidateFieldNames"/> is every static field name on the
    /// declaring interop type (order irrelevant).
    /// </summary>
    /// <exception cref="AccessException">
    /// No candidate carries the derived tail, or more than one does — either way the layer
    /// refuses rather than handing back a pointer to a different function.
    /// </exception>
    public static string FieldNameFor(
        MethodBase method,
        IEnumerable<string> candidateFieldNames,
        string declaredType)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        var tail = SignatureTail(method);
        var prefix = MethodInfoPrefix + method.Name + "_";

        string? match = null;
        int matches = 0;
        var seen = new List<string>();
        foreach (var name in candidateFieldNames ?? Array.Empty<string>())
        {
            if (name == null || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            seen.Add(name);
            if (!TailMatches(TailOf(name, prefix.Length), tail)) continue;
            matches++;
            match ??= name;
        }

        if (matches == 1) return match!;
        if (matches == 0)
            throw new AccessException(AccessMessages.OverloadUnmatched(
                declaredType, method.Name, tail, seen.Count));
        throw new AccessException(AccessMessages.OverloadAmbiguous(
            declaredType, method.Name, tail, matches));
    }

    /// <summary>
    /// The <c>_</c>-separated words Il2CppInterop writes between a method's name and its return
    /// type, and nothing else. Measured over <c>ProjectM.Shared.dll</c> and
    /// <c>Il2Cppmscorlib.dll</c> on build 248598344 (only the first four and the five modifiers
    /// occur there); the rest are the remaining <c>MethodAttributes.MemberAccessMask</c> spellings
    /// Il2CppInterop's <c>MethodRewriteContext</c> can emit.
    /// </summary>
    private static readonly HashSet<string> HeadWords = new(StringComparer.Ordinal)
    {
        "Public", "Private", "Internal", "Protected",
        "ProtectedInternal", "PrivateProtected", "FamANDAssem", "FamORAssem",
        "Assembly", "Family", "CompilerControlled", "Unknown",
        "Abstract", "Virtual", "Static", "Final", "New",
    };

    /// <summary>
    /// Whether <paramref name="text"/> — one interop field name with its prefix, dedup index and
    /// <c>_PDM</c> marker already stripped — carries <paramref name="tail"/> as its signature, on a
    /// token boundary.
    ///
    /// The tail must occupy the last <c>N</c> <c>_</c>-separated segments <em>and</em> every
    /// segment before it must be a <see cref="HeadWords"/> visibility/modifier word. Suffix
    /// containment is not enough: <c>Public_Static_Single_Int32_Single_NativeArray_1_float2</c>
    /// ends with <c>_Single_NativeArray_1_float2</c>, and treating that as a match either counts
    /// two overloads (loud) or, when the shorter overload's own static is absent on this build,
    /// hands back the longer overload's method-info pointer with no diagnostic (R40 finding 6).
    /// </summary>
    internal static bool TailMatches(string? text, string? tail)
    {
        if (text == null || string.IsNullOrEmpty(tail)) return false;
        var textSegments = text.Split('_');
        var tailSegments = tail!.Split('_');
        int head = textSegments.Length - tailSegments.Length;
        if (head < 0) return false;
        for (int i = 0; i < tailSegments.Length; i++)
            if (!string.Equals(textSegments[head + i], tailSegments[i], StringComparison.Ordinal))
                return false;
        for (int i = 0; i < head; i++)
            if (!HeadWords.Contains(textSegments[i]))
                return false;
        return true;
    }

    /// <summary>
    /// Strips the interop's trailing dedup index (and the <c>_PDM</c> marker it appends for a
    /// method the obfuscation pass could not prove live) so what is left ends with the signature.
    /// </summary>
    private static string TailOf(string fieldName, int from)
    {
        var rest = fieldName.Substring(from);
        int end = rest.Length;
        int digits = end;
        while (digits > 0 && rest[digits - 1] >= '0' && rest[digits - 1] <= '9') digits--;
        if (digits < end && digits > 0 && rest[digits - 1] == '_') end = digits - 1;
        if (end >= 4 && string.CompareOrdinal(rest, end - 4, "_PDM", 0, 4) == 0) end -= 4;
        return rest.Substring(0, end);
    }

    /// <summary>
    /// Refuse a blittable field access whose <c>T</c> is not the width of the field on this build.
    /// <c>il2cpp_field_get_value</c> takes no size: it copies the <em>field's</em> width into the
    /// caller's buffer, so a narrower <c>T</c> is a stack write past the local and a wider one
    /// makes the runtime read adjacent stack into the game's object. Both are silent.
    /// </summary>
    /// <param name="fieldBytes">
    /// The field's own size on this build, from <c>il2cpp_field_get_type</c>. Zero or negative
    /// means the runtime would not say, and the check passes rather than inventing a refusal.
    /// </param>
    public static void RequireWidth(int fieldBytes, int valueBytes, string declaredType, string fieldName)
    {
        if (fieldBytes <= 0 || fieldBytes == valueBytes) return;
        throw new AccessException(AccessMessages.WidthMismatch(declaredType, fieldName, fieldBytes, valueBytes));
    }

    /// <summary>
    /// Refuse an unmanaged accessor on a reference-typed field, whatever its width says.
    ///
    /// R40 finding 3: <see cref="RequireWidth"/> is answered for a reference field with
    /// <c>IntPtr.Size</c>, which is the honest byte count of the pointer slot — and therefore
    /// <em>authorizes</em> <c>SetValue&lt;long&gt;</c>, which stores an arbitrary integer into a
    /// slot the IL2CPP GC will treat as an object reference and dereference on its next scan. The
    /// width is a true number about the wrong question, so the classification has to be asked
    /// separately and asked first. A reference field gets the pointer pair and the offset, and
    /// only when the declaration says <c>ref</c>.
    /// </summary>
    /// <param name="classified">
    /// Whether the runtime said which it is (<c>il2cpp_class_is_valuetype</c> on the field's own
    /// class). False is a <em>refusal</em>, not a pass (R41 finding 1): it is the one condition
    /// this gate was added for, so opening on it would make the guarantee true only where the
    /// check already succeeded. This is deliberately not the policy <see cref="RequireWidth"/>
    /// follows for an unreadable size — an unchecked width is a bounded stack read or write of the
    /// wrong length, an unchecked classification is an integer written into a GC-visible slot.
    /// </param>
    /// <param name="unclassifiedStep">
    /// Which step did not answer, for the message. Ignored when <paramref name="classified"/>.
    /// </param>
    public static void RequireValueTyped(
        bool classified, bool isReference, string declaredType, string fieldName, string unclassifiedStep = "")
    {
        if (!classified)
            throw new AccessException(AccessMessages.Unclassified(declaredType, fieldName, unclassifiedStep));
        if (!isReference) return;
        throw new AccessException(AccessMessages.ReferenceField(declaredType, fieldName));
    }

    /// <summary>
    /// The mirror: refuse the raw object-pointer accessors on a value-typed field.
    /// <c>il2cpp_field_set_value_object</c> on a <c>float3</c> writes a pointer over the struct,
    /// and <c>il2cpp_field_get_value_object</c> boxes it into a pointer the caller will treat as
    /// an object. A <c>ref</c> declaration on a value-typed field is the mistake this names.
    ///
    /// An unclassified field is refused here too, for the same reason and with the same message as
    /// <see cref="RequireValueTyped"/>: the pointer pair over a value the runtime would not
    /// classify writes a pointer over the value just as surely.
    /// </summary>
    public static void RequireReferenceTyped(
        bool classified, bool isReference, string declaredType, string fieldName, string unclassifiedStep = "")
    {
        if (!classified)
            throw new AccessException(AccessMessages.Unclassified(declaredType, fieldName, unclassifiedStep));
        if (isReference) return;
        throw new AccessException(AccessMessages.ValueField(declaredType, fieldName));
    }
}
