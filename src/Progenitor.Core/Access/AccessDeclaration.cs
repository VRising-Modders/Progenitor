using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Progenitor.Access;

/// <summary>What one declaration line names.</summary>
public enum AccessDeclarationKind
{
    /// <summary>A type. No accessor members; a resilience row and a compile-time existence check.</summary>
    Type = 0,

    /// <summary>An instance or static field. Emits typed get/set accessors.</summary>
    Field = 1,

    /// <summary>A method. Emits the interop MethodInfo pointer and the interop stub's MethodBase.</summary>
    Method = 2,
}

/// <summary>
/// One line of an <c>access.txt</c> declaration file, parsed.
///
/// The file is a plain text list of members to reach (see <c>docs/modules.md</c> "Access").
/// The grammar below is ours, and what we emit
/// is plain C# rather than a bytecode transform.
///
/// This type is game-free on purpose — <c>tools/accessgen</c>, <c>Resilience.MetadataScan</c> and
/// the unit tests all read declarations on a machine that never launched V Rising.
/// </summary>
public sealed class AccessDeclaration
{
    public AccessDeclaration(
        AccessDeclarationKind kind,
        string assembly,
        string type,
        string name,
        string signature = "",
        bool isStatic = false,
        string fieldType = "",
        bool isReference = false)
    {
        Kind = kind;
        Assembly = assembly ?? "";
        Type = type ?? "";
        Name = name ?? "";
        Signature = signature ?? "";
        IsStatic = isStatic;
        FieldType = fieldType ?? "";
        IsReference = isReference;
    }

    public AccessDeclarationKind Kind { get; }

    /// <summary>Simple assembly name, from the enclosing <c>assembly</c> directive.</summary>
    public string Assembly { get; }

    /// <summary>Full type name; nested types are joined with <c>/</c>, the metadata spelling.</summary>
    public string Type { get; }

    /// <summary>Member name; <c>""</c> for a <see cref="AccessDeclarationKind.Type"/> row.</summary>
    public string Name { get; }

    /// <summary>
    /// Overload discriminator for a method, as written between the parentheses and normalized
    /// (single spaces after commas), or <c>""</c> when the line named no parameter list.
    /// </summary>
    public string Signature { get; }

    /// <summary>A field declared with the trailing <c>static</c> modifier.</summary>
    public bool IsStatic { get; }

    /// <summary>
    /// The field's type, from the required <c>: Type</c> annotation. The generator emits a
    /// non-generic accessor pair from it beside the generic one, and it is the declaration the
    /// runtime width check reads back to the author when <c>T</c> is the wrong size. <c>""</c>
    /// only on a non-field row.
    /// </summary>
    public string FieldType { get; }

    /// <summary>
    /// A field declared with the trailing <c>ref</c> modifier: <see cref="FieldType"/> is a
    /// reference type, so the field holds an object pointer. Such a field has no blittable
    /// reading at all — the generic <c>T : unmanaged</c> pair would be the worst case of the
    /// width hazard, not a convenience — so the generator emits only the raw-pointer pair
    /// (<c>&lt;name&gt;Object</c>) and the offset diagnostic for it.
    /// </summary>
    public bool IsReference { get; }

    /// <summary>The last identifier of <see cref="Type"/> — the generated nested class name.</summary>
    public string SimpleTypeName
    {
        get
        {
            int slash = Type.LastIndexOf('/');
            var tail = slash >= 0 ? Type.Substring(slash + 1) : Type;
            int dot = tail.LastIndexOf('.');
            return dot >= 0 ? tail.Substring(dot + 1) : tail;
        }
    }

    /// <summary>
    /// C# spelling of <see cref="Type"/>: nested separators become <c>.</c> so the emitter can
    /// write <c>global::ProjectM.Outer.Nested</c>.
    /// </summary>
    public string CSharpTypeName => Type.Replace('/', '.');

    /// <summary>
    /// Canonical one-line form. Sorting and hashing use this, so a reordered or re-commented
    /// declaration file produces byte-identical generated output.
    /// </summary>
    public string Canonical() => Kind switch
    {
        AccessDeclarationKind.Type => "type " + Assembly + " " + Type,
        AccessDeclarationKind.Field => "field " + Assembly + " " + Type + " " + Name
            + (FieldType.Length == 0 ? "" : " : " + FieldType)
            + (IsReference ? " ref" : "")
            + (IsStatic ? " static" : ""),
        _ => "method " + Assembly + " " + Type + " " + Name
            + (Signature.Length == 0 ? "" : "(" + Signature + ")"),
    };

    /// <summary>Sort key so generation is order-independent.</summary>
    public static int Compare(AccessDeclaration a, AccessDeclaration b) =>
        string.CompareOrdinal(a?.Canonical() ?? "", b?.Canonical() ?? "");
}

/// <summary>
/// Reads an <c>access.txt</c> declaration file.
///
/// <code>
/// # comments run to end of line; blank lines are ignored
/// access v1                      # required first directive
/// assembly ProjectM              # sets the assembly for the lines that follow
/// type   ProjectM.Internal.Thing
/// field  ProjectM.SomeType someField : System.Single      # ': Type' is required
/// field  ProjectM.SomeType someCounter : System.Int64 static
/// field  ProjectM.SomeType someThing : ProjectM.Thing ref  # a reference-typed field
/// method ProjectM.SomeType SomeMethod
/// method ProjectM.SomeType SomeMethod(System.Int32, System.Single)
/// </code>
///
/// Every diagnostic carries the 1-based line number. Parsing never throws and never partially
/// succeeds silently: a file with any error yields <c>false</c> and the full error list.
/// </summary>
public static class AccessDeclarationParser
{
    /// <summary>The only format version this parser accepts.</summary>
    public const string Version = "v1";

    public static bool TryParse(
        IEnumerable<string>? lines,
        out List<AccessDeclaration> declarations,
        out List<string> errors)
    {
        declarations = new List<AccessDeclaration>();
        errors = new List<string>();
        if (lines == null)
        {
            errors.Add("line 0: no declaration text");
            return false;
        }

        bool sawHeader = false;
        string assembly = "";
        int lineNumber = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in lines)
        {
            lineNumber++;
            var line = StripComment(raw ?? "");
            if (line.Length == 0) continue;

            var parts = Split(line);
            var keyword = parts[0];

            if (!sawHeader)
            {
                if (!string.Equals(keyword, "access", StringComparison.Ordinal))
                {
                    errors.Add("line " + lineNumber + ": the first directive must be 'access " + Version + "'");
                    return false;
                }
                if (parts.Count != 2 || !string.Equals(parts[1], Version, StringComparison.Ordinal))
                {
                    errors.Add("line " + lineNumber + ": unsupported declaration format '" + line
                        + "'; this build reads 'access " + Version + "'");
                    return false;
                }
                sawHeader = true;
                continue;
            }

            switch (keyword)
            {
                case "access":
                    errors.Add("line " + lineNumber + ": a second 'access' header");
                    continue;

                case "assembly":
                    if (parts.Count != 2 || !IsIdentifierPath(parts[1]))
                    {
                        errors.Add("line " + lineNumber + ": 'assembly' takes one simple assembly name");
                        continue;
                    }
                    assembly = parts[1];
                    continue;

                case "type":
                case "field":
                case "method":
                    break;

                default:
                    errors.Add("line " + lineNumber + ": unknown directive '" + keyword + "'");
                    continue;
            }

            if (assembly.Length == 0)
            {
                errors.Add("line " + lineNumber + ": no 'assembly' directive is in effect");
                continue;
            }

            if (!TryParseMember(keyword, parts, assembly, lineNumber, errors, out var declaration))
                continue;

            var canonical = declaration!.Canonical();
            if (!seen.Add(canonical))
            {
                errors.Add("line " + lineNumber + ": duplicate declaration '" + canonical + "'");
                continue;
            }
            declarations.Add(declaration);
        }

        if (!sawHeader)
        {
            errors.Add("line 0: empty declaration file; expected 'access " + Version + "'");
            return false;
        }

        declarations.Sort(AccessDeclaration.Compare);
        return errors.Count == 0;
    }

    private static bool TryParseMember(
        string keyword,
        List<string> parts,
        string assembly,
        int lineNumber,
        List<string> errors,
        out AccessDeclaration? declaration)
    {
        declaration = null;

        if (keyword == "type")
        {
            if (parts.Count != 2 || !IsTypeName(parts[1]))
            {
                errors.Add("line " + lineNumber + ": 'type' takes one fully qualified type name");
                return false;
            }
            declaration = new AccessDeclaration(AccessDeclarationKind.Type, assembly, parts[1], "");
            return true;
        }

        if (parts.Count < 3 || !IsTypeName(parts[1]))
        {
            errors.Add("line " + lineNumber + ": '" + keyword + "' takes a type and a member name");
            return false;
        }
        var type = parts[1];

        if (keyword == "method")
        {
            var rest = string.Join(" ", parts.GetRange(2, parts.Count - 2));
            string name = rest;
            string signature = "";
            int open = rest.IndexOf('(');
            if (open >= 0)
            {
                if (!rest.EndsWith(")", StringComparison.Ordinal))
                {
                    errors.Add("line " + lineNumber + ": unbalanced parameter list in '" + rest + "'");
                    return false;
                }
                name = rest.Substring(0, open).Trim();
                signature = NormaliseSignature(rest.Substring(open + 1, rest.Length - open - 2));
            }
            if (!IsIdentifier(name))
            {
                errors.Add("line " + lineNumber + ": '" + name + "' is not a method name");
                return false;
            }
            declaration = new AccessDeclaration(AccessDeclarationKind.Method, assembly, type, name, signature);
            return true;
        }

        // field
        var fieldName = parts[2];
        if (!IsIdentifier(fieldName))
        {
            errors.Add("line " + lineNumber + ": '" + fieldName + "' is not a field name");
            return false;
        }
        bool isStatic = false;
        bool isReference = false;
        string fieldType = "";
        for (int i = 3; i < parts.Count; i++)
        {
            if (string.Equals(parts[i], "static", StringComparison.Ordinal))
            {
                isStatic = true;
                continue;
            }
            if (string.Equals(parts[i], "ref", StringComparison.Ordinal))
            {
                isReference = true;
                continue;
            }
            if (string.Equals(parts[i], ":", StringComparison.Ordinal) && i + 1 < parts.Count)
            {
                fieldType = parts[++i];
                if (!IsTypeName(fieldType))
                {
                    errors.Add("line " + lineNumber + ": '" + fieldType + "' is not a type name");
                    return false;
                }
                continue;
            }
            errors.Add("line " + lineNumber + ": unexpected token '" + parts[i] + "'");
            return false;
        }
        // R39 finding 3: the field's type is the only thing that makes the generated accessor's
        // width checkable, so it is required rather than decorative.
        if (fieldType.Length == 0)
        {
            errors.Add("line " + lineNumber + ": field '" + fieldName
                + "' needs a ': <Type>' — the access layer checks the accessor's width against it");
            return false;
        }
        declaration = new AccessDeclaration(
            AccessDeclarationKind.Field, assembly, type, fieldName, "", isStatic, fieldType, isReference);
        return true;
    }

    /// <summary>
    /// sha256 of the canonical declarations, lowercase hex. The generated header carries it so a
    /// generated file and the declaration file it came from can be compared without regenerating.
    /// Independent of comments, ordering and whitespace by construction.
    /// </summary>
    public static string Hash(IReadOnlyList<AccessDeclaration>? declarations)
    {
        var sb = new StringBuilder();
        if (declarations != null)
        {
            var copy = new List<AccessDeclaration>(declarations);
            copy.Sort(AccessDeclaration.Compare);
            for (int i = 0; i < copy.Count; i++) sb.Append(copy[i].Canonical()).Append('\n');
        }
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        var hex = new StringBuilder(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++)
            hex.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        return hex.ToString();
    }

    private static string StripComment(string line)
    {
        int hash = line.IndexOf('#');
        if (hash >= 0) line = line.Substring(0, hash);
        return line.Trim();
    }

    private static List<string> Split(string line)
    {
        var parts = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            int start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i])) i++;
            if (i > start) parts.Add(line.Substring(start, i - start));
        }
        return parts;
    }

    private static string NormaliseSignature(string inner)
    {
        var pieces = inner.Split(',');
        var kept = new List<string>(pieces.Length);
        for (int i = 0; i < pieces.Length; i++)
        {
            var piece = pieces[i].Trim();
            if (piece.Length > 0) kept.Add(piece);
        }
        return string.Join(", ", kept);
    }

    private static bool IsIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '_' || char.IsLetterOrDigit(c)) continue;
            return false;
        }
        return !char.IsDigit(s[0]);
    }

    private static bool IsIdentifierPath(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var piece in s.Split('.'))
            if (!IsIdentifier(piece)) return false;
        return true;
    }

    /// <summary>A dotted name, optionally with <c>/</c> nested separators and <c>`n</c> arity.</summary>
    private static bool IsTypeName(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var nested in s.Split('/'))
        {
            if (nested.Length == 0) return false;
            foreach (var piece in nested.Split('.'))
            {
                if (piece.Length == 0) return false;
                int tick = piece.IndexOf('`');
                var head = tick >= 0 ? piece.Substring(0, tick) : piece;
                if (!IsIdentifier(head)) return false;
                if (tick >= 0)
                {
                    var arity = piece.Substring(tick + 1);
                    if (arity.Length == 0) return false;
                    foreach (var c in arity) if (!char.IsDigit(c)) return false;
                }
            }
        }
        return true;
    }
}
