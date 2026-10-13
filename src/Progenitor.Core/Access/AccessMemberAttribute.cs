using System;

namespace Progenitor.Access;

/// <summary>
/// One declared access-layer member, stamped into the generated assembly by
/// <c>tools/accessgen</c>.
///
/// **Why an attribute and not a side file.** A generated accessor resolves its member by *name*
/// at first use, so the member never appears in the assembly's TypeRef/MemberRef tables — the
/// Resilience metadata scan (`Resilience/MetadataScan.cs`) would see nothing and still report
/// "all bind". Assembly-level attributes put the declarations in the assembly's own metadata,
/// where the same `PEReader` pass that reads the MemberRef tables can read them: no game process,
/// no `Assembly.Load`, no extra file to keep beside the DLL, and the declarations travel with the
/// binary they describe.
///
/// <see cref="Signature"/> is <c>*</c> when the declaration named no overload, which the scan
/// reads as "any overload of this name counts".
/// </summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class AccessMemberAttribute : Attribute
{
    public AccessMemberAttribute(string assembly, string type, string name, string kind, string signature)
    {
        Assembly = assembly ?? "";
        Type = type ?? "";
        Name = name ?? "";
        Kind = kind ?? "";
        Signature = signature ?? "";
    }

    /// <summary>Simple game assembly name, e.g. <c>ProjectM</c>.</summary>
    public string Assembly { get; }

    /// <summary>Full type name, nested types joined with <c>/</c>.</summary>
    public string Type { get; }

    /// <summary>Member name; <c>""</c> for a type declaration.</summary>
    public string Name { get; }

    /// <summary><c>Type</c>, <c>Field</c> or <c>Method</c>.</summary>
    public string Kind { get; }

    /// <summary>Overload discriminator, or <c>*</c> for "any overload".</summary>
    public string Signature { get; }

    /// <summary>The wildcard <see cref="Signature"/>: match a member by name alone.</summary>
    public const string AnySignature = "*";
}
