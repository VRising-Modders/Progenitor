using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Progenitor.Access;

namespace Progenitor.AccessGen;

/// <summary>What the interop assemblies say one declared field holds.</summary>
public enum InteropFieldShape
{
    /// <summary>Not answerable here — no such assembly, type or member, or a generic parameter.</summary>
    Unknown = 0,

    /// <summary>A value type: the blittable accessors are the right ones.</summary>
    Value = 1,

    /// <summary>A reference type: the field holds an object pointer, so the line needs <c>ref</c>.</summary>
    Reference = 2,
}

/// <summary>
/// Where <see cref="AccessGenerator.VerifyReferenceModifiers"/> gets its second opinion. An
/// interface so the rule is testable without any assembly on disk, and so a caller with no interop
/// folder simply does not pass one.
/// </summary>
public interface IInteropFieldShapes
{
    /// <summary>
    /// What <paramref name="declaration"/>'s field holds, plus one sentence saying where the
    /// answer came from (or why there is none).
    /// </summary>
    InteropFieldShape ShapeOf(AccessDeclaration declaration, out string detail);
}

/// <summary>
/// Reads the interop assemblies BepInEx generated (<c>&lt;game&gt;\BepInEx\interop</c>) and answers
/// "is this field's declared type a class or a struct" from their metadata.
///
/// **Metadata only.** A <see cref="PEReader"/> over files, the way
/// <c>Progenitor.Resilience.MetadataScan</c> does it: no <c>Assembly.Load</c>, no reference on
/// Il2CppInterop or ProjectM (the csproj's <c>ForbidGameReferences</c> target still holds), no game
/// process. The generator stays runnable on a machine that has never launched V Rising — it just
/// verifies nothing there, and says so.
///
/// The interop models an IL2CPP instance field as a C# **property** on the stub type
/// (<c>public RandomLocalizationKeys RandomLocalizationKeys</c>), with the
/// <c>NativeFieldInfoPtr_&lt;name&gt;</c> static beside it, so the property is looked up first and
/// a real field of that name second.
/// </summary>
public sealed class InteropMetadataShapes : IInteropFieldShapes, IDisposable
{
    private readonly string _directory;
    private readonly Dictionary<string, Assembly?> _assemblies = new(StringComparer.OrdinalIgnoreCase);

    public InteropMetadataShapes(string directory) => _directory = directory ?? "";

    /// <summary>The folder this reads, for the generator's log line.</summary>
    public string Directory => _directory;

    public InteropFieldShape ShapeOf(AccessDeclaration declaration, out string detail)
    {
        detail = "";
        if (declaration == null || declaration.Kind != AccessDeclarationKind.Field)
        {
            detail = "not a field declaration";
            return InteropFieldShape.Unknown;
        }

        var assembly = Load(declaration.Assembly);
        if (assembly == null)
        {
            detail = "no " + declaration.Assembly + ".dll in " + _directory;
            return InteropFieldShape.Unknown;
        }
        return assembly.ShapeOf(declaration.Type, declaration.Name, out detail);
    }

    private Assembly? Load(string simpleName)
    {
        if (string.IsNullOrEmpty(simpleName)) return null;
        if (_assemblies.TryGetValue(simpleName, out var cached)) return cached;

        Assembly? loaded = null;
        var path = Path.Combine(_directory, simpleName + ".dll");
        if (File.Exists(path))
        {
            try { loaded = new Assembly(path); }
            catch (BadImageFormatException) { loaded = null; }
            catch (IOException) { loaded = null; }
            catch (UnauthorizedAccessException) { loaded = null; }
        }
        _assemblies[simpleName] = loaded;
        return loaded;
    }

    public void Dispose()
    {
        foreach (var assembly in _assemblies.Values) assembly?.Dispose();
        _assemblies.Clear();
    }

    private sealed class Assembly : IDisposable
    {
        private readonly PEReader _pe;
        private readonly MetadataReader _md;
        private readonly Dictionary<string, TypeDefinitionHandle> _types = new(StringComparer.Ordinal);
        private readonly ShapeProvider _provider;

        public Assembly(string path)
        {
            _pe = new PEReader(File.OpenRead(path));
            _md = _pe.GetMetadataReader();
            _provider = new ShapeProvider();
            foreach (var handle in _md.TypeDefinitions)
                _types[FullName(_md, handle)] = handle;
        }

        public InteropFieldShape ShapeOf(string typeName, string memberName, out string detail)
        {
            detail = "";
            if (!_types.TryGetValue(typeName ?? "", out var typeHandle))
            {
                detail = "no type " + typeName + " in the interop assembly";
                return InteropFieldShape.Unknown;
            }

            var type = _md.GetTypeDefinition(typeHandle);

            foreach (var handle in type.GetProperties())
            {
                var property = _md.GetPropertyDefinition(handle);
                if (!string.Equals(_md.GetString(property.Name), memberName, StringComparison.Ordinal)) continue;
                var signature = property.DecodeSignature(_provider, null);
                detail = "the interop declares it as a property";
                return Describe(signature.ReturnType, ref detail);
            }

            foreach (var handle in type.GetFields())
            {
                var field = _md.GetFieldDefinition(handle);
                if (!string.Equals(_md.GetString(field.Name), memberName, StringComparison.Ordinal)) continue;
                detail = "the interop declares it as a field";
                var shape = field.DecodeSignature(_provider, null);
                return Describe(shape, ref detail);
            }

            detail = "the interop type " + typeName + " has no member named " + memberName;
            return InteropFieldShape.Unknown;
        }

        private static InteropFieldShape Describe(int shape, ref string detail)
        {
            switch (shape)
            {
                case 1:
                    detail += ", value-typed";
                    return InteropFieldShape.Value;
                case 2:
                    detail += ", reference-typed";
                    return InteropFieldShape.Reference;
                default:
                    detail += ", but its signature is a generic parameter this cannot classify";
                    return InteropFieldShape.Unknown;
            }
        }

        private static string FullName(MetadataReader md, TypeDefinitionHandle handle)
        {
            var td = md.GetTypeDefinition(handle);
            var name = md.GetString(td.Name);
            var declaring = td.GetDeclaringType();
            if (!declaring.IsNil) return FullName(md, declaring) + "/" + name;
            var ns = md.GetString(td.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public void Dispose() => _pe.Dispose();
    }

    /// <summary>
    /// Decodes one signature to <c>0</c> unknown, <c>1</c> value, <c>2</c> reference. The
    /// <c>rawTypeKind</c> a signature blob carries is exactly this question:
    /// <c>ELEMENT_TYPE_VALUETYPE</c> (0x11) or <c>ELEMENT_TYPE_CLASS</c> (0x12), ECMA-335 II.23.2.
    /// </summary>
    private sealed class ShapeProvider : ISignatureTypeProvider<int, object?>
    {
        private const int Unknown = 0;
        private const int Value = 1;
        private const int Reference = 2;
        private const byte ElementTypeValueType = 0x11;

        public int GetPrimitiveType(PrimitiveTypeCode typeCode) =>
            typeCode == PrimitiveTypeCode.String || typeCode == PrimitiveTypeCode.Object
                ? Reference
                : Value;

        public int GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            rawTypeKind == ElementTypeValueType ? Value : Reference;

        public int GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            rawTypeKind == ElementTypeValueType ? Value : Reference;

        public int GetTypeFromSpecification(
            MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            try { return reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext); }
            catch (BadImageFormatException) { return Unknown; }
        }

        // A generic instance is a class or a struct according to its definition: List<int> is a
        // class, Nullable<int> a struct.
        public int GetGenericInstantiation(int genericType, ImmutableArray<int> typeArguments) => genericType;

        public int GetSZArrayType(int elementType) => Reference;

        public int GetArrayType(int elementType, ArrayShape shape) => Reference;

        // Neither can appear as a field's own type on an interop stub; both are unmanaged if they
        // somehow did.
        public int GetPointerType(int elementType) => Value;

        public int GetFunctionPointerType(MethodSignature<int> signature) => Value;

        public int GetByReferenceType(int elementType) => Unknown;

        public int GetGenericMethodParameter(object? genericContext, int index) => Unknown;

        public int GetGenericTypeParameter(object? genericContext, int index) => Unknown;

        public int GetModifiedType(int modifier, int unmodifiedType, bool isRequired) => unmodifiedType;

        public int GetPinnedType(int elementType) => elementType;
    }
}
