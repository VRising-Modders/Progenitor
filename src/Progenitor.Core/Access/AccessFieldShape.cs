using System;

namespace Progenitor.Access;

/// <summary>
/// What the runtime says about one field: how wide it is, and — the part the width cannot speak to
/// — whether it holds a value or an object pointer.
///
/// The decision lives here, apart from the four <c>il2cpp_*</c> calls that answer it, for one
/// reason: R41 finding 1 was a fail-open path through exactly these branches (a zero from
/// <c>il2cpp_class_from_type</c>, or any throw), and neither branch had a test, because the only
/// code that could reach them sat in the half of the access layer that references Il2CppInterop
/// and therefore cannot be compiled into the test project. <see cref="Classify"/> takes the four
/// answers as delegates, so the zero and the throw are both drivable from a game-free test, and
/// <c>AccessResolver</c> is left holding nothing but the native calls themselves.
/// </summary>
public readonly struct AccessFieldShape
{
    public AccessFieldShape(int width, bool isReference, bool classified, string unclassifiedStep)
    {
        Width = width < 0 ? 0 : width;
        IsReference = isReference;
        Classified = classified;
        UnclassifiedStep = classified ? "" : (unclassifiedStep ?? "");
    }

    /// <summary>Bytes <c>il2cpp_field_get_value</c> will copy; 0 when the runtime would not say.</summary>
    public int Width { get; }

    /// <summary>The field holds an object pointer. Only meaningful when <see cref="Classified"/>.</summary>
    public bool IsReference { get; }

    /// <summary>The runtime answered <c>il2cpp_class_is_valuetype</c> for this field.</summary>
    public bool Classified { get; }

    /// <summary>
    /// When <see cref="Classified"/> is false, the step that did not answer — named in the refusal
    /// so the log says which call to reproduce rather than "it failed". <c>""</c> otherwise.
    /// </summary>
    public string UnclassifiedStep { get; }

    /// <summary>
    /// Ask the runtime, through the four calls, and fail closed on anything it will not answer.
    ///
    /// <paramref name="pointerSize"/> is the honest width of a reference field's slot — and
    /// exactly why the width must never be the only gate: <c>sizeof(long)</c> is 8 too, so a width
    /// check alone <em>authorizes</em> writing an arbitrary integer into a GC-visible slot (R40
    /// finding 3).
    ///
    /// Every failure path here produces <c>Classified == false</c> with the step named, and
    /// <see cref="AccessNaming.RequireValueTyped"/> / <see cref="AccessNaming.RequireReferenceTyped"/>
    /// refuse on it. The width's own unknown stays a permissive zero, which is the right call for a
    /// missing number; a missing classification is not a number (R41 finding 1).
    /// </summary>
    public static AccessFieldShape Classify(
        Func<IntPtr> fieldType,
        Func<IntPtr, IntPtr> classFromType,
        Func<IntPtr, bool> isValueType,
        Func<IntPtr, int> valueSize,
        int pointerSize)
    {
        // Which call is being made right now, so a throw names the step rather than the outcome.
        string step = "il2cpp_field_get_type";
        try
        {
            var type = fieldType();
            if (type == IntPtr.Zero)
                return Unclassified("il2cpp_field_get_type returned 0");

            step = "il2cpp_class_from_type";
            var klass = classFromType(type);
            if (klass == IntPtr.Zero)
                return Unclassified("il2cpp_class_from_type returned 0");

            step = "il2cpp_class_is_valuetype";
            if (!isValueType(klass))
                return new AccessFieldShape(pointerSize, isReference: true, classified: true, "");

            step = "il2cpp_class_value_size";
            return new AccessFieldShape(valueSize(klass), isReference: false, classified: true, "");
        }
        catch (AccessException)
        {
            // The member is not on this build at all. That is a different sentence, already
            // written by whoever resolved the FieldInfo, and it is not cached as a shape.
            throw;
        }
        catch (Exception e)
        {
            return Unclassified(step + " threw " + e.GetType().Name);
        }
    }

    private static AccessFieldShape Unclassified(string step) =>
        new AccessFieldShape(0, isReference: false, classified: false, step);
}
