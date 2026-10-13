using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Progenitor.Install.Admission;

/// <summary>One plugin entry point: a type carrying <c>[BepInPlugin]</c> directly.</summary>
internal sealed record PluginEntryPoint(string TypeName, string Guid, string Name, string Version);

/// <summary>One structural finding: which image, which site, which code. A finding is a fact; the evaluator decides.</summary>
internal sealed record PreflightFinding(AdmissionReasonCode Code, string Image, string Site)
{
    public override string ToString() => Code + " in " + Image + " at " + Site;
}

/// <summary>
/// One generic instantiation whose definition lives in a game/interop assembly and whose
/// arguments include a game value type: <c>NativeArray&lt;ProjectM.Health&gt;</c>,
/// <c>EntityManager.GetComponentData&lt;ProjectM.Health&gt;</c>. A structural fact — whether the
/// native instantiation exists on a build is a separate, qualified question.
/// </summary>
internal sealed record InteropInstantiation(string Image, string Definition, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// What <see cref="ArtifactPreflight"/> read from the bytes. Plain data; no Assembly, Type or
/// delegate. <see cref="Valid"/> false carries the refusal code and nothing else is meaningful.
/// </summary>
internal sealed record PreflightOutcome(
    bool Valid,
    AdmissionReasonCode? Refusal,
    string? RefusalDetail,
    string AssemblyName,
    string AssemblyVersion,
    string Mvid,
    IReadOnlyList<PluginEntryPoint> EntryPoints,
    IReadOnlyList<string> BaseChain,
    bool BaseChainResolved,
    bool BaseIsProgenitorPlugin,
    PackageSide DeclaredSide,
    bool SideDeclared,
    IReadOnlyList<string> AssemblyReferences,
    IReadOnlyList<string> UnresolvedReferences,
    IReadOnlyList<PreflightFinding> Findings,
    IReadOnlyList<InteropInstantiation> InteropInstantiations,
    int ManagedGenericInstantiations,
    IReadOnlyList<string> ScannedImages,
    IReadOnlyList<string>? BepInProcesses = null,
    IReadOnlyList<string>? BepInDependencies = null)
{
    /// <summary>INSTORD1: the process names the main image's <c>[BepInProcess]</c> attributes name, in declaration order. Empty means the plugin never said.</summary>
    public IReadOnlyList<string> Processes => BepInProcesses ?? Array.Empty<string>();

    /// <summary>INSTORD1: the plugin GUIDs the main image's <c>[BepInDependency]</c> attributes name. Plugin GUIDs, not package names.</summary>
    public IReadOnlyList<string> DependencyGuids => BepInDependencies ?? Array.Empty<string>();

    public static PreflightOutcome Refused(AdmissionReasonCode code, string? detail) => new(
        false, code, detail, "", "", "",
        Array.Empty<PluginEntryPoint>(), Array.Empty<string>(), false, false,
        PackageSide.Unknown, false,
        Array.Empty<string>(), Array.Empty<string>(),
        Array.Empty<PreflightFinding>(), Array.Empty<InteropInstantiation>(), 0, Array.Empty<string>());

    public bool HasFinding(AdmissionReasonCode code)
    {
        for (int i = 0; i < Findings.Count; i++)
            if (Findings[i].Code == code) return true;
        return false;
    }
}

/// <summary>Knobs for one scan: which assembly names count as framework/game, and the expected identity.</summary>
internal sealed class PreflightOptions
{
    public static PreflightOptions Default { get; } = new();

    /// <summary>Simple-name prefixes resolved by identity, never by bytes: the runtime, BepInEx, Progenitor, interop and the game.</summary>
    public IReadOnlyList<string> KnownFrameworkPrefixes { get; init; } = new[]
    {
        "System", "netstandard", "mscorlib", "Microsoft.",
        "BepInEx", "Progenitor", "0Harmony", "MonoMod", "Il2CppInterop",
        "Il2Cpp", "UnityEngine", "Unity", "ProjectM", "Stunlock",
    };

    /// <summary>Prefixes whose value types are "game value types" and whose generics are interop definitions.</summary>
    public IReadOnlyList<string> GameAssemblyPrefixes { get; init; } = new[]
    {
        "ProjectM", "Unity", "Stunlock", "Il2Cpp",
    };

    /// <summary>Base types that end the chain: the BepInEx IL2CPP plugin base and the Progenitor plugin base.</summary>
    public IReadOnlyList<string> TerminalBases { get; init; } = new[]
    {
        "BepInEx.Unity.IL2CPP.BasePlugin",
        "BepInEx.BasePlugin",
        "Progenitor.Plugins.ProgenitorPlugin`1",
    };

    public const string BepInPluginAttribute = "BepInEx.BepInPlugin";
    public const string BepInPluginAssembly = "BepInEx.Core";

    /// <summary>INSTORD1: <c>[BepInProcess("VRising.exe")]</c> — which process an ordinary plugin loads in.</summary>
    public const string BepInProcessAttribute = "BepInEx.BepInProcess";

    /// <summary>INSTORD1: <c>[BepInDependency("guid")]</c> — a plugin GUID, never a Thunderstore package name.</summary>
    public const string BepInDependencyAttribute = "BepInEx.BepInDependency";
    public const string ProgenitorSideAttribute = "Progenitor.Plugins.ProgenitorSideAttribute";
    public const string ProgenitorPluginBase = "Progenitor.Plugins.ProgenitorPlugin`1";
    public const string UnsafeHarmonyGetter = "get_UnsafeHarmony";
    public const string ClassInjectorType = "Il2CppInterop.Runtime.Injection.ClassInjector";

    /// <summary>Hard cap on images (main plus bundled) one scan will open.</summary>
    public int MaxImages { get; init; } = 32;
}

/// <summary>
/// Pure, non-executing scanner over bounded immutable bytes. Reads a candidate plugin image and
/// every bundled dependency with <see cref="PEReader"/> / <see cref="MetadataReader"/>: never
/// <c>Assembly.Load</c>, never a custom-attribute instance, never a constructor, never a module
/// initializer. Custom-attribute blobs are decoded by hand (a prolog and serialized strings),
/// method bodies are walked as opcode bytes, and generic signatures are decoded into name trees.
/// Resolution of a base chain or a reference goes only through known framework identities and
/// the explicit bundled closure; anything else is reported unresolved, never assumed.
/// </summary>
internal static class ArtifactPreflight
{
    public static PreflightOutcome Scan(
        ReadOnlyMemory<byte> image,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? bundled = null,
        PreflightOptions? options = null)
    {
        options ??= PreflightOptions.Default;
        bundled ??= new Dictionary<string, ReadOnlyMemory<byte>>();
        if (bundled.Count + 1 > options.MaxImages)
            return PreflightOutcome.Refused(AdmissionReasonCode.MalformedImage, "more bundled images than the scan cap");

        var images = new List<OpenImage>();
        try
        {
            if (!OpenImage.TryOpen("main", image, out var main, out var code, out var detail))
                return PreflightOutcome.Refused(code, detail);
            images.Add(main);

            var bundledByName = new Dictionary<string, OpenImage>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in bundled)
            {
                if (!OpenImage.TryOpen(kv.Key, kv.Value, out var dep, out code, out detail))
                    return PreflightOutcome.Refused(code, "bundled " + kv.Key + ": " + detail);
                images.Add(dep);
                bundledByName[dep.AssemblyName] = dep;
                bundledByName[kv.Key] = dep;
            }

            return ScanOpened(main, bundledByName, images, options);
        }
        finally
        {
            for (int i = 0; i < images.Count; i++) images[i].Dispose();
        }
    }

    private static PreflightOutcome ScanOpened(
        OpenImage main,
        Dictionary<string, OpenImage> bundled,
        List<OpenImage> all,
        PreflightOptions options)
    {
        var md = main.Reader;
        var asm = md.GetAssemblyDefinition();
        string asmName = md.GetString(asm.Name);
        string asmVersion = asm.Version.ToString();
        string mvid = md.GetGuid(md.GetModuleDefinition().Mvid).ToString("D");

        // Entry points and side: directly declared attributes on TypeDefs of the main image only.
        var entryPoints = new List<PluginEntryPoint>();
        var processes = new List<string>();
        var dependencyGuids = new List<string>();
        TypeDefinitionHandle entryHandle = default;
        PackageSide side = PackageSide.Unknown;
        bool sideDeclared = false;
        foreach (var th in md.TypeDefinitions)
        {
            var td = md.GetTypeDefinition(th);
            foreach (var ah in td.GetCustomAttributes())
            {
                var ca = md.GetCustomAttribute(ah);
                if (!TryAttributeType(md, ca, out var attrName, out var attrAsm)) continue;
                if (attrName == PreflightOptions.BepInPluginAttribute
                    && string.Equals(attrAsm, PreflightOptions.BepInPluginAssembly, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryDecodeThreeStrings(md, ca, out var guid, out var name, out var version))
                    {
                        if (entryPoints.Count == 0) entryHandle = th;
                        entryPoints.Add(new PluginEntryPoint(FullName(md, td), guid, name, version));
                    }
                }
                else if (attrName == PreflightOptions.BepInProcessAttribute)
                {
                    // INSTORD1: one serialized string, the executable name. Read, never resolved
                    // against a running process here: the planner compares it to the target.
                    if (TryDecodeFirstString(md, ca, out var process))
                        AddDistinct(processes, process);
                }
                else if (attrName == PreflightOptions.BepInDependencyAttribute)
                {
                    // INSTORD1: the first argument is the dependency's plugin GUID. The optional
                    // second argument (a version string or a flags int) is not read: a version
                    // range is not something this route resolves.
                    if (TryDecodeFirstString(md, ca, out var depGuid))
                        AddDistinct(dependencyGuids, depGuid);
                }
                else if (attrName == PreflightOptions.ProgenitorSideAttribute)
                {
                    if (TryDecodeInt32(md, ca, out int raw))
                    {
                        sideDeclared = true;
                        side = raw switch
                        {
                            1 => PackageSide.Server,
                            2 => PackageSide.Client,
                            3 => PackageSide.Both,
                            _ => PackageSide.Unknown,
                        };
                    }
                }
            }
        }

        // Base chain of the first entry point, resolved only through terminals and bundled bytes.
        var chain = new List<string>();
        bool chainResolved = false;
        bool baseIsProgenitor = false;
        if (entryPoints.Count > 0)
            chainResolved = WalkBaseChain(main, entryHandle, bundled, options, chain, out baseIsProgenitor);

        // References and their resolution.
        var refs = new List<string>();
        var unresolved = new List<string>();
        foreach (var img in all)
        {
            foreach (var arh in img.Reader.AssemblyReferences)
            {
                string name = img.Reader.GetString(img.Reader.GetAssemblyReference(arh).Name);
                if (img == main) refs.Add(name);
                if (IsKnown(name, options.KnownFrameworkPrefixes)) continue;
                if (bundled.ContainsKey(name)) continue;
                if (string.Equals(name, main.AssemblyName, StringComparison.OrdinalIgnoreCase)) continue;
                string row = img.Label + " -> " + name;
                if (!unresolved.Contains(row)) unresolved.Add(row);
            }
        }

        // Findings and generic instantiations across every image.
        var findings = new List<PreflightFinding>();
        var interop = new List<InteropInstantiation>();
        int managedGenerics = 0;
        var scanned = new List<string>();
        foreach (var img in all)
        {
            scanned.Add(img.Label + "=" + img.AssemblyName);
            ScanReferences(img, options, findings);
            ScanMethods(img, findings);
            ScanGenerics(img, options, interop, ref managedGenerics);
        }

        return new PreflightOutcome(
            true, null, null,
            asmName, asmVersion, mvid,
            entryPoints, chain, chainResolved, baseIsProgenitor,
            side, sideDeclared,
            refs, unresolved,
            findings, interop, managedGenerics, scanned,
            processes, dependencyGuids);
    }

    // ---- base chain -------------------------------------------------------------------------

    private static bool WalkBaseChain(
        OpenImage start,
        TypeDefinitionHandle startType,
        Dictionary<string, OpenImage> bundled,
        PreflightOptions options,
        List<string> chain,
        out bool baseIsProgenitor)
    {
        baseIsProgenitor = false;
        var img = start;
        var handle = startType;
        for (int depth = 0; depth < 32; depth++)
        {
            var td = img.Reader.GetTypeDefinition(handle);
            var baseHandle = td.BaseType;
            if (baseHandle.IsNil) return chain.Count > 0 && IsTerminal(chain[chain.Count - 1], options);

            string baseName;
            string? baseAsm;
            if (baseHandle.Kind == HandleKind.TypeDefinition)
            {
                var bh = (TypeDefinitionHandle)baseHandle;
                baseName = FullName(img.Reader, img.Reader.GetTypeDefinition(bh));
                chain.Add(baseName);
                if (IsTerminal(baseName, options)) { baseIsProgenitor |= baseName == PreflightOptions.ProgenitorPluginBase; return true; }
                handle = bh;
                continue;
            }
            if (baseHandle.Kind == HandleKind.TypeReference)
            {
                if (!TryTypeRef(img.Reader, (TypeReferenceHandle)baseHandle, out baseName, out baseAsm)) return false;
            }
            else if (baseHandle.Kind == HandleKind.TypeSpecification)
            {
                var node = img.Reader.GetTypeSpecification((TypeSpecificationHandle)baseHandle)
                    .DecodeSignature(new NodeProvider(img.Reader), null);
                var def = node.GenericDefinition ?? node;
                baseName = def.Name;
                baseAsm = def.Assembly;
            }
            else
            {
                return false;
            }

            chain.Add(baseName);
            if (IsTerminal(baseName, options))
            {
                baseIsProgenitor |= baseName == PreflightOptions.ProgenitorPluginBase;
                return true;
            }
            if (baseAsm == null) return false;
            if (bundled.TryGetValue(baseAsm, out var dep) && dep.TryFindType(baseName, out var found))
            {
                img = dep;
                handle = found;
                continue;
            }
            // Known framework identity that is not a terminal (e.g. a BepInEx helper base), or an
            // assembly outside the closure: neither is walked from bytes. Unresolved, not success.
            return false;
        }
        return false;
    }

    private static bool IsTerminal(string name, PreflightOptions options)
    {
        for (int i = 0; i < options.TerminalBases.Count; i++)
            if (string.Equals(name, options.TerminalBases[i], StringComparison.Ordinal)) return true;
        return false;
    }

    // ---- reference findings -----------------------------------------------------------------

    private static void ScanReferences(OpenImage img, PreflightOptions options, List<PreflightFinding> findings)
    {
        var md = img.Reader;
        foreach (var trh in md.TypeReferences)
        {
            if (!TryTypeRef(md, trh, out var full, out var asm)) continue;
            string ns = NamespaceOf(full);
            if (ns == "HarmonyLib" || string.Equals(asm, "0Harmony", StringComparison.OrdinalIgnoreCase))
                Add(findings, AdmissionReasonCode.HarmonyReference, img.Label, full);
            else if (full == PreflightOptions.ClassInjectorType)
                Add(findings, AdmissionReasonCode.ClassInjectorReference, img.Label, full);
            else if (full == "System.Activator" || ns == "System.Reflection.Emit" || full == "System.Runtime.Loader.AssemblyLoadContext")
                Add(findings, AdmissionReasonCode.ReflectionEscape, img.Label, full);
        }

        foreach (var mrh in md.MemberReferences)
        {
            var mr = md.GetMemberReference(mrh);
            string name = md.GetString(mr.Name);
            string parent = ParentName(md, mr.Parent);
            if (name == PreflightOptions.UnsafeHarmonyGetter)
                Add(findings, AdmissionReasonCode.UnsafeHarmonyReference, img.Label, parent + "::" + name);
            else if (parent == "System.Reflection.Assembly" && name.StartsWith("Load", StringComparison.Ordinal))
                Add(findings, AdmissionReasonCode.ReflectionEscape, img.Label, parent + "::" + name);
            else if (parent == "System.Type" && (name == "GetType" || name == "MakeGenericType"))
                Add(findings, AdmissionReasonCode.ReflectionEscape, img.Label, parent + "::" + name);
            else if (name == "MakeGenericMethod")
                Add(findings, AdmissionReasonCode.ReflectionEscape, img.Label, parent + "::" + name);
        }

        int moduleRefs = md.GetTableRowCount(TableIndex.ModuleRef);
        for (int row = 1; row <= moduleRefs; row++)
        {
            var mref = md.GetModuleReference(MetadataTokens.ModuleReferenceHandle(row));
            Add(findings, AdmissionReasonCode.NativeImport, img.Label, "ModuleRef " + md.GetString(mref.Name));
        }
    }

    private static string ParentName(MetadataReader md, EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                return TryTypeRef(md, (TypeReferenceHandle)parent, out var n, out _) ? n : "?";
            case HandleKind.TypeDefinition:
                return FullName(md, md.GetTypeDefinition((TypeDefinitionHandle)parent));
            case HandleKind.TypeSpecification:
                var node = md.GetTypeSpecification((TypeSpecificationHandle)parent).DecodeSignature(new NodeProvider(md), null);
                return (node.GenericDefinition ?? node).Name;
            default:
                return parent.Kind.ToString();
        }
    }

    // ---- method bodies ----------------------------------------------------------------------

    private static void ScanMethods(OpenImage img, List<PreflightFinding> findings)
    {
        var md = img.Reader;
        foreach (var mh in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(mh);
            string site = FullName(md, md.GetTypeDefinition(m.GetDeclaringType())) + "::" + md.GetString(m.Name);
            if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0)
            {
                Add(findings, AdmissionReasonCode.NativeImport, img.Label, "DllImport " + site);
                continue;
            }
            if (m.RelativeVirtualAddress == 0) continue;
            if ((m.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL) continue;

            byte[] il;
            try { il = img.Pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes() ?? Array.Empty<byte>(); }
            catch (BadImageFormatException) { Add(findings, AdmissionReasonCode.MalformedImage, img.Label, "body " + site); continue; }

            if (BodyHasCalli(il))
                Add(findings, AdmissionReasonCode.NativeImport, img.Label, "calli " + site);
        }
    }

    // The opcode table is read from System.Reflection.Emit.OpCodes as static data only: this
    // scanner never emits, defines or invokes anything. It is the one authoritative operand-size
    // table in the runtime, which is why a hand-typed copy is not kept here.
    private static readonly Dictionary<short, OperandType> Opcodes = BuildOpcodes();

    private static Dictionary<short, OperandType> BuildOpcodes()
    {
        var table = new Dictionary<short, OperandType>();
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (f.GetValue(null) is OpCode op) table[op.Value] = op.OperandType;
        }
        return table;
    }

    /// <summary>Walks one IL body opcode by opcode; true when a <c>calli</c> (0x29) is a real instruction, not operand bytes.</summary>
    internal static bool BodyHasCalli(byte[] il)
    {
        int i = 0;
        while (i < il.Length)
        {
            short value;
            if (il[i] == 0xFE)
            {
                if (i + 1 >= il.Length) return false;
                value = (short)(0xFE00 | il[i + 1]);
                i += 2;
            }
            else
            {
                value = il[i];
                i += 1;
            }
            if (value == OpCodes.Calli.Value) return true;
            if (!Opcodes.TryGetValue(value, out var operand)) return false; // undecodable: stop, never guess.
            switch (operand)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: i += 1; break;
                case OperandType.InlineVar: i += 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: i += 8; break;
                case OperandType.InlineSwitch:
                    if (i + 4 > il.Length) return false;
                    int n = BitConverter.ToInt32(il, i);
                    i += 4 + 4 * Math.Max(0, n);
                    break;
                default: i += 4; break;
            }
        }
        return false;
    }

    // ---- generic instantiations -------------------------------------------------------------

    private static void ScanGenerics(OpenImage img, PreflightOptions options, List<InteropInstantiation> interop, ref int managed)
    {
        var md = img.Reader;
        var provider = new NodeProvider(md);
        int specCount = md.GetTableRowCount(TableIndex.TypeSpec);
        for (int row = 1; row <= specCount; row++)
        {
            var h = MetadataTokens.TypeSpecificationHandle(row);
            TypeNode node;
            try { node = md.GetTypeSpecification(h).DecodeSignature(provider, null); }
            catch (BadImageFormatException) { continue; }
            Classify(img, options, node, interop, ref managed);
        }

        int msCount = md.GetTableRowCount(TableIndex.MethodSpec);
        for (int row = 1; row <= msCount; row++)
        {
            var h = MetadataTokens.MethodSpecificationHandle(row);
            var ms = md.GetMethodSpecification(h);
            ImmutableArray<TypeNode> args;
            try { args = ms.DecodeSignature(provider, null); }
            catch (BadImageFormatException) { continue; }
            string methodName;
            string? declAsm;
            string declName;
            if (ms.Method.Kind == HandleKind.MemberReference)
            {
                var mr = md.GetMemberReference((MemberReferenceHandle)ms.Method);
                methodName = md.GetString(mr.Name);
                (declName, declAsm) = ParentIdentity(md, mr.Parent);
            }
            else if (ms.Method.Kind == HandleKind.MethodDefinition)
            {
                var def = md.GetMethodDefinition((MethodDefinitionHandle)ms.Method);
                methodName = md.GetString(def.Name);
                declName = FullName(md, md.GetTypeDefinition(def.GetDeclaringType()));
                declAsm = img.AssemblyName;
            }
            else continue;

            bool gameDef = IsKnown(declAsm, options.GameAssemblyPrefixes);
            bool gameArg = false;
            for (int i = 0; i < args.Length; i++) gameArg |= HasGameValueType(args[i], options);
            string text = declName + "::" + methodName + "<" + Join(args) + ">";
            if (gameDef && gameArg) interop.Add(new InteropInstantiation(img.Label, declName + "::" + methodName, text));
            else managed++;
        }
    }

    private static void Classify(OpenImage img, PreflightOptions options, TypeNode node, List<InteropInstantiation> interop, ref int managed)
    {
        if (node.Args.Length == 0)
        {
            // Arrays / pointers / by-refs over a generic instance: look inside.
            if (node.Element != null) Classify(img, options, node.Element, interop, ref managed);
            return;
        }
        var def = node.GenericDefinition ?? node;
        bool gameDef = IsKnown(def.Assembly, options.GameAssemblyPrefixes);
        bool gameArg = false;
        for (int i = 0; i < node.Args.Length; i++) gameArg |= HasGameValueType(node.Args[i], options);
        if (gameDef && gameArg) interop.Add(new InteropInstantiation(img.Label, def.Name, node.ToString()));
        else managed++;
        for (int i = 0; i < node.Args.Length; i++)
            if (node.Args[i].Args.Length > 0) Classify(img, options, node.Args[i], interop, ref managed);
    }

    private static bool HasGameValueType(TypeNode node, PreflightOptions options)
    {
        if (node.IsValueType && IsKnown(node.Assembly, options.GameAssemblyPrefixes)) return true;
        for (int i = 0; i < node.Args.Length; i++)
            if (HasGameValueType(node.Args[i], options)) return true;
        return node.Element != null && HasGameValueType(node.Element, options);
    }

    private static (string Name, string? Assembly) ParentIdentity(MetadataReader md, EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                return TryTypeRef(md, (TypeReferenceHandle)parent, out var n, out var a) ? (n, a) : ("?", null);
            case HandleKind.TypeDefinition:
                return (FullName(md, md.GetTypeDefinition((TypeDefinitionHandle)parent)), md.GetString(md.GetAssemblyDefinition().Name));
            case HandleKind.TypeSpecification:
                var node = md.GetTypeSpecification((TypeSpecificationHandle)parent).DecodeSignature(new NodeProvider(md), null);
                var def = node.GenericDefinition ?? node;
                return (def.Name, def.Assembly);
            default:
                return (parent.Kind.ToString(), null);
        }
    }

    // ---- attribute blobs (decoded by hand; never instantiated) ------------------------------

    internal static bool TryAttributeType(MetadataReader md, CustomAttribute ca, out string fullName, out string? assembly)
    {
        fullName = "";
        assembly = null;
        EntityHandle parent;
        if (ca.Constructor.Kind == HandleKind.MemberReference)
            parent = md.GetMemberReference((MemberReferenceHandle)ca.Constructor).Parent;
        else if (ca.Constructor.Kind == HandleKind.MethodDefinition)
            parent = md.GetMethodDefinition((MethodDefinitionHandle)ca.Constructor).GetDeclaringType();
        else return false;

        if (parent.Kind == HandleKind.TypeReference)
            return TryTypeRef(md, (TypeReferenceHandle)parent, out fullName, out assembly);
        if (parent.Kind == HandleKind.TypeDefinition)
        {
            fullName = FullName(md, md.GetTypeDefinition((TypeDefinitionHandle)parent));
            assembly = md.GetString(md.GetAssemblyDefinition().Name);
            return true;
        }
        return false;
    }

    internal static bool TryDecodeThreeStrings(MetadataReader md, CustomAttribute ca, out string a, out string b, out string c)
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

    /// <summary>INSTORD1: the first serialized string argument of an attribute blob (prolog 0x0001).</summary>
    private static bool TryDecodeFirstString(MetadataReader md, CustomAttribute ca, out string value)
    {
        value = "";
        try
        {
            var blob = md.GetBlobReader(ca.Value);
            if (blob.ReadUInt16() != 0x0001) return false;
            value = blob.ReadSerializedString() ?? "";
            return true;
        }
        catch (BadImageFormatException) { return false; }
    }

    private static void AddDistinct(List<string> into, string? value)
    {
        string v = (value ?? "").Trim();
        if (v.Length == 0) return;
        for (int i = 0; i < into.Count; i++)
            if (string.Equals(into[i], v, StringComparison.OrdinalIgnoreCase)) return;
        into.Add(v);
    }

    private static bool TryDecodeInt32(MetadataReader md, CustomAttribute ca, out int value)
    {
        value = 0;
        try
        {
            var blob = md.GetBlobReader(ca.Value);
            if (blob.ReadUInt16() != 0x0001) return false;
            if (blob.RemainingBytes < 4) return false;
            value = blob.ReadInt32();
            return true;
        }
        catch (BadImageFormatException) { return false; }
    }

    // ---- naming helpers ---------------------------------------------------------------------

    internal static string FullName(MetadataReader md, TypeDefinition td)
    {
        string name = md.GetString(td.Name);
        if (td.IsNested)
        {
            var outer = md.GetTypeDefinition(td.GetDeclaringType());
            return FullName(md, outer) + "/" + name;
        }
        string ns = td.Namespace.IsNil ? "" : md.GetString(td.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    internal static bool TryTypeRef(MetadataReader md, TypeReferenceHandle h, out string fullName, out string? assembly)
    {
        var tr = md.GetTypeReference(h);
        string name = md.GetString(tr.Name);
        string ns = tr.Namespace.IsNil ? "" : md.GetString(tr.Namespace);
        fullName = ns.Length == 0 ? name : ns + "." + name;
        assembly = null;
        var scope = tr.ResolutionScope;
        if (scope.IsNil) return true;
        switch (scope.Kind)
        {
            case HandleKind.AssemblyReference:
                assembly = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
                return true;
            case HandleKind.TypeReference:
                if (!TryTypeRef(md, (TypeReferenceHandle)scope, out var outer, out assembly)) return false;
                fullName = outer + "/" + name;
                return true;
            case HandleKind.ModuleDefinition:
                assembly = md.GetString(md.GetAssemblyDefinition().Name);
                return true;
            default:
                return true;
        }
    }

    private static string NamespaceOf(string full)
    {
        int slash = full.IndexOf('/');
        if (slash >= 0) full = full.Substring(0, slash);
        int dot = full.LastIndexOf('.');
        return dot < 0 ? "" : full.Substring(0, dot);
    }

    internal static bool IsKnown(string? name, IReadOnlyList<string> prefixes)
    {
        if (string.IsNullOrEmpty(name)) return false;
        for (int i = 0; i < prefixes.Count; i++)
        {
            string p = prefixes[i];
            if (p.EndsWith(".", StringComparison.Ordinal))
            {
                if (name!.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(name, p, StringComparison.OrdinalIgnoreCase)
                || name!.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string Join(ImmutableArray<TypeNode> nodes)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < nodes.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(nodes[i].ToString());
        }
        return sb.ToString();
    }

    private static void Add(List<PreflightFinding> findings, AdmissionReasonCode code, string image, string site)
    {
        for (int i = 0; i < findings.Count; i++)
        {
            if (findings[i].Code == code && findings[i].Image == image && findings[i].Site == site) return;
        }
        findings.Add(new PreflightFinding(code, image, site));
    }

    // ---- one opened image -------------------------------------------------------------------

    private sealed class OpenImage : IDisposable
    {
        public PEReader Pe { get; }
        public MetadataReader Reader { get; }
        public string Label { get; }
        public string AssemblyName { get; }

        private OpenImage(PEReader pe, MetadataReader reader, string label)
        {
            Pe = pe;
            Reader = reader;
            Label = label;
            AssemblyName = reader.IsAssembly ? reader.GetString(reader.GetAssemblyDefinition().Name) : label;
        }

        public static bool TryOpen(string label, ReadOnlyMemory<byte> bytes, out OpenImage image, out AdmissionReasonCode code, out string? detail)
        {
            image = null!;
            code = AdmissionReasonCode.MalformedImage;
            detail = null;
            if (bytes.Length < 128) { detail = "fewer than 128 bytes"; return false; }
            PEReader? pe = null;
            try
            {
                pe = new PEReader(ImmutableArray.Create(bytes.ToArray()));
                if (!pe.HasMetadata)
                {
                    code = AdmissionReasonCode.NotManagedImage;
                    detail = "no CLI metadata";
                    pe.Dispose();
                    return false;
                }
                var md = pe.GetMetadataReader();
                if (!md.IsAssembly) { detail = "module without an assembly manifest"; pe.Dispose(); return false; }
                image = new OpenImage(pe, md, label);
                return true;
            }
            catch (BadImageFormatException e)
            {
                detail = e.Message;
                pe?.Dispose();
                return false;
            }
            catch (InvalidOperationException e)
            {
                detail = e.Message;
                pe?.Dispose();
                return false;
            }
        }

        public bool TryFindType(string fullName, out TypeDefinitionHandle handle)
        {
            foreach (var th in Reader.TypeDefinitions)
            {
                if (string.Equals(FullName(Reader, Reader.GetTypeDefinition(th)), fullName, StringComparison.Ordinal))
                {
                    handle = th;
                    return true;
                }
            }
            handle = default;
            return false;
        }

        public void Dispose() => Pe.Dispose();
    }

    // ---- signature decoding into name trees -------------------------------------------------

    internal sealed class TypeNode
    {
        public TypeNode(string name, string? assembly, bool isValueType, ImmutableArray<TypeNode> args, TypeNode? element = null, TypeNode? genericDefinition = null)
        {
            Name = name;
            Assembly = assembly;
            IsValueType = isValueType;
            Args = args;
            Element = element;
            GenericDefinition = genericDefinition;
        }

        public string Name { get; }
        public string? Assembly { get; }
        public bool IsValueType { get; }
        public ImmutableArray<TypeNode> Args { get; }
        public TypeNode? Element { get; }
        public TypeNode? GenericDefinition { get; }

        public override string ToString()
        {
            if (Args.Length == 0) return Element != null ? Name.Replace("{0}", Element.ToString()) : Name;
            return (GenericDefinition ?? this).Name + "<" + Join(Args) + ">";
        }
    }

    private sealed class NodeProvider : ISignatureTypeProvider<TypeNode, object?>
    {
        private readonly MetadataReader _md;
        public NodeProvider(MetadataReader md) => _md = md;

        private static TypeNode Prim(string name, bool vt) => new(name, "System.Private.CoreLib", vt, ImmutableArray<TypeNode>.Empty);

        public TypeNode GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Object => Prim("System.Object", false),
            PrimitiveTypeCode.String => Prim("System.String", false),
            PrimitiveTypeCode.Void => Prim("System.Void", false),
            PrimitiveTypeCode.TypedReference => Prim("System.TypedReference", true),
            _ => Prim("System." + typeCode, true),
        };

        public TypeNode GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            var td = reader.GetTypeDefinition(handle);
            return new TypeNode(FullName(reader, td), reader.GetString(reader.GetAssemblyDefinition().Name), rawTypeKind == 0x11, ImmutableArray<TypeNode>.Empty);
        }

        public TypeNode GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TryTypeRef(reader, handle, out var name, out var asm);
            return new TypeNode(name, asm, rawTypeKind == 0x11, ImmutableArray<TypeNode>.Empty);
        }

        public TypeNode GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, context);

        public TypeNode GetSZArrayType(TypeNode elementType) => new("{0}[]", elementType.Assembly, false, ImmutableArray<TypeNode>.Empty, elementType);
        public TypeNode GetArrayType(TypeNode elementType, ArrayShape shape) => new("{0}[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]", elementType.Assembly, false, ImmutableArray<TypeNode>.Empty, elementType);
        public TypeNode GetByReferenceType(TypeNode elementType) => new("{0}&", elementType.Assembly, false, ImmutableArray<TypeNode>.Empty, elementType);
        public TypeNode GetPointerType(TypeNode elementType) => new("{0}*", elementType.Assembly, false, ImmutableArray<TypeNode>.Empty, elementType);
        public TypeNode GetPinnedType(TypeNode elementType) => elementType;
        public TypeNode GetModifiedType(TypeNode modifier, TypeNode unmodifiedType, bool isRequired) => unmodifiedType;
        public TypeNode GetFunctionPointerType(MethodSignature<TypeNode> signature) => new("fnptr", null, false, ImmutableArray<TypeNode>.Empty);
        public TypeNode GetGenericInstantiation(TypeNode genericType, ImmutableArray<TypeNode> typeArguments) =>
            new(genericType.Name, genericType.Assembly, genericType.IsValueType, typeArguments, null, genericType);
        public TypeNode GetGenericMethodParameter(object? genericContext, int index) => new("!!" + index, null, false, ImmutableArray<TypeNode>.Empty);
        public TypeNode GetGenericTypeParameter(object? genericContext, int index) => new("!" + index, null, false, ImmutableArray<TypeNode>.Empty);
    }
}
