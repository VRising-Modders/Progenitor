using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Progenitor.DriftMapTool;

/// <summary>One member of a type, as the diff sees it.</summary>
public sealed class MemberRow
{
    public MemberRow(string name, string signature, bool isField, bool isStatic)
    {
        Name = name ?? "";
        Signature = signature ?? "";
        IsField = isField;
        IsStatic = isStatic;
    }

    public string Name { get; }

    /// <summary>Decoded signature: <c>Ret(P1, P2)</c> for a method, the field type for a field.</summary>
    public string Signature { get; }

    public bool IsField { get; }
    public bool IsStatic { get; }

    /// <summary>Identity inside one declaring type.</summary>
    public string Key => (IsField ? "F|" : "M|") + Name + "|" + Signature;

    /// <summary>A compiler-generated name (a backing field, an iterator, a lambda closure).</summary>
    public bool IsCompilerGenerated => Name.IndexOf('<') >= 0;
}

/// <summary>Every member of one type, keyed by <see cref="MemberRow.Key"/>.</summary>
public sealed class TypeSurface
{
    public TypeSurface(string fullName) => FullName = fullName ?? "";

    /// <summary>Full name, nested types joined with <c>/</c> — MetadataScan's encoding.</summary>
    public string FullName { get; }

    public List<MemberRow> Members { get; } = new();

    /// <summary>Ordinal set of member keys, for the type-rename "same shape" test.</summary>
    public HashSet<string> Shape()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in Members)
            if (!member.IsCompilerGenerated) set.Add(member.Key);
        return set;
    }
}

/// <summary>
/// The type and member surface of one interop assembly set, read with
/// <see cref="MetadataReader"/> only — no Cecil, no assembly load, no game process (research
/// compat-rewriter.md §2.2: the generator must run on a machine that never launched V Rising,
/// and it must agree with <c>MetadataScan</c> on what a signature string is).
/// </summary>
public sealed class AssemblySurface
{
    private readonly Dictionary<string, Dictionary<string, TypeSurface>> _byAssembly =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Assembly simple names in the set.</summary>
    public IEnumerable<string> Assemblies => _byAssembly.Keys;

    public IReadOnlyDictionary<string, TypeSurface> Types(string assembly) =>
        _byAssembly.TryGetValue(assembly, out var types)
            ? types
            : new Dictionary<string, TypeSurface>(StringComparer.Ordinal);

    public bool HasAssembly(string assembly) => _byAssembly.ContainsKey(assembly);

    /// <summary>Assemblies in this set that define <paramref name="typeFullName"/>.</summary>
    public List<string> AssembliesDefining(string typeFullName)
    {
        var found = new List<string>();
        foreach (var pair in _byAssembly)
            if (pair.Value.ContainsKey(typeFullName)) found.Add(pair.Key);
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    /// <summary>
    /// Read every <c>*.dll</c> in <paramref name="directory"/> whose simple name matches one of
    /// <paramref name="prefixes"/>. An unreadable file is skipped with a note rather than
    /// failing the run: an interop folder routinely carries files that are not managed images.
    /// </summary>
    public static AssemblySurface Read(string directory, IReadOnlyList<string> prefixes, List<string> notes)
    {
        var surface = new AssemblySurface();
        foreach (var path in Directory.GetFiles(directory, "*.dll"))
        {
            string simple = Path.GetFileNameWithoutExtension(path);
            if (!Matches(simple, prefixes)) continue;
            try
            {
                surface._byAssembly[simple] = ReadOne(path);
            }
            catch (Exception e)
            {
                notes.Add("skipped " + Path.GetFileName(path) + ": " + e.Message);
            }
        }
        return surface;
    }

    private static bool Matches(string name, IReadOnlyList<string> prefixes)
    {
        for (int i = 0; i < prefixes.Count; i++)
            if (name.StartsWith(prefixes[i], StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static Dictionary<string, TypeSurface> ReadOne(string path)
    {
        var types = new Dictionary<string, TypeSurface>(StringComparer.Ordinal);
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return types;
        var md = pe.GetMetadataReader();
        var provider = new SignatureText(md);

        foreach (var handle in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(handle);
            string full = FullName(md, td);
            if (full.Length == 0) continue;
            var surface = new TypeSurface(full);
            foreach (var mh in td.GetMethods())
            {
                var m = md.GetMethodDefinition(mh);
                string sig;
                try { sig = FormatMethod(m.DecodeSignature(provider, null)); }
                catch { continue; }
                bool isStatic = (m.Attributes & System.Reflection.MethodAttributes.Static) != 0;
                surface.Members.Add(new MemberRow(md.GetString(m.Name), sig, isField: false, isStatic));
            }
            foreach (var fh in td.GetFields())
            {
                var f = md.GetFieldDefinition(fh);
                string sig;
                try { sig = f.DecodeSignature(provider, null); }
                catch { continue; }
                bool isStatic = (f.Attributes & System.Reflection.FieldAttributes.Static) != 0;
                surface.Members.Add(new MemberRow(md.GetString(f.Name), sig, isField: true, isStatic));
            }
            types[full] = surface;
        }
        return types;
    }

    private static string FullName(MetadataReader md, TypeDefinition td)
    {
        var names = new List<string>();
        var current = td;
        for (int depth = 0; depth < 16; depth++)
        {
            string ns = current.Namespace.IsNil ? "" : md.GetString(current.Namespace);
            string name = md.GetString(current.Name);
            names.Add(ns.Length == 0 ? name : ns + "." + name);
            var declaring = current.GetDeclaringType();
            if (declaring.IsNil) break;
            current = md.GetTypeDefinition(declaring);
        }
        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>Identical to <c>MetadataScan.FormatMethod</c>: the two must render the same string.</summary>
    internal static string FormatMethod(MethodSignature<string> signature)
    {
        var sb = new StringBuilder();
        sb.Append(signature.ReturnType);
        sb.Append('(');
        for (int i = 0; i < signature.ParameterTypes.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(signature.ParameterTypes[i]);
        }
        sb.Append(')');
        if (signature.GenericParameterCount > 0)
            sb.Append('`').Append(signature.GenericParameterCount);
        return sb.ToString();
    }

    /// <summary>
    /// Renders signature blobs the way <c>MetadataScan.SignatureText</c> does. It is a copy of
    /// that renderer rather than a reference to it because the Core original is a private nested
    /// class; the end-to-end test (generate a map, rewrite a consumer, load it) is what pins the
    /// two renderings against each other.
    /// </summary>
    private sealed class SignatureText : ISignatureTypeProvider<string, object?>
    {
        private readonly MetadataReader _reader;

        public SignatureText(MetadataReader reader) => _reader = reader;

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.TypedReference => "System.TypedReference",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            PrimitiveTypeCode.Void => "System.Void",
            _ => typeCode.ToString(),
        };

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var td = reader.GetTypeDefinition(handle);
            string ns = td.Namespace.IsNil ? "" : reader.GetString(td.Namespace);
            string name = reader.GetString(td.Name);
            var declaring = td.GetDeclaringType();
            if (!declaring.IsNil) return GetTypeFromDefinition(reader, declaring, 0) + "/" + name;
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var tr = reader.GetTypeReference(handle);
            string ns = tr.Namespace.IsNil ? "" : reader.GetString(tr.Namespace);
            string name = reader.GetString(tr.Name);
            if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
                return GetTypeFromReference(reader, (TypeReferenceHandle)tr.ResolutionScope, 0) + "/" + name;
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            try { return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext); }
            catch { return "?"; }
        }

        public string GetSZArrayType(string elementType) => elementType + "[]";

        public string GetArrayType(string elementType, ArrayShape shape) =>
            elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";

        public string GetByReferenceType(string elementType) => elementType + "&";

        public string GetPointerType(string elementType) => elementType + "*";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType + "<" + string.Join(",", typeArguments) + ">";

        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;

        public string GetFunctionPointerType(MethodSignature<string> signature) =>
            "fnptr " + FormatMethod(signature);

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType + " pinned";
    }
}
