using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Progenitor.Plugins;

namespace Progenitor.Install.Admission;

/// <summary>
/// Mod compatibility C3: what one plugin image's <c>[BepInPlugin]</c> type declares in
/// <c>[NetworkCompatibility]</c>, read from the bytes. <see cref="Declared"/> false: the type carries no such attribute.
/// </summary>
internal sealed record PluginCompatFacts(
    string Guid,
    string Version,
    bool Declared,
    NetworkCompatibilityLevel Level,
    bool RuleDeclared,
    NetworkVersionRule Rule,
    string Peers)
{
    /// <summary>The declared <c>Peers</c> text, trimmed; empty when none.</summary>
    public string DeclaredPeers => (Peers ?? "").Trim();

    /// <summary>
    /// The range the join evaluation uses for this build (<see cref="CompatRanges.EffectiveText"/>): the declared range,
    /// else the explicit rule, else the same minor version. Empty only when the version cannot be carried.
    /// </summary>
    public string EffectiveRange => Declared
        ? CompatRanges.EffectiveText(Version, DeclaredPeers, RuleDeclared, Rule) ?? ""
        : CompatDeclaration.DefaultRange(Version);
}

/// <summary>
/// Mod compatibility C3: reads the <c>[NetworkCompatibility]</c> declaration of a plugin image without loading it
/// (<see cref="MetadataReader"/> only, the attribute blob decoded by hand, as <see cref="ArtifactPreflight"/> does). Package
/// inspection uses it to check that <c>progenitor.json</c>'s <c>compatibility.peers</c> agrees with the image. Game-free.
/// </summary>
internal static class PluginCompatReader
{
    public const string AttributeName = "Progenitor.Plugins.NetworkCompatibilityAttribute";
    private const string PeersMember = "Peers";

    /// <summary>The image's plugin entry points (<c>[BepInPlugin]</c> types) and their declarations. Empty when the bytes are not a managed assembly.</summary>
    public static IReadOnlyList<PluginCompatFacts> Read(ReadOnlyMemory<byte> image)
    {
        var list = new List<PluginCompatFacts>();
        if (image.Length < 128) return list;
        try
        {
            using var pe = new PEReader(ImmutableArray.Create(image.ToArray()));
            if (!pe.HasMetadata) return list;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return list;
            foreach (var th in md.TypeDefinitions)
            {
                var td = md.GetTypeDefinition(th);
                string guid = "", version = "";
                bool entry = false, declared = false, ruleDeclared = false;
                byte level = 0, rule = 0;
                string peers = "";
                foreach (var ah in td.GetCustomAttributes())
                {
                    var ca = md.GetCustomAttribute(ah);
                    if (!ArtifactPreflight.TryAttributeType(md, ca, out var name, out var asm)) continue;
                    if (name == PreflightOptions.BepInPluginAttribute
                        && string.Equals(asm, PreflightOptions.BepInPluginAssembly, StringComparison.OrdinalIgnoreCase))
                    {
                        if (ArtifactPreflight.TryDecodeThreeStrings(md, ca, out guid, out _, out version)) entry = true;
                    }
                    else if (name == AttributeName && TryDecode(md, ca, out level, out ruleDeclared, out rule, out peers))
                    {
                        declared = true;
                    }
                }

                if (!entry) continue;
                // Review fix: the attribute is Inherited, and the runtime reads it up the base-type chain
                // (CompatDeclaration.From), so a declaration on a base class in this image counts too.
                if (!declared) declared = TryInherited(md, td, out level, out ruleDeclared, out rule, out peers);
                list.Add(new PluginCompatFacts(guid, version, declared, (NetworkCompatibilityLevel)level, ruleDeclared,
                    (NetworkVersionRule)rule, peers));
            }
        }
        catch (BadImageFormatException) { list.Clear(); }
        catch (InvalidOperationException) { list.Clear(); }

        return list;
    }

    /// <summary>
    /// The nearest base class of <paramref name="type"/> defined in this image that carries the attribute. A base in another
    /// assembly (the framework's <c>ProgenitorPlugin&lt;T&gt;</c>, <c>BasePlugin</c>) ends the walk: it declares nothing.
    /// </summary>
    private static bool TryInherited(MetadataReader md, TypeDefinition type, out byte level, out bool ruleDeclared, out byte rule, out string peers)
    {
        level = 0;
        rule = 0;
        ruleDeclared = false;
        peers = "";
        var current = type;
        for (int depth = 0; depth < 32; depth++)
        {
            var next = LocalType(md, current.BaseType);
            if (next == null) return false;
            current = md.GetTypeDefinition(next.Value);
            foreach (var ah in current.GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(ah);
                if (!ArtifactPreflight.TryAttributeType(md, ca, out var name, out _)) continue;
                if (name == AttributeName && TryDecode(md, ca, out level, out ruleDeclared, out rule, out peers)) return true;
            }
        }

        return false;
    }

    /// <summary>A type defined in this image: a plain definition, or the generic type of a local generic instantiation.</summary>
    private static TypeDefinitionHandle? LocalType(MetadataReader md, EntityHandle handle)
    {
        if (handle.IsNil) return null;
        if (handle.Kind == HandleKind.TypeDefinition) return (TypeDefinitionHandle)handle;
        if (handle.Kind != HandleKind.TypeSpecification) return null;
        var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
        if (blob.RemainingBytes < 2 || blob.ReadByte() != 0x15) return null; // GENERICINST
        byte kind = blob.ReadByte();
        if (kind != 0x12 && kind != 0x11) return null;                       // CLASS or VALUETYPE
        var generic = blob.ReadTypeHandle();
        return generic.Kind == HandleKind.TypeDefinition ? (TypeDefinitionHandle)generic : null;
    }

    /// <summary>
    /// The facts of the entry point whose GUID is <paramref name="id"/>, else the only entry point when
    /// <paramref name="id"/> is empty; null when there is none or more than one candidate.
    /// </summary>
    public static PluginCompatFacts? Pick(IReadOnlyList<PluginCompatFacts> entries, string? id)
    {
        if (entries == null || entries.Count == 0) return null;
        string want = (id ?? "").Trim();
        if (want.Length == 0) return entries.Count == 1 ? entries[0] : null;
        foreach (var e in entries)
            if (string.Equals(e.Guid, want, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    /// <summary>
    /// Decode <c>[NetworkCompatibility(level[, rule], Peers = "...")]</c>: the constructor's argument count comes from its
    /// signature, each argument is a one-byte enum (both enums are <c>: byte</c>), then the named arguments; only a
    /// string property named <c>Peers</c> is read.
    /// </summary>
    private static bool TryDecode(MetadataReader md, CustomAttribute ca, out byte level, out bool ruleDeclared, out byte rule, out string peers)
    {
        level = 0;
        rule = 0;
        ruleDeclared = false;
        peers = "";
        try
        {
            int args = ParameterCount(md, ca);
            if (args < 1 || args > 2) return false;
            var blob = md.GetBlobReader(ca.Value);
            if (blob.ReadUInt16() != 0x0001) return false;
            if (blob.RemainingBytes < args) return false;
            level = blob.ReadByte();
            if (args == 2)
            {
                rule = blob.ReadByte();
                ruleDeclared = true;
            }

            if (blob.RemainingBytes < 2) return true;
            int named = blob.ReadUInt16();
            for (int i = 0; i < named; i++)
            {
                byte kind = blob.ReadByte();
                byte type = blob.ReadByte();
                if ((kind != 0x54 && kind != 0x53) || type != 0x0E) break; // only string members are read
                string? member = blob.ReadSerializedString();
                string? value = blob.ReadSerializedString();
                if (string.Equals(member, PeersMember, StringComparison.Ordinal)) peers = value ?? "";
            }

            return true;
        }
        catch (BadImageFormatException) { return false; }
    }

    private static int ParameterCount(MetadataReader md, CustomAttribute ca)
    {
        BlobHandle sig;
        if (ca.Constructor.Kind == HandleKind.MemberReference)
            sig = md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Signature;
        else if (ca.Constructor.Kind == HandleKind.MethodDefinition)
            sig = md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).Signature;
        else return -1;
        var reader = md.GetBlobReader(sig);
        reader.ReadSignatureHeader();
        return reader.ReadCompressedInteger();
    }
}
