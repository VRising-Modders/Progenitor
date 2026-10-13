using System;

namespace Progenitor.Access;

/// <summary>
/// Every failure sentence the access layer can produce, in one game-free place so the wording is
/// unit-testable and identical between the runtime and the generator.
/// </summary>
public static class AccessMessages
{
    /// <summary>The prefix every access-layer line carries.</summary>
    public const string Prefix = "Access: ";

    /// <summary>
    /// The one sentence a mod author sees when a declared member is not on the build that is
    /// running. It names the member and points at the two reports that know which build this is —
    /// the address library (<c>tools/addresslib</c>) and the resilience report
    /// (<c>tools/resilience-report.ps1</c>), whose member scan lists this declaration by name.
    /// </summary>
    public static string NotFound(string type, string member) =>
        Prefix + Describe(type, member)
        + " not found on this build; the address library/resilience report knows which build";

    /// <summary>The member resolved but the interop stub did not hand back a usable pointer.</summary>
    public static string Unresolved(string type, string member, string what) =>
        Prefix + Describe(type, member) + " resolved but " + what
        + "; the address library/resilience report knows which build";

    /// <summary>More than one overload matched a method declaration that named no parameter list.</summary>
    public static string Ambiguous(string type, string member, int count) =>
        Prefix + Describe(type, member) + " has " + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
        + " overloads on this build; add a parameter list to the access.txt line";

    /// <summary>
    /// The declared overload resolved, but no <c>NativeMethodInfoPtr_*</c> static on the interop
    /// type carries the signature the interop's own naming would give it. Names the derived tail
    /// so the author can compare it against the field the interop actually emitted.
    /// </summary>
    public static string OverloadUnmatched(string type, string member, string tail, int candidates) =>
        Prefix + Describe(type, member) + " has " + Number(candidates)
        + " method-info pointer field(s) on this build and none is named for the declared overload ("
        + tail + "); the address library/resilience report knows which build";

    /// <summary>
    /// More than one <c>NativeMethodInfoPtr_*</c> static matched the derived signature, which the
    /// interop's naming should make impossible — so the layer refuses instead of picking one.
    /// </summary>
    public static string OverloadAmbiguous(string type, string member, string tail, int matches) =>
        Prefix + Describe(type, member) + " matches " + Number(matches)
        + " method-info pointer fields for one declared overload (" + tail
        + "); the access layer will not pick one";

    /// <summary>
    /// A blittable field accessor was given a <c>T</c> that is not the width of the field on this
    /// build. Names both widths, because the fix is either the accessor's type argument or the
    /// <c>: &lt;Type&gt;</c> on the access.txt line.
    /// </summary>
    public static string WidthMismatch(string type, string member, int fieldBytes, int valueBytes) =>
        Prefix + Describe(type, member) + " is " + Number(fieldBytes)
        + " bytes on this build; the accessor's type is " + Number(valueBytes)
        + " bytes. Reading or writing it would run past the value; correct the access.txt type";

    /// <summary>
    /// An unmanaged accessor was asked for a field that holds an object pointer on this build. The
    /// width cannot speak to this — a reference field is <c>IntPtr.Size</c> bytes wide and a
    /// <c>long</c> passes the size check — so the refusal is on the classification, and the message
    /// names the one-word fix on the access.txt line (R40 finding 3).
    /// </summary>
    public static string ReferenceField(string type, string member) =>
        Prefix + Describe(type, member)
        + " is reference-typed on this build: it holds an object pointer, and writing an integer"
        + " into it would hand the IL2CPP GC a value it will dereference as an object. Declare it"
        + " with the trailing 'ref' modifier on the access.txt line (field " + Describe(type, "")
        + " " + (string.IsNullOrEmpty(member) ? "<name>" : member)
        + " : <Type> ref) and use the " + (string.IsNullOrEmpty(member) ? "<name>" : member)
        + "Object pointer accessors and " + (string.IsNullOrEmpty(member) ? "<name>" : member)
        + "Offset instead";

    /// <summary>
    /// The runtime would not say whether the field holds a value or an object pointer, so every
    /// accessor is refused rather than guessed (R41 finding 1).
    ///
    /// This is deliberately not the policy <see cref="WidthMismatch"/>'s zero follows. An
    /// unreadable <em>size</em> is a missing number, and inventing a refusal from it would break
    /// every field on a build where <c>il2cpp_class_value_size</c> is unavailable; the cost of
    /// letting it through is a bounded stack read or write of the wrong length. An unclassified
    /// <em>field</em> is different in kind: passing it authorizes <c>SetValue&lt;long&gt;</c> on
    /// a slot the IL2CPP GC may dereference as an object, which is a heap corruption whose crash
    /// lands somewhere else entirely. So the two unknowns are not symmetric, and this one refuses.
    /// </summary>
    /// <param name="step">
    /// The step that did not answer — <c>il2cpp_field_get_type returned 0</c>,
    /// <c>il2cpp_class_from_type returned 0</c>, or <c>&lt;step&gt; threw &lt;Exception&gt;</c> —
    /// so the log names the one call to reproduce rather than "it failed".
    /// </param>
    public static string Unclassified(string type, string member, string step) =>
        Prefix + Describe(type, member)
        + " could not be classified as value- or reference-typed on this build ("
        + (string.IsNullOrEmpty(step) ? "the runtime would not say" : step!)
        + "); every accessor is refused rather than guessed, because an unmanaged write over an"
        + " object pointer hands the IL2CPP GC an integer it will dereference. The address"
        + " library/resilience report knows which build";

    /// <summary>
    /// The mirror of <see cref="ReferenceField"/>: the raw object-pointer accessors were asked for
    /// a field that holds a value on this build, which is a <c>ref</c> the build does not support.
    /// </summary>
    public static string ValueField(string type, string member) =>
        Prefix + Describe(type, member)
        + " is value-typed on this build: it holds no object pointer, so an object-pointer read or"
        + " write would run over the value. Drop the trailing 'ref' modifier from the access.txt"
        + " line and use the typed accessors";

    private static string Number(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Describe(string type, string member)
    {
        var t = string.IsNullOrEmpty(type) ? "?" : type!.Replace('/', '.');
        return string.IsNullOrEmpty(member) ? t : t + "." + member;
    }
}

/// <summary>
/// Thrown when a declared member is missing or unusable on the build that is running. The message
/// is one of <see cref="AccessMessages"/>; there is nothing else to catch on.
/// </summary>
public sealed class AccessException : Exception
{
    public AccessException(string message) : base(message) { }

    public AccessException(string message, Exception inner) : base(message, inner) { }
}
