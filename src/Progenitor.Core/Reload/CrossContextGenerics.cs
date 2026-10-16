using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Progenitor.Reload;

/// <summary>One generic a plugin defines whose instantiations live in another collectible context.</summary>
internal sealed class CrossContextGeneric
{
    internal CrossContextGeneric(string member, IReadOnlyList<string> instantiations, IReadOnlyList<string> holders)
    {
        Member = member;
        Instantiations = instantiations;
        Holders = holders;
    }

    /// <summary>The generic method or type the plugin defines, e.g. <c>Example.Mod.Gallery.On</c>.</summary>
    internal string Member { get; }

    /// <summary>Sorted distinct instantiations, e.g. <c>On&lt;IUiButton&gt;</c>.</summary>
    internal IReadOnlyList<string> Instantiations { get; }

    /// <summary>The assemblies whose load context the instantiations are created in (usually <c>Progenitor</c>).</summary>
    internal IReadOnlyList<string> Holders { get; }
}

/// <summary>Where an assembly's types live, from the point of view of one plugin.</summary>
internal enum ContextHome
{
    /// <summary>A context that never unloads (the runtime, the game interop, BepInEx, Bootstrap). Never chosen as a home.</summary>
    Fixed,
    /// <summary>The plugin's own collectible context (its own assembly and any dependency loaded into it).</summary>
    Self,
    /// <summary>Another collectible context, older than the plugin: Progenitor Core.</summary>
    Framework,
    /// <summary>An open generic parameter; the real home is decided when the code runs.</summary>
    Unknown,
}

/// <summary>
/// The cross-context generic scan (grep <c>runtime-generic</c>, docs/hot-reload.md "Generic helpers").
///
/// <para><b>What the runtime does.</b> When a generic is defined in a collectible assembly, CoreCLR
/// creates each instantiation in the context of the type argument that lives in a <i>different</i>
/// collectible context, picking the newest such context, and uses the defining context only when
/// there is none. Arguments from contexts that never unload (the runtime library, game interop) are
/// never candidates, and arguments from the defining context are skipped. The context that receives
/// the instantiation then keeps a permanent reference to the defining context. So a generic a plugin
/// defines and instantiates with a Core type is created in Core's context and Core keeps the plugin
/// alive until that Core generation unloads. JIT-compiling a caller is enough.</para>
///
/// <para><b>The rule, exactly.</b> Homes are computed bottom-up. A plain type's home is its
/// assembly's (<see cref="ContextHome"/>); an array, pointer or by-ref takes its element's. A
/// constructed generic <c>G&lt;A1..An&gt;</c> takes the newest home among its arguments that are
/// collectible and differ from <c>G</c>'s own home (the plugin is newer than Core), else <c>G</c>'s.
/// An instantiation of a generic <b>the plugin defines</b> pins the plugin when its home is
/// <see cref="ContextHome.Framework"/>; that is the case exactly when some argument (at any depth
/// that survives the rule) lives in Core. Consequences, all confirmed on .NET 6.0.35:</para>
/// <list type="bullet">
/// <item><c>On&lt;IUiButton&gt;</c>, <c>Pair&lt;string, IUiButton&gt;</c>, <c>Pair&lt;Mine, IUiButton&gt;</c>,
/// <c>One&lt;List&lt;IUiButton&gt;&gt;</c>, <c>One&lt;IUiButton[]&gt;</c> and <c>Box&lt;IUiButton&gt;</c> pin.
/// A plugin type next to the Core type does not help: it is in the defining context, so it is skipped.</item>
/// <item><c>Pair&lt;string, int&gt;</c>, <c>One&lt;Mine&gt;</c>, <c>One&lt;Dictionary&lt;IUiButton, Mine&gt;&gt;</c>
/// (that dictionary lives in the plugin, the newer context) and <c>One&lt;CoreBox&lt;Mine&gt;&gt;</c> do not.</item>
/// <item>A generic Core defines, instantiated with plugin types (<c>ctx.Bind&lt;T&gt;</c>, <c>IUiTabs.Add&lt;T&gt;</c>),
/// lives in the plugin and does not pin it.</item>
/// </list>
///
/// <para>Metadata only and game-free: one pass over the TypeSpec, MethodSpec and signature blobs with
/// <see cref="MetadataReader"/>, so the test suite, the reload admission and the pin diagnosis run
/// the same code. It reports references, not calls that run: dead code counts, because compiling
/// its caller is enough. An instantiation over an open generic parameter is reported only when a
/// Core argument already decides it.</para>
/// </summary>
internal static class CrossContextGenerics
{
    /// <summary>Progenitor Core's simple name; plugins bind it from Core's own collectible context.</summary>
    internal const string CoreAssemblyName = "Progenitor";

    /// <summary>The admission warning for one offending member.</summary>
    internal static string WarningLine(string member) =>
        member + " is a generic defined in this plugin and instantiated only with Progenitor types; the runtime "
        + "will keep this plugin loaded until Progenitor Core unloads, so hot reloads of it leak and block Core "
        + "swaps. Make the helper non-generic.";

    /// <summary>The pin diagnosis line for one offending member of a retired plugin that is still alive.</summary>
    internal static string DiagnosisLine(string member) =>
        "pin-diag plugin-holder runtime-generic " + member + " -> released when this Core generation unloads";

    /// <summary>
    /// The default classification: the plugin's own assembly and <paramref name="ownContext"/> are
    /// <see cref="ContextHome.Self"/>, Core is <see cref="ContextHome.Framework"/>, everything else
    /// is <see cref="ContextHome.Fixed"/>. That matches the reloader, which binds Core from Core's
    /// context and resolves nothing else from a collectible one.
    /// </summary>
    internal static Func<string, ContextHome> DefaultClassifier(string selfName, ICollection<string>? ownContext = null) => name =>
    {
        if (string.Equals(name, selfName, StringComparison.OrdinalIgnoreCase)) return ContextHome.Self;
        if (ownContext != null && ownContext.Contains(name)) return ContextHome.Self;
        if (string.Equals(name, CoreAssemblyName, StringComparison.Ordinal)) return ContextHome.Framework;
        return ContextHome.Fixed;
    };

    /// <summary>Scan a PE image. Never throws; an unreadable image has no findings and sets <paramref name="error"/>.</summary>
    internal static IReadOnlyList<CrossContextGeneric> Scan(byte[]? image, Func<string, ContextHome>? classify, out string error)
    {
        error = "";
        if (image == null || image.Length == 0) return Array.Empty<CrossContextGeneric>();
        try
        {
            using var pe = new PEReader(new MemoryStream(image, writable: false));
            if (!pe.HasMetadata) return Array.Empty<CrossContextGeneric>();
            return Scan(pe.GetMetadataReader(), classify);
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return Array.Empty<CrossContextGeneric>();
        }
    }

    /// <summary>Scan a file on disk. Never throws.</summary>
    internal static IReadOnlyList<CrossContextGeneric> ScanFile(string path, Func<string, ContextHome>? classify, out string error)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return Array.Empty<CrossContextGeneric>();
        }
        return Scan(bytes, classify, out error);
    }

    /// <summary>
    /// Scan one assembly's metadata. <paramref name="classify"/> maps an assembly simple name to its
    /// home; null uses <see cref="DefaultClassifier"/> for this assembly's own name.
    /// </summary>
    internal static IReadOnlyList<CrossContextGeneric> Scan(MetadataReader md, Func<string, ContextHome>? classify)
    {
        if (md == null) throw new ArgumentNullException(nameof(md));
        string self = md.IsAssembly ? md.GetString(md.GetAssemblyDefinition().Name) : "";
        var provider = new Provider(md, classify ?? DefaultClassifier(self));

        int typeSpecs = md.GetTableRowCount(TableIndex.TypeSpec);
        for (int row = 1; row <= typeSpecs; row++)
            provider.TypeSpec(MetadataTokens.TypeSpecificationHandle(row));

        int methodSpecs = md.GetTableRowCount(TableIndex.MethodSpec);
        for (int row = 1; row <= methodSpecs; row++)
        {
            try { provider.MethodSpec(md.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(row))); }
            catch (BadImageFormatException) { /* a malformed row has no instantiation to report */ }
        }

        // Inline generic instantiations in signatures (fields, locals, parameters, member references)
        // are created when the type loader or the JIT meets them, without a TypeSpec row.
        foreach (var h in md.FieldDefinitions)
            Try(() => md.GetFieldDefinition(h).DecodeSignature(provider, null));
        foreach (var h in md.MethodDefinitions)
            Try(() => md.GetMethodDefinition(h).DecodeSignature(provider, null));
        foreach (var h in md.PropertyDefinitions)
            Try(() => md.GetPropertyDefinition(h).DecodeSignature(provider, null));
        foreach (var h in md.MemberReferences)
        {
            var mr = md.GetMemberReference(h);
            Try(() =>
            {
                if (mr.GetKind() == MemberReferenceKind.Method) mr.DecodeMethodSignature(provider, null);
                else mr.DecodeFieldSignature(provider, null);
            });
        }
        int sigs = md.GetTableRowCount(TableIndex.StandAloneSig);
        for (int row = 1; row <= sigs; row++)
        {
            var sig = md.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(row));
            Try(() =>
            {
                if (sig.GetKind() == StandaloneSignatureKind.LocalVariables) sig.DecodeLocalSignature(provider, null);
                else sig.DecodeMethodSignature(provider, null);
            });
        }

        return provider.Results();
    }

    private static void Try(Action read)
    {
        try { read(); }
        catch (BadImageFormatException) { /* skip a malformed blob */ }
        catch (InvalidOperationException) { /* skip a blob of an unexpected kind */ }
    }

    // ------------------------------------------------------------------ the type model

    private sealed class Node
    {
        internal static readonly ImmutableArray<Node> None = ImmutableArray<Node>.Empty;

        internal Node(string text, ContextHome home, string holder, string? selfMember = null, Node? definition = null, ImmutableArray<Node> args = default)
        {
            Text = text;
            Home = home;
            Holder = holder;
            SelfMember = selfMember;
            Definition = definition;
            Args = args.IsDefault ? None : args;
        }

        internal string Text { get; }
        internal ContextHome Home { get; }
        /// <summary>The assembly that owns <see cref="Home"/> when it is <see cref="ContextHome.Framework"/>.</summary>
        internal string Holder { get; }
        /// <summary>Full name, for a type definition in the plugin itself.</summary>
        internal string? SelfMember { get; }
        internal Node? Definition { get; }
        internal ImmutableArray<Node> Args { get; }
    }

    /// <summary>
    /// The home of a constructed generic whose definition lives in <paramref name="defHome"/>
    /// (see the class remarks). The plugin is the newest context, so it wins over Core.
    /// </summary>
    internal static ContextHome Instantiate(ContextHome defHome, IReadOnlyList<ContextHome> args, out int holderIndex)
    {
        holderIndex = -1;
        bool open = false;
        int self = -1, framework = -1;
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == ContextHome.Unknown) { open = true; continue; }
            if (a == ContextHome.Fixed || a == defHome) continue;
            if (a == ContextHome.Self && self < 0) self = i;
            if (a == ContextHome.Framework && framework < 0) framework = i;
        }
        if (self >= 0) { holderIndex = self; return ContextHome.Self; }
        if (defHome == ContextHome.Self)
        {
            // Open arguments cannot rescue it: an argument in the plugin's own context is skipped,
            // and one in a context that never unloads is never a candidate.
            if (framework >= 0) { holderIndex = framework; return ContextHome.Framework; }
            return ContextHome.Self;
        }
        // An open argument might turn out to be a plugin type, which would win.
        if (open) return ContextHome.Unknown;
        if (framework >= 0) { holderIndex = framework; return ContextHome.Framework; }
        return defHome;
    }

    private sealed class Provider : ISignatureTypeProvider<Node, object?>
    {
        private readonly MetadataReader _md;
        private readonly Func<string, ContextHome> _classify;
        private readonly Dictionary<TypeSpecificationHandle, Node> _specs = new();
        private readonly Dictionary<TypeDefinitionHandle, Node> _defs = new();
        private readonly Dictionary<TypeReferenceHandle, Node> _refs = new();
        private readonly Dictionary<string, ContextHome> _homes = new(StringComparer.OrdinalIgnoreCase);
        private readonly SortedDictionary<string, (SortedSet<string> inst, SortedSet<string> holders)> _hits = new(StringComparer.Ordinal);
        private static readonly Node Open = new("?", ContextHome.Unknown, "");

        internal Provider(MetadataReader md, Func<string, ContextHome> classify)
        {
            _md = md;
            _classify = classify;
        }

        internal IReadOnlyList<CrossContextGeneric> Results()
        {
            var list = new List<CrossContextGeneric>(_hits.Count);
            foreach (var kv in _hits)
                list.Add(new CrossContextGeneric(kv.Key, new List<string>(kv.Value.inst), new List<string>(kv.Value.holders)));
            return list;
        }

        private void Hit(string member, string instantiation, string holder)
        {
            if (!_hits.TryGetValue(member, out var entry))
            {
                entry = (new SortedSet<string>(StringComparer.Ordinal), new SortedSet<string>(StringComparer.Ordinal));
                _hits[member] = entry;
            }
            entry.inst.Add(instantiation);
            if (holder.Length > 0) entry.holders.Add(holder);
        }

        private ContextHome HomeOf(string assembly)
        {
            if (_homes.TryGetValue(assembly, out var home)) return home;
            try { home = _classify(assembly); }
            catch { home = ContextHome.Fixed; }
            _homes[assembly] = home;
            return home;
        }

        internal void TypeSpec(TypeSpecificationHandle handle) => GetTypeFromSpecification(_md, null, handle, 0);

        internal void MethodSpec(MethodSpecification spec)
        {
            var methodArgs = spec.DecodeSignature(this, null);
            string? member;
            string name;
            ImmutableArray<Node> classArgs = Node.None;
            if (spec.Method.Kind == HandleKind.MethodDefinition)
            {
                var def = _md.GetMethodDefinition((MethodDefinitionHandle)spec.Method);
                name = _md.GetString(def.Name);
                member = TypeDef(def.GetDeclaringType()).SelfMember + "." + name;
                // A generic method of a generic type referenced by its definition token: the type's
                // own parameters are open here.
                int typeParams = _md.GetTypeDefinition(def.GetDeclaringType()).GetGenericParameters().Count;
                if (typeParams > 0)
                {
                    var b = ImmutableArray.CreateBuilder<Node>(typeParams);
                    for (int i = 0; i < typeParams; i++) b.Add(Open);
                    classArgs = b.MoveToImmutable();
                }
            }
            else if (spec.Method.Kind == HandleKind.MemberReference)
            {
                var mr = _md.GetMemberReference((MemberReferenceHandle)spec.Method);
                name = _md.GetString(mr.Name);
                Node? parent = mr.Parent.Kind switch
                {
                    HandleKind.TypeDefinition => TypeDef((TypeDefinitionHandle)mr.Parent),
                    HandleKind.TypeSpecification => GetTypeFromSpecification(_md, null, (TypeSpecificationHandle)mr.Parent, 0),
                    _ => null,
                };
                var definition = parent?.Definition ?? parent;
                if (definition?.SelfMember == null) return; // a generic another assembly defines
                member = definition.SelfMember + "." + name;
                if (parent!.Definition != null) classArgs = parent.Args;
            }
            else return;

            var homes = new List<ContextHome>(classArgs.Length + methodArgs.Length);
            var all = new List<Node>(classArgs.Length + methodArgs.Length);
            foreach (var a in classArgs) { homes.Add(a.Home); all.Add(a); }
            foreach (var a in methodArgs) { homes.Add(a.Home); all.Add(a); }
            if (Instantiate(ContextHome.Self, homes, out int holder) == ContextHome.Framework)
                Hit(member, name + "<" + Join(methodArgs) + ">", all[holder].Holder);
        }

        private Node TypeDef(TypeDefinitionHandle handle)
        {
            if (_defs.TryGetValue(handle, out var known)) return known;
            var def = _md.GetTypeDefinition(handle);
            string full = FullName(def);
            var node = new Node(Short(_md.GetString(def.Name)), ContextHome.Self, "", full);
            _defs[handle] = node;
            return node;
        }

        private string FullName(TypeDefinition def)
        {
            string name = Short(_md.GetString(def.Name));
            var outer = def.GetDeclaringType();
            if (!outer.IsNil) return FullName(_md.GetTypeDefinition(outer)) + "." + name;
            string ns = _md.GetString(def.Namespace);
            return ns.Length > 0 ? ns + "." + name : name;
        }

        public Node GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeDef(handle);

        public Node GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            if (_refs.TryGetValue(handle, out var known)) return known;
            var tr = reader.GetTypeReference(handle);
            var scope = tr.ResolutionScope;
            while (scope.Kind == HandleKind.TypeReference) scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            string assembly = "";
            ContextHome home = ContextHome.Self; // module-scoped or nil scope: this module
            if (scope.Kind == HandleKind.AssemblyReference)
            {
                assembly = reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
                home = HomeOf(assembly);
            }
            var node = new Node(Short(reader.GetString(tr.Name)), home, home == ContextHome.Framework ? assembly : "");
            _refs[handle] = node;
            return node;
        }

        public Node GetTypeFromSpecification(MetadataReader reader, object? context, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            if (_specs.TryGetValue(handle, out var known)) return known;
            _specs[handle] = Open; // a recursive spec (it cannot be in valid metadata) reads as open
            Node node;
            try { node = reader.GetTypeSpecification(handle).DecodeSignature(this, context); }
            catch (BadImageFormatException) { node = Open; }
            _specs[handle] = node;
            return node;
        }

        public Node GetGenericInstantiation(Node genericType, ImmutableArray<Node> typeArguments)
        {
            var homes = new ContextHome[typeArguments.Length];
            for (int i = 0; i < homes.Length; i++) homes[i] = typeArguments[i].Home;
            var home = Instantiate(genericType.Home, homes, out int holderIndex);
            string holder = home == ContextHome.Framework
                ? (holderIndex >= 0 ? typeArguments[holderIndex].Holder : genericType.Holder)
                : "";
            string text = genericType.Text + "<" + Join(typeArguments) + ">";
            if (genericType.SelfMember != null && home == ContextHome.Framework)
                Hit(genericType.SelfMember, text, holder);
            return new Node(text, home, holder, definition: genericType, args: typeArguments);
        }

        public Node GetSZArrayType(Node elementType) => Wrap(elementType, "[]");
        public Node GetArrayType(Node elementType, ArrayShape shape) => Wrap(elementType, "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]");
        public Node GetByReferenceType(Node elementType) => Wrap(elementType, "&");
        public Node GetPointerType(Node elementType) => Wrap(elementType, "*");
        public Node GetPinnedType(Node elementType) => elementType;
        public Node GetModifiedType(Node modifier, Node unmodifiedType, bool isRequired) => unmodifiedType;
        public Node GetFunctionPointerType(MethodSignature<Node> signature) => new("fnptr", ContextHome.Fixed, "");
        public Node GetGenericMethodParameter(object? genericContext, int index) => Open;
        public Node GetGenericTypeParameter(object? genericContext, int index) => Open;
        public Node GetPrimitiveType(PrimitiveTypeCode typeCode) => new(Primitive(typeCode), ContextHome.Fixed, "");

        private static Node Wrap(Node element, string suffix) => new(element.Text + suffix, element.Home, element.Holder);
    }

    private static string Join(ImmutableArray<Node> nodes)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < nodes.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(nodes[i].Text);
        }
        return sb.ToString();
    }

    /// <summary><c>List`1</c> reads <c>List</c>; the arguments follow in angle brackets.</summary>
    private static string Short(string name)
    {
        int tick = name.IndexOf('`');
        return tick > 0 ? name.Substring(0, tick) : name;
    }

    private static string Primitive(PrimitiveTypeCode code) => code switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.Void => "void",
        _ => code.ToString(),
    };

    /// <summary>
    /// Scan a loaded assembly's own metadata (no file read, no type resolved) when it sits in a
    /// collectible context, classifying every assembly in that context as the plugin's own. Null
    /// for an assembly that never unloads or whose metadata is not readable. Never throws.
    /// </summary>
    internal static IReadOnlyList<CrossContextGeneric>? ScanLoaded(Assembly? assembly, out string error)
    {
        error = "";
        try
        {
            if (assembly == null) return null;
            var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly);
            if (context == null || !context.IsCollectible) return null;
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var a in context.Assemblies)
            {
                try { if (a.GetName().Name is { Length: > 0 } n) own.Add(n); }
                catch { /* an assembly without a readable name is not a candidate */ }
            }
            string self = assembly.GetName().Name ?? "";
            return ScanRaw(assembly, DefaultClassifier(self, own), out error);
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return null;
        }
    }

#if PROGENITOR_TESTS
    // The test suite compiles without unsafe code and loads its images from files.
    private static IReadOnlyList<CrossContextGeneric>? ScanRaw(Assembly assembly, Func<string, ContextHome> classify, out string error)
    {
        error = "";
        return assembly.Location.Length == 0 ? null : ScanFile(assembly.Location, classify, out error);
    }
#else
    // The loaded image's own metadata: a stream-loaded plugin has no file location.
    private static unsafe IReadOnlyList<CrossContextGeneric>? ScanRaw(Assembly assembly, Func<string, ContextHome> classify, out string error)
    {
        error = "";
        if (!assembly.TryGetRawMetadata(out byte* blob, out int length)) return null;
        return Scan(new MetadataReader(blob, length), classify);
    }
#endif

    /// <summary>
    /// The admission check for one loaded plugin assembly: scan it, remember the result for the pin
    /// diagnosis and pass one <see cref="WarningLine"/> per offending member to <paramref name="warn"/>.
    /// Returns the findings (empty for an assembly that never unloads). Never throws.
    /// </summary>
    internal static IReadOnlyList<CrossContextGeneric> Admit(Assembly? assembly, Action<string>? warn, Action<string>? debug = null)
    {
        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var hits = ScanLoaded(assembly, out string error);
            string name = "";
            try { name = assembly?.GetName().Name ?? ""; }
            catch { name = ""; }
            if (error.Length > 0) debug?.Invoke(name + ": the cross-context generic scan could not run: " + error);
            if (hits == null) return Array.Empty<CrossContextGeneric>();
            Remember(name, hits);
            foreach (var hit in hits)
                warn?.Invoke(WarningLine(hit.Member) + " Instantiations: " + string.Join(", ", hit.Instantiations) + ".");
            debug?.Invoke(name + ": cross-context generic scan found "
                + hits.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " in "
                + clock.Elapsed.TotalMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " ms");
            return hits;
        }
        catch
        {
            return Array.Empty<CrossContextGeneric>();
        }
    }

    // ------------------------------------------------------------------ admission cache

    private static readonly object Gate = new();
    private static readonly Dictionary<string, IReadOnlyList<CrossContextGeneric>> ByAssembly = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Remember the admission scan of <paramref name="assemblyName"/> for the pin diagnosis. Strings only: nothing here holds the plugin.</summary>
    internal static void Remember(string assemblyName, IReadOnlyList<CrossContextGeneric> findings)
    {
        if (string.IsNullOrEmpty(assemblyName)) return;
        lock (Gate) ByAssembly[assemblyName] = findings ?? Array.Empty<CrossContextGeneric>();
    }

    /// <summary>The last admission scan of <paramref name="assemblyName"/>, or null when it was never scanned.</summary>
    internal static IReadOnlyList<CrossContextGeneric>? Recall(string? assemblyName)
    {
        if (string.IsNullOrEmpty(assemblyName)) return null;
        lock (Gate) return ByAssembly.TryGetValue(assemblyName!, out var found) ? found : null;
    }

    /// <summary>Forget every remembered scan (tests).</summary>
    internal static void ClearRemembered()
    {
        lock (Gate) ByAssembly.Clear();
    }

    // ------------------------------------------------------------------ runtime cross-references

    /// <summary>
    /// The managed <c>LoaderAllocator</c> that keeps a collectible type alive
    /// (<c>RuntimeType.m_keepalive</c>), or null for a type that never unloads or when the field is
    /// not there. Reflection over runtime internals; never throws.
    /// </summary>
    internal static object? LoaderAllocatorOf(Type? type)
    {
        try
        {
            if (type == null) return null;
            var field = type.GetType().GetField("m_keepalive", BindingFlags.Instance | BindingFlags.NonPublic);
            var value = field?.GetValue(type);
            return value != null && value.GetType().Name == "LoaderAllocator" ? value : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The index in <paramref name="holder"/>'s handle table (<c>LoaderAllocator.m_slots</c>) that
    /// holds <paramref name="target"/>, or -1: none, or the field is not readable. This is where the
    /// runtime records "this context keeps that one alive". Never throws.
    /// </summary>
    internal static int FindReference(object? holder, object? target)
    {
        try
        {
            if (holder == null || target == null) return -1;
            var t = holder.GetType();
            if (t.GetField("m_slots", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(holder) is not object[] slots) return -1;
            int used = slots.Length;
            if (t.GetField("m_slotsUsed", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(holder) is int u && u >= 0 && u < used) used = u;
            for (int i = 0; i < used; i++)
                if (ReferenceEquals(slots[i], target)) return i;
            return -1;
        }
        catch
        {
            return -1;
        }
    }
}
