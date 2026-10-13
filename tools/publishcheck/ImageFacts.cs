using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Progenitor.Plugins;

namespace Progenitor.PublishCheck;

/// <summary>A <c>[BepInDependency]</c> as the bytes declare it.</summary>
internal sealed record DeclaredDependency(string Guid, bool Soft, string MinVersion);

/// <summary>One <c>[BepInPlugin]</c> type of an image and the Progenitor attributes it carries (its own or a local base class's).</summary>
internal sealed class PluginEntry
{
    public string TypeName { get; init; } = "";
    public string Guid { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";

    /// <summary>The raw <c>[ProgenitorSide]</c> value (1 server, 2 client, 3 both); null when not declared (the runtime default is both).</summary>
    public int? SideRaw { get; set; }

    /// <summary>True when <c>[NetworkCompatibility]</c> is declared.</summary>
    public bool CompatDeclared { get; set; }

    /// <summary>The declared <c>Peers</c> text, trimmed; empty when none.</summary>
    public string Peers { get; set; } = "";

    /// <summary>True when <c>[NetworkCompatibility]</c> passes a <c>NetworkVersionRule</c> (its second argument).</summary>
    public bool RuleDeclared { get; set; }

    /// <summary>The declared rule; meaningful when <see cref="RuleDeclared"/>.</summary>
    public NetworkVersionRule Rule { get; set; }

    /// <summary>The peer range the join evaluation uses: <c>Peers</c>, else the declared rule (Exact, None), else the same minor version.</summary>
    public string EffectivePeers => CompatRanges.EffectiveText(Version, Peers, RuleDeclared, Rule) ?? "";

    public List<DeclaredDependency> Dependencies { get; } = new();

    /// <summary><c>client</c>, <c>server</c> or <c>both</c>, as <c>progenitor.json</c> spells the side; <c>both</c> when undeclared.</summary>
    public string SideText => SideRaw switch
    {
        null => "both",
        1 => "server",
        2 => "client",
        3 => "both",
        _ => "unknown",
    };
}

/// <summary>
/// What a plugin image says about itself, read from metadata only: no assembly is loaded and nothing runs.
/// The member-reference scan reports references, not calls that run: dead code counts, and helper DLLs a mod
/// ships are scanned only when they are passed in too.
/// </summary>
internal sealed class ImageFacts
{
    private const string BepInPluginAttribute = "BepInEx.BepInPlugin";
    private const string BepInDependencyAttribute = "BepInEx.BepInDependency";
    private const string ProgenitorSideAttribute = "Progenitor.Plugins.ProgenitorSideAttribute";
    private const string NetworkCompatibilityAttribute = "Progenitor.Plugins.NetworkCompatibilityAttribute";

    /// <summary>The Progenitor Core assembly's simple name (<c>Progenitor.dll</c>).</summary>
    public const string ProgenitorAssemblyName = "Progenitor";

    public bool IsManagedAssembly { get; private set; }
    public string AssemblyName { get; private set; } = "";
    public List<PluginEntry> Plugins { get; } = new();

    /// <summary>The <c>Progenitor</c> assembly version this image was compiled against; null when it does not reference Core.</summary>
    public Version? ProgenitorReference { get; private set; }

    /// <summary>Direct key and device reads, as <c>Namespace.Type::member</c> (the load-time check's list plus <c>Gamepad.current</c>).</summary>
    public IReadOnlyList<string> DirectKeyReads { get; private set; } = Array.Empty<string>();

    /// <summary>References <c>BepInEx.Configuration.ConfigFile::Bind</c> itself instead of <c>ctx.Bind</c> / <c>ctx.Config.Bind</c>.</summary>
    public bool BindsConfigFileDirectly { get; private set; }

    /// <summary>Constructs its own <c>ConfigFile</c>.</summary>
    public bool CreatesConfigFile { get; private set; }

    /// <summary>Binds settings through Progenitor (<c>PluginContext.Bind</c> or <c>PluginConfig.Bind</c>).</summary>
    public bool UsesProgenitorBind { get; private set; }

    /// <summary>Touches <c>ctx.Settings</c>, <c>ctx.Menu</c> or <c>ctx.Keybinds</c>, which register the cfg as a Mods-menu settings source.</summary>
    public bool RegistersSettingsSource { get; private set; }

    /// <summary>Reads <c>ctx.UnsafeHarmony</c>, which opts the plugin out of hot swapping.</summary>
    public bool UsesUnsafeHarmony { get; private set; }

    /// <summary>Registers its own IL2CPP types, which makes it process-lifetime.</summary>
    public bool InjectsIl2CppTypes { get; private set; }

    public static ImageFacts Read(byte[] image)
    {
        var facts = new ImageFacts();
        if (image == null || image.Length < 128) return facts;
        try
        {
            using var pe = new PEReader(ImmutableArray.Create(image));
            if (!pe.HasMetadata) return facts;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return facts;
            facts.IsManagedAssembly = true;
            facts.AssemblyName = md.GetString(md.GetAssemblyDefinition().Name);
            facts.ReadReferences(md);
            facts.ReadPlugins(md);
        }
        catch (BadImageFormatException) { facts.IsManagedAssembly = false; }
        catch (InvalidOperationException) { facts.IsManagedAssembly = false; }

        return facts;
    }

    private void ReadReferences(MetadataReader md)
    {
        foreach (var h in md.AssemblyReferences)
        {
            var ar = md.GetAssemblyReference(h);
            if (string.Equals(md.GetString(ar.Name), ProgenitorAssemblyName, StringComparison.Ordinal))
                ProgenitorReference = ar.Version;
        }

        var keys = new SortedSet<string>(DirectKeyReadCheck.Find(md), StringComparer.Ordinal);
        foreach (var handle in md.MemberReferences)
        {
            var member = md.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = md.GetTypeReference((TypeReferenceHandle)member.Parent);
            string ns = md.GetString(type.Namespace);
            string name = md.GetString(type.Name);
            string m = md.GetString(member.Name);

            // The repository rule names Gamepad.current too; the load-time check leaves it out.
            if (ns == "UnityEngine.InputSystem" && name == "Gamepad" && m == "get_current")
                keys.Add(ns + "." + name + "::" + m);

            if (ns == "BepInEx.Configuration" && name == "ConfigFile")
            {
                if (m == "Bind") BindsConfigFileDirectly = true;
                else if (m == ".ctor") CreatesConfigFile = true;
            }
            else if (ns == "Progenitor.Plugins" && (name == "PluginContext" || name == "PluginConfig") && m == "Bind")
            {
                UsesProgenitorBind = true;
            }

            if (ns == "Progenitor.Plugins" && name == "PluginContext")
            {
                if (m == "get_Settings" || m == "get_Menu" || m == "get_Keybinds") RegistersSettingsSource = true;
                if (m == "get_UnsafeHarmony") UsesUnsafeHarmony = true;
            }

            if ((ns == "Il2CppInterop.Runtime.Injection" && name == "ClassInjector" && m.StartsWith("RegisterTypeInIl2Cpp", StringComparison.Ordinal))
                || (ns == "BepInEx.Unity.IL2CPP" && name == "BasePlugin" && m == "AddComponent")
                || (ns == "BepInEx.Unity.IL2CPP" && name == "IL2CPPChainloader" && m == "AddUnityComponent"))
                InjectsIl2CppTypes = true;
        }

        DirectKeyReads = new List<string>(keys);
    }

    private void ReadPlugins(MetadataReader md)
    {
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            PluginEntry? entry = null;
            foreach (var ah in td.GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(ah);
                if (AttributeName(md, ca) != BepInPluginAttribute) continue;
                if (!TryThreeStrings(md, ca, out var guid, out var name, out var version)) continue;
                entry = new PluginEntry { TypeName = FullName(md, td), Guid = guid, Name = name, Version = version };
            }

            if (entry == null) continue;

            // The runtime reads [ProgenitorSide] and [NetworkCompatibility] up the base chain; a base in this image counts.
            var current = td;
            bool sideDone = false, compatDone = false;
            for (int depth = 0; depth < 32; depth++)
            {
                foreach (var ah in current.GetCustomAttributes())
                {
                    var ca = md.GetCustomAttribute(ah);
                    string attr = AttributeName(md, ca);
                    if (!sideDone && attr == ProgenitorSideAttribute && TryInt32(md, ca, out int side))
                    {
                        entry.SideRaw = side;
                        sideDone = true;
                    }
                    else if (!compatDone && attr == NetworkCompatibilityAttribute && TryPeers(md, ca, out var peers, out bool ruleDeclared, out byte rule))
                    {
                        entry.CompatDeclared = true;
                        entry.RuleDeclared = ruleDeclared;
                        entry.Rule = (NetworkVersionRule)rule;
                        entry.Peers = peers.Trim();
                        compatDone = true;
                    }
                    else if (depth == 0 && attr == BepInDependencyAttribute && TryDependency(md, ca, out var dep))
                    {
                        entry.Dependencies.Add(dep);
                    }
                }

                var next = LocalBase(md, current.BaseType);
                if (next == null) break;
                current = md.GetTypeDefinition(next.Value);
            }

            Plugins.Add(entry);
        }
    }

    private static TypeDefinitionHandle? LocalBase(MetadataReader md, EntityHandle handle)
    {
        if (handle.IsNil) return null;
        if (handle.Kind == HandleKind.TypeDefinition) return (TypeDefinitionHandle)handle;
        if (handle.Kind != HandleKind.TypeSpecification) return null;
        var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)handle).Signature);
        if (blob.RemainingBytes < 2 || blob.ReadByte() != 0x15) return null; // GENERICINST
        byte kind = blob.ReadByte();
        if (kind != 0x12 && kind != 0x11) return null;
        var generic = blob.ReadTypeHandle();
        return generic.Kind == HandleKind.TypeDefinition ? (TypeDefinitionHandle)generic : null;
    }

    private static string AttributeName(MetadataReader md, CustomAttribute ca)
    {
        EntityHandle parent;
        if (ca.Constructor.Kind == HandleKind.MemberReference)
            parent = md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent;
        else if (ca.Constructor.Kind == HandleKind.MethodDefinition)
            parent = md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType();
        else return "";

        if (parent.Kind == HandleKind.TypeReference)
        {
            var tr = md.GetTypeReference((TypeReferenceHandle)parent);
            string ns = md.GetString(tr.Namespace);
            string name = md.GetString(tr.Name);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        if (parent.Kind == HandleKind.TypeDefinition)
            return FullName(md, md.GetTypeDefinition((TypeDefinitionHandle)parent));
        return "";
    }

    private static string FullName(MetadataReader md, TypeDefinition td)
    {
        string name = md.GetString(td.Name);
        if (td.IsNested) return FullName(md, md.GetTypeDefinition(td.GetDeclaringType())) + "/" + name;
        string ns = td.Namespace.IsNil ? "" : md.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    private static bool TryThreeStrings(MetadataReader md, CustomAttribute ca, out string a, out string b, out string c)
    {
        a = b = c = "";
        try
        {
            var blob = md.GetBlobReader(ca.Value);
            if (blob.ReadUInt16() != 0x0001) return false;
            a = blob.ReadSerializedString() ?? "";
            b = blob.ReadSerializedString() ?? "";
            c = blob.ReadSerializedString() ?? "";
            return true;
        }
        catch (BadImageFormatException) { return false; }
    }

    private static bool TryInt32(MetadataReader md, CustomAttribute ca, out int value)
    {
        value = 0;
        try
        {
            var blob = md.GetBlobReader(ca.Value);
            if (blob.ReadUInt16() != 0x0001 || blob.RemainingBytes < 4) return false;
            value = blob.ReadInt32();
            return true;
        }
        catch (BadImageFormatException) { return false; }
    }

    /// <summary><c>[NetworkCompatibility(level[, rule], Peers = "...")]</c>: one byte per enum argument, then named string members.</summary>
    private static bool TryPeers(MetadataReader md, CustomAttribute ca, out string peers, out bool ruleDeclared, out byte rule)
    {
        peers = "";
        ruleDeclared = false;
        rule = 0;
        try
        {
            var types = ParameterTypes(md, ca);
            if (types.Count < 1 || types.Count > 2) return false;
            var blob = md.GetBlobReader(ca.Value);
            if (blob.ReadUInt16() != 0x0001 || blob.RemainingBytes < types.Count) return false;
            blob.ReadByte(); // level
            if (types.Count == 2)
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
                if ((kind != 0x54 && kind != 0x53) || type != 0x0E) break;
                string? member = blob.ReadSerializedString();
                string? value = blob.ReadSerializedString();
                if (member == "Peers") peers = value ?? "";
            }

            return true;
        }
        catch (BadImageFormatException) { return false; }
    }

    /// <summary>
    /// <c>[BepInDependency(guid)]</c>, <c>(guid, DependencyFlags)</c> (1 hard, 2 soft) or <c>(guid, minimumVersion)</c>.
    /// </summary>
    private static bool TryDependency(MetadataReader md, CustomAttribute ca, out DeclaredDependency dep)
    {
        dep = new DeclaredDependency("", false, "");
        try
        {
            var types = ParameterTypes(md, ca);
            if (types.Count < 1) return false;
            var blob = md.GetBlobReader(ca.Value);
            if (blob.ReadUInt16() != 0x0001) return false;
            string guid = blob.ReadSerializedString() ?? "";
            bool soft = false;
            string min = "";
            if (types.Count >= 2)
            {
                if (types[1] == 0x0E) min = blob.ReadSerializedString() ?? "";
                else if (blob.RemainingBytes >= 4) soft = (blob.ReadInt32() & 2) != 0;
            }

            dep = new DeclaredDependency(guid, soft, min);
            return guid.Length > 0;
        }
        catch (BadImageFormatException) { return false; }
    }

    /// <summary>The constructor's parameter element types (0x0E string; 0x11 / 0x12 a value or class type).</summary>
    private static List<byte> ParameterTypes(MetadataReader md, CustomAttribute ca)
    {
        var list = new List<byte>();
        BlobHandle sig;
        if (ca.Constructor.Kind == HandleKind.MemberReference)
            sig = md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Signature;
        else if (ca.Constructor.Kind == HandleKind.MethodDefinition)
            sig = md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).Signature;
        else return list;

        var reader = md.GetBlobReader(sig);
        reader.ReadSignatureHeader();
        int count = reader.ReadCompressedInteger();
        reader.ReadByte(); // return type: void
        for (int i = 0; i < count; i++)
        {
            byte t = reader.ReadByte();
            list.Add(t);
            if (t == 0x11 || t == 0x12) reader.ReadTypeHandle();
            else if (t != 0x0E && !(t >= 0x02 && t <= 0x0D)) break; // anything richer is not an argument we read
        }

        return list;
    }
}
