using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Progenitor.Resilience;

/// <summary>What a <see cref="MemberBinding"/> row is: a type, a method or a field.</summary>
public enum MemberKind
{
    Type = 0,
    Method = 1,
    Field = 2,
}

/// <summary>Whether the reference still resolves in the current interop assemblies.</summary>
public enum BindingStatus
{
    /// <summary>Type exists, member exists, signature matches. Core will bind.</summary>
    Bound = 0,

    /// <summary>The declaring type is not in that assembly any more (renamed, moved or removed).</summary>
    TypeMissing = 1,

    /// <summary>The type is there; nothing with that member name is.</summary>
    MemberMissing = 2,

    /// <summary>The name is there, but no overload has the signature Core references.</summary>
    SignatureChanged = 3,

    /// <summary>
    /// The reference could not be checked: the assembly is not on disk, its metadata is
    /// unreadable, the signature blob did not decode, or a TypeSpec parent did not reduce to a
    /// type reference. An ordinary generic instantiation is **not** in this bucket — it is
    /// resolved against the generic definition (see <see cref="MetadataScan"/>).
    /// </summary>
    NotChecked = 4,
}

/// <summary>
/// One member of a game assembly that <c>Progenitor.dll</c> references, plus whether it still
/// resolves against the interop assemblies on disk. Produced by <see cref="MetadataScan"/> from
/// metadata alone — no game process, no assembly load, no <c>Type.GetType</c>.
///
/// <see cref="Signature"/> is what Core asks for (decoded from Core's MemberRef blob).
/// <see cref="Found"/> is what the interop assembly actually offers when the two disagree, so a
/// <see cref="BindingStatus.SignatureChanged"/> row can be read without a native analysis.
///
/// The list of these rows is "will Core compile-and-bind against this build". A row that flips
/// from <see cref="BindingStatus.Bound"/> to anything else after a game update is a call site
/// that will throw at runtime (IL2CPP interop resolves lazily) or fail to rebuild.
/// </summary>
public sealed class MemberBinding
{
    public MemberBinding(
        string assembly,
        string type,
        string name,
        MemberKind kind,
        string signature,
        BindingStatus status,
        string found = "")
    {
        Assembly = assembly ?? "";
        Type = type ?? "";
        Name = name ?? "";
        Kind = kind;
        Signature = signature ?? "";
        Status = status;
        Found = found ?? "";
    }

    /// <summary>Simple assembly name from Core's AssemblyRef, e.g. <c>ProjectM</c>.</summary>
    public string Assembly { get; }

    /// <summary>Full type name, nested types joined with <c>/</c>.</summary>
    public string Type { get; }

    /// <summary>Member name; <c>""</c> for a <see cref="MemberKind.Type"/> row.</summary>
    public string Name { get; }

    public MemberKind Kind { get; }

    /// <summary>Decoded signature Core references, e.g. <c>System.Void(ProjectM.Foo, System.Int32)</c>.</summary>
    public string Signature { get; }

    public BindingStatus Status { get; }

    /// <summary>Signatures the current assembly actually has for this name, when they disagree.</summary>
    public string Found { get; }

    /// <summary>Stable identity across runs; the diff is keyed on this.</summary>
    public string Key => Assembly + "|" + Type + "|" + Name + "|" + Signature;

    /// <summary>One line for the report: <c>ProjectM!Foo.Bar(System.Int32) -> System.Void</c>.</summary>
    public string Describe()
    {
        if (Kind == MemberKind.Type) return Assembly + "!" + Type;
        return Assembly + "!" + Type + "." + Name + (Signature.Length == 0 ? "" : " " + Signature);
    }

    public JsonObject ToJson() => new()
    {
        ["assembly"] = Assembly,
        ["type"] = Type,
        ["name"] = Name,
        ["kind"] = Kind.ToString(),
        ["signature"] = Signature,
        ["status"] = Status.ToString(),
        ["found"] = Found,
    };

    public static MemberBinding FromJson(JsonObject obj) => new(
        JsonRead.String(obj, "assembly"),
        JsonRead.String(obj, "type"),
        JsonRead.String(obj, "name"),
        ParseKind(JsonRead.String(obj, "kind")),
        JsonRead.String(obj, "signature"),
        ParseStatus(JsonRead.String(obj, "status")),
        JsonRead.String(obj, "found"));

    private static MemberKind ParseKind(string s) =>
        Enum.TryParse<MemberKind>(s, ignoreCase: true, out var k) ? k : MemberKind.Method;

    private static BindingStatus ParseStatus(string s) =>
        Enum.TryParse<BindingStatus>(s, ignoreCase: true, out var v) ? v : BindingStatus.NotChecked;

    /// <summary>Sort key so a stored list and a fresh list are comparable line by line.</summary>
    public static int Compare(MemberBinding a, MemberBinding b) =>
        string.CompareOrdinal(a?.Key ?? "", b?.Key ?? "");

    public static void Sort(List<MemberBinding> list) => list?.Sort(Compare);
}
