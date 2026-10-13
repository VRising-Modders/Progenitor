using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Threading;

namespace Progenitor.Resilience;

/// <summary>
/// Member-level "will Core still bind" check, from metadata only.
///
/// Step 1 (<see cref="ScanReferences"/>) reads <c>Progenitor.dll</c> with
/// <see cref="MetadataReader"/> and walks its TypeRef and MemberRef tables, keeping every row
/// whose resolution scope is a game AssemblyRef (<c>ProjectM*</c>, <c>Unity*</c>,
/// <c>Stunlock*</c> by default). That is exactly the set of game types and members Core's IL
/// mentions — no more, no less.
///
/// Step 2 (<see cref="Resolve"/>) indexes the matching interop assemblies' TypeDef / MethodDef /
/// FieldDef tables and asks, per reference, whether the type is there, whether the name is
/// there, and whether any overload has the same decoded signature.
///
/// No game process, no <c>Assembly.Load</c>, no Il2CppInterop: a <see cref="PEReader"/> over
/// files. That is why the whole file is game-free and runs from
/// <c>tools/resilience-report.ps1</c> on a machine that never launched V Rising.
///
/// A member reached through a generic instantiation (a TypeSpec MemberRef parent —
/// <c>NativeArray&lt;T&gt;</c>, <c>DynamicBuffer&lt;T&gt;</c>, <c>List&lt;T&gt;</c>, most of
/// Core's Entities surface) is resolved against the **generic definition**: ECMA-335 writes such
/// a MemberRef's signature in terms of the declaring type's own parameters (<c>!0</c>), which is
/// exactly what the TypeDef side renders, so the two strings are comparable. Only a TypeSpec that
/// cannot be reduced to a type reference at all keeps a <see cref="BindingStatus.NotChecked"/>
/// row. Nothing is dropped (R31 finding 7): before that fix the whole generic surface was skipped
/// silently and the report still said "all bind".
///
/// Limits (stated again in the report and in the research note): the check sees signatures, not
/// behavior, so a method that kept its shape and changed its meaning still reads as Bound; and
/// it says nothing about the native side beyond whether an address table exists for the build.
/// </summary>
public static class MetadataScan
{
    /// <summary>
    /// Assembly slot for a row whose declaring type could not be named at all (an irreducible
    /// TypeSpec parent). Deliberately not a valid file name so <see cref="Resolve"/> leaves it
    /// <see cref="BindingStatus.NotChecked"/> with its own reason.
    /// </summary>
    public const string UnresolvedAssembly = "(unresolved)";

    /// <summary>Assembly-name prefixes treated as "the game". Matched case-insensitively.</summary>
    public static readonly string[] DefaultGameAssemblyPrefixes = { "ProjectM", "Unity", "Stunlock" };

    /// <summary>True when a simple assembly name is one of the game assemblies we track.</summary>
    public static bool IsGameAssembly(string? name, IReadOnlyList<string>? prefixes = null)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var list = prefixes ?? DefaultGameAssemblyPrefixes;
        for (int i = 0; i < list.Count; i++)
        {
            var p = list[i];
            if (string.IsNullOrEmpty(p)) continue;
            if (name!.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Every game type and member <paramref name="assemblyPath"/> references, as unresolved
    /// <see cref="MemberBinding"/> rows (<see cref="BindingStatus.NotChecked"/>). Returns an
    /// empty list and a reason instead of throwing.
    /// </summary>
    public static IReadOnlyList<MemberBinding> ScanReferences(
        string assemblyPath,
        out string? error,
        IReadOnlyList<string>? prefixes = null)
    {
        error = null;
        var rows = new List<MemberBinding>();
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) { error = assemblyPath + " has no managed metadata."; return rows; }
            var md = pe.GetMetadataReader();
            var provider = new SignatureText(md);

            var refAssembly = new Dictionary<TypeReferenceHandle, string>();
            var refName = new Dictionary<TypeReferenceHandle, string>();

            foreach (var handle in md.TypeReferences)
            {
                if (!TryResolveTypeRef(md, handle, out var asm, out var full)) continue;
                if (!IsGameAssembly(asm, prefixes)) continue;
                // R34 finding 10: the declaring-type half of a key goes through the ONE shared
                // reduction both readers use, so a row this scan produces and a key the rewriter
                // looks up are the same string for a generic declaring type. A TypeRef name is
                // already open, so this is a no-op here today — which is the point: the rule
                // lives in one place, and the rewriter's Cecil side calls the same function.
                full = Progenitor.Compat.DriftRule.OpenTypeText(full);
                refAssembly[handle] = asm;
                refName[handle] = full;
                rows.Add(new MemberBinding(asm, full, "", MemberKind.Type, "", BindingStatus.NotChecked));
            }

            foreach (var handle in md.MemberReferences)
            {
                var mr = md.GetMemberReference(handle);
                TypeReferenceHandle parent;
                if (mr.Parent.Kind == HandleKind.TypeReference)
                {
                    parent = (TypeReferenceHandle)mr.Parent;
                }
                else if (mr.Parent.Kind == HandleKind.TypeSpecification)
                {
                    // A generic instantiation. Reduce it to the open type it instantiates and
                    // check the member there instead of dropping the row.
                    var spec = (TypeSpecificationHandle)mr.Parent;
                    var reduced = TryGenericDefinition(md, spec, out parent);
                    if (reduced == SpecParent.Unreadable)
                    {
                        rows.Add(new MemberBinding(
                            UnresolvedAssembly,
                            DescribeSpec(md, spec, provider),
                            md.GetString(mr.Name),
                            MemberKind.Method,
                            "",
                            BindingStatus.NotChecked,
                            "TypeSpec parent blob did not decode"));
                        continue;
                    }
                    if (reduced != SpecParent.Reference) continue;
                }
                else
                {
                    continue; // ModuleRef / MethodDef parents (vararg call sites): not a type.
                }
                if (!refAssembly.TryGetValue(parent, out var asm)) continue;
                var type = refName[parent];
                var name = md.GetString(mr.Name);

                string signature;
                MemberKind kind;
                try
                {
                    if (mr.GetKind() == MemberReferenceKind.Field)
                    {
                        kind = MemberKind.Field;
                        signature = mr.DecodeFieldSignature(provider, null);
                    }
                    else
                    {
                        kind = MemberKind.Method;
                        signature = FormatMethod(mr.DecodeMethodSignature(provider, null));
                    }
                }
                catch (Exception e)
                {
                    rows.Add(new MemberBinding(asm, type, name, MemberKind.Method, "",
                        BindingStatus.NotChecked, "signature blob unreadable: " + e.Message));
                    continue;
                }
                rows.Add(new MemberBinding(asm, type, name, kind, signature, BindingStatus.NotChecked));
            }

            // Job FACC. A generated access-layer accessor resolves its member by NAME at first
            // use, so that member is a string literal, not a MemberRef row — the two loops above
            // cannot see it and the report would say "all bind" about a member nothing can bind.
            // tools/accessgen stamps every declaration into the assembly as an assembly-level
            // AccessMemberAttribute, which is metadata in this same file, so they are read here
            // and resolved by the same Resolve() pass as any other reference. See
            // Access/AccessMemberAttribute.cs.
            ScanAccessDeclarations(md, rows, prefixes);
        }
        catch (Exception e)
        {
            error = "failed to scan " + assemblyPath + ": " + e.Message;
        }

        var deduped = Dedupe(rows);
        MemberBinding.Sort(deduped);
        return deduped;
    }

    /// <summary>
    /// Resolve every reference against the assemblies in <paramref name="interopDirectory"/>.
    /// An assembly that is not on disk leaves its rows <see cref="BindingStatus.NotChecked"/>.
    /// </summary>
    public static IReadOnlyList<MemberBinding> Resolve(
        IReadOnlyList<MemberBinding> references,
        string interopDirectory,
        CancellationToken cancellation = default)
    {
        var result = new List<MemberBinding>();
        if (references == null) return result;
        var indexes = new Dictionary<string, AssemblyIndex?>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in references)
        {
            cancellation.ThrowIfCancellationRequested();
            if (row == null) continue;
            // A row the scanner already explained (unreadable signature blob, irreducible
            // TypeSpec parent) keeps its reason instead of being relabeled "dll not readable".
            if (row.Status == BindingStatus.NotChecked && row.Found.Length > 0)
            {
                result.Add(row);
                continue;
            }
            if (!indexes.TryGetValue(row.Assembly, out var index))
            {
                index = TryIndex(Path.Combine(interopDirectory ?? "", row.Assembly + ".dll"));
                indexes[row.Assembly] = index;
            }
            result.Add(index == null
                ? new MemberBinding(row.Assembly, row.Type, row.Name, row.Kind, row.Signature,
                    BindingStatus.NotChecked, row.Assembly + ".dll not readable in " + interopDirectory)
                : index.Resolve(row));
        }

        MemberBinding.Sort(result);
        return result;
    }

    /// <summary>Assembly version from the metadata Assembly row, or "" when unreadable.</summary>
    public static string ReadAssemblyVersion(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return "";
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return "";
            return md.GetAssemblyDefinition().Version.ToString();
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// The module version id of a managed assembly, lowercase hex with no braces, or "" when the
    /// file is missing, unreadable or has no metadata.
    /// </summary>
    /// <remarks>
    /// Job FUPD10 (R57 finding 7): the compiler stamps a fresh MVID into every compilation, so
    /// this is the only cheap thing that distinguishes two builds of Core that share an assembly
    /// version — which every developer-tree deploy does, version bumps being a separate commit.
    /// Same shape as <see cref="ReadAssemblyVersion"/> and, like it, never throws.
    /// <para><b>Not the way Core's own MVID is read</b> (job FUPD11, R58 finding 9). This answers
    /// "which build is this <em>file</em>", which is a question about a file on disk. The
    /// fingerprint's <c>core</c> row asks "which Core is <em>running</em>", and that is taken from
    /// the loaded assembly's <c>ManifestModule.ModuleVersionId</c> beside its version, so the two
    /// halves cannot come from different Cores. Do not route the core row back through here.</para>
    /// </remarks>
    public static string ReadModuleVersionId(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return "";
            var md = pe.GetMetadataReader();
            return md.GetGuid(md.GetModuleDefinition().Mvid).ToString("N");
        }
        catch
        {
            return "";
        }
    }

    private static List<MemberBinding> Dedupe(List<MemberBinding> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var outRows = new List<MemberBinding>(rows.Count);
        foreach (var row in rows)
            if (seen.Add(row.Key)) outRows.Add(row);
        return outRows;
    }

    private static bool TryResolveTypeRef(MetadataReader md, TypeReferenceHandle handle, out string assembly, out string full)
    {
        assembly = "";
        full = "";
        var names = new List<string>();
        var current = handle;
        for (int depth = 0; depth < 16; depth++)
        {
            var tr = md.GetTypeReference(current);
            var ns = tr.Namespace.IsNil ? "" : md.GetString(tr.Namespace);
            var name = md.GetString(tr.Name);
            names.Add(ns.Length == 0 ? name : ns + "." + name);

            var scope = tr.ResolutionScope;
            if (scope.Kind == HandleKind.AssemblyReference)
            {
                var ar = md.GetAssemblyReference((AssemblyReferenceHandle)scope);
                assembly = md.GetString(ar.Name);
                names.Reverse();
                full = string.Join("/", names);
                return true;
            }
            if (scope.Kind == HandleKind.TypeReference)
            {
                current = (TypeReferenceHandle)scope;
                continue;
            }
            return false; // ModuleRef / ModuleDef / nil: not a game assembly reference
        }
        return false;
    }

    private static readonly SpecRoot SpecRootProvider = new();

    /// <summary>What a TypeSpec MemberRef parent reduced to.</summary>
    private enum SpecParent
    {
        /// <summary>A generic instantiation of a type reference: check the member on the open type.</summary>
        Reference,

        /// <summary>An array, pointer, generic parameter or a TypeDef in the scanned assembly —
        /// never a member defined in a game assembly, so out of scope by the same rule that
        /// drops non-game TypeRefs.</summary>
        OutOfScope,

        /// <summary>The blob did not decode. Counted <see cref="BindingStatus.NotChecked"/>.</summary>
        Unreadable,
    }

    /// <summary>
    /// The open generic type a TypeSpec instantiates, when that reduces to a TypeRef —
    /// <c>NativeArray&lt;Entity&gt;</c> to <c>NativeArray`1</c>.
    /// </summary>
    private static SpecParent TryGenericDefinition(
        MetadataReader md,
        TypeSpecificationHandle handle,
        out TypeReferenceHandle definition)
    {
        definition = default;
        try
        {
            var root = md.GetTypeSpecification(handle).DecodeSignature(SpecRootProvider, null);
            if (root == null || root.Value.IsNil) return SpecParent.OutOfScope;
            definition = root.Value;
            return SpecParent.Reference;
        }
        catch
        {
            return SpecParent.Unreadable;
        }
    }

    /// <summary>Readable text for a TypeSpec, for the <c>NotChecked</c> row's type column.</summary>
    private static string DescribeSpec(
        MetadataReader md,
        TypeSpecificationHandle handle,
        ISignatureTypeProvider<string, object?> provider)
    {
        try { return md.GetTypeSpecification(handle).DecodeSignature(provider, null); }
        catch { return "(unreadable TypeSpec)"; }
    }

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
    /// Simple name of the attribute <c>tools/accessgen</c> stamps declarations with. Matched by
    /// name rather than by resolving the type, because this scan never loads an assembly.
    /// </summary>
    internal const string AccessMemberAttributeName = "AccessMemberAttribute";

    /// <summary>
    /// Adds one row per declared access-layer member. Rows land <see cref="BindingStatus.NotChecked"/>
    /// like every other reference and are resolved by <see cref="Resolve"/>.
    ///
    /// A declaration that names no overload carries the wildcard signature
    /// <c>AccessMemberAttribute.AnySignature</c>, which <see cref="AssemblyIndex.Resolve"/> reads
    /// as "any member with this name". That is exactly what the accessor does at runtime, so the
    /// report and the runtime agree about what counts as present.
    /// </summary>
    private static void ScanAccessDeclarations(
        MetadataReader md,
        List<MemberBinding> rows,
        IReadOnlyList<string>? prefixes)
    {
        if (!md.IsAssembly) return;
        foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute attribute;
            try { attribute = md.GetCustomAttribute(handle); }
            catch { continue; }
            if (!IsAccessMemberAttribute(md, attribute)) continue;

            string assembly, type, name, kind, signature;
            try
            {
                var blob = md.GetBlobReader(attribute.Value);
                if (blob.RemainingBytes < 2 || blob.ReadUInt16() != 1) continue; // prolog
                assembly = blob.ReadSerializedString() ?? "";
                type = blob.ReadSerializedString() ?? "";
                name = blob.ReadSerializedString() ?? "";
                kind = blob.ReadSerializedString() ?? "";
                signature = blob.ReadSerializedString() ?? "";
            }
            catch
            {
                // A blob that does not decode is a generator bug, not a game update. Say so
                // rather than dropping the declaration silently.
                rows.Add(new MemberBinding(UnresolvedAssembly, "(access declaration)", "", MemberKind.Type, "",
                    BindingStatus.NotChecked, "AccessMemberAttribute blob did not decode"));
                continue;
            }

            if (type.Length == 0) continue;
            if (!IsGameAssembly(assembly, prefixes)) continue;

            var memberKind = kind switch
            {
                "Field" => MemberKind.Field,
                "Method" => MemberKind.Method,
                _ => MemberKind.Type,
            };
            if (name.Length == 0) memberKind = MemberKind.Type;
            rows.Add(new MemberBinding(
                assembly, Progenitor.Compat.DriftRule.OpenTypeText(type), name, memberKind,
                memberKind == MemberKind.Type ? "" : signature, BindingStatus.NotChecked));
        }
    }

    private static bool IsAccessMemberAttribute(MetadataReader md, CustomAttribute attribute)
    {
        try
        {
            switch (attribute.Constructor.Kind)
            {
                case HandleKind.MethodDefinition:
                {
                    // The attribute is defined in the assembly being scanned (Core's own case).
                    var ctor = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                    var td = md.GetTypeDefinition(ctor.GetDeclaringType());
                    return string.Equals(md.GetString(td.Name), AccessMemberAttributeName, StringComparison.Ordinal);
                }
                case HandleKind.MemberReference:
                {
                    // A plugin that references Core's attribute rather than defining its own.
                    var mr = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    if (mr.Parent.Kind != HandleKind.TypeReference) return false;
                    var tr = md.GetTypeReference((TypeReferenceHandle)mr.Parent);
                    return string.Equals(md.GetString(tr.Name), AccessMemberAttributeName, StringComparison.Ordinal);
                }
                default:
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private static AssemblyIndex? TryIndex(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return AssemblyIndex.Build(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Type / member / signature index of one interop assembly, built from TypeDef rows.</summary>
    internal sealed class AssemblyIndex
    {
        private readonly Dictionary<string, TypeEntry> _types = new(StringComparer.Ordinal);

        public static AssemblyIndex Build(string path)
        {
            var index = new AssemblyIndex();
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return index;
            var md = pe.GetMetadataReader();
            var provider = new SignatureText(md);

            foreach (var handle in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(handle);
                var full = FullName(md, td, handle);
                if (full.Length == 0) continue;
                var entry = new TypeEntry();
                foreach (var mh in td.GetMethods())
                {
                    var m = md.GetMethodDefinition(mh);
                    var name = md.GetString(m.Name);
                    string sig;
                    try { sig = FormatMethod(m.DecodeSignature(provider, null)); }
                    catch { sig = ""; }
                    entry.Add(name, sig);
                }
                foreach (var fh in td.GetFields())
                {
                    var f = md.GetFieldDefinition(fh);
                    var name = md.GetString(f.Name);
                    string sig;
                    try { sig = f.DecodeSignature(provider, null); }
                    catch { sig = ""; }
                    entry.Add(name, sig);
                }
                _typesAdd(index, full, entry);
            }
            return index;
        }

        private static void _typesAdd(AssemblyIndex index, string full, TypeEntry entry) => index._types[full] = entry;

        public MemberBinding Resolve(MemberBinding row)
        {
            if (!_types.TryGetValue(row.Type, out var type))
                return new MemberBinding(row.Assembly, row.Type, row.Name, row.Kind, row.Signature,
                    BindingStatus.TypeMissing);
            if (row.Kind == MemberKind.Type)
                return new MemberBinding(row.Assembly, row.Type, row.Name, row.Kind, row.Signature,
                    BindingStatus.Bound);
            if (!type.TryGet(row.Name, out var signatures))
                return new MemberBinding(row.Assembly, row.Type, row.Name, row.Kind, row.Signature,
                    BindingStatus.MemberMissing);
            // Job FACC: an access declaration that named no overload asks only that the name
            // exists, which is exactly what the generated accessor asks of the build at runtime.
            if (string.Equals(row.Signature, Progenitor.Access.AccessMemberAttribute.AnySignature, StringComparison.Ordinal))
                return new MemberBinding(row.Assembly, row.Type, row.Name, row.Kind, row.Signature,
                    BindingStatus.Bound);
            for (int i = 0; i < signatures.Count; i++)
                if (string.Equals(signatures[i], row.Signature, StringComparison.Ordinal))
                    return new MemberBinding(row.Assembly, row.Type, row.Name, row.Kind, row.Signature,
                        BindingStatus.Bound);
            return new MemberBinding(row.Assembly, row.Type, row.Name, row.Kind, row.Signature,
                BindingStatus.SignatureChanged, string.Join(" | ", signatures));
        }

        private static string FullName(MetadataReader md, TypeDefinition td, TypeDefinitionHandle handle)
        {
            var names = new List<string>();
            var current = td;
            for (int depth = 0; depth < 16; depth++)
            {
                var ns = current.Namespace.IsNil ? "" : md.GetString(current.Namespace);
                var name = md.GetString(current.Name);
                names.Add(ns.Length == 0 ? name : ns + "." + name);
                var declaring = current.GetDeclaringType();
                if (declaring.IsNil) break;
                current = md.GetTypeDefinition(declaring);
            }
            names.Reverse();
            return string.Join("/", names);
        }

        private sealed class TypeEntry
        {
            private readonly Dictionary<string, List<string>> _members = new(StringComparer.Ordinal);

            public void Add(string name, string signature)
            {
                if (!_members.TryGetValue(name, out var list))
                {
                    list = new List<string>();
                    _members[name] = list;
                }
                if (!list.Contains(signature)) list.Add(signature);
            }

            public bool TryGet(string name, out List<string> signatures) => _members.TryGetValue(name, out signatures!);
        }
    }

    /// <summary>
    /// Reduces a TypeSpec blob to the TypeRef it is built on: a generic instantiation yields its
    /// open type, a custom modifier is transparent, everything else yields null. That is all the
    /// TypeSpec MemberRef path needs — the member itself is then looked up on the open type,
    /// whose TypeDef renders its signatures with the same <c>!0</c> parameters the MemberRef blob
    /// uses.
    /// </summary>
    private sealed class SpecRoot : ISignatureTypeProvider<TypeReferenceHandle?, object?>
    {
        public TypeReferenceHandle? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => handle;

        public TypeReferenceHandle? GetGenericInstantiation(TypeReferenceHandle? genericType, ImmutableArray<TypeReferenceHandle?> typeArguments) => genericType;

        public TypeReferenceHandle? GetModifiedType(TypeReferenceHandle? modifier, TypeReferenceHandle? unmodifiedType, bool isRequired) => unmodifiedType;

        public TypeReferenceHandle? GetPinnedType(TypeReferenceHandle? elementType) => elementType;

        public TypeReferenceHandle? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => null;

        public TypeReferenceHandle? GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            try { return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext); }
            catch { return null; }
        }

        public TypeReferenceHandle? GetPrimitiveType(PrimitiveTypeCode typeCode) => null;

        // An array, a pointer or a byref parent is not the generic instantiation this path is
        // for: null, so the row is counted NotChecked rather than resolved against the element.
        public TypeReferenceHandle? GetSZArrayType(TypeReferenceHandle? elementType) => null;

        public TypeReferenceHandle? GetArrayType(TypeReferenceHandle? elementType, ArrayShape shape) => null;

        public TypeReferenceHandle? GetByReferenceType(TypeReferenceHandle? elementType) => null;

        public TypeReferenceHandle? GetPointerType(TypeReferenceHandle? elementType) => null;

        public TypeReferenceHandle? GetFunctionPointerType(MethodSignature<TypeReferenceHandle?> signature) => null;

        public TypeReferenceHandle? GetGenericMethodParameter(object? genericContext, int index) => null;

        public TypeReferenceHandle? GetGenericTypeParameter(object? genericContext, int index) => null;
    }

    /// <summary>Renders signature blobs as readable text. The same renderer runs over Core's
    /// MemberRefs and the interop MethodDefs, so the two strings are comparable.</summary>
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
            var ns = td.Namespace.IsNil ? "" : reader.GetString(td.Namespace);
            var name = reader.GetString(td.Name);
            var declaring = td.GetDeclaringType();
            if (!declaring.IsNil)
                return GetTypeFromDefinition(reader, declaring, 0) + "/" + name;
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var tr = reader.GetTypeReference(handle);
            var ns = tr.Namespace.IsNil ? "" : reader.GetString(tr.Namespace);
            var name = reader.GetString(tr.Name);
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
